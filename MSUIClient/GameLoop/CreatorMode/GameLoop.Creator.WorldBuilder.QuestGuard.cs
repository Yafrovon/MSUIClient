using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest guardrails in the Quest Creator form (shared_docs/QUEST_TABLETOP.md §13.2, slice 2). QuestGuardrailLaw decides;
// this file feeds it and draws the guided resolve list:
//
//  * World = live facts (GET /QuestAtlas/Facts, fetched when the draft's references change) + the UNPUBLISHED pack
//    content (this pack and every enabled pack: spawns, replacements, quests, relations, loot, vendors) + the table's
//    census when the board is open.
//  * Save stays disabled until every finding is resolved: a Blocker by changing the draft (its fix buttons do exactly
//    that - pick on the board, remove the prerequisite, return to the giver, remove the objective), a Confirm by the
//    explicit confirmation it names. Confirmations belong to the draft object.
//  * Delete is guarded the same way: quests that require the deleted one must be re-linked first (one op re-links every
//    follower in this pack to the deleted quest's own prerequisite).
//  * The web app refuses to publish a pack whose quests audit with C8 errors (server side of the same law).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private Task<JsonObject>? _wbGuardTask;
    private string _wbGuardTaskRefs = "";
    private JsonObject? _wbGuardFacts;
    private string _wbGuardFactsRefs = "";
    private string _wbGuardError = "";
    private readonly ConditionalWeakTable<QuestAuthoringLaw.Draft, HashSet<string>> _wbQuestConfirmed = new();
    private readonly Dictionary<uint, HashSet<string>> _wbDeleteConfirmed = new();
    /// <summary>The players' quest-state reset chosen for a draft ("log" or "all"); saved as a one-time quest-reset doc.</summary>
    private readonly ConditionalWeakTable<QuestAuthoringLaw.Draft, string> _wbQuestReset = new();

    // ── facts ───────────────────────────────────────────────────────────────

    private static string WbGuardRefs(QuestAuthoringLaw.Draft d, uint deleting = 0)
    {
        var c = new SortedSet<uint> { d.Giver, d.Ender };
        var o = new SortedSet<uint>();
        var items = new SortedSet<uint>();
        foreach (var g in d.Goals)
        {
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.Kill) c.Add(g.Target);
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.UseObject) o.Add(g.Target);
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.Collect) { items.Add(g.Item); c.Add(g.DropFrom); }
        }
        var q = new SortedSet<uint> { (uint)Math.Abs((long)d.PrevQuest), (uint)Math.Max(d.NextInChain, 0), d.Entry, deleting };
        var h = new SortedSet<uint> { d.Entry, deleting };
        static string J(SortedSet<uint> s) => string.Join(",", s.Where(x => x != 0));
        return $"{J(c)}|{J(o)}|{J(q)}|{J(items)}|{J(h)}";
    }

    /// <summary>Ask for the facts the draft needs; true while the answer for the current references is not here yet.</summary>
    private bool WbGuardFactsPending(string refs)
    {
        if (_wbGuardTask is { IsCompleted: true } t)
        {
            _wbGuardTask = null;
            if (t.IsFaulted) { _wbGuardError = t.Exception?.GetBaseException().Message ?? "facts failed"; _wbGuardFacts = null; }
            else { _wbGuardFacts = t.Result; _wbGuardError = ""; }
            _wbGuardFactsRefs = _wbGuardTaskRefs;
        }
        if (_wbGuardFactsRefs == refs) return false;
        if (_wbGuardTask is null && SuiWebAppUrl.Length > 0)
        {
            var parts = refs.Split('|').Select(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList()).ToArray();
            _wbGuardTaskRefs = refs;
            _wbGuardTask = _qaClient.FactsAsync(SuiWebAppUrl, parts[0], parts[1], parts[2], parts[3], parts[4]);
        }
        return SuiWebAppUrl.Length > 0;
    }

    private JsonObject? WbFact(string table, uint id) =>
        _wbGuardFacts?[table]?[id.ToString(CultureInfo.InvariantCulture)] as JsonObject;

    /// <summary>Pack docs that will be in the world after the next publish: this pack's and every enabled pack's.</summary>
    private IEnumerable<(int Pack, JsonObject Body)> WbWorldDocs(string kind)
    {
        var enabled = _wbState?.Packs.Where(p => p.Enabled).Select(p => p.Id).ToHashSet() ?? new HashSet<int>();
        foreach (var doc in (_wbDocs ?? new JsonArray()).OfType<JsonObject>())
            if ((string?)doc["kind"] == kind && doc["body"] is JsonObject b && (int?)doc["packId"] is int pack && (pack == _wbPackId || enabled.Contains(pack)))
                yield return (pack, b);
    }

    /// <summary>The guardrails' view of the world for one draft.</summary>
    private sealed class WbQuestWorld : QuestGuardrailLaw.IWorld
    {
        private readonly GameLoop _g;
        private readonly QuestAuthoringLaw.Draft _d;
        private readonly bool _factsLoaded;
        public WbQuestWorld(GameLoop g, QuestAuthoringLaw.Draft d, bool factsLoaded) { _g = g; _d = d; _factsLoaded = factsLoaded && g._wbGuardFacts is not null; }

        private static long N(JsonNode? n) => n is null ? 0 : WbNum(n);

        public int? CreatureSpawns(uint e)
        {
            int pack = _g.WbWorldDocs("dbrow:creature").Count(x => N(x.Body["id"]) == e)
                     + _g.WbWorldDocs("npc-replacement").Count(x => N(x.Body["replacementEntry"]) == e);
            if (_factsLoaded) return pack + (int)N(_g.WbFact("creatures", e)?["spawns"]);
            if (pack > 0) return pack;
            int census = _g._ttCreatures.Count(c => c.Unit.Entry == e);
            return census > 0 ? census : null;
        }

        public int? ObjectSpawns(uint e)
        {
            int pack = _g.WbWorldDocs("dbrow:gameobject").Count(x => N(x.Body["id"]) == e);
            if (_factsLoaded) return pack + (int)N(_g.WbFact("objects", e)?["spawns"]);
            return pack > 0 ? pack : null;
        }

        public int? CreatureQuestRoles(uint e)
        {
            int pack = _g.WbWorldDocs("dbrow:creature_questrelation").Concat(_g.WbWorldDocs("dbrow:creature_involvedrelation"))
                .Count(x => N(x.Body["id"]) == e && N(x.Body["quest"]) != _d.Entry);
            return _factsLoaded ? pack + (int)N(_g.WbFact("creatures", e)?["questRoles"]) : pack > 0 ? pack : null;
        }

        public string CreatureName(uint e) =>
            _g.WbWorldDocs("dbrow:creature_template").FirstOrDefault(x => N(x.Body["entry"]) == e).Body?["name"]?.ToString()
            ?? (string?)_g.WbFact("creatures", e)?["name"]
            ?? (_g.TabletopCreatureInfo(e) is { Name.Length: > 0 } c ? c.Name : $"creature {e}");

        public string ObjectName(uint e) => (string?)_g.WbFact("objects", e)?["name"] ?? _g.WbGoName(e);

        private JsonObject? PackQuest(uint id) =>
            _g.WbWorldDocs("dbrow:quest_template").FirstOrDefault(x => N(x.Body["entry"]) == id).Body;

        public bool? QuestExists(uint id)
        {
            if (PackQuest(id) is not null || _g.WbFact("quests", id) is not null) return true;
            if (_factsLoaded) return false;
            if (_g._qaBoardBase?.Quests.ContainsKey(id) == true || _g._qaBoard?.Quests.ContainsKey(id) == true) return true;
            return null;
        }

        public string QuestTitle(uint id) =>
            PackQuest(id)?["Title"]?.ToString() ?? (string?)_g.WbFact("quests", id)?["title"]
            ?? _g._qaBoard?.Quests.GetValueOrDefault(id)?.Title ?? $"quest {id}";

        public int? PrevOf(uint id)
        {
            if (id == _d.Entry) return _d.PrevQuest;
            if (PackQuest(id) is { } q) return (int)N(q["PrevQuestId"]);
            if (_g.WbFact("quests", id) is { } f) return (int)N(f["prev"]);
            return _factsLoaded ? 0 : null;
        }

        public bool? Startable(uint id)
        {
            if (PackQuest(id) is not null)
            {
                var givers = _g.WbWorldDocs("dbrow:creature_questrelation").Where(x => N(x.Body["quest"]) == id).Select(x => (uint)N(x.Body["id"])).ToList();
                if (givers.Count == 0) return false;
                var spawns = givers.Select(CreatureSpawns).ToList();
                return spawns.Any(s => s > 0) ? true : spawns.Any(s => s is null) ? null : false;
            }
            if (_g.WbFact("quests", id) is { } f) return N(f["spawnedStarters"]) > 0;
            return null;
        }

        public IReadOnlyList<(uint Id, string Title, bool SamePack)> Followers(uint id)
        {
            var list = new List<(uint, string, bool)>();
            foreach (var (pack, b) in _g.WbWorldDocs("dbrow:quest_template"))
            {
                uint e = (uint)N(b["entry"]);
                if (e != _d.Entry && e != id && Math.Abs(N(b["PrevQuestId"])) == id) list.Add((e, b["Title"]?.ToString() ?? $"quest {e}", pack == _g._wbPackId));
            }
            if (_g._wbGuardFacts?["followers"]?[id.ToString(CultureInfo.InvariantCulture)] is JsonArray live)
                foreach (var x in live.OfType<JsonObject>())
                {
                    uint e = (uint)N(x["id"]);
                    // A published pack quest is listed from its doc above (the doc is what a publish installs).
                    if (e == _d.Entry || list.Any(l => l.Item1 == e) || PackQuest(e) is not null) continue;
                    list.Add((e, (string?)x["title"] ?? $"quest {e}", false));
                }
            return list;
        }

        public int? ItemSources(uint item)
        {
            int pack = _g.WbWorldDocs("dbrow:creature_loot_template").Count(x => N(x.Body["item"]) == item)
                     + _g.WbWorldDocs("dbrow:npc_vendor").Count(x => N(x.Body["item"]) == item);
            if (_factsLoaded) return pack + (int)N(_g.WbFact("items", item)?["sources"]);
            return pack > 0 ? pack : null;
        }

        public string ItemName(uint item) => (string?)_g.WbFact("items", item)?["name"] is { Length: > 0 } n ? n : _g.WbItemName(item);

        public int DropSharedBy(uint creature, uint item)
        {
            var template = _g.WbWorldDocs("dbrow:creature_template").FirstOrDefault(x => N(x.Body["entry"]) == creature).Body;
            long loot = template is null ? 0 : N(template["loot_id"]);
            if (loot == 0) return 0;
            return _g.WbWorldDocs("dbrow:creature_loot_template").Count(x => N(x.Body["entry"]) == loot && N(x.Body["item"]) != item
                && double.TryParse(x.Body["ChanceOrQuestChance"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ch) && ch < 0);
        }

        public IReadOnlyCollection<uint> StockGivers(uint quest) =>
            quest < QuestAuthoringLaw.PackEntryBase && _g._wbStockEntry == quest && _g._wbStockQuest is { } s ? WbIds(s["givers"]) : Array.Empty<uint>();

        public IReadOnlyList<(string Name, string State)>? Holders(uint quest)
        {
            if (!_factsLoaded) return null;
            if (_g._wbGuardFacts?["holders"]?[quest.ToString(CultureInfo.InvariantCulture)] is not JsonArray a) return Array.Empty<(string, string)>();
            return a.OfType<JsonObject>().Select(x => ((string?)x["name"] ?? "?", (string?)x["state"] ?? "")).ToList();
        }
    }

    // ── the resolve list (Save) ─────────────────────────────────────────────

    /// <summary>Draw the checks for the open draft; returns true when Save may go ahead.</summary>
    private bool DrawWbQuestGuardrails(QuestAuthoringLaw.Draft d)
    {
        string refs = WbGuardRefs(d);
        bool pending = WbGuardFactsPending(refs);
        var findings = QuestGuardrailLaw.Check(d, new WbQuestWorld(this, d, !pending));
        var confirmed = _wbQuestConfirmed.GetValue(d, _ => new HashSet<string>());
        confirmed.IntersectWith(findings.Select(f => f.Key));   // a confirmation is for what it said; a changed finding asks again
        int open = QuestGuardrailLaw.Unresolved(findings, confirmed);

        ImGui.Separator();
        if (pending) ImGui.TextDisabled("Checking the world for this quest...");
        else if (_wbGuardError.Length > 0) ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f), "World checks unavailable: " + _wbGuardError);
        if (findings.Count == 0 && !pending) ImGui.TextColored(new Vector4(0.55f, 0.85f, 0.55f, 1f), "Checks: nothing in the world would break.");
        else if (findings.Count > 0)
            ImGui.TextUnformatted(open == 0 ? $"Checks: all {findings.Count} resolved." : $"Checks: {open} of {findings.Count} to resolve before saving");
        DrawWbFindings(findings, confirmed, f => WbApplyQuestFix(d, f));
        // A chosen reset belongs to the holders finding: gone when that finding is (no holders any more) or undone.
        if (_wbQuestReset.TryGetValue(d, out var scope))
        {
            if (!findings.Any(f => f.Key.StartsWith("holders:", StringComparison.Ordinal) && confirmed.Contains(f.Key))) _wbQuestReset.Remove(d);
            else ImGui.TextColored(new Vector4(0.95f, 0.76f, 0.31f, 1f), scope == "all"
                ? "On save: everyone who holds or finished this quest can take it again after the next publish that restarts the server."
                : "On save: the quest leaves its holders' quest logs at the next publish that restarts the server.");
        }
        return !pending && open == 0;
    }

    /// <summary>The same verdict without drawing: (facts still pending, unresolved findings). Save refuses on either.</summary>
    private (bool Pending, int Open, List<QuestGuardrailLaw.Finding> Findings) WbQuestGuardState(QuestAuthoringLaw.Draft d)
    {
        bool pending = WbGuardFactsPending(WbGuardRefs(d));
        var findings = QuestGuardrailLaw.Check(d, new WbQuestWorld(this, d, !pending));
        var confirmed = _wbQuestConfirmed.GetValue(d, _ => new HashSet<string>());
        return (pending, QuestGuardrailLaw.Unresolved(findings, confirmed), findings);
    }

    private void DrawWbFindings(List<QuestGuardrailLaw.Finding> findings, HashSet<string> confirmed, Action<QuestGuardrailLaw.Fix> apply)
    {
        foreach (var f in findings)
        {
            ImGui.PushID(f.Key);
            bool done = f.Level == QuestGuardrailLaw.Level.Confirm && confirmed.Contains(f.Key);
            var colour = done ? new Vector4(0.55f, 0.85f, 0.55f, 1f)
                : f.Level == QuestGuardrailLaw.Level.Blocker ? new Vector4(1f, 0.45f, 0.35f, 1f) : new Vector4(1f, 0.78f, 0.35f, 1f);
            string tag = done ? "Resolved" : f.Level == QuestGuardrailLaw.Level.Blocker ? "Must fix" : "Confirm";
            string area = f.Area switch
            {
                QuestGuardrailLaw.Area.Unreachable => "unreachable", QuestGuardrailLaw.Area.Uncompletable => "uncompletable",
                QuestGuardrailLaw.Area.SharedContent => "shared content", _ => "characters",
            };
            ImGui.TextColored(colour, $"{tag} · {area}");
            ImGui.TextWrapped(f.Message);
            ImGui.Indent();
            if (done) { if (ImGui.SmallButton("Undo")) { confirmed.Remove(f.Key); apply(new QuestGuardrailLaw.Fix("", "unresolve")); } }
            else
            {
                bool first = true;
                foreach (var fix in f.Fixes)
                {
                    if (fix.Action.StartsWith("pick:", StringComparison.Ordinal) && !TabletopActive) continue;   // board picks live on the table
                    if (!first) ImGui.SameLine();
                    first = false;
                    if (ImGui.SmallButton(fix.Label))
                    {
                        if (fix.Action == "confirm") confirmed.Add(f.Key);
                        else if (fix.Action.StartsWith("resolve:", StringComparison.Ordinal)) { confirmed.Add(f.Key); apply(fix); }
                        else apply(fix);
                    }
                }
                if (f.Level == QuestGuardrailLaw.Level.Blocker && !TabletopActive && f.Fixes.All(x => x.Action.StartsWith("pick:", StringComparison.Ordinal)))
                    ImGui.TextDisabled("Change it in the form above.");
            }
            ImGui.Unindent();
            ImGui.PopID();
        }
    }

    private void WbApplyQuestFix(QuestAuthoringLaw.Draft d, QuestGuardrailLaw.Fix fix)
    {
        string a = fix.Action;
        if (a == "clear:prev") d.PrevQuest = 0;
        else if (a == "clear:next") d.NextInChain = 0;
        else if (a == "ender:giver") d.Ender = 0;
        else if (a == "unretire") d.Retired = false;
        else if (a.StartsWith("resolve:reset:", StringComparison.Ordinal)) _wbQuestReset.AddOrUpdate(d, a["resolve:reset:".Length..]);
        else if (a == "unresolve") _wbQuestReset.Remove(d);
        else if (a.StartsWith("relink:", StringComparison.Ordinal)) WbRelinkFollowers(d.Entry, d.PrevQuest);
        else if (a.StartsWith("goal:remove:", StringComparison.Ordinal) && int.TryParse(a["goal:remove:".Length..], out int i) && i < d.Goals.Length)
            d.Goals[i] = new QuestAuthoringLaw.Objective();
        else if (a.StartsWith("pick:", StringComparison.Ordinal))
        {
            string key = a["pick:".Length..];                 // "npc:wbq-kill0" - the same key the picker's own button arms
            _ttPickFor = key;
            _ttPickGround = null;
            _ttPickWhat = key.StartsWith("object:", StringComparison.Ordinal) ? "an object pin" : "a creature figure or an NPC pin";
        }
    }

    // ── the delete guard ────────────────────────────────────────────────────

    /// <summary>Checks before deleting the open quest; true when the delete may go ahead.</summary>
    private bool DrawWbQuestDeleteGuard(uint entry, JsonObject savedQuest)
    {
        var probe = new QuestAuthoringLaw.Draft { Entry = entry };
        // The open draft's references already carry this quest (followers, holders): one fetch serves Save and Delete.
        string refs = WbGuardRefs(_wbQuest.Entry == entry ? _wbQuest : probe);
        bool pending = WbGuardFactsPending(refs);
        int prevOfDeleted = (int)WbNum(savedQuest["PrevQuestId"]);
        var findings = QuestGuardrailLaw.CheckDelete(entry, prevOfDeleted, new WbQuestWorld(this, probe, !pending));
        if (!_wbDeleteConfirmed.TryGetValue(entry, out var confirmed)) _wbDeleteConfirmed[entry] = confirmed = new HashSet<string>();
        if (pending) { ImGui.TextDisabled("Checking what depends on this quest..."); return false; }
        if (findings.Count == 0) return true;
        ImGui.TextWrapped("Before deleting:");
        DrawWbFindings(findings, confirmed, fix =>
        {
            if (!fix.Action.StartsWith("relink:", StringComparison.Ordinal)) return;
            WbRelinkFollowers(entry, prevOfDeleted);
        });
        return QuestGuardrailLaw.Unresolved(findings, confirmed) == 0;
    }

    /// <summary>One op: every quest of this pack that requires <paramref name="entry"/> requires its prerequisite instead.</summary>
    private void WbRelinkFollowers(uint entry, int prevOfDeleted)
    {
        var ctx = WbQuestContext();
        var items = new JsonArray();
        foreach (var q in WbDocBodies("dbrow:quest_template").Where(q => Math.Abs(WbNum(q["PrevQuestId"])) == entry && WbNum(q["entry"]) != entry))
        {
            var follower = QuestAuthoringLaw.Read(q, ctx);
            follower.PrevQuest = prevOfDeleted;
            foreach (var item in QuestAuthoringLaw.Items(follower, ctx)) items.Add(item!.DeepClone());
        }
        if (items.Count == 0) { _wbMessage = "No quest in this pack requires it."; return; }
        WbOp($"re-link followers of quest {entry}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"re-link followers of quest {entry}", items.ToJsonString()),
            _ => { _wbDocsTask = null; WbRequestDocs(); _wbGuardFactsRefs = ""; });
    }
}
