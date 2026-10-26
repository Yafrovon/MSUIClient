using System.Numerics;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH HIERARCHY + MOTION MODIFIERS (SPELL_CREATOR_PLAN.md P3.8).
//
// PARENT: a piece may hang off another piece. Its bone's parent is then that piece's bone, so
// everything the parent does - travel, spin, swing, grow - carries the child, the way Cleave's
// planes ride one swinging bone. Pivots stay absolute (caster frame): an M2 bone composes as
// parent · T(pivot) · anim · T(-pivot), so a child placed anywhere follows its parent from
// where it is.
//
// ORBIT and JITTER do not touch the piece's own tracks. A piece that has either gets one extra
// MODIFIER bone between it and its parent, pivoting at the piece's origin, whose tracks run on
// their OWN global sequence: they loop seamlessly forever whatever the sketch's length, which a
// missile (looping for seconds of flight) needs and a one-shot does not mind.
//
//   Orbit   translation round a circle of OrbitRadius about OrbitAxis, OrbitTurnsPerSecond,
//           16 keys a turn (linear); the piece keeps its own facing (a moon, not a spoke).
//   Jitter  seeded random translation (±JitterYards) and rotation (±JitterDegrees about all
//           three axes) at JitterRate keys a second, 32 keys a loop with the last = the first;
//           STEPPED (held, the way lightning flickers) unless JitterSmooth.
//
// Modifier bones are numbered AFTER every piece bone, so a piece's bone - which its vertices
// name in a BYTE - never moves.
// ═══════════════════════════════════════════════════════════════════════════
public static partial class SketchWriter
{
    private const int OrbitKeysPerTurn = 16;
    private const int JitterKeys = 32;
    private const ushort InterpNone = 0;

    public static bool HasModifiers(SketchPiece piece) =>
        (piece.Motion.OrbitRadius > 0.001f && MathF.Abs(piece.Motion.OrbitTurnsPerSecond) > 0.001f) ||
        ((piece.Motion.JitterYards > 0.0001f || piece.Motion.JitterDegrees > 0.01f) && piece.Motion.JitterRate > 0.01f);

    /// <summary>Each piece's parent piece index, or -1. A name that is not in the doc, the
    /// piece itself, or anything that would close a loop resolves to the root.</summary>
    public static int[] ResolveParents(SketchDoc doc)
    {
        var parents = new int[doc.Pieces.Count];
        for (int i = 0; i < parents.Length; i++)
        {
            string? name = doc.Pieces[i].Place.Parent;
            parents[i] = name is { Length: > 0 }
                ? doc.Pieces.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                : -1;
            if (parents[i] == i) parents[i] = -1;
        }
        // Break cycles: walk up from each piece; meeting itself means the link that closes it goes.
        for (int i = 0; i < parents.Length; i++)
        {
            int walker = parents[i];
            for (int steps = 0; walker >= 0 && steps <= parents.Length; steps++)
            {
                if (walker == i) { parents[i] = -1; break; }
                walker = parents[walker];
            }
        }
        return parents;
    }

    private static Vector3 OrbitNormal(SketchDirection axis) => axis switch
    {
        SketchDirection.Forward => Vector3.UnitX,
        SketchDirection.Left => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    private static TrackRef WriteModifierTranslation(Blob b, SketchPiece piece, List<uint> globalSequences)
    {
        SketchMotion m = piece.Motion;
        bool orbit = m.OrbitRadius > 0.001f && MathF.Abs(m.OrbitTurnsPerSecond) > 0.001f;
        bool jitter = m.JitterYards > 0.0001f && m.JitterRate > 0.01f;
        if (!orbit && !jitter) return TrackRef.Absent;

        // One loop of the modifier: a whole number of turns AND a whole jitter cycle would need a
        // common period; an orbit with jitter uses the orbit's turn and re-rolls the jitter keys
        // on that same clock (the noise has no period to keep).
        uint periodMs;
        int count;
        if (orbit)
        {
            periodMs = (uint)Math.Clamp(MathF.Round(1000f / MathF.Abs(m.OrbitTurnsPerSecond)), 20f, 600_000f);
            count = OrbitKeysPerTurn + 1;
        }
        else
        {
            periodMs = (uint)Math.Clamp(MathF.Round(JitterKeys * 1000f / m.JitterRate), 20f, 600_000f);
            count = JitterKeys + 1;
        }

        Vector3 n = OrbitNormal(m.OrbitAxis);
        Vector3 u = MathF.Abs(n.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        Vector3 v = Vector3.Normalize(Vector3.Cross(n, u));
        u = Vector3.Cross(v, n);
        var random = new Random(JitterSeed(piece, 1));
        var times = new uint[count];
        var keys = new Vector3[count];
        for (int k = 0; k < count; k++)
        {
            times[k] = (uint)((long)periodMs * k / (count - 1));
            Vector3 key = Vector3.Zero;
            if (orbit)
            {
                float angle = MathF.Tau * k / (count - 1) * MathF.Sign(m.OrbitTurnsPerSecond);
                key += m.OrbitRadius * (u * MathF.Cos(angle) + v * MathF.Sin(angle));
            }
            if (jitter)
                key += m.JitterYards * new Vector3(Noise(random), Noise(random), Noise(random));
            keys[k] = key;
        }
        if (jitter && !orbit) keys[^1] = keys[0];                      // the loop closes on itself
        if (orbit && jitter) keys[^1] = keys[0];
        return GlobalTrack(b, WriteVectorTrack(b, times, keys), periodMs, jitter && !m.JitterSmooth && !orbit, globalSequences);
    }

    private static TrackRef WriteModifierRotation(Blob b, SketchPiece piece, List<uint> globalSequences)
    {
        SketchMotion m = piece.Motion;
        if (m.JitterDegrees <= 0.01f || m.JitterRate <= 0.01f) return TrackRef.Absent;
        uint periodMs = (uint)Math.Clamp(MathF.Round(JitterKeys * 1000f / m.JitterRate), 20f, 600_000f);
        var random = new Random(JitterSeed(piece, 2));
        var times = new uint[JitterKeys + 1];
        var keys = new Quaternion[JitterKeys + 1];
        float limit = m.JitterDegrees * MathF.PI / 180f;
        for (int k = 0; k <= JitterKeys; k++)
        {
            times[k] = (uint)((long)periodMs * k / JitterKeys);
            keys[k] = k == JitterKeys ? keys[0] : Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
                Noise(random) * limit, Noise(random) * limit, Noise(random) * limit));
        }
        return GlobalTrack(b, WriteQuaternionTrack(b, times, keys), periodMs, !m.JitterSmooth, globalSequences);
    }

    /// <summary>Put a written track on a fresh global sequence of this period, optionally stepped.</summary>
    private static TrackRef GlobalTrack(Blob b, TrackRef track, uint periodMs, bool stepped, List<uint> globalSequences)
    {
        short global = (short)globalSequences.Count;
        globalSequences.Add(Math.Max(periodMs, 1u));
        return track with { GlobalSequence = global, Interpolation = stepped ? InterpNone : track.Interpolation };
    }

    private static float Noise(Random random) => (float)(random.NextDouble() * 2.0 - 1.0);

    /// <summary>Deterministic per piece: the same doc compiles to the same bytes (the lab's
    /// determinism law), and two pieces never flicker in lockstep.</summary>
    private static int JitterSeed(SketchPiece piece, int salt)
    {
        unchecked
        {
            int hash = (int)2166136261 ^ piece.Motion.JitterSeed;
            foreach (char c in piece.Name) hash = (hash ^ c) * 16777619;
            return hash ^ (salt * 0x5bd1e995);
        }
    }

    /// <summary>How far a modifier can carry a piece from its origin (bounds).</summary>
    private static float ModifierReach(SketchPiece piece) =>
        (HasModifiers(piece) ? MathF.Max(piece.Motion.OrbitRadius, 0f) + MathF.Max(piece.Motion.JitterYards, 0f) * 1.8f : 0f);
}
