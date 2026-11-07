# Guide for AI coding agents

This file tells coding agents (Claude Code, Codex, Copilot, Cursor…) how to work on SimpleRGB safely.
Detailed, task-specific instructions live in **[`.claude/skills/`](.claude/skills)**. Read the matching skill before
starting a task.

| Task | Skill |
|---|---|
| Build, test, run, package, release | [`build-test-release`](.claude/skills/build-test-release/SKILL.md) |
| Anything that sends data to hardware | [`hardware-safety`](.claude/skills/hardware-safety/SKILL.md) (**always read first**) |
| Support a new device / write a plugin | [`add-device-support`](.claude/skills/add-device-support/SKILL.md) |
| Learn a protocol from a vendor's app | [`reverse-engineer-vendor-app`](.claude/skills/reverse-engineer-vendor-app/SKILL.md) |
| Change the UI and verify it visually | [`ui-preview`](.claude/skills/ui-preview/SKILL.md) |
| Add or tune lighting effects / music mode | [`effects-and-music`](.claude/skills/effects-and-music/SKILL.md) |

## Ground rules

1. **Never guess hardware commands on a real device.** Learn the protocol from the vendor's software or an existing
   plugin, prove it against a simulated device in `tests/`, and only then touch hardware, with the owner's consent.
2. **Never weaken `SmBusSafetyGuard`** or the "no flash/firmware writes" rule. See [docs/SAFETY.md](docs/SAFETY.md).
3. **Don't commit SignalRGB plugins.** They aren't licensed for redistribution; the app downloads them on the
   user's PC. Built-in plugins in `src/SimpleRGB.Core/Assets/Plugins/` must be original code.
4. **Keep the device thread contract:** everything a plugin calls runs on that device's thread; UI ↔ device
   communication goes through immutable `LightingSnapshot`s and `RgbDevice.Enqueue`.
5. **Test before you claim.** `dotnet test tests/SimpleRGB.Tests` must pass. For UI work, run `--preview` and look at
   a screenshot of the window only (see `ui-preview`).
6. Ask the user before installing drivers or tools, stopping other programs, or changing another program's settings.

## Layout

```
src/SimpleRGB.Core/   engine: Plugins/, Devices/, Hardware/{Hid,SmBus}/, Lighting/, Audio/, Media/, Sensors/, Settings/, Platform/
src/SimpleRGB/        WPF UI: MainWindow.xaml, Controls/, ViewModels/, Themes/Dark.xaml, Platform/ (tray, autostart, single instance)
tests/SimpleRGB.Tests xUnit; Fakes.cs has fake HID devices and simulated SMBus chips
tools/SimpleRGB.Probe CLI diagnostics (list, run, sensors, display, audio, albumart)
docs/                 ARCHITECTURE, PLUGIN_API, SAFETY
```
