# Quest Tabletop scripted capture (shared_docs/QUEST_TABLETOP.md §10): boots MSUIClient into the offline Creator world
# with MSUI_TABLETOP_PROBE and saves dumps/gameplay-tabletop-<area>-overview/-close screenshots. No login.
#   powershell -File tools/tabletop/launch-tabletop.ps1 [-Zones "40,7001"] [-Name tabletop-p1] [-Patch7 <file>] [-Visible]
# Settings: scratch/tabletop/tabletop-settings.json (git-ignored; seeded from the World Builder's Creator settings).
# Log: scratch/tabletop/logs/<Name>.log; finished when it shows "[tabletop-probe] VERDICT". Refuses to start while any
# MSUIClient runs; runs hidden (no window, no focus) unless -Visible.
param(
    [string]$Zones = "40,7001",
    [string]$Name = "tabletop",
    [string]$Exe = "",
    [string]$Patch7 = "",
    [switch]$Visible,
    # -Edit: after the Militia stage on Westfall, run the P5 edit-on-the-board probe (writes only the QA pack on the web app).
    [switch]$Edit
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Exe) { $Exe = Join-Path $repo "MSUIClient\bin\Debug\net8.0\MSUIClient.exe" }
$dir = Join-Path $repo "scratch\tabletop"
$logs = Join-Path $dir "logs"
New-Item -ItemType Directory -Force $logs | Out-Null
if (Get-Process -Name MSUIClient -ErrorAction SilentlyContinue) { Write-Output "ABORT: an MSUIClient is already running"; exit 2 }
$settings = Join-Path $dir "tabletop-settings.json"
if (-not (Test-Path $settings)) { Copy-Item (Join-Path $repo "scratch\worldbuilder\wb-settings.json") $settings }
$env:MSUI_SETTINGS_PATH = $settings
# Never sync World Content Packs into the shared GameData: this client talks to another web app than the owner's
# (2026-10-14: a QA run swapped the owner's patch-7 build #59 for WSL's #4 and his next launch re-synced everything).
$env:MSUI_WORLDPACK_SYNC = "off"
$env:MSUI_TABLETOP_PROBE = $Zones
$env:MSUI_TABLETOP_EDIT = $(if ($Edit) { "1" } else { "" })
# -Patch7 <file>: the probe mounts that patch-7.MPQ in its own process (e.g. a published build saved to scratch).
$env:MSUI_TABLETOP_PATCH7 = $(if ($Patch7) { (Resolve-Path $Patch7).Path } else { "" })
$env:MSUI_WB_BACKGROUND = $(if ($Visible) { "0" } else { "1" })
$p = Start-Process -FilePath $Exe -WindowStyle Hidden -WorkingDirectory (Join-Path $repo "MSUIClient") `
    -RedirectStandardOutput (Join-Path $logs "$Name.log") -RedirectStandardError (Join-Path $logs "$Name.err") -PassThru
Set-Content -Path (Join-Path $dir "tabletop-client.winpid") -Value $p.Id -Encoding ascii
Write-Output "launched PID $($p.Id); log $(Join-Path $logs "$Name.log")"
