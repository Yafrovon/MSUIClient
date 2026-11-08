using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// SPELL LESSONS — learn the Spell Workshop by building (owner, 2026-11-03: "we really need a
// tutorial that shows how to make different types of spells, so that someone can basically
// follow along, like free handing a sketch while looking at it").
//
// A lesson is a list of steps. Each step says what to do in words, puts a pulsing gold outline
// round the control to use (LessonMark / LessonMarked, called right after that widget), and
// watches the design: when the thing is done - by hand - the step ticks and the lesson moves on
// by itself. "Show me" does the step for you, through the same calls the controls make, so a
// stuck learner is never stuck. Nothing here is a separate mode: the spell being built is an
// ordinary design that saves, reopens and goes to Test live.
//
// Probe: MSUI_SPELL_LESSON=<1-based lesson> MSUI_SPELL_LESSON_AUTO=1 plays the lesson through
// with Show me on every step that is not done within a second, keeps one frame per step and
// quits; every step must end DONE ("[lesson] ... FAIL" otherwise).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>One step. <paramref name="Done"/> null = a reading step (Next moves on).</summary>
    private sealed record LessonStep(string Title, string Do, string? Mark = null, Func<bool>? Done = null,
        Action? ShowMe = null, Action? Enter = null, string? Where = null);

    private sealed record Lesson(string Name, string Kind, string Blurb, Func<LessonStep[]> Build);

    private bool _lessonsOpen;
    private Lesson? _lesson;
    private LessonStep[] _lessonSteps = Array.Empty<LessonStep>();
    private int _lessonStep;
    private double _lessonDoneAt;
    private int _lessonMarkSeenFrame = -10;
    private bool _lessonMarkHidden;
    private int _lessonScrolledStep = -1;
    private CreatorSpellDoc? _lessonStepDoc;
    private string _creatorLastRecipe = "";

    private static readonly int LessonAutoIndex =
        int.TryParse(Environment.GetEnvironmentVariable("MSUI_SPELL_LESSON"), out int n) ? n : 0;
    private static readonly bool LessonAuto =
        Environment.GetEnvironmentVariable("MSUI_SPELL_LESSON_AUTO") is { Length: > 0 };
    private int _lessonAutoPhase = -1;
    private double _lessonAutoAt;
    private int _lessonAutoFailures;
    private string _lessonAutoMark = "-";

    private LessonStep? LessonCurrent =>
        _lesson is not null && _lessonStep >= 0 && _lessonStep < _lessonSteps.Length ? _lessonSteps[_lessonStep] : null;

    // ── the outline ─────────────────────────────────────────────────────────

    /// <summary>Call right after a widget: if the current step points at <paramref name="key"/>,
    /// outline that widget in pulsing gold.</summary>
    private void LessonMark(string key)
    {
        if (LessonCurrent is not { Mark: { } mark } || !string.Equals(mark, key, StringComparison.Ordinal)) return;
        if (!ImGui.IsItemVisible())
        {
            // Scrolled out of its panel: bring it into view once per step (then leave the
            // scrolling to the learner).
            _lessonMarkHidden = true;
            _lessonMarkSeenFrame = ImGui.GetFrameCount();
            if (_lessonScrolledStep != _lessonStep)
            {
                _lessonScrolledStep = _lessonStep;
                ImGui.SetScrollFromPosY(ImGui.GetItemRectMin().Y - ImGui.GetWindowPos().Y, 0.35f);
            }
            return;
        }
        _lessonMarkHidden = false;
        _lessonMarkSeenFrame = ImGui.GetFrameCount();
        float pad = 4f * CreatorUiScale;
        Vector2 min = ImGui.GetItemRectMin() - new Vector2(pad), max = ImGui.GetItemRectMax() + new Vector2(pad);
        float pulse = 0.55f + 0.45f * MathF.Sin((float)NowSeconds() * 6f);
        var dl = ImGui.GetForegroundDrawList();
        dl.AddRect(min, max, AtlasU(AtlasGold, pulse), 5f, ImDrawFlags.None, 3f * CreatorUiScale);
        dl.AddRect(min - new Vector2(3f), max + new Vector2(3f), AtlasU(AtlasGold, pulse * 0.35f), 7f, ImDrawFlags.None, 2f);
    }

    /// <summary><see cref="LessonMark"/> as a pass-through, so a control is marked in place:
    /// <c>if (LessonMarked("move.travel", ImGui.Combo(...)))</c>. The widget is drawn first (it is
    /// the argument), so the outline lands on it.</summary>
    private bool LessonMarked(string key, bool result)
    {
        LessonMark(key);
        return result;
    }

    // ── helpers the steps share ─────────────────────────────────────────────

    private SketchPiece? LessonPiece(SpellStage stage, SketchShapeKind? kind = null)
    {
        if (_creatorSpell is not { } doc) return null;
        SketchDoc sketch = SketchForStage(doc, stage);
        if (kind is { } k) return sketch.Pieces.LastOrDefault(p => p.Shape.Kind == k);
        if (_sketchStage == stage && _sketchSelected >= 0 && _sketchSelected < sketch.Pieces.Count)
            return sketch.Pieces[_sketchSelected];
        return sketch.Pieces.LastOrDefault();
    }

    /// <summary>Open the part of the Spell Workshop a step needs: the IDE inspector in the docked
    /// spell layout, the sidebar section otherwise.</summary>
    private void LessonReveal(SpellIdeKind kind, string sectionId)
    {
        _creatorPanel = CreatorPanel.Spells;
        if (SpellFocusActive) SpellIdeSelect(new SpellIdeSelection(kind));
        else Settings.Creator.SectionOpen[$"Spells/{sectionId}"] = true;
    }

    private void LessonEdit(SketchPiece? piece, Action<SketchPiece> change)
    {
        if (piece is null) return;
        change(piece);
        SketchChanged();
    }

    private void LessonAddShape(SpellStage stage, SketchShapeKind kind)
    {
        if (_creatorSpell is not { } doc) return;
        if (_sketchStage != stage) SketchEditStage(stage);
        SketchDoc sketch = SketchForStage(doc, stage);
        sketch.AddPiece(kind);
        SelectSketchPiece(doc, sketch, sketch.Pieces.Count - 1);
        SketchChanged();
    }

    private void LessonRecipe(string name)
    {
        if (_creatorSpell is not { } doc || CreatorRules(doc) is not { } rules) return;
        ApplyCreatorRecipe(doc, rules, CreatorRecipes.First(r => r.Name == name));
    }

    /// <summary>Play the whole spell: every planned phase ticked, Hold off, the loop running.</summary>
    private void LessonPlayAll()
    {
        if (_creatorSpell is not { } doc) return;
        _sketchHold = false;
        _creatorLoopPrecast = doc.PlannedStages.Contains(SpellStage.Precast);
        _creatorLoopCast = doc.PlannedStages.Contains(SpellStage.Cast);
        _creatorLoopMissilePhase = doc.PlannedMissile;
        _creatorLoopImpactPhase = doc.PlannedStages.Contains(SpellStage.Impact);
        _creatorLoopStateHold = doc.PlannedStages.Contains(SpellStage.State);
        _creatorLoopChannelHold = doc.PlannedStages.Contains(SpellStage.Channel);
        if (!_creatorLoopOn) ToggleCreatorLoop();
        _creatorLoopNextAt = 0;
    }

    /// <summary>Every part that has something drawn in it is ticked, Hold off, the loop on. (An
    /// empty planned part does not count: the loop rightly drops a phase with nothing in it.)</summary>
    private bool LessonPlayingAll()
    {
        if (_creatorSpell is not { } doc || !_creatorLoopOn || _sketchHold) return false;
        // Missile pieces (Travel "To the target") play as the missile, not the cast.
        bool Drawn(SpellStage stage) => SketchForStage(doc, stage).Pieces.Any(p => p.Motion.Mode != SketchTravelMode.ToTarget);
        bool flies = SketchForStage(doc, SpellStage.Cast).Pieces.Any(p => p.Motion.Mode == SketchTravelMode.ToTarget);
        return (!Drawn(SpellStage.Impact) || _creatorLoopImpactPhase) &&
               (!Drawn(SpellStage.State) || _creatorLoopStateHold) &&
               (!Drawn(SpellStage.Cast) || _creatorLoopCast) &&
               (!flies || _creatorLoopMissilePhase);
    }

    private LessonStep LessonNewSpell(string name) => new(
        "Start a new spell",
        $"In the Spell section, type a name in the name box - \"{name}\" will do - and press New spell. " +
        "A new spell starts from nothing: no art, no rules, an empty anatomy.",
        Mark: "spell.new",
        Done: () => _creatorSpell is { } doc && !ReferenceEquals(doc, _lessonStepDoc) && IsCreatorNewSpellId(doc.Info.Id),
        ShowMe: () => StartNewCreatorSpell(name),
        Enter: () => { _lessonStepDoc = _creatorSpell; LessonReveal(SpellIdeKind.Spell, "ws-spell"); },
        Where: "Spell Workshop > Spell section (top of the panel)");

    private LessonStep LessonPart(SpellStage stage, string why) => new(
        $"Work on the {stage.ToString().ToLowerInvariant()}",
        $"In the Sketch window's ANATOMY, click {stage}. {why}",
        Mark: $"part.{stage}",
        Done: () => _sketchStage == stage,
        ShowMe: () => SketchEditStage(stage),
        Enter: () => { _sketchOpen = true; _sketchAnatomyFolded = false; },
        Where: "Sketch window (right) > ANATOMY");

    private LessonStep LessonShape(SpellStage stage, SketchShapeKind kind, string name, string why) => new(
        $"Add a {name.ToLowerInvariant()}",
        $"Click {name} on the Sketch window's shape shelf. {why}",
        Mark: $"shape.{name}",
        Done: () => LessonPiece(stage, kind) is not null,
        ShowMe: () => LessonAddShape(stage, kind),
        Enter: () => { _sketchOpen = true; if (_sketchStage != stage) SketchEditStage(stage); },
        Where: "Sketch window > the row of shape buttons");

    private LessonStep LessonPlay(string say) => new(
        "Watch it",
        say + " In the Sketch, untick Hold if it is ticked (Hold plays only the part you are " +
        "drawing). Then press Loop in the Loop section: every part plays, over and over, and " +
        "any change you make shows up on the next pass.",
        Mark: "loop.toggle",
        Done: LessonPlayingAll,
        ShowMe: LessonPlayAll,
        Enter: () => LessonReveal(SpellIdeKind.Loop, "ws-loop"),
        Where: "Spell Workshop > Loop section");

    // ── the lessons ─────────────────────────────────────────────────────────

    private Lesson[]? _lessons;
    private Lesson[] Lessons => _lessons ??= new[]
    {
        new Lesson("A bolt", "projectile",
            "Fireball's shape: a glowing ball flies from your hands to the target and bursts on it. " +
            "Rules from a recipe, a sphere that travels, sparks, a ring on impact.",
            LessonBolt),
        new Lesson("Draw your own", "free-hand",
            "Draw a shape by hand on the paper, keep it, then make it spin, rise and fade. " +
            "Anything you can draw can be a piece of a spell.",
            LessonFreehand),
        new Lesson("Moving things", "travel in space",
            "How pieces move: fly a set distance in a direction, ease out, spin, orbit, ride on " +
            "another piece, crackle. The MOVE card, one dial at a time.",
            LessonMoving),
        new Lesson("A ward on yourself", "buff / aura",
            "A buff: a glowing ring turns at your feet for as long as the aura lasts, and your " +
            "body takes a colour wash. The State part and the Body dials.",
            LessonWard),
    };

    private LessonStep[] LessonBolt() => new[]
    {
        LessonNewSpell("Ember Bolt"),
        new LessonStep("Give it rules",
            "Open the Rules section and click the recipe Bolt under START FROM A REAL SPELL. It copies " +
            "Fireball's rules - a missile at an enemy, damage on hit - and plans the parts: precast, " +
            "cast, a missile, impact.",
            Mark: "recipe.Bolt",
            Done: () => _creatorLastRecipe == "Bolt" && _creatorSpell is { PlannedMissile: true },
            ShowMe: () => LessonRecipe("Bolt"),
            Enter: () => { _creatorLastRecipe = ""; LessonReveal(SpellIdeKind.Rules, "ws-rules"); },
            Where: "Spell Workshop > Rules section"),
        LessonPart(SpellStage.Cast, "The missile lives in the cast part: any piece there whose Travel " +
                                    "is \"To the target\" is what flies."),
        LessonShape(SpellStage.Cast, SketchShapeKind.Sphere, "Sphere",
            "A real 3D ball appears at your chest, white, one yard across."),
        new LessonStep("Colour it",
            "In the LOOK card, click the Colour swatch and pick a fiery orange.",
            Mark: "look.colour",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere) is { } p && p.Look.Colour != Vector3.One,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere), p => p.Look.Colour = new Vector3(1f, 0.5f, 0.15f)),
            Where: "Sketch window > LOOK card"),
        new LessonStep("Make it glow",
            "Drag Glow up to 0.8 or more: a soft halo round the ball.",
            Mark: "look.glow",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere) is { Look.Glow: >= 0.75f },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere), p => p.Look.Glow = 0.85f),
            Where: "Sketch window > LOOK card"),
        new LessonStep("Make it fly",
            "In the MOVE card, set Travel to \"To the target\". That one setting makes it the missile: " +
            "the game flies it from your hands to whatever you cast at, at the spell's speed.",
            Mark: "move.travel",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere) is { Motion.Mode: SketchTravelMode.ToTarget },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere), p => p.Motion.Mode = SketchTravelMode.ToTarget),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Pick its speed",
            "The yd/s box under Travel is how fast it flies (it is the spell's own speed). Try 12 - " +
            "slow enough to watch. Press Next when you are happy.",
            Mark: "move.speed",
            Where: "Sketch window > MOVE card, under Travel"),
        new LessonStep("Sparks",
            "Scroll the cards down to EXTRAS and set Sparks to Embers: the ball sheds glowing bits as it flies.",
            Mark: "extras.sparks",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere) is { } p && p.Extras.Sparks.Preset != SketchSparkPreset.None,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Sphere), p => p.Extras.Sparks.Preset = SketchSparkPreset.Embers),
            Where: "Sketch window > EXTRAS card (bottom of the cards)"),
        new LessonStep("Something to hit",
            "A missile needs a target. Top bar > Target, spawn any creature - or press Show me for a kobold.",
            Done: () => _creatorSpawns.Count > 0,
            ShowMe: () => SpawnCreatorCreature("Kobold Worker", 10912, 0f),
            Where: "Top bar > Target"),
        LessonPart(SpellStage.Impact, "This part plays on the target when the missile arrives."),
        LessonShape(SpellStage.Impact, SketchShapeKind.Ring, "Ring", "A ring lies flat on the ground under the target."),
        new LessonStep("Let it burst",
            "In MOVE, drag Grow's second number to 3 (the ring swells to three times its size) and " +
            "Fade in/out's second number to 0.5 (it fades away as it swells).",
            Mark: "move.grow",
            Done: () => LessonPiece(SpellStage.Impact, SketchShapeKind.Ring) is { Motion.GrowTo: >= 2f, Motion.FadeOut: >= 0.4f },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Impact, SketchShapeKind.Ring), p =>
            {
                p.Motion.GrowTo = 3f; p.Motion.FadeOut = 0.5f; p.Motion.Duration = 0.6f;
                p.Look.Colour = new Vector3(1f, 0.55f, 0.2f);
            }),
            Where: "Sketch window > MOVE card"),
        LessonPlay("Your bolt is built."),
        new LessonStep("That is a bolt",
            "Every part you just used works the same in any spell. Session saves it; Test live " +
            "puts it on your live character. Next lesson: drawing your own shapes."),
    };

    private LessonStep[] LessonFreehand() => new[]
    {
        LessonNewSpell("Scribble"),
        new LessonStep("Say what kind of spell",
            "The Sketch window asks \"What kind of spell is this?\". Click Instant, no travel - a " +
            "cast and an impact, nothing flies.",
            Mark: "plan.Instant, no travel",
            Done: () => _creatorSpell is { PlanStarted: true },
            ShowMe: () => { if (_creatorSpell is { } doc) ChooseSketchPlan(doc, 2); },
            Enter: () => { _sketchOpen = true; _sketchAnatomyFolded = false; },
            Where: "Sketch window > ANATOMY"),
        LessonPart(SpellStage.Cast, "You will draw into the cast."),
        new LessonStep("Open the paper",
            "Click Draw (the first button on the shape shelf). A square of paper opens.",
            Mark: "shape.Draw",
            Done: () => _sketchDrawOpen || LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is not null,
            ShowMe: OpenSketchDrawCanvas,
            Where: "Sketch window > shape shelf, first button"),
        new LessonStep("Draw something",
            "Hold the left mouse button on the paper and draw - a star, a rune, a hook, a lightning " +
            "zig-zag. Lift the pen and draw again for more lines. Draw it big: the paper is the piece.",
            Done: () => _sketchDrawStrokes.Sum(s => s.Count) >= 12 || LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is not null,
            ShowMe: LessonDrawStar,
            Where: "The paper in the middle of the screen"),
        new LessonStep("Keep it",
            "Press Keep it (or Enter). Your drawing becomes a piece with the same four cards as any " +
            "shape: LOOK, PLACE, MOVE, EXTRAS.",
            Mark: "canvas.keep",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is not null,
            ShowMe: () => { if (_creatorSpell is { } doc) { if (_sketchDrawStrokes.Count == 0) LessonDrawStar(); AcceptSketchDrawing(doc, SketchForStage(doc, SpellStage.Cast)); } },
            Where: "Under the paper"),
        new LessonStep("Colour and glow",
            "LOOK: pick a colour (a cool blue reads well) and push Glow to 0.8.",
            Mark: "look.colour",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is { } p && p.Look.Colour != Vector3.One,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke), p => { p.Look.Colour = new Vector3(0.45f, 0.7f, 1f); p.Look.Glow = 0.8f; }),
            Where: "Sketch window > LOOK card"),
        new LessonStep("Spin it",
            "MOVE: drag Spin deg/s to 180 - half a turn a second, about its own face.",
            Mark: "move.spin",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is { } p && MathF.Abs(p.Motion.SpinDegreesPerSecond) >= 30f,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke), p => p.Motion.SpinDegreesPerSecond = 180f),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Make it rise",
            "MOVE: keep Travel on Fixed distance, set yd to 3, Direction to Up, and \"in s\" to 1.5 - " +
            "it rises three yards over a second and a half.",
            Mark: "move.distance",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is { Motion.Distance: >= 1f, Motion.Direction: SketchDirection.Up },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke), p =>
            { p.Motion.Distance = 3f; p.Motion.Direction = SketchDirection.Up; p.Motion.Duration = 1.5f; }),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Fade it out",
            "MOVE: set Fade in/out to 0.2 and 0.8 so it eases in and dissolves as it rises.",
            Mark: "move.fade",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke) is { Motion.FadeOut: >= 0.5f },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Stroke), p => { p.Motion.FadeIn = 0.2f; p.Motion.FadeOut = 0.8f; }),
            Where: "Sketch window > MOVE card"),
        LessonPlay("Your drawing is a spell effect."),
        new LessonStep("Draw anything",
            "Undo the drawing (delete) and try your own: the paper keeps every line you draw, and " +
            "each shape gets the same cards. Next lesson: all the ways a piece can move."),
    };

    private LessonStep[] LessonMoving() => new[]
    {
        LessonNewSpell("Moving Parts"),
        new LessonStep("An instant spell",
            "Click Instant, no travel in the Sketch's ANATOMY.",
            Mark: "plan.Instant, no travel",
            Done: () => _creatorSpell is { PlanStarted: true },
            ShowMe: () => { if (_creatorSpell is { } doc) ChooseSketchPlan(doc, 2); },
            Enter: () => { _sketchOpen = true; _sketchAnatomyFolded = false; },
            Where: "Sketch window > ANATOMY"),
        LessonPart(SpellStage.Cast, "Everything in this lesson goes in the cast."),
        LessonShape(SpellStage.Cast, SketchShapeKind.Disc, "Disc", "A glowing disc at your chest."),
        new LessonStep("Fly a distance",
            "MOVE: Travel is Fixed distance - the piece flies from where it is and dies at the end. " +
            "Set yd to 6 and \"in s\" to 1: it shoots six yards out in front of you in one second.",
            Mark: "move.distance",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Disc) is { Motion.Distance: >= 3f },
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Disc), p => { p.Motion.Distance = 6f; p.Motion.Duration = 1f; }),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Pick the direction",
            "Direction: Forward is where you face, Left is to your left, Up is up, Custom is any " +
            "mix. Try Left, watch it, then put it back to Forward.",
            Mark: "move.direction",
            Where: "Sketch window > MOVE card"),
        new LessonStep("Ease",
            "Ease: Linear keeps one speed the whole way; Ease out starts fast and settles. Flip it " +
            "and watch the difference, then press Next.",
            Mark: "move.ease",
            Where: "Sketch window > MOVE card"),
        LessonShape(SpellStage.Cast, SketchShapeKind.Star, "Star", "A second piece: a star."),
        new LessonStep("Ride on the disc",
            "PLACE: set Rides on to the disc. A rider goes wherever its parent goes - the star now " +
            "flies out with the disc.",
            Mark: "place.parent",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Star) is { } star &&
                        LessonPiece(SpellStage.Cast, SketchShapeKind.Disc) is { } disc && star.Place.Parent == disc.Name,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Star), p =>
                p.Place.Parent = LessonPiece(SpellStage.Cast, SketchShapeKind.Disc)?.Name ?? ""),
            Where: "Sketch window > PLACE card"),
        new LessonStep("Orbit",
            "MOVE (the star): set Orbit yd, turns/s to 1 and 1.5 - the star circles the disc one " +
            "yard out, one and a half turns a second, while the disc flies.",
            Mark: "move.orbit",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Star) is { Motion.OrbitRadius: >= 0.3f } p && p.Motion.OrbitTurnsPerSecond != 0f,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Star), p => { p.Motion.OrbitRadius = 1f; p.Motion.OrbitTurnsPerSecond = 1.5f; }),
            Where: "Sketch window > MOVE card, below Fade"),
        new LessonStep("Spin",
            "Spin deg/s turns a piece about its own face (tick \"about forward\" to turn it about the " +
            "way it points instead). Give the star 360.",
            Mark: "move.spin",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Star) is { } p && MathF.Abs(p.Motion.SpinDegreesPerSecond) >= 30f,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Star), p => p.Motion.SpinDegreesPerSecond = 360f),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Crackle",
            "Jitter yd, deg, /s shakes a piece: 0.05, 20, 12 makes it buzz like electricity. Give the " +
            "star some.",
            Mark: "move.jitter",
            Done: () => LessonPiece(SpellStage.Cast, SketchShapeKind.Star) is { } p && (p.Motion.JitterYards > 0f || p.Motion.JitterDegrees > 0f),
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.Cast, SketchShapeKind.Star), p => { p.Motion.JitterYards = 0.05f; p.Motion.JitterDegrees = 20f; p.Motion.JitterRate = 12f; }),
            Where: "Sketch window > MOVE card, below Orbit"),
        LessonPlay("Disc out, star circling it."),
        new LessonStep("The whole toolbox",
            "Starts at s delays a piece, Grow swells it, Fade dissolves it, Flash strobes it, Swing " +
            "sweeps it like a blade. Stack them: everything plays at once. For a missile, Travel " +
            "\"To the target\" hands the flight to the game (lesson 1)."),
    };

    private LessonStep[] LessonWard() => new[]
    {
        LessonNewSpell("Warding Ring"),
        new LessonStep("Buff rules",
            "Rules section: click the recipe Self buff - Frost Armor's rules, an aura on yourself. " +
            "It plans a cast and a State: the part that stays up while the aura lasts.",
            Mark: "recipe.Self buff",
            Done: () => _creatorLastRecipe == "Self buff",
            ShowMe: () => LessonRecipe("Self buff"),
            Enter: () => { _creatorLastRecipe = ""; LessonReveal(SpellIdeKind.Rules, "ws-rules"); },
            Where: "Spell Workshop > Rules section"),
        LessonPart(SpellStage.State, "The State plays on you for as long as the buff lasts."),
        LessonShape(SpellStage.State, SketchShapeKind.Ring, "Ring", "It lies flat on the ground round your feet."),
        new LessonStep("Gold, glowing",
            "LOOK: a gold colour and Glow 0.8.",
            Mark: "look.colour",
            Done: () => LessonPiece(SpellStage.State, SketchShapeKind.Ring) is { } p && p.Look.Colour != Vector3.One,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.State, SketchShapeKind.Ring), p => { p.Look.Colour = new Vector3(1f, 0.82f, 0.35f); p.Look.Glow = 0.8f; }),
            Where: "Sketch window > LOOK card"),
        new LessonStep("Turn slowly",
            "MOVE: Spin deg/s 60 - a slow, steady turn.",
            Mark: "move.spin",
            Done: () => LessonPiece(SpellStage.State, SketchShapeKind.Ring) is { } p && MathF.Abs(p.Motion.SpinDegreesPerSecond) >= 10f,
            ShowMe: () => LessonEdit(LessonPiece(SpellStage.State, SketchShapeKind.Ring), p => p.Motion.SpinDegreesPerSecond = 60f),
            Where: "Sketch window > MOVE card"),
        new LessonStep("Colour the body",
            "Composition section > STATE > Body (aura): tick tint and pick a colour. While the buff " +
            "is up, your whole body takes that wash.",
            Mark: "body.tint",
            Done: () => _creatorSpell is { } doc && doc.Composition.TryGetValue(SpellStage.State, out CreatorStageComposition? c) && c.Body.Tint is not null,
            ShowMe: () =>
            {
                if (_creatorSpell is not { } doc || !doc.Composition.TryGetValue(SpellStage.State, out CreatorStageComposition? c)) return;
                c.Body = c.Body with { Tint = 0xFFD060u };
                ReapPresentedEffect();
                _creatorLoopNextAt = 0;
            },
            Enter: () => LessonReveal(SpellIdeKind.Composition, "ws-composition"),
            Where: "Spell Workshop > Composition section > STATE"),
        LessonPlay("A ward."),
        new LessonStep("Buffs and auras",
            "The State's slots (Head, Chest, Base...) hang models on the body, Body (aura) can also " +
            "make it see-through, freeze its animations or grow it. A channelled spell works the " +
            "same with the Channel part."),
    };

    /// <summary>A five-point star on the paper, for Show me.</summary>
    private void LessonDrawStar()
    {
        if (!_sketchDrawOpen) OpenSketchDrawCanvas();
        var stroke = new List<Vector2>();
        for (int i = 0; i <= 5; i++)
        {
            float a = -MathF.PI / 2f + i * 4f * MathF.PI / 5f;
            stroke.Add(new Vector2(0.5f + 0.38f * MathF.Cos(a), 0.5f + 0.38f * MathF.Sin(a)));
        }
        // Fill in the long edges so it reads as a hand-drawn line, not five points.
        var dense = new List<Vector2>();
        for (int i = 0; i < stroke.Count - 1; i++)
            for (int k = 0; k < 8; k++) dense.Add(Vector2.Lerp(stroke[i], stroke[i + 1], k / 8f));
        dense.Add(stroke[^1]);
        _sketchDrawStrokes.Clear();
        _sketchDrawStrokes.Add(dense);
    }

    // ── running ─────────────────────────────────────────────────────────────

    private void StartLesson(Lesson lesson)
    {
        _lesson = lesson;
        _lessonSteps = lesson.Build();
        _lessonsOpen = true;
        _creatorPanel = CreatorPanel.Spells;
        EnterLessonStep(0);
        Console.WriteLine($"[lesson] start '{lesson.Name}' ({_lessonSteps.Length} steps)");
    }

    private void EnterLessonStep(int index)
    {
        _lessonStep = Math.Clamp(index, 0, Math.Max(_lessonSteps.Length - 1, 0));
        _lessonDoneAt = 0;
        _lessonMarkSeenFrame = -10;
        _lessonScrolledStep = -1;
        LessonCurrent?.Enter?.Invoke();
    }

    private void EndLesson()
    {
        if (_lesson is not null) Console.WriteLine($"[lesson] end '{_lesson.Name}'");
        _lesson = null;
        _lessonSteps = Array.Empty<LessonStep>();
    }

    /// <summary>Per frame (creator world): tick the current step, move on a beat after it is done.</summary>
    private void UpdateLessons()
    {
        UpdateLessonAuto();
        if (LessonCurrent is not { Done: { } done }) return;
        double now = NowSeconds();
        bool isDone;
        try { isDone = done(); } catch { isDone = false; }
        if (!isDone) { _lessonDoneAt = 0; return; }
        if (_lessonDoneAt == 0) { _lessonDoneAt = now; return; }
        if (now - _lessonDoneAt < 1.1 || _lessonStep >= _lessonSteps.Length - 1) return;
        EnterLessonStep(_lessonStep + 1);
    }

    private void UpdateLessonAuto()
    {
        if (LessonAutoIndex <= 0 || !LessonAuto) return;
        double now = NowSeconds();
        switch (_lessonAutoPhase)
        {
            case -1:
                if (_gl is null || _worldLoadStarted || now < 1.0) return;
                EnterOfflineWorld();
                _lessonAutoPhase = 0;
                return;
            case 0:
                if (_worldLoading || !_creatorWorldRequested || _spellCatalog is null || _spellEffects is null) return;
                if (LessonAutoIndex > Lessons.Length) { Console.WriteLine($"[lesson] no lesson {LessonAutoIndex}"); _quitRequested = true; return; }
                StartLesson(Lessons[LessonAutoIndex - 1]);
                _lessonAutoPhase = 1; _lessonAutoAt = now;
                return;
            case 1:   // each step: a second to be done by itself, then Show me, then judge
            {
                if (LessonCurrent is not { } step) return;
                if (now - _lessonAutoAt < 1.0) return;
                bool done = step.Done?.Invoke() ?? true;
                _lessonAutoMark = step.Mark is null ? "-" : ImGui.GetFrameCount() - _lessonMarkSeenFrame <= 2
                    ? (_lessonMarkHidden ? "hidden" : "shown") : "missing";
                if (!done) { step.ShowMe?.Invoke(); _lessonAutoPhase = 2; _lessonAutoAt = now; return; }
                FinishAutoStep(step, true);
                return;
            }
            case 2:
            {
                if (LessonCurrent is not { } step || now - _lessonAutoAt < 1.0) return;
                FinishAutoStep(step, step.Done?.Invoke() ?? true);
                return;
            }
            case 3:
                if (now - _lessonAutoAt < 2.0) return;
                Console.WriteLine($"[lesson] DONE failures={_lessonAutoFailures}");
                Console.Out.Flush();
                _quitRequested = true;
                _lessonAutoPhase = 4;
                return;
        }
    }

    private void FinishAutoStep(LessonStep step, bool done)
    {
        if (!done)
        {
            _lessonAutoFailures++;
            Console.WriteLine($"[lesson]   state: loop={_creatorLoopOn} hold={_sketchHold} pre={_creatorLoopPrecast} cast={_creatorLoopCast} " +
                              $"miss={_creatorLoopMissilePhase} imp={_creatorLoopImpactPhase} state={_creatorLoopStateHold} " +
                              $"plan=[{string.Join(",", _creatorSpell?.PlannedStages ?? new HashSet<SpellStage>())}]");
        }
        Console.WriteLine($"[lesson] step {_lessonStep + 1}/{_lessonSteps.Length} '{step.Title}' {(done ? "DONE" : "FAIL")}" +
                          $" mark={_lessonAutoMark}");
        ArmGameplayDumpNamed($"lesson{LessonAutoIndex}-{_lessonStep + 1:00}");
        if (_lessonStep >= _lessonSteps.Length - 1) { _lessonAutoPhase = 3; _lessonAutoAt = NowSeconds(); return; }
        EnterLessonStep(_lessonStep + 1);
        _lessonAutoPhase = 1; _lessonAutoAt = NowSeconds();
    }

    // ── the window ──────────────────────────────────────────────────────────

    /// <summary>The Lessons button (top of the Spell section).</summary>
    private void DrawCreatorLessonsButton()
    {
        if (CreatorButton(_lessonsOpen ? "Lessons (open)" : "Lessons - learn by building", 0f)) _lessonsOpen = !_lessonsOpen;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Step-by-step lessons: build a bolt, draw your own shape, make things move, " +
                             "make a buff. Each step outlines the control to use in gold and ticks when you " +
                             "have done it; Show me does it for you.");
    }

    private void DrawCreatorLessons()
    {
        if (!_lessonsOpen || !_creatorWorldRequested || (SpellShowcaseName is not null && _showcaseLog.Count > 0)) return;
        var io = ImGui.GetIO();
        float cs = CreatorUiScale;
        float left = MathF.Max(_creatorSidebarRight, 0f) + 12f;
        float right = io.DisplaySize.X - (_sketchOpen ? Math.Clamp(560f * cs, 420f, io.DisplaySize.X * 0.62f) + 16f : 8f);
        float width = Math.Clamp(right - left - 12f, 320f, 600f * cs);
        float top = SpellFocusActive ? _spellIdeStripHeight + 8f : CreatorSidebarTop;
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, io.DisplaySize.Y - top - 16f));
        if (BeginSpellIdeWindow("##lessons", new Vector2(left + MathF.Max(right - left - width, 0f) * 0.5f, top),
                Vector2.Zero, ImGuiCond.Always, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize, 0.3f))
        {
            ImGui.PushTextWrapPos(0f);
            if (LessonCurrent is not { } step) DrawLessonList();
            else DrawLessonStep(step);
            ImGui.PopTextWrapPos();
        }
        EndSpellIdeWindow();
    }

    private void DrawLessonList()
    {
        ImGui.TextColored(SpellIdeGold, "LESSONS");
        ImGui.SameLine();
        if (ImGui.SmallButton("close##lessons")) _lessonsOpen = false;
        ImGui.TextDisabled("Build a spell step by step with the real controls. The control to use glows " +
                           "gold; each step ticks when you have done it.");
        ImGui.Spacing();
        for (int i = 0; i < Lessons.Length; i++)
        {
            Lesson lesson = Lessons[i];
            ImGui.PushID(i);
            if (CreatorButton($"{i + 1}. {lesson.Name}", 200f * CreatorUiScale)) StartLesson(lesson);
            ImGui.SameLine();
            ImGui.TextDisabled(lesson.Kind);
            ImGui.TextUnformatted(lesson.Blurb);
            ImGui.Spacing();
            ImGui.PopID();
        }
        ImGui.Separator();
        if (CreatorButton("Watch an expert: Stormwing", 0f)) StartShowcaseInPlace("stormwing");
        ImGui.SameLine();
        ImGui.TextDisabled("an advanced spell built in front of you, step by step");
    }

    private void DrawLessonStep(LessonStep step)
    {
        ImGui.TextColored(SpellIdeGold, _lesson!.Name.ToUpperInvariant());
        ImGui.SameLine();
        ImGui.TextDisabled($"step {_lessonStep + 1} of {_lessonSteps.Length}");
        // Progress pips.
        var dl = ImGui.GetWindowDrawList();
        Vector2 at = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X, pip = MathF.Max(w / _lessonSteps.Length, 2f);
        for (int i = 0; i < _lessonSteps.Length; i++)
            dl.AddRectFilled(at + new Vector2(i * pip + 1f, 0f), at + new Vector2((i + 1) * pip - 1f, 4f),
                AtlasU(AtlasGold, i < _lessonStep ? 0.9f : i == _lessonStep ? 0.55f : 0.15f));
        ImGui.Dummy(new Vector2(w, 8f));

        ImGui.TextUnformatted(step.Title);
        ImGui.TextColored(AtlasInk, step.Do);
        bool seen = step.Mark is not null && ImGui.GetFrameCount() - _lessonMarkSeenFrame <= 2;
        if (step.Where is { } where)
            ImGui.TextDisabled(seen && !_lessonMarkHidden ? $"Where: {where} - outlined in gold."
                : seen ? $"Where: {where} - scroll to it, it is outlined in gold."
                : $"Where: {where}");

        bool done = step.Done?.Invoke() ?? false;
        if (step.Done is not null)
        {
            if (done) ImGui.TextColored(AtlasGreen, "Done!");
            else ImGui.TextColored(AtlasInkFaint, "waiting for you...");
        }

        ImGui.Spacing();
        bool last = _lessonStep >= _lessonSteps.Length - 1;
        if (step.ShowMe is not null && !done)
        {
            if (CreatorButton("Show me", 0f)) step.ShowMe();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Do this step for me (then look at what changed).");
            ImGui.SameLine();
        }
        if (_lessonStep > 0)
        {
            if (CreatorButton("< Back", 0f)) EnterLessonStep(_lessonStep - 1);
            ImGui.SameLine();
        }
        if (!last)
        {
            if (CreatorButton(step.Done is null || done ? "Next >" : "Skip >", 0f)) EnterLessonStep(_lessonStep + 1);
            ImGui.SameLine();
        }
        if (CreatorButton(last ? "Finish" : "Leave lesson", 0f)) EndLesson();
    }

    /// <summary>Run the scripted Stormwing build in this session (the showcase), from the lessons list.</summary>
    private void StartShowcaseInPlace(string name)
    {
        SpellShowcaseName = name;
        _showcaseLog.Clear();
        _showcaseStep = 0;
        _showcaseAt = NowSeconds() - 10;
        _showcaseAuto = false;
        _lessonsOpen = false;
    }
}
