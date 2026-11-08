using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// SPELL ANATOMY — the top of the Sketch window (owner, 2026-09-10):
//
//   "the tool must become an IDE that I can start from 0, decide if i want
//    precast or no, then cast, then missile if thats part of it, if not
//    impact, or channel if that's the cast - etc. The IDE user gets to decide
//    that, and has a tool that lets them understand/create every piece of the
//    puzzle."
//
// A spell's visual is not one thing, it is a handful of independent slots on a
// SpellVisual row, and the game fires every slot that is populated
// (SpellVisualCatalog.cs:44-45: "every populated slot on a reached row fires.
// The stage selects LIFETIME POLICY only"). So the anatomy IS the set of slots
// you choose to fill, and that choice is the first thing an author makes.
//
// WHY A PLAN, AND NOT JUST "DOES IT HAVE ART". Because in the data, a precast
// you deliberately left out and a precast you have not got to yet look exactly
// the same: empty. One is finished and one is a to-do, and only the author
// knows which. So the plan is stored, and the list below shows three states
// rather than two - planned and authored, planned and empty (a to-do), and not
// part of this spell.
//
// Each row also says WHEN it plays and HOW LONG it lives, because that is the
// part nobody can read off the data.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    /// <summary>One row of the anatomy: a slot on the SpellVisual row, in play order.</summary>
    private readonly record struct SketchPhaseRow(
        string Name, SpellStage? Stage, bool Missile, string When, string Life);

    /// <summary>Play order, which is NOT the DBC field order: this is the order a player
    /// experiences, and the order an author builds in.</summary>
    private static readonly SketchPhaseRow[] SketchPhaseRows =
    {
        new("Precast", SpellStage.Precast, false,
            "while the cast bar is filling",
            "Removed when the cast finishes or is interrupted. This is the gathering-energy " +
            "part. A spell with no cast time cannot show one."),
        new("Cast", SpellStage.Cast, false,
            "at the instant the spell goes off",
            "Self-terminating: it plays once, for as long as its own sequence, and stops. " +
            "This is the swing, the flash, the release."),
        new("Missile", null, true,
            "flying from the caster to the target",
            "Only spells with travel time have one. The model flies through the WORLD at the " +
            "spell's missile speed, so unlike everything else here it does not ride the " +
            "caster's animation. An arrow, a fireball, a bolt."),
        new("Impact", SpellStage.Impact, false,
            "where it lands",
            "Self-terminating, at the target - or at the ground for a ground-targeted spell. " +
            "This is the hit. A melee spell like Cleave goes straight here from Cast, with " +
            "no missile in between."),
        new("Channel", SpellStage.Channel, false,
            "over and over while the channel lasts",
            "Persistent: it keeps being re-presented until the channel ends. Use this INSTEAD " +
            "of Cast for a spell that is held rather than fired."),
        new("State", SpellStage.State, false,
            "for as long as the aura is on the unit",
            "Persistent: it does not stop by itself. This is the buff glow, the shield, the " +
            "thing that says an effect is still on you."),
    };

    /// <summary>The shapes real spells actually take. Choosing one is the "start from 0"
    /// moment: it does not create any art, it writes down which phases this spell HAS, so the
    /// list below turns into a to-do list instead of a wall of equal-looking empties.</summary>
    private static readonly (string Name, string Example, SpellStage[] Stages, bool Missile)[]
        SketchPlanPresets =
    {
        ("Melee strike", "Cleave, Sinister Strike",
            new[] { SpellStage.Cast, SpellStage.Impact }, false),
        ("Projectile", "Fireball, Shadow Bolt",
            new[] { SpellStage.Precast, SpellStage.Cast, SpellStage.Impact }, true),
        ("Instant, no travel", "Arcane Explosion",
            new[] { SpellStage.Cast, SpellStage.Impact }, false),
        ("Channelled", "Blizzard, Drain Life",
            new[] { SpellStage.Cast, SpellStage.Channel }, false),
        ("Buff on yourself", "Power Word: Fortitude",
            new[] { SpellStage.Cast, SpellStage.State }, false),
    };

    /// <summary>How many pieces the author has drawn in a phase.</summary>
    private static int SketchPieceCount(CreatorSpellDoc doc, SpellStage stage) =>
        doc.Sketches.TryGetValue(stage, out SketchDoc? sketch) ? sketch.Pieces.Count : 0;

    /// <summary>How many models a phase already carries that the author did NOT draw - what the
    /// spell inherited from whatever it was derived from. Worth showing separately: "Cleave's
    /// cast already has a model" is a different fact from "I have drawn two pieces".</summary>
    private int SketchInheritedCount(CreatorSpellDoc doc, SpellStage stage)
    {
        if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) return 0;
        string sketchPath = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name,
            stage.ToString().ToLowerInvariant());
        int count = 0;
        foreach (string? slot in composition.Slots)
            if (slot is { Length: > 0 } &&
                !string.Equals(slot, sketchPath, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    private static bool SketchHasMissile(CreatorSpellDoc doc) =>
        doc.MissileOverride is { Length: > 0 } || doc.MissilePath is { Length: > 0 };

    /// <summary>The anatomy list: what this spell is made of, what is still to do, and one
    /// sentence each on when it plays and how long it lives.</summary>
    private bool _sketchAnatomyFolded;

    /// <summary>Take plan preset <paramref name="index"/> (the "What kind of spell is this?" row).</summary>
    private void ChooseSketchPlan(CreatorSpellDoc doc, int index)
    {
        var (_, _, stages, missile) = SketchPlanPresets[index];
        doc.PlannedStages.Clear();
        foreach (SpellStage stage in stages) doc.PlannedStages.Add(stage);
        doc.PlannedMissile = missile;
        doc.PlanStarted = true;
        // Arm every phase the plan calls for, and land on the first one. Choosing
        // "projectile" and being dropped into a precast the loop does not play is
        // how this tool manages to do nothing while behaving perfectly.
        foreach (SpellStage planned in stages) SetSketchPhasePlaying(planned, true);
        SketchEditStage(stages[0]);
    }

    private void DrawSketchAnatomy(CreatorSpellDoc doc, float cs)
    {
        // Foldable once the plan is made: it is how you switch part, but it is tall, and the
        // cards below are where the work happens (owner, 2026-11-03).
        if (doc.PlanStarted)
        {
            if (ImGui.ArrowButton("##anatomy-fold", _sketchAnatomyFolded ? ImGuiDir.Right : ImGuiDir.Down))
                _sketchAnatomyFolded = !_sketchAnatomyFolded;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_sketchAnatomyFolded ? "Show the spell's parts" : "Fold the parts away to make room for the cards");
            ImGui.SameLine();
        }
        ImGui.TextColored(SpellIdeGold, "ANATOMY");
        if (doc.PlanStarted && _sketchAnatomyFolded)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(folded - drawing the {_sketchStage.ToString().ToLowerInvariant()})");
            return;
        }
        CreatorHelp("What this spell is made of. A spell's visual is not one thing - it is a " +
                    "handful of independent parts, and the game plays every part you fill in. " +
                    "Decide which parts this spell HAS, then draw each one.\n\n" +
                    "Nothing here is forced: a melee spell has no missile, a channelled spell " +
                    "uses Channel instead of Cast, and a buff needs a State that stays up.");

        // ── the "start from 0" row ──────────────────────────────────────────
        if (!doc.PlanStarted)
        {
            ImGui.TextDisabled("What kind of spell is this?");
            for (int i = 0; i < SketchPlanPresets.Length; i++)
            {
                var (name, example, stages, missile) = SketchPlanPresets[i];
                if (i > 0 && i % 3 != 0) ImGui.SameLine();
                if (LessonMarked($"plan.{name}", ImGui.SmallButton($"{name}##plan{i}")))
                    ChooseSketchPlan(doc, i);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{example}\n\n" +
                        string.Join(" -> ", stages.Select(x => x.ToString())) +
                        (missile ? " (with a missile between cast and impact)" : ""));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("start empty"))
            {
                doc.PlanStarted = true;
                doc.PlannedStages.Clear();
                doc.PlannedMissile = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Decide the parts yourself, one at a time.");
            ImGui.Separator();
            return;
        }

        // ── the parts, in the order a player experiences them ───────────────
        foreach (SketchPhaseRow row in SketchPhaseRows)
        {
            ImGui.PushID(row.Name);

            bool planned = row.Missile ? doc.PlannedMissile
                                       : row.Stage is { } st && doc.PlannedStages.Contains(st);
            int drawn = row.Missile ? 0 : SketchPieceCount(doc, row.Stage!.Value);
            int inherited = row.Missile
                ? (SketchHasMissile(doc) ? 1 : 0)
                : SketchInheritedCount(doc, row.Stage!.Value);
            bool has = drawn > 0 || inherited > 0;
            bool current = !row.Missile && row.Stage == _sketchStage;

            // In / out of the plan. This is the "decide if i want precast or no" switch.
            bool inPlan = planned;
            if (ImGui.Checkbox("##plan", ref inPlan))
            {
                if (row.Missile) doc.PlannedMissile = inPlan;
                else if (inPlan) doc.PlannedStages.Add(row.Stage!.Value);
                else doc.PlannedStages.Remove(row.Stage!.Value);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Does this spell have a {row.Name.ToLowerInvariant()}?\n" +
                                 "Ticking it adds it to the list of parts to build; it does not " +
                                 "create anything on its own.");

            ImGui.SameLine();
            Vector4 tint = current ? SpellIdeGold
                : has ? new Vector4(0.85f, 0.88f, 0.9f, 1f)
                : planned ? new Vector4(0.95f, 0.72f, 0.35f, 1f)     // planned but empty = to-do
                : new Vector4(0.5f, 0.5f, 0.54f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
            bool clicked = LessonMarked($"part.{row.Name}", ImGui.SmallButton($"{row.Name,-8}##go"));
            ImGui.PopStyleColor();
            if (clicked && row.Stage is { } target) SketchEditStage(target);
            if (row.Missile && clicked)
                _sketchStatus = "A missile is made by setting a piece's Travel to \"To the target\" " +
                                "in whichever phase launches it.";

            // Two columns, not three. A third column of "when it plays" simply does not fit
            // beside a tick, a name and a count at any sane window width - it ran straight
            // through the state text and came out as "Precastnot part of this spellwhile the
            // cast b". It lives in the tooltip, which is where a sentence belongs anyway.
            ImGui.SameLine(140f * cs);
            string state = drawn > 0 && inherited > 0 ? $"{drawn} drawn + {inherited} inherited"
                : drawn > 0 ? $"{drawn} piece{(drawn == 1 ? "" : "s")}"
                : inherited > 0 ? $"{inherited} inherited"
                : planned ? "to do"
                : "-";
            ImGui.TextDisabled(state);
            CreatorHelp($"Plays {row.When}.\n\n{row.Life}");

            // What the BODY does during this part - the caster animation, by name. It is a
            // property of the stage's kit (SpellVisualKit field 2), so it belongs on the
            // stage's row, not three panels away under Composition. Shown for the parts in
            // the plan: an animation on a part the spell does not have is noise.
            if (!row.Missile && planned &&
                doc.Composition.TryGetValue(row.Stage!.Value, out CreatorStageComposition? stageComposition))
            {
                ImGui.Indent(28f * cs);
                ImGui.TextDisabled("body does");
                ImGui.SameLine(112f * cs);
                DrawCreatorAnimationButton(row.Stage.Value, stageComposition, 210f * cs);
                ImGui.Unindent(28f * cs);
            }

            ImGui.PopID();
        }

        ImGui.Spacing();
        if (ImGui.SmallButton("rethink"))
        {
            doc.PlanStarted = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick a different kind of spell. Nothing you have drawn is deleted.");
        ImGui.Separator();
    }
}
