# Spell Sketch — draw it, place it, move it, dress it (spec, 2026-09-10)

Status, 2026-09-10: **S1 BUILT and proven** (the compiler, `MSUIClient/Creator/Sketch/`,
§10) and **S2 / S4 BUILT, live-unverified** (the window and its four cards,
`GameLoop/CreatorMode/GameLoop.Creator.Sketch.cs`, §11). S3 is partly there — a piece
selects its own bone so the existing handles land on it, but card edits are not yet on
the undo stack. S5 (trails and sparks) is BUILT 2026-10-26 (SPELL_CREATOR_PLAN.md P3.3/P3.4: `SketchWriter.Extras.cs`, cross-file record copy, all §4.5 presets plus Crackle and "From a model..."); S5b and S8 are SPEC. **Update 2026-10-25:** S6 (the draw
canvas) is built; **S7 is BUILT and proven live** (SPELL_CREATOR_PLAN.md P0 - the `sketches`
block inside the session's `design`, reopen, the Completer from-zero path and a sketched spell
cast on the live server). S5b is partial: the MOVE card's Speed field is not read by anything
yet (the missile speed is spell data, P1's rules part).
Written for the implementer after the owner's ask, verbatim:

> "My goal for today would be that I could basically free form, in the editor,
> draw a crescent shape, Ctrl C + Ctrl V, position them in their intended
> origin, declare how far I want it to travel, either draw the line of movement
> relative to the caster or input into a box, and also if we want other
> effects / glowies / etc emitting from it, what kind of trails if any."

Read `shared_docs/SPELL_CREATOR_IDE.md` first. This document adds ONE thing to
that IDE: authoring geometry that does not exist in Blizzard's files. Everything
else the owner asked for (place, move, dress, preview, export) already exists
for Blizzard's models and is reused, not rebuilt.

Owner's decisions, same evening: travel is BOTH "a fixed distance from the
caster" and "to the target, with the spell's range" (§4.3). Defaults and the
preset textures were delegated ("I don't know how to answer") and are decided
in §9 from the lab's census of what Blizzard's own effects use; change them by
editing the tables, not by asking again.

## 0. The one idea

The game can only play M2 files. Blizzard's own Cleave crescent is a flat
rectangle with a crescent painted on it, riding a bone that swings it. So a
"free-form effect" is not a new kind of thing — it is that exact thing, made by
us instead of Blizzard:

    a Sketch  =  pieces (painted planes on sticks)
               + how each piece moves
               + what hangs off each piece (trail, sparks, glow)

    compile   →  one real vanilla M2 (v256) + its BLPs, per stage

    then      →  it is an ordinary phase model: every existing dial, handle,
                 clock, composition slot and session/Completer path applies.

The compiler (Sketch → M2) is the only genuinely new engineering. It is a
writer for a file format we already read, validate and patch in two repos.

## 1. Today's goal, as clicks

The scenario the owner described, the way it must feel:

1. Spell Workshop open on any spell, cast stage selected. Strip: **Sketch**.
2. Shape shelf: **Crescent** (or **Draw** and draw one). A crescent appears at
   the caster's chest, facing the camera, white, 1 yd tall.
3. LOOK card: colour amber, Glow on, Blend Additive.
4. PLACE card: Facing **Vertical (forward)**. It now stands on edge like a
   sword slash. Drag it up 0.5 yd with the existing handle arrows.
5. **Ctrl+C, Ctrl+V**: a second crescent, offset 0.5 yd left. Drag it where it
   should start.
6. MOVE card on each: Travel **3 yd** Forward over **0.6 s**, Fade out. Or
   click **Draw path** and click two points on the lattice in front of the
   caster.
7. EXTRAS card: Trail **Thin white**, Sparks **Embers, 20/s**.
8. Loop plays; the two slashes leave the caster, fly 3 yards, fade. Pause,
   scrub, adjust. Session → push. The Completer gets the M2 and BLPs like any
   custom file.

If any step needs a second panel, a hidden mode or a number the owner has to
derive, the slice is not done.

## 2. The object model

One `Sketch` per stage of the spell (cast, impact, …); it lives on the spell
doc and is saved with the session so it reopens editable.

| Object | Fields (units) | Default | Meaning |
|---|---|---|---|
| Sketch | Stage, Pieces[], Length (s) | Length = longest motion, min 0.5 | One compiled M2 |
| Piece | Name, Shape, Look, Place, Motion, Extras | — | One painted plane on its own bone |
| Shape.Primitive | Kind (Crescent, Ring, Disc, Arrow, Line, Star, Rect), Size W×H (yd), Thickness (0..1), Softness (0..1) | Crescent 1×1, 0.35, 0.3 | Drawn mathematically into the texture |
| Shape.Stroke | Points[] (0..1 canvas), Width (px), Softness, Closed | Width 24 | Drawn by hand into the texture |
| Look | Colour, Alpha, Glow (0..1), Blend (Additive / Alpha / Mod), Unlit | white, 1, 0.5, Additive, on | The colour record + material + optional second additive batch |
| Place | Origin (yd, caster frame: forward/left/up), Facing (Vertical-forward, Vertical-side, Flat, Face camera, Custom yaw/pitch/roll), Scale | (0, 0, 1.2), Face camera, 1 | The bone's rest pose |
| Motion.Travel | Mode (Fixed distance / To the target), Distance (yd), Direction (Forward / Left / Up / typed vector), Duration (s), Ease (linear / out); in target mode: Speed (yd/s) | Fixed, 0, Forward, 0.6, out; 20 | Fixed: translation keys on the piece. Target: the whole Sketch becomes the stage's missile (§4.3) |
| Motion.Path | Points[] (caster frame), Duration | — | Translation keys along the polyline |
| Motion.Spin | Degrees/s about own normal | 0 | Rotation keys |
| Motion.Grow | From × To scale over Duration | 1 → 1 | Scale keys |
| Motion.Fade | In (s), Out (s) | 0, 0.2 | Alpha keys |
| Extras.Trail | Preset (None / Thin white / Wide soft / Fire), Length (s), Colour | None | A ribbon on the piece's bone |
| Extras.Sparks | Preset (None / Embers / Motes / Smoke / Sparkle), Rate (/s), Colour, Speed | None | A particle emitter on the piece's bone |
| Extras.Glow | Radius (yd), Colour | 0 | A second, larger additive plane on the same bone |

Every field is a number a human can say out loud. No field is a file offset.

## 3. The window (obviousness is the requirement)

The strip gets a **Sketch** toggle beside Gizmos. It opens ONE window, docked
where the inspector is, and the inspector shows the selected piece's cards:

    ┌ SKETCH: cast ─────────────────────────────────────────┐
    │ [Draw] [Crescent] [Ring] [Disc] [Arrow] [Line] [Star]   │  ← shape shelf: click = new piece
    │ ─────────────────────────────────────────────────────  │
    │ pieces                                                  │
    │  ▸ slash 1        crescent  3 yd fwd   trail sparks     │  ← one line each, state visible
    │  ▸ slash 2        crescent  3 yd fwd   trail sparks     │
    │ [copy] [paste] [delete]        Ctrl+C / Ctrl+V / Del    │
    │ ─────────────────────────────────────────────────────  │
    │ LOOK   colour ■  alpha ─●─  glow ─●─  blend [Additive]  │
    │ PLACE  facing [Vertical, forward ▾]  scale ─●─          │
    │        origin  fwd 0.0  left 0.5  up 1.2   (or drag it) │
    │ MOVE   [Fixed distance ▾]  3.0 yd  [Forward ▾]  in 0.6 s  [ease ▾] │
    │        [Draw path]   spin 0 °/s   grow 1→1   fade 0/0.2 │
    │        (To the target: speed 20 yd/s, range = the spell's)  │
    │ EXTRAS trail [Thin white ▾] 0.3 s    sparks [Embers ▾] 20/s │
    │        glow  0.0 yd                                     │
    └─────────────────────────────────────────────────────────┘

Rules for the cards:

- Four cards, always in this order, always all visible. No collapsing.
- Each field has the `(?)` help the IDE already uses, one sentence, plain.
- Everything previews live; there is no Apply button.
- **Draw** opens a square canvas over the stage (not a new window): draw with
  the left button, Enter accepts, Escape discards. The stroke becomes the
  piece's texture; the piece is otherwise identical to a primitive piece.
- **Draw path** arms a click mode on the lattice floor: each click adds a point
  in front of the caster (caster frame, so it survives turning around), Enter
  accepts, Escape cancels. The path draws as a line with arrowheads while the
  piece is selected.
- **Ctrl+C / Ctrl+V** copies the selected piece with every card; the paste is
  offset 0.5 yd left so the two are never on top of each other. **Delete**
  removes. **Ctrl+Z** rides the IDE's undo stack (extend it: every card change
  on a piece is one undo entry, not only drags).
- Selecting a piece selects its BONE in the world: the existing translate
  arrows and rotate rings appear on it. Dragging them writes Place. This is
  "position them at their intended origin" — the handles from §2.10 of the IDE
  spec, no new gizmo code.
- The piece list shows state in words ("3 yd fwd", "trail", "sparks") so a
  glance tells what each piece does.

## 4. The compiler: Sketch → M2 + BLPs

New code, all in `MSUIClient/Creator/Sketch/` (a subsystem: it never
references GameLoop — CODE_STRUCTURE_LAW). Pure input → bytes.

### 4.1 Textures

- One RGBA texture per piece, power of two, 128..512 by the piece's size
  (Size × 128 px/yd, clamped). White where the shape is; colour comes from the
  colour record so LOOK colour changes never re-rasterize.
- Primitives are drawn with signed-distance maths, not bitmaps: crescent =
  disc minus a disc offset by `Thickness`; ring = |r − r0| < w; arrow, line,
  star likewise. Softness is the falloff width of the distance field. Glow is
  a second, blurred copy in the same texture's alpha at lower weight.
- Strokes are rasterized from the canvas polyline with round caps, width and
  softness as above.
- Bitmap → BLP through `Creator/BlpWriterService.EncodeBitmapToBlpUncompressed`
  (CORRECTED 2026-09-10: this spec first named `ConvertPngToBlpVanillaMatched`,
  which takes a PNG **path** and would put a disk round-trip inside the
  "recompile on every card change" budget. The bitmap entry point already exists,
  writes `compression=1, alphaDepth=8`, and is what S1 uses). Uncompressed
  alpha, never DXT1: soft edges must survive, and a DXT block makes every one of
  them hard. The texture is WHITE with the drawing in the alpha plane, so its
  palette is a single entry and the encode is lossless.
- Paths: `Spells\Custom\{spellId}_{spell}\sketch_{stage}_{piece}.blp`. Served
  live by the MPQ override layer (`MpqMount.SetOverride`), shipped in the
  session as `tintedBlps` with reason `sketch`.

### 4.2 Geometry

- Each piece is one quad: 4 vertices, 2 triangles, UV (0,0)..(1,1), normal
  along the plane normal, bone weight 255 on the piece's bone. Size W×H yards
  in the piece's LOCAL plane; Place and Motion never touch vertices, only the
  bone.
- Coordinates are the FILE's raw frame: x forward, y left, z up. The runtime
  reads them through M2Reader's swap (x, z, −y); this is proven in the emitter
  lab (`SpellGizmoHandleLaw.Swap`). A vertical slash facing forward is a quad in
  the raw XZ plane. Do not write model-space (swapped) coordinates.
- Glow extra = a second quad, `Radius` larger, on the same bone, in its own
  submesh with an additive material.

### 4.3 Skeleton and motion

- Bone 0: root, static, pivot at the origin. One bone per PIECE, parent 0,
  pivot at the piece's Origin. A glow does NOT get its own bone: §4.2 already
  says it rides the piece's, and sharing is what guarantees the halo travels
  with the slash instead of beside it. (Resolved 2026-09-10; §4.3 previously
  said "and one per glow", which contradicted §4.2.)
- The un-rotated quad lies in the raw XZ plane with its normal along raw +Y, so
  the facings are: Vertical forward = identity; Vertical side = yaw −90° about
  raw Z; Flat = roll +90° about raw X; Face camera = identity plus the bone's
  billboard bit. Vertices are LOCAL to the pivot, because vanilla's bone
  convention is `T(pivot)·T(translation)·R·S·T(-pivot)` — a quad centred on its
  own pivot is what makes spin and grow act about the piece's centre.
- 1.0 in the file is 1.0 YARD. There is no yards-per-unit constant anywhere in
  the spell path, so a 1 × 1 yd crescent is literally ±0.5 in file floats.
- Place = the piece bone's first keys: translation (Origin), rotation (Facing
  as a quaternion), scale.
- Motion = keys on that bone over the Sketch's single sequence:
  - Travel: translation keys origin → origin + direction × distance, `Ease`
    as key spacing (linear: 2 keys; out: 4 keys on an ease curve).
  - Path: one translation key per path point, times by cumulative length.
  - Spin: rotation keys, 8 per turn, about the plane normal.
  - Grow: scale keys.
  - Fade: alpha keys on the piece's colour record (0 → 1 over In, 1 → 0 over
    Out at the end).
- Rotation keys are raw-frame quaternions; Euler → quaternion through
  `M2BoneParser.EulerToQuaternion` so the two agree. Key times in ms.

TRACK LAW (learned from the readers during S1 — every one of these fails
SILENTLY, which is why they are written down):

- A vanilla animation block is **FLAT**, 28 bytes: `{u16 interp, i16 globalSeq,
  M2Array ranges, M2Array timestamps, M2Array keys}` — ONE timestamp list and
  ONE key list for the whole model, with a `ranges` entry per sequence giving
  `[first, last]`. The nested `M2Array<M2Array<T>>` shape is WotLK+ and
  misparses here (`M2Reader.cs:2764-2769` says how it was caught: the keys stop
  being unit quaternions).
- `AnimationRange.End` is **INCLUSIVE** — the index of the last key.
- `nTimestamps` MUST equal `nKeys`, or the reader silently wipes timestamps,
  keys AND ranges and the track is dead with no diagnostic.
- Timestamps are ABSOLUTE ms on the model timeline, must be STRICTLY
  increasing, and must stay inside the owning sequence's `[start, end]` window.
  Rounding to whole ms is where two keys collide; nudge them apart and then
  make sure the nudge did not push the last one past the end.
- `globalSequence` must be **-1 (0xFFFF)**. A 0 there means "global sequence 0"
  and the bone reads as static.
- Rotation keys are **four raw floats** (16 B/key). Packed int16 quaternions are
  TBC+ and read as garbage.
- An alpha track whose keys are all equal is read as a CONSTANT, and a constant
  of zero makes the whole batch "constant invisible" and it is never submitted.
  A real fade is safe (its keys differ); an all-zero track is not.
- ONE sequence, id 0, duration = Sketch.Length × 1000 ms, flags: not looping.
  `SpellAttachment.SelfTerminatingSpan` reads the sequence duration, so this is
  also how long the effect lives — say so in the Length field's help.
- Face-camera facing: set the bone's spherical billboard flag (M2 bone flag
  0x08; `SpellMeshSkinningLaw.ApplyBillboardBones` reads the 0x78 billboard
  mask), and the mesh renderer turns the piece to the camera every frame.

Travel has two modes, chosen per Sketch (the owner: "both"):

- **Fixed distance** (default): the piece flies from where it is placed, on
  its own translation keys, and the effect stays attached to the caster. The
  slash leaves the hand and dies 3 yards out whatever the target is.
- **To the target**: the compiled M2 is placed in the stage's MISSILE slot
  (Composition already owns it) instead of an attachment slot. The game flies
  it from the caster to the target over the spell's range at the missile speed
  — the same field the Completer form and Composition already write (Spell.dbc
  field 37). The piece's own spin, grow and fade keys still play during the
  flight, the Travel distance box is replaced by Speed, and the range is shown
  read-only as the spell's. An impact-stage Sketch is the natural partner (the
  hit). The loop previews it through the existing missile stage; a fixed-point
  preview target is what the Target panel already spawns.

### 4.4 Materials, colours, batches

- Render flag per piece: blend by LOOK (M2 blend modes as the emitter help in
  `GameLoop.Creator.Spells.cs` names them: 2 alpha blend, 4 additive, 5 mod),
  flags Unlit + TwoSided, NoZWrite for any blended mode.
- One colour record per piece (colour track: 1 key = LOOK colour; alpha track:
  the Fade keys). One transparency record per piece (weight 1).
- Texture entries type 0 (hard path) per piece; texture lookup, texture unit
  lookup and transparency lookup arrays one entry per batch.
- One view: submesh per piece (geoset id 0, the quad's index range, bounding
  sphere), batch per submesh pointing at its material/colour/texture. Layout
  per `M2MeshParser` (submesh 32 B, batch 24 B) — those readers ARE the
  contract, and the lab already cross-checks them against M2Reader.

### 4.5 Extras

- Trail: a ribbon record on the piece's bone (layout in `M2RibbonParser`),
  colour = LOOK colour, lifetime = the card's Length. The presets are
  Blizzard's own ribbons, chosen from the lab's ribbon census (2026-09-10 run;
  `dotnet run --project tools/spell-emitter-lab` prints it): the writer copies
  the named record (its tracks included) from the archive model and re-targets
  bone, texture path and colour. Copying a proven record beats authoring one
  from numbers.

  | Preset | Copy from | Texture | Height above/below (yd) | Edges/s | Life (s) | Blend |
  |---|---|---|---|---|---|---|
  | Thin white (default) | `Spells\ArcaneShot_Missile.m2` r0 | `SPELLS\GRADIENT128.BLP` (32 ribbons use it) | 0.06 / 0.06 | 50 | 0.2 | 4 additive |
  | Soft wide | `Spells\BalanceOfNature_Impact_Base.m2` r0 | `SPELLS\RIBBONBLUR1.BLP` (the untinted RibbonBlur; the `_Gold/_Blue/_Purple2` variants are the same shape pre-coloured) | 0.11 / 0.11 | 45 | 0.6 | 4 |
  | Glow | `Spells\Cripple_Impact_Base.m2` r0 | `SPELLS\GENERICGLOW2B.BLP` (24) | 0.17 / 0.17 | 33 | 0.4 | 4 |
  | Charge | `Spells\ChargeTrail.m2` r0 | `SPELLS\GRAD3A.BLP` | 0.47 / 0.47 | 50 | 1.0 | 4 |

- Sparks: a particle emitter record on the piece's bone (layout in
  `M2EmitterParser`; the record is **504 bytes (0x1F8)**, not the 476 most
  references quote, and the real shape field is the u16 at +0x2A), copied the
  same way from the emitter the census names
  (`dumps/spell-emitter-census.csv`, one row per emitter in the game), then
  rate, colour and speed overridden from the card. Cross-file copy of an
  emitter with its tracks is new (the existing `CloneEmitter` clones within one
  file); the lab proves the copy re-reads the same scalars as the source.

  | Preset | Copy from | Texture | Shape | Rate/s | Life (s) | Speed (yd/s) | Blend |
  |---|---|---|---|---|---|---|---|
  | Embers (default) | `Particles\LoginFX.m2` e0 | `CREATURE\FIREELEMENTAL\EMBER.BLP` | plane | 20 | 2.0 | 0.56 (authored −0.56: sign matters, IDE spec §1) | 4 |
  | Motes | `Spells\ArcaneExplosion_Impact_Chest.m2` e4 | `SPELLS\STAR5A.BLP` (63 emitters use it) | sphere | 24 | 0.75 | 1.16 | 4 |
  | Sparkle | `Spells\Arcane_Missile.m2` e1 | `World\SkillActivated\Containers\Sparkle.blp` | plane | 26 | 0.6 | 0.28 | 4 |
  | Smoke | `Spells\ArcaneExplosion_Impact_Chest.m2` e3 | `SPELLS\TOONSMOKE16.BLP` (86 with case variants) | plane | 40 | 0.5 | 1.64 | 4 |
  | Flare | `Item\ObjectComponents\Ammo\ArrowFireFlight_01.m2` e1 | `ITEM\OBJECTCOMPONENTS\WEAPON\FLARE.BLP` (188, the most used particle in the game) | plane | 40 | 0.8 | 0.56 | 4 |

- Extras ride the piece's bone, so they travel with it. That is the point.

### 4.6 The file

- Header: `MD20`, version 256, name, bounding box and radius computed from
  the pieces at rest and at the end of travel (both, so culling never clips a
  flying slash), every other array zero-count with offset 0.
- Write order: header, then each array appended, offsets absolute; strings
  NUL-terminated, arrays 16-byte aligned (the vanilla client never required
  alignment, but the two readers' `Fits` checks are the contract).
- Validation, in this order, before the bytes are used: `M2Reader.Parse` is
  non-null and `HasRenderableContent`; `M2MeshParser.ReadMeshes`,
  `M2RibbonParser.ReadRibbons`, `M2BoneParser.ReadBones`,
  `M2EmitterParser.ReadEmitters` return the expected counts. The lab writes a
  fixture Sketch (two crescents, travel, trail, sparks) and runs exactly these,
  plus the web repo's `M2Reader` on the same bytes. That second reader is
  **linked** into `tools/spell-emitter-lab/spell-emitter-lab.csproj` as a single
  source file behind an `Exists()` condition (`WEB_M2READER`), not copied and not
  project-referenced — the web project is `Microsoft.NET.Sdk.Web` and would drag
  ASP.NET and EF into the client tray, and a copy would drift, which defeats the
  point of a second opinion. The lab says so out loud when the web repo is not
  checked out beside this one.

  The web reader is STRICTER than this client's in three ways that constrain the
  writer: it rejects a model with no vertices (no `particleOnly` path, no
  `HasRenderableContent` fallback), so always emit ≥1 vertex, ≥1 inline view and
  ≥3 triangle indices; `RawM2Document` rejects any version that is not exactly
  256 and any file under 0x144 bytes; and its batch-span validator requires
  entries in ALL THREE combo tables (textureLookup, textureUnits AND
  uvAnimationLookup) — only the weight combo may be 0xFFFF.

### 4.7 Into the IDE

- Compile on every card change, in memory, the way `RebuildCreatorModel` does
  a patch: bytes → `SpellEffectSource.SetModelOverride` (+ the particle system's
  geometry override) + BLP overrides. Under 5 ms for a handful of pieces.
- The compiled model is registered as a phase model of its stage through the
  existing `EnsureCreatorModel` + a Composition slot (Chest by default), so
  the tree shows it, its bones/emitters/ribbons appear in the outliner, and
  every existing dial works on it. The Sketch cards are the friendly face; the
  raw dials stay available underneath.
- Paused editing, scrub, replay: inherited from the clock (IDE spec §2.2).

## 5. Export and the Completer

- Session JSON: the `models[]` entry for the compiled M2 (its custom path,
  `m2Base64`), the BLPs as `tintedBlps` (reason `sketch`), the composition slot
  naming the custom path, and a new `sketches` block holding the Sketch objects
  themselves so the spell reopens editable in Creator Mode.
- Completer (MangosSuperUI): `SpellVisualCloner.EnsureEffect` already creates a
  `SpellVisualEffectName` row for a composed path by cloning a template row
  from the source kit or missile. Verify and cover the one gap: a source spell
  with NO effect rows at all has no template — fall back to a fixed known row
  (pick one, document it in the cloner). The store's per-path M2s and extra
  files already carry custom paths into the patch MPQ.
- Nothing else changes server-side. A Sketch is a model like any other.

## 6. What exists and is reused (do not rebuild)

| Need | Already here |
|---|---|
| Move / rotate the piece in the world | handles on the piece's bone: `GameLoop.Creator.Handles.cs`, `SpellGizmoHandleLaw` |
| Reference frame, "forward" | the lattice in the caster's facing frame (`SpellEmitterGizmoLaw.Lattice`) |
| Pause / scrub / replay | `GameLoop.Creator.Clock.cs` |
| Put the model in a slot, scale it | `GameLoop.Creator.Composition.cs` |
| PNG → BLP, custom paths served live | `Creator/BlpWriterService.cs`, `MpqMount.SetOverride`, `GameLoop.Creator.TextureImport.cs` |
| Bytes hot-swapped into the running effect | `SpellEffectSource.SetModelOverride`, `SpellParticleSystem.SetGeometryModelOverride` |
| Record layouts | `Creator/M2MeshParser.cs`, `M2RibbonParser.cs`, `M2BoneParser.cs`, `M2EmitterParser.cs` |
| Validation | `Formats/M2Reader.cs` (`Parse`, `IsValid`, `HasRenderableContent`), the emitter lab |
| Session push, per-path store, cloner | `GameLoop.Creator.Session.cs`; web `CompleterStore`, `SpellVisualCloner` |
| Undo | `_creatorUndo` in `GameLoop.Creator.Handles.cs` (extend to card edits) |

## 7. Slices, each with its proof

| # | Slice | Done when |
|---|---|---|
| S1 | ✅ **DONE 2026-09-10** — `Creator/Sketch/SketchModel.cs` (the objects), `SketchTextures.cs` (SDF primitives + stroke raster + BLP), `SketchWriter.cs` (M2 bytes) | ✅ Lab fixture: a two-piece Sketch compiles; `M2Reader.Parse` + all four creator readers + the web `M2Reader` agree on counts, bones, keys, colours; the BLP re-reads. Plus a rendered filmstrip (§10) |
| S2 | ✅ **BUILT 2026-09-10, live-unverified** — Sketch window: shape shelf, piece list, LOOK + PLACE cards, live compile into a Composition slot | Owner clicks Crescent and sees an amber crescent standing vertical at the chest — needs the client running |
| S3 | ◐ PART — copy / paste / delete buttons are in and selecting a piece picks its bone (so the existing translate arrows and rotate rings land on it); the keyboard chords and undo for card edits are not | Two crescents dragged apart; Ctrl+Z walks back |
| S4 | ✅ **BUILT 2026-09-10, live-unverified** — MOVE card: travel box + direction, spin, grow, fade, the Length read-out, and both travel modes. "Draw path" on the lattice is NOT built; the compiler honours a path if one is set | Two slashes fly 3 yd forward and fade; pause + scrub shows every frame |
| S5 | EXTRAS: trail, sparks, glow presets (§4.5 tables), cross-file record copy | A trail follows the slash; embers pour off it; lab has one copy test per preset |
| S5b | MOVE "To the target": missile-slot placement, Speed box, range read-out, impact-stage partner | A sketched bolt flies to the spawned target and the impact sketch plays on arrival |
| S6 | Draw canvas | A hand-drawn shape flies like a primitive does |
| S7 | ✅ **DONE 2026-10-25** (SPELL_CREATOR_PLAN.md P0) — `sketches` in the session `design`, Completer from zero (template 18282), reopen editable | Push → complete → the patch plays the sketch in the real client; reopening the spell shows the pieces |
| S8 | Obviousness pass with the owner | The §1 script needs no explanation |

S1 must land first and alone. Nothing in the window is worth building until
the writer round-trips through both readers. **It does, as of 2026-09-10 — §10.**

## 8. Laws and traps for the implementer

- AGENTS.md rules stand: never commit, never launch the client without
  permission in the conversation, build both trays, `interface-wire-check
  --shared-docs-only` and `--imgui-policy-only` green, locator before grep.
- The compiler is a subsystem under `MSUIClient/Creator/Sketch/`; it never
  references GameLoop. The window is a GameLoop partial in `GameLoop/CreatorMode/`.
- Write RAW file coordinates and raw quaternions. Never write what M2Reader
  hands you back. The lab's swap checks are the tripwire.
- Strings inside records (texture paths, geometry model names) are
  `M2Array<char>` with absolute offsets: append at EOF, NUL-terminate.
- The two `M2Reader`s (client `Formats/`, web `Services/M2Handlers/`) must both
  accept the bytes; the Completer parses what it stores.
- Extend the emitter lab; do not start a second harness.
- Tooling on this machine: the Bash tool's quoted heredocs fail (write files
  with Write, edit with python scripts asserting on anchors); an `in`
  parameter captured by a lambda is CS1628 — pass structs by value.
- Creator files are dev-excluded from the ImGui policy; the Sketch window may
  use ImGui widgets freely, like the rest of the workshop.

## 9. Decided defaults (delegated by the owner, 2026-09-10)

Nothing here needs a question. Change a value by editing this table.

| Decision | Value | Why |
|---|---|---|
| Travel modes | Both: Fixed distance (default) and To the target | Owner: "both" |
| Default facing by shape | Crescent, Arrow, Line: Vertical forward. Ring, Disc: Flat. Star, Draw: Face camera | A slash stands on edge, a ring lies on the ground, a sparkle looks at you |
| Default size | 1 × 1 yd | Cleave's crescent plane is about that; a human-sized reference |
| Default origin | forward 0, left 0, up 1.2 yd (chest) | Where the eye expects a cast effect |
| Default look | White, alpha 1, glow 0.5, Additive, Unlit | Additive is what 9 in 10 spell effects use; white tints cleanly |
| Default motion | none; fade out 0.2 s | A new piece should sit still until told otherwise, and never pop out |
| Paste offset | 0.5 yd left | Two copies must never sit on top of each other |
| Trail presets | §4.5 table (Thin white default) | The census: `GRADIENT128` is the game's missile trail |
| Spark presets | §4.5 table (Embers default) | The census: copied from named Blizzard emitters |
| Texture size | 128 px per yard, clamped 128..512, power of two | Soft edges need the resolution; 512 caps the BLP at 1 MB |
| Sketch length | longest motion, minimum 0.5 s | The sequence duration is also the effect's life |

## 10. S1 build log (2026-09-10)

Built: `MSUIClient/Creator/Sketch/SketchModel.cs`, `SketchTextures.cs`,
`SketchWriter.cs` (namespace `MSUIClient.Creator.Sketch`; references nothing in
`GameLoop`, per CODE_STRUCTURE_LAW §1).

Proof, in `tools/spell-emitter-lab` (extended, not a second harness — §8):

- Lab tally went **36,133 → 36,885 checks, 0 mismatches**; `mesh-layers=1135
  (824 with a colour record) ribbons=312` are unchanged, so nothing pre-existing
  regressed.
- Fixture A is the §1 script: two crescents (the second made by the doc's own
  Ctrl+V, which must land 0.5 yd left), amber, vertical-forward, 3 yd forward
  over 0.6 s on an ease-out, fading. 2,010-byte M2 + 2 BLPs.
- Fixture B is a flat ring, a camera-facing star wearing a glow, and a
  side-facing crescent spinning — the facings, the extra glow quad on the
  piece's own bone, and a rotation track with real keys.
- Checked on those bytes: `M2Reader.Parse` accepts them and `IsValid` /
  `HasRenderableContent` hold; every count agrees between the runtime reader and
  `M2MeshParser` / `M2BoneParser` / `M2RibbonParser` / `M2EmitterParser`; the
  coordinate swap agrees in BOTH directions on every bone (raw pivot vs the
  reader's `(x, z, −y)`); a 1 yd quad measures 1 yd and sits at the authored
  chest height; the ease-out travel is 4 keys ending exactly 3 yd forward with
  an inclusive `[0,3]` range; rotation keys are unit quaternions and the facings
  are the expected raw-frame Eulers; the amber survives into the colour record
  un-swapped; the fade is 3 alpha keys ending at zero and is NOT mistaken for a
  constant-invisible batch; the texture resolves through the lookup chain and
  matches the §4.1 path law; the BLP re-reads at 128×128 with
  `compression=1, alphaDepth=8`, a solid interior, a soft edge, and pure white
  RGB; and the **web repo's `M2Reader`** agrees with this client on all of it.
- Our bytes are ordinary M2 bytes: `M2MeshParser.ApplyMeshPatch` and
  `M2BoneParser.ApplyBonePatch` both still work on them and both readers still
  accept the result — i.e. every existing IDE dial and the rotate rings apply to
  a compiled Sketch, which is the whole premise of §0.
- Every primitive draws: each of the seven shapes is rasterized at Thickness
  0.01 / 0.35 / 1 and Softness 0 / 0.5 / 1 and its lit pixels counted, so a
  shape that comes out invisible (or as a solid block) fails here rather than
  in front of the owner.
- An empty Sketch is refused rather than emitted as an unloadable M2, and so is
  one with more than `SketchWriter.MaxPieces` (128) pieces — an M2 vertex holds
  its bone index in a BYTE, so piece 255 would silently ride the root instead of
  its own bone. A full 128-piece Sketch compiles and every vertex still names its
  own bone; the 129th is refused. The window gates its shape shelf on the same
  constant.
- Record sizes are self-enforcing: the writer measures each array it emits
  against the stride the readers index it by and throws if they differ, and every
  header patch is bounds-checked against 0x144. Getting a record's size wrong is
  the one mistake here that produces a file both readers happily accept and whose
  every subsequent record is garbage.

Speed (§4.7 asked for a recompile on every card change, "under 5 ms"): a cold
two-piece recompile is **~5.6 ms**, and a cached one is **~0.02 ms**. The cache
(`SketchWriter.SketchTextureCache`, keyed on the SHAPE) is what makes that gap:
because the texture is white and the colour lives in the colour record, a LOOK
colour, an alpha, a placement, a travel distance, a spin or a fade cannot change
a pixel — only the shape and the glow weight can. So dragging any motion or
colour dial recompiles in microseconds and only an actual shape edit pays the
rasterizer. Ctrl+V copies the shape too, so two pasted crescents share one
raster rather than rendering the same image twice.

Picture: the lab also rasterizes the compiled M2 to
**`dumps/sketch-preview.png`** each run — the client's own `M2Animator` drives
the bones, the colour record supplies tint and fade, the BLP supplies the shape,
and a small software rasterizer (deliberately sharing no code with the GL
renderer, so agreement is evidence rather than a tautology) draws six frames per
fixture. Row 1 shows the slash leaving the chest, decelerating over 3 yards and
fading; row 2 shows the ring lying flat, the star's glow halo, and the crescent
spinning; row 3 is the whole shape shelf, each primitive drawn from its own
distance field. The fixture's own bytes are kept beside it as
`dumps/sketch-fixture.m2` and its BLPs, because the first useful question when a
reader disagrees one day is "what did we actually write".

NOT built yet: ribbons and particle emitters on a piece (S5 — the cross-file
record copy), and everything from the window outward (S2-S4, S5b-S8). The
writer leaves both arrays at `count=0, offset=0`, which is the correct "absent"
encoding, and the lab asserts they are empty so the day they are not is a
deliberate change.

## 11. S2 / S4 build log, and the review round (2026-09-10)

### The window

`MSUIClient/GameLoop/CreatorMode/GameLoop.Creator.Sketch.cs`. A **Sketch** toggle joins
Gizmos and Bones on the IDE strip; it opens one window docked where the inspector is,
with the shape shelf, the piece list (each row showing its state in words), copy / paste
/ delete, and the four cards — LOOK, PLACE, MOVE, EXTRAS — always in that order, always
all visible, every field with its one-sentence `(?)`.

The pieces live on `CreatorSpellDoc.Sketches` (per stage), so they are the SOURCE and the
compiled M2 in `Models` is the output. Selecting a piece sets `_creatorBonePick` to its
bone and selects the compiled model in the IDE, so the translate arrows and rotate rings
that already exist land on the piece with no new gizmo code (§6).

Compile-and-push is the minimal mimic of `RebuildCreatorModel` named in §4.7: mount
override (so `EnsureCreatorModel` can read the custom path back) → `SetModelOverride` →
`SetGeometryModelOverride` → `InvalidateModel` (mandatory: meshes bake per path at first
draw) → `SetCreatorSlot`, or `SetCreatorMissile` when any piece asks to travel to the
target. Recompiles refresh the `CreatorModelDoc` in place, because `EnsureCreatorModel`
only ever reads a path once and would otherwise keep describing the previous compile.
Rebuilds are rate-limited to 80 ms, the same throttle a handle drag uses.

EXTRAS draws its glow dial live and its trail / spark dials greyed with a line saying
they are slice S5. A dial that cannot do anything is worse than no dial, and a dial that
silently does nothing is worse still.

### The review round

An adversarial review (five lenses over the new subsystem, three skeptics per finding,
majority to survive) returned **six confirmed defects out of twenty candidates**. Two
mattered, and both were the same shape: WRONG IN A WAY NOTHING WAS LOOKING AT.

1. **A camera-facing piece rendered edge-on — invisible.** `FaceCamera` is the default
   facing for Star and Stroke, so `AddPiece(Star)` compiled to a piece that drew nothing.
   The billboard REPLACES the bone's basis rather than rotating it, and the plane it
   leaves facing the viewer is the raw YZ one, not the raw XZ plane every other facing
   uses. Neither reader can object — the file is perfectly valid — and the offline
   preview could not see it either, because rasterizing through `M2Animator` never
   applies the billboard.

   The first fix put the quad in the right plane with the two axes swapped: visible, and
   silently rotated a quarter turn, which an area check happily passes. The lab now runs
   the real `SpellMeshSkinningLaw.ApplyBillboardBones` over the compiled bones and
   measures the projected WIDTH and HEIGHT of a deliberately oblong 2.0 × 0.5 piece from
   two different cameras. With the bug re-introduced it reports `0.0000 sq yd`; with the
   quarter-turn re-introduced it reports `0.5 × 2.0`. The preview rasterizer applies the
   billboard now too, so the picture shows what the game shows.

2. **A drawn path outran its own sequence.** Timestamps are whole milliseconds inside the
   sequence window, so a track holds at most `lengthMs + 1` keys; a hand-drawn stroke can
   carry hundreds of points over half a second. The writer spread them as 0, 1, 2, … —
   outside the window, where both readers drop the tail and the piece freezes part-way
   along the path the owner drew. The path is now thinned by arc length (keeping the first
   and last point) to half the millisecond budget so real spacing survives, the
   impossible case throws instead of answering wrongly, and the last-resort fallback is an
   even integer spread, which provably fits. The lab draws a 700-point path into a 600 ms
   sketch and asserts every stamp is inside the window, strictly increasing, and that the
   stroke still starts and ends where it was drawn.

The other four: the arrowhead was sliced flat against the texture border above Thickness
0.76; the glow was cut into a SQUARE by the edge of its own texture on a default piece
(visible in the first preview, around the star); a one-point stroke drew a dot twice the
pen width; and the byte-sized bone index. All fixed. The shape now occupies the middle
60% of its texture and the QUAD is scaled up to compensate, so "1 × 1 yd" still means the
SHAPE is one yard and the margin outside it is where the halo lives. The lab checks the
outer band of every shape stays dark at full glow and full softness.

Fourteen candidates were dismissed as unreachable or harmless by a 3-skeptic majority.

Lab tally after all of it: **37,525 checks, 0 mismatches**, `mesh-layers=1135`,
`ribbons=312` unchanged.

### What is NOT verified

Everything above is offline proof: the compiler, its bytes, both readers, and a rendered
filmstrip. **The window itself has never been on screen.** It builds in both trays and
both `interface-wire-check` gates are green, but no one has clicked Crescent yet — and
§8's own rule is that the owner launches. S2's "done when" is an owner clicking a button,
so S2 is BUILT, not DONE.

## 12. The IDE turn (2026-09-10, after the first live run)

Owner, having run it: *"the tool must become an IDE that I can start from 0, decide if i
want precast or no, then cast, then missile if thats part of it, if not impact, or channel
if that's the cast - etc. The IDE user gets to decide that, and has a tool that lets them
understand/create every piece of the puzzle."*

### 12.1 A spell's anatomy IS which slots you fill

A `SpellVisual` row is thirteen independent slots, and
`Formats/SpellVisualCatalog.cs:44-45` states the rule: **"every populated slot on a reached
row fires. The stage selects LIFETIME POLICY only"** (`StageLife { SelfTerminating,
Persistent, AuraState }`). So the anatomy is not a mode to pick — it is the SET of parts
the author chooses to author. Precast, Cast, Missile, Impact, Channel, State, Area.

`GameLoop/CreatorMode/GameLoop.Creator.Anatomy.cs` makes that the top of the Sketch
window: the parts in the order a player experiences them, each with a tick ("does this
spell have one?"), what it holds (drawn pieces vs models inherited from the source spell),
when it plays and how long it lives. Five presets — melee strike, projectile, instant,
channelled, buff — write down a plan without creating any art.

**Why a stored plan and not just "does it have art":** in the data a precast you decided
against and a precast you have not got to yet are both empty. One is finished, one is a
to-do, and only the author knows which. The plan is what makes the list a checklist.

### 12.2 "Forward" is not forward — and that is a CLIENT BUG, not the format

The owner's report was that a piece told "3 yd forward" curves along the body. An attached
effect inherits its attachment bone's full pose, rotation included
(`SpellAttachment.cs:156`), so the first answer was "that is how the game works". **That
answer was wrong**, and the measurement says why.

Every player race's Chest attachment bone (HumanMale 63, NightElfFemale 65, OrcMale 64,
TaurenMale 70) carries M2 bone flag **`0x04` — ignore parent rotation**. Blizzard set that
flag precisely so a chest effect does NOT inherit the spine's swing. **MSUIClient ignores
it**: `M2Animator` reads only sequence flags (`:135`, `:620`), never `M2Bone.Flags`, and
`SpellMeshSkinningLaw.ApplyBillboardBones` — which is where the `0x04` branch lives
(`:177-180`) — is called for doodads, attached items, effect meshes, emitters and ribbons,
and never for a character skeleton.

Measured, by walking the real `SpellAttachment.World` over the real skeletons:

| Slot | Attachment | Off the caster's facing, SpellCastDirected | Swing over the clip |
|---|---|---|---|
| Head | 0x14 | **0.0°** | **0.0°** |
| Base (feet) | 0x13 | **0.0°** | **0.0°** |
| Chest | 0x22 | 47.6° (Night Elf 82.7°) | 9.7° |
| Right hand | 0x16 | 87.0° | 20.8° |

For the default sketch (3 yd forward, 0.6 s) on Chest that is a **1.99 yd end-point error
and 38° of tilt**; on SpellPrecast, 4.31 yd. Simulating the `0x04` flag drops it to
**0.04 yd / 0.8°**. So this is a fidelity bug affecting every chest-attached effect in the
game, not just sketches. NOT FIXED YET — it is an engine change with reach beyond this
feature and belongs in its own round.

What IS fixed: the Sketch's default slot moved from Chest to **Base**, which is rigid on
every race in every animation, and where the default Origin `(0, 0, 1.2)` still puts the
piece at chest height. Every slot now states its measured steadiness in the window, and
the three Special slots are marked for what they are — **they do not exist on player
models at all**, and silently fall back (`SpellAttachment.cs:71`) to a spine bone that
swings more than Chest.

Blizzard agrees with the new default: a census of all 40,780 `Spell.dbc` rows puts 2,385
cast kits on Base and only 152 on Chest. **Cleave's own cast kit uses Base.**

### 12.3 Cleave, measured — the worked example

`Spell.dbc` 845 → `SpellVisual` 219: cast kit 366, impact kit 507, **and nothing else** —
no precast, no state, no channel, no missile, speed 0. Two parts.

- **Cast kit 366**: caster animation 57 `Special1H`, sound 3091, one model in slot 2
  (**0x13 Base**), `Spells\Cleave_Cast_Base.m2`, scale 1.
- **Impact kit 507**: no animation at all, sound 3112, one model in slot 1 (**0x22 Chest**),
  `Spells\Cleave_Impact_Chest.m2`.

Inside the cast model: 1 sequence (1,233 ms, non-looping), 10 bones, **12 vertices = three
quads**, 3 textures, 3 particle emitters, 0 ribbons. And the thing the IDE spec asserted
and this confirms outright — **the crescent is the bone sweep**:

- bone 1 holds still for **233 ms**, then yaws **215° in 200 ms** (~1,075 °/s) about a
  point on the body axis at z = 1.075 yd, then **holds** to the end of the sequence;
- bones 3/4/5 carry **only scale**, 1.0 → 1.3 — the quads never rotate relative to their
  parent;
- the three quads are ~2.36 × 1.69 yd, near-horizontal (17–19° off flat), fanned 14° and
  16° apart, hung ~0.85 yd out from the sweep axis. The outer corner travels 8.7 yd of arc
  in 200 ms.

### 12.4 What that recipe demanded of the tool

Reproducing it exposed the one capability the Sketch did not have: **a piece could only
spin about its own centre.** A slash is not a thing that spins, it is a thing hung off an
arm that goes round once and stops. Two fields close it:

- `SketchPlace.Offset` — "Held out": how far the drawing sits from the point it turns
  about. Zero turns in place; 0.85 yd sweeps.
- `SketchMotion.SwingDegrees` — turn ONCE by this much over Duration, then hold, rather
  than spinning forever.

With those, plus `StartAt` (§11), Cleave's cast is expressible: three Rect pieces, 2.36 ×
1.69 yd, Flat facing, Held out 0.85 yd, fanned by quarter-turn/angles, Starts at 0.23 s,
Swing 215° over 0.20 s, Grow 1.0 → 1.3, on Base.

### 12.5 Starting from zero, literally

Owner, after the anatomy landed: *"it forces me to pick a spell first. I dont see a
'create new spell'."* Correct — and the deeper version of the same complaint: the workshop
was built to DERIVE. Its only entry point was a search box over `Spell.dbc`, so every new
spell began life as an edit of somebody else's, and the author had to make a choice before
they had made any.

**New spell** now sits above that search (`StartNewCreatorSpell`,
`GameLoop.Creator.Spells.cs`): name it and go. It builds a `SpellInfo` with
`VisualId = 0`, which means nothing is inherited — no kits, no models, no slots — and the
empty-visual branch of `SelectCreatorSpell` now runs `InitCreatorComposition` so a Sketch
still has somewhere to put what it compiles. The Sketch window opens with the plan
un-started, so the first thing the author sees is "what kind of spell is this?" rather
than someone else's art.

The preview path already supported this and nobody had noticed: `PresentSpellEffect`
substitutes a COMPOSED kit for a stage the source never authored
(`GameLoop.DevTools.SpellAnimation.cs:46-53`), and its gate is
`composed.Info.Id == spellId`, which for a brand-new spell is `0 == 0`. So a spell with no
visual at all loops and plays.

The id stays 0 until the Completer assigns a real one, so local paths are keyed by NAME.
Turning a from-scratch spell into real DBC rows is the Completer's job, and its "no
template row to clone" gap (§5) is still open — that is the remaining blocker on S7 for
spells that were never derived from anything.

