# Wavelit Privacy Policy

_Last updated: 26 October 2026_

Wavelit is a live-wallpaper app for Windows. This policy explains what data it handles.

## What Wavelit collects

**Nothing is sent to us.** Wavelit has no account system, no analytics, no telemetry, no advertising and no crash reporting service. The developer receives no data from your use of the app.

## Data stored on your PC

Wavelit stores these files in `%LOCALAPPDATA%\Wavelit` on your computer:

- your settings and wallpaper library, including the wallpapers remembered for each monitor setup;
- API keys and tokens you choose to enter, encrypted with Windows Data Protection (DPAPI) so only your Windows account can read them;
- downloaded videos, images, shader data and thumbnails (a cache you can clear in Settings);
- images, live wallpapers and overlays you make in the Studio (`creations`, including the Studio history), and the optional Local AI pack (`models`), which you can remove in Settings;
- the city you pick for day-and-night wallpapers (used only to work out sunrise and sunset on your PC);
- log files for troubleshooting, kept for 7 days. Logs never contain API keys or prompts (the Studio logs only the number of AI tokens used, so you can see the cost).

Deleting that folder removes all of it. The Microsoft Store version keeps it in the same place (so your library survives reinstalling); delete the folder after uninstalling if you want everything gone.

## The mouse cursor (overlays and interactive wallpapers)

When you turn on an **overlay** that reacts to the cursor (or use a wallpaper that reacts to the mouse), Wavelit reads where the mouse cursor is, about 60 times a second, and whether the main button is pressed. It does this only while that wallpaper is visible and only reacts while the cursor is over the bare desktop. It uses no input hooks and never reads the keyboard. The position is passed to the wallpaper on your PC and is never stored, logged or sent anywhere. Choosing no overlay, or the "Still" cursor response, stops it completely.

## Music (audio visualizers)

When a **music wallpaper or overlay** is showing (or "Move with the music" is on), Wavelit listens to what your PC is playing: a loopback of the default speakers or headphones. It **never uses the microphone**. The sound is analysed in memory into a few numbers (loudness per frequency band, beats, tempo) about 60 times a second and passed to the wallpaper on your PC. No audio is recorded, stored, logged or sent anywhere. Listening stops whenever the wallpaper is hidden, paused or covered, and Settings → Music turns it off completely.

## Desktop widgets and the taskbar

The clock, date, greeting and title you put on the desktop are drawn by Wavelit on your PC. The text you type (for example your name in a greeting) is kept only in Wavelit's settings file.

If you choose a taskbar option on the Desktop page (icon alignment, or hiding the taskbar), Wavelit changes Windows' own per-user taskbar settings. It first saves your previous values in `taskbar-original.json` in the same folder, and puts them back when you turn the option off or quit Wavelit (unless you chose "Keep after quitting"). Nothing is injected into Windows Explorer and no administrator rights are used.

## Reporting AI-made content

Wavelit can also apply a see-through taskbar look by editing the appearance section of **TranslucentTB**'s own settings file (only when you pick a look). Your original TranslucentTB settings are saved first, and "Undo my changes" restores them.

The **Report** button on AI creations opens a pre-filled report on GitHub in your browser. Wavelit itself sends nothing: you see the report and decide whether to submit it, and the prompt and code are included only if you tick the box. Reports on GitHub are public.

## Third-party services

Wavelit only connects to these services when you use the matching feature. Like any web request, each one sees your IP address.

| Service | When | What is sent |
|---|---|---|
| Wallhaven (`wallhaven.cc`) | Discover searches with Images on | Your search text (safe-for-work results only) |
| Wikimedia Commons (`commons.wikimedia.org`, `upload.wikimedia.org`) | Discover with Videos on (also the Featured videos) | Your search text, or a curated term such as "aurora" |
| NASA Image and Video Library (`images-api.nasa.gov`, `images-assets.nasa.gov`) | Discover searches with Videos on | Your search text |
| Openverse (`api.openverse.org`) and the sites hosting its images | Discover searches with Images on | Your search text |
| Pixabay (`pixabay.com`) | You searched with a Pixabay key | Your search text and your Pixabay key |
| Pexels (`api.pexels.com`, `videos.pexels.com`, `images.pexels.com`) | You searched with a Pexels key | Your search text and your Pexels key |
| Shadertoy (`www.shadertoy.com`) | You searched with a Shadertoy key | Your search text and your Shadertoy key |
| Pollinations (`image.pollinations.ai`) | You create with **Quick try** | Your prompt and the image size |
| Cloudflare Workers AI (`api.cloudflare.com`) | You create with **Cloudflare** (pictures or shaders) | Your prompt and options, the image size, for shaders Wavelit's shader instructions plus the previous shader code and its error messages when fixing or refining, and your Cloudflare account ID and token |
| Anthropic (`api.anthropic.com`) | You make a live wallpaper or overlay with **Claude** in the Studio, or test your key in Settings | Your prompt and options, Wavelit's shader instructions, the previous shader code and its error messages when fixing or refining, and your Anthropic API key. Anthropic bills your own account. |
| Hugging Face (`huggingface.co`) | You download the optional Local AI pack | A normal file download (no prompts, nothing about you) |
| Web pages you add | You set a web page as wallpaper | Normal web browsing requests to that site |

**Creating "On this PC"** (Local AI) runs entirely on your computer: prompts and images never leave it.

**"Any AI chat"** in the Studio sends nothing: Wavelit copies a prompt to your clipboard, and you choose where to paste it. No screenshots, files or other data are ever sent to an AI service, and AI-written shaders are only run as graphics code on your PC after Wavelit has checked them.

These services have their own privacy policies: [Wallhaven](https://wallhaven.cc/privacy), [Wikimedia](https://foundation.wikimedia.org/wiki/Policy:Privacy_policy), [NASA](https://www.nasa.gov/privacy/), [Openverse](https://openverse.org/privacy), [Pixabay](https://pixabay.com/service/privacy/), [Pexels](https://www.pexels.com/privacy-policy/), [Shadertoy](https://www.shadertoy.com/terms), [Pollinations](https://pollinations.ai), [Cloudflare](https://www.cloudflare.com/privacypolicy/), [Anthropic](https://www.anthropic.com/legal/privacy) and [Hugging Face](https://huggingface.co/privacy).

## Children

Wavelit doesn't knowingly collect any personal information from anyone, including children.

## Contact

Open an issue on the project's GitHub page.
