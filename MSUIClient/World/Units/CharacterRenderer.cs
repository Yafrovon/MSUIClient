using System.Numerics;
using System.Diagnostics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>
/// Draws one skinned character. This is the unit renderer, not a player
/// renderer - every NPC, mob and other player in Phase 2 goes through the same
/// path, which is why the state it consumes is a plain <see cref="UnitState"/>
/// rather than a CharacterController.
///
/// SAME SHAPE AS DoodadRenderer, DELIBERATELY
///   Model out of the MPQs, one VAO, one draw per visible submesh, textures via
///   batch.TextureIndex -> TextureLookup -> Textures. It reuses wmo.frag
///   unchanged so a character lights and fogs exactly like the ground it stands
///   on. Only the vertex shader is new, because only skinning is new.
///
/// WHAT MAKES A CHARACTER DIFFERENT FROM A DOODAD
///   1. It has a skeleton, so vertices are transformed by bone matrices before
///      the instance matrix. See M2Animator.
///   2. Its submeshes are GEOSETS, and they are not all meant to be visible at
///      once. Draw them all and the character wears every hairstyle in the file
///      simultaneously. The naked-default rules below come from the SuperUI
///      character viewer's geoset-rules.js, verified against HumanMale.
///   3. Most of its texture slots carry NO FILENAME. A character M2 expects the
///      application to supply the body skin, hair and cape images; only Type 0
///      slots name a BLP. That is the whole reason CharacterSkinCompositor
///      exists in SuperUI, and it is the part this class approximates for now.
///
/// THE MODEL-TO-WORLD BASIS IS NOT OPTIONAL, EVEN THOUGH DOODADS APPEAR NOT TO
/// HAVE ONE
///   The handbook says an M2's render vertices need no basis. That is true for
///   a doodad only because ADT placement space is itself Y-up, so the
///   placement-to-world conversion carries the flip. A character has no ADT
///   placement, so this class applies that conversion's LINEAR PART directly:
///
///       (x, y, z) -> (-z, -x, y)
///
///   which is exactly PlacementToWorld with the map-corner translation removed.
///
/// AND THE HEADING OFFSET IS A KNOB, NOT A CONSTANT
///   Which model-space axis a character faces along is the one thing here that
///   cannot be settled by arithmetic - a bounding box is invariant under a half
///   turn, so no scorer can catch a backwards character. The derivation says
///   Yaw + 90 degrees. It is exposed as a live slider so one run and one number
///   settles it, instead of a rebuild per guess. The debug capsule already
///   draws a facing spike; line the model up with that.
/// </summary>
public sealed partial class CharacterRenderer : IDisposable
{
    /// <summary>Position(3) Normal(3) UV(2) BoneWeights(4) BoneIndices(4).</summary>
    private const int FloatsPerVertex = 16;

    /// <summary>
    /// AnimationData.dbc IDs baked at load. Locomotion plus the airborne set.
    /// Missing entries are normal and handled by fallback chains in
    /// <see cref="ChooseClip"/> - not every model has every animation.
    /// </summary>
    private static readonly int[] BakedAnimations =
        [0, 4, 5, 9, 11, 12, 13, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 30,
         37, 38, 39, 40, 41, 42, 43, 44, 45, 85, 87, 88, 89, 90, 92, 93, 96, 97, 98, 99, 100, 101, 102, 103, 104,
         114, 115, 116, 117, 187];

    /// <summary>
    /// Geoset variant shown per category when nothing is equipped. Ported from
    /// the character viewer's geoset-rules.js NAKED_DEFAULTS, which was verified
    /// against a HumanMale reference capture. Categories absent from this table
    /// are hidden; category 0 is special-cased (base body plus one hairstyle).
    /// </summary>
    private static readonly Dictionary<int, int> NakedDefaults = new()
    {
        [1] = 1,    // face chin
        [2] = 1,    // face jaw
        [3] = 1,    // face mouth
        [4] = 1,    // bare hands
        [5] = 1,    // bare shins / no boots
        [7] = 2,    // ears  (variant 2 is the normal ear, variant 1 is minimal)
        [13] = 1,   // bare pants
        [15] = 1,   // no cape
        [32] = 1,   // face geometry
    };

    /// <summary>ADT placement space to world, linear part only: (x,y,z) -> (-z,-x,y).</summary>
    private static readonly Matrix4x4 ModelToWorld = new(
         0f, -1f, 0f, 0f,
         0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
         0f, 0f, 0f, 1f);

    /// <summary>State the renderer needs about a unit. Player today, packets tomorrow.</summary>
    public struct UnitState
    {
        public ulong Guid;
        public Vector3 Position;

        /// <summary>
        /// The AIM: where the unit is pointed, what a movement packet carries.
        /// The drawn body is a separate angle that chases this one - see
        /// <see cref="DriveBodyHeading"/>.
        /// </summary>
        public float Yaw;

        public bool Grounded;
        public float VerticalVelocity;
        public float FallTimeMs;
        public bool Walking;
        public bool Flying;
        public bool Swimming;
        public float SwimPitch;
        public bool Engaged;

        /// <summary>UNIT_FIELD_BYTES_1's StandState byte (see UnitStandState) - the
        /// server's own field, not client-guessed. Default 0 (Stand).</summary>
        public byte StandState;

        /// <summary>UNIT_NPC_EMOTESTATE's raw Emotes.dbc id (Dance, ...), or 0. See
        /// ObjectFields.UNIT_NPC_EMOTESTATE's doc comment.</summary>
        public uint EmoteState;

        /// <summary>Hold the already-evaluated body pose and all of its animation clocks.</summary>
        public bool FreezePose;
        public bool ApplyBodyVisual;
        public float BodyAlpha;
        public Vector3 BodyTint;

        // ── intent ───────────────────────────────────────────────────────────
        //
        // WHY THE RENDERER IS TOLD WHAT WAS PRESSED. It used to work the
        // direction and speed out by differencing Position and low-passing the
        // result, which is a defensible idea and wrong in four ways at once:
        // it lagged every start by about eighty milliseconds, it read a wall
        // slide as a change of direction and played WalkBackwards while you
        // held W, it could not see a turn in place at all because turning
        // produces no displacement, and it fed the leg-cycle rate a raw
        // per-frame delta that jittered with the frame time.
        //
        // None of those are fixable downstream, because by then the
        // information is already gone. The reference client drives the
        // animation layer from the movement flags and the commanded velocity,
        // so this does too.

        /// <summary>-1 back .. +1 forward, as pressed.</summary>
        public float Forward;

        /// <summary>-1 left .. +1 right, as pressed.</summary>
        public float Strafe;

        /// <summary>Commanded horizontal speed in yd/s. Zero when no key is held.</summary>
        public float Speed;

        /// <summary>
        /// The aim is being steered this frame - turn keys or mouse-look.
        ///
        /// This is what freezes the standing body chase. While you are steering,
        /// the body holds and the aim leads it; the sweep back onto the aim only
        /// runs once you stop.
        /// </summary>
        public bool Steering;

        /// <summary>
        /// False means "nobody filled the fields above" - the glue booth, or any
        /// caller that only has a position. Falls back to measuring displacement,
        /// which is fine for a character standing on a pedestal.
        /// </summary>
        public bool HasIntent;

        /// <summary>
        /// Yards/sec the body is being carried at by something that is NOT your input: a
        /// server-authored spline on your own mover. Charge and Intercept are the everyday
        /// ones; a knockback and a taxi hop arrive the same way. 0 whenever input owns the body.
        /// </summary>
        public float CarriedSpeed;

        /// <summary>
        /// MOVING IS NOT THE SAME QUESTION AS "IS A KEY DOWN", AND READING IT THAT WAY IS WHAT
        /// FROZE CHARGE. This was Forward/Strafe alone, so any travel you did not personally
        /// command scored as standing still: ResolveMotion took the !Moving branch, hard-zeroed
        /// the gait, and the body slid across the ground bolt upright at full speed. Reported
        /// 2026-09-01 for Charge; every server-driven translation had it.
        ///
        /// The mount renderer already got this right — Program.cs takes
        /// `_serverRideSpline?.AverageSpeed ?? controller.PlanarSpeed` for the mount's gait, so
        /// the mount ran while its rider stood. CarriedSpeed feeds the character the same number.
        /// </summary>
        public readonly bool Moving =>
            MathF.Abs(Forward) > 0.01f || MathF.Abs(Strafe) > 0.01f || CarriedSpeed > 0.01f;
    }

    private enum SlotFill { Bound, BodySkin, Unbound }

    private sealed class Slot
    {
        public Texture? Texture;
        public float AlphaCutoff = 0.35f;
        public SlotFill Fill = SlotFill.Unbound;
        public uint Type;
        public string Source = "";
    }

    private sealed class Piece
    {
        public uint IndexStart;
        public uint IndexCount;
        public int SlotIndex = -1;
        public bool TwoSided;
        public int GeosetId;
        public int Category;
        public int Variant;
        public bool Visible;
        public int SubmeshIndex;
        public int BatchIndex;
        public sbyte PriorityPlane;
        public ushort MaterialLayer;

        /// <summary>
        /// M2 blend mode. 0 opaque, 1 alpha-key, 2 alpha, 3+ additive and the
        /// modulate variants. TWO AND ABOVE ARE TRANSPARENT and must be drawn
        /// in a second pass with depth writes off.
        /// </summary>
        public int BlendMode;

        public bool NoZWrite;
        public bool Transparent => BlendMode >= 2 || NoZWrite;
    }

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly AssetWorkerPool? _workers;
    private readonly GpuUploadWorker? _uploads;

    private Shader _shader = null!;
    private uint _vao, _vbo, _ebo;

    private M2Model? _m2;
    private M2Animator? _animator;
    private readonly List<Piece> _pieces = [];
    private readonly List<Slot> _slots = [];
    private Texture? _magenta;

    /// <summary>
    /// The base skin BGRA is kept because equipment is composited ONTO it, and
    /// re-equipping has to start from bare skin rather than from whatever was
    /// painted last time.
    /// </summary>
    private byte[]? _baseSkin;
    private int _skinWidth, _skinHeight;
    private float _skinCutoff = 0.35f;
    private int _bodySlotIndex = -1;
    private Texture? _bareSkin;
    private Texture? _dressedSkin;

    private sealed class PreparedTextureData
    {
        public byte[] Pixels = [];
        public int Width;
        public int Height;
    }

    private sealed class PreparedSlotData
    {
        public uint Type;
        public SlotFill Fill;
        public string Source = "";
        public float AlphaCutoff = 0.35f;
        public PreparedTextureData? Texture;
    }

    private sealed class PreparedAppearanceData
    {
        public required CharacterEquipment Equipment;
        public required List<PreparedSlotData> Slots;
        public required PreparedTextureData Magenta;
        public PreparedTextureData? BareSkin;
        public PreparedTextureData? DressedSkin;
        public byte[]? BaseSkin;
        public int SkinWidth;
        public int SkinHeight;
        public float SkinCutoff;
        public string SkinPath = "";
        public int BodySlotIndex = -1;
        public int UnboundSlots;
    }

    private sealed class UploadedAppearanceData
    {
        public required Dictionary<PreparedTextureData, Texture> Textures;
    }

    private sealed class AppearanceLoadJob
    {
        public required Task<PreparedAppearanceData> Worker;
        public PreparedAppearanceData? Ready;
        public Task<UploadedAppearanceData>? Upload;
    }

    private AppearanceLoadJob? _appearanceLoad;
    private const double AppearanceFinalizeBudgetMs = 2.0;

    private ItemDisplayTable? _itemDisplay;
    private CharSectionsTable? _charSections;
    private CharHairGeosetsTable? _charHairGeosets;
    private CharacterGeosets? _characterGeosets;

    /// <summary>
    /// Character-creation appearance choices. In a real login these arrive in
    /// the four appearance bytes of the character record; until then they are
    /// knobs, because flipping through them is the fastest way to prove the
    /// CharSections lookup is finding real rows rather than falling through.
    /// </summary>
    public int SkinId { get; set; }
    public int FaceId { get; set; }
    // Until login supplies the real appearance byte, keep the Human male style
    // used by the original test character. CharHairGeosets maps style 9 to
    // geoset 10; style 0 is the valid but bald/default scalp.
    public int HairStyleId { get; set; } = 9;
    public int HairColorId { get; set; }
    public int FacialHairId { get; set; }
    private AttachedItemRenderer? _attached;

    /// <summary>Helms, shoulders, weapons and shields. Null until shaders load.</summary>
    public AttachedItemRenderer? Attached => _attached;

    /// <summary>What this character is wearing. Populate it, then call ApplyEquipment.</summary>
    public CharacterEquipment Equipment { get; set; } = new();

    /// <summary>Resolved type-2 cloak source currently bound by the production renderer.</summary>
    public string VariantCapeTexture => _slots.FirstOrDefault(slot =>
        slot.Type == 2 && slot.Fill == SlotFill.Bound)?.Source ?? "";

    /// <summary>Release regenerable attachment assets between unattended item-batch chunks.</summary>
    public void ClearVariantItemCache() => _attached?.ClearVariantCache();

    private Matrix4x4[] _skin = [];
    private float[] _packed = [];

    private M2Animator.Clip? _clip;
    private float _clipTime;
    private float _globalTime;
    private float _clipRate = 1f;
    private M2Animator.Clip? _combatAction;
    // A landed-hit wound is a secondary blend, not an action-track replacement. Keeping its
    // clip and clock separate lets an in-flight swing continue to its authored $CAH impact key.
    private M2Animator.Clip? _combatReaction;
    private float _combatReactionTime;
    private bool _combatReactionMasked;
    private M2Animator.Clip? _spellHold;
    // Upper-body action mask (seated eat/drink/emote, and any one-shot armed while the lower
    // body is committed - moving, swimming, mounted). _combatAction is layered onto the SpineLow
    // subtree over the locomotion or seated base (Render) instead of hijacking the whole body -
    // the Benilla committed_lower rule, see CharacterPoseLaw.CommittedLower. It runs on its own
    // clock because _clipTime then belongs to the base pose; _actionOverlayArmedRef restarts that
    // clock when a fresh action is armed, and is also where the route is chosen for the play.
    private float _actionOverlayTime;
    private M2Animator.Clip? _actionOverlayArmedRef;
    private M2Animator.Clip? _torsoOverlayForRender;
    // The route for the CURRENT play, decided once when the action is armed and never
    // re-evaluated - see CharacterPoseLaw.CommittedLower on why per-frame routing pops the legs.
    private bool _combatActionMasked;
    private M2Animator.Clip? _rightSheathOverlay;
    private M2Animator.Clip? _leftSheathOverlay;
    private float _sheathOverlayTime;
    private float _sheathSwapAt;
    private float _sheathCeremonyDuration;
    private bool _sheathCeremonyActive;
    private bool _sheathSwapReady;
    public bool SheathCeremonyActive => _sheathCeremonyActive;
    public long CombatActionsTriggered { get; private set; }
    public string CurrentAnimation => _clip?.Name ?? "none";
    public string CurrentActionAnimation => _combatAction?.Name ?? "none";
    public string CurrentReactionAnimation => _combatReaction?.Name ?? "none";
    public string CurrentSpellHoldAnimation => _spellHold?.Name ?? "none";
    public string CurrentPresentationAnimation =>
        _spellHold?.Name ?? _combatAction?.Name ?? _clip?.Name ?? "none";
    public string CurrentBaseAnimation => _clip?.Name ?? "none";
    public string PreviousBaseAnimation => _previousClip?.Name ?? "none";
    public float CurrentBlendWeight => _blendDuration <= 0f
        ? 1f : 1f - Math.Clamp(_blendRemaining / _blendDuration, 0f, 1f);

    public readonly record struct ClipTransition(
        long Sequence,
        int FromId,
        string FromName,
        int ToId,
        string ToName,
        float OutgoingTime);

    private long _clipTransitionSequence;
    public ClipTransition LastClipTransition { get; private set; }

    // ── cross-fade ───────────────────────────────────────────────────────────
    //
    // The clip we are fading OUT of, its own clock, and how much of the fade is
    // left. Two slots only: a change during a fade drops the older pose rather
    // than growing a stack. That is the standard compromise and it is invisible
    // outside of deliberately mashed direction changes.
    private M2Animator.Clip? _previousClip;
    private float _previousClipTime;
    private float _previousClipRate = 1f;
    private float _blendRemaining;
    private float _blendDuration;
    // When set, overrides the cross-fade length for the very next clip switch only
    // (the seated->run blend uses it to stay smooth regardless of the run clip's own
    // authored blendTime). Consumed and cleared by SwitchClip.
    private float? _forceNextBlendSeconds;

    private Vector3 _lastPosition;
    private bool _hasLastPosition;

    /// <summary>Speed the gait is chosen from: commanded when we have intent, measured otherwise.</summary>
    private float _groundSpeed;

    /// <summary>Speed the leg-cycle rate divides by. Same source, no smoothing.</summary>
    private float _instantGroundSpeed;

    /// <summary>Displacement-derived speed, kept for the HUD so the two can be compared.</summary>
    private float _measuredSpeed;

    private float _forwardness;
    private float _sideness;

    /// <summary>The drawn body's heading OFFSET from the aim, radians. See <see cref="DriveBodyHeading"/>.</summary>
    private float _moveYaw;

    /// <summary>The drawn body's ABSOLUTE heading, radians. The aim is <see cref="UnitState.Yaw"/>.</summary>
    private float _bodyYaw;
    private bool _hasBodyYaw;

    /// <summary>How far the body turned this frame, signed. Drives the turn-in-place shuffle.</summary>
    private float _bodyTurnStep;

    /// <summary>Edge-detect for the _combatAction movement-break rule in Update -
    /// see the comment there for why this has to be a change, not a level check.</summary>
    private bool _wasMovingLastFrame;
    // Edge-detect for leaving a ground stand-state, which ends a seated consume - see Update.
    private bool _wasSeatedLastFrame;

    // ── landing ──────────────────────────────────────────────────────────────
    private M2Animator.Clip? _landClip;
    private float _landForward, _landStrafe;
    private bool _landWalking;
    private bool _wasAirborne;
    private bool _jumpArcActive;
    private bool _jumpHangShown;
    private M2Animator.Clip? _jumpStartClip;

    // ── seated / kneeling / sleeping (UnitStandState) ──────────────────────────
    // Same bracket idea as landing above: Down/Up are one-shot transitions either
    // side of a held Loop, not something ChooseClip re-derives from scratch every
    // frame. _seatedLoopAnimId is 0 whenever StandState isn't one of the three
    // this client renders (chair-sitting is deferred - see ChooseClip).
    private int _seatedLoopAnimId;
    private int _seatedUpAnimId;
    private M2Animator.Clip? _seatedDownClip;
    private M2Animator.Clip? _seatedUpClip;

    /// <summary>Below this the character counts as standing still, in yards per second.</summary>
    private const float MoveThreshold = 0.3f;

    /// <summary>
    /// Rate the body eases toward its strafe offset, per second.
    ///
    /// Not a taste value: the reference client blends a quarter of the remaining
    /// gap per frame at sixty frames a second, and -ln(0.75) * 60 = 17.26 is the
    /// frame-rate-independent form of exactly that.
    /// </summary>
    private const float StrafeBlendRate = 17.26f;

    /// <summary>
    /// The body's catch-up pace as a multiple of <see cref="BodyTurnRate"/> after
    /// steering is released. 0.8 closes ninety degrees in about 625 ms, allowing
    /// one or two authored foot shuffles to carry the rotation instead of a
    /// four-frame snap. Steering itself still holds the ninety-degree ceiling.
    /// </summary>
    public float StationaryChaseRate { get; set; } = 0.8f;

    /// <summary>
    /// The body's turn rate, radians per second. Matches the aim's own standing
    /// rate so the chase closes at a believable speed.
    /// </summary>
    private const float BodyTurnRate = MathF.PI;

    /// <summary>
    /// Locomotion clips, for the phase-preservation rule in
    /// <see cref="SwitchClip"/>. Shift-walking out of a run must not restart the
    /// leg cycle.
    /// </summary>
    private static readonly HashSet<int> LocomotionAnimations = [4, 5, 11, 12, 13, 92, 93];

    /// <summary>Clips that mean "airborne", for the landing gate.</summary>
    private static readonly HashSet<int> AirborneAnimations = [37, 38, 40];

    /// <summary>
    /// Categories forced off, for finding which geoset is doubling up. Drag
    /// through them in the HUD: whatever disappears is what was fighting.
    /// </summary>
    public HashSet<int> HiddenCategories { get; } = [];

    /// <summary>Category and variant of every geoset currently drawn.</summary>
    public List<(int Category, int Variant)> ActiveGeosets { get; private set; } = [];

    // Head-texture diagnostics (surfaced live in the HUD + a Capture-to-file button).
    public string HairResolution { get; private set; } = "";
    public bool ScalpCovered { get; private set; }
    private readonly List<string> _headDiag = new();
    public IReadOnlyList<string> HeadDiag => _headDiag;
    public string? LastDiagnosticPath { get; private set; }

    /// <summary>
    /// Hide the hairstyle without hiding the body.
    ///
    /// The per-category checkboxes cannot do this, and that is a flaw in them:
    /// category 0 holds the BASE BODY at variant 0 and every hairstyle at the
    /// others, so unticking category 0 to test whether hair is fighting the
    /// helm removes the entire character. Hair needs its own switch.
    /// </summary>
    public bool HideHair { get; set; }

    /// <summary>
    /// Draw ONE geoset and nothing else. -1 draws them all normally.
    ///
    /// Hiding categories was not decisive because z-fighting needs both halves
    /// present to show, so switching one off tells you a pair stopped fighting
    /// but not which pair. Soloing inverts that: step through the eleven drawn
    /// geosets one at a time, and the one that flickers ON ITS OWN is either
    /// self-overlapping or fighting something outside the geoset list entirely.
    /// If NONE of them flickers alone, the fight is between two of them and the
    /// index where it starts is the second half of the answer.
    /// </summary>
    public int SoloGeoset { get; set; } = -1;

    // ── knobs ────────────────────────────────────────────────────────────────

    public bool Enabled { get; set; } = true;

    /// <summary>Bind pose, no animation. First thing to try if the model looks folded.</summary>
    public bool BindPose { get; set; }

    /// <summary>Fresh Stand animation frozen at time zero, used by the portrait booth.</summary>
    public bool FrozenStandPose { get; set; }

    /// <summary>
    /// A caller-owned Stand clock used for a live UI model. It changes only the pose evaluated
    /// by <see cref="Render"/>; the world locomotion and action clocks remain untouched.
    /// </summary>
    public float? StandPreviewTime { get; set; }

    /// <summary>Client-local held Loot-50 pose; locomotion still outranks it.</summary>
    public bool LootKneel { get; set; }

    /// <summary>
    /// Set when the model has more bones than the shader can hold. Animation is
    /// then refused outright rather than run on a truncated skeleton.
    ///
    /// This exists because the truncated version is WORSE than no animation and
    /// much harder to read: clamping the missing bones onto the last valid one
    /// looks perfect in bind pose and like a folded paper alien in motion, with
    /// nothing on screen pointing at the bone table. A parse or capacity failure
    /// must never present later as something that looks like a maths bug.
    /// </summary>
    public bool BoneOverflow { get; private set; }

    /// <summary>Draw every geoset. Produces the "all hairstyles at once" blob, on purpose.</summary>
    public bool ShowAllGeosets { get; set; }

    /// <summary>Paint texture slots we could not resolve magenta instead of falling back to skin.</summary>
    public bool MagentaUnbound { get; set; }

    /// <summary>Degrees added to the unit's yaw before the model-to-world basis. See class doc.</summary>
    public float HeadingOffsetDegrees { get; set; } = 90f;

    public float ModelScale { get; set; } = 1f;
    public byte SheathState { get; set; }
    private float _bindPoseHeight = 1.8f;

    /// <summary>Standing model height at scale 1, derived once from bind-pose geometry.</summary>
    public float BindPoseHeight() => _bindPoseHeight;

    /// <summary>
    /// The Stand sequence's authored CAaBox Z extent, scaled exactly once by the
    /// displayed character model. Chat bubbles latch this per line; it is not a
    /// posed overhead attachment.
    /// </summary>
    public float StandBoxHeight()
    {
        if (_m2 is null) return 0f;
        M2Sequence? stand = _m2.Sequences.FirstOrDefault(s =>
            s.AnimationId == 0 && s.VariationId == 0) ??
            _m2.Sequences.FirstOrDefault(s => s.AnimationId == 0);
        float scale = MathF.Max(0.01f, ModelScale);
        float authored = stand?.BoundsZExtent ?? 0f;
        return MathF.Max(authored, _bindPoseHeight) * scale;
    }

    public bool TryGetAuthoredPortrait(in UnitState state, out M2PortraitCamera camera,
        out Matrix4x4 modelTransform)
    {
        if (_m2?.PortraitCamera is not { } authored)
        {
            camera = default;
            modelTransform = default;
            return false;
        }

        camera = authored;
        modelTransform = BuildTransform(state);
        return true;
    }

    /// <summary>Vertical nudge, in yards. The M2 origin should already be at the feet.</summary>
    public float ZOffset { get; set; }

    /// <summary>
    /// The saddle: attachment 0 of the steed this character is riding, in world space,
    /// supplied each frame by <see cref="CreatureRenderer.TryDrawSelfMount"/>. Null means on
    /// foot and the ordinary ground transform applies.
    ///
    /// When it is set the seat owns placement outright — position, facing, ZOffset and the
    /// strafe body yaw all come from the mount, because the rider is parented to it. See
    /// CreatureRenderer.Mounts.cs for why 1.12 mounts are the whole "vehicle" story.
    /// </summary>
    public Matrix4x4? MountSeat { get; set; }

    public bool Mounted => MountSeat.HasValue;

    /// <summary>
    /// How a character that is travelling sideways is made to look right.
    ///
    /// WholeBody turns the entire model to face the direction of travel and
    /// plays the ordinary run cycle. Simple, reads cleanly, and correct for the
    /// common case: the camera still sits behind where you are FACING, so
    /// strafing right shows you the character's side while he runs.
    ///
    /// LowerBody turns only the hips and legs and leaves the torso facing
    /// forward. Closer to what the real client does and more work: it needs the
    /// right bone, which is what TwistBone and the diagnostics around it exist
    /// for.
    ///
    /// Clips picks a separate sideways animation and rotates nothing. This is
    /// the version that looks like a dance step at running speed, kept only so
    /// the three are one click apart.
    /// </summary>
    public enum StrafeStyle { Split, WholeBody, LowerBody, Clips }

    /// <summary>
    /// Split is the real one and the default. The other three are the earlier
    /// attempts, kept because they are the ends of the same slider: TorsoFollow
    /// at 1.0 IS WholeBody and at 0.0 IS LowerBody.
    /// </summary>
    public StrafeStyle Strafe { get; set; } = StrafeStyle.Split;

    /// <summary>
    /// How much of the strafe angle the TORSO keeps, 0 to 1. The legs always
    /// take the full angle.
    ///
    /// Strafing is not "turn the body" and it is not "turn the legs" - it is
    /// both, at different angles. Nico measured the real client by eye at about
    /// ninety degrees on the legs against sixty on the torso, so roughly two
    /// thirds. WoWee's renderer carries the matching hook,
    /// setInstanceTorsoYaw(id, deltaYawRad) with a per-instance
    /// torsoYawOverrideRad - a DELTA on the torso over whatever the body is
    /// already doing.
    ///
    /// The exact constant lives in their character_renderer.cpp, which I have
    /// not seen, so this is a slider set to his measurement rather than a
    /// number I am pretending to know.
    /// </summary>
    public float TorsoFollow { get; set; } = 0.66f;

    /// <summary>Where the torso half of the strafe applies. Forwarded to the animator.</summary>
    public int TorsoBone
    {
        get => _animator?.TorsoBone ?? -1;
        set { if (_animator is not null) _animator.TorsoBone = value; }
    }

    /// <summary>
    /// Ceiling on the twist, in degrees. A hundred is already past what a hip
    /// can do; beyond it the legs visibly detach from the body.
    /// </summary>
    public float MaxTwistDegrees { get; set; } = 100f;

    /// <summary>
    /// Hold the strafe angle fixed regardless of movement. Zero means off and
    /// the animation drives it as normal.
    ///
    /// THIS IS THE TEST, not a feature, and it is what turned "did nothing"
    /// into an answer. Stand still and drag it: in WholeBody the model should
    /// swing to face the angle, in LowerBody the legs should and the torso
    /// should not. Whichever half moves tells you which half the code is
    /// actually acting on, with nothing depending on the trigger firing.
    /// </summary>
    public float ForceAngleDegrees { get; set; }

    /// <summary>Where the twist stops. Forwarded to the animator so the HUD can adjust it live.</summary>
    public int TwistBone
    {
        get => _animator?.TwistBone ?? -1;
        set { if (_animator is not null) _animator.TwistBone = value; }
    }

    /// <summary>
    /// True twists the hip bone's subtree, which is the lower body. False
    /// restores the original scheme - twist everything, cancel at that bone -
    /// which rotates the UPPER body instead. Kept so the two are one click
    /// apart if a rig turns out to be the other shape.
    /// </summary>
    public bool TwistSubtree
    {
        get => _animator?.TwistSubtree ?? true;
        set { if (_animator is not null) _animator.TwistSubtree = value; }
    }

    public float MoveYawDegrees => _moveYaw * 180f / MathF.PI;

    /// <summary>
    /// Cross-fade used when the M2 sequence authors a blendTime of zero, in
    /// seconds. Also the floor for locomotion transitions.
    ///
    /// Vanilla's own values cluster around 150 ms. Set it to zero to get the old
    /// hard-cut behaviour back in one click, which is the fastest way to confirm
    /// that a pose problem is or is not the blend.
    /// </summary>
    public float DefaultBlendSeconds { get; set; } = 0.15f;

    /// <summary>
    /// How far the drawn body may lag the aim while standing, in degrees.
    ///
    /// This is the frozen chase, and it is the most recognisable thing about a
    /// vanilla character standing still: turn the camera and the body does not
    /// follow until the aim is ninety degrees ahead of it, then it holds exactly
    /// that lag until you let go and it sweeps square again. Zero disables it and
    /// welds the body to the aim, which is what this client did before.
    /// </summary>
    public float StandingChaseCeilingDegrees { get; set; } = 90f;

    // Live animation diagnostics.
    public float BodyYawDegrees => _bodyYaw * 180f / MathF.PI;
    public float BodyYawRadians => _bodyYaw;

    /// <summary>Snap the rendered body to a server-authoritative facing change.</summary>
    public void SnapFacing(float yaw)
    {
        _bodyYaw = yaw;
        _moveYaw = 0f;
        _bodyTurnStep = 0f;
        _hasBodyYaw = true;
    }
    public string BlendFrom => _previousClip?.Name ?? "";
    public float BlendWeight => BlendWeightNow();
    public float BlendFromTime => _previousClipTime;
    public float IncomingBlendWeight => _previousClip is null ? 0f : 1f - BlendWeightNow();
    public float MeasuredSpeed => _measuredSpeed;

    public string Race { get; private set; } = "Human";
    public string Gender { get; private set; } = "Male";

    // ── diagnostics ──────────────────────────────────────────────────────────

    public bool Loaded => _m2 is not null;
    public string ModelPath { get; private set; } = "";
    public int BoneCount => _animator?.BoneCount ?? 0;
    public int ClipCount => _animator?.Clips.Count ?? 0;
    public int PieceCount => _pieces.Count;
    public int VisiblePieces { get; private set; }
    public int UnboundSlots { get; private set; }
    public string ClipName => _clip?.Name ?? "(bind pose)";
    public int ClipId => _clip?.AnimationId ?? -1;
    public bool ClipLooping => _clip?.Looping ?? true;
    public float ClipTime => _clipTime;
    public float ClipDuration => _clip?.DurationSeconds ?? 0f;
    public float ClipRate => _clipRate;
    public float ClipMoveSpeed => _clip?.MoveSpeed ?? 0f;
    public float GroundSpeed => _groundSpeed;
    public string SkinTexturePath { get; private set; } = "";
    public Action<string, int, M2Animator.Resolution>? AnimationResolved { get; set; }
    /// <summary>Emotes.dbc id (UNIT_NPC_EMOTESTATE / SMSG_EMOTE space) -&gt; AnimationData id,
    /// 0 if none. Wired to the live <see cref="Formats.EmoteCatalog"/> so the state-emote
    /// (Dance) path resolves from the real DBC, same source as the one-shot emote path -
    /// no second hand-maintained table.</summary>
    public Func<uint, int>? EmoteAnimResolver { get; set; }
    /// <summary>One callback per authored $FSD marker crossed by the live base clip.</summary>
    public Action? FootstepAnimationEvent { get; set; }
    public Action<string>? CreatureAnimationSoundEvent { get; set; }
    public Action<string>? CombatAnimationSoundEvent { get; set; }
    private int _footstepSequence = -1;
    private float _footstepTime;

    private const int BaseAnimationTrack = 0;
    private const int ActionAnimationTrack = 1;
    private const int SpellHoldAnimationTrack = 2;

    // Walking out of a sit does NOT play the stand-up clip in the real 1.12 client -
    // it blends the seated pose straight into the gait ("very clipped, basically just
    // gets you sitting -> running", per reference-client observation). This is the
    // cross-fade length for that one seated->run transition: quick, but long enough
    // to read as smooth rather than a pop. A stationary /stand still plays the full
    // authored stand-up. Tunable.
    private const float SeatedRunBlendSeconds = 0.18f;

    public CharacterRenderer(GL gl, ClientConfig config,
        AssetWorkerPool? workers = null, GpuUploadWorker? uploads = null)
    {
        _gl = gl;
        _config = config;
        _workers = workers;
        _uploads = uploads;
    }

    /// <summary>Carry world/dev tuning onto a renderer whose authored assets are already loaded.</summary>
    public void CopyRuntimeTuningFrom(CharacterRenderer? source)
    {
        if (source is null) return;
        HideHair = source.HideHair;
        SoloGeoset = source.SoloGeoset;
        Enabled = source.Enabled;
        BindPose = source.BindPose;
        FrozenStandPose = source.FrozenStandPose;
        ShowAllGeosets = source.ShowAllGeosets;
        MagentaUnbound = source.MagentaUnbound;
        HeadingOffsetDegrees = source.HeadingOffsetDegrees;
        ModelScale = source.ModelScale;
        SheathState = source.SheathState;
        ZOffset = source.ZOffset;
        Strafe = source.Strafe;
        TorsoFollow = source.TorsoFollow;
        MaxTwistDegrees = source.MaxTwistDegrees;
        ForceAngleDegrees = source.ForceAngleDegrees;
        DefaultBlendSeconds = source.DefaultBlendSeconds;
        StandingChaseCeilingDegrees = source.StandingChaseCeilingDegrees;
        StationaryChaseRate = source.StationaryChaseRate;
        AnimationResolved = source.AnimationResolved;
        EmoteAnimResolver = source.EmoteAnimResolver;
        SunDirection = source.SunDirection;
        SunColor = source.SunColor;
        SunIntensity = source.SunIntensity;
        AmbientColor = source.AmbientColor;
        AmbientIntensity = source.AmbientIntensity;
        ShadowSoftness = source.ShadowSoftness;
        FogColor = source.FogColor;
        FogStart = source.FogStart;
        FogEnd = source.FogEnd;
        AlphaCutoff = source.AlphaCutoff;
        HiddenCategories.Clear();
        HiddenCategories.UnionWith(source.HiddenCategories);
    }

    /// <summary>
    /// character.vert is new because skinning is new. The fragment stage is
    /// wmo.frag UNCHANGED - sharing the file is what guarantees a character
    /// cannot light differently from the world around it.
    ///
    /// Note it is still a SEPARATE GL PROGRAM from the WMO and doodad ones, so
    /// its uniforms must be set independently. Forgetting uAlphaCutoff on the
    /// doodad program once turned every tree into a black rectangle.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "character.vert"),
            Path.Combine(shaderDir, "character.frag"));

        _attached = new AttachedItemRenderer(_gl, _config);
        _attached.LoadShaders(shaderDir);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    public bool Load(string race, string gender)
    {
        Race = race;
        Gender = gender;

        byte[]? bytes = null;
        foreach (string candidate in ModelPathCandidates(race, gender))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null)
            {
                ModelPath = candidate;
                break;
            }
        }

        if (bytes is null)
        {
            Console.WriteLine($"[character] no model found for {race} {gender} - tried " +
                              string.Join(", ", ModelPathCandidates(race, gender)));
            return false;
        }

        var m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid)
        {
            Console.WriteLine($"[character] {ModelPath} parsed to nothing usable");
            return false;
        }

        _m2 = m2;
        float minHeight = float.PositiveInfinity;
        float maxHeight = float.NegativeInfinity;
        foreach (M2Vertex vertex in m2.Vertices)
        {
            float worldZ = Vector3.Transform(
                new Vector3(vertex.PosX, vertex.PosY, vertex.PosZ), ModelToWorld).Z;
            minHeight = MathF.Min(minHeight, worldZ);
            maxHeight = MathF.Max(maxHeight, worldZ);
        }
        _bindPoseHeight = float.IsFinite(minHeight) && float.IsFinite(maxHeight)
            ? MathF.Max(0.3f, maxHeight - MathF.Min(0f, minHeight))
            : 1.8f;
        ResetModelState();   // re-Load safe: drop the previous model before the appending builders run

        Console.WriteLine(
            $"[character] {ModelPath}: {m2.Vertices.Count:N0} verts, {m2.Indices.Count / 3:N0} tris, " +
            $"{m2.Submeshes.Count} geoset(s), {m2.Bones.Count} bone(s), {m2.Sequences.Count} sequence(s)");

        if (m2.Bones.Count > M2Animator.MaxBones)
        {
            BoneOverflow = true;
            Console.WriteLine(
                $"[character] ERROR {m2.Bones.Count} bones exceeds the shader's {M2Animator.MaxBones}.");
            Console.WriteLine(
                "[character] ANIMATION DISABLED - a truncated skeleton renders correctly in bind " +
                "pose and grotesquely in motion, which is a far worse failure than standing still. " +
                $"Raise MaxBones in M2Animator.cs AND MAX_BONES in character.vert to at least " +
                $"{m2.Bones.Count}, together.");
        }

        _animator = M2Animator.Build(m2, BakedAnimations);
        if (_animator is null)
        {
            Console.WriteLine("[character] model has no skeleton - it will draw in bind pose only");
        }
        else
        {
            _animator.ResolutionSink = (unit, track, resolution) =>
                AnimationResolved?.Invoke(unit, track, resolution);
            // Never expose the bind pose for the frame between model load and the first
            // animation update. Stand is also the live upper-body base for the sparse
            // ShuffleLeft/ShuffleRight tracks; without it those tracks restore unkeyed arms
            // to their outstretched bind-pose rotations.
            _clip = _animator.Find(0);
            _clipTime = 0f;
            _animator.TurnBasePose = _clip;
            _skin = new Matrix4x4[_animator.BoneCount];
            _packed = new float[M2Animator.MaxBones * 12];

            var baked = _animator.Clips.Values
                .OrderBy(c => c.AnimationId)
                .Select(c => $"{c.Name} {c.DurationSeconds:F2}s/{c.AnimatedBones}b " +
                             $"move {c.MoveSpeed:F2}");
            Console.WriteLine($"[character] clips: {string.Join(", ", baked)}");

            foreach (int wanted in BakedAnimations)
                if (_animator.Find(wanted) is null)
                    Console.WriteLine($"[character] no usable {M2Animator.AnimationName(wanted)} " +
                                      $"(id {wanted}) in this model");
        }

        BuildTextureSlots(m2);
        BuildGpuBuffers(m2);
        BuildPieces(m2);

        LoadItemDisplay();
        AttachedItemRenderer.ReportAttachments(m2);
        ApplyEquipment();

        return true;
    }

    /// <summary>
    /// Rebuild textures and geosets after an appearance change. Cheap enough to
    /// call from a slider - the M2 and its GPU buffers are untouched.
    /// </summary>
    public void Reload()
    {
        if (_m2 is null) return;

        foreach (var texture in _slots.Select(x => x.Texture)
                     .Where(t => t is not null &&
                                 !ReferenceEquals(t, _bareSkin) &&
                                 !ReferenceEquals(t, _dressedSkin))
                     .Distinct())
            texture!.Dispose();

        _bareSkin?.Dispose();
        _bareSkin = null;
        _dressedSkin?.Dispose();
        _dressedSkin = null;
        _magenta?.Dispose();
        _magenta = null;

        _slots.Clear();
        _bodySlotIndex = -1;
        _baseSkin = null;

        BuildTextureSlots(_m2);
        ApplyEquipment();
    }

    /// <summary>
    /// Queue the race/gender-stable part of an enter-world avatar change. The
    /// selected/offline renderer already owns the model, skeleton and buffers;
    /// only appearance pixels, equipment pixels and their textures are rebuilt.
    /// CPU decoding/composition and GL uploads follow the same worker split as
    /// the S6 creature pipeline. <see cref="PumpAppearanceUpdate"/> performs at
    /// most one state transition per frame and the final main-thread swap is
    /// measured against the shared two-millisecond adoption budget.
    /// </summary>
    public bool QueueAppearanceUpdate(int skinId, int faceId, int hairStyleId,
        int hairColorId, int facialHairId, CharacterEquipment equipment)
    {
        if (_m2 is null) return false;
        if (_workers is null || _uploads is null)
        {
            SkinId = skinId;
            FaceId = faceId;
            HairStyleId = hairStyleId;
            HairColorId = hairColorId;
            FacialHairId = facialHairId;
            Equipment = equipment;
            Reload();
            return true;
        }

        // These catalogues are immutable after load, but their lazy creation is
        // deliberately kept on the owning thread before a worker reads them.
        LoadCharSections();
        LoadCharHairGeosets();
        LoadItemDisplay();

        SkinId = skinId;
        FaceId = faceId;
        HairStyleId = hairStyleId;
        HairColorId = hairColorId;
        FacialHairId = facialHairId;

        var request = new AppearanceRequest(
            skinId, faceId, hairStyleId, hairColorId, facialHairId, equipment);
        _appearanceLoad = new AppearanceLoadJob
        {
            Worker = _workers.Run(() => PrepareAppearanceUpdate(request)),
        };
        return true;
    }

    public bool AppearanceReady => _appearanceLoad is null;

    public void PumpAppearanceUpdate()
    {
        AppearanceLoadJob? job = _appearanceLoad;
        if (job is null || !job.Worker.IsCompleted) return;

        if (job.Ready is null)
        {
            try { job.Ready = job.Worker.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                Console.WriteLine($"[character-prepare] appearance failed: {exception.Message}");
                _appearanceLoad = null;
                return;
            }
            return;
        }

        if (job.Upload is null)
        {
            PreparedAppearanceData ready = job.Ready;
            job.Upload = _uploads!.Enqueue("player-avatar", uploadGl =>
            {
                var textures = new Dictionary<PreparedTextureData, Texture>();
                foreach (PreparedTextureData texture in EnumeratePreparedTextures(ready).Distinct())
                    textures[texture] = Texture.From2D(
                        uploadGl, texture.Pixels, texture.Width, texture.Height,
                        mipmaps: true, repeat: true, ownerGl: _gl);
                return new UploadedAppearanceData { Textures = textures };
            });
            return;
        }

        if (!job.Upload.IsCompleted) return;

        long started = Stopwatch.GetTimestamp();
        try
        {
            FinalizeAppearanceUpdate(job.Ready, job.Upload.GetAwaiter().GetResult());
            Console.WriteLine("[character] async player appearance ready");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[character-upload] appearance failed: {exception.Message}");
        }
        finally
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed > AppearanceFinalizeBudgetMs)
                Console.WriteLine($"[character] player finalize over budget: {elapsed:F2} ms");
            _appearanceLoad = null;
        }
    }

    private readonly record struct AppearanceRequest(
        int SkinId, int FaceId, int HairStyleId, int HairColorId, int FacialHairId,
        CharacterEquipment Equipment);

    private PreparedAppearanceData PrepareAppearanceUpdate(in AppearanceRequest request)
    {
        M2Model m2 = _m2 ?? throw new InvalidOperationException("character model is not loaded");
        uint raceId = CharSectionsTable.RaceId(Race);
        uint sexId = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
        string skinPath = "";
        string hairPath = "";
        string facialHairPath = "";
        var overlays = new List<(string Path, FaceRegion Region)>();

        if (_charSections is not null)
        {
            var skinRow = _charSections.Find(
                raceId, sexId, CharSectionsTable.SectionSkin, -1, request.SkinId);
            if (skinRow is not null) skinPath = skinRow.Texture1;

            var faceRow = _charSections.Find(
                raceId, sexId, CharSectionsTable.SectionFace, request.FaceId, request.SkinId);
            if (faceRow is not null)
            {
                if (faceRow.Texture1.Length > 0) overlays.Add((faceRow.Texture1, FaceRegion.Lower));
                if (faceRow.Texture2.Length > 0) overlays.Add((faceRow.Texture2, FaceRegion.Upper));
            }

            var hairRow = _charSections.Find(
                raceId, sexId, CharSectionsTable.SectionHair,
                request.HairStyleId, request.HairColorId);
            if (hairRow is not null) hairPath = hairRow.Texture1;
            if (hairPath.Length == 0)
            {
                var substitute = _charSections.Find(
                    raceId, sexId, CharSectionsTable.SectionHair,
                    HairSubstituteVariation, request.HairColorId);
                if (substitute is not null) hairPath = substitute.Texture1;
            }

            var facialRow = _charSections.Find(
                raceId, sexId, CharSectionsTable.SectionFacialHair,
                request.FacialHairId, request.HairColorId);
            if (facialRow is not null)
            {
                facialHairPath = facialRow.Texture1;
                if (facialRow.Texture1.Length > 0) overlays.Add((facialRow.Texture1, FaceRegion.Lower));
                if (facialRow.Texture2.Length > 0) overlays.Add((facialRow.Texture2, FaceRegion.Upper));
            }
            if (hairRow is not null)
            {
                if (hairRow.Texture2.Length > 0) overlays.Add((hairRow.Texture2, FaceRegion.Lower));
                if (hairRow.Texture3.Length > 0) overlays.Add((hairRow.Texture3, FaceRegion.Upper));
            }
        }

        var decodedByPath = new Dictionary<string, PreparedTextureData>(StringComparer.OrdinalIgnoreCase);
        PreparedTextureData? bare = null;
        float skinCutoff = 0.35f;
        string usedSkinPath = "";
        foreach (string candidate in SkinCandidates(skinPath, Race, Gender))
        {
            bare = DecodePreparedTexture(candidate, decodedByPath, out skinCutoff);
            if (bare is null) continue;
            usedSkinPath = candidate;
            break;
        }

        byte[]? baseSkin = bare?.Pixels.ToArray();
        if (bare is not null && baseSkin is not null)
        {
            foreach ((string path, FaceRegion region) in overlays)
            {
                PreparedTextureData? overlay = null;
                foreach (string candidate in CharacterTextureCandidates(path))
                {
                    overlay = DecodePreparedTexture(candidate, decodedByPath, out _);
                    if (overlay is not null) break;
                }
                if (overlay is null) continue;
                var (x, y, rw, rh) = region == FaceRegion.Upper
                    ? (0, 160, 128, 32)
                    : (0, 192, 128, 64);
                float sx = bare.Width / 256f, sy = bare.Height / 256f;
                CharacterEquipment.BlitOver(
                    baseSkin, bare.Width, bare.Height,
                    overlay.Pixels, overlay.Width, overlay.Height,
                    (int)(x * sx), (int)(y * sy), (int)(rw * sx), (int)(rh * sy));
            }
            bare = new PreparedTextureData
            {
                Pixels = baseSkin,
                Width = bare.Width,
                Height = bare.Height,
            };
        }

        request.Equipment.GenderSuffix =
            Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
        request.Equipment.Resolve(_itemDisplay);
        PreparedTextureData? dressed = null;
        if (bare is not null && request.Equipment.Pieces.Count > 0)
        {
            byte[] pixels = request.Equipment.Composite(
                bare.Pixels, bare.Width, bare.Height,
                path => DecodePixels(path, decodedByPath));
            dressed = new PreparedTextureData
            {
                Pixels = pixels,
                Width = bare.Width,
                Height = bare.Height,
            };
        }

        PreparedTextureData? cape = PrepareCapeTexture(request.Equipment, decodedByPath);
        var slots = new List<PreparedSlotData>(m2.Textures.Count);
        int bodySlot = -1;
        int unbound = 0;
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            var reference = m2.Textures[i];
            var slot = new PreparedSlotData { Type = reference.Type };
            string external = reference.Type switch
            {
                6 => hairPath,
                7 => facialHairPath,
                _ => "",
            };

            if (!string.IsNullOrWhiteSpace(reference.Filename))
            {
                slot.Texture = DecodePreparedTexture(reference.Filename, decodedByPath, out float cutoff);
                slot.AlphaCutoff = cutoff;
                if (slot.Texture is not null)
                {
                    slot.Fill = SlotFill.Bound;
                    slot.Source = reference.Filename;
                }
            }
            else if (external.Length > 0)
            {
                foreach (string candidate in CharacterTextureCandidates(external))
                {
                    slot.Texture = DecodePreparedTexture(candidate, decodedByPath, out float cutoff);
                    if (slot.Texture is null) continue;
                    slot.AlphaCutoff = cutoff;
                    slot.Fill = SlotFill.Bound;
                    slot.Source = candidate;
                    break;
                }
            }
            else if (reference.Type == 1 && bare is not null)
            {
                if (bodySlot < 0) bodySlot = i;
                slot.Texture = dressed ?? bare;
                slot.AlphaCutoff = skinCutoff;
                slot.Fill = SlotFill.BodySkin;
                slot.Source = usedSkinPath;
            }
            else if (reference.Type == 2 && cape is not null)
            {
                slot.Texture = cape;
                slot.Fill = SlotFill.Bound;
                slot.Source = "equipped cloak";
            }

            if (slot.Fill == SlotFill.Unbound && reference.Type == 6)
            {
                foreach (string candidate in HairFallbackCandidates())
                {
                    slot.Texture = DecodePreparedTexture(candidate, decodedByPath, out float cutoff);
                    if (slot.Texture is null) continue;
                    slot.AlphaCutoff = cutoff;
                    slot.Fill = SlotFill.Bound;
                    slot.Source = candidate;
                    break;
                }
            }
            if (slot.Fill == SlotFill.Unbound) unbound++;
            slots.Add(slot);
        }

        return new PreparedAppearanceData
        {
            Equipment = request.Equipment,
            Slots = slots,
            Magenta = new PreparedTextureData { Pixels = [255, 0, 255, 255], Width = 1, Height = 1 },
            BareSkin = bare,
            DressedSkin = dressed,
            BaseSkin = baseSkin,
            SkinWidth = bare?.Width ?? 0,
            SkinHeight = bare?.Height ?? 0,
            SkinCutoff = skinCutoff,
            SkinPath = usedSkinPath,
            BodySlotIndex = bodySlot,
            UnboundSlots = unbound,
        };
    }

    private PreparedTextureData? PrepareCapeTexture(CharacterEquipment equipment,
        Dictionary<string, PreparedTextureData> cache)
    {
        var cloak = equipment.Pieces.LastOrDefault(piece =>
            piece.InventoryType == CharacterEquipment.Slot.Cloak && piece.Row is not null);
        if (cloak?.Row is null) return null;
        foreach (string name in new[] { cloak.Row.ModelTexture1, cloak.Row.ModelTexture2 }
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string candidate in CapeTextureCandidates(name))
                if (DecodePreparedTexture(candidate, cache, out _) is { } decoded) return decoded;
        return null;
    }

    private (byte[] bgra, int w, int h)? DecodePixels(string path,
        Dictionary<string, PreparedTextureData> cache)
        => DecodePreparedTexture(path, cache, out _) is { } decoded
            ? (decoded.Pixels, decoded.Width, decoded.Height)
            : null;

    private PreparedTextureData? DecodePreparedTexture(string path,
        Dictionary<string, PreparedTextureData> cache, out float alphaCutoff)
    {
        alphaCutoff = 0.35f;
        if (cache.TryGetValue(path, out PreparedTextureData? cached))
        {
            alphaCutoff = ComputeAlphaCutoff(cached.Pixels);
            return cached;
        }
        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
        if (decoded is null) return null;
        var (pixels, width, height) = decoded.Value;
        if (pixels.Length < 4 || width <= 0 || height <= 0) return null;
        alphaCutoff = ComputeAlphaCutoff(pixels);
        var result = new PreparedTextureData { Pixels = pixels, Width = width, Height = height };
        cache[path] = result;
        return result;
    }

    private static float ComputeAlphaCutoff(byte[] pixels)
    {
        byte maxAlpha = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > maxAlpha) maxAlpha = pixels[i];
        if (maxAlpha == 0) return 0f;
        if (maxAlpha == 1)
            for (int i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 0) pixels[i] = 255;
        return 0.35f;
    }

    private static IEnumerable<PreparedTextureData> EnumeratePreparedTextures(
        PreparedAppearanceData prepared)
    {
        if (prepared.BareSkin is not null) yield return prepared.BareSkin;
        if (prepared.DressedSkin is not null) yield return prepared.DressedSkin;
        foreach (PreparedSlotData slot in prepared.Slots)
            if (slot.Texture is not null) yield return slot.Texture;
        yield return prepared.Magenta;
    }

    private void FinalizeAppearanceUpdate(
        PreparedAppearanceData prepared, UploadedAppearanceData uploaded)
    {
        Texture[] oldTextures = _slots.Select(slot => slot.Texture)
            .Where(texture => texture is not null)
            .Append(_bareSkin)
            .Append(_dressedSkin)
            .Append(_magenta)
            .Where(texture => texture is not null)
            .Select(texture => texture!)
            .Distinct()
            .ToArray();

        _slots.Clear();
        foreach (PreparedSlotData preparedSlot in prepared.Slots)
            _slots.Add(new Slot
            {
                Type = preparedSlot.Type,
                Fill = preparedSlot.Fill,
                Source = preparedSlot.Source,
                AlphaCutoff = preparedSlot.AlphaCutoff,
                Texture = preparedSlot.Texture is not null
                    ? uploaded.Textures[preparedSlot.Texture]
                    : null,
            });

        _baseSkin = prepared.BaseSkin;
        _skinWidth = prepared.SkinWidth;
        _skinHeight = prepared.SkinHeight;
        _skinCutoff = prepared.SkinCutoff;
        _bodySlotIndex = prepared.BodySlotIndex;
        SkinTexturePath = prepared.SkinPath.Length > 0 ? prepared.SkinPath : "(none)";
        UnboundSlots = prepared.UnboundSlots;
        _bareSkin = prepared.BareSkin is not null ? uploaded.Textures[prepared.BareSkin] : null;
        _dressedSkin = prepared.DressedSkin is not null ? uploaded.Textures[prepared.DressedSkin] : null;
        _magenta = uploaded.Textures[prepared.Magenta];
        Equipment = prepared.Equipment;
        ApplyGeosetVisibility();
        if (_attached is not null)
        {
            _attached.RaceGenderCode = RaceGenderCode(Race, Gender);
            _attached.Rebuild(Equipment);
        }

        foreach (Texture texture in oldTextures) texture.Dispose();
    }

    /// <summary>
    /// Read ItemDisplayInfo.dbc out of the MPQs. Non-fatal: without it the
    /// character is simply undressed, which is what it was already.
    /// </summary>
    private void LoadItemDisplay()
    {
        if (_itemDisplay is not null) return;

        var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, ItemDisplayTable.MpqPath);
        if (bytes is null)
        {
            Console.WriteLine($"[dbc] {ItemDisplayTable.MpqPath} not found in the MPQs - no gear");
            return;
        }

        _itemDisplay = ItemDisplayTable.Parse(bytes);
    }

    /// <summary>
    /// Resolve the equipped pieces, repaint the body atlas and redo geoset
    /// visibility. Safe to call again after changing <see cref="Equipment"/>.
    /// </summary>
    public void ApplyEquipment()
    {
        Equipment.GenderSuffix = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
        Equipment.Resolve(_itemDisplay);
        BindCapeTexture();

        if (_baseSkin is not null && Equipment.Pieces.Count == 0)
        {
            foreach (var slot in _slots)
                if (slot.Fill == SlotFill.BodySkin) slot.Texture = _bareSkin;

            _dressedSkin?.Dispose();
            _dressedSkin = null;
        }
        else if (_baseSkin is not null && Equipment.Pieces.Count > 0)
        {
            var composited = Equipment.Composite(
                _baseSkin, _skinWidth, _skinHeight,
                path => AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path));

            var texture = Texture.From2D(_gl, composited, _skinWidth, _skinHeight);

            // Type-1 slots follow the dressed atlas. Hair/scalp exceptions are
            // selected per draw below, because category 0 contains both the
            // actual body and hairstyle meshes that share this texture slot.
            foreach (var slot in _slots)
                if (slot.Fill == SlotFill.BodySkin) slot.Texture = texture;

            _dressedSkin?.Dispose();
            _dressedSkin = texture;
            _ = _bodySlotIndex;
        }

        ApplyGeosetVisibility();
        if (_attached is not null)
        {
            _attached.RaceGenderCode = RaceGenderCode(Race, Gender);
            _attached.Rebuild(Equipment);
        }
    }

    /// <summary>
    /// Cloaks are not attached item models. The character M2 already contains
    /// the cloth as geoset 1502; ItemDisplayInfo.ModelTexture supplies the BLP
    /// for its replaceable type-2 (OBJECT_SKIN) texture slot.
    /// </summary>
    private void BindCapeTexture()
    {
        var capeSlots = _slots.Where(slot => slot.Type == 2).ToList();
        if (capeSlots.Count == 0) return;

        // ApplyEquipment may be called repeatedly from the equipment UI.
        foreach (var texture in capeSlots
                     .Where(slot => slot.Fill == SlotFill.Bound && slot.Texture is not null)
                     .Select(slot => slot.Texture!)
                     .Distinct())
            texture.Dispose();

        foreach (var slot in capeSlots)
        {
            slot.Texture = null;
            slot.Fill = SlotFill.Unbound;
            slot.Source = "";
            slot.AlphaCutoff = 0.35f;
        }

        var cloak = Equipment.Pieces.LastOrDefault(piece =>
            piece.InventoryType == CharacterEquipment.Slot.Cloak && piece.Row is not null);
        if (cloak?.Row is null) return;

        var names = new[] { cloak.Row.ModelTexture1, cloak.Row.ModelTexture2 }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Texture? capeTexture = null;
        float capeCutoff = 0.35f;
        string source = "";

        foreach (string name in names)
        {
            foreach (string candidate in CapeTextureCandidates(name))
            {
                capeTexture = MakeTexture(candidate, out capeCutoff);
                if (capeTexture is null) continue;
                source = candidate;
                break;
            }
            if (capeTexture is not null) break;
        }

        if (capeTexture is null)
        {
            Console.WriteLine($"[character] cloak '{cloak.Name}' has no resolvable cape texture " +
                              $"('{cloak.Row.ModelTexture1}', '{cloak.Row.ModelTexture2}')");
            return;
        }

        foreach (var slot in capeSlots)
        {
            slot.Texture = capeTexture;
            slot.Fill = SlotFill.Bound;
            slot.Source = source;
            slot.AlphaCutoff = capeCutoff;
        }

        Console.WriteLine($"[character] cloak '{cloak.Name}' -> type-2 slot(s): {source}");
    }

    private IEnumerable<string> CapeTextureCandidates(string partial)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        bool hasDirectory = stem.Contains('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        if (hasDirectory)
        {
            yield return stem + ".blp";
            yield break;
        }

        string suffix = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
        yield return $@"Item\ObjectComponents\Cape\{stem}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_U.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_U.blp";
    }

    /// <summary>
    /// Two-letter race code plus M or F. Helm models are per race and gender -
    /// a helm has to fit the head it sits on - so the file name carries this
    /// suffix where shoulders and weapons do not.
    /// </summary>
    private static string RaceGenderCode(string race, string gender)
    {
        string r = race.ToLowerInvariant() switch
        {
            "human" => "Hu",
            "orc" => "Or",
            "dwarf" => "Dw",
            "nightelf" => "Ni",
            "scourge" or "undead" => "Sc",
            "tauren" => "Ta",
            "gnome" => "Gn",
            "troll" => "Tr",
            _ => "Hu",
        };

        return r + (gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M");
    }

    /// <summary>
    /// Vanilla character models live at Character\Race\Gender\RaceGender.m2.
    /// The .mdx variants are tried for the same reason DoodadRenderer tries
    /// them: vanilla tooling is inconsistent about which extension it records.
    /// </summary>
    private static IEnumerable<string> ModelPathCandidates(string race, string gender)
    {
        string stem = $"Character\\{race}\\{gender}\\{race}{gender}";
        yield return stem + ".m2";
        yield return stem + ".M2";
        yield return stem + ".mdx";
    }

    /// <summary>
    /// Resolve one image per texture SLOT, not per geoset, so slots are shared.
    ///
    /// M2 texture types, from the vanilla table:
    ///     0  the slot names a BLP and we just read it
    ///     1  CHAR_SKIN        the body atlas - supplied by the application
    ///     2  OBJECT_SKIN      cape or item texture - nothing equipped yet
    ///     6  CHAR_HAIR        supplied by the application
    ///     7  CHAR_FACIAL_HAIR supplied by the application
    ///     8  SKIN_EXTRA       supplied by the application
    ///
    /// Everything except type 0 is normally driven by CharSections.dbc, which
    /// this client does not read yet. Until it does, this resolves the base
    /// skin by filename convention and logs every slot it could not bind. That
    /// logging is not decoration: SuperUI's writer spent a long time silently
    /// falling back to the body atlas for every unresolved slot, which rendered
    /// plausibly - hair textured like skin - and hid the real error underneath.
    /// </summary>
    /// <summary>
    /// Resolve every texture slot the model asks for.
    ///
    /// SLOTS ARE FILLED BY TYPE, AND THE TYPES DO NOT SHARE A SOURCE. This is
    /// the whole thing, and getting it wrong is the "hair textures like skin"
    /// bug: type 6 is hair and must get the CharSections HAIR texture, not the
    /// body atlas. Pointing every empty slot at the skin renders plausibly and
    /// is wrong everywhere it matters.
    ///
    ///   type 0  the slot names a BLP - just read it
    ///   type 1  CHAR_SKIN        the body atlas, composited below
    ///   type 2  OBJECT_SKIN      a cape or item texture; nothing until one is worn
    ///   type 6  CHAR_HAIR        CharSections section 3, by hair style and colour
    ///   type 7  CHAR_FACIAL_HAIR CharSections section 2
    ///   type 8  SKIN_EXTRA       CharSections section 4, the underwear
    ///
    /// AND THE FACE IS NOT A SLOT AT ALL. Most races' body skin BLP has no eye
    /// detail whatsoever - the eyes live in a CharSections Face row that gets
    /// composited onto the atlas. Miss that and the character renders
    /// blank-faced, which looks exactly like "eyes closed" and sends you
    /// hunting through geosets for something that was never there.
    /// </summary>
    private void BuildTextureSlots(M2Model m2)
    {
        LoadCharSections();
        LoadCharHairGeosets();

        uint raceId = CharSectionsTable.RaceId(Race);
        uint sexId = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;

        string skinPath = "";
        string hairPath = "";
        string facialHairPath = "";
        // (path, region). CharSections TELLS US which is which - Texture1 is the
        // lower face and Texture2 the upper - and the first version threw that
        // away into a flat list and then tried to infer the region back from
        // the image height. Guessing at something you were handed is how the
        // face ended up painted across the eyes.
        var overlays = new List<(string Path, FaceRegion Region)>();

        if (_charSections is not null)
        {
            var skinRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionSkin, -1, SkinId);
            if (skinRow is not null) skinPath = skinRow.Texture1;

            // Face: matched on face shape AND skin tone. Texture1 is the lower
            // face, Texture2 the upper - and the upper is where the eyes are.
            var faceRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionFace, FaceId, SkinId);
            if (faceRow is not null)
            {
                if (faceRow.Texture1.Length > 0) overlays.Add((faceRow.Texture1, FaceRegion.Lower));
                if (faceRow.Texture2.Length > 0) overlays.Add((faceRow.Texture2, FaceRegion.Upper));
                Console.WriteLine($"[character] face lower '{faceRow.Texture1}' upper '{faceRow.Texture2}'");
            }
            else
            {
                Console.WriteLine($"[character] no CharSections Face row for race {raceId} sex {sexId} " +
                                  $"face {FaceId} skin {SkinId} - the character will be blank-faced");
            }

            var hairRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionHair, HairStyleId, HairColorId);
            if (hairRow is not null) hairPath = hairRow.Texture1;

            // BLANK HAIR ROW -> VARIATION 1 AT THE SAME COLOUR (benilla sections.rs hair_mesh_texture).
            // The client has no fallback LOOKUP: its single type-6 binder reads TextureName[0] and an
            // EMPTY name is a NO-OP that leaves the slot untouched, and the three call sites run in
            // build order skin -> hairStyle -> facialHair with two of them passing variation LITERAL 1.
            // The fixpoint is "take the hairStyle row; when it is blank take variation 1 at the same
            // colour". That literal 1 is load-bearing where a race authors several sheets per colour -
            // Human MALE variation 0 is the only fully blank row and resolves to Hair03_<colour>, NOT
            // to the Hair00_00 the old convention fallback below would have picked (that is the FEMALE
            // sheet at colour 0 - wrong style and wrong colour).
            if (hairPath.Length == 0)
            {
                var hairSubstitute = _charSections.Find(raceId, sexId, CharSectionsTable.SectionHair,
                                                        HairSubstituteVariation, HairColorId);
                if (hairSubstitute is not null && hairSubstitute.Texture1.Length > 0)
                {
                    hairPath = hairSubstitute.Texture1;
                    Console.WriteLine($"[character] hair style {HairStyleId} names no sheet - benilla " +
                                      $"substitute variation {HairSubstituteVariation} -> '{hairPath}'");
                }
            }

            var facialRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionFacialHair, FacialHairId, HairColorId);
            if (facialRow is not null)
            {
                facialHairPath = facialRow.Texture1;

                // Composite the FACIAL-HAIR section onto the face tiles too, ON TOP of the face
                // overlays (build order face -> facial hair, benilla sections.rs composite_body).
                // This is where the EYEBROWS live: the section's UPPER strip (Texture2) is the brow
                // row, keyed by HAIR colour (not skin - that is why the brows match the hair). The
                // LOWER strip (Texture1) is the flat facial-hair underlay. Both blend over the eyes
                // via the alpha-aware BlitOver rather than erasing them. Without this the eyes
                // composite but the brows never paint - the "no eyebrows" bug. Added after the face
                // block so the list order is face(lower,upper) -> facial(lower,upper).
                if (facialRow.Texture1.Length > 0) overlays.Add((facialRow.Texture1, FaceRegion.Lower));
                if (facialRow.Texture2.Length > 0) overlays.Add((facialRow.Texture2, FaceRegion.Upper));
                Console.WriteLine($"[character] facial-hair lower '{facialRow.Texture1}' upper '{facialRow.Texture2}' (brows/underlay, hair-coloured)");
            }

            // THE HAIRLINE - the SCALP strips (fixed 2026-07-29). benilla composite_body's head fan-out
            // is skin -> face -> facial hair -> HAIR, and that last pair is the CharSections HAIR row's
            // OTHER two columns: Texture2 = ScalpLower<style>_<colour> -> the LOWER face tile, Texture3 =
            // ScalpUpper<style>_<colour> -> the UPPER tile (benilla sections.rs: SECTION_HAIR col 1 ->
            // TILE_G9, col 2 -> TILE_G8). They paint the hairline and the scalp shading onto the head
            // itself, UNDER the hair mesh. Without them the hair geoset's scalp submesh - which samples
            // the body atlas, not the hair sheet - is bare skin, so the hair has no painted root line and
            // dissolves into the forehead. That was Nico's "the hair blends into the forehead" (ours vs
            // 1.12, screenshot 2026-07-29).
            //
            // NOTE FOR THE NEXT READER: benilla's own comment on this claims the hair columns are empty
            // for Human male and that the overlay is only there for other races. THAT IS WRONG on 1.12.1
            // data - verified by decoding CharSections.dbc out of GameData/Data/dbc.MPQ: every Human male
            // hair variation 1-11 carries ScalpLowerHair0x_00 / ScalpUpperHair0x_00, and only the blank
            // variation 0 has none. Do not skip this overlay on the strength of that comment.
            //
            // Appended AFTER the facial-hair pair so the list order is face -> facial hair -> hair,
            // exactly benilla's; within a tile the later entry blends over the earlier one.
            if (hairRow is not null)
            {
                if (hairRow.Texture2.Length > 0) overlays.Add((hairRow.Texture2, FaceRegion.Lower));
                if (hairRow.Texture3.Length > 0) overlays.Add((hairRow.Texture3, FaceRegion.Upper));
                if (hairRow.Texture2.Length > 0 || hairRow.Texture3.Length > 0)
                    Console.WriteLine($"[character] scalp lower '{hairRow.Texture2}' upper '{hairRow.Texture3}' (the hairline)");
            }
        }

        // Fall back to the filename convention when the table is missing or the
        // ids do not match a row.
        Texture? skin = null;
        float skinCutoff = 0.35f;
        string usedSkinPath = "";

        foreach (string candidate in SkinCandidates(skinPath, Race, Gender))
        {
            var made = MakeTexture(candidate, out float cutoff, out var pixels, out int w, out int h);
            if (made is null) continue;

            skin = made;
            usedSkinPath = candidate;
            skinCutoff = cutoff;
            _baseSkin = pixels;
            _skinWidth = w;
            _skinHeight = h;
            break;
        }

        SkinTexturePath = usedSkinPath.Length > 0 ? usedSkinPath : "(none)";
        _skinCutoff = skinCutoff;

        if (skin is null)
        {
            Console.WriteLine($"[character] no body skin BLP found for {Race} {Gender}");
        }
        else
        {
            Console.WriteLine($"[character] body skin {usedSkinPath}");

            // Paint the face and underwear into the base BEFORE any gear, so
            // re-equipping composites onto a face rather than erasing it.
            if (overlays.Count > 0 && _baseSkin is not null)
            {
                int painted = ApplyAppearanceOverlays(overlays);
                skin.Dispose();
                skin = Texture.From2D(_gl, _baseSkin, _skinWidth, _skinHeight);
                Console.WriteLine($"[character] composited {painted}/{overlays.Count} appearance overlay(s) onto the skin");
            }
        }

        // Keep the correctly composed bare atlas alive. Dressed body pieces
        // use a second texture, while hairstyle scalp and ear geosets must keep
        // sampling this one or armor regions bleed across the head.
        _bareSkin = skin;

        UnboundSlots = 0;

        for (int i = 0; i < m2.Textures.Count; i++)
        {
            var reference = m2.Textures[i];
            var slot = new Slot { Type = reference.Type };

            string external = reference.Type switch
            {
                6 => hairPath,
                7 => facialHairPath,
                _ => "",
            };

            if (!string.IsNullOrWhiteSpace(reference.Filename))
            {
                slot.Texture = MakeTexture(reference.Filename, out float cutoff);
                slot.AlphaCutoff = cutoff;
                if (slot.Texture is not null) { slot.Fill = SlotFill.Bound; slot.Source = reference.Filename; }
            }
            else if (external.Length > 0)
            {
                foreach (string candidate in CharacterTextureCandidates(external))
                {
                    slot.Texture = MakeTexture(candidate, out float cutoff);
                    if (slot.Texture is null) continue;
                    slot.AlphaCutoff = cutoff;
                    slot.Fill = SlotFill.Bound;
                    slot.Source = candidate;
                    break;
                }
            }
            else if (reference.Type == 1 && skin is not null)
            {
                if (reference.Type == 1 && _bodySlotIndex < 0) _bodySlotIndex = i;
                slot.Texture = skin;
                slot.AlphaCutoff = skinCutoff;
                slot.Fill = SlotFill.BodySkin;
                slot.Source = usedSkinPath;
            }

            // LAST DITCH. A hair (type 6) slot with no CharSections row at all must NOT be left to
            // sample a stale/dressed atlas (armour bleeds onto the head), so fall back to the race
            // convention BLP. This is NOT what benilla does and it is not the blank-row path any
            // more - a blank hairStyle row is handled properly above (variation 1 at the same
            // colour). This only fires when the row is missing outright, e.g. a broken DBC.
            if (slot.Fill == SlotFill.Unbound && reference.Type == 6)
            {
                foreach (string candidate in HairFallbackCandidates())
                {
                    slot.Texture = MakeTexture(candidate, out float cutoff);
                    if (slot.Texture is null) continue;
                    slot.AlphaCutoff = cutoff;
                    slot.Fill = SlotFill.Bound;
                    slot.Source = candidate;
                    break;
                }
            }

            if (slot.Fill == SlotFill.Unbound) UnboundSlots++;

            _slots.Add(slot);

            Console.WriteLine(
                $"[character] texslot {i}: type={reference.Type} file='{reference.Filename}' " +
                $"-> {slot.Fill}" + (slot.Source.Length > 0 ? $" {slot.Source}" : ""));
        }

        if (UnboundSlots > 0)
            Console.WriteLine($"[character] {UnboundSlots} texture slot(s) unbound - " +
                              "tick 'Magenta unbound' to see which geosets they are");

        // 1x1 BGRA magenta. Deliberately impossible to overlook.
        _magenta = Texture.From2D(_gl, [255, 0, 255, 255], 1, 1);
    }

    /// <summary>benilla's type-6 substitute variation: when a hairStyle row names no sheet, the client's
    /// incremental apply leaves whatever variation 1 bound at the same colour (sections.rs
    /// hair_mesh_texture / HAIR_SUBSTITUTE_VARIATION).</summary>
    private const int HairSubstituteVariation = 1;

    /// <summary>Last-ditch hair file when CharSections has no row for this race/sex at all.</summary>
    private IEnumerable<string> HairFallbackCandidates()
    {
        yield return $"Character\\{Race}\\Hair00_00.blp";
        yield return $"Character\\{Race}\\{Gender}\\Hair00_00.blp";
        yield return $"Character\\{Race}\\{Gender}\\{Race}{Gender}Hair00_00.blp";
    }

    /// <summary>
    /// The vanilla blend modes that matter, mapped to GL. Anything unrecognised
    /// falls back to straight alpha, which is the safe wrong answer - visible
    /// and roughly right rather than invisible or blindingly additive.
    /// </summary>
    private void ApplyBlendMode(int mode)
    {
        switch (mode)
        {
            case 3:     // Add
            case 4:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 5:     // Mod
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case 6:     // Mod2x
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
                break;
            default:    // 2 Alpha, and anything unknown
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    /// <summary>
    /// Report any two VISIBLE pieces whose index ranges overlap.
    ///
    /// This is the definitive test for the flicker, not another theory about
    /// which categories might be fighting. Two draws that share triangles are
    /// literally the same surface submitted twice, and no depth function can
    /// order a surface against itself - it is z-fighting by construction.
    ///
    /// Silence means the geometry is disjoint and the flicker is coming from
    /// somewhere else: coplanar-but-distinct meshes, or something outside the
    /// character entirely.
    /// </summary>
    private void ReportOverlaps()
    {
        var visible = _pieces.Where(p => p.Visible).ToList();
        int reported = 0;

        for (int a = 0; a < visible.Count; a++)
        {
            for (int b = a + 1; b < visible.Count; b++)
            {
                // Several batches over one submesh are intentional material
                // layers, not duplicate geosets. Their authored priority/layer
                // order is exactly why the renderer preserves every batch.
                if (visible[a].SubmeshIndex == visible[b].SubmeshIndex) continue;

                uint aStart = visible[a].IndexStart, aEnd = aStart + visible[a].IndexCount;
                uint bStart = visible[b].IndexStart, bEnd = bStart + visible[b].IndexCount;

                if (aStart >= bEnd || bStart >= aEnd) continue;

                if (reported++ == 0)
                    Console.WriteLine("[character] OVERLAPPING DRAWS - the same triangles are being " +
                                      "submitted more than once, which IS the flicker:");

                Console.WriteLine(
                    $"[character]   geoset {visible[a].GeosetId} [{aStart}..{aEnd}) overlaps " +
                    $"geoset {visible[b].GeosetId} [{bStart}..{bEnd})");

                if (reported >= 12) { Console.WriteLine("[character]   ... more"); return; }
            }
        }

        if (reported == 0)
            Console.WriteLine($"[character] {visible.Count} visible batch(es), no unintended overlapping index ranges");

        var blended = visible.Where(p => p.Transparent).ToList();
        Console.WriteLine(
            $"[character] draw split: {visible.Count - blended.Count} opaque, {blended.Count} blended" +
            (blended.Count > 0
                ? " -> " + string.Join(" ", blended.Select(p => $"{p.GeosetId}(mode {p.BlendMode}{(p.NoZWrite ? ",noZ" : "")})"))
                : ""));
    }

    /// <summary>
    /// Paint the two CharSections face layers into their canonical atlas
    /// rectangles. Underwear is a separate 128x64 pelvis component and must
    /// never be stretched across the complete body texture.
    /// </summary>
    private enum FaceRegion { Lower, Upper }

    private int ApplyAppearanceOverlays(List<(string Path, FaceRegion Region)> paths)
    {
        if (_baseSkin is null) return 0;

        int painted = 0;

        foreach (var (path, region) in paths)
        {
            (byte[] bgra, int w, int h)? decoded = null;
            string used = "";

            foreach (string candidate in CharacterTextureCandidates(path))
            {
                decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, candidate);
                if (decoded is not null) { used = candidate; break; }
            }

            if (decoded is null)
            {
                Console.WriteLine($"[character] appearance overlay '{path}' not found");
                continue;
            }

            var (bgra, w, h) = decoded.Value;

            // CharSections tells us which face strip this is. Match the working
            // SuperUI compositor and always paint into that canonical region;
            // never infer a full-atlas replacement from image dimensions.
            var (x, y, rw, rh) = region == FaceRegion.Upper
                ? (0, 160, 128, 32)
                : (0, 192, 128, 64);

            float sx = _skinWidth / 256f, sy = _skinHeight / 256f;

            CharacterEquipment.BlitOver(_baseSkin, _skinWidth, _skinHeight, bgra, w, h,
                                        (int)(x * sx), (int)(y * sy), (int)(rw * sx), (int)(rh * sy));
            Console.WriteLine($"[character] overlay {used} -> face {region} ({x},{y},{rw},{rh}) from {w}x{h}");

            painted++;
        }

        return painted;
    }

    /// <summary>
    /// CharSections stores partial paths, sometimes with the extension and
    /// sometimes without. Same candidate treatment SuperUI's compositor uses.
    /// </summary>
    private IEnumerable<string> CharacterTextureCandidates(string partial)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        yield return stem + ".blp";
        yield return $@"Character\{stem}.blp";
        yield return $@"Character\{Race}\{Gender}\{stem}.blp";
    }

    private static IEnumerable<string> SkinCandidates(string fromDbc, string race, string gender)
    {
        if (fromDbc.Length > 0)
        {
            string stem = fromDbc;
            if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            yield return stem + ".blp";
        }

        foreach (string candidate in SkinPathCandidates(race, gender)) yield return candidate;
    }

    private void LoadCharSections()
    {
        if (_charSections is not null) return;

        var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, CharSectionsTable.MpqPath);
        if (bytes is null)
        {
            Console.WriteLine($"[dbc] {CharSectionsTable.MpqPath} not found - no face, no hair colour");
            return;
        }

        _charSections = CharSectionsTable.Parse(bytes);
    }

    private void LoadCharHairGeosets()
    {
        if (_characterGeosets is not null) return;

        var hairBytes = AdtTerrainReader.ReadFileFromMpqs(
            _config.ClientDataPath, CharHairGeosetsTable.MpqPath);
        if (hairBytes is null)
        {
            Console.WriteLine($"[dbc] {CharHairGeosetsTable.MpqPath} not found - hairstyle mesh will use a fallback");
        }
        else _charHairGeosets = CharHairGeosetsTable.Parse(hairBytes);

        var facialBytes = AdtTerrainReader.ReadFileFromMpqs(
            _config.ClientDataPath, CharacterFacialHairTable.MpqPath);
        var helmetBytes = AdtTerrainReader.ReadFileFromMpqs(
            _config.ClientDataPath, HelmetGeosetVisTable.MpqPath);
        _characterGeosets = new CharacterGeosets(
            _charHairGeosets,
            facialBytes is null ? null : CharacterFacialHairTable.Parse(facialBytes),
            helmetBytes is null ? null : HelmetGeosetVisTable.Parse(helmetBytes));
    }

    private static IEnumerable<string> SkinPathCandidates(string race, string gender)
    {
        string dir = $"Character\\{race}\\{gender}\\{race}{gender}";
        for (int variant = 0; variant < 4; variant++)
            yield return $"{dir}Skin00_{variant:00}.blp";

        yield return $"{dir}Skin00.blp";
    }

    /// <summary>
    /// Decode a BLP and pick its alpha cutoff, guarding the 1-bit case.
    ///
    /// BlpDecoder returns 1-bit alpha as 0 or 1 rather than 0 or 255. In the
    /// shader that is 0.004, which fails any sensible cut on every texel, so the
    /// surface loads, textures correctly, and renders as nothing at all. That is
    /// what made Goldshire's walls disappear and it survived two wrong
    /// diagnoses. Character models lean on alpha for hair, eyelashes and cloth
    /// edges, so it would land here too.
    ///
    /// THE PROPER FIX BELONGS IN BlpDecoder. This is the same point-of-use guard
    /// WmoRenderer carries, and both should be deleted the day that lands.
    /// </summary>
    private Texture? MakeTexture(string blpPath, out float alphaCutoff)
        => MakeTexture(blpPath, out alphaCutoff, out _, out _, out _);

    private Texture? MakeTexture(string blpPath, out float alphaCutoff,
                                 out byte[]? pixels, out int width, out int height)
    {
        alphaCutoff = 0.35f;
        pixels = null;
        width = 0;
        height = 0;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null) return null;

        var (bgra, w, h) = decoded.Value;
        if (bgra.Length < 4 || w <= 0 || h <= 0) return null;

        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] > maxAlpha) maxAlpha = bgra[i];

        if (maxAlpha == 0)
        {
            // No alpha channel at all. Cutting anything would erase the model.
            alphaCutoff = 0f;
        }
        else if (maxAlpha == 1)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] != 0) bgra[i] = 255;

            Console.WriteLine($"[character] {blpPath}: 1-bit alpha decoded as 0/1, rescaled to 0/255");
        }

        pixels = bgra;
        width = w;
        height = h;

        return Texture.From2D(_gl, bgra, w, h);
    }

    private unsafe void BuildGpuBuffers(M2Model m2)
    {
        int vertexCount = m2.Vertices.Count;
        var vertices = new float[vertexCount * FloatsPerVertex];

        int clampedIndices = 0;

        for (int i = 0; i < vertexCount; i++)
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

            // Weights are bytes summing to 255. Normalise here rather than in
            // the shader so the shader stays a straight weighted sum.
            float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
            if (total <= 0f)
            {
                // No influence at all. Pin to bone 0 rather than collapsing the
                // vertex to the origin, which is the visible failure mode.
                vertices[o + 8] = 1f;
                vertices[o + 9] = 0f;
                vertices[o + 10] = 0f;
                vertices[o + 11] = 0f;
                vertices[o + 12] = 0f;
                vertices[o + 13] = 0f;
                vertices[o + 14] = 0f;
                vertices[o + 15] = 0f;
                continue;
            }

            vertices[o + 8] = v.BoneWeight0 / total;
            vertices[o + 9] = v.BoneWeight1 / total;
            vertices[o + 10] = v.BoneWeight2 / total;
            vertices[o + 11] = v.BoneWeight3 / total;

            vertices[o + 12] = ClampBone(v.BoneIndex0, ref clampedIndices);
            vertices[o + 13] = ClampBone(v.BoneIndex1, ref clampedIndices);
            vertices[o + 14] = ClampBone(v.BoneIndex2, ref clampedIndices);
            vertices[o + 15] = ClampBone(v.BoneIndex3, ref clampedIndices);
        }

        if (clampedIndices > 0)
            Console.WriteLine($"[character] {clampedIndices} bone reference(s) past " +
                              $"{M2Animator.MaxBones} were clamped");

        var indices = m2.Indices.ToArray();

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* p = indices)
        {
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
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    private static float ClampBone(byte index, ref int clamped)
    {
        if (index < M2Animator.MaxBones) return index;
        clamped++;
        return M2Animator.MaxBones - 1;
    }

    /// <summary>
    /// One drawable piece per M2 batch. A submesh may have several authored
    /// material passes; collapsing those to "first batch wins" loses textures,
    /// transparency and the layer order used by faces, hair and effects.
    /// </summary>
    private void BuildPieces(M2Model m2)
    {
        var representedSubmeshes = new HashSet<int>();
        int fallbackPieces = 0;

        foreach (var entry in m2.Batches
                     .Select((batch, index) => (batch, index))
                     .OrderBy(x => x.batch.PriorityPlane)
                     .ThenBy(x => x.batch.MaterialLayer))
        {
            var batch = entry.batch;
            int sub = batch.SubmeshIndex;
            if (sub < 0 || sub >= m2.Submeshes.Count) continue;

            var submesh = m2.Submeshes[sub];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > m2.Indices.Count) continue;

            int slot = -1;
            if (batch.TextureIndex < m2.TextureLookup.Count)
                slot = m2.TextureLookup[batch.TextureIndex];

            bool twoSided = false;
            int blendMode = 0;
            bool noZWrite = false;

            if (batch.MaterialIndex < m2.RenderFlags.Count)
            {
                var flags = m2.RenderFlags[batch.MaterialIndex];
                twoSided = flags.TwoSided;
                blendMode = flags.BlendingMode;
                noZWrite = flags.NoZWrite;
            }

            _pieces.Add(new Piece
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                SlotIndex = slot >= 0 && slot < _slots.Count ? slot : -1,
                TwoSided = twoSided,
                BlendMode = blendMode,
                NoZWrite = noZWrite,
                GeosetId = submesh.Id,
                Category = submesh.Id / 100,
                Variant = submesh.Id % 100,
                SubmeshIndex = sub,
                BatchIndex = entry.index,
                PriorityPlane = batch.PriorityPlane,
                MaterialLayer = batch.MaterialLayer,
            });

            representedSubmeshes.Add(sub);
        }

        // Malformed/simple assets occasionally carry geometry without a batch.
        // Keep that geometry visible with a conservative opaque fallback.
        for (int sub = 0; sub < m2.Submeshes.Count; sub++)
        {
            if (representedSubmeshes.Contains(sub)) continue;
            var submesh = m2.Submeshes[sub];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > m2.Indices.Count) continue;

            _pieces.Add(new Piece
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                GeosetId = submesh.Id,
                Category = submesh.Id / 100,
                Variant = submesh.Id % 100,
                SubmeshIndex = sub,
                BatchIndex = -1,
            });
            fallbackPieces++;
        }

        int layeredSubmeshes = _pieces
            .Where(p => p.BatchIndex >= 0)
            .GroupBy(p => p.SubmeshIndex)
            .Count(g => g.Count() > 1);
        Console.WriteLine($"[character] render list: {m2.Batches.Count} batch(es) -> {_pieces.Count} draw(s), " +
                          $"{layeredSubmeshes} layered submesh(es), {fallbackPieces} fallback draw(s)");
    }

    /// <summary>
    /// Decide which geosets are drawn.
    ///
    /// Category 0 is base body (variant 0) plus exactly ONE hairstyle. Every
    /// other category shows the single variant named in NakedDefaults, or
    /// nothing. Skipping this step is not a subtle bug: the character wears all
    /// thirteen hairstyles at once.
    ///
    /// Equipment then overrides the map, which is why this builds a
    /// category-to-variant table first rather than testing each piece directly.
    /// A geosetGroup of zero means "leave the default", not "hide", so the two
    /// passes have to stay separate.
    /// </summary>
    private void ApplyGeosetVisibility()
    {
        uint raceId = CharSectionsTable.RaceId(Race);
        uint sexId = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;

        // Use the same byte-faithful visibility engine as streamed players and humanoid NPCs.
        // In particular, robes remove boot/knee/leg groups and HelmetGeosetVisData selects the
        // base scalp/facial/ear variants hidden by this race's helm. The old category override
        // table could not express either rule: it left boots showing through a robe and removed
        // the hair geoset altogether for a closed helm, opening a hole in the scalp.
        HashSet<int>? visibleGeosets = _characterGeosets?.Visible(
            raceId, sexId, HairStyleId, FacialHairId, BuildEquipGeosets());
        if (visibleGeosets is not null)
        {
            // CharacterRenderer historically exposes this always-on face helper. It is outside
            // the 16 character-region slots governed by GeosRenderPrep, so preserve it here.
            visibleGeosets.Add(3201);
            foreach (Piece piece in _pieces)
            {
                bool show = visibleGeosets.Contains(piece.GeosetId);
                if (HideHair && piece.Category == 0 && piece.Variant > 0) show = false;
                if (HiddenCategories.Contains(piece.Category)) show = false;
                piece.Visible = show;
            }

            ApplySoloGeoset();
            FinishGeosetDiagnostics(raceId, sexId);
            return;
        }

        var hairVariants = _pieces
            .Where(p => p.Category == 0 && p.Variant > 0)
            .Select(p => p.Variant)
            .Distinct()
            .ToList();

        int mappedHair = _charHairGeosets?.Find(raceId, sexId, HairStyleId) ?? -1;

        // The DBC is authoritative. style+1 is only a last-resort convention
        // for incomplete custom data sets; choosing an arbitrary high variant
        // pairs one hairstyle's texture with another hairstyle's geometry.
        int fallbackHair = Math.Max(HairStyleId + 1, 1);
        int hair = hairVariants.Contains(mappedHair)
            ? mappedHair
            : hairVariants.Contains(fallbackHair)
                ? fallbackHair
                : hairVariants.Contains(1) ? 1
                    : hairVariants.Count > 0 ? hairVariants.Min() : -1;

        var selected = new Dictionary<int, int>(NakedDefaults);
        Equipment.ApplyGeosets(selected);

        // Closed helms suppress hair. Open helms such as Helm of Might keep the
        // style-specific scalp; forcing every helmet bald is visibly wrong and
        // does not affect the material flicker investigated below.
        if (Equipment.HidesHair()) hair = -1;

        foreach (var piece in _pieces)
        {
            bool show;

            if (piece.Category == 0)
                show = piece.Variant == 0 || (hair >= 0 && !HideHair && piece.Variant == hair);
            else
                show = selected.TryGetValue(piece.Category, out int want) && want > 0 && piece.Variant == want;

            if (HiddenCategories.Contains(piece.Category)) show = false;

            piece.Visible = show;
        }

        ApplySoloGeoset();
        FinishGeosetDiagnostics(raceId, sexId);
    }

    private EquipGeosets BuildEquipGeosets()
    {
        var equip = new EquipGeosets();
        foreach (CharacterEquipment.Piece piece in Equipment.Pieces)
        {
            if (piece.Row is null) continue;
            switch (piece.InventoryType)
            {
                case CharacterEquipment.Slot.Shirt: equip.Bodyslots[0] = piece.Row; break;
                case CharacterEquipment.Slot.Chest:
                case CharacterEquipment.Slot.Robe: equip.Bodyslots[1] = piece.Row; break;
                case CharacterEquipment.Slot.Waist: equip.Bodyslots[2] = piece.Row; break;
                case CharacterEquipment.Slot.Legs: equip.Bodyslots[3] = piece.Row; break;
                case CharacterEquipment.Slot.Feet: equip.Bodyslots[4] = piece.Row; break;
                case CharacterEquipment.Slot.Wrists: equip.Bodyslots[5] = piece.Row; break;
                case CharacterEquipment.Slot.Hands: equip.Bodyslots[6] = piece.Row; break;
                case CharacterEquipment.Slot.Tabard: equip.Bodyslots[7] = piece.Row; break;
                case CharacterEquipment.Slot.Cloak:
                    equip.HasCloak = true;
                    equip.CloakGroup = piece.Row.GeosetGroup.Length > 0
                        ? piece.Row.GeosetGroup[0] : 0;
                    break;
                case CharacterEquipment.Slot.Head:
                    equip.HelmVis = (piece.Row.HelmetGeosetVis1,
                        piece.Row.HelmetGeosetVis2);
                    break;
            }
        }
        return equip;
    }

    private void ApplySoloGeoset()
    {
        if (SoloGeoset < 0) return;
        var geosets = _pieces
            .Where(p => p.Visible)
            .Select(p => (p.Category, p.Variant))
            .Distinct()
            .OrderBy(x => x.Category)
            .ThenBy(x => x.Variant)
            .ToList();
        if (SoloGeoset >= geosets.Count) return;
        var selectedGeoset = geosets[SoloGeoset];
        foreach (Piece piece in _pieces)
            if (piece.Visible && (piece.Category, piece.Variant) != selectedGeoset)
                piece.Visible = false;
    }

    private void FinishGeosetDiagnostics(uint raceId, uint sexId)
    {
        ReportOverlaps();

        int mappedHair = _charHairGeosets?.Find(raceId, sexId, HairStyleId) ?? -1;
        int hair = _pieces.Where(p => p.Visible && p.Category == 0 && p.Variant > 0)
            .Select(p => p.Variant).DefaultIfEmpty(-1).First();

        // Head/hair/ear diagnostic - surfaced LIVE in the HUD + a Capture-to-file button,
        // so "is the head right?" is a colour you read, not console lines you scrape.
        _headDiag.Clear();
        ScalpCovered = hair >= 0 && !HideHair;
        HairResolution = hair >= 0
            ? $"hair style {HairStyleId}/{HairColorId} -> geoset variant {hair} " +
              (hair == mappedHair ? "(DBC match)"
                  : hair == 1 ? "(base scalp selected by helm visibility)"
                  : "(fallback - may not cover the scalp)")
            : "NO hair geoset selected -> the bald base-body scalp shows the dressed atlas";
        foreach (var hp in _pieces.Where(p => p.Visible && (p.Category == 0 || p.Category == 7)))
        {
            var hs = hp.SlotIndex >= 0 ? _slots[hp.SlotIndex] : null;
            string htype = hs is null ? "?" : hs.Type.ToString();
            string hfill = hs is null ? "none" : hs.Fill.ToString();
            bool baseBody = hp.Category == 0 && hp.Variant == 0;
            string line = $"geo{hp.GeosetId} cat{hp.Category} var{hp.Variant} slot=type{htype} fill={hfill} src='{hs?.Source}'"
                        + (baseBody ? "  <- BASE BODY (its head samples the dressed atlas)" : "");
            _headDiag.Add(line);
            Console.WriteLine($"[character] HEAD {line}");
        }

        VisiblePieces = _pieces.Count(p => p.Visible);

        // Published for the HUD. Overlapping geometry is what z-fighting IS,
        // so being able to switch one category off and watch the flicker stop
        // names the culprit faster than any amount of reasoning about it.
        ActiveGeosets = _pieces
            .Where(p => p.Visible)
            .Select(p => (p.Category, p.Variant))
            .Distinct()
            .OrderBy(x => x.Category)
            .ToList();

        var byCategory = _pieces
            .Where(p => p.Visible)
            .GroupBy(p => p.Category)
            .OrderBy(g => g.Key)
            .Select(g => $"c{g.Key}=[{string.Join(",", g.Select(p => p.GeosetId))}]");

        Console.WriteLine(
            $"[character] geosets {VisiblePieces}/{_pieces.Count} visible" +
            (hair >= 0 ? $" (hair style {HairStyleId} -> variant {hair}" +
                         (hair == mappedHair ? ", DBC)" : ", fallback)")
                       : " (no hair geoset found)") +
            $": {string.Join(" ", byCategory)}");
    }

    /// <summary>Write a full character diagnostic report to a file next to the client; returns the path.</summary>
    public string SaveDiagnostics()
    {
        var lines = new List<string>
        {
            "MSUI character diagnostics",
            $"race={Race} gender={Gender} skin={SkinId} face={FaceId} hair={HairStyleId}/{HairColorId} facial={FacialHairId}",
            $"model={ModelPath}",
            $"body skin={SkinTexturePath}",
            $"hair: {HairResolution}",
            $"scalp covered by a hair geoset: {ScalpCovered}",
            $"visible geosets: {VisiblePieces}/{PieceCount}",
            "-- texture slots --",
        };
        for (int i = 0; i < _slots.Count; i++)
            lines.Add($"slot {i}: type={_slots[i].Type} fill={_slots[i].Fill} src='{_slots[i].Source}'");
        lines.Add("-- head/hair/ear geosets (what shows on the head) --");
        lines.AddRange(_headDiag);
        lines.Add("-- geometry/UV probe (visible submeshes; V<0.50=arm, 0.50-0.625=hand, >0.625=face/head) --");
        if (_m2 is not null)
        {
            var seenSub = new HashSet<int>();
            foreach (var pc in _pieces.Where(x => x.Visible))
            {
                if (!seenSub.Add(pc.SubmeshIndex)) continue;
                if (pc.SubmeshIndex < 0 || pc.SubmeshIndex >= _m2.Submeshes.Count) continue;
                var sm = _m2.Submeshes[pc.SubmeshIndex];
                int end = Math.Min(sm.IndexStart + sm.IndexCount, _m2.Indices.Count);
                float minY = 1e9f, maxY = -1e9f;
                for (int ii = sm.IndexStart; ii < end; ii++)
                {
                    int vi = _m2.Indices[ii];
                    if (vi < 0 || vi >= _m2.Vertices.Count) continue;
                    float y = _m2.Vertices[vi].PosY;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                float crown = minY + 0.85f * (maxY - minY);
                float minV = 1e9f, maxV = -1e9f, cMinV = 1e9f, cMaxV = -1e9f;
                for (int ii = sm.IndexStart; ii < end; ii++)
                {
                    int vi = _m2.Indices[ii];
                    if (vi < 0 || vi >= _m2.Vertices.Count) continue;
                    var v = _m2.Vertices[vi];
                    if (v.TexV < minV) minV = v.TexV;
                    if (v.TexV > maxV) maxV = v.TexV;
                    if (v.PosY >= crown) { if (v.TexV < cMinV) cMinV = v.TexV; if (v.TexV > cMaxV) cMaxV = v.TexV; }
                }
                lines.Add($"  geo{sm.Id} sub{pc.SubmeshIndex}: Y {minY:F2}..{maxY:F2}  V {minV:F2}..{maxV:F2}  crownV {cMinV:F2}..{cMaxV:F2}");
            }
        }
        string path = Path.Combine(AppContext.BaseDirectory, "msui-character-diag.txt");
        try { File.WriteAllText(path, string.Join(Environment.NewLine, lines)); }
        catch (Exception e) { Console.WriteLine($"[character] diag write failed: {e.Message}"); }
        LastDiagnosticPath = path;
        Console.WriteLine($"[character] diagnostics written: {path}");
        return path;
    }

    // ── animation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start the manual draw/stow motion as independent right/left-arm overlays. Returns false
    /// when the model or currently equipped hands cannot author a ceremony; callers then snap.
    /// </summary>
    public bool BeginSheathCeremony()
    {
        CancelSheathCeremony();
        if (_animator is null || _m2 is null || !_animator.HasArmOverlayRoots) return false;

        CharacterEquipment.Piece? main = Equipment.Pieces.FirstOrDefault(
            piece => piece.EquipmentSlot == 15);
        CharacterEquipment.Piece? off = Equipment.Pieces.FirstOrDefault(
            piece => piece.EquipmentSlot == 16);
        _rightSheathOverlay = ResolveSheathOverlay(main);
        _leftSheathOverlay = ResolveSheathOverlay(off);
        if (_rightSheathOverlay is null && _leftSheathOverlay is null) return false;

        _sheathOverlayTime = 0f;
        _sheathSwapAt = float.PositiveInfinity;
        _sheathCeremonyDuration = 0f;
        if (_rightSheathOverlay is { } right)
        {
            _sheathSwapAt = SheathSwapMoment(right);
            _sheathCeremonyDuration = right.DurationSeconds;
        }
        if (_leftSheathOverlay is { } left)
        {
            _sheathSwapAt = MathF.Min(_sheathSwapAt, SheathSwapMoment(left));
            _sheathCeremonyDuration = MathF.Max(_sheathCeremonyDuration, left.DurationSeconds);
        }
        _sheathCeremonyActive = _sheathCeremonyDuration > 0f;
        return _sheathCeremonyActive;
    }

    public bool ConsumeSheathSwap()
    {
        if (!_sheathSwapReady) return false;
        _sheathSwapReady = false;
        return true;
    }

    public void CancelSheathCeremony()
    {
        _rightSheathOverlay = null;
        _leftSheathOverlay = null;
        _sheathOverlayTime = 0f;
        _sheathSwapAt = 0f;
        _sheathCeremonyDuration = 0f;
        _sheathCeremonyActive = false;
        _sheathSwapReady = false;
    }

    private M2Animator.Clip? ResolveSheathOverlay(CharacterEquipment.Piece? piece)
    {
        if (piece is null || _animator is null) return null;
        int animationId = piece.Sheath is 3 or 7 ? 90 : 89;
        return _animator.FindOrBake(animationId, includeStaticSequences: true);
    }

    private float SheathSwapMoment(M2Animator.Clip clip)
    {
        float fallback = clip.DurationSeconds * 0.5f;
        if (_m2 is null || clip.SequenceIndex < 0 || clip.SequenceIndex >= _m2.Sequences.Count)
            return fallback;

        M2Sequence sequence = _m2.Sequences[clip.SequenceIndex];
        float best = float.PositiveInfinity;
        foreach (M2EventMarker marker in _m2.Events)
        {
            string identifier = marker.Identifier.TrimEnd('\0');
            if (!identifier.Equals("$SHL", StringComparison.Ordinal) &&
                !identifier.Equals("$SHR", StringComparison.Ordinal))
                continue;
            foreach (uint timestamp in marker.Times)
            {
                if (timestamp < sequence.StartTimestamp || timestamp > sequence.EndTimestamp) continue;
                best = MathF.Min(best, (timestamp - sequence.StartTimestamp) / 1000f);
            }
        }
        return float.IsFinite(best) ? Math.Clamp(best, 0f, clip.DurationSeconds) : fallback;
    }

    private void AdvanceSheathCeremony(float dt)
    {
        if (!_sheathCeremonyActive) return;
        _sheathOverlayTime += dt;
        if (!_sheathSwapReady && _sheathOverlayTime >= _sheathSwapAt)
            _sheathSwapReady = true;
        if (_sheathOverlayTime >= _sheathCeremonyDuration)
        {
            if (!_sheathSwapReady) _sheathSwapReady = true;
            _sheathCeremonyActive = false;
        }
    }

    public void Update(float dt, in UnitState state)
    {
        if (_m2 is null || dt <= 0f) return;

        // Ice Block is not a locomotion root or a stun animation. It preserves the exact pose
        // reached by the cast and stops every character animation clock until the aura ends.
        if (state.FreezePose) return;

        AdvanceSheathCeremony(dt);

        MeasureMotion(dt, state);
        ResolveMotion(state);

        bool airborne = !state.Grounded && !state.Flying;
        bool jumpLaunched = airborne && !_wasAirborne && state.VerticalVelocity > 0.5f;
        if (jumpLaunched && _animator is not null)
        {
            _jumpArcActive = true;
            _jumpHangShown = false;
            _jumpStartClip = _animator.Resolve("player", BaseAnimationTrack, 37, false, 38, 40, 0);
            if (_jumpStartClip is not null)
                Console.WriteLine($"[anim] JumpStart window {_jumpStartClip.DurationSeconds:F6}s");
        }
        LatchLanding(state, airborne);
        if (!airborne)
        {
            _jumpArcActive = false;
            _jumpHangShown = false;
            _jumpStartClip = null;
        }
        _wasAirborne = airborne;

        DriveBodyHeading(dt, state, airborne);

        // Benilla's byte-reconstructed wound slot is independent of the base/action clock:
        // it decays over one wound-clip span and releases without ever stopping the swing below.
        if (_combatReaction is not null)
        {
            _combatReactionTime += dt;
            if (_combatReactionTime >= _combatReaction.DurationSeconds)
            {
                _combatReaction = null;
                _combatReactionTime = 0f;
            }
        }

        // A looping one-shot (Dance, AnimID 69) is a state, not a burst - it must
        // keep cycling until something else interrupts it (movement, another
        // action). Only a non-looping clip's own duration ends it here; the
        // wrap-vs-clamp split already lives in M2Animator.ClipTime.
        if (_combatAction is not null && ReferenceEquals(_clip, _combatAction) &&
            !_combatAction.Looping && _clipTime >= _combatAction.DurationSeconds)
            _combatAction = null;

        // Route the play ONCE, as it is armed. Benilla calls route_oneshot on the live state
        // when the request is armed (driver.rs:563-670) and never reconsiders it. Re-deciding
        // every frame would swap the base out from under a half-played clip the moment you
        // stopped running - the legs would pop from the gait onto the action's own leg keys
        // partway through. A freshly armed action (identity change) also restarts the mask
        // clock, because _clipTime belongs to the base pose while masked.
        if (!ReferenceEquals(_combatAction, _actionOverlayArmedRef))
        {
            _actionOverlayArmedRef = _combatAction;
            _actionOverlayTime = 0f;
            _combatActionMasked = _combatAction is not null && CommittedLowerState(state);
        }

        // ...with ONE clause re-checked every frame: the stand-state.
        //
        // Movement is local input, known the instant an action is armed. StandState is the
        // SERVER's field (UNIT_FIELD_BYTES_1) and lands on its own schedule - for a drink it
        // arrives a beat AFTER the eat emote it belongs to. Latching the route across that gap
        // is what left a drinking character standing, eating full-body, for seconds before it
        // finally sat (reported 2026-09-04; food happened to win the race, drink lost it).
        //
        // Upgrading full-body -> masked is safe in the way the reverse is not. It moves the
        // action OFF the base and lets the seated pose show through underneath; a masked ->
        // full-body downgrade mid-clip would drag the legs onto the action's own keys, which
        // is the pop the arm-time latch exists to prevent. So the route only ever tightens.
        if (_combatAction is not null && !_combatActionMasked && SeatedNow(state))
        {
            // Carry the elapsed full-body time onto the overlay clock so the bite continues
            // from where it got to rather than snapping back to frame 0 as the character sits.
            if (ReferenceEquals(_clip, _combatAction)) _actionOverlayTime = _clipTime;
            _combatActionMasked = true;
        }

        // LEAVING the seat ends a seated consume outright. Standing or moving drops the
        // Eating/Drinking aura server-side and stops the emote ticks, so the bite already in
        // flight must not keep chewing on the torso over a standing idle for the rest of its
        // duration. Only the seated -> not-seated EDGE does this: a cast masked by MOTION has
        // nothing to do with the stand-state and is deliberately left alone, which is why this
        // cannot just be folded into the movement-flag rule below.
        if (_combatAction is not null && _combatActionMasked &&
            _wasSeatedLastFrame && !SeatedNow(state))
        {
            _combatAction = null;
            _combatActionMasked = false;
        }
        _wasSeatedLastFrame = SeatedNow(state);

        // A movement-flag CHANGE ends a FULL-BODY play, not just the clip's own
        // duration - confirmed against the Benilla trace (driver.rs:847-877: "when
        // oneshot_finished(id) OR a movement-flag change, drv.mode = Mode::Gait").
        // CHANGE, not "currently moving" - the first cut of this (2026-08-16) used a
        // continuous `state.Moving` check, which nulls _combatAction on literally the
        // frame after it's armed whenever the player is already moving when the action
        // triggers, so the one-shot would never be drawn even once.
        //
        // ONLY the full-body route. A masked play is not Mode::Swing at all - it is an
        // overlay node running beside the base machine, so the movement flags that return
        // the base to Mode::Gait have nothing to end there. It runs its own clock to
        // completion. That is what stops a cast started while running from being cut, and
        // it is why the old edge-trigger compromise (documented here until 2026-09-04) is
        // gone rather than merely narrowed: with masking wired, there is nothing left to
        // compromise about. CastHold (_spellHold) is deliberately NOT touched - that one is
        // a live server-cast pose, released by the real cast-cancel path
        // (ReleaseSpellVisual/CancelSpellVisual), not a local movement heuristic.
        if (_combatAction is not null && !_combatActionMasked &&
            state.Moving != _wasMovingLastFrame)
        {
            _combatAction = null;
            _combatActionMasked = false;
        }
        _wasMovingLastFrame = state.Moving;

        // Upper-body action mask clock. While masked, _clip is the base pose - the gait when
        // moving, the seated pose when sat - and _clipTime belongs to it, so the action
        // advances on its OWN clock here and ends on its own duration. The whole-body expiry
        // above only fires while the action IS _clip.
        bool masked = _combatAction is not null && _combatActionMasked;
        if (masked)
        {
            _actionOverlayTime += dt;   // ChooseClip hands the base clip back at rate 1
            if (!_combatAction!.Looping && _actionOverlayTime >= _combatAction.DurationSeconds)
            {
                _combatAction = null;   // one-shot done: upper body falls back to the base pose
                masked = false;
            }
        }
        _torsoOverlayForRender = masked ? _combatAction : null;

        var next = ChooseClip(state, out float rate);

        // ORDER MATTERS. SwitchClip snapshots _clipRate as the rate the OUTGOING
        // clip should keep striding at while it fades. Assigning the new rate
        // first would hand it the incoming clip's rate instead, and a run fading
        // out into a stand would drop to walking cadence for the length of the
        // fade - a small, slow, extremely hard to name wrongness.
        SwitchClip(next);
        _clipRate = rate;

        // Both clocks advance. The outgoing one keeps running at the rate it had
        // when it was current, so a run cycle fading out keeps striding instead
        // of freezing on the frame it was replaced.
        _clipTime += dt * _clipRate;
        _previousClipTime += dt * _previousClipRate;

        EmitFootstepEvents();

        _globalTime += dt;

        if (_blendRemaining > 0f)
        {
            _blendRemaining -= dt;
            if (_blendRemaining <= 0f)
            {
                _blendRemaining = 0f;
                _previousClip = null;
            }
        }

        // Cheap insurance. A NaN here would freeze the pose and look exactly
        // like a state-machine bug, which is a diagnosis I would rather not
        // have to make twice.
        if (float.IsNaN(_clipTime) || float.IsInfinity(_clipTime)) _clipTime = 0f;
        if (float.IsNaN(_previousClipTime) || float.IsInfinity(_previousClipTime))
            _previousClipTime = 0f;
        if (float.IsNaN(_globalTime) || float.IsInfinity(_globalTime)) _globalTime = 0f;
    }

    private void EmitFootstepEvents()
    {
        if (_m2 is null || _clip is null || FootstepAnimationEvent is null)
        {
            _footstepSequence = -1;
            return;
        }
        if (_footstepSequence != _clip.SequenceIndex || _clipTime < _footstepTime)
        {
            _footstepSequence = _clip.SequenceIndex;
            _footstepTime = _clipTime;
            return;
        }
        int count = FootstepAnimationLaw.CountCrossings(
            _m2, _clip, _footstepTime, _clipTime);
        foreach (string identifier in CreatureAnimationSoundLaw.CrossedVocalEvents(
                     _m2, _clip, _footstepTime, _clipTime))
        {
            CreatureAnimationSoundEvent?.Invoke(identifier);
            CombatAnimationSoundEvent?.Invoke(identifier);
        }
        _footstepTime = _clipTime;
        // One footfall per tick, as on the creature path: four in a frame is four
        // waveOut devices and no extra information.
        if (count > 0) FootstepAnimationEvent();
    }

    /// <summary>Restart the packet-driven melee one-shot on the local player.</summary>
    public void TriggerCombatSwing(bool offHand)
    {
        if (_animator is null) return;
        // A fresh primary play on the same bones evicts the vanilla secondary wound slot. This
        // keeps a prior flinch from bleeding into the next attack while still allowing a wound
        // received during this swing to blend over it.
        _combatReaction = null;
        _combatReactionTime = 0f;
        bool twoHand = Equipment.Pieces.Any(p => p.InventoryType == CharacterEquipment.Slot.TwoHand);
        bool mainWeapon = Equipment.Pieces.Any(p => p.InventoryType is
            CharacterEquipment.Slot.Weapon or CharacterEquipment.Slot.MainHand);
        bool offWeapon = Equipment.Pieces.Any(p => p.InventoryType == CharacterEquipment.Slot.OffHand);
        int requested = offHand ? (offWeapon ? 87 : 117) : twoHand ? 18 : mainWeapon ? 17 : 16;
        _combatAction = offHand
            ? _animator.Resolve("player", ActionAnimationTrack, requested, false, 87, 117, 16)
            : _animator.Resolve("player", ActionAnimationTrack, requested, false, 16, 17, 18, 19, 85);
        CombatActionsTriggered++;
        RestartCombatAction();
    }

    public void TriggerCombatReaction(uint victimState, bool landedHit)
    {
        if (_animator is null) return;
        int requested = victimState switch
        {
            2 or 8 => 30,
            3 => 20,
            5 => 24,
            _ when landedHit => 9,
            _ => -1,
        };
        M2Animator.Clip? reaction = requested switch
        {
            30 => _animator.Resolve("player", ActionAnimationTrack, 30, false, 9),
            20 => _animator.Resolve("player", ActionAnimationTrack, 20, false, 21, 22, 23, 9),
            24 => _animator.Resolve("player", ActionAnimationTrack, 24, false, 9),
            9 => _animator.Resolve("player", ActionAnimationTrack, 9, false),
            _ => null,
        };
        if (reaction is null) return;

        if (requested == 9)
        {
            // CombatWound uses the reference client's secondary slot. Mid-swing the current
            // bone-0 clip is not a Ready stance, so the wound is SpineLow-masked and the attack
            // continues underneath. Between swings, Ready1H/2H (25-29) flinches full-body.
            _combatReaction = reaction;
            _combatReactionTime = 0f;
            _combatReactionMasked = _clip?.AnimationId is not (>= 25 and <= 29);
            CombatActionsTriggered++;
            return;
        }

        // Dodge/parry/block are primary defense animations in the reference client, not wound
        // blends, so retain their existing action-track replacement behaviour.
        _combatAction = reaction;
        CombatActionsTriggered++;
        RestartCombatAction();
    }

    public void BeginSpellVisual(ushort? animationId)
    {
        if (_animator is null || animationId is not { } id || id == 0) { _spellHold = null; return; }
        _spellHold = _animator.Resolve("player", SpellHoldAnimationTrack, id, true);
        if (_spellHold is not null) RestartCombatActionFor(_spellHold);
    }

    public void ReleaseSpellVisual(ushort? animationId)
    {
        _spellHold = null;
        if (_animator is null || animationId is not { } id || id == 0) return;
        _combatAction = _animator.Resolve("player", ActionAnimationTrack, id, true);
        if (_combatAction is not null)
        {
            CombatActionsTriggered++;
            RestartCombatAction();
        }
    }

    public void CancelSpellVisual() => _spellHold = null;

    public float TriggerOneShot(int animationId)
    {
        if (_animator is null) return 0f;
        // bakeOnDemand: true - unlike the combat ids in BakedAnimations, callers
        // here (text-emote playback, the loot kneel) pass arbitrary
        // AnimationData.dbc ids never pre-baked at load. Without on-demand
        // baking this silently fell through to the fallback chain (id 0,
        // Stand) every time, which - being the same cached clip already
        // playing - just snapped _clipTime back to 0 (a visible idle "reset")
        // while the requested animation never played. Mirrors the bakeOnDemand
        // already used by BeginSpellVisual/ReleaseSpellVisual below.
        _combatAction = _animator.Resolve("player", ActionAnimationTrack, animationId, true, 0);
        if (_combatAction is null) return 0f;
        CombatActionsTriggered++;
        RestartCombatAction();
        return _combatAction.DurationSeconds;
    }

    private void RestartCombatActionFor(M2Animator.Clip clip)
    {
        // Preserve the outgoing locomotion pose. ChooseClip/SwitchClip owns the
        // next-frame transition and will cross-fade Run/Walk into this action.
        // Clearing _clip here discarded the only source pose and made moving
        // spell casts hard-cut even though the mixer supports cross-fades.
        if (ReferenceEquals(_clip, clip)) _clipTime = 0f;
    }

    private void RestartCombatAction()
    {
        if (_combatAction is null) return;
        if (ReferenceEquals(_clip, _combatAction)) _clipTime = 0f;
    }

    /// <summary>
    /// Move to a new clip, carrying the leg cycle's PHASE where that is the
    /// honest thing to do, and starting a cross-fade either way.
    ///
    /// Two rules, and they are separate:
    ///
    /// PHASE. Walk, Run and WalkBackwards are the same cycle at different
    /// speeds. Shift-walking out of a run, or turning to back up, must continue
    /// the stride rather than restart it - a reset there is the visible leg pop.
    /// Between a locomotion clip and anything else the phase is meaningless, so
    /// the new clip starts at zero and the fade covers the seam.
    ///
    /// FADE. Always, unless the file explicitly asks for a hard cut on something
    /// that is not locomotion. See <see cref="BlendSecondsFor"/>.
    /// </summary>
    private void SwitchClip(M2Animator.Clip? next)
    {
        if (ReferenceEquals(next, _clip)) return;

        var outgoing = _clip;
        float outgoingTime = _clipTime;
        float outgoingRate = _clipRate;

        _clip = next;
        LastClipTransition = new ClipTransition(
            ++_clipTransitionSequence,
            outgoing?.AnimationId ?? -1,
            outgoing?.Name ?? "none",
            next?.AnimationId ?? -1,
            next?.Name ?? "none",
            outgoingTime);

        bool carryPhase =
            outgoing is not null && next is not null &&
            outgoing.DurationSeconds > 0.0001f && next.DurationSeconds > 0.0001f &&
            LocomotionAnimations.Contains(outgoing.AnimationId) &&
            LocomotionAnimations.Contains(next.AnimationId);

        if (carryPhase)
        {
            float phase = outgoingTime / outgoing!.DurationSeconds;
            phase -= MathF.Floor(phase);
            _clipTime = phase * next!.DurationSeconds;
        }
        else
        {
            _clipTime = 0f;
        }

        float blend = _forceNextBlendSeconds ?? BlendSecondsFor(outgoing, next);
        _forceNextBlendSeconds = null;
        if (outgoing is null || blend <= 0f)
        {
            _previousClip = null;
            _blendRemaining = 0f;
            _blendDuration = 0f;
            return;
        }

        _previousClip = outgoing;
        _previousClipTime = outgoingTime;
        _previousClipRate = outgoingRate;
        _blendDuration = blend;
        _blendRemaining = blend;
    }

    /// <summary>
    /// How long to cross-fade into <paramref name="next"/>.
    ///
    /// The M2 sequence carries its own blendTime and that is the value the
    /// reference client uses, so it wins wherever it is authored. Where it is
    /// not - and plenty of vanilla sequences leave it at zero - the fallback
    /// applies rather than the zero, because a hard cut IS the leg pop this
    /// whole change exists to remove.
    ///
    /// Set <see cref="DefaultBlendSeconds"/> to zero to put the old hard-cut
    /// behaviour back everywhere in one click. That is the A/B, and it is worth
    /// having: if a pose ever looks soft or smeared, this is the first switch to
    /// throw.
    /// </summary>
    private float BlendSecondsFor(M2Animator.Clip? outgoing, M2Animator.Clip? next)
    {
        if (outgoing is null || next is null) return 0f;

        float authored = next.BlendSeconds;
        if (float.IsNaN(authored) || authored < 0f) authored = 0f;

        float seconds = authored > 0f ? authored : DefaultBlendSeconds;

        // A fade longer than a short clip would never finish before the clip
        // itself is replaced, which reads as a permanently soft pose.
        return Math.Clamp(seconds, 0f, 0.4f);
    }

    /// <summary>Smoothstepped weight of the OUTGOING clip: 1 at the swap, 0 when done.</summary>
    private float BlendWeightNow()
    {
        if (_previousClip is null || _blendDuration <= 0f || _blendRemaining <= 0f) return 0f;

        float t = Math.Clamp(_blendRemaining / _blendDuration, 0f, 1f);

        // Smoothstep rather than linear: a linear fade has a velocity
        // discontinuity at both ends, and on a leg cycle that shows up as a
        // small twitch entering and leaving the blend.
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Ground speed and direction, from what was PRESSED when we are told, and
    /// from displacement when we are not.
    ///
    /// The measured path is kept for two reasons: the glue booth and anything
    /// else that only hands over a position still needs to work, and having both
    /// numbers on the HUD is what makes a disagreement between them legible
    /// instead of a mystery.
    /// </summary>
    private void MeasureMotion(float dt, in UnitState state)
    {
        if (!_hasLastPosition)
        {
            _lastPosition = state.Position;
            _hasLastPosition = true;
            return;
        }

        var delta = state.Position - _lastPosition;
        _lastPosition = state.Position;

        var flat = new Vector3(delta.X, delta.Y, 0f);
        _measuredSpeed = flat.Length() / dt;

        if (state.HasIntent) return;

        _instantGroundSpeed = _measuredSpeed;

        // ASYMMETRIC ON PURPOSE. Smoothing exists so a single frame against a
        // doorframe does not flick the clip, but it also means releasing W
        // leaves the speed decaying for a tenth of a second and the run cycle
        // keeps going after the character has stopped. Speeding UP is smoothed;
        // a genuine stop is taken immediately.
        float blend = 1f - MathF.Exp(-dt * 12f);

        if (_measuredSpeed < MoveThreshold) _groundSpeed = _measuredSpeed;
        else _groundSpeed += (_measuredSpeed - _groundSpeed) * blend;

        if (flat.LengthSquared() > 1e-8f)
        {
            var direction = Vector3.Normalize(flat);
            var facing = new Vector3(MathF.Cos(state.Yaw), MathF.Sin(state.Yaw), 0f);
            var right = new Vector3(MathF.Sin(state.Yaw), -MathF.Cos(state.Yaw), 0f);

            _forwardness += (Vector3.Dot(direction, facing) - _forwardness) * blend;
            _sideness += (Vector3.Dot(direction, right) - _sideness) * blend;
        }
    }

    /// <summary>
    /// Turn this frame's intent into the three numbers the gait selector wants.
    /// No smoothing anywhere: the key is down or it is not.
    ///
    /// The direction terms are the input axes themselves. The controller builds
    /// its wish vector as forward*Forward + right*Strafe with right = (sin, -cos),
    /// so the dot products the measured path works so hard to recover are, in
    /// closed form, exactly Forward and Strafe over their own length.
    /// </summary>
    private void ResolveMotion(in UnitState state)
    {
        if (!state.HasIntent) return;

        if (!state.Moving)
        {
            _groundSpeed = 0f;
            _instantGroundSpeed = 0f;
            _forwardness = 0f;
            _sideness = 0f;
            return;
        }

        float length = MathF.Sqrt(state.Forward * state.Forward + state.Strafe * state.Strafe);
        if (length < 1e-6f)
        {
            // Moving with no input of your own: a server ride owns the body (Charge, Intercept,
            // a knockback, a taxi hop). Run straight ahead at the ride's speed — the ride drives
            // the yaw too, so forward IS the direction of travel and there is nothing to strafe.
            // state.Speed is the controller's own planar speed and reads 0 under a ride, which
            // is why the gait rate has to come off CarriedSpeed here.
            _groundSpeed = state.CarriedSpeed > 0.01f ? state.CarriedSpeed : state.Speed;
            _instantGroundSpeed = _groundSpeed;
            _forwardness = 1f;
            _sideness = 0f;
            return;
        }

        _groundSpeed = state.Speed;
        _instantGroundSpeed = state.Speed;

        _forwardness = state.Forward / length;
        _sideness = state.Strafe / length;
    }

    /// <summary>
    /// Advance the DRAWN BODY's heading, which is not the aim.
    ///
    /// This client had one heading and welded the model to it, so the character
    /// pivoted rigidly in the idle pose whenever the camera turned and stood
    /// square while strafing sideways. The reference carries two angles and
    /// reconciles them per frame, and the four cases are all distinct:
    ///
    ///   STRAFING     the body turns to the aim +/- 90 degrees (pure) or +/- 45
    ///                (diagonal, mirrored while backpedalling so the legs never
    ///                cross), eased toward that OFFSET rather than toward an
    ///                absolute angle - a left-to-right flip is an exact 180
    ///                degree tie in absolute yaw and would pick a side at
    ///                random, where in offset space it always swings round the
    ///                front. The torso is pulled back by TorsoFollow, which is
    ///                the counter-twist that keeps the head looking at the aim.
    ///
    ///   MOVING or
    ///   AIRBORNE     snap to the aim. A backpedal keeps facing forward and
    ///                plays WalkBackwards; it does not turn round.
    ///
    ///   STANDING     the frozen chase. While you are steering, the body simply
    ///                holds and only the ninety-degree ceiling applies, so the
    ///                aim and the head lead and the body follows at exactly that
    ///                lag. Let go and it sweeps square at eight times the turn
    ///                rate. The lag is a FREEZE, not a slow rate - which is why
    ///                a slow-follow easing never looked right.
    ///
    /// The signed step it takes is what drives the turn-in-place shuffle, so the
    /// feet move when the BODY does. A stationary mouse turn shuffles, and a body
    /// held frozen under a leading head keeps its feet planted - both correct,
    /// and neither expressible from the key state alone.
    /// </summary>
    private void DriveBodyHeading(float dt, in UnitState state, bool airborne)
    {
        _bodyTurnStep = 0f;

        if (!_hasBodyYaw)
        {
            _bodyYaw = state.Yaw;
            _hasBodyYaw = true;
        }

        // The test harness wins outright: hold the offset and drive nothing.
        if (ForceAngleDegrees != 0f)
        {
            _moveYaw = ForceAngleDegrees * MathF.PI / 180f;
            _bodyYaw = WrapPi(state.Yaw + _moveYaw);
            return;
        }

        // No intent means no way to know whether the aim is being STEERED, and
        // the standing chase is defined by exactly that. Rather than guess, weld
        // the body to the aim - which is what the glue booth's turntable wants
        // anyway: it spins the model by driving Yaw, and a body that lagged
        // ninety degrees behind it while shuffling its feet would be nonsense.
        if (!state.HasIntent)
        {
            _bodyYaw = state.Yaw;
            _moveYaw = 0f;
            return;
        }

        // TWO DIFFERENT GATES, and they are not the same set of modes.
        //
        // The strafe offset is wanted by everything except Clips - LowerBody
        // exists precisely to put it on the hips, so gating it on "the whole
        // model turns" would silently disable that mode's only feature. Clips
        // picks a sideways animation instead and must not also rotate, or the
        // two compound.
        //
        // The standing chase is narrower. It only makes sense where the offset
        // reaches the MODEL HEADING: on the hips alone a ninety-degree lag would
        // wring the legs off a standing torso, and in Clips nothing consumes it
        // at all, so the feet would shuffle at a body that never turned.
        float strafeOffset = Strafe == StrafeStyle.Clips ? 0f : StrafeBodyOffset(state);
        bool bodyTurns = Strafe is StrafeStyle.Split or StrafeStyle.WholeBody;

        if (strafeOffset != 0f)
        {
            float current = WrapPi(_bodyYaw - state.Yaw);
            float eased = current + WrapPi(strafeOffset - current) *
                                    (1f - MathF.Exp(-dt * StrafeBlendRate));
            _bodyYaw = WrapPi(state.Yaw + eased);
        }
        else if (bodyTurns && !state.Moving && !airborne)
        {
            float ceiling = MathF.Max(0f, StandingChaseCeilingDegrees) * MathF.PI / 180f;
            float delta = WrapPi(state.Yaw - _bodyYaw);

            _bodyTurnStep = CharacterPoseLaw.StandingBodyStep(
                delta, state.Steering, ceiling, dt, BodyTurnRate, StationaryChaseRate);
            _bodyYaw = WrapPi(_bodyYaw + _bodyTurnStep);
        }
        else
        {
            // Moving, airborne, or a mode that does not turn the model: the body
            // SNAPS to the aim rather than easing onto it. That is the
            // reference's own behaviour (its facing-snap list), and it is why
            // releasing strafe while still running forward swings the model
            // square in a single frame instead of settling into it. It reads
            // abrupt written down and correct on screen; if it ever looks wrong,
            // this assignment is the one line to ease, and it is the only one.
            _bodyYaw = state.Yaw;
        }

        // Everything downstream - BuildTransform, the torso counter-twist, the
        // hip twist - already works in terms of an offset from the aim, so the
        // absolute body heading is published in exactly that form and none of it
        // has to change.
        _moveYaw = WrapPi(_bodyYaw - state.Yaw);
        if (MathF.Abs(_moveYaw) < 0.002f) _moveYaw = 0f;
    }

    /// <summary>
    /// Where the body points while strafing, as an offset from the aim.
    ///
    /// Ninety degrees for a pure strafe, forty-five when a forward or back key
    /// is also held. The sign flips while backpedalling: the body is then
    /// running the WalkBackwards cycle, so it must face the mirror of the travel
    /// direction rather than the direction itself, or the legs cross.
    /// </summary>
    private static float StrafeBodyOffset(in UnitState state)
    {
        bool left = state.Strafe < -0.01f;
        bool right = state.Strafe > 0.01f;
        if (!left && !right) return 0f;

        bool back = state.Forward < -0.01f;
        bool alongAim = MathF.Abs(state.Forward) > 0.01f;

        float magnitude = alongAim ? MathF.PI / 4f : MathF.PI / 2f;
        return left != back ? magnitude : -magnitude;
    }

    /// <summary>Wrap to (-pi, pi], so easing toward a target always takes the short way.</summary>
    private static float WrapPi(float radians)
    {
        const float tau = MathF.PI * 2f;
        radians = ((radians % tau) + tau) % tau;
        return radians > MathF.PI ? radians - tau : radians;
    }

    /// <summary>
    /// Arm the landing clip on touchdown, and only on a landing that was
    /// actually SEEN as airborne.
    ///
    /// The fall animation is debounced by FallAnimationDelayMs so that a
    /// one-frame floor-query miss on a staircase does not flicker the clip. That
    /// same blip must not produce a landing either - a JumpEnd fired every time
    /// you walk down stairs is a worse artefact than the one the debounce
    /// removed. So the gate is what was on screen, not what physics thought.
    /// </summary>
    private void LatchLanding(in UnitState state, bool airborne)
    {
        if (airborne || !_wasAirborne || _animator is null) return;
        if (_clip is null || !AirborneAnimations.Contains(_clip.AnimationId)) return;

        // Standing: JumpEnd. Running: the run-through landing, which keeps the
        // stride. Walking or backing up: nothing at all, because the only
        // authored landing that moves is a forward one and playing it backwards
        // is worse than cutting straight to the gait.
        _landClip = !state.Moving
            ? _animator.Resolve("player", BaseAnimationTrack, 39, false)
            : state.Walking || state.Forward < -0.01f
                ? null
                : _animator.Resolve("player", BaseAnimationTrack, 187, false);

        _landForward = state.Forward;
        _landStrafe = state.Strafe;
        _landWalking = state.Walking;
    }

    /// <summary>
    /// The server is holding us in a ground stand-state right now. Split out of
    /// <see cref="CommittedLowerState"/> because this is the one clause of the route that is
    /// re-evaluated per frame rather than latched at arm time - see Update.
    /// </summary>
    private static bool SeatedNow(in UnitState state) =>
        (UnitStandState)state.StandState is
            UnitStandState.Sit or UnitStandState.Sleep or UnitStandState.Kneel;

    /// <summary>
    /// The full Benilla committed_lower test for the local player - CharacterPoseLaw.CommittedLower
    /// fed from this frame's UnitState. Decides whether a one-shot masks to the SpineLow subtree or
    /// replaces the whole body, and is evaluated once per play (see the arm-time capture in Update).
    ///
    /// Two clauses of the reference rule are deliberately passed as false, and both are narrower
    /// than the moving case this exists to fix:
    ///
    /// TURNING - Benilla's ROUTE_COMMITTED_MOVE includes the turn keys. UnitState carries only
    /// <c>Steering</c>, which is "turn keys OR mouse-look"; masking on mouse-look would make
    /// virtually every cast masked, since a player mouse-looks near-continuously. Wiring this needs
    /// the turn bits on UnitState in their own right. Until then a stationary turning cast stays
    /// full-body, which is what it already did.
    ///
    /// COMBAT-WHILE-FALLING - the reference gates this on is_combat(animation id), and _combatAction
    /// is a resolved Clip that no longer carries the id it came from. Airborne casts stay full-body.
    /// </summary>
    private bool CommittedLowerState(in UnitState state) =>
        CharacterPoseLaw.CommittedLower(
            moving: state.Moving,
            turning: false,
            swimming: state.Swimming,
            // Sit is the case food and drink hit - the server sets UNIT_FIELD_BYTES_1
            // StandState=Sit for both, verified on the wire. Chair-sit stand-states are not
            // rendered as seated poses here (see ChooseClip), so they stay excluded.
            seated: SeatedNow(state),
            mounted: Mounted,
            combatAnimation: false,
            falling: !state.Grounded);

    private M2Animator.Clip? ChooseClip(in UnitState state, out float rate)
    {
        rate = 1f;
        if (_animator is null || BindPose || BoneOverflow) return null;

        // Seated, and it outranks everything below: there is no locomotion to choose while
        // the steed does the travelling, and vanilla dismounts you before a cast or a swing
        // could ever want the frames. 91 is "Mount" in AnimationData.dbc.
        if (Mounted)
            return _animator.Resolve("player", BaseAnimationTrack,
                CreatureRenderer.RiderAnimationId, true, 0);

        // Benilla's route_oneshot (select.rs:813-825), both halves now wired (2026-09-04; the
        // seated half landed 2026-08-25). A one-shot is handed back as the WHOLE BODY only when
        // the lower body is free - standing still. While it is committed, fall through to the
        // locomotion or seated pose below and let Render layer _combatAction onto the SpineLow
        // subtree instead, at ~8:1 (CharacterPoseLaw.OneshotOverlayWeight) over the base that
        // keeps running underneath: legs keep striding, torso plays the one-shot. Cam confirmed
        // that shape from real 1.12 play, and it is what the reference trace documents
        // (driver.rs:631-644/1137-1178).
        //
        // The route is decided at ARM time and read from _combatActionMasked here, not
        // recomputed - ChooseClip runs every frame and a mid-play change of answer would pop the
        // legs. CharacterPoseLaw.CommittedLower carries the two clauses still passed as false
        // (turn keys, combat-while-falling) and why.
        //
        // The five stand-state commands (/sit /kneel /stand /dance /sleep) are the one exception
        // and are never masked: they refuse outright with "You cannot do this while moving."
        // - see SubmitStandStateChange's gate in GameLoop.Chat.cs, which is what keeps them from
        // reaching this path at all while moving.
        if (_combatAction is not null && !_combatActionMasked) return _combatAction;
        if (_spellHold is not null) return _spellHold;

        if (state.Swimming)
        {
            _landClip = null;
            int swimId = !state.Moving ? 41
                : state.Forward < -0.01f ? 45
                : state.Strafe < -0.01f ? 43
                : state.Strafe > 0.01f ? 44
                : 42;
            M2Animator.Clip? swim = _animator.Resolve("player", BaseAnimationTrack,
                swimId, true, state.Moving ? 42 : 41, 0);
            return state.Moving ? LocomotionClip(swim, false, out rate) : swim;
        }

        // A "state" emote (Dance, ...) - UNIT_NPC_EMOTESTATE, not SMSG_EMOTE; see
        // that field's doc comment for why. Unlike Sit/Kneel/Sleep below, the
        // AnimationData ids these resolve to (Dance is 69) are themselves already
        // looping sequences with no separate Down/Up bracket in the real data, so
        // this needs no transition state machine - just hold the loop while the
        // field is nonzero. !state.Moving is a client-side belt-and-braces cutoff:
        // whether VMaNGOS itself clears UNIT_NPC_EMOTESTATE on movement is
        // unconfirmed, and waiting on that round trip either way would reintroduce
        // the same too-slow-to-break feel just fixed for _combatAction above.
        if (state.EmoteState != 0 && !state.Moving &&
            EmoteAnimResolver?.Invoke(state.EmoteState) is int stateAnimId && stateAnimId > 0)
            return _animator.Resolve("player", BaseAnimationTrack, stateAnimId, true, 0);

        // Ground-sit/sleep/kneel, driven by the server's own UNIT_FIELD_BYTES_1
        // StandState byte (see UnitStandState) rather than anything client-guessed.
        // Down->Loop->Up ids are the real AnimationData.dbc rows, read directly out
        // of this client's own AnimationData.dbc (dumps/AnimationData.dbc) - not
        // recalled. Chair-sitting (SitChair/SitLowChair/SitMediumChair/
        // SitHighChair) is deliberately not handled here: it is driven by
        // GameObject-use, not the /sit command this feature covers, and each
        // chair height needs its own seat-offset math this client doesn't have yet.
        (int down, int loop, int up) = (UnitStandState)state.StandState switch
        {
            UnitStandState.Sit => (96, 97, 98),      // SitGroundDown / SitGround / SitGroundUp
            UnitStandState.Sleep => (99, 100, 101),  // SleepDown / Sleep / SleepUp
            UnitStandState.Kneel => (114, 115, 116), // KneelStart / KneelLoop / KneelEnd
            _ => (0, 0, 0),
        };

        // !state.Moving here too, for the same reason as the state-emote branch
        // above: don't wait on the server's own reaction to standing up (or, per
        // Cam's account of the real client, its own client-side auto-stand) when
        // we already know movement just started. Whichever pose we were just in
        // is still cached in _seatedLoopAnimId/_seatedUpAnimId below, so this
        // falls straight into the Up bracket exactly like a real StandState
        // revert would - SendStandStateChange(Stand) (Program.cs's movement
        // handling) tells the server the same thing separately.
        if (loop != 0 && !state.Moving)
        {
            _seatedUpClip = null;
            if (_seatedLoopAnimId != loop)
            {
                // Entering this pose fresh, or switching straight from one seated
                // pose to another without passing through Stand in between - either
                // way the Down bracket (re)arms once against the NEW loop id.
                _seatedDownClip = _animator.Resolve("player", BaseAnimationTrack, down, true, loop);
                _seatedLoopAnimId = loop;
                _seatedUpAnimId = up;
            }
            bool downPlaying = _seatedDownClip is not null &&
                ReferenceEquals(_clip, _seatedDownClip) && _clipTime < _seatedDownClip.DurationSeconds;
            return downPlaying
                ? _seatedDownClip
                : _animator.Resolve("player", BaseAnimationTrack, loop, true, 0);
        }

        if (_seatedLoopAnimId != 0)
        {
            // The seated pose just ended. Arm the authored Up bracket ONLY for a
            // stationary stand-up (a plain /stand) - that plays the full, deliberate
            // rise. Standing up BECAUSE movement started does NOT play the stand-up
            // clip at all in the real 1.12 client: it blends the seated pose straight
            // into the gait ("very clipped, basically only gets you from sitting to
            // running; the full stand-up doesn't play"). So leave it unarmed when
            // moving and force a short, smooth sit->run cross-fade instead.
            _seatedUpClip = state.Moving
                ? null
                : _animator.Resolve("player", BaseAnimationTrack, _seatedUpAnimId, true, 0);
            if (state.Moving) _forceNextBlendSeconds = SeatedRunBlendSeconds;
            _seatedLoopAnimId = 0;
        }
        // Movement also cuts short a stationary stand-up already in progress: drop the
        // Up clip and blend into the gait rather than finishing the rise.
        if (state.Moving && _seatedUpClip is not null)
        {
            _seatedUpClip = null;
            _forceNextBlendSeconds = SeatedRunBlendSeconds;
        }
        if (_seatedUpClip is not null)
        {
            bool upFinished = ReferenceEquals(_clip, _seatedUpClip) &&
                _clipTime >= _seatedUpClip.DurationSeconds;
            if (upFinished) _seatedUpClip = null;
            else return _seatedUpClip;
        }

        if (state.Flying)
        {
            _landClip = null;
            return _animator.Resolve("player", BaseAnimationTrack, 40, false, 38, 0);
        }

        // A deliberate jump is a transition bracket, not a direct selection of
        // the hang loop. The launch event arms JumpStart 37 and its own authored
        // duration controls the handoff. A short arc lands directly from 37;
        // only an arc still airborne after that window sees Jump 38.
        if (!state.Grounded && _jumpArcActive)
        {
            if (_jumpStartClip is not null)
            {
                bool startFinished = ReferenceEquals(_clip, _jumpStartClip) &&
                                     _clipTime >= _jumpStartClip.DurationSeconds;
                if (!startFinished) return _jumpStartClip;
            }

            if (!_jumpHangShown)
            {
                _jumpHangShown = true;
                return _animator.Resolve("player", BaseAnimationTrack, 38, false, 40, 0);
            }
        }

        if (!state.Grounded && state.VerticalVelocity > 0.5f)
        {
            // A deliberate jump must react immediately. The controller's
            // positive launch velocity distinguishes it from a transient loss
            // of support while walking over stairs or a narrow prop.
            _landClip = null;
            return _animator.Resolve("player", BaseAnimationTrack, 38, false, 37, 40, 0);
        }

        if (!state.Grounded &&
            state.FallTimeMs >= MathF.Max(0f, _config.Movement.FallAnimationDelayMs))
        {
            // Physics has been airborne continuously for long enough that this
            // is a real fall, not a one-frame floor-query miss. Only animation
            // is delayed; gravity and collision have already been running.
            _landClip = null;
            return _animator.Resolve("player", BaseAnimationTrack, 40, false, 38, 0);
        }

        // ── landing ──────────────────────────────────────────────────────────
        //
        // The landing clip is NOT a bracket you have to sit through. Land and
        // immediately press a direction and the run starts on that frame; land
        // and release and you stand. Only an uninterrupted landing plays out,
        // which is why the intent at touchdown is snapshotted and compared.
        if (_landClip is not null)
        {
            bool intentChanged =
                MathF.Abs(state.Forward - _landForward) > 0.01f ||
                MathF.Abs(state.Strafe - _landStrafe) > 0.01f ||
                state.Walking != _landWalking;

            bool finished = ReferenceEquals(_clip, _landClip) &&
                            _clipTime >= _landClip.DurationSeconds;

            if (intentChanged || finished)
            {
                _landClip = null;
            }
            else
            {
                // The run-through landing is a locomotion clip and has to track
                // the speed like one, or a landing at full run plays at walking
                // cadence and the feet skate for its whole length. JumpEnd is a
                // standing pose and keeps its authored timing.
                return _landClip.AnimationId == 187
                    ? LocomotionClip(_landClip, state.Walking, out rate)
                    : _landClip;
            }
        }

        // NO MOVEMENT-STOP GRACE WINDOW. WoWee's FSM holds the moving state
        // open past the last motion and I copied that; Nico's verdict was that
        // it feels awful and the sharp stop is better. The airborne debounce
        // above is separate: it filters unstable support without extending
        // locomotion after input stops.
        bool standing = state.HasIntent ? !state.Moving : _groundSpeed < MoveThreshold;

        if (standing)
        {
            if (LootKneel)
                return _animator.Resolve("player", BaseAnimationTrack, 50, false, 0);

            if (state.StandState != 0)
            {
                int pose = Engine.UI.StandStateUiLaw.LoopAnimation(state.StandState);
                if (pose != 0)
                    return _animator.Resolve("player", BaseAnimationTrack, pose, false, 0);
            }

            // TURN IN PLACE. The shuffle rides the BODY's actual rotation, not
            // the turn keys - which is the whole reason it is worth having.
            // While the frozen chase holds the body under a leading aim the feet
            // hold too, and a stationary MOUSE turn shuffles the moment the body
            // steps, with no key involved either way.
            if (_bodyTurnStep > 1e-5f)
            {
                var left = _animator.Resolve("player", BaseAnimationTrack, 11, false);
                if (left is not null) return left;
            }
            else if (_bodyTurnStep < -1e-5f)
            {
                var right = _animator.Resolve("player", BaseAnimationTrack, 12, false);
                if (right is not null) return right;
            }

            if (state.Engaged)
            {
                bool twoHand = Equipment.Pieces.Any(p => p.InventoryType == CharacterEquipment.Slot.TwoHand);
                bool armed = Equipment.Pieces.Any(p => p.InventoryType is
                    CharacterEquipment.Slot.Weapon or CharacterEquipment.Slot.MainHand);
                int ready = twoHand ? 27 : armed ? 26 : 25;
                return _animator.Resolve("player", BaseAnimationTrack, ready, false, 25, 0);
            }

            return _animator.Resolve("player", BaseAnimationTrack, 0, false);
        }

        // Angle between where the character is FACING and where he is actually
        // GOING. Zero is straight ahead, positive is toward his left.
        //
        // Negated side term because "right" sits at world yaw (Yaw - 90): with
        // facing = (cos Y, sin Y) and right = (sin Y, -cos Y), a direction at
        // (Yaw + phi) gives forwardness = cos(phi) and sideness = -sin(phi).
        float phi = MathF.Atan2(-_sideness, _forwardness);

        bool rotating = Strafe is StrafeStyle.Split or StrafeStyle.WholeBody
                     || (Strafe == StrafeStyle.LowerBody && _animator.TwistBone >= 0);

        if (rotating)
        {
            // Backing up is a clip choice, not an angle. Any rearward component
            // wins over the strafe, exactly as the reference's flag ladder has
            // BACKWARD dominate STRAFE_LEFT/RIGHT - so back-and-left plays the
            // backwards cycle and the body leans through its strafe offset
            // rather than turning round to run off.
            //
            // The measured path has no key flags to read, so it keeps the old
            // 110-degree test on the travel angle, which is the same boundary
            // expressed in the only terms it has.
            const float backwards = 1.92f;   // about 110 degrees

            bool backing = state.HasIntent
                ? state.Forward < -0.01f
                : MathF.Abs(phi) > backwards;

            if (backing)
                return LocomotionClip(
                    _animator.Resolve("player", BaseAnimationTrack, 13, false, 4, 5, 0),
                    state.Walking, out rate);

            return LocomotionClip(
                state.Walking
                    ? _animator.Resolve("player", BaseAnimationTrack, 4, false, 5, 0)
                    : _animator.Resolve("player", BaseAnimationTrack, 5, false, 4, 0),
                state.Walking, out rate);
        }

        // Fallback: discrete sideways clips, no twist. Kept so the two can be
        // compared in one click, but this is the version that looks like a
        // dance step at running speed.
        if (MathF.Abs(_forwardness) >= MathF.Abs(_sideness) * 1.2f)
        {
            if (_forwardness >= 0f)
                return LocomotionClip(
                    state.Walking
                        ? _animator.Resolve("player", BaseAnimationTrack, 4, false, 5, 0)
                        : _animator.Resolve("player", BaseAnimationTrack, 5, false, 4, 0),
                    state.Walking, out rate);

            return LocomotionClip(
                _animator.Resolve("player", BaseAnimationTrack, 13, false, 4, 5, 0),
                state.Walking, out rate);
        }

        var sideways = _sideness > 0f
            ? (state.Walking
                ? _animator.Resolve("player", BaseAnimationTrack, 12, false, 92, 5, 4, 0)
                : _animator.Resolve("player", BaseAnimationTrack, 92, false, 12, 5, 4, 0))
            : (state.Walking
                ? _animator.Resolve("player", BaseAnimationTrack, 11, false, 93, 5, 4, 0)
                : _animator.Resolve("player", BaseAnimationTrack, 93, false, 11, 5, 4, 0));
        return LocomotionClip(sideways, state.Walking, out rate);
    }

    /// <summary>
    /// Match foot-cycle playback to the speed authored into the selected M2
    /// sequence. The old code divided by controller run/walk constants, which
    /// only works if every clip's stride was authored for exactly those values.
    /// ModelScale participates because scaling a model scales its stride.
    ///
    /// The numerator is the COMMANDED speed where we have it. It used to be the
    /// raw per-frame displacement, which modulated the leg cycle with frame-time
    /// jitter, with every ground snap, and with every yard of wall slide - a
    /// low-grade wobble on the run that never resolved into anything nameable.
    /// </summary>
    private M2Animator.Clip? LocomotionClip(
        M2Animator.Clip? clip, bool walking, out float rate)
    {
        float fallback = walking ? _config.Movement.WalkSpeed : _config.Movement.RunSpeed;
        if (clip?.AnimationId == 13 && !walking)
            fallback = _config.Movement.BackwardSpeed;

        float authored = (clip?.MoveSpeed ?? 0f) * ModelScale;
        float strideSpeed = float.IsFinite(authored) && authored > 0.1f && authored < 100f
            ? authored
            : fallback;

        if (strideSpeed < 0.1f) strideSpeed = 5f;
        rate = Math.Clamp(_instantGroundSpeed / strideSpeed, 0.35f, 2.5f);
        return clip;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public Matrix4x4 BuildTransform(in UnitState state)
    {
        // The strafe angle goes into the model's own heading in WholeBody mode,
        // which is the entire mechanism: the character turns to face where he
        // is travelling and the ordinary run cycle plays.
        //
        // Note it does NOT touch state.Yaw. That is the character's facing, the
        // camera sits behind it, and a movement packet will want it in Phase 2.
        // Only the drawn model turns, so strafing right shows you his side
        // while the view stays where you are pointed.
        // Split turns the whole model too - the torso is then pulled back part
        // of the way by its own yaw, which is where the 90-against-60 comes from.
        // Riding: the steed already carries scale, facing and ground placement, and the
        // seat matrix is expressed in ITS model space — so the rider adds nothing but its
        // own size. Applying the basis or the heading again here would rotate the body off
        // the saddle it is parented to.
        if (MountSeat is { } seat) return Matrix4x4.CreateScale(ModelScale) * seat;

        bool bodyTurns = Strafe is StrafeStyle.Split or StrafeStyle.WholeBody;
        float bodyYaw = bodyTurns && !BindPose && !FrozenStandPose && StandPreviewTime is null
            ? _moveYaw
            : 0f;

        float heading = state.Yaw + HeadingOffsetDegrees * MathF.PI / 180f + bodyYaw;
        var position = state.Position + new Vector3(0f, 0f, ZOffset);

        return Matrix4x4.CreateScale(ModelScale)
             * Matrix4x4.CreateRotationY(heading)
             * ModelToWorld
             * Matrix4x4.CreateTranslation(position);
    }

    public SpellUnitPose SpellPose(in UnitState state)
    {
        if (_m2 is null) return SpellUnitPose.Missing;
        return new SpellUnitPose(true, state.Position, state.Yaw, BuildTransform(state), _m2, _skin);
    }

    public unsafe void Render(Camera camera, in UnitState state)
    {
        _attached?.BeginGlowFrame();
        if (!Enabled || _m2 is null || _shader is null || _pieces.Count == 0) return;

        int bones = _animator?.BoneCount ?? 0;
        if (_animator is not null)
        {
            // The hip clamp is a joint limit, so it belongs where the hips
            // actually do the work. In Split the legs come from the model
            // heading, which has no such limit - and must not, or the standing
            // chase would be capped at the wrong angle.
            float maxTwist = MaxTwistDegrees * MathF.PI / 180f;

            // Mounted takes the same exit as bind/frozen: the seated pose is authored whole,
            // and twisting its hips against a saddle it is parented to only breaks it.
            _animator.LowerBodyYaw =
                BindPose || FrozenStandPose || StandPreviewTime is not null || Mounted ||
                Strafe != StrafeStyle.LowerBody
                    ? 0f
                    : Math.Clamp(_moveYaw, -maxTwist, maxTwist);

            // TorsoYaw is the moving-strafe counter-twist. The stationary frozen chase is a
            // lag between aim and whole-body heading; its slower release catch-up is handled
            // by StandingBodyStep, while sparse shuffle shoulders inherit Stand in M2Animator.
            _animator.TorsoYaw = CharacterPoseLaw.TorsoCounterYaw(
                BindPose || StandPreviewTime is not null || Mounted, FrozenStandPose,
                Strafe == StrafeStyle.Split,
                state.Moving, ForceAngleDegrees != 0f, TorsoFollow, _moveYaw);
            if (StandPreviewTime is float standTime)
            {
                _animator.Evaluate(_animator.Find(0), standTime, _globalTime, _skin);
            }
            else if (BindPose)
            {
                _animator.Evaluate(null, 0f, _globalTime, _skin);
            }
            else if (FrozenStandPose)
            {
                _animator.Evaluate(_animator.Find(0), 0f, 0f, _skin);
            }
            else
            {
                M2Animator.Clip? rightOverlay = _sheathCeremonyActive &&
                    _rightSheathOverlay is { } right && _sheathOverlayTime <= right.DurationSeconds
                        ? right : null;
                M2Animator.Clip? leftOverlay = _sheathCeremonyActive &&
                    _leftSheathOverlay is { } left && _sheathOverlayTime <= left.DurationSeconds
                        ? left : null;
                _animator.EvaluateWithArmOverlays(_clip, _clipTime,
                                   _previousClip, _previousClipTime, BlendWeightNow(),
                                   rightOverlay, _sheathOverlayTime,
                                   leftOverlay, _sheathOverlayTime,
                                   _torsoOverlayForRender, _actionOverlayTime,
                                   _globalTime, _skin,
                                   _combatReaction, _combatReactionTime,
                                   CombatReactionWeight(), _combatReactionMasked,
                                   CharacterPoseLaw.OneshotOverlayWeight);
            }
            M2Animator.Pack(_skin, Math.Min(bones, M2Animator.MaxBones), _packed);
        }

        var modelTransform = BuildTransform(state);
        modelTransform.M41 -= camera.Position.X;
        modelTransform.M42 -= camera.Position.Y;
        modelTransform.M43 -= camera.Position.Z;

        _shader.Use();
        _shader.Set("uModel", modelTransform);
        _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uShadowWrap", ShadowSoftness);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTexture", 0);
        _shader.Set("uHasTexture2", 0);
        _shader.Set("uSteadyModulatedGlow", 0);
        float bodyAlpha = state.ApplyBodyVisual ? Math.Clamp(state.BodyAlpha, 0f, 1f) : 1f;
        Vector3 bodyTint = state.ApplyBodyVisual ? state.BodyTint : Vector3.One;
        _shader.Set("uBodyAlpha", bodyAlpha);
        _shader.Set("uBodyTint", bodyTint);
        _shader.Set("uUnlit", 0);
        _shader.Set("uFogPolicy", 0);
        CarriedLightFrame.Upload(_shader, camera.Position);

        if (bones > 0)
            _shader.SetVec4Array("uBones", _packed, Math.Min(bones, M2Animator.MaxBones) * 3);
        _shader.Set("uBoneCount", Math.Min(bones, M2Animator.MaxBones));

        _gl.BindVertexArray(_vao);

        bool cullingOn = true;
        VisiblePieces = 0;

        // Opaque/alpha-test first, then transparent/additive with depth writes
        // disabled. This preserves the M2 material distinction without making
        // translucent cards reject each other through the depth buffer.
        bool bodyTranslucent = bodyAlpha < 1f - AuraVisualLaw.AlphaSettledEpsilon;
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;

            if (transparentPass)
            {
                // Depth TEST stays on - blended geometry still hides behind
                // walls. Depth WRITE goes off, so two blended surfaces cannot
                // reject each other.
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.Blend);
            }

            DrawPieces(transparentPass, bodyTranslucent, ref cullingOn);

            if (transparentPass)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);

        // Attached items ride the SAME skin matrices that were just evaluated,
        // which is what makes a pauldron follow the shoulder.
        if (_attached is not null)
        {
            _attached.SunDirection = SunDirection;
            _attached.SunColor = SunColor;
            _attached.SunIntensity = SunIntensity;
            _attached.AmbientColor = AmbientColor;
            _attached.AmbientIntensity = AmbientIntensity;
            _attached.ShadowSoftness = ShadowSoftness;
            _attached.FogColor = FogColor;
            _attached.FogStart = FogStart;
            _attached.FogEnd = FogEnd;
            _attached.SheathState = SheathState;
            _attached.BodyAlpha = bodyAlpha;
            _attached.BodyTint = bodyTint;
        }
        _attached?.Render(camera, modelTransform, _m2, _skin, state.Guid, _globalTime);
    }

    /// <summary>
    /// Vanilla's wound secondary starts at 75% and smoothstep-decays to zero over the clip.
    /// Keeping this in the renderer makes the lifetime and the sampled pose share one clock.
    /// </summary>
    private float CombatReactionWeight()
    {
        if (_combatReaction is null || _combatReaction.DurationSeconds <= 0f) return 0f;
        float remaining = Math.Clamp(
            1f - _combatReactionTime / _combatReaction.DurationSeconds, 0f, 1f);
        return (3f - 2f * remaining) * remaining * remaining * 0.75f;
    }

    public IReadOnlyList<ItemGlowPlacement> ItemGlowPlacements =>
        _attached?.GlowPlacements ?? Array.Empty<ItemGlowPlacement>();
    public IReadOnlyList<CarriedLightPlacement> CarriedLightPlacements =>
        _attached?.CarriedLights ?? Array.Empty<CarriedLightPlacement>();
    public IReadOnlyList<FishingPoleTipPlacement> FishingPoleTips =>
        _attached?.FishingPoleTips ?? Array.Empty<FishingPoleTipPlacement>();
    public void BeginItemGlowFrame() => _attached?.BeginGlowFrame();

    private unsafe void DrawPieces(bool transparentPass, bool bodyTranslucent, ref bool cullingOn)
    {
        foreach (var piece in _pieces)
        {
            if (!ShowAllGeosets && !piece.Visible) continue;
            if ((bodyTranslucent || piece.Transparent) != transparentPass) continue;

            var slot = piece.SlotIndex >= 0 ? _slots[piece.SlotIndex] : null;

            Texture? drawTexture = slot?.Texture;
            bool isHeadPiece = (piece.Category == 0 && piece.Variant > 0) || piece.Category == 7;
            if (isHeadPiece && slot is not null &&
                (slot.Fill == SlotFill.BodySkin || slot.Type == 1) && _bareSkin is not null)
            {
                drawTexture = _bareSkin;   // hair/scalp/ears never sample the dressed (geared) atlas
            }

            bool unbound = slot is null || slot.Fill == SlotFill.Unbound || drawTexture is null;

            // SuperUI emits transparent materials for unresolved client-filled
            // slots. Rendering them as solid grey creates phantom cape/facial-
            // hair/skin-extra surfaces. Magenta mode intentionally overrides
            // this so those assignments can still be diagnosed.
            if (unbound && !MagentaUnbound && slot?.Type is 2 or 7 or 8)
                continue;

            if (piece.TwoSided && cullingOn)
            {
                _gl.Disable(EnableCap.CullFace);
                cullingOn = false;
            }
            else if (!piece.TwoSided && !cullingOn)
            {
                _gl.Enable(EnableCap.CullFace);
                cullingOn = true;
            }

            if (unbound)
            {
                if (MagentaUnbound && _magenta is not null)
                {
                    _magenta.Bind(0);
                    _shader.Set("uHasTexture", 1);
                    _shader.Set("uAlphaCutoff", 0f);
                }
                else
                {
                    _shader.Set("uHasTexture", 0);
                    _shader.Set("uAlphaCutoff", 0f);
                }
            }
            else
            {
                drawTexture!.Bind(0);
                _shader.Set("uHasTexture", 1);

                // Blend mode decides whether alpha CUTS or COMPOSITES, and
                // doing both is how a soft hair edge turns into a hard one.
                //   0  opaque      no cut at all
                //   1  alpha key   cut, no blend
                //   2+ blended     blend, no cut
                float cutoff = piece.BlendMode switch
                {
                    0 => 0f,
                    1 => MathF.Min(slot!.AlphaCutoff, AlphaCutoff),
                    _ => 0f,
                };
                _shader.Set("uAlphaCutoff", cutoff);

                if (transparentPass) ApplyBlendMode(piece.BlendMode);
            }

            _gl.DrawElements(PrimitiveType.Triangles, piece.IndexCount,
                DrawElementsType.UnsignedShort, (void*)(piece.IndexStart * sizeof(ushort)));

            VisiblePieces++;
        }
    }

    /// <summary>
    /// Copied value for value from DoodadRenderer. These are not defaults to be
    /// The atmosphere supplies these colours and intensities. The character
    /// shader then applies the legacy Model2 response instead of WMO Lambert.
    /// </summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public float ShadowSoftness { get; set; } = 0f;   // zero selects in-world Model2 lighting; the glue booth uses its nonzero wrap preset
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    /// <summary>Global ceiling on the per-slot cutoff. Drag to zero to prove alpha is the culprit.</summary>
    public float AlphaCutoff { get; set; } = 0.35f;

    /// <summary>
    /// Drop every per-model GPU/texture/geoset resource so a re-Load (e.g. swapping the
    /// startup test body to the logged-in character) does NOT stack the previous model.
    /// BuildPieces, BuildTextureSlots and BuildGpuBuffers all APPEND, so re-loading without
    /// this doubled the geometry - every hairstyle at once, warped limbs. No-op on first load.
    /// </summary>
    private void ResetModelState()
    {
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }

        foreach (var texture in _slots.Select(s => s.Texture)
                     .Where(t => t is not null &&
                                 !ReferenceEquals(t, _bareSkin) &&
                                 !ReferenceEquals(t, _dressedSkin))
                     .Distinct())
            texture!.Dispose();

        _bareSkin?.Dispose(); _bareSkin = null;
        _dressedSkin?.Dispose(); _dressedSkin = null;
        _magenta?.Dispose(); _magenta = null;

        _slots.Clear();
        _pieces.Clear();
        _headDiag.Clear();
        _bodySlotIndex = -1;
        _baseSkin = null;
        _animator = null;
        CancelSheathCeremony();

        // Every clip reference has to go, not just the current one. A Clip holds
        // a per-bone array sized to the animator that baked it, so a stale
        // outgoing clip surviving a model swap would be sampled against the new
        // skeleton's bone count - an index out of range at best, and a silently
        // wrong pose at worst.
        _clip = null;
        _previousClip = null;
        _landClip = null;
        _jumpStartClip = null;
        _jumpArcActive = false;
        _jumpHangShown = false;
        _blendRemaining = 0f;
        _blendDuration = 0f;
        _clipTime = 0f;
        _previousClipTime = 0f;
        _hasBodyYaw = false;
        _hasLastPosition = false;
        _wasAirborne = false;

        BoneOverflow = false;
    }

    public void Dispose()
    {
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);

        // Slots share textures, so dispose distinct instances only - and skip
        // the composited atlas, which is disposed on its own below.
        foreach (var texture in _slots.Select(s => s.Texture)
                     .Where(t => t is not null &&
                                 !ReferenceEquals(t, _bareSkin) &&
                                 !ReferenceEquals(t, _dressedSkin))
                     .Distinct())
            texture!.Dispose();

        _attached?.Dispose();
        _bareSkin?.Dispose();
        _dressedSkin?.Dispose();
        _magenta?.Dispose();
        _shader?.Dispose();

        _slots.Clear();
        _pieces.Clear();
    }
}
