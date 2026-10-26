using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// THE AREA PART (shared_docs/SPELL_CREATOR_PLAN.md P2.5): what stays on the ground while a
// spell's DynamicObject lives - Blizzard's falling shards, Consecration's glow, a lightning
// field. In 1.12 it is three fields of SpellVisual: 11 (the gate), 12 (the looping centre model)
// and 13 (the area kit, whose type-9 CharProc names one of the client's SEVEN literal shard
// models and how many fall per second over the radius - SpellAreaVisualLaw). The live client
// plays it from the server's DynamicObject; the workshop plays the same machinery at the
// preview spot, sized by the rules' radius, for as long as "Area" is held in the Loop row.
//
// Any model can be the centre (a Blizzard art piece, or a Sketch); the shards are the client's
// own table - a custom shard model is not something a stock 1.12 client can play.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private sealed class CreatorAreaComposition
    {
        public string? Model;
        public string? AuthoredModel;
        public float Scale = 1f;
        public int ShardSelector = -1;
        public int AuthoredShardSelector = -1;
        public float ShardRate;
        public float AuthoredShardRate;
        /// <summary>A SoundEntries id picked for the area kit (f13), P3.9.</summary>
        public uint? Sound;

        public bool Changed =>
            !string.Equals(Model ?? "", AuthoredModel ?? "", StringComparison.OrdinalIgnoreCase) ||
            MathF.Abs(Scale - 1f) > 1e-4f || ShardSelector != AuthoredShardSelector ||
            (ShardSelector >= 0 && MathF.Abs(ShardRate - AuthoredShardRate) > 1e-3f) || Sound is not null;

        public bool Any => Model is { Length: > 0 } || ShardSelector >= 0;

        public void Reset()
        {
            Model = AuthoredModel;
            Scale = 1f;
            ShardSelector = AuthoredShardSelector;
            ShardRate = AuthoredShardRate;
            Sound = null;
        }
    }

    private static readonly string[] CreatorShardNames = SpellAreaVisualLaw.ClientShardModels
        .Select((path, i) => $"{i} {Path.GetFileNameWithoutExtension(path)}").Prepend("none").ToArray();

    /// <summary>Read the source's area (called with the stage composition).</summary>
    private void InitCreatorArea(CreatorSpellDoc doc)
    {
        var area = new CreatorAreaComposition();
        if (_spellVisualCatalog?.TryGetAreaVisual(doc.Info.VisualId, out SpellAreaVisualInfo authored) == true)
        {
            area.AuthoredModel = authored.LoopingModelPath;
            if (authored.Emitters.Count > 0)
            {
                area.AuthoredShardSelector = authored.Emitters[0].Selector;
                area.AuthoredShardRate = authored.Emitters[0].InstancesPerSecond;
            }
        }
        area.Reset();
        doc.Area = area;
    }

    /// <summary>The area the preview plays: the composition's, with the source's sound.</summary>
    private bool TryCreatorComposedArea(CreatorSpellDoc doc, out SpellAreaVisualInfo area)
    {
        uint? sound = _spellVisualCatalog?.TryGetAreaVisual(doc.Info.VisualId, out SpellAreaVisualInfo authored) == true
            ? authored.Sound : null;
        var emitters = new List<SpellAreaEmitterInfo>(1);
        if (doc.Area.ShardSelector >= 0 && doc.Area.ShardSelector < SpellAreaVisualLaw.ClientShardModels.Count)
            emitters.Add(new SpellAreaEmitterInfo(doc.Area.ShardSelector,
                SpellAreaVisualLaw.ClientShardModels[doc.Area.ShardSelector], MathF.Max(doc.Area.ShardRate, 0.1f)));
        area = new SpellAreaVisualInfo(doc.Area.Model is { Length: > 0 } m ? m : null, emitters, sound);
        return area.LoopingModelPath is not null || emitters.Count > 0 || sound is not null;
    }

    /// <summary>The radius the area covers in the preview: the largest effect radius in the rules.</summary>
    private float CreatorAreaPreviewRadius(CreatorSpellDoc doc)
    {
        float radius = 0f;
        if (doc.Rules is { } rules && SpellRuleTablesLoaded is { } tables)
            for (int e = 1; e <= 3; e++)
                if (tables.Radii.TryGetValue((uint)rules.GetInt(Creator.Spells.SpellRules.Column("effectRadiusIndex", e)), out var r))
                    radius = MathF.Max(radius, r.Yards);
        return radius > 0f ? radius : _creatorAreaRadius;
    }

    private void DrawCreatorAreaCompositionBody(CreatorSpellDoc doc, float cs)
    {
        CreatorAreaComposition area = doc.Area;
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "AREA (what stays on the ground)" + (area.Changed ? "  *" : ""));
        if (area.Changed)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset##area")) { area.Reset(); RestartCreatorArea(); }
        }
        ImGui.TextDisabled("Centre");
        ImGui.SameLine(130f * cs);
        if (area.Model is { Length: > 0 } model) ImGui.TextUnformatted(Path.GetFileName(model));
        else ImGui.TextDisabled("(none)");
        ImGui.SameLine();
        if (ImGui.SmallButton("pick##area")) OpenCreatorModelPicker(null, CreatorAreaPickerSlot);
        if (area.Model is { Length: > 0 })
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("clear##area")) { area.Model = null; RestartCreatorArea(); }
            float scale = area.Scale;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Centre scale##area", ref scale, 0.1f, 5f, "x%.2f")) { area.Scale = scale; RestartCreatorArea(); }
        }
        int shard = area.ShardSelector + 1;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.Combo("Shards##area", ref shard, CreatorShardNames, CreatorShardNames.Length))
        {
            area.ShardSelector = shard - 1;
            if (area.ShardSelector >= 0 && area.ShardRate <= 0f) area.ShardRate = 6f;
            RestartCreatorArea();
        }
        if (area.ShardSelector >= 0)
        {
            float rate = area.ShardRate;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Shards / s##area", ref rate, 0.5f, 40f, "%.1f")) { area.ShardRate = rate; RestartCreatorArea(); }
        }
        CreatorHelp("The lasting part of a ground spell: a looping CENTRE model (any effect model, or a sketch) and " +
                    "SHARDS scattered over the radius - one of the seven the 1.12 client knows (Blizzard's ice, Rain " +
                    "of Fire's flame, Call Lightning's bolt ...). It plays while the server's area lives (a persistent " +
                    "area aura); in the workshop, tick Area in the Loop row. Its size is the rules' radius.");
    }

    /// <summary>Does the area wait for the missile? A deferred ground missile places its lasting area
    /// on arrival; anything else has it up from the cast.</summary>
    private bool CreatorAreaWaitsForArrival(CreatorSpellDoc doc) =>
        doc.Rules is { } r && r.Deferred && r.GroundTargeted && r.Speed > 0f && _creatorLoopMissilePhase;

    /// <summary>How long the previewed area lives: the rules' duration row, else 6 s.</summary>
    private double CreatorAreaPreviewSeconds(CreatorSpellDoc doc)
    {
        if (doc.Rules is { } r && SpellRuleTablesLoaded is { } t &&
            t.Durations.TryGetValue((uint)r.GetInt("durationIndex"), out var d) && d.BaseMs > 0)
            return d.BaseMs / 1000.0;
        return 6.0;
    }

    /// <summary>The model picker's slot code for "the area centre" (beside -1 = the missile).</summary>
    private const int CreatorAreaPickerSlot = -2;

    private void RestartCreatorArea()
    {
        if (!_creatorAreaActive) return;
        _spellEffects?.ReapArea(CreatorAreaGuid);
        _spellSounds?.StopHold(CreatorAreaGuid);
        StopCreatorAudio(CreatorAudioCue.Area);
        _creatorAreaActive = false;
    }

    private static JsonObject? CreatorAreaJson(CreatorSpellDoc doc) => doc.Area.Changed
        ? new JsonObject
        {
            ["modelPath"] = doc.Area.Model ?? "",
            ["scale"] = doc.Area.Scale,
            ["shardSelector"] = doc.Area.ShardSelector,
            ["shardRate"] = doc.Area.ShardRate,
            ["sound"] = doc.Area.Sound is { } sound ? sound : null,
        }
        : null;

    private static void ApplyCreatorAreaJson(CreatorSpellDoc doc, JsonNode? node)
    {
        if (node is not JsonObject o) return;
        doc.Area.Model = o["modelPath"]?.GetValue<string>() is { Length: > 0 } m ? m : null;
        doc.Area.Scale = o["scale"]?.GetValue<float>() ?? 1f;
        doc.Area.ShardSelector = o["shardSelector"]?.GetValue<int>() ?? -1;
        doc.Area.ShardRate = o["shardRate"]?.GetValue<float>() ?? 0f;
        doc.Area.Sound = o["sound"]?.GetValue<uint>() is uint sound && sound != 0 ? sound : null;
    }
}
