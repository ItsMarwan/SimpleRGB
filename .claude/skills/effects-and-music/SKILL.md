---
name: effects-and-music
description: Add or tune SimpleRGB lighting effects, sync behaviour, music mode (beat/bass/treble detection, Spotify-only capture, album-art colors) and sensor-driven displays. Use when changing Effects.cs, AudioAnalyzer, NowPlayingService or LightingSnapshot.
---

# Effects and music mode

## Effects

`src/SimpleRGB.Core/Lighting/Effects.cs`: `Effects.Evaluate(ZoneLighting, EffectContext)` is a pure function of time,
LED position, the zone settings and audio. To add an effect:

1. Add a value to `LightingMode`. If it needs them, extend `UsesColor/UsesSecondColor/UsesSpeed/UsesSize/UsesDirection`.
2. Implement the case in `Evaluate`. Use `c.Time` (the shared `LightingClock`, never a per-device clock), `pos`
   (already reversed if `Reverse`), `size`, and `Hash01/Noise` for deterministic randomness (seeded per zone).
3. Add a tile in `MainViewModel.EffectOptions` and a label in `LightingVisuals.ModeText`.
4. Test in `MusicAndEffectsTests.cs`: no exceptions over time/positions, black at brightness 0, and that it
   actually changes over time (or matches a known pattern).

Rules: everything must be deterministic for a given time, so devices stay in sync. Size 50 must keep the original
look (`Width(size)`).

## Sync

`DeviceManager.BuildLighting` creates each device's `LightingSnapshot`: `Sync` is applied unless the device has
`ExcludeFromSync`. Device threads render `FrameColors(snapshot, LightingClock.Seconds, inputs)`.

## Music mode

- Capture: `Audio/AudioCapture.cs`. `ProcessLoopbackCapture` (Windows process loopback via
  `ActivateAudioInterfaceAsync("VAD\\Process_Loopback")`, Spotify's root PID including child processes) or
  `SystemLoopbackCapture` (NAudio WASAPI loopback).
- `AudioService` runs only while some zone uses Music (or the music panel is open). It follows Spotify
  starting/stopping and feeds silence so levels decay (Windows sends no data while quiet).
- `AudioAnalyzer`: 2048-point FFT every 512 samples, per-band auto-gain, beat = rising bass energy above 1.45 × its
  1-second mean, at least 200 ms apart. Test with synthetic signals (`AudioAnalyzerTests`: a 120 BPM kick gives 16 ± 1 beats in 8 s).
- Silence turns Music zones fully off.
- Album colors: `Media/NowPlayingService.cs`. Windows media session thumbnail → 64×64 → `AlbumPalette.Extract`
  (k-means in OKLab, ranked by area plus colorfulness; black-and-white covers give white). It's keyed on the image hash
  and re-checked 1.5 s and 4 s after a track change, because Spotify updates the artwork late.

Debug with the probe: `audio --pid <pid>`, `audio --all`, `albumart --out <dir>`.

## Sensor displays

`Sensors/SensorService.cs` exposes CPU/GPU temperature and load to plugins (`sensors.*`), polled at
`SensorService.PollIntervalMs` (plugins can request 250 ms–5 s). Display plugins hold and smooth readings themselves
and request a refresh rate with `device.setMinimumFrameRate`.
