using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

// Draws the networked unit stream: creatures/NPCs and remote players as their M2 models
// at the server-given position / orientation / scale, skinned, animated and textured.
// Player appearance/equipment is explicitly gated from the established NPC path.
//
// TRANSFORM (camera-relative, matches CharacterRenderer):
//   Scale * RotationY(heading) * Basis * Translate(pos), eye subtracted from the row.
//
// TEXTURES: resolved BY M2 TEXTURE TYPE — 0 embedded, 11/12/13 monster-skin variations,
//   type-1 CHAR_SKIN via CreatureDisplayInfoExtra (baked atlas or default body skin).
//
// GEOSETS (new): a character-model NPC's M2 holds EVERY variant (all hairstyles, beards,
//   sleeves...). CharacterGeosets.Visible() (benilla visible_geosets) computes the set of
//   skinSectionIds to draw from the NPC's CreatureDisplayInfoExtra hair/facial/equipment;
//   any submesh not in the set is skipped. Beasts are unfiltered (they have no variants).
//
// ANIMATION: one M2Animator per model, per-instance clock; idle/walk/run from spline speed.

public sealed partial class CreatureRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly ClientConfig _config;
    private readonly CreatureLifecycleTracker? _lifecycle;
    private readonly AssetWorkerPool? _workers;
    private readonly GpuUploadWorker? _uploads;
    private readonly string _attachmentShaderDir;
    private Shader? _shader;
    private CreatureModelResolver? _resolver;
    private ItemDisplayTable? _itemDisplay;
    private CharSectionsTable? _charSections;
    private CharacterGeosets? _geosets;
    private readonly Dictionary<string, LoadedModel?> _modelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Appearance?> _appearanceCache = new(StringComparer.OrdinalIgnoreCase);
    private sealed class UnitAttachments
    {
        public AttachedItemRenderer.MountSet Mounts = null!;
        public string Signature = "";
        public float LastSeenAt;
    }
    private readonly Dictionary<ulong, UnitAttachments> _unitAttachments = [];
    private AttachedItemRenderer? _attachedItems;
    private readonly Matrix4x4[] _bindSkin = Enumerable.Repeat(Matrix4x4.Identity, M2Animator.MaxBones).ToArray();

    public bool Enabled { get; set; } = true;
    public bool Ok { get; private set; }

    public float HeadingOffsetDegrees { get; set; } = 90f;
    public float ScaleMultiplier { get; set; } = 1f;
    public int DrawnLastFrame { get; private set; }
    public int PlayersDrawnLastFrame { get; private set; }
    public int AnimatedLastFrame { get; private set; }
    public double LoadMillisecondsThisFrame { get; private set; }
    public int LoadsThisFrame { get; private set; }
    public int CacheEntries => _modelCache.Count;
    public long CombatActionsTriggered { get; private set; }
    public int CombatActionsActive => _combatActions.Count;
    public ulong HoveredGuid { get; set; }
    public ulong SelectedGuid { get; set; }
    public ulong SelfPlayerGuid { get; set; }

    /// <summary>CRPG free-view multi-selection: every member wears the target highlight.</summary>
    public HashSet<ulong> GroupSelectedGuids { get; } = [];

    /// <summary>
    /// Tool-owned selections that must read unmistakably on the model itself. Encounter Lab
    /// authoring uses this instead of overloading the ordinary hover/target lift, which is
    /// intentionally subtle during normal play.
    /// </summary>
    public HashSet<ulong> ProminentSelectedGuids { get; } = [];
    public Action<string, int, M2Animator.Resolution>? AnimationResolved { get; set; }
    /// <summary>Emotes.dbc id (UNIT_NPC_EMOTESTATE space) -&gt; AnimationData id, 0 if none.
    /// Wired to the live <see cref="Formats.EmoteCatalog"/> - same resolver the local
    /// player uses, so remote Dance resolves from the real DBC, not a static table.</summary>
    public Func<uint, int>? EmoteAnimResolver { get; set; }
    /// <summary>Creature template TypeFlags by entry. Off-frustum animation event
    /// tracks stay silent unless the reference's MORE_AUDIBLE bit (0x20) is set.</summary>
    public Func<uint, uint?>? TypeFlagsFor { get; set; }
    /// <summary>rider/root guid, event-source display id, world-space feet.</summary>
    public Action<ulong, int, Vector3, float>? FootstepAnimationEvent { get; set; }
    public Action<ulong, int, Vector3, string>? CreatureAnimationSoundEvent { get; set; }
    public Action<ulong, string>? CombatAnimationSoundEvent { get; set; }

    /// <summary>
    /// Read-only bridge to the game loop's ask-once item-template cache. Remote players expose
    /// public item entries rather than display ids; rendering waits until every non-empty entry
    /// has a settled lookup so the first visible frame is already dressed.
    /// </summary>
    public Func<uint, (bool Settled, ItemTemplate? Item)>? PlayerItemResolver { get; set; }

    public readonly record struct PortraitSpecimen(int DisplayId, string ModelPath);
    private IReadOnlyList<PortraitSpecimen> _portraitSpecimens = Array.Empty<PortraitSpecimen>();
    public IReadOnlyList<PortraitSpecimen> PortraitSpecimens => _portraitSpecimens;

    private const int BaseAnimationTrack = 0;
    private const int ActionAnimationTrack = 1;
    private const int SpellHoldAnimationTrack = 2;

    /// <summary>Master animation switch (off = static bind pose).</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Beyond this range a creature draws its static bind pose (skinning you couldn't see anyway).</summary>
    public float AnimateDistance { get; set; } = 130f;

    /// <summary>Rendered scale used by the CPU targeting proxy.</summary>
    public float PickScale(WorldEntity entity)
        => UnitRenderScale(entity.Scale, ScaleMultiplier);

    public float DisplayBaseAlpha(int displayId) => _resolver?.BaseAlpha(displayId) ?? 1f;

    public static float UnitRenderScale(float objectFieldScale, float tuningMultiplier = 1f)
        => MathF.Max(0.01f, objectFieldScale) * tuningMultiplier;

    /// <summary>Filter humanoid-NPC geosets to the correct variants (off = draw every geoset, the old blob).</summary>
    public bool GeosetFilter { get; set; } = true;

    /// <summary>
    /// The game loop copies these from WorldAtmosphere every frame. Keeping the same property
    /// shape as CharacterRenderer also lets streamed equipment inherit the exact unit lighting.
    /// </summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    private const float DefaultWalkSpeed = 2.5f;
    private const float MovingEpsilon = 0.1f;

    private static readonly Matrix4x4 Basis = new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private const int FloatsPerVertex = 16;   // pos3 + norm3 + uv2 + weight4 + index4
    private const double FinalizeBudgetMs = 2.0;
    private int _diagLogged;

    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly Dictionary<ulong, SpellUnitPose> _spellPoses = [];
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];

    private readonly Dictionary<ulong, float> _animTime = new();
    private readonly Dictionary<ulong, TacticalFreezeVisualLatch> _tacticalFreezeVisuals = [];
    private readonly Dictionary<ulong, float> _tacticalFreezeStartedAt = [];
    private readonly Dictionary<ulong, (int Sequence, float Time)> _footstepTime = [];
    private readonly Dictionary<ulong, float> _animationEventOutOfViewSince = [];
    private readonly Dictionary<ulong, CombatAction> _combatActions = new();
    /// <summary>Edge-detect for the _combatActions movement-break rule below - see
    /// its comment (and CharacterRenderer.Update's matching one) for why this has
    /// to be a change, not a level check. Pruned in PruneAnimState.</summary>
    private readonly Dictionary<ulong, bool> _wasMovingByGuid = new();
    // Benilla's route_oneshot verdict for each unit's CURRENT play, the per-guid mirror of
    // CharacterRenderer's _combatActionMasked. Keyed on the play's StartedAt so a fresh trigger
    // re-routes while a resend of the same play keeps the route it was armed with.
    private readonly Dictionary<ulong, (float StartedAt, bool Masked, bool Seated)>
        _combatActionRoute = new();
    private readonly Dictionary<ulong, int> _spellHolds = new();
    private readonly HashSet<ulong> _lootKneeling = new();
    private readonly HashSet<ulong> _knownAlive = new();
    private readonly HashSet<ulong> _observedDead = new();
    private readonly Dictionary<ulong, float> _deathTime = new();
    private readonly Dictionary<ulong, float> _deadCreatureSeenAt = new();
    private readonly Dictionary<ulong, float> _respawnFadeStartedAt = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _stale = new();
    private readonly List<WorldEntity> _orderedUnits = [];
    private readonly List<UnitShadowCaster> _shadowCasters = [];
    private Vector3 _sortCameraPosition;
    private float _globalTime;
    private const float DeadCreatureRetentionSeconds = 10f * 60f;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;

    private readonly record struct CombatAction(int AnimationId, float StartedAt, float ExpiresAt,
        bool AuthoredExact = false);
    private readonly record struct TacticalFreezeVisualLatch(
        float EvaluationGlobalTime,
        M2Animator.Clip? Clip, float ClipTime,
        M2Animator.Clip? TorsoOverlay, float TorsoOverlayTime);

    /// <summary>Units that completed a model draw this frame, consumed by the shared blob pass.</summary>
    public IReadOnlyList<UnitShadowCaster> ShadowCasters => _shadowCasters;

    /// <summary>Turns the model's authored XY bounds into a stable contact-shadow half extent.</summary>
    public static float GroundShadowRadius(float horizontalRadius, float renderScale)
        => Math.Clamp(MathF.Max(0f, horizontalRadius) * MathF.Max(0.01f, renderScale),
            0.35f, 12f);

    public void TriggerCombatSwing(ulong guid, bool offHand)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        _combatActions[guid] = new CombatAction(offHand ? 87 : 16, _globalTime, _globalTime + 3f);
        CombatActionsTriggered++;
    }

    public void TriggerOneShot(ulong guid, int animationId)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        // The server double-fires the eat/drink emote ~0.1-0.2s apart (confirmed on
        // the wire). A same-animation re-trigger while the previous one is still
        // running keeps its StartedAt so the clip isn't snapped back to frame 0
        // mid-play - only the expiry window is refreshed. A genuinely new emote (the
        // previous already finished and was pruned) starts fresh, preserving the
        // natural settle-between-bites rhythm. Unlike the local player (whose overlay
        // clock survives a re-trigger because Resolve hands back a cached clip and its
        // reset keys on clip identity), the remote clock is StartedAt-relative, so it
        // needs this explicitly. TriggerOneShot is the emote-only path; swings/spells
        // trigger elsewhere.
        float startedAt = _combatActions.TryGetValue(guid, out CombatAction existing) &&
                          existing.AnimationId == animationId
            ? existing.StartedAt
            : _globalTime;
        _combatActions[guid] = new CombatAction(animationId, startedAt,
            _globalTime + 4f, AuthoredExact: true);
        CombatActionsTriggered++;
    }

    /// <summary>
    /// Optimistic CMSG_LOOT pose for a streamed player body. This is a durable base-pose
    /// override rather than a one-shot because the latch owns it until loot opens/refuses.
    /// </summary>
    public void SetLootKneel(ulong guid, bool kneeling)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        bool changed = kneeling ? _lootKneeling.Add(guid) : _lootKneeling.Remove(guid);
        if (changed) _animTime[guid] = 0f;
    }

    public void TriggerCombatReaction(ulong guid, uint victimState, bool landedHit)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        int animationId = victimState switch
        {
            2 or 8 => 30, // dodge / deflect
            3 => 20,      // unarmed parry fallback
            5 => 24,      // shield block
            _ when landedHit => 9, // CombatWound
            _ => -1,
        };
        if (animationId >= 0)
        {
            _combatActions[guid] = new CombatAction(animationId, _globalTime, _globalTime + 3f);
            CombatActionsTriggered++;
        }
    }

    public void BeginSpellVisual(ulong guid, ushort? animationId)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        if (animationId is { } id && id != 0) _spellHolds[guid] = id;
        else _spellHolds.Remove(guid);
        _animTime[guid] = 0f;
    }

    public void ReleaseSpellVisual(ulong guid, ushort? animationId)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        _spellHolds.Remove(guid);
        if (animationId is { } id && id != 0)
        {
            _combatActions[guid] = new CombatAction(id, _globalTime, _globalTime + 4f,
                AuthoredExact: true);
            CombatActionsTriggered++;
        }
    }

    public void CancelSpellVisual(ulong guid)
    {
        if (!TacticalFreezePoseLaw.IsFrozen(guid)) _spellHolds.Remove(guid);
    }

    /// <summary>
    /// A direct RTS move is an action boundary: the ordered body must leave any packet-driven
    /// cast/swing one-shot immediately instead of finishing several authored seconds while its
    /// server spline is already carrying it away. Translation still begins only when the
    /// authoritative spline arrives; this method owns presentation state alone.
    /// </summary>
    public void InterruptActionForMovement(ulong guid)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid)) return;
        _spellHolds.Remove(guid);
        _combatActions.Remove(guid);
        _animTime[guid] = 0f;
    }

    private sealed class LoadedModel
    {
        public uint Vao, Vbo, Ebo;
        public readonly List<DrawBatch> Batches = new();
        public M2Animator? Animator;
        public int BoneCount;
        public float MinHeight;
        public float MaxHeight;
        public float HorizontalRadius;
        public M2PortraitCamera? PortraitCamera;
        public M2Model Source = null!;
        public HashSet<int>? VisibleGeosets; // legacy first-load copy; render uses Appearance
    }
    private sealed class Appearance
    {
        public readonly List<Texture?> Textures = [];
        public HashSet<int>? VisibleGeosets;
    }
    private struct DrawBatch
    {
        public int Start, Count;
        public Texture? Tex;
        public int Blend;
        public int GeosetId;
        public bool TwoSided;
    }

    private sealed class PreparedModel
    {
        public M2Model? Source;
        public M2Animator? Animator;
        public float[] Vertices = [];
        public ushort[] Indices = [];
        public float MinHeight;
        public float MaxHeight;
        public float HorizontalRadius;
    }

    private readonly record struct UploadedBuffers(uint Vbo, uint Ebo);

    private sealed class ModelLoadJob
    {
        public required string Path;
        public required Task<PreparedModel?> Worker;
        public PreparedModel? Ready;
        public Task<UploadedBuffers>? Upload;
    }

    private sealed class PreparedTexture
    {
        public string Path = "";
        public byte[]? Bgra;
        public int Width;
        public int Height;
    }

    private sealed class PreparedAppearance
    {
        public HashSet<int>? VisibleGeosets;
        public List<PreparedTexture?> Textures = [];
    }

    private sealed class AppearanceLoadJob
    {
        public required Task<PreparedAppearance?> Worker;
        public PreparedAppearance? Ready;
        public Task<Dictionary<string, Texture?>>? Upload;
    }

    private readonly Dictionary<string, ModelLoadJob> _modelJobs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requestedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppearanceLoadJob> _appearanceJobs =
        new(StringComparer.OrdinalIgnoreCase);

    public CreatureRenderer(GL gl, MpqMount mpq, ClientConfig config,
        CreatureLifecycleTracker? lifecycle = null, AssetWorkerPool? workers = null,
        GpuUploadWorker? uploads = null)
    {
        _gl = gl;
        _mpq = mpq;
        _config = config;
        _lifecycle = lifecycle;
        _workers = workers;
        _uploads = uploads;
        _attachmentShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(_attachmentShaderDir, "attached.vert")))
            _attachmentShaderDir = Path.Combine(config.RepoRoot, "MSUIClient", "Shaders");
        try
        {
            var diBytes = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath);
            var mdBytes = mpq.ReadFile(CreatureModelDataTable.MpqPath);
            var exBytes = mpq.ReadFile(CreatureDisplayExtraTable.MpqPath);
            var di = diBytes is null ? null : CreatureDisplayInfoTable.Parse(diBytes);
            var md = mdBytes is null ? null : CreatureModelDataTable.Parse(mdBytes);
            var ex = exBytes is null ? null : CreatureDisplayExtraTable.Parse(exBytes);
            if (di is not null && md is not null)
            {
                _resolver = new CreatureModelResolver(di, md, ex);
                _portraitSpecimens = di.All
                    .Select(row => _resolver.TryResolve((int)row.Id, out CreatureModelInfo info)
                        ? new PortraitSpecimen((int)row.Id, info.ModelPath)
                        : default)
                    .Where(specimen => specimen.DisplayId > 0)
                    .OrderBy(specimen => specimen.DisplayId)
                    .ToArray();
                _shader = Shader.FromSource(_gl, "creature", VertSrc, FragSrc);
                _attachedItems = new AttachedItemRenderer(_gl, _config);
                _attachedItems.LoadShaders(_attachmentShaderDir);

                // Geoset visibility for humanoid NPCs (best-effort — filter degrades to naked defaults).
                var idBytes = mpq.ReadFile(ItemDisplayTable.MpqPath);
                _itemDisplay = idBytes is null ? null : ItemDisplayTable.Parse(idBytes);
                var hairBytes = mpq.ReadFile(CharHairGeosetsTable.MpqPath);
                var facialBytes = mpq.ReadFile(CharacterFacialHairTable.MpqPath);
                var helmBytes = mpq.ReadFile(HelmetGeosetVisTable.MpqPath);
                var sectionBytes = mpq.ReadFile(CharSectionsTable.MpqPath);
                _charSections = sectionBytes is null ? null : CharSectionsTable.Parse(sectionBytes);
                _geosets = new CharacterGeosets(
                    hairBytes is null ? null : CharHairGeosetsTable.Parse(hairBytes),
                    facialBytes is null ? null : CharacterFacialHairTable.Parse(facialBytes),
                    helmBytes is null ? null : HelmetGeosetVisTable.Parse(helmBytes));

                Ok = true;
                Console.WriteLine($"[creature] renderer ready ({di.Count} display rows, {md.Count} models, " +
                                  $"{(ex?.Count ?? 0)} extended-npc rows, geosets={(_geosets.Ok ? "on" : "no-dbc")}, " +
                                  $"itemdisplay={(_itemDisplay is null ? "MISSING" : _itemDisplay.Count.ToString())})");
            }
            else Console.WriteLine("[creature] CreatureDisplayInfo/CreatureModelData DBCs missing — unit rendering off");
        }
        catch (Exception e) { Console.WriteLine($"[creature] init failed: {e.Message}"); Ok = false; }
    }

    public void Render(Camera camera, IReadOnlyList<WorldEntity> visibleUnits)
    {
        _attachedItems?.BeginGlowFrame();
        DrawnLastFrame = 0;
        PlayersDrawnLastFrame = 0;
        AnimatedLastFrame = 0;
        LoadMillisecondsThisFrame = 0;
        LoadsThisFrame = 0;
        _shadowCasters.Clear();
        double nowS = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(nowS - _lastSeconds, 0.0, 0.1);
        _lastSeconds = nowS;
        _globalTime += dt;
        TrackTacticalFreezeIntervals();
        ReconcileTacticalFreezeThaws();
        if (!Ok || !Enabled || _shader is null || _resolver is null) return;

        Vector3 camPos = camera.Position;
        Matrix4x4 viewProj = camera.RelativeViewProjection;
        float heading0 = HeadingOffsetDegrees * MathF.PI / 180f;

        BeginUnitShader(camera);
        _seen.Clear();
        _spellPoses.Clear();

        _orderedUnits.Clear();
        foreach (WorldEntity entity in visibleUnits)
            if (entity.IsUnit && (!entity.IsPlayer || entity.Guid != SelfPlayerGuid))
                _orderedUnits.Add(entity);
        _sortCameraPosition = camPos;
        _orderedUnits.Sort(CompareUnitDistance);

        foreach (var e in _orderedUnits)
        {
            CreatureLifecycleTracker? lifecycle = e.IsCreature ? _lifecycle : null;
            if (e.DisplayId <= 0)
            {
                lifecycle?.NoteReason(e.Guid, CreatureLifecycleTracker.ReasonCode.QUERY_PENDING);
                continue;
            }
            if (!TryResolveRenderInfo(e, out CreatureModelInfo info))
            {
                lifecycle?.NoteReason(e.Guid, CreatureLifecycleTracker.ReasonCode.RESOLVE_FAILED);
                continue;
            }
            lifecycle?.NoteDisplayResolved(e.Guid, e.DisplayId, info.ModelPath);

            string modelKey = info.ModelPath;
            bool spentLoadSlot = false;
            if (!_modelCache.TryGetValue(modelKey, out var model))
            {
                bool firstEnqueue = lifecycle?.NoteLoadEnqueued(e.Guid, "world-render") == true;
                if (firstEnqueue)
                    Console.WriteLine($"[creature-lifecycle] {e.Guid:X16} enqueue world-render {info.ModelPath}");
                if (!EligibleForLoad(e, camPos, camera.Target, viewProj) ||
                    LoadMillisecondsThisFrame >= FinalizeBudgetMs)
                    continue;
                lifecycle?.NoteModelLoading(e.Guid);
                if (!TryAcquireModel(info, allowFinalize: true, out model, out bool finalizedModel))
                    continue;
                if (finalizedModel)
                {
                    spentLoadSlot = true;
                }
            }
            if (model is null)
            {
                lifecycle?.NoteReason(e.Guid, CreatureLifecycleTracker.ReasonCode.RESOLVE_FAILED);
                continue;
            }

            string appearanceKey = AppearanceKey(info);
            if (!_appearanceCache.TryGetValue(appearanceKey, out Appearance? appearance))
            {
                if (!spentLoadSlot)
                {
                    bool firstEnqueue = lifecycle?.NoteLoadEnqueued(e.Guid, "world-render") == true;
                    if (firstEnqueue)
                        Console.WriteLine($"[creature-lifecycle] {e.Guid:X16} enqueue world-render {info.ModelPath}");
                    if (!EligibleForLoad(e, camPos, camera.Target, viewProj) ||
                        LoadMillisecondsThisFrame >= FinalizeBudgetMs)
                        continue;
                }
                lifecycle?.NoteModelLoading(e.Guid);
                if (!TryAcquireAppearance(model, info, allowFinalize: true,
                        out appearance, out bool finalizedAppearance))
                    continue;
            }
            lifecycle?.NoteModelReady(e.Guid, appearance is not null);
            if (appearance is null) continue;

            _seen.Add(e.Guid);
            TrackLifeState(e);
            float respawnAlpha = e.IsCreature ? RespawnFadeAlpha(e.Guid) : 1f;

            // UNIT_FIELD_SCALE_X is already the complete unit render scale. vmangos folds
            // CreatureModelData × CreatureDisplayInfo into it; applying DbcScale again
            // squares native sub-1 scales and makes wolves/critters tiny.
            float scale = UnitRenderScale(e.Scale, ScaleMultiplier);
            float heading = e.Orientation + heading0;
            bool tacticalFrozen = TacticalFreezePoseLaw.IsFrozen(e.Guid);
            bool animationFrozen = e.AuraVisual.Frozen || tacticalFrozen;
            float evaluationGlobalTime = tacticalFrozen &&
                _tacticalFreezeVisuals.TryGetValue(e.Guid, out TacticalFreezeVisualLatch heldVisual)
                    ? heldVisual.EvaluationGlobalTime : _globalTime;
            bool animationEventsElected = AnimationEventsElected(e, camPos, viewProj);
            if (!animationEventsElected) ForgetAnimationEventClocks(e.Guid);
            bool prominentHighlight = ProminentSelectedGuids.Contains(e.Guid);
            bool highlighted = prominentHighlight || e.Guid == HoveredGuid ||
                e.Guid == SelectedGuid || GroupSelectedGuids.Contains(e.Guid);
            // A strong pulse makes authoring selection obvious even when the model stands in
            // bright light or far away. Normal hover/target selection retains the stock
            // subtle lift.
            float highlightStrength = prominentHighlight
                ? 0.95f + 0.18f * MathF.Sin(_globalTime * 4f)
                : highlighted ? 64f / 255f : 0f;

            // The steed draws FIRST, because the rider's instance transform is its saddle.
            // Until the mount model is resident this is false and the rider draws on the
            // ground as it always did — a mount pops in, it never blocks its rider.
            MountDraw mount = default;
            bool mounted = false;
            if (e.MountDisplayId > 0)
                mounted = TryDrawMount(camera, e.Guid, e.MountDisplayId, e.Position, e.Orientation,
                    e.Spline?.AverageSpeed ?? 0f,
                    e.Speeds is { Length: > 0 } speeds ? speeds[0] : 0f,
                    e.Flying || e.Spline?.Flying == true,
                    dt, animationEventsElected, highlighted,
                    e.AuraVisual.Alpha * respawnAlpha, e.AuraVisual.Tint,
                    animationFrozen, out mount);
            else ForgetMount(e.Guid);

            Matrix4x4 worldModel = mounted
                ? Matrix4x4.CreateScale(MathF.Max(0.01f, e.Scale)) * mount.Seat
                : Matrix4x4.CreateScale(scale)
                    * Matrix4x4.CreateRotationY(heading)
                    * Basis
                    * Matrix4x4.CreateTranslation(e.Position);
            Matrix4x4 m = worldModel;
            m.M41 -= camPos.X; m.M42 -= camPos.Y; m.M43 -= camPos.Z;
            _shader.Set("uModel", m);
            _shader.Set("uHighlight", highlightStrength);
            float bodyAlpha = e.AuraVisual.Alpha * respawnAlpha;
            Vector3 bodyTint = e.AuraVisual.Tint;
            bool bodyTranslucent = e.AuraVisual.Translucent ||
                respawnAlpha < 1f - AuraVisualLaw.AlphaSettledEpsilon;
            _shader.Set("uBodyAlpha", bodyAlpha);
            _shader.Set("uBodyTint", bodyTint);

            int boneCount = 0;
            M2Animator.Clip? pickClip = null;
            if (Animate && model.Animator is not null && model.BoneCount > 0 &&
                (e.IsDead || Vector3.Distance(e.Position, camPos) <= AnimateDistance))
            {
                string unit = e.IsPlayer ? $"player:{e.Guid:X16}" : $"creature:{e.DisplayId}";
                if (!_animTime.TryGetValue(e.Guid, out float at)) at = InitialPhase(e.Guid);
                bool frozen = animationFrozen;
                M2Animator.Clip? clip;
                float rate;
                // A movement-flag CHANGE (edge, not "currently moving" - see
                // CharacterRenderer.Update's matching comment for why the earlier,
                // continuous version of this was wrong) ends a one-shot immediately
                // rather than waiting out its duration. EmoteState/seated-pose below
                // stay on a continuous !remoteMoving guard deliberately - those are
                // real vanilla states that cannot coexist with movement at all
                // (confirmed: Cam's own account of standing up on movement start),
                // unlike an ordinary one-shot swing/emote, which the real client
                // would mask to the upper body and keep playing regardless of when
                // it started (tabled - see the "KNOWN WRONG" comments).
                bool remoteMoving = (e.Spline?.AverageSpeed ?? 0f) > MovingEpsilon;
                bool remoteMovementChanged = remoteMoving != _wasMovingByGuid.GetValueOrDefault(e.Guid);
                _wasMovingByGuid[e.Guid] = remoteMoving;

                // Route this unit's one-shot ONCE per play - the per-guid mirror of
                // CharacterRenderer's arm-time capture, and for the same reason: re-deciding
                // per frame would swap the base out from under a half-played clip.
                bool remoteMasked = false;
                bool remoteSeated = SeatedLoopAnimId(e.Fields.StandState) != 0;
                if (_combatActions.TryGetValue(e.Guid, out CombatAction routedAction))
                {
                    if (!_combatActionRoute.TryGetValue(e.Guid, out var route) ||
                        route.StartedAt != routedAction.StartedAt)
                    {
                        route = (routedAction.StartedAt, CharacterPoseLaw.CommittedLower(
                            moving: remoteMoving,
                            turning: false,
                            swimming: (e.MoveFlags & (uint)MovementFlags.Swimming) != 0,
                            seated: remoteSeated,
                            mounted: mounted,
                            combatAnimation: false,
                            falling: false), remoteSeated);
                        _combatActionRoute[e.Guid] = route;
                    }
                    // The stand-state clause is live, exactly as for the local player - the
                    // server's seat can land after the emote it belongs to, and a latched
                    // full-body route would leave a drinking unit standing until the play ended.
                    // See CharacterRenderer.Update. The route only ever tightens.
                    if (!route.Masked && remoteSeated)
                    {
                        route = (route.StartedAt, true, true);
                        _combatActionRoute[e.Guid] = route;
                    }
                    // Leaving the seat ends a seated consume, the mirror of the local rule.
                    if (!animationFrozen && route.Seated && !remoteSeated)
                    {
                        _combatActions.Remove(e.Guid);
                        _combatActionRoute.Remove(e.Guid);
                    }
                    else remoteMasked = route.Masked;
                }
                else _combatActionRoute.Remove(e.Guid);

                // A movement-flag change ends a FULL-BODY play only. A masked overlay runs beside
                // the base machine and owns its own clock - see CharacterRenderer.Update.
                if (!animationFrozen && remoteMovementChanged && !remoteMasked)
                {
                    _combatActions.Remove(e.Guid);
                    _combatActionRoute.Remove(e.Guid);
                }

                // Masked remote one-shot (someone else eating/drinking seated, or casting while
                // running): layer it onto the SpineLow subtree over the base pose instead of
                // hijacking the whole body. Resolve and expire the action HERE so the locomotion
                // and seated-loop branches below are free to be the base clip; it renders as a
                // torso overlay at Evaluate time.
                M2Animator.Clip? torsoOverlay = null;
                float torsoOverlayTime = 0f;
                if (remoteMasked &&
                    _combatActions.TryGetValue(e.Guid, out CombatAction maskedAction) &&
                    ResolveCombatClip(model.Animator, unit, maskedAction) is { } maskedActionClip)
                {
                    float maskedActionTime = frozen ? at : _globalTime - maskedAction.StartedAt;
                    if (!frozen && !maskedActionClip.Looping &&
                        maskedActionTime >= maskedActionClip.DurationSeconds)
                    {
                        _combatActions.Remove(e.Guid);   // one-shot done: plain base pose
                        _combatActionRoute.Remove(e.Guid);
                        remoteMasked = false;
                    }
                    else
                    {
                        torsoOverlay = maskedActionClip;
                        torsoOverlayTime = maskedActionTime;
                    }
                }

                if (e.IsDead)
                {
                    clip = model.Animator.Resolve(unit, ActionAnimationTrack, 1, true, 6, 0);
                    rate = 1f;
                    float deathAt = _deathTime.GetValueOrDefault(e.Guid, float.PositiveInfinity);
                    at = frozen ? at : float.IsPositiveInfinity(deathAt)
                        ? clip?.DurationSeconds ?? 0f
                        : MathF.Min(deathAt + dt, clip?.DurationSeconds ?? deathAt + dt);
                    _deathTime[e.Guid] = at;
                }
                // Both halves of the Benilla committed_lower rule are handled above
                // (torsoOverlay); !remoteMasked keeps this full-body branch for the case it is
                // still correct for - a unit standing still, lower body free. See
                // CharacterRenderer.ChooseClip (Benilla driver.rs:631-644/1137-1178).
                else if (!remoteMasked &&
                    _combatActions.TryGetValue(e.Guid, out CombatAction action) &&
                    ResolveCombatClip(model.Animator, unit, action) is { } actionClip)
                {
                    clip = actionClip;
                    rate = 1f;
                    float actionTime = frozen ? at : _globalTime - action.StartedAt;
                    // See CharacterRenderer.Update's matching comment: a looping
                    // clip (Dance) is a state, not a burst, and must keep cycling
                    // past its own DurationSeconds until something else clears it.
                    if (!frozen && !actionClip.Looping && actionTime >= actionClip.DurationSeconds)
                        _combatActions.Remove(e.Guid);
                    at = actionTime;
                }
                else if (_spellHolds.TryGetValue(e.Guid, out int heldAnimation) &&
                    model.Animator.Resolve(unit, SpellHoldAnimationTrack, heldAnimation, true) is { } holdClip)
                {
                    clip = holdClip;
                    rate = 1f;
                    if (!frozen) at += dt;
                }
                else if (mounted)
                {
                    // Seated. No locomotion cycle — the legs (or wheels) are the steed's
                    // job now, and 91 is the one pose vanilla plays on every mount.
                    clip = model.Animator.Resolve(unit, BaseAnimationTrack, RiderAnimationId, true, 0);
                    rate = 1f;
                    if (!frozen) at += dt;
                }
                else if (!remoteMoving && e.Fields.NpcEmoteState != 0 &&
                    EmoteAnimResolver?.Invoke(e.Fields.NpcEmoteState) is int remoteStateAnimId &&
                    remoteStateAnimId > 0 &&
                    model.Animator.Resolve(unit, BaseAnimationTrack, remoteStateAnimId, true, 0) is { } stateClip)
                {
                    // Someone else dancing - see CharacterRenderer.ChooseClip's matching
                    // comment for why this is UNIT_NPC_EMOTESTATE and not SMSG_EMOTE.
                    clip = stateClip;
                    rate = 1f;
                    if (!frozen) at += dt;
                }
                else if (!remoteMoving && SeatedLoopAnimId(e.Fields.StandState) is int seatedLoop && seatedLoop != 0)
                {
                    // Someone else sitting/kneeling/sleeping: just the held loop, no
                    // Down/Up transition polish - that lives on CharacterRenderer
                    // (the local player) only. See its ChooseClip comment for the
                    // real AnimationData ids and why chair-sitting is out of scope.
                    clip = model.Animator.Resolve(unit, BaseAnimationTrack, seatedLoop, true, 0);
                    rate = 1f;
                    if (!frozen) at += dt;
                }
                else
                {
                    if (!frozen) _combatActions.Remove(e.Guid);
                    clip = SelectClip(e, model.Animator, unit,
                        _lootKneeling.Contains(e.Guid), out rate);
                    if (!frozen) at += dt * rate;
                }
                // Tactical Freeze is stronger than a zero animation rate: retain the exact clip
                // identity and overlays sampled on the first frozen frame. A later field/spline
                // packet cannot make the body switch from run/swing/emote to stand while locked.
                if (tacticalFrozen)
                {
                    if (_tacticalFreezeVisuals.TryGetValue(e.Guid, out TacticalFreezeVisualLatch latch))
                    {
                        clip = latch.Clip;
                        at = latch.ClipTime;
                        torsoOverlay = latch.TorsoOverlay;
                        torsoOverlayTime = latch.TorsoOverlayTime;
                        evaluationGlobalTime = latch.EvaluationGlobalTime;
                    }
                    else
                    {
                        float freezeStartedAt = EnsureTacticalFreezeStartedAt(e.Guid);
                        _tacticalFreezeVisuals[e.Guid] = new TacticalFreezeVisualLatch(
                            freezeStartedAt, clip, at, torsoOverlay, torsoOverlayTime);
                        evaluationGlobalTime = freezeStartedAt;
                    }
                }
                if (float.IsNaN(at) || float.IsInfinity(at)) at = 0f;
                _animTime[e.Guid] = at;

                if (clip is not null)
                {
                    pickClip = clip;
                    if (!mounted && animationEventsElected && !frozen)
                        EmitFootstepEvents(e.Guid, e.DisplayId, e.Position,
                            e.Scale, model.Source, clip, at, mount: false);
                    boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
                    if (torsoOverlay is not null)
                        model.Animator.EvaluateWithArmOverlays(clip, at, null, 0f, 0f,
                            null, 0f, null, 0f, torsoOverlay, torsoOverlayTime,
                            evaluationGlobalTime, _skin,
                            torsoOverlayWeight: CharacterPoseLaw.OneshotOverlayWeight);
                    else
                        model.Animator.Evaluate(clip, at, evaluationGlobalTime, _skin);
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                    AnimatedLastFrame++;
                }
            }
            _shader.Set("uBoneCount", boneCount);

            Matrix4x4[] poseSkin = new Matrix4x4[model.Source.Bones.Count];
            IReadOnlyList<Matrix4x4> sourceSkin = boneCount > 0 ? _skin : _bindSkin;
            for (int poseIndex = 0; poseIndex < poseSkin.Length; poseIndex++)
                poseSkin[poseIndex] = poseIndex < sourceSkin.Count
                    ? sourceSkin[poseIndex] : Matrix4x4.Identity;
            _spellPoses[e.Guid] = new SpellUnitPose(true, e.Position, e.Orientation,
                worldModel, model.Source, poseSkin,
                GeosetFilter ? appearance.VisibleGeosets : null,
                pickClip?.BoundsCenter ?? Vector3.Zero,
                pickClip?.BoundsRadius ?? 0f);

            bool filter = GeosetFilter && appearance.VisibleGeosets is not null;
            _gl.BindVertexArray(model.Vao);
            _gl.Enable(EnableCap.CullFace);
            bool cullingOn = true;
            for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
            {
                DrawBatch b = model.Batches[batchIndex];
                if (filter && !appearance.VisibleGeosets!.Contains(b.GeosetId)) continue;

                ApplyBatchCulling(b, ref cullingOn);
                bool additive = b.Blend is 3 or 4;
                bool alphaKey = b.Blend == 1;
                if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
                else if (bodyTranslucent || b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
                else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
                _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0f);
                appearance.Textures[batchIndex]?.Bind(0);
                DrawElements(b.Start, b.Count);
            }
            if (!cullingOn) _gl.Enable(EnableCap.CullFace);
            if (e.IsPlayer) PlayersDrawnLastFrame++;
            else
            {
                DrawnLastFrame++;
                _lifecycle?.NoteFirstDraw(e.Guid);
            }

            if (CastsGroundShadow(e))
                _shadowCasters.Add(new UnitShadowCaster(e.Position, mounted
                    ? mount.GroundRadius
                    : GroundShadowRadius(model.HorizontalRadius, scale), respawnAlpha));

            DrawUnitAttachments(camera, e, model, info, m,
                boneCount > 0 ? _skin : _bindSkin, applyAuraVisual: true,
                alphaMultiplier: respawnAlpha);
            // The attachment path has its own shader; restore ours before the
            // next streamed unit uploads its model/bone uniforms.
            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(true);
            _shader.Use();
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);

        PublishMountCount();
        PruneAnimState();
    }

    public bool TryGetSpellPose(ulong guid, out SpellUnitPose pose)
        => _spellPoses.TryGetValue(guid, out pose);

    private bool TryResolveRenderInfo(WorldEntity entity, out CreatureModelInfo info)
    {
        info = default;
        if (_resolver is null || !_resolver.TryResolve(entity.DisplayId, out CreatureModelInfo model))
            return false;
        if (!entity.IsPlayer)
        {
            info = model;
            return true;
        }

        bool ok = TryBuildPlayerModelInfo(entity, model, PlayerItemResolver, out info);
        LogPlayerGearState(entity, ok, info);
        return ok;
    }

    /// <summary>Pure remote-player adapter used by the live renderer and wire regression checks.</summary>
    public static bool TryBuildPlayerModelInfo(WorldEntity entity, CreatureModelInfo model,
        Func<uint, (bool Settled, ItemTemplate? Item)>? itemResolver,
        out CreatureModelInfo info)
    {
        info = default;
        if (!entity.IsPlayer) return false;
        (byte race, _, byte sex, _) = entity.Fields.Bytes0;
        if (race is < 1 or > 8 || sex > 1) return false;
        (byte skin, byte face, byte hairStyle, byte hairColor) =
            entity.Fields.PlayerAppearance;

        // CreatureDisplayInfoExtra's ten equipment columns are head, shoulder, then the eight
        // composited body slots. Append cloak as an MSUI-only eleventh value; NPC rows remain ten.
        var equipment = new uint[11];
        (int VisibleSlot, int AppearanceSlot)[] slots =
        [
            (0, 0), (2, 1), (3, 2), (4, 3), (5, 4), (6, 5),
            (7, 6), (8, 7), (9, 8), (18, 9), (14, 10),
        ];
        foreach ((int visibleSlot, int appearanceSlot) in slots)
        {
            uint entry = entity.Fields.PlayerVisibleItemEntry(visibleSlot);
            if (entry == 0) continue;
            // A slot whose template has not settled yet is drawn empty rather than blocking
            // the whole player (invisible-until-settled, or forever if one query dies).
            // ExtEquipment feeds the appearance cache key, so the settle re-dresses for free.
            (bool settled, ItemTemplate? item) = itemResolver?.Invoke(entry) ?? (false, null);
            if (!settled) continue;
            equipment[appearanceSlot] = item?.DisplayInfoId ?? 0;
        }

        info = model with
        {
            HasExtended = true,
            ExtRace = race,
            ExtSex = sex,
            ExtSkin = skin,
            ExtFace = face,
            ExtHairStyle = hairStyle,
            ExtHairColor = hairColor,
            ExtFacialHair = entity.Fields.PlayerFacialHair,
            ExtEquipment = equipment,
            BakeName = "",
            IsPlayerAppearance = true,
        };
        return true;
    }

    /// <summary>
    /// One console line per remote player whenever their resolved-gear state changes —
    /// distinguishes zero visible-item entries, unsettled templates, settled-null templates,
    /// and display ids at a glance. Cheap: a hash compare per frame, string work only on change.
    /// </summary>
    private void LogPlayerGearState(WorldEntity entity, bool ok, in CreatureModelInfo info)
    {
        ulong signature = ok ? 1UL : 2UL;
        if (ok)
            foreach (uint display in info.ExtEquipment)
                signature = signature * 31 + display;
        for (int slot = 0; slot < 19; slot++)
            signature = signature * 31 + entity.Fields.PlayerVisibleItemEntry(slot);
        if (_playerGearLogState.TryGetValue(entity.Guid, out ulong previous) &&
            previous == signature) return;
        _playerGearLogState[entity.Guid] = signature;
        var parts = new List<string>();
        for (int slot = 0; slot < 19; slot++)
        {
            uint entry = entity.Fields.PlayerVisibleItemEntry(slot);
            if (entry == 0) continue;
            string state;
            if (PlayerItemResolver is null) state = "no-resolver";
            else
            {
                (bool settled, ItemTemplate? item) = PlayerItemResolver(entry);
                state = !settled ? "pending"
                    : item is null ? "null-template" : $"disp {item.DisplayInfoId}";
            }
            parts.Add($"{slot}:{entry}={state}");
        }
        Console.WriteLine($"[player-gear] 0x{entity.Guid:X} drawn={ok} " +
            (parts.Count == 0 ? "no visible items" : string.Join(" ", parts)));
    }

    private readonly Dictionary<ulong, ulong> _playerGearLogState = [];

    private int CompareUnitDistance(WorldEntity left, WorldEntity right) =>
        Vector3.DistanceSquared(left.Position, _sortCameraPosition)
            .CompareTo(Vector3.DistanceSquared(right.Position, _sortCameraPosition));

    private bool EligibleForLoad(WorldEntity entity, Vector3 cameraPosition, Vector3 cameraTarget,
        Matrix4x4 viewProjection)
    {
        CreatureLifecycleTracker? lifecycle = entity.IsCreature ? _lifecycle : null;
        float radius = MathF.Max(1f, UnitRenderScale(entity.Scale, ScaleMultiplier) * 2f);
        float distanceSq = Vector3.DistanceSquared(entity.Position, cameraPosition);
        if (distanceSq > AnimateDistance * AnimateDistance)
        {
            lifecycle?.NoteAdmission(entity.Guid, distanceSq,
                CreatureLifecycleTracker.AdmissionCode.OUT_OF_RADIUS,
                entity.Position, cameraPosition, cameraTarget);
            return false;
        }
        Vector3 relative = entity.Position - cameraPosition;
        bool visible = Camera.BoxInFrustum(viewProjection,
            relative - new Vector3(radius, radius, radius),
            relative + new Vector3(radius, radius, radius * 1.5f));
        lifecycle?.NoteAdmission(entity.Guid, distanceSq, visible
                ? CreatureLifecycleTracker.AdmissionCode.VISIBLE
                : CreatureLifecycleTracker.AdmissionCode.OUT_OF_FRUSTUM,
            entity.Position, cameraPosition, cameraTarget);
        return visible;
    }

    private bool AnimationEventsElected(WorldEntity entity, Vector3 cameraPosition,
        Matrix4x4 viewProjection)
    {
        float radius = AnimationEventElectionLaw.PaddedRadius(
            UnitRenderScale(entity.Scale, ScaleMultiplier));
        Vector3 relative = entity.Position - cameraPosition;
        bool visible = Camera.BoxInFrustum(viewProjection,
            relative - new Vector3(radius, radius, radius),
            relative + new Vector3(radius, radius, radius));

        const uint MoreAudible = 0x20;
        bool moreAudible = entity.IsCreature &&
            TypeFlagsFor?.Invoke(entity.Entry) is uint flags &&
            (flags & MoreAudible) != 0;
        float outOfViewSince = _animationEventOutOfViewSince.GetValueOrDefault(
            entity.Guid, -1f);
        bool elected = AnimationEventElectionLaw.IsElected(
            visible, moreAudible, _globalTime, ref outOfViewSince);
        if (outOfViewSince < 0f)
            _animationEventOutOfViewSince.Remove(entity.Guid);
        else
            _animationEventOutOfViewSince[entity.Guid] = outOfViewSince;
        return elected;
    }

    public void NoteKnownNotDrawn(EntityStore entities)
    {
        _shadowCasters.Clear();
        // Targeting consumes the last completed render pose. If this pass is skipped, there is
        // no visible body to click; never leave a stale prior-frame mesh in the pick set.
        _spellPoses.Clear();
        PublishMountCount();
        if (_lifecycle is null) return;
        foreach (WorldEntity entity in entities.Units)
            if (entity.IsCreature)
                _lifecycle.NoteReason(entity.Guid,
                    CreatureLifecycleTracker.ReasonCode.NOT_IN_WORLD);
    }

    private static bool CastsGroundShadow(WorldEntity entity)
    {
        if (entity.Flying || entity.Spline?.Flying == true) return false;
        const uint airborneOrSwimming =
            (uint)(MovementFlags.Falling | MovementFlags.Swimming);
        return (entity.MoveFlags & airborneOrSwimming) == 0;
    }

    private void ApplyAttachmentAtmosphere()
    {
        if (_attachedItems is null) return;
        _attachedItems.SunDirection = SunDirection;
        _attachedItems.SunColor = SunColor;
        _attachedItems.SunIntensity = SunIntensity;
        _attachedItems.AmbientColor = AmbientColor;
        _attachedItems.AmbientIntensity = AmbientIntensity;
        _attachedItems.FogColor = FogColor;
        _attachedItems.FogStart = FogStart;
        _attachedItems.FogEnd = FogEnd;
    }

    private void DrawUnitAttachments(Camera camera, WorldEntity entity, LoadedModel model,
        in CreatureModelInfo info, Matrix4x4 transform, Matrix4x4[] skin,
        bool applyAuraVisual = false, float alphaMultiplier = 1f)
    {
        if (model.Source.Attachments.Count == 0 || _attachedItems is null) return;
        uint head = info.HasExtended && info.ExtEquipment.Length > 0
            ? info.ExtEquipment[0] : 0;
        uint shoulders = info.HasExtended && info.ExtEquipment.Length > 1
            ? info.ExtEquipment[1] : 0;
        string suffix = RaceGenderCode(info.ExtRace, info.ExtSex);
        var equipment = new CharacterEquipment();
        string signature;
        if (entity.IsPlayer)
        {
            var parts = new List<string>(19);
            for (int slot = 0; slot < 19; slot++)
            {
                uint entry = entity.Fields.PlayerVisibleItemEntry(slot);
                if (entry == 0 || PlayerItemResolver is null) continue;
                (bool settled, ItemTemplate? item) = PlayerItemResolver(entry);
                if (!settled || item is null || item.DisplayInfoId == 0) continue;
                equipment.Add($"player visible slot {slot}", item.DisplayInfoId,
                    (int)item.InventoryType, slot, (byte)item.Class, (byte)item.Subclass,
                    (byte)item.Material, (byte)item.Sheath,
                    Enumerable.Range(0, 7)
                        .Select(enchantSlot => entity.Fields.PlayerVisibleItemEnchant(slot, enchantSlot))
                        .ToArray());
                string enchants = string.Join(',', Enumerable.Range(0, 7)
                    .Select(enchantSlot => entity.Fields.PlayerVisibleItemEnchant(slot, enchantSlot)));
                parts.Add($"{slot}:{item.DisplayInfoId}:{item.InventoryType}:{item.Sheath}:{enchants}");
            }
            signature = $"player:{suffix}:{string.Join('|', parts)}";
        }
        else
        {
            uint d0 = entity.Fields.VirtualItemDisplay(0);
            uint d1 = entity.Fields.VirtualItemDisplay(1);
            uint d2 = entity.Fields.VirtualItemDisplay(2);
            if ((head | shoulders | d0 | d1 | d2) == 0) return;
            if (head != 0)
                equipment.Add("NPC head", head, CharacterEquipment.Slot.Head);
            if (shoulders != 0)
                equipment.Add("NPC shoulders", shoulders, CharacterEquipment.Slot.Shoulders);
            AddVirtualPiece(equipment, entity.Fields, 0, d0);
            AddVirtualPiece(equipment, entity.Fields, 1, d1);
            AddVirtualPiece(equipment, entity.Fields, 2, d2);
            signature = $"npc:{head}:{shoulders}:{suffix}|" +
                $"{d0}:{entity.Fields.VirtualItemInfo(0)}:{entity.Fields.VirtualItemSheath(0)}|" +
                $"{d1}:{entity.Fields.VirtualItemInfo(1)}:{entity.Fields.VirtualItemSheath(1)}|" +
                $"{d2}:{entity.Fields.VirtualItemInfo(2)}:{entity.Fields.VirtualItemSheath(2)}";
        }
        if (equipment.Pieces.Count == 0) return;

        if (!_unitAttachments.TryGetValue(entity.Guid, out UnitAttachments? state))
        {
            state = new UnitAttachments();
            _unitAttachments[entity.Guid] = state;
        }
        if (state.Signature != signature)
        {
            equipment.Resolve(_itemDisplay);
            _attachedItems.RaceGenderCode = suffix;
            state.Mounts = _attachedItems.BuildMountSet(equipment);
            state.Signature = signature;
        }
        state.LastSeenAt = _globalTime;
        _attachedItems.BodyAlpha = applyAuraVisual
            ? entity.AuraVisual.Alpha * Math.Clamp(alphaMultiplier, 0f, 1f)
            : 1f;
        _attachedItems.BodyTint = applyAuraVisual ? entity.AuraVisual.Tint : Vector3.One;
        _attachedItems.GlowOwnerKey = $"unit:{entity.Guid:X16}";
        _attachedItems.Render(camera, transform, model.Source, skin, state.Mounts,
            entity.Fields.SheathState, entity.Guid, _globalTime);
    }

    public IReadOnlyList<ItemGlowPlacement> ItemGlowPlacements =>
        _attachedItems?.GlowPlacements ?? Array.Empty<ItemGlowPlacement>();
    public IReadOnlyList<CarriedLightPlacement> CarriedLightPlacements =>
        _attachedItems?.CarriedLights ?? Array.Empty<CarriedLightPlacement>();
    public IReadOnlyList<FishingPoleTipPlacement> FishingPoleTips =>
        _attachedItems?.FishingPoleTips ?? Array.Empty<FishingPoleTipPlacement>();
    public void BeginItemGlowFrame() => _attachedItems?.BeginGlowFrame();

    private static void AddVirtualPiece(CharacterEquipment equipment, ObjectFields fields,
        int heldSlot, uint display)
    {
        if (display == 0) return;
        var info = fields.VirtualItemInfo(heldSlot);
        int inventory = info.InventoryType;
        if (inventory == 0)
            inventory = heldSlot switch
            {
                0 => CharacterEquipment.Slot.MainHand,
                1 => CharacterEquipment.Slot.OffHand,
                _ => 15,
            };
        equipment.Add($"virtual weapon {heldSlot}", display, inventory, 15 + heldSlot,
            info.Class, info.Subclass, info.Material, fields.VirtualItemSheath(heldSlot));
    }

    public readonly record struct PortraitFraming(float EyeHeight, float Distance, float Height);

    public bool TryGetAuthoredPortrait(WorldEntity entity, out M2PortraitCamera camera,
        out Matrix4x4 modelTransform)
    {
        if (!TryGetModel(entity, out LoadedModel? model) || model?.PortraitCamera is not { } authored)
        {
            camera = default;
            modelTransform = default;
            return false;
        }

        float heading = entity.Orientation + HeadingOffsetDegrees * MathF.PI / 180f;
        modelTransform = Matrix4x4.CreateScale(UnitRenderScale(entity.Scale, ScaleMultiplier))
            * Matrix4x4.CreateRotationY(heading)
            * Basis
            * Matrix4x4.CreateTranslation(entity.Position);
        camera = authored;
        return true;
    }

    /// <summary>Loads the selected display if needed and derives a tight, model-space portrait camera.</summary>
    public bool TryGetPortraitFraming(WorldEntity entity, out PortraitFraming framing)
    {
        framing = default;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;

        float scale = UnitRenderScale(entity.Scale, ScaleMultiplier);
        float min = model.MinHeight * scale;
        float max = model.MaxHeight * scale;
        float height = MathF.Max(0.5f, max - min);
        float eye = min + height * 0.92f;
        float window = Math.Clamp(
            MathF.Max(0.34f * height, 0.9f * model.HorizontalRadius * scale),
            0.55f, 1.10f);
        const float fovyDegrees = 0.5f * 180f / MathF.PI;
        float distance = (window * 0.5f) /
            MathF.Tan(fovyDegrees * 0.5f * MathF.PI / 180f);
        framing = new PortraitFraming(eye, MathF.Max(0.8f, distance), height);
        return true;
    }

    public float SelectionRadius(WorldEntity entity)
    {
        // Riding: the ring goes around what is standing on the ground, not around the
        // rider's own waist, or a horse ends up wearing a bracelet.
        if (TryGetMountGroundRadius(entity.Guid, out float mountRadius)) return mountRadius;
        if (TryGetModel(entity, out LoadedModel? model) && model is not null)
            return MathF.Max(0.35f, model.HorizontalRadius * UnitRenderScale(entity.Scale, ScaleMultiplier));
        return 0.7f * UnitRenderScale(entity.Scale, ScaleMultiplier);
    }

    public bool TryGetOverheadHeight(WorldEntity entity, out float height)
    {
        height = 0f;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;
        float scale = UnitRenderScale(entity.Scale, ScaleMultiplier);
        height = MathF.Max(0.3f, model.MaxHeight * scale);

        // A rider's head is a saddle above its own feet, and it wears the steed's scale
        // through the seat. Measure from there or the name plants itself in the horse.
        if (_mountsDrawn.TryGetValue(entity.Guid, out MountDraw mount))
            height = MathF.Max(height,
                mount.SeatHeight + model.MaxHeight * MathF.Max(0.01f, entity.Scale) * mount.Scale);
        return true;
    }

    /// <summary>
    /// Build-5875's third-person camera pivot: attachment 17 Z + 0.0972 for character models,
    /// otherwise 90% of the vertex-box height, all in the unit's live render scale.
    /// </summary>
    public bool TryGetCameraPivotHeight(WorldEntity entity, out float height)
    {
        height = ViewSubjectLaw.PivotFallback;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;
        float? attachment17Z = model.Source.Attachments
            .FirstOrDefault(attachment => attachment.Id == 17)?.Position.Z;
        height = ViewSubjectLaw.PivotHeight(attachment17Z, model.MinHeight, model.MaxHeight,
            UnitRenderScale(entity.Scale, ScaleMultiplier));
        return true;
    }

    /// <summary>
    /// Return the static Stand-sequence CAaBox height used by the reference chat
    /// bubble, not the broader model bounds used by posed overhead names.
    /// </summary>
    public bool TryGetStandBoxHeight(WorldEntity entity, out float height)
    {
        height = 0f;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;
        M2Sequence? stand = model.Source.Sequences.FirstOrDefault(s =>
            s.AnimationId == 0 && s.VariationId == 0) ??
            model.Source.Sequences.FirstOrDefault(s => s.AnimationId == 0);
        float authored = stand?.BoundsZExtent ?? 0f;
        float modelHeight = MathF.Max(0f, model.MaxHeight - model.MinHeight);
        height = MathF.Max(authored, modelHeight) *
            UnitRenderScale(entity.Scale, ScaleMultiplier);
        return true;
    }

    /// <summary>
    /// Draw exactly one creature for an offscreen portrait. This deliberately
    /// does not advance, track, count, or prune world animation state.
    /// </summary>
    public bool RenderPortrait(Camera camera, WorldEntity entity, float standAnimationTime = 0f)
    {
        if (_shader is null || !TryGetModel(entity, out LoadedModel? model) || model is null ||
            !TryResolveRenderInfo(entity, out CreatureModelInfo info)) return false;
        string appearanceKey = AppearanceKey(info);
        if (!_appearanceCache.TryGetValue(appearanceKey, out Appearance? appearance))
        {
            _lifecycle?.NoteLoadEnqueued(entity.Guid, nameof(RenderPortrait));
            _lifecycle?.NoteModelLoading(entity.Guid);
            if (!TryAcquireAppearance(model, info, allowFinalize: true,
                    out appearance, out _)) return false;
        }
        _lifecycle?.NoteModelReady(entity.Guid, appearance is not null);
        if (appearance is null) return false;

        Vector3 camPos = camera.Position;
        float heading = entity.Orientation + HeadingOffsetDegrees * MathF.PI / 180f;
        Matrix4x4 transform = Matrix4x4.CreateScale(UnitRenderScale(entity.Scale, ScaleMultiplier))
            * Matrix4x4.CreateRotationY(heading)
            * Basis
            * Matrix4x4.CreateTranslation(entity.Position);
        transform.M41 -= camPos.X; transform.M42 -= camPos.Y; transform.M43 -= camPos.Z;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _shader.Use();
        _shader.Set("uViewProj", camera.RelativeViewProjection);
        _shader.Set("uModel", transform);
        _shader.Set("uSunDir", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uTex", 0);
        _shader.Set("uHighlight", 0f);
        _shader.Set("uBodyAlpha", 1f);
        _shader.Set("uBodyTint", Vector3.One);
        ApplyAttachmentAtmosphere();

        int boneCount = 0;
        if (model.Animator is not null && model.BoneCount > 0)
        {
            // Round portrait callers keep the default fresh Stand instance at t=0. Live
            // PlayerModel-style body panes supply their own clamped Stand-loop time.
            M2Animator.Clip? clip = model.Animator.FindOrBake(0);
            boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
            float animationTime = MathF.Max(0f, standAnimationTime);
            model.Animator.Evaluate(clip, animationTime, animationTime, _skin);
            M2Animator.Pack(_skin, boneCount, _packed);
            _shader.SetVec4Array("uBones", _packed, boneCount * 3);
        }
        _shader.Set("uBoneCount", boneCount);

        bool filter = GeosetFilter && appearance.VisibleGeosets is not null;
        _gl.BindVertexArray(model.Vao);
        _gl.Enable(EnableCap.CullFace);
        bool cullingOn = true;
        for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
        {
            DrawBatch batch = model.Batches[batchIndex];
            if (filter && !appearance.VisibleGeosets!.Contains(batch.GeosetId)) continue;
            ApplyBatchCulling(batch, ref cullingOn);
            bool additive = batch.Blend is 3 or 4;
            bool alphaKey = batch.Blend == 1;
            if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
            else if (batch.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
            else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
            _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0f);
            appearance.Textures[batchIndex]?.Bind(0);
            DrawElements(batch.Start, batch.Count);
        }
        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _lifecycle?.NoteFirstDraw(entity.Guid);
        DrawUnitAttachments(camera, entity, model, info, transform,
            boneCount > 0 ? _skin : _bindSkin);
        return true;
    }

    private bool TryGetModel(WorldEntity entity, out LoadedModel? model,
        [CallerMemberName] string caller = "")
    {
        model = null;
        if (!Ok || !Enabled || _resolver is null || !entity.IsUnit || entity.DisplayId <= 0 ||
            !TryResolveRenderInfo(entity, out CreatureModelInfo info))
        {
            if (entity.IsCreature)
                _lifecycle?.NoteReason(entity.Guid, entity.DisplayId <= 0
                    ? CreatureLifecycleTracker.ReasonCode.QUERY_PENDING
                    : CreatureLifecycleTracker.ReasonCode.RESOLVE_FAILED);
            return false;
        }
        _lifecycle?.NoteDisplayResolved(entity.Guid, entity.DisplayId, info.ModelPath);
        string key = info.ModelPath;
        if (!_modelCache.TryGetValue(key, out model))
        {
            bool firstEnqueue = _lifecycle?.NoteLoadEnqueued(entity.Guid, caller) == true;
            if (firstEnqueue)
                Console.WriteLine($"[creature-lifecycle] {entity.Guid:X16} enqueue {caller} {info.ModelPath}");
            _lifecycle?.NoteModelLoading(entity.Guid);
            // Non-render callers (portrait framing, selection proxy, nameplate
            // height) may request residency, but must never parse, upload or
            // adopt a model on their own timing path.
            _requestedModels.Add(key);
            return false;
        }
        else _lifecycle?.NoteModelReady(entity.Guid, model is not null);
        return model is not null;
    }

    private static M2Animator.Clip? SelectClip(
        WorldEntity e, M2Animator animator, string unit, bool lootKneeling, out float rate)
    {
        rate = 1f;
        float speed = e.Spline?.AverageSpeed ?? 0f;
        bool flying = e.Flying || e.Spline?.Flying == true;
        if (e.Spline is null || speed <= MovingEpsilon)
        {
            // Flying is durable actor state; the spline only says whether the body is travelling
            // right now. Prefer the model's authored Hover loop, then its generic Fly cycle. The
            // final fall/stand fallbacks keep models with a smaller animation vocabulary visible.
            if (flying)
                return animator.Resolve(unit, BaseAnimationTrack, 193, true, 135, 40, 0);

            if (lootKneeling)
                return animator.Resolve(unit, BaseAnimationTrack, 50, true, 0);

            int pose = StandStateUiLaw.LoopAnimation(e.Fields.UnitStandState);
            if (pose != 0)
                return animator.Resolve(unit, BaseAnimationTrack, pose, true, 0);

            return e.Engaged
                ? animator.Resolve(unit, BaseAnimationTrack, 25, true, 26, 27, 28, 0)
                : animator.Resolve(unit, BaseAnimationTrack, 0, true);
        }

        // A flying MonsterMove spline still has a perfectly ordinary world speed, but it
        // must not be fed into the ground Walk/Run chooser. Taxi gryphons generally author
        // animation 135 as their travelling wing cycle and 193 as hover; the fall and
        // ground clips are deliberately last-resort fallbacks for smaller animation sets.
        if (flying)
        {
            M2Animator.Clip? flight = animator.Resolve(
                unit, BaseAnimationTrack, 135, true, 193, 40, 5, 4, 0);
            if (flight is not null && flight.MoveSpeed > 0.01f)
                rate = Math.Clamp(speed / flight.MoveSpeed, 0.25f, 3f);
            return flight;
        }

        float walk = e.Speeds is { Length: > 0 } sp && sp[0] > 0f ? sp[0] : DefaultWalkSpeed;
        M2Animator.Clip? clip = speed > 2f * walk
            ? animator.Resolve(unit, BaseAnimationTrack, 5, true, 4, 0)
            : animator.Resolve(unit, BaseAnimationTrack, 4, true, 5, 0);

        if (clip is not null && clip.MoveSpeed > 0.01f)
            rate = Math.Clamp(speed / clip.MoveSpeed, 0.25f, 3f);
        return clip;
    }

    /// <summary>The loop-only AnimationData id for a StandState this renderer
    /// draws on remote units, or 0 if it's Stand/Dead/a chair variant/anything
    /// else not covered here. Mirrors CharacterRenderer.ChooseClip's Down/Loop/Up
    /// triplet's middle id - see there for the sourcing and the id table.</summary>
    private static int SeatedLoopAnimId(byte standState) => (UnitStandState)standState switch
    {
        UnitStandState.Sit => 97,
        UnitStandState.Sleep => 100,
        UnitStandState.Kneel => 115,
        _ => 0,
    };

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

    private void TrackLifeState(WorldEntity entity)
    {
        if (entity.IsDead)
        {
            bool witnessedAlive = _knownAlive.Remove(entity.Guid);
            if (_observedDead.Add(entity.Guid))
                _deathTime[entity.Guid] = witnessedAlive ? 0f : float.PositiveInfinity;
            if (entity.IsCreature)
                _deadCreatureSeenAt[entity.Guid] = _globalTime;
            _combatActions.Remove(entity.Guid);
            return;
        }

        bool resurrected = _observedDead.Remove(entity.Guid);
        if (entity.IsCreature && _deadCreatureSeenAt.Remove(entity.Guid))
            resurrected = true;
        _deathTime.Remove(entity.Guid);
        _knownAlive.Add(entity.Guid);
        if (resurrected)
        {
            _combatActions[entity.Guid] = new CombatAction(7, _globalTime, _globalTime + 3f);
            if (entity.IsCreature)
                _respawnFadeStartedAt[entity.Guid] = _globalTime;
        }
    }

    /// <summary>
    /// The build-5875 streamed-object appear curve: a two-second cubic ramp. We arm it only on a
    /// creature's witnessed dead-to-alive edge, so first sight, ordinary stream-in and remote
    /// players keep their established presentation.
    /// </summary>
    private float RespawnFadeAlpha(ulong guid)
    {
        if (!_respawnFadeStartedAt.TryGetValue(guid, out float started)) return 1f;
        float elapsed = _globalTime - started;
        if (elapsed >= CreatureRespawnFadeLaw.DurationSeconds)
        {
            _respawnFadeStartedAt.Remove(guid);
            return 1f;
        }
        return CreatureRespawnFadeLaw.Alpha(elapsed);
    }

    private static float InitialPhase(ulong guid) => (guid % 977) / 977f * 5f;

    /// <summary>
    /// Record membership independently of model resolution/visibility. A unit can freeze while
    /// offscreen or while its model is loading; its action clocks still need the whole interval,
    /// not merely the time since its first successful frozen draw.
    /// </summary>
    private void TrackTacticalFreezeIntervals()
    {
        foreach (ulong guid in TacticalFreezePoseLaw.FrozenGuids)
            _ = EnsureTacticalFreezeStartedAt(guid);
    }

    /// <summary>
    /// Return the renderer-frame time at which this GUID first entered the aggregate freeze set.
    /// The self-mount pass runs before the streamed-unit pass, so it may be the first renderer
    /// path to observe a newly frozen GUID and must be able to seed the same shared interval.
    /// </summary>
    private float EnsureTacticalFreezeStartedAt(ulong guid)
    {
        _tacticalFreezeStartedAt.TryAdd(guid, _globalTime);
        return _tacticalFreezeStartedAt[guid];
    }

    /// <summary>
    /// The first non-frozen render frame rebases wall-clock-authored visual timestamps by the
    /// complete authoritative membership interval. Clip-local clocks were never advanced; this
    /// keeps a swing/emote from expiring in PruneAnimState when the last overlapping lock thaws.
    /// </summary>
    private void ReconcileTacticalFreezeThaws()
    {
        if (_tacticalFreezeStartedAt.Count == 0) return;
        foreach ((ulong guid, float frozenAt) in _tacticalFreezeStartedAt.Where(pair =>
                     !TacticalFreezePoseLaw.IsFrozen(pair.Key)).ToArray())
        {
            float paused = MathF.Max(0f, _globalTime - frozenAt);
            if (_combatActions.TryGetValue(guid, out CombatAction action))
                _combatActions[guid] = action with
                {
                    StartedAt = action.StartedAt + paused,
                    ExpiresAt = action.ExpiresAt + paused,
                };
            if (_respawnFadeStartedAt.TryGetValue(guid, out float respawnAt))
                _respawnFadeStartedAt[guid] = respawnAt + paused;
            _tacticalFreezeStartedAt.Remove(guid);
            _tacticalFreezeVisuals.Remove(guid);
        }
    }

    private void PruneAnimState()
    {
        _stale.Clear();
        foreach (var k in _animTime.Keys)
            if (!_seen.Contains(k) && !TacticalFreezePoseLaw.IsFrozen(k)) _stale.Add(k);
        foreach (var pair in _combatActions)
            if ((!_seen.Contains(pair.Key) || pair.Value.ExpiresAt <= _globalTime) &&
                !TacticalFreezePoseLaw.IsFrozen(pair.Key))
                if (!_stale.Contains(pair.Key)) _stale.Add(pair.Key);
        foreach (ulong guid in _animationEventOutOfViewSince.Keys)
            if (!_seen.Contains(guid) && !TacticalFreezePoseLaw.IsFrozen(guid) &&
                !_stale.Contains(guid)) _stale.Add(guid);
        foreach (var k in _stale)
        {
            _animTime.Remove(k);
            _combatActions.Remove(k);
            _spellHolds.Remove(k);
            _lootKneeling.Remove(k);
            _knownAlive.Remove(k);
            _observedDead.Remove(k);
            _deathTime.Remove(k);
            _respawnFadeStartedAt.Remove(k);
            _footstepTime.Remove(k);
            _animationEventOutOfViewSince.Remove(k);
            _wasMovingByGuid.Remove(k);
            _combatActionRoute.Remove(k);
            _tacticalFreezeStartedAt.Remove(k);
            _tacticalFreezeVisuals.Remove(k);
        }

        // A corpse commonly streams out before its spawn comes alive again. Keep only the
        // dead-creature fact across that gap; all animation/model state above remains volatile.
        // The bounded retention prevents a same-GUID sighting much later from being misread.
        _stale.Clear();
        foreach (var pair in _deadCreatureSeenAt)
            if (_globalTime - pair.Value >= DeadCreatureRetentionSeconds)
                _stale.Add(pair.Key);
        foreach (ulong guid in _stale) _deadCreatureSeenAt.Remove(guid);

        // A steed whose rider stopped drawing (left the world, walked out of range)
        // must not keep supplying a saddle to the nameplate and selection-ring
        // queries, which run outside the loop and would place them at a stale spot.
        _stale.Clear();
        foreach (var pair in _mountsDrawn)
            if (_globalTime - pair.Value.LastSeenAt >= 1f &&
                !TacticalFreezePoseLaw.IsFrozen(pair.Key)) _stale.Add(pair.Key);
        foreach (ulong guid in _stale) ForgetMount(guid);

        // Visibility can fluctuate at the frustum edge and streamed units can
        // briefly leave the entity set. Keep lightweight mount sets warm long
        // enough to avoid rebuilding them on the next frame/packet episode;
        // GPU models and the shader belong to the one shared renderer.
        _stale.Clear();
        foreach (var pair in _unitAttachments)
            if (_globalTime - pair.Value.LastSeenAt >= 30f) _stale.Add(pair.Key);
        foreach (ulong guid in _stale) _unitAttachments.Remove(guid);
    }

    private static string AppearanceKey(in CreatureModelInfo info) =>
        info.IsPlayerAppearance
            ? $"{info.ModelPath}|player:{info.ExtRace}/{info.ExtSex}/{info.ExtSkin}/{info.ExtFace}/" +
              $"{info.ExtHairStyle}/{info.ExtHairColor}/{info.ExtFacialHair}/" +
              $"{string.Join('.', info.ExtEquipment)}"
            : info.HasExtended
                ? $"{info.ModelPath}|npc:{info.ExtRace}/{info.ExtSex}/{info.ExtSkin}/{info.ExtHairStyle}/{info.ExtFacialHair}/{info.BakeName}/{string.Join('.', info.ExtEquipment)}"
                : $"{info.ModelPath}|{string.Join(",", info.Textures)}";

    // Build EquipGeosets from the shared appearance layout. NPC rows carry ten display ids;
    // streamed players append cloak as an eleventh value after resolving their public items.
    private EquipGeosets? BuildNpcEquip(in CreatureModelInfo info)
    {
        if (_itemDisplay is null || info.ExtEquipment.Length < 10) return null;
        var eq = info.ExtEquipment;   // [head, shoulder, shirt, chest, belt, pants, boots, wrist, gloves, tabard]
        var e = new EquipGeosets();
        for (int i = 0; i < 8; i++)   // bodyslots = shirt..tabard = eq[2..9]
        {
            uint disp = eq[2 + i];
            e.Bodyslots[i] = disp != 0 ? _itemDisplay.Find(disp) : null;
        }
        if (eq[0] != 0 && _itemDisplay.Find(eq[0]) is { } head)   // helm hides hair
            e.HelmVis = (head.HelmetGeosetVis1, head.HelmetGeosetVis2);
        if (eq.Length > 10 && eq[10] != 0 && _itemDisplay.Find(eq[10]) is { } cloak)
        {
            e.HasCloak = cloak.ModelTexture1.Length > 0 || cloak.ModelTexture2.Length > 0;
            e.CloakGroup = cloak.GeosetGroup[0];
        }
        return e;
    }

    private bool TryAcquireModel(in CreatureModelInfo info, bool allowFinalize,
        out LoadedModel? model, out bool finalized)
    {
        finalized = false;
        if (_modelCache.TryGetValue(info.ModelPath, out model)) return true;

        // Portrait-batch construction intentionally has no streaming workers.
        if (_workers is null || _uploads is null)
        {
            model = LoadModelMeasured(info);
            _modelCache[info.ModelPath] = model;
            finalized = true;
            return true;
        }

        if (!_modelJobs.TryGetValue(info.ModelPath, out ModelLoadJob? job))
        {
            string path = info.ModelPath;
            _requestedModels.Remove(path);
            job = new ModelLoadJob { Path = path, Worker = _workers.Run(() => PrepareModel(path)) };
            _modelJobs[path] = job;
            model = null;
            return false;
        }
        if (!job.Worker.IsCompleted)
        {
            model = null;
            return false;
        }

        if (job.Ready is null)
        {
            try { job.Ready = job.Worker.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                Console.WriteLine($"[creature-prepare] {job.Path} failed: {exception.Message}");
                _modelCache[job.Path] = null;
                _modelJobs.Remove(job.Path);
                model = null;
                return true;
            }
            if (job.Ready is null)
            {
                _modelCache[job.Path] = null;
                _modelJobs.Remove(job.Path);
                model = null;
                return true;
            }
        }

        if (job.Upload is null)
        {
            PreparedModel ready = job.Ready;
            job.Upload = _uploads.Enqueue(Path.GetFileName(job.Path),
                uploadGl => UploadPreparedModel(uploadGl, ready));
            model = null;
            return false;
        }
        if (!job.Upload.IsCompleted || !allowFinalize)
        {
            model = null;
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        LoadsThisFrame++;
        try
        {
            UploadedBuffers uploaded = job.Upload.GetAwaiter().GetResult();
            model = FinalizePreparedModel(job.Ready, uploaded);
            _modelCache[job.Path] = model;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[creature-upload] {job.Path} failed: {exception.Message}");
            model = null;
            _modelCache[job.Path] = null;
        }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _modelJobs.Remove(job.Path);
        }
        finalized = true;
        return true;
    }

    private PreparedModel? PrepareModel(string path)
    {
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        M2Model? m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid) return null;

        var prepared = new PreparedModel
        {
            Source = m2,
            Animator = M2Animator.Build(m2, Array.Empty<int>()),
            Vertices = new float[m2.Vertices.Count * FloatsPerVertex],
            Indices = m2.Indices.ToArray(),
        };
        float minHeight = float.PositiveInfinity;
        float maxHeight = float.NegativeInfinity;
        float horizontalRadius = 0f;
        for (int i = 0; i < m2.Vertices.Count; i++)
        {
            var vertex = m2.Vertices[i];
            int offset = i * FloatsPerVertex;
            prepared.Vertices[offset] = vertex.PosX;
            prepared.Vertices[offset + 1] = vertex.PosY;
            prepared.Vertices[offset + 2] = vertex.PosZ;
            prepared.Vertices[offset + 3] = vertex.NormX;
            prepared.Vertices[offset + 4] = vertex.NormY;
            prepared.Vertices[offset + 5] = vertex.NormZ;
            prepared.Vertices[offset + 6] = vertex.TexU;
            prepared.Vertices[offset + 7] = vertex.TexV;

            float total = vertex.BoneWeight0 + vertex.BoneWeight1 +
                vertex.BoneWeight2 + vertex.BoneWeight3;
            if (total <= 0f)
            {
                prepared.Vertices[offset + 8] = 1f;
            }
            else
            {
                prepared.Vertices[offset + 8] = vertex.BoneWeight0 / total;
                prepared.Vertices[offset + 9] = vertex.BoneWeight1 / total;
                prepared.Vertices[offset + 10] = vertex.BoneWeight2 / total;
                prepared.Vertices[offset + 11] = vertex.BoneWeight3 / total;
                prepared.Vertices[offset + 12] = ClampBone(vertex.BoneIndex0);
                prepared.Vertices[offset + 13] = ClampBone(vertex.BoneIndex1);
                prepared.Vertices[offset + 14] = ClampBone(vertex.BoneIndex2);
                prepared.Vertices[offset + 15] = ClampBone(vertex.BoneIndex3);
            }

            Vector3 basis = Vector3.Transform(
                new Vector3(vertex.PosX, vertex.PosY, vertex.PosZ), Basis);
            minHeight = MathF.Min(minHeight, basis.Z);
            maxHeight = MathF.Max(maxHeight, basis.Z);
            horizontalRadius = MathF.Max(horizontalRadius,
                MathF.Sqrt(basis.X * basis.X + basis.Y * basis.Y));
        }
        prepared.MinHeight = float.IsFinite(minHeight) ? minHeight : 0f;
        prepared.MaxHeight = float.IsFinite(maxHeight) ? maxHeight : 2f;
        prepared.HorizontalRadius = MathF.Max(0.25f, horizontalRadius);
        return prepared;
    }

    private static unsafe UploadedBuffers UploadPreparedModel(GL gl, PreparedModel prepared)
    {
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* vertices = prepared.Vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(prepared.Vertices.Length * sizeof(float)), vertices,
                BufferUsageARB.StaticDraw);
        uint ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* indices = prepared.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(prepared.Indices.Length * sizeof(ushort)), indices,
                BufferUsageARB.StaticDraw);
        return new UploadedBuffers(vbo, ebo);
    }

    private unsafe LoadedModel FinalizePreparedModel(
        PreparedModel prepared, UploadedBuffers uploaded)
    {
        M2Model m2 = prepared.Source!;
        var model = new LoadedModel
        {
            Source = m2,
            Animator = prepared.Animator,
            BoneCount = prepared.Animator is { BoneCount: <= M2Animator.MaxBones }
                ? prepared.Animator.BoneCount : 0,
            PortraitCamera = m2.PortraitCamera,
            MinHeight = prepared.MinHeight,
            MaxHeight = prepared.MaxHeight,
            HorizontalRadius = prepared.HorizontalRadius,
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

        foreach (var batch in m2.Batches)
        {
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            var submesh = m2.Submeshes[batch.SubmeshIndex];
            int blend = batch.MaterialIndex < m2.RenderFlags.Count
                ? m2.RenderFlags[batch.MaterialIndex].BlendingMode : 0;
            bool twoSided = batch.MaterialIndex < m2.RenderFlags.Count &&
                m2.RenderFlags[batch.MaterialIndex].TwoSided;
            model.Batches.Add(new DrawBatch
            {
                Start = submesh.IndexStart,
                Count = submesh.IndexCount,
                Blend = blend,
                GeosetId = submesh.Id,
                TwoSided = twoSided,
            });
        }
        return model;
    }

    private bool TryAcquireAppearance(LoadedModel model, in CreatureModelInfo info,
        bool allowFinalize, out Appearance? appearance, out bool finalized)
    {
        string key = AppearanceKey(info);
        finalized = false;
        if (_appearanceCache.TryGetValue(key, out appearance)) return true;
        if (_workers is null || _uploads is null)
        {
            appearance = BuildAppearanceMeasured(model, info);
            _appearanceCache[key] = appearance;
            finalized = true;
            return true;
        }

        if (!_appearanceJobs.TryGetValue(key, out AppearanceLoadJob? job))
        {
            CreatureModelInfo captured = info;
            job = new AppearanceLoadJob
            {
                Worker = _workers.Run<PreparedAppearance?>(() => PrepareAppearance(model.Source, captured)),
            };
            _appearanceJobs[key] = job;
            appearance = null;
            return false;
        }
        if (!job.Worker.IsCompleted)
        {
            appearance = null;
            return false;
        }
        if (job.Ready is null)
        {
            try { job.Ready = job.Worker.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                Console.WriteLine($"[creature-appearance] {info.ModelPath} failed: {exception.Message}");
                _appearanceCache[key] = null;
                _appearanceJobs.Remove(key);
                appearance = null;
                return true;
            }
            if (job.Ready is null)
            {
                _appearanceCache[key] = null;
                _appearanceJobs.Remove(key);
                appearance = null;
                return true;
            }
        }
        if (job.Upload is null)
        {
            PreparedAppearance ready = job.Ready;
            PreparedTexture[] pending = ready.Textures
                .Where(texture => texture is not null && !_texCache.ContainsKey(texture.Path))
                .Select(texture => texture!)
                .DistinctBy(texture => texture.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            job.Upload = _uploads.Enqueue(Path.GetFileName(info.ModelPath), uploadGl =>
            {
                var textures = new Dictionary<string, Texture?>(StringComparer.OrdinalIgnoreCase);
                foreach (PreparedTexture texture in pending)
                    textures[texture.Path] = texture.Bgra is null ? null : Texture.From2D(
                        uploadGl, texture.Bgra, texture.Width, texture.Height,
                        mipmaps: true, repeat: true, ownerGl: _gl);
                return textures;
            });
            appearance = null;
            return false;
        }
        if (!job.Upload.IsCompleted || !allowFinalize)
        {
            appearance = null;
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        LoadsThisFrame++;
        try
        {
            foreach (var pair in job.Upload.GetAwaiter().GetResult())
                _texCache.TryAdd(pair.Key, pair.Value);
            appearance = new Appearance { VisibleGeosets = job.Ready.VisibleGeosets };
            foreach (PreparedTexture? texture in job.Ready.Textures)
                appearance.Textures.Add(texture is not null &&
                    _texCache.TryGetValue(texture.Path, out Texture? loaded) ? loaded : null);
            _appearanceCache[key] = appearance;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[creature-appearance-upload] {info.ModelPath} failed: {exception.Message}");
            appearance = null;
            _appearanceCache[key] = null;
        }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _appearanceJobs.Remove(key);
        }
        finalized = true;
        return true;
    }

    private PreparedAppearance PrepareAppearance(M2Model m2, CreatureModelInfo info)
    {
        var prepared = new PreparedAppearance();
        if (info.HasExtended && _geosets is not null)
        {
            EquipGeosets? equipment = BuildNpcEquip(info);
            HashSet<int> visible = _geosets.Visible(
                info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, equipment);
            prepared.VisibleGeosets = m2.Submeshes.Any(submesh => visible.Contains(submesh.Id))
                ? visible : null;
        }

        string path = info.ModelPath;
        string modelDir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
        PreparedTexture? bareHead = PrepareNpcBareComposite(info);
        PreparedTexture? carriedTexture = null;
        foreach (var batch in m2.Batches)
        {
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            int geosetId = m2.Submeshes[batch.SubmeshIndex].Id;
            PreparedTexture? preparedTexture = null;
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                int textureIndex = m2.TextureLookup[batch.TextureIndex];
                if (textureIndex >= 0 && textureIndex < m2.Textures.Count)
                {
                    M2TextureRef reference = m2.Textures[textureIndex];
                    bool liveCharacterComposite = info.IsPlayerAppearance;
                    if (bareHead is not null &&
                        (liveCharacterComposite ? reference.Type == 1
                            : IsNpcBareHeadBatch(reference.Type, geosetId)))
                    {
                        preparedTexture = bareHead;
                        prepared.Textures.Add(preparedTexture);
                        continue;
                    }
                    IReadOnlyList<string> candidates = ResolveBatchTexture(
                        reference.Type, reference.Filename, modelDir, info);
                    foreach (string candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        byte[]? bytes = _mpq.ReadFile(candidate);
                        if (bytes is null) continue;
                        try
                        {
                            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
                            preparedTexture = new PreparedTexture
                            {
                                Path = candidate,
                                Bgra = bgra,
                                Width = width,
                                Height = height,
                            };
                            break;
                        }
                        catch { }
                    }
                }
            }
            if (preparedTexture is not null) carriedTexture = preparedTexture;
            else preparedTexture = carriedTexture;
            prepared.Textures.Add(preparedTexture);
        }
        return prepared;
    }

    private unsafe LoadedModel? LoadModel(in CreatureModelInfo info)
    {
        string path = info.ModelPath;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { Console.WriteLine($"[creature] model '{path}' not in MPQ"); return null; }
            M2Model? m2 = M2Reader.Parse(bytes);
            if (m2 is null || !m2.IsValid) return null;

            var lm = new LoadedModel { PortraitCamera = m2.PortraitCamera, Source = m2 };

            var animator = M2Animator.Build(m2, Array.Empty<int>());
            if (animator is not null && animator.BoneCount <= M2Animator.MaxBones)
            {
                animator.ResolutionSink = (unit, track, resolution) =>
                    AnimationResolved?.Invoke(unit, track, resolution);
                lm.Animator = animator;
                lm.BoneCount = animator.BoneCount;
            }

            // Geoset visibility for humanoid NPCs (character models). Beasts stay unfiltered.
            if (info.HasExtended && _geosets is not null)
            {
                var eq = BuildNpcEquip(info);
                var vis = _geosets.Visible(info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, eq);
                // Fail-safe: if the computed set matches no submesh, don't hide the whole NPC.
                int match = 0;
                foreach (var sm in m2.Submeshes) if (vis.Contains(sm.Id)) match++;
                lm.VisibleGeosets = match > 0 ? vis : null;
                if (match == 0)
                    Console.WriteLine($"[creature] {path}: geoset set matched 0 submeshes — drawing all (check DBC layout)");
            }

            var verts = new float[m2.Vertices.Count * FloatsPerVertex];
            float minHeight = float.PositiveInfinity, maxHeight = float.NegativeInfinity;
            float horizontalRadius = 0f;
            for (int i = 0; i < m2.Vertices.Count; i++)
            {
                var v = m2.Vertices[i]; int o = i * FloatsPerVertex;
                verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
                verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
                verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;

                float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
                if (total <= 0f)
                {
                    verts[o + 8] = 1f; verts[o + 9] = 0f; verts[o + 10] = 0f; verts[o + 11] = 0f;
                    verts[o + 12] = 0f; verts[o + 13] = 0f; verts[o + 14] = 0f; verts[o + 15] = 0f;
                }
                else
                {
                    verts[o + 8] = v.BoneWeight0 / total; verts[o + 9] = v.BoneWeight1 / total;
                    verts[o + 10] = v.BoneWeight2 / total; verts[o + 11] = v.BoneWeight3 / total;
                    verts[o + 12] = ClampBone(v.BoneIndex0); verts[o + 13] = ClampBone(v.BoneIndex1);
                    verts[o + 14] = ClampBone(v.BoneIndex2); verts[o + 15] = ClampBone(v.BoneIndex3);
                }

                Vector3 worldBasis = Vector3.Transform(new Vector3(v.PosX, v.PosY, v.PosZ), Basis);
                minHeight = MathF.Min(minHeight, worldBasis.Z);
                maxHeight = MathF.Max(maxHeight, worldBasis.Z);
                horizontalRadius = MathF.Max(horizontalRadius,
                    MathF.Sqrt(worldBasis.X * worldBasis.X + worldBasis.Y * worldBasis.Y));
            }
            lm.MinHeight = float.IsFinite(minHeight) ? minHeight : 0f;
            lm.MaxHeight = float.IsFinite(maxHeight) ? maxHeight : 2f;
            lm.HorizontalRadius = MathF.Max(0.25f, horizontalRadius);
            ushort[] idx = m2.Indices.ToArray();

            lm.Vao = _gl.GenVertexArray(); _gl.BindVertexArray(lm.Vao);
            lm.Vbo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ArrayBuffer, lm.Vbo);
            fixed (float* p = verts) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            lm.Ebo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lm.Ebo);
            fixed (ushort* p = idx) _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
            int stride = FloatsPerVertex * sizeof(float);
            _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(8 * sizeof(float)));
            _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(12 * sizeof(float)));
            _gl.BindVertexArray(0);

            string modelDir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
            int textured = 0;
            string firstTex = "NONE";
            foreach (var b in m2.Batches)
            {
                if (b.SubmeshIndex >= m2.Submeshes.Count) continue;
                var sm = m2.Submeshes[b.SubmeshIndex];

                Texture? tex = null;
                if (b.TextureIndex < m2.TextureLookup.Count)
                {
                    int t = m2.TextureLookup[b.TextureIndex];
                    if (t >= 0 && t < m2.Textures.Count)
                    {
                        var candidates = ResolveBatchTexture(m2.Textures[t].Type, m2.Textures[t].Filename, modelDir, info);
                        tex = LoadTexture(candidates, out string hit);
                        if (tex is not null) { textured++; if (firstTex == "NONE") firstTex = hit; }
                    }
                }

                int blend = b.MaterialIndex < m2.RenderFlags.Count
                    ? m2.RenderFlags[b.MaterialIndex].BlendingMode : 0;
                bool twoSided = b.MaterialIndex < m2.RenderFlags.Count &&
                    m2.RenderFlags[b.MaterialIndex].TwoSided;
                lm.Batches.Add(new DrawBatch
                {
                    Start = sm.IndexStart,
                    Count = sm.IndexCount,
                    Tex = tex,
                    Blend = blend,
                    GeosetId = sm.Id,
                    TwoSided = twoSided,
                });
            }

            if (_diagLogged < 30)
            {
                _diagLogged++;
                int vis = lm.VisibleGeosets?.Count ?? -1;
                Console.WriteLine($"[creature] {path} ext={info.HasExtended} bones={lm.BoneCount} " +
                                  $"clips={lm.Animator?.Clips.Count ?? 0} batches={lm.Batches.Count} " +
                                  $"textured={textured}/{lm.Batches.Count} visgeosets={vis} first=[{firstTex}]");
            }
            return lm;
        }
        catch (Exception e) { Console.WriteLine($"[creature] model '{path}' failed: {e.Message}"); return null; }
    }

    private static float ClampBone(byte index) => index < M2Animator.MaxBones ? index : 0f;

    private Appearance? BuildAppearance(LoadedModel model, in CreatureModelInfo info)
    {
        try
        {
            var appearance = new Appearance();
            M2Model m2 = model.Source;
            string path = info.ModelPath;
            string modelDir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
            PreparedTexture? bareHead = PrepareNpcBareComposite(info);
            Texture? bareHeadTexture = null;
            if (bareHead is not null)
            {
                if (!_texCache.TryGetValue(bareHead.Path, out bareHeadTexture))
                {
                    bareHeadTexture = Texture.From2D(_gl, bareHead.Bgra!,
                        bareHead.Width, bareHead.Height, mipmaps: true, repeat: true);
                    _texCache[bareHead.Path] = bareHeadTexture;
                }
            }

            if (info.HasExtended && _geosets is not null)
            {
                var eq = BuildNpcEquip(info);
                var visible = _geosets.Visible(
                    info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, eq);
                int matches = 0;
                foreach (var submesh in m2.Submeshes)
                    if (visible.Contains(submesh.Id)) matches++;
                appearance.VisibleGeosets = matches > 0 ? visible : null;
            }

            Texture? carriedTexture = null;
            foreach (var batch in m2.Batches)
            {
                if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
                Texture? texture = null;
                if (batch.TextureIndex < m2.TextureLookup.Count)
                {
                    int textureIndex = m2.TextureLookup[batch.TextureIndex];
                    if (textureIndex >= 0 && textureIndex < m2.Textures.Count)
                    {
                        M2TextureRef reference = m2.Textures[textureIndex];
                        bool liveCharacterComposite = info.IsPlayerAppearance;
                        if (bareHeadTexture is not null &&
                            (liveCharacterComposite ? reference.Type == 1
                                : IsNpcBareHeadBatch(reference.Type,
                                    m2.Submeshes[batch.SubmeshIndex].Id)))
                        {
                            texture = bareHeadTexture;
                        }
                        else
                        {
                            var candidates = ResolveBatchTexture(
                                reference.Type, reference.Filename, modelDir, info);
                            texture = LoadTexture(candidates, out _);
                            if (texture is not null) carriedTexture = texture;
                            else texture = carriedTexture;
                        }
                    }
                }
                appearance.Textures.Add(texture);
            }
            return appearance;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[creature] appearance '{info.ModelPath}' failed: {exception.Message}");
            return null;
        }
    }

    // ── texture resolution ────────────────────────────────────────────────────────

    private IReadOnlyList<string> ResolveBatchTexture(uint type, string embedded,
        string modelDir, in CreatureModelInfo info)
    {
        if (!string.IsNullOrEmpty(embedded)) return new[] { embedded };

        switch (type)
        {
            case 11: case 12: case 13:
            {
                int slot = (int)type - 11;
                string name = slot < info.Textures.Length && !string.IsNullOrEmpty(info.Textures[slot])
                    ? info.Textures[slot]
                    : (info.Textures.Length > 0 ? info.Textures[0] : "");
                if (string.IsNullOrEmpty(name)) return Array.Empty<string>();
                return new[] { UnderDir(modelDir, name) };
            }
            case 1:
                return NpcBodySkinCandidates(info);
            case 2 when info.IsPlayerAppearance:
                return CharacterCapeTextureCandidates(info);
            case 6:
                return NpcHairTextureCandidates(info);
            case 7 when info.IsPlayerAppearance:
                return NpcFacialHairTextureCandidates(info);
            default:
                if (info.Textures.Length > 0 && !string.IsNullOrEmpty(info.Textures[0]))
                    return new[] { UnderDir(modelDir, info.Textures[0]) };
                return Array.Empty<string>();
        }
    }

    private static string UnderDir(string dir, string stem) =>
        dir.Length > 0 ? dir + "\\" + stem + ".blp" : stem + ".blp";

    private static IReadOnlyList<string> NpcBodySkinCandidates(in CreatureModelInfo info)
    {
        if (!info.HasExtended) return Array.Empty<string>();
        var list = new List<string>(3);

        if (!string.IsNullOrEmpty(info.BakeName))
        {
            string bake = info.BakeName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? info.BakeName : info.BakeName + ".blp";
            list.Add(bake.Contains('\\') ? bake : "Textures\\BakedNpcTextures\\" + bake);
        }

        string race = RaceFolder(info.ExtRace);
        string gender = info.ExtSex == 1 ? "Female" : "Male";
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin{(int)info.ExtSkin:00}_00.blp");
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin00_00.blp");
        return list;
    }

    private IReadOnlyList<string> NpcHairTextureCandidates(in CreatureModelInfo info)
    {
        if (!info.HasExtended || _charSections is null) return Array.Empty<string>();
        CharSectionRow? row = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionHair,
            info.ExtHairStyle, (int)info.ExtHairColor);
        if (row is null || row.Texture1.Length == 0)
            row = _charSections.Find(
                info.ExtRace, info.ExtSex, CharSectionsTable.SectionHair,
                1, (int)info.ExtHairColor);
        if (row is null || row.Texture1.Length == 0) return Array.Empty<string>();
        return CharacterTextureCandidates(row.Texture1, info.ExtRace, info.ExtSex).ToArray();
    }

    private IReadOnlyList<string> NpcFacialHairTextureCandidates(in CreatureModelInfo info)
    {
        if (!info.HasExtended || _charSections is null) return Array.Empty<string>();
        CharSectionRow? row = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionFacialHair,
            info.ExtFacialHair, (int)info.ExtHairColor);
        return row is null || row.Texture1.Length == 0
            ? Array.Empty<string>()
            : CharacterTextureCandidates(row.Texture1, info.ExtRace, info.ExtSex).ToArray();
    }

    private IReadOnlyList<string> CharacterCapeTextureCandidates(in CreatureModelInfo info)
    {
        if (_itemDisplay is null || info.ExtEquipment.Length <= 10 ||
            _itemDisplay.Find(info.ExtEquipment[10]) is not { } cloak)
            return Array.Empty<string>();
        string suffix = info.ExtSex == 1 ? "F" : "M";
        var result = new List<string>();
        foreach (string partial in new[] { cloak.ModelTexture1, cloak.ModelTexture2 }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string stem = partial.Replace('/', '\\').TrimStart('\\');
            bool hasDirectory = stem.Contains('\\');
            if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            if (hasDirectory) result.Add(stem + ".blp");
            else
            {
                result.Add($@"Item\ObjectComponents\Cape\{stem}.blp");
                result.Add($@"Item\TextureComponents\Cape\{stem}.blp");
                result.Add($@"Item\ObjectComponents\Cape\{stem}_{suffix}.blp");
                result.Add($@"Item\ObjectComponents\Cape\{stem}_U.blp");
                result.Add($@"Item\TextureComponents\Cape\{stem}_{suffix}.blp");
                result.Add($@"Item\TextureComponents\Cape\{stem}_U.blp");
            }
        }
        return result;
    }

    private static bool IsNpcBareHeadBatch(uint textureType, int geosetId)
    {
        if (textureType != 1) return false;
        int category = geosetId / 100;
        int variant = geosetId % 100;
        return (category == 0 && variant > 0) || category == 7;
    }

    private static string NpcBareDescriptor(in CreatureModelInfo info) =>
        $"composite://npc-bare/r{info.ExtRace}-s{info.ExtSex}-" +
        $"sk{info.ExtSkin}-f{info.ExtFace}-h{info.ExtHairStyle}-" +
        $"hc{info.ExtHairColor}-fh{info.ExtFacialHair}" +
        (info.IsPlayerAppearance ? $"-eq{string.Join('.', info.ExtEquipment)}" : "");

    private PreparedTexture? PrepareNpcBareComposite(in CreatureModelInfo info)
    {
        if (!info.HasExtended || _charSections is null) return null;
        byte raceId = info.ExtRace;
        byte sexId = info.ExtSex;

        CharSectionRow? skinRow = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionSkin,
            -1, (int)info.ExtSkin);
        IEnumerable<string> skinCandidates = skinRow is null
            ? Array.Empty<string>()
            : CharacterTextureCandidates(skinRow.Texture1, info.ExtRace, info.ExtSex);
        string race = RaceFolder(info.ExtRace);
        string gender = info.ExtSex == 1 ? "Female" : "Male";
        skinCandidates = skinCandidates.Concat(
        [
            $@"Character\{race}\{gender}\{race}{gender}Skin{(int)info.ExtSkin:00}_00.blp",
            $@"Character\{race}\{gender}\{race}{gender}Skin00_00.blp",
        ]).Distinct(StringComparer.OrdinalIgnoreCase);

        byte[]? atlas = null;
        int atlasWidth = 0, atlasHeight = 0;
        foreach (string candidate in skinCandidates)
        {
            byte[]? bytes = _mpq.ReadFile(candidate);
            if (bytes is null) continue;
            try
            {
                atlas = BlpDecoder.GetPixels(bytes, 0, out atlasWidth, out atlasHeight);
                break;
            }
            catch { }
        }
        if (atlas is null) return null;

        void Overlay(string partial, bool upper)
        {
            if (partial.Length == 0) return;
            foreach (string candidate in CharacterTextureCandidates(
                         partial, raceId, sexId))
            {
                byte[]? bytes = _mpq.ReadFile(candidate);
                if (bytes is null) continue;
                try
                {
                    byte[] pixels = BlpDecoder.GetPixels(bytes, 0,
                        out int width, out int height);
                    (int x, int y, int w, int h) = upper
                        ? (0, 160, 128, 32)
                        : (0, 192, 128, 64);
                    float sx = atlasWidth / 256f, sy = atlasHeight / 256f;
                    CharacterEquipment.BlitOver(atlas, atlasWidth, atlasHeight,
                        pixels, width, height, (int)(x * sx), (int)(y * sy),
                        (int)(w * sx), (int)(h * sy));
                    return;
                }
                catch { }
            }
        }

        CharSectionRow? face = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionFace,
            (int)info.ExtFace, (int)info.ExtSkin);
        if (face is not null)
        {
            Overlay(face.Texture1, upper: false);
            Overlay(face.Texture2, upper: true);
        }
        CharSectionRow? facial = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionFacialHair,
            info.ExtFacialHair, (int)info.ExtHairColor);
        if (facial is not null)
        {
            Overlay(facial.Texture1, upper: false);
            Overlay(facial.Texture2, upper: true);
        }
        CharSectionRow? hair = _charSections.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionHair,
            info.ExtHairStyle, (int)info.ExtHairColor);
        if (hair is not null)
        {
            Overlay(hair.Texture2, upper: false);
            Overlay(hair.Texture3, upper: true);
        }

        // A streamed player needs its public item display ids painted over the live
        // CharSections composite in the same canonical order as the local CharacterRenderer.
        if (info.IsPlayerAppearance &&
            info.ExtEquipment.Any(display => display != 0))
        {
            CharacterEquipment equipment = BuildAppearanceEquipment(info);
            atlas = equipment.Composite(atlas, atlasWidth, atlasHeight,
                DecodeEquipmentTexture);
        }

        return new PreparedTexture
        {
            Path = NpcBareDescriptor(info),
            Bgra = atlas,
            Width = atlasWidth,
            Height = atlasHeight,
        };
    }

    private CharacterEquipment BuildAppearanceEquipment(in CreatureModelInfo info)
    {
        var equipment = new CharacterEquipment
        {
            GenderSuffix = info.ExtSex == 1 ? "F" : "M",
        };
        int[] inventoryTypes =
        [
            CharacterEquipment.Slot.Head,
            CharacterEquipment.Slot.Shoulders,
            CharacterEquipment.Slot.Shirt,
            CharacterEquipment.Slot.Chest,
            CharacterEquipment.Slot.Waist,
            CharacterEquipment.Slot.Legs,
            CharacterEquipment.Slot.Feet,
            CharacterEquipment.Slot.Wrists,
            CharacterEquipment.Slot.Hands,
            CharacterEquipment.Slot.Tabard,
            CharacterEquipment.Slot.Cloak,
        ];
        for (int i = 0; i < info.ExtEquipment.Length && i < inventoryTypes.Length; i++)
            if (info.ExtEquipment[i] != 0)
                equipment.Add($"streamed appearance slot {i}", info.ExtEquipment[i],
                    inventoryTypes[i]);
        equipment.Resolve(_itemDisplay);
        return equipment;
    }

    private (byte[] bgra, int w, int h)? DecodeEquipmentTexture(string path)
    {
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        try
        {
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            return (pixels, width, height);
        }
        catch { return null; }
    }

    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    private readonly Dictionary<string, Texture?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private Texture? LoadTexture(IReadOnlyList<string> candidates, out string hitPath)
    {
        hitPath = "NONE";
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_texCache.TryGetValue(path, out var cached))
            {
                if (cached is not null) { hitPath = path; return cached; }
                continue;
            }
            Texture? tex = null;
            try
            {
                byte[]? blp = _mpq.ReadFile(path);
                if (blp is not null) { byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h); tex = Texture.From2D(_gl, bgra, w, h, mipmaps: true, repeat: true); }
            }
            catch { /* leave null */ }
            _texCache[path] = tex;
            if (tex is not null) { hitPath = path; return tex; }
        }
        return null;
    }

    private unsafe void DrawElements(int start, int count)
        => _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedShort, (void*)(start * sizeof(ushort)));

    /// <summary>Apply the M2 material's two-sided flag without leaking disabled
    /// face culling into the following batch or renderer.</summary>
    private void ApplyBatchCulling(in DrawBatch batch, ref bool cullingOn)
    {
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
    }

    public void Dispose()
    {
        ClearPortraitCache();
        _unitAttachments.Clear();
        _attachedItems?.Dispose();
        _shader?.Dispose();
    }

    private LoadedModel? LoadModelMeasured(in CreatureModelInfo info)
    {
        long started = Stopwatch.GetTimestamp();
        LoadsThisFrame++;
        try { return LoadModel(info); }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private Appearance? BuildAppearanceMeasured(LoadedModel model, in CreatureModelInfo info)
    {
        long started = Stopwatch.GetTimestamp();
        LoadsThisFrame++;
        try { return BuildAppearance(model, info); }
        finally
        {
            LoadMillisecondsThisFrame += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    /// <summary>Release synchronously loaded specimen assets between bounded batch chunks.</summary>
    public void ClearPortraitCache()
    {
        foreach (var m in _modelCache.Values)
        {
            if (m is null) continue;
            if (m.Vbo != 0) _gl.DeleteBuffer(m.Vbo);
            if (m.Ebo != 0) _gl.DeleteBuffer(m.Ebo);
            if (m.Vao != 0) _gl.DeleteVertexArray(m.Vao);
        }
        _modelCache.Clear();
        _appearanceCache.Clear();
        _modelJobs.Clear();
        _appearanceJobs.Clear();
        _requestedModels.Clear();
        foreach (var t in _texCache.Values) t?.Dispose();
        _texCache.Clear();
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
out vec3 vWorld;
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
    vWorld = rel.xyz;
}";

    private const string FragSrc = @"#version 330 core
in vec3 vNorm;
in vec2 vUv;
in float vDist;
in vec3 vWorld;
uniform sampler2D uTex;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uAlphaCut;
uniform float uHighlight;
uniform float uBodyAlpha;
uniform vec3 uBodyTint;
uniform vec3 uAmbientColor;
uniform float uAmbientIntensity;
uniform int uPointLightCount;
uniform vec3 uPointLightPos[8];
uniform vec3 uPointLightColor[8];
out vec4 frag;
vec3 carriedPointLight(vec3 normal, vec3 worldPos){
    float d0=1e30,d1=1e30,d2=1e30; vec3 v0=vec3(0.0),v1=vec3(0.0),v2=vec3(0.0);
    vec3 c0=vec3(0.0),c1=vec3(0.0),c2=vec3(0.0);
    for(int i=0;i<8;i++){ if(i>=uPointLightCount) break;
        vec3 v=uPointLightPos[i]-worldPos; float ds=dot(v,v);
        if(ds<d0){d2=d1;v2=v1;c2=c1;d1=d0;v1=v0;c1=c0;d0=ds;v0=v;c0=uPointLightColor[i];}
        else if(ds<d1){d2=d1;v2=v1;c2=c1;d1=ds;v1=v;c1=uPointLightColor[i];}
        else if(ds<d2){d2=ds;v2=v;c2=uPointLightColor[i];}}
    vec3 s=vec3(0.0);
    if(d0<1e29){float d=sqrt(d0);s+=c0*max(dot(normal,v0/max(d,.001)),0.0)/max(.7*d+.03*d*d,.001);}
    if(d1<1e29){float d=sqrt(d1);s+=c1*max(dot(normal,v1/max(d,.001)),0.0)/max(.7*d+.03*d*d,.001);}
    if(d2<1e29){float d=sqrt(d2);s+=c2*max(dot(normal,v2/max(d,.001)),0.0)/max(.7*d+.03*d*d,.001);}
    return s;
}
float model2SunResponse(float mu){
    return (4.0/17.0)*(0.375+2.0*mu+1.875*mu*mu);
}
const float WorldModelSelfFill = 0.25;
void main(){
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    vec3 normal = normalize(vNorm);
    if (!gl_FrontFacing) normal = -normal;
    float sunResponse = model2SunResponse(dot(normal, normalize(uSunDir)));
    vec3 light = uAmbientColor * uAmbientIntensity
        + uSunColor * uSunIntensity * sunResponse + vec3(uHighlight);
    light += vec3(WorldModelSelfFill);
    light += carriedPointLight(normal, vWorld);
    light = max(light, vec3(0.0));
    float fog = clamp((vDist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    frag = vec4(mix(t.rgb * uBodyTint * light, uFogColor, fog), t.a * uBodyAlpha);
}";
}
