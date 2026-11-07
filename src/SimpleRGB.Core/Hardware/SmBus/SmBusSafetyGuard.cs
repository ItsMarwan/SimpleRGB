using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Hardware.SmBus;

/// <summary>
/// Wraps the motherboard (chipset) SMBus and makes the memory configuration chips on it unreachable,
/// no matter what a plugin asks for:
/// <list type="bullet">
/// <item>0x50–0x57: SPD EEPROM (DDR3/DDR4) / SPD hub (DDR5). Holds the RAM's timings; corrupting it can make a stick unbootable.</item>
/// <item>0x30–0x37: DDR4 SPD write-protect (SWP0–3, CWP) and page-select (SPA0/SPA1) commands.</item>
/// <item>0x48–0x4F: DDR5 PMICs (RAM voltage regulators). Only locked on DDR5 / unknown memory, because on
///       older boards 0x4E/0x4F are legitimate ASUS Aura RGB controllers.</item>
/// </list>
/// Nothing addressed to a protected chip ever reaches the hardware. Probes (quick writes) are answered as
/// "occupied" so no plugin will try to move a device onto a protected address, and every read or write
/// returns an error.
/// </summary>
public sealed class SmBusSafetyGuard : SmBus
{
    private readonly SmBus _inner;
    private readonly bool[] _protected = new bool[128];
    private readonly HashSet<byte> _warned = new();

    public SmBusSafetyGuard(SmBus inner, bool protectDdr5Pmic)
    {
        _inner = inner;
        Vendor = inner.Vendor;
        Product = inner.Product;
        SubVendor = inner.SubVendor;
        SubDevice = inner.SubDevice;
        Port = inner.Port;
        for (var a = 0x30; a <= 0x37; a++) _protected[a] = true;
        for (var a = 0x50; a <= 0x57; a++) _protected[a] = true;
        if (protectDdr5Pmic)
            for (var a = 0x48; a <= 0x4F; a++) _protected[a] = true;
        ProtectsDdr5Pmic = protectDdr5Pmic;
    }

    public bool ProtectsDdr5Pmic { get; }
    public SmBus Inner => _inner;
    public override string Name => _inner.Name;
    public override SmBusKind Kind => _inner.Kind;

    public bool IsProtected(byte address) => address >= 128 || _protected[address];

    private bool Blocked(byte address, string op)
    {
        if (!IsProtected(address)) return false;
        lock (_warned)
        {
            if (_warned.Add(address))
                Log.Warn("SMBus guard", $"Blocked {op} to protected address 0x{address:X2} on {Name}. " +
                                        "SimpleRGB never touches RAM configuration chips.");
        }
        return true;
    }

    // A probe of a protected address reports "something is here" (0) without touching the bus.
    public override int WriteQuick(byte address) => Blocked(address, "probe") ? 0 : _inner.WriteQuick(address);
    public override int ReadByte(byte address) => Blocked(address, "read") ? -1 : _inner.ReadByte(address);
    public override int WriteByte(byte address, byte value) => Blocked(address, "write") ? -1 : _inner.WriteByte(address, value);
    public override int ReadByteData(byte address, byte register) => Blocked(address, "read") ? -1 : _inner.ReadByteData(address, register);
    public override int WriteByteData(byte address, byte register, byte value) => Blocked(address, "write") ? -1 : _inner.WriteByteData(address, register, value);
    public override int ReadWordData(byte address, byte register) => Blocked(address, "read") ? -1 : _inner.ReadWordData(address, register);
    public override int WriteWordData(byte address, byte register, ushort value) => Blocked(address, "write") ? -1 : _inner.WriteWordData(address, register, value);
    public override int WriteBlockData(byte address, byte register, ReadOnlySpan<byte> data) => Blocked(address, "write") ? -1 : _inner.WriteBlockData(address, register, data);
    public override int ReadI2CBlockData(byte address, byte register, Span<byte> buffer) => Blocked(address, "read") ? -1 : _inner.ReadI2CBlockData(address, register, buffer);
    public override int WriteI2CBlockData(byte address, byte register, ReadOnlySpan<byte> data) => Blocked(address, "write") ? -1 : _inner.WriteI2CBlockData(address, register, data);
    public override int RawWrite(byte address, ReadOnlySpan<byte> data) => Blocked(address, "write") ? -1 : _inner.RawWrite(address, data);
    public override int RawRead(byte address, Span<byte> buffer) => Blocked(address, "read") ? -1 : _inner.RawRead(address, buffer);

    public override void Dispose()
    {
        _inner.Dispose();
        base.Dispose();
    }
}
