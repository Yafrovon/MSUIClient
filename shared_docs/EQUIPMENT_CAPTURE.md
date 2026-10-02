# Equipment renderer capture (2026-09-28)

This is repeatable **offline production-renderer inspection** for custom equipment. It uses `CharacterRenderer`, `CharacterEquipment`, `AttachedItemRenderer`, mounted client MPQs and `PortraitRenderTarget`. It never authenticates, moves a live character, or certifies in-world appearance.

## Run

```powershell
dotnet run --project MSUIClient/MSUIClient.csproj -c Release -- --variant-batch --axis equipment --list C:/path/equipment.json --out C:/path/equipment-review
```

An optional client config is the first argument after `--`. Normal `client-config.json` stays unchanged. `--limit N` allows an intentionally incomplete iteration and exits nonzero because the full requested matrix was not captured. Existing `items`, `players`, and `npc-extras` axes keep their behavior.

## Input

```json
{
  "schemaVersion": 1,
  "sets": [{
    "key": "custom-plate",
    "equipment": [
      { "name": "Helm", "displayId": 12345, "inventoryType": 1, "equipmentSlot": 0 },
      { "name": "Shoulders", "displayId": 12346, "inventoryType": 3, "equipmentSlot": 2 },
      { "name": "Chest", "displayId": 12347, "inventoryType": 5, "equipmentSlot": 4 },
      { "name": "Sword", "displayId": 12348, "inventoryType": 21, "equipmentSlot": 15, "itemClass": 2, "itemSubclass": 7, "sheath": 3 }
    ]
  }]
}
```

These numbers are illustrative, not an installed custom set. Display IDs must resolve in mounted `ItemDisplayInfo.dbc`; this is not an item-template-entry lookup. `inventoryType` is the item's normal inventory type; `equipmentSlot` is its zero-based player slot (head=0, shoulders=2, chest=4, waist=5, legs=6, feet=7, wrists=8, hands=9, cloak=14, main=15, off=16, ranged=17). Give weapons their actual `sheath`, `itemClass` and `itemSubclass` values.

Defaults: 768 square pixels, all races 1..8, sexes 0/1, sheath states 1 and 0, six views (front, right, back, left, front and back three-quarter), five exact animation samples (Stand 0s and .6s, Walk .25s, Run .2s, AttackUnarmed .2s): 960 captures per equipped set. Animation IDs are explicit input: replace the attack sample with the appropriate equipped attack (Attack1H, Attack2H, etc.) when reviewing weapon grip/clearance. Missing animation is an error, never silent Stand fallback.

Override top-level `width`, `height`, `races`, `sexes`, `sheathStates`, `views`, and `poses` for targeted iterations. A pose is `{"key":"attack-1h","animationId":17,"timeSeconds":0.2}`. A view is `{"key":"front","yawDegrees":0,"pitchDegrees":0}`. Keys contain letters, numbers, dash or underscore. Each manifest needs a nonempty `sets` array.

## Evidence and acceptance

- `index.html`: filterable actual captures and value/silhouette diagnostics.
- `captures.json`: requested body/pose/time, exact camera and model matrices, resolved attachment bone/root matrices, gear/material references, geosets, pixel bounds, border hits, luminance counts and horizontal silhouette intervals.
- `assets.json`: concrete MPQ supplier, byte length and SHA-256 for resolved models/textures and `ItemDisplayInfo.dbc`.
- `summary.json`: requested/completed coverage and explicit `inWorldVerified:false` / `visualReviewRequired:true`.
- `*-value-contour.png`: grayscale values and orange silhouette edge from the same raw capture.

Exit 0 means the declared matrix was captured without implemented technical errors. It **does not mean artistic acceptance**. Missing displays, requested body textures, attachment count/texture/bone failures, unavailable animations, blank captures and persistent image-border clipping are technical errors. Near-black/near-white/magenta counts and silhouette spans are measurements, not universal deformation thresholds. Framing expands deterministically on border contact and records attempts.

Neutral studio light isolates texture/shape review. Final acceptance still needs daylight/interior/night views in the actual world, drawn/stowed states, appropriate combat/movement/casting poses and another observer's view. Helm tests use appearance choice zero; other hair/beard choices need separate review. The separate world spell/particle system is not captured here. No raid roster is used.

## Implementation state

2026-09-28: equipment axis and deterministic pose/attachment observation added. Validation results are reported by the implementing task; this document is not proof of passing captures or live-world acceptance.

2026-09-28 validation: Debug/Release builds and the shared-docs check passed. Stock weapon all-body baseline produced384 frames without technical errors; Debug/Release copies of the24-frame smoke matrix were byte-identical. Missing-display and unavailable-animation negative cases exit4 with explicit errors. The first ten custom weapon matrix produced2,880/2,880 primary frames with zero technical-error cases and every declared model/skin SHA256 matching the mounted MPQ winner. This remains offline production-renderer evidence, not live-world verification or artistic acceptance.

For larger collections, use a bounded deliberate matrix rather than the general defaults: stand at six angles, all16 bodies, drawn/stowed; then walk/run/appropriate attack at two three-quarter angles drawn. This is288 primary images per weapon. Extra animation time samples should investigate a specific concern. Keep the exact manifest and report limitations with the images.

## Live equipment observation (2026-09-28)

The existing authenticated live protocol now accepts `equipment watch self|selection` and
`equipment inspect self|selection [safe-label]`. These only observe. For a remote player,
watch first, allow at least one world-rendered frame, then inspect. The chosen player's
19 server-visible item entries, cached item-template display IDs and inventory types,
sheath, camera and clock are written under `<live --out>/equipment/`. The remote renderer
also records the body display kit/geosets and resolved attachment mounts used in its
latest world draw. Off-screen/unresolved remote bodies report unavailable render evidence;
the probe never forces a load, query, equip or pose to make evidence appear complete.

Self refers to `ControlledGuid`; the result additionally records the real session GUID.
The local controlled body reports its CharacterRenderer equipment. The streamed body
uses the actual world draw observation. These distinctions must remain visible when
reviewing possession or observer tests. Mounted models alone do not prove visible pixels:
normal sheath policy, occlusion and materials still require the paired gameplay image.

The protocol's `dump LABEL` remains the normal gameplay JSON/PNG dump in the client
`dumps` folder. Successful command sends are not server acceptance. Check actual equipped
entries/display IDs, protocol failures, actual animation/server events and both files.
The stock and authored offline evidence remains separate from these live observations.

`equipment require-guid self|selection DECIMAL_GUID` is a fail-closed read-only precondition: a mismatch ends the live protocol with EQUIPMENT_ACTOR_MISMATCH before any subsequent step. Debug and Release compile validation passed to isolated scratch outputs (19 pre-existing warnings); SharedDocs check passed. The live command itself still awaits authenticated runtime verification.

## Bounded posed triangle probes (2026-09-28)

Set `MSUI_EQUIPMENT_MESH_PROBE=64` for at most 64 unique set/body/pose/sheath samples
in an equipment batch. Unset/default zero adds no mesh observer. Additional camera
views reference the first sample; use the recorded sampledView when comparing.
The gzip sidecar records completed body skinning through TargetMeshPickLaw, selected
geosets, and each actual submitted helm/shoulder batch with its own exact palette
and absolute draw transform. `captures.json` includes the file hash and counts.
No realm connection is made. Sidecars are diagnostic geometry evidence, not assets
for redistribution or artistic acceptance.

Compare custom and stock on identical race, sex, appearance, pose/time, sheath and
sampled view. The offline `probe_armor_contacts.py` in the web equipment-workshop
tools tests surfaces with Blender BVH. Enclosed vertices alone are deliberately
not called penetration. Alpha holes, two-sided/backface visibility and face
occlusion need material/pixel review. Counts or zero intersections do not prove
an armor set is artistically acceptable or live-world verified.

## Bare-foot compositor correction (2026-09-28)

The actual mounted vanilla `DBFilesClient\ChrRaces.dbc` resolves from `patch.MPQ`
(1,234 bytes, SHA256 `c338676c04e9c322839ab46ca45f5ba76f55e1eabe3e72847e467d675ea99d43`).
Rows 6 (Tauren) and 8 (Troll) have Flags=14, including bare-foot bit 0x2; Human,
Orc, Dwarf, Undead and Gnome have 12, Night Elf has 4. `RaceAppearanceTable` now
reads that field, and every native `CharacterEquipment.Composite` caller passes
its actual wearer's policy. Only FootTexture (region 7) is skipped. LegLower
(region 6), boot geosets and the authored source assets are preserved.

Primary implementation corroboration: archived WMV commit
https://github.com/Chuanhsing/wowmodelviewer/blob/79a97c404db38d7b1eef8b1f769eb84c5a202f32/src/charcontrol.cpp#L306
sets race-specific showFeet and conditionally omits CR_FOOT at lines1344–1349.
The old vanilla WMV0.48d compositor lacks that rule and is not evidence against
or proof of intended original-client behavior. WoWDBDefs confirms the original
ChrRaces field positions; this is grounded in the mounted original race flags,
not a special case for authored armor or a claim that WMV perfectly emulates1.12.

`dotnet run --project tools/race-foot-composite-check -c Debug -- GameData/Data REPORT.json`
reads the production MpqMount winner, records its hash/rows and checks the actual
CPU compositor.17 assertions passed: normal Human paints foot and lower leg;
Tauren/Troll preserve base foot pixels but paint lower leg; skipped foot assets
are not loaded; input skin remains unchanged; altered flags change policy.
Debug/Release builds pass with19 existing warnings. Native before/after capture
and live-world inspection are separate pending checks; this test does not launch
the client or certify complete equipment appearance.
### 2026-09-28 � live setup readiness gates

`equipment require-level self 60` and `equipment require-spells self 196,197,...`
read the controlled actor's replicated level and per-GUID spellbook. Missing data or a
mismatch terminates the live protocol as EQUIPMENT_READINESS_MISMATCH, before later
equip/capture steps. `selection` is also accepted, but spell checks need that actor's
actual streamed spellbook; remote visible fields alone do not prove learned spells.
These diagnostics never learn, level or request data. Setup protocols must wait for
server replies before the checks. `assert-skills-capped` alone can pass a level1 actor,
so it is not proof that GM setup succeeded.


### 2026-09-28 — fail-fast equipped-item readiness

`equipment require-items self|selection SLOT:ENTRY,SLOT:ENTRY,...` checks the actual
player's replicated `PlayerVisibleItemEntry` fields. `self` resolves `ControlledGuid`;
`selection` resolves the selected GUID. The actor must exist and be a player.
The nonempty specification accepts unique decimal slots 0..18 and unsigned decimal
item entries, including 0 to require an empty slot. It checks only the listed slots;
list all 19 to require an exact full outfit. Example:
`equipment require-items self 0:1102522,2:1102523,15:0,16:0,17:1102497`.

Invalid syntax, duplicate/out-of-range slots, unavailable actors and any item mismatch
terminate the live protocol with `EQUIPMENT_READINESS_MISMATCH`, preserving the expected
and actual slot entries before any subsequent step. Use after server updates settle
and before any mutation stage that assumes an initial loadout; repeat after equips and
before capture. The guard never loads assets, queries templates, equips items or mutates
the actor. It proves the current visible fields, not saved database persistence.

Focused checks: `dotnet run --project tools/interface-wire-check -- --equipment-readiness-only`.
Runtime fail-fast behavior still requires an authenticated negative protocol probe;
pure parser/mismatch tests and source wiring checks do not claim that live execution.

2026-09-28 validation: equipment readiness passed 32 focused parser, mismatch and source
wiring checks. Debug and normal Release builds succeeded (19 existing warnings);
PossessLaw and SharedDocs checks passed. No authenticated client launch or server restart
was performed for this change.


### 2026-09-28 — remote shoulder inspection identity

The first remote Redfen observation correctly drew both shoulder models but reported
slot=-1/displayId=0/inventoryType=0 for those mounts. Both shoulder mount paths had
omitted their parent equipment metadata. Those original captures remain technically
ineligible; model filenames are not used to infer the missing identity.

Each mount now carries an observation-only `EquipmentMountIdentity` value snapshot.
Shoulders take it directly from the `CharacterEquipment.Piece` that created each mount;
other attachments use their existing construction identity. The remote observation
reports that snapshot. Legacy render/effect owner fields, attachment IDs, model/texture
resolution, materials, visibility and transforms are unchanged. Runtime recapture is
required to establish complete remote evidence after this correction.

Focused checks: `dotnet run --project tools/interface-wire-check -- --equipment-mount-identity-only`.


### 2026-09-28 — explicit offline appearance inputs

An optional top-level `appearance` object accepts `skin`, `face`, `hairStyle`,
`hairColor` and `facialHair` character-creation indices. Explicit appearance
requires one race and one sex per manifest; use separate output directories for
different appearances. Omission preserves the previous choice-zero behavior and
capture filenames. Example: `"appearance":{"skin":0,"face":0,"hairStyle":2,
"hairColor":0,"facialHair":0}`; valid indices depend on that body and mounted DBCs.

The offline harness validates skin/face rows, hairstyle mapping, hair color and
facial-style mapping before loading the body. Invalid requests produce technical
errors instead of silently testing another style. The renderer's original hair
sheet substitution policy remains unchanged. Captures record the actual five
choices, hair resolution and active geosets; assets include the three appearance
DBCs. This does not alter character appearance during gameplay. A single valid
hairstyle or a hidden-hair helmet does not prove every hairstyle fits.

Validation of this extension is pending until builds and explicit native positive/
negative probes are recorded by the implementing task.

2026-09-28 independent validation: isolated Debug/Release builds and the retained
native run were reviewed without promoting the normal Release binary. The 16
manifest checks and SharedDocs check pass. Four positive PNGs retain verified
hashes and expected actual geosets (legacy Human male zero; explicit hairstyle 9,
color 2, facial style 4). Hairstyle 255 exits with code 4, records
`appearance-hair-style-unavailable`, and produces no PNG. Evidence is retained in
MangosSuperUI `scratch/equipment-batch/appearance-study/appearance-validation.json`.

The read-only `AppearanceAudit` helper checked all 1,265 hairstyle/color pairs
admitted by the mounted Vanilla tables: every pair has an exact hair section row
and an exact or intentional variation-1 substitute sheet. Several valid facial
mapping choices have no facial texture row (including female decorations and
Tauren horn choices); absence alone is not an invalid appearance. The report binds
the actual three DBC hashes in `independent-combination-audit.json`. This audit
checks current table combinations, not every referenced texture or a corrupted
custom DBC. Production texture fallbacks remain unchanged and texture-slot sources,
hair resolution and warnings must still be reviewed. No armor fit, all-hairstyle,
cosmetic acceptance or in-world claim follows from these smoke checks.
### 2026-09-28 — mesh sidecars record the applied appearance

Corrected the equipment mesh sidecar's obsolete hardcoded zero appearance. It
now reads SkinId, FaceId, HairStyleId, HairColorId and FacialHairId from the same
applied CharacterRenderer used by captures.json, and records explicitRequest.
The change only observes state; normal appearance selection and rendering are
unchanged. Historical appearance-zero probes remain valid. The old nonzero
appearance-study sidecar is retained but its false-zero appearance fields are
superseded; its paired capture metadata already recorded the actual appearance.

Both isolated Debug and Release builds passed with 19 existing warnings and zero
errors. A verifier rejects that retained old nonzero sidecar, then passes four
new native frames: two legacy-zero and two Human male hair9/color2/facial4, with
matching capture/mesh fields, visible geosets, PNG/probe hashes and mounted DBC
hashes. Invalid hair255 still exits4 without a PNG. Exact source and isolated
client hashes are in MangosSuperUI scratch/equipment-batch/appearance-probe-study/
validation.json. Standard Release was not promoted. This is diagnostic wiring
proof, not all-hairstyle fit or cosmetic acceptance.

### 2026-09-28 — original type8 skin-extra routing and paired proof

Original Tauren tail/mane batches now bind the independent skin-section Texture2
declared by mounted CharSections.dbc. Type8 is not underwear and is never the
dressed type1 atlas. The controlled renderer previously skipped its unbound
type8 batches: the apparent garment stripe at the tail root was underlying body
visible behind missing tail geometry. Male geoset1501 includes both a type1 hump
and a type8 tail; female extra surfaces share geoset0. No geoset is suppressed.

CharSectionsTable.SkinExtraTexture follows race/sex/skin color and returns empty
for absent declarations or negative choices. Controlled sync/async, remote
player/NPC and legacy player preparation use this selector. Missing type8 is
excluded from previous-batch texture fallback; type1 composition is unchanged.
The old ignored SYSTEM_CHARACTER.md section4/underwear note is corrected.

Both isolated builds passed (0 errors,19 existing warnings). The focused command
`interface-wire-check --character-skin-extra-only <mounted Data>` passes23 checks,
including exact original Tauren skin-zero texture hashes, wrong-section decoys,
generic race/sex/color selection, shared-geoset texture separation and retained
equipment composition. Normal Release and installed archives remain unchanged.

The native same-mount pair covers six outfits (four raid drafts and original
Valor/Magister), Human/Tauren both sexes and front/left/right/back:96 before and96
after frames, zero technical errors. All48 Human controls are pixel-identical;
all48 Tauren frames change. All Tauren after views were personally inspected:
complete furred tails and mane surfaces are restored while torso clothing stays.
Exactly two original skin-extra assets are newly bound; common asset hashes are
unchanged and none removed. Full source/build/image provenance is in the web repo
`artifacts/equipment-workshop/armor-redesign/raid-v3/skin-extra-diagnostic/TYPE8_CORRECTION.md`
and `native-pair-v1/pair-audit.json`. Fixed isolated DLL SHA256:
`7657c2da7aba749b60fcb3aeac5688a6100bb52682a3ee990e4f510c519df9ea`.

This proves static controlled-renderer behavior at skin0. Actual remote runtime,
other skin colors, motion and complete armor acceptance remain separate gates.

### 2026-09-28 — tested skin-extra client promoted

The exact tested DLL `7657c2da7aba749b60fcb3aeac5688a6100bb52682a3ee990e4f510c519df9ea` and matching PDB were promoted to normal Release after verifying its previous DLL and confirming no normal client process was running. The old pair is retained outside the repository at `C:/Users/nico/AppData/Local/Temp/msui-skin-extra-client-df2a9986d6154ef2894c73edfdcea1e5`. No rebuild incorporated additional source changes; configuration, dependencies, shaders and installed MPQs were untouched.

A normal-path offline smoke rendered Human/Tauren, both sexes, three angles:12 frames,4 posed meshes,21 bound native members and zero technical errors. Every PNG is byte-identical to the matching isolated-binary render on the same mount. Receipt: web repository `artifacts/equipment-workshop/weapon-redesign/raid-v3/native-v5/client-promotion.json`. Live remote behavior and final equipment acceptance remain unverified.


### 2026-09-29 — authored hand closure implemented; numerical verification only

The isolated candidate applies original body M2 animation 15 (`HandsClosed`)
rotations to the occupied hand's authored finger key-bone roots and descendants.
The right roots are 8–12 and the left roots 13–17; sparse original mappings remain
sparse, with no guessed race-specific bone indices. Sampling uses the absolute
sequence start and its key range, including the original bracketing keys where
no key lies inside the short sequence. Only finger rotation is overlaid after
the ordinary pose/action layers. Base translation, scale and unrelated bones
continue unchanged; this does not play HandsClosed as a whole-body animation.

Closure follows actual visible, resolved palm attachments: attachment 1 closes
the right hand and attachment 2 the left. A wrist-mounted shield does not close
the left hand; stowed or hidden weapons do not close a palm. Bow/wand placement
uses the existing attachment and effective sheath policy. Casting itself is not
an unconditional grip override: actual held presentation determines occupancy.
Controlled, player and creature render paths pass their own occupancy into pose
evaluation. The local renderer caches cloned mount entries and retains the same
mount snapshot, effective sheath and grip through FreezePose; creature tactical
freeze likewise retains that presentation. Model/cache resets clear the local
latch. No original body mesh, weights, geosets, atlas rules, equipment source
assets or attachment transforms were edited for this correction.

Final independent numerical receipt, in the MangosSuperUI repository:
`artifacts/hand-pose-check-v1/verification-final-v1.json`, SHA256
`441d1828678c48c059c04a19cc457f874e3b860aacedfd92cd9e7d7e4a7cc32e`.
Each final Debug and Release candidate passed 55,040 assertions plus 33 census
assertions, including 8,640 selected-hand cases over 16 original bodies and 20
animation IDs with ordinary, cross-faded and layered evaluation. All 2,880 None
pose hashes remain bit-exact against the old Release baseline; unselected bones
and wrist/weapon attachment matrices remain exact. The independent oracle reads
original rotation arrays rather than calling HandGripPose. Synthetic checks
also cover missing/sparse channels, range interpolation and snapshot isolation.
The retained old-client run fails the source closure check on all 16 bodies.

Exact isolated runtime DLLs under the web repository's
`artifacts/equipment-workshop/hand-grip-v1/`:

- `build-release-v2/MSUIClient.dll` SHA256 `8f0588ed98c9663ddceb58627a65e16d20977f662479ec0b84c069ece3401af8`.
- `build-debug-v2/MSUIClient.dll` SHA256 `c5fc997d4dad0a2718a40e140bc07d4b32f75b3f3cccf5d2e4e0ca660f23109f`.

Reproduction and test scope: [hand-pose-check tool guide](../tools/hand-pose-check/README.md).
The guarded capture runner and its preparation guide are in the web repository:
`artifacts/equipment-workshop/hand-grip-v1/Invoke-GuardedHandGripCapture.ps1`
and the adjacent `README.md`. Copy-paste preflight only (no `-Launch`):

```powershell
$gripRoot = 'C:/Users/nico/source/repos/MangosSuperUI/artifacts/equipment-workshop/hand-grip-v1'
& "$gripRoot/Invoke-GuardedHandGripCapture.ps1" `
  -ClientDll "$gripRoot/build-release-v2/MSUIClient.dll" `
  -ExpectedDllSha256 '8f0588ed98c9663ddceb58627a65e16d20977f662479ec0b84c069ece3401af8' `
  -PartKey HuM-stock-custom-standing-smoke `
  -OutputDirectory "$gripRoot/native-release-smoke-NEW"
```

At this checkpoint the candidate is **not installed, native-visually verified,
or live-verified**. The user's answer about closing their open Debug session
PID 61396 is pending; that PID is a recorded checkpoint, not a fresh process
check. The runner refuses existing or unreadable client processes and never
closes them. Root coordinates the native slot before any `-Launch` use, with a
new output directory and fresh preflight. Numerical tests neither render pixels
nor prove finger/handle seating, live transitions, or freeze integration in a
rendered session. Existing cosmetic acceptance remains historical evidence of
its exact binaries and assets; it does not establish this new finger-grip fix.
All previous captures, failures, source receipts and acceptance ledgers remain
preserved. Offline forced cast/attack samples also do not prove live-state policy.

### 2026-09-30 — hand closure v4 installed (mesh-probe fix + presented-animation law)

The numerically verified v2 candidate failed its first native capture: with
`MSUI_EQUIPMENT_MESH_PROBE_WEAPONS=1` every frame reported
`mesh-probe-missing-submitted-mounts`, because the character now draws cloned snapshot
mounts and the probe compared by reference. `AttachedItemRenderer.Mount.SnapshotOrigin`
now links a clone to its built mount and `GameLoop.EquipmentMeshProbe` counts the origin,
so a stale snapshot still fails. Pixels are unchanged by this fix.

Native review then showed TaurenMale's spell/kneel/swim animations carry the hand
attachment ~0.3 off the palm, leaving a fist beside the weapon. The original client stows
the weapon for animations whose AnimationData.dbc WeaponFlags have 0x4 (always) or 0x10
(except drawn-ranged handling ids 46,49,105–107,109–112), so its palm is empty there.
`HandGripLaw.ForPresentedAnimation` (World/Units/HandGripPose.cs) keeps the animation's own
fingers under such an animation; flags come from `AnimationDataCatalog.WeaponFlagsFor`.
Local (inspection pose, masked action, else full-body clip), remote (torso overlay else clip,
before the freeze latch) and legacy player paths all apply it. MSUIClient still does not
stow weapons itself (the original per-animation sheath reconcile is not implemented).

Evidence (web repo `artifacts/equipment-workshop/hand-grip-v1/`): 270 native frames per
binary over HuM/HuF/TaM stand/run/attacks/cast with stock and custom weapons, pair boards and
probe seating reports; v4 = v3 on 240 frames and = the old client on all 60 forced-cast
frames. `tools/hand-pose-check`: 55,059 checks per build, fails on v3 by design. Installed
Release `162336f6…`, Debug `c8d513de…` (rollback outside the repo), byte-identical smoke
after install, and live self/remote observation. Seal: `build-checkpoint-v4/checkpoint.json`.
Offline forced poses remain samples; the other 13 bodies have numerical, not native, proof.

## 2026-09-30 — volumetric art candidates reviewed with this capture system

The web repo's volumetric pipeline (`docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`, tools in
`tools/equipment-workshop/volumetric/`) reviews every candidate with the captures described here.

Weapons:
- `Invoke-NativeReview.ps1 -MeshProbe` captures 16 bodies x 2 views x 2 poses, with weapon mesh probes on.
- `grip_seating_check.py` measures hand seating from those probes.
- `prepare_installed_baseline.py` mounts the installed data with no review overlay, so installed and
  candidate weapons are captured with the same manifests. The before/after sheets come from those pairs.

Armor:
- `Invoke-ArmorCandidateReview.ps1` runs `ArmorPrototype --package` into a private overlay, then a bound
  capture of 16 bodies x 6 views x 4 poses: stand, attack-1h 17, cast-omni 52, run 5.
- That is 64 mesh probes and 384 frames.
- `armor_sheet.py --focus helm|shoulders` crops each frame from its own viewProjection and the recorded
  helm/shoulder placements.

Lessons for this system:
- Mesh probes weld positions across draws. A helm made of separately built parts that share a ring welds
  into non-manifold edges, so the production validator rejected it. Local gates must weld by position
  too.
- The fit baselines (`fit-baselines-valor`) hold the whole visible body in each attachment's frame. Frames
  differ per body: helm origins are not at the same place on each skull, and ScM's shoulder frame is
  rotated. Fit against those baselines. Never add runtime offsets.
- Numbers never replaced looking. The largest defects were all found on native sheets:
  - pads rising to eye level;
  - white-plane blades;
  - undersized weapons;
  - masks reading as visors.