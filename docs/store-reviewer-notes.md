# Notes for Microsoft Store certification

Paste the following into **Partner Center → Submission → Notes for certification**.

---

Wavelit is a live-wallpaper app. It shows videos, animated shaders or web pages *behind the desktop icons*, in place of the static Windows wallpaper.

**How it does this.** At startup Wavelit creates one borderless child window per monitor and parents it into the desktop's wallpaper layer, using the standard `Progman`/`WorkerW` technique that other Store wallpaper apps also use. Those windows are visible wallpaper content. They take no input (clicks go to the desktop icons), aren't hidden from the user, and never cover other apps. Wavelit doesn't inject code into Explorer or any other process, never elevates, and needs no special capabilities beyond `runFullTrust` and `internetClient`.

**Where to find it.** After launch, Wavelit's window opens (it's also in the system tray). Left-click the tray icon to open it again; right-click for Pause / Play / Quit.

**How to test.**

1. Launch Wavelit. The included "Aurora" wallpaper appears on the desktop. No account or key is needed.
2. Open **Library**, hover any card and click **Set as wallpaper**.
3. Open **Displays** to choose the same wallpaper on every monitor, a different one per monitor, or one spanned across monitors.
4. Open any app fullscreen (for example press F11 in Edge). Wavelit pauses itself and CPU/GPU use drops to about zero. It resumes when you return to the desktop.
5. Online search (Discover) needs the user's own free Pexels or Shadertoy API key, entered in Settings. **Test key for reviewers:** `<<PASTE A PEXELS KEY CREATED FOR CERTIFICATION>>`
6. Quit from the tray menu. The normal Windows wallpaper comes back immediately.

**Content.** Videos come from Pexels, which is moderated and has no adult content, and shaders from Shadertoy. Searches containing adult terms are blocked in the app. Web wallpapers are addresses the user types; there's no web search.

**Privacy.** No telemetry, no account. Network requests go only to api.pexels.com, videos.pexels.com, images.pexels.com and www.shadertoy.com when the user searches or picks a wallpaper. Privacy policy: `<<GITHUB PAGES URL>>/privacy-policy`.
