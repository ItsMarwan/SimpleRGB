using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Lighting;

namespace SimpleRGB.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExitBehavior
{
    /// <summary>Stop updating; most controllers keep showing the last colors.</summary>
    KeepColors,
    /// <summary>Turn all lights off when SimpleRGB exits.</summary>
    TurnOff,
}

public sealed class DeviceSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Name the user gave this device (null = automatic).</summary>
    public string? Name { get; set; }
    /// <summary>Keep this device's own lighting even when "Sync all devices" is on.</summary>
    public bool ExcludeFromSync { get; set; }
    public int Brightness { get; set; } = 100;
    public Dictionary<string, ZoneLighting> Zones { get; set; } = new();
    public Dictionary<string, int> ChannelLedCounts { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool EnableSmBusDevices { get; set; } = true;
    public ExitBehavior ExitBehavior { get; set; } = ExitBehavior.KeepColors;
    public int MasterBrightness { get; set; } = 100;
    public bool LightsOff { get; set; }
    public bool SyncAll { get; set; }
    public ZoneLighting SyncLighting { get; set; } = ZoneLighting.Default;
    public bool ShownTrayHint { get; set; }
    /// <summary>Colors the user pinned in the color picker.</summary>
    public List<string> FavoriteColors { get; set; } = new();
    /// <summary>Most recently used colors, newest first.</summary>
    public List<string> RecentColors { get; set; } = new();
    public Audio.AudioSource AudioSource { get; set; } = Audio.AudioSource.Spotify;
    /// <summary>Devices found last time (lets SimpleRGB retry a GPU that wasn't ready at boot).</summary>
    public List<string> KnownDevices { get; set; } = new();

    /// <summary>Other programs' "start with Windows" entries the user asked SimpleRGB to remove (kept for undo).</summary>
    public List<Platform.StartupEntry> DisabledStartupEntries { get; set; } = new();
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    public DeviceSettings ForDevice(string key)
    {
        if (!Devices.TryGetValue(key, out var d))
        {
            d = new DeviceSettings();
            Devices[key] = d;
        }
        return d;
    }

    public void Normalize()
    {
        MasterBrightness = Math.Clamp(MasterBrightness, 0, 100);
        SyncLighting = (SyncLighting ?? ZoneLighting.Default).Normalize();
        Devices ??= new();
        KnownDevices ??= new();
        FavoriteColors ??= new();
        RecentColors ??= new();
        DisabledStartupEntries ??= new();
        foreach (var d in Devices.Values)
        {
            d.Brightness = Math.Clamp(d.Brightness, 0, 100);
            d.Zones ??= new();
            d.ChannelLedCounts ??= new();
            d.Parameters ??= new();
            foreach (var key in d.Zones.Keys.ToList())
                d.Zones[key] = (d.Zones[key] ?? ZoneLighting.Default).Normalize();
        }
    }
}

/// <summary>Loads and saves settings.json atomically (write temp file, then replace).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _sync = new();
    private Timer? _debounce;

    public AppSettings Current { get; private set; }

    public event Action? Saved;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Current = Load(_path);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for inspection and start fresh instead of crashing.
            Log.Error("Settings", $"settings.json is unreadable, starting with defaults ({ex.Message}).");
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* ignore */ }
        }
        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    /// <summary>Saves shortly after the last change so dragging a slider doesn't hammer the disk.</summary>
    public void SaveSoon(int delayMs = 400)
    {
        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => SaveNow(), null, delayMs, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        string json;
        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = null;
            json = JsonSerializer.Serialize(Current, Options);
        }

        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Could not save settings", ex);
        }
    }

    /// <summary>Reads settings under the store lock (safe from any thread).</summary>
    public T Read<T>(Func<AppSettings, T> read)
    {
        lock (_sync) return read(Current);
    }

    /// <summary>Runs a mutation under the store lock and schedules a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_sync) change(Current);
        SaveSoon();
    }
}
