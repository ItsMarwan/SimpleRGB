using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Hardware.SmBus;

/// <summary>
/// The system-wide SMBus mutex shared by HWiNFO, LibreHardwareMonitor, OpenRGB, SignalRGB and others.
/// Holding it for every transfer stops two programs from interleaving transactions on the bus,
/// which is the classic cause of garbage writes.
/// </summary>
internal static class GlobalSmBusMutex
{
    private const string MutexName = @"Global\Access_SMBUS.HTP.Method";
    private static Mutex? _mutex;
    private static bool _initialized;
    private static readonly object InitLock = new();

    private static Mutex? Get()
    {
        lock (InitLock)
        {
            if (_initialized) return _mutex;
            _initialized = true;
            try
            {
                _mutex = new Mutex(false, MutexName);
            }
            catch (Exception ex)
            {
                try { _mutex = Mutex.OpenExisting(MutexName); }
                catch { Log.Warn("SMBus", $"Could not open the global SMBus mutex ({ex.Message}). Transfers will not be coordinated with other apps."); }
            }
            return _mutex;
        }
    }

    /// <summary>Runs <paramref name="transfer"/> while holding the mutex. Returns -1 if the bus stays busy.</summary>
    public static int Run(Func<int> transfer, int timeoutMs = 1000)
    {
        var mutex = Get();
        if (mutex == null) return transfer();

        bool acquired;
        try { acquired = mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { acquired = true; } // previous owner crashed; we own it now
        if (!acquired) return -1;
        try { return transfer(); }
        finally { mutex.ReleaseMutex(); }
    }
}
