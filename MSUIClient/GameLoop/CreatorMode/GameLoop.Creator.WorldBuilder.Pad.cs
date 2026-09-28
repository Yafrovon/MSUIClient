using System.Buffers.Binary;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Building pad (shared_docs/WORLD_BUILDER.md §7, verifier G4): a WMO set on a slope is buried on the
// uphill side and hangs on the downhill side. "pad" levels the terrain under its footprint (+ margin)
// to the building's floor and feathers the edge back into the slope — ONE sculpt op, undoable and
// audited like any brush stroke.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>MOHD bounding box (WMO local space) of a root file, or null.</summary>
    private (Vector3 Min, Vector3 Max)? WbWmoBounds(string path)
    {
        byte[]? root = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, path);
        if (root is null) return null;
        for (int at = 0; at + 8 <= root.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(at + 4));
            if (root[at] == 'D' && root[at + 1] == 'H' && root[at + 2] == 'O' && root[at + 3] == 'M' && size >= 0x3C)
            {
                Vector3 V(int o) => new(BitConverter.ToSingle(root, at + 8 + o), BitConverter.ToSingle(root, at + 12 + o), BitConverter.ToSingle(root, at + 16 + o));
                return (V(0x24), V(0x30));
            }
            if (size < 0) break;
            at += 8 + size;
        }
        return null;
    }

    /// <summary>
    /// Level the terrain under placement <paramref name="id"/> to its floor (placement z − 0.1):
    /// full strength inside the footprint grown by <paramref name="margin"/>, smoothly back to the
    /// untouched slope over <paramref name="falloff"/> yd. Returns the vertex count, or −1 when the
    /// placement/model/terrain is unavailable (tiles must be resident).
    /// </summary>
    private int WbPadPlacement(int id, float margin, float falloff)
    {
        var p = _wbState?.Placements.FirstOrDefault(x => x.Id == id && !x.Deleted);
        if (p is null || p.Kind != "wmo" || _terrain is null) return -1;
        if (WbWmoBounds(p.ModelPath) is not { } b) return -1;
        var m = WmoRenderer.ModfTransform(WorldBuilderLaw.WorldToPlacement(new Vector3(p.PosX, p.PosY, p.PosZ)),
            new Vector3(p.RotX, p.RotY, p.RotZ));
        if (!Matrix4x4.Invert(m, out var inv)) return -1;
        float floor = p.PosZ - 0.1f;
        var centre = (b.Min + b.Max) * 0.5f;
        var half = (b.Max - b.Min) * 0.5f;
        float reach = MathF.Sqrt(half.X * half.X + half.Y * half.Y) + margin + falloff;

        _wbStroke.Clear();
        int n = 0;
        var (c0, r0) = WorldBuilderLaw.TileOf(p.PosX + reach, p.PosY + reach);
        var (c1, r1) = WorldBuilderLaw.TileOf(p.PosX - reach, p.PosY - reach);
        for (int col = Math.Min(c0, c1); col <= Math.Max(c0, c1); col++)
            for (int row = Math.Min(r0, r1); row <= Math.Max(r0, r1); row++)
                for (int gr = 0; gr <= 128; gr++)
                    for (int gc = 0; gc <= 128; gc++)
                    {
                        var v = WorldBuilderLaw.VertexWorld(col, row, gr, gc);
                        if (MathF.Abs(v.X - p.PosX) > reach || MathF.Abs(v.Y - p.PosY) > reach) continue;
                        // Distance outside the footprint rectangle, measured in the building's own axes.
                        var local = Vector3.Transform(new Vector3(v.X, v.Y, floor), inv);
                        float dx = MathF.Max(MathF.Abs(local.X - centre.X) - half.X - margin, 0f);
                        float dy = MathF.Max(MathF.Abs(local.Y - centre.Y) - half.Y - margin, 0f);
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d >= falloff) continue;
                        float t = d / falloff, w = 1f - t * t * (3f - 2f * t);   // smoothstep back to the slope
                        if (_terrain.SampleHeight(v.X, v.Y) is not { } h) continue;
                        float delta = (floor - h) * w;
                        if (MathF.Abs(delta) < 0.01f) continue;
                        if (!_wbStroke.TryGetValue((col, row), out var arr))
                            _wbStroke[(col, row)] = arr = new float[WorldBuilderLaw.VertexCount];
                        arr[gr * 129 + gc] = delta;
                        n++;
                    }
        if (n == 0) return 0;
        WbFinishStroke($"pad #{id} {Path.GetFileNameWithoutExtension(p.ModelPath)}");
        return n;
    }
}
