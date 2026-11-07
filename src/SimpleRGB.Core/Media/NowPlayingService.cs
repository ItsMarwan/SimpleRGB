using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Lighting;
using Windows.Graphics.Imaging;
using Windows.Media.Control;

namespace SimpleRGB.Core.Media;

public sealed record NowPlaying(string Title, string Artist, string App, bool IsPlaying, IReadOnlyList<RgbColor> Palette)
{
    public static NowPlaying None { get; } = new("", "", "", false, Array.Empty<RgbColor>());
    public bool HasTrack => Title.Length > 0;
}

/// <summary>
/// Reads the current song and its album art from Windows' media controls (the same info shown on the
/// volume flyout). No Spotify login needed. Prefers Spotify's session when several apps play media.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private readonly object _sync = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private volatile NowPlaying _current = NowPlaying.None;
    private string _paletteKey = "";
    private string _trackKey = "";
    private bool _started;
    private int _refreshVersion;

    public NowPlaying Current => _current;
    public event Action? Changed;

    public async Task StartAsync()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = RefreshAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = RefreshAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"Now-playing info unavailable: {ex.Message}");
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions();
        return sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
               ?? _manager.GetCurrentSession();
    }

    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        try
        {
            var session = PickSession();
            if (!ReferenceEquals(session, _session))
            {
                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= OnSessionChanged;
                    _session.PlaybackInfoChanged -= OnSessionChanged;
                }
                _session = session;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged += OnSessionChanged;
                    _session.PlaybackInfoChanged += OnSessionChanged;
                }
            }

            if (session == null)
            {
                Publish(NowPlaying.None);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var app = session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ? "Spotify" : session.SourceAppUserModelId;
            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";

            // The palette follows the actual image (Spotify often swaps the artwork a moment after the title),
            // so it's keyed on the pixels, not the track name.
            var palette = _current.Palette;
            if (props?.Thumbnail != null && await ReadPixelsAsync(props.Thumbnail) is { } pixels)
            {
                var hash = Hash(pixels);
                if (hash != _paletteKey)
                {
                    _paletteKey = hash;
                    palette = AlbumPalette.Extract(pixels);
                }
            }
            else if (props?.Thumbnail == null)
            {
                palette = Array.Empty<RgbColor>();
                _paletteKey = "";
            }

            var trackKey = $"{app}|{title}|{artist}";
            if (trackKey != _trackKey)
            {
                _trackKey = trackKey;
                // Look at the artwork again shortly after a track change in case it arrives late.
                _ = Task.Delay(1500).ContinueWith(_ => RefreshAsync());
                _ = Task.Delay(4000).ContinueWith(_ => RefreshAsync());
            }

            if (version == _refreshVersion)
                Publish(new NowPlaying(title, artist, app, playing, palette));
        }
        catch (Exception ex)
        {
            Log.Debug("Media", $"Now-playing refresh failed: {ex.Message}");
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private void Publish(NowPlaying value)
    {
        if (_current == value) return;
        _current = value;
        try { Changed?.Invoke(); } catch { /* ignore listener errors */ }
    }

    private static async Task<byte[]?> ReadPixelsAsync(Windows.Storage.Streams.IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 64, ScaledHeight = 64, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return pixels.DetachPixelData();
        }
        catch (Exception ex)
        {
            Log.Debug("Media", $"Album art unreadable: {ex.Message}");
            return null;
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data));

    public void Dispose()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }
    }
}

/// <summary>
/// Finds the colors you actually see on album art: pixels are clustered (k-means in OKLab, a perceptual
/// color space), clusters are ranked by how much of the cover they fill (with a bonus for colorful ones),
/// and each is turned into an LED color that keeps its real hue and saturation.
/// </summary>
public static class AlbumPalette
{
    private const int K = 6;

    public static IReadOnlyList<RgbColor> Extract(byte[] bgra)
    {
        var count = bgra.Length / 4;
        if (count == 0) return Array.Empty<RgbColor>();

        var lab = new (double L, double A, double B)[count];
        for (var i = 0; i < count; i++)
            lab[i] = ToOkLab(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);

        var clusters = KMeans(lab);
        var total = (double)count;

        var ranked = clusters
            .Where(c => c.Count > 0)
            .Select(c =>
            {
                var chroma = Math.Sqrt(c.A * c.A + c.B * c.B);
                var share = c.Count / total;
                return (c.L, c.A, c.B, Share: share, Chroma: chroma);
            })
            .ToList();

        // Colorful regions (chroma ≥ 0.05) that aren't near-black. Ranked by area, with a colorfulness bonus.
        var colorful = ranked
            .Where(c => c.Chroma >= 0.05 && c.L >= 0.25 && c.Share >= 0.03)
            .OrderByDescending(c => c.Share * (0.6 + Math.Min(c.Chroma, 0.25) * 4))
            .ToList();

        var result = new List<RgbColor>();
        foreach (var c in colorful)
        {
            var color = ToLedColor(c.L, c.A, c.B);
            // Skip near-duplicates (hue within ~25° and similar saturation).
            if (result.Any(r => Similar(r, color))) continue;
            result.Add(color);
            if (result.Count == 4) break;
        }

        if (result.Count == 0)
        {
            // Black-and-white / grey cover: white light is the honest match.
            return new[] { RgbColor.White };
        }

        // A one-color cover still needs something to switch between on each beat: add close neighbours.
        if (result.Count == 1)
        {
            var (h, s, _) = result[0].ToHsv();
            result.Add(RgbColor.FromHsv(h + 0.06, s, 1));
            result.Add(RgbColor.FromHsv(h - 0.06 + 1, Math.Min(1, s + 0.1), 1));
        }
        return result;
    }

    private static bool Similar(RgbColor a, RgbColor b)
    {
        var (h1, s1, _) = a.ToHsv();
        var (h2, s2, _) = b.ToHsv();
        var dh = Math.Abs(h1 - h2);
        dh = Math.Min(dh, 1 - dh);
        return dh < 0.07 && Math.Abs(s1 - s2) < 0.3;
    }

    /// <summary>Full brightness (LEDs can't show "dark"), same hue, saturation kept but slightly lifted.</summary>
    private static RgbColor ToLedColor(double l, double a, double b)
    {
        var (r, g, bl) = FromOkLab(l, a, b);
        var (h, s, _) = new RgbColor(r, g, bl).ToHsv();
        s = Math.Clamp(s * 1.1 + 0.05, 0, 1);
        return RgbColor.FromHsv(h, s, 1);
    }

    private sealed class Cluster
    {
        public double L, A, B;
        public int Count;
    }

    private static List<Cluster> KMeans((double L, double A, double B)[] px)
    {
        // Deterministic k-means++ style seeding: start from the pixel nearest the mean, then pick the farthest.
        var centers = new List<(double L, double A, double B)>();
        double ml = 0, ma = 0, mb = 0;
        foreach (var p in px) { ml += p.L; ma += p.A; mb += p.B; }
        ml /= px.Length; ma /= px.Length; mb /= px.Length;
        centers.Add(px.MinBy(p => Dist(p, (ml, ma, mb))));
        while (centers.Count < Math.Min(K, px.Length))
            centers.Add(px.MaxBy(p => centers.Min(c => Dist(p, c))));

        var assign = new int[px.Length];
        var clusters = new List<Cluster>();
        for (var iter = 0; iter < 12; iter++)
        {
            for (var i = 0; i < px.Length; i++)
            {
                var best = 0;
                var bestD = double.MaxValue;
                for (var k = 0; k < centers.Count; k++)
                {
                    var d = Dist(px[i], centers[k]);
                    if (d < bestD) { bestD = d; best = k; }
                }
                assign[i] = best;
            }
            clusters = centers.Select(_ => new Cluster()).ToList();
            for (var i = 0; i < px.Length; i++)
            {
                var c = clusters[assign[i]];
                c.L += px[i].L; c.A += px[i].A; c.B += px[i].B; c.Count++;
            }
            for (var k = 0; k < clusters.Count; k++)
            {
                var c = clusters[k];
                if (c.Count == 0) continue;
                c.L /= c.Count; c.A /= c.Count; c.B /= c.Count;
                centers[k] = (c.L, c.A, c.B);
            }
        }
        return clusters;
    }

    private static double Dist((double L, double A, double B) p, (double L, double A, double B) q) =>
        (p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B);

    private static double ToLinear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte FromLinear(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }

    private static (double L, double A, double B) ToOkLab(byte r8, byte g8, byte b8)
    {
        double r = ToLinear(r8), g = ToLinear(g8), b = ToLinear(b8);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (byte R, byte G, byte B) FromOkLab(double L, double a, double b)
    {
        var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }
}
