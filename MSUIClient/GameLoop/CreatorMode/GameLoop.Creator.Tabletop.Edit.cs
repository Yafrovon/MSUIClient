using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Tabletop;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P5, slice 1 (shared_docs/QUEST_TABLETOP.md §13.3): edit PACK quests on the board. The quest card's
// "Edit on the table" (or the panel's "New quest") opens the World Builder's own Quest Creator form
// (DrawWbQuestSection - QuestAuthoringLaw, one audited op per save, the pack pre-flight after) in place of the card.
// The table adds three things around that form, nothing else:
//
//  * Board picks: "Pick on the board" beside the giver/ender/kill/drop pickers (a figure or an NPC pin), the object
//    picker (an object pin), the chain pickers (a pin that offers one quest - stock quests included) and the
//    exploration "Pick the place on the board" (the ground under the cursor, on the board's map).
//  * The live draft: the draft is spliced into the zone bundle as a quest of its own (missing NPC spawns from the
//    packs' placements and the creature census, the drop source as an item source, the exploration spot as a trigger)
//    and the board is rebuilt from it whenever the draft changes - pins, chain arcs, journey, dots and the ring redraw
//    as you type. The published board comes back when the editor closes.
//  * A draft badge over the giver: "unsaved" until Save, then "saved - publish to play it".
//
// Stock quests stay read-only here; their overrides are slice 3 (§13.3).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>Board id of a draft that has no entry yet (never a real quest id).</summary>
    private const uint TtDraftNewId = 0xFFFF_FF00u;
    /// <summary>Synthetic trigger id for the draft's exploration spot.</summary>
    private const uint TtDraftTriggerId = 0xFFFF_FF01u;

    private bool _ttEditOpen;
    private QuestBoard? _qaBoardBase;              // the published board while the draft overlay is shown
    private string _ttEditSig = "";
    private uint _ttEditShownId;
    private uint? _ttEditPending;                  // waiting for the pack docs to open this quest
    private bool _ttEditRefetched;
    private uint _ttEditStock;   // a stock quest shown read-only (cannot be overridden additively)
    private string _ttEditStatus = "";

    // Board pick: "npc:<picker id>", "object:<picker id>", "quest:<picker id>" or "ground:<what>".
    private string? _ttPickFor;
    private string _ttPickWhat = "";
    private Action<Vector3>? _ttPickGround;
    private readonly Dictionary<string, uint> _ttPicked = new();

    /// <summary>The map a quest form's ground pick lands on: the board's on the table, the character's otherwise.</summary>
    private int WbQuestPickMap => TabletopActive && _tabletop?.Board is { } b ? (int)b.MapId : _config.Start.Map;

    /// <summary>Everything the draft carries that the board can show or the pack stores, as one comparable string.</summary>
    private static string QuestDraftSignature(QuestAuthoringLaw.Draft d)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{d.Entry}|{d.Title}|{d.Details}|{d.Objectives}|{d.RequestText}|{d.CompleteText}|{d.Level}|{d.MinLevel}|{d.Zone}|");
        sb.Append(CultureInfo.InvariantCulture, $"{d.Giver}|{d.Ender}|{d.Xp}|{d.Money}|{d.RepFaction}|{d.RepValue}|{d.PrevQuest}|{d.NextInChain}|{d.ExclusiveGroup}|{d.Retired}|");
        foreach (var g in d.Goals)
            sb.Append(CultureInfo.InvariantCulture, $"g{(int)g.Kind},{g.Target},{g.Count},{g.Item},{g.DropFrom},{g.DropChance},{g.ExploreMap},{g.ExploreX},{g.ExploreY},{g.ExploreZ},{g.ExploreRadius},{g.Text}|");
        foreach (var (item, count) in d.Choices) sb.Append(CultureInfo.InvariantCulture, $"c{item},{count}|");
        foreach (var (item, count) in d.Fixed) sb.Append(CultureInfo.InvariantCulture, $"f{item},{count}|");
        return sb.ToString();
    }

    // ── open / close ────────────────────────────────────────────────────────

    /// <summary>Card button: open a pack quest in the editor (waits for the pack docs if they are not here yet).</summary>
    private void TtEditQuest(uint questId)
    {
        TtPickCancel();
        _ttEditOpen = true;
        _ttEditPending = questId;
        _ttEditStock = 0;
        _ttEditStatus = "Loading pack content...";
        if (_wbDocs is null) WbRequestDocs();
        TtEditResolvePending();
    }

    /// <summary>Panel button: a new quest in the chosen pack, giver and objectives picked on the board.</summary>
    private void TtEditNew()
    {
        TtPickCancel();
        _ttEditOpen = true;
        _ttEditPending = null;
        if (_wbDocs is null) WbRequestDocs();
        if (_wbState is null) WbRequestState();
        WbQuestFollowPack();
        _wbQuestWorkingDrafts[(_wbPackId, _wbQuestEditing)] = _wbQuest;
        _wbQuest = _wbQuestWorkingDrafts.GetValueOrDefault((_wbPackId, 0u), new QuestAuthoringLaw.Draft());
        if (_wbQuest.Entry == 0 && _wbQuest.Zone == 0 && _ttAreaId != 0) _wbQuest.Zone = (int)_ttAreaId;
        _wbQuestEditing = 0;
        _wbQuestFindings.Clear();
        _wbQuestSaveStatus = "";
        _ttEditStatus = _wbPackId == 0 ? "Choose the pack that will own the quest." : "";
        _ttEditSig = "";
    }

    private void TtEditResolvePending()
    {
        if (_ttEditPending is { } stockId && stockId < WbTemplateBase)
        {
            // A Blizzard quest (slice 3): its pack override, else the stock row; one pack per stock quest.
            bool done = WbOpenStockQuest(stockId, out string why);
            _ttEditStatus = why;
            if (done) { _ttEditPending = null; _ttEditSig = ""; if (why.Length == 0) _ttEditStatus = ""; }
            if (done && _wbQuest.Entry != stockId) _ttEditStock = stockId;   // read-only (no override possible)
            return;
        }
        if (_ttEditPending is not { } id || _wbDocs is null) return;
        var doc = _wbDocs.OfType<JsonObject>().FirstOrDefault(x => (string?)x["kind"] == "dbrow:quest_template"
            && x["body"] is JsonObject b && WbNum(b["entry"]) == id);
        if (doc?["body"] is not JsonObject body || (int?)doc["packId"] is not int pack)
        {
            // The cached pack content may predate the quest: fetch it once more before giving up.
            if (!_ttEditRefetched && _wbDocsTask is null) { _ttEditRefetched = true; _wbDocs = null; WbRequestDocs(); return; }
            _ttEditPending = null;
            _ttEditRefetched = false;
            _ttEditStatus = $"Quest {id} is not stored in any pack on this web app, so it cannot be edited here yet.";
            return;
        }
        _ttEditPending = null;
        _ttEditRefetched = false;
        if (_wbPackId != pack) { _wbPackId = pack; WbRequestState(); }
        if (_wbState is null) WbRequestState();
        WbQuestFollowPack();
        WbLoadQuestDraft(body);
        _ttEditStatus = "";
        _ttEditSig = "";
    }

    private void TtEditClose()
    {
        TtPickCancel();
        _ttEditOpen = false;
        _ttEditShownId = 0;
        _ttEditStock = 0;
        _ttEditPending = null;
        _ttEditStatus = "";
        _ttEditSig = "";
        if (_qaBoardBase is not null)
        {
            _qaBoard = _qaBoardBase;
            _qaBoardBase = null;
            TtPruneVisible();
            _qaDirty = true;
        }
    }

    /// <summary>Drop visible / selected quest ids the current board does not hold (QaRebuild indexes them).</summary>
    private void TtPruneVisible()
    {
        if (_qaBoard is not { } m) return;
        _qaVisible.RemoveWhere(id => !m.Quests.ContainsKey(id));
        if (_qaSelQuest is { } sel && !m.Quests.ContainsKey(sel)) _qaSelQuest = null;
    }

    /// <summary>A new zone: the overlay belongs to the old board.</summary>
    private void TtEditZoneChanged()
    {
        _qaBoardBase = null;
        _ttEditSig = "";
        TtPickCancel();
    }

    // ── per frame ───────────────────────────────────────────────────────────

    /// <summary>After QaUpdate: open a waiting quest, and rebuild the board from the draft whenever the draft changed.</summary>
    private void TtEditUpdate()
    {
        if (!_ttEditOpen) return;
        TtEditResolvePending();
        if (_ttEditPending is not null || _qaBoard is null || _tabletop?.Board is not { } board) return;
        string sig = $"{_wbPackId}#{QuestDraftSignature(_wbQuest)}#{TtRewritesSig()}";
        if (sig == _ttEditSig) return;
        _ttEditSig = sig;
        _qaBoardBase ??= _qaBoard;
        var dq = TtDraftQuest(_qaBoardBase, _wbQuest);
        _qaBoard = new QuestBoard(TtOverlayBundle(_qaBoardBase.Bundle, dq, _wbQuest, (int)board.MapId),
            (map, x, y) => map == board.MapId && board.Contains(x, y));
        bool first = _ttEditShownId == 0;
        _ttEditShownId = dq.Id;
        _qaSelEntity = null; _qaSelFigure = null;
        TtPruneVisible();   // a save renames the draft (placeholder -> real entry): no id may outlive its board
        QaSelectQuest(dq.Id, fly: first && dq.Givers.Count > 0);
        QaRefresh();
    }

    /// <summary>The draft as a board quest, keeping what the form does not model from the published row.</summary>
    private static Q.Quest TtDraftQuest(QuestBoard published, QuestAuthoringLaw.Draft d)
    {
        uint id = d.Entry != 0 ? d.Entry : TtDraftNewId;
        var old = published.Quests.GetValueOrDefault(id);
        var q = new Q.Quest
        {
            Id = id, Patch = old?.Patch ?? 0, Custom = true, Home = true,
            Title = d.Title.Trim().Length > 0 ? d.Title : "(untitled quest)",
            Level = d.Level, MinLevel = d.MinLevel, MaxLevel = old?.MaxLevel ?? 0, ZoneOrSort = d.Zone,
            Races = old?.Races ?? 0, Classes = old?.Classes ?? 0, Type = old?.Type ?? 0, Method = old?.Method ?? 2,
            QuestFlags = old?.QuestFlags ?? 0, SpecialFlags = old?.SpecialFlags ?? 0, SuggestedPlayers = old?.SuggestedPlayers ?? 0,
            LimitTime = old?.LimitTime ?? 0, Prev = d.PrevQuest, Next = old?.Next ?? 0, NextInChain = d.NextInChain,
            ExclusiveGroup = d.ExclusiveGroup, BreadcrumbFor = old?.BreadcrumbFor ?? 0, SrcItem = old?.SrcItem ?? 0,
            SrcSpell = old?.SrcSpell ?? 0, StartScript = old?.StartScript ?? 0, CompleteScript = old?.CompleteScript ?? 0,
            Text = new Q.QuestText { Details = d.Details, Objectives = d.Objectives, RequestItems = d.RequestText, OfferReward = d.CompleteText, End = old?.Text.End },
        };
        foreach (var g in d.Goals)
        {
            switch (g.Kind)
            {
                case QuestAuthoringLaw.ObjectiveKind.Kill when g.Target != 0:
                    q.Objectives.Add(new Q.Objective("kill", g.Target, Math.Max(1, g.Count), g.Text, 0)); break;
                case QuestAuthoringLaw.ObjectiveKind.UseObject when g.Target != 0:
                    q.Objectives.Add(new Q.Objective("object", g.Target, Math.Max(1, g.Count), g.Text, 0)); break;
                case QuestAuthoringLaw.ObjectiveKind.Collect when g.Item != 0:
                    q.Objectives.Add(new Q.Objective("item", g.Item, Math.Max(1, g.Count), null, 0)); break;
                case QuestAuthoringLaw.ObjectiveKind.Explore when g.ExploreX != 0 || g.ExploreY != 0:
                    q.Objectives.Add(new Q.Objective("explore", TtDraftTriggerId, 1, null, 0)); break;
            }
        }
        q.Rewards = new Q.Rewards
        {
            Xp = d.Xp, Money = d.Money, Spell = old?.Rewards.Spell ?? 0, SpellCast = old?.Rewards.SpellCast ?? 0,
            Choice = d.Choices.Where(c => c.Item != 0).Select(c => new Q.ItemCount(c.Item, Math.Max(1, c.Count))).ToList(),
            Items = d.Fixed.Where(c => c.Item != 0).Select(c => new Q.ItemCount(c.Item, Math.Max(1, c.Count))).ToList(),
            Rep = d.RepFaction != 0 && d.RepValue != 0 ? new() { new Q.Rep(d.RepFaction, d.RepValue) } : new(),
        };
        bool stock = id < QuestAuthoringLaw.PackEntryBase;
        if (stock && old is not null) { q.Givers.AddRange(old.Givers); q.Enders.AddRange(old.Enders); }
        if (d.Retired) q.Title += " (retired)";
        if (d.Giver != 0 && !q.Givers.Any(r => r.Kind == "npc" && r.Entry == d.Giver)) q.Givers.Add(new Q.Ref("npc", d.Giver));
        uint ender = d.Ender != 0 ? d.Ender : d.Giver;
        if (ender != 0 && !q.Enders.Any(r => r.Kind == "npc" && r.Entry == ender)) q.Enders.Add(new Q.Ref("npc", ender));
        return q;
    }

    /// <summary>The published bundle with the draft in it, and whatever the draft points at that the bundle lacks.</summary>
    private Q.ZoneBundle TtOverlayBundle(Q.ZoneBundle published, Q.Quest dq, QuestAuthoringLaw.Draft d, int mapId)
    {
        var b = new Q.ZoneBundle
        {
            Zone = published.Zone,
            Quests = published.Quests.Where(q => q.Id != dq.Id).Append(dq).ToList(),
            Stubs = published.Stubs,
            Npcs = new Dictionary<string, Q.Npc>(published.Npcs),
            Objects = published.Objects,
            Items = new Dictionary<string, Q.Item>(published.Items),
            Triggers = new Dictionary<string, Q.Trigger>(published.Triggers),
            // Slice 5: rewritten lines and greetings read as rewritten on the table (saved rewrites, any pack).
            Lines = published.Lines.Select(l => l.TextId != 0 && TtRewrite($"broadcast_text:{l.TextId}") is { } t ? l with { Text = t } : l).ToList(),
            Greetings = published.Greetings.Select(g => g.Kind == "npc" && TtRewrite($"quest_greeting:{g.Entry}:0") is { } t ? g with { Text = t } : g).ToList(),
        };
        foreach (var r in dq.Givers.Concat(dq.Enders)) TtEnsureNpc(b, r.Entry, mapId);
        foreach (var g in d.Goals)
        {
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.Kill && g.Target != 0) TtEnsureNpc(b, g.Target, mapId);
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.Collect && g.Item != 0)
            {
                string key = g.Item.ToString(CultureInfo.InvariantCulture);
                var old = b.Items.GetValueOrDefault(key);
                var item = new Q.Item
                {
                    Id = g.Item, Name = old?.Name ?? WbItemName(g.Item), Quality = old?.Quality ?? 1,
                    ItemClass = old?.ItemClass ?? 12, Subclass = old?.Subclass ?? 0, Sources = new(old?.Sources ?? new()),
                };
                if (g.DropFrom != 0 && !item.Sources.Any(s => s.Kind == "npc" && s.Entry == g.DropFrom))
                {
                    item.Sources.Insert(0, new Q.Source("npc", g.DropFrom, g.DropChance));
                    TtEnsureNpc(b, g.DropFrom, mapId);
                }
                b.Items[key] = item;
            }
            if (g.Kind == QuestAuthoringLaw.ObjectiveKind.Explore && (g.ExploreX != 0 || g.ExploreY != 0))
                b.Triggers[TtDraftTriggerId.ToString(CultureInfo.InvariantCulture)] = new Q.Trigger
                {
                    Id = TtDraftTriggerId, Name = $"{dq.Title} (discover)", Map = g.ExploreMap,
                    X = g.ExploreX, Y = g.ExploreY, Z = g.ExploreZ, Radius = g.ExploreRadius,
                };
        }
        return b;
    }

    /// <summary>An NPC the published bundle does not place: its spawns from the packs' placements (published or not)
    /// and from the creature census, its name from the pack template, the census or the creator catalogue.</summary>
    private void TtEnsureNpc(Q.ZoneBundle b, uint entry, int mapId)
    {
        string key = entry.ToString(CultureInfo.InvariantCulture);
        if (b.Npcs.TryGetValue(key, out var have) && have.Spawns.Count > 0) return;
        var spawns = new List<float[]>();
        string? name = null;
        foreach (var doc in (_wbDocs ?? new JsonArray()).OfType<JsonObject>())
        {
            if (doc["body"] is not JsonObject body) continue;
            string kind = (string?)doc["kind"] ?? "";
            if (kind == "dbrow:creature" && WbNum(body["id"]) == entry && WbNum(body["map"]) == mapId)
                spawns.Add(new[] { mapId, TtF(body["position_x"]), TtF(body["position_y"]), TtF(body["position_z"]) });
            else if (kind == "dbrow:creature_template" && WbNum(body["entry"]) == entry) name ??= (string?)body["name"];
        }
        foreach (var (unit, spawn, _, _) in _ttCreatures)
            if (unit.Entry == entry && spawns.Count < 300) spawns.Add(new[] { mapId, spawn.X, spawn.Y, spawn.Z });
        if (name is null && TabletopCreatureInfo(entry) is { Name.Length: > 0 } info) name = info.Name;
        b.Npcs[key] = new Q.Npc
        {
            Entry = entry, Name = name ?? have?.Name ?? $"Creature {entry}", Subname = have?.Subname,
            LevelMin = have?.LevelMin ?? 0, LevelMax = have?.LevelMax ?? 0, Faction = have?.Faction ?? 0,
            NpcFlags = have?.NpcFlags ?? 0, Rank = have?.Rank ?? 0, SpawnTotal = spawns.Count, Spawns = spawns,
        };
    }

    /// <summary>Saved line rewrites (any pack): the overlay rebuilds when one is saved or restored.</summary>
    private string TtRewritesSig() => string.Join(";", (_wbDocs ?? new JsonArray()).OfType<JsonObject>()
        .Where(d => (string?)d["kind"] == "text-override").Select(d => $"{d["docKey"]}={d["body"]?["text"]?.ToString().GetHashCode()}"));

    private string? TtRewrite(string key) =>
        (_wbDocs ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "text-override" && (string?)d["docKey"] == key)?["body"]?["text"]?.ToString();

    private static float TtF(JsonNode? n) =>
        n is JsonValue v && float.TryParse(WbScalar(v), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    // ── board picks ─────────────────────────────────────────────────────────

    /// <summary>"Pick on the board" for an entity picker; returns the picked entry once the click lands.</summary>
    private uint TtBoardPickButton(string kind, string id, uint value, bool compact = false)
    {
        string key = $"{kind}:{id}";
        if (_ttPicked.Remove(key, out uint picked)) value = picked;
        bool armed = _ttPickFor == key;
        string label = armed ? "Click the board... (Esc)" : compact ? "Pick" : "Pick on the board";
        if (ImGui.SmallButton($"{label}##ttpick-{key}"))
        {
            if (armed) TtPickCancel();
            else
            {
                _ttPickFor = key;
                _ttPickGround = null;
                _ttPickWhat = kind switch
                {
                    "npc" => "a creature figure or an NPC pin",
                    "object" => "an object pin",
                    _ => "a pin that offers one quest",
                };
            }
        }
        if (ImGui.IsItemHovered() && !armed)
            ImGui.SetTooltip(kind == "quest" ? "Click a quest giver pin on the table (stock quests too)." : $"Click {(kind == "npc" ? "a creature figure or an NPC pin" : "an object pin")} on the table.");
        return value;
    }

    /// <summary>The table's version of WbPickButton: the next click on the board's ground is the answer.</summary>
    private bool TtGroundPickButton(string label, string what, Action<Vector3> done)
    {
        string key = "ground:" + what;
        bool armed = _ttPickFor == key;
        bool clicked = CreatorButton(armed ? "Click the board... (Esc)" : label);
        if (clicked)
        {
            if (armed) TtPickCancel();
            else { _ttPickFor = key; _ttPickWhat = what; _ttPickGround = done; }
        }
        return clicked;
    }

    private void TtPickCancel()
    {
        _ttPickFor = null;
        _ttPickGround = null;
        _ttPickWhat = "";
    }

    /// <summary>A click on the table while a pick is armed. True when it was consumed (hit or a clear miss message).</summary>
    private bool TtPickClick(Vector2 mouse)
    {
        if (_ttPickFor is not { } key) return false;
        string kind = key[..key.IndexOf(':')];
        if (kind == "ground")
        {
            var display = ImGui.GetIO().DisplaySize;
            Vector3? hit = _tabletop is not null && _ttCamera.ScreenPointToRay(mouse, display) is { } ray
                ? _tabletop.Pick(ray.Origin, ray.Direction, _ttCamera.FarPlane) : null;
            if (hit is not { } p) { _ttEditStatus = "That click missed the board."; return true; }
            float z = TtGroundAt(p.X, p.Y) ?? _tabletop!.HeightAt(p.X, p.Y);
            var done = _ttPickGround;
            TtPickCancel();
            done?.Invoke(new Vector3(p.X, p.Y, z));
            _ttEditStatus = Inv($"Picked ({p.X:F0}, {p.Y:F0}).");
            return true;
        }
        uint? entry = null;
        string why = "";
        switch (QaHit(mouse))
        {
            case QaPin pin when kind == "npc" && pin.Kind == "npc": entry = pin.Entry; break;
            case QaPin pin when kind == "object" && pin.Kind == "object": entry = pin.Entry; break;
            case QaPin pin when kind == "quest":
            {
                var offers = pin.Gives.Where(q => q != _ttEditShownId).ToList();
                if (offers.Count == 1) entry = offers[0];
                else why = offers.Count == 0 ? "That pin offers no other quest." : $"That pin offers {offers.Count} quests - zoom in or use the list.";
                break;
            }
            case WorldEntity unit when kind == "npc" && unit.IsCreature && unit.Entry != 0: entry = unit.Entry; break;
            case null: why = "Nothing there - click a pin or a figure."; break;
            default: why = kind == "npc" ? "Click a creature figure or an NPC pin." : kind == "object" ? "Click an object pin." : "Click a quest giver pin."; break;
        }
        if (entry is { } e)
        {
            _ttPicked[key] = e;
            TtPickCancel();
            _ttEditStatus = "";
        }
        else _ttEditStatus = why;
        return true;
    }

    /// <summary>Esc on the table cancels an armed pick before anything else.</summary>
    private bool TtPickEscape()
    {
        if (_ttPickFor is null) return false;
        TtPickCancel();
        _ttEditStatus = "Pick cancelled.";
        return true;
    }

    // ── drawing ─────────────────────────────────────────────────────────────

    /// <summary>The draft badge over the giver and the armed pick's cursor hint.</summary>
    private void TtEditDrawOverlay()
    {
        if (!_ttEditOpen) return;
        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();
        float ui = MathF.Max(1f, io.DisplaySize.Y / 1080f);
        if (_qaBoardBase is not null && _qaBoard is { } m && m.Quests.TryGetValue(_ttEditShownId, out var dq) && m.GiverPlace(dq) is { } at)
        {
            var shown = QaShown(at, QaPostBase * 3.6f);
            if (QaProject(shown, io.DisplaySize, out var px))
            {
                bool saved = _wbQuestEditing != 0 && _wbQuestSavedSigs.TryGetValue((_wbPackId, _wbQuestEditing), out var s) && s == QuestDraftSignature(_wbQuest);
                string text = saved ? "DRAFT - saved, publish to play it" : "DRAFT - unsaved";
                var size = QaTextSize(text, 13f * ui);
                var min = px - new Vector2(size.X * 0.5f + 7f * ui, size.Y + 10f * ui);
                var max = px + new Vector2(size.X * 0.5f + 7f * ui, -4f * ui);
                dl.AddRectFilled(min, max, QaCol(saved ? new Vector3(0.20f, 0.42f, 0.30f) : new Vector3(0.55f, 0.30f, 0.12f), 0.92f), 4f * ui);
                dl.AddRect(min, max, QaCol(new Vector3(1f, 0.92f, 0.7f), 0.8f), 4f * ui);
                dl.AddText(ImGui.GetFont(), 13f * ui, min + new Vector2(7f * ui, 3f * ui), QaCol(new Vector3(1f, 0.96f, 0.86f), 1f), text);
            }
        }
        if (_ttPickFor is not null && !io.WantCaptureMouse)
        {
            string hint = "Click " + _ttPickWhat + "  ·  Esc cancels";
            var p = io.MousePos + new Vector2(16f, 18f) * ui;
            var size = QaTextSize(hint, 13f * ui);
            dl.AddRectFilled(p - new Vector2(6f, 3f) * ui, p + size + new Vector2(6f, 3f) * ui, QaCol(new Vector3(0.08f, 0.06f, 0.05f), 0.85f), 4f * ui);
            dl.AddText(ImGui.GetFont(), 13f * ui, p, QaCol(new Vector3(0.95f, 0.76f, 0.31f), 1f), hint);
        }
    }

    /// <summary>The editor, drawn in the card's place (right side).</summary>
    private void TtEditDrawWindow()
    {
        var io = ImGui.GetIO();
        float s = CreatorPixelScale;
        float top = CreatorSidebarTop;
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - 14f * s, top), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowSize(new Vector2(QaCardWidth, io.DisplaySize.Y - top - 14f * s), ImGuiCond.Always);
        PushCreatorStyle();
        if (ImGui.Begin("##tt-edit", CreatorChromeFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings))
        {
            bool close = DrawCreatorPanelChrome(_wbQuestEditing == 0 ? "New quest" : "Edit quest", null);
            BeginCreatorContent();
            TtEditPackLine();
            if (_ttEditStatus.Length > 0) ImGui.TextWrapped(_ttEditStatus);
            if (_ttEditPending is null && _wbPackId != 0 && _ttEditStock == 0)
            {
                ImGui.TextDisabled("The board shows your draft as you edit. Save stores it in the pack; publish puts it in the game.");
                ImGui.Separator();
                DrawWbQuestSection();
                TtDrawVoices();   // slice 5: greetings and spoken lines
            }
            EndCreatorContent();
            if (close) TtEditClose();
        }
        ImGui.End();
        PopCreatorStyle();
    }

    private void TtEditPackLine()
    {
        if (_wbState is null) { ImGui.TextDisabled(_wbStateTask is null ? "Packs not loaded." : "Loading packs..."); if (_wbStateTask is null) WbRequestState(); return; }
        string current = _wbState.Packs.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "(choose a pack)";
        bool locked = _wbQuestEditing != 0 && _ttEditPending is null;   // an existing quest belongs to its pack
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (locked) ImGui.BeginDisabled();
        if (ImGui.BeginCombo("Pack##tt-edit", current))
        {
            foreach (var p in _wbState.Packs)
                if (ImGui.Selectable($"{p.Name}{(p.Enabled ? "" : "  (draft only)")}##tt-pack{p.Id}", p.Id == _wbPackId))
                {
                    _wbPackId = p.Id;
                    WbRequestState();
                    WbRequestDocs();
                    if (_ttEditPending is null) TtEditNew();   // a pack chosen for a new quest: start (or resume) that pack's new-quest draft
                }
            ImGui.EndCombo();
        }
        if (locked) ImGui.EndDisabled();
        if (_wbState.Packs.Count == 0) ImGui.TextWrapped("No packs yet: create one in World Builder > Pack first.");
    }
}
