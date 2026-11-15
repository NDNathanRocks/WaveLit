# Microsoft Store release checklist

Status checked 2026-11-14 against `main`. Tick each box as it's done.

## Done (checked)

- [x] Round 9 (`ship-ready`) merged into `main` and pushed.
- [x] CI `build` passes on `main`: build, 461 unit tests, MSIX pack.
- [x] CI artifact `Wavelit-msix` is produced (about 131 MB; artifacts are kept 90 days).
- [x] Manifest declares `runFullTrust` only (no `unvirtualizedResources`), has the `WavelitStartup` startup task, and uses the reserved identity `NathanDSouza.WaveLit`.
- [x] Privacy policy is live: https://ndnathanrocks.github.io/WaveLit/privacy-policy
- [x] `docs/store-reviewer-notes.md` matches the code (capabilities, 3 ms GPU budget, Studio flow, taskbar section hidden in the Store build).

## 1. Get the package

- [ ] GitHub → Actions → **build** → latest run on `main` → Artifacts → download **Wavelit-msix**.
- [ ] Unzip it to get `Wavelit-0.1.N.0-x64.msix`. It's unsigned on purpose (the Store signs it), so it won't install by double-clicking.

## 2. Partner Center submission (first round: private)

- [ ] **Pricing and availability:** price (about $3); Visibility → **Private audience** (your own Microsoft account) for the first round.
- [ ] **Properties:** category Personalization; privacy policy URL above.
- [ ] **Age ratings:** complete the questionnaire (user-generated / AI content: yes; web access: yes; safe-search only).
- [ ] **Packages:** upload the `.msix` and confirm it validates.
- [ ] **Store listing:** description, at least one screenshot, and the line "Uses live generative AI (optional, bring your own key)".
- [ ] **Generative AI disclosure:** tick it in Partner Center (policy 11.16).
- [ ] **Notes for certification:** paste `docs/store-reviewer-notes.md` (everything below its `---` line).
- [ ] **`runFullTrust` justification**, if asked: desktop app that embeds wallpaper windows in the desktop layer and starts its bundled helper process.
- [ ] Submit. Certification usually takes 1–3 business days; if it fails, keep the report.

## 3. Test the Store-signed build (install from the private link)

- [ ] App launches; a wallpaper shows on both monitors, including the portrait one (negative coordinates).
- [ ] Settings → Start with Windows → Wavelit appears under Windows Settings → Apps → Startup and starts after a reboot.
- [ ] Desktop page shows the Store note instead of the taskbar options.
- [ ] Dashboard pages open: Discover, Studio (three choices; Overlay → Design it → Keep), Overlays, Displays, Desktop, Settings.
- [ ] Overlays react to the cursor; "Move with the music" responds to real music playing.
- [ ] Desktop widgets lean and pulse when those toggles are on.
- [ ] A fullscreen app (F11 in Edge) pauses Wavelit; returning to the desktop resumes it.
- [ ] Quit from the tray restores the normal Windows wallpaper.
- [ ] Soak test from `docs/soak-testing.md` (12–72 h) on this build.

## 4. Before going public

- [ ] Formal trademark search for "Wavelit" (reserving the Store name is not a trademark check).
- [ ] Decide on Shadertoy in the paid build: get Shadertoy's written OK, or keep Shadertoy search in the GitHub build only (`plan.md` §10).
- [ ] Switch Visibility to public and resubmit. Each upload needs a higher version; CI uses `0.1.<run number>.0`.
