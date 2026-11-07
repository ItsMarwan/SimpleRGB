using SimpleRGB.Core.Audio;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Media;
using Xunit.Abstractions;

namespace SimpleRGB.Tests;

public class EffectSyncTests
{
    [Theory]
    [InlineData(LightingMode.Breathing)]
    [InlineData(LightingMode.Rainbow)]
    [InlineData(LightingMode.ColorCycle)]
    [InlineData(LightingMode.ColorWave)]
    [InlineData(LightingMode.Heartbeat)]
    public void DevicesRenderingTheSameMoment_ShowTheSameColor(LightingMode mode)
    {
        // Two devices (different zone ids / seeds) asked at the same shared-clock time agree exactly.
        var snapshot = new LightingSnapshot { Sync = new ZoneLighting { Mode = mode, Color = "#FF4400" } };
        var t = 12.345;
        var motherboard = new FrameColors(snapshot, t);
        var gpu = new FrameColors(snapshot, t);
        // Single-LED zones on different devices: identical.
        Assert.Equal(motherboard.SubdeviceLed("12V Header 1", 0, 1), gpu.DeviceLed(0, 1));
        // The same LED of equally long strips on different devices: identical.
        Assert.Equal(motherboard.ChannelLed("Channel 1", 5, 30), gpu.ChannelLed("Channel 2", 5, 30));
    }

    [Fact]
    public void LedOrder_NotGridCoordinates_PlacesSingleLedZonesAtTheStart()
    {
        // A 12V header: one LED at (1,1) on a 3x3 grid must count as the first (and only) LED.
        Assert.Equal((0, 1), SimpleRGB.Core.Plugins.DeviceModel.IndexOf(new() { (1, 1) }, 1, 1, 3, 3));
        // GPU fan LEDs scattered on a grid are numbered in the plugin's order.
        var fan = new List<(int, int)> { (0, 5), (1, 4), (2, 3), (3, 4) };
        Assert.Equal((2, 4), SimpleRGB.Core.Plugins.DeviceModel.IndexOf(fan, 2, 3, 15, 8));
    }

    [Fact]
    public void ReverseAndSize_ChangeTheRainbow()
    {
        var baseZ = new ZoneLighting { Mode = LightingMode.Rainbow };
        RgbColor At(ZoneLighting z, double pos) => Effects.Evaluate(z, new EffectContext { Time = 2, Position = pos, Count = 30, Brightness = 1 });
        Assert.Equal(At(baseZ, 0.2), At(baseZ with { Reverse = true }, 0.8));
        Assert.NotEqual(At(baseZ, 0.3), At(baseZ with { Size = 10 }, 0.3));
        // Size 50 is the original look.
        Assert.Equal(At(baseZ, 0.3), At(baseZ with { Size = 50 }, 0.3));
    }

    [Fact]
    public void SharedClock_IsMonotonic()
    {
        var a = LightingClock.Seconds;
        Thread.Sleep(5);
        Assert.True(LightingClock.Seconds > a);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void EveryEffect_ProducesColorsForAnyInput(LightingMode mode)
    {
        var z = new ZoneLighting { Mode = mode, Color = "#3366FF", Color2 = "#FFAA00", Speed = 70 };
        var frame = new AudioFrame { Bass = 0.8, Mid = 0.4, Treble = 0.2, Volume = 0.6, BeatCount = 3, LastBeatTime = 0.95, Spectrum = new float[16] };
        for (var t = 0.0; t < 20; t += 0.37)
            for (var i = 0; i < 40; i += 7)
            {
                var c = new EffectContext { Time = t, Position = i / 39.0, Index = i, Count = 40, Seed = 1234, Brightness = 1, Audio = frame };
                _ = Effects.Evaluate(z, c); // must not throw
                Assert.Equal(RgbColor.Black, Effects.Evaluate(z, c with { Brightness = 0 }));
            }
    }

    public static IEnumerable<object[]> AllModes() => Enum.GetValues<LightingMode>().Select(m => new object[] { m });

    [Theory]
    [InlineData(LightingMode.Breathing)] [InlineData(LightingMode.Comet)] [InlineData(LightingMode.Sparkle)]
    [InlineData(LightingMode.Candle)] [InlineData(LightingMode.Heartbeat)] [InlineData(LightingMode.ColorWave)]
    public void AnimatedEffects_ChangeOverTime(LightingMode mode)
    {
        var z = new ZoneLighting { Mode = mode, Color = "#FF0000", Color2 = "#0000FF" };
        var colors = Enumerable.Range(0, 60)
            .Select(i => Effects.Evaluate(z, new EffectContext { Time = i * 0.1, Position = 0.3, Index = 3, Count = 10, Seed = 9, Brightness = 1 }))
            .Distinct().Count();
        Assert.True(colors > 3, $"{mode} looked static ({colors} distinct colors)");
    }

    [Fact]
    public void Gradient_RunsFromColor1ToColor2()
    {
        var z = new ZoneLighting { Mode = LightingMode.Gradient, Color = "#FF0000", Color2 = "#0000FF" };
        Assert.Equal(new RgbColor(255, 0, 0), Effects.Evaluate(z, new EffectContext { Position = 0, Count = 10, Brightness = 1 }));
        Assert.Equal(new RgbColor(0, 0, 255), Effects.Evaluate(z, new EffectContext { Position = 1, Count = 10, Brightness = 1 }));
    }

    [Fact]
    public void Music_NewColorEveryBeat_ChangesWithBeatCount()
    {
        var z = new ZoneLighting { Mode = LightingMode.Music, MusicColors = MusicColors.NewColorEveryBeat, MusicReact = MusicReact.Bass };
        RgbColor At(ulong beats) => Effects.Evaluate(z, new EffectContext
        {
            Time = 5, Count = 1, Brightness = 1,
            Audio = new AudioFrame { Bass = 1, BeatCount = beats, Spectrum = new float[16] },
        });
        Assert.NotEqual(At(1), At(2));
        Assert.Equal(At(5), At(5));
    }

    [Fact]
    public void Music_SameColor_KeepsTheChosenColorAndFollowsTheLevel()
    {
        var z = new ZoneLighting { Mode = LightingMode.Music, MusicColors = MusicColors.SameColor, MusicReact = MusicReact.Treble, Color = "#00FF00", Speed = 50 };
        RgbColor At(double treble) => Effects.Evaluate(z, new EffectContext
        {
            Count = 1, Brightness = 1, Audio = new AudioFrame { Treble = treble, Spectrum = new float[16] },
        });
        var loud = At(1);
        var quiet = At(0);
        Assert.Equal(0, loud.R);
        Assert.Equal(0, loud.B);
        Assert.True(loud.G > quiet.G);
        Assert.Equal(RgbColor.Black, quiet); // silence = lights off
    }

    [Fact]
    public void Music_AlbumArt_UsesThePalette()
    {
        var palette = new[] { new RgbColor(255, 0, 0), new RgbColor(0, 0, 255) };
        var z = new ZoneLighting { Mode = LightingMode.Music, MusicColors = MusicColors.AlbumArt, MusicReact = MusicReact.Everything, Speed = 100 };
        RgbColor At(ulong beats) => Effects.Evaluate(z, new EffectContext
        {
            Count = 1, Brightness = 1, Palette = palette,
            Audio = new AudioFrame { Volume = 1, BeatCount = beats, Spectrum = new float[16] },
        });
        Assert.Equal(new RgbColor(255, 0, 0), At(0));
        Assert.Equal(new RgbColor(0, 0, 255), At(1));
    }

    [Fact]
    public void Music_Equalizer_FillsTheStripWithTheLevel()
    {
        var z = new ZoneLighting { Mode = LightingMode.Music, MusicStyle = MusicStyle.Equalizer, MusicColors = MusicColors.SameColor, MusicReact = MusicReact.Everything, Color = "#FFFFFF", Speed = 22 };
        var audio = new AudioFrame { Volume = 0.5, Spectrum = new float[16] };
        var bottom = Effects.Evaluate(z, new EffectContext { Position = 0.05, Count = 40, Brightness = 1, Audio = audio });
        var top = Effects.Evaluate(z, new EffectContext { Position = 0.95, Count = 40, Brightness = 1, Audio = audio });
        Assert.True(bottom.R > 200);
        Assert.True(top.R < 40);
    }
}

public class AudioAnalyzerTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static (AudioAnalyzer Analyzer, Func<double> Time) Create()
    {
        var analyzer = new AudioAnalyzer();
        double samples = 0;
        analyzer.Clock = () => samples / Rate;
        return (analyzer, () => samples / Rate);
    }

    /// <summary>Synthetic kick drum (decaying 55 Hz thump) every 0.5 s = 120 BPM, over quiet pad noise.</summary>
    [Fact]
    public void DetectsBeatsOfA120BpmKick()
    {
        var analyzer = new AudioAnalyzer();
        long n = 0;
        analyzer.Clock = () => n / (double)Rate;
        var rng = new Random(1);
        var buffer = new float[480];
        double maxBass = 0;
        for (var block = 0; block < Rate * 8 / buffer.Length; block++)
        {
            for (var i = 0; i < buffer.Length; i++, n++)
            {
                var t = n / (double)Rate;
                var sinceKick = t % 0.5;
                var kick = sinceKick < 0.15 ? Math.Sin(2 * Math.PI * 55 * sinceKick) * Math.Exp(-sinceKick * 25) : 0;
                var pad = (rng.NextDouble() - 0.5) * 0.02;
                buffer[i] = (float)(kick * 0.8 + pad);
            }
            analyzer.Feed(buffer, Rate);
            maxBass = Math.Max(maxBass, analyzer.Current.Bass);
        }

        var beats = analyzer.Current.BeatCount;
        output.WriteLine($"beats detected in 8 s: {beats} (expected ~16), max bass {maxBass:F2}");
        Assert.InRange((int)beats, 13, 17);
        Assert.True(maxBass > 0.6);
    }

    [Fact]
    public void HiHatsRaiseTrebleNotBass()
    {
        var analyzer = new AudioAnalyzer();
        long n = 0;
        analyzer.Clock = () => n / (double)Rate;
        var buffer = new float[480];
        double treble = 0, bass = 0;
        for (var block = 0; block < Rate * 3 / buffer.Length; block++)
        {
            for (var i = 0; i < buffer.Length; i++, n++)
            {
                var t = n / (double)Rate;
                buffer[i] = (float)(Math.Sin(2 * Math.PI * 9000 * t) * 0.3 + Math.Sin(2 * Math.PI * 70 * t) * 0.002);
            }
            analyzer.Feed(buffer, Rate);
            treble = analyzer.Current.Treble;
            bass = analyzer.Current.Bass;
        }
        output.WriteLine($"treble {treble:F2}, centroid {analyzer.Current.Centroid:F2}");
        Assert.True(analyzer.Current.Centroid > 0.6);
        Assert.True(treble > 0.5);
    }

    [Fact]
    public void Silence_FadesEverythingOut()
    {
        var analyzer = new AudioAnalyzer();
        var loud = Enumerable.Range(0, 4800).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
        for (var i = 0; i < 10; i++) analyzer.Feed(loud, Rate);
        analyzer.FeedSilence(Rate);
        Assert.True(analyzer.Current.Volume < 0.01);
        Assert.False(analyzer.Current.HasSignal);
    }
}

public class AlbumPaletteTests
{
    private static byte[] Image(params RgbColor[] stripes)
    {
        var px = new byte[48 * 48 * 4];
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 48; x++)
            {
                var c = stripes[x * stripes.Length / 48];
                var o = (y * 48 + x) * 4;
                px[o] = c.B; px[o + 1] = c.G; px[o + 2] = c.R; px[o + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void FindsTheMainColors()
    {
        var palette = AlbumPalette.Extract(Image(new RgbColor(220, 20, 30), new RgbColor(20, 40, 200)));
        Assert.Equal(2, palette.Count);
        Assert.Contains(palette, c => c.R > 200 && c.B < 120);
        Assert.Contains(palette, c => c.B > 200 && c.R < 120);
    }

    [Fact]
    public void OneColorCover_GetsNeighbourShadesForBeatChanges()
    {
        var palette = AlbumPalette.Extract(Image(new RgbColor(60, 30, 255)));
        Assert.Equal(3, palette.Count);
        Assert.Equal(3, palette.Distinct().Count());
    }

    [Fact]
    public void BlackAndWhiteCover_GivesWhiteLight()
    {
        Assert.Equal(new[] { RgbColor.White }, AlbumPalette.Extract(Image(new RgbColor(10, 10, 10), new RgbColor(128, 128, 128), new RgbColor(250, 250, 250))));
    }

    [Fact]
    public void BigBackgroundColor_BeatsASmallNeonDetail()
    {
        // 90% deep teal background with a small hot-pink logo: teal must come first.
        var px = new byte[64 * 64 * 4];
        for (var i = 0; i < 64 * 64; i++)
        {
            var logo = i % 64 < 8 && i / 64 < 48; // ~9% of the area
            var c = logo ? new RgbColor(255, 20, 200) : new RgbColor(10, 90, 100);
            px[i * 4] = c.B; px[i * 4 + 1] = c.G; px[i * 4 + 2] = c.R; px[i * 4 + 3] = 255;
        }
        var palette = AlbumPalette.Extract(px);
        var (h, _, _) = palette[0].ToHsv();
        Assert.InRange(h, 0.45, 0.55); // teal/cyan hue
        Assert.Contains(palette, c => c.R > 200 && c.B > 150 && c.G < 120); // the pink is still available for beats
    }

    [Fact]
    public void MutedColors_StayMuted()
    {
        // A dusty pink cover must not turn into hot pink.
        var palette = AlbumPalette.Extract(Image(new RgbColor(200, 150, 160)));
        var (_, s, _) = palette[0].ToHsv();
        Assert.True(s < 0.5, $"saturation {s:F2}");
    }
}
