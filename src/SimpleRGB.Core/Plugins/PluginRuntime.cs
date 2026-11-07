using System.Globalization;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Hardware.Hid;
using SimpleRGB.Core.Hardware.SmBus;
using SimpleRGB.Core.Lighting;
using SimpleRGB.Core.Platform;

namespace SimpleRGB.Core.Plugins;

/// <summary>
/// One plugin instance running in its own sandboxed JavaScript engine (Jint). Plugins get no file,
/// network, process or .NET access: only the SignalRGB device/bus API implemented here.
/// Not thread-safe: use one instance from one thread.
/// </summary>
public sealed partial class PluginRuntime : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);
    private const int DefaultReadTimeoutMs = 1000;

    private readonly Engine _engine;
    private readonly DeviceModel _model = new();
    // Read by the UI thread, written by the device thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _paramValues = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _savedParams;
    private readonly Dictionary<string, JsValue> _channelObjects = new();
    private readonly HashSet<string> _warnedOnce = new();
    private ObjectInstance? _module;
    private IFrameColors _colors = BlackFrame.Instance;
    private int _alertCounter;

    // Log rate limiting (plugins sometimes log every frame).
    private long _logWindowStart = Environment.TickCount64;
    private int _logCount, _logSuppressed;

    public PluginInfo Info { get; }
    public HidBackend? Hid { get; }
    public SmBus? Bus { get; }
    public byte BusAddress { get; }
    public string LogName { get; private set; }

    /// <summary>Structure changed since the last <see cref="TakeStructure"/>.</summary>
    internal bool StructureDirty => _model.Dirty;
    public int FrameRateTarget => _model.FrameRateTarget;
    public int MinimumFrameRate => _model.MinimumFrameRate;

    private PluginRuntime(PluginInfo info, HidBackend? hid, SmBus? bus, byte address,
        IReadOnlyDictionary<string, string>? savedParams, IReadOnlyDictionary<string, int>? channelCounts)
    {
        Info = info;
        Hid = hid;
        Bus = bus;
        BusAddress = address;
        _savedParams = savedParams ?? new Dictionary<string, string>();
        if (channelCounts != null)
            foreach (var (k, v) in channelCounts) _model.RequestedChannelCounts[k] = v;
        LogName = string.IsNullOrEmpty(info.Name) ? Path.GetFileNameWithoutExtension(info.FilePath) : info.Name;

        _engine = new Engine(o =>
        {
            o.TimeoutInterval(CallTimeout);
            o.LimitRecursion(2000);
            o.Strict = false;
        });
    }

    // ---------------------------------------------------------------- creation

    /// <summary>Loads a plugin for reading its metadata only (no hardware attached).</summary>
    public static PluginRuntime LoadForMetadata(PluginInfo info) => Load(info, null, null, 0, null, null);

    public static PluginRuntime LoadForHid(PluginInfo info, HidBackend hid,
        IReadOnlyDictionary<string, string>? savedParams = null, IReadOnlyDictionary<string, int>? channelCounts = null) =>
        Load(info, hid, null, 0, savedParams, channelCounts);

    public static PluginRuntime LoadForSmBus(PluginInfo info, SmBus bus, byte address,
        IReadOnlyDictionary<string, string>? savedParams = null, IReadOnlyDictionary<string, int>? channelCounts = null) =>
        Load(info, null, bus, address, savedParams, channelCounts);

    private static PluginRuntime Load(PluginInfo info, HidBackend? hid, SmBus? bus, byte address,
        IReadOnlyDictionary<string, string>? savedParams, IReadOnlyDictionary<string, int>? channelCounts)
    {
        var runtime = new PluginRuntime(info, hid, bus, address, savedParams, channelCounts);
        try
        {
            runtime.LoadModule();
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    [GeneratedRegex(@"\bfrom\s*['""]([^'""]+)['""]")]
    private static partial Regex ImportRegex();

    private void LoadModule()
    {
        var source = File.ReadAllText(Info.FilePath);
        InstallGlobals();

        // Imports: SignalRGB built-ins get stubs, relative imports are resolved inside the plugin folder only.
        var added = new HashSet<string>(StringComparer.Ordinal);
        AddImports(source, Path.GetDirectoryName(Info.FilePath)!, added, depth: 0);

        _engine.Modules.Add("plugin", source);
        try
        {
            _engine.Constraints.Reset();
            _module = _engine.Modules.Import("plugin");
        }
        catch (Exception ex) when (ex is not DeviceGoneException)
        {
            throw new PluginException($"Failed to load {Info.RelativePath}: {Describe(ex)}", ex);
        }

        var name = GetString("Name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            _model.Name = name;
            LogName = name;
        }

        var size = CallExportRaw("Size");
        if (size.IsArray()) SetSizeFrom(size);
        ApplyExportedLeds();
    }

    private void AddImports(string source, string directory, HashSet<string> added, int depth)
    {
        if (depth > 8) return;
        foreach (Match m in ImportRegex().Matches(source))
        {
            var spec = m.Groups[1].Value;
            if (!added.Add(spec)) continue;

            if (spec.StartsWith("@SignalRGB/", StringComparison.OrdinalIgnoreCase))
            {
                var name = spec["@SignalRGB/".Length..].ToLowerInvariant();
                if (name.EndsWith(".js")) name = name[..^3];
                var code = name switch
                {
                    "errors" => JsStubs.ErrorsModule,
                    "systeminfo" => JsStubs.SystemInfoModule,
                    "permissions" => JsStubs.PermissionsModule,
                    _ => JsStubs.GenericModule(name),
                };
                _engine.Modules.Add(spec, code);
                continue;
            }

            if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal))
            {
                var full = Path.GetFullPath(Path.Combine(directory, spec));
                var root = Path.GetFullPath(Info.RootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    throw new PluginException($"Import '{spec}' is missing or outside the plugin folder.");
                var code = File.ReadAllText(full);
                _engine.Modules.Add(spec, code);
                AddImports(code, Path.GetDirectoryName(full)!, added, depth + 1);
                continue;
            }

            throw new PluginException($"Unsupported import '{spec}'.");
        }
    }

    // ---------------------------------------------------------------- exports

    public bool HasExport(string name) => _module != null && _module.Get(name) is { } v && v.IsObject() && v.AsObject() is Function;

    private JsValue CallExportRaw(string name, params JsValue[] args)
    {
        if (_module == null) return JsValue.Undefined;
        var fn = _module.Get(name);
        if (fn.IsUndefined() || !fn.IsObject() || fn.AsObject() is not Function) return JsValue.Undefined;
        try
        {
            _engine.Constraints.Reset();
            return CallFn(fn, args);
        }
        catch (DeviceGoneException) { throw; }
        catch (Exception ex)
        {
            throw new PluginException($"{name}() failed: {Describe(ex)}", ex);
        }
        finally
        {
            if (Bus != null) Bus.Lock.ExitAll(); // never leave the bus locked for other devices
        }
    }

    public string GetString(string export)
    {
        try
        {
            var v = CallExportRaw(export);
            return v.IsUndefined() || v.IsNull() ? "" : TypeConverter.ToString(v);
        }
        catch (PluginException) { return ""; }
    }

    public List<int> GetIntList(string export)
    {
        var list = new List<int>();
        JsValue v;
        try { v = CallExportRaw(export); } catch (PluginException) { return list; }
        if (v.IsArray())
        {
            var arr = v.AsArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                var n = TypeConverter.ToNumber(arr[i]);
                if (!double.IsNaN(n)) list.Add((int)n);
            }
        }
        else if (v.IsNumber())
        {
            list.Add((int)v.AsNumber());
        }
        return list;
    }

    public List<string> GetStringList(string export)
    {
        var list = new List<string>();
        JsValue v;
        try { v = CallExportRaw(export); } catch (PluginException) { return list; }
        if (!v.IsArray()) return list;
        var arr = v.AsArray();
        for (uint i = 0; i < arr.Length; i++)
            if (!arr[i].IsUndefined()) list.Add(TypeConverter.ToString(arr[i]));
        return list;
    }

    /// <summary>Reads ControllableParameters() and publishes them as globals (user values win over defaults).</summary>
    public void LoadParameters()
    {
        JsValue v;
        try { v = CallExportRaw("ControllableParameters"); }
        catch (PluginException ex) { Warn(ex.Message); return; }
        if (!v.IsArray()) return;
        var arr = v.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var p = ParseParameter(arr[i]);
            if (p != null) RegisterParameter(p);
        }
    }

    public bool Validate(HidEndpointInfo endpoint)
    {
        if (!HasExport("Validate")) return true;
        var obj = EndpointObject(endpoint);
        var result = CallExportRaw("Validate", obj);
        return TypeConverter.ToBoolean(result);
    }

    /// <summary>Runs the plugin's Scan(bus) on a "free address" bus and returns the addresses it claims.</summary>
    public List<int> Scan(SmBus bus)
    {
        var freeBus = CreateBusObject(bus, fixedAddress: null);
        JsValue result;
        try { result = CallExportRaw("Scan", freeBus); }
        finally { bus.Lock.ExitAll(); }
        var list = new List<int>();
        if (!result.IsArray()) return list;
        var arr = result.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var n = TypeConverter.ToNumber(arr[i]);
            if (!double.IsNaN(n) && n is >= 0 and < 128 && !list.Contains((int)n)) list.Add((int)n);
        }
        return list;
    }

    public void Initialize(IFrameColors colors)
    {
        _colors = colors;
        CallExportRaw("Initialize");
        ApplyExportedLeds();
    }

    public void Render(IFrameColors colors)
    {
        _colors = colors;
        CallExportRaw("Render");
    }

    public void Shutdown(IFrameColors colors, bool systemSuspending)
    {
        _colors = colors;
        CallExportRaw("Shutdown", systemSuspending);
    }

    /// <summary>Changes a user parameter and fires the plugin's on&lt;Name&gt;Changed callback.</summary>
    public void SetParameter(string property, string value)
    {
        var p = _model.Parameters.FirstOrDefault(x => x.Property == property);
        if (p == null) return;
        _paramValues[property] = value;
        _engine.SetValue(property, ToJsParameterValue(p, value));
        CallExportRaw($"on{property}Changed");
    }

    /// <summary>Sets a host-managed global such as shutdownColor or LightingMode.</summary>
    public void SetHostGlobal(string property, string value) => _engine.SetValue(property, value);

    public string? GetParameterValue(string property) => _paramValues.TryGetValue(property, out var v) ? v : null;

    internal DeviceStructure TakeStructure() => _model.Snapshot();

    internal DeviceModel Model => _model;

    // ---------------------------------------------------------------- globals

    private void InstallGlobals()
    {
        _engine.SetValue("__hostWarn", new ClrFunction(_engine, "__hostWarn", (_, a) => { Warn(Str(a, 0)); return JsValue.Undefined; }));
        _engine.SetValue("__hostLog", new ClrFunction(_engine, "__hostLog", (_, a) => { DeviceLog(Str(a, 0)); return JsValue.Undefined; }));

        var sys = new JsObject(_engine);
        sys.Set("model", SystemInfo.MotherboardModel);
        sys.Set("manufacturer", SystemInfo.MotherboardManufacturer);
        sys.Set("biosVendor", SystemInfo.BiosVendor);
        sys.Set("biosVersion", SystemInfo.BiosVersion);
        _engine.SetValue("__sys", sys);

        _engine.Execute(JsStubs.Prelude);

        // Common host-managed globals. Lighting always comes from SimpleRGB ("Canvas" mode).
        _engine.SetValue("LightingMode", "Canvas");
        _engine.SetValue("forcedColor", "#000000");
        _engine.SetValue("shutdownColor", "#000000");

        var device = CreateDeviceObject();
        var wrap = _engine.GetValue("__wrapHost");
        _engine.SetValue("device", CallFn(wrap, device, "device"));

        if (Bus != null)
            _engine.SetValue("bus", CreateBusObject(Bus, BusAddress));

        // Read-only PC sensors for display devices (cooler screens). -1 = not available.
        var sensors = new JsObject(_engine);
        static double V(double? v) => v is { } x && double.IsFinite(x) ? Math.Round(x) : -1;
        Fn(sensors, "cpuTemp", _ => V(Sensors.SensorService.Shared.Current.CpuTemp));
        Fn(sensors, "cpuLoad", _ => V(Sensors.SensorService.Shared.Current.CpuLoad));
        Fn(sensors, "gpuTemp", _ => V(Sensors.SensorService.Shared.Current.GpuTemp));
        Fn(sensors, "gpuLoad", _ => V(Sensors.SensorService.Shared.Current.GpuLoad));
        Fn(sensors, "setPollInterval", a => { Sensors.SensorService.PollIntervalMs = (int)Num(a, 0, 1000); return JsValue.Undefined; });
        _engine.SetValue("sensors", sensors);
    }

    /// <summary>The plugin wants Shutdown() on every exit (e.g. so a status display never shows stale values).</summary>
    public bool AlwaysShutdown => _module != null && TypeConverter.ToBoolean(SafeExport("AlwaysShutdown"));

    /// <summary>The plugin must not write while one of its ConflictingProcesses runs (the device would flicker).</summary>
    public bool PauseWhenConflicting => _module != null && TypeConverter.ToBoolean(SafeExport("PauseWhenConflicting"));

    private JsValue SafeExport(string name)
    {
        try { return CallExportRaw(name); } catch (PluginException) { return JsValue.Undefined; }
    }

    private void Fn(ObjectInstance target, string name, Func<JsValue[], JsValue> body)
    {
        target.FastSetDataProperty(name, new ClrFunction(_engine, name, (_, args) =>
        {
            try
            {
                return body(args);
            }
            catch (DeviceGoneException) { throw; }
            catch (Exception ex)
            {
                // Host errors must never escape into script code (JS try/catch can't catch them).
                WarnOnce("err:" + name, $"device API {name}() failed: {ex.Message}");
                return JsValue.Undefined;
            }
        }));
    }

    private JsValue CreateDeviceObject()
    {
        var d = new JsObject(_engine);

        // ---- logging & timing
        Fn(d, "log", a => { DeviceLog(Format(Arg(a, 0))); return JsValue.Undefined; });
        Fn(d, "pause", a => { Pause(Num(a, 0, 0)); return JsValue.Undefined; });
        Fn(d, "setFrameRateTarget", a => { _model.FrameRateTarget = (int)Math.Clamp(Num(a, 0, 0), 0, 240); return JsValue.Undefined; });
        Fn(d, "setMinimumFrameRate", a => { _model.MinimumFrameRate = (int)Math.Clamp(Num(a, 0, 0), 0, 30); return JsValue.Undefined; });

        // ---- identity
        Fn(d, "vendorId", _ => Hid?.Device.VendorId ?? 0);
        Fn(d, "productId", _ => Hid?.Device.ProductId ?? 0);
        Fn(d, "getMotherboardName", _ => SystemInfo.MotherboardModel);
        Fn(d, "getDeviceInfo", _ =>
        {
            var o = new JsObject(_engine);
            o.Set("product", Hid?.Device.Product ?? "");
            o.Set("manufacturer", Hid?.Device.Manufacturer ?? "");
            o.Set("serialNumber", Hid?.Device.SerialNumber ?? "");
            o.Set("vendorId", Hid?.Device.VendorId ?? 0);
            o.Set("productId", Hid?.Device.ProductId ?? 0);
            return o;
        });
        Fn(d, "setName", a => { var n = Str(a, 0); if (!string.IsNullOrWhiteSpace(n)) { _model.Name = n; LogName = n; _model.Touch(); } return JsValue.Undefined; });
        Fn(d, "getBrightness", _ => 100);
        Fn(d, "fanControlDisabled", _ => true); // SimpleRGB never drives fans.
        Fn(d, "lcdControlAllowed", _ => false);
        Fn(d, "getFanlevel", _ => 50);
        Fn(d, "getNormalizedFanlevel", _ => 0.5);

        // ---- HID transfers
        Fn(d, "write", a =>
        {
            var conn = RequireHid().RequireActive();
            var data = Bytes(Arg(a, 0), LengthArg(a, 1));
            return data.Length == 0 ? -1 : conn.Write(data);
        });
        Fn(d, "send_report", a =>
        {
            var conn = RequireHid().RequireActive();
            var data = Bytes(Arg(a, 0), LengthArg(a, 1));
            return data.Length == 0 ? -1 : conn.SendFeatureReport(data);
        });
        Fn(d, "read", a =>
        {
            var hid = RequireHid();
            var conn = hid.RequireActive();
            var length = LengthArg(a, 1) ?? Math.Max(conn.Endpoint.InputReportLength, 1);
            var timeout = (int)Num(a, 2, DefaultReadTimeoutMs);
            var buffer = new byte[Math.Clamp(length, 1, 4096)];
            var n = conn.Read(buffer, timeout);
            hid.LastReadSize = Math.Max(n, 0);
            return ByteArray(buffer);
        });
        Fn(d, "get_report", a =>
        {
            var hid = RequireHid();
            var conn = hid.RequireActive();
            var length = LengthArg(a, 1) ?? Math.Max(conn.Endpoint.FeatureReportLength, 1);
            var buffer = Bytes(Arg(a, 0), Math.Clamp(length, 1, 4096));
            var n = conn.GetFeatureReport(buffer);
            hid.LastReadSize = Math.Max(n, 0);
            if (n < 0) Array.Clear(buffer);
            return ByteArray(buffer);
        });
        Fn(d, "input_report", a =>
        {
            var hid = RequireHid();
            var conn = hid.RequireActive();
            var length = LengthArg(a, 1) ?? Math.Max(conn.Endpoint.InputReportLength, 1);
            var buffer = Bytes(Arg(a, 0), Math.Clamp(length, 1, 4096));
            var n = conn.GetInputReport(buffer);
            hid.LastReadSize = Math.Max(n, 0);
            if (n < 0) Array.Clear(buffer);
            return ByteArray(buffer);
        });
        Fn(d, "getLastReadSize", _ => Hid?.LastReadSize ?? 0);
        Fn(d, "clearReadBuffer", _ => { ClearReadBuffer(); return JsValue.Undefined; });
        Fn(d, "flush", _ => { ClearReadBuffer(); return JsValue.Undefined; });
        Fn(d, "set_endpoint", a =>
        {
            var hid = RequireHid();
            int? collection = Arg(a, 3).IsUndefined() ? null : (int)Num(a, 3, 0);
            if (!hid.SetActive((int)Num(a, 0, -1), (int)Num(a, 1, 0), (int)Num(a, 2, 0), collection))
                WarnOnce($"ep:{Num(a, 0, -1)}:{Num(a, 1, 0)}:{Num(a, 2, 0)}", "set_endpoint: requested endpoint not found on this device");
            return JsValue.Undefined;
        });
        Fn(d, "getHidEndpoints", _ =>
        {
            var eps = Hid?.Device.Endpoints ?? (IReadOnlyList<HidEndpointInfo>)Array.Empty<HidEndpointInfo>();
            return new JsArray(_engine, eps.Select(e => (JsValue)EndpointObject(e)).ToArray());
        });
        Fn(d, "getHidInfo", _ =>
        {
            var ep = Hid?.Active?.Endpoint;
            var o = new JsObject(_engine);
            o.Set("featureLength", ep?.FeatureReportLength ?? 0);
            o.Set("readLength", ep?.InputReportLength ?? 0);
            o.Set("writeLength", ep?.OutputReportLength ?? 0);
            return o;
        });
        Fn(d, "bulk_transfer", _ => { WarnOnce("bulk", "bulk_transfer is not supported (raw USB devices need libusb)"); return new JsArray(_engine); });
        Fn(d, "control_transfer", _ => { WarnOnce("ctrl", "control_transfer is not supported"); return new JsArray(_engine); });

        // ---- main device layout
        Fn(d, "setSize", a => { SetSizeFrom(Arg(a, 0)); return JsValue.Undefined; });
        Fn(d, "setControllableLeds", a =>
        {
            _model.LedNames = StringList(Arg(a, 0));
            _model.LedPositions = Positions(Arg(a, 1));
            _model.Touch();
            return JsValue.Undefined;
        });
        Fn(d, "color", a =>
        {
            var (index, count) = DeviceModel.IndexOf(_model.LedPositions, Num(a, 0, 0), Num(a, 1, 0), _model.Width, _model.Height);
            return ColorArray(_colors.DeviceLed(index, count));
        });
        foreach (var noop in new[] { "setImageFromUrl", "setImageFromBase64", "setImageFromResource", "setSubdeviceImage",
                     "setSubdeviceImageUrl", "SetIsSubdeviceController", "reportLcdSupport", "setProtocol", "repollName",
                     "repollLeds", "repollSize", "createFanControl", "removeFanControl", "setRPM", "createTemperatureSensor",
                     "removeTemperatureSensor", "SetTemperature", "setSubdeviceLedMap" })
            Fn(d, noop, _ => JsValue.Undefined);
        Fn(d, "getImageBuffer", _ => new JsArray(_engine));
        Fn(d, "ConvertColorToImageBuffer", _ => new JsArray(_engine));
        Fn(d, "addFeature", _ => JsValue.Undefined); // feature globals (battery, keyboard…) always exist as stubs

        // ---- subdevices
        Fn(d, "createSubdevice", a =>
        {
            var id = Str(a, 0);
            if (id.Length > 0 && _model.FindSubdevice(id) == null)
            {
                _model.Subdevices.Add(new DeviceModel.Subdevice { Id = id, Name = id });
                _model.Touch();
            }
            return JsValue.Undefined;
        });
        Fn(d, "removeSubdevice", a => { _model.Subdevices.RemoveAll(s => s.Id == Str(a, 0)); _model.Touch(); return JsValue.Undefined; });
        Fn(d, "setSubdeviceName", a => { if (_model.FindSubdevice(Str(a, 0)) is { } s) { s.Name = Str(a, 1); _model.Touch(); } return JsValue.Undefined; });
        Fn(d, "setSubdeviceSize", a =>
        {
            if (_model.FindSubdevice(Str(a, 0)) is { } s)
            {
                s.Width = Math.Max(1, (int)Num(a, 1, 1));
                s.Height = Math.Max(1, (int)Num(a, 2, 1));
            }
            return JsValue.Undefined;
        });
        Fn(d, "setSubdeviceLeds", a =>
        {
            if (_model.FindSubdevice(Str(a, 0)) is { } s)
            {
                s.LedNames = StringList(Arg(a, 1));
                s.LedPositions = Positions(Arg(a, 2));
                _model.Touch();
            }
            return JsValue.Undefined;
        });
        Fn(d, "subdeviceColor", a =>
        {
            var id = Str(a, 0);
            var s = _model.FindSubdevice(id);
            var (index, count) = s == null
                ? (0, 1)
                : DeviceModel.IndexOf(s.LedPositions, Num(a, 1, 0), Num(a, 2, 0), s.Width, s.Height);
            return ColorArray(_colors.SubdeviceLed(id, index, count));
        });
        Fn(d, "getCurrentSubdevices", _ => new JsArray(_engine, _model.Subdevices.Select(s => (JsValue)s.Id).ToArray()));

        // ---- component channels (ARGB headers etc.)
        Fn(d, "addChannel", a =>
        {
            var id = Str(a, 0);
            if (id.Length == 0) return JsValue.Undefined;
            var limit = Math.Clamp((int)Num(a, 1, 60), 0, 1024);
            var existing = _model.FindChannel(id);
            if (existing == null)
            {
                // Default: light the whole header (unused LED data is harmless on WS2812-style strips).
                var count = _model.RequestedChannelCounts.TryGetValue(id, out var requested)
                    ? requested
                    : (int)Num(a, 2, limit);
                _model.Channels.Add(new DeviceModel.Channel { Id = id, LedLimit = limit, LedCount = Math.Clamp(count, 0, limit) });
            }
            else
            {
                existing.LedLimit = limit;
                existing.LedCount = Math.Clamp(existing.LedCount, 0, limit);
            }
            _model.Touch();
            return JsValue.Undefined;
        });
        Fn(d, "removeChannel", a => { _model.Channels.RemoveAll(c => c.Id == Str(a, 0)); _channelObjects.Remove(Str(a, 0)); _model.Touch(); return JsValue.Undefined; });
        Fn(d, "getChannelNames", _ => new JsArray(_engine, _model.Channels.Select(c => (JsValue)c.Id).ToArray()));
        Fn(d, "channel", a => ChannelObject(Str(a, 0)));
        Fn(d, "SetLedLimit", a => { _model.TotalLedLimit = (int)Num(a, 0, 0); return JsValue.Undefined; });
        Fn(d, "getLedCount", _ => _model.Channels.Sum(c => c.LedCount));
        Fn(d, "getChannelPulseColor", a => _colors.ZoneBaseColor(ZoneInfo.ChannelZoneId(Str(a, 0))).ToHex());
        Fn(d, "createColorArray", a =>
        {
            var color = RgbColor.Parse(Str(a, 0), RgbColor.Black);
            var count = Math.Clamp((int)Num(a, 1, 0), 0, 4096);
            return ColorArrayOf(Enumerable.Repeat(color, count).ToArray(), Str(a, 2), Str(a, 3));
        });

        // ---- alerts & messages (shown in the UI)
        Fn(d, "notify", a =>
        {
            var id = $"alert{++_alertCounter}";
            _model.Messages[id] = new DeviceMessage(id, Str(a, 0), Str(a, 1), (int)Num(a, 2, 0));
            _model.Touch();
            return id;
        });
        Fn(d, "denotify", a => { if (_model.Messages.Remove(Str(a, 0))) _model.Touch(); return JsValue.Undefined; });
        Fn(d, "addMessage", a =>
        {
            var id = "msg:" + Str(a, 0);
            _model.Messages[id] = new DeviceMessage(id, Str(a, 1), Str(a, 2), 0);
            _model.Touch();
            return JsValue.Undefined;
        });
        Fn(d, "removeMessage", a => { if (_model.Messages.Remove("msg:" + Str(a, 0))) _model.Touch(); return JsValue.Undefined; });

        // ---- user properties
        Fn(d, "addProperty", a => { if (ParseParameter(Arg(a, 0)) is { } p) RegisterParameter(p); return JsValue.Undefined; });
        Fn(d, "removeProperty", a => { _model.Parameters.RemoveAll(p => p.Property == Str(a, 0)); _model.Touch(); return JsValue.Undefined; });
        Fn(d, "hasProperty", a => _model.Parameters.Any(p => p.Property == Str(a, 0)));
        Fn(d, "getPropertyNames", _ => new JsArray(_engine, _model.Parameters.Select(p => (JsValue)p.Property).ToArray()));
        Fn(d, "getProperty", a =>
        {
            var p = _model.Parameters.FirstOrDefault(x => x.Property == Str(a, 0));
            if (p == null) return JsValue.Null;
            var o = new JsObject(_engine);
            o.Set("property", p.Property);
            o.Set("label", p.Label);
            o.Set("type", p.Type);
            o.Set("default", p.Default);
            o.Set("value", ToJsParameterValue(p, _paramValues.GetValueOrDefault(p.Property, p.Default)));
            return o;
        });
        Fn(d, "setUserProperty", a =>
        {
            var p = _model.Parameters.FirstOrDefault(x => x.Property == Str(a, 0));
            if (p != null)
            {
                var value = Arg(a, 1).IsBoolean() ? (Arg(a, 1).AsBoolean() ? "true" : "false") : Str(a, 1);
                _paramValues[p.Property] = value;
                _engine.SetValue(p.Property, ToJsParameterValue(p, value));
            }
            return JsValue.Undefined;
        });

        return d;
    }

    private JsValue ChannelObject(string id)
    {
        if (_model.FindChannel(id) == null) return JsValue.Null;
        if (_channelObjects.TryGetValue(id, out var cached)) return cached;

        var c = new JsObject(_engine);
        Fn(c, "SetLedLimit", a => { if (_model.FindChannel(id) is { } ch) { ch.LedLimit = (int)Num(a, 0, ch.LedLimit); ch.LedCount = Math.Min(ch.LedCount, ch.LedLimit); _model.Touch(); } return JsValue.Undefined; });
        Fn(c, "LedCount", _ => _model.FindChannel(id)?.LedCount ?? 0);
        Fn(c, "ledCount", _ => _model.FindChannel(id)?.LedCount ?? 0);
        Fn(c, "shouldPulseColors", _ => false);
        Fn(c, "OverrideColors", _ => false);
        Fn(c, "getComponentNames", _ => new JsArray(_engine));
        Fn(c, "getComponentColors", _ => new JsArray(_engine));
        Fn(c, "getColors", a =>
        {
            var count = _model.FindChannel(id)?.LedCount ?? 0;
            var colors = new RgbColor[count];
            for (var i = 0; i < count; i++) colors[i] = _colors.ChannelLed(id, i, count);
            return ColorArrayOf(colors, Str(a, 0), Str(a, 1));
        });
        Fn(c, "getPulse", a =>
        {
            var count = Math.Clamp((int)Num(a, 0, 0), 0, 4096);
            var color = _colors.ZoneBaseColor(ZoneInfo.ChannelZoneId(id));
            return ColorArrayOf(Enumerable.Repeat(color, count).ToArray(), Str(a, 1), Str(a, 2));
        });
        var wrapped = CallFn(_engine.GetValue("__wrapHost"), c, "channel");
        _channelObjects[id] = wrapped;
        return wrapped;
    }

    private JsValue CreateBusObject(SmBus bus, byte? fixedAddress)
    {
        var b = new JsObject(_engine);
        var isFixed = fixedAddress.HasValue;
        var offset = isFixed ? 0 : 1; // free-address functions take the address as first argument

        byte? Addr(JsValue[] a)
        {
            if (isFixed) return fixedAddress!.Value;
            var n = Num(a, 0, -1);
            return n is >= 0 and <= 0x7F ? (byte)n : null;
        }

        int Locked(Func<int> op)
        {
            if (!bus.Lock.TryEnter(2000)) return -1;
            try { return op(); }
            finally { bus.Lock.Exit(); }
        }

        byte Reg(JsValue[] a, int i) => (byte)((int)Num(a, i, 0) & 0xFF);

        Fn(b, "ReadByte", a => Addr(a) is { } ad ? Locked(() => bus.ReadByteData(ad, Reg(a, offset))) : -1);
        Fn(b, "ReadByteWithoutRegister", a => Addr(a) is { } ad ? Locked(() => bus.ReadByte(ad)) : -1);
        Fn(b, "ReadWord", a => Addr(a) is { } ad ? Locked(() => bus.ReadWordData(ad, Reg(a, offset))) : -1);
        Fn(b, "WriteByte", a => Addr(a) is { } ad ? Locked(() => bus.WriteByteData(ad, Reg(a, offset), Reg(a, offset + 1))) : -1);
        Fn(b, "WriteWord", a => Addr(a) is { } ad ? Locked(() => bus.WriteWordData(ad, Reg(a, offset), (ushort)((int)Num(a, offset + 1, 0) & 0xFFFF))) : -1);
        Fn(b, "WriteBlock", a =>
        {
            if (Addr(a) is not { } ad) return -1;
            var size = Math.Clamp((int)Num(a, offset + 1, 0), 0, 32);
            var data = Bytes(Arg(a, offset + 2), size);
            return data.Length == 0 ? -1 : Locked(() => bus.WriteBlockData(ad, Reg(a, offset), data));
        });
        Fn(b, "WriteBlockWithoutRegister", a =>
        {
            if (Addr(a) is not { } ad) return -1;
            var size = Math.Clamp((int)Num(a, offset, 0), 0, 64);
            var data = Bytes(Arg(a, offset + 1), size);
            return data.Length == 0 ? -1 : Locked(() => bus.RawWrite(ad, data));
        });
        Fn(b, "ReadBlockBytes", a =>
        {
            var size = Math.Clamp((int)Num(a, offset + 1, 0), 0, 32);
            var buffer = new byte[size];
            var status = Addr(a) is { } ad && size > 0 ? Locked(() => bus.ReadI2CBlockData(ad, Reg(a, offset), buffer)) : -1;
            if (status < 0) Array.Clear(buffer);
            return new JsArray(_engine, new JsValue[] { status < 0 ? -1 : 0, ByteArray(buffer) });
        });
        Fn(b, "ReadBlockWithoutRegister", a =>
        {
            var size = Math.Clamp((int)Num(a, offset, 0), 0, 64);
            var buffer = new byte[size];
            var status = Addr(a) is { } ad && size > 0 ? Locked(() => bus.RawRead(ad, buffer)) : -1;
            if (status < 0) Array.Clear(buffer);
            return new JsArray(_engine, new JsValue[] { status < 0 ? -1 : 0, ByteArray(buffer) });
        });
        Fn(b, "WriteQuick", a =>
        {
            var n = isFixed && Arg(a, 0).IsUndefined() ? fixedAddress!.Value : Num(a, 0, -1);
            return n is >= 0 and <= 0x7F ? Locked(() => bus.WriteQuick((byte)n)) : -1;
        });

        Fn(b, "IsSystemBus", _ => bus.IsSystemBus);
        Fn(b, "IsIntelBus", _ => bus.Kind == SmBusKind.IntelSystem);
        Fn(b, "IsAMDBus", _ => false);
        Fn(b, "IsNvidiaBus", _ => bus.Kind == SmBusKind.NvidiaGpu);
        Fn(b, "IsAMDGpuBus", _ => false);
        Fn(b, "IsNuvotonBus", _ => false);
        Fn(b, "Name", _ => bus.Name);
        Fn(b, "Vendor", _ => bus.Vendor);
        Fn(b, "SubVendor", _ => bus.SubVendor);
        Fn(b, "Product", _ => bus.Product);
        Fn(b, "SubDevice", _ => bus.SubDevice);
        Fn(b, "Port", _ => bus.Port);
        Fn(b, "Type", _ => isFixed ? "Fixed" : "Free");
        Fn(b, "GetAddress", _ => isFixed ? (int)fixedAddress!.Value : -1);
        Fn(b, "TransactionLock", _ => { bus.Lock.TryEnter(5000); return JsValue.Undefined; });
        Fn(b, "TransactionUnlock", _ => { bus.Lock.Exit(); return JsValue.Undefined; });
        Fn(b, "HasTransactionLock", _ => bus.Lock.IsHeldByCurrentThread);
        Fn(b, "log", a => { DeviceLog(Format(Arg(a, 0))); return JsValue.Undefined; });
        Fn(b, "pause", a => { Pause(Num(a, 0, 0)); return JsValue.Undefined; });
        Fn(b, "FetchRamInfo", _ => "");
        // Plugins detect fixed vs free buses with String(bus).startsWith("I2CBusWrapperFixedAddress").
        Fn(b, "toString", _ => isFixed ? "I2CBusWrapperFixedAddress" : "I2CBusWrapperFreeAddress");

        return CallFn(_engine.GetValue("__wrapHost"), b, "bus");
    }

    // ---------------------------------------------------------------- parameters

    private PluginParameter? ParseParameter(JsValue value)
    {
        if (!value.IsObject()) return null;
        var o = value.AsObject();
        string S(string key) { var v = o.Get(key); return v.IsUndefined() || v.IsNull() ? "" : TypeConverter.ToString(v); }
        double? D(string key) { var v = o.Get(key); if (v.IsUndefined() || v.IsNull()) return null; var n = TypeConverter.ToNumber(v); return double.IsNaN(n) ? null : n; }

        var property = S("property");
        if (property.Length == 0) return null;
        var values = new List<string>();
        var vs = o.Get("values");
        if (vs.IsArray())
        {
            var arr = vs.AsArray();
            for (uint i = 0; i < arr.Length; i++) values.Add(TypeConverter.ToString(arr[i]));
        }

        var defaultValue = o.Get("default");
        var def = defaultValue.IsBoolean() ? (defaultValue.AsBoolean() ? "true" : "false") : S("default");

        return new PluginParameter
        {
            Property = property,
            Label = S("label") is { Length: > 0 } l ? l : property,
            Type = S("type").ToLowerInvariant(),
            Group = S("group"),
            Description = S("description"),
            Default = def,
            Values = values,
            Min = D("min"),
            Max = D("max"),
            Step = D("step"),
        };
    }

    private void RegisterParameter(PluginParameter p)
    {
        _model.Parameters.RemoveAll(x => x.Property == p.Property);
        _model.Parameters.Add(p);
        _model.Touch();
        if (p.IsManagedByHost) return;

        var value = _savedParams.TryGetValue(p.Property, out var saved) ? saved : p.Default;
        if (p.Type == "combobox" && p.Values.Count > 0 && !p.Values.Contains(value)) value = p.Default;
        _paramValues[p.Property] = value;
        _engine.SetValue(p.Property, ToJsParameterValue(p, value));
    }

    private static JsValue ToJsParameterValue(PluginParameter p, string value)
    {
        switch (p.Type)
        {
            case "boolean":
                return value.Trim() is "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            case "number":
            case "hue":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
                return double.TryParse(p.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            default:
                return value;
        }
    }

    // ---------------------------------------------------------------- helpers

    private HidBackend RequireHid() => Hid ?? throw new PluginException("This plugin needs a HID device.");

    private void ClearReadBuffer()
    {
        if (Hid?.Active is not { } conn) return;
        var buffer = new byte[Math.Max(conn.Endpoint.InputReportLength, 1)];
        for (var i = 0; i < 256 && conn.Read(buffer, 0) > 0; i++) { }
        conn.FlushInput();
        Hid.LastReadSize = 0;
    }

    private JsObject EndpointObject(HidEndpointInfo e)
    {
        var o = new JsObject(_engine);
        o.Set("interface", e.Interface);
        o.Set("usage", e.Usage);
        o.Set("usage_page", e.UsagePage);
        o.Set("collection", e.Collection);
        return o;
    }

    private void ApplyExportedLeds()
    {
        if (_model.LedPositions.Count > 0 || _module == null) return;
        try
        {
            var positions = Positions(CallExportRaw("LedPositions"));
            if (positions.Count == 0) return;
            var names = StringList(CallExportRaw("LedNames"));
            _model.LedPositions = positions;
            _model.LedNames = names;
            _model.Touch();
        }
        catch (PluginException) { /* optional exports */ }
    }

    private void SetSizeFrom(JsValue v)
    {
        if (!v.IsArray()) return;
        var arr = v.AsArray();
        if (arr.Length < 2) return;
        // Some plugins report -1 when a board has no fixed LEDs; clamp instead of failing.
        _model.Width = Math.Max(1, (int)TypeConverter.ToNumber(arr[0]));
        _model.Height = Math.Max(1, (int)TypeConverter.ToNumber(arr[1]));
        _model.Touch();
    }

    private static List<string> StringList(JsValue v)
    {
        var list = new List<string>();
        if (!v.IsArray()) return list;
        var arr = v.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var item = arr[i];
            // Some plugins push nested arrays like ["Zone 1"].
            list.Add(item.IsArray() && item.AsArray().Length > 0 ? TypeConverter.ToString(item.AsArray()[0]) : TypeConverter.ToString(item));
        }
        return list;
    }

    private static List<(int X, int Y)> Positions(JsValue v)
    {
        var list = new List<(int, int)>();
        if (!v.IsArray()) return list;
        var arr = v.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            if (!p.IsArray()) continue;
            var pa = p.AsArray();
            if (pa.Length < 2) continue;
            var x = TypeConverter.ToNumber(pa[0]);
            var y = TypeConverter.ToNumber(pa[1]);
            list.Add((double.IsNaN(x) ? 0 : (int)x, double.IsNaN(y) ? 0 : (int)y));
        }
        return list;
    }

    private static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;

    /// <summary>Calls a JS function with `this` = undefined. (Jint's Call overloads are easy to mix up.)</summary>
    private static JsValue CallFn(JsValue fn, params JsValue[] args) => fn.Call(JsValue.Undefined, args);

    private static double Num(JsValue[] a, int i, double fallback)
    {
        var v = Arg(a, i);
        if (v.IsUndefined() || v.IsNull()) return fallback;
        var n = TypeConverter.ToNumber(v);
        return double.IsNaN(n) || double.IsInfinity(n) ? fallback : n;
    }

    private static string Str(JsValue[] a, int i)
    {
        var v = Arg(a, i);
        return v.IsUndefined() || v.IsNull() ? "" : TypeConverter.ToString(v);
    }

    private static int? LengthArg(JsValue[] a, int i)
    {
        var v = Arg(a, i);
        if (v.IsUndefined() || v.IsNull()) return null;
        var n = TypeConverter.ToNumber(v);
        return double.IsNaN(n) ? null : Math.Clamp((int)n, 0, 4096);
    }

    /// <summary>JS array → bytes, zero padded / truncated to <paramref name="length"/> (values wrap like a uint8 cast).</summary>
    private static byte[] Bytes(JsValue v, int? length)
    {
        var count = 0;
        Jint.Native.JsArray? arr = null;
        if (v.IsArray())
        {
            arr = v.AsArray();
            count = (int)Math.Min(arr.Length, 4096u);
        }
        var result = new byte[length ?? count];
        if (arr == null) return result;
        var n = Math.Min(count, result.Length);
        for (var i = 0; i < n; i++)
        {
            var d = TypeConverter.ToNumber(arr[(uint)i]);
            result[i] = double.IsNaN(d) || double.IsInfinity(d) ? (byte)0 : unchecked((byte)(long)d);
        }
        return result;
    }

    private JsArray ByteArray(ReadOnlySpan<byte> data)
    {
        var values = new JsValue[data.Length];
        for (var i = 0; i < data.Length; i++) values[i] = (int)data[i];
        return new JsArray(_engine, values);
    }

    private JsArray ColorArray(RgbColor c) => new(_engine, new JsValue[] { (int)c.R, (int)c.G, (int)c.B });

    private static readonly Dictionary<string, int[]> ColorOrders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RGB"] = new[] { 0, 1, 2 }, ["RBG"] = new[] { 0, 2, 1 }, ["BGR"] = new[] { 2, 1, 0 },
        ["BRG"] = new[] { 2, 0, 1 }, ["GBR"] = new[] { 1, 2, 0 }, ["GRB"] = new[] { 1, 0, 2 },
    };

    /// <summary>"Inline" → [r,g,b,r,g,b…] in the given color order; "Separate" → [[r…],[g…],[b…]].</summary>
    private JsArray ColorArrayOf(RgbColor[] colors, string arrayOrder, string colorOrder)
    {
        if (arrayOrder.Equals("Separate", StringComparison.OrdinalIgnoreCase))
        {
            var r = new JsValue[colors.Length];
            var g = new JsValue[colors.Length];
            var b = new JsValue[colors.Length];
            for (var i = 0; i < colors.Length; i++) { r[i] = (int)colors[i].R; g[i] = (int)colors[i].G; b[i] = (int)colors[i].B; }
            return new JsArray(_engine, new JsValue[] { new JsArray(_engine, r), new JsArray(_engine, g), new JsArray(_engine, b) });
        }

        var order = ColorOrders.GetValueOrDefault(colorOrder.Length == 0 ? "RGB" : colorOrder, ColorOrders["RGB"]);
        var values = new JsValue[colors.Length * 3];
        Span<byte> rgb = stackalloc byte[3];
        for (var i = 0; i < colors.Length; i++)
        {
            rgb[0] = colors[i].R; rgb[1] = colors[i].G; rgb[2] = colors[i].B;
            values[i * 3] = (int)rgb[order[0]];
            values[i * 3 + 1] = (int)rgb[order[1]];
            values[i * 3 + 2] = (int)rgb[order[2]];
        }
        return new JsArray(_engine, values);
    }

    private static void Pause(double ms)
    {
        if (ms <= 0) return;
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(ms, 10_000)));
    }

    private string Format(JsValue v)
    {
        if (v.IsUndefined()) return "undefined";
        if (v.IsString()) return v.AsString();
        try { return CallFn(_engine.GetValue("__fmt"), v).AsString(); }
        catch { return TypeConverter.ToString(v); }
    }

    private void DeviceLog(string message)
    {
        var now = Environment.TickCount64;
        if (now - _logWindowStart > 10_000)
        {
            if (_logSuppressed > 0) Log.Debug(LogName, $"({_logSuppressed} log lines suppressed)");
            _logWindowStart = now;
            _logCount = 0;
            _logSuppressed = 0;
        }
        if (++_logCount > 60) { _logSuppressed++; return; }
        Log.Info(LogName, message);
    }

    private void Warn(string message) => Log.Warn(LogName, message);

    private void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key)) Warn(message);
    }

    private static string Describe(Exception ex) => ex switch
    {
        JavaScriptException js => $"{js.Error} at {js.Location.Start.Line}:{js.Location.Start.Column}",
        TimeoutException => "the plugin took too long and was stopped",
        PluginException pe => pe.Message,
        _ => ex.Message,
    };

    public void Dispose()
    {
        Hid?.Dispose();
        _engine.Dispose();
    }

    private sealed class BlackFrame : IFrameColors
    {
        public static readonly BlackFrame Instance = new();
        public RgbColor DeviceLed(int i, int c) => RgbColor.Black;
        public RgbColor SubdeviceLed(string id, int i, int c) => RgbColor.Black;
        public RgbColor ChannelLed(string id, int i, int c) => RgbColor.Black;
        public RgbColor ZoneBaseColor(string zoneId) => RgbColor.Black;
    }
}
