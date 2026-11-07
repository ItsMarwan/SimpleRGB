namespace SimpleRGB.Core.Plugins;

public enum ZoneKind { Device, Subdevice, Channel }

/// <summary>An addressable part of a device the user can color independently.</summary>
public sealed record ZoneInfo(string Id, string Name, ZoneKind Kind, int LedCount, int LedLimit)
{
    public static string DeviceZoneId => "device";
    public static string SubdeviceZoneId(string id) => "sub:" + id;
    public static string ChannelZoneId(string id) => "ch:" + id;
}

public sealed record DeviceMessage(string Id, string Title, string Text, int Priority);

/// <summary>Immutable view of a device's layout for the UI. Rebuilt whenever the plugin changes it.</summary>
public sealed record DeviceStructure(
    string Name,
    IReadOnlyList<ZoneInfo> Zones,
    IReadOnlyList<PluginParameter> Parameters,
    IReadOnlyList<DeviceMessage> Messages,
    int Version);

/// <summary>
/// Everything a plugin has told us about the device's layout. Only touched on the device's own thread;
/// the UI gets <see cref="Snapshot"/>.
/// </summary>
internal sealed class DeviceModel
{
    public sealed class Subdevice
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public int Width { get; set; } = 1;
        public int Height { get; set; } = 1;
        public List<string> LedNames { get; set; } = new();
        public List<(int X, int Y)> LedPositions { get; set; } = new();
    }

    public sealed class Channel
    {
        public required string Id { get; init; }
        public int LedLimit { get; set; }
        public int LedCount { get; set; }
    }

    public string Name { get; set; } = "";
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public List<string> LedNames { get; set; } = new();
    public List<(int X, int Y)> LedPositions { get; set; } = new();
    public int TotalLedLimit { get; set; }
    public List<Subdevice> Subdevices { get; } = new();
    public List<Channel> Channels { get; } = new();
    public List<PluginParameter> Parameters { get; } = new();
    public Dictionary<string, DeviceMessage> Messages { get; } = new();
    public int FrameRateTarget { get; set; }
    /// <summary>Frames per second to keep sending even when nothing animates (e.g. a live sensor display).</summary>
    public int MinimumFrameRate { get; set; }

    /// <summary>User-chosen LED counts per channel id (applied when the plugin adds the channel).</summary>
    public Dictionary<string, int> RequestedChannelCounts { get; } = new();

    private int _version;
    public bool Dirty { get; private set; } = true;
    public void Touch() { Dirty = true; }

    public Subdevice? FindSubdevice(string id) => Subdevices.FirstOrDefault(s => s.Id == id);

    /// <summary>Order of the LED at (x, y) in <paramref name="positions"/>; falls back to x/y for unknown points.</summary>
    public static (int Index, int Count) IndexOf(List<(int X, int Y)> positions, double x, double y, int width, int height)
    {
        var count = positions.Count;
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        for (var i = 0; i < count; i++)
            if (positions[i].X == ix && positions[i].Y == iy) return (i, count);
        // Not one of the declared LEDs: place it along the longer axis.
        return width >= height ? (Math.Clamp(ix, 0, Math.Max(0, width - 1)), Math.Max(1, width))
                               : (Math.Clamp(iy, 0, Math.Max(0, height - 1)), Math.Max(1, height));
    }
    public Channel? FindChannel(string id) => Channels.FirstOrDefault(c => c.Id == id);

    public DeviceStructure Snapshot()
    {
        Dirty = false;
        var zones = new List<ZoneInfo>();
        if (LedPositions.Count > 0)
            zones.Add(new ZoneInfo(ZoneInfo.DeviceZoneId, "Device LEDs", ZoneKind.Device, LedPositions.Count, LedPositions.Count));
        foreach (var s in Subdevices)
            zones.Add(new ZoneInfo(ZoneInfo.SubdeviceZoneId(s.Id), CleanName(s.Name, s.Id), ZoneKind.Subdevice, s.LedPositions.Count, s.LedPositions.Count));
        foreach (var c in Channels)
            zones.Add(new ZoneInfo(ZoneInfo.ChannelZoneId(c.Id), c.Id, ZoneKind.Channel, c.LedCount, c.LedLimit));
        return new DeviceStructure(Name, zones, Parameters.ToList(), Messages.Values.ToList(), ++_version);
    }

    /// <summary>Plugins often name subdevices "Parent Device - 12V Header 1"; the zone list only needs the last part.</summary>
    private static string CleanName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var idx = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return idx >= 0 && idx + 3 < name.Length ? name[(idx + 3)..] : name;
    }
}

/// <summary>Supplies LED colors to a plugin during one frame.</summary>
/// <remarks>LEDs are addressed by their order within the zone (not grid coordinates), so effects line up
/// across devices: a single-LED zone is always at the start of an effect, strips flow in LED order.</remarks>
public interface IFrameColors
{
    Lighting.RgbColor DeviceLed(int index, int count);
    Lighting.RgbColor SubdeviceLed(string subdeviceId, int index, int count);
    Lighting.RgbColor ChannelLed(string channelId, int index, int count);
    Lighting.RgbColor ZoneBaseColor(string zoneId);
}
