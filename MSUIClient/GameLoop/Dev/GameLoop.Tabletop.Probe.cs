using System.Globalization;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop probe (shared_docs/QUEST_TABLETOP.md §10): a scripted offline capture. Activated by
// MSUI_TABLETOP_PROBE=<areaId>[@x:y],<areaId>[@x:y],... (default "40,7001": Westfall, Gilneas; @x:y = the world
// point the close framing centres on, default Sentinel Hill for Westfall and the board centre otherwise).
//
// It boots the creator world, checks the ADT reader's MCNK origin law on Azeroth_29_51 (§8), opens the tabletop,
// and per zone saves dumps/gameplay-tabletop-<area>-overview and -close (after the opening shot and after a framing
// flight); then closes the view and checks that the body, the game camera and the streaming centre are exactly as
// before and that nothing was sent. Console lines: [tabletop-probe] PASS|FAIL ..., then VERDICT. Observe-only.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string? TabletopProbeSpec = Environment.GetEnvironmentVariable("MSUI_TABLETOP_PROBE");

    private int _ttProbeStage;
    private double _ttProbeAt, _ttProbeStarted;
    private int _ttProbeFailures;
    private readonly List<(uint Area, Vector2? Close)> _ttProbeZones = new();
    private int _ttProbeZone;

    private void TabletopProbeCheck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[tabletop-probe] {(ok ? "PASS" : "FAIL")}  {name}" + (detail.Length > 0 ? $"  [{detail}]" : ""));
        if (!ok) _ttProbeFailures++;
    }

    private void TabletopProbeShot(string name)
    {
        _currentVantage = name;
        ArmGameplayDump();
        Console.WriteLine($"[tabletop-probe] shot {name}");
    }

    private void UpdateTabletopProbe()
    {
        if (TabletopProbeSpec is null || _ttProbeStage >= 99) return;
        double now = NowSeconds();
        if (TabletopProbePerfSampling(now)) return;
        if (_ttProbeStarted > 0 && now - _ttProbeStarted > 300 + 150 * Math.Max(1, _ttProbeZones.Count))
        {
            TabletopProbeCheck("probe finished in time", false, $"stuck at stage {_ttProbeStage}");
            TabletopProbeFinish();
            return;
        }

        switch (_ttProbeStage)
        {
            case 0:
            {
                if (_gl is null || _worldLoadStarted || _mpq is null) return;
                if (_ttProbeAt == 0) { _ttProbeAt = now; return; }
                if (now - _ttProbeAt < 1.0) return;
                _ttProbeStarted = now;
                // MSUI_TABLETOP_PATCH7=<file>: mount that patch-7 in THIS process only (owner, 2026-10-14: the box's
                // published Gilneas build for the capture; the client's own GameData files are not touched).
                if (Environment.GetEnvironmentVariable("MSUI_TABLETOP_PATCH7") is { Length: > 0 } patch7)
                {
                    bool mounted = File.Exists(patch7) && _mpq.ReplaceArchive("patch-7.MPQ", patch7);
                    ReloadWorldMapData();
                    ResetInstanceData();
                    TabletopProbeCheck("patch-7 mounted for this run", mounted, $"{patch7} ({(File.Exists(patch7) ? new FileInfo(patch7).Length : 0)} bytes)");
                }
                foreach (string part in (TabletopProbeSpec.Length > 0 ? TabletopProbeSpec : "40,7001").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] at = part.Split('@');
                    if (!uint.TryParse(at[0], out uint area)) continue;
                    Vector2? close = null;
                    if (at.Length > 1 && at[1].Split(':') is { Length: 2 } xy &&
                        float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float cx) &&
                        float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float cy)) close = new Vector2(cx, cy);
                    else if (area == 40) close = new Vector2(-10508f, 1046f);   // Sentinel Hill
                    _ttProbeZones.Add((area, close));
                }

                // §8: the ADT reader's MCNK origin law, on the tile the web build paid for.
                var adt = _mpq.ReadFile(@"World\Maps\Azeroth\Azeroth_29_51.adt") is { } bytes ? AdtTerrainReader.Parse(bytes, 51, 29) : null;
                var first = adt?.Chunks?.FirstOrDefault(c => c.IndexX == 0 && c.IndexY == 0);
                TabletopProbeCheck("MCNK origin: 0x68 = world X (north), 0x6C = world Y (west) on Azeroth_29_51",
                    first is not null && MathF.Abs(first.BaseX - -10133.333f) < 0.5f && MathF.Abs(first.BaseY - 1600f) < 0.5f,
                    first is null ? "tile/chunk missing" : $"BaseX {first.BaseX:0.0}, BaseY {first.BaseY:0.0}");
                // MCNR is stored (x, y, z) with Z up (2026-10-22: the reader had swapped up into Y since the first commit,
                // lighting terrain as if it faced sideways and streaking the table's slope projection on flat ground).
                var n0 = first?.NormalAt(0) ?? default;
                TabletopProbeCheck("MCNR terrain normals: up is Z (flat chunk 0 of Azeroth_29_51)", n0.Z > 0.9f,
                    $"({n0.X:0.00}, {n0.Y:0.00}, {n0.Z:0.00})");
                var relief = adt is null ? null : ReliefCompiler.Compile(adt, 29, 51);
                TabletopProbeCheck("relief compiler: every chunk origin agrees with its index placement",
                    relief is not null && relief.OriginsChecked == 256 && relief.OriginsAgree == 256,
                    relief is null ? "no relief" : $"{relief.OriginsAgree}/{relief.OriginsChecked}");

                // Boot the creator world at the first zone's centre; the table never moves the body after this.
                _config.DevTools = true;
                Vector2 spot = TryTabletopBoard(_ttProbeZones.FirstOrDefault().Area, out var board, out _) ? board.Centre : new Vector2(-10508f, 1046f);
                Settings.Creator.LocMap = board.MapId;
                Settings.Creator.LocMapName = string.IsNullOrEmpty(board.MapDirectory) ? "Azeroth" : board.MapDirectory;
                Settings.Creator.LocX = spot.X; Settings.Creator.LocY = spot.Y; Settings.Creator.LocZ = 300f;
                Console.WriteLine($"[tabletop-probe] zones {string.Join(", ", _ttProbeZones.Select(z => z.Area))}; entering the creator world at ({spot.X:0}, {spot.Y:0})");
                EnterOfflineWorld();
                _ttProbeStage = 1; _ttProbeAt = now;
                return;
            }

            case 1:   // the world is in and the body has landed: remember everything, open the table
            {
                if (_worldLoading || _controller is null || now - _ttProbeAt < 10.0) return;
                // MSUI_TABLETOP_WORLDSHOT=1: one shot of the ordinary world view first (terrain lighting comparisons).
                if (Environment.GetEnvironmentVariable("MSUI_TABLETOP_WORLDSHOT") == "1" && !_ttProbeWorldShot)
                {
                    _ttProbeWorldShot = true;
                    TabletopProbeShot($"tabletop-{_ttProbeZones[0].Area}-world");
                    _ttProbeAt = now - 8.0;
                    return;
                }
                Settings.Creator.Tabletop.AreaId = _ttProbeZones[0].Area;
                _creatorPanel = CreatorPanel.Tabletop;
                _ttProbeZone = 0;
                _ttProbeStage = 2; _ttProbeAt = now;
                return;
            }

            case 2:   // board built and the opening shot finished: overview
            {
                uint area = _ttProbeZones[_ttProbeZone].Area;
                if (_ttLoad is null && (_tabletop?.Board is null || _ttAreaId != area) && now - _ttProbeAt > 3.0)
                {
                    TabletopProbeCheck($"zone {area}: board", false, _ttStatus);
                    // A sweep goes on with the next zone.
                    _ttProbeZone++;
                    _ttProbeStage = _ttProbeZone < _ttProbeZones.Count ? 6 : 5;
                    _ttProbeAt = now;
                    return;
                }
                if (_ttLoad is not null || _tabletop?.Board is null || _ttAreaId != area || TabletopFlying || now - _ttBoardReadyAt < TabletopLaw.IntroSeconds + 0.8) return;
                if (!TabletopProbeNearSettled(now)) return;
                TabletopProbeCheck($"zone {area} ({_ttZoneName}): board built", _tabletop.TileCount > 0,
                    $"{_tabletop.TileCount} land tiles, {_tabletop.MinimapCount} minimap, span {_tabletop.Board.Value.Span:0} yd, baseZ {_tabletop.BaseZ:0.0}");
                TabletopProbeCheck($"zone {area}: minimap paint on every land tile", _tabletop.MinimapCount == _tabletop.TileCount,
                    $"{_tabletop.MinimapCount}/{_tabletop.TileCount}");
                TabletopProbeCheck($"zone {area}: MCNK origins agree", _ttOriginsChecked > 0 && _ttOriginsAgree == _ttOriginsChecked,
                    $"{_ttOriginsAgree}/{_ttOriginsChecked}");
                TabletopProbeNearChecks(area, "overview");
                Console.WriteLine($"[tabletop-probe] info zone {area} quests: {_qaStatus}; {_qaPins.Count} pins, {_qaArcs.Count} arcs, {_qaDots.Count} dots; fps {ImGuiNET.ImGui.GetIO().Framerate:0}");
                TabletopProbeShot($"tabletop-{area}-overview");
                TabletopProbePerfStart($"zone {area} overview", now);
                _ttProbeStage = Environment.GetEnvironmentVariable("MSUI_TABLETOP_LIVE") == "1" ? 20 : 3; _ttProbeAt = now;
                return;
            }

            case 3:   // close framing
            {
                if (now - _ttProbeAt < 1.0 || _tabletop?.Board is not { } b) return;
                var c = _ttProbeZones[_ttProbeZone].Close ?? b.Centre;
                // MSUI_TABLETOP_CLOSE_HALF=<yards>: a tighter close framing (texture checks at the owner's close-up range).
                float half = float.TryParse(Environment.GetEnvironmentVariable("MSUI_TABLETOP_CLOSE_HALF"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float closeHalf) && closeHalf > 0 ? closeHalf : MathF.Min(450f, b.Span * 0.12f);
                if (closeHalf > 0)
                    TabletopFlyTo(new Vector3(c.X, c.Y, _tabletop.HeightAt(c.X, c.Y)), closeHalf, 0.95f, null, TabletopLaw.FrameSeconds);
                else TabletopFrame(c - new Vector2(half), c + new Vector2(half));
                _ttProbeStage = 4; _ttProbeAt = now;
                return;
            }

            case 4:
            {
                if (TabletopFlying || now - _ttProbeAt < TabletopLaw.FrameSeconds + 0.8) return;
                if (!TabletopProbeNearSettled(now)) return;
                uint area = _ttProbeZones[_ttProbeZone].Area;
                TabletopProbeNearChecks(area, "close");
                TabletopProbeShot($"tabletop-{area}-close");
                TabletopProbePerfStart($"zone {area} close", now);
                _ttProbeStage = 7; _ttProbeAt = now;
                return;
            }

            case 7:   // the same close framing at k = 1 (true heights): the seam / regression reference (§10.3)
            {
                if (now - _ttProbeAt < 1.0) return;
                if (_ttProbeSavedK is null)
                {
                    _ttProbeSavedK = Settings.Creator.Tabletop.Exaggeration;
                    Settings.Creator.Tabletop.Exaggeration = 1f;
                    _ttProbeAt = now;
                    return;
                }
                uint area = _ttProbeZones[_ttProbeZone].Area;
                TabletopProbeCheck($"zone {area}: k = 1 makes the view transform the identity",
                    _ttNear is null || (_ttNear.View.K == 1f && _ttNear.View.Lift(123f) == 0f), $"k {_ttNear?.View.K}");
                TabletopProbeShot($"tabletop-{area}-close-k1");
                _ttProbeStage = 8; _ttProbeAt = now;
                return;
            }

            case 8:   // restore k; on Westfall, a creature close-up at Sentinel Hill
            {
                if (now - _ttProbeAt < 0.5) return;
                uint area = _ttProbeZones[_ttProbeZone].Area;
                if (_ttProbeSavedK is { } savedK)
                {
                    Settings.Creator.Tabletop.Exaggeration = savedK;
                    _ttProbeSavedK = null;
                    if (area == 40 && _tabletop is not null)
                    {
                        var hill = new Vector2(-10508f, 1046f);
                        TabletopFlyTo(new Vector3(hill.X, hill.Y, _tabletop.HeightAt(hill.X, hill.Y)), 140f, 0.95f, null, 1.0f);
                        _ttProbeAt = now;
                        return;
                    }
                }
                if (area == 40 && (TabletopFlying || now - _ttProbeAt < 2.0 || !TabletopProbeNearSettled(now))) return;
                if (area == 40)
                {
                    Console.WriteLine($"[tabletop-probe] info zone 40 figures: {_ttCensusStatus}; creatures drawn last frame {_ttNear?.CreaturesDrawn}, units passed {_ttUnitsThisFrame.Count}");
                    var eye = _ttCamera.Position;
                    Console.WriteLine($"[tabletop-probe] info figures camera: target ({_ttTarget.X:0.0}, {_ttTarget.Y:0.0}, {_ttTarget.Z:0.0}) r {_ttRadius:0.0} polar {_ttPolar:0.00} az {_ttAzimuth:0.00} eye ({eye.X:0.0}, {eye.Y:0.0}, {eye.Z:0.0}); shown ground {_tabletop?.ShownHeightAt(_ttTarget.X, _ttTarget.Y):0.0}, base {_tabletop?.BaseZ:0.0}, near ground {_ttNear?.GroundAt(_ttTarget.X, _ttTarget.Y):0.0}");
                    TabletopProbeCheck("zone 40: creatures stand in the near layer", _ttUnitsThisFrame.Count > 0, $"{_ttUnitsThisFrame.Count} units");
                    TabletopProbeShot("tabletop-40-figures");
                    _ttProbeStage = 9; _ttProbeAt = now;   // §10.6: The People's Militia
                    return;
                }
                _ttProbeZone++;
                if (_ttProbeZone < _ttProbeZones.Count)
                {
                    _ttProbeStage = 6; _ttProbeAt = now;   // next zone after the shot is written
                    return;
                }
                _ttProbeStage = 5; _ttProbeAt = now;
                return;
            }

            case 9:   // §10.6: select "The People's Militia" (Gryan Stoutmantle, Sentinel Hill) and read the table
            {
                if (now - _ttProbeAt < 1.0) return;
                if (_qaBoard is not { } m)
                {
                    if (now - _ttProbeAt < 20.0) return;
                    TabletopProbeCheck("zone 40: quest bundle loaded", false, _qaStatus);
                    _ttProbeStage = 10; _ttProbeAt = now;
                    return;
                }
                if (_qaSelQuest is null)
                {
                    var militia = m.Quests.Values.Where(q => q.Title == "The People's Militia").ToList();
                    TabletopProbeCheck("zone 40: The People's Militia is on the board", militia.Count > 0, $"{militia.Count} steps found");
                    if (militia.Count == 0) { _ttProbeStage = 10; _ttProbeAt = now; return; }
                    var chain = m.ChainOf[militia[0].Id];
                    QaSelectQuest(chain.Ids[0]);
                    _ttProbeAt = now;
                    return;
                }
                if (TabletopFlying || now - _ttProbeAt < TabletopLaw.FrameSeconds + 1.0 || !TabletopProbeNearSettled(now)) return;
                var sel = m.Quests[_qaSelQuest.Value];
                var ids = m.ChainOf[sel.Id].Ids;
                string steps = string.Join(" → ", ids.Select(i => $"{i} {m.Quests[i].Title} (depth {m.Depth.GetValueOrDefault(i)})"));
                TabletopProbeCheck("Militia chain lists its steps in order", ids.Count >= 3 &&
                    ids.Zip(ids.Skip(1)).All(p => m.Depth.GetValueOrDefault(p.First) < m.Depth.GetValueOrDefault(p.Second)), steps);
                var gryan = sel.Givers.FirstOrDefault();
                int loops = _qaArcs.Count(a => a.Loop && gryan is not null && m.PiecePlaces(gryan.Kind, gryan.Entry).Any(p => Vector2.Distance(new(p.X, p.Y), new(a.A.X, a.A.Y)) < 5f));
                TabletopProbeCheck("same-NPC hand-offs show as loops over the giver", loops >= ids.Count - 1,
                    $"{loops} loops over {(gryan is null ? "?" : m.EntityName(gryan.Kind, gryan.Entry))}");
                var focus = QaFocusPoints();
                int targets = m.ObjectiveTargets(sel).Count(t => t.Points.Count > 0);
                TabletopProbeCheck("selection frames giver, objective places and turn-in",
                    focus.Count >= 1 + targets && _qaPins.Any(p => p.InFocus), $"{focus.Count} focus points, {targets} placed objectives, {_qaPins.Count(p => p.InFocus)} pins in focus");
                Console.WriteLine($"[tabletop-probe] info militia: quest {sel.Id}; {_qaPins.Count} pins, {_qaArcs.Count} arcs, {_qaDots.Count} dots, {_qaRings.Count} rings; card open {QaCardOpen}");
                TabletopProbeShot("tabletop-40-militia");
                _ttProbeStage = 10; _ttProbeAt = now;
                return;
            }

            case 10:   // after the Militia shot: the overview with every visible quest's pieces, then the next zone
            {
                if (now - _ttProbeAt < 1.0) return;
                if (_qaSelQuest is not null)
                {
                    QaClearSelection();
                    TabletopOverview();
                    _ttProbeAt = now;
                    return;
                }
                if (TabletopFlying || now - _ttProbeAt < TabletopLaw.OverviewSeconds + 1.0 || !TabletopProbeNearSettled(now)) return;
                Console.WriteLine($"[tabletop-probe] info zone {_ttProbeZones[_ttProbeZone].Area} quests overview: {_qaVisible.Count} quests, {_qaPins.Count} pins, {_qaArcs.Count} arcs");
                TabletopProbeShot($"tabletop-{_ttProbeZones[_ttProbeZone].Area}-quests");
                _ttProbeZone++;
                _ttProbeStage = _ttProbeZone < _ttProbeZones.Count ? 6 : 5;
                _ttProbeAt = now;
                return;
            }

            case 6:
            {
                if (now - _ttProbeAt < 1.0) return;
                TabletopLoadZone(_ttProbeZones[_ttProbeZone].Area);
                _ttProbeStage = 2; _ttProbeAt = now;
                return;
            }

            case 5:   // leave the view; everything must be as it was
            {
                if (now - _ttProbeAt < 1.0) return;
                if (_creatorPanel == CreatorPanel.Tabletop) { _creatorPanel = CreatorPanel.None; _ttProbeAt = now; return; }
                if (_ttOpen) return;
                // Open-time snapshot vs close: game camera, body (position, facing, flying), streaming centre, map, packets.
                TabletopProbeCheck("leaving the view: camera, body, stream centre unchanged; nothing sent", _ttLastCloseClean, _ttLastCloseReport);
                _ttProbeStage = 11; _ttProbeAt = now;
                return;
            }

            case 20:   // the living board: wait for the brain feed and the looks, then pick a bot (a moving one if any)
            {
                uint area = _ttProbeZones[_ttProbeZone].Area;
                bool dressed = _live.Values.Any(b => b.View is not null);
                if ((_live.Count == 0 || !dressed) && now - _ttProbeAt < 30) return;
                Console.WriteLine($"[tabletop-probe] info zone {area} live: {_liveStatus}; {_live.Values.Count(b => b.View is not null)} dressed, {_live.Values.Count(b => b.Speed > 0)} moving, {_live.Values.Count(b => b.Stalled)} stalled, {_live.Values.Count(b => b.Dead)} dead");
                TabletopProbeCheck($"zone {area}: live bots on the board", _live.Count > 0, _liveStatus);
                var pick = _live.Values.Where(b => b.View is not null).OrderByDescending(b => b.Speed > 0).ThenBy(b => b.Guid).FirstOrDefault();
                if (pick is null) { _ttProbeStage = 3; _ttProbeAt = now; return; }
                LiveSelect(pick);
                _ttProbeLiveFrom = pick.Shown;
                Console.WriteLine($"[tabletop-probe] info live pick {pick.Name} ({pick.Guid}) {pick.Goal}/{pick.Step} at ({pick.Shown.X:0}, {pick.Shown.Y:0}) speed {pick.Speed:0.0}");
                _ttProbeStage = 21; _ttProbeAt = now;
                return;
            }

            case 21:   // follow it for a while: it must stand dressed in the near layer, and (if it walks) move
            {
                if (now - _ttProbeAt < 9.0 || TabletopFlying) return;
                if (!TabletopProbeNearSettled(now)) return;
                uint area = _ttProbeZones[_ttProbeZone].Area;
                var sel = _liveSel is { } g && _live.TryGetValue(g, out var b) ? b : null;
                bool drawn = sel is not null && _ttUnitsThisFrame.Any(u => LiveBotOf(u) == sel);
                TabletopProbeCheck($"zone {area}: the selected bot stands dressed in the near layer", drawn,
                    sel is null ? "selection lost" : $"{sel.Name}: view {(sel.View is null ? "none" : "built")}, items {sel.Look?.Items.Count ?? 0}, units this frame {_ttUnitsThisFrame.Count}, drawn {_ttNear?.CreaturesDrawn}");
                var eye = _ttCamera.Position;
                Console.WriteLine($"[tabletop-probe] info live camera: target ({_ttTarget.X:0.0}, {_ttTarget.Y:0.0}, {_ttTarget.Z:0.0}) r {_ttRadius:0.0} polar {_ttPolar:0.00} eye ({eye.X:0.0}, {eye.Y:0.0}, {eye.Z:0.0}); bot ({sel?.Shown.X:0.0}, {sel?.Shown.Y:0.0}, {sel?.Shown.Z:0.0}); far ground {_tabletop?.HeightAt(_ttTarget.X, _ttTarget.Y):0.0} shown {_tabletop?.ShownHeightAt(_ttTarget.X, _ttTarget.Y):0.0}; near ground {_ttNear?.GroundAt(_ttTarget.X, _ttTarget.Y):0.0}; base {_tabletop?.BaseZ:0.0} k {_tabletop?.Exaggeration:0.0}; near {_ttNear?.Status}");
                if (sel is not null)
                    Console.WriteLine($"[tabletop-probe] info live {sel.Name} moved {Vector2.Distance(new(sel.Shown.X, sel.Shown.Y), new(_ttProbeLiveFrom.X, _ttProbeLiveFrom.Y)):0.0} yd in 9 s; follow {_liveFollow}; card {QaCardOpen}");
                TabletopProbeShot($"tabletop-{area}-live");
                _ttProbeStage = 22; _ttProbeAt = now;
                return;
            }

            case 22:   // one more frame a few seconds later (the follow shot)
            {
                if (now - _ttProbeAt < 4.0) return;
                TabletopProbeShot($"tabletop-{_ttProbeZones[_ttProbeZone].Area}-live-later");
                _ttProbeStage = 23; _ttProbeAt = now;
                return;
            }

            case 23:   // after the shot has been written: back to the normal sequence
            {
                if (now - _ttProbeAt < 1.0) return;
                QaClearSelection();
                TabletopOverview();
                _ttProbeStage = 3; _ttProbeAt = now;
                return;
            }

            case 11:   // nothing local by hand: the creator catalogue downloads itself from the web app on any machine
            {
                EnsureCreatorCreatures();
                if (_creatorCreaturesFetch is not null && now - _ttProbeAt < 60) return;
                string path = CreatorCreatureTable.PathIn(_config.RepoRoot);
                TabletopProbeCheck("creator catalogue downloaded from the web app and kept as the offline copy",
                    _creatorCreatures is { Count: > 0 } && _creatorCreaturesStatus.Length == 0 && File.Exists(path),
                    $"{_creatorCreatures?.Count ?? 0} creatures; {(_creatorCreaturesStatus.Length > 0 ? _creatorCreaturesStatus : "fresh")}; file {(File.Exists(path) ? "written" : "missing")}");
                TabletopProbeFinish();
                return;
            }
        }
    }

    // §7/§10.5 frame times: the hitch recorder's completed frames over a fixed window per framing.
    private const double TabletopPerfSeconds = 5.0;
    private string? _ttPerfLabel;
    private double _ttPerfStarted;
    private long _ttPerfLastIndex = -1;
    private readonly List<(double Frame, double Render)> _ttPerf = new();

    private void TabletopProbePerfStart(string label, double now)
    {
        // Starts after the shot just armed has been written (a PNG encode is a ~0.5 s frame of the probe's own).
        _ttPerfLabel = label; _ttPerfStarted = now + 1.5; _ttPerfLastIndex = _hitch.LastCompleted.Index; _ttPerf.Clear();
    }

    /// <summary>While a window is open: collect each completed frame; true holds the probe's stages until it closes.</summary>
    private bool TabletopProbePerfSampling(double now)
    {
        if (_ttPerfLabel is null) return false;
        var f = _hitch.LastCompleted;
        if (now < _ttPerfStarted) { _ttPerfLastIndex = f.Index; return true; }
        if (f.Index != _ttPerfLastIndex) { _ttPerfLastIndex = f.Index; _ttPerf.Add((f.FrameMs, f.RenderMs)); }
        if (now - _ttPerfStarted < TabletopPerfSeconds) return true;
        if (_ttPerf.Count > 0)
        {
            var ms = _ttPerf.Select(p => p.Frame).OrderBy(x => x).ToList();
            double P(double q) => ms[Math.Min(ms.Count - 1, (int)(q * ms.Count))];
            double avg = ms.Average();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tabletop-probe] perf {_ttPerfLabel}: {ms.Count} frames, {1000.0 / avg:0} fps avg; frame ms p50 {P(0.5):0.0} p95 {P(0.95):0.0} max {ms[^1]:0.0}; render ms avg {_ttPerf.Average(p => p.Render):0.0}; over 25 ms {ms.Count(x => x > 25.0)}; {ImGuiNET.ImGui.GetIO().DisplaySize.X:0}x{ImGuiNET.ImGui.GetIO().DisplaySize.Y:0}"));
        }
        _ttPerfLabel = null;
        return false;
    }

    private Vector3 _ttProbeLiveFrom;
    private bool _ttProbeWorldShot;
    private float? _ttProbeSavedK;
    private double _ttProbeNearWaitSince;

    /// <summary>Wait (up to 90 s) for the near layer to finish loading around the current target and for the census.</summary>
    private bool TabletopProbeNearSettled(double now)
    {
        if (!Settings.Creator.Tabletop.World || _ttNear is null) return true;
        if (_ttProbeNearWaitSince == 0) _ttProbeNearWaitSince = now;
        bool settled = _ttNear.Ready && _ttCensus is null && _qaBundleTask is null && (_qaBundle is null || _qaBoard is not null);
        if (!settled && now - _ttProbeNearWaitSince < 90) return false;
        _ttProbeNearWaitSince = 0;
        if (!settled) TabletopProbeCheck("near layer settled in time", false, _ttNear.Status);
        return true;
    }

    private void TabletopProbeNearChecks(uint area, string framing)
    {
        if (!Settings.Creator.Tabletop.World) return;
        TabletopProbeCheck($"zone {area} {framing}: near layer ready", _ttNear is { Ready: true },
            _ttNear?.Failure ?? _ttNear?.Status ?? "no near layer");
        TabletopProbeCheck($"zone {area} {framing}: real buildings and props on the table",
            _ttNear is { Buildings: > 0, Props: > 0 }, $"{_ttNear?.Buildings} buildings, {_ttNear?.Props} props");
        Console.WriteLine($"[tabletop-probe] info zone {area} {framing}: {_ttCensusStatus}; creatures drawn last frame {_ttNear?.CreaturesDrawn}");
    }

    private void TabletopProbeFinish()
    {
        Console.WriteLine($"[tabletop-probe] VERDICT {(_ttProbeFailures == 0 ? "PASS" : $"FAIL ({_ttProbeFailures})")}");
        Console.Out.Flush();
        _quitRequested = true;
        _ttProbeStage = 99;
    }
}
