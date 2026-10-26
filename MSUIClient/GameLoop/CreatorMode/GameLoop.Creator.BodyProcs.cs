using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: BODY dials - the kit's CharProc lanes (SPELL_CREATOR_PLAN.md P4.4)
//
// A kit carries up to four CharProc lanes (SpellVisualKit f15-18 types, f19+ params). The
// ones that change the UNIT rather than hang a model on it get a dial here, with the
// semantics the client already plays them by:
//   1  tint          p0 packed RGB              (State kit: the aura colours the body)
//   14 alpha         p0 0..1                    (State kit: the body fades to it, 1 s ramp)
//   11 anim rate     p0 factor, 0 = frozen      (State kit: Ice Block's freeze)
//   8  weapon trail  p0 RGB, p1 20, p2 ms, p3 % (any kit: GameLoop.WeaponTrail.cs)
// Every other lane the source authored (chain beam 0, area shards 9, 13/7/2...) is kept as
// it was. The Completer writes the composed lanes (`charProcs`, SpellVisualCloner).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>A coloured weapon trail (CharProc 8).</summary>
    private readonly record struct CreatorTrail(uint Rgb, float DurationMs, float AlphaPercent);

    /// <summary>The body dials of one phase; null = that lane is absent.</summary>
    private readonly record struct CreatorBody(uint? Tint, float? Alpha, float? Rate, CreatorTrail? Trail)
    {
        /// <summary>A factor on the body's size (CharProc 2).</summary>
        public float? Scale { get; init; }
        public bool Any => Tint is not null || Alpha is not null || Rate is not null || Trail is not null || Scale is not null;
    }

    private static readonly int[] CreatorBodyProcTypes =
        { AuraVisualLaw.TintProc, AuraVisualLaw.AlphaProc, AuraVisualLaw.AnimationRateProc, AuraVisualLaw.ScaleProc, WeaponTrailProc };

    /// <summary>The body a kit's lanes already say (the first lane of each dialled type).</summary>
    private static CreatorBody CreatorBodyOf(IReadOnlyList<SpellVisualCharProc> procs)
    {
        uint? tint = null; float? alpha = null, rate = null, scale = null; CreatorTrail? trail = null;
        foreach (SpellVisualCharProc proc in procs)
        {
            float P(int i) => i < proc.Parameters.Length && float.IsFinite(proc.Parameters[i]) ? proc.Parameters[i] : 0f;
            switch (proc.Type)
            {
                case AuraVisualLaw.TintProc when tint is null: tint = (uint)Math.Clamp(MathF.Round(P(0)), 0f, 0xFFFFFF); break;
                case AuraVisualLaw.AlphaProc when alpha is null: alpha = P(0); break;
                case AuraVisualLaw.AnimationRateProc when rate is null: rate = P(0); break;
                case AuraVisualLaw.ScaleProc when scale is null: scale = P(0); break;
                case WeaponTrailProc when trail is null:
                    trail = new CreatorTrail((uint)Math.Clamp(MathF.Round(P(0)), 0f, 0xFFFFFF), P(2), P(3));
                    break;
            }
        }
        return new CreatorBody(tint, alpha, rate, trail) { Scale = scale };
    }

    /// <summary>The lanes the phase's kit carries: the source's non-body lanes, then the dials.
    /// Never more than the kit's four; <paramref name="dropped"/> counts what did not fit.</summary>
    private static SpellVisualCharProc[] CreatorComposedProcs(IReadOnlyList<SpellVisualCharProc> authored, CreatorBody body, out int dropped)
    {
        var lanes = authored.Where(p => Array.IndexOf(CreatorBodyProcTypes, p.Type) < 0).ToList();
        if (body.Tint is uint tint) lanes.Add(new SpellVisualCharProc(AuraVisualLaw.TintProc, new[] { (float)tint, 0f, 0f, 0f }));
        if (body.Alpha is float alpha) lanes.Add(new SpellVisualCharProc(AuraVisualLaw.AlphaProc, new[] { alpha, 0f, 0f, 0f }));
        if (body.Rate is float rate) lanes.Add(new SpellVisualCharProc(AuraVisualLaw.AnimationRateProc, new[] { rate, 0f, 0f, 0f }));
        if (body.Scale is float scale) lanes.Add(new SpellVisualCharProc(AuraVisualLaw.ScaleProc, new[] { scale, 0f, 0f, 0f }));
        if (body.Trail is { } trail)
            lanes.Add(new SpellVisualCharProc(WeaponTrailProc, new[] { (float)trail.Rgb, 20f, trail.DurationMs, trail.AlphaPercent }));
        dropped = Math.Max(0, lanes.Count - 4);
        return lanes.Take(4).ToArray();
    }

    private static SpellVisualCharProc[] CreatorComposedProcs(CreatorStageComposition c) =>
        c.Body == c.AuthoredBody ? c.AuthoredProcs : CreatorComposedProcs(c.AuthoredProcs, c.Body, out _);

    /// <summary>The export's `charProcs`: all four lanes, positional (type -1 = empty); null = keep.</summary>
    private static JsonArray? CreatorProcLanesJson(CreatorStageComposition c)
    {
        if (c.Body == c.AuthoredBody) return null;
        SpellVisualCharProc[] procs = CreatorComposedProcs(c);
        return new JsonArray(Enumerable.Range(0, 4).Select(lane => (JsonNode)new JsonObject
        {
            ["type"] = lane < procs.Length ? procs[lane].Type : -1,
            ["params"] = new JsonArray(Enumerable.Range(0, 4).Select(p =>
                (JsonNode)(lane < procs.Length && p < procs[lane].Parameters.Length ? procs[lane].Parameters[p] : 0f)).ToArray()),
        }).ToArray());
    }

    private static JsonObject CreatorBodyJson(CreatorBody body) => new()
    {
        ["tint"] = body.Tint is { } tint ? tint : null,
        ["alpha"] = body.Alpha is { } alpha ? alpha : null,
        ["rate"] = body.Rate is { } rate ? rate : null,
        ["scale"] = body.Scale is { } scale ? scale : null,
        ["trail"] = body.Trail is { } trail
            ? new JsonObject { ["rgb"] = trail.Rgb, ["ms"] = trail.DurationMs, ["alpha"] = trail.AlphaPercent }
            : null,
    };

    private static CreatorBody CreatorBodyFromJson(JsonNode? node) => node is not JsonObject o ? default : new CreatorBody(
        o["tint"]?.GetValue<uint>(),
        o["alpha"]?.GetValue<float>(),
        o["rate"]?.GetValue<float>(),
        o["trail"] is JsonObject t
            ? new CreatorTrail(t["rgb"]?.GetValue<uint>() ?? 0xFFFFFF, t["ms"]?.GetValue<float>() ?? 600f, t["alpha"]?.GetValue<float>() ?? 100f)
            : null) { Scale = o["scale"]?.GetValue<float>() };

    // ── the preview: the State phase's body dials on the local character ─────

    private readonly AuraVisualState _creatorBodyAura = new();
    private bool _creatorBodyAuraLive;

    /// <summary>While the creator presents its spell's STATE phase, the body lanes act on the
    /// local character exactly as a live aura's would (UpdateAuraStateVisuals' law).</summary>
    private void UpdateCreatorBodyPreview(double now)
    {
        var body = new List<AuraBodySpell>(1);
        // The State hold re-presents every loop tick while cast/impact interleave: hold the body
        // look for the whole hold, not only while State was the last kit presented.
        bool stateShown = (_creatorLoopOn && _creatorLoopStateHold) ||
                          (_presentedEffectSpell == _creatorSpell?.Info.Id && _presentedEffectStage == "STATE");
        if (_creatorWorldRequested && _creatorSpell is { } doc && stateShown &&
            doc.Composition.TryGetValue(SpellStage.State, out CreatorStageComposition? state))
        {
            AuraBodyNode[] nodes = AuraVisualLaw.Nodes(CreatorComposedProcs(state));
            if (nodes.Length != 0) body.Add(new AuraBodySpell(doc.Info.Id, nodes));
        }
        _creatorBodyAuraLive = _creatorWorldRequested;
        if (_creatorBodyAuraLive) _creatorBodyAura.Reconcile(1f, body, now);
    }

    /// <summary>The body look the local character renders with in the creator (null outside it).</summary>
    private AuraVisualState? CreatorBodyAura => _creatorBodyAuraLive ? _creatorBodyAura : null;

    // ── the inspector rows ───────────────────────────────────────────────────

    private void DrawCreatorBodyRows(SpellStage stage, CreatorStageComposition composition, float cs)
    {
        CreatorBody body = composition.Body;
        bool changed = false;
        if (stage == SpellStage.State)
        {
            ImGui.TextDisabled("Body (aura)");
            ImGui.SameLine(130f * cs);
            bool tintOn = body.Tint is not null;
            if (ImGui.Checkbox("tint##body", ref tintOn)) { body = body with { Tint = tintOn ? 0x4080FFu : null }; changed = true; }
            if (body.Tint is uint tint)
            {
                ImGui.SameLine();
                var colour = new Vector3((tint >> 16) & 0xFF, (tint >> 8) & 0xFF, tint & 0xFF) / 255f;
                if (ImGui.ColorEdit3("##tint", ref colour, ImGuiColorEditFlags.NoInputs))
                {
                    body = body with { Tint = CreatorPackRgb(colour) };
                    changed = true;
                }
            }
            ImGui.SameLine();
            bool alphaOn = body.Alpha is not null;
            if (ImGui.Checkbox("see-through##body", ref alphaOn)) { body = body with { Alpha = alphaOn ? 0.5f : null }; changed = true; }
            if (body.Alpha is float alpha)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f * cs);
                if (ImGui.SliderFloat("##alpha", ref alpha, 0f, 1f, "%.2f")) { body = body with { Alpha = alpha }; changed = true; }
            }
            ImGui.SameLine();
            bool rateOn = body.Rate is not null;
            if (ImGui.Checkbox("anim speed##body", ref rateOn)) { body = body with { Rate = rateOn ? 0.5f : null }; changed = true; }
            if (body.Rate is float rate)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f * cs);
                if (ImGui.SliderFloat("##rate", ref rate, 0f, 3f, rate == 0f ? "frozen" : "x%.2f")) { body = body with { Rate = rate }; changed = true; }
            }
            ImGui.SameLine();
            bool scaleOn = body.Scale is not null;
            if (ImGui.Checkbox("size##body", ref scaleOn)) { body = body with { Scale = scaleOn ? 1.5f : null }; changed = true; }
            if (body.Scale is float size)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f * cs);
                if (ImGui.SliderFloat("##scale", ref size, 0.25f, 3f, "x%.2f")) { body = body with { Scale = size }; changed = true; }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What the aura does to the body it sits on, while it lasts: a colour wash, " +
                                 "see-through (fades in over a second), and the speed its animations play at " +
                                 "(0 = frozen solid, like Ice Block), and its size (Bloodlust's x1.5). Plays on the caster while State is previewed.");
        }

        ImGui.TextDisabled("Weapon trail");
        ImGui.SameLine(130f * cs);
        bool trailOn = body.Trail is not null;
        if (ImGui.Checkbox("##trail", ref trailOn))
        {
            body = body with { Trail = trailOn ? new CreatorTrail(0xDB0B10, 600f, 100f) : null };
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A coloured streak behind the swinging weapon hand for a while - Heroic Strike's " +
                             "and Cleave's is 0.6 s, Whirlwind's 10 s.");
        if (body.Trail is { } trail)
        {
            ImGui.SameLine();
            var colour = new Vector3((trail.Rgb >> 16) & 0xFF, (trail.Rgb >> 8) & 0xFF, trail.Rgb & 0xFF) / 255f;
            if (ImGui.ColorEdit3("##trailrgb", ref colour, ImGuiColorEditFlags.NoInputs))
            { trail = trail with { Rgb = CreatorPackRgb(colour) }; changed = true; }
            ImGui.SameLine();
            float seconds = trail.DurationMs / 1000f;
            ImGui.SetNextItemWidth(90f * cs);
            if (ImGui.SliderFloat("##trailms", ref seconds, 0.1f, 10f, "%.1f s")) { trail = trail with { DurationMs = MathF.Round(seconds * 1000f) }; changed = true; }
            ImGui.SameLine();
            float strength = trail.AlphaPercent;
            ImGui.SetNextItemWidth(80f * cs);
            if (ImGui.SliderFloat("##trailalpha", ref strength, 0f, 100f, "%.0f%%")) { trail = trail with { AlphaPercent = strength }; changed = true; }
            body = body with { Trail = trail };
        }

        if (!changed) return;
        composition.Body = body;
        CreatorComposedProcs(composition.AuthoredProcs, body, out int dropped);
        if (dropped > 0)
            _creatorExportStatus = $"{stage}: a kit has four effect lanes - {dropped} did not fit (the source already uses the rest).";
        ReapPresentedEffect();
        _creatorLoopNextAt = 0;
    }

    private static uint CreatorPackRgb(Vector3 colour)
    {
        uint C(float v) => (uint)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
        return (C(colour.X) << 16) | (C(colour.Y) << 8) | C(colour.Z);
    }
}
