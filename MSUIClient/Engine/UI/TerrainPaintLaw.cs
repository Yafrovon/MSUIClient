using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure rules for painting ground textures into one ADT chunk (World Builder road surfaces; shared_docs/WORLD_BUILDER.md).
/// No GL, no GameLoop. The web app's ADT writer must apply the SAME rule so the client preview equals the build.
///
/// A vanilla chunk has up to 4 texture layers. Layer 0 is the base; layers 1-3 each carry a 64x64 alpha that blends OVER
/// everything below it. Painting works in VISIBLE-WEIGHT space instead (how much of each texture the eye sees at a
/// pixel; weights sum to 1), so the result does not depend on layer order: raise the painted texture's weight, scale the
/// others down, then convert back to per-layer alphas in the existing order. A texture the chunk lacks takes a free
/// slot, or replaces the least-visible layer (its weight goes to the strongest remaining layer at each pixel).
/// Alphas are quantized to the 4-bit precision vanilla MCAL stores, so the preview shows what the file will hold.
///
/// Alpha layout (AdtTerrainReader / TerrainTextures): [row * 64 + column]; the row runs along world X (north to south),
/// the column along world Y (west to east), matching the outer vertex grid's gridRow / gridCol.
/// </summary>
public static class TerrainPaintLaw
{
    public const int AlphaSize = 64, Pixels = AlphaSize * AlphaSize, MaxLayers = 4;
    public const float ChunkSize = WorldBuilderLaw.Tile / 16f;

    /// <summary>World position (X, Y) of an alpha pixel's centre. Chunk (ix, iy): ix runs along world Y, iy along world X.</summary>
    public static Vector2 PixelWorld(int col, int row, int chunkX, int chunkY, int pixelRow, int pixelCol) =>
        new((32 - row) * WorldBuilderLaw.Tile - chunkY * ChunkSize - (pixelRow + 0.5f) * ChunkSize / AlphaSize,
            (32 - col) * WorldBuilderLaw.Tile - chunkX * ChunkSize - (pixelCol + 0.5f) * ChunkSize / AlphaSize);

    /// <summary>A road's paint amount at a distance from its centre line: full within half the width, smoothstep to 0 across the soft edge.</summary>
    public static float RoadAmount(float distance, float width, float softEdge, float strength)
    {
        float half = width / 2f;
        if (distance <= half) return strength;
        if (distance >= half + softEdge) return 0f;
        float t = (distance - half) / MathF.Max(softEdge, 0.001f);
        return strength * (1f - t * t * (3f - 2f * t));
    }

    // ── road looks (2026-11-15) ─────────────────────────────────────────────────────────────────────────────────────
    // A stock road is not a stamped band: its edge wanders, grass and dirt show through the stones in patches, and a dirt
    // shoulder runs under it. RoadLook carries those (all zero = the plain band above, so older paths paint unchanged).
    // The wobble and the patches come from a seeded value noise in WORLD yards, so a road crossing a tile border is
    // continuous and the web build reproduces the preview exactly. Integer hashing only (unchecked), no Random.

    /// <summary>Yards per edge wobble of a ragged road, and per patch of a patchy one.</summary>
    public const float RaggedScale = 7f, PatchScale = 3.5f;

    /// <summary>How a road is laid: width/soft edge/strength as <see cref="RoadAmount"/>; <c>Ragged</c> = yards the edge
    /// wanders in and out; <c>Patchy</c> 0..1 = how much of the ground shows through in patches; <c>Seed</c> fixes the
    /// pattern (stored in the doc, so the build paints the same one).</summary>
    public readonly record struct RoadLook(float Width, float SoftEdge, float Strength, float Ragged = 0f, float Patchy = 0f, int Seed = 0)
    {
        /// <summary>Furthest from the centre line this look can paint.</summary>
        public float Reach => Width / 2f + SoftEdge + MathF.Max(0f, Ragged);
    }

    /// <summary>Paint amount of a road look at world point <paramref name="p"/>, <paramref name="distance"/> yards from
    /// its centre line.</summary>
    public static float RoadAmountAt(Vector2 p, float distance, in RoadLook look)
    {
        float d = distance;
        if (look.Ragged > 0f) d -= look.Ragged * Fbm(p.X / RaggedScale, p.Y / RaggedScale, look.Seed);
        float a = RoadAmount(d, look.Width, look.SoftEdge, look.Strength);
        if (a > 0f && look.Patchy > 0f)
        {
            float n = Fbm(p.X / PatchScale, p.Y / PatchScale, unchecked(look.Seed + 7919)) * 0.5f + 0.5f;
            a *= 1f - Math.Clamp(look.Patchy, 0f, 1f) * SmoothStep(0.45f, 0.85f, n);
        }
        return a;
    }

    /// <summary>A stable seed for a path name (FNV-1a), used when a path is first given a ragged or patchy look.</summary>
    public static int SeedFor(string name)
    {
        uint h = 2166136261;
        foreach (char ch in name) h = unchecked((h ^ ch) * 16777619);
        return unchecked((int)(h & 0x7FFFFFFF));
    }

    /// <summary>Three octaves of value noise, roughly -1..1.</summary>
    public static float Fbm(float x, float y, int seed) =>
        (Value(x, y, seed) * 0.6f + Value(x * 2.03f, y * 2.03f, unchecked(seed + 1)) * 0.3f +
         Value(x * 4.07f, y * 4.07f, unchecked(seed + 2)) * 0.1f);

    private static float Value(float x, float y, int seed)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int ix = (int)fx, iy = (int)fy;
        float tx = x - fx, ty = y - fy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), e = Hash(ix + 1, iy + 1, seed);
        return (a + (b - a) * tx) + ((c + (e - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    private static float Hash(int x, int y, int seed)
    {
        uint h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 1442695041u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777215f * 2f - 1f;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Visible weight of every layer at every pixel. <paramref name="alphas"/>[0] (the base) is ignored.</summary>
    public static float[][] Weights(IReadOnlyList<byte[]?> alphas)
    {
        int n = alphas.Count;
        var w = new float[n][];
        for (int i = 0; i < n; i++) w[i] = new float[Pixels];
        for (int p = 0; p < Pixels; p++)
        {
            float remaining = 1f;
            for (int i = n - 1; i >= 1; i--)
            {
                float a = alphas[i] is { Length: >= Pixels } map ? map[p] / 255f : 0f;
                w[i][p] = a * remaining;
                remaining *= 1f - a;
            }
            if (n > 0) w[0][p] = remaining;
        }
        return w;
    }

    /// <summary>Per-layer alphas (layer 0 null) reproducing the weights in the given order, 4-bit quantized.</summary>
    public static byte[]?[] Alphas(float[][] weights)
    {
        int n = weights.Length;
        var alphas = new byte[]?[n];
        for (int i = 1; i < n; i++) alphas[i] = new byte[Pixels];
        for (int p = 0; p < Pixels; p++)
        {
            float remaining = 1f;
            for (int i = n - 1; i >= 1; i--)
            {
                float a = remaining > 1e-6f ? Math.Clamp(weights[i][p] / remaining, 0f, 1f) : 0f;
                int q = (int)MathF.Round(a * 15f);          // 4-bit, like vanilla MCAL
                alphas[i]![p] = (byte)(q * 17);
                remaining *= 1f - q / 15f;
            }
        }
        return alphas;
    }

    /// <summary>
    /// Paint <paramref name="texture"/> into one chunk. <paramref name="textures"/> and <paramref name="alphas"/> are the
    /// chunk's layers bottom first (same length; alphas[0] unused) and are replaced in place. <paramref name="amount"/>
    /// (pixelRow, pixelCol) gives 0..1 per pixel. Returns false (lists untouched) when nothing is painted.
    /// </summary>
    public static bool PaintChunk(List<int> textures, List<byte[]?> alphas, int texture, Func<int, int, float> amount, int keep = -1)
    {
        var paint = new float[Pixels];
        bool any = false;
        for (int r = 0; r < AlphaSize; r++)
            for (int c = 0; c < AlphaSize; c++)
                if (Math.Clamp(amount(r, c), 0f, 1f) is var a && a > 0f) { paint[r * AlphaSize + c] = a; any = true; }
        if (!any) return false;

        var weights = Weights(alphas).ToList();
        var tex = textures.ToList();
        if (tex.Count == 0) { tex.Add(texture); weights.Add(Filled(1f)); }
        int slot = tex.IndexOf(texture);
        if (slot < 0)
        {
            if (tex.Count < MaxLayers) { tex.Add(texture); weights.Add(new float[Pixels]); slot = tex.Count - 1; }
            else
            {
                // Replace the least-visible layer; each pixel's share goes to the strongest remaining layer there.
                // Never the layer <paramref name="keep"/> (a road's own shoulder, painted the pass before). Visibility is
                // summed WITHOUT the last alpha row and column: readers of 4-bit alpha overwrite those with their
                // neighbours (stock files carry padding there), so counting them let the client preview and the web
                // build pick different layers on a near tie (2026-11-15 parity run, Azeroth_32_48 chunks 8,9 and 8,11).
                slot = Enumerable.Range(0, tex.Count).Where(i => tex[i] != keep).OrderBy(i => VisibleSum(weights[i])).First();
                for (int p = 0; p < Pixels; p++)
                {
                    int best = -1;
                    for (int i = 0; i < tex.Count; i++)
                        if (i != slot && (best < 0 || weights[i][p] > weights[best][p])) best = i;
                    weights[best][p] += weights[slot][p];
                    weights[slot][p] = 0f;
                }
                tex[slot] = texture;
            }
        }
        for (int p = 0; p < Pixels; p++)
        {
            float a = paint[p];
            if (a <= 0f) continue;
            float old = weights[slot][p], now = old + (1f - old) * a;
            float scale = old < 1f - 1e-6f ? (1f - now) / (1f - old) : 0f;
            for (int i = 0; i < tex.Count; i++) if (i != slot) weights[i][p] *= scale;
            weights[slot][p] = now;
        }
        var result = Alphas(weights.ToArray());
        textures.Clear(); textures.AddRange(tex);
        alphas.Clear(); alphas.AddRange(result);
        return true;
    }

    private static float[] Filled(float v) { var a = new float[Pixels]; Array.Fill(a, v); return a; }

    /// <summary>A layer's visible weight summed over alpha rows/columns 0..62 (row and column 63 are reader padding).</summary>
    private static float VisibleSum(float[] w)
    {
        float s = 0f;
        for (int r = 0; r < AlphaSize - 1; r++)
            for (int c = 0; c < AlphaSize - 1; c++) s += w[r * AlphaSize + c];
        return s;
    }
}
