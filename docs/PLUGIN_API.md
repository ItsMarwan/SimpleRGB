# Plugin API

SimpleRGB runs **SignalRGB-style JavaScript plugins** (ES modules) in a sandbox. Existing SignalRGB HID and SMBus
plugins work unchanged; this page documents what SimpleRGB implements and what it adds.

Plugins have **no** access to files, network, processes or .NET. Anything unknown on `device`, `bus` or the
`@SignalRGB/*` modules is a harmless no-op that logs a warning once.

## Exports SimpleRGB reads

| Export | Purpose |
|---|---|
| `Name()`, `Publisher()` | Display name |
| `VendorId()`, `ProductId()` | USB IDs (HID plugins). `ProductId` may return a number or an array |
| `Type()` | `"Hid"` or `"SMBUS"` (`rawusb`, `serial`, `network`, `hybrid` are skipped) |
| `DeviceType()` | `motherboard`, `gpu`, `ram`, `cooler`, `keyboard`, `mouse`… (picks the icon and sidebar section) |
| `Validate(endpoint)` | HID: return true for each endpoint (`{interface, usage, usage_page, collection}`) to open. `interface` is -1 for single-interface devices |
| `Scan(bus)` | SMBus: return the addresses this plugin drives (on a free-address bus) |
| `Initialize()`, `Render()`, `Shutdown(systemSuspending)` | Lifecycle. `Render` runs on the device's own thread |
| `ControllableParameters()` | User settings (`combobox`, `boolean`, `number`, `color`, `textfield`). They become globals; `on<Property>Changed()` is called on change |
| `Size()`, `LedNames()`, `LedPositions()` | Default layout (usually set in `Initialize` instead) |
| `ConflictingProcesses()` | Programs that also drive this device (shown as a warning) |
| **`AlwaysShutdown()`** *(SimpleRGB)* | Return true to get `Shutdown()` on every stop, e.g. so a status display never shows stale values |
| **`PauseWhenConflicting()`** *(SimpleRGB)* | Return true to pause rendering while one of `ConflictingProcesses()` runs (no flicker fights) |

## `device`

**Transfers (HID):** `write(data, len)`, `read(data, len, timeoutMs=1000)`, `send_report(data, len)`,
`get_report(data, len)`, `input_report(data, len)`, `getLastReadSize()`, `clearReadBuffer()` / `flush()`,
`set_endpoint(interface, usage, usagePage, collection?)`, `getHidEndpoints()`, `getHidInfo()`, `getDeviceInfo()`.
Semantics follow hidapi: `data[0]` is the report id; short writes are zero-padded to the report length; reads strip a
leading `0` report id; values wrap like `uint8`.

**Layout:** `setName`, `setSize([w,h])`, `setControllableLeds(names, positions)`,
`createSubdevice / removeSubdevice / setSubdeviceName / setSubdeviceSize / setSubdeviceLeds`,
`addChannel(id, limit, default?) / removeChannel / channel(id) / getChannelNames / SetLedLimit / getLedCount`.

**Colors:** `color(x, y)` → `[r,g,b]`; `subdeviceColor(id, x, y)`;
`channel(id).getColors("Inline" | "Separate", "RGB" | "GRB" | …)`; `createColorArray(hex, count, order, colorOrder)`;
`getChannelPulseColor(id)`. `shouldPulseColors()` is always false (SimpleRGB has no component-setup mode).

**Other:** `log`, `pause(ms)` (max 10 s), `notify / denotify / addMessage / removeMessage` (shown in the UI),
`addProperty / removeProperty / getProperty / hasProperty / getPropertyNames / setUserProperty`,
`setFrameRateTarget(fps)` (cap while animating), `getMotherboardName()`, `getBrightness()` (always 100; brightness is
already applied to colors), `fanControlDisabled()` (always **true**: SimpleRGB never drives fans).

**SimpleRGB extensions:**

| Call | Purpose |
|---|---|
| `device.setMinimumFrameRate(fps)` | Keep sending at least `fps` frames per second (max 30) even when the lighting is static, e.g. a live sensor display |

## `sensors` *(SimpleRGB)*

Read-only PC sensors for status displays. Each returns a number, or `-1` if unavailable.

| Call | Source |
|---|---|
| `sensors.cpuTemp()` | Intel package temperature (PawnIO IntelMSR, needs admin) |
| `sensors.cpuLoad()` | Total CPU usage % |
| `sensors.gpuTemp()` / `sensors.gpuLoad()` | NVIDIA GPU core temperature / utilization % |
| `sensors.setPollInterval(ms)` | How often sensors are read (250–5000 ms) |

## `bus` (SMBus plugins)

Fixed-address bus in `Initialize`/`Render`; free-address bus passed to `Scan`. Free-address calls take the address first:
`ReadByte(reg)`, `ReadByteWithoutRegister()`, `ReadWord(reg)`, `ReadBlockBytes(reg, size)` → `[status, bytes]`,
`ReadBlockWithoutRegister(size)`, `WriteByte(reg, v)`, `WriteWord(reg, w)`, `WriteBlock(reg, size, data)`
(SMBus block with length byte), `WriteBlockWithoutRegister(size, data)` (raw I2C), `WriteQuick(addr)`,
`IsSystemBus / IsIntelBus / IsNvidiaBus / IsAMDBus`, `Vendor / SubVendor / Product / SubDevice`, `GetAddress`,
`Type()` → `"Fixed" | "Free"`, `TransactionLock / TransactionUnlock`. Return values are negative on failure.

All chipset-SMBus traffic passes through `SmBusSafetyGuard`; see [SAFETY.md](SAFETY.md).

## Modules

`@SignalRGB/systeminfo` (`GetMotherboardInfo`, `GetBiosInfo`), `@SignalRGB/Errors(.js)` (`Assert`, `ContextError`,
`globalContext`) and `@SignalRGB/permissions` are implemented. Other `@SignalRGB/*` modules are no-op stubs.
Relative imports (`./helper.js`) work, but only inside the plugin's own folder.

## Writing a plugin

1. Put `MyDevice.js` in `%APPDATA%\SimpleRGB\Plugins\Custom\<Brand>\` (or, for a contribution, in
   `src/SimpleRGB.Core/Assets/Plugins/<Brand>/`).
2. Copy the structure of `Thermalright_Digital_Display.js`: identify the device in `Initialize`, describe LEDs, send
   colors in `Render`.
3. Test against a fake device first (see `tests/SimpleRGB.Tests/ThermalrightDisplayTests.cs`).
4. Click refresh in SimpleRGB.
