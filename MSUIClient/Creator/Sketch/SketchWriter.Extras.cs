using System.Numerics;
using System.Text;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH EXTRAS — sparks and trails on a piece, copied out of Blizzard's own models
// (SPELL_SKETCH.md §4.5 / slice S5; SPELL_CREATOR_PLAN.md P3.3 + P3.4).
//
// A particle emitter is 504 bytes and a ribbon 0xDC, and neither is flat: each carries tracks
// and arrays addressed by FILE OFFSETS. Authoring one from numbers means guessing a dozen
// interacting values the client then renders as garbage, so the Sketch never authors one - it
// COPIES a proven record out of the donor model and relocates every array the record addresses
// into the sketch file:
//
//   particle  +0x18 / +0x20 geometry / recursion model names (char)
//             ten float tracks at 0x34 + 28 t (emission speed .. z source)
//             +0x1D4 spline points (vec3), +0x1DC the enabled track (u8)
//   ribbon    +0x14 texture slots (u16), +0x1C materials (u16)
//             colour (vec3), alpha (fixed16), height above / below (f32),
//             texture slot (u16) and visibility (u8) tracks
//
// Every track is re-timed onto the sketch's ONE sequence: the keys that fall in the donor's
// first sequence, rebased to 0 (a spell model's sequence 0 is its effect clip). A track on a
// donor GLOBAL sequence keeps its keys and gets its own global sequence here with the donor's
// duration, so it keeps its own clock exactly as it did at home.
//
// What the copy re-targets: the bone (the piece's), the position (the piece's pivot, so the
// extra rides the piece wherever it travels), the texture / material (appended to this file's
// tables) and what the card overrides (rate, speed, size, colour, trail life). Everything else
// is Blizzard's.
//
// Pure: donors in through a reader, bytes out. No reader, or a donor that is not there = no
// extra (the piece still compiles) and a note says why.
// ═══════════════════════════════════════════════════════════════════════════
public static partial class SketchWriter
{
    public const int ParticleStride = 504;
    public const int RibbonStride = 0xDC;
    private const int HdrRibbons = 0x134;
    private const int HdrParticles = 0x13C;
    private const int ParticleTrackBase = 0x34;
    private const int ParticleTrackCount = 10;
    private const int ParticleSplineArray = 0x1D4;
    private const int ParticleEnabledTrack = 0x1DC;
    private const int ParticleColourKeys = 336;
    private const int ParticleScaleKeys = 348;

    /// <summary>Where a preset's record comes from - SPELL_SKETCH.md §4.5's tables, chosen from
    /// the lab's census (`dumps/spell-emitter-census.csv`). The lab compiles every one.</summary>
    public readonly record struct DonorRef(string Model, int Index);

    public static DonorRef? SparkDonor(SketchSparks sparks) => sparks.Preset switch
    {
        SketchSparkPreset.Embers => new DonorRef(@"Particles\LoginFX.m2", 0),
        SketchSparkPreset.Motes => new DonorRef(@"Spells\ArcaneExplosion_Impact_Chest.m2", 4),
        SketchSparkPreset.Sparkle => new DonorRef(@"Spells\Arcane_Missile.m2", 1),
        SketchSparkPreset.Smoke => new DonorRef(@"Spells\ArcaneExplosion_Impact_Chest.m2", 3),
        SketchSparkPreset.Flare => new DonorRef(@"Item\ObjectComponents\Ammo\ArrowFireFlight_01.m2", 1),
        SketchSparkPreset.Crackle => new DonorRef(@"Spells\LightningStreak_Missile.m2", 0),
        SketchSparkPreset.Custom when sparks.Model is { Length: > 0 } model => new DonorRef(model, Math.Max(sparks.Emitter, 0)),
        _ => null,
    };

    public static DonorRef? TrailDonor(SketchTrail trail) => trail.Preset switch
    {
        SketchTrailPreset.ThinWhite => new DonorRef(@"Spells\ArcaneShot_Missile.m2", 0),
        SketchTrailPreset.SoftWide => new DonorRef(@"Spells\BalanceOfNature_Impact_Base.m2", 0),
        SketchTrailPreset.Glow => new DonorRef(@"Spells\Cripple_Impact_Base.m2", 0),
        SketchTrailPreset.Charge => new DonorRef(@"Spells\ChargeTrail.m2", 0),
        SketchTrailPreset.Custom when trail.Model is { Length: > 0 } model => new DonorRef(model, Math.Max(trail.Ribbon, 0)),
        _ => null,
    };

    /// <summary>One donor record resolved for one piece: the bytes, where the record sits, the
    /// texture it samples and (ribbons) the material it draws with.</summary>
    private sealed record Extra(
        int Piece, bool Ribbon, byte[] Donor, int Record,
        string TexturePath, uint TextureFlags, ushort MaterialFlags, ushort Blend,
        uint SequenceStart, uint SequenceEnd);

    // Donor models are small and the Sketch recompiles on every card change (§4.7: ~5 ms), so
    // each donor is read from the archives once per session.
    private static readonly Dictionary<string, byte[]?> DonorCache = new(StringComparer.OrdinalIgnoreCase);

    private static byte[]? ReadDonor(string path, Func<string, byte[]?> archive)
    {
        lock (DonorCache)
        {
            if (DonorCache.TryGetValue(path, out byte[]? cached)) return cached;
            byte[]? bytes;
            try { bytes = archive(path); }
            catch { bytes = null; }
            if (bytes is not null && !IsVanillaM2(bytes)) bytes = null;
            return DonorCache[path] = bytes;
        }
    }

    private static bool IsVanillaM2(byte[] d) =>
        d.Length >= 0x148 && d[0] == 'M' && d[1] == 'D' && d[2] == '2' && d[3] == '0' && BitConverter.ToUInt32(d, 4) == 256;

    /// <summary>The extras a doc asks for, resolved against the archives. A donor that is not
    /// there, is not an M2, or has no such record is skipped with a note - never a failed build.</summary>
    private static List<Extra> ResolveExtras(SketchDoc doc, Func<string, byte[]?>? archive, List<string> notes)
    {
        var extras = new List<Extra>();
        for (int i = 0; i < doc.Pieces.Count; i++)
        {
            SketchPiece piece = doc.Pieces[i];
            foreach ((DonorRef? donorRef, bool ribbon) in new[] { (SparkDonor(piece.Extras.Sparks), false), (TrailDonor(piece.Extras.Trail), true) })
            {
                if (donorRef is not { } donor) continue;
                string what = $"{piece.Name} {(ribbon ? "trail" : "sparks")} ({Path.GetFileName(donor.Model)} {(ribbon ? "r" : "e")}{donor.Index})";
                if (archive is null) { notes.Add($"{what}: no archive to copy from"); continue; }
                byte[]? bytes = ReadDonor(donor.Model, archive);
                if (bytes is null) { notes.Add($"{what}: model not found"); continue; }
                Extra? extra = ribbon ? ResolveRibbon(i, bytes, donor.Index) : ResolveParticle(i, bytes, donor.Index);
                if (extra is null) { notes.Add($"{what}: the model has no such record, or it has no plain texture"); continue; }
                extras.Add(extra);
            }
        }
        return extras;
    }

    private static Extra? ResolveParticle(int piece, byte[] d, int index)
    {
        (uint n, uint ofs) = Arr(d, HdrParticles);
        if (index < 0 || index >= n || ofs + (long)n * ParticleStride > d.Length) return null;
        int record = (int)(ofs + (uint)index * ParticleStride);
        if (!TryTexture(d, BitConverter.ToUInt16(d, record + 22), out string path, out uint flags)) return null;
        (uint start, uint end) = FirstSequence(d);
        return new Extra(piece, false, d, record, path, flags, 0, 0, start, end);
    }

    private static Extra? ResolveRibbon(int piece, byte[] d, int index)
    {
        (uint n, uint ofs) = Arr(d, HdrRibbons);
        if (index < 0 || index >= n || ofs + (long)n * RibbonStride > d.Length) return null;
        int record = (int)(ofs + (uint)index * RibbonStride);
        (uint nTex, uint ofsTex) = Arr(d, record + 0x14);
        if (nTex == 0 || ofsTex + 2 > d.Length) return null;
        if (!TryTexture(d, BitConverter.ToUInt16(d, (int)ofsTex), out string path, out uint flags)) return null;
        ushort materialFlags = 0x04 | 0x01 | 0x10, blend = 4;   // two-sided, unlit, no Z-write, additive
        (uint nMat, uint ofsMat) = Arr(d, record + 0x1C);
        (uint nRf, uint ofsRf) = Arr(d, HdrRenderFlags);
        if (nMat > 0 && ofsMat + 2 <= d.Length)
        {
            ushort material = BitConverter.ToUInt16(d, (int)ofsMat);
            if (material < nRf && ofsRf + (material + 1L) * RenderFlagStride <= d.Length)
            {
                materialFlags = BitConverter.ToUInt16(d, (int)(ofsRf + material * RenderFlagStride));
                blend = BitConverter.ToUInt16(d, (int)(ofsRf + material * RenderFlagStride + 2));
            }
        }
        (uint start, uint end) = FirstSequence(d);
        return new Extra(piece, true, d, record, path, flags, materialFlags, blend, start, end);
    }

    /// <summary>A donor texture slot that names a real file (type 0). Character-skin slots
    /// (type != 0) are filled at run time by the model's owner, which a Sketch does not have.</summary>
    private static bool TryTexture(byte[] d, int slot, out string path, out uint flags)
    {
        path = ""; flags = 0;
        (uint n, uint ofs) = Arr(d, HdrTextures);
        if (slot >= n || ofs + (slot + 1L) * TextureStride > d.Length) return false;
        int entry = (int)(ofs + (uint)slot * TextureStride);
        if (BitConverter.ToUInt32(d, entry) != 0) return false;
        flags = BitConverter.ToUInt32(d, entry + 4);
        uint length = BitConverter.ToUInt32(d, entry + 8), at = BitConverter.ToUInt32(d, entry + 12);
        if (length <= 1 || at + length > d.Length) return false;
        path = Encoding.ASCII.GetString(d, (int)at, (int)length).TrimEnd('\0');
        return path.Length > 0;
    }

    private static (uint Start, uint End) FirstSequence(byte[] d)
    {
        (uint n, uint ofs) = Arr(d, HdrSequences);
        if (n == 0 || ofs + SequenceStride > d.Length) return (0, uint.MaxValue);
        return (BitConverter.ToUInt32(d, (int)ofs + 4), BitConverter.ToUInt32(d, (int)ofs + 8));
    }

    private static (uint Count, uint Offset) Arr(byte[] d, int at) =>
        at + 8 <= d.Length ? (BitConverter.ToUInt32(d, at), BitConverter.ToUInt32(d, at + 4)) : (0u, 0u);

    // ── the copy ────────────────────────────────────────────────────────────

    /// <summary>Re-time one donor track onto the sketch (see the header) and write it. The
    /// optional transform rewrites each value in place (a recolour, a rate scale).</summary>
    private static TrackRef CopyTrack(Blob b, Extra extra, int at, int elementSize,
        List<uint> globalSequences, Dictionary<(byte[], int), short> globalMap, Action<byte[], int>? transform = null)
    {
        byte[] d = extra.Donor;
        ushort interp = BitConverter.ToUInt16(d, at);
        short global = BitConverter.ToInt16(d, at + 2);
        uint nTimes = BitConverter.ToUInt32(d, at + 12), ofsTimes = BitConverter.ToUInt32(d, at + 16);
        uint nKeys = BitConverter.ToUInt32(d, at + 20), ofsKeys = BitConverter.ToUInt32(d, at + 24);
        if (nKeys == 0 || nKeys > 4096 || ofsKeys + (long)nKeys * elementSize > d.Length) return TrackRef.Absent;
        bool timed = nTimes == nKeys && ofsTimes + (long)nTimes * 4 <= d.Length;

        var picked = new List<(uint Time, int Key)>();
        short sketchGlobal = -1;
        (uint gCount, uint gOfs) = Arr(d, HdrGlobalSequences);
        if (timed && global >= 0 && global < gCount && gOfs + (global + 1L) * 4 <= d.Length)
        {
            // Its own clock at home, its own clock here.
            for (int k = 0; k < nKeys; k++) picked.Add((BitConverter.ToUInt32(d, (int)ofsTimes + k * 4), k));
            if (!globalMap.TryGetValue((d, global), out sketchGlobal))
            {
                sketchGlobal = (short)globalSequences.Count;
                globalSequences.Add(Math.Max(BitConverter.ToUInt32(d, (int)gOfs + global * 4), 1u));
                globalMap[(d, global)] = sketchGlobal;
            }
        }
        else if (timed)
        {
            for (int k = 0; k < nKeys; k++)
            {
                uint t = BitConverter.ToUInt32(d, (int)ofsTimes + k * 4);
                if (t >= extra.SequenceStart && t <= extra.SequenceEnd) picked.Add((t - extra.SequenceStart, k));
            }
            if (picked.Count == 0)
            {
                // No key inside the clip: hold the value in force when it starts.
                int held = 0;
                for (int k = 0; k < nKeys; k++)
                    if (BitConverter.ToUInt32(d, (int)ofsTimes + k * 4) <= extra.SequenceStart) held = k;
                picked.Add((0, held));
            }
        }
        else picked.Add((0, 0));

        int rangeOffset = b.Mark();
        b.U32(0); b.U32((uint)(picked.Count - 1));
        int timeOffset = b.Mark();
        foreach ((uint time, _) in picked) b.U32(time);
        int keyOffset = b.Mark();
        var value = new byte[elementSize];
        foreach ((_, int key) in picked)
        {
            Array.Copy(d, ofsKeys + key * elementSize, value, 0, elementSize);
            transform?.Invoke(value, 0);
            foreach (byte x in value) b.U8(x);
        }
        return new TrackRef(interp, rangeOffset, 1, timeOffset, picked.Count, keyOffset, picked.Count, sketchGlobal);
    }

    /// <summary>A donor char array (a model name) copied into the sketch: (count, offset) or absent.</summary>
    private static (uint Count, uint Offset) CopyBytes(Blob b, byte[] d, int arrayAt, int elementSize, int? countOverride = null)
    {
        (uint n, uint ofs) = Arr(d, arrayAt);
        if (n == 0 || ofs == 0) return (0, 0);
        int count = countOverride ?? (int)n;
        if (ofs + (long)count * elementSize > d.Length) return (0, 0);
        var bytes = new byte[count * elementSize];
        Array.Copy(d, ofs, bytes, 0, bytes.Length);
        return (n, (uint)b.Append(bytes));
    }

    private static void Put(byte[] record, int at, uint value) => BitConverter.GetBytes(value).CopyTo(record, at);
    private static void Put(byte[] record, int at, float value) => BitConverter.GetBytes(value).CopyTo(record, at);
    private static void Put(byte[] record, int at, TrackRef track)
    {
        BitConverter.GetBytes(track.Interpolation).CopyTo(record, at);
        BitConverter.GetBytes(track.GlobalSequence).CopyTo(record, at + 2);
        Put(record, at + 4, (uint)track.RangesCount); Put(record, at + 8, (uint)track.RangesOffset);
        Put(record, at + 12, (uint)track.TimesCount); Put(record, at + 16, (uint)track.TimesOffset);
        Put(record, at + 20, (uint)track.KeysCount); Put(record, at + 24, (uint)track.KeysOffset);
    }

    /// <summary>Scale every float key so the track's largest magnitude becomes |target| (its
    /// shape - a burst, a ramp - survives). A track of zeros stays zeros.</summary>
    private static Action<byte[], int>? ScaleTo(Extra extra, int at, float target)
    {
        float max = MaxAbsKey(extra.Donor, at);
        if (max < 1e-6f || !float.IsFinite(target)) return null;
        float factor = MathF.Abs(target) / max;
        return (v, o) => BitConverter.GetBytes(BitConverter.ToSingle(v, o) * factor).CopyTo(v, o);
    }

    internal static float MaxAbsKey(byte[] d, int at)
    {
        uint n = BitConverter.ToUInt32(d, at + 20), ofs = BitConverter.ToUInt32(d, at + 24);
        float max = 0f;
        if (n == 0 || n > 4096 || ofs + (long)n * 4 > d.Length) return 0f;
        for (int k = 0; k < n; k++) max = MathF.Max(max, MathF.Abs(BitConverter.ToSingle(d, (int)ofs + k * 4)));
        return max;
    }

    /// <summary>A recolour that keeps each key's brightness and alpha: the donor's birth → death
    /// ramp still reads as a ramp, in the card's colour.</summary>
    private static uint Recolour(uint argb, Vector3 tint)
    {
        float r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, bl = argb & 0xFF;
        float bright = MathF.Max(r, MathF.Max(g, bl)) / 255f;
        uint C(float c) => (uint)Math.Clamp(MathF.Round(Math.Clamp(c, 0f, 1f) * bright * 255f), 0f, 255f);
        return (argb & 0xFF000000u) | (C(tint.X) << 16) | (C(tint.Y) << 8) | C(tint.Z);
    }

    private static byte[] WriteParticleRecord(Blob b, Extra extra, SketchPiece piece, int bone, int texture,
        List<uint> globalSequences, Dictionary<(byte[], int), short> globalMap)
    {
        byte[] d = extra.Donor;
        var record = new byte[ParticleStride];
        Array.Copy(d, extra.Record, record, 0, ParticleStride);
        SketchSparks card = piece.Extras.Sparks;

        BitConverter.GetBytes(-1).CopyTo(record, 0);                       // particleId: none
        Vector3 at = piece.Place.Origin + piece.Place.Offset;              // raw frame, like the bone pivot
        Put(record, 8, at.X); Put(record, 12, at.Y); Put(record, 16, at.Z);
        BitConverter.GetBytes((ushort)bone).CopyTo(record, 20);
        BitConverter.GetBytes((ushort)texture).CopyTo(record, 22);

        foreach (int arrayAt in new[] { 0x18, 0x20 })
        {
            (uint n, uint ofs) = CopyBytes(b, d, extra.Record + arrayAt, 1);
            Put(record, arrayAt, n); Put(record, arrayAt + 4, ofs);
        }
        for (int t = 0; t < ParticleTrackCount; t++)
        {
            int trackAt = extra.Record + ParticleTrackBase + t * TrackStride;
            Action<byte[], int>? transform = t switch
            {
                0 when card.Speed is { } speed => ScaleTo(extra, trackAt, speed),
                6 when card.Rate is { } rate => ScaleTo(extra, trackAt, rate),
                _ => null,
            };
            Put(record, ParticleTrackBase + t * TrackStride, CopyTrack(b, extra, trackAt, 4, globalSequences, globalMap, transform));
        }
        {
            (uint n, _) = Arr(d, extra.Record + ParticleSplineArray);
            int points = n >= 3 ? 3 * ((int)n / 3) + 1 : (int)n;   // what M2Reader reads
            (uint count, uint ofs) = CopyBytes(b, d, extra.Record + ParticleSplineArray, 12, Math.Max(points, (int)n));
            Put(record, ParticleSplineArray, count); Put(record, ParticleSplineArray + 4, ofs);
        }
        Put(record, ParticleEnabledTrack, CopyTrack(b, extra, extra.Record + ParticleEnabledTrack, 1, globalSequences, globalMap));

        for (int k = 0; k < 3; k++)
        {
            if (card.Colour is { } tint)
                Put(record, ParticleColourKeys + k * 4, Recolour(BitConverter.ToUInt32(record, ParticleColourKeys + k * 4), tint));
            if (MathF.Abs(card.Size - 1f) > 1e-4f)
                Put(record, ParticleScaleKeys + k * 4, BitConverter.ToSingle(record, ParticleScaleKeys + k * 4) * Math.Max(card.Size, 0.01f));
        }
        return record;
    }

    private static byte[] WriteRibbonRecord(Blob b, Extra extra, SketchPiece piece, int bone, int texture, int material,
        List<uint> globalSequences, Dictionary<(byte[], int), short> globalMap)
    {
        byte[] d = extra.Donor;
        int r = extra.Record;
        var record = new byte[RibbonStride];
        Array.Copy(d, r, record, 0, RibbonStride);
        SketchTrail card = piece.Extras.Trail;

        BitConverter.GetBytes(-1).CopyTo(record, 0);
        Put(record, 4, (uint)bone);
        Vector3 at = piece.Place.Origin + piece.Place.Offset;
        Put(record, 8, at.X); Put(record, 12, at.Y); Put(record, 16, at.Z);

        // One texture and one material of this file's own; the slot track then always says 0.
        int textures = b.Mark(); b.U16((ushort)texture);
        Put(record, 0x14, 1u); Put(record, 0x18, (uint)textures);
        int materials = b.Mark(); b.U16((ushort)material);
        Put(record, 0x1C, 1u); Put(record, 0x20, (uint)materials);

        // Colour: the trail wears the piece's colour unless the card names its own (§4.5).
        Vector3 colour = card.Colour ?? piece.Look.Colour;
        Put(record, 0x24, CopyTrack(b, extra, r + 0x24, 12, globalSequences, globalMap, (v, o) =>
        {
            BitConverter.GetBytes(colour.X).CopyTo(v, o);
            BitConverter.GetBytes(colour.Y).CopyTo(v, o + 4);
            BitConverter.GetBytes(colour.Z).CopyTo(v, o + 8);
        }));
        Put(record, 0x40, CopyTrack(b, extra, r + 0x40, 2, globalSequences, globalMap));
        Put(record, 0x5C, CopyTrack(b, extra, r + 0x5C, 4, globalSequences, globalMap));
        Put(record, 0x78, CopyTrack(b, extra, r + 0x78, 4, globalSequences, globalMap));
        if (card.Length > 0.01f) Put(record, 0x98, card.Length);          // edge lifetime = the card's Length
        Put(record, 0xA4, CopyTrack(b, extra, r + 0xA4, 2, globalSequences, globalMap, (v, o) => { v[o] = 0; v[o + 1] = 0; }));
        Put(record, 0xC0, CopyTrack(b, extra, r + 0xC0, 1, globalSequences, globalMap));
        return record;
    }

    /// <summary>Write every extra's record (their data first, then the contiguous arrays) and
    /// point the header at them. Texture and material indices were assigned by the caller.</summary>
    private static void WriteExtras(Blob b, SketchDoc doc, List<Extra> extras, int firstTexture, int firstMaterial,
        List<uint> globalSequences)
    {
        var globalMap = new Dictionary<(byte[], int), short>(new DonorGlobalComparer());
        var particles = new List<byte[]>();
        var ribbons = new List<byte[]>();
        int ribbonMaterial = firstMaterial;
        for (int i = 0; i < extras.Count; i++)
        {
            Extra extra = extras[i];
            SketchPiece piece = doc.Pieces[extra.Piece];
            int bone = extra.Piece + 1;
            if (extra.Ribbon)
                ribbons.Add(WriteRibbonRecord(b, extra, piece, bone, firstTexture + i, ribbonMaterial++, globalSequences, globalMap));
            else
                particles.Add(WriteParticleRecord(b, extra, piece, bone, firstTexture + i, globalSequences, globalMap));
        }
        foreach ((List<byte[]> records, int header, int stride, string what) in new[]
                 { (particles, HdrParticles, ParticleStride, "particle emitter"), (ribbons, HdrRibbons, RibbonStride, "ribbon") })
        {
            if (records.Count == 0) { b.PatchArray(header, 0, 0); continue; }
            int start = b.Mark();
            foreach (byte[] record in records) foreach (byte x in record) b.U8(x);
            ExpectStride(b, start, records.Count, stride, what);
            b.PatchArray(header, records.Count, start);
        }
    }

    /// <summary>Global sequences are per DONOR FILE: key by the array's identity, not its contents.</summary>
    private sealed class DonorGlobalComparer : IEqualityComparer<(byte[], int)>
    {
        public bool Equals((byte[], int) a, (byte[], int) c) => ReferenceEquals(a.Item1, c.Item1) && a.Item2 == c.Item2;
        public int GetHashCode((byte[], int) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }
}
