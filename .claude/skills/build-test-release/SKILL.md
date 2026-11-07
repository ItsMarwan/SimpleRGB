---
name: build-test-release
description: Build, test, run, install and release SimpleRGB. Use when compiling, running the test suite, launching the app, packaging a release zip, or tagging a GitHub release.
---

# Build, test and release SimpleRGB

Requirements: Windows 10/11 x64, .NET 8 SDK. The app targets `net8.0-windows10.0.19041.0` (needed for Windows media APIs).

## Everyday commands

```powershell
dotnet build SimpleRGB.sln
dotnet test tests/SimpleRGB.Tests            # must stay green; needs no hardware
dotnet run --project src/SimpleRGB           # real app (drives hardware!)
dotnet run --project src/SimpleRGB -- --preview   # virtual devices only: safe for UI work
```

`--preview` uses its own data folder (`%TEMP%\SimpleRGB.PreviewData`) and its own single-instance lock, so it can
run next to an installed copy.

## Gotchas

- **Locked files:** if SimpleRGB (or a preview) is running from `bin/`, the build can't copy the DLLs (MSB3027).
  Build to another folder (`dotnet build src/SimpleRGB -o <tmp>`) or ask the user to exit the app from its **tray
  icon** (closing the window only hides it to the tray).
- **Elevated instance:** the installed app usually runs as administrator (for RAM/PawnIO). Non-elevated tools can't
  stop it or capture its window; ask the user.
- Real-plugin tests (`RealPluginTests`) skip themselves when SignalRGB plugins aren't present (e.g. in CI).
- Built-in plugins (`src/SimpleRGB.Core/Assets/Plugins/**/*.js`) and PawnIO modules are copied to `Plugins/` and
  `PawnIO/` next to the exe; tests load them from the test output folder.

## Install locally

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Publishes Release to `%LOCALAPPDATA%\Programs\SimpleRGB`, creates shortcuts, and, if "Start with Windows" is on,
re-points the scheduled task (`SimpleRGB`, highest privileges, logon trigger) via `SimpleRGB.exe --register-autostart`.

## Release

1. Update `<Version>` in `src/SimpleRGB/SimpleRGB.csproj` and add a section to `CHANGELOG.md`.
2. Commit, then tag and push: `git tag v1.2.3 && git push origin v1.2.3`.
3. `.github/workflows/release.yml` builds, tests, and attaches two zips to a GitHub release:
   `SimpleRGB-v1.2.3-win-x64.zip` (needs .NET 8 Desktop Runtime) and `…-self-contained.zip`.

Manual equivalent:

```powershell
dotnet publish src/SimpleRGB -c Release -r win-x64 --self-contained false -o out/fd -p:DebugType=none
dotnet publish src/SimpleRGB -c Release -r win-x64 --self-contained true  -o out/sc -p:DebugType=none
```

## Command-line switches (app)

| Switch | Effect |
|---|---|
| `--preview [--page <name>]` | Virtual devices, no hardware; optionally open a device page |
| `--autostart` | Started by the logon task (starts hidden if "Start hidden" is on) |
| `--minimized` | Start in the tray |
| `--register-autostart` | Point the logon task at this exe, then exit (needs admin) |
| `--wait-for-pid <pid>` | Used when restarting as administrator |
