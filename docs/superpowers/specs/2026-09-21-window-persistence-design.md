# Window Bounds Persistence Design — 2026-09-21

## Why
Desktop window resets to 1050x720 at default position on every launch. Users expect Eling desktop to reopen where they left it, including maximized state.

## What
Remember position (X/Y), size (Width/Height), and maximized flag across restarts. Restore on startup, save on move/resize/state change. Fail-safe: corrupt or off-screen data falls back to defaults.

## Architecture
No new service. Extend existing `DesktopSettingsStore` (`~/.config/eling/config/desktop-settings.json`, honoring `ELING_USER_SCOPE`) with a `window` section. `MainWindow` is the single consumer: restore once at startup, save debounced at runtime.

```
startup: MainWindow ctor -> DesktopSettingsStore.GetWindowBounds() -> validate + clamp -> apply
runtime: PositionChanged / SizeChanged / WindowState -> debounce 500ms -> SaveWindowBounds()
```

## Components

### DesktopSettingsStore
Add `WindowBounds` record `(X, Y, Width, Height, IsMaximized)` plus `GetWindowBounds()` and `SaveWindowBounds()`. Refactor `GetBackendUrl`/`SetBackendUrl` to shared read-modify-write so backend URL and window data never overwrite each other. Backward compatible: old files without `window` deserialize to null and fall back to defaults.

Validation: Width >= 400, Height >= 300, finite numbers. Invalid data returns null and is ignored.

### MainWindow
Restore in constructor after `InitializeComponent` when services are available. Clamp restored position to union of current screens; if off-screen (monitor unplugged), center with default size. Save is skipped while minimized. When maximized, preserve last normal bounds and only flip `IsMaximized`. XAML keeps `Width=1050 Height=720` as first-run default.

## Error handling
Read/write wrapped in try/catch with warning logs, never crashing the window. Missing file, corrupt JSON, or invalid numbers all fall back silently. Off-screen restore falls back to centered default.

## Testing
Unit-test `DesktopSettingsStore` round-trip (save then read), backward compat (backend URL preserved when saving bounds and vice versa), and invalid-data rejection. Manual verify: move, resize, restart (restored); maximize, restart (maximized); unplug monitor scenario (centered, not lost).
