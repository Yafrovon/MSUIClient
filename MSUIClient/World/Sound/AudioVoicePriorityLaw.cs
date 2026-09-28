namespace MSUIClient.World.Sound;

/// <summary>
/// Who gets a voice when the mix is full (owner 2026-09-22: "in raid everything is resetting
/// the audio on cast, so it's super choppy").
///
/// The log showed the cause, not a device fault: the mixer sat at its 32-voice cap for whole
/// fights (320-450 requests per 2 s from 39 casting bots) and admitted every new cue by stopping
/// the OLDEST one-shot. Other units' loops (precast hums, missile flights, eating) could not be
/// stolen, so the handful of free slots churned: each one-shot lived ~30 ms before a newer cue
/// faded it out, the player's own casts included. Cutting a playing sound is audible; not
/// starting one nobody would pick out of the crowd is not.
///
/// So admission is ranked:
///   Bed     - music and ambience: never stolen by an effect.
///   Own     - the driven body's sounds, UI and anything without a unit owner.
///   Other   - another unit's one-shot.
///   OtherLoop - another unit's loop: the least valuable, and it never ends on its own.
/// A new cue may only replace a STRICTLY less important voice (a lower rank, or the same rank
/// at well under half its loudness); otherwise the new cue is dropped. Other units also live
/// inside their own budget and loop cap, which keeps slots free for the player, and the same
/// sound from other units is not restarted inside a short window (ten mages' identical precast
/// is one sound, not ten).
/// </summary>
public static class AudioVoicePriorityLaw
{
    public enum Rank { OtherLoop = 0, Other = 1, Own = 2, Bed = 3 }

    public const int MaximumVoices = 40;
    /// <summary>Other units' voices together; the rest is reserved for the player and UI.</summary>
    public const int OtherUnitVoices = 28;
    /// <summary>Other units' loops together (precast holds, missiles, channels).</summary>
    public const int OtherUnitLoops = 8;
    /// <summary>The same sound from other units is not restarted inside this window.</summary>
    public const long OtherUnitRepeatWindowMs = 90;
    /// <summary>At the same rank, a voice is only replaced by one at least this many times louder.</summary>
    public const float SameRankLoudnessMargin = 2f;

    public static Rank Classify(string category, ulong owner, ulong priorityOwner, bool looping)
    {
        if (category is "music" or "ambience") return Rank.Bed;
        if (owner == 0 || owner == priorityOwner || category.StartsWith("ui", StringComparison.Ordinal))
            return Rank.Own;
        return looping ? Rank.OtherLoop : Rank.Other;
    }

    public static bool IsOtherUnit(Rank rank) => rank is Rank.Other or Rank.OtherLoop;

    /// <summary>Whether a live voice may be stopped to admit the incoming one.</summary>
    public static bool MayReplace(Rank victim, float victimGain, Rank incoming, float incomingGain)
    {
        if (victim == Rank.Bed) return false;
        if (victim < incoming) return true;
        return victim == incoming && victimGain * SameRankLoudnessMargin < incomingGain;
    }

    /// <summary>The better victim of two: lower rank, then quieter, then older.</summary>
    public static bool IsBetterVictim(Rank rank, float gain, long startedAtMs,
        Rank otherRank, float otherGain, long otherStartedAtMs)
    {
        if (rank != otherRank) return rank < otherRank;
        if (MathF.Abs(gain - otherGain) > 0.001f) return gain < otherGain;
        return startedAtMs < otherStartedAtMs;
    }
}
