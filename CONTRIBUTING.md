# Contributing to SimpleRGB

Thanks for helping! Bug reports, device requests and pull requests are all welcome.

## Getting started

```powershell
git clone https://github.com/ItsMarwan/SimpleRGB.git
cd SimpleRGB
dotnet test tests/SimpleRGB.Tests
dotnet run --project src/SimpleRGB -- --preview   # UI with virtual devices, safe to play with
```

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the big picture and [docs/SAFETY.md](docs/SAFETY.md) before
touching anything that talks to hardware.

## Adding a device

1. Run `dotnet run --project tools/SimpleRGB.Probe -- list` (read-only) and note the VID:PID and endpoints.
2. If a SignalRGB plugin exists, it should already work. If it doesn't, the fix usually belongs in `PluginRuntime.cs`
   (a missing or incorrect host API).
3. Otherwise, write an original built-in plugin in `src/SimpleRGB.Core/Assets/Plugins/<Brand>/` following
   [docs/PLUGIN_API.md](docs/PLUGIN_API.md), and **test it against a simulated device** (see
   `tests/SimpleRGB.Tests/ThermalrightDisplayTests.cs`).
4. Learn protocols from the vendor's own software or documentation, never by sending guessed commands.

The step-by-step guides in [`.claude/skills/`](.claude/skills) work for humans too.

## Pull requests

- Keep changes focused, and add tests for behavior changes.
- `dotnet test tests/SimpleRGB.Tests` must pass (CI runs it on every PR).
- For UI changes, attach a screenshot from `--preview`.
- Don't commit SignalRGB plugins, vendor code or assets. SimpleRGB is GPL-3.0; contributions are licensed the same way.

## Code style

- C# 12 / .NET 8, nullable enabled, file-scoped namespaces.
- Comments explain *why*, not *what*.
- Hardware code never throws into plugin scripts; it logs, returns an error value, and keeps the app running.
