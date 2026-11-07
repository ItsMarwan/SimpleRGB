using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Plugins;

namespace SimpleRGB.ViewModels;

/// <summary>An entry in the sidebar.</summary>
public abstract class NavItem : ObservableObject
{
    private string _name = "";
    private string _subtitle = "";
    private Brush? _preview;

    public string Name { get => _name; set => Set(ref _name, value); }

    private LightingSnapshot? _snapshot;
    private ZoneLighting? _previewLighting;
    /// <summary>What this device shows right now (drives the live LED previews).</summary>
    public LightingSnapshot? Snapshot { get => _snapshot; set => Set(ref _snapshot, value); }
    /// <summary>For items without zones (All devices): the lighting to preview.</summary>
    public ZoneLighting? PreviewLighting { get => _previewLighting; set => Set(ref _previewLighting, value); }
    public virtual string PreviewZoneId => "device";
    public virtual int PreviewLedCount => 16;
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public Brush? Preview { get => _preview; set => Set(ref _preview, value); }
    public abstract string Glyph { get; }

    /// <summary>Sidebar section ("" = no header).</summary>
    public virtual string Category => "";
    public virtual int CategoryOrder => 0;
}

public sealed class AllDevicesItem : NavItem
{
    public AllDevicesItem() { Name = "All devices"; }
    public override string Glyph => "";
}

public sealed class DeviceItem : NavItem
{
    private int _structureVersion = -1;
    private int _brightness = 100;
    private string _status = "";
    private Brush _stateBrush = Brushes.Gray;
    private bool _hasError;

    public DeviceItem(RgbDevice device)
    {
        Device = device;
        Name = device.Name;
    }

    public RgbDevice Device { get; }

    private bool _followsSync = true;
    /// <summary>False = keeps its own lighting even when "Sync all devices" is on.</summary>
    public bool FollowsSync { get => _followsSync; set { if (Set(ref _followsSync, value)) Raise(nameof(SyncText)); } }
    public string SyncText => FollowsSync ? "Follows sync" : "Own lighting";

    public override string PreviewZoneId => Zones.FirstOrDefault(z => z.LedCount > 1)?.Id ?? Zones.FirstOrDefault()?.Id ?? "device";
    public override int PreviewLedCount => 16;
    public string Key => Device.Key;

    public ObservableCollection<ZoneItem> Zones { get; } = new();
    public ObservableCollection<ParameterItem> Parameters { get; } = new();
    public ObservableCollection<DeviceMessage> Messages { get; } = new();

    public override string Category => KindText switch
    {
        "Memory" => "Memory",
        "Graphics card" => "Graphics card",
        "Motherboard" => "Motherboard",
        "Cooler display" => "Cooler",
        "Keyboard" => "Keyboard",
        "Mouse" => "Mouse",
        _ => "Other devices",
    };

    public override int CategoryOrder => Category switch
    {
        "Motherboard" => 1,
        "Graphics card" => 2,
        "Memory" => 3,
        "Cooler" => 4,
        "Keyboard" => 5,
        "Mouse" => 6,
        _ => 9,
    };

    /// <summary>A short, friendly name before numbering/renaming (e.g. "RAM stick").</summary>
    public string BaseName
    {
        get
        {
            if (Device.DeviceType == "ram" || KindText == "Memory") return "RAM stick";
            var n = Device.Name.Trim();
            if (n.EndsWith(" Controller", StringComparison.OrdinalIgnoreCase)) n = n[..^" Controller".Length];
            return n;
        }
    }

    private bool _isRenaming;
    private string _renameText = "";
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }

    public override string Glyph => Device.DeviceType switch
    {
        "cooler" => "\uE9CA",
        "keyboard" => "",
        "mouse" => "",
        "gpu" => "",
        _ => "",
    };

    public string KindText => Device.Connection switch
    {
        DeviceConnection.SmBus => Device.DeviceType == "ram" ? "Memory" : "SMBus",
        DeviceConnection.GpuI2C => "Graphics card",
        _ => Device.DeviceType switch
        {
            "motherboard" => "Motherboard",
            "cooler" => "Cooler display",
            "keyboard" => "Keyboard",
            "mouse" => "Mouse",
            "" => "USB device",
            var t => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t),
        },
    };

    public string ConnectionText => Device.ConnectionText;
    public string PluginText => $"{Device.Plugin.RelativePath} · from {Device.Plugin.Source}";

    public int Brightness { get => _brightness; set => Set(ref _brightness, Math.Clamp(value, 0, 100)); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public bool HasParameters => Parameters.Count > 0;
    public bool HasZones => Zones.Count > 0;

    /// <summary>Pulls the latest layout/state from the device (call on the UI thread).</summary>
    public void Refresh(Func<string, ZoneLighting> lightingFor, Func<string, int?> channelCount)
    {
        var s = Device.Structure;

        if (s.Version != _structureVersion)
        {
            _structureVersion = s.Version;
            var selected = Zones.Where(z => z.IsSelected).Select(z => z.Id).ToHashSet();
            var hadZones = Zones.Count > 0;
            Zones.Clear();
            foreach (var z in s.Zones)
            {
                var item = new ZoneItem(z, lightingFor(z.Id)) { IsSelected = !hadZones || selected.Contains(z.Id) };
                if (z.Kind == ZoneKind.Channel && channelCount(z.Id) is { } n) item.SetLedCount(n);
                Zones.Add(item);
            }

            var existing = Parameters.ToDictionary(p => p.Definition.Property);
            Parameters.Clear();
            foreach (var p in s.Parameters.Where(p => !p.IsManagedByHost && p.Type is "combobox" or "boolean" or "number" or "color" or "textfield"))
                Parameters.Add(existing.TryGetValue(p.Property, out var old) && old.Definition == p
                    ? old
                    : new ParameterItem(this, p, Device.GetParameterValue(p.Property) ?? p.Default));

            Messages.Clear();
            foreach (var m in s.Messages) Messages.Add(m);
            Raise(nameof(HasParameters));
            Raise(nameof(PreviewZoneId));
            Raise(nameof(HasZones));
        }

        Status = Device.State switch
        {
            DeviceState.Running => "Running",
            DeviceState.Starting => "Starting…",
            DeviceState.Paused => Device.StatusText,
            DeviceState.Disconnected => "Disconnected",
            DeviceState.Stopped => "Stopped",
            _ => Device.StatusText,
        };
        HasError = Device.State is DeviceState.Error or DeviceState.Disconnected;
        StateBrush = Device.State switch
        {
            DeviceState.Running => (Brush)System.Windows.Application.Current.Resources["Success"],
            DeviceState.Starting or DeviceState.Paused => (Brush)System.Windows.Application.Current.Resources["Warning"],
            DeviceState.Error => (Brush)System.Windows.Application.Current.Resources["Danger"],
            _ => (Brush)System.Windows.Application.Current.Resources["Muted"],
        };
        Subtitle = Device.State == DeviceState.Running
            ? $"{Zones.Count} zone{(Zones.Count == 1 ? "" : "s")} · {Device.ConnectionText.Split(',')[0]}"
            : Status;
    }
}

public sealed class ZoneItem : ObservableObject
{
    private ZoneLighting _lighting;
    private bool _isSelected;
    private int _ledCount;

    public ZoneItem(ZoneInfo info, ZoneLighting lighting)
    {
        Info = info;
        _lighting = lighting;
        _ledCount = info.LedCount;
    }

    public ZoneInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool IsChannel => Info.Kind == ZoneKind.Channel;
    public int LedLimit => Info.LedLimit;

    public string Detail => Info.Kind switch
    {
        ZoneKind.Channel => $"ARGB header · {LedCount} LEDs",
        ZoneKind.Subdevice when Info.LedCount == 1 => "RGB header",
        _ => $"{Info.LedCount} LED{(Info.LedCount == 1 ? "" : "s")}",
    };

    public int LedCount
    {
        get => _ledCount;
        set { if (Set(ref _ledCount, value)) Raise(nameof(Detail)); }
    }

    public void SetLedCount(int count) => LedCount = Math.Clamp(count, 0, LedLimit);

    public ZoneLighting Lighting
    {
        get => _lighting;
        set
        {
            _lighting = value;
            Raise();
            Raise(nameof(Swatch));
            Raise(nameof(LightingText));
        }
    }

    public Brush Swatch => LightingVisuals.BrushFor(_lighting);
    public string LightingText => _syncOverride is { } sync ? "Sync · " + LightingVisuals.Describe(sync) : LightingVisuals.Describe(_lighting);

    private ZoneLighting? _syncOverride;
    /// <summary>Set while the device follows "Sync all devices" (the zone then shows the sync lighting).</summary>
    public ZoneLighting? SyncOverride
    {
        get => _syncOverride;
        set { if (_syncOverride == value) return; _syncOverride = value; Raise(); Raise(nameof(LightingText)); }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class ParameterItem : ObservableObject
{
    private readonly DeviceItem _owner;
    private string _value;

    public ParameterItem(DeviceItem owner, PluginParameter definition, string value)
    {
        _owner = owner;
        Definition = definition;
        _value = value;
    }

    public PluginParameter Definition { get; }
    public string Label => Definition.Label;
    public string Description => Definition.Description;
    public string Type => Definition.Type;
    public IReadOnlyList<string> Options => Definition.Values;
    public double Min => Definition.Min ?? 0;
    public double Max => Definition.Max ?? 100;
    public double Step => Definition.Step ?? 1;

    /// <summary>Raised when the user changes the value.</summary>
    public static event Action<DeviceItem, ParameterItem>? Changed;

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            Raise(nameof(BoolValue));
            Raise(nameof(NumberValue));
            Changed?.Invoke(_owner, this);
        }
    }

    public bool BoolValue
    {
        get => _value.Trim() is "1" || _value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public double NumberValue
    {
        get => double.TryParse(_value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : Min;
        set
        {
            var step = Step > 0 ? Step : 1;
            Value = (Math.Round(value / step) * step).ToString(CultureInfo.InvariantCulture);
        }
    }
}

public sealed class NoticeItem
{
    public required Notice Notice { get; init; }
    public string Text => Notice.Text;
    public bool NeedsAdmin => Notice.Kind == NoticeKind.NeedsAdmin;
    public bool CanClose => Notice.App != null;
    public bool CanStopStartup => Notice.App != null && Notice.AppStartsWithWindows;
    public string CloseText => $"Close {Core.Platform.ConflictingApps.ProcessName(Notice.App ?? "")}";
    public string Glyph => Notice.Kind == NoticeKind.Info ? "" : "";
}
