using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

[Flags]
public enum HandGrip { None = 0, Right = 1, Left = 2 }

/// <summary>Only palm attachments close fingers. A shield on attachment 0 uses the wrist.</summary>
public static class HandGripLaw
{
    public static HandGrip ForAttachment(int attachmentId) => attachmentId switch
    {
        1 => HandGrip.Right,
        2 => HandGrip.Left,
        _ => HandGrip.None,
    };

    // AnimationData.dbc WeaponFlags bits the original client's per-animation sheath reconcile
    // tests on every play (client 0x5fdf80; benilla creature_anim/select.rs reconcile_sheath):
    // 0x4 stows always (casts, swim, mount, sit-chair, sleep, loot), 0x10 stows except the
    // ranged-handling plays while a ranged weapon is drawn (emotes, unarmed attacks, sit-ground,
    // kneel, bow/rifle/thrown shots). Blizzard ships "NoSheathe" emote variants for the cases
    // that keep a weapon in hand.
    public const uint WeaponStowAlways = 0x4;
    public const uint WeaponStowUnlessRanged = 0x10;
    private static readonly int[] RangedHandlingAnimations = [46, 49, 105, 106, 107, 109, 110, 111, 112];

    /// <summary>
    /// The palms that grip under the animation actually presented. MSUIClient keeps a drawn
    /// weapon mounted where the original would have stowed it, but under a stow animation the
    /// original palm is empty, so the fingers keep that animation's own pose. Closing them there
    /// makes a fist beside a weapon the animation carried off the palm (TaurenMale's spell,
    /// kneel and swim animations move its hand attachment ~0.3 away). An unknown id or a
    /// missing table keeps plain palm occupancy.
    /// </summary>
    public static HandGrip ForPresentedAnimation(HandGrip occupied, int animationId,
        IReadOnlyDictionary<int, uint>? weaponFlags, byte sheathState)
    {
        if (occupied == HandGrip.None || animationId < 0 || weaponFlags is null ||
            !weaponFlags.TryGetValue(animationId, out uint flags))
            return occupied;
        if ((flags & WeaponStowAlways) != 0) return HandGrip.None;
        if ((flags & WeaponStowUnlessRanged) != 0 &&
            !(sheathState == 2 && Array.IndexOf(RangedHandlingAnimations, animationId) >= 0))
            return HandGrip.None;
        return occupied;
    }
}

/// <summary>
/// The original model's static HandsClosed rotations, masked to finger key-bone subtrees.
/// Sample the sequence's absolute start through its inclusive key window: a 33 ms pose
/// can lie BETWEEN keys, so baking only timestamps inside that band loses its curl.
/// Translation, scale, wrists and weapon attachment transforms stay on the live animation.
/// </summary>
internal sealed class HandGripPose
{
    private readonly HandGrip[] _hands;
    private readonly Quaternion?[] _rotations;

    public HandGripPose(M2Model model)
    {
        _hands = new HandGrip[model.Bones.Count];
        _rotations = new Quaternion?[model.Bones.Count];
        int sequenceIndex = model.Sequences.FindIndex(sequence => sequence.AnimationId == 15);
        if (sequenceIndex < 0) return;

        for (int key = 8; key <= 17; key++)
        {
            int root = key < model.KeyBoneLookup.Count ? model.KeyBoneLookup[key] : -1;
            if (root < 0 || root >= model.Bones.Count)
                root = model.Bones.FindIndex(bone => bone.KeyBoneId == key);
            if (root < 0) continue;
            HandGrip hand = key <= 12 ? HandGrip.Right : HandGrip.Left;
            for (int bone = 0; bone < model.Bones.Count; bone++)
            {
                int ancestor = bone;
                for (int depth = 0; depth < model.Bones.Count && ancestor >= 0 && ancestor < model.Bones.Count; depth++)
                {
                    if (ancestor == root) { _hands[bone] |= hand; break; }
                    ancestor = model.Bones[ancestor].ParentBone;
                }
            }
        }

        uint frame = model.Sequences[sequenceIndex].StartTimestamp;
        for (int bone = 0; bone < model.Bones.Count; bone++)
            if (_hands[bone] != HandGrip.None)
                _rotations[bone] = Sample(model.Bones[bone].Rotation, sequenceIndex, frame);
    }

    public void Apply(int bone, HandGrip closedHands, ref Quaternion rotation)
    {
        if ((_hands[bone] & closedHands) != 0 && _rotations[bone] is Quaternion authored)
            rotation = authored;
    }

    private static Quaternion? Sample(M2AnimTrack<Vector4> track, int sequenceIndex, uint frame)
    {
        int count = Math.Min(track.Timestamps.Count, track.Keys.Count);
        if (track.GlobalSequence >= 0 || count == 0) return null;
        int lo = 0, hi = count - 1;
        if (sequenceIndex < track.Ranges.Count)
        {
            var range = track.Ranges[sequenceIndex];
            lo = (int)Math.Min(range.Start, (uint)(count - 1));
            hi = (int)Math.Min(range.End, (uint)(count - 1));
        }
        int first = lo;
        if (lo < hi)
            for (int key = lo; key <= hi && track.Timestamps[key] <= frame; key++) first = key;
        Quaternion a = Normalize(track.Keys[first]);
        if (lo >= hi || track.InterpolationType == 0 || first + 1 >= count) return a;
        uint ta = track.Timestamps[first], tb = track.Timestamps[first + 1];
        if (tb <= ta) return a;
        float fraction = Math.Clamp(((float)frame - ta) / ((float)tb - ta), 0f, 1f);
        return Quaternion.Normalize(Quaternion.Slerp(a, Normalize(track.Keys[first + 1]), fraction));
    }

    private static Quaternion Normalize(Vector4 value)
    {
        var q = new Quaternion(value.X, value.Y, value.Z, value.W);
        float length = q.LengthSquared();
        return float.IsFinite(length) && length > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }
}
