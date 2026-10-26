using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Tabletop;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop - the LIVE table (shared_docs/QUEST_TABLETOP.md §12, owner 2026-10-22: "we LIVE the experience as a
// true tabletop view ... that view is non-negotiable"). Opened from the Command View's commander map in a logged-in
// session. The table keeps its whole look (relief board, skirt, walnut table, warm light, miniature lens, exaggerated
// relief); what changes is where the world near the camera comes from:
//
//  * The Command View rig follows the table's ground target, so the server's freecam eye (CMSG_SUI_CAM) streams the
//    real world around wherever the table looks: mobs, players, bots, their spells and deaths.
//  * The near layer is the GAME's own world renderers (terrain, buildings, props, game objects, water, units, spell
//    kits, missiles, chain beams, ribbons, particles, ground decals) drawn through the table camera with the same view
//    transform the offline near scene uses: terrain and water exaggerated in the shader, placements lifted by the
//    ground under them, units drawn at lifted positions (so every bone a spell attaches to is lifted too), fixed world
//    points (ground AoE, destinations) lifted through SpellEffectSource.FixedPointView, decals given the terrain relief.
//  * Beyond the server's visibility bubble the board stays populated by the brain feed's bots and the census figures.
//
// Isolation, restated for live: the table never moves the logged-in body and never sends movement; the ONLY packet it
// causes is the Command View's own eye heartbeat (same map only - the packet has no map). Every renderer setting the
// pass changes is restored when the table closes; the relief uniforms are reset with one k = 1 frame.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private bool _liveTableOpen;
    private readonly TabletopViewTransform _liveTableView = new();
    private readonly Dictionary<int, float> _liveTableWmoLift = new();
    private readonly HashSet<(int col, int row)> _liveTableTiles = new();
    private int _liveTableDetachFrames;
    private string _liveTableStatus = "";
    private Vector3 _liveTableRigSent;
    private int _liveTableEyeMoves;

    /// <summary>Server visibility around the eye (Visibility.Distance.Continents = 100 on the box) plus a margin: inside
    /// it the live world is the truth, so census figures stand down there.</summary>
    private const float LiveTableBubbleYards = 130f;
    /// <summary>The eye rides this far above the ground under the table's target.</summary>
    private const float LiveTableEyeHeight = 25f;

    private sealed record LiveTableSaved(
        bool LockOnPrimary,
        bool TerrainChunkCulling,
        bool WmoPortalCulling, bool WmoOcclusion, bool WmoFrustum, bool WmoSuppressLod,
        float DoodadDrawDistance,
        float CreatureAnimateDistance, float CreatureScale,
        bool LiquidUseAuthored, bool LiquidHasAuthored, Vector3 OceanClose, Vector3 OceanFar, Vector3 RiverClose,
        Vector3 RiverFar, float OceanShallow, float OceanDeep, float RiverShallow, float RiverDeep);
    private LiveTableSaved? _liveTableSaved;

    /// <summary>The lift of the ground under (x, y) while the live table is open; 0 otherwise (the game's own frames).</summary>
    private float LiveTableLiftAt(float x, float y)
    {
        if (!_liveTableOpen || _terrain is null) return 0f;
        return _terrain.SampleHeight(x, y) is { } ground ? _liveTableView.Lift(ground) : 0f;
    }

    /// <summary>A terrain-surface point as the table shows it (the shader's relief rule).</summary>
    private Vector3 LiveTableShown(Vector3 p)
        => p with { Z = p.Z + (_liveTableView.K - 1f) * (p.Z - _liveTableView.BaseZ) };

    // ── open / close ───────────────────────────────────────────────────────

    /// <summary>Open the table from a live Command View (the commander map's button, or the protocol runner).</summary>
    private bool LiveTableOpen()
    {
        if (_net is not { IsInWorld: true }) { _liveTableStatus = "Not in the world."; return false; }
        if (!_freeView) { _liveTableStatus = "The live table opens from the Command View."; return false; }
        _commanderMapOpen = false;
        _liveTableOpen = true;
        _creatorPanel = CreatorPanel.Tabletop;
        // The character's own zone first (the eye cannot leave this map), else the last board if it is on this map.
        uint zone = _minimapReportedZoneId;
        if (zone != 0 && TryTabletopBoard(zone, out var b, out _) && b.MapId == _config.Start.Map)
            Settings.Creator.Tabletop.AreaId = zone;
        Console.WriteLine($"[tabletop] live table opening on zone {Settings.Creator.Tabletop.AreaId} (map {_config.Start.Map})");
        return true;
    }

    /// <summary>OpenTabletop's live half: save everything the live pass will bend, start the rig at the camera.</summary>
    private void LiveTableBegin()
    {
        _liveTableSaved = new LiveTableSaved(
            Settings.Controls.CommandViewLockOnPrimary,
            _terrain?.ChunkCulling ?? true,
            _wmo?.UsePortalCulling ?? true, _wmo?.OcclusionCulling ?? false, _wmo?.FrustumCulling ?? true,
            _wmo?.SuppressDistanceLodShells ?? false,
            _doodads?.DrawDistance ?? 300f,
            _creatures?.AnimateDistance ?? 130f, _creatures?.ScaleMultiplier ?? 1f,
            _liquid?.UseAuthoredColors ?? false, _liquid?.HasAuthoredColors ?? false,
            _liquid?.OceanClose ?? default, _liquid?.OceanFar ?? default, _liquid?.RiverClose ?? default,
            _liquid?.RiverFar ?? default, _liquid?.OceanAlphaShallow ?? 1f, _liquid?.OceanAlphaDeep ?? 1f,
            _liquid?.RiverAlphaShallow ?? 1f, _liquid?.RiverAlphaDeep ?? 1f);
        // Lock-on-primary would drag the rig back to its unit every frame (and break itself past 40 yd).
        Settings.Controls.CommandViewLockOnPrimary = false;
        _liveTableDetachFrames = 0;
        _liveTableEyeMoves = 0;
        _liveTableRigSent = _controller?.Position ?? default;
        if (_spellEffects is not null) _spellEffects.FixedPointView = p => p with { Z = p.Z + LiveTableLiftAt(p.X, p.Y) };
        Console.WriteLine($"[tabletop] live table open: rig ({_liveTableRigSent.X:0}, {_liveTableRigSent.Y:0}, {_liveTableRigSent.Z:0}) map {_config.Start.Map}");
    }

    /// <summary>CloseTabletop's live half: put every renderer back; the rig stays where the table looked (the Command
    /// View carries on from there).</summary>
    private void LiveTableEnd()
    {
        _liveTableOpen = false;
        if (_spellEffects is not null) _spellEffects.FixedPointView = null;
        if (_liveTableSaved is { } s)
        {
            Settings.Controls.CommandViewLockOnPrimary = s.LockOnPrimary;
            if (_terrain is not null) _terrain.ChunkCulling = s.TerrainChunkCulling;
            if (_wmo is not null)
            {
                _wmo.UsePortalCulling = s.WmoPortalCulling; _wmo.OcclusionCulling = s.WmoOcclusion;
                _wmo.FrustumCulling = s.WmoFrustum; _wmo.SuppressDistanceLodShells = s.WmoSuppressLod;
                _wmo.ModelLift = null;
            }
            if (_doodads is not null) { _doodads.DrawDistance = s.DoodadDrawDistance; _doodads.ModelLift = null; }
            if (_creatures is not null) { _creatures.AnimateDistance = s.CreatureAnimateDistance; _creatures.ScaleMultiplier = s.CreatureScale; }
            if (_liquid is not null)
            {
                _liquid.UseAuthoredColors = s.LiquidUseAuthored; _liquid.HasAuthoredColors = s.LiquidHasAuthored;
                _liquid.OceanClose = s.OceanClose; _liquid.OceanFar = s.OceanFar; _liquid.RiverClose = s.RiverClose;
                _liquid.RiverFar = s.RiverFar; _liquid.OceanAlphaShallow = s.OceanShallow; _liquid.OceanAlphaDeep = s.OceanDeep;
                _liquid.RiverAlphaShallow = s.RiverShallow; _liquid.RiverAlphaDeep = s.RiverDeep;
            }
        }
        _liveTableSaved = null;
        // The shader programs keep the last relief uniforms: one world frame with k = 1 resets them, then detach.
        _liveTableView.K = 1f;
        _liveTableDetachFrames = 2;
        if (_controller is not null) _window.Camera.Target = _controller.Position;
        _freecamCamSentAt = 0;
        Console.WriteLine($"[tabletop] live table closed: the eye moved {_liveTableEyeMoves} time(s); rig at ({_controller?.Position.X:0}, {_controller?.Position.Y:0})");
    }

    /// <summary>Per update (before the Command View heartbeat): the rig - and with it the server eye - follows the
    /// table's ground target; after a close, the k = 1 reset frames and then the detach.</summary>
    private void LiveTableUpdate()
    {
        if (_liveTableDetachFrames > 0 && --_liveTableDetachFrames == 0)
        {
            if (_terrain is not null) _terrain.Tabletop = null;
            if (_wmo is not null) _wmo.Tabletop = null;
            if (_doodads is not null) _doodads.Tabletop = null;
            if (_liquid is not null) _liquid.Tabletop = null;
        }
        if (!_liveTableOpen || !_ttOpen) return;
        if (!_freeView || _net is not { IsInWorld: true })
        {
            // Left the Command View (possession, a forced release, a lost session): the live table goes with it.
            _creatorPanel = CreatorPanel.None;
            return;
        }
        // M (the commander map) from the table goes back to the map it was opened from.
        if (_commanderMapOpen) { _creatorPanel = CreatorPanel.None; return; }
        if (_controller is null || _tabletop?.Board is not { } board) return;
        if (board.MapId != _config.Start.Map)
        {
            _liveTableStatus = "This board is on another map: the live eye stays with your character's map.";
            return;
        }
        _liveTableStatus = "";
        float ground = _terrain?.SampleHeight(_ttTarget.X, _ttTarget.Y) ?? _ttTarget.Z;
        var rig = new Vector3(_ttTarget.X, _ttTarget.Y, ground + LiveTableEyeHeight);
        // Also when something else moved the rig (the Command View's wheel-fly reads the same wheel the table zooms with).
        if (Vector3.DistanceSquared(rig, _liveTableRigSent) < 2f * 2f &&
            Vector3.DistanceSquared(_controller.Position, rig) < 2f * 2f) return;
        _commanderFlySettle = null;
        _controller.Teleport(rig.X, rig.Y, rig.Z);
        _window.Camera.Target = rig;
        if (Vector3.Distance(rig, _liveTableRigSent) > 60f) _liveTableEyeMoves++;
        _liveTableRigSent = rig;
    }

    /// <summary>
    /// Protocol runner (GameLoop.LiveRun.cs) commands for the live table, so its proof is repeatable:
    /// <c>tabletop-live freeview</c> enters the Command View; <c>open [areaId]</c>; <c>frame x y [radius]</c>;
    /// <c>bot combat|moving|NAME</c> selects and follows a feed bot (in combat first); <c>status</c>; <c>shot NAME</c>;
    /// <c>close</c>. Every command logs what the table shows right now.
    /// </summary>
    private bool RunLiveTabletop(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = p.Length > 1 ? p[1].ToLowerInvariant() : "status";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (verb)
        {
            case "freeview":
                if (!_freeView) ToggleFreeView();
                return true;
            case "open":
                if (p.Length > 2 && uint.TryParse(p[2], out uint area)) Settings.Creator.Tabletop.AreaId = area;
                bool opened = LiveTableOpen();
                if (opened && p.Length > 2 && uint.TryParse(p[2], out uint forced)) Settings.Creator.Tabletop.AreaId = forced;
                Console.WriteLine($"[tabletop-live] open: {(opened ? "ok" : _liveTableStatus)}");
                return opened;
            case "frame":
            {
                if (p.Length < 4 || _tabletop is null) return false;
                float x = float.Parse(p[2], inv), y = float.Parse(p[3], inv);
                float r = p.Length > 4 ? float.Parse(p[4], inv) : 160f;
                TabletopFlyTo(new Vector3(x, y, _terrain?.SampleHeight(x, y) ?? _tabletop.HeightAt(x, y)), r, 0.95f, null, 1.2f);
                return true;
            }
            case "bot":
            {
                string want = p.Length > 2 ? p[2] : "combat";
                int[] casters = [5, 7, 8, 9, 11];
                var pick = want.Equals("caster", StringComparison.OrdinalIgnoreCase)
                    ? _live.Values.Where(b => b.InCombat && !b.Dead && b.Look is { } l && casters.Contains(l.Cls)).OrderBy(b => b.Guid).FirstOrDefault()
                      ?? _live.Values.Where(b => b.InCombat && !b.Dead).OrderBy(b => b.Guid).FirstOrDefault()
                    : want.Equals("combat", StringComparison.OrdinalIgnoreCase)
                    ? _live.Values.Where(b => b.InCombat && !b.Dead).OrderBy(b => b.Guid).FirstOrDefault()
                      ?? _live.Values.Where(b => b.Speed > 0).OrderBy(b => b.Guid).FirstOrDefault()
                    : want.Equals("moving", StringComparison.OrdinalIgnoreCase)
                        ? _live.Values.Where(b => b.Speed > 0).OrderBy(b => b.Guid).FirstOrDefault()
                        : _live.Values.FirstOrDefault(b => b.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
                pick ??= _live.Values.OrderBy(b => b.Guid).FirstOrDefault();
                if (pick is null) { Console.WriteLine("[tabletop-live] bot: no live bots on this board"); return false; }
                LiveSelect(pick);
                if (p.Length > 3 && float.TryParse(p[3], System.Globalization.NumberStyles.Float, inv, out float frameRadius))
                    TabletopFlyTo(pick.Shown, frameRadius, 0.95f, null, TabletopLaw.FrameSeconds);
                Console.WriteLine($"[tabletop-live] bot: {pick.Name} ({pick.Guid}) {pick.Goal}/{pick.Step} combat {pick.InCombat} at ({pick.Shown.X:0}, {pick.Shown.Y:0})");
                return true;
            }
            case "shot":
                TabletopProbeShot(p.Length > 2 ? p[2] : "tabletop-live");
                break;
            case "close":
                _creatorPanel = CreatorPanel.None;
                return true;
        }
        // status (also after shot)
        var rig = _controller?.Position ?? default;
        int liveUnits = _ttUnitsThisFrame.Count(u => _entities.TryGet(u.Guid, out _));
        Console.WriteLine(string.Create(inv,
            $"[tabletop-live] status: open {_liveTableOpen} active {TabletopActive} board {_ttZoneName} map {_tabletop?.Board?.MapId} (char map {_config.Start.Map}); " +
            $"rig ({rig.X:0}, {rig.Y:0}, {rig.Z:0}) eye moves {_liveTableEyeMoves}; world tiles {_liveTableTiles.Count} [{string.Join(' ', _liveTableTiles.OrderBy(t => t))}] far hidden {_tabletop?.FarTilesHidden} terrain drawn {_terrain?.DrawnLastFrame} tile under rig {TabletopLawTile(rig)}; " +
            $"units drawn {_creatures?.DrawnLastFrame} (live {liveUnits} of {_ttUnitsThisFrame.Count}); entities {_entities.Units.Count()}; " +
            $"spell instances {_spellEffects?.ActiveCount} meshes drawn {_spellEffectMeshes?.DrawnLastFrame} particles {_spellParticles?.LiveParticles}; " +
            $"feed {_liveStatus}; {(_liveTableStatus.Length > 0 ? _liveTableStatus : "eye ok")}"));
        return _liveTableOpen;
    }

    /// <summary>The live table's HUD: the table's own panels (zone, quests, layers, cards, the living board) and a
    /// line about the eye; nothing of the combat HUD draws over it.</summary>
    private void DrawLiveTableHud()
    {
        _creatorSidebarRight = 0f;
        DrawTabletopUi();
        if (_liveTableStatus.Length > 0)
        {
            var io = ImGuiNET.ImGui.GetIO();
            var dl = ImGuiNET.ImGui.GetForegroundDrawList();
            float s = MathF.Max(1f, io.DisplaySize.Y / 1080f);
            var size = QaTextSize(_liveTableStatus, 14f * s);
            var at = new Vector2((io.DisplaySize.X - size.X) * 0.5f, 70f * s);
            dl.AddText(ImGuiNET.ImGui.GetFont(), 14f * s, at + new Vector2(1f, 1f) * s, 0xE6000000, _liveTableStatus);
            dl.AddText(ImGuiNET.ImGui.GetFont(), 14f * s, at, 0xFF8FD0FF, _liveTableStatus);
        }
    }

    // ── the live near layer ────────────────────────────────────────────────

    /// <summary>The live table's near draw: the game's world renderers through the table camera, lifted.</summary>
    private Action? LiveTableNearDraw()
    {
        if (_tabletop?.Board is not { } board || !Settings.Creator.Tabletop.World) return null;
        _liveTableView.K = _tabletop.Exaggeration;
        _liveTableView.BaseZ = _tabletop.BaseZ;
        _liveTableView.SetBoard(board.Top, board.Bottom, board.Left, board.Right);
        _liveTableTiles.Clear();
        if (_terrain is not null) foreach (var t in _terrain.LoadedTiles) _liveTableTiles.Add(t);
        double now = NowSeconds();
        float dt = _ttLastRenderAt > 0 ? (float)Math.Clamp(now - _ttLastRenderAt, 0, 0.25) : 0f;
        _ttLastRenderAt = now;
        var units = TabletopUnits();
        var light = TabletopNearLighting();
        return () => LiveTableRenderWorld(light, units, dt);
    }

    private void LiveTableRenderWorld(TabletopNearScene.NearLighting l, IReadOnlyList<WorldEntity> units, float dt)
    {
        var cam = _ttCamera;
        LiveTableApplyRenderers(l);
        var clip = _liveTableView.Board[0];
        _gl!.Enable(EnableCap.ClipDistance0);
        _gl.Enable(EnableCap.ClipDistance1);
        _gl.Enable(EnableCap.ClipDistance2);
        _gl.Enable(EnableCap.ClipDistance3);
        try
        {
            _liveTableWmoLift.Clear();
            _terrain?.Render(cam, clip);
            _wmo?.Render(cam, clip);
            _doodads?.Render(cam, clip);
            if (_liquid is not null) { _liquid.Time += dt; _liquid.Render(cam, clip); }
        }
        finally
        {
            _gl.Disable(EnableCap.ClipDistance0);
            _gl.Disable(EnableCap.ClipDistance1);
            _gl.Disable(EnableCap.ClipDistance2);
            _gl.Disable(EnableCap.ClipDistance3);
        }

        // Units: the real ones (lifted copies of the live entities) and the board's figures beyond the bubble. The
        // world's own creature renderer draws them, so every swing, cast, emote and hit reaction the packet handlers
        // fire plays here, and the spell poses it publishes are the lifted ones.
        if (_creatures is { } creatures)
        {
            creatures.SelfPlayerGuid = 0;
            creatures.BeginItemGlowFrame();
            creatures.Render(cam, units);
        }

        // Spell presentation, exactly the world pass's order, through the table camera.
        double spellNow = SpellClockNow;
        if (_spellEffects is not null)
        {
            _spellEffects.SyncItemGlows(_creatures?.ItemGlowPlacements ?? Array.Empty<ItemGlowPlacement>(), spellNow);
            _spellEffects.SyncUnitModelEffects(
                _creatures?.UnitModelEffectPlacements ?? Array.Empty<UnitModelEffectPlacement>(), spellNow);
        }
        CarriedLightFrame.Commit(_creatures?.CarriedLightPlacements ?? Array.Empty<CarriedLightPlacement>(), cam.Position);
        // Emission LOD measures from the camera: the table's camera is far by design, so measure from its target.
        var lodEye = cam.AuthoredTarget ?? cam.Position;
        if (_spellParticles is not null && _spellEffects is not null)
            _spellParticles.Simulate(TakeSpellClockDelta(dt), lodEye, _spellEffects.EmitterInstances(
                spellNow, SpellEffectUnitPose, _spellFxBillboardJointPoseB, cam.Position, cam.Forward),
                SpellParticleGroundHeight);
        if (_spellEffects is not null && _spellEffectMeshes is not null)
        {
            _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
            IEnumerable<SpellMeshDraw> meshes = _spellEffects.MeshInstances(spellNow, SpellEffectUnitPose);
            if (_spellParticles is not null) meshes = meshes.Concat(_spellParticles.GeometryInstances());
            _spellEffectMeshes.Render(cam, meshes, SpellGroundHeight);
        }
        if (_spellEffects is not null && _spellRibbons is not null)
            _spellRibbons.Render(cam, _spellEffects.RibbonInstances(spellNow, SpellEffectUnitPose), _spellFxBillboardJointPoseB);
        if (_spellChainBeams is not null && _spellChainBeamRenderer is not null)
            _spellChainBeamRenderer.Render(cam, spellNow, _spellChainBeams.Snapshot(spellNow, SpellEffectUnitPose), SpellEffectUnitPose);
        // World particles (campfires, torches, braziers): the emitters stand on their placements, which the table lifts.
        if (_particles is not null && _doodads is not null)
        {
            var near = cam.AuthoredTarget ?? cam.Position;
            _particles.Simulate(dt, near, _doodads.EmitterInstances(near, LiveTableBubbleYards * 3f)
                .Select(e => e with
                {
                    Transform = e.Transform * Matrix4x4.CreateTranslation(0f, 0f, LiveTableLiftAt(e.Transform.M41, e.Transform.M42)),
                }));
            _particles.Render(cam);
        }
        _spellParticles?.Render(cam);
    }

    /// <summary>Health bars over the live units that are fighting or hurt (the feed's bots draw their own), so a fight
    /// reads at table distance.</summary>
    private void LiveTableDrawUnitBars()
    {
        if (!_liveTableOpen || _ttRadius > 900f) return;
        var display = ImGuiNET.ImGui.GetIO().DisplaySize;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        float ui = MathF.Max(1f, display.Y / 1080f);
        float figure = TabletopLaw.FigureScale(Settings.Creator.Tabletop.FigureScale, _ttRadius);
        foreach (var u in _ttUnitsThisFrame)
        {
            if (!_entities.TryGet(u.Guid, out WorldEntity live) || _live.ContainsKey((uint)u.Guid) && u.IsPlayer) continue;
            uint max = live.Fields.MaxHealth, hp = live.Fields.Health;
            if (max == 0 || hp == 0 || live.Fields.ReadsDead || (!live.Engaged && hp >= max)) continue;
            float pct = Math.Clamp(hp / (float)max, 0f, 1f);
            if (!QaProject(u.Position + new Vector3(0f, 0f, 2.8f * figure * MathF.Max(live.Fields.Scale, 0.5f)), display, out var px)) continue;
            float w = 30f * ui, h = 3.5f * ui;
            var at = px - new Vector2(w * 0.5f, 0f);
            dl.AddRectFilled(at - new Vector2(1f, 1f) * ui, at + new Vector2(w, h) + new Vector2(1f, 1f) * ui, QaCol(new Vector3(0.06f, 0.04f, 0.02f), 0.85f));
            var col = pct > 0.5f ? QuestBoard.Hex(0x4caf50) : pct > 0.2f ? QuestBoard.Hex(0xe8a33a) : QuestBoard.Hex(0xe0614f);
            dl.AddRectFilled(at, at + new Vector2(w * pct, h), QaCol(col, 0.95f));
        }
    }

    /// <summary>Bend the world renderers for this frame's table pass: the view transform, culling that would be wrong
    /// for lifted placements or a camera kilometres out, model lifts, the table's light and fog, effect far clips off.
    /// (Light and fog come back by themselves: the world pass re-applies the atmosphere every frame. Everything else is
    /// put back by LiveTableEnd.)</summary>
    private void LiveTableApplyRenderers(TabletopNearScene.NearLighting l)
    {
        if (_terrain is { } terrain)
        {
            terrain.Tabletop = _liveTableView;
            terrain.ChunkCulling = false;
            terrain.SunDirection = l.SunDirection; terrain.SunColor = l.SunColor; terrain.SunIntensity = l.SunIntensity;
            terrain.AmbientColor = l.AmbientColor; terrain.AmbientIntensity = l.AmbientIntensity;
            terrain.FogColor = l.FogColor; terrain.FogStart = l.FogStart; terrain.FogEnd = l.FogEnd;
            terrain.VisibilityDistance = float.PositiveInfinity;
        }
        if (_wmo is { } wmo)
        {
            wmo.Tabletop = _liveTableView;
            wmo.UsePortalCulling = false; wmo.OcclusionCulling = false; wmo.FrustumCulling = false;
            wmo.SuppressDistanceLodShells = true;
            wmo.ModelLift ??= (origin, id) =>
            {
                float lift = _liveTableView.Lift(_terrain?.SampleHeight(origin.X, origin.Y) ?? origin.Z);
                _liveTableWmoLift[id] = lift;
                return lift;
            };
            wmo.SunDirection = l.SunDirection; wmo.SunColor = l.SunColor; wmo.SunIntensity = l.SunIntensity;
            wmo.AmbientColor = l.AmbientColor; wmo.AmbientIntensity = l.AmbientIntensity;
            wmo.FogColor = l.FogColor; wmo.FogStart = l.FogStart; wmo.FogEnd = l.FogEnd;
            wmo.VisibilityDistance = float.PositiveInfinity;
        }
        if (_doodads is { } d)
        {
            d.Tabletop = _liveTableView;
            d.DrawDistance = 100000f;
            d.ModelLift ??= (origin, wmoId) => wmoId > 0 && _liveTableWmoLift.TryGetValue(wmoId, out float lift)
                ? lift : _liveTableView.Lift(_terrain?.SampleHeight(origin.X, origin.Y) ?? origin.Z);
            d.SunDirection = l.SunDirection; d.SunColor = l.SunColor; d.SunIntensity = l.SunIntensity;
            d.AmbientColor = l.AmbientColor; d.AmbientIntensity = l.AmbientIntensity;
            d.FogColor = l.FogColor; d.FogStart = l.FogStart; d.FogEnd = l.FogEnd;
            d.VisibilityDistance = float.PositiveInfinity;
        }
        if (_liquid is { } liquid)
        {
            liquid.Tabletop = _liveTableView;
            liquid.SunDirection = l.SunDirection; liquid.SunColor = l.SunColor; liquid.SunIntensity = l.SunIntensity;
            liquid.AmbientColor = l.AmbientColor; liquid.AmbientIntensity = l.AmbientIntensity;
            liquid.FogColor = l.FogColor; liquid.FogStart = l.FogStart; liquid.FogEnd = l.FogEnd;
            liquid.UseAuthoredColors = true; liquid.HasAuthoredColors = true;
            liquid.OceanClose = liquid.OceanFar = liquid.RiverClose = liquid.RiverFar = l.WaterColor;
            liquid.OceanAlphaShallow = liquid.OceanAlphaDeep = liquid.RiverAlphaShallow = liquid.RiverAlphaDeep = l.WaterAlpha;
        }
        if (_creatures is { } c)
        {
            c.AnimateDistance = 100000f;
            c.ScaleMultiplier = TabletopLaw.FigureScale(Settings.Creator.Tabletop.FigureScale, _ttRadius);
            c.SunDirection = l.SunDirection; c.SunColor = l.SunColor; c.SunIntensity = l.SunIntensity;
            c.AmbientColor = l.AmbientColor; c.AmbientIntensity = l.AmbientIntensity;
            c.FogColor = l.FogColor; c.FogStart = l.FogStart; c.FogEnd = l.FogEnd;
        }
        if (_spellEffectMeshes is { } m)
        {
            m.FarClip = 0f; m.FogColor = l.FogColor; m.FogStart = l.FogStart; m.FogEnd = l.FogEnd;
            m.SunDirection = l.SunDirection; m.SunColor = l.SunColor; m.SunIntensity = l.SunIntensity;
            m.AmbientColor = l.AmbientColor; m.AmbientIntensity = l.AmbientIntensity;
        }
        if (_spellParticles is { } p) { p.FarClip = 0f; p.FogColor = l.FogColor; p.FogStart = l.FogStart; p.FogEnd = l.FogEnd; }
        if (_spellRibbons is { } r) { r.FarClip = 0f; r.FogColor = l.FogColor; r.FogStart = l.FogStart; r.FogEnd = l.FogEnd; }
        if (_spellChainBeamRenderer is { } beams) beams.FarClip = 0f;
    }

    /// <summary>The live entities on the board inside the loaded world, as lifted view copies (TabletopUnits adds them
    /// first). Units only; corpses keep their own path in the world pass.</summary>
    private void LiveTableAppendUnits(List<WorldEntity> into)
    {
        if (_tabletop?.Board is not { } board) return;
        foreach (var e in _entities.Units)
        {
            if (!e.IsUnit || !board.Contains(e.Position.X, e.Position.Y)) continue;
            if (!_liveTableTiles.Contains(TabletopLawTile(e.Position))) continue;
            into.Add(e.WithViewPosition(e.Position with { Z = e.Position.Z + LiveTableLiftAt(e.Position.X, e.Position.Y) }));
        }
    }

    /// <summary>True when a board figure stands where the live world already shows the truth (the eye's bubble).</summary>
    private bool LiveTableInBubble(Vector3 p)
        => _liveTableOpen && _controller is not null &&
           Vector2.DistanceSquared(new(p.X, p.Y), new(_controller.Position.X, _controller.Position.Y)) < LiveTableBubbleYards * LiveTableBubbleYards;
}
