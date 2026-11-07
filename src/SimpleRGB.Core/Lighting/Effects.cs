using System.Diagnostics;
using System.Text.Json.Serialization;
using SimpleRGB.Core.Audio;

namespace SimpleRGB.Core.Lighting;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LightingMode
{
    Static,
    Breathing,
    ColorCycle,
    Rainbow,
    Gradient,
    ColorWave,
    Comet,
    Sparkle,
    Candle,
    Heartbeat,
    Music,
    Off,
}

/// <summary>Which part of the song drives the lights in Music mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicReact { Beat, Bass, Vocals, Treble, Everything }

/// <summary>How Music mode picks colors.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicColors { SameColor, NewColorEveryBeat, AlbumArt, Frequency }

/// <summary>How Music mode draws on a strip.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicStyle { Pulse, Equalizer }

/// <summary>What one zone (a header, a channel, a GPU, a RAM stick…) should show.</summary>
public sealed record ZoneLighting
{
    public LightingMode Mode { get; init; } = LightingMode.Static;
    public string Color { get; init; } = "#00A2FF";
    /// <summary>Second color for Gradient, Color wave and Sparkle.</summary>
    public string Color2 { get; init; } = "#FF2D95";

    /// <summary>1 (slow) … 100 (fast). Animation speed, or sensitivity in Music mode.</summary>
    public int Speed { get; init; } = 50;

    /// <summary>1 … 100: rainbow/wave width, comet tail length, sparkle density.</summary>
    public int Size { get; init; } = 50;
    /// <summary>Run moving effects the other way along the strip.</summary>
    public bool Reverse { get; init; }

    public MusicReact MusicReact { get; init; } = MusicReact.Beat;
    public MusicColors MusicColors { get; init; } = MusicColors.NewColorEveryBeat;
    public MusicStyle MusicStyle { get; init; } = MusicStyle.Pulse;

    public static ZoneLighting Default { get; } = new();

    [JsonIgnore]
    public bool IsAnimated => Mode is not (LightingMode.Static or LightingMode.Gradient or LightingMode.Off);

    [JsonIgnore]
    public RgbColor BaseColor => RgbColor.Parse(Color, RgbColor.White);

    [JsonIgnore]
    public RgbColor SecondColor => RgbColor.Parse(Color2, RgbColor.White);

    public static bool UsesSecondColor(LightingMode mode) =>
        mode is LightingMode.Gradient or LightingMode.ColorWave or LightingMode.Sparkle;

    public static bool UsesColor(LightingMode mode) =>
        mode is not (LightingMode.ColorCycle or LightingMode.Rainbow or LightingMode.Off);

    public static bool UsesSize(LightingMode mode) =>
        mode is LightingMode.Rainbow or LightingMode.ColorWave or LightingMode.Comet or LightingMode.Sparkle;

    public static bool UsesDirection(LightingMode mode) =>
        mode is LightingMode.Rainbow or LightingMode.ColorWave or LightingMode.Comet or LightingMode.Gradient;

    public static bool UsesSpeed(LightingMode mode) =>
        mode is not (LightingMode.Static or LightingMode.Gradient or LightingMode.Off or LightingMode.Music);

    public ZoneLighting Normalize() => this with
    {
        Speed = Math.Clamp(Speed, 1, 100),
        Size = Math.Clamp(Size, 1, 100),
        Color = RgbColor.Parse(Color, RgbColor.Parse(Default.Color, RgbColor.White)).ToHex(),
        Color2 = RgbColor.Parse(Color2, RgbColor.Parse(Default.Color2, RgbColor.White)).ToHex(),
        Mode = Enum.IsDefined(Mode) ? Mode : LightingMode.Static,
        MusicReact = Enum.IsDefined(MusicReact) ? MusicReact : MusicReact.Beat,
        MusicColors = Enum.IsDefined(MusicColors) ? MusicColors : MusicColors.NewColorEveryBeat,
        MusicStyle = Enum.IsDefined(MusicStyle) ? MusicStyle : MusicStyle.Pulse,
    };
}

/// <summary>
/// One clock for every device, so Breathing, Rainbow and every other animation stay in perfect step
/// across the motherboard, GPU and RAM.
/// </summary>
public static class LightingClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Seconds => Watch.Elapsed.TotalSeconds;
}

/// <summary>Everything an effect needs to color one LED.</summary>
public readonly struct EffectContext
{
    public double Time { get; init; }
    /// <summary>0..1 along the zone.</summary>
    public double Position { get; init; }
    public int Index { get; init; }
    public int Count { get; init; }
    /// <summary>Stable per-zone number, so random effects differ between zones.</summary>
    public uint Seed { get; init; }
    /// <summary>0..1, device × master brightness.</summary>
    public double Brightness { get; init; }
    public AudioFrame? Audio { get; init; }
    public IReadOnlyList<RgbColor>? Palette { get; init; }
}

public static class Effects
{
    /// <summary>Compatibility helper for simple callers.</summary>
    public static RgbColor Evaluate(ZoneLighting lighting, double seconds, double position, double brightness) =>
        Evaluate(lighting, new EffectContext { Time = seconds, Position = position, Count = 1, Brightness = brightness });

    public static RgbColor Evaluate(ZoneLighting z, in EffectContext c)
    {
        if (c.Brightness <= 0) return RgbColor.Black;
        var speed = Math.Clamp(z.Speed, 1, 100) / 100.0;
        var t = c.Time;
        var pos = Math.Clamp(c.Position, 0, 1);
        if (z.Reverse) pos = 1 - pos;
        var size = Math.Clamp(z.Size, 1, 100) / 100.0;
        var a = z.BaseColor;

        RgbColor color;
        switch (z.Mode)
        {
            case LightingMode.Off:
                return RgbColor.Black;

            case LightingMode.Breathing:
            {
                var period = Lerp(7.0, 1.2, speed);
                var k = (1 - Math.Cos(t / period * 2 * Math.PI)) / 2;
                color = a.Scale(0.02 + 0.98 * k * k);
                break;
            }

            case LightingMode.ColorCycle:
                color = RgbColor.FromHsv(t / Lerp(30.0, 2.0, speed), 1, 1);
                break;

            case LightingMode.Rainbow:
                // Size 50 = one full rainbow along the zone; smaller = more repeats, larger = wider bands.
                color = RgbColor.FromHsv(pos / Width(size) - t / Lerp(20.0, 1.5, speed), 1, 1);
                break;

            case LightingMode.Gradient:
                color = Mix(a, z.SecondColor, pos);
                break;

            case LightingMode.ColorWave:
            {
                var phase = (pos / Width(size) - t / Lerp(10.0, 1.0, speed)) * 2 * Math.PI;
                color = Mix(a, z.SecondColor, 0.5 + 0.5 * Math.Sin(phase));
                break;
            }

            case LightingMode.Comet:
            {
                var head = Frac(t / Lerp(6.0, 0.6, speed));
                if (c.Count <= 1)
                {
                    // A single LED flares as the comet passes.
                    var d = Frac(head);
                    color = a.Scale(d < 0.3 ? Math.Pow(1 - d / 0.3, 2) : 0.03);
                }
                else
                {
                    var behind = Frac(head - pos);
                    var tail = Lerp(0.08, 0.9, size);
                    color = a.Scale(behind < tail ? Math.Pow(1 - behind / tail, 2.2) : 0.03);
                }
                break;
            }

            case LightingMode.Sparkle:
            {
                var rate = Lerp(0.6, 5.0, speed);
                var cell = t * rate + Hash01(c.Seed, (uint)c.Index, 7);
                var cycle = (uint)Math.Floor(cell);
                var phase = cell - cycle;
                var on = Hash01(c.Seed, (uint)c.Index, cycle) < Lerp(0.08, 0.8, size);
                var spark = on && phase < 0.35 ? Math.Sin(Math.PI * phase / 0.35) : 0;
                color = Mix(a.Scale(0.25), z.SecondColor, spark);
                break;
            }

            case LightingMode.Candle:
            {
                var n = Noise(t * Lerp(2.0, 9.0, speed) + c.Index * 0.73 + c.Seed % 97);
                var n2 = Noise(t * Lerp(5.0, 17.0, speed) + c.Index * 1.31 + 13.7);
                var k = Math.Clamp(0.55 + 0.35 * n + 0.15 * n2, 0.25, 1);
                color = a.Scale(k);
                break;
            }

            case LightingMode.Heartbeat:
            {
                var bpm = Lerp(45, 140, speed);
                var p = Frac(t * bpm / 60.0);
                var k = Bump(p, 0.04, 0.035) + 0.65 * Bump(p, 0.22, 0.04);
                color = a.Scale(0.04 + 0.96 * Math.Min(1, k));
                break;
            }

            case LightingMode.Music:
                color = Music(z, c, pos, speed);
                break;

            default:
                color = a;
                break;
        }

        return color.Scale(c.Brightness);
    }

    // ---------------------------------------------------------------- music

    private static readonly RgbColor[] BeatHues = BuildBeatHues();

    private static RgbColor[] BuildBeatHues()
    {
        // Golden-angle steps give clearly different colors on every beat.
        var list = new RgbColor[12];
        for (var i = 0; i < list.Length; i++) list[i] = RgbColor.FromHsv(Frac(i * 0.381966), 1, 1);
        return list;
    }

    private static RgbColor Music(ZoneLighting z, in EffectContext c, double pos, double sensitivity)
    {
        var audio = c.Audio ?? AudioFrame.Silent;
        var gain = 0.55 + sensitivity * 2.2; // 0.57 .. 2.75

        // Level of the band the user chose, with a beat flash for "Beat".
        double level = z.MusicReact switch
        {
            MusicReact.Bass => audio.Bass,
            MusicReact.Vocals => audio.Mid,
            MusicReact.Treble => audio.Treble,
            MusicReact.Everything => audio.Volume,
            _ => Math.Max(audio.Bass * 0.55, audio.BeatFlash(c.Time)),
        };
        level = Math.Clamp(level * gain, 0, 1);

        RgbColor tint;
        switch (z.MusicColors)
        {
            case MusicColors.NewColorEveryBeat:
                tint = BeatHues[(int)(audio.BeatCount % (ulong)BeatHues.Length)];
                break;
            case MusicColors.AlbumArt:
                tint = c.Palette is { Count: > 0 } palette
                    ? palette[(int)(audio.BeatCount % (ulong)palette.Count)]
                    : BeatHues[(int)(audio.BeatCount % (ulong)BeatHues.Length)];
                break;
            case MusicColors.Frequency:
                // Equalizer: low notes red → high notes violet along the strip; Pulse: follows the dominant pitch.
                tint = z.MusicStyle == MusicStyle.Equalizer && c.Count > 1
                    ? RgbColor.FromHsv(pos * 0.8, 1, 1)
                    : RgbColor.FromHsv(Math.Clamp(audio.Centroid, 0, 1) * 0.8, 1, 1);
                break;
            default:
                tint = z.BaseColor;
                break;
        }

        // No music → lights fully off; they only light up with the song.
        if (!audio.HasSignal && level <= 0.01) return RgbColor.Black;
        const double floor = 0.0;
        if (z.MusicStyle == MusicStyle.Equalizer && c.Count > 1)
        {
            double fill;
            if (z.MusicColors == MusicColors.Frequency && audio.Spectrum.Length > 0)
            {
                // Each LED shows its own frequency band.
                var band = audio.Spectrum[Math.Min(audio.Spectrum.Length - 1, (int)(pos * audio.Spectrum.Length))];
                fill = Math.Clamp(band * gain, 0, 1);
                return tint.Scale(floor + (1 - floor) * fill);
            }
            // Meter: the strip fills up with the level, soft edge at the top.
            fill = level;
            var edge = Math.Clamp((fill - pos) * 8 + 0.5, 0, 1);
            return tint.Scale(floor + (1 - floor) * edge);
        }

        return tint.Scale(floor + (1 - floor) * level);
    }

    // ---------------------------------------------------------------- helpers

    public static RgbColor Mix(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbColor(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Pattern width as a fraction of the zone: size 50 → 1 (one rainbow), 1 → ¼, 100 → 3.</summary>
    private static double Width(double size) => size <= 0.5 ? Lerp(0.25, 1, size * 2) : Lerp(1, 3, (size - 0.5) * 2);
    private static double Frac(double v) => v - Math.Floor(v);
    private static double Bump(double x, double center, double width) => Math.Exp(-Math.Pow((x - center) / width, 2));

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352D;
        x ^= x >> 15; x *= 0x846CA68B;
        x ^= x >> 16;
        return x;
    }

    private static double Hash01(uint a, uint b, uint c) => Hash(a * 0x9E3779B1 ^ Hash(b * 0x85EBCA77 ^ Hash(c))) / (double)uint.MaxValue;

    /// <summary>Smooth value noise in -1..1.</summary>
    private static double Noise(double x)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        var a = Hash01((uint)(long)i, 0, 1) * 2 - 1;
        var b = Hash01((uint)(long)(i + 1), 0, 1) * 2 - 1;
        return a + (b - a) * u;
    }
}
