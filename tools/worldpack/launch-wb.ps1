# World Builder script run (tiers 1+2, publish, probes): boots MSUIClient in Creator Mode and runs a script of World
# Builder commands (shared_docs/WORLD_BUILDER.md §6/§9). No login - it never kicks a live session.
#   powershell -File tools/worldpack/launch-wb.ps1 -Script <script.txt> -Name <log name> [-LogDir <dir>]
# Settings: scratch/worldbuilder/wb-settings.json (git-ignored; LaunchMode "Creator"). Log: <LogDir>/<Name>.log;
# the Windows PID goes to <LogDir>/wb-client.winpid - stop ONLY that PID (the owner may have a client open).
# Refuses to start while any MSUIClient runs. Finished when the log shows "[wbscript] quit".
param(
    [Parameter(Mandatory=$true)][string]$Script,
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$LogDir = "",
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Exe) { $Exe = Join-Path $repo "MSUIClient\bin\Debug\net8.0\MSUIClient.exe" }
$sp = if ($LogDir) { $LogDir } else { Join-Path $repo "scratch\worldbuilder\logs" }
New-Item -ItemType Directory -Force $sp | Out-Null
if (Get-Process -Name MSUIClient -ErrorAction SilentlyContinue) { Write-Output "ABORT: an MSUIClient is already running"; exit 2 }
$env:MSUI_SETTINGS_PATH = Join-Path $repo "scratch\worldbuilder\wb-settings.json"
$env:MSUI_WB_SCRIPT = (Resolve-Path $Script).Path
$p = Start-Process -FilePath $Exe -WindowStyle Hidden -WorkingDirectory (Join-Path $repo "MSUIClient") `
    -RedirectStandardOutput (Join-Path $sp "$Name.log") -RedirectStandardError (Join-Path $sp "$Name.err") -PassThru
Set-Content -Path (Join-Path $sp "wb-client.winpid") -Value $p.Id -Encoding ascii
Write-Output "launched PID $($p.Id); log $(Join-Path $sp "$Name.log")"
