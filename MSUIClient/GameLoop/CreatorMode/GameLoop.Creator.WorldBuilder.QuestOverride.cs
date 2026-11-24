using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Stock quest overrides in the Quest Creator (shared_docs/QUEST_TABLETOP.md §13, slice 3). A Blizzard quest opens in the
// same form; saving stores ONE "quest-override" doc in the pack (QuestAuthoringLaw.OverrideItems) which the web app
// installs as an extra quest_template row at the override patch - same quest id, the stock row untouched, the original
// back the moment the override is removed or the pack disabled. One pack per stock quest: a quest another pack already
// overrides opens in that pack. A stock quest that already has a stock row at the override patch cannot be overridden
// additively; the editor says so and stays read-only (an in-place edit needs the owner's sign-off).
//
// Source of the form: the pack's override doc when there is one, else the stock row (GET /QuestAtlas/StockQuest), which
// also brings the stock givers/enders (kept, never removed - a stock quest may only GAIN pack NPCs) and its stock
// exploration triggers (their flag is kept).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private JsonObject? _wbStockQuest;              // the StockQuest reply for _wbStockEntry
    private uint _wbStockEntry;
    private Task<JsonObject>? _wbStockTask;
    private uint _wbStockTaskEntry;
    private string _wbStockError = "";
    private uint _wbRestoreReload;   // the next load of this stock quest discards the open (just removed) version

    private bool WbEditingStock => _wbQuest.Entry != 0 && _wbQuest.Entry < WbTemplateBase;

    /// <summary>The override doc of a stock quest in ANY pack (one pack may own it), with its pack.</summary>
    private (int Pack, JsonObject Body)? WbOverrideDoc(uint entry)
    {
        foreach (var doc in (_wbDocs ?? new JsonArray()).OfType<JsonObject>())
            if ((string?)doc["kind"] == "quest-override" && (string?)doc["docKey"] == entry.ToString(CultureInfo.InvariantCulture)
                && doc["body"] is JsonObject b && (int?)doc["packId"] is int pack)
                return (pack, b);
        return null;
    }

    private static List<uint> WbIds(JsonNode? n) => n is JsonArray a ? a.Select(x => (uint)WbNum(x)).Where(x => x != 0).ToList() : new();

    /// <summary>Fetch the stock row of <paramref name="entry"/>; true once it is here (or failed: see _wbStockError).</summary>
    private bool WbStockReady(uint entry)
    {
        if (_wbStockTask is { IsCompleted: true } t)
        {
            _wbStockTask = null;
            if (t.IsFaulted) { _wbStockError = t.Exception?.GetBaseException().Message ?? "failed"; _wbStockQuest = null; }
            else { _wbStockQuest = t.Result; _wbStockError = ""; }
            _wbStockEntry = _wbStockTaskEntry;
        }
        if (_wbStockEntry == entry && _wbStockTask is null) return true;
        if (_wbStockTask is null && SuiWebAppUrl.Length > 0)
        {
            _wbStockTaskEntry = entry;
            _wbStockTask = _qaClient.StockQuestAsync(SuiWebAppUrl, entry);
        }
        return SuiWebAppUrl.Length == 0;
    }

    /// <summary>The stock-quest part of the Quest Creator context (empty unless <paramref name="entry"/> is the stock
    /// quest whose row is loaded).</summary>
    private void WbStockContext(QuestAuthoringLaw.Context ctx, uint entry, out bool stock, out HashSet<uint> givers, out HashSet<uint> enders, out bool trigger)
    {
        stock = false; givers = new(); enders = new(); trigger = false;
        if (entry == 0 || entry >= WbTemplateBase || _wbStockEntry != entry || _wbStockQuest is null) return;
        stock = true;
        givers = WbIds(_wbStockQuest["givers"]).ToHashSet();
        enders = WbIds(_wbStockQuest["enders"]).ToHashSet();
        trigger = WbIds(_wbStockQuest["exploreTriggers"]).Any(id => id < 7000);   // a pack trigger is in the pack docs already
        // The row the form edits: the pack's override when it has one, else the stock row.
        var baseRow = WbOverrideDoc(entry) is { } o ? (JsonObject)o.Body.DeepClone() : (JsonObject)_wbStockQuest["row"]!.DeepClone();
        baseRow.Remove("retired");
        baseRow["entry"] = entry;
        ctx.Quests[entry] = baseRow;
        foreach (uint g in givers) ctx.GiverRelations.Add(new JsonObject { ["id"] = g, ["quest"] = entry });
        foreach (uint e in enders) ctx.EnderRelations.Add(new JsonObject { ["id"] = e, ["quest"] = entry });
    }

    /// <summary>Open a stock quest in the form (called by the table's pending-open loop). False while waiting.</summary>
    private bool WbOpenStockQuest(uint entry, out string status)
    {
        status = "";
        if (_wbDocs is null) { status = "Loading pack content..."; return false; }
        var owned = WbOverrideDoc(entry);
        if (owned is { } o && o.Pack != _wbPackId) { _wbPackId = o.Pack; WbRequestState(); }
        if (owned is null && _wbPackId == 0) { status = "A Blizzard quest: choose the pack that will own your changes."; return false; }
        if (!WbStockReady(entry)) { status = "Loading the quest..."; return false; }
        if (_wbStockQuest is null) { status = "Could not load this quest: " + _wbStockError; return true; }
        if (_wbStockQuest["overridePossible"]?.GetValue<bool>() != true)
        {
            status = $"Quest {entry} already has a stock row at patch {_wbStockQuest["overridePatch"]}, so a pack cannot replace it without editing " +
                     "Blizzard data in place. That needs the owner's sign-off; it stays read-only here.";
            return true;
        }
        WbQuestFollowPack();
        var row = owned is { } doc ? (JsonObject)doc.Body.DeepClone() : (JsonObject)_wbStockQuest["row"]!.DeepClone();
        row["entry"] = entry;
        WbLoadQuestDraft(row, discardLocal: _wbRestoreReload == entry);
        _wbRestoreReload = 0;
        _wbQuest.Retired = owned is { } d2 && d2.Body["retired"] is JsonValue r && r.TryGetValue<bool>(out bool retired) && retired;
        if (owned is null) _wbQuestSavedSigs.Remove((_wbPackId, entry));   // nothing stored in a pack yet
        else _wbQuestSavedSigs[(_wbPackId, entry)] = QuestDraftSignature(_wbQuest);
        _wbQuestSaveStatus = owned is null
            ? "A Blizzard quest. Saving stores your version in this pack under the same quest id; the original stays underneath."
            : $"Your pack's version of this Blizzard quest.";
        return true;
    }

    /// <summary>The stock-quest header in the form: what saving does, Retire, Restore the original.</summary>
    private void DrawWbStockQuestHeader(QuestAuthoringLaw.Draft d)
    {
        if (!WbEditingStock) return;
        var owned = WbOverrideDoc(d.Entry);
        ImGui.TextColored(new Vector4(0.95f, 0.76f, 0.31f, 1f), $"Blizzard quest {d.Entry}");
        ImGui.TextWrapped(owned is null
            ? "Saving stores your version in this pack under the same quest id. The original comes back when you restore it or disable the pack."
            : "Your pack replaces this quest. Restore the original to undo it; disabling the pack does the same.");
        bool retired = d.Retired;
        if (ImGui.Checkbox("Retire this quest (never offered again)##wbq-retire", ref retired)) d.Retired = retired;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Characters who already hold it keep it. Quests that require it must be re-linked first.");
        if (owned is { } o)
        {
            ImGui.BeginDisabled(_wbOps.Count > 0);
            if (WbConfirmButton("Restore the original", $"Remove this pack's version of quest {d.Entry}, with the givers and exploration area it added? The Blizzard quest comes back after the next publish. Undo brings your version back."))
                WbRestoreStockQuest(d.Entry, o.Pack);
            ImGui.EndDisabled();
        }
        ImGui.Separator();
    }

    private void WbRestoreStockQuest(uint entry, int pack)
    {
        var items = new JsonArray { new JsonObject { ["kind"] = "quest-override", ["key"] = entry.ToString(CultureInfo.InvariantCulture), ["body"] = null } };
        foreach (var doc in WbQuestDocs(entry)) items.Add(WbDeleteItem(doc));   // pack givers / exploration it added
        WbOp($"restore Blizzard quest {entry}", _wbClient.ContentAsync(SuiWebAppUrl, pack, $"restore Blizzard quest {entry}", items.ToJsonString()), _ =>
        {
            // Drop the cached docs: the reopen below must wait for content without the override, never reuse it.
            _wbDocs = null; _wbDocsTask = null; WbRequestDocs();
            _wbQuestWorkingDrafts.Remove((pack, entry));
            _wbQuestSavedSigs.Remove((pack, entry));
            _wbRestoreReload = entry;
            _wbQuestSaveStatus = "Restored: the Blizzard quest returns after the next publish.";
            if (TabletopActive && _ttEditOpen) TtEditQuest(entry);   // reopen from the stock row
        });
    }
}
