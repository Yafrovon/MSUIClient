using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell Workshop IDE LAYOUT (2026-09-10) — replaces the two-sidebar focus layout.
//
// The effect is the subject, so the stage gets the screen. Four small pieces
// and nothing else (owner, 2026-09-10: "as much visual space as possible"):
//
//   * STRIP (top-left): Back, the spell, the transport (Loop / Pause / Step /
//     Speed) and the view toggles (Void / Grid / Gizmos / Tree / Inspector),
//     plus Hide. The controls you touch every ten seconds, one row.
//   * OUTLINER (left, collapsible): spell -> fixed rows -> phases -> emitters.
//     Selecting an emitter draws its gizmo bold; clicking a gizmo in the world
//     selects it here. That link is what makes the spatial view an editor.
//   * ONE INSPECTOR (right, draggable, resizable): the dials for whatever is
//     selected, swapped in place. "pin" keeps a copy open on purpose - a second
//     window is a deliberate act, never a side effect of drilling down.
//   * TIMELINE (bottom, ONLY while paused): the scrub and the live phases.
//
// Hide clears every window to a small "Show" pill; the vanilla TOGGLEUI chord
// (Alt+Z) clears everything including the pill. Nothing here persists except
// the switches that were already settings (grid, gizmos, stage radius).
//
// The classic floating panel and the deck still draw the SAME registered
// sections (RegisterCreatorSpellsSections); this layout draws the same bodies
// through its own selection model and does not touch the section registry.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum SpellIdeKind { Spell, Rules, Loop, Clock, Stage, Audio, Session, Composition, Phase, Emitter, Mesh, Ribbon }

    /// <summary>What the inspector shows: a fixed section, a phase model, or one emitter of it.</summary>
    private readonly record struct SpellIdeSelection(SpellIdeKind Kind, string Path = "", int Emitter = -1)
    {
        public bool IsEmitterOf(string path, int emitter) =>
            Kind == SpellIdeKind.Emitter && Emitter == emitter &&
            string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

        public bool IsPhaseOf(string path) =>
            Kind is SpellIdeKind.Phase or SpellIdeKind.Emitter or SpellIdeKind.Mesh or SpellIdeKind.Ribbon &&
            string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Session-only opt-out, written by the strip's "deck" button. NEVER
    /// persisted: a reflexive click must not rewrite a preference - the permanent
    /// switch is the checkbox in the Creator UI dials.</summary>
    private bool _spellFocusSuppressed;

    private SpellIdeSelection _spellIdeSelection = new(SpellIdeKind.Spell);
    private readonly List<SpellIdeSelection> _spellIdePins = new();
    private int _spellIdeUnpinRequest = -1;
    private bool _spellIdeOutlinerHidden;
    private bool _spellIdeInspectorHidden;
    private bool _spellIdeHidden;
    private float _spellIdeStripHeight;
    private float _spellIdeTimelineHeight;
    private float _spellIdeOutlinerWidth;
    private (Vector2 Pos, Vector2 Size)? _spellIdeInspectorRect;

    /// <summary>
    /// The single source of truth for "the workshop owns the screen this frame",
    /// read by the workspace entry point, the inset helpers and the popped-section
    /// pass. The Root-view and customizer terms keep the rails and the deck intact
    /// for the Encounter Lab and the Character Customizer.
    /// </summary>
    private bool SpellFocusActive =>
        CreatorWorkspaceActive && Settings.Creator.SpellFocus && !_spellFocusSuppressed &&
        _creatorPanel == CreatorPanel.Spells && _workspaceView == WorkspaceView.Root &&
        _encounterPlayerSetupKey is null;

    private bool SpellIdeWindowsVisible => SpellFocusActive && !_spellIdeHidden && !_uiHidden;

    /// <summary>How far windows that cascade from the LEFT edge must move right (the outliner).</summary>
    private float SpellIdeLeftInset =>
        SpellIdeWindowsVisible && !_spellIdeOutlinerHidden ? _spellIdeOutlinerWidth : 0f;

    /// <summary>How far right-edge-anchored windows must move left: the inspector's width
    /// while it sits docked against the right edge, nothing once it has been dragged away.</summary>
    private float SpellIdeRightInset
    {
        get
        {
            if (!SpellIdeWindowsVisible || _spellIdeInspectorHidden ||
                _spellIdeInspectorRect is not { } rect) return 0f;
            float right = rect.Pos.X + rect.Size.X;
            return ImGui.GetIO().DisplaySize.X - right < 16f ? rect.Size.X : 0f;
        }
    }

    /// <summary>A new spell: every path-keyed selection just died. An invalid Phase
    /// selection heals to the FIRST phase (the useful landing), or the picker.</summary>
    private void SpellIdeSelectionReset()
    {
        _spellIdeSelection = new SpellIdeSelection(SpellIdeKind.Phase, "");
        _spellIdePins.Clear();
    }

    private void SpellIdeSelect(SpellIdeSelection selection)
    {
        _spellIdeSelection = selection;
        _spellIdeInspectorHidden = false;
    }

    private bool TryGetSpellIdeModel(string path, out CreatorModelDoc model)
    {
        model = null!;
        if (_creatorSpell is not { } doc) return false;
        foreach (CreatorModelDoc candidate in doc.Models.Values)
            if (string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                model = candidate;
                return true;
            }
        return false;
    }

    private bool SpellIdeSelectionValid(SpellIdeSelection s) => s.Kind switch
    {
        SpellIdeKind.Phase => TryGetSpellIdeModel(s.Path, out _),
        SpellIdeKind.Emitter => TryGetSpellIdeModel(s.Path, out CreatorModelDoc m) &&
                                m.Emitters.Any(e => e.Index == s.Emitter),
        SpellIdeKind.Mesh => TryGetSpellIdeModel(s.Path, out CreatorModelDoc mm) &&
                             mm.Meshes.Any(m => m.BatchIndex == s.Emitter),
        SpellIdeKind.Ribbon => TryGetSpellIdeModel(s.Path, out CreatorModelDoc rm) &&
                               rm.Ribbons.Any(r => r.Index == s.Emitter),
        _ => true,
    };

    private void HealSpellIdeSelection()
    {
        if (_creatorSpell is not { } doc)
        {
            if (_spellIdeSelection.Kind != SpellIdeKind.Spell)
                _spellIdeSelection = new SpellIdeSelection(SpellIdeKind.Spell);
            _spellIdePins.Clear();
            return;
        }
        if (!SpellIdeSelectionValid(_spellIdeSelection))
            _spellIdeSelection = doc.Models.Count > 0
                ? new SpellIdeSelection(SpellIdeKind.Phase, doc.Models.Values.First().Path)
                : new SpellIdeSelection(SpellIdeKind.Spell);
        _spellIdePins.RemoveAll(p => !SpellIdeSelectionValid(p));
    }

    // ── entry ────────────────────────────────────────────────────────────────

    private void DrawCreatorSpellFocus()
    {
        // INVARIANT: every early return happens ABOVE this line. _creatorScaleBoost
        // multiplies BOTH CreatorUiScale and CreatorTextScale, so leaking it renders
        // every later creator window this frame up to 2.2x oversized. The id is
        // "Spells" verbatim - it keys the per-window dials shared with the other layouts.
        _activePanelTune = "Spells";
        _creatorScaleBoost = WorkspaceDeckBoost;

        HealSpellIdeSelection();
        _spellIdeOutlinerWidth = 0f;
        if (_uiHidden)
        {
            // TOGGLEUI: the whole picture is the stage. Nothing, not even the pill.
        }
        else if (_spellIdeHidden)
        {
            DrawSpellIdeShowPill();
            SpellIdeDragHandles();
            SpellIdePickGizmo();
        }
        else
        {
            DrawSpellIdeStrip();
            if (CreatorSpellPaused) DrawSpellIdeTimeline();
            else _spellIdeTimelineHeight = 0f;
            if (!_spellIdeOutlinerHidden) DrawSpellIdeOutliner();
            DrawSketchWindow();
            if (!_spellIdeInspectorHidden) DrawSpellIdeInspector(_spellIdeSelection, pinIndex: -1);
            for (int i = 0; i < _spellIdePins.Count; i++) DrawSpellIdeInspector(_spellIdePins[i], i);
            if (_spellIdeUnpinRequest >= 0 && _spellIdeUnpinRequest < _spellIdePins.Count)
                _spellIdePins.RemoveAt(_spellIdeUnpinRequest);
            _spellIdeUnpinRequest = -1;
            SpellIdeDragHandles();   // handles first: a grabbed handle is never a label click
            SpellIdePickGizmo();
        }

        _creatorScaleBoost = 1f;
        _activePanelTune = null;
        if (_workspaceHelpOpen) DrawWorkspaceHelp();
    }

    // ── window plumbing ──────────────────────────────────────────────────────

    /// <summary>A compact creator-styled window. Deliberately NOT BeginWorkspaceRail
    /// (its translucent wash is wrong under dense dials) and not the dialog chrome
    /// (the plaque and border cost the very pixels this layout exists to save).</summary>
    private bool BeginSpellIdeWindow(string id, Vector2 pos, Vector2 size, ImGuiCond cond,
        ImGuiWindowFlags extra, float alphaBoost = 0f)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(pos, cond);
        if (size.X > 0f || size.Y > 0f) ImGui.SetNextWindowSize(size, cond);
        PushCreatorStyle();
        // The deck's compact rhythm: air scales with the display ONCE; the boost
        // stays on glyphs and widgets, or padding reads as enormous empty rows.
        float air = MathF.Max(io.DisplaySize.Y / GlueCanvasH, 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 6f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7f, 4f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 3f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.05f, 0.045f,
            Math.Clamp(Settings.Creator.PanelAlpha + alphaBoost, 0.15f, 1f)));
        bool open = ImGui.Begin(id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | extra);
        ImGui.SetWindowFontScale(CreatorTextScale);
        return open;
    }

    /// <summary>Runs even when Begin returned false - the pushes happened either way.</summary>
    private void EndSpellIdeWindow()
    {
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(4);
        PopCreatorStyle();
    }

    private static readonly Vector4 SpellIdeGold = new(1f, 0.82f, 0.28f, 1f);

    /// <summary>A small on/off button for the strip: gold plate when on, a bordered dark
    /// plate when off - an off toggle that reads as plain text is a toggle nobody finds
    /// (owner, 2026-09-10: "I don't see the button for toggling void").</summary>
    private void SpellIdeToggle(string label, bool on, Action flip, string tip)
    {
        ImGui.SameLine();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.80f, 0.64f, 0.22f, on ? 1f : 0.55f));
        if (on)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.80f, 0.64f, 0.22f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.92f, 0.76f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.06f, 0.03f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.15f, 0.14f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.26f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.90f, 0.86f, 0.78f, 1f));
        }
        if (ImGui.SmallButton(label)) flip();
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    // ── the strip ────────────────────────────────────────────────────────────

    private void DrawSpellIdeStrip()
    {
        float cs = CreatorUiScale;
        var settings = Settings.Creator;
        if (BeginSpellIdeWindow("##spell-ide-strip", Vector2.Zero, Vector2.Zero, ImGuiCond.Always,
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, 0.15f))
        {
            if (CreatorButton("< Back", 64f * cs)) _creatorPanel = CreatorPanel.None;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Close the Spell Workshop (back to the rails).");
            ImGui.SameLine();
            if (_creatorSpell is { } doc)
            {
                if (EditingCreatorChild)
                {
                    // A child spell is open (P4.1): say so, and the way back, where the name is.
                    DrawCreatorChildBar();
                }
                else
                {
                    ImGui.TextColored(SpellIdeGold, doc.Info.Name);
                    ImGui.SameLine();
                    ImGui.TextDisabled($"#{doc.Info.Id}");
                    if (doc.Children.Count > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"+{doc.Children.Count} child spell(s)");
                    }
                }
            }
            else ImGui.TextColored(SpellIdeGold, "SPELL WORKSHOP");
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();

            // Transport.
            bool haveSpell = _creatorSpell is not null;
            if (!haveSpell) ImGui.BeginDisabled();
            if (CreatorButton(_creatorLoopOn ? "Stop" : "Loop", 60f * cs)) ToggleCreatorLoop();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Loop the phases checked in the Loop row (all of them by default " +
                                 "for a spell that has them).");
            ImGui.SameLine();
            bool paused = _creatorClockPaused;
            if (CreatorButton(paused ? "Resume" : "Pause", 72f * cs)) SetCreatorClockPaused(!paused);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Pause the whole spell presentation and keep editing; the held " +
                                 "picture re-lays itself with every change.");
            if (paused)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("<")) StepCreatorClock(-CreatorClockStepSeconds);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Step back 1/30 s");
                ImGui.SameLine();
                if (ImGui.SmallButton(">")) StepCreatorClock(CreatorClockStepSeconds);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Step forward 1/30 s");
            }
            else
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(88f * cs);
                ImGui.SliderFloat("##spell-ide-speed", ref _creatorClockSpeed, 0.1f, 1f, "%.2fx");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Playback speed (slow motion).");
            }
            if (!haveSpell) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("|");

            // View toggles.
            SpellIdeToggle("Void", _creatorStageActive, () => SetCreatorStageActive(!_creatorStageActive),
                "Void stage: black world, only a ground disc at your feet. Radius in the " +
                "'Void stage & gizmos' row.");
            SpellIdeToggle("Grid", settings.SpellGrid,
                () => { settings.SpellGrid = !settings.SpellGrid; SettingsFile?.Save(); },
                "Reference grid through your feet (floor, side, front).");
            SpellIdeToggle("Gizmos", settings.SpellGizmos,
                () => { settings.SpellGizmos = !settings.SpellGizmos; SettingsFile?.Save(); },
                "Emitter gizmos and drag handles: origin, frame, birth shape, reach. Click a label " +
                "to select it, drag the handles on the selection to move / size / rotate it.");
            SpellIdeToggle("Sketch", _sketchOpen,
                () => _sketchOpen = !_sketchOpen,
                "Draw your own geometry: pick a shape, place it, give it travel and a fade. " +
                "It compiles to a real model, so every other dial here works on the result.");
            SpellIdeToggle("Bones", settings.GizmoBones,
                () => { settings.GizmoBones = !settings.GizmoBones; SettingsFile?.Save(); },
                "The selected phase's skeleton (b<n>). Click a bone to pick it; the picked bone " +
                "wears rotate rings.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Frame")) FrameCreatorSelection();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Centre the view on the selection (double-clicking a label in the world does the same).");
            ImGui.SameLine();
            bool canUndo = CanUndoDesign, canRedo = CanRedoDesign;
            if (!canUndo) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Undo")) UndoCreatorDesign();
            if (!canUndo) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canUndo
                    ? $"Undo the last change - any card, dial, rule, slot, sketch piece or drag ({_designUndo.Count} step(s), Ctrl+Z)"
                    : "Nothing to undo yet.");
            ImGui.SameLine();
            if (!canRedo) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Redo")) RedoCreatorDesign();
            if (!canRedo) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canRedo ? $"Redo ({_designRedo.Count} step(s), Ctrl+Y)" : "Nothing to redo.");
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            SpellIdeToggle("Tree", !_spellIdeOutlinerHidden,
                () => _spellIdeOutlinerHidden = !_spellIdeOutlinerHidden,
                "The outliner: spell, phases, emitters.");
            SpellIdeToggle("Inspector", !_spellIdeInspectorHidden,
                () => _spellIdeInspectorHidden = !_spellIdeInspectorHidden,
                "The dials for the selection.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Hide")) _spellIdeHidden = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clear every window; a small Show pill stays top-left.\n" +
                                 "The TOGGLEUI chord (Alt+Z by default) clears the pill too.");
            ImGui.SameLine();
            if (ImGui.SmallButton("dials")) _openPanelTuneId = _openPanelTuneId is null ? "Spells" : null;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("This layout's size dials.");
            ImGui.SameLine();
            if (ImGui.SmallButton("deck")) _spellFocusSuppressed = true;   // session only, no Save
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Back to the rails + deck for this session ('IDE' in the deck " +
                                 "header returns here).");
            ImGui.SameLine();
            if (ImGui.SmallButton("?")) _workspaceHelpOpen = !_workspaceHelpOpen;

            _spellIdeStripHeight = ImGui.GetWindowSize().Y;
        }
        EndSpellIdeWindow();
    }

    private void DrawSpellIdeShowPill()
    {
        if (BeginSpellIdeWindow("##spell-ide-pill", Vector2.Zero, Vector2.Zero, ImGuiCond.Always,
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, -0.2f))
        {
            if (ImGui.SmallButton("Show workshop")) _spellIdeHidden = false;
        }
        EndSpellIdeWindow();
    }

    // ── the outliner ─────────────────────────────────────────────────────────

    private void DrawSpellIdeOutliner()
    {
        var io = ImGui.GetIO();
        float cs = CreatorUiScale;
        float width = MathF.Min(250f * cs, io.DisplaySize.X * 0.24f);
        float top = _spellIdeStripHeight;
        float height = MathF.Max(io.DisplaySize.Y - top - _spellIdeTimelineHeight, 80f);
        _spellIdeOutlinerWidth = width;

        if (BeginSpellIdeWindow("##spell-ide-outliner", new Vector2(0f, top), new Vector2(width, height),
                ImGuiCond.Always, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            var doc = _creatorSpell;
            SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Spell),
                doc is null ? "Spell: (search)" : $"Spell: {doc.Info.Name}");
            if (doc is null)
            {
                ImGui.TextDisabled("Search a spell in the inspector.");
            }
            else
            {
                // What the spell DOES comes first: it decides what the parts below have to show.
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Rules),
                    doc.Rules is { } r && r.ChangedColumns.Any() ? "Rules *  (what it does)" : "Rules  (what it does)");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Loop), _creatorLoopOn ? "Loop  (running)" : "Loop");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Clock), _creatorClockPaused ? "Clock  (paused)" : "Clock");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Stage), _creatorStageActive ? "Void stage  (on)" : "Void stage & gizmos");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Audio), doc.Audio.Count == 0 ? "Audio" : "Audio *");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Composition), CreatorCompositionChanged(doc) ? "Composition *" : "Composition");
                SpellIdeRow(new SpellIdeSelection(SpellIdeKind.Session), "Session");
                ImGui.Separator();
                ImGui.TextDisabled("PHASES");
                if (doc.Models.Count == 0)
                    ImGui.TextWrapped("This spell's visual has no effect models to tune.");

                foreach (CreatorModelDoc model in doc.Models.Values)
                {
                    bool phaseSelected = _spellIdeSelection.Kind == SpellIdeKind.Phase &&
                                         _spellIdeSelection.IsPhaseOf(model.Path);
                    var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                                ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
                    if (phaseSelected) flags |= ImGuiTreeNodeFlags.Selected;
                    ImGui.PushID(model.Path);
                    bool open = ImGui.TreeNodeEx("##phase", flags, CreatorModelLabel(doc, model));
                    if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
                        SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Phase, model.Path));
                    if (open)
                    {
                        // Everything the model draws: mesh layers (m), ribbons (r), emitters (e).
                        DrawSpellIdeMeshRibbonRows(model, cs);
                        var (texByEmitter, slotByEmitter) = CreatorEmitterTextureMaps(model);
                        foreach (EmitterSnapshot emitter in model.Emitters)
                        {
                            bool off = model.DisabledEmitters.Contains(emitter.Index);
                            bool added = emitter.Index >= model.OriginalEmitterCount;
                            bool selected = _spellIdeSelection.IsEmitterOf(model.Path, emitter.Index);
                            ImGui.PushID(emitter.Index);
                            if (slotByEmitter.TryGetValue(emitter.Index, out int slot))
                            {
                                ImGui.ColorButton("##swatch", CreatorSlotColor(slot),
                                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                                    new Vector2(10f * cs, 10f * cs));
                                ImGui.SameLine();
                            }
                            // State FIRST: the tree is narrow and clips the tail of a row.
                            string text = $"e{emitter.Index}  " + (off ? "[OFF] " : "") + (added ? "[added] " : "") +
                                          texByEmitter.GetValueOrDefault(emitter.Index, "no tex");
                            if (ImGui.Selectable(text, selected))
                                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Emitter, model.Path, emitter.Index));
                            if (ImGui.IsItemHovered()) _creatorGizmoHover = (model.Path, emitter.Index);
                            ImGui.PopID();
                        }
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
            }
        }
        EndSpellIdeWindow();
    }

    private void SpellIdeRow(SpellIdeSelection selection, string label)
    {
        bool selected = _spellIdeSelection.Kind == selection.Kind;
        if (ImGui.Selectable(label, selected)) SpellIdeSelect(selection);
    }

    // ── the inspector ────────────────────────────────────────────────────────

    private string SpellIdeTitle(SpellIdeSelection selection)
    {
        switch (selection.Kind)
        {
            case SpellIdeKind.Spell: return "Spell";
            case SpellIdeKind.Rules: return "Rules - what the spell does";
            case SpellIdeKind.Loop: return "Loop";
            case SpellIdeKind.Clock: return "Clock";
            case SpellIdeKind.Stage: return "Void stage & gizmos";
            case SpellIdeKind.Audio: return "Audio";
            case SpellIdeKind.Session: return "Session";
            case SpellIdeKind.Composition: return "Composition (phases, attachments, animation, scale)";
        }
        if (_creatorSpell is not { } doc || !TryGetSpellIdeModel(selection.Path, out CreatorModelDoc model))
            return "(gone)";
        string label = CreatorModelLabel(doc, model);
        return selection.Kind switch
        {
            SpellIdeKind.Emitter => $"{label}  /  e{selection.Emitter}",
            SpellIdeKind.Mesh => $"{label}  /  m{selection.Emitter}",
            SpellIdeKind.Ribbon => $"{label}  /  r{selection.Emitter}",
            _ => label,
        };
    }

    private void DrawSpellIdeInspector(SpellIdeSelection selection, int pinIndex)
    {
        var io = ImGui.GetIO();
        float cs = CreatorUiScale;
        float width = MathF.Min(460f * cs, io.DisplaySize.X * 0.36f);
        float maxH = MathF.Max(io.DisplaySize.Y - _spellIdeStripHeight - _spellIdeTimelineHeight - 16f,
            200f);
        float defaultH = MathF.Min(maxH, io.DisplaySize.Y * 0.62f);
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        float cascade = pinIndex >= 0 ? (pinIndex + 1) * 28f * cs : 0f;
        var pos = new Vector2(io.DisplaySize.X - width - 8f - cascade, _spellIdeStripHeight + 8f + cascade);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300f * cs, 160f * cs),
            new Vector2(io.DisplaySize.X, maxH));
        string id = pinIndex >= 0 ? $"###spell-ide-pin-{pinIndex}" : "###spell-ide-inspector";

        if (BeginSpellIdeWindow(id, pos, new Vector2(width, defaultH), cond,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ClampCreatorWindowOnScreen();
            if (pinIndex < 0) _spellIdeInspectorRect = (ImGui.GetWindowPos(), ImGui.GetWindowSize());

            // Header: title left, the small actions right-aligned.
            ImGui.TextColored(SpellIdeGold, (pinIndex >= 0 ? "PIN  " : "") + SpellIdeTitle(selection));
            bool soloable = selection.Kind == SpellIdeKind.Phase;
            float actionsW = ImGui.CalcTextSize(pinIndex >= 0 ? "unpin" : "pin").X + 16f * cs;
            if (soloable) actionsW += ImGui.CalcTextSize("solo").X + 20f * cs;
            if (pinIndex < 0) actionsW += ImGui.CalcTextSize("x").X + 20f * cs;
            ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(),
                ImGui.GetWindowContentRegionMax().X - actionsW));
            if (soloable)
            {
                if (ImGui.SmallButton("solo")) SoloCreatorSpellPhase("ws-" + selection.Path);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play only this phase, on repeat.");
                ImGui.SameLine();
            }
            if (pinIndex < 0)
            {
                if (ImGui.SmallButton("pin")) _spellIdePins.Add(selection);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Keep this open in its own window while you select something else.");
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) _spellIdeInspectorHidden = true;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide the inspector (any selection brings it back).");
            }
            else
            {
                if (ImGui.SmallButton("unpin")) _spellIdeUnpinRequest = pinIndex;
            }
            ImGui.Separator();

            // HorizontalScrollbar is load-bearing: a texture row is an unbreakable
            // SameLine chain and ImGui CLIPS rather than wraps.
            ImGui.BeginChild("##spell-ide-body", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
            ImGui.SetWindowFontScale(CreatorTextScale);
            ImGui.PushID(id);
            DrawSpellIdeBody(selection);
            ImGui.PopID();
            ImGui.SetWindowFontScale(1f);
            ImGui.EndChild();
        }
        EndSpellIdeWindow();
    }

    private void DrawSpellIdeBody(SpellIdeSelection selection)
    {
        float cs = CreatorUiScale;
        switch (selection.Kind)
        {
            case SpellIdeKind.Spell:
                _creatorResultsFractionOverride = 0.5f;
                DrawCreatorSpellPickerBody();
                _creatorResultsFractionOverride = null;
                return;
            case SpellIdeKind.Rules: DrawCreatorRulesBody(); return;
            case SpellIdeKind.Loop: DrawCreatorLoopBody(); return;
            case SpellIdeKind.Clock: DrawCreatorClockBody(); return;
            case SpellIdeKind.Stage: DrawCreatorStageBody(); return;
            case SpellIdeKind.Audio: DrawCreatorAudioBody(); return;
            case SpellIdeKind.Session: DrawCreatorSessionSection(); return;
            case SpellIdeKind.Composition: DrawCreatorCompositionBody(); return;
        }

        if (!TryGetSpellIdeModel(selection.Path, out CreatorModelDoc model))
        {
            ImGui.TextDisabled("This phase is gone - pick another in the tree.");
            return;
        }

        if (selection.Kind is SpellIdeKind.Mesh or SpellIdeKind.Ribbon)
        {
            DrawSpellIdeMeshRibbonBody(model, selection);
            return;
        }

        if (selection.Kind == SpellIdeKind.Phase)
        {
            bool dirty = DrawCreatorModelLook(model, cs);
            dirty |= DrawCreatorMeshRibbonLists(model);
            if (model.Bones.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"BONES ({model.Bones.Count})" + (model.BoneEdits.Count > 0 ? " *" : ""));
                CreatorHelp("The skeleton every emitter, ribbon and mesh vertex rides. Pose a bone " +
                    "here; re-parent emitters and ribbons in their own editors.");
                dirty |= DrawCreatorBonesBody(model);
            }
            ImGui.Spacing();
            ImGui.TextDisabled($"EMITTERS ({model.Emitters.Count()})");
            dirty |= DrawCreatorTransplantRow(model);
            CreatorHelp("Each emitter is one particle source inside this model. Pick one in the " +
                "tree (or click its gizmo in the world) to edit it; here you can only switch " +
                "them on and off. A bold gizmo is the selected emitter. Mesh layers and " +
                "ribbons above are the model's OTHER two kinds of drawing - a crescent that " +
                "survives every emitter being off lives there.");
            var (texByEmitter, slotByEmitter) = CreatorEmitterTextureMaps(model);
            foreach (EmitterSnapshot emitter in model.Emitters)
            {
                ImGui.PushID(emitter.Index);
                bool on = !model.DisabledEmitters.Contains(emitter.Index);
                if (ImGui.Checkbox("##on", ref on))
                {
                    if (on) model.DisabledEmitters.Remove(emitter.Index);
                    else model.DisabledEmitters.Add(emitter.Index);
                    dirty = true;
                }
                ImGui.SameLine();
                if (slotByEmitter.TryGetValue(emitter.Index, out int slot))
                {
                    ImGui.ColorButton("##swatch", CreatorSlotColor(slot),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                        new Vector2(10f * cs, 10f * cs));
                    ImGui.SameLine();
                }
                string text = $"e{emitter.Index}  {texByEmitter.GetValueOrDefault(emitter.Index, "no tex")}" +
                              $", blend {emitter.BlendMode}, shape {emitter.EmitterType}, bone {emitter.Bone}" +
                              (emitter.Index >= model.OriginalEmitterCount ? "  [added]" : "");
                if (ImGui.Selectable(text, false))
                    SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Emitter, model.Path, emitter.Index));
                if (ImGui.IsItemHovered()) _creatorGizmoHover = (model.Path, emitter.Index);
                ImGui.PopID();
            }
            if (dirty) RebuildCreatorModel(model);
            return;
        }

        EmitterSnapshot? selected = model.Emitters.FirstOrDefault(e => e.Index == selection.Emitter);
        if (selected is null)
        {
            ImGui.TextDisabled("This emitter is gone - pick another in the tree.");
            return;
        }
        {
            var (texByEmitter, slotByEmitter) = CreatorEmitterTextureMaps(model);
            if (slotByEmitter.TryGetValue(selected.Index, out int slot))
            {
                ImGui.ColorButton("##swatch", CreatorSlotColor(slot),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                    new Vector2(12f * cs, 12f * cs));
                ImGui.SameLine();
            }
            ImGui.TextDisabled($"{texByEmitter.GetValueOrDefault(selected.Index, "no tex")}, " +
                               $"blend {selected.BlendMode}, shape {selected.EmitterType}, bone {selected.Bone}" +
                               (selected.Index >= model.OriginalEmitterCount ? "  [added]" : "") +
                               (model.DisabledEmitters.Contains(selected.Index) ? "  [OFF]" : ""));
            ImGui.PushID(selected.Index);
            CreatorEmitterEdit verdict = DrawCreatorEmitterBody(model, selected, slotByEmitter, cs);
            ImGui.PopID();
            if (verdict == CreatorEmitterEdit.Removed)
            {
                RebuildCreatorModel(model);
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Phase, model.Path));
            }
            else if (verdict == CreatorEmitterEdit.Dirty) RebuildCreatorModel(model);
        }
    }

    // ── the timeline (paused only) ───────────────────────────────────────────

    private void DrawSpellIdeTimeline()
    {
        var io = ImGui.GetIO();
        float cs = CreatorUiScale;
        float estimate = _spellIdeTimelineHeight > 0f
            ? _spellIdeTimelineHeight
            : ImGui.GetTextLineHeightWithSpacing() * CreatorTextScale * 2.4f + 20f;
        if (BeginSpellIdeWindow("##spell-ide-timeline", new Vector2(0f, io.DisplaySize.Y - estimate),
                new Vector2(io.DisplaySize.X, 0f), ImGuiCond.Always,
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, 0.1f))
        {
            ImGui.TextColored(SpellIdeGold, "PAUSED");
            ImGui.SameLine();
            DrawCreatorScrub(MathF.Max(io.DisplaySize.X - 520f * cs, 120f * cs));
            ImGui.SameLine();
            if (CreatorButton("Resume", 72f * cs)) SetCreatorClockPaused(false);

            // The live phases, one line.
            if (_spellEffects is not null)
            {
                var parts = new List<string>();
                foreach (var live in _spellEffects.LiveInstances())
                {
                    if (parts.Count >= 6) { parts.Add("..."); break; }
                    double age = _creatorClockNow - (live.Missile ? live.LaunchedAt : live.Started);
                    parts.Add($"{live.Stage.ToLowerInvariant()} {Path.GetFileName(live.Path)} {age:0.00}s");
                }
                ImGui.TextDisabled(parts.Count == 0 ? "(nothing playing)" : string.Join("   ", parts));
            }
            // The dope sheet (P3.2): the selection's keyframes, editable while paused.
            DrawCreatorDopeSheet(io.DisplaySize.X - 24f * cs);
            _spellIdeTimelineHeight = ImGui.GetWindowSize().Y;
        }
        EndSpellIdeWindow();
    }

    // ── click a gizmo to select it ───────────────────────────────────────────

    /// <summary>The world-to-tree link: the label nearest the mouse (within a few pixels)
    /// is hover-highlighted; a left click selects it (an emitter or ribbon in the tree, a
    /// bone as the BONES section's pick) and a double click frames it. A handle under the
    /// mouse or in hand wins over every label. The label anchors were projected by the
    /// label pass; one frame of lag is invisible.</summary>
    private void SpellIdePickGizmo()
    {
        var io = ImGui.GetIO();
        _creatorGizmoHoverLabel = null;
        if (io.WantCaptureMouse || _window.MouseCaptured || _gizmoLabels.Count == 0 || CreatorHandlesActive) return;
        Vector2 mouse = io.MousePos;
        Vector2 display = io.DisplaySize;
        const float pickPixels = 18f;
        float best = pickPixels * pickPixels;
        GizmoLabel? hit = null;
        foreach (GizmoLabel label in _gizmoLabels)
        {
            if (!_window.Camera.TryWorldToScreen(label.World, display, out Vector2 pixel)) continue;
            float d = Vector2.DistanceSquared(pixel, mouse);
            // Bones sit under emitters more often than not: an emitter label wins a tie.
            if (label.Kind == GizmoLabelKind.Bone) d += 9f;
            if (d < best)
            {
                best = d;
                hit = label;
            }
        }
        if (hit is not { } picked) return;
        if (picked.Kind == GizmoLabelKind.Emitter) _creatorGizmoHover = (picked.ModelPath, picked.Index);
        else _creatorGizmoHoverLabel = (picked.ModelPath, picked.Kind, picked.Index);

        bool doubleClick = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (!doubleClick && !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        switch (picked.Kind)
        {
            case GizmoLabelKind.Emitter:
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Emitter, picked.ModelPath, picked.Index));
                break;
            case GizmoLabelKind.Ribbon:
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Ribbon, picked.ModelPath, picked.Index));
                break;
            case GizmoLabelKind.Bone:
                _creatorBonePick[picked.ModelPath] = picked.Index;
                if (!_spellIdeSelection.IsPhaseOf(picked.ModelPath) || _spellIdeSelection.Kind != SpellIdeKind.Phase)
                    SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Phase, picked.ModelPath));
                break;
        }
        if (doubleClick) FrameCreatorPoint(picked.World);
    }
}
