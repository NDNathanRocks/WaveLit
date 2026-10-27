# Notes for Microsoft Store certification

Paste the following into **Partner Center → Submission → Notes for certification**.

---

Wavelit is a live-wallpaper app. It shows videos, images, animated shaders or web pages *behind the desktop icons*, in place of the static Windows wallpaper.

**How it does this.** At startup Wavelit creates one borderless child window per monitor and parents it into the desktop's wallpaper layer, using the standard `Progman`/`WorkerW` technique that other Store wallpaper apps also use. Those windows are visible wallpaper content. They take no input (clicks go to the desktop icons), aren't hidden from the user, and never cover other apps. Wavelit doesn't inject code into Explorer or any other process, never elevates, and needs no special capabilities beyond `runFullTrust` and `internetClient`.

**Helper process.** The Studio's Picture tab starts `Wavelit.Create.exe`, which ships inside this package. It sharpens images, and optionally generates them with a local AI model. It runs only while the user is creating and exits after two idle minutes.

**Optional model download (data only).** If the user opts in, Wavelit downloads model weights, which are data files and not code (about 6.9 GB), from Hugging Face. Every file is pinned to an exact commit and verified against a SHA-256 hash in the app before use. All executable code, including ONNX Runtime and DirectML, ships in the package.

**Where to find it.** After the first-run setup, Wavelit's window opens (it's also in the system tray). Left-click the tray icon to open it again; right-click for Pause / Play / Quit.

**How to test.**

1. Launch Wavelit. A short setup appears: pick a wallpaper (it changes live behind the window), then **Get started**. No account or key is needed.
2. **Discover**: the home page is curated (Wavelit's shaders, then hand-picked Featured wallpapers); the mood chips (Nature, Space, …) show curated feeds. Search "mountains" or "earth" for results from Wallhaven, Wikimedia Commons, NASA and Openverse, all with no keys. Hover a card and click **Set as wallpaper**.
3. **Studio → Picture**: type a description, choose **Quick try**, then **Create**. Pick a result. It's sharpened to the screen size and set as the wallpaper.
   **Studio → Live wallpaper / Overlay** writes animated shaders with AI. With no key, choose **Any AI chat**: **Copy prompt**, paste it into any chat AI, paste the answer back, then **Check & preview**. Claude and Cloudflare need the user's own key (bring-your-own-key; the app has no backend and ships no keys). AI output is GLSL only, run through WebGL `shaderSource` in the sandboxed WebView2 after a static check, a compile and a timed GPU test; it is never JavaScript and is never applied without the user's click.
4. **Displays**: with two or more monitors, **Extend** continues one picture across all screens. **Framing** lets you drag and zoom it.
5. **Overlays** (Overlays page, or the setup screen): choose **Stardust** or any other card, then move the mouse over an empty part of the desktop. The particles part around the cursor; a click sends out a burst. Wavelit reads the cursor position with `GetCursorPos` only while the wallpaper is visible (no hooks, no keyboard), and only reacts over the bare desktop.
6. Open any app fullscreen (for example press F11 in Edge). Wavelit pauses itself and CPU/GPU use drops to about zero. It resumes when you return to the desktop.
7. Quit from the tray menu. The normal Windows wallpaper comes back immediately.

**Content safety.**
- Wallhaven is always queried as safe-for-work only. Wavelit never sends a Wallhaven key, so adult results can't be requested.
- Openverse is queried with `mature=false`, and Pixabay with `safesearch=true`.
- The home page and mood chips never show raw API results: a hand-picked Featured list (checked by eye), plus Wallhaven tag queries that exclude portraits and characters.
- Search text and AI prompts (pictures and Studio shaders) with adult terms are blocked in the app.
- Cloud generators are asked for safe output.
- Local generations pass an on-device image safety classifier before they are shown.
- Web wallpapers are addresses the user types; there's no web search.

**Generative AI disclosure (policy 11.16).** The Studio can write live wallpapers and overlays (GLSL shaders) with the user's own Anthropic key, a free Cloudflare account, or by copy-paste with any chat AI. It can also make pictures with a local model or a cloud generator. This is optional and off until the user brings a key or pastes a reply. Disclosed in the listing ("Uses live generative AI (optional, bring your own key)") and in Partner Center.
- **Labels.** Every AI creation is marked "Made with AI" (model and prompt kept with it). The Library has a filter: Everything / Made with AI / Not AI.
- **In-app reporting.** There's a **Report** button on each Studio result and on AI items in the Library. It opens a pre-filled GitHub issue (label `ai-content-report`) in the browser. Nothing is sent automatically.
- **Triage.** Reports are reviewed by the maintainers within 7 days. Actions: extend the blocked-terms list (`ContentGuard`) and the shader lint, tighten the system prompt, and ship a fix. Users can remove any creation locally at once (right-click → Remove).
- **Guardrails.** Generated content is GLSL only, never script. It runs through WebGL `shaderSource` in a sandboxed WebView2 with its own profile and no network access. Before the user sees anything it must pass a static lint (no unbounded loops, textures or preprocessor) and a GPU budget probe on the user's own graphics card (3 ms at 1080p, with every slider at its maximum and loud music). Prompts are checked against blocked terms. Local pictures pass an on-device safety classifier. Nothing is applied without the user's click. Music visualizers never flash ("Reduce flashing" is on by default: beat effects are soft and under three per second).

**How to test the new features without a key.**
- *Music:* Discover → search "neon" or "space" → apply **Neon Bars**, **Synthwave Drive** or **Resonant Stardust**, then play any music. In silence they idle calmly. Overlays → **Resonant Stardust**, or turn on "Move with the music" for any overlay.
- *Studio → Music tab:* pick "Any AI chat (free)", copy the prompt, paste a reply. The preview plays a built-in demo beat; nothing is heard.
- *Desktop page:* turn on "Widgets on the desktop", pick a theme, then drag the clock in the preview. The real desktop updates. Taskbar: "Centred/Left" and "Invisible taskbar" use Windows' own settings and are restored when Wavelit quits.

**Restricted capability `unvirtualizedResources` (justification).** Wavelit is a desktop personalisation app. At the user's request it changes per-user settings that belong to Windows and to another app:
- the taskbar icon alignment (`HKCU\…\Explorer\Advanced\TaskbarAl`);
- start-with-Windows (`HKCU\…\Run`, through the `wavelit.exe` execution alias);
- TranslucentTB's `settings.json`, for a see-through taskbar (only when the user picks a look).

With MSIX write virtualization these changes would land in a private copy and have no effect. Every change is opt-in, the previous values are saved first, and they are restored when the feature is turned off (the taskbar settings also on quit). Wavelit's own data stays in `%LOCALAPPDATA%\Wavelit`.

**Privacy.** No telemetry, no account. Network requests go only to the services the user uses (listed in the privacy policy). Privacy policy: `<<GITHUB PAGES URL>>/privacy-policy`.
