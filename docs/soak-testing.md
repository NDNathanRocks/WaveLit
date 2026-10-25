# Soak testing (do this before every release)

Wallpapers run for days, so a slow leak is the most likely way for Wavelit to earn a 1-star review. Before launch, and before any release that touches `Wallpaper/` or `Web/`, run this test.

## Run

```powershell
dotnet src/Wavelit.App/bin/Release/net10.0-windows/Wavelit.dll --soak
```

`--soak` appends a sample to `%LOCALAPPDATA%\Wavelit\logs\resources.csv` every minute with these columns:
- Wavelit's private memory, working set and managed heap;
- handle, GDI and USER object counts;
- private memory and process count across **all** WebView2 processes (browser, GPU, renderers).

## Scenarios (at least 12 h each; 72 h for the release candidate)

1. A 4K video with the same wallpaper on every display (loop crossfade on).
2. A multipass Shadertoy shader with a texture.
3. A web page wallpaper.
4. Normal daily use: games, fullscreen video, locking the PC, sleep and wake, plugging and unplugging a monitor.

## Pass criteria

- After the first hour, `private_mb` and `browser_private_mb` stay flat (growth under 5%).
- `handles`, `gdi` and `user` are flat.
- `browser_processes` stays constant (no orphaned renderers).
- The log shows no `ERR` lines.

Paste the CSV into a spreadsheet and chart each column. Any steady upward line is a bug, however small.

## Known baselines (dev machine, 2 displays, 2026-10-25)

| State | Wavelit | Wavelit + WebView2 total |
|---|---|---|
| Two 30 fps shaders playing | ~140 MB | ~450 MB |
| After 20 rapid video/shader swaps | ~190 MB (before GC) | ~420 MB |
| Fullscreen app open (suspended) | renderer CPU: 0.00 s over 5 s | |
