using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// Runtime skeletal animation for a vanilla M2.
///
/// WHAT THIS IS
///   SkinnedGlbWriter solved vanilla M2 animation once already, but it solved
///   it by BAKING into glTF for three.js to play back. This is the same maths
///   evaluated live, on the CPU, into an array of skinning matrices that go
///   straight to a uniform. No glTF, no SharpGLTF, no bake step.
///
/// THE COORDINATE QUESTION IS ALREADY ANSWERED
///   M2Reader converts EVERYTHING on the way in, to one consistent Y-up space:
///
///       render vertices   (x, y, z)      -> (x, z, -y)
///       bone pivots       (x, y, z)      -> (x, z, -y)
///       translation keys  (x, y, z)      -> (x, z, -y)
///       rotation keys     (x, y, z, w)   -> (x, z, -y, w)
///       scale keys        (x, y, z)      -> (x, z, y)
///
///   Vertices, pivots and tracks therefore all live in the SAME space, and
///   skinning needs no basis anywhere. The only conversion left is model space
///   to world space, and that belongs to the renderer's instance matrix, not
///   here. Do not add a basis to this file.
///
///   The rotation mapping deliberately diverges from WMV's
///   (-qx, -qz, qy, qw): that one assembles the body correctly and then rotates
///   every joint the wrong way round. See M2Reader.ParseBones.
///
/// THE TRS MAPPING (from SkinnedGlbWriter, do not re-derive)
///   M2's own convention is  M = T(pivot) * T(trans) * R * S * T(-pivot),
///   i.e. the TRS is expressed IN THE PIVOT FRAME. Put the bone AT its pivot
///   relative to its parent and that conjugation disappears:
///
///       rest local     = T(pivot - parent.pivot)
///       animated local = S(scale) * R(rot) * T(pivot - parent.pivot + trans)
///       global         = local * parentGlobal
///
///   (System.Numerics is row-vector, so that product order reads
///   scale-then-rotate-then-translate.)
///
/// WHY THE INVERSE BIND MATRIX IS FREE
///   Accumulate the rest locals up any chain and the translations simply add:
///   globalBind(i) = T(pivot_i). So inverseBind(i) = T(-pivot_i) exactly, with
///   no matrix inversion and no numerical error.
///
///   That has a useful consequence: with no clip playing, every skin matrix is
///   the identity and the model renders in bind pose, byte-identical to drawing
///   it as a static mesh. So "the model is in the wrong place" and "the
///   animation is wrong" can never be the same bug.
///
/// CLIP SELECTION ACCEPTS BOTH VANILLA ENCODINGS
///   WoWee slices shared arrays through AnimationRange[sequenceIndex]. Some
///   character M2s instead repeat a whole-track sentinel range and rely on the
///   sequence header's absolute timestamps. M2AnimTrack validates the range
///   against that header window and falls back when the range is uninformative.
/// </summary>
public sealed class M2Animator
{
    public enum ResolutionKind { Exact, BakedOnDemand, Fallback, Missing, Substituted }

    public readonly record struct Resolution(
        Clip? Clip, int RequestedId, int PlayedId, ResolutionKind Kind);

    public Action<string, int, Resolution>? ResolutionSink { get; set; }

    /// <summary>
    /// Bone budget. MEASURED, not assumed - and the first value was wrong.
    ///
    /// HumanMale.m2 has 119 bones. Vanilla character models carry a full set of
    /// finger and facial joints on top of the body skeleton, so "a humanoid has
    /// maybe fifty bones" is simply false here.
    ///
    /// The first attempt set this to 80 on the theory that 80 * 3 vec4 = 960
    /// float components fits inside the 1024 GL 3.3 guarantees. It does, but it
    /// does not fit a character: bones 80-118 were never uploaded and every
    /// vertex weighted to them was clamped onto bone 79, dragging a third of
    /// the mesh onto one arbitrary joint.
    ///
    /// THAT FAILURE WAS INVISIBLE IN BIND POSE, because bone 79's skin matrix
    /// is the identity there like every other. Bind pose looked perfect and
    /// animation looked like a folded paper alien. Guard below.
    ///
    /// 160 * 3 = 480 vec4 = 1920 components; with uViewProjection and uModel
    /// that is 1952. Above the 1024 the spec guarantees, comfortably inside the
    /// 4096 both of Nico's GPUs report, and inside a conservative 2048 too. If
    /// a driver ever cannot take it, Shader.cs throws at link time with the
    /// full driver log - loud, immediate and diagnosable.
    ///
    /// RAISE THIS IN TWO PLACES OR NOT AT ALL: here and character.vert's
    /// MAX_BONES. They must agree.
    ///
    /// The permanent answer, when Phase 2 puts fifty units on screen, is a
    /// uniform buffer object: GL 3.3 guarantees a 16 KB block, which is 341
    /// bones, and it uploads once per unit instead of as loose uniforms.
    /// </summary>
    public const int MaxBones = 160;

    /// <summary>Per-bone keyframes for one clip. Empty arrays mean "hold the rest pose".</summary>
    public struct BoneChannels
    {
        public float[] TranslationTimes;
        public Vector3[] TranslationKeys;
        public float[] RotationTimes;
        public Quaternion[] RotationKeys;
        public float[] ScaleTimes;
        public Vector3[] ScaleKeys;
    }

    public sealed class Clip
    {
        public int SequenceIndex;
        public int AnimationId;
        public string Name = "";
        public float DurationSeconds;
        public float MoveSpeed;
        public bool Looping;
        public Vector3 BoundsCenter;
        public float BoundsRadius;

        /// <summary>
        /// Seconds to cross-fade INTO this clip, straight off the M2 sequence's
        /// blendTime field. Zero means the file asks for a hard cut.
        ///
        /// The caller decides what to do with a zero - see
        /// CharacterRenderer.BlendSecondsFor, which floors locomotion
        /// transitions rather than letting a leg cycle snap to frame one.
        /// </summary>
        public float BlendSeconds;

        /// <summary>Raw sequence flags, logged so we learn what they mean rather than assume.</summary>
        public uint SourceFlags;

        public BoneChannels[] Bones = [];

        /// <summary>Bones that actually move in this clip - purely for the log line.</summary>
        public int AnimatedBones;
    }

    private readonly M2Model _m2;
    private readonly int _boneCount;

    private readonly short[] _parent;
    private readonly Vector3[] _pivot;
    private readonly Vector3[] _restTranslation;

    /// <summary>Parents-before-children evaluation order. M2 files are usually
    /// already sorted this way, but nothing in the format promises it.</summary>
    private readonly int[] _order;

    private readonly Matrix4x4[] _global;
    private int _rightArmRoot = -1;
    private int _leftArmRoot = -1;

    private readonly Dictionary<int, Clip> _clips = [];
    private readonly Dictionary<int, Clip> _sequenceClips = [];

    public IReadOnlyDictionary<int, Clip> Clips => _clips;
    public int BoneCount => _boneCount;
    public bool HasArmOverlayRoots => _rightArmRoot >= 0 && _leftArmRoot >= 0;

    /// <summary>
    /// Base pose for the sparse ShuffleLeft/ShuffleRight clips. Those clips only key the
    /// turning bones; treating every absent channel as bind pose lifts the character's arms.
    /// CharacterRenderer supplies Stand here. Other M2 users leave it null, so spell and
    /// creature animation playback is unchanged.
    /// </summary>
    public Clip? TurnBasePose { get; set; }

    /// <summary>
    /// Extra yaw applied to the LOWER BODY only, in radians about the model's
    /// vertical axis. Zero for normal playback.
    ///
    /// This is how strafing actually looks in game, and it is not an animation
    /// choice: the hips and legs swing to point along the direction of travel
    /// while the torso keeps facing forward, and the ordinary run cycle plays
    /// underneath. Selecting a separate sideways clip instead gives a character
    /// shuffling its feet in place like a dance step, which is what this
    /// replaced.
    ///
    /// It also handles diagonals for free, which no set of discrete clips can:
    /// forward-and-right is simply forty-five degrees of twist.
    /// </summary>
    public float LowerBodyYaw { get; set; }

    /// <summary>
    /// The bone where the twist is cancelled. Everything from here upward rides
    /// along unrotated: the hips, or the low spine on a model that has no
    /// hips bone.
    ///
    /// Settable because it comes from the key-bone table, which is a convention
    /// rather than a guarantee. If the torso twists with the legs, or the twist
    /// begins at the wrong vertebra, this is the number to change - and the HUD
    /// exposes it, so that is one drag instead of one rebuild.
    ///
    /// -1 disables the mechanism entirely.
    /// </summary>
    /// <summary>
    /// The bone whose SUBTREE carries the twist. The hips: everything hanging
    /// below it - thighs, calves, feet - rotates with it, and nothing else
    /// moves.
    ///
    /// THIS WAS THE OTHER WAY ROUND AND IT WAS WRONG. The first version twisted
    /// the whole skeleton at the root and cancelled it at the hips, on the
    /// theory that "everything except the torso" is the legs. It is not: with
    /// the bone the key-bone table hands back, everything-except-its-subtree is
    /// the UPPER body, so the torso swung and the legs stayed planted - the
    /// exact inverse of the goal.
    ///
    /// The empirical reading is what fixed it. Cancelling at bone N left the
    /// upper body rotating, which says N's subtree IS the lower body, which
    /// says the twist belongs ON that subtree rather than everywhere else. So
    /// it is applied at the bone now, and nothing needs cancelling.
    ///
    /// Settable, because which bone this is comes from a convention rather than
    /// a guarantee. -1 disables the mechanism.
    /// </summary>
    public int TwistBone { get; set; } = -1;

    /// <summary>
    /// True applies the twist to <see cref="TwistBone"/>'s subtree, which is
    /// what the lower body wants. False restores the original scheme - twist
    /// everything, cancel at that bone - so the two can be compared in a click
    /// if a model's rig turns out to be the other shape.
    /// </summary>
    public bool TwistSubtree { get; set; } = true;

    /// <summary>
    /// A SECOND yaw, applied to the torso subtree, in radians.
    ///
    /// This is what the real client does and what neither of my first two
    /// attempts was: strafing is not "turn the body" or "turn the legs", it is
    /// BOTH AT DIFFERENT ANGLES. Nico measured it by eye at roughly ninety
    /// degrees on the legs and sixty on the torso, and WoWee's renderer header
    /// carries exactly the hook for it - `setInstanceTorsoYaw(id, deltaYawRad)`
    /// with a per-instance `torsoYawOverrideRad`. A DELTA on the torso, on top
    /// of whatever the body is already doing.
    ///
    /// Same mechanism as LowerBodyYaw: appended at a bone, so it lands on that
    /// bone's whole subtree and nothing else.
    /// </summary>
    public float TorsoYaw { get; set; }

    /// <summary>
    /// The bone whose subtree is the UPPER body - spine, arms, head. Distinct
    /// from <see cref="TwistBone"/>, whose subtree is the legs.
    ///
    /// Resolved from key bone 4 (SpineLow). -1 disables the torso half.
    /// </summary>
    public int TorsoBone { get; set; } = -1;

    /// <summary>Key-bone IDs from the vanilla M2 key-bone table.</summary>
    private const int KeyBoneSpineLow = 4;
    private const int KeyBoneWaist = 5;

    private M2Animator(M2Model m2)
    {
        _m2 = m2;
        _boneCount = m2.Bones.Count;

        _parent = new short[_boneCount];
        _pivot = new Vector3[_boneCount];
        _restTranslation = new Vector3[_boneCount];
        _global = new Matrix4x4[_boneCount];

        for (int i = 0; i < _boneCount; i++)
        {
            var bone = m2.Bones[i];
            short p = bone.ParentBone;
            if (p < 0 || p >= _boneCount || p == i) p = -1;

            _parent[i] = p;
            _pivot[i] = bone.Pivot;
            _restTranslation[i] = p >= 0 ? bone.Pivot - m2.Bones[p].Pivot : bone.Pivot;
        }

        _order = BuildEvaluationOrder(_parent);
        ResolveArmRoots(m2);
    }

    /// <summary>
    /// Build an animator and bake the requested AnimationData.dbc IDs.
    /// Returns null for a model with no skeleton - a doodad, in other words,
    /// which the caller should draw through DoodadRenderer instead.
    /// </summary>
    public static M2Animator? Build(M2Model m2, IEnumerable<int> animationIds,
        bool includeStaticSequences = false)
    {
        if (!m2.HasSkeleton) return null;

        var animator = new M2Animator(m2);
        animator.ResolveTwistBone(m2);

        foreach (int id in animationIds.Distinct())
        {
            var clip = animator.Bake(id, includeStaticSequences);
            if (clip is not null)
            {
                animator._clips[id] = clip;
                animator._sequenceClips[clip.SequenceIndex] = clip;
            }
        }

        return animator;
    }

    /// <summary>
    /// Find the bone that separates legs from torso, and say out loud what was
    /// found and how. The key-bone table is a convention, not a guarantee.
    ///
    /// TWO INDEPENDENT ROUTES, because they fail independently:
    ///   1. Each M2Bone carries its own KeyBoneId. Direct, and works even when
    ///      the lookup array is short or zero-filled.
    ///   2. KeyBoneLookup[keyBoneId] -> bone index. The documented route, and
    ///      the one that quietly returns bone 0 when the array is not what we
    ///      assume it is.
    ///
    /// ROUTE 2 FAILING TO ZERO IS THE DANGEROUS CASE. Bone 0 is normally the
    /// root, and a twist cancelled AT the root cancels completely: the root
    /// appends it, that same bone immediately removes it, net effect nothing at
    /// all. Indistinguishable from the feature never being wired up, which is
    /// why it is rejected explicitly below.
    /// </summary>
    private void ResolveTwistBone(M2Model m2)
    {
        int hips = -1;
        string via = "";

        for (int i = 0; i < m2.Bones.Count && hips < 0; i++)
            if (m2.Bones[i].KeyBoneId == KeyBoneWaist) { hips = i; via = "bone.KeyBoneId Waist"; }

        for (int i = 0; i < m2.Bones.Count && hips < 0; i++)
            if (m2.Bones[i].KeyBoneId == KeyBoneSpineLow) { hips = i; via = "bone.KeyBoneId SpineLow"; }

        if (hips < 0)
        {
            hips = LookupKeyBone(m2, KeyBoneWaist);
            if (hips >= 0) via = "KeyBoneLookup Waist";
        }

        if (hips < 0)
        {
            hips = LookupKeyBone(m2, KeyBoneSpineLow);
            if (hips >= 0) via = "KeyBoneLookup SpineLow";
        }

        DumpKeyBones(m2);

        if (hips < 0)
        {
            Console.WriteLine("[anim] no hips or low-spine bone found - lower-body twist unavailable. " +
                              "Pick one from the table above with 'Twist bone (hips)' in the HUD.");
            return;
        }

        // Under the subtree scheme a root bone would rotate the entire model.
        // Visible rather than silent, so warn instead of refusing.
        if (_parent[hips] < 0)
            Console.WriteLine(
                $"[anim] WARNING bone {hips} resolved as the hips (via {via}) but it is a ROOT bone - " +
                "twisting it will swing the whole character. Pick another with 'Twist bone (hips)' in the HUD.");

        TwistBone = hips;

        ResolveTorsoBone(m2, hips);

        // The size of the subtree is the check that matters now. Two legs are
        // on the order of ten to twenty bones out of a hundred and nineteen; a
        // subtree holding most of the skeleton is the wrong bone and will swing
        // the whole character.
        int subtree = CountSubtree(hips);

        Console.WriteLine(
            $"[anim] lower-body twist applied at bone {hips} (via {via}), parent {_parent[hips]}, " +
            $"pivot height {_pivot[hips].Y:F2}, subtree holds {subtree} of {_boneCount} bone(s)");

        if (subtree > _boneCount / 2)
            Console.WriteLine(
                "[anim] WARNING that subtree is most of the skeleton - it is probably not the hips. " +
                "Pick another from the table above with 'Twist bone (hips)' in the HUD.");
    }

    /// <summary>
    /// Find the bone whose subtree is the upper body. Key bone 4 is SpineLow,
    /// and unlike the hips this one has a check that actually proves it: the
    /// torso must NOT be inside the leg subtree, or the two yaws would compound
    /// on the same geometry instead of splitting it.
    /// </summary>
    private void ResolveTorsoBone(M2Model m2, int hips)
    {
        int torso = -1;

        for (int i = 0; i < m2.Bones.Count && torso < 0; i++)
            if (m2.Bones[i].KeyBoneId == KeyBoneSpineLow) torso = i;

        if (torso < 0) torso = LookupKeyBone(m2, KeyBoneSpineLow);

        if (torso < 0)
        {
            Console.WriteLine("[anim] no low-spine bone - the torso half of the strafe is unavailable");
            return;
        }

        if (IsDescendant(torso, hips))
        {
            Console.WriteLine(
                $"[anim] bone {torso} sits INSIDE the leg subtree, so torso and leg yaw would compound. " +
                "Torso half disabled - pick another with 'Torso bone' in the HUD.");
            return;
        }

        TorsoBone = torso;
        Console.WriteLine(
            $"[anim] torso yaw applies at bone {torso}, parent {_parent[torso]}, " +
            $"subtree holds {CountSubtree(torso)} of {_boneCount} bone(s)");
    }

    private bool IsDescendant(int node, int ancestor)
    {
        int guard = 0;
        while (node >= 0 && guard++ < _boneCount)
        {
            if (node == ancestor) return true;
            node = _parent[node];
        }
        return false;
    }

    private void ResolveArmRoots(M2Model m2)
    {
        int ShoulderAncestor(uint handAttachmentId)
        {
            M2Attachment? attachment = m2.Attachments.FirstOrDefault(a => a.Id == handAttachmentId);
            int bone = attachment is null ? -1 : (int)attachment.BoneIndex;
            int guard = 0;
            while (bone >= 0 && bone < _boneCount && guard++ < _boneCount)
            {
                if (m2.Bones[bone].KeyBoneId is 2 or 3) return bone;
                bone = _parent[bone];
            }
            return -1;
        }

        _rightArmRoot = ShoulderAncestor(1);
        _leftArmRoot = ShoulderAncestor(2);
        if (HasArmOverlayRoots)
            Console.WriteLine($"[anim] sheath arm masks: right root {_rightArmRoot}, left root {_leftArmRoot}");
        else
            Console.WriteLine("[anim] no complete hand-to-shoulder arm masks; sheath ceremony will snap");
    }

    /// <summary>How many bones hang below this one, itself included.</summary>
    private int CountSubtree(int root)
    {
        int count = 0;

        // _order is parents-before-children, so one forward pass marks the whole
        // subtree without recursion.
        var inSubtree = new bool[_boneCount];
        foreach (int i in _order)
        {
            if (i == root) inSubtree[i] = true;
            else
            {
                short p = _parent[i];
                if (p >= 0 && inSubtree[p]) inSubtree[i] = true;
            }
            if (inSubtree[i]) count++;
        }

        return count;
    }

    /// <summary>
    /// Print every bone that claims a key-bone ID, so choosing the right one is
    /// reading a table rather than guessing among a hundred and nineteen.
    /// </summary>
    private void DumpKeyBones(M2Model m2)
    {
        var named = new List<string>();
        for (int i = 0; i < m2.Bones.Count; i++)
        {
            int key = m2.Bones[i].KeyBoneId;
            if (key < 0) continue;
            named.Add($"{i}:key{key}(p{_parent[i]},y{_pivot[i].Y:F2})");
        }

        Console.WriteLine($"[anim] KeyBoneLookup has {m2.KeyBoneLookup.Count} entries; " +
                          $"{named.Count} bone(s) carry a key-bone ID");

        if (named.Count > 0)
            Console.WriteLine("[anim] key bones: " + string.Join("  ", named));
    }

    private static int LookupKeyBone(M2Model m2, int keyBoneId)
    {
        if (keyBoneId < 0 || keyBoneId >= m2.KeyBoneLookup.Count) return -1;
        short index = m2.KeyBoneLookup[keyBoneId];
        return index >= 0 && index < m2.Bones.Count ? index : -1;
    }

    public Clip? Find(int animationId) => _clips.TryGetValue(animationId, out var c) ? c : null;

    public Clip? FindSequence(int sequenceIndex)
        => _sequenceClips.TryGetValue(sequenceIndex, out Clip? clip) ? clip : null;

    /// <summary>
    /// Bake one exact file-order sequence slot. Effect tracks are indexed by
    /// this slot, so an AnimationData id is not a sufficient identity when a
    /// model carries variations or duplicate ids.
    /// </summary>
    public Clip? FindSequenceOrBake(int sequenceIndex, bool includeStaticSequences = true)
    {
        if (_sequenceClips.TryGetValue(sequenceIndex, out Clip? cached)) return cached;
        Clip? clip = BakeSequence(sequenceIndex, includeStaticSequences);
        if (clip is null) return null;
        _sequenceClips[sequenceIndex] = clip;
        _clips.TryAdd(clip.AnimationId, clip);
        return clip;
    }

    /// <summary>Resolve an authored animation on first use and retain the baked clip.</summary>
    public Clip? FindOrBake(int animationId, bool includeStaticSequences = false)
    {
        if (_clips.TryGetValue(animationId, out Clip? cached)) return cached;
        Clip? clip = Bake(animationId, includeStaticSequences);
        if (clip is not null)
        {
            _clips[animationId] = clip;
            _sequenceClips[clip.SequenceIndex] = clip;
        }
        return clip;
    }

    /// <summary>
    /// Resolve one requested clip, optionally baking that exact clip and then walking an
    /// explicitly authored fallback chain. This is the single requested-versus-played
    /// decision point used by the runtime renderers.
    /// </summary>
    public Clip? Resolve(string unit, int track, int requestedId, bool bakeOnDemand,
        params int[] authoredFallbacks)
    {
        Clip? clip;
        ResolutionKind kind;
        if (_clips.TryGetValue(requestedId, out clip))
        {
            kind = ResolutionKind.Exact;
        }
        else if (bakeOnDemand && (clip = Bake(requestedId)) is not null)
        {
            _clips[requestedId] = clip;
            _sequenceClips[clip.SequenceIndex] = clip;
            kind = ResolutionKind.BakedOnDemand;
        }
        else
        {
            clip = null;
            foreach (int fallbackId in authoredFallbacks)
            {
                if (!_clips.TryGetValue(fallbackId, out clip) && bakeOnDemand)
                {
                    clip = Bake(fallbackId);
                    if (clip is not null)
                    {
                        _clips[fallbackId] = clip;
                        _sequenceClips[clip.SequenceIndex] = clip;
                    }
                }
                if (clip is null) continue;
                break;
            }
            kind = clip is null ? ResolutionKind.Missing : ResolutionKind.Fallback;
        }

        ResolutionSink?.Invoke(unit, track,
            new Resolution(clip, requestedId, clip?.AnimationId ?? -1, kind));
        return clip;
    }

    /// <summary>First clip in the list that this model actually has.</summary>
    public Clip? FindFirst(params int[] animationIds)
    {
        foreach (int id in animationIds)
            if (_clips.TryGetValue(id, out var c)) return c;
        return null;
    }

    // ── baking ───────────────────────────────────────────────────────────────

    private Clip? Bake(int animationId, bool includeStaticSequences = false)
    {
        int seqIdx = _m2.TryFindSequenceIndexByAnimationId(animationId);
        if (seqIdx < 0) return null;

        return BakeSequence(seqIdx, includeStaticSequences);
    }

    private Clip? BakeSequence(int seqIdx, bool includeStaticSequences)
    {
        if (seqIdx < 0 || seqIdx >= _m2.Sequences.Count) return null;

        var seq = _m2.Sequences[seqIdx];
        uint startMs = seq.StartTimestamp;
        uint endMs = seq.EndTimestamp;

        var clip = new Clip
        {
            SequenceIndex = seqIdx,
            AnimationId = seq.AnimationId,
            Name = AnimationName(seq.AnimationId),
            DurationSeconds = seq.DurationMs / 1000f,
            MoveSpeed = seq.MoveSpeed,
            Looping = seq.IsLooping,
            BoundsCenter = (seq.BoundsMinimum + seq.BoundsMaximum) * .5f,
            BoundsRadius = seq.BoundsRadius,
            BlendSeconds = seq.BlendTimeMs / 1000f,
            SourceFlags = seq.Flags,
            Bones = new BoneChannels[_boneCount],
        };

        for (int i = 0; i < _boneCount; i++)
        {
            var bone = _m2.Bones[i];
            var channels = new BoneChannels
            {
                TranslationTimes = [],
                TranslationKeys = [],
                RotationTimes = [],
                RotationKeys = [],
                ScaleTimes = [],
                ScaleKeys = [],
            };

            bool animated = false;

            var translationKeys = bone.Translation
                .EnumerateSequenceKeys(seqIdx, startMs, endMs).ToList();
            if (translationKeys.Count > 0)
            {
                channels.TranslationTimes = translationKeys.Select(k => k.timeMs / 1000f).ToArray();
                channels.TranslationKeys = translationKeys.Select(k => k.value).ToArray();
                animated = true;
            }

            var rotationKeys = bone.Rotation
                .EnumerateSequenceKeys(seqIdx, startMs, endMs).ToList();
            if (rotationKeys.Count > 0)
            {
                channels.RotationTimes = rotationKeys.Select(k => k.timeMs / 1000f).ToArray();
                channels.RotationKeys = rotationKeys
                    .Select(k => NormalizeQuaternion(
                        new Quaternion(k.value.X, k.value.Y, k.value.Z, k.value.W)))
                    .ToArray();
                animated = true;
            }

            var scaleKeys = bone.Scale
                .EnumerateSequenceKeys(seqIdx, startMs, endMs).ToList();
            if (scaleKeys.Count > 0)
            {
                channels.ScaleTimes = scaleKeys.Select(k => k.timeMs / 1000f).ToArray();
                channels.ScaleKeys = scaleKeys.Select(k => k.value).ToArray();
                animated = true;
            }

            clip.Bones[i] = channels;
            if (animated) clip.AnimatedBones++;
        }

        // A clip with no moving bones is worse than useless: it looks like a
        // frozen character and reads as a renderer bug rather than a data one.
        if (clip.AnimatedBones == 0 && !includeStaticSequences) return null;

        // Some sequences declare a zero-length window. Fall back to the longest
        // authored keyframe rather than dividing by zero later.
        if (clip.DurationSeconds <= 0.0001f)
        {
            float longest = 0f;
            foreach (var c in clip.Bones)
            {
                if (c.TranslationTimes.Length > 0) longest = MathF.Max(longest, c.TranslationTimes[^1]);
                if (c.RotationTimes.Length > 0) longest = MathF.Max(longest, c.RotationTimes[^1]);
                if (c.ScaleTimes.Length > 0) longest = MathF.Max(longest, c.ScaleTimes[^1]);
            }
            clip.DurationSeconds = longest;
        }

        return clip.DurationSeconds > 0.0001f ? clip : null;
    }

    // ── evaluation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fill <paramref name="skin"/> with one skinning matrix per bone.
    ///
    /// Pass a null clip for bind pose, which produces exact identities - the
    /// cheapest possible way to separate a placement problem from an animation
    /// problem.
    /// </summary>
    public void Evaluate(Clip? clip, float timeSeconds, float globalTimeSeconds, Matrix4x4[] skin)
        => Evaluate(clip, timeSeconds, null, 0f, 0f, globalTimeSeconds, skin);

    /// <summary>
    /// The same, cross-fading OUT of <paramref name="previous"/>.
    ///
    /// WHY THIS EXISTS AT ALL. Playback used to be a reference compare, a swap
    /// and `_clipTime = 0`: a hard cut with the leg cycle yanked back to its
    /// first frame. Every stand-to-run, every direction change and every
    /// landing popped, and no single one of them was wrong enough to point at -
    /// which is exactly why it read as "something about the movement is off"
    /// rather than as a bug with a name.
    ///
    /// The blend happens in TRS, not in matrices. Lerping two matrices shears
    /// the pose through the halfway point; lerping the translation, slerping the
    /// rotation and lerping the scale is the same thing three.js's AnimationMixer
    /// and every other mixer does, and it is the only version that stays rigid.
    ///
    /// <paramref name="previousWeight"/> is how much of the OUTGOING clip
    /// survives: 1 at the instant of the change, 0 when the fade is done.
    ///
    /// Global sequences are sampled ONCE, after the blend. They keep their own
    /// clock across clip changes by definition, so fading between two copies of
    /// the same global track would be meaningless work.
    /// </summary>
    public void Evaluate(Clip? clip, float timeSeconds,
                         Clip? previous, float previousTimeSeconds, float previousWeight,
                         float globalTimeSeconds, Matrix4x4[] skin)
        => EvaluateWithArmOverlays(clip, timeSeconds, previous, previousTimeSeconds,
            previousWeight, null, 0f, null, 0f, null, 0f, globalTimeSeconds, skin);

    /// <summary>
    /// Evaluate the live base pose and replace only keyed channels inside the requested arm
    /// subtrees. This is the vanilla per-slot sheath composition: gait and jump legs continue
    /// while each occupied hand performs its own draw/stow one-shot.
    ///
    /// <paramref name="torsoOverlay"/> is the same idea rooted at <see cref="TorsoBone"/>
    /// (SpineLow) instead of a shoulder: it replaces the keyed channels of the whole upper-body
    /// subtree - spine, arms, head - over the base pose, leaving the legs on the base clip. This
    /// is the Benilla <c>route_oneshot</c> "committed_lower" mask: while the lower body is
    /// committed (seated, moving, swimming or mounted - see CharacterPoseLaw.CommittedLower,
    /// where turn keys are still the one clause left out), an emote/swing/cast plays on the
    /// torso only rather than replacing the whole body. <paramref name="torsoOverlayWeight"/>
    /// selects replace (1) or Benilla's 8:1 blend over the still-running base gait. Applied
    /// BEFORE the arm overlays so an explicit
    /// per-hand sheath one-shot still wins on its own arm bones if the two ever coincide.
    ///
    /// <paramref name="reactionOverlay"/> is the wound-flinch secondary slot. Unlike the
    /// action overlays above it does not replace keyed channels: it blends toward them by
    /// <paramref name="reactionWeight"/> while the attack/base track keeps advancing beneath
    /// it. Benilla's reconstruction of the vanilla slot seeds that weight at 0.75 and decays
    /// it to zero over the wound clip. A masked wound is limited to the SpineLow subtree;
    /// a full-body wound uses every bone.
    /// </summary>
    public void EvaluateWithArmOverlays(Clip? clip, float timeSeconds,
                         Clip? previous, float previousTimeSeconds, float previousWeight,
                         Clip? rightOverlay, float rightOverlayTime,
                         Clip? leftOverlay, float leftOverlayTime,
                         Clip? torsoOverlay, float torsoOverlayTime,
                         float globalTimeSeconds, Matrix4x4[] skin,
                         Clip? reactionOverlay = null, float reactionOverlayTime = 0f,
                         float reactionWeight = 0f, bool reactionMasked = true,
                         float torsoOverlayWeight = 1f)
    {
        if (skin.Length < _boneCount)
            throw new ArgumentException($"skin array holds {skin.Length}, need {_boneCount}", nameof(skin));

        float t = ClipTime(clip, timeSeconds);
        float tPrevious = ClipTime(previous, previousTimeSeconds);
        Clip? basePose = IsSparseTurn(clip) ? TurnBasePose : null;
        Clip? previousBasePose = IsSparseTurn(previous) ? TurnBasePose : null;
        float basePoseTime = ClipTime(basePose, globalTimeSeconds);
        float previousBasePoseTime = ClipTime(previousBasePose, globalTimeSeconds);
        float rightTime = ClipTime(rightOverlay, rightOverlayTime);
        float leftTime = ClipTime(leftOverlay, leftOverlayTime);
        float torsoTime = ClipTime(torsoOverlay, torsoOverlayTime);
        bool torsoMasking = torsoOverlay is not null && TorsoBone >= 0 && TorsoBone < _boneCount;
        // Weight 1 replaces the subtree's keyed channels outright; anything below blends the
        // overlay over the base that is still running underneath. Benilla's masked route arms
        // at ONESHOT_OVERLAY_WEIGHT (8:1, CharacterPoseLaw.OneshotOverlayWeight) precisely
        // because the base is NOT masked out of the subtree - see the summary above.
        float torsoWeight = Math.Clamp(torsoOverlayWeight, 0f, 1f);
        if (float.IsNaN(torsoWeight)) torsoWeight = 1f;
        bool torsoBlending = torsoMasking && torsoWeight < 0.999f;
        float reactionTime = ClipTime(reactionOverlay, reactionOverlayTime);
        float reactionBlend = Math.Clamp(reactionWeight, 0f, 1f);
        if (float.IsNaN(reactionBlend)) reactionBlend = 0f;
        bool reactionActive = reactionOverlay is not null && reactionBlend > 0.001f;
        // A rig without SpineLow cannot honor the mask; Benilla degrades that case to the
        // full-body secondary node rather than silently dropping the flinch.
        bool reactionMasking = reactionActive && reactionMasked &&
            TorsoBone >= 0 && TorsoBone < _boneCount;

        float weight = previous is null ? 0f : Math.Clamp(previousWeight, 0f, 1f);
        if (float.IsNaN(weight)) weight = 0f;
        bool blending = previous is not null && weight > 0.001f;

        // Global tracks are clip-independent, so they are sampled whenever
        // ANYTHING is playing - including a frame where the outgoing clip is all
        // that is left.
        bool sampleGlobals = clip is not null || previous is not null;

        // A rotation appended AFTER a bone's global transform is applied in
        // MODEL space, about the model's own vertical axis through the origin
        // (which sits between the character's feet). Because every child does
        // `local * parentGlobal`, whatever is appended to a bone becomes the
        // rightmost factor of that bone's entire subtree - so appending once at
        // the hips rotates the hips, thighs, calves and feet, and touches
        // nothing above.
        bool twisting = LowerBodyYaw != 0f && TwistBone >= 0 && TwistBone < _boneCount;
        var twist = twisting ? Matrix4x4.CreateRotationY(LowerBodyYaw) : Matrix4x4.Identity;
        var untwist = twisting ? Matrix4x4.CreateRotationY(-LowerBodyYaw) : Matrix4x4.Identity;

        bool torsoTwisting = TorsoYaw != 0f && TorsoBone >= 0 && TorsoBone < _boneCount;
        var torsoTwist = torsoTwisting ? Matrix4x4.CreateRotationY(TorsoYaw) : Matrix4x4.Identity;

        foreach (int i in _order)
        {
            SampleLocal(clip, t, basePose, basePoseTime, i,
                out var translation, out var rotation, out var scale);

            if (blending)
            {
                SampleLocal(previous, tPrevious, previousBasePose, previousBasePoseTime, i,
                            out var outTranslation, out var outRotation, out var outScale);

                translation = Vector3.Lerp(translation, outTranslation, weight);
                rotation = Quaternion.Slerp(rotation, outRotation, weight);
                scale = Vector3.Lerp(scale, outScale, weight);
            }

            // Upper-body mask first, so a per-hand sheath overlay below still wins
            // on its own arm bones if the two are ever active together.
            if (torsoMasking && IsDescendant(i, TorsoBone))
            {
                if (torsoBlending)
                    BlendOverlayChannels(torsoOverlay!, torsoTime, torsoWeight, i,
                        ref translation, ref rotation, ref scale);
                else
                    ApplyOverlayChannels(torsoOverlay!, torsoTime, i,
                        ref translation, ref rotation, ref scale);
            }
            if (reactionActive && (!reactionMasking || IsDescendant(i, TorsoBone)))
                BlendOverlayChannels(reactionOverlay!, reactionTime, reactionBlend, i,
                    ref translation, ref rotation, ref scale);
            if (rightOverlay is not null && _rightArmRoot >= 0 && IsDescendant(i, _rightArmRoot))
                ApplyOverlayChannels(rightOverlay, rightTime, i,
                    ref translation, ref rotation, ref scale);
            if (leftOverlay is not null && _leftArmRoot >= 0 && IsDescendant(i, _leftArmRoot))
                ApplyOverlayChannels(leftOverlay, leftTime, i,
                    ref translation, ref rotation, ref scale);

            if (sampleGlobals)
            {
                // Global tracks do not belong to Stand, Walk, Run, or any
                // other clip. They keep their own clock across clip changes.
                // HumanMale bone 75 uses a global scale track to shrink the
                // closed-eyelid polygons away for most of a 6.633-second loop.
                var bone = _m2.Bones[i];
                if (TrySampleGlobal(bone.Translation, globalTimeSeconds, out Vector3 globalTranslation))
                    translation += globalTranslation;
                if (TrySampleGlobal(bone.Rotation, globalTimeSeconds, out Quaternion globalRotation))
                    rotation = globalRotation;
                if (TrySampleGlobal(bone.Scale, globalTimeSeconds, out Vector3 globalScale))
                    scale = globalScale;
            }

            var local = Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateFromQuaternion(rotation)
                      * Matrix4x4.CreateTranslation(translation);

            short p = _parent[i];
            var parentGlobal = p >= 0 ? _global[p] : Matrix4x4.Identity;

            // Old scheme, kept behind the switch: twist everything, take it back
            // off at this bone. That rotates everything OUTSIDE the subtree,
            // which on a vanilla rig is the upper body.
            if (twisting && !TwistSubtree && i == TwistBone) parentGlobal *= untwist;

            var global = local * parentGlobal;

            // The subtree scheme, and the default: one append at the hips.
            if (twisting && TwistSubtree && i == TwistBone) global *= twist;
            else if (twisting && !TwistSubtree && p < 0) global *= twist;

            // And the same again at the torso, with its own angle. Two bones,
            // two appends, two halves of the body at different yaws.
            if (torsoTwisting && i == TorsoBone) global *= torsoTwist;

            _global[i] = global;
        }

        for (int i = 0; i < _boneCount; i++)
            skin[i] = Matrix4x4.CreateTranslation(-_pivot[i]) * _global[i];
    }

    /// <summary>Wrap a looping clip's clock, clamp a one-shot's. Zero with no clip.</summary>
    private static float ClipTime(Clip? clip, float timeSeconds)
    {
        if (clip is null || clip.DurationSeconds <= 0f) return 0f;

        float t = clip.Looping
            ? timeSeconds % clip.DurationSeconds
            : Math.Clamp(timeSeconds, 0f, clip.DurationSeconds);

        return t < 0f ? t + clip.DurationSeconds : t;
    }

    private static bool IsSparseTurn(Clip? clip)
        => clip?.AnimationId is 11 or 12;

    /// <summary>
    /// One bone's animated local TRS at a clip time, rest pose included.
    ///
    /// The rest translation is folded in here rather than at the call site so
    /// that blending two of these is correct without a special case: both carry
    /// the same rest term, and lerping them leaves it untouched.
    /// </summary>
    private void SampleLocal(Clip? clip, float t, int bone,
                             out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        translation = _restTranslation[bone];
        rotation = Quaternion.Identity;
        scale = Vector3.One;

        if (clip is null) return;

        var c = clip.Bones[bone];

        if (c.TranslationTimes.Length > 0)
            translation += SampleVector3(c.TranslationTimes, c.TranslationKeys, t);

        if (c.RotationTimes.Length > 0)
            rotation = SampleQuaternion(c.RotationTimes, c.RotationKeys, t);

        if (c.ScaleTimes.Length > 0)
            scale = SampleVector3(c.ScaleTimes, c.ScaleKeys, t);
    }

    private void ApplyOverlayChannels(Clip overlay, float time, int bone,
        ref Vector3 translation, ref Quaternion rotation, ref Vector3 scale)
    {
        BoneChannels channels = overlay.Bones[bone];
        if (channels.TranslationTimes.Length > 0)
            translation = _restTranslation[bone] +
                SampleVector3(channels.TranslationTimes, channels.TranslationKeys, time);
        if (channels.RotationTimes.Length > 0)
            rotation = SampleQuaternion(channels.RotationTimes, channels.RotationKeys, time);
        if (channels.ScaleTimes.Length > 0)
            scale = SampleVector3(channels.ScaleTimes, channels.ScaleKeys, time);
    }

    /// <summary>
    /// Blend the channels actually authored by a secondary clip over the current pose. Missing
    /// channels leave the pose beneath untouched; treating them as bind pose would pull an
    /// attacking character toward a T-pose for the duration of every wound flinch.
    /// </summary>
    private void BlendOverlayChannels(Clip overlay, float time, float weight, int bone,
        ref Vector3 translation, ref Quaternion rotation, ref Vector3 scale)
    {
        BoneChannels channels = overlay.Bones[bone];
        if (channels.TranslationTimes.Length > 0)
        {
            Vector3 overlayTranslation = _restTranslation[bone] +
                SampleVector3(channels.TranslationTimes, channels.TranslationKeys, time);
            translation = Vector3.Lerp(translation, overlayTranslation, weight);
        }
        if (channels.RotationTimes.Length > 0)
        {
            Quaternion overlayRotation =
                SampleQuaternion(channels.RotationTimes, channels.RotationKeys, time);
            rotation = Quaternion.Slerp(rotation, overlayRotation, weight);
        }
        if (channels.ScaleTimes.Length > 0)
        {
            Vector3 overlayScale = SampleVector3(channels.ScaleTimes, channels.ScaleKeys, time);
            scale = Vector3.Lerp(scale, overlayScale, weight);
        }
    }

    /// <summary>
    /// Sample a sparse clip over a live base pose. Each channel is independent: a keyed turn
    /// rotation replaces Stand's rotation while an absent hand/shoulder channel keeps Stand.
    /// </summary>
    private void SampleLocal(Clip? clip, float t, Clip? basePose, float basePoseTime, int bone,
                             out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        if (basePose is null)
        {
            SampleLocal(clip, t, bone, out translation, out rotation, out scale);
            return;
        }

        SampleLocal(basePose, basePoseTime, bone, out translation, out rotation, out scale);
        if (clip is null) return;

        BoneChannels c = clip.Bones[bone];
        if (c.TranslationTimes.Length > 0)
            translation = _restTranslation[bone] +
                SampleVector3(c.TranslationTimes, c.TranslationKeys, t);
        if (c.RotationTimes.Length > 0)
            rotation = SampleQuaternion(c.RotationTimes, c.RotationKeys, t);
        if (c.ScaleTimes.Length > 0)
            scale = SampleVector3(c.ScaleTimes, c.ScaleKeys, t);
    }

    /// <summary>
    /// Pack skinning matrices for the shader: three vec4 per bone holding the
    /// ROWS of the transform, so the vertex shader is three dot products and
    /// there is no mat3x4 column-order ambiguity to get wrong.
    ///
    ///     out.x = dot(a, vec4(pos, 1))
    ///     out.y = dot(b, vec4(pos, 1))
    ///     out.z = dot(c, vec4(pos, 1))
    /// </summary>
    public static void Pack(Matrix4x4[] skin, int count, float[] destination)
    {
        for (int i = 0; i < count; i++)
        {
            var m = skin[i];
            int o = i * 12;

            destination[o + 0] = m.M11; destination[o + 1] = m.M21;
            destination[o + 2] = m.M31; destination[o + 3] = m.M41;

            destination[o + 4] = m.M12; destination[o + 5] = m.M22;
            destination[o + 6] = m.M32; destination[o + 7] = m.M42;

            destination[o + 8] = m.M13; destination[o + 9] = m.M23;
            destination[o + 10] = m.M33; destination[o + 11] = m.M43;
        }
    }

    // ── sampling ─────────────────────────────────────────────────────────────

    private static int UpperSegment(float[] times, float t)
    {
        // Binary search for the first key at or after t. Vanilla clips hold a
        // few dozen keys per bone, so this is not hot, but it is O(log n) and
        // costs nothing to write correctly.
        int lo = 0, hi = times.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private bool TryGetGlobalTime<T>(M2AnimTrack<T> track, float globalTimeSeconds,
                                     out float timeMs, out int count) where T : struct
    {
        timeMs = 0f;
        count = Math.Min(track.Timestamps.Count, track.Keys.Count);
        int globalIndex = track.GlobalSequence;
        if (globalIndex < 0 || globalIndex >= _m2.GlobalSequenceDurations.Count || count == 0)
            return false;

        uint durationMs = _m2.GlobalSequenceDurations[globalIndex];
        if (durationMs == 0) return true;

        timeMs = globalTimeSeconds * 1000f % durationMs;
        if (timeMs < 0f) timeMs += durationMs;
        return true;
    }

    private bool TrySampleGlobal(M2AnimTrack<Vector3> track, float globalTimeSeconds,
                                 out Vector3 value)
    {
        value = default;
        if (!TryGetGlobalTime(track, globalTimeSeconds, out float timeMs, out int count))
            return false;

        int hi = UpperGlobalSegment(track.Timestamps, count, timeMs);
        if (hi == 0 || timeMs <= track.Timestamps[0])
        {
            value = track.Keys[0];
            return true;
        }
        if (hi >= count)
        {
            value = track.Keys[count - 1];
            return true;
        }

        int lo = hi - 1;
        if (track.InterpolationType == 0 || track.Timestamps[hi] == track.Timestamps[lo])
        {
            value = timeMs == track.Timestamps[hi] ? track.Keys[hi] : track.Keys[lo];
            return true;
        }

        float amount = (timeMs - track.Timestamps[lo]) /
                       (track.Timestamps[hi] - track.Timestamps[lo]);
        value = Vector3.Lerp(track.Keys[lo], track.Keys[hi], amount);
        return true;
    }

    private bool TrySampleGlobal(M2AnimTrack<Vector4> track, float globalTimeSeconds,
                                 out Quaternion value)
    {
        value = Quaternion.Identity;
        if (!TryGetGlobalTime(track, globalTimeSeconds, out float timeMs, out int count))
            return false;

        int hi = UpperGlobalSegment(track.Timestamps, count, timeMs);
        if (hi == 0 || timeMs <= track.Timestamps[0])
        {
            value = NormalizeQuaternion(ToQuaternion(track.Keys[0]));
            return true;
        }
        if (hi >= count)
        {
            value = NormalizeQuaternion(ToQuaternion(track.Keys[count - 1]));
            return true;
        }

        int lo = hi - 1;
        var a = NormalizeQuaternion(ToQuaternion(track.Keys[lo]));
        var b = NormalizeQuaternion(ToQuaternion(track.Keys[hi]));
        if (track.InterpolationType == 0 || track.Timestamps[hi] == track.Timestamps[lo])
        {
            value = timeMs == track.Timestamps[hi] ? b : a;
            return true;
        }

        float amount = (timeMs - track.Timestamps[lo]) /
                       (track.Timestamps[hi] - track.Timestamps[lo]);
        value = Quaternion.Slerp(a, b, amount);
        return true;
    }

    private static int UpperGlobalSegment(IReadOnlyList<uint> times, int count, float timeMs)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (times[mid] < timeMs) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static Quaternion ToQuaternion(Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static Vector3 SampleVector3(float[] times, Vector3[] keys, float t)
    {
        if (keys.Length == 1) return keys[0];
        if (t <= times[0]) return keys[0];
        if (t >= times[^1]) return keys[^1];

        int hi = UpperSegment(times, t);
        int lo = Math.Max(hi - 1, 0);

        float span = times[hi] - times[lo];
        if (span <= 1e-6f) return keys[hi];

        return Vector3.Lerp(keys[lo], keys[hi], (t - times[lo]) / span);
    }

    private static Quaternion SampleQuaternion(float[] times, Quaternion[] keys, float t)
    {
        if (keys.Length == 1) return keys[0];
        if (t <= times[0]) return keys[0];
        if (t >= times[^1]) return keys[^1];

        int hi = UpperSegment(times, t);
        int lo = Math.Max(hi - 1, 0);

        float span = times[hi] - times[lo];
        if (span <= 1e-6f) return keys[hi];

        // Slerp, not Lerp. Nlerp is cheaper and looks fine on small deltas, but
        // vanilla run cycles have keys far enough apart that elbows visibly cut
        // corners.
        return Quaternion.Slerp(keys[lo], keys[hi], (t - times[lo]) / span);
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float lengthSq = q.LengthSquared();
        if (lengthSq < 1e-12f) return Quaternion.Identity;
        return Quaternion.Normalize(q);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int[] BuildEvaluationOrder(short[] parent)
    {
        int n = parent.Length;
        var order = new List<int>(n);
        var placed = new bool[n];

        // Repeated passes rather than recursion: a corrupt file with a parent
        // cycle would blow the stack, and this simply leaves the cycle out.
        bool progress = true;
        while (progress && order.Count < n)
        {
            progress = false;
            for (int i = 0; i < n; i++)
            {
                if (placed[i]) continue;
                short p = parent[i];
                if (p >= 0 && !placed[p]) continue;

                placed[i] = true;
                order.Add(i);
                progress = true;
            }
        }

        // Anything left is in a cycle. Append it so the arrays stay complete;
        // it will evaluate against a stale parent, which is visible and local
        // rather than a crash.
        for (int i = 0; i < n; i++)
            if (!placed[i]) order.Add(i);

        return [.. order];
    }

    /// <summary>
    /// AnimationData.dbc names, for log lines and the HUD. Mirrors
    /// animation-names.js and SkinnedGlbWriter.AnimationName; only the IDs this
    /// client actually drives are transcribed.
    /// </summary>
    public static string AnimationName(int id) => id switch
    {
        0 => "Stand",
        1 => "Death",
        4 => "Walk",
        5 => "Run",
        6 => "Dead",
        7 => "Rise",
        9 => "CombatWound",
        11 => "ShuffleLeft",
        12 => "ShuffleRight",
        13 => "WalkBackwards",
        16 => "AttackUnarmed",
        17 => "Attack1H",
        18 => "Attack2H",
        19 => "Attack2HL",
        25 => "ReadyUnarmed",
        26 => "Ready1H",
        27 => "Ready2H",
        28 => "Ready2HL",
        30 => "Dodge",
        37 => "JumpStart",
        38 => "Jump",
        39 => "JumpEnd",
        40 => "Fall",
        41 => "SwimIdle",
        42 => "Swim",
        92 => "RunRight",
        93 => "RunLeft",
        135 => "Fly",
        193 => "Hover",
        69 => "EmoteDance",
        187 => "JumpLandRun",
        _ => $"Anim{id}",
    };
}
