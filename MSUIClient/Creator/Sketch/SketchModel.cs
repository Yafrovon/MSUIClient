using System.Numerics;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH OBJECT MODEL — shared_docs/SPELL_SKETCH.md §2 (fields) and §9 (defaults).
//
// A Sketch is what the owner draws: pieces (painted planes on sticks), how each
// one moves, and what hangs off it. It compiles to ONE vanilla M2 (v256) plus a
// BLP per piece (SketchTextures + SketchWriter), after which it is an ordinary
// phase model and every existing IDE dial applies.
//
// Every field here is a number a human can say out loud. No field is a file
// offset: the whole point of the subsystem seam is that the WINDOW edits this,
// and only SketchWriter knows what a byte is.
//
// LAW (CODE_STRUCTURE_LAW §1): this namespace never references GameLoop.
//
// Units are YARDS and SECONDS throughout, in the CASTER frame:
//   Forward = +X, Left = +Y, Up = +Z   (the raw M2 file frame; the runtime
//   reader swaps to model space itself — SPELL_SKETCH.md §4.2, and see
//   SketchWriter for the one place that conversion is allowed to matter).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Which mathematically-drawn outline a piece wears, or <see cref="Stroke"/> for a hand drawing.</summary>
public enum SketchShapeKind
{
    Crescent,
    Ring,
    Disc,
    Arrow,
    Line,
    Star,
    Rect,
    /// <summary>Free-hand: the piece's texture is rasterized from <see cref="SketchShape.Points"/>.</summary>
    Stroke,

    // ── SOLIDS (SPELL_CREATOR_PLAN.md P3.5): real 3D meshes, not painted planes. Width is the
    // diameter, Height the length along the piece's own up axis (raw +Z before the facing turns
    // it); the image wraps around them with real UVs, and the LOOK image / UV scroll / flipbook
    // are what make a sphere read as a ball of lightning instead of a plastic one.
    Sphere,
    Cylinder,
    Cone,
    /// <summary>A ring tube: Thickness is the tube radius as a fraction of the ring's.</summary>
    Torus,
    /// <summary>Two crossed planes along the piece's FORWARD axis (Width = length): a bolt, a beam,
    /// a streak - readable from any side, the way Blizzard builds lightning bolts.</summary>
    Beam,
}

public static class SketchShapes
{
    /// <summary>A solid mesh (wrapped image) rather than a painted plane (drawn mask).</summary>
    public static bool IsSolid(SketchShapeKind kind) =>
        kind is SketchShapeKind.Sphere or SketchShapeKind.Cylinder or SketchShapeKind.Cone or
            SketchShapeKind.Torus or SketchShapeKind.Beam;
}

/// <summary>Material blend, named as the emitter help in GameLoop.Creator.Spells.cs names them.</summary>
public enum SketchBlend
{
    /// <summary>M2 blend mode 4. The default: nine in ten spell effects are additive.</summary>
    Additive = 0,
    /// <summary>M2 blend mode 2.</summary>
    Alpha = 1,
    /// <summary>M2 blend mode 5.</summary>
    Mod = 2,
}

/// <summary>The bone's rest orientation. "Vertical, forward" is a sword slash standing on edge.</summary>
public enum SketchFacing
{
    VerticalForward,
    VerticalSide,
    Flat,
    /// <summary>Spherical billboard: the bone wears the billboard flag and the renderer turns it every frame.</summary>
    FaceCamera,
    Custom,
}

/// <summary>Owner, 2026-09-10: "both".</summary>
public enum SketchTravelMode
{
    /// <summary>The piece flies on its own translation keys and the effect stays on the caster.</summary>
    FixedDistance,
    /// <summary>The compiled M2 goes in the stage's MISSILE slot; the game flies it at the spell's speed.</summary>
    ToTarget,
}

public enum SketchDirection { Forward, Left, Up, Custom }

public enum SketchEase { Linear, Out }

/// <summary>Custom = any ribbon of any model (SketchTrail.Model / Ribbon).</summary>
public enum SketchTrailPreset { None, ThinWhite, SoftWide, Glow, Charge, Custom }

/// <summary>Crackle = LightningStreak's sparks; Custom = any emitter of any model
/// (SketchSparks.Model / Emitter).</summary>
public enum SketchSparkPreset { None, Embers, Motes, Sparkle, Smoke, Flare, Crackle, Custom }

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>What the piece looks like before colour: an outline drawn into its texture.</summary>
public sealed class SketchShape
{
    public SketchShapeKind Kind = SketchShapeKind.Crescent;

    /// <summary>Size in YARDS, in the piece's own plane. §9: 1 x 1 (Cleave's crescent plane is about that).</summary>
    public float Width = 1f;
    public float Height = 1f;

    /// <summary>0..1. For a crescent, how much of the disc the bite leaves; for a ring, the band width.</summary>
    public float Thickness = 0.35f;

    /// <summary>0..1 falloff width of the distance field. 0 is a hard edge.</summary>
    public float Softness = 0.3f;

    /// <summary>Stroke only: the hand drawing, one polyline per pen-down in 0..1 canvas
    /// space. A list of lists rather than a single line because lifting the pen is most of
    /// what drawing IS - you cannot draw a cross, a rune or a pair of claw marks with one
    /// unbroken stroke without a connecting line you did not want.</summary>
    public List<List<Vector2>> Strokes = new();

    /// <summary>Total points across every stroke, for the cache key and the piece list.</summary>
    public int StrokePointCount
    {
        get
        {
            int n = 0;
            foreach (List<Vector2> stroke in Strokes) n += stroke.Count;
            return n;
        }
    }

    /// <summary>Stroke only: pen width in texture pixels.</summary>
    public float StrokeWidth = 24f;

    /// <summary>Stroke only: join the last point back to the first.</summary>
    public bool Closed;

    /// <summary>Solids only: segments around (rings = half of it for a sphere). 8..48.</summary>
    public int Segments = 16;

    public SketchShape Clone() => new()
    {
        Kind = Kind,
        Width = Width,
        Height = Height,
        Thickness = Thickness,
        Softness = Softness,
        Strokes = Strokes.ConvertAll(stroke => new List<Vector2>(stroke)),
        StrokeWidth = StrokeWidth,
        Closed = Closed,
        Segments = Segments,
    };
}

/// <summary>Colour, glow and material. The texture stays white: LOOK colour rides the colour record,
/// so changing the colour never re-rasterizes (SPELL_SKETCH.md §4.1).</summary>
public sealed class SketchLook
{
    public Vector3 Colour = Vector3.One;      // white
    public float Alpha = 1f;
    public float Glow = 0.5f;                 // 0..1 weight of the blurred copy baked into alpha
    public SketchBlend Blend = SketchBlend.Additive;
    public bool Unlit = true;

    /// <summary>Any archive image (BLP path) to wear INSTEAD of the generated shape mask - a
    /// lightning sheet, an energy swirl. Null = the drawn shape. The colour record still tints it.</summary>
    public string? Image;

    /// <summary>Texture scroll in image widths / heights per second (0 = still).</summary>
    public Vector2 UvScroll;

    /// <summary>Flipbook: the image is a grid of FlipCols x FlipRows frames played at FlipFps
    /// (1 x 1 or 0 fps = no flipbook). Wins over UvScroll.</summary>
    public int FlipRows = 1;
    public int FlipCols = 1;
    public float FlipFps;

    public bool HasFlipbook => FlipRows * FlipCols > 1 && FlipFps > 0.01f;
    public bool HasUvAnimation => HasFlipbook || UvScroll.LengthSquared() > 1e-8f;

    public SketchLook Clone() => (SketchLook)MemberwiseClone();
}

/// <summary>The bone's rest pose. Dragging the world handles writes exactly this.</summary>
public sealed class SketchPlace
{
    /// <summary>Caster frame, yards: X forward, Y left, Z up.
    ///
    /// §9: chest height, where the eye expects a cast effect. Measured against the real camera
    /// and kept: the character does occlude part of a piece here, but the default camera sits
    /// BEHIND the caster, so moving a piece "forward" moves it AWAY from the viewer and the
    /// torso hides more of it, not less. Tried 0.6 forward, looked worse, reverted.</summary>
    public Vector3 Origin = new(0f, 0f, 1.2f);

    public SketchFacing Facing = SketchFacing.FaceCamera;

    /// <summary>Custom facing only, DEGREES in the RAW frame: roll about forward (X), pitch
    /// about left (Y), yaw about up (Z) — the same order M2BoneParser.EulerToQuaternion reads,
    /// so the number shown here is literally the number in the file.</summary>
    public Vector3 CustomEuler = Vector3.Zero;

    /// <summary>Quarter turns IN THE PIECE'S OWN PLANE, 0-3, applied on top of the facing.
    ///
    /// Kept separate from the facing rather than folded into a Euler triple so the two stay
    /// independently adjustable: turning a slash 90 degrees should not cost you the named
    /// facing it started from, and should not require composing rotations by hand.</summary>
    public int QuarterTurns;

    /// <summary>Mirror the drawing across the piece's own axes. This is NOT a rotation - a
    /// crescent flipped horizontally is a "(" become a ")", which no amount of turning gives
    /// you. Written as swapped UVs, so it costs nothing and cannot produce a negative scale
    /// (which flips winding and is not worth the risk in this format).</summary>
    public bool MirrorX;
    public bool MirrorY;

    /// <summary>How far the DRAWING sits from the point it turns about, in yards
    /// (forward, left, up). Zero means the piece turns in place.
    ///
    /// This is what separates a spin from a SWING, and it is how nearly every melee effect in
    /// the game is actually built. Cleave's cast is three painted planes hung about 0.85 yd out
    /// from a point on the caster's body axis at chest height; one bone yaws 215 degrees in
    /// 200 ms and carries them through the arc. The planes themselves never rotate relative to
    /// their parent - they only get bigger. Without an offset you can only spin a piece on the
    /// spot, and no amount of spinning gives you a sweep.</summary>
    public Vector3 Offset = Vector3.Zero;

    public float Scale = 1f;

    /// <summary>Another piece's NAME: this piece rides that piece's bone - its travel, spin,
    /// swing and grow carry this one (SPELL_CREATOR_PLAN.md P3.8). Null = the caster.</summary>
    public string? Parent;

    public SketchPlace Clone() => (SketchPlace)MemberwiseClone();
}

/// <summary>How the piece moves over the Sketch's single sequence.</summary>
public sealed class SketchMotion
{
    public SketchTravelMode Mode = SketchTravelMode.FixedDistance;

    /// <summary>When the piece SHOWS UP, in seconds from the start of the phase.
    ///
    /// A phase is not one instant: a slash leaves the hand, and a beat later a second slash
    /// follows it. Everything else on this card is measured FROM this moment, so a piece with
    /// Start 0.2 and Travel 0.6 is done at 0.8 s.</summary>
    public float StartAt;

    /// <summary>Fixed mode: how far, in yards. §9 default motion is NONE — a new piece sits still.</summary>
    public float Distance;

    public SketchDirection Direction = SketchDirection.Forward;

    /// <summary>Custom direction only: a vector in the caster frame (normalised at compile).</summary>
    public Vector3 CustomDirection = new(1f, 0f, 0f);

    public float Duration = 0.6f;

    public SketchEase Ease = SketchEase.Out;

    /// <summary>To-the-target mode: yd/s written to the spell's missile speed (Spell.dbc field 37).</summary>
    public float Speed = 20f;

    /// <summary>Draw path: caster-frame points. When non-empty this REPLACES Travel.</summary>
    public List<Vector3> Path = new();

    /// <summary>Degrees per second, forever, about the piece's turning axis. Use it for
    /// something that tumbles the whole time it is alive.</summary>
    public float SpinDegreesPerSecond;

    /// <summary>Turn by this many degrees ONCE, over <see cref="Duration"/>, then hold.
    ///
    /// This is the swing: a slash is not a thing that spins, it is a thing that goes round once
    /// and stops. Cleave is 215 degrees in 200 ms. Combined with an Offset it sweeps; on its own
    /// it turns the piece in place.</summary>
    public float SwingDegrees;

    public float GrowFrom = 1f;
    public float GrowTo = 1f;

    public float FadeIn;
    public float FadeOut = 0.2f;

    // ── MODIFIERS (P3.8): their own looping clock, on top of everything above ──

    /// <summary>Circle the piece's origin at this radius (yd), this many turns a second (sign =
    /// direction), in the plane across OrbitAxis (Up = round the caster, Forward = a corkscrew
    /// round the flight).</summary>
    public float OrbitRadius;
    public float OrbitTurnsPerSecond;
    public SketchDirection OrbitAxis = SketchDirection.Up;

    /// <summary>Random shake: up to JitterYards off and JitterDegrees turned, re-rolled
    /// JitterRate times a second. Held between rolls (flicker - lightning arcs) unless
    /// JitterSmooth (a wobble). JitterSeed picks another, equally random, pattern.</summary>
    public float JitterYards;
    public float JitterDegrees;
    public float JitterRate = 12f;
    public bool JitterSmooth;
    public int JitterSeed;

    /// <summary>Longest time this motion occupies, used for the Sketch's sequence length.</summary>
    public float Span
    {
        get
        {
            float span = 0f;
            if (Path.Count > 1 || Distance > 0f) span = MathF.Max(span, Duration);
            if (GrowFrom != GrowTo) span = MathF.Max(span, Duration);
            if (SwingDegrees != 0f) span = MathF.Max(span, Duration);
            span = MathF.Max(span, FadeIn + FadeOut);
            // A piece that starts late makes the whole sketch longer, or its own motion would
            // be cut off by the sequence ending underneath it.
            return MathF.Max(StartAt, 0f) + span;
        }
    }

    public SketchMotion Clone()
    {
        var copy = (SketchMotion)MemberwiseClone();
        copy.Path = new List<Vector3>(Path);
        return copy;
    }
}

/// <summary>A ribbon on the piece's bone.</summary>
public sealed class SketchTrail
{
    public SketchTrailPreset Preset = SketchTrailPreset.None;
    /// <summary>Edge lifetime in seconds: how long the streak is.</summary>
    public float Length = 0.3f;
    /// <summary>Null = follow the piece's LOOK colour.</summary>
    public Vector3? Colour;
    /// <summary>Custom: the archive model and ribbon index to copy.</summary>
    public string? Model;
    public int Ribbon;

    public SketchTrail Clone() => (SketchTrail)MemberwiseClone();
}

/// <summary>A particle emitter on the piece's bone.</summary>
public sealed class SketchSparks
{
    public SketchSparkPreset Preset = SketchSparkPreset.None;
    /// <summary>Particles per second; null = the donor's own. The donor's rate curve keeps its
    /// shape, scaled so its peak is this.</summary>
    public float? Rate;
    /// <summary>Emission speed in yd/s (sign kept from the donor); null = the donor's own.</summary>
    public float? Speed;
    /// <summary>Particle size multiplier on the donor's three size keys.</summary>
    public float Size = 1f;
    /// <summary>Null = the donor's own colours; set = recoloured, keeping its brightness ramp.</summary>
    public Vector3? Colour;
    /// <summary>Custom: the archive model and emitter index to copy.</summary>
    public string? Model;
    public int Emitter;

    public SketchSparks Clone() => (SketchSparks)MemberwiseClone();
}

/// <summary>A second, larger additive plane on the same bone. 0 = off.</summary>
public sealed class SketchGlow
{
    public float Radius;
    public Vector3? Colour;

    public SketchGlow Clone() => (SketchGlow)MemberwiseClone();
}

public sealed class SketchExtras
{
    public SketchTrail Trail = new();
    public SketchSparks Sparks = new();
    public SketchGlow Glow = new();

    public SketchExtras Clone() => new() { Trail = Trail.Clone(), Sparks = Sparks.Clone(), Glow = Glow.Clone() };
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One painted plane on its own bone: the unit of everything the owner does.</summary>
public sealed class SketchPiece
{
    public string Name = "piece";
    public SketchShape Shape = new();
    public SketchLook Look = new();
    public SketchPlace Place = new();
    public SketchMotion Motion = new();
    public SketchExtras Extras = new();

    public SketchPiece Clone() => new()
    {
        Name = Name,
        Shape = Shape.Clone(),
        Look = Look.Clone(),
        Place = Place.Clone(),
        Motion = Motion.Clone(),
        Extras = Extras.Clone(),
    };

    /// <summary>§9: a fresh piece of this kind, with the facing its shape implies.</summary>
    public static SketchPiece New(SketchShapeKind kind, string name)
    {
        var piece = new SketchPiece { Name = name };
        piece.Shape.Kind = kind;
        piece.Place.Facing = DefaultFacing(kind);
        return piece;
    }

    /// <summary>The pose a NEW piece starts in. Every one of them is visible from the camera
    /// the author is actually looking through.
    ///
    /// §9 originally said "a slash stands on edge, a ring lies on the ground, a sparkle looks
    /// at you", and that is the right ARTISTIC answer - but driving the real client showed what
    /// it costs. "Vertical, forward" puts the quad in the plane that contains the view axis
    /// when the camera is behind the caster, which is where the camera always starts. The piece
    /// compiled, loaded, built its mesh and drew - as a one-pixel line. Clicking Crescent
    /// appeared to do nothing at all, twice, to two different people.
    ///
    /// So a new piece now starts TURNED TO FACE YOU. It is still fully posable - unlike
    /// "Face camera" it keeps a real rotation, so it can be swung and spun - and standing it on
    /// edge for a sweep is one click away. Visible first, artistic second: an author can always
    /// turn a shape they can see, and can do nothing at all with one they cannot.</summary>
    public static SketchFacing DefaultFacing(SketchShapeKind kind) => kind switch
    {
        // A solid looks the same from every side; its own axis stands up.
        _ when SketchShapes.IsSolid(kind) => SketchFacing.VerticalForward,
        SketchShapeKind.Ring or SketchShapeKind.Disc => SketchFacing.Flat,
        _ => SketchFacing.VerticalSide,
    };

    /// <summary>The one-line state the piece list shows ("3 yd fwd  trail  sparks").</summary>
    public string StateLine()
    {
        var parts = new List<string> { Shape.Kind.ToString().ToLowerInvariant() };
        if (Motion.Mode == SketchTravelMode.ToTarget) parts.Add($"to target {Motion.Speed:0.#} yd/s");
        else if (Motion.Path.Count > 1) parts.Add($"path {Motion.Path.Count} pts");
        else if (Motion.Distance > 0f) parts.Add($"{Motion.Distance:0.#} yd {DirectionWord(Motion.Direction)}");
        if (Motion.SwingDegrees != 0f) parts.Add($"swing {Motion.SwingDegrees:0}deg");
        if (Motion.SpinDegreesPerSecond != 0f) parts.Add($"spin {Motion.SpinDegreesPerSecond:0} deg/s");
        if (Motion.StartAt > 0f) parts.Add($"@{Motion.StartAt:0.00}s");
        if (Extras.Trail.Preset != SketchTrailPreset.None) parts.Add("trail");
        if (Extras.Sparks.Preset != SketchSparkPreset.None) parts.Add("sparks");
        if (Extras.Glow.Radius > 0f) parts.Add("glow");
        return string.Join("  ", parts);
    }

    private static string DirectionWord(SketchDirection dir) => dir switch
    {
        SketchDirection.Forward => "fwd",
        SketchDirection.Left => "left",
        SketchDirection.Up => "up",
        _ => "custom",
    };
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One Sketch per stage of a spell; compiles to one M2. Saved with the session
/// (the `sketches` block, SPELL_SKETCH.md §5) so the spell reopens editable.</summary>
public sealed class SketchDoc
{
    /// <summary>The stage this Sketch belongs to: "cast", "impact", "missile", ...</summary>
    public string Stage = "cast";

    public List<SketchPiece> Pieces = new();

    /// <summary>Seconds. Also the effect's LIFE: SpellAttachment.SelfTerminatingSpan reads the
    /// sequence duration, so this is how long the effect lives.</summary>
    public float Length
    {
        get
        {
            float longest = 0f;
            foreach (SketchPiece piece in Pieces) longest = MathF.Max(longest, piece.Motion.Span);
            return MathF.Max(longest, MinimumLength);
        }
    }

    /// <summary>§9: minimum 0.5 s.</summary>
    public const float MinimumLength = 0.5f;

    /// <summary>§9: two copies must never sit on top of each other.</summary>
    public const float PasteOffsetLeftYards = 0.5f;

    /// <summary>Any piece asking for the missile slot puts the whole Sketch there (§4.3).</summary>
    public bool IsMissile
    {
        get
        {
            foreach (SketchPiece piece in Pieces)
                if (piece.Motion.Mode == SketchTravelMode.ToTarget) return true;
            return false;
        }
    }

    public SketchDoc Clone()
    {
        var copy = new SketchDoc { Stage = Stage };
        foreach (SketchPiece piece in Pieces) copy.Pieces.Add(piece.Clone());
        return copy;
    }

    /// <summary>Add a fresh piece of this kind with a name that is unique in the doc.</summary>
    public SketchPiece AddPiece(SketchShapeKind kind)
    {
        var piece = SketchPiece.New(kind, UniqueName(kind.ToString().ToLowerInvariant()));
        Pieces.Add(piece);
        return piece;
    }

    /// <summary>Ctrl+V: the copy lands 0.5 yd LEFT of its source (§9).</summary>
    public SketchPiece PasteCopy(SketchPiece source)
    {
        SketchPiece copy = source.Clone();
        copy.Name = UniqueName(StripTrailingNumber(source.Name));
        copy.Place.Origin.Y += PasteOffsetLeftYards;
        Pieces.Add(copy);
        return copy;
    }

    public string UniqueName(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) stem = "piece";
        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{stem} {i}";
            bool taken = false;
            foreach (SketchPiece piece in Pieces)
                if (string.Equals(piece.Name, candidate, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
            if (!taken) return candidate;
        }
        return stem;
    }

    private static string StripTrailingNumber(string name)
    {
        int cut = name.Length;
        while (cut > 0 && char.IsDigit(name[cut - 1])) cut--;
        return name[..cut].TrimEnd();
    }

    /// <summary>The custom archive path this Sketch compiles to (§4.1 path law).</summary>
    public static string ModelPath(int spellId, string spellName, string stage) =>
        $@"Spells\Custom\{spellId}_{Sanitize(spellName)}\sketch_{Sanitize(stage)}.m2";

    /// <summary>The custom archive path for one piece's texture (§4.1 path law).</summary>
    public static string TexturePath(int spellId, string spellName, string stage, string piece) =>
        $@"Spells\Custom\{spellId}_{Sanitize(spellName)}\sketch_{Sanitize(stage)}_{Sanitize(piece)}.blp";

    /// <summary>Archive paths are ASCII, no spaces, no separators.</summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "x";
        Span<char> buffer = stackalloc char[Math.Min(raw.Length, 48)];
        int n = 0;
        foreach (char c in raw)
        {
            if (n == buffer.Length) break;
            buffer[n++] = char.IsLetterOrDigit(c) ? c : '_';
        }
        return n == 0 ? "x" : new string(buffer[..n]);
    }
}
