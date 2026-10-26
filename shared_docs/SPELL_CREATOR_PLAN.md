# Spell Creator Plan — from visual workshop to a full in-world spell builder (2026-10-25)

Owner, 2026-10-25: *"We have the spell workshop in the client. It is not sufficient ... how to
fully build spells within the creator, alla a custom blender/3d tool that exists within the
gameworld ... casts a slow moving ball of lightning at a given spot (player chooses the landing
spot) ... this should be able to build ANY spell that's feasible within the rules of the vanilla
world."* Then: *"I want us to do this here, no subagents, and I want a full plan that you can
execute on without asking me."*

This is that plan. It is the EXECUTION authority for the Spell Creator: phases, steps, files,
acceptance, and the decisions already taken so nobody has to ask again. The design documents it
builds on stay authoritative for what they describe: `SPELL_CREATOR_IDE.md` (the stage, clock,
gizmos, patchers), `SPELL_SKETCH.md` (the Sketch compiler and window), `SPELL_IDE_MAP.md` (the
field inventory, census and task walk). When a step here lands, its rows in those documents move
the same day, and its line in §9 here gets a dated entry.

## 0. The diagnosis (2026-10-25 audit, code-read across all three repos)

A spell is four things. The workshop owned one of them.

| Layer | Owner before this plan | State found |
|---|---|---|
| LOOK — models, particles, textures, sounds, kits | client workshop | strong for re-tuning Blizzard art; weak for new art (flat cards only, no texture animation, no trails/sparks on sketches, no copy-from-another-spell, first-key-only tracks) |
| RULES — targeting, radius, effects, auras, timing, cost | web Completer | thin: always a clone of a source spell; 6 effect / 7 aura types; targets, radius, trigger, attributes inherited; the client Spell.dbc mirrors only part of `spell_template` (targets/radius/trigger/cooldown silently diverge) |
| BEHAVIOUR OVER TIME — when each part happens | vmangos Core | missiles delay only per UNIT; everything at a ground point (persistent area, trigger, summon) happens at CAST time and area targets are picked at cast time |
| PLAYBACK — the game showing what was built | client runtime | ground cursor + dest cast packet + straight missile + dynobj area: present. Gaps: a ground missile with hits flies one missile PER UNIT; nothing plays at the ground point on arrival; no camera shake / world effect / SMSG_PLAY_SPELL_IMPACT; CharProc types other than 0,1,9,11,12,14 ignored; bone flag 0x04 ignored on character skeletons; spell catalogs never reload |

And the pipe between them leaked: a compiled Sketch M2 is never exported
(`RefreshSketchModelDoc` makes Original == Working, so `BuildCreatorSpellEntry` ships no bytes);
sketches are not saved (no `sketches` block, no reopen); the Completer refuses a spell with no
source spell and skips one whose source visual is 0; its store is keyed by spell NAME inside the
ephemeral `wwwroot`; testing needs a hand-copied MPQ, a client restart and a server restart.

Real templates that already do the ground-missile shapes (read-only `spell_template` query):
Flare 1543 (speed 5, `targets` 0x40, two PAAs at implicit 28), Fiery Burst 13900 (speed 30,
ground missile → one burst, A=16), Spice Mortar 31364 (ground missile + TRIGGER_MISSILE 32 to a
second spell), Flamestrike 2120 (damage + PAA), Blizzard 10 (channelled PAA, aura 3 at 1000 ms),
Might of Ragnaros 21154 (missile whose impact SUMMONS at the spot).

## 1. Decisions (taken; do not re-ask)

| # | Decision | Why |
|---|---|---|
| D1 | **Authoring lives in the workshop.** The Completer becomes the BUILD SERVICE (it keeps its web form, but every field the workshop authors arrives in the session and wins). | the owner's "within the creator"; one surface, one preview |
| D2 | **"Within the rules of vanilla" is a checkable law** (§2), enforced by a validator before export, not a feeling. | "ANY spell that's feasible" needs a definition of feasible |
| D3 | **Core gets ONE generic timing primitive: deferred destination**, gated per spell by a new custom flag `SPELL_CUSTOM_DEFERRED_DEST = 0x4000` (the first free `Custom` bit, `SpellDefines.h:963-977`). With the flag, a spell with speed > 0 and a destination applies its destination effects (PAA, summon, trigger/trigger-missile, send-event) and fills its area-at-dest targets when the missile ARRIVES: delay = dist(caster, dest) / speed, the same law units already use (`Spell.cpp:902-912`). Stock spells keep stock behaviour (flag off). | without it no spell can wait for its missile to land; per-spell C++ is forbidden by D2; the stock 1.12 client does not care when the server applies effects, so it stays vanilla-legal |
| D4 | **A thing that MOVES and ACTS on its own (a rolling ball that zaps along its path, a totem, a lingering orb that casts) is a summoned ACTOR**: an invisible creature with a visual aura and EventAI, summoned by the spell. A thing that only sits and ticks is a persistent area aura. | that is how Blizzard built every such spell in 1.12 (Might of Ragnaros, totems, Inferno) |
| D5 | **New index rows are allowed.** When no existing SpellRadius / SpellCastTimes / SpellDuration / SpellRange row fits, the Completer appends one (ids from 1000 up) to BOTH the server DBC dir and the client patch. Otherwise the nearest existing row is used. | the tables are data; both sides read the same files |
| D6 | **Every "+" copies from a real spell first** (emitter, ribbon, layer, sound, rules, whole recipes); blank numbers are the fallback, not the start. | owner 2026-09-19: "from existing of course" |
| D7 | **Persistence is the session JSON.** The same serializer is the save file, the push payload AND the undo snapshot (§5 P3.7). | one format, round-trip tested once |
| D8 | **Testing live is one button** (P0.7): push → build → client hot-mounts the patch and reloads its catalogs → server `.reload spell_template` → `.learn` on the online character. | the loop was five manual steps and two restarts |
| D9 | **Bots and characters for live tests are SuperUI test characters made for the test** (AGENTS.md test-environment rule); the raid roster is never borrowed. | standing rule |
| D10 | **The lightning ball is the acceptance spell** (§8). A phase is not done until the part of the ball it owns works live. | the owner's example |

## 2. The vanilla-legality law (D2)

A spell may be exported only if all of these hold; the validator (`Creator/Spells/SpellLegality.cs`,
pure, P1.2) reports each as a line with the field that broke it.

1. **Client art**: every model is a v256 M2 both readers accept (`M2Reader.Parse` + the four
   creator parsers, and the web `M2Reader` in the lab), every texture a BLP2 the client decodes,
   every DBC row the 1.12 client's own layout (field counts as `SpellVisualCatalog` /
   `SpellCatalog` read them).
2. **Server rules**: at most 3 effects (`MAX_EFFECT_INDEX`); effect ids only from the 134 with a
   handler (`SpellEffects.cpp:58` table, non-`EffectNULL`/`EffectUnused`), aura ids only from the
   193 with a handler (`SpellAuras.cpp:62`); implicit target ids only from `SpellDefines.h:23-88`;
   every effect × target pair from the compatibility table the generator extracts (P1.2);
   index fields name a row that exists (or one this build adds, D5).
3. **Core primitives**: only generic, data-gated behaviour (D3's flag, EventAI, DB scripts).
   No per-spell C++.
4. **Agreement**: the Spell.dbc row the patch carries and the `spell_template` row the server
   loads agree on every field both read (P0.5's column map is the definition).

## 3. Target architecture

```
WORKSHOP (client, Creator Mode)                     BUILD SERVICE (web Completer)            CORE
 SPELL                                                                                        
  ├─ RULES (new)  target · timing · cost · effects ──► rules block ──► spell_template ◄──────── loads
  │               recipes "like Flare"                               Spell.dbc (column map)   D3 flag
  │               legality law · rings on the stage                  new index rows (D5)
  ├─ TIMELINE     Precast → Cast → Missile → Impact → Area → Channel/State   (Anatomy = outliner root)
  │   each PART:  animation · sound · slots · server-timing band
  │    └─ SLOT → MODEL (Blizzard M2 tuned, or SKETCH compiled) ──► models[] + tintedBlps[] ──► patch MPQ
  │         └─ pieces (3D primitives, UV/flipbook) · emitters · ribbons · bones · keys (dope sheet)
  ├─ CHILDREN     trigger / trigger-missile / periodic-trigger spells, summoned ACTORS (D4)
  └─ SESSION      save = push = undo snapshot (D7) · reopen · Test live (D8)
```

## 4. Working rules for executing this plan

- AGENTS.md stands: never commit/push/branch; build the client Debug AND Release on every code
  change; `interface-wire-check --shared-docs-only` and `--imgui-policy-only` green; web app
  `dotnet build` + `MangosSuperUI.Tests`; Core builds on the box in `~/vmangos/build`.
- Deploys are authorised (AGENTS.md test-environment + Core mission grants): Core install +
  scoped restart of the vmangos `mangosd` screen session (never the CMaNGOS one); web app via
  `MangosSuperUI/tools/deploy-webapp.ps1`. Check nothing live depends on the server first
  (`pgrep -a mangosd`, online characters) and restart between, not during, tests.
- Probe first (rule 8): `msui-console.log`, `~/vmangos/run/bin/Server.log`, the lab. Every new
  law gets a lab check (`tools/spell-emitter-lab`) or a `MangosSuperUI.Tests` test — extend those,
  never start a third harness.
- Visual claims need frames (memory: verify-visuals-with-frames): a filmstrip from the lab
  rasterizer offline, a frame burst from the running client live. Delete dumps once used.
- SQL stays read-only for me; all DB writes go through the web app's normal paths.
- Each step ends with: builds green, its check green, the docs moved, a dated line in §9.

## 5. The phases

Each step: **what**, **where**, **done when**. Steps inside a phase run in the order listed.

### P0 — The pipe (nothing built from scratch survives until this lands)

| Step | What | Where | Done when |
|---|---|---|---|
| P0.1 | Sketch-compiled models always ship their bytes (a model with no archive file behind its path is `Authored`; `BuildCreatorSpellEntry` includes it and writes `m2Base64` unconditionally; its BLPs ride `tintedBlps` reason `sketch`) | `GameLoop.Creator.Sketch.cs` `RefreshSketchModelDoc`, `GameLoop.Creator.Session.cs`, `CreatorModelDoc` | an exported New-spell entry with one crescent carries the M2 bytes and its BLP; lab asserts it |
| P0.2 | `sketches` block: a pure serializer for `SketchDoc` (enums by name, version field) | new `Creator/Sketch/SketchJson.cs`; Session export | lab: serialize → parse → compile gives byte-identical M2 |
| P0.3 | Reopen: "Saved spells" in the workshop lists `spell-session.json` entries; opening one rebuilds the doc (new or derived), recompiles sketches, restores composition + audio, and mounts each shipped `m2Base64` as that model's baked original | Session.cs (`OpenCreatorSessionEntry`), Spells.cs | close the client, reopen, open the entry: same pieces, same preview |
| P0.4 | Completer from zero: `sourceSpellId 0` builds from a documented template row (`SpellCreatorService.FromZeroTemplateSpell`) with every rules field overwritten; `SpellVisualCloner` creates a fresh SpellVisual row when the source visual is 0/unknown; `RebuildUnifiedPatchFromConfigsAsync` no longer skips visual-0 sources | MangosSuperUI `SpellCompleterController.cs`, `SpellCreatorService.cs`, `SpellVisualCloner.cs`, `PatchController.cs` | Tests: a from-zero design yields spell_template + Spell.dbc + SpellVisual + kit + effect rows |
| P0.5 | Full Spell.dbc ↔ spell_template column map, used for every rebuild (all fields the client reads: targets, range, radius, implicit targets, trigger, recovery/category cooldowns, attributes, interrupt flags, power, reagents, durations, cast time, speed, visuals, strings) | new `Services/SpellServices/SpellDbcColumnMap.cs`; `PatchBuilderService` | Tests: for every stock spell, mapping its spell_template row reproduces the stock Spell.dbc row on every mapped field (mismatch report is the evidence) |
| P0.6 | Durable, collision-free store: Completer bytes move from `wwwroot/images/textures/custom/{name}` to a persistent data dir keyed by design id | `CompleterStore.cs` | two designs with the same name both survive a web app redeploy |
| P0.7 | Test live (D8): `/SpellCompleter/TestLive` = build → patch bytes → `.reload spell_template` → `.learn <id>` on the named online character; the client's workshop button pushes, downloads the patch, hot-mounts it (`MpqMount`), reloads `SpellCatalog` / `SpellVisualCatalog` | web controller + RA; client `Net/SpellPushClient.cs`, `GameLoop.ActionBars.cs` catalog load → `ReloadSpellCatalogs()` | a sketched spell goes from the workshop to a castable spell on a live test character with no restart (Server.log + client frame burst) |

### P1 — Rules in the world (the "what it does")

| Step | What | Where | Done when |
|---|---|---|---|
| P1.1 | `SpellRules`: the pure model of the data half (target mode, range, cast/channel time, duration, cooldowns, cost, school, speed, custom flags, attributes subset, 3 effects with type/aura/targets A/B/radius/points/dice/amplitude/misc/trigger/chain); filled from Spell.dbc for derived spells (`SpellCatalog` reads the extra columns) | new `Creator/Spells/SpellRules.cs`, `Formats/SpellCatalog.cs` | lab: every stock spell → SpellRules → row reproduces the mapped fields |
| P1.2 | The legality data: a generator reads the Core tree (effect handler table, aura handler table, implicit-target switch) over ssh into a tracked `MSUIClient/Formats/Data/vanilla-spell-rules.json`; `SpellLegality` validates a `SpellRules` against it (§2) | `tools/spellvis/spell_rules_extract.py`, `Creator/Spells/SpellLegality.cs` | every stock player spell validates; seeded illegal fixtures fail with the right line |
| P1.3 | Index catalogs: SpellRadius / SpellCastTimes / SpellDuration / SpellRange readers; a picker shows the value in yards/seconds and "nearest existing" vs "new row" (D5) | `Formats/SpellIndexCatalog.cs` | lab reads all four; Flamestrike's indices show 5 yd / 3 s etc. |
| P1.4 | The RULES part: first row of the outliner; inspector cards TARGET · TIMING · COST · EFFECTS(3); each field has `(?)`; "like …" copies a field group from any spell | new `GameLoop.Creator.Rules.cs`; `GameLoop.Creator.SpellIde.cs` | a new author finds range, radius and damage without help |
| P1.5 | Stage gizmos for rules: range ring around the caster, radius ring (or cone) at the preview point, the preview point itself | `GameLoop.Creator.Gizmos.cs`, `SpellEmitterGizmoLaw` | rings match the numbers (lab arithmetic + a frame) |
| P1.6 | Recipes: Bolt (Fireball), Ground missile (Flare / Fiery Burst), Burst + linger (Flamestrike), Rain (Blizzard), Chain (Chain Lightning), Melee strike (Cleave), Self buff, Channel (Drain Life). A recipe = rules + anatomy plan + recommended slots, copied from the named spell | `Creator/Spells/SpellRecipes.cs`; Anatomy presets join it | each recipe, built from zero, validates and previews |
| P1.7 | Export `rules` block; Completer applies it to spell_template + Spell.dbc (P0.5 map) + new index rows (D5) | Session.cs; web controller + `PatchBuilderService` | Tests + live: Flamestrike rebuilt from the recipe matches stock on every mapped field and burns live |

### P2 — The ground point (Core + client fidelity)

| Step | What | Where | Done when |
|---|---|---|---|
| P2.1 | D3 in the Core: `SPELL_CUSTOM_DEFERRED_DEST`; at cast, area-at-dest effects are not filled and destination effects are not executed; a dest moment = dist/speed is scheduled; at arrival the area targets are filled and every dest effect executes; `[SUI-SPELL] deferred-dest` log line with the measured delay | `src/game/Spells/Spell.cpp`, `SpellDefines.h`, `SpellEntry.h` | live: a speed-5 ground spell's damage and PAA appear at arrival, log delay = dist/speed ± one tick |
| P2.2 | Client: a spell whose target mask has DEST_LOCATION flies ONE missile to the destination whatever the hit list; on arrival the impact kit plays at the point (ground impact) | `GameLoop/Combat/GameLoop.Casting.cs:184-225` | live: Iron Grenade into a pack = one grenade, one burst |
| P2.3 | SMSG_PLAY_SPELL_IMPACT handled (kit on a unit); Core sends it for units hit at a deferred arrival | client `GameLoop.Net.cs` dispatch + Casting; Core Spell.cpp | live: units hit at arrival flash their impact kit |
| P2.4 | Creator preview of the ground point: a "pick point" mode clicks the lattice floor; the missile flies there with the release-event timing; impact and area play at the point; the server's timing is simulated (deferred or not, from the rules) | `GameLoop.Creator.Spells.cs` `SpawnCreatorMissile`, `World/Units/SpellEffectSource.cs` | preview of the ball matches the live cast in a side-by-side frame burst |
| P2.5 | AREA joins the anatomy: SpellVisual f11/f12/f13 (area gate, area effect model, area kit) are a part with slots and a sketch, previewed for the rules' duration, exported in `composition.area`, cloned by the Completer | `GameLoop.Creator.Anatomy.cs`, `Composition.cs`, web `SpellVisualCloner` | a new spell's lingering area shows in preview and live |

### P3 — The modelling layer (the "Blender" half)

| Step | What | Where | Done when |
|---|---|---|---|
| P3.1 | Track model: one abstraction over every animated track (emitter scalars + enable, colour/alpha/transparency, bone rot/trans/scale, ribbon colour/alpha/visibility, UV anim) with read keys / insert / move / delete / set value; writer obeys the TRACK LAW (`SPELL_SKETCH.md` §4.3) and relocates arrays at EOF | new `Creator/M2TrackPatcher.cs` | lab: insert/move/delete round-trip through both readers on real fixtures |
| P3.2 | Dope sheet: while paused, the timeline grows a lane per track of the selection; drag keys, add key at the scrub time, delete; curve view for one track | `GameLoop.Creator.SpellIde.cs` timeline, new `GameLoop.Creator.Timeline.cs` | a Cleave rate ramp edited key by key previews and exports |
| P3.3 | Cross-file transplant: copy an emitter / ribbon / mesh layer (record + its tracks + its texture entry + material) from any donor M2 into any target M2, re-targeted to a chosen bone | new `Creator/M2RecordTransplant.cs` | lab: each preset of `SPELL_SKETCH.md` §4.5 copied into a sketch re-reads identical scalars to its donor |
| P3.4 | S5: trails and sparks on sketch pieces (the presets via P3.3); "+ from any spell" library browser over the census for emitters/ribbons/layers in every model | `SketchWriter` (post-compile transplant), `GameLoop.Creator.Sketch.cs`, new `GameLoop.Creator.Library.cs` | a trail follows a slash; embers pour off it; +Em works on a sketch model |
| P3.5 | 3D primitives: Sphere, Cylinder, Cone, Torus, Beam (cylindrical billboard) as low-poly meshes with UVs, beside the 2D shapes | `SketchModel` (`SketchShapeKind`), `SketchWriter` geometry | lab: vertex/triangle counts, bounds, normals; filmstrip shows them |
| P3.6 | Texture animation on pieces: UV scroll (speed u/v), UV rotate, flipbook (atlas rows × cols at a frame rate = stepped translation keys); archive atlases selectable | `SketchWriter` texture-animation block, `SketchModel.Look` | lab: the UV track round-trips; filmstrip shows the scroll |
| P3.7 | Universal undo (D7): every card, dial, composition and sketch edit pushes a doc snapshot (debounced per gesture); Ctrl+Z / Ctrl+Y | `GameLoop.Creator.Handles.cs` undo → doc-level stack | every edit kind undoes; 64 deep |
| P3.8 | Piece hierarchy + motion modifiers: parent a piece to another piece; Orbit (radius, rate); Jitter (seeded random translation/rotation keys at a rate: flicker, arcs) | `SketchModel` / `SketchWriter` bones | lab: child follows parent; jitter deterministic per seed |
| P3.9 | Sound picker over existing SoundEntries (search, preview, pick), and audio for a part of a new spell (kit created on demand) | `GameLoop.Creator.Spells.cs` audio, `Formats/` SoundEntries reader | a new spell's cast plays an existing Blizzard sound live |

### P4 — Chains, actors, engine extras

| Step | What | Where | Done when |
|---|---|---|---|
| P4.1 | Child spells: an effect of type trigger / trigger-missile / periodic-trigger can point at "new child", which is a second spell doc in the same design, edited in place; exported as a group; the Completer assigns ids together and patches the references | rules + session (`children[]`); web controller | Spice-Mortar shape built from zero: ball lands → child explosion |
| P4.2 | Actors (D4): effect SUMMON at the destination with a workshop-authored actor: invisible creature (display, scale, visual aura = a state kit of a child spell), lifetime, EventAI (every N s cast child at nearest enemy within R; optional move to the point) | rules ACTOR card; web: creature_template + EventAI rows in a reserved range (the World Pack table policy) | live: a lingering orb zaps a test bot every second for 6 s |
| P4.3 | Engine: camera shake (kit f14 → SpellEffectCameraShakes.dbc), kit world effect (f12), bone flag 0x04 on character skeletons (`M2Animator`), next CharProc types by census order | client runtime | each plays on a Blizzard spell first, then gets an IDE dial |
| P4.4 | IDE dials for kit extras: camera shake picker, CharProc tint/alpha/rate on State | Composition rows | a new spell shakes the camera on impact live |

### P5 — Proof that it can build ANY spell

| Step | What | Where | Done when |
|---|---|---|---|
| P5.1 | Visual round trip (`SPELL_IDE_MAP.md` §1.4, slice R): for every spell with a visual, derive → export → run the Completer's cloner offline (linked source, like `WEB_M2READER`) → diff the DBC rows and bytes | `tools/spell-emitter-lab` with `LAB_ROUNDTRIP=1` (`LAB_ROUNDTRIP_SHOW=<visual>` for one diff) | report "N of M lossless" with the field list for the rest |
| P5.2 | Rules round trip: every `spell_template` row → SpellRules → row, diffed; legality over all player spells | lab + Tests | report published in §9 |

## 6. Order of execution

P0 (all) → P2.1 + P2.2 (the Core risk early, while P1 is designed) → P1 (all) → P2.3–P2.5 →
P3.1 → P3.3 → P3.4 → P3.5 → P3.6 → P3.8 → P3.2 → P3.7 → P3.9 → P4 → P5. The lightning ball is
re-attempted live at the end of each phase and its state recorded in §8.

## 7. Risks and how they are handled

| Risk | Handling |
|---|---|
| `.reload spell_template` updates existing rows only, or misses caches | P0.7 probes it on a fresh id first; fall back to a scoped mangosd restart inside TestLive with the reason in the response |
| The deferred-dest change disturbs delayed unit spells | the flag gates it; stock spells never set it; Server.log line on every deferred arrival |
| A new SpellRadius row id collides with a stock id | ids ≥ 1000 (stock max is far below); the validator checks |
| The client hot-mount serves a stale catalog | `ReloadSpellCatalogs` re-reads the mount and rebinds action-bar spell infos; checked by a probe line |
| Track writes corrupt files silently | TRACK LAW + lab round trip through both readers on every patcher |

## 8. The acceptance spell: Ball Lightning (D10)

Recipe "Ground missile" → rename "Ball Lightning". RULES: target Ground, range 30 yd, cast 2.0 s,
speed 6 yd/s, deferred destination ON, effect 1 SCHOOL_DAMAGE nature at implicit 16, radius 8 yd;
effect 2 TRIGGER_SPELL → child "Ball Lightning Field" (PAA, aura 3 nature, 1 s ticks, 6 s, radius
6 yd) — or P4.2's actor. TIMELINE: Precast (ReadySpellOmni, hands crackle) → Cast (SpellCastOmni)
→ Missile (sketch: Sphere with a UV-scrolled lightning texture + a camera-facing glow + jittered
arc ribbons + spark motes) → Impact at the point (burst + camera shake) → Area (crackling ground
ring for the field's duration). Live check: cast at a point 24 yd away; ball takes 4.0 s; damage,
impact and field appear on arrival; bots in the radius take nature damage per tick.

| Phase | Ball state |
|---|---|
| start | not buildable (no rules authoring, no deferred dest, no sphere/UV/sparks, sketch lost on export) |
| **ACCEPTED (2026-10-26)** after P2.3 + P4.3/P4.4 | **every line of the §8 spec, live** (Magetest, `--live-protocol`, cast 24 yd at the Northshire kobold camp): precast kit 10008 plays ReadySpellOmni (52) with `BallLightning_Precast_LeftHand/RightHand` (Lightning_Cast_Hand) crackling; cast 1.9 s, SpellCastOmni (54); server `deferred-dest launch dist=24.00 speed=6.00 arrivalMs=4000`, `arrival atMs=4009 units=4`; client GroundImpact 4.05 s after GO; damage 130/140/123/126 on arrival, the authored impact sketch (`BallLightning_Impact_Base`: swelling flash + Crackle sparks + 3 jittered arcs) on the spot AND on each of the 4 units (`DeferredImpact`), the camera jolts (War Stomp's shake #3, peak 0.10 yd), the field (`[dynobj-fx] spawn ... radius=10`) crackles with Call Lightning shards. Ball in flight: scrolling LightningBall sphere + Lightning4 shell + sparks + 2 flickering arcs. Field ticks on survivors: proven in P2.5 (this pack died to the blast) |
| after P3.8 (2026-10-26) | **the ball arcs**: two Beam pieces wearing `Spells\Lightning3.blp` (the blue horizontal bolt) flicker through 50 degrees twelve times a second (stepped jitter, own seed each) through the sphere, sparks pour off it; Test live (patch-3 73cf3682): live the ball crackles across the pack to the spot, 125/135/123 on arrival, then the field. Left: impact burst on the units hit + camera shake (P2.3 / P4.3) |
| after P3.3 + P3.4 (2026-10-26) | **the ball crackles**: Lightning Streak's own spark emitter copied cross-file onto the sphere (Crackle preset, tinted blue, 60/s, x5 size, 3 yd/s), Test live replaced #40030 again: live, the ball drops onto the spot trailing cyan sparks, 129/129/130 on arrival, then the field. Left: arcs (P3.8 jitter or a Beam piece), impact burst + camera shake (P4.3) |
| after P3.5 + P3.6 (2026-10-26) | **the ball is a ball**: a Sphere wearing `Spells\LightningBall.blp` scrolling round and over it (0.5, 0.25 /s), a larger Sphere shell wearing `Spells\Lightning4.blp` scrolling the other way, a glow shell - built in the workshop, Test live replaced #40030 in place, cast live into the kobold pack: the 3D ball lands on the spot (frame burst), 126/129/130 on arrival, then the field. Left: sparks/arcs (P3.3/P3.4), impact burst + camera shake (P4.3) |
| after P2.5 (2026-10-26) | **the mechanic is complete and live**: ball (sketch disc) flies 16 yd at 8 yd/s to the picked spot; on arrival 123-137 nature to the pack AND a 6 s persistent area (Call Lightning bolts as its shards) placed then - not at cast - ticking 5/s on a survivor (PeriodicReceive ×2). Left: the ball's own lightning art (P3), impact burst + camera shake (P4.3) |
| after P1.1-P1.4/P1.6/P1.7 (2026-10-26) | built from the workshop alone: New spell → recipe "Ground missile" → a glowing disc sketch as the missile → Test live → #40031 cast live into a kobold pack: the disc flies to the spot (frame burst), damage 128/136/130 (authored 120-140) lands on arrival. Still missing: lightning art (sphere, flipbook, sparks, arcs - P3), nature school / field / zaps (P4) |
| after P0 + P2.1/2.2 (2026-10-25) | the RULES are castable live: a from-zero spell set to ground target, speed 6, nature damage at the point, deferred - cast 14 yd into a kobold pack, damage landed on arrival (+2.5 s), not at cast. Rules still set through the raw spell editor (P1), no ball art yet (P3) |

## 9. Progress log

- 2026-10-25 — plan written after the four-layer audit (§0). Execution starts at P0.1.
- 2026-10-25 — **P0 BUILT and proven live.**
  - Client: `ComposeCreatorModelBytes` split out of `RebuildCreatorModel`; sketches ship one-shot
    bytes + `reason=sketch` BLPs (`BuildSketchExport`); a recompile now re-applies the model's
    dials (they were silently dropped). `Creator/CreatorJson.cs` (one serializer),
    `Creator/Sketch/SketchJson.cs` (`sketches`), `GameLoop.Creator.Design.cs` (the `design`
    block: capture/apply, the "open" button on every saved entry). New spells export
    `sourceSpellId 0` + `designId` + `localSpellId`.
  - Web: `SpellDbcColumnMap` (134 columns, derived from data, re-proven live by
    `/SpellCompleter/ColumnMapCheck`: 22,119 of 22,357 stock spells agree on every column,
    the rest are vmangos data corrections in effectItemType/targets). It replaced three wrong
    mirrors: school MASK written into the school NUMBER field, base/spell level swapped, and
    dieSides "+1 in the DBC" (vmangos rolls base + irand(1, dieSides); the Completer now writes
    max-min+1 to both sides, JS `pointsFromSql` agrees). From zero: template 18282 "Dummy Spell"
    (`SpellCreatorService.FromZeroTemplateSpell`), a zero SpellVisual row in the cloner, the
    rebuild no longer skips visual-0 sources, and the builder reads the Completer's stored
    bytes BEFORE the archives (a sketch model has no archive file and was dropped). Store →
    `App_Data/spell-completer/{entry}` with the legacy wwwroot read fallback. Test live:
    `ReplaceEntry` (replace in place), school −1 = inherit, `/Patch/SpellPatchInfo`.
  - Client Test live: `Net/SpellPatchClient.cs` (startup sync of patch-3 like patch-7;
    `[spellpatch]` line), `Net/SpellTestLiveClient.cs`, `GameLoop.Creator.TestLive.cs` (button,
    install, `spell-test-request.json`, `spell-test-entries.json`), `GameLoop.SpellTestLive.cs`
    (live watcher: re-mount, `ReloadSpellCatalogs`, `.reload spell_template`, `.learn`).
  - Proof: lab 37,567 checks (sketch json: 3 docs byte-identical round trip); web tests
    599/600 (the one failure, `FleetReportTests.RenderDetailed_KeepsBothBotsWhenNamesCollide`, is
    outside this work) + 4 new `SpellDbcColumnMapTests`; `MSUI_SPELLPIPE_PROBE` 16/16 against the
    deployed web app: New spell → Disc sketch → #40029 built from zero (Spell.dbc row, visual
    10002, cast kit with the sketch in Base, effect row → `Spells\ProbeBall_Cast_Base.m2`, the
    BLP in patch-3); live client (Magetest, `--live-protocol`) learned #40029 from the request
    within seconds and cast it: SMSG_SPELL_START/GO, cast kit 10007 presented.
  - Found live and fixed: replacing patch-3 while another client holds it → "Access denied";
    now falls back to a versioned copy `Data\spellpatch\patch-3-<sha>.MPQ`, named in the request.
  - Not yet seen: the disc on screen in the LIVE client (the dump came after the 0.8 s effect
    ended) - P2's frame bursts cover it.
- 2026-10-25 — **P2.1 + P2.2 BUILT and proven live.**
  - Core (`~/vmangos`, uncommitted; trail `tools/core-patches/sui-deferred-destination.patch`):
    `SPELL_CUSTOM_DEFERRED_DEST = 0x4000`; `Spell::cast` clears the cast-time targets before
    SPELL_GO (it carries no hits) and schedules arrival at max(5, dist)/speed;
    `handle_delayed` → `ArriveAtDeferredDestination` refills the targets and runs the ground
    phase then; `[SUI-SPELL] deferred-dest launch|arrival` lines. Built, installed (mangosd
    96dcbb4f, prev kept as `mangosd.prev-d76b5918`), scoped restart; the 147 bots that were
    online were logged back in from the recorded list (`/Bots/ConnectBot`).
  - Client: a TARGET_FLAG_DEST_LOCATION spell with speed flies ONE missile to the point whatever
    the hit list; arrival plays the area sound, the impact on each listed unit, or - with none
    listed - the impact kit AT the point (`SpellEffectSource.SpawnGroundKit`, `GroundImpact` verdict).
  - Live (Magetest): 12 yd → planned 2000 ms, arrived 2019 ms, units 0; 14 yd into a kobold pack →
    planned 2337, arrived 2506 (map update granularity), units 3, three SpellDamageReceive 2.5 s
    after GO. Found: damage 99 instead of 100 - vmangos adds effectBaseDice to the roll and the
    from-zero template has 0; every Completer points write now sets effectBaseDice = 1.
  - Open: P2.3 (impact kit on units hit at a deferred arrival) - no vanilla packet carries it
    that this core sends; revisit with the per-unit approach in P4.3.
- 2026-10-26 — **P1.1-P1.4, P1.6, P1.7 BUILT and proven live.**
  - `Creator/Spells/SpellTemplateColumns.cs` (the client's copy of the 134-column map, labelled),
    `Formats/SpellRuleTables.cs` (raw Spell.dbc rows + SpellCastTimes/Duration/Range/Radius with
    every field and plain-words text), `Creator/Spells/SpellRules.cs` (values vs baseline; the
    `rules` block = changed columns only; SetPoints writes base dice 1), `SpellLegality.cs` +
    `Data/vanilla-spell-rules.json` from `tools/spellvis/spell_rules_extract.py` (Core handler
    tables by POSITION - effects 132/133 have no entry at all, aura 192 has no comment; 112/134
    effects and 176/193 auras implemented; 1,127 stock combos with counts and an example).
  - Workshop: **Rules** is the first outliner row (`GameLoop.Creator.Rules.cs`): recipes (Bolt,
    Ground missile, Burst + linger, Rain, Chain, Melee strike, Self buff, Heal, Channel - each a
    real spell's rules), TARGET / TIMING / COST & SCHOOL / EFFECTS ×3 cards with pickers by name
    and use count, "copy from any spell" by card, the legality lines, every column (advanced).
    The rules travel in the entry and in the `design` (reopen restores them).
  - Completer: `rules.columns` → spell_template overrides after every form field (D1); push accepts
    a rules-only design.
  - Found and fixed on the way: (1) `JsonValue.Create(object)` in the rules block crashed the
    first session save; (2) in-memory JsonValue<long> refused TryGetValue<double>, so an
    in-process reopen restored no rules (and the probe's Test live then pushed none);
    (3) **Core:** `.reload spell_template` never re-ran `AssignInternalSpellFlags`, so a spell
    created after server start had AllowedTargetMask 0 and the passive anticheat rejected its
    ground cast ("unexpected target mask (64)", Anticheat.log) - the reload handler now rebuilds
    them (mangosd 1e7aa83c, in `tools/core-patches/sui-deferred-destination.patch`); (4) a
    MISSILE sketch exported one-shot and faded out after 0.5 s of a 6 s flight - invisible in
    the live frames; missile sketches now always loop and ignore fades (lab pins both).
  - Proof: probe 22/22 (recipe passes the law with no error, rules block exact, reopen
    identical); live: #40031 created after the restart learned and cast; 5-frame burst shows
    the disc crossing 18 yd at 3 yd/s; arrival 6017 vs 6016 ms planned.
  - Not yet: P1.5 rings on the stage, new index rows (D5) in the Completer, the Rules UI seen
    on screen (built headlessly; the UI capture is the next probe step).
- 2026-10-26 — **P1.5 + P2.4 BUILT, seen on screen; the Rules UI seen on screen.**
  - `GameLoop.Creator.GroundPreview.cs`: a ground-targeted spell previews to a SPOT (15 yd ahead,
    or "Pick the spot" = one click on the ground, in the world-click chain), the missile flies
    there at the RULES' speed (`CreatorMissileSpeed`), the impact plays AT the spot
    (`PresentSpellEffect(..., groundPoint)` → `SpawnGroundKit`), the area hold stands there;
    rings on the stage: range (blue, SpellRange max) around the caster, cross + blast radius
    (amber, largest effect SpellRadius) at the spot.
  - Seen in frames (probe `ui=1;preview=1`): the IDE with **Rules \*** first in the outliner,
    recipes, TARGET/TIMING cards; the ball crossing 15 yd at 3 yd/s into the amber 10 yd ring.
  - Found on screen and fixed: (1) every sketch recompile re-selected the sketch model in the
    inspector - the author's Rules selection was yanked away (`SelectSketchPiece(takeInspector)`);
    (2) the Sketch window froze its FirstUseEver position on frame one, ON TOP of the inspector
    (both unreadable) - now docked beside it every frame; (3) the Sketch MOVE card's "yd/s" in
    To-target mode edited a dead sketch-only field ("20 yd/s" beside a spell flying at 8) - it now
    edits the spell's rules speed.
- 2026-10-26 — **P2.5 BUILT and proven live; the Ball Lightning mechanic complete.**
  - The AREA part (`GameLoop.Creator.Area.cs`): centre model (any model, picker slot "area") +
    scale, shards = one of the client's seven type-9 shard models + rate; previewed at the spot,
    sized by the rules' radius, and for a deferred ground missile only from ARRIVAL for the rules'
    duration (`CreatorAreaWaitsForArrival`); exported as `composition.area`, saved in the design.
  - Completer: `AreaComposition` → SpellVisual 11/12 (centre effect row) and 13 (area kit, lane 0
    type 9 [selector, rate]). Found in the cloner: a kit it CREATES (zero row, or another stage's
    row as template) carried four CharProc lanes of type 0 = CHAIN_CHANNEL (empty is -1), plus the
    template stage's sound/shake/world effect - `BlankKitExtras` now empties them.
  - Live: #40030 = burst (effect 1) + persistent area aura (effect 2, periodic damage at 28,
    radius 10, 1 s ticks, 6 s); client `[dynobj-fx] spawn ... spell=40030` at the arrival moment,
    the bolts striking in frames, PeriodicReceive 5 at +1.1 s and +1.9 s on a kobold.
  - Test pitfall (not a bug): a GM-mode caster makes everything it hits evade, and the server's
    area skips evading creatures - test field ticks with `.gm off` (the probe protocol restores
    `.gm on`).
- 2026-10-26 — **P3.5 + P3.6 BUILT and proven live** (UV *rotate* not built - scroll and flipbook cover
  the ball; it is a third TextureTransform track when something needs it).
  - `SketchShapeKind` Sphere / Cylinder / Cone / Torus / Beam (`SketchShapes.IsSolid`),
    `SketchShape.Segments`; `Creator/Sketch/SketchSolids.cs` builds them in the piece's raw frame
    round its pivot (the bone's facing/spin/travel apply unchanged); a solid's glow is the same
    mesh inflated (a shell). `SketchLook.Image` (any archive BLP, shipped as a PATH - no sketch
    BLP), `UvScroll`, `FlipCols/FlipRows/FlipFps`.
  - `SketchWriter` restructured round `Draw` meshes: per-piece texture paths, texture flags 3
    (wrap) on imaged/animated textures, one TextureTransform per animated piece at 0x074 on its
    OWN global sequence, the uvAnimationLookup, the global sequence table, a ushort vertex budget.
    Flipbook = STEPPED translation keys (interpolation 0, one per cell). Scroll = two linear keys
    over the shortest period on which BOTH axes land on whole repeats (0.5 and 1.25 /s → 4 s,
    (2, 5)) - the first cut rounded one period and would have jumped at the seam; the lab caught it.
  - `SpellEffectMeshRenderer` honours M2 texture wrap flags (every spell texture was uploaded
    CLAMPED, so a scrolling image smeared its edge pixels - Blizzard's own scrolling spells too).
  - Sketch window: the five solids on the shape shelf; Smoothness / Tube dials; LOOK > Image with
    a searchable thumbnail picker over `Spells\`, `Particles\`, `Textures\` (chips: lightning,
    fire, frost...), Scroll /s, flipbook Cols / Rows / Frames /s; the baked-halo Glow greys out for
    imaged pieces and points at EXTRAS > Glow yd.
  - Proof: lab 38,429 checks (new: every solid compiles, both readers accept it, 6 batches, the
    image piece ships no BLP and wraps, two transforms on two global sequences, seamless scroll,
    the flipbook HOLDS cell 4 at 0.30 s, per-batch transform lookup, sphere pole normal);
    `MSUI_SPELLPIPE_PROBE` `ball=1` 26/26 (image pieces ship no BLP, the model wears the archive
    image wrapped with two transforms, reopen restores sphere + image + scroll); creator preview
    burst and live burst (Magetest, -8810,-226) both show the scrolling 3D ball.
- 2026-10-26 — **P3.3 + P3.4 BUILT and proven live** (sparks and trails = SPELL_SKETCH slice S5).
  - `Creator/Sketch/SketchWriter.Extras.cs` (the writer is now `partial`): a donor's particle
    (504 B) or ribbon (0xDC) record is COPIED, every array it addresses relocated into the sketch
    file - particle: the two model-name arrays, the ten float tracks, the spline points
    (+0x1D4, which the web transplanter never needed), the enabled track; ribbon: its texture-slot
    and material arrays and six tracks. Each track is re-timed onto the sketch's one sequence
    (keys inside the donor's sequence 0, rebased to 0; held key when none falls inside); a track
    on a donor GLOBAL sequence keeps its keys and gets its own global sequence with the donor's
    duration. Re-targeted: bone, position (the piece's pivot), texture (appended to the table,
    donor flags kept), ribbon material (appended to the render flags), and the card: spark rate /
    speed (scaled so the donor's curve keeps its shape), size, recolour (keeps the brightness ramp
    and alpha), trail life and colour (= LOOK colour unless set). Donors are read once per
    session; a missing donor is a NOTE (the build's `Notes`, shown in the window), never a failure.
  - `SketchSparks` / `SketchTrail`: nullable Rate / Speed (null = the donor's), Size, Model +
    Emitter / Ribbon for `Custom`; new presets `Crackle` (`Spells\LightningStreak_Missile.m2` e0)
    and `Custom` ("From a model...", the model picker gained sparks / trail slots and lists the
    model's emitters / ribbons by texture, rate and life). The EXTRAS card replaced its greyed
    placeholders. `Build(..., archive:)` - the client passes the mounted MPQs.
  - `MSUI_FX_POOL_TRACE=1` (`SpellParticleSystem`): twice a second, every live pool's particle
    count, rate, life, speed and position. It is what showed the first "no sparks" was not a copy
    bug: 27 particles alive, each a 5 cm sprite (the donor's own size) - hence the Size dial.
  - Proof: lab 38,532 checks - all 10 presets (6 sparks, 4 trails) copy from the archives and
    re-read the SAME scalars as their donors through M2Reader (and the web reader accepts every
    one), overrides land (rate peak 55, size x2, blue recolour with the alpha ramp kept), the copy
    is deterministic, no archive = two notes and a clean build, an unknown custom donor = a note.
    `MSUI_SPELLPIPE_PROBE ball=1` 26/26; creator preview burst shows the sparks; Test live #40030
    (patch-3 189fc7bb) cast live: `[spell-fx-place] spell:Spells\BallLightning_Missile.m2 e0`,
    the ball trails sparks to the spot, damage 129/129/130.
- 2026-10-26 — **P3.8 BUILT and proven live** (hierarchy, orbit, jitter).
  - `SketchPlace.Parent` (a piece NAME; "Rides on" in PLACE): the piece's bone hangs off that
    piece's bone, pivots stay absolute so a child follows from where it is; deleting a piece
    clears its children's link; `SketchWriter.ResolveParents` breaks any loop to the caster.
  - `SketchMotion.Orbit*` / `Jitter*` (MOVE card): a MODIFIER bone between the piece and its
    parent (`Creator/Sketch/SketchWriter.Modifiers.cs`), pivot at the piece's origin, tracks on
    their OWN global sequences - seamless forever on a looping missile. Orbit = 16 linear keys a
    turn round the chosen axis (the piece keeps its facing); jitter = 32 seeded keys a loop of
    translation and/or rotation about all three axes, STEPPED (flicker) unless Smooth, last key =
    first. Modifier bones are numbered after every piece bone, so no vertex's byte bone index
    moves; more than 255 bones refuses the build.
  - A wrong turn worth keeping: the first arcs rendered ORANGE. Not a decoder swap -
    `Particles\Lightning_Side.blp` IS orange (Fire Wall uses it); an earlier ad-hoc texture
    sheet had shown it blue. The lab now has `LAB_DUMP_BLP="a.blp;b.blp" LAB_DUMP_DIR=dir`, which
    decodes archive images through the client's own BlpDecoder - check colours with THAT.
    The blue horizontal bolt is `Spells\Lightning3.blp`.
  - Proof: lab 38,555 checks (new, through the client's M2Animator: 7 bones for 4 pieces + root +
    2 modifiers with the right parents, global sequences, stepped jitter; the child rides its
    parent's 2 yd travel; the orbit keeps 0.5 yd and comes round a turn later; the jitter holds
    between rolls, jumps at the next, stays in its box, is deterministic; every vertex keeps its
    piece bone; a parent cycle breaks and still compiles). `MSUI_SPELLPIPE_PROBE ball=1` 27/27 (the
    shipped ball carries 1 emitter and 7 bones); creator burst and live burst show the arcs.
- 2026-10-26 — **P2.3 BUILT (client-only) and proven live.** A deferred destination lists nobody
  at GO, so the per-unit impact kit had nobody to play on. `GameLoop.Casting.cs`
  (`DeferredImpact`, `ObserveDeferredImpact`, hooked beside the other combat observers in
  `GameLoop.Net.cs`): the ground missile with an empty GO remembers its cast; every NON-periodic
  SMSG_SPELLNONMELEEDAMAGELOG for that caster + spell is a victim - one seen before the client's
  missile lands waits for it, one after plays at once, each unit once per cast, window 1.5 s
  after arrival (60 s max flight). No Core change and no new opcode: the damage log already IS
  the server's list. Live: `DeferredImpact` on every damaged unit within 1 ms of its damage.
- 2026-10-26 — **P4.3 (camera shake) + its P4.4 dial BUILT and proven live.**
  - The client had NO camera shake. `SpellVisualCatalog` now reads kit f14 and the two tables
    (`SpellEffectCameraShakes.dbc` 9 rows: id + three CameraShakes ids; `CameraShakes.dbc` 24
    rows: id, type, direction 0/1/2, amplitude, frequency, duration, phase, coefficient;
    `TryGetCameraShake`). `GameLoop.CameraShake.cs`: `CameraShakeLaw` (wave = A·w(2π(f t +
    phase))·(1 - t/D)^coef, w = sin for type 0, |sin| for type 1; 0.05 yd per table unit; full
    within 15 yd of the camera target, none past 60) and the per-frame sum into
    `Camera.ShakeOffset`, which moves `View`/`RelativeView` only. `SpellEffectSource.KitShake`
    fires for every kit spawn (unit or ground) with a shake; the same shake within 0.1 s is one
    jolt (an impact on five victims). `[camera-shake]` log lines carry start and peak.
  - Authoring: a "Camera shake" combo per phase in Composition (the game's 9 shakes, labelled by
    their waves), in the composed preview kit, in the session export (`cameraShake`, 0 = none)
    and the design block; the Completer (`StageComposition.CameraShake`) writes kit field 14
    (deployed). Lab: 352 stock kits shake; every table row resolves; the wave law holds
    (silent outside its life, never above amplitude, type 1 never below rest, falloff).
  - Live: Blizzard's own Thunderclap (8078) jolts the camera (peak 0.084 yd); the Ball's landing
    with shake #3 (peak 0.10 yd).
  - The lab gained `LAB_DUMP_DBC="DBFilesClient\X.dbc[:rows]"` (shape + first rows) and
    `LAB_LIST_SHAKES=1`. Two dead ends worth recording: Lightning_Side.blp and Blue_Glow2.blp
    ARE orange (all their stock emitters have rate 0 - dead emitters - which is why Frostbolt
    never shows them); the player War Stomp (20549) has no shake, NPC War Stomp (45) does.
- 2026-10-26 — **P3.7 universal undo BUILT and proven** (`GameLoop.Creator.Undo.cs`). The undo
  unit is the DESIGN - the snapshot the session export carries (`CaptureCreatorDesign`: rules,
  composition incl. shake and sounds, area, sketches, every model's edits, audio, imports) - so
  anything a save keeps is undoable and nothing needs its own undo code. Commit: twice a second
  while no mouse button is down, no handle drag and no sketch recompile is queued, a change from
  the last commit pushes it (a drag or slider sweep = one step). Undo/redo re-apply through
  `ApplyCreatorDesign` (the reopen path) with the snapshot's own import/audio byte references;
  64 deep each way; a different spell starts a fresh history. Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z and
  the strip's Undo / Redo buttons now drive it (the handle stack remains only for Escape-cancel
  of a drag in flight). `[creator-undo]` log lines name what changed ("the sketch", "the rules").
  Proof: `MSUI_SPELLPIPE_PROBE ...;undo=1` - a rule edit and a sketch edit make two steps; undo
  takes back the sketch then the rule, redo brings both back, and the rest of the pipe (export,
  reopen, Test live) passes on the undone design: 39/39.
- 2026-10-26 — **P3.9 game-sound picker BUILT and proven live.** `SoundEntriesCatalog.Entries` /
  `SoundKitLibrary.Catalog`; each audio row gains "Game sound": search (+ chips) over all 4,600+
  SoundEntries by name or file, listen (the real playback path), pick. A pick lives where its
  kit lives - `CreatorStageComposition.Sound` (kit f13), `doc.MissileSound` (SpellVisual f10, the
  flight loop), `doc.Area.Sound` (area kit f13) - so preview, export (`sound` in composition),
  design round trip and undo all carry it; picking drops an imported file for that cue (a file
  would win). A NEW spell's rows are no longer dead: a phase the design plans or fills, the
  missile of anything with speed, and an area with content can all sound (kits are created on
  demand). Completer (`SpellVisualCloner`): stage/area `Sound` -> kit 13 (an area kit is made for
  a sound alone), missile `Sound` -> visual 10; `SpellCompositionTests` (2) pin the parse.
  The lab gained `LAB_FIND_SOUND="word word"`.
  Live (#40030, patch-3 858f7c0a): the sound journal shows 31 (Call Lightning precast) at cast
  start, 3014 (MissileNatureLoop, looping) through the flight, 4936 (Blasted Lands bolt, its three
  variants) at the spot and on every unit hit.
- 2026-10-26 — **P4.1 child spells BUILT and proven live** ("Thunder Mortar": the shell lands, its
  child explodes - Spice Mortar's shape, from zero, in the workshop).
  - `GameLoop.Creator.Children.cs`. A child is a second spell kept as its own DESIGN (+ its last
    export entry) inside the parent - never a second live doc. "+ new child spell" on a trigger
    effect (TRIGGER_MISSILE 32 / TRIGGER_SPELL 64 / periodic-trigger aura 23) makes one from
    zero under a new local id and opens it; the strip shows "CHILD of X  < back to X". Editing
    stashes the parent's design (a stack: children can have children); "back" writes the
    child's design + a fresh export into the stash and reopens the parent through the reopen
    path. The parent's trigger column holds the child's LOCAL id; the Rules card shows
    "Triggers child: <name>  edit / unlink" or, for a plain id, the spell's name and "+ new
    child spell" / "use <other child>". The design block carries `children` (undo covers them);
    the export carries `children: [{localId, entry}]`.
  - Test live (`SpellTestLiveClient.Child`): children are pushed + completed FIRST (each under its
    own remembered id in spell-test-entries.json); the parent entry's `effectTriggerSpellN`
    columns are rewritten from local to real ids; then the parent. One patch, one learn.
  - Server law, read in vmangos and recorded in the code: TRIGGER_MISSILE runs per unit target
    and casts the child AT THE PARENT'S DESTINATION; implicit target 1 (caster) = exactly once;
    implicit 18 adds only an explicit unit target (a ground cast has none - never fires). In
    the whole 1.12 table only 2 spells use effect 32 and 1 uses 64 on a ground spell: there was
    no stock pattern to copy; the evidence came from two new `[SUI-SPELL]` Core log lines
    (`deferred-dest target ... effectMask`, `trigger-missile A -> B at (x,y,z) result=`),
    installed in mangosd 5cb6f672 (prev kept as mangosd.prev-1e7aa83c; trail in
    `tools/core-patches/sui-deferred-destination.patch`).
  - A Completer gap this exposed (fixed, deployed): `RebuildClientPatch` never mirrored the new
    visual id into `spell_template.spellVisual1` (only the older Generate path did), and vmangos
    sends SPELL_START/GO for a TRIGGERED spell only when that is set
    (`Spell::IsNeedSendToClient`) - the child fired but the client never heard of it. The rebuild
    now writes it (only when it differs).
  - Proof: `MSUI_SPELLPIPE_PROBE "name=Thunder Mortar;recipe=Ground missile;missile=1;designid=
    thundermortar01;child=1;testlive=1"` 24/24 (new spell opens, back writes one child with its
    export and effect 3 names it); Test live: child -> #40032, parent #40033 with
    `effect3=32 A3=1 effectTriggerSpell3=40032`; live (Magetest, 14 yd into the pack): parent lands
    (3/3/3 - it only taps), server `trigger-missile 40033 -> 40032 at (-8810,-240,82.2)
    result=255`, client SPELL_GO for 40032, the child's nature blast 69/65/63.
- 2026-10-26 — **P5.2 rules round trip BUILT (lab).** Every stock Spell.dbc row is authored onto the
  from-zero template the way the workshop does it (`CopyFrom` every column but the server-only
  ones), exported as the session's rules JSON, applied to a FRESH template and read back column by
  column (floats compared as floats): **22,394 of 22,394 lossless**. The legality law over the same
  rows, as a census: 22,348 clean, 46 flagged - all Blizzard's own oddities (effect / aura types
  vmangos does not implement, trigger ids that name no spell), i.e. exactly what an author copying
  from those spells is warned about. P5.1 (visual round trip through the Completer's cloner offline)
  is still open.
- 2026-10-26 — **P4.2 actors BUILT and proven live** ("Storm Orb": the shell lands and leaves an orb
  that zaps the nearest enemy every second for 6 s - the plan's own acceptance line).
  - Decision as built: an actor is a TOTEM - Blizzard's own mechanism for a thing that sits and acts
    (Searing Totem: SUMMON_TOTEM_SLOT1 87, target 44; TotemAI casts the creature's spell at the nearest
    attackable unit in that spell's range whenever it is not already casting - the spell's cast time
    paces it; the summoning spell's duration is its life; owner's faction). EffectSummon (28) was
    rejected after reading it: it makes a saved PET and refuses when one exists.
  - Core primitive (generic, gated by `SPELL_CUSTOM_DEFERRED_DEST`, mangosd a33970b1, trail in
    `tools/core-patches/sui-deferred-destination.patch`): `EffectSummonTotem` plants the totem at the
    spell's destination instead of 2 yd off the caster; `[SUI-SPELL] totem-at-dest` logs it. Stock
    totems are untouched.
  - Workshop (`GameLoop.Creator.Actors.cs`): a SUMMON_TOTEM effect's card shows "+ make the actor
    here" -> ACTOR (name, display with presets, scale, the spell it casts - a child made with "+ new
    child spell for it" or any id). It rides the design (`actor`) and the export; Test live rewrites
    its spell from the child's local id to the real one, and asks the live client to
    `.reload creature_template` too.
  - Completer (`SpellCompleterController.WriteActorAsync`, deployed): the actor row is Blizzard's
    Searing Totem (2523, patch 0) cloned through a temporary table to 7,900,000 + the spell's entry
    (inside the World Pack template range, above anything a pack allocates, stable per spell), made
    this design's (name, display, scale, spell_id1, type 11), and the spell's totem effect misc value
    pointed at it.
  - Live (Magetest, kobold camp, 14 yd): server `totem-at-dest spell=40035 creature=7940035 at
    (-8810,-240,82.2)`; the orb's guid then deals 15/19/20/18/20/16 - one zap per second, six in
    the 6 s life, child #40034's lightning impact on each target.
- 2026-10-26 — **P3.1 track model BUILT (lab-proven); the P3.2 dope sheet is next.**
  `Creator/M2TrackPatcher.cs`: one model over every animated track (emitter scalars + enabled gate,
  bone T/R/S, colour + alpha, transparency, texture transforms, ribbon colour/alpha/heights/
  visibility) with stable ids ("e3.rate", "b2.rotation"); Read gives absolute times + values; Write
  rewrites a track WHOLE - new arrays at EOF (16-aligned), block repointed, per-sequence ranges
  rebuilt from the times (range-less tracks stay range-less, global-sequence tracks keep theirs).
  `CreatorModelDoc.TrackEdits` (by id) apply after every other edit in the compose pipeline and ride
  the design (`trackEdits`). Lab: 308 tracks over 4 stock models rewrite losslessly; an inserted key
  lands, a moved one moves, deleting it restores the original; both readers accept the edits.
- 2026-10-26 — **P3.2 dope sheet BUILT and proven** (`GameLoop.Creator.DopeSheet.cs`). While the clock
  is paused, the timeline grows KEYS: one lane per track of the selection (an emitter's ten scalars
  + its on/off gate; a ribbon's colour, alpha, heights, visibility; for a phase, a combo over its
  bone / colour / transparency / texture-motion groups), keys as diamonds over the model's first
  sequence with the paused instance's playhead. Click selects (the value edits below - float,
  vec3, unit quaternion, 0..1 alpha, integer), drag moves (committed on release), right-click
  deletes, double-click adds a key holding the value the track has there; "hold (step)" toggles
  interpolation; an edited lane goes gold with a reset. Every change is a whole-track rewrite in
  `TrackEdits` (sorted, deduplicated), so it previews at once and rides export, design and undo.
  A layout bug the trace caught: an unconditional SameLine before the optional reset button
  stacked every unedited lane on one row (seven lanes at y=1275) - fixed.
  Proof: `MSUI_SPELLPIPE_PROBE "name=Dope;dope=1"` 5/5 on Arcane Missiles (5143): the sheet's commit
  path adds a key to `Magic_PreCast_Hand.m2` e0 rate (1 -> 2 keys, 77 at 3833 ms) that is in the
  model's bytes, the design carries it, reopening restores it; the frame shows the eleven lanes,
  "rate" gold with its new key and the playhead. The Cleave rate-ramp live export named in the
  step is not separately run - the export path is the same model bytes Test live already ships.
- 2026-10-26 — **P4.3 world effects + their P4.4 dial BUILT and proven live; CharProc census done.**
  - Census (`LAB_KIT_CENSUS=1` in the lab): kit f12 is a SpellVisualEffectName id - a WORLD effect,
    a model planted at the unit's feet rather than on a bone - used by 37 kits (Thunder Clap's
    `ThunderClap_Cast_Base`, Frost Nova's `Frost_Nova_state`, Entangling Roots, Net, Web,
    Tranquility's area, Vanish). The client played none of them.
  - Runtime: `SpellVisualKitInfo.WorldEffect` (f12 resolved to a model path);
    `SpellEffectSource.KitWorldEffect` fires on every unit kit spawn, `GameLoop.PlantKitWorldEffect`
    resolves where the unit stands and `SpawnWorldEffect` plants it there, tied to unit + spell so the
    kit's state reaps it (a ground kit plants it at its point directly). `[spell-world]` log lines.
    Blizzard first: Thunderclap (8078) plants its ring at the caster; Frost Nova (122) plants
    `Frost_Nova_state` at each frozen kobold's feet with an AuraState life.
  - Dial: Composition gains "World (feet)" per phase (pick / clear, preview, export `worldEffect`:
    "" removes, a path sets, absent keeps; design round trip); the Completer writes kit f12 through
    its own effect cloning (`KIT_FIELD_WORLD`, deployed; `SpellCompositionTests` pin the parse).
    Live: Ball Lightning's impact phase wears Thunder Clap's ring; the Completer made it
    `BallLightning_World.m2` and the landing planted it at all three victims' feet.
  - CharProc types the client still ignores, by use (parameters read from the data): 8 x34 (Heroic
    Strike, Cleave, Whirlwind, Bladestorm, Recklessness: p0 a packed RGB, p1 20, p2 a duration in
    ms - 600 for a strike, 10,000 for Whirlwind - p3 an alpha %: a coloured WEAPON TRAIL); 13 x18
    (Foul Chill, Glacial Roar, Reflect Magic: a colour + two 0.5/0.25 factors - a pulsing body
    tint, by the look); 7 x9 (Torpor, Sap, Stealth, Rocket Boots: small enums); 2 x5 (Berserk 1.0,
    Bloodlust 1.5, Tiger's Fury 1.1: a factor); 6, 10 (Fishing's line colour), 15 x1. Type 8 is the
    next to build (a ribbon between the weapon's base and tip for p2 ms); 13/7/2 need a reference
    for their semantics before code (probe first, don't theorize).
- 2026-10-26 — **P3.3 completed: transplant into STOCK models** (the Sketch half was done with P3.4).
  `Creator/M2RecordTransplant.cs` grafts any particle emitter or ribbon of any donor M2 into any
  target M2 by offset-preserving surgery: the texture table (and, for a ribbon, the render flags)
  rewritten at EOF with one more entry, every array the donor record addresses copied and
  repointed, tracks re-timed onto the target's first sequence (donor global-sequence tracks get a
  new global sequence appended), the particle / ribbon array rewritten at EOF with the new record
  last. In the workshop: the phase inspector's "+ copy from another spell's model" (model picker ->
  the donor's emitters and trails with texture, rate, life -> add); grafts live in
  `CreatorModelDoc.Transplants`, apply right after the model's own clones (so the emitter dials
  address them by index), ride the design and the export, and can be removed.
  Proof: lab 39,630 checks - all 10 SPELL_SKETCH §4.5 donors (6 sparks, 4 trails) grafted into
  Blizzard's `Spells\Cleave_Cast_Base.m2`: one more record each, re-reading the donor's rate / life /
  speed / colours / flags / texture (ribbons: texture, edges/s), on bone 0 at the given point, the
  target's own emitters untouched, both readers accept. Probe `dope=1` 6/6: Lightning Streak's
  sparks grafted into Arcane Missiles' `Magic_PreCast_Hand.m2` (2 -> 3 emitters), carried by the
  design and restored on reopen.
- 2026-10-26 — **CharProc type 8 (weapon trail) BUILT; sketch trails proven DRAWN.**
  `GameLoop.WeaponTrail.cs`: a kit lane of type 8 (`SpellEffectSource.KitWeaponTrail`: p0 packed RGB,
  p2 duration ms, p3 alpha %) attaches a generated model to the unit's RIGHT HAND (0x16) for p2
  ms - a speck carrying Blizzard's own "Charge" trail ribbon (copied cross-file, ~1 yd tall)
  recoloured to p0, built once per colour with the Sketch writer, so the ribbon system commits
  edges behind the swinging hand. Live: Debilitating Charge (1906, type-8 kit, no power cost) -
  `[weapon-trail] spell 1906: #DB0B10 for 1000 ms` on Magetest at the GO, the charge lands (67).
  The ribbon renderer itself was asked: `MSUI_PIPE_TRAIL=1` puts the Charge trail on the flying
  ball and the probe checks `SpellRibbonSystem.WasDrawn` for the sketch model - PASS (the frames
  cannot show it: the ball flies straight away from the camera, so its trail is behind it).

- 2026-10-26 — **P5.1 visual round trip BUILT: 2,169 of 2,174 stock visuals rebuilt from zero losslessly.**
  `LAB_ROUNDTRIP=1` in `tools/spell-emitter-lab` (the lab links the web `SpellVisualCloner.cs` and
  `DbcWriterService.cs`, the `WEB_M2READER` pattern): for every stock SpellVisual it derives the
  composition the client would export (every phase's kit with its animation, slots, sound, shake,
  world effect, CharProc lanes, kit type; the missile, its sound and attachment; the area visual),
  serialises it as JSON TEXT (in-memory `JsonValue<ushort>` nodes made `ReadFloat` return 0 - the
  harness round-trips through text like the wire does), runs the real Completer cloner from an
  EMPTY visual and diffs the new SpellVisual + SpellVisualKit rows field by field (model paths
  through `CloneResult.EffectFiles`). `LAB_ROUNDTRIP_SHOW=<visual>` prints one diff.
  Cloner fidelity gaps it found, fixed and deployed (the known FleetReportTests failure aside):
  a kit MADE by the cloner now gets its phase's KIT TYPE (f1: precast/cast 0, impact 1,
  state/channel 2 - measured over every stock kit) or a stated `kitType`; a stage's `charProcs`
  writes all four lanes (type + 4 params; `[]` clears, absent keeps); the missile's `attachment`
  writes SpellVisual f9; `visualFields` states the raw f8/f9/f14/f15 nothing else models.
  Raw fields are read as INTEGERS (`ReadRaw`): through a float, f14's `0xFFFFFFFF` overflowed to 0.
  `SpellCompositionTests.RoundTripFieldsParse` pins the new parse (web 606/607, the known one).
  Residuals (5), each read with `LAB_ROUNDTRIP_SHOW`: visuals 1, 37, 89, 115, 188 carry an area
  model (f12) with the area GATE f11 = 0 - dormant data the client never draws (`TryGetAreaVisual`
  requires the gate), so the composition does not carry it. Also counted apart, not as a loss: visual
  129's impact kit 210 has no SpellVisualKit row (a dangling stock reference: nothing renders, the
  cloner writes none). CharProc lanes are said POSITIONALLY (an empty lane = type -1): visual 7597
  keeps its tint in lane 1.
  Regression after the deploy: Ball Lightning (#40030) Test live 33/33 and a live cast at the
  kobold camp - precast 10008 (52), cast 10009 (54), damage, shake #3 at each hit,
  `BallLightning_World.m2` planted.
- 2026-10-26 — **P4.4 completed: the BODY dials (CharProc tint / see-through / animation speed / weapon
  trail) BUILT and proven live.** `GameLoop.Creator.BodyProcs.cs`: each phase in Composition gains
  "Body (aura)" on State (tint colour, see-through 0-1, anim speed 0-3 with 0 = frozen) and
  "Weapon trail" on every phase (colour, seconds, strength), with the semantics the client already
  plays (`AuraVisualLaw`: 1 tint p0 RGB, 14 alpha, 11 rate; 8 trail p0 RGB / p2 ms / p3 %). The
  source's other lanes (chain 0, shards 9, 13/7/2) are kept; a kit holds four, an overflow is told.
  Preview: while State is held or presented, the local character wears the look through the same
  `AuraVisualState` a live aura uses (`BuildUnitState` takes the creator's stand-in); a body-only
  phase now plays (`PresentSpellEffect` accepts a kit with lanes and no models). Export
  `charProcs` (four positional lanes, -1 empty; absent = keep), design `body` (so undo too).
  Live evidence channel: `[aura-body] spell S on U: tint #RRGGBB, alpha A` once per aura.
  Proof: probe `name=Crimson Veil;recipe=Self buff;body=1;testlive=1` 30/30 - preview tint
  #FF2020 and alpha 0.50 after the ramp, back to 1.00 when State stops; export lanes
  `[{1,[16719904..]},{14,[0.5..]},-1,-1]` and the Cast trail `{8,[2162496,20,1000,100]}`; reopen
  restores both; Test live built #40036. Live (Magetest, Northshire): cast -> `[weapon-trail] spell
  40036: #20FF40 for 1000 ms`, aura APPLY -> `[aura-body] spell 40036 on 2: tint #FF2020, alpha 0.5`
  read from the Completer-written kit; the frame shows the mage red and see-through (the tree
  through the robe), `.unaura` -> the plain mage. Lab 39,646 checks; gates green.
- 2026-10-26 — **D5 new index rows BUILT end to end and proven live (no restart per row).** A spell's
  cast time / duration / range / radius may now be ANY value: each Rules index picker gains a value
  box + "use" - an existing row that says it exactly, else a LOCAL row (900,000+,
  `SpellRuleTables.AddLocalRow`, re-added on every table read so pickers, rings and the legality
  law see it) carried as the rules block's `newIndexRows` (and the design's; a JsonValue<uint>
  read fix let an in-memory reopen keep them). Web `SpellIndexRows` (Completer, at completion):
  an equal row in the SERVER's table is reused, else one is appended at 1000+ and the file written
  (temp + move); the rules' index columns are pointed at the real rows (a local id left over is an
  error); patch-3 carries the four tables whenever they hold a 1000+ row (unified and single
  builds). Core (mangosd 6946a113 -> a7ac48e2): `DBCStorage::MergeNewRowsFrom` - append-only merge
  of a re-read file (existing entries never moved or freed; the old index table leaked on purpose;
  table published before the count) - and `.reload spell_index` (`[SUI] spell index rows merged:
  castTimes +n ...`, reading DataDir/5875/ like startup); Test live sends it before
  `.reload spell_template` when the design has new rows (`reloadSpellIndex`). Core diff:
  `tools/core-patches/sui-deferred-destination.patch` now holds every Spell Creator Core change.
  Proof: `SpellIndexRowsTests` (reuse, append at the floor, range row shape, column rewrite,
  orphan rejected); probe `newrow=1` 26/26 twice. Live, after the restart: probe at 2.45 s ->
  the Completer added SpellCastTimes 1001 (the 7.5 yd radius reused row 1000 from the first build);
  Magetest's `.reload spell_index` -> `castTimes +1` (then `+0`, idempotent); "Odd Burst"
  (#40037) cast with `serverDurationMs=2450 dbcCastTimeMs=2450`, the 7.5 yd blast hit three
  kobolds (128/140/127). Stock note: a 2.3 s row exists (31); 2.35 s became row 1000. Lab 39,662;
  the round trip reads 2,171 of 2,176 (patch-3's new spells' visuals now counted; same 5 residuals).
- 2026-10-26 — **CharProc type 2 (body SCALE) BUILT from a reference; 7 and 13 stay open.** The
  reference: wowdev.wiki DB/SpellVisualKit lists the procedures - 0/12 chain, 1 colour (p0 colour,
  p1 period, p2 endTime, p3 fadeOutTime), 2 Scale (p0), 4 emissive, 6 eclipse, 7 Stand/Walk anim
  (p0/p1 index the client's own stand and walk anim TABLES), 8 weapon trail (p0 colour, p1
  fadeOutRate, p2 duration, p3 alpha), 9 blizzard shards, 10 fishing line, 11 freeze, 14 alpha,
  15 fade; 13 is "?". Type 2 fits the data (Bloodlust II 1.5, Tiger's Fury 1.1, Berserk 1.0):
  `AuraVisualLaw.ScaleProc` is a head node like tint/alpha/rate, `WorldEntity.Scale` = the scale
  field x the aura's (so renderers, picking, shadows and nameplates follow), the local character's
  ModelScale mirrors it, and Composition's State gains "size" (export lane type 2, design
  `body.scale`). Blizzard first, live: `.aura 553` on Magetest -> `[aura-body] spell 553 on 2:
  scale 1.5`, the frame shows the mage half again as tall with the kit's flaming hands; before,
  normal. Probe body=1 31/31 (preview aura scale 1.5 -> ModelScale 1.5; lane `{2,[1.5..]}`; back to
  1 when State stops). Not built: 7 (needs the client's stand/walk index tables - not in any DBC we
  read), 13 (unknown even to the reference), 1's period/fade params (our tint is still p0 only).
