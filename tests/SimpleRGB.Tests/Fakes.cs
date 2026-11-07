using System.Text;
using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.Tests;

/// <summary>Frame colors with a fixed color everywhere.</summary>
internal sealed class SolidFrame(RgbColor color) : IFrameColors
{
    public RgbColor DeviceLed(int i, int c) => color;
    public RgbColor SubdeviceLed(string id, int i, int c) => color;
    public RgbColor ChannelLed(string id, int i, int c) => color;
    public RgbColor ZoneBaseColor(string zoneId) => color;
}

/// <summary>A fake HID endpoint that records writes and answers with scripted replies.</summary>
internal sealed class FakeHid : IHidConnection
{
    public FakeHid(HidEndpointInfo endpoint) => Endpoint = endpoint;
    public HidEndpointInfo Endpoint { get; }
    public bool IsDisconnected { get; set; }
    public List<byte[]> Writes { get; } = new();
    public Queue<byte[]> PendingReads { get; } = new();
    public Func<byte[], byte[]?>? Responder { get; set; }

    public int Write(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Writes.Add(copy);
        if (Responder?.Invoke(copy) is { } reply) PendingReads.Enqueue(reply);
        return copy.Length;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (!PendingReads.TryDequeue(out var reply)) return 0;
        var n = Math.Min(reply.Length, buffer.Length);
        reply.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public int SendFeatureReport(ReadOnlySpan<byte> data) { Writes.Add(data.ToArray()); return data.Length; }
    public int GetFeatureReport(Span<byte> buffer) => buffer.Length;
    public int GetInputReport(Span<byte> buffer) => buffer.Length;
    public void FlushInput() => PendingReads.Clear();
    public void Dispose() { }

    public static HidEndpointInfo MakeEndpoint(int vid, int pid, int iface, int usagePage = 0xFF72, int usage = 0xA1, int len = 65) => new()
    {
        Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_{iface:x2}#fake",
        VendorId = vid, ProductId = pid, Interface = iface, Collection = 0, UsagePage = usagePage, Usage = usage,
        InputReportLength = len, OutputReportLength = len, FeatureReportLength = 0,
        Product = "Fake device", ParentInstanceId = "USB\\FAKE",
    };
}

/// <summary>Records every transfer so tests can prove where writes went.</summary>
internal abstract class RecordingBus : SmBus
{
    public List<(byte Address, string Op, byte[] Data)> Log { get; } = new();
    /// <summary>Addresses that received data. Zero-data presence probes (quick writes) are not counted.</summary>
    public IEnumerable<byte> WrittenAddresses => Log.Where(l => l.Op.StartsWith("W") && l.Op != "WQ").Select(l => l.Address).Distinct();
    public IEnumerable<byte> TouchedAddresses => Log.Select(l => l.Address).Distinct();
    protected void Record(byte a, string op, params byte[] data) => Log.Add((a, op, data));
}

/// <summary>
/// Simulates ENE/Aura-compatible DDR4 RAM sticks (e.g. TeamGroup Delta RGB) plus SPD EEPROMs on a chipset SMBus.
/// All sticks start at 0x77 like after a cold boot and get remapped by the plugin.
/// </summary>
internal sealed class FakeEneRamBus : RecordingBus
{
    public sealed class Stick
    {
        public byte Address;
        public ushort Pointer;
        public readonly byte[] Memory = new byte[0x10000];
        public int SelectedIndex = -1;
    }

    public readonly List<Stick> Sticks = new();
    public readonly HashSet<byte> SpdAddresses = new() { 0x50, 0x51, 0x52, 0x53 };

    public FakeEneRamBus(int stickCount, int ledCount = 8)
    {
        Vendor = 0x8086; Product = 0x7A23;
        for (var i = 0; i < stickCount; i++)
        {
            var s = new Stick { Address = 0x77 };
            Encoding.ASCII.GetBytes("AUDA0-E6K5-0101").CopyTo(s.Memory, 0x1000);
            Encoding.ASCII.GetBytes("TEAMGROUP").CopyTo(s.Memory, 0x1030);
            s.Memory[0x1C02] = (byte)ledCount;
            Sticks.Add(s);
        }
    }

    public override string Name => "Fake Intel SMBus";
    public override SmBusKind Kind => SmBusKind.IntelSystem;

    // Only the first unmapped stick answers on 0x77 (the others wait their turn).
    private Stick? At(byte a) => Sticks.FirstOrDefault(s => s.Address == a);

    public override int WriteQuick(byte a) { Record(a, "WQ"); return At(a) != null || SpdAddresses.Contains(a) ? 0 : -1; }
    public override int ReadByte(byte a) { Record(a, "R"); return At(a) != null ? 0 : -1; }
    public override int WriteByte(byte a, byte v) { Record(a, "WB", v); return At(a) != null ? 0 : -1; }

    public override int ReadByteData(byte a, byte reg)
    {
        Record(a, "RBD", reg);
        if (SpdAddresses.Contains(a)) return 0x23;
        var s = At(a);
        if (s == null) return -1;
        return reg switch
        {
            0x81 => s.Memory[s.Pointer++],
            >= 0x90 and <= 0x9F => 0x10 + (reg - 0x90),
            >= 0xA0 and <= 0xAF => reg - 0xA0,
            _ => 0,
        };
    }

    public override int WriteByteData(byte a, byte reg, byte v)
    {
        Record(a, "WBD", reg, v);
        var s = At(a);
        if (s == null) return -1;
        if (reg != 0x01) return 0;
        if (s.Pointer == 0x80F8) s.SelectedIndex = v;
        else if (s.Pointer == 0x80F9 && s.Address == 0x77) s.Address = (byte)(v >> 1); // remap
        else s.Memory[s.Pointer] = v;
        return 0;
    }

    public override int ReadWordData(byte a, byte reg) { Record(a, "RWD", reg); return At(a) != null ? 0 : -1; }

    public override int WriteWordData(byte a, byte reg, ushort w)
    {
        Record(a, "WWD", reg, (byte)w, (byte)(w >> 8));
        var s = At(a);
        if (s == null) return -1;
        if (reg == 0x00) s.Pointer = (ushort)(((w << 8) & 0xFF00) | ((w >> 8) & 0xFF));
        return 0;
    }

    public override int WriteBlockData(byte a, byte reg, ReadOnlySpan<byte> data)
    {
        Record(a, "WBLK", data.ToArray());
        var s = At(a);
        if (s == null) return -1;
        if (reg == 0x03) data.CopyTo(s.Memory.AsSpan(s.Pointer));
        return 0;
    }

    public override int ReadI2CBlockData(byte a, byte reg, Span<byte> buffer) { Record(a, "RI2C", reg); return -1; }
    public override int WriteI2CBlockData(byte a, byte reg, ReadOnlySpan<byte> data) { Record(a, "WI2C", data.ToArray()); return -1; }
}

/// <summary>Simulates a Gigabyte RTX 4070 Ti Gaming OC (1458:4100) RGB controller at 0x71 on NVIDIA I2C.</summary>
internal sealed class FakeGigabyteGpuBus : RecordingBus
{
    private byte[] _lastWrite = Array.Empty<byte>();

    public FakeGigabyteGpuBus()
    {
        Vendor = 0x10DE; Product = 0x2782; SubVendor = 0x1458; SubDevice = 0x4100; Port = 1;
    }

    public override string Name => "Fake NVIDIA GeForce RTX 4070 Ti";
    public override SmBusKind Kind => SmBusKind.NvidiaGpu;

    public override int WriteQuick(byte a) { Record(a, "WQ"); return -1; }
    public override int ReadByte(byte a) { Record(a, "R"); return -1; }
    public override int WriteByte(byte a, byte v) { Record(a, "WB", v); return -1; }
    public override int ReadByteData(byte a, byte r) { Record(a, "RBD", r); return -1; }
    public override int WriteByteData(byte a, byte r, byte v) { Record(a, "WBD", r, v); return -1; }
    public override int ReadWordData(byte a, byte r) { Record(a, "RWD", r); return -1; }
    public override int WriteWordData(byte a, byte r, ushort w) { Record(a, "WWD", r); return -1; }
    public override int WriteBlockData(byte a, byte r, ReadOnlySpan<byte> d) { Record(a, "WBLK", d.ToArray()); return -1; }
    public override int ReadI2CBlockData(byte a, byte r, Span<byte> b) { Record(a, "RI2C", r); return -1; }
    public override int WriteI2CBlockData(byte a, byte r, ReadOnlySpan<byte> d) { Record(a, "WI2C", d.ToArray()); return -1; }

    public override int RawWrite(byte a, ReadOnlySpan<byte> data)
    {
        Record(a, "WRAW", data.ToArray());
        if (a != 0x71) return -1;
        _lastWrite = data.ToArray();
        return 0;
    }

    public override int RawRead(byte a, Span<byte> buffer)
    {
        Record(a, "RRAW");
        if (a != 0x71) return -1;
        buffer.Clear();
        if (_lastWrite.Length > 0 && _lastWrite[0] == 0xAB && buffer.Length >= 2)
        {
            buffer[0] = 0xAB;
            buffer[1] = 0x10;
        }
        return buffer.Length;
    }
}
