# Changelog

## 0.4.0 — everyday helpers, a calmer island, and characters

The island now handles the small things you do dozens of times a day — and was redesigned so it stays calm
as features keep coming.

**New layout (built to scale)**
- **Home is a fixed dashboard**: one slot for urgent things (approvals, time's up, calls), a media hero beside a
  2×2 action grid, a strip of small live chips, and a slim volume / status bar. It never grows into a long list.
- **New Live tab** with a count badge: full details for everything ongoing — AI agents, timers, downloads,
  activities and privacy — grouped with headings. Tap any chip on Home to jump there.
- The header now holds only the character, the tabs and two icons. The opened island is wider (600 px) for a
  two-column layout; Shelf and Clipboard scroll inside the island instead of stretching it.

**Characters** — pick one in Settings → Character (with live previews of every reaction):
- **Blip** the robot, **Mochi** the dumpling, **Sprout** the seedling (blooms on good news, droops when you're away)
  and **Orbit** the planet (its moon speeds up while agents work). All original, all drawn in code.

**Everyday helpers**

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
