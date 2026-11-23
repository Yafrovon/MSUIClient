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
    /// <summary>Signature of each quest as its pack last held it (loaded or saved), so the table can say "unsaved".</summary>
    private readonly Dictionary<(int Pack, uint Entry), string> _wbQuestSavedSigs = new();
    private static readonly string[] WbObjectiveKinds = { "(none)", "Kill creature", "Collect item", "Use object", "Discover a place" };

    private void DrawWbQuestSection()
    {
        float w = CreatorControlWidth;
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("loading pack content..."); return; }
        WbQuestFollowPack();
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
        if (_wbQuestEditing != 0 && quests.FirstOrDefault(q => WbNum(q["entry"]) == _wbQuestEditing) is { } openQuest)
        {
            bool deleteClear = DrawWbQuestDeleteGuard((uint)_wbQuestEditing, openQuest);
            ImGui.BeginDisabled(_wbOps.Count > 0 || !deleteClear);
            if (WbConfirmButton("Delete this quest", $"Delete the quest \"{openQuest["Title"]}\" from this pack, with its giver/ender links and discovery trigger? Undo brings it back."))
            {
                uint entry = (uint)_wbQuestEditing;
                WbDeleteDocs($"delete quest {openQuest["Title"]}", WbQuestDocs(entry), () =>
                {
                    _wbQuest = new QuestAuthoringLaw.Draft(); _wbQuestEditing = 0; _wbQuestFindings.Clear(); _wbQuestSaveStatus = "Quest deleted.";
                });
            }
            ImGui.EndDisabled();
        }
        var d = _wbQuest; // A selection above changes the draft in this same frame.
        DrawWbStockQuestHeader(d);

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
                    ImGui.TextUnformatted(g.Item == 0 ? "Item: none chosen" : $"Item: {WbItemName(g.Item)}");
                    if (ImGui.SmallButton("Choose item")) _wbQuestItemTarget = i;
                    g.DropFrom = WbNpcPicker("Dropped by", g.DropFrom, $"wbq-drop{i}");
                    if (g.DropFrom != 0)
                    {
                        float chance = g.DropChance; ImGui.SetNextItemWidth(w * 0.6f);
                        if (ImGui.InputFloat("% (quest-only)", ref chance, 0f, 0f, "%.1f")) g.DropChance = Math.Clamp(chance, 1f, 100f);
                    }
                    break;
                case QuestAuthoringLaw.ObjectiveKind.Explore:
                {
                    var goal = g;
                    int pickMap = WbQuestPickMap;
                    WbPickButton("Pick the place on the ground", $"the place to discover (objective {i + 1})", at =>
                    { goal.ExploreMap = pickMap; goal.ExploreX = at.X; goal.ExploreY = at.Y; goal.ExploreZ = at.Z; }, g.ExploreRadius);
                    bool placed = g.ExploreX != 0 || g.ExploreY != 0;
                    var spot = new Vector3(g.ExploreX, g.ExploreY, g.ExploreZ);
                    if (!placed) ImGui.TextDisabled("Not picked yet.");
                    else if (g.ExploreMap != pickMap) ImGui.TextDisabled("The place is on another map.");
                    else if (TabletopActive) ImGui.TextDisabled(Inv($"Ringed on the table at ({spot.X:F0}, {spot.Y:F0})."));
                    else
                    {
                        WbMarker(spot, $"Discover: {(d.Title.Length > 0 ? d.Title : "quest")}", 0xFF40FF80u, g.ExploreRadius);
                        float away = _controller is null ? 0f : Vector3.Distance(_controller.Position, spot);
                        ImGui.TextDisabled(Inv($"Marked in the world, {away:F0} yd away."));
                        ImGui.SameLine();
                        if (_controller is not null && ImGui.SmallButton("Go there")) _controller.Teleport(spot.X, spot.Y, spot.Z + 8f);
                    }
                    float r = g.ExploreRadius; ImGui.SetNextItemWidth(w * 0.6f);
                    if (ImGui.SliderFloat("Area size (yd)", ref r, 2f, 60f, "%.0f")) g.ExploreRadius = Math.Clamp(r, 2f, 100f);
                    break;
                }
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
        // Guardrails (QUEST_TABLETOP.md §13.2): nothing saves while it would leave the world broken or unconfirmed.
        bool guarded = problems.Count > 0 || DrawWbQuestGuardrails(d);
        bool saving = _wbQuestSaveTask is { IsCompleted: false };
        bool ready = _wbPackId != 0 && problems.Count == 0 && guarded && !saving;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton(saving ? "Saving quest..." : _wbQuestEditing == 0 ? "Create quest" : "Save quest changes")) WbSaveQuest();
        if (!ready) ImGui.EndDisabled();
        if (_wbQuestSaveStatus.Length > 0) ImGui.TextWrapped(_wbQuestSaveStatus);
        foreach (var f in _wbQuestFindings) ImGui.TextWrapped(f);
    }

    /// <summary>The selected pack changed: park the open draft under its pack and bring back the new pack's.</summary>
    private void WbQuestFollowPack()
    {
        if (_wbQuestPack == _wbPackId) return;
        if (_wbQuestPack > 0) _wbQuestDrafts[_wbQuestPack] = (_wbQuest, _wbQuestEditing);
        _wbQuestPack = _wbPackId;
        var saved = _wbQuestDrafts.GetValueOrDefault(_wbPackId, (new QuestAuthoringLaw.Draft(), 0u));
        _wbQuest = saved.Item1; _wbQuestEditing = saved.Item2;
        _wbQuestFindings.Clear(); _wbQuestSaveStatus = ""; _wbQuestItemTarget = -1;
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
        string shown = value == 0 ? "Choose an NPC" : npcs.FirstOrDefault(n => WbNum(n["entry"]) == value) is { } t ? (string?)t["name"] ?? "Unnamed NPC"
            : _creatorCreatures?.Search(value.ToString(CultureInfo.InvariantCulture), 1) is { Count: > 0 } stock ? stock[0].Name + " (game creature)"
            : TabletopActive && TabletopCreatureInfo(value) is { Name.Length: > 0 } censusNpc ? censusNpc.Name + " (game creature)" : $"Creature {value}";
        ImGui.SetNextItemWidth(w);
        bool expanded = ImGui.BeginCombo($"{label}##{id}", shown);
        ObserveWorldBuilderUiItem(label);
        if (expanded)
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            string search = _wbQuestNpcSearch.GetValueOrDefault(id, "");
            ImGui.SetNextItemWidth(w);
            if (ImGui.InputText($"Search by name##{id}", ref search, 80)) _wbQuestNpcSearch[id] = search;
            if (npcs.Count > 0) ImGui.TextDisabled("This pack's NPCs");
            foreach (var n in npcs.Where(n => ((string?)n["name"] ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)).OrderBy(n => (string?)n["name"]))
            {
                if (ImGui.Selectable($"{(string?)n["name"]}##{id}{WbNum(n["entry"])}", WbNum(n["entry"]) == value)) value = (uint)WbNum(n["entry"]);
                ObserveWorldBuilderUiItem($"{label}: {n["name"]}");
            }
            EnsureCreatorCreatures();
            if (search.Length >= 2 && _creatorCreatures?.Search(search, 25) is { Count: > 0 } world)
            {
                ImGui.TextDisabled("Creatures in the game");
                foreach (var c in world)
                {
                    if (ImGui.Selectable($"{c.Name}{(c.SubName.Length > 0 ? " <" + c.SubName + ">" : "")}##{id}w{c.Entry}", c.Entry == value)) value = c.Entry;
                    ObserveWorldBuilderUiItem($"{label}: {c.Name}");
                }
            }
            else if (search.Length < 2) ImGui.TextDisabled("Type 2+ letters to search every creature in the game.");
            ImGui.EndCombo();
        }
        if (TabletopActive) value = TtBoardPickButton("npc", id, value);
        else
        {
            uint selected = _wbNpcEditEntry;
            if (selected == 0 && _entities.TryGet(_selectionGuid, out var sel) && sel.IsCreature) selected = sel.Entry;
            if (selected != 0 && ImGui.SmallButton($"Use the NPC I have selected##{id}")) value = selected;
        }
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
        // Custom objects of this pack, plus every object kind this pack has placed (stock chests, nodes, campfires...).
        var choices = WbDocBodies("dbrow:gameobject_template").Select(o => ((uint)WbNum(o["entry"]), o["name"]?.ToString() ?? "object"))
            .Concat(WbNpcPackDocs("dbrow:gameobject", _wbPackId).Select(d => WbNpcUInt(d["body"]?["id"])).Distinct().Select(e => (e, WbGoName(e))))
            .GroupBy(c => c.Item1).Select(g => g.First()).OrderBy(c => c.Item2).ToList();
        string name = choices.FirstOrDefault(c => c.Item1 == value).Item2 ?? WbGoName(value);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Object##wbq", value == 0 ? "Choose an object" : name))
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            foreach (var (entry, label) in choices) if (ImGui.Selectable($"{label}##wbq-object{entry}", entry == value)) value = entry;
            ImGui.EndCombo();
        }
        if (TabletopActive) value = TtBoardPickButton("object", "wbq-object", value);
        if (choices.Count == 0) ImGui.TextWrapped("Place the object first (Chests page); it then appears here.");
        if (ImGui.TreeNode("Advanced object entry##wbq"))
        { int entry = (int)value; ImGui.SetNextItemWidth(CreatorControlWidth * .6f); if (ImGui.InputInt("Entry##wbq-object", ref entry, 0)) value = (uint)Math.Max(0, entry); ImGui.TreePop(); }
        return value;
    }

    private int WbQuestPicker(string label, int value, List<JsonObject> quests, string id)
    {
        string shown = value == 0 ? "(none)" : quests.FirstOrDefault(q => WbNum(q["entry"]) == Math.Abs(value)) is { } q2 ? $"{(string?)q2["Title"]} ({value})"
            : TabletopActive && _qaBoard?.Quests.GetValueOrDefault((uint)Math.Abs((long)value)) is { } bq ? $"{bq.Title} ({value})"
            : value.ToString(CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        if (ImGui.BeginCombo($"{label}##{id}", shown))
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            foreach (var q in quests)
                if (WbNum(q["entry"]) != _wbQuest.Entry && ImGui.Selectable($"{(string?)q["Title"]}##{id}{WbNum(q["entry"])}", WbNum(q["entry"]) == Math.Abs((long)value))) value = (int)WbNum(q["entry"]);
            ImGui.EndCombo();
        }
        // On the table any quest on the board can be linked, stock ones included (a pin that offers one quest).
        if (TabletopActive)
        {
            ImGui.SameLine();
            uint picked = TtBoardPickButton("quest", id, (uint)Math.Abs((long)value), compact: true);
            if (picked != (uint)Math.Abs((long)value)) value = value < 0 ? -(int)picked : (int)picked;
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
            // Buttons on their own (indented) line: a long item name would push them past the panel edge.
            ImGui.Indent(); if (ImGui.SmallButton(slots[i].Item == 0 ? "Choose item" : "Change")) _wbQuestItemTarget = targetBase + i;
            if (slots[i].Item != 0)
            {
                ImGui.SameLine(); if (ImGui.SmallButton("Remove")) slots[i] = (0, 0);
                if (slots[i].Item != 0) { int count = slots[i].Count; ImGui.SetNextItemWidth(CreatorControlWidth * .6f); if (ImGui.InputInt("Quantity", ref count, 0)) slots[i] = (slots[i].Item, Math.Clamp(count, 1, 255)); }
            }
            ImGui.Unindent();
            ImGui.PopID();
        }
    }

    private void WbSetQuestItem(int target, uint item)
    {
        if (target >= 0 && target < _wbQuest.Goals.Length && target < 10) _wbQuest.Goals[target].Item = item;
        else if (target is >= 10 and < 16) _wbQuest.Choices[target - 10] = (item, 1);
        else if (target is >= 20 and < 24) _wbQuest.Fixed[target - 20] = (item, 1);
    }

    private QuestAuthoringLaw.Context WbQuestContext(uint? forEntry = null)
    {
        uint entry = forEntry ?? _wbQuest.Entry;
        WbStockContext(new QuestAuthoringLaw.Context(), entry, out bool stock, out var stockGivers, out var stockEnders, out bool stockTrigger);
        var ctx = new QuestAuthoringLaw.Context
        {
            NextTriggerId = Math.Max(7100u, (uint)((_wbDocs ?? new JsonArray()).OfType<JsonObject>()
                .Where(x => (string?)x["kind"] == "dbc:AreaTrigger").Select(x => WbNum(JsonValue.Create((string?)x["docKey"] ?? "0"))).DefaultIfEmpty(0).Max() + 1)),
            StockOverride = stock, StockGivers = stockGivers, StockEnders = stockEnders, StockExploreTrigger = stockTrigger,
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
        if (stock) WbStockContext(ctx, entry, out _, out _, out _, out _);   // the stock row + its stock relations
        return ctx;
    }

    /// <summary>Fill the panel from a saved quest row (edit mode).</summary>
    private void WbLoadQuestDraft(JsonObject q, bool discardLocal = false)
    {
        uint entry = (uint)WbNum(q["entry"]);
        // discardLocal (Restore the original): the open draft is the version just removed - neither park nor reuse it.
        if (discardLocal) { _wbQuestWorkingDrafts.Remove((_wbPackId, entry)); if (_wbQuestEditing != entry) _wbQuestWorkingDrafts[(_wbPackId, _wbQuestEditing)] = _wbQuest; }
        else _wbQuestWorkingDrafts[(_wbPackId, _wbQuestEditing)] = _wbQuest;
        // What the pack holds, as the table's "saved" reference (a local working draft may already differ from it).
        var held = QuestAuthoringLaw.Read(q, WbQuestContext(entry));
        if (held.Ender == held.Giver) held.Ender = 0;   // the form's own normal form (below)
        _wbQuestSavedSigs[(_wbPackId, entry)] = QuestDraftSignature(held);
        _wbQuest = _wbQuestWorkingDrafts.TryGetValue((_wbPackId, entry), out var local) ? local : QuestAuthoringLaw.Read(q, WbQuestContext(entry));
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
        // Guardrails (QUEST_TABLETOP.md §13.2) hold for every caller - the button, questui scripts, probes.
        var guard = WbQuestGuardState(d);
        if (guard.Pending) { _wbMessage = "quest: still checking the world - save again in a moment"; return; }
        if (guard.Open > 0)
        {
            _wbMessage = $"quest: {guard.Open} check(s) to resolve before saving";
            _wbQuestSaveStatus = "Not saved: resolve the checks above first.";
            return;
        }
        if (d.Entry == 0)
        {
            d.Entry = Math.Max(WbNextId("dbrow:quest_template", "entry", WbTemplateBase), WbReservedEntry + 1);
            WbReservedEntry = d.Entry;
        }
        var ctx = WbQuestContext();
        if (QuestAuthoringLaw.Problems(d, ctx) is { Count: > 0 } problems) { _wbMessage = "quest: " + string.Join("; ", problems); return; }
        bool stockQuest = d.Entry < WbTemplateBase;
        var items = stockQuest ? QuestAuthoringLaw.OverrideItems(d, ctx) : QuestAuthoringLaw.Items(d, ctx);
        // The holders' quest state, when the author chose a reset: one request, run once at the next restarting publish.
        string? resetScope = _wbQuestReset.TryGetValue(d, out var rs) ? rs : null;
        if (resetScope is not null)
            items.Add(new JsonObject
            {
                ["kind"] = "quest-reset", ["key"] = d.Entry.ToString(CultureInfo.InvariantCulture),
                ["body"] = new JsonObject { ["entry"] = d.Entry, ["scope"] = resetScope, ["requestId"] = Guid.NewGuid().ToString("N") },
            });
        uint entry = d.Entry;
        _wbQuestCheckEntry = entry;
        _wbQuestFindings.Clear();
        int pack = _wbPackId;
        _wbQuestSavingPack = pack;
        _wbQuestSaveStatus = "Saving quest...";
        _wbQuestSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, pack, $"quest {d.Title} ({entry})", items.ToJsonString());
        string savedSig = QuestDraftSignature(d);
        WbOp($"quest {d.Title} ({entry})", _wbQuestSaveTask,
            r =>
            {
                _wbQuestSavedSigs[(pack, entry)] = savedSig;
                if (resetScope is not null)
                {
                    // The request is stored: it is one-time, so the next save asks again (holders may have changed by then).
                    _wbQuestReset.Remove(d);
                    _wbQuestConfirmed.GetValue(d, _ => new HashSet<string>()).RemoveWhere(k => k.StartsWith("holders:", StringComparison.Ordinal));
                    _wbMessage = $"quest {entry}: players' progress will be reset ({resetScope}) at the next publish that restarts the server";
                }
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
            if (report?["findings"] is not JsonArray findings) { _wbQuestFindings.Add("Quest saved, but the server did not return a validation report. Use 1. Check drafts on the Publish page."); return; }
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
        // Guardrails: the save waits for the world checks (wait state "questsave"); a script confirms findings only by
        // naming them: "confirm": ["kill.questnpc", ...] (key prefixes). Anything else unresolved refuses the save.
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        _wbScriptQuestConfirm = doc.RootElement.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
    }

    private List<string> _wbScriptQuestConfirm = new();

    /// <summary>Script wait "questsave": true once the save went out (or was refused with its findings printed).</summary>
    private bool WbScriptQuestSaveStep()
    {
        var d = _wbQuest;
        var guard = WbQuestGuardState(d);
        if (guard.Pending) return false;
        var confirmed = _wbQuestConfirmed.GetValue(d, _ => new HashSet<string>());
        foreach (var f in guard.Findings)
            if (f.Level == QuestGuardrailLaw.Level.Confirm && _wbScriptQuestConfirm.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal)))
                confirmed.Add(f.Key);
        foreach (var f in guard.Findings)
            Console.WriteLine($"[questui] check {f.Level} {f.Key}: {f.Message}{(confirmed.Contains(f.Key) ? " (confirmed by script)" : "")}");
        WbSaveQuest();
        if (_wbQuestSaveTask is null) Console.WriteLine($"[questui] not saved: {_wbMessage}");
        _wbScriptWaitFor = "idle";   // then wait for the save op like any other edit
        return false;
    }

    /// <summary>A quest's own pack docs: the template, giver/ender links (creature and object) and its discovery trigger.</summary>
    private List<JsonObject> WbQuestDocs(uint entry)
    {
        var all = (_wbDocs ?? new()).OfType<JsonObject>().Where(d => (int?)d["packId"] == _wbPackId && d["body"] is JsonObject).ToList();
        var triggers = all.Where(d => (d["kind"]?.ToString() ?? "").Contains("areatrigger", StringComparison.OrdinalIgnoreCase) &&
                                      WbNpcUInt(d["body"]?["quest"]) == entry).Select(d => WbNpcUInt(d["body"]?["id"])).ToHashSet();
        return all.Where(d =>
        {
            string kind = d["kind"]?.ToString() ?? "";
            var b = d["body"]!;
            return kind == "dbrow:quest_template" ? WbNpcUInt(b["entry"]) == entry
                : kind == "quest-reset" ? (string?)d["docKey"] == entry.ToString(CultureInfo.InvariantCulture)   // a pending reset goes with it
                : kind.EndsWith("relation", StringComparison.OrdinalIgnoreCase) ? WbNpcUInt(b["quest"]) == entry
                : kind.Equals("dbc:AreaTrigger", StringComparison.OrdinalIgnoreCase) ? triggers.Contains(WbNpcUInt(d["docKey"]))
                : kind.Contains("areatrigger", StringComparison.OrdinalIgnoreCase) && triggers.Contains(WbNpcUInt(b["id"]));
        }).ToList();
    }
}
