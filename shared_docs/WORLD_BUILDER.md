# World Builder — terrain, placement and optional World Content Packs

Owner mission 2026-09-26 (Nico): move the web app's prototype World Editor into client
Creator Mode as a real authoring pipeline; place content in Northshire and the Barrens,
regenerate everything the server needs, ship the client MPQ, prove it live; then build an
unreleased 1.12 zone (buildings, NPCs, vendors, a real instanced dungeon) with purpose-built
NPC and dungeon tools. **New content is OPTIONAL**: it lives in toggleable World Content Packs so
nobody else running SuperUI is forced to use it.

This page is the design law and the as-built record. Append dated notes; do not rewrite history.

## 1. Why the prototype had bad mob pathing (web app, as found 2026-09-26)

The browser World Editor (`MangosSuperUI/Controllers/WorldEditorController*.cs`,
`Services/ServerDataService.cs`) is kept for reference but is superseded by this pipeline:

- mmaps rebuilt for ONE tile; neighbours never — navmesh connectivity at tile borders went stale.
- Only the affected `.vmtile` was copied next to a freshly assembled `.vmtree`: vmtree and vmtiles
  of one map are index-coupled, so mixing generations is unsafe. Copy a map's vmaps as one set.
- Sculpted `.map` written with area/liquid/hole offsets = 0 (tile lost water, area ids, holes), and
  written to a directory MoveMapGenerator did not read — navmesh and server heights disagreed.
- `patch-Z.MPQ` held one ADT of one preset (every commit dropped the others) and was a LETTER
  patch: `MapExtractor` reads a fixed archive list and `VMapExtractor` only numbered patches, so
  the extractors never saw the edit.
- No audit rows, no server-side undo, WMO only (no M2), placement scale silently ignored.

## 2. Shape of the new pipeline

```
Client Creator Mode (World Builder panel)            MangosSuperUI (/WorldPacks/*)              Box
  sculpt / place / delete / move  ── op JSON ──▶  wp_op (before/after) + audit_log row
  Ctrl+Z                           ── Undo    ──▶  inverse op, audited
  Publish                          ── Publish ──▶  build job:
                                                     1. patch ADTs/WDTs/DBCs of ENABLED packs  ─▶ patch-7.MPQ
                                                     2. extract workspace (~/worldpacks/work) reads vanilla + patch-7
                                                        MapExtractor -m <maps>  → maps/*.map
                                                        VMapExtractor -m <maps> → Buildings → VMapAssembler → vmaps
                                                        MoveMapGenerator <map> --tile (touched tiles + 8 neighbours;
                                                        whole map for new maps)
                                                     3. install into run/data with a baseline of every file first
                                                        overwritten; files of disabled packs restored from baseline
                                                     4. restart mangosd (scoped), verify binary/process
  poll /WorldPacks/Status  ◀────────────────────────
  download patch-7.MPQ, hot-mount (MpqMount.AddArchive), rebuild terrain/collision
```

Rules:

1. **The client never writes the server.** Every edit is a POST to the web app, which stores the
   op with before/after state and writes `vmangos_admin.audit_log` (category `worldpack`).
2. **Everything is an op.** `wp_op` rows are the source of truth; the live tables
   (`wp_sculpt`, `wp_placement`, …) are the fold of the ops. Undo = apply the inverse op (itself
   audited). Undo never deletes history.
3. **Packs are optional.** A pack is enabled or disabled server-wide. Publish builds from the
   ENABLED packs only. Disabling a pack and publishing restores vanilla files from the baseline and
   removes the pack's DB rows. A client without `patch-7.MPQ` sees the vanilla world.
4. **One archive, numbered.** `patch-7.MPQ` = union of enabled packs. Numbered so both the client
   and the extractors read it; above patch-3 (spells) and patch-4 (unified retexture).
5. **Extract, don't hand-write.** Server `.map`/vmaps/mmaps come from the real extractors run over
   vanilla + patch-7, never from hand-written `.map` or `dir_bin` records.
6. **A map's vmaps travel as a set** (vmtree + all vmtiles + referenced models).
7. **mmaps: touched tiles and their 8 neighbours**, and the `.mmap` params must match the installed
   ones byte-for-byte for existing maps (the build fails loudly otherwise).
8. **Baseline before overwrite.** The first time a run/data file is replaced its original goes to
   `~/worldpacks/baseline/`; the installed-file manifest says which files a publish owns.

## 3. Data model (vmangos_admin)

- `wp_pack(id, pack_key, name, description, enabled, created_at, updated_at)`.
- `wp_op(id, pack_id, kind, target, before_json, after_json, created_at, operator, operator_ip,
  undone_by, audit_id)` — kind ∈ sculpt, place, move, delete, map, npc, …
- `wp_sculpt(pack_id, map_id, tile_x, tile_y, vertex_index, height_delta)` — V9 129×129 per ADT
  (`tile_x` = ADT file column = world-grid X of `{map}_{x}_{y}.adt`).
- `wp_sculpt_surface` — the same sparse grid, applied to the finished terrain after source sculpt,
  stitching, coast shaping and paths. New human/script strokes send `surface: true`; old requests and
  old undo payloads retain source-layer semantics. `State.surfaceSculptSupported` gates the new client.
- `wp_placement(id, pack_id, map_id, kind wmo|m2, model_path, pos_x, pos_y, pos_z (WORLD coords),
  rot_x, rot_y, rot_z (degrees), scale (m2 only), doodad_set, deleted)`; MODF/MDDF uniqueId =
  `7_000_000 + id`.
- `wp_build(id, status, started_at, finished_at, log, mpq_sha1, mpq_size)`.

Coordinates are WoW WORLD coordinates everywhere on the wire (X north, Y west, Z up). The
conversion to ADT placement space happens once, in the web app's ADT writer.

## 4. Client (Creator Mode → World Builder)

- Creator layout (2026-10-14, owner's choice): the classic top buttons stay; the open panel docks to the LEFT edge as a
  full-height sidebar that scrolls inside itself (`GameLoop.Creator.Sidebar.cs`, `Settings.Creator.Sidebar`, default on;
  UI options can switch back to floating windows). Right edge drags the width (remembered per panel), the chevron folds
  it to a tab. Look = the web app's Quest Atlas (`MangosSuperUI/wwwroot/css/questatlas.css`): warm dark glass, hairline
  borders, cream ink, one gold accent; flat buttons replace the red panel-button art inside Creator windows.
- Panel `CreatorPanel.World` has task pages: Terrain, Paths, Buildings, Objects, NPCs, Quests, Publish and More,
  shown as wrapping page chips (not tabs). The pack selector, Fly camera / Grid / Snap, active action and save status
  stay above the scrollable form.
- Shared building blocks (`GameLoop.Creator.WorldBuilder.Common.cs`) - every page uses them, no page asks for raw
  coordinates: **pick tool** (`WbPickButton`: the button arms a one-shot ground click with a ring under the cursor;
  Escape cancels), **world markers** (`WbMarker`, `WbTileOutline`: what a form refers to is drawn in the world with a
  label), **confirm dialog** (`WbConfirmButton` + one top-level `WbDrawConfirmDialog`: every delete/remove/move-region/
  publish asks first and says that Undo restores it), **Undo** (`WbUndoButton`: "Undid: <label>").
- Sculpt: raise/lower/smooth/flatten brushes on the V9 grid; the live preview mutates the cached
  ADT heights, rebuilds that tile's mesh and height grid; strokes are sent as ops on mouse-up.
- Place: WMO/M2 catalogue from the MPQ listfiles; ghost via `AddDynamic`; click to place/move,
  explicit rotation/height/scale fields and remove; Ctrl+Z = server undo of the last pack op.
- Publish: POST, poll status, download `patch-7.MPQ` into `GameData\Data`, hot-mount.

### Human workflow

Open **Creator Mode → World**. Choose a pack at the top, or use **Packs...** to create one.
Pack drafts are separate from the running world. The pack's Enabled setting chooses whether it is included
in the next server-wide publish; disabling it also needs a publish to restore the previous world.

| Task | How to use it |
|---|---|
| Terrain | Numbered steps. Choose Raise, Lower, Smooth or Flatten, then **Start sculpting**. Drag on the ground; releasing saves one stroke. Shift reverses Raise/Lower; brackets resize the brush. Flatten takes the stroke's starting height, or **Pick the level height** on the ground (then **Use stroke start** to go back). The readiness message explains missing terrain or server support; the last step links to Publish (step 3: Download). |
| Paths | Optional **Fly camera** (panel top): the camera detaches, rises above trees and looks down at the World Builder's own **View angle**; W A S D fly, Space/Caps Lock up/down, right mouse turns; the pointer stays free and clicks go to the tool (no box-selection). **Ground grid** shows the terrain's cells, chunk borders, the picked cell and tile/chunk/cell/height; **Snap to grid** puts points on cell corners. The page is four steps: **1. Draw the route** (Start drawing, click points; drag moves, Ctrl+click inserts, Delete removes); **2. Road surface** (tick Paint a road surface, choose one of the game's own `Tileset` textures from the thumbnail picker - search, zone filter, quick words - set width, soft edge, strength; the road is painted on the loaded ground immediately); **3. Shape the ground** (optional Level the ground: graded pass/ramp with height preview, Even out the slope, point heights); **4. Name and save**. Saved paths reopen from the list. |
| Buildings | Choose Buildings or Props and small objects, search (or click a quick word: crate, barrel, tree...) and choose **Place selected model**. Click the ground; brackets rotate the preview. Random heading / Random size re-roll after each click, and the cursor preview already shows the next roll, for scattering props; several placements may save at once. **Edit placed objects**: heading, north/south and west/east position, height (**Put on the ground**), tilt and scale, previewed live before **Save object changes** (exact numbers sit under **Exact position**); **Level the ground under it** (a small terrain pad, one stroke), **Move** (keeps its lift above the ground), **Duplicate** (a copy armed to move), **Go to**, **Remove** (confirm). Selecting another object with unsaved edits asks Discard / Keep editing. List rows show model names, not ids. |
| Objects | Server objects players click or loot: chests, herb/ore nodes, campfires, chairs, mailboxes, signs, doors. Pick a Kind, search, select, **Place selected object**, click the ground (keep clicking to place more; brackets or the **Facing** slider turn it; respawn seconds). Objects with no model cannot be placed (the reason is shown). **Edit placed server objects** lists this pack's objects (or **Select an object in the world**): facing, height, respawn, Move, Go to, Remove (confirm); unsaved edits preview live. **Custom chests** (steps 1-4): choose a look from the game's chest models, name it, money as gold/silver/copper, item loot (chance, min/max), **Create chest** (or **Save chest changes**); **Your chests** reopens, edits or deletes them. Saved objects draw as model previews until the live server shows the published object. |
| Existing town NPC | Open **NPCs** and click its visible model, or use **Find / change NPC**. Edit name/title, appearance, services or equipment/greeting. **Save this NPC in pack** makes a version for that spawn only; later **Save NPC changes** edits that same version. The stock shared template stays intact. A disabled Save button says why. **Delete this NPC** / **Restore the original NPC** (confirm) also remove its placements and patrols. Switching NPCs with unsaved edits asks first. |
| New NPC / placer | **New NPC**, name/look/roles, then **Create NPC**: the page switches to **Place NPCs** with placing already armed and a see-through copy of the NPC under the cursor - click the ground. Place NPCs is three steps: 1. who and how (**One NPC / A group that fights together / A patrol route**), 2. **Facing** slider and Start placing / Start route, 3. click the ground (patrols: click points, **Save patrol**, at least two points). Clicks that cannot place say why. **Placed NPCs in this pack** lists each spawn: Go to, Edit this NPC, Move it (**Pick a new spot**, **Face toward...**), Remove placement (confirm). Saved patrols draw as lines. |
| Quests | Select or create a quest; fill Story and level, Quest giver and return (stock NPCs searchable by name as well as pack NPCs), Objectives and optional Rewards/chain. Pick NPCs, items and objects (placed pack objects included) by name. **Discover a place** objectives: **Pick the place on the ground**, an **Area size** slider and a ring marker, **Go there**. Save, read the quest's check results and reopen it to continue editing; **Delete this quest** (confirm). Unsaved quest drafts survive switching quests/packs within the session. |
| Publish | Save open forms, check the listed enabled packs, choose **1. Check drafts**, then **2. Publish** - a confirm lists the packs that go in and warns that the game server restarts; busy buttons say why they wait. After success choose **3. Download build** to update this client's terrain and collision. |
| Larger areas | **More** contains Maps and dungeons, Regions and paths, Check world content and Pack data (deletes confirm). **Name a place within your map**: **Pick the centre on the ground**, ring marker, **Save named place**. **Connect two places with a portal**: name, **1. Pick the arrival on the ground**, **2. Pick the entrance on the ground** (size, minimum level), **3. Save portal**; both ends draw as markers; **Your portals**: Go to, Delete portal (confirm). New maps copy terrain via **Pick the terrain to copy** (tile outline in the world). Region relocation: **Pick where it goes** (outline) and **Move pack region** (confirm); regions containing NPC patrol routes refuse relocation explicitly. |

**Escape cancels the active tool before opening the menu**, including while a model search has focus.
An unfinished stroke or patrol route is discarded; already saved edits remain. Unsaved path points stay in the
Paths editor (Clear points or New path discards them). **Ctrl+Z** and the Undo buttons
reverse the selected pack's last saved operation, including an edit from another task page. Text-field Undo remains
with the text field. Cancelling an object move leaves its saved position intact.

NPC previews show static spawn positions, not live movement. Use **NPC view options → Refresh nearby NPCs**
after moving to another area. Stock NPCs with scripts, custom AI names, EventAI or original-entry quest objectives can be inspected
but cannot yet be imported; the form explains why. Imported stock spawns keep their original pose and route;
authored pack spawns can be moved. Other entry-sensitive server systems may need separate compatibility work.
Selecting another NPC, New NPC or another pack discards unsaved NPC form edits; a failed save retains the draft.
An NPC with a saved version in another pack must be edited in that pack, even when that pack is disabled.
The message names the pack to choose in the selector. A rejected selection keeps the previous form visible;
check the form's NPC name before saving.

## 5. Content packs beyond terrain (Phase 2)

Server-side maps, areas and area triggers are WORLD-DB tables in VMaNGOS (`map_template`,
`area_template`, `areatrigger_template`, `areatrigger_teleport`); the client needs the matching
DBC rows (`Map.dbc`, `AreaTable.dbc`, `AreaTrigger.dbc`) inside patch-7. So a pack owns four more
kinds of content, all stored as `wp_doc` rows and changed only through `POST /WorldPacks/Content`
(one audited, undoable op per call, however many docs it touches):

| kind | key | publish does |
|---|---|---|
| `dbrow:<table>` | the table's key columns joined by `|` | writes the row into the world DB; rows a previous publish added and no enabled pack owns are deleted (`wp_installed_row`) |
| `dbc:<Name>` | row id | clones a stock row (`cloneFrom`) and patches fields into the patch-7 copy of the DBC (`{ "f": x }` = float) |
| `map` | map id | new map: WDT from its tile docs, whole-map extract + navmesh |
| `tile` | `map:col:row` | stamps a stock ADT into a pack map OR onto a stock continent (0/1: the stamp REPLACES that stock tile while the pack is enabled) - terrain/textures/water, trees and/or buildings optional, area id + subzone paints; `healHoles: [{x, y}]` closes terrain-hole squares; `areaReplace: "from>to"` re-tags one area's chunks (land raised out of the sea joins the zone); `dropWmos`/`dropDoodads` (file names) remove buildings/props; `stitch: <yd>` blends the stamp's seams into the ground around it; a tile stamped onto ITSELF is an in-place edit (stock ids kept) |
| `path` | name | a graded path (pass, land bridge, road): `points [[x,y,z],...]`, `width`, `falloff` - applied after the seams, the same on every tile it crosses (G13 walks it) |
| `worldmap` | `map:area` | `map`, `area`, `directory`, `polygon: [[worldX,worldY],...]`; generates continent ZMP ownership, additive highlight BLP and tight WorldMapArea bounds from one polygon. Optional `terrain` fits explicitly listed tiles to the same coast (details below). |

**One outline for terrain and the continent map.** `WorldPackWorldMap` validates a finite, simple polygon and a
pack-owned top-level AreaTable row with a name; overlapping ownership and an explicit conflicting WorldMapArea
doc are rejected. The build replaces only ZMP cell centres inside the polygon, preserving every outside cell.
The highlight is RGB on black with BLP alpha depth 0 (the gameplay additive loader derives its alpha), width 128,
and a power-of-two file height. Generated 3:2 WorldMapArea bounds place that image on the original painted
continent. The player's normal ZMP hover path provides the name and glow. The continent never draws a rectangle
of stamped minimap tiles; the zone view can still show its minimap mosaic.

`worldmap.terrain` has explicit `tiles: [[col,row],...]`, `seaLevel`, `seaDepth`, `coastWidth`, `minimumLand`,
optional `joinNorth`, and `joinWidth`. `WorldPackCoast` runs after seam stitching: inside becomes land, the coast
tapers to sea level, and exterior returns toward the original destination seabed. The northern join remains a
land connection. Coastal source props and abandoned terrain holes are removed, exterior areas and water come
from the destination baseline, and untouched baseline surfaces keep their original normals. The graded access
path is applied afterward. Only the declared tiles participate; the polygon is pack data, not client gameplay code.

**The safety law:** ordinary `dbrow:` documents only ADD rows in reserved ranges — templates, quests, texts, loot, gossip
7,000,000+; spawn guids 1,500,000-7,999,999 (vanilla spawn guids are 24-bit; mangosd starts its runtime counter above the highest DB guid and SHUTS DOWN when it overflows - 70,000,000 did exactly that on 2026-09-26); map ids 800+; area and area-trigger ids 7,000+ (AreaTable
explore bits 1100+, the stock max is 1076). These documents never overwrite a stock row
(`WorldPackContent.Tables` holds the key columns + floors).

**Existing NPC editing (owner request, 2026-09-28):** a separate `npc-replacement` document may redirect
one existing stock spawn to a reserved template owned by that pack. The template and its editable services
are copied; the original shared template is untouched. `wp_npc_baseline` durably records the original,
observed and pending entries before the world spawn changes. The update compares the observed entry,
refuses an outside edit, and is recoverable across interrupted publishes. Disable or remove the document
and publish to restore the original entry before deleting the pack template. Two enabled packs cannot
replace the same spawn. Reserved NPCs may retain links to positive stock quest IDs; relationships between
two stock IDs remain outside ordinary pack ownership. All authoring still goes through audited, undoable
Content operations. Read-only nearby/detail endpoints supply offline Creator previews and forms.

**Human editor contract (2026-09-28):** World has task pages for Terrain, Buildings, NPCs, Quests, Publish
and additional map/region/check tools. The chosen pack, active action and save feedback stay visible.
Escape cancels the current world-editing action before the menu; cancelling an unfinished stroke restores
its preview, while Undo reverses an already saved operation. New strokes must affect the final rendered
terrain after generation, including shaped coasts and paths. Saving NPC, quest and object forms is explicit;
publishing is a separate server-wide action. Offline NPCs must be visible and selectable without a live
server session, including existing town vendors. A form must preserve data it does not expose.

Client tools (World panel sections): **NPC Creator** (look from any creature, name/title, level,
faction, rank, role flags incl. vendor/trainer/innkeeper/repair/banker, vendor list, trainer spell
list copied from a stock trainer, weapons, greeting text; one op = template + equip + gossip +
vendor rows + optional spawn), **Spawn NPCs** (click to place pack creatures; previewed offline),
**Quest Creator** (kill/collect, rewards, giver/ender), **Pack content** (browse/delete docs),
**Maps & dungeons** (new map from a stamped tile block; subzone paint; portal = AreaTrigger.dbc +
areatrigger_template + areatrigger_teleport; a dungeon = an instance-type map + a portal in + out).

Initial choice (2026-09-26, superseded): **Gilneas** (Zone Ideas map "Gilneas"), as pack map 800 — walled off in
vanilla, so a separate map entered through the Greymane Wall is lore-consistent and toggles cleanly.
Stock Azeroth has no land there (rows 34+ south of Silverpine are open sea, area 2397/2398), so the
terrain is stamped from Silverpine (cols 27-30 × rows 29-33: forest, Lordamere shore, west and south
coast) with its villages/Shadowfang Keep stripped and its trees kept, then reshaped and built up.
Dungeon: **Greymane Fortress** (instance map 801).
Gilneas subsequently moved onto continent map 0. Its current authoring outline follows the already painted
land south of Silverpine; the old destination ADTs there were sea, while the continent art already showed land.

## 6. Script driver and verification tools

- `MSUI_WB_SCRIPT=<file>` boots Creator Mode and runs World Builder commands through the same code
  as the panel (place/sculpt/undo/publish/wait-build/download/newmap/portal/subzone/content/
  travel/probe/height/ghosts/xray/xraynav/shot). A live console: appended lines run as they land.
- `track <entry> <seconds>` in the live protocol runner (`--live-protocol`) prints every visible
  creature of that entry twice a second — the pathing proof below came from it.
- interface-wire-check `--world-builder-only`; web `WorldPackAdtTests` (byte-exact ADT round trip,
  sculpt, normals vs stock, relocation, WDT, DropWmos/DropDoodads).
- The World Pack Verifier (§7) and its script commands `verify [rerun]`, `verify-spawns`, `survey`.
  A rejected op prints `[wbscript] ERROR op rejected:` — a script never runs on past a failed save.
- `worldmap-outline.py` traces explicit painted-map pixel vertices into world positions using stock continent
  bounds; `--preview` makes a review sheet. `fit-gilneas.py` generates an offline migration plan and expected
  snapshots; only `--apply` / `--apply-existing` writes through audited APIs with a stale-snapshot check.
- `gen-live.py --maps 0 --sections worldmap` and `gen-wb-verify.py --maps 0` scope verification to the continent.
  `gen-map-smoke.py OUT --docs DOCS.json --state STATE.json --map 0` builds an offline gate/town/coast/map protocol;
  optional `--dock-route='x,y,z|x,y,z|...'` walks a collision-measured shore-to-deck route. It never changes a party.
- Live developer commands: `worldmap-probe <map> <worldX> <worldY> <expectedArea>` checks mounted ownership,
  name, decoded highlight and actual GL texture; expected area 0 requires no resolved zone. `worldmap-continent
  <map>` opens the normal painted frame. `worldmap-hover <map> <worldX> <worldY>` supplies pointer input through
  the existing QA mouse proxy, so ordinary hover code draws the glow/name; `worldmap-hover off` clears it.

## 7. The World Pack Verifier — nothing built counts until it passes

Owner rule (2026-09-26): **everything a pack builds is verified, not just buildings**, and "I looked at a
screenshot" is not verification. Three tiers, each automated; a publish is done only when all three are
clean (or every remaining finding is explained in the as-built log).

**Tier 1 — static audit, web app, runs inside every publish** (`WorldPackAudit` + `WorldPackVerifier`,
phase `verify` after the mangosd restart; `GET /WorldPacks/Verify` re-runs, `/WorldPacks/VerifyReport`
returns the last; report in `worldpacks/out/verify.json`). It reads the PUBLISHED world (out/patch-7 over
stock), the enabled packs' docs/placements, the live world DB and mangosd's logs of the current run:

| id | checks |
|----|--------|
| G1 | every stamped tile's kept WMOs/doodads equal the stock source + stamp offset (model, position, rotation) |
| G2 | placed WMO group boxes interpenetrating any other building (> 0.5 yd) |
| G3 | ADT props / placed M2s standing inside a placed building |
| G4 | placed building's bottom vs terrain (floating); **buried walls** — terrain inside the footprint above the floor (fix: script `pad <id>`) |
| G5 | road texture under the footprint; nearest road distance and how far off square the building sits |
| G6 | props left where a dropped WMO stood |
| G7 | every pack spawn vs terrain (under ground / floating / **on a roof**); indoor spawns deferred to tier 2 |
| G8 | every portal arrival vs terrain |
| G10 | water (MCLQ surface): boats on the surface, flat dock decks 0.2-4 yd above it; deck = the M2's dominant **upward-facing collision surface**, using its actual heights (undersides and posts are excluded). Sloping ramps report their full height span and slope and require a live shore-to-deck walk in both directions; no creature standing in deep water |
| G11 | every pack tile has a minimap image (md5translate.trs -> existing BLP); the build RE-RENDERS the pixels where the published ground differs from the ground its source image shows (`WorldPackMinimap`: height, land/water, dropped or placed buildings; colours fitted to the Blizzard image, one sea model; `wp_*.blp`) |
| G9 | every patched tile: no height crack along chunk/tile borders; on stock tiles no normal differs from Blizzard's where no height moved (sculpt drift = shading patches) |
| C1-C10 | spawns↔templates, never-spawned templates, npc flags ↔ vendor/trainer/quest rows, **gossip options for every service of an NPC with its own menu**, equipment/display/loot existence, vendor items + trainer spells exist, gossip texts, quests (givers/enders spawned and flagged, kill targets spawned, objective items obtainable, rewards and chain exist), EventAI (runs, spells/texts exist, flags valid), maps (map_template, ghost entrance, portals in AND out, Light row), areas (area_template, graveyard per zone + its WorldSafeLocs), triggers (AreaTrigger.dbc + areatrigger_template) |
| C11 | linked packs and patrols (creature_groups): both guids pack spawns, members within 40 yd of the leader, the leader's own row, formation followers idle; a patrol (movement_type 2) has >= 2 creature_movement points, legs <= 80 yd |
| G14 | every patrol waypoint on the published ground and every leg (the closing one too) walkable: grade, deep water; a leg through a building's group box is only a warning when the navmesh does NOT connect the route (a city is one WMO) |
| G15 | **reachability on the server navmesh** (the installed `.mmtile`s, `WorldPackNavMesh`): on a pack INSTANCE every pack creature and patrol waypoint stands on the walk-connected component of the entrance arrival (error otherwise: the group cannot walk there, the mob evades "target unreachable"); on a continent a combatant on a tiny island (< 1500 sq yd: a roof, a rock top) is a warning. Plan with `tools/worldpack/navmesh.py` before publishing |
| G16 | generated world-map assets: AreaTable name/parent/map, exact WorldMapArea bounds, polygon-owned ZMP cells and byte-preserved outside cells, decoded highlight pixels/dimensions/alpha and alignment |
| G17 | coast terrain on both outer and inner ADT vertices: no newly dry land outside the painted polygon, no submerged interior. Every active exterior liquid cell must match stock flags and all four water heights; inland ponds are preserved. The original stock land, narrow beach and declared northern join are treated explicitly |
| C0 | every pack DB row fits its live column type: integer widths and UNSIGNED on every numeric type (creature_groups.angle is FLOAT UNSIGNED - build #28 was refused mid-install). Pre-flight AND publish refuse on it |
| L1 | every Server.log/DBErrors.log line of the current run that names a pack-owned id; "ignored/nonexistent/skipped/invalid" lines are errors — mangosd DROPS what it complains about |

**Tier 2 — client collision pass** (Creator Mode → World → Verify, or script `verify-spawns`): every pack
spawn and portal arrival on the map, against the real client collision with its tiles resident — floor within
1.2 yd under the feet, no ceiling within 2 yd, not boxed in by walls. `survey <x> <y> <dist> <name>` takes four
eye-level shots (N/E/S/W) of anything built, for review at human eye height, not from the sky.
**Terrain holes** (every G12 chunk of the loaded report) are judged by the player's OWN physics: a probe
`CharacterController` (off-screen, same terrain + collision) is dropped into every 0.83 yd hole cell, and from
every standable edge walked into each cell nothing catches. error = a walking body leaves the world; warn = only
the client's void guard holds it (a visible gap: heal it); info = caught / void sealed in by walls. The pass
writes `worldpack-holes.json` next to the client (per chunk: a catch point + the first walk-off) — the tier-3
hand-off (`tools/worldpack/gen-live.py --holes`).

**Tier 3 — live, as a real character** (live protocol as Testwar): reach every NPC and open each service
(shop, trainer, inn, bank), take a quest, kill its targets, turn it in, enter and clear the dungeon.

**Loop speed (2026-09-26, owner: "improve going forward").** A round is serial by nature (one publish,
one server, one Testwar), so the loop itself was made cheaper instead of parallelised:
- *Pre-flight* (`GET /WorldPacks/Preflight`, script `preflight`, panel "Pre-flight"): C1-C10 + the
  world-DB column-type validation (C0) on the UNPUBLISHED docs, in seconds. Publish runs it first
  (phase `preflight`) and refuses to build at all on a C0 error — a row MySQL would reject.
- *Incremental pack-map navmesh*: `worldpacks/navtiles.json` fingerprints every patched ADT; a pack map
  whose tile set is unchanged regenerates only changed tiles + neighbours with the installed params
  file and keeps the rest installed (was: the whole map, 186 s for Gilneas, every publish).
- *Changed-only tier 2*: `verify-spawns changed` visits only targets whose row changed since their last
  clean check or that stand within 80 yd of a placement added/moved since then
  (`worldpack-verified.json` next to the client, a convenience cache — never evidence).

Tools the checks call for: `pad <placementId> [margin] [falloff]` levels a building's footprint to its floor
and feathers back into the slope (one sculpt op); `move <placementId> x y z|ground [heading]` (one Move op);
`hour <h>` pins daylight for review shots; `survey x y dist name` stands OUTSIDE on each side facing the
subject (an orbit camera around the origin ends up inside the building). Tier 2 also flags a spawn on a
ROOF (collision floor well above terrain, open sky above, a room below — a dock has no room below) and its
fix mode widens the open-ground search 10/20/30 yd, never falling back to the topmost surface.
Pack-map sculpt preview: a pack map has no stock tile, so the preview base is the mounted patch-7 tile
minus the sculpt it was published with (only when the mounted patch IS the last publish).

**Quest Creator** (World panel > Quest Creator; `Engine/UI/QuestAuthoringLaw.cs`, pinned by
`--world-builder-only`): a quest draft becomes EVERYTHING it needs to work, as one audited op - the quest
row (4 objectives: kill / collect / use object / explore, 6 choice + 4 fixed rewards, reputation, chain),
giver/turn-in relations, quest-only loot rows on a pack drop source (loot_id assigned when missing),
AreaTrigger.dbc + areatrigger_template + areatrigger_involvedrelation + SpecialFlags 2 for exploration,
and the quest-giver flag + QUESTGIVER gossip option on pack NPCs that lack them. Stock creatures cannot be
drop sources (a pack never edits stock rows). Existing quests load back for editing; `$N/$C/$R/$B/$G`
preview; after a save the pre-flight runs and the quest's own findings show. Script: `questui draft.json`.
Item search needs `creator-items.tsv` (git-ignored; regenerate read-only from the world DB:
`SELECT entry,name,class,subclass,quality,display_id,inventory_type,required_level,item_level` from the
highest patch row per entry, with a header line) - quest items are included (search filter -2 = any item).

**Live players get the pack's COLLISION too.** patch-7.MPQ is only what the client renders; MSUIClient's
live collision reads server-format vmaps. Download therefore also syncs `/WorldPacks/Installed` vmaps/
mmaps into `GameData/vmaps|mmaps` (`Net/WorldPackCollisionSync.cs`: originals kept once in
`.worldpack-baseline/`, files no longer installed restored, `worldpack-sync.json` manifest). Found by
tier 3: a pack wolf 8.7 yd above a live player who fell through the pack's ground geometry... and then,
with collision synced, still unreachable - it stood on a CLIFF LIP (next paragraph).

**Slope law.** Vanilla walks up to ~50 degrees. Spawn placement (`WbOpenGround`) refuses > 45 degrees,
tier 1 G7 and tier 2 flag > 50 degrees on terrain, tier-2 fix mode re-homes such spawns.

**Minimap for pack maps** (G11): the build appends a `dir: <map>` section to md5translate.trs mapping each
stamped tile to its source image or a generated `wp_*.blp`. Pixels whose final terrain, water or props differ
from the source are re-rendered after shaping and placements; unchanged pixels keep the source art.

**Tier 3 practicalities** (scratchpad `gilneas/gen-live.py`, sections portals/npcs/quests/dungeon): revive
first (a killed session leaves the character dead in the world); combat with `.gm off` (a GM's melee never
lands) and god mode; `gm-to-selection` onto wandering mobs; wait on DECODED quest verdicts (the SENT ones
race); a 5-player boss is fought for real for 60 s (proves it engages and casts) then finished with
`.damage` to prove death + loot - balance for a party is a separate group test. Screenshots: automatic
dump families are capped (`AutomaticDumpFamilies`), fight captures are start + outcome only.

Lessons that created checks (2026-09-26, Gilneas build #7):
- G1 found `AdtDocument.DropWmos` renumbering MODF name indices on tiles WITHOUT the dropped WMO and then
  returning early without installing the new name table: every Silverpine farm tile had its buildings'
  MODELS swapped (farmhouse where the barn stood, crypt where the shipwreck was) — the "clipping, not
  aligned with the path" the owner saw. Fixed + `DropWmos_NoMatch_LeavesTheTileByteIdentical`.
- C4: a creature with its own `gossip_menu_id` shows ONLY that menu's `gossip_menu_option` rows (menu 0's
  generic browse/train/inn options are for menu-less creatures): all 8 Gilneas service NPCs were
  unreachable. Packs may now own `gossip_menu_option` (62000+); the NPC Creator writes one per role.
- L1: `ObjectMgr::LoadMapTemplate` ERASES an instance whose ghost entrance is not on continent 0/1 (the
  "ignored" log line) — map 801 and its entrance portal did not exist on the server. Ghost entrance -1.
- C0 + L1: pack spawn guids at 70,000,000 took mangosd down the first time a player entered the world
  ("Creature guid overflow!! Can't continue") - no load-time complaint, so no check saw it. Range moved
  to [1,500,000, 8,000,000), enforced by WorldPackContent.RowKey and verifier C0; L1 treats
  "shutting down" as fatal. Found by tier 3 (a real login) - the tier that cannot be skipped.
- G6: dropping Shadowfang Keep left its fence and courtyard trees; the build now drops the ADT props
  inside a dropped WMO's group boxes (`AdtDocument.DropDoodads`).
- G12 + tiers 2/3 (2026-09-27, owner: "sometimes you fall through the floor in Gilneas"): STOCK mine, crypt
  and cave mouths (Emberstone Mine, a Silverpine crypt, the Arathi-style fortress entrance...) leave a corner
  of their terrain-hole square with NO geometry under it (column dumps: only the arch overhead). Walking over
  it dropped Testwar out of the world 10 times in 15 live walk-offs (z -96..-555). Not a collision-rule bug -
  the client and the server extractor agree (`COLLISION || RENDER && !DETAIL`). Fixed in the CLIENT for every
  zone: `CharacterController` hole void guard (in a hole with nothing WALKABLE under the feet, hold the body
  on the hole's height field, never above its current height) + shell void guard (under the terrain shell,
  8+ yd into a fall, loaded world, nothing walkable below: back onto the surface). Pinned by
  `character-controller-clinical-check` (three scenarios) and `--world-builder-only`. After: 0 falls live.
  The gaps stay visible (warnings); a pack can close a square with `healHoles` (tile doc) = `healhole x y`.
- Tier 3 quests: Testwar leads the 40-man raid, and vanilla gives a non-raid quest no kill credit and hides
  its quest drops in a raid group (Core `Player.cpp` `isRaidGroup() && !IsAllowedInRaid()`). 10 real kills,
  0 credit. The generator proves the camps killable, GM-completes (labelled SETUP) and runs the real turn-in.
- A World Builder script run auto-logged the account in and KICKED the running live protocol (same account).
  Script runs (`MSUI_WB_SCRIPT`) no longer auto-login; pinned by `--world-builder-only`.
- Tier 1 and tier 2 disagreed about the ground at Keel Harbor by 2-4 yd (2026-09-27, continent Gilneas): tier
  2 "fixed" two dockhands INTO the ground and tier 1 (G7) then called them buried. `terrain x y` (archive that
  supplies the tile, the file's chunk heights, the AdtCache's, the height grid) showed the file right and the
  cache wrong: the Creator preview of a SCULPTED continent stamp rebuilt the tile on the stock SEA tile's
  relative heights (~0) because it decided "pack stamp?" from the tile docs, which arrive after the first sculpt
  state, and cached that guess. The base is now chosen by the published manifest's `stamps` (patch-7
  `WorldPacksuild.json`, always mounted) or a doc, and never cached before the docs load. Tier 2 checks the
  editor's terrain, so any preview bug is a verification bug: when two tiers disagree, `terrain x y` first.
  Fix mode also lifts a BURIED spawn (terrain above its feet) onto the surface.

## 8. As-built log

- 2026-09-26: design written; research findings in §1.
- 2026-09-26: extractors patched on the box (`MapExtractor`/`VMapExtractor` read patch-3..99 and take
  `-m <maps>`); workspace `~/worldpacks/{work,tools,baseline,out}`. Re-extracting stock maps 0/1
  reproduced all 1705 `.map` and 929 vmap files and the Northshire `0004832.mmtile` byte-for-byte
  (the determinism the byte-diff install relies on).
- 2026-09-26: MCNR byte order is (worldX, worldY, up) — measured; MSUIClient `McnkChunk.NormalAt`
  reads it as (b0, b2, b1), which looks like a separate client lighting bug (not changed here).
- 2026-09-26: **Phase 1 Northshire proven live.** Pack `northshire-barrens-test`: HumanTwoStory.wmo
  at (-8879,-241) across Northshire Peasant waypoint lines + a sculpted hill at (-8845,-150). Build #1:
  patch-7 850 KiB, 615 server files installed over a baseline, mangosd restarted clean. Client: ghost
  before publish; after download/hot-mount the WMO comes from the patched ADT (28 WMO placements vs 27)
  and is solid in client collision (probe z 88.6 over terrain 81.4); X-Ray navmesh shows the house
  footprint carved out and the hill. Server: peasant 80127's waypoint segment (-8872,-246)→(-8896,-234)
  runs straight through the house; tracked live it walked north along x≈-8870 then west along
  y≈-234 (the house's north wall) and returned down x≈-8873.3 (its east wall) — pathfinding around it.
- 2026-09-26: fixes found by that run — a new spawn renumbers the map's vmtree so ~350 vmtiles differ
  in bytes only: they are installed as a set but navmesh work now keys off the PATCHED ADTs (+ neighbours),
  not vmtile bytes; `AdtCache.SetMap` is a no-op on the same map, so the hot reload now calls
  `AdtCache.Invalidate()`; a stray 90-byte `000_48_32.vmtile` (May 9, the old editor's swapped-tile bug)
  was not stock and was removed by the install (kept in the baseline).
- 2026-09-26: **Phase 1 Barrens proven live** (build #2, same pack): OrcMedium.wmo at (-632,-2495.5),
  BarrensTree03/BarrensWagon01 M2s, a sculpted mound (-672,-2560) + Northshire ElwynnCliffRock02/
  ElwynnTreeMid01 M2s. Client after hot-mount: all static, solid (probe z 115.8 orc roof, 99.7 tree M2
  collision, 98.1 wagon; terrain ~92), mound 104.7. Server: gazelle 14009's waypoint line
  (-615,-2484)→(-649,-2507) runs through the building; tracked for three laps it arced ~14 yd north
  (via -632.6,-2481.1) and came down x≈-646 to the next waypoint.
- 2026-09-27: **every client mirrors the server's packs.** `Net/WorldPackStartupSync.cs` runs before the
  archives mount: the web app's last build vs the local `patch-7.MPQ.json` sidecar (build id + sha1); a
  mismatch downloads patch-7 and syncs the pack collision. Proven: sidecar removed → "updated to build #16
  (19349 KiB patch-7; collision synced: 990 file(s))" and the sha1 matched the server's. `/WorldPacks/
  Installed` now lists a sha1 per file (cached by path+size+mtime) and the sync skips files already
  byte-identical, so a new build moves only what changed.
  2026-10-14: the blocking part is now only the version check (5 s) and, when the build moved, the patch-7
  download (60 s); the sidecar is written as soon as patch-7 lands. Collision syncs in the background and
  is "current" when both `worldpack-sync.json` manifests (vmaps, mmaps) carry the server's build id, so an
  interrupted sync resumes next launch. Before this, the whole sync blocked `Load()` under a 90 s timeout
  and a timed-out sync never wrote the sidecar, so every launch redid it (owner saw minute-long F5 starts).
  The `[worldpacks]` line is printed after the console tee, so it now reaches `msui-console.log`.
  2026-10-14 (later): **why the slow boot came back, and the two fixes.** The scripted QA clients
  (`tools/worldpack/launch-wb.ps1`, `tools/tabletop/launch-tabletop.ps1`) share `GameData` with the owner's client
  but point at the WSL web app; their startup sync swapped the owner's patch-7 build #59 for WSL's #4 (18:58) and
  restarted the collision sync, so the owner's next launch re-downloaded everything. (1) `MSUI_WORLDPACK_SYNC=off`
  skips the startup sync ("sync skipped ... GameData left as it is"); both launchers set it, and the World
  Builder's in-game Download then saves to `scratch/worldpack-qa/` and touches neither the sidecar nor collision.
  (2) **No more black, frozen-looking window**: the sync runs on a worker while `ClientWindow.PresentWhileWaiting`
  keeps polling events and presenting a progress card ("Updating world content", stage, MB of MB, bar; a sweep
  while the size is unknown; nothing for the first 0.25 s) and the title bar says the same. Verified with
  `MSUI_STARTUP_PROGRESS_DEMO=<png>` (a simulated 21 MB download through the same card, frame saved at 3 s).
- 2026-09-27: **world map for pack maps.** No WorldMapArea row, no painted art → the world map of a pack map
  (id 800+) is the mosaic of its own minimap tiles (`WorldMapUiLaw.TryMinimapMosaicBounds`, 3:2, west left,
  north up); stock maps and instances are untouched. It emits `world-map/mosaic/DRAWN` (map, tiles, bounds);
  the map's Map.dbc name titles the dropdown. Tier 3 section `worldmap` asserts it on every pack map.
- 2026-09-27: **pack portals are named on the client.** The build writes the packs' areatrigger_teleport
  rows into patch-7 as `WorldPacksreatrigger_teleport.tsv` (the client's reference TSV format);
  `AreaTriggerTeleportTable.MergePack` merges it over `data/reference` - pack portals had been bare
  "trigger 7010" volumes with no destination for the loading screen or the prewarm.
- 2026-09-27: live protocol rules found by the full run: a kill-only/explore quest goes from COMPLETE_QUEST
  straight to the reward OFFER (request-items only when the quest asks for items); `wait-grounded <s>`
  settles a body teleported onto a wandering mob's live spot before combat (a slope threw a fixture).
  Portal 7011 (back to Silverpine) proven live. GM commands that take a PLAYER (`.cheat god`, `.revive`,
  `.quest complete/remove`) act on the SELECTION when no name is given: with a creature selected god mode
  was never on and Testwar died to Lord Godfrey (a quest kill objective), the death popup displaced every
  later quest frame and the ghost could not enter the dungeon. The generator names the character, revives at
  every quest, and fights a boss (rank 3) for a fixed time (asserts it swings back and, with EventAI, casts)
  before the GM finish - never a solo fight to the death. `select entry-nearest` skips the dead, so loot
  after a kill uses `select corpse-nearest:<entry>`, and waits for the loot REPLY (`family=loot
  step=response`; a boss must answer OPEN) - `waitfor family=loot` matched our own request line. Fights face
  the target first (`face selected`: a `.go` keeps the old orientation → BADFACING forever), and
  `fight-until-dead` re-presses attack when the server ends auto-attack with the target alive
  (`LiveFightReengage` verdict).
- 2026-09-27: **a teleport that lands inside a trigger no longer kills the portal.** The client reported the
  trigger 2 ms after MSG_MOVE_TELEPORT_ACK; the core handles the ack on the map thread and CMSG_AREATRIGGER
  on the session thread, judged it against the OLD position ("too far, ignore", debug log only) and the latch
  then left the player standing in a dead portal (portal 7011, live). Area triggers now wait 0.5 s after a
  server teleport is applied (`HoldAreaTriggersAfterTeleport`) - summons and arrivals inside volumes too.

- 2026-09-27: **Gilneas is continent land.** Owner: zones in WoW do not have portals - only instances and raids.
  Gilneas had been its own map (800) behind a teleport volume at the Greymane Wall. South of the wall the stock
  Eastern Kingdoms are open sea (tiles rows 35-40, z -515); the Gilneas block now REPLACES those sea tiles
  (map 0, cols 27-30 x rows 35-39) and you walk in: through the Greymane gate (the vanilla gate is shut by a
  portcullis PROP `greatwall_portcullis.mdx` in tile 29,33 - an in-place stamp drops it and a pack placement
  raises it into the arch), over the plateau, through a pass cut in the ridge and across a causeway (path
  `greymane-pass`). Tools built for it (panel section "Regions, seams & paths" + script commands):
  `relocate <from> <to> <dCol> <dRow>` (one undoable op: every doc, placement and sculpt moved by whole tiles;
  `WorldPackRelocation` + tests), `stamp ...` onto a continent, `tile <c0-c1> <r0-r1> <key> <value>` (stitch,
  dropWmos, dropDoodads ... one op per block), `carve <name> <w> <falloff> x y z|ground ...` (path doc).
  Build: block-wide seam stitch (every seam a world-space segment with the neighbour's final heights; every
  stamped vertex blends toward ALL seams in range AT ONCE - a vertex ON a seam takes exactly that seam's height,
  elsewhere inverse-square weights scaled by the strongest smoothstep band, so shared vertices agree and seams
  that end fade out. The first version blended seam after seam and a later seam pulled on-seam vertices of an
  earlier one off it: build #19 had 6 G9 cracks up to 41 yd; build #20, 0), the stamp's own sculpt BEFORE the
  stitch (sculpt after it reopened the seams), stock sea water carried into chunks the new ground takes below it (`AdtDocument.CarryLiquidFrom`),
  paths after the seams, a generated WorldMapArea row per pack zone, the manifest lists `stamps` (the client
  paints them over the continent's painted world map), minimap lines of replaced continent tiles point at the
  stamp's SOURCE image (G11 checks it). Verifier G13: every path walked on the published ground (grade <= 45,
  not under water, not into a building). Live: `walk-to x y timeout [radius]`; the generator's `paths` section
  walks every path.
- 2026-09-27: **walked in from Silverpine, live.** Testwar (build #23): through the Greymane gate corridor and
  down the pass on foot, no teleport; 293 steps, 2 stops. Both were real: the path's last point lay inside a
  farmhouse footprint (now G13 "runs into a building"; the end moved out) and a tree trunk stood in the lane
  (live collision includes M2 props; the Creator's offline collision is buildings only, so `walkprobe` walked
  through it). A path now CLEARS its lane (props within half the width; `clear: false` keeps them). The pass
  and plateau (tile 29,34 and the south side of 29,33) still said "The Great Sea" on the minimap: land raised
  out of the sea keeps the sea's area id until `areaReplace "2397>7001"`. The zone world map of a pack zone
  is every minimap tile in its bounds, and where Blizzard has NO minimap (open ocean without an ADT) the
  open-sea image it shares across 165 empty tiles - those gaps showed the frame's golden parchment. (Zooming
  the continent's painted art to the zone instead was tried: ~70 source pixels at 15x, a blurry smear.)
  Known gap: a stock tile the pack reshaped without stamping keeps its stock minimap (29,34's strip).
  A stamp with `stitch` 0 is never reshaped by its neighbours' seams (an identity stamp for an area re-tag
  opened a 7.5 yd G9 crack against the untouched stock tile beside it, build #25).
  Tier 2 after `healhole all` x2: map 0 0 errors (the one warning is Northshire's, another pack), map 801 0/0.
- 2026-09-27 (afternoon): **zone world map checked live** (Testwar, `failures=0`): Gilneas 7001 draws 80 tiles, no
  parchment gap. The thin olive line west of the block (cols 25-26, bottom of row 36) is Blizzard's own art: stock
  minimaps `600e88d5...` and `aa26c10d...` carry that strip in their bottom rows (decoded with mpqpeek). The same
  run found the sea fill's flaw: map 801 got a generated WorldMapArea row, so it takes the zone path, and with four
  unique tiles the "most-shared" image was simply its first tile - the town repeated around the dungeon's edges
  (`tiles=16`). The fill now needs an image shared by >= 16 tiles (`WorldMapUiLaw.SharedSeaTexture`, pinned by
  `--world-builder-only`); 801 draws its 4 tiles on the frame's parchment. `gen-live.py` `worldmap` asserts
  `tiles=N;` for every pack map (N = its tile docs); re-run live: `tiles=4`, PASS.
- 2026-09-27: **a fair tester must START like a real level-N character** (owner: "you need weapon skills and stuff
  levelled up"). The first fair run was stopped: Gilnwar, GM-levelled 1 -> 42, kept weapon skill 5 and defense 1 and
  died to the first wolf, which never left 100%. And `.revive` is `ResurrectPlayer(0.5)` even on a LIVING character:
  every fight after a stand-up started at half health. Fair SETUP now: level, `.learn all_trainer` (only
  trainer-GREEN spells: level/rank/skill met - class trainers + weapon masters), quest-only class abilities
  (`QUEST_SPELLS`; warrior stances), `.reset items` + four bags (`inventory ensure-bag`, re-runnable, then
  `require-bag` from the server's fields), premade talents + gear, `.maxskill`, and the client asserts it:
  `assert-skills-capped` (every Weapon Skills line incl. Defense at level x 5 from PLAYER_SKILL_INFO; live:
  17 lines 210/210 at 42). Every stand-up is `.revive` + `.replenish`; each boss pull `.replenish` + `.group
  replenish`. Result on the first quest: full health 2471, wolves die in ~13 s for ~190 damage. A kill skipped
  because the body was still sliding after `wait-grounded` left the quest 9/10 and the server refused the turn-in
  (`CanRewardQuest` resends the offer): `fight-until-dead` now waits up to 3 s for the body to land.
- 2026-09-27: **fair quest objectives are earned like a player earns them: `kill-for-quest`.** The per-spawn
  protocol (teleport ONTO spawn k, settle, face, attack) failed the second fair run: in a clustered den the
  tester stood ~12 s being hit by the neighbours before swinging (2500 -> 401 health, died, target untouched),
  and six quests ask for more kills than their camp has spawns (Ripper's Den 8 of 5, Howls 10/6, Shadowcasters
  6/5, Mastiffs 10/6, Naga 10/6, Forsaken 6/5; respawn 300 s), so revisited spawns were corpses. The runner step
  `kill-for-quest <quest> <kill:N|item:ID> <entry> <count> <spell> <s> [radius]` runs until the quest log's
  counter (or the carried item count) reaches count: attackers first, else the nearest living target pulled by a
  GM teleport BESIDE it (movement SETUP), respawns waited out at the camp, item objectives looted, `.replenish`
  below 60% between pulls and `.revive`+`.replenish` after a death (SETUP) - the `QuestKills` verdict carries
  pulls/adds/deaths/rests/chases/loots/seconds. Proof (Gilnwar 42): Ripper's Den 8/8, 7 pulls, 1 add, 0 deaths,
  389 s (4 min of it the respawn); Fangs for the Watch 6/6 fangs in 20 kills (drop 40%), 998 s; both turned in
  with earned XP. The small camps are a DESIGN finding for the owner (a player waits a respawn cycle there).
- 2026-09-27: **fair run, all 12 open-world quests EARNED** (Gilnwar 42, trained + skills 210, no god mode; 0 failures
  before the dungeon): every objective 5.5-7 min except Gorefang (57 s) and Fangs (692 s, 15 kills for 6 fangs at
  40%); deaths: Howls 1, Shadowcasters 1, Gorefang 1 (it killed the solo warrior once, finished after the stand-up).
  Found on the way, each fixed: (1) the rotation named Heroic Strike rank 5 (11564) but a TRAINED 42 knows rank 6
  (11565) - every cast was refused client-side and every fight ran on auto-attack; the runner now casts the highest
  known rank of the named spell's chain (`LiveRotationSpell`, `SkillLineCatalog.AbilityRank`). (2) Boss trials that
  follow a failed one are confounded: a timed-out boss stays alive and pulls into the next trial, a wipe leaves the
  party bots dead - each trial now starts with `.group revive` + `.replenish` + `.group replenish`. (3) boss-trial
  logs `BossTrialState` every 5 s (boss + every member: position, health, boss target).
  **Dungeon findings (owner's call, not fixed):** Baron Ashbury and Lord Walden spawn 43.8 yd apart, so a
  ranged member standing back pulls the second boss; in run 3 Ashbury EVADED six times ("[EVADE] ... target
  unreachable 24 s (victim <hunter bot>, 14.6-16.4 yd)") - a spot near his room the boss cannot path to - and
  the trial timed out at 80%. The stock party-bot healer is class-random (priest/druid/paladin): a paladin healed
  (run 3), a druid produced ZERO heals (run 4: Ashbury 100 -> 33% in 80 s by ~165 group dps, then the unhealed
  group died one by one - WIPE). The balance numbers need a group whose healer heals.
- 2026-09-27 (evening): **linked packs and patrols** in World Content Packs (owner: the dungeon needs patrols and packs
  that feel like Scarlet Monastery). `creature_groups` pack-writable (both guids pack spawns); verifier C11 (links,
  leader row, formation followers idle, patrol routes >= 2 points, legs <= 80 yd) and G14 (waypoints on the
  published ground, every leg incl. the closing one: grade/water/buildings); human tool = World panel spawn modes
  Linked pack / Patrol, agent path = script `mobpack` / `patrol` / `movespawn`, same builders. Core: party-bot
  healers heal while their group fights (`PartyBotAI::IsGroupFighting`) - a druid healer never entered combat and
  its out-of-combat heal needs 80% mana. **Found:** map 801's north-east ward, where Baron Ashbury and Lord Walden
  stand, is a navmesh island (`.mmap path` type 4 to the gate, road and plaza; type 1 only inside) - every earlier
  boss trial had teleported the group in. Handoff §00 has the probe table and the next steps (move both bosses
  into the connected city, lay the pack/patrol route, grow the camps, publish, prove it live).
- 2026-09-27: **zone faction is chosen, not inherited.** Duskhaven's minimap name was RED for the Human tester:
  every Gilneas AreaTable row cloned Silverpine (130) and inherited FactionGroupMask 4 (Horde), while
  area_template.team said 0 - client and server disagreed. Build #27 sets field 20 = 2 and team = 2 on all eight
  areas (one Content op). New C10: an inherited faction is a warning, a client/server mismatch an error
  (`WorldPackAreaFactionTests`). Tier 3 `worldmap` asserts the minimap `pvp=` (new field of the `minimap area`
  verdict) against the zone's faction for `--team`, with GM mode OFF: `.gm on` moves the character to faction
  template 35 (no group masks) and every zone - Stormwind too - reads Contested. Live: Duskhaven `pvp=Friendly`.
- 2026-09-27 (night): **Greymane Fortress laid out like a Scarlet Monastery wing, planned on the server navmesh.**
  The server's `.mmtile`s parsed offline (`tools/worldpack/navmesh.py`: Detour v7 polys united through neighbours and
  tile portals) reproduce every live `.mmap path` probe of the afternoon: the NE ward is a 4889 sq yd island, the
  gate/road/plaza/keep one 1,064,845 sq yd component. Layout (29 trash in 11 pulls, bosses >= 40 yd from any trash
  point, every spot MAIN and open): road sentries (2 guards) -> road patrol (houndmaster + 2 hounds between the
  ponds, 6 points) -> gatekeepers (guard, retainer, guard) -> courtyard watch -> **Baron Ashbury on the plaza (32,328)**
  -> kennel keepers (houndmaster + 2 hounds) -> NW lane patrol (retainer + guard) -> hound yard -> south garden
  (3 retainers) -> **Lord Walden on the NW lawn (-34,483)** -> keep forecourt west/east packs + plague kennel (3
  hounds) -> Lord Godfrey at the keep door (unchanged). The 19 scattered trash were deleted (one op). Quest camps +2
  each (owner: "increase a bit"): 7/8/7/8/8/7.
  Found on the way, each now a check: (1) **build #28 was refused mid-install**: `creature_groups.angle` is FLOAT
  UNSIGNED and the pack builder wrote Atan2 angles (negative half the time); the pre-flight C0 only knew integer
  widths. Builder angles are now [0, 2pi) (`WorldBuilderLaw.PositiveAngle`), C0 knows UNSIGNED on every numeric type
  (`FitsColumn` + test), and the install's "transaction" is MyISAM - the half-written rows stayed until build #29.
  (2) **G15 reachability** (`WorldPackNavMesh`, tests with hand-built tiles; also in pre-flight, seconds): the NW lane
  patrol's G14 "crosses stromguard.wmo" warning (the whole city is one WMO) became info, and on map 0 two combatants
  stood on navmesh islands - a new Forsaken Deathstalker (73 sq yd) and a Bloodfang Stalker on a rock top (189) -
  moved onto connected ground. (3) Patrols proven live (`patrol-watch`): the road patrol reached its far point in 90 s
  with 2/2 hounds in formation; the lane patrol walked home with its follower (a watch that began AT the far point
  "passed" in 0 s - the leader must now be seen away first; `patrol-at` is the pre-pull wait).
  (4) **The balance group is SuperUI bots, never stock vmangos party bots** (owner, 2026-09-27: "use superui bots.
  Dont use partybots"). The first fair runs used `.partybot add` (inherited from the afternoon's plan) and an agent
  spent the evening patching PartyBotAI - all of it REVERTED (mangosd back to 80d5d2bc, PartyBotAI.cpp/.h = HEAD).
  Now: `tools/worldpack/make-group.py` creates the group (`/Bots/AddBots`: warrior tank, priest healer, mage, rogue by
  default; their SuperUI talent profile picks the role and SuperUI spends the talents), `gen-live.py --bots A,B,C,D`
  SETUP levels, trains, maxes skills, gears (level-39 premade set per class + role) and invites them, summons them
  into the dungeon and to every gathering spot (`.namego`); they fight on the tester's target (PlayerParty doctrine).
  AGENTS.md now says it plainly: the box is a test environment - make, level and gear SuperUI bots as a test needs.
  New runner steps: `pack-trial <spell> <s> <guid,...>` (the group takes a linked pack: CLEARED / WIPE / TIMEOUT, kills,
  adds, deaths), `patrol-watch` / `patrol-at`, `boss-trial ... pull` (the group gathered 30 yd out, the tester runs in
  and the group follows its target; before, the tester stood beside the boss alone while the bots caught up). `gen-live.py` `patrols` section and fair
  dungeon: every pull gathered 30 yd out in GM mode (SETUP), trash before the boss it is nearest to, bosses in level order.

- 2026-09-27 (late night): **minimaps follow the published ground.** The build used to map each stamped tile to its SOURCE
  image before sculpt, placements and the seam stitch had run, and a reshaped stock tile kept its stock image: the pass
  raised out of the sea (Azeroth 29,34) showed open sea, the block's edges stitched down into the ocean showed
  Silverpine forest, dropped buildings were still drawn. Now, after the final ADTs, every touched tile (stamps, sculpted
  stock tiles, tiles with placed buildings) is compared pixel by pixel with the ground its image shows; where it differs
  the pixel is re-rendered (texture-layer colours, hillshade, water by log depth, placed buildings as roofs, trees as
  shade) with the ground colours fitted to that tile's own Blizzard image and ONE sea model fitted to every touched tile
  plus the stock sea around it, feathered in; water in both keeps Blizzard's art unless the seabed dropped far (lake ->
  ocean blends). Build #32: 17 of 26 Gilneas tiles re-rendered, tier 1 0 errors. Preview without publishing:
  `WorldPackMinimapProbe` (before/after PNGs). Tests `WorldPackMinimapTests`.
  Also: the launch scripts are tracked (`tools/worldpack/launch-wb.ps1`, `launch-live.ps1`; live runs start in place),
  `make-group.py` makes a SuperUI group, `gen-live.py --setup` is one-time, the fair boss trial no longer fails on group
  loot rules, a patrol out of sight no longer fails `patrol-watch`/`patrol-at` (only the timeout does), L1 ignores
  SuperUI bots logging in inside a pack map. The handoff was rewritten as a current-state page.
- 2026-09-27 (stop): **the continent world map is wrong (owner-rejected).** The client pastes the stamped tiles' minimap
  images as a rectangle over Blizzard's painted Eastern Kingdoms map, and our land blocks Baradin Bay's western approach.
  Blizzard's art ALREADY paints a landmass below Silverpine (about ADT cols 27.2-30.0 x rows 34.0-37.7) that the hover
  map `Azeroth.zmp` leaves as The Great Sea, so it has no glow and no name. Gilneas must become that piece: land fitted
  to its outline, ZMP cells -> 7001, a generated `GilneasHighlight.blp`, the rectangle removed. Probe:
  `tools/worldpack/worldmap-probe.py`. Plan and the owner decision: WORLD_BUILDER_HANDOFF.md §3 item 1.

- 2026-09-27 (late-night continuation, historical build #33 checkpoint): **painted-outline map and coast pipeline
  implemented; live acceptance was pending at this checkpoint.** The owner directed work on the map and actual land and deferred the dungeon. The stamped-tile
  continent rectangle is removed. A traced Gilneas polygon is tracked in
  `tools/worldpack/examples/gilneas/worldmap-outline.json`; `WorldPackWorldMap` generates ownership/highlight/bounds
  and G16, while `WorldPackCoast` fits terrain to that polygon and G17. The concrete migration moves the authored
  land one tile west and two north, rehomes coastal content, keeps the Greymane approach fixed and recuts its pass.
  `fit-gilneas.py` checks its original snapshot and applies only through audited APIs. Nine migration requests
  have succeeded; `docs-after-mapfix.json` / `state-after-mapfix.json` are the frozen post-migration snapshots in
  scratch. Preflight: 0 errors, 19 warnings. This is authoring state, not evidence of a published new build.
  Final offline build probe: 0 errors, three existing G3 warnings; G9 checks 31 tiles with 0 cracks/normal drift;
  G17 checks 883,498 outer/inner vertices with 0 new dry exterior and 0 submerged interior.
  Focused map tests: 13 passed (polygon validation, mapping, multi-zone preservation, highlight serialization and
  asset audit); relevant web suite: 70 passed. Debug/Release client builds and world-builder/ImGui policy wire checks
  passed. New real-client QA uses mounted catalogs/GL art and ordinary hover input, with screenshot dumps
  of the plain continent and Gilneas glow/name. `gen-map-smoke.py` creates seven uninterrupted gate legs, settlement
  walks, coast observations and a shore-to-dock walk, then returns Gilnwar to Duskhaven and clears GM/pointer input.
  No dungeon replay, group reset or new party is part of this map-focused run. The final web app was deployed and
  **build #33 started**; tier-2 collision and live smoke were pending then. Build #32 was the last completed publication
  at that checkpoint; the following entries supersede that status.
- 2026-09-27 (after build #34): **the map, coast and gate pass are verified; the dock approach exposed a separate
  collision defect.** Build #34 published successfully (MPQ SHA1 `ebe8c6f4b931b0e912daefdd83a01fedb17559aa`), with
  tier 1 zero errors and six existing warnings (other test packs' props/building and the fixed gate's portcullis).
  The rebuilt server navmesh accepts all 72 overworld combatants; G9 has no cracks or normal drift and G17 has no
  exterior land spill or submerged interior. The full map-0 client collision pass checked 111 targets with zero errors;
  its changed-only follow-up checked six with zero errors after the outdoor harbor NPC was placed at its actual floor.
  Live Gilnwar smoke walked all seven consecutive gate/pass legs and the settlement routes, checked coast samples,
  and proved the mounted Gilneas ZMP ownership, name and highlight while surrounding stock zones remained intact.
  The plain continent and hover screenshots were inspected. The run ended with exactly two failures: both tried to
  cross the main dock's vertical side. This was not a fully accepted live run. Evidence: scratch
  `build34-status.json`, `build34-verify.json`, `logs/mapfix-tier2*.log`, and `logs/map-smoke-build34.log`.
- 2026-09-27 (dock correction, build #35 publication checkpoint): **an underside is not a deck.** The earlier
  `M2WalkableTop` audit used the absolute normal and selected the dock's underside at local Z=2.859 instead of its
  upward deck at Z=5.922. It now rejects downward triangles and measures the dominant upward plane with actual
  heights; G10 reports a ramp's full sloping span and requires two-way walking proof. The relevant web suite passed
  76 tests. A scoped pair of Move operations lowers dock #8 to a true deck of Z=3.059 and puts ramp #12 between dry
  shore and deck (rotation 180 degrees, scale 0.4). Raw collision triangles, including serialized MDDF scale, give a
  0.098 yd shore step, a 21.714 degree ramp, and 0.276 yd overlap with the deck. The offline build probe passes with
  no errors, preserving all NPCs and the fixed gate. `scratch/worldbuilder/dock-fit-plan.json` records the two full
  API bodies and the bidirectional center route; `dock-fit-plan-geometry.json` records 729 footprint samples.
  Build #35 was publishing at this checkpoint; the new dock had not yet passed live walking in both directions.
- 2026-09-27 (after the build #35 checkpoint): **restoring stock ground must also remove the source lake above it.**
  Independent binary review found 718 imported active water cells outside the polygon on Azeroth 30,34; 77 stood
  visibly above stock-dry ground. `CarryLiquidFrom` had skipped a dry baseline chunk and retained its source lake.
  The coast pass now clears only exterior cells absent from stock, preserving inland ponds and mixed boundary
  chunks. An entirely empty MCLQ becomes an eight-byte empty block, with offsets and liquid flags updated: clearing
  MCNK flags alone does not stop the client rendering the retained payload. G17 now compares exterior active cells
  and their four water heights with stock. Minimap sampling uses those same active cells, so a liquid-only removal
  triggers the existing wet-to-dry repaint instead of leaving blue lake art.
  The relevant web suite passed 79 tests. The rebuilt offline candidate has zero errors: G17 checks 883,498 terrain
  vertices and 308,772 exterior liquid cells without a mismatch; G9 remains clean. A separate raw-MCLQ comparison
  across all 31 rebuilt Azeroth tiles proves 718 exterior mismatches became zero, while every one of 130,076 inland
  active cells and 309,971 existing exterior stock-water cells retained its flags and water-height values exactly.
  The leaked cells at (-1239.583,535.417) and (-1397.917,664.583) are now dry, while the inland pond at
  (-1197.917,1602.083) retains its Z=32.930923 water. Evidence: scratch `coast-liquid-before-summary.json`,
  `coast-liquid-after-summary.json`, `source-liquid-leak-before.json`, and `dock-fit-plan-probe/report.json`.
  These are offline results; publication #36 and focused live dock/water acceptance remain the next checkpoint.

- 2026-09-28 (build #36 live checkpoint): **the water fix is published and confirmed in the client.** Build #36
  has zero verifier errors, six existing placement warnings and patch SHA1
  `94628c995ea9fef23afccc43e04c64a5b139446f`; the downloaded client file matches. The changed collision scan
  checked six targets with zero errors, only the existing Northshire hole warning. Both exterior probes report
  no liquid; the interior pond retains its Z32.930923 surface. The zone mosaic and normal continent hover pass.
  The live run still counted 11 failures: nine dock walk legs stopped at the same high-end bevel, and two standing
  assertions used a steep stock-terrain diagnostic point. The next protocol observes that second dry cell from
  the first stable point; its liquid reading was already correct. The dock bevel, rather than its walking plane,
  requires a buried entrance. No character-physics rules are changed to accommodate this placement.
  Visual zone-map review also identified a green stripe and dark sea pixels inherited byte-for-byte from stock
  `texture.MPQ` tiles 26,36 and 31,34-35. These are retained stock minimap-art limitations, not added terrain;
  evidence is `scratch/worldbuilder/minimap-art-review/`. A painted-style zone map remains deferred.

- 2026-09-28 (accepted, build #37): **the map and actual terrain correction are complete.** The final ramp Move
  142 places #12 at (-1468,2595.84,2.93218), heading 180, pitch 2, scale 0.4. Flattening the ramp to 19.714 degrees
  buries its high bevel in the beach and its low bevel beneath the main deck; 5,084 collision-face samples and an
  independent transform agree. The main deck remains at Z3.059. The full offline probe has zero errors, and the
  published verifier has zero errors/six existing warnings. Client patch SHA1
  `feaaea5693f748f0c92664d9f6153385f59b24c5` matches publication. The changed collision pass checks six targets,
  with all five harbor targets clean and only the old Northshire warning remaining.
  The final live run passes **all 96 steps, zero failures**: five outward and five return walk legs, uninterrupted
  across shore/ramp/deck with radius 0.5; both dry exterior liquid observations; the retained interior pond;
  zone mosaic7001 (35 tiles); mounted name/highlight and real pointer-driven hover. Forward/return/hover screenshots
  were inspected. The earlier seven continuous Greymane legs, four settlements, three coast checks and 25 map
  probes remain valid on the same terrain. All 79 relevant web tests and the client Debug/Release builds passed.
  Gilnwar finished alive, stationary and grounded in Duskhaven (-2525,2058.333,17.167658), GM/input off, existing
  party preserved. Both QA clients exited. Dungeon and camp-difficulty work remain deferred; nothing is committed.
  Evidence: `scratch/worldbuilder/build37-{status,verify}.json`, `logs/mapfix-tier2-build37.log`,
  `logs/dock-recheck-build37.log`, `dock-forward-build37.png`, `dock-return-build37.png`, `gilneas-hover-build37.png`,
  `dock-bevel-fit-plan.json` and `dock-bevel-fit-apply-journal.json`.

- 2026-09-28 (human editor pass): **World Builder now has task pages and explicit editing actions.** The pack,
  active tool and save feedback stay visible; Terrain, Buildings, NPCs, Quests and Publish replace the long
  stack of unrelated open sections. More holds maps, regions, checks and raw pack data. The window migrates an
  old undersized layout once and respects later resizing. Numeric fields show their values without cramped
  step buttons; object names are readable, with full model paths available as tooltips.
  Escape cancels tools even from a focused search field; unsaved strokes and patrols disappear, object moves
  leave the saved pose intact, and Ctrl+Z works after cancelling. Undo labels explicitly say "last pack edit".
  Sculpt now loads its published baseline without visiting Publish, starts on the first click, picks terrain
  rather than roofs, waits for every required tile, and rolls back failed saves. A separate final-surface layer
  preserves human strokes after coastline/path generation; legacy source-layer operations keep their meaning.
  Empty-layer output matches 31 build37 ADTs byte-for-byte, and a brush proof matches 67 outer and 76 inner
  vertices within 0.00000114yd. A real pointer-created stroke stayed at 9.50068yd after build38 download.
  Offline NPCs are visible and selectable. Editing a supported stock NPC clones its full template/services
  into a pack and redirects only the selected spawn at publication, with durable restore ownership. Shared
  vendor/trainer lists, equipment, hidden fields, stock quest links and gossip are preserved. Scripted/AI and
  original-entry quest-objective NPCs explain their current import limits. Pack NPC edits keep their ID.
  Quest editing preserves hidden data, supports named choices and multiple objectives/givers, validates the
  draft, and retains unsaved per-quest/per-pack work. The human QA reopened and edited the same quest ID.
  Maps use neutral defaults and allocated IDs; region relocation carries paths and world-map polygons and
  refuses unsupported patrol relocation before making changes.
  The temporary human-tools-qa pack passed build38 with 0 verifier errors and 6 existing warnings. Final
  pointer-driven editor QA passed; the live vendor run passed 40 steps with 0 failures, showing the renamed
  Godric at GUID79952 with all eight original shop items. Published comparison passed 29/29: stock template
  and other packs unchanged, only that spawn redirected. Gilnwar returned alive and grounded to Duskhaven,
  GM/input off; no party operations or purchases. Earlier exploratory camera/scroll failures remain in their
  logs and are not counted as clean runs. Pack 4 was then disabled and build #39 restored the original world
  (0 errors, 6 existing warnings). Restoration comparison passed 23/23; the client remounted #39 and measured
  the original 6.99955 yd terrain height. Lower, Smooth and Flatten changed preview terrain in the expected
  direction and cancelled exactly, leaving saved terrain unchanged. Smooth's tile-edge neighbor sampling was
  corrected and a planar-slope regression added. Final numeric-field layout was visually checked in the client.
  Source validation: web WorldPack/GradedPath 121 tests, NPC authoring 19 checks, WorldBuilder/ImGui/shared-docs
  checks and normal Debug/Release builds. Usage and current limits are in §4. No commits or pushes.
  Evidence: `scratch/worldbuilder/logs/human-tools-{initial,round2,round3,round4,vendor-published}.log`,
  `build38-human-status.json`, `surface-{zero,stroke}-proof-comparison.json`, and `npc-roundtrip/*-report.json`.
  **The continent highlight's bright border is still pending the owner's requested subtle-fill refinement.**

- **2026-10-07 - noggit-lite pass: Paths, Chests and props (client only; no web change, no publish).** The owner
  found the manual tools unusable for a human ("the tooling exists ... but it's not functional for a human using the
  manual tool"). Paths moved out of More to their own page: click-placed, draggable, insertable points instead of
  "add point where I stand"; centre/lane/blend lines drawn on the ground; a live terrain-preview layer computed with
  `WorldBuilderLaw.PathWeight` (the web GradedPath rule, wire-checked); saved paths listed, reopened, renamed (old key
  deleted in the same op) and deleted; unknown body fields such as `approach` are kept. New Chests page: stock
  templates from the read-only `/GameObjects/Search`, one reserved-range `dbrow:gameobject` row per click with a yaw
  quaternion (rotation2/3), model previews with the live renderer's transforms, edit/move/remove, and custom chests
  (`gameobject_template` copy from `/GameObjects/FullRow` with `data1` = its own entry as loot id, `data3` consumable,
  copper, `gameobject_loot_template` rows). Buildings: Buildings/Props filter, quick words, scatter rolls, up to four
  placements saving at once, X/Y/tilt fields, live draft preview, Duplicate, selection ring. Probe additions:
  `ui-page paths|gameobjects` and `ui-panel-size W H`. Real-pointer QA on the disabled `human-tools-qa` pack: path
  tool armed, three clicked points, drag, Escape, Save path (op 267), Delete path (269); Battered Chest placed by click
  (op 266, at the click with the expected quaternion), its saved preview rendered on the ground, selected from the
  list with the ring, removed (268). The pack ends with no path or gameobject docs. Not yet exercised live: the
  ground-preview layer (the QA client had no build mounted, so it correctly reported "terrain not loaded"), a
  published chest/custom chest on the live server, and road texture painting, which does not exist yet (paths only
  regrade the ground). Logs: `scratch/worldbuilder/logs/gui-{paths-6,paths-8,chest-1,cleanup-1,cleanup-2}.log`.

- **2026-10-14 - roads the human way: fly camera, ground grid, painted surfaces (client; web step pending).** The owner
  had run a pre-change client (the old "Add point where I stand" box) and asked for a fly mode, a visible grid
  selector and roads made from the game's own textures. Built: World Builder fly camera on top of Free view (own view
  angle `WbFlyPitchOverride`, starts 40 yd up, Free view box-selection suppressed while a World Builder action owns the
  left button, tools pick terrain to 900 yd); ground grid overlay + snap; the Paths page as four steps; a vanilla ground
  texture picker (`Tileset\<Zone>\*.blp`, thumbnails); road painting with `TerrainPaintLaw` (visible-weight paint,
  at most 4 layers - a fifth replaces the least visible - 4-bit alphas like MCAL), applied to the loaded tile and
  restored per tile when the road changes, the page closes or the tile re-streams. Grading is now optional per path
  (`grade`; old docs without it still grade). Real-pointer QA (`gui-road-5.log`): fly camera on, four clicked points,
  ElwynnCobbleStoneBase chosen in the picker, 6 chunks painted, screenshot shows the cobble road exactly between the
  lane lines (alpha orientation confirmed). **Saving a painted road is blocked by design** until the web app reports
  `State.roadSurfaceSupported`: today's build would grade every path doc and ignore the paint. Web contract (phase 2):
  path body `grade` (default true) and `surface {texture, width, softEdge, strength}`; after `ApplySurface` and before
  Write, paint every touched chunk with the TerrainPaintLaw algorithm (port it verbatim: weights, replacement,
  4-bit quantization, pixel centres from `PixelWorld`), append MTEX names, rebuild MCLY/MCAL, fix nLayers/sizeAlpha and
  the MCNK offsets after each splice; a no-op paint must keep the byte-identical round trip. Ground effects (grass) are
  not rebuilt by the client preview.

- **2026-10-14 (later) - road surfaces ship: web app phase 2, verified on the owner's LOCAL WSL stack.** Owner rule: World
  Builder deploys and QA use the local WSL instance (`mangossuperui.service`, http://localhost:5000), never 192.168.0.2.
  Web app (MangosSuperUI, uncommitted): `Services/WorldPacks/TerrainPaint.cs` (verbatim port of the client law),
  `AdtDocument.PaintTexture` + `ReplaceSubchunk` (MCLY/MCAL rewrite, nLayers, sizeAlpha = payload + 8 as stock files have
  it, MTEX append, offsets after each splice shifted), `GradedPath` `Grade`/`Surface`/`TouchedTiles`/`PaintSurface`, build
  step after `ApplySurface`, controller validation (Tileset\...\*.blp, grade:false needs a surface) and
  `State.roadSurfaceSupported = true`. Tests: `WorldPackPaintTests` 7 (law parity; no-op paint byte-identical; a strip on
  Azeroth_32_48 reads back at full weight while far chunks, heights, water, holes and props are unchanged; MTEX reused) and
  the full WorldPack/GradedPath suite 128/128. Deployed into WSL `/opt/mangossuperui` (backup
  `/home/wowvmangos/deploy/backups/auto-20261014T064227`). Real-pointer e2e on WSL pack `road-qa` (id 1): road drawn in the
  fly camera, ElwynnCobbleStoneBase picked, saved through Save path (`grade:false` + `surface`); the build logged
  `tile 0:32,48 painted ... (6 chunk(s))` - the same 6 chunks the preview painted - and the client, after downloading that
  patch-7, rendered the cobble road from the published ADT with no preview layer (`road-baked` shot). WSL setup done for
  this: the 14 stock MPQs copied to `~/worldpacks/work/Data`, `~/worldpacks/tools` -> `run/bin/Extractors`. **Open: WSL
  publishes stop at server extraction** - the WSL Core's MapExtractor has no `-m <maps>` option (a SuperUI patch present
  only on the box), so builds #2/#3 failed after building patch-7 and installed nothing server-side. Road surfaces need no
  server data (heights/collision/navmesh unchanged), but any other publish on WSL needs those extractor patches.
  **Resolved the same day:** the owner updated the WSL Core (`0453e1201 Various hooks for packs`; extractors rebuilt with
  the `-m` map filter). WSL build #4 then succeeded end to end: road painted (6 chunks), patch-7 sha1 2fa16fcf...,
  MapExtractor/VMapExtractor `-m 0`, VMapAssembler, navmesh for 25 tiles, 14 server files installed, scoped mangosd
  restart ("World initialized"), verifier 0 errors / 0 warnings (G13: 27 samples, steepest 14 degrees). The client's
  startup sync fetched build #4 and renders the road (`road-build4` shot).

- **2026-10-14 (evening) - human usability pass over every World Builder tool** (owner: "make sure that they are human
  usable in the form that we see here" - the Paths page as the model). Rule applied everywhere: nothing asks for typed
  coordinates or ids; a ground point is chosen with a pick button and shown as a world marker; destructive actions
  confirm; disabled buttons say why; long rows put their buttons on their own line (a long name pushed "Delete portal"
  past the panel edge). §4 lists the result per page. New shared file `GameLoop.Creator.WorldBuilder.Common.cs` (pick
  tool, markers, tile outline, confirm dialog, undo button); `...NpcPlace.cs` (cursor ghost, unsaved-changes guard,
  placement list, delete/restore NPC with dependents); custom chest form v2 in `...Objects.cs`. Bugs found by real-pointer
  runs: auto-placing after Create NPC was cancelled three ways (a script-only guard, the docs reload racing
  `WbValidateSpawnSelection`, and the post-save refresh of the same NPC resetting the page - `_wbNpcRefreshing`); a
  confirm modal drawn inside the page lost its clicks (now one top-level dialog with a fixed width). QA runs in the
  background: `MSUI_WB_BACKGROUND=1` (default in `launch-wb.ps1`, `-Visible` to opt out) creates the window hidden but
  still rendering, so test clients never take the screen or keyboard; the UI probe scrolls to an off-screen button and
  retries (`ERROR` after 4 tries). Verified on WSL pack `road-qa` by real pointer frames (scripts in
  `scratch/worldbuilder/pass-*.txt`): NPC create -> armed placement -> click -> list -> Remove placement -> Delete NPC;
  object place -> select in world -> Remove; portal arrival/entrance picks -> Save -> list -> Delete portal (ops #28/#29);
  Publish confirm -> Cancel (no build). Pack left with only the earlier `qa-cobble-road` path. Not yet clicked through by
  a script: quest Discover-a-place pick, building Level-the-ground, terrain flatten pick, region move.

- **2026-11-15 - fly camera, saved roads that stay, and road looks measured from the whole world (client + web app,
  uncommitted; built and unit-tested, not yet deployed to WSL or clicked through live).** Owner: the fly camera's right
  mouse only spun in place; a saved road vanished when another page opened ("Show saved paths" seemed broken); a cobble
  road "is the correct stone but not the right mix"; then "make sure we have full coverage of all the various
  roads/paths around the world".
  - *Fly camera.* `WbFreeCameraActive` = free view (however entered: Fly camera, top-bar Free view, Ctrl+F) with the World
    page open. Its tilt is free - the Command View angle knob does not apply (`WbFlyPitchOverride`/`_wbFlyOwned` are
    gone). Right-drag is an in-place free look (yaw + pitch, -83..83 deg about the camera itself: the rig is put back along
    the boom each frame, `WbUpdateFlyOrbit`), WASD/wheel/edge pan still fly while held; the View angle slider shows and
    sets the live pitch; PageUp/PageDown tilt directly. The Command View outside the World page is unchanged. Same
    camera, later that day: dark contour-shaped holes in the hills by Stormwind's gate were the Command View roof cut's
    PROXIMITY square (WmoRenderer.ResolveCutPlane: a building within 12 yd of the character -> a 30x30 yd square around
    them, everything above the cut height discarded, terrain included). The World Builder camera now feeds none of the
    "see the party" cuts (cutaway, roof cut + terrain leg, slice, sight tunnels/canopy, party sight); QA shot
    `gameplay-wb-cut-check` at the same spot shows the hills whole.
  - *Saved roads stay.* `WbRoadSpecsToPreview`: with "Show saved paths in the world" ticked, every saved surface of the pack
    on this map is painted on EVERY World Builder page (the route lines still only draw on the Paths page); the editor path
    paints live on the Paths page and its saved version elsewhere.
  - *Road looks* (`TerrainPaintLaw.RoadLook` / `RoadAmountAt`, verbatim in web `TerrainPaint`): `surface` gains
    `ragged` (yd the edge wanders), `patchy` (0..1, ground showing through), `seed` (the pattern, stored so the build
    paints the preview's pattern) and `shoulder {texture, extra, softEdge, strength[, patchy]}` - a second texture laid
    FIRST, `width + 2 * extra` wide, the road's wobble, `seed + 101`. Noise is seeded integer-hash value noise in world
    yards (continuous across tiles; no Random). All zero = the old plain band, so older docs paint unchanged. Paint order:
    per path shoulder then road; across paths by name (ordinal) in both the build and the preview. Web: `PathSurface`
    (`Look`, `ShoulderLook`, `Reach`), `PaintSurface` two passes, build loop ordered and checks the shoulder texture exists,
    controller validates shoulder/ragged/patchy, `State.roadLookSupported = true`. The client refuses to SAVE a look (ragged,
    patchy or shoulder) until the web app reports `roadLookSupported` - an older build would paint a plain band.
  - *Full coverage.* `tools/road-survey` reads every stock ADT of every terrain map (patch-7 excluded; the Development map,
    Emerald Dream and the GM islands left out), builds 512x512 visible-weight grids per texture per tile and measures road-like
    bands (width 2A/P 2-16 yd, >= 30 yd long, >= 5x wide): width, interior strength, soft edge from the fade profile,
    ragged from the raw/smoothed perimeter ratio, patchy from holes in the closed band, the shoulder as the texture most
    enriched just outside the band, the ground 14-16 yd out. Output: `MSUIClient/Data/road-styles.json` (embedded,
    `RoadStyleCatalog`) and `shared_docs/ROAD_STYLES.md`. First run: 20 maps, 2,155 tiles, **238 styles** (95 road, 142
    trail, 1 paving); every one of the 39 road-named ground textures visible in the world has at least one style (e.g.
    Elwynn: ElwynnCobbleStoneBase 4.2 yd on an ElwynnDirtBase2 shoulder +5.2 yd; Barrens: BarrensRoad01 3 yd on
    BarrensBaseDirt +5.2 yd; Durotar: DurotarRoad 5.2 yd on DurotarRock +4.2 yd). Ragged and patchy are estimates
    (described in the tool's header); the user can adjust every number after picking a style.
  - *Paths page.* "Road style" (searchable, this map first, grouped by map, tooltip with the measured numbers); "Copy a road
    from the ground" (pick tool: samples the STOCK textures under the click - a previewed tile is read from its snapshot -
    and applies the best-matching style, or lists the textures with Road/Shoulder buttons); "See it in the world" (flies to
    the longest measured stretch); Ragged edge, Patchy, New pattern; "Shoulder under the road" with its own texture, extra
    width, soft edge, strength; the shoulder's outer edge drawn in the overlay. The doc records `style` (+ `styleAdjusted`).
  - *Tests.* Client `interface-wire-check --world-builder-only` PASS (pinned `RoadAmountAt`/`Fbm`/`SeedFor` values, plain look
    == old band, catalog loads with stock textures). Web `WorldPackPaintTests` +3 (the same pinned values; parse of look +
    shoulder; a shoulder+road painted on Azeroth_32_48 reads back cobble on the centre line and dirt 4 yd out); WorldPack
    suite 128/128. Full web suite 612/613: `FleetReportTests.RenderDetailed_KeepsBothBotsWhenNamesCollide` fails, in code
    this change does not touch. Regenerate the catalog with `dotnet run -c Release --project tools/road-survey` whenever
    the stock data or the measuring rules change.
  - *Deployed and tested the same day (WSL).* Web app published Release and installed with `Scripts/deploy-auto.sh`
    (health check OK, `roadLookSupported: true`); `~/deploy/backups` is root-owned on WSL, so the run used
    `BACKUPS=$HOME/deploy-backups` (backups `auto-20261115T132007`, `auto-20261115T132807`). **Parity run** (scratch tool
    referencing both projects: web `GradedPath.PaintSurface` + `AdtDocument.Write` vs the client's own `WbRoadPasses` +
    `WbPaintRoadTile` by reflection, re-read with `AdtTerrainReader`, every chunk's visible weight per texture compared):
    the first run found **2 of 256 chunks differing** on Azeroth_32_48 - in a full (4-layer) chunk the road had to evict a
    layer, and (a) the least-visible choice summed alpha row/column 63, which the client reader overwrites with row/col 62
    while the web reads raw, so near ties went different ways, and (b) the road could evict the shoulder it was just laid
    on. Fixed in both copies of the law: visibility summed over rows/cols 0..62 only, and `PaintChunk(..., keep)` never
    evicts the kept layer (the road keeps its shoulder; `AdtDocument.PaintTexture(keepTexture)`, client
    `WbPaintRoadTile(keep)`); pinned by a new check in both suites. After the fix: **0 differing chunks, max weight
    difference 0** for six styles (Elwynn cobble, Barrens road, Durotar road, Stranglethorn cobble, Westfall dirt trail,
    Dun Morogh road) on their own example tiles; the only differences are in row/column 63, which every reader replaces.
    **Live QA** (`scratch/worldbuilder/road-looks.txt`, `road-looks-reopen.txt`, hidden client, own pack `road-looks-qa`
    created DISABLED so no publish includes it): Road style dropdown + search picked "The Barrens: road"; "Copy a road from
    the ground" on stock cobble at (-9345.1, 164.8) picked "Elwynn Forest: road (Elwynn Cobble Stone Base)"; a 4-point road
    saved as op #33 with the full look (`ragged`, `patchy`, `seed`, `shoulder ElwynnDirtBase2 +5.2`, `style`); it stayed
    painted on the Terrain page; reopening restored every value. Shots `dumps/gameplay-wb-looks-*`, `gameplay-wb-reopen-*`.
    Found and fixed on the way: the content region's horizontal scrollbar fed back into the layout (HorizontalScrollbar
    makes ImGui lay out at max(content, visible), so one overflow widened all wrapped text for good) - the content width is
    now set explicitly from fixed-width items only (`CreatorNoteItemRight`), with a 1 px margin for ImGui's own rounding;
    and the save message reported the levelling width (12 yd) for a painted-only road.
  - *Published (owner: "Publish it").* WSL build **#5** of `road-qa` (the only enabled pack; `road-looks-qa` stays
    disabled), `restartServer: false` (roads change only textures): succeeded in 14.6 min, patch-7 sha1 c929cbe5...,
    2,059,776 bytes, 383 server files installed, verifier 0 errors / 0 warnings. Build log: `qa-cobble-road` painted 6
    chunks on 0:32,48 and `testpathSWentrance` 4 chunks on 0:31,49 - the counts the preview paints. The published
    archive was downloaded to scratch (NOT into GameData - the QA rule) and checked with the parity tool's `--baked` mode:
    both touched tiles, painted on stock by the client's preview code in name order, equal the published tiles (0 of 256
    chunks differing, max weight difference 0). Both published roads are plain cobble (saved before road looks), so the
    styled look is covered by the offline parity runs above; the styled QA road lives in the disabled `road-looks-qa`.

## 9. Building a zone — the playbook (humans AND agents)

Owner rule (2026-09-27): every capability has two faces — a manual tool in Creator Mode for a human, and an
intent path for an agent (script command / API / generator) plus a CHECK that proves the result. Asked for
"another zone", an agent runs this table top to bottom; nothing is done until its gate is green. Both faces
go through the same code (the panel buttons and the script commands call the same methods; every edit is one
audited, undoable web-app op). Worked example: `tools/worldpack/examples/gilneas/` (the content batches that
built Gilneas: `make-world.ps1` = maps/tiles/subzones/lights/graveyards/portals, `make-content.ps1` = NPCs,
mobs, loot, gossip, quests, the dungeon; posted with the script command `content <file.json>`).

| step | human (Creator → World) | agent | gate |
|---|---|---|---|
| 1 land | Regions, seams & paths: "Stamp here" onto a continent (a ZONE - seamless) · Maps & dungeons: new map (an INSTANCE/raid only) | `stamp <src> <sc> <sr> <w> <h> <dc> <dr> [stitch] [area]` on map 0/1; `newmap` for instances; `relocate` moves a built region; `tile ... stitch 150` blends the block in | preflight C0/C10; G1 stamp fidelity; G9 no crack at the seams; G11 minimap |
| 2 subzones, lights, graveyards | Maps & dungeons: subzone paint | `subzone`, content batch (AreaTable/Light/WorldSafeLocs docs; SET the faction: AreaTable field 20 = area_template.team, 0 contested / 2 Alliance / 4 Horde - a clone inherits its source's) | C10 (incl. faction chosen + client = server); tier 3 `worldmap` minimap `pvp=` |
| 3 terrain | Sculpt brushes | `sculpt`, `pad <placementId>` | G9 cracks/normals; G4 buried walls |
| 4 buildings + props | Place (ghost, gizmo, catalogue) | `place`, `move <id> x y z\|ground [heading]`, `pad` | G2 clipping, G3 props inside, G4 floating, G5 road + squareness, G6 leftovers; `survey` → `tools/worldpack/contact.py` (review the sheet, then delete it) |
| 5 terrain holes | Verify: "heal" on a hole finding, "Heal all walk-offs" | `healhole x y` (tile doc `healHoles`); `healhole all` = every square the last tier-2 hole pass walked off into (`worldpack-holes.json` "voids", this map, one op) | G12; tier-2 hole pass: 0 errors (warnings = visible gaps the client guard holds) |
| 6 NPCs + services | NPC Creator | content batch (`make-content.ps1` pattern) | C1-C7 (gossip option per service!); tier 3 `npcs` |
| 7 mobs + spawns | Spawn NPCs: Single / Linked pack / Patrol | `spawn`, `spawnz`, `spawnring`; dungeons: `mobpack`, `patrol`, `movespawn` - plan every spot first with `tools/worldpack/navmesh.py check/draw` (MAIN component, open ground; bosses >= 40 yd from any trash point) | G7, G10, C11, G14, **G15 reachability**; tier 2 floor/ceiling/walls/roof/slope/water, `verify-spawns fix` repairs; tier 3 `patrols` (patrol-watch) |
| 8 quests | Quest Creator | `questui draft.json` | C8; tier 3 `quests` |
| 9 access + dungeon | Regions, seams & paths: carve a path (pass, land bridge, road) · Maps & dungeons: portal for an INSTANCE only | `carve <name> <w> <falloff> x y z|ground ...`; in-place stamps drop blocking props (`tile c r dropDoodads ...`); `portal` for dungeon entrances/exits | G13 path grade; C10 (instance ghost entrance on a continent), G8; tier 3 `paths` (walked on foot) + `dungeon` |
| 10 publish + client pass | Publish, Verify section | `python tools/worldpack/gen-wb-verify.py OUT --pack K --publish` → `powershell -File tools/worldpack/launch-wb.ps1 -Script OUT -Name <log>` (the build also re-renders the minimap of every tile whose published ground differs from its source image) | tier 1: 0 errors; `[verify-client] SUMMARY map N: ... 0 error(s)` for every map |
| 11 live | — | `python tools/worldpack/gen-live.py OUT --pack K --holes <client bin>/worldpack-holes.json` → `powershell -File tools/worldpack/launch-live.ps1 -Protocol OUT -Name <log> -Character <name>` (starts where the character stands; sections portals, paths, npcs, holes, quests, worldmap, patrols, dungeon) | `PROTOCOL_DONE failures=0`, or each failure explained in §8 |
| 12 level + balance | — | a fresh level-appropriate tester: `--character-select` run with `char-create <name> <race> <class> <gender>`, then `gen-live.py OUT --pack K --char <name> --fair <level> --sections quests,patrols,dungeon` (labelled SETUP: trainer spells of the level, quest abilities, items reset + bags, premade talents/gear, `.maxskill` proven by `assert-skills-capped`; stand-ups at full health; no god mode, real quest credit earned by `kill-for-quest`, the dungeon with a SuperUI bot group made by `tools/worldpack/make-group.py` and passed as `--bots`: levelled, trained, geared and invited by the SETUP, gathered 30 yd short of every pull, the tester opens) | every quest turned in with EARNED credit (`QuestKills` verdict per objective: pulls, adds, deaths, rests, seconds); every trash pull `PackTrial outcome=CLEARED` and each boss `BossTrial outcome=KILLED` - seconds, health left and deaths are the balance numbers |

Fast loops: `preflight` (seconds, unpublished docs), `verify-spawns changed` (only what moved), the
incremental navmesh (a publish that touched two tiles rebuilds two tiles). `verify rerun` re-runs tier 1 on
the server without publishing.

Diagnostics (script commands; each prints `[tag]` lines to the console log):
- `rayprobe x y z` — 5x5 grid of straight-down rays: what a falling body could land on (+ the terrain under the centre).
- `terrain x y` — where the client's ground comes from: supplying archive, the file's chunk heights vs the
  AdtCache's (a World Builder preview rewrites it) vs the height grid the controller stands on.
- `column x y` — every WMO face over a point, top to bottom, MOPY flags, kept/dropped by walking collision,
  plus the whole instance's walkable-face census per group/flag (is there a collision ramp at all?).
- `faces x y z r` — authored WMO faces near a point (WmoRenderer.DumpFacesNear).
- `walkprobe fx fy fz tx ty` — the player's own controller, off-screen: stand, run to the target, settle;
  traced every 3 frames (ground source, hole, grounded). The answer to "does a player fall here?".
- `probe`, `height`, `xray`/`xraynav` — terrain/collision/server-navmesh views (§6).
- Web: `WorldPackSpotProbe` (`MSUI_SPOT="map,x,y"`): terrain, props and WMO group boxes (world space, which
  one is over the point) and every hole of the map's tiles.

Rules learned the hard way (each is enforced where it can be):
- Never log the test account in twice: a script run no longer auto-logins; still, one live client at a time.
- Testwar leads the raid: quest kill/drop credit is impossible in a raid group (vanilla). Tier 3 proves camps
  killable and GM-completes as labelled SETUP; say so in any report. Quests are reset with `.quest remove`.
- Screenshots are evidence only while being read: `survey` → contact sheet → review → delete. Automatic dump
  families are capped (`AutomaticDumpFamilies`).
- Anything a player can WALK to must hold them: the hole/shell void guards are the client-wide safety net, but a
  pack should still heal the gaps it can (they are visible).
