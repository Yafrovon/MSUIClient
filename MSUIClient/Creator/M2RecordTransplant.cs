using System.Numerics;
using System.Text;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// CROSS-FILE RECORD TRANSPLANT (SPELL_CREATOR_PLAN.md P3.3) - copy one particle emitter or ribbon
// out of ANY donor M2 into ANY target M2 (a Blizzard spell model being tuned, not a Sketch: the
// Sketch writer has its own copy in SketchWriter.Extras.cs because it lays the file out itself).
//
// Offset-preserving surgery, like every Creator patcher: nothing already in the target moves.
//   texture   the texture table is rewritten at EOF with one more entry (the donor's image,
//             its flags); the new emitter samples it directly (particles index the TABLE).
//   ribbon    also gets a private render-flag record (the donor's material) and its own u16
//             texture / material index arrays.
//   record    every array the donor record addresses (model names, the scalar tracks, spline
//             points, the on/off gate; for a ribbon colour / alpha / heights / slot / visibility)
//             is copied to EOF and the copy's offsets repointed. Tracks are RE-TIMED: the keys in
//             the donor's first sequence, rebased onto the target's first sequence (a held key
//             when none falls inside); a track on a donor global sequence keeps its keys on a
//             NEW global sequence appended to the target's table.
//   array     the target's particle (0x13C) or ribbon (0x134) array is rewritten at EOF with the
//             new record last - its index is the old count.
// The new record rides `bone` at `rawPosition` (raw M2 frame, z up).
// ═══════════════════════════════════════════════════════════════════════════
public static class M2RecordTransplant
{
    private const int HdrGlobalSequences = 0x014, HdrSequences = 0x01C, HdrTextures = 0x05C, HdrRenderFlags = 0x084,
        HdrRibbons = 0x134, HdrParticles = 0x13C;
    private const int ParticleStride = 504, RibbonStride = 0xDC, TextureStride = 16, RenderFlagStride = 4, SequenceStride = 68, Track = 28;

    public sealed record Result(byte[] Model, int NewIndex, string TexturePath);

    private static bool IsVanilla(byte[] d) => d.Length >= 0x148 && d[0] == 'M' && d[1] == 'D' && d[2] == '2' && d[3] == '0' && BitConverter.ToUInt32(d, 4) == 256;
    private static uint U32(byte[] d, int at) => BitConverter.ToUInt32(d, at);
    private static (uint N, uint Ofs) Arr(byte[] d, int at) => at + 8 <= d.Length ? (U32(d, at), U32(d, at + 4)) : (0u, 0u);

    private sealed class Out
    {
        public readonly List<byte> Bytes;
        public Out(byte[] start) => Bytes = new List<byte>(start);
        public int Mark() { while (Bytes.Count % 16 != 0) Bytes.Add(0); return Bytes.Count; }
        public int Append(ReadOnlySpan<byte> data) { int at = Mark(); Bytes.AddRange(data.ToArray()); return at; }
        public void Patch(int at, uint value) { byte[] b = BitConverter.GetBytes(value); for (int i = 0; i < 4; i++) Bytes[at + i] = b[i]; }
    }

    public static Result? AddParticle(byte[] target, byte[] donor, int donorIndex, ushort bone, Vector3 rawPosition, out string note)
        => Add(target, donor, donorIndex, ribbon: false, bone, rawPosition, out note);

    public static Result? AddRibbon(byte[] target, byte[] donor, int donorIndex, ushort bone, Vector3 rawPosition, out string note)
        => Add(target, donor, donorIndex, ribbon: true, bone, rawPosition, out note);

    private static Result? Add(byte[] target, byte[] donor, int index, bool ribbon, ushort bone, Vector3 at, out string note)
    {
        note = "";
        if (!IsVanilla(target) || !IsVanilla(donor)) { note = "not a vanilla M2"; return null; }
        int stride = ribbon ? RibbonStride : ParticleStride;
        int header = ribbon ? HdrRibbons : HdrParticles;
        (uint dn, uint dofs) = Arr(donor, header);
        if (index < 0 || index >= dn || dofs + (long)dn * stride > donor.Length) { note = $"the donor has no {(ribbon ? "ribbon" : "emitter")} {index}"; return null; }
        int rec = (int)(dofs + (uint)index * stride);

        // The donor's texture (a ribbon names it through its first texture slot).
        int slot = ribbon
            ? (Arr(donor, rec + 0x14) is var (tn, tofs) && tn > 0 && tofs + 2 <= donor.Length ? BitConverter.ToUInt16(donor, (int)tofs) : -1)
            : BitConverter.ToUInt16(donor, rec + 22);
        if (!TryTexture(donor, slot, out string texPath, out uint texFlags)) { note = "the donor's texture is not a plain file"; return null; }

        var o = new Out(target);
        // 1. Texture table + 1.
        (uint tn0, uint tofs0) = Arr(target, HdrTextures);
        byte[] name = Encoding.ASCII.GetBytes(texPath + "\0");
        int nameAt = o.Append(name);
        int texTableAt = o.Mark();
        if (tn0 > 0) o.Bytes.AddRange(target.AsSpan((int)tofs0, (int)tn0 * TextureStride).ToArray());
        o.Bytes.AddRange(BitConverter.GetBytes(0u)); o.Bytes.AddRange(BitConverter.GetBytes(texFlags));
        o.Bytes.AddRange(BitConverter.GetBytes((uint)name.Length)); o.Bytes.AddRange(BitConverter.GetBytes((uint)nameAt));
        o.Patch(HdrTextures, tn0 + 1); o.Patch(HdrTextures + 4, (uint)texTableAt);
        int newTexture = (int)tn0;

        // 2. Ribbon material: render flags + 1 (the donor's material).
        int newMaterial = -1;
        if (ribbon)
        {
            ushort mFlags = 0x15, mBlend = 4;
            if (Arr(donor, rec + 0x1C) is var (mn, mofs) && mn > 0 && mofs + 2 <= donor.Length &&
                Arr(donor, HdrRenderFlags) is var (rn, rofs) && BitConverter.ToUInt16(donor, (int)mofs) is var m && m < rn)
            {
                mFlags = BitConverter.ToUInt16(donor, (int)(rofs + m * RenderFlagStride));
                mBlend = BitConverter.ToUInt16(donor, (int)(rofs + m * RenderFlagStride + 2));
            }
            (uint fn, uint fofs) = Arr(target, HdrRenderFlags);
            int flagsAt = o.Mark();
            if (fn > 0) o.Bytes.AddRange(target.AsSpan((int)fofs, (int)fn * RenderFlagStride).ToArray());
            o.Bytes.AddRange(BitConverter.GetBytes(mFlags)); o.Bytes.AddRange(BitConverter.GetBytes(mBlend));
            o.Patch(HdrRenderFlags, fn + 1); o.Patch(HdrRenderFlags + 4, (uint)flagsAt);
            newMaterial = (int)fn;
        }

        // 3. The record's data, re-timed.
        (uint dStart, uint dEnd) = FirstSequence(donor);
        (uint tStart, _) = FirstSequence(target);
        uint tSeqCount = Arr(target, HdrSequences).N;
        var globals = new List<uint>();
        (uint gn, uint gofs) = Arr(target, HdrGlobalSequences);
        for (int g = 0; g < gn; g++) globals.Add(U32(target, (int)gofs + g * 4));
        int firstNewGlobal = globals.Count;
        var globalMap = new Dictionary<short, short>();
        byte[] record = donor.AsSpan(rec, stride).ToArray();

        void CopyTrack(int relative, int elementSize, Func<byte[], byte[]>? transform = null)
        {
            int at = rec + relative;
            ushort interp = BitConverter.ToUInt16(donor, at);
            short global = BitConverter.ToInt16(donor, at + 2);
            uint nTimes = U32(donor, at + 12), ofsTimes = U32(donor, at + 16), nKeys = U32(donor, at + 20), ofsKeys = U32(donor, at + 24);
            void Write(uint rangesCount, uint rangesOfs, uint n, uint timesOfs, uint keysOfs, short g)
            {
                BitConverter.GetBytes(interp).CopyTo(record, relative);
                BitConverter.GetBytes(g).CopyTo(record, relative + 2);
                BitConverter.GetBytes(rangesCount).CopyTo(record, relative + 4); BitConverter.GetBytes(rangesOfs).CopyTo(record, relative + 8);
                BitConverter.GetBytes(n).CopyTo(record, relative + 12); BitConverter.GetBytes(n == 0 ? 0 : timesOfs).CopyTo(record, relative + 16);
                BitConverter.GetBytes(n).CopyTo(record, relative + 20); BitConverter.GetBytes(n == 0 ? 0 : keysOfs).CopyTo(record, relative + 24);
            }
            if (nKeys == 0 || nKeys > 4096 || ofsKeys + (long)nKeys * elementSize > donor.Length) { Write(0, 0, 0, 0, 0, -1); return; }
            bool timed = nTimes == nKeys && ofsTimes + (long)nTimes * 4 <= donor.Length;
            var picked = new List<(uint T, int K)>();
            short newGlobal = -1;
            (uint dgn, uint dgofs) = Arr(donor, HdrGlobalSequences);
            if (timed && global >= 0 && global < dgn)
            {
                for (int k = 0; k < nKeys; k++) picked.Add((U32(donor, (int)ofsTimes + k * 4), k));
                if (!globalMap.TryGetValue(global, out newGlobal))
                {
                    newGlobal = (short)globals.Count;
                    globals.Add(Math.Max(U32(donor, (int)dgofs + global * 4), 1u));
                    globalMap[global] = newGlobal;
                }
            }
            else if (timed)
            {
                for (int k = 0; k < nKeys; k++)
                {
                    uint t = U32(donor, (int)ofsTimes + k * 4);
                    if (t >= dStart && t <= dEnd) picked.Add((t - dStart + tStart, k));
                }
                if (picked.Count == 0)
                {
                    int held = 0;
                    for (int k = 0; k < nKeys; k++) if (U32(donor, (int)ofsTimes + k * 4) <= dStart) held = k;
                    picked.Add((tStart, held));
                }
            }
            else picked.Add((tStart, 0));

            int timesAt = o.Mark();
            foreach ((uint t, _) in picked) o.Bytes.AddRange(BitConverter.GetBytes(t));
            int keysAt = o.Mark();
            foreach ((_, int k) in picked)
            {
                byte[] value = donor.AsSpan((int)ofsKeys + k * elementSize, elementSize).ToArray();
                o.Bytes.AddRange(transform?.Invoke(value) ?? value);
            }
            uint rangesCount = 0; int rangesAt = 0;
            if (newGlobal < 0 && tSeqCount > 0)
            {
                rangesAt = o.Mark();
                for (int s = 0; s < tSeqCount; s++)
                {
                    o.Bytes.AddRange(BitConverter.GetBytes(0u));
                    o.Bytes.AddRange(BitConverter.GetBytes(s == 0 ? (uint)(picked.Count - 1) : 0u));
                }
                rangesCount = tSeqCount;
            }
            Write(rangesCount, (uint)rangesAt, (uint)picked.Count, (uint)timesAt, (uint)keysAt, newGlobal);
        }
        void CopyArray(int relative, int elementSize, int? countOverride = null)
        {
            (uint n, uint ofs) = Arr(donor, rec + relative);
            int count = countOverride ?? (int)n;
            if (n == 0 || ofs == 0 || ofs + (long)count * elementSize > donor.Length)
            {
                BitConverter.GetBytes(0u).CopyTo(record, relative); BitConverter.GetBytes(0u).CopyTo(record, relative + 4);
                return;
            }
            int copyAt = o.Append(donor.AsSpan((int)ofs, count * elementSize));
            BitConverter.GetBytes(n).CopyTo(record, relative); BitConverter.GetBytes((uint)copyAt).CopyTo(record, relative + 4);
        }

        BitConverter.GetBytes(-1).CopyTo(record, 0);
        BitConverter.GetBytes(at.X).CopyTo(record, 8); BitConverter.GetBytes(at.Y).CopyTo(record, 12); BitConverter.GetBytes(at.Z).CopyTo(record, 16);
        if (ribbon)
        {
            BitConverter.GetBytes((uint)bone).CopyTo(record, 4);
            int texIdxAt = o.Append(BitConverter.GetBytes((ushort)newTexture));
            BitConverter.GetBytes(1u).CopyTo(record, 0x14); BitConverter.GetBytes((uint)texIdxAt).CopyTo(record, 0x18);
            int matIdxAt = o.Append(BitConverter.GetBytes((ushort)newMaterial));
            BitConverter.GetBytes(1u).CopyTo(record, 0x1C); BitConverter.GetBytes((uint)matIdxAt).CopyTo(record, 0x20);
            CopyTrack(0x24, 12); CopyTrack(0x40, 2); CopyTrack(0x5C, 4); CopyTrack(0x78, 4);
            CopyTrack(0xA4, 2, v => new byte[v.Length]);      // the slot track names the ribbon's own (single) texture: 0
            CopyTrack(0xC0, 1);
        }
        else
        {
            BitConverter.GetBytes(bone).CopyTo(record, 20);
            BitConverter.GetBytes((ushort)newTexture).CopyTo(record, 22);
            CopyArray(0x18, 1); CopyArray(0x20, 1);
            for (int t = 0; t < 10; t++) CopyTrack(0x34 + t * Track, 4);
            (uint sn, _) = Arr(donor, rec + 0x1D4);
            CopyArray(0x1D4, 12, sn >= 3 ? Math.Max(3 * ((int)sn / 3) + 1, (int)sn) : (int)sn);
            CopyTrack(0x1DC, 1);
        }

        // 4. Global sequences + any the donor brought.
        if (globals.Count > firstNewGlobal)
        {
            int gAt = o.Mark();
            foreach (uint duration in globals) o.Bytes.AddRange(BitConverter.GetBytes(duration));
            o.Patch(HdrGlobalSequences, (uint)globals.Count); o.Patch(HdrGlobalSequences + 4, (uint)gAt);
        }

        // 5. The record array + 1.
        (uint an, uint aofs) = Arr(target, header);
        int arrayAt = o.Mark();
        if (an > 0) o.Bytes.AddRange(target.AsSpan((int)aofs, (int)an * stride).ToArray());
        o.Bytes.AddRange(record);
        o.Patch(header, an + 1); o.Patch(header + 4, (uint)arrayAt);
        note = $"{(ribbon ? "ribbon" : "emitter")} {an} <- donor {index} ({Path.GetFileName(texPath)})";
        return new Result(o.Bytes.ToArray(), (int)an, texPath);
    }

    private static (uint Start, uint End) FirstSequence(byte[] d)
    {
        (uint n, uint ofs) = Arr(d, HdrSequences);
        return n == 0 || ofs + SequenceStride > d.Length ? (0u, uint.MaxValue) : (U32(d, (int)ofs + 4), U32(d, (int)ofs + 8));
    }

    private static bool TryTexture(byte[] d, int slot, out string path, out uint flags)
    {
        path = ""; flags = 0;
        (uint n, uint ofs) = Arr(d, HdrTextures);
        if (slot < 0 || slot >= n || ofs + (slot + 1L) * TextureStride > d.Length) return false;
        int entry = (int)(ofs + (uint)slot * TextureStride);
        if (U32(d, entry) != 0) return false;
        flags = U32(d, entry + 4);
        uint length = U32(d, entry + 8), at = U32(d, entry + 12);
        if (length <= 1 || at + length > d.Length) return false;
        path = Encoding.ASCII.GetString(d, (int)at, (int)length).TrimEnd('\0');
        return path.Length > 0;
    }
}
