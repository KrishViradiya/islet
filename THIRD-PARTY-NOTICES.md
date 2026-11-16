# Third-party notices

Islet is MIT licensed (see `LICENSE`). It includes or uses the following. Islet's own code, icon and
artwork were written for this project; nothing was copied from the projects listed under "Inspiration".

## Bundled software

| Component | License | Notes |
|---|---|---|
| [.NET runtime and WPF / Windows Forms](https://github.com/dotnet/runtime) | MIT, © .NET Foundation and Contributors | Bundled in self-contained releases. Full license and the runtime's own third-party notices: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> and <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT> |
| [Velopack](https://github.com/velopack/velopack) | MIT | Installer, uninstaller and self-updates. License: <https://github.com/velopack/velopack/blob/develop/LICENSE> |

## Online services (all free, no keys, used only when the feature is on)

| Service | Used for | Terms |
|---|---|---|
| [Open-Meteo](https://open-meteo.com) | Weather (opt-in) | Data licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) — "Weather data by Open-Meteo.com". Free for non-commercial use. |
| [LRCLIB](https://lrclib.net) | Synced lyrics (can be turned off) | Lyrics belong to their respective rights holders. Islet only displays them while a song plays; it does not store or redistribute them. |
| [GitHub Releases](https://github.com/KrishViradiya/islet/releases) | Update checks (can be turned off) | Standard GitHub terms. |

## Operating system resources (not redistributed)

- **Segoe Fluent Icons / Segoe MDL2 Assets / Segoe UI Symbol** — fonts that ship with Windows, referenced by name only.
- **Windows media controls, Core Audio, CapabilityAccessManager** — Windows APIs.
- **curl.exe** — ships with Windows 10/11; Islet's optional Claude Code hooks call it.

## Trademarks

Spotify, YouTube, Apple Music, Claude, Claude Code, Codex and Windows are trademarks of their respective
owners. Islet is an independent project and is not affiliated with or endorsed by them; names are used
only to describe compatibility.

## Inspiration

UI and feature ideas were inspired by [exuupery/island](https://github.com/exuupery/island) and
[TwoCode](https://twocode.nl/). Islet's implementation, code and artwork are original.
