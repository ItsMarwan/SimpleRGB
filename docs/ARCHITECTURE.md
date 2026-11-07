# Architecture

SimpleRGB is a .NET 8 WPF app split into a hardware/logic library and a thin UI.

```
src/SimpleRGB.Core      device engine (no UI)
src/SimpleRGB           WPF app: views, view models, tray, autostart, single instance
tests/SimpleRGB.Tests   xUnit tests (no hardware needed; real plugins run against simulated devices)
tools/SimpleRGB.Probe   CLI diagnostics for real hardware
```

## Data flow

```
 UI (MainViewModel) ──edits──► SettingsStore (settings.json, atomic, debounced)
        │                              │
        └──ApplyLighting()──► DeviceManager.BuildLighting(key) ──► LightingSnapshot (immutable)
                                                                         │
                         ┌───────────────────────────────────────────────┘
                         ▼
   RgbDevice (one background thread per device)
     loop: FrameColors(snapshot, LightingClock.Seconds, audio/album inputs)
           └─► PluginRuntime.Render()  ── JS plugin calls device.color(...) / channel.getColors(...)
                                        └─► HidConnection / SmBus (PawnIO, NVAPI) ──► hardware
```

- **One thread per device.** A slow or broken device can't stall the others or the UI. A plugin that keeps failing
  is stopped (8 consecutive errors). A device that disappears is marked *Disconnected*.
- **Frame rates.** Static lighting: 4 fps keep-alive (or the plugin's `setMinimumFrameRate`). Animated: 30 fps.
  Music: 60 fps. These are capped at 60 for USB and 30 for SMBus/GPU I2C, which are slow shared buses.
- **One clock.** `LightingClock` is shared by every device, so effects stay in step across devices.
- **LEDs by order, not coordinates.** Plugins pass grid coordinates; `DeviceModel.IndexOf` turns them into the LED's
  index within its zone, so effects line up across devices (a one-LED zone is always at the start of an effect).

## Key components

| Area | Files | Notes |
|---|---|---|
| Plugin runtime | `Plugins/PluginRuntime.cs`, `JsStubs.cs` | Jint engine per device instance. Implements `device`, `bus`, `sensors` and the `@SignalRGB/*` modules. See [PLUGIN_API.md](PLUGIN_API.md) |
| Plugin library | `Plugins/PluginLibrary.cs` | Roots in priority order: Custom > SimpleRGB built-in > SignalRGB (local copy) > GitLab. Metadata cache keyed on file size + time |
| Discovery | `Devices/DeviceManager.cs` | HID first (devices start as soon as they're found), then GPU and chipset buses scanned in parallel. Remembers devices to retry a GPU that wasn't ready at boot |
| HID | `Hardware/Hid/*` | SetupAPI enumeration grouped by USB parent; overlapped I/O with timeouts; hidapi-compatible semantics |
| SMBus | `Hardware/SmBus/*` | `PawnIoSmBus` (Intel i801 via PawnIO), `NvApiI2CBus` (NVIDIA port 1), `SmBusSafetyGuard` |
| Effects | `Lighting/Effects.cs` | Pure functions of (zone lighting, time, LED position, audio). Easy to unit test |
| Audio | `Audio/*` | WASAPI system loopback or **process loopback** (Spotify only). FFT, auto-gain and beat detection in `AudioAnalyzer`. Listens only while Music mode is used |
| Now playing | `Media/NowPlayingService.cs` | Windows media session (song info and album art). Album palette via k-means in OKLab |
| Sensors | `Sensors/SensorService.cs` | CPU temperature (Intel MSR via PawnIO, read-only), CPU load, GPU temperature/load (NVAPI) |
| Conflicts | `Platform/ConflictingApps.cs` | Detects and closes conflicting apps; can remove and restore their autostart entry (on request only) |
| Preview | `Devices/PreviewDevices.cs` | `--preview`: virtual devices with a separate data folder and lock. Never touches hardware |

## Built-in drivers

Devices without a SignalRGB plugin get a SimpleRGB plugin in `src/SimpleRGB.Core/Assets/Plugins/` (copied next to
the exe under `Plugins/`). They're ordinary JS plugins using the same API, plus SimpleRGB extensions.

- `Thermalright/Thermalright_Digital_Display.js`: Thermalright Digital cooler displays (`0416:8001`, PA120 Digital
  layout). The protocol mirrors Thermalright's TRCC app: identify `DA DB DC DD … 01`, frames of 20-byte header +
  84 × RGB in wiring order, 64-byte HID reports, colors capped at 40%.

## Settings

`%APPDATA%\SimpleRGB\settings.json`: written to a temp file, then swapped in. A corrupt file is kept as `.broken` and
defaults are used. Per device: zones, brightness, plugin parameters, channel LED counts, sync exclusion and custom name.
