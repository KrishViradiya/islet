<#
  Islet CLI - put anything on your Dynamic Island.

  islet run <command...>                 Run a command; the island shows it live, parses "NN%" from its
                                         output as progress, and turns green/red when it exits.
  islet push <title> [subtitle] [0..1]   Create/update an activity (prints its id)
  islet set <id> <0..1> [subtitle]       Update progress of an activity
  islet done <id> [subtitle]             Mark success
  islet fail <id> [subtitle]             Mark failure
  islet clear <id>                       Dismiss
  islet ls                               List activities

  Examples:
    islet run npm run build
    islet run pip install torch
    $id = islet push "Rendering video" "frame 1/900" 0
    islet set $id 0.5 "frame 450/900"
    islet done $id
#>

$ErrorActionPreference = 'Stop'
$Base = 'http://localhost:47823'

function Invoke-Islet([string]$Method, [string]$Path, $Body) {
    try {
        $params = @{ Method = $Method; Uri = "$Base$Path"; TimeoutSec = 2; ContentType = 'application/json' }
        if ($null -ne $Body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress)) }
        return Invoke-RestMethod @params
    } catch {
        return $null   # island not running: never break the user's command
    }
}

function Show-Help {
    if ((Get-Content $PSCommandPath -Raw) -match '(?s)<#(.*?)#>') { Write-Host $Matches[1] }
}

$verb = if ($args.Count -gt 0) { $args[0] } else { 'help' }
$rest = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }

switch ($verb) {
    'run' {
        if ($rest.Count -eq 0) { Write-Host 'usage: islet run <command...>'; exit 2 }
        $command = $rest -join ' '
        # Show "build.bat --fast" rather than "C:\long\path\to\build.bat --fast"
        $shown = @($rest)
        if ($shown[0] -match '[\\/]') { $shown[0] = Split-Path $shown[0] -Leaf }
        $shown = $shown -join ' '
        $title = if ($shown.Length -gt 48) { $shown.Substring(0, 47) + '...' } else { $shown }

        $created = Invoke-Islet POST '/activities' @{ title = $title; subtitle = 'running'; state = 'running' }
        $id = if ($created) { $created.id } else { $null }

        $timer = [Diagnostics.Stopwatch]::StartNew()
        $lastSent = [datetime]::MinValue

        # cmd /c so builtins, npm/pip shims and pipes all work; 2>&1 merges stderr into the stream we parse.
        & cmd.exe /d /c "$command 2>&1" | ForEach-Object {
            $line = "$_"
            Write-Host $line
            if (-not $id) { return }
            $now = Get-Date
            if (($now - $lastSent).TotalMilliseconds -lt 300) { return }   # throttle

            $body = @{ }
            $text = $line.Trim()
            if ($text.Length -gt 0) { $body.subtitle = if ($text.Length -gt 90) { $text.Substring(0, 89) + '...' } else { $text } }
            # Find the last "NN%" / "NN.N%" on the line (pip, npm, ffmpeg-style progress output)
            $found = [regex]::Matches($line, '(\d{1,3}(?:\.\d+)?)\s?%')
            if ($found.Count -gt 0) {
                $pct = [double]$found[$found.Count - 1].Groups[1].Value
                if ($pct -le 100) { $body.progress = $pct / 100 }
            }
            if ($body.Count -gt 0) {
                $null = Invoke-Islet POST "/activities/$id" $body
                $lastSent = $now
            }
        }
        $code = $LASTEXITCODE
        $secs = [int]$timer.Elapsed.TotalSeconds

        if ($id) {
            $state = if ($code -eq 0) { 'success' } else { 'error' }
            $null = Invoke-Islet POST "/activities/$id" @{ state = $state; subtitle = "exit code $code - ${secs}s" }
        }
        exit $code
    }
    'push' {
        $body = @{ title = $rest[0]; state = 'running' }
        if ($rest.Count -gt 1) { $body.subtitle = $rest[1] }
        if ($rest.Count -gt 2) { $body.progress = [double]$rest[2] }
        $r = Invoke-Islet POST '/activities' $body
        if ($r) { $r.id } else { Write-Error 'Islet is not running.' }
    }
    'set' {
        $body = @{ progress = [double]$rest[1] }
        if ($rest.Count -gt 2) { $body.subtitle = $rest[2] }
        $null = Invoke-Islet POST "/activities/$($rest[0])" $body
    }
    'done' {
        $body = @{ state = 'success' }
        if ($rest.Count -gt 1) { $body.subtitle = $rest[1] }
        $null = Invoke-Islet POST "/activities/$($rest[0])" $body
    }
    'fail' {
        $body = @{ state = 'error' }
        if ($rest.Count -gt 1) { $body.subtitle = $rest[1] }
        $null = Invoke-Islet POST "/activities/$($rest[0])" $body
    }
    'clear' { $null = Invoke-Islet DELETE "/activities/$($rest[0])" $null }
    'ls'    { Invoke-Islet GET '/activities' $null | Format-Table id, state, title, subtitle, progress, elapsed }
    default { Show-Help }
}
