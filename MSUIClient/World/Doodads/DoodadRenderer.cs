using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Collision;
using MSUIClient.World.Units;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Doodads;

/// <summary>
/// Draws M2 doodads — trees, rocks, fences, barrels, the several hundred props
/// per tile that make terrain look like a place rather than a heightmap.
///
/// SAME SHAPE AS WmoRenderer, DELIBERATELY
///   MDDF placements out of the ADT, models out of the MPQs, one VAO per model,
///   one draw per batch. And the same principle: the collision hull comes from
///   the SAME parse as the render mesh, so a tree cannot be solid somewhere it
///   is not drawn.
///
/// AN M2 CARRIES ITS OWN COLLISION MESH, which is the thing that makes vmaps
/// unnecessary for a client. The render mesh of a tree is thousands of
/// triangles including every leaf; the collision hull is a few dozen around the
/// trunk. Many props have none at all and are meant to be walked through — that
/// is why the vmap extractor never produced a .vmo for Torch.m2 or
/// Innchandelier.m2, which looked like 237 missing files and was in fact 237
/// models with nothing to collide against.
///
/// THE COUNTS ARE THE DESIGN CONSTRAINT
///   785 placements on the Northshire tile alone, so roughly 7,000 across a 3x3
///   block, drawn from maybe 150 distinct models. Instances are therefore
///   grouped by model so each VAO binds once, frustum culled individually, and
///   dropped past a draw distance. Without all three this is tens of thousands
///   of draw calls a frame.
/// </summary>
public sealed partial class DoodadRenderer : IDisposable
{
    /// <summary>The Command View interior cut for this frame (Engine/WorldCut.cs), or null.
    /// Set by GameLoop from WmoRenderer.ActiveCut after the cell update.</summary>
    public WorldCut? Cut { get; set; }

    /// <summary>World-space chest points of the party members the line-of-sight cut protects
    /// (Engine/WorldCut.cs). Empty = no tunnels. Set by GameLoop once per frame.</summary>
    public List<Vector3> SightTargets { get; } = [];

    /// <summary>Command View party sight (World/PartySight.cs); null = never consulted.</summary>
    public PartySightPass? PartySight { get; set; }

    /// <summary>Canopy cut height above a party member's feet (the Command View cut height).</summary>
    public float CanopyCutHeight { get; set; } = 4.5f;

    /// <summary>The Command View's camera-side slice around the primary (Engine/WorldCut.cs),
    /// copied from WmoRenderer.Slice by GameLoop once per frame; null = off.</summary>
    public WorldSlice? Slice { get; set; }

    private void SetSightUniforms(Vector3 cameraPosition)
    {
        int count = Math.Min(SightTargets.Count, WorldCut.MaxSightLines);
        _shader.Set("uSightCount", count);
        for (int i = 0; i < count; i++)
            _shader.Set($"uSightTo[{i}]", SightTargets[i] - cameraPosition);
        _shader.Set("uSightRadius", new Vector2(WorldCut.SightRadiusNear, WorldCut.SightRadiusFar));
        _shader.Set("uCanopy", new Vector3(WorldCut.CanopyRadius, CanopyCutHeight, 1.2f));

        _shader.Set("uSliceActive", Slice is not null ? 1 : 0);
        if (Slice is not WorldSlice slice) return;
        _shader.Set("uSliceDir", slice.Forward);
        _shader.Set("uSliceDepth", slice.Depth);
        _shader.Set("uSliceFloorZ", slice.RelativeFloorZ(cameraPosition));
        _shader.Set("uSliceCentre", slice.RelativeCentre(cameraPosition));
        _shader.Set("uSliceRadius", WorldSlice.Radius);
    }

    /// <summary>Position(3) + Normal(3) + UV(2).</summary>
    private const int FloatsPerVertex = 8;

    /// <summary>Map corner offset. Identical to VmapFormat.CoordShift.</summary>
    private const float CoordShift = 32f * 533.33333f;

    /// <summary>
    /// "No baked light, full daylight" - the value every terrain doodad uses,
    /// and the value GL supplies for a disabled vertex attribute, so a doodad
    /// with no interior light renders exactly as it did before interior
    /// lighting existed.
    /// </summary>
    private static readonly Vector4 ExteriorLight = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// Bases for the M2 COLLISION hull. The render mesh needs none — an M2
    /// stores render vertices Y-up already — but the collision arrays in the
    /// same file are Z-up, so the hull alone gets converted.
    ///
    /// Index 0 is identity for completeness. Index 2 is the measured answer,
    /// (x, y, z) -> (x, z, -y), the same conversion WMO geometry needs. Index 1
    /// is the other proper rotation onto those axes; it differs by a half turn
    /// about X, invisible on a tree trunk and not on something asymmetric.
    ///
    /// Every entry has determinant +1, so none of them mirrors the geometry.
    /// </summary>
    private static readonly (string Name, Matrix4x4 M)[] Bases =
    [
        ("xyz", Matrix4x4.Identity),
        ("x-zy", new Matrix4x4(1, 0, 0, 0,  0, 0, 1, 0,  0, -1, 0, 0,  0, 0, 0, 1)),
        ("xz-y", new Matrix4x4(1, 0, 0, 0,  0, 0, -1, 0,  0, 1, 0, 0,  0, 0, 0, 1)),
        ("-zyx", new Matrix4x4(0, 0, 1, 0,  0, 1, 0, 0,  -1, 0, 0, 0,  0, 0, 0, 1)),
    ];

    public static string BasisName(int index)
        => index >= 0 && index < Bases.Length ? Bases[index].Name : "?";

    /// <summary>
    /// Which entry the COLLISION HULL uses, and it is not the same one.
    ///
    /// Measured, not assumed. Comparing every hull's local bounds against its
    /// own render mesh showed X identical and Y/Z swapped on 113 of 127 models:
    ///
    ///     elwynncliffrock02  render 17.7, 8.0, 19.0   hull 17.7, 19.0, 8.0
    ///     flagpole01         render  1.0, 10.0, 1.1   hull  1.0,  1.1, 10.0
    ///     smalldock          render 15.9, 26.5, 8.7   hull 16.0,  8.3, 26.3
    ///
    /// So an M2 stores its render vertices Y-up and its collision vertices
    /// Z-up. Two arrays, two conventions, one file. The hull therefore needs
    /// the basis the render mesh does not — and it turns out to be the same
    /// (x, y, z) -> (x, z, -y) that WMO geometry needs, which makes sense if
    /// the collision arrays share a lineage with the WMO tooling.
    ///
    /// Index 1 is the other proper rotation that maps the same axes; it differs
    /// by a half turn about X, which is invisible on a tree trunk and not on
    /// something asymmetric like a dock. If a hull looks flipped end for end,
    /// try it.
    /// </summary>
    public int CollisionBasisIndex { get; set; } = 2;

    /// <summary>ADT placement space to WoW world space: (x, y, z) -> (C - z, C - x, y).</summary>
    private static Matrix4x4 PlacementToWorld => new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        CoordShift, CoordShift, 0f, 1f);

    private sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public bool TwoSided;

        /// <summary>
        /// M2 material flag 0x01. The batch is authored at full brightness and
        /// must ignore lighting entirely: lantern glows, fire, torch flames,
        /// the additive glow planes around a brazier. Without honouring it, a
        /// lantern goes out the moment interior lighting darkens the room it
        /// hangs in, which is the one thing a lantern must never do.
        /// </summary>
        public bool Unlit;

        /// <summary>
        /// The material's M2 blending mode, ACTED ON since 2026-08-12:
        /// 0 opaque, 1 alpha-key, 2 alpha, 3 no-alpha-add, 4 add, 5 modulate,
        /// 6 modulate2x.
        ///
        /// Modes 0 and 1 draw in the main pass — mode 1 with the classic 0.5
        /// cutout cutoff, mode 0 with no cutoff at all, so an opaque material
        /// whose BLP happens to carry junk alpha can no longer lose fragments
        /// to a test it never asked for. Modes 2–6 are deferred to a second
        /// BLENDED pass after all opaque geometry: depth test on, depth write
        /// off, per-mode glBlendFunc (see SetBatchBlendFunc), and a cutoff of
        /// ~1/255 so faint halo texels survive. That second pass is what makes
        /// a lamp glow (Glow32.blp, blend 4 additive, alpha 0..0.6) visible:
        /// the old single pass alpha-tested it at 0.5 and drew the survivors
        /// opaque, which deleted the halo and left a hard-edged core.
        /// </summary>
        public ushort BlendMode;

        /// <summary>Modes 2-6: drawn in the blended pass, not the opaque one.</summary>
        public bool Blended => BlendMode >= 2;

        /// <summary>
        /// M2 material flag 0x10. Honoured in the opaque pass only; the blended
        /// pass never writes depth for anything.
        /// </summary>
        public bool NoDepthWrite;

        /// <summary>
        /// uFogMode for doodad.frag: 0 normal fog, 1 unfogged (render flag
        /// 0x02), 2 fog-to-black (additive), 3 fog-to-white (modulate),
        /// 4 fog-to-grey (modulate2x). Computed once at build so the draw loop
        /// only forwards it.
        /// </summary>
        public int FogMode;

        /// <summary>
        /// The batch's animated UV translation (M2 texture transform), resolved
        /// once at build via batch +22 → uvAnimLookup → uvAnimations. Null =
        /// static UVs, which is every tree, rock and fence. THIS is what makes
        /// a lavafall pour: the Blackrock falls are static wedge meshes whose
        /// lava texture scrolls ~1 V per 3.333 s loop; without evaluating it
        /// they sit frozen at their t=0 UV state (the "black wedge").
        /// Only X/Y of each key are UV — Z is authoring garbage (see
        /// M2TextureTransform).
        /// </summary>
        public M2AnimTrack<Vector3>? UvTranslation;
    }

    private sealed class Model : IDisposable
    {
        public uint Vao, Vbo, Ebo, InstanceVbo;
        public List<Batch> Batches = [];
        public Vector3 LocalMin, LocalMax;
        public int TriangleCount;

        /// <summary>
        /// Any batch with blend mode 2+. Cached so the draw loops can skip the
        /// deferred-pass bookkeeping for the overwhelming majority of models
        /// (trees, rocks, fences) that have nothing to blend.
        /// </summary>
        public bool HasBlendedBatches;

        /// <summary>
        /// The parsed M2, retained ONLY when some batch carries a UV-translation
        /// track — M2TrackSampling needs the model's sequences/global-sequence
        /// table to evaluate it. Null for the static majority, so their parsed
        /// data stays collectable exactly as before. (Emitter models already
        /// retain the whole M2Model through Emitters[i].OwnerModel.)
        /// </summary>
        public M2Model? UvAnimSource;

        // ── Animated STATIC doodads ─────────────────────────────────────────
        //
        // The Blackrock lava BUBBLES are M2 doodads whose entire behaviour is a
        // bone-scale loop: five root bones, one 17-vertex dome rigidly weighted
        // to each, a linear scale track 1.0 → 2.78 → snap to 1.0 (the pop),
        // and paired UV-translation tracks that flip the texture to its burst
        // frame in sync. Rendering them as bind-pose meshes leaves frozen domes
        // sitting on the lava. Models classified by BuildModel as animated
        // (at least one multi-key bone track) get a
        // CPU re-skin ONCE PER MODEL PER FRAME into the shared VBO — instances
        // stay instanced and pop in sync, which matches how the WMO places them
        // (a shared model with shared timing).
        //
        // Creature-grade decorative M2s (perched gryphons, birds and similar
        // ambient actors) use the same path now. They used to be rejected by a
        // 16-bone / 768-vertex scope guard and consequently remained in bind pose.
        // Multi-animation models play their authored Stand clip; authored chains
        // made entirely from animation id 0 retain the absolute-timeline sampler
        // required by lava bubbles and other environmental loops.

        /// <summary>The parsed M2, retained for bone-track sampling and the
        /// bind-pose vertices/weights. Null = static (the vast majority).</summary>
        public M2Model? BoneAnimSource;

        /// <summary>Visible vertices depend on a camera-facing/ignore-parent M2 bone.
        /// These models require a camera- and placement-specific CPU pose and therefore
        /// leave the shared instanced path.</summary>
        public bool UsesCameraFacingBones;

        /// <summary>Clip evaluator for creature-grade decorative M2s. Null means
        /// the model uses the authored absolute animation-0 timeline instead.</summary>
        public M2Animator? BoneAnimator;

        /// <summary>Usually AnimationData 0 (Stand), or sequence slot zero when
        /// the model has no named Stand sequence.</summary>
        public M2Animator.Clip? BoneClip;

        /// <summary>Retained only when the M2 has event markers. Dynamic
        /// GameObject audio reads this parsed timeline; it never reloads the model.</summary>
        public M2Model? EventSource;
        public M2Model? AttachmentSource;
        public Matrix4x4[]? AttachmentSkin;

        /// <summary>Interleaved skinned vertices, reused every frame.</summary>
        public float[]? AnimVertexScratch;

        /// <summary>Per-bone model-space transforms, reused every frame.</summary>
        public Matrix4x4[]? AnimBoneMatrices;

        /// <summary>Wall clock of the last re-skin, so a second render pass in
        /// the same frame does not skin (and upload) twice.</summary>
        public float LastAnimSampleTime = float.NegativeInfinity;
        public ulong LastAnimSampleDynamicGuid;

        /// <summary>Bind-pose render positions and indices retained for exact dynamic-GO mouse picking.</summary>
        public Vector3[] PickVertices = [];
        public ushort[] PickIndices = [];

        /// <summary>The M2's own collision hull, local space, three verts per triangle.</summary>
        public Vector3[] CollisionTriangles = [];

        /// <summary>Local bounds of that hull, for comparison against the render mesh.</summary>
        public Vector3 CollisionMin, CollisionMax;
        public string SourcePath = "";

        /// <summary>
        /// The M2's particle emitters (PLAN_14). Kept on the shared Model
        /// because the emitter DEFINITION is per-model, while the live particle
        /// pool a stage-2 renderer spawns from it will be per-instance - two
        /// torches must not share one pool (PLAN_14 H5).
        /// </summary>
        public List<M2ParticleEmitter> Emitters = [];

        /// <summary>
        /// The BLP path for each emitter's texture, resolved once at load.
        /// Emitter.Texture indexes the M2's TEXTURES array directly - NOT
        /// through TextureLookup, which is the mesh's indirection and would
        /// pick the wrong file.
        /// </summary>
        public string[] EmitterTexturePaths = [];

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
            if (InstanceVbo != 0) _gl.DeleteBuffer(InstanceVbo);
        }
    }

    private sealed class ModelPreloadJob
    {
        public required string Path;
        public required string CacheKey;
        public required Task<PreparedModel> Worker;
        public PreparedModel? Ready;
        public Task<UploadedModel>? Upload;
        public System.Diagnostics.Stopwatch Timer = System.Diagnostics.Stopwatch.StartNew();
    }

    private sealed class UploadedModel
    {
        public uint Vbo;
        public uint Ebo;
        public Dictionary<string, Texture?> Textures = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PreparedModel
    {
        public M2Model? Parsed;
        public bool Missing;
        public List<PreparedTexture> Textures = [];
    }

    private sealed class PreparedTexture
    {
        public required string Path;
        public byte[]? Bgra;
        public int Width;
        public int Height;
    }

    private sealed class Instance
    {
        public Matrix4x4 Transform;
        public Vector3 WorldMin, WorldMax;
        public string Path = "";

        /// <summary>
        /// Exact server-gameobject owner for dynamic placements; zero for ADT/WMO
        /// scenery. ParticleRenderer carries this through to its pool identity so
        /// two summoned effects at the same rounded position never share state,
        /// and so procedural GameObject portal surfaces are never inferred from a
        /// generic model-space emitter.
        /// </summary>
        public ulong DynamicGuid;

        /// <summary>
        /// Moving owner-keyed placements are queried from their live transform instead of being
        /// baked into CollisionWorld. Ordinary stationary GameObjects retain the snapshot path.
        /// </summary>
        public bool LiveCollision;
        public bool CosmeticOnly;

        /// <summary>An exact AnimationData one-shot owned by this dynamic
        /// GameObject placement. Static scenery never carries one.</summary>
        public DynamicAnimation? OneShot;

        /// <summary>
        /// Per-GameObject held state pose. Unlike the shared model idle, this is
        /// owner-local: two copies of one crate model may be open and closed at
        /// the same time. A transition OneShot draws over it for one authored
        /// window and then hands back to this pose.
        /// </summary>
        public DynamicStateAnimation? StateAnimation;

        /// <summary>
        /// Baked interior light for THIS placement. rgb is MODD.color / 255,
        /// a is how much daylight to use instead (0 interior, 1 exterior).
        ///
        /// (0, 0, 0, 1) means "no baked light, full daylight", which is both
        /// the correct answer for every terrain doodad and the value GL hands
        /// a disabled vertex attribute - so the default costs nothing and
        /// behaves identically to having no interior lighting at all.
        /// </summary>
        public Vector4 Light = new(0f, 0f, 0f, 1f);

        /// <summary>Appear-fade spawn time in seconds; 0 = opaque/no fade. Set from
        /// the persistent per-key stamp so a re-placement on a tile crossing does
        /// not re-fade. See <see cref="ResolveAppearStart"/>.</summary>
        public float AppearStart;

        // Set only for MODD furniture. Terrain doodads remain unowned and are
        // never portal-culled.
        public int WmoInstanceId;
        public int[] OwnerGroups = [];
    }

    private sealed record DynamicAnimation(
        int AnimationId, float StartedAt, float DurationSeconds);

    private sealed record DynamicStateAnimation(
        int AnimationId, float StartedAt, bool Frozen);

    /// <summary>
    /// What actually goes in the instance VBO: the placement matrix, the baked
    /// light, the appear-fade start, then the hover-highlight boost. Sequential
    /// layout of 22 floats with no padding, which is what the stride arithmetic
    /// in BuildModel assumes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public Matrix4x4 Transform;
        public Vector4 Light;
        public float AppearStart;

        /// <summary>Additive brightness for the hovered dynamic placement —
        /// the same 64/255 boost CreatureRenderer adds for a hovered unit.
        /// 0 for every static doodad and every non-hovered gameobject, which is
        /// also the GL default for a disabled attribute.</summary>
        public float Highlight;
    }

    private readonly GL _gl;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _untexturedModelsReported =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances grouped by model, so each VAO binds once per frame.</summary>
    private readonly Dictionary<Model, List<Instance>> _byModel = [];

    /// <summary>
    /// Just the cull bounds, 24 bytes, contiguous. A value type on purpose.
    /// </summary>
    private readonly struct CullBounds(Vector3 min, Vector3 max)
    {
        public readonly Vector3 Min = min;
        public readonly Vector3 Max = max;
    }

    /// <summary>
    /// Bounds parallel to <see cref="_byModel"/>, same model key, same index.
    ///
    /// Measured 2026-07-25: the cull ran at **751-9,053 ns per instance** across
    /// two crossings, against roughly 50-100 ns for the arithmetic it actually
    /// performs. The loop was never the problem. `Instance` is a sealed CLASS,
    /// so `List&lt;Instance&gt;` is a list of pointers, and reading `WorldMin`
    /// and `WorldMax` for every placement means dereferencing thousands of
    /// scattered heap objects that PopulateDoodads allocated moments earlier in
    /// the same frame. The rate tracked model count (512 models -> 9,053 ns,
    /// 153 models -> 751 ns), which is locality, not workload: more models means
    /// more separate lists means a more scattered walk.
    ///
    /// So the cull reads THIS instead. 6,364 placements x 24 bytes is ~153 KB,
    /// contiguous, and it fits in L2. The heap object is touched only for the
    /// instances that survive - roughly 250 of 6,600, so about 96 percent of the
    /// pointer chases disappear.
    ///
    /// Kept parallel rather than folded into one type to hold the change down to
    /// three sites; the two Add paths and the two Clear paths are the only
    /// places it can drift, and DrainCullBounds self-heals if it ever does.
    /// </summary>
    private readonly Dictionary<Model, List<CullBounds>> _cullBounds = [];

    /// <summary>
    /// A/B switch for the flat-bounds cull, so the change can be proven in one
    /// session without a rebuild. PLAN_08 section 7 step 3: a change that does
    /// not move its own named field did nothing. The named field here is
    /// `cull` in the [hitch] doodad line. Toggle it, cross the same boundary,
    /// diff that number.
    /// </summary>
    public bool FlatCullBounds { get; set; } = true;

    private bool _cullBoundsDriftReported;
    private readonly List<InstanceData> _visibleInstances = [];

    private readonly HashSet<string> _placed = [];
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _preloadQueue = new();
    private readonly Queue<string> _newlyReadyModels = new();
    private readonly HashSet<string> _preloadQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (float DistanceSq, string Queue)> _preloadTrace =
        new(StringComparer.OrdinalIgnoreCase);
    // A POOL of in-flight prepare jobs. Was a single _preloadJob, which prepared
    // exactly ONE model at a time - the ~0.10 s x N serial stall on zone load and
    // .tele (245 Stormwind doodads = ~25 s of one-at-a-time streaming). The
    // AssetWorkerPool caps real concurrency; keeping several jobs in flight
    // saturates it so many models decode in parallel.
    private readonly List<ModelPreloadJob> _preloadJobs = new();
    private const int MaxConcurrentPreloads = 12;

    private Shader _shader = null!;

    public int InstanceCount { get; private set; }

    /// <summary>
    /// How many placements carry a baked interior light (MODD.color) rather
    /// than falling back to daylight. Purely diagnostic: if this reads 0 while
    /// standing in a tavern, MODR or the interior gate is the thing that broke,
    /// not the shader.
    /// </summary>
    public int InteriorLitCount { get; private set; }
    public int ModelCount => _models.Count(m => m.Value is not null);

    /// <summary>
    /// Every loaded model that carries particle emitters, with its path.
    /// Read-only; PLAN_14 stage 1's panel is the only caller.
    /// </summary>
    public IEnumerable<(string Path, IReadOnlyList<M2ParticleEmitter> Emitters)> ModelsWithEmitters()
    {
        foreach (var (path, model) in _models)
            if (model is not null && model.Emitters.Count > 0)
                yield return (path, model.Emitters);
    }

    /// <summary>
    /// One entry per (placement, emitter) within <paramref name="radius"/> of a
    /// point, for the particle renderer to keep pools for.
    ///
    /// Distance is measured to the PLACEMENT, not the emitter, because the
    /// emitter's own offset needs the transform applied and the caller does that
    /// anyway. A generous radius here is cheap - the pool map is what bounds the
    /// work, not this walk.
    /// </summary>
    public IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                        int EmitterIndex, string TexturePath, double AnimationTime, int AnimationId,
                        Vector3? LocalOrigin, Quaternion? LocalRotation, ulong OwnerGuid)>
        EmitterInstances(Vector3 near, float radius)
    {
        float r2 = radius * radius;
        foreach (var (model, instances) in _byModel)
        {
            if (model.Emitters.Count == 0) continue;
            foreach (var inst in instances)
            {
                var origin = new Vector3(inst.Transform.M41, inst.Transform.M42, inst.Transform.M43);
                if (Vector3.DistanceSquared(origin, near) > r2) continue;
                if (inst.WmoInstanceId >= 0 && PortalVisibility is not null &&
                    !PortalVisibility(inst.WmoInstanceId, inst.OwnerGroups)) continue;

                for (int e = 0; e < model.Emitters.Count; e++)
                {
                    string tex = e < model.EmitterTexturePaths.Length
                        ? model.EmitterTexturePaths[e] : "";
                    yield return (inst.Path, inst.Transform, model.Emitters[e], e, tex, double.NaN, 0,
                        null, null, inst.DynamicGuid);
                }
            }
        }
    }

    /// <summary>Total emitters across every loaded model.</summary>
    public int EmitterCount
    {
        get
        {
            int n = 0;
            foreach (var m in _models.Values) n += m?.Emitters.Count ?? 0;
            return n;
        }
    }
    public int TextureCount => _textures.Count(t => t.Value is not null);
    public int PendingPreloads => _preloadQueue.Count + _preloadJobs.Count;
    public Action<string, string, float>? PreloadDequeued { get; set; }
    public int TotalTriangles { get; private set; }
    public int CollisionModels { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public long TrianglesLastFrame { get; private set; }

    /// <summary>Placed instances rejected this frame by the draw-distance test.</summary>
    public int DistanceCulledLastFrame { get; private set; }
    /// <summary>Placed instances rejected this frame by the frustum test.</summary>
    public int FrustumCulledLastFrame { get; private set; }
    /// <summary>Frame counter for the throttled [doodad-cull] diagnostic.</summary>
    private int _cullLogFrames;
    public double RenderMilliseconds { get; private set; }
    public void NoteNotRendered()
    {
        RenderMilliseconds = 0;
        CullMilliseconds = InstanceUploadMilliseconds = DrawMilliseconds = 0;
        UploadedModelsLastFrame = FirstTouchModelsLastFrame = 0;
        CullModelsLastFrame = CullInstancesLastFrame = 0;
    }

    // ── The 60 ms split (2026-07-25) ────────────────────────────────────────
    //
    // A crossing frame measured `doodad 60.3 ms` of CPU render while the GPU
    // drew the same pass in 0.1 ms, with no GC pause (1.7 of 91 ms), no upload
    // in flight, and the frame's last GL call returning in 0.1 ms. The cost is
    // inside RenderInstanced and it is not the GPU, not the collector and not
    // the swap. RenderInstanced does three unrelated things in one loop, and one
    // RenderMilliseconds cannot tell them apart:
    //
    //   Cull   - pure CPU over every placement (6,695 at the crossing)
    //   Upload - one glBufferData per visible model, StreamDraw
    //   Draw   - texture binds, uniform sets, DrawElementsInstanced
    //
    // They fail for different reasons. Cull is our own arithmetic and scales
    // with placement count. Upload and Draw are driver calls: on this Intel
    // driver the FIRST bind of a texture created on the shared upload context
    // can force a synchronization, and a crossing is exactly when dozens of
    // models are touched by the render context for the first time.
    //
    // Note the uploads counter added earlier CANNOT see that case - it reports
    // uploads in flight during the frame, and these completed frames earlier.
    // First-touch is a different failure from concurrent-upload, so it needs its
    // own count. That is what FirstTouchModelsLastFrame is for.

    /// <summary>Distance and frustum rejection over every placement. Pure CPU, ours.</summary>
    public double CullMilliseconds { get; private set; }

    /// <summary>The per-model glBufferData of visible instance data. Driver call.</summary>
    public double InstanceUploadMilliseconds { get; private set; }

    /// <summary>Binds, uniform sets and DrawElementsInstanced. Driver calls.</summary>
    public double DrawMilliseconds { get; private set; }

    /// <summary>
    /// Models drawn this frame that were not drawn last frame. A crossing spikes
    /// this; a steady walk holds it near zero. If the cost tracks THIS rather
    /// than the placement count, it is first-touch of shared-context objects and
    /// the fix is a warm-up pass, not a cheaper cull.
    /// </summary>
    public int FirstTouchModelsLastFrame { get; private set; }

    /// <summary>Models that issued a glBufferData this frame.</summary>
    public int UploadedModelsLastFrame { get; private set; }

    /// <summary>
    /// Entries of _byModel walked this frame, and instances examined across all
    /// of them. Without these, CullMilliseconds cannot be divided by anything
    /// and "55.8 ms" stays a number instead of a rate.
    ///
    /// The measured rate is what decides the next move. Roughly:
    ///   ~50-100 ns/instance  - normal; the cull is fine and the cost is elsewhere
    ///   ~1000+ ns/instance   - memory, not arithmetic. Instance is a sealed
    ///                          class, so this loop pointer-chases 6,000+
    ///                          scattered heap objects that PopulateDoodads
    ///                          allocated moments earlier in the same frame.
    ///                          The fix is flat contiguous bounds, not a cheaper
    ///                          test.
    /// High ns/model with low ns/instance would mean the opposite - per-model
    /// overhead over a dictionary with far more entries than expected.
    /// </summary>
    public int CullModelsLastFrame { get; private set; }

    /// <summary>Instances examined by the cull this frame, across all models.</summary>
    public int CullInstancesLastFrame { get; private set; }

    // Preallocated and reused - an instrument that allocates two sets per frame
    // would add to the allocation rate it exists to help explain.
    // Not readonly: these are swapped rather than copied at the end of each
    // instanced pass.
    private HashSet<Model> _drawnPreviousFrame = [];
    private HashSet<Model> _drawnThisFrame = [];

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Runtime gate for owner-local GameObject animation (chest lids, doors). The
    /// first live entry produced a repeatable nvoglv64.dll access violation the
    /// instant the initial Closed (AnimationData 147) poses were armed: the shared
    /// VAO leaves the divisor-1 instance arrays enabled, and the per-instance pose
    /// draw's non-instanced glDrawElements fetched them at index 0 off an
    /// unallocated <c>InstanceVbo</c>. That store is now seeded with one element at
    /// build (see BuildModel), so the fetch is in-bounds, and only the handful of
    /// GameObject models actually holding a pose leave the instanced path (see
    /// <see cref="_perInstanceModels"/>) -- the static world stays instanced. With
    /// both in place the lane is on by default; set false to fall back to the pre-
    /// animation renderer (GameObjects then rest at their loader pose).
    /// </summary>
    public bool DynamicGameObjectAnimationEnabled { get; set; } = true;
    public bool FrustumCulling { get; set; } = true;
    /// <summary>
    /// When true, a placement whose model is not resident queues that model and
    /// returns immediately. Runtime placement refreshes will adopt it later.
    /// This keeps MPQ reads, parsing, texture decoding and uploads out of the
    /// render thread.
    /// </summary>
    public bool DemandStreaming { get; set; }
    public bool UseInstancing { get; set; } = true;

    public float DrawDistance { get; set; } = 300f;
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Alpha below which an ALPHA-KEY (M2 blend mode 1) fragment is discarded.
    ///
    /// This MUST be set. DoodadRenderer owns its own Shader instance — a
    /// separate GL program built from the same source as the WMO one — so
    /// uniforms set over there do not apply here. Leaving uAlphaCutoff unset
    /// left it at zero, no alpha test ran, and every foliage card rendered as
    /// an opaque black rectangle. Tree leaves are alpha cutouts; without this
    /// the forest becomes a wall of dark panels.
    ///
    /// Since 2026-08-12 it applies to mode-1 batches only: mode 0 (opaque)
    /// draws with no test, and modes 2-6 use the blended pass's 1/255 cutoff
    /// (see Batch.BlendMode and BlendedAlphaCutoff).
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    /// <summary>
    /// Overbright factor for baked vertex light. MUST TRACK THE WMO RENDERER'S
    /// VALUE. Vanilla authored vertex colour in [0, 2] rather than [0, 1], and
    /// a prop lit on a different scale from the floor under it detaches from
    /// that floor in a way that is instantly visible and hard to name.
    /// </summary>
    public float VertexColorScale { get; set; } = 2.0f;

    /// <summary>Beyond-portal fill light, driven per frame by GameLoop (see
    /// WmoRenderer). Position is world-absolute; colour premultiplied by
    /// intensity; radius 0 disables it so exterior props are untouched.</summary>
    public Vector3 PortalLightWorldPos { get; set; }
    public Vector3 PortalLightColor { get; set; }
    public float PortalLightRadius { get; set; }

    /// <summary>
    /// Whether props inside buildings use their baked MODD.color instead of the
    /// outdoor sun. Off restores the pre-interior-lighting look exactly, which
    /// is what makes this a useful A/B toggle rather than a debug leftover.
    /// </summary>
    public bool InteriorLighting { get; set; } = true;

    /// <summary>Single portal-visibility authority supplied by WmoRenderer.
    /// Null keeps every doodad visible.</summary>
    public Func<int, int[], bool>? PortalVisibility { get; set; }

    // ── appear fade (benilla model_fade.rs) ─────────────────────────────────────

    /// <summary>Ease streamed-in doodads in over <see cref="AppearFadeSeconds"/>
    /// (alpha = t^3) instead of popping. Off restores the original hard pop-in.</summary>
    public bool AppearFade { get; set; }

    /// <summary>Appear-fade ramp length in seconds (benilla APPEAR_FADE_SECS = 2).</summary>
    public float AppearFadeSeconds { get; set; } = 2f;

    /// <summary>World clock in seconds, pushed each frame by GameLoop. Drives both
    /// the shader fade and the spawn-time stamp below.</summary>
    public float NowSeconds { get; set; }

    /// <summary>True once the loading curtain has lifted. While false (initial
    /// build behind the curtain) every placement is stamped opaque, so the
    /// curtain's own fade covers the first reveal and only models streamed in
    /// later ease in - benilla arms its appear fades on the same "world shown"
    /// signal (model_fade.rs arm_appear_fade / progress.focus_resident).</summary>
    public bool WorldShown { get; set; }

    /// <summary>
    /// Spawn time per placement KEY, surviving ResetPlacements. A tile crossing
    /// rebuilds every resident placement from scratch (Program.cs UpdateWorldResidency),
    /// so without a persistent stamp the whole visible world would re-fade on every
    /// crossing. A key already here reuses its (zero, or long-past) start and stays
    /// opaque; a genuinely new key seen after the world is shown is stamped NOW and
    /// eases in. Capped so a long roam can't grow it without bound.
    /// </summary>
    private readonly Dictionary<string, float> _appearStartByKey = new(StringComparer.Ordinal);
    private const int AppearKeyCap = 262144;

    /// <summary>
    /// Start a new opaque world residency epoch. Ring rebuilds deliberately
    /// preserve fade keys; renderer reuse for a different prepared portal must
    /// clear them so historic active-world timestamps cannot hold candidate
    /// doodads permanently transparent.
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

    /// <summary>Resolve (and remember) the appear-fade start for a placement key.
    /// 0 means "opaque, no fade". Only ever fades a genuinely new placement that
    /// first appears after the world is on screen.</summary>
    private float ResolveAppearStart(string key)
    {
        if (!AppearFade) return 0f;
        if (_appearStartByKey.TryGetValue(key, out float start)) return start;
        start = WorldShown ? NowSeconds : 0f;
        if (_appearStartByKey.Count < AppearKeyCap) _appearStartByKey[key] = start;
        return start;
    }

    public DoodadRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
    }

    /// <summary>
    /// Doodads have their OWN shader pair, forked from the WMO one.
    ///
    /// They used to load wmo.vert/wmo.frag directly. That was harmless while
    /// props and walls wanted identical lighting, and became a hazard the
    /// moment they did not: the baked MOCV lighting on building walls is
    /// correct and must not move, while props need per-instance interior light
    /// and the M2 unlit flag, neither of which a wall has any use for.
    /// Forking the files is what lets doodad lighting change without touching
    /// wall lighting.
    ///
    /// The exterior lighting and fog maths in the fork are character-for-
    /// character the WMO ones, so a barrel standing in a field lights exactly
    /// as it did before the fork.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "doodad.vert"),
            Path.Combine(shaderDir, "doodad.frag"));
    }

    public void LoadForTiles(
        IEnumerable<(int col, int row)> tiles,
        AdtCache adts,
        Vector2? streamCentre = null,
        float maxDistance = float.PositiveInfinity,
        bool reportDiagnostics = true,
        IReadOnlySet<string>? modelFilter = null)
    {
        var started = DateTime.UtcNow;
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Doodads is null) continue;

            foreach (var d in adt.Doodads)
            {
                if (string.IsNullOrWhiteSpace(d.ModelPath)) continue;
                if (modelFilter is not null &&
                    !modelFilter.Contains(ModelCacheKey(d.ModelPath))) continue;

                var transform = BuildPlacement(d);
                if (streamCentre is Vector2 centre && !float.IsPositiveInfinity(maxDistance))
                {
                    var delta = new Vector2(transform.M41, transform.M42) - centre;
                    if (delta.LengthSquared() > maxDistanceSq) continue;
                }

                // A doodad straddling a tile edge is listed in both ADTs. Do
                // not reserve the key until its model is resident: demand
                // streaming must be able to retry this placement next refresh.
                // Key on the WORLD position and an extension-less, lower-cased path so
                // this collapses with the WMO-embedded key below - the same doodad present
                // in BOTH the terrain (.mdx) and a WMO doodad set (.m2) is otherwise drawn
                // twice (the instance portal). See NormalizeModelKey.
                string key = $"{NormalizeModelKey(d.ModelPath)}|{transform.M41:F2}|{transform.M42:F2}|{transform.M43:F2}";
                if (_placed.Contains(key)) continue;

                var model = ResolveModel(d.ModelPath);
                if (model is null) continue;
                if (ExistingEmitterPlacementNear(model, transform))
                {
                    _placed.Add(key);
                    Console.WriteLine($"[doodad] deduped near-coincident effect: {d.ModelPath}");
                    continue;
                }
                _placed.Add(key);

                var (min, max) = TransformedBounds(model, transform);

                if (!_byModel.TryGetValue(model, out var list))
                {
                    list = [];
                    _byModel[model] = list;
                }

                list.Add(new Instance
                {
                    Transform = transform,
                    WorldMin = min,
                    WorldMax = max,
                    Path = d.ModelPath,
                    AppearStart = ResolveAppearStart(key),
                });
                CullBoundsFor(model).Add(new CullBounds(min, max));

                InstanceCount++;
                TotalTriangles += model.TriangleCount;
            }
        }

        var elapsed = DateTime.UtcNow - started;
        if (reportDiagnostics)
            Console.WriteLine(
                $"[doodad] {InstanceCount} placement(s), {ModelCount} model(s), " +
                $"{TextureCount} texture(s), {CollisionModels} with collision, " +
                $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");

        if (reportDiagnostics && _missing.Count > 0)
            Console.WriteLine($"[doodad] {_missing.Count} model(s) not found in the MPQs");

        if (reportDiagnostics) ReportHullAlignment();
    }

    /// <summary>
    /// Drop outdoor and interior placements while retaining model/texture GPU
    /// caches. Streaming rebuilds the small active placement set from ADTs.
    /// </summary>
    public void ResetPlacements()
    {
        _byModel.Clear();
        _cullBounds.Clear();
        _placed.Clear();
        _dynamicArtKits.Clear();
        _artKitOwnerScratch.Clear();
        _dynamicArtKitParts.Clear();
        _dynamicByKey.Clear();   // instances died with _byModel; the GO sync re-adds via HasDynamic
        _dynamicWmoProps.Clear();
        InstanceCount = 0;
        InteriorLitCount = 0;
        TotalTriangles = 0;
        DrawnLastFrame = 0;
    }

    /// <summary>Queue outdoor M2 assets referenced by an outer ADT ring.</summary>
    public void QueuePreloadForTiles(
        IEnumerable<(int col, int row)> tiles,
        AdtCache adts,
        Vector2? streamCentre = null,
        float maxDistance = float.PositiveInfinity)
    {
        float maxDistanceSq = maxDistance * maxDistance;
        var paths = new List<(string Path, float DistanceSq)>();
        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Doodads is null) continue;

            foreach (var doodad in adt.Doodads)
            {
                float distanceSq = 0f;
                if (streamCentre is Vector2 centre && !float.IsPositiveInfinity(maxDistance))
                {
                    var transform = BuildPlacement(doodad);
                    var delta = new Vector2(transform.M41, transform.M42) - centre;
                    distanceSq = delta.LengthSquared();
                    if (distanceSq > maxDistanceSq) continue;
                }

                paths.Add((doodad.ModelPath, distanceSq));
            }
        }
        QueuePreloadModels(
            paths.OrderBy(p => p.DistanceSq).Select(p => (p.Path, p.DistanceSq)),
            "outdoor-doodad");
    }

    /// <summary>Queue M2 paths without creating visible placements.</summary>
    public void QueuePreloadModels(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            QueuePreloadModel(path, 0f, "doodad");
    }

    public void QueuePreloadModels(
        IEnumerable<(string Path, float DistanceSq)> paths, string queue)
    {
        foreach (var (path, distanceSq) in paths)
            QueuePreloadModel(path, distanceSq, queue);
    }

    private void QueuePreloadModel(string path, float distanceSq, string queue)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string key = ModelCacheKey(path);
        if (_models.ContainsKey(key) ||
            _preloadJobs.Any(j => j.CacheKey.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
            !_preloadQueued.Add(key)) return;
        _preloadQueue.Enqueue(path);
        _preloadTrace[key] = (distanceSq, queue);
    }

    /// <summary>
    /// Start CPU preparation on workers, then advance ready texture/mesh jobs
    /// on the render thread. MPQ extraction, M2 parsing and BLP decoding never
    /// consume a movement frame; callers may cap ready-job adoption when this
    /// is background retirement rather than an opaque loading phase.
    /// </summary>
    public bool WarmNextPreload(
        bool waitForWorker = false, int maxReadyJobs = int.MaxValue)
    {
        // Keep the worker pool saturated: start prepares until MaxConcurrentPreloads
        // are in flight. THIS is the parallelism - PrepareModel (MPQ read + M2 parse
        // + BLP decode) now runs on the pool for many models at once instead of one.
        while (_preloadJobs.Count < MaxConcurrentPreloads && _preloadQueue.Count > 0)
        {
            string path = _preloadQueue.Dequeue();
            string key = ModelCacheKey(path);
            _preloadQueued.Remove(key);
            var trace = _preloadTrace.GetValueOrDefault(key);
            _preloadTrace.Remove(key);
            PreloadDequeued?.Invoke(trace.Queue ?? "doodad", path, trace.DistanceSq);
            if (_models.ContainsKey(key)) continue;
            if (_preloadJobs.Any(j => j.CacheKey.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            _preloadJobs.Add(new ModelPreloadJob
            {
                Path = path,
                CacheKey = key,
                Worker = _workers.Run(() => PrepareModel(path)),
            });
        }

        if (_preloadJobs.Count == 0) return false;

        // Advance every job whose CPU prepare is done. FinalizePreload enqueues the
        // GPU upload (cheap - keeps the upload thread fed) and, once the upload lands,
        // builds + caches the model. The GL build is the only main-thread cost, a few
        // ms per model; upload throughput bounds the rest.
        int readyJobsAdvanced = 0;
        for (int i = _preloadJobs.Count - 1;
             i >= 0 && readyJobsAdvanced < Math.Max(0, maxReadyJobs);
             i--)
        {
            var job = _preloadJobs[i];
            if (waitForWorker && !job.Worker.IsCompleted)
                try { job.Worker.GetAwaiter().GetResult(); } catch { }
            if (!job.Worker.IsCompleted) continue; // still preparing on a worker thread

            readyJobsAdvanced++;
            var stepTimer = System.Diagnostics.Stopwatch.StartNew();
            if (FinalizePreload(job, waitForWorker))
            {
                _preloadJobs.RemoveAt(i);
                if (job.Timer.Elapsed.TotalSeconds >= 0.05)
                    Console.WriteLine($"[doodad-preload] {Path.GetFileName(job.Path)} prepared in " +
                                      $"{job.Timer.Elapsed.TotalSeconds:F2}s, " +
                                      $"{_preloadQueue.Count + _preloadJobs.Count} in flight");
            }
            if (stepTimer.Elapsed.TotalMilliseconds >= 8)
                Console.WriteLine($"[stream-budget] doodad finalize {Path.GetFileName(job.Path)} " +
                                  $"took {stepTimer.Elapsed.TotalMilliseconds:F0}ms");
        }

        return _preloadQueue.Count > 0 || _preloadJobs.Count > 0;
    }

    /// <summary>Drain model paths that became resident since the previous demand pass.</summary>
    public void DrainNewlyReadyModelPaths(List<string> destination)
    {
        destination.Clear();
        while (_newlyReadyModels.Count > 0) destination.Add(_newlyReadyModels.Dequeue());
    }

    /// <summary>
    /// Check each collision hull against its own render mesh, in MODEL space.
    ///
    /// This is the question that matters and it needs no placement at all:
    /// render mesh and collision hull go through the SAME instance transform,
    /// so they cannot be misaligned in the world unless they already disagree
    /// inside the file. Comparing them locally removes every other variable.
    ///
    /// A hull that sits INSIDE its render bounds is correct and the geometry is
    /// simply coarser than what you see - a tree's hull is the trunk, never the
    /// canopy, so wireframe that ignores the leaves is right rather than
    /// broken. A hull that pokes OUTSIDE, or whose extents look like a
    /// permutation of the render extents, means the collision arrays use a
    /// different axis convention than the vertex array and need their own
    /// basis.
    /// </summary>
    private void ReportHullAlignment()
    {
        var withHull = _models.Values
            .Where(m => m is not null && m.CollisionTriangles.Length >= 3)
            .Select(m => m!)
            .ToList();

        if (withHull.Count == 0) return;

        int contained = 0;
        var offenders = new List<(string Path, Vector3 Leak, Vector3 RenderSize, Vector3 HullSize)>();

        foreach (var m in withHull)
        {
            // How far the hull escapes the render box on each axis, in yards.
            var low = m.LocalMin - m.CollisionMin;
            var high = m.CollisionMax - m.LocalMax;

            var leak = new Vector3(
                MathF.Max(0, MathF.Max(low.X, high.X)),
                MathF.Max(0, MathF.Max(low.Y, high.Y)),
                MathF.Max(0, MathF.Max(low.Z, high.Z)));

            // A little slop is normal; hulls are hand-made and can bulge.
            if (leak.X < 0.5f && leak.Y < 0.5f && leak.Z < 0.5f) { contained++; continue; }

            offenders.Add((
                Path.GetFileName(m.SourcePath),
                leak,
                m.LocalMax - m.LocalMin,
                m.CollisionMax - m.CollisionMin));
        }

        Console.WriteLine(
            $"[doodad] hull check: {contained}/{withHull.Count} collision hull(s) sit inside their " +
            $"render mesh (collision basis {CollisionBasisIndex} - {BasisName(CollisionBasisIndex)})");

        foreach (var o in offenders.OrderByDescending(o => o.Leak.Length()).Take(6))
        {
            Console.WriteLine(
                $"[doodad]   {o.Path,-34} escapes by ({o.Leak.X,6:F1},{o.Leak.Y,6:F1},{o.Leak.Z,6:F1})  " +
                $"render ({o.RenderSize.X,6:F1},{o.RenderSize.Y,6:F1},{o.RenderSize.Z,6:F1})  " +
                $"hull ({o.HullSize.X,6:F1},{o.HullSize.Y,6:F1},{o.HullSize.Z,6:F1})");
        }

        if (offenders.Count == 0)
        {
            Console.WriteLine(
                "[doodad] hulls agree with the render meshes - collision is COARSER than the " +
                "visual by design (a tree's hull is its trunk), not misplaced");
        }
        else
        {
            Console.WriteLine(
                $"[doodad] {offenders.Count} hull(s) still escape their render bounds. Permuted hull sizes " +
                "mean the collision basis is wrong - try another render.doodadCollisionBasis");
        }
    }

    /// <summary>
    /// Add a doodad whose transform someone else worked out — used for the
    /// furniture embedded in buildings, which is placed in WMO local space and
    /// rides that building's own transform out into the world.
    ///
    /// Everything downstream is shared with ADT doodads: the same model cache,
    /// the same VAO per model, the same batching, and the same collision hull
    /// treatment. A bed is solid for exactly the same reason a tree is.
    /// </summary>
    /// <param name="light">
    /// Baked interior light for this placement: rgb = MODD.color / 255, a = how
    /// much daylight to use instead. Null keeps the exterior default, which is
    /// what an unlit-by-the-building placement wants.
    /// </param>
    /// <summary>Dedup-key path part: extension-less and lower-cased, so an MDDF
    /// ".mdx" placement and a WMO-embedded ".m2" placement of the SAME model share
    /// a key. Without it the two placement paths never dedup and a doodad present in
    /// both sources (the instance portal) is placed - and drawn - twice.</summary>
    private static string NormalizeModelKey(string path)
    {
        int dot = path.LastIndexOf('.');
        return (dot > 0 ? path[..dot] : path).ToLowerInvariant();
    }

    /// <summary>Console dump of every placement whose path contains <paramref name="filter"/>,
    /// with its world position - to catch a double-placed doodad like the portal.</summary>
    public void DumpEmitterPlacements(string filter)
    {
        int total = 0;
        Console.WriteLine($"[place-dump] placements matching '{filter}':");
        foreach (var (_, instances) in _byModel)
            foreach (var inst in instances)
            {
                if (filter.Length > 0 &&
                    inst.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var t = inst.Transform;
                Console.WriteLine($"[place-dump]   {inst.Path}  @ ({t.M41:F2}, {t.M42:F2}, {t.M43:F2})");
                total++;
            }
        Console.WriteLine($"[place-dump] {total} placement(s)");
    }

    /// <summary>Two same-model placements within this radius (yards) of each other
    /// are treated as one - but ONLY for emitter-carrying models (portals/effects),
    /// so tightly-clustered props (rocks, crates) are never merged. The instance
    /// portal is authored in BOTH the terrain and the entrance WMO ~1.4 yd apart.</summary>
    private const float EmitterDedupRadius = 2.5f;

    private bool ExistingEmitterPlacementNear(Model model, Matrix4x4 transform)
    {
        if (model.Emitters.Count == 0) return false;
        if (!_byModel.TryGetValue(model, out var list)) return false;
        var p = new Vector3(transform.M41, transform.M42, transform.M43);
        foreach (var inst in list)
        {
            var q = new Vector3(inst.Transform.M41, inst.Transform.M42, inst.Transform.M43);
            if (Vector3.DistanceSquared(p, q) < EmitterDedupRadius * EmitterDedupRadius)
                return true;
        }
        return false;
    }

    /// <summary>Remove an existing near-coincident placement of this emitter model
    /// so a later WMO-embedded copy supersedes a terrain one (the portal is authored
    /// in both, and the WMO copy is the more centred/intended one). Keeps _cullBounds
    /// parallel to _byModel and the counters correct.</summary>
    private bool RemoveNearEmitterPlacement(Model model, Matrix4x4 transform)
    {
        if (model.Emitters.Count == 0) return false;
        if (!_byModel.TryGetValue(model, out var list)) return false;
        var p = new Vector3(transform.M41, transform.M42, transform.M43);
        for (int i = 0; i < list.Count; i++)
        {
            var q = new Vector3(list[i].Transform.M41, list[i].Transform.M42, list[i].Transform.M43);
            if (Vector3.DistanceSquared(p, q) >= EmitterDedupRadius * EmitterDedupRadius) continue;
            if (list[i].Light.W < 0.5f) InteriorLitCount--;
            InstanceCount--;
            TotalTriangles -= model.TriangleCount;
            list.RemoveAt(i);
            if (_cullBounds.TryGetValue(model, out var cb) && i < cb.Count) cb.RemoveAt(i);
            return true;
        }
        return false;
    }

    public bool AddPlaced(string modelPath, Matrix4x4 transform, Vector4? light = null,
        int wmoInstanceId = 0, int[]? ownerGroups = null)
    {
        string key = $"{NormalizeModelKey(modelPath)}|{transform.M41:F2}|{transform.M42:F2}|{transform.M43:F2}";
        if (_placed.Contains(key)) return true;

        var model = ResolveModel(modelPath);
        if (model is null) return false;
        // A WMO-embedded effect (the entrance portal) supersedes a near terrain copy -
        // it is the more centred, intended placement. Remove the terrain one, then add.
        if (RemoveNearEmitterPlacement(model, transform))
            Console.WriteLine($"[doodad] WMO effect supersedes a near terrain copy: {modelPath}");
        _placed.Add(key);

        var (min, max) = TransformedBounds(model, transform);

        if (!_byModel.TryGetValue(model, out var list))
        {
            list = [];
            _byModel[model] = list;
        }

        list.Add(new Instance
        {
            Transform = transform,
            WorldMin = min,
            WorldMax = max,
            Path = modelPath,
            Light = light ?? new Vector4(0f, 0f, 0f, 1f),
            AppearStart = ResolveAppearStart(key),
            WmoInstanceId = wmoInstanceId,
            OwnerGroups = ownerGroups ?? [],
        });
        CullBoundsFor(model).Add(new CullBounds(min, max));

        InstanceCount++;
        if ((light ?? new Vector4(0f, 0f, 0f, 1f)).W < 0.5f) InteriorLitCount++;
        TotalTriangles += model.TriangleCount;
        return true;
    }

    // ── dynamic (server gameobject) placements ───────────────────────────────

    /// <summary>Outcome of <see cref="AddDynamic"/>. Pending means the model is
    /// still streaming (it was queued here) and the caller should retry next
    /// frame; Unavailable is permanent for the session — the M2 is missing from
    /// the MPQs or has no renderable content.</summary>
    public enum DynamicPlacement { Placed, Pending, Unavailable }

    /// <summary>
    /// Live per-key placements (server gameobjects, keyed by GUID). The
    /// Instance itself lives in <see cref="_byModel"/> next to every static
    /// doodad, so the opaque pass AND the deferred blended pass — instanced or
    /// not — draw it with no extra path; this map only remembers which Instance
    /// belongs to which key so a despawn or a re-add can find and remove it.
    /// Cleared by <see cref="ResetPlacements"/> along with everything else —
    /// the per-frame gameobject sync notices HasDynamic went false and re-adds.
    /// </summary>
    private readonly Dictionary<ulong, (Model Model, Instance Instance)> _dynamicByKey = [];
    private readonly Dictionary<(ulong HostGuid, int PropIndex),
        (Model Model, Instance Instance)> _dynamicWmoProps = [];
    private ulong _nextDynamicWmoPropIdentity = 1;

    public bool HasDynamic(ulong key) => _dynamicByKey.ContainsKey(key);

    public bool SetDynamicCollisionLive(ulong key, bool live = true)
    {
        if (!_dynamicByKey.TryGetValue(key, out var entry)) return false;
        entry.Instance.LiveCollision = live;
        return true;
    }

    /// <summary>
    /// Move one existing dynamic placement without removing/recreating its
    /// Instance. Transport cars change every frame; preserving the instance also
    /// preserves any active authored animation and avoids list churn.
    /// </summary>
    public bool TryUpdateDynamicTransform(ulong key, Matrix4x4 transform)
    {
        if (!_dynamicByKey.TryGetValue(key, out var entry) ||
            !_byModel.TryGetValue(entry.Model, out List<Instance>? instances))
            return false;
        int index = instances.IndexOf(entry.Instance);
        if (index < 0) return false;
        var (min, max) = TransformedBounds(entry.Model, transform);
        entry.Instance.Transform = transform;
        entry.Instance.WorldMin = min;
        entry.Instance.WorldMax = max;
        if (_cullBounds.TryGetValue(entry.Model, out List<CullBounds>? bounds) &&
            index < bounds.Count)
            bounds[index] = new CullBounds(min, max);
        return true;
    }

    public bool TryUpdateDynamicWmoPropTransform(
        ulong hostGuid, int propIndex, Matrix4x4 transform)
    {
        if (!_dynamicWmoProps.TryGetValue((hostGuid, propIndex), out var entry) ||
            !_byModel.TryGetValue(entry.Model, out List<Instance>? instances))
            return false;
        int index = instances.IndexOf(entry.Instance);
        if (index < 0) return false;
        var (min, max) = TransformedBounds(entry.Model, transform);
        entry.Instance.Transform = transform;
        entry.Instance.WorldMin = min;
        entry.Instance.WorldMax = max;
        if (_cullBounds.TryGetValue(entry.Model, out List<CullBounds>? bounds) &&
            index < bounds.Count)
            bounds[index] = new CullBounds(min, max);
        return true;
    }

    /// <summary>Publish one set-0 MODD prop under a dynamic WMO GameObject.
    /// The host/prop tuple owns lifecycle while a reserved synthetic identity
    /// keeps per-prop animation/particle state distinct.</summary>
    public DynamicPlacement AddDynamicWmoProp(ulong hostGuid, int propIndex,
        string modelPath, Matrix4x4 transform, Vector4 light,
        int wmoInstanceId, int[] ownerGroups)
    {
        RemoveDynamicWmoProp((hostGuid, propIndex));
        Model? model = ResolveModel(modelPath);
        if (model is null)
        {
            if (_models.ContainsKey(ModelCacheKey(modelPath)))
                return DynamicPlacement.Unavailable;
            QueuePreloadModel(modelPath, 0f, "wmo-gameobject-prop");
            return DynamicPlacement.Pending;
        }

        var (min, max) = TransformedBounds(model, transform);
        if (!_byModel.TryGetValue(model, out List<Instance>? list))
            _byModel[model] = list = [];
        ulong identity = 0xFFFF_0000_0000_0000UL |
            (_nextDynamicWmoPropIdentity++ & 0x0000_FFFF_FFFF_FFFFUL);
        var instance = new Instance
        {
            Transform = transform,
            WorldMin = min,
            WorldMax = max,
            Path = modelPath,
            DynamicGuid = identity,
            Light = light,
            WmoInstanceId = wmoInstanceId,
            OwnerGroups = ownerGroups,
            AppearStart = 0f,
        };
        list.Add(instance);
        CullBoundsFor(model).Add(new CullBounds(min, max));
        _dynamicWmoProps[(hostGuid, propIndex)] = (model, instance);
        InstanceCount++;
        if (light.W < 0.5f) InteriorLitCount++;
        TotalTriangles += model.TriangleCount;
        return DynamicPlacement.Placed;
    }

    public void RemoveDynamicWmoPropsExcept(ulong hostGuid, IReadOnlySet<int> retained)
    {
        foreach (var key in _dynamicWmoProps.Keys
                     .Where(key => key.HostGuid == hostGuid && !retained.Contains(key.PropIndex))
                     .ToArray())
            RemoveDynamicWmoProp(key);
    }

    public void RemoveDynamicWmoProps(ulong hostGuid)
    {
        foreach (var key in _dynamicWmoProps.Keys
                     .Where(key => key.HostGuid == hostGuid).ToArray())
            RemoveDynamicWmoProp(key);
    }

    private bool RemoveDynamicWmoProp((ulong HostGuid, int PropIndex) key)
    {
        if (!_dynamicWmoProps.Remove(key, out var entry)) return false;
        return RemoveDynamicInstance(entry.Model, entry.Instance);
    }

    /// <summary>
    /// Reference fishing-line far endpoint: the gameobject's placement base plus
    /// half of its scaled authored bounding-box height. WorldMin.Z is not used;
    /// the bobber pivot remains the waterline authority.
    /// </summary>
    public bool TryGetDynamicFishingLineEnd(ulong key, out Vector3 endpoint)
    {
        endpoint = default;
        if (!_dynamicByKey.TryGetValue(key, out var entry)) return false;
        Instance instance = entry.Instance;
        float localHeight = MathF.Max(0f, entry.Model.LocalMax.Z - entry.Model.LocalMin.Z);
        float scale = new Vector3(instance.Transform.M11, instance.Transform.M12,
            instance.Transform.M13).Length();
        float height = localHeight * scale;
        endpoint = new Vector3(instance.Transform.M41, instance.Transform.M42,
            instance.Transform.M43 + height * .5f);
        return true;
    }

    /// <summary>
    /// Expose the already-parsed animation event timeline for one dynamic
    /// GameObject. The renderer remains the model owner; callers receive a
    /// read-only reference, the exact currently armed sequence, and that arm's
    /// local monotonic clock (not the unrelated process wall clock).
    /// </summary>
    public bool TryGetDynamicEventTimeline(ulong key, out M2Model model,
        out int sequenceIndex, out double playbackSeconds)
    {
        model = null!;
        sequenceIndex = -1;
        playbackSeconds = 0;
        if (!_dynamicByKey.TryGetValue(key, out var entry) ||
            entry.Model.EventSource is not { } source || source.Sequences.Count == 0)
            return false;
        model = source;
        if (entry.Model.BoneAnimator is { } animator &&
            entry.Instance.OneShot is { } oneShot)
        {
            sequenceIndex = animator.FindOrBake(oneShot.AnimationId,
                includeStaticSequences: true)?.SequenceIndex ?? -1;
            playbackSeconds = Math.Clamp(
                NowSeconds - oneShot.StartedAt, 0f, oneShot.DurationSeconds);
        }
        else if (entry.Model.BoneAnimator is { } stateAnimator &&
                 entry.Instance.StateAnimation is { } state)
        {
            // A frozen missing-rest fallback never advances and therefore never
            // crosses an event marker.
            if (state.Frozen) return false;
            sequenceIndex = stateAnimator.FindOrBake(state.AnimationId,
                includeStaticSequences: true)?.SequenceIndex ?? -1;
            playbackSeconds = Math.Max(0f, NowSeconds - state.StartedAt);
        }
        else
        {
            sequenceIndex = entry.Model.BoneClip?.SequenceIndex ??
                source.TryFindSequenceIndexByAnimationId(0);
            playbackSeconds = NowSeconds;
        }
        if (sequenceIndex < 0) sequenceIndex = 0;
        return true;
    }

    /// <summary>
    /// The dynamic placement (gameobject GUID) currently under the mouse, or 0.
    /// Set per frame by the targeting pass, exactly like
    /// CreatureRenderer.HoveredGuid; the matching instance draws with an
    /// additive brightness boost in BOTH the opaque and blended passes,
    /// instanced or not (the boost rides the per-instance VBO, so the deferred
    /// blended pass inherits it for free).
    /// </summary>
    public ulong HighlightedDynamicKey { get; set; }

    /// <summary>The vanilla hover brighten — the same 64/255 additive boost the
    /// creature/player shaders use for a hovered or selected unit.</summary>
    private const float DynamicHighlightBoost = 64f / 255f;

    /// <summary>
    /// Nearest dynamic placement (server gameobject) hit by a world ray, for
    /// mouse-over picking. The world AABB and oriented model box are broad phases;
    /// the accepted hit is against the model's visible render triangles, matching
    /// the object mesh the player sees. Empty corners around rotated signs and
    /// benches therefore cannot cover a nearby NPC.
    /// Static doodads are never tested: a tree is scenery, not an entity.
    /// A hit strictly beyond <paramref name="maxDistance"/> does not count, so
    /// callers can pass the nearest unit hit and let the unit win ties.
    /// </summary>
    public bool TryPickDynamic(
        Vector3 origin, Vector3 direction, float maxDistance, out ulong key, out float distance)
    {
        key = 0;
        distance = maxDistance;
        bool hit = false;
        foreach (var (candidate, entry) in _dynamicByKey)
        {
            if (RayDynamicRenderMesh(origin, direction, distance,
                    entry.Model, entry.Instance, out float t) &&
                t < distance)
            {
                distance = t;
                key = candidate;
                hit = true;
            }
        }
        foreach (var (owner, entry) in _dynamicWmoProps)
        {
            if (RayDynamicRenderMesh(origin, direction, distance,
                    entry.Model, entry.Instance, out float t) &&
                t < distance)
            {
                distance = t;
                key = owner.HostGuid;
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>
    /// Whether a world point lies on or immediately beside one of a dynamic GameObject's
    /// oriented model bounds. Used only to recognize the WMO surface supporting a flush-mounted
    /// sign/mailbox; unlike a unioned world AABB, this cannot bless an unrelated point in the
    /// empty corners around a rotated prop.
    /// </summary>
    public bool IsWorldPointNearDynamicPickBounds(ulong key, Vector3 point, float tolerance)
    {
        if (_dynamicByKey.TryGetValue(key, out var direct) &&
            IsWorldPointNearModelBounds(point, tolerance, direct.Model, direct.Instance))
            return true;
        foreach (var (owner, entry) in _dynamicWmoProps)
        {
            if (owner.HostGuid != key) continue;
            if (IsWorldPointNearModelBounds(point, tolerance, entry.Model, entry.Instance))
                return true;
        }
        return false;
    }

    private static bool RayDynamicRenderMesh(Vector3 origin, Vector3 direction,
        float maxDistance, Model model, Instance instance, out float worldDistance)
    {
        worldDistance = 0f;
        if (maxDistance <= 0f || direction.LengthSquared() < 1e-12f ||
            model.PickVertices.Length == 0 || model.PickIndices.Length < 3 ||
            !Matrix4x4.Invert(instance.Transform, out Matrix4x4 inverse))
            return false;

        Vector3 worldDirection = Vector3.Normalize(direction);
        if (!RayAabb(origin, worldDirection, instance.WorldMin, instance.WorldMax,
                out float broadHit) || broadHit >= maxDistance)
            return false;

        Vector3 localOrigin = Vector3.Transform(origin, inverse);
        Vector3 localEnd = Vector3.Transform(origin + worldDirection * maxDistance, inverse);
        Vector3 localDelta = localEnd - localOrigin;
        float localLimit = localDelta.Length();
        if (localLimit <= 1e-6f) return false;

        Vector3 localDirection = localDelta / localLimit;
        if (!RayAabb(localOrigin, localDirection, model.LocalMin, model.LocalMax,
                out float orientedHit) || orientedHit > localLimit)
            return false;

        float nearestLocal = localLimit;
        bool found = false;
        for (int i = 0; i + 2 < model.PickIndices.Length; i += 3)
        {
            int ia = model.PickIndices[i];
            int ib = model.PickIndices[i + 1];
            int ic = model.PickIndices[i + 2];
            if ((uint)ia >= (uint)model.PickVertices.Length ||
                (uint)ib >= (uint)model.PickVertices.Length ||
                (uint)ic >= (uint)model.PickVertices.Length)
                continue;
            if (!RayTriangle(localOrigin, localDirection,
                    model.PickVertices[ia], model.PickVertices[ib], model.PickVertices[ic],
                    out float localDistance) || localDistance > nearestLocal)
                continue;
            nearestLocal = localDistance;
            found = true;
        }
        if (!found) return false;

        Vector3 localPoint = localOrigin + localDirection * nearestLocal;
        Vector3 worldPoint = Vector3.Transform(localPoint, instance.Transform);
        worldDistance = Vector3.Distance(origin, worldPoint);
        return worldDistance < maxDistance;
    }

    private static bool IsWorldPointNearModelBounds(Vector3 point, float tolerance,
        Model model, Instance instance)
    {
        if (tolerance < 0f || !Matrix4x4.Invert(instance.Transform, out Matrix4x4 inverse))
            return false;
        Vector3 localPoint = Vector3.Transform(point, inverse);
        Vector3 nearestLocal = Vector3.Clamp(localPoint, model.LocalMin, model.LocalMax);
        Vector3 nearestWorld = Vector3.Transform(nearestLocal, instance.Transform);
        return Vector3.DistanceSquared(point, nearestWorld) <= tolerance * tolerance;
    }
    /// <summary>
    /// Raycast owner-keyed M2 collision at its current retained transform. Lift cars move every
    /// frame and cannot be baked into the static BVH without leaving a solid ghost behind.
    /// </summary>
    public bool TryRaycastDynamicCollision(Vector3 origin, Vector3 direction,
        float maxDistance, out ulong key, out RayHit hit, Predicate<ulong>? accept = null)
    {
        key = 0;
        hit = default;
        if (maxDistance <= 0f || direction.LengthSquared() < 1e-12f) return false;

        Vector3 worldDirection = Vector3.Normalize(direction);
        float best = maxDistance;
        bool found = false;
        foreach ((ulong candidate, var entry) in _dynamicByKey)
        {
            if (accept is not null && !accept(candidate)) continue;
            Instance instance = entry.Instance;
            Vector3[] triangles = entry.Model.CollisionTriangles;
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

            for (int index = 0; index + 2 < triangles.Length; index += 3)
            {
                if (!RayTriangle(localOrigin, localDirection,
                        triangles[index], triangles[index + 1], triangles[index + 2],
                        out float localDistance) ||
                    localDistance < 0f || localDistance > localLimit)
                    continue;

                Vector3 localPoint = localOrigin + localDirection * localDistance;
                Vector3 worldPoint = Vector3.Transform(localPoint, instance.Transform);
                float worldDistance = Vector3.Distance(origin, worldPoint);
                if (worldDistance > best) continue;

                Vector3 localNormal = Vector3.Cross(
                    triangles[index + 1] - triangles[index],
                    triangles[index + 2] - triangles[index]);
                Vector3 worldNormal = Vector3.TransformNormal(localNormal, instance.Transform);
                if (worldNormal.LengthSquared() <= 1e-12f) continue;
                worldNormal = Vector3.Normalize(worldNormal);
                if (Vector3.Dot(worldNormal, worldDirection) > 0f) worldNormal = -worldNormal;

                best = worldDistance;
                key = candidate;
                hit = new RayHit(worldDistance, worldPoint, worldNormal, index / 3);
                found = true;

                localEnd = Vector3.Transform(origin + worldDirection * best, inverse);
                localDelta = localEnd - localOrigin;
                localLimit = localDelta.Length();
                if (localLimit > 1e-6f) localDirection = localDelta / localLimit;
            }
        }
        return found;
    }

    /// <summary>Slab test. Enter distance is 0 when the origin is inside the
    /// box, which keeps a gameobject pickable while standing against it.</summary>
    private static bool RayAabb(
        Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float enter)
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

    private static bool RayTriangle(
        Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        const float epsilon = 1e-7f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < epsilon) return false;
        float inverse = 1f / determinant;
        Vector3 t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > epsilon;
    }

    /// <summary>
    /// Place (or replace) a dynamic per-entity model. Deliberately NOT routed
    /// through <see cref="AddPlaced"/>: that path's positional dedup key is
    /// wrong for entities — a gameobject's identity is its GUID, and a re-add
    /// with a new transform/display must replace the old placement, not be
    /// swallowed as a duplicate.
    /// </summary>
    public DynamicPlacement AddDynamic(ulong key, string modelPath, Matrix4x4 transform,
        bool liveCollision = false, Vector4? light = null)
    {
        RemoveDynamic(key);

        var model = ResolveModel(modelPath);
        if (model is null)
        {
            // A cached null is the permanent "missing / unrenderable" verdict;
            // an absent cache entry just means the model has not streamed yet.
            if (_models.ContainsKey(ModelCacheKey(modelPath)))
                return DynamicPlacement.Unavailable;
            QueuePreloadModel(modelPath, 0f, "gameobject");
            return DynamicPlacement.Pending;
        }

        var (min, max) = TransformedBounds(model, transform);

        if (!_byModel.TryGetValue(model, out var list))
        {
            list = [];
            _byModel[model] = list;
        }

        var instance = new Instance
        {
            Transform = transform,
            WorldMin = min,
            WorldMax = max,
            Path = modelPath,
            DynamicGuid = key,
            LiveCollision = liveCollision,
            AppearStart = ResolveAppearStart($"go|{key:X16}"),
            Light = light ?? ExteriorLight,
        };
        list.Add(instance);
        CullBoundsFor(model).Add(new CullBounds(min, max));
        _dynamicByKey[key] = (model, instance);
        if (instance.Light.W < 0.5f) InteriorLitCount++;

        InstanceCount++;
        TotalTriangles += model.TriangleCount;
        return DynamicPlacement.Placed;
    }

    /// <summary>
    /// Re-light one dynamic placement (a server gameobject) - the room under it
    /// resolved after it was placed, or it moved. Same payload as MODD light:
    /// rgb = baked colour / 255, a = daylight blend (0 interior, 1 exterior).
    /// </summary>
    public bool TrySetDynamicLight(ulong key, Vector4 light)
    {
        if (!_dynamicByKey.TryGetValue(key, out var entry)) return false;
        Instance instance = entry.Instance;
        if (instance.Light == light) return true;
        if (instance.Light.W < 0.5f) InteriorLitCount--;
        instance.Light = light;
        if (light.W < 0.5f) InteriorLitCount++;
        return true;
    }

    /// <summary>
    /// Arm one exact AnimationData clip on one dynamic placement. Missing placements,
    /// static models, and models that do not author the requested clip reject the arm;
    /// callers then retain their existing immediate/no-animation behavior.
    /// </summary>
    public bool TryPlayDynamicAnimation(ulong key, int animationId,
        out float durationSeconds)
    {
        durationSeconds = 0;
        if (!DynamicGameObjectAnimationEnabled) return false;
        if (!_dynamicByKey.TryGetValue(key, out var entry) ||
            entry.Model.BoneAnimator is null)
            return false;
        M2Animator.Clip? clip = entry.Model.BoneAnimator.FindOrBake(animationId);
        if (clip is null || clip.DurationSeconds <= 0) return false;
        durationSeconds = clip.DurationSeconds;
        entry.Instance.OneShot = new(animationId, NowSeconds, durationSeconds);
        return true;
    }

    /// <summary>
    /// Apply one GAMEOBJECT_STATE observation to an owner-local M2. First sight
    /// snaps to the held rest pose. A real edge arms exactly one transition
    /// window and preloads the destination rest pose it will settle onto.
    /// Missing/static models remain rendered at their loader pose.
    /// </summary>
    public bool TryApplyDynamicStateAnimation(ulong key, uint? previousState,
        uint state, out int playedAnimationId, out bool transition)
    {
        playedAnimationId = -1;
        transition = false;
        if (!_dynamicByKey.TryGetValue(key, out var entry)) return false;
        // Report a successfully consumed static state while the unsafe animation lane is
        // quarantined. This prevents a retry every frame and preserves the pre-animation
        // renderer without suppressing the GameObject itself.
        if (!DynamicGameObjectAnimationEnabled) return true;
        if (entry.Model.BoneAnimator is not { } animator) return true;

        bool Owns(int id) => animator.FindOrBake(
            id, includeStaticSequences: true) is not null;
        GameObjectAnimationLaw.StatePlay? play =
            GameObjectAnimationLaw.ResolveStatePlay(previousState, state);
        if (play is not { } requested)
        {
            entry.Instance.OneShot = null;
            entry.Instance.StateAnimation = null;
            return true;
        }

        // Resolve and retain the destination pose before arming a motion. The
        // render sampler automatically falls back to it when the one-shot's
        // exact window ends.
        if (GameObjectAnimationLaw.RestAnimationId(state) is int restRequested)
        {
            GameObjectAnimationLaw.OwnedAnimation rest =
                GameObjectAnimationLaw.RemapMissing(restRequested, Owns);
            if (animator.FindOrBake(rest.AnimationId,
                    includeStaticSequences: true) is not null)
                entry.Instance.StateAnimation = new(
                    rest.AnimationId, NowSeconds, rest.Frozen);
        }

        GameObjectAnimationLaw.OwnedAnimation resolved =
            GameObjectAnimationLaw.RemapMissing(requested.AnimationId, Owns);
        M2Animator.Clip? clip = animator.FindOrBake(resolved.AnimationId,
            includeStaticSequences: true);
        if (clip is null) return true;

        playedAnimationId = resolved.AnimationId;
        transition = requested.Kind == GameObjectAnimationLaw.StatePlayKind.Motion;
        if (transition && clip.DurationSeconds > 0f && !resolved.Frozen)
            entry.Instance.OneShot = new(
                resolved.AnimationId, NowSeconds, clip.DurationSeconds);
        else
        {
            entry.Instance.OneShot = null;
            entry.Instance.StateAnimation = new(
                resolved.AnimationId, NowSeconds, resolved.Frozen);
        }
        return true;
    }

    /// <summary>Remove a dynamic placement (despawn, out-of-range, or the first
    /// half of a re-add). The parallel cull-bounds entry goes at the same index,
    /// like <see cref="RemoveNearEmitterPlacement"/>; RebuildCullBounds would
    /// self-heal any drift regardless.</summary>
    public bool RemoveDynamic(ulong key)
    {
        RemoveDynamicArtKit(key);
        if (!_dynamicByKey.Remove(key, out var entry)) return false;
        return RemoveDynamicInstance(entry.Model, entry.Instance);
    }

    private bool RemoveDynamicInstance(Model model, Instance instance)
    {
        if (_byModel.TryGetValue(model, out var list))
        {
            int index = list.IndexOf(instance);
            if (index >= 0)
            {
                if (instance.Light.W < 0.5f) InteriorLitCount--;
                list.RemoveAt(index);
                if (_cullBounds.TryGetValue(model, out var bounds) && index < bounds.Count)
                    bounds.RemoveAt(index);
                InstanceCount--;
                TotalTriangles -= model.TriangleCount;
                return true;
            }
        }
        return false;
    }

    /// <summary>Report after a batch of AddPlaced calls.</summary>
    public void ReportInterior(int requested, int placed, double seconds)
        => Console.WriteLine(
            $"[doodad] building interiors: {placed}/{requested} placement(s) " +
            $"({requested - placed} model(s) unavailable) in {seconds:F1}s");

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>World Builder ghost (shared_docs/WORLD_BUILDER.md §4): the exact transform an MDDF
    /// entry with these placement-space fields gets once published, for <see cref="AddDynamic"/>.</summary>
    public static Matrix4x4 MddfTransform(Vector3 placementPos, Vector3 rotDeg, float scale)
        => BuildPlacement(new AdtTerrainReader.DoodadPlacement
        {
            PosX = placementPos.X, PosY = placementPos.Y, PosZ = placementPos.Z,
            RotX = rotDeg.X, RotY = rotDeg.Y, RotZ = rotDeg.Z, Scale = scale,
        });

    private static Matrix4x4 BuildPlacement(AdtTerrainReader.DoodadPlacement d)
    {
        const float deg = MathF.PI / 180f;

        // rotY - 270 + 180 = rotY - 90, the net heading the WMO calibration
        // arrived at once the hand-measured half turn was folded in.
        float heading = (d.RotY - 90f) * deg;

        float scale = d.Scale > 0.0001f ? d.Scale : 1f;

        // No basis: an M2's render vertices are already in placement space.
        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateRotationX(d.RotZ * deg)
             * Matrix4x4.CreateRotationZ(-d.RotX * deg)
             * Matrix4x4.CreateRotationY(heading)
             * Matrix4x4.CreateTranslation(d.PosX, d.PosY, d.PosZ)
             * PlacementToWorld;
    }

    private static (Vector3 min, Vector3 max) TransformedBounds(Model model, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? model.LocalMin.X : model.LocalMax.X,
                (c & 2) == 0 ? model.LocalMin.Y : model.LocalMax.Y,
                (c & 4) == 0 ? model.LocalMin.Z : model.LocalMax.Z);

            var p = Vector3.Transform(corner, m);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// MDDF stores .mdx (and occasionally .mdl) names, while the files in the
    /// MPQ are .m2. Vanilla tooling has always done this swap; without it every
    /// single doodad silently fails to load.
    /// </summary>
    private static IEnumerable<string> PathCandidates(string modelPath)
    {
        int dot = modelPath.LastIndexOf('.');
        if (dot > 0)
        {
            string stem = modelPath[..dot];
            yield return stem + ".m2";
            if (!modelPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                yield return modelPath;
        }
        else
        {
            yield return modelPath + ".m2";
            yield return modelPath;
        }
    }

    /// <summary>
    /// ADT MDDF records traditionally spell model files .mdx/.mdl while WMO
    /// interiors normally spell the same MPQ asset .m2. Cache by the physical
    /// M2 name so crossing from the outdoor pass to the interior pass cannot
    /// parse, texture and upload one model twice.
    /// </summary>
    public static string ModelCacheKey(string modelPath)
    {
        string path = modelPath.Replace('/', '\\');
        string extension = Path.GetExtension(path);
        if (extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m2", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^extension.Length] + ".m2";
        }

        return path;
    }

    private Model? ResolveModel(string modelPath)
    {
        string cacheKey = ModelCacheKey(modelPath);
        if (_models.TryGetValue(cacheKey, out var cached)) return cached;

        if (DemandStreaming)
            return null;

        var inflight = _preloadJobs.FirstOrDefault(
            j => j.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase));
        if (inflight is not null)
        {
            while (!FinalizePreloadBlocking(inflight)) { }
            _preloadJobs.Remove(inflight);
            return _models.GetValueOrDefault(cacheKey);
        }

        byte[]? bytes = null;
        foreach (var candidate in PathCandidates(modelPath))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) break;
        }

        if (bytes is null)
        {
            _missing.Add(modelPath);
            _models[cacheKey] = null;
            return null;
        }

        var m2 = M2Reader.Parse(bytes);
        // HasRenderableContent, not IsValid: an emitter-only model (no render
        // mesh, just particles) is real content and must load.
        if (m2 is null || !m2.HasRenderableContent)
        {
            _models[cacheKey] = null;
            return null;
        }

        var model = BuildModel(m2, modelPath);
        if (model is not null) model.SourcePath = modelPath;
        _models[cacheKey] = model;

        if (model is not null && model.CollisionTriangles.Length >= 3) CollisionModels++;

        return model;
    }

    private PreparedModel PrepareModel(string path)
    {
        byte[]? bytes = null;
        foreach (var candidate in PathCandidates(path))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) break;
        }

        if (bytes is null) return new PreparedModel { Missing = true };

        var parsed = M2Reader.Parse(bytes);
        // HasRenderableContent, not IsValid — see ResolveModel.
        if (parsed is null || !parsed.HasRenderableContent) return new PreparedModel();

        var prepared = new PreparedModel { Parsed = parsed };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in parsed.Textures)
        {
            string texturePath = source.Filename;
            if (string.IsNullOrWhiteSpace(texturePath) || !seen.Add(texturePath)) continue;

            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, texturePath);
            if (decoded is null)
            {
                prepared.Textures.Add(new PreparedTexture { Path = texturePath });
                continue;
            }

            var (bgra, width, height) = decoded.Value;
            prepared.Textures.Add(new PreparedTexture
            {
                Path = texturePath,
                Bgra = bgra,
                Width = width,
                Height = height,
            });
        }

        return prepared;
    }

    private bool FinalizePreloadBlocking(ModelPreloadJob job)
    {
        if (!job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        return FinalizePreload(job, waitForUpload: true);
    }

    private bool FinalizePreload(ModelPreloadJob job, bool waitForUpload)
    {
        try { job.Ready ??= job.Worker.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[doodad-preload] {Path.GetFileName(job.Path)} failed - {ex.Message}");
            _models[job.CacheKey] = null;
            return true;
        }
        var ready = job.Ready;

        if (ready.Missing)
        {
            _missing.Add(job.Path);
            _models[job.CacheKey] = null;
            return true;
        }

        if (ready.Parsed is null)
        {
            _models[job.CacheKey] = null;
            return true;
        }

        if (job.Upload is null)
        {
            var pendingTextures = ready.Textures
                .Where(t => !_textures.ContainsKey(t.Path))
                .ToList();
            job.Upload = _uploads.Enqueue(Path.GetFileName(job.Path), uploadGl =>
                UploadPreparedModel(uploadGl, ready.Parsed, pendingTextures));
        }
        if (waitForUpload && !job.Upload.IsCompleted)
            try { job.Upload.GetAwaiter().GetResult(); } catch { }
        if (!job.Upload.IsCompleted) return false;

        UploadedModel uploaded;
        try { uploaded = job.Upload.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[doodad-upload] {Path.GetFileName(job.Path)} failed - {ex.Message}");
            _models[job.CacheKey] = null;
            return true;
        }

        foreach (var (path, texture) in uploaded.Textures)
            if (!_textures.ContainsKey(path)) _textures[path] = texture;

        var model = BuildModel(ready.Parsed, job.Path, uploaded);
        if (model is not null) model.SourcePath = job.Path;
        _models[job.CacheKey] = model;
        if (model is not null) _newlyReadyModels.Enqueue(job.Path);
        if (model is not null && model.CollisionTriangles.Length >= 3) CollisionModels++;
        return true;
    }

    private unsafe UploadedModel UploadPreparedModel(
        GL gl, M2Model m2, IReadOnlyList<PreparedTexture> textures)
    {
        var uploaded = new UploadedModel();
        foreach (var texture in textures)
        {
            uploaded.Textures[texture.Path] = texture.Bgra is null
                ? null
                : Texture.From2D(gl, texture.Bgra, texture.Width, texture.Height, ownerGl: _gl);
        }

        var vertices = BuildVertexArray(m2, out _, out _);
        var indices = m2.Indices.ToArray();

        uploaded.Vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, uploaded.Vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uploaded.Ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, uploaded.Ebo);
        fixed (ushort* p = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        return uploaded;
    }

    private unsafe Model? BuildModel(M2Model m2, string sourcePath,
        UploadedModel? uploaded = null)
    {
        var vertices = BuildVertexArray(m2, out var min, out var max);

        var indices = m2.Indices.ToArray();
        bool hasGeometry = m2.Vertices.Count > 0 && indices.Length >= 3;
        if (!hasGeometry)
        {
            // Pure-emitter models (brazier smoke, waterfall mist) have no
            // render mesh at all — M2Reader.HasRenderableContent admits them
            // for their particle/ribbon emitters. They still get a Model (and
            // an empty VAO nothing is ever drawn from) so their emitters
            // register through the normal placement path; a model with neither
            // geometry nor emitters genuinely has nothing to offer.
            if (m2.ParticleEmitters.Count == 0 && m2.RibbonEmitters.Count == 0)
                return null;
            indices = [];
            min = max = Vector3.Zero;
        }

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

        uint instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceVbo);

        // Seed one zeroed element so the store is never a zero-byte buffer. The
        // VAO below leaves locations 3-9 permanently enabled with divisor 1
        // sourcing THIS buffer. The per-instance GameObject-pose pass draws with a
        // plain (non-instanced) glDrawElements, which still fetches those enabled
        // divisor-1 arrays at instance index 0. Before this seed, a GameObject
        // model drawn per-instance before it was ever drawn instanced backed those
        // arrays with an unallocated store, and the index-0 fetch ran off the end
        // -- the repeatable nvoglv64 access violation that forced the animation
        // lane off. One element makes the fetch in-bounds; uUseInstancing=0 makes
        // the shader ignore the value it reads there.
        InstanceData instanceSeed = default;
        _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)sizeof(InstanceData), &instanceSeed, BufferUsageARB.StreamDraw);

        // 16 floats of placement matrix, then 4 of baked light, then 1 appear-fade
        // start, then 1 hover-highlight boost: one InstanceData. Locations 3..6 are
        // the matrix rows, 7 the light, 8 the appear-fade start, 9 the highlight.
        const uint instanceStride = 22 * sizeof(float);
        for (uint row = 0; row < 4; row++)
        {
            uint location = 3 + row;
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false,
                instanceStride, (void*)(row * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(location, 1);
        }

        _gl.EnableVertexAttribArray(7);
        _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(16 * sizeof(float)));
        _gl.VertexAttribDivisor(7, 1);

        _gl.EnableVertexAttribArray(8);
        _gl.VertexAttribPointer(8, 1, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(20 * sizeof(float)));
        _gl.VertexAttribDivisor(8, 1);

        _gl.EnableVertexAttribArray(9);
        _gl.VertexAttribPointer(9, 1, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(21 * sizeof(float)));
        _gl.VertexAttribDivisor(9, 1);

        _gl.BindVertexArray(0);

        var model = new Model
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            InstanceVbo = instanceVbo,
            LocalMin = min,
            LocalMax = max,
            TriangleCount = indices.Length / 3,
        };
        model.Attach(_gl);

        BuildBatches(m2, model, indices.Length, sourcePath);
        BuildPickMesh(m2, model, indices);
        if (m2.Events.Count > 0) model.EventSource = m2;
        if (m2.Attachments.Count > 0)
        {
            model.AttachmentSource = m2;
            model.AttachmentSkin = new Matrix4x4[m2.Bones.Count];
        }
        ClassifyBoneAnimation(m2, model, hasGeometry, sourcePath);
        model.CollisionTriangles = BuildCollision(m2, CollisionBasisIndex);
        model.Emitters = m2.ParticleEmitters;
        model.EmitterTexturePaths = new string[m2.ParticleEmitters.Count];
        for (int i = 0; i < m2.ParticleEmitters.Count; i++)
        {
            int ti = m2.ParticleEmitters[i].Texture;
            model.EmitterTexturePaths[i] =
                ti >= 0 && ti < m2.Textures.Count ? m2.Textures[ti].Filename : "";
        }

        if (model.CollisionTriangles.Length >= 3)
        {
            var cmin = new Vector3(float.MaxValue);
            var cmax = new Vector3(float.MinValue);
            foreach (var v in model.CollisionTriangles)
            {
                cmin = Vector3.Min(cmin, v);
                cmax = Vector3.Max(cmax, v);
            }
            model.CollisionMin = cmin;
            model.CollisionMax = cmax;
        }

        return model;
    }

    private static float[] BuildVertexArray(M2Model m2, out Vector3 min, out Vector3 max)
    {
        var vertices = new float[m2.Vertices.Count * FloatsPerVertex];
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        for (int i = 0; i < m2.Vertices.Count; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.PosX;
            vertices[o + 1] = v.PosY;
            vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX;
            vertices[o + 4] = v.NormY;
            vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU;
            vertices[o + 7] = v.TexV;

            var p = new Vector3(v.PosX, v.PosY, v.PosZ);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return vertices;
    }

    /// <summary>
    /// One draw per batch. The texture chain is batch.TextureIndex ->
    /// TextureLookup -> Textures, which is the same indirection the item
    /// preview pipeline in SuperUI had to get right; going straight to
    /// Textures[TextureIndex] resolves the wrong image on most models.
    /// </summary>
    private static void BuildPickMesh(M2Model m2, Model model, ushort[] indices)
    {
        if (m2.Vertices.Count == 0 || indices.Length < 3 || model.Batches.Count == 0) return;
        model.PickVertices = m2.Vertices
            .Select(vertex => new Vector3(vertex.PosX, vertex.PosY, vertex.PosZ))
            .ToArray();

        var pickIndices = new List<ushort>();
        var includedRanges = new HashSet<(uint Start, uint Count)>();
        foreach (Batch batch in model.Batches)
        {
            var range = (batch.IndexStart, batch.IndexCount);
            if (!includedRanges.Add(range)) continue;
            int start = checked((int)batch.IndexStart);
            int count = checked((int)batch.IndexCount);
            if (start < 0 || count < 3 || start + count > indices.Length) continue;
            for (int i = start; i < start + count; i++)
                pickIndices.Add(indices[i]);
        }
        model.PickIndices = [.. pickIndices];
    }

    private void BuildBatches(M2Model m2, Model model, int indexCount, string sourcePath)
    {
        for (int batchIndex = 0; batchIndex < m2.Batches.Count; batchIndex++)
        {
            var batch = m2.Batches[batchIndex];
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;

            // Match the vanilla/Benilla M2 alpha-combine gate. HouseSmoke,
            // Blacksmith_Smoke and similar emitter models contain helper
            // geometry whose constant texture weight is zero; drawing that
            // otherwise produces the frozen white plumes and long blue lines.
            if (m2.IsBatchConstantInvisible(batch)) continue;

            var submesh = m2.Submeshes[batch.SubmeshIndex];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > indexCount) continue;

            Texture? texture = null;
            string texturePath = "";
            int textureType = -1;
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                int texIdx = m2.TextureLookup[batch.TextureIndex];
                if (texIdx >= 0 && texIdx < m2.Textures.Count)
                {
                    M2TextureRef textureRef = m2.Textures[texIdx];
                    texturePath = textureRef.Filename;
                    textureType = unchecked((int)textureRef.Type);
                    texture = ResolveTexture(texturePath);
                }
            }

            // A flat opaque grey is a renderer diagnostic, never valid world art. In
            // particular, old M2s with a replaceable/empty texture reference produce a
            // free-standing grey card which vanishes when viewed from its culled back face.
            // That is worse than omitting the unresolved batch, and it can mask the exact
            // bad asset indefinitely. Keep the whole model resident (other batches may be
            // valid), skip only this batch, and report the source once for diagnosis.
            if (texture is null)
            {
                if (_untexturedModelsReported.Add(sourcePath))
                {
                    string authored = string.IsNullOrWhiteSpace(texturePath)
                        ? $"<empty replaceable texture type {textureType}>"
                        : texturePath;
                    Console.WriteLine($"[doodad-untextured] {sourcePath} batch {batchIndex} " +
                                      $"cannot resolve {authored} - batch suppressed");
                }
                continue;
            }

            var material = batch.MaterialIndex < m2.RenderFlags.Count
                ? m2.RenderFlags[batch.MaterialIndex]
                : null;
            bool twoSided = material?.TwoSided ?? false;
            bool unlit = material?.Unlit ?? false;

            // Vanilla M2 blending modes: 0 opaque, 1 alpha-key, 2 alpha,
            // 3 no-alpha-add, 4 add, 5 modulate, 6 modulate2x. A batch with no
            // material entry is treated as alpha-key — the cutout behaviour
            // every batch used to get, and the safe guess for foliage.
            ushort blendMode = material?.BlendingMode ?? 1;
            if (blendMode > 6) blendMode = 2; // out-of-spec: plain alpha blend

            int fogMode = 0;
            if (material?.Unfogged == true) fogMode = 1;
            else if (blendMode is 3 or 4) fogMode = 2;   // additive: fog to black
            else if (blendMode == 5) fogMode = 3;        // modulate: fog to white
            else if (blendMode == 6) fogMode = 4;        // modulate2x: fog to grey

            // Animated UV translation (scrolling lava/water). A track with no
            // keys is authored-empty and stays null so the draw loop's
            // zero-offset fast path applies.
            int uvAnim = m2.GetTextureTransformForBatch(batch);
            M2AnimTrack<Vector3>? uvTrack =
                uvAnim >= 0 ? m2.TextureTransforms[uvAnim].Translation : null;
            if (uvTrack is not null && uvTrack.Keys.Count == 0) uvTrack = null;

            model.Batches.Add(new Batch
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                Texture = texture,
                TwoSided = twoSided,
                Unlit = unlit,
                BlendMode = blendMode,
                NoDepthWrite = material?.NoZWrite ?? false,
                FogMode = fogMode,
                UvTranslation = uvTrack,
            });
        }

        model.HasBlendedBatches = model.Batches.Any(b => b.Blended);
        if (model.Batches.Any(b => b.UvTranslation is not null))
            model.UvAnimSource = m2;

        // Only synthesize a whole-mesh batch for malformed/legacy models that
        // genuinely provide no batch table. If authored batches existed but
        // were all constant-invisible, restoring the complete mesh here would
        // undo vanilla's alpha cull and expose particle-emitter helper wedges.
        if (model.Batches.Count == 0 && m2.Batches.Count == 0 && indexCount > 0)
        {
            model.Batches.Add(new Batch
            {
                IndexStart = 0,
                IndexCount = (uint)indexCount,
                Texture = null,
                TwoSided = true,
            });
        }
    }

    /// <summary>
    /// Pull out the collision hull, converting it into the render mesh's space
    /// on the way so both go through the same instance transform afterwards.
    /// </summary>
    private static Vector3[] BuildCollision(M2Model m2, int basisIndex)
    {
        if (!m2.HasCollision) return [];

        var basis = Bases[Math.Clamp(basisIndex, 0, Bases.Length - 1)].M;

        var triangles = new List<Vector3>(m2.CollisionIndices.Count);

        for (int i = 0; i + 2 < m2.CollisionIndices.Count; i += 3)
        {
            int i0 = m2.CollisionIndices[i];
            int i1 = m2.CollisionIndices[i + 1];
            int i2 = m2.CollisionIndices[i + 2];

            if (i0 >= m2.CollisionVertices.Count ||
                i1 >= m2.CollisionVertices.Count ||
                i2 >= m2.CollisionVertices.Count) continue;

            triangles.Add(Vector3.Transform(m2.CollisionVertices[i0], basis));
            triangles.Add(Vector3.Transform(m2.CollisionVertices[i1], basis));
            triangles.Add(Vector3.Transform(m2.CollisionVertices[i2], basis));
        }

        return [.. triangles];
    }

    private Texture? ResolveTexture(string blpPath)
    {
        if (string.IsNullOrWhiteSpace(blpPath)) return null;
        if (_textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null)
        {
            _textures[blpPath] = null;
            return null;
        }

        var (bgra, w, h) = decoded.Value;
        var texture = Texture.From2D(_gl, bgra, w, h);
        _textures[blpPath] = texture;
        return texture;
    }

    // ── collision ────────────────────────────────────────────────────────────

    /// <summary>
    /// Feed every doodad's own collision hull into the world, using the SAME
    /// transform the renderer draws with. Models without a hull contribute
    /// nothing, which is correct: you are meant to walk through a torch.
    /// </summary>
    /// <summary>
    /// Record each solid doodad placement as a reference to its immutable
    /// collision geometry plus a transform. See CollisionBatch for why this
    /// does not expand triangles.
    /// </summary>
    public int SnapshotCollision(List<CollisionBatch> into)
    {
        int solid = 0;

        foreach (var (model, instances) in _byModel)
        {
            var tris = model.CollisionTriangles;
            if (tris.Length < 3) continue;

            foreach (var instance in instances)
            {
                if (instance.LiveCollision || instance.CosmeticOnly) continue;
                into.Add(new CollisionBatch(tris, instance.Transform, instance.Path, 0));
                solid++;
            }
        }

        return solid;
    }

    /// <summary>Dev probe: doodad placements whose origin lies within <paramref name="radius"/> of a world point.</summary>
    public void DumpInstancesNear(Vector3 worldPos, float radius)
    {
        foreach (var (model, instances) in _byModel)
            foreach (var instance in instances)
            {
                Vector3 origin = instance.Transform.Translation;
                if (Vector3.Distance(origin, worldPos) > radius) continue;
                Console.WriteLine($"[doodads-near] {instance.Path} origin=({origin.X:F1},{origin.Y:F1},{origin.Z:F2}) collisionTris={model.CollisionTriangles.Length / 3} live={instance.LiveCollision}");
            }
    }

    public void AppendCollision(CollisionWorld world)
    {
        int solid = 0, triangles = 0;

        foreach (var (model, instances) in _byModel)
        {
            var tris = model.CollisionTriangles;
            if (tris.Length < 3) continue;

            foreach (var instance in instances)
            {
                if (instance.LiveCollision || instance.CosmeticOnly) continue;
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

                solid++;
            }
        }

        Console.WriteLine(
            $"[collision] from doodads: {solid} solid instance(s) of {CollisionModels} model(s), " +
            $"{triangles:N0} triangles");
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Cutoff for the blended pass. Not zero — fully-transparent texels of an
    /// additive halo contribute nothing but still cost a blend read-modify-
    /// write — but low enough that Glow32.blp's faint outer alpha (0..0.6)
    /// survives where the opaque pass's 0.5 cutout test deleted it.
    /// </summary>
    private const float BlendedAlphaCutoff = 1f / 255f;

    /// <summary>
    /// Blended draws deferred from the opaque loop, replayed after ALL opaque
    /// geometry so a lamp halo cannot be painted over by an opaque wall drawn
    /// later. Reused every frame; a handful of entries in practice (lamps,
    /// torches, glow planes).
    /// </summary>
    private readonly List<(Model Model, Instance Instance)> _deferredBlended = [];
    private readonly List<(Model Model, uint InstanceCount)> _deferredBlendedInstanced = [];

    /// <summary>
    /// Family-A GameObject models holding a live owner-local pose this frame (a
    /// retained state pose or an unfinished transition). Rebuilt each frame from
    /// <see cref="_dynamicByKey"/>. These models -- and only these -- leave the
    /// instanced path for the per-instance pose draw, so two copies of one crate
    /// model can be open and closed at once while the static scenery around them
    /// stays fully instanced. Empty in the steady state with the lane disabled.
    /// </summary>
    private readonly HashSet<Model> _perInstanceModels = [];

    /// <summary>
    /// glBlendFunc per M2 blend mode, the same equations the WMO MOMT split
    /// uses for its modes (§3.25) extended with the additive/modulate family:
    ///   2 alpha        (SRC_ALPHA, ONE_MINUS_SRC_ALPHA)
    ///   3 no-alpha-add (ONE, ONE)
    ///   4 add          (SRC_ALPHA, ONE)
    ///   5 modulate     (DST_COLOR, ZERO)
    ///   6 modulate2x   (DST_COLOR, SRC_COLOR)
    /// </summary>
    private void SetBatchBlendFunc(ushort blendMode)
    {
        switch (blendMode)
        {
            case 3: _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); break;
            case 4: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;
            case 5: _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero); break;
            case 6: _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor); break;
            default: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); break;
        }
    }

    /// <summary>
    /// The cutoff the OPAQUE pass hands doodad.frag for a batch. Mode 1
    /// (alpha-key) keeps the classic cutout test; mode 0 (opaque) gets none,
    /// so junk alpha in an opaque material's BLP cannot discard fragments.
    /// Untextured batches never test — see the AlphaCutoff docstring.
    /// </summary>
    private float OpaqueCutoffFor(Batch batch)
        => batch.Texture is not null && batch.BlendMode == 1 ? AlphaCutoff : 0f;

    /// <summary>
    /// The batch's animated UV offset at the world clock, or zero for static
    /// UVs. Sampled against sequence 0 (Stand — the only sequence a placed
    /// doodad plays; the Blackrock falls author exactly one) on the same
    /// NowSeconds clock the appear fade uses, so all instances of a model
    /// scroll in lockstep and the loop wraps seamlessly (the authored
    /// translations are whole UV multiples per cycle). Tracks that declare a
    /// global sequence take M2TrackSampling's global-clock path automatically.
    /// Applied as vUV = aUV + offset — the same convention GlueScene's login
    /// scene already uses for these tracks. Z is authoring garbage; only X/Y
    /// are UV.
    /// </summary>
    private Vector2 UvOffsetFor(Model model, Batch batch)
    {
        if (batch.UvTranslation is null || model.UvAnimSource is null) return Vector2.Zero;
        // A bone-animated doodad samples its UV tracks on the same absolute-
        // timeline clock the bones use, so the lava bubble's burst-frame UV
        // flip lands on the same frame as the scale pop. The bubble's UV keys
        // live in the CHAINED second sequence (band 3333..6667); the
        // sequence-0 sampler below never reaches them.
        if (model.BoneAnimSource is not null)
        {
            var a = M2TrackSampling.AbsoluteVector(
                batch.UvTranslation, model.UvAnimSource, NowSeconds, Vector3.Zero);
            return new Vector2(a.X, a.Y);
        }
        var t = M2TrackSampling.Vector(
            batch.UvTranslation, model.UvAnimSource, 0, NowSeconds, Vector3.Zero);
        return new Vector2(t.X, t.Y);
    }

    // ── Animated static doodads: classification + CPU re-skin ────────────────

    /// <summary>
    /// Models currently classified bone-animated, for diagnostics.
    /// Static models never allocate animation scratch storage or touch their VBO.
    /// </summary>
    public int AnimatedModelCount { get; private set; }

    /// <summary>Loaded models routed through the camera/placement-specific billboard path.</summary>
    public int BillboardModelCount { get; private set; }

    /// <summary>
    /// Decide whether a doodad model gets the per-frame CPU re-skin, and
    /// pre-size its scratch buffers. Also inflates the model's local cull
    /// bounds by the largest authored bone scale/translation so a bubble
    /// mid-pop (2.78x) is not frustum-rejected at the screen edge — the
    /// instance AABBs are computed from these bounds at placement time.
    /// </summary>
    private void ClassifyBoneAnimation(M2Model m2, Model model, bool hasGeometry,
        string sourcePath)
    {
        if (!hasGeometry || !m2.HasSkeleton) return;

        bool cameraFacing = DoodadBillboardLaw.RequiresPerInstancePose(m2);
        bool animated = m2.Sequences.Count > 0 && m2.HasAnimatedBones;
        if (!cameraFacing && !animated) return;

        model.BoneAnimSource = m2;
        model.UsesCameraFacingBones = cameraFacing;
        model.AnimVertexScratch = new float[m2.Vertices.Count * FloatsPerVertex];
        model.AnimBoneMatrices = new Matrix4x4[m2.Bones.Count];

        // Environmental props such as Blackrock lava bubbles deliberately chain several
        // animation-0 ranges on one absolute timeline. Creature-grade decorative models
        // instead contain Stand, Walk, Run, attack, and other distinct AnimationData ids;
        // running their whole file timeline would cycle through every action, so those use
        // the normal idle clip evaluator.
        bool animationZeroTimeline = animated &&
            m2.Sequences.All(sequence => sequence.AnimationId == 0);
        if (animated && !animationZeroTimeline)
        {
            model.BoneAnimator = M2Animator.Build(m2,
                [0, 153, 154, 155, 156, 157], includeStaticSequences: true);
            model.BoneClip = model.BoneAnimator?.Find(0)
                ?? model.BoneAnimator?.FindSequenceOrBake(0, includeStaticSequences: true);
        }
        if (animated) AnimatedModelCount++;
        if (cameraFacing)
        {
            BillboardModelCount++;
            int unresolved = model.Batches.Count(batch => batch.Texture is null);
            Console.WriteLine($"[doodad-billboard] {sourcePath} — {m2.Bones.Count} bone(s), " +
                $"{m2.Vertices.Count} vertices, {model.Batches.Count} batch(es), " +
                $"{unresolved} unresolved texture batch(es)");
        }

        // Conservative animated bounds: every corner scaled by the largest
        // authored scale key about every bone pivot, padded by the largest
        // translation key. Exact for the bubble (uniform scale about pivots).
        float maxScale = 1f;
        float maxTranslation = 0f;
        foreach (var bone in m2.Bones)
        {
            foreach (var k in bone.Scale.Keys)
                maxScale = MathF.Max(maxScale,
                    MathF.Max(MathF.Abs(k.X), MathF.Max(MathF.Abs(k.Y), MathF.Abs(k.Z))));
            foreach (var k in bone.Translation.Keys)
                maxTranslation = MathF.Max(maxTranslation, k.Length());
        }
        if (maxScale > 1f || maxTranslation > 0f)
        {
            var min = model.LocalMin;
            var max = model.LocalMax;
            foreach (var bone in m2.Bones)
            {
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? model.LocalMin.X : model.LocalMax.X,
                        (c & 2) == 0 ? model.LocalMin.Y : model.LocalMax.Y,
                        (c & 4) == 0 ? model.LocalMin.Z : model.LocalMax.Z);
                    var p = bone.Pivot + (corner - bone.Pivot) * maxScale;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            var pad = new Vector3(maxTranslation);
            model.LocalMin = min - pad;
            model.LocalMax = max + pad;
        }

        if (animated)
            Console.WriteLine(
                $"[doodad] animated doodad: {m2.Name} — {m2.Bones.Count} bone(s), " +
                $"{m2.Vertices.Count} vertice(s), " +
                (model.BoneClip is { } clip
                    ? $"idle {clip.AnimationId} ({clip.DurationSeconds:F3}s)"
                    : $"timeline loop {m2.AbsoluteTimelineDurationMs} ms"));
    }

    /// <summary>
    /// Sample the bone tracks at the world clock, re-skin the (tiny) vertex
    /// buffer on the CPU, and re-upload the model's shared VBO. Runs once per
    /// MODEL per frame — instances share the result, so all placements of one
    /// bubble model animate in sync, exactly as they share the mesh. The VAO's
    /// attribute pointers captured the VBO at build time, so re-specifying the
    /// buffer's data needs no attribute rebinding.
    ///
    /// Bone law (vanilla M2): p' = pivot + translation(t) + R(t)·S(t)·(p − pivot),
    /// composed with the parent. In System.Numerics row-vector convention that
    /// is T(−pivot)·S·R·T(pivot+translation), then local · parent.
    /// </summary>
    private void EvaluateAnimationMatrices(Model model, M2Model m2,
        DynamicAnimation? oneShot, DynamicStateAnimation? stateAnimation, Matrix4x4[] mats)
    {
        if (model.BoneAnimator is not null && oneShot is not null)
        {
            M2Animator.Clip? clip = model.BoneAnimator.FindOrBake(oneShot.AnimationId);
            model.BoneAnimator.Evaluate(clip,
                MathF.Min(NowSeconds - oneShot.StartedAt, oneShot.DurationSeconds),
                NowSeconds, mats);
        }
        else if (model.BoneAnimator is not null && stateAnimation is not null)
        {
            M2Animator.Clip? clip = model.BoneAnimator.FindOrBake(
                stateAnimation.AnimationId, includeStaticSequences: true);
            float elapsed = stateAnimation.Frozen
                ? 0f : MathF.Max(0f, NowSeconds - stateAnimation.StartedAt);
            model.BoneAnimator.Evaluate(clip, elapsed, NowSeconds, mats);
        }
        else if (model.BoneAnimator is not null && model.BoneClip is not null)
        {
            model.BoneAnimator.Evaluate(model.BoneClip, NowSeconds, NowSeconds, mats);
        }
        else
        {
            for (int i = 0; i < m2.Bones.Count; i++)
            {
                var bone = m2.Bones[i];
                var translation = M2TrackSampling.AbsoluteVector(
                    bone.Translation, m2, NowSeconds, Vector3.Zero);
                var rotation = M2TrackSampling.AbsoluteQuaternion(bone.Rotation, m2, NowSeconds);
                var scale = M2TrackSampling.AbsoluteVector(bone.Scale, m2, NowSeconds, Vector3.One);

                var local = Matrix4x4.CreateTranslation(-bone.Pivot)
                          * Matrix4x4.CreateScale(scale)
                          * Matrix4x4.CreateFromQuaternion(rotation)
                          * Matrix4x4.CreateTranslation(bone.Pivot + translation);

                // M2 bones are ordered parent-before-child; a forward reference
                // (malformed) degrades to treating the bone as a root.
                mats[i] = bone.ParentBone >= 0 && bone.ParentBone < i
                    ? local * mats[bone.ParentBone]
                    : local;
            }
        }

    }

    private unsafe void UpdateAnimatedVertices(Model model, Instance? instance = null,
        Camera? camera = null)
    {
        var m2 = model.BoneAnimSource;
        if (m2 is null || model.AnimVertexScratch is null || model.AnimBoneMatrices is null)
            return;
        DynamicAnimation? oneShot = instance?.OneShot;
        if (oneShot is not null &&
            NowSeconds - oneShot.StartedAt >= oneShot.DurationSeconds)
        {
            instance!.OneShot = null;
            oneShot = null;
        }
        DynamicStateAnimation? stateAnimation = instance?.StateAnimation;
        ulong sampleGuid = oneShot is null && stateAnimation is null
            ? 0 : instance!.DynamicGuid;
        bool perInstanceBillboard = model.UsesCameraFacingBones &&
            instance is not null && camera is not null;
        if (!perInstanceBillboard && model.LastAnimSampleTime == NowSeconds &&
            model.LastAnimSampleDynamicGuid == sampleGuid) return;
        model.LastAnimSampleTime = NowSeconds;
        model.LastAnimSampleDynamicGuid = sampleGuid;

        var mats = model.AnimBoneMatrices;
        EvaluateAnimationMatrices(model, m2, oneShot, stateAnimation, mats);

        if (perInstanceBillboard)
            DoodadBillboardLaw.Apply(m2, instance!.Transform, camera!, mats);

        var dst = model.AnimVertexScratch;
        for (int i = 0; i < m2.Vertices.Count; i++)
        {
            var v = m2.Vertices[i];
            var bindPos = new Vector3(v.PosX, v.PosY, v.PosZ);
            var bindNorm = new Vector3(v.NormX, v.NormY, v.NormZ);

            Vector3 pos = Vector3.Zero, norm = Vector3.Zero;
            float total = 0f;
            for (int w = 0; w < 4; w++)
            {
                byte weight = w switch
                {
                    0 => v.BoneWeight0, 1 => v.BoneWeight1,
                    2 => v.BoneWeight2, _ => v.BoneWeight3,
                };
                if (weight == 0) continue;
                byte index = w switch
                {
                    0 => v.BoneIndex0, 1 => v.BoneIndex1,
                    2 => v.BoneIndex2, _ => v.BoneIndex3,
                };
                if (index >= mats.Length) continue;
                float f = weight / 255f;
                pos += Vector3.Transform(bindPos, mats[index]) * f;
                norm += Vector3.TransformNormal(bindNorm, mats[index]) * f;
                total += f;
            }
            if (total < 1e-3f)
            {
                pos = bindPos;
                norm = bindNorm;
            }
            else if (total < 0.999f || total > 1.001f)
            {
                float inv = 1f / total;
                pos *= inv;
            }
            if (norm.LengthSquared() > 1e-12f) norm = Vector3.Normalize(norm);

            int o = i * FloatsPerVertex;
            dst[o + 0] = pos.X;
            dst[o + 1] = pos.Y;
            dst[o + 2] = pos.Z;
            dst[o + 3] = norm.X;
            dst[o + 4] = norm.Y;
            dst[o + 5] = norm.Z;
            dst[o + 6] = v.TexU;
            dst[o + 7] = v.TexV;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        fixed (float* p = dst)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(dst.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
    }

    public void Render(Camera camera) => Render(camera, null);

    /// <summary>
    /// Draw props with an optional absolute-world clip plane. The caller owns
    /// GL_CLIP_DISTANCE0 state; the no-plane overload remains the active-world
    /// path after a prepared renderer is promoted.
    /// </summary>
    public unsafe void Render(Camera camera, WorldClipPlane? worldClipPlane)
    {
        long started = Stopwatch.GetTimestamp();
        DrawnLastFrame = 0;
        DrawCallsLastFrame = 0;
        TrianglesLastFrame = 0;
        DistanceCulledLastFrame = 0;
        FrustumCulledLastFrame = 0;
        if (!Enabled || _shader is null || _byModel.Count == 0)
        {
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        // Retained despawn placements no longer have gameplay entities, but their
        // cosmetic children still follow the parent's current animation until removal.
        UpdateDynamicArtKitPoses();

        // Which GameObject models hold a live owner-local pose this frame -- a
        // retained state pose or an unfinished transition. Only these leave the
        // instanced path (and only for their own placements), so a resting chest
        // lid or a swinging door never de-instances the scenery around it. Empty
        // in the steady state, which keeps the whole world on the fast path.
        _perInstanceModels.Clear();
        foreach (Model model in _byModel.Keys)
            if (model.UsesCameraFacingBones)
                _perInstanceModels.Add(model);
        foreach (var entry in _dynamicByKey.Values)
            if (entry.Instance.StateAnimation is not null ||
                entry.Instance.OneShot is { } oneShot &&
                NowSeconds - oneShot.StartedAt < oneShot.DurationSeconds)
                _perInstanceModels.Add(entry.Model);

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uWorldClipPlane", worldClipPlane is { IsValid: true } clip
            ? clip.RelativeEquation(camera.Position)
            : new Vector4(0f, 0f, 0f, 1f));
        _shader.Set("uCutActive", Cut is not null ? 1 : 0);
        _shader.Set("uCutRect", Cut?.RelativeRect(camera.Position) ?? Vector4.Zero);
        _shader.Set("uCutZ", Cut?.RelativeZ(camera.Position) ?? 0f);
        SetSightUniforms(camera.Position);
        if (PartySight is { } partySight) partySight.Apply(_shader, camera.Position);
        else PartySightPass.BindInactive(_shader);
        // uUseInstancing is set per pass below (1 for RenderInstanced, 0 for the
        // per-instance GameObject-pose pass), not once for the whole frame.
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        CarriedLightFrame.Upload(_shader, camera.Position);
        _shader.Set("uTexture", 0);
        _shader.Set("uVertexColorScale", VertexColorScale);
        _shader.Set("uStyleWeight", 0.42f);
        _shader.Set("uAppearFadeEnabled", AppearFade ? 1 : 0);
        _shader.Set("uNow", NowSeconds);
        _shader.Set("uAppearFadeSecs", MathF.Max(AppearFadeSeconds, 0.0001f));
        _shader.Set("uBlendedBatch", 0);
        _shader.Set("uHighlight", 0f);

        // The hovered gameobject's Instance, if it is placed right now. Resolved
        // once; the draw loops compare by reference.
        Instance? highlighted = HighlightedDynamicKey != 0 &&
            _dynamicByKey.TryGetValue(HighlightedDynamicKey, out var highlightedEntry)
                ? highlightedEntry.Instance : null;

        var viewProjection = camera.RelativeViewProjection;
        var eye = camera.Position;
        _shader.Set("uPortalLightPos",
            PortalLightRadius > 0f ? PortalLightWorldPos - eye : Vector3.Zero);
        _shader.Set("uPortalLightColor", PortalLightColor);
        _shader.Set("uPortalLightRadius", PortalLightRadius);
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);
        float maxDistanceSq = effectiveDrawDistance * effectiveDrawDistance;
        bool cullingOn = true;

        if (UseInstancing)
        {
            // The whole world, minus any model holding an owner-local pose, on the
            // instanced fast path (RenderInstanced skips exactly _perInstanceModels).
            _shader.Set("uUseInstancing", 1);
            RenderInstanced(viewProjection, eye, maxDistanceSq, highlighted, ref cullingOn);

            // Then just the stateful GameObject models, each placement drawn with
            // its own held pose: a closed chest, the one open chest you are
            // looting, a door mid-swing. Skipped entirely when none exist, so the
            // steady-state world pays nothing for this second pass.
            if (_perInstanceModels.Count > 0)
            {
                _shader.Set("uUseInstancing", 0);
                RenderNonInstanced(camera, eye, maxDistanceSq, viewProjection,
                    highlighted, _perInstanceModels, ref cullingOn);
            }

            if (!cullingOn) _gl.Enable(EnableCap.CullFace);
            _gl.BindVertexArray(0);
            MaybeLogCull(effectiveDrawDistance);
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        // Whole-world non-instanced path (UseInstancing off, the A/B baseline).
        _shader.Set("uUseInstancing", 0);
        RenderNonInstanced(camera, eye, maxDistanceSq, viewProjection,
            highlighted, null, ref cullingOn);

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        MaybeLogCull(effectiveDrawDistance);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The non-instanced path does not produce the three-way split. Zero it
        // rather than leaving the last instanced frame's numbers standing - a
        // stale timer reporting a plausible value is worse than a missing one.
        CullMilliseconds = InstanceUploadMilliseconds = DrawMilliseconds = 0;
        UploadedModelsLastFrame = FirstTouchModelsLastFrame = 0;
        CullModelsLastFrame = CullInstancesLastFrame = 0;
    }

    /// <summary>
    /// Draw doodad models with a per-instance uniform (the caller must have set
    /// uUseInstancing to 0). This is the flavour that supports owner-local
    /// GameObject bone poses, since the shared instanced VBO can hold only one
    /// pose per model. Two callers: the whole world when instancing is toggled off
    /// (<paramref name="onlyModels"/> null), and -- on the instanced fast path --
    /// only the handful of GameObject models holding a live pose this frame
    /// (<paramref name="onlyModels"/> = the stateful set), drawn right after
    /// RenderInstanced has skipped exactly those models so nothing double-draws.
    /// </summary>
    private unsafe void RenderNonInstanced(
        Camera camera, Vector3 eye, float maxDistanceSq, Matrix4x4 viewProjection,
        Instance? highlighted, HashSet<Model>? onlyModels, ref bool cullingOn)
    {
        // Appear fade needs straight-alpha blending while a doodad eases in; at
        // alpha 1 (every steady doodad) it composites identically to opaque, so
        // this is a no-op for the resident world. Depth-write stays on (benilla
        // wow_model.wgsl). Restored after the loop.
        if (AppearFade)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        _shader!.Set("uPreserveAlpha", AppearFade ? 1 : 0);

        _deferredBlended.Clear();
        bool depthWriteOn = true;

        foreach (var (model, instances) in _byModel)
        {
            if (onlyModels is not null && !onlyModels.Contains(model)) continue;
            bool bound = false;

            foreach (var instance in instances)
            {
                if (instance.WmoInstanceId != 0 &&
                    PortalVisibility is not null &&
                    !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
                // Distance first: it is a subtraction, the frustum test is six
                // dot products, and most doodads fail on distance.
                var centre = (instance.WorldMin + instance.WorldMax) * 0.5f;
                if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                if (FrustumCulling &&
                    !Camera.BoxInFrustum(viewProjection,
                        instance.WorldMin - eye,
                        instance.WorldMax - eye)) { FrustumCulledLastFrame++; continue; }

                if (model.BoneAnimSource is not null)
                    UpdateAnimatedVertices(model, instance, camera);
                if (!bound)
                {
                    _gl.BindVertexArray(model.Vao);
                    bound = true;
                }

                var modelTransform = instance.Transform;
                modelTransform.M41 -= eye.X;
                modelTransform.M42 -= eye.Y;
                modelTransform.M43 -= eye.Z;

                _shader.Set("uModel", modelTransform);
                _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);
                _shader.Set("uInstanceLight",
                    InteriorLighting ? instance.Light : ExteriorLight);
                _shader.Set("uAppearStart", instance.AppearStart);
                _shader.Set("uHighlight",
                    ReferenceEquals(instance, highlighted) ? DynamicHighlightBoost : 0f);

                // Blended batches (M2 blend modes 2-6) are deferred until all
                // opaque geometry is down; only the opaque family draws here.
                if (model.HasBlendedBatches)
                    _deferredBlended.Add((model, instance));

                foreach (var batch in model.Batches)
                {
                    if (batch.Blended) continue;

                    if (batch.TwoSided && cullingOn)
                    {
                        _gl.Disable(EnableCap.CullFace);
                        cullingOn = false;
                    }
                    else if (!batch.TwoSided && !cullingOn)
                    {
                        _gl.Enable(EnableCap.CullFace);
                        cullingOn = true;
                    }

                    if (batch.NoDepthWrite == depthWriteOn)
                    {
                        depthWriteOn = !batch.NoDepthWrite;
                        _gl.DepthMask(depthWriteOn);
                    }

                    if (batch.Texture is not null)
                    {
                        batch.Texture.Bind(0);
                        _shader.Set("uHasTexture", 1);
                    }
                    else
                    {
                        _shader.Set("uHasTexture", 0);
                    }
                    _shader.Set("uAlphaCutoff", OpaqueCutoffFor(batch));

                    _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                    _shader.Set("uFogMode", batch.FogMode);
                    _shader.Set("uUvOffset", UvOffsetFor(model, batch));

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                    DrawCallsLastFrame++;
                    TrianglesLastFrame += batch.IndexCount / 3;
                }

                DrawnLastFrame++;
            }
        }

        if (AppearFade) _gl.Disable(EnableCap.Blend);
        if (!depthWriteOn) { _gl.DepthMask(true); depthWriteOn = true; }

        DrawBlendedDeferred(camera, eye, highlighted, ref cullingOn);
    }

    /// <summary>
    /// The BLENDED pass, non-instanced flavour: replay every deferred
    /// (model, instance) after the whole opaque world is down. Depth TEST stays
    /// on — a halo behind a wall is still occluded — but depth WRITE is off, so
    /// glows never punch holes for each other. Order is the opaque pass's
    /// iteration order, deliberately unsorted: the common modes (additive,
    /// modulate) are order-independent, and a global depth sort is not worth
    /// its cost for a handful of lamp halos.
    /// </summary>
    private unsafe void DrawBlendedDeferred(
        Camera camera, Vector3 eye, Instance? highlighted, ref bool cullingOn)
    {
        if (_deferredBlended.Count == 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _shader!.Set("uBlendedBatch", 1);

        Model? boundModel = null;
        foreach (var (model, instance) in _deferredBlended)
        {
            if (model.BoneAnimSource is not null)
                UpdateAnimatedVertices(model, instance, camera);
            if (!ReferenceEquals(model, boundModel))
            {
                _gl.BindVertexArray(model.Vao);
                boundModel = model;
            }

            var modelTransform = instance.Transform;
            modelTransform.M41 -= eye.X;
            modelTransform.M42 -= eye.Y;
            modelTransform.M43 -= eye.Z;

            _shader.Set("uModel", modelTransform);
            _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);
            _shader.Set("uInstanceLight",
                InteriorLighting ? instance.Light : ExteriorLight);
            _shader.Set("uAppearStart", instance.AppearStart);
            _shader.Set("uHighlight",
                ReferenceEquals(instance, highlighted) ? DynamicHighlightBoost : 0f);

            DrawBlendedBatches(model, 0, ref cullingOn);
        }

        _shader.Set("uBlendedBatch", 0);
        _gl.DepthMask(true);
        // Leave the engine-default straight-alpha func behind, not whatever
        // additive/modulate func the last batch selected.
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);
        _deferredBlended.Clear();
    }

    /// <summary>
    /// The BLENDED pass, instanced flavour. Each deferred model's InstanceVbo
    /// still holds exactly this frame's visible set — uploaded by the opaque
    /// loop, and nothing touches it in between — so this pass re-binds the VAO
    /// and issues only the blended batches over the same instances.
    /// </summary>
    private unsafe void DrawBlendedDeferredInstanced(ref bool cullingOn)
    {
        if (_deferredBlendedInstanced.Count == 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _shader!.Set("uBlendedBatch", 1);

        foreach (var (model, instanceCount) in _deferredBlendedInstanced)
        {
            _gl.BindVertexArray(model.Vao);
            DrawBlendedBatches(model, instanceCount, ref cullingOn);
        }

        _shader.Set("uBlendedBatch", 0);
        _gl.DepthMask(true);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);
        _deferredBlendedInstanced.Clear();
    }

    /// <summary>
    /// Issue a model's blend-mode-2+ batches with per-mode blend state.
    /// instanceCount 0 means the non-instanced DrawElements path; the caller
    /// has already set the per-instance uniforms (or uploaded instance data).
    /// </summary>
    private unsafe void DrawBlendedBatches(Model model, uint instanceCount, ref bool cullingOn)
    {
        foreach (var batch in model.Batches)
        {
            if (!batch.Blended) continue;

            if (batch.TwoSided && cullingOn)
            {
                _gl.Disable(EnableCap.CullFace);
                cullingOn = false;
            }
            else if (!batch.TwoSided && !cullingOn)
            {
                _gl.Enable(EnableCap.CullFace);
                cullingOn = true;
            }

            SetBatchBlendFunc(batch.BlendMode);

            if (batch.Texture is not null)
            {
                batch.Texture.Bind(0);
                _shader!.Set("uHasTexture", 1);
                _shader.Set("uAlphaCutoff", BlendedAlphaCutoff);
            }
            else
            {
                _shader!.Set("uHasTexture", 0);
                _shader.Set("uAlphaCutoff", 0f);
            }

            _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
            _shader.Set("uFogMode", batch.FogMode);
            _shader.Set("uUvOffset", UvOffsetFor(model, batch));

            if (instanceCount > 0)
            {
                _gl.DrawElementsInstanced(PrimitiveType.Triangles, batch.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)),
                    instanceCount);
                TrianglesLastFrame += (long)(batch.IndexCount / 3) * instanceCount;
            }
            else
            {
                _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                TrianglesLastFrame += batch.IndexCount / 3;
            }
            DrawCallsLastFrame++;
        }
    }

    /// <summary>
    /// Throttled per-cull diagnostic. Answers the "a doodad is well within draw
    /// distance but does not appear until I am close" question: if a nearby prop
    /// is missing from `placed` it is a streaming/placement gap, not a cull; if
    /// it shows under dist-culled or frustum-culled the render cull is eating it.
    /// Roughly one line every two seconds at 60 FPS.
    /// </summary>
    private void MaybeLogCull(float effectiveDrawDistance)
    {
        if (++_cullLogFrames < 120) return;
        _cullLogFrames = 0;
        Console.WriteLine(
            $"[doodad-cull] placed {InstanceCount}, drawn {DrawnLastFrame}, " +
            $"dist-culled {DistanceCulledLastFrame}, frustum-culled {FrustumCulledLastFrame}, " +
            $"drawDist {effectiveDrawDistance:F0} (slider {DrawDistance:F0}, visCap " +
            $"{(float.IsPositiveInfinity(VisibilityDistance) ? -1f : VisibilityDistance):F0})");
    }

    private List<CullBounds> CullBoundsFor(Model model)
    {
        if (!_cullBounds.TryGetValue(model, out var bounds))
        {
            bounds = [];
            _cullBounds[model] = bounds;
        }
        return bounds;
    }

    /// <summary>
    /// Bring the parallel bounds back in line if a placement path ever forgets
    /// to append. Self-healing on purpose: a cull reading a stale or short
    /// bounds list would silently draw the wrong props, and a wrong picture is
    /// far worse than a slow one. Reported once, not per frame, but reported -
    /// a silent repair is how a bug survives a whole session.
    /// </summary>
    private void RebuildCullBounds(List<Instance> instances, List<CullBounds> bounds)
    {
        bounds.Clear();
        foreach (var instance in instances)
            bounds.Add(new CullBounds(instance.WorldMin, instance.WorldMax));

        if (_cullBoundsDriftReported) return;
        _cullBoundsDriftReported = true;
        Console.WriteLine(
            "[doodad-cull] cull bounds drifted from placements and were rebuilt - " +
            "a placement path is adding to _byModel without updating _cullBounds");
    }

    private unsafe void RenderInstanced(
        Matrix4x4 viewProjection, Vector3 eye, float maxDistanceSq, Instance? highlighted,
        ref bool cullingOn)
    {
        // Three accumulators rather than three brackets around the whole loop:
        // the phases interleave per model, so only a running total is honest.
        double cullTicks = 0, uploadTicks = 0, drawTicks = 0;
        int uploadedModels = 0;
        int firstTouch = 0;
        int cullModels = 0;
        int cullInstances = 0;
        _drawnThisFrame.Clear();

        // Appear fade needs straight-alpha blending while a doodad eases in.
        //
        // IT IS NOW ENABLED PER MODEL, NOT FOR THE WHOLE PASS. At alpha 1 the
        // blend composites identically to opaque, so it was described as a
        // no-op — but it is not free: every doodad fragment in the world paid a
        // colour-buffer read-modify-write for a fade that, in the steady state,
        // no doodad is doing. Blending is now switched on only for models that
        // actually have an instance mid-fade, which in practice is a handful for
        // two seconds after a tile streams in and none at all thereafter.
        bool blendOn = false;
        bool depthWriteOn = true;
        _deferredBlendedInstanced.Clear();

        foreach (var (model, instances) in _byModel)
        {
            // A model holding an owner-local GameObject pose this frame is drawn by
            // the per-instance pass in Render (RenderNonInstanced) instead: the
            // shared instanced VBO can carry only one pose, so a closed chest and
            // the open chest beside it cannot both ride it. Skipping it here is
            // what keeps that pass from double-drawing over this one.
            if (_perInstanceModels.Contains(model)) continue;

            long cullStarted = Stopwatch.GetTimestamp();
            cullModels++;
            cullInstances += instances.Count;
            _visibleInstances.Clear();
            if (FlatCullBounds)
            {
                // The reject path never touches an Instance. About 96% of
                // placements are rejected (5,616 of 6,694 measured), so this is
                // where the pointer chases go: a linear walk over 24-byte
                // structs, dereferencing the heap object only for survivors.
                var bounds = CullBoundsFor(model);
                if (bounds.Count != instances.Count) RebuildCullBounds(instances, bounds);

                var boundsSpan = CollectionsMarshal.AsSpan(bounds);
                for (int i = 0; i < boundsSpan.Length; i++)
                {
                    ref readonly var b = ref boundsSpan[i];
                    var centre = (b.Min + b.Max) * 0.5f;
                    if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                    if (FrustumCulling &&
                        !Camera.BoxInFrustum(viewProjection,
                            b.Min - eye,
                            b.Max - eye)) { FrustumCulledLastFrame++; continue; }

                    var instance = instances[i];
                    if (instance.WmoInstanceId != 0 &&
                        PortalVisibility is not null &&
                        !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
                    var transform = instance.Transform;
                    transform.M41 -= eye.X;
                    transform.M42 -= eye.Y;
                    transform.M43 -= eye.Z;
                    _visibleInstances.Add(new InstanceData
                    {
                        Transform = transform,
                        Light = InteriorLighting ? instance.Light : ExteriorLight,
                        AppearStart = instance.AppearStart,
                        Highlight = ReferenceEquals(instance, highlighted)
                            ? DynamicHighlightBoost : 0f,
                    });
                }
            }
            else
            {
                // The original array-of-pointers walk, kept verbatim as the A/B
                // baseline. Deleting it would make the toggle a lie.
                foreach (var instance in instances)
                {
                    if (instance.WmoInstanceId != 0 &&
                        PortalVisibility is not null &&
                        !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
                    var centre = (instance.WorldMin + instance.WorldMax) * 0.5f;
                    if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                    if (FrustumCulling &&
                        !Camera.BoxInFrustum(viewProjection,
                            instance.WorldMin - eye,
                            instance.WorldMax - eye)) { FrustumCulledLastFrame++; continue; }

                    var transform = instance.Transform;
                    transform.M41 -= eye.X;
                    transform.M42 -= eye.Y;
                    transform.M43 -= eye.Z;
                    _visibleInstances.Add(new InstanceData
                    {
                        Transform = transform,
                        Light = InteriorLighting ? instance.Light : ExteriorLight,
                        AppearStart = instance.AppearStart,
                        Highlight = ReferenceEquals(instance, highlighted)
                            ? DynamicHighlightBoost : 0f,
                    });
                }
            }

            cullTicks += Stopwatch.GetTimestamp() - cullStarted;

            if (_visibleInstances.Count == 0) continue;

            // Bone-animated doodads (lava bubbles): re-skin the shared VBO at
            // the world clock before any of this model's batches draw. Null for
            // the static majority; visible-only, so off-screen bubbles cost
            // nothing.
            if (model.BoneAnimSource is not null) UpdateAnimatedVertices(model);

            _drawnThisFrame.Add(model);
            if (!_drawnPreviousFrame.Contains(model)) firstTouch++;

            long uploadStarted = Stopwatch.GetTimestamp();
            _gl.BindVertexArray(model.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
            var instanceData = CollectionsMarshal.AsSpan(_visibleInstances);
            fixed (InstanceData* p = instanceData)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(instanceData.Length * sizeof(InstanceData)), p, BufferUsageARB.StreamDraw);
            }
            uploadTicks += Stopwatch.GetTimestamp() - uploadStarted;
            uploadedModels++;

            long drawStarted = Stopwatch.GetTimestamp();

            // Does anything we are about to draw actually need blending? Only
            // the survivors are scanned, and only until the first fader.
            bool wantBlend = false;
            if (AppearFade)
            {
                float fadeSecs = MathF.Max(AppearFadeSeconds, 0.0001f);
                foreach (var visible in _visibleInstances)
                {
                    if (visible.AppearStart > 0f && NowSeconds - visible.AppearStart < fadeSecs)
                    {
                        wantBlend = true;
                        break;
                    }
                }
            }

            if (wantBlend && !blendOn)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                blendOn = true;
            }
            else if (!wantBlend && blendOn)
            {
                _gl.Disable(EnableCap.Blend);
                blendOn = false;
            }

            _shader.Set("uPreserveAlpha", wantBlend ? 1 : 0);

            uint instanceCount = (uint)_visibleInstances.Count;

            // Blended batches (M2 blend modes 2-6) draw after ALL opaque
            // geometry. The instance data uploaded just above stays valid in
            // model.InstanceVbo until next frame, so the deferred pass only
            // needs the model and the count.
            if (model.HasBlendedBatches)
                _deferredBlendedInstanced.Add((model, instanceCount));

            foreach (var batch in model.Batches)
            {
                if (batch.Blended) continue;

                if (batch.TwoSided && cullingOn)
                {
                    _gl.Disable(EnableCap.CullFace);
                    cullingOn = false;
                }
                else if (!batch.TwoSided && !cullingOn)
                {
                    _gl.Enable(EnableCap.CullFace);
                    cullingOn = true;
                }

                if (batch.NoDepthWrite == depthWriteOn)
                {
                    depthWriteOn = !batch.NoDepthWrite;
                    _gl.DepthMask(depthWriteOn);
                }

                if (batch.Texture is not null)
                {
                    batch.Texture.Bind(0);
                    _shader.Set("uHasTexture", 1);
                }
                else
                {
                    _shader.Set("uHasTexture", 0);
                }
                _shader.Set("uAlphaCutoff", OpaqueCutoffFor(batch));

                _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                _shader.Set("uFogMode", batch.FogMode);
                _shader.Set("uUvOffset", UvOffsetFor(model, batch));

                _gl.DrawElementsInstanced(PrimitiveType.Triangles, batch.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)),
                    instanceCount);
                DrawCallsLastFrame++;
                TrianglesLastFrame += (long)(batch.IndexCount / 3) * instanceCount;
            }

            drawTicks += Stopwatch.GetTimestamp() - drawStarted;
            DrawnLastFrame += _visibleInstances.Count;
        }

        if (blendOn) _gl.Disable(EnableCap.Blend);
        if (!depthWriteOn) _gl.DepthMask(true);

        long deferredStarted = Stopwatch.GetTimestamp();
        DrawBlendedDeferredInstanced(ref cullingOn);
        drawTicks += Stopwatch.GetTimestamp() - deferredStarted;

        double perTick = 1000.0 / Stopwatch.Frequency;
        CullMilliseconds = cullTicks * perTick;
        InstanceUploadMilliseconds = uploadTicks * perTick;
        DrawMilliseconds = drawTicks * perTick;
        UploadedModelsLastFrame = uploadedModels;
        FirstTouchModelsLastFrame = firstTouch;
        CullModelsLastFrame = cullModels;
        CullInstancesLastFrame = cullInstances;

        // Swap, do not copy: this runs every frame and copying a set of live
        // models per frame is exactly the kind of quiet allocation that made
        // 40 MB/frame possible in the first place.
        (_drawnPreviousFrame, _drawnThisFrame) = (_drawnThisFrame, _drawnPreviousFrame);
    }

    public void Dispose()
    {
        foreach (var job in _preloadJobs)
        {
            try { job.Worker.GetAwaiter().GetResult(); }
            catch { /* Shutdown must continue even if a background decode failed. */ }
            try
            {
                if (job.Upload is { } upload)
                {
                    var orphan = upload.GetAwaiter().GetResult();
                    foreach (var texture in orphan.Textures.Values) texture?.Dispose();
                    if (orphan.Vbo != 0) _gl.DeleteBuffer(orphan.Vbo);
                    if (orphan.Ebo != 0) _gl.DeleteBuffer(orphan.Ebo);
                }
            }
            catch { /* The upload worker may already be unwinding. */ }
        }
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _byModel.Clear();
        _cullBounds.Clear();
        _preloadQueue.Clear();
        _preloadQueued.Clear();
        _preloadJobs.Clear();
        _shader?.Dispose();
    }
}
