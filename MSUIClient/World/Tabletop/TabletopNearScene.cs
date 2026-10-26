using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Doodads;
using MSUIClient.World.Units;
using MSUIClient.World.Wmo;
using Silk.NET.OpenGL;

namespace MSUIClient.World.Tabletop;

/// <summary>
/// The Quest Tabletop's near layer (shared_docs/QUEST_TABLETOP.md §4): the REAL world - full terrain, WMO buildings, M2
/// props, water and creatures - streamed in a ring of ADT tiles around the camera's ground target, drawn with the
/// table's exaggeration (<see cref="TabletopViewTransform"/>) and cut at the board rectangle.
///
/// It is a world of its own, modelled on <see cref="Portals.PortalDestinationScene"/>: its own terrain/WMO/doodad/liquid/
/// creature renderers and ADT cache, so the game's world, its streaming centre, collision and the controlled body are
/// never touched. Loading is staged and non-blocking (<see cref="Step"/> once per frame); a new focus tile restarts
/// the stages. Creatures are synthetic, view-only units supplied by the caller.
/// </summary>
public sealed class TabletopNearScene : IDisposable
{
    private enum Phase { Idle, WarmGeometry, QueueDoodads, WarmDoodads, PlaceDoodads, Ready }

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly TerrainRenderer _terrain;
    private readonly WmoRenderer _wmo;
    private readonly DoodadRenderer? _doodads;
    private readonly LiquidRenderer _liquid;
    private readonly CreatureRenderer? _creatures;
    private readonly int _radius;

    private Phase _phase;
    private AdtCache? _adts;
    private WdtFile? _wdt;
    private string? _mapDir;
    private HashSet<(int col, int row)> _ring = [];
    private (int col, int row) _centre;
    private Vector2 _focus;
    private readonly Dictionary<int, float> _wmoLift = new();
    private float _time;

    public TabletopViewTransform View { get; } = new();
    /// <summary>The ring of tiles this layer has fully loaded (the far layer hides these), empty while loading.</summary>
    public IReadOnlySet<(int col, int row)> ReadyTiles => _phase == Phase.Ready ? _ring : EmptyRing;
    private static readonly HashSet<(int col, int row)> EmptyRing = [];
    public bool Ready => _phase == Phase.Ready;
    public string Status => _phase == Phase.Ready
        ? $"near {_ring.Count} tiles around [{_centre.col},{_centre.row}]: {_wmo.InstanceCount} buildings, {_doodads?.InstanceCount ?? 0} props"
        : _phase == Phase.Idle ? "near layer idle" : $"near layer loading ({_phase})";
    public int Buildings => _wmo.InstanceCount;
    public int Props => _doodads?.InstanceCount ?? 0;
    public int CreaturesDrawn => _creatures?.DrawnLastFrame ?? 0;
    /// <summary>Item entry -> template for view-only players (the living board's bots): the remote-player renderer
    /// dresses them from it without any server query.</summary>
    public Func<uint, (bool Settled, ItemTemplate? Item)>? PlayerItemResolver
    {
        set { if (_creatures is not null) _creatures.PlayerItemResolver = value; }
    }
    public string? Failure { get; private set; }

    public TabletopNearScene(GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers, MpqMount mpq, int radius = 1)
    {
        _gl = gl;
        _config = config;
        _radius = Math.Clamp(radius, 0, 3);
        string shaders = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!Directory.Exists(shaders)) shaders = Path.Combine(config.RepoRoot, "MSUIClient", "Shaders");

        _terrain = new TerrainRenderer(gl, config, uploads, workers) { Tabletop = View, ChunkCulling = false };
        _wmo = new WmoRenderer(gl, config, uploads, workers)
        {
            Tabletop = View,
            UsePortalCulling = false,
            SuppressDistanceLodShells = true,
            OcclusionCulling = false,
            FrustumCulling = false,   // lifted placements: the unlifted boxes would cull wrongly
        };
        _liquid = new LiquidRenderer(gl) { Tabletop = View };
        if (config.Render.Doodads)
        {
            _doodads = new DoodadRenderer(gl, config, uploads, workers)
            {
                Tabletop = View,
                DemandStreaming = true,
                DrawDistance = 100000f,
                CollisionBasisIndex = config.Render.DoodadCollisionBasis,
            };
            _doodads.PortalVisibility = _wmo.IsDoodadPortalVisible;
        }
        _terrain.LoadShaders(shaders);
        _wmo.LoadShaders(shaders);
        _doodads?.LoadShaders(shaders);
        _liquid.LoadShaders(shaders);
        _liquid.LoadLiquidTextures(config.ClientDataPath);
        try { _creatures = new CreatureRenderer(gl, mpq, config, null, workers, uploads) { AnimateDistance = 100000f }; }
        catch (Exception e) { Console.WriteLine($"[tabletop] near creatures unavailable: {e.Message}"); }

        // Ride the exaggerated ground at true scale; a WMO's furniture rides with its building.
        _wmo.ModelLift = (origin, id) =>
        {
            float lift = View.Lift(_terrain.SampleHeight(origin.X, origin.Y) ?? origin.Z);
            _wmoLift[id] = lift;
            return lift;
        };
        if (_doodads is not null)
            _doodads.ModelLift = (origin, wmoId) => wmoId > 0 && _wmoLift.TryGetValue(wmoId, out float l)
                ? l : View.Lift(_terrain.SampleHeight(origin.X, origin.Y) ?? origin.Z);
    }

    /// <summary>Point the layer at a map (Map.dbc directory). Clears everything.</summary>
    public void SetMap(string mapDirectory)
    {
        if (string.Equals(_mapDir, mapDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _mapDir = mapDirectory;
        _wdt = WdtFile.Read(_config.ClientDataPath, mapDirectory);
        _adts = new AdtCache(_config.ClientDataPath, mapDirectory);
        _wmo.ResetForMapChange();
        _doodads?.ResetPlacements();
        _liquid.UnloadAll();
        _phase = Phase.Idle;
        _centre = (-1, -1);
    }

    /// <summary>Follow the camera's ground target: a new centre tile restarts the staged load.</summary>
    public void Focus(Vector2 groundTarget)
    {
        if (_adts is null || _wdt is null || _wdt.UsesGlobalWmo) return;
        _focus = groundTarget;
        var centre = TerrainRenderer.TileAt(groundTarget.X, groundTarget.Y);
        if (centre == _centre && _phase != Phase.Idle) return;
        _centre = centre;
        _ring = TerrainRenderer.TileRing(centre.col, centre.row, _radius);
        _ring.RemoveWhere(t => !_wdt.HasTile(t.col, t.row));
        _wmo.BeginOpaqueWorldEpoch();
        _doodads?.BeginOpaqueWorldEpoch();
        _terrain.QueuePreload(_ring, _adts, _centre);
        _wmo.QueuePreloadForTiles(_ring, _adts, groundTarget);
        _phase = Phase.WarmGeometry;
        Failure = null;
    }

    /// <summary>Advance the staged load by a small unit of work; never waits.</summary>
    public void Step()
    {
        try
        {
            switch (_phase)
            {
                case Phase.WarmGeometry:
                    _wmo.WarmNextPreload(waitForWorker: false);
                    _terrain.PumpPreloads();
                    if (!_terrain.PreloadReady(_ring) || _wmo.PendingPreloads != 0) return;
                    _terrain.SetResidency(_centre.col, _centre.row, _radius, _adts!);
                    _wmo.ResetPlacements();
                    _wmo.LoadForTiles(_ring, _adts!, warmedOnly: true);
                    _phase = Phase.QueueDoodads;
                    return;
                case Phase.QueueDoodads:
                    _liquid.LoadForTiles(_ring, _adts!);
                    _liquid.UpdateWmoLiquid(_wmo.LiquidVersion, _wmo.EnumerateLiquid());
                    if (_doodads is null) { _phase = Phase.Ready; return; }
                    _doodads.ResetPlacements();
                    _doodads.QueuePreloadForTiles(_ring, _adts!, _focus, RingReach);
                    _doodads.QueuePreloadModels(_wmo.EnumerateDoodads().Select(d => d.ModelPath));
                    _phase = _doodads.PendingPreloads == 0 ? Phase.PlaceDoodads : Phase.WarmDoodads;
                    return;
                case Phase.WarmDoodads:
                    for (int i = 0; i < 8 && _doodads!.PendingPreloads > 0; i++) _doodads.WarmNextPreload(waitForWorker: false);
                    if (_doodads!.PendingPreloads == 0) _phase = Phase.PlaceDoodads;
                    return;
                case Phase.PlaceDoodads:
                    _doodads!.LoadForTiles(_ring, _adts!, _focus, RingReach, reportDiagnostics: false);
                    foreach (var d in _wmo.EnumerateDoodads())
                        _doodads.AddPlaced(d.ModelPath, d.Transform, d.Light, d.WmoInstanceId, d.OwnerGroups);
                    _phase = Phase.Ready;
                    Console.WriteLine($"[tabletop] {Status}");
                    return;
            }
        }
        catch (Exception e)
        {
            Failure = e.Message;
            Console.WriteLine($"[tabletop] near layer failed: {e}");
            _phase = Phase.Idle;
        }
    }

    private float RingReach => (_radius + 1) * 533.33333f * 1.5f;

    /// <summary>
    /// Draw the near layer with the tabletop camera into the bound target (depth shared with the far layer).
    /// <paramref name="units"/> are the view-only creatures already lifted onto the shown ground.
    /// </summary>
    public void Render(Camera camera, NearLighting light, IReadOnlyList<WorldEntity> units, float dt)
    {
        if (_phase == Phase.Idle) return;
        _time += dt;
        _wmo.NowSeconds = _time;
        if (_doodads is not null) _doodads.NowSeconds = _time;
        _liquid.Time += dt;
        Apply(light);
        _wmoLift.Clear();
        var clip = View.Board[0];
        _gl.Enable(EnableCap.ClipDistance0);
        _gl.Enable(EnableCap.ClipDistance1);
        _gl.Enable(EnableCap.ClipDistance2);
        _gl.Enable(EnableCap.ClipDistance3);
        try
        {
            _terrain.Render(camera, clip);
            if (_phase == Phase.Ready)
            {
                _wmo.Render(camera, clip);
                _doodads?.Render(camera, clip);
            }
            _liquid.Render(camera, clip);
        }
        finally
        {
            _gl.Disable(EnableCap.ClipDistance0);
            _gl.Disable(EnableCap.ClipDistance1);
            _gl.Disable(EnableCap.ClipDistance2);
            _gl.Disable(EnableCap.ClipDistance3);
        }
        if (_creatures is { Ok: true } && units.Count > 0) _creatures.Render(camera, units);
    }

    /// <summary>True ground height (unexaggerated) in the loaded ring, or null.</summary>
    public float? GroundAt(float x, float y) => _terrain.SampleHeight(x, y);

    /// <summary>The table's light and fog, in the world renderers' terms.</summary>
    public readonly record struct NearLighting(Vector3 SunDirection, Vector3 SunColor, float SunIntensity,
        Vector3 AmbientColor, float AmbientIntensity, Vector3 FogColor, float FogStart, float FogEnd,
        Vector3 WaterColor, float WaterAlpha);

    private void Apply(NearLighting l)
    {
        _terrain.SunDirection = l.SunDirection; _terrain.SunColor = l.SunColor; _terrain.SunIntensity = l.SunIntensity;
        _terrain.AmbientColor = l.AmbientColor; _terrain.AmbientIntensity = l.AmbientIntensity;
        _terrain.FogColor = l.FogColor; _terrain.FogStart = l.FogStart; _terrain.FogEnd = l.FogEnd;
        _terrain.VisibilityDistance = float.PositiveInfinity;
        _wmo.SunDirection = l.SunDirection; _wmo.SunColor = l.SunColor; _wmo.SunIntensity = l.SunIntensity;
        _wmo.AmbientColor = l.AmbientColor; _wmo.AmbientIntensity = l.AmbientIntensity;
        _wmo.FogColor = l.FogColor; _wmo.FogStart = l.FogStart; _wmo.FogEnd = l.FogEnd;
        _wmo.VisibilityDistance = float.PositiveInfinity;
        if (_doodads is { } d)
        {
            d.SunDirection = l.SunDirection; d.SunColor = l.SunColor; d.SunIntensity = l.SunIntensity;
            d.AmbientColor = l.AmbientColor; d.AmbientIntensity = l.AmbientIntensity;
            d.FogColor = l.FogColor; d.FogStart = l.FogStart; d.FogEnd = l.FogEnd;
            d.VisibilityDistance = float.PositiveInfinity;
        }
        _liquid.SunDirection = l.SunDirection; _liquid.SunColor = l.SunColor; _liquid.SunIntensity = l.SunIntensity;
        _liquid.AmbientColor = l.AmbientColor; _liquid.AmbientIntensity = l.AmbientIntensity;
        _liquid.FogColor = l.FogColor; _liquid.FogStart = l.FogStart; _liquid.FogEnd = l.FogEnd;
        _liquid.UseAuthoredColors = true;
        _liquid.HasAuthoredColors = true;
        _liquid.OceanClose = _liquid.OceanFar = _liquid.RiverClose = _liquid.RiverFar = l.WaterColor;
        _liquid.OceanAlphaShallow = _liquid.OceanAlphaDeep = _liquid.RiverAlphaShallow = _liquid.RiverAlphaDeep = l.WaterAlpha;
        if (_creatures is { } c)
        {
            c.SunDirection = l.SunDirection; c.SunColor = l.SunColor; c.SunIntensity = l.SunIntensity;
            c.AmbientColor = l.AmbientColor; c.AmbientIntensity = l.AmbientIntensity;
            c.FogColor = l.FogColor; c.FogStart = l.FogStart; c.FogEnd = l.FogEnd;
        }
    }

    public void Dispose()
    {
        _creatures?.Dispose();
        _doodads?.Dispose();
        _liquid.Dispose();
        _wmo.Dispose();
        _terrain.Dispose();
    }
}
