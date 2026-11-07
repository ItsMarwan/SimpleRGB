using System.Windows;
using System.Windows.Media;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Lighting;

namespace SimpleRGB.Controls;

/// <summary>
/// A live row of LEDs showing exactly what a zone (or an effect) looks like right now.
/// Animates only while visible and only if the lighting actually moves.
/// </summary>
public sealed class LedStrip : FrameworkElement
{
    public static readonly DependencyProperty LightingProperty = DependencyProperty.Register(
        nameof(Lighting), typeof(ZoneLighting), typeof(LedStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(LightingSnapshot), typeof(LedStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    public static readonly DependencyProperty ZoneIdProperty = DependencyProperty.Register(
        nameof(ZoneId), typeof(string), typeof(LedStrip), new FrameworkPropertyMetadata("device", FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    public static readonly DependencyProperty LedCountProperty = DependencyProperty.Register(
        nameof(LedCount), typeof(int), typeof(LedStrip), new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    public static readonly DependencyProperty LedSpacingProperty = DependencyProperty.Register(
        nameof(LedSpacing), typeof(double), typeof(LedStrip), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Music levels / album colors for previews (set once by the app).</summary>
    public static IEffectInputs? Inputs { get; set; }

    private bool _subscribed;
    private TimeSpan _lastFrame;
    private RgbColor[] _buffer = new RgbColor[12];

    public LedStrip()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    public ZoneLighting? Lighting { get => (ZoneLighting?)GetValue(LightingProperty); set => SetValue(LightingProperty, value); }
    public LightingSnapshot? Snapshot { get => (LightingSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public string ZoneId { get => (string)GetValue(ZoneIdProperty); set => SetValue(ZoneIdProperty, value); }
    public int LedCount { get => (int)GetValue(LedCountProperty); set => SetValue(LedCountProperty, value); }
    public double LedSpacing { get => (double)GetValue(LedSpacingProperty); set => SetValue(LedSpacingProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LedStrip)d).UpdateSubscription();

    private bool IsAnimated =>
        Lighting?.IsAnimated == true || (Snapshot is { } s && !s.LightsOff && s.ForZone(ZoneId ?? "device").IsAnimated);

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && IsAnimated)
        {
            if (_subscribed) return;
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
        else
        {
            Unsubscribe();
        }
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t - _lastFrame < TimeSpan.FromMilliseconds(33)) return; // ~30 fps is plenty for a preview
        _lastFrame = t;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var count = Math.Clamp(LedCount, 1, 64);
        if (_buffer.Length != count) _buffer = new RgbColor[count];

        if (Lighting is { } lighting)
            LightingPreview.Fill(lighting, _buffer, lighting.Mode == LightingMode.Music && Inputs?.Audio.HasSignal != true ? DemoMusic.Instance : Inputs);
        else if (Snapshot is { } snapshot) LightingPreview.Fill(snapshot, ZoneId ?? "device", _buffer, Inputs);
        else Array.Fill(_buffer, new RgbColor(40, 42, 48));

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var gap = count > 1 ? LedSpacing : 0;
        var ledW = Math.Max(1, (w - gap * (count - 1)) / count);
        var radius = Math.Min(ledW, h) / 2.5;

        for (var i = 0; i < count; i++)
        {
            var c = _buffer[i];
            var x = i * (ledW + gap);
            var rect = new Rect(x, 0, ledW, h);
            var lum = Math.Max(c.R, Math.Max(c.G, c.B));
            // Dark base so "off" LEDs still read as LEDs.
            dc.DrawRoundedRectangle(OffBrush, null, rect, radius, radius);
            if (lum > 6)
            {
                var color = Color.FromRgb(c.R, c.G, c.B);
                var glow = new SolidColorBrush(Color.FromArgb((byte)(70 * lum / 255), c.R, c.G, c.B));
                glow.Freeze();
                var glowRect = rect;
                glowRect.Inflate(1.5, 1.5);
                dc.DrawRoundedRectangle(glow, null, glowRect, radius + 1.5, radius + 1.5);
                var fill = new SolidColorBrush(color);
                fill.Freeze();
                dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            }
        }
    }

    /// <summary>A pretend song (120 BPM) so the Music tile shows what it does while nothing plays.</summary>
    private sealed class DemoMusic : IEffectInputs
    {
        public static readonly DemoMusic Instance = new();
        public Core.Audio.AudioFrame Audio
        {
            get
            {
                var t = LightingClock.Seconds;
                var beat = Math.Floor(t * 2);
                var since = t * 2 - beat;
                var bands = new float[16];
                for (var i = 0; i < 16; i++) bands[i] = (float)(0.35 + 0.35 * Math.Sin(t * 3 + i * 0.7) * Math.Exp(-since * 2));
                return new Core.Audio.AudioFrame
                {
                    Bass = Math.Exp(-since * 4), Mid = 0.5, Treble = 0.4, Volume = 0.6 + 0.4 * Math.Exp(-since * 3),
                    Centroid = 0.5 + 0.3 * Math.Sin(t), Spectrum = bands, BeatCount = (ulong)beat,
                    LastBeatTime = beat / 2, HasSignal = true,
                };
            }
        }
        public IReadOnlyList<RgbColor> Palette => Array.Empty<RgbColor>();
    }

    private static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2D)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
