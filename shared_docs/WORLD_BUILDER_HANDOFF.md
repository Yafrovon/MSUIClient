# World Builder / Gilneas - handoff (updated 2026-09-28: human editor pass)

**Human editor pass verified.** World now has task navigation, explicit start/cancel, corrected sculpt persistence,
visible/selectable offline NPCs, scoped editing of existing stock spawns, and data-preserving NPC/quest forms.
Usage and current import limits are in WORLD_BUILDER.md §4. Dungeon work remains deferred.

**Current publication is build #39: 0 verifier errors, 6 existing warnings.** The temporary `human-tools-qa`
pack (ID 4) is disabled. Original packs 1-3 remain enabled and unchanged. Build #38 published a real mouse-created
stroke, a Godric vendor edit and a quest; build #39 restored the original world. The test draft remains available
for evidence and is not active content. Placement 16 and extra NPC spawn 1500155 were removed/undone; no patrol
was saved. Do not enable this QA pack as part of ordinary release work.

- Sculpt stayed exactly 9.50068 yd after build #38 download and returned to 6.99955 yd on mounted build #39. Lower,
  Smooth and Flatten then changed real preview terrain in the expected direction; Escape while holding the
  mouse restored every sample exactly and left saved terrain unchanged. Smooth now samples across tile edges;
  a planar-slope edge/corner regression guards against introducing creases.
- Live vendor protocol: 40 steps, 0 failures. Godric's original spawn 79952 used clone 7000313 with the edited name
  and all 8 original shop items; Escape closed the merchant without opening the menu. Published data comparison
  passed 29/29. Restoration passed 23/23: exact original spawn/template/equipment/services, clone absent from
  live data, disabled draft retained and every other pack unchanged.
- Actual pointer QA covered placement, move cancellation, Undo/removal, NPC selection/edit/reopen, NPC placement
  and Undo, patrol cancellation, quest creation/reopen/edit, checks/publication/download. Round4 and final brush
  assertions pass. The optional final NPC probe expected stock1213 while pack3 was selected and hit the intended
  ownership guard for the retained pack4 draft; this is not counted as a passing assertion. Guidance now names
  the owning pack and its draft-only state. Earlier exploratory camera/scroll failures remain in their logs.
  The corrected ownership probe passed with zero failures: explicit selection of the named pack reopened
  clone 7000313, and obsolete guidance cleared. Log: `logs/human-tools-npc-ownership-final.log`.

Normal Debug/Release outputs include the final Smooth correction and readable numeric fields. WorldBuilder,
ImGui policy and shared-docs checks pass; web WorldPack/GradedPath 121 tests and NPC authoring 19 checks pass.
The empty surface layer reproduces 31 ADTs byte-for-byte; the brush proof matches 67 outer and 76 inner vertices
within 0.00000114 yd. Evidence under `scratch/worldbuilder/`: `build{38,39}-human-status.json`,
`logs/human-tools-{round4,final,vendor-published}.log`, `surface-{zero,stroke}-proof-comparison.json`, and
`npc-roundtrip/*-report.json`. Build #39 mounted SHA1: `7997afbb08acc40dde83ae5069c91d15f3418568`.

Final World Builder web deploy was 01:42:35 local, backup `auto-20260928T014203`, including the stock quest-objective
import guard. The concurrent Forge task owns patch-4 and its subsequent deployments; preserve its work and
coordinate client use/restarts with task `01a0e651-7324-77d2-9882-88ae61adcfa1`. Patch-4 remained unchanged during
this round. No commits, pushes, branches, direct SQL writes or worldstate restores were performed.
All World Builder QA clients exited normally with injected input released. No WorldPack build is running.

**Previous completed work: the continent map and actual terrain correction were published and verified on #37.**
Gilneas now fits Blizzard's painted peninsula below Silverpine, with the continent art intact, an open bay,
and its own real hover glow/name. Towns/camps moved with the land and the fixed Greymane entrance is walkable.
**Build #37: 0 verifier errors, 6 existing warnings. Final live run: 96 steps, 0 failures.**
The earlier seven-leg gate walk, four settlement checks and 25 map probes passed; the final ten-leg dock round
trip also passes without teleporting between shore and deck. Both exterior water leaks are absent, the interior
pond remains, and the zone mosaic and actual hover render correctly. All 79 relevant tests pass.

Gilnwar is alive, grounded and stationary in Duskhaven (-2525,2058.333,17.167658), with GM mode and injected
pointer input off. His existing party is preserved. Both QA clients exited; no build or QA process remains running.
Nothing is committed. Final evidence: `scratch/worldbuilder/build37-{status,verify}.json`,
`logs/mapfix-tier2-build37.log`, `logs/dock-recheck-build37.log`, and `gilneas-hover-build37.png`.

This page is the CURRENT state and how to continue - any agent (or a human) should be able to pick it up cold.
Design and law: `shared_docs/WORLD_BUILDER.md` (§7 verifier checks, §8 dated as-built history, §9 the playbook for
building a zone). History lives there, not here.

## 0. Rules that apply to everything below

- **The box is a test environment** (AGENTS.md "This is a TEST environment"). Create, level, gear, teleport, kill and
  revive characters and bots as a test needs - no permission needed. Still off-limits: commits/pushes/branches (the
  owner does git), direct SQL writes, worldstate restores, the unrelated CMaNGOS server.
- **Bots are SuperUI bots, never stock vmangos `.partybot`s** (not part of this project). Make a group with
  `tools/worldpack/make-group.py`. Leave the raid roster (Testwar + 39 bots) to raid work.
- **Every capability has two faces**: a Creator Mode panel for a human AND a script command / generator for an agent
  (simple enough for a small local model), plus a CHECK that proves the result. Nothing built counts until the
  verifier passes it (tiers 1-3, WORLD_BUILDER.md §7).
- **Don't redo work**: set a tester up ONCE (`--setup`), then every run starts where the character stands.
- **One live client at a time**; the launch scripts refuse to start otherwise and record the PID they started.
  Before stopping a recorded PID, verify its process name and start time: Windows may reuse it after client exit.

## 1. Where Gilneas stands

| part | state | proof |
|---|---|---|
| Land | Gilneas moved one tile west and two north, fits the painted polygon south of Silverpine, and has a recut walk-in from the fixed Greymane gate | build #37 G17; seven continuous live walk legs and zone 7001 assertion pass on the same terrain |
| Towns + services | Existing towns/services relocated with the land; coastal harbor and camp rehomed; NPC floors verified | no Gilneas tier-2 errors/warnings; four settlement checks/walks and all ten dock round-trip legs pass |
| Quests | 13 (12 open world + Lord Godfrey); camps +2 spawns each (7/8 per camp) | all 12 open-world quests turned in with EARNED credit by Gilnwar (level 42) before the camps grew; the bigger camps are NOT re-run yet |
| Dungeon | **Deferred.** Greymane Fortress (map 801), its existing party and encounter content are outside the current map test | historical SuperUI run: 11/11 pulls, all three bosses, 0 deaths |
| Holes | Obsolete coastal holes healed; no Gilneas walk-off failures in actual client collision | 111 targets checked; focused six-target recheck after harbor NPC correction; only existing Northshire warning remains |
| Minimaps | Regenerated from final ground/water; zone mosaic draws 35 tiles and was visually reviewed | build #37 G11 + live mosaic; inherited stock stripe/dark-water art is noted below |
| **Continent world map** | **Function verified live; visual refinement requested.** Painted continent preserved; hover/name work and bay stays open. Owner wants a subtle filled highlight without the bright border; that styling is still pending. | G16; 25 ownership/name/GL-highlight probes pass; final `scratch/worldbuilder/gilneas-hover-build37.png` |
| Authoring data | Nine migration requests, NPC correction 139, dock Moves 140/141 and final ramp Move 142 succeeded through audited APIs | frozen `map-fit-plan*`, `dock-fit*`, `dock-bevel-fit*` plans/journals; final `docs-build37.json` / `state-build37.json`; never replay the original migration |
| Publish | **Build #37 succeeded: 0 errors, 6 existing placement warnings.** G9 36 tiles / 0 cracks / 0 normal drift; G17 883,498 terrain samples and 308,772 exterior water cells / 0 failures; all 72 overworld combatants walkable | `scratch/worldbuilder/build37-status.json`, `build37-verify.json`; client SHA1 `feaaea5693f748f0c92664d9f6153385f59b24c5` |

Test characters: **Gilnwar** (Human Warrior 42, account `nico`, fair-tester setup done, NOT in the raid) with his
SuperUI group **Lornbear** (warrior tank), **Wolfpants** (priest healer), **Luckytrap** (mage), **Galenward** (rogue),
all level 42, geared, in his party.
The map smoke does not add/remove bots or change their party. It starts with Gilnwar alive, travels his body
alone to map 0, and finishes in Duskhaven with GM mode and injected hover input off.

## 2. How to do things (copy-paste; run from the MSUIClient repo root)

| task | command |
|---|---|
| World Builder script (edits, publish, tiers 1+2) | `powershell -File tools/worldpack/launch-wb.ps1 -Script <script.txt> -Name <name>` - done when the log shows `[wbscript] quit` |
| Tiers 1+2 script for a pack | `python tools/worldpack/gen-wb-verify.py OUT.txt --pack gilneas [--publish] [--fix]` |
| Tier 3 live protocol | `python tools/worldpack/gen-live.py OUT.txt --pack gilneas --char Gilnwar --fair 42 --sections quests,patrols,dungeon --bots Lornbear,Wolfpants,Luckytrap,Galenward` then `powershell -File tools/worldpack/launch-live.ps1 -Protocol OUT.txt -Name <name>` - result `[live-run] PROTOCOL_DONE failures=N` |
| First time for a NEW tester / group | add `--setup` once (levels, trains, reset items + gear, weapon skills, invites the bots) |
| New SuperUI group | `python tools/worldpack/make-group.py` (warrior tank, priest healer, mage, rogue; `--horde`, or `race:class` pairs) - prints the `--bots` argument |
| Plan spawns / packs / patrols / boss rooms | `python tools/worldpack/navmesh.py --map 801 check '[["boss",32,328]]'` (MAIN = reachable, open6 = room to fight) and `... draw OUT.png xN xS yW yE [ppy] [marks]` |
| Script commands for content | `mobpack <entry[,entry]> <count> <x> <y> <spread>`, `patrol <leader> <follower> <n> x1 y1 x2 y2 ...`, `movespawn <guid> <x> <y> [facing]`, `spawnring <entry> <x> <y> <r> <n> <wander>`, `content <file.json>` (body null = delete) |
| Re-run tier 1 without publishing | `curl http://192.168.0.2:5000/WorldPacks/Verify` (full) / `.../Preflight` (seconds, unpublished docs) |
| Deploy the web app | `powershell -File tools/deploy-webapp.ps1` in the MangosSuperUI repo (refuses during a build; auto-rollback) |
| Build the client | `dotnet build MSUIClient/MSUIClient.csproj -c Debug` and `-c Release`; checks: `dotnet run --project tools/interface-wire-check -p:OutDir=MSUIClient/bin/WbCheck/ -- --world-builder-only` |
| Web app tests | `dotnet test MangosSuperUI.Tests --filter "FullyQualifiedName~WorldPack\|FullyQualifiedName~GradedPath"`: 79 passed, including 13 `WorldPackWorldMapTests`. Client Debug/Release and world-builder/ImGui policy wire checks passed. |
| Where the land sits on the painted continent map | `python tools/worldpack/worldmap-probe.py OUT.png [--cols 27-30 --rows 35-39 --crop x0,y0,x1,y1 --scale 4]` - art + published land in red + ADT grid; stdout = WorldMapArea rows and the ZMP area ids |
| Reproduce traced outline | `python tools/worldpack/worldmap-outline.py OUT.json --input tools/worldpack/examples/gilneas/worldmap-outline.json --preview OUT.png` - stock painted art and explicit world polygon, no flood fill |
| Offline migration plan | `python tools/worldpack/fit-gilneas.py PLAN.json --docs BEFORE-docs.json --state BEFORE-state.json` - plan/expected snapshots only; `--apply-existing` applies a reviewed plan through audited APIs after checking its original state. **Already applied this round: do not apply again.** |
| Map-only verifier | `python tools/worldpack/gen-wb-verify.py OUT.txt --pack gilneas --maps 0` (or `scratch/worldbuilder/mapfix-tier2.txt`, which downloads first); launch with `launch-wb.ps1` |
| Map-only live ownership QA | `python tools/worldpack/gen-live.py OUT.txt --pack gilneas --maps 0 --sections worldmap` - inside, outside and unowned-sea expectations from authored polygon + stock ownership |
| Map smoke evidence | `scratch/worldbuilder/map-smoke-build34.txt`, log `scratch/worldbuilder/logs/map-smoke-build34.log`: seven gate legs, four settlements, coast physics and 25 map probes pass; two dock entry failures prompted the scoped correction |
| Final dock / zone-map check | `powershell -File tools/worldpack/launch-live.ps1 -Protocol scratch/worldbuilder/dock-recheck.txt -Name dock-recheck -Character Gilnwar` **after publication/download/tier 2**; ten continuous walk legs with radius 0.5, shore/ramp/deck/return grounding, zone mosaic and actual continent hover screenshots |
| Regenerate smoke offline | `python tools/worldpack/gen-map-smoke.py OUT.txt --docs DOCS.json --state STATE.json --map 0 '--dock-route=-1468,2588,6\|-1468,2592,5.948\|-1468,2595,4.753\|-1468,2598,3.558\|-1468,2605,3.059\|-1468,2615,3.059'` (the backslashes only escape table pipes in Markdown; pass plain pipes in the quoted argument). Use the focused dock script for strict two-way join acceptance. |
| Minimap preview without publishing | `dotnet test MangosSuperUI.Tests --filter FullyQualifiedName~WorldPackMinimapProbe` with `MSUI_MINIMAP_OUT=<dir>` - before/after PNGs per pack tile |

Git-ignored machine files the scripts use: `scratch/worldbuilder/live-config.json` (account config with the
password - never copy into a tracked file), `live-settings.json`, `wb-settings.json`. Logs go to
`scratch/worldbuilder/logs/` unless `-LogDir` is given. Box access: AGENTS.local.md.
Both runners request client exit at `[wbscript] quit` / `[live-run] PROTOCOL_DONE`; wait for that PID to exit
before launching the next one. On this machine Python is bundled at
`C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`.

## 3. Map acceptance and deferred work

1. **Map/terrain correction completed and verified on build #37.** Owner, 2026-09-27: generate the piece
   so it "naturally fits the map", it "can't choke the bay", and "each map gets its own hover highlight and name up
   top". Then the key point: *"isn't it... already there? literally the map piece that just doesn't have a highlight
   below Silverpine?"* - **yes.** Blizzard's painted Eastern Kingdoms art already has a landmass below Silverpine
   with no zone behind it. Gilneas must BE that piece.

   Original rejection evidence (build #32; `python tools/worldpack/worldmap-probe.py OUT.png` shows the painted continent with
   the published Gilneas land in red, the ADT grid, and the hover map's area ids; delete the PNG after reading):
   - **How vanilla does it**: continent art `Interface\WorldMap\Azeroth\Azeroth1..12.blp` (4x3 tiles, 1002x668
     visible). Hover ownership = `Interface\WorldMap\Azeroth.zmp`, 128x128 uint32 AreaTable ids in WORLD coordinates
     (one cell = half an ADT tile; tile (c, r) = cells (2c..2c+1, 2r..2r+1), index = column + row*128). The glow =
     `Interface\WorldMap\{Dir}\{Dir}Highlight.blp` (additive RGB on black, 128 wide, pow2 height) placed by the zone's
     WorldMapArea bounds. The name up top = AreaTable name of the hovered id. Client code: hover
     `_worldMapHits.TryResolveArea` + `DrawWorldMapAreaHighlight` (`GameLoop.WorldMap.cs` ~181-188), formats in
     `Formats/WorldMapOverlayCatalog.cs` (`WorldMapZoneMap`, `WorldMapHighlightInfo`, `WorldMapHighlightMask`).
   - **The painted piece** covers roughly ADT cols 27.2-30.0 x rows 34.0-37.7 (its north edge is the border with
     Silverpine's green, pale blue-grey coast wash around it). The ZMP says The Great Sea there (areas 2397/2365),
     so no hover, no glow, no name. East of col ~30 and south of row ~37.8 is the painted, hatched water of Baradin
     Bay's western approach (ZMP Baradin Bay 298 sits at cols 32-33 x rows 37-40).
   - **Build #32's land did not match it**: published Gilneas spanned cols ~27.3-30.8 x rows 35.0-39.8 - about one column too
     far east (into the bay) and two rows too far south (it almost reaches the southern landmass at row 40). That
     is the "choked bay".

   Implemented this round:
   - The continent rectangle and `WorldMapStampedTiles` helper are removed; the zone mosaic remains.
   - `worldmap-outline.py` reproduces the explicit traced polygon in `examples/gilneas/worldmap-outline.json`
     and its painted-art review sheet. Flood fill is no longer used.
   - `fit-gilneas.py` moved the authored block one tile west/two north, kept the Greymane approach fixed,
     rehomed the harbor/camp and content, and wrote the new pass. Nine audited API requests succeeded.
   - `WorldPackCoast` fits actual terrain to that polygon, restores exterior seabed/water/areas from baseline,
     clears obsolete coastal props/holes, and preserves unchanged normals. Final offline proof: G9 31 tiles,
     0 cracks and 0 normal drift; G17 883,498 outer/inner samples, 0 new dry exterior and 0 submerged interior.
   - `WorldPackWorldMap` generates ZMP cells, RGB/additive highlight and tight aspect-correct WorldMapArea
     from `worldmap {map,area,directory,polygon,terrain?}`. G16 checks assets, alignment and untouched ownership.
     The client uses its ordinary mounted ZMP/highlight catalogs. Focused map tests: 13 passed.

   Completed acceptance:
   - Builds #33 and #34 published successfully. Build #34 has 0 errors and 6 existing placement warnings;
     G9 checks 36 tiles without cracks/normal drift, G17 checks 883,498 vertices, G15 places all 72 map-0
     combatants on walkable navigation, and G16 checks the real emitted hover assets.
   - Tier 2 checked 111 actual client collision targets. The harbor NPC 1500106 warning was corrected to Z6
     by audited op 139; the six-target recheck has 0 errors and only the unrelated existing Northshire hole warning.
   - The full build-34 live run passed all seven continuous Greymane legs, four settlement walks/grounding,
     west/east/south coast checks and 25 mounted map ownership/name/highlight probes. Real painted-continent and
     Gilneas hover screenshots were inspected; the bay remains open. Evidence: `logs/map-smoke-build34.log`,
     `continent-build34.png`, `gilneas-hover-build34.png` under `scratch/worldbuilder/`.
   - That run counted **two failures**, both attempts to enter the same dock. The old geometry helper selected
     an underside face. Upward collision surfaces now identify the real deck; sloped surfaces use an oriented
     plane and actual height span. Six regressions were added (76 relevant tests total).

   Final acceptance completed:
   - The #36 high-end bevel failure was fixed by Move 142. Platform 8 remains Z=-2.86286 (deck 3.059).
     Ramp 12 is (-1468,2595.84,2.93218), rotY=180, rotZ=2, scale=0.4. Its 19.714-degree surface has both
     end bevels buried beneath beach/deck; 5,084 sampled blocking-face points and independent transforms agree.
   - Build #37's changed-only collision scan: 6 targets / 0 errors; five harbor targets clean, only the existing
     Northshire hole warning. Client patch hash matches. The final live run has **0 failures over 96 steps**:
     ten continuous forward/return dock legs at radius 0.5, actual grounding, both exterior water cells dry,
     interior pond Z32.930923 retained, zone mosaic, mounted name/highlight and real pointer-driven hover.
   - Final screenshots `dock-forward-build37.png`, `dock-return-build37.png`, `gilneas-hover-build37.png`
     were inspected. Gilnwar returned to Duskhaven, GM/pointer off, party preserved; both QA clients exited.
   - The earlier #36 run's 11 failures remain recorded: nine cascading dock legs plus two standing assertions
     on a stock-steep observation point. The final protocol reads that second liquid cell from the first dry point.
2. **Later: painted-style Gilneas zone map** (`Gilneas1..12.blp`) instead of the generated minimap mosaic.
   This is distinct from the corrected painted continent map. The mosaic retains a stock green stripe on 26,36
   and dark sea art on 31,34-35 (byte-identical to `texture.MPQ`, absent from patch7); provenance is in
   `scratch/worldbuilder/minimap-art-review/`. Offshore dock space retains stock Great Sea/Hillsbrad area ownership;
   the shore is Keel Harbor/Gilneas. No new land is present outside the fitted coast. Build #32 images are historical.
3. **Deferred: re-run the six grown camps** with Gilnwar (no `--setup`):
   `gen-live.py OUT --pack gilneas --char Gilnwar --fair 42 --sections quests --quests 7000101,7000102,7000103,7000104,7000106,7000107`
   - but first remove the bots from his party (a solo test; kills in a party are shared) or accept a group run.
   The four bots were left inside map 801.
4. **Deferred: dungeon difficulty is the owner's call**: with the level-42 SuperUI group nothing came close to a wipe
   (pulls 10-41 s, bosses 44-93 s, 0 deaths). Numbers are in WORLD_BUILDER.md §8.
5. Known baseline warnings remain outside this correction: the published verifier reports six placement warnings,
   the map-0 offline probe reports three, and tier 2 reports the pre-existing Northshire hole. Do not conflate these
   different verification scopes or change unrelated content to clear their counts.

## 4. Uncommitted work (for the owner's review; nothing is committed)

- **MangosSuperUI**: `Services/WorldPacks/*` (build, audit/verifier incl. G15 `WorldPackNavMesh`, `WorldPackMinimap`,
  relocation, paths), `Controllers/WorldPacksController.cs`, `Scripts/deploy-auto.sh`, `MangosSuperUI.Tests/WorldPack*`.
- **MSUIClient**: `GameLoop/CreatorMode/GameLoop.Creator.WorldBuilder*.cs`, `Engine/UI/WorldBuilderLaw.cs`,
  `Net/WorldPack*.cs`, `GameLoop/Dev/GameLoop.LiveRun*.cs` (pack-trial, patrol-watch/at, boss-trial pull, kill-for-quest),
  `tools/worldpack/*` (generators, navmesh planner, make-group, launch scripts, `worldmap-probe.py`), `tools/interface-wire-check/
  WorldBuilderClinicalChecks.cs`, `shared_docs/WORLD_BUILDER*.md`, AGENTS.md (test-environment section).
- **Core (box tree)**: no World Builder changes. The 2026-09-27 PartyBotAI patches were reverted to HEAD; mangosd
  80d5d2bc runs. The tree still carries the owner's own uncommitted SuperUI raid work (not ours).
