using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator.Sketch;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// THE DRAW CANVAS — shared_docs/SPELL_SKETCH.md slice S6, and the half of the
// owner's original ask that the shape shelf does not answer:
//
//   "I could basically free form, in the editor, draw a crescent shape..."
//
// The shelf gives you seven shapes somebody else decided on. This gives you a
// square of paper: hold the left button and draw, lift the pen and draw again,
// Enter to accept, Escape to throw it away. What comes out is a piece exactly
// like any other - it gets the same LOOK, PLACE, MOVE and EXTRAS cards, and it
// compiles through the same signed-distance-free stroke rasterizer into the
// same kind of texture on the same kind of quad.
//
// Drawing happens in 0..1 canvas space, so the drawing is resolution-free: the
// piece's Size in yards and the texture's pixel size are decided later and
// independently, and re-sizing a piece never re-samples a hand-drawn line.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private bool _sketchDrawOpen;
    private readonly List<List<Vector2>> _sketchDrawStrokes = new();
    private List<Vector2>? _sketchDrawStroke;
    private float _sketchDrawPen = 24f;
    private bool _sketchDrawClosed;

    /// <summary>Below this (in canvas units) a new sample is the same point as the last one.
    /// Without it a slow hand lays down hundreds of coincident points, which cost memory,
    /// blunt the round joins and make the piece's cache key enormous for no visible gain.</summary>
    private const float SketchDrawMinStep = 0.004f;

    private void OpenSketchDrawCanvas()
    {
        _sketchDrawOpen = true;
        _sketchDrawStrokes.Clear();
        _sketchDrawStroke = null;
    }

    /// <summary>The paper. Returns true while it is open, so the caller can tell that the
    /// canvas - not the cards - owns the mouse this frame.</summary>
    private bool DrawSketchDrawCanvas(CreatorSpellDoc doc, SketchDoc sketch)
    {
        if (!_sketchDrawOpen) return false;
        float cs = CreatorUiScale;
        var io = ImGui.GetIO();

        float side = MathF.Min(MathF.Min(io.DisplaySize.X, io.DisplaySize.Y) * 0.6f, 620f * cs);
        var size = new Vector2(side + 16f, side + 92f * cs);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowPos((io.DisplaySize - size) * 0.5f, ImGuiCond.Appearing);

        if (ImGui.Begin("##sketch-draw", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                                         ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking))
        {
            ImGui.TextColored(SpellIdeGold, "DRAW");
            CreatorHelp("Hold the left button and draw. Lift and draw again for a separate " +
                        "stroke. Enter keeps it, Escape throws it away.\n\n" +
                        "What you draw becomes the shape of a new piece - it gets the same " +
                        "colour, placement, movement and timing controls as any other.");
            ImGui.SameLine();
            ImGui.TextDisabled($"{_sketchDrawStrokes.Count} stroke(s)");

            ImGui.SetNextItemWidth(140f * cs);
            ImGui.SliderFloat("Pen", ref _sketchDrawPen, 4f, 96f, "%.0f px");
            ImGui.SameLine();
            ImGui.Checkbox("Fill", ref _sketchDrawClosed);
            CreatorHelp("Fill closes each stroke and fills the inside, turning an outline into " +
                        "a solid shape.");

            // ── the paper ───────────────────────────────────────────────────
            Vector2 origin = ImGui.GetCursorScreenPos();
            var canvas = new Vector2(side, side);
            ImGui.InvisibleButton("##paper", canvas);
            bool hovered = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            uint paper = ImGui.GetColorU32(new Vector4(0.06f, 0.06f, 0.07f, 1f));
            uint frame = ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.5f, 1f));
            uint guide = ImGui.GetColorU32(new Vector4(0.16f, 0.16f, 0.19f, 1f));
            uint ink = ImGui.GetColorU32(new Vector4(1f, 0.95f, 0.8f, 1f));

            draw.AddRectFilled(origin, origin + canvas, paper);
            // Quarter guides and the centre, so a shape can be aimed rather than guessed at.
            for (int i = 1; i < 4; i++)
            {
                float t = origin.X + canvas.X * i / 4f;
                float u = origin.Y + canvas.Y * i / 4f;
                draw.AddLine(new Vector2(t, origin.Y), new Vector2(t, origin.Y + canvas.Y), guide);
                draw.AddLine(new Vector2(origin.X, u), new Vector2(origin.X + canvas.X, u), guide);
            }
            draw.AddRect(origin, origin + canvas, frame);

            // ── the pen ─────────────────────────────────────────────────────
            // While the button is HELD the stroke follows the hand even past the paper's
            // edge (clamped to it): `held` already means the press began on the paper, and
            // gating on `hovered` as well stopped sampling the moment the cursor crossed the
            // frame, so a wide curve came back as one straight chord across the gap.
            Vector2 local = (io.MousePos - origin) / side;
            if (held)
            {
                Vector2 clamped = Vector2.Clamp(local, Vector2.Zero, Vector2.One);
                if (_sketchDrawStroke is null)
                {
                    _sketchDrawStroke = new List<Vector2> { clamped };
                    _sketchDrawStrokes.Add(_sketchDrawStroke);
                }
                else if ((clamped - _sketchDrawStroke[^1]).Length() >= SketchDrawMinStep)
                {
                    _sketchDrawStroke.Add(clamped);
                }
            }
            else if (!held && _sketchDrawStroke is not null)
            {
                _sketchDrawStroke = null;          // pen lifted: the next press starts a stroke
            }

            // ── the ink ─────────────────────────────────────────────────────
            // The preview must draw what the rasterizer will make. SketchTextures strokes the
            // path with ROUND caps and ROUND joins (Skia), so a dot is a disc and a curve is
            // one continuous band. ImGui's thick AddLine is a bare rectangle per segment - no
            // cap, no join - and a drag came out as a fringe of loose rectangles ("line
            // things") while a tap drew a clean disc. A disc at every vertex, under the
            // segments, IS a round join; the two together are exactly the Skia stroke.
            float penPixels = MathF.Max(_sketchDrawPen * side / 256f, 1.5f);
            float capRadius = penPixels * 0.5f;
            int capSegments = capRadius < 4f ? 6 : capRadius < 12f ? 10 : 16;
            foreach (List<Vector2> stroke in _sketchDrawStrokes)
            {
                if (stroke.Count == 1)
                {
                    draw.AddCircleFilled(origin + stroke[0] * side, capRadius, ink, capSegments);
                    continue;
                }
                for (int i = 0; i < stroke.Count; i++)
                    draw.AddCircleFilled(origin + stroke[i] * side, capRadius, ink, capSegments);
                for (int i = 1; i < stroke.Count; i++)
                    draw.AddLine(origin + stroke[i - 1] * side, origin + stroke[i] * side,
                                 ink, penPixels);
                if (_sketchDrawClosed && stroke.Count > 2)
                    draw.AddLine(origin + stroke[^1] * side, origin + stroke[0] * side,
                                 ink, penPixels);
            }
            if (hovered)
                draw.AddCircle(io.MousePos, penPixels * 0.5f, frame);

            // ── keep it, or do not ──────────────────────────────────────────
            bool empty = _sketchDrawStrokes.Count == 0;
            if (empty) ImGui.BeginDisabled();
            if (LessonMarked("canvas.keep", CreatorButton("Keep it", 80f * cs)) || ImGui.IsKeyPressed(ImGuiKey.Enter))
                AcceptSketchDrawing(doc, sketch);
            if (empty) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("undo stroke") && _sketchDrawStrokes.Count > 0)
            {
                _sketchDrawStrokes.RemoveAt(_sketchDrawStrokes.Count - 1);
                _sketchDrawStroke = null;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("clear"))
            {
                _sketchDrawStrokes.Clear();
                _sketchDrawStroke = null;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _sketchDrawOpen = false;
                _sketchDrawStrokes.Clear();
                _sketchDrawStroke = null;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Enter keeps  ·  Esc discards");
        }
        ImGui.End();
        return true;
    }

    /// <summary>The drawing becomes an ordinary piece. Everything after this point - colour,
    /// placement, travel, timing, the compiler - treats it exactly like a Crescent.</summary>
    private void AcceptSketchDrawing(CreatorSpellDoc doc, SketchDoc sketch)
    {
        _sketchDrawOpen = false;
        if (_sketchDrawStrokes.Count == 0) return;
        if (sketch.Pieces.Count >= SketchWriter.MaxPieces)
        {
            _sketchStatus = $"Too many pieces (max {SketchWriter.MaxPieces}).";
            return;
        }

        SketchPiece piece = sketch.AddPiece(SketchShapeKind.Stroke);
        piece.Name = sketch.UniqueName("drawing");
        piece.Shape.Strokes = _sketchDrawStrokes.ConvertAll(stroke => new List<Vector2>(stroke));
        piece.Shape.StrokeWidth = _sketchDrawPen;
        piece.Shape.Closed = _sketchDrawClosed;

        _sketchDrawStrokes.Clear();
        _sketchDrawStroke = null;

        SelectSketchPiece(doc, sketch, sketch.Pieces.Count - 1);
        SketchChanged();
        _sketchStatus = $"\"{piece.Name}\" added from your drawing.";
    }
}
