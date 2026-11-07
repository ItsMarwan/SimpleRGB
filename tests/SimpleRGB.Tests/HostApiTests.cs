using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Tests;

/// <summary>Host API behaviour checked with small hand-written plugins.</summary>
public class HostApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "simplergb-tests-" + Guid.NewGuid().ToString("N"));

    public HostApiTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private PluginInfo Write(string code, string name = "test.js")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return new PluginInfo { FilePath = path, RelativePath = name, RootDir = _root, Source = "Test", Kind = PluginKind.Hid };
    }

    private (PluginRuntime Runtime, FakeHid Hid) LoadHid(string code)
    {
        var ep = FakeHid.MakeEndpoint(0x1234, 0x5678, 0, len: 17);
        var device = new HidPhysicalDevice("USB\\T", 0x1234, 0x5678, "Test", "Test", "", new[] { ep });
        FakeHid? fake = null;
        var backend = new HidBackend(device, e => fake = new FakeHid(e));
        var runtime = PluginRuntime.LoadForHid(Write(code), backend);
        runtime.LoadParameters();
        backend.Open(ep);
        return (runtime, fake!);
    }

    [Fact]
    public void Metadata_IsReadFromExports()
    {
        using var r = PluginRuntime.LoadForMetadata(Write("""
            export function Name() { return "Widget"; }
            export function VendorId() { return 0x1234; }
            export function ProductId() { return [0x1, 0x2]; }
            export function Type() { return "Hid"; }
            """));
        Assert.Equal("Widget", r.GetString("Name"));
        Assert.Equal(new[] { 0x1234 }, r.GetIntList("VendorId"));
        Assert.Equal(new[] { 1, 2 }, r.GetIntList("ProductId"));
    }

    [Fact]
    public void Write_PadsToRequestedLengthAndWrapsValuesLikeUint8()
    {
        var (r, hid) = LoadHid("""
            export function Initialize() { device.write([0x01, 300, -1], 8); }
            """);
        using (r)
        {
            r.Initialize(new SolidFrame(RgbColor.Black));
            Assert.Equal(new byte[] { 0x01, 44, 255, 0, 0, 0, 0, 0 }, hid.Writes.Single());
        }
    }

    [Fact]
    public void ChannelColors_RespectColorOrderAndLedCount()
    {
        var (r, hid) = LoadHid("""
            export function Initialize() { device.addChannel("Strip", 10, 3); }
            export function Render() { device.write([0xAA].concat(device.channel("Strip").getColors("Inline", "GRB")), 17); }
            """);
        using (r)
        {
            r.Initialize(new SolidFrame(RgbColor.Black));
            r.Render(new SolidFrame(new RgbColor(1, 2, 3)));
            var w = hid.Writes.Last();
            Assert.Equal(new byte[] { 0xAA, 2, 1, 3, 2, 1, 3, 2, 1, 3 }, w[..10]);
            var zone = r.TakeStructure().Zones.Single();
            Assert.Equal("ch:Strip", zone.Id);
            Assert.Equal(3, zone.LedCount);
            Assert.Equal(10, zone.LedLimit);
        }
    }

    [Fact]
    public void BooleanAndNumberParameters_AreTypedGlobals()
    {
        var (r, hid) = LoadHid("""
            export function ControllableParameters() { return [
                { property: "flag", label: "Flag", type: "boolean", default: "0" },
                { property: "level", label: "Level", type: "number", min: "0", max: "10", default: "4" },
            ]; }
            export function Render() { device.write([flag ? 1 : 2, level + 1], 2); }
            export function onflagChanged() { device.write([9, 9], 2); }
            """);
        using (r)
        {
            r.Render(new SolidFrame(RgbColor.Black));
            Assert.Equal(new byte[] { 2, 5 }, hid.Writes.Last()); // "0" must be false, not a truthy string
            r.SetParameter("flag", "true");
            Assert.Equal(new byte[] { 9, 9 }, hid.Writes.Last()); // change callback fired
            r.Render(new SolidFrame(RgbColor.Black));
            Assert.Equal(new byte[] { 1, 5 }, hid.Writes.Last());
        }
    }

    [Fact]
    public void UnknownApiCalls_AreHarmlessNoOps()
    {
        var (r, hid) = LoadHid("""
            export function Initialize() { device.someFutureApi(1, 2); battery.setBatteryLevel(50); device.write([1], 1); }
            """);
        using (r)
        {
            r.Initialize(new SolidFrame(RgbColor.Black));
            Assert.Single(hid.Writes);
        }
    }

    [Fact]
    public void ScriptErrors_BecomePluginExceptions()
    {
        var (r, _) = LoadHid("export function Render() { null.boom(); }");
        using (r) Assert.Throws<PluginException>(() => r.Render(new SolidFrame(RgbColor.Black)));
    }

    [Fact]
    public void Disconnect_IsReportedAsDeviceGone()
    {
        var (r, hid) = LoadHid("export function Render() { device.write([1], 1); }");
        using (r)
        {
            hid.IsDisconnected = true;
            Assert.Throws<DeviceGoneException>(() => r.Render(new SolidFrame(RgbColor.Black)));
        }
    }

    [Fact]
    public void RelativeImportOutsidePluginFolder_IsRejected()
    {
        var info = Write("import { x } from '../../evil.js'; export function Name() { return 'x'; }", "sub/p.js");
        Assert.Throws<PluginException>(() => PluginRuntime.LoadForMetadata(info));
    }

    [Fact]
    public void RelativeImportInsideFolder_Works()
    {
        Write("export const answer = 42;", "brand/helper.js");
        var info = Write("import { answer } from './helper.js'; export function Name() { return 'v' + answer; }", "brand/p.js");
        using var r = PluginRuntime.LoadForMetadata(info);
        Assert.Equal("v42", r.GetString("Name"));
    }

    [Fact]
    public void Plugins_HaveNoAccessToTheHostSystem()
    {
        using var r = PluginRuntime.LoadForMetadata(Write("""
            export function Name() {
                const probes = [typeof require, typeof process, typeof System, typeof importNamespace, typeof fetch, typeof XMLHttpRequest];
                return probes.join(',');
            }
            """));
        Assert.Equal("undefined,undefined,undefined,undefined,undefined,undefined", r.GetString("Name"));
    }

    [Fact]
    public void SignalRgbModules_AreAvailable()
    {
        using var r = PluginRuntime.LoadForMetadata(Write("""
            import systeminfo from "@SignalRGB/systeminfo";
            import { Assert, globalContext } from "@SignalRGB/Errors.js";
            import LCD from "@SignalRGB/lcd";
            const board = systeminfo.GetMotherboardInfo();
            export function Name() { globalContext.set("a", 1); LCD.whatever(); return typeof board.model + Assert.softIsDefined(1); }
            """));
        Assert.Equal("stringtrue", r.GetString("Name"));
    }
}
