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
- images you create on the Create page (`creations`), and the optional Local AI pack (`models`), which you can remove in Settings;
- the city you pick for day-and-night wallpapers (used only to work out sunrise and sunset on your PC);
- log files for troubleshooting, kept for 7 days. Logs never contain API keys or prompts.

Uninstalling Wavelit, or deleting that folder, removes all of it.

## The mouse cursor (overlays and interactive wallpapers)

When you turn on an **overlay** that reacts to the cursor (or use a wallpaper that reacts to the mouse), Wavelit reads where the mouse cursor is, about 60 times a second, and whether the main button is pressed. It does this only while that wallpaper is visible and only reacts while the cursor is over the bare desktop. It uses no input hooks and never reads the keyboard. The position is passed to the wallpaper on your PC and is never stored, logged or sent anywhere. Choosing no overlay, or the "Still" cursor response, stops it completely.

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
| Cloudflare Workers AI (`api.cloudflare.com`) | You create with **Cloudflare** | Your prompt, the image size, and your Cloudflare account ID and token |
| Hugging Face (`huggingface.co`) | You download the optional Local AI pack | A normal file download (no prompts, nothing about you) |
| Web pages you add | You set a web page as wallpaper | Normal web browsing requests to that site |

**Creating "On this PC"** (Local AI) runs entirely on your computer: prompts and images never leave it.

These services have their own privacy policies: [Wallhaven](https://wallhaven.cc/privacy), [Wikimedia](https://foundation.wikimedia.org/wiki/Policy:Privacy_policy), [NASA](https://www.nasa.gov/privacy/), [Openverse](https://openverse.org/privacy), [Pixabay](https://pixabay.com/service/privacy/), [Pexels](https://www.pexels.com/privacy-policy/), [Shadertoy](https://www.shadertoy.com/terms), [Pollinations](https://pollinations.ai), [Cloudflare](https://www.cloudflare.com/privacypolicy/) and [Hugging Face](https://huggingface.co/privacy).

## Children

Wavelit doesn't knowingly collect any personal information from anyone, including children.

## Contact

Open an issue on the project's GitHub page.
