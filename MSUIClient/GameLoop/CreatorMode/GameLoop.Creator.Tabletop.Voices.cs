using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P5 slice 5 (shared_docs/QUEST_TABLETOP.md §13.3): what NPCs say around the quest open in the table's editor -
// the givers' quest greetings and every line the quest's start/end scripts speak. Text only (script actions are later).
//
//  * A Blizzard line or greeting is rewritten as a pack "text-override" (the web app changes that one row at publish with a
//    baseline; Restore original removes the rewrite). One pack per line: a line another pack rewrote opens in that pack.
//  * A pack NPC's greeting is its own pack row (dbrow:quest_greeting); a pack line is its own dbrow:broadcast_text.
//  * Checks: a line spoken in several places (other scripts, gossip pages) changes everywhere - confirm first.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private sealed record TtVoice(string Key, string Kind, string Where, uint Entry, int Type, string Published, bool Stock);

    private readonly Dictionary<string, string> _ttVoiceText = new();
    private readonly HashSet<string> _ttVoiceConfirmed = new();
    private Task<JsonObject>? _ttVoiceFactsTask;
    private string _ttVoiceFactsKey = "";
    private JsonObject? _ttVoiceFacts;
    private Task<Net.WorldPackClient.Reply>? _ttVoiceSaveTask;
    private string _ttVoiceStatus = "";

    /// <summary>The lines of the open quest (from the published board), greetings of its givers first.</summary>
    private List<TtVoice> TtVoices(uint questId)
    {
        var list = new List<TtVoice>();
        var board = _qaBoardBase ?? _qaBoard;
        if (board is null || !board.Quests.TryGetValue(questId, out var q)) return list;
        foreach (var g in q.Givers.Where(g => g.Kind == "npc").Select(g => g.Entry).Distinct())
        {
            var greet = board.Bundle.Greetings.FirstOrDefault(x => x.Kind == "npc" && x.Entry == g);
            bool stock = g < WbTemplateBase;
            if (greet is null && stock) continue;   // adding a greeting to a Blizzard NPC is a script-level change (later)
            list.Add(new TtVoice($"quest_greeting:{g}:0", "greeting", $"{board.EntityName("npc", g)} greets", g, 0, greet?.Text ?? "", stock));
        }
        foreach (var l in board.Bundle.Lines.Where(l => l.Command == 0 && l.TextId != 0 &&
                     (l.Phase == "start" && l.Script == q.StartScript || l.Phase == "end" && l.Script == q.CompleteScript)))
            list.Add(new TtVoice($"broadcast_text:{l.TextId}", "line",
                $"{(l.Phase == "start" ? "When the quest starts" : "When it is turned in")}{(l.Delay > 0 ? $", after {l.Delay} s" : "")}",
                l.TextId, 0, l.Text ?? "", l.TextId < WbTemplateBase));
        return list;
    }

    /// <summary>The rewrite of a Blizzard line in any pack (one pack per line), or a pack row's doc.</summary>
    private (int Pack, JsonObject Doc)? TtVoiceDoc(TtVoice v)
    {
        foreach (var doc in (_wbDocs ?? new JsonArray()).OfType<JsonObject>())
        {
            if ((int?)doc["packId"] is not int pack || doc["body"] is not JsonObject b) continue;
            string kind = (string?)doc["kind"] ?? "";
            if (v.Stock && kind == "text-override" && (string?)doc["docKey"] == v.Key) return (pack, doc);
            if (!v.Stock && v.Kind == "greeting" && kind == "dbrow:quest_greeting" && WbNum(b["entry"]) == v.Entry && WbNum(b["type"]) == 0) return (pack, doc);
            if (!v.Stock && v.Kind == "line" && kind == "dbrow:broadcast_text" && WbNum(b["entry"]) == v.Entry) return (pack, doc);
        }
        return null;
    }

    private static string TtVoiceDocText(TtVoice v, JsonObject doc) => doc["body"] is JsonObject b
        ? (string?)(v.Stock ? b["text"] : v.Kind == "greeting" ? b["content_default"] : b["male_text"]) ?? "" : "";

    /// <summary>The "What NPCs say" section under the quest form on the table.</summary>
    private void TtDrawVoicesPump()
    {
        if (_ttVoiceSaveTask is { IsCompleted: true } save)
        {
            _ttVoiceSaveTask = null;
            _ttVoiceStatus = save.IsCompletedSuccessfully && save.Result.Success ? "Saved. Players hear it after the next publish."
                : "Not saved: " + (save.Exception?.GetBaseException().Message ?? save.Result.Error);
            _wbDocsTask = null; WbRequestDocs();
        }
    }

    private void TtDrawVoices()
    {
        TtDrawVoicesPump();
        uint questId = _wbQuest.Entry;
        var voices = questId == 0 ? new List<TtVoice>() : TtVoices(questId);
        if (!WbQuestHeader("6. What NPCs say##tt-voices")) return;
        if (voices.Count == 0) { ImGui.TextWrapped("No greeting or spoken line belongs to this quest yet (adding lines to scripts comes later)."); return; }

        // Shared-use facts for the lines (where else each Blizzard line is spoken).
        string key = string.Join(",", voices.Where(v => v.Kind == "line").Select(v => v.Entry));
        if (key != _ttVoiceFactsKey && _ttVoiceFactsTask is null && key.Length > 0 && SuiWebAppUrl.Length > 0)
        {
            _ttVoiceFactsKey = key;
            _ttVoiceFactsTask = _qaClient.FactsAsync(SuiWebAppUrl, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(),
                Array.Empty<uint>(), voices.Where(v => v.Kind == "line").Select(v => v.Entry));
        }
        if (_ttVoiceFactsTask is { IsCompleted: true } ft) { _ttVoiceFactsTask = null; _ttVoiceFacts = ft.IsCompletedSuccessfully ? ft.Result : null; }

        foreach (var v in voices)
        {
            ImGui.PushID(v.Key);
            var owned = TtVoiceDoc(v);
            string saved = owned is { } o ? TtVoiceDocText(v, o.Doc) : v.Published;
            if (!_ttVoiceText.TryGetValue(v.Key, out var text)) _ttVoiceText[v.Key] = text = saved;
            ImGui.TextColored(AtlasGold, v.Where);
            if (v.Stock) ImGui.TextDisabled(owned is { } ow ? $"Blizzard line, rewritten by {_wbState?.Packs.FirstOrDefault(p => p.Id == ow.Pack)?.Name ?? $"pack {ow.Pack}"}" : "Blizzard line");
            if (ImGui.InputTextMultiline("##text", ref text, 2000, new Vector2(-1f, 60f * CreatorUiScale))) _ttVoiceText[v.Key] = text;
            var findings = TtVoiceFindings(v);
            _ttVoiceConfirmed.IntersectWith(_ttVoiceConfirmed.Where(k => !k.StartsWith(v.Key + "|", StringComparison.Ordinal) || findings.Any(f => $"{v.Key}|{f.Key}" == k)).ToList());
            var local = new HashSet<string>(_ttVoiceConfirmed.Where(k => k.StartsWith(v.Key + "|", StringComparison.Ordinal)).Select(k => k[(v.Key.Length + 1)..]));
            bool changed = text != saved;
            if (changed) DrawWbFindings(findings, local, _ => { });
            foreach (var k in local) _ttVoiceConfirmed.Add($"{v.Key}|{k}");
            bool ready = changed && text.Trim().Length > 0 && _wbPackId != 0 && _ttVoiceSaveTask is null && QuestGuardrailLaw.Unresolved(findings, local) == 0;
            if (!ready) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Save line")) TtVoiceSave(v, text, owned?.Pack);
            if (!ready) ImGui.EndDisabled();
            if (changed) { ImGui.SameLine(); if (ImGui.SmallButton("Revert")) _ttVoiceText[v.Key] = saved; }
            if (v.Stock && owned is { } own)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Restore original"))
                {
                    var items = new JsonArray { new JsonObject { ["kind"] = "text-override", ["key"] = v.Key, ["body"] = null } };
                    _ttVoiceSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, own.Pack, $"restore line {v.Key}", items.ToJsonString());
                    WbOp($"restore line {v.Key}", _ttVoiceSaveTask);
                    _ttVoiceText.Remove(v.Key);
                }
            }
            ImGui.TextDisabled(QuestAuthoringLaw.Preview(text));
            ImGui.PopID();
        }
        if (_ttVoiceStatus.Length > 0) ImGui.TextWrapped(_ttVoiceStatus);
    }

    /// <summary>A line spoken in several places changes all of them: confirm first.</summary>
    private List<QuestGuardrailLaw.Finding> TtVoiceFindings(TtVoice v)
    {
        var f = new List<QuestGuardrailLaw.Finding>();
        if (v.Kind != "line" || !v.Stock) return f;
        int? uses = _ttVoiceFacts?["texts"]?[v.Entry.ToString(CultureInfo.InvariantCulture)] is { } n ? (int)WbNum(n) : null;
        if (uses is > 1)
            f.Add(new($"voice.shared:{uses}", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                $"This line is spoken in {uses} places in the world; rewriting it changes every one of them.", new[] { new QuestGuardrailLaw.Fix("Change them all", "confirm") }));
        else if (uses is null)
            f.Add(new("voice.unknown", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                "Cannot check where else this line is spoken. Confirm rewriting it everywhere is fine.", new[] { new QuestGuardrailLaw.Fix("Confirm", "confirm") }));
        return f;
    }

    private void TtVoiceSave(TtVoice v, string text, int? ownerPack)
    {
        int pack = ownerPack ?? _wbPackId;
        JsonObject item;
        if (v.Stock)
            item = new JsonObject
            {
                ["kind"] = "text-override", ["key"] = v.Key,
                ["body"] = new JsonObject { ["table"] = v.Kind == "greeting" ? "quest_greeting" : "broadcast_text", ["entry"] = v.Entry, ["type"] = v.Type, ["text"] = text },
            };
        else if (v.Kind == "greeting")
            item = new JsonObject
            {
                ["kind"] = "dbrow:quest_greeting",
                ["body"] = new JsonObject { ["entry"] = v.Entry, ["type"] = 0, ["content_default"] = text, ["emote_id"] = 0, ["emote_delay"] = 0 },
            };
        else
        {
            var body = TtVoiceDoc(v) is { } d && d.Doc["body"] is JsonObject b ? (JsonObject)b.DeepClone() : new JsonObject { ["entry"] = v.Entry };
            body["male_text"] = text;
            if (string.IsNullOrEmpty((string?)body["female_text"]) || (string?)body["female_text"] == v.Published) body["female_text"] = text;
            item = new JsonObject { ["kind"] = "dbrow:broadcast_text", ["body"] = body };
        }
        string label = $"{(v.Kind == "greeting" ? "greeting" : "line")} {v.Key}";
        _ttVoiceSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, pack, label, new JsonArray { item }.ToJsonString());
        WbOp(label, _ttVoiceSaveTask);
        _ttVoiceStatus = "Saving...";
    }
}

