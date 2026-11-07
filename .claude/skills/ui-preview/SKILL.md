---
name: ui-preview
description: Change SimpleRGB's WPF UI and verify it visually without touching hardware, using --preview mode and window-only screenshots. Use for any XAML, view model, theme or control change.
---

# UI work with preview mode

## Run the UI with virtual devices

```powershell
dotnet build src/SimpleRGB -o <scratch>\ui
<scratch>\ui\SimpleRGB.exe --preview                 # All devices page
<scratch>\ui\SimpleRGB.exe --preview --page ASUS      # open a device page by name (also: Thermalright, RAM, GIGABYTE)
```

Preview mode (`PreviewDevices.cs`) creates a motherboard (2 × 12 V + 3 ARGB channels), a GPU (25 LEDs), 4 RAM sticks and
the Thermalright display with fake HID connections. It never scans or opens real hardware, ignores USB hot-plug, has
no tray icon, uses `%TEMP%\SimpleRGB.PreviewData`, and has its own single-instance lock. Closing the window exits it.

## Screenshot only the app window

Use `PrintWindow` on the window handle found by title `"SimpleRGB (preview: no hardware)"`. **Don't** copy screen
regions (`CopyFromScreen`): it captures whatever is on top, which may be the user's private content.
`FindWindow` from PowerShell needs `[NullString]::Value` for the class, or look the window up via
`Get-Process | ? MainWindowTitle -eq ...`.

## Where things live

| Piece | File |
|---|---|
| Layout | `src/SimpleRGB/MainWindow.xaml` (sidebar, center, lighting editor; settings overlay spans all columns) |
| Theme and styles | `src/SimpleRGB/Themes/Dark.xaml` (brushes `Bg/Panel/Card/Line/Accent…`, styles `EffectTile`, `Switch`, `NavItem`, `ZoneItem`) |
| Live LED previews | `Controls/LedStrip.cs` (`Lighting` for effect tiles, or `Snapshot` + `ZoneId` for real zones; animates only while visible) |
| Color picker | `Controls/ColorPicker.xaml(.cs)` (favorites/recents bound to commands) |
| State | `ViewModels/MainViewModel.cs`, `ViewModels/DeviceItems.cs` |

## Pitfalls

- Don't name a `Style` like an existing brush key (`Card` broke `Background="{StaticResource Card}"`).
- Mode/option buttons must act on **Click**, not two-way `IsChecked` bindings (binding echoes once flipped settings on startup).
- `ApplyEditor` ignores writes that don't change anything, so layout/binding echoes never save or toggle sync.
- Hidden items still take a cell in a `UniformGrid`; bind to a filtered list (`DeviceList`) instead.
- Slider `Maximum` must be raised before `Value` (see `LoadEditorFromSelection`).
