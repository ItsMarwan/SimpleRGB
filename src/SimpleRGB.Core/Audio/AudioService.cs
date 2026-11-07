using System.Diagnostics;
using System.Text.Json.Serialization;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Audio;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioSource
{
    /// <summary>Only Spotify's audio (falls back to all PC audio if Windows can't isolate it).</summary>
    Spotify,
    /// <summary>Everything the PC plays.</summary>
    AllAudio,
}

/// <summary>
/// Listens to music only while some zone is in Music mode, so it costs nothing otherwise.
/// Picks the source, follows Spotify starting/stopping, and recovers from audio device changes.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly AudioAnalyzer _analyzer = new();
    private readonly object _sync = new();
    private Thread? _worker;
    private volatile bool _active;
    private volatile bool _disposed;
    private volatile AudioSource _source = AudioSource.Spotify;
    private volatile bool _restartRequested;
    private IAudioCapture? _capture;
    private int _capturedPid;
    private bool _capturingSystem;
    private long _lastDataTick;
    private string _status = "Off";

    public AudioFrame Current => _active ? _analyzer.Current : AudioFrame.Silent;
    public string Status { get { lock (_sync) return _status; } }
    public bool IsActive => _active;

    public event Action? StatusChanged;

    public AudioSource Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value;
            _restartRequested = true; // handled on the audio thread so the UI never waits on Windows audio setup
        }
    }

    /// <summary>Turns listening on or off. Cheap to call repeatedly.</summary>
    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        lock (_sync)
        {
            // One long-lived worker that idles while inactive avoids start/stop races.
            if (_worker == null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "SimpleRGB audio" };
                _worker.Start();
            }
        }
        Log.Info("Audio", active ? "Music mode on: listening." : "Music mode off: stopped listening.");
    }

    private void Run()
    {
        var lastCheck = 0L;
        var wasActive = false;
        while (!_disposed)
        {
            if (!_active)
            {
                if (wasActive)
                {
                    lock (_sync) StopCapture();
                    SetStatus("Off");
                    wasActive = false;
                }
                Thread.Sleep(50);
                continue;
            }
            if (!wasActive) { wasActive = true; lastCheck = 0; }

            try
            {
                var now = Environment.TickCount64;
                if (_restartRequested)
                {
                    _restartRequested = false;
                    lock (_sync) StopCapture();
                    lastCheck = 0;
                }
                // Re-evaluate every 2 s (Spotify started/stopped) or right away if capture died.
                if (now - lastCheck > 2000 || _capture is { Failed: true })
                {
                    lastCheck = now;
                    EnsureCapture();
                }

                // Windows sends nothing while it's quiet; keep the levels falling smoothly.
                if (now - Interlocked.Read(ref _lastDataTick) > 60)
                    _analyzer.FeedSilence(512);
            }
            catch (Exception ex)
            {
                Log.Warn("Audio", $"Audio worker error: {ex.Message}");
            }
            Thread.Sleep(10);
        }

        lock (_sync) StopCapture();
    }

    private void EnsureCapture()
    {
        lock (_sync)
        {
            if (_capture is { Failed: true }) StopCapture();

            if (_source == AudioSource.Spotify)
            {
                var pid = FindSpotify();
                if (pid == 0)
                {
                    if (_capture != null) StopCapture();
                    SetStatus("Waiting for Spotify… (start a song)");
                    return;
                }
                if (_capture != null && !_capturingSystem && _capturedPid == pid) return;
                if (_capture != null && _capturingSystem) return; // fallback already running

                StopCapture();
                var capture = ProcessLoopbackCapture.TryStart(pid, OnSamples, out var error);
                if (capture != null)
                {
                    _capture = capture;
                    _capturedPid = pid;
                    _capturingSystem = false;
                    SetStatus("Listening to Spotify");
                    return;
                }
                Log.Warn("Audio", $"Couldn't isolate Spotify's audio ({error}); using all PC audio instead.");
                StartSystem("Listening to all PC audio (Windows couldn't isolate Spotify)");
                return;
            }

            if (_capture != null && _capturingSystem) return;
            StopCapture();
            StartSystem("Listening to all PC audio");
        }
    }

    private void StartSystem(string status)
    {
        try
        {
            _capture = new SystemLoopbackCapture(OnSamples);
            _capturingSystem = true;
            _capturedPid = 0;
            SetStatus(status);
        }
        catch (Exception ex)
        {
            _capture = null;
            SetStatus("No audio output device found");
            Log.Warn("Audio", $"PC audio capture failed: {ex.Message}");
        }
    }

    private void StopCapture()
    {
        try { _capture?.Dispose(); } catch { /* ignore */ }
        _capture = null;
        _capturedPid = 0;
        _capturingSystem = false;
    }

    private void OnSamples(float[] buffer, int count, int sampleRate)
    {
        Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
        _analyzer.Feed(buffer.AsSpan(0, count), sampleRate);
    }

    /// <summary>The root Spotify process (its children are included by process loopback).</summary>
    private static int FindSpotify()
    {
        var best = 0;
        var bestStart = DateTime.MaxValue;
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            try
            {
                var start = p.StartTime;
                if (start < bestStart) { bestStart = start; best = p.Id; }
            }
            catch { if (best == 0) best = p.Id; }
            finally { p.Dispose(); }
        }
        return best;
    }

    private void SetStatus(string status)
    {
        bool changed;
        lock (_sync)
        {
            changed = _status != status;
            _status = status;
        }
        if (!changed) return;
        Log.Info("Audio", status);
        try { StatusChanged?.Invoke(); } catch { /* listener errors must not stop audio */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _active = false;
        _worker?.Join(1000);
        lock (_sync) StopCapture();
    }
}
