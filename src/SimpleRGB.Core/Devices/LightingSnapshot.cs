using SimpleRGB.Core.Audio;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Core.Devices;

/// <summary>Immutable lighting state handed from the UI to a device thread.</summary>
public sealed record LightingSnapshot
{
    public IReadOnlyDictionary<string, ZoneLighting> Zones { get; init; } = new Dictionary<string, ZoneLighting>();
    public ZoneLighting? Sync { get; init; }
    public bool LightsOff { get; init; }
    public double Brightness { get; init; } = 1.0;

    public static LightingSnapshot Black { get; } = new() { LightsOff = true };

    public ZoneLighting ForZone(string zoneId) =>
        Sync ?? (Zones.TryGetValue(zoneId, out var z) ? z : ZoneLighting.Default);

    public bool IsAnimated(IEnumerable<string> zoneIds) =>
        !LightsOff && Brightness > 0 && (Sync?.IsAnimated ?? zoneIds.Any(id => ForZone(id).IsAnimated));

    public bool UsesMusic(IEnumerable<string> zoneIds) =>
        !LightsOff && Brightness > 0 && (Sync != null ? Sync.Mode == LightingMode.Music : zoneIds.Any(id => ForZone(id).Mode == LightingMode.Music));
}

/// <summary>Live inputs shared by all devices for one frame (music levels, album colors).</summary>
public interface IEffectInputs
{
    AudioFrame Audio { get; }
    IReadOnlyList<RgbColor> Palette { get; }
}

internal sealed class NoEffectInputs : IEffectInputs
{
    public static readonly NoEffectInputs Instance = new();
    public AudioFrame Audio => AudioFrame.Silent;
    public IReadOnlyList<RgbColor> Palette => Array.Empty<RgbColor>();
}

/// <summary>Turns a lighting snapshot plus the shared clock into per-LED colors for a plugin.</summary>
internal sealed class FrameColors : IFrameColors
{
    private readonly LightingSnapshot _lighting;
    private readonly double _seconds;
    private readonly AudioFrame _audio;
    private readonly IReadOnlyList<RgbColor> _palette;
    private readonly Dictionary<string, (string Zone, uint Seed)> _subIds = new();
    private readonly Dictionary<string, (string Zone, uint Seed)> _chIds = new();
    private static readonly uint DeviceSeed = Fnv(ZoneInfo.DeviceZoneId);

    public FrameColors(LightingSnapshot lighting, double seconds, IEffectInputs? inputs = null)
    {
        _lighting = lighting;
        _seconds = seconds;
        inputs ??= NoEffectInputs.Instance;
        _audio = inputs.Audio;
        _palette = inputs.Palette;
    }

    private RgbColor Color(string zoneId, uint seed, double position, int index, int count) =>
        _lighting.LightsOff
            ? RgbColor.Black
            : Effects.Evaluate(_lighting.ForZone(zoneId), new EffectContext
            {
                Time = _seconds,
                Position = position,
                Index = index,
                Count = count,
                Seed = seed,
                Brightness = _lighting.Brightness,
                Audio = _audio,
                Palette = _palette,
            });

    private static double Pos(double v, int size) => size > 1 ? Math.Clamp(v / (size - 1), 0, 1) : 0;

    public RgbColor DeviceLed(int index, int count) =>
        Color(ZoneInfo.DeviceZoneId, DeviceSeed, Pos(index, count), index, count);

    public RgbColor SubdeviceLed(string subdeviceId, int index, int count)
    {
        if (!_subIds.TryGetValue(subdeviceId, out var z))
        {
            var id = ZoneInfo.SubdeviceZoneId(subdeviceId);
            _subIds[subdeviceId] = z = (id, Fnv(id));
        }
        return Color(z.Zone, z.Seed, Pos(index, count), index, count);
    }

    public RgbColor ChannelLed(string channelId, int index, int count)
    {
        if (!_chIds.TryGetValue(channelId, out var z))
        {
            var id = ZoneInfo.ChannelZoneId(channelId);
            _chIds[channelId] = z = (id, Fnv(id));
        }
        return Color(z.Zone, z.Seed, Pos(index, count), index, count);
    }

    public RgbColor ZoneBaseColor(string zoneId) => Color(zoneId, Fnv(zoneId), 0, 0, 1);

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var ch in s) { h ^= ch; h *= 16777619u; }
        return h;
    }
}
