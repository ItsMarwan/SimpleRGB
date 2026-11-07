using System.Runtime.InteropServices;
using System.Text;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Sensors;

/// <summary>A reading of the PC's vital signs. Null means "not available on this PC / right now".</summary>
public sealed record SensorReading(double? CpuTemp, double? CpuLoad, double? GpuTemp, double? GpuLoad)
{
    public static SensorReading Unknown { get; } = new(null, null, null, null);
}

public interface ISensorSource
{
    SensorReading Current { get; }
}

/// <summary>
/// Reads CPU temperature (Intel, via PawnIO's read-only MSR access), CPU load (Windows), and GPU temperature and
/// load (NVIDIA driver). Polls once a second, and only after something asked for a value.
/// Read-only: nothing here ever writes to hardware.
/// </summary>
public sealed class SensorService : ISensorSource, IDisposable
{
    private static readonly Lazy<SensorService> Instance = new(() => new SensorService());

    /// <summary>Process-wide sensor source; tests can replace it.</summary>
    public static ISensorSource Shared { get; set; } = new LazyShared();

    private sealed class LazyShared : ISensorSource
    {
        public SensorReading Current => Instance.Value.Current;
    }

    private readonly object _sync = new();
    private Thread? _thread;
    private volatile SensorReading _current = SensorReading.Unknown;
    private volatile bool _disposed;
    private long _lastRequest;
    private static volatile int _pollIntervalMs = 1000;

    /// <summary>How often sensors are read (250 ms … 5 s). Displays can ask for faster updates.</summary>
    public static int PollIntervalMs
    {
        get => _pollIntervalMs;
        set => _pollIntervalMs = Math.Clamp(value, 250, 5000);
    }

    public SensorReading Current
    {
        get
        {
            Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
            EnsureRunning();
            return _current;
        }
    }

    private void EnsureRunning()
    {
        if (_thread != null) return;
        lock (_sync)
        {
            if (_thread != null || _disposed) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "SimpleRGB sensors", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    private void Run()
    {
        var cpuLoad = new CpuLoadMeter();
        IntelCpuTemp? cpuTemp = IntelCpuTemp.TryOpen();
        var gpu = NvidiaSensors.TryOpen();
        Log.Info("Sensors", $"Sensors: CPU temp {(cpuTemp != null ? "available" : "unavailable (needs Intel CPU, PawnIO and admin)")}, " +
                            $"GPU {(gpu != null ? "available" : "unavailable")}.");

        while (!_disposed)
        {
            try
            {
                // Idle when nobody has asked for readings in the last 10 s.
                if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 10_000)
                {
                    var (gpuTemp, gpuLoad) = gpu?.Read() ?? (null, null);
                    _current = new SensorReading(cpuTemp?.Read(), cpuLoad.Read(), gpuTemp, gpuLoad);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Sensors", $"Sensor read failed: {ex.Message}");
            }
            Thread.Sleep(PollIntervalMs);
        }
        cpuTemp?.Dispose();
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Total CPU usage from GetSystemTimes deltas.</summary>
internal sealed class CpuLoadMeter
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _idle, _kernel, _user;
    private bool _primed;

    public double? Read()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var di = idle - _idle;
        var total = (kernel - _kernel) + (user - _user); // kernel time includes idle time
        _idle = idle; _kernel = kernel; _user = user;
        if (!_primed || total <= 0) { _primed = true; return null; }
        return Math.Clamp(100.0 * (total - di) / total, 0, 100);
    }
}

/// <summary>
/// Intel package temperature = TjMax (IA32_TEMPERATURE_TARGET, 0x1A2) − readout (IA32_PACKAGE_THERM_STATUS, 0x1B1),
/// through PawnIO's official IntelMSR module. Read-only.
/// </summary>
internal sealed unsafe class IntelCpuTemp : IDisposable
{
    private const uint MsrTemperatureTarget = 0x1A2;
    private const uint MsrPackageThermStatus = 0x1B1;
    private static readonly byte[] ReadName = Encoding.ASCII.GetBytes("ioctl_read_msr\0");

    private readonly Hardware.SmBus.PawnIoLibrary _lib;
    private IntPtr _handle;
    private readonly int _tjMax;

    private IntelCpuTemp(Hardware.SmBus.PawnIoLibrary lib, IntPtr handle, int tjMax)
    {
        _lib = lib;
        _handle = handle;
        _tjMax = tjMax;
    }

    public static IntelCpuTemp? TryOpen()
    {
        try
        {
            if (!IsIntel()) return null;
            var module = Path.Combine(AppPaths.PawnIoModulesDir, "IntelMSR.bin");
            if (!File.Exists(module)) return null;
            var lib = Hardware.SmBus.PawnIoLibrary.Load(out _);
            if (lib == null) return null;

            IntPtr handle;
            if (lib.Open(&handle) != 0) return null; // not admin / driver missing
            var blob = File.ReadAllBytes(module);
            int hr;
            fixed (byte* p = blob) hr = lib.LoadModule(handle, p, (nuint)blob.Length);
            if (hr != 0) { lib.Close(handle); return null; }

            var probe = new IntelCpuTemp(lib, handle, 100);
            var target = probe.ReadMsr(MsrTemperatureTarget);
            var tj = target is { } t ? (int)((t >> 16) & 0xFF) : 0;
            if (tj is < 70 or > 130) tj = 100;
            return new IntelCpuTemp(lib, handle, tj);
        }
        catch (Exception ex)
        {
            Log.Debug("Sensors", $"CPU temperature unavailable: {ex.Message}");
            return null;
        }
    }

    private ulong? ReadMsr(uint msr)
    {
        if (_handle == IntPtr.Zero) return null;
        ulong input = msr, output = 0;
        nuint returned = 0;
        fixed (byte* name = ReadName)
        {
            var hr = _lib.Execute(_handle, name, &input, 1, &output, 1, &returned);
            return hr == 0 ? output : null;
        }
    }

    public double? Read()
    {
        var status = ReadMsr(MsrPackageThermStatus);
        if (status is not { } s) return null;
        var readout = (int)((s >> 16) & 0x7F);
        var temp = _tjMax - readout;
        return temp is > 0 and < 150 ? temp : null;
    }

    private static bool IsIntel()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("VendorIdentifier") as string)?.Contains("GenuineIntel", StringComparison.Ordinal) == true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { _lib.Close(_handle); _handle = IntPtr.Zero; }
    }
}

/// <summary>GPU core temperature and utilization from the NVIDIA driver (NVAPI, user mode).</summary>
internal sealed unsafe class NvidiaSensors
{
    private const uint ID_Initialize = 0x0150E828;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GetThermalSettings = 0xE3640A56;
    private const uint ID_GetDynamicPstatesInfoEx = 0x60DED2ED;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalSensor { public int Controller, DefaultMin, DefaultMax, CurrentTemp, Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalSettings
    {
        public uint Version, Count;
        public ThermalSensor S0, S1, S2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint IsPresent, Percentage; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DynamicPstates
    {
        public uint Version, Flags;
        public Utilization U0, U1, U2, U3, U4, U5, U6, U7;
    }

    private readonly IntPtr _gpu;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, ThermalSettings*, int> _thermal;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, DynamicPstates*, int> _pstates;

    private NvidiaSensors(IntPtr gpu, delegate* unmanaged[Cdecl]<IntPtr, int, ThermalSettings*, int> thermal,
        delegate* unmanaged[Cdecl]<IntPtr, DynamicPstates*, int> pstates)
    {
        _gpu = gpu;
        _thermal = thermal;
        _pstates = pstates;
    }

    public static NvidiaSensors? TryOpen()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!File.Exists(path)) return null;
            var module = NativeLibrary.Load(path);
            var query = (delegate* unmanaged[Cdecl]<uint, IntPtr>)NativeLibrary.GetExport(module, "nvapi_QueryInterface");
            var init = (delegate* unmanaged[Cdecl]<int>)query(ID_Initialize);
            var enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, int*, int>)query(ID_EnumPhysicalGPUs);
            var thermal = (delegate* unmanaged[Cdecl]<IntPtr, int, ThermalSettings*, int>)query(ID_GetThermalSettings);
            var pstates = (delegate* unmanaged[Cdecl]<IntPtr, DynamicPstates*, int>)query(ID_GetDynamicPstatesInfoEx);
            if (init == null || enumGpus == null || init() != 0) return null;
            IntPtr* handles = stackalloc IntPtr[64];
            int count = 0;
            if (enumGpus(handles, &count) != 0 || count == 0) return null;
            return new NvidiaSensors(handles[0], thermal, pstates);
        }
        catch (Exception ex)
        {
            Log.Debug("Sensors", $"NVIDIA sensors unavailable: {ex.Message}");
            return null;
        }
    }

    public (double? Temp, double? Load) Read()
    {
        double? temp = null, load = null;
        if (_thermal != null)
        {
            var t = new ThermalSettings { Version = (uint)(sizeof(ThermalSettings) | (2 << 16)) };
            if (_thermal(_gpu, 15 /* all sensors */, &t) == 0 && t.Count > 0)
            {
                // Target 1 = GPU core; fall back to the first sensor.
                var s = t.S0.Target == 1 || t.Count == 1 ? t.S0 : t.S1.Target == 1 ? t.S1 : t.S0;
                if (s.CurrentTemp is > 0 and < 150) temp = s.CurrentTemp;
            }
        }
        if (_pstates != null)
        {
            var p = new DynamicPstates { Version = (uint)(sizeof(DynamicPstates) | (1 << 16)) };
            if (_pstates(_gpu, &p) == 0 && p.U0.IsPresent != 0)
                load = Math.Clamp((double)p.U0.Percentage, 0, 100);
        }
        return (temp, load);
    }
}
