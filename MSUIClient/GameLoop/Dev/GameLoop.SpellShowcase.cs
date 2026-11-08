using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell showcase: a spell built from nothing in the workshop, step by step, while
// someone watches (owner, 2026-11-03: "make a spell that doesn't really exist in any
// capacity, so I can see how it's done").
//
//   MSUI_SPELL_SHOWCASE=stormwing   build it in the open creator, pausing on each step,
//                                   then loop it on a dummy and leave the workshop open
//   MSUI_SPELL_SHOWCASE_CHECK=1     the same, then a frame burst across one cast and quit
//
// Every step is something an author does with the workshop's own controls - New spell,
// a recipe, rules, a stock kit's models in a phase, sketch pieces, sounds - called the way
// the controls call them, so the result is an ordinary design: it saves, reopens, exports
// and goes to Test live like any other.
//
// STORMWING: Chain Bolt's own precast in the caster's hands; a slow ball of lightning
// flanked by four crescent light-blades in an X-wing cross (round edge leading, horns
// trailing) with lightning leaping off it in flight; on impact lightning bursting out of the target
// in every direction, re-striking and dimming over 2 s (the Sketch's Flash dial, added for it).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private string? SpellShowcaseName =
        Environment.GetEnvironmentVariable("MSUI_SPELL_SHOWCASE") is { Length: > 0 } s ? s : null;
    private static readonly bool SpellShowcaseCheck =
        Environment.GetEnvironmentVariable("MSUI_SPELL_SHOWCASE_CHECK") is { Length: > 0 };
    /// <summary>MSUI_SPELL_SHOWCASE_SHIP=1: when the spell is built, press Test live (the web app
    /// builds it into the live server and patch-3) and quit once it reports.</summary>
    private static readonly bool SpellShowcaseShip =
        Environment.GetEnvironmentVariable("MSUI_SPELL_SHOWCASE_SHIP") is { Length: > 0 };

    private int _showcaseStep = -1;
    private double _showcaseAt;
    private int _showcaseShots;
    private readonly List<string> _showcaseLog = new();

    /// <summary>Seconds between steps when the showcase plays itself (the check and ship runs, or
    /// Auto-play ticked); otherwise each step waits for the viewer's Next.</summary>
    private static float ShowcasePace => SpellShowcaseCheck ? 0.6f : 6f;
    private bool _showcaseAuto = SpellShowcaseCheck || SpellShowcaseShip;
    private bool _showcaseNext;

    /// <summary>May the next step run? Auto: after the pace. Manual: when Next was pressed.</summary>
    private bool ShowcaseGo(double now)
    {
        if (_showcaseAuto) return now - _showcaseAt >= ShowcasePace;
        if (!_showcaseNext) return false;
        _showcaseNext = false;
        return true;
    }

    private static readonly Vector3 StormBlue = new(0.72f, 0.84f, 1f);

    private void ShowcaseSay(string line)
    {
        _showcaseLog.Add(line);
        Console.WriteLine($"[showcase] {line}");
        // The check run keeps one frame per step: the UI as the viewer sees it.
        if (SpellShowcaseCheck) ArmGameplayDumpNamed($"showcase-step-{_showcaseLog.Count:00}");
    }

    private void UpdateSpellShowcase()
    {
        if (SpellShowcaseName is null) return;
        double now = NowSeconds();
        switch (_showcaseStep)
        {
            case -1:
                if (_gl is null || _worldLoadStarted || now < 1.0) return;
                EnterOfflineWorld();
                _showcaseStep = 0; _showcaseAt = now;
                return;

            case 0:   // the world is up: open the workshop on a new spell
            {
                if (_worldLoading || !_creatorWorldRequested || _spellCatalog is null || _spellEffects is null) return;
                if (now - _showcaseAt < 2.0) return;
                StartNewCreatorSpell("Stormwing");
                if (_creatorSpell is not { } doc) { ShowcaseSay("FAIL: New spell made no document"); _showcaseStep = 99; return; }
                doc.DesignId = "stormwing01";
                doc.PlanStarted = true;
                // The viewer's own layout (sidebar or docked), whatever it is.
                Settings.Creator.SpellGizmos = false;
                _creatorPanel = CreatorPanel.Spells;
                _workspaceView = WorkspaceView.Root;
                _spellFocusSuppressed = false;
                _spellIdeHidden = false;
                _spellIdeInspectorHidden = false;
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Rules));
                ShowcaseSay("1. New spell \"Stormwing\" - from nothing: no source spell, no visual yet.");
                _showcaseStep = 1; _showcaseAt = now;
                return;
            }

            case 1:   // RULES: what it does
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc || CreatorRules(doc) is not { } rules || SpellRuleTablesLoaded is not { } tables) return;
                CreatorRecipe recipe = CreatorRecipes.First(r => r.Name == "Bolt");
                ApplyCreatorRecipe(doc, rules, recipe);
                rules.Set("school", 3);                                    // nature
                rules.SetPoints(1, 130, 150);
                rules.Set("speed", 4.5f);                                  // slow: a 24 yd cast flies ~5 s
                SetRulesIndexValue(rules, tables, "castingTimeIndex", 2000);
                SetRulesIndexValue(rules, tables, "rangeIndex", 30);
                ShowcaseSay("2. Rules: recipe \"Bolt\" (a missile at an enemy, damage on hit) - nature, " +
                            "130-150, 2.0 s cast, 30 yd, missile speed 4.5 yd/s (slow).");
                _showcaseStep = 2; _showcaseAt = now;
                return;
            }

            case 2:   // PRECAST: Chain Bolt's own, as the owner picked it
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc) return;
                doc.PlannedStages.Add(SpellStage.Precast);
                doc.PlannedStages.Add(SpellStage.Cast);
                string what = "(Chain Bolt not found)";
                if (_spellCatalog!.TryGet(8292, out SpellInfo chainBolt) &&
                    _spellVisualCatalog?.TryGetStages(chainBolt.VisualId, out SpellVisualStages cb) == true &&
                    _spellVisualCatalog.TryGetKit(cb.Precast, out SpellVisualKitInfo precast))
                {
                    var used = new List<string>();
                    foreach (SpellVisualKitEffect effect in precast.Effects)
                    {
                        int slot = Array.IndexOf(SpellVisualCatalog.KitAttachmentIds, effect.AttachmentId);
                        if (slot < 0) continue;
                        SetCreatorSlot(doc, SpellStage.Precast, slot, effect.ModelPath);
                        used.Add($"{Path.GetFileNameWithoutExtension(effect.ModelPath)} on {CreatorSlotNames[slot]}");
                    }
                    if (doc.Composition.TryGetValue(SpellStage.Precast, out CreatorStageComposition? phase))
                        phase.AnimationId = precast.AnimationId;
                    if (precast.Sound is uint sound) SetCreatorPickedSound(doc, CreatorAudioCue.Precast, sound);
                    what = string.Join(", ", used);
                }
                if (doc.Composition.TryGetValue(SpellStage.Cast, out CreatorStageComposition? cast))
                    cast.AnimationId = ShowcaseAnimation("SpellCastOmni");
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Composition));
                ShowcaseSay($"3. Precast: Chain Bolt (8292)'s own kit - {what}, its animation and hum. " +
                            "Cast: the release (SpellCastOmni).");
                _showcaseStep = 3; _showcaseAt = now;
                return;
            }

            case 3:   // MISSILE: the ball
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc) return;
                SketchDoc sketch = SketchForStage(doc, SpellStage.Cast);
                sketch.Pieces.Clear();
                SketchPiece core = sketch.AddPiece(SketchShapeKind.Sphere);
                core.Name = "ball";
                core.Shape.Width = core.Shape.Height = 0.7f;
                core.Look.Image = @"Spells\LightningBall.blp";
                core.Look.UvScroll = new Vector2(0.6f, 0.3f);
                core.Look.Colour = new Vector3(0.85f, 0.9f, 1f);
                core.Extras.Glow.Radius = 0.3f;
                core.Place.Origin = Vector3.Zero;   // a missile is centred ON its flight line
                core.Motion.Mode = SketchTravelMode.ToTarget;
                core.Motion.Distance = 0f;
                core.Motion.Speed = 4.5f;
                SketchPiece shell = sketch.AddPiece(SketchShapeKind.Sphere);
                shell.Name = "shell";
                shell.Shape.Width = shell.Shape.Height = 0.95f;
                shell.Look.Image = @"Spells\Lightning4.blp";
                shell.Look.UvScroll = new Vector2(-1f, 0.5f);
                shell.Look.Colour = new Vector3(0.6f, 0.7f, 1f);
                shell.Look.Alpha = 0.75f;
                shell.Place.Origin = Vector3.Zero;
                shell.Motion.Mode = SketchTravelMode.ToTarget;
                shell.Motion.Distance = 0f;
                _sketchStage = SpellStage.Cast;
                _sketchOpen = true;
                ShowcaseSay("4. Missile, drawn in the Sketch: a SPHERE wearing the game's lightning-ball " +
                            "image, scrolling, inside a crackling Lightning4 shell.");
                _showcaseStep = 4; _showcaseAt = now;
                return;
            }

            case 4:   // MISSILE: the four crescent blades
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc) return;
                SketchDoc sketch = SketchForStage(doc, SpellStage.Cast);
                // X-wing: four blades on the diagonals round the flight axis. Each is a crescent in
                // the plane that holds the flight line and its diagonal (VerticalForward, rolled
                // about forward), pushed out along that diagonal by its Offset (which turns with
                // it). The drawn crescent bulges BACKWARD ("(" facing -X), so MirrorX turns it
                // round: the smooth half-moon leads, the two horns trail.
                // They CIRCLE the sphere in flight: all four ride an invisible, UNTURNED hub at the
                // ball's centre (Place.Parent) that spins about its forward axis - the flight line -
                // so each blade keeps its fin pose and the cross turns like rifling. (A hub stood
                // side-on to make its face normal point forward passed that sideways turn on to the
                // blades: they tumbled.) One full turn a second against the missile's 1.0 s loop.
                SketchPiece hub = sketch.AddPiece(SketchShapeKind.Disc);
                hub.Name = "hub";
                hub.Shape.Width = hub.Shape.Height = 0.05f;
                hub.Look.Alpha = 0f;
                hub.Look.Glow = 0f;
                hub.Place.Origin = Vector3.Zero;
                hub.Place.Facing = SketchFacing.VerticalForward;
                hub.Motion.Mode = SketchTravelMode.ToTarget;
                hub.Motion.Distance = 0f;
                hub.Motion.FadeOut = 0f;
                hub.Motion.SpinDegreesPerSecond = 360f;
                hub.Motion.SpinAboutForward = true;
                for (int k = 0; k < 4; k++)
                {
                    SketchPiece blade = sketch.AddPiece(SketchShapeKind.Crescent);
                    blade.Name = $"blade{k + 1}";
                    blade.Shape.Width = 0.65f;
                    blade.Shape.Height = 1.0f;
                    blade.Shape.Thickness = 0.42f;
                    blade.Shape.Softness = 0.2f;
                    blade.Look.Colour = new Vector3(0.45f, 0.68f, 1f);   // light, but blue: white reads as paper
                    blade.Look.Glow = 0.7f;
                    blade.Place.Origin = Vector3.Zero;
                    blade.Place.Facing = SketchFacing.Custom;
                    blade.Place.CustomEuler = new Vector3(45f + 90f * k, 0f, 0f);
                    blade.Place.MirrorX = true;
                    blade.Place.Parent = "hub";
                    blade.Place.Offset = new Vector3(-0.1f, 0f, 0.75f);
                    blade.Motion.Mode = SketchTravelMode.ToTarget;
                    blade.Motion.Distance = 0f;
                    // A faint shimmer, not a flicker: the blades are solid light.
                    blade.Motion.JitterYards = 0.02f;
                    blade.Motion.JitterRate = 18f;
                    blade.Motion.JitterSmooth = true;
                    blade.Motion.JitterSeed = k;
                }
                // Lightning leaping OFF the ball in flight, every direction: bolts anchored at the
                // ball's centre and reaching out (the Offset puts the bolt's near end at the centre,
                // and turns with it), each re-aimed at a random new direction every strike (stepped
                // jitter through 180 degrees = anywhere) and FLASHING, out of step with the others.
                var arcRng = new Random(3);
                for (int a = 0; a < 7; a++)
                {
                    SketchPiece arc = sketch.AddPiece(SketchShapeKind.Beam);
                    arc.Name = $"leap{a + 1}";
                    float reach = 1.3f + (float)arcRng.NextDouble() * 0.8f;
                    arc.Shape.Width = reach;
                    arc.Shape.Height = 0.5f;
                    arc.Look.Image = @"Spells\Lightning3.blp";
                    arc.Look.Colour = StormBlue;
                    arc.Place.Origin = Vector3.Zero;
                    arc.Place.Facing = SketchFacing.Custom;
                    arc.Place.CustomEuler = new Vector3(0f, -60f + 20f * a, 51f * a);
                    arc.Place.Offset = new Vector3(reach * 0.5f, 0f, 0f);
                    arc.Motion.Mode = SketchTravelMode.ToTarget;
                    arc.Motion.Distance = 0f;
                    arc.Motion.Duration = 1f;
                    arc.Motion.StartAt = 0f;   // the flight loop is exactly 1.0 s (the blades' turn)
                    arc.Motion.FlashEvery = 0.08f + (float)arcRng.NextDouble() * 0.07f;
                    arc.Motion.FlashOn = 0.045f;
                    arc.Motion.JitterDegrees = 180f;
                    arc.Motion.JitterRate = 1f / arc.Motion.FlashEvery;
                    arc.Motion.JitterSeed = 60 + a;
                }
                _sketchSlot = 0;
                CompileSketch(doc, sketch);
                string path = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, "cast");
                bool isMissile = string.Equals(doc.MissileOverride, path, StringComparison.OrdinalIgnoreCase);
                SetCreatorPickedSound(doc, CreatorAudioCue.Missile, 3014);
                ShowcaseSay("5. Four CRESCENT light-blades in an X-wing cross round the ball: rolled 45/135/225/315 " +
                            $"degrees about the flight line, pushed out 0.75 yd, mirrored so the round edge leads - all riding " +
                            "a hidden hub that spins once a second, so they CIRCLE the sphere as it flies. " +
                            "And lightning leaping off it in flight: 7 bolts from its centre, each FLASHING and re-aimed " +
                            "at a random direction every strike. " +
                            $"Compiled: {(isMissile ? "it IS the spell's missile" : "NOT the missile?")}; flight loop sound 3014.");
                _showcaseStep = 5; _showcaseAt = now;
                return;
            }

            case 5:   // IMPACT: lightning in every direction
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc) return;
                doc.PlannedStages.Add(SpellStage.Impact);
                SketchDoc burst = SketchForStage(doc, SpellStage.Impact);
                burst.Pieces.Clear();
                // Lightning bursting out of the target in EVERY direction: bolts from its centre
                // (anchored by the Offset, like the missile's), spread evenly over a sphere, each
                // FLASHING out of step and re-aimed a little every strike, longer and shorter ones
                // mixed, all dimming out over 2 seconds. No particles: it is all lightning.
                const float center = 1.1f;
                const int bolts = 18;
                var burstRng = new Random(11);
                float golden = MathF.PI * (3f - MathF.Sqrt(5f));
                for (int k = 0; k < bolts; k++)
                {
                    float z = 1f - 2f * (k + 0.5f) / bolts;                 // even spread over the sphere
                    float r = MathF.Sqrt(1f - z * z);
                    var dir = new Vector3(MathF.Cos(golden * k) * r, MathF.Sin(golden * k) * r, z);
                    float reach = (k % 3 == 0 ? 2.6f : 1.7f) + (float)burstRng.NextDouble() * 0.6f;
                    SketchPiece bolt = burst.AddPiece(SketchShapeKind.Beam);
                    bolt.Name = $"burst{k + 1}";
                    bolt.Shape.Width = reach;
                    bolt.Shape.Height = 0.7f;
                    bolt.Look.Image = @"Spells\Lightning3.blp";
                    bolt.Look.Colour = StormBlue;
                    bolt.Place.Origin = new Vector3(0f, 0f, center);
                    bolt.Place.Facing = SketchFacing.Custom;
                    bolt.Place.CustomEuler = ShowcaseEulerFor(dir);
                    bolt.Place.Offset = new Vector3(reach * 0.5f, 0f, 0f);
                    bolt.Motion.Duration = 2f;
                    bolt.Motion.StartAt = (float)burstRng.NextDouble() * 0.08f;
                    bolt.Motion.FadeOut = 1.5f;
                    bolt.Motion.FlashEvery = 0.07f + (float)burstRng.NextDouble() * 0.07f;
                    bolt.Motion.FlashOn = 0.04f + (float)burstRng.NextDouble() * 0.02f;
                    bolt.Motion.JitterDegrees = 30f;
                    bolt.Motion.JitterRate = 1f / bolt.Motion.FlashEvery;
                    bolt.Motion.JitterSeed = 300 + k;
                }
                // The heart of it: a bright core flaring with the strikes.
                SketchPiece heart = burst.AddPiece(SketchShapeKind.Disc);
                heart.Name = "heart";
                heart.Shape.Width = heart.Shape.Height = 0.6f;
                heart.Look.Colour = new Vector3(0.8f, 0.9f, 1f);
                heart.Look.Glow = 0.8f;
                heart.Place.Origin = new Vector3(0f, 0f, center);
                heart.Place.Facing = SketchFacing.FaceCamera;
                heart.Motion.Duration = 2f;
                heart.Motion.FadeOut = 1.6f;
                heart.Motion.FlashEvery = 0.09f;
                heart.Motion.FlashOn = 0.06f;
                _sketchStage = SpellStage.Impact;
                _sketchSlot = 2;
                CompileSketch(doc, burst);
                SetCreatorPickedSound(doc, CreatorAudioCue.Impact, 4936);
                if (doc.Composition.TryGetValue(SpellStage.Impact, out CreatorStageComposition? impact))
                    impact.AnimationId = ShowcaseAnimation("CombatWound");
                ShowcaseSay("6. Impact, its own Sketch: lightning bursting out of the target in every direction - 18 bolts " +
                            "from its centre spread over a sphere, each FLASHING (the new Flash dial) and re-aimed every " +
                            "strike, round a flaring core - dimming out over 2 seconds. Landing sound 4936.");
                _showcaseStep = 6; _showcaseAt = now;
                return;
            }

            case 6:   // put a target in front and play the whole spell on a loop
            {
                if (!ShowcaseGo(now)) return;
                if (_creatorSpell is not { } doc) return;
                if (_creatorSpawns.Count == 0) SpawnCreatorCreature("Kobold Worker", 10912, 0f);
                // Done sketching - what closing the Sketch window does: Hold off (it soloed the
                // phase being drawn and compiled a LOOPING preview of it), and both sketches
                // recompiled as the one-shots the spell really plays.
                _sketchHold = false;
                _sketchOpen = false;
                _sketchStage = SpellStage.Cast; _sketchSlot = 0;
                CompileSketch(doc, SketchForStage(doc, SpellStage.Cast));
                _sketchStage = SpellStage.Impact; _sketchSlot = 2;
                CompileSketch(doc, SketchForStage(doc, SpellStage.Impact));
                Settings.Creator.SpellGizmos = false;
                Settings.Creator.SpellGrid = false;   // watch the spell, not the yard grid
                _creatorLoopPrecast = true; _creatorLoopCast = true; _creatorLoopMissilePhase = true;
                _creatorLoopImpactPhase = true; _creatorLoopStateHold = false; _creatorLoopChannelHold = false;
                _creatorLoopAreaHold = false;
                _creatorLoopPeriod = 7f; _creatorLoopNextAt = 0; _creatorLoopOn = true;
                AddCreatorSpellToSession(doc, "Stormwing");
                ShowcaseSay("7. Sketch closed (Hold off, one-shot models), a Kobold Worker to hit, and the whole spell on a loop: precast, cast, the slow X-wing ball, " +
                            "the X on the kobold. Saved to the session (it reopens and goes to Test live as it is).");
                _showcaseStep = SpellShowcaseCheck ? 7 : SpellShowcaseShip ? 60 : 50;
                _showcaseAt = now; _showcaseShots = 0;
                return;
            }

            case 7:   // CHECK: frames across one cast, then quit
            {
                if (_showcaseShots == 0 && now - _showcaseAt < 1.0) return;
                if (now - _showcaseAt < 0.45) return;
                if (_showcaseShots >= 32) { _showcaseStep = 99; _showcaseAt = now; return; }
                if (_showcaseShots == 0) { _spellIdeHidden = true; _creatorLoopNextAt = 0; }
                if (_showcaseShots == 16 && _window?.Camera is { } side)
                {
                    side.OrbitYaw += MathF.PI * 0.5f;   // from the side: the crescents' shape
                    _creatorLoopNextAt = 0;
                }
                ArmGameplayDumpNamed($"showcase-{_showcaseShots:00}");
                _showcaseShots++;
                _showcaseAt = now;
                return;
            }

            case 60:   // SHIP: Test live, as the button does
            {
                if (now - _showcaseAt < 1.0) return;
                if (_creatorSpell is not { } doc) return;
                BeginCreatorTestLive(doc, "Stormwing");
                ShowcaseSay("8. Test live: the web app builds Stormwing into the live server and patch-3.");
                _showcaseStep = 61; _showcaseAt = now;
                return;
            }

            case 61:
            {
                if (_testLive is { Running: true } || _testLiveStatus.Length == 0)
                {
                    if (now - _showcaseAt > 180) { Console.WriteLine("[showcase] Test live timed out"); _showcaseStep = 99; }
                    return;
                }
                Console.WriteLine($"[showcase] SHIPPED {_testLiveStatus}");
                _showcaseStep = 99; _showcaseAt = now;
                return;
            }

            case 99:
                if (now - _showcaseAt < 2.0) return;
                Console.WriteLine("[showcase] DONE");
                Console.Out.Flush();
                _quitRequested = true;
                _showcaseStep = 100;
                return;
        }
    }

    private ushort? ShowcaseAnimation(string name)
    {
        for (int id = 0; id < 512; id++)
            if (_animationData?.TryGetName(id, out string found) == true &&
                string.Equals(found, name, StringComparison.OrdinalIgnoreCase)) return (ushort)id;
        return null;
    }

    /// <summary>The Custom facing (degrees, the file's own Euler convention) that points a
    /// Beam's long axis (raw +X) along <paramref name=direction/>. It aims for the HALF TURN
    /// about the bisector of +X and the direction: a half turn is its own inverse, so the beam
    /// lands on the direction whichever way round a renderer applies the bone's quaternion (a
    /// plain search got the X's strokes pointing at the camera - invisible).</summary>
    private static Vector3 ShowcaseEulerFor(Vector3 direction)
    {
        Vector3 best = Vector3.Zero;
        float bestScore = float.MinValue;
        for (int roll = -180; roll < 180; roll += 5)
            for (int pitch = -90; pitch <= 90; pitch += 5)
                for (int yaw = -180; yaw < 180; yaw += 5)
                {
                    Vector4 v = M2BoneParser.EulerToQuaternion(new Vector3(roll, pitch, yaw));
                    var q = new Quaternion(v.X, v.Y, v.Z, v.W);
                    float score = MathF.Min(Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), direction),
                                            Vector3.Dot(Vector3.Transform(Vector3.UnitX, Quaternion.Conjugate(q)), direction));
                    if (score > bestScore + 1e-5f) { bestScore = score; best = new Vector3(roll, pitch, yaw); }
                }
        Console.WriteLine($"[showcase] facing for {direction}: euler {best} (both conventions within {MathF.Acos(Math.Clamp(bestScore, -1f, 1f)) * 180f / MathF.PI:0.#} deg)");
        return best;
    }

    /// <summary>The step list, top centre, while the showcase runs.</summary>
    private void DrawSpellShowcase()
    {
        if (SpellShowcaseName is null || _showcaseLog.Count == 0) return;
        if (SpellShowcaseCheck && _showcaseStep >= 7) return;   // clean frames
        var io = ImGui.GetIO();
        float cs = CreatorUiScale;
        // Between the sidebar (left) and the Sketch window (right), under the top bar.
        float left = MathF.Max(_creatorSidebarRight, 0f) + 12f;
        float right = io.DisplaySize.X - (_sketchOpen ? Math.Clamp(560f * cs, 420f, io.DisplaySize.X * 0.62f) + 16f : 8f);
        float width = Math.Clamp(right - left - 12f, 320f, 640f * cs);
        float top = SpellFocusActive ? _spellIdeStripHeight + 8f : CreatorSidebarTop;
        // Fixed width, height follows the text (the creator's own window look, like the Sketch).
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, io.DisplaySize.Y - top - 16f));
        if (BeginSpellIdeWindow("##showcase", new Vector2(left + MathF.Max(right - left - width, 0f) * 0.5f, top),
                Vector2.Zero, ImGuiCond.Always, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize, 0.25f))
        {
            ImGui.TextColored(SpellIdeGold, "BUILDING STORMWING");
            ImGui.SameLine();
            ImGui.TextDisabled($"step {Math.Min(_showcaseLog.Count, 8)} of 8");
            ImGui.PushTextWrapPos(0f);
            // The step just done in full; the earlier ones as one line each, so the window stays short.
            for (int i = 0; i < _showcaseLog.Count - 1; i++)
            {
                string line = _showcaseLog[i];
                int cut = line.IndexOf(" - ", StringComparison.Ordinal);
                ImGui.TextDisabled(cut > 0 && cut < 90 ? line[..cut] : line.Length > 90 ? line[..90] + "..." : line);
            }
            ImGui.Separator();
            ImGui.TextUnformatted(_showcaseLog[^1]);
            ImGui.PopTextWrapPos();

            bool waiting = _showcaseStep is >= 1 and <= 6;
            ImGui.Spacing();
            if (!waiting) ImGui.BeginDisabled();
            if (ImGui.Button(_showcaseAuto ? "Playing..." : "Next step >")) _showcaseNext = true;
            if (!waiting) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.Checkbox("Auto-play", ref _showcaseAuto);
            if (_showcaseStep >= 50)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("done - it loops on the kobold; edit anything, it is an ordinary design.");
            }
        }
        EndSpellIdeWindow();
    }
}
