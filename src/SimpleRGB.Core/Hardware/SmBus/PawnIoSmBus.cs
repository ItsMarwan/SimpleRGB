using System.Runtime.InteropServices;
using System.Text;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Hardware.SmBus;

/// <summary>
/// Intel chipset SMBus through the signed PawnIO driver (https://pawnio.eu) and its official
/// SmbusI801 module. PawnIO must be installed separately and the app must run as administrator.
/// </summary>
public sealed unsafe class PawnIoSmBus : SmBus
{
    private const int I2C_SMBUS_WRITE = 0, I2C_SMBUS_READ = 1;
    private const int PROTO_QUICK = 0, PROTO_BYTE = 1, PROTO_BYTE_DATA = 2, PROTO_WORD_DATA = 3,
        PROTO_BLOCK_DATA = 5, PROTO_I2C_BLOCK_DATA = 8;

    private static readonly byte[] XferName = Encoding.ASCII.GetBytes("ioctl_smbus_xfer\0");
    private static readonly byte[] IdentityName = Encoding.ASCII.GetBytes("ioctl_identity\0");

    private readonly PawnIoLibrary _lib;
    private IntPtr _handle;
    private readonly object _sync = new();

    public override string Name { get; }
    public override SmBusKind Kind => SmBusKind.IntelSystem;
    public int BaseAddress { get; }

    private PawnIoSmBus(PawnIoLibrary lib, IntPtr handle, ulong[] identity)
    {
        _lib = lib;
        _handle = handle;
        BaseAddress = (int)identity[1];
        var pciIds = identity[2];
        Vendor = (int)(pciIds & 0xFFFF);
        Product = (int)((pciIds >> 16) & 0xFFFF);
        SubVendor = (int)((pciIds >> 32) & 0xFFFF);
        SubDevice = (int)((pciIds >> 48) & 0xFFFF);
        Name = $"Intel SMBus - 0x{BaseAddress:x4}";
    }

    public static bool IsDriverInstalled => PawnIoLibrary.FindLibrary() != null;

    /// <summary>Opens the Intel SMBus. Returns null with a human readable reason when unavailable.</summary>
    public static PawnIoSmBus? TryOpen(out string? reason)
    {
        reason = null;
        var lib = PawnIoLibrary.Load(out reason);
        if (lib == null) return null;

        var modulePath = Path.Combine(AppPaths.PawnIoModulesDir, "SmbusI801.bin");
        if (!File.Exists(modulePath))
        {
            reason = $"PawnIO module missing: {modulePath}";
            return null;
        }

        IntPtr handle;
        var hr = lib.Open(&handle);
        if (hr != 0)
        {
            reason = (uint)hr == 0x80070005
                ? "PawnIO needs administrator rights. Restart SimpleRGB as administrator to control RAM."
                : $"Could not open the PawnIO driver (0x{hr:X8}).";
            return null;
        }

        var blob = File.ReadAllBytes(modulePath);
        fixed (byte* p = blob)
            hr = lib.LoadModule(handle, p, (nuint)blob.Length);
        if (hr != 0)
        {
            lib.Close(handle);
            // The module refuses to load when no supported Intel SMBus controller exists (e.g. AMD systems).
            reason = $"No supported Intel SMBus controller found (0x{hr:X8}).";
            return null;
        }

        var identity = new ulong[3];
        nuint returned = 0;
        fixed (byte* name = IdentityName)
        fixed (ulong* outp = identity)
            hr = lib.Execute(handle, name, null, 0, outp, 3, &returned);
        if (hr != 0)
        {
            lib.Close(handle);
            reason = $"PawnIO SMBus identity query failed (0x{hr:X8}).";
            return null;
        }

        return new PawnIoSmBus(lib, handle, identity);
    }

    private int Xfer(byte address, int readWrite, byte command, int protocol, ReadOnlySpan<ulong> data, Span<ulong> output)
    {
        if (address > 0x7F) return -1;
        Span<ulong> input = stackalloc ulong[4 + 5];
        input[0] = address;
        input[1] = (ulong)readWrite;
        input[2] = command;
        input[3] = (ulong)protocol;
        data.CopyTo(input[4..]);
        var inCount = 4 + data.Length;
        var inArr = input[..inCount].ToArray();
        var outArr = new ulong[output.Length];

        var result = GlobalSmBusMutex.Run(() =>
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero) return -1;
                nuint returned = 0;
                fixed (byte* name = XferName)
                fixed (ulong* inp = inArr)
                fixed (ulong* outp = outArr)
                {
                    var hr = _lib.Execute(_handle, name, inp, (nuint)inArr.Length, outArr.Length == 0 ? null : outp,
                        (nuint)outArr.Length, &returned);
                    return hr == 0 ? 0 : -1;
                }
            }
        });
        outArr.CopyTo(output);
        return result;
    }

    private static void PackBlock(ReadOnlySpan<byte> data, Span<ulong> cells)
    {
        // cells hold [length, data...] as little-endian bytes, 5 cells = 40 bytes.
        Span<byte> bytes = stackalloc byte[40];
        bytes.Clear();
        bytes[0] = (byte)data.Length;
        data.CopyTo(bytes[1..]);
        for (var i = 0; i < 5; i++) cells[i] = BitConverter.ToUInt64(bytes.Slice(i * 8, 8));
    }

    private static byte UnpackByte(ReadOnlySpan<ulong> cells, int byteIndex) =>
        (byte)(cells[byteIndex / 8] >> (8 * (byteIndex % 8)));

    public override int WriteQuick(byte address) =>
        Xfer(address, I2C_SMBUS_WRITE, 0, PROTO_QUICK, ReadOnlySpan<ulong>.Empty, Span<ulong>.Empty);

    public override int ReadByte(byte address)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, I2C_SMBUS_READ, 0, PROTO_BYTE, ReadOnlySpan<ulong>.Empty, o) < 0 ? -1 : (int)(o[0] & 0xFF);
    }

    // SMBus "send byte": the byte travels in the command field.
    public override int WriteByte(byte address, byte value)
    {
        Span<ulong> d = stackalloc ulong[] { 0 };
        return Xfer(address, I2C_SMBUS_WRITE, value, PROTO_BYTE, d, Span<ulong>.Empty);
    }

    public override int ReadByteData(byte address, byte register)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, I2C_SMBUS_READ, register, PROTO_BYTE_DATA, ReadOnlySpan<ulong>.Empty, o) < 0 ? -1 : (int)(o[0] & 0xFF);
    }

    public override int WriteByteData(byte address, byte register, byte value)
    {
        Span<ulong> d = stackalloc ulong[] { value };
        return Xfer(address, I2C_SMBUS_WRITE, register, PROTO_BYTE_DATA, d, Span<ulong>.Empty);
    }

    public override int ReadWordData(byte address, byte register)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, I2C_SMBUS_READ, register, PROTO_WORD_DATA, ReadOnlySpan<ulong>.Empty, o) < 0 ? -1 : (int)(o[0] & 0xFFFF);
    }

    public override int WriteWordData(byte address, byte register, ushort value)
    {
        Span<ulong> d = stackalloc ulong[] { value };
        return Xfer(address, I2C_SMBUS_WRITE, register, PROTO_WORD_DATA, d, Span<ulong>.Empty);
    }

    public override int WriteBlockData(byte address, byte register, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return -1;
        Span<ulong> cells = stackalloc ulong[5];
        PackBlock(data, cells);
        return Xfer(address, I2C_SMBUS_WRITE, register, PROTO_BLOCK_DATA, cells, Span<ulong>.Empty);
    }

    public override int WriteI2CBlockData(byte address, byte register, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return -1;
        Span<ulong> cells = stackalloc ulong[5];
        PackBlock(data, cells);
        return Xfer(address, I2C_SMBUS_WRITE, register, PROTO_I2C_BLOCK_DATA, cells, Span<ulong>.Empty);
    }

    public override int ReadI2CBlockData(byte address, byte register, Span<byte> buffer)
    {
        var length = Math.Min(buffer.Length, 32);
        if (length < 1) return -1;
        Span<ulong> d = stackalloc ulong[] { (ulong)length };
        Span<ulong> o = stackalloc ulong[5];
        if (Xfer(address, I2C_SMBUS_READ, register, PROTO_I2C_BLOCK_DATA, d, o) < 0) return -1;
        var count = (int)Math.Min(o[0], (ulong)length);
        for (var i = 0; i < count; i++) buffer[i] = UnpackByte(o, 8 + i);
        return count;
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                _lib.Close(_handle);
                _handle = IntPtr.Zero;
            }
        }
        base.Dispose();
    }
}

/// <summary>Late-bound access to PawnIOLib.dll from the PawnIO installation folder.</summary>
internal sealed unsafe class PawnIoLibrary
{
    private static PawnIoLibrary? _instance;
    private static readonly object LoadLock = new();

    private readonly delegate* unmanaged[Stdcall]<IntPtr*, int> _open;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, int> _load;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int> _execute;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int> _close;

    private PawnIoLibrary(IntPtr module)
    {
        _open = (delegate* unmanaged[Stdcall]<IntPtr*, int>)NativeLibrary.GetExport(module, "pawnio_open");
        _load = (delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, int>)NativeLibrary.GetExport(module, "pawnio_load");
        _execute = (delegate* unmanaged[Stdcall]<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int>)NativeLibrary.GetExport(module, "pawnio_execute");
        _close = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(module, "pawnio_close");
    }

    public static string? FindLibrary()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, "PawnIO", "PawnIOLib.dll");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static PawnIoLibrary? Load(out string? reason)
    {
        reason = null;
        lock (LoadLock)
        {
            if (_instance != null) return _instance;
            var path = FindLibrary();
            if (path == null)
            {
                reason = "PawnIO is not installed. Install it from https://pawnio.eu to control RAM lighting.";
                return null;
            }
            try
            {
                // Absolute path only: never let DLL search order pick up a different PawnIOLib.dll.
                var module = NativeLibrary.Load(path);
                _instance = new PawnIoLibrary(module);
                return _instance;
            }
            catch (Exception ex)
            {
                reason = $"Could not load {path}: {ex.Message}";
                Log.Error("PawnIO", reason);
                return null;
            }
        }
    }

    public int Open(IntPtr* handle) => _open(handle);
    public int LoadModule(IntPtr handle, byte* blob, nuint size) => _load(handle, blob, size);
    public int Execute(IntPtr handle, byte* name, ulong* input, nuint inCount, ulong* output, nuint outCount, nuint* returned) =>
        _execute(handle, name, input, inCount, output, outCount, returned);
    public int Close(IntPtr handle) => _close(handle);
}
