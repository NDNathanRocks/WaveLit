# Wavelit

**Live wallpapers for Windows 10 and 11.** Videos, real-time GPU shaders and web pages behind your desktop icons, on every monitor. Wavelit stays out of your way: it pauses automatically when a game or fullscreen app is running.

- **Seamless video loops.** Crossfade looping hides the jump at the end of clips that weren't made to loop.
- **Live shaders.** Six original shaders are included, and you can search Shadertoy with a free key.
- **Multi-monitor.** Show the same wallpaper on every screen, a different one per screen, or span one across all of them. Mixed resolutions, portrait screens and negative coordinates all work.
- **Zero cost while gaming.** Fullscreen and maximized windows, the lock screen, display sleep, battery saver and Remote Desktop all pause or suspend rendering. Renderer CPU drops to 0%.
- **Search millions of free videos** from Pexels (bring your own free API key).
- **Private.** No account, no telemetry, no ads. API keys are encrypted on your PC.

## Install

| Option | For |
|---|---|
| **Microsoft Store** (coming soon) | One-click install, automatic updates, signed by Microsoft. |
| **Build from source** (below) | Developers. Free forever. |

> Windows 11 **Smart App Control** blocks unsigned executables. Builds from source run fine via `dotnet Wavelit.dll`, or with Smart App Control turned off. Store builds are signed by Microsoft and never blocked.

## Build from source

Requirements: Windows 10 19041+ / Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).

```powershell
git clone <this repo>
cd wavelit
dotnet build Wavelit.sln -c Release
dotnet test tests/Wavelit.Core.Tests
dotnet src/Wavelit.App/bin/Release/net10.0-windows/Wavelit.dll
```

Command-line switches:

| Switch | Effect |
|---|---|
| `--background` | Start in the tray without opening the dashboard (used by start-with-Windows). |
| `--quit` | Ask the running instance to exit cleanly. |
| `--verbose` | Debug-level logs, including every pause and resume decision. |
| `--soak` | Write resource usage to `logs/resources.csv` every minute (leak hunting). |
| `--no-wallpaper` | Dashboard only (UI debugging). |

Logs, settings and the cache live in `%LOCALAPPDATA%\Wavelit`.

## Getting API keys (optional, free)

- **Pexels.** Sign up at [pexels.com/api](https://www.pexels.com/api/) and copy your key into *Settings → Pexels videos*.
- **Shadertoy.** Sign in, open [shadertoy.com/myapps](https://www.shadertoy.com/myapps), create an app key and paste it into *Settings → Shadertoy*.

Without keys, Wavelit still works fully with the included shaders, your own videos (MP4/WebM/MOV) and web pages.

## How it works

Wavelit places a child window between the desktop wallpaper and the icons. This is the standard "WorkerW" technique; it supports both the classic layout and the Windows 11 24H2+ layout. Each monitor's window hosts a WebView2 that plays video (with hardware decoding), runs a Shadertoy-compatible WebGL2 runtime, or shows a web page. A governor watches foreground windows, display power, the lock screen and battery, and pauses or fully suspends renderers when the wallpaper can't be seen. See [`plan.md`](plan.md) for the architecture.

## Credits & licenses

- Wavelit is licensed under the **GNU GPL v3** (see [LICENSE](LICENSE)). The "Wavelit" name and logo are not covered by the license.
- Videos are provided by [Pexels](https://www.pexels.com) under the Pexels License; creators are credited on every card.
- Shadertoy shaders belong to their authors and are licensed CC BY-NC-SA 3.0 unless the author states otherwise.
- Built with [WPF-UI](https://github.com/lepoco/wpfui) (MIT), [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT), [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) (MIT), [Serilog](https://serilog.net) (Apache-2.0) and Microsoft Edge WebView2.

## Contributing

Issues and pull requests are welcome. Contributions are accepted under the GPL v3. See [`CLAUDE.md`](CLAUDE.md) for the engineering ground rules (threading model, platform gotchas, review checklist).
