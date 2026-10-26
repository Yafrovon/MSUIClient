using System.Numerics;
using MSUIClient.Creator.Spells;
using MSUIClient.Engine;
using MSUIClient.World.Spells;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// THE GROUND POINT IN THE WORKSHOP (shared_docs/SPELL_CREATOR_PLAN.md P2.4 + P1.5).
//
// A ground-targeted spell (Rules > Target > "player picks a spot") previews the way the live
// game plays it: the missile flies to a SPOT, at the RULES' speed, and the impact plays AT the
// spot - not on a dummy and not 20 yards ahead at a made-up speed. The spot defaults to 15 yd
// in front of the caster on the ground; "Pick the spot" arms one click on the ground to move it.
//
// On the stage: a dim ring at the spell's RANGE around the caster, and at the spot a cross and
// a ring at the largest radius any effect uses - so "30 yd range, 10 yd blast" is something
// you see, sized by the same SpellRange/SpellRadius rows the server reads.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private Vector3? _creatorGroundPoint;
    private bool _creatorGroundPickArmed;

    private static readonly Vector4 CreatorRangeRingColour = new(0.55f, 0.75f, 1f, 0.35f);
    private static readonly Vector4 CreatorSpotColour = new(1f, 0.72f, 0.25f, 0.95f);

    /// <summary>Does the spell being built aim at the ground? The rules say so when they exist;
    /// otherwise the source spell's own Spell.dbc targets.</summary>
    private bool CreatorGroundTargeted(CreatorSpellDoc doc) =>
        doc.Rules?.GroundTargeted ?? ((doc.Info.Targets & SpellRules.TargetFlagDestLocation) != 0);

    /// <summary>The missile speed the preview flies at: the rules', else the source spell's.</summary>
    private float CreatorMissileSpeed(CreatorSpellDoc doc) =>
        doc.Rules is { } rules ? rules.Speed : doc.Info.Speed;

    /// <summary>The spot a ground spell lands on: the picked one, else 15 yd ahead, on the ground.</summary>
    private Vector3 CreatorGroundPreviewPoint()
    {
        if (_creatorGroundPoint is { } picked) return picked;
        if (_controller is null) return default;
        var forward = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
        Vector3 spot = _controller.Position + forward * 15f;
        if (_terrain?.SampleHeight(spot.X, spot.Y) is float ground) spot.Z = ground;
        return spot;
    }

    /// <summary>One click on the ground moves the spot. Runs in the world-click chain beside the
    /// World Builder's and the Encounter Lab's armed tools; owns the click only while armed.</summary>
    private bool HandleCreatorGroundPickClick(WorldMouseClick click)
    {
        if (!_creatorGroundPickArmed || !_creatorWorldRequested) return false;
        _creatorGroundPickArmed = false;
        if (click.Button == Silk.NET.Input.MouseButton.Left && TryPickGround(click.Position, out Vector3 spot))
        {
            _creatorGroundPoint = spot;
            _creatorLoopNextAt = 0;
            _rulesStatus = $"The spot is {Vector3.Distance(spot, _controller?.Position ?? spot):0.#} yd away.";
        }
        return true;
    }

    /// <summary>Range ring around the caster, the spot and its blast radius.</summary>
    private void AddCreatorRulesRings(List<GizmoLine> lines, CreatorSpellDoc doc)
    {
        if (_controller is null || doc.Rules is not { } rules || SpellRuleTablesLoaded is not { } tables) return;
        if (tables.Ranges.TryGetValue((uint)rules.GetInt("rangeIndex"), out var range) && range.Max > 0.5f)
            Ring(lines, _controller.Position + new Vector3(0f, 0f, 0.08f), range.Max, CreatorRangeRingColour, 64);
        if (!CreatorGroundTargeted(doc)) return;

        Vector3 spot = CreatorGroundPreviewPoint() + new Vector3(0f, 0f, 0.08f);
        const float cross = 0.6f;
        lines.Add(new GizmoLine(spot - Vector3.UnitX * cross, spot + Vector3.UnitX * cross, CreatorSpotColour));
        lines.Add(new GizmoLine(spot - Vector3.UnitY * cross, spot + Vector3.UnitY * cross, CreatorSpotColour));
        lines.Add(new GizmoLine(spot, spot + Vector3.UnitZ * 1.5f, CreatorSpotColour with { W = 0.5f }));
        float radius = 0f;
        for (int e = 1; e <= 3; e++)
            if (tables.Radii.TryGetValue((uint)rules.GetInt(SpellRules.Column("effectRadiusIndex", e)), out var r))
                radius = MathF.Max(radius, r.Yards);
        if (radius > 0f) Ring(lines, spot, radius, CreatorSpotColour with { W = 0.7f }, 48);
    }

    private static void Ring(List<GizmoLine> lines, Vector3 centre, float radius, Vector4 colour, int segments)
    {
        Vector3 Point(int i)
        {
            float a = i * MathF.Tau / segments;
            return centre + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0f);
        }
        for (int i = 0; i < segments; i++) lines.Add(new GizmoLine(Point(i), Point(i + 1), colour));
    }
}
