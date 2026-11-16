# Privacy

Islet has **no accounts, no analytics, no telemetry and no ads**. Everything runs on your PC.

## What stays on your PC

- Settings, shelf and pinned clipboard items: `%APPDATA%\Islet`.
- Clipboard history lives in memory only (just pinned text is saved). Content that password managers mark as
  private is never recorded.
- The Live Activities / AI agents API listens on `localhost` only and rejects requests from web pages.
- Claude Code hook data (prompts, tool names, answers) goes from Claude Code to Islet on your PC and nowhere else.
- Meeting mode only reads *which* app uses your microphone (from Windows' privacy records) and mutes / unmutes the
  mic when you ask. Islet never listens to or records audio.
- The Downloads watcher only looks at file names and sizes in your Downloads folder; nothing is uploaded.

## What goes over the internet (only when the feature is on)

| Feature | Sent to | What is sent | Default |
|---|---|---|---|
| Synced lyrics | lrclib.net | Song title, artist, duration | On (Settings → Music) |
| Weather | open-meteo.com | The city name you typed, then its coordinates | Off |
| Update checks | github.com | A normal download request for the release list | On (Settings → About) |

## Things Islet changes on your PC (and how to undo them)

- **Start with Windows**: a value in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (toggle in Settings).
- **Claude Code hooks**: only if you click *Install hooks*; a backup of `~/.claude/settings.json` is made first.
  Remove them with *Remove* in Settings.
- Uninstalling Islet removes both automatically.
