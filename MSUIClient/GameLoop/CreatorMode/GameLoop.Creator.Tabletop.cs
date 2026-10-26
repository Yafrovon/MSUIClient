using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Tabletop;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop (shared_docs/QUEST_TABLETOP.md): Creator Mode's raised-relief quest view, the client port of the
// web Quest Atlas. A swap: while the Tabletop panel is open the world is not drawn; the table is, with its own
// camera (FOV 30, orbit around a ground target, the opening "film pan"). P1 = the table: far-layer relief from the
// client's own ADTs + minimap tiles (patch-7 included), slab, skirt, table, water, lights, miniature lens, relief
// slider.
//
// Gameplay isolation (§4): the view never moves the controlled body (Program.cs tabletopLocked skips the
// controller update and zeroes every movement axis), never sends anything (Creator Mode has no session), keeps the
// streaming centre on the body, and owns the mouse so the world camera cannot turn. Opening snapshots the game
// camera, the body and the streaming centre; closing restores the camera and reports whether all three held.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>The table is up: the Creator panel offline, or the live table opened from the Command View
    /// (GameLoop.Creator.Tabletop.LiveWorld.cs).</summary>
    private bool TabletopActive => (_creatorWorldRequested || _liveTableOpen) && _creatorPanel == CreatorPanel.Tabletop;

    private TabletopRenderer? _tabletop;
    private readonly Camera _ttCamera = new()
    {
        FieldOfViewDegrees = TabletopLaw.FovDegrees, NearPlane = 5f, FarPlane = 80000f,
    };
    private bool _ttOpen;

    // Orbit state (world yards; azimuth/polar in the web sketch's spherical terms, see TabletopLaw.OrbitPosition).
    // _ttTarget.Z is a TRUE height: the camera exaggerates it each frame like everything else, so moving the relief
    // slider never leaves the camera inside a hill it was framing.
    private Vector3 _ttTarget;
    private float _ttRadius = 3000f, _ttPolar = 0.82f, _ttAzimuth;
    private float _ttDeltaAzimuth, _ttDeltaPolar;
    private Vector3 _ttPanOffset;

    private sealed record TabletopFlight(double T0, float Seconds, Vector3 FromTarget, Vector3 ToTarget,
        float FromRadius, float FromPolar, float FromAzimuth, float ToRadius, float ToPolar, float ToAzimuth);
    private TabletopFlight? _ttFlight;

    // Board loading.
    private uint _ttAreaId;
    private string _ttZoneName = "";
    private string _ttStatus = "";
    private Task<(TabletopLaw.Board Board, List<TabletopRenderer.TileInput> Tiles, int Agree, int Checked)>? _ttLoad;
    private bool _ttIntroPending;
    private double _ttBoardReadyAt;

    // Input.
    private int _ttDragButton = -1;
    private Vector2 _ttPressAt;
    private float _ttPressTravel;
    private Vector3? _ttHover;

    // What the view must leave exactly as it found it.
    private sealed record TabletopSnapshot(Vector3 CamTarget, float Yaw, float OrbitYaw, float Pitch, float Distance,
        float EffectiveDistance, float Fov, float Near, float Far, Vector3? AuthoredPosition, Vector3? AuthoredTarget,
        Vector3 Body, float BodyYaw, bool BodyFlying, (int, int)? Resident, int Map, long Packets);
    private TabletopSnapshot? _ttSnapshot;
    /// <summary>The last close's verdict (probe and console): camera, body and streaming centre unchanged.</summary>
    private string _ttLastCloseReport = "";
    private bool _ttLastCloseClean;

    // ── open / close ────────────────────────────────────────────────────────

    /// <summary>Per-frame: open on entering the panel, close on leaving it (panel switch, close button, Creator exit).</summary>
    private void UpdateTabletopLifecycle()
    {
        if (TabletopActive && !_ttOpen) OpenTabletop();
        else if (!TabletopActive && _ttOpen) CloseTabletop();
        else if (_liveTableOpen && !TabletopActive) LiveTableEnd();   // closed before it ever opened
        LiveTableUpdate();
    }

    private void OpenTabletop()
    {
        _ttOpen = true;
        var cam = _window.Camera;
        _ttSnapshot = new TabletopSnapshot(cam.Target, cam.Yaw, cam.OrbitYaw, cam.Pitch, cam.Distance, cam.EffectiveDistance,
            cam.FieldOfViewDegrees, cam.NearPlane, cam.FarPlane, cam.AuthoredPosition, cam.AuthoredTarget,
            _controller?.Position ?? Vector3.Zero, _controller?.Yaw ?? 0f, _controller?.Flying ?? false,
            _residentCentre, _config.Start.Map, _movementSender.PacketsSent);
        Console.WriteLine($"[tabletop] open: body ({_ttSnapshot.Body.X:0.00}, {_ttSnapshot.Body.Y:0.00}, {_ttSnapshot.Body.Z:0.00}) " +
                          $"stream {_residentCentre} map {_config.Start.Map}");
        if (_liveTableOpen) LiveTableBegin();
        var s = Settings.Creator.Tabletop;
        if (_tabletop?.Board is null || _ttAreaId != s.AreaId) TabletopLoadZone(s.AreaId);
        else TabletopIntro();
    }

    private void CloseTabletop()
    {
        _ttOpen = false;
        _ttFlight = null;
        _ttDragButton = -1;
        if (_liveTableOpen)
        {
            // Live: the rig was the table's eye on purpose and the stream followed it; the Command View carries on
            // from where the table looked. The verdict that matters live: no movement packet left the session.
            bool quietLive = _ttSnapshot is null || _movementSender.PacketsSent == _ttSnapshot.Packets;
            _ttLastCloseClean = quietLive;
            _ttLastCloseReport = $"live: movement packets {(quietLive ? "none sent" : $"SENT {_movementSender.PacketsSent - _ttSnapshot!.Packets}")}, eye moves {_liveTableEyeMoves}";
            Console.WriteLine($"[tabletop] close: {_ttLastCloseReport}");
            _ttSnapshot = null;
            LiveTableEnd();
            return;
        }
        if (_ttSnapshot is not { } snap) return;
        var cam = _window.Camera;
        bool camSame = cam.Target == snap.CamTarget && cam.Yaw == snap.Yaw && cam.OrbitYaw == snap.OrbitYaw && cam.Pitch == snap.Pitch &&
                       cam.Distance == snap.Distance && cam.FieldOfViewDegrees == snap.Fov && cam.AuthoredPosition == snap.AuthoredPosition &&
                       cam.AuthoredTarget == snap.AuthoredTarget;
        // The view never writes the game camera; restoring is belt and braces so "exactly as before" holds even if a
        // binding (zoom keys) reached it while the table was up.
        cam.Target = snap.CamTarget; cam.Yaw = snap.Yaw; cam.OrbitYaw = snap.OrbitYaw; cam.Pitch = snap.Pitch;
        cam.Distance = snap.Distance; cam.EffectiveDistance = snap.EffectiveDistance; cam.FieldOfViewDegrees = snap.Fov;
        cam.NearPlane = snap.Near; cam.FarPlane = snap.Far; cam.AuthoredPosition = snap.AuthoredPosition; cam.AuthoredTarget = snap.AuthoredTarget;
        var body = _controller?.Position ?? Vector3.Zero;
        bool bodySame = body == snap.Body && (_controller?.Yaw ?? 0f) == snap.BodyYaw && (_controller?.Flying ?? false) == snap.BodyFlying;
        bool streamSame = _residentCentre == snap.Resident && _config.Start.Map == snap.Map;
        bool quiet = _movementSender.PacketsSent == snap.Packets && _net is null;
        _ttLastCloseClean = camSame && bodySame && streamSame && quiet;
        _ttLastCloseReport = $"game camera {(camSame ? "unchanged" : "CHANGED (restored)")}, body {(bodySame ? "unchanged" : $"MOVED to ({body.X:0.00}, {body.Y:0.00}, {body.Z:0.00})")}, " +
                             $"stream centre {(streamSame ? "unchanged" : $"CHANGED to {_residentCentre}")}, " +
                             $"packets {(quiet ? "none sent (no session)" : $"SENT {_movementSender.PacketsSent - snap.Packets}")}";
        Console.WriteLine($"[tabletop] close: {_ttLastCloseReport}");
        _ttSnapshot = null;
    }

    // ── board ───────────────────────────────────────────────────────────────

    /// <summary>The board for a zone: its WorldMapArea rectangle (patch-7 rows included) on its map.</summary>
    private bool TryTabletopBoard(uint areaId, out TabletopLaw.Board board, out string name)
    {
        board = default;
        name = "";
        EnsureWorldMapAreas();
        EnsureInstanceData();
        EnsureAreaTableForMinimap();
        if (_worldMapAreas is null || !_worldMapAreas.TryGetArea(areaId, out var area)) return false;
        string? dir = _maps?.Get((int)area.MapId)?.Directory;
        if (string.IsNullOrEmpty(dir)) return false;
        name = _areas?.ZoneName(areaId) is { Length: > 0 } z ? z : area.Directory;
        // WorldMapArea: Left/Right = world Y max/min, Top/Bottom = world X max/min.
        board = new TabletopLaw.Board((int)area.MapId, dir,
            MathF.Max(area.Top, area.Bottom), MathF.Min(area.Top, area.Bottom),
            MathF.Max(area.Left, area.Right), MathF.Min(area.Left, area.Right));
        return true;
    }

    private void TabletopLoadZone(uint areaId)
    {
        if (_mpq is null) { _ttStatus = "No game data mounted."; return; }
        QaBeginZone(areaId);
        var bundleTask = _qaBundleTask;
        bool mapRect = TryTabletopBoard(areaId, out var board, out string name);
        // Dungeons and raids have no world-map rectangle: their table is the region around the entrance outside
        // (owner, 2026-10-21; the spec's givers-and-enders framing spread Blackrock Depths over half a continent).
        if (!mapRect && TryTabletopEntranceBoard(areaId, out board, out name)) mapRect = true;
        HashSet<uint>? members = null;
        string? scanDir = null;
        if (!mapRect)
        {
            // No WorldMapArea rectangle (pack zones such as Gilneas): the board is the bounding box of the terrain chunks
            // tagged with the zone or its subzones, found off-thread from the client's own ADTs (owner, 2026-10-14).
            EnsureAreaTableForMinimap();
            EnsureInstanceData();
            uint? mapId = _areas?.MapOf(areaId);
            scanDir = mapId is { } m ? _maps?.Get((int)m)?.Directory : null;
            if (_areas is null || mapId is null || string.IsNullOrEmpty(scanDir))
            {
                _ttStatus = $"Zone {areaId} is not in this client's AreaTable.";
                Console.WriteLine($"[tabletop] zone {areaId}: unknown area");
                return;
            }
            members = _areas.ZoneMembers(areaId);
            name = _areas.ZoneName(areaId) is { Length: > 0 } z ? z : $"zone {areaId}";
            board = new TabletopLaw.Board((int)mapId.Value, scanDir, 0, 0, 0, 0);
        }
        _ttAreaId = areaId;
        _ttZoneName = name;
        Settings.Creator.Tabletop.AreaId = areaId;
        SettingsFile?.Save();
        EnsureMinimapTileMap();
        var minimap = _minimapTileMap is null ? new Dictionary<string, string>() : new Dictionary<string, string>(_minimapTileMap, StringComparer.OrdinalIgnoreCase);
        var mpq = _mpq;
        _ttStatus = mapRect ? $"Building {name}..." : $"Finding {name} on the map...";
        if (mapRect)
            Console.WriteLine($"[tabletop] loading {name} (area {areaId}) map {board.MapId} {board.MapDirectory}: " +
                              $"X {board.Bottom:0}..{board.Top:0}, Y {board.Right:0}..{board.Left:0}, span {board.Span:0} yd");
        _ttLoad = Task.Run(() =>
        {
            // No map row (dungeons were handled before: their entrance): 1. the web bundle's own rectangle, 2. the terrain
            // chunks tagged with the zone (owner, 2026-10-14), 3. a frame on its givers and enders (spec §8) as the last resort.
            Q.ZoneBundle? bundle = null;
            if (members is not null && bundleTask is not null)
            {
                try { bundle = bundleTask.GetAwaiter().GetResult(); } catch { /* the chunk scan below still frames it */ }
                if (bundle?.Zone.Rect is not null && QaBundleBoard(bundle, scanDir!) is { } fromRect)
                {
                    board = fromRect;
                    members = null;
                    Console.WriteLine($"[tabletop] loading {name} (area {areaId}, board from the web bundle's rectangle) map {board.MapId} {board.MapDirectory}: " +
                                      $"X {board.Bottom:0}..{board.Top:0}, Y {board.Right:0}..{board.Left:0}, span {board.Span:0} yd");
                }
            }
            if (members is not null && TabletopBoardFromChunks(mpq, board.MapId, scanDir!, members) is { } found)
            {
                board = found;
                members = null;
                Console.WriteLine($"[tabletop] loading {name} (area {areaId}, board from ADT chunk areas) map {board.MapId} {board.MapDirectory}: " +
                                  $"X {board.Bottom:0}..{board.Top:0}, Y {board.Right:0}..{board.Left:0}, span {board.Span:0} yd");
            }
            if (members is not null && QaBundleBoard(bundle, scanDir!) is { } fromGivers)
            {
                board = fromGivers;
                members = null;
                Console.WriteLine($"[tabletop] loading {name} (area {areaId}, board framed on its givers and enders) map {board.MapId} {board.MapDirectory}: " +
                                  $"X {board.Bottom:0}..{board.Top:0}, Y {board.Right:0}..{board.Left:0}, span {board.Span:0} yd");
            }
            if (members is not null)
                throw new InvalidOperationException($"{name} has no map rectangle, no terrain tagged with it and no quest givers on {scanDir}");
            var tiles = new List<TabletopRenderer.TileInput>();
            int agree = 0, checkedOrigins = 0;
            foreach (var (col, row) in board.Tiles())
            {
                ReliefTile? relief = null;
                byte[]? adtBytes = mpq.ReadFile($@"World\Maps\{board.MapDirectory}\{board.MapDirectory}_{col}_{row}.adt");
                if (adtBytes is not null)
                {
                    try
                    {
                        var adt = AdtTerrainReader.Parse(adtBytes, row, col);
                        if (adt is not null) relief = ReliefCompiler.Compile(adt, col, row);
                    }
                    catch (Exception e) { Console.WriteLine($"[tabletop] ADT {col}_{row} failed: {e.Message}"); }
                }
                if (relief is not null) { agree += relief.OriginsAgree; checkedOrigins += relief.OriginsChecked; }
                byte[]? px = null;
                int w = 0, h = 0;
                if (relief is not null && minimap.TryGetValue($@"{board.MapDirectory}\map{col:00}_{row:00}.blp", out string? hashed) &&
                    mpq.ReadFile(@"textures\Minimap\" + hashed) is { } blp)
                {
                    try { px = BlpDecoder.GetPixels(blp, 0, out w, out h); }
                    catch (Exception e) { Console.WriteLine($"[tabletop] minimap {col}_{row} failed: {e.Message}"); }
                }
                tiles.Add(new TabletopRenderer.TileInput(col, row, relief, px, w, h));
            }
            return (board, tiles, agree, checkedOrigins);
        });
    }

    private static readonly Dictionary<(int Map, uint Area), TabletopLaw.Board?> TabletopChunkBoards = new();

    /// <summary>
    /// The bounding box of every MCNK on <paramref name="dir"/> whose AreaId is one of <paramref name="members"/> (a zone
    /// and its subzones), reading only chunk headers of the tiles the WDT lists. Cached per process.
    /// </summary>
    private static TabletopLaw.Board? TabletopBoardFromChunks(MpqMount mpq, int mapId, string dir, HashSet<uint> members)
    {
        uint key = members.Min();
        lock (TabletopChunkBoards)
            if (TabletopChunkBoards.TryGetValue((mapId, key), out var cached)) return cached;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var wdt = mpq.ReadFile($@"World\Maps\{dir}\{dir}.wdt") is { } wb ? WdtFile.Parse(wb) : null;
        const float Chunk = TabletopLaw.TileYards / 16f;
        float top = float.MinValue, bottom = float.MaxValue, left = float.MinValue, right = float.MaxValue;
        int hits = 0, scanned = 0;
        foreach (var (col, row) in wdt?.Tiles ?? (IReadOnlyList<(int, int)>)Array.Empty<(int, int)>())
        {
            if (mpq.ReadFile($@"World\Maps\{dir}\{dir}_{col}_{row}.adt") is not { } bytes) continue;
            scanned++;
            foreach (var (ix, iy, area) in AdtChunkAreas.Read(bytes))
            {
                if (!members.Contains(area)) continue;
                float north = (32 - row) * TabletopLaw.TileYards - iy * Chunk, west = (32 - col) * TabletopLaw.TileYards - ix * Chunk;
                top = MathF.Max(top, north); bottom = MathF.Min(bottom, north - Chunk);
                left = MathF.Max(left, west); right = MathF.Min(right, west - Chunk);
                hits++;
            }
        }
        TabletopLaw.Board? board = hits == 0 ? null : new TabletopLaw.Board(mapId, dir, top, bottom, left, right);
        Console.WriteLine($"[tabletop] chunk-area scan {dir}: {scanned} tiles, {hits} chunks in the zone, {started.ElapsedMilliseconds} ms");
        lock (TabletopChunkBoards) TabletopChunkBoards[(mapId, key)] = board;
        return board;
    }

    /// <summary>
    /// The board of a dungeon or raid: a square around its entrance in the outside world. The instance is the area's own
    /// map when that is not a continent, else the Map.dbc instance with the zone's name (Blackrock Depths is filed under map
    /// 0); an entrance that is itself inside another instance (Molten Core in Blackrock Depths) is followed outward.
    /// </summary>
    private bool TryTabletopEntranceBoard(uint areaId, out TabletopLaw.Board board, out string name)
    {
        board = default;
        name = "";
        EnsureAreaTableForMinimap();
        EnsureInstanceData();
        if (_areas is null || _maps is null) return false;
        name = _areas.ZoneName(areaId) is { Length: > 0 } z ? z : $"zone {areaId}";
        string zoneName = name;
        int? instance = _areas.MapOf(areaId) is { } own && own > 1 ? (int)own
            : _maps.All.FirstOrDefault(m => m.InstanceType != 0 && string.Equals(m.Name, zoneName, StringComparison.OrdinalIgnoreCase))?.Id;
        if (instance is not int map || _maps.Get(map) is not { InstanceType: not 0 }) return false;
        for (int hop = 0; hop < 4; hop++)
        {
            var doors = EntrancesTo(map);
            if (doors.Count == 0) return false;
            var volume = doors[0].Volume;
            if (_maps.Get(volume.MapId) is { InstanceType: not 0 }) { map = volume.MapId; continue; }   // entered from another instance
            if (_maps.Get(volume.MapId)?.Directory is not { Length: > 0 } dir) return false;
            const float h = TabletopLaw.EntranceHalfYards;
            board = new TabletopLaw.Board(volume.MapId, dir, volume.X + h, volume.X - h, volume.Y + h, volume.Y - h);
            Console.WriteLine($"[tabletop] {name} (area {areaId}): board around the entrance '{doors[0].Entrance.Name}' " +
                              $"({volume.X:0}, {volume.Y:0}) on {dir}" + (hop > 0 ? $" via {hop} instance(s)" : ""));
            return true;
        }
        return false;
    }

    /// <summary>Adopt a finished background build (GL thread).</summary>
    private void PumpTabletopLoad()
    {
        if (_ttLoad is not { IsCompleted: true } task) return;
        _ttLoad = null;
        if (task.IsFaulted) { _ttStatus = "Build failed: " + task.Exception?.GetBaseException().Message; return; }
        var (board, tiles, agree, checkedOrigins) = task.Result;
        _tabletop ??= new TabletopRenderer(_gl!);
        _tabletop.LoadBoard(board, tiles);
        int land = tiles.Count(t => t.Relief is not null);
        _ttStatus = $"{_ttZoneName}: {land} land tiles, {tiles.Count - land} sea, {_tabletop.MinimapCount} minimap";
        Console.WriteLine($"[tabletop] board {_ttZoneName}: {tiles.Count} tiles ({land} land, {_tabletop.MinimapCount} minimap), " +
                          $"baseZ {_tabletop.BaseZ:0.0}; MCNK origins agree {agree}/{checkedOrigins}");
        _ttOriginsAgree = agree; _ttOriginsChecked = checkedOrigins;
        _ttBoardReadyAt = NowSeconds();
        TabletopNearBoard(board);
        TabletopIntro();
    }

    private int _ttOriginsAgree, _ttOriginsChecked;

    // ── camera ──────────────────────────────────────────────────────────────

    /// <summary>The opening shot (§2): a low sweep across the table settling into the overview over 3.6 s.</summary>
    private void TabletopIntro()
    {
        if (_tabletop?.Board is not { } b) return;
        var start = TabletopLaw.IntroStart(b.Span);
        _ttTarget = new Vector3(b.Centre.X, b.Centre.Y, _tabletop.BaseZ);
        (_ttRadius, _ttPolar, _ttAzimuth) = start;
        _ttDeltaAzimuth = _ttDeltaPolar = 0f;
        _ttPanOffset = Vector3.Zero;
        var end = TabletopLaw.IntroEnd(b.Span);
        TabletopFlyTo(TabletopLaw.OverviewTarget(b, _tabletop.BaseZ), end.Radius, end.Polar, end.Azimuth, TabletopLaw.IntroSeconds);
    }

    private void TabletopOverview()
    {
        if (_tabletop?.Board is not { } b) return;
        TabletopFlyTo(TabletopLaw.OverviewTarget(b, _tabletop.BaseZ), b.Span * 1.15f, 0.82f, null, TabletopLaw.OverviewSeconds);
    }

    /// <summary>Frame a ground box (world X/Y extents) on the shown board.</summary>
    private void TabletopFrame(Vector2 min, Vector2 max, float seconds = TabletopLaw.FrameSeconds)
    {
        if (_tabletop is null) return;
        var c = (min + max) * 0.5f;
        var target = new Vector3(c.X, c.Y, _tabletop.HeightAt(c.X, c.Y));
        TabletopFlyTo(target, TabletopLaw.FrameRadius(max.X - min.X, max.Y - min.Y), 0.9f, null, seconds);
    }

    private void TabletopFlyTo(Vector3 target, float radius, float? polar, float? azimuth, float seconds)
    {
        float toAz = TabletopLaw.ShortestAzimuth(_ttAzimuth, azimuth ?? _ttAzimuth);
        _ttFlight = new TabletopFlight(NowSeconds(), seconds, _ttTarget, target, _ttRadius, _ttPolar, _ttAzimuth,
            radius, polar ?? _ttPolar, toAz);
    }

    private bool TabletopFlying => _ttFlight is not null;

    /// <summary>Advance the flight, the damped orbit and the clamps; place the tabletop camera.</summary>
    private void UpdateTabletopCamera(float dt)
    {
        if (_ttFlight is { } f)
        {
            float t = Math.Min(1f, (float)((NowSeconds() - f.T0) / f.Seconds));
            float e = TabletopLaw.EaseInOutCubic(t);
            _ttTarget = Vector3.Lerp(f.FromTarget, f.ToTarget, e);
            _ttRadius = f.FromRadius + (f.ToRadius - f.FromRadius) * e;
            _ttPolar = f.FromPolar + (f.ToPolar - f.FromPolar) * e;
            _ttAzimuth = f.FromAzimuth + (f.ToAzimuth - f.FromAzimuth) * e;
            if (t >= 1f) _ttFlight = null;
        }
        else
        {
            // OrbitControls damping (factor 0.08 per 60 Hz frame), made frame-rate independent.
            float damp = 1f - MathF.Pow(1f - 0.08f, Math.Clamp(dt, 0f, 0.25f) * 60f);
            _ttAzimuth += _ttDeltaAzimuth * damp; _ttDeltaAzimuth *= 1f - damp;
            _ttPolar += _ttDeltaPolar * damp; _ttDeltaPolar *= 1f - damp;
            _ttTarget += _ttPanOffset * damp; _ttPanOffset *= 1f - damp;
        }
        float span = _tabletop?.Board?.Span ?? 3000f;
        _ttPolar = Math.Clamp(_ttPolar, 0.02f, TabletopLaw.MaxPolar);
        if (!TabletopFlying) _ttRadius = Math.Clamp(_ttRadius, TabletopLaw.MinDistance, TabletopLaw.MaxDistance(span));
        var shown = _tabletop is null ? _ttTarget
            : _ttTarget with { Z = TabletopLaw.Exaggerate(_ttTarget.Z, _tabletop.BaseZ, _tabletop.Exaggeration) };
        _ttCamera.AuthoredTarget = shown;
        _ttCamera.AuthoredPosition = TabletopLaw.OrbitPosition(shown, _ttRadius, _ttPolar, _ttAzimuth);
        _ttCamera.AuthoredUp = Vector3.UnitZ;
        _ttCamera.FieldOfViewDegrees = TabletopLaw.FovDegrees;
        _ttCamera.FarPlane = MathF.Max(20000f, span * 8f);
        var fb = _window.FramebufferSize;
        if (fb.X > 0 && fb.Y > 0) _ttCamera.AspectRatio = fb.X / fb.Y;
    }

    // ── input ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Left-drag pans along the ground, right-drag orbits, wheel dollies (three.js OrbitControls numbers); any input
    /// cancels a flight; a press-release under 5 px is a click. Only when the pointer is not over a panel.
    /// </summary>
    private void TabletopInput()
    {
        var io = ImGui.GetIO();
        bool overUi = io.WantCaptureMouse;
        float h = MathF.Max(io.DisplaySize.Y, 1f);
        if (_ttDragButton < 0 && !overUi)
        {
            for (int b = 0; b < 2; b++)
                if (ImGui.IsMouseClicked((ImGuiMouseButton)b)) { _ttDragButton = b; _ttPressAt = io.MousePos; _ttPressTravel = 0f; _ttFlight = null; }
        }
        if (_ttDragButton >= 0)
        {
            var d = io.MouseDelta;
            _ttPressTravel += d.Length();
            if (_ttDragButton == 1)
            {
                _ttDeltaAzimuth -= MathF.Tau * d.X / h;
                _ttDeltaPolar -= MathF.Tau * d.Y / h;
            }
            else
            {
                float scale = 2f * _ttRadius * MathF.Tan(TabletopLaw.FovDegrees * MathF.PI / 360f) / h;
                var forward = _ttTarget - (_ttCamera.AuthoredPosition ?? _ttTarget);
                var flat = Vector3.Normalize(new Vector3(forward.X, forward.Y, 0f) + new Vector3(1e-6f, 0f, 0f));
                var right = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitZ));
                _ttPanOffset += -right * d.X * scale + flat * d.Y * scale;
            }
            if (!ImGui.IsMouseDown((ImGuiMouseButton)_ttDragButton))
            {
                if (_ttDragButton == 0 && _ttPressTravel < TabletopLaw.ClickTravelPixels) TabletopClick();
                _ttDragButton = -1;
            }
        }
        if (!overUi && io.MouseWheel != 0f)
        {
            _ttFlight = null;
            _ttRadius *= MathF.Pow(0.95f, io.MouseWheel);
        }

        // Ground under the cursor for the coordinates HUD.
        _ttHover = null;
        if (!overUi && _tabletop is not null && _ttCamera.ScreenPointToRay(io.MousePos, io.DisplaySize) is { } ray)
            _ttHover = _tabletop.Pick(ray.Origin, ray.Direction, _ttCamera.FarPlane);
    }

    private void TabletopClick()
    {
        if (QaPick(ImGui.GetIO().MousePos)) return;
        if (_ttHover is { } p) Console.WriteLine($"[tabletop] click ground ({p.X:0.0}, {p.Y:0.0}) z {_tabletop!.HeightAt(p.X, p.Y):0.0}");
    }

    /// <summary>Esc on the table: clear the selection and fly back to the overview (§2). Consumes the key.</summary>
    private bool ConsumeTabletopEscape()
    {
        if (!TabletopActive) return false;
        QaClearSelection();
        TabletopOverview();
        return true;
    }

    // ── UI + frame ──────────────────────────────────────────────────────────

    /// <summary>The tabletop's panel, input and HUD (Creator HUD pass). Owns the mouse for the rest of the frame.</summary>
    private void DrawTabletopUi()
    {
        PumpTabletopLoad();
        var io = ImGui.GetIO();
        TabletopInput();
        UpdateTabletopCamera(io.DeltaTime);
        TabletopNearUpdate();
        QaUpdate();
        LiveUpdate();
        QaDrawOverlays();
        LiveDrawOverlays();
        LiveTableDrawUnitBars();

        if (BeginCreatorPanel(_liveTableOpen ? "Live Tabletop" : "Quest Tabletop", "Tabletop", 380f, 620f))
        {
            BeginCreatorContent();
            ImGui.TextDisabled(_ttLoad is not null ? "Building the table..." : _ttStatus);
            QaDrawZonePicker();
            if (CreatorButton("Overview")) { QaClearSelection(); TabletopOverview(); }
            ImGui.SameLine();
            if (CreatorButton("Opening shot")) TabletopIntro();
            ImGui.Spacing();
            QaDrawFiltersAndList();
            if (_ttNear is not null) ImGui.TextDisabled(_ttNear.Failure is { } f ? "Near layer failed: " + f : _ttNear.Status);
            if (_ttCensusStatus.Length > 0) ImGui.TextDisabled(_ttCensusStatus);
            if (_liveStatus.Length > 0) ImGui.TextDisabled(_liveStatus);
            ImGui.TextDisabled("Left-drag pans, right-drag turns, the wheel zooms. Esc: overview.");
            EndCreatorContent();
        }
        EndCreatorPanel();
        QaDrawLayersPopover();
        QaDrawCard();
        QaDrawHover();

        if (_tabletop is not null)
        {
            _tabletop.Exaggeration = TabletopLaw.ShownExaggeration(Settings.Creator.Tabletop.Exaggeration, _ttRadius,
                Settings.Creator.Tabletop.ReliefEasesClose);
            _tabletop.Miniature = Settings.Creator.Tabletop.Miniature;
            _tabletop.ShowWater = Settings.Creator.Tabletop.Water;
        }

        // Coordinates HUD, bottom-left (§6): world x/y/z under the cursor.
        if (_ttHover is { } p && _tabletop is not null)
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"x {p.X:0.0}   y {p.Y:0.0}   z {_tabletop.HeightAt(p.X, p.Y):0.0}");
            var dl = ImGui.GetForegroundDrawList();
            var pos = new Vector2(_creatorSidebarRight + 14f, io.DisplaySize.Y - ImGui.GetTextLineHeight() - 14f);
            var size = ImGui.CalcTextSize(text);
            dl.AddRectFilled(pos - new Vector2(8, 4), pos + size + new Vector2(8, 4), AtlasU(new Vector4(20 / 255f, 16 / 255f, 12 / 255f, 0.7f)), 5f);
            dl.AddText(pos, AtlasU(AtlasInkDim), text);
        }

        // The table owns the mouse: the window must not start a look or a world click under it.
        io.WantCaptureMouse = true;
    }

    private string _ttZoneFilter = "";

    private void DrawTabletopZonePicker()
    {
        EnsureWorldMapAreas();
        EnsureAreaTableForMinimap();
        if (_worldMapAreas is null) return;
        string Label(WorldMapAreaInfo a) => _areas?.ZoneName(a.AreaId) is { Length: > 0 } z ? z : a.Directory;
        ImGui.SetNextItemWidth(CreatorControlWidth * 1.4f);
        if (ImGui.BeginCombo("Zone", _ttZoneName.Length > 0 ? _ttZoneName : "Choose a zone"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##tt-zone-filter", "search", ref _ttZoneFilter, 64);
            foreach (var (title, maps) in new[] { ("Eastern Kingdoms", new[] { 0u }), ("Kalimdor", new[] { 1u }), ("Elsewhere", Array.Empty<uint>()) })
            {
                var group = _worldMapAreas.Areas
                    .Where(a => maps.Length == 0 ? a.MapId > 1 : maps.Contains(a.MapId))
                    .Where(a => _ttZoneFilter.Length == 0 || Label(a).Contains(_ttZoneFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Label).ToList();
                if (group.Count == 0) continue;
                ImGui.TextDisabled(title);
                foreach (var a in group)
                    if (ImGui.Selectable($"{Label(a)}##tt-zone-{a.AreaId}", a.AreaId == _ttAreaId))
                        TabletopLoadZone(a.AreaId);
            }
            ImGui.EndCombo();
        }
    }

    /// <summary>Draw the table instead of the world. False until the renderer exists (the world draws meanwhile).</summary>
    private bool RenderTabletop()
    {
        if (_gl is null) return false;
        _tabletop ??= new TabletopRenderer(_gl);
        if (_liveTableOpen)
        {
            var live = _tabletop.Board is null ? null : LiveTableNearDraw();
            _tabletop.Render(_ttCamera, live, live is null ? null : _liveTableTiles);
            return true;
        }
        var near = _tabletop.Board is null ? null : TabletopNearDraw();
        _tabletop.Render(_ttCamera, near, near is null ? null : _ttNear?.ReadyTiles);
        return true;
    }
}
