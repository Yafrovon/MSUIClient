using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH WINDOW — shared_docs/SPELL_SKETCH.md §1 and §3 (slices S2-S4).
//
// The friendly face of Creator/Sketch/. The compiler there turns a SketchDoc
// into a real vanilla M2 plus its BLPs; this file is the window that edits the
// doc, and the plumbing that puts the result in front of the owner while they
// type.
//
// THE POINT (§1): click Crescent, get a crescent at your chest. Colour it,
// stand it up, Ctrl+V it, tell it to fly 3 yards, and watch the loop play it.
// If any step needs a second panel, a hidden mode or a number the owner has to
// derive, the slice is not done.
//
// The four cards are always in this order and always all visible - LOOK, PLACE,
// MOVE, EXTRAS - with no collapsing, because a card that hides is a card you
// forget exists. Everything previews live; there is no Apply button.
//
// HOW A SKETCH REACHES THE SCREEN (the minimal mimic of RebuildCreatorModel):
//   MpqMount.SetOverride(path, bytes)          - makes the custom path exist
//   SpellEffectSource.SetModelOverride(...)    - the effect-instance layer
//   SpellParticleSystem.SetGeometryModelOverride
//   SpellEffectMeshRenderer.InvalidateModel    - REQUIRED: meshes bake per path
//   SetCreatorSlot / SetCreatorMissile         - puts it in the phase's kit
// The path string IS the identity; there is no handle to hold.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private bool _sketchOpen;
    private SpellStage _sketchStage = SpellStage.Cast;
    private int _sketchSelected;
    private SketchPiece? _sketchClipboard;
    /// <summary>Base (feet), NOT Chest.
    ///
    /// Measured on HumanMale, NightElfFemale, OrcMale and TaurenMale by walking the real
    /// SpellAttachment.World: Base resolves to a parentless, key-less bone with an identity
    /// basis, so it is 0.0 degrees off the caster's facing in EVERY animation on EVERY race.
    /// Chest is 47.6 degrees off during SpellCastDirected (82.7 on a Night Elf) and swings
    /// another 9.7 while the clip plays - which is exactly the "it curves along the body"
    /// the owner reported.
    ///
    /// Nothing is lost by moving: the default Origin is already (0, 0, 1.2), so a piece on
    /// Base still appears at chest height - it just does so in a frame that holds still.</summary>
    private int _sketchSlot = 2;
    private string _sketchStatus = "";
    private bool _sketchDeleteArmed;

    /// <summary>Keep the phase being edited ON SCREEN instead of letting it flash past.
    ///
    /// A cast is self-terminating: it lives for its own sequence length - half a second by
    /// default - and the loop re-presents it every couple of seconds. You cannot drag a handle
    /// on something that exists for a quarter of the time, which is why the owner was pausing
    /// the clock by hand to get a frame to work in. While this is on, the edited phase spawns
    /// persistent and simply stays.</summary>
    private bool _sketchHold = true;

    /// <summary>Shape-keyed, so dragging a colour or motion dial never re-rasterizes.</summary>
    private readonly SketchWriter.SketchTextureCache _sketchTextureCache = new();
    private readonly BlpWriterService _sketchBlpWriter = new();

    /// <summary>A pending recompile, rate-limited the way a handle drag is: a slider being
    /// dragged fires every frame, and a recompile per frame is work nobody asked for.</summary>
    private bool _sketchRebuildPending;
    private double _sketchRebuildEarliest;
    private const double SketchRebuildThrottleSeconds = 0.08;

    private static readonly string[] SketchShapeNames =
        { "Crescent", "Ring", "Disc", "Arrow", "Line", "Star", "Rect",
          "Sphere", "Cylinder", "Cone", "Torus", "Beam" };
    private static readonly SketchShapeKind[] SketchShapeKinds =
    {
        SketchShapeKind.Crescent, SketchShapeKind.Ring, SketchShapeKind.Disc,
        SketchShapeKind.Arrow, SketchShapeKind.Line, SketchShapeKind.Star, SketchShapeKind.Rect,
        // Solids (SPELL_CREATOR_PLAN.md P3.5): real meshes an image wraps round.
        SketchShapeKind.Sphere, SketchShapeKind.Cylinder, SketchShapeKind.Cone,
        SketchShapeKind.Torus, SketchShapeKind.Beam,
    };

    // The LOOK card's archive image picker (P3.6): every effect image in the game, searchable,
    // as thumbnails. Listed once from the mounted archives.
    private bool _sketchImagePickerOpen;
    private readonly byte[] _sketchImageSearchBuf = new byte[64];
    private List<string>? _sketchImageFiles;
    private static readonly string[] SketchImageChips =
        { "lightning", "fire", "frost", "arcane", "shadow", "holy", "nature", "swirl", "glow", "smoke", "ball" };
    private static readonly string[] SketchFacingNames =
        { "Vertical, forward", "Vertical, side", "Flat", "Face camera", "Custom" };
    private static readonly string[] SketchBlendNames = { "Additive", "Alpha", "Modulate" };
    private static readonly string[] SketchTravelNames = { "Fixed distance", "To the target" };
    private static readonly string[] SketchDirectionNames = { "Forward", "Left", "Up", "Custom" };
    private static readonly string[] SketchEaseNames = { "Linear", "Ease out" };

    /// <summary>How steady each attachment slot is, measured rather than guessed (see the
    /// comment on _sketchSlot). Index matches CreatorSlotNames.</summary>
    private static readonly string[] SketchSlotSteadiness =
    {
        "steady - a fixed point at head height, it does not turn when the caster looks around",
        "SWINGS - up to 48 degrees off the caster's facing during a cast, and moving",
        "steady - the model's own origin, identical to the caster's facing in every animation",
        "follows the left hand, all of it",
        "follows the right hand, all of it",
        "follows the head",
        "NOT ON PLAYER MODELS - silently falls back to a spine bone that swings more than Chest",
        "NOT ON PLAYER MODELS - silently falls back to a spine bone that swings more than Chest",
        "NOT ON PLAYER MODELS - silently falls back to a spine bone that swings more than Chest",
    };

    // ── The doc ─────────────────────────────────────────────────────────────

    private SketchDoc SketchForStage(CreatorSpellDoc doc, SpellStage stage)
    {
        if (doc.Sketches.TryGetValue(stage, out SketchDoc? existing)) return existing;
        var created = new SketchDoc { Stage = stage.ToString().ToLowerInvariant() };
        doc.Sketches[stage] = created;
        return created;
    }

    /// <summary>Should this stage be held on screen because it is the one being sketched?
    /// Read by PresentSpellEffect when it decides whether the kit is persistent.</summary>
    private bool SketchHoldsStage(string stage) =>
        _sketchHold && _sketchOpen && _creatorSpell is { } holdDoc &&
        Enum.TryParse(stage, ignoreCase: true, out SpellStage parsed) && parsed == _sketchStage &&
        holdDoc.Sketches.TryGetValue(parsed, out SketchDoc? held) && held.Pieces.Count > 0;

    /// <summary>While Hold is on the loop plays ONLY the phase being sketched.
    ///
    /// Persistence alone is not enough to keep a piece on screen, because there is exactly one
    /// presented kit: PresentSpellEffect reaps whatever was showing before it spawns. So a
    /// planned cast + impact takes it in turns, and the cast is torn down a beat after it
    /// appears - which looks exactly like the flicker the owner was trying to pause through.
    /// Holding therefore means soloing: the other phases stand down while you work, and their
    /// ticks in the anatomy list are untouched so nothing is lost when you switch it off.</summary>
    private bool SketchSoloBlocks(SpellStage stage) =>
        _sketchHold && _sketchOpen && stage != _sketchStage && _creatorSpell is { } soloDoc &&
        soloDoc.Sketches.TryGetValue(_sketchStage, out SketchDoc? soloed) && soloed.Pieces.Count > 0;

    /// <summary>Is the loop actually playing the phase being edited?
    ///
    /// Only Cast and Impact loop by default (GameLoop.Creator.Spells.cs:209-214). Everything
    /// else - precast, missile, channel, state - is off until someone ticks it in the Loop row.
    /// So it was possible to draw a piece into a precast, watch it compile, see its model and
    /// its bones appear in the tree, and have NOTHING on screen, with nothing anywhere saying
    /// why. That is the worst failure this window can have, and it is the one that happened.</summary>
    private bool SketchPhaseIsPlaying(SpellStage stage) => stage switch
    {
        SpellStage.Precast => _creatorLoopPrecast,
        SpellStage.Cast => _creatorLoopCast,
        SpellStage.Impact => _creatorLoopImpactPhase,
        SpellStage.State => _creatorLoopStateHold,
        SpellStage.Channel => _creatorLoopChannelHold,
        _ => false,
    };

    private void SetSketchPhasePlaying(SpellStage stage, bool on)
    {
        switch (stage)
        {
            case SpellStage.Precast: _creatorLoopPrecast = on; break;
            case SpellStage.Cast: _creatorLoopCast = on; break;
            case SpellStage.Impact: _creatorLoopImpactPhase = on; break;
            case SpellStage.State: _creatorLoopStateHold = on; break;
            case SpellStage.Channel: _creatorLoopChannelHold = on; break;
        }
        _creatorLoopNextAt = 0;
    }

    /// <summary>Editing a phase turns its preview on. Authoring into something you cannot see
    /// is never what anyone meant.</summary>
    private void SketchEditStage(SpellStage stage)
    {
        _sketchStage = stage;
        _sketchSelected = 0;
        if (!SketchPhaseIsPlaying(stage)) SetSketchPhasePlaying(stage, true);
        SketchChanged();
    }

    private SketchPiece? SelectedSketchPiece(SketchDoc sketch) =>
        _sketchSelected >= 0 && _sketchSelected < sketch.Pieces.Count ? sketch.Pieces[_sketchSelected] : null;

    /// <summary>Any card change lands here. The recompile itself happens on the next update
    /// tick so a drag does not pay for one per frame.</summary>
    private void SketchChanged()
    {
        _sketchRebuildPending = true;
        _sketchRebuildEarliest = NowSeconds() + SketchRebuildThrottleSeconds;
    }

    /// <summary>Driven from the creator update, beside the rest of the workshop.</summary>
    private void UpdateCreatorSketch()
    {
        if (_creatorSpell is not { } doc) return;
        SketchDoc sketch = SketchForStage(doc, _sketchStage);

        // Anything the owner dragged in the WORLD is folded back into the cards first, so the
        // handles and the cards can never disagree about where a piece is.
        if (AdoptSketchBoneEdits(doc, sketch)) SketchChanged();

        if (!_sketchRebuildPending) return;
        if (NowSeconds() < _sketchRebuildEarliest) return;
        _sketchRebuildPending = false;
        CompileSketch(doc, sketch);
    }

    /// <summary>Drag a piece in the world and the cards catch up.
    ///
    /// The translate arrows and rotate rings write into the MODEL's `BoneEdits`, which
    /// `RebuildCreatorModel` re-applies on top of the authored bytes. But a Sketch piece's bone
    /// is not authored by hand - it is GENERATED from the cards on every recompile - so the two
    /// would fight: drag the piece, change any card, and the drag silently vanishes because the
    /// bone was rebuilt from PLACE. There can only be one source of truth, and for a sketch it
    /// has to be the sketch.
    ///
    /// So a bone edit on a sketch model is treated as INPUT: read it into the piece's PLACE card
    /// and drop the edit. The rings become the orientation control, the arrows become the origin
    /// control, the numbers on the card update as you drag, and nothing is lost on the next
    /// recompile. A ring drag also flips Facing to Custom, because that is now literally true -
    /// and the three degrees it shows are the AutoCAD-style read-out of where the piece points.</summary>
    private bool AdoptSketchBoneEdits(CreatorSpellDoc doc, SketchDoc sketch)
    {
        if (sketch.Pieces.Count == 0) return false;
        string modelPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
        if (!doc.Models.TryGetValue(modelPath, out CreatorModelDoc? model) ||
            model.BoneEdits.Count == 0) return false;

        bool adopted = false;
        foreach (int bone in model.BoneEdits.Keys.ToList())
        {
            int index = bone - 1;                       // bone 0 is the static root
            if (index < 0 || index >= sketch.Pieces.Count) { model.BoneEdits.Remove(bone); continue; }

            BonePatch patch = model.BoneEdits[bone];
            SketchPiece piece = sketch.Pieces[index];

            // The pivot IS the piece's origin (SketchWriter writes it there), so a translate
            // drag reads straight back into the card, raw frame and all.
            Vector3 origin = piece.Place.Origin;
            if (patch.PivotX is { } px) origin.X = px;
            if (patch.PivotY is { } py) origin.Y = py;
            if (patch.PivotZ is { } pz) origin.Z = pz;
            if (origin != piece.Place.Origin) { piece.Place.Origin = origin; adopted = true; }

            Vector3? turned = patch.Rotation is { } raw
                ? M2BoneParser.QuaternionToEuler(raw)
                : patch.RotationEulerDegrees;
            if (turned is { } euler)
            {
                piece.Place.Facing = SketchFacing.Custom;
                piece.Place.CustomEuler = euler;
                adopted = true;
            }

            model.BoneEdits.Remove(bone);
        }
        return adopted;
    }

    // ── Compile and push ────────────────────────────────────────────────────

    /// <summary>Sketch -> bytes -> the running effect. The compiled model becomes an ordinary
    /// phase model, so the outliner shows it, its bones appear, and every existing dial and
    /// handle works on it - which is the whole premise of the feature.</summary>
    private void CompileSketch(CreatorSpellDoc doc, SketchDoc sketch)
    {
        string modelPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
        string renderPath = SpellVisualCatalog.ModelPath(modelPath);

        if (sketch.Pieces.Count == 0)
        {
            ClearSketchModel(doc, sketch, modelPath, renderPath);
            _sketchStatus = "No pieces yet - click a shape.";
            return;
        }

        // While holding, the bytes we PREVIEW loop. The bytes that get exported never do -
        // this flag is an editing convenience, not part of the spell.
        bool holding = _sketchHold && _sketchOpen && !sketch.IsMissile;
        if (CompileSketchModel(doc, sketch, holding) is not { } build)
        {
            _sketchStatus = sketch.Pieces.Count > SketchWriter.MaxPieces
                ? $"Too many pieces (max {SketchWriter.MaxPieces})."
                : "The sketch could not be compiled.";
            return;
        }

        bool missile = sketch.IsMissile;
        bool placed = missile
            ? string.Equals(doc.MissileOverride, modelPath, StringComparison.OrdinalIgnoreCase)
            : doc.Composition.TryGetValue(_sketchStage, out CreatorStageComposition? composition) &&
              string.Equals(composition.Slots[_sketchSlot], modelPath, StringComparison.OrdinalIgnoreCase);

        if (!placed)
        {
            // A sketch occupies exactly ONE place. Moving it from Base to Left hand used to
            // FILL the new slot without emptying the old one, so the same model played from
            // both and it looked like the piece had been duplicated - with no new piece in the
            // list to explain where the second one came from. Clear every slot this model is
            // sitting in before taking the new one.
            ReleaseSketchModelFromSlots(doc, modelPath, missile ? -1 : _sketchSlot);
            if (missile) SetCreatorMissile(doc, modelPath);
            else SetCreatorSlot(doc, _sketchStage, _sketchSlot, modelPath);
        }
        else
        {
            ReapPresentedEffect();
            _creatorLoopNextAt = 0;
        }

        // Compiling a phase means someone is working on it, so it had better be playing - and
        // the LOOP itself has to be running, or the phase list is moot. Drawing a piece into a
        // stopped loop is the same silence as drawing into an unplayed phase, and the author
        // has no reason to connect either one to a transport button they never touched.
        if (!sketch.IsMissile && !SketchPhaseIsPlaying(_sketchStage))
            SetSketchPhasePlaying(_sketchStage, true);
        if (!_creatorLoopOn) ToggleCreatorLoop();

        // Re-apply the selection now that the model EXISTS. SelectSketchPiece needs a compiled
        // model to point the bone pick and the handles at, and on the very first piece there is
        // none yet - the compile is throttled to the next tick, so the select ran, found nothing
        // and returned. That is why the drag handles never appeared on a freshly added piece.
        if (_sketchSelected >= 0 && _sketchSelected < sketch.Pieces.Count)
            SelectSketchPiece(doc, sketch, _sketchSelected,
                // A recompile re-points the handles; it must not yank the inspector away from a
                // part the author chose (measured 2026-10-26: Rules selected, the next card change
                // put the missile model back in the inspector).
                takeInspector: _spellIdeSelection.Kind is SpellIdeKind.Phase or SpellIdeKind.Emitter
                    or SpellIdeKind.Mesh or SpellIdeKind.Ribbon or SpellIdeKind.Spell);

        if (CreatorSpellPaused) ReplayCreatorEffects();

        _sketchStatus = $"{build.PieceCount} piece(s), {build.BatchCount} quad(s), " +
                        $"{build.LengthSeconds:0.00}s, {build.Model.Length} B" +
                        (missile ? "  (missile)" : $"  ({CreatorSlotNames[_sketchSlot]})");
    }

    /// <summary>Where a sketch's sparks and trails are copied from: the mounted game archives.</summary>
    private Func<string, byte[]?> SketchArchive => path => _mpq.ReadFile(path);

    /// <summary>Compile one sketch and install the result everywhere a renderer or the doc reads
    /// it, WITHOUT deciding where it sits (CompileSketch places it; reopening a saved design
    /// restores the composition instead). Null when the writer refuses the doc.</summary>
    private SketchWriter.SketchBuild? CompileSketchModel(CreatorSpellDoc doc, SketchDoc sketch, bool loopForEditing)
    {
        string modelPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
        string renderPath = SpellVisualCatalog.ModelPath(modelPath);
        SketchWriter.SketchBuild? build = SketchWriter.Build(
            sketch, (int)doc.Info.Id, doc.Info.Name, _sketchBlpWriter, _sketchTextureCache,
            loopForEditing: loopForEditing, archive: SketchArchive);
        if (build is null) return null;
        // A spark or trail whose donor could not be copied is skipped, never a failed build -
        // but it must not be skipped SILENTLY.
        if (build.Notes is { Count: > 0 } notes) _sketchStatus = "Not copied: " + string.Join("; ", notes);

        // The textures first: the model names them, so they must resolve when it is parsed.
        foreach (SketchWriter.SketchTextureFile texture in build.Textures)
        {
            _mpq.SetOverride(texture.Path, texture.Blp);
            doc.SketchTextures[texture.Path] = texture.Blp;
        }

        // The mount override is what makes the custom path EXIST: EnsureCreatorModel reads
        // the "original" back through the mount, and without this there is nothing to read.
        _mpq.SetOverride(modelPath, build.Model);
        _spellEffects?.SetModelOverride(modelPath, build.Model);
        _spellParticles?.SetGeometryModelOverride(renderPath, build.Model);
        _spellEffectMeshes?.InvalidateModel(renderPath);       // meshes bake per path at first draw

        RefreshSketchModelDoc(doc, modelPath, build.Model);
        return build;
    }

    /// <summary>The bytes a sketch SHIPS: recompiled one-shot (never the editing loop), then
    /// every dial the author turned on the compiled model re-applied on top - the same
    /// composition the preview uses, on the bytes the game will play (SPELL_CREATOR_PLAN.md
    /// P0.1; before it, a sketch's model left the client with no bytes at all, because its
    /// preview original and working copy were the same array and the export only shipped a
    /// model whose working copy DIFFERED from its original).</summary>
    private (byte[] Model, IReadOnlyList<SketchWriter.SketchTextureFile> Textures)? BuildSketchExport(
        CreatorSpellDoc doc, SketchDoc sketch)
    {
        SketchWriter.SketchBuild? build = SketchWriter.Build(
            sketch, (int)doc.Info.Id, doc.Info.Name, _sketchBlpWriter, _sketchTextureCache,
            loopForEditing: false, archive: SketchArchive);
        if (build is null) return null;
        string modelPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
        byte[] model = doc.Models.TryGetValue(modelPath, out CreatorModelDoc? tuned)
            ? ComposeCreatorModelBytes(tuned, build.Model, out _)
            : build.Model;
        return (model, build.Textures);
    }

    /// <summary>Take this sketch's model out of every attachment slot and out of the missile,
    /// except the one it is about to occupy. Leaving it in two places is not a second piece -
    /// it is the same piece playing twice, which is exactly as confusing as it sounds.</summary>
    private void ReleaseSketchModelFromSlots(CreatorSpellDoc doc, string modelPath, int keepSlot)
    {
        foreach (SpellStage stage in CreatorStages)
        {
            if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) continue;
            for (int slot = 0; slot < composition.Slots.Length; slot++)
            {
                if (stage == _sketchStage && slot == keepSlot) continue;
                if (string.Equals(composition.Slots[slot], modelPath, StringComparison.OrdinalIgnoreCase))
                    SetCreatorSlot(doc, stage, slot, null);
            }
        }
        if (keepSlot >= 0 && string.Equals(doc.MissileOverride, modelPath, StringComparison.OrdinalIgnoreCase))
            SetCreatorMissile(doc, null);
    }

    /// <summary>Recompiling means the phase model's bytes changed underneath it. EnsureCreatorModel
    /// only ever reads a path once, so refresh the doc in place or the outliner, the bone list and
    /// every handle keep describing the PREVIOUS compile.</summary>
    private void RefreshSketchModelDoc(CreatorSpellDoc doc, string modelPath, byte[] bytes)
    {
        if (!doc.Models.TryGetValue(modelPath, out CreatorModelDoc? model))
        {
            EnsureCreatorModel(doc, modelPath);
            return;
        }
        model.Original = bytes;
        model.Working = (byte[])bytes.Clone();
        model.Emitters = M2EmitterParser.ReadEmitters(model.Working);
        model.OriginalEmitterCount = model.Emitters.Count;
        model.Textures = Creator.M2TextureParser.ParseTextures(model.Original);
        model.Meshes = M2MeshParser.ReadMeshes(model.Working);
        model.OriginalLayerCount = model.Meshes.Count;
        model.Ribbons = M2RibbonParser.ReadRibbons(model.Working);
        model.OriginalRibbonCount = model.Ribbons.Count;
        model.Bones = M2BoneParser.ReadBones(model.Working);
        // The cards are the source of truth for a sketch's bones; anything left here would be
        // re-applied on top of the freshly generated ones and drift away from what the cards say.
        model.BoneEdits.Clear();
        // Any OTHER dial the author turned on the compiled model (an emitter added from the
        // inspector, a hue, a texture swap) must survive the recompile: re-apply them on the
        // new bytes. Before this a recompile silently dropped them until the next dial moved.
        if (CreatorModelHasEdits(model)) RebuildCreatorModel(model, replay: false);
    }

    private void ClearSketchModel(CreatorSpellDoc doc, SketchDoc sketch, string modelPath, string renderPath)
    {
        _spellEffects?.SetModelOverride(modelPath, null);
        _spellParticles?.SetGeometryModelOverride(renderPath, null);
        _spellEffectMeshes?.InvalidateModel(renderPath);
        if (string.Equals(doc.MissileOverride, modelPath, StringComparison.OrdinalIgnoreCase))
            SetCreatorMissile(doc, null);
        if (doc.Composition.TryGetValue(_sketchStage, out CreatorStageComposition? composition) &&
            string.Equals(composition.Slots[_sketchSlot], modelPath, StringComparison.OrdinalIgnoreCase))
            SetCreatorSlot(doc, _sketchStage, _sketchSlot, null);
    }

    /// <summary>Selecting a piece selects its BONE, so the translate arrows and rotate rings
    /// that already exist appear on it (§6: no new gizmo code). Bone 0 is the static root, so
    /// piece i is bone i+1.</summary>
    private void SelectSketchPiece(CreatorSpellDoc doc, SketchDoc sketch, int index, bool takeInspector = true)
    {
        _sketchSelected = index;
        if (index < 0 || index >= sketch.Pieces.Count) return;
        string modelPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
        if (!doc.Models.ContainsKey(modelPath)) return;
        _creatorBonePick[modelPath] = index + 1;
        if (takeInspector) SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Phase, modelPath));

        // Put the handles ON the piece. The translate arrows and rotate rings already exist and
        // already write back into the PLACE card (AdoptSketchBoneEdits) - but they only draw
        // when BOTH the gizmo layer and the skeleton layer are switched on, and nothing about
        // selecting a piece used to switch them on. So the direct manipulation was there the
        // whole time and invisible, which is the same as not being there.
        bool changed = false;
        if (!Settings.Creator.SpellGizmos) { Settings.Creator.SpellGizmos = true; changed = true; }
        if (!Settings.Creator.GizmoBones) { Settings.Creator.GizmoBones = true; changed = true; }
        if (changed) SettingsFile?.Save();
    }

    /// <summary>Is this phase model one the Sketch generates? The raw dials still work on it,
    /// but the next recompile rebuilds it from the cards, so anything typed there is temporary.
    /// The model inspector says so rather than letting the owner find out the hard way.</summary>
    private bool IsSketchModel(CreatorSpellDoc doc, string path)
    {
        foreach ((SpellStage stage, SketchDoc sketch) in doc.Sketches)
        {
            _ = stage;
            if (sketch.Pieces.Count == 0) continue;
            if (string.Equals(path, SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage),
                    StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Which Sketch produced this phase model, if any.</summary>
    private bool TryGetSketchForModel(CreatorSpellDoc doc, string path, out SketchDoc found)
    {
        foreach (SketchDoc sketch in doc.Sketches.Values)
        {
            if (sketch.Pieces.Count == 0) continue;
            if (string.Equals(path, SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage),
                    StringComparison.OrdinalIgnoreCase)) { found = sketch; return true; }
        }
        found = null!;
        return false;
    }

    /// <summary>A sketch model's bones are not anonymous: bone 0 is the root everything hangs
    /// from, and bone i+1 IS piece i. Saying so turns "b1" into "crescent 1".</summary>
    private static string SketchBoneName(SketchDoc sketch, int boneIndex)
    {
        if (boneIndex == 0) return "b0  (root - the whole effect)";
        int piece = boneIndex - 1;
        return piece < sketch.Pieces.Count
            ? $"b{boneIndex}  {sketch.Pieces[piece].Name}"
            : $"b{boneIndex}";
    }

    // ── The window ──────────────────────────────────────────────────────────

    private void DrawSketchWindow()
    {
        if (!_sketchOpen || _creatorSpell is not { } doc) return;
        float cs = CreatorUiScale;
        var io = ImGui.GetIO();

        // Wide enough for the anatomy's two columns and a shape shelf of eight buttons at the
        // owner's UI scale. 420 was measured at scale 1 and clipped everything past "Arrow".
        float width = Math.Clamp(560f * cs, 420f, io.DisplaySize.X * 0.62f);
        // Anatomy + shelf + piece list + four cards is a tall window, and an auto-sized one
        // simply grows off the bottom of the screen taking its own header with it - which is
        // how the owner ended up looking at a LOOK card with no way back to the shape shelf.
        // An EXPLICIT height, not an auto-sized one. Auto-size fixes the window to whatever the
        // content happened to be on the frame it was created, so the moment the anatomy list
        // grew - the moment a plan was picked - the shape shelf, the piece list and all four
        // cards were simply clipped off the bottom with no scrollbar to reach them.
        float maxHeight = MathF.Max(io.DisplaySize.Y - _spellIdeStripHeight - 24f, 260f);
        ImGui.SetNextWindowSize(new Vector2(width, MathF.Min(maxHeight, 820f * cs)), ImGuiCond.Always);
        var pos = new Vector2(io.DisplaySize.X - width - 8f - SpellIdeRightInset, _spellIdeStripHeight + 8f);
        // Docked BESIDE the inspector, every frame: on the first frame the inspector has not yet
        // said where it sits (SpellIdeRightInset is 0 until it draws once), and a FirstUseEver
        // position froze the window ON TOP of it - both unreadable (frame of 2026-10-26).
        if (!BeginSpellIdeWindow("##spell-sketch", pos, Vector2.Zero, ImGuiCond.Always,
                ImGuiWindowFlags.None, 0.10f))
        {
            EndSpellIdeWindow();
            return;
        }

        SketchDoc sketch = SketchForStage(doc, _sketchStage);

        // ── header: which stage this sketch belongs to ──────────────────────
        ImGui.TextColored(SpellIdeGold, $"SKETCH: {sketch.Stage}");
        ImGui.SameLine();
        if (ImGui.SmallButton("x")) _sketchOpen = false;
        CreatorHelp("Free-form geometry: draw or pick a shape, place it, tell it how to travel, " +
                    "and hang trails or sparks off it. It compiles to a real vanilla M2, so every " +
                    "other dial in this workshop applies to the result.");

        DrawSketchAnatomy(doc, cs);
        ImGui.TextColored(SpellIdeGold, _sketchStage.ToString().ToUpperInvariant());
        CreatorHelp("The part you are drawing now. Click another part in the list above to " +
                    "switch to it; each one compiles to its own model.");

        // Never let the loop be silently skipping the phase being edited.
        ImGui.SameLine();
        if (!_creatorLoopOn)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.6f, 0.4f, 1f), "the loop is STOPPED");
            ImGui.SameLine();
            if (ImGui.SmallButton("run it")) ToggleCreatorLoop();
            CreatorHelp("Nothing plays while the loop is stopped, however much you draw. " +
                        "Adding a piece starts it for you; this is here for when you stop it " +
                        "deliberately and then wonder where everything went.");
        }
        else if (SketchPhaseIsPlaying(_sketchStage))
        {
            bool hold = _sketchHold;
            if (ImGui.Checkbox("Hold", ref hold))
            {
                _sketchHold = hold;
                SketchChanged();               // the sequence itself changes: one-shot vs looping
                ReapPresentedEffect();
                _creatorLoopNextAt = 0;
            }
            CreatorHelp("Keep this phase on screen while you work on it, instead of letting it " +
                        "flash past every couple of seconds.\n\n" +
                        "A cast normally lives for its own length and stops, which is correct in " +
                        "the game and useless in an editor - you cannot grab a handle on " +
                        "something that is only there a quarter of the time.\n\n" +
                        "While this is on, the phase you are editing is the only one that plays " +
                        "and it does not stop. The other phases keep their ticks in the list " +
                        "above and come back the moment you switch this off.");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.95f, 0.6f, 0.4f, 1f), "NOT being played");
            ImGui.SameLine();
            if (ImGui.SmallButton("play it")) SetSketchPhasePlaying(_sketchStage, true);
            CreatorHelp("The loop only plays the phases ticked in the Loop row, and by default " +
                        "that is Cast and Impact. Nothing drawn into an unplayed phase will " +
                        "appear, however correct it is.");
        }

        if (!sketch.IsMissile)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * cs);
            if (ImGui.Combo("Slot", ref _sketchSlot, CreatorSlotNames, CreatorSlotNames.Length))
                SketchChanged();
            CreatorHelp("Where on the caster the whole sketch hangs - and, because of that, WHAT " +
                        "\"forward\" means for every piece in it. See the line below.");

            // The single most confusing thing about an attached effect, said out loud - with the
            // MEASURED number, because the paragraph alone did not land.
            bool steady = _sketchSlot is 0 or 2;
            ImGui.TextColored(
                steady ? new Vector4(0.55f, 0.85f, 0.55f, 1f) : new Vector4(0.95f, 0.6f, 0.4f, 1f),
                $"\"Forward\" means forward for the {CreatorSlotNames[_sketchSlot].ToLowerInvariant()}: " +
                SketchSlotSteadiness[_sketchSlot]);
            CreatorHelp(
                "An attached effect rides one of the caster's bones and inherits that bone's TURN " +
                "as well as its position. So \"3 yards forward\" is three yards along whatever way " +
                "that bone is pointing at that moment - and if the bone sweeps during the " +
                "animation, a piece travelling \"forward\" curves along the body.\n\n" +
                "Measured on four races by walking the real attachment maths: Base and Head are " +
                "0 degrees off the caster's facing in every animation. Chest is 48 degrees off " +
                "during a cast (83 on a Night Elf) and still moving. Base is the default for " +
                "exactly that reason, and because your Origin already carries the height, a piece " +
                "there still sits at the chest - it just stops swinging.\n\n" +
                "Special 1, 2 and 3 do not exist on player models at all: choosing one silently " +
                "lands on a spine bone that swings MORE than Chest.\n\n" +
                "If you want a piece to ignore the caster entirely, set Travel to \"To the target\" " +
                "- that makes the sketch the spell's missile and flies it through the world.");
        }

        ImGui.Separator();

        // ── shape shelf: one click = one new piece ──────────────────────────
        // Draw first: the shelf is seven shapes somebody else chose, and this is the one that
        // lets the owner make their own.
        if (CreatorButton("Draw", 56f * cs)) OpenSketchDrawCanvas();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw your own shape by hand, then place it and move it like any other piece.");

        // Wrap the shelf rather than letting it run off the window: a shape you cannot see is
        // a shape you do not have.
        float shelfRight = ImGui.GetWindowContentRegionMax().X;
        for (int i = 0; i < SketchShapeKinds.Length; i++)
        {
            float next = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X +
                         ImGui.GetStyle().ItemSpacing.X + 62f * cs;
            if (next < shelfRight) ImGui.SameLine();
            if (ImGui.SmallButton(SketchShapeNames[i]))
            {
                if (sketch.Pieces.Count >= SketchWriter.MaxPieces)
                    _sketchStatus = $"Too many pieces (max {SketchWriter.MaxPieces}).";
                else
                {
                    sketch.AddPiece(SketchShapeKinds[i]);
                    SelectSketchPiece(doc, sketch, sketch.Pieces.Count - 1);
                    SketchChanged();
                }
            }
        }
        CreatorHelp("Click a shape to add it at your chest, one yard across, facing the way that " +
                    "shape usually faces: a slash stands on edge, a ring lies on the ground, a " +
                    "sparkle looks at you.\n\n" +
                    "Sphere, Cylinder, Cone, Torus and Beam are SOLIDS: real 3D meshes with an " +
                    "image wrapped round them (LOOK > Image). A ball of lightning is a sphere " +
                    "wearing a lightning image that scrolls; a bolt is a beam.");

        // ── the piece list: state in words, so a glance tells what each does ─
        ImGui.Spacing();
        if (sketch.Pieces.Count == 0)
            ImGui.TextDisabled("No pieces yet - click a shape above.");
        for (int i = 0; i < sketch.Pieces.Count; i++)
        {
            SketchPiece piece = sketch.Pieces[i];
            ImGui.PushID(i);
            bool selected = i == _sketchSelected;
            if (ImGui.Selectable($"{piece.Name}##row", selected, ImGuiSelectableFlags.None,
                    new Vector2(120f * cs, 0f)))
                SelectSketchPiece(doc, sketch, i);
            ImGui.SameLine(130f * cs);
            ImGui.TextDisabled(piece.StateLine());
            ImGui.PopID();
        }

        // ── copy / paste / delete ───────────────────────────────────────────
        ImGui.Spacing();
        SketchPiece? current = SelectedSketchPiece(sketch);
        if (current is null) ImGui.BeginDisabled();
        if (ImGui.SmallButton("copy")) { _sketchClipboard = current?.Clone(); _sketchDeleteArmed = false; }
        if (current is null) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy the selected piece with every card (Ctrl+C).");

        ImGui.SameLine();
        if (_sketchClipboard is null) ImGui.BeginDisabled();
        if (ImGui.SmallButton("paste")) PasteSketchPiece(doc, sketch);
        if (_sketchClipboard is null) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paste it half a yard to the left, so the two are never on top of " +
                             "each other (Ctrl+V).");

        ImGui.SameLine();
        if (current is null) ImGui.BeginDisabled();
        if (ImGui.SmallButton(_sketchDeleteArmed ? "sure?" : "delete"))
        {
            if (_sketchDeleteArmed) { DeleteSketchPiece(doc, sketch); _sketchDeleteArmed = false; }
            else _sketchDeleteArmed = true;
        }
        if (current is null) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove the selected piece (click twice).");

        ImGui.SameLine();
        ImGui.TextDisabled($"{sketch.Length:0.00}s");
        CreatorHelp("How long the whole sketch runs, taken from the longest motion (minimum half " +
                    "a second). This is also how long the effect LIVES: the game reads the " +
                    "sequence length to decide when to stop drawing it.");

        if (_sketchStatus.Length > 0)
        {
            ImGui.TextDisabled(_sketchStatus);
        }

        // ── the four cards ──────────────────────────────────────────────────
        // In their own scrolling region so the parts above - which are how you get to another
        // phase or another shape - can never be scrolled away.
        if (current is not null)
        {
            ImGui.Separator();
            if (ImGui.BeginChild("##sketch-cards", new Vector2(0f, 0f), false))
            {
                DrawSketchLookCard(current, cs);
                ImGui.Separator();
                DrawSketchPlaceCard(current, cs);
                ImGui.Separator();
                DrawSketchMoveCard(current, sketch, cs);
                ImGui.Separator();
                DrawSketchExtrasCard(current, cs);
            }
            ImGui.EndChild();
        }

        EndSpellIdeWindow();

        // The paper sits above everything, because while it is open it owns the mouse.
        DrawSketchDrawCanvas(doc, sketch);
    }

    private void PasteSketchPiece(CreatorSpellDoc doc, SketchDoc sketch)
    {
        if (_sketchClipboard is null) return;
        if (sketch.Pieces.Count >= SketchWriter.MaxPieces)
        {
            _sketchStatus = $"Too many pieces (max {SketchWriter.MaxPieces}).";
            return;
        }
        sketch.PasteCopy(_sketchClipboard);
        SelectSketchPiece(doc, sketch, sketch.Pieces.Count - 1);
        SketchChanged();
    }

    private void DeleteSketchPiece(CreatorSpellDoc doc, SketchDoc sketch)
    {
        if (_sketchSelected < 0 || _sketchSelected >= sketch.Pieces.Count) return;
        // Its children drop to the caster - and must forget the name, or the next piece to be
        // given it would quietly adopt them.
        string gone = sketch.Pieces[_sketchSelected].Name;
        foreach (SketchPiece other in sketch.Pieces)
            if (other.Place.Parent == gone) other.Place.Parent = null;
        sketch.Pieces.RemoveAt(_sketchSelected);
        SelectSketchPiece(doc, sketch, Math.Min(_sketchSelected, sketch.Pieces.Count - 1));
        SketchChanged();
    }

    // ── LOOK ────────────────────────────────────────────────────────────────

    private void DrawSketchLookCard(SketchPiece piece, float cs)
    {
        ImGui.TextColored(SpellIdeGold, "LOOK");
        CreatorHelp("Colour, brightness and how the piece blends with the world. The image itself " +
                    "is white - the colour lives in the model's colour record, so changing it is " +
                    "instant and never redraws the shape.");

        Vector3 colour = piece.Look.Colour;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.ColorEdit3("Colour", ref colour, ImGuiColorEditFlags.NoInputs))
        {
            piece.Look.Colour = colour;
            SketchChanged();
        }

        ImGui.SameLine();
        float alpha = piece.Look.Alpha;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.SliderFloat("Alpha", ref alpha, 0.05f, 1f, "%.2f"))
        {
            piece.Look.Alpha = alpha;
            SketchChanged();
        }

        bool solid = SketchShapes.IsSolid(piece.Shape.Kind);
        float glow = piece.Look.Glow;
        ImGui.BeginDisabled(solid || piece.Look.Image is not null);
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.SliderFloat("Glow", ref glow, 0f, 1f, "%.2f"))
        {
            piece.Look.Glow = glow;
            SketchChanged();
        }
        ImGui.EndDisabled();
        CreatorHelp(solid || piece.Look.Image is not null
            ? "A halo baked into a DRAWN shape's image. This piece wears a whole image instead, " +
              "so its halo is EXTRAS > Glow yd: a second, larger shell of the same mesh."
            : "A soft halo baked around the shape itself. This one DOES redraw the image, " +
              "so it is the only look dial that is not instant.");

        ImGui.SameLine();
        int blend = (int)piece.Look.Blend;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.Combo("Blend", ref blend, SketchBlendNames, SketchBlendNames.Length))
        {
            piece.Look.Blend = (SketchBlend)blend;
            SketchChanged();
        }
        CreatorHelp("Additive adds light to whatever is behind it and is what nearly every spell " +
                    "effect uses. Alpha blends normally. Modulate darkens.");

        DrawSketchImageRow(piece, cs);
    }

    // The image a piece wears and how it moves across it (P3.6). A drawn shape wears its own
    // generated mask; a solid wears plain white until it is given an image - which is what turns
    // a plastic ball into a ball of lightning.
    private void DrawSketchImageRow(SketchPiece piece, float cs)
    {
        SketchLook look = piece.Look;
        string current = look.Image is null
            ? (SketchShapes.IsSolid(piece.Shape.Kind) ? "plain white" : "the drawn shape")
            : Path.GetFileNameWithoutExtension(look.Image);
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Image: {current}");
        if (look.Image is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(look.Image);
        ImGui.SameLine();
        if (CreatorButton(_sketchImagePickerOpen ? "Close##sketch-img" : "Choose##sketch-img", 64f * cs))
            _sketchImagePickerOpen = !_sketchImagePickerOpen;
        if (look.Image is not null)
        {
            ImGui.SameLine();
            if (CreatorButton("Clear##sketch-img", 56f * cs))
            {
                look.Image = null;
                SketchChanged();
            }
        }
        CreatorHelp("Wear any image from the game's archives instead of the drawn shape - a " +
                    "lightning sheet, a fire swirl, an energy ring. The colour above still tints " +
                    "it (white keeps it as painted). It ships as a path, not a copy: the player's " +
                    "own game already has it.");
        if (_sketchImagePickerOpen && DrawSketchImagePicker(look.Image, out string? picked))
        {
            look.Image = picked;
            // An image painted for a sheet repeats: scrolling it is the usual next step, so the
            // picker leaves the dials below in view rather than closing on the user.
            SketchChanged();
        }

        Vector2 scroll = look.UvScroll;
        ImGui.BeginDisabled(look.HasFlipbook);
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.DragFloat2("Scroll /s", ref scroll, 0.01f, -10f, 10f, "%.2f"))
        {
            look.UvScroll = scroll;
            SketchChanged();
        }
        ImGui.EndDisabled();
        CreatorHelp("Slide the image across the piece, in image widths (u) and heights (v) per " +
                    "second. On a sphere u runs AROUND and v from pole to pole, so (0.5, 0) spins " +
                    "the pattern round the ball and (0, 1) pours it over the top. The loop is " +
                    "always seamless: the model's clock is chosen so both axes land on whole " +
                    "repeats.");

        int cols = look.FlipCols, rows = look.FlipRows;
        float fps = look.FlipFps;
        ImGui.SetNextItemWidth(70f * cs);
        bool flipChanged = ImGui.SliderInt("Cols", ref cols, 1, 8);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f * cs);
        flipChanged |= ImGui.SliderInt("Rows", ref rows, 1, 8);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * cs);
        flipChanged |= ImGui.SliderFloat("Frames /s", ref fps, 0f, 60f, "%.0f");
        if (flipChanged)
        {
            look.FlipCols = cols;
            look.FlipRows = rows;
            look.FlipFps = fps;
            SketchChanged();
        }
        CreatorHelp("Flipbook: the image is a grid of frames (many of Blizzard's flame and " +
                    "lightning sheets are 4 x 4) played one after another at this rate. Each " +
                    "frame is HELD, not blended - the way the game plays them. A flipbook wins " +
                    "over scrolling. 1 x 1 or 0 frames/s is off.");
    }

    /// <summary>Thumbnail grid of every effect image in the archives (Spells\, Particles\,
    /// Textures\). True when the choice changed.</summary>
    private bool DrawSketchImagePicker(string? selected, out string? picked)
    {
        picked = selected;
        if (_sketchImageFiles is null && _mpq is not null)
            _sketchImageFiles = _mpq.ListedFiles()
                .Where(f => f.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) &&
                            (f.StartsWith("Spells\\", StringComparison.OrdinalIgnoreCase) ||
                             f.StartsWith("Particles\\", StringComparison.OrdinalIgnoreCase) ||
                             f.StartsWith("Textures\\", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (_sketchImageFiles is null) { ImGui.TextDisabled("Game images are not loaded."); return false; }

        float cs = CreatorUiScale;
        ImGui.SetNextItemWidth(200f * cs);
        ImGui.InputText("Search##sketch-img", _sketchImageSearchBuf, (uint)_sketchImageSearchBuf.Length);
        bool firstChip = true;
        foreach (string chip in SketchImageChips)
        {
            float width = ImGui.CalcTextSize(chip).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if (!firstChip && ImGui.GetItemRectMax().X + width < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X)
                ImGui.SameLine();
            if (ImGui.SmallButton(chip + "##sketch-img-chip"))
            {
                Array.Clear(_sketchImageSearchBuf);
                System.Text.Encoding.UTF8.GetBytes(chip, _sketchImageSearchBuf);
            }
            firstChip = false;
        }

        string[] words = BufToString(_sketchImageSearchBuf).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> hits = _sketchImageFiles
            .Where(f => words.All(w => f.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(96).ToList();
        bool changed = false;
        float thumb = 48f * cs;
        if (ImGui.BeginChild("##sketch-img-grid", new Vector2(-1f, thumb * 3.3f + 8f), true))
        {
            float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            for (int i = 0; i < hits.Count; i++)
            {
                string path = hits[i];
                if (i > 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + thumb < right) ImGui.SameLine();
                uint handle = _gameplayArt?.Handle(path) ?? 0;
                // Effect images are mostly white-on-black additive art: a dark backing shows them
                // the way the game will.
                Vector2 at = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddRectFilled(at, at + new Vector2(thumb, thumb), 0xFF101010u);
                if (handle != 0) ImGui.Image((nint)handle, new Vector2(thumb, thumb));
                else ImGui.Dummy(new Vector2(thumb, thumb));
                if (string.Equals(path, selected, StringComparison.OrdinalIgnoreCase))
                    ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - Vector2.One * 2, ImGui.GetItemRectMax() + Vector2.One * 2,
                        0xFF33DDEEu, 0f, ImDrawFlags.None, 3f);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
                if (ImGui.IsItemClicked()) { picked = path; changed = true; }
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled(hits.Count == 96 ? "First 96 shown; narrow the search." : $"{hits.Count} images");
        return changed;
    }

    // ── PLACE ───────────────────────────────────────────────────────────────

    private void DrawSketchPlaceCard(SketchPiece piece, float cs)
    {
        ImGui.TextColored(SpellIdeGold, "PLACE");
        if (_sketchPlaceMode) DrawSketchPlaceWalls(piece, cs);
        CreatorHelp("Where the piece sits and which way it points, relative to the caster. You can " +
                    "type the numbers or drag the arrows on it in the world.");

        int facing = (int)piece.Place.Facing;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.Combo("Facing", ref facing, SketchFacingNames, SketchFacingNames.Length))
        {
            piece.Place.Facing = (SketchFacing)facing;
            SketchChanged();
        }
        CreatorHelp("Vertical forward: standing on edge, sweeping the way you face - a sword slash. " +
                    "Vertical side: turned to face you. Flat: lying on the ground. Face camera: " +
                    "always turned to whoever is looking.");

        ImGui.SameLine();
        float scale = piece.Place.Scale;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.SliderFloat("Scale", ref scale, 0.1f, 5f, "%.2f"))
        {
            piece.Place.Scale = scale;
            SketchChanged();
        }

        // ── rides on (P3.8): hang this piece off another one ──
        if (_creatorSpell is { } rideDoc)
        {
            SketchDoc rideSketch = SketchForStage(rideDoc, _sketchStage);
            var names = new List<string> { "the caster" };
            names.AddRange(rideSketch.Pieces.Where(p => !ReferenceEquals(p, piece)).Select(p => p.Name));
            int current = piece.Place.Parent is { } parentName ? Math.Max(names.IndexOf(parentName), 0) : 0;
            ImGui.SetNextItemWidth(150f * cs);
            if (ImGui.Combo("Rides on", ref current, names.ToArray(), names.Count))
            {
                piece.Place.Parent = current == 0 ? null : names[current];
                SketchChanged();
            }
            CreatorHelp("Hang this piece off another one: wherever that piece travels, spins, swings " +
                        "or grows, this one goes with it - from where it is now. A spark ring round a " +
                        "flying ball, a second blade on a swinging arm. A choice that would make a " +
                        "loop (A on B on A) falls back to the caster.");
        }

        // ── turn and mirror: the two things a flat shape needs and a facing preset cannot give.
        if (ImGui.SmallButton("turn -90"))
        {
            piece.Place.QuarterTurns = (piece.Place.QuarterTurns + 3) % 4;
            SketchChanged();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("turn +90"))
        {
            piece.Place.QuarterTurns = (piece.Place.QuarterTurns + 1) % 4;
            SketchChanged();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{piece.Place.QuarterTurns * 90}°");
        CreatorHelp("Turn the piece a quarter at a time IN ITS OWN PLANE, keeping whatever facing " +
                    "it has. For finer control, drag the rotate rings on the piece in the world - " +
                    "they write straight back into this card.");

        ImGui.SameLine();
        bool mirrorX = piece.Place.MirrorX;
        if (ImGui.Checkbox("Mirror", ref mirrorX)) { piece.Place.MirrorX = mirrorX; SketchChanged(); }
        ImGui.SameLine();
        bool mirrorY = piece.Place.MirrorY;
        if (ImGui.Checkbox("Flip", ref mirrorY)) { piece.Place.MirrorY = mirrorY; SketchChanged(); }
        CreatorHelp("Mirror swaps left for right, Flip swaps top for bottom. These are NOT turns: " +
                    "a mirrored crescent is a \"(\" become a \")\", which no amount of turning " +
                    "will give you.");

        if (ImGui.SmallButton("reset orientation"))
        {
            piece.Place.Facing = SketchPiece.DefaultFacing(piece.Shape.Kind);
            piece.Place.CustomEuler = Vector3.Zero;
            piece.Place.QuarterTurns = 0;
            piece.Place.MirrorX = false;
            piece.Place.MirrorY = false;
            SketchChanged();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to the facing this shape starts with, no turn and no mirror.");

        Vector3 origin = piece.Place.Origin;
        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.DragFloat3("Origin", ref origin, 0.02f, -20f, 20f, "%.2f"))
        {
            piece.Place.Origin = origin;
            SketchChanged();
        }
        CreatorHelp("Yards from the caster: forward, left, up. Up 1.2 is about chest height. " +
                    "This is also the point the piece TURNS ABOUT.\n\n" +
                    "Dragging the piece's arrows in the world writes these same three numbers.");

        bool placing = _sketchPlaceMode;
        if (ImGui.Checkbox("Point to place", ref placing))
        {
            _sketchPlaceMode = placing;
            if (placing) ResetSketchPlaceBox();
        }
        CreatorHelp("Point at the world and click to say \"origin HERE\". The grid cell under " +
                    "your mouse lights up as a cube, and clicking it moves the piece there.\n\n" +
                    "Use this instead of dragging when the effect is hard to catch: it does not " +
                    "need the piece to be on screen at all, so there is nothing to time and " +
                    "nothing to pause. The cells are the lattice's own, so everything lands on " +
                    "the grid.");
        if (_sketchPlaceMode)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_sketchPlaceCell is { } cell
                ? $"{cell.X:0.00} fwd  {cell.Y:0.00} left  {cell.Z:0.00} up"
                : "(point at the grid)");
        }

        Vector3 offset = piece.Place.Offset;
        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.DragFloat3("Held out", ref offset, 0.02f, -20f, 20f, "%.2f"))
        {
            piece.Place.Offset = offset;
            SketchChanged();
        }
        CreatorHelp("How far the DRAWING is held out from that turning point, in yards. Leave it " +
                    "at zero and the piece turns on the spot; give it some and the piece SWEEPS " +
                    "around, like a blade on the end of an arm.\n\n" +
                    "This is how the game's own melee effects are built. Cleave hangs three " +
                    "painted planes about 0.85 yards out from a point on the caster's body axis " +
                    "and swings the lot of them through 215 degrees.");

        Vector2 size = new(piece.Shape.Width, piece.Shape.Height);
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.DragFloat2("Size (yd)", ref size, 0.02f, 0.05f, 20f, "%.2f"))
        {
            piece.Shape.Width = MathF.Max(size.X, 0.05f);
            piece.Shape.Height = MathF.Max(size.Y, 0.05f);
            SketchChanged();
        }
        CreatorHelp(piece.Shape.Kind switch
        {
            SketchShapeKind.Sphere => "Width = the ball's diameter round its axis, height = along it (a stretched ball when they differ). A human is about two yards tall.",
            SketchShapeKind.Cylinder or SketchShapeKind.Cone => "Width = the diameter, height = the length along its own axis.",
            SketchShapeKind.Torus => "Width = the ring's diameter. Tube (below) is how fat the ring is.",
            SketchShapeKind.Beam => "Width = the length along the piece's forward axis, height = how thick the bolt is.",
            _ => "How big the drawn shape is, in yards. A human is about two yards tall.",
        });

        if (SketchShapes.IsSolid(piece.Shape.Kind))
        {
            if (piece.Shape.Kind != SketchShapeKind.Beam)
            {
                int segments = piece.Shape.Segments;
                ImGui.SetNextItemWidth(110f * cs);
                if (ImGui.SliderInt("Smoothness", ref segments, SketchSolids.MinSegments, SketchSolids.MaxSegments))
                {
                    piece.Shape.Segments = segments;
                    SketchChanged();
                }
                CreatorHelp("Segments around the solid. 12-16 reads as round at spell sizes; more " +
                            "costs vertices (the model has a 65,535 limit across all pieces).");
            }
            if (piece.Shape.Kind == SketchShapeKind.Torus)
            {
                ImGui.SameLine();
                float tube = piece.Shape.Thickness;
                ImGui.SetNextItemWidth(110f * cs);
                if (ImGui.SliderFloat("Tube", ref tube, 0.05f, 0.9f, "%.2f"))
                {
                    piece.Shape.Thickness = tube;
                    SketchChanged();
                }
                CreatorHelp("How fat the ring's tube is, as a fraction of the ring's radius.");
            }
        }
        else if (piece.Shape.Kind != SketchShapeKind.Stroke)
        {
            float thickness = piece.Shape.Thickness;
            ImGui.SetNextItemWidth(110f * cs);
            if (ImGui.SliderFloat("Thickness", ref thickness, 0.01f, 1f, "%.2f"))
            {
                piece.Shape.Thickness = thickness;
                SketchChanged();
            }
            CreatorHelp("How fat the shape is: for a crescent, how much of the moon is left; for a " +
                        "ring, how wide the band.");

            ImGui.SameLine();
            float softness = piece.Shape.Softness;
            ImGui.SetNextItemWidth(110f * cs);
            if (ImGui.SliderFloat("Softness", ref softness, 0f, 1f, "%.2f"))
            {
                piece.Shape.Softness = softness;
                SketchChanged();
            }
            CreatorHelp("How blurred the outline is. Zero is a crisp edge.");
        }

        if (piece.Place.Facing == SketchFacing.Custom)
        {
            Vector3 euler = piece.Place.CustomEuler;
            ImGui.SetNextItemWidth(220f * cs);
            if (ImGui.DragFloat3("Angles (deg)", ref euler, 1f, -360f, 360f, "%.0f"))
            {
                piece.Place.CustomEuler = euler;
                SketchChanged();
            }
            CreatorHelp("The exact orientation, in degrees: roll about forward, pitch about left, " +
                        "yaw about up. Dragging a rotate ring in the world writes these three " +
                        "numbers, so this is the read-out as well as the control.");
        }
        else
        {
            ImGui.TextDisabled($"Angles: {SketchFacingNames[(int)piece.Place.Facing].ToLowerInvariant()}" +
                               (piece.Place.QuarterTurns != 0 ? $" + {piece.Place.QuarterTurns * 90}°" : "") +
                               (piece.Place.MirrorX ? " mirrored" : "") +
                               (piece.Place.MirrorY ? " flipped" : ""));
            CreatorHelp("Drag a rotate ring on the piece in the world and this becomes exact " +
                        "degrees you can type into.");
        }
    }

    // ── MOVE ────────────────────────────────────────────────────────────────

    private void DrawSketchMoveCard(SketchPiece piece, SketchDoc sketch, float cs)
    {
        ImGui.TextColored(SpellIdeGold, "MOVE");
        CreatorHelp("When the piece shows up, and how it travels, turns, grows and fades from " +
                    "that moment on.");

        float startAt = piece.Motion.StartAt;
        ImGui.SetNextItemWidth(90f * cs);
        if (ImGui.DragFloat("Starts at s", ref startAt, 0.01f, 0f, 10f, "%.2f"))
        {
            piece.Motion.StartAt = MathF.Max(startAt, 0f);
            SketchChanged();
        }
        CreatorHelp("Seconds from the start of this PHASE before the piece appears. It is " +
                    "invisible and still until then, and everything else on this card is " +
                    "measured from that moment - so Starts at 0.20 with a 0.60 s travel finishes " +
                    "at 0.80 s. This is how you make a second slash follow the first.");
        ImGui.SameLine();
        ImGui.TextDisabled($"of {sketch.Length:0.00}s");

        int mode = (int)piece.Motion.Mode;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.Combo("Travel", ref mode, SketchTravelNames, SketchTravelNames.Length))
        {
            piece.Motion.Mode = (SketchTravelMode)mode;
            SketchChanged();
        }
        CreatorHelp("Fixed distance: the piece flies from where you put it and dies that many " +
                    "yards out, whatever the target is, and the effect stays on the caster. " +
                    "To the target: the whole sketch becomes the spell's missile and the game " +
                    "flies it to whatever you cast at, over the spell's own range.");

        if (piece.Motion.Mode == SketchTravelMode.FixedDistance)
        {
            ImGui.SameLine();
            float distance = piece.Motion.Distance;
            ImGui.SetNextItemWidth(90f * cs);
            if (ImGui.DragFloat("yd", ref distance, 0.05f, 0f, 60f, "%.2f"))
            {
                piece.Motion.Distance = MathF.Max(distance, 0f);
                SketchChanged();
            }

            int direction = (int)piece.Motion.Direction;
            ImGui.SetNextItemWidth(110f * cs);
            if (ImGui.Combo("Direction", ref direction, SketchDirectionNames, SketchDirectionNames.Length))
            {
                piece.Motion.Direction = (SketchDirection)direction;
                SketchChanged();
            }

            if (piece.Motion.Direction == SketchDirection.Custom)
            {
                Vector3 custom = piece.Motion.CustomDirection;
                ImGui.SetNextItemWidth(220f * cs);
                if (ImGui.DragFloat3("Vector", ref custom, 0.02f, -1f, 1f, "%.2f"))
                {
                    piece.Motion.CustomDirection = custom;
                    SketchChanged();
                }
                CreatorHelp("Forward, left, up. It is normalised, so only the direction matters.");
            }
        }
        else
        {
            // The flight speed is SPELL data (spell_template.speed / Spell.dbc 37), the one number
            // both the server's arrival timing and every client's missile read. This field used to
            // edit a sketch-only copy nothing read (shown "20 yd/s" beside a spell flying at 8);
            // it now edits the spell's own speed through the Rules, and mirrors it into the piece
            // only so the piece list says the truth.
            ImGui.SameLine();
            Creator.Spells.SpellRules? speedRules = _creatorSpell is { } speedDoc ? CreatorRules(speedDoc) : null;
            float speed = speedRules?.Speed ?? piece.Motion.Speed;
            piece.Motion.Speed = speed;
            ImGui.SetNextItemWidth(90f * cs);
            if (ImGui.DragFloat("yd/s", ref speed, 0.25f, 0.5f, 200f, "%.1f") && speedRules is not null)
            {
                speedRules.Set("speed", MathF.Round(MathF.Max(speed, 0.5f), 1));
                piece.Motion.Speed = speedRules.Speed;
            }
            CreatorHelp("How fast the missile flies - the SPELL's speed, the same field as Rules > " +
                        "Timing > Missile speed. The range is the spell's own; the missile goes wherever " +
                        "you cast it.");
        }

        float duration = piece.Motion.Duration;
        ImGui.SetNextItemWidth(90f * cs);
        if (ImGui.DragFloat("in s", ref duration, 0.01f, 0.05f, 10f, "%.2f"))
        {
            piece.Motion.Duration = MathF.Max(duration, 0.05f);
            SketchChanged();
        }
        CreatorHelp("How long the travel (and the grow) takes.");

        ImGui.SameLine();
        int ease = (int)piece.Motion.Ease;
        ImGui.SetNextItemWidth(100f * cs);
        if (ImGui.Combo("Ease", ref ease, SketchEaseNames, SketchEaseNames.Length))
        {
            piece.Motion.Ease = (SketchEase)ease;
            SketchChanged();
        }
        CreatorHelp("Linear travels at a steady speed; ease out starts fast and slows down, which " +
                    "reads as a thrown thing losing energy.");

        bool billboarded = piece.Place.Facing == SketchFacing.FaceCamera;
        if (billboarded) ImGui.BeginDisabled();

        float swing = piece.Motion.SwingDegrees;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.DragFloat("Swing deg", ref swing, 5f, -1080f, 1080f, "%.0f"))
        {
            piece.Motion.SwingDegrees = swing;
            SketchChanged();
        }
        CreatorHelp("Turn ONCE by this many degrees over the time above, then hold - which is what " +
                    "a slash actually does. Combined with \"Held out\" on the PLACE card it " +
                    "becomes a sweep.\n\nCleave is 215 degrees over 0.20 s, starting 0.23 s in.");

        float spin = piece.Motion.SpinDegreesPerSecond;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.DragFloat("Spin deg/s", ref spin, 5f, -2000f, 2000f, "%.0f"))
        {
            piece.Motion.SpinDegreesPerSecond = spin;
            SketchChanged();
        }
        if (billboarded) ImGui.EndDisabled();
        CreatorHelp(billboarded
            ? "A camera-facing piece is turned to the camera every frame, which overrides any turn " +
              "of its own. Pick another facing to swing or spin it."
            : "Keep turning, forever, for as long as the piece is alive - a tumble rather than a " +
              "swing. Use Swing for a slash; use this for something that spins the whole time.");

        Vector2 grow = new(piece.Motion.GrowFrom, piece.Motion.GrowTo);
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.DragFloat2("Grow", ref grow, 0.02f, 0.05f, 10f, "%.2f"))
        {
            piece.Motion.GrowFrom = MathF.Max(grow.X, 0.05f);
            piece.Motion.GrowTo = MathF.Max(grow.Y, 0.05f);
            SketchChanged();
        }
        CreatorHelp("Scale at the start and at the end of the travel: 1 to 2 doubles it as it goes.");

        Vector2 fade = new(piece.Motion.FadeIn, piece.Motion.FadeOut);
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.DragFloat2("Fade in/out s", ref fade, 0.01f, 0f, 5f, "%.2f"))
        {
            piece.Motion.FadeIn = MathF.Max(fade.X, 0f);
            piece.Motion.FadeOut = MathF.Max(fade.Y, 0f);
            SketchChanged();
        }
        CreatorHelp("Seconds to fade up at the start and down at the end. A piece that pops out of " +
                    "existence reads as a bug, so a short fade out is the default.");

        if (piece.Motion.Path.Count > 1)
        {
            ImGui.TextDisabled($"Drawn path: {piece.Motion.Path.Count} points (overrides the travel box)");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear path"))
            {
                piece.Motion.Path.Clear();
                SketchChanged();
            }
        }

        DrawSketchModifierRows(piece, cs);
    }

    private static readonly string[] SketchOrbitAxisNames = { "round the forward axis", "round the left axis", "round the up axis" };

    // Orbit and jitter (P3.8): their own looping clock on top of everything above.
    private void DrawSketchModifierRows(SketchPiece piece, float cs)
    {
        SketchMotion m = piece.Motion;
        ImGui.Spacing();
        Vector2 orbit = new(m.OrbitRadius, m.OrbitTurnsPerSecond);
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.DragFloat2("Orbit yd, turns/s", ref orbit, 0.01f, -10f, 10f, "%.2f"))
        {
            m.OrbitRadius = MathF.Max(orbit.X, 0f);
            m.OrbitTurnsPerSecond = orbit.Y;
            SketchChanged();
        }
        if (m.OrbitRadius > 0.001f)
        {
            ImGui.SameLine();
            int axis = m.OrbitAxis switch { SketchDirection.Forward => 0, SketchDirection.Left => 1, _ => 2 };
            ImGui.SetNextItemWidth(170f * cs);
            if (ImGui.Combo("##orbit-axis", ref axis, SketchOrbitAxisNames, SketchOrbitAxisNames.Length))
            {
                m.OrbitAxis = axis switch { 0 => SketchDirection.Forward, 1 => SketchDirection.Left, _ => SketchDirection.Up };
                SketchChanged();
            }
        }
        CreatorHelp("Circle the piece's spot at this radius, this many turns a second (negative turns " +
                    "the other way). Round the up axis circles the caster; round the forward axis is " +
                    "a corkscrew round a missile's flight. It loops on its own clock, forever.");

        Vector3 jitter = new(m.JitterYards, m.JitterDegrees, m.JitterRate);
        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.DragFloat3("Jitter yd, deg, /s", ref jitter, 0.01f, 0f, 60f, "%.2f"))
        {
            m.JitterYards = MathF.Max(jitter.X, 0f);
            m.JitterDegrees = Math.Clamp(jitter.Y, 0f, 180f);
            m.JitterRate = Math.Clamp(jitter.Z, 0.1f, 60f);
            SketchChanged();
        }
        CreatorHelp("Random shake: up to this far off and this many degrees turned, re-rolled this " +
                    "many times a second. Held between rolls it FLICKERS - a lightning arc is a Beam " +
                    "wearing a lightning image with 30-60 degrees of jitter at 10-15/s. Smooth makes " +
                    "it a wobble instead.");
        if (m.JitterYards > 0.0001f || m.JitterDegrees > 0.01f)
        {
            bool smooth = m.JitterSmooth;
            if (ImGui.Checkbox("Smooth##jitter", ref smooth)) { m.JitterSmooth = smooth; SketchChanged(); }
            ImGui.SameLine();
            if (ImGui.SmallButton("re-roll##jitter")) { m.JitterSeed++; SketchChanged(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Another, equally random, pattern.");
        }
    }

    // ── EXTRAS ──────────────────────────────────────────────────────────────

    private void DrawSketchExtrasCard(SketchPiece piece, float cs)
    {
        ImGui.TextColored(SpellIdeGold, "EXTRAS");
        CreatorHelp("Things that hang off the piece and travel with it.");

        float radius = piece.Extras.Glow.Radius;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.DragFloat("Glow yd", ref radius, 0.02f, 0f, 5f, "%.2f"))
        {
            piece.Extras.Glow.Radius = MathF.Max(radius, 0f);
            SketchChanged();
        }
        CreatorHelp("A second, larger copy of the shape drawn additively behind it - a halo that " +
                    "reaches this many yards further out. Zero is off.");

        // Trails and sparks (SPELL_SKETCH.md §4.5, PLAN P3.3/P3.4): a record COPIED out of one
        // of Blizzard's own models (SketchWriter.Extras.cs), riding the piece's bone.
        DrawSketchSparksRow(piece, cs);
        ImGui.Spacing();
        DrawSketchTrailRow(piece, cs);
    }

    private static readonly string[] SketchSparkNames =
        { "None", "Embers", "Motes", "Sparkle", "Smoke", "Flare", "Crackle", "From a model..." };
    private static readonly string[] SketchTrailNames =
        { "None", "Thin white", "Soft wide", "Glow", "Charge", "From a model..." };
    private const int CreatorSparksPickerSlot = -3;
    private const int CreatorTrailPickerSlot = -4;

    // Donor models parsed for the cards (their own rate / speed, their emitter list).
    private readonly Dictionary<string, M2Model?> _sketchDonorModels = new(StringComparer.OrdinalIgnoreCase);

    private M2Model? SketchDonorModel(string path)
    {
        if (_sketchDonorModels.TryGetValue(path, out M2Model? cached)) return cached;
        M2Model? model = null;
        try { if (_mpq.ReadFile(path) is { } bytes) model = M2Reader.Parse(bytes); }
        catch { model = null; }
        return _sketchDonorModels[path] = model;
    }

    private void DrawSketchSparksRow(SketchPiece piece, float cs)
    {
        SketchSparks sparks = piece.Extras.Sparks;
        int preset = (int)sparks.Preset;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.Combo("Sparks", ref preset, SketchSparkNames, SketchSparkNames.Length))
        {
            sparks.Preset = (SketchSparkPreset)preset;
            sparks.Rate = null; sparks.Speed = null;   // a new donor starts as Blizzard made it
            if (sparks.Preset == SketchSparkPreset.Custom && sparks.Model is null)
                OpenCreatorModelPicker(null, CreatorSparksPickerSlot);
            SketchChanged();
        }
        CreatorHelp("Particles pouring off the piece, copied from one of the game's own effects - " +
                    "they travel with the piece. Embers, Motes, Sparkle, Smoke and Flare are the " +
                    "most used emitters in the game; Crackle is Lightning Streak's sparks. 'From a " +
                    "model' takes any emitter of any spell model.");
        if (sparks.Preset == SketchSparkPreset.None) return;

        if (sparks.Preset == SketchSparkPreset.Custom)
        {
            ImGui.TextDisabled(sparks.Model is { } m ? Path.GetFileName(m) : "no model yet");
            ImGui.SameLine();
            if (CreatorButton("Pick##sparks-model", 48f * cs)) OpenCreatorModelPicker(null, CreatorSparksPickerSlot);
            if (sparks.Model is { } model && SketchDonorModel(model) is { } donorModel)
            {
                if (donorModel.ParticleEmitters.Count == 0) ImGui.TextDisabled("That model has no particle emitters.");
                else
                {
                    string[] names = donorModel.ParticleEmitters.Select((e, i) =>
                        $"e{i}: {Path.GetFileNameWithoutExtension(e.Texture < donorModel.Textures.Count ? donorModel.Textures[e.Texture].Filename : "?")}" +
                        $" ({e.EmissionRate:0.#}/s, {e.Lifespan:0.##} s)").ToArray();
                    int index = Math.Clamp(sparks.Emitter, 0, names.Length - 1);
                    ImGui.SetNextItemWidth(240f * cs);
                    if (ImGui.Combo("Emitter##sparks", ref index, names, names.Length))
                    {
                        sparks.Emitter = index;
                        sparks.Rate = null; sparks.Speed = null;
                        SketchChanged();
                    }
                }
            }
        }

        // The donor's own numbers are the starting values: a dial starts where Blizzard left it.
        M2ParticleEmitter? donor = SketchWriter.SparkDonor(sparks) is { } donorRef &&
                                   SketchDonorModel(donorRef.Model) is { } dm && donorRef.Index < dm.ParticleEmitters.Count
            ? dm.ParticleEmitters[donorRef.Index] : null;
        float rate = sparks.Rate ?? donor?.ScalarTracks[6].Keys.DefaultIfEmpty(0f).Max(k => MathF.Abs(k)) ?? 20f;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.DragFloat("Rate /s", ref rate, 0.5f, 0f, 400f, "%.1f"))
        {
            sparks.Rate = MathF.Max(rate, 0f);
            SketchChanged();
        }
        ImGui.SameLine();
        float speed = sparks.Speed ?? MathF.Abs(donor?.EmissionSpeed ?? 0.56f);
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.DragFloat("Speed##sparks", ref speed, 0.02f, 0f, 20f, "%.2f"))
        {
            sparks.Speed = MathF.Max(speed, 0f);
            SketchChanged();
        }
        CreatorHelp("Rate: particles a second at the peak (the donor's own burst or ramp keeps its " +
                    "shape). Speed: how fast they leave, in yards a second; the direction stays the " +
                    "donor's.");
        float size = sparks.Size;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.SliderFloat("Size##sparks", ref size, 0.1f, 5f, "x%.2f"))
        {
            sparks.Size = size;
            SketchChanged();
        }
        ImGui.SameLine();
        bool tint = sparks.Colour is not null;
        if (ImGui.Checkbox("Tint##sparks", ref tint))
        {
            sparks.Colour = tint ? piece.Look.Colour : null;
            SketchChanged();
        }
        if (sparks.Colour is { } colour)
        {
            ImGui.SameLine();
            if (ImGui.ColorEdit3("##sparks-colour", ref colour, ImGuiColorEditFlags.NoInputs))
            {
                sparks.Colour = colour;
                SketchChanged();
            }
        }
        CreatorHelp("Tint recolours the particles but keeps the donor's bright-to-dim ramp and its " +
                    "fade. Off = the donor's own colours.");
    }

    private void DrawSketchTrailRow(SketchPiece piece, float cs)
    {
        SketchTrail trail = piece.Extras.Trail;
        int preset = (int)trail.Preset;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.Combo("Trail", ref preset, SketchTrailNames, SketchTrailNames.Length))
        {
            trail.Preset = (SketchTrailPreset)preset;
            if (trail.Preset == SketchTrailPreset.Custom && trail.Model is null)
                OpenCreatorModelPicker(null, CreatorTrailPickerSlot);
            SketchChanged();
        }
        CreatorHelp("A ribbon streaming behind the piece as it moves - the streak behind a missile " +
                    "or a blade. It wears the piece's colour. Copied from the game's own trails: " +
                    "Thin white is the standard missile trail.");
        if (trail.Preset == SketchTrailPreset.None) return;

        if (trail.Preset == SketchTrailPreset.Custom)
        {
            ImGui.TextDisabled(trail.Model is { } m ? Path.GetFileName(m) : "no model yet");
            ImGui.SameLine();
            if (CreatorButton("Pick##trail-model", 48f * cs)) OpenCreatorModelPicker(null, CreatorTrailPickerSlot);
            if (trail.Model is { } model && SketchDonorModel(model) is { } donorModel)
            {
                if (donorModel.RibbonEmitters.Count == 0) ImGui.TextDisabled("That model has no trails.");
                else
                {
                    string[] names = donorModel.RibbonEmitters.Select((r, i) =>
                        $"r{i}: {Path.GetFileNameWithoutExtension(r.Texture < donorModel.Textures.Count ? donorModel.Textures[r.Texture].Filename : "?")}").ToArray();
                    int index = Math.Clamp(trail.Ribbon, 0, names.Length - 1);
                    ImGui.SetNextItemWidth(240f * cs);
                    if (ImGui.Combo("Ribbon##trail", ref index, names, names.Length))
                    {
                        trail.Ribbon = index;
                        SketchChanged();
                    }
                }
            }
        }

        float length = trail.Length;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.SliderFloat("Length s", ref length, 0.05f, 3f, "%.2f"))
        {
            trail.Length = length;
            SketchChanged();
        }
        CreatorHelp("How long each part of the trail lives, in seconds: at 20 yd/s a 0.3 s trail is " +
                    "six yards long.");
        ImGui.SameLine();
        bool own = trail.Colour is not null;
        if (ImGui.Checkbox("Own colour##trail", ref own))
        {
            trail.Colour = own ? piece.Look.Colour : null;
            SketchChanged();
        }
        if (trail.Colour is { } colour)
        {
            ImGui.SameLine();
            if (ImGui.ColorEdit3("##trail-colour", ref colour, ImGuiColorEditFlags.NoInputs))
            {
                trail.Colour = colour;
                SketchChanged();
            }
        }
    }

    /// <summary>The model picker hands a sparks / trail donor back here.</summary>
    private void PickSketchExtraModel(int slot, string path)
    {
        if (_creatorSpell is not { } doc) return;
        SketchDoc sketch = SketchForStage(doc, _sketchStage);
        if (SelectedSketchPiece(sketch) is not { } piece) return;
        if (slot == CreatorSparksPickerSlot)
        {
            piece.Extras.Sparks.Preset = SketchSparkPreset.Custom;
            piece.Extras.Sparks.Model = path;
            piece.Extras.Sparks.Emitter = 0;
            piece.Extras.Sparks.Rate = null; piece.Extras.Sparks.Speed = null;
        }
        else
        {
            piece.Extras.Trail.Preset = SketchTrailPreset.Custom;
            piece.Extras.Trail.Model = path;
            piece.Extras.Trail.Ribbon = 0;
        }
        SketchChanged();
    }
}
