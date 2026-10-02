# Hand pose check

Read-only console checks for vanilla equipment-driven finger closure. No window,
OpenGL context, login, inventory operation or asset mutation is performed. Outputs
must be new files. Do not overwrite earlier failures or use this numerical proof
as rendered/live acceptance.

Run from the MSUIClient repository. Use an explicit candidate assembly and an
isolated output directory to avoid rebuilding or replacing the normal client:

```powershell
$candidate = 'C:/Users/nico/source/repos/MangosSuperUI/artifacts/equipment-workshop/hand-grip-v1/build-release-v1/MSUIClient.dll'
$toolOutput = 'C:/Users/nico/source/repos/MangosSuperUI/artifacts/hand-pose-check-v1/tool-candidate-NEW'
dotnet build tools/hand-pose-check/hand-pose-check.csproj -c Release "-p:ClientAssembly=$candidate" -o $toolOutput
dotnet "$toolOutput/hand-pose-check.dll" --census C:/Users/nico/source/repos/MSUIClient/GameData/Data C:/Users/nico/source/repos/MangosSuperUI/artifacts/hand-pose-check-v1/census-NEW.json
dotnet "$toolOutput/hand-pose-check.dll" --baseline C:/Users/nico/source/repos/MSUIClient/GameData/Data C:/Users/nico/source/repos/MangosSuperUI/artifacts/hand-pose-check-v1/baseline-NEW.json
dotnet "$toolOutput/hand-pose-check.dll" --verify C:/Users/nico/source/repos/MSUIClient/GameData/Data C:/Users/nico/source/repos/MangosSuperUI/artifacts/hand-pose-check-v1/regression-NEW.json
```

`--census` records the actual mounted supplier and SHA for AnimationData.dbc and
all 16 original body M2s, animation15 and Stand/Ready/attack finger channels,
key-bone mappings, descendants, and raw keys bracketing the sequence start.
All16 current bodies have HandsClosed; 33ms except NightElf male/female and
Scourge male at34ms. Finger keybone entries are sparse on several bodies; absent
IDs must never be replaced with guessed race-specific indices.

`--baseline` records bit-exact hashes of the unmodified pose matrices, allowing
old/new client comparison without requiring HandGrip on the old client.

`--verify` additionally checks the public evaluator and attachment-occupancy
APIs. It independently samples the parsed source rotation arrays at the original
HandsClosed absolute start, reconstructs local transforms from the unchanged
base pose, and substitutes only the selected finger rotations. It does not call
HandGripPose or use the baked HandsClosed clip as the expected result.

The source sampling convention is documented in the available reference at
`C:/Users/nico/Desktop/benilla-main/crates/benilla-formats/src/models/key_anim.rs`
lines161–210 and `anim.rs` lines655–721: the first sample is bounded to the
inclusive range, its next interpolation key may be the next key in the complete
track, and the interpolation fraction is clamped. A whole-body HandsClosed
animation or channel copy is wrong: the base translation/scale and every other
body part must continue unchanged.

Regressions cover None/right/left/both, several times in Stand/Walk/Run/Ready/
attacks/casts, cross-fading and layered arm/torso/reaction evaluation, exact
unselected bones and wrist/weapon attachment matrices, repeatability after
closure, missing pose/roots/rotation, global-track exclusion, STEP and linear
sparse ranges, key-bone-tag fallback, rotation-only preservation, and actual body
attachment occupancy for stow/draw, dual wield, shield, bow, wand and visibility.
The GL-backed renderer is never instantiated; reflection populates its retained
MountSet and calls only the public pure ResolveHandGrip seam.
The pure snapshot fixture also checks that visibility and refreshed mount data
cannot mutate a retained frozen-pose snapshot; shared model resource identity is
preserved. It never calls the GL constructor, render or disposal methods.

Against the old client without HandGrip, verification still evaluates its real
base matrices and reports the source-rotation mismatch. This permits a meaningful
failure-before/pass-after comparison; it does not merely assert an API exists.

Exit0 means all requested checks passed. Exit1 means a recorded regression failed;
argument/IO/parse failures may abort before a report exists. A successful numeric
run still needs separately coordinated native screenshots and personal review.

2026-09-30: `--verify` additionally checks snapshot clone origins (2 checks) and
`HandGripLaw.ForPresentedAnimation` (17 checks: synthetic WeaponFlags tables plus the mounted
AnimationData.dbc — 0x4 stows always, 0x10 stows except drawn-ranged handling, 0x20/0 keep
occupancy). Final v4 builds pass 55,059 checks each; the same tool built against the v3 DLL
fails with "HandGripLaw.ForPresentedAnimation missing", which is the intended negative control.
Evidence: web repo `artifacts/hand-pose-check-v4/`.
