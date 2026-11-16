<#
  Codex CLI -> Islet bridge (experimental).
  In ~/.codex/config.toml (Settings > AI agents has a "Copy" button for this line):
    notify = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\path\\to\\islet-codex-notify.ps1"]
  Codex passes a JSON payload as a command-line argument; we forward whichever argument is JSON.
#>
$json = $args | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -First 1
if (-not $json) { exit 0 }
try {
    Invoke-RestMethod -Method Post -Uri 'http://localhost:47823/agent/codex' -ContentType 'application/json' `
        -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 2 | Out-Null
} catch { }   # island not running: never bother Codex
exit 0
