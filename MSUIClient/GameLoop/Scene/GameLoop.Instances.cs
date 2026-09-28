using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World;

namespace MSUIClient;

// ============================================================================
// The instances instrument (PLAN_13_INSTANCES.md section 6, stage 1).
//
// Same rule as Program.DevTools.cs, Program.Hitch.cs, Program.LightProbe.cs and
// Program.Portals.cs: developer TOOLING. It reads Map.dbc and every map's WDT
// and prints what it found. Nothing here loads a map, moves the player or
// touches a renderer — stage 1 is deliberately read-only so it cannot regress
// the working outdoor client (PLAN_13 H5).
//
// WHAT THIS PANEL IS FOR
//   PLAN_13 section 1 makes a set of claims about the archives that were read
//   with tools/mpqpeek — a Python port that the handbook says loses to the C#
//   whenever they disagree. This panel is the C#. If it prints 44 maps and the
//   tile ranges in section 1's table, the readers agree with the tool and stage
//   2 can start. If it does not, the WDT parse is wrong and section 1 is the
//   reference.
// ============================================================================
public sealed partial class GameLoop
{
    private MapTable? _maps;
    private Dictionary<int, WdtFile?>? _mapWdts;
    private AreaTriggerTable? _areaTriggers;
    private bool _instancesLoadAttempted;
    private double _instancesLoadMs;
    private string _instanceFilter = "";
    private bool _instancesInstanceOnly;

    /// <summary>
    /// Read Map.dbc and every map's WDT, once. 44 WDTs is a few megabytes of
    /// MAIN tables and takes a moment, so it happens on first open of the panel
    /// rather than at startup — nothing in the client needs it yet.
    /// </summary>
    private void EnsureInstanceData()
    {
        if (_instancesLoadAttempted) return;
        _instancesLoadAttempted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, MapTable.MpqPath);
            if (bytes is null)
            {
                Console.WriteLine($"[instances] {MapTable.MpqPath} not found in the archives");
                return;
            }

            _maps = MapTable.Parse(bytes);
            if (_maps is null) return;

            var trig = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, AreaTriggerTable.MpqPath);
            _areaTriggers = trig is null ? null : AreaTriggerTable.Parse(trig);
            _teleports = AreaTriggerTeleportTable.Load(_config.RepoRoot);
            _teleports.MergePack(AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, AreaTriggerTeleportTable.PackMpqPath));

            _mapWdts = new Dictionary<int, WdtFile?>(_maps.Count);
            int withWdt = 0, globalWmo = 0, terrain = 0;
            foreach (var m in _maps.All)
            {
                var wdt = WdtFile.Read(_config.ClientDataPath, m.Directory);
                _mapWdts[m.Id] = wdt;
                if (wdt is null) continue;
                withWdt++;
                if (wdt.UsesGlobalWmo) globalWmo++; else terrain++;
            }

            Console.WriteLine($"[instances] {_maps.Count} map(s), {withWdt} with a WDT " +
                              $"({globalWmo} global-WMO, {terrain} terrain)");
        }
        finally
        {
            // In the finally so the readout is honest on the failure paths too -
            // "read in 0 ms" next to "Map.dbc did not load" reads like a second
            // bug when it is only an unstopped stopwatch.
            sw.Stop();
            _instancesLoadMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Forget Map.dbc/WDT/AreaTrigger.dbc so the next EnsureInstanceData re-reads them —
    /// the archive chain changed (World Builder hot-mounted a patch-7 that may add maps).</summary>
    private void ResetInstanceData()
    {
        _instancesLoadAttempted = false;
        _maps = null;
        _mapWdts = null;
        _areaTriggers = null;
    }

    private void DrawInstancesPanel()
    {
        if (!ImGui.CollapsingHeader("Instances (PLAN_13)")) return;

        EnsureInstanceData();

        if (_maps is null || _mapWdts is null)
        {
            ImGui.TextDisabled("Map.dbc did not load - see the console");
            return;
        }

        int globalWmo = 0, terrain = 0, noWdt = 0;
        foreach (var m in _maps.All)
        {
            var w = _mapWdts.GetValueOrDefault(m.Id);
            if (w is null) noWdt++;
            else if (w.UsesGlobalWmo) globalWmo++;
            else terrain++;
        }

        // PLAN_13 section 7 step 1: this line is the test. 44 maps, and the
        // two kinds split the way section 1's first table says.
        ImGui.Text($"{_maps.Count} map(s)   {globalWmo} global-WMO   {terrain} terrain" +
                   (noWdt > 0 ? $"   {noWdt} with NO WDT" : ""));
        ImGui.TextDisabled($"read in {_instancesLoadMs:F0} ms   current map: {_config.Start.MapName}");

        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("filter##inst", ref _instanceFilter, 64u);
        ImGui.SameLine();
        ImGui.Checkbox("instances only", ref _instancesInstanceOnly);
        ImGui.SameLine();
        if (ImGui.Button("Dump to console")) DumpInstanceTable();

        if (_travelReturn is { } back)
        {
            if (ImGui.Button($"Return to {back.DisplayName}##travelback")) TravelBack();
            ImGui.SameLine();
            ImGui.TextDisabled($"({back.Position.X:F0}, {back.Position.Y:F0}, {back.Position.Z:F0})");
        }

        ImGui.TextDisabled("Area triggers: server-authoritative");
        if (_teleports is null || _teleports.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                $"no {AreaTriggerTeleportTable.FileName} - destination labels unavailable");
        }
        else if (_lastPortalMessage.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"last: {_lastPortalMessage}");
        }

        if (_travelStatus.Length > 0) ImGui.TextUnformatted(_travelStatus);

        ImGui.Separator();

        foreach (var m in _maps.All)
        {
            if (_instancesInstanceOnly && !m.IsInstance) continue;
            if (_instanceFilter.Length > 0 &&
                m.Directory.IndexOf(_instanceFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                m.Name.IndexOf(_instanceFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var wdt = _mapWdts.GetValueOrDefault(m.Id);
            bool here = string.Equals(m.Directory, _config.Start.MapName,
                                      StringComparison.OrdinalIgnoreCase);

            string kind = wdt is null ? "no WDT" : wdt.UsesGlobalWmo ? "global WMO" : "terrain";
            string head = $"{m.Id,4}  {m.Name}   [{kind}]{(here ? "   <- you are here" : "")}";

            if (!ImGui.TreeNodeEx($"{head}##map{m.Id}", ImGuiTreeNodeFlags.SpanAvailWidth)) continue;

            ImGui.TextDisabled($"dir {m.Directory}   {m.InstanceType}");

            if (wdt is null)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                    $@"World\Maps\{m.Directory}\{m.Directory}.wdt not in the archives");
                ImGui.TreePop();
                continue;
            }

            ImGui.TextDisabled($"MPHD flags 0x{wdt.Flags:X4}   MVER {wdt.Version}");

            // The doors that lead here, and the spot in the wider world you
            // would walk in from. This is the affordance that matters: it puts
            // you OUTSIDE the dungeon facing its entrance, and the portal does
            // the rest - which is what actually happens in the game.
            foreach (var (tel, volume, stand, facing) in EntrancesTo(m.Id))
            {
                if (ImGui.Button($"Go to entrance##ent{tel.Id}"))
                    GoToEntrance(volume, stand, facing, tel.Name);
                ImGui.SameLine();
                ImGui.TextDisabled($"{tel.Name}  (trigger {tel.Id} on map {volume.MapId})");
                if (tel.RequiredLevel > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"lvl {tel.RequiredLevel}");
                }
            }

            // Travel. H1: the button does not care whether this is a dungeon,
            // only whether the map has terrain to stand on - which is why
            // Azeroth and Deadmines are the same row with different numbers.
            bool canTravel = !wdt.UsesGlobalWmo && wdt.TileCount > 0 && !here;
            if (canTravel)
            {
                if (ImGui.Button($"Travel straight in##go{m.Id}")) TravelTo(m, wdt);
                ImGui.SameLine();
                ImGui.TextDisabled("skips the portal - arrival point, not the real entrance (H3)");
            }
            else if (here)
            {
                ImGui.TextDisabled("you are already on this map");
            }
            else if (wdt.UsesGlobalWmo)
            {
                ImGui.TextDisabled("global WMO - stage 3, not travellable yet");
            }
            else
            {
                ImGui.TextDisabled("no terrain tiles - nothing to stand on");
            }

            if (wdt.TileCount > 0)
            {
                var (cc, cr) = wdt.CentreTile;
                var (wx, wy) = TileOrigin(cc, cr);
                ImGui.Text($"{wdt.TileCount} tile(s)   col {wdt.MinCol}..{wdt.MaxCol}   " +
                           $"row {wdt.MinRow}..{wdt.MaxRow}");
                ImGui.Text($"centre tile [{cc},{cr}]   world approx ({wx:F0}, {wy:F0})");

                // The centre of the occupied block is not always an occupied
                // tile - `development` spreads 18 tiles across col 0..63 and its
                // centre [31,1] has no ADT. Stage 2's spawn uses SpawnTile for
                // exactly that reason, so the panel shows when the two differ.
                var (sc, sr) = wdt.SpawnTile;
                if (sc != cc || sr != cr)
                {
                    var (sx, sy) = TileOrigin(sc, sr);
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                        $"centre tile is EMPTY - spawn tile [{sc},{sr}] ({sx:F0}, {sy:F0})");
                }
            }
            else
            {
                ImGui.TextDisabled("0 tiles - no terrain on this map at all");
            }

            if (wdt.GlobalWmoPath is not null)
            {
                // TextUnformatted, not TextWrapped: ImGui.NET forwards the
                // string to native igText* as the FORMAT argument, so a '%' in
                // archive-derived data renders garbage. No 1.12 path contains
                // one, but the data decides that, not us.
                ImGui.TextUnformatted(wdt.GlobalWmoPath);
                if (wdt.GlobalWmo is { } g)
                {
                    ImGui.Text($"pos ({g.PosX:F1}, {g.PosY:F1}, {g.PosZ:F1})   " +
                               $"rot ({g.RotX:F1}, {g.RotY:F1}, {g.RotZ:F1})");
                    ImGui.Text($"lo ({g.BbMinX:F1}, {g.BbMinY:F1}, {g.BbMinZ:F1})   " +
                               $"hi ({g.BbMaxX:F1}, {g.BbMaxY:F1}, {g.BbMaxZ:F1})");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                        "MWMO names a WMO but there is no MODF placement");
                }
            }

            ImGui.TreePop();
        }
    }

    /// <summary>
    /// World position of a tile's origin corner, matching AdtTerrainReader:
    /// originX = (32 - row) * GRID_SIZE, originY = (32 - col) * GRID_SIZE.
    /// Row drives X and col drives Y - the transposition that makes WoW's grid
    /// confusing every single time.
    /// </summary>
    private static (float X, float Y) TileOrigin(int col, int row)
        => ((32 - row) * AdtTerrainReader.GRID_SIZE, (32 - col) * AdtTerrainReader.GRID_SIZE);

    /// <summary>
    /// The whole table in one console block, so it can be pasted next to
    /// PLAN_13 section 1 and diffed by eye. That comparison is the entire point
    /// of stage 1.
    /// </summary>
    private void DumpInstanceTable()
    {
        if (_maps is null || _mapWdts is null) return;

        Console.WriteLine("[instances] id  ty directory                  kind        tiles col     row     centre");
        foreach (var m in _maps.All)
        {
            var w = _mapWdts.GetValueOrDefault(m.Id);
            if (w is null)
            {
                Console.WriteLine($"[instances] {m.Id,4} {(int)m.InstanceType}  {m.Directory,-26} NO WDT");
                continue;
            }

            string kind = w.UsesGlobalWmo ? "globalWMO" : "terrain  ";
            string range = w.TileCount > 0
                ? $"{w.MinCol}..{w.MaxCol,-4} {w.MinRow}..{w.MaxRow,-4} [{w.CentreTile.Col},{w.CentreTile.Row}]"
                : "";
            Console.WriteLine($"[instances] {m.Id,4} {(int)m.InstanceType}  {m.Directory,-26} {kind} " +
                              $"{w.TileCount,5} {range} {w.GlobalWmoPath ?? ""}");
        }
    }
    // ========================================================================
    // STAGE 2 — travel (PLAN_13_INSTANCES.md H2, H3, H4).
    //
    // Swap the world's CONTENT. Do not re-run Load(): it builds the MPQ mount,
    // the worker pools, the GL renderers, the shaders and the character, almost
    // none of which is per-map, and re-running it would leak GL objects and
    // restart the workers.
    //
    // Five things are per-map, and PLAN_13 H2 named three of them as missing.
    // They are not any more:
    //
    //   AdtCache's map directory   -> AdtCache.SetMap        (new)
    //   the resident terrain tiles -> TerrainRenderer.UnloadAll (new)
    //   the built liquid meshes    -> LiquidRenderer.UnloadAll  (new)
    //   WMO and doodad placements  -> ResetForMapChange / ResetPlacements
    //   the collision world        -> LoadCollision, which already rebuilds it
    //
    // The refill is deliberately the SAME sequence UpdateWorldResidency runs on
    // a tile crossing, in the same order, because a second refill path is a
    // second thing to keep correct. H1: there is no `if (isDungeon)` anywhere
    // below - Deadmines and Azeroth differ only in how many tiles they have.
    // ========================================================================

    /// <summary>Where a travel came from, so the trip back lands where you left.</summary>
    private sealed class TravelReturn
    {
        public int MapId;
        public string MapName = "";
        public string DisplayName = "";
        public Vector3 Position;
        public float Facing;
    }

    private TravelReturn? _travelReturn;
    private bool _travelInProgress;
    private string _travelStatus = "";

    /// <summary>
    /// Decide where a map should drop you, and say honestly whether it can.
    ///
    /// H3: these are ARRIVAL POINTS, not entrances. There is no offline source
    /// for "where does this dungeon's door put you" - AreaTrigger.dbc carries
    /// the trigger volumes and the destinations live in the server's
    /// areatrigger_teleport table, which we do not have. When P2 lands the
    /// server sends a real position and this becomes a debug affordance, so do
    /// not hand-author twenty coordinates it will supersede.
    /// </summary>
    private bool TryPlanArrival(MapRow map, WdtFile wdt, out Vector2 where, out string why)
    {
        where = default;

        if (wdt.UsesGlobalWmo)
        {
            why = "global-WMO map - that is stage 3, and it needs WMO collision " +
                  "resident before the player is placed or you fall through the floor";
            return false;
        }

        if (wdt.TileCount == 0)
        {
            why = "terrain map with zero tiles - there is nothing to stand on";
            return false;
        }

        // SpawnTile, not CentreTile. `development` occupies 18 tiles scattered
        // across col 0..63 and the centre of that block is [31,1], which has no
        // ADT at all. Any map whose tiles are not a solid rectangle can do this.
        var (col, row) = wdt.SpawnTile;
        where = TerrainRenderer.TileCenter(col, row);
        why = "";

        // THE TILE CENTRE IS USUALLY NOT WHERE THE DUNGEON IS. Measured on the
        // first run: Deadmines' tile cluster centres on (-267, -267), while its
        // content - read off the collision BVH it actually built - spans
        // X -316..162 Y -1050..-342. The arrival missed the dungeon by 469 yards
        // and every one of its 698 doodads was distance-culled. A dungeon does
        // not sit in the middle of its tile allocation and there is no reason it
        // should.
        //
        // Every AreaTrigger on an instance map sits INSIDE that instance's
        // playable space, because that is what a trigger is for. So the nearest
        // one to the tile centre is a far better guess than the tile centre, and
        // it costs one table lookup. Still an arrival point and not an entrance
        // (H3) - the client cannot know which trigger is the way in, only that
        // they are all somewhere you can stand.
        var anchor = _areaTriggers?.NearestOnMapXY(map.Id, where);
        if (anchor is not null)
        {
            where = new Vector2(anchor.X, anchor.Y);
            Console.WriteLine($"[travel] {map.Name}: arriving at AreaTrigger {anchor.Id} " +
                              $"({anchor.X:F0}, {anchor.Y:F0}, {anchor.Z:F0}) rather than tile centre " +
                              $"[{col},{row}]");
        }

        return true;
    }

    /// <summary>
    /// Travel to a map. Returns false and changes NOTHING if it refuses.
    ///
    /// PLAN_13 §9: a refused transition is a bad afternoon, a half-cleared world
    /// is a bug hunt. So every check that can be made BEFORE the teardown is
    /// made before it, and the teardown itself cannot fail.
    /// </summary>
    private bool TravelTo(MapRow map, WdtFile wdt, Vector3? exactPosition = null,
                          float? facing = null, bool recordReturn = true)
    {
        if (_travelInProgress) return false;

        if (_terrain is null || _adts is null || _controller is null)
        {
            _travelStatus = "world is not loaded";
            Console.WriteLine("[travel] refused - the world is not loaded");
            return false;
        }

        Vector2 arrive;
        if (exactPosition is Vector3 exact)
        {
            // An exact destination skips TryPlanArrival, and with it both of its
            // refusals - which is how a portal into the Stockade (map 34) or the
            // Deeprun Tram (map 369) would tear the world down and land in the
            // restore path. Those are real teleport rows, so the guard has to be
            // repeated here rather than assumed unreachable.
            if (wdt.UsesGlobalWmo || wdt.TileCount == 0)
            {
                _travelStatus = $"refused: {map.Name} has no terrain tiles (stage 3)";
                Console.WriteLine($"[travel] refused {map.Name} - " +
                                  (wdt.UsesGlobalWmo ? "global-WMO map, that is stage 3"
                                                     : "zero terrain tiles"));
                return false;
            }
            arrive = new Vector2(exact.X, exact.Y);
        }
        else if (!TryPlanArrival(map, wdt, out arrive, out string why))
        {
            _travelStatus = $"refused: {why}";
            Console.WriteLine($"[travel] refused {map.Name} - {why}");
            return false;
        }

        // Remember where we were BEFORE anything is torn down. A return trip
        // that has to guess is not a return trip.
        var from = new TravelReturn
        {
            MapId = _config.Start.Map,
            MapName = _config.Start.MapName,
            DisplayName = _maps?.ByDirectory(_config.Start.MapName)?.Name ?? _config.Start.MapName,
            Position = _controller.Position,
            Facing = _controller.Yaw,
        };

        _travelInProgress = true;
        var timer = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"[travel] {from.DisplayName} -> {map.Name} ({map.Directory}) " +
                              $"arriving at ({arrive.X:F0}, {arrive.Y:F0})");

            // A map change produces a legitimately enormous frame. That is not
            // the bug the hitch recorder hunts (PLAN_07).
            _hitch.SuppressFor(5.0);

            // ---- teardown, in dependency order ----------------------------
            // Collision first: it holds references into WMO and doodad
            // placements, and it is what the controller is standing on.
            TearDownWorldContent();

            // The cache LAST of the content clears and BEFORE any reload, so
            // nothing can re-read a stale tile between the two.
            _adts.SetMap(map.Directory);
            _residentCentre = null;

            // The current map is the config's Start block. Mutating it in
            // memory is how every other consumer - exterior lighting, vantages,
            // scene dumps, hitch records, the vmap collision loader - follows
            // along without a second notion of "which map are we on".
            _config.Start.Map = map.Id;
            _config.Start.MapName = map.Directory;

            // ---- refill: the same sequence a tile crossing runs ------------
            var centre = TerrainRenderer.TileAt(arrive.X, arrive.Y);
            _terrain.SetResidency(centre.col, centre.row, _config.Start.TileRadius, _adts);

            if (_terrain.TileCount == 0)
            {
                // Nothing loaded. Do not leave the player in an empty grid and
                // do not recurse into TravelTo - unwind by hand, here, once.
                Console.WriteLine($"[travel] FAILED - {map.Directory} produced 0 terrain tiles " +
                                  $"around [{centre.col},{centre.row}]; returning to {from.DisplayName}");
                _travelStatus = $"FAILED: {map.Name} produced no terrain";
                RestoreAfterFailedTravel(from);
                return false;
            }

            _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);
            _liquid?.LoadForTiles(_terrain.LoadedTiles, _adts);

            var preloadRing = TerrainRenderer.TileRing(centre.col, centre.row, WmoPreloadRadius);
            _wmo?.QueuePreloadForTiles(preloadRing, _adts);

            PopulateDoodads(centre, reportDiagnostics: true);
            _adts.Retain(preloadRing);
            _residentCentre = centre;

            // H4 — COLLISION BEFORE THE PLAYER IS PLACED, not after. This is
            // the ordering the whole plan says is most likely to be got wrong,
            // and the symptom of getting it wrong is falling through the floor.
            // LoadCollision is used rather than BeginCollisionBuild because the
            // latter builds on a worker and returns immediately, which is
            // exactly the race this line exists to avoid.
            LoadCollision();

            // ---- arrival ---------------------------------------------------
            float? ground = _terrain.SampleHeight(arrive.X, arrive.Y);
            float z = exactPosition?.Z ?? ground ?? 0f;
            if (exactPosition is null && ground is null)
            {
                // No MCVT under the arrival point - a hole, or the tile centre
                // landed on a gap. Fall from above rather than spawn at Z=0,
                // which on most maps is underground.
                z = 500f;
                Console.WriteLine($"[travel] no terrain height at ({arrive.X:F0}, {arrive.Y:F0}) - " +
                                  "arriving from above and falling in");
            }

            _controller.Teleport(arrive.X, arrive.Y, z);
            _controller.Yaw = facing ?? _controller.Yaw;
            _window.Camera.Target = _controller.Position;
            _window.Camera.Yaw = _controller.Yaw;

            _foliage?.ForceRescatter();

            // RE-LATCH AGAINST THE DESTINATION'S TRIGGERS, on every arrival and
            // not just the portal path. Without this the Return button is
            // unusable for any dungeon entered through a portal: the return trip
            // puts you back INSIDE the entrance volume you walked into, the
            // stale latch still names a trigger on the map you just left, and
            // the next frame sends you straight back in. Same for "Travel
            // straight in", which deliberately arrives at a trigger centre.
            _portalLatch = _areaTriggers?.Containing(_config.Start.Map, _controller.Position)?.Id ?? 0;
            _portalLatchMap = _config.Start.Map;

            _travelStatus =
                $"{map.Name}: {_terrain.TileCount} tile(s), {_wmo?.InstanceCount ?? 0} WMO, " +
                $"{_doodads?.InstanceCount ?? 0} doodad(s) in {timer.Elapsed.TotalSeconds:F2}s";

            Console.WriteLine($"[travel] arrived on {map.Name} at " +
                              $"({arrive.X:F0}, {arrive.Y:F0}, {z:F1}) tile [{centre.col},{centre.row}] - " +
                              $"{_terrain.TileCount} terrain, {_wmo?.InstanceCount ?? 0} WMO, " +
                              $"{_doodads?.InstanceCount ?? 0} doodad placement(s), " +
                              $"{timer.Elapsed.TotalSeconds:F2}s");

            // LAST, not earlier. If anything above throws, RestoreAfterFailedTravel
            // puts us back on `from`'s map - and a return point naming the map we
            // are standing on offers a Return button that goes nowhere.
            _travelReturn = recordReturn ? from : null;
            return true;
        }
        catch (Exception ex)
        {
            // Logging alone would leave the world unrecoverably half-swapped:
            // the teardown has run, the config names the new map, and the player
            // is still standing at the OLD map's coordinates with no collision.
            // On the next frame UpdateWorldResidency would re-home the new map
            // around those coordinates and drop them through it. If this is the
            // first travel of the session there is no Return button either, so
            // the only other exit is a restart.
            Console.WriteLine($"[travel] FAILED - {ex.Message}");
            _travelStatus = $"FAILED: {ex.Message}";
            try { RestoreAfterFailedTravel(from); }
            catch (Exception restore)
            {
                Console.WriteLine($"[travel] restore ALSO failed - {restore.Message}. " +
                                  "The world is now inconsistent; restart the client.");
            }
            return false;
        }
        finally
        {
            _travelInProgress = false;
        }
    }

    /// <summary>
    /// Drop every piece of per-map content, including the two kinds nobody had
    /// noticed were per-map until this was reviewed.
    ///
    /// THE ASYNCHRONOUS COLLISION BUILD IS THE DANGEROUS ONE. BeginCollisionBuild
    /// snapshots placements onto a worker and stamps them with
    /// `_collisionGeneration`; AcceptReadyCollision installs the result if the
    /// stamp still matches. Nulling `_collision` does NOT stop a build that is
    /// already in flight, so a crossing that started a second before the travel
    /// would land the OLD map's BVH on the new map one frame after arrival - and
    /// you would be colliding with Elwynn inside the Deadmines, silently. Bumping
    /// the generation is what makes that result get dropped instead. It is H4's
    /// failure arriving by a door H4 did not look at.
    ///
    /// THE BACKGROUND DISCOVERY QUEUE is milder but the same shape: it holds the
    /// starting map's outer-ring tile coordinates and would feed them to the new
    /// map's preloader for the couple of seconds it takes to drain.
    /// </summary>
    private void TearDownWorldContent(bool preserveWorldBundle = false)
    {
        // Renderer promotion keeps the same authenticated SUI session. Drop
        // source apertures/correlations, but retain the negotiated portal-v1
        // capability so the destination can prepare its own portals immediately.
        ResetRealPortals(resetCapability: !preserveWorldBundle);
        _globalWmoPlacement = null;
        if (_controller is not null) _controller.TerrainAbsentByDesign = false;
        if (!preserveWorldBundle)
        {
            _collision = null;
            if (_controller is not null) _controller.Collision = null;
        }
        _collisionDebug?.Clear();

        // Orphan any in-flight collision build so its result is discarded.
        _collisionBuildTask = null;
        _collisionGeneration++;
        _doodadCollisionDirty = false;
        _doodadCollisionPending = 0;

        // Tile coordinates of a map we are leaving.
        _backgroundDiscovery.Clear();
        _backgroundAdtLoad = null;

        if (!preserveWorldBundle)
        {
            _wmo?.ResetForMapChange();
            _doodads?.ResetPlacements();
            _liquid?.UnloadAll();
            _terrain?.UnloadAll();
        }

        // Dynamic placement bookkeeping belongs to the renderer instance. A
        // promoted destination has a different DoodadRenderer, so even a
        // same-map teleport must let surviving WorldEntities publish their GOs
        // into that new registry on the next reconciliation pass.
        _gameObjectPlacements.Clear();
        _gameObjectAnimationStates.Clear();
        _gameObjectDespawnAnimations.Clear();
        _gameObjectRetainedDestroys.Clear();

        // Pools are keyed by placement position. Keeping them across a map
        // change would leave a dungeon's torches burning in mid-air over Elwynn
        // until something happened to walk past the same coordinates.
        _particles?.Clear();
    }

    /// <summary>
    /// Put the world back after a travel that tore down and then found nothing.
    /// Inline rather than a recursive TravelTo call, because the failure mode
    /// being handled is "the reload produced nothing" and recursing into the
    /// same code to handle it is how one bad map becomes an infinite loop.
    /// </summary>
    private void RestoreAfterFailedTravel(TravelReturn from)
    {
        if (_terrain is null || _adts is null || _controller is null) return;

        _config.Start.Map = from.MapId;
        _config.Start.MapName = from.MapName;

        TearDownWorldContent();
        _adts.SetMap(from.MapName);

        var centre = TerrainRenderer.TileAt(from.Position.X, from.Position.Y);
        _terrain.SetResidency(centre.col, centre.row, _config.Start.TileRadius, _adts);
        _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);
        _liquid?.LoadForTiles(_terrain.LoadedTiles, _adts);
        PopulateDoodads(centre, reportDiagnostics: false);
        _residentCentre = centre;
        LoadCollision();

        _controller.Teleport(from.Position.X, from.Position.Y, from.Position.Z);
        _controller.Yaw = from.Facing;
        _window.Camera.Target = _controller.Position;
        _window.Camera.Yaw = _controller.Yaw;
        _foliage?.ForceRescatter();

        _portalLatch = _areaTriggers?.Containing(_config.Start.Map, _controller.Position)?.Id ?? 0;
        _portalLatchMap = _config.Start.Map;

        Console.WriteLine($"[travel] restored {from.DisplayName} at " +
                          $"({from.Position.X:F0}, {from.Position.Y:F0}, {from.Position.Z:F0})");
    }

    /// <summary>The trip back, to the exact spot you left from.</summary>
    private void TravelBack()
    {
        var back = _travelReturn;
        if (back is null || _maps is null || _mapWdts is null) return;

        var row = _maps.ByDirectory(back.MapName);
        var wdt = row is null ? null : _mapWdts.GetValueOrDefault(row.Id);
        if (row is null || wdt is null)
        {
            Console.WriteLine($"[travel] cannot return - no Map.dbc row or WDT for '{back.MapName}'");
            return;
        }

        // No _travelReturn = null here. TravelTo clears it on the path that
        // succeeds, because recordReturn is false for the return trip itself.
        // Clearing it here as well would only ever fire when the trip was
        // REFUSED or failed and restored - taking away the button that is the
        // way back, while the player is still in the dungeon.
        TravelTo(row, wdt, back.Position, back.Facing, recordReturn: false);
    }

    // ========================================================================
    // PORTALS — walking through a doorway instead of being placed behind one.
    //
    // Two halves, from two sources, joined here:
    //   AreaTrigger.dbc          the VOLUME. 432 of them, both sides of every
    //                            portal, sphere or oriented box.
    //   areatrigger_teleport     where each one SENDS you. VMaNGOS's world DB;
    //                            nothing in the client carries it.
    //
    // A trigger with no teleport row is not a portal - most of the 432 are
    // quest and script triggers - so the join is what turns the table into a
    // set of doorways.
    // ========================================================================

    private AreaTriggerTeleportTable? _teleports;

    /// <summary>
    /// EntrancesTo is a nested scan over two tables and the panel would call it
    /// once per expanded row per frame. The tables never change after load.
    /// </summary>
    private readonly Dictionary<int, List<(AreaTriggerTeleport Entrance, AreaTriggerRow Volume, Vector3 Stand, float Facing)>>
        _entrancesByMap = [];

    /// <summary>
    /// The trigger we are currently standing in and must not re-fire. Set after
    /// every travel, because vanilla drops you close enough to the return portal
    /// that a naive test bounces you straight back: Deadmines' entrance lands
    /// you 8.2 yards from exit trigger 119, whose radius is 6. Outside it, but
    /// only just, and one step is enough to make an infinite loop.
    /// </summary>
    private int _portalLatch;
    private int _portalLatchMap = int.MinValue;

    /// <summary>
    /// A near teleport that lands INSIDE a trigger (a summon, a GM .go, an arrival inside another
    /// volume) must not report it in the same breath as MSG_MOVE_TELEPORT_ACK: the core handles the
    /// ack on the map thread and CMSG_AREATRIGGER on the session thread, so the trigger can be judged
    /// against the OLD position - "too far, ignore" (a debug-level log line) - and the latch then keeps
    /// the player standing in a dead portal. 2026-09-27: Greymane Wall portal 7011 did nothing live.
    /// </summary>
    private double _areaTriggerHoldUntil;
    private const double AreaTriggerTeleportHoldSeconds = 0.5;

    private void HoldAreaTriggersAfterTeleport() =>
        _areaTriggerHoldUntil = MSUIClient.Net.MovementInfo.ClientUptimeMs() / 1000.0 + AreaTriggerTeleportHoldSeconds;

    private string _lastPortalMessage = "";

    /// <summary>
    /// Report a portal if the player has walked into one. Called once per Update.
    ///
    /// Deliberately not gated on DevTools or the optional teleport-name table:
    /// every AreaTrigger.dbc volume is server-owned gameplay, including quest,
    /// inn, battleground, script and portal triggers.
    /// </summary>
    private void UpdateAreaTriggers()
    {
        if (_areaTriggers is null || !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)) return;
        if (_travelInProgress) return;
        if (MSUIClient.Net.MovementInfo.ClientUptimeMs() / 1000.0 < _areaTriggerHoldUntil) return;   // see HoldAreaTriggersAfterTeleport
        // Do not latch the volume while frozen/draining: after the authoritative plan completes,
        // remaining inside must still be able to produce the first live CMSG_AREATRIGGER.
        if (TacticalFreezeBlocksLiveCommands) return;

        // Area triggers are server-owned gameplay volumes for the body the session
        // acts as: the driven bot while possessing (the server runs the handler as
        // GetSuiActor since 2026-09-03 — the owner drove a bot through the Stormwind
        // mage-tower portal and "just walked right through"), else the logged-in
        // character. In Free View the controller is only the observer rig, and the
        // interaction body is the session body there, so an invisible camera never
        // trips inns, quests, battlegrounds, or portals on anyone's behalf.
        Vector3 bodyPosition = sessionBody.Position;

        int mapId = _config.Start.Map;
        if (_portalLatchMap != mapId)
        {
            _portalLatch = 0;
            _portalLatchMap = mapId;
        }
        bool stillInsideLatched = _portalLatch != 0 &&
            _areaTriggers.ForMap(mapId).FirstOrDefault(row => row.Id == _portalLatch)
                ?.Contains(bodyPosition) == true;
        AreaTriggerRow? first = stillInsideLatched
            ? null : _areaTriggers.Containing(mapId, bodyPosition);
        AreaTriggerLatchStep step = AreaTriggerLaw.Step(
            _portalLatch, stillInsideLatched, first?.Id);
        _portalLatch = step.LatchedId;
        if (step.ReportId is not int reportId) return;

        AreaTriggerTeleport? dest = _teleports?.Get(reportId);
        string label = dest?.Name is { Length: > 0 } name
            ? name : $"trigger {reportId}";
        Console.WriteLine($"[area-trigger] entered {reportId} '{label}' on map {mapId}");
        if (dest is not null)
        {
            _lastPortalMessage = dest.RequiredLevel > 0
                ? $"{dest.Name} (vanilla requires level {dest.RequiredLevel})"
                : dest.Name;
        }
        bool triggerSent = _net?.AreaTrigger((uint)reportId) == true;
        EmitInterface("portal", "area-trigger", triggerSent ? "SENT" : "SEND_FAILED", 0,
            FormattableString.Invariant($"trigger={reportId};map={mapId};name={label}"));
        if (triggerSent)
        {
            // Do not mutate map, position, collision, or entities here. VMaNGOS
            // validates the trigger against its authoritative player pose and
            // answers with TRANSFER_PENDING + NEW_WORLD. The old local TravelTo
            // path rendered the destination without moving the server, allowing
            // subsequent movement packets to save an outdoor position on the
            // dungeon map and poisoning the next login.
            if (dest is not null) _travelStatus = $"waiting for server: {dest.Name}";
        }
        else
        {
            if (dest is not null) _travelStatus = $"portal request failed: {dest.Name}";
            Console.WriteLine($"[area-trigger] could not send CMSG_AREATRIGGER {reportId}");
        }
    }

    /// <summary>
    /// Travel by map id, for the portal path where the destination is exact and
    /// comes from the server's own table rather than from a guess.
    /// </summary>
    private bool TravelToMapId(int mapId, Vector3 position, float orientation, string why)
    {
        if (_maps is null || _mapWdts is null)
        {
            Console.WriteLine($"[portal] '{why}' cannot fire - the map table is not loaded " +
                              "(open the Instances panel once)");
            return false;
        }

        var row = _maps.Get(mapId);
        var wdt = row is null ? null : _mapWdts.GetValueOrDefault(mapId);
        if (row is null || wdt is null)
        {
            Console.WriteLine($"[portal] '{why}' targets map {mapId}, which has no Map.dbc row or WDT");
            return false;
        }

        // TravelTo re-latches on arrival for us - see the note beside it.
        return TravelTo(row, wdt, position, orientation);
    }

    /// <summary>
    /// Every portal that leads TO this map, with the spot in the wider world
    /// where you would stand to use it.
    ///
    /// The standing spot is derived, not guessed: it is the destination of the
    /// PAIRED EXIT - the trigger inside the dungeon that leads back out. That
    /// is by construction a legal, walkable position just outside the entrance,
    /// authored by Blizzard, and it needs no geometry from us.
    ///
    /// Pairing is geometric, never by name. For an entrance E on map A leading
    /// to map B, the paired exit is the trigger on map B whose destination is
    /// nearest E's own volume. For Deadmines that picks exit 119 (9.4 yd from
    /// entrance 78) over the Back Exit 121 (173 yd), which is also what the
    /// names say - but the names are VMaNGOS's prose and this does not depend
    /// on them.
    /// </summary>
    private List<(AreaTriggerTeleport Entrance, AreaTriggerRow Volume, Vector3 Stand, float Facing)>
        EntrancesTo(int mapId)
    {
        if (_entrancesByMap.TryGetValue(mapId, out var cached)) return cached;

        var result = new List<(AreaTriggerTeleport Entrance, AreaTriggerRow Volume, Vector3 Stand, float Facing)>();
        if (_areaTriggers is null || _teleports is null) return result;

        foreach (var (id, tel) in _teleports.ById)
        {
            if (tel.TargetMap != mapId) continue;

            // The volume this teleport belongs to. Without it we do not know
            // where in the world the doorway is.
            AreaTriggerRow? volume = null;
            foreach (var t in _areaTriggers.All)
                if (t.Id == id) { volume = t; break; }
            if (volume is null) continue;

            // The way back out, chosen by where it lands relative to this door.
            AreaTriggerTeleport? bestExit = null;
            float bestSq = float.MaxValue;
            foreach (var (_, back) in _teleports.ById)
            {
                if (back.TargetMap != volume.MapId) continue;

                // Never pair a door with itself. When volume.MapId == mapId both
                // filters below are satisfied by `tel`, at distance zero, so it
                // would always win - and "Go to entrance" would stand you on the
                // FAR side of the door, facing backwards.
                if (back.Id == tel.Id) continue;
                var v = _areaTriggers.All.FirstOrDefault(t => t.Id == back.Id);
                if (v is null || v.MapId != mapId) continue;

                float dx = back.TargetPosition.X - volume.X;
                float dy = back.TargetPosition.Y - volume.Y;
                float d = dx * dx + dy * dy;
                if (d >= bestSq) continue;
                bestSq = d;
                bestExit = back;
            }

            Vector3 stand;
            float facing;
            if (bestExit is not null)
            {
                stand = bestExit.TargetPosition;
                // The exit faces AWAY from the door it just spat you out of, so
                // turn around and the doorway is straight ahead: walk forward
                // and you go in.
                facing = bestExit.TargetOrientation + MathF.PI;
            }
            else
            {
                stand = new Vector3(volume.X, volume.Y, volume.Z);
                facing = tel.TargetOrientation;
            }

            result.Add((tel, volume, stand, facing));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Entrance.Name, b.Entrance.Name));
        _entrancesByMap[mapId] = result;
        return result;
    }

    /// <summary>
    /// Put the player in the wider world, just outside a dungeon's door, facing
    /// it. This is the "walk in yourself" affordance, and it is what H3 should
    /// have said all along: the entrance is not something to derive, it is
    /// something the data already knows.
    /// </summary>
    private void GoToEntrance(AreaTriggerRow volume, Vector3 stand, float facing, string label)
    {
        if (_maps is null || _mapWdts is null) return;

        var row = _maps.Get(volume.MapId);
        var wdt = row is null ? null : _mapWdts.GetValueOrDefault(volume.MapId);
        if (row is null || wdt is null)
        {
            Console.WriteLine($"[portal] cannot reach map {volume.MapId} for '{label}'");
            return;
        }

        Console.WriteLine($"[portal] going to '{label}' - standing at " +
                          $"({stand.X:F0},{stand.Y:F0},{stand.Z:F0}) facing the door " +
                          $"(trigger {volume.Id} is {Vector2.Distance(new Vector2(stand.X, stand.Y), new Vector2(volume.X, volume.Y)):F0} yd ahead)");

        if (TravelToMapId(volume.MapId, stand, facing, label))
            _travelStatus = $"outside {label} - walk forward to enter";
    }

}
