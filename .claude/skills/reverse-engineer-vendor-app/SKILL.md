---
name: reverse-engineer-vendor-app
description: Learn an RGB device's USB protocol from the manufacturer's own Windows software (decompiling .NET apps, reading data files, or observing traffic) so SimpleRGB can support it without guessing. Use when a device has no plugin and the vendor app (e.g. TRCC, Redragon, Aula) is installed.
---

# Learn a protocol from the vendor's app

Goal: interoperability. Learn exactly what the vendor app sends so SimpleRGB can send the same. Never experiment
on the hardware. Read `hardware-safety` first.

## 1. Find the app and its technology

```powershell
Get-ChildItem "${env:ProgramFiles(x86)}","$env:ProgramFiles" -Depth 2 -Filter *.exe | Where-Object Name -match '<brand>'
$t = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes('<app.exe>'))
$t -match 'mscoree\.dll'     # True → .NET: decompile (best case)
```

Native apps (C++/DuiLib/Qt): look at data files (`*.json`, `*.ini`, `*.config`) for key maps and LED indexes, check for
an existing plugin of the same protocol family (same VID, same vendor collection such as page `0xFF1C` usage `0x92`),
or observe traffic (step 4).

## 2. .NET apps: decompile (worked example: TRCC → Thermalright)

Install the decompiler into a scratch folder (not system-wide). Use version 8.x on a .NET 8 SDK:

```powershell
dotnet tool install ilspycmd --version 8.2.0.7535 --tool-path <scratch>\tools
<scratch>\tools\ilspycmd.exe -p -o <scratch>\out\App "<path>\App.exe"
```

Then search the output:

- VID/PID (often decimal!): `grep -rn "1046\|32769" ` (0x0416 / 0x8001), or `new UsbHidDevice(`.
- Handshake: what's sent on connect, and which reply byte holds the model id (account for the leading report-id byte).
- Frame builder: header bytes, payload length, LED order ("wiring" arrays), chunking (64-byte reports), sleeps
  between frames, and **brightness multipliers** (TRCC caps at `* 0.4`).
- Rendering logic (e.g. a 7-segment font, which digits show which value).

## 3. Confirm with read-only queries

Send only the vendor's identify/version query and compare the reply with the decompiled parser
(`SimpleRGB.Probe thermalright-id` is an example). Don't send frames yet.

## 4. Observing traffic (native apps), only with consent

Windows' built-in USB ETW tracing (no install) can record what the vendor app sends while the user changes lighting.
The trace also captures **keystrokes**, so warn the user not to type anything private, keep it short, read only the
lighting packets, and delete it afterwards. Prefer decompilation or data files whenever possible.

## 5. Implement and prove offline

Write the plugin (`add-device-support`), then a simulated device test asserting the exact packets. Real hardware comes
last, with consent, for a few seconds, with a visual check by the owner. Close the vendor app first: two apps writing
causes flicker. Add `PauseWhenConflicting()` so they never fight later.

## Legal and ethics

Decompile only software the user has installed, for interoperability. Don't copy vendor code or assets into the repo;
re-implement the protocol in original code.
