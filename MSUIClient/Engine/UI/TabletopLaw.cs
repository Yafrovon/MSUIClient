using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The Quest Tabletop's numbers and geometry (shared_docs/QUEST_TABLETOP.md §2, §4, §8), stated once, pure.
/// Ported from the web sketch (MangosSuperUI wwwroot/js/questatlas/stage.js), converted from its scene units
/// (0.1 per yard, y up) to world yards (X north, Y west, Z up) so the table and the real world share one space.
/// </summary>
public static class TabletopLaw
{
    public const float TileYards = 533.33333f;

    // §2 visual law (web sketch values; tune by eye with the owner).
    public const float DefaultExaggeration = 2.2f;
    public const float MinExaggeration = 1f, MaxExaggeration = 5f;
    public const float SlabDepth = 45f;                 // yards of "earth" under the board's lowest point
    public const float FovDegrees = 30f;
    public const float MaxPolar = 1.38f;                // radians from straight UP (three.js phi): never below ~11° over the table
    public const float MinDistance = 40f;               // web: 4 scene units
    public const float FogNearSpans = 1.6f, FogFarSpans = 4.5f;
    public const float IntroSeconds = 3.6f, FrameSeconds = 1.3f, OverviewSeconds = 1.4f;
    public const float ClickTravelPixels = 5f;
    public const float TiltFocus = 0.52f;               // focus band, screen y from the BOTTOM (GL/three convention)
    public const float TiltBlurPixels = 2.2f;
    public const float ToneExposure = 1.05f;

    // Colours: sRGB hex, as the web sketch writes them (the renderer decodes to linear).
    public const uint Background = 0x1a140e, Table = 0x3a281a, SkirtTop = 0x6e5034, SkirtBottom = 0x24180f;
    public const uint HemiSky = 0xfff0d8, HemiGround = 0x2a2016, Sun = 0xffe0b0, Water = 0x2f6f8f, BareGround = 0x8a7a5a;
    public const float HemiIntensity = 1.15f, SunIntensity = 2.1f, WaterAlpha = 0.72f;

    /// <summary>A board: the zone rectangle in world yards. Top = max X (north), Bottom = min X, Left = max Y (west), Right = min Y.</summary>
    public readonly record struct Board(int MapId, string MapDirectory, float Top, float Bottom, float Left, float Right)
    {
        public float Width => Left - Right;             // west-east yards
        public float Depth => Top - Bottom;             // north-south yards
        public float Span => MathF.Max(Width, Depth);
        public Vector2 Centre => new((Top + Bottom) * 0.5f, (Left + Right) * 0.5f);
        public bool Contains(float x, float y) => x <= Top && x >= Bottom && y <= Left && y >= Right;

        /// <summary>The ADT tiles the board touches, (col, row) with col from Y (west → east) and row from X (north → south).</summary>
        public IEnumerable<(int Col, int Row)> Tiles()
        {
            int row0 = (int)MathF.Floor(32f - Top / TileYards), row1 = (int)MathF.Floor(32f - Bottom / TileYards);
            int col0 = (int)MathF.Floor(32f - Left / TileYards), col1 = (int)MathF.Floor(32f - Right / TileYards);
            for (int row = Math.Max(0, row0); row <= Math.Min(63, row1); row++)
            for (int col = Math.Max(0, col0); col <= Math.Min(63, col1); col++)
                yield return (col, row);
        }
    }

    /// <summary>A dungeon's board: this many yards each way around its entrance in the outside world (owner, 2026-10-21).</summary>
    public const float EntranceHalfYards = 800f;

    /// <summary>Figure scale (owner, 2026-10-21: a slider with a cap, eased by zoom): creatures are true scale while the
    /// camera is within <see cref="FigureTrueYards"/> of its target, so a guard never outgrows the tower door, and grow
    /// smoothly to the slider's value by <see cref="FigureFullYards"/>, so the board still shows who stands where.</summary>
    public const float MinFigureScale = 1f, MaxFigureScale = 4f, DefaultFigureScale = 3f;
    public const float FigureTrueYards = 150f, FigureFullYards = 800f;

    public static float FigureScale(float slider, float cameraRadius)
    {
        float t = Math.Clamp((cameraRadius - FigureTrueYards) / (FigureFullYards - FigureTrueYards), 0f, 1f);
        t = t * t * (3f - 2f * t);
        return 1f + (Math.Clamp(slider, MinFigureScale, MaxFigureScale) - 1f) * t;
    }

    /// <summary>Relief eases with zoom (owner, 2026-10-22: close-ups looked "totally bad pixelated"): exaggeration makes
    /// every slope steeper, so up close the ground is both stretched and seen edge-on, and at true heights the same view
    /// is crisp. Full slider value from <see cref="ReliefFullYards"/> out, easing to <see cref="ReliefCloseFraction"/> of
    /// the extra height by <see cref="ReliefCloseYards"/>; the board still reads as a raised miniature from mid-range.</summary>
    public const float ReliefCloseYards = 200f, ReliefFullYards = 1200f, ReliefCloseFraction = 0.2f;

    public static float ShownExaggeration(float slider, float cameraRadius, bool easeClose)
    {
        float k = Math.Clamp(slider, MinExaggeration, MaxExaggeration);
        if (!easeClose) return k;
        float t = Math.Clamp((cameraRadius - ReliefCloseYards) / (ReliefFullYards - ReliefCloseYards), 0f, 1f);
        t = t * t * (3f - 2f * t);
        return 1f + (k - 1f) * (ReliefCloseFraction + (1f - ReliefCloseFraction) * t);
    }

    /// <summary>creature_template.type for ambient wildlife (rabbits, rats, deer): the Critters layer, off by default.</summary>
    public const byte CritterType = 8;

    /// <summary>Pin glyphs keep the web sprite's screen size up close and shrink to <see cref="PinOverviewScale"/> of it
    /// as the camera pulls back to the overview (owner, 2026-10-21: "pins are large at overview").</summary>
    public const float PinCloseYards = 700f, PinOverviewScale = 0.55f;

    public static float PinScale(float cameraRadius, float span)
    {
        float far = MathF.Max(PinCloseYards + 1f, IntroEnd(span).Radius);
        float t = Math.Clamp((cameraRadius - PinCloseYards) / (far - PinCloseYards), 0f, 1f);
        t = t * t * (3f - 2f * t);
        return 1f + (PinOverviewScale - 1f) * t;
    }

    /// <summary>At most this many same-NPC loops over one pin; the rest are counted ("+N").</summary>
    public const int MaxLoopsPerPin = 3;

    /// <summary>
    /// Slab base: the board's lowest land-or-water SURFACE minus the slab depth; open sea on the board pulls it to ≤ 0
    /// first. Owner, 2026-10-14: under the lowest land or the water surface, not the sea floor (the web sketch's sea-floor
    /// base made Westfall's skirt ~1,300 yd tall at k 2.2); a sea floor below the base hides under the water and the table.
    /// </summary>
    public static float BaseZ(float minSurface, bool openSea)
        => (float.IsFinite(minSurface) ? (openSea ? MathF.Min(minSurface, 0f) : minSurface) : 0f) - SlabDepth;

    /// <summary>Exaggerated height: z' = baseZ + (z − baseZ)·k. k = 1 is the identity (normal rendering).</summary>
    public static float Exaggerate(float z, float baseZ, float k) => baseZ + (z - baseZ) * k;

    /// <summary>A model rides the exaggerated ground at true scale: z' = ground' + (z − ground).</summary>
    public static float ModelZ(float z, float ground, float baseZ, float k) => Exaggerate(ground, baseZ, k) + (z - ground);

    /// <summary>
    /// Camera position for an orbit around <paramref name="target"/>, in the web sketch's spherical terms:
    /// polar from straight up, azimuth 0 = camera due SOUTH of the target (north up the screen), growing toward east.
    /// </summary>
    public static Vector3 OrbitPosition(Vector3 target, float radius, float polar, float azimuth)
    {
        float s = MathF.Sin(polar);
        // scene x = east = −worldY, scene z = south = −worldX (stage.js), y = up.
        return target + new Vector3(-radius * s * MathF.Cos(azimuth), -radius * s * MathF.Sin(azimuth), radius * MathF.Cos(polar));
    }

    /// <summary>Inverse of <see cref="OrbitPosition"/>: (radius, polar, azimuth) of an eye around a target.</summary>
    public static (float Radius, float Polar, float Azimuth) Spherical(Vector3 eye, Vector3 target)
    {
        var d = eye - target;
        float r = d.Length();
        if (r < 1e-4f) return (0f, 0f, 0f);
        float polar = MathF.Acos(Math.Clamp(d.Z / r, -1f, 1f));
        float azimuth = MathF.Atan2(-d.Y, -d.X);
        return (r, polar, azimuth);
    }

    public static float EaseInOutCubic(float t)
        => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

    /// <summary>Shortest way round: the target azimuth moved within π of the start.</summary>
    public static float ShortestAzimuth(float from, float to)
    {
        while (to - from > MathF.PI) to -= MathF.Tau;
        while (from - to > MathF.PI) to += MathF.Tau;
        return to;
    }

    /// <summary>The opening "film pan" (§2): start orbit, end target offset and orbit, all relative to the board.</summary>
    public static (float Radius, float Polar, float Azimuth) IntroStart(float span) => (span * 2.4f, 1.42f, -1.2f);
    public static (float Radius, float Polar, float Azimuth) IntroEnd(float span) => (span * 1.15f, 0.82f, 0.12f);
    /// <summary>The overview target sits a little south of centre (web: scene z + 0.05·span), on the slab base.</summary>
    public static Vector3 OverviewTarget(Board b, float baseZ) => new(b.Centre.X - b.Span * 0.05f, b.Centre.Y, baseZ);
    public static float MaxDistance(float span) => span * 3f;
    /// <summary>Radius that frames a box of the given ground size (web Stage.frame).</summary>
    public static float FrameRadius(float sizeX, float sizeY) => MathF.Max(MathF.Max(sizeX, sizeY), 240f) * 1.45f + 300f;

    /// <summary>Sun position relative to the board centre (north-west, high), as the web fits it to the span.</summary>
    public static Vector3 SunOffset(float span) => new(span * 0.4f, span * 0.55f, span * 0.9f);
    public static float ShadowHalfExtent(float span) => span * 0.75f;

    /// <summary>three.js ACESFilmicToneMapping (exposure / 0.6, RRT+ODT fit) + sRGB encode: a linear colour as shown.</summary>
    public static Vector3 ToneMap(Vector3 linear, float exposure = ToneExposure)
    {
        var c = linear * (exposure / 0.6f);
        c = new Vector3(0.59719f * c.X + 0.35458f * c.Y + 0.04823f * c.Z,
                        0.07600f * c.X + 0.90834f * c.Y + 0.01566f * c.Z,
                        0.02840f * c.X + 0.13383f * c.Y + 0.83777f * c.Z);
        static float Fit(float v) => (v * (v + 0.0245786f) - 0.000090537f) / (v * (0.983729f * v + 0.4329510f) + 0.238081f);
        c = new Vector3(Fit(c.X), Fit(c.Y), Fit(c.Z));
        c = new Vector3(1.60475f * c.X - 0.53108f * c.Y - 0.07367f * c.Z,
                        -0.10208f * c.X + 1.10813f * c.Y - 0.00605f * c.Z,
                        -0.00327f * c.X - 0.07276f * c.Y + 1.07602f * c.Z);
        static float Srgb(float v) { v = Math.Clamp(v, 0f, 1f); return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f; }
        return new Vector3(Srgb(c.X), Srgb(c.Y), Srgb(c.Z));
    }

    /// <summary>
    /// The table's water as the far layer shows a flat surface: #2f6f8f lit by the hemisphere + sun (Lambert, three.js
    /// units), tone-mapped. The near layer's liquids take this colour so near and far water meet without a seam.
    /// </summary>
    public static Vector3 ShownWater(float sunElevationSin)
    {
        var albedo = SrgbToLinear(Rgb(Water));
        var light = SrgbToLinear(Rgb(HemiSky)) * HemiIntensity + SrgbToLinear(Rgb(Sun)) * SunIntensity * MathF.Max(sunElevationSin, 0f);
        return ToneMap(albedo * light / MathF.PI);
    }

    /// <summary>The sRGB transfer undone: an encoded colour as linear light.</summary>
    public static Vector3 SrgbToLinear(Vector3 c)
    {
        static float L(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        return new Vector3(L(c.X), L(c.Y), L(c.Z));
    }

    /// <summary>A sRGB hex colour as 0..1 floats (still sRGB-encoded).</summary>
    public static Vector3 Rgb(uint hex) => new(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);
}
