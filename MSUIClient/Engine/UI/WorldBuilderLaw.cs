using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure rules of the Creator Mode World Builder (shared_docs/WORLD_BUILDER.md). No GL, no
/// GameLoop — pinned by interface-wire-check <c>--world-builder-only</c>.
///
/// Coordinates: WoW WORLD (X north, Y west, Z up) on the wire. Tile file
/// <c>{map}_{col}_{row}.adt</c>; its 129×129 outer vertex grid (index = gridRow*129 + gridCol) sits
/// at worldX = (32-row)·TILE − gridRow·UNIT, worldY = (32-col)·TILE − gridCol·UNIT — the mapping
/// TerrainTile proved against server heights. MODF/MDDF placement space is (C−Y, Z, C−X).
/// </summary>
public static class WorldBuilderLaw
{
    public const float Tile = 533.33333f;
    public const float Unit = Tile / 128f;
    public const float Corner = 32f * Tile;
    public const int Side = 129;
    public const int VertexCount = Side * Side;

    public enum BrushMode { Raise, Lower, Smooth, Flatten }

    /// <summary>The preview and request use exactly the same finite deltas. Tiny dabs are not persisted.</summary>
    public static bool SavesDelta(float delta) => float.IsFinite(delta) && MathF.Abs(delta) > 0.001f;

    /// <summary>Apply or roll back one saved stroke without discarding other edits on the tile.</summary>
    public static void ApplyStroke(float[] terrain, float[] stroke, bool undo = false)
    {
        if (terrain.Length != stroke.Length) throw new ArgumentException("Stroke and terrain grids must match.");
        for (int i = 0; i < terrain.Length; i++)
            if (SavesDelta(stroke[i])) terrain[i] += undo ? -stroke[i] : stroke[i];
    }

    /// <summary>Basic import validation; the pack builder remains authoritative for polygon geometry and terrain rules.</summary>
    public static bool TryParseWorldMapOutline(string json, out JsonObject? body, out string problem)
    {
        body = null;
        problem = "";
        JsonObject? parsed;
        try { parsed = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException ex) { problem = "Invalid JSON: " + ex.Message; return false; }
        if (parsed is null) { problem = "The file must contain one world-map outline object."; return false; }
        if (parsed["map"] is not JsonValue mapValue || !mapValue.TryGetValue<int>(out int map) || map < 0)
        { problem = "Map must be a non-negative integer."; return false; }
        if (parsed["area"] is not JsonValue areaValue || !areaValue.TryGetValue<uint>(out uint area) || area < 7000)
        { problem = "Area must be a pack area ID (7000 or above)."; return false; }
        if (parsed["directory"] is not JsonValue dirValue || !dirValue.TryGetValue<string>(out string? directory) ||
            string.IsNullOrWhiteSpace(directory) || directory.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        { problem = "Directory must use letters, numbers, underscores or hyphens."; return false; }
        if (parsed["polygon"] is not JsonArray polygon || polygon.Count < 3 || polygon.Count > 4096)
        { problem = "Polygon must contain 3 to 4096 world-coordinate pairs."; return false; }
        var distinct = new HashSet<(double, double)>();
        foreach (var point in polygon)
        {
            if (point is not JsonArray { Count: 2 } pair ||
                pair[0] is not JsonValue xValue || !xValue.TryGetValue<double>(out double x) || !double.IsFinite(x) ||
                pair[1] is not JsonValue yValue || !yValue.TryGetValue<double>(out double y) || !double.IsFinite(y) ||
                Math.Abs(x) > Corner || Math.Abs(y) > Corner)
            { problem = "Every polygon point must be a finite [world X, world Y] pair inside the map."; return false; }
            distinct.Add((x, y));
        }
        if (distinct.Count < 3) { problem = "Polygon needs at least three distinct points."; return false; }
        if (parsed["terrain"] is not null and not JsonObject)
        { problem = "Terrain settings must be an object."; return false; }
        body = parsed;
        return true;
    }

    public static Vector3 WorldToPlacement(Vector3 w) => new(Corner - w.Y, w.Z, Corner - w.X);
    public static Vector3 PlacementToWorld(Vector3 p) => new(Corner - p.Z, Corner - p.X, p.Y);

    public static (int col, int row) TileOf(float worldX, float worldY) =>
        ((int)MathF.Floor(32f - worldY / Tile), (int)MathF.Floor(32f - worldX / Tile));

    public static Vector2 VertexWorld(int col, int row, int gridRow, int gridCol) =>
        new((32 - row) * Tile - gridRow * Unit, (32 - col) * Tile - gridCol * Unit);

    /// <summary>The four adjacent physical vertices, crossing ADT boundaries. Only the edge of the
    /// entire map clamps: a tile edge must not replace a real neighbour with the vertex itself.</summary>
    public static (int col, int row, int gridRow, int gridCol)[] NeighbourVertices(int col, int row, int gridRow, int gridCol)
    {
        var result = new (int col, int row, int gridRow, int gridCol)[4];
        int i = 0;
        foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            int r = Math.Clamp(row * 128 + gridRow + dr, 0, 64 * 128);
            int c = Math.Clamp(col * 128 + gridCol + dc, 0, 64 * 128);
            int tr = Math.Min(r / 128, 63), tc = Math.Min(c / 128, 63);
            result[i++] = (tc, tr, r - tr * 128, c - tc * 128);
        }
        return result;
    }

    /// <summary>Smooth falloff: 1 at the centre, 0 at the rim; <paramref name="hardness"/> 0..1
    /// keeps the inner part at full strength.</summary>
    public static float Falloff(float distance, float radius, float hardness)
    {
        if (radius <= 0f || distance >= radius) return 0f;
        float t = distance / radius;
        float inner = Math.Clamp(hardness, 0f, 0.95f);
        if (t <= inner) return 1f;
        float u = (t - inner) / (1f - inner);
        return 1f - u * u * (3f - 2f * u);
    }

    /// <summary>Every outer vertex (in every tile — border vertices appear once per tile that owns
    /// them) within <paramref name="radius"/> of the brush centre, with its falloff weight.</summary>
    public static List<(int col, int row, int gridRow, int gridCol, float weight)> BrushVertices(
        Vector2 centre, float radius, float hardness)
    {
        var hits = new List<(int, int, int, int, float)>();
        var (c0, r0) = TileOf(centre.X + radius, centre.Y + radius);
        var (c1, r1) = TileOf(centre.X - radius, centre.Y - radius);
        for (int col = Math.Max(c0, 0); col <= Math.Min(c1, 63); col++)
            for (int row = Math.Max(r0, 0); row <= Math.Min(r1, 63); row++)
            {
                float ox = (32 - row) * Tile, oy = (32 - col) * Tile;
                int gr0 = Math.Max((int)MathF.Floor((ox - (centre.X + radius)) / Unit), 0);
                int gr1 = Math.Min((int)MathF.Ceiling((ox - (centre.X - radius)) / Unit), Side - 1);
                int gc0 = Math.Max((int)MathF.Floor((oy - (centre.Y + radius)) / Unit), 0);
                int gc1 = Math.Min((int)MathF.Ceiling((oy - (centre.Y - radius)) / Unit), Side - 1);
                for (int gr = gr0; gr <= gr1; gr++)
                    for (int gc = gc0; gc <= gc1; gc++)
                    {
                        var v = new Vector2(ox - gr * Unit, oy - gc * Unit);
                        float w = Falloff(Vector2.Distance(v, centre), radius, hardness);
                        if (w > 0f) hits.Add((col, row, gr, gc, w));
                    }
            }
        return hits;
    }

    /// <summary>
    /// One brush dab's height change for one vertex. <paramref name="height"/> is its current
    /// absolute height, <paramref name="neighbourMean"/> the mean of its 4 grid neighbours,
    /// <paramref name="flattenTarget"/> the height captured when the stroke began.
    /// <paramref name="amount"/> is yards for Raise/Lower and a 0..1 blend for Smooth/Flatten
    /// (both already scaled by frame time by the caller).
    /// </summary>
    public static float Dab(BrushMode mode, float weight, float amount, float height, float neighbourMean, float flattenTarget) =>
        mode switch
        {
            BrushMode.Raise => amount * weight,
            BrushMode.Lower => -amount * weight,
            BrushMode.Smooth => (neighbourMean - height) * Math.Clamp(amount * weight, 0f, 1f),
            BrushMode.Flatten => (flattenTarget - height) * Math.Clamp(amount * weight, 0f, 1f),
            _ => 0f,
        };

    /// <summary>Inner (V8) vertex delta = mean of its four corner deltas — the same rule the web
    /// app's ADT writer applies, so the preview matches the published tile exactly.</summary>
    public static float InnerDelta(float[] outer, int innerRow, int innerCol)
    {
        int g = innerRow * Side + innerCol;
        return (outer[g] + outer[g + 1] + outer[g + Side] + outer[g + Side + 1]) * 0.25f;
    }

    /// <summary>MCNR bytes for a height gradient, in the FILE order (worldX, worldY, up) — measured
    /// on stock Northshire by the web app's WorldPackAdtTests. dRow/dCol are dh per yard along the
    /// grid axes (rows run toward −X, columns toward −Y, so n ∝ (dRow, dCol, 1)).</summary>
    public static (sbyte b0, sbyte b1, sbyte b2) EncodeNormal(float dRow, float dCol)
    {
        var n = Vector3.Normalize(new Vector3(dRow, dCol, 1f));
        static sbyte B(float v) => (sbyte)Math.Clamp(MathF.Round(v * 127f), -127, 127);
        return (B(n.X), B(n.Y), B(n.Z));
    }

    /// <summary>Placement uniqueId the web app writes into MODF/MDDF (WorldPackStore.UniqueIdBase).</summary>
    public static uint UniqueId(int placementId) => 7_000_000u + (uint)placementId;

    /// <summary>
    /// Distance in the ground plane from <paramref name="p"/> to a polyline of waypoints, and the path's height
    /// at the closest point (linear between the two waypoints' z) - the carve tool's grading rule.
    /// </summary>
    public static (float Distance, float Z) PathDistance(IReadOnlyList<Vector3> points, Vector2 p)
    {
        float best = float.MaxValue, z = points.Count > 0 ? points[0].Z : 0f;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = new Vector2(points[i].X, points[i].Y);
            var b = new Vector2(points[i + 1].X, points[i + 1].Y);
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best) { best = d; z = points[i].Z + (points[i + 1].Z - points[i].Z) * t; }
        }
        return (best, z);
    }

    /// <summary>An angle in [0, 2pi): world-DB angle columns such as creature_groups.angle are FLOAT UNSIGNED.</summary>
    public static float PositiveAngle(float radians)
    {
        float a = radians % MathF.Tau;
        if (a < 0f) a += MathF.Tau;
        return a >= MathF.Tau ? 0f : a;
    }

    /// <summary>Ghost key for renderer dynamic slots — a high tag no server GUID uses.</summary>
    public static ulong GhostKey(int placementId) => 0xB0B0_0000_0000_0000UL | (uint)placementId;
}
