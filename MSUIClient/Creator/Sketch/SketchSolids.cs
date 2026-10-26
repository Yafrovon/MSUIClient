using System.Numerics;
using Vertex = MSUIClient.Creator.Sketch.SketchWriter.Vertex;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH SOLIDS — real 3D meshes for the Sketch (shared_docs/SPELL_CREATOR_PLAN.md P3.5).
//
// A painted plane is how Blizzard builds a slash; a ball, a pillar, a ring of force or a
// lightning bolt is a MESH with an image wrapped round it, scrolled or flipped (P3.6). These are
// those meshes, generated in the piece's LOCAL raw frame (x forward, y left, z up - the same frame
// SketchWriter's quads use), centred on the piece's pivot (Origin + Offset), so the facing, spin,
// swing, grow and travel the bone carries apply to them unchanged.
//
//   Sphere    Width = diameter round the up axis, Height = diameter along it (an ellipsoid when
//             they differ). u wraps around, v runs top to bottom.
//   Cylinder  Width = diameter, Height = length; side + both caps.
//   Cone      Width = base diameter, Height = length, apex up; side + base cap.
//   Torus     Width = ring diameter, Thickness = tube radius as a fraction of the ring radius.
//   Beam      Width = length along FORWARD, Height = thickness: two crossed planes (XZ and XY),
//             so it reads from every side - Blizzard's own construction for bolts.
//
// A glow is the same mesh inflated by Extras.Glow.Radius: a shell, not a billboard.
// Pure: piece in, vertices and indices out.
// ═══════════════════════════════════════════════════════════════════════════
public static class SketchSolids
{
    public const int MinSegments = 6;
    public const int MaxSegments = 48;

    internal static (Vertex[] Vertices, int[] Indices) Build(SketchPiece piece, bool glow)
    {
        SketchShape shape = piece.Shape;
        float grow = glow ? MathF.Max(piece.Extras.Glow.Radius, 0f) : 0f;
        float radius = MathF.Max(MathF.Abs(shape.Width) * 0.5f, 0.01f) + grow;
        float height = MathF.Max(MathF.Abs(shape.Height), 0.01f) + grow * 2f;
        int segments = Math.Clamp(shape.Segments, MinSegments, MaxSegments);
        Vector3 pivot = piece.Place.Origin + piece.Place.Offset;

        var vertices = new List<Vertex>();
        var indices = new List<int>();
        switch (shape.Kind)
        {
            case SketchShapeKind.Sphere:
                Sphere(vertices, indices, radius, height * 0.5f, segments);
                break;
            case SketchShapeKind.Cylinder:
                Lathe(vertices, indices, radius, radius, height, segments);
                Cap(vertices, indices, radius, +height * 0.5f, segments, up: true);
                Cap(vertices, indices, radius, -height * 0.5f, segments, up: false);
                break;
            case SketchShapeKind.Cone:
                Lathe(vertices, indices, radius, 0.0005f, height, segments);
                Cap(vertices, indices, radius, -height * 0.5f, segments, up: false);
                break;
            case SketchShapeKind.Torus:
                Torus(vertices, indices, radius, radius * Math.Clamp(shape.Thickness, 0.05f, 0.9f) + grow, segments);
                break;
            case SketchShapeKind.Beam:
                Beam(vertices, indices, MathF.Abs(shape.Width) + grow * 2f, height);
                break;
            default:
                throw new ArgumentException($"{shape.Kind} is not a solid", nameof(piece));
        }

        var placed = new Vertex[vertices.Count];
        for (int i = 0; i < placed.Length; i++)
            placed[i] = vertices[i] with { Position = vertices[i].Position + pivot };
        return (placed, indices.ToArray());
    }

    // u around (θ), v top to bottom (φ). The seam column is duplicated so u runs 0..1.
    private static void Sphere(List<Vertex> v, List<int> idx, float r, float rz, int segments)
    {
        int rings = Math.Max(segments / 2, 3);
        int start = v.Count;
        for (int j = 0; j <= rings; j++)
        {
            float phi = MathF.PI * j / rings;
            for (int i = 0; i <= segments; i++)
            {
                float theta = MathF.Tau * i / segments;
                var p = new Vector3(r * MathF.Sin(phi) * MathF.Cos(theta), r * MathF.Sin(phi) * MathF.Sin(theta), rz * MathF.Cos(phi));
                Vector3 n = Vector3.Normalize(new Vector3(p.X / (r * r), p.Y / (r * r), p.Z / (rz * rz)) + new Vector3(0f, 0f, 1e-9f));
                v.Add(new Vertex(p, n, (float)i / segments, (float)j / rings));
            }
        }
        Grid(idx, start, segments, rings);
    }

    // A ring at the bottom (radius rBottom) and one at the top (rTop): cylinder or cone side.
    private static void Lathe(List<Vertex> v, List<int> idx, float rBottom, float rTop, float h, int segments)
    {
        int start = v.Count;
        float slope = (rBottom - rTop) / h;
        for (int j = 0; j <= 1; j++)
        {
            float z = j == 0 ? +h * 0.5f : -h * 0.5f;
            float r = j == 0 ? rTop : rBottom;
            for (int i = 0; i <= segments; i++)
            {
                float theta = MathF.Tau * i / segments;
                var dir = new Vector3(MathF.Cos(theta), MathF.Sin(theta), 0f);
                Vector3 n = Vector3.Normalize(dir + new Vector3(0f, 0f, slope));
                v.Add(new Vertex(dir * r + new Vector3(0f, 0f, z), n, (float)i / segments, j));
            }
        }
        Grid(idx, start, segments, 1);
    }

    private static void Cap(List<Vertex> v, List<int> idx, float r, float z, int segments, bool up)
    {
        var normal = new Vector3(0f, 0f, up ? 1f : -1f);
        int centre = v.Count;
        v.Add(new Vertex(new Vector3(0f, 0f, z), normal, 0.5f, 0.5f));
        for (int i = 0; i <= segments; i++)
        {
            float theta = MathF.Tau * i / segments;
            float c = MathF.Cos(theta), s = MathF.Sin(theta);
            v.Add(new Vertex(new Vector3(c * r, s * r, z), normal, 0.5f + 0.5f * c, 0.5f - 0.5f * s));
        }
        for (int i = 0; i < segments; i++)
        {
            idx.Add(centre); idx.Add(centre + 1 + i); idx.Add(centre + 2 + i);
        }
    }

    private static void Torus(List<Vertex> v, List<int> idx, float major, float minor, int segments)
    {
        int minorSegments = Math.Max(segments / 2, 4);
        int start = v.Count;
        for (int j = 0; j <= minorSegments; j++)
        {
            float phi = MathF.Tau * j / minorSegments;
            for (int i = 0; i <= segments; i++)
            {
                float theta = MathF.Tau * i / segments;
                var around = new Vector3(MathF.Cos(theta), MathF.Sin(theta), 0f);
                Vector3 n = around * MathF.Cos(phi) + new Vector3(0f, 0f, MathF.Sin(phi));
                v.Add(new Vertex(around * major + n * minor, n, (float)i / segments, (float)j / minorSegments));
            }
        }
        Grid(idx, start, segments, minorSegments);
    }

    private static void Beam(List<Vertex> v, List<int> idx, float length, float thickness)
    {
        float l = MathF.Max(length, 0.01f) * 0.5f, t = thickness * 0.5f;
        // Plane 1: XZ (normal +Y). Plane 2: XY (normal +Z). u along the length.
        foreach ((Vector3 across, Vector3 normal) in new[] { (Vector3.UnitZ, Vector3.UnitY), (Vector3.UnitY, Vector3.UnitZ) })
        {
            int b = v.Count;
            v.Add(new Vertex(new Vector3(-l, 0f, 0f) + across * t, normal, 0f, 0f));
            v.Add(new Vertex(new Vector3(+l, 0f, 0f) + across * t, normal, 1f, 0f));
            v.Add(new Vertex(new Vector3(-l, 0f, 0f) - across * t, normal, 0f, 1f));
            v.Add(new Vertex(new Vector3(+l, 0f, 0f) - across * t, normal, 1f, 1f));
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2); idx.Add(b + 2); idx.Add(b + 1); idx.Add(b + 3);
        }
    }

    // (columns + 1) x (rows + 1) vertices laid out row by row → two triangles per cell.
    private static void Grid(List<int> idx, int start, int columns, int rows)
    {
        int stride = columns + 1;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
            {
                int a = start + j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }
    }
}
