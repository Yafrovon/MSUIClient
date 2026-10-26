using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: DRAG HANDLES (shared_docs/SPELL_CREATOR_IDE.md §2.10)
//
// The write half of the spatial editor. The selected emitter or ribbon wears a
// translate manipulator (centre square, three arrows in the lattice's facing
// frame, three plane squares); the selected emitter also wears its shape marks
// (the reach tip for speed, the birth rectangle's edge marks or the sphere
// radii); the picked bone of the selected phase wears three rings (rotate about
// its own axes) and, when it has a translation track, the translate set.
//
// A drag writes the SAME patch the inspector's dials write (EmitterPatch /
// RibbonPatch / BonePatch), rebuilt live at ~12 Hz without the paused replay,
// then once more on release with it. Shift snaps a move to the grid's minor
// cell and a ring to 5 degrees. Escape cancels a drag; Ctrl+Z (or the strip's
// Undo) restores the patch as it was before the last drag - the inspector's own
// dials are not on the undo stack.
//
// Geometry and arithmetic are the pure SpellGizmoHandleLaw; this file owns the
// state machine, the mouse ownership (LeftButtonReservedForWorldClicks keeps a
// left-drag on a handle from orbiting the camera) and the writes.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const double CreatorHandleRebuildMilliseconds = 80;
    private const int CreatorUndoDepth = 64;

    private readonly List<GizmoHandle> _gizmoHandles = new();
    private readonly List<GizmoLine> _gizmoOverlayLines = new();
    private int _gizmoHandleHover = -1;
    private CreatorHandleDrag? _gizmoDrag;
    private bool _gizmoReservedLeft;
    private readonly Stack<(string Label, Action Restore)> _creatorUndo = new();
    private double _creatorHandleAppliedAt;
    private string _creatorHandleHint = "";

    /// <summary>One drag in flight: the handle, where it started (a parameter along its axis,
    /// a point on its plane, or an angle on its ring), the target's values when it started, and
    /// the frames a world delta is carried back through.</summary>
    private sealed class CreatorHandleDrag
    {
        public required GizmoHandle Handle;
        public required CreatorModelDoc Model;
        public float StartParam;
        public Vector3 StartPoint;
        public float StartAngle;
        public Vector3 StartRaw;             // position / translation, raw file frame
        public float StartScalar;            // speed / area / radius
        public Matrix4x4 LinearFrame;        // model -> world for the translate delta
        public Matrix4x4 BoneWorldLinear;
        public Matrix4x4 ParentWorldLinear;
        public bool Moved;
        public bool Cancelled;
    }

    private bool CreatorHandlesActive => _gizmoDrag is not null || _gizmoHandleHover >= 0;

    // ── build (debug pass, after the gizmos) ─────────────────────────────────

    /// <summary>Handles for the current selection, from this frame's live frames. Called by
    /// RenderCreatorGizmos, which draws the result as an overlay (never depth-tested: a handle
    /// you cannot see is a handle you cannot grab).</summary>
    private void BuildCreatorHandles(CreatorSpellDoc doc, IReadOnlyList<SpellEmitterFrame> emitterFrames,
        IReadOnlyList<SpellEffectSource.BoneFrame> boneFrames)
    {
        _gizmoHandles.Clear();
        _gizmoOverlayLines.Clear();
        if (_controller is null || !Settings.Creator.SpellGizmos) return;
        Camera camera = _window.Camera;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float fov = camera.FieldOfViewDegrees * MathF.PI / 180f;
        float yaw = _controller.Yaw;
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 up = Vector3.UnitZ;
        float Unit(Vector3 at) => SpellGizmoHandleLaw.WorldPerPixel(Vector3.Distance(camera.Position, at), fov, display.Y);

        // While a drag is in flight the handle set is frozen on its target so the piece under
        // the mouse cannot change identity mid-gesture.
        SpellIdeSelection selection = _spellIdeSelection;
        if (_gizmoDrag is { } drag)
            selection = drag.Handle.Target switch
            {
                GizmoTargetKind.Emitter => new SpellIdeSelection(SpellIdeKind.Emitter, drag.Handle.ModelPath, drag.Handle.Index),
                GizmoTargetKind.Ribbon => new SpellIdeSelection(SpellIdeKind.Ribbon, drag.Handle.ModelPath, drag.Handle.Index),
                _ => new SpellIdeSelection(SpellIdeKind.Phase, drag.Handle.ModelPath),
            };

        if (selection.Kind == SpellIdeKind.Emitter && TryGetSpellIdeModel(selection.Path, out CreatorModelDoc emitterModel) &&
            !emitterModel.DisabledEmitters.Contains(selection.Emitter))
        {
            foreach (SpellEmitterFrame frame in emitterFrames)
            {
                if (frame.EmitterIndex != selection.Emitter ||
                    !TryResolveGizmoModel(doc, frame.Path, out CreatorModelDoc owner) ||
                    !ReferenceEquals(owner, emitterModel)) continue;
                float unit = Unit(frame.Origin);
                SpellGizmoHandleLaw.Translate(_gizmoHandles, GizmoTargetKind.Emitter, emitterModel.Path,
                    frame.EmitterIndex, frame.Origin, forward, left, up, unit);
                SpellGizmoHandleLaw.EmitterShape(_gizmoHandles, frame, emitterModel.Path, unit, Settings.Creator.GizmoReach);
                _creatorHandleFrame = frame.LinearFrame;
                break;   // one manipulator, on the first live instance
            }
        }
        else if (selection.Kind == SpellIdeKind.Ribbon && TryGetSpellIdeModel(selection.Path, out CreatorModelDoc ribbonModel) &&
                 !ribbonModel.DisabledRibbons.Contains(selection.Emitter))
        {
            if (TryCreatorRibbonWorld(doc, ribbonModel, selection.Emitter, boneFrames, out Vector3 at, out Matrix4x4 frame))
            {
                SpellGizmoHandleLaw.Translate(_gizmoHandles, GizmoTargetKind.Ribbon, ribbonModel.Path, selection.Emitter,
                    at, forward, left, up, Unit(at));
                _creatorHandleFrame = frame;
            }
        }
        else if (selection.Kind == SpellIdeKind.Phase && Settings.Creator.GizmoBones &&
                 TryGetSpellIdeModel(selection.Path, out CreatorModelDoc boneModel) && boneModel.Bones.Count > 0)
        {
            int picked = CreatorPickedBone(boneModel);
            string wanted = SpellVisualCatalog.ModelPath(boneModel.Path);
            foreach (SpellEffectSource.BoneFrame frame in boneFrames)
            {
                if (frame.Bone != picked || !TryResolveGizmoModel(doc, frame.Path, out CreatorModelDoc owner) ||
                    !string.Equals(SpellVisualCatalog.ModelPath(owner.Path), wanted, StringComparison.OrdinalIgnoreCase))
                    continue;
                BoneSnapshot bone = boneModel.Bones[picked];
                bool rotatable = bone.RotationKeys > 0;
                if (rotatable || bone.TranslationKeys > 0)
                    SpellGizmoHandleLaw.Bone(_gizmoHandles, boneModel.Path, picked, frame.World, frame.Frame,
                        Unit(frame.World), bone.TranslationKeys > 0, forward, left, up);
                if (!rotatable) _gizmoHandles.RemoveAll(h => h.IsRing);
                _creatorBoneWorldLinear = frame.Frame;
                _creatorParentWorldLinear = frame.ParentFrame;
                break;
            }
        }

        if (_gizmoHandles.Count == 0) return;
        Vector3 camRight = SpellGizmoHandleLaw.SafeNormalize(camera.FlatRight, Vector3.UnitY);
        Vector3 camUp = SpellGizmoHandleLaw.SafeNormalize(Vector3.Cross(camRight, camera.Forward), Vector3.UnitZ);
        for (int i = 0; i < _gizmoHandles.Count; i++)
        {
            GizmoHandle h = _gizmoHandles[i];
            bool active = _gizmoDrag is { } d && d.Handle.Kind == h.Kind && d.Handle.Target == h.Target &&
                          d.Handle.Index == h.Index;
            SpellGizmoHandleLaw.Draw(_gizmoOverlayLines, h, hovered: i == _gizmoHandleHover, active,
                Unit(h.Anchor), camRight, camUp);
        }
    }

    // The live frames the current handle set was built from (world <- model).
    private Matrix4x4 _creatorHandleFrame = Matrix4x4.Identity;
    private Matrix4x4 _creatorBoneWorldLinear = Matrix4x4.Identity;
    private Matrix4x4 _creatorParentWorldLinear = Matrix4x4.Identity;

    /// <summary>Where a ribbon sits this instant: its (edited or authored) position through its
    /// (edited or authored) bone's live frame, on the first live instance of the model.</summary>
    private bool TryCreatorRibbonWorld(CreatorSpellDoc doc, CreatorModelDoc model, int ribbonIndex,
        IReadOnlyList<SpellEffectSource.BoneFrame> boneFrames, out Vector3 world, out Matrix4x4 frame)
    {
        world = default;
        frame = Matrix4x4.Identity;
        RibbonSnapshot? ribbon = model.Ribbons.FirstOrDefault(r => r.Index == ribbonIndex);
        if (ribbon is null) return false;
        RibbonPatch? edit = model.RibbonEdits.GetValueOrDefault(ribbonIndex);
        int bone = edit?.Bone ?? ribbon.Bone;
        var raw = new Vector3(edit?.PositionX ?? ribbon.PositionX, edit?.PositionY ?? ribbon.PositionY,
            edit?.PositionZ ?? ribbon.PositionZ);
        string wanted = SpellVisualCatalog.ModelPath(model.Path);
        foreach (SpellEffectSource.BoneFrame bf in boneFrames)
        {
            if (bf.Bone != bone || !TryResolveGizmoModel(doc, bf.Path, out CreatorModelDoc owner) ||
                !string.Equals(SpellVisualCatalog.ModelPath(owner.Path), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            // The ribbon record is a MODEL-space point on its bone's skinning matrix (the
            // renderer's own placement); the pivot is inside skin[b].
            world = Vector3.Transform(SpellGizmoHandleLaw.Swap(raw), bf.Frame);
            frame = bf.Frame;
            return true;
        }
        return false;
    }

    // ── the state machine (GUI pass, before the label pick) ──────────────────

    private void SpellIdeDragHandles()
    {
        var io = ImGui.GetIO();
        _creatorHandleHint = "";
        Vector2 display = io.DisplaySize;
        Camera camera = _window.Camera;
        Vector2? Project(Vector3 world) =>
            camera.TryProjectToScreen(world, display, out Vector2 pixel, out _) ? pixel : null;

        // Ctrl+Z / Ctrl+Y: the whole design (GameLoop.Creator.Undo.cs) - a drag is one step of it.
        HandleCreatorUndoKeys();

        if (_gizmoDrag is { } drag)
        {
            bool escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
            if (escape) drag.Cancelled = true;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || escape)
            {
                FinishCreatorHandleDrag(drag);
                _gizmoDrag = null;
            }
            else
            {
                UpdateCreatorHandleDrag(drag, io.MousePos, display, io.KeyShift);
                _creatorHandleHint = $"{SpellGizmoHandleLaw.Describe(drag.Handle.Kind)}  -  Esc cancels" +
                                     (io.KeyShift ? "  (snapping)" : "  (Shift snaps)");
            }
        }
        else
        {
            _gizmoHandleHover = -1;
            if (!io.WantCaptureMouse && !_window.MouseCaptured && _gizmoHandles.Count > 0)
            {
                _gizmoHandleHover = SpellGizmoHandleLaw.Pick(_gizmoHandles, io.MousePos, Project);
                if (_gizmoHandleHover >= 0)
                {
                    GizmoHandle h = _gizmoHandles[_gizmoHandleHover];
                    _creatorHandleHint = $"{CreatorHandleTargetLabel(h)}: {SpellGizmoHandleLaw.Describe(h.Kind)}";
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) BeginCreatorHandleDrag(h, io.MousePos, display);
                }
            }
        }

        // Own the left button while a handle is under the mouse or in hand, and give it back
        // the moment neither is true - the dev editors share this flag, hence the bookkeeping.
        bool reserve = CreatorHandlesActive && SpellFocusActive;
        if (reserve && !_gizmoReservedLeft) { _window.LeftButtonReservedForWorldClicks = true; _gizmoReservedLeft = true; }
        else if (!reserve && _gizmoReservedLeft) { _window.LeftButtonReservedForWorldClicks = false; _gizmoReservedLeft = false; }

        if (_creatorHandleHint.Length > 0)
        {
            ImDrawListPtr draw = ImGui.GetForegroundDrawList();
            Vector2 at = io.MousePos + new Vector2(16f, 18f);
            draw.AddText(at + Vector2.One, 0xD0000000, _creatorHandleHint);
            draw.AddText(at, ImGui.ColorConvertFloat4ToU32(SpellGizmoHandleLaw.HoverColour), _creatorHandleHint);
        }
    }

    private static string CreatorHandleTargetLabel(in GizmoHandle h) => h.Target switch
    {
        GizmoTargetKind.Emitter => $"e{h.Index}",
        GizmoTargetKind.Ribbon => $"r{h.Index}",
        _ => $"b{h.Index}",
    };

    /// <summary>Escape pre-gate: a drag in flight owns Escape ahead of the game-menu ladder.</summary>
    private bool ConsumeCreatorHandleEscape()
    {
        if (_gizmoDrag is not { } drag) return false;
        drag.Cancelled = true;
        FinishCreatorHandleDrag(drag);
        _gizmoDrag = null;
        return true;
    }

    private void BeginCreatorHandleDrag(GizmoHandle h, Vector2 mouse, Vector2 display)
    {
        if (!TryGetSpellIdeModel(h.ModelPath, out CreatorModelDoc model)) return;
        if (_window.Camera.ScreenPointToRay(mouse, display) is not { } ray) return;

        var drag = new CreatorHandleDrag { Handle = h, Model = model, LinearFrame = _creatorHandleFrame,
            BoneWorldLinear = _creatorBoneWorldLinear, ParentWorldLinear = _creatorParentWorldLinear };

        // Where the gesture starts, in the handle's own parameter.
        if (h.IsAxisDrag) drag.StartParam = SpellGizmoHandleLaw.AxisParam(ray.Origin, ray.Direction, h.Anchor, h.Axis);
        else if (h.IsPlaneDrag)
        {
            Vector3 normal = h.Kind == GizmoHandleKind.Center ? -_window.Camera.Forward : h.Axis;
            drag.StartPoint = SpellGizmoHandleLaw.PlanePoint(ray.Origin, ray.Direction, h.Anchor, normal) ?? h.Anchor;
        }
        else if (h.IsRing)
            drag.StartAngle = SpellGizmoHandleLaw.RingAngle(ray.Origin, ray.Direction, h.Anchor, h.Axis, h.Tangent) ?? 0f;

        // The values being written, as they are now (edit over authored).
        switch (h.Target)
        {
            case GizmoTargetKind.Emitter:
            {
                EmitterSnapshot? e = model.Emitters.FirstOrDefault(x => x.Index == h.Index);
                if (e is null) return;
                EmitterPatch? p = model.Edits.GetValueOrDefault(h.Index);
                drag.StartRaw = new Vector3(p?.PositionX ?? e.PositionX, p?.PositionY ?? e.PositionY, p?.PositionZ ?? e.PositionZ);
                drag.StartScalar = h.Kind switch
                {
                    GizmoHandleKind.Speed => p?.EmissionSpeed ?? e.TrackValues.GetValueOrDefault("emissionSpeed") ?? 0f,
                    GizmoHandleKind.AreaLength or GizmoHandleKind.Radius =>
                        p?.EmissionAreaLength ?? e.TrackValues.GetValueOrDefault("emissionAreaLength") ?? 0f,
                    GizmoHandleKind.AreaWidth => p?.EmissionAreaWidth ?? e.TrackValues.GetValueOrDefault("emissionAreaWidth") ?? 0f,
                    _ => 0f,
                };
                PushCreatorUndo($"e{h.Index} {SpellGizmoHandleLaw.Describe(h.Kind)}",
                    CreatorUndoRestore(model, h.Index, CloneCreatorPatch(p)));
                break;
            }
            case GizmoTargetKind.Ribbon:
            {
                RibbonSnapshot? r = model.Ribbons.FirstOrDefault(x => x.Index == h.Index);
                if (r is null) return;
                RibbonPatch? p = model.RibbonEdits.GetValueOrDefault(h.Index);
                drag.StartRaw = new Vector3(p?.PositionX ?? r.PositionX, p?.PositionY ?? r.PositionY, p?.PositionZ ?? r.PositionZ);
                PushCreatorUndo($"r{h.Index} move", CreatorUndoRestore(model, h.Index, CloneCreatorPatch(p)));
                break;
            }
            case GizmoTargetKind.Bone:
            {
                if (h.Index < 0 || h.Index >= model.Bones.Count) return;
                BoneSnapshot b = model.Bones[h.Index];
                BonePatch? p = model.BoneEdits.GetValueOrDefault(h.Index);
                Vector3 t = b.TranslationFirst ?? Vector3.Zero;
                drag.StartRaw = new Vector3(p?.TranslationX ?? t.X, p?.TranslationY ?? t.Y, p?.TranslationZ ?? t.Z);
                PushCreatorUndo($"b{h.Index} {(h.IsRing ? "rotate" : "move")}",
                    CreatorUndoRestore(model, h.Index, CloneCreatorPatch(p)));
                break;
            }
        }
        _gizmoDrag = drag;
        _creatorHandleAppliedAt = 0;
    }

    private void UpdateCreatorHandleDrag(CreatorHandleDrag drag, Vector2 mouse, Vector2 display, bool snap)
    {
        if (_window.Camera.ScreenPointToRay(mouse, display) is not { } ray) return;
        GizmoHandle h = drag.Handle;
        float minor = Settings.Creator.GridMinorSixthYard ? SpellEmitterGizmoLaw.SixthYardMinor
                                                          : SpellEmitterGizmoLaw.DefaultMinorYards;
        bool changed = false;

        if (h.IsRing)
        {
            if (SpellGizmoHandleLaw.RingAngle(ray.Origin, ray.Direction, h.Anchor, h.Axis, h.Tangent) is not { } angle) return;
            float delta = angle - drag.StartAngle;
            if (snap) delta = SpellGizmoHandleLaw.Snap(delta, 5f * MathF.PI / 180f);
            changed = ApplyCreatorBoneRotation(drag, delta);
        }
        else if (h.IsAxisDrag)
        {
            float t = SpellGizmoHandleLaw.AxisParam(ray.Origin, ray.Direction, h.Anchor, h.Axis);
            switch (h.Kind)
            {
                case GizmoHandleKind.Speed:
                {
                    // The tip sits |speed| * lifespan * stretch yards out; Scale is stretch * lifespan.
                    float speed = MathF.Max(t, 0.02f) / MathF.Max(h.Scale, 1e-6f);
                    if (snap) speed = SpellGizmoHandleLaw.Snap(speed, 0.5f);
                    speed *= drag.StartScalar < 0f ? -1f : 1f;
                    changed = ApplyCreatorEmitterScalar(drag, GizmoHandleKind.Speed, speed);
                    break;
                }
                case GizmoHandleKind.AreaLength:
                case GizmoHandleKind.AreaWidth:
                case GizmoHandleKind.Radius:
                {
                    float kernel = MathF.Max(t, 0f) / MathF.Max(h.Scale, 1e-6f);
                    float value = h.Kind == GizmoHandleKind.Radius || drag.Model.Emitters.FirstOrDefault(e => e.Index == h.Index)?.EmitterType == 2
                        ? kernel : kernel * 2f;
                    if (snap) value = SpellGizmoHandleLaw.Snap(value, minor);
                    changed = ApplyCreatorEmitterScalar(drag, h.Kind, value);
                    break;
                }
                default:
                {
                    float along = t - drag.StartParam;
                    if (snap) along = SpellGizmoHandleLaw.Snap(along, minor);
                    changed = ApplyCreatorTranslate(drag, h.Axis * along);
                    break;
                }
            }
        }
        else if (h.IsPlaneDrag)
        {
            Vector3 normal = h.Kind == GizmoHandleKind.Center ? -_window.Camera.Forward : h.Axis;
            if (SpellGizmoHandleLaw.PlanePoint(ray.Origin, ray.Direction, h.Anchor, normal) is not { } hit) return;
            Vector3 delta = hit - drag.StartPoint;
            if (snap) delta = new Vector3(SpellGizmoHandleLaw.Snap(delta.X, minor), SpellGizmoHandleLaw.Snap(delta.Y, minor),
                SpellGizmoHandleLaw.Snap(delta.Z, minor));
            changed = ApplyCreatorTranslate(drag, delta);
        }

        if (!changed) return;
        drag.Moved = true;
        double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        if (now - _creatorHandleAppliedAt >= CreatorHandleRebuildMilliseconds)
        {
            _creatorHandleAppliedAt = now;
            RebuildCreatorModel(drag.Model, replay: false);
        }
    }

    private void FinishCreatorHandleDrag(CreatorHandleDrag drag)
    {
        if (drag.Cancelled)
        {
            // The undo entry pushed at the start IS the pre-drag state: pop and apply it.
            if (_creatorUndo.Count > 0) _creatorUndo.Pop().Restore();
            return;
        }
        if (!drag.Moved)
        {
            if (_creatorUndo.Count > 0) _creatorUndo.Pop();   // a click, not a drag: nothing to undo
            return;
        }
        RebuildCreatorModel(drag.Model);   // the final bytes, and the paused replay
    }

    // ── writes ───────────────────────────────────────────────────────────────

    private bool ApplyCreatorTranslate(CreatorHandleDrag drag, Vector3 worldDelta)
    {
        GizmoHandle h = drag.Handle;
        CreatorModelDoc model = drag.Model;
        switch (h.Target)
        {
            case GizmoTargetKind.Emitter:
            {
                Vector3 raw = drag.StartRaw + SpellGizmoHandleLaw.WorldDeltaToRaw(worldDelta, drag.LinearFrame);
                EmitterPatch p = model.Edits.TryGetValue(h.Index, out var found) ? found : new EmitterPatch { EmitterIndex = h.Index };
                if (p.PositionX == raw.X && p.PositionY == raw.Y && p.PositionZ == raw.Z) return false;
                p.PositionX = raw.X; p.PositionY = raw.Y; p.PositionZ = raw.Z;
                model.Edits[h.Index] = p;
                return true;
            }
            case GizmoTargetKind.Ribbon:
            {
                Vector3 raw = drag.StartRaw + SpellGizmoHandleLaw.WorldDeltaToRaw(worldDelta, drag.LinearFrame);
                RibbonPatch p = model.RibbonEdits.TryGetValue(h.Index, out var found) ? found : new RibbonPatch { RibbonIndex = h.Index };
                if (p.PositionX == raw.X && p.PositionY == raw.Y && p.PositionZ == raw.Z) return false;
                p.PositionX = raw.X; p.PositionY = raw.Y; p.PositionZ = raw.Z;
                model.RibbonEdits[h.Index] = p;
                return true;
            }
            case GizmoTargetKind.Bone:
            {
                // A bone's translation key lives in its PARENT's frame (S * R * T * parent).
                Vector3 raw = drag.StartRaw + SpellGizmoHandleLaw.WorldDeltaToRaw(worldDelta, drag.ParentWorldLinear);
                BonePatch p = model.BoneEdits.TryGetValue(h.Index, out var found) ? found : new BonePatch { BoneIndex = h.Index };
                if (p.TranslationX == raw.X && p.TranslationY == raw.Y && p.TranslationZ == raw.Z) return false;
                p.TranslationX = raw.X; p.TranslationY = raw.Y; p.TranslationZ = raw.Z;
                if (h.Index < model.Bones.Count && model.Bones[h.Index].Animated) p.AllKeys = true;
                model.BoneEdits[h.Index] = p;
                return true;
            }
        }
        return false;
    }

    private bool ApplyCreatorEmitterScalar(CreatorHandleDrag drag, GizmoHandleKind kind, float value)
    {
        CreatorModelDoc model = drag.Model;
        int index = drag.Handle.Index;
        EmitterPatch p = model.Edits.TryGetValue(index, out var found) ? found : new EmitterPatch { EmitterIndex = index };
        switch (kind)
        {
            case GizmoHandleKind.Speed:
                if (p.EmissionSpeed == value) return false;
                p.EmissionSpeed = value;
                break;
            case GizmoHandleKind.AreaLength:
            case GizmoHandleKind.Radius:
                if (p.EmissionAreaLength == value) return false;
                p.EmissionAreaLength = value;
                break;
            case GizmoHandleKind.AreaWidth:
                if (p.EmissionAreaWidth == value) return false;
                p.EmissionAreaWidth = value;
                break;
            default: return false;
        }
        model.Edits[index] = p;
        return true;
    }

    private bool ApplyCreatorBoneRotation(CreatorHandleDrag drag, float radians)
    {
        CreatorModelDoc model = drag.Model;
        int index = drag.Handle.Index;
        if (index < 0 || index >= model.Bones.Count || model.Bones[index].RotationKeys == 0) return false;
        // The live pose turned by the drag, expressed as the local rotation the file must hold;
        // M2Reader's axis swap undone on the way to the bytes.
        Vector4 modelQ = SpellGizmoHandleLaw.RotateBoneWorld(drag.BoneWorldLinear, drag.ParentWorldLinear,
            drag.Handle.Axis, radians);
        Vector4 raw = SpellGizmoHandleLaw.ModelToRawQuaternion(modelQ);
        BonePatch p = model.BoneEdits.TryGetValue(index, out var found) ? found : new BonePatch { BoneIndex = index };
        if (p.Rotation == raw) return false;
        p.Rotation = raw;
        p.RotationEulerDegrees = null;
        // A ring drag means "hold THIS pose": an animated bone gets every key written.
        if (model.Bones[index].Animated) p.AllKeys = true;
        model.BoneEdits[index] = p;
        return true;
    }

    // ── undo ─────────────────────────────────────────────────────────────────

    private void PushCreatorUndo(string label, Action restore)
    {
        _creatorUndo.Push((label, restore));
        if (_creatorUndo.Count > CreatorUndoDepth)
        {
            var kept = _creatorUndo.Take(CreatorUndoDepth).Reverse().ToArray();
            _creatorUndo.Clear();
            foreach (var entry in kept) _creatorUndo.Push(entry);
        }
    }

    private void CreatorUndoLast()
    {
        if (_creatorUndo.Count == 0) return;
        _creatorUndo.Pop().Restore();
    }

    private string? CreatorUndoLabel => _creatorUndo.Count > 0 ? _creatorUndo.Peek().Label : null;

    private Action CreatorUndoRestore(CreatorModelDoc model, int index, EmitterPatch? before) => () =>
    {
        if (before is null) model.Edits.Remove(index); else model.Edits[index] = before;
        RebuildCreatorModel(model);
    };

    private Action CreatorUndoRestore(CreatorModelDoc model, int index, RibbonPatch? before) => () =>
    {
        if (before is null) model.RibbonEdits.Remove(index); else model.RibbonEdits[index] = before;
        RebuildCreatorModel(model);
    };

    private Action CreatorUndoRestore(CreatorModelDoc model, int index, BonePatch? before) => () =>
    {
        if (before is null) model.BoneEdits.Remove(index); else model.BoneEdits[index] = before;
        RebuildCreatorModel(model);
    };

    /// <summary>A property-wise copy of a patch DTO (they are flat bags of nullable values).</summary>
    private static T? CloneCreatorPatch<T>(T? source) where T : class, new()
    {
        if (source is null) return null;
        var copy = new T();
        foreach (var property in typeof(T).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    // ── framing ──────────────────────────────────────────────────────────────

    /// <summary>Put the selection at the centre of the view: in the character view the orbit
    /// swings and zooms so the point sits on the line through the eye target; in the free view
    /// the rig is re-seated on it (the Command View pattern).</summary>
    private void FrameCreatorSelection()
    {
        if (CreatorSelectionWorld() is not { } point) return;
        FrameCreatorPoint(point);
    }

    private Vector3? CreatorSelectionWorld()
    {
        SpellIdeSelection s = _spellIdeSelection;
        foreach (GizmoLabel label in _gizmoLabels)
        {
            if (!string.Equals(label.ModelPath, s.Path, StringComparison.OrdinalIgnoreCase)) continue;
            switch (s.Kind)
            {
                case SpellIdeKind.Emitter when label.Kind == GizmoLabelKind.Emitter && label.Index == s.Emitter:
                case SpellIdeKind.Ribbon when label.Kind == GizmoLabelKind.Ribbon && label.Index == s.Emitter:
                    return label.World;
                case SpellIdeKind.Phase or SpellIdeKind.Mesh when label.Kind == GizmoLabelKind.Bone &&
                    TryGetSpellIdeModel(s.Path, out CreatorModelDoc m) && label.Index == CreatorPickedBone(m):
                    return label.World;
            }
        }
        // A phase with no bone label in view: its first live emitter, else its root.
        foreach (GizmoLabel label in _gizmoLabels)
            if (string.Equals(label.ModelPath, s.Path, StringComparison.OrdinalIgnoreCase)) return label.World;
        return _controller?.Position is { } feet ? feet + new Vector3(0f, 0f, 1.2f) : null;
    }

    private void FrameCreatorPoint(Vector3 point)
    {
        if (_controller is null) return;
        Camera camera = _window.Camera;
        if (_freeView)
        {
            _commanderFlySettle = null;
            _controller.Teleport(point.X, point.Y, point.Z - camera.EyeHeight);
            camera.Target = _controller.Position;
            _freecamCamSentAt = 0;
            _rtsWheelRetreatYards = 0f;
            return;
        }
        Vector3 eye = camera.EyeTarget;
        Vector3 toEye = eye - point;
        float distance = toEye.Length();
        if (distance > 0.05f)
        {
            Vector3 dir = toEye / distance;   // the camera sits beyond the eye target along this
            float viewYaw = MathF.Atan2(-dir.Y, -dir.X);
            float orbit = viewYaw - camera.Yaw;
            while (orbit > MathF.PI) orbit -= MathF.PI * 2f;
            while (orbit <= -MathF.PI) orbit += MathF.PI * 2f;
            camera.OrbitYaw = orbit;
            camera.Pitch = Math.Clamp(MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)), -Camera.PitchLimit, Camera.PitchLimit);
        }
        camera.Distance = Math.Clamp(distance + 2.5f, camera.MinDistance, camera.MaxDistance);
        camera.EffectiveDistance = camera.Distance;
    }
}
