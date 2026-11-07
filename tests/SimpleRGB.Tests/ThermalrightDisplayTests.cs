using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;
using SimpleRGB.Core.Sensors;

namespace SimpleRGB.Tests;

[Collection("Sensors")] // replaces the process-wide sensor source
public class ThermalrightDisplayTests : IDisposable
{
    private readonly ISensorSource _original = SensorService.Shared;
    public void Dispose() => SensorService.Shared = _original;

    private sealed class FixedSensors(SensorReading reading) : ISensorSource
    {
        public SensorReading Current { get; set; } = reading;
    }

    private static readonly string[] Wiring =
    {
        "Cpu2", "Cpu1", "F1", "A1", "B1", "G1", "E1", "D1", "C1", "F2", "A2", "B2", "G2", "E2", "D2", "C2",
        "F3", "A3", "B3", "G3", "E3", "D3", "C3", "SSD", "HSD", "C11", "B11", "F4", "A4", "B4", "G4", "E4", "D4", "C4",
        "F5", "A5", "B5", "G5", "E5", "D5", "C5", "BFB", "BFB1", "C10", "D10", "E10", "G10", "B10", "A10", "F10",
        "C9", "D9", "E9", "G9", "B9", "A9", "F9", "B12", "C12", "SSD1", "HSD1", "C8", "D8", "E8", "G8", "B8", "A8", "F8",
        "C7", "D7", "E7", "G7", "B7", "A7", "F7", "C6", "D6", "E6", "G6", "B6", "A6", "F6", "Gpu1", "Gpu2",
    };

    private static (PluginRuntime Runtime, FakeHid Hid) Load(byte model)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Plugins", "Thermalright", "Thermalright_Digital_Display.js");
        Assert.True(File.Exists(file), "built-in plugin not copied to output");
        var info = new PluginInfo
        {
            FilePath = file, RelativePath = "Thermalright/Thermalright_Digital_Display.js",
            RootDir = Path.Combine(AppContext.BaseDirectory, "Plugins"), Source = "SimpleRGB", Kind = PluginKind.Hid,
        };
        var ep = FakeHid.MakeEndpoint(0x0416, 0x8001, -1, usagePage: 0xFF00, usage: 0x01);
        var device = new HidPhysicalDevice("USB\\VID_0416&PID_8001\\X", 0x0416, 0x8001, "HID Transfer", "Nuvoton", "", new[] { ep });
        FakeHid? fake = null;
        var backend = new HidBackend(device, e => fake = new FakeHid(e)
        {
            // Same reply the real cooler gave: DA DB DC DD 10 10 10 10 …
            Responder = w => w.Length > 4 && w[1] == 0xDA && w[13] == 0x01
                ? new byte[] { 0xDA, 0xDB, 0xDC, 0xDD, model, model, model, model }.Concat(new byte[56]).ToArray()
                : null,
        });
        var runtime = PluginRuntime.LoadForHid(info, backend);
        runtime.LoadParameters();
        Assert.True(runtime.Validate(ep));
        backend.Open(ep);
        return (runtime, fake!);
    }

    /// <summary>Reassembles the 5 HID reports of one frame and maps colors back to LED names.</summary>
    private static Dictionary<string, (byte R, byte G, byte B)> Decode(List<byte[]> writes)
    {
        Assert.Equal(5, writes.Count);
        Assert.All(writes, w => { Assert.Equal(65, w.Length); Assert.Equal(0, w[0]); });
        var stream = writes.SelectMany(w => w.Skip(1)).ToArray();
        Assert.Equal(new byte[] { 0xDA, 0xDB, 0xDC, 0xDD }, stream[..4]);
        Assert.Equal(0x02, stream[12]);
        Assert.Equal(252, stream[16]);
        var result = new Dictionary<string, (byte, byte, byte)>();
        for (var i = 0; i < 84; i++)
            result[Wiring[i]] = (stream[20 + i * 3], stream[21 + i * 3], stream[22 + i * 3]);
        return result;
    }

    private static string Lit(Dictionary<string, (byte R, byte G, byte B)> leds, int digit) =>
        string.Concat("ABCDEFG".Where(s => leds[$"{s}{digit}"].R > 0));

    [Fact]
    public void ShowsCpuAndGpuReadingsLikeTrcc()
    {
        SensorService.Shared = new FixedSensors(new SensorReading(CpuTemp: 47, CpuLoad: 100, GpuTemp: null, GpuLoad: 5));
        var (runtime, hid) = Load(0x10);
        using (runtime)
        {
            runtime.Initialize(new SolidFrame(RgbColor.White));
            Assert.Equal("Thermalright PA120 Digital Display", runtime.TakeStructure().Name);
            hid.Writes.Clear();
            runtime.Render(new SolidFrame(RgbColor.White));
            var leds = Decode(hid.Writes);

            // CPU 47 °C: blank, 4, 7.
            Assert.Equal("", Lit(leds, 1));
            Assert.Equal("BCFG", Lit(leds, 2));
            Assert.Equal("ABC", Lit(leds, 3));
            // CPU 100 %: leading "1" + 0 0.
            Assert.True(leds["B11"].R > 0 && leds["C11"].R > 0);
            Assert.Equal("ABCDEF", Lit(leds, 4));
            Assert.Equal("ABCDEF", Lit(leds, 5));
            // GPU temperature unknown: dashes, never a made-up number.
            Assert.Equal("G", Lit(leds, 6));
            Assert.Equal("G", Lit(leds, 7));
            Assert.Equal("G", Lit(leds, 8));
            // GPU 5 %: blank, 5.
            Assert.Equal("", Lit(leds, 9));
            Assert.Equal("ACDFG", Lit(leds, 10));
            Assert.True(leds["B12"].R == 0 && leds["C12"].R == 0);
            // Labels and units: CPU/GPU, °C on, °F off, % on.
            foreach (var on in new[] { "Cpu1", "Cpu2", "Gpu1", "Gpu2", "SSD", "SSD1", "BFB", "BFB1" }) Assert.True(leds[on].R > 0, on);
            Assert.True(leds["HSD"].R == 0 && leds["HSD1"].R == 0);
            // Brightness never exceeds TRCC's 40% cap (255 × 0.4 = 102).
            Assert.All(leds.Values, c => Assert.True(c.R <= 102 && c.G <= 102 && c.B <= 102));
            Assert.Equal(102, leds["A3"].R);
        }
    }

    [Fact]
    public void Fahrenheit_ConvertsAndSwitchesTheUnitLed()
    {
        SensorService.Shared = new FixedSensors(new SensorReading(50, 3, 60, 7));
        var (runtime, hid) = Load(0x10);
        using (runtime)
        {
            runtime.Initialize(new SolidFrame(RgbColor.White));
            runtime.SetParameter("TempUnit", "Fahrenheit");
            hid.Writes.Clear();
            runtime.Render(new SolidFrame(RgbColor.White));
            var leds = Decode(hid.Writes);
            // 50 °C = 122 °F.
            Assert.Equal("BC", Lit(leds, 1));
            Assert.Equal("ABDEG", Lit(leds, 2));
            Assert.Equal("ABDEG", Lit(leds, 3));
            Assert.True(leds["HSD"].R > 0 && leds["SSD"].R == 0);
        }
    }

    [Fact]
    public void TemperatureColors_GoGreenToRed_AndCpuOnlyBlanksTheGpuRow()
    {
        SensorService.Shared = new FixedSensors(new SensorReading(CpuTemp: 90, CpuLoad: 50, GpuTemp: 40, GpuLoad: 20));
        var (runtime, hid) = Load(0x10);
        using (runtime)
        {
            runtime.Initialize(new SolidFrame(RgbColor.White));
            runtime.SetParameter("DigitColors", "By temperature");
            hid.Writes.Clear();
            runtime.Render(new SolidFrame(RgbColor.White));
            var leds = Decode(hid.Writes);
            // CPU 90 °C (above "hot" 80) → red; GPU 40 °C (below "cool" 50) → green. 40% cap applies.
            Assert.Equal((102, 0, 0), (leds["B3"].R, leds["B3"].G, leds["B3"].B));
            Assert.Equal((0, 102, 0), (leds["B8"].R, leds["B8"].G, leds["B8"].B));

            runtime.SetParameter("ShowRows", "CPU only");
            hid.Writes.Clear();
            runtime.Render(new SolidFrame(RgbColor.White));
            leds = Decode(hid.Writes);
            Assert.All(new[] { "Gpu1", "Gpu2", "BFB1", "SSD1", "A8", "B8", "C8", "B10" }, n => Assert.Equal(0, leds[n].R + leds[n].G + leds[n].B));
            Assert.True(leds["Cpu1"].R > 0);
        }
    }

    [Fact]
    public void UnknownModel_IsLeftUntouched()
    {
        SensorService.Shared = new FixedSensors(new SensorReading(40, 10, 40, 10));
        var (runtime, hid) = Load(0x30); // some other Thermalright layout
        using (runtime)
        {
            runtime.Initialize(new SolidFrame(RgbColor.White));
            var afterIdentify = hid.Writes.Count;
            runtime.Render(new SolidFrame(RgbColor.White));
            runtime.Shutdown(new SolidFrame(RgbColor.Black), false);
            Assert.Equal(afterIdentify, hid.Writes.Count); // only the identify query was ever sent
            Assert.Single(runtime.TakeStructure().Messages);
        }
    }

    [Fact]
    public void Shutdown_BlanksTheDisplay()
    {
        SensorService.Shared = new FixedSensors(new SensorReading(40, 10, 40, 10));
        var (runtime, hid) = Load(0x10);
        using (runtime)
        {
            runtime.Initialize(new SolidFrame(RgbColor.White));
            Assert.True(runtime.AlwaysShutdown);
            hid.Writes.Clear();
            runtime.Shutdown(new SolidFrame(RgbColor.White), false);
            Assert.All(Decode(hid.Writes).Values, c => Assert.True(c.R == 0 && c.G == 0 && c.B == 0));
        }
    }
}
