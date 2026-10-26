using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator grid + emitter gizmos (shared_docs/SPELL_CREATOR_IDE.md §2.3)
//
// The picture half of the spatial editor: a reference grid around the acting
// body, one gizmo per live emitter of the selected spell, a marker per ribbon
// and the selected phase's skeleton - built by the pure SpellEmitterGizmoLaw
// from the particle system's live pool frames and the effect source's posed
// bones, drawn by SpellGizmoRenderer in the debug pass. Labels ride the ImGui
// background draw list (creator mode is a dev surface under the ImGui policy).
//
// The selected emitter / ribbon / picked bone draws bold and wears its drag
// handles (GameLoop.Creator.Handles.cs, §2.10), drawn as a second, never
// depth-tested overlay pass so a handle is always grabbable.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum GizmoLabelKind { Emitter, Bone, Ribbon }

    /// <summary>A world-anchored tag: the e/b/r label at an emitter origin, a bone pivot or a
    /// ribbon; also the pick set for click-to-select and double-click-to-frame.</summary>
    private readonly record struct GizmoLabel(Vector3 World, string Text, uint Color, string ModelPath,
        GizmoLabelKind Kind, int Index);

    /// <summary>Built in Program.cs init beside the collision debug renderers.</summary>
    private SpellGizmoRenderer? _spellGizmos;

    private readonly List<GizmoLine> _gizmoLines = new();
    private readonly List<GizmoLabel> _gizmoLabels = new();
    private readonly List<SpellEmitterFrame> _creatorEmitterFrames = new();
    private readonly List<SpellEffectSource.BoneFrame> _creatorBoneFrames = new();

    /// <summary>Written by CreatorCategory for the header it just drew, read by the
    /// model editor to find the hovered emitter row.</summary>
    private bool _creatorCategoryHovered;

    /// <summary>The emitter whose row header is hovered this GUI pass (model path, index).</summary>
    private (string Path, int Index)? _creatorGizmoHover;

    /// <summary>The bone or ribbon label under the mouse this GUI pass (bold next frame).</summary>
    private (string Path, GizmoLabelKind Kind, int Index)? _creatorGizmoHoverLabel;

    private bool CreatorGizmosVisible =>
        _creatorWorldRequested && !_worldLoading &&
        _creatorPanel == CreatorPanel.Spells && _creatorSpell is not null;

    private int CreatorEmitterLabelCount => _gizmoLabels.Count(l => l.Kind == GizmoLabelKind.Emitter);

    /// <summary>Debug pass, after the world and the particles so depth is populated.</summary>
    private void RenderCreatorGizmos()
    {
        _gizmoLines.Clear();
        _gizmoLabels.Clear();
        _creatorEmitterFrames.Clear();
        _creatorBoneFrames.Clear();
        _gizmoHandles.Clear();
        _gizmoOverlayLines.Clear();
        if (_spellGizmos is null || !CreatorGizmosVisible) return;

        var settings = Settings.Creator;
        if (settings.SpellGrid && _controller is not null)
        {
            float minor = settings.GridMinorSixthYard
                ? SpellEmitterGizmoLaw.SixthYardMinor
                : SpellEmitterGizmoLaw.DefaultMinorYards;
            float extent = Math.Clamp(settings.GridExtent, 1f, 8f);
            // While placing, the peelable box replaces the grid entirely (UpdateSketchPlaceCube
            // draws it): two overlaid grids would say two different things about what is pickable.
            if (_sketchPlaceMode && _sketchOpen) { }
            else if (settings.GridLattice)
                // The character stands in a clear pocket: the controller's capsule plus a margin.
                SpellEmitterGizmoLaw.Lattice(_gizmoLines, _controller.Position, _controller.Yaw,
                    minor, SpellEmitterGizmoLaw.MajorYards, extent,
                    MathF.Max(_config.Movement.Radius, 0.45f) + 0.2f, _config.Movement.Height + 0.25f);
            else
                SpellEmitterGizmoLaw.Grid(_gizmoLines, _controller.Position, _controller.Yaw,
                    minor, SpellEmitterGizmoLaw.MajorYards, extent,
                    floor: true, side: true, front: true);
        }

        if (settings.SpellGizmos && _creatorSpell is { } doc)
        {
            if (_spellParticles is not null)
                foreach (SpellEmitterFrame frame in _spellParticles.EmitterFrames())
                {
                    if (!TryResolveGizmoModel(doc, frame.Path, out CreatorModelDoc model)) continue;
                    _creatorEmitterFrames.Add(frame);
                    int slot = -1;
                    foreach (var tex in model.Textures)
                        if (tex.ReferencedByEmitters.Contains(frame.EmitterIndex))
                        {
                            slot = tex.Index;
                            break;
                        }
                    Vector4 color = slot >= 0 ? CreatorSlotColor(slot) : new Vector4(1f, 1f, 1f, 1f);
                    bool highlighted = IsGizmoEmitterHighlighted(model, frame.EmitterIndex);
                    SpellEmitterGizmoLaw.Emitter(_gizmoLines, frame, color, highlighted, settings.GizmoReach);
                    _gizmoLabels.Add(new GizmoLabel(frame.Origin, $"e{frame.EmitterIndex}",
                        ImGui.ColorConvertFloat4ToU32(color with { W = highlighted ? 1f : 0.7f }),
                        model.Path, GizmoLabelKind.Emitter, frame.EmitterIndex));
                }

            // Every live instance's posed skeleton, once: the skeleton view, the ribbon markers
            // and the bone handles all read from this.
            if (_spellEffects is not null)
                foreach (SpellEffectSource.BoneFrame frame in _spellEffects.BoneFrames(SpellClockNow, SpellEffectUnitPose))
                    if (TryResolveGizmoModel(doc, frame.Path, out _)) _creatorBoneFrames.Add(frame);

            AddCreatorRulesRings(_gizmoLines, doc);
            DrawCreatorRibbonMarkers(doc);
            DrawCreatorSkeleton(doc, settings.GizmoBones);
            BuildCreatorHandles(doc, _creatorEmitterFrames, _creatorBoneFrames);
        }

        // Place-by-pointing draws into the same list, after the lattice it snaps to.
        UpdateSketchPlaceCube(_gizmoLines);

        if (_gizmoLines.Count > 0)
        {
            _spellGizmos.DepthTest = !settings.GizmoThroughWalls;
            _spellGizmos.Render(_window.Camera, _gizmoLines);
        }
        if (_gizmoOverlayLines.Count > 0)
        {
            _spellGizmos.DepthTest = false;
            _spellGizmos.Render(_window.Camera, _gizmoOverlayLines);
        }
    }

    /// <summary>A diamond and an r&lt;n&gt; tag where each ribbon of the spell's models sits this
    /// instant, in the ribbon's texture slot colour; bold when selected or hovered. Ribbons draw
    /// behind their bone, so this is the only fixed thing to grab.</summary>
    private void DrawCreatorRibbonMarkers(CreatorSpellDoc doc)
    {
        foreach (CreatorModelDoc model in doc.Models.Values)
        {
            foreach (RibbonSnapshot ribbon in model.Ribbons)
            {
                if (model.DisabledRibbons.Contains(ribbon.Index)) continue;
                if (!TryCreatorRibbonWorld(doc, model, ribbon.Index, _creatorBoneFrames, out Vector3 p, out Matrix4x4 frame))
                    continue;
                bool bold = _spellIdeSelection.Kind == SpellIdeKind.Ribbon &&
                            string.Equals(_spellIdeSelection.Path, model.Path, StringComparison.OrdinalIgnoreCase) &&
                            _spellIdeSelection.Emitter == ribbon.Index ||
                            _creatorGizmoHoverLabel is { } hover && hover.Kind == GizmoLabelKind.Ribbon &&
                            hover.Index == ribbon.Index &&
                            string.Equals(hover.Path, model.Path, StringComparison.OrdinalIgnoreCase);
                int slot = model.RibbonEdits.GetValueOrDefault(ribbon.Index)?.TextureSlot ?? ribbon.TextureSlot;
                Vector4 colour = (slot >= 0 ? CreatorSlotColor(slot) : new Vector4(1f, 1f, 1f, 1f)) with { W = bold ? 1f : 0.5f };
                float d = bold ? 0.14f : 0.09f;
                _gizmoLines.Add(new GizmoLine(p - Vector3.UnitX * d, p + Vector3.UnitZ * d, colour));
                _gizmoLines.Add(new GizmoLine(p + Vector3.UnitZ * d, p + Vector3.UnitX * d, colour));
                _gizmoLines.Add(new GizmoLine(p + Vector3.UnitX * d, p - Vector3.UnitZ * d, colour));
                _gizmoLines.Add(new GizmoLine(p - Vector3.UnitZ * d, p - Vector3.UnitX * d, colour));
                _gizmoLines.Add(new GizmoLine(p - Vector3.UnitY * d, p + Vector3.UnitY * d, colour));
                // The edge the ribbon commits: from -heightBelow to +heightAbove along the bone's up.
                RibbonPatch? edit = model.RibbonEdits.GetValueOrDefault(ribbon.Index);
                float above = edit?.HeightAbove ?? ribbon.HeightAboveFirst ?? 0f;
                float below = edit?.HeightBelow ?? ribbon.HeightBelowFirst ?? 0f;
                Vector3 up = SpellGizmoHandleLaw.SafeNormalize(Vector3.TransformNormal(Vector3.UnitY, SpellGizmoHandleLaw.Linear(frame)), Vector3.UnitZ);
                if (above > 1e-3f || below > 1e-3f)
                    _gizmoLines.Add(new GizmoLine(p - up * below, p + up * above, colour with { W = colour.W * 0.7f }));
                _gizmoLabels.Add(new GizmoLabel(p, $"r{ribbon.Index}", ImGui.ColorConvertFloat4ToU32(colour),
                    model.Path, GizmoLabelKind.Ribbon, ribbon.Index));
            }
        }
    }

    /// <summary>The selected phase's skeleton: thin lines to each parent, b&lt;n&gt; labels, the
    /// bone the BONES section is looking at drawn bold (SPELL_CREATOR_IDE §2.6).</summary>
    private void DrawCreatorSkeleton(CreatorSpellDoc doc, bool enabled)
    {
        if (!enabled || _spellIdeSelection.Kind is not (SpellIdeKind.Phase or SpellIdeKind.Emitter or SpellIdeKind.Mesh or SpellIdeKind.Ribbon) ||
            !TryGetSpellIdeModel(_spellIdeSelection.Path, out CreatorModelDoc boneModel))
            return;
        string wanted = SpellVisualCatalog.ModelPath(boneModel.Path);
        int picked = CreatorPickedBone(boneModel);
        var positions = new Dictionary<(string Path, int Bone), Vector3>();
        var frames = new List<SpellEffectSource.BoneFrame>();
        foreach (SpellEffectSource.BoneFrame frame in _creatorBoneFrames)
        {
            if (!TryResolveGizmoModel(doc, frame.Path, out CreatorModelDoc owner) ||
                !string.Equals(SpellVisualCatalog.ModelPath(owner.Path), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            positions[(frame.Path, frame.Bone)] = frame.World;
            frames.Add(frame);
        }
        foreach (SpellEffectSource.BoneFrame frame in frames)
        {
            bool bold = frame.Bone == picked ||
                        _creatorGizmoHoverLabel is { } hover && hover.Kind == GizmoLabelKind.Bone && hover.Index == frame.Bone &&
                        string.Equals(hover.Path, boneModel.Path, StringComparison.OrdinalIgnoreCase);
            var colour = new Vector4(1f, 1f, 1f, bold ? 0.95f : 0.35f);
            float tick = bold ? 0.08f : 0.04f;
            Vector3 p = frame.World;
            _gizmoLines.Add(new GizmoLine(p - Vector3.UnitX * tick, p + Vector3.UnitX * tick, colour));
            _gizmoLines.Add(new GizmoLine(p - Vector3.UnitY * tick, p + Vector3.UnitY * tick, colour));
            _gizmoLines.Add(new GizmoLine(p - Vector3.UnitZ * tick, p + Vector3.UnitZ * tick, colour));
            if (frame.Parent >= 0 && positions.TryGetValue((frame.Path, frame.Parent), out Vector3 parent))
                _gizmoLines.Add(new GizmoLine(p, parent, colour with { W = colour.W * 0.6f }));
            _gizmoLabels.Add(new GizmoLabel(p, $"b{frame.Bone}",
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, bold ? 1f : 0.6f)), boneModel.Path,
                GizmoLabelKind.Bone, frame.Bone));
        }
    }

    /// <summary>A pool path is "spell:{resolved model path}#{instance id}"; match it to the
    /// selected spell's model docs by resolved path so only THIS spell's emitters draw.</summary>
    private static bool TryResolveGizmoModel(CreatorSpellDoc doc, string poolPath,
        out CreatorModelDoc model)
    {
        model = null!;
        const string prefix = "spell:";
        if (!poolPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int hash = poolPath.IndexOf('#');
        string path = hash > prefix.Length ? poolPath[prefix.Length..hash] : poolPath[prefix.Length..];
        foreach (CreatorModelDoc candidate in doc.Models.Values)
        {
            if (string.Equals(SpellVisualCatalog.ModelPath(candidate.Path), path,
                    StringComparison.OrdinalIgnoreCase))
            {
                model = candidate;
                return true;
            }
        }
        return false;
    }

    private bool IsGizmoEmitterHighlighted(CreatorModelDoc model, int emitterIndex)
    {
        // The IDE's selection and its pinned inspectors draw bold; the hovered row and
        // an open classic-layout category do too.
        if (_spellIdeSelection.IsEmitterOf(model.Path, emitterIndex)) return true;
        foreach (SpellIdeSelection pin in _spellIdePins)
            if (pin.IsEmitterOf(model.Path, emitterIndex)) return true;
        if (_creatorGizmoHover is { } hover &&
            string.Equals(hover.Path, model.Path, StringComparison.OrdinalIgnoreCase) &&
            hover.Index == emitterIndex)
            return true;
        return GetSectionOpen($"ws-{model.Path}-em{emitterIndex}", false);
    }

    /// <summary>GUI pass: the e/b/r tags at their anchors, under every window.</summary>
    private void DrawCreatorGizmoLabels()
    {
        if (_gizmoLabels.Count == 0 || !CreatorGizmosVisible) return;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (GizmoLabel label in _gizmoLabels)
        {
            if (!_window.Camera.TryWorldToScreen(label.World, display, out Vector2 pixel)) continue;
            Vector2 at = pixel + new Vector2(7f, -9f);
            draw.AddText(at + Vector2.One, 0xC0000000, label.Text);
            draw.AddText(at, label.Color, label.Text);
        }
    }
}
