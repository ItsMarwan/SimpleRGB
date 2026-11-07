using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Audio;

/// <summary>Delivers mono float samples to a callback until disposed.</summary>
internal interface IAudioCapture : IDisposable
{
    /// <summary>Set if capture died (device removed, process exited…).</summary>
    bool Failed { get; }
}

/// <summary>Everything the PC plays (default output device), via NAudio's WASAPI loopback.</summary>
internal sealed class SystemLoopbackCapture : IAudioCapture
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly Action<float[], int, int> _sink;
    private float[] _mono = Array.Empty<float>();

    public bool Failed { get; private set; }

    public SystemLoopbackCapture(Action<float[], int, int> sink)
    {
        _sink = sink;
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Warn("Audio", $"PC audio capture stopped: {e.Exception.Message}");
            Failed = true;
        };
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var f = _capture.WaveFormat;
            var channels = Math.Max(1, f.Channels);
            var bytesPerSample = f.BitsPerSample / 8;
            if (bytesPerSample == 0) return;
            var frames = e.BytesRecorded / (bytesPerSample * channels);
            if (_mono.Length < frames) _mono = new float[frames];

            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var ch = 0; ch < channels; ch++)
                {
                    var o = (i * channels + ch) * bytesPerSample;
                    sum += (f.Encoding, f.BitsPerSample) switch
                    {
                        (WaveFormatEncoding.IeeeFloat, 32) or (WaveFormatEncoding.Extensible, 32) => BitConverter.ToSingle(e.Buffer, o),
                        (_, 16) => BitConverter.ToInt16(e.Buffer, o) / 32768f,
                        (_, 24) => ((e.Buffer[o] << 8 | e.Buffer[o + 1] << 16 | e.Buffer[o + 2] << 24) >> 8) / 8388608f,
                        (_, 32) => BitConverter.ToInt32(e.Buffer, o) / 2147483648f,
                        _ => 0,
                    };
                }
                _mono[i] = sum / channels;
            }
            _sink(_mono, frames, f.SampleRate);
        }
        catch (Exception ex)
        {
            Log.Warn("Audio", $"Audio data error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _capture.DataAvailable -= OnData; _capture.StopRecording(); } catch { /* already stopped */ }
        _capture.Dispose();
    }
}

/// <summary>
/// Audio from one program (and its child processes) only, using Windows' process loopback
/// (Windows 10 2004 / build 20348+, Windows 11). This is how "Spotify only" ignores games and calls.
/// </summary>
internal sealed class ProcessLoopbackCapture : IAudioCapture
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _captureClient;
    private readonly Thread _thread;
    private readonly Action<float[], int, int> _sink;
    private volatile bool _stop;

    public bool Failed { get; private set; }

    private ProcessLoopbackCapture(IAudioClient client, IAudioCaptureClient captureClient, Action<float[], int, int> sink)
    {
        _client = client;
        _captureClient = captureClient;
        _sink = sink;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SimpleRGB process audio" };
        _thread.Start();
    }

    public static ProcessLoopbackCapture? TryStart(int processId, Action<float[], int, int> sink, out string? error)
    {
        error = null;
        try
        {
            var client = Activate(processId);
            var format = new WaveFormatEx
            {
                wFormatTag = 3, // IEEE float
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = Channels * 4,
                nAvgBytesPerSec = SampleRate * Channels * 4,
                cbSize = 0,
            };
            var fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, fmt, false);
                const uint flags = 0x00020000 /*LOOPBACK*/ | 0x80000000 /*AUTOCONVERTPCM*/ | 0x08000000 /*SRC_DEFAULT_QUALITY*/;
                Check(client.Initialize(0 /*shared*/, flags, 2_000_000 /*200 ms*/, 0, fmt, IntPtr.Zero), "Initialize");
            }
            finally
            {
                Marshal.FreeHGlobal(fmt);
            }

            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var service), "GetService");
            var capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
            return new ProcessLoopbackCapture(client, capture, sink);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void Loop()
    {
        var mono = new float[SampleRate / 10];
        try
        {
            while (!_stop)
            {
                Thread.Sleep(8);
                while (!_stop)
                {
                    if (_captureClient.GetNextPacketSize(out var packet) < 0) { Failed = true; return; }
                    if (packet == 0) break;
                    if (_captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) { Failed = true; return; }
                    var n = (int)Math.Min(frames, (uint)mono.Length);
                    if ((flags & 0x2) != 0 || data == IntPtr.Zero) // AUDCLNT_BUFFERFLAGS_SILENT
                    {
                        Array.Clear(mono, 0, n);
                    }
                    else
                    {
                        unsafe
                        {
                            var p = (float*)data;
                            for (var i = 0; i < n; i++) mono[i] = (p[i * 2] + p[i * 2 + 1]) * 0.5f;
                        }
                    }
                    _captureClient.ReleaseBuffer(frames);
                    if (n > 0) _sink(mono, n, SampleRate);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audio", $"Program audio capture stopped: {ex.Message}");
            Failed = true;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        try { _client.Stop(); } catch { /* ignore */ }
        try { Marshal.ReleaseComObject(_captureClient); } catch { /* ignore */ }
        try { Marshal.ReleaseComObject(_client); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- activation

    private static IAudioClient Activate(int processId)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propVariant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            // PROPVARIANT { VT_BLOB, reserved×3, BLOB { cbSize, pBlobData } }
            for (var i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, 65); // VT_BLOB
            Marshal.WriteInt32(propVariant, 8, paramsSize);
            Marshal.WriteIntPtr(propVariant, 16, paramsPtr);

            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, propVariant, handler, out var operation);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Windows did not answer the audio activation request.");
            Check(operation.GetActivateResult(out var hr, out var result), "GetActivateResult");
            Check(hr, "ActivateAudioInterfaceAsync");
            return (IAudioClient)result;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }
}
