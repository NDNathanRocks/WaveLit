# Notes for Microsoft Store certification

Paste the following into **Partner Center → Submission → Notes for certification**.

---

Wavelit is a live-wallpaper app. It shows videos, images, animated shaders or web pages *behind the desktop icons*, in place of the static Windows wallpaper.

**How it does this.** At startup Wavelit creates one borderless child window per monitor and parents it into the desktop's wallpaper layer, using the standard `Progman`/`WorkerW` technique that other Store wallpaper apps also use. Those windows are visible wallpaper content. They take no input (clicks go to the desktop icons), aren't hidden from the user, and never cover other apps. Wavelit doesn't inject code into Explorer or any other process, never elevates, and needs no special capabilities beyond `runFullTrust` and `internetClient`.

**Helper process.** The Create page starts `Wavelit.Create.exe`, which ships inside this package. It sharpens images, and optionally generates them with a local AI model. It runs only while the user is creating and exits after two idle minutes.

**Optional model download (data only).** If the user opts in, Wavelit downloads model weights, which are data files and not code (about 6.9 GB), from Hugging Face. Every file is pinned to an exact commit and verified against a SHA-256 hash in the app before use. All executable code, including ONNX Runtime and DirectML, ships in the package.

**Where to find it.** After the first-run setup, Wavelit's window opens (it's also in the system tray). Left-click the tray icon to open it again; right-click for Pause / Play / Quit.

**How to test.**

1. Launch Wavelit. A short setup appears: pick a wallpaper (it changes live behind the window), then **Get started**. No account or key is needed.
2. **Discover**: the home page is curated (Wavelit's shaders, then hand-picked Featured wallpapers); the mood chips (Nature, Space, …) show curated feeds. Search "mountains" or "earth" for results from Wallhaven, Wikimedia Commons, NASA and Openverse, all with no keys. Hover a card and click **Set as wallpaper**.
3. **Create**: type a description, choose **Quick try**, then **Create**. Pick a result. It's sharpened to the screen size and set as the wallpaper.
4. **Displays**: with two or more monitors, **Extend** continues one picture across all screens. **Framing** lets you drag and zoom it.
5. **Overlays** (Overlays page, or the setup screen): choose **Stardust** or any other card, then move the mouse over an empty part of the desktop. The particles part around the cursor; a click sends out a burst. Wavelit reads the cursor position with `GetCursorPos` only while the wallpaper is visible (no hooks, no keyboard), and only reacts over the bare desktop.
6. Open any app fullscreen (for example press F11 in Edge). Wavelit pauses itself and CPU/GPU use drops to about zero. It resumes when you return to the desktop.
7. Quit from the tray menu. The normal Windows wallpaper comes back immediately.

**Content safety.**
- Wallhaven is always queried as safe-for-work only. Wavelit never sends a Wallhaven key, so adult results can't be requested.
- Openverse is queried with `mature=false`, and Pixabay with `safesearch=true`.
- The home page and mood chips never show raw API results: a hand-picked Featured list (checked by eye), plus Wallhaven tag queries that exclude portraits and characters.
- Search text and AI prompts with adult terms are blocked in the app.
- Cloud generators are asked for safe output.
- Local generations pass an on-device image safety classifier before they are shown.
- Web wallpapers are addresses the user types; there's no web search.

**Privacy.** No telemetry, no account. Network requests go only to the services the user uses (listed in the privacy policy). Privacy policy: `<<GITHUB PAGES URL>>/privacy-policy`.
