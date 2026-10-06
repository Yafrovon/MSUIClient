# WoW Karting — racer carts, real portals, an Azeroth circuit

Owner mission, 2026-10-04. Design + as-built log. Read this before touching karting code or the
`wow-karting` World Content Pack.

## 0. What the owner asked for (decisions, 2026-10-04)

- **Karts are real server mounts**: the Mirage Raceway cars — Goblin Racer (creature 4251, display
  10318) and Gnome Racer (4252, display 2490). Speed **+200 %** (3× run, 21 yd/s).
- **The circuit is the real world.** Roads where a rider does not die instantly, joined by **Real
  Portals** (the August 2026 see-through, walk-through portals). The start is the Mirage Raceway in
  Thousand Needles (it should look like plain carting there). ~3 minutes a lap, 3 laps. Jumps: zoom up
  a hill or cliff and fly *into* a portal to another area.
- **Opt-in like Gilneas**: everything is a World Content Pack; a server without the pack enabled (and
  a client without its patch-7) is vanilla.
- **Portals are simply real**: usable by anyone, not race-gated.
- **No god mode.** A kart is a mount, not magic: it grants a **neutral status** (mobs do not aggro on
  a kart rider, bots included); falls, water and the world stay dangerous.
- **Race**: a race marshal NPC at the start hands out karts and queues racers; **SuperUI bots** race
  too; a true MK64-style **3, 2, 1, GO**, "WoWized", on the client; checkpoints in order, a lap counts
  only in sequence.
- **Then the items**: Mario Kart 64 items (banana, green/red/blue shell, mushroom ×1/×3/golden, star,
  lightning, fake item box, Boo, item boxes on the track) with their effects copied 1:1 — built from
  WoW models and spell visuals, no Nintendo assets — in a carting UI.
- The never-player-accessible mount list (115 aura-78 spells, researched 2026-10-04) is kept for later;
  nothing is done with it now.

## 1. What already existed (research, 2026-10-04)

| Piece | Where | State |
|---|---|---|
| Client ride override, look/feel tuning, presets 10318/2490/15381 | `GameLoop/Scene/GameLoop.Mount.Toolkit.cs` | built Aug 2026, owner used it |
| Client cart kit (6 slots, charges, `NoteMountKitToken` pickup seam) | `GameLoop/Scene/GameLoop.Mount.Kit.cs` | built Aug 2026, plain ImGui bar |
| Mount rendering from `UNIT_FIELD_MOUNTDISPLAYID` | `World/Units/CreatureRenderer.Mounts.cs` | any server mount renders; rocket cars drawn **3.16 yd** off the unit (root bone bakes `(-3.16, 0, 0.89)`) |
| Real Portals (window + walk-through + destination preload, opcodes 844-847) | client `GameLoop/Scene/GameLoop.RealPortals.cs`, `World/Portals/*`; Core `SuperUiContent/SuiWorld/Bridge/SuiPortal.cpp` | only the **six summoned stock Mage portals** are accepted on both sides |
| World Content Packs | `shared_docs/WORLD_BUILDER.md` | Gilneas is the reference pack |
| Mirage Raceway cars in stock vmangos | creature guids 21680/21682, EventAI speed re-roll | they never actually race |

Core facts that shape the design:

- A mount is aura 78 (`HandleAuraMounted`, misc = creature entry → display); speed is aura 32
  (`HandleAuraModIncreaseMountedSpeed`).
- `Player::TeleportTo` keeps the mount. The only map-change dismount is entering a map where
  `!IsMountAllowed()` (every instance except ZG/ZF/AQ20) — **the circuit stays on maps 0 and 1**.
- Area triggers fire while mounted (`MiscHandler.cpp` has no mounted gate).
- Spells live in `spell_template` (Spell.dbc is client-only) in a vector indexed by id
  (`SpellMgr::mSpellEntryMap`, sized max id + 1) — pack spells use a **low reserved range**, not 7,000,000.
- GO type 22 (spellcaster): data0 spell, data1 charges, data2 partyOnly, data3 allowMounted, data4 large.
  `GameObject::Use` sets `GO_FLAG_LOCKED` on use.

## 2. Architecture

| Layer | Owns |
|---|---|
| **Pack `wow-karting`** (data) | kart spells (`spell_template` + `Spell.dbc` clone), portal teleport spells + `spell_target_position`, portal `gameobject_template` (type 22, partyOnly 0, allowMounted 1, large 1) + spawns, the marshal NPC, the course |
| **Web app** (`MangosSuperUI`) | pack-writable `spell_template` / `spell_target_position` and `Spell.dbc` in the pack spell range |
| **Core** (`SuperUiContent/SuiWorld/Karting/`) | public pack portals in Real Portals; kart neutral status; the race (marshal, queue, grid, countdown, checkpoints, laps, results, bot racers) — dormant unless the pack's data is present |
| **Client** | pack portals as Real Portals; the rocket-car offset as a renderer default; the 3-2-1-GO countdown and race HUD; later the item UI |

Reserved ids (pack law, `WorldPackContent`): templates 7,000,000+; spawn guids 1,500,000-7,999,999;
**pack spells 60,000-64,999** (stock tops out at 50,025).

## 3. Phases

1. **Ride** — pack spell support; the two kart spells; renderer offset default; verify live: mounted
   look, 21 yd/s, no rubber-banding.
2. **Portals** — public pack portals on both sides; one portal pair; verify live: window, walk-through
   (and drive-through at speed) keeps the kart, both continents.
3. **Loop** — lay the circuit (Thousand Needles start → … → back), measure ~3 min/lap, drive it live.
4. **Race** — marshal, queue, grid, 3-2-1-GO, checkpoints/laps/results, neutral status, bot racers.
5. **Items** — MK64 item set, item boxes, carting UI.

## 3b. Phase 5 design - the Mario Kart 64 items

Mechanics copied 1:1 from MK64; everything you SEE is WoW (models, spell visuals, sounds - no Nintendo assets).
Server-authoritative (Core `SuiKartingItems.cpp`); the client draws the item window and its roulette and sends "use".

| MK64 item | Effect (1:1) | How it is built |
|---|---|---|
| Item box | drive through: roulette, then an item; the box breaks and is back in ~2 s | summoned creature (box model) at course `box` points; per-racer pickup by distance |
| Banana / bunch (5) | dropped behind; a kart hitting one spins out (~1 s) | summoned creature where dropped; hit by distance |
| Green shell / triple | fired forward (or back, holding back): straight line, bounces off walls, hit = tumble (~1.5 s); triple orbits until fired | summoned creature moving at shell speed (MovePoint along its line, bounce on blocked path) |
| Red shell / triple | homes on the racer ahead | summoned creature re-targeting the next racer each tick |
| Spiny (blue) shell | flies along the racing line to 1st place, blows up there and hits anyone near | summoned creature following the course line to the leader |
| Mushroom / triple | short big boost | pack aura: speed + boost visual |
| Golden mushroom | unlimited boosts for ~7.5 s | item state on the server; each use = mushroom |
| Star | invincible + faster ~7.5 s; ramming spins others | pack aura (glow) + server immunity + contact check |
| Lightning | every OTHER racer shrinks (slower, spins, drops its item) for a time by place; a full-size kart squashes a shrunk one | pack aura: scale + slow; strike visual |
| Fake item box | dropped; looks like a box; hit = tumble | summoned creature (box model, tell-tale tint) |
| Boo | invisible + intangible ~5 s, steals a random opponent's item | pack aura (translucent) + server immunity + item transfer |

Item odds by place follow MK64's shape (front: bananas, green shells, fake boxes, Boo; middle: red shells,
mushrooms; back: stars, lightning, triple reds, golden mushrooms, the spiny shell). The race manager owns items,
boxes and projectiles; bots fire shells at a racer ahead, drop bananas when someone is close behind, boost at once.
Wire: SMSG_SUI_KART kind Item (roulette, held item, uses left); CMSG action 5 = use (param 1 = backwards).

## 4. Tools (human + agent paths)

| Tool | What |
|---|---|
| `tools/worldpack/examples/karting/course.json` | THE course: legs (map, route, checkpoints, ramp, lanes, portal, arrival), start grid, laps, speed |
| `tools/worldpack/examples/karting/gen-karting.py OUT [--post]` | course → pack `wow-karting` docs (kart spells, portal spell/target/template/spawn, ramps + cleared lanes); `--post` also deletes docs the course no longer generates |
| `tools/worldpack/examples/karting/gen-kart-live.py OUT [--laps N] [--leg N] [--kart 38001]` | tier-3 protocol: drive the course for real (`drive-through` into every portal) |
| `tools/worldpack/kart-course.py view/draw/measure` | minimap views with the route, lap length/time |
| `kart-course.py trace-road MAP x0 y0 x1 y1 TEXTURE [pad] [via]` | **the racing line**: A* along the road the game PAINTS (ADT texture layer, e.g. `DeadwindPassShale`); `road` renders a layer. The minimap-colour `trace` cannot tell grey road from grey rock |
| `kart-course.py props COURSE [radius]` (`MSUI_STOCK=1` = without patch-7) | ADT props (trees, rocks, roots) near each route: a kart stops dead on a trunk the heights never show |
| `tools/worldpack/navmesh.py route` | a walkable line on the server navmesh (clearance-weighted); bots path on it anyway |
| live runner `kart-assert` / `drive` / `drive-through` | `GameLoop/Dev/GameLoop.LiveRun.Karting.cs`; a failed drive dumps a screenshot |
| verifier G18 | public Real Portals: one destination row per teleport spell, a reachable window, arrival on the ground (G7 skips floating pack portals) |
| verifier G13 | now also the CROSS slope of a lane; `"grade": false` paths are cleared lanes (props removed, ground untouched) |

## 5. As-built log

- 2026-10-04: research + this document.
- 2026-10-04: **Phase 1 + 2 built and verified live** (Kartwar, a SuperUI test character on the test account):
  - Web app: pack-writable `spell_template` / `spell_target_position` (ids [38000, 40000)), `Spell.dbc` rows shipped
    ROWS-ONLY as `WorldPacks\Spell.dbc` (patch-3 owns the real file; `SpellCatalog.Load` merges it).
  - Core: `SuiKarting` (neutral kart riders: `CallAIMoveLOS` skips hostile sight-aggro on a rider with the 'KART'
    dummy aura); `SuiPortal::IsPublicPackPortal` (placed type-22 pack portals, not party-only, pack teleport spell)
    accepted by Real Portals and never locked by `GameObject::Use`.
  - Client: pack portals in Real Portals (`IsPackPortalEntry`, `PortalPrewarmLaw.IsPackPortalIdentity`); an
    unready crossing holds at most 0.75 s and never holds an AIRBORNE body (a kart off a ramp fell out of the window);
    rocket-car root offset cancelled by MEASUREMENT (goblin (-3.16, 0.89, 0); gnome ~0 - a shared constant put it 3 yd
    sideways); the cars' built-in NPC drivers hidden (`MountTuning.HiddenSubmeshes`: goblin submesh 2, gnome 4-10).
  - Live: karts 21.00 yd/s server-granted (avg 21.0 measured); raceway → Elwynn → Deadwind portals crossed mounted;
    Elwynn → Deadwind was a SEAMLESS Real Portal (prepared world promoted, no loading screen); the first crossing of a
    lap still takes the loading curtain (map 0 not prepared in the ~7 s a kart covers in 150 yd) - Phase 4 prewarms the
    next leg's destination.
  - Lessons: neutral status is not immunity - a creature already fighting a rider dazes and DISMOUNTS it (vanilla
    Dazed); test SETUP mounts before teleporting. Terrain heights never show props: trunks and boulders stopped the
    kart until `props` + cleared lanes. A narrow GRADED lane on the ~4-yd ADT grid made a 42-degree facet (G13 now
    measures cross slope; lanes are ungraded).
- 2026-10-04 (build #48): **a full lap driven live, failures=0** (`gen-kart-live.py --laps 1`, Kartwar on the goblin
  kart): raceway + ramp jump 87.0 s → Elwynn 48.1 s → Deadwind 37.4 s = **172.5 s**. Elwynn → Deadwind and Deadwind →
  raceway (continent change) were SEAMLESS Real Portals; the ramp jump → Elwynn still takes the loading curtain.
  Open: the Deadwind ravine-lip jump (the kart stops at (-10898,-1959) although props/GOs/WMOs/server terrain are
  clear) - the portal stands on the road before the crest until a client collision probe explains it.
- 2026-10-04: **Phase 4, the race, built and raced live.** Core `SuperUiContent/SuiWorld/Karting/SuiKartingRace.cpp`
  (`SuiKarting::Tick` in `World::Update`, `SuiKarting::TickRacer` at the top of `AiBotAI::UpdateAI`, the marshal's
  gossip `npc_sui_kart_marshal` registered from `AddScripts()`), CMSG/SMSG_SUI_KART 878/879 (NUM_MSG_TYPES 880,
  capability bit 15, `docs/SUI_WIRE_PROTOCOL.md` on the box), client `Net/KartingWire.cs` + `GameLoop/Hud/GameLoop.Karting.cs`
  (`/kart join|leave|start|status`; LAP/PLACE/TIME panel, next-checkpoint arrow, results, MK64 3-2-1-GO with a three-lamp
  starter, MapPing beats and a goblin steam whistle on GO; Daisy the Race Starter Girl yells it in-world).
  - Course data = pack game_tele rows `kart:azeroth:{meta,grid,cp,line,portal}` (gen-karting.py `course_items`); the
    Core loads them at the first tick ("[KARTING] course 'azeroth': 3 laps, 8 grid slot(s), 7 checkpoint(s), 3 leg(s)").
  - The marshal Zippa Sparkcog (pack creature 7100000, Pozzik's model) by the start line: a kart, join, or "start
    now" (fills the grid with racer bots).
  - Racer bots: SuperUI AiBotAI characters (Gearspark, Boltzap, Fizzwick, Cograttle, Sprocka, Turbonk, Nitrocog),
    logged in per race; they drive each leg's racing line (MovePoint + pathfinding at kart speed x skill 0.92-0.98).
    `GameObject::Use` on a pack portal does NOT move a bot (unexplained; a client's use does) - the bot is teleported
    to the portal spell's own spell_target_position instead (logged "direct teleport").
  - The next leg's portal destination is pushed (SMSG kind Prewarm) and warmed through the Real Portals cast-prewarm
    slot (`RealPortalCastPrewarm.Placed`): **all 9 portal crossings of a 3-lap race were seamless**, the raceway jump
    included.
  - Live (Kartwar, race 2, build #49 + Core): joined → 6 racers on the grid → 3-2-1-GO on the second → laps at 3:33 and
    6:35 → "You finish 1st!" 9:04.4, protocol failures=0. Fixed on the way: a racer crossing continents is briefly out
    of the world (FindPlayer missed it and the race dropped its only human); 1.12 caps FontHeight at 32, so the big
    countdown digit is a SCALED frame.
- 2026-10-04: **Phase 5, the MK64 items, built and raced live** (design in 3b). Core item engine in
  `SuiKartingRace.cpp` (RollItem by rank, Hit with 1.2 s immunity and Star/Boo shields, FireShell: green follows the
  racing line, red homes on the racer ahead, spiny targets the leader across maps; DropHazard; UseItem incl.
  lightning shrink+spin+drop, Boo steal, golden mushroom 7.5 s; bots use items too). SMSG kind Item = u8 item,
  u8 uses, u16 rouletteMs, u16 goldenMs; CMSG action 5 = use (param 1 = backwards). Pack content (gen-karting.py
  `items`): effect spells 38201-38206, item box / banana / fake box GOs 7100110-12, shell creatures 7100010-12,
  24 `kart:azeroth:box:NN` tele rows (four boxes across the road at 0.4 and 0.75 of every leg).
  - Client: item window beside the LAP panel (WoW icons, 12 Hz roulette with MapPing ticks, uses count, golden
    clock), the **Use Kart Item** binding (default F; hold S = backwards) and `/kart use [back]`.
  - Live (build #50, 0 verifier errors, Kartwar + 5 bots): the player picked up and fired green shell, fake box
    (backwards), bananas; bots fired shells, lightning (everyone else shrunk; a shrunk racer was squashed), fake
    boxes; all hits logged as "[KARTING] race N: X hit by ...". Protocol: `gen-kart-live.py --race --items --laps 3`.
  - Open: the scripted test driver (not the race) crashed into the Deadwind rock wall at (-10471,-1732) on lap 2
    after a 0.5-0.7 s frame hitch at tile [35,51] (driver-flush-at-imgui) and never recovered, so that run's human
    did not finish; the client has no spin/tumble visual of its own for the 38202/38203 auras yet.
- 2026-10-04: **Seating fixed, and the custom go-kart.** The baked-origin correction cancelled the goblin car's
  WHOLE root translation (-3.16, 0.89, 0) - the 0.89 is render-Y height, the lift that stands the car on its
  wheels - so the car sank a yard into the ground and the rider floated over a half-buried seat (owner report).
  `BakedMountOriginCorrection` now cancels the horizontal part only. The gnome car's upright tower is its real
  bind-pose design (body to z 6.1, driver geometry at the bottom), not a bug.
  - The Racing Kart (spell 38003, creature 7100020, display 71000, model row 7100): an MK64-style go-kart built
    by `tools/worldpack/examples/karting/kart-model.py`, a from-code vanilla M2 writer (sequences 0/4/5 with
    spinning wheels and engine shake, attachment 0 = the seat inside a tub whose skirts hide the rider's legs),
    textured only with stock goblin art (RocketCart01 atlas: red planks, shark-mouth nose, XXX plank, skull hex,
    pennant; TNT-wagon wheels; Shadowfang riveted metal).
  - Pipeline (web app): pack doc kind `asset` (key `Creature\SUI*\<file>.m2|.blp`, body base64, MD20/BLP2
    magic checked) ships files in patch-7; CreatureDisplayInfo (ids >= 71000) and CreatureModelData (>= 7100)
    are pack DBCs, written to the client AND merged into mangosd's OWN stock copies (`ServerBaseDbcs`: the
    server's tables are a different extraction - never copy the client's over them); `creature_display_info_addon`
    is pack-writable (>= 71000). Unit::Mount refuses a display mangosd's DBC lacks.
  - Core: the marshal offers "a Racing Kart" first; joiners without a kart and every racer bot get 38003.
  - Live: build #54, 0 verifier errors; ridden at the raceway (side/front/low captures), a race started with
    6 racers on go-karts. `gen-kart-live.py` now defaults to `--kart 38003`.
- 2026-10-04 (evening): **Portal glitches fixed, verified frame by frame; course v2.** The owner watched runs the
  log called "seamless" and saw the ground vanish and trees blink. Evidence first: `GameLoop.FrameRecorder.cs`
  (live `record-crossings <name>` / `record-now <why>`: 1.5 s before + 6 s after every crossing, 640 px frames +
  frames.csv with terrain/GL state; `tools/worldpack/frame-sheet.py` makes contact sheets) and `MSUI_GL_DEBUG=1`
  (`Engine/GlDebugOutput.cs`, KHR_debug errors with the managed stack). Three client bugs, all in the Real Portals
  world promotion, not karting:
  1. Floor gone = every terrain draw REJECTED (GL_INVALID_OPERATION "program texture usage"): the party-sight
     samplers (units 6-9) were only bound by a PartySightPass, which a prepared world never has, so they sat on
     unit 0 with the tileset array. `PartySightPass.BindInactive` now runs for every terrain/WMO/doodad draw
     without a pass, and promotion hands the pass over (`CopyPromotedRendererTuning`).
  2. Trees vanishing ~4 s later = the first tile crossing re-faded the whole world: the prepared doodad/WMO
     renderers were built with AppearFade off (no fade keys), switched on at promotion. `MarkPlacedOpaque()`.
  3. World churn on arrival = the next leg's warm started on the arrival frame (same map): held 8 s
     (`KartWarmSettleSeconds`). Plus: a Denied portal descriptor no longer drops a PLACED warm (retry 0.5 s) and
     Core `PORTAL_PRELOAD_RADIUS` is 200 (the client tracks to 180, a 21 yd/s kart outruns its last movement packet).
  - Karts lost their mount on Stormwind's roofs: the city WMO counts as INDOORS and the spells (Swift Mistsaber
    clones) were SPELL_ATTR_ONLY_OUTDOORS - cleared on 38001-38003. Tyres: generated chevron tread BLP
    (`kart-model.py tread_blp`, pack asset `Creature\SUIKart\SUIKartTread.blp`).
  - **Course v2** (owner: 2 laps, shorter, bursty, more portals, real jumps, Stormwind rooftops): raceway start
    straight -> new launch ramp into a portal -> Stormwind's outer wall walkway over the Valley of Heroes (z 120)
    -> off its NW edge into a mid-air portal -> Elwynn road -> crest ramp into a portal -> Westfall cobblestone
    road -> portal to the raceway. 1,223 yd/lap, 4 portals. Route points may carry z ([x, y, z]: `leg_z` in
    gen-karting.py) and `jumps` (route indices: the test driver presses jump, `drive` waypoint "x,y,j").
    Portals stand 2-3 yd past a ramp lip (10 yd out a launched kart is already under the window).
  - Rooftop survey: live `roof-scan x0 y0 x1 y1 step name` (collision height + RENDERED WMO height `zr`, skipping
    MOPY material 0xFF collision-only faces - Stormwind's roofs carry INVISIBLE collision planes at z 114 that a
    kart drives on in mid-air), `tools/worldpack/roofmap.py` (height map), `roofgraph.py` (roof pieces + jumps),
    `roofpath.py` (A* over visible drivable cells). Stormwind's pitched roofs give <= 45 yd flat runs; the wall
    walkways are the long drivable "rooftops". The wall runs into the mountain past (-8975, 320).
  - Verifier: G18 now accepts a portal over a terrain hole (city floor) and one hung beside a building surface.
  - Live: build #59, 0 errors; all 4 crossings clean in the frame bursts (no GL errors); full 2-lap race, all 6
    finished (Gearspark 1:53.0 ... Kartwar 2:18.7), protocol failures=0. Bot fix: racer bots take a portal within
    12 yd (they stalled at the wall edge, the portal hangs past it).
- Course v1: Mirage Raceway (start/finish west straight, chicane, north turn, east straight, ramp jump) → Elwynn road
  → Deadwind Pass road (painted-road trace) off the ravine lip near Karazhan → raceway. 3,643 yd ≈ 173 s/lap.
