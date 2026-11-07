---
name: add-device-support
description: Add support for a new RGB device in SimpleRGB, either by enabling an existing SignalRGB plugin or by writing a built-in SimpleRGB plugin (JS) with tests. Use when a user's device isn't detected, shows "no RGB plugin", or needs extra features (e.g. display settings).
---

# Add device support

Read `hardware-safety` first.

## 1. Find out what the device is (read-only)

```powershell
dotnet run --project tools/SimpleRGB.Probe -- list
```

This shows every HID device with VID:PID, interfaces (`if`), collections, usage pages and report lengths, which
plugins match, and the GPU/SMBus buses. Also check `%APPDATA%\SimpleRGB\logs\simplergb.log`.

## 2. Is there already a plugin?

Search the local plugin copies for the vendor id (lowercase hex, e.g. `0x320f`):

```bash
grep -rli "0x320f" "$APPDATA/SimpleRGB/Plugins/"
```

- **Plugin exists and matches the PID:** it should just work. If it doesn't, check `Validate()` against the endpoints
  and the log for script errors. Host API gaps are fixed in `PluginRuntime.cs`.
- **Plugin exists for sibling PIDs (same protocol family):** don't just add the PID. Confirm the protocol is
  identical (vendor app or traffic capture), because LED index maps usually differ per model.
- **No plugin:** write a built-in one (step 3). First learn the protocol (`reverse-engineer-vendor-app`).

## 3. Write a built-in plugin

Location: `src/SimpleRGB.Core/Assets/Plugins/<Brand>/<Device>.js` (copied to `Plugins/` next to the exe; priority
over SignalRGB copies). Template: `Thermalright/Thermalright_Digital_Display.js`.

Checklist:

- Exports: `Name`, `VendorId`, `ProductId` (array), `Publisher` (`"SimpleRGB"`), `Type` (`"Hid"`), `DeviceType`,
  `Validate(endpoint)` (match the exact vendor collection), `Initialize`, `Render`, `Shutdown`.
- `Initialize`: **identify the model** with a read-only query; set `supported` only for known layouts; then
  `device.setControllableLeds(names, positions)` in **visual order** (effects flow in LED order).
- `Render`: build the frame from `device.color(x, y)` per LED and send it with `device.write([reportId, ...], len)`.
  Keep vendor pacing and brightness caps.
- Optional: `ControllableParameters()` with `group: "display"` etc. (shown under *Device settings*),
  `ConflictingProcesses()` + `PauseWhenConflicting()`, `AlwaysShutdown()`, `device.setMinimumFrameRate(fps)`,
  `sensors.*` for status displays (see `docs/PLUGIN_API.md`).
- No SignalRGB code copied: built-in plugins must be original (GPL-3.0 project).

## 4. Test against a simulated device (required)

Add a test class like `tests/SimpleRGB.Tests/ThermalrightDisplayTests.cs`:

- `FakeHid` with a `Responder` that answers the identify query like the real device.
- Decode the written reports back into LED colors, and assert headers, lengths, chunking, value caps and the
  "unknown model → only the identify query was sent" path.
- Replace sensors with `SensorService.Shared = new FixedSensors(...)` when needed.

```powershell
dotnet test tests/SimpleRGB.Tests
```

## 5. Real hardware (with consent)

Ask the owner, close the vendor app, then run once for a few seconds and ask them to confirm visually, e.g. with a
small probe command modelled on `probe display`. Then check it in the app (`dotnet run --project src/SimpleRGB`).

## 6. Document

Add the device to the README's *Supported hardware* table and to `CHANGELOG.md`.
