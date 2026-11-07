using System.Collections.Concurrent;
using System.Diagnostics;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Core.Devices;

public enum DeviceState { Starting, Running, Paused, Error, Disconnected, Stopped }

public enum DeviceConnection { Usb, SmBus, GpuI2C }

/// <summary>
/// A detected device driven by one plugin instance on its own background thread.
/// All plugin calls happen on that thread; everything public here is thread-safe.
/// </summary>
public sealed class RgbDevice : IDisposable
{
    private const int StaticFps = 4;      // keep-alive rate when nothing moves
    private const int AnimatedFps = 30;
    private const int MusicFps = 60;

    /// <summary>USB can take 60 fps; chipset SMBus / GPU I2C are slow shared buses, so they get 30.</summary>
    private int MaxFps => Connection == DeviceConnection.Usb ? 60 : 30;
    private const int MaxConsecutiveFailures = 8;

    private readonly PluginRuntime _runtime;
    private readonly ConcurrentQueue<Action<PluginRuntime>> _actions = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IEffectInputs _inputs = NoEffectInputs.Instance;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _turnOffOnStop;
    private readonly bool _alwaysShutdown;
    private readonly bool _pauseWhenConflicting;
    private long _lastConflictCheck;
    private string? _conflict;
    private volatile LightingSnapshot _lighting = LightingSnapshot.Black;
    private volatile DeviceStructure _structure;
    private volatile DeviceState _state = DeviceState.Starting;
    private volatile string _statusText = "Starting…";

    public string Key { get; }
    public PluginInfo Plugin => _runtime.Info;
    public DeviceConnection Connection { get; }
    public string ConnectionText { get; }
    public string DeviceType { get; }
    public IReadOnlyList<string> ConflictingProcesses { get; }

    public DeviceStructure Structure => _structure;
    public DeviceState State => _state;
    public string StatusText => _statusText;
    public string Name => string.IsNullOrWhiteSpace(_structure.Name) ? Plugin.Name : _structure.Name;

    /// <summary>Raised (on the device thread) when the zone layout, name, messages or state change.</summary>
    public event Action<RgbDevice>? Changed;

    internal RgbDevice(string key, PluginRuntime runtime, DeviceConnection connection, string connectionText)
    {
        Key = key;
        _runtime = runtime;
        Connection = connection;
        ConnectionText = connectionText;
        DeviceType = runtime.GetString("DeviceType").ToLowerInvariant();
        ConflictingProcesses = runtime.GetStringList("ConflictingProcesses");
        _alwaysShutdown = runtime.AlwaysShutdown;
        _pauseWhenConflicting = runtime.PauseWhenConflicting && ConflictingProcesses.Count > 0;
        _structure = runtime.TakeStructure();
    }

    public void Start(LightingSnapshot lighting, IEffectInputs? inputs = null)
    {
        _lighting = lighting;
        _inputs = inputs ?? NoEffectInputs.Instance;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"SimpleRGB device: {Name}",
            Priority = ThreadPriority.AboveNormal, // smooth effects even when the PC is busy
        };
        _thread.Start();
    }

    public void UpdateLighting(LightingSnapshot lighting)
    {
        _lighting = lighting;
        _wake.Set();
    }

    public void SetParameter(string property, string value) => Enqueue(r => r.SetParameter(property, value));

    public void SetChannelLedCount(string channelId, int count) => Enqueue(r =>
    {
        if (r.Model.FindChannel(channelId) is { } ch)
        {
            ch.LedCount = Math.Clamp(count, 0, ch.LedLimit);
            r.Model.Touch();
        }
    });

    public string? GetParameterValue(string property) => _runtime.GetParameterValue(property);

    private void Enqueue(Action<PluginRuntime> action)
    {
        _actions.Enqueue(action);
        _wake.Set();
    }

    /// <summary>Stops the device thread. With <paramref name="turnOff"/> the plugin's Shutdown() turns lights off.</summary>
    public void Stop(bool turnOff, int timeoutMs = 4000)
    {
        _turnOffOnStop = turnOff;
        _stopRequested = true;
        _wake.Set();
        if (_thread != null && _thread.IsAlive && !_thread.Join(timeoutMs))
            Log.Warn(Name, "Device thread did not stop in time.");
        if (_state != DeviceState.Disconnected && _state != DeviceState.Error) SetState(DeviceState.Stopped, "Stopped");
    }

    private void Run()
    {
        // Last line of defence: an exception escaping a background thread would terminate the app.
        try { RunCore(); }
        catch (Exception ex)
        {
            Log.Error(Name, "Device thread crashed", ex);
            SetState(DeviceState.Error, "Stopped: " + ex.Message);
        }
    }

    private void RunCore()
    {
        try
        {
            SetState(DeviceState.Starting, "Initializing…");
            _runtime.Initialize(new FrameColors(_lighting, LightingClock.Seconds, _inputs));
            PublishStructureIfChanged();
            SetState(DeviceState.Running, "Running");
            Log.Info(Name, $"Initialized ({ConnectionText}, plugin {Plugin.RelativePath}).");
        }
        catch (DeviceGoneException ex)
        {
            SetState(DeviceState.Disconnected, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(Name, "Initialize failed", ex);
            SetState(DeviceState.Error, "Could not initialize: " + ex.Message);
            return;
        }

        var failures = 0;
        while (!_stopRequested)
        {
            if (_pauseWhenConflicting && IsPausedForConflict())
            {
                _wake.WaitOne(1000);
                continue;
            }
            var frameStart = _clock.Elapsed;
            var lighting = _lighting;
            try
            {
                while (_actions.TryDequeue(out var action)) action(_runtime);
                // The shared LightingClock keeps every device's animation in step.
                _runtime.Render(new FrameColors(lighting, LightingClock.Seconds, _inputs));
                PublishStructureIfChanged();
                if (failures > 0)
                {
                    failures = 0;
                    SetState(DeviceState.Running, "Running");
                }
            }
            catch (DeviceGoneException ex)
            {
                Log.Warn(Name, ex.Message);
                SetState(DeviceState.Disconnected, "Disconnected");
                return;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error(Name, $"Render failed ({failures}/{MaxConsecutiveFailures})", ex);
                if (failures >= MaxConsecutiveFailures)
                {
                    SetState(DeviceState.Error, "Stopped after repeated errors: " + ex.Message);
                    return;
                }
                _wake.WaitOne(500);
                continue;
            }

            var zoneIds = _structure.Zones.Select(z => z.Id).ToList();
            var animated = lighting.IsAnimated(zoneIds);
            var fps = !animated ? Math.Max(StaticFps, _runtime.MinimumFrameRate) : lighting.UsesMusic(zoneIds) ? MusicFps : AnimatedFps;
            fps = Math.Min(fps, MaxFps);
            if (animated && _runtime.FrameRateTarget > 0) fps = Math.Min(fps, _runtime.FrameRateTarget);
            // Never faster than MaxFps, even while the user drags the color picker (each change wakes us).
            var minGap = TimeSpan.FromSeconds(1.0 / MaxFps) - (_clock.Elapsed - frameStart);
            if (minGap > TimeSpan.Zero) Thread.Sleep(minGap);
            var wait = TimeSpan.FromSeconds(1.0 / fps) - (_clock.Elapsed - frameStart);
            if (wait > TimeSpan.Zero) _wake.WaitOne(wait);
        }

        if (_turnOffOnStop || _alwaysShutdown)
        {
            try
            {
                _runtime.SetHostGlobal("shutdownColor", "#000000");
                _runtime.Shutdown(new FrameColors(LightingSnapshot.Black, 0), systemSuspending: false);
            }
            catch (Exception ex)
            {
                Log.Warn(Name, $"Shutdown failed: {ex.Message}");
            }
        }
    }

    /// <summary>Checks (every 2 s) whether e.g. TRCC is running; while it is, this device stays hands-off.</summary>
    private bool IsPausedForConflict()
    {
        var now = Environment.TickCount64;
        if (now - _lastConflictCheck >= 2000)
        {
            _lastConflictCheck = now;
            var running = ConflictingProcesses.FirstOrDefault(Platform.ConflictingApps.IsRunning);
            if (running != _conflict)
            {
                _conflict = running;
                if (running != null)
                {
                    Log.Info(Name, $"Paused: {running} is running and controls this device.");
                    SetState(DeviceState.Paused, $"Paused while {Platform.ConflictingApps.ProcessName(running)} is running");
                }
                else
                {
                    Log.Info(Name, "Resumed.");
                    SetState(DeviceState.Running, "Running");
                }
            }
        }
        return _conflict != null;
    }

    public string? PausedBecauseOf => _conflict;
    public bool PausesForConflicts => _pauseWhenConflicting;

    private void PublishStructureIfChanged()
    {
        if (!_runtime.StructureDirty) return;
        _structure = _runtime.TakeStructure();
        RaiseChanged();
    }

    private void SetState(DeviceState state, string text)
    {
        if (_state == state && _statusText == text) return;
        _state = state;
        _statusText = text;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this); }
        catch (Exception ex) { Log.Error(Name, "Change handler failed", ex); }
    }

    public void Dispose()
    {
        if (_thread is { IsAlive: true }) Stop(turnOff: false);
        // If the plugin is stuck inside a call, leave its engine alone; the thread is a background
        // thread and dies with the process. Disposing under it could corrupt state.
        if (_thread is { IsAlive: true }) return;
        _runtime.Dispose();
        _wake.Dispose();
    }
}
