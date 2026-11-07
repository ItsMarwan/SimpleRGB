using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static SimpleRGB.Core.Hardware.Hid.HidNative;

namespace SimpleRGB.Core.Hardware.Hid;

/// <summary>Operations a plugin can perform on an opened HID endpoint. Mirrors hidapi semantics.</summary>
public interface IHidConnection : IDisposable
{
    HidEndpointInfo Endpoint { get; }

    /// <summary>True once Windows reported that the device is gone.</summary>
    bool IsDisconnected { get; }

    /// <summary>Interrupt/output write. data[0] is the report id. Returns bytes written or -1.</summary>
    int Write(ReadOnlySpan<byte> data);

    /// <summary>Interrupt read with timeout. Returns bytes read (0 on timeout) or -1 on error.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Feature report write. data[0] is the report id.</summary>
    int SendFeatureReport(ReadOnlySpan<byte> data);

    /// <summary>Feature report read. buffer[0] must hold the report id. Returns bytes or -1.</summary>
    int GetFeatureReport(Span<byte> buffer);

    /// <summary>HidD_GetInputReport. buffer[0] must hold the report id.</summary>
    int GetInputReport(Span<byte> buffer);

    /// <summary>Discards queued input reports.</summary>
    void FlushInput();
}

/// <summary>Real Windows HID endpoint using overlapped I/O so every call has a timeout.</summary>
public sealed unsafe class HidConnection : IHidConnection
{
    private const uint WriteTimeoutMs = 1000;

    private readonly SafeFileHandle _handle;
    private readonly SafeWaitHandle _readEvent;
    private readonly SafeWaitHandle _writeEvent;
    private readonly object _ioLock = new();
    private bool _disposed;

    public HidEndpointInfo Endpoint { get; }
    public bool IsDisconnected { get; private set; }

    private HidConnection(HidEndpointInfo endpoint, SafeFileHandle handle)
    {
        Endpoint = endpoint;
        _handle = handle;
        _readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        _writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
    }

    /// <summary>Opens an endpoint for read/write. Returns null (with a reason) if Windows refuses.</summary>
    public static HidConnection? Open(HidEndpointInfo endpoint, out string? error)
    {
        error = null;
        var handle = CreateFileW(endpoint.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            // Same fallback as hidapi: some collections only allow feature reports with no access rights.
            handle = CreateFileW(endpoint.Path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                error = $"Windows error {err} opening {endpoint}";
                return null;
            }
        }
        return new HidConnection(endpoint, handle);
    }

    public int Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return -1;
        // hidapi: pad short writes up to the output report length; longer writes are sent as-is.
        var length = Math.Max(data.Length, Endpoint.OutputReportLength);
        var buffer = new byte[length];
        data.CopyTo(buffer);

        lock (_ioLock)
        {
            if (_disposed) return -1;
            var ov = new NativeOverlapped { EventHandle = _writeEvent.DangerousGetHandle() };
            ResetEvent(_writeEvent);
            fixed (byte* p = buffer)
            {
                if (!WriteFile(_handle, p, length, IntPtr.Zero, &ov))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return Fail(err);
                    if (WaitForSingleObject(_writeEvent, WriteTimeoutMs) != WAIT_OBJECT_0)
                        CancelIoEx(_handle, &ov);
                }
                // Always wait for completion so the buffer/overlapped are never used after we leave.
                if (!GetOverlappedResult(_handle, &ov, out var written, true))
                    return Fail(Marshal.GetLastWin32Error());
                return written;
            }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var reportLength = Endpoint.InputReportLength;
        if (reportLength <= 0) return -1;
        var raw = new byte[reportLength];
        int bytesRead;

        lock (_ioLock)
        {
            if (_disposed) return -1;
            var ov = new NativeOverlapped { EventHandle = _readEvent.DangerousGetHandle() };
            ResetEvent(_readEvent);
            fixed (byte* p = raw)
            {
                if (!ReadFile(_handle, p, reportLength, IntPtr.Zero, &ov))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return Fail(err);
                    var wait = (uint)Math.Clamp(timeoutMs, 0, 30_000);
                    if (WaitForSingleObject(_readEvent, wait) != WAIT_OBJECT_0)
                        CancelIoEx(_handle, &ov);
                }
                if (!GetOverlappedResult(_handle, &ov, out bytesRead, true))
                {
                    var err = Marshal.GetLastWin32Error();
                    return err == ERROR_OPERATION_ABORTED ? 0 : Fail(err);
                }
            }
        }

        // hidapi: when the device doesn't use report ids (first byte 0) strip that byte.
        var offset = 0;
        if (bytesRead > 0 && raw[0] == 0) { offset = 1; bytesRead--; }
        var count = Math.Min(bytesRead, buffer.Length);
        raw.AsSpan(offset, count).CopyTo(buffer);
        return count;
    }

    public int SendFeatureReport(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return -1;
        var length = Math.Max(data.Length, Endpoint.FeatureReportLength);
        var buffer = new byte[length];
        data.CopyTo(buffer);
        lock (_ioLock)
        {
            if (_disposed) return -1;
            fixed (byte* p = buffer)
                return HidD_SetFeature(_handle, p, length) ? data.Length : Fail(Marshal.GetLastWin32Error());
        }
    }

    public int GetFeatureReport(Span<byte> buffer)
    {
        if (buffer.Length == 0) return -1;
        var length = Math.Max(buffer.Length, Endpoint.FeatureReportLength);
        var raw = new byte[length];
        raw[0] = buffer[0];
        lock (_ioLock)
        {
            if (_disposed) return -1;
            fixed (byte* p = raw)
                if (!HidD_GetFeature(_handle, p, length)) return Fail(Marshal.GetLastWin32Error());
        }
        var count = Math.Min(buffer.Length, length);
        raw.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public int GetInputReport(Span<byte> buffer)
    {
        if (buffer.Length == 0) return -1;
        var length = Math.Max(buffer.Length, Endpoint.InputReportLength);
        var raw = new byte[length];
        raw[0] = buffer[0];
        lock (_ioLock)
        {
            if (_disposed) return -1;
            fixed (byte* p = raw)
                if (!HidD_GetInputReport(_handle, p, length)) return Fail(Marshal.GetLastWin32Error());
        }
        var count = Math.Min(buffer.Length, length);
        raw.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public void FlushInput()
    {
        lock (_ioLock)
        {
            if (!_disposed) HidD_FlushQueue(_handle);
        }
    }

    private int Fail(int win32Error)
    {
        // Only errors that unambiguously mean "unplugged". ERROR_GEN_FAILURE / ERROR_BAD_COMMAND can
        // also be a single stalled transfer, which must not tear the device down.
        if (win32Error is ERROR_DEVICE_NOT_CONNECTED or ERROR_FILE_NOT_FOUND)
            IsDisconnected = true;
        return -1;
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
            _readEvent.Dispose();
            _writeEvent.Dispose();
        }
    }
}
