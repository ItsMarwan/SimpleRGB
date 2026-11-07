namespace SimpleRGB.Core.Plugins;

public enum PluginKind { Unknown, Hid, SmBus, Other }

/// <summary>Static facts about a plugin file, read once and cached.</summary>
public sealed record PluginInfo
{
    public required string FilePath { get; init; }
    /// <summary>Path relative to the plugin root, e.g. "Asus/Motherboard Controllers/Asus_Motherboard_Controller.js".</summary>
    public required string RelativePath { get; init; }
    /// <summary>Root folder that relative imports must stay inside.</summary>
    public required string RootDir { get; init; }
    /// <summary>Where the file came from: Custom, SignalRGB or GitLab.</summary>
    public required string Source { get; init; }

    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public PluginKind Kind { get; init; }
    public string RawType { get; init; } = "";
    public IReadOnlyList<int> VendorIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> ProductIds { get; init; } = Array.Empty<int>();
    public string? LoadError { get; init; }

    public bool Matches(int vendorId, int productId) =>
        LoadError == null && Kind == PluginKind.Hid && VendorIds.Contains(vendorId) && ProductIds.Contains(productId);

    public override string ToString() => $"{Name} ({RelativePath})";
}

/// <summary>A user-facing setting a plugin declares in ControllableParameters().</summary>
public sealed record PluginParameter
{
    public required string Property { get; init; }
    public required string Label { get; init; }
    /// <summary>color, combobox, number, boolean, textfield or hue.</summary>
    public required string Type { get; init; }
    public string Group { get; init; } = "";
    public string Description { get; init; } = "";
    public string Default { get; init; } = "";
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }

    /// <summary>Parameters SimpleRGB drives itself and never shows to the user.</summary>
    public bool IsManagedByHost => Property is "LightingMode" or "forcedColor" or "shutdownColor";
}
