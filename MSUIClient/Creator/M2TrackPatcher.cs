using System.Text;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// M2 TRACK PATCHER (SPELL_CREATOR_PLAN.md P3.1): ONE model over every animated track of a
// vanilla v256 M2 - read its keys, rewrite them whole.
//
// A vanilla track is a 28-byte block: u16 interpolation, i16 global sequence, then three
// (count, offset) arrays - per-sequence [first, last] key RANGES (inclusive), TIMESTAMPS (u32 ms on
// the model's one absolute timeline) and KEYS. Every patcher before this one edited keys IN PLACE
// (same count, same offsets); this one changes the COUNT - adds, moves and deletes keys - so it
// never writes in place: the new arrays are appended at EOF (16-aligned) and only the block's
// counts / offsets are repointed. Nothing else in the file moves (the offset-preserving surgery
// law every Creator patcher follows), so it composes with every other edit.
//
// Ranges are rebuilt from the new timestamps: for each sequence, the keys inside its
// [start, end] window. A track that had NO ranges (the range-less effect tracks that live on the
// absolute timeline) is written range-less again. A track on a global sequence keeps it.
//
// Track ids are stable strings - "e3.rate", "b2.rotation", "c0.alpha", "r1.heightAbove",
// "t0.translation", "w0" - so an edit survives a reparse and travels in the design.
// ═══════════════════════════════════════════════════════════════════════════
public enum TrackValueKind { Float, Vec3, Quat, Fixed16, U8, U16 }

public sealed record TrackInfo(string Id, string Group, string Label, int BlockOffset, TrackValueKind Kind);

/// <summary>A track's keys: absolute timestamps and one float[] per key (component count by kind;
/// fixed16 as 0..1, u8/u16 as their integer).</summary>
public sealed class TrackKeys
{
    public ushort Interpolation;
    public short GlobalSequence = -1;
    public bool HadRanges;
    public List<uint> Times = new();
    public List<float[]> Values = new();

    public TrackKeys Clone() => new()
    {
        Interpolation = Interpolation, GlobalSequence = GlobalSequence, HadRanges = HadRanges,
        Times = new List<uint>(Times), Values = Values.Select(v => (float[])v.Clone()).ToList(),
    };
}

public static class M2TrackPatcher
{
    public const int BlockSize = 28;
    private const int HdrGlobalSequences = 0x014, HdrSequences = 0x01C, HdrBones = 0x034, HdrColours = 0x054,
        HdrTransparency = 0x064, HdrUvAnimations = 0x074, HdrRibbons = 0x134, HdrParticles = 0x13C;
    private const int SequenceStride = 68, BoneStride = 108, ColourStride = 56, TransparencyStride = 28,
        UvStride = 84, RibbonStride = 0xDC, ParticleStride = 504;

    private static readonly string[] EmitterTrackNames =
        { "speed", "speedVariation", "verticalRange", "horizontalRange", "gravity", "lifespan", "rate", "areaLength", "areaWidth", "zSource" };

    public static int Components(TrackValueKind kind) => kind switch
    {
        TrackValueKind.Vec3 => 3,
        TrackValueKind.Quat => 4,
        _ => 1,
    };

    private static int ElementSize(TrackValueKind kind) => kind switch
    {
        TrackValueKind.Float => 4,
        TrackValueKind.Vec3 => 12,
        TrackValueKind.Quat => 16,              // vanilla rotation keys are four raw floats
        TrackValueKind.Fixed16 => 2,
        TrackValueKind.U16 => 2,
        _ => 1,
    };

    private static bool IsVanilla(byte[] d) => d.Length >= 0x148 && d[0] == 'M' && d[1] == 'D' && d[2] == '2' && d[3] == '0' && BitConverter.ToUInt32(d, 4) == 256;
    private static (uint Count, uint Offset) Arr(byte[] d, int at) =>
        at + 8 <= d.Length ? (BitConverter.ToUInt32(d, at), BitConverter.ToUInt32(d, at + 4)) : (0u, 0u);

    /// <summary>Every animated track in the file, grouped by owner.</summary>
    public static List<TrackInfo> Enumerate(byte[] d)
    {
        var tracks = new List<TrackInfo>();
        if (!IsVanilla(d)) return tracks;
        void Each(int header, int stride, Action<int, int> visit)
        {
            (uint n, uint ofs) = Arr(d, header);
            if (n == 0 || ofs + (long)n * stride > d.Length) return;
            for (int i = 0; i < n; i++) visit(i, (int)ofs + i * stride);
        }
        Each(HdrParticles, ParticleStride, (i, o) =>
        {
            for (int t = 0; t < EmitterTrackNames.Length; t++)
                tracks.Add(new TrackInfo($"e{i}.{EmitterTrackNames[t]}", $"e{i}", EmitterTrackNames[t], o + 0x34 + t * BlockSize, TrackValueKind.Float));
            tracks.Add(new TrackInfo($"e{i}.enabled", $"e{i}", "enabled", o + 0x1DC, TrackValueKind.U8));
        });
        Each(HdrBones, BoneStride, (i, o) =>
        {
            tracks.Add(new TrackInfo($"b{i}.translation", $"b{i}", "translation", o + 12, TrackValueKind.Vec3));
            tracks.Add(new TrackInfo($"b{i}.rotation", $"b{i}", "rotation", o + 40, TrackValueKind.Quat));
            tracks.Add(new TrackInfo($"b{i}.scale", $"b{i}", "scale", o + 68, TrackValueKind.Vec3));
        });
        Each(HdrColours, ColourStride, (i, o) =>
        {
            tracks.Add(new TrackInfo($"c{i}.colour", $"c{i}", "colour", o, TrackValueKind.Vec3));
            tracks.Add(new TrackInfo($"c{i}.alpha", $"c{i}", "alpha", o + BlockSize, TrackValueKind.Fixed16));
        });
        Each(HdrTransparency, TransparencyStride, (i, o) =>
            tracks.Add(new TrackInfo($"w{i}", $"w{i}", "weight", o, TrackValueKind.Fixed16)));
        Each(HdrUvAnimations, UvStride, (i, o) =>
        {
            tracks.Add(new TrackInfo($"t{i}.translation", $"t{i}", "uv translation", o, TrackValueKind.Vec3));
            tracks.Add(new TrackInfo($"t{i}.rotation", $"t{i}", "uv rotation", o + BlockSize, TrackValueKind.Quat));
            tracks.Add(new TrackInfo($"t{i}.scale", $"t{i}", "uv scale", o + 2 * BlockSize, TrackValueKind.Vec3));
        });
        Each(HdrRibbons, RibbonStride, (i, o) =>
        {
            tracks.Add(new TrackInfo($"r{i}.colour", $"r{i}", "colour", o + 0x24, TrackValueKind.Vec3));
            tracks.Add(new TrackInfo($"r{i}.alpha", $"r{i}", "alpha", o + 0x40, TrackValueKind.Fixed16));
            tracks.Add(new TrackInfo($"r{i}.heightAbove", $"r{i}", "height above", o + 0x5C, TrackValueKind.Float));
            tracks.Add(new TrackInfo($"r{i}.heightBelow", $"r{i}", "height below", o + 0x78, TrackValueKind.Float));
            tracks.Add(new TrackInfo($"r{i}.visibility", $"r{i}", "visibility", o + 0xC0, TrackValueKind.U8));
        });
        return tracks;
    }

    public static TrackInfo? Find(byte[] d, string id) => Enumerate(d).FirstOrDefault(t => t.Id == id);

    /// <summary>The model's sequences as absolute [start, end] windows (ms).</summary>
    public static List<(uint Start, uint End)> Sequences(byte[] d)
    {
        var list = new List<(uint, uint)>();
        (uint n, uint ofs) = Arr(d, HdrSequences);
        if (ofs + (long)n * SequenceStride > d.Length) return list;
        for (int i = 0; i < n; i++)
        {
            int o = (int)ofs + i * SequenceStride;
            list.Add((BitConverter.ToUInt32(d, o + 4), BitConverter.ToUInt32(d, o + 8)));
        }
        return list;
    }

    public static TrackKeys? Read(byte[] d, TrackInfo track)
    {
        int at = track.BlockOffset;
        if (at + BlockSize > d.Length) return null;
        var keys = new TrackKeys
        {
            Interpolation = BitConverter.ToUInt16(d, at),
            GlobalSequence = BitConverter.ToInt16(d, at + 2),
            HadRanges = BitConverter.ToUInt32(d, at + 4) > 0,
        };
        uint nTimes = BitConverter.ToUInt32(d, at + 12), ofsTimes = BitConverter.ToUInt32(d, at + 16);
        uint nKeys = BitConverter.ToUInt32(d, at + 20), ofsKeys = BitConverter.ToUInt32(d, at + 24);
        int size = ElementSize(track.Kind);
        if (nKeys > 65536 || ofsKeys + (long)nKeys * size > d.Length) return null;
        bool timed = nTimes == nKeys && ofsTimes + (long)nTimes * 4 <= d.Length;
        for (int k = 0; k < nKeys; k++)
        {
            keys.Times.Add(timed ? BitConverter.ToUInt32(d, (int)ofsTimes + k * 4) : 0u);
            keys.Values.Add(Decode(d, (int)ofsKeys + k * size, track.Kind));
        }
        return keys;
    }

    private static float[] Decode(byte[] d, int at, TrackValueKind kind) => kind switch
    {
        TrackValueKind.Float => new[] { BitConverter.ToSingle(d, at) },
        TrackValueKind.Vec3 => new[] { BitConverter.ToSingle(d, at), BitConverter.ToSingle(d, at + 4), BitConverter.ToSingle(d, at + 8) },
        TrackValueKind.Quat => new[] { BitConverter.ToSingle(d, at), BitConverter.ToSingle(d, at + 4), BitConverter.ToSingle(d, at + 8), BitConverter.ToSingle(d, at + 12) },
        TrackValueKind.Fixed16 => new[] { BitConverter.ToInt16(d, at) / 32767f },
        TrackValueKind.U16 => new[] { (float)BitConverter.ToUInt16(d, at) },
        _ => new[] { (float)d[at] },
    };

    private static void Encode(List<byte> o, float[] v, TrackValueKind kind)
    {
        switch (kind)
        {
            case TrackValueKind.Float: o.AddRange(BitConverter.GetBytes(v[0])); break;
            case TrackValueKind.Vec3: for (int i = 0; i < 3; i++) o.AddRange(BitConverter.GetBytes(v[i])); break;
            case TrackValueKind.Quat: for (int i = 0; i < 4; i++) o.AddRange(BitConverter.GetBytes(v[i])); break;
            case TrackValueKind.Fixed16: o.AddRange(BitConverter.GetBytes((short)Math.Clamp(MathF.Round(v[0] * 32767f), -32767f, 32767f))); break;
            case TrackValueKind.U16: o.AddRange(BitConverter.GetBytes((ushort)Math.Clamp(MathF.Round(v[0]), 0f, 65535f))); break;
            default: o.Add((byte)Math.Clamp(MathF.Round(v[0]), 0f, 255f)); break;
        }
    }

    /// <summary>Rewrite one track whole: keys sorted by time (a later key at the same ms replaces
    /// the earlier), arrays appended at EOF, ranges rebuilt per sequence. Returns the new file.</summary>
    public static byte[] Write(byte[] d, TrackInfo track, TrackKeys keys)
    {
        var sorted = keys.Times.Select((t, i) => (Time: t, Value: keys.Values[i]))
            .GroupBy(k => k.Time).Select(g => g.Last()).OrderBy(k => k.Time).ToList();
        var o = new List<byte>(d);
        void Align() { while (o.Count % 16 != 0) o.Add(0); }

        Align();
        int timesAt = o.Count;
        foreach (var k in sorted) o.AddRange(BitConverter.GetBytes(k.Time));
        Align();
        int valuesAt = o.Count;
        foreach (var k in sorted) Encode(o, k.Value, track.Kind);

        int rangesAt = 0, rangeCount = 0;
        if (keys.HadRanges && keys.GlobalSequence < 0)
        {
            Align();
            rangesAt = o.Count;
            foreach ((uint start, uint end) in Sequences(d))
            {
                int first = sorted.FindIndex(k => k.Time >= start && k.Time <= end);
                int last = sorted.FindLastIndex(k => k.Time >= start && k.Time <= end);
                o.AddRange(BitConverter.GetBytes((uint)Math.Max(first, 0)));
                o.AddRange(BitConverter.GetBytes((uint)Math.Max(last, 0)));
                rangeCount++;
            }
        }
        byte[] result = o.ToArray();
        int at = track.BlockOffset;
        BitConverter.GetBytes(keys.Interpolation).CopyTo(result, at);
        BitConverter.GetBytes(keys.GlobalSequence).CopyTo(result, at + 2);
        BitConverter.GetBytes((uint)rangeCount).CopyTo(result, at + 4);
        BitConverter.GetBytes((uint)(rangeCount == 0 ? 0 : rangesAt)).CopyTo(result, at + 8);
        BitConverter.GetBytes((uint)sorted.Count).CopyTo(result, at + 12);
        BitConverter.GetBytes((uint)(sorted.Count == 0 ? 0 : timesAt)).CopyTo(result, at + 16);
        BitConverter.GetBytes((uint)sorted.Count).CopyTo(result, at + 20);
        BitConverter.GetBytes((uint)(sorted.Count == 0 ? 0 : valuesAt)).CopyTo(result, at + 24);
        return result;
    }

    /// <summary>Linear sample of a track at absolute ms (stepped when interpolation is 0) - the value
    /// a new key starts from.</summary>
    public static float[] Sample(TrackKeys keys, uint ms, TrackValueKind kind)
    {
        int n = keys.Times.Count;
        if (n == 0) return new float[Components(kind)];
        if (ms <= keys.Times[0]) return (float[])keys.Values[0].Clone();
        for (int i = 1; i < n; i++)
        {
            if (ms > keys.Times[i]) continue;
            if (keys.Interpolation == 0 || keys.Times[i] == keys.Times[i - 1]) return (float[])keys.Values[i - 1].Clone();
            float t = (ms - keys.Times[i - 1]) / (float)(keys.Times[i] - keys.Times[i - 1]);
            return keys.Values[i - 1].Zip(keys.Values[i], (a, b) => a + (b - a) * t).ToArray();
        }
        return (float[])keys.Values[^1].Clone();
    }

    public static string Describe(float[] v, TrackValueKind kind) => kind switch
    {
        TrackValueKind.Vec3 => $"({v[0]:0.###}, {v[1]:0.###}, {v[2]:0.###})",
        TrackValueKind.Quat => $"({v[0]:0.##}, {v[1]:0.##}, {v[2]:0.##}, {v[3]:0.##})",
        TrackValueKind.Fixed16 => $"{v[0]:0.###}",
        TrackValueKind.Float => $"{v[0]:0.###}",
        _ => $"{v[0]:0}",
    };
}
