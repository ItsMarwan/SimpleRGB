using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Core.Devices;

/// <summary>A HID connection that talks to nothing (UI preview mode). Optional scripted replies.</summary>
public sealed class VirtualHidConnection(HidEndpointInfo endpoint, Func<byte[], byte[]?>? responder = null) : IHidConnection
{
    private readonly Queue<byte[]> _replies = new();
    public HidEndpointInfo Endpoint { get; } = endpoint;
    public bool IsDisconnected => false;

    public int Write(ReadOnlySpan<byte> data)
    {
        if (responder?.Invoke(data.ToArray()) is { } reply) _replies.Enqueue(reply);
        return data.Length;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (!_replies.TryDequeue(out var r)) return 0;
        var n = Math.Min(r.Length, buffer.Length);
        r.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public int SendFeatureReport(ReadOnlySpan<byte> data) => data.Length;
    public int GetFeatureReport(Span<byte> buffer) => buffer.Length;
    public int GetInputReport(Span<byte> buffer) => buffer.Length;
    public void FlushInput() => _replies.Clear();
    public void Dispose() { }
}

/// <summary>
/// Fake devices for "--preview": lets the UI be designed and tested with no hardware access at all.
/// </summary>
public static class PreviewDevices
{
    private const string Motherboard = """
        export function Name() { return "ASUS TUF GAMING B760M-PLUS WIFI D4 Controller"; }
        export function VendorId() { return 0x0B05; } export function ProductId() { return [0x19AF]; }
        export function Type() { return "Hid"; } export function DeviceType() { return "motherboard"; }
        export function ControllableParameters() { return [
          { property: "RGBconfig", group: "lighting", label: "ARGB Channel Configuration", type: "combobox", values: ["RGB", "GRB"], default: "RGB" },
          { property: "disableRGBHeaders", group: "", label: "Disable 12v Headers", type: "boolean", default: "0" } ]; }
        export function Initialize() {
          for (const h of ["12V Header 1", "12V Header 2"]) { device.createSubdevice(h); device.setSubdeviceName(h, "ASUS AURA - " + h); device.setSubdeviceLeds(h, ["12v RGB Header"], [[1, 1]]); }
          for (let i = 1; i <= 3; i++) device.addChannel("Channel " + i, 120);
        }
        export function Render() { for (let i = 1; i <= 3; i++) device.channel("Channel " + i).getColors("Inline"); }
        """;

    private const string Gpu = """
        export function Name() { return "GIGABYTE 4070Ti Gaming OC V2"; }
        export function Type() { return "SMBUS"; } export function DeviceType() { return "gpu"; }
        export function Initialize() {
          const names = [], pos = [];
          for (let f = 0; f < 3; f++) for (let i = 0; i < 8; i++) { names.push(`Fan ${f + 1} LED ${i + 1}`); pos.push([f * 5 + i % 4, Math.floor(i / 4)]); }
          names.push("Side Logo LED"); pos.push([12, 3]);
          device.setControllableLeds(names, pos); device.setSize([15, 4]);
        }
        export function Render() { device.color(0, 0); }
        """;

    private const string Ram = """
        export function Name() { return "Aura Compatible RAM"; }
        export function Type() { return "SMBUS"; } export function DeviceType() { return "ram"; }
        export function Initialize() {
          const n = [], p = []; for (let i = 0; i < 8; i++) { n.push("LED " + (i + 1)); p.push([0, 7 - i]); }
          device.setControllableLeds(n, p); device.setSize([1, 8]);
        }
        export function Render() { device.color(0, 0); }
        """;

    /// <summary>Creates the preview devices (each with its own runtime, nothing touches hardware).</summary>
    public static List<(string Key, PluginRuntime Runtime, DeviceConnection Connection, string ConnectionText)> Create(string scratchDir)
    {
        Directory.CreateDirectory(scratchDir);
        var list = new List<(string, PluginRuntime, DeviceConnection, string)>();

        PluginInfo Info(string name, string code, PluginKind kind)
        {
            var path = Path.Combine(scratchDir, name);
            File.WriteAllText(path, code);
            return new PluginInfo { FilePath = path, RelativePath = name, RootDir = scratchDir, Source = "Preview", Kind = kind };
        }

        PluginRuntime Hid(PluginInfo info, int vid, int pid, Func<byte[], byte[]?>? responder = null)
        {
            var ep = new HidEndpointInfo
            {
                Path = $"preview:{vid:X4}:{pid:X4}", VendorId = vid, ProductId = pid, Interface = 2, Collection = 0,
                UsagePage = 0xFF00, Usage = 1, InputReportLength = 65, OutputReportLength = 65, ParentInstanceId = "PREVIEW",
                Product = info.RelativePath,
            };
            var phys = new HidPhysicalDevice("PREVIEW", vid, pid, info.RelativePath, "Preview", "", new[] { ep });
            var backend = new HidBackend(phys, e => new VirtualHidConnection(e, responder));
            var rt = PluginRuntime.LoadForHid(info, backend);
            rt.LoadParameters();
            backend.Open(ep);
            return rt;
        }

        list.Add(("preview:mb", Hid(Info("mb.js", Motherboard, PluginKind.Hid), 0x0B05, 0x19AF), DeviceConnection.Usb, "USB 0B05:19AF"));
        list.Add(("preview:gpu", PluginRuntime.LoadForMetadata(Info("gpu.js", Gpu, PluginKind.SmBus)), DeviceConnection.GpuI2C, "NVIDIA GeForce RTX 4070 Ti, address 0x71"));
        for (var i = 0; i < 4; i++)
            list.Add(($"preview:ram{i}", PluginRuntime.LoadForMetadata(Info("ram.js", Ram, PluginKind.SmBus)), DeviceConnection.SmBus, $"Intel SMBus - 0xefa0, address 0x{0x39 + i:X2}"));

        var display = Path.Combine(AppPaths.BuiltInPluginsDir, "Thermalright", "Thermalright_Digital_Display.js");
        if (File.Exists(display))
        {
            var info = new PluginInfo { FilePath = display, RelativePath = "Thermalright/Thermalright_Digital_Display.js", RootDir = AppPaths.BuiltInPluginsDir, Source = "SimpleRGB", Kind = PluginKind.Hid };
            list.Add(("preview:lcd", Hid(info, 0x0416, 0x8001, w => w.Length > 13 && w[1] == 0xDA && w[13] == 1
                ? new byte[] { 0xDA, 0xDB, 0xDC, 0xDD, 0x10, 0x10, 0x10, 0x10 }.Concat(new byte[56]).ToArray() : null),
                DeviceConnection.Usb, "USB 0416:8001"));
        }
        foreach (var entry in list) entry.Item2.LoadParameters();
        return list;
    }
}
