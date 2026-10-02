using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Creator (shared_docs/WORLD_BUILDER.md §5). Everything a working quest needs goes out as ONE
// audited op built by QuestAuthoringLaw: the quest row, giver/turn-in relations, quest-only drop loot,
// exploration triggers, and the quest-giver flag + gossip option on pack NPCs that lack them. After a
// save the pack's pre-flight runs and this quest's findings show here - the same check a publish runs.
//
// Script: questui <draft.json>  (loads a draft into the panel and saves it through the same path)
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private QuestAuthoringLaw.Draft _wbQuest = new();
    private uint _wbQuestEditing;                         // 0 = a new quest
    private readonly byte[] _wbQuestItemSearch = new byte[64];
    private string _wbQuestItemSearchLast = "\u0001";
    private List<CreatorItemTable.Item>? _wbQuestItemHits;
    private int _wbQuestItemTarget = -1;                   // 0..3 objective, 10..15 choice, 20..23 fixed
    private readonly List<string> _wbQuestFindings = new();
    private uint _wbQuestCheckEntry;
    private int _wbQuestPack = -1;
    private readonly Dictionary<int, (QuestAuthoringLaw.Draft Draft, uint Editing)> _wbQuestDrafts = new();
    private readonly Dictionary<(int Pack, uint Entry), QuestAuthoringLaw.Draft> _wbQuestWorkingDrafts = new();
    private readonly Dictionary<string, string> _wbQuestNpcSearch = new();
    private Task<WorldPackClient.Reply>? _wbQuestSaveTask;
    private string _wbQuestSaveStatus = "";
    private bool _wbQuestItemPopupOpened;
    private bool _wbQuestItemsAttempted;
    private int _wbQuestCheckPack;
    private int _wbQuestSavingPack;
    private static readonly string[] WbObjectiveKinds = { "(none)", "Kill creature", "Collect item", "Use object", "Explore here" };

    private void DrawWbQuestSection()
    {
        float w = CreatorControlWidth;
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("loading pack content..."); return; }
        if (_wbQuestPack != _wbPackId)
        {
            if (_wbQuestPack > 0) _wbQuestDrafts[_wbQuestPack] = (_wbQuest, _wbQuestEditing);
            _wbQuestPack = _wbPackId;
            var saved = _wbQuestDrafts.GetValueOrDefault(_wbPackId, (new QuestAuthoringLaw.Draft(), 0u));
            _wbQuest = saved.Item1; _wbQuestEditing = saved.Item2;
            _wbQuestFindings.Clear(); _wbQuestSaveStatus = ""; _wbQuestItemTarget = -1;
        }
        ImGui.TextWrapped("Choose who offers the quest, what the player does, and what they receive. Save stores a draft in this pack; publish applies it to the world.");

        // ── which quest ────────────────────────────────────────────────────
        var quests = WbDocBodies("dbrow:quest_template").OrderBy(q => WbNum(q["entry"])).ToList();
        string current = _wbQuestEditing == 0 ? "New quest" : _wbQuest.Title;
        ImGui.SetNextItemWidth(w);
        bool questOpen = ImGui.BeginCombo("Quest##wbq-pick", current);
        ObserveWorldBuilderUiItem("Quest picker");
        if (questOpen)
        {
            if (ImGui.Selectable("Start a new quest", _wbQuestEditing == 0))
            {
                _wbQuestWorkingDrafts[(_wbPackId, _wbQuestEditing)] = _wbQuest;
                _wbQuest = _wbQuestWorkingDrafts.GetValueOrDefault((_wbPackId, 0u), new QuestAuthoringLaw.Draft());
                _wbQuestEditing = 0; _wbQuestSaveStatus = "Local drafts are kept while you switch between quests."; _wbQuestFindings.Clear();
            }
            foreach (var q in quests)
            {
                if (ImGui.Selectable($"{(string?)q["Title"]}##wbq{WbNum(q["entry"])}", WbNum(q["entry"]) == _wbQuestEditing))
                    WbLoadQuestDraft(q);
                ObserveWorldBuilderUiItem($"Quest: {q["Title"]}");
            }
            ImGui.EndCombo();
        }
        var d = _wbQuest; // A selection above changes the draft in this same frame.

        // ── text ───────────────────────────────────────────────────────────
        if (WbQuestHeader("1. Story and level##wbq", true))
        {
        string title = d.Title; ImGui.SetNextItemWidth(w); if (ImGui.InputText("Title##wbq", ref title, 120)) d.Title = title;
        ObserveWorldBuilderUiItem("Quest title");
        ImGui.TextDisabled("What the quest giver says");
        string details = d.Details; if (ImGui.InputTextMultiline("##wbq-details", ref details, 2000, new Vector2(-1f, 70f * CreatorUiScale))) d.Details = details;
        string obj = d.Objectives; ImGui.SetNextItemWidth(w); if (ImGui.InputText("Objectives##wbq", ref obj, 600)) d.Objectives = obj;
        ObserveWorldBuilderUiItem("Quest objectives text");
        string req = d.RequestText; ImGui.SetNextItemWidth(w); if (ImGui.InputText("While incomplete##wbq", ref req, 600)) d.RequestText = req;
        string done = d.CompleteText; ImGui.SetNextItemWidth(w); if (ImGui.InputText("On turn-in##wbq", ref done, 1000)) d.CompleteText = done;
        int lvl = d.Level, min = d.MinLevel;
        ImGui.SetNextItemWidth(w * 0.5f); if (ImGui.InputInt("Level##wbq", ref lvl, 0)) d.Level = Math.Clamp(lvl, 1, 63);
        ImGui.SetNextItemWidth(w * 0.5f); if (ImGui.InputInt("Minimum level##wbq", ref min, 0)) d.MinLevel = Math.Clamp(min, 1, 63);
        d.Zone = WbQuestAreaPicker(d.Zone);
        if (ImGui.TreeNode("Text shortcuts##wbq"))
        { ImGui.TextWrapped("$N = player name, $C = class, $R = race, $B = new line. Use the player preview below to check your text."); ImGui.TreePop(); }
        }

        // ── who ────────────────────────────────────────────────────────────
        if (WbQuestHeader("2. Quest giver and return##wbq", true))
        {
        if (!WbDocBodies("dbrow:creature_template").Any())
        {
            ImGui.TextWrapped("This pack has no NPCs yet. Create and place an NPC, then choose them here.");
            if (CreatorButton("Create an NPC##wbq")) WbOpenPage("npcs");
        }
        d.Giver = WbNpcPicker("Given by", d.Giver, "wbq-giver");
        bool same = d.Ender == 0;
        if (ImGui.Checkbox("Return to the quest giver##wbq", ref same)) d.Ender = same ? 0 : d.Giver;
        if (!same) d.Ender = WbNpcPicker("Return to", d.Ender, "wbq-ender");
        ImGui.TextWrapped("Saving adds the quest-giver flag and conversation option to this pack's NPCs when needed.");
        }

        // ── objectives ─────────────────────────────────────────────────────
        if (WbQuestHeader("3. Objectives##wbq", true))
        {
        ImGui.TextWrapped("Add a kill, an item to collect, an object to use, or a place to discover. Item drops can be attached to creatures in this pack.");
        int empty = Array.FindIndex(d.Goals, g => g.Kind == QuestAuthoringLaw.ObjectiveKind.None);
        for (int i = 0; i < d.Goals.Length; i++)
        {
            var g = d.Goals[i];
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.None && i != empty) continue;
            ImGui.PushID($"wbq-goal{i}");
            int kind = (int)g.Kind;
            ImGui.SetNextItemWidth(w);
            if (ImGui.Combo($"{i + 1}", ref kind, WbObjectiveKinds, WbObjectiveKinds.Length)) g.Kind = (QuestAuthoringLaw.ObjectiveKind)kind;
            ObserveWorldBuilderUiItem($"Objective {i + 1} kind");
            int count = g.Count;
            if (g.Kind is QuestAuthoringLaw.ObjectiveKind.Kill or QuestAuthoringLaw.ObjectiveKind.Collect or QuestAuthoringLaw.ObjectiveKind.UseObject)
            { ImGui.SetNextItemWidth(w * 0.5f); if (ImGui.InputInt("Required amount##count", ref count, 0)) g.Count = Math.Clamp(count, 1, 255); }
            switch (g.Kind)
            {
                case QuestAuthoringLaw.ObjectiveKind.Kill:
                    g.Target = WbNpcPicker("Creature", g.Target, $"wbq-kill{i}"); break;
                case QuestAuthoringLaw.ObjectiveKind.UseObject:
                {
                    g.Target = WbQuestObjectPicker(g.Target);
                    break;
                }
                case QuestAuthoringLaw.ObjectiveKind.Collect:
                    ImGui.TextUnformatted(g.Item == 0 ? "item: (none)" : $"item: {WbItemName(g.Item)} ({g.Item})");
                    if (ImGui.SmallButton("Choose item")) _wbQuestItemTarget = i;
                    g.DropFrom = WbNpcPicker("Drops from (pack creature)", g.DropFrom, $"wbq-drop{i}");
                    if (g.DropFrom != 0)
                    {
                        float chance = g.DropChance; ImGui.SetNextItemWidth(w * 0.6f);
                        if (ImGui.InputFloat("% (quest-only)", ref chance, 0f, 0f, "%.1f")) g.DropChance = Math.Clamp(chance, 1f, 100f);
                    }
                    break;
                case QuestAuthoringLaw.ObjectiveKind.Explore:
                    if (ImGui.SmallButton("Use my current position") && _controller is not null)
                    { g.ExploreMap = _config.Start.Map; g.ExploreX = _controller.Position.X; g.ExploreY = _controller.Position.Y; g.ExploreZ = _controller.Position.Z; }
                    ObserveWorldBuilderUiItem("Use my current position");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(Inv($"map {g.ExploreMap} ({g.ExploreX:F0}, {g.ExploreY:F0}, {g.ExploreZ:F0})"));
                    float r = g.ExploreRadius; ImGui.SetNextItemWidth(w * 0.6f);
                    if (ImGui.InputFloat("radius", ref r, 0f, 0f, "%.1f")) g.ExploreRadius = Math.Clamp(r, 2f, 100f);
                    break;
            }
            if (g.Kind is QuestAuthoringLaw.ObjectiveKind.Kill or QuestAuthoringLaw.ObjectiveKind.UseObject)
            { string t = g.Text; ImGui.SetNextItemWidth(w); if (ImGui.InputText("quest log line (optional)", ref t, 200)) g.Text = t; }
            ImGui.PopID();
        }
        if (empty < 0 && d.Goals.Length < 9 && CreatorButton("Add another objective##wbq")) d.Goals = d.Goals.Append(new QuestAuthoringLaw.Objective()).ToArray();
        }

        // ── rewards ────────────────────────────────────────────────────────
        if (WbQuestHeader("4. Rewards##wbq"))
        {
        int xp = d.Xp, gold = Math.Max(d.Money, 0) / 10000, silver = Math.Max(d.Money, 0) / 100 % 100, copper = Math.Max(d.Money, 0) % 100;
        ImGui.SetNextItemWidth(w * 0.6f); if (ImGui.InputInt("XP##wbq", ref xp, 0)) d.Xp = Math.Max(xp, 0);
        ObserveWorldBuilderUiItem("Quest XP");
        bool moneyChanged = false;
        ImGui.SetNextItemWidth(w * 0.6f); moneyChanged |= ImGui.InputInt("Gold##wbq", ref gold, 0);
        ImGui.SetNextItemWidth(w * 0.6f); moneyChanged |= ImGui.InputInt("Silver##wbq", ref silver, 0);
        ImGui.SetNextItemWidth(w * 0.6f); moneyChanged |= ImGui.InputInt("Copper##wbq", ref copper, 0);
        if (moneyChanged) d.Money = (int)Math.Clamp(Math.Max(0L, gold) * 10000 + Math.Clamp(silver, 0, 99) * 100L + Math.Clamp(copper, 0, 99), 0, int.MaxValue);
        WbRewardRow("Choose one of", d.Choices, 10);
        WbRewardRow("Always get", d.Fixed, 20);
        if (ImGui.TreeNode("Reputation reward (optional)##wbq"))
        {
        int rv = d.RepValue;
        d.RepFaction = WbQuestFactionPicker(d.RepFaction);
        if (d.RepFaction != 0) { ImGui.SetNextItemWidth(w * 0.6f); if (ImGui.InputInt("Reputation##wbq", ref rv, 0)) d.RepValue = rv; }
        ImGui.TreePop();
        }
        }

        // ── chain ──────────────────────────────────────────────────────────
        if (WbQuestHeader("5. Prerequisites and quest chain##wbq"))
        {
        ImGui.TextWrapped("Optional: choose the quest the player must finish first. The next quest is offered after this one is completed.");
        d.PrevQuest = WbQuestPicker("Requires completing", d.PrevQuest, quests, "wbq-prev");
        d.NextInChain = WbQuestPicker("Next in chain", d.NextInChain, quests, "wbq-next");
        if (d.PrevQuest != 0)
        {
            bool active = d.PrevQuest < 0;
            if (ImGui.Checkbox("Prerequisite must be active instead of completed", ref active)) d.PrevQuest = active ? -Math.Abs(d.PrevQuest) : Math.Abs(d.PrevQuest);
        }
        if (ImGui.TreeNode("Advanced chain settings##wbq"))
        { int eg = d.ExclusiveGroup; ImGui.SetNextItemWidth(w * 0.6f); if (ImGui.InputInt("Exclusive group##wbq", ref eg, 0)) d.ExclusiveGroup = eg; ImGui.TreePop(); }
        }

        // ── item picker (shared by objectives and rewards) ─────────────────
        if (_wbQuestItemTarget >= 0 && !_wbQuestItemPopupOpened) { ImGui.OpenPopup("Choose an item##wbq"); _wbQuestItemPopupOpened = true; }
        ImGui.SetNextWindowSize(new Vector2(440f, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("Choose an item##wbq"))
        {
            WbEnsureQuestItems();
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("find item##wbq-item", _wbQuestItemSearch, (uint)_wbQuestItemSearch.Length);
            string iq = WbText(_wbQuestItemSearch);
            if (_wbItems is null)
            { ImGui.TextWrapped("The item catalogue is unavailable (creator-items.tsv). Add the catalogue to search items by name."); if (ImGui.SmallButton("Retry catalogue")) { _wbQuestItemsAttempted = false; _wbQuestItemSearchLast = "\u0001"; } }
            else if (iq.Length < 2) ImGui.TextDisabled("Type at least two letters of the item name.");
            if (iq != _wbQuestItemSearchLast) { _wbQuestItemSearchLast = iq; _wbQuestItemHits = iq.Length >= 2 ? _wbItems?.Search(iq, -2, 30) : null; }
            if (iq.Length >= 2 && _wbQuestItemHits is { Count: 0 }) ImGui.TextDisabled("No matching items. Try a shorter name.");
            if (_wbQuestItemHits is { Count: > 0 } hits && ImGui.BeginListBox("##wbq-item-hits", new Vector2(-1f, 100f * CreatorUiScale)))
            {
                foreach (var it in hits)
                    if (ImGui.Selectable($"{it.Name}##wbqi{it.Entry}")) { WbSetQuestItem(_wbQuestItemTarget, it.Entry); _wbQuestItemTarget = -1; ImGui.CloseCurrentPopup(); }
                ImGui.EndListBox();
            }
            if (ImGui.SmallButton("Cancel##wbq-item")) { _wbQuestItemTarget = -1; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        else if (_wbQuestItemPopupOpened) { _wbQuestItemTarget = -1; _wbQuestItemPopupOpened = false; }

        // ── preview ────────────────────────────────────────────────────────
        ImGui.Separator();
        if (ImGui.TreeNode("Preview (as the player reads it)##wbq-prev"))
        {
            ImGui.TextWrapped(d.Title);
            ImGui.TextWrapped(QuestAuthoringLaw.Preview(d.Details));
            ImGui.TextDisabled("Quest Objectives");
            ImGui.TextWrapped(QuestAuthoringLaw.Preview(d.Objectives));
            ImGui.TextDisabled("On turn-in");
            ImGui.TextWrapped(QuestAuthoringLaw.Preview(d.CompleteText));
            ImGui.TreePop();
        }

        // ── save ───────────────────────────────────────────────────────────
        var problems = QuestAuthoringLaw.Problems(d, WbQuestContext());
        foreach (var p in problems) ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), p);
        bool saving = _wbQuestSaveTask is { IsCompleted: false };
        bool ready = _wbPackId != 0 && problems.Count == 0 && !saving;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton(saving ? "Saving quest..." : _wbQuestEditing == 0 ? "Create quest" : "Save quest changes")) WbSaveQuest();
        if (!ready) ImGui.EndDisabled();
        if (_wbQuestSaveStatus.Length > 0) ImGui.TextWrapped(_wbQuestSaveStatus);
        foreach (var f in _wbQuestFindings) ImGui.TextWrapped(f);
    }

    private bool WbQuestHeader(string label, bool open = false)
    {
        bool expanded = ImGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ObserveWorldBuilderUiItem(label);
        return expanded;
    }

    private IEnumerable<JsonObject> WbDocBodies(string kind) =>
        (_wbDocs ?? new JsonArray()).OfType<JsonObject>().Where(x => (int?)x["packId"] == _wbPackId && (string?)x["kind"] == kind && x["body"] is JsonObject).Select(x => (JsonObject)x["body"]!);

    private static long WbNum(JsonNode? n) => n is JsonValue v && long.TryParse(WbScalar(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;

    private string WbItemName(uint entry)
    {
        WbEnsureQuestItems();
        return _wbItems?.Search(entry.ToString(CultureInfo.InvariantCulture), -2, 1) is { Count: > 0 } hit ? hit[0].Name : "item";
    }

    private void WbEnsureQuestItems()
    {
        if (_wbItems is not null || _wbQuestItemsAttempted) return;
        _wbQuestItemsAttempted = true; _wbItems = CreatorItemTable.Load(_config.RepoRoot);
    }

    private int WbQuestFactionPicker(int value)
    {
        string shown = value == 0 ? "No reputation reward" : _factionCatalog?.TryGetName((uint)value, out string? n) == true ? n : $"Faction {value}";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Faction##wbq-rep", shown))
        {
            if (ImGui.Selectable("No reputation reward", value == 0)) value = 0;
            if (_factionCatalog is { } catalog)
                for (int i = 0; i < 64; i++)
                    if (catalog.TryGetByReputationIndex(i, out var faction) && ImGui.Selectable($"{faction.Name}##wbqrep{faction.Id}", faction.Id == value)) value = (int)faction.Id;
            ImGui.EndCombo();
        }
        if (ImGui.TreeNode("Advanced faction entry##wbq"))
        { ImGui.SetNextItemWidth(CreatorControlWidth * .6f); ImGui.InputInt("Faction ID##wbq", ref value, 0); value = Math.Max(0, value); ImGui.TreePop(); }
        return value;
    }

    /// <summary>Pack creature picker + manual entry + "my target".</summary>
    private uint WbNpcPicker(string label, uint value, string id)
    {
        float w = CreatorControlWidth;
        var npcs = WbDocBodies("dbrow:creature_template").ToList();
        string shown = value == 0 ? "Choose an NPC" : npcs.FirstOrDefault(n => WbNum(n["entry"]) == value) is { } t ? (string?)t["name"] ?? "Unnamed NPC" : $"Other creature ({value})";
        ImGui.SetNextItemWidth(w);
        bool expanded = ImGui.BeginCombo($"{label}##{id}", shown);
        ObserveWorldBuilderUiItem(label);
        if (expanded)
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            string search = _wbQuestNpcSearch.GetValueOrDefault(id, "");
            ImGui.SetNextItemWidth(w);
            if (ImGui.InputText($"Search by name##{id}", ref search, 80)) _wbQuestNpcSearch[id] = search;
            foreach (var n in npcs.Where(n => ((string?)n["name"] ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)).OrderBy(n => (string?)n["name"]))
            {
                if (ImGui.Selectable($"{(string?)n["name"]}##{id}{WbNum(n["entry"])}", WbNum(n["entry"]) == value)) value = (uint)WbNum(n["entry"]);
                ObserveWorldBuilderUiItem($"{label}: {n["name"]}");
            }
            ImGui.EndCombo();
        }
        uint selected = _wbNpcEditEntry;
        if (selected == 0 && _entities.TryGet(_selectionGuid, out var sel) && sel.IsCreature) selected = sel.Entry;
        if (selected != 0 && ImGui.SmallButton($"Use selected NPC##{id}")) value = selected;
        if (ImGui.TreeNode($"Advanced NPC entry##{id}"))
        { int manual = (int)value; ImGui.SetNextItemWidth(w * 0.6f); if (ImGui.InputInt($"Entry##{id}", ref manual, 0)) value = (uint)Math.Max(manual, 0); ImGui.TreePop(); }
        return value;
    }

    private int WbQuestAreaPicker(int value)
    {
        var areas = WbDocBodies("dbrow:area_template").OrderBy(a => (string?)a["name"]).ToList();
        string name = areas.FirstOrDefault(a => WbNum(a["entry"]) == value)?["name"]?.ToString() ?? _areas?.ZoneName((uint)Math.Max(value, 0)) ?? "";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Quest log zone##wbq", value == 0 ? "Unsorted" : name.Length > 0 ? name : $"Area {value}"))
        {
            if (ImGui.Selectable("Unsorted", value == 0)) value = 0;
            foreach (var a in areas)
                if (ImGui.Selectable($"{a["name"]}##wbq-area{a["entry"]}", WbNum(a["entry"]) == value)) value = (int)WbNum(a["entry"]);
            ImGui.EndCombo();
        }
        if (ImGui.TreeNode("Other zone or category##wbq"))
        { ImGui.SetNextItemWidth(CreatorControlWidth * .6f); ImGui.InputInt("Area/category ID##wbq", ref value, 0); ImGui.TreePop(); }
        return value;
    }

    private uint WbQuestObjectPicker(uint value)
    {
        var objects = WbDocBodies("dbrow:gameobject_template").OrderBy(o => (string?)o["name"]).ToList();
        string name = objects.FirstOrDefault(o => WbNum(o["entry"]) == value)?["name"]?.ToString() ?? $"Object {value}";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Object##wbq", value == 0 ? "Choose an object" : name))
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            foreach (var o in objects) if (ImGui.Selectable($"{o["name"]}##wbq-object{o["entry"]}", WbNum(o["entry"]) == value)) value = (uint)WbNum(o["entry"]);
            ImGui.EndCombo();
        }
        if (objects.Count == 0) ImGui.TextWrapped("No objects are authored in this pack yet. Advanced entry can reference an existing world object.");
        if (ImGui.TreeNode("Advanced object entry##wbq"))
        { int entry = (int)value; ImGui.SetNextItemWidth(CreatorControlWidth * .6f); if (ImGui.InputInt("Entry##wbq-object", ref entry, 0)) value = (uint)Math.Max(0, entry); ImGui.TreePop(); }
        return value;
    }

    private int WbQuestPicker(string label, int value, List<JsonObject> quests, string id)
    {
        string shown = value == 0 ? "(none)" : quests.FirstOrDefault(q => WbNum(q["entry"]) == Math.Abs(value)) is { } q2 ? $"{(string?)q2["Title"]} ({value})" : value.ToString(CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        if (ImGui.BeginCombo($"{label}##{id}", shown))
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            foreach (var q in quests)
                if (WbNum(q["entry"]) != _wbQuest.Entry && ImGui.Selectable($"{(string?)q["Title"]}##{id}{WbNum(q["entry"])}", WbNum(q["entry"]) == Math.Abs((long)value))) value = (int)WbNum(q["entry"]);
            ImGui.EndCombo();
        }
        return value;
    }

    private void WbRewardRow(string label, (uint Item, int Count)[] slots, int targetBase)
    {
        ImGui.TextUnformatted(label);
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Item == 0 && Array.FindIndex(slots, s => s.Item == 0) != i) continue;
            ImGui.PushID($"{label}{i}");
            ImGui.TextUnformatted(slots[i].Item == 0 ? $"  {i + 1}: -" : $"  {i + 1}: {WbItemName(slots[i].Item)} x{Math.Max(1, slots[i].Count)}");
            ImGui.SameLine(); if (ImGui.SmallButton("set")) _wbQuestItemTarget = targetBase + i;
            if (slots[i].Item != 0)
            {
                ImGui.SameLine(); if (ImGui.SmallButton("Remove")) slots[i] = (0, 0);
                if (slots[i].Item != 0) { int count = slots[i].Count; ImGui.SetNextItemWidth(CreatorControlWidth * .6f); if (ImGui.InputInt("Quantity", ref count, 0)) slots[i] = (slots[i].Item, Math.Clamp(count, 1, 255)); }
            }
            ImGui.PopID();
        }
    }

    private void WbSetQuestItem(int target, uint item)
    {
        if (target >= 0 && target < _wbQuest.Goals.Length && target < 10) _wbQuest.Goals[target].Item = item;
        else if (target is >= 10 and < 16) _wbQuest.Choices[target - 10] = (item, 1);
        else if (target is >= 20 and < 24) _wbQuest.Fixed[target - 20] = (item, 1);
    }

    private QuestAuthoringLaw.Context WbQuestContext()
    {
        var ctx = new QuestAuthoringLaw.Context
        {
            NextTriggerId = Math.Max(7100u, (uint)((_wbDocs ?? new JsonArray()).OfType<JsonObject>()
                .Where(x => (string?)x["kind"] == "dbc:AreaTrigger").Select(x => WbNum(JsonValue.Create((string?)x["docKey"] ?? "0"))).DefaultIfEmpty(0).Max() + 1)),
        };
        foreach (var t in WbDocBodies("dbrow:creature_template")) ctx.PackTemplates[(uint)WbNum(t["entry"])] = t;
        foreach (var r in WbDocBodies("dbrow:areatrigger_involvedrelation")) ctx.ExploreTriggerOf[(uint)WbNum(r["quest"])] = (uint)WbNum(r["id"]);
        foreach (var t in WbDocBodies("dbrow:areatrigger_template")) ctx.ExploreTriggers[(uint)WbNum(t["id"])] = t;
        foreach (var q in WbDocBodies("dbrow:quest_template")) ctx.Quests[(uint)WbNum(q["entry"])] = q;
        ctx.GiverRelations.AddRange(WbDocBodies("dbrow:creature_questrelation"));
        ctx.EnderRelations.AddRange(WbDocBodies("dbrow:creature_involvedrelation"));
        ctx.LootRows.AddRange(WbDocBodies("dbrow:creature_loot_template"));
        foreach (var o in WbDocBodies("dbrow:gossip_menu_option"))
        {
            long menu = WbNum(o["menu_id"]);
            if (!ctx.MenuOptions.TryGetValue(menu, out var l)) ctx.MenuOptions[menu] = l = new();
            l.Add(o);
        }
        return ctx;
    }

    /// <summary>Fill the panel from a saved quest row (edit mode).</summary>
    private void WbLoadQuestDraft(JsonObject q)
    {
        _wbQuestWorkingDrafts[(_wbPackId, _wbQuestEditing)] = _wbQuest;
        uint entry = (uint)WbNum(q["entry"]);
        _wbQuest = _wbQuestWorkingDrafts.TryGetValue((_wbPackId, entry), out var local) ? local : QuestAuthoringLaw.Read(q, WbQuestContext());
        if (_wbQuest.Ender == _wbQuest.Giver) _wbQuest.Ender = 0;
        _wbQuestEditing = _wbQuest.Entry;
        _wbQuestFindings.Clear();
        _wbQuestSaveStatus = "Editing a saved quest. Changes take effect after saving and publishing.";
        _wbQuestItemTarget = -1;
    }

    private void WbSaveQuest()
    {
        if (_wbPackId == 0) { _wbMessage = "Choose a pack before saving a quest."; return; }
        if (_wbQuestSaveTask is { IsCompleted: false }) return;
        if (_wbDocs is null) { WbRequestDocs(); _wbMessage = "loading pack content first - press Save again"; return; }
        var d = _wbQuest;
        if (d.Entry == 0)
        {
            d.Entry = Math.Max(WbNextId("dbrow:quest_template", "entry", WbTemplateBase), WbReservedEntry + 1);
            WbReservedEntry = d.Entry;
        }
        var ctx = WbQuestContext();
        if (QuestAuthoringLaw.Problems(d, ctx) is { Count: > 0 } problems) { _wbMessage = "quest: " + string.Join("; ", problems); return; }
        var items = QuestAuthoringLaw.Items(d, ctx);
        uint entry = d.Entry;
        _wbQuestCheckEntry = entry;
        _wbQuestFindings.Clear();
        int pack = _wbPackId;
        _wbQuestSavingPack = pack;
        _wbQuestSaveStatus = "Saving quest...";
        _wbQuestSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, pack, $"quest {d.Title} ({entry})", items.ToJsonString());
        WbOp($"quest {d.Title} ({entry})", _wbQuestSaveTask,
            r =>
            {
                _wbQuestDrafts[pack] = (d, entry);
                _wbQuestWorkingDrafts[(pack, entry)] = d;
                if (_wbQuestWorkingDrafts.TryGetValue((pack, 0), out var newDraft) && ReferenceEquals(newDraft, d)) _wbQuestWorkingDrafts.Remove((pack, 0));
                WbRequestDocs();
                if (_wbPackId == pack && ReferenceEquals(_wbQuest, d))
                { _wbQuestEditing = entry; _wbQuestSaveStatus = $"Saved '{d.Title}' to this pack. Checking it before publication..."; WbQuestPreflight(entry); }
            });
    }

    /// <summary>After a save: the pack pre-flight, filtered to what concerns this quest.</summary>
    private void WbQuestPreflight(uint entry)
    {
        _wbQuestCheckEntry = entry; _wbQuestCheckPack = _wbPackId;
        var task = _wbClient.PreflightAsync(SuiWebAppUrl);
        _wbPendingQuestCheck = task;
    }

    private Task<string>? _wbPendingQuestCheck;

    private void PumpWbQuestCheck()
    {
        if (_wbQuestSaveTask is { IsCompleted: true } save)
        {
            _wbQuestSaveTask = null;
            if (_wbQuestSavingPack == _wbPackId && (save.IsFaulted || save.IsCanceled || !save.Result.Success))
                _wbQuestSaveStatus = "Quest was not saved: " + (save.IsCanceled ? "request cancelled" : save.Exception?.GetBaseException().Message ?? save.Result.Error);
        }
        if (_wbPendingQuestCheck is not { IsCompleted: true } t) return;
        _wbPendingQuestCheck = null;
        if (_wbQuestCheckPack != _wbPackId || _wbQuestCheckEntry != _wbQuest.Entry) return;
        _wbQuestFindings.Clear();
        if (t.IsFaulted || t.IsCanceled) { _wbQuestFindings.Add("Quest saved, but validation could not finish: " + t.Exception?.GetBaseException().Message); return; }
        try
        {
            var report = JsonNode.Parse(t.Result)?["report"] as JsonObject;
            if (report?["findings"] is not JsonArray findings) { _wbQuestFindings.Add("Quest saved, but the server did not return a validation report. Run Validate on the Publish page."); return; }
            string tag = $"quest {_wbQuestCheckEntry}";
            foreach (var f in findings.OfType<JsonObject>())
            {
                string subject = (string?)f["subject"] ?? "";
                if ((subject == tag || subject.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase)) && (string?)f["severity"] != "info")
                    _wbQuestFindings.Add($"{(string?)f["severity"]}: {(string?)f["message"]}");
            }
            _wbQuestSaveStatus = _wbQuestFindings.Count == 0 ? "Saved. No issues were reported for this quest. Publish the pack when you are ready to test it in game." : "Saved with issues to fix before publication:";
        }
        catch (Exception ex) { _wbQuestFindings.Add("Quest saved, but validation could not be read: " + ex.Message); }
        if (WbScriptPath is not null) foreach (var f in _wbQuestFindings) Console.WriteLine($"[questui] {f}");
    }

    /// <summary>Script: load a draft (JSON, QuestAuthoringLaw.Draft shape) into the panel and save it.</summary>
    private void WbScriptQuestUi(string file)
    {
        var d = JsonSerializer.Deserialize<QuestAuthoringLaw.Draft>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
            ?? throw new InvalidOperationException($"{file}: not a quest draft");
        if (d.Goals.Length < 4) d.Goals = d.Goals.Concat(Enumerable.Range(0, 4 - d.Goals.Length).Select(_ => new QuestAuthoringLaw.Objective())).ToArray();
        if (d.Choices.Length < 6) d.Choices = d.Choices.Concat(new (uint, int)[6 - d.Choices.Length]).ToArray();
        if (d.Fixed.Length < 4) d.Fixed = d.Fixed.Concat(new (uint, int)[4 - d.Fixed.Length]).ToArray();
        _wbQuest = d;
        _wbQuestEditing = d.Entry;
        WbSaveQuest();
    }
}
