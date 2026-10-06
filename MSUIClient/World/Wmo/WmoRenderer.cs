using System.Numerics;
using System.Diagnostics;
using System.Buffers;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Collision;
using MSUIClient.World.Units;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Wmo;

/// <summary>
/// Why a WMO group drew or did not draw. One winning reason per group, decided by
/// WmoRenderer.ClassifyGroup, which both the render path and the read-only pick /
/// dump explanation call - so the reason reported can never drift from the reason
/// acted on. The last group (NotResident..Antiportal) is assigned at the build /
/// stream stage by the scene dump, not by ClassifyGroup. See PLAN_03_REASON_CODES.md.
/// </summary>
public enum WmoReasonCode
{
    Drawn,
    DrawnShellFar,
    ShellNearSuppressed,
    InteriorCull,
    DistanceCulled,
    FrustumCulled,
    OcclusionCulled,
    OverrideShow,
    OverrideHide,
    NotResident,
    MissingFile,
    NotBuilt,
    NoGeometry,
    Antiportal,
}

/// <summary>
/// Draws WMO buildings — the abbey, the farmhouses, Stormwind.
///
/// PIPELINE
///   ADT MODF placements (AdtTerrainReader.AdtResult.Wmos)
///     -> root .wmo out of the MPQs        (WmoReader.ParseRoot)
///     -> group files _000.wmo, _001.wmo   (WmoReader.ParseGroup)
///     -> one VAO per group, one draw per MOBA batch
///   Textures come from MOMT material names via AdtTerrainReader.ReadBlpPixels,
///   which is the same BLP -> BGRA -> GL path the terrain tileset already uses.
///
/// PLACEMENT — the coordinate part, which is where this could go wrong
///
///   MODF positions are NOT world coordinates. They live in ADT placement
///   space: a Y-up system whose origin is the map corner, with posX and posZ
///   running 0..34133 (= 64 * 533.33333). AdtTerrainReader's own diagnostics
///   pin the axes down:
///
///       posX / 533.33 -> ADT column, and column = 32 - worldY / 533.33
///       posZ / 533.33 -> ADT row,    and row    = 32 - worldX / 533.33
///       posY          -> world Z
///
///   Solving those gives the conversion in <see cref="PlacementToWorld"/>:
///
///       worldX = C - posZ        C = 32 * 533.33333 = 17066.67
///       worldY = C - posX
///       worldZ = posY
///
///   C is the SAME constant the vmap files use, which is a good sign rather
///   than a coincidence — both are measuring from the same map corner. And the
///   linear part has determinant +1, so it is a rotation, not a mirror:
///   triangle winding and normals come through unharmed.
///
///   The model's own vertices are already in this Y-up space, so the whole
///   placement transform is built there and converted to world once, at the
///   end. Same shape as the vmap loader, for the same reason.
///
/// THE ONE THING NOT YET VERIFIED is the ROTATION convention — whether MODF's
/// Euler triple composes as RotY(rotY - 270) * RotZ(-rotX) * RotX(rotZ), which
/// is what most ADT renderers use. So this self-checks: MODF also ships an
/// axis-aligned bounding box for the PLACED model, in the same space. Convert
/// that box to world, compare it against the bounds of the geometry we actually
/// transformed, and a wrong rotation shows up as a mismatch immediately —
/// printed at load, no walking around required.
/// </summary>
public sealed class WmoRenderer : IDisposable
{
    /// <summary>Position(3) + Normal(3) + UV(2) + MOCV colour(4).</summary>
    private const int FloatsPerVertex = 12;

    /// <summary>Map corner offset. Identical to VmapFormat.CoordShift.</summary>
    private const float CoordShift = 32f * 533.33333f;

    private sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public bool TwoSided;
        /// <summary>MOMT F_UNLIT: texture brightness, no scene light (lamp glass, glow panes).</summary>
        public bool Unlit;
        /// <summary>MOMT F_SIDN emissive colour, added × the night fraction on lit lanes; zero when clear.</summary>
        public Vector3 Sidn;

        /// <summary>
        /// Whether this batch's texture actually carries alpha. The cutoff
        /// itself is applied at DRAW time, not baked here — baking it made the
        /// HUD slider inert, which cost a debugging round trip.
        /// </summary>
        public bool AlphaTest;
        public bool Transparent;

        /// <summary>
        /// Which MOBA run this batch belongs to, which is what decides how its
        /// baked MOCV colour combines with the runtime sun:
        ///   1 transparent — fades from baked to daylight across a portal
        ///   2 interior    — baked only; no sun, no runtime ambient
        ///   3 exterior    — daylight only; MOCV ignored
        /// Also 3 for any group with no MOCV at all, which is how a vanilla
        /// exterior group is lit and matches the pre-MOCV behaviour exactly.
        /// </summary>
        public int Type = 3;
    }

    private sealed class GroupMesh : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public List<Batch> Batches = [];
        public Vector3 LocalMin, LocalMax;
        // Authored MOGP bounds. Rendering/culling keeps the bounds measured
        // from MOVT above; the minimap law explicitly uses these file bounds.
        public Vector3 AuthoredLocalMin, AuthoredLocalMax;
        public bool IsInterior;
        public bool IsDistanceLod;

        // Identity, kept for the in-game group picker and for live (re-tunable)
        // shell classification at draw time.
        public int GroupIndex;
        public string GroupName = "";
        public uint GroupFlags;
        public uint GroupWmoId;
        public uint GroupLiquid = 0x0fu;
        public int VertexCount;

        // The run of MOPR entries that belong to this group (MOGP +0x24/+0x26):
        // every doorway out of it. Retained here so the portal graph survives
        // the load job - PreparedWmo is transient and the raw WmoGroupData is
        // discarded once the mesh is uploaded. PLAN_10 D1/D2.
        public int PortalStart;
        public int PortalCount;

        // Local-space geometry retained on the CPU purely for the triangle-level
        // group picker (the GPU copy can't be read back cheaply). Positions only.
        public Vector3[] PickPositions = [];
        public int[] PickIndices = [];
        // PickIndices minus the collision-only faces (MOPY material 0xFF): what is actually drawn.
        public int[] RenderedPickIndices = [];

        // The exact walking-collision face set for this group (MOPY DETAIL
        // excluded), retained for the camera's current-room down ray. AABBs are
        // only a broad phase; using them as the room verdict makes overlapping
        // door/stair cells select whichever box happens to be smallest.
        public Vector3[] CollisionTriangles = [];

        // MOCV of every CollisionTriangles vertex (rgb bytes, index-parallel, 3 per
        // vertex), kept ONLY for INTERIOR groups that are not EXTERIOR/EXTERIOR_LIT
        // and ship vertex colours; empty otherwise. This is what lights a unit or a
        // gameobject standing on the floor - see ResolveInteriorLight.
        public byte[] CollisionColors = [];

        // DETAIL faces which stop the camera but not the walking body: MOPY
        // DETAIL (0x04) set and NOCAMCOLLIDE (0x02) clear. Kept disjoint from
        // CollisionTriangles for the archived camera-void fallback only; these
        // must never enter walking collision.
        public Vector3[] CameraOnlyTriangles = [];

        // Interior render faces accepted by the client's footprint/footstep ray
        // (MOPY flags & 0x88 == 0), plus each face's MOMT GroundType.  This is
        // deliberately separate from walking collision: many authored floors
        // have a render sheet over a coplanar collision sheet.
        public Vector3[] FootstepTriangles = [];
        public uint[] FootstepTerrainTypes = [];

        // MOCV rgb per FootstepTriangles vertex, INTERIOR groups only (see
        // CollisionColors). The render floor is what a unit visibly stands on and
        // many authored floors have no coplanar walking face - the Ironforge
        // Commons by the bank is one - so the interior-light probe reads it too.
        public byte[] FootstepColors = [];

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
        }
    }

    private sealed class Model : IDisposable
    {
        public List<GroupMesh> Groups = [];
        public int TriangleCount;

        // ── Portal graph (PLAN_10) ──────────────────────────────────────────
        //
        // MOPV/MOPT/MOPR, retained from the load job. WmoReader has parsed these
        // since the WMO reader was written and the comment on WmoRootData.Portals
        // still says "no renderer reads these yet" - this is where that stops
        // being true. Kept in WMO LOCAL space, exactly as authored: an instance
        // transform turns them into world space when needed, and storing them
        // pre-transformed would break the moment a model is placed twice.
        public List<(float x, float y, float z)> PortalVertices = [];
        public List<WmoPortal> Portals = [];
        public List<WmoPortalRef> PortalRefs = [];

        /// <summary>MOHD's portal count, for the cross-check in PLAN_10 §7 step 2.</summary>
        public uint DeclaredPortalCount;

        /// <summary>MOHD +0x20: WMOAreaTable.WMOID.</summary>
        public uint WmoId;

        /// <summary>
        /// The building's embedded doodads — its furniture. Beds, kitchen
        /// fittings, tables, barrels: none of these are ADT placements, they
        /// belong to the WMO itself via MODS/MODN/MODD and are placed in WMO
        /// LOCAL space, so they ride the building's own transform.
        /// </summary>
        public List<WmoDoodadSet> DoodadSets = [];
        public List<WmoDoodadDef> Doodads = [];

        /// <summary>
        /// Per-doodad baked interior light, index-parallel to <see cref="Doodads"/>.
        ///
        /// rgb = MODD.color / 255, a = daylight blend (0 = fully baked interior,
        /// 1 = fully outdoor sun+ambient). Doodads owned by an EXTERIOR or
        /// EXTERIOR_LIT group — lamp posts, chimneys, the crates stacked against
        /// an outside wall — get (0,0,0,1), i.e. lit exactly as they are today.
        ///
        /// See BuildDoodadLighting for why MODD.color is a baked light and not
        /// the "tint" the wiki calls it.
        /// </summary>
        public Vector4[] DoodadLight = [];

        /// <summary>MODR ownership, index-parallel to <see cref="Doodads"/>.
        /// A boundary prop may belong to more than one group and is visible when
        /// any owning group is in the portal PVS.</summary>
        public int[][] DoodadOwners = [];

        /// <summary>
        /// Collidable triangles in WMO local space, three vertices each.
        ///
        /// This is the whole point of collecting collision here rather than
        /// from vmaps: it is the SAME vertex array the renderer uploads, so the
        /// wall you see and the wall you hit cannot be in different places. The
        /// vmap path loads a second copy of the same building through a second
        /// transform chain, and any disagreement between the two is a bug that
        /// simply cannot exist if there is only one chain.
        /// </summary>
        public Vector3[] CollisionTriangles = [];
        /// <summary>Collision-only faces (MOPY material 0xFF): solid but never drawn.</summary>
        public Vector3[] BlockerTriangles = [];
        public int CollisionSkipped;

        /// <summary>
        /// MLIQ surfaces — the canals, fountains, indoor pools and lava channels
        /// this building contains (PLAN_15_WMO_LIQUID.md).
        ///
        /// Kept in WMO LOCAL space for the same reason PortalVertices and
        /// CollisionTriangles are: a model may be placed more than once, and
        /// storing anything pre-transformed breaks the second placement.
        /// EnumerateLiquid applies the instance transform.
        ///
        /// **Holds the GroupMesh itself, not a group index, and that is
        /// deliberate.** `Groups` only receives groups that survived being
        /// non-null and building, so its indices do NOT line up with the source
        /// group indices in a model where anything was skipped — and something is
        /// skipped in most large models. An index here would silently read a
        /// different group's bounding box, which is exactly the kind of
        /// off-by-a-few-entries fault that produces plausible wrong numbers
        /// rather than a crash.
        /// </summary>
        public List<(WmoLiquid Liquid, GroupMesh Mesh)> Liquids = [];

        public void Dispose()
        {
            foreach (var g in Groups) g.Dispose();
        }
    }

    /// <summary>
    /// Incremental WMO build state. A city WMO can contain hundreds of group
    /// files; treating the whole root as one frame-sized operation caused the
    /// multi-second freezes seen while walking into Stormwind.
    /// </summary>
    private sealed class ModelLoadJob
    {
        public required string RootPath;
        public required Task<PreparedWmo> Worker;
        public PreparedWmo? Ready;
        public Task<UploadedWmo>? Upload;
        public bool UploadAccepted;
        public Model Model = new();
        public List<Vector3> Collision = [];
        /// <summary>MOPY material 0xFF faces: solid, never drawn (invisible walls, door blockers).
        /// Kept apart so party sight can treat them as blockers only (World/PartySight.cs).</summary>
        public List<Vector3> Blockers = [];
        public int NextGroup;
        public int Skipped;
        public int MissingFiles, Unparsed, Empty, Antiportal, Unbuilt, BatchesDropped;
        public int IndicesCovered, IndicesTotal;
        public System.Diagnostics.Stopwatch Timer = System.Diagnostics.Stopwatch.StartNew();
    }

    private sealed class UploadedWmo
    {
        public Dictionary<string, Texture?> Textures = new(StringComparer.OrdinalIgnoreCase);
        public List<UploadedGroup?> Groups = [];
    }

    private sealed class UploadedGroup
    {
        public uint Vbo;
        public uint Ebo;
    }

    private sealed class PreparedWmo
    {
        public WmoRootData? Root;
        public bool MissingRoot;
        public List<WmoGroupData?> Groups = [];
        public List<PreparedTexture> Textures = [];
        public int MissingFiles, Unparsed, Empty, Antiportal;
    }

    private sealed class PreparedTexture
    {
        public required string Path;
        public byte[]? Bgra;
        public bool Pooled;
        public int Width;
        public int Height;
        public byte MaxAlpha;
    }

    private sealed class Instance
    {
        public int Id;
        public Model Model = null!;
        public Matrix4x4 Transform;
        public Vector3 WorldMin, WorldMax;
        public Vector3 Origin;
        public string Path = "";

        /// <summary>Owning server GameObject for a dynamic WMO hull; zero for
        /// ordinary MODF/WDT scenery placements.</summary>
        public ulong DynamicGuid;

        /// <summary>Which MODS doodad set this placement asked for.</summary>
        public int DoodadSet;

        /// <summary>MODF +0x3C: WMOAreaTable.NameSetID.</summary>
        public uint NameSetId;

        /// <summary>Appear-fade spawn time in seconds; 0 = opaque/no fade.</summary>
        public float AppearStart;
    }

    /// <summary>Per-frame inputs the group classifier needs, gathered once per instance.</summary>
    private readonly struct FrameCullContext
    {
        public readonly Vector3 CameraPosition;
        public readonly bool CameraInside;
        public readonly float EffectiveDrawDistance;
        public readonly Matrix4x4 ViewProjection;

        /// <summary>PLAN_10: interior groups reachable through portals from the
        /// camera's cell this frame, in FILE-index space. Null when portal culling
        /// is off or could not seed for this instance - the interior heuristic then
        /// runs unchanged (D6).</summary>
        public readonly HashSet<int>? ReachableGroups;

        /// <summary>PLAN_10 D1: the camera is standing in a real cell of THIS WMO
        /// (CameraGroup belongs to this instance). A precise "have I crossed inside"
        /// signal - null on the bridge/terrain, set once through the gate - used to
        /// drop the distance shell at the doorway instead of at a yard mark.</summary>
        public readonly bool CameraInCell;

        public FrameCullContext(Vector3 cameraPosition, bool cameraInside,
            float effectiveDrawDistance, Matrix4x4 viewProjection,
            HashSet<int>? reachableGroups = null, bool cameraInCell = false)
        {
            CameraPosition = cameraPosition;
            CameraInside = cameraInside;
            EffectiveDrawDistance = effectiveDrawDistance;
            ViewProjection = viewProjection;
            ReachableGroups = reachableGroups;
            CameraInCell = cameraInCell;
        }
    }

    private readonly GL _gl;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private readonly Dictionary<ulong, Instance> _dynamicByGuid = [];
    private int _nextInstanceId;

    // Final-camera portal seeds and PVS, keyed by placed WMO identity rather
    // than model path (the same root can be placed more than once).
    private readonly Dictionary<int, int[]> _cameraSeeds = [];
    private readonly Dictionary<int, HashSet<int>?> _portalVisible = [];

    // Per-frame scratch for the front-to-back draw order. Fields rather than
    // locals so the draw loop allocates nothing: this runs every frame, and a
    // per-frame List in a hot path is how a GC pause ends up looking like a
    // rendering hitch.
    private readonly List<(float Distance, Instance Instance)> _drawOrder = [];
    private readonly List<(float Distance, GroupMesh Group)> _visibleGroups = [];

    /// <summary>
    /// One instance's contribution to the frame, after culling: the uniforms it
    /// needs and the slice of <see cref="_flatGroups"/> it owns.
    ///
    /// Culling runs once, in instance order; the two draw passes then walk these
    /// slices in opposite directions.
    /// </summary>
    private readonly record struct InstanceSlice(
        Matrix4x4 Model, float AppearAlpha, bool Fading, int GroupStart, int GroupCount);

    private readonly List<InstanceSlice> _instanceSlices = [];
    private readonly List<GroupMesh> _flatGroups = [];
    private readonly HashSet<string> _placed = [];
    private readonly PriorityQueue<string, float> _preloadQueue = new();
    private Vector2? _preloadStreamCentre;

    /// <summary>
    /// Preload-ring tiles whose ADT had not finished parsing when the ring was
    /// computed. Retried every frame from <see cref="WarmNextPreload"/> instead
    /// of being waited on - see the contract on QueuePreloadForTiles.
    /// </summary>
    private readonly HashSet<(int col, int row)> _deferredRingTiles = new();
    private AdtCache? _ringAdts;
    private readonly HashSet<string> _preloadQueued = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxConcurrentPreloads = 4;
    private readonly List<ModelLoadJob> _preloadJobs = [];
    private int _preloadFinalizeCursor;

    /// <summary>Textures whose decoded alpha channel is entirely zero.</summary>
    private readonly HashSet<string> _opaqueTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textures whose 1-bit alpha came back as 0/1 and was rescaled to 0/255.</summary>
    private readonly HashSet<string> _rescaledAlpha = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BLPs that were named by a material but could not be decoded.</summary>
    private readonly HashSet<string> _failedTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Batches that ended up with no texture at all, from any slot.</summary>
    private int _texturelessBatches;

    /// <summary>Frame counter for the throttled [wmo-vis] visibility trace.</summary>
    private int _wmoVisLogFrames;

    /// <summary>Largest WMO group count seen so far this frame (for the HUD trace).</summary>
    private int _frameLargestWmoGroupCount;

    private Shader _shader = null!;

    public int InstanceCount => _instances.Count;
    public int DynamicInstanceCount => _dynamicByGuid.Count;

    /// <summary>
    /// Every placed building's world bounds, for cross-checking against the
    /// collision placement of the same model.
    /// </summary>
    public IEnumerable<(string Path, Vector3 Min, Vector3 Max, Vector3 Origin)> Placements
        => _instances.Select(i => (i.Path, i.WorldMin, i.WorldMax, i.Origin));
    public int ModelCount => _models.Count(m => m.Value is not null);
    public int TextureCount => _textures.Count(t => t.Value is not null);
    public int PendingPreloads =>
        _deferredRingTiles.Count + _preloadQueue.Count + _preloadJobs.Count;
    public Action<string, float>? PreloadDequeued { get; set; }

    /// <summary>Ring tiles still waiting on an ADT parse. Should trend to zero.</summary>
    public int DeferredRingTiles => _deferredRingTiles.Count;
    public int TotalTriangles { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public int VisibleGroupsLastFrame { get; private set; }
    public int LodGroupsCulledLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public long TrianglesLastFrame { get; private set; }
    public double RenderMilliseconds { get; private set; }
    public void NoteNotRendered() => RenderMilliseconds = 0;
    public bool Enabled { get; set; } = true;
    public bool FrustumCulling { get; set; } = true;
    public bool UseDistanceLodShells { get; set; } = true;

    /// <summary>
    /// Reject distance-only impostor groups outright while retaining normal
    /// detailed-group classification. This is intentionally separate from
    /// <see cref="UseDistanceLodShells"/>: turning that switch off makes an
    /// already-classified shell fall through as ordinary detail geometry. The
    /// isolated real-portal preview uses this fail-closed shell mode because it
    /// has already warmed the destination's detailed WMO groups and must never
    /// composite a far-city silhouette over them. Active-world rendering leaves
    /// it false.
    /// </summary>
    public bool SuppressDistanceLodShells { get; set; }

    // ── appear fade (benilla model_fade.rs) ─────────────────────────────────────

    /// <summary>Ease a streamed-in building in over <see cref="AppearFadeSeconds"/>
    /// (alpha = t^3) instead of popping. Off restores the original hard pop-in.</summary>
    public bool AppearFade { get; set; }

    /// <summary>Appear-fade ramp length in seconds (benilla APPEAR_FADE_SECS = 2).</summary>
    public float AppearFadeSeconds { get; set; } = 2f;

    /// <summary>World clock in seconds, pushed each frame by GameLoop.</summary>
    public float NowSeconds { get; set; }

    /// <summary>True once the loading curtain has lifted; while false the initial
    /// buildings are stamped opaque so the curtain covers the first reveal.</summary>
    public bool WorldShown { get; set; }

    /// <summary>Spawn time per placement KEY, surviving ResetPlacements, so a tile
    /// crossing's rebuild does not re-fade resident buildings. See the doodad
    /// renderer for the full rationale.</summary>
    private readonly Dictionary<string, float> _appearStartByKey = new(StringComparer.Ordinal);
    private const int AppearKeyCap = 65536;

    /// <summary>
    /// Start a new opaque world residency epoch. Placement fade keys normally
    /// survive a same-world ring rebuild, but they must not survive when this
    /// renderer is recycled as an isolated portal destination: old positive
    /// timestamps paired with a reset/frozen preview clock make every new WMO
    /// remain at alpha zero forever.
    /// </summary>
    /// <summary>
    /// Record every placement already resident as opaque. A Real Portals prepared world is built with
    /// AppearFade off, so it remembers none of its keys; switched on at promotion, the first tile crossing
    /// (a full ring rebuild) then saw the whole visible world as new and faded it all in from nothing - every
    /// tree vanishing seconds after a crossing (WoW Karting frame bursts, 2026-10-04).
    /// </summary>
    public void MarkPlacedOpaque()
    {
        foreach (string key in _placed)
            if (_appearStartByKey.Count < AppearKeyCap) _appearStartByKey.TryAdd(key, 0f);
    }

    public void BeginOpaqueWorldEpoch(float nowSeconds = 0f)
    {
        _appearStartByKey.Clear();
        NowSeconds = nowSeconds;
        WorldShown = false;
    }

    private float ResolveAppearStart(string key)
    {
        if (!AppearFade) return 0f;
        if (_appearStartByKey.TryGetValue(key, out float start)) return start;
        start = WorldShown ? NowSeconds : 0f;
        if (_appearStartByKey.Count < AppearKeyCap) _appearStartByKey[key] = start;
        return start;
    }

    /// <summary>
    /// Yards of slack on the per-group "is the camera inside this WMO" test that
    /// drives the distance-impostor swap (the Stormwind cathedral / grand-entrance
    /// stand-ins) and keeps a large WMO's own interior visible from within.
    /// Positive grows every group box (you count as inside sooner / from further
    /// out); negative shrinks it (you must be deeper in). Tune against the real
    /// client so the impostor drops right at the gate.
    /// </summary>
    public float InsideInstanceMargin { get; set; } = 0f;

    /// <summary>Dump the group table of large WMOs once at load (name/flags/LOD).
    /// Off by default now that it's captured; the HUD toggle re-enables it (then reload).</summary>
    public bool DumpLargeWmoGroups { get; set; } = false;

    /// <summary>Distance (yd) at which a big WMO's own interior cells are culled
    /// while the camera is OUTSIDE it. HUD-tunable. Was the hard-coded 120.</summary>
    public float InteriorCullDistance { get; set; } = 120f;

    /// <summary>
    /// PLAN_10 D7 - portal-traversal interior visibility (benilla wmo_portal).
    /// When on and the camera is inside this WMO, an interior group draws only if
    /// it is reachable through on-screen doorways from the camera's cell - which
    /// hides Stormwind's exterior roof (and every other unreachable interior) from
    /// within. Off (default) keeps the 120-yd <see cref="InteriorCullDistance"/>
    /// heuristic. It NEVER removes an exterior group or a distance-LOD shell
    /// (D4/D5), only interior groups, and falls back to the heuristic whenever it
    /// cannot seed or reaches nothing (D6). Default off so a visibility regression
    /// can't ship silently; flip it on in the WMO panel to A/B it.
    /// </summary>
    public bool UsePortalCulling { get; set; } = true;

    // ════════════════════════════════════════════════════════════════════════
    // PLAN_10 D1 — which group is the camera in?
    //
    // Portal traversal starts from the camera's group, so this is the whole
    // problem before it is any of the problem: get it wrong and every later
    // symptom looks like a portal bug. It is also useful on its own - interior
    // lighting and MFOG both need it - which is why the plan says build it
    // alone and confirm it against walking through a door.
    //
    // Nothing here culls anything yet. This is the instrument.
    // ════════════════════════════════════════════════════════════════════════

    // ── SUI free-view cutaway (Divinity-style; deliberately removable) ────────
    // One input property, one seed resolution in UpdateCameraCell, one override
    // where the flood is chosen. Delete those three and the renderer is
    // byte-identical to before; at runtime the same is one Settings checkbox.

    /// <summary>
    /// World position of the cutaway subject (the commanded toon), or null when
    /// the feature is off / nobody indoors is commanded. When it resolves to a
    /// non-shell cell of some instance, that instance's flood is seeded HERE
    /// instead of from the camera — see <see cref="ComputeCutawayGroups"/>.
    /// </summary>
    public void SetCutawaySubject(Vector3? world, float? terrainWorldZ = null)
    {
        _cutawaySubject = world;
        _cutawaySubjectTerrainZ = terrainWorldZ;
    }

    private Vector3? _cutawaySubject;
    private float? _cutawaySubjectTerrainZ;

    // ── Cut plane (Engine/WorldCut.cs) ──────────────────────────────────────────────────────
    private Vector3? _cutPlaneSubject;
    private float _cutPlaneHeight = 4.5f;
    private float? _cutPlaneTerrainZ;                    // terrain under the subject, world Z
    private const float CutPlaneMaxFeetDrop = 4.5f;      // probe is 1.5 yd over the feet; more is a storey below
    private (int InstanceId, HashSet<int> Groups)? _cutPlaneGroups;
    private readonly HashSet<GroupMesh> _cutPlaneMeshes = [];   // the opened cells, for the batch loop
    private const float CutPlaneReachYards = 35f;        // cells further than this stay roofed
    private const float CutPlaneFootprintCapYards = 45f; // half-extent cap around the subject
    private string _cutPlaneLoggedState = "off";

    /// <summary>World-space chest points of the party members the line-of-sight cut protects
    /// (Engine/WorldCut.cs). Empty = no tunnels. Set by GameLoop once per frame.</summary>
    public List<Vector3> SightTargets { get; } = [];

    /// <summary>The Command View's camera-side slice around the primary (Engine/WorldCut.cs),
    /// or null. Set by GameLoop once per frame after the cell update; only meaningful while
    /// <see cref="ActiveCut"/> supplies the footprint.</summary>
    public WorldSlice? Slice { get; set; }

    private void SetSightUniforms(Vector3 cameraPosition)
    {
        int count = Math.Min(SightTargets.Count, WorldCut.MaxSightLines);
        _shader.Set("uSightCount", count);
        for (int i = 0; i < count; i++)
            _shader.Set($"uSightTo[{i}]", SightTargets[i] - cameraPosition);
        _shader.Set("uSightRadius", new Vector2(WorldCut.SightRadiusNear, WorldCut.SightRadiusFar));
        SetSliceUniforms(cameraPosition);
    }

    private void SetSliceUniforms(Vector3 cameraPosition)
    {
        _shader.Set("uSliceActive", Slice is not null ? 1 : 0);
        if (Slice is not WorldSlice slice) return;
        _shader.Set("uSliceDir", slice.Forward);
        _shader.Set("uSliceDepth", slice.Depth);
        _shader.Set("uSliceFloorZ", slice.RelativeFloorZ(cameraPosition));
        _shader.Set("uSliceCentre", slice.RelativeCentre(cameraPosition));
        _shader.Set("uSliceRadius", WorldSlice.Radius);
    }

    /// <summary>The interior cut resolved by the last <see cref="UpdateCameraCell"/>, or null.
    /// GameLoop hands it to the terrain and doodad renderers and floors the free-view rig on it.</summary>
    public WorldCut? ActiveCut { get; private set; }

    /// <summary>Creator void stage (shared_docs/SPELL_CREATOR_IDE.md §2.1): keep only floors
    /// inside this disc around the acting body. World space; null = off.</summary>
    public (Vector3 Centre, float Radius, float HalfHeight)? Stage { get; set; }

    /// <summary>Command View party sight (World/PartySight.cs); null = never consulted.</summary>
    public PartySightPass? PartySight { get; set; }

    /// <summary>Feed the commanded unit's feet (or null to switch the cut off) and the cut height
    /// above them, once per frame before <see cref="UpdateCameraCell"/>.</summary>
    public void SetCutPlaneSubject(Vector3? feet, float height, float? terrainWorldZ = null)
    {
        _cutPlaneSubject = feet;
        _cutPlaneTerrainZ = terrainWorldZ;
        _cutPlaneHeight = height;
    }

    // ── Party sight: the primary's rooms must DRAW ─────────────────────────────────────────
    // Portal culling draws the interior cells reachable from the CAMERA's cell; from the sky
    // that is the exterior shell and whatever doorway is in view. Party sight cuts the roof
    // wherever the primary sees the floor beneath, and that floor is in a cell the camera never
    // reached - so the cut opened onto nothing (sky through a cave roof, capture 2026-09-02).
    // Same remedy as the roof plane: the cells around the subject join the reachable set.
    private Vector3? _sightSubject;
    private float? _sightTerrainZ;
    private Dictionary<int, HashSet<int>>? _sightGroups;   // instance id -> groups to draw
    private const float SightReachYards = 100f;   // >= PartySightPass.Range, so every seen cell draws

    /// <summary>Feed the party sight subject's feet (null = off) once per frame before
    /// <see cref="UpdateCameraCell"/>, the way <see cref="SetCutPlaneSubject"/> is fed.</summary>
    public void SetPartySightSubject(Vector3? feet, float? terrainWorldZ = null)
    {
        _sightSubject = feet;
        _sightTerrainZ = terrainWorldZ;
    }

    private string _sightLoggedState = "";

    /// <summary>
    /// Every group of every building within <see cref="SightReachYards"/> of the subject, by
    /// distance alone - no cell election, no portal flood. The subject at a cave MOUTH stands
    /// on terrain outside any cell (the owner's exact case), so a seed search finds nothing;
    /// and this is only a "draw these" set, not a footprint: an interior the primary cannot
    /// see stays behind opaque walls or is discarded by the sight rule itself, so drawing a
    /// few extra rooms costs GPU, never correctness.
    /// </summary>
    private void ResolveSightGroups()
    {
        _sightGroups = null;
        string state = "off";
        if (_sightSubject is Vector3 feet)
        {
            state = "no-building-in-reach";
            int buildings = 0, cells = 0;
            foreach (var instance in _instances)
            {
                if (DistanceToBox(feet, instance.WorldMin, instance.WorldMax) > SightReachYards) continue;
                HashSet<int>? reached = null;
                foreach (var g in instance.Model.Groups)
                {
                    if (g.IsDistanceLod) continue;
                    var (gMin, gMax) = TransformedBounds(g, instance.Transform);
                    if (DistanceToBox(feet, gMin, gMax) > SightReachYards) continue;
                    (reached ??= []).Add(g.GroupIndex);
                }
                if (reached is null) continue;
                (_sightGroups ??= [])[instance.Id] = reached;
                buildings++;
                cells += reached.Count;
            }
            if (buildings > 0) state = $"engaged: {buildings} building(s), {cells} cell(s)";
        }
        if (state != _sightLoggedState)
        {
            _sightLoggedState = state;
            Console.WriteLine($"[party-sight] rooms {state}");
        }
    }

    private (int InstanceId, HashSet<int> Groups)? _cutaway;
    private string _cutawayLoggedState = "off";

    /// <summary>Where the camera is, in WMO terms. Null means outdoors.</summary>
    public readonly record struct CameraCell(
        string InstancePath, int GroupIndex, string GroupName,
        bool IsInterior, float Volume, int PortalCount, bool IsExterior = false,
        int InstanceId = 0);

    /// <summary>
    /// One authored WMO group selected for the interior minimap. Bounds remain
    /// in WMO-local space so a consumer can project every group through the
    /// context's one authoritative
    /// <see cref="InteriorMinimapContext.WorldToLocal"/> transform.
    /// </summary>
    public readonly record struct InteriorMinimapGroup(
        int GroupIndex, string GroupName, uint GroupFlags, uint GroupWmoId,
        Vector3 LocalMin, Vector3 LocalMax);

    /// <summary>WMOAreaTable join keys from the position-cast zone-text ray.</summary>
    public readonly record struct AreaMinimapIdentity(
        uint RootWmoId, uint NameSetId, uint GroupWmoId);

    /// <summary>
    /// Read-only WMO membership for the PLAYER rather than the render camera.
    /// The context deliberately carries local coordinates and the exact inverse
    /// placement transform: a WMO root can be placed more than once, so neither
    /// its filename nor world coordinates alone define an interior map.
    /// </summary>
    public sealed record InteriorMinimapContext(
        int InstanceId,
        string InstancePath,
        int GroupIndex,
        string GroupName,
        uint GroupFlags,
        uint RootWmoId,
        uint NameSetId,
        uint GroupWmoId,
        Vector3 LocalPosition,
        Vector3 GroupLocalMin,
        Vector3 GroupLocalMax,
        Matrix4x4 LocalToWorld,
        Matrix4x4 WorldToLocal,
        IReadOnlyList<InteriorMinimapGroup> ReachableGroups);

    /// <summary>
    /// The most specific group containing the camera, or null when outdoors.
    /// Recomputed once per frame; read by the HUD and, later, by traversal.
    /// </summary>
    public CameraCell? CameraGroup { get; private set; }


    /// <summary>
    /// True when the camera is outdoors, or an exterior-flagged group is in the
    /// completed portal flood from its current interior cell. Weather consumes
    /// this as the reference's portal-aware outdoor visibility bit: an inn room
    /// freezes precipitation, while the same storm remains alive through an
    /// open doorway. Missing portal state fails closed only for a positively
    /// identified interior cell.
    /// </summary>
    public bool CameraExteriorPortalVisible
    {
        get
        {
            if (CameraGroup is not { } cameraCell || cameraCell.IsExterior) return true;
            foreach (Instance instance in _instances)
            {
                if (instance.Id != cameraCell.InstanceId ||
                    !_portalVisible.TryGetValue(instance.Id, out HashSet<int>? visible) ||
                    visible is null) continue;
                foreach (GroupMesh group in instance.Model.Groups)
                    if ((group.GroupFlags & 0x08u) != 0 && visible.Contains(group.GroupIndex))
                        return true;
            }
            return false;
        }
    }

    /// <summary>How many groups contained the camera before the tie-break.</summary>
    public int CameraGroupCandidates { get; private set; }

    /// <summary>
    /// Find the group whose real walking-collision surface is directly below
    /// the camera. Group boxes are broad-phase only: they overlap at rooms,
    /// stairs and doorways and cannot authoritatively answer "which room".
    /// Portal crossings can produce two seeds so both sides remain visible at
    /// the transition; a nearer terrain surface wins and means outdoors.
    /// </summary>
    public void UpdateCameraCell(Vector3 cameraWorld, float? terrainWorldZ = null)
    {
        CameraCell? best = null;
        float bestDrop = float.MaxValue;
        int candidates = 0;
        _cameraSeeds.Clear();

        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var eyeLocal = Vector3.Transform(cameraWorld, inv);
            float? terrainLocalZ = terrainWorldZ is float tz
                ? Vector3.Transform(new Vector3(cameraWorld.X, cameraWorld.Y, tz), inv).Z
                : null;

            var seeds = FindCameraSeeds(instance.Model, eyeLocal, terrainLocalZ,
                out float drop, out int columnCandidates);
            candidates += columnCandidates;
            if (seeds.Length == 0) continue;

            _cameraSeeds[instance.Id] = seeds;
            if (drop >= bestDrop) continue;
            var group = instance.Model.Groups.FirstOrDefault(g => g.GroupIndex == seeds[0]);
            if (group is null) continue;

            bestDrop = drop;
            var size = group.LocalMax - group.LocalMin;
            float volume = MathF.Max(size.X, 0f) * MathF.Max(size.Y, 0f) * MathF.Max(size.Z, 0f);
            best = new CameraCell(instance.Path, group.GroupIndex, group.GroupName,
                group.IsInterior, volume, group.PortalCount,
                (group.GroupFlags & 0x08u) != 0, instance.Id);
        }

        CameraGroup = best;
        CameraGroupCandidates = candidates;

        // Cutaway subject: same cell resolution as the camera, once per frame.
        // A pure-EXTERIOR (0x08) seed cell is refused — those boxes are huge
        // ("at the gate" is not "inside") and seeding there would cut a whole
        // city around a toon that is standing outdoors. The flood is computed
        // HERE and cached: it is view-independent, so per-frame recompute in the
        // render loop would be pure waste.
        _cutaway = null;
        string cutawayState = "off";
        if (_cutawaySubject is Vector3 subject)
        {
            cutawayState = "no-cell";
            float bestSubjectDrop = float.MaxValue;
            string seedName = "";
            foreach (var instance in _instances)
            {
                if (!Matrix4x4.Invert(instance.Transform, out var subjInv)) continue;
                var subjLocal = Vector3.Transform(subject, subjInv);
                float? subjTerrainZ = _cutawaySubjectTerrainZ is float stz
                    ? Vector3.Transform(new Vector3(subject.X, subject.Y, stz), subjInv).Z
                    : null;
                var seeds = FindCameraSeeds(instance.Model, subjLocal, subjTerrainZ,
                    out float drop, out _);
                if (seeds.Length == 0 || drop >= bestSubjectDrop) continue;
                var seedGroup = instance.Model.Groups.FirstOrDefault(
                    g => g.GroupIndex == seeds[0]);
                if (seedGroup is null) continue;
                if ((seedGroup.GroupFlags & 0x08u) != 0)
                {
                    cutawayState = $"shell-cell:{seedGroup.GroupName}";
                    continue;
                }
                bestSubjectDrop = drop;
                var groups = ComputeCutawayGroups(instance, seeds);
                _cutaway = (instance.Id, groups);
                seedName = seedGroup.GroupName;
                cutawayState = $"engaged:{System.IO.Path.GetFileName(instance.Path)}" +
                    $"/{seedName} reached={groups.Count}/{instance.Model.Groups.Count}";
            }
        }
        if (cutawayState != _cutawayLoggedState)
        {
            _cutawayLoggedState = cutawayState;
            Console.WriteLine($"[cutaway] {cutawayState}");
        }

        ResolveCutPlane();
        ResolveSightGroups();
    }

    /// <summary>
    /// The cut plane's footprint: the true-interior (0x2000) cell the subject stands in plus every
    /// interior cell reachable from it through portals WITHOUT crossing a non-interior group (so a
    /// Stormwind inn stops at its own door and never floods into the streets). Its world XY bounds,
    /// padded a yard, form the footprint; the cut sits <see cref="_cutPlaneHeight"/> above the feet.
    /// <summary>
    /// Bumped whenever a WMO model becomes resident or a placement appears, so a
    /// consumer caching per-position answers (interior unit light, gameobject
    /// light) knows the world under its feet changed.
    /// </summary>
    public int ResidentVersion { get; private set; }

    /// <summary>Probe height over the feet for the interior-light floor ray; the
    /// same 1.5 yd the roof cut uses, so both agree on the room.</summary>
    private const float InteriorLightProbeHeight = 1.5f;

    /// <summary>
    /// The baked interior light a dynamic M2 - a unit, a mount, a gameobject -
    /// receives standing at <paramref name="feet"/>, or null when it stands
    /// outdoors or in a daylight cell.
    ///
    /// 1.12 never lit interiors at runtime: walls carry MOCV, props carry
    /// MODD.color, and a model that walks in takes the light of the room it is
    /// in. The room's light at a spot is the floor's baked colour under it -
    /// checked offline 2026-09-04 against GoldshireInn: every floor-standing
    /// prop's MODD.color is the MOCV interpolated on the walking face beneath it
    /// (x1.2, the residual being the MOLT lamp pools). Feeding units the outdoor
    /// sky instead is why a toon in Ironforge at night read as moonlit grey while
    /// the props beside her were warm.
    ///
    /// Same cell verdict as the roof cut (FindCameraSeeds, CutPlaneMaxFeetDrop)
    /// and the same group law as BuildDoodadLighting (INTERIOR, not EXTERIOR /
    /// EXTERIOR_LIT), so a unit and the chair beside it cannot disagree about
    /// which light they are in. The answer is MOCV / 255, UNSCALED: consumers
    /// apply the walls' VertexColorScale exactly as the doodad path does.
    /// </summary>
    public Vector3? ResolveInteriorLight(Vector3 feet, float? terrainWorldZ)
    {
        Vector3 probe = feet + new Vector3(0f, 0f, InteriorLightProbeHeight);
        Vector3? best = null;
        float bestDrop = float.MaxValue;
        foreach (var instance in _instances)
        {
            if (probe.X < instance.WorldMin.X - 1f || probe.X > instance.WorldMax.X + 1f ||
                probe.Y < instance.WorldMin.Y - 1f || probe.Y > instance.WorldMax.Y + 1f ||
                probe.Z < instance.WorldMin.Z - 1f || probe.Z > instance.WorldMax.Z + 1f)
                continue;
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var local = Vector3.Transform(probe, inv);
            float? terrainLocalZ = terrainWorldZ is float tz
                ? Vector3.Transform(new Vector3(probe.X, probe.Y, tz), inv).Z
                : null;
            var seeds = FindCameraSeeds(instance.Model, local, terrainLocalZ, out float drop, out _);
            if (seeds.Length == 0)
            {
                // No walking face under the probe at all - an authored floor with
                // no coplanar collision sheet (the Ironforge Commons by the bank).
                // The render floor of an interior group still says which room it
                // is, guarded by the terrain the same way the seed search is.
                if (!TryFloorColorAnyInterior(instance.Model, local, out Vector3 renderColor,
                        out float renderZ)) continue;
                float renderDrop = local.Z - renderZ;
                if (renderDrop > CutPlaneMaxFeetDrop || renderDrop >= bestDrop) continue;
                if (terrainLocalZ is float t && t <= local.Z && t > renderZ) continue;
                bestDrop = renderDrop;
                best = renderColor;
                continue;
            }
            if (drop >= bestDrop) continue;
            // The unit must STAND in the cell: a floor far below the probe is a
            // storey (or a cave) beneath it, not the room it is in.
            if (drop > CutPlaneMaxFeetDrop) continue;
            var group = instance.Model.Groups.FirstOrDefault(g => g.GroupIndex == seeds[0]);
            if (group is null) continue;
            // Daylight cells (BuildDoodadLighting's law): outdoors, or an
            // interior-flagged street/shell that the data says to sun-light.
            if (!group.IsInterior || (group.GroupFlags & 0x48u) != 0) continue;
            if (!TryFloorColor(group, local, out Vector3 color) &&
                !TryFloorColorAnyInterior(instance.Model, local, out color, out _))
                continue;
            bestDrop = drop;
            best = color;
        }
        return best;
    }

    /// <summary>Dev-tool narration of <see cref="ResolveInteriorLight"/>: every instance the
    /// probe fell inside, which cell it seeded, and why that cell did or did not light.</summary>
    public string DescribeInteriorLight(Vector3 feet, float? terrainWorldZ)
    {
        Vector3 probe = feet + new Vector3(0f, 0f, InteriorLightProbeHeight);
        var sb = new System.Text.StringBuilder();
        foreach (var instance in _instances)
        {
            if (probe.X < instance.WorldMin.X - 1f || probe.X > instance.WorldMax.X + 1f ||
                probe.Y < instance.WorldMin.Y - 1f || probe.Y > instance.WorldMax.Y + 1f ||
                probe.Z < instance.WorldMin.Z - 1f || probe.Z > instance.WorldMax.Z + 1f)
                continue;
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var local = Vector3.Transform(probe, inv);
            float? terrainLocalZ = terrainWorldZ is float tz
                ? Vector3.Transform(new Vector3(probe.X, probe.Y, tz), inv).Z : null;
            var seeds = FindCameraSeeds(instance.Model, local, terrainLocalZ, out float drop, out int cands);
            sb.Append($" {System.IO.Path.GetFileName(instance.Path)}: cands={cands} seeds={seeds.Length} drop={drop:0.##}");
            if (seeds.Length == 0)
            {
                sb.Append(TryFloorColorAnyInterior(instance.Model, local, out Vector3 rc, out float rz)
                    ? $" render-floor=({rc.X * 255:0},{rc.Y * 255:0},{rc.Z * 255:0}) z={rz:0.#} probeZ={local.Z:0.#}"
                    : " no-render-floor");
                continue;
            }
            var group = instance.Model.Groups.FirstOrDefault(g => g.GroupIndex == seeds[0]);
            if (group is null) { sb.Append(" (no group)"); continue; }
            sb.Append($" '{group.GroupName}' flags=0x{group.GroupFlags:X} int={group.IsInterior} colors={group.CollisionColors.Length / 3}");
            if (TryFloorColor(group, local, out Vector3 c, out float z))
                sb.Append($" floor=({c.X * 255:0},{c.Y * 255:0},{c.Z * 255:0}) z={z:0.#} probeZ={local.Z:0.#}");
            else if (TryFloorColorAnyInterior(instance.Model, local, out c, out z))
                sb.Append($" any-floor=({c.X * 255:0},{c.Y * 255:0},{c.Z * 255:0}) z={z:0.#}");
            else sb.Append(" no-floor");
        }
        return sb.Length == 0 ? " (no instance contains the probe)" : sb.ToString();
    }

    /// <summary>The highest coloured walking face of <paramref name="group"/> under
    /// the local-space probe, MOCV interpolated at the probe's XY.</summary>
    private static bool TryFloorColor(GroupMesh group, Vector3 probe, out Vector3 color)
        => TryFloorColor(group, probe, out color, out _);

    /// <summary>The walking face first (the cell verdict's own set), else the render
    /// floor: whichever coloured face is highest under the probe.</summary>
    private static bool TryFloorColor(GroupMesh group, Vector3 probe, out Vector3 color, out float floorZ)
    {
        bool walk = TryFloorColor(group.CollisionTriangles, group.CollisionColors, probe,
            out Vector3 walkColor, out float walkZ);
        bool render = TryFloorColor(group.FootstepTriangles, group.FootstepColors, probe,
            out Vector3 renderColor, out float renderZ);
        if (!walk && !render) { color = default; floorZ = float.NegativeInfinity; return false; }
        if (walk && (!render || walkZ >= renderZ)) { color = walkColor; floorZ = walkZ; }
        else { color = renderColor; floorZ = renderZ; }
        return true;
    }

    private static bool TryFloorColor(Vector3[] tris, byte[] rgb, Vector3 probe,
        out Vector3 color, out float floorZ)
    {
        color = default;
        floorZ = float.NegativeInfinity;
        if (tris.Length < 3 || rgb.Length < tris.Length * 3) return false;
        int bestTri = -1;
        float bestU = 0f, bestV = 0f;
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            if (!TriangleBaryAt(tris[i], tris[i + 1], tris[i + 2], probe.X, probe.Y,
                    out float z, out float u, out float v)) continue;
            if (z <= probe.Z && z > floorZ) { floorZ = z; bestTri = i; bestU = u; bestV = v; }
        }
        if (bestTri < 0) return false;
        Vector3 a = ColorAt(rgb, bestTri), b = ColorAt(rgb, bestTri + 1), c = ColorAt(rgb, bestTri + 2);
        color = a + bestU * (b - a) + bestV * (c - a);
        return true;

        static Vector3 ColorAt(byte[] rgb, int vertex) =>
            new(rgb[vertex * 3] / 255f, rgb[vertex * 3 + 1] / 255f, rgb[vertex * 3 + 2] / 255f);
    }

    /// <summary>
    /// Doorway fallback: the seed came through a portal plane rather than this
    /// group's own floor, so take the highest coloured interior floor of ANY
    /// group of the model under the probe. Keeps a unit in a doorway lit by the
    /// room instead of flickering to daylight.
    /// </summary>
    private static bool TryFloorColorAnyInterior(Model model, Vector3 probe, out Vector3 color,
        out float floorZ)
    {
        color = default;
        floorZ = float.NegativeInfinity;
        bool found = false;
        foreach (var group in model.Groups)
        {
            if ((group.CollisionColors.Length == 0 && group.FootstepColors.Length == 0) ||
                (group.GroupFlags & 0x48u) != 0 ||
                probe.X < group.LocalMin.X || probe.X > group.LocalMax.X ||
                probe.Y < group.LocalMin.Y || probe.Y > group.LocalMax.Y ||
                group.LocalMin.Z > probe.Z) continue;
            if (!TryFloorColor(group, probe, out Vector3 c, out float z)) continue;
            if (found && z <= floorZ) continue;
            floorZ = z; color = c; found = true;
        }
        return found;
    }

    private static bool TriangleBaryAt(Vector3 a, Vector3 b, Vector3 c, float x, float y,
        out float z, out float u, out float v)
    {
        float v0x = b.X - a.X, v0y = b.Y - a.Y;
        float v1x = c.X - a.X, v1y = c.Y - a.Y;
        float v2x = x - a.X, v2y = y - a.Y;
        float den = v0x * v1y - v1x * v0y;
        if (MathF.Abs(den) < 1.0e-7f) { z = 0f; u = 0f; v = 0f; return false; }
        u = (v2x * v1y - v1x * v2y) / den;
        v = (v0x * v2y - v2x * v0y) / den;
        if (u < -1.0e-5f || v < -1.0e-5f || u + v > 1.00001f) { z = 0f; return false; }
        z = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
        return true;
    }

    /// A subject standing outdoors, or in a shell/street cell, gets no cut at all.
    /// </summary>
    private void ResolveCutPlane()
    {
        ActiveCut = null;
        _cutPlaneGroups = null;
        _cutPlaneMeshes.Clear();
        string state = "off";
        if (_cutPlaneSubject is Vector3 feet)
        {
            state = "no-interior-cell";
            Vector3 probe = feet + new Vector3(0f, 0f, 1.5f);
            float bestDrop = float.MaxValue;
            foreach (var instance in _instances)
            {
                if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
                var local = Vector3.Transform(probe, inv);
                // Terrain under the feet, in the instance's space, so the seed search keeps its outside
                // verdict. Without it a unit standing on the hill OVER the Deadmines dropped straight
                // through the ground into cave1_19 and the cut sliced the hillside off, showing the
                // world behind it (owner, 2026-09-02). The other two seed callers already pass this.
                float? terrainLocalZ = _cutPlaneTerrainZ is float tz
                    ? Vector3.Transform(new Vector3(probe.X, probe.Y, tz), inv).Z
                    : null;
                var seeds = FindCameraSeeds(instance.Model, local, terrainLocalZ, out float drop, out _);
                if (seeds.Length == 0 || drop >= bestDrop) continue;
                // The subject must STAND in the cell: a drop far beyond the probe height means the
                // floor found is a storey (or a cave) below its feet, not the one it is on.
                if (drop > CutPlaneMaxFeetDrop) { state = $"floor-too-far:{drop:0.#}"; continue; }
                var seedGroup = instance.Model.Groups.FirstOrDefault(g => g.GroupIndex == seeds[0]);
                // A true interior only: not a shell, and not an exterior-lit street cell (0x40) -
                // Stormwind's streets are flagged interior too, and seeding or flooding through
                // them turned the AH's cut into a slice across the whole city (capture 2026-09-01).
                if (seedGroup is null || (seedGroup.GroupFlags & 0x2000u) == 0 ||
                    (seedGroup.GroupFlags & 0x40u) != 0)
                {
                    state = $"not-interior:{seedGroup?.GroupName ?? "?"}";
                    continue;
                }
                bestDrop = drop;

                var byFile = new Dictionary<int, GroupMesh>(instance.Model.Groups.Count);
                foreach (var g in instance.Model.Groups) byFile[g.GroupIndex] = g;
                var reached = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(seedGroup.GroupIndex);
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                while (stack.Count > 0)
                {
                    int gi = stack.Pop();
                    if (reached.Contains(gi) || !byFile.TryGetValue(gi, out var gm)) continue;
                    var (gMin, gMax) = TransformedBounds(gm, instance.Transform);
                    // Only cells near the subject open up: a mine's far galleries and an inn's
                    // distant wings keep their roofs, so the hole in the world stays local.
                    if (gi != seedGroup.GroupIndex && DistanceToBox(feet, gMin, gMax) > CutPlaneReachYards)
                        continue;
                    reached.Add(gi);
                    _cutPlaneMeshes.Add(gm);
                    min = Vector3.Min(min, gMin);
                    max = Vector3.Max(max, gMax);
                    int start = gm.PortalStart;
                    int end = Math.Min(gm.PortalStart + gm.PortalCount, instance.Model.PortalRefs.Count);
                    for (int i = Math.Max(0, start); i < end; i++)
                    {
                        int nb = instance.Model.PortalRefs[i].GroupIndex;
                        if (nb == 0xFFFF || reached.Contains(nb)) continue;
                        if (byFile.TryGetValue(nb, out var nbGroup) &&
                            (nbGroup.GroupFlags & 0x2000u) != 0 && (nbGroup.GroupFlags & 0x40u) == 0)
                            stack.Push(nb);
                    }
                }
                const float pad = 1f;
                // Hard cap on the footprint as well, whatever the cell bounds say.
                min = Vector3.Max(min, feet - new Vector3(CutPlaneFootprintCapYards));
                max = Vector3.Min(max, feet + new Vector3(CutPlaneFootprintCapYards));
                ActiveCut = new WorldCut(
                    new Vector2(min.X - pad, min.Y - pad), new Vector2(max.X + pad, max.Y + pad),
                    feet.Z + _cutPlaneHeight);
                _cutPlaneGroups = (instance.Id, reached);
                state = $"engaged:{System.IO.Path.GetFileName(instance.Path)}/{seedGroup.GroupName} " +
                    $"cells={reached.Count} cutZ={feet.Z + _cutPlaneHeight:0.#} " +
                    $"footprint=({min.X:0},{min.Y:0})-({max.X:0},{max.Y:0})";
            }

            // PROXIMITY fallback (owner, 2026-09-02: "character is on floor, so treat the
            // immediate 10 yards around it as cut ceilings ... any time characters are near
            // buildings/caves - so on true open ground, a hill wouldn't react like that"). No
            // interior cell under the feet (a cave mouth, a porch, a doorway) but a building
            // within reach: cut everything above the head in a square around the unit. Terrain
            // included, through the same uniforms. Nothing near = nothing cut.
            if (ActiveCut is null)
            {
                string? nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var instance in _instances)
                {
                    float d = DistanceToBox(feet, instance.WorldMin, instance.WorldMax);
                    if (d <= CutPlaneProximityYards && d < nearestDistance)
                    {
                        nearestDistance = d;
                        nearest = System.IO.Path.GetFileName(instance.Path);
                    }
                }
                if (nearest is not null)
                {
                    ActiveCut = new WorldCut(
                        new Vector2(feet.X - CutPlaneProximityRadius, feet.Y - CutPlaneProximityRadius),
                        new Vector2(feet.X + CutPlaneProximityRadius, feet.Y + CutPlaneProximityRadius),
                        feet.Z + _cutPlaneHeight);
                    state = $"proximity:{nearest} dist={nearestDistance:0.#} radius={CutPlaneProximityRadius:0} " +
                        $"cutZ={feet.Z + _cutPlaneHeight:0.#}";
                }
            }
        }
        if (state != _cutPlaneLoggedState)
        {
            _cutPlaneLoggedState = state;
            Console.WriteLine($"[cutplane] {state}");
        }
    }

    /// <summary>A building's bounds this close to the unit's feet engages the proximity cut.</summary>
    private const float CutPlaneProximityYards = 12f;

    /// <summary>Half-extent of the proximity cut's square footprint around the unit.</summary>
    private const float CutPlaneProximityRadius = 15f;

    /// <summary>
    /// Resolve the interior WMO cell beneath an arbitrary world position without
    /// changing <see cref="CameraGroup"/>, portal-render seeds, or cutaway state.
    /// This is the minimap/player-position counterpart to
    /// <see cref="UpdateCameraCell"/>: both use the same retained collision faces,
    /// portal-boundary tie handling, nearest-floor election, and terrain-wins
    /// rule, but this query is independent of camera orbit and view frustum.
    /// </summary>
    public InteriorMinimapContext? ResolveInteriorMinimapContext(
        Vector3 world, float radius, float? terrainWorldZ = null)
    {
        if (!float.IsFinite(radius) || radius <= 0f) return null;

        Instance? bestInstance = null;
        GroupMesh? bestGroup = null;
        Matrix4x4 bestWorldToLocal = default;
        Vector3 bestLocal = default;
        float bestDrop = float.MaxValue;

        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var worldToLocal)) continue;
            var local = Vector3.Transform(world, worldToLocal);
            float? terrainLocalZ = terrainWorldZ is float terrainZ
                ? Vector3.Transform(new Vector3(world.X, world.Y, terrainZ), worldToLocal).Z
                : null;

            var seeds = FindCameraSeeds(instance.Model, local, terrainLocalZ,
                out float drop, out _);
            if (seeds.Length == 0 || drop >= bestDrop) continue;
            var group = instance.Model.Groups.FirstOrDefault(g => g.GroupIndex == seeds[0]);
            if (group is null) continue;

            bestDrop = drop;
            bestInstance = instance;
            bestGroup = group;
            bestWorldToLocal = worldToLocal;
            bestLocal = local;
        }

        if (bestInstance is null || bestGroup is null) return null;

        var groups = SelectInteriorMinimapGroups(bestInstance, bestGroup.GroupIndex, world, radius);

        return new InteriorMinimapContext(
            bestInstance.Id,
            bestInstance.Path,
            bestGroup.GroupIndex,
            bestGroup.GroupName,
            bestGroup.GroupFlags,
            bestInstance.Model.WmoId,
            bestInstance.NameSetId,
            bestGroup.GroupWmoId,
            bestLocal,
            bestGroup.AuthoredLocalMin,
            bestGroup.AuthoredLocalMax,
            bestInstance.Transform,
            bestWorldToLocal,
            groups);
    }

    /// <summary>
    /// Resolve the archived CurrentAreaInterior claim without changing render,
    /// portal, or cutaway state. Unlike the display context above, this casts
    /// from feet + 0.1 yd through walking faces only: no portal crossing and no
    /// reachable-group traversal. The nearest face across all placements wins;
    /// strictly nearer terrain and an EXTERIOR (0x08) winning group mean outdoors.
    /// </summary>
    public AreaMinimapIdentity? ResolveAreaMinimapIdentity(
        Vector3 feetWorld, float? terrainWorldZ = null)
    {
        if (!float.IsFinite(feetWorld.X) || !float.IsFinite(feetWorld.Y) ||
            !float.IsFinite(feetWorld.Z)) return null;

        Vector3 probeWorld = feetWorld + new Vector3(0f, 0f, 0.1f);
        Instance? bestInstance = null;
        GroupMesh? bestGroup = null;
        float bestDrop = float.MaxValue;

        foreach (var instance in _instances)
        {
            if (instance.Model.WmoId == 0 ||
                !AreaProbeInsideBounds(probeWorld, instance) ||
                !Matrix4x4.Invert(instance.Transform, out var worldToLocal)) continue;
            Vector3 probeLocal = Vector3.Transform(probeWorld, worldToLocal);
            float? terrainLocalZ = terrainWorldZ is float terrainZ
                ? Vector3.Transform(
                    new Vector3(probeWorld.X, probeWorld.Y, terrainZ), worldToLocal).Z
                : null;
            GroupMesh? group = FindAreaFace(
                instance.Model, probeLocal, terrainLocalZ, out float drop);
            if (group is null || drop >= bestDrop) continue;
            bestDrop = drop;
            bestInstance = instance;
            bestGroup = group;
        }

        if (bestInstance is null || bestGroup is null ||
            (bestGroup.GroupFlags & 0x08u) != 0) return null;
        return new AreaMinimapIdentity(
            bestInstance.Model.WmoId, bestInstance.NameSetId, bestGroup.GroupWmoId);
    }

    /// <summary>
    /// Broad phase for the area/footstep probes: a placement whose world AABB the
    /// probe is not even inside cannot own a walking face under it. Every caller
    /// used to Invert the transform and triangle-scan EVERY placed WMO per probe —
    /// per minimap blip, per footstep, per soundscape tick — which is most of what
    /// made interiors (many instances, many groups) feel heavy. A face must be at
    /// or below the probe, so a probe under the whole model (below WorldMin.Z) or
    /// far above it has nothing to find either.
    /// </summary>
    private static bool AreaProbeInsideBounds(Vector3 probeWorld, Instance instance)
    {
        const float margin = 1.5f;
        const float aboveRoofSlack = 5f;
        return probeWorld.X >= instance.WorldMin.X - margin &&
               probeWorld.X <= instance.WorldMax.X + margin &&
               probeWorld.Y >= instance.WorldMin.Y - margin &&
               probeWorld.Y <= instance.WorldMax.Y + margin &&
               probeWorld.Z >= instance.WorldMin.Z - margin &&
               probeWorld.Z <= instance.WorldMax.Z + aboveRoofSlack;
    }

    /// <summary>
    /// Resolve the WMO half of the archived footstep-surface law.  A true return
    /// means an interior WMO owns the column; <paramref name="terrainType"/> is
    /// zero when its accepted render surface has no usable MOMT material, which
    /// is silent and must never fall through to the ADT underneath.
    /// </summary>
    public bool TrySampleFootstepTerrain(
        Vector3 feetWorld, float? terrainWorldZ, out uint terrainType)
        => TrySampleFootstepTerrain(
            feetWorld, terrainWorldZ, out terrainType, out _, out _);

    /// <summary>
    /// Footstep surface ownership plus the placed-WMO identity needed to scope
    /// retained MLIQ queries. An exterior group deliberately returns false.
    /// </summary>
    public bool TrySampleFootstepTerrain(
        Vector3 feetWorld, float? terrainWorldZ, out uint terrainType,
        out int liquidInstanceId)
        => TrySampleFootstepTerrain(
            feetWorld, terrainWorldZ, out terrainType, out liquidInstanceId, out _);

    public bool TrySampleFootstepTerrain(
        Vector3 feetWorld, float? terrainWorldZ, out uint terrainType,
        out int liquidInstanceId, out int liquidGroupIndex)
    {
        terrainType = 0;
        liquidInstanceId = 0;
        liquidGroupIndex = -1;
        if (!float.IsFinite(feetWorld.X) || !float.IsFinite(feetWorld.Y) ||
            !float.IsFinite(feetWorld.Z)) return false;

        Vector3 probeWorld = feetWorld + new Vector3(0f, 0f, 0.1f);
        GroupMesh? bestGroup = null;
        Instance? bestInstance = null;
        Vector3 bestProbe = default;
        float bestDrop = float.MaxValue;

        foreach (var instance in _instances)
        {
            if (!AreaProbeInsideBounds(probeWorld, instance) ||
                !Matrix4x4.Invert(instance.Transform, out var worldToLocal)) continue;
            Vector3 probeLocal = Vector3.Transform(probeWorld, worldToLocal);
            float? terrainLocalZ = terrainWorldZ is float terrainZ
                ? Vector3.Transform(
                    new Vector3(probeWorld.X, probeWorld.Y, terrainZ), worldToLocal).Z
                : null;
            GroupMesh? group = FindAreaFace(
                instance.Model, probeLocal, terrainLocalZ, out float drop);
            if (group is null || drop >= bestDrop) continue;
            bestDrop = drop;
            bestGroup = group;
            bestInstance = instance;
            bestProbe = probeLocal;
        }

        // EXTERIOR is the outdoor leg: allow the caller to ask the ADT.  An
        // interior-lit/exterior-like group (0x40) owns the column but has no
        // footprint material set, matching the reference's silent result.
        if (bestInstance is null || bestGroup is null ||
            (bestGroup.GroupFlags & 0x08u) != 0) return false;
        liquidInstanceId = bestInstance.Id;
        liquidGroupIndex = bestGroup.GroupIndex;

        float bestZ = float.NegativeInfinity;
        Vector3[] triangles = bestGroup.FootstepTriangles;
        uint[] types = bestGroup.FootstepTerrainTypes;
        for (int face = 0, i = 0; i + 2 < triangles.Length && face < types.Length;
             face++, i += 3)
        {
            if (!TriangleZAt(triangles[i], triangles[i + 1], triangles[i + 2],
                    bestProbe.X, bestProbe.Y, out float z) ||
                z > bestProbe.Z || z <= bestZ) continue;
            bestZ = z;
            terrainType = types[face];
        }
        return true;
    }

    /// <summary>
    /// Resolve the owning room's MOGP whole-group liquid override. This precedes
    /// MLIQ sampling and represents a submerged room with no surface grid.
    /// </summary>
    public bool TryGetGroupLiquidOverride(
        int instanceId, int groupIndex, out byte shaderType)
    {
        shaderType = 0;
        Instance? instance = _instances.FirstOrDefault(i => i.Id == instanceId);
        GroupMesh? group = instance?.Model.Groups.FirstOrDefault(
            g => g.GroupIndex == groupIndex);
        return group is not null &&
               WmoLiquidPointLaw.TryMapGroupOverride(group.GroupLiquid, out shaderType);
    }

    /// <summary>
    /// Archived interior-minimap selection law. Traversal starts at exactly the
    /// player's cell. A group must overlap the player-centred query in world XY
    /// both to emit and to recurse; EXTERIOR is a hard stop. A doorway can be
    /// followed only when its real transformed polygon intersects all six query
    /// half-spaces. Thus a connected city never expands into an unbounded map.
    /// </summary>
    private static List<InteriorMinimapGroup> SelectInteriorMinimapGroups(
        Instance instance, int seedGroupIndex, Vector3 playerWorld, float radius)
    {
        var model = instance.Model;
        var byFile = new Dictionary<int, GroupMesh>(model.Groups.Count);
        foreach (var group in model.Groups) byFile[group.GroupIndex] = group;

        var result = new List<InteriorMinimapGroup>();
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(seedGroupIndex);

        float xyRadius = 2f * radius;
        var queryMin = new Vector3(
            playerWorld.X - xyRadius,
            playerWorld.Y - xyRadius,
            playerWorld.Z - 1.5f * radius);
        var queryMax = new Vector3(
            playerWorld.X + xyRadius,
            playerWorld.Y + xyRadius,
            playerWorld.Z + radius);

        while (stack.Count > 0)
        {
            int groupIndex = stack.Pop();
            if (!visited.Add(groupIndex) || !byFile.TryGetValue(groupIndex, out var group)) continue;

            // 0x08 EXTERIOR stops recursion. A 0x80 no-emit group may still
            // connect rooms, but is not itself returned below.
            if ((group.GroupFlags & 0x08u) != 0) continue;

            var (worldMin, worldMax) = TransformedBounds(
                group.AuthoredLocalMin, group.AuthoredLocalMax, instance.Transform);
            if (worldMax.X < queryMin.X || worldMin.X > queryMax.X ||
                worldMax.Y < queryMin.Y || worldMin.Y > queryMax.Y)
                continue;

            if ((group.GroupFlags & (0x08u | 0x80u)) == 0)
            {
                result.Add(new InteriorMinimapGroup(
                    group.GroupIndex, group.GroupName, group.GroupFlags, group.GroupWmoId,
                    group.AuthoredLocalMin, group.AuthoredLocalMax));
            }

            int start = Math.Max(0, group.PortalStart);
            int end = Math.Min(group.PortalStart + group.PortalCount, model.PortalRefs.Count);
            for (int i = start; i < end; i++)
            {
                var reference = model.PortalRefs[i];
                int neighbour = reference.GroupIndex;
                if (neighbour == ushort.MaxValue || visited.Contains(neighbour) ||
                    reference.PortalIndex >= model.Portals.Count) continue;
                if (!PortalIntersectsQuery(model, model.Portals[reference.PortalIndex],
                        instance.Transform, queryMin, queryMax)) continue;
                stack.Push(neighbour);
            }
        }

        result.Sort(static (left, right) => left.GroupIndex.CompareTo(right.GroupIndex));
        return result;
    }

    /// <summary>
    /// Conservative polygon/AABB plane test: reject only when every transformed
    /// portal vertex lies outside the same one of the query's six planes.
    /// </summary>
    private static bool PortalIntersectsQuery(
        Model model, WmoPortal portal, Matrix4x4 localToWorld,
        Vector3 queryMin, Vector3 queryMax)
    {
        int start = portal.StartVertex;
        int count = portal.VertexCount;
        if (count < 3 || start < 0 || start + count > model.PortalVertices.Count) return false;

        bool allLeft = true, allRight = true;
        bool allBack = true, allFront = true;
        bool allBelow = true, allAbove = true;
        for (int i = 0; i < count; i++)
        {
            var raw = model.PortalVertices[start + i];
            var world = Vector3.Transform(new Vector3(raw.x, raw.y, raw.z), localToWorld);
            allLeft &= world.X < queryMin.X;
            allRight &= world.X > queryMax.X;
            allBack &= world.Y < queryMin.Y;
            allFront &= world.Y > queryMax.Y;
            allBelow &= world.Z < queryMin.Z;
            allAbove &= world.Z > queryMax.Z;
        }
        return !(allLeft || allRight || allBack || allFront || allBelow || allAbove);
    }

    private const float CameraGroupMaxDrop = 1760f;
    private const float AreaGroupMaxDrop = 1000f;
    private const float PortalNearParallel = 1.0e-4f;
    private const float PortalPlaneSnap = 0.1f;
    private const float PortalNearestTieEps = 1.0e-4f;

    private static GroupMesh? FindAreaFace(
        Model model, Vector3 probe, float? terrainZ, out float drop)
    {
        float bestZ = float.NegativeInfinity;
        GroupMesh? best = null;
        foreach (var group in model.Groups)
        {
            // The archived down-ray is column-local. Reject groups whose
            // authored bounds cannot contain the probe before touching their
            // collision faces; city roots can otherwise turn this 10 Hz area
            // query into a full-WMO triangle scan.
            if (group.IsDistanceLod ||
                probe.X < group.LocalMin.X || probe.X > group.LocalMax.X ||
                probe.Y < group.LocalMin.Y || probe.Y > group.LocalMax.Y ||
                group.LocalMin.Z > probe.Z ||
                // A group whose TOP sits below the best floor found so far cannot
                // hold a higher floor — skip its whole triangle list. Cuts the
                // lower storeys and basements out of every probe in a tall root.
                group.LocalMax.Z <= bestZ)
                continue;
            var tris = group.CollisionTriangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                if (!TriangleZAt(tris[i], tris[i + 1], tris[i + 2],
                        probe.X, probe.Y, out float z) || z > probe.Z || z <= bestZ)
                    continue;
                bestZ = z;
                best = group;
            }
        }

        drop = probe.Z - bestZ;
        if (best is null || drop > AreaGroupMaxDrop ||
            terrainZ is float terrain && terrain <= probe.Z && terrain > bestZ)
        {
            drop = float.MaxValue;
            return null;
        }
        return best;
    }

    private static int[] FindCameraSeeds(Model model, Vector3 eye, float? terrainZ,
        out float drop, out int candidates)
    {
        drop = float.MaxValue;
        candidates = 0;
        float bestZ = float.NegativeInfinity;
        GroupMesh? best = null;

        foreach (var group in model.Groups)
        {
            if (group.IsDistanceLod ||
                eye.X < group.LocalMin.X || eye.X > group.LocalMax.X ||
                eye.Y < group.LocalMin.Y || eye.Y > group.LocalMax.Y ||
                group.LocalMin.Z > eye.Z) continue;

            candidates++;
            var tris = group.CollisionTriangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                if (!TriangleZAt(tris[i], tris[i + 1], tris[i + 2], eye.X, eye.Y, out float z)) continue;
                if (z <= eye.Z && z > bestZ) { bestZ = z; best = group; }
            }
        }

        int? across = null;
        foreach (var group in model.Groups)
        {
            if (group.IsDistanceLod ||
                eye.X < group.LocalMin.X || eye.X > group.LocalMax.X ||
                eye.Y < group.LocalMin.Y || eye.Y > group.LocalMax.Y ||
                group.LocalMin.Z > eye.Z) continue;

            int end = Math.Min(group.PortalStart + group.PortalCount, model.PortalRefs.Count);
            for (int ri = Math.Max(0, group.PortalStart); ri < end; ri++)
            {
                var reference = model.PortalRefs[ri];
                if (reference.GroupIndex == ushort.MaxValue ||
                    reference.PortalIndex >= model.Portals.Count) continue;
                var portal = model.Portals[reference.PortalIndex];
                float nz = portal.NormalZ;
                float signed = portal.NormalX * eye.X + portal.NormalY * eye.Y + nz * eye.Z + portal.PlaneDistance;
                float z;
                if (MathF.Abs(nz) < PortalNearParallel)
                {
                    if (MathF.Abs(signed) > PortalPlaneSnap) continue;
                    z = eye.Z;
                }
                else
                {
                    z = -(portal.NormalX * eye.X + portal.NormalY * eye.Y + portal.PlaneDistance) / nz;
                    if (z > eye.Z) continue;
                }
                if (z < bestZ - PortalNearestTieEps ||
                    !PointInPortal(model, portal, new Vector3(eye.X, eye.Y, z))) continue;

                int neighbour = reference.GroupIndex;
                int chosen = ((signed >= 0f) == (reference.Side > 0)) ? group.GroupIndex : neighbour;
                var chosenGroup = model.Groups.FirstOrDefault(g => g.GroupIndex == chosen);
                if (chosenGroup is null) continue;
                int other = chosen == group.GroupIndex ? neighbour : group.GroupIndex;
                bestZ = z;
                best = chosenGroup;
                across = model.Groups.Any(g => g.GroupIndex == other) && other != chosen ? other : null;
            }
        }

        if (best is null)
        {
            // Archived decision 0692: when both the faithful walking-face and
            // portal legs miss, retry over only the faces the camera collides
            // with but walking drops. Any terrain surface on the down segment
            // preserves the ordinary outside verdict (doorsteps/open ground).
            // Terrain above the eye is not on that segment and does not gate
            // the fallback, which is the Deadmines DETAIL-floor case.
            if (terrainZ is float fallbackTerrain && fallbackTerrain <= eye.Z)
                return [];

            float fallbackZ = float.NegativeInfinity;
            GroupMesh? fallback = null;
            foreach (var group in model.Groups)
            {
                if (group.IsDistanceLod ||
                    eye.X < group.LocalMin.X || eye.X > group.LocalMax.X ||
                    eye.Y < group.LocalMin.Y || eye.Y > group.LocalMax.Y ||
                    group.LocalMin.Z > eye.Z) continue;

                var tris = group.CameraOnlyTriangles;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    if (!TriangleZAt(tris[i], tris[i + 1], tris[i + 2],
                            eye.X, eye.Y, out float z)) continue;
                    if (z <= eye.Z && z > fallbackZ)
                    {
                        fallbackZ = z;
                        fallback = group;
                    }
                }
            }

            if (fallback is null || eye.Z - fallbackZ > CameraGroupMaxDrop ||
                (fallback.GroupFlags & 0x08u) != 0)
                return [];
            drop = eye.Z - fallbackZ;
            return [fallback.GroupIndex];
        }

        if (eye.Z - bestZ > CameraGroupMaxDrop || (best.GroupFlags & 0x08u) != 0)
            return [];
        if (terrainZ is float tz && tz <= eye.Z && tz > bestZ) return [];

        drop = eye.Z - bestZ;
        return across is int otherGroup ? [best.GroupIndex, otherGroup] : [best.GroupIndex];
    }

    private static bool TriangleZAt(Vector3 a, Vector3 b, Vector3 c, float x, float y, out float z)
    {
        float v0x = b.X - a.X, v0y = b.Y - a.Y;
        float v1x = c.X - a.X, v1y = c.Y - a.Y;
        float v2x = x - a.X, v2y = y - a.Y;
        float den = v0x * v1y - v1x * v0y;
        if (MathF.Abs(den) < 1.0e-7f) { z = 0f; return false; }
        float u = (v2x * v1y - v1x * v2y) / den;
        float v = (v0x * v2y - v2x * v0y) / den;
        if (u < -1.0e-5f || v < -1.0e-5f || u + v > 1.00001f) { z = 0f; return false; }
        z = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
        return true;
    }

    private static bool PointInPortal(Model model, WmoPortal portal, Vector3 point)
    {
        int start = portal.StartVertex, count = portal.VertexCount;
        if (count < 3 || start < 0 || start + count > model.PortalVertices.Count) return false;
        var normal = new Vector3(portal.NormalX, portal.NormalY, portal.NormalZ);
        int dropAxis = MathF.Abs(normal.X) >= MathF.Abs(normal.Y)
            ? (MathF.Abs(normal.X) >= MathF.Abs(normal.Z) ? 0 : 2)
            : (MathF.Abs(normal.Y) >= MathF.Abs(normal.Z) ? 1 : 2);
        static (float U, float V) Project(Vector3 p, int axis) => axis switch
        {
            0 => (p.Y, p.Z),
            1 => (p.X, p.Z),
            _ => (p.X, p.Y),
        };
        var q = Project(point, dropAxis);
        bool inside = false;
        var prevRaw = model.PortalVertices[start + count - 1];
        var prev = Project(new Vector3(prevRaw.x, prevRaw.y, prevRaw.z), dropAxis);
        for (int i = 0; i < count; i++)
        {
            var raw = model.PortalVertices[start + i];
            var cur = Project(new Vector3(raw.x, raw.y, raw.z), dropAxis);
            if ((cur.V > q.V) != (prev.V > q.V) &&
                q.U < (prev.U - cur.U) * (q.V - cur.V) / (prev.V - cur.V) + cur.U)
                inside = !inside;
            prev = cur;
        }
        return inside;
    }

    // ════════════════════════════════════════════════════════════════════════
    // PLAN_10 D2 — portal traversal (benilla wmo_portal/mod.rs).
    //
    // Flood the portal graph from the camera's cell, clipping the view frustum at
    // each doorway. A group is reachable if some chain of on-screen doorways leads
    // to it. Interiors not reached are culled - which is what hides the roof from
    // inside. Runs only for the one instance the camera is inside, only when the
    // toggle is on; everything else keeps the existing heuristic (D6).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Interior groups the last portal flood reached (diagnostic).</summary>
    public int PortalReachedLastFrame { get; private set; }

    private const int PortalDepthCap = 64;       // benilla DEPTH_CAP
    private const int PortalMaxIters = 1 << 16;  // benilla MAX_ITERS
    private const float PortalRectEps = 0.001f;  // benilla RECT_EPS
    private const float PortalWClampBand = 0.001f;
    private const float PortalWClampSub = 1.0e-5f;

    /// <summary>
    /// View-independent portal flood for the SUI free-view cutaway: every group
    /// reachable from the subject's cell WITHOUT ever entering a pure-EXTERIOR
    /// (0x08) group. No screen-rect clipping and no front-side tests — the
    /// subject is a person in a room, not an eye with a frustum — so the result
    /// is stable however the sky camera moves. The building's shell and roof are
    /// 0x08, stay unreached, and cull via the authoritative ReachableGroups gate.
    /// </summary>
    private static HashSet<int> ComputeCutawayGroups(Instance instance, int[] seeds)
    {
        var model = instance.Model;
        var byFile = new Dictionary<int, GroupMesh>(model.Groups.Count);
        foreach (var g in model.Groups) byFile[g.GroupIndex] = g;
        var reachable = new HashSet<int>();
        var stack = new Stack<int>();
        foreach (int s in seeds)
            if (byFile.ContainsKey(s)) stack.Push(s);
        while (stack.Count > 0)
        {
            int g = stack.Pop();
            if (!reachable.Add(g)) continue;
            if (!byFile.TryGetValue(g, out var gm)) continue;
            int start = gm.PortalStart;
            int end = Math.Min(gm.PortalStart + gm.PortalCount, model.PortalRefs.Count);
            for (int i = start; i < end; i++)
            {
                if (i < 0) break;
                int nb = model.PortalRefs[i].GroupIndex;
                if (nb == 0xFFFF || reachable.Contains(nb)) continue;
                if (byFile.TryGetValue(nb, out var nbGroup) &&
                    (nbGroup.GroupFlags & 0x08u) != 0)
                    continue;   // never through the shell — that IS the cut
                stack.Push(nb);
            }
        }
        return reachable;
    }

    /// <summary>
    /// The set of group FILE indices reachable from <paramref name="seedGroupIndex"/>
    /// through portals whose doorway stays inside the running screen rect. Depth-
    /// and iteration-capped; the shrinking rect kills cycles even without a visited
    /// set (benilla relies on the same three guards).
    /// </summary>
    private HashSet<int> ComputeReachableGroups(
        Instance instance, IReadOnlyList<int> seedGroupIndices, Vector3 cameraPosition, Matrix4x4 relVp)
    {
        var reachable = new HashSet<int>();
        var model = instance.Model;

        // Model.Groups is NOT file-index-aligned (empty / antiportal groups are
        // dropped at load); MOPR refs and CameraGroup speak file indices.
        var byFile = new Dictionary<int, GroupMesh>(model.Groups.Count);
        foreach (var g in model.Groups) byFile[g.GroupIndex] = g;

        if (!Matrix4x4.Invert(instance.Transform, out var inv)) return reachable;
        var eyeLocal = Vector3.Transform(cameraPosition, inv);

        // (group, came-from, rect x0,y0,x1,y1, depth). Full screen = [-1,-1,1,1].
        // Multiple seeds (benilla mod.rs:355-372): one when the camera is inside a
        // cell, or every EXTERIOR group full-screen when the camera is outside.
        var stack = new Stack<(int g, int came, float x0, float y0, float x1, float y1, int depth)>();
        foreach (int seed in seedGroupIndices)
            if (byFile.ContainsKey(seed))
                stack.Push((seed, -1, -1f, -1f, 1f, 1f, 0));
        if (stack.Count == 0) return reachable;   // nothing seedable -> D6 fallback

        int iters = 0;
        while (stack.Count > 0)
        {
            var (g, came, rx0, ry0, rx1, ry1, depth) = stack.Pop();
            if (++iters > PortalMaxIters) break;
            reachable.Add(g);
            if (depth >= PortalDepthCap) continue;
            if (!byFile.TryGetValue(g, out var gm)) continue;   // no mesh -> dead end

            int start = gm.PortalStart;
            int end = Math.Min(gm.PortalStart + gm.PortalCount, model.PortalRefs.Count);
            for (int i = start; i < end; i++)
            {
                if (i < 0) break;
                var r = model.PortalRefs[i];
                int nb = r.GroupIndex;
                if (nb == 0xFFFF || nb == came) continue;              // sentinel / entry portal
                if (r.PortalIndex < 0 || r.PortalIndex >= model.Portals.Count) continue;
                var p = model.Portals[r.PortalIndex];

                // D3 front-side test: signed distance of the eye (LOCAL space) to
                // the portal plane, oriented by Side. Enter only from the front.
                float d = p.NormalX * eyeLocal.X + p.NormalY * eyeLocal.Y
                          + p.NormalZ * eyeLocal.Z + p.PlaneDistance;
                if (r.Side < 0) d = -d;
                if (d < 0f) continue;

                float rawPlaneDistance = p.NormalX * eyeLocal.X + p.NormalY * eyeLocal.Y
                    + p.NormalZ * eyeLocal.Z + p.PlaneDistance;
                bool eyeOnPortal = MathF.Abs(rawPlaneDistance) <= 0.01f && PointInPortal(model, p, eyeLocal);
                float px0, py0, px1, py1;
                if (eyeOnPortal)
                {
                    // WoW's doorway crossing special case: projection is
                    // singular while the eye intersects the portal plane, so
                    // use the full screen for this edge. Without this the room
                    // ahead collapses out of the PVS for exactly one frame.
                    px0 = py0 = -1f;
                    px1 = py1 = 1f;
                }
                else if (!PortalScreenRect(model, p, instance.Transform, cameraPosition, relVp,
                             out px0, out py0, out px1, out py1))
                {
                    continue;   // doorway fully off-screen / behind
                }

                float ix0 = MathF.Max(rx0, px0), iy0 = MathF.Max(ry0, py0);
                float ix1 = MathF.Min(rx1, px1), iy1 = MathF.Min(ry1, py1);
                if (ix1 - ix0 < PortalRectEps || iy1 - iy0 < PortalRectEps) continue;  // collapsed

                stack.Push((nb, g, ix0, iy0, ix1, iy1, depth + 1));
            }
        }

        // Deferred exterior (benilla wmo_portal/mod.rs:453-459): if the flood
        // reached ANY pure-EXTERIOR (0x08) group, the whole exterior shell is
        // visible - you can see the sky/skyline through that doorway, so every
        // outer-wall/tower/roof group draws. If it reached NONE (deep inside, no
        // line to the outdoors) the exterior groups stay unreached and get culled -
        // which is exactly how the trade-district roof and outer shell disappear
        // once you are inside. (Outdoors this is a no-op: the seed already pushed
        // every 0x08 group, so one is always reached.)
        bool reachedExterior = false;
        foreach (int gi in reachable)
            if (byFile.TryGetValue(gi, out var xg) && (xg.GroupFlags & 0x08u) != 0) { reachedExterior = true; break; }
        if (reachedExterior)
            foreach (var g in model.Groups)
                if ((g.GroupFlags & 0x08u) != 0) reachable.Add(g.GroupIndex);

        return reachable;
    }

    /// <summary>
    /// Project a portal polygon to an NDC bounding rect, Sutherland-Hodgman-clipped
    /// against the four side planes of the frustum (no near plane, so a straddling
    /// doorway opens wide instead of collapsing - benilla's w-clamp does the same).
    /// Camera-relative + RelativeViewProjection, matching Camera.BoxInFrustum's
    /// row-vector convention. Returns false when fewer than three vertices survive.
    /// </summary>
    private static bool PortalScreenRect(
        Model model, WmoPortal p, Matrix4x4 instanceTransform, Vector3 cameraPosition, Matrix4x4 relVp,
        out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = maxX = maxY = 0f;
        int s = p.StartVertex, n = p.VertexCount;
        if (n < 3 || s < 0 || s + n > model.PortalVertices.Count) return false;

        // f >= 0 keeps the vertex. k: 0 = w+x, 1 = w-x, 2 = w+y, 3 = w-y.
        static float Plane(Vector4 v, int k) => k switch
        {
            0 => v.W + v.X,
            1 => v.W - v.X,
            2 => v.W + v.Y,
            _ => v.W - v.Y,
        };

        var cur = new List<Vector4>(n + 4);
        for (int i = 0; i < n; i++)
        {
            var vv = model.PortalVertices[s + i];
            var world = Vector3.Transform(new Vector3(vv.x, vv.y, vv.z), instanceTransform);
            cur.Add(Vector4.Transform(new Vector4(world - cameraPosition, 1f), relVp));
        }

        for (int k = 0; k < 4; k++)
        {
            var next = new List<Vector4>(cur.Count + 1);
            for (int i = 0; i < cur.Count; i++)
            {
                Vector4 a = cur[i], b = cur[(i + 1) % cur.Count];
                float fa = Plane(a, k), fb = Plane(b, k);
                if (fa >= 0f) next.Add(a);
                if ((fa >= 0f) != (fb >= 0f))
                {
                    float t = fa / (fa - fb);
                    next.Add(a + (b - a) * t);
                }
            }
            cur = next;
            if (cur.Count < 3) return false;
        }

        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        foreach (var c in cur)
        {
            float w = MathF.Abs(c.W) < PortalWClampBand ? PortalWClampSub : c.W;
            float x = c.X / w, y = c.Y / w;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        return true;
    }

    /// <summary>
    /// Portal polygons as world-space triangles, for the debug draw.
    ///
    /// PLAN_10 §6 item 4: a portal whose plane or winding is wrong is obvious as
    /// a SHAPE and invisible as a number. Doorways should appear as flat quads
    /// standing in the door openings - one lying in a floor, or floating in a
    /// wall, says the transform or the vertex range is wrong before any
    /// traversal is written on top of it.
    ///
    /// Fan-triangulated from the polygon, which is correct because MOPT polygons
    /// are convex by construction. Reuses CollisionDebugRenderer.RenderHighlight
    /// rather than adding a GL path - handbook §10 records writing from scratch
    /// what already existed, twice.
    /// </summary>
    public List<Vector3> PortalDebugTriangles(bool onlyCameraWmo)
    {
        var tris = new List<Vector3>();
        string? only = onlyCameraWmo ? CameraGroup?.InstancePath : null;
        if (onlyCameraWmo && only is null) return tris;

        foreach (var instance in _instances)
        {
            if (only is not null &&
                !instance.Path.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;

            var model = instance.Model;
            foreach (var portal in model.Portals)
            {
                int start = portal.StartVertex;
                int count = portal.VertexCount;
                if (count < 3 || start < 0 || start + count > model.PortalVertices.Count) continue;

                var v0 = ToWorld(model.PortalVertices[start], instance.Transform);
                for (int i = 1; i + 1 < count; i++)
                {
                    tris.Add(v0);
                    tris.Add(ToWorld(model.PortalVertices[start + i], instance.Transform));
                    tris.Add(ToWorld(model.PortalVertices[start + i + 1], instance.Transform));
                }
            }
        }
        return tris;

        static Vector3 ToWorld((float x, float y, float z) v, Matrix4x4 m)
            => Vector3.Transform(new Vector3(v.x, v.y, v.z), m);
    }

    /// <summary>
    /// Print the portal graph of every loaded WMO that has one.
    ///
    /// PLAN_10 §7 step 2: this is what turns "the traversal is wrong" into
    /// "portal 12 links group 4 to group 7 and it should not". It also runs the
    /// two integrity checks the reader only ever asserted in a comment -
    /// MOHD's NPortals against the parsed count, and every MOPR group index
    /// being in range.
    /// </summary>
    public void DumpPortalGraph()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int withPortals = 0;

        foreach (var instance in _instances)
        {
            if (!seen.Add(instance.Path)) continue;
            var model = instance.Model;
            if (model.Portals.Count == 0 && model.DeclaredPortalCount == 0) continue;
            withPortals++;

            string name = Path.GetFileName(instance.Path);
            Console.WriteLine($"[portals] {name}: {model.Portals.Count} portal(s), " +
                              $"{model.PortalRefs.Count} reference(s), " +
                              $"{model.PortalVertices.Count} vertex(es), " +
                              $"{model.Groups.Count} group(s)");

            if (model.DeclaredPortalCount != (uint)model.Portals.Count)
                Console.WriteLine($"[portals]   MISMATCH: MOHD declares {model.DeclaredPortalCount} " +
                                  $"but MOPT parsed {model.Portals.Count} - the reader's assumption " +
                                  "is broken and traversal must not be trusted");

            // MOPR group indices are FILE indices. Model.Groups is the list of
            // groups that produced a mesh, and a group that was empty, missing
            // or antiportal never got one - so the list is NOT index-aligned
            // with the file and must never be indexed by a MOPR value. Look up
            // by GroupMesh.GroupIndex, which is the file index.
            var byFileIndex = new Dictionary<int, GroupMesh>();
            foreach (var g in model.Groups) byFileIndex[g.GroupIndex] = g;

            int badRefs = 0, badPortals = 0;
            foreach (var r in model.PortalRefs)
            {
                if (!byFileIndex.ContainsKey(r.GroupIndex)) badRefs++;
                if (r.PortalIndex >= model.Portals.Count) badPortals++;
            }
            if (badRefs > 0 || badPortals > 0)
                Console.WriteLine($"[portals]   {badRefs} ref(s) point at a group with no mesh, " +
                                  $"{badPortals} portal ref(s) out of range " +
                                  "(refs with no mesh are usually fine - empty or antiportal groups)");

            foreach (var g in model.Groups)
            {
                if (g.PortalCount == 0) continue;
                var targets = new List<string>();
                for (int i = 0; i < g.PortalCount; i++)
                {
                    int idx = g.PortalStart + i;
                    if (idx < 0 || idx >= model.PortalRefs.Count) { targets.Add("?"); continue; }
                    var r = model.PortalRefs[idx];
                    string to = byFileIndex.TryGetValue(r.GroupIndex, out var target)
                        ? $"{r.GroupIndex}:{target.GroupName}"
                        : $"{r.GroupIndex}:no-mesh";
                    targets.Add($"p{r.PortalIndex}->{to}{(r.Side < 0 ? "-" : "+")}");
                }
                Console.WriteLine($"[portals]   [{g.GroupIndex,3}] '{g.GroupName}' " +
                                  $"{(g.IsInterior ? "INT" : "ext")}  {g.PortalCount} door(s): " +
                                  string.Join("  ", targets));
            }
        }

        if (withPortals == 0)
            Console.WriteLine("[portals] no loaded WMO declares any portals");
    }

    /// <summary>Distance (yd) below which an impostor shell hides even from
    /// outside (you are right on top of it). HUD-tunable. Was the hard-coded 196.</summary>
    public float ShellNearGuard { get; set; } = 196f;

    /// <summary>An ALWAYS_DRAW group under this many vertices is treated as a
    /// distance impostor regardless of its interior flag. HUD-tunable; retunes
    /// the whole city live because classification runs at draw time.</summary>
    public int ImpostorMaxVertices { get; set; } = 2000;

    /// <summary>Print the throttled [wmo-vis] line to the console. Off by default
    /// now that the same numbers live in the HUD.</summary>
    public bool VisTrace { get; set; }

    // Live visibility state for the biggest WMO in view, surfaced in the HUD so
    // the swap can be watched without console spam.
    public bool LastInsideCity { get; private set; }
    public int ShellsDrawnLastFrame { get; private set; }
    public int ShellsHiddenLastFrame { get; private set; }
    public string LargestWmoName { get; private set; } = "";
    public int LargestWmoGroupsDrawn { get; private set; }
    public int LargestWmoGroupCount { get; private set; }

    /// <summary>Collision BVH used for occlusion tests. Set by the game loop to the
    /// active world; when null, occlusion culling is skipped.</summary>
    public CollisionWorld? OcclusionWorld { get; set; }

    /// <summary>Hide exterior groups the collision BVH shows are blocked by nearer
    /// geometry (only while inside a large WMO). Off by default; tunable/toggleable.</summary>
    public bool OcclusionCulling { get; set; }

    /// <summary>Only occlusion-test exterior groups beyond this (yd) — nearby ones
    /// are cheap to just draw and shouldn't flicker.</summary>
    public float OcclusionMinDistance { get; set; } = 40f;

    /// <summary>Slack (yd) subtracted from the group distance before the occlusion
    /// ray, so grazing self-hits at the group's own face don't read as blocked.</summary>
    public float OcclusionMargin { get; set; } = 8f;

    public int OccludedGroupsLastFrame { get; private set; }

    public float DrawDistance { get; set; }
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Draw every batch two-sided, ignoring each material's IsNoCull flag.
    ///
    /// WMO winding is not consistent — that is why the collision raycast is
    /// two-sided as well. A group wound inward whose material does not set
    /// IsNoCull renders as nothing at all from outside, which reads as a
    /// building with missing walls rather than as a culling problem. Toggling
    /// this tells the two apart in one click: if the missing pieces reappear,
    /// it is winding, not lost geometry.
    ///
    /// DEFAULTED OFF, AND IT USED TO BE ON. As a diagnostic this is exactly
    /// right; as a shipping default it was the most expensive line in the
    /// client. The WMO pass is 72-86% of GPU time in a city, and with this on
    /// every wall in it paid double triangle setup and double rasterised
    /// fragments — on an integrated GPU with no hidden-surface removal, where
    /// fill is the whole budget. No quality preset touched it either, which is
    /// most of why dropping to Low never helped.
    ///
    /// If buildings now show missing walls, tick it back on: that answers the
    /// winding question in one click, exactly as intended. The fix for a real
    /// winding problem is per-batch, not a global.
    /// </summary>
    public bool ForceTwoSided { get; set; }

    /// <summary>Curated show/hide overrides consulted by ClassifyGroup before any
    /// heuristic. Null = none. The data ships and is honoured in release (PLAN_04).</summary>
    public VisibilityOverrides? Overrides { get; set; }

    /// <summary>
    /// Alpha below which a fragment is discarded, for textures that actually
    /// have an alpha channel. Railings and window tracery need it; a wall panel
    /// whose texture has no alpha must never be subject to it.
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.35f;

    /// <summary>
    /// Light WMO interiors from their baked MOCV vertex colours instead of the
    /// outdoor sun. Off reverts to the old behaviour exactly (every batch takes
    /// the exterior path), which is the fastest way to see what MOCV is doing.
    /// </summary>
    public bool UseVertexColors { get; set; } = true;

    /// <summary>
    /// The overbright factor applied to MOCV. 2.0 is not a taste setting: the
    /// classic render path halves vertex colours at load and doubles them at
    /// draw, so the authored range is [0, 2] rather than [0, 1] and lanterns
    /// are meant to blow past white. Exposed because it is the single knob
    /// that says whether interiors are too dark or too flat.
    /// </summary>
    public float VertexColorScale { get; set; } = 2.0f;

    /// <summary>Brightness multiplier on baked MOCV interior lighting (Deadmines
    /// etc). >1 lifts the too-dark interior seen through a portal; exterior
    /// (daylight/no-MOCV) geometry is on a different path and is untouched.</summary>
    public float InteriorBrightness { get; set; } = 1.0f;

    /// <summary>Beyond-portal fill light, driven per frame by GameLoop from the
    /// nearest instance portal. Position is world-absolute (the renderer
    /// subtracts the camera itself); colour is premultiplied by intensity;
    /// radius 0 leaves the light off so exterior lighting is untouched.</summary>
    public Vector3 PortalLightWorldPos { get; set; }
    public Vector3 PortalLightColor { get; set; }
    public float PortalLightRadius { get; set; }

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    /// <summary>1 overnight, 0 by day: scales the SIDN window glow (WorldAtmosphere.NightFraction).</summary>
    public float NightFraction { get; set; }
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    public WmoRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
        DrawDistance = Math.Max(100f, config.Render.WmoDistance);
        FogEnd = DrawDistance;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "wmo.vert"),
            Path.Combine(shaderDir, "wmo.frag"));
    }

    /// <summary>
    /// Read the ADTs for these tiles and place every WMO they reference.
    ///
    /// This re-reads each ADT rather than borrowing the terrain's parse. That
    /// is a real cost — startup already reads every tile twice — but it keeps
    /// buildings independent of terrain, and collapsing all the reads into one
    /// is a single obvious change once the shape settles.
    /// </summary>
    public void LoadForTiles(
        IEnumerable<(int col, int row)> tiles, AdtCache adts, bool warmedOnly = false)
    {
        var started = DateTime.UtcNow;
        var pending = new List<(Model Model, AdtTerrainReader.WmoInstance Placement)>();

        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Wmos is null) continue;

            foreach (var w in adt.Wmos)
            {
                if (string.IsNullOrWhiteSpace(w.ModelPath)) continue;
                if (w.ModelPath.StartsWith("Unknown_", StringComparison.Ordinal)) continue;

                // A building straddling a tile edge is listed in every ADT it
                // touches. Identity is the model plus its exact position.
                string key = $"{w.ModelPath}|{w.PosX:F2}|{w.PosY:F2}|{w.PosZ:F2}";
                if (_placed.Contains(key)) continue;

                // The cold-start placement pass must never turn an async warm
                // miss into the blocking ResolveModel spin. Leave the placement
                // eligible so the normal residency path can add it once warm.
                var model = warmedOnly
                    ? _models.GetValueOrDefault(w.ModelPath)
                    : ResolveModel(w.ModelPath);
                if (model is null) continue;
                if (!_placed.Add(key)) continue;

                pending.Add((model, w));
            }
        }

        foreach (var (model, w) in pending)
            PlaceResolvedModel(model, w, globalMap: false);

        if (pending.Count > 0) BumpLiquidVersion();

        TotalTriangles = _instances.Sum(i => i.Model.TriangleCount);

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine(
            $"[wmo] {_instances.Count} placement(s), {ModelCount} model(s), {TextureCount} texture(s), " +
            $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");

        if (_texturelessBatches > 0)
            Console.WriteLine(
                $"[wmo] {_texturelessBatches} batch(es) have no texture in ANY material slot " +
                "- these draw as flat grey");

        if (_rescaledAlpha.Count > 0)
            Console.WriteLine(
                $"[wmo] {_rescaledAlpha.Count} texture(s) had 1-bit alpha decoded as 0/1 and were " +
                "rescaled to 0/255 — BlpDecoder should be doing this");

        if (_opaqueTextures.Count > 0)
            Console.WriteLine(
                $"[wmo] {_opaqueTextures.Count} texture(s) decoded with an all-zero alpha channel " +
                "- these are opaque and are now exempt from the alpha cut");

        if (_failedTextures.Count > 0)
        {
            Console.WriteLine($"[wmo] {_failedTextures.Count} named texture(s) failed to decode:");
            foreach (var name in _failedTextures.Take(10))
                Console.WriteLine($"[wmo]   {name}");
        }
    }

    /// <summary>
    /// Queue the single root WMO named by a global-WMO map's WDT. These maps
    /// have no ADTs, so routing this through <see cref="QueuePreloadForTiles"/>
    /// can never discover the model.
    /// </summary>
    public void QueuePreloadGlobal(AdtTerrainReader.WmoInstance placement)
    {
        string path = placement.ModelPath;
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("Unknown_", StringComparison.Ordinal) ||
            _models.ContainsKey(path) ||
            FindPreloadJob(path) is not null ||
            !_preloadQueued.Add(path)) return;

        _preloadQueue.Enqueue(path, 0f);
    }

    /// <summary>
    /// Place the WDT-level WMO used by maps such as Blackrock Depths. Its MODF
    /// coordinates are centred on the instance map, not on the 64x64 ADT grid;
    /// using the terrain placement transform moves it about 17,000 yards away.
    /// </summary>
    public bool LoadGlobal(AdtTerrainReader.WmoInstance placement, bool warmedOnly = false)
    {
        string path = placement.ModelPath;
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("Unknown_", StringComparison.Ordinal)) return false;

        string key = PlacementKey(placement);
        if (_placed.Contains(key)) return true;

        var model = warmedOnly ? _models.GetValueOrDefault(path) : ResolveModel(path);
        if (model is null || !_placed.Add(key)) return false;

        PlaceResolvedModel(model, placement, globalMap: true);
        BumpLiquidVersion();
        TotalTriangles = _instances.Sum(i => i.Model.TriangleCount);
        Console.WriteLine($"[wmo-global] {Path.GetFileName(path)}, " +
                          $"{model.Groups.Count} group(s), {TotalTriangles:N0} triangles");
        return true;
    }

    private static string PlacementKey(AdtTerrainReader.WmoInstance placement)
        => $"{placement.ModelPath}|{placement.PosX:F2}|{placement.PosY:F2}|{placement.PosZ:F2}";

    private void PlaceResolvedModel(Model model, AdtTerrainReader.WmoInstance placement,
                                    bool globalMap)
    {
        string key = PlacementKey(placement);
        var transform = globalMap
            ? BuildGlobalPlacement(placement)
            : BuildPlacement(placement);
        var (min, max) = TransformedBounds(model, transform);
        Matrix4x4 placementToWorld = globalMap
            ? GlobalPlacementToWorld
            : PlacementToWorld;

        ResidentVersion++;
        _instances.Add(new Instance
        {
            Id = ++_nextInstanceId,
            Model = model,
            Transform = transform,
            WorldMin = min,
            WorldMax = max,
            Origin = Vector3.Transform(
                new Vector3(placement.PosX, placement.PosY, placement.PosZ),
                placementToWorld),
            Path = placement.ModelPath,
            DoodadSet = placement.DoodadSet,
            NameSetId = placement.NameSetId,
            AppearStart = ResolveAppearStart(key),
        });

        VerifyPlacement(placement, min, max, placementToWorld);
    }

    public enum DynamicPlacement { Placed, Pending, Unavailable }

    /// <summary>
    /// Raw WMO local space is already WoW's X/Y/Z frame. A streamed GameObject
    /// therefore needs only its uniform scale, WoW-Z yaw and world translation;
    /// MODF's placement-space basis/corner shift must never enter this path.
    /// </summary>
    public static Matrix4x4 DynamicGameObjectTransform(
        Vector3 position, float yaw, float scale) =>
        Matrix4x4.CreateScale(scale > 0.0001f ? scale : 1f) *
        Matrix4x4.CreateRotationZ(yaw) *
        Matrix4x4.CreateTranslation(position);

    public bool HasDynamic(ulong guid) => _dynamicByGuid.ContainsKey(guid);

    /// <summary>Publish one WMO-display server GameObject by GUID. Missing
    /// resident models queue on the ordinary WMO streaming lane and retry on a
    /// later reconciliation frame; the game thread never blocks on parsing.</summary>
    public DynamicPlacement AddDynamic(ulong guid, string modelPath, Matrix4x4 transform)
    {
        RemoveDynamic(guid);
        if (_models.TryGetValue(modelPath, out Model? resolved))
        {
            if (resolved is null) return DynamicPlacement.Unavailable;
            var (min, max) = TransformedBounds(resolved, transform);
            var instance = new Instance
            {
                Id = ++_nextInstanceId,
                Model = resolved,
                Transform = transform,
                WorldMin = min,
                WorldMax = max,
                Origin = new Vector3(transform.M41, transform.M42, transform.M43),
                Path = modelPath,
                DynamicGuid = guid,
                DoodadSet = 0,
                NameSetId = 0,
                // Current Benilla does not apply the terrain-WMO appear fade to
                // WMO-display GameObjects.
                AppearStart = 0f,
            };
            _instances.Add(instance);
            ResidentVersion++;
            _dynamicByGuid[guid] = instance;
            TotalTriangles += resolved.TriangleCount;
            if (resolved.Liquids.Count > 0) BumpLiquidVersion();
            return DynamicPlacement.Placed;
        }

        if (FindPreloadJob(modelPath) is null && _preloadQueued.Add(modelPath))
            _preloadQueue.Enqueue(modelPath, 0f);
        return DynamicPlacement.Pending;
    }

    /// <summary>Move a dynamic WMO hull and its draw/pick/cull bounds in place.</summary>
    public bool TryUpdateDynamicTransform(ulong guid, Matrix4x4 transform)
    {
        if (!_dynamicByGuid.TryGetValue(guid, out Instance? instance)) return false;
        instance.Transform = transform;
        (instance.WorldMin, instance.WorldMax) = TransformedBounds(instance.Model, transform);
        instance.Origin = new Vector3(transform.M41, transform.M42, transform.M43);
        if (instance.Model.Liquids.Count > 0) BumpLiquidVersion();
        return true;
    }

    public bool RemoveDynamic(ulong guid)
    {
        if (!_dynamicByGuid.Remove(guid, out Instance? instance)) return false;
        _instances.Remove(instance);
        _cameraSeeds.Remove(instance.Id);
        _portalVisible.Remove(instance.Id);
        if (CameraGroup is { } camera && camera.InstanceId == instance.Id) CameraGroup = null;
        TotalTriangles -= instance.Model.TriangleCount;
        if (instance.Model.Liquids.Count > 0) BumpLiquidVersion();
        return true;
    }

    /// <summary>Nearest streamed WMO GameObject AABB hit. Static scenery WMOs
    /// never participate, so the returned identity is always a server GUID.</summary>
    public bool TryPickDynamic(Vector3 origin, Vector3 direction, float maxDistance,
        out ulong guid, out float distance)
    {
        guid = 0;
        distance = maxDistance;
        bool hit = false;
        foreach ((ulong candidate, Instance instance) in _dynamicByGuid)
        {
            if (RayAabb(origin, direction, instance.WorldMin, instance.WorldMax, out float t) &&
                t < distance)
            {
                guid = candidate;
                distance = t;
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>
    /// Raycast the live collision mesh of WMO-display GameObjects and retain the
    /// owning server GUID. Static world collision is baked into CollisionWorld;
    /// a moving hull cannot join that snapshot without leaving a ghost collider
    /// at the sampled pose, so controlled transport support queries this lane.
    /// </summary>
    public bool TryRaycastDynamicCollision(Vector3 origin, Vector3 direction,
        float maxDistance, out ulong guid, out RayHit hit,
        Predicate<ulong>? accept = null)
    {
        guid = 0;
        hit = default;
        if (maxDistance <= 0f || direction.LengthSquared() < 1e-12f) return false;

        Vector3 worldDirection = Vector3.Normalize(direction);
        float best = maxDistance;
        bool found = false;
        foreach ((ulong candidate, Instance instance) in _dynamicByGuid)
        {
            if (accept is not null && !accept(candidate)) continue;
            Vector3[] triangles = instance.Model.CollisionTriangles;
            if (triangles.Length < 3 ||
                !RayAabb(origin, worldDirection, instance.WorldMin, instance.WorldMax,
                    out float boxDistance) || boxDistance > best ||
                !Matrix4x4.Invert(instance.Transform, out Matrix4x4 inverse))
                continue;

            Vector3 localOrigin = Vector3.Transform(origin, inverse);
            Vector3 localEnd = Vector3.Transform(origin + worldDirection * best, inverse);
            Vector3 localDelta = localEnd - localOrigin;
            float localLimit = localDelta.Length();
            if (localLimit <= 1e-6f) continue;
            Vector3 localDirection = localDelta / localLimit;

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                if (!RayTriangle(localOrigin, localDirection,
                        triangles[i], triangles[i + 1], triangles[i + 2], out float localT) ||
                    localT < 0f || localT > localLimit)
                    continue;

                Vector3 localPoint = localOrigin + localDirection * localT;
                Vector3 worldPoint = Vector3.Transform(localPoint, instance.Transform);
                float worldDistance = Vector3.Distance(origin, worldPoint);
                if (worldDistance > best) continue;

                Vector3 localNormal = Vector3.Cross(
                    triangles[i + 1] - triangles[i], triangles[i + 2] - triangles[i]);
                Vector3 worldNormal = Vector3.TransformNormal(localNormal, instance.Transform);
                if (worldNormal.LengthSquared() <= 1e-12f) continue;
                worldNormal = Vector3.Normalize(worldNormal);
                if (Vector3.Dot(worldNormal, worldDirection) > 0f) worldNormal = -worldNormal;

                best = worldDistance;
                guid = candidate;
                hit = new RayHit(worldDistance, worldPoint, worldNormal, i / 3);
                found = true;

                // The endpoint used to define localLimit was based on the old
                // best. Shortening it is optional for correctness but prevents
                // later triangles from doing unnecessary range work.
                localEnd = Vector3.Transform(origin + worldDirection * best, inverse);
                localDelta = localEnd - localOrigin;
                localLimit = localDelta.Length();
                if (localLimit > 1e-6f) localDirection = localDelta / localLimit;
            }
        }
        return found;
    }

    private static bool RayAabb(Vector3 origin, Vector3 direction,
        Vector3 min, Vector3 max, out float enter)
    {
        enter = 0f;
        float t0 = 0f, t1 = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            float inv = 1f / d;
            float ta = (lo - o) * inv, tb = (hi - o) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        enter = t0;
        return true;
    }

    /// <summary>
    /// Drop placed instances while retaining parsed models, textures and GPU
    /// buffers. A streaming ring change should rebuild cheap placement state,
    /// never pay the model-loading cost again.
    /// </summary>
    public void ResetPlacements()
    {
        _instances.Clear();
        _dynamicByGuid.Clear();
        _placed.Clear();
        _cameraSeeds.Clear();
        _portalVisible.Clear();
        CameraGroup = null;
        TotalTriangles = 0;
        BumpLiquidVersion();   // PLAN_15 D5: canals must not survive their building
    }

    /// <summary>
    /// ResetPlacements PLUS the outer-ring bookkeeping, for a map change.
    ///
    /// ResetPlacements alone is right for a tile crossing, where the deferred
    /// ring is still describing tiles of the same world and is meant to
    /// survive. Across a map boundary it is describing tiles that no longer
    /// exist, holding a reference to an AdtCache that has been repointed - so
    /// retrying them would queue the NEW map's buildings for the OLD map's ring.
    ///
    /// Model, texture and GPU-buffer caches are deliberately NOT cleared: they
    /// are keyed by file path, and a WMO file is the same file whichever map
    /// places it. Dropping them would make every round trip re-parse and
    /// re-upload Stormwind.
    /// </summary>
    public void ResetForMapChange()
    {
        ResetPlacements();
        _deferredRingTiles.Clear();
        _ringAdts = null;
    }

    /// <summary>
    /// Discover WMO assets in an outer tile ring without placing them. The
    /// parsed models, decoded textures and GPU buffers stay in the normal
    /// caches, so making the inner ring resident later is a cheap cache hit.
    /// </summary>
    /// <summary>
    /// Queue every WMO referenced by these tiles for background warming.
    ///
    /// NON-BLOCKING BY CONTRACT (PLAN_08 D1). This is speculative warming of the
    /// outer preload ring, so a tile whose ADT is not parsed yet is deferred and
    /// retried next frame rather than waited on.
    ///
    /// It used to call adts.Get(), which blocks on a pending parse. With 25 ring
    /// tiles and a worker pool busy with doodads, the hitch recorder measured
    /// that at 61 ms of a 187 ms tile-crossing freeze - the single largest item.
    /// Nothing warmed here is needed this frame: the buildings are two tiles
    /// away. Waiting for them was pure loss.
    /// </summary>
    public void QueuePreloadForTiles(
        IEnumerable<(int col, int row)> tiles, AdtCache adts, Vector2? streamCentre = null)
    {
        _ringAdts = adts;
        _preloadStreamCentre = streamCentre;
        foreach (var key in tiles) TryQueueRingTile(key, adts);
    }

    /// <summary>
    /// Enqueue one ring tile's WMOs if its ADT is already parsed; otherwise
    /// start the parse on the pool and remember the tile for a later retry.
    /// </summary>
    private void TryQueueRingTile((int col, int row) key, AdtCache adts)
    {
        if (!adts.TryPeek(key.col, key.row, out var adt))
        {
            // Kick the parse off-thread and come back to it. QueueLoad is
            // idempotent, so calling it again next frame costs nothing.
            adts.QueueLoad(key.col, key.row, _workers);
            _deferredRingTiles.Add(key);
            return;
        }

        // Authoritative answer, including "this tile has no ADT" - either way
        // the tile is finished and must not be retried forever.
        _deferredRingTiles.Remove(key);
        if (adt?.Wmos is null) return;

        foreach (var w in adt.Wmos)
        {
            string path = w.ModelPath;
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("Unknown_", StringComparison.Ordinal) ||
                _models.ContainsKey(path) ||
                FindPreloadJob(path) is not null ||
                !_preloadQueued.Add(path)) continue;

            var transform = BuildPlacement(w);
            float distanceSq = _preloadStreamCentre is Vector2 centre
                ? Vector2.DistanceSquared(new Vector2(transform.M41, transform.M42), centre)
                : 0f;
            _preloadQueue.Enqueue(path, distanceSq);
        }
    }

    /// <summary>
    /// Retry ring tiles whose ADT has finished parsing since it was deferred.
    /// Cheap: a dictionary probe each, and the set empties as parses land.
    /// </summary>
    private void DrainDeferredRingTiles()
    {
        if (_ringAdts is null || _deferredRingTiles.Count == 0) return;

        // Snapshot: TryQueueRingTile mutates the set it is iterating.
        foreach (var key in _deferredRingTiles.ToArray())
            TryQueueRingTile(key, _ringAdts);
    }

    /// <summary>Warm one queued model. Runtime streaming calls this once per frame.</summary>
    public bool WarmNextPreload(bool waitForWorker = false)
    {
        // Ring tiles deferred by TryQueueRingTile get their retry here, because
        // this is already called once per frame by the streaming update.
        DrainDeferredRingTiles();

        while (_preloadJobs.Count < MaxConcurrentPreloads && _preloadQueue.Count > 0)
        {
            _preloadQueue.TryDequeue(out string? path, out float distanceSq);
            if (path is null) continue;
            _preloadQueued.Remove(path);
            PreloadDequeued?.Invoke(path, distanceSq);
            if (_models.ContainsKey(path)) continue;
            var started = StartModelLoad(path);
            if (started is not null) _preloadJobs.Add(started);
        }

        if (_preloadJobs.Count == 0) return false;

        int jobIndex = FindReadyPreloadJob();
        if (jobIndex < 0 && waitForWorker)
        {
            jobIndex = _preloadFinalizeCursor % _preloadJobs.Count;
            try { _preloadJobs[jobIndex].Worker.GetAwaiter().GetResult(); } catch { }
        }
        // No main-thread progress is possible yet. Returning true here makes a
        // wall-clock DrainWarm loop busy-spin for its whole budget and competes
        // with the worker it is waiting for.
        if (jobIndex < 0) return false;

        var job = _preloadJobs[jobIndex];
        _preloadFinalizeCursor = (jobIndex + 1) % _preloadJobs.Count;
        var stepTimer = System.Diagnostics.Stopwatch.StartNew();
        bool completed = StepModelLoad(job, waitForWorker);
        if (completed)
        {
            _preloadJobs.RemoveAt(jobIndex);
            if (_preloadJobs.Count == 0) _preloadFinalizeCursor = 0;
            else if (jobIndex < _preloadFinalizeCursor) _preloadFinalizeCursor--;
            Console.WriteLine($"[wmo-preload] {Path.GetFileName(job.RootPath)} prepared over " +
                              $"{job.Ready?.Root?.NGroups ?? 0} group(s), {job.Timer.Elapsed.TotalSeconds:F2}s, " +
                              $"{_preloadQueue.Count} queued, {_preloadJobs.Count} in flight");
        }
        if (stepTimer.Elapsed.TotalMilliseconds >= 8)
            Console.WriteLine($"[stream-budget] WMO finalize {Path.GetFileName(job.RootPath)} " +
                              $"took {stepTimer.Elapsed.TotalMilliseconds:F0}ms");

        // An upload in flight is the same no-progress condition. Once it lands,
        // each call builds one ready group and DrainWarm may continue to budget.
        return completed || job.Upload is null || job.Upload.IsCompleted;
    }

    private ModelLoadJob? FindPreloadJob(string rootPath)
    {
        foreach (var job in _preloadJobs)
            if (job.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) return job;
        return null;
    }

    private int FindReadyPreloadJob()
    {
        for (int offset = 0; offset < _preloadJobs.Count; offset++)
        {
            int index = (_preloadFinalizeCursor + offset) % _preloadJobs.Count;
            var job = _preloadJobs[index];
            if (job.Worker.IsCompleted && (job.Upload is null || job.Upload.IsCompleted))
                return index;
        }
        return -1;
    }

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// ADT placement space (Y-up, origin at the map corner) to WoW world space.
    /// Row-vector convention, matching System.Numerics and the way Camera
    /// uploads its matrices.
    ///
    ///     (x, y, z) -> (C - z, C - x, y)
    /// </summary>
    private static Matrix4x4 PlacementToWorld => new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        CoordShift, CoordShift, 0f, 1f);

    /// <summary>
    /// Global-WMO MODF space uses the same axes as an ADT MODF but is already
    /// centred on the instance map. It therefore needs the linear part of
    /// <see cref="PlacementToWorld"/> without the continent-corner translation.
    /// BRD's transformed bounds become X 259..1487, Y -848..265, Z -209..171,
    /// which contains the server entrance (457, 34, -68).
    /// </summary>
    private static Matrix4x4 GlobalPlacementToWorld => new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>
    /// Model vertex basis to placement basis: (x, y, z) -> (x, z, -y).
    ///
    /// Settled by calibration and then confirmed in game, so the 64-candidate
    /// search that found it is gone. Note that M2 does NOT use this — a doodad's
    /// render mesh is already Y-up and needs identity — while an M2's COLLISION
    /// hull does. Three arrays, two conventions; do not assume they agree.
    /// </summary>
    private static readonly Matrix4x4 Basis = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    public string ConventionName => "xz-y, heading rotY-90";

    /// <summary>
    /// Extra heading applied to every WMO, on top of whatever calibration picks.
    ///
    /// THIS IS NOT SOMETHING CALIBRATION CAN FIND, and the reason is worth
    /// keeping: an axis-aligned bounding box is INVARIANT under a 180 degree
    /// rotation about the vertical axis. Same extents, same size. The centre
    /// term barely moves either, because a WMO's local origin usually sits near
    /// the middle of its geometry. So the scorer is structurally blind to a
    /// half-turn — it can settle which axis is up, and it cannot settle which
    /// way a building faces.
    ///
    /// That part was determined by looking at Northshire: every building faced
    /// backwards, uniformly, which is the signature of a constant heading
    /// error rather than a per-model one.
    ///
    /// Do not "fix" the scoring function to catch this. It cannot, by
    /// construction.
    /// </summary>
    private const float HeadingCorrectionDegrees = 180f;

    private static Matrix4x4 BuildPlacement(AdtTerrainReader.WmoInstance w)
        => BuildPlacement(w, PlacementToWorld);

    /// <summary>World Builder ghost (shared_docs/WORLD_BUILDER.md §4): the exact transform a MODF
    /// entry with these placement-space fields gets once published, for <see cref="AddDynamic"/>.</summary>
    public static Matrix4x4 ModfTransform(Vector3 placementPos, Vector3 rotDeg)
        => BuildPlacement(new AdtTerrainReader.WmoInstance
        {
            PosX = placementPos.X, PosY = placementPos.Y, PosZ = placementPos.Z,
            RotX = rotDeg.X, RotY = rotDeg.Y, RotZ = rotDeg.Z,
        });

    private static Matrix4x4 BuildGlobalPlacement(AdtTerrainReader.WmoInstance w)
        => BuildPlacement(w, GlobalPlacementToWorld);

    private static Matrix4x4 BuildPlacement(
        AdtTerrainReader.WmoInstance w, Matrix4x4 placementToWorld)
    {
        const float deg = MathF.PI / 180f;

        // rotY - 270 from calibration, plus the 180 that only eyes could find,
        // is rotY - 90. The bounding-box score could settle which axis is up
        // and never which way a building faces, because an AABB is invariant
        // under a half turn about the vertical.
        float heading = (w.RotY - 90f) * deg;

        // Column-vector intent: RotY(heading) * RotZ(-rotX) * RotX(rotZ).
        // System.Numerics is row-vector, so the order reverses.
        var rotation = Matrix4x4.CreateRotationX(w.RotZ * deg)
                     * Matrix4x4.CreateRotationZ(-w.RotX * deg)
                     * Matrix4x4.CreateRotationY(heading);

        return Basis
             * rotation
             * Matrix4x4.CreateTranslation(w.PosX, w.PosY, w.PosZ)
             * placementToWorld;
    }

    /// <summary>
    /// World bounds of a placed model, from the eight corners of each group's
    /// local box. Used for frustum culling and for the placement check.
    /// </summary>
    private static (Vector3 min, Vector3 max) TransformedBounds(Model model, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var g in model.Groups)
        {
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? g.LocalMin.X : g.LocalMax.X,
                    (c & 2) == 0 ? g.LocalMin.Y : g.LocalMax.Y,
                    (c & 4) == 0 ? g.LocalMin.Z : g.LocalMax.Z);

                var p = Vector3.Transform(corner, m);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        return (min, max);
    }

    private static (Vector3 min, Vector3 max) TransformedBounds(GroupMesh group, Matrix4x4 m)
        => TransformedBounds(group.LocalMin, group.LocalMax, m);

    private static (Vector3 min, Vector3 max) TransformedBounds(
        Vector3 localMin, Vector3 localMax, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? localMin.X : localMax.X,
                (c & 2) == 0 ? localMin.Y : localMax.Y,
                (c & 4) == 0 ? localMin.Z : localMax.Z);
            var p = Vector3.Transform(corner, m);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static float DistanceToBox(Vector3 p, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Is the camera inside this placed WMO — inside one of its real spatial
    /// cells, not merely inside the whole model's world box? This is WoWee's
    /// findContainingGroup test: transform the camera into the model's local
    /// space and check it against each GROUP box. Distance-only shells are
    /// skipped because a shell's box is huge and does not mean you are indoors;
    /// counting it made the whole-instance box swallow the approach bridge and
    /// suppress the impostor everywhere. Streets, rooms and building shells give
    /// a boundary near the gate: on the bridge you are inside none of them, in
    /// the Trade District you are inside a street cell.
    /// </summary>
    private bool CameraInsideInstance(Instance instance, Vector3 cameraWorld)
    {
        if (!Matrix4x4.Invert(instance.Transform, out var inv)) return false;
        var local = Vector3.Transform(cameraWorld, inv);
        float m = InsideInstanceMargin;
        foreach (var g in instance.Model.Groups)
        {
            if (g.IsDistanceLod) continue;
            if (local.X >= g.LocalMin.X - m && local.X <= g.LocalMax.X + m &&
                local.Y >= g.LocalMin.Y - m && local.Y <= g.LocalMax.Y + m &&
                local.Z >= g.LocalMin.Z - m && local.Z <= g.LocalMax.Z + m)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The single visibility decision for one WMO group: returns exactly one
    /// WmoReasonCode. Render() switches on it to decide drawing; the read-only
    /// explanation used by the picker and scene dump calls this SAME method, so the
    /// reason reported can never drift from the reason acted on. Toggle state is read
    /// from this renderer's own properties; only the per-frame/per-group varying data
    /// is passed in. See PLAN_03_REASON_CODES.md.
    /// </summary>
    private WmoReasonCode ClassifyGroup(
        Instance instance, GroupMesh group, Vector3 groupMin, Vector3 groupMax, in FrameCullContext ctx)
    {
        // Curated overrides win over every heuristic below (PLAN_04). A hide is
        // absolute; a show bypasses the heuristic culls but still respects residency
        // (only resident groups reach here) and frustum. This is the pragmatic fix
        // for cases the heuristics cannot get right - e.g. the thief01 entrance keep
        // that stays visible across the open courtyard.
        bool forceShow = false;
        if (Overrides is not null)
        {
            var ov = Overrides.Resolve(instance.Path, group.GroupIndex, ctx.CameraInside);
            if (ov == WmoReasonCode.OverrideHide) return WmoReasonCode.OverrideHide;
            if (ov == WmoReasonCode.OverrideShow) forceShow = true;
        }

        float groupDistance = DistanceToBox(ctx.CameraPosition, groupMin, groupMax);
        bool shell = false;

        // Candidate portal views have their destination detail resident. Their
        // transformed camera can straddle a city's inside/outside cell boundary,
        // where the normal shell swap would otherwise expose a low-poly exterior
        // silhouette for some source angles. Suppression must test the baked
        // classification directly; UseDistanceLodShells=false would instead draw
        // this same group through the ordinary-detail path.
        if (SuppressDistanceLodShells && group.IsDistanceLod)
            return WmoReasonCode.ShellNearSuppressed;

        if (!forceShow)
        {
            if (UseDistanceLodShells && group.IsDistanceLod)
            {
                var groupCentre = (groupMin + groupMax) * 0.5f;
                bool suppress;
                if (UsePortalCulling)
                    // Portal culling gives a precise "have I crossed inside" signal,
                    // so the silhouette (e.g. the cathedral) holds across the whole
                    // approach/bridge and drops the instant you step into a cell -
                    // instead of vanishing at the ShellNearGuard yard mark out on the
                    // bridge. The yard guard is intentionally NOT applied here.
                    suppress = ctx.CameraInCell;
                else
                    // Culling off: byte-identical to the tuned behaviour (CameraInside
                    // OR within ShellNearGuard yards of the shell centre).
                    suppress = ctx.CameraInside ||
                        Vector3.DistanceSquared(ctx.CameraPosition, groupCentre) < ShellNearGuard * ShellNearGuard;
                if (suppress) return WmoReasonCode.ShellNearSuppressed;
                shell = true;
            }
            else
            {
                // benilla's visible[] gates EVERY group - true interiors (0x2000),
                // the 0x40 "exterior-lit" street/roof cells that make up most of a
                // city WMO, AND pure-EXTERIOR (0x08) shell groups. Exterior groups
                // are kept drawing by the outdoor seed (when outside) or the
                // deferred-exterior pass (inside, when a doorway to the sky is in
                // view); with neither, they cull - which is what finally hides the
                // trade-district roof once you are inside. Distance-LOD shells were
                // already handled above and never reach here (D5 skyline intact).
                if (ctx.ReachableGroups is not null)
                {
                    // PLAN_10 D2: the flood is authoritative. Reached => draw,
                    // unreached => cull. Retires the 120-yd rule entirely.
                    if (!ctx.ReachableGroups.Contains(group.GroupIndex))
                        return WmoReasonCode.InteriorCull;
                }
                else if (group.IsInterior && !ctx.CameraInside && groupDistance > InteriorCullDistance)
                {
                    // D6 legacy heuristic (only when the flood couldn't seed): kept
                    // interior-only, so with portal culling off/unseedable nothing
                    // changes - exterior groups still always draw.
                    return WmoReasonCode.InteriorCull;
                }
            }

            if (groupDistance > ctx.EffectiveDrawDistance)
                return WmoReasonCode.DistanceCulled;
        }

        if (FrustumCulling &&
            !Camera.BoxInFrustum(ctx.ViewProjection,
                groupMin - ctx.CameraPosition, groupMax - ctx.CameraPosition))
            return WmoReasonCode.FrustumCulled;

        if (!forceShow && OcclusionCulling && OcclusionWorld is not null
            && !group.IsDistanceLod
            && groupDistance > OcclusionMinDistance
            && IsOccluded(ctx.CameraPosition, groupMin, groupMax))
            return WmoReasonCode.OcclusionCulled;

        if (forceShow) return WmoReasonCode.OverrideShow;
        return shell ? WmoReasonCode.DrawnShellFar : WmoReasonCode.Drawn;
    }

    /// <summary>
    /// Is the group's nearest point blocked from the camera by nearer solid
    /// geometry, per the collision BVH? One ray at the group's closest box point,
    /// stopped a margin short of it, so a hit means something else is in front.
    /// A coarse single-ray test — good enough to drop the entrance towers seen
    /// through a district, not a full occlusion pass.
    /// </summary>
    private bool IsOccluded(Vector3 camera, Vector3 groupMin, Vector3 groupMax)
    {
        // Cull only when EVERY corner of the group's box is blocked by nearer
        // solid geometry. A single clear corner (a roof edge against the sky, a
        // tower top over a building) keeps the whole group visible — so this
        // hides a structure only when it is genuinely fully behind something,
        // never when it is merely partly obscured.
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? groupMin.X : groupMax.X,
                (c & 2) == 0 ? groupMin.Y : groupMax.Y,
                (c & 4) == 0 ? groupMin.Z : groupMax.Z);

            var to = corner - camera;
            float dist = to.Length();
            if (dist <= OcclusionMargin) return false;
            if (!OcclusionWorld!.Raycast(camera, to, dist - OcclusionMargin).HasValue)
                return false;   // this corner is clear -> group is visible
        }
        return true;   // all eight corners blocked
    }

    /// <summary>
    /// MODF's own bounding box for the placed model, in world space. This is
    /// the target every candidate is scored against.
    /// </summary>
    private static (Vector3 min, Vector3 max) ModfBoxInWorld(
        AdtTerrainReader.WmoInstance w, Matrix4x4 placementToWorld)
    {
        var a = Vector3.Transform(
            new Vector3(w.BbMinX, w.BbMinY, w.BbMinZ), placementToWorld);
        var b = Vector3.Transform(
            new Vector3(w.BbMaxX, w.BbMaxY, w.BbMaxZ), placementToWorld);
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <summary>
    /// Per-placement sanity check. Compares centre and size
    /// against MODF's box for the placed model.
    ///
    /// Bounding boxes are padded, so a few yards of size disagreement is normal
    /// and only the centre is really diagnostic. This exists to catch the one
    /// building that lands somewhere else entirely while the rest are fine —
    /// which a single global convention cannot fix and is worth knowing about.
    /// </summary>
    private static void VerifyPlacement(AdtTerrainReader.WmoInstance w,
                                        Vector3 min, Vector3 max,
                                        Matrix4x4 placementToWorld)
    {
        var (mMin, mMax) = ModfBoxInWorld(w, placementToWorld);

        var centreError = ((min + max) * 0.5f) - ((mMin + mMax) * 0.5f);
        float error = centreError.Length();

        if (error < 15f) return;

        Console.WriteLine(
            $"[wmo] OFF-CENTRE {Path.GetFileName(w.ModelPath)}: geometry centre " +
            $"({(min.X + max.X) * 0.5f:F0},{(min.Y + max.Y) * 0.5f:F0},{(min.Z + max.Z) * 0.5f:F0}) " +
            $"vs MODF centre ({(mMin.X + mMax.X) * 0.5f:F0},{(mMin.Y + mMax.Y) * 0.5f:F0}," +
            $"{(mMin.Z + mMax.Z) * 0.5f:F0}), {error:F1} yd apart");
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private Model? ResolveModel(string rootPath)
    {
        if (_models.TryGetValue(rootPath, out var cached)) return cached;

        // If the requested model is already being staged, finish that same job
        // rather than starting a duplicate. With the outer-ring lead this path
        // should be rare, but correctness wins if the player moves unusually
        // quickly or teleports.
        ModelLoadJob? job = FindPreloadJob(rootPath) ?? StartModelLoad(rootPath);
        if (job is null) return null;

        if (!job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        while (!StepModelLoad(job, waitForUpload: true)) { }
        int preloadIndex = _preloadJobs.IndexOf(job);
        if (preloadIndex >= 0) _preloadJobs.RemoveAt(preloadIndex);
        if (_preloadJobs.Count == 0) _preloadFinalizeCursor = 0;
        else _preloadFinalizeCursor %= _preloadJobs.Count;
        return _models.GetValueOrDefault(rootPath);
    }

    private ModelLoadJob? StartModelLoad(string rootPath)
    {
        if (_models.ContainsKey(rootPath)) return null;
        return new ModelLoadJob
        {
            RootPath = rootPath,
            Worker = _workers.Run(() => PrepareWmo(rootPath)),
        };
    }

    private PreparedWmo PrepareWmo(string rootPath)
    {
        var prepared = new PreparedWmo();
        var rootBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, rootPath);
        if (rootBytes is null)
        {
            prepared.MissingRoot = true;
            return prepared;
        }

        var root = WmoReader.ParseRoot(rootBytes);
        if (root is null) return prepared;
        prepared.Root = root;

        string stem = rootPath[..^4];
        for (int g = 0; g < (int)root.NGroups; g++)
        {
            string groupPath = $"{stem}_{g:D3}.wmo";
            var groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, groupPath);
            if (groupBytes is null)
            {
                prepared.MissingFiles++;
                prepared.Groups.Add(null);
                continue;
            }

            var group = WmoReader.ParseGroup(groupBytes, root.Flags);
            if (group is null)
            {
                prepared.Unparsed++;
                prepared.Groups.Add(null);
                continue;
            }
            if (g < root.GroupInfos.Count)
                group.GroupName = root.GroupInfos[g].Name;
            if (group.IsAntiportal)
            {
                prepared.Antiportal++;
                prepared.Groups.Add(null);
                continue;
            }
            if (group.Vertices.Count == 0 || group.Indices.Count < 3)
            {
                prepared.Empty++;
                prepared.Groups.Add(null);
                continue;
            }
            prepared.Groups.Add(group);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in root.Materials)
        foreach (string texturePath in new[]
                 { material.Texture0Name, material.Texture1Name, material.Texture2Name })
        {
            if (string.IsNullOrWhiteSpace(texturePath) || !seen.Add(texturePath)) continue;
            var decoded = AdtTerrainReader.ReadBlpPixelsPooled(
                _config.ClientDataPath, texturePath);
            if (decoded is null)
            {
                prepared.Textures.Add(new PreparedTexture { Path = texturePath });
                continue;
            }

            var (bgra, width, height) = decoded.Value;
            byte maxAlpha = 0;
            int pixelBytes = checked(width * height * 4);
            for (int i = 3; i < pixelBytes; i += 4)
            {
                if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
                if (maxAlpha > 1) break;
            }
            if (maxAlpha == 1)
                for (int i = 3; i < pixelBytes; i += 4)
                    if (bgra[i] != 0) bgra[i] = 255;

            prepared.Textures.Add(new PreparedTexture
            {
                Path = texturePath,
                Bgra = bgra,
                Pooled = true,
                Width = width,
                Height = height,
                MaxAlpha = maxAlpha,
            });
        }

        return prepared;
    }

    private unsafe UploadedWmo UploadPreparedWmo(
        GL gl, PreparedWmo prepared, IReadOnlyList<PreparedTexture> textures)
    {
        var uploaded = new UploadedWmo();

        foreach (var texture in textures)
        {
            try
            {
                uploaded.Textures[texture.Path] = texture.Bgra is null
                    ? null
                    : Texture.From2D(gl, texture.Bgra, texture.Width, texture.Height, ownerGl: _gl);
            }
            finally
            {
                if (texture is { Pooled: true, Bgra: not null })
                {
                    ArrayPool<byte>.Shared.Return(texture.Bgra);
                    texture.Bgra = null;
                    texture.Pooled = false;
                }
            }
        }

        foreach (var group in prepared.Groups)
        {
            if (group is null)
            {
                uploaded.Groups.Add(null);
                continue;
            }

            var vertices = BuildGroupVertexArray(group, out _, out _);
            var indices = group.Indices.ToArray();
            var gpu = new UploadedGroup
            {
                Vbo = gl.GenBuffer(),
                Ebo = gl.GenBuffer(),
            };

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.Vbo);
            fixed (float* p = vertices)
                gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gpu.Ebo);
            fixed (ushort* p = indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

            uploaded.Groups.Add(gpu);
        }

        return uploaded;
    }

    /// <summary>
    /// Build at most one WMO group. Returns true once the model is finalized.
    /// Group granularity keeps runtime preload work bounded while the asset is
    /// still beyond the fog boundary.
    /// </summary>
    private bool StepModelLoad(ModelLoadJob job, bool waitForUpload = false)
    {
        try { job.Ready ??= job.Worker.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo-preload] {Path.GetFileName(job.RootPath)} failed - {ex.Message}");
            job.Model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }
        var ready = job.Ready;
        if (ready.MissingRoot)
        {
            Console.WriteLine($"[wmo] not in MPQs: {job.RootPath}");
            _models[job.RootPath] = null;
            return true;
        }
        if (ready.Root is null)
        {
            Console.WriteLine($"[wmo] failed to parse root: {job.RootPath}");
            _models[job.RootPath] = null;
            return true;
        }

        job.MissingFiles = ready.MissingFiles;
        job.Unparsed = ready.Unparsed;
        job.Empty = ready.Empty;
        job.Antiportal = ready.Antiportal;

        // Carry the portal graph onto the Model before `ready` goes out of
        // scope. Copied by reference: the arrays are immutable after parse and
        // one WMO root is shared by every placement of it.
        job.Model.PortalVertices = ready.Root.PortalVertices;
        job.Model.Portals = ready.Root.Portals;
        job.Model.PortalRefs = ready.Root.PortalRefs;
        job.Model.DeclaredPortalCount = ready.Root.NPortals;
        job.Model.WmoId = ready.Root.WmoId;

        if (job.Upload is null)
        {
            var pendingTextures = ready.Textures
                .Where(t => !_textures.ContainsKey(t.Path))
                .ToList();
            foreach (var texture in ready.Textures)
                if (_textures.ContainsKey(texture.Path)) ReturnPreparedTexture(texture);
            job.Upload = _uploads.Enqueue(Path.GetFileName(job.RootPath), uploadGl =>
                UploadPreparedWmo(uploadGl, ready, pendingTextures));
        }
        if (waitForUpload && !job.Upload.IsCompleted)
            try { job.Upload.GetAwaiter().GetResult(); } catch { }
        if (!job.Upload.IsCompleted) return false;

        UploadedWmo uploaded;
        try { uploaded = job.Upload.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo-upload] {Path.GetFileName(job.RootPath)} failed - {ex.Message}");
            job.Model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }

        if (!job.UploadAccepted)
        {
            foreach (var (path, texture) in uploaded.Textures)
            {
                if (!_textures.ContainsKey(path)) _textures[path] = texture;
                var prepared = ready.Textures.First(t =>
                    t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (texture is null) _failedTextures.Add(path);
                else if (prepared.MaxAlpha == 0) _opaqueTextures.Add(path);
                else if (prepared.MaxAlpha == 1) _rescaledAlpha.Add(path);
            }
            job.UploadAccepted = true;
        }

        if (job.NextGroup < ready.Groups.Count)
        {
            int groupIndex = job.NextGroup++;
            var group = ready.Groups[groupIndex];
            if (group is null) return false;

            bool isDistanceLod = IsDistanceOnlyLod(
                job.RootPath, ready.Root.NGroups, group.GroupFlags,
                group.Vertices.Count, group.GroupName);
            var mesh = BuildGroupMesh(
                group, ready.Root, isDistanceLod, groupIndex, uploaded.Groups[groupIndex]);
            if (mesh is null) { job.Unbuilt++; return false; }

            job.BatchesDropped += _lastBatchesDropped;
            job.IndicesCovered += _lastIndicesCovered;
            job.IndicesTotal += _lastIndicesTotal;

            job.Model.Groups.Add(mesh);
            job.Model.TriangleCount += group.Indices.Count / 3;

            // PLAN_15. MLIQ has been parsed since the WMO reader was written and
            // never read; this is where that stops being true. Retained in LOCAL
            // space — see Model.Liquids. Note the group's own MOGP bbox is kept
            // alongside so the runtime escape check (PLAN_15 §7 step 1) can
            // reproduce the offline derivation without re-reading the file.
            if (group.Liquid is not null)
            {
                // Pair it with the mesh we just built, not with groupIndex: see
                // Model.Liquids for why an index would quietly misalign.
                job.Model.Liquids.Add((group.Liquid, mesh));
                _liquidGroupsSeen++;
                BumpLiquidVersion();   // adoption is async — see LiquidVersion
            }

            var groupCollision = new List<Vector3>();
            // Floor colours ride the same face set as the walking collision so the
            // interior-light probe lands on exactly the face the room verdict used.
            List<byte>? groupColors =
                mesh.IsInterior && (group.GroupFlags & 0x48u) == 0 &&
                group.VertexColors.Length >= group.Vertices.Count * 4
                    ? new List<byte>() : null;
            CollectCollision(group, groupCollision, job.Collision, job.Blockers, ref job.Skipped,
                groupColors);
            mesh.CollisionTriangles = [.. groupCollision];
            mesh.CollisionColors = groupColors is null ? [] : [.. groupColors];
            var cameraOnlyCollision = new List<Vector3>();
            CollectCameraOnlyCollision(group, cameraOnlyCollision);
            mesh.CameraOnlyTriangles = [.. cameraOnlyCollision];
            CollectFootstepSurfaces(group, ready.Root, mesh);
            return false;
        }

        var model = job.Model;
        model.CollisionTriangles = [.. job.Collision];
        model.BlockerTriangles = [.. job.Blockers];
        model.CollisionSkipped = job.Skipped;

        if (job.IndicesTotal > 0 && job.IndicesCovered < job.IndicesTotal)
        {
            // Under 100% is EXPECTED, not a fault: MOPY marks collision-only
            // triangles (materialId 0xFF) that are solid and never drawn, and
            // they are not in any MOBA batch. Every model measured sits around
            // 90%, which is the invisible-wall geometry. Only a sharp outlier
            // would mean anything.
            double covered = 100.0 * job.IndicesCovered / job.IndicesTotal;
            if (covered < 70.0)
                Console.WriteLine(
                    $"[wmo] {Path.GetFileName(job.RootPath)}: MOBA batches draw only {covered:F1}% of " +
                    $"indices — unusually low, most models sit near 90%");
        }

        if (job.MissingFiles + job.Unparsed + job.Empty + job.Antiportal +
            job.Unbuilt + job.BatchesDropped > 0)
        {
            Console.WriteLine(
                $"[wmo] {Path.GetFileName(job.RootPath)}: {model.Groups.Count}/{ready.Root.NGroups} group(s) drawn" +
                (job.MissingFiles > 0 ? $", {job.MissingFiles} file(s) not in MPQ" : "") +
                (job.Unparsed > 0 ? $", {job.Unparsed} failed to parse" : "") +
                (job.Empty > 0 ? $", {job.Empty} empty" : "") +
                (job.Antiportal > 0 ? $", {job.Antiportal} antiportal skipped" : "") +
                (job.Unbuilt > 0 ? $", {job.Unbuilt} mesh build failed" : "") +
                (job.BatchesDropped > 0 ? $", {job.BatchesDropped} batch(es) dropped" : ""));
        }

        if (model.Groups.Count == 0)
        {
            Console.WriteLine($"[wmo] no drawable groups: {job.RootPath}");
            model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }

        model.DoodadSets = ready.Root.DoodadSets;
        model.Doodads = ready.Root.Doodads;
        model.DoodadLight = BuildDoodadLighting(ready);
        model.DoodadOwners = BuildDoodadOwners(ready);

        int lodShells = model.Groups.Count(g => g.IsDistanceLod);
        if (lodShells > 0)
            Console.WriteLine($"[wmo-lod] {Path.GetFileName(job.RootPath)}: " +
                              $"{lodShells} distance-only shell group(s)");

        // One-time structural dump for large city WMOs. Identifies exactly which
        // group is that blue entrance roof / the cathedral shell so the right
        // ones can be classified as approach-only LOD instead of guessing. Local
        // centre lets a group be matched to what is on screen.
        if (DumpLargeWmoGroups && ready.Root.NGroups > 50)
        {
            Console.WriteLine($"[wmo-groups] {Path.GetFileName(job.RootPath)}: " +
                              $"{ready.Root.NGroups} group(s) [idx 'name' flags int/ext LOD verts localCentre]");
            for (int gi = 0; gi < ready.Groups.Count; gi++)
            {
                var g = ready.Groups[gi];
                if (g is null) continue;
                bool lod = IsDistanceOnlyLod(
                    job.RootPath, ready.Root.NGroups, g.GroupFlags,
                    g.Vertices.Count, g.GroupName);
                float cx = (g.BbMinX + g.BbMaxX) * 0.5f;
                float cy = (g.BbMinY + g.BbMaxY) * 0.5f;
                float cz = (g.BbMinZ + g.BbMaxZ) * 0.5f;
                Console.WriteLine(
                    $"[wmo-groups]  [{gi,3}] '{g.GroupName}' 0x{g.GroupFlags:X8} " +
                    $"{(g.IsInterior ? "INT" : "ext")}{(lod ? " LOD" : "")} " +
                    $"v={g.Vertices.Count} c=({cx:F0},{cy:F0},{cz:F0})");
            }
        }

        _models[job.RootPath] = model;
        ResidentVersion++;
        return true;
    }

    private static void ReturnPreparedTexture(PreparedTexture texture)
    {
        if (!texture.Pooled || texture.Bgra is null) return;
        ArrayPool<byte>.Shared.Return(texture.Bgra);
        texture.Bgra = null;
        texture.Pooled = false;
    }

    /// <summary>
    /// Pull the collidable triangles out of a group.
    ///
    /// MOPY carries one (flags, materialId) pair per triangle. The pieces that
    /// matter here:
    ///
    ///   flags &amp; 0x04  F_DETAIL - decorative geometry the real client does not
    ///                  collide against. Excluding it is what stops you bumping
    ///                  into mouldings and window tracery.
    ///   materialId 0xFF - a collision-only triangle: solid but never drawn.
    ///                  These are how invisible walls and door blockers exist.
    ///
    /// So MOBA batches decide what gets DRAWN and MOPY decides what is SOLID,
    /// out of one vertex array. That is exactly how the 1.12 client does it,
    /// and why it never needed a separate collision file.
    /// </summary>
    /// <param name="into">Every solid face (the group's own list: cells, floors, physics).</param>
    /// <param name="blockers">The material-0xFF subset, solid but never drawn - the world upload
    /// keeps them as a separate source so party sight never opens a hole onto one.</param>
    private static void CollectCollision(WmoGroupData group, List<Vector3> into, List<Vector3> drawn,
        List<Vector3> blockers,
        ref int skipped, List<byte>? colors = null)
    {
        int triangles = group.Indices.Count / 3;

        for (int t = 0; t < triangles; t++)
        {
            // A group without MOPY is treated as fully solid rather than
            // silently non-collidable: walking through a building is a much
            // worse failure than colliding with a moulding.
            bool blocker = false;
            if (t < group.TriMaterials.Count)
            {
                var (flags, material) = group.TriMaterials[t];
                if ((flags & 0x04) != 0) { skipped++; continue; }
                blocker = material == 0xFF;
            }

            int i0 = group.Indices[t * 3];
            int i1 = group.Indices[t * 3 + 1];
            int i2 = group.Indices[t * 3 + 2];

            if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count || i2 >= group.Vertices.Count)
                continue;

            var a = group.Vertices[i0];
            var b = group.Vertices[i1];
            var c = group.Vertices[i2];

            into.Add(new Vector3(a.x, a.y, a.z));
            into.Add(new Vector3(b.x, b.y, b.z));
            into.Add(new Vector3(c.x, c.y, c.z));
            if (colors is not null)
            {
                // VertexColors is RGBA per MOVT vertex (swizzled by the reader, then
                // FixVertexColors - the same bytes the walls draw with).
                AddColor(colors, group.VertexColors, i0);
                AddColor(colors, group.VertexColors, i1);
                AddColor(colors, group.VertexColors, i2);
            }
            var side = blocker ? blockers : drawn;
            {
                side.Add(new Vector3(a.x, a.y, a.z));
                side.Add(new Vector3(b.x, b.y, b.z));
                side.Add(new Vector3(c.x, c.y, c.z));
            }
        }
    }

    private static void AddColor(List<byte> into, byte[] rgba, int vertex)
    {
        into.Add(rgba[vertex * 4 + 0]);
        into.Add(rgba[vertex * 4 + 1]);
        into.Add(rgba[vertex * 4 + 2]);
    }

    /// <summary>
    /// Retain the exact camera-minus-walking face set for the current-room
    /// down-ray's guarded fallback: DETAIL set, NOCAMCOLLIDE clear. Unlike the
    /// walking gather, a missing MOPY entry cannot prove membership in this
    /// complement and is therefore skipped.
    /// </summary>
    private static void CollectCameraOnlyCollision(WmoGroupData group, List<Vector3> into)
    {
        int triangles = group.Indices.Count / 3;
        for (int t = 0; t < triangles; t++)
        {
            if (t >= group.TriMaterials.Count) continue;
            var (flags, _) = group.TriMaterials[t];
            if ((flags & 0x04) == 0 || (flags & 0x02) != 0) continue;

            int i0 = group.Indices[t * 3];
            int i1 = group.Indices[t * 3 + 1];
            int i2 = group.Indices[t * 3 + 2];
            if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count ||
                i2 >= group.Vertices.Count)
                continue;

            var a = group.Vertices[i0];
            var b = group.Vertices[i1];
            var c = group.Vertices[i2];
            into.Add(new Vector3(a.x, a.y, a.z));
            into.Add(new Vector3(b.x, b.y, b.z));
            into.Add(new Vector3(c.x, c.y, c.z));
        }
    }

    /// <summary>
    /// Retain the exact WMO render-material face set used by footstep surface
    /// sampling.  0x08 collision faces and 0x80 already-visited faces are
    /// rejected; material 0xFF/out-of-range is retained as TerrainType 0 so an
    /// owning interior remains silent instead of leaking through to ADT dirt.
    /// </summary>
    private static void CollectFootstepSurfaces(
        WmoGroupData group, WmoRootData root, GroupMesh mesh)
    {
        if ((group.GroupFlags & 0x48u) != 0 ||
            group.VertexColors.Length < group.Vertices.Count * 4) return;

        var triangles = new List<Vector3>();
        var terrainTypes = new List<uint>();
        List<byte>? colors = group.IsInterior ? new List<byte>() : null;
        int count = group.Indices.Count / 3;
        for (int face = 0; face < count; face++)
        {
            byte flags = 0;
            byte materialId = 0xFF;
            if (face < group.TriMaterials.Count)
                (flags, materialId) = group.TriMaterials[face];
            if ((flags & 0x88) != 0) continue;

            int i0 = group.Indices[face * 3];
            int i1 = group.Indices[face * 3 + 1];
            int i2 = group.Indices[face * 3 + 2];
            if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count ||
                i2 >= group.Vertices.Count) continue;

            var a = group.Vertices[i0];
            var b = group.Vertices[i1];
            var c = group.Vertices[i2];
            triangles.Add(new Vector3(a.x, a.y, a.z));
            triangles.Add(new Vector3(b.x, b.y, b.z));
            triangles.Add(new Vector3(c.x, c.y, c.z));
            terrainTypes.Add(materialId < root.Materials.Count
                ? root.Materials[materialId].GroundType
                : 0u);
            if (colors is not null)
            {
                AddColor(colors, group.VertexColors, i0);
                AddColor(colors, group.VertexColors, i1);
                AddColor(colors, group.VertexColors, i2);
            }
        }
        mesh.FootstepTriangles = [.. triangles];
        mesh.FootstepTerrainTypes = [.. terrainTypes];
        mesh.FootstepColors = colors is null ? [] : [.. colors];
    }

    /// <summary>
    /// Feed every placed building's collidable triangles into a collision
    /// world, using the SAME transform the renderer draws with.
    /// </summary>
    /// <summary>
    /// Record each placed building as a reference to its immutable collision
    /// geometry plus a transform. Does NOT expand triangles - that is the whole
    /// point (see CollisionBatch). Cheap enough to run on the render thread
    /// every time collision needs rebuilding.
    /// </summary>
    public int SnapshotCollision(List<CollisionBatch> into)
    {
        int placed = 0;

        foreach (var instance in _instances)
        {
            // Moving GameObject WMOs have an owner-aware live collision lane.
            // Baking one into this immutable snapshot strands a ghost hull at
            // whichever timetable pose happened to be current during rebuild.
            if (instance.DynamicGuid != 0) continue;
            var tris = instance.Model.CollisionTriangles;
            if (tris.Length < 3) continue;

            into.Add(new CollisionBatch(
                tris, instance.Transform, instance.Path, instance.Model.CollisionSkipped));
            // Collision-only faces as their own batch/source: solid for physics, never a
            // party sight target (World/PartySight.cs).
            if (instance.Model.BlockerTriangles.Length >= 3)
                into.Add(new CollisionBatch(
                    instance.Model.BlockerTriangles, instance.Transform, instance.Path + "#blocker", 0));
            placed++;
        }

        return placed;
    }

    /// <summary>
    /// Dev probe for "fell through the world here": every authored face of every resident WMO
    /// instance whose footprint contains the world XY, top to bottom, with its height at that
    /// point, MOPY flags, material and whether walking collision keeps it (DETAIL 0x04 = dropped).
    /// Re-reads the group files: this is what the DATA offers a falling body at that column.
    /// </summary>
    public void DumpFacesUnder(float x, float y)
    {
        foreach (Instance instance in _instances)
        {
            if (x < instance.WorldMin.X || x > instance.WorldMax.X || y < instance.WorldMin.Y || y > instance.WorldMax.Y) continue;
            Console.WriteLine($"[wmo-column] ({x:F1}, {y:F1}) inside {instance.Path} id={instance.Id} z {instance.WorldMin.Z:F0}..{instance.WorldMax.Z:F0}");
            byte[]? rootBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, instance.Path);
            WmoRootData? root = rootBytes is null ? null : WmoReader.ParseRoot(rootBytes);
            if (root is null) { Console.WriteLine("[wmo-column]   root unreadable"); continue; }
            string stem = instance.Path[..^4];
            var rows = new List<(float Z, string Line)>();
            // Whole-instance census of walkable-facing faces (n.z > 0.5) per group and MOPY flag:
            // where a stair's rendered treads are DETAIL, is there a solid ramp anywhere at all?
            var floors = new SortedDictionary<string, (int N, float Lo, float Hi)>();
            for (int g = 0; g < (int)root.NGroups; g++)
            {
                byte[]? groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, $"{stem}_{g:D3}.wmo");
                if (groupBytes is null) { Console.WriteLine($"[wmo-column]   g{g} unreadable"); continue; }
                WmoGroupData group;
                try { group = WmoReader.ParseGroup(groupBytes, root.Flags); } catch (Exception e) { Console.WriteLine($"[wmo-column]   g{g} parse failed: {e.Message}"); continue; }
                for (int t = 0; t < group.Indices.Count / 3; t++)
                {
                    int i0 = group.Indices[t * 3], i1 = group.Indices[t * 3 + 1], i2 = group.Indices[t * 3 + 2];
                    if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count || i2 >= group.Vertices.Count) continue;
                    var (ax, ay, az) = group.Vertices[i0]; var (bx, by, bz) = group.Vertices[i1]; var (cx, cy, cz) = group.Vertices[i2];
                    Vector3 a = Vector3.Transform(new Vector3(ax, ay, az), instance.Transform);
                    Vector3 b = Vector3.Transform(new Vector3(bx, by, bz), instance.Transform);
                    Vector3 c = Vector3.Transform(new Vector3(cx, cy, cz), instance.Transform);
                    float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    (byte fl0, byte mat0) = t < group.TriMaterials.Count ? group.TriMaterials[t] : ((byte)0, (byte)0);
                    if (Vector3.Normalize(Vector3.Cross(b - a, c - a)).Z > 0.5f)
                    {
                        string key = $"g{g} mopy=0x{fl0:X2}{(mat0 == 0xFF ? " mat=0xFF" : "")}";
                        float lo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), hi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                        floors[key] = floors.TryGetValue(key, out var f) ? (f.N + 1, MathF.Min(f.Lo, lo), MathF.Max(f.Hi, hi)) : (1, lo, hi);
                    }
                    if (MathF.Abs(d) < 1e-6f) continue;   // vertical face: no floor at a column
                    float l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                    float l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                    float l3 = 1f - l1 - l2;
                    if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                    float z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                    (byte flags, byte material) = t < group.TriMaterials.Count ? group.TriMaterials[t] : ((byte)0, (byte)0);
                    Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    rows.Add((z, $"[wmo-column]   z {z,7:F2}  g{g} gflags=0x{group.GroupFlags:X} tri{t} mopy=0x{flags:X2} mat={material} n.z={n.Z:F2} walk={((flags & 0x04) == 0 ? "KEPT" : "dropped(detail)")}"));
                }
            }
            foreach (var (_, line) in rows.OrderByDescending(r => r.Z)) Console.WriteLine(line);
            Console.WriteLine($"[wmo-column]   {rows.Count} face(s) over this column");
            foreach (var (key, f) in floors)
                Console.WriteLine($"[wmo-floors]   {key,-24} {f.N,5} upward face(s), z {f.Lo:F1}..{f.Hi:F1}");
        }
    }

    /// <summary>
    /// Dev probe: every authored group face of every resident WMO instance within
    /// <paramref name="radius"/> of a world point, with its MOPY flags and material id, plus how
    /// many of that instance's faces reached the walking collision. Re-reads the group files, so
    /// it shows what the DATA says regardless of what the gather kept.
    /// </summary>
    public void DumpFacesNear(Vector3 worldPos, float radius)
    {
        foreach (Instance instance in _instances)
            if (Vector3.Distance(instance.Origin, worldPos) < 80f)
                Console.WriteLine($"[wmo-faces] nearby instance {instance.Path} id={instance.Id} origin=({instance.Origin.X:F0},{instance.Origin.Y:F0},{instance.Origin.Z:F0}) bbox=({instance.WorldMin.X:F0},{instance.WorldMin.Y:F0},{instance.WorldMin.Z:F0})..({instance.WorldMax.X:F0},{instance.WorldMax.Y:F0},{instance.WorldMax.Z:F0}) groups={instance.Model.Groups.Count}");
        foreach (Instance instance in _instances)
        {
            if (worldPos.X < instance.WorldMin.X - radius || worldPos.X > instance.WorldMax.X + radius ||
                worldPos.Y < instance.WorldMin.Y - radius || worldPos.Y > instance.WorldMax.Y + radius ||
                worldPos.Z < instance.WorldMin.Z - radius || worldPos.Z > instance.WorldMax.Z + radius) continue;
            int collisionTris = instance.Model.Groups.Sum(g => g.CollisionTriangles.Length) / 3;
            Console.WriteLine($"[wmo-faces] instance {instance.Path} id={instance.Id} groups={instance.Model.Groups.Count} collisionTris={collisionTris} bbox=({instance.WorldMin.X:F0},{instance.WorldMin.Y:F0},{instance.WorldMin.Z:F0})..({instance.WorldMax.X:F0},{instance.WorldMax.Y:F0},{instance.WorldMax.Z:F0})");
            byte[]? rootBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, instance.Path);
            WmoRootData? root = rootBytes is null ? null : WmoReader.ParseRoot(rootBytes);
            if (root is null) { Console.WriteLine("[wmo-faces]   root unreadable"); continue; }
            string stem = instance.Path[..^4];
            int printed = 0;
            for (int g = 0; g < (int)root.NGroups && printed < 200; g++)
            {
                byte[]? groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, $"{stem}_{g:D3}.wmo");
                if (groupBytes is null) { Console.WriteLine($"[wmo-faces]   g{g} unreadable"); continue; }
                WmoGroupData group;
                try { group = WmoReader.ParseGroup(groupBytes, root.Flags); } catch (Exception e) { Console.WriteLine($"[wmo-faces]   g{g} parse failed: {e.Message}"); continue; }
                int tris = group.Indices.Count / 3;
                int nearAny = 0; float zMin = float.MaxValue, zMax = float.MinValue;
                for (int t = 0; t < tris; t++)
                {
                    int j0 = group.Indices[t * 3]; if (j0 >= group.Vertices.Count) continue;
                    var (vx, vy, vz) = group.Vertices[j0];
                    Vector3 w = Vector3.Transform(new Vector3(vx, vy, vz), instance.Transform);
                    if (Vector3.Distance(w, worldPos) > radius * 2f) continue;
                    nearAny++; zMin = MathF.Min(zMin, w.Z); zMax = MathF.Max(zMax, w.Z);
                }
                if (nearAny > 0) Console.WriteLine($"[wmo-faces]   g{g} \"{group.GroupName}\" gflags=0x{group.GroupFlags:X}: {nearAny} face(s) within {radius * 2f:F0} yd, z {zMin:F1}..{zMax:F1}");
                for (int t = 0; t < tris && printed < 200; t++)
                {
                    int i0 = group.Indices[t * 3], i1 = group.Indices[t * 3 + 1], i2 = group.Indices[t * 3 + 2];
                    if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count || i2 >= group.Vertices.Count) continue;
                    var (ax, ay, az) = group.Vertices[i0]; var (bx, by, bz) = group.Vertices[i1]; var (cx, cy, cz) = group.Vertices[i2];
                    Vector3 a = Vector3.Transform(new Vector3(ax, ay, az), instance.Transform);
                    Vector3 b = Vector3.Transform(new Vector3(bx, by, bz), instance.Transform);
                    Vector3 c = Vector3.Transform(new Vector3(cx, cy, cz), instance.Transform);
                    Vector3 centre = (a + b + c) / 3f;
                    if (Vector3.Distance(centre, worldPos) > radius) continue;
                    (byte flags, byte material) = t < group.TriMaterials.Count ? group.TriMaterials[t] : ((byte)0xFF, (byte)0xFF);
                    Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    Console.WriteLine($"[wmo-faces]   g{g} \"{group.GroupName}\" gflags=0x{group.GroupFlags:X} tri{t} mopy=0x{flags:X2} mat={material} n=({n.X:F2},{n.Y:F2},{n.Z:F2}) a=({a.X:F1},{a.Y:F1},{a.Z:F2}) b=({b.X:F1},{b.Y:F1},{b.Z:F2}) c=({c.X:F1},{c.Y:F1},{c.Z:F2})");
                    printed++;
                }
            }
            Console.WriteLine($"[wmo-faces]   {printed} face(s) within {radius:F1} yd (data)");
            // The RENDER mesh the picker keeps (what is on screen), per resident group.
            int rendered = 0;
            for (int gi = 0; gi < instance.Model.Groups.Count; gi++)
            {
                GroupMesh mesh = instance.Model.Groups[gi];
                int near = 0;
                for (int t = 0; t + 2 < mesh.PickIndices.Length; t += 3)
                {
                    Vector3 a = Vector3.Transform(mesh.PickPositions[mesh.PickIndices[t]], instance.Transform);
                    if (Vector3.Distance(a, worldPos) <= radius) near++;
                }
                if (near > 0)
                {
                    rendered += near;
                    Console.WriteLine($"[wmo-faces]   render group#{gi} has {near} face(s) near; collisionTris={mesh.CollisionTriangles.Length / 3} cameraOnly={mesh.CameraOnlyTriangles.Length / 3} pick={mesh.PickIndices.Length / 3}");
                }
            }
            Console.WriteLine($"[wmo-faces]   {rendered} render face(s) within {radius:F1} yd across {instance.Model.Groups.Count} group(s)");
        }
    }

    public void AppendCollision(CollisionWorld world)
    {
        int placed = 0, triangles = 0, skipped = 0;

        foreach (var instance in _instances)
        {
            var tris = instance.Model.CollisionTriangles;
            if (tris.Length < 3) continue;

            int source = world.RegisterSource(Path.GetFileName(instance.Path));
            var m = instance.Transform;

            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                world.AddTriangle(
                    Vector3.Transform(tris[i], m),
                    Vector3.Transform(tris[i + 1], m),
                    Vector3.Transform(tris[i + 2], m),
                    source);
                triangles++;
            }

            // Collision-only faces under their own source, so party sight can tell them apart.
            var blockers = instance.Model.BlockerTriangles;
            if (blockers.Length >= 3)
            {
                int blockerSource = world.RegisterSource(Path.GetFileName(instance.Path) + "#blocker");
                for (int i = 0; i + 2 < blockers.Length; i += 3)
                {
                    world.AddTriangle(
                        Vector3.Transform(blockers[i], m),
                        Vector3.Transform(blockers[i + 1], m),
                        Vector3.Transform(blockers[i + 2], m),
                        blockerSource);
                    triangles++;
                }
            }

            skipped += instance.Model.CollisionSkipped;
            placed++;
        }

        Console.WriteLine(
            $"[collision] from client geometry: {placed} building(s), " +
            $"{triangles:N0} solid triangles, {skipped:N0} detail triangles excluded");
    }

    /// <summary>Batches discarded by the most recent BuildGroupMesh call.</summary>
    private int _lastBatchesDropped;

    /// <summary>
    /// Indices the last group's batches actually draw, against how many it has.
    ///
    /// If MOBA does not cover the whole index buffer then some triangles are
    /// never submitted at all — which looks exactly like missing walls, loads
    /// without a single warning, and is invisible to any texture or culling
    /// theory. Worth measuring before guessing again.
    /// </summary>
    private int _lastIndicesCovered, _lastIndicesTotal;

    private unsafe GroupMesh? BuildGroupMesh(
        WmoGroupData group, WmoRootData root, bool isDistanceLod, int groupIndex,
        UploadedGroup? uploaded = null)
    {
        var vertices = BuildGroupVertexArray(group, out var min, out var max);

        var indices = group.Indices.ToArray();

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = uploaded?.Vbo ?? _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        if (uploaded is null)
        {
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        uint ebo = uploaded?.Ebo ?? _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        if (uploaded is null)
        {
            fixed (ushort* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        // Location 7, not 3: locations 3-6 are the four rows of the instancing
        // matrix and are claimed even when instancing is off for this draw.
        _gl.EnableVertexAttribArray(7);
        _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));

        _gl.BindVertexArray(0);

        var mesh = new GroupMesh
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            LocalMin = min,
            LocalMax = max,
            AuthoredLocalMin = new Vector3(group.BbMinX, group.BbMinY, group.BbMinZ),
            AuthoredLocalMax = new Vector3(group.BbMaxX, group.BbMaxY, group.BbMaxZ),
            IsInterior = group.IsInterior,
            IsDistanceLod = isDistanceLod,
            GroupIndex = groupIndex,
            GroupName = group.GroupName,
            GroupFlags = group.GroupFlags,
            GroupWmoId = group.GroupWmoId,
            GroupLiquid = group.GroupLiquid,
            PortalStart = group.PortalStart,
            PortalCount = group.PortalCount,
            VertexCount = group.Vertices.Count,
            PickPositions = BuildPickPositions(group),
            PickIndices = [.. group.Indices.Select(i => (int)i)],
            RenderedPickIndices = BuildRenderedIndices(group),
        };
        mesh.Attach(_gl);

        // MOBA batches: each is a run of indices sharing one material.
        _lastBatchesDropped = 0;
        _lastIndicesCovered = 0;
        _lastIndicesTotal = indices.Length;

        // MOBA is ordered transparent, then interior, then exterior, and the
        // three MOGP counts say where the boundaries are. When they do not add
        // up to the table we actually got, trust nothing and light the whole
        // group the old way rather than guessing a boundary.
        bool hasColors = group.VertexColors.Length >= group.Vertices.Count * 4;
        int transEnd = group.TransBatchCount;
        int intEnd = transEnd + group.IntBatchCount;
        bool countsUsable = hasColors
            && intEnd + group.ExtBatchCount == group.Batches.Count
            && group.Batches.Count > 0;

        for (int bi = 0; bi < group.Batches.Count; bi++)
        {
            var b = group.Batches[bi];
            if (b.IndexCount == 0) { _lastBatchesDropped++; continue; }
            if (b.IndexStart + b.IndexCount > (uint)indices.Length) { _lastBatchesDropped++; continue; }

            var material = b.MaterialId < root.Materials.Count ? root.Materials[b.MaterialId] : null;
            var texture = material is null ? null : ResolveMaterialTexture(material);

            if (texture is null) _texturelessBatches++;

            _lastIndicesCovered += (int)b.IndexCount;

            mesh.Batches.Add(new Batch
            {
                IndexStart = b.IndexStart,
                IndexCount = b.IndexCount,
                Texture = texture,
                TwoSided = material?.IsNoCull ?? false,
                // The window/lamp law (reference glMaterialfv(GL_EMISSION)): UNLIT batches ignore
                // the scene light, SIDN batches add their authored colour × the night fraction.
                // Neither used to reach the shader, so every pane and lamp head went dark at night.
                Unlit = material?.IsUnlit ?? false,
                Sidn = material is { IsSidn: true } ? material.SidnColor : Vector3.Zero,
                // WoWee follows MOMT, rather than guessing from BLP contents:
                // mode 0 is opaque, mode 1 is alpha-key, modes 2+ blend.
                // Cutting every texture that merely contains alpha is what
                // turned ordinary walls and roofs into torn paper.
                AlphaTest = material?.BlendMode == 1 && MaterialHasAlpha(material),
                Transparent = material?.BlendMode >= 2,
                Type = !countsUsable ? 3 : bi < transEnd ? 1 : bi < intEnd ? 2 : 3,
            });
        }

        // No batches means nothing references this geometry — draw it whole
        // rather than dropping it, so a WMO with an odd MOBA table still shows.
        if (mesh.Batches.Count == 0)
        {
            mesh.Batches.Add(new Batch
            {
                IndexStart = 0,
                IndexCount = (uint)indices.Length,
                Texture = null,
                TwoSided = true,
            });
        }

        return mesh;
    }

    /// <summary>
    /// Whether a group is a distance-only impostor shell. Evaluated live at draw
    /// time (not baked at load) so the HUD's "Impostor max verts" slider retunes
    /// the whole city without a reload.
    ///
    /// The real distance impostors turned out to be flagged INTERIOR, which the
    /// old !indoor guards threw away. The Stormwind group dump proved it:
    ///   [302] 'Cathedral of Light' 0x00012841 INT v=96   (ALWAYS_DRAW set)
    ///   [303] 'Taventrance16'      0x00012841 INT v=48
    ///   [304] 'Old Town'           0x00012841 INT v=48
    /// These are the "double cathedral" / distant-district silhouettes Blizzard
    /// shows on approach. The signal is ALWAYS_DRAW (0x10000) + a low vertex
    /// count, NOT the interior/exterior bit: a genuine interior room is never
    /// ALWAYS_DRAW. So an ALWAYS_DRAW low-poly group is a shell regardless of
    /// the indoor flag; the detailed versions (e.g. 'Taventrance15' v=2368, no
    /// ALWAYS_DRAW) are untouched.
    /// </summary>
    private bool IsDistanceOnlyLod(
        string rootPath, uint nGroups, uint flags, int vertexCount, string groupName)
    {
        if (nGroups <= 50) return false;

        bool indoor = (flags & 0x2000) != 0;
        bool alwaysDraw = (flags & 0x00010000) != 0;
        string name = (groupName ?? "").ToLowerInvariant();
        bool facade = name.Contains("facade", StringComparison.Ordinal);
        bool cityShell = name.StartsWith("city", StringComparison.Ordinal) && name.Length <= 8;
        bool stormwind = Path.GetFileName(rootPath)
            .Equals("stormwind.wmo", StringComparison.OrdinalIgnoreCase);
        bool cathedralShell = stormwind && indoor && (flags & 0x80) != 0;
        bool alwaysDrawImpostor = alwaysDraw && vertexCount < ImpostorMaxVertices;

        return (vertexCount < 100 && !indoor)
            || (alwaysDraw && vertexCount < 5000 && !indoor)
            || alwaysDrawImpostor
            || (facade && !indoor)
            || (cityShell && !indoor)
            || cathedralShell;
    }

    private static float[] BuildGroupVertexArray(
        WmoGroupData group, out Vector3 min, out Vector3 max)
    {
        var vertices = new float[group.Vertices.Count * FloatsPerVertex];
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        for (int i = 0; i < group.Vertices.Count; i++)
        {
            var v = group.Vertices[i];
            var n = i < group.Normals.Count ? group.Normals[i] : (x: 0f, y: 0f, z: 1f);
            var uv = i < group.UVs.Count ? group.UVs[i] : (u: 0f, v: 0f);

            // No MOCV: 0.5 is neutral, because the render path doubles it.
            // Those groups are classified exterior anyway, so the shader never
            // reads this - it just must not be black if something slips through.
            int c = i * 4;
            bool hasColor = c + 3 < group.VertexColors.Length;
            float cr = hasColor ? group.VertexColors[c + 0] / 255f : 0.5f;
            float cg = hasColor ? group.VertexColors[c + 1] / 255f : 0.5f;
            float cb = hasColor ? group.VertexColors[c + 2] / 255f : 0.5f;
            float ca = hasColor ? group.VertexColors[c + 3] / 255f : 1.0f;

            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.x;
            vertices[o + 1] = v.y;
            vertices[o + 2] = v.z;
            vertices[o + 3] = n.x;
            vertices[o + 4] = n.y;
            vertices[o + 5] = n.z;
            vertices[o + 6] = uv.u;
            vertices[o + 7] = uv.v;
            vertices[o + 8] = cr;
            vertices[o + 9] = cg;
            vertices[o + 10] = cb;
            vertices[o + 11] = ca;

            var p = new Vector3(v.x, v.y, v.z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return vertices;
    }

    /// <summary>
    /// A material's diffuse texture, trying every slot it has.
    ///
    /// MOMT carries three texture offsets. Slot 0 is the diffuse on most
    /// materials, but not all — and reading only slot 0 meant any material that
    /// leaves it empty rendered with the shader's untextured fallback colour.
    /// That is what the pale patches all over the Goldshire inn were: not
    /// missing geometry, not missing files, just a texture nobody asked for.
    /// </summary>
    private Texture? ResolveMaterialTexture(WmoMaterial material)
        => ResolveTexture(material.Texture0Name)
        ?? ResolveTexture(material.Texture1Name)
        ?? ResolveTexture(material.Texture2Name);

    private Texture? ResolveTexture(string blpPath)
    {
        if (string.IsNullOrWhiteSpace(blpPath)) return null;
        if (_textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null)
        {
            // Collected rather than printed: one line per failure buries the
            // pattern, and the pattern is the useful part.
            _failedTextures.Add(blpPath);
            _textures[blpPath] = null;
            return null;
        }

        var (bgra, w, h) = decoded.Value;

        // ALPHA SANITY, and the reason the buildings were full of holes.
        //
        // The shader discards fragments below a cutoff, which is how railings
        // and window tracery work. But some BLPs come back from the decoder
        // with an alpha channel that is not on a 0..255 scale: a 1-bit alpha
        // texture decoded as 0 or 1 gives 0.004 in the shader, which fails a
        // 0.35 cut on EVERY texel. The wall loads, textures correctly, and
        // renders as nothing at all.
        //
        // So look at what actually came back:
        //   max == 0        no alpha channel — mark opaque, never cut it
        //   max == 1        1-bit alpha decoded to 0/1 — rescale to 0/255 so
        //                   the cutout works as intended instead of erasing
        //                   the surface
        //   otherwise       a real 8-bit alpha channel, use it as is
        //
        // The proper fix belongs in BlpDecoder, which should be emitting 0/255
        // for 1-bit alpha in the first place. This is a guard at the point of
        // use, not a substitute for that.
        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
            if (maxAlpha > 1) break;
        }

        if (maxAlpha == 0)
        {
            _opaqueTextures.Add(blpPath);
        }
        else if (maxAlpha == 1)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] != 0) bgra[i] = 255;
        }

        UploadPreparedTexture(new PreparedTexture
        {
            Path = blpPath,
            Bgra = bgra,
            Width = w,
            Height = h,
            MaxAlpha = maxAlpha,
        });
        return _textures.GetValueOrDefault(blpPath);
    }

    private void UploadPreparedTexture(PreparedTexture prepared)
    {
        if (_textures.ContainsKey(prepared.Path)) return;
        if (prepared.Bgra is null)
        {
            _failedTextures.Add(prepared.Path);
            _textures[prepared.Path] = null;
            return;
        }

        if (prepared.MaxAlpha == 0) _opaqueTextures.Add(prepared.Path);
        else if (prepared.MaxAlpha == 1) _rescaledAlpha.Add(prepared.Path);

        _textures[prepared.Path] = Texture.From2D(
            _gl, prepared.Bgra, prepared.Width, prepared.Height);
    }

    /// <summary>Does the material's resolved texture carry a real alpha channel?</summary>
    private bool MaterialHasAlpha(WmoMaterial? material)
    {
        if (material is null) return false;

        foreach (var name in new[] { material.Texture0Name, material.Texture1Name, material.Texture2Name })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!_textures.TryGetValue(name, out var t) || t is null) continue;
            return !_opaqueTextures.Contains(name);
        }

        return false;
    }

    /// <summary>
    /// Inverse of <see cref="Basis"/>: placement space back into WMO local space.
    ///
    /// An embedded doodad is an M2, and M2 render vertices are Y-up while WMO
    /// vertices are Z-up. MODD positions live in WMO space, so the M2 has to be
    /// rotated INTO that space before the MODD transform, and the building's
    /// own Basis then carries the whole assembly back out. Basis composed with
    /// this is the identity, which is the check that it is the right matrix.
    /// </summary>
    private static readonly Matrix4x4 M2ToWmo = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    /// <summary>
    /// "No baked light, full daylight" — the encoding a doodad gets when it is
    /// not owned by an interior group, and the value OpenGL itself supplies for
    /// a disabled vertex attribute. Terrain doodads therefore need no special
    /// case: they land on this by default and render exactly as before.
    /// </summary>
    private static readonly Vector4 ExteriorDoodadLight = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// Resolve each doodad's baked interior light from MODD.color, gated by
    /// which group owns it (MODR).
    ///
    /// MODD.color is NOT a tint. The wiki calls it one; measuring says
    /// otherwise. Sampling the group's own MOCV floor directly beneath each
    /// doodad and correlating that against the shipped MODD.color gives
    /// r = 0.89 over 4929 channel samples, and the ratio sits at ~1.0 across
    /// every brightness bucket — MODD.color lives on the same scale as RAW
    /// MOCV. Two instances of the SAME model in the same building carry
    /// different colours (BARREL01 as (60,60,60) in one room and (114,113,110)
    /// in another), which a tint could not do. It is Blizzard's own pre-baked
    /// answer to "how lit is this prop", computed once at map-compile time.
    ///
    /// Scale: the retail client halves MOCV at load and doubles it at draw, so
    /// CMapObj::QueryLighting effectively returns 2x the raw stored value. Our
    /// wall path skips the halving and multiplies by VertexColorScale = 2.0 at
    /// draw instead, landing on the same place. Since MODD.color is on the raw
    /// MOCV scale, doodads feed color/255 into that SAME 2.0 — never
    /// pre-doubled, which would give 4x. This preserves the invariant that
    /// actually shows on screen: a barrel matches the floor it stands on.
    ///
    /// No MOHD.ambColor is added on top. The wall path does not add it either,
    /// and in classic-era data the ambient is already baked into MOCV.
    ///
    /// The gate matters. Measured across 335 WMO roots / 70,228 placements:
    /// interior-owned doodads average RGB (116,103,99) with 0.1% black, while
    /// exterior-owned ones average (51,51,63) with 12.7% pure black — because
    /// the client never reads those, so nobody ever checked them. Applying
    /// MODD.color ungated would black out every lamp post in the game. Zero
    /// doodads were unreferenced by MODR, so no fallback is needed.
    /// </summary>
    private static Vector4[] BuildDoodadLighting(PreparedWmo ready)
    {
        var root = ready.Root;
        if (root is null || root.Doodads.Count == 0) return [];

        var light = new Vector4[root.Doodads.Count];
        for (int i = 0; i < light.Length; i++) light[i] = ExteriorDoodadLight;

        foreach (var group in ready.Groups)
        {
            if (group is null || group.DoodadRefs.Count == 0) continue;
            if (!group.IsInterior) continue;

            // EXTERIOR (0x8) and EXTERIOR_LIT (0x40) both mean "use daylight".
            // CMapObj::QueryLighting rejects such a group outright and falls
            // back to the outdoor sun, so a doodad inside one must too.
            if ((group.GroupFlags & 0x48) != 0) continue;

            foreach (ushort index in group.DoodadRefs)
            {
                if (index >= light.Length) continue;
                var d = root.Doodads[index];
                light[index] = new Vector4(
                    d.ColorR / 255f, d.ColorG / 255f, d.ColorB / 255f, 0f);
            }
        }

        return light;
    }

    private static int[][] BuildDoodadOwners(PreparedWmo ready)
    {
        int count = ready.Root?.Doodads.Count ?? 0;
        if (count == 0) return [];

        var owners = new List<int>[count];
        for (int groupIndex = 0; groupIndex < ready.Groups.Count; groupIndex++)
        {
            var group = ready.Groups[groupIndex];
            if (group is null) continue;
            foreach (ushort doodadIndex in group.DoodadRefs)
            {
                if (doodadIndex >= count) continue;
                (owners[doodadIndex] ??= []).Add(groupIndex);
            }
        }

        var result = new int[count][];
        for (int i = 0; i < count; i++) result[i] = owners[i]?.Distinct().ToArray() ?? [];
        return result;
    }

    /// <summary>
    /// Every embedded doodad of every placed building, as a model path, a
    /// world transform and its baked light, ready to hand to the doodad
    /// renderer.
    ///
    /// Set 0 is "$DefaultGlobal" and is always present; a placement may name a
    /// second set on top of it, which is how one tavern model furnishes
    /// differently in different towns.
    /// </summary>
    public IEnumerable<(string ModelPath, Matrix4x4 Transform, Vector4 Light,
        int WmoInstanceId, int[] OwnerGroups)> EnumerateDoodads()
        => EnumerateDoodads(Vector2.Zero, float.PositiveInfinity);

    /// <summary>Set-0 MODD props belonging to one WMO-display GameObject.
    /// PropIndex is the stable root-MODD index used for owner-scoped dynamic M2
    /// reconciliation; every returned transform already follows the hull.</summary>
    public IEnumerable<(int PropIndex, string ModelPath, Matrix4x4 Transform,
        Vector4 Light, int WmoInstanceId, int[] OwnerGroups)>
        EnumerateDynamicDoodads(ulong guid)
    {
        if (!_dynamicByGuid.TryGetValue(guid, out Instance? instance)) yield break;
        Model model = instance.Model;
        if (model.Doodads.Count == 0) yield break;

        foreach (int setIndex in DoodadSetsFor(model, 0))
        {
            WmoDoodadSet set = model.DoodadSets[setIndex];
            for (uint i = 0; i < set.DoodadCount; i++)
            {
                uint index = set.FirstInstanceIndex + i;
                if (index >= model.Doodads.Count) break;
                WmoDoodadDef d = model.Doodads[(int)index];
                if (string.IsNullOrWhiteSpace(d.ModelPath)) continue;
                Matrix4x4 local = M2ToWmo
                    * Matrix4x4.CreateScale(d.Scale > 0.0001f ? d.Scale : 1f)
                    * Matrix4x4.CreateFromQuaternion(
                        new Quaternion(d.QuatX, d.QuatY, d.QuatZ, d.QuatW))
                    * Matrix4x4.CreateTranslation(d.PosX, d.PosY, d.PosZ);
                Vector4 light = index < (uint)model.DoodadLight.Length
                    ? model.DoodadLight[(int)index]
                    : ExteriorDoodadLight;
                int[] owners = index < (uint)model.DoodadOwners.Length
                    ? model.DoodadOwners[(int)index]
                    : [];
                yield return ((int)index, d.ModelPath, local * instance.Transform,
                    light, instance.Id, owners);
            }
        }
    }

    /// <summary>Whether an embedded prop's owning room is in this frame's PVS.
    /// Missing/invalid portal state fails open, matching the WMO group fallback.</summary>
    public bool IsDoodadPortalVisible(int wmoInstanceId, int[] ownerGroups)
    {
        if (!UsePortalCulling || ownerGroups.Length == 0) return true;
        if (!_portalVisible.TryGetValue(wmoInstanceId, out var visible) || visible is null) return true;
        foreach (int group in ownerGroups)
            if (visible.Contains(group)) return true;
        return false;
    }

    /// <summary>
    /// Whether a world point belongs to one of the rooms in this frame's portal
    /// flood. This is deliberately an AABB-only admission test: callers use it
    /// to avoid expensive room/floor triangle probes for work that cannot be
    /// seen. Missing portal state and doorway ambiguity fail open.
    /// </summary>
    public bool IsPointPortalVisible(Vector3 worldPoint, float margin = 3f)
    {
        if (!UsePortalCulling ||
            CameraGroup is not { IsExterior: false } cameraCell ||
            !_portalVisible.TryGetValue(cameraCell.InstanceId,
                out HashSet<int>? visible) || visible is null)
            return true;

        Instance? instance = _instances.FirstOrDefault(i => i.Id == cameraCell.InstanceId);
        if (instance is null) return true;

        // A point outside the camera's WMO may be visible through an exterior
        // portal. The room PVS cannot classify it, so preserve the outdoor path.
        float m = MathF.Max(0f, margin);
        if (worldPoint.X < instance.WorldMin.X - m || worldPoint.X > instance.WorldMax.X + m ||
            worldPoint.Y < instance.WorldMin.Y - m || worldPoint.Y > instance.WorldMax.Y + m ||
            worldPoint.Z < instance.WorldMin.Z - m || worldPoint.Z > instance.WorldMax.Z + m)
            return true;

        if (!Matrix4x4.Invert(instance.Transform, out Matrix4x4 worldToLocal)) return true;
        Vector3 local = Vector3.Transform(worldPoint, worldToLocal);
        bool belongsToGroup = false;
        foreach (GroupMesh group in instance.Model.Groups)
        {
            if (group.IsDistanceLod ||
                local.X < group.LocalMin.X - m || local.X > group.LocalMax.X + m ||
                local.Y < group.LocalMin.Y - m || local.Y > group.LocalMax.Y + m ||
                local.Z < group.LocalMin.Z - m || local.Z > group.LocalMax.Z + m)
                continue;

            belongsToGroup = true;
            if (visible.Contains(group.GroupIndex)) return true;
        }

        // Bounds overlap at stairs and doors, so reject only when the point was
        // positively claimed by one or more groups and all of them were hidden.
        return !belongsToGroup;
    }

    // ── WMO liquid (PLAN_15_WMO_LIQUID.md) ──────────────────────────────────

    /// <summary>
    /// MLIQ-bearing groups adopted so far. Exposed rather than private because a
    /// private counter that is only ever incremented is a CS0414 warning, and this
    /// project builds at zero warnings — but it is genuinely useful too: it is the
    /// count BEFORE placement, so comparing it against
    /// LiquidRenderer.WmoSurfaceCount separates "the model has no liquid" from
    /// "the liquid was dropped on the way to the mesh".
    /// </summary>
    public int LiquidGroupsAdopted => _liquidGroupsSeen;

    private int _liquidGroupsSeen;

    /// <summary>
    /// Bumped whenever the set of placed instances OR the liquid content of any
    /// resident model changes.
    ///
    /// LiquidRenderer rebuilds its WMO surfaces when this moves, rather than on
    /// the tile-crossing event. That distinction matters and is not paranoia:
    /// a WMO is placed the instant its ADT is read but its groups are adopted
    /// asynchronously over later frames, so a rebuild fired on the crossing runs
    /// BEFORE Model.Liquids is populated and produces an empty canal that never
    /// refills. SYSTEM_INSTANCES.md records the same race on async doors, where
    /// it also failed silently.
    /// </summary>
    public int LiquidVersion { get; private set; }

    private void BumpLiquidVersion() => LiquidVersion++;

    /// <summary>
    /// Every MLIQ surface of every placed instance, converted to WORLD space.
    ///
    /// Mirrors <see cref="EnumerateDoodads()"/>: this class owns placement, the
    /// liquid renderer owns drawing. Keeping the draw in LiquidRenderer is what
    /// stops a canal from drifting out of sync with the open-world river — they
    /// share one shader, one uniform block and one tuning HUD.
    ///
    /// Vertices come out as a (XVerts x YVerts) grid, row-major over j then i,
    /// matching <see cref="WmoLiquid.VertexHeights"/>. The local layout is
    /// PLAN_15 §4.1, derived against 235 real groups:
    ///
    ///     local(i, j) = (CornerX + i*UNIT, CornerY + j*UNIT, HeightAt(i, j))
    ///
    /// Z-up, same space as MOVT. The docstring on WmoLiquid used to say Y-up and
    /// was wrong by a factor of 18 on the containment score.
    /// </summary>
    public IEnumerable<WmoLiquidSurface> EnumerateLiquid()
    {
        const float unit = 33.3333f / 8.0f;   // PROVEN, PLAN_15 §4.2 — 470/470 corners snap to it

        foreach (var instance in _instances)
        {
            var model = instance.Model;
            if (model.Liquids.Count == 0) continue;

            foreach (var (liq, mesh) in model.Liquids)
            {
                if (liq.XVerts < 2 || liq.YVerts < 2) continue;

                var world = new Vector3[liq.XVerts * liq.YVerts];
                for (int j = 0; j < liq.YVerts; j++)
                for (int i = 0; i < liq.XVerts; i++)
                {
                    var local = new Vector3(
                        liq.CornerX + i * unit,
                        liq.CornerY + j * unit,
                        liq.HeightAt(i, j));
                    world[j * liq.XVerts + i] = Vector3.Transform(local, instance.Transform);
                }

                var (groupWorldMin, _) = TransformedBounds(
                    mesh.AuthoredLocalMin, mesh.AuthoredLocalMax, instance.Transform);
                yield return new WmoLiquidSurface(
                    instance.Id, instance.Path, mesh.GroupIndex, mesh.GroupName,
                    groupWorldMin.Z, mesh.GroupLiquid, liq, world);
            }
        }
    }

    /// <summary>
    /// Diagnostic for PLAN_15 §7 step 1: how far each MLIQ surface falls outside
    /// the authored MOGP bounding box of the group that owns it, in LOCAL space.
    ///
    /// One deliberate difference from the offline run: this compares against
    /// GroupMesh.LocalMin/Max, which are derived from the MOVT vertices, whereas
    /// the offline scorer used the authored MOGP box at +0x0C/+0x18. On the
    /// groups sampled the two agree to about a tenth of a yard, so the totals are
    /// comparable — but they are not the same quantity and a small drift between
    /// the runtime and offline numbers is expected rather than alarming.
    ///
    /// This is the metric that settled the coordinate convention offline,
    /// recomputed at runtime so the two can be compared. Expect it to reproduce:
    /// 103 of 235 groups at exactly 0.00 and a worst case near 108 yards, on
    /// groups whose pool genuinely overhangs its render geometry. **A wildly
    /// larger number means the instance transform is wrong, not the convention** —
    /// the convention is settled and is not the thing to go back and doubt.
    /// </summary>
    public (int surfaces, double totalEscape, double worstEscape, string worstName) LiquidEscapeCheck()
    {
        const float unit = 33.3333f / 8.0f;
        int n = 0;
        double total = 0, worst = 0;
        string worstName = "";

        foreach (var instance in _instances)
        foreach (var (liq, g) in instance.Model.Liquids)
        {
            if (liq.XVerts < 2 || liq.YVerts < 2) continue;

            var mn = new Vector3(
                liq.CornerX, liq.CornerY,
                liq.VertexHeights.Length == 0 ? 0f : liq.VertexHeights.Min());
            var mx = new Vector3(
                liq.CornerX + (liq.XVerts - 1) * unit,
                liq.CornerY + (liq.YVerts - 1) * unit,
                liq.VertexHeights.Length == 0 ? 0f : liq.VertexHeights.Max());

            double esc =
                  Math.Max(0, g.LocalMin.X - mn.X) + Math.Max(0, mx.X - g.LocalMax.X)
                + Math.Max(0, g.LocalMin.Y - mn.Y) + Math.Max(0, mx.Y - g.LocalMax.Y)
                + Math.Max(0, g.LocalMin.Z - mn.Z) + Math.Max(0, mx.Z - g.LocalMax.Z);

            n++;
            total += esc;
            if (esc > worst)
            {
                worst = esc;
                worstName = $"{Path.GetFileName(instance.Path)} [{g.GroupIndex}] '{g.GroupName}'";
            }
        }

        return (n, total, worst, worstName);
    }

    /// <summary>
    /// Embedded doodads near a streaming centre. A huge WMO may intersect the
    /// resident ADT ring while most of its furniture is hundreds of yards away;
    /// filtering individual MODD transforms prevents that furniture from
    /// dominating startup.
    /// </summary>
    public IEnumerable<(string ModelPath, Matrix4x4 Transform, Vector4 Light,
        int WmoInstanceId, int[] OwnerGroups)> EnumerateDoodads(
        Vector2 centre, float maxDistance)
    {
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (var instance in _instances)
        {
            // Dynamic WMO GameObjects need owner-keyed prop instances which
            // follow every transform update. The residency population path is
            // static and would strand their MODD props at one sampled pose.
            if (instance.DynamicGuid != 0) continue;
            var model = instance.Model;
            if (model.Doodads.Count == 0) continue;

            foreach (int setIndex in DoodadSetsFor(model, instance.DoodadSet))
            {
                var set = model.DoodadSets[setIndex];

                for (uint i = 0; i < set.DoodadCount; i++)
                {
                    uint index = set.FirstInstanceIndex + i;
                    if (index >= model.Doodads.Count) break;

                    var d = model.Doodads[(int)index];
                    if (string.IsNullOrWhiteSpace(d.ModelPath)) continue;

                    var local =
                        M2ToWmo
                        * Matrix4x4.CreateScale(d.Scale > 0.0001f ? d.Scale : 1f)
                        * Matrix4x4.CreateFromQuaternion(new Quaternion(d.QuatX, d.QuatY, d.QuatZ, d.QuatW))
                        * Matrix4x4.CreateTranslation(d.PosX, d.PosY, d.PosZ);

                    var transform = local * instance.Transform;
                    if (!float.IsPositiveInfinity(maxDistance))
                    {
                        var delta = new Vector2(transform.M41, transform.M42) - centre;
                        if (delta.LengthSquared() > maxDistanceSq) continue;
                    }

                    var light = index < (uint)model.DoodadLight.Length
                        ? model.DoodadLight[(int)index]
                        : ExteriorDoodadLight;

                    int[] owners = index < (uint)model.DoodadOwners.Length
                        ? model.DoodadOwners[(int)index]
                        : [];
                    yield return (d.ModelPath, transform, light, instance.Id, owners);
                }
            }
        }
    }

    private static IEnumerable<int> DoodadSetsFor(Model model, int requested)
    {
        if (model.DoodadSets.Count == 0) yield break;

        yield return 0;

        if (requested > 0 && requested < model.DoodadSets.Count)
            yield return requested;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public void Render(Camera camera) => Render(camera, null);

    /// <summary>
    /// Draw buildings with an optional absolute-world clip plane. The caller
    /// owns GL_CLIP_DISTANCE0 state so this can be scoped to an isolated portal
    /// candidate without changing active-world rendering.
    /// </summary>
    public unsafe void Render(Camera camera, WorldClipPlane? worldClipPlane)
    {
        long started = Stopwatch.GetTimestamp();
        DrawnLastFrame = 0;
        VisibleGroupsLastFrame = 0;
        LodGroupsCulledLastFrame = 0;
        DrawCallsLastFrame = 0;
        TrianglesLastFrame = 0;
        _frameLargestWmoGroupCount = 0;
        OccludedGroupsLastFrame = 0;
        PortalReachedLastFrame = 0;
        _portalVisible.Clear();
        if (!Enabled || _shader is null || _instances.Count == 0)
        {
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uWorldClipPlane", worldClipPlane is { IsValid: true } clip
            ? clip.RelativeEquation(camera.Position)
            : new Vector4(0f, 0f, 0f, 1f));
        _shader.Set("uUseInstancing", 0);
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uCutActive", ActiveCut is not null ? 1 : 0);
        _shader.Set("uCutRect", ActiveCut?.RelativeRect(camera.Position) ?? Vector4.Zero);
        _shader.Set("uCutZ", ActiveCut?.RelativeZ(camera.Position) ?? 0f);
        _shader.Set("uStageActive", Stage is not null ? 1 : 0);
        _shader.Set("uStageCentre", Stage is { } stage ? stage.Centre - camera.Position : Vector3.Zero);
        _shader.Set("uStageRadius", Stage?.Radius ?? 0f);
        _shader.Set("uStageHalfHeight", Stage?.HalfHeight ?? 0f);
        SetSightUniforms(camera.Position);
        if (PartySight is { } partySight) partySight.Apply(_shader, camera.Position);
        else PartySightPass.BindInactive(_shader);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTexture", 0);
        _shader.Set("uVertexColorScale", VertexColorScale);
        _shader.Set("uInteriorBrightness", InteriorBrightness);
        _shader.Set("uStyleWeight", 0.62f);
        CarriedLightFrame.Upload(_shader, camera.Position);

        var viewProjection = camera.RelativeViewProjection;
        var cameraPosition = camera.Position;
        _shader.Set("uPortalLightPos",
            PortalLightRadius > 0f ? PortalLightWorldPos - cameraPosition : Vector3.Zero);
        _shader.Set("uPortalLightColor", PortalLightColor);
        _shader.Set("uPortalLightRadius", PortalLightRadius);
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);
        bool cullingOn = true;

        // ── FRONT TO BACK ────────────────────────────────────────────────────
        //
        // _instances is in placement order, which is to say arbitrary. Drawing a
        // city that way means the far side of it shades every pixel and is then
        // painted over by the near side — on hardware with no hidden-surface
        // removal, that is the difference between one shaded fragment per pixel
        // and five.
        //
        // Nearest first lets early-Z reject the rest for free. It costs one sort
        // of a few dozen instances per frame, on a list that is reused so the
        // sort allocates nothing.
        _drawOrder.Clear();
        foreach (var candidate in _instances)
        {
            if (FrustumCulling &&
                !Camera.BoxInFrustum(viewProjection,
                    candidate.WorldMin - cameraPosition,
                    candidate.WorldMax - cameraPosition)) continue;

            _drawOrder.Add((DistanceToBox(cameraPosition, candidate.WorldMin, candidate.WorldMax),
                            candidate));
        }
        _drawOrder.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        _instanceSlices.Clear();
        _flatGroups.Clear();

        foreach (var (_, instance) in _drawOrder)
        {
            var modelTransform = instance.Transform;
            modelTransform.M41 -= cameraPosition.X;
            modelTransform.M42 -= cameraPosition.Y;
            modelTransform.M43 -= cameraPosition.Z;

            // Appear fade: scale this building's output alpha while it eases in.
            // uAppearAlpha is ALWAYS set by the draw pass (a GL uniform defaults
            // to 0, which would make every building invisible) - 1.0 is the
            // resident/steady value. The uniforms are no longer set here because
            // this loop only culls; the passes below set them per slice.
            float appearAlpha = 1f;
            if (AppearFade && instance.AppearStart > 0f)
            {
                float t = Math.Clamp(
                    (NowSeconds - instance.AppearStart) / MathF.Max(AppearFadeSeconds, 0.0001f), 0f, 1f);
                appearAlpha = t * t * t;
            }
            bool instanceFading = AppearFade && appearAlpha < 0.999f;
            if (instanceFading && appearAlpha <= 0f) continue;   // spawn frame: invisible, no depth

            // A city is one WMO instance containing many spatial groups. The
            // impostor swap and interior visibility both key off whether the
            // camera is inside one of this WMO's real cells (CameraInsideInstance),
            // which is Blizzard's approach-LOD model: shells show from outside,
            // detailed geometry from within.
            bool cameraInside = CameraInsideInstance(instance, cameraPosition);
            // Precise "have I crossed into the CITY INTERIOR of THIS building"
            // (PLAN_10 D1). Excludes pure-EXTERIOR cells: the Stormwind entrance keep
            // 'thief01' is a 0x08 group with a 10M-yd^3 box that swallows the whole
            // gate, so being in it is "at the gate", not "inside". Only a real
            // interior (0x2000) or exterior-lit street (0x40) cell counts as inside,
            // so the cathedral silhouette holds until you actually reach the streets.
            bool cameraInCell = _cameraSeeds.ContainsKey(instance.Id);

            // PLAN_10 D1/D2 (benilla wmo_portal/mod.rs:355-372): flood the portal
            // graph and let ClassifyGroup draw non-exterior groups only when reached.
            //   - camera INSIDE a real cell of this building -> seed from that cell.
            //   - camera OUTSIDE (or in another building) -> seed from every EXTERIOR
            //     (0x08) group of THIS building, full-screen, and flood through the
            //     doorways into the interiors. This is what hides Stormwind's roof
            //     from the gate approach; the exterior shell/towers (0x08) always
            //     draw, so the skyline is never lost (D5).
            // D6: no portals, or the flood reaches nothing -> null -> heuristic runs.
            HashSet<int>? reachable = null;
            // SUI free-view cutaway: for the ONE instance holding the commanded
            // toon, the flood is seeded at the toon and the gate becomes the cut
            // (ReachableGroups is authoritative for every group, shell included).
            // Reaching nothing falls through to the normal path below.
            if (_cutaway is { } cut && cut.InstanceId == instance.Id &&
                cut.Groups.Count > 0)
                reachable = cut.Groups;
            // Cut plane: the opened interior must DRAW even though the camera is above the roof
            // and no doorway is in view — union its cells into whatever the portal flood reaches.
            HashSet<int>? cutPlaneCells = _cutPlaneGroups is { } cp && cp.InstanceId == instance.Id
                ? cp.Groups : null;
            if (reachable is null
                && UsePortalCulling
                && instance.Model.Portals.Count > 0
                && instance.Model.PortalRefs.Count > 0)
            {
                if (_cameraSeeds.TryGetValue(instance.Id, out var cameraSeeds))
                {
                    reachable = ComputeReachableGroups(
                        instance, cameraSeeds, cameraPosition, viewProjection);
                }
                else
                {
                    var extSeeds = new List<int>();
                    foreach (var g in instance.Model.Groups)
                        if ((g.GroupFlags & 0x08u) != 0) extSeeds.Add(g.GroupIndex);  // pure EXTERIOR
                    if (extSeeds.Count > 0)
                        reachable = ComputeReachableGroups(
                            instance, extSeeds, cameraPosition, viewProjection);
                }
                if (reachable is { Count: 0 }) reachable = null;
                PortalReachedLastFrame = reachable?.Count ?? 0;
            }
            // Party sight: the subject's own cells draw too, or the sight cut opens onto sky.
            HashSet<int>? sightCells = null;
            _sightGroups?.TryGetValue(instance.Id, out sightCells);
            if (cutPlaneCells is not null || sightCells is not null)
            {
                var opened = reachable is null ? new HashSet<int>() : new HashSet<int>(reachable);
                if (reachable is null)
                    foreach (var g in instance.Model.Groups)
                        if ((g.GroupFlags & 0x08u) != 0) opened.Add(g.GroupIndex);
                if (cutPlaneCells is not null) opened.UnionWith(cutPlaneCells);
                if (sightCells is not null) opened.UnionWith(sightCells);
                reachable = opened;
            }
            _portalVisible[instance.Id] = reachable;

            _visibleGroups.Clear();
            int shellsDrawn = 0, shellsHidden = 0;
            var cull = new FrameCullContext(
                cameraPosition, cameraInside, effectiveDrawDistance, viewProjection, reachable, cameraInCell);
            foreach (var group in instance.Model.Groups)
            {
                var (groupMin, groupMax) = TransformedBounds(group, instance.Transform);
                float groupDistance = DistanceToBox(cameraPosition, groupMin, groupMax);

                // The decision lives in ClassifyGroup so the picker and dump report
                // the exact reason this loop acts on. The per-reason counters below
                // reproduce the previous behaviour (shells drawn/hidden, occluded,
                // LOD-culled); the switch draws for the two Drawn* reasons.
                switch (ClassifyGroup(instance, group, groupMin, groupMax, in cull))
                {
                    case WmoReasonCode.Drawn:
                        _visibleGroups.Add((groupDistance, group));
                        break;
                    case WmoReasonCode.DrawnShellFar:
                        _visibleGroups.Add((groupDistance, group));
                        shellsDrawn++;
                        break;
                    case WmoReasonCode.OverrideShow:
                        _visibleGroups.Add((groupDistance, group));
                        break;
                    case WmoReasonCode.ShellNearSuppressed:
                        LodGroupsCulledLastFrame++;
                        shellsHidden++;
                        break;
                    case WmoReasonCode.OcclusionCulled:
                        OccludedGroupsLastFrame++;
                        break;
                    default:
                        // InteriorCull / DistanceCulled / FrustumCulled: skipped,
                        // counted only in the frame aggregates.
                        break;
                }
            }

            // Surface the biggest WMO's swap state to the HUD (and, if VisTrace
            // is on, the console). On the approach bridge inside should read
            // False and shells should draw; in the Trade District inside should
            // read True and shells hide.
            if (instance.Model.Groups.Count > _frameLargestWmoGroupCount)
            {
                _frameLargestWmoGroupCount = instance.Model.Groups.Count;
                LargestWmoGroupCount = instance.Model.Groups.Count;
                LargestWmoName = Path.GetFileName(instance.Path);
                LargestWmoGroupsDrawn = _visibleGroups.Count;
                LastInsideCity = cameraInside;
                ShellsDrawnLastFrame = shellsDrawn;
                ShellsHiddenLastFrame = shellsHidden;

                if (VisTrace && ++_wmoVisLogFrames >= 120)
                {
                    _wmoVisLogFrames = 0;
                    Console.WriteLine(
                        $"[wmo-vis] {LargestWmoName} inside={cameraInside} " +
                        $"shellsDrawn={shellsDrawn} shellsHidden={shellsHidden} " +
                        $"groupsDrawn={_visibleGroups.Count}/{instance.Model.Groups.Count}");
                }
            }

            if (_visibleGroups.Count == 0)
                continue;

            DrawnLastFrame++;
            VisibleGroupsLastFrame += _visibleGroups.Count;

            // Nearest group first, for the same early-Z reason as the instance
            // sort — a cathedral's near wall should reject its own far wall. The
            // transparent pass then walks this list BACKWARDS, because blending
            // is only correct far-to-near. One sort, both orders.
            //
            // Note this reorders GROUPS, never batches within a group: coplanar
            // decals still draw in their authored MOBA order, which is what that
            // ordering is actually load-bearing for.
            _visibleGroups.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

            // Record, don't draw. Drawing here would nest the transparent pass
            // inside the instance loop, which the near-to-far instance order
            // makes actively wrong: a near building's banners (depth-write OFF)
            // would be laid down before a far building's opaque walls, and the
            // walls would then paint over them wherever the near building did
            // not itself write depth — which is exactly where a banner hangs.
            //
            // So the whole world's opaque geometry goes down first, near to far,
            // and the transparent geometry follows far to near. Two orders, one
            // pass of culling.
            int sliceStart = _flatGroups.Count;
            for (int gi = 0; gi < _visibleGroups.Count; gi++)
                _flatGroups.Add(_visibleGroups[gi].Group);

            _instanceSlices.Add(new InstanceSlice(
                modelTransform, appearAlpha, instanceFading, sliceStart, _visibleGroups.Count));
        }

        // ── the two draw passes ──────────────────────────────────────────────
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;

            if (transparentPass)
            {
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }

            bool fadeBlendOn = false;

            for (int si = 0; si < _instanceSlices.Count; si++)
            {
                // Instances: near to far when opaque, far to near when blending.
                var slice = transparentPass
                    ? _instanceSlices[_instanceSlices.Count - 1 - si]
                    : _instanceSlices[si];

                if (slice.GroupCount == 0) continue;

                // The opaque pass blends only for a building still easing in,
                // with depth-write left ON so it still occludes (benilla
                // wow_model.wgsl). Toggled per instance rather than per pass.
                if (!transparentPass)
                {
                    if (slice.Fading && !fadeBlendOn)
                    {
                        _gl.Enable(EnableCap.Blend);
                        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                        fadeBlendOn = true;
                    }
                    else if (!slice.Fading && fadeBlendOn)
                    {
                        _gl.Disable(EnableCap.Blend);
                        fadeBlendOn = false;
                    }
                }

                // Set lazily, for the same reason the VAO bind is: plenty of
                // buildings have no transparent batch at all, and this pass
                // would otherwise upload three uniforms per instance to draw
                // nothing from them.
                bool sliceUniformsSet = false;

                for (int gi = 0; gi < slice.GroupCount; gi++)
                {
                    // Groups: same rule as instances, one level down.
                    var group = transparentPass
                        ? _flatGroups[slice.GroupStart + slice.GroupCount - 1 - gi]
                        : _flatGroups[slice.GroupStart + gi];

                    // Bind lazily. Most groups have no transparent batches at all,
                    // and binding a VAO to then draw nothing from it was N wasted
                    // binds per frame on a city.
                    bool bound = false;

                    foreach (var batch in group.Batches)
                    {
                        if (batch.Transparent != transparentPass) continue;

                        if (!sliceUniformsSet)
                        {
                            _shader.Set("uModel", slice.Model);
                            _shader.Set("uModelViewProjection", slice.Model * viewProjection);
                            _shader.Set("uAppearAlpha", slice.AppearAlpha);
                            _shader.Set("uPreserveAlpha", transparentPass || slice.Fading ? 1 : 0);
                            sliceUniformsSet = true;
                        }

                        if (!bound)
                        {
                            _gl.BindVertexArray(group.Vao);
                            bound = true;
                        }

                        // Cut plane: the opened cells draw two-sided. From above, the camera looks
                        // at the BACK of the near walls; culled, they vanished with the angle and
                        // the room read as a floor floating on the street (owner, 2026-09-01).
                        bool twoSided = batch.TwoSided || ForceTwoSided ||
                            (_cutPlaneMeshes.Count > 0 && _cutPlaneMeshes.Contains(group));

                        if (twoSided && cullingOn)
                        {
                            _gl.Disable(EnableCap.CullFace);
                            cullingOn = false;
                        }
                        else if (!twoSided && !cullingOn)
                        {
                            _gl.Enable(EnableCap.CullFace);
                            cullingOn = true;
                        }

                        _shader.Set("uBatchType", UseVertexColors ? batch.Type : 3);
                        _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                        _shader.Set("uSidn", batch.Sidn * NightFraction);

                        if (batch.Texture is not null)
                        {
                            batch.Texture.Bind(0);
                            _shader.Set("uHasTexture", 1);
                            _shader.Set("uAlphaCutoff", batch.AlphaTest ? AlphaCutoff : 0f);
                        }
                        else
                        {
                            _shader.Set("uHasTexture", 0);
                            _shader.Set("uAlphaCutoff", 0f);
                        }

                        _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                            DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                        DrawCallsLastFrame++;
                        TrianglesLastFrame += batch.IndexCount / 3;
                    }
                }
            }

            if (transparentPass)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
            else if (fadeBlendOn)
            {
                _gl.Disable(EnableCap.Blend);
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // ── in-game diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Re-run shell classification on every loaded group with the current
    /// <see cref="ImpostorMaxVertices"/>. Cheap, and called from the HUD slider
    /// so the impostor set retunes live without a reload. Classification is baked
    /// (not per-frame) to keep the draw loop allocation-free.
    /// </summary>
    public void ReclassifyShells()
    {
        foreach (var instance in _instances)
        {
            uint n = (uint)instance.Model.Groups.Count;
            foreach (var g in instance.Model.Groups)
                g.IsDistanceLod = IsDistanceOnlyLod(
                    instance.Path, n, g.GroupFlags, g.VertexCount, g.GroupName);
        }
    }

    /// <summary>One WMO group a pick ray passed through.</summary>
    public readonly record struct GroupHit(
        string Path, string Root, int GroupIndex, string Name, uint Flags,
        int VertexCount, bool Interior, bool Shell, WmoReasonCode Reason, float Distance);

    /// <summary>One WMO group described for the scene dump: identity plus the exact
    /// WmoReasonCode ClassifyGroup assigned it this view.</summary>
    public readonly record struct GroupReport(
        int GroupIndex, string Name, uint Flags, int VertexCount,
        bool Interior, bool Shell, float Distance, WmoReasonCode Reason);

    /// <summary>One resident WMO instance summarized for the scene dump.</summary>
    public readonly record struct InstanceSummary(
        string Root, float Distance, int Groups, int Drawn, int Shells, bool CameraInside);

    private static int[] BuildRenderedIndices(WmoGroupData group)
    {
        var list = new List<int>(group.Indices.Count);
        for (int t = 0; t * 3 + 2 < group.Indices.Count; t++)
        {
            if (t < group.TriMaterials.Count && group.TriMaterials[t].materialId == 0xFF) continue;
            list.Add(group.Indices[t * 3]); list.Add(group.Indices[t * 3 + 1]); list.Add(group.Indices[t * 3 + 2]);
        }
        return list.ToArray();
    }

    private static Vector3[] BuildPickPositions(WmoGroupData group)
    {
        var arr = new Vector3[group.Vertices.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            var v = group.Vertices[i];
            arr[i] = new Vector3(v.x, v.y, v.z);
        }
        return arr;
    }

    /// <summary>
    /// The WMO groups the ray actually PIERCES (triangle-level), nearest first —
    /// so the top entry is the surface under the cursor, not every box behind it.
    /// The ray is taken into each instance's local space (WMO placement is a pure
    /// rotation+translation, so distances are preserved) and tested against that
    /// group's retained triangles. A per-group local AABB prunes the misses.
    /// </summary>
    /// <summary>
    /// World-space triangles of ONE group, for the dev "highlight picked group"
    /// overlay - so the exact geometry under discussion is unambiguous. Uses the
    /// retained pick mesh (local space) transformed by the instance; absolute world
    /// space, matching RenderHighlight's camera.ViewProjection. instancePath is the
    /// full path (GroupHit.Root).
    /// </summary>
    public List<Vector3> GroupWorldTriangles(string instancePath, int groupIndex)
    {
        var tris = new List<Vector3>();
        foreach (var instance in _instances)
        {
            if (!string.Equals(instance.Path, instancePath, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var g in instance.Model.Groups)
            {
                if (g.GroupIndex != groupIndex) continue;
                var pos = g.PickPositions;
                var idx = g.PickIndices;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
                    tris.Add(Vector3.Transform(pos[a], instance.Transform));
                    tris.Add(Vector3.Transform(pos[b], instance.Transform));
                    tris.Add(Vector3.Transform(pos[c], instance.Transform));
                }
                return tris;
            }
        }
        return tris;
    }

    /// <summary>
    /// Nearest hit on the RENDERED WMO triangles (what is on screen), or null. The collision world is the
    /// server's vmaps and carries invisible collision-only surfaces - e.g. the planes Blizzard laid over
    /// Stormwind's roofs - so a roof survey must ask the render mesh too (WoW Karting rooftops, 2026-10-04).
    /// </summary>
    public float? RaycastRendered(Vector3 rayOrigin, Vector3 rayDir, float maxDistance)
    {
        float best = maxDistance;
        bool hit = false;
        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            foreach (var g in instance.Model.Groups)
            {
                if (!RayHitsBox(lo, ld, g.LocalMin, g.LocalMax, out _)) continue;
                if (!NearestTriangle(lo, ld, g.PickPositions, g.RenderedPickIndices, out float t) || t >= best) continue;
                best = t;
                hit = true;
            }
        }
        return hit ? best : null;
    }

    public List<GroupHit> PickGroups(Camera camera, Vector3 rayOrigin, Vector3 rayDir, int max = 14)
    {
        var cameraPosition = camera.Position;
        var viewProjection = camera.RelativeViewProjection;
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);

        var hits = new List<GroupHit>();
        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            uint n = (uint)instance.Model.Groups.Count;

            var cull = new FrameCullContext(
                cameraPosition, CameraInsideInstance(instance, cameraPosition),
                effectiveDrawDistance, viewProjection, null,
                CameraGroup is { } pc && pc.InstanceId == instance.Id && !pc.IsExterior);

            foreach (var g in instance.Model.Groups)
            {
                if (!RayHitsBox(lo, ld, g.LocalMin, g.LocalMax, out _)) continue;
                if (!NearestTriangle(lo, ld, g.PickPositions, g.PickIndices, out float t)) continue;

                var (groupMin, groupMax) = TransformedBounds(g, instance.Transform);
                var reason = ClassifyGroup(instance, g, groupMin, groupMax, in cull);
                bool shell = UseDistanceLodShells && IsDistanceOnlyLod(
                    instance.Path, n, g.GroupFlags, g.VertexCount, g.GroupName);
                hits.Add(new GroupHit(
                    Path.GetFileName(instance.Path), instance.Path, g.GroupIndex, g.GroupName,
                    g.GroupFlags, g.VertexCount, g.IsInterior, shell, reason, t));
            }
        }
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (hits.Count > max) hits.RemoveRange(max, hits.Count - max);
        return hits;
    }

    /// <summary>
    /// Every group of the WMO instance the crosshair ray hits, each with the
    /// WmoReasonCode it draws/skips under - the "why is this building doing that"
    /// block of the scene dump. Null when the ray hits no WMO. Shares ClassifyGroup
    /// with Render, so these are the real reasons. Dump-only (PLAN_02).
    /// </summary>
    public List<GroupReport>? DescribeInstanceUnderRay(Camera camera, Vector3 rayOrigin, Vector3 rayDir)
    {
        Instance? best = null;
        float bestT = float.MaxValue;
        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            foreach (var g in instance.Model.Groups)
            {
                if (!RayHitsBox(lo, ld, g.LocalMin, g.LocalMax, out _)) continue;
                if (!NearestTriangle(lo, ld, g.PickPositions, g.PickIndices, out float t)) continue;
                if (t < bestT) { bestT = t; best = instance; }
            }
        }
        if (best is null) return null;

        var cull = new FrameCullContext(
            camera.Position, CameraInsideInstance(best, camera.Position),
            MathF.Min(DrawDistance, VisibilityDistance), camera.RelativeViewProjection);

        var report = new List<GroupReport>();
        foreach (var g in best.Model.Groups)
        {
            var (mn, mx) = TransformedBounds(g, best.Transform);
            var reason = ClassifyGroup(best, g, mn, mx, in cull);
            report.Add(new GroupReport(
                g.GroupIndex, g.GroupName, g.GroupFlags, g.VertexCount,
                g.IsInterior, g.IsDistanceLod, DistanceToBox(camera.Position, mn, mx), reason));
        }
        return report;
    }

    /// <summary>
    /// Every resident WMO instance summarized (nearest first): root, distance,
    /// group count, how many drew, how many were distance-shells, camera-inside.
    /// Classifies each group through ClassifyGroup - a one-shot cost paid only when
    /// a dump is taken. Dump-only (PLAN_02).
    /// </summary>
    public List<InstanceSummary> SummarizeInstances(Camera camera)
    {
        var cp = camera.Position;
        var vp = camera.RelativeViewProjection;
        float edd = MathF.Min(DrawDistance, VisibilityDistance);

        var summaries = new List<InstanceSummary>();
        foreach (var instance in _instances)
        {
            bool inside = CameraInsideInstance(instance, cp);
            var cull = new FrameCullContext(cp, inside, edd, vp);
            int drawn = 0, shells = 0;
            foreach (var g in instance.Model.Groups)
            {
                var (mn, mx) = TransformedBounds(g, instance.Transform);
                switch (ClassifyGroup(instance, g, mn, mx, in cull))
                {
                    case WmoReasonCode.Drawn: drawn++; break;
                    case WmoReasonCode.DrawnShellFar: drawn++; shells++; break;
                    case WmoReasonCode.OverrideShow: drawn++; break;
                }
            }
            summaries.Add(new InstanceSummary(
                Path.GetFileName(instance.Path),
                DistanceToBox(cp, instance.WorldMin, instance.WorldMax),
                instance.Model.Groups.Count, drawn, shells, inside));
        }
        summaries.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return summaries;
    }

    /// <summary>Nearest ray-triangle hit distance over a group's triangles.</summary>
    private static bool NearestTriangle(
        Vector3 o, Vector3 d, Vector3[] verts, int[] indices, out float tMin)
    {
        tMin = float.MaxValue;
        bool hit = false;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;
            if (RayTriangle(o, d, verts[a], verts[b], verts[c], out float t) && t < tMin)
            {
                tMin = t;
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>Two-sided Moller-Trumbore ray/triangle. t is distance along d.</summary>
    private static bool RayTriangle(
        Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        const float eps = 1e-7f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps) return false;
        float invDet = 1f / det;
        var tv = o - v0;
        float u = Vector3.Dot(tv, p) * invDet;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * invDet;
        return t > eps;
    }

    /// <summary>Slab ray/AABB test. Returns the near entry distance along dir.</summary>
    private static bool RayHitsBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t)
    {
        t = 0f;
        float tmin = 0f, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? mn.X : a == 1 ? mn.Y : mn.Z;
            float hi = a == 0 ? mx.X : a == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(da) < 1e-8f)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            float inv = 1f / da;
            float t1 = (lo - oa) * inv;
            float t2 = (hi - oa) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        t = tmin;
        return true;
    }

    public void Dispose()
    {
        foreach (var job in _preloadJobs)
            try { job.Worker.GetAwaiter().GetResult(); }
            catch { /* Shutdown must continue even if a background decode failed. */ }
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _instances.Clear();
        foreach (var job in _preloadJobs) job.Model.Dispose();
        _preloadJobs.Clear();
        _preloadQueue.Clear();
        _preloadQueued.Clear();
        _shader?.Dispose();
    }
}

/// <summary>
/// One MLIQ liquid surface, already placed in WORLD space, ready for
/// LiquidRenderer to turn into triangles. Produced by
/// <see cref="WmoRenderer.EnumerateLiquid"/>. See PLAN_15_WMO_LIQUID.md.
///
/// <para><b>Vertices</b> is the full (XVerts x YVerts) grid, row-major over j
/// then i, index-parallel to <see cref="WmoLiquid.VertexHeights"/>. Nothing has
/// been culled: the tile mask decides what is drawn and it is a TILE-level
/// decision, so the vertex grid has to stay complete or the indices stop
/// lining up.</para>
///
/// <para><b>The tile mask matters more than it looks.</b> Across the 235 MLIQ
/// groups in wmo.MPQ, 46,455 tiles are hidden against roughly 68,000 drawn. A
/// liquid grid is a bounding rectangle with the pool cut out of it. Ignore the
/// mask and Stormwind gets a single sheet of water over the whole district
/// rather than two canals.</para>
/// </summary>
public sealed class WmoLiquidSurface
{
    public WmoLiquidSurface(int instanceId, string modelPath, int groupIndex, string groupName,
                            float groupFloor, uint groupLiquid, WmoLiquid liquid,
                            System.Numerics.Vector3[] vertices)
    {
        InstanceId = instanceId;
        ModelPath = modelPath;
        GroupIndex = groupIndex;
        GroupName = groupName;
        GroupFloor = groupFloor;
        Liquid = liquid;
        Vertices = vertices;
        SoundNibble = ResolveSoundNibble(groupLiquid, liquid);
        (SoundBoundsMin, SoundBoundsMax, SoundFallbackHeight) = ResolveSoundBounds(liquid, vertices);
    }

    public int InstanceId { get; }
    public string ModelPath { get; }
    public int GroupIndex { get; }
    public string GroupName { get; }
    /// <summary>Lowest world Z of the owning group's transformed authored bounds.</summary>
    public float GroupFloor { get; }
    public WmoLiquid Liquid { get; }

    /// <summary>
    /// Whole-group MOGP override, or the first wet MLIQ tile's low nibble. This
    /// is the reference's SoundWaterType key for the complete WMO surface.
    /// </summary>
    public byte SoundNibble { get; }

    public System.Numerics.Vector2 SoundBoundsMin { get; }
    public System.Numerics.Vector2 SoundBoundsMax { get; }
    public float SoundFallbackHeight { get; }

    /// <summary>World-space grid, row-major j*XVerts + i.</summary>
    public System.Numerics.Vector3[] Vertices { get; }

    public int XVerts => Liquid.XVerts;
    public int YVerts => Liquid.YVerts;
    public int XTiles => Liquid.XTiles;
    public int YTiles => Liquid.YTiles;

    /// <summary>
    /// Substance of tile (i, j) translated into the code water.frag actually
    /// routes on. THIS TRANSLATION IS THE POINT — PLAN_15 §4.5.
    ///
    /// MLIQ encodes 0 water / 1 ocean / 2 magma / 3 slime in the low two bits.
    /// water.frag routes on the MCLQ codes, where 4 is river/lake, 1 ocean,
    /// 6 magma, 3 slime (SYSTEM_WATER.md §1.1). Three of the six live MLIQ codes
    /// mean the same thing under both encodings by coincidence, which is exactly
    /// why passing them through untranslated survives a test in Stormwind and
    /// puts blue water in Ironforge's lava channels.
    /// </summary>
    public byte ShaderType(int i, int j) => Liquid.BasicType(i, j) switch
    {
        0 => (byte)4,   // water -> river/lake
        1 => (byte)1,   // ocean
        2 => (byte)6,   // magma
        _ => (byte)3,   // slime
    };

    public bool IsHidden(int i, int j) => Liquid.IsHidden(i, j);

    private static byte ResolveSoundNibble(uint groupLiquid, WmoLiquid liquid)
    {
        if ((groupLiquid & 0x0f) != 0x0f) return (byte)(groupLiquid & 0x0f);
        for (int j = 0; j < liquid.YTiles; j++)
        for (int i = 0; i < liquid.XTiles; i++)
        {
            byte nibble = liquid.TypeNibble(i, j);
            if (nibble != 0x0f) return nibble;
        }
        return 0x0f;
    }

    private static (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max, float Fallback)
        ResolveSoundBounds(WmoLiquid liquid, System.Numerics.Vector3[] vertices)
    {
        var min = new System.Numerics.Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new System.Numerics.Vector2(float.NegativeInfinity, float.NegativeInfinity);
        float fallback = float.NegativeInfinity;
        for (int j = 0; j < liquid.YTiles; j++)
        for (int i = 0; i < liquid.XTiles; i++)
        {
            if (liquid.IsHidden(i, j)) continue;
            int tl = j * liquid.XVerts + i;
            foreach (int index in new[] { tl, tl + 1, tl + liquid.XVerts, tl + liquid.XVerts + 1 })
            {
                if ((uint)index >= (uint)vertices.Length) continue;
                System.Numerics.Vector3 point = vertices[index];
                min = System.Numerics.Vector2.Min(min, new(point.X, point.Y));
                max = System.Numerics.Vector2.Max(max, new(point.X, point.Y));
                fallback = MathF.Max(fallback, point.Z);
            }
        }
        if (!float.IsFinite(fallback))
            return (System.Numerics.Vector2.Zero, System.Numerics.Vector2.Zero, 0f);
        return (min, max, fallback);
    }

    /// <summary>
    /// Authored per-vertex texture coordinate in repeats (raw MLIQ int16 s/t
    /// over 255). Meaningful ONLY where the tiles are magma — for the other
    /// substances the same bytes are flow data and the shader must keep its
    /// planar mapping (it gates on the vertex type, so passing the value
    /// through unconditionally is safe). See WmoLiquid.VertexS.
    /// </summary>
    public System.Numerics.Vector2 AuthoredUv(int i, int j)
    {
        var (u, v) = Liquid.UvAt(i, j);
        return new System.Numerics.Vector2(u, v);
    }
}
