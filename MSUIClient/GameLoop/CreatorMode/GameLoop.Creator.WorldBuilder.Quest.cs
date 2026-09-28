using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

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
    private static readonly string[] WbObjectiveKinds = { "(none)", "Kill creature", "Collect item", "Use object", "Explore here" };

    private void DrawWbQuestSection()
    {
        float w = CreatorControlWidth;
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("loading pack content..."); return; }
        var d = _wbQuest;

        // ── which quest ────────────────────────────────────────────────────
        var quests = WbDocBodies("dbrow:quest_template").OrderBy(q => WbNum(q["entry"])).ToList();
        string current = _wbQuestEditing == 0 ? "(new quest)" : $"{_wbQuestEditing} {d.Title}";
        ImGui.SetNextItemWidth(w);
        if (ImGui.BeginCombo("Quest##wbq-pick", current))
        {
            if (ImGui.Selectable("(new quest)", _wbQuestEditing == 0)) { _wbQuest = new(); _wbQuestEditing = 0; }
            foreach (var q in quests)
                if (ImGui.Selectable($"{WbNum(q["entry"])} {(string?)q["Title"]}##wbq{WbNum(q["entry"])}", WbNum(q["entry"]) == _wbQuestEditing))
                    WbLoadQuestDraft(q);
            ImGui.EndCombo();
        }

        // ── text ───────────────────────────────────────────────────────────
        string title = d.Title; ImGui.SetNextItemWidth(w); if (ImGui.InputText("Title##wbq", ref title, 120)) d.Title = title;
        ImGui.TextDisabled("Quest text  ($N name, $C class, $R race, $B new line, $Gmale:female;)");
        string details = d.Details; if (ImGui.InputTextMultiline("##wbq-details", ref details, 2000, new Vector2(-1f, 70f * CreatorUiScale))) d.Details = details;
        string obj = d.Objectives; ImGui.SetNextItemWidth(w); if (ImGui.InputText("Objectives##wbq", ref obj, 600)) d.Objectives = obj;
        string req = d.RequestText; ImGui.SetNextItemWidth(w); if (ImGui.InputText("While incomplete##wbq", ref req, 600)) d.RequestText = req;
        string done = d.CompleteText; ImGui.SetNextItemWidth(w); if (ImGui.InputText("On turn-in##wbq", ref done, 1000)) d.CompleteText = done;
        int lvl = d.Level, min = d.MinLevel, zone = d.Zone;
        ImGui.SetNextItemWidth(w * 0.3f); if (ImGui.InputInt("Level##wbq", ref lvl)) d.Level = Math.Clamp(lvl, 1, 63);
        ImGui.SameLine(); ImGui.SetNextItemWidth(w * 0.3f); if (ImGui.InputInt("Min##wbq", ref min)) d.MinLevel = Math.Clamp(min, 1, 63);
        ImGui.SetNextItemWidth(w * 0.4f); if (ImGui.InputInt("Zone (area id)##wbq", ref zone)) d.Zone = zone;

        // ── who ────────────────────────────────────────────────────────────
        d.Giver = WbNpcPicker("Given by", d.Giver, "wbq-giver");
        d.Ender = WbNpcPicker("Turned in to (blank = giver)", d.Ender, "wbq-ender");

        // ── objectives ─────────────────────────────────────────────────────
        ImGui.Separator();
        ImGui.TextDisabled("Objectives");
        for (int i = 0; i < 4; i++)
        {
            var g = d.Goals[i];
            ImGui.PushID($"wbq-goal{i}");
            int kind = (int)g.Kind;
            ImGui.SetNextItemWidth(w * 0.45f);
            if (ImGui.Combo($"{i + 1}", ref kind, WbObjectiveKinds, WbObjectiveKinds.Length)) g.Kind = (QuestAuthoringLaw.ObjectiveKind)kind;
            int count = g.Count;
            if (g.Kind is QuestAuthoringLaw.ObjectiveKind.Kill or QuestAuthoringLaw.ObjectiveKind.Collect or QuestAuthoringLaw.ObjectiveKind.UseObject)
            { ImGui.SameLine(); ImGui.SetNextItemWidth(w * 0.25f); if (ImGui.InputInt("x##count", ref count)) g.Count = Math.Clamp(count, 1, 255); }
            switch (g.Kind)
            {
                case QuestAuthoringLaw.ObjectiveKind.Kill:
                    g.Target = WbNpcPicker("Creature", g.Target, $"wbq-kill{i}"); break;
                case QuestAuthoringLaw.ObjectiveKind.UseObject:
                {
                    int go = (int)g.Target; ImGui.SetNextItemWidth(w * 0.5f);
                    if (ImGui.InputInt("Gameobject entry", ref go)) g.Target = (uint)Math.Max(go, 0);
                    break;
                }
                case QuestAuthoringLaw.ObjectiveKind.Collect:
                    ImGui.TextUnformatted(g.Item == 0 ? "item: (none)" : $"item: {WbItemName(g.Item)} ({g.Item})");
                    ImGui.SameLine(); if (ImGui.SmallButton("pick item")) _wbQuestItemTarget = i;
                    g.DropFrom = WbNpcPicker("Drops from (pack creature)", g.DropFrom, $"wbq-drop{i}");
                    if (g.DropFrom != 0)
                    {
                        float chance = g.DropChance; ImGui.SetNextItemWidth(w * 0.4f);
                        if (ImGui.InputFloat("% (quest-only)", ref chance, 5f, 10f, "%.0f")) g.DropChance = Math.Clamp(chance, 1f, 100f);
                    }
                    break;
                case QuestAuthoringLaw.ObjectiveKind.Explore:
                    if (ImGui.SmallButton("use where I stand") && _controller is not null)
                    { g.ExploreMap = _config.Start.Map; g.ExploreX = _controller.Position.X; g.ExploreY = _controller.Position.Y; g.ExploreZ = _controller.Position.Z; }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(Inv($"map {g.ExploreMap} ({g.ExploreX:F0}, {g.ExploreY:F0}, {g.ExploreZ:F0})"));
                    float r = g.ExploreRadius; ImGui.SetNextItemWidth(w * 0.4f);
                    if (ImGui.InputFloat("radius", ref r, 1f, 5f, "%.0f")) g.ExploreRadius = Math.Clamp(r, 2f, 100f);
                    break;
            }
            if (g.Kind != QuestAuthoringLaw.ObjectiveKind.None)
            { string t = g.Text; ImGui.SetNextItemWidth(w); if (ImGui.InputText("quest log line (optional)", ref t, 200)) g.Text = t; }
            ImGui.PopID();
        }

        // ── rewards ────────────────────────────────────────────────────────
        ImGui.Separator();
        ImGui.TextDisabled("Rewards");
        int xp = d.Xp, money = d.Money;
        ImGui.SetNextItemWidth(w * 0.4f); if (ImGui.InputInt("XP##wbq", ref xp)) d.Xp = Math.Max(xp, 0);
        ImGui.SetNextItemWidth(w * 0.4f); if (ImGui.InputInt("Money (copper)##wbq", ref money)) d.Money = Math.Max(money, 0);
        WbRewardRow("Choose one of", d.Choices, 10);
        WbRewardRow("Always get", d.Fixed, 20);
        int rf = d.RepFaction, rv = d.RepValue;
        ImGui.SetNextItemWidth(w * 0.4f); if (ImGui.InputInt("Reputation faction##wbq", ref rf)) d.RepFaction = Math.Max(rf, 0);
        if (d.RepFaction != 0) { ImGui.SetNextItemWidth(w * 0.4f); if (ImGui.InputInt("Reputation##wbq", ref rv)) d.RepValue = rv; }

        // ── chain ──────────────────────────────────────────────────────────
        ImGui.Separator();
        ImGui.TextDisabled("Chain");
        d.PrevQuest = WbQuestPicker("Requires completing", d.PrevQuest, quests, "wbq-prev");
        d.NextInChain = WbQuestPicker("Next in chain", d.NextInChain, quests, "wbq-next");
        int eg = d.ExclusiveGroup; ImGui.SetNextItemWidth(w * 0.4f);
        if (ImGui.InputInt("Exclusive group##wbq", ref eg)) d.ExclusiveGroup = eg;

        // ── item picker (shared by objectives and rewards) ─────────────────
        if (_wbQuestItemTarget >= 0)
        {
            _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("find item##wbq-item", _wbQuestItemSearch, (uint)_wbQuestItemSearch.Length);
            string iq = WbText(_wbQuestItemSearch);
            if (iq != _wbQuestItemSearchLast) { _wbQuestItemSearchLast = iq; _wbQuestItemHits = iq.Length >= 2 ? _wbItems?.Search(iq, -2, 30) : null; }
            if (_wbQuestItemHits is { Count: > 0 } hits && ImGui.BeginListBox("##wbq-item-hits", new Vector2(-1f, 100f * CreatorUiScale)))
            {
                foreach (var it in hits)
                    if (ImGui.Selectable($"{it.Name}  ({it.Entry})##wbqi{it.Entry}")) { WbSetQuestItem(_wbQuestItemTarget, it.Entry); _wbQuestItemTarget = -1; }
                ImGui.EndListBox();
            }
            if (ImGui.SmallButton("cancel##wbq-item")) _wbQuestItemTarget = -1;
        }

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
        bool ready = _wbPackId != 0 && problems.Count == 0;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton(_wbQuestEditing == 0 ? "Create quest" : $"Save quest {_wbQuestEditing}")) WbSaveQuest();
        if (!ready) ImGui.EndDisabled();
        foreach (var f in _wbQuestFindings) ImGui.TextWrapped(f);
    }

    private IEnumerable<JsonObject> WbDocBodies(string kind) =>
        (_wbDocs ?? new JsonArray()).OfType<JsonObject>().Where(x => (string?)x["kind"] == kind && x["body"] is JsonObject).Select(x => (JsonObject)x["body"]!);

    private static long WbNum(JsonNode? n) => n is JsonValue v && long.TryParse(WbScalar(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;

    private string WbItemName(uint entry)
    {
        _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
        return _wbItems?.Search(entry.ToString(CultureInfo.InvariantCulture), -2, 1) is { Count: > 0 } hit ? hit[0].Name : "item";
    }

    /// <summary>Pack creature picker + manual entry + "my target".</summary>
    private uint WbNpcPicker(string label, uint value, string id)
    {
        float w = CreatorControlWidth;
        var npcs = WbDocBodies("dbrow:creature_template").ToList();
        string shown = value == 0 ? "(none)" : npcs.FirstOrDefault(n => WbNum(n["entry"]) == value) is { } t ? $"{(string?)t["name"]} ({value})" : $"stock {value}";
        ImGui.SetNextItemWidth(w * 0.6f);
        if (ImGui.BeginCombo($"{label}##{id}", shown))
        {
            if (ImGui.Selectable("(none)", value == 0)) value = 0;
            foreach (var n in npcs.OrderBy(n => (string?)n["name"]))
                if (ImGui.Selectable($"{(string?)n["name"]} ({WbNum(n["entry"])})##{id}{WbNum(n["entry"])}", WbNum(n["entry"]) == value)) value = (uint)WbNum(n["entry"]);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"target##{id}") && _entities.TryGet(_selectionGuid, out var sel) && sel.IsCreature) value = sel.Entry;
        int manual = (int)value;
        ImGui.SameLine(); ImGui.SetNextItemWidth(w * 0.25f);
        if (ImGui.InputInt($"##{id}-manual", ref manual, 0)) value = (uint)Math.Max(manual, 0);
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
                if (ImGui.Selectable($"{WbNum(q["entry"])} {(string?)q["Title"]}##{id}{WbNum(q["entry"])}", WbNum(q["entry"]) == value)) value = (int)WbNum(q["entry"]);
            ImGui.EndCombo();
        }
        return value;
    }

    private void WbRewardRow(string label, (uint Item, int Count)[] slots, int targetBase)
    {
        ImGui.TextUnformatted(label);
        for (int i = 0; i < slots.Length; i++)
        {
            ImGui.PushID($"{label}{i}");
            ImGui.TextUnformatted(slots[i].Item == 0 ? $"  {i + 1}: -" : $"  {i + 1}: {WbItemName(slots[i].Item)} x{Math.Max(1, slots[i].Count)}");
            ImGui.SameLine(); if (ImGui.SmallButton("set")) _wbQuestItemTarget = targetBase + i;
            if (slots[i].Item != 0) { ImGui.SameLine(); if (ImGui.SmallButton("x")) slots[i] = (0, 0); }
            ImGui.PopID();
        }
    }

    private void WbSetQuestItem(int target, uint item)
    {
        if (target is >= 0 and < 4) _wbQuest.Goals[target].Item = item;
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
        uint entry = (uint)WbNum(q["entry"]);
        string S(string c) => (string?)q[c] ?? "";
        var d = new QuestAuthoringLaw.Draft
        {
            Entry = entry, Title = S("Title"), Details = S("Details"), Objectives = S("Objectives"), RequestText = S("RequestItemsText"),
            CompleteText = S("OfferRewardText"), Level = (int)WbNum(q["QuestLevel"]), MinLevel = (int)WbNum(q["MinLevel"]), Zone = (int)WbNum(q["ZoneOrSort"]),
            Xp = (int)WbNum(q["RewXP"]), Money = (int)WbNum(q["RewOrReqMoney"]), RepFaction = (int)WbNum(q["RewRepFaction1"]), RepValue = (int)WbNum(q["RewRepValue1"]),
            PrevQuest = (int)WbNum(q["PrevQuestId"]), NextInChain = (int)WbNum(q["NextQuestInChain"]), ExclusiveGroup = (int)WbNum(q["ExclusiveGroup"]),
        };
        d.Giver = (uint)WbDocBodies("dbrow:creature_questrelation").Where(r => WbNum(r["quest"]) == entry).Select(r => WbNum(r["id"])).FirstOrDefault();
        d.Ender = (uint)WbDocBodies("dbrow:creature_involvedrelation").Where(r => WbNum(r["quest"]) == entry).Select(r => WbNum(r["id"])).FirstOrDefault();
        int g = 0;
        for (int i = 1; i <= 4 && g < 4; i++)
        {
            long unit = WbNum(q[$"ReqCreatureOrGOId{i}"]);
            if (unit != 0)
                d.Goals[g++] = new() { Kind = unit > 0 ? QuestAuthoringLaw.ObjectiveKind.Kill : QuestAuthoringLaw.ObjectiveKind.UseObject,
                                       Target = (uint)Math.Abs(unit), Count = (int)WbNum(q[$"ReqCreatureOrGOCount{i}"]) };
        }
        for (int i = 1; i <= 4 && g < 4; i++)
        {
            long item = WbNum(q[$"ReqItemId{i}"]);
            if (item == 0) continue;
            var drop = WbDocBodies("dbrow:creature_loot_template").FirstOrDefault(l => WbNum(l["item"]) == item);
            uint dropFrom = drop is null ? 0 : (uint)(WbDocBodies("dbrow:creature_template").FirstOrDefault(t => WbNum(t["loot_id"]) == WbNum(drop["entry"]))?["entry"] is JsonNode e ? WbNum(e) : 0);
            d.Goals[g++] = new() { Kind = QuestAuthoringLaw.ObjectiveKind.Collect, Item = (uint)item, Count = (int)WbNum(q[$"ReqItemCount{i}"]),
                                   DropFrom = dropFrom, DropChance = drop is null ? 35f : MathF.Abs((float)WbNum(drop["ChanceOrQuestChance"])) };
        }
        for (int i = 0; i < 6; i++) d.Choices[i] = ((uint)WbNum(q[$"RewChoiceItemId{i + 1}"]), (int)WbNum(q[$"RewChoiceItemCount{i + 1}"]));
        for (int i = 0; i < 4; i++) d.Fixed[i] = ((uint)WbNum(q[$"RewItemId{i + 1}"]), (int)WbNum(q[$"RewItemCount{i + 1}"]));
        _wbQuest = d;
        _wbQuestEditing = entry;
        _wbQuestFindings.Clear();
    }

    private void WbSaveQuest()
    {
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
        _wbQuestEditing = entry;
        _wbQuestCheckEntry = entry;
        _wbQuestFindings.Clear();
        WbOp($"quest {d.Title} ({entry})", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"quest {d.Title} ({entry})", items.ToJsonString()),
            r => { WbRequestDocs(); if (r.Success) WbQuestPreflight(entry); });
    }

    /// <summary>After a save: the pack pre-flight, filtered to what concerns this quest.</summary>
    private void WbQuestPreflight(uint entry)
    {
        var task = _wbClient.PreflightAsync(SuiWebAppUrl);
        _wbPendingQuestCheck = task;
    }

    private Task<string>? _wbPendingQuestCheck;

    private void PumpWbQuestCheck()
    {
        if (_wbPendingQuestCheck is not { IsCompleted: true } t) return;
        _wbPendingQuestCheck = null;
        _wbQuestFindings.Clear();
        if (t.IsFaulted) { _wbQuestFindings.Add("pre-flight failed: " + t.Exception?.GetBaseException().Message); return; }
        string tag = $"quest {_wbQuestCheckEntry}";
        var report = JsonNode.Parse(t.Result)?["report"] as JsonObject;
        foreach (var f in (report?["findings"] as JsonArray ?? new()).OfType<JsonObject>())
            if (((string?)f["subject"] ?? "").Contains(tag) && (string?)f["severity"] != "info")
                _wbQuestFindings.Add($"{(string?)f["severity"]}: {(string?)f["message"]}");
        if (_wbQuestFindings.Count == 0) _wbQuestFindings.Add($"pre-flight: quest {_wbQuestCheckEntry} is clean");
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
