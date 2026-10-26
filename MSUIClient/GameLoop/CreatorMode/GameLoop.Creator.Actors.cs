using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Creator.Spells;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// ACTORS (SPELL_CREATOR_PLAN.md P4.2, decision D4).
//
// A thing that SITS at the landing point and ACTS on its own - a lingering orb that zaps, a
// storm cloud that strikes - is how Blizzard built it in 1.12: a TOTEM. The spell's
// SUMMON_TOTEM effect (87-90) plants a creature whose TotemAI casts the creature's spell at the
// nearest attackable enemy in that spell's range, again whenever it is not already casting (the
// spell's cast time paces it), for the summoning spell's duration, in its owner's faction.
//
// The workshop makes that creature for the spell: name, display, scale, and the spell it casts -
// typically a CHILD spell made here (GameLoop.Creator.Children.cs). The Completer writes the row
// at 7,900,000 + the spell's entry, cloned from Blizzard's Searing Totem so every column is a real
// totem's, and points the totem effect at it. A Core primitive (gated by the deferred-destination
// flag) plants the totem where the missile landed instead of at the caster's feet.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private sealed class CreatorActor
    {
        /// <summary>The effect slot (1-3) whose SUMMON_TOTEM names this actor.</summary>
        public int Effect;
        public string Name = "Orb";
        public uint DisplayId = 4589;          // Searing Totem's own model until the author picks one
        public float Scale = 1f;
        /// <summary>The spell it casts: a child's LOCAL id or a real spell id.</summary>
        public uint Spell;
    }

    public static bool IsTotemEffect(int type) => type is >= 87 and <= 90;

    private static readonly (string Label, uint Display)[] CreatorActorDisplays =
    {
        ("Searing Totem", 4589), ("Invisible", 1126), ("Stoneclaw Totem", 4587), ("Windfury Totem", 4590),
    };

    private void DrawCreatorActorRow(CreatorSpellDoc doc, SpellRules rules, int e, float cs)
    {
        CreatorActor actor = doc.Actor is { } existing && existing.Effect == e ? existing : null!;
        if (actor is null)
        {
            ImGui.TextDisabled("Summons a totem that is not this spell's own.");
            if (ImGui.SmallButton("+ make the actor here"))
                doc.Actor = new CreatorActor { Effect = e, Name = $"{doc.Info.Name} Actor" };
            CreatorHelp("A thing that sits where the spell lands and acts on its own - a lingering orb " +
                        "that zaps the nearest enemy every second. It is a TOTEM, exactly how the game " +
                        "builds Searing Totem: it casts its spell at the nearest enemy in that spell's " +
                        "range, again whenever the last cast ends, for this spell's Duration.");
            return;
        }
        ImGui.TextColored(SpellIdeGold, "ACTOR");
        byte[] nameBuf = new byte[64];
        System.Text.Encoding.UTF8.GetBytes(actor.Name, 0, Math.Min(actor.Name.Length, 60), nameBuf, 0);
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.7f);
        if (ImGui.InputText("Name##actor", nameBuf, (uint)nameBuf.Length)) actor.Name = BufToString(nameBuf);
        int display = (int)actor.DisplayId;
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.4f);
        if (ImGui.InputInt("Display##actor", ref display)) actor.DisplayId = (uint)Math.Max(display, 1);
        foreach ((string label, uint id) in CreatorActorDisplays)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{label}##actor-d")) actor.DisplayId = id;
        }
        float scale = actor.Scale;
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.5f);
        if (ImGui.SliderFloat("Scale##actor", ref scale, 0.2f, 5f, "%.2f")) actor.Scale = scale;

        // What it casts: a child spell (made here) or any spell id.
        CreatorChildSpell? child = doc.Children.FirstOrDefault(c => c.LocalId == actor.Spell);
        if (child is not null)
        {
            ImGui.TextDisabled("Casts");
            ImGui.SameLine();
            ImGui.TextColored(SpellIdeGold, $"child: {child.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("edit##actor-child")) EditCreatorChild(doc, doc.Children.IndexOf(child));
        }
        else
        {
            int spell = (int)actor.Spell;
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.5f);
            if (ImGui.InputInt("Casts spell##actor", ref spell)) actor.Spell = (uint)Math.Max(spell, 0);
            if (actor.Spell > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(_spellCatalog?.TryGet(actor.Spell, out SpellInfo s) == true ? s.Name : "(unknown spell)");
            }
            if (ImGui.SmallButton("+ new child spell for it"))
            {
                // The zap: a child made from zero; the actor casts it.
                NewCreatorChildFor(doc, child => actor.Spell = child.LocalId);
                return;
            }
        }
        if (ImGui.SmallButton("remove actor")) doc.Actor = null;
        CreatorHelp("It lives for this spell's Duration (TIMING). Its spell's RANGE is how far it looks for " +
                    "an enemy, its CAST TIME how often it fires.");
    }

    private static JsonObject? CreatorActorJson(CreatorActor? actor) => actor is null ? null : new JsonObject
    {
        ["effect"] = actor.Effect,
        ["name"] = actor.Name,
        ["displayId"] = actor.DisplayId,
        ["scale"] = actor.Scale,
        ["spell"] = actor.Spell,
    };

    private static CreatorActor? ApplyCreatorActorJson(JsonNode? node) => node is JsonObject o ? new CreatorActor
    {
        Effect = o["effect"]?.GetValue<int>() ?? 0,
        Name = o["name"]?.GetValue<string>() ?? "Actor",
        DisplayId = o["displayId"]?.GetValue<uint>() ?? 4589,
        Scale = o["scale"]?.GetValue<float>() ?? 1f,
        Spell = o["spell"]?.GetValue<uint>() ?? 0,
    } : null;
}
