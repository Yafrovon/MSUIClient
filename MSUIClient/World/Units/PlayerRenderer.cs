using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.Engine.UI;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

// Draws OTHER players from the networked entity stream — both server-owned bots and real players
// from other clients. The local player is drawn by CharacterRenderer (`_character`) and is excluded
// here by guid.
//
// SCALABLE, SHARED-RESOURCE design (mirrors CreatureRenderer, NOT a per-player CharacterRenderer):
//   * MODEL cache keyed by (race,gender): one skeleton VAO/VBO/EBO + M2Animator per race+gender
//     (Character\<Race>\<Gender>\<Race><Gender>.m2). ~16 combos ever.
//   * APPEARANCE cache keyed by an appearance+equipment SIGNATURE: the composited armor atlas,
//     per-batch textures, visible-geoset set and attachment mount set. Identical bots collapse to
//     one entry. Atlas/hair/cape textures are shared through _texCache keyed by a descriptor that
//     encodes the signature (identical looks reuse the same GPU texture).
//   * PER-GUID instance state (tiny): anim clock, death clock, alive/dead, last-built signature.
//
// The one thing the humanoid-NPC path lacks and players need — runtime ARMOR ATLAS compositing
// (sleeves/chest/legs/gloves/boots painted onto the body skin) — is done via
// CharacterEquipment.Composite. Equipment resolves from the 19 visible-item ENTRIES through the
// async ItemTemplateCache, so the signature converges as SMSG_ITEM_QUERY_SINGLE replies arrive
// (gear "pops in", then rebuilds stop).

public sealed class PlayerRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly ClientConfig _config;
    private readonly AssetWorkerPool? _workers;
    private readonly GpuUploadWorker? _uploads;
    private readonly string _attachmentShaderDir;

    private Shader? _shader;
    private ItemDisplayTable? _itemDisplay;
    private CharSectionsTable? _charSections;
    private CharacterGeosets? _geosets;
    private AttachedItemRenderer? _attachedItems;

    public bool Ok { get; private set; }
    public bool Enabled { get; set; } = true;
    public bool Animate { get; set; } = true;
    public bool GeosetFilter { get; set; } = true;

    public float HeadingOffsetDegrees { get; set; } = 90f;
    public float ScaleMultiplier { get; set; } = 1f;

    /// <summary>Beyond this a player is streamed/drawn in bind pose (skinning you couldn't see).</summary>
    public float AnimateDistance { get; set; } = 130f;

    /// <summary>Beyond this a player is neither resident nor drawn.</summary>
    public float DrawDistance { get; set; } = 200f;

    /// <summary>Animate at most this many (nearest-first); the rest hold bind pose. Bounds crowd cost.</summary>
    public int AnimatedCap { get; set; } = 40;

    public int DrawnLastFrame { get; private set; }
    public int AnimatedLastFrame { get; private set; }
    public double LoadMillisecondsThisFrame { get; private set; }
    public int ModelCacheEntries => _modelCache.Count;
    public int AppearanceCacheEntries => _appearanceCache.Count;
    public int PendingAppearances => _appearanceJobs.Count;
    public ulong HoveredGuid { get; set; }
    public ulong SelectedGuid { get; set; }
    public Action<string, int, M2Animator.Resolution>? AnimationResolved { get; set; }

    private const int BaseAnimationTrack = 0;
    private const int ActionAnimationTrack = 1;
    private const int SpellHoldAnimationTrack = 2;

    private const int FloatsPerVertex = 16;   // pos3 + norm3 + uv2 + weight4 + index4
    private const double FinalizeBudgetMs = 2.0;
    private const float DefaultWalkSpeed = 2.5f;
    private const float MovingEpsilon = 0.1f;

    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    private static readonly Vector3 FogColor = new(0.56f, 0.71f, 0.85f);
    private const float FogStart = 350f, FogEnd = 900f;

    // Y-up model space -> WoW axes, byte-identical to CharacterRenderer/CreatureRenderer.
    private static readonly Matrix4x4 Basis = new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly Matrix4x4[] _bindSkin =
        Enumerable.Repeat(Matrix4x4.Identity, M2Animator.MaxBones).ToArray();
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];

    // ── per-guid instance state ──────────────────────────────────────────────
    private readonly Dictionary<ulong, float> _animTime = new();
    private readonly Dictionary<ulong, CombatAction> _combatActions = new();
    private readonly Dictionary<ulong, int> _spellHolds = new();
    private readonly HashSet<ulong> _knownAlive = new();
    private readonly HashSet<ulong> _observedDead = new();
    private readonly Dictionary<ulong, float> _deathTime = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _stale = new();
    private readonly List<WorldEntity> _orderedUnits = new();
    private Vector3 _sortCameraPosition;

    private float _globalTime;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;

    private readonly record struct CombatAction(int AnimationId, float StartedAt, float ExpiresAt,
        bool AuthoredExact = false);

    // ── model cache ──────────────────────────────────────────────────────────
    private sealed class LoadedModel
    {
        public uint Vao, Vbo, Ebo;
        public readonly List<DrawBatch> Batches = new();
        public M2Animator? Animator;
        public int BoneCount;
        public M2Model Source = null!;
    }
    private struct DrawBatch
    {
        public int Start, Count;
        public int Blend;
        public int GeosetId;
        public uint TextureType;
        public string Embedded;   // embedded texture filename, if any
    }

    private sealed class Appearance
    {
        public readonly List<Texture?> Textures = new();   // parallel to model.Batches
        public HashSet<int>? VisibleGeosets;
        public AttachedItemRenderer.MountSet? Mounts;
        public float LastSeenAt;
        public string AtlasPath = "";   // the unique composited-atlas texture in _texCache; disposed on eviction
    }

    private readonly Dictionary<string, LoadedModel?> _modelCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Appearance?> _appearanceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _texCache =
        new(StringComparer.OrdinalIgnoreCase);

    public PlayerRenderer(GL gl, MpqMount mpq, ClientConfig config,
        AssetWorkerPool? workers = null, GpuUploadWorker? uploads = null)
    {
        _gl = gl;
        _mpq = mpq;
        _config = config;
        _workers = workers;
        _uploads = uploads;
        _attachmentShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(_attachmentShaderDir, "attached.vert")))
            _attachmentShaderDir = Path.Combine(config.RepoRoot, "MSUIClient", "Shaders");

        try
        {
            var idBytes = mpq.ReadFile(ItemDisplayTable.MpqPath);
            _itemDisplay = idBytes is null ? null : ItemDisplayTable.Parse(idBytes);
            var sectionBytes = mpq.ReadFile(CharSectionsTable.MpqPath);
            _charSections = sectionBytes is null ? null : CharSectionsTable.Parse(sectionBytes);
            var hairBytes = mpq.ReadFile(CharHairGeosetsTable.MpqPath);
            var facialBytes = mpq.ReadFile(CharacterFacialHairTable.MpqPath);
            var helmBytes = mpq.ReadFile(HelmetGeosetVisTable.MpqPath);
            _geosets = new CharacterGeosets(
                hairBytes is null ? null : CharHairGeosetsTable.Parse(hairBytes),
                facialBytes is null ? null : CharacterFacialHairTable.Parse(facialBytes),
                helmBytes is null ? null : HelmetGeosetVisTable.Parse(helmBytes));

            _shader = Shader.FromSource(_gl, "player", VertSrc, FragSrc);
            _attachedItems = new AttachedItemRenderer(_gl, _config);
            _attachedItems.LoadShaders(_attachmentShaderDir);

            Ok = _itemDisplay is not null && _charSections is not null;
            Console.WriteLine($"[player] renderer ready (itemDisplay={_itemDisplay?.Count ?? 0}, " +
                              $"charSections={(_charSections is not null ? "ok" : "MISSING")}, " +
                              $"geosets={(_geosets.Ok ? "on" : "no-dbc")})");
            if (!Ok)
                Console.WriteLine("[player] ItemDisplayInfo/CharSections missing — player rendering off");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[player] init failed: {e.Message}");
            Ok = false;
        }
    }

    public static float UnitRenderScale(float objectFieldScale, float tuningMultiplier = 1f)
        => MathF.Max(0.01f, objectFieldScale) * tuningMultiplier;

    // ── the frame ──────────────────────────────────────────────────────────────

    public void Render(Camera camera, EntityStore entities, ulong localGuid,
        NetworkClient? net, ItemTemplateCache? items)
    {
        DrawnLastFrame = 0;
        AnimatedLastFrame = 0;
        LoadMillisecondsThisFrame = 0;
        if (!Ok || !Enabled || _shader is null) return;

        double nowS = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(nowS - _lastSeconds, 0.0, 0.1);
        _lastSeconds = nowS;
        _globalTime += dt;

        Vector3 camPos = camera.Position;
        Matrix4x4 viewProj = camera.RelativeViewProjection;
        float heading0 = HeadingOffsetDegrees * MathF.PI / 180f;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _shader.Use();
        _shader.Set("uViewProj", viewProj);
        _shader.Set("uSunDir", SunDir);
        _shader.Set("uAmbientColor", new Vector3(.45f));
        _shader.Set("uDiffuseColor", new Vector3(.55f));
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uTex", 0);
        _seen.Clear();

        _orderedUnits.Clear();
        foreach (WorldEntity entity in entities.Units)
            if (entity.IsPlayer && entity.Guid != localGuid) _orderedUnits.Add(entity);
        _sortCameraPosition = camPos;
        _orderedUnits.Sort(CompareUnitDistance);

        int animatedThisFrame = 0;

        foreach (WorldEntity e in _orderedUnits)
        {
            float distanceSq = Vector3.DistanceSquared(e.Position, camPos);
            if (distanceSq > DrawDistance * DrawDistance) continue;

            (byte race, _, byte gender, _) = e.Fields.Bytes0;
            if (race == 0) continue;   // descriptor not populated yet

            Vector3 relative = e.Position - camPos;
            const float radius = 3f;
            if (!Camera.BoxInFrustum(viewProj,
                    relative - new Vector3(radius, radius, radius),
                    relative + new Vector3(radius, radius, radius * 1.5f)))
                continue;

            if (!TryAcquireModel(race, gender, out LoadedModel? model))
                continue;
            if (model is null) continue;

            // Cheap per-frame signature (issues item queries, no kit build / no logging).
            string sig = AppearanceSignature(e, race, gender, net, items);
            if (!TryAcquireAppearance(model, sig, e, race, gender, net, items,
                    out Appearance? appearance))
                continue;
            if (appearance is null) continue;

            appearance.LastSeenAt = _globalTime;
            _seen.Add(e.Guid);
            TrackLifeState(e);

            float scale = UnitRenderScale(e.Scale, ScaleMultiplier);
            float heading = e.Orientation + heading0;
            Matrix4x4 worldModel = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationY(heading)
                * Basis
                * Matrix4x4.CreateTranslation(e.Position);
            Matrix4x4 m = worldModel;
            m.M41 -= camPos.X; m.M42 -= camPos.Y; m.M43 -= camPos.Z;
            _shader.Set("uModel", m);
            _shader.Set("uHighlight", e.Guid == HoveredGuid || e.Guid == SelectedGuid ? 64f / 255f : 0f);

            bool wantAnimate = Animate && model.Animator is not null && model.BoneCount > 0 &&
                (e.IsDead || (distanceSq <= AnimateDistance * AnimateDistance &&
                              animatedThisFrame < AnimatedCap));

            int boneCount = 0;
            if (wantAnimate)
            {
                boneCount = AnimateUnit(e, model, dt);
                if (boneCount > 0)
                {
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                    AnimatedLastFrame++;
                    if (!e.IsDead) animatedThisFrame++;
                }
            }
            _shader.Set("uBoneCount", boneCount);

            bool filter = GeosetFilter && appearance.VisibleGeosets is not null;
            _gl.BindVertexArray(model.Vao);
            for (int i = 0; i < model.Batches.Count; i++)
            {
                DrawBatch b = model.Batches[i];
                if (filter && !appearance.VisibleGeosets!.Contains(b.GeosetId)) continue;

                bool additive = b.Blend is 3 or 4;
                bool alphaKey = b.Blend == 1;
                if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
                else if (b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
                else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
                _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.02f);
                (i < appearance.Textures.Count ? appearance.Textures[i] : null)?.Bind(0);
                DrawElements(b.Start, b.Count);
            }
            DrawnLastFrame++;

            // Attachments (helm/shoulders/weapons/shield) use their own shader.
            if (appearance.Mounts is { Count: > 0 } mounts && _attachedItems is not null)
            {
                _attachedItems.RaceGenderCode = RaceGenderCode(race, gender);
                _attachedItems.Render(camera, m, model.Source,
                    boneCount > 0 ? _skin : _bindSkin, mounts, e.Fields.SheathState,
                    modelTime: _globalTime);
                _gl.Enable(EnableCap.Blend);
                _gl.DepthMask(true);
                _shader.Use();
            }
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);

        PruneState();
    }

    private int CompareUnitDistance(WorldEntity a, WorldEntity b) =>
        Vector3.DistanceSquared(a.Position, _sortCameraPosition)
            .CompareTo(Vector3.DistanceSquared(b.Position, _sortCameraPosition));

    // ── animation ────────────────────────────────────────────────────────────

    private int AnimateUnit(WorldEntity e, LoadedModel model, float dt)
    {
        string unit = $"player:{e.Guid:X16}";
        if (!_animTime.TryGetValue(e.Guid, out float at)) at = InitialPhase(e.Guid);
        M2Animator.Clip? clip;

        if (e.IsDead)
        {
            clip = model.Animator!.Resolve(unit, ActionAnimationTrack, 1, true, 6, 0);
            float deathAt = _deathTime.GetValueOrDefault(e.Guid, float.PositiveInfinity);
            at = float.IsPositiveInfinity(deathAt)
                ? clip?.DurationSeconds ?? 0f
                : MathF.Min(deathAt + dt, clip?.DurationSeconds ?? deathAt + dt);
            _deathTime[e.Guid] = at;
        }
        else if (_combatActions.TryGetValue(e.Guid, out CombatAction action) &&
                 ResolveCombatClip(model.Animator!, unit, action) is { } actionClip)
        {
            clip = actionClip;
            float actionTime = _globalTime - action.StartedAt;
            if (actionTime >= actionClip.DurationSeconds) _combatActions.Remove(e.Guid);
            at = actionTime;
        }
        else if (_spellHolds.TryGetValue(e.Guid, out int held) &&
                 model.Animator!.Resolve(unit, SpellHoldAnimationTrack, held, true) is { } holdClip)
        {
            clip = holdClip;
            at += dt;
        }
        else
        {
            _combatActions.Remove(e.Guid);
            clip = SelectClip(e, model.Animator!, unit, out float rate);
            at += dt * rate;
        }

        if (float.IsNaN(at) || float.IsInfinity(at)) at = 0f;
        _animTime[e.Guid] = at;

        if (clip is null) return 0;
        int boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
        model.Animator!.Evaluate(clip, at, _globalTime, _skin);
        return boneCount;
    }

    private const float FastRunSpeed = 11.0f;   // benilla select.rs FAST_RUN_SPEED -> Sprint (143)

    // Gait selection mirrors benilla creature_anim/select.rs gait_candidates: the interpolation spline
    // gives SPEED, the last MSG_MOVE_* flags give DIRECTION (fwd/back/strafe/turn/swim). AnimationData
    // ids: 0 Stand, 4 Walk, 5 Run, 143 Sprint, 13 WalkBackwards, 11/12 Shuffle L/R, 41-45 swim set,
    // 25-28 engaged ready.
    private static M2Animator.Clip? SelectClip(
        WorldEntity e, M2Animator animator, string unit, out float rate)
    {
        rate = 1f;
        uint flags = e.MoveFlags;
        float speed = e.Spline?.AverageSpeed ?? 0f;
        bool moving = e.Spline is not null && speed > MovingEpsilon;
        float walk = e.Speeds is { Length: > 0 } sp && sp[0] > 0f ? sp[0] : DefaultWalkSpeed;

        float RateFor(M2Animator.Clip? clip) =>
            clip is not null && clip.MoveSpeed > 0.01f ? Math.Clamp(speed / clip.MoveSpeed, 0.25f, 3f) : 1f;

        if ((flags & (uint)MovementFlags.Swimming) != 0)
        {
            if (!moving) return animator.Resolve(unit, BaseAnimationTrack, 41, true, 0);
            int swimId = (flags & (uint)MovementFlags.Backward) != 0 ? 45
                : (flags & (uint)MovementFlags.StrafeLeft) != 0 ? 43
                : (flags & (uint)MovementFlags.StrafeRight) != 0 ? 44
                : 42;
            M2Animator.Clip? swim = animator.Resolve(unit, BaseAnimationTrack, swimId, true, 42, 41, 0);
            rate = RateFor(swim);
            return swim;
        }

        // Strafe-only translation (holding a mouse button and A/D, or Q/E) does not reliably
        // clear MovingEpsilon on the interpolated spline the way forward movement does, so
        // "moving" alone used to miss it and fall through to the Stand/engaged-ready branch
        // below - visible as the mount rearing up while actually sliding sideways. The flags
        // are set correctly regardless (LocalMovementSender), so trust those here too.
        bool strafing = (flags &
            (uint)(MovementFlags.StrafeLeft | MovementFlags.StrafeRight)) != 0;
        if (moving || strafing)
        {
            if ((flags & (uint)MovementFlags.Backward) != 0)
            {
                M2Animator.Clip? back = animator.Resolve(unit, BaseAnimationTrack, 13, true, 4, 0);
                rate = RateFor(back);
                return back;
            }
            M2Animator.Clip? clip = speed >= FastRunSpeed
                ? animator.Resolve(unit, BaseAnimationTrack, 143, true, 5, 4, 0)
                : speed > 2f * walk
                    ? animator.Resolve(unit, BaseAnimationTrack, 5, true, 4, 0)
                    : animator.Resolve(unit, BaseAnimationTrack, 4, true, 5, 0);
            rate = RateFor(clip);
            return clip;
        }

        int pose = StandStateUiLaw.LoopAnimation(e.Fields.UnitStandState);
        if (pose != 0)
            return animator.Resolve(unit, BaseAnimationTrack, pose, true, 0);

        // Standing: turn-in-place shuffle (turn key, no translation), else engaged ready, else Stand.
        if ((flags & (uint)MovementFlags.TurnLeft) != 0)
            return animator.Resolve(unit, BaseAnimationTrack, 11, true, 0);
        if ((flags & (uint)MovementFlags.TurnRight) != 0)
            return animator.Resolve(unit, BaseAnimationTrack, 12, true, 0);
        return e.Engaged
            ? animator.Resolve(unit, BaseAnimationTrack, 25, true, 26, 27, 28, 0)
            : animator.Resolve(unit, BaseAnimationTrack, 0, true);
    }

    private static M2Animator.Clip? ResolveCombatClip(
        M2Animator animator, string unit, in CombatAction action)
    {
        if (action.AuthoredExact)
            return animator.Resolve(unit, ActionAnimationTrack, action.AnimationId, true);
        return action.AnimationId switch
        {
            16 => animator.Resolve(unit, ActionAnimationTrack, 16, true, 17, 18, 19, 85),
            87 => animator.Resolve(unit, ActionAnimationTrack, 87, true, 88, 117, 16),
            20 => animator.Resolve(unit, ActionAnimationTrack, 20, true, 21, 22, 23, 9, 0),
            7 => animator.Resolve(unit, ActionAnimationTrack, 7, true, 0),
            _ => animator.Resolve(unit, ActionAnimationTrack, action.AnimationId, true, 9, 0),
        };
    }

    public void TriggerCombatSwing(ulong guid, bool offHand)
        => _combatActions[guid] = new CombatAction(offHand ? 87 : 16, _globalTime, _globalTime + 3f);

    public void TriggerCombatReaction(ulong guid, uint victimState, bool landedHit)
    {
        int id = victimState switch
        {
            2 or 8 => 30, 3 => 20, 5 => 24, _ when landedHit => 9, _ => -1,
        };
        if (id >= 0) _combatActions[guid] = new CombatAction(id, _globalTime, _globalTime + 3f);
    }

    public void BeginSpellVisual(ulong guid, ushort? animationId)
    {
        if (animationId is { } id && id != 0) _spellHolds[guid] = id;
        else _spellHolds.Remove(guid);
        _animTime[guid] = 0f;
    }

    public void ReleaseSpellVisual(ulong guid, ushort? animationId)
    {
        _spellHolds.Remove(guid);
        if (animationId is { } id && id != 0)
            _combatActions[guid] = new CombatAction(id, _globalTime, _globalTime + 4f, AuthoredExact: true);
    }

    public void CancelSpellVisual(ulong guid) => _spellHolds.Remove(guid);

    private void TrackLifeState(WorldEntity entity)
    {
        if (entity.IsDead)
        {
            bool witnessedAlive = _knownAlive.Remove(entity.Guid);
            if (_observedDead.Add(entity.Guid))
                _deathTime[entity.Guid] = witnessedAlive ? 0f : float.PositiveInfinity;
            _combatActions.Remove(entity.Guid);
            return;
        }
        bool resurrected = _observedDead.Remove(entity.Guid);
        _deathTime.Remove(entity.Guid);
        _knownAlive.Add(entity.Guid);
        if (resurrected)
            _combatActions[entity.Guid] = new CombatAction(7, _globalTime, _globalTime + 3f);
    }

    private static float InitialPhase(ulong guid) => (guid % 977) / 977f * 5f;

    private void PruneState()
    {
        _stale.Clear();
        foreach (ulong k in _animTime.Keys) if (!_seen.Contains(k)) _stale.Add(k);
        foreach (ulong k in _stale)
        {
            _animTime.Remove(k);
            _combatActions.Remove(k);
            _spellHolds.Remove(k);
            _knownAlive.Remove(k);
            _observedDead.Remove(k);
            _deathTime.Remove(k);
        }
        EvictOldAppearances();
    }

    /// <summary>Model cache stays forever (~16 combos). Appearance atlases are per-look and can pile up
    /// over a long session, so LRU-evict the least-recently-seen live looks (and dispose their unique
    /// composited atlas) once past the cap. Runs on the render thread, so GL deletes are safe.</summary>
    private const int MaxAppearances = 96;
    private readonly List<KeyValuePair<string, Appearance?>> _evictScratch = new();
    private void EvictOldAppearances()
    {
        int live = 0;
        foreach (Appearance? v in _appearanceCache.Values) if (v is not null) live++;
        if (live <= MaxAppearances) return;

        _evictScratch.Clear();
        foreach (var kv in _appearanceCache)
            if (kv.Value is not null && _globalTime - kv.Value.LastSeenAt > 1f)
                _evictScratch.Add(kv);
        _evictScratch.Sort((a, b) => a.Value!.LastSeenAt.CompareTo(b.Value!.LastSeenAt));

        int toEvict = live - MaxAppearances;
        foreach (var kv in _evictScratch)
        {
            if (toEvict <= 0) break;
            Appearance app = kv.Value!;
            if (app.AtlasPath.Length > 0 && _texCache.Remove(app.AtlasPath, out Texture? atlas))
                atlas?.Dispose();
            _appearanceCache.Remove(kv.Key);
            toEvict--;
        }
    }

    // ── model residency (async worker -> upload -> finalize) ─────────────────────

    private sealed class PreparedModel
    {
        public M2Model? Source;
        public M2Animator? Animator;
        public float[] Vertices = [];
        public ushort[] Indices = [];
    }
    private readonly record struct UploadedBuffers(uint Vbo, uint Ebo);
    private sealed class ModelLoadJob
    {
        public required string Key;
        public required byte Race;
        public required byte Gender;
        public required Task<PreparedModel?> Worker;
        public PreparedModel? Ready;
        public Task<UploadedBuffers>? Upload;
    }
    private readonly Dictionary<string, ModelLoadJob> _modelJobs =
        new(StringComparer.OrdinalIgnoreCase);

    private bool TryAcquireModel(byte race, byte gender, out LoadedModel? model)
    {
        string key = $"{race}:{gender}";
        if (_modelCache.TryGetValue(key, out model)) return true;

        // Synchronous fallback (no worker pools — portrait/batch contexts).
        if (_workers is null || _uploads is null)
        {
            model = LoadModelSync(race, gender);
            _modelCache[key] = model;
            return true;
        }

        if (!_modelJobs.TryGetValue(key, out ModelLoadJob? job))
        {
            byte r = race, g = gender;
            job = new ModelLoadJob
            {
                Key = key, Race = r, Gender = g,
                Worker = _workers.Run(() => PrepareModel(r, g)),
            };
            _modelJobs[key] = job;
            model = null;
            return false;
        }
        if (!job.Worker.IsCompleted) { model = null; return false; }

        if (job.Ready is null)
        {
            try { job.Ready = job.Worker.GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[player-prepare] {key} failed: {ex.Message}"); }
            if (job.Ready is null)
            {
                _modelCache[key] = null;
                _modelJobs.Remove(key);
                model = null;
                return true;
            }
        }
        if (job.Upload is null)
        {
            PreparedModel ready = job.Ready;
            job.Upload = _uploads.Enqueue(key, uploadGl => UploadPreparedModel(uploadGl, ready));
            model = null;
            return false;
        }
        if (!job.Upload.IsCompleted || LoadMillisecondsThisFrame >= FinalizeBudgetMs)
        {
            model = null;
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            UploadedBuffers uploaded = job.Upload.GetAwaiter().GetResult();
            model = FinalizePreparedModel(job.Ready, uploaded);
            _modelCache[key] = model;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[player-upload] {key} failed: {ex.Message}");
            model = null;
            _modelCache[key] = null;
        }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _modelJobs.Remove(key);
        }
        return true;
    }

    private LoadedModel? LoadModelSync(byte race, byte gender)
    {
        PreparedModel? prepared = PrepareModel(race, gender);
        if (prepared is null) return null;
        UploadedBuffers uploaded = UploadPreparedModel(_gl, prepared);
        return FinalizePreparedModel(prepared, uploaded);
    }

    private PreparedModel? PrepareModel(byte race, byte gender)
    {
        string path = ModelPath(race, gender);
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) { Console.WriteLine($"[player] model '{path}' not in MPQ"); return null; }
        M2Model? m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid) return null;

        var prepared = new PreparedModel
        {
            Source = m2,
            Animator = M2Animator.Build(m2, Array.Empty<int>()),
            Vertices = new float[m2.Vertices.Count * FloatsPerVertex],
            Indices = m2.Indices.ToArray(),
        };
        for (int i = 0; i < m2.Vertices.Count; i++)
        {
            M2Vertex v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            prepared.Vertices[o] = v.PosX; prepared.Vertices[o + 1] = v.PosY; prepared.Vertices[o + 2] = v.PosZ;
            prepared.Vertices[o + 3] = v.NormX; prepared.Vertices[o + 4] = v.NormY; prepared.Vertices[o + 5] = v.NormZ;
            prepared.Vertices[o + 6] = v.TexU; prepared.Vertices[o + 7] = v.TexV;
            float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
            if (total <= 0f) { prepared.Vertices[o + 8] = 1f; }
            else
            {
                prepared.Vertices[o + 8] = v.BoneWeight0 / total;
                prepared.Vertices[o + 9] = v.BoneWeight1 / total;
                prepared.Vertices[o + 10] = v.BoneWeight2 / total;
                prepared.Vertices[o + 11] = v.BoneWeight3 / total;
                prepared.Vertices[o + 12] = ClampBone(v.BoneIndex0);
                prepared.Vertices[o + 13] = ClampBone(v.BoneIndex1);
                prepared.Vertices[o + 14] = ClampBone(v.BoneIndex2);
                prepared.Vertices[o + 15] = ClampBone(v.BoneIndex3);
            }
        }
        return prepared;
    }

    private static unsafe UploadedBuffers UploadPreparedModel(GL gl, PreparedModel prepared)
    {
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = prepared.Vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(prepared.Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        uint ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* idx = prepared.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(prepared.Indices.Length * sizeof(ushort)), idx, BufferUsageARB.StaticDraw);
        return new UploadedBuffers(vbo, ebo);
    }

    private unsafe LoadedModel FinalizePreparedModel(PreparedModel prepared, UploadedBuffers uploaded)
    {
        M2Model m2 = prepared.Source!;
        var model = new LoadedModel
        {
            Source = m2,
            Animator = prepared.Animator,
            BoneCount = prepared.Animator is { BoneCount: <= M2Animator.MaxBones }
                ? prepared.Animator.BoneCount : 0,
            Vbo = uploaded.Vbo,
            Ebo = uploaded.Ebo,
        };
        if (model.Animator is not null)
            model.Animator.ResolutionSink = (unit, track, resolution) =>
                AnimationResolved?.Invoke(unit, track, resolution);

        model.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(model.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);
        int stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(12 * sizeof(float)));
        _gl.BindVertexArray(0);

        foreach (M2Batch batch in m2.Batches)
        {
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            M2Submesh sm = m2.Submeshes[batch.SubmeshIndex];
            int blend = batch.MaterialIndex < m2.RenderFlags.Count
                ? m2.RenderFlags[batch.MaterialIndex].BlendingMode : 0;
            uint type = 0;
            string embedded = "";
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                int ti = m2.TextureLookup[batch.TextureIndex];
                if (ti >= 0 && ti < m2.Textures.Count)
                {
                    type = m2.Textures[ti].Type;
                    embedded = m2.Textures[ti].Filename ?? "";
                }
            }
            model.Batches.Add(new DrawBatch
            {
                Start = sm.IndexStart, Count = sm.IndexCount,
                Blend = blend, GeosetId = sm.Id, TextureType = type, Embedded = embedded,
            });
        }
        return model;
    }

    // ── appearance residency ─────────────────────────────────────────────────

    private sealed class PreparedTexture
    {
        public string Path = "";
        public byte[]? Bgra;
        public int Width, Height;
    }
    private sealed class PreparedAppearance
    {
        public HashSet<int>? VisibleGeosets;
        public List<PreparedTexture?> BatchTextures = new();
        public CharacterEquipment Equipment = null!;
        public byte Race, Gender;
        public string AtlasPath = "";
    }
    private sealed class AppearanceLoadJob
    {
        public required Task<PreparedAppearance?> Worker;
        public PreparedAppearance? Ready;
        public Task<Dictionary<string, Texture?>>? Upload;
    }
    private readonly Dictionary<string, AppearanceLoadJob> _appearanceJobs =
        new(StringComparer.OrdinalIgnoreCase);

    private bool TryAcquireAppearance(LoadedModel model, string sig, WorldEntity entity,
        byte race, byte gender, NetworkClient? net, ItemTemplateCache? items,
        out Appearance? appearance)
    {
        if (_appearanceCache.TryGetValue(sig, out appearance)) return true;

        if (_workers is null || _uploads is null)
        {
            CharacterEquipment kitSync = BuildEquipment(entity, net, items);
            kitSync.Signature = sig;
            PreparedAppearance? prep = PrepareAppearance(model.Source, kitSync, race, gender);
            appearance = prep is null ? null : FinalizeAppearanceSync(prep);
            _appearanceCache[sig] = appearance;
            return true;
        }

        if (!_appearanceJobs.TryGetValue(sig, out AppearanceLoadJob? job))
        {
            // Build + resolve the kit ONCE per unique appearance (never per frame).
            CharacterEquipment kit = BuildEquipment(entity, net, items);
            kit.Signature = sig;
            M2Model source = model.Source;
            byte r = race, g = gender;
            job = new AppearanceLoadJob
            {
                Worker = _workers.Run<PreparedAppearance?>(() => PrepareAppearance(source, kit, r, g)),
            };
            _appearanceJobs[sig] = job;
            appearance = null;
            return false;
        }
        if (!job.Worker.IsCompleted) { appearance = null; return false; }

        if (job.Ready is null)
        {
            try { job.Ready = job.Worker.GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[player-appearance] {sig} failed: {ex.Message}"); }
            if (job.Ready is null)
            {
                _appearanceCache[sig] = null;
                _appearanceJobs.Remove(sig);
                appearance = null;
                return true;
            }
        }
        if (job.Upload is null)
        {
            PreparedAppearance ready = job.Ready;
            PreparedTexture[] pending = ready.BatchTextures
                .Where(t => t is not null && t.Bgra is not null && !_texCache.ContainsKey(t.Path))
                .Select(t => t!)
                .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            job.Upload = _uploads.Enqueue("player-appearance", uploadGl =>
            {
                var textures = new Dictionary<string, Texture?>(StringComparer.OrdinalIgnoreCase);
                foreach (PreparedTexture t in pending)
                    textures[t.Path] = t.Bgra is null ? null
                        : Texture.From2D(uploadGl, t.Bgra, t.Width, t.Height,
                            mipmaps: true, repeat: true, ownerGl: _gl);
                return textures;
            });
            appearance = null;
            return false;
        }
        if (!job.Upload.IsCompleted || LoadMillisecondsThisFrame >= FinalizeBudgetMs)
        {
            appearance = null;
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var pair in job.Upload.GetAwaiter().GetResult())
                _texCache.TryAdd(pair.Key, pair.Value);
            appearance = FinalizeAppearance(job.Ready);
            _appearanceCache[sig] = appearance;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[player-appearance-upload] {sig} failed: {ex.Message}");
            appearance = null;
            _appearanceCache[sig] = null;
        }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _appearanceJobs.Remove(sig);
        }
        return true;
    }

    private Appearance FinalizeAppearance(PreparedAppearance prep)
    {
        var appearance = new Appearance { VisibleGeosets = prep.VisibleGeosets, AtlasPath = prep.AtlasPath };
        foreach (PreparedTexture? t in prep.BatchTextures)
            appearance.Textures.Add(t is not null && _texCache.TryGetValue(t.Path, out Texture? tex) ? tex : null);
        if (_attachedItems is not null)
        {
            _attachedItems.RaceGenderCode = RaceGenderCode(prep.Race, prep.Gender);
            appearance.Mounts = _attachedItems.BuildMountSet(prep.Equipment);
        }
        return appearance;
    }

    private Appearance FinalizeAppearanceSync(PreparedAppearance prep)
    {
        var appearance = new Appearance { VisibleGeosets = prep.VisibleGeosets, AtlasPath = prep.AtlasPath };
        foreach (PreparedTexture? t in prep.BatchTextures)
        {
            if (t is null || t.Bgra is null) { appearance.Textures.Add(null); continue; }
            if (!_texCache.TryGetValue(t.Path, out Texture? tex))
            {
                tex = Texture.From2D(_gl, t.Bgra, t.Width, t.Height, mipmaps: true, repeat: true);
                _texCache[t.Path] = tex;
            }
            appearance.Textures.Add(tex);
        }
        if (_attachedItems is not null)
        {
            _attachedItems.RaceGenderCode = RaceGenderCode(prep.Race, prep.Gender);
            appearance.Mounts = _attachedItems.BuildMountSet(prep.Equipment);
        }
        return appearance;
    }

    /// <summary>Worker-thread: build the dressed atlas + per-batch textures + visible geosets.</summary>
    private PreparedAppearance? PrepareAppearance(M2Model m2, CharacterEquipment equipment,
        byte race, byte gender)
    {
        var prep = new PreparedAppearance { Equipment = equipment, Race = race, Gender = gender };
        // Appearance bytes ride on the kit (set in BuildEquipment) so the worker needs no extra descriptor.
        CharacterEquipment.PlayerAppearance look = equipment.PlayerLook ?? default;
        byte skin = look.Skin, face = look.Face, hairStyle = look.HairStyle,
             hairColor = look.HairColor, facialHair = look.FacialHair;

        // Dressed skin atlas: bare (skin+face+facial+hair) then armor painted on top.
        PreparedTexture? atlas = BuildDressedAtlas(equipment, race, gender,
            skin, face, hairStyle, hairColor, facialHair);
        prep.AtlasPath = atlas?.Path ?? "";

        // Hair (type 6) texture and the cloak's cape (type 2) texture.
        PreparedTexture? hairTex = BuildHairTexture(race, gender, hairStyle, hairColor);
        PreparedTexture? capeTex = BuildCapeTexture(equipment, gender);

        // Geosets.
        if (_geosets is not null)
        {
            EquipGeosets equip = BuildEquipGeosets(equipment);
            HashSet<int> visible = _geosets.Visible(race, gender, hairStyle, facialHair, equip);
            prep.VisibleGeosets = m2.Submeshes.Any(sm => visible.Contains(sm.Id)) ? visible : null;
        }

        PreparedTexture? carried = null;
        foreach (M2Batch batch in m2.Batches)
        {
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            uint type = 0;
            string embedded = "";
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                int ti = m2.TextureLookup[batch.TextureIndex];
                if (ti >= 0 && ti < m2.Textures.Count)
                {
                    type = m2.Textures[ti].Type;
                    embedded = m2.Textures[ti].Filename ?? "";
                }
            }

            PreparedTexture? tex = type switch
            {
                1 => atlas,                                   // CHAR_SKIN body/face -> dressed atlas
                2 => capeTex,                                 // OBJECT_SKIN -> the cloak's cape texture
                6 => hairTex,                                 // CHAR_HAIR
                _ when embedded.Length > 0 => LoadPrepared(embedded),
                _ => null,
            };
            if (tex is not null) carried = tex;
            else tex = carried;
            prep.BatchTextures.Add(tex);
        }
        return prep;
    }

    /// <summary>Bare skin atlas (CharSections) with armor composited on top.</summary>
    private PreparedTexture? BuildDressedAtlas(CharacterEquipment equipment, byte race, byte gender,
        byte skin, byte face, byte hairStyle, byte hairColor, byte facialHair)
    {
        if (_charSections is null) return null;
        string raceFolder = RaceFolder(race);
        string genderName = gender == 1 ? "Female" : "Male";

        // Base skin BLP.
        var skinRow = _charSections.Find(race, gender, CharSectionsTable.SectionSkin, -1, skin);
        IEnumerable<string> skinCandidates = skinRow is null
            ? Array.Empty<string>()
            : CharacterTextureCandidates(skinRow.Texture1, race, gender);
        skinCandidates = skinCandidates.Concat(
        [
            $@"Character\{raceFolder}\{genderName}\{raceFolder}{genderName}Skin{(int)skin:00}_00.blp",
            $@"Character\{raceFolder}\{genderName}\{raceFolder}{genderName}Skin00_00.blp",
        ]).Distinct(StringComparer.OrdinalIgnoreCase);

        byte[]? atlas = null;
        int w = 0, h = 0;
        foreach (string candidate in skinCandidates)
        {
            byte[]? bytes = _mpq.ReadFile(candidate);
            if (bytes is null) continue;
            try { atlas = BlpDecoder.GetPixels(bytes, 0, out w, out h); break; }
            catch { }
        }
        if (atlas is null) return null;

        void Overlay(string partial, bool upper)
        {
            if (partial.Length == 0) return;
            foreach (string candidate in CharacterTextureCandidates(partial, race, gender))
            {
                byte[]? bytes = _mpq.ReadFile(candidate);
                if (bytes is null) continue;
                try
                {
                    byte[] px = BlpDecoder.GetPixels(bytes, 0, out int pw, out int ph);
                    (int x, int y, int rw, int rh) = upper ? (0, 160, 128, 32) : (0, 192, 128, 64);
                    float sx = w / 256f, sy = h / 256f;
                    CharacterEquipment.BlitOver(atlas, w, h, px, pw, ph,
                        (int)(x * sx), (int)(y * sy), (int)(rw * sx), (int)(rh * sy));
                    return;
                }
                catch { }
            }
        }

        var faceRow = _charSections.Find(race, gender, CharSectionsTable.SectionFace, face, skin);
        if (faceRow is not null) { Overlay(faceRow.Texture1, false); Overlay(faceRow.Texture2, true); }
        var facialRow = _charSections.Find(race, gender, CharSectionsTable.SectionFacialHair, facialHair, hairColor);
        if (facialRow is not null) { Overlay(facialRow.Texture1, false); Overlay(facialRow.Texture2, true); }
        var hairRow = _charSections.Find(race, gender, CharSectionsTable.SectionHair, hairStyle, hairColor);
        if (hairRow is not null) { Overlay(hairRow.Texture2, false); Overlay(hairRow.Texture3, true); }

        // Paint equipped armor onto the atlas (the piece the NPC path lacks).
        equipment.GenderSuffix = gender == 1 ? "F" : "M";
        byte[] dressed;
        try
        {
            dressed = equipment.Composite(atlas, w, h, LoadForComposite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[player] atlas composite failed: {ex.Message}");
            dressed = atlas;
        }

        return new PreparedTexture
        {
            Path = "player-atlas://" + equipment.Signature + $"|{race}.{gender}.{skin}.{face}.{hairStyle}.{hairColor}.{facialHair}",
            Bgra = dressed, Width = w, Height = h,
        };
    }

    private (byte[] bgra, int w, int h)? LoadForComposite(string path)
    {
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        try { byte[] px = BlpDecoder.GetPixels(bytes, 0, out int w, out int h); return (px, w, h); }
        catch { return null; }
    }

    private PreparedTexture? BuildHairTexture(byte race, byte gender, byte hairStyle, byte hairColor)
    {
        if (_charSections is null) return null;
        CharSectionRow? row = _charSections.Find(race, gender, CharSectionsTable.SectionHair, hairStyle, hairColor)
            ?? _charSections.Find(race, gender, CharSectionsTable.SectionHair, 1, hairColor);
        if (row is null || row.Texture1.Length == 0) return null;
        foreach (string candidate in CharacterTextureCandidates(row.Texture1, race, gender))
            return LoadPrepared(candidate);   // first candidate is the canonical path; decode happens in LoadPrepared
        return null;
    }

    private PreparedTexture? BuildCapeTexture(CharacterEquipment equipment, byte gender)
    {
        CharacterEquipment.Piece? cloak = equipment.Pieces.LastOrDefault(p =>
            p.InventoryType == CharacterEquipment.Slot.Cloak && p.Row is not null);
        if (cloak?.Row is null) return null;
        foreach (string name in new[] { cloak.Row.ModelTexture1, cloak.Row.ModelTexture2 }
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string candidate in CapeTextureCandidates(name, gender))
                if (LoadPrepared(candidate) is { } tex) return tex;
        return null;
    }

    private static IEnumerable<string> CapeTextureCandidates(string partial, byte gender)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        bool hasDirectory = stem.Contains('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        if (hasDirectory) { yield return stem + ".blp"; yield break; }
        string suffix = gender == 1 ? "F" : "M";
        yield return $@"Item\ObjectComponents\Cape\{stem}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_U.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_U.blp";
    }

    private PreparedTexture? LoadPrepared(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        try
        {
            byte[] px = BlpDecoder.GetPixels(bytes, 0, out int w, out int h);
            return new PreparedTexture { Path = path, Bgra = px, Width = w, Height = h };
        }
        catch { return null; }
    }

    // ── equipment / geosets from player fields ──────────────────────────────────

    private CharacterEquipment BuildEquipment(WorldEntity e, NetworkClient? net, ItemTemplateCache? items)
    {
        var kit = new CharacterEquipment();
        (byte race, _, byte gender, _) = e.Fields.Bytes0;
        (byte skin, byte face, byte hairStyle, byte hairColor) = e.Fields.PlayerAppearance;
        kit.PlayerLook = new CharacterEquipment.PlayerAppearance(
            skin, face, hairStyle, hairColor, e.Fields.PlayerFacialHair);

        if (items is not null)
        {
            for (int slot = 0; slot < 19; slot++)
            {
                uint entry = e.Fields.PlayerVisibleItemEntry(slot);
                if (entry == 0) continue;
                if (net is not null) items.Require(entry, e.Guid, net);
                if (!EquipmentDisplayPreferenceLaw.EquipmentSlotShown(
                        slot, e.Fields.PlayerFlags)) continue;
                if (!items.TryGet(entry, out ItemTemplate? t) || t is null) continue;
                kit.Add($"slot{slot}", t.DisplayInfoId, (int)t.InventoryType, slot,
                    (byte)t.Class, (byte)t.Subclass, (byte)t.Material, (byte)t.Sheath,
                    Enumerable.Range(0, 7)
                        .Select(enchantSlot => e.Fields.PlayerVisibleItemEnchant(slot, enchantSlot))
                        .ToArray());
            }
        }
        kit.Resolve(_itemDisplay);
        return kit;
    }

    /// <summary>
    /// A content-addressed signature that CONVERGES: identical looks share it (bots collapse to one
    /// appearance), and it changes only while item queries are still resolving. Per equipped slot the
    /// token is the resolved display id, "?entry" while the query is outstanding, or "0" once known
    /// empty — so it stabilizes after the last SMSG_ITEM_QUERY_SINGLE_RESPONSE and rebuilds stop.
    /// Cheap: dictionary lookups only, no kit build, no logging.
    /// </summary>
    private string AppearanceSignature(WorldEntity e, byte race, byte gender,
        NetworkClient? net, ItemTemplateCache? items)
    {
        (byte skin, byte face, byte hairStyle, byte hairColor) = e.Fields.PlayerAppearance;
        var sb = new System.Text.StringBuilder(96);
        sb.Append(race).Append('/').Append(gender).Append('/')
          .Append(skin).Append('/').Append(face).Append('/')
          .Append(hairStyle).Append('/').Append(hairColor).Append('/')
          .Append(e.Fields.PlayerFacialHair).Append('|')
          .Append(e.Fields.PlayerFlags &
              (EquipmentDisplayPreferenceLaw.HideHelm |
               EquipmentDisplayPreferenceLaw.HideCloak)).Append('|');
        for (int slot = 0; slot < 19; slot++)
        {
            uint entry = e.Fields.PlayerVisibleItemEntry(slot);
            if (entry == 0) { sb.Append("0:"); continue; }
            if (items is null) { sb.Append('?').Append(entry).Append(':'); continue; }
            if (net is not null) items.Require(entry, e.Guid, net);
            if (!EquipmentDisplayPreferenceLaw.EquipmentSlotShown(
                    slot, e.Fields.PlayerFlags))
            {
                sb.Append("hidden:");
                continue;
            }
            if (items.TryGet(entry, out ItemTemplate? t))
            {
                sb.Append(t?.DisplayInfoId ?? 0).Append(':');
                for (int enchantSlot = 0; enchantSlot < 7; enchantSlot++)
                    sb.Append(e.Fields.PlayerVisibleItemEnchant(slot, enchantSlot)).Append(',');
            }
            else
                sb.Append('?').Append(entry).Append(':');
        }
        return sb.ToString();
    }

    private EquipGeosets BuildEquipGeosets(CharacterEquipment equipment)
    {
        var eq = new EquipGeosets();
        foreach (CharacterEquipment.Piece p in equipment.Pieces)
        {
            if (p.Row is null) continue;
            switch (p.InventoryType)
            {
                case CharacterEquipment.Slot.Shirt: eq.Bodyslots[0] = p.Row; break;
                case CharacterEquipment.Slot.Chest:
                case CharacterEquipment.Slot.Robe: eq.Bodyslots[1] = p.Row; break;
                case CharacterEquipment.Slot.Waist: eq.Bodyslots[2] = p.Row; break;
                case CharacterEquipment.Slot.Legs: eq.Bodyslots[3] = p.Row; break;
                case CharacterEquipment.Slot.Feet: eq.Bodyslots[4] = p.Row; break;
                case CharacterEquipment.Slot.Wrists: eq.Bodyslots[5] = p.Row; break;
                case CharacterEquipment.Slot.Hands: eq.Bodyslots[6] = p.Row; break;
                case CharacterEquipment.Slot.Tabard: eq.Bodyslots[7] = p.Row; break;
                case CharacterEquipment.Slot.Cloak:
                    eq.HasCloak = true;
                    eq.CloakGroup = p.Row.GeosetGroup.Length > 0 ? p.Row.GeosetGroup[0] : 0;
                    break;
                case CharacterEquipment.Slot.Head:
                    eq.HelmVis = (p.Row.HelmetGeosetVis1, p.Row.HelmetGeosetVis2);
                    break;
            }
        }
        return eq;
    }

    // ── shared helpers ──────────────────────────────────────────────────────────

    private static string ModelPath(byte race, byte gender)
    {
        string raceFolder = RaceFolder(race);
        string genderName = gender == 1 ? "Female" : "Male";
        return $@"Character\{raceFolder}\{genderName}\{raceFolder}{genderName}.m2";
    }

    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    private static string RaceGenderCode(byte race, byte gender) =>
        (race switch
        {
            1 => "Hu", 2 => "Or", 3 => "Dw", 4 => "Ni",
            5 => "Sc", 6 => "Ta", 7 => "Gn", 8 => "Tr", _ => "Hu",
        }) + (gender == 1 ? "F" : "M");

    private static IEnumerable<string> CharacterTextureCandidates(string partial, byte race, byte gender)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        string raceFolder = RaceFolder(race);
        string genderName = gender == 1 ? "Female" : "Male";
        yield return stem + ".blp";
        yield return $@"Character\{stem}.blp";
        yield return $@"Character\{raceFolder}\{genderName}\{stem}.blp";
    }

    private static float ClampBone(byte index) => index < M2Animator.MaxBones ? index : 0f;

    private unsafe void DrawElements(int start, int count)
        => _gl.DrawElements(PrimitiveType.Triangles, (uint)count,
            DrawElementsType.UnsignedShort, (void*)(start * sizeof(ushort)));

    public void Dispose()
    {
        foreach (LoadedModel? m in _modelCache.Values)
        {
            if (m is null) continue;
            if (m.Vbo != 0) _gl.DeleteBuffer(m.Vbo);
            if (m.Ebo != 0) _gl.DeleteBuffer(m.Ebo);
            if (m.Vao != 0) _gl.DeleteVertexArray(m.Vao);
        }
        _modelCache.Clear();
        _appearanceCache.Clear();
        foreach (Texture? t in _texCache.Values) t?.Dispose();
        _texCache.Clear();
        _attachedItems?.Dispose();
        _shader?.Dispose();
    }

    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNorm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aBoneWeights;
layout(location=4) in vec4 aBoneIndices;
uniform mat4 uModel;
uniform mat4 uViewProj;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
out vec3 vNorm;
out vec2 vUv;
out float vDist;
vec3 skinPoint(vec3 p, int b){
    vec4 h = vec4(p, 1.0);
    return vec3(dot(uBones[b*3+0], h), dot(uBones[b*3+1], h), dot(uBones[b*3+2], h));
}
vec3 skinVec(vec3 v, int b){
    return vec3(dot(uBones[b*3+0].xyz, v), dot(uBones[b*3+1].xyz, v), dot(uBones[b*3+2].xyz, v));
}
void main(){
    vec3 position = aPos;
    vec3 normal = aNorm;
    if (uBoneCount > 0){
        vec3 sp = vec3(0.0); vec3 sn = vec3(0.0); float total = 0.0;
        for (int i = 0; i < 4; i++){
            float w = aBoneWeights[i];
            if (w <= 0.0) continue;
            int b = int(aBoneIndices[i] + 0.5);
            if (b < 0 || b >= uBoneCount) continue;
            sp += skinPoint(aPos, b) * w;
            sn += skinVec(aNorm, b) * w;
            total += w;
        }
        if (total > 0.0001){ position = sp / total; normal = sn / total; }
    }
    vec4 rel = uModel * vec4(position, 1.0);
    gl_Position = uViewProj * rel;
    vNorm = normalize(mat3(uModel) * normal);
    vUv = aUv;
    vDist = length(rel.xyz);
}";

    private const string FragSrc = @"#version 330 core
in vec3 vNorm;
in vec2 vUv;
in float vDist;
uniform sampler2D uTex;
uniform vec3 uSunDir;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uAlphaCut;
uniform float uHighlight;
uniform vec3 uAmbientColor;
uniform vec3 uDiffuseColor;
out vec4 frag;
void main(){
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    float ndl = max(dot(normalize(vNorm), normalize(uSunDir)), 0.0);
    vec3 light = uAmbientColor + uDiffuseColor * ndl + vec3(uHighlight);
    float fog = clamp((vDist - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
    frag = vec4(mix(t.rgb * light, uFogColor, fog), t.a);
}";
}
