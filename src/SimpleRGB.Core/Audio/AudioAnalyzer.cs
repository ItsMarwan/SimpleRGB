using NAudio.Dsp;
using SimpleRGB.Core.Lighting;

namespace SimpleRGB.Core.Audio;

/// <summary>A snapshot of what the music is doing right now. All levels are 0..1.</summary>
public sealed class AudioFrame
{
    public static readonly AudioFrame Silent = new() { Spectrum = new float[AudioAnalyzer.SpectrumBands] };

    public double Bass { get; init; }
    public double Mid { get; init; }
    public double Treble { get; init; }
    public double Volume { get; init; }
    /// <summary>Where the sound's energy sits: 0 = deep bass … 1 = high treble.</summary>
    public double Centroid { get; init; }
    /// <summary>16 log-spaced bands, 40 Hz → 16 kHz.</summary>
    public float[] Spectrum { get; init; } = Array.Empty<float>();
    /// <summary>Increments on every detected beat (drives "new color every beat").</summary>
    public ulong BeatCount { get; init; }
    /// <summary><see cref="LightingClock"/> time of the last beat.</summary>
    public double LastBeatTime { get; init; } = double.NegativeInfinity;
    public bool HasSignal { get; init; }

    /// <summary>1 right on a beat, fading out over ~150 ms.</summary>
    public double BeatFlash(double now)
    {
        var dt = now - LastBeatTime;
        return dt is < 0 or > 1 ? 0 : Math.Exp(-dt / 0.14);
    }
}

/// <summary>
/// Turns raw audio into music features: band levels with automatic gain, onset-based beat detection,
/// a 16-band spectrum and the spectral centroid. Feed it from a single thread.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int SpectrumBands = 16;
    private const int FftLog2 = 11;              // 2048-point FFT
    private const int FftSize = 1 << FftLog2;
    private const int Hop = 512;                 // ~10.7 ms at 48 kHz
    private const int HistoryLength = 96;        // ~1 s of bass energy for beat detection

    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _magnitudes = new double[FftSize / 2];
    private readonly double[] _bassHistory = new double[HistoryLength];
    private readonly object _sync = new();
    private int _ringPos, _sinceHop, _historyPos, _historyCount;
    private int _sampleRate = 48000;

    private readonly AutoGain _bassGain = new(), _midGain = new(), _trebleGain = new(), _volumeGain = new();
    private readonly AutoGain[] _bandGains = Enumerable.Range(0, SpectrumBands).Select(_ => new AutoGain()).ToArray();
    private double _bass, _mid, _treble, _volume, _centroid;
    private readonly float[] _spectrum = new float[SpectrumBands];
    private ulong _beats;
    private double _lastBeat = double.NegativeInfinity;
    private double _prevBass;
    private volatile AudioFrame _current = AudioFrame.Silent;

    public AudioAnalyzer()
    {
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)))); // Hann
    }

    public AudioFrame Current => _current;

    /// <summary>Time source; replaceable in tests.</summary>
    public Func<double> Clock { get; set; } = () => LightingClock.Seconds;

    public void Feed(ReadOnlySpan<float> mono, int sampleRate)
    {
        lock (_sync)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            foreach (var s in mono)
            {
                _ring[_ringPos] = float.IsFinite(s) ? s : 0f;
                _ringPos = (_ringPos + 1) & (FftSize - 1);
                if (++_sinceHop >= Hop)
                {
                    _sinceHop = 0;
                    Analyze();
                }
            }
        }
    }

    /// <summary>Keeps levels decaying when the audio device sends nothing (Windows sends no data during silence).</summary>
    public void FeedSilence(int samples)
    {
        Span<float> zeros = stackalloc float[256];
        zeros.Clear();
        while (samples > 0)
        {
            var n = Math.Min(samples, zeros.Length);
            Feed(zeros[..n], _sampleRate);
            samples -= n;
        }
    }

    private void Analyze()
    {
        double sumSquares = 0;
        for (var i = 0; i < FftSize; i++)
        {
            var v = _ring[(_ringPos + i) & (FftSize - 1)];
            if (i >= FftSize - Hop) sumSquares += v * v;
            _fft[i].X = v * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftLog2, _fft);
        for (var k = 0; k < _magnitudes.Length; k++)
            _magnitudes[k] = Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);

        var rms = Math.Sqrt(sumSquares / Hop);
        var hasSignal = rms > 2e-4;

        var bassRaw = Band(30, 150);
        var midRaw = Band(300, 2500);
        var trebleRaw = Band(5000, 14000);

        var bass = _bassGain.Normalize(bassRaw, hasSignal);
        var mid = _midGain.Normalize(midRaw, hasSignal);
        var treble = _trebleGain.Normalize(trebleRaw, hasSignal);
        var volume = _volumeGain.Normalize(rms, hasSignal);

        _bass = Smooth(_bass, bass);
        _mid = Smooth(_mid, mid);
        _treble = Smooth(_treble, treble);
        _volume = Smooth(_volume, volume);

        // 16 log-spaced bands, 40 Hz – 16 kHz.
        double weighted = 0, total = 0;
        for (var b = 0; b < SpectrumBands; b++)
        {
            var lo = 40 * Math.Pow(400, b / (double)SpectrumBands);
            var hi = 40 * Math.Pow(400, (b + 1) / (double)SpectrumBands);
            var raw = Band(lo, hi);
            var level = _bandGains[b].Normalize(raw, hasSignal);
            _spectrum[b] = (float)Smooth(_spectrum[b], level);
            weighted += raw * b;
            total += raw;
        }
        if (hasSignal && total > 0)
            _centroid += ((weighted / total / (SpectrumBands - 1)) - _centroid) * 0.15;

        DetectBeat(bassRaw, hasSignal);

        _current = new AudioFrame
        {
            Bass = _bass,
            Mid = _mid,
            Treble = _treble,
            Volume = _volume,
            Centroid = _centroid,
            Spectrum = (float[])_spectrum.Clone(),
            BeatCount = _beats,
            LastBeatTime = _lastBeat,
            HasSignal = hasSignal,
        };
    }

    private void DetectBeat(double bassRaw, bool hasSignal)
    {
        double mean = 0;
        for (var i = 0; i < _historyCount; i++) mean += _bassHistory[i];
        mean = _historyCount > 0 ? mean / _historyCount : 0;

        var now = Clock();
        var rising = bassRaw > _prevBass;
        if (hasSignal && _historyCount >= HistoryLength / 3 && rising &&
            bassRaw > mean * 1.45 && bassRaw > _bassGain.Peak * 0.25 && now - _lastBeat > 0.20)
        {
            _beats++;
            _lastBeat = now;
        }

        _prevBass = bassRaw;
        _bassHistory[_historyPos] = bassRaw;
        _historyPos = (_historyPos + 1) % HistoryLength;
        if (_historyCount < HistoryLength) _historyCount++;
    }

    private double Band(double loHz, double hiHz)
    {
        var binHz = _sampleRate / (double)FftSize;
        var lo = Math.Clamp((int)(loHz / binHz), 1, _magnitudes.Length - 1);
        var hi = Math.Clamp((int)Math.Ceiling(hiHz / binHz), lo + 1, _magnitudes.Length);
        double sum = 0;
        for (var k = lo; k < hi; k++) sum += _magnitudes[k] * _magnitudes[k];
        return Math.Sqrt(sum / (hi - lo));
    }

    /// <summary>Fast attack, slower release: punchy but not flickery.</summary>
    private static double Smooth(double current, double target) =>
        current + (target - current) * (target > current ? 0.6 : 0.16);

    /// <summary>Tracks a decaying peak so quiet and loud songs both use the full 0..1 range.</summary>
    private sealed class AutoGain
    {
        public double Peak { get; private set; } = 1e-6;

        public double Normalize(double value, bool hasSignal)
        {
            if (!hasSignal) return 0;
            Peak = Math.Max(value, Peak * 0.9985); // half-life ≈ 5 s
            Peak = Math.Max(Peak, 1e-6);
            return Math.Clamp(value / Peak, 0, 1);
        }
    }
}
