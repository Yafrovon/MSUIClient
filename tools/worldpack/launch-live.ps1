# Live protocol run (tier 3): logs a character in and runs a protocol from tools/worldpack/gen-live.py.
#   powershell -File tools/worldpack/launch-live.ps1 -Protocol <protocol.txt> -Name <log name> [-Character Gilnwar]
#       [-Timeout 5400] [-CharacterSelect] [-Arena] [-LogDir <dir>]
# Starts where the character stands (--live-in-place); -Arena teleports to the old movement-arena fixture first.
# -CharacterSelect stops at character select (for a protocol that creates a character: char-create).
# Config: scratch/worldbuilder/live-config.json (git-ignored: it holds the account password - never copy it into a
# tracked file) + live-settings.json. Log: <LogDir>/<Name>.log, result line "[live-run] PROTOCOL_DONE failures=N";
# the Windows PID goes to <LogDir>/live-client.winpid - stop ONLY that PID. Refuses while any MSUIClient runs.
param(
    [Parameter(Mandatory=$true)][string]$Protocol,
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$Character = "Gilnwar",
    [int]$Timeout = 5400,
    [switch]$CharacterSelect,
    [switch]$Arena,
    [string]$LogDir = "",
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Exe) { $Exe = Join-Path $repo "MSUIClient\bin\Debug\net8.0\MSUIClient.exe" }
$sp = if ($LogDir) { $LogDir } else { Join-Path $repo "scratch\worldbuilder\logs" }
New-Item -ItemType Directory -Force $sp | Out-Null
if (Get-Process -Name MSUIClient -ErrorAction SilentlyContinue) { Write-Output "ABORT: an MSUIClient is already running"; exit 2 }
$out = Join-Path $sp "live-out"
New-Item -ItemType Directory -Force $out | Out-Null
$env:MSUI_SETTINGS_PATH = Join-Path $repo "scratch\worldbuilder\live-settings.json"
$argList = @((Join-Path $repo "scratch\worldbuilder\live-config.json"), "--live-bootstrap", "--live-protocol", (Resolve-Path $Protocol).Path,
    "--out", $out, "--timeout", "$Timeout", "--character", $Character)
if (-not $Arena) { $argList += "--live-in-place" }
if ($CharacterSelect) { $argList += "--character-select" }
$p = Start-Process -FilePath $Exe -WindowStyle Hidden -ArgumentList $argList -WorkingDirectory (Join-Path $repo "MSUIClient") `
    -RedirectStandardOutput (Join-Path $sp "$Name.log") -RedirectStandardError (Join-Path $sp "$Name.err") -PassThru
Set-Content -Path (Join-Path $sp "live-client.winpid") -Value $p.Id -Encoding ascii
Write-Output "launched PID $($p.Id); log $(Join-Path $sp "$Name.log")"
