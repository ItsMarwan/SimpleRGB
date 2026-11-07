namespace SimpleRGB.Core.Hardware.Hid;

/// <summary>One HID top-level collection ("endpoint" in SignalRGB terms).</summary>
public sealed record HidEndpointInfo
{
    public required string Path { get; init; }
    public required int VendorId { get; init; }
    public required int ProductId { get; init; }

    /// <summary>USB interface number from "&amp;MI_xx", or -1 for single-interface devices (same as hidapi).</summary>
    public required int Interface { get; init; }

    /// <summary>Collection number from "&amp;COLxx", or 0.</summary>
    public required int Collection { get; init; }

    public required int UsagePage { get; init; }
    public required int Usage { get; init; }
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string SerialNumber { get; init; } = "";

    /// <summary>Instance id of the USB device that owns this collection (groups endpoints of one physical device).</summary>
    public required string ParentInstanceId { get; init; }

    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} if={Interface} col={Collection} page=0x{UsagePage:X4} usage=0x{Usage:X2} " +
        $"in={InputReportLength} out={OutputReportLength} feat={FeatureReportLength}";
}

/// <summary>A physical USB device with all of its HID endpoints.</summary>
public sealed record HidPhysicalDevice(
    string InstanceId,
    int VendorId,
    int ProductId,
    string Product,
    string Manufacturer,
    string SerialNumber,
    IReadOnlyList<HidEndpointInfo> Endpoints)
{
    public string Key => $"{VendorId:X4}:{ProductId:X4}:{InstanceId}";
}
