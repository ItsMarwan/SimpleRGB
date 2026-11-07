using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleRGB.Core;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Audio;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Media;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;
using SimpleRGB.Core.Settings;
using SimpleRGB.Platform;

namespace SimpleRGB.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DeviceManager _manager;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private NavItem? _selected;
    private bool _loadingEditor;
    private Color _editorColor = Colors.DeepSkyBlue;
    private LightingMode _editorMode = LightingMode.Static;
    private int _editorSpeed = 50;
    private int _channelLedCount;
    private bool _isScanning;
    private string _scanStatus = "";
    private bool _settingsOpen;
    private string _savedText = "";
    private bool _startWithWindows;
    private bool _pluginBusy;
    private string _pluginStatus = "";
    private DispatcherTimer? _savedTimer;

    public MainViewModel(DeviceManager manager, SettingsStore settings, Dispatcher dispatcher)
    {
        _manager = manager;
        _settings = settings;
        _dispatcher = dispatcher;

        NavItems.Add(AllDevices);
        _selected = AllDevices;

        _manager.DevicesChanged += () => _dispatcher.InvokeAsync(SyncDevices);
        _manager.DeviceChanged += d => _dispatcher.InvokeAsync(() => RefreshDevice(d));
        _settings.Saved += () => _dispatcher.InvokeAsync(ShowSaved);
        ParameterItem.Changed += OnParameterChanged;

        RescanCommand = new RelayCommand(() => _ = RescanAsync());
        SelectAllZonesCommand = new RelayCommand(SelectAllZones);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        OpenSettingsCommand = new RelayCommand(() => { RefreshSettingsInfo(); SettingsOpen = true; });
        CloseSettingsCommand = new RelayCommand(() => SettingsOpen = false);
        ToggleLightsCommand = new RelayCommand(() => LightsOff = !LightsOff);
        TurnOffSyncCommand = new RelayCommand(() => SyncAll = false);
        OpenPluginFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.PluginsDir));
        OpenLogCommand = new RelayCommand(() => OpenFolder(AppPaths.LogDir));
        ImportPluginsCommand = new RelayCommand(_ => _ = ImportPluginsAsync(), _ => !PluginBusy);
        DownloadPluginsCommand = new RelayCommand(_ => _ = DownloadPluginsAsync(), _ => !PluginBusy);
        GetPawnIoCommand = new RelayCommand(() => OpenUrl("https://pawnio.eu"));
        CloseAppCommand = new RelayCommand(p => CloseApp(p as NoticeItem, false));
        CloseAppAndStartupCommand = new RelayCommand(p => CloseApp(p as NoticeItem, true));
        RestoreStartupCommand = new RelayCommand(() =>
        {
            var n = _manager.RestoreStartupEntries();
            PluginStatus = "";
            StartupRestoreText = n > 0 ? $"Restored {n} startup entr{(n == 1 ? "y" : "ies")}." : "Nothing to restore.";
            Raise(nameof(HasDisabledStartupEntries));
        });
        BeginRenameCommand = new RelayCommand(() =>
        {
            if (SelectedDevice is not { } d) return;
            d.RenameText = d.Name;
            d.IsRenaming = true;
        });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => { if (SelectedDevice is { } d) d.IsRenaming = false; });

        InitColors();
        RefreshEffectTiles();
        NavView = System.Windows.Data.CollectionViewSource.GetDefaultView(NavItems);
        NavView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(NavItem.Category)));

        // Programs like TRCC can be started/closed at any time: keep the conflict notices current.
        _conflictTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => Task.Run(_manager.RefreshConflictNotices), _dispatcher);
        _conflictTimer.Start();

        LoadEditorFromSelection();
    }

    // ------------------------------------------------------------------ collections

    public AllDevicesItem AllDevices { get; } = new();
    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<NoticeItem> Notices { get; } = new();
    public ObservableCollection<UnsupportedDevice> Unsupported { get; } = new();
    public bool HasUnsupported => Unsupported.Count > 0;
    public int DeviceCount => NavItems.Count - 1;
    /// <summary>Devices only (no "All devices" entry), for the device grid.</summary>
    public IReadOnlyList<DeviceItem> DeviceList => NavItems.OfType<DeviceItem>().ToList();

    /// <summary>Preview/testing helper: select the first device whose name contains <paramref name="text"/>.</summary>
    public void SelectByName(string text)
    {
        var item = NavItems.OfType<DeviceItem>().FirstOrDefault(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (item != null) Selected = item;
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand SelectAllZonesCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CloseSettingsCommand { get; }
    public RelayCommand ToggleLightsCommand { get; }
    public RelayCommand TurnOffSyncCommand { get; }
    public RelayCommand OpenPluginFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ImportPluginsCommand { get; }
    public RelayCommand DownloadPluginsCommand { get; }
    public RelayCommand GetPawnIoCommand { get; }
    public RelayCommand CloseAppCommand { get; }
    public RelayCommand CloseAppAndStartupCommand { get; }
    public RelayCommand RestoreStartupCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }

    /// <summary>The sidebar list, grouped into sections (Motherboard, Graphics card, Memory…).</summary>
    public System.ComponentModel.ICollectionView NavView { get; }

    private readonly DispatcherTimer _conflictTimer;
    private string _startupRestoreText = "";
    public string StartupRestoreText { get => _startupRestoreText; private set => Set(ref _startupRestoreText, value); }
    public bool HasDisabledStartupEntries => _settings.Read(s => s.DisabledStartupEntries.Count > 0);

    private void CloseApp(NoticeItem? notice, bool alsoStartup)
    {
        if (notice?.Notice.App is not { } exe) return;
        var app = Core.Platform.ConflictingApps.ProcessName(exe);
        Task.Run(() =>
        {
            var ok = _manager.CloseConflictingApp(exe, alsoStartup);
            _dispatcher.InvokeAsync(() =>
            {
                Raise(nameof(HasDisabledStartupEntries));
                if (!ok) MessageBox.Show($"{app} couldn't be closed. Close it from its own tray icon, then SimpleRGB takes over automatically.",
                    "SimpleRGB", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    /// <summary>Custom name → else friendly name, numbered when several look the same (RAM stick 1–4).</summary>
    private void ApplyNames()
    {
        var items = NavItems.OfType<DeviceItem>().ToList();
        foreach (var group in items.GroupBy(i => i.BaseName))
        {
            var list = group.OrderBy(i => i.Device.ConnectionText, StringComparer.OrdinalIgnoreCase).ToList();
            for (var n = 0; n < list.Count; n++)
            {
                var item = list[n];
                var custom = _settings.Read(s => s.Devices.GetValueOrDefault(item.Key)?.Name);
                item.Name = !string.IsNullOrWhiteSpace(custom) ? custom! : list.Count > 1 ? $"{item.BaseName} {n + 1}" : item.BaseName;
            }
        }
    }

    private void CommitRename()
    {
        if (SelectedDevice is not { } d) return;
        var text = d.RenameText.Trim();
        if (text.Length > 60) text = text[..60];
        _settings.Update(s => s.ForDevice(d.Key).Name = text.Length == 0 ? null : text);
        d.IsRenaming = false;
        ApplyNames();
    }

    /// <summary>Set by the window: asks the app to relaunch elevated.</summary>
    public Action? RequestRestartElevated { get; set; }

    // ------------------------------------------------------------------ selection

    public NavItem? Selected
    {
        get => _selected;
        set
        {
            if (value == null || !Set(ref _selected, value)) return;
            Raise(nameof(SelectedDevice));
            Raise(nameof(IsAllSelected));
            Raise(nameof(IsDeviceSelected));
            Raise(nameof(EditorEnabled));
            Raise(nameof(ShowSyncBanner));
            Raise(nameof(DeviceBrightness));
            Raise(nameof(SelectedFollowsSync));
            LoadEditorFromSelection();
            RaiseEditorVisibility();
        }
    }

    public DeviceItem? SelectedDevice => _selected as DeviceItem;
    public bool IsAllSelected => _selected is AllDevicesItem;
    public bool IsDeviceSelected => _selected is DeviceItem;

    /// <summary>Called by the view when the zone list selection changes.</summary>
    public void ZoneSelectionChanged()
    {
        LoadEditorFromSelection();
        Raise(nameof(EditorTitle));
        Raise(nameof(ShowChannelLeds));
        Raise(nameof(ChannelLedLimit));
    }

    private IReadOnlyList<ZoneItem> TargetZones()
    {
        if (SelectedDevice is not { } d) return Array.Empty<ZoneItem>();
        var selected = d.Zones.Where(z => z.IsSelected).ToList();
        return selected.Count > 0 ? selected : d.Zones.ToList();
    }

    private void SelectAllZones()
    {
        if (SelectedDevice is not { } d) return;
        foreach (var z in d.Zones) z.IsSelected = true;
        ZoneSelectionChanged();
    }

    public string EditorTitle
    {
        get
        {
            if (IsAllSelected) return "Every device";
            var d = SelectedDevice;
            if (d == null) return "";
            var zones = TargetZones();
            return zones.Count == d.Zones.Count ? "Whole device" : string.Join(", ", zones.Select(z => z.Name));
        }
    }

    // ------------------------------------------------------------------ editor

    private Color _editorColor2 = Color.FromRgb(0xFF, 0x2D, 0x95);
    private int _colorSlot;
    private MusicReact _musicReact = MusicReact.Beat;
    private MusicColors _musicColors = MusicColors.NewColorEveryBeat;
    private MusicStyle _musicStyle = MusicStyle.Pulse;

    /// <summary>Effect tiles; each previews its animation live with the current colors.</summary>
    public ObservableCollection<EffectOption> EffectOptions { get; } = new()
    {
        new EffectOption(LightingMode.Static, "Static"),
        new EffectOption(LightingMode.Breathing, "Breathe"),
        new EffectOption(LightingMode.ColorCycle, "Color cycle"),
        new EffectOption(LightingMode.Rainbow, "Rainbow"),
        new EffectOption(LightingMode.Gradient, "Gradient"),
        new EffectOption(LightingMode.ColorWave, "Color wave"),
        new EffectOption(LightingMode.Comet, "Comet"),
        new EffectOption(LightingMode.Sparkle, "Sparkle"),
        new EffectOption(LightingMode.Candle, "Candle"),
        new EffectOption(LightingMode.Heartbeat, "Heartbeat"),
        new EffectOption(LightingMode.Music, "Music"),
        new EffectOption(LightingMode.Off, "Off"),
    };

    private int _editorSize = 50;
    private bool _editorReverse;

    public int EditorSize
    {
        get => _editorSize;
        set { if (Set(ref _editorSize, Math.Clamp(value, 1, 100))) ApplyEditor(); }
    }

    public bool EditorReverse
    {
        get => _editorReverse;
        set { if (Set(ref _editorReverse, value)) ApplyEditor(); }
    }

    public bool ShowSize => ZoneLighting.UsesSize(EditorMode);
    public bool ShowDirection => ZoneLighting.UsesDirection(EditorMode);
    public string SizeLabel => EditorMode switch
    {
        LightingMode.Comet => "Tail length",
        LightingMode.Sparkle => "Density",
        _ => "Width",
    };

    /// <summary>Lighting the editor currently describes (used for tile previews and recents).</summary>
    private ZoneLighting EditorLighting() => new ZoneLighting
    {
        Mode = EditorMode,
        Color = LightingVisuals.ToRgb(EditorColor).ToHex(),
        Color2 = LightingVisuals.ToRgb(EditorColor2).ToHex(),
        Speed = EditorSpeed,
        Size = _editorSize,
        Reverse = _editorReverse,
        MusicReact = _musicReact,
        MusicColors = _musicColors,
        MusicStyle = _musicStyle,
    }.Normalize();

    private void RefreshEffectTiles()
    {
        var current = EditorLighting();
        foreach (var tile in EffectOptions)
            tile.Preview = current with { Mode = tile.Mode, MusicColors = tile.Mode == LightingMode.Music ? MusicColors.Frequency : current.MusicColors };
    }

    public static IReadOnlyList<string> MusicColorOptions { get; } = new[]
    {
        "Keep my color", "New color every beat", "Album art colors", "Colors by pitch",
    };

    /// <summary>Color 1 (main color).</summary>
    public Color EditorColor
    {
        get => _editorColor;
        set
        {
            if (!Set(ref _editorColor, value)) return;
            if (_colorSlot == 0) Raise(nameof(PickerColor));
            Raise(nameof(Color1Brush));
            ApplyEditor();
        }
    }

    public Color EditorColor2
    {
        get => _editorColor2;
        set
        {
            if (!Set(ref _editorColor2, value)) return;
            if (_colorSlot == 1) Raise(nameof(PickerColor));
            Raise(nameof(Color2Brush));
            ApplyEditor();
        }
    }

    /// <summary>The color the picker edits: color 1 or color 2.</summary>
    public Color PickerColor
    {
        get => _colorSlot == 1 && ShowSecondColor ? _editorColor2 : _editorColor;
        set
        {
            if (_colorSlot == 1 && ShowSecondColor) EditorColor2 = value;
            else EditorColor = value;
        }
    }

    public int ColorSlot
    {
        get => _colorSlot;
        set
        {
            if (!Set(ref _colorSlot, value is 1 ? 1 : 0)) return;
            Raise(nameof(PickerColor));
            Raise(nameof(IsSlot1));
            Raise(nameof(IsSlot2));
        }
    }

    public bool IsSlot1 => _colorSlot == 0 || !ShowSecondColor;
    public bool IsSlot2 => _colorSlot == 1 && ShowSecondColor;
    public Brush Color1Brush => new SolidColorBrush(_editorColor);
    public Brush Color2Brush => new SolidColorBrush(_editorColor2);

    public LightingMode EditorMode
    {
        get => _editorMode;
        set
        {
            if (!Set(ref _editorMode, value)) return;
            if (!ShowSecondColor) _colorSlot = 0;
            RaiseEditorVisibility();
            ApplyEditor();
        }
    }

    public int EditorSpeed
    {
        get => _editorSpeed;
        set { if (Set(ref _editorSpeed, Math.Clamp(value, 1, 100))) ApplyEditor(); }
    }

    public MusicReact EditorMusicReact
    {
        get => _musicReact;
        set { if (Set(ref _musicReact, value)) ApplyEditor(); }
    }

    public int EditorMusicColorsIndex
    {
        get => (int)_musicColors;
        set
        {
            var v = (MusicColors)Math.Clamp(value, 0, 3);
            if (_musicColors == v) return;
            _musicColors = v;
            Raise();
            RaiseEditorVisibility();
            ApplyEditor();
        }
    }

    public MusicStyle EditorMusicStyle
    {
        get => _musicStyle;
        set { if (Set(ref _musicStyle, value)) ApplyEditor(); }
    }

    public bool ShowColor => ZoneLighting.UsesColor(EditorMode) && !(EditorMode == LightingMode.Music && _musicColors != MusicColors.SameColor);
    public bool ShowSecondColor => ZoneLighting.UsesSecondColor(EditorMode);
    public bool ShowSpeed => ZoneLighting.UsesSpeed(EditorMode);
    public bool ShowEffectOptions => ShowSpeed || ShowSize || ShowDirection;
    public bool ShowMusic => EditorMode == LightingMode.Music;
    public string SecondColorLabel => EditorMode == LightingMode.Sparkle ? "Sparkles" : "Color 2";

    private void RaiseEditorVisibility()
    {
        Raise(nameof(ShowColor));
        Raise(nameof(ShowSecondColor));
        Raise(nameof(ShowSpeed));
        Raise(nameof(ShowMusic));
        Raise(nameof(SecondColorLabel));
        Raise(nameof(ShowSize));
        Raise(nameof(ShowDirection));
        Raise(nameof(SizeLabel));
        Raise(nameof(ShowEffectOptions));
        Raise(nameof(PickerColor));
        Raise(nameof(IsSlot1));
        Raise(nameof(IsSlot2));
        var musicVisible = ShowMusic && EditorEnabled;
        _manager.UiWantsAudio = musicVisible;
        MusicPanelVisibilityChanged?.Invoke(musicVisible);
    }

    /// <summary>The window starts/stops its meter timer with this.</summary>
    public event Action<bool>? MusicPanelVisibilityChanged;

    public bool EditorEnabled => IsAllSelected || !SyncAll || SelectedDevice is { FollowsSync: false };
    public bool ShowSyncBanner => IsDeviceSelected && SyncAll && SelectedDevice is { FollowsSync: true };

    /// <summary>Header switch: does the selected device follow "Sync all devices"?</summary>
    public bool SelectedFollowsSync
    {
        get => SelectedDevice?.FollowsSync ?? true;
        set { if (SelectedDevice is { } d) SetFollowsSync(d, value); }
    }

    private void SetFollowsSync(DeviceItem d, bool follows)
    {
        if (d.FollowsSync == follows) return;
        d.FollowsSync = follows;
        _settings.Update(s => s.ForDevice(d.Key).ExcludeFromSync = !follows);
        _manager.ApplyLighting(d.Device);
        RefreshPreview(d);
        RefreshPreviews();
        Raise(nameof(SelectedFollowsSync));
        Raise(nameof(EditorEnabled));
        Raise(nameof(ShowSyncBanner));
        Raise(nameof(ShowChannelLeds));
        LoadEditorFromSelection();
    }

    public bool ShowChannelLeds => !IsAllSelected && TargetZones() is { Count: 1 } z && z[0].IsChannel && EditorEnabled;
    public int ChannelLedLimit => TargetZones() is { Count: 1 } z ? z[0].LedLimit : 0;

    public int ChannelLedCount
    {
        get => _channelLedCount;
        set
        {
            if (!Set(ref _channelLedCount, value) || _loadingEditor) return;
            if (SelectedDevice is not { } d || TargetZones() is not { Count: 1 } zones || !zones[0].IsChannel) return;
            var zone = zones[0];
            zone.SetLedCount(value);
            var channelId = zone.Info.Id[ZoneInfo.ChannelZoneId("").Length..];
            _settings.Update(s => s.ForDevice(d.Key).ChannelLedCounts[channelId] = zone.LedCount);
            d.Device.SetChannelLedCount(channelId, zone.LedCount);
        }
    }

    private void LoadEditorFromSelection()
    {
        // The LED-count slider's maximum must be updated before its value, or WPF clamps the value to the old maximum.
        Raise(nameof(ChannelLedLimit));
        _loadingEditor = true;
        try
        {
            ZoneLighting lighting;
            if (IsAllSelected)
                lighting = _settings.Read(s => s.SyncLighting);
            else
                lighting = TargetZones().FirstOrDefault()?.Lighting ?? ZoneLighting.Default;

            EditorMode = lighting.Mode;
            EditorSpeed = lighting.Speed;
            EditorSize = lighting.Size;
            EditorReverse = lighting.Reverse;
            EditorColor = LightingVisuals.ToColor(lighting.BaseColor);
            EditorColor2 = LightingVisuals.ToColor(lighting.SecondColor);
            EditorMusicReact = lighting.MusicReact;
            EditorMusicColorsIndex = (int)lighting.MusicColors;
            EditorMusicStyle = lighting.MusicStyle;
            if (TargetZones() is { Count: 1 } z && z[0].IsChannel) ChannelLedCount = z[0].LedCount;
        }
        finally
        {
            _loadingEditor = false;
        }
        Raise(nameof(EditorTitle));
        Raise(nameof(ShowChannelLeds));
        Raise(nameof(ChannelLedLimit));
        RaiseEditorVisibility();
    }

    private void ApplyEditor()
    {
        RefreshEffectTiles();
        if (_loadingEditor) return;
        var lighting = EditorLighting();
        NoteRecentColor(lighting);

        if (IsAllSelected)
        {
            // Writes that don't change anything (layout/binding echoes) must not flip sync on or save.
            if (SyncAll && _settings.Read(s => s.SyncLighting) == lighting) return;
            _settings.Update(s => { s.SyncAll = true; s.SyncLighting = lighting; });
            Raise(nameof(SyncAll));
            Raise(nameof(EditorEnabled));
            Raise(nameof(ShowSyncBanner));
            _manager.ApplyLighting();
            RefreshPreviews();
            return;
        }

        if (SelectedDevice is not { } d || !EditorEnabled) return;
        var zones = TargetZones();
        if (zones.All(z => z.Lighting == lighting)) return;
        _settings.Update(s =>
        {
            var ds = s.ForDevice(d.Key);
            foreach (var z in zones) ds.Zones[z.Id] = lighting;
        });
        foreach (var z in zones) z.Lighting = lighting;
        _manager.ApplyLighting(d.Device);
        RefreshPreview(d);
    }

    // ------------------------------------------------------------------ music

    private double _meterBass, _meterMid, _meterTreble, _beatPulse;
    private string _audioStatus = "";
    private string _nowPlayingText = "";
    private string _albumKey = "";
    private IReadOnlyList<Brush> _albumColors = Array.Empty<Brush>();

    public AudioSource AudioSource
    {
        get => _settings.Read(s => s.AudioSource);
        set
        {
            if (AudioSource == value) return;
            _manager.SetAudioSource(value);
            Raise();
        }
    }

    public double MeterBass { get => _meterBass; private set => Set(ref _meterBass, value); }
    public double MeterMid { get => _meterMid; private set => Set(ref _meterMid, value); }
    public double MeterTreble { get => _meterTreble; private set => Set(ref _meterTreble, value); }
    public double BeatPulse { get => _beatPulse; private set => Set(ref _beatPulse, value); }
    public string AudioStatus { get => _audioStatus; private set => Set(ref _audioStatus, value); }
    public string NowPlayingText { get => _nowPlayingText; private set => Set(ref _nowPlayingText, value); }
    public IReadOnlyList<Brush> AlbumColors { get => _albumColors; private set => Set(ref _albumColors, value); }

    /// <summary>Called ~30 times a second by the window while the music panel is visible.</summary>
    public void TickMeters()
    {
        var frame = _manager.Audio?.Current ?? AudioFrame.Silent;
        MeterBass = frame.Bass;
        MeterMid = frame.Mid;
        MeterTreble = frame.Treble;
        BeatPulse = frame.BeatFlash(LightingClock.Seconds);
        AudioStatus = _manager.Audio?.Status ?? "";

        var np = _manager.NowPlaying?.Current ?? NowPlaying.None;
        NowPlayingText = np.HasTrack
            ? $"{(np.IsPlaying ? "▶" : "⏸")}  {np.Title}{(np.Artist.Length > 0 ? " — " + np.Artist : "")}"
            : "";
        var key = string.Join(",", np.Palette.Select(c => c.ToHex()));
        if (key != _albumKey)
        {
            _albumKey = key;
            AlbumColors = np.Palette.Select(c => (Brush)new SolidColorBrush(LightingVisuals.ToColor(c))).ToList();
        }
    }

    // ------------------------------------------------------------------ global controls

    public bool SyncAll
    {
        get => _settings.Read(s => s.SyncAll);
        set
        {
            if (SyncAll == value) return;
            _settings.Update(s => s.SyncAll = value);
            Raise();
            Raise(nameof(EditorEnabled));
            Raise(nameof(ShowSyncBanner));
            Raise(nameof(ShowChannelLeds));
            _manager.ApplyLighting();
            RefreshPreviews();
            RaiseEditorVisibility();
        }
    }

    public int MasterBrightness
    {
        get => _settings.Read(s => s.MasterBrightness);
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (MasterBrightness == value) return;
            _settings.Update(s => s.MasterBrightness = value);
            Raise();
            _manager.ApplyLighting();
            RefreshPreviews();
        }
    }

    public bool LightsOff
    {
        get => _settings.Read(s => s.LightsOff);
        set
        {
            if (LightsOff == value) return;
            _settings.Update(s => s.LightsOff = value);
            Raise();
            Raise(nameof(LightsButtonText));
            _manager.ApplyLighting();
            RefreshPreviews();
        }
    }

    public string LightsButtonText => LightsOff ? "Turn lights on" : "Turn lights off";

    public int DeviceBrightness
    {
        get => SelectedDevice?.Brightness ?? 100;
        set
        {
            if (SelectedDevice is not { } d) return;
            d.Brightness = value;
            _settings.Update(s => s.ForDevice(d.Key).Brightness = d.Brightness);
            Raise();
            _manager.ApplyLighting(d.Device);
            RefreshPreview(d);
        }
    }

    // ------------------------------------------------------------------ status

    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string SavedText { get => _savedText; private set => Set(ref _savedText, value); }

    private void ShowSaved()
    {
        SavedText = "All changes saved";
        _savedTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) =>
        {
            SavedText = "";
            _savedTimer!.Stop();
        }, _dispatcher);
        _savedTimer.Stop();
        _savedTimer.Start();
    }

    // ------------------------------------------------------------------ device list

    public async Task RescanAsync(bool importPlugins = true)
    {
        IsScanning = true;
        try { await _manager.ScanAsync(importPlugins); }
        finally { IsScanning = false; }
    }

    private void SyncDevices()
    {
        IsScanning = _manager.IsScanning;
        ScanStatus = _manager.ScanStatus;

        var devices = _manager.Devices;
        var keepSelectedKey = (Selected as DeviceItem)?.Key;

        // Remove items whose device object is gone (rescan creates new device objects).
        foreach (var item in NavItems.OfType<DeviceItem>().Where(i => !devices.Contains(i.Device)).ToList())
            NavItems.Remove(item);

        foreach (var device in devices)
        {
            if (NavItems.OfType<DeviceItem>().Any(i => i.Device == device)) continue;
            var item = new DeviceItem(device)
            {
                Brightness = _settings.Read(s => s.Devices.GetValueOrDefault(device.Key)?.Brightness ?? 100),
                FollowsSync = !_settings.Read(s => s.Devices.GetValueOrDefault(device.Key)?.ExcludeFromSync ?? false),
            };
            NavItems.Add(item);
            RefreshDevice(device);
        }

        // Sort: motherboard/USB first, then GPU, then RAM (stable within groups).
        var ordered = NavItems.OfType<DeviceItem>()
            .OrderBy(i => i.CategoryOrder)
            .ThenBy(i => i.Device.ConnectionText, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = NavItems.IndexOf(ordered[i]);
            if (current != i + 1) NavItems.Move(current, i + 1);
        }

        ApplyNames();

        if (keepSelectedKey != null)
            Selected = NavItems.OfType<DeviceItem>().FirstOrDefault(i => i.Key == keepSelectedKey) ?? (NavItem)AllDevices;
        if (Selected != null && !NavItems.Contains(Selected)) Selected = AllDevices;

        Notices.Clear();
        foreach (var n in _manager.Notices) Notices.Add(new NoticeItem { Notice = n });
        Unsupported.Clear();
        foreach (var u in _manager.UnsupportedDevices) Unsupported.Add(u);
        Raise(nameof(HasUnsupported));
        Raise(nameof(DeviceCount));
        Raise(nameof(DeviceList));
        RefreshPreviews();
    }

    private void RefreshDevice(RgbDevice device)
    {
        var item = NavItems.OfType<DeviceItem>().FirstOrDefault(i => i.Device == device);
        if (item == null) return;
        var wasStructure = item.Zones.Count;
        item.Refresh(
            zoneId => _settings.Read(s => s.Devices.GetValueOrDefault(device.Key)?.Zones.GetValueOrDefault(zoneId)) ?? ZoneLighting.Default,
            zoneId => _settings.Read(s =>
                s.Devices.GetValueOrDefault(device.Key)?.ChannelLedCounts.TryGetValue(zoneId[ZoneInfo.ChannelZoneId("").Length..], out var n) == true ? n : (int?)null));
        ApplyNames();
        RefreshPreview(item);
        if (item == Selected && wasStructure != item.Zones.Count) LoadEditorFromSelection();
    }

    private void RefreshPreviews()
    {
        var sync = SyncAll ? _settings.Read(s => s.SyncLighting) : null;
        AllDevices.Preview = LightingVisuals.BrushFor(_settings.Read(s => s.SyncLighting));
        AllDevices.PreviewLighting = _settings.Read(s => s.SyncLighting);
        AllDevices.Subtitle = SyncAll ? "Sync on · " + LightingVisuals.Describe(sync!) : $"{DeviceCount} device{(DeviceCount == 1 ? "" : "s")}";
        foreach (var d in NavItems.OfType<DeviceItem>()) RefreshPreview(d, sync);
    }

    private void RefreshPreview(DeviceItem d, ZoneLighting? sync = null)
    {
        sync ??= SyncAll ? _settings.Read(s => s.SyncLighting) : null;
        if (!d.FollowsSync) sync = null;
        var lighting = sync ?? d.Zones.FirstOrDefault()?.Lighting ?? ZoneLighting.Default;
        d.Preview = LightingVisuals.BrushFor(lighting);
        d.Snapshot = _manager.BuildLighting(d.Key);
        foreach (var z in d.Zones) z.SyncOverride = sync;
    }

    // ------------------------------------------------------------------ favorites & recent colors

    public ObservableCollection<string> FavoriteColors { get; } = new();
    public ObservableCollection<string> RecentColors { get; } = new();
    public RelayCommand AddFavoriteCommand { get; private set; } = null!;
    public RelayCommand RemoveFavoriteCommand { get; private set; } = null!;
    public RelayCommand ToggleDeviceSyncCommand { get; private set; } = null!;
    private DispatcherTimer? _recentTimer;
    private string? _pendingRecent;

    private void InitColors()
    {
        foreach (var c in _settings.Read(s => s.FavoriteColors.ToList())) FavoriteColors.Add(c);
        foreach (var c in _settings.Read(s => s.RecentColors.ToList())) RecentColors.Add(c);
        AddFavoriteCommand = new RelayCommand(p =>
        {
            if (p is not string hex || FavoriteColors.Contains(hex)) return;
            FavoriteColors.Insert(0, hex);
            while (FavoriteColors.Count > 16) FavoriteColors.RemoveAt(FavoriteColors.Count - 1);
            _settings.Update(s => s.FavoriteColors = FavoriteColors.ToList());
        });
        RemoveFavoriteCommand = new RelayCommand(p =>
        {
            if (p is string hex && FavoriteColors.Remove(hex)) _settings.Update(s => s.FavoriteColors = FavoriteColors.ToList());
        });
        ToggleDeviceSyncCommand = new RelayCommand(p => { if (p is DeviceItem d) SetFollowsSync(d, !d.FollowsSync); });
        _recentTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _recentTimer!.Stop();
            if (_pendingRecent is not { } hex) return;
            RecentColors.Remove(hex);
            RecentColors.Insert(0, hex);
            while (RecentColors.Count > 8) RecentColors.RemoveAt(RecentColors.Count - 1);
            _settings.Update(s => s.RecentColors = RecentColors.ToList());
        }, _dispatcher);
        _recentTimer.Stop();
    }

    /// <summary>Remembers a color once the user has settled on it (not every step of a drag).</summary>
    private void NoteRecentColor(ZoneLighting lighting)
    {
        if (!ZoneLighting.UsesColor(lighting.Mode) || _recentTimer == null) return;
        _pendingRecent = lighting.BaseColor.ToHex();
        _recentTimer.Stop();
        _recentTimer.Start();
    }

    private void OnParameterChanged(DeviceItem device, ParameterItem parameter)
    {
        _settings.Update(s => s.ForDevice(device.Key).Parameters[parameter.Definition.Property] = parameter.Value);
        device.Device.SetParameter(parameter.Definition.Property, parameter.Value);
    }

    // ------------------------------------------------------------------ settings panel

    public bool SettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            var ok = Autostart.SetEnabled(value);
            _startWithWindows = ok ? value : Autostart.IsEnabled();
            _settings.Update(s => s.StartWithWindows = _startWithWindows);
            Raise();
        }
    }

    public bool StartMinimized
    {
        get => _settings.Read(s => s.StartMinimized);
        set { _settings.Update(s => s.StartMinimized = value); Raise(); }
    }

    public bool CloseToTray
    {
        get => _settings.Read(s => s.CloseToTray);
        set { _settings.Update(s => s.CloseToTray = value); Raise(); }
    }

    public int ExitBehaviorIndex
    {
        get => _settings.Read(s => s.ExitBehavior) == ExitBehavior.TurnOff ? 1 : 0;
        set { _settings.Update(s => s.ExitBehavior = value == 1 ? ExitBehavior.TurnOff : ExitBehavior.KeepColors); Raise(); }
    }

    public bool EnableSmBus
    {
        get => _settings.Read(s => s.EnableSmBusDevices);
        set
        {
            if (EnableSmBus == value) return;
            _settings.Update(s => s.EnableSmBusDevices = value);
            Raise();
            _ = RescanAsync(importPlugins: false);
        }
    }

    public string PawnIoStatus => !PawnIoSmBus.IsDriverInstalled
        ? "PawnIO driver not installed. RAM lighting needs it."
        : Core.Platform.SystemInfo.IsElevated
            ? "PawnIO driver installed and active."
            : "PawnIO driver installed. RAM lighting needs SimpleRGB to run as administrator.";

    public bool PawnIoMissing => !PawnIoSmBus.IsDriverInstalled;

    public string PluginSummary { get; private set; } = "";

    public bool PluginBusy { get => _pluginBusy; private set { Set(ref _pluginBusy, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string PluginStatus { get => _pluginStatus; private set => Set(ref _pluginStatus, value); }

    public string VersionText => $"SimpleRGB {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    private void RefreshSettingsInfo()
    {
        _startWithWindows = Autostart.IsEnabled();
        Raise(nameof(StartWithWindows));
        var files = _manager.Library.EffectiveFiles();
        var bySource = files.GroupBy(f => f.Source).Select(g => $"{g.Count()} from {g.Key}");
        PluginSummary = files.Count == 0 ? "No plugins yet." : $"{files.Count} plugins ({string.Join(", ", bySource)})";
        Raise(nameof(PluginSummary));
        Raise(nameof(PawnIoStatus));
        Raise(nameof(PawnIoMissing));
    }

    private async Task ImportPluginsAsync()
    {
        PluginBusy = true;
        PluginStatus = "Copying plugins from SignalRGB…";
        try
        {
            var changed = await Task.Run(() => _manager.Library.ImportFromSignalRgb());
            PluginStatus = PluginLibrary.FindSignalRgbPluginFolders().Count == 0
                ? "SignalRGB isn't installed on this PC. Use the GitLab download instead."
                : $"{changed} plugin file(s) updated.";
            RefreshSettingsInfo();
            if (changed > 0) await RescanAsync(importPlugins: false);
        }
        catch (Exception ex) { PluginStatus = "Import failed: " + ex.Message; }
        finally { PluginBusy = false; }
    }

    private async Task DownloadPluginsAsync()
    {
        PluginBusy = true;
        PluginStatus = "Downloading plugins from gitlab.com/signalrgb…";
        try
        {
            var count = await _manager.Library.DownloadFromGitLabAsync();
            PluginStatus = $"Downloaded {count} plugin file(s).";
            RefreshSettingsInfo();
            await RescanAsync(importPlugins: false);
        }
        catch (Exception ex) { PluginStatus = "Download failed: " + ex.Message; }
        finally { PluginBusy = false; }
    }

    private void RestartAsAdmin() => RequestRestartElevated?.Invoke();

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("App", $"Could not open {path}: {ex.Message}"); }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("App", $"Could not open {url}: {ex.Message}"); }
    }
}

public sealed class EffectOption(LightingMode mode, string label) : ObservableObject
{
    private ZoneLighting _preview = new() { Mode = mode };
    public LightingMode Mode { get; } = mode;
    public string Label { get; } = label;
    public ZoneLighting Preview { get => _preview; set => Set(ref _preview, value); }
}
