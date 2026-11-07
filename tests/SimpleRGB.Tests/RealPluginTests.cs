using SimpleRGB.Core;
using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;
using Xunit.Abstractions;

namespace SimpleRGB.Tests;

/// <summary>
/// Runs the real SignalRGB plugins for this PC's hardware against simulated devices, so every byte they
/// would send is checked before touching real hardware. Plugins come from the local import folder
/// (they are not part of this repository); tests are skipped when a plugin is missing.
/// </summary>
public class RealPluginTests(ITestOutputHelper output)
{
    private static PluginInfo? FindPlugin(string relativePath)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleRGB", "Plugins", "SignalRGB");
        var file = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file)) return null;
        return new PluginInfo { FilePath = file, RelativePath = relativePath, RootDir = root, Source = "SignalRGB", Kind = PluginKind.Hid };
    }

    // Config table and firmware string captured from SignalRGB's log on the real TUF GAMING B760M-PLUS WIFI D4.
    private static readonly byte[] AsusConfigTable =
    {
        236, 48, 0, 0, 30, 159, 3, 1, 0, 0, 120, 60, 0, 1, 0, 0, 120, 60, 0, 1, 0, 0, 120, 60,
        0, 0, 0, 0, 0, 0, 0, 1, 4, 2, 1, 244, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    private static byte[] AsusFirmwareReply()
    {
        var reply = new byte[65];
        reply[0] = 0xEC;
        reply[1] = 0x02;
        System.Text.Encoding.ASCII.GetBytes("AULA3-AR32-0214").CopyTo(reply, 2);
        return reply;
    }

    [Fact]
    public void AsusAuraMotherboard_DetectsHeadersAndSendsCorrectPackets()
    {
        var plugin = FindPlugin("Asus/Motherboard Controllers/Asus_Motherboard_Controller.js");
        if (plugin == null) { output.WriteLine("Plugin not imported; skipped."); return; }

        var eps = new[] { FakeHid.MakeEndpoint(0x0B05, 0x19AF, 2) };
        var device = new HidPhysicalDevice("USB\\FAKE", 0x0B05, 0x19AF, "AURA LED Controller", "AsusTek", "", eps);
        FakeHid? fake = null;
        var backend = new HidBackend(device, ep => fake = new FakeHid(ep)
        {
            Responder = w => (w[0], w[1]) switch
            {
                (0xEC, 0x82) => AsusFirmwareReply(),
                (0xEC, 0xB0) => AsusConfigTable,
                _ => null,
            },
        });

        using var runtime = PluginRuntime.LoadForHid(plugin, backend);
        runtime.LoadParameters();
        Assert.True(runtime.Validate(eps[0]));
        Assert.NotNull(backend.Open(eps[0]));

        runtime.Initialize(new SolidFrame(RgbColor.Black));
        var zones = runtime.TakeStructure().Zones;
        foreach (var z in zones) output.WriteLine($"{z.Id} {z.Name} {z.LedCount}");

        // Same layout SignalRGB reports for this board: 2x 12V RGB headers + 3 ARGB channels.
        Assert.Equal(new[] { "sub:12V Header 1", "sub:12V Header 2", "ch:Channel 1", "ch:Channel 2", "ch:Channel 3" }, zones.Select(z => z.Id));

        // Direct mode for mainboard (0) and the 3 ARGB channels: EC 35 <ch> 00 00 FF.
        for (byte ch = 0; ch <= 3; ch++)
            Assert.Contains(fake!.Writes, w => w.Length == 65 && w[0] == 0xEC && w[1] == 0x35 && w[2] == ch && w[5] == 0xFF);

        fake!.Writes.Clear();
        runtime.Render(new SolidFrame(new RgbColor(255, 0, 0)));

        // Every packet is a 65-byte direct-color packet (EC 40 …), nothing else is sent while rendering.
        Assert.All(fake.Writes, w => { Assert.Equal(65, w.Length); Assert.Equal(0xEC, w[0]); Assert.Equal(0x40, w[1]); });

        // 12V headers travel on direct index 4 with the apply bit: EC 40 84 00 02 FF 00 00 FF 00 00.
        Assert.Contains(fake.Writes, w => w[2] == 0x84 && w[3] == 0 && w[4] == 2 &&
                                          w.AsSpan(5, 6).SequenceEqual(new byte[] { 255, 0, 0, 255, 0, 0 }));

        // Each ARGB channel: 120 LEDs in 6 packets of 20, the last one with the apply bit.
        for (byte ch = 0; ch < 3; ch++)
        {
            var packets = fake.Writes.Where(w => (w[2] & 0x7F) == ch).ToList();
            Assert.Equal(6, packets.Count);
            Assert.Equal(0x80 | ch, packets[^1][2]);
            Assert.All(packets, p => Assert.Equal(20, p[4]));
            Assert.All(packets, p => Assert.Equal(255, p[5]));
        }
    }

    [Fact]
    public void EneRam_RemapsSticksAndNeverWritesOutsideTheRgbChips()
    {
        var plugin = FindPlugin("Asus/ENE_RAM.js");
        if (plugin == null) { output.WriteLine("Plugin not imported; skipped."); return; }
        plugin = plugin with { Kind = PluginKind.SmBus };

        var fake = new FakeEneRamBus(stickCount: 4);
        var guarded = new SmBusSafetyGuard(fake, protectDdr5Pmic: false);

        List<int> addresses;
        using (var scanner = PluginRuntime.LoadForMetadata(plugin))
            addresses = scanner.Scan(guarded);
        output.WriteLine("Scan: " + string.Join(", ", addresses.Select(a => $"0x{a:X2}")));

        // SignalRGB found these exact addresses (57–60) on the real PC.
        Assert.Equal(new[] { 0x39, 0x3A, 0x3B, 0x3C }, addresses);

        foreach (var address in addresses)
        {
            using var runtime = PluginRuntime.LoadForSmBus(plugin, guarded, (byte)address);
            runtime.LoadParameters();
            runtime.Initialize(new SolidFrame(RgbColor.Black));
            runtime.Render(new SolidFrame(new RgbColor(255, 0, 0)));

            var zones = runtime.TakeStructure().Zones;
            Assert.Single(zones);
            Assert.Equal(8, zones[0].LedCount);

            var stick = fake.Sticks.Single(s => s.Address == address);
            Assert.Equal(1, stick.Memory[0x8020]); // direct mode
            // ENE V2 color block at 0x8100, in R,B,G order: red → FF 00 00 for each LED.
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(255, stick.Memory[0x8100 + i * 3]);
                Assert.Equal(0, stick.Memory[0x8100 + i * 3 + 1]);
                Assert.Equal(0, stick.Memory[0x8100 + i * 3 + 2]);
            }
        }

        var written = fake.WrittenAddresses.OrderBy(a => a).ToList();
        output.WriteLine("Addresses written: " + string.Join(", ", written.Select(a => $"0x{a:X2}")));
        Assert.All(written, a => Assert.Contains(a, new byte[] { 0x77, 0x39, 0x3A, 0x3B, 0x3C }));
        // SPD EEPROM / write-protect / page-select chips are never even addressed.
        Assert.DoesNotContain(fake.TouchedAddresses, a => a is >= 0x50 and <= 0x57 or >= 0x30 and <= 0x37);
    }

    [Fact]
    public void GigabyteGpu_UsesOnlyAddress0x71WithEightByteWrites()
    {
        var plugin = FindPlugin("GigaByte/Gigabyte_Gen2_GPU.js");
        if (plugin == null) { output.WriteLine("Plugin not imported; skipped."); return; }
        plugin = plugin with { Kind = PluginKind.SmBus };

        var bus = new FakeGigabyteGpuBus();
        List<int> addresses;
        using (var scanner = PluginRuntime.LoadForMetadata(plugin))
            addresses = scanner.Scan(bus);
        Assert.Equal(new[] { 0x71 }, addresses);

        using var runtime = PluginRuntime.LoadForSmBus(plugin, bus, 0x71);
        runtime.LoadParameters();
        runtime.Initialize(new SolidFrame(RgbColor.Black));
        bus.Log.Clear();
        runtime.Render(new SolidFrame(new RgbColor(0, 0, 255)));

        var structure = runtime.TakeStructure();
        output.WriteLine($"{structure.Name}: {structure.Zones.Single().LedCount} LEDs");
        Assert.Equal(25, structure.Zones.Single().LedCount); // 3 fans x 8 LEDs + side logo on the 4070 Ti Gaming OC V2

        var writes = bus.Log.Where(l => l.Op.StartsWith('W')).ToList();
        Assert.NotEmpty(writes);
        Assert.All(writes, w => { Assert.Equal(0x71, w.Address); Assert.Equal("WRAW", w.Op); Assert.Equal(8, w.Data.Length); });
        // Per-LED color packets (0xB0+) carry blue.
        Assert.Contains(writes, w => w.Data[0] >= 0xB0 && w.Data[1] == 0x01 && w.Data[2] == 0 && w.Data[3] == 0 && w.Data[4] == 255);
    }
}
