# Hardware safety

SimpleRGB sends data to real hardware. These rules are enforced in code, not left to plugins.
**Any change that weakens them needs a very good reason and tests.**

## 1. RAM configuration chips are unreachable

The motherboard (chipset) SMBus also carries the RAM's **SPD EEPROM**, which holds its timings. Corrupting it can
make a stick unbootable. `SmBusSafetyGuard` wraps every chipset bus and blocks, before anything reaches the hardware:

| Addresses | Chip | Rule |
|---|---|---|
| `0x50–0x57` | SPD EEPROM (DDR3/4) / SPD hub (DDR5) | all transfers blocked |
| `0x30–0x37` | DDR4 SPD write-protect (SWP/CWP) and page-select (SPA) | all transfers blocked |
| `0x48–0x4F` | DDR5 PMIC (RAM voltage regulators) | blocked on DDR5 or unknown memory. Left open on DDR3/DDR4, where older ASUS Aura controllers live at `0x4E/0x4F` |

A probe (quick write) of a blocked address returns "occupied", so no plugin will try to move a device onto it.
Reads and writes return an error. Covered by `SafetyGuardTests` and `RealPluginTests.EneRam_*`.

## 2. No permanent writes

- No firmware updates, no saving profiles to onboard flash, no EEPROM writes.
- Only live ("direct mode") color data, which a power cycle resets.
- Fans are never driven (`fanControlDisabled()` is always true).
- Sensors are **read-only**: the Intel MSR path only calls PawnIO's `ioctl_read_msr` on `0x1A2` and `0x1B1`.

## 3. Coordinate with other software

- Every chipset-SMBus transfer holds the system mutex `Global\Access_SMBUS.HTP.Method`, like HWiNFO, LibreHardwareMonitor and OpenRGB.
- Devices whose plugin declares `PauseWhenConflicting()` stop writing while their vendor app runs (e.g. TRCC), and
  the UI offers to close that app. Removing another app's "start with Windows" entry happens only when the user clicks
  for it, and can be undone in Settings.

## 4. Respect vendor limits

Built-in drivers copy the vendor's own limits. For example, Thermalright displays are never driven above the 40%
brightness that TRCC uses.

## 5. Sandbox plugins

Plugins run in Jint: no file, network, process or .NET access. Relative imports can't leave the plugin folder.
Long-running calls are stopped by a timeout. Host functions never throw into scripts; only a real "device
disconnected" ends a device.

## 6. Be conservative with unknown devices

- A driver must **identify** the device (read-only query) and only drive models whose layout it knows. For example, the
  Thermalright driver drives model ids 16–31 only and leaves other models untouched.
- When reverse-engineering, decompile or observe the vendor app; never guess commands on real hardware.
- Prove a new protocol against a **simulated device in tests** before touching real hardware.
