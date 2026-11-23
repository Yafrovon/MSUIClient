using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.World.Tabletop;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P5 slice 4 (shared_docs/QUEST_TABLETOP.md §13.3): move a STOCK spawn on the board - a quest giver, a turn-in
// NPC, an objective creature - as a pack "spawn-override" (the web app moves that one spawn row at publish, with a
// baseline that puts it back when the move is removed or the pack disabled; one pack per spawn).
//
//  * The figure card (or an NPC card whose NPC stands in one place) offers "Move on the board": the spawn's guid comes
//    from GET /QuestAtlas/Spawns (nearest to the clicked place), then the next click on the board is the new place.
//  * Checks before saving (the same must-resolve list): a waypoint walker cannot be moved (its path stays) - blocked;
//    a wanderer, a quest NPC and a long move need a confirmation.
//  * The board previews every move (saved in a pack, or being placed): pins and figures stand at the new place, a
//    dashed line leads from the old one. Pack NPCs are moved in World Builder > NPCs, not here.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private sealed class TtMoveDraft
    {
        public uint Entry;
        public int Map;
        public Vector3 Near;                 // the clicked place (spawn lookup)
        public uint Guid;
        public Vector3 From;
        public float Facing;
        public long Movement;
        public int QuestRoles = -1;
        public Vector3? To;
        public int OwnerPack;                // a pack that already moves this spawn (0 = none)
        public string Status = "Finding the spawn...";
        public readonly HashSet<string> Confirmed = new();
    }

    private TtMoveDraft? _ttMove;
    private Task<JsonArray>? _ttMoveSpawnsTask;
    private Task<JsonObject>? _ttMoveFactsTask;
    private Task<Net.WorldPackClient.Reply>? _ttMoveSaveTask;
    private string _ttMoveSig = "";
    private bool _ttMoveAwaitDocs;
    private readonly List<(uint Entry, Vector3 From, Vector3 To, bool Draft)> _ttMoves = new();

    /// <summary>Spawn moves of this pack and every enabled pack (what the next publish installs), plus the one being placed.</summary>
    private void TtMoveCollect()
    {
        _ttMoves.Clear();
        var enabled = _wbState?.Packs.Where(p => p.Enabled).Select(p => p.Id).ToHashSet() ?? new HashSet<int>();
        foreach (var doc in (_wbDocs ?? new JsonArray()).OfType<JsonObject>())
        {
            if ((string?)doc["kind"] != "spawn-override" || doc["body"] is not JsonObject b || (int?)doc["packId"] is not int pack) continue;
            if (pack != _wbPackId && !enabled.Contains(pack)) continue;
            if (_ttMove is { } mv && (string?)doc["docKey"] == $"creature:{mv.Guid}") continue;   // being edited: the draft wins
            _ttMoves.Add(((uint)WbNum(b["entry"]), new Vector3(TtF(b["fromX"]), TtF(b["fromY"]), TtF(b["fromZ"])),
                new Vector3(TtF(b["x"]), TtF(b["y"]), TtF(b["z"])), false));
        }
        if (_ttMove is { Guid: not 0, To: { } to } m) _ttMoves.Add((m.Entry, m.From, to, true));
    }

    /// <summary>Per frame (before the board is built): when the set of moves changed, rebuild the board from the moved bundle.</summary>
    private void TtMoveSync()
    {
        TtMovePump();
        TtMoveCollect();
        string sig = string.Join(";", _ttMoves.Select(m => Inv($"{m.Entry}:{m.From.X:0.0},{m.From.Y:0.0}>{m.To.X:0.0},{m.To.Y:0.0}")));
        if (sig == _ttMoveSig) return;
        _ttMoveSig = sig;
        if (_qaBoard is null) return;   // the next build applies them
        _qaBoard = null;                 // QaUpdate rebuilds from TtMovedBundle(_qaBundle) this frame
        _qaBoardBase = null;
        _ttEditSig = "";
    }

    /// <summary>The bundle with every previewed move applied to its NPC spawns (pins stand at the new place).</summary>
    private Q.ZoneBundle TtMovedBundle(Q.ZoneBundle b)
    {
        if (_ttMoves.Count == 0) return b;
        var npcs = new Dictionary<string, Q.Npc>(b.Npcs);
        foreach (var group in _ttMoves.GroupBy(m => m.Entry))
        {
            string key = group.Key.ToString(CultureInfo.InvariantCulture);
            if (!npcs.TryGetValue(key, out var n)) continue;
            var spawns = n.Spawns.Select(s => (float[])s.Clone()).ToList();
            foreach (var mv in group)
                foreach (var s in spawns)
                    if (s.Length >= 4 && MathF.Abs(s[1] - mv.From.X) < 2f && MathF.Abs(s[2] - mv.From.Y) < 2f) { s[1] = mv.To.X; s[2] = mv.To.Y; s[3] = mv.To.Z; }
            npcs[key] = new Q.Npc
            {
                Entry = n.Entry, Name = n.Name, Subname = n.Subname, LevelMin = n.LevelMin, LevelMax = n.LevelMax, Faction = n.Faction,
                NpcFlags = n.NpcFlags, Rank = n.Rank, SpawnTotal = n.SpawnTotal, Spawns = spawns,
            };
        }
        return new Q.ZoneBundle
        {
            Zone = b.Zone, Quests = b.Quests, Stubs = b.Stubs, Npcs = npcs, Objects = b.Objects, Items = b.Items,
            Triggers = b.Triggers, Lines = b.Lines, Greetings = b.Greetings,
        };
    }

    /// <summary>A census figure's place after the previewed moves.</summary>
    private Vector3 TtMovedSpawn(uint entry, Vector3 spawn)
    {
        foreach (var m in _ttMoves)
            if (m.Entry == entry && MathF.Abs(spawn.X - m.From.X) < 2f && MathF.Abs(spawn.Y - m.From.Y) < 2f) return m.To;
        return spawn;
    }

    // ── the move flow ───────────────────────────────────────────────────────

    private void TtMoveBegin(uint entry, Vector3 near)
    {
        if (_tabletop?.Board is not { } board || SuiWebAppUrl.Length == 0) return;
        TtPickCancel();
        _ttMove = new TtMoveDraft { Entry = entry, Map = (int)board.MapId, Near = near };
        _ttMoveSpawnsTask = _qaClient.SpawnsAsync(SuiWebAppUrl, "creature", entry, (int)board.MapId);
        _ttMoveFactsTask = _qaClient.FactsAsync(SuiWebAppUrl, new[] { entry }, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
        if (_wbDocs is null) WbRequestDocs();
        if (_wbState is null) WbRequestState();
    }

    private void TtMovePump()
    {
        if (_ttMove is not { } m) return;
        if (_ttMoveFactsTask is { IsCompleted: true } ft)
        {
            _ttMoveFactsTask = null;
            m.QuestRoles = ft.IsCompletedSuccessfully ? (int)WbNum(ft.Result["creatures"]?[m.Entry.ToString(CultureInfo.InvariantCulture)]?["questRoles"]) : -1;
        }
        if (_ttMoveSpawnsTask is { IsCompleted: true } st)
        {
            _ttMoveSpawnsTask = null;
            if (!st.IsCompletedSuccessfully) { m.Status = "Could not read the spawns: " + st.Exception?.GetBaseException().Message; return; }
            var best = st.Result.OfType<JsonObject>()
                .Select(s => (Node: s, At: new Vector3(TtF(s["x"]), TtF(s["y"]), TtF(s["z"]))))
                .OrderBy(s => Vector2.Distance(new(s.At.X, s.At.Y), new(m.Near.X, m.Near.Y))).FirstOrDefault();
            // A spawn already moved by a pack stands (on the board) at its new place: match against the move docs too.
            var moved = (_wbDocs ?? new JsonArray()).OfType<JsonObject>().Where(d => (string?)d["kind"] == "spawn-override" && d["body"] is JsonObject b
                && (uint)WbNum(b["entry"]) == m.Entry && Vector2.Distance(new(TtF(b["x"]), TtF(b["y"])), new(m.Near.X, m.Near.Y)) < 3f).FirstOrDefault();
            if (moved?["body"] is JsonObject mb)
            {
                m.Guid = (uint)WbNum(mb["guid"]);
                m.From = new Vector3(TtF(mb["fromX"]), TtF(mb["fromY"]), TtF(mb["fromZ"]));
                m.To = new Vector3(TtF(mb["x"]), TtF(mb["y"]), TtF(mb["z"]));
                m.Facing = TtF(mb["o"]);
                m.OwnerPack = (int?)moved["packId"] ?? 0;
                m.Movement = best.Node is not null && (uint)WbNum(best.Node["guid"]) == m.Guid ? WbNum(best.Node["movement"]) : 0;
            }
            else if (best.Node is not null && Vector2.Distance(new(best.At.X, best.At.Y), new(m.Near.X, m.Near.Y)) < 8f)
            {
                m.Guid = (uint)WbNum(best.Node["guid"]);
                m.From = best.At;
                m.Facing = TtF(best.Node["o"]);
                m.Movement = WbNum(best.Node["movement"]);
                m.OwnerPack = (_wbDocs ?? new JsonArray()).OfType<JsonObject>()
                    .FirstOrDefault(d => (string?)d["kind"] == "spawn-override" && (string?)d["docKey"] == $"creature:{m.Guid}")?["packId"]?.GetValue<int>() ?? 0;
            }
            else { m.Status = "No spawn of this creature stands there."; return; }
            if (m.OwnerPack != 0 && m.OwnerPack != _wbPackId) { _wbPackId = m.OwnerPack; WbRequestState(); }
            m.Status = "";
            if (m.To is null) TtMoveArmPick();
        }
        if (_ttMoveSaveTask is { IsCompleted: true } save)
        {
            _ttMoveSaveTask = null;
            if (save.IsCompletedSuccessfully && save.Result.Success)
            {
                // Keep the draft (and its preview) until the reloaded pack content shows the result: no flicker back.
                m.Status = "Saved.";
                _ttMoveAwaitDocs = true;
                _wbDocsTask = null; WbRequestDocs();
            }
            else m.Status = "Not saved: " + (save.Exception?.GetBaseException().Message ?? save.Result.Error);
        }
        if (_ttMoveAwaitDocs && _wbDocsTask is null && _wbDocs is not null && _wbOps.Count == 0)
        {
            _ttMoveAwaitDocs = false;
            _ttMove = null;
        }
    }

    private void TtMoveArmPick()
    {
        if (_ttMove is not { } m) return;
        _ttPickFor = "ground:move";
        _ttPickWhat = "the new place on the board";
        _ttPickGround = at => { if (_ttMove == m) { m.To = at; m.Confirmed.Clear(); } };
    }

    /// <summary>What moving this spawn would change, as the same must-resolve findings as the quest form.</summary>
    private List<QuestGuardrailLaw.Finding> TtMoveFindings(TtMoveDraft m)
    {
        var f = new List<QuestGuardrailLaw.Finding>();
        string name = TabletopCreatureInfo(m.Entry) is { Name.Length: > 0 } c ? c.Name : $"creature {m.Entry}";
        if (m.Movement == 2)
            f.Add(new("move.waypoints", QuestGuardrailLaw.Area.Uncompletable, QuestGuardrailLaw.Level.Blocker,
                $"{name} walks a fixed waypoint path; moving the spawn does not move the path, so it would walk straight back.", Array.Empty<QuestGuardrailLaw.Fix>()));
        else if (m.Movement == 1)
            f.Add(new("move.wanders", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                $"{name} wanders around its spawn point; it will wander around the new place.", new[] { new QuestGuardrailLaw.Fix("Fine", "confirm") }));
        if (m.QuestRoles > 0)
            f.Add(new($"move.quests:{m.QuestRoles}", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                $"{name} gives or takes {m.QuestRoles} quest(s); players will find them at the new place.", new[] { new QuestGuardrailLaw.Fix("Move them anyway", "confirm") }));
        else if (m.QuestRoles < 0)
            f.Add(new("move.quests.unknown", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                $"Cannot check which quests {name} gives or takes. Confirm moving them is fine.", new[] { new QuestGuardrailLaw.Fix("Confirm", "confirm") }));
        if (m.To is { } to && Vector2.Distance(new(to.X, to.Y), new(m.From.X, m.From.Y)) is var d && d > 400f)
            f.Add(new($"move.far:{(int)d / 100}", QuestGuardrailLaw.Area.SharedContent, QuestGuardrailLaw.Level.Confirm,
                Inv($"That is {d:0} yd from where players know to find {name}."), new[] { new QuestGuardrailLaw.Fix("Keep it there", "confirm") }));
        return f;
    }

    /// <summary>The card's move section (figure card, or an NPC card for an NPC standing in one place).</summary>
    private void TtMoveCardSection(uint entry, Vector3 at)
    {
        if (_liveTableOpen) return;
        QaSection("Place");
        if (entry >= WbTemplateBase) { ImGui.TextDisabled("A pack NPC: move it in World Builder > NPCs."); return; }
        if (_ttMove is not { } m || m.Entry != entry)
        {
            bool movedAlready = _ttMoves.Any(x => x.Entry == entry && Vector2.Distance(new(x.To.X, x.To.Y), new(at.X, at.Y)) < 3f);
            if (CreatorButton(movedAlready ? "Change the move##tt-move" : "Move on the board##tt-move")) TtMoveBegin(entry, at);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Your pack moves this Blizzard spawn; the original place comes back when the move is removed or the pack is disabled.");
            return;
        }
        if (m.Status.Length > 0) ImGui.TextWrapped(m.Status);
        if (m.Guid == 0) { if (CreatorButton("Cancel##tt-move")) _ttMove = null; return; }
        string pack = _wbState?.Packs.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "(choose a pack in the quest editor first)";
        ImGui.TextDisabled($"Spawn {m.Guid} · pack: {pack}");
        if (m.To is not { } to)
        {
            ImGui.TextWrapped("Click the new place on the board.");
            if (CreatorButton("Cancel##tt-move")) { TtPickCancel(); _ttMove = null; }
            return;
        }
        ImGui.TextWrapped(Inv($"From ({m.From.X:0}, {m.From.Y:0}) to ({to.X:0}, {to.Y:0}): {Vector2.Distance(new(to.X, to.Y), new(m.From.X, m.From.Y)):0} yd."));
        if (CreatorButton("Pick another place##tt-move")) TtMoveArmPick();
        var findings = TtMoveFindings(m);
        m.Confirmed.IntersectWith(findings.Select(x => x.Key));
        DrawWbFindings(findings, m.Confirmed, _ => { });
        bool ready = _wbPackId != 0 && _ttMoveSaveTask is null && QuestGuardrailLaw.Unresolved(findings, m.Confirmed) == 0;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton(_ttMoveSaveTask is not null ? "Saving..." : "Save the move##tt-move")) TtMoveSave();
        if (!ready) ImGui.EndDisabled();
        ImGui.SameLine();
        if (CreatorButton("Cancel##tt-move-c")) { TtPickCancel(); _ttMove = null; }
        if (m.OwnerPack != 0)
        {
            ImGui.SameLine();
            if (WbConfirmButton("Put it back##tt-move", $"Remove this pack's move of spawn {m.Guid}? It returns to its original place after the next publish."))
                TtMoveRemove(m);
        }
    }

    private JsonObject TtMoveBody(TtMoveDraft m) => new()
    {
        ["table"] = "creature", ["guid"] = m.Guid, ["entry"] = m.Entry, ["map"] = m.Map,
        ["x"] = Math.Round(m.To!.Value.X, 2), ["y"] = Math.Round(m.To.Value.Y, 2), ["z"] = Math.Round(m.To.Value.Z, 2), ["o"] = Math.Round(m.Facing, 3),
        ["fromX"] = Math.Round(m.From.X, 2), ["fromY"] = Math.Round(m.From.Y, 2), ["fromZ"] = Math.Round(m.From.Z, 2),
    };

    private void TtMoveSave()
    {
        if (_ttMove is not { Guid: not 0, To: not null } m || _wbPackId == 0) return;
        var items = new JsonArray { new JsonObject { ["kind"] = "spawn-override", ["key"] = $"creature:{m.Guid}", ["body"] = TtMoveBody(m) } };
        string label = $"move spawn {m.Guid} ({TabletopCreatureInfo(m.Entry).Name ?? m.Entry.ToString(CultureInfo.InvariantCulture)})";
        _ttMoveSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString());
        WbOp(label, _ttMoveSaveTask);
    }

    private void TtMoveRemove(TtMoveDraft m)
    {
        var items = new JsonArray { new JsonObject { ["kind"] = "spawn-override", ["key"] = $"creature:{m.Guid}", ["body"] = null } };
        string label = $"put spawn {m.Guid} back";
        _ttMoveSaveTask = _wbClient.ContentAsync(SuiWebAppUrl, m.OwnerPack, label, items.ToJsonString());
        WbOp(label, _ttMoveSaveTask);
    }

    /// <summary>Dashed lines from each previewed move's old place to its new one.</summary>
    private void TtMoveDrawOverlay()
    {
        if (_ttMoves.Count == 0 || _tabletop is null) return;
        var dl = ImGui.GetForegroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        foreach (var mv in _ttMoves)
        {
            if (!QaProject(QaShown(mv.From, 2f), display, out var a) || !QaProject(QaShown(mv.To, 2f), display, out var b)) continue;
            var dir = b - a;
            float len = dir.Length();
            if (len < 4f) continue;
            uint col = QaCol(mv.Draft ? new Vector3(1f, 0.78f, 0.35f) : new Vector3(0.75f, 0.6f, 0.95f), 0.95f);
            for (float t = 0; t < len; t += 12f) dl.AddLine(a + dir * (t / len), a + dir * (MathF.Min(t + 7f, len) / len), col, 2f);
            dl.AddCircle(a, 6f, col, 16, 2f);
        }
    }
}
