using System.Diagnostics;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Platform;
using SimpleRGB.Core.Plugins;
using SimpleRGB.Core.Settings;

namespace SimpleRGB.Core.Devices;

public enum NoticeKind { Info, Warning, NeedsAdmin, Conflict }

public sealed record Notice(NoticeKind Kind, string Text)
{
    /// <summary>For conflicts: the program the user can close from the notice (e.g. "TRCC.exe").</summary>
    public string? App { get; init; }
    /// <summary>The program also starts with Windows (offer to stop that too).</summary>
    public bool AppStartsWithWindows { get; init; }
}

public sealed record UnsupportedDevice(string Name, int VendorId, int ProductId);

/// <summary>Finds devices, runs them, and pushes lighting from settings to them.</summary>
public sealed class DeviceManager : IDisposable
{
    private static readonly string[] KnownRgbApps =
    {
        "SignalRgb", "OpenRGB", "ArmouryCrate", "ArmouryCrate.UserSessionHelper", "LightingService",
        "iCUE", "RGBFusion", "GCC", "LEDKeeper2", "MysticLight", "NZXT CAM", "lghub",
    };

    private readonly SettingsStore _settings;
    private readonly PluginLibrary _library;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _sync = new();
    private List<RgbDevice> _devices = new();
    private List<SmBus> _buses = new();
    private List<Notice> _notices = new();
    private List<UnsupportedDevice> _unsupported = new();
    private HashSet<string> _lastHidKeys = new();
    private volatile bool _disposed;

    private readonly Audio.AudioService? _audio;
    private readonly Media.NowPlayingService? _nowPlaying;
    private readonly IEffectInputs _inputs;
    /// <summary>Live music/album inputs, for UI previews.</summary>
    public IEffectInputs Inputs => _inputs;
    private volatile bool _uiWantsAudio;

    public DeviceManager(SettingsStore settings, PluginLibrary library,
        Audio.AudioService? audio = null, Media.NowPlayingService? nowPlaying = null)
    {
        _settings = settings;
        _library = library;
        _audio = audio;
        _nowPlaying = nowPlaying;
        _inputs = new LiveInputs(audio, nowPlaying);
        if (_audio != null) _audio.Source = settings.Read(s => s.AudioSource);
    }

    private sealed class LiveInputs(Audio.AudioService? audio, Media.NowPlayingService? nowPlaying) : IEffectInputs
    {
        public Audio.AudioFrame Audio => audio?.Current ?? Core.Audio.AudioFrame.Silent;
        public IReadOnlyList<RgbColor> Palette => nowPlaying?.Current.Palette ?? Array.Empty<RgbColor>();
    }

    public Audio.AudioService? Audio => _audio;
    public Media.NowPlayingService? NowPlaying => _nowPlaying;

    public void SetAudioSource(Audio.AudioSource source)
    {
        _settings.Update(s => s.AudioSource = source);
        if (_audio != null) _audio.Source = source;
    }

    /// <summary>The UI is showing music meters, so keep listening even if no device uses Music yet.</summary>
    public bool UiWantsAudio
    {
        get => _uiWantsAudio;
        set { _uiWantsAudio = value; UpdateAudioDemand(); }
    }

    /// <summary>Listens only while something needs it (lightweight when music mode isn't used).</summary>
    private void UpdateAudioDemand()
    {
        if (_audio == null) return;
        var needed = _uiWantsAudio || _settings.Read(s =>
            !s.LightsOff && ((s.SyncAll && s.SyncLighting.Mode == LightingMode.Music) ||
                             s.Devices.Values.Any(d => (!s.SyncAll || d.ExcludeFromSync) && d.Zones.Values.Any(z => z.Mode == LightingMode.Music))));
        _audio.SetActive(needed);
        if (needed) _ = _nowPlaying?.StartAsync();
    }

    public PluginLibrary Library => _library;
    public SettingsStore Settings => _settings;

    public IReadOnlyList<RgbDevice> Devices { get { lock (_sync) return _devices.ToList(); } }
    public IReadOnlyList<Notice> Notices { get { lock (_sync) return _notices.ToList(); } }
    public IReadOnlyList<UnsupportedDevice> UnsupportedDevices { get { lock (_sync) return _unsupported.ToList(); } }

    public bool IsScanning { get; private set; }
    public string ScanStatus { get; private set; } = "";

    /// <summary>Device list, notices or scan status changed. Raised on a background thread.</summary>
    public event Action? DevicesChanged;
    /// <summary>One device changed (layout/state). Raised on that device's thread.</summary>
    public event Action<RgbDevice>? DeviceChanged;

    // ---------------------------------------------------------------- scanning

    /// <summary>Stops everything and detects all devices again.</summary>
    public async Task ScanAsync(bool importPlugins = true)
    {
        if (_disposed) return;
        await _scanLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => ScanCore(importPlugins)).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Rescans only if the set of connected USB devices actually changed (hot-plug).</summary>
    public async Task RescanIfUsbChangedAsync()
    {
        if (PreviewMode) return;
        var keys = await Task.Run(() => HidEnumerator.EnumerateDevices().Select(d => d.Key).ToHashSet()).ConfigureAwait(false);
        bool changed;
        lock (_sync) changed = !keys.SetEquals(_lastHidKeys);
        if (changed)
        {
            Log.Info("Devices", "USB devices changed; rescanning.");
            await ScanAsync(importPlugins: false).ConfigureAwait(false);
        }
    }

    private void SetStatus(string status, bool scanning)
    {
        ScanStatus = status;
        IsScanning = scanning;
        RaiseDevicesChanged();
    }

    private int _scanGeneration;

    private void ScanCore(bool importPlugins)
    {
        var sw = Stopwatch.StartNew();
        var generation = Interlocked.Increment(ref _scanGeneration);
        SetStatus("Stopping devices…", true);
        StopAll(turnOff: false);
        if (PreviewMode)
        {
            StartPreviewDevices();
            return;
        }

        var notices = new List<Notice>();
        var known = _settings.Read(s => s.KnownDevices.ToHashSet());

        try
        {
            // Lights first: use the plugins already on disk. Syncing with SignalRGB happens in the background after
            // the scan (only the very first run, with no plugins yet, has to wait for it).
            if (importPlugins && _library.Count == 0)
            {
                SetStatus("Getting plugins…", true);
                try { _library.ImportFromSignalRgb(); }
                catch (Exception ex) { Log.Warn("Plugins", $"Import from SignalRGB failed: {ex.Message}"); }
                importPlugins = false;
            }

            if (_library.Count == 0)
                notices.Add(new Notice(NoticeKind.Warning,
                    "No device plugins found. Open Settings → Plugins and download them from GitLab."));

            SetStatus("Looking for USB devices…", true);
            var physical = HidEnumerator.EnumerateDevices();
            lock (_sync) _lastHidKeys = physical.Select(p => p.Key).ToHashSet();

            var enumerated = sw.ElapsedMilliseconds;
            var (hidPlugins, smbusPlugins) = _library.Discover(physical.Select(p => p.VendorId).ToList());
            Log.Info("Devices", $"{physical.Count} USB HID device(s), {hidPlugins.Count} matching HID plugin(s), {smbusPlugins.Count} SMBus plugin(s) " +
                                $"(USB list {enumerated} ms, plugins {sw.ElapsedMilliseconds - enumerated} ms).");

            // USB devices light up immediately, one by one, without waiting for the slower buses.
            var unsupported = new List<UnsupportedDevice>();
            foreach (var device in physical)
            {
                var created = TryCreateHidDevice(device, hidPlugins);
                if (created != null) StartDevice(created);
                else unsupported.Add(new UnsupportedDevice(
                    string.IsNullOrWhiteSpace(device.Product) ? "Unknown USB device" : device.Product,
                    device.VendorId, device.ProductId));
            }
            lock (_sync) _unsupported = unsupported;
            RaiseDevicesChanged();

            if (_settings.Read(s => s.EnableSmBusDevices))
            {
                SetStatus("Looking for GPU and RAM lighting…", true);
                var buses = OpenSmBuses(notices);
                var ordered = OrderByKnown(smbusPlugins, known);
                // GPU and chipset buses are independent: scan them at the same time.
                Task.WaitAll(buses.Select(bus => Task.Run(() => ScanBus(bus, ordered))).ToArray());
                ScheduleGpuRetry(known, ordered, generation);
            }

            AddConflictNotices(Devices.ToList(), notices);
        }
        catch (Exception ex)
        {
            Log.Error("Devices", "Scan failed", ex);
            notices.Add(new Notice(NoticeKind.Warning, "Device scan failed: " + ex.Message));
        }

        lock (_sync) _notices = notices;
        RememberDevices(known);
        if (importPlugins)
            _ = Task.Run(() =>
            {
                try
                {
                    var changed = _library.ImportFromSignalRgb();
                    if (changed > 0) Log.Info("Plugins", $"{changed} plugin update(s) from SignalRGB will be used from the next scan.");
                }
                catch (Exception ex) { Log.Warn("Plugins", $"Background plugin update failed: {ex.Message}"); }
            });
        UpdateAudioDemand();
        var count = Devices.Count;
        Log.Info("Devices", $"Scan finished in {sw.ElapsedMilliseconds} ms: {count} device(s).");
        SetStatus(count == 0 ? "No RGB devices found" : "", false);
    }

    /// <summary>UI preview mode: virtual devices only, no hardware is touched.</summary>
    /// <summary>Preview mode: every scan only (re)creates virtual devices. Real hardware is never opened.</summary>
    public bool PreviewMode { get; set; }

    public void StartPreviewDevices()
    {
        PreviewMode = true;
        foreach (var (key, runtime, connection, text) in PreviewDevices.Create(Path.Combine(Path.GetTempPath(), "SimpleRGB.Preview")))
            StartDevice(new RgbDevice(key, runtime, connection, text));
        SetStatus("", false);
    }

    /// <summary>Adds a device to the list and starts it right away.</summary>
    private void StartDevice(RgbDevice device)
    {
        lock (_sync) _devices = _devices.Append(device).ToList();
        device.Changed += OnDeviceChanged;
        device.Start(BuildLighting(device.Key), _inputs);
        RaiseDevicesChanged();
    }

    /// <summary>Remembers which devices this PC has, so missing ones (e.g. a GPU not ready at boot) can be retried.</summary>
    private void RememberDevices(HashSet<string>? keepAlso = null)
    {
        var keys = Devices.Select(d => d.Key).ToHashSet();
        if (keepAlso != null)
            foreach (var k in keepAlso.Where(k => k.Contains(":NvidiaGpu:"))) keys.Add(k); // dropped only after retries fail
        _settings.Update(s => s.KnownDevices = keys.OrderBy(k => k).ToList());
    }

    private static List<PluginInfo> OrderByKnown(List<PluginInfo> plugins, HashSet<string> known)
    {
        var knownPlugins = known.Where(k => k.StartsWith("smbus:", StringComparison.Ordinal))
            .Select(k => k.Split(':')[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plugins.OrderBy(p => knownPlugins.Contains(p.RelativePath) ? 0 : 1).ToList();
    }

    private List<SmBus> OpenSmBuses(List<Notice> notices)
    {
        var buses = new List<SmBus>();
        buses.AddRange(NvApiI2CBus.EnumerateGpus());

        var pawn = PawnIoSmBus.TryOpen(out var reason);
        if (pawn != null)
        {
            // DDR4/DDR3 have no PMIC, and older ASUS boards keep Aura controllers at 0x4E/0x4F.
            var protectPmic = SystemInfo.MemoryGeneration is not (MemoryGeneration.Ddr4 or MemoryGeneration.Ddr3);
            buses.Add(new SmBusSafetyGuard(pawn, protectPmic));
            Log.Info("SMBus", $"Opened {pawn.Name} (PCI {pawn.Vendor:X4}:{pawn.Product:X4}); memory {SystemInfo.MemoryGeneration}; PMIC lock {(protectPmic ? "on" : "off")}.");
        }
        else if (reason != null)
        {
            Log.Info("SMBus", reason);
            var needsAdmin = PawnIoSmBus.IsDriverInstalled && !SystemInfo.IsElevated;
            notices.Add(new Notice(needsAdmin ? NoticeKind.NeedsAdmin : NoticeKind.Info,
                needsAdmin ? "RAM lighting needs administrator rights." : "RAM lighting unavailable: " + reason));
        }

        lock (_sync) _buses = _buses.Concat(buses).ToList();
        return buses;
    }

    private static string SmBusKey(PluginInfo plugin, SmBus bus, int address) =>
        $"smbus:{plugin.RelativePath}:{bus.Kind}:{bus.Vendor:X4}{bus.Product:X4}{bus.SubVendor:X4}{bus.SubDevice:X4}:{address:X2}";

    /// <summary>Runs every SMBus plugin's Scan on one bus; each device found starts immediately.</summary>
    private void ScanBus(SmBus bus, List<PluginInfo> plugins)
    {
        var claimed = new HashSet<int>();
        foreach (var plugin in plugins)
        {
            if (_disposed) return;
            List<int> addresses;
            try
            {
                using var scanner = PluginRuntime.LoadForMetadata(plugin);
                addresses = scanner.Scan(bus);
            }
            catch (Exception ex)
            {
                Log.Debug("SMBus", $"{plugin.RelativePath} scan on {bus.Name} failed: {ex.Message}");
                continue;
            }

            foreach (var address in addresses)
            {
                if (!claimed.Add(address)) continue;
                if (bus is SmBusSafetyGuard guard && guard.IsProtected((byte)address))
                {
                    Log.Warn("SMBus", $"{plugin.Name} claimed protected address 0x{address:X2}; ignored.");
                    continue;
                }

                var key = SmBusKey(plugin, bus, address);
                if (Devices.Any(d => d.Key == key)) continue;
                var saved = SavedDevice(key);
                if (saved is { Enabled: false }) continue;
                try
                {
                    var runtime = PluginRuntime.LoadForSmBus(plugin, bus, (byte)address, saved?.Parameters, saved?.ChannelLedCounts);
                    runtime.LoadParameters();
                    var connection = bus.Kind == SmBusKind.NvidiaGpu ? DeviceConnection.GpuI2C : DeviceConnection.SmBus;
                    StartDevice(new RgbDevice(key, runtime, connection, $"{bus.Name}, address 0x{address:X2}"));
                    Log.Info("Devices", $"{plugin.Name} on {bus.Name} @ 0x{address:X2}");
                }
                catch (Exception ex)
                {
                    Log.Warn("Devices", $"{plugin.RelativePath} at 0x{address:X2} failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Right after boot the NVIDIA driver (or the card's RGB chip) is sometimes not ready yet. If a GPU we
    /// found before is missing, look again a few times in the background — without touching other devices.
    /// </summary>
    private void ScheduleGpuRetry(HashSet<string> known, List<PluginInfo> plugins, int generation)
    {
        bool Missing() => known.Any(k => k.Contains(":NvidiaGpu:") && Devices.All(d => d.Key != k));
        if (!Missing()) return;
        Log.Info("Devices", "A graphics card seen before wasn't found yet; will retry shortly.");

        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 4, 8, 15, 30 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                if (_disposed || generation != _scanGeneration) return;
                if (!await _scanLock.WaitAsync(0).ConfigureAwait(false)) continue; // a full scan is running
                try
                {
                    if (generation != _scanGeneration) return;
                    // Fresh NVAPI enumeration; replace previous (empty) GPU buses.
                    var gpuBuses = NvApiI2CBus.EnumerateGpus();
                    lock (_sync) _buses = _buses.Concat(gpuBuses).ToList();
                    foreach (var bus in gpuBuses) ScanBus(bus, plugins);
                    if (!Missing())
                    {
                        Log.Info("Devices", $"Graphics card found on retry after {delay} s.");
                        RememberDevices();
                        UpdateAudioDemand();
                        return;
                    }
                }
                finally { _scanLock.Release(); }
            }
            Log.Warn("Devices", "Graphics card still not found after retries.");
            RememberDevices();
        });
    }

    private RgbDevice? TryCreateHidDevice(HidPhysicalDevice device, List<PluginInfo> plugins)
    {
        foreach (var plugin in plugins.Where(p => p.Matches(device.VendorId, device.ProductId)))
        {
            var key = $"hid:{plugin.RelativePath}:{device.VendorId:X4}:{device.ProductId:X4}:{device.InstanceId}";
            var saved = SavedDevice(key);
            if (saved is { Enabled: false }) return null;

            var backend = new HidBackend(device);
            PluginRuntime? runtime = null;
            try
            {
                runtime = PluginRuntime.LoadForHid(plugin, backend, saved?.Parameters, saved?.ChannelLedCounts);
                runtime.LoadParameters();

                var valid = device.Endpoints.Where(ep =>
                {
                    try { return runtime.Validate(ep); }
                    catch (PluginException ex) { Log.Warn(plugin.Name, ex.Message); return false; }
                }).ToList();

                if (valid.Count == 0)
                {
                    runtime.Dispose();
                    continue;
                }

                var opened = valid.Count(ep => backend.Open(ep) != null);
                if (opened == 0)
                {
                    Log.Warn(plugin.Name, $"Could not open {device.Product}; another program may be using it exclusively.");
                    runtime.Dispose();
                    continue;
                }

                Log.Info("Devices", $"{device.Product} ({device.VendorId:X4}:{device.ProductId:X4}) → {plugin.RelativePath} [{opened} endpoint(s)]");
                return new RgbDevice(key, runtime, DeviceConnection.Usb, $"USB {device.VendorId:X4}:{device.ProductId:X4}");
            }
            catch (Exception ex)
            {
                Log.Warn("Devices", $"{plugin.RelativePath} failed for {device.Product}: {ex.Message}");
                if (runtime != null) runtime.Dispose(); else backend.Dispose();
            }
        }
        return null;
    }

    private static void AddConflictNotices(List<RgbDevice> devices, List<Notice> notices)
    {
        try
        {
            var running = Process.GetProcesses().Select(p => { try { return p.ProcessName; } catch { return ""; } })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Programs a device explicitly can't share with (e.g. TRCC for the Thermalright display): one notice each,
            // with buttons to close it. These devices pause themselves while the program runs.
            var exclusive = devices.Where(d => d.PausesForConflicts)
                .SelectMany(d => d.ConflictingProcesses.Select(p => (Device: d, Exe: p)))
                .GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase);
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in exclusive)
            {
                var exe = g.Key;
                var name = ConflictingApps.ProcessName(exe);
                handled.Add(name);
                if (!running.Contains(name)) continue;
                var startup = ConflictingApps.FindStartupEntry(exe) != null;
                notices.Add(new Notice(NoticeKind.Conflict,
                    $"{name} is running and controls your {g.First().Device.Name}. SimpleRGB has paused that device so they don't fight. Close {name} to let SimpleRGB take over.")
                {
                    App = exe,
                    AppStartsWithWindows = startup,
                });
            }

            var others = KnownRgbApps.Where(running.Contains)
                .Concat(devices.SelectMany(d => d.ConflictingProcesses).Select(ConflictingApps.ProcessName).Where(running.Contains))
                .Where(n => !handled.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (others.Count > 0)
                notices.Add(new Notice(NoticeKind.Conflict,
                    $"{string.Join(", ", others)} is running and may fight SimpleRGB for your lights. Close it for flicker-free lighting."));
        }
        catch { /* best effort */ }
    }

    /// <summary>Re-checks running programs (called periodically by the UI) and refreshes the notices.</summary>
    public void RefreshConflictNotices()
    {
        List<Notice> updated;
        lock (_sync)
        {
            updated = _notices.Where(n => n.Kind != NoticeKind.Conflict).ToList();
        }
        AddConflictNotices(Devices.ToList(), updated);
        bool changed;
        lock (_sync)
        {
            changed = !updated.SequenceEqual(_notices);
            if (changed) _notices = updated;
        }
        if (changed) RaiseDevicesChanged();
    }

    /// <summary>Closes a conflicting program; optionally stops it starting with Windows (saved, so it can be undone).</summary>
    public bool CloseConflictingApp(string exe, bool alsoStopStartup)
    {
        if (alsoStopStartup && ConflictingApps.FindStartupEntry(exe) is { } entry && ConflictingApps.DisableStartup(entry))
            _settings.Update(s =>
            {
                s.DisabledStartupEntries.RemoveAll(e => e.Name == entry.Name && e.KeyPath == entry.KeyPath);
                s.DisabledStartupEntries.Add(entry);
            });
        var closed = ConflictingApps.Close(exe);
        RefreshConflictNotices();
        return closed;
    }

    /// <summary>Puts back every startup entry SimpleRGB removed.</summary>
    public int RestoreStartupEntries()
    {
        var entries = _settings.Read(s => s.DisabledStartupEntries.ToList());
        var restored = entries.Where(ConflictingApps.RestoreStartup).ToList();
        _settings.Update(s => s.DisabledStartupEntries.RemoveAll(e => restored.Contains(e)));
        return restored.Count;
    }

    // ---------------------------------------------------------------- lighting

    public LightingSnapshot BuildLighting(string deviceKey) => _settings.Read(s =>
    {
        var device = s.Devices.GetValueOrDefault(deviceKey);
        return new LightingSnapshot
        {
            LightsOff = s.LightsOff || device is { Enabled: false },
            Sync = s.SyncAll && device is not { ExcludeFromSync: true } ? s.SyncLighting : null,
            Zones = device?.Zones.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, ZoneLighting>(),
            Brightness = s.MasterBrightness / 100.0 * ((device?.Brightness ?? 100) / 100.0),
        };
    });

    /// <summary>A private copy of a device's saved settings (null if none).</summary>
    private DeviceSettings? SavedDevice(string key) => _settings.Read(s =>
        s.Devices.TryGetValue(key, out var d)
            ? new DeviceSettings
            {
                Enabled = d.Enabled,
                Brightness = d.Brightness,
                Parameters = new Dictionary<string, string>(d.Parameters),
                ChannelLedCounts = new Dictionary<string, int>(d.ChannelLedCounts),
                Zones = new Dictionary<string, ZoneLighting>(d.Zones),
            }
            : null);

    /// <summary>Re-sends lighting from the current settings to every running device.</summary>
    public void ApplyLighting()
    {
        foreach (var device in Devices) device.UpdateLighting(BuildLighting(device.Key));
        UpdateAudioDemand();
    }

    public void ApplyLighting(RgbDevice device)
    {
        device.UpdateLighting(BuildLighting(device.Key));
        UpdateAudioDemand();
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnDeviceChanged(RgbDevice device) => DeviceChanged?.Invoke(device);

    private void RaiseDevicesChanged()
    {
        try { DevicesChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("Devices", "DevicesChanged handler failed", ex); }
    }

    private void StopAll(bool turnOff)
    {
        List<RgbDevice> devices;
        List<SmBus> buses;
        lock (_sync)
        {
            devices = _devices;
            buses = _buses;
            _devices = new();
            _buses = new();
        }

        // Stop in parallel so one slow device doesn't hold up the others.
        Parallel.ForEach(devices, d =>
        {
            d.Changed -= OnDeviceChanged;
            d.Stop(turnOff);
            d.Dispose();
        });
        foreach (var bus in buses)
        {
            try { bus.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Stops all devices according to the user's exit preference.</summary>
    public void Shutdown() => Shutdown(null);

    /// <param name="turnOff">Override the exit preference (e.g. keep colors while restarting as admin).</param>
    public void Shutdown(bool? turnOff)
    {
        if (_disposed) return;
        _disposed = true;
        StopAll(turnOff ?? _settings.Read(s => s.ExitBehavior) == ExitBehavior.TurnOff);
        _audio?.SetActive(false);
    }

    public void Dispose() => Shutdown();
}
