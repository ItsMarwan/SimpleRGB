# Changelog

## v1.0.0 (first public release)

- Runs SignalRGB HID and SMBus plugins in a sandbox. Imports them from a local SignalRGB install or downloads them from GitLab.
- Motherboards (USB), NVIDIA GPU lighting (driver I2C), RAM (Intel SMBus via PawnIO) with hard-locked SPD/PMIC addresses.
- Built-in driver for Thermalright Digital cooler displays (PA120 Digital family): live CPU/GPU temperature and usage,
  refresh rate, update interval, smoothing, temperature colors, and an automatic pause while TRCC runs.
- 12 effects with a shared clock, so everything stays in sync; speed, width and direction options.
- Music mode: beat/bass/vocals/treble, Spotify-only capture, album-art colors, pulse or equalizer.
- "Sync all devices", with per-device opt-out.
- Modern UI: live LED previews, animated effect tiles, color picker with favorites and recent colors, grouped device list, rename.
- Fast startup (USB devices light up in about 1 s), automatic GPU retry after boot, hot-plug and resume handling.
- Tray icon and Start with Windows (scheduled task, no UAC prompt at login).
