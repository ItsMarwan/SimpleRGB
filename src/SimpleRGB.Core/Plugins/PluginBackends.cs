using SimpleRGB.Core.Hardware.Hid;

namespace SimpleRGB.Core.Plugins;

/// <summary>Thrown when the physical device disappeared; stops the device instead of retrying forever.</summary>
public sealed class DeviceGoneException(string message) : Exception(message);

/// <summary>Thrown when plugin code fails (script error, timeout…).</summary>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The HID side of a plugin instance: all endpoints of one USB device and the ones it opened.</summary>
public sealed class HidBackend : IDisposable
{
    private readonly Func<HidEndpointInfo, IHidConnection?> _opener;
    private readonly Dictionary<string, IHidConnection> _open = new();

    public HidPhysicalDevice Device { get; }
    public IHidConnection? Active { get; private set; }
    public int LastReadSize { get; set; }

    public HidBackend(HidPhysicalDevice device, Func<HidEndpointInfo, IHidConnection?>? opener = null)
    {
        Device = device;
        _opener = opener ?? (ep =>
        {
            var c = HidConnection.Open(ep, out var error);
            if (c == null && error != null) Diagnostics.Log.Warn("HID", error);
            return c;
        });
    }

    public IReadOnlyCollection<IHidConnection> OpenConnections => _open.Values;

    /// <summary>Opens (or reuses) an endpoint. The first one opened becomes active.</summary>
    public IHidConnection? Open(HidEndpointInfo endpoint)
    {
        if (_open.TryGetValue(endpoint.Path, out var existing)) return existing;
        var connection = _opener(endpoint);
        if (connection == null) return null;
        _open[endpoint.Path] = connection;
        Active ??= connection;
        return connection;
    }

    public bool SetActive(int iface, int usage, int usagePage, int? collection)
    {
        var match = Device.Endpoints.FirstOrDefault(e =>
            e.Interface == iface && e.Usage == usage && e.UsagePage == usagePage &&
            (collection == null || e.Collection == collection));
        if (match == null) return false;
        var connection = Open(match);
        if (connection == null) return false;
        Active = connection;
        return true;
    }

    public IHidConnection RequireActive()
    {
        var active = Active ?? throw new PluginException("No HID endpoint is open for this device.");
        if (active.IsDisconnected) throw new DeviceGoneException($"{Device.Product} was disconnected.");
        return active;
    }

    public void Dispose()
    {
        foreach (var c in _open.Values) c.Dispose();
        _open.Clear();
        Active = null;
    }
}
