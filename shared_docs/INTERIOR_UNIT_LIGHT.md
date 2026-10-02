# Interior unit light — the room lights the model (2026-09-04)

## The report

Ironforge Commons at night, MSUI vs the 1.12 client: our toon, the NPCs and the
mailbox read as moonlit grey-blue while the 1.12 client shows them in the warm
fire light of the hall. The walls and the building's own props (MODD) were
already warm in MSUI; only the DYNAMIC models were wrong — units, mounts,
attached items, server gameobjects.

## The law

1.12 never lights an interior at runtime. Walls carry MOCV, the building's props
carry MODD.color, and a model that walks in takes the light of the room it is
in. The room's light at a spot is **the floor's baked colour under it**:

- Checked offline against `GoldshireInn.wmo` + its group files: every
  floor-standing prop's MODD.color is the MOCV interpolated on the walking face
  beneath it (about x1.2; the residual is the MOLT lamp pools). MOHD.ambColor is
  NOT it (Ironforge's is (5,5,14)), and the MOLT point lights alone do not
  reproduce MODD.color (radii 7–30 yd, sparse).
- So: unit light = MOCV of the floor face under the feet, x the walls'
  `VertexColorScale`, exactly the payload the doodad path uses for MODD.

One resolver, `WmoRenderer.ResolveInteriorLight(feet, terrainZ)`:

- Same cell verdict as the roof cut: `FindCameraSeeds` from a probe 1.5 yd over
  the feet, `CutPlaneMaxFeetDrop` (a floor a storey below is not the room).
- Same group law as `BuildDoodadLighting`: INTERIOR (0x2000) and not
  EXTERIOR / EXTERIOR_LIT (0x48). Anything else is daylight (null).
- Colour from the walking face first (`GroupMesh.CollisionColors`), else the
  render floor (`GroupMesh.FootstepColors`) — many authored floors have no
  coplanar walking sheet (the Commons by the Ironforge bank has none at all;
  the creator body falls through there — a collision matter, not this one).
- Returns MOCV/255 unscaled. Consumers scale.

## Consumers

- Units, mounts, attached items: `World/Wmo/InteriorUnitLight.cs` caches the
  answer per guid (re-ask on a 0.75 yd move, every 2 s, or when
  `WmoRenderer.ResidentVersion` changes), a 24 floor-ray budget per frame, and
  eases the value (4/s) so a doorway is a fade, not a pop. Leaving a room fades
  the WEIGHT only, so the blend never passes through black.
- Shader payload `uInteriorLight` = (rgb MOCV/255, a = interior weight;
  0 = daylight = GL's default for an unset uniform, so the glue booth and
  portraits that never set it are untouched) + `uBakedLightScale`.
  `character.frag` and the creature shader:
  `room = rgb * scale * (0.75 + 0.35 * sunResponse)`, `light = mix(light, room, a)`.
- Server gameobjects (mailboxes, chests, braziers): `DoodadRenderer.AddDynamic`
  takes the light at placement and `TrySetDynamicLight` re-lights every
  placement when a WMO becomes resident (buildings stream in AFTER the server
  has spawned their gameobjects) and every 2 s.
- The one switch is the props' "Baked interior light (MODD)" setting: off
  restores the sky-lit look for units and gameobjects too.

## Proof

`MSUI_INTERIORLIGHT_PROBE=1` (`GameLoop/Dev/GameLoop.InteriorLight.Probe.cs`)
boots the creator world in the Ironforge Commons at 23:00 (the cold-blue sky of
the report), drops a dwarf, a human and a mailbox beside the body, prints
PASS/FAIL per claim plus `DescribeInteriorLight` narration, and writes
`dumps/gameplay-interiorlight-on-*.png` / `-off-*.png`. Run it with a scratch
`MSUI_SETTINGS_PATH` copy of settings.json. First run 2026-09-04: floor MOCV
(215,146,102), all checks PASS, the OFF capture reproduces the report exactly.

Read the offline check's method before re-theorising: the numbers are in the
resolver's doc comment and here, and MOHD ambient / MOLT were both tried.


## 2026-09-28 — Live equipment observation

`equipment watch self` (or `selection`), a rendered frame, then
`equipment inspect self safe-label` now saves the production interior uniform
(RGB = unscaled MOCV, W = blend weight), enable state, sample feet, terrain height,
fresh floor classification and WMO room narration under `lighting.interior`.
The streamed path snapshots the uniform at its actual world draw; local self
reports the last value assigned to CharacterRenderer. Diagnostics never call
`InteriorUnitLight.For` or advance its blend. The floor query is an independent
read of the current resident geometry, not proof that the last frame used it.
`camera` additionally records `EffectiveDistance`, `EyeTarget`, the measured
eye-to-target distance, field of view, aspect ratio and collision enable state.
This distinguishes requested zoom from achieved framing. Pair the JSON with its
gameplay screenshot; no model/art approval follows from an interior weight.
