using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator-mode HUD: a top-left row of red glue buttons opening skinned panels.
// Character (race/sex/appearance), Gear (tier sets + item search), Teleport
// (preset locations + world map), Target (creature browser + spawns),
// Spells (the spell workshop).
//
// The bar is its own small auto-sized ImGui window (NOT full-screen - a
// full-screen invisible window would steal the camera's mouse input).
//
// PANELS ARE BUILT FROM SECTIONS. Every panel registers its drill-down groups
// as (panel, id, label, body) each frame; the panel window renders them in a
// user-arranged order (drag a header onto another to reorder), and any section
// can be POPPED OUT into its own floating window (the header's corner button;
// the popped window's X docks it back). Order + popped state persist in
// GameSettings.Creator. All windows are freely resizable by their edges and
// corners; ImGui's own ini file remembers each window's rect.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum CreatorPanel { None, Character, Gear, Teleport, Target, Spells, XRay, World }
    private CreatorPanel _creatorPanel;

    // Character look state (defaults mirror the offline test character).
    private byte _creatorRace = 1;      // ChrRaces id, Human
    private byte _creatorSex;           // 0 male, 1 female
    private readonly int[] _creatorDials = new int[5];   // skin, face, hairStyle, hairColor, facialHair
    private CharCreateCatalog? _creatorCatalog;
    private bool _creatorCatalogTried;

    // Gear state: normalized slot key -> worn piece. Seeded from Battlegear of Might.
    private readonly record struct CreatorPiece(string Name, uint DisplayId, int InventoryType);
    private Dictionary<int, CreatorPiece>? _creatorEquip;
    private int _creatorClassIndex;     // index into CreatorTierSets.Classes
    private CreatorItemTable? _creatorItems;
    private bool _creatorItemsTried;
    private bool _creatorSearchOpen;
    private readonly byte[] _creatorSearchBuf = new byte[64];
    private int _creatorSearchSlot = -1;   // inventoryType filter, -1 = any
    private List<CreatorItemTable.Item>? _creatorSearchResults;

    private static readonly (string Label, byte Race)[] CreatorRaces =
    {
        ("Human", 1), ("Dwarf", 3), ("Night Elf", 4), ("Gnome", 7),
        ("Orc", 2), ("Undead", 5), ("Tauren", 6), ("Troll", 8),
    };

    private static readonly (string Label, int InvType)[] CreatorSearchSlots =
    {
        ("Any slot", -1), ("Head", 1), ("Shoulder", 3), ("Chest", 5), ("Robe", 20),
        ("Shirt", 4), ("Tabard", 19), ("Back", 16), ("Waist", 6), ("Legs", 7),
        ("Feet", 8), ("Wrist", 9), ("Hands", 10), ("Main Hand", 21), ("One-Hand", 13),
        ("Two-Hand", 17), ("Off Hand", 22), ("Held", 23), ("Shield", 14), ("Ranged", 15),
    };

    private static readonly Vector4[] CreatorQualityColors =
    {
        new(0.62f, 0.62f, 0.62f, 1f),   // poor
        new(1f, 1f, 1f, 1f),            // common
        new(0.12f, 1f, 0f, 1f),         // uncommon
        new(0f, 0.44f, 0.87f, 1f),      // rare
        new(0.64f, 0.21f, 0.93f, 1f),   // epic
        new(1f, 0.50f, 0f, 1f),         // legendary
        new(0.90f, 0.80f, 0.50f, 1f),   // artifact
    };

    // ── per-modal layout dials ───────────────────────────────────────────────
    // Every window has a gear button opening ITS OWN dial set (PanelTuning in
    // the settings), multiplying on top of the shared modal dials - so each
    // modal can be dialed into its own "perfect" layout independently.

    /// <summary>The window whose per-modal dials apply to widgets drawn right now.</summary>
    private string? _activePanelTune;

    /// <summary>The window whose layout popup (the gear button) is open.</summary>
    private string? _openPanelTuneId;

    private static readonly GameSettings.PanelTuneSetting CreatorNeutralTune = new();

    private GameSettings.PanelTuneSetting ActivePanelTune =>
        _activePanelTune is { } id &&
        Settings.Creator.PanelTuning.TryGetValue(id, out var tune) ? tune : CreatorNeutralTune;

    private GameSettings.PanelTuneSetting PanelTuneFor(string id)
    {
        if (!Settings.Creator.PanelTuning.TryGetValue(id, out var tune))
            Settings.Creator.PanelTuning[id] = tune = new GameSettings.PanelTuneSetting();
        return tune;
    }

    // Which field kinds each window actually drew last frame, so the layout
    // popup only offers dials for what exists in that window.
    private readonly Dictionary<string, HashSet<string>> _panelFieldKinds = new();

    private void NotePanelField(string kind)
    {
        if (_activePanelTune is not { } id) return;
        if (!_panelFieldKinds.TryGetValue(id, out var kinds))
            _panelFieldKinds[id] = kinds = new HashSet<string>();
        kinds.Add(kind);
    }

    /// <summary>MODAL widget/panel size multiplier: the shared dial times the
    /// active window's own Widget dial, times the workspace deck's
    /// display-derived boost (1 everywhere else).</summary>
    private float CreatorUiScale => Math.Clamp(Settings.Creator.UiScale, 0.6f, 2.5f)
        * Math.Clamp(ActivePanelTune.Widget, 0.5f, 2.5f) * _creatorScaleBoost;

    /// <summary>MODAL text-only size multiplier: shared dial times the window's own,
    /// times the workspace deck's display-derived boost (1 everywhere else).</summary>
    private float CreatorTextScale => Math.Clamp(Settings.Creator.TextScale, 0.6f, 2.5f)
        * Math.Clamp(ActivePanelTune.Text, 0.5f, 2.5f) * _creatorScaleBoost;

    /// <summary>The active window's red-button size dial.</summary>
    private float CreatorButtonMul => Math.Clamp(ActivePanelTune.Button, 0.5f, 2.5f);

    /// <summary>The active window's header/+- icon size dial.</summary>
    private float CreatorIconMul => Math.Clamp(ActivePanelTune.Icon, 0.5f, 3f);

    /// <summary>Red-button height under the active window's dials.</summary>
    private float CreatorButtonHeight => CreatorRowHeight * CreatorButtonMul;

    /// <summary>Top-bar button size multiplier - independent of the modal dials.</summary>
    private float CreatorBarScale => Math.Clamp(Settings.Creator.BarScale, 0.6f, 2.5f);

    /// <summary>Top-bar caption size multiplier - independent of the modal dials.</summary>
    private float CreatorBarTextScale => Math.Clamp(Settings.Creator.BarTextScale, 0.6f, 2.5f);

    private bool _creatorUiOptionsOpen;

    /// <summary>While > 0, every creator window re-asserts its default rect (the
    /// "Reset window layout" button). Decremented once per frame.</summary>
    private int _creatorLayoutResetFrames;

    // ── section registry ─────────────────────────────────────────────────────
    // Rebuilt every frame (closures are cheap); the registry is what lets a
    // popped-out section keep drawing after its parent panel is closed.

    private readonly record struct CreatorSectionDef(
        string Panel, string Id, string Label, bool DefaultOpen, Action Body);

    private readonly List<CreatorSectionDef> _creatorSectionDefs = new();

    private void CreatorSection(string panel, string id, string label, bool defaultOpen, Action body)
        => _creatorSectionDefs.Add(new CreatorSectionDef(panel, id, label, defaultOpen, body));

    /// <summary>The creator-mode overlay: menu bar, the open panel, popped-out sections.</summary>
    private void DrawCreatorHud()
    {
        _creatorSectionDefs.Clear();
        _creatorGizmoHover = null;
        _creatorGizmoHoverLabel = null;
        RegisterCreatorCharacterSections();
        RegisterCreatorGearSections();
        RegisterCreatorTeleportSections();
        RegisterCreatorTargetSections();
        RegisterCreatorSpellsSections();
        RegisterCreatorXraySections();
        RegisterCreatorWorldSections();

        if (Settings.Creator.Workspace)
        {
            // The docked layout: rails + bottom deck instead of floating panels.
            DrawCreatorWorkspace();
            if (_creatorUiOptionsOpen) DrawCreatorUiOptions();
        }
        else
        {
            DrawCreatorMenuBar();
            switch (_creatorPanel)
            {
                case CreatorPanel.Character: DrawCreatorSectionPanel("Character", "Character", 500f, 560f); break;
                case CreatorPanel.Gear: DrawCreatorSectionPanel("Gear", "Gear", 400f, 480f); break;
                case CreatorPanel.Teleport: DrawCreatorSectionPanel("Teleport", "Teleport", 480f, 560f); break;
                case CreatorPanel.Target: DrawCreatorSectionPanel("Target", "Target", 430f, 560f); break;
                case CreatorPanel.Spells: DrawCreatorSectionPanel("Spells", "Spell Workshop", 500f, 640f); break;
                case CreatorPanel.XRay: DrawCreatorSectionPanel("XRay", "Collision X-Ray", 460f, 560f); break;
                case CreatorPanel.World: DrawCreatorSectionPanel("World", "World Builder", 470f, 680f); break;
            }
        }
        UpdateWorldBuilder();
        DrawCreatorGizmoLabels();
        DrawPoppedCreatorSections();
        DrawMountToolkit();
        DrawMountKitBar();
        if (_creatorSearchOpen) DrawCreatorItemSearch();
        DrawCreatorTextureSwapPicker();
        DrawCreatorAudioFilePicker();
        DrawCreatorFilePicker();
        DrawCreatorModelPicker();
        DrawCreatorAnimationPicker();
        DrawCreatorPanelTunePopup();
        if (_creatorLayoutResetFrames > 0) _creatorLayoutResetFrames--;

        // Escape dismisses the transient windows, innermost first (never the
        // panels - Escape in the world keeps its normal meaning once these are
        // gone). Skipped while typing so a text field's own Escape still works.
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.GetIO().WantTextInput)
        {
            if (_creatorFilePickerTitle is not null) CloseCreatorFilePicker();
            else if (_modelPickerTarget is not null) _modelPickerTarget = null;
            else if (_texSwapTarget is not null) _texSwapTarget = null;
            else if (_openPanelTuneId is not null)
            {
                _openPanelTuneId = null;
                _creatorEditLayoutPanel = null;
            }
            else if (_creatorSearchOpen) _creatorSearchOpen = false;
            else if (_creatorUiOptionsOpen) _creatorUiOptionsOpen = false;
            // NOTE: the Spell Workshop's IDE layout deliberately has NO rung here.
            // This ladder does not consume the key, so the press falls through to the
            // vanilla game menu - one Escape would both open the menu and silently
            // rebuild the workshop behind it, and a second would not undo it. The
            // layout is left via the Spell icon, the header's "deck" button, or the
            // Creator UI dials checkbox.
        }
    }

    /// <summary>One shared control width so sliders and inputs line up into a
    /// clean column whatever the window width: about half the row, clamped.</summary>
    private float CreatorControlWidth =>
        Math.Clamp(ImGui.GetContentRegionAvail().X * 0.52f,
            140f * CreatorUiScale, 300f * CreatorUiScale);

    // ── search result lists ──────────────────────────────────────────────────
    // One consistent treatment for every results box (spells, creatures, items,
    // splice sources): near-opaque dark plate so rows read against any world,
    // comfortable row height, and a height that GROWS with the result count up
    // to a fraction of the window - resizing the window resizes the list.

    private float CreatorResultRowHeight => ImGui.GetTextLineHeight() + 8f * CreatorUiScale;

    private bool BeginCreatorResults(string id, int itemCount, float maxFraction = 0.45f)
    {
        float cs = CreatorUiScale;
        float rowStride = CreatorResultRowHeight + ImGui.GetStyle().ItemSpacing.Y;
        float desired = MathF.Max(itemCount, 1) * rowStride + 14f * cs;
        float cap = MathF.Max(
            ImGui.GetContentRegionAvail().Y * (_creatorResultsFractionOverride ?? maxFraction),
            150f * cs);
        float height = MathF.Min(desired, cap);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.07f, 0.07f, 0.08f, 0.97f));
        return ImGui.BeginChild(id, new Vector2(0f, height), true);
    }

    private void EndCreatorResults()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>A comfortable, full-width result row.</summary>
    private bool CreatorResultRow(string label, bool selected = false)
        => ImGui.Selectable(label, selected, ImGuiSelectableFlags.None,
            new Vector2(0f, CreatorResultRowHeight));

    // ── deferred scale dials ─────────────────────────────────────────────────
    // A creator scale dial shows its value live but COMMITS only on release.
    // These dials resize the very windows that host them (window padding, the
    // per-frame minimum-size constraints, the on-screen clamp), so writing
    // through while the knob was held put the slider's own geometry in a
    // feedback loop and every creator window lurched around mid-drag.

    private string? _creatorHeldDialId;
    private float _creatorHeldDialValue;

    /// <summary>Set around a results list to override BeginCreatorResults' default
    /// share of the region - the Spell Workshop's IDE layout hosts the picker in
    /// a FULL-HEIGHT pane, where 45% would crowd out every phase row below it.</summary>
    private float? _creatorResultsFractionOverride;

    private bool CreatorDeferredDial(string id, string label, float lo, float hi,
        Func<float> get, Action<float> set, float itemWidth, string fmt = "%.2fx")
    {
        float value = _creatorHeldDialId == id ? _creatorHeldDialValue : get();
        ImGui.SetNextItemWidth(itemWidth);
        ImGui.SliderFloat(label, ref value, lo, hi, fmt);
        if (ImGui.IsItemActive())
        {
            _creatorHeldDialId = id;
            _creatorHeldDialValue = value;
            return false;
        }
        if (_creatorHeldDialId != id) return false;
        _creatorHeldDialId = null;
        if (MathF.Abs(value - get()) < 0.0005f) return false;
        set(value);
        return true;
    }

    /// <summary>The per-window layout popup opened by a window's gear button:
    /// dials for exactly the field kinds that window draws, persisted per window
    /// on top of the shared modal dials.</summary>
    private void DrawCreatorPanelTunePopup()
    {
        if (_openPanelTuneId is not { } id) return;
        _activePanelTune = null;   // the popup itself follows only the shared dials
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(300f * s, 120f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(340f * cs, 430f * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(250f * cs, 190f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-panel-tune", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome($"Layout: {id}")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();
            var tune = PanelTuneFor(id);
            var kinds = _panelFieldKinds.GetValueOrDefault(id);
            bool save = false;

            bool Dial(string label, Func<float> get, Action<float> set, float max = 2.5f)
                => CreatorDeferredDial($"tune/{id}/{label}", label, 0.5f, max,
                    get, set, 170f * cs);

            ImGui.TextDisabled("SIZES");
            save |= Dial("Text size", () => tune.Text, v => tune.Text = v);
            save |= Dial("Widget size", () => tune.Widget, v => tune.Widget = v);
            if (kinds?.Contains("buttons") == true)
                save |= Dial("Button size", () => tune.Button, v => tune.Button = v);
            if (kinds?.Contains("headers") == true)
                save |= Dial("Header / +- icon size", () => tune.Icon, v => tune.Icon = v, 3f);
            save |= Dial("Row spacing", () => tune.Spacing, v => tune.Spacing = v);
            if (CreatorButton("Reset sizes"))
            {
                tune.Text = tune.Widget = tune.Button = tune.Icon = tune.Spacing = 1f;
                save = true;
            }

            if (kinds?.Contains("movable-buttons") == true)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("PLACEMENT");
                bool editing = _creatorEditLayoutPanel == id;
                if (CreatorButton(editing ? "Done moving" : "Move buttons"))
                    _creatorEditLayoutPanel = editing ? null : id;
                ImGui.SameLine();
                if (CreatorButton("Reset positions"))
                {
                    Settings.Creator.WidgetOffsets.Remove(id);
                    save = true;
                }
                ImGui.TextWrapped(editing
                    ? "Drag any green-outlined button in the window to place it. " +
                      "Buttons do not fire while moving. Click Done moving to finish."
                    : "Move buttons puts this window in edit mode: drag its buttons " +
                      "wherever you think they should be. Positions persist.");
            }

            ImGui.TextDisabled("Applies to this window only, on top of the shared UI dials.");
            if (save) SettingsFile?.Save();
            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        if (!open)
        {
            _openPanelTuneId = null;
            if (_creatorEditLayoutPanel == id) _creatorEditLayoutPanel = null;
        }
    }

    private void DrawCreatorMenuBar()
    {
        // The bar sizes with its OWN dials (BarScale / BarTextScale), not the
        // modal dials - so the modals can be dialed in without moving the bar.
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorBarScale;
        ImGui.SetNextWindowPos(new Vector2(8f * s, 6f * s), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("##creator-bar", flags)) { ImGui.End(); return; }

        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        var size = new Vector2(118f * s, 30f * s);
        // The explicit caption size replicates GlueButton's auto rule (height x
        // ratio, floored at the base font) so BarTextScale 1.0 is bit-identical
        // to the auto caption, and other values scale from there.
        float captionPx = MathF.Max(size.Y * GlueTune.CaptionSizeRatio, ImGui.GetFontSize())
                          * CreatorBarTextScale;

        CreatorBarButton("Character", CreatorPanel.Character, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("Gear", CreatorPanel.Gear, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("Teleport", CreatorPanel.Teleport, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("Target", CreatorPanel.Target, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("Spells", CreatorPanel.Spells, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("X-Ray", CreatorPanel.XRay, size, captionPx);
        ImGui.SameLine();
        CreatorBarButton("World", CreatorPanel.World, size, captionPx);

        // The Encounter Lab has its own lifetime (Ctrl+E, draws in live mode
        // too), so its button toggles the Lab directly instead of being a panel.
        ImGui.SameLine();
        bool encClicked = _skin?.GlueButton("Encounter", size, true, captionPx)
                          ?? ImGui.Button("Encounter", size);
        if (_encounterLabOpen)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                VanillaGold, 3f, ImDrawFlags.None, 2f);
        if (encClicked) ToggleEncounterLab();

        // The UI-options toggle: layout/scale dials live in their own panel.
        ImGui.SameLine();
        if (_skin?.GlueButton("UI", new Vector2(46f * s, 30f * s), true, captionPx) ?? ImGui.SmallButton("UI"))
            _creatorUiOptionsOpen = !_creatorUiOptionsOpen;

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();

        DrawCreatorFreeViewButton();
        if (_creatorUiOptionsOpen) DrawCreatorUiOptions();
    }

    /// <summary>Top-right corner: the free-view toggle as a button, so Ctrl+F is
    /// discoverable. Gold rim while the sky rig is live, same as the bar.</summary>
    private void DrawCreatorFreeViewButton()
    {
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorBarScale;
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetIO().DisplaySize.X - 8f * s, 6f * s),
            ImGuiCond.Always, new Vector2(1f, 0f));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("##creator-freeview", flags)) { ImGui.End(); return; }

        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        var size = new Vector2(100f * s, 30f * s);
        float captionPx = MathF.Max(size.Y * GlueTune.CaptionSizeRatio, ImGui.GetFontSize())
                          * CreatorBarTextScale;
        bool clicked = _skin?.GlueButton("Free view", size, true, captionPx)
                       ?? ImGui.Button("Free view", size);
        if (_freeView)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                VanillaGold, 3f, ImDrawFlags.None, 2f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_freeView ? "Return to your character (Ctrl+F)"
                                       : "Rise into the command view (Ctrl+F)");
        if (clicked) ToggleFreeView();
        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    /// <summary>Creator UI options: everything that sizes or arranges the creator
    /// windows - widget/text scale, panel opacity, padding and spacing dials, and
    /// the layout reset. Bar and opacity dials are live while dragging; the
    /// geometry dials show their value live but apply on release
    /// (see <see cref="CreatorDeferredDial"/>).</summary>
    private void DrawCreatorUiOptions()
    {
        _activePanelTune = "Creator UI";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(620f * s, 64f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(340f * cs, 420f * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260f * cs, 200f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-ui-options", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Creator UI", "Creator UI")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();
            var creator = Settings.Creator;
            bool save = false;

            ImGui.TextDisabled("TOP BAR (the 6 buttons)");
            float bar = creator.BarScale;
            ImGui.SetNextItemWidth(180f * cs);
            if (ImGui.SliderFloat("Bar button size", ref bar, 0.6f, 2f, "%.2fx")) creator.BarScale = bar;
            save |= ImGui.IsItemDeactivatedAfterEdit();

            float barText = creator.BarTextScale;
            ImGui.SetNextItemWidth(180f * cs);
            if (ImGui.SliderFloat("Bar text size", ref barText, 0.6f, 2f, "%.2fx")) creator.BarTextScale = barText;
            save |= ImGui.IsItemDeactivatedAfterEdit();

            ImGui.Spacing();
            ImGui.TextDisabled("MODALS (all panels share these)");
            save |= CreatorDeferredDial("creator-ui/widget", "Widget scale", 0.6f, 2f,
                () => creator.UiScale, v => creator.UiScale = v, 180f * cs);
            save |= CreatorDeferredDial("creator-ui/text", "Text scale", 0.6f, 2f,
                () => creator.TextScale, v => creator.TextScale = v, 180f * cs);

            float alpha = creator.PanelAlpha;
            ImGui.SetNextItemWidth(180f * cs);
            if (ImGui.SliderFloat("Background opacity", ref alpha, 0.2f, 1f, "%.2f")) creator.PanelAlpha = alpha;
            save |= ImGui.IsItemDeactivatedAfterEdit();

            save |= CreatorDeferredDial("creator-ui/padding", "Padding", 0.4f, 2f,
                () => creator.PaddingScale, v => creator.PaddingScale = v, 180f * cs);
            save |= CreatorDeferredDial("creator-ui/spacing", "Row spacing", 0.4f, 2f,
                () => creator.SpacingScale, v => creator.SpacingScale = v, 180f * cs);

            ImGui.Spacing();
            ImGui.TextDisabled("LAYOUT");
            ImGui.TextWrapped("Drag any panel edge or corner to resize it; sizes are remembered. " +
                              "Drag a section header onto another to reorder. The corner button on a " +
                              "header pops that section out into its own window; the popped window's " +
                              "close button docks it back.");
            if (CreatorButton("Reset dials"))
            {
                creator.BarScale = 1f;
                creator.BarTextScale = 1f;
                creator.UiScale = 1f;
                creator.TextScale = 1f;
                creator.PanelAlpha = 0.62f;
                creator.PaddingScale = 1f;
                creator.SpacingScale = 1f;
                save = true;
            }
            ImGui.SameLine();
            if (CreatorButton("Reset window layout"))
            {
                creator.SectionOrder.Clear();
                creator.PoppedSections.Clear();
                _creatorLayoutResetFrames = 2;
                save = true;
            }

            bool workspace = creator.Workspace;
            if (ImGui.Checkbox("Docked workspace layout (rails + bottom deck)", ref workspace))
            {
                creator.Workspace = workspace;
                save = true;
            }
            ImGui.TextDisabled(workspace
                ? "Panels dock into the bottom deck; the right rail's Wins button also returns here."
                : "Classic floating windows. Check to try the docked layout.");

            if (workspace)
            {
                bool focus = creator.SpellFocus;
                if (ImGui.Checkbox("Spell Workshop IDE layout", ref focus))
                {
                    creator.SpellFocus = focus;
                    if (focus) _spellFocusSuppressed = false;
                    save = true;
                }
                ImGui.TextDisabled(focus
                    ? "The Spell Workshop becomes an IDE: a top-left strip, a collapsible " +
                      "tree, one inspector for the selection and a timeline while paused - " +
                      "the rest of the screen is the stage."
                    : "The Spell Workshop uses the bottom deck like every other panel.");
            }

            if (save) SettingsFile?.Save();
            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
        if (!open) _creatorUiOptionsOpen = false;
    }

    private void CreatorBarButton(string label, CreatorPanel panel, Vector2 size, float captionPx)
    {
        bool clicked = _skin?.GlueButton(label, size, true, captionPx) ?? ImGui.Button(label, size);
        if (_creatorPanel == panel)   // the open panel's button wears the gold rim
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                VanillaGold, 3f, ImDrawFlags.None, 2f);
        if (clicked) _creatorPanel = _creatorPanel == panel ? CreatorPanel.None : panel;
    }

    // ── panel chrome ─────────────────────────────────────────────────────────
    // WowSkin.PushStyle deliberately leaves WindowBg TRANSPARENT (the settings
    // modal paints its own dialog art). Creator panels must therefore paint
    // their own chrome or the world bleeds straight through the widgets:
    // an almost-opaque warm fill + the riveted UI-DialogBox nine-slice border,
    // plus opaque dark title bars (the skin never styles ImGui's title bar).

    private int _creatorStyleColors;
    private int _creatorStyleVars;

    private void PushCreatorStyle()
    {
        _skin?.PushStyle();
        _creatorStyleColors = 0;
        _creatorStyleVars = 0;
        void C(ImGuiCol which, Vector4 color) { ImGui.PushStyleColor(which, color); _creatorStyleColors++; }
        void V(ImGuiStyleVar which, Vector2 value) { ImGui.PushStyleVar(which, value); _creatorStyleVars++; }

        C(ImGuiCol.Text, new Vector4(0.96f, 0.93f, 0.86f, 1f));
        C(ImGuiCol.TextDisabled, new Vector4(0.80f, 0.68f, 0.42f, 1f));   // section headers read gold, not grey
        C(ImGuiCol.FrameBg, new Vector4(0.13f, 0.13f, 0.14f, 0.90f));     // grey input/slider wells
        C(ImGuiCol.ChildBg, new Vector4(0.06f, 0.06f, 0.07f, 0.45f));

        // Breathing room, following the widget + padding dials: the skin's
        // paddings are tuned for the dense settings modal and read cramped here.
        float ps = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;
        float padMul = Math.Clamp(Settings.Creator.PaddingScale, 0.4f, 2f);
        float spaceMul = Math.Clamp(Settings.Creator.SpacingScale, 0.4f, 2f)
                         * Math.Clamp(ActivePanelTune.Spacing, 0.5f, 2.5f);
        V(ImGuiStyleVar.WindowPadding, new Vector2(20f, 18f) * ps * padMul);
        V(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 9f) * ps * spaceMul);
        V(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f) * ps);
    }

    private void PopCreatorStyle()
    {
        if (_creatorStyleVars > 0) { ImGui.PopStyleVar(_creatorStyleVars); _creatorStyleVars = 0; }
        if (_creatorStyleColors > 0) { ImGui.PopStyleColor(_creatorStyleColors); _creatorStyleColors = 0; }
        _skin?.PopStyle();
    }

    /// <summary>
    /// The 1.12 dialog chrome, replacing ImGui's window decoration entirely:
    /// UI-DialogBox border + background over a near-opaque fill, the
    /// UI-DialogBox-Header plaque hanging above the frame with the title, and
    /// the round UI-Panel-MinimizeButton close. Returns true when close was
    /// clicked. Call right after a successful Begin on a NoTitleBar window.
    /// </summary>
    private bool DrawCreatorPanelChrome(string title, string? tuneId = null)
    {
        var dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        float ps = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;

        // The header plaque hangs ABOVE the frame (GameMenuFrame.xml numbers);
        // the window clip rect would eat it, so override while painting chrome.
        dl.PushClipRect(min - new Vector2(64f * ps, 64f * ps),
                        max + new Vector2(64f * ps, 64f * ps), false);
        // Semi-grey fill like the in-game menus (opacity is a UI-options dial),
        // INSET from the window rect so nothing bleeds past the border art's
        // rounded corners.
        var fillInset = new Vector2(5f, 5f) * ps;
        float alpha = Math.Clamp(Settings.Creator.PanelAlpha, 0.2f, 1f);
        dl.AddRectFilled(min + fillInset, max - fillInset,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.11f, 0.11f, 0.12f, alpha)));
        if (_skin is not null)
        {
            float saved = _skin.Scale;
            _skin.Scale = ps;
            _skin.DrawBackdrop(dl, min, max, WowSkin.Dialog);
            _skin.HeaderPlaque(dl, min, max.X - min.X, title);
            _skin.Scale = saved;
        }
        dl.PopClipRect();

        // Round red close button, top-right on the frame - vanilla's 32px art,
        // full-size so it is comfortably clickable.
        Vector2 keep = ImGui.GetCursorPos();
        var closeSize = new Vector2(38f, 38f) * ps;
        var closePos = new Vector2(max.X - closeSize.X - 1f * ps, min.Y + 1f * ps);
        bool closed = DrawImageButtonClicked(dl, $"##creator-close-{title}", closePos, closeSize,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");

        // The gear: this window's own layout dials, tucked left of the close
        // button. Deliberately NOT scaled by the creator dials (it is the one
        // knob that must stay reachable however the window is tuned): it follows
        // the window's width a little and stays inside the top "navbar" strip.
        if (tuneId is not null)
        {
            float baseS = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f);
            float gear = Math.Clamp((max.X - min.X) * 0.030f, 11f * baseS, 16f * baseS);
            float closeCentreY = min.Y + 1f * ps + closeSize.Y * 0.5f;
            var gearPos = new Vector2(closePos.X - gear - 6f * baseS, closeCentreY - gear * 0.5f);
            ImGui.SetCursorScreenPos(gearPos);
            bool gearClicked = ImGui.InvisibleButton($"##creator-gear-{title}", new Vector2(gear, gear));
            bool gearHovered = ImGui.IsItemHovered();
            uint col = gearHovered || _openPanelTuneId == tuneId
                ? 0xffffffff : VanillaGold;
            Vector2 centre = gearPos + new Vector2(gear, gear) * 0.5f;
            float rim = gear * 0.32f;
            float stroke = MathF.Max(1.2f, 1.5f * baseS);
            dl.AddCircle(centre, rim, col, 12, stroke);
            dl.AddCircleFilled(centre, gear * 0.10f, col);
            for (int spoke = 0; spoke < 8; spoke++)
            {
                float a = spoke * MathF.PI / 4f;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                dl.AddLine(centre + dir * rim, centre + dir * (rim + gear * 0.16f), col, stroke);
            }
            if (gearHovered) ImGui.SetTooltip("Layout dials for this window");
            if (gearClicked) _openPanelTuneId = _openPanelTuneId == tuneId ? null : tuneId;
        }
        ImGui.SetCursorPos(keep);

        // Clear the plaque's visible plate before content begins.
        ImGui.Dummy(new Vector2(1f, 16f * ps));
        return closed;
    }

    /// <summary>
    /// Begin a creator panel window under the bar, dressed in the real 1.12
    /// dialog chrome. Freely resizable by edges/corners (the rect persists via
    /// ImGui's ini); the default rect only applies on first use or layout reset.
    /// Returns false when closed.
    /// </summary>
    /// <summary>Every creator window's content flags: the OUTER window never
    /// scrolls - content lives in the inset scroll region (BeginCreatorContent),
    /// so rows clip inside the frame and the scrollbar sits inside the border.</summary>
    private const ImGuiWindowFlags CreatorChromeFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>Keep a dragged window reachable: at least a sliver on screen
    /// horizontally, and the header strip always below the top edge.</summary>
    private void ClampCreatorWindowOnScreen()
    {
        var disp = ImGui.GetIO().DisplaySize;
        const float keep = 60f;
        // Minimized / mid-resize the display reports tiny or zero - clamping
        // then would invert the range (min > max threw at boot) and there is
        // nothing sensible to clamp against anyway.
        if (disp.X < keep * 3f || disp.Y < keep * 3f) return;
        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();
        float minX = keep - size.X, maxX = disp.X - keep;
        float minY = 24f, maxY = disp.Y - keep;
        if (maxX < minX || maxY < minY) return;
        float x = Math.Clamp(pos.X, minX, maxX);
        float y = Math.Clamp(pos.Y, minY, maxY);
        if (x != pos.X || y != pos.Y) ImGui.SetWindowPos(new Vector2(x, y));
    }

    /// <summary>The inset scroll region every chrome window's content lives in.
    /// Ends above the border ring; fonts re-applied (a child is its own window).</summary>
    private void BeginCreatorContent()
    {
        float ps = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;
        ImGui.BeginChild("##creator-content", new Vector2(0f, -10f * ps));
        ImGui.SetWindowFontScale(CreatorTextScale);
    }

    private void EndCreatorContent()
    {
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
    }

    private bool BeginCreatorPanel(string title, string tuneId, float defaultW, float defaultH)
    {
        _activePanelTune = tuneId;
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(8f * s, 64f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(defaultW * cs, defaultH * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(250f * cs, 170f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        if (!ImGui.Begin($"###creator-{title}", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            return false;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome(title, tuneId)) _creatorPanel = CreatorPanel.None;
        ImGui.SetWindowFontScale(CreatorTextScale);
        return true;
    }

    private void EndCreatorPanel()
    {
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
    }

    // ── section rendering ────────────────────────────────────────────────────

    /// <summary>A panel window whose content is its registered sections, drawn in
    /// the user-arranged order with reorder + pop-out affordances.</summary>
    /// <summary>One line of live context per panel, shown in its fixed toolbar.</summary>
    private string CreatorPanelStatus(string panelId) => panelId switch
    {
        "Teleport" => _travelStatus ?? "",
        "Spells" => _creatorSpell is { } doc ? $"{doc.Info.Id}  {doc.Info.Name}" : "no spell selected",
        "Target" => _creatorSpawns.Count > 0 ? $"{_creatorSpawns.Count} spawned" : "",
        "World" => WorldBuilderStatus(),
        "XRay" => _xrayActive
            ? (_xrayWorld is { } w ? $"active, {w.TriangleCount:N0} triangles" : "active, building...")
            : "off",
        _ => "",
    };

    private void SetAllPanelSections(string panelId, bool open)
    {
        foreach (var def in _creatorSectionDefs)
            if (def.Panel == panelId)
                Settings.Creator.SectionOpen[$"{panelId}/{def.Id}"] = open;
        SettingsFile?.Save();
    }

    /// <summary>The fixed strip under the plaque: live status on the left,
    /// expand/collapse-all on the right. Never scrolls with the content.</summary>
    private void DrawCreatorPanelToolbar(string panelId)
    {
        float cs = CreatorUiScale;
        float avail = ImGui.GetContentRegionAvail().X;
        float expandW = ImGui.CalcTextSize("Expand all").X + 14f * cs;
        float collapseW = ImGui.CalcTextSize("Collapse all").X + 14f * cs;
        float buttons = expandW + collapseW + 10f * cs;

        string status = CreatorPanelStatus(panelId);
        if (status.Length > 0)
        {
            float maxW = MathF.Max(avail - buttons - 12f * cs, 40f);
            while (status.Length > 4 && ImGui.CalcTextSize(status).X > maxW)
                status = status[..^4] + "...";
            ImGui.TextDisabled(status);
            if (ImGui.IsItemHovered() && status.EndsWith("..."))
                ImGui.SetTooltip(CreatorPanelStatus(panelId));
            ImGui.SameLine(MathF.Max(avail - buttons, 0f));
        }
        else
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(avail - buttons, 0f));
        }
        if (ImGui.SmallButton("Expand all")) SetAllPanelSections(panelId, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Collapse all")) SetAllPanelSections(panelId, false);
        ImGui.Separator();
    }

    private void DrawCreatorSectionPanel(string panelId, string title, float defaultW, float defaultH)
    {
        if (!BeginCreatorPanel(title, panelId, defaultW, defaultH)) return;
        float cs = CreatorUiScale;

        if (_creatorEditLayoutPanel == panelId)
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.35f, 1f),
                "MOVE MODE - drag any outlined button to place it");
        DrawCreatorPanelToolbar(panelId);
        BeginCreatorContent();

        foreach (string id in OrderedSectionIds(panelId))
        {
            int at = _creatorSectionDefs.FindIndex(d => d.Panel == panelId && d.Id == id);
            if (at < 0) continue;
            var def = _creatorSectionDefs[at];
            if (IsSectionPopped(panelId, id))
            {
                if (CreatorPoppedPlaceholder(def)) TogglePoppedSection(panelId, id, false);
                continue;
            }
            bool openSection = CreatorSectionHeader(def);
            if (!openSection) continue;
            ImGui.PushID(id);
            ImGui.Indent(10f * cs);
            def.Body();
            ImGui.Unindent(10f * cs);
            ImGui.PopID();
            ImGui.Spacing();
        }

        EndCreatorContent();
        EndCreatorPanel();
    }

    /// <summary>Popped-out sections draw as their own chrome windows, panel open or
    /// not. The chrome X docks the section back into its panel.</summary>
    private void DrawPoppedCreatorSections()
    {
        if (Settings.Creator.PoppedSections.Count == 0) return;
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        int slot = 0;
        foreach (var def in _creatorSectionDefs.ToList())
        {
            if (!IsSectionPopped(def.Panel, def.Id)) continue;
            // The Spell Workshop's IDE layout IS the home for every one of its
            // sections and offers no tear-off corner, so a section popped in another
            // layout must not float over the model here - and must not draw twice,
            // which would run the model editor's rebuild against one doc in a single
            // frame. The popped state is kept, and returns with the deck.
            if (SpellFocusActive && def.Panel == "Spells") continue;
            string key = $"{def.Panel}/{def.Id}";
            _activePanelTune = def.Panel;   // popped windows follow their parent panel's dials
            var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
            // Cascade clear of whatever owns the left edge - the classic 340*s lands
            // UNDER the IDE's outliner.
            float popLeft = SpellFocusActive ? SpellIdeLeftInset + 20f * cs : 340f * s;
            ImGui.SetNextWindowPos(
                new Vector2(popLeft + 30f * slot * s, (100f + 30f * slot) * s), cond);
            ImGui.SetNextWindowSize(new Vector2(400f * cs, 340f * cs), cond);
            ImGui.SetNextWindowSizeConstraints(new Vector2(220f * cs, 140f * cs),
                new Vector2(float.MaxValue, float.MaxValue));
            slot++;
            PushCreatorStyle();
            bool open = true;
            if (ImGui.Begin($"###creator-pop-{key}", CreatorChromeFlags))
            {
                ClampCreatorWindowOnScreen();
                if (DrawCreatorPanelChrome(def.Label, def.Panel)) open = false;
                ImGui.SetWindowFontScale(CreatorTextScale);
                BeginCreatorContent();
                ImGui.PushID(key);
                def.Body();
                ImGui.PopID();
                EndCreatorContent();
                ImGui.SetWindowFontScale(1f);
            }
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            if (!open) TogglePoppedSection(def.Panel, def.Id, false);
        }
    }

    /// <summary>Stored order filtered to sections that exist this frame, with any
    /// new sections appended in registration order.</summary>
    private List<string> OrderedSectionIds(string panelId)
    {
        var current = new List<string>();
        foreach (var def in _creatorSectionDefs)
            if (def.Panel == panelId) current.Add(def.Id);

        if (!Settings.Creator.SectionOrder.TryGetValue(panelId, out var stored))
            return current;
        var result = stored.Where(current.Contains).ToList();
        foreach (string id in current)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }

    private void MoveSectionBefore(string panel, string dragged, string before)
    {
        var order = OrderedSectionIds(panel);
        if (!order.Remove(dragged)) return;
        int at = order.IndexOf(before);
        order.Insert(at < 0 ? order.Count : at, dragged);
        Settings.Creator.SectionOrder[panel] = order;
        SettingsFile?.Save();
    }

    private bool IsSectionPopped(string panel, string id)
        => Settings.Creator.PoppedSections.Contains($"{panel}/{id}");

    private void TogglePoppedSection(string panel, string id, bool popped)
    {
        var list = Settings.Creator.PoppedSections;
        string key = $"{panel}/{id}";
        if (popped) { if (!list.Contains(key)) list.Add(key); }
        else list.Remove(key);
        SettingsFile?.Save();
    }

    // Side-band drag state, the inventory drag-drop pattern: the payload is a
    // marker; the actual "what is being dragged" lives here.
    private string? _dragSectionPanel;
    private string? _dragSectionId;

    /// <summary>
    /// A top-level section header: the quest-log +/- art and gold label, a
    /// drag-source/target for reordering, and the pop-out corner button.
    /// Returns true while the section is expanded.
    /// </summary>
    private bool CreatorSectionHeader(in CreatorSectionDef def)
    {
        NotePanelField("headers");
        string key = $"{def.Panel}/{def.Id}";
        bool open = GetSectionOpen(key, def.DefaultOpen);
        float cs = CreatorUiScale;
        float icon = 18f * cs * CreatorIconMul;
        float h = MathF.Max(CreatorRowHeight, icon + 4f * cs);
        var dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        float avail = MathF.Max(ImGui.GetContentRegionAvail().X, 80f);
        float popW = MathF.Min(22f * cs * CreatorIconMul, h);
        float headW = MathF.Max(avail - popW - 8f * cs, 50f);

        if (ImGui.InvisibleButton($"##sec-{key}", new Vector2(headW, h)))
        {
            open = !open;
            SetSectionOpen(key, open);
        }
        bool hovered = ImGui.IsItemHovered();

        // A full-width header band: warm plate behind the label so sections read
        // as one bar (hover brightens it), with a shadow line separating rows.
        dl.AddRectFilled(pos, pos + new Vector2(avail, h),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.36f, 0.28f, 0.12f, hovered ? 0.55f : 0.30f)),
            3f * cs);
        dl.AddLine(pos + new Vector2(0f, h), pos + new Vector2(avail, h), 0x55000000, 1f);

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            _dragSectionPanel = def.Panel;
            _dragSectionId = def.Id;
            ImGui.SetDragDropPayload("CREATOR_SECTION", IntPtr.Zero, 0);
            ImGui.TextUnformatted(def.Label);
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("CREATOR_SECTION");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                _dragSectionPanel == def.Panel && _dragSectionId is { } dragged && dragged != def.Id)
            {
                MoveSectionBefore(def.Panel, dragged, def.Id);
                _dragSectionPanel = _dragSectionId = null;
            }
            ImGui.EndDragDropTarget();
        }

        var iconMin = pos + new Vector2(0f, (h - icon) * 0.5f);
        uint plusMinus = _gameplayArt?.Handle(open
            ? @"Interface\Buttons\UI-MinusButton-Up"
            : @"Interface\Buttons\UI-PlusButton-Up") ?? 0;
        if (plusMinus != 0)
            dl.AddImage((nint)plusMinus, iconMin, iconMin + new Vector2(icon, icon));
        else
            dl.AddText(iconMin, 0xffffffff, open ? "-" : "+");

        var textPos = new Vector2(pos.X + icon + 6f * cs, pos.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(textPos + new Vector2(1f, 1f), 0xdd000000, def.Label);
        dl.AddText(textPos, hovered ? 0xffffffff : VanillaGold, def.Label);

        ImGui.SameLine(0f, 8f * cs);
        if (CreatorPopButton($"##pop-{key}", popW, h))
            TogglePoppedSection(def.Panel, def.Id, true);

        return open;
    }

    /// <summary>The small corner button on a section header that tears it off.</summary>
    private bool CreatorPopButton(string id, float w, float h)
    {
        var dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        float box = MathF.Min(w, h);
        var boxMin = pos + new Vector2(0f, (h - box) * 0.5f);
        uint col = hovered ? 0xffffffff : VanillaGold;
        // A window glyph: outer frame + a bold "title bar" line, drawn by hand so
        // no font/art dependency can leave the button invisible.
        dl.AddRect(boxMin + new Vector2(2f, 2f), boxMin + new Vector2(box - 2f, box - 2f), col, 2f);
        dl.AddLine(boxMin + new Vector2(3f, 5f), boxMin + new Vector2(box - 3f, 5f), col, 2f);
        if (hovered)
            ImGui.SetTooltip("Pop this section out into its own window.\n" +
                             "Drag the section header to reorder sections.");
        return clicked;
    }

    /// <summary>The dim in-panel row standing in for a popped-out section.
    /// Returns true when clicked (dock the section back).</summary>
    private bool CreatorPoppedPlaceholder(in CreatorSectionDef def)
    {
        string key = $"{def.Panel}/{def.Id}";
        float cs = CreatorUiScale;
        float h = CreatorRowHeight;
        var dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        float avail = MathF.Max(ImGui.GetContentRegionAvail().X, 80f);
        bool clicked = ImGui.InvisibleButton($"##dock-{key}", new Vector2(avail, h));
        bool hovered = ImGui.IsItemHovered();
        string label = $"{def.Label}  (popped out - click to dock)";
        var textPos = new Vector2(pos.X + 4f * cs, pos.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(textPos, hovered ? 0xffffffff : 0x88ffffff, label);
        return clicked;
    }

    // ── text-aware sizing ────────────────────────────────────────────────────
    // The widget dial sizes padding and minimum widths; HEIGHTS always derive
    // from the live text size, and widths grow to fit their caption - so no
    // combination of the two dials ever clips a label.

    /// <summary>Control height that always fits the current text scale.</summary>
    private float CreatorRowHeight => ImGui.GetTextLineHeight() + 8f * CreatorUiScale;

    // ── movable buttons (the gear popup's "Move buttons" edit mode) ──────────
    // In edit mode every red button in the window grows a green outline and can
    // be dragged anywhere; its offset from the natural flow position persists.
    // The original flow slot stays occupied (a Dummy) so nothing else reflows.

    private string? _creatorEditLayoutPanel;   // panel id currently in edit mode
    private string? _draggingWidgetKey;

    private Vector2 GetWidgetOffset(string panel, string key)
        => Settings.Creator.WidgetOffsets.TryGetValue(panel, out var map) &&
           map.TryGetValue(key, out float[]? off) && off is { Length: 2 }
            ? new Vector2(off[0], off[1]) : Vector2.Zero;

    private void SetWidgetOffset(string panel, string key, Vector2 offset)
    {
        if (!Settings.Creator.WidgetOffsets.TryGetValue(panel, out var map))
            Settings.Creator.WidgetOffsets[panel] = map = new Dictionary<string, float[]>();
        map[key] = new[] { offset.X, offset.Y };
    }

    /// <summary>Draw a red panel button honoring its hand-placed offset; in edit
    /// mode it drags instead of clicking. All creator red buttons route here.</summary>
    private bool CreatorAnchoredButton(string label, Vector2 size)
    {
        NotePanelField("buttons");
        NotePanelField("movable-buttons");
        string panel = _activePanelTune ?? "";
        string key = $"btn:{label}";
        bool edit = panel.Length > 0 && _creatorEditLayoutPanel == panel;
        float cs = CreatorUiScale;
        Vector2 basePos = ImGui.GetCursorScreenPos();
        Vector2 offset = GetWidgetOffset(panel, key) * cs;
        bool displaced = offset != Vector2.Zero;
        if (displaced) ImGui.SetCursorScreenPos(basePos + offset);

        bool clicked = _skin?.PanelButton(label, size) ?? ImGui.Button(label, size);
        if (edit)
        {
            clicked = false;   // edit mode: buttons move, they do not fire
            var dl = ImGui.GetWindowDrawList();
            dl.AddRect(ImGui.GetItemRectMin() - new Vector2(2f, 2f),
                ImGui.GetItemRectMax() + new Vector2(2f, 2f),
                0xff44dd44, 3f, ImDrawFlags.None, 2f);
            if (ImGui.IsItemActive())
            {
                _draggingWidgetKey = key;
                SetWidgetOffset(panel, key, (offset + ImGui.GetIO().MouseDelta) / cs);
            }
            else if (_draggingWidgetKey == key && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _draggingWidgetKey = null;
                SettingsFile?.Save();
            }
        }
        if (displaced)
        {
            // Keep the natural slot occupied so the surrounding layout is stable.
            ImGui.SetCursorScreenPos(basePos);
            ImGui.Dummy(size);
        }
        return clicked;
    }

    /// <summary>A button at least minWidth wide, grown to fit its caption, as tall as the
    /// text - drawn with the real UI-Panel-Button art (the vanilla in-game button).
    /// Follows the window's own Button dial, and can be hand-placed in edit mode.</summary>
    private bool CreatorButton(string label, float minWidth = 0f)
    {
        float mul = CreatorButtonMul;
        Vector2 text = ImGui.CalcTextSize(label);
        var size = new Vector2(
            MathF.Max(minWidth * mul, text.X + 36f * CreatorUiScale * mul),
            CreatorButtonHeight);
        return CreatorAnchoredButton(label, size);
    }

    // ── drill-down categories (nested, plain - no drag/pop) ──────────────────

    /// <summary>Every expand/collapse is persisted - the arrangement you leave a
    /// panel in is the arrangement it reopens with next session.</summary>
    private bool GetSectionOpen(string key, bool defaultOpen)
        => Settings.Creator.SectionOpen.TryGetValue(key, out bool open) ? open : defaultOpen;

    private void SetSectionOpen(string key, bool open)
    {
        Settings.Creator.SectionOpen[key] = open;
        SettingsFile?.Save();
    }

    /// <summary>
    /// A vanilla expandable category row - the quest-log +/- button art with a
    /// gold header. Returns true while expanded. The id is stable storage for the
    /// open state; the visible label may change freely. Used for NESTED groups
    /// (emitters inside a model editor); top-level groups are sections.
    /// </summary>
    private bool CreatorCategory(string id, string label, bool defaultOpen = false,
        Vector4? marker = null)
    {
        NotePanelField("headers");
        bool open = GetSectionOpen(id, defaultOpen);
        float cs = CreatorUiScale;
        float icon = 18f * cs * CreatorIconMul;
        float h = MathF.Max(CreatorRowHeight, icon + 4f * cs);
        var dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        float avail = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        if (ImGui.InvisibleButton($"##cat-{id}", new Vector2(avail, h)))
        {
            open = !open;
            SetSectionOpen(id, open);
        }
        bool hovered = ImGui.IsItemHovered();
        _creatorCategoryHovered = hovered;

        // A dimmer band than top-level sections: nested groups read as sub-rows.
        dl.AddRectFilled(pos, pos + new Vector2(avail, h),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.24f, 0.11f, hovered ? 0.40f : 0.18f)),
            2f * cs);

        var iconMin = pos + new Vector2(0f, (h - icon) * 0.5f);
        uint plusMinus = _gameplayArt?.Handle(open
            ? @"Interface\Buttons\UI-MinusButton-Up"
            : @"Interface\Buttons\UI-PlusButton-Up") ?? 0;
        if (plusMinus != 0)
            dl.AddImage((nint)plusMinus, iconMin, iconMin + new Vector2(icon, icon));
        else
            dl.AddText(iconMin, 0xffffffff, open ? "-" : "+");

        var textPos = new Vector2(pos.X + icon + 6f * cs, pos.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        // Identity marker: a small colored square between the +/- icon and the
        // label (the workshop uses it to tie emitters to the texture they draw).
        if (marker is { } mc)
        {
            float sq = MathF.Min(icon * 0.55f, 12f * cs);
            var sqMin = new Vector2(textPos.X, pos.Y + (h - sq) * 0.5f);
            dl.AddRectFilled(sqMin, sqMin + new Vector2(sq, sq),
                ImGui.ColorConvertFloat4ToU32(mc), 2f * cs);
            dl.AddRect(sqMin, sqMin + new Vector2(sq, sq), 0x66000000, 2f * cs);
            textPos.X += sq + 5f * cs;
        }
        dl.AddText(textPos + new Vector2(1f, 1f), 0xdd000000, label);
        dl.AddText(textPos, hovered ? 0xffffffff : VanillaGold, label);
        return open;
    }

    /// <summary>A small gold "(?)" after the previous item; hovering explains
    /// exactly what the knob changes. Every creator knob carries one.</summary>
    private void CreatorHelp(string text)
    {
        ImGui.SameLine(0f, 4f * CreatorUiScale);
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(340f * CreatorUiScale);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>A tiny reset button after the previous knob. Returns true when
    /// clicked - the caller puts the knob back to its authored/default value.</summary>
    private bool CreatorResetKnob(string id)
    {
        ImGui.SameLine(0f, 4f * CreatorUiScale);
        bool clicked = ImGui.SmallButton($"x##rst{id}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset this knob");
        return clicked;
    }

    /// <summary>The widest caption in a set, plus button padding - a grid column width.
    /// The pad covers ImGui's frame padding on both sides (the skin pushes 6px-scaled
    /// each side) with margin, so the widest label never clips.</summary>
    private float CreatorColumnWidth(IEnumerable<string> labels)
    {
        float widest = 0f;
        foreach (string label in labels)
            widest = MathF.Max(widest, ImGui.CalcTextSize(label).X);
        return widest + 36f * CreatorUiScale;
    }

    /// <summary>Combo width sized to its widest option (plus the arrow button).</summary>
    private float CreatorComboWidth(IEnumerable<string> labels) =>
        CreatorColumnWidth(labels) + ImGui.GetFrameHeight();

    // ── Character ────────────────────────────────────────────────────────────

    private void RegisterCreatorCharacterSections()
    {
        CreatorSection("Character", "char-race", "Race & Sex", true, DrawCreatorRaceBody);
        CreatorSection("Character", "char-appearance", "Appearance", true, DrawCreatorAppearanceBody);
    }

    private void EnsureCreatorCatalog()
    {
        if (_creatorCatalogTried) return;
        _creatorCatalogTried = true;
        _creatorCatalog = CharCreateCatalog.Load(_config.ClientDataPath);
    }

    private void DrawCreatorRaceBody()
    {
        EnsureCreatorCatalog();
        float cs = CreatorUiScale;
        float raceW = CreatorColumnWidth(CreatorRaces.Select(r => r.Label));
        var dl = ImGui.GetWindowDrawList();
        NotePanelField("buttons");
        for (int i = 0; i < CreatorRaces.Length; i++)
        {
            if (i % 4 != 0) ImGui.SameLine();
            bool active = _creatorRace == CreatorRaces[i].Race;
            var size = new Vector2(raceW * CreatorButtonMul, CreatorButtonHeight);
            bool clicked = CreatorAnchoredButton(CreatorRaces[i].Label, size);
            if (active)   // gold rim marks the worn race, vanilla checked-tab style
                dl.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), VanillaGold, 0f,
                    ImDrawFlags.None, MathF.Max(1f, 2f * cs));
            if (clicked && !active)
            {
                _creatorRace = CreatorRaces[i].Race;
                ClampCreatorDials();
                ApplyCreatorLook(modelChanged: true);
            }
        }

        int sex = _creatorSex;
        if (ImGui.RadioButton("Male", ref sex, 0) | ImGui.RadioButton("Female", ref sex, 1))
        {
            if (sex != _creatorSex)
            {
                _creatorSex = (byte)sex;
                ClampCreatorDials();
                ApplyCreatorLook(modelChanged: true);
            }
        }
    }

    private void DrawCreatorAppearanceBody()
    {
        EnsureCreatorCatalog();
        float cs = CreatorUiScale;
        int[] counts = _creatorCatalog?.DialCounts(_creatorRace, _creatorSex) ?? new[] { 10, 10, 10, 10, 10 };
        string[] dialNames = { "Skin", "Face", "Hair style", "Hair color", _creatorSex == 1 ? "Markings" : "Facial hair" };
        bool dialsChanged = false;
        for (int i = 0; i < 5; i++)
        {
            int max = Math.Max(counts[i] - 1, 0);
            int value = Math.Min(_creatorDials[i], max);
            ImGui.SetNextItemWidth(240f * cs);
            if (ImGui.SliderInt(dialNames[i], ref value, 0, max) && value != _creatorDials[i])
            {
                _creatorDials[i] = value;
                dialsChanged = true;
            }
        }
        if (dialsChanged) ApplyCreatorLook(modelChanged: false);

        if (CreatorButton("Randomize"))
        {
            var rng = Random.Shared;
            for (int i = 0; i < 5; i++)
                _creatorDials[i] = counts[i] > 0 ? rng.Next(counts[i]) : 0;
            ApplyCreatorLook(modelChanged: false);
        }
    }

    private void ClampCreatorDials()
    {
        int[] counts = _creatorCatalog?.DialCounts(_creatorRace, _creatorSex) ?? new[] { 1, 1, 1, 1, 1 };
        for (int i = 0; i < 5; i++)
            _creatorDials[i] = Math.Clamp(_creatorDials[i], 0, Math.Max(counts[i] - 1, 0));
    }

    /// <summary>
    /// Push the creator's race/sex/dials/equipment onto the live world character.
    /// Race/sex changes need a synchronous model re-Load (the GlueBooth create-
    /// preview approach); dial/equipment changes ride the async appearance path.
    /// </summary>
    private void ApplyCreatorLook(bool modelChanged)
    {
        if (_character is null) return;
        CharacterEquipment kit = BuildCreatorEquipment();
        if (modelChanged)
        {
            string folder = CreatorRaceFolder(_creatorRace);
            string gender = _creatorSex == 1 ? "Female" : "Male";
            if (!_character.Load(folder, gender))
            {
                Console.WriteLine($"[creator] could not load {folder} {gender}");
                return;
            }
            _character.SkinId = _creatorDials[0];
            _character.FaceId = _creatorDials[1];
            _character.HairStyleId = _creatorDials[2];
            _character.HairColorId = _creatorDials[3];
            _character.FacialHairId = _creatorDials[4];
            _character.Equipment = kit;
            _character.Reload();
        }
        else
        {
            _character.QueueAppearanceUpdate(_creatorDials[0], _creatorDials[1],
                _creatorDials[2], _creatorDials[3], _creatorDials[4], kit);
        }
        SaveCreatorLook();
    }

    private static string CreatorRaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    // ── Gear ─────────────────────────────────────────────────────────────────

    /// <summary>Chest/robe share a key; weapon-ish types collapse to hand slots.</summary>
    private static int CreatorSlotKey(int inventoryType) => inventoryType switch
    {
        20 => CharacterEquipment.Slot.Chest,
        13 or 17 => CharacterEquipment.Slot.MainHand,
        14 or 23 => CharacterEquipment.Slot.OffHand,
        25 or 26 => CharacterEquipment.Slot.Ranged,
        _ => inventoryType,
    };

    private Dictionary<int, CreatorPiece> CreatorEquip
    {
        get
        {
            if (_creatorEquip is null)
            {
                _creatorEquip = new Dictionary<int, CreatorPiece>();
                // A persisted look wins; a fresh install starts in the Battlegear.
                if (Settings.Creator.Equipment is { Count: > 0 } saved)
                {
                    foreach (var piece in saved)
                        _creatorEquip[CreatorSlotKey(piece.InventoryType)] =
                            new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
                }
                else
                {
                    foreach (var piece in CharacterEquipment.BattlegearOfMight().Pieces)
                        _creatorEquip[CreatorSlotKey(piece.InventoryType)] =
                            new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
                }
            }
            return _creatorEquip;
        }
    }

    /// <summary>Load the persisted creator look into the live fields and wear it.
    /// Called once when a creator session enters the world.</summary>
    private void RestoreCreatorLook()
    {
        var saved = Settings.Creator;
        _creatorRace = saved.Race is >= 1 and <= 8 ? saved.Race : (byte)1;
        _creatorSex = saved.Sex == 1 ? (byte)1 : (byte)0;
        if (saved.Dials is { Length: 5 })
            Array.Copy(saved.Dials, _creatorDials, 5);
        _creatorEquip = null;   // re-seed from the persisted equipment
        ApplyCreatorLook(modelChanged: true);
        Console.WriteLine($"[creator] restored look: race {_creatorRace} sex {_creatorSex}, " +
                          $"{CreatorEquip.Count} piece(s)");
    }

    /// <summary>Persist the current creator look. Called from ApplyCreatorLook, so
    /// every race/sex/dial/gear change sticks into the next session.</summary>
    private void SaveCreatorLook()
    {
        var target = Settings.Creator;
        target.Race = _creatorRace;
        target.Sex = _creatorSex;
        target.Dials = (int[])_creatorDials.Clone();
        target.Equipment = CreatorEquip.Values
            .Select(p => new GameSettings.CreatorPieceSetting
            { Name = p.Name, DisplayId = p.DisplayId, InventoryType = p.InventoryType })
            .ToList();
        SettingsFile?.Save();
    }

    private CharacterEquipment BuildCreatorEquipment()
    {
        var kit = new CharacterEquipment();
        foreach (var piece in CreatorEquip.Values)
            kit.Add(piece.Name, piece.DisplayId, piece.InventoryType);
        return kit;
    }

    private void RegisterCreatorGearSections()
    {
        CreatorSection("Gear", "gear-tiers", "Tier Sets", true, DrawCreatorTierBody);
        CreatorSection("Gear", "gear-worn", "Worn Equipment", true, DrawCreatorWornBody);
    }

    private void DrawCreatorTierBody()
    {
        float cs = CreatorUiScale;
        string[] classes = CreatorTierSets.Classes;
        _creatorClassIndex = Math.Clamp(_creatorClassIndex, 0, classes.Length - 1);
        ImGui.SetNextItemWidth(CreatorComboWidth(classes));
        ImGui.Combo("Class", ref _creatorClassIndex, classes, classes.Length);
        foreach (string tier in CreatorTierSets.Tiers)
        {
            if (tier != CreatorTierSets.Tiers[0]) ImGui.SameLine();
            if (CreatorButton(tier, 56f * cs))
                ApplyCreatorTierSet(classes[_creatorClassIndex], tier);
        }
        ImGui.TextDisabled("Weapons are kept when swapping tier sets.");
    }

    private void DrawCreatorWornBody()
    {
        int? removeKey = null;
        foreach (var (key, piece) in CreatorEquip.OrderBy(p => p.Key))
        {
            ImGui.PushID(key);
            if (ImGui.SmallButton("x")) removeKey = key;
            ImGui.SameLine();
            ImGui.TextUnformatted($"{CreatorSlotName(key)}: {piece.Name}");
            ImGui.PopID();
        }
        if (removeKey is { } gone)
        {
            CreatorEquip.Remove(gone);
            ApplyCreatorLook(modelChanged: false);
        }

        ImGui.Spacing();
        if (CreatorButton("Find item...")) _creatorSearchOpen = true;
        ImGui.SameLine();
        if (CreatorButton("Undress"))
        {
            CreatorEquip.Clear();
            ApplyCreatorLook(modelChanged: false);
        }
    }

    private static string CreatorSlotName(int slotKey) => slotKey switch
    {
        1 => "Head", 3 => "Shoulder", 4 => "Shirt", 5 => "Chest", 6 => "Waist",
        7 => "Legs", 8 => "Feet", 9 => "Wrist", 10 => "Hands", 15 => "Ranged",
        16 => "Back", 19 => "Tabard", 21 => "Main Hand", 22 => "Off Hand",
        _ => $"Slot {slotKey}",
    };

    private void ApplyCreatorTierSet(string cls, string tier)
    {
        if (!CreatorTierSets.Sets.TryGetValue(cls, out var tiers) ||
            !tiers.TryGetValue(tier, out var pieces)) return;

        // Tier sets are armor: drop worn armor, keep hands/ranged (the weapons).
        var keep = new[] { CharacterEquipment.Slot.MainHand, CharacterEquipment.Slot.OffHand, CharacterEquipment.Slot.Ranged };
        foreach (int key in CreatorEquip.Keys.Where(k => !keep.Contains(k)).ToList())
            CreatorEquip.Remove(key);
        foreach (var piece in pieces)
        {
            if (piece.InventoryType == 11) continue;   // rings have no visual
            CreatorEquip[CreatorSlotKey(piece.InventoryType)] =
                new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
        }
        ApplyCreatorLook(modelChanged: false);
        Console.WriteLine($"[creator] dressed {cls} {tier} ({pieces.Length} pieces)");
    }

    private void DrawCreatorItemSearch()
    {
        if (!_creatorItemsTried)
        {
            _creatorItemsTried = true;
            _creatorItems = CreatorItemTable.Load(_config.RepoRoot);
        }

        _activePanelTune = "Find Item";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(390f * s, 64f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(440f * cs, 500f * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(280f * cs, 200f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-find-item", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Find Item", "Find Item")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();
            if (_creatorItems is null)
            {
                ImGui.TextWrapped("creator-items.tsv is missing. Regenerate it from " +
                                  "MangosSuperUI (/Items/Search dump) and restart.");
            }
            else
            {
                ImGui.SetNextItemWidth(200f * cs);
                bool changed = ImGui.InputText("##search", _creatorSearchBuf, (uint)_creatorSearchBuf.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130f * cs);
                int slotIndex = Array.FindIndex(CreatorSearchSlots, x => x.InvType == _creatorSearchSlot);
                if (slotIndex < 0) slotIndex = 0;
                string[] slotLabels = CreatorSearchSlots.Select(x => x.Label).ToArray();
                if (ImGui.Combo("##slot", ref slotIndex, slotLabels, slotLabels.Length))
                {
                    _creatorSearchSlot = CreatorSearchSlots[slotIndex].InvType;
                    changed = true;
                }

                string query = BufToString(_creatorSearchBuf);
                if (changed || _creatorSearchResults is null)
                    _creatorSearchResults = query.Length >= 2 || _creatorSearchSlot >= 0
                        ? _creatorItems.Search(query, _creatorSearchSlot)
                        : new List<CreatorItemTable.Item>();

                ImGui.TextDisabled(query.Length < 2 && _creatorSearchSlot < 0
                    ? "Type at least 2 letters, or pick a slot."
                    : $"{_creatorSearchResults.Count} result(s), click to equip");

                ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.07f, 0.07f, 0.08f, 0.97f));
                if (ImGui.BeginChild("##results", new Vector2(0f, -4f), true))
                {
                    foreach (var item in _creatorSearchResults)
                    {
                        var color = CreatorQualityColors[Math.Min(item.Quality, (byte)6)];
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        bool clicked = CreatorResultRow($"{item.Name}##{item.Entry}");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"entry {item.Entry}  display {item.DisplayId}\n" +
                                             $"{CreatorSlotName(CreatorSlotKey(item.InventoryType))}  ilvl {item.ItemLevel}");
                        if (clicked)
                        {
                            CreatorEquip[CreatorSlotKey(item.InventoryType)] =
                                new CreatorPiece(item.Name, item.DisplayId, item.InventoryType);
                            ApplyCreatorLook(modelChanged: false);
                        }
                    }
                }
                ImGui.EndChild();
                ImGui.PopStyleColor();
            }
            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
        if (!open) _creatorSearchOpen = false;
    }

    // ── Registered in the world/spell slices ─────────────────────────────────

    private partial void RegisterCreatorTeleportSections();
    private partial void RegisterCreatorTargetSections();
    private partial void RegisterCreatorSpellsSections();
    private partial void RegisterCreatorXraySections();
}
