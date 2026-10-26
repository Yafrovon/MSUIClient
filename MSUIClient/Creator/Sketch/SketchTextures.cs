using System.Globalization;
using System.Numerics;
using System.Text;
using SkiaSharp;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH TEXTURES — shared_docs/SPELL_SKETCH.md §4.1.
//
// One RGBA texture per piece. WHITE where the shape is; the colour comes from
// the M2 colour record, so a LOOK colour change never re-rasterizes. Only the
// ALPHA channel carries the drawing, which is exactly why the BLP must be
// UNCOMPRESSED (compression=1, alphaDepth=8): a soft edge in a DXT1 block is a
// hard edge, and every one of these shapes is soft edges.
//
// Primitives are signed-distance maths, not bitmaps: the outline is evaluated
// per pixel, so Softness is a real falloff width and any size re-renders clean.
// Strokes are the hand-drawn polyline rasterized with round caps.
//
// TEXTURE SPACE: the un-rotated quad lies in the RAW XZ plane (SketchWriter
// QuadVertices), so u runs 0..1 left to right along the quad's local +X and
// v runs 0..1 top to bottom along its local -Z — the ordinary image convention,
// with v=0 at the top of the quad. The SDFs below work in CENTRED space
// p = (u-0.5, 0.5-v), so +y is up in the picture and an Arrow points +x, which
// is forward. SketchWriter's UVs honour exactly this.
// ═══════════════════════════════════════════════════════════════════════════
public static class SketchTextures
{
    /// <summary>§9: 128 px per yard, clamped 128..512, power of two.</summary>
    public const int MinSize = 128;
    public const int MaxSize = 512;
    public const float PixelsPerYard = 128f;

    /// <summary>The outer radius every centred primitive is drawn to, as a fraction of the
    /// texture's half-extent. The rest of the image is MARGIN: a soft edge and a glow both
    /// spread outward, and a halo that runs into the texture border stops being a halo and
    /// becomes a square (which is exactly what the first version shipped — visible as a
    /// boxy glow around the star in the preview). The margin is 0.5 − R, and the lab
    /// asserts the outermost band of every shape stays dark at the worst settings.</summary>
    private const float R = 0.30f;

    /// <summary>R, for callers that need to convert between quad size and drawn size.</summary>
    public const float ShapeRadius = R;

    /// <summary>Blur radius for the glow, in texture units. Four box passes spread roughly
    /// twice this, so R + SoftMax + 2×GlowReach must stay under 0.5.</summary>
    private const float GlowReach = 0.06f;

    /// <summary>The widest a soft edge may spread beyond the outline.</summary>
    private const float SoftMax = 0.05f;

    /// <summary>How much bigger the QUAD is than the shape drawn on it.
    ///
    /// The shape occupies the middle <c>2R</c> of the image, so a quad sized to the shape
    /// would draw it at 60% of the width the owner typed. SketchWriter scales the quad by
    /// this instead, which keeps "1 × 1 yd" meaning the SHAPE is one yard and puts the
    /// transparent margin outside it where the glow can live.</summary>
    public const float QuadOversize = 0.5f / R;

    /// <summary>§9 texture size: the piece's largest side at 128 px/yd, rounded to a
    /// power of two and clamped. A square texture stretched onto a W x H quad is
    /// deliberate — a 2 x 1 crescent is a wide crescent.</summary>
    public static int SizeFor(SketchShape shape)
    {
        float yards = MathF.Max(MathF.Abs(shape.Width), MathF.Abs(shape.Height));
        int wanted = (int)MathF.Round(yards * PixelsPerYard);
        int size = MinSize;
        while (size < wanted && size < MaxSize) size <<= 1;
        return Math.Clamp(size, MinSize, MaxSize);
    }

    /// <summary>Rasterize one piece into an uncompressed BLP2. Returns null only if the
    /// BLP encoder fails, which the caller must treat as "do not use these bytes".</summary>
    public static byte[]? BuildBlp(SketchPiece piece, BlpWriterService blp)
    {
        using SKBitmap bitmap = BuildBitmap(piece);
        return blp.EncodeBitmapToBlpUncompressed(bitmap);
    }

    /// <summary>Everything that changes the IMAGE, and nothing that does not.
    ///
    /// This is the other half of "colour lives in the colour record" (§4.1): because the
    /// texture is white, a LOOK colour, an alpha, a placement, a travel distance or a fade
    /// cannot alter a pixel — only the shape and the glow weight can. Keying on exactly
    /// those means the expensive half of a recompile happens when the SHAPE changes and
    /// never while a motion dial is being dragged.</summary>
    public static string CacheKey(SketchPiece piece)
    {
        SketchShape shape = piece.Shape;
        var key = new StringBuilder(64);
        key.Append((int)shape.Kind).Append('|')
           .Append(shape.Width.ToString("R", CultureInfo.InvariantCulture)).Append('|')
           .Append(shape.Height.ToString("R", CultureInfo.InvariantCulture)).Append('|')
           .Append(shape.Thickness.ToString("R", CultureInfo.InvariantCulture)).Append('|')
           .Append(shape.Softness.ToString("R", CultureInfo.InvariantCulture)).Append('|')
           .Append(piece.Look.Glow.ToString("R", CultureInfo.InvariantCulture));
        if (shape.Kind == SketchShapeKind.Stroke)
        {
            key.Append("|s").Append(shape.StrokeWidth.ToString("R", CultureInfo.InvariantCulture))
               .Append(shape.Closed ? 'c' : 'o');
            foreach (List<Vector2> stroke in shape.Strokes)
            {
                key.Append("|#");
                foreach (Vector2 point in stroke)
                    key.Append('|').Append(point.X.ToString("R", CultureInfo.InvariantCulture))
                       .Append(',').Append(point.Y.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        return key.ToString();
    }

    /// <summary>The white-with-alpha bitmap for a piece. Public so the lab can look at it
    /// without going through the BLP encoder.</summary>
    public static SKBitmap BuildBitmap(SketchPiece piece)
    {
        SketchShape shape = piece.Shape;
        int size = SizeFor(shape);

        // A SOLID wraps its whole image round the mesh: full coverage, no margin, no baked glow
        // (its glow is a second, larger shell of the same mesh).
        if (SketchShapes.IsSolid(shape.Kind))
        {
            var solidInfo = new SKImageInfo(MinSize, MinSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var solid = new SKBitmap(solidInfo);
            var white = new SKColor[MinSize * MinSize];
            Array.Fill(white, new SKColor(255, 255, 255, 255));
            solid.Pixels = white;
            return solid;
        }

        float[] coverage = shape.Kind == SketchShapeKind.Stroke
            ? StrokeCoverage(shape, size)
            : PrimitiveCoverage(shape, size);

        // Glow: a blurred copy of the same coverage, added at the LOOK weight. One
        // separable box blur pass either side is enough — this is a halo, not an image.
        float glowWeight = Math.Clamp(piece.Look.Glow, 0f, 1f);
        if (glowWeight > 0.001f)
        {
            int radius = Math.Max(1, (int)MathF.Round(GlowReach * size));
            float[] blurred = Blur(coverage, size, radius);
            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = MathF.Min(1f, coverage[i] + blurred[i] * glowWeight);
        }

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var pixels = new SKColor[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            byte a = (byte)Math.Clamp((int)MathF.Round(coverage[i] * 255f), 0, 255);
            pixels[i] = new SKColor(255, 255, 255, a);   // white everywhere: colour is the record's job
        }
        bitmap.Pixels = pixels;
        return bitmap;
    }

    // ── Primitives (signed distance) ────────────────────────────────────────

    private static float[] PrimitiveCoverage(SketchShape shape, int size)
    {
        var coverage = new float[size * size];

        // A zero Softness still gets one pixel of falloff, or the edge aliases.
        float soft = MathF.Max(Math.Clamp(shape.Softness, 0f, 1f) * SoftMax, 1.5f / size);
        float thickness = Math.Clamp(shape.Thickness, 0.01f, 1f);

        for (int y = 0; y < size; y++)
        {
            float py = 0.5f - (y + 0.5f) / size;      // +y up
            for (int x = 0; x < size; x++)
            {
                float px = (x + 0.5f) / size - 0.5f;  // +x right
                float sd = Distance(shape.Kind, new Vector2(px, py), thickness);
                coverage[y * size + x] = SmoothCoverage(sd, soft);
            }
        }
        return coverage;
    }

    /// <summary>Signed distance to the outline: negative inside.</summary>
    private static float Distance(SketchShapeKind kind, Vector2 p, float thickness) => kind switch
    {
        SketchShapeKind.Disc => p.Length() - R,

        // §4.1: "crescent = disc minus a disc offset by Thickness". Equal radii, so the
        // crescent's fattest point is exactly the offset: thickness 0 is nothing, 1 is a
        // full disc, and everything between is a moon.
        SketchShapeKind.Crescent => Subtract(
            p.Length() - R,
            (p - new Vector2(thickness * 2f * R, 0f)).Length() - R),

        // Ring: |r - r0| < w, the band width scaled by Thickness.
        SketchShapeKind.Ring => RingDistance(p, thickness),

        SketchShapeKind.Rect => BoxDistance(p, new Vector2(R, R)),

        SketchShapeKind.Line => BoxDistance(p, new Vector2(R, Math.Clamp(thickness * 0.25f, 0.01f, R))),

        SketchShapeKind.Arrow => ArrowDistance(p, thickness),

        SketchShapeKind.Star => Star5Distance(p, R, Math.Clamp(1f - thickness, 0.2f, 0.9f)),

        _ => p.Length() - R,
    };

    private static float RingDistance(Vector2 p, float thickness)
    {
        float band = MathF.Max(thickness * R, 0.01f);
        float mid = R - band * 0.5f;
        return MathF.Abs(p.Length() - mid) - band * 0.5f;
    }

    private static float BoxDistance(Vector2 p, Vector2 half)
    {
        float dx = MathF.Abs(p.X) - half.X;
        float dy = MathF.Abs(p.Y) - half.Y;
        float outside = new Vector2(MathF.Max(dx, 0f), MathF.Max(dy, 0f)).Length();
        float inside = MathF.Min(MathF.Max(dx, dy), 0f);
        return outside + inside;
    }

    /// <summary>Shaft plus head, pointing +x. Thickness fattens both.
    ///
    /// Every dimension is clamped to stay inside R: the first version derived the head's
    /// half-height as three times the shaft's, which at Thickness 1 reached 0.66 — past the
    /// old 0.44 radius and past the image border, so the arrowhead came out sliced flat at
    /// full alpha against the edge of the texture.</summary>
    private static float ArrowDistance(Vector2 p, float thickness)
    {
        float headHalf = Math.Clamp(thickness * 0.30f, 0.06f, R * 0.80f);
        float headLength = MathF.Min(headHalf * 1.5f, R * 1.1f);
        float headStart = R - headLength;
        float shaftHalf = MathF.Min(headHalf * 0.38f, R * 0.5f);

        float shaftCentre = (headStart - R) * 0.5f;
        float shaftHalfLength = (headStart + R) * 0.5f;
        float shaft = BoxDistance(p - new Vector2(shaftCentre, 0f),
                                  new Vector2(MathF.Max(shaftHalfLength, 0.01f), shaftHalf));
        float head = TriangleDistance(p,
            new Vector2(R, 0f),
            new Vector2(headStart, headHalf),
            new Vector2(headStart, -headHalf));
        return MathF.Min(shaft, head);
    }

    /// <summary>Exact signed distance to a triangle (iq).</summary>
    private static float TriangleDistance(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        Vector2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
        Vector2 v0 = p - p0, v1 = p - p1, v2 = p - p2;

        Vector2 pq0 = v0 - e0 * Math.Clamp(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0), 0f, 1f);
        Vector2 pq1 = v1 - e1 * Math.Clamp(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1), 0f, 1f);
        Vector2 pq2 = v2 - e2 * Math.Clamp(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2), 0f, 1f);

        float s = MathF.Sign(e0.X * e2.Y - e0.Y * e2.X);
        Vector2 d0 = new(Vector2.Dot(pq0, pq0), s * (v0.X * e0.Y - v0.Y * e0.X));
        Vector2 d1 = new(Vector2.Dot(pq1, pq1), s * (v1.X * e1.Y - v1.Y * e1.X));
        Vector2 d2 = new(Vector2.Dot(pq2, pq2), s * (v2.X * e2.Y - v2.Y * e2.X));
        Vector2 d = Vector2.Min(Vector2.Min(d0, d1), d2);
        return -MathF.Sqrt(d.X) * MathF.Sign(d.Y);
    }

    /// <summary>Five-pointed star (iq's sdStar5). <paramref name="rf"/> is the inner/outer ratio.</summary>
    private static float Star5Distance(Vector2 p, float r, float rf)
    {
        var k1 = new Vector2(0.809016994375f, -0.587785252292f);
        var k2 = new Vector2(-k1.X, k1.Y);
        p.X = MathF.Abs(p.X);
        p -= 2f * MathF.Max(Vector2.Dot(k1, p), 0f) * k1;
        p -= 2f * MathF.Max(Vector2.Dot(k2, p), 0f) * k2;
        p.X = MathF.Abs(p.X);
        p.Y -= r;
        var ba = rf * new Vector2(-k1.Y, k1.X) - new Vector2(0f, 1f);
        float h = Math.Clamp(Vector2.Dot(p, ba) / Vector2.Dot(ba, ba), 0f, r);
        return (p - ba * h).Length() * MathF.Sign(p.Y * ba.X - p.X * ba.Y);
    }

    /// <summary>Boolean subtract on distance fields: A minus B.</summary>
    private static float Subtract(float a, float b) => MathF.Max(a, -b);

    /// <summary>Distance to coverage, soft over +/- <paramref name="soft"/>.</summary>
    private static float SmoothCoverage(float sd, float soft)
    {
        float t = Math.Clamp(0.5f - sd / (2f * soft), 0f, 1f);
        return t * t * (3f - 2f * t);     // smoothstep
    }

    // ── Strokes (the Draw canvas) ───────────────────────────────────────────

    /// <summary>The hand-drawn polyline with round caps and joins, softened by the same
    /// Softness dial. Skia strokes the path; the softness is a blur of the result.</summary>
    private static float[] StrokeCoverage(SketchShape shape, int size)
    {
        var coverage = new float[size * size];
        if (shape.StrokePointCount == 0) return coverage;

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            float pen = MathF.Max(shape.StrokeWidth * size / 256f, 1f);
            using var paint = new SKPaint
            {
                Style = shape.Closed ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Stroke,
                StrokeWidth = pen,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = SKColors.White,
                IsAntialias = true,
            };
            // A STROKED circle of radius w/2 centres the pen ON that radius, so it comes out a
            // ring of outer diameter 2w - a dot twice the pen the owner chose. Dots fill.
            using var dot = new SKPaint
            {
                Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true,
            };

            foreach (List<Vector2> stroke in shape.Strokes)
            {
                if (stroke.Count == 0) continue;
                // Skia will not stroke a zero-length path, and a tap should still make a mark.
                if (stroke.Count == 1)
                {
                    canvas.DrawCircle(stroke[0].X * size, stroke[0].Y * size, pen * 0.5f, dot);
                    continue;
                }
                using var path = new SKPath();
                path.MoveTo(stroke[0].X * size, stroke[0].Y * size);
                for (int i = 1; i < stroke.Count; i++)
                    path.LineTo(stroke[i].X * size, stroke[i].Y * size);
                if (shape.Closed && stroke.Count > 2) path.Close();
                canvas.DrawPath(path, paint);
            }
        }

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                coverage[y * size + x] = bitmap.GetPixel(x, y).Alpha / 255f;

        int softRadius = (int)MathF.Round(Math.Clamp(shape.Softness, 0f, 1f) * 0.06f * size);
        return softRadius > 0 ? Blur(coverage, size, softRadius) : coverage;
    }

    // ── Blur (separable box, twice: cheap and smooth enough for a halo) ─────

    private static float[] Blur(float[] source, int size, int radius)
    {
        float[] a = BoxPass(source, size, radius, horizontal: true);
        float[] b = BoxPass(a, size, radius, horizontal: false);
        float[] c = BoxPass(b, size, radius, horizontal: true);
        return BoxPass(c, size, radius, horizontal: false);
    }

    private static float[] BoxPass(float[] source, int size, int radius, bool horizontal)
    {
        var result = new float[source.Length];
        float inverse = 1f / (radius * 2 + 1);
        for (int major = 0; major < size; major++)
        {
            float running = 0f;
            for (int k = -radius; k <= radius; k++) running += Sample(source, size, major, k, horizontal);
            for (int minor = 0; minor < size; minor++)
            {
                result[Index(size, major, minor, horizontal)] = running * inverse;
                running -= Sample(source, size, major, minor - radius, horizontal);
                running += Sample(source, size, major, minor + radius + 1, horizontal);
            }
        }
        return result;
    }

    private static float Sample(float[] source, int size, int major, int minor, bool horizontal)
    {
        if (minor < 0 || minor >= size) return 0f;        // clamp-to-zero: a halo fades off the edge
        return source[Index(size, major, minor, horizontal)];
    }

    private static int Index(int size, int major, int minor, bool horizontal) =>
        horizontal ? major * size + minor : minor * size + major;
}
