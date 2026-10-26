# Wavelit

**Live wallpapers for Windows 10 and 11.** Videos, images, real-time GPU shaders and web pages behind your desktop icons, on every monitor. Wavelit stays out of your way: it pauses automatically when a game or fullscreen app is running.

- **Extend across monitors, properly.** One picture continues across all your screens, lined up with your Windows display arrangement: any size, resolution or orientation. Each screen draws its own part on a shared clock, so videos stay in sync. The subject goes on your main screen, never on a bezel.
- **Bezel and physical-size compensation.** Wavelit reads each monitor's real size, so a horizon stays straight across a 24" and a 27" screen, and can hide a strip of picture behind the frames.
- **Framing editor.** Drag and zoom a video or image behind a map of your monitors; the desktop follows live.
- **Create wallpapers from a prompt.** Shaped exactly like your screens, then AI-sharpened to full resolution. Generate on your own GPU (private, unlimited, optional download), with your free Cloudflare account, or with a no-account quick try.
- **Living stills.** Turn any image into a live wallpaper with slow drift, rain, snow, fireflies or floating dust, at a few percent of GPU.
- **Free sources with no keys.** Wallhaven wallpapers, NASA space videos and Creative Commons images from Openverse. Add a free Pixabay key for 4K stock video. Paste any Shadertoy-style shader.
- **Follows the sun.** One wallpaper by day, another by night, switched at your local sunrise and sunset (worked out on your PC).
- **Remembers each monitor setup.** Docked, laptop-only and TV setups each keep their own wallpapers.
- **Seamless video loops** with crossfade, and **zero cost while gaming**: fullscreen apps, the lock screen, display sleep, battery saver and Remote Desktop pause or suspend rendering.
- **Private.** No account, no telemetry, no ads. Keys are encrypted on your PC.

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
| `--snapshot` | Save what every wallpaper window draws, laid out like your monitors, to `logs/snapshot.png`. |

Logs, settings and the cache live in `%LOCALAPPDATA%\Wavelit`.

## Getting API keys (optional, free)

Wavelit works without any key: included shaders, Wallhaven, NASA and Openverse in Discover, the Create page's quick try, local AI, your own videos, images and web pages.

- **Pixabay** (recommended, instant). Sign up at [pixabay.com](https://pixabay.com/accounts/register/); your key is on [pixabay.com/api/docs](https://pixabay.com/api/docs/). Paste it in *Settings → Pixabay videos*.
- **Cloudflare Workers AI** (for sharper cloud creations). Create a free account, copy your Account ID, and create an API token with the "Workers AI" template. Paste both in *Settings → Cloudflare*.
- **Pexels**: new keys are paused by Pexels; existing keys still work.
- **Shadertoy**: needs a Shadertoy account. Alternatively, use *Library → Paste a shader*.

## How it works

Wavelit places a child window between the desktop wallpaper and the icons. This is the standard "WorkerW" technique; it supports both the classic layout and the Windows 11 24H2+ layout. Each monitor's window hosts a WebView2 that plays video (with hardware decoding), runs a Shadertoy-compatible WebGL2 runtime, or shows a web page. A governor watches foreground windows, display power, the lock screen and battery, and pauses or fully suspends renderers when the wallpaper can't be seen. See [`plan.md`](plan.md) for the architecture.

## Credits & licenses

- Wavelit is licensed under the **GNU GPL v3** (see [LICENSE](LICENSE)). The "Wavelit" name and logo are not covered by the license.
- Third-party components and models: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- Media from online sources belongs to its creators and is credited on every card (Wallhaven, NASA, Openverse, Pixabay, Pexels, Shadertoy).
- Built with [WPF-UI](https://github.com/lepoco/wpfui) (MIT), [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT), [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) (MIT), [Serilog](https://serilog.net) (Apache-2.0) and Microsoft Edge WebView2.

## Contributing

Issues and pull requests are welcome. Contributions are accepted under the GPL v3. See [`CLAUDE.md`](CLAUDE.md) for the engineering ground rules (threading model, platform gotchas, review checklist).
