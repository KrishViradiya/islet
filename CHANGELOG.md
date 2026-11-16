# Changelog

## 0.4.0 — everyday helpers

The island now handles the small things you do dozens of times a day.

- **Meeting mode.** When Teams, Zoom, Meet or any app uses your microphone, the island shows the app and a call
  timer, plus a big mute button. **Ctrl+Alt+M** mutes / unmutes your mic system-wide from anywhere, with a clear
  red indicator so you never wonder "am I muted?".
- **Timers & focus.** One tap for 1 / 5 / 10 / 15 / 25 / 45 minutes, or scroll the custom chip to any length.
  The countdown ring lives in the pill; when time's up the island opens with **+5 min / Done**.
  Scriptable too: `curl -X POST "localhost:47823/timer?minutes=25&label=Focus"`.
- **Downloads.** Works with every browser: a live card while a file downloads, then **Open / Show in folder /
  Keep on shelf** — or drag it straight into an email, chat or folder.
- **Devices.** AirPods-style pop-ups when Bluetooth headphones connect (with battery level when reported),
  charger plugged in / out, and low-battery warnings at 20% and 10%.
- **Split island.** When two things are live (say a timer and music), the second one pops out as its own bubble.
- **Quick actions.** Mic, Timer, Snip (screenshot) and Downloads, one tap away on the Home tab.
- **First-run welcome** that teaches hover, scroll-for-volume, drop-to-shelf and the two hotkeys.
- Content in the folded pill now cross-fades when something new takes over.
- Everything new can be switched off in Settings → Modules (and Appearance → Split island).

## 0.3.0 — first public release

Liquid island with Blip, music with Spotify controls and synced lyrics, Claude Code status and approvals,
Live Activities API and CLI, Privacy Sentinel, shelf, clipboard history, weather, settings, installer and
self-updates.
