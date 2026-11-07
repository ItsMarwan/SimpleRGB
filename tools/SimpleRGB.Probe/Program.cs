// SimpleRGB.Probe: command-line diagnostics for SimpleRGB.
//   list                  read-only: HID devices, matching plugins, GPU/SMBus buses (no writes)
//   run [options]         detect devices and drive them like the app does
//       --color RRGGBB    static color for every zone (default 00A2FF)
//       --mode M          Static | Breathing | ColorCycle | Rainbow | Off
//       --seconds N       how long to run (default 8)
//       --no-smbus        skip GPU/RAM buses (USB devices only)
//       --only TEXT       only devices whose name contains TEXT
//       --turn-off        turn lights off on exit (default: keep colors)
using System.Diagnostics;
using SimpleRGB.Core;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Platform;
using SimpleRGB.Core.Plugins;
using SimpleRGB.Core.Settings;

var cmd = args.Length > 0 ? args[0] : "list";
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);

Log.MinimumLevel = Flag("--debug") ? LogLevel.Debug : LogLevel.Info;
Log.EntryLogged += e => Console.WriteLine(e);

Console.WriteLine($"SimpleRGB probe | board: {SystemInfo.MotherboardManufacturer} {SystemInfo.MotherboardModel} | memory: {SystemInfo.MemoryGeneration} | admin: {SystemInfo.IsElevated}");

switch (cmd)
{
    case "list": List(); break;
    case "run": await Run(); break;
    case "audio": await Audio(); break;
    case "thermalright-id": ThermalrightId(); break;
    case "display": await Display(); break;
    case "albumart": await AlbumArt(); break;
    case "sensors":
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(1200);
            var r = SimpleRGB.Core.Sensors.SensorService.Shared.Current;
            Console.WriteLine($"CPU {r.CpuTemp?.ToString("0") ?? "n/a"} °C, {r.CpuLoad?.ToString("0") ?? "n/a"} %   GPU {r.GpuTemp?.ToString("0") ?? "n/a"} °C, {r.GpuLoad?.ToString("0") ?? "n/a"} %");
        }
        break;
    default: Console.WriteLine("usage: probe list | probe run [--color RRGGBB] [--mode M] [--seconds N] [--no-smbus] [--only TEXT] [--turn-off]"); break;
}
Log.Flush();
return;

// probe thermalright-id : sends TRCC's identify query (DA DB DC DD) to 0416:8001 and prints the reply. Read-only.
void ThermalrightId()
{
    var dev = HidEnumerator.EnumerateDevices().FirstOrDefault(d => d.VendorId == 0x0416 && d.ProductId == 0x8001);
    if (dev == null) { Console.WriteLine("0416:8001 not found"); return; }
    foreach (var ep in dev.Endpoints) Console.WriteLine("endpoint: " + ep);
    var ep0 = dev.Endpoints[0];
    using var c = HidConnection.Open(ep0, out var err);
    if (c == null) { Console.WriteLine("open failed: " + err); return; }
    var q = new byte[65];
    byte[] p = { 0xDA, 0xDB, 0xDC, 0xDD, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
    p.CopyTo(q, 1);
    Console.WriteLine("write: " + c.Write(q));
    var buf = new byte[65];
    for (var i = 0; i < 3; i++)
    {
        var n = c.Read(buf, 1000);
        Console.WriteLine($"read {n}: " + BitConverter.ToString(buf, 0, Math.Max(n, 0)));
        if (n > 0) break;
    }
}

// probe display [--color RRGGBB] [--seconds N] : drives ONLY the Thermalright display with live sensors, then blanks it.
async Task Display()
{
    var seconds = int.TryParse(Opt("--seconds"), out var sec) ? sec : 30;
    var color = RgbColor.Parse("#" + (Opt("--color") ?? "00C8FF").TrimStart('#'), RgbColor.White);
    var dev = HidEnumerator.EnumerateDevices().FirstOrDefault(d => d.VendorId == 0x0416 && d.ProductId == 0x8001);
    if (dev == null) { Console.WriteLine("display not found"); return; }
    var root = Path.Combine(AppContext.BaseDirectory, "Plugins");
    var info = new PluginInfo { FilePath = Path.Combine(root, "Thermalright", "Thermalright_Digital_Display.js"),
        RelativePath = "Thermalright/Thermalright_Digital_Display.js", RootDir = root, Source = "SimpleRGB", Kind = PluginKind.Hid };
    var backend = new HidBackend(dev);
    using var rt = PluginRuntime.LoadForHid(info, backend);
    rt.LoadParameters();
    foreach (var ep in dev.Endpoints.Where(rt.Validate)) backend.Open(ep);
    var lighting = new LightingSnapshot { Sync = new ZoneLighting { Mode = LightingMode.Static, Color = color.ToHex() } };
    rt.Initialize(new FrameColorsProxy(lighting));
    Console.WriteLine($"Device: {rt.TakeStructure().Name}");
    var end = DateTime.Now.AddSeconds(seconds);
    var n = 0;
    while (DateTime.Now < end)
    {
        rt.Render(new FrameColorsProxy(lighting));
        if (n++ % 8 == 0)
        {
            var r = SimpleRGB.Core.Sensors.SensorService.Shared.Current;
            Console.WriteLine($"showing CPU {r.CpuTemp?.ToString("0") ?? "--"}C {r.CpuLoad?.ToString("0") ?? "--"}%  GPU {r.GpuTemp?.ToString("0") ?? "--"}C {r.GpuLoad?.ToString("0") ?? "--"}%");
        }
        await Task.Delay(250);
    }
    rt.Shutdown(new FrameColorsProxy(LightingSnapshot.Black), false);
    Console.WriteLine("Display blanked, done.");
}

// probe albumart --out DIR : saves the current song's album art (as Windows provides it) + a palette preview.
async Task AlbumArt()
{
    var dir = Opt("--out") ?? ".";
    var mgr = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var session = mgr.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) ?? mgr.GetCurrentSession();
    if (session == null) { Console.WriteLine("no media session"); return; }
    var props = await session.TryGetMediaPropertiesAsync();
    Console.WriteLine($"{props.Title} - {props.Artist}  (thumbnail: {props.Thumbnail != null})");
    if (props.Thumbnail == null) return;
    using var stream = await props.Thumbnail.OpenReadAsync();
    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
    Console.WriteLine($"art size {decoder.PixelWidth}x{decoder.PixelHeight}");
    var full = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
        new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
    var px = full.DetachPixelData();
    SaveBmp(Path.Combine(dir, "art.bmp"), px, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    var small = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
        new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 48, ScaledHeight = 48, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant },
        Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
    var palette = SimpleRGB.Core.Media.AlbumPalette.Extract(small.DetachPixelData());
    Console.WriteLine("palette: " + string.Join(" ", palette));
    // Swatch strip 60px per color.
    var w = Math.Max(1, palette.Count) * 60;
    var strip = new byte[w * 60 * 4];
    for (var y = 0; y < 60; y++) for (var x = 0; x < w; x++)
    {
        var c = palette.Count == 0 ? RgbColor.Black : palette[x / 60];
        var o = (y * w + x) * 4; strip[o] = c.B; strip[o + 1] = c.G; strip[o + 2] = c.R; strip[o + 3] = 255;
    }
    SaveBmp(Path.Combine(dir, "palette.bmp"), strip, w, 60);
}

static void SaveBmp(string path, byte[] bgra, int w, int h)
{
    using var f = new BinaryWriter(File.Create(path));
    var size = 54 + w * h * 4;
    f.Write((ushort)0x4D42); f.Write(size); f.Write(0); f.Write(54);
    f.Write(40); f.Write(w); f.Write(-h); f.Write((ushort)1); f.Write((ushort)32); f.Write(0); f.Write(w * h * 4); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
    f.Write(bgra, 0, w * h * 4);
}

// probe audio --pid N [--seconds S]   : capture one process only (process loopback) and print music features
// probe audio --all [--seconds S]     : capture all PC audio
async Task Audio()
{
    var seconds = int.TryParse(Opt("--seconds"), out var sec) ? sec : 6;
    var analyzer = new SimpleRGB.Core.Audio.AudioAnalyzer();
    var samples = 0L;
    void Sink(float[] b, int n, int rate) { samples += n; analyzer.Feed(b.AsSpan(0, n), rate); }
    IDisposable? capture;
    if (int.TryParse(Opt("--pid"), out var pid))
    {
        capture = SimpleRGB.Core.Audio.ProcessLoopbackCapture.TryStart(pid, Sink, out var error);
        Console.WriteLine(capture != null ? $"Process loopback started for PID {pid}" : $"Process loopback FAILED: {error}");
        if (capture == null) return;
    }
    else
    {
        capture = new SimpleRGB.Core.Audio.SystemLoopbackCapture(Sink);
        Console.WriteLine("System loopback started");
    }
    var np = new SimpleRGB.Core.Media.NowPlayingService();
    await np.StartAsync();
    for (var i = 0; i < seconds * 4; i++)
    {
        await Task.Delay(250);
        var f = analyzer.Current;
        Console.WriteLine($"t={i / 4.0,5:F2}s samples={samples,7} signal={(f.HasSignal ? "yes" : "no ")} bass={Bar(f.Bass)} mid={Bar(f.Mid)} treble={Bar(f.Treble)} beats={f.BeatCount}");
    }
    var now = np.Current;
    Console.WriteLine(now.HasTrack ? $"Now playing: {now.Title} - {now.Artist} ({now.App}), palette {string.Join(" ", now.Palette)}" : "Now playing: (no media session)");
    capture.Dispose();
    static string Bar(double v) => new string('#', (int)Math.Round(Math.Clamp(v, 0, 1) * 10)).PadRight(10, '.');
}

void List()
{
    var sw = Stopwatch.StartNew();
    var library = new PluginLibrary();
    var imported = library.ImportFromSignalRgb();
    Console.WriteLine($"\nPlugins: {library.Count} files ({imported} updated from SignalRGB) in {sw.ElapsedMilliseconds} ms");

    var devices = HidEnumerator.EnumerateDevices();
    sw.Restart();
    var (hid, smbus) = library.Discover(devices.Select(d => d.VendorId).ToList());
    Console.WriteLine($"Metadata: {hid.Count} candidate HID plugins, {smbus.Count} SMBus plugins in {sw.ElapsedMilliseconds} ms\n");

    foreach (var d in devices)
    {
        var matches = hid.Where(p => p.Matches(d.VendorId, d.ProductId)).ToList();
        Console.WriteLine($"[{d.VendorId:X4}:{d.ProductId:X4}] {d.Product} ({d.Manufacturer}) {d.InstanceId}");
        foreach (var e in d.Endpoints) Console.WriteLine($"      {e}");
        Console.WriteLine(matches.Count == 0 ? "   -> no plugin" : "   -> " + string.Join(", ", matches.Select(m => m.RelativePath)));
    }

    Console.WriteLine("\nGPU I2C buses:");
    foreach (var bus in NvApiI2CBus.EnumerateGpus())
    {
        Console.WriteLine($"   {bus.Name}: {bus.Vendor:X4}:{bus.Product:X4} sub {bus.SubVendor:X4}:{bus.SubDevice:X4}");
        bus.Dispose();
    }

    Console.WriteLine("\nChipset SMBus:");
    using var pawn = PawnIoSmBus.TryOpen(out var reason);
    Console.WriteLine(pawn != null
        ? $"   {pawn.Name}: PCI {pawn.Vendor:X4}:{pawn.Product:X4} sub {pawn.SubVendor:X4}:{pawn.SubDevice:X4}"
        : $"   unavailable: {reason}");
}

async Task Run()
{
    var color = Opt("--color") ?? "00A2FF";
    var mode = Enum.TryParse<LightingMode>(Opt("--mode") ?? "Static", true, out var m) ? m : LightingMode.Static;
    var seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 8;
    var only = Opt("--only");

    // Private settings so the probe never touches the user's real settings.json.
    var dir = Path.Combine(Path.GetTempPath(), "SimpleRGB.Probe");
    Directory.CreateDirectory(dir);
    var store = new SettingsStore(Path.Combine(dir, "probe-settings.json"));
    store.Current.EnableSmBusDevices = !Flag("--no-smbus");
    store.Current.SyncAll = true;
    store.Current.SyncLighting = new ZoneLighting { Mode = mode, Color = "#" + color.TrimStart('#'), Speed = 50 };
    store.Current.ExitBehavior = Flag("--turn-off") ? ExitBehavior.TurnOff : ExitBehavior.KeepColors;

    using var manager = new DeviceManager(store, new PluginLibrary());
    var sw = Stopwatch.StartNew();
    await manager.ScanAsync();
    Console.WriteLine($"\nScan took {sw.ElapsedMilliseconds} ms");

    if (only != null)
        foreach (var d in manager.Devices.Where(d => !d.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
            d.Stop(turnOff: false);

    await Task.Delay(1500);
    foreach (var n in manager.Notices) Console.WriteLine($"NOTICE [{n.Kind}] {n.Text}");
    foreach (var u in manager.UnsupportedDevices) Console.WriteLine($"unsupported: {u.Name} {u.VendorId:X4}:{u.ProductId:X4}");
    foreach (var d in manager.Devices)
    {
        Console.WriteLine($"\nDEVICE {d.Name} [{d.State}: {d.StatusText}] via {d.ConnectionText}");
        Console.WriteLine($"   plugin {d.Plugin.RelativePath} ({d.Plugin.Source})");
        foreach (var z in d.Structure.Zones) Console.WriteLine($"   zone {z.Id} \"{z.Name}\" {z.Kind} leds={z.LedCount}/{z.LedLimit}");
        foreach (var p in d.Structure.Parameters.Where(p => !p.IsManagedByHost))
            Console.WriteLine($"   param {p.Property} ({p.Type}) = {d.GetParameterValue(p.Property)}");
        foreach (var msg in d.Structure.Messages) Console.WriteLine($"   message: {msg.Title} {msg.Text}");
    }

    Console.WriteLine($"\nRunning {mode} {color} for {seconds}s …");
    var proc = Process.GetCurrentProcess();
    var cpuStart = proc.TotalProcessorTime;
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    proc.Refresh();
    var cpu = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds / (seconds * 1000.0) / Environment.ProcessorCount * 100;
    Console.WriteLine($"CPU while running: {cpu:F2}% of the machine, working set {proc.WorkingSet64 / 1024 / 1024} MB");
    foreach (var d in manager.Devices) Console.WriteLine($"   {d.Name}: {d.State} ({d.StatusText})");
    manager.Shutdown();
    Console.WriteLine("Stopped.");
}


sealed class FrameColorsProxy(LightingSnapshot s) : IFrameColors
{
    private RgbColor C(string zone, int i, int c) => s.LightsOff ? RgbColor.Black
        : Effects.Evaluate(s.ForZone(zone), new EffectContext { Time = LightingClock.Seconds, Position = c > 1 ? i / (double)(c - 1) : 0, Index = i, Count = c, Brightness = 1 });
    public RgbColor DeviceLed(int i, int c) => C(ZoneInfo.DeviceZoneId, i, c);
    public RgbColor SubdeviceLed(string id, int i, int c) => C(ZoneInfo.SubdeviceZoneId(id), i, c);
    public RgbColor ChannelLed(string id, int i, int c) => C(ZoneInfo.ChannelZoneId(id), i, c);
    public RgbColor ZoneBaseColor(string z) => C(z, 0, 1);
}
