using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SimpleRGB.Platform;
using SimpleRGB.ViewModels;

namespace SimpleRGB;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _deviceChangeDebounce;

    /// <summary>Raised (debounced) when Windows reports USB devices being plugged in or removed.</summary>
    public event Action? UsbDevicesChanged;

    /// <summary>Raised when the user closes the window while "keep running" is on.</summary>
    public event Action? HiddenToTray;

    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _deviceChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceChangeDebounce.Tick += (_, _) =>
        {
            _deviceChangeDebounce.Stop();
            UsbDevicesChanged?.Invoke();
        };

        SourceInitialized += (_, _) =>
        {
            WindowTheme.ApplyDark(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        // Live music meters: only tick while the music panel is on screen.
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => _vm.TickMeters();
        vm.MusicPanelVisibilityChanged += visible => { _musicPanelVisible = visible; UpdateMeterTimer(); };
        IsVisibleChanged += (_, _) => UpdateMeterTimer();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _vm.SettingsOpen) { _vm.SettingsOpen = false; e.Handled = true; }
        };
    }

    private readonly DispatcherTimer _meterTimer;
    private bool _musicPanelVisible;

    private void UpdateMeterTimer()
    {
        if (_musicPanelVisible && IsVisible) _meterTimer.Start();
        else _meterTimer.Stop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            // Coalesce the burst of notifications a single plug event produces.
            _deviceChangeDebounce.Stop();
            _deviceChangeDebounce.Start();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliable foreground on Windows
        Topmost = false;
        Focus();
    }

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e) => _vm.ZoneSelectionChanged();

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelRenameCommand.Execute(null); e.Handled = true; }
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    // Option buttons only act on a real click (never from layout/binding side effects).
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Core.Lighting.LightingMode mode }) _vm.EditorMode = mode;
        else if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Core.Lighting.LightingMode>(tag, out var parsed))
            _vm.EditorMode = parsed;
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || tag.IndexOf(':') is var i && i < 0) return;
        var kind = tag[..i];
        var value = tag[(i + 1)..];
        switch (kind)
        {
            case "slot": _vm.ColorSlot = value == "1" ? 1 : 0; break;
            case "source" when Enum.TryParse<Core.Audio.AudioSource>(value, out var source): _vm.AudioSource = source; break;
            case "react" when Enum.TryParse<Core.Lighting.MusicReact>(value, out var react): _vm.EditorMusicReact = react; break;
            case "style" when Enum.TryParse<Core.Lighting.MusicStyle>(value, out var style): _vm.EditorMusicStyle = style; break;
        }
    }

    private void StyleSeparately_Click(object sender, RoutedEventArgs e) => _vm.SelectedFollowsSync = false;

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) => _vm.SettingsOpen = false;

    private void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
