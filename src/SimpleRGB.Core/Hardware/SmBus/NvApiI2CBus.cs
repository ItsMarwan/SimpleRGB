using System.Runtime.InteropServices;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Hardware.SmBus;

/// <summary>
/// I2C on an NVIDIA GPU through the official driver's NVAPI (nvapi64.dll), port 1, where board
/// partners attach their RGB controllers. No extra driver and no admin rights required.
/// Layout and function ids match OpenRGB's proven implementation.
/// </summary>
public sealed unsafe class NvApiI2CBus : SmBus
{
    private const uint ID_Initialize = 0x0150E828;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GPU_GetPCIIdentifiers = 0x2DDFB66E;
    private const uint ID_GPU_GetFullName = 0xCEEE8E9F;
    private const uint ID_I2CWriteEx = 0x283AC65A;
    private const uint ID_I2CReadEx = 0x4D7B0709;

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_I2C_INFO_V3
    {
        public uint version;
        public uint displayMask;
        public byte isDdcPort;
        public byte i2cDevAddress;
        public byte* i2cRegAddress;
        public uint regAddrSize;
        public byte* data;
        public uint size;
        public uint i2cSpeed;
        public uint i2cSpeedKhz;
        public byte portId;
        public uint isPortIdSet;
    }

    private static readonly object InitLock = new();
    private static bool _initialized;
    private static delegate* unmanaged[Cdecl]<IntPtr, NV_I2C_INFO_V3*, uint*, int> _write;
    private static delegate* unmanaged[Cdecl]<IntPtr, NV_I2C_INFO_V3*, uint*, int> _read;

    private readonly IntPtr _gpu;
    private readonly object _sync = new();

    public override string Name { get; }
    public override SmBusKind Kind => SmBusKind.NvidiaGpu;

    private NvApiI2CBus(IntPtr gpu, string name, uint deviceId, uint subSystemId)
    {
        _gpu = gpu;
        Name = name;
        Vendor = (int)(deviceId & 0xFFFF);
        Product = (int)(deviceId >> 16);
        SubVendor = (int)(subSystemId & 0xFFFF);
        SubDevice = (int)(subSystemId >> 16);
        Port = 1;
    }

    /// <summary>Returns one bus per NVIDIA GPU. Empty when there is no NVIDIA driver.</summary>
    public static List<NvApiI2CBus> EnumerateGpus()
    {
        var buses = new List<NvApiI2CBus>();
        lock (InitLock)
        {
            try
            {
                var path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
                if (!File.Exists(path)) return buses;
                var module = NativeLibrary.Load(path);
                var query = (delegate* unmanaged[Cdecl]<uint, IntPtr>)NativeLibrary.GetExport(module, "nvapi_QueryInterface");

                var init = (delegate* unmanaged[Cdecl]<int>)query(ID_Initialize);
                var enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, int*, int>)query(ID_EnumPhysicalGPUs);
                var getPci = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, uint*, int>)query(ID_GPU_GetPCIIdentifiers);
                var getName = (delegate* unmanaged[Cdecl]<IntPtr, sbyte*, int>)query(ID_GPU_GetFullName);
                _write = (delegate* unmanaged[Cdecl]<IntPtr, NV_I2C_INFO_V3*, uint*, int>)query(ID_I2CWriteEx);
                _read = (delegate* unmanaged[Cdecl]<IntPtr, NV_I2C_INFO_V3*, uint*, int>)query(ID_I2CReadEx);
                if (init == null || enumGpus == null || getPci == null || _write == null || _read == null)
                {
                    Log.Warn("NVAPI", "NVAPI is missing required functions; GPU lighting unavailable.");
                    return buses;
                }

                if (!_initialized)
                {
                    var status = init();
                    if (status != 0) { Log.Warn("NVAPI", $"NvAPI_Initialize failed ({status})."); return buses; }
                    _initialized = true;
                }

                IntPtr* handles = stackalloc IntPtr[64];
                sbyte* nameBuf = stackalloc sbyte[64];
                int count = 0;
                if (enumGpus(handles, &count) != 0) return buses;

                for (var i = 0; i < count; i++)
                {
                    uint deviceId, subSystemId, revision, extDevice;
                    if (getPci(handles[i], &deviceId, &subSystemId, &revision, &extDevice) != 0) continue;
                    new Span<sbyte>(nameBuf, 64).Clear();
                    var name = getName != null && getName(handles[i], nameBuf) == 0
                        ? new string(nameBuf).Trim()
                        : $"NVIDIA GPU {i}";
                    if (!name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)) name = "NVIDIA " + name;
                    buses.Add(new NvApiI2CBus(handles[i], name, deviceId, subSystemId));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("NVAPI", $"NVIDIA I2C unavailable: {ex.Message}");
            }
        }
        return buses;
    }

    private int Transfer(byte address, bool write, byte? register, byte* data, ref uint size)
    {
        if (address > 0x7F) return -1;
        lock (_sync)
        {
            byte reg = register ?? (byte)0;
            var info = new NV_I2C_INFO_V3
            {
                version = (uint)(sizeof(NV_I2C_INFO_V3) | (3 << 16)),
                displayMask = 0,
                isDdcPort = 0,
                i2cDevAddress = (byte)(address << 1),
                i2cRegAddress = register.HasValue ? &reg : null,
                regAddrSize = register.HasValue ? 1u : 0u,
                data = data,
                size = size,
                i2cSpeed = 0xFFFF,   // deprecated field, must be 0xFFFF
                i2cSpeedKhz = 0,     // NVAPI_I2C_SPEED_DEFAULT
                portId = 1,          // RGB controllers are on port 1
                isPortIdSet = 1,
            };
            uint unknown = 0;
            var status = write ? _write(_gpu, &info, &unknown) : _read(_gpu, &info, &unknown);
            if (!write) size = info.size;
            return status == 0 ? 0 : -1;
        }
    }

    // NVAPI cannot do an SMBus quick write; report "nothing here" so plugins skip the address.
    public override int WriteQuick(byte address) => -1;

    public override int ReadByte(byte address)
    {
        byte b = 0; uint size = 1;
        return Transfer(address, false, null, &b, ref size) < 0 ? -1 : b;
    }

    public override int WriteByte(byte address, byte value)
    {
        uint size = 1;
        return Transfer(address, true, null, &value, ref size);
    }

    public override int ReadByteData(byte address, byte register)
    {
        byte b = 0; uint size = 1;
        return Transfer(address, false, register, &b, ref size) < 0 ? -1 : b;
    }

    public override int WriteByteData(byte address, byte register, byte value)
    {
        uint size = 1;
        return Transfer(address, true, register, &value, ref size);
    }

    public override int ReadWordData(byte address, byte register)
    {
        byte* buf = stackalloc byte[2]; uint size = 2;
        return Transfer(address, false, register, buf, ref size) < 0 ? -1 : buf[0] | (buf[1] << 8);
    }

    public override int WriteWordData(byte address, byte register, ushort value)
    {
        byte* buf = stackalloc byte[2] { (byte)value, (byte)(value >> 8) };
        uint size = 2;
        return Transfer(address, true, register, buf, ref size);
    }

    public override int WriteBlockData(byte address, byte register, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return -1;
        byte* buf = stackalloc byte[33];
        buf[0] = (byte)data.Length;
        data.CopyTo(new Span<byte>(buf + 1, 32));
        var size = (uint)data.Length + 1;
        return Transfer(address, true, register, buf, ref size);
    }

    public override int ReadI2CBlockData(byte address, byte register, Span<byte> buffer)
    {
        var length = Math.Min(buffer.Length, 32);
        if (length < 1) return -1;
        byte* buf = stackalloc byte[32];
        var size = (uint)length;
        if (Transfer(address, false, register, buf, ref size) < 0) return -1;
        var count = (int)Math.Min(size, (uint)length);
        new Span<byte>(buf, count).CopyTo(buffer);
        return count;
    }

    public override int WriteI2CBlockData(byte address, byte register, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return -1;
        byte* buf = stackalloc byte[32];
        data.CopyTo(new Span<byte>(buf, 32));
        var size = (uint)data.Length;
        return Transfer(address, true, register, buf, ref size);
    }

    public override int RawWrite(byte address, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 64) return -1;
        byte* buf = stackalloc byte[64];
        data.CopyTo(new Span<byte>(buf, 64));
        var size = (uint)data.Length;
        return Transfer(address, true, null, buf, ref size);
    }

    public override int RawRead(byte address, Span<byte> buffer)
    {
        var length = Math.Min(buffer.Length, 64);
        if (length < 1) return -1;
        byte* buf = stackalloc byte[64];
        var size = (uint)length;
        if (Transfer(address, false, null, buf, ref size) < 0) return -1;
        var count = (int)Math.Min(size, (uint)length);
        new Span<byte>(buf, count).CopyTo(buffer);
        return count;
    }
}
