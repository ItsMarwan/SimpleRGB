using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Core.Devices;

/// <summary>Computes the exact colors a zone shows right now, for live previews in the UI.</summary>
public static class LightingPreview
{
    public static void Fill(LightingSnapshot lighting, string zoneId, Span<RgbColor> leds, IEffectInputs? inputs = null)
    {
        var frame = new FrameColors(lighting, LightingClock.Seconds, inputs);
        var count = leds.Length;
        for (var i = 0; i < count; i++)
            leds[i] = zoneId.StartsWith("ch:", StringComparison.Ordinal) ? frame.ChannelLed(zoneId[3..], i, count)
                : zoneId.StartsWith("sub:", StringComparison.Ordinal) ? frame.SubdeviceLed(zoneId[4..], i, count)
                : frame.DeviceLed(i, count);
    }

    /// <summary>Preview of one lighting on its own (effect tiles).</summary>
    public static void Fill(ZoneLighting lighting, Span<RgbColor> leds, IEffectInputs? inputs = null)
    {
        var audio = (inputs ?? NoEffectInputs.Instance).Audio;
        var palette = (inputs ?? NoEffectInputs.Instance).Palette;
        var t = LightingClock.Seconds;
        for (var i = 0; i < leds.Length; i++)
            leds[i] = Effects.Evaluate(lighting, new EffectContext
            {
                Time = t, Position = leds.Length > 1 ? i / (double)(leds.Length - 1) : 0, Index = i, Count = leds.Length,
                Seed = 7, Brightness = 1, Audio = audio, Palette = palette,
            });
    }
}
