using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Creator.Spells;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// CHILD SPELLS (SPELL_CREATOR_PLAN.md P4.1).
//
// A trigger effect (TRIGGER_MISSILE 32 / TRIGGER_SPELL 64 / a periodic-trigger aura 23) names
// another spell. In the workshop that other spell can be a CHILD: a second spell of the same
// design, made from zero and edited in place - Spice Mortar's shape (the shell lands, its child
// explodes) built without leaving the window.
//
// A child is kept as its own DESIGN (the JSON the session saves) plus its last export entry,
// inside the parent - never as a second live doc, because the workshop has exactly one live doc
// and switching docs is what SelectCreatorSpell / ApplyCreatorDesign already do faithfully.
//   edit     the parent's design is stashed, the child's design is opened (the reopen path);
//   back     the child's design and a fresh export entry go back into the stashed parent,
//            which is reopened. Nesting works the same way (a stack).
// The parent's trigger column holds the child's LOCAL id (9,000,001+). Test live completes the
// children FIRST, then rewrites those local ids to the real ones in the parent's rules before
// the parent is completed (SpellTestLiveClient) - one patch, one learn, the server triggers the
// rest.
//
// Server law (vmangos Spell.cpp, measured 2026-10-26): TRIGGER_MISSILE fires per unit target
// and casts the child AT THE PARENT'S DESTINATION; with implicit target 1 (the caster) it fires
// exactly once - so a "land, then explode" child uses effect 32, target A = 1. Under
// SPELL_CUSTOM_DEFERRED_DEST that happens at arrival. (Target 18 adds only an explicit unit
// target, which a ground cast does not have: the child would never fire.)
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private sealed class CreatorChildSpell
    {
        public uint LocalId;
        public string Name = "";
        public JsonObject Design = new();
        /// <summary>Its last export (built when the editor leaves it); what Test live pushes.</summary>
        public JsonObject? Entry;
    }

    private sealed record CreatorParentStash(JsonObject Design, Dictionary<string, byte[]> Imports,
        Dictionary<string, byte[]> Audio, int ChildIndex, string ParentName);

    private readonly Stack<CreatorParentStash> _creatorParents = new();
    private bool EditingCreatorChild => _creatorParents.Count > 0;

    public static bool IsTriggerEffect(SpellRules rules, int e) =>
        rules.EffectType(e) is 32 or 64 || (rules.EffectType(e) is 6 or 27 or 35 && rules.EffectAura(e) == 23);

    /// <summary>Make a new child for trigger effect <paramref name="effect"/> and open it.</summary>
    private void NewCreatorChild(CreatorSpellDoc doc, int effect)
    {
        if (doc.Rules is not { } rules) return;
        NewCreatorChildFor(doc, child => rules.Set(SpellRules.Column("effectTriggerSpell", effect), child.LocalId));
    }

    /// <summary>Make a new child, let <paramref name="link"/> name it (a trigger column, an actor's
    /// spell), and open it.</summary>
    private void NewCreatorChildFor(CreatorSpellDoc doc, Action<CreatorChildSpell> link)
    {
        uint localId = ++_creatorNextNewSpellId;
        int n = doc.Children.Count + 1;
        var child = new CreatorChildSpell
        {
            LocalId = localId,
            Name = $"{doc.Info.Name} {(n == 1 ? "Child" : $"Child {n}")}",
        };
        child.Design = new JsonObject
        {
            ["version"] = CreatorDesignVersion,
            ["designId"] = $"{doc.DesignId}.child{n}",
            ["spell"] = new JsonObject { ["isNew"] = true, ["id"] = localId, ["name"] = child.Name },
            ["plan"] = new JsonObject { ["started"] = false, ["stages"] = new JsonArray(), ["missile"] = false },
        };
        doc.Children.Add(child);
        link(child);
        EditCreatorChild(doc, doc.Children.Count - 1);
    }

    private void EditCreatorChild(CreatorSpellDoc doc, int index)
    {
        if (index < 0 || index >= doc.Children.Count) return;
        DesignSnapshot parent = CaptureDesignSnapshot(doc);
        if (JsonNode.Parse(parent.Json) is not JsonObject parentDesign) return;
        _creatorParents.Push(new CreatorParentStash(parentDesign, parent.Imports, parent.Audio, index, doc.Info.Name));
        CreatorChildSpell child = doc.Children[index];
        (Dictionary<string, byte[]> imports, Dictionary<string, byte[]> audio) = EntryBytes(child.Entry);
        string verdict = ApplyCreatorDesign((JsonObject)child.Design.DeepClone(), imports, audio);
        _creatorExportStatus = $"Editing child \"{child.Name}\" of \"{doc.Info.Name}\" - {verdict}";
        Console.WriteLine($"[creator-child] edit {child.Name} (#{child.LocalId}) of {doc.Info.Name}");
    }

    /// <summary>Close the child editor: its design and a fresh export go back into the parent,
    /// which is reopened.</summary>
    private void ReturnToCreatorParent()
    {
        if (_creatorParents.Count == 0 || _creatorSpell is not { } childDoc) return;
        CreatorParentStash stash = _creatorParents.Pop();
        JsonObject childDesign = CaptureCreatorDesign(childDoc);
        JsonObject? childEntry = BuildCreatorSpellEntry(childDoc, childDoc.Info.Name);
        if (stash.Design["children"] is JsonArray children && stash.ChildIndex < children.Count &&
            children[stash.ChildIndex] is JsonObject slot)
        {
            slot["name"] = childDoc.Info.Name;
            slot["design"] = childDesign;
            slot["entry"] = childEntry;
        }
        string verdict = ApplyCreatorDesign(stash.Design, stash.Imports, stash.Audio);
        _creatorExportStatus = $"Back to \"{stash.ParentName}\" - {verdict}";
        Console.WriteLine($"[creator-child] back to {stash.ParentName} (child entry {(childEntry is null ? "none - nothing in it yet" : "built")})");
    }

    /// <summary>An export entry's imported images and audio, as the reopen path wants them.</summary>
    private static (Dictionary<string, byte[]> Imports, Dictionary<string, byte[]> Audio) EntryBytes(JsonObject? entry)
    {
        var imports = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var audio = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (entry?["tintedBlps"] is JsonArray blps)
            foreach (JsonNode? blp in blps)
                if (blp?["reason"]?.GetValue<string>() == "import" &&
                    blp["path"]?.GetValue<string>() is { Length: > 0 } path &&
                    blp["blpBase64"]?.GetValue<string>() is { Length: > 0 } b64)
                    imports[path] = Convert.FromBase64String(b64);
        if (entry?["audio"] is JsonArray tracks)
            foreach (JsonNode? track in tracks)
                if (track?["cue"]?.GetValue<string>() is { Length: > 0 } cue &&
                    track["fileBase64"]?.GetValue<string>() is { Length: > 0 } b64)
                    audio[cue] = Convert.FromBase64String(b64);
        return (imports, audio);
    }

    // ── the design block's "children" ──

    private static JsonArray CaptureCreatorChildren(CreatorSpellDoc doc) => new(doc.Children.Select(c => (JsonNode)new JsonObject
    {
        ["localId"] = c.LocalId,
        ["name"] = c.Name,
        ["design"] = c.Design.DeepClone(),
        ["entry"] = c.Entry?.DeepClone(),
    }).ToArray());

    private void ApplyCreatorChildren(CreatorSpellDoc doc, JsonNode? node)
    {
        doc.Children.Clear();
        if (node is not JsonArray children) return;
        foreach (JsonNode? c in children)
        {
            if (c is not JsonObject child || child["design"] is not JsonObject design) continue;
            uint localId = child["localId"]?.GetValue<uint>() ?? 0;
            if (localId > _creatorNextNewSpellId) _creatorNextNewSpellId = localId;
            doc.Children.Add(new CreatorChildSpell
            {
                LocalId = localId,
                Name = child["name"]?.GetValue<string>() ?? "child",
                Design = (JsonObject)design.DeepClone(),
                Entry = child["entry"] is JsonObject entry ? (JsonObject)entry.DeepClone() : null,
            });
        }
    }

    // ── the Rules card's trigger row ──

    private void DrawCreatorTriggerRow(CreatorSpellDoc doc, SpellRules rules, int e, float cs)
    {
        string column = SpellRules.Column("effectTriggerSpell", e);
        uint trigger = (uint)Math.Max(rules.GetInt(column), 0);
        CreatorChildSpell? child = doc.Children.FirstOrDefault(c => c.LocalId == trigger);
        if (child is not null)
        {
            ImGui.TextDisabled("Triggers");
            ImGui.SameLine();
            ImGui.TextColored(SpellIdeGold, $"child: {child.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("edit##child")) EditCreatorChild(doc, doc.Children.IndexOf(child));
            ImGui.SameLine();
            if (ImGui.SmallButton("unlink##child")) rules.Set(column, 0);
            CreatorHelp("This effect casts the child spell - its own rules, sketches and sounds, made here. " +
                        "Test live completes it first and links the real id.");
            return;
        }
        int value = (int)trigger;
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        if (ImGui.InputInt("Trigger spell", ref value)) rules.Set(column, Math.Max(value, 0));
        if (trigger > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_spellCatalog?.TryGet(trigger, out SpellInfo t) == true ? t.Name : "(unknown spell)");
        }
        if (IsTriggerEffect(rules, e))
        {
            if (ImGui.SmallButton("+ new child spell")) NewCreatorChild(doc, e);
            CreatorHelp("Make the spell this effect casts, here, from zero: the ball that lands and its " +
                        "explosion, a bolt that leaves a fire behind. For 'land, then explode' use TRIGGER " +
                        "MISSILE with Target A = 1 (caster): it fires once, at the landing point.");
            foreach (CreatorChildSpell other in doc.Children.Where(c => c.LocalId != trigger))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"use {other.Name}##use-{other.LocalId}")) rules.Set(column, other.LocalId);
            }
        }
    }

    /// <summary>The bar at the top of the IDE while a child is open.</summary>
    private void DrawCreatorChildBar()
    {
        if (!EditingCreatorChild || _creatorSpell is not { } doc) return;
        CreatorParentStash stash = _creatorParents.Peek();
        ImGui.TextColored(SpellIdeGold, $"CHILD of \"{stash.ParentName}\":");
        ImGui.SameLine();
        ImGui.TextUnformatted(doc.Info.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton($"< back to {stash.ParentName}")) ReturnToCreatorParent();
    }
}
