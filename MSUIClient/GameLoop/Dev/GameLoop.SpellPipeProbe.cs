using System.Numerics;
using System.Text.Json.Nodes;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// SPELL PIPELINE PROBE — the standing end-to-end proof for shared_docs/SPELL_CREATOR_PLAN.md.
// Scripted, headless of any human: boots straight into the creator world, builds a spell from
// NOTHING the way an author would, and checks every hop of the pipe with the real code paths.
//
//   MSUI_SPELLPIPE_PROBE="name=Probe Ball;testlive=1"
//
//   1. New spell → a Disc sketch piece on the cast → the window's own compile;
//   2. the session entry: the sketch model ships WITH bytes (one-shot, not the editing loop),
//      its BLP ships as reason=sketch, a new spell has sourceSpellId 0, the design block
//      carries the pieces;
//   3. reopen the entry into a fresh doc: same pieces, same slot, byte-identical export;
//   4. (testlive=1) the Test live loop against the configured web app: push → complete →
//      build → download → install, and the request file the live client reads.
//
// Every check prints "[spellpipe] PASS|FAIL ..."; the last line is the tally. Extend it with
// every phase of the plan (rules, deferred destination, the ball) - never a second harness.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string? SpellPipeProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_SPELLPIPE_PROBE");

    private int _pipeStage = -1;
    private static readonly CreatorBody PipeBodyState = new(0xFF2020u, 0.5f, null, null) { Scale = 1.5f };
    private static readonly CreatorTrail PipeBodyTrail = new(0x20FF40u, 1000f, 100f);
    private double _pipeStageAt;
    private string _pipeName = "Probe Ball";
    private bool _pipeTestLive;
    private string? _pipeDesignId;
    private string? _pipeRecipe;
    private bool _pipeMissile;
    private bool _pipeShowUi;
    private bool _pipePreview;
    private bool _pipeArea;
    private int _pipeShots;
    private int _pipePass, _pipeFail;
    private JsonObject? _pipeEntry;
    private byte[]? _pipeSketchBytes;
    private bool _pipeBall;
    private bool _pipeUndo;
    private bool _pipeChild;
    private bool _pipeActor;
    private bool _pipeDope;
    private bool _pipeBody;
    private bool _pipeNewRow;
    private uint _pipeCastRow, _pipeRadiusRow;
    private int _pipeCastMs, _pipeCastFrom = 2300;
    private string? _pipeDopeModel;
    private uint _pipeChildLocal;
    private float _pipeUndoSpeed;
    private int _pipeUndoPieces;

    private void PipeCheck(bool ok, string what)
    {
        if (ok) _pipePass++; else _pipeFail++;
        Console.WriteLine($"[spellpipe] {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void UpdateSpellPipeProbe()
    {
        if (SpellPipeProbeSpec is null) return;
        double now = NowSeconds();
        if (_pipeStage < 0)
        {
            foreach (string part in SpellPipeProbeSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part[..eq].Trim().ToLowerInvariant(), value = part[(eq + 1)..].Trim();
                if (key == "name") _pipeName = value;
                else if (key == "testlive") _pipeTestLive = value is "1" or "true";
                else if (key == "designid") _pipeDesignId = value;
                else if (key == "recipe") _pipeRecipe = value;
                else if (key == "missile") _pipeMissile = value is "1" or "true";
                else if (key == "ui") _pipeShowUi = value is "1" or "true";
                else if (key == "preview") _pipePreview = value is "1" or "true";
                else if (key == "area") _pipeArea = value is "1" or "true";
                else if (key == "ball") _pipeBall = value is "1" or "true";
                else if (key == "undo") _pipeUndo = value is "1" or "true";
                else if (key == "child") _pipeChild = value is "1" or "true";
                else if (key == "actor") _pipeActor = value is "1" or "true";
                else if (key == "dope") _pipeDope = value is "1" or "true";
                else if (key == "body") _pipeBody = value is "1" or "true";
                else if (key == "newrow") _pipeNewRow = value is "1" or "true";
                else if (key == "castms" && int.TryParse(value, out int castMs)) _pipeCastFrom = castMs;
            }
            Console.WriteLine($"[spellpipe] armed: name='{_pipeName}' testlive={_pipeTestLive}");
            _pipeStage = 0; _pipeStageAt = now;
            return;
        }

        switch (_pipeStage)
        {
            case 0:
                if (_gl is null || _worldLoadStarted || now - _pipeStageAt < 1.0) return;
                EnterOfflineWorld();
                _pipeStage = 1; _pipeStageAt = now;
                return;

            case 1:   // the author's clicks: New spell, Disc, compile
            {
                if (_worldLoading || !_creatorWorldRequested || _spellCatalog is null || _spellEffects is null) return;
                if (now - _pipeStageAt < 2.0) return;
                if (_pipeDope)
                {
                    // P3.2 runs on a STOCK spell: Arcane Missiles' emitters are Blizzard's own keyframes.
                    PipeCheck(_spellCatalog.TryGet(5143, out SpellInfo arcane), "Arcane Missiles (5143) is in Spell.dbc");
                    SelectCreatorSpell(arcane);
                    _pipeStage = 50; _pipeStageAt = now;
                    return;
                }
                StartNewCreatorSpell(_pipeName);
                if (_creatorSpell is not { } doc) { PipeCheck(false, "New spell produced no doc"); _pipeStage = 9; return; }
                // A fixed design id makes two runs the SAME design: Test live then replaces the
                // spell id the first run created instead of minting a new one.
                if (_pipeDesignId is { Length: > 0 }) doc.DesignId = _pipeDesignId;
                doc.PlanStarted = true;
                doc.PlannedStages.Add(SpellStage.Cast);
                SketchDoc sketch = SketchForStage(doc, SpellStage.Cast);
                SketchPiece disc = sketch.AddPiece(SketchShapeKind.Disc);
                disc.Look.Colour = new System.Numerics.Vector3(0.4f, 0.6f, 1f);
                disc.Motion.Distance = 2f;
                disc.Motion.Duration = 0.8f;
                if (_pipeMissile)
                {
                    // The sketch IS the missile: a camera-facing disc the game flies to the point.
                    disc.Motion.Mode = SketchTravelMode.ToTarget;
                    disc.Motion.Distance = 0f;
                    disc.Place.Facing = SketchFacing.FaceCamera;
                    disc.Shape.Width = disc.Shape.Height = 0.9f;
                    disc.Look.Glow = 0.9f;
                }
                if (_pipeMissile && _pipeBall)
                {
                    // P3.5/P3.6: the ball of lightning as an author builds it - a SPHERE wearing the
                    // game's own lightning-ball image, scrolling round and over it, a crackling
                    // lightning shell spinning the other way, and a glow shell.
                    sketch.Pieces.Remove(disc);
                    SketchPiece core = sketch.AddPiece(SketchShapeKind.Sphere);
                    core.Shape.Width = core.Shape.Height = 0.8f;
                    core.Look.Image = @"Spells\LightningBall.blp";
                    core.Look.UvScroll = new System.Numerics.Vector2(0.5f, 0.25f);
                    core.Look.Colour = new System.Numerics.Vector3(0.85f, 0.85f, 1f);
                    core.Extras.Glow.Radius = 0.35f;
                    core.Motion.Mode = SketchTravelMode.ToTarget;
                    core.Motion.Distance = 0f;
                    // P3.3/P3.4: Lightning Streak's own sparks, copied cross-file, blue, crackling off it.
                    core.Extras.Sparks.Preset = SketchSparkPreset.Crackle;
                    core.Extras.Sparks.Colour = new System.Numerics.Vector3(0.55f, 0.75f, 1f);
                    core.Extras.Sparks.Rate = 60f;
                    core.Extras.Sparks.Size = 5f;    // the donor's sparks are 5 cm sprites
                    core.Extras.Sparks.Speed = 3f;
                    if (Environment.GetEnvironmentVariable("MSUI_PIPE_TRAIL") is not null)
                    {
                        core.Extras.Trail.Preset = SketchTrailPreset.Charge;      // a streak behind the flying ball
                        core.Extras.Trail.Length = 0.6f;
                    }   // out past the 0.55 yd shell within their 0.5 s life
                    SketchPiece shell = sketch.AddPiece(SketchShapeKind.Sphere);
                    shell.Shape.Width = shell.Shape.Height = 1.1f;
                    shell.Look.Image = @"Spells\Lightning4.blp";
                    shell.Look.UvScroll = new System.Numerics.Vector2(-1f, 0.5f);
                    shell.Look.Colour = new System.Numerics.Vector3(0.6f, 0.7f, 1f);
                    shell.Look.Alpha = 0.8f;
                    shell.Motion.Mode = SketchTravelMode.ToTarget;
                    shell.Motion.Distance = 0f;
                    // P3.8: two arcs - Beams wearing the blue horizontal bolt, flickering (stepped
                    // jitter) through 50 degrees twelve times a second, each on its own seed.
                    for (int a = 0; a < 2; a++)
                    {
                        SketchPiece arc = sketch.AddPiece(SketchShapeKind.Beam);
                        arc.Shape.Width = 2.6f; arc.Shape.Height = 0.7f;   // well out past the 1.1 yd shell
                        arc.Look.Image = @"Spells\Lightning3.blp";   // the blue horizontal bolt (Lightning_Side is Fire Wall's ORANGE one)
                        arc.Look.Colour = new System.Numerics.Vector3(0.7f, 0.8f, 1f);
                        arc.Motion.Mode = SketchTravelMode.ToTarget;
                        arc.Motion.Distance = 0f;
                        arc.Motion.JitterDegrees = 50f;
                        arc.Motion.JitterRate = 12f;
                        arc.Motion.JitterYards = 0.05f;
                    }
                    // P2.3: the burst on every unit the ball hits, AUTHORED here as the impact
                    // phase's own sketch: a lightning flash that swells and fades, sparks, three arcs.
                    doc.PlannedStages.Add(SpellStage.Impact);
                    SketchDoc burst = SketchForStage(doc, SpellStage.Impact);
                    SketchPiece flash = burst.AddPiece(SketchShapeKind.Sphere);
                    flash.Shape.Width = flash.Shape.Height = 0.6f;
                    flash.Look.Image = @"Spells\LightningBall.blp";
                    flash.Look.UvScroll = new System.Numerics.Vector2(1f, 0.5f);
                    flash.Look.Colour = new System.Numerics.Vector3(0.8f, 0.85f, 1f);
                    flash.Motion.GrowFrom = 0.5f; flash.Motion.GrowTo = 3.5f;
                    flash.Motion.Duration = 0.4f; flash.Motion.FadeOut = 0.4f;
                    flash.Extras.Sparks.Preset = SketchSparkPreset.Crackle;
                    flash.Extras.Sparks.Colour = new System.Numerics.Vector3(0.55f, 0.75f, 1f);
                    flash.Extras.Sparks.Rate = 250f; flash.Extras.Sparks.Size = 6f; flash.Extras.Sparks.Speed = 5f;
                    for (int a = 0; a < 3; a++)
                    {
                        SketchPiece zap = burst.AddPiece(SketchShapeKind.Beam);
                        zap.Shape.Width = 2f; zap.Shape.Height = 0.6f;
                        zap.Look.Image = @"Spells\Lightning3.blp";
                        zap.Look.Colour = new System.Numerics.Vector3(0.7f, 0.8f, 1f);
                        zap.Motion.GrowFrom = 0.6f; zap.Motion.GrowTo = 1.6f;
                        zap.Motion.Duration = 0.4f; zap.Motion.FadeOut = 0.35f;
                        zap.Motion.JitterDegrees = 70f; zap.Motion.JitterRate = 15f;
                    }
                    _sketchStage = SpellStage.Impact;
                    _sketchSlot = 2;
                    CompileSketch(doc, burst);
                    string burstPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, "impact");
                    PipeCheck(doc.Composition.TryGetValue(SpellStage.Impact, out CreatorStageComposition? impactPhase) &&
                              impactPhase.Slots.Contains(burstPath, StringComparer.OrdinalIgnoreCase),
                        "the impact sketch sits in the impact phase");
                    // P4.4: the landing jolts the camera - War Stomp's own bump (SpellEffectCameraShakes 3).
                    if (impactPhase is not null) impactPhase.Shake = 3;
                    // P4.3/P4.4: and a WORLD effect - Thunder Clap's ground ring planted at each victim's feet.
                    if (impactPhase is not null && EnsureCreatorModel(doc, @"Spells\ThunderClap_Cast_Base.m2"))
                        impactPhase.WorldEffect = @"Spells\ThunderClap_Cast_Base.m2";

                    // §8 timeline: Precast = the caster readies (ReadySpellOmni) with lightning
                    // crackling in BOTH hands; Cast = SpellCastOmni. Ids looked up BY NAME.
                    ushort? AnimationByName(string wanted)
                    {
                        for (int id = 0; id < 512; id++)
                            if (_animationData?.TryGetName(id, out string name) == true &&
                                string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return (ushort)id;
                        return null;
                    }
                    doc.PlannedStages.Add(SpellStage.Precast);
                    SetCreatorSlot(doc, SpellStage.Precast, 3, @"Spells\Lightning_Cast_Hand.m2");
                    SetCreatorSlot(doc, SpellStage.Precast, 4, @"Spells\Lightning_Cast_Hand.m2");
                    ushort? ready = AnimationByName("ReadySpellOmni"), release = AnimationByName("SpellCastOmni");
                    PipeCheck(ready is not null && release is not null, $"ReadySpellOmni / SpellCastOmni found by name ({ready}/{release})");
                    if (doc.Composition.TryGetValue(SpellStage.Precast, out CreatorStageComposition? precastPhase)) precastPhase.AnimationId = ready;
                    if (doc.Composition.TryGetValue(SpellStage.Cast, out CreatorStageComposition? castPhase)) castPhase.AnimationId = release;
                    PipeCheck(precastPhase?.Slots[3] is { Length: > 0 } && precastPhase.Slots[4] is { Length: > 0 },
                        "the precast crackles in both hands");

                    // P3.9: the game's own sounds, picked - Call Lightning's precast hum, the nature
                    // missile loop in flight, the Blasted Lands bolt (3 variants) on landing.
                    SetCreatorPickedSound(doc, CreatorAudioCue.Precast, 31);
                    SetCreatorPickedSound(doc, CreatorAudioCue.Missile, 3014);
                    SetCreatorPickedSound(doc, CreatorAudioCue.Impact, 4936);
                    PipeCheck(CreatorPickedSound(doc, CreatorAudioCue.Precast) == 31 && CreatorPickedSound(doc, CreatorAudioCue.Missile) == 3014 &&
                              CreatorPickedSound(doc, CreatorAudioCue.Impact) == 4936,
                        "a NEW spell's phases take picked game sounds (precast 31, missile 3014, impact 4936)");
                    disc = core;
                }
                if (_pipeRecipe is { Length: > 0 } recipeName)
                {
                    CreatorRecipe? recipe = CreatorRecipes.FirstOrDefault(r => r.Name.Equals(recipeName, StringComparison.OrdinalIgnoreCase));
                    PipeCheck(recipe is not null, $"recipe '{recipeName}' exists");
                    if (recipe is not null && CreatorRules(doc) is { } rules)
                    {
                        ApplyCreatorRecipe(doc, rules, recipe);
                        rules.SetPoints(1, 120, 140);
                        if (_pipeNewRow && SpellRuleTablesLoaded is { } rowTables)
                        {
                            // D5: a 2.3 s cast and a 7.5 yd blast - values no stock row says. The first
                            // build adds rows for them; later builds find those rows in patch-3.
                            // The first cast time from 2.3 s on that no STOCK row says (a built row, 1000+, may).
                            for (_pipeCastMs = _pipeCastFrom; ; _pipeCastMs += 50)
                                if (rowTables.ExactRow("SpellCastTimes", _pipeCastMs) is var stock && (stock == 0 || stock >= 1000)) break;
                            _pipeCastRow = SetRulesIndexValue(rules, rowTables, "castingTimeIndex", _pipeCastMs);
                            _pipeRadiusRow = SetRulesIndexValue(rules, rowTables, "effectRadiusIndex1", 7.5);
                            PipeCheck((SpellRuleTables.IsLocalRow(_pipeCastRow) || _pipeCastRow >= 1000) &&
                                      (SpellRuleTables.IsLocalRow(_pipeRadiusRow) || _pipeRadiusRow >= 1000),
                                $"{_pipeCastMs} ms and 7.5 yd are new rows or rows a build added (cast {_pipeCastRow}, radius {_pipeRadiusRow})");
                            PipeCheck(rowTables.CastTimeText((uint)rules.GetInt("castingTimeIndex")) == $"{_pipeCastMs / 1000f:0.##} s" &&
                                      rowTables.RadiusText((uint)rules.GetInt("effectRadiusIndex1")) == "7.5 yd",
                                $"the pickers read the new rows like any other ({_pipeCastMs} ms, 7.5 yd)");
                        }
                        if (_pipeArea)
                        {
                            // The lightning FIELD the ball leaves: Blizzard's pattern (a persistent
                            // area aura of periodic damage at the destination), nature, 6 s, 1 s ticks,
                            // and Call Lightning's bolts as the area's shards.
                            rules.Set("school", 3);
                            rules.Set("effect2", 27);
                            rules.Set("effectApplyAuraName2", 3);
                            rules.Set("effectImplicitTargetA2", 28);
                            rules.Set("effectImplicitTargetB2", 0);
                            rules.Set("effectRadiusIndex2", 13);
                            rules.Set("effectAmplitude2", 1000);
                            rules.SetPoints(2, 25, 25);
                            if (SpellRuleTablesLoaded is { } t)
                            {
                                uint six = t.Durations.Where(d => d.Value.BaseMs == 6000).Select(d => d.Key).DefaultIfEmpty(0u).Min();
                                PipeCheck(six != 0, $"a 6 s duration row exists (row {six})");
                                rules.Set("durationIndex", six);
                            }
                            doc.Area.ShardSelector = 2;     // Spells\CallLightning_Impact.m2
                            doc.Area.ShardRate = 8f;
                            PipeCheck(doc.Area.Changed, "the area composition changed (Call Lightning shards)");
                            if (_pipeBall && SpellRuleTablesLoaded is { } tables)
                            {
                                // SPELL_CREATOR_PLAN.md §8, to the number: cast 2.0 s, 30 yd, 6 yd/s,
                                // 8 yd blast - so a cast 24 yd away flies for 4.0 s.
                                uint two = tables.CastTimes.Where(c => c.Value.BaseMs == 2000 && c.Value.PerLevelMs == 0).Select(c => c.Key).DefaultIfEmpty(0u).Min();
                                uint thirty = tables.Ranges.Where(r => MathF.Abs(r.Value.Max - 30f) < 0.01f && r.Value.Min == 0f).Select(r => r.Key).DefaultIfEmpty(0u).Min();
                                uint eight = tables.Radii.Where(r => MathF.Abs(r.Value.Yards - 8f) < 0.01f).Select(r => r.Key).DefaultIfEmpty(0u).Min();
                                PipeCheck(two != 0 && thirty != 0 && eight != 0, $"rows for 2.0 s / 30 yd / 8 yd exist ({two}/{thirty}/{eight})");
                                rules.Set("castingTimeIndex", two);
                                rules.Set("rangeIndex", thirty);
                                rules.Set("effectRadiusIndex1", eight);
                                rules.Set("speed", 6f);
                            }
                        }
                        var lines = Creator.Spells.SpellLegality.Validate(rules, SpellRuleTablesLoaded,
                            id => SpellRuleTablesLoaded?.TryGetSpellRow(id, out _) == true);
                        foreach (var line in lines) Console.WriteLine($"[spellpipe] law {line.Severity}: {line.Message}");
                        PipeCheck(!lines.Any(l => l.Severity == Creator.Spells.SpellLegality.Severity.Error),
                            "the recipe's rules pass the legality law with no error");
                        PipeCheck(rules.GroundTargeted == (recipe.Name == "Ground missile" || recipe.Name == "Burst + linger" || recipe.Name == "Rain"),
                            "the recipe's ground targeting is as the recipe says");
                    }
                }
                _sketchStage = SpellStage.Cast;
                _sketchOpen = true;
                CompileSketch(doc, sketch);
                string path = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, "cast");
                PipeCheck(doc.Models.ContainsKey(path), $"the sketch compiled into a phase model ({path})");
                if (_pipeMissile)
                    PipeCheck(string.Equals(doc.MissileOverride, path, StringComparison.OrdinalIgnoreCase),
                        "the sketch model is the spell's missile");
                else
                    PipeCheck(doc.Composition.TryGetValue(SpellStage.Cast, out var c) && c.Slots.Contains(path, StringComparer.OrdinalIgnoreCase),
                        "the sketch model sits in a cast slot");
                if (_pipeBody)
                {
                    // P4.4 body dials: the aura washes the body red and makes it see-through; the cast
                    // swings a green weapon trail for a second.
                    PipeCheck(doc.Composition.ContainsKey(SpellStage.State) && doc.Composition.ContainsKey(SpellStage.Cast),
                        "the doc has State and Cast phases");
                    if (doc.Composition.TryGetValue(SpellStage.State, out CreatorStageComposition? statePhase)) statePhase.Body = PipeBodyState;
                    if (doc.Composition.TryGetValue(SpellStage.Cast, out CreatorStageComposition? castPhase)) castPhase.Body = castPhase.Body with { Trail = PipeBodyTrail };
                    PipeCheck(CreatorStageAvailable(doc, SpellStage.State), "a State that only changes the body can play");
                    _pipeStage = 60; _pipeStageAt = now;
                    return;
                }
                _pipeStage = _pipeActor ? 40 : _pipeChild ? 30 : _pipeUndo ? 20 : 2; _pipeStageAt = now;
                return;
            }

            // ── P4.4: the body dials preview on the caster ──
            case 60:
            {
                if (_creatorSpell is not { } doc) { _pipeStage = 9; return; }
                _creatorLoopOn = false;
                PipeCheck(PresentSpellEffect(doc.Info.Id, "state"), "the body-only State presents");
                _pipeStage = 61; _pipeStageAt = now;
                return;
            }
            case 61:   // the 1 s alpha ramp has run: the caster is red and half see-through
            {
                if (now - _pipeStageAt < 1.6) return;
                Vector3 tint = _creatorBodyAura.Tint;
                PipeCheck(CreatorBodyAura is not null && MathF.Abs(tint.X - 1f) < 0.01f && MathF.Abs(tint.Y - 0x20 / 255f) < 0.01f &&
                          MathF.Abs(_creatorBodyAura.Alpha - 0.5f) < 0.02f,
                    $"the preview body is tinted #FF2020 and see-through (tint {tint}, alpha {_creatorBodyAura.Alpha:0.00})");
                CharacterRenderer.UnitState body = BuildUnitState(includeAuraVisual: true);
                PipeCheck(body.ApplyBodyVisual && MathF.Abs(body.BodyAlpha - 0.5f) < 0.02f && body.BodyTint == tint,
                    "the character renders with that body look");
                PipeCheck(MathF.Abs(_creatorBodyAura.Scale - 1.5f) < 0.001f && MathF.Abs((_character?.ModelScale ?? 0f) - 1.5f) < 0.01f,
                    $"and half as big again (aura scale {_creatorBodyAura.Scale:0.##}, model scale {_character?.ModelScale:0.##})");
                ArmGameplayDumpNamed("spellpipe-body");
                _pipeStage = 62; _pipeStageAt = now;
                return;
            }
            case 62:   // the frame is taken; State off: the body comes back
            {
                if (now - _pipeStageAt < 1.0) return;
                ReapPresentedEffect();
                _pipeStage = 63; _pipeStageAt = now;
                return;
            }
            case 63:
            {
                if (now - _pipeStageAt < 1.3) return;
                PipeCheck(_creatorBodyAura.Tint == Vector3.One && _creatorBodyAura.Alpha > 0.98f && _creatorBodyAura.Scale == 1f,
                    $"State off: the body is itself again (alpha {_creatorBodyAura.Alpha:0.00})");
                _pipeStage = 2; _pipeStageAt = now;
                return;
            }

            // ── P3.2: the dope sheet on a stock spell ──
            case 50:   // select the first model with emitters, its e0; play a moment, then pause
            {
                if (now - _pipeStageAt < 2.0 || _creatorSpell is not { } doc) return;
                CreatorModelDoc? model = doc.Models.Values.FirstOrDefault(m =>
                    Creator.M2TrackPatcher.Enumerate(m.Working).Any(t => t.Id == "e0.rate"));
                PipeCheck(model is not null, $"a model of the spell has an e0 rate track ({model?.Path})");
                if (model is null) { _pipeStage = 9; return; }
                _pipeDopeModel = model.Path;
                // The workshop as an author opens it, so the frame shows the lanes.
                Settings.Creator.Workspace = true;
                Settings.Creator.SpellFocus = true;
                _creatorPanel = CreatorPanel.Spells;
                _workspaceView = WorkspaceView.Root;
                _spellFocusSuppressed = false;
                _spellIdeHidden = false;
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Emitter, model.Path, 0));
                _creatorLoopOn = true; _creatorLoopNextAt = 0;
                _pipeStage = 51; _pipeStageAt = now;
                return;
            }
            case 51:   // pause; add a key through the sheet's own commit; check bytes, design, reopen
            {
                if (now - _pipeStageAt < 1.2 || _creatorSpell is not { } doc || _pipeDopeModel is null ||
                    !doc.Models.TryGetValue(_pipeDopeModel, out CreatorModelDoc? model)) return;
                SetCreatorClockPaused(true);
                Creator.TrackInfo rate = Creator.M2TrackPatcher.Find(model.Working, "e0.rate")!;
                Creator.TrackKeys keys = Creator.M2TrackPatcher.Read(model.Working, rate)!;
                var seqs = Creator.M2TrackPatcher.Sequences(model.Working);
                uint mid = seqs.Count > 0 ? seqs[0].Start + (seqs[0].End - seqs[0].Start) / 2 : 250u;
                Creator.TrackKeys add = keys.Clone();
                add.Times.Add(mid); add.Values.Add(new[] { 77f });
                CommitDopeTrack(model, rate, add);
                Creator.TrackKeys? after = Creator.M2TrackPatcher.Read(model.Working, Creator.M2TrackPatcher.Find(model.Working, "e0.rate")!);
                PipeCheck(after is not null && after.Times.Contains(mid) && after.Values[after.Times.IndexOf(mid)][0] == 77f &&
                          after.Times.Count == keys.Times.Count + (keys.Times.Contains(mid) ? 0 : 1),
                    $"a key added in the sheet is in the model's bytes ({keys.Times.Count} -> {after?.Times.Count} keys, 77 at {mid} ms)");
                // P3.3: graft Lightning Streak's sparks into this Blizzard model.
                int emittersBefore = model.Emitters.Count();
                model.Transplants.Add(new CreatorTransplant(@"Spells\LightningStreak_Missile.m2", 0, false));
                RebuildCreatorModel(model);
                PipeCheck(model.Emitters.Count() == emittersBefore + 1 &&
                          M2Reader.Parse(model.Working)?.ParticleEmitters.Count == emittersBefore + 1,
                    $"a grafted emitter joins the stock model ({emittersBefore} -> {model.Emitters.Count()})");
                System.Text.Json.Nodes.JsonObject design = CaptureCreatorDesign(doc);
                PipeCheck(design.ToJsonString().Contains("\"e0.rate\"") && design.ToJsonString().Contains("LightningStreak_Missile"),
                    "the design carries the track edit and the graft");
                _pipeStage = 52; _pipeStageAt = now;
                return;
            }
            case 52:   // the paused timeline has drawn its lanes by now: the frame; then reopen
            {
                if (now - _pipeStageAt < 1.0) return;
                ArmGameplayDumpNamed("spellpipe-dopesheet");
                _pipeStage = 53; _pipeStageAt = now;
                return;
            }
            case 53:   // reopen the design: the edit comes back
            {
                if (now - _pipeStageAt < 1.0 || _creatorSpell is not { } doc) return;
                System.Text.Json.Nodes.JsonObject design = CaptureCreatorDesign(doc);
                string verdict = ApplyCreatorDesign(design, new Dictionary<string, byte[]>(), new Dictionary<string, byte[]>());
                PipeCheck(_creatorSpell is { } back && _pipeDopeModel is not null && back.Models.TryGetValue(_pipeDopeModel, out var m) &&
                          m.TrackEdits.ContainsKey("e0.rate") &&
                          Creator.M2TrackPatcher.Read(m.Working, Creator.M2TrackPatcher.Find(m.Working, "e0.rate")!)!.Values.Any(v => v[0] == 77f) &&
                          m.Transplants.Count == 1,
                    $"reopening the design restores the keyframe edit and the graft ({verdict})");
                _pipeStage = 9; _pipeStageAt = now;
                return;
            }

            // ── P4.2: an actor - the ball lands and leaves an orb that zaps the nearest enemy ──
            case 40:
            {
                if (now - _pipeStageAt < 1.0 || _creatorSpell is not { } parent || parent.Rules is not { } rules) return;
                rules.SetPoints(1, 3, 3);                       // the shell only taps; the ORB does the work
                rules.Set("effect2", 87);                       // SUMMON_TOTEM_SLOT1, as Searing Totem (3599)
                rules.Set("effectImplicitTargetA2", 44);
                rules.Set("effectImplicitTargetB2", 0);
                if (SpellRuleTablesLoaded is { } t)
                    rules.Set("durationIndex", t.Durations.Where(d => d.Value.BaseMs == 6000).Select(d => d.Key).DefaultIfEmpty(0u).Min());
                parent.Actor = new CreatorActor { Effect = 2, Name = "Storm Orb", DisplayId = 4589, Scale = 1.2f };
                CreatorActor orb = parent.Actor;
                NewCreatorChildFor(parent, c => orb.Spell = c.LocalId);
                PipeCheck(EditingCreatorChild, "the actor's '+ new child spell' opens the zap from zero");
                _pipeStage = 41; _pipeStageAt = now;
                return;
            }
            case 41:   // the zap: 1 s cast (that paces the orb), 20 yd, a nature bolt at an enemy
            {
                if (now - _pipeStageAt < 1.0 || _creatorSpell is not { } zap || CreatorRules(zap) is not { } rules) return;
                if (SpellRuleTablesLoaded is { } t)
                {
                    rules.Set("castingTimeIndex", t.CastTimes.Where(c => c.Value.BaseMs == 1000 && c.Value.PerLevelMs == 0).Select(c => c.Key).DefaultIfEmpty(1u).Min());
                    rules.Set("rangeIndex", t.Ranges.Where(r => MathF.Abs(r.Value.Max - 20f) < 0.01f && r.Value.Min == 0f).Select(r => r.Key).DefaultIfEmpty(0u).Min());
                }
                rules.Set("school", 3);
                rules.Set("effect1", 2);
                rules.Set("effectImplicitTargetA1", 6);
                rules.Set("effectImplicitTargetB1", 0);
                rules.SetPoints(1, 15, 20);
                zap.PlanStarted = true;
                zap.PlannedStages.Add(SpellStage.Impact);
                SetCreatorSlot(zap, SpellStage.Impact, 1, @"Spells\LightningBolt_Impact_Chest.m2");
                _pipeChildLocal = zap.Info.Id;
                _pipeStage = 42; _pipeStageAt = now;
                return;
            }
            case 42:
            {
                if (now - _pipeStageAt < 1.0) return;
                ReturnToCreatorParent();
                PipeCheck(!EditingCreatorChild && _creatorSpell is { Actor: { } a } back && a.Spell == _pipeChildLocal &&
                          back.Children.Count == 1 && back.Children[0].Entry is not null,
                    $"back in the parent: the actor casts the zap (#{_pipeChildLocal}), which travels inside it");
                _pipeStage = 2; _pipeStageAt = now;
                return;
            }

            // ── P4.1: a child spell - the ball lands, its child explodes ──
            case 30:   // the parent's effect 3 becomes TRIGGER_MISSILE (once, at the point) to a NEW child
            {
                if (now - _pipeStageAt < 1.0 || _creatorSpell is not { } parent || parent.Rules is not { } parentRules) return;
                parentRules.SetPoints(1, 3, 3);   // the shell only taps: the CHILD's blast must find living targets
                parentRules.Set("effect3", 32);
                parentRules.Set("effectImplicitTargetA3", 1);
                parentRules.Set("effectImplicitTargetB3", 0);
                NewCreatorChild(parent, 3);
                PipeCheck(EditingCreatorChild && _creatorSpell is { } c && !ReferenceEquals(c, parent) && IsCreatorNewSpellId(c.Info.Id),
                    "+ new child spell opens a new spell from zero in the editor");
                _pipeStage = 31; _pipeStageAt = now;
                return;
            }
            case 31:   // author the child: an instant nature blast at its cast point, a sketched ring
            {
                if (now - _pipeStageAt < 1.0 || _creatorSpell is not { } child || CreatorRules(child) is not { } rules) return;
                rules.Set("castingTimeIndex", 1);
                rules.Set("school", 3);
                rules.Set("effect1", 2);
                rules.Set("effectImplicitTargetA1", 16);
                rules.Set("effectImplicitTargetB1", 0);
                if (SpellRuleTablesLoaded is { } t)
                    rules.Set("effectRadiusIndex1", t.Radii.Where(r => MathF.Abs(r.Value.Yards - 10f) < 0.01f).Select(r => r.Key).DefaultIfEmpty(0u).Min());
                rules.SetPoints(1, 60, 70);
                child.PlanStarted = true;
                child.PlannedStages.Add(SpellStage.Impact);
                SketchDoc ring = SketchForStage(child, SpellStage.Impact);
                SketchPiece wave = ring.AddPiece(SketchShapeKind.Ring);
                wave.Place.Facing = SketchFacing.Flat;
                wave.Place.Origin = new System.Numerics.Vector3(0f, 0f, 0.3f);
                wave.Look.Colour = new System.Numerics.Vector3(0.6f, 0.8f, 1f);
                wave.Shape.Thickness = 0.25f;
                wave.Motion.GrowFrom = 0.5f; wave.Motion.GrowTo = 12f;
                wave.Motion.Duration = 0.5f; wave.Motion.FadeOut = 0.45f;
                wave.Extras.Sparks.Preset = SketchSparkPreset.Crackle;
                wave.Extras.Sparks.Rate = 200f; wave.Extras.Sparks.Size = 6f; wave.Extras.Sparks.Speed = 6f;
                _sketchStage = SpellStage.Impact;
                _sketchSlot = 2;
                CompileSketch(child, ring);
                SetCreatorPickedSound(child, CreatorAudioCue.Impact, 4936);
                _pipeChildLocal = child.Info.Id;
                _pipeStage = 32; _pipeStageAt = now;
                return;
            }
            case 32:   // back to the parent: the child travels inside it, the trigger names it
            {
                if (now - _pipeStageAt < 1.0) return;
                ReturnToCreatorParent();
                PipeCheck(!EditingCreatorChild && _creatorSpell is { } back && back.Children.Count == 1 &&
                          back.Children[0].LocalId == _pipeChildLocal && back.Children[0].Entry is not null &&
                          back.Rules?.GetInt("effectTriggerSpell3") == (int)_pipeChildLocal,
                    $"back in the parent: one child (#{_pipeChildLocal}) with its export, effect 3 triggers it");
                _pipeStage = _pipeUndo ? 20 : 2; _pipeStageAt = now;
                return;
            }

            // ── P3.7: universal undo - a rule edit and a sketch edit, each its own step ──
            case 20:   // let the design settle into its first commit, then edit a RULE
            {
                if (now - _pipeStageAt < 1.5 || _creatorSpell?.Rules is not { } rules) return;
                _pipeUndoSpeed = (float)rules.Speed;
                _pipeUndoPieces = SketchForStage(_creatorSpell, SpellStage.Cast).Pieces.Count;
                rules.Set("speed", 13);
                _pipeStage = 21; _pipeStageAt = now;
                return;
            }
            case 21:   // committed; now a SKETCH edit
            {
                if (now - _pipeStageAt < 1.5 || _creatorSpell is not { } doc) return;
                SketchDoc castSketch = SketchForStage(doc, SpellStage.Cast);
                castSketch.AddPiece(SketchShapeKind.Star);
                SketchChanged();
                _pipeStage = 22; _pipeStageAt = now;
                return;
            }
            case 22:   // undo the sketch edit, then the rule edit; redo the rule
            {
                if (now - _pipeStageAt < 1.5 || _creatorSpell is null) return;
                PipeCheck(_designUndo.Count >= 2, $"two edits made two undo steps ({_designUndo.Count})");
                UndoCreatorDesign();
                PipeCheck(_creatorSpell is { } a && SketchForStage(a, SpellStage.Cast).Pieces.Count == _pipeUndoPieces &&
                          Math.Abs(a.Rules!.Speed - 13) < 0.01,
                    "undo 1 takes the sketch piece back and keeps the rule");
                UndoCreatorDesign();
                PipeCheck(_creatorSpell is { } b && Math.Abs(b.Rules!.Speed - _pipeUndoSpeed) < 0.01,
                    $"undo 2 restores the rule (speed {_creatorSpell?.Rules?.Speed} = {_pipeUndoSpeed})");
                RedoCreatorDesign();
                PipeCheck(_creatorSpell is { } c && Math.Abs(c.Rules!.Speed - 13) < 0.01 && _designRedo.Count == 1,
                    "redo brings the rule edit back, one redo left");
                RedoCreatorDesign();
                PipeCheck(_creatorSpell is { } d && SketchForStage(d, SpellStage.Cast).Pieces.Count == _pipeUndoPieces + 1,
                    "redo 2 brings the sketch piece back");
                UndoCreatorDesign(); UndoCreatorDesign();   // back to the design under test
                PipeCheck(_creatorSpell is { } e && Math.Abs(e.Rules!.Speed - _pipeUndoSpeed) < 0.01 &&
                          SketchForStage(e, SpellStage.Cast).Pieces.Count == _pipeUndoPieces, "and back to the start");
                _pipeStage = 2; _pipeStageAt = now;
                return;
            }

            case 2:   // the export
            {
                if (now - _pipeStageAt < 2.0) return;
                if (_creatorSpell is not { } doc) { _pipeStage = 9; return; }
                JsonObject? entry = BuildCreatorSpellEntry(doc, _pipeName);
                PipeCheck(entry is not null, $"a session entry was built ({_creatorExportStatus})");
                if (entry is null) { _pipeStage = 9; return; }
                _pipeEntry = entry;
                PipeCheck(entry["sourceSpellId"]?.GetValue<uint>() == 0, "a new spell exports sourceSpellId 0");
                if (_pipeBall)
                {
                    JsonNode? comp = entry["composition"];
                    PipeCheck(comp?["precast"]?["sound"]?.GetValue<uint>() == 31 && comp?["impact"]?["sound"]?.GetValue<uint>() == 4936 &&
                              comp?["missile"]?["sound"]?.GetValue<uint>() == 3014 && comp?["impact"]?["cameraShake"]?.GetValue<uint>() == 3 &&
                              CreatorAudioAvailable(doc, CreatorAudioCue.Impact) && CreatorAudioAvailable(doc, CreatorAudioCue.Missile),
                        "the export carries the picked sounds and the shake; the new spell's impact and missile can sound");
                }
                if (_pipeNewRow)
                {
                    JsonNode? rulesBlock = entry["rules"];
                    long castColumn = rulesBlock?["columns"]?["castingTimeIndex"]?.GetValue<long>() ?? 0;
                    JsonArray? rows = rulesBlock?["newIndexRows"] as JsonArray;
                    Console.WriteLine($"[spellpipe] newIndexRows: {rows?.ToJsonString() ?? "(none)"}");
                    PipeCheck(castColumn == _pipeCastRow &&
                              (!SpellRuleTables.IsLocalRow(_pipeCastRow) ||
                               rows?.Any(r => r?["table"]?.GetValue<string>() == "SpellCastTimes" &&
                                              r["localId"]?.GetValue<uint>() == _pipeCastRow && r["value"]?.GetValue<double>() == _pipeCastMs) == true),
                        "the export names the cast-time row and carries any new row it needs");
                }
                if (_pipeBody)
                {
                    JsonArray? stateLanes = entry["composition"]?["state"]?["charProcs"] as JsonArray;
                    JsonArray? castLanes = entry["composition"]?["cast"]?["charProcs"] as JsonArray;
                    bool Lane(JsonArray? lanes, int type, int param, float value) => lanes?.Any(l =>
                        l?["type"]?.GetValue<int>() == type && MathF.Abs((l["params"]?[param]?.GetValue<float>() ?? -9f) - value) < 0.01f) == true;
                    PipeCheck(stateLanes?.Count == 4 && Lane(stateLanes, 1, 0, 0xFF2020) && Lane(stateLanes, 14, 0, 0.5f) && Lane(stateLanes, 2, 0, 1.5f),
                        $"the export's State lanes: tint #FF2020 + alpha 0.5 + scale 1.5, four positional lanes ({stateLanes?.ToJsonString()})");
                    PipeCheck(Lane(castLanes, 8, 0, 0x20FF40) && Lane(castLanes, 8, 2, 1000f),
                        $"the export's Cast lanes: a green 1 s weapon trail ({castLanes?.ToJsonString()})");
                }
                // The CAST sketch (the missile); an impact sketch, when there is one, ships beside it.
                JsonNode? model = (entry["models"] as JsonArray)?.FirstOrDefault(m => m?["authored"]?.GetValue<string>() == "sketch" &&
                    m["path"]?.GetValue<string>().EndsWith("sketch_cast.m2", StringComparison.OrdinalIgnoreCase) == true);
                string? b64 = model?["m2Base64"]?.GetValue<string>();
                PipeCheck(b64 is { Length: > 0 }, "the sketch model ships with its bytes");
                if (b64 is { Length: > 0 })
                {
                    _pipeSketchBytes = Convert.FromBase64String(b64);
                    PipeCheck(M2Reader.Parse(_pipeSketchBytes) is { IsValid: true }, "the shipped sketch M2 parses");
                    SketchDoc sketch = doc.Sketches[SpellStage.Cast];
                    byte[]? oneShot = SketchWriter.Build(sketch, (int)doc.Info.Id, doc.Info.Name, _sketchBlpWriter,
                        loopForEditing: false, archive: SketchArchive)?.Model;
                    PipeCheck(oneShot is not null && oneShot.AsSpan().SequenceEqual(_pipeSketchBytes),
                        "the shipped bytes are the ONE-SHOT compile (never the editing loop)");
                }
                bool blp = (entry["tintedBlps"] as JsonArray)?.Any(b => b?["reason"]?.GetValue<string>() == "sketch" &&
                    b["blpBase64"]?.GetValue<string>() is { Length: > 0 }) == true;
                if (_pipeBall)
                {
                    // Image-wearing pieces ship a PATH, not a picture: no sketch BLP at all, and the
                    // model names the archive images.
                    PipeCheck(!blp, "image-wearing pieces ship no sketch BLP");
                    M2Model? ballModel = _pipeSketchBytes is { } ballBytes ? M2Reader.Parse(ballBytes) : null;
                    PipeCheck(ballModel?.Textures.Any(t => t.Filename.EndsWith("LightningBall.blp", StringComparison.OrdinalIgnoreCase) && (t.Flags & 3) == 3) == true &&
                              ballModel.TextureTransforms.Count == 2,
                        "the ball model wears the archive image, wrapped, with two scrolling texture transforms");
                    PipeCheck(ballModel?.ParticleEmitters.Count == 1 && ballModel.Bones.Count == 7,
                        $"the shipped ball carries its copied sparks and two arc modifier bones ({ballModel?.ParticleEmitters.Count} emitters, {ballModel?.Bones.Count} bones)");
                }
                else PipeCheck(blp, "the sketch's BLP ships as reason=sketch");
                if (_pipeRecipe is { Length: > 0 })
                {
                    JsonNode? cols = entry["rules"]?["columns"];
                    Console.WriteLine($"[spellpipe] rules block: {cols?.ToJsonString()}");
                    PipeCheck(cols is JsonObject { Count: > 0 }, "the entry carries a rules block");
                    if (_pipeArea)
                        PipeCheck(entry["composition"]?["area"]?["shardSelector"]?.GetValue<int>() == 2 &&
                                  cols?["effect2"]?.GetValue<long>() == 27,
                            "the entry carries the area composition and the persistent-area effect");
                    if (_pipeRecipe.Equals("Ground missile", StringComparison.OrdinalIgnoreCase))
                        PipeCheck(cols?["targets"]?.GetValue<long>() == 64 && cols?["customFlags"]?.GetValue<long>() == 0x4000 &&
                                  Math.Abs((cols?["speed"]?.GetValue<float>() ?? 0f) - (_pipeBall && _pipeArea ? 6f : 8f)) < 0.01f,
                            $"ground missile rules: targets 0x40, deferred flag, speed {(_pipeBall && _pipeArea ? 6 : 8)}");
                }
                int pieces = (entry["design"]?["sketches"]?["cast"]?["pieces"] as JsonArray)?.Count ?? 0;
                PipeCheck(pieces == (_pipeBall ? 4 : 1), $"the design block carries the sketch pieces ({pieces})");
                AddCreatorSpellToSession(doc, _pipeName);
                PipeCheck(File.Exists(CreatorSessionPath), $"the entry was written to {CreatorSessionPath}");
                _pipeStage = 3; _pipeStageAt = now;
                return;
            }

            case 3:   // reopen into a fresh doc
            {
                StartNewCreatorSpell("Probe Scratch");
                string verdict = OpenCreatorSessionEntry(_pipeEntry!);
                Console.WriteLine($"[spellpipe] reopen: {verdict}");
                bool reopened = _creatorSpell is { } doc && doc.Info.Name == _pipeName &&
                                doc.Sketches.TryGetValue(SpellStage.Cast, out SketchDoc? s) && s.Pieces.Count == (_pipeBall ? 4 : 1) &&
                                (!_pipeBall || (s.Pieces[0].Shape.Kind == SketchShapeKind.Sphere && s.Pieces[0].Look.Image is { Length: > 0 } &&
                                                s.Pieces[0].Look.UvScroll.X > 0.4f));
                PipeCheck(reopened, "reopening restores the spell and its pieces (sphere, image and scroll included)");
                if (_pipeBody)
                    PipeCheck(_creatorSpell is { } bodyDoc &&
                              bodyDoc.Composition.TryGetValue(SpellStage.State, out CreatorStageComposition? st) && st.Body == PipeBodyState &&
                              bodyDoc.Composition.TryGetValue(SpellStage.Cast, out CreatorStageComposition? ct) && ct.Body.Trail == PipeBodyTrail,
                        "reopening restores the body dials (State tint + alpha, Cast trail)");
                if (_creatorSpell is { } again && _pipeSketchBytes is not null &&
                    BuildCreatorSpellEntry(again, _pipeName) is { } second)
                {
                    string? b64 = (second["models"] as JsonArray)?
                        .FirstOrDefault(m => m?["authored"]?.GetValue<string>() == "sketch" &&
                            m["path"]?.GetValue<string>().EndsWith("sketch_cast.m2", StringComparison.OrdinalIgnoreCase) == true)?["m2Base64"]?.GetValue<string>();
                    PipeCheck(b64 is not null && Convert.FromBase64String(b64).AsSpan().SequenceEqual(_pipeSketchBytes),
                        "the reopened design exports byte-identical sketch bytes");
                    PipeCheck(second["designId"]?.GetValue<string>() == _pipeEntry!["designId"]?.GetValue<string>(),
                        "the reopened design keeps its design id");
                    string rulesBefore = _pipeEntry["rules"]?.ToJsonString() ?? "", rulesAfter = second["rules"]?.ToJsonString() ?? "";
                    if (rulesBefore != rulesAfter)
                        Console.WriteLine($"[spellpipe] rules before: {rulesBefore}{Environment.NewLine}[spellpipe] rules after:  {rulesAfter}");
                    PipeCheck(rulesBefore == rulesAfter, "the reopened design carries the same rules block");
                }
                if (_pipeShowUi)
                {
                    // The workshop exactly as an author opens it: the IDE layout on the Spells panel,
                    // the RULES part selected - so the frame shows the cards, not the bare world.
                    Settings.Creator.Workspace = true;
                    Settings.Creator.SpellFocus = true;
                    _creatorPanel = CreatorPanel.Spells;
                    _workspaceView = WorkspaceView.Root;
                    _spellFocusSuppressed = false;
                    _spellIdeHidden = false;
                    _spellIdeInspectorHidden = false;
                    SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Rules));
                    _pipeStage = 5; _pipeStageAt = now;
                    return;
                }
                ArmGameplayDumpNamed("spellpipe-reopened");
                if (_pipeTestLive && _creatorSpell is { } live)
                {
                    BeginCreatorTestLive(live, _pipeName);
                    _pipeStage = 4;
                }
                else _pipeStage = 9;
                _pipeStageAt = now;
                return;
            }

            case 4:   // Test live: wait for the network half and the install
                if (_testLive is { Running: true }) return;
                if (now - _pipeStageAt > 240) { PipeCheck(false, "Test live timed out"); _pipeStage = 9; return; }
                if (_testLiveStatus.Length == 0) return;   // UpdateCreatorTestLive has not consumed it yet
                PipeCheck(!_testLiveStatus.StartsWith("Test live failed", StringComparison.Ordinal) &&
                          !_testLiveStatus.StartsWith("No MSUI", StringComparison.Ordinal), $"Test live: {_testLiveStatus}");
                PipeCheck(File.Exists(SpellTestRequestPath), "the live-client request file was written");
                PipeCheck(_mpq?.IsMounted(Net.SpellPatchClient.PatchName) == true, "patch-3 is mounted in this client");
                if (_pipeNewRow)
                {
                    InvalidateSpellRuleTables();
                    uint cast = SpellRuleTablesLoaded?.ExactRow("SpellCastTimes", _pipeCastMs) ?? 0;
                    uint radius = SpellRuleTablesLoaded?.ExactRow("SpellRadius", 7.5) ?? 0;
                    PipeCheck(cast >= 1000 && !SpellRuleTables.IsLocalRow(cast) && radius >= 1000 && !SpellRuleTables.IsLocalRow(radius),
                        $"patch-3 now carries the added rows (SpellCastTimes {cast}, SpellRadius {radius})");
                }
                _pipeStage = 9; _pipeStageAt = now;
                return;

            case 5:   // the Rules UI, on screen
                if (now - _pipeStageAt < 2.0) return;
                ArmGameplayDumpNamed("spellpipe-rules-ui");
                PipeCheck(SpellIdeWindowsVisible, "the Spell IDE windows are visible");
                PipeCheck(_spellIdeSelection.Kind == SpellIdeKind.Rules, "the Rules part is selected in the IDE");
                if (_pipePreview && _creatorSpell is { } previewDoc && previewDoc.Rules is { } previewRules)
                {
                    // The ground preview: a slow ball to the spot, impact there, rings on the stage.
                    previewRules.Set("speed", 3);
                    _sketchHold = false;
                    _creatorLoopPrecast = false; _creatorLoopCast = true; _creatorLoopMissilePhase = true;
                    _creatorLoopImpactPhase = true; _creatorLoopStateHold = false; _creatorLoopChannelHold = false;
                    _creatorLoopAreaHold = _pipeArea;
                    _creatorLoopPeriod = 8f; _creatorLoopNextAt = 0; _creatorLoopOn = true;
                    Settings.Creator.SpellGizmos = true;
                    _spellIdeHidden = true;   // the world, not the panels
                    PipeCheck(CreatorGroundTargeted(previewDoc), "the preview knows the spell aims at the ground");
                    _pipeStage = 6; _pipeShots = 0;
                }
                else if (_pipeTestLive && _creatorSpell is { } liveDoc)
                {
                    BeginCreatorTestLive(liveDoc, _pipeName);
                    _pipeStage = 4;
                }
                else _pipeStage = 9;
                _pipeStageAt = now;
                return;

            case 6:   // frames across the preview flight (launch at ~0 s, 15 yd at 3 yd/s = 5 s)
            {
                double[] at = { 1.0, 2.2, 3.4, 4.6, 5.6 };
                if (_pipeShots < at.Length && now - _pipeStageAt >= at[_pipeShots])
                {
                    ArmGameplayDumpNamed($"spellpipe-preview-{_pipeShots}");
                    _pipeShots++;
                }
                if (_pipeShots < at.Length) return;
                bool live = _spellEffects?.LiveInstances().Any(i => i.Stage is "MISSILE" or "IMPACT") == true;
                Console.WriteLine($"[spellpipe] preview instances: {string.Join(", ", _spellEffects?.LiveInstances().Select(i => i.Stage + ":" + Path.GetFileName(i.Path)) ?? [])}");
                if (Environment.GetEnvironmentVariable("MSUI_PIPE_TRAIL") is not null && _creatorSpell is { } trailDoc)
                {
                    string castPath = SpellVisualCatalog.ModelPath(SketchDoc.ModelPath((int)trailDoc.Info.Id, trailDoc.Info.Name, "cast"));
                    PipeCheck(_spellRibbons?.WasDrawn(castPath) == true, $"the ball's copied Charge trail was DRAWN by the ribbon renderer ({castPath})");
                }
                _pipeStage = 9; _pipeStageAt = now;
                return;
            }

            case 9:
                if (now - _pipeStageAt < 2.0) return;
                Console.WriteLine($"[spellpipe] DONE pass={_pipePass} fail={_pipeFail}");
                Console.Out.Flush();
                _quitRequested = true;
                _pipeStage = 10;
                return;
        }
    }

    private void ArmGameplayDumpNamed(string vantage)
    {
        _currentVantage = vantage;
        ArmGameplayDump();
    }
}
