using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SimpleRGB.Core.Diagnostics;
using static SimpleRGB.Core.Hardware.Hid.HidNative;

namespace SimpleRGB.Core.Hardware.Hid;

public static partial class HidEnumerator
{
    [GeneratedRegex(@"[&#]mi_([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"&col([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionRegex();

    /// <summary>Lists every present HID collection. Never throws; failures are logged and skipped.</summary>
    public static unsafe List<HidEndpointInfo> EnumerateEndpoints()
    {
        var result = new List<HidEndpointInfo>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            Log.Error("HID", $"SetupDiGetClassDevs failed ({Marshal.GetLastWin32Error()})");
            return result;
        }

        try
        {
            for (var index = 0; ; index++)
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref ifData)) break;

                var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var required, ref devInfo);
                if (required <= 0) continue;

                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (4 + WCHAR, padded).
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, detail, required, out _, ref devInfo)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;

                    var info = QueryEndpoint(path, devInfo.DevInst);
                    if (info != null) result.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    /// <summary>Groups endpoints by their physical USB device.</summary>
    public static List<HidPhysicalDevice> EnumerateDevices()
    {
        return EnumerateEndpoints()
            .GroupBy(e => (e.VendorId, e.ProductId, e.ParentInstanceId))
            .Select(g =>
            {
                var eps = g.OrderBy(e => e.Interface).ThenBy(e => e.Collection).ToList();
                var named = eps.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Product)) ?? eps[0];
                return new HidPhysicalDevice(g.Key.ParentInstanceId, g.Key.VendorId, g.Key.ProductId,
                    named.Product, named.Manufacturer, named.SerialNumber, eps);
            })
            .OrderBy(d => d.VendorId).ThenBy(d => d.ProductId)
            .ToList();
    }

    private static unsafe HidEndpointInfo? QueryEndpoint(string path, int devInst)
    {
        // Access 0 = query only. Works even for keyboards/mice that Windows opens exclusively.
        using var handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attrs)) return null;

        HIDP_CAPS caps = default;
        if (HidD_GetPreparsedData(handle, out var pp))
        {
            try { _ = HidP_GetCaps(pp, out caps); }
            finally { HidD_FreePreparsedData(pp); }
        }

        var mi = InterfaceRegex().Match(path);
        var col = CollectionRegex().Match(path);

        return new HidEndpointInfo
        {
            Path = path,
            VendorId = attrs.VendorID,
            ProductId = attrs.ProductID,
            Interface = mi.Success ? int.Parse(mi.Groups[1].Value, NumberStyles.HexNumber) : -1,
            Collection = col.Success ? int.Parse(col.Groups[1].Value, NumberStyles.HexNumber) : 0,
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputReportLength = caps.InputReportByteLength,
            OutputReportLength = caps.OutputReportByteLength,
            FeatureReportLength = caps.FeatureReportByteLength,
            Product = ReadString(handle, 0),
            Manufacturer = ReadString(handle, 1),
            SerialNumber = ReadString(handle, 2),
            ParentInstanceId = FindUsbParent(devInst) ?? $"VID_{attrs.VendorID:X4}&PID_{attrs.ProductID:X4}",
        };
    }

    private static unsafe string ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int which)
    {
        var buf = stackalloc char[256];
        new Span<char>(buf, 256).Clear();
        var ok = which switch
        {
            0 => HidD_GetProductString(handle, buf, 512),
            1 => HidD_GetManufacturerString(handle, buf, 512),
            _ => HidD_GetSerialNumberString(handle, buf, 512),
        };
        if (!ok) return "";
        buf[255] = '\0';
        return new string(buf).Trim();
    }

    /// <summary>Walks up the device tree to the USB device node (not an interface node).</summary>
    private static string? FindUsbParent(int devInst)
    {
        var current = devInst;
        string? firstParent = null;
        for (var depth = 0; depth < 6; depth++)
        {
            if (CM_Get_Parent(out var parent, current, 0) != 0) break;
            var id = GetDeviceId(parent);
            if (id == null) break;
            firstParent ??= id;
            if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
                !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                return id;
            current = parent;
        }
        return firstParent;
    }
}
