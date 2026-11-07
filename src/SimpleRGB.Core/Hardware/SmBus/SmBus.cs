namespace SimpleRGB.Core.Hardware.SmBus;

public enum SmBusKind
{
    /// <summary>Intel chipset SMBus (ICH/PCH, "i801"). RAM and some motherboard RGB live here.</summary>
    IntelSystem,
    /// <summary>NVIDIA GPU I2C via NVAPI (port 1, where RGB controllers are attached).</summary>
    NvidiaGpu,
}

/// <summary>
/// An SMBus/I2C bus. All transfer methods return a negative number on failure; read methods return
/// the value read on success. Implementations must be thread-safe.
/// </summary>
public abstract class SmBus : IDisposable
{
    private readonly ReentrantBusLock _lock = new();

    public abstract string Name { get; }
    public abstract SmBusKind Kind { get; }

    public int Vendor { get; protected init; }
    public int Product { get; protected init; }
    public int SubVendor { get; protected init; }
    public int SubDevice { get; protected init; }
    public int Port { get; protected init; }

    public bool IsSystemBus => Kind == SmBusKind.IntelSystem;

    /// <summary>Transaction lock plugins can hold across several transfers (TransactionLock/Unlock).</summary>
    public ReentrantBusLock Lock => _lock;

    public abstract int WriteQuick(byte address);
    /// <summary>SMBus "receive byte".</summary>
    public abstract int ReadByte(byte address);
    /// <summary>SMBus "send byte".</summary>
    public abstract int WriteByte(byte address, byte value);
    public abstract int ReadByteData(byte address, byte register);
    public abstract int WriteByteData(byte address, byte register, byte value);
    public abstract int ReadWordData(byte address, byte register);
    public abstract int WriteWordData(byte address, byte register, ushort value);
    /// <summary>SMBus block write (sends a length byte first). Max 32 bytes.</summary>
    public abstract int WriteBlockData(byte address, byte register, ReadOnlySpan<byte> data);
    /// <summary>I2C block read starting at <paramref name="register"/>. Returns bytes read.</summary>
    public abstract int ReadI2CBlockData(byte address, byte register, Span<byte> buffer);
    /// <summary>I2C block write (no length byte). Max 32 bytes.</summary>
    public abstract int WriteI2CBlockData(byte address, byte register, ReadOnlySpan<byte> data);

    /// <summary>Plain I2C write of raw bytes (no register). Default maps it onto SMBus primitives.</summary>
    public virtual int RawWrite(byte address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return WriteQuick(address);
        if (data.Length == 1) return WriteByte(address, data[0]);
        return WriteI2CBlockData(address, data[0], data[1..]);
    }

    /// <summary>Plain I2C read of raw bytes (no register). Not available on chipset SMBus.</summary>
    public virtual int RawRead(byte address, Span<byte> buffer) => -1;

    public override string ToString() => Name;

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>A reentrant lock that can be force-released if a plugin forgets to unlock.</summary>
public sealed class ReentrantBusLock
{
    private readonly object _sync = new();
    private int _ownerThread;
    private int _depth;

    public bool TryEnter(int timeoutMs)
    {
        var me = Environment.CurrentManagedThreadId;
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_sync)
        {
            while (_depth > 0 && _ownerThread != me)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_sync, (int)remaining);
            }
            _ownerThread = me;
            _depth++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            if (_depth == 0 || _ownerThread != Environment.CurrentManagedThreadId) return;
            if (--_depth == 0) { _ownerThread = 0; Monitor.PulseAll(_sync); }
        }
    }

    public bool IsHeldByCurrentThread
    {
        get { lock (_sync) return _depth > 0 && _ownerThread == Environment.CurrentManagedThreadId; }
    }

    /// <summary>Releases every level held by the current thread.</summary>
    public void ExitAll()
    {
        lock (_sync)
        {
            if (_depth == 0 || _ownerThread != Environment.CurrentManagedThreadId) return;
            _depth = 0;
            _ownerThread = 0;
            Monitor.PulseAll(_sync);
        }
    }
}
