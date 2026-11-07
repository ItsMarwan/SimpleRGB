using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Settings;

namespace SimpleRGB.Tests;

public class SafetyGuardTests
{
    [Theory]
    [InlineData(0x50)] [InlineData(0x57)] [InlineData(0x30)] [InlineData(0x36)] [InlineData(0x37)]
    public void ProtectedAddresses_NeverReachTheBus(int address)
    {
        var fake = new FakeEneRamBus(0);
        var guard = new SmBusSafetyGuard(fake, protectDdr5Pmic: true);
        var a = (byte)address;

        Assert.Equal(0, guard.WriteQuick(a)); // reported as "occupied" so nothing remaps onto it
        Assert.Equal(-1, guard.WriteByteData(a, 0, 0));
        Assert.Equal(-1, guard.WriteWordData(a, 0, 0));
        Assert.Equal(-1, guard.WriteBlockData(a, 0, new byte[] { 1 }));
        Assert.Equal(-1, guard.WriteI2CBlockData(a, 0, new byte[] { 1 }));
        Assert.Equal(-1, guard.RawWrite(a, new byte[] { 1, 2 }));
        Assert.Equal(-1, guard.WriteByte(a, 1));
        Assert.Equal(-1, guard.ReadByteData(a, 0));
        Assert.Empty(fake.Log);
    }

    [Fact]
    public void PmicRange_IsOnlyLockedWhenRequested()
    {
        var fake = new FakeEneRamBus(0);
        Assert.True(new SmBusSafetyGuard(fake, protectDdr5Pmic: true).IsProtected(0x4F));
        Assert.False(new SmBusSafetyGuard(fake, protectDdr5Pmic: false).IsProtected(0x4F));
        Assert.False(new SmBusSafetyGuard(fake, protectDdr5Pmic: true).IsProtected(0x77));
    }

    [Fact]
    public void RgbAddresses_PassThrough()
    {
        var fake = new FakeEneRamBus(1);
        var guard = new SmBusSafetyGuard(fake, protectDdr5Pmic: true);
        Assert.Equal(0, guard.WriteQuick(0x77));
        Assert.Single(fake.Log);
    }
}

public class EffectTests
{
    [Fact]
    public void Static_AppliesBrightness()
    {
        var c = Effects.Evaluate(new ZoneLighting { Color = "#FF8000" }, 0, 0, 0.5);
        Assert.Equal(new RgbColor(128, 64, 0), c);
    }

    [Fact]
    public void Off_And_ZeroBrightness_AreBlack()
    {
        Assert.Equal(RgbColor.Black, Effects.Evaluate(new ZoneLighting { Mode = LightingMode.Off, Color = "#FFFFFF" }, 3, 0, 1));
        Assert.Equal(RgbColor.Black, Effects.Evaluate(new ZoneLighting { Color = "#FFFFFF" }, 3, 0, 0));
    }

    [Fact]
    public void Breathing_StaysWithinBaseColor()
    {
        for (double t = 0; t < 10; t += 0.13)
        {
            var c = Effects.Evaluate(new ZoneLighting { Mode = LightingMode.Breathing, Color = "#40FF20" }, t, 0, 1);
            Assert.True(c.R <= 0x40 && c.G <= 0xFF && c.B <= 0x20);
        }
    }

    [Fact]
    public void Rainbow_VariesAlongTheZone()
    {
        var z = new ZoneLighting { Mode = LightingMode.Rainbow };
        Assert.NotEqual(Effects.Evaluate(z, 1, 0, 1), Effects.Evaluate(z, 1, 0.5, 1));
    }

    [Theory]
    [InlineData("#00a2ff", 0x00, 0xA2, 0xFF)]
    [InlineData("fff", 255, 255, 255)]
    [InlineData(" #102030 ", 0x10, 0x20, 0x30)]
    public void ColorParsing(string text, int r, int g, int b)
    {
        Assert.True(RgbColor.TryParse(text, out var c));
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData("")] [InlineData("#12345")] [InlineData("zzzzzz")] [InlineData(null)]
    public void InvalidColors_AreRejected(string? text) => Assert.False(RgbColor.TryParse(text, out _));

    [Fact]
    public void HsvRoundTrip()
    {
        var c = new RgbColor(12, 200, 99);
        var (h, s, v) = c.ToHsv();
        Assert.Equal(c, RgbColor.FromHsv(h, s, v));
    }

    [Fact]
    public void Snapshot_SyncOverridesZones()
    {
        var snap = new LightingSnapshot
        {
            Zones = new Dictionary<string, ZoneLighting> { ["a"] = new() { Color = "#FF0000" } },
            Sync = new ZoneLighting { Color = "#00FF00" },
        };
        Assert.Equal("#00FF00", snap.ForZone("a").Color);
        Assert.Equal("#00FF00", snap.ForZone("unknown").Color);
    }
}

public class SyncExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simplergb-sync-" + Guid.NewGuid().ToString("N"));
    public SyncExclusionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ExcludedDevice_KeepsItsOwnLightingWhileSyncIsOn()
    {
        var store = new SettingsStore(Path.Combine(_dir, "s.json"));
        store.Current.SyncAll = true;
        store.Current.SyncLighting = new ZoneLighting { Color = "#FF0000" };
        store.Current.ForDevice("lcd").ExcludeFromSync = true;
        store.Current.ForDevice("lcd").Zones["device"] = new ZoneLighting { Color = "#00FF00" };
        using var manager = new DeviceManager(store, new SimpleRGB.Core.Plugins.PluginLibrary(Array.Empty<(string, string)>()));

        Assert.Equal("#00FF00", manager.BuildLighting("lcd").ForZone("device").Color);
        Assert.Equal("#FF0000", manager.BuildLighting("motherboard").ForZone("device").Color);
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simplergb-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        store.Current.MasterBrightness = 42;
        store.Current.ForDevice("dev").Zones["ch:Channel 1"] = new ZoneLighting { Mode = LightingMode.Rainbow, Color = "#123456", Speed = 77 };
        store.SaveNow();

        var loaded = new SettingsStore(path).Current;
        Assert.Equal(42, loaded.MasterBrightness);
        var z = loaded.Devices["dev"].Zones["ch:Channel 1"];
        Assert.Equal(LightingMode.Rainbow, z.Mode);
        Assert.Equal("#123456", z.Color);
        Assert.Equal(77, z.Speed);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsAndKeepsBackup()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        Assert.Equal(100, store.Current.MasterBrightness);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "MasterBrightness": 900, "SyncLighting": { "Color": "nope", "Speed": -5 } }""");
        var s = new SettingsStore(path).Current;
        Assert.Equal(100, s.MasterBrightness);
        Assert.Equal(1, s.SyncLighting.Speed);
        Assert.True(RgbColor.TryParse(s.SyncLighting.Color, out _));
    }
}
