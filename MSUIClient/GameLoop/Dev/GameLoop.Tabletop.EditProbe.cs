using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.World.Tabletop;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P5 probe (shared_docs/QUEST_TABLETOP.md §13): MSUI_TABLETOP_EDIT=1 with MSUI_TABLETOP_PROBE=40 runs, after
// the Militia stage on Westfall, a whole edit on the board through the real paths - no form field is filled that a board
// click can fill:
//
//   new quest in the QA pack (created once, draft only) -> giver = a click on Gryan Stoutmantle's pin -> kill target = a
//   click on a creature figure -> explore = a click on the board -> prerequisite = a click on a pin offering one quest ->
//   the board shows the draft (pins, ring, dots, chain arc) -> Save (one audited op + pre-flight) -> reopen from the pack
//   -> delete (undoable op) -> close: the published board is back.
//
// Writes only to the test web app's QA pack (WSL, never 192.168.0.2 for World Builder work) and deletes what it saved.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly bool TabletopEditProbe = Environment.GetEnvironmentVariable("MSUI_TABLETOP_EDIT") == "1";
    private const string TtProbePackKey = "tabletop-edit-qa";
    private bool _ttEditProbeDone;
    private uint _ttEditProbeEntry;
    private uint _ttEditProbeGryan;
    private uint _ttEditProbePrev;
    private uint _ttEditProbeKill;
    private uint _ttEditProbeB;
    private QuestAuthoringLaw.Draft? _ttEditProbeA;
    private Task<Net.WorldPackClient.Reply>? _ttEditProbePackTask;

    /// <summary>Stages 30-49. Returns to stage 10's successor (the overview) when done.</summary>
    private void UpdateTabletopEditProbe(double now)
    {
        switch (_ttProbeStage)
        {
            case 30:   // packs and docs: find or create the QA pack
            {
                if (_wbState is null) { if (_wbStateTask is null) WbRequestState(); if (now - _ttProbeAt > 30) TtProbeAbort("pack state never loaded"); return; }
                if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); return; }
                if (_wbState.Packs.FirstOrDefault(p => p.PackKey == TtProbePackKey) is { } pack)
                {
                    _wbPackId = pack.Id;
                    // Leftovers of an interrupted run go first (each delete is an undoable op).
                    if (_wbOps.Count > 0 || _wbDocsTask is not null) return;
                    var leftovers = WbDocBodies("dbrow:quest_template").Where(q => ((string?)q["Title"] ?? "").StartsWith("Tabletop probe", StringComparison.Ordinal))
                        .Select(q => (uint)WbNum(q["entry"])).ToList();
                    if (leftovers.Count > 0)
                    {
                        if (now - _ttProbeAt > 60) { TtProbeAbort($"could not clear {leftovers.Count} leftover probe quest(s)"); return; }
                        Console.WriteLine($"[tabletop-probe] info edit: deleting {leftovers.Count} leftover probe quest(s) {string.Join(", ", leftovers)}");
                        WbDeleteDocs("delete leftover probe quests", leftovers.SelectMany(WbQuestDocs).ToList(), () => WbRequestDocs());
                        return;
                    }
                    Console.WriteLine($"[tabletop-probe] info edit: QA pack {pack.Id} ({pack.Name}, enabled {pack.Enabled})");
                    _ttProbeStage = 32; _ttProbeAt = now;
                    return;
                }
                if (_ttEditProbePackTask is null)
                {
                    _ttEditProbePackTask = _wbClient.CreatePackAsync(SuiWebAppUrl, TtProbePackKey, "Tabletop edit QA", "Quest Tabletop P5 probe (draft only, never enabled)");
                    return;
                }
                if (!_ttEditProbePackTask.IsCompleted) return;
                var r = _ttEditProbePackTask;
                _ttEditProbePackTask = null;
                if (r.IsFaulted || r.Result.Pack is null) { TtProbeAbort("could not create the QA pack: " + (r.Exception?.GetBaseException().Message ?? r.Result.Error)); return; }
                _wbPackId = r.Result.Pack.Id;
                _wbState = null; WbRequestState();
                _ttProbeStage = 31; _ttProbeAt = now;
                return;
            }

            case 31:
                if (_wbState is null || _wbStateTask is not null) return;
                _ttProbeStage = 32; _ttProbeAt = now;
                return;

            case 32:   // the panel's New quest; frame Sentinel Hill so Gryan's pin and the figures are on screen
            {
                TtEditNew();
                _wbQuest.Title = "Tabletop probe: Hill patrol";
                _wbQuest.Details = "Defenders of Sentinel Hill, $N, need a hand.";
                _wbQuest.Objectives = "Thin out the nearby creatures and walk the ridge.";
                _wbQuest.Level = 12; _wbQuest.MinLevel = 10;
                TabletopProbeCheck("edit: the editor opens for a new quest in the QA pack", _ttEditOpen && _wbQuestEditing == 0 && _wbPackId != 0,
                    $"open {_ttEditOpen}, pack {_wbPackId}, editing {_wbQuestEditing}");
                var hill = new Vector2(-10508f, 1046f);
                TabletopFlyTo(new Vector3(hill.X, hill.Y, _tabletop!.HeightAt(hill.X, hill.Y)), 260f, 0.9f, null, 1.0f);
                _ttProbeStage = 33; _ttProbeAt = now;
                return;
            }

            case 33:   // giver: click Gryan's pin
            {
                if (TabletopFlying || now - _ttProbeAt < 2.5 || _qaBoardBase is null) { if (now - _ttProbeAt > 30) TtProbeAbort("the draft overlay never appeared"); return; }
                var m = _qaBoard!;
                var gryan = m.Quests.Values.FirstOrDefault(q => q.Title == "The People's Militia")?.Givers.FirstOrDefault();
                _ttEditProbeGryan = gryan?.Entry ?? 0;
                // Show every quest so Gryan's pin is drawn (filters keep the selection only).
                var rect = _qaPinRects.FirstOrDefault(r => r.Pin.Kind == "npc" && r.Pin.Entry == _ttEditProbeGryan);
                if (rect.Pin is null) { if (now - _ttProbeAt > 8) TtProbeAbort($"Gryan's pin ({_ttEditProbeGryan}) not on screen; {_qaPinRects.Count} pins drawn"); return; }
                _ttPickFor = "npc:wbq-giver"; _ttPickWhat = "probe";
                bool consumed = TtPickClick((rect.Min + rect.Max) * 0.5f);
                TabletopProbeCheck("edit: a click on Gryan Stoutmantle's pin answers the giver pick",
                    consumed && _ttPicked.GetValueOrDefault("npc:wbq-giver") == _ttEditProbeGryan, $"picked {_ttPicked.GetValueOrDefault("npc:wbq-giver")}, status '{_ttEditStatus}'");
                _ttProbeStage = 34; _ttProbeAt = now;
                return;
            }

            case 34:   // the form consumed the pick; kill target: click a creature figure
            {
                if (now - _ttProbeAt < 0.5) return;
                TabletopProbeCheck("edit: the form took the giver from the board", _wbQuest.Giver == _ttEditProbeGryan && _ttEditProbeGryan != 0,
                    $"giver {_wbQuest.Giver}");
                _wbQuest.Goals[0].Kind = QuestAuthoringLaw.ObjectiveKind.Kill;
                _wbQuest.Goals[0].Count = 6;
                var display = ImGui.GetIO().DisplaySize;
                var figure = _ttUnitsThisFrame.Where(u => u.IsCreature && u.Entry != 0 && u.Entry != _ttEditProbeGryan)
                    .Select(u => (Unit: u, Ok: QaProject(u.Position + new Vector3(0f, 0f, 1.2f), display, out var px), Px: px))
                    .FirstOrDefault(x => x.Ok && x.Px.X > _creatorSidebarRight + 40f && x.Px.X < display.X - QaCardWidth - 40f
                        && !_qaPinRects.Any(r => Vector2.Distance((r.Min + r.Max) * 0.5f, x.Px) < 30f));
                if (figure.Unit is null) { TtProbeAbort($"no clickable creature figure ({_ttUnitsThisFrame.Count} units)"); return; }
                _ttPickFor = "npc:wbq-kill0"; _ttPickWhat = "probe";
                TtPickClick(figure.Px);
                uint got = _ttPicked.GetValueOrDefault("npc:wbq-kill0");
                TabletopProbeCheck("edit: a click on a creature figure answers the kill-target pick", got != 0,
                    $"picked {got} (aimed at {figure.Unit.Entry} {TabletopCreatureInfo(figure.Unit.Entry).Name}), status '{_ttEditStatus}'");
                _ttProbeStage = 35; _ttProbeAt = now;
                return;
            }

            case 35:   // explore: a click on the ground (the form's own pick button routes here on the table)
            {
                if (now - _ttProbeAt < 0.5) return;
                TabletopProbeCheck("edit: the form took the kill target", _wbQuest.Goals[0].Target != 0, $"target {_wbQuest.Goals[0].Target}");
                _wbQuest.Goals[1].Kind = QuestAuthoringLaw.ObjectiveKind.Explore;
                _wbQuest.Goals[1].ExploreRadius = 25f;
                var goal = _wbQuest.Goals[1];
                int map = WbQuestPickMap;
                _ttPickFor = "ground:probe"; _ttPickWhat = "probe";
                _ttPickGround = at => { goal.ExploreMap = map; goal.ExploreX = at.X; goal.ExploreY = at.Y; goal.ExploreZ = at.Z; };
                var display = ImGui.GetIO().DisplaySize;
                var aim = new Vector2((_creatorSidebarRight + display.X - QaCardWidth) * 0.5f, display.Y * 0.62f);
                TtPickClick(aim);
                bool placed = goal.ExploreX != 0 && goal.ExploreY != 0 && _tabletop!.Board is { } b && b.Contains(goal.ExploreX, goal.ExploreY);
                TabletopProbeCheck("edit: a click on the board sets the place to discover", placed,
                    Inv($"({goal.ExploreX:0}, {goal.ExploreY:0}, {goal.ExploreZ:0}) map {goal.ExploreMap}; status '{_ttEditStatus}'"));
                _ttProbeStage = 36; _ttProbeAt = now;
                return;
            }

            case 36:   // prerequisite: a pin that offers exactly one (other) quest
            {
                if (now - _ttProbeAt < 0.8) return;
                // A pin offering one quest whose centre no later-drawn pin covers (QaHit takes the topmost).
                var rects = _qaPinRects.ToList();
                bool Covered(int i, Vector2 c) => rects.Skip(i + 1).Any(r => c.X >= r.Min.X && c.X <= r.Max.X && c.Y >= r.Min.Y && c.Y <= r.Max.Y);
                var rect = rects.Select((r, i) => (r, i)).Where(x => x.r.Pin.Gives.Count == 1 && !x.r.Pin.Gives.Contains(_ttEditShownId)
                    && !Covered(x.i, (x.r.Min + x.r.Max) * 0.5f)).Select(x => x.r).FirstOrDefault();
                if (rect.Pin is null)
                {
                    // Pull back so more pins are on screen.
                    if (_ttRadius < 1500f) { _ttRadius *= 1.6f; _ttProbeAt = now; return; }
                    TtProbeAbort("no pin offering a single quest on screen");
                    return;
                }
                _ttEditProbePrev = rect.Pin.Gives.First();
                _ttPickFor = "quest:wbq-prev"; _ttPickWhat = "probe";
                TtPickClick((rect.Min + rect.Max) * 0.5f);
                TabletopProbeCheck("edit: a click on a single-quest pin answers the prerequisite pick",
                    _ttPicked.GetValueOrDefault("quest:wbq-prev") == _ttEditProbePrev, $"picked {_ttPicked.GetValueOrDefault("quest:wbq-prev")}, expected {_ttEditProbePrev}; status '{_ttEditStatus}'");
                // The form consumes a quest pick only while its chain section is open; the probe applies it as the form would.
                if (_ttPicked.Remove("quest:wbq-prev", out uint prev)) _wbQuest.PrevQuest = (int)prev;
                _ttProbeStage = 37; _ttProbeAt = now;
                return;
            }

            case 37:   // the board shows the draft
            {
                if (now - _ttProbeAt < 1.0) return;
                var m = _qaBoard!;
                bool inBoard = m.Quests.TryGetValue(TtDraftNewId, out var dq);
                TabletopProbeCheck("edit: the draft is a quest on the board (published board kept aside)", inBoard && _qaBoardBase is not null && _qaSelQuest == TtDraftNewId,
                    $"draft on board {inBoard}, base kept {_qaBoardBase is not null}, selected {_qaSelQuest}");
                if (dq is not null)
                {
                    var targets = m.ObjectiveTargets(dq);
                    TabletopProbeCheck("edit: the draft's giver pin is in focus", _qaPins.Any(p => p.InFocus && p.Entry == _ttEditProbeGryan), $"{_qaPins.Count(p => p.InFocus)} pins in focus");
                    TabletopProbeCheck("edit: the kill target shows where it stands", targets.Any(t => t.Kind == "kill" && t.Points.Count > 0),
                        string.Join("; ", targets.Select(t => $"{t.Kind} {t.Points.Count}")));
                    TabletopProbeCheck("edit: the place to discover is ringed", _qaRings.Any(r => r.Trigger.Id == TtDraftTriggerId && r.Strong), $"{_qaRings.Count} rings");
                    TabletopProbeCheck("edit: the prerequisite draws a chain arc into the draft", m.Edges.Any(e => e.From == _ttEditProbePrev && e.To == TtDraftNewId),
                        $"{m.Edges.Count(e => e.To == TtDraftNewId)} edges into the draft");
                }
                QaFrameFocus();
                _ttProbeStage = 38; _ttProbeAt = now;
                return;
            }

            case 38:
                if (TabletopFlying || now - _ttProbeAt < TabletopLaw.FrameSeconds + 1.0) return;
                TabletopProbeShot("tabletop-40-edit-draft");
                _ttProbeStage = 50; _ttProbeAt = now;
                return;

            // ── slice 2: guardrails (QuestGuardrailLaw + live facts) ─────────────
            case 50:   // the draft as built is sound
            {
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) { if (now - _ttProbeAt > 30) TtProbeAbort("world facts never arrived: " + _wbGuardError); return; }
                TabletopProbeCheck("guard: the board-built draft has nothing to resolve", g.Open == 0 && _wbGuardError.Length == 0,
                    $"{g.Open} open: {string.Join(" | ", g.Findings.Select(f => f.Key))}; facts error '{_wbGuardError}'");
                _ttEditProbeKill = _wbQuest.Goals[0].Target;
                _wbQuest.Goals[0].Target = 6_999_999;   // no such creature: spawned nowhere
                _ttProbeStage = 51; _ttProbeAt = now;
                return;
            }

            case 51:   // an unspawned kill target is a blocker and Save refuses
            {
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) return;
                bool blocker = g.Findings.Any(f => f.Key.StartsWith("kill.nospawn:0:", StringComparison.Ordinal) && f.Level == QuestGuardrailLaw.Level.Blocker
                    && f.Fixes.Any(x => x.Action == "goal:remove:0"));
                WbSaveQuest();
                TabletopProbeCheck("guard: an unspawned kill target blocks Save (with fixes offered)", blocker && _wbQuestSaveTask is null && _wbQuestEditing == 0,
                    $"findings {string.Join(" | ", g.Findings.Select(f => $"{f.Level} {f.Key}"))}; message '{_wbMessage}'");
                _wbQuest.Goals[0].Target = _ttEditProbeKill;
                _wbQuest.PrevQuest = 6_999_998;         // no such quest
                _ttProbeStage = 52; _ttProbeAt = now;
                return;
            }

            case 52:   // a missing prerequisite is a blocker; its fix clears it
            {
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) return;
                var f = g.Findings.FirstOrDefault(x => x.Key == "prev.missing:6999998");
                if (f?.Fixes.FirstOrDefault(x => x.Action == "clear:prev") is { } fix) WbApplyQuestFix(_wbQuest, fix);
                TabletopProbeCheck("guard: a missing prerequisite blocks; 'Remove the prerequisite' resolves it",
                    f is { Level: QuestGuardrailLaw.Level.Blocker } && _wbQuest.PrevQuest == 0, $"finding {f?.Key ?? "none"}, prev now {_wbQuest.PrevQuest}");
                _wbQuest.PrevQuest = (int)_ttEditProbePrev;
                _wbQuest.Goals[0].Target = _ttEditProbeGryan;   // kill the quest giver himself
                _ttProbeStage = 53; _ttProbeAt = now;
                return;
            }

            case 53:   // killing a quest NPC needs an explicit confirmation
            {
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) return;
                var f = g.Findings.FirstOrDefault(x => x.Key.StartsWith("kill.questnpc:0:", StringComparison.Ordinal));
                int before = g.Open;
                if (f is not null) _wbQuestConfirmed.GetValue(_wbQuest, _ => new HashSet<string>()).Add(f.Key);
                int after = WbQuestGuardState(_wbQuest).Open;
                TabletopProbeCheck("guard: a kill target who gives quests needs a confirmation, and the confirmation resolves it",
                    f is { Level: QuestGuardrailLaw.Level.Confirm } && before > 0 && after == 0, $"finding '{f?.Message}', open {before} -> {after}");
                _wbQuest.Goals[0].Target = _ttEditProbeKill;
                _ttProbeStage = 39; _ttProbeAt = now;
                return;
            }

            case 39:   // save through the real op
            {
                if (now - _ttProbeAt < 1.0) return;
                if (WbQuestGuardState(_wbQuest).Pending) return;   // the world checks for the restored draft first
                var problems = QuestAuthoringLaw.Problems(_wbQuest, WbQuestContext());
                TabletopProbeCheck("edit: the draft has no blocking problems", problems.Count == 0, string.Join("; ", problems));
                WbSaveQuest();
                _ttProbeStage = 40; _ttProbeAt = now;
                return;
            }

            case 40:   // saved, checked, the badge says so
            {
                if ((_wbQuestSaveTask is not null || _wbOps.Count > 0 || _wbPendingQuestCheck is not null || _wbDocsTask is not null) && now - _ttProbeAt < 60) return;
                _ttEditProbeEntry = _wbQuestEditing;
                bool saved = _ttEditProbeEntry >= WbTemplateBase && _wbQuestSavedSigs.TryGetValue((_wbPackId, _ttEditProbeEntry), out var sig) && sig == QuestDraftSignature(_wbQuest);
                TabletopProbeCheck("edit: Save stores the quest in the pack (one op) and the board calls it saved", saved,
                    $"entry {_ttEditProbeEntry}; status '{_wbQuestSaveStatus}'; message '{_wbMessage}'");
                foreach (var f in _wbQuestFindings) Console.WriteLine($"[tabletop-probe] info edit pre-flight: {f}");
                TabletopProbeCheck("edit: after the save the board shows the quest under its real entry",
                    _qaBoard!.Quests.ContainsKey(_ttEditProbeEntry) && !_qaBoard.Quests.ContainsKey(TtDraftNewId), $"selected {_qaSelQuest}");
                TabletopProbeShot("tabletop-40-edit-saved");
                _ttProbeStage = 41; _ttProbeAt = now;
                return;
            }

            case 41:   // close, then reopen from the pack (what the card's Edit button does)
            {
                if (now - _ttProbeAt < 1.0) return;
                TtEditClose();
                TabletopProbeCheck("edit: closing brings the published board back", _qaBoardBase is null && _qaBoard is not null && !_qaBoard.Quests.ContainsKey(_ttEditProbeEntry));
                _wbQuest = new QuestAuthoringLaw.Draft(); _wbQuestEditing = 0;
                _wbQuestWorkingDrafts.Remove((_wbPackId, _ttEditProbeEntry));
                TtEditQuest(_ttEditProbeEntry);
                _ttProbeStage = 42; _ttProbeAt = now;
                return;
            }

            case 42:
            {
                if (_ttEditPending is not null && now - _ttProbeAt < 30) return;
                TabletopProbeCheck("edit: Edit on the table reopens the saved quest from its pack",
                    _ttEditOpen && _wbQuestEditing == _ttEditProbeEntry && _wbQuest.Title == "Tabletop probe: Hill patrol" && _wbQuest.Giver == _ttEditProbeGryan
                    && _wbQuest.Goals.Any(g => g.Kind == QuestAuthoringLaw.ObjectiveKind.Explore && g.ExploreX != 0) && _wbQuest.PrevQuest == (int)_ttEditProbePrev,
                    $"editing {_wbQuestEditing}, title '{_wbQuest.Title}', giver {_wbQuest.Giver}, prev {_wbQuest.PrevQuest}; status '{_ttEditStatus}'");
                bool badgeSaved = _wbQuestSavedSigs.TryGetValue((_wbPackId, _ttEditProbeEntry), out var sig) && sig == QuestDraftSignature(_wbQuest);
                TabletopProbeCheck("edit: a freshly reopened quest counts as saved (no false 'unsaved' badge)", badgeSaved);
                _ttProbeStage = 60; _ttProbeAt = now;
                return;
            }

            // ── slice 2: the delete guard (a follow-up quest must be re-linked first) ──
            case 60:   // save a follow-up B that requires A
            {
                if (_ttEditProbeB == 0 && _wbQuestSaveTask is null && _wbOps.Count == 0)
                {
                    if (_wbQuest.Title != "Tabletop probe: follow-up")
                    {
                        _ttEditProbeA = _wbQuest;
                        _wbQuest = new QuestAuthoringLaw.Draft
                        {
                            Title = "Tabletop probe: follow-up", Details = "Back to the ridge.", Objectives = "Again.", Level = 13, MinLevel = 10,
                            Giver = _ttEditProbeGryan, PrevQuest = (int)_ttEditProbeEntry, Zone = (int)_ttAreaId,
                        };
                        _wbQuest.Goals[0].Kind = QuestAuthoringLaw.ObjectiveKind.Kill; _wbQuest.Goals[0].Target = _ttEditProbeKill; _wbQuest.Goals[0].Count = 3;
                        _wbQuestEditing = 0;
                        return;
                    }
                    if (WbQuestGuardState(_wbQuest).Pending) return;
                    WbSaveQuest();
                    if (_wbQuestSaveTask is null) { TtProbeAbort("follow-up not saved: " + _wbMessage); return; }
                    _ttEditProbeB = uint.MaxValue;   // saving
                    return;
                }
                if (_wbQuestSaveTask is not null || _wbOps.Count > 0 || _wbDocsTask is not null) return;
                _ttEditProbeB = _wbQuest.Entry;
                Console.WriteLine($"[tabletop-probe] info guard: follow-up {_ttEditProbeB} requires {_ttEditProbeEntry}");
                _wbQuest = _ttEditProbeA!; _wbQuestEditing = _ttEditProbeEntry;
                _ttProbeStage = 61; _ttProbeAt = now;
                return;
            }

            case 61:   // deleting A is blocked by B; Re-link resolves it in one op
            {
                if (_wbDocs is null || _wbDocsTask is not null) return;
                var probe = new QuestAuthoringLaw.Draft { Entry = _ttEditProbeEntry };
                if (WbGuardFactsPending(WbGuardRefs(_wbQuest))) return;
                var findings = QuestGuardrailLaw.CheckDelete(_ttEditProbeEntry, (int)_ttEditProbePrev, new WbQuestWorld(this, probe, true));
                var f = findings.FirstOrDefault(x => x.Key == $"delete.follower:{_ttEditProbeB}");
                TabletopProbeCheck("guard: deleting a quest another quest requires is blocked, with a re-link fix",
                    f is { Level: QuestGuardrailLaw.Level.Blocker } && f.Fixes.Any(x => x.Action.StartsWith("relink:", StringComparison.Ordinal)),
                    string.Join(" | ", findings.Select(x => $"{x.Level} {x.Key}")));
                WbRelinkFollowers(_ttEditProbeEntry, (int)_ttEditProbePrev);
                _ttProbeStage = 62; _ttProbeAt = now;
                return;
            }

            case 62:
            {
                if ((_wbOps.Count > 0 || _wbDocsTask is not null || _wbDocs is null) && now - _ttProbeAt < 60) return;
                var probe = new QuestAuthoringLaw.Draft { Entry = _ttEditProbeEntry };
                var findings = QuestGuardrailLaw.CheckDelete(_ttEditProbeEntry, (int)_ttEditProbePrev, new WbQuestWorld(this, probe, true));
                long bPrev = WbDocBodies("dbrow:quest_template").Where(q => WbNum(q["entry"]) == _ttEditProbeB).Select(q => WbNum(q["PrevQuestId"])).FirstOrDefault();
                TabletopProbeCheck("guard: after Re-link the follow-up requires A's own prerequisite and the delete is clear",
                    findings.All(x => !x.Key.StartsWith("delete.follower:", StringComparison.Ordinal)) && bPrev == _ttEditProbePrev,
                    $"B prev {bPrev} (expected {_ttEditProbePrev}); {string.Join(" | ", findings.Select(x => x.Key))}");
                _ttProbeStage = 43; _ttProbeAt = now;
                return;
            }

            case 43:   // clean up: delete the probe quest (an undoable op), close the editor
            {
                if (now - _ttProbeAt < 1.0) return;
                uint entry = _ttEditProbeEntry;
                var docs = WbQuestDocs(entry);
                if (_ttEditProbeB is not 0 and not uint.MaxValue) docs.AddRange(WbQuestDocs(_ttEditProbeB));
                WbDeleteDocs($"delete probe quests {entry} {_ttEditProbeB}", docs, () => { });
                _ttProbeStage = 44; _ttProbeAt = now;
                return;
            }

            case 44:
            {
                if ((_wbOps.Count > 0 || _wbDocsTask is not null) && now - _ttProbeAt < 60) return;
                if (_wbDocs is null) { WbRequestDocs(); return; }
                TabletopProbeCheck("edit: the probe quests are deleted from the QA pack", WbQuestDocs(_ttEditProbeEntry).Count == 0 && WbQuestDocs(_ttEditProbeB).Count == 0,
                    $"{WbQuestDocs(_ttEditProbeEntry).Count + WbQuestDocs(_ttEditProbeB).Count} docs left");
                TtEditClose();
                _ttProbeStage = 70; _ttProbeAt = now;
                return;
            }

            // ── slice 3: a Blizzard quest as a pack override ──────────────────────
            case 70:   // open The People's Militia (12): from the stock row, unsaved, giver kept
            {
                if (_ttEditProbeOverrideStarted == false)
                {
                    _ttEditProbeOverrideStarted = true;
                    // Leftover override of an interrupted run: restore it first.
                    if (WbOverrideDoc(12) is { } left) { WbRestoreStockQuest(12, left.Pack); _ttProbeAt = now; return; }
                }
                if (_wbOps.Count > 0 || _wbDocsTask is not null) return;
                if (!_ttEditOpen) { TtEditQuest(12); return; }
                if (_ttEditPending is not null) { if (now - _ttProbeAt > 30) TtProbeAbort("stock quest never opened: " + _ttEditStatus); return; }
                TabletopProbeCheck("override: a Blizzard quest opens in the form from its stock row",
                    _wbQuest.Entry == 12 && WbEditingStock && _wbQuest.Title == "The People's Militia" && _wbQuest.Giver == _ttEditProbeGryan
                    && !_wbQuestSavedSigs.ContainsKey((_wbPackId, 12u)) && _wbStockQuestRowColumns() > 60,
                    $"entry {_wbQuest.Entry}, title '{_wbQuest.Title}', giver {_wbQuest.Giver}, stock columns {_wbStockQuestRowColumns()}, status '{_ttEditStatus}'");
                _wbQuest.Title = "The People's Militia (tabletop QA)";
                _wbQuest.Objectives = "Slay the Defias on the coast (QA override).";
                _ttProbeStage = 71; _ttProbeAt = now;
                return;
            }

            case 71:   // save as an override: one quest-override doc carrying the whole stock row, no stock relations
            {
                if (now - _ttProbeAt < 1.0) return;
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) return;
                var conf = _wbQuestConfirmed.GetValue(_wbQuest, _ => new HashSet<string>());
                // The holders: choose "drop it from their quest logs at publish" (a one-time reset request; never published here).
                var holders = g.Findings.FirstOrDefault(f => f.Key.StartsWith("holders:12:", StringComparison.Ordinal));
                var reset = holders?.Fixes.FirstOrDefault(x => x.Action == "resolve:reset:log");
                TabletopProbeCheck("reset: the holders finding offers 'accept', 'drop from logs' and 'reset for everyone'",
                    holders is not null && reset is not null && holders.Fixes.Any(x => x.Action == "resolve:reset:all") && holders.Fixes.Any(x => x.Action == "confirm"),
                    holders?.Message ?? "no holders finding");
                if (holders is not null && reset is not null) { conf.Add(holders.Key); WbApplyQuestFix(_wbQuest, reset); }
                foreach (var f in g.Findings.Where(f => f.Level == QuestGuardrailLaw.Level.Confirm)) conf.Add(f.Key);
                Console.WriteLine($"[tabletop-probe] info override checks: {string.Join(" | ", g.Findings.Select(f => $"{f.Level} {f.Key}"))}");
                TabletopProbeCheck("override: the quest that follows Militia must be acknowledged (shared content)",
                    g.Findings.Any(f => f.Key.StartsWith("followers:12:", StringComparison.Ordinal)));
                if (g.Findings.Any(f => f.Level == QuestGuardrailLaw.Level.Blocker)) { TtProbeAbort("unexpected blocker on the override"); return; }
                WbSaveQuest();
                _ttProbeStage = 72; _ttProbeAt = now;
                return;
            }

            case 72:
            {
                if ((_wbQuestSaveTask is not null || _wbOps.Count > 0 || _wbDocsTask is not null || _wbPendingQuestCheck is not null) && now - _ttProbeAt < 60) return;
                var doc = WbOverrideDoc(12);
                var resetDoc = WbQuestDocs(12).FirstOrDefault(x => (string?)x["kind"] == "quest-reset");
                TabletopProbeCheck("reset: Save also stores ONE quest-reset request (scope log, a request id) and clears the choice",
                    resetDoc?["body"] is System.Text.Json.Nodes.JsonObject rb && (string?)rb["scope"] == "log" && ((string?)rb["requestId"])?.Length == 32
                    && !_wbQuestReset.TryGetValue(_wbQuest, out _), resetDoc?.ToJsonString() ?? "missing");
                var relations = WbQuestDocs(12).Where(x => (string?)x["kind"] != "quest-reset").ToList();
                int cols = doc?.Body.Count ?? 0;
                TabletopProbeCheck("override: Save stores ONE quest-override doc with the whole stock row and the new title",
                    doc is { } d && d.Pack == _wbPackId && (string?)d.Body["Title"] == "The People's Militia (tabletop QA)" && cols > 60 && relations.Count == 0,
                    $"doc {(doc is null ? "missing" : $"pack {doc.Value.Pack}")}, {cols} columns, extra docs {relations.Count}; message '{_wbMessage}'");
                var m = _qaBoard!;
                TabletopProbeCheck("override: the board shows the changed title and keeps Gryan as the giver",
                    m.Quests.TryGetValue(12, out var q12) && q12.Title.StartsWith("The People's Militia (tabletop QA)", StringComparison.Ordinal)
                    && q12.Givers.Any(r => r.Entry == _ttEditProbeGryan), q12?.Title ?? "not on the board");
                _wbQuest.Retired = true;
                _ttProbeStage = 73; _ttProbeAt = now;
                return;
            }

            case 73:   // retiring Militia would strand quest 13 (a Blizzard follower): blocked, no re-link possible
            {
                var g = WbQuestGuardState(_wbQuest);
                if (g.Pending) return;
                var f = g.Findings.FirstOrDefault(x => x.Key == "retire.follower:13");
                WbSaveQuest();
                TabletopProbeCheck("override: retiring a quest a Blizzard quest requires is blocked (keep it, or change that quest first)",
                    f is { Level: QuestGuardrailLaw.Level.Blocker } && f.Fixes.All(x => x.Action == "unretire") && _wbQuestSaveTask is null,
                    $"{f?.Message ?? "no finding"}; message '{_wbMessage}'");
                if (f?.Fixes.FirstOrDefault() is { } keep) WbApplyQuestFix(_wbQuest, keep);
                _ttProbeStage = 74; _ttProbeAt = now;
                return;
            }

            case 74:   // reopen while another pack is selected: the override opens in the pack that owns it
            {
                if (now - _ttProbeAt < 0.5) return;
                if (_ttEditProbeOwner == 0)
                {
                    _ttEditProbeOwner = _wbPackId;
                    TtEditClose();
                    _wbQuestWorkingDrafts.Remove((_wbPackId, 12u));
                    _wbPackId = _wbState?.Packs.FirstOrDefault(p => p.Id != _ttEditProbeOwner)?.Id ?? _wbPackId;
                    TtEditQuest(12);
                    return;
                }
                if (_ttEditPending is not null) return;
                TabletopProbeCheck("override: one pack per Blizzard quest - it reopens in the pack that owns it, as saved",
                    _wbPackId == _ttEditProbeOwner && _wbQuest.Title == "The People's Militia (tabletop QA)"
                    && _wbQuestSavedSigs.TryGetValue((_wbPackId, 12u), out var sig) && sig == QuestDraftSignature(_wbQuest),
                    $"pack {_wbPackId} (owner {_ttEditProbeOwner}), title '{_wbQuest.Title}'");
                WbRestoreStockQuest(12, _ttEditProbeOwner);
                _ttProbeStage = 75; _ttProbeAt = now;
                return;
            }

            case 75:   // Restore the original: the override doc is gone and the form shows the Blizzard quest again
            {
                if ((_wbOps.Count > 0 || _wbDocsTask is not null || _ttEditPending is not null) && now - _ttProbeAt < 60) return;
                TabletopProbeCheck("override: Restore the original removes the pack's version (and its pending reset request)",
                    WbOverrideDoc(12) is null && WbQuestDocs(12).Count == 0 && _wbQuest.Entry == 12 && _wbQuest.Title == "The People's Militia",
                    $"title '{_wbQuest.Title}', docs left {WbQuestDocs(12).Count}");
                TtEditClose();
                TtEditQuest(6402);   // Stormwind Rendezvous: has a stock patch-10 row
                _ttProbeStage = 76; _ttProbeAt = now;
                return;
            }

            case 76:   // a quest with a stock row at the override patch stays read-only (in-place edits need sign-off)
            {
                if (_ttEditPending is not null && now - _ttProbeAt < 30) return;
                TabletopProbeCheck("override: a quest already at the override patch is refused, read-only, with the reason",
                    _ttEditStock == 6402 && _ttEditStatus.Contains("sign-off", StringComparison.Ordinal), _ttEditStatus);
                TtEditClose();
                _ttProbeStage = 80; _ttProbeAt = now;
                return;
            }

            // ── slice 4: move a Blizzard spawn (Gryan Stoutmantle) as a pack spawn-override ──
            case 80:
            {
                if (_wbOps.Count > 0 || _wbDocsTask is not null || _qaBoard is null) return;
                // Leftover move of an interrupted run goes first.
                if ((_wbDocs ?? new()).OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "spawn-override" && (int?)d["packId"] == _wbPackId) is { } left)
                {
                    WbDeleteDocs("delete leftover probe move", new List<System.Text.Json.Nodes.JsonObject> { left }, () => { });
                    return;
                }
                var places = _qaBoard.SpawnsOnBoard("npc", _ttEditProbeGryan);
                if (places.Count != 1) { TtProbeAbort($"Gryan stands in {places.Count} places on the board"); return; }
                _ttEditProbeGryanAt = places[0];
                TtMoveBegin(_ttEditProbeGryan, places[0]);
                TabletopFlyTo(new Vector3(places[0].X, places[0].Y - 30f, _tabletop!.HeightAt(places[0].X, places[0].Y)), 220f, 0.9f, null, 1.0f);
                _ttProbeStage = 81; _ttProbeAt = now;
                return;
            }

            case 81:   // the spawn resolves to a guid, the next board click is the new place
            {
                if (_ttMove is not { } m) { TtProbeAbort("move flow closed"); return; }
                if (TabletopFlying || now - _ttProbeAt < 2.5) return;
                if ((m.Guid == 0 || m.QuestRoles == -1 && _ttMoveFactsTask is not null) && now - _ttProbeAt < 30) return;
                TabletopProbeCheck("move: Gryan's spawn resolves to its guid and the board waits for the new place",
                    m.Guid != 0 && _ttPickFor == "ground:move" && m.QuestRoles > 0, $"guid {m.Guid}, roles {m.QuestRoles}, pick '{_ttPickFor}', status '{m.Status}'");
                var display = ImGui.GetIO().DisplaySize;
                // 60 yd east of him: project that point and click it.
                var target = _ttEditProbeGryanAt + new Vector3(0f, -60f, 0f);
                if (!QaProject(QaShown(target), display, out var px)) { TtProbeAbort("target not on screen"); return; }
                TtPickClick(px);
                _ttProbeStage = 82; _ttProbeAt = now;
                return;
            }

            case 82:   // the checks: a quest NPC needs a confirmation; then save
            {
                if (_ttMove is not { To: { } to } m) { TtProbeAbort("no new place picked: " + _ttEditStatus); return; }
                var findings = TtMoveFindings(m);
                TabletopProbeCheck("move: moving a quest giver needs a confirmation before saving",
                    findings.Any(f => f.Key.StartsWith("move.quests:", StringComparison.Ordinal) && f.Level == QuestGuardrailLaw.Level.Confirm)
                    && QuestGuardrailLaw.Unresolved(findings, m.Confirmed) > 0,
                    Inv($"{string.Join(" | ", findings.Select(f => f.Key))}; to ({to.X:0}, {to.Y:0}), {Vector2.Distance(new(to.X, to.Y), new(m.From.X, m.From.Y)):0} yd"));
                var fake = new TtMoveDraft { Entry = m.Entry, Movement = 2, QuestRoles = 0, From = m.From, To = to };
                TabletopProbeCheck("move: a waypoint walker cannot be moved (its path stays) - blocked",
                    TtMoveFindings(fake).Any(f => f.Key == "move.waypoints" && f.Level == QuestGuardrailLaw.Level.Blocker));
                foreach (var f in findings.Where(f => f.Level == QuestGuardrailLaw.Level.Confirm)) m.Confirmed.Add(f.Key);
                _ttEditProbeMoveTo = to;
                _ttEditProbeMoveGuid = m.Guid;
                TtMoveSave();
                _ttProbeStage = 83; _ttProbeAt = now;
                return;
            }

            case 83:   // saved: one spawn-override doc; the board shows Gryan at the new place
            {
                if ((_ttMove is not null || _wbOps.Count > 0 || _wbDocsTask is not null || _wbDocs is null || _qaBoard is null) && now - _ttProbeAt < 60) return;
                var doc = (_wbDocs ?? new()).OfType<System.Text.Json.Nodes.JsonObject>()
                    .FirstOrDefault(d => (string?)d["kind"] == "spawn-override" && (string?)d["docKey"] == $"creature:{_ttEditProbeMoveGuid}");
                TabletopProbeCheck("move: Save stores one spawn-override doc in the pack (new place + original place)",
                    doc?["body"] is System.Text.Json.Nodes.JsonObject b && (int?)doc["packId"] == _wbPackId && MathF.Abs(TtF(b["x"]) - _ttEditProbeMoveTo.X) < 0.1f
                    && MathF.Abs(TtF(b["fromX"]) - _ttEditProbeGryanAt.X) < 0.1f, doc?.ToJsonString() ?? $"missing; message '{_wbMessage}'");
                var now0 = _qaBoard!.SpawnsOnBoard("npc", _ttEditProbeGryan);
                TabletopProbeCheck("move: the board previews Gryan at the new place (his pin moves with him)",
                    now0.Count == 1 && Vector2.Distance(new(now0[0].X, now0[0].Y), new(_ttEditProbeMoveTo.X, _ttEditProbeMoveTo.Y)) < 1f,
                    now0.Count == 1 ? Inv($"pin at ({now0[0].X:0}, {now0[0].Y:0})") : $"{now0.Count} places");
                TabletopProbeShot("tabletop-40-move");
                _ttProbeStage = 84; _ttProbeAt = now;
                return;
            }

            case 84:   // Put it back: reopen the move from its new place and remove it
            {
                if (now - _ttProbeAt < 1.5) return;
                if (_ttMove is null) { TtMoveBegin(_ttEditProbeGryan, _ttEditProbeMoveTo); return; }
                if (_ttMove.Guid == 0) { if (now - _ttProbeAt > 30) TtProbeAbort("move reopen failed: " + _ttMove.Status); return; }
                TabletopProbeCheck("move: a moved spawn reopens as the pack's move (owner pack known)", _ttMove.OwnerPack == _wbPackId && _ttMove.Guid == _ttEditProbeMoveGuid,
                    $"owner {_ttMove.OwnerPack}, guid {_ttMove.Guid}");
                TtPickCancel();
                TtMoveRemove(_ttMove);
                _ttProbeStage = 85; _ttProbeAt = now;
                return;
            }

            case 85:
            {
                if ((_ttMove is not null || _wbOps.Count > 0 || _wbDocsTask is not null || _wbDocs is null || _qaBoard is null) && now - _ttProbeAt < 60) return;
                var places = _qaBoard!.SpawnsOnBoard("npc", _ttEditProbeGryan);
                bool docGone = !(_wbDocs ?? new()).OfType<System.Text.Json.Nodes.JsonObject>().Any(d => (string?)d["kind"] == "spawn-override" && (int?)d["packId"] == _wbPackId);
                TabletopProbeCheck("move: Put it back removes the move and Gryan stands at his original place again",
                    docGone && places.Count == 1 && Vector2.Distance(new(places[0].X, places[0].Y), new(_ttEditProbeGryanAt.X, _ttEditProbeGryanAt.Y)) < 1f,
                    $"doc gone {docGone}; {(places.Count == 1 ? Inv($"pin at ({places[0].X:0}, {places[0].Y:0})") : $"{places.Count} places")}");
                _ttProbeStage = 90; _ttProbeAt = now;
                return;
            }

            // ── slice 5: what NPCs say (quest 155 "The Defias Brotherhood": a start-script line; Gryan's greeting) ──
            case 90:
            {
                if (_wbOps.Count > 0 || _wbDocsTask is not null) return;
                var leftovers = (_wbDocs ?? new()).OfType<System.Text.Json.Nodes.JsonObject>()
                    .Where(d => (string?)d["kind"] == "text-override" && (int?)d["packId"] == _wbPackId).ToList();
                if (leftovers.Count > 0) { WbDeleteDocs("delete leftover probe rewrites", leftovers, () => { }); return; }
                if (!_ttEditOpen) { TtEditQuest(155); return; }
                if (_ttEditPending is not null) { if (now - _ttProbeAt > 30) TtProbeAbort("quest 155 never opened: " + _ttEditStatus); return; }
                var voices = TtVoices(155);
                var line = voices.FirstOrDefault(v => v.Key == "broadcast_text:9");
                // 155 is started by the Defias Traitor (no greeting); Gryan's greeting belongs to Militia (12).
                var greet = TtVoices(12).FirstOrDefault(v => v.Key == $"quest_greeting:{_ttEditProbeGryan}:0");
                TabletopProbeCheck("voices: the quest's spoken line and its giver's greeting are listed for editing",
                    line is { Stock: true } && line.Published.StartsWith("Follow me", StringComparison.Ordinal) && greet is { Stock: true, Published.Length: > 10 },
                    string.Join(" | ", voices.Select(v => $"{v.Key} '{v.Published[..Math.Min(30, v.Published.Length)]}'")));
                if (line is null || greet is null) { TtProbeAbort("voices missing"); return; }
                _ttEditProbeLine = line; _ttEditProbeGreet = greet;
                _ttProbeStage = 91; _ttProbeAt = now;
                return;
            }

            case 91:   // where else the line is spoken (facts), then save the rewrite
            {
                if (_ttVoiceFactsKey != "9" || _ttVoiceFacts is null)
                {
                    if (_ttVoiceFactsTask is null && _ttVoiceFactsKey != "9")
                    {
                        _ttVoiceFactsKey = "9";
                        _ttVoiceFactsTask = _qaClient.FactsAsync(SuiWebAppUrl, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new uint[] { 9 });
                    }
                    if (_ttVoiceFactsTask is { IsCompleted: true } ft) { _ttVoiceFactsTask = null; _ttVoiceFacts = ft.IsCompletedSuccessfully ? ft.Result : null; }
                    if (now - _ttProbeAt < 30) return;
                }
                long uses = WbNum(_ttVoiceFacts?["texts"]?["9"]);
                var findings = TtVoiceFindings(_ttEditProbeLine!);
                TabletopProbeCheck("voices: the line's other uses are counted (a shared line needs a confirmation)",
                    uses >= 1 && (uses > 1) == findings.Any(f => f.Key.StartsWith("voice.shared:", StringComparison.Ordinal)), $"spoken in {uses} place(s)");
                TtVoiceSave(_ttEditProbeLine!, "Follow me, $n. The hideout is close (tabletop QA).", null);
                _ttProbeStage = 92; _ttProbeAt = now;
                return;
            }

            case 92:
            {
                if ((_ttVoiceSaveTask is not null || _wbOps.Count > 0 || _wbDocsTask is not null) && now - _ttProbeAt < 60) return;
                TtDrawVoicesPump();
                TtVoiceSave(_ttEditProbeGreet!, "Westfall needs every hand, $n (tabletop QA).", null);
                _ttProbeStage = 93; _ttProbeAt = now;
                return;
            }

            case 93:   // two text-override docs; the table reads the rewritten line
            {
                if ((_ttVoiceSaveTask is not null || _wbOps.Count > 0 || _wbDocsTask is not null || _wbDocs is null) && now - _ttProbeAt < 60) return;
                TtDrawVoicesPump();
                if (now - _ttProbeAt < 1.5) return;   // the overlay rebuilds from the refreshed docs
                bool lineDoc = TtRewrite("broadcast_text:9") == "Follow me, $n. The hideout is close (tabletop QA).";
                bool greetDoc = TtRewrite($"quest_greeting:{_ttEditProbeGryan}:0") == "Westfall needs every hand, $n (tabletop QA).";
                bool shown = _qaBoard?.Bundle.Lines.Any(l => l.TextId == 9 && (l.Text ?? "").EndsWith("(tabletop QA).", StringComparison.Ordinal)) == true
                    && _qaBoard.Bundle.Greetings.Any(g => g.Entry == _ttEditProbeGryan && (g.Text ?? "").EndsWith("(tabletop QA).", StringComparison.Ordinal));
                TabletopProbeCheck("voices: Save stores text-override docs for the line and the greeting, and the table reads them",
                    lineDoc && greetDoc && shown, $"line doc {lineDoc}, greeting doc {greetDoc}, shown on the board {shown}; status '{_ttVoiceStatus}'");
                var restore = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject { ["kind"] = "text-override", ["key"] = "broadcast_text:9", ["body"] = null },
                    new System.Text.Json.Nodes.JsonObject { ["kind"] = "text-override", ["key"] = $"quest_greeting:{_ttEditProbeGryan}:0", ["body"] = null },
                };
                WbOp("probe: restore original lines", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, "probe: restore original lines", restore.ToJsonString()),
                    _ => { _wbDocsTask = null; WbRequestDocs(); });
                _ttProbeStage = 94; _ttProbeAt = now;
                return;
            }

            case 94:
            {
                if ((_wbOps.Count > 0 || _wbDocsTask is not null || _wbDocs is null) && now - _ttProbeAt < 60) return;
                TabletopProbeCheck("voices: Restore original removes the rewrites", TtRewrite("broadcast_text:9") is null && TtRewrite($"quest_greeting:{_ttEditProbeGryan}:0") is null);
                TtEditClose();
                _ttEditProbeDone = true;
                _ttProbeStage = 10; _ttProbeAt = now;
                return;
            }
        }
    }

    private bool _ttEditProbeOverrideStarted;
    private TtVoice? _ttEditProbeLine, _ttEditProbeGreet;
    private Vector3 _ttEditProbeGryanAt, _ttEditProbeMoveTo;
    private uint _ttEditProbeMoveGuid;
    private int _ttEditProbeOwner;
    private int _wbStockQuestRowColumns() => (_wbStockQuest?["row"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;

    private void TtProbeAbort(string why)
    {
        TabletopProbeCheck("edit probe", false, why);
        TtEditClose();
        _ttEditProbeDone = true;
        _ttProbeStage = 10; _ttProbeAt = NowSeconds();
    }
}
