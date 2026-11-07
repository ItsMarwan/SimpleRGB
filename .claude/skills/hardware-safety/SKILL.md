---
name: hardware-safety
description: Mandatory safety rules for any SimpleRGB change or test that sends data to real hardware (HID writes, SMBus/I2C, RAM, GPU, cooler displays, keyboards). Read before touching device code, plugins, the SMBus guard, or running the app/probe against real devices.
---

# Hardware safety

SimpleRGB talks to real, expensive hardware. A wrong byte to the wrong chip can corrupt RAM SPD data, hang a GPU's
RGB controller, or wear out a keyboard's flash. Follow these rules without exception.

## Never

- Never weaken or bypass `SmBusSafetyGuard` (`src/SimpleRGB.Core/Hardware/SmBus/SmBusSafetyGuard.cs`). Protected:
  `0x50–0x57` (SPD), `0x30–0x37` (SPD write-protect/page), `0x48–0x4F` on DDR5 (PMIC).
- Never send firmware updates, "save to device/profile", EEPROM or flash writes. Live color data only.
- Never send guessed or experimental commands to real hardware. If you don't know a byte's meaning from the vendor's
  software or an existing plugin, don't send it.
- Never drive fans or change voltages, clocks or power limits.
- Never write MSRs. Sensors use PawnIO `ioctl_read_msr` on `0x1A2` and `0x1B1` only.
- Never install drivers, stop the user's programs, or edit another app's settings or autostart without explicit consent.

## Always

1. **Identify before driving.** Send only a read-only identify query first and drive only models whose layout you
   know. Leave unknown models untouched (`device.notify(...)` instead).
2. **Simulate first.** Write a fake device in `tests/SimpleRGB.Tests` (`FakeHid`, `FakeEneRamBus`,
   `FakeGigabyteGpuBus` are examples). Assert every packet's length, header, addresses and value ranges.
3. **Copy vendor limits** (brightness caps, packet pacing, write lengths). The Thermalright display stays ≤ 40% brightness.
4. **Use `--preview`** for UI work so nothing touches hardware.
5. **Get the owner's consent** before the first real-hardware test, tell them what will happen (e.g. "lights turn
   green for 8 s"), and ask them to confirm visually. Close conflicting RGB apps first (they cause flicker).
6. **Pause on conflicts.** If a vendor app drives the same device, export `PauseWhenConflicting()` and list it in
   `ConflictingProcesses()`.
7. Read-only exploration (`SimpleRGB.Probe list`, `sensors`, `thermalright-id`) is fine; anything that writes
   (`run`, `display`) needs consent.

## Risk levels

| Path | Worst realistic outcome | Notes |
|---|---|---|
| HID direct-mode colors | Wrong colors until reboot | Low risk |
| NVIDIA GPU I2C | RGB controller hangs until reboot | Use the plugin's exact address and write length |
| Chipset SMBus (RAM) | SPD corruption (stick unusable) | Guarded; never touch SPD addresses |
| Keyboard/mouse "save" commands | Flash wear / overwritten profiles | Use streaming/direct mode only |
