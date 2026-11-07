using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleRGB.Core.Lighting;

namespace SimpleRGB.Controls;

public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.Red, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public static readonly DependencyProperty FavoritesProperty = DependencyProperty.Register(
        nameof(Favorites), typeof(IEnumerable), typeof(ColorPicker), new PropertyMetadata(null, OnListChanged));

    public static readonly DependencyProperty RecentsProperty = DependencyProperty.Register(
        nameof(Recents), typeof(IEnumerable), typeof(ColorPicker), new PropertyMetadata(null, OnListChanged));

    public static readonly DependencyProperty AddFavoriteCommandProperty = DependencyProperty.Register(
        nameof(AddFavoriteCommand), typeof(ICommand), typeof(ColorPicker));

    public static readonly DependencyProperty RemoveFavoriteCommandProperty = DependencyProperty.Register(
        nameof(RemoveFavoriteCommand), typeof(ICommand), typeof(ColorPicker));

    private static readonly string[] PresetColors =
    {
        "#FF0000", "#FF5A00", "#FFB000", "#FFE600", "#7CFF00", "#00FF3C", "#00FFB4", "#00E5FF",
        "#0090FF", "#0030FF", "#6A2BFF", "#B000FF", "#FF00C8", "#FF2D6A", "#FFB46B", "#FFFFFF",
    };

    // Kept separately so hue survives when saturation or brightness hit zero.
    private double _h, _s = 1, _v = 1;
    private bool _internal;

    public ColorPicker()
    {
        InitializeComponent();
        Presets.ItemsSource = PresetColors;
        SvArea.SizeChanged += (_, _) => { SvClip.Rect = new Rect(0, 0, SvArea.ActualWidth, SvArea.ActualHeight); UpdateVisuals(); };
        HueArea.SizeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => { SyncFromColor(SelectedColor); UpdateListVisibility(); };
    }

    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public IEnumerable? Favorites { get => (IEnumerable?)GetValue(FavoritesProperty); set => SetValue(FavoritesProperty, value); }
    public IEnumerable? Recents { get => (IEnumerable?)GetValue(RecentsProperty); set => SetValue(RecentsProperty, value); }
    public ICommand? AddFavoriteCommand { get => (ICommand?)GetValue(AddFavoriteCommandProperty); set => SetValue(AddFavoriteCommandProperty, value); }
    public ICommand? RemoveFavoriteCommand { get => (ICommand?)GetValue(RemoveFavoriteCommandProperty); set => SetValue(RemoveFavoriteCommandProperty, value); }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (!picker._internal) picker.SyncFromColor((Color)e.NewValue);
    }

    private static void OnListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= picker.ListCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged now) now.CollectionChanged += picker.ListCollectionChanged;
        picker.UpdateListVisibility();
    }

    private void ListCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateListVisibility();

    private void UpdateListVisibility()
    {
        NoFavorites.Visibility = Favorites?.Cast<object>().Any() == true ? Visibility.Collapsed : Visibility.Visible;
        RecentPanel.Visibility = Recents?.Cast<object>().Any() == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncFromColor(Color c)
    {
        var (h, s, v) = new RgbColor(c.R, c.G, c.B).ToHsv();
        if (s > 0 && v > 0) _h = h;
        _s = s;
        _v = v;
        UpdateVisuals();
    }

    private void Commit()
    {
        var rgb = RgbColor.FromHsv(_h, _s, _v);
        _internal = true;
        try { SetCurrentValue(SelectedColorProperty, Color.FromRgb(rgb.R, rgb.G, rgb.B)); }
        finally { _internal = false; }
        UpdateVisuals();
    }

    private void SetColor(RgbColor c)
    {
        SyncFromColor(Color.FromRgb(c.R, c.G, c.B));
        _internal = true;
        try { SetCurrentValue(SelectedColorProperty, Color.FromRgb(c.R, c.G, c.B)); }
        finally { _internal = false; }
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var hue = RgbColor.FromHsv(_h, 1, 1);
        HueFill.Fill = new SolidColorBrush(Color.FromRgb(hue.R, hue.G, hue.B));
        HueThumb.Fill = HueFill.Fill;

        var current = SelectedColor;
        var brush = new SolidColorBrush(current);
        Preview.Background = brush;
        SvThumb.Fill = brush;
        if (!HexBox.IsKeyboardFocused) HexBox.Text = $"#{current.R:X2}{current.G:X2}{current.B:X2}";
        if (!RBox.IsKeyboardFocused) RBox.Text = current.R.ToString();
        if (!GBox.IsKeyboardFocused) GBox.Text = current.G.ToString();
        if (!BBox.IsKeyboardFocused) BBox.Text = current.B.ToString();

        var w = SvArea.ActualWidth;
        var h = SvArea.ActualHeight;
        Canvas.SetLeft(SvThumb, _s * w - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * h - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _h * Math.Max(0, HueArea.ActualWidth - HueThumb.Width));
    }

    private void SvArea_MouseDown(object sender, MouseButtonEventArgs e) { SvArea.CaptureMouse(); PickSv(e.GetPosition(SvArea)); }
    private void SvArea_MouseMove(object sender, MouseEventArgs e) { if (SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea)); }

    private void PickSv(Point p)
    {
        if (SvArea.ActualWidth <= 0 || SvArea.ActualHeight <= 0) return;
        _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _v = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1);
        Commit();
    }

    private void HueArea_MouseDown(object sender, MouseButtonEventArgs e) { HueArea.CaptureMouse(); PickHue(e.GetPosition(HueArea)); }
    private void HueArea_MouseMove(object sender, MouseEventArgs e) { if (HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea)); }

    private void PickHue(Point p)
    {
        var usable = Math.Max(1, HueArea.ActualWidth - HueThumb.Width);
        _h = Math.Clamp((p.X - HueThumb.Width / 2) / usable, 0, 0.9999);
        if (_s < 0.05) _s = 1;
        if (_v < 0.05) _v = 1;
        Commit();
    }

    private void Area_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == HexBox) ApplyHex(); else ApplyRgb();
        e.Handled = true;
    }

    private void HexBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHex();
    private void Rgb_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyRgb();

    private void ApplyHex()
    {
        if (RgbColor.TryParse(HexBox.Text, out var c)) SetColor(c);
        else UpdateVisuals();
    }

    private void ApplyRgb()
    {
        static byte P(string t, byte fallback) => int.TryParse(t, out var v) ? (byte)Math.Clamp(v, 0, 255) : fallback;
        var cur = SelectedColor;
        SetColor(new RgbColor(P(RBox.Text, cur.R), P(GBox.Text, cur.G), P(BBox.Text, cur.B)));
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && RgbColor.TryParse(hex, out var c)) SetColor(c);
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedColor;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (AddFavoriteCommand?.CanExecute(hex) == true) AddFavoriteCommand.Execute(hex);
    }

    private void Favorite_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && RemoveFavoriteCommand?.CanExecute(hex) == true)
            RemoveFavoriteCommand.Execute(hex);
        e.Handled = true;
    }
}
