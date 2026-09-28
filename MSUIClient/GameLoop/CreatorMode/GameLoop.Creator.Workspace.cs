using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator Workspace (2026-08-20) — the docked layout.
//
// Instead of floating windows fighting for the middle of the screen, the
// creator chrome lives at the edges: a LEFT rail of square buttons (the
// top-level panels; entering Encounter swaps the rail to the Lab's sections),
// a RIGHT rail of quick actions (free view, customizer, layout switch), and a
// full-width BOTTOM DECK that whatever rail button is pressed fills with its
// controls. The world keeps the whole centre. Settings.Creator.Workspace turns
// the whole thing off and the classic floating windows return unchanged.
//
// The Spell Workshop opts OUT of this shape into a third layout - two wide
// master/detail panes and no deck at all, because it registers one section per
// phase model and the deck's columns clip every one of them. See
// GameLoop.Creator.SpellIde.cs; DrawCreatorWorkspace hands off to it via a
// single early return.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private enum WorkspaceView { Root, Encounter }

    private WorkspaceView _workspaceView;
    private string _workspaceEncSection = "fight";

    /// <summary>Auto-calibration for the bottom deck. The rails scale with the
    /// display (display.Y / GlueCanvasH) but panel content scales only with the
    /// owner's dials, so on a tall display the deck read unreadably small next
    /// to its own rail. While the deck draws, this display-derived factor is
    /// folded into <see cref="CreatorUiScale"/>/<see cref="CreatorTextScale"/>;
    /// it is 1 everywhere else.</summary>
    private float _creatorScaleBoost = 1f;

    private float WorkspaceDeckBoost =>
        Math.Clamp(0.72f * ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 1f, 2.2f);

    /// <summary>The docked workspace is on and this is a creator-world frame.</summary>
    private bool CreatorWorkspaceActive => _creatorWorldRequested && Settings.Creator.Workspace;

    /// <summary>The square rail's width - the ONE home of the 64f literal.</summary>
    private float WorkspaceRailWidth =>
        64f * MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorBarScale;

    /// <summary>How far right-edge-anchored windows (the customizer, its slide-in
    /// launcher) must move left so the right rail does not cover them - or, in the
    /// Spell Workshop's IDE layout, the inspector while it is docked right.</summary>
    private float WorkspaceRightInsetX => !CreatorWorkspaceActive ? 0f
        : SpellFocusActive ? SpellIdeRightInset
        : WorkspaceRailWidth;

    private void DrawCreatorWorkspace()
    {
        var io = ImGui.GetIO();
        float s = MathF.Max(io.DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorBarScale;
        float railW = WorkspaceRailWidth;

        // Ctrl+E can close the Lab underneath the encounter view.
        if (_workspaceView == WorkspaceView.Encounter && !_encounterLabOpen)
            _workspaceView = WorkspaceView.Root;

        // The Spell Workshop's IDE layout REPLACES this whole layout: one early
        // return both swaps the rails for the strip / tree / inspector and suppresses
        // the deck (DrawWorkspaceDeck is simply never reached). Note it does NOT write
        // Settings.Creator.DeckFraction - a 0 there would clamp back to 0.16 below
        // and permanently shrink the user's deck.
        if (SpellFocusActive) { DrawCreatorSpellFocus(); return; }

        DrawWorkspaceLeftRail(railW, s);
        DrawWorkspaceRightRail(railW, s);

        bool deckWanted = _workspaceView == WorkspaceView.Encounter
            || _creatorPanel != CreatorPanel.None
            || _encounterPlayerSetupKey is not null;
        if (deckWanted) DrawWorkspaceDeck(railW, s);
    }

    // ── rails ────────────────────────────────────────────────────────────────

    private void BeginWorkspaceRail(string id, float railW, bool left)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(left ? 0f : io.DisplaySize.X - railW, 0f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(railW, io.DisplaySize.Y), ImGuiCond.Always);
        // Translucent rails: the world reads THROUGH them, so the scene keeps
        // its whole width; the buttons carry their own near-opaque plates.
        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            new Vector4(0.05f, 0.05f, 0.06f, Math.Clamp(Settings.Creator.PanelAlpha - 0.17f, 0.2f, 0.75f)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 8f));
        ImGui.Begin(id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing);
    }

    private static void EndWorkspaceRail()
    {
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>One square rail button: a real WoW icon on a dark plate with the
    /// caption along its bottom edge, hover highlight from the vanilla button
    /// art, gold rim when active. Missing icon art degrades to the question-mark
    /// fallback via GameplayArt. Returns true on click.</summary>
    private bool WorkspaceRailButton(string id, string caption, string iconFile, float railW,
        float s, bool active, string tooltip, bool enabled = true)
    {
        float sq = railW - 12f * s;
        ImGui.SetCursorPosX((railW - sq) * 0.5f);
        bool pressed = ImGui.InvisibleButton("##rail-" + id, new Vector2(sq, sq)) && enabled;
        Vector2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.03f, 0.03f, 0.035f, 0.92f)), 5f * s);
        uint icon = _gameplayArt?.Handle($@"Interface\Icons\{iconFile}") ?? 0;
        if (icon != 0)
        {
            // WoW icons carry a baked bezel; crop it so the plate is the bezel.
            Vector2 inset = new(3f * s);
            dl.AddImageRounded((nint)icon, min + inset, max - inset,
                new Vector2(0.07f), new Vector2(0.93f),
                enabled ? 0xffffffffu : 0xff6f6f6fu, 4f * s);
        }
        if (hovered && enabled)
        {
            // The action bar's own hover art - the warm square glow, not the
            // cool list-row highlight.
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
            else dl.AddRect(min, max, 0x6055ddff, 5f * s, ImDrawFlags.None, 2f);
        }
        dl.AddRect(min, max, active ? VanillaGold : 0xff2a2a2e, 5f * s,
            ImDrawFlags.None, active ? 2.5f : 1.5f);

        if (caption.Length > 0)
        {
            float px = Math.Clamp(11f * s * CreatorBarTextScale, 9f, 26f);
            var font = ImGui.GetFont();
            Vector2 measured = ImGui.CalcTextSize(caption) * (px / ImGui.GetFontSize());
            Vector2 at = new(min.X + (sq - measured.X) * 0.5f, max.Y - measured.Y - 2f * s);
            dl.AddRectFilled(new Vector2(min.X, at.Y - 1f), max, 0x99000000, 5f * s,
                ImDrawFlags.RoundCornersBottom);
            dl.AddText(font, px, at + new Vector2(1f, 1f), 0xff000000, caption);
            dl.AddText(font, px, at, 0xff4ab6d8, caption);
        }
        if (hovered) ImGui.SetTooltip(tooltip);
        ImGui.Spacing();
        return pressed;
    }

    private void DrawWorkspaceLeftRail(float railW, float s)
    {
        BeginWorkspaceRail("##workspace-rail-left", railW, left: true);
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;

        if (_workspaceView == WorkspaceView.Root)
        {
            void Panel(string label, string icon, CreatorPanel panel, string tip)
            {
                if (!WorkspaceRailButton(label, label, icon, railW, s, _creatorPanel == panel, tip))
                    return;
                _creatorPanel = _creatorPanel == panel ? CreatorPanel.None : panel;
                // Opening a panel afresh clears a session-only focus opt-out, so
                // Escape/"deck" suppress THIS visit to the workshop, not every later one.
                if (_creatorPanel == panel) _spellFocusSuppressed = false;
            }

            Panel("Char", "INV_Misc_Head_Human_01", CreatorPanel.Character,
                "Character - race, look, dials");
            Panel("Gear", "INV_Chest_Chain", CreatorPanel.Gear,
                "Gear - equipment and displays");
            Panel("Tele", "Spell_Arcane_TeleportStormWind", CreatorPanel.Teleport,
                "Teleport - travel the world");
            Panel("Targ", "Ability_Marksmanship", CreatorPanel.Target,
                "Target - creatures and spawns");
            Panel("Spell", "INV_Misc_Book_09", CreatorPanel.Spells, "Spell Workshop");
            Panel("X-Ray", "Spell_Holy_MindVision", CreatorPanel.XRay, "Collision X-Ray");
            Panel("World", "INV_Misc_Map_01", CreatorPanel.World, "World Builder - sculpt, place, publish");
            ImGui.Separator();
            // No gold rim here: the rim means "this is the view you are in", and
            // in the root view it never is - the Lab can stay OPEN (simulating)
            // behind the scenes after Back without wearing a stale highlight.
            if (WorkspaceRailButton("enc", "Enc", "INV_Misc_Head_Dragon_01", railW, s,
                    false,
                    "Encounter Lab (Ctrl+E) - the rail switches to its sections"))
            {
                if (!_encounterLabOpen) ToggleEncounterLab();
                if (_encounterLabOpen) _workspaceView = WorkspaceView.Encounter;
            }
            ImGui.Separator();
            if (WorkspaceRailButton("ui", "UI", "Trade_Engineering", railW, s,
                    _creatorUiOptionsOpen, "Creator UI dials"))
                _creatorUiOptionsOpen = !_creatorUiOptionsOpen;
        }
        else
        {
            if (WorkspaceRailButton("back", "Back", "INV_Misc_Rune_01", railW, s, false,
                    "Back to the creator panels (the encounter keeps running)"))
                _workspaceView = WorkspaceView.Root;
            ImGui.Separator();

            void Section(string label, string icon, string key, string tip)
            {
                if (!WorkspaceRailButton(key, label, icon, railW, s,
                        _workspaceEncSection == key && _encounterPlayerSetupKey is null, tip))
                    return;
                // A section press reclaims the deck from the customizer; the
                // draft survives per actor key, so reopening loses nothing.
                _encounterPlayerSetupKey = null;
                _workspaceEncSection = _workspaceEncSection == key ? "" : key;
            }

            Section("Fight", "INV_Misc_Head_Dragon_01", "fight",
                "Encounter - pick a document, status, refresh");
            Section("Scene", "INV_Misc_Map_01", "scenario",
                "Scenario - placement, playbook, aggro, roster (sub-tabs)");
            Section("Play", "Ability_Rogue_Sprint", "transport",
                "Transport - GO/play/pause/scrub + dials (sub-tabs)");
            Section("Time", "INV_Misc_PocketWatch_01", "timeline",
                "Timeline - the event list around now");
            Section("Probe", "INV_Misc_Spyglass_02", "probe",
                "Position probe - place, waypoint, report");
            Section("Over", "Spell_Nature_EarthBind", "overlays",
                "Overlays - footprints, routes, labels");
            Section("Abil", "Spell_Fire_FireBall02", "abilities",
                "Abilities - the boss's authored kit");
            Section("Cover", "INV_Misc_Note_02", "coverage", "Coverage & declared holes");
            Section("Tape", "Spell_Nature_TimeStop", "tape",
                "Tape - record & compare live SPELL_GO");
        }

        if (_skin is not null) _skin.Scale = savedScale;
        EndWorkspaceRail();
    }

    private bool _workspaceHelpOpen;

    private void DrawWorkspaceRightRail(float railW, float s)
    {
        BeginWorkspaceRail("##workspace-rail-right", railW, left: false);
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;

        if (WorkspaceRailButton("fly", "Fly", "Spell_Nature_FarSight", railW, s, _freeView,
                _freeView ? "Return to your character (Ctrl+F)"
                          : "Rise into the command view (Ctrl+F)"))
            ToggleFreeView();

        string? customizeKey = SelectedEncounterPlayerSetupKey();
        if (WorkspaceRailButton("cust", "Cust", "Spell_Shadow_Charm", railW, s,
                _encounterPlayerSetupKey is not null,
                customizeKey is not null
                    ? "Open the Character Customizer for the selected body"
                    : _encounterPlayerSetupKey is not null
                        ? "Close the Character Customizer"
                        : "Select one friendly body in the command view to customize it",
                enabled: customizeKey is not null || _encounterPlayerSetupKey is not null))
        {
            if (customizeKey is not null) OpenEncounterPlayerSetup(customizeKey);
            else if (_encounterPlayerSetupKey is not null) _encounterPlayerSetupKey = null;
        }

        ImGui.Separator();
        if (WorkspaceRailButton("wins", "Wins", "INV_Misc_Gear_01", railW, s, false,
                "Switch back to the classic floating windows"))
        {
            Settings.Creator.Workspace = false;
            SettingsFile?.Save();
        }

        // The big "?", anchored to the rail's bottom: every control, explained.
        float sq = railW - 12f * s;
        float bottomY = ImGui.GetWindowHeight() - sq - 14f * s;
        if (bottomY > ImGui.GetCursorPosY()) ImGui.SetCursorPosY(bottomY);
        if (WorkspaceRailButton("help", "Help", "INV_Misc_QuestionMark", railW, s,
                _workspaceHelpOpen, "What does every button do?"))
            _workspaceHelpOpen = !_workspaceHelpOpen;

        if (_skin is not null) _skin.Scale = savedScale;
        EndWorkspaceRail();

        if (_workspaceHelpOpen) DrawWorkspaceHelp();
    }

    /// <summary>The help window the "?" opens: every rail button, deck control
    /// and world gesture, in one place.</summary>
    private void DrawWorkspaceHelp()
    {
        _activePanelTune = null;
        float cs = CreatorUiScale;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.42f),
            ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(660f * cs, 560f * cs), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420f, 320f),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        if (ImGui.Begin("###workspace-help", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Help")) _workspaceHelpOpen = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            void Head(string text) => ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), text);
            // The name column must be measured at the CURRENT font scale, or a
            // scaled-up font overprints the description over the name.
            float nameColumn = MathF.Max(ImGui.CalcTextSize("Right-click").X,
                ImGui.CalcTextSize("Shift+Right").X) + 18f * cs;
            void Row(string name, string what)
            {
                ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), name);
                ImGui.SameLine(nameColumn);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(what);
                ImGui.PopTextWrapPos();
            }

            Head("LEFT RAIL — creator panels");
            Row("Char", "Your character: race, sex, face/hair dials.");
            Row("Gear", "Equipment: pick display pieces for every slot.");
            Row("Tele", "Teleport: travel anywhere in the world.");
            Row("Targ", "Creatures: browse and spawn any creature.");
            Row("Spell", "Spell Workshop: play and edit any spell's visuals.");
            Row("X-Ray", "Collision X-Ray: see the collision world.");
            Row("Enc", "Encounter Lab (Ctrl+E). The rail switches to its sections.");
            Row("UI", "Creator UI dials: scales, opacity, layout, this workspace toggle.");
            ImGui.Spacing();

            Head("LEFT RAIL — encounter sections (after Enc)");
            Row("Back", "Return to the creator panels. The encounter keeps running.");
            Row("Fight", "Pick an encounter document, see its status, refresh the DB.");
            Row("Scene", "The scenario, in sub-tabs: Placement (boss + raid, pull ring, roam), " +
                         "Playbook (per phase x job orders), Aggro (who she faces when), " +
                         "Roster (every body's verbs and dps).");
            Row("Play", "Playback, in sub-tabs: Controls (GO/play/pause/step/scrub) and " +
                        "Dials (speed, seed, step, raid dps).");
            Row("Time", "The event timeline around the current instant.");
            Row("Probe", "Position probe: place a marker, waypoint it, read what hits it.");
            Row("Over", "Overlay toggles: footprints, routes, actor marks, labels.");
            Row("Abil", "The boss's authored ability kit.");
            Row("Cover", "Which behaviour sources were consulted, and declared holes.");
            Row("Tape", "Record live SMSG_SPELL_GO and compare it to the sim.");
            ImGui.Spacing();

            Head("RIGHT RAIL");
            Row("Fly", "Free view (Ctrl+F): the sky rig you command the raid from.");
            Row("Cust", "Character Customizer for the ONE selected friendly body - opens in the bottom deck.");
            Row("Wins", "Back to the classic floating-window layout.");
            Row("?", "This window.");
            ImGui.Spacing();

            Head("IDE MODE (the Spell Workshop)");
            Row("What", "The workshop becomes an IDE and the rest of the screen is the stage: " +
                        "a top-left strip (Back, the spell, Loop / Pause / Step / Speed, the " +
                        "Stage / Grid / Gizmo view toggles), a collapsible tree on the left " +
                        "(spell, phases, emitters), ONE inspector on the right with the dials " +
                        "for whatever is selected, and a timeline strip along the bottom only " +
                        "while paused.");
            Row("Select", "Click a phase or emitter in the tree - or click an emitter's gizmo " +
                          "in the world - and the inspector swaps to it. 'pin' keeps a copy " +
                          "open while you select something else; 'x' hides the inspector " +
                          "until the next selection.");
            Row("Space", "'Tree' and 'Inspector' fold each side away; 'Hide' clears every " +
                         "window to a small Show pill; the TOGGLEUI chord (Alt+Z) clears " +
                         "the pill too. The inspector drags and resizes freely.");
            Row("Leaving", "'< Back' closes the workshop. 'deck' returns it to the rails+deck " +
                           "for this session (an 'IDE' button in the deck header brings it " +
                           "back). The UI dials' 'Spell Workshop IDE layout' checkbox is the " +
                           "permanent switch. Escape keeps its normal meaning.");
            ImGui.Spacing();

            Head("BOTTOM DECK");
            Row("Top edge", "Drag the deck's top edge to make it taller or shorter.");
            Row("dials", "Per-window size dials for whatever the deck is showing.");
            Row("Opacity", "UI (left rail) - Background opacity sets how much world " +
                           "reads through the deck and the floating windows.");
            ImGui.Spacing();

            Head("IN THE WORLD (free view)");
            Row("Left-click", "Select a body. Drag for a marquee over several.");
            Row("Shift+Left", "Stage a waypoint for the selection (pre-pull queue; GO commits).");
            Row("Right-click", "Order the selection to run there now.");
            Row("Shift+Right", "On a waypoint dot: grab it and spin its arrival facing; any click commits.");
            Row("Ctrl+Right", "Teleport what-if: the body stands THERE at this instant.");
            Row("Alt+Right", "Set arrival facing on the last order.");
            Row("Ctrl+Z", "Undo the last staged waypoint.");
            Row("Ctrl+Tab", "Cycle control across your character and controllable bots.");

            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
    }

    // ── the deck ─────────────────────────────────────────────────────────────

    private void DrawWorkspaceDeck(float railW, float s)
    {
        var io = ImGui.GetIO();
        float deckH = Math.Clamp(Settings.Creator.DeckFraction, 0.16f, 0.55f) * io.DisplaySize.Y;
        float width = MathF.Max(io.DisplaySize.X - railW * 2f, 200f);

        bool encounterView = _workspaceView == WorkspaceView.Encounter;
        // The customizer temporarily OWNS the deck - selection work wants the
        // world, and the plan editor wants the width.
        bool customizer = _encounterPlayerSetupKey is not null;
        // (panelId, title): panelId is the classic tune/section id, so per-window
        // dials, section order and pop-out state are SHARED with the classic
        // floating layout rather than forked.
        (string panelId, string title) = customizer
            ? ("encounter-player-setup", "Character Customizer")
            : encounterView
                ? ("encounter-lab", "Encounter Lab")
                : _creatorPanel switch
                {
                    CreatorPanel.Character => ("Character", "Character"),
                    CreatorPanel.Gear => ("Gear", "Gear"),
                    CreatorPanel.Teleport => ("Teleport", "Teleport"),
                    CreatorPanel.Target => ("Target", "Target"),
                    CreatorPanel.Spells => ("Spells", "Spell Workshop"),
                    CreatorPanel.XRay => ("XRay", "Collision X-Ray"),
                    CreatorPanel.World => ("World", "World Builder"),
                    _ => ("", ""),
                };
        if (panelId.Length == 0) return;

        _activePanelTune = panelId;
        _creatorScaleBoost = WorkspaceDeckBoost;
        float deckTop = io.DisplaySize.Y - deckH;

        // The manila sub-tab folders sit ON TOP of the deck plate, outside it.
        string[] subTabs = customizer
            ? EncounterPlanDeckTabs
            : encounterView ? WorkspaceSubTabsFor(_workspaceEncSection) : [];
        if (subTabs.Length > 0)
            DrawWorkspaceDeckTabStrip(railW, deckTop, s,
                customizer ? "customizer" : _workspaceEncSection, subTabs);

        ImGui.SetNextWindowPos(new Vector2(railW, deckTop), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, deckH), ImGuiCond.Always);
        PushCreatorStyle();
        // Compact rhythm. PushCreatorStyle's paddings multiply the display
        // factor AND the deck boost, which read as enormous empty rows down
        // here. Air scales with the display ONCE; the boost stays on glyphs
        // and widgets.
        float air = MathF.Max(io.DisplaySize.Y / GlueCanvasH, 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 7f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4.5f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 3.5f) * air);
        // The shared "Background opacity" dial (UI options / the deck's own
        // dials popup) governs the deck plate DIRECTLY - the owner decides how
        // much world reads through it. The rails keep their derived value.
        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            new Vector4(0.055f, 0.05f, 0.045f, Math.Clamp(Settings.Creator.PanelAlpha, 0.15f, 1f)));
        if (ImGui.Begin("##workspace-deck", CreatorChromeFlags | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing))
        {
            DrawWorkspaceDeckSplitter(width);

            // Header: gold title, live status, gear (per-window dials).
            // ImGui child windows MULTIPLY their font scale by the parent's, so
            // the deck window itself must return to 1 before any content child
            // begins or the content renders at scale squared (columns: cubed).
            ImGui.SetWindowFontScale(CreatorTextScale);
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), title);
            string status = customizer
                ? ""
                : encounterView
                    ? _encounterDefinition is { } definition
                        ? $"{definition.Name} · {_encounterOutcome}" : "no encounter loaded"
                    : CreatorPanelStatus(panelId);
            if (status.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(status); }
            // The Spell Workshop can be sent BACK to its IDE layout from here -
            // otherwise the deck's own "deck" button and Escape would be one-way.
            bool offerFocus = panelId == "Spells";
            float gearW = ImGui.CalcTextSize("dials").X + 16f * CreatorUiScale;
            if (offerFocus) gearW += ImGui.CalcTextSize("IDE").X + 24f * CreatorUiScale;
            ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(),
                ImGui.GetWindowContentRegionMax().X - gearW));
            if (offerFocus)
            {
                if (ImGui.SmallButton("IDE"))
                {
                    _spellFocusSuppressed = false;
                    if (!Settings.Creator.SpellFocus)
                    {
                        Settings.Creator.SpellFocus = true;
                        SettingsFile?.Save();
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Back to the IDE layout: strip, tree, inspector, and the stage.");
                ImGui.SameLine();
            }
            if (ImGui.SmallButton("dials"))
                _openPanelTuneId = _openPanelTuneId is null ? _activePanelTune : null;
            ImGui.Separator();
            ImGui.SetWindowFontScale(1f);   // children apply the scale themselves

            if (customizer)
            {
                ImGui.BeginChild("##deck-customizer", new Vector2(0f, 0f));
                ImGui.SetWindowFontScale(CreatorTextScale);
                DrawEncounterPlayerSetupDeckBody(
                    WorkspaceSubTabIndex("customizer", EncounterPlanDeckTabs));
                ImGui.SetWindowFontScale(1f);
                ImGui.EndChild();
            }
            else if (encounterView) DrawWorkspaceEncounterDeck();
            else DrawWorkspacePanelDeck(panelId);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        PopCreatorStyle();
        _activePanelTune = null;
        _creatorScaleBoost = 1f;
    }

    // ── deck sub-tabs ────────────────────────────────────────────────────────

    private readonly Dictionary<string, int> _workspaceSubTabSel = new(StringComparer.Ordinal);
    private static readonly string[] WorkspaceScenarioTabs = ["Placement", "Playbook", "Aggro", "Roster"];
    private static readonly string[] WorkspaceTransportTabs = ["Controls", "Dials"];
    private static readonly string[] WorkspaceAbilityTabs = ["Visualize", "Kit"];

    private static string[] WorkspaceSubTabsFor(string section) => section switch
    {
        "scenario" => WorkspaceScenarioTabs,
        "transport" => WorkspaceTransportTabs,
        "abilities" => WorkspaceAbilityTabs,
        _ => [],
    };

    private int WorkspaceSubTabIndex(string section, string[] tabs) =>
        tabs.Length == 0 ? 0 : Math.Clamp(_workspaceSubTabSel.GetValueOrDefault(section), 0, tabs.Length - 1);

    /// <summary>Manila folder tabs ATTACHED to the deck's top edge, outside the
    /// plate: the selected folder is parchment and flush with the deck, the
    /// others sit slightly lower and darker. A borderless click-through window
    /// in the world strip just above the deck.</summary>
    private void DrawWorkspaceDeckTabStrip(float railW, float deckTop, float s,
        string key, string[] tabs)
    {
        float tabH = 34f * s;
        float px = Math.Clamp(14f * s * CreatorBarTextScale, 11f, 30f);
        var font = ImGui.GetFont();
        int sel = WorkspaceSubTabIndex(key, tabs);

        ImGui.SetNextWindowPos(new Vector2(railW + 16f * s, deckTop - tabH), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(
            ImGui.GetIO().DisplaySize.X - railW * 2f - 32f * s, tabH), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBackground;
        if (ImGui.Begin("##workspace-deck-tabs", flags))
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetWindowPos();
            float x = 0f;
            for (int i = 0; i < tabs.Length; i++)
            {
                Vector2 textSize = ImGui.CalcTextSize(tabs[i]) * (px / ImGui.GetFontSize());
                float w = textSize.X + 30f * s;
                bool active = i == sel;
                float top = active ? 0f : 7f * s;
                Vector2 min = origin + new Vector2(x, top);
                Vector2 max = origin + new Vector2(x + w, tabH);
                ImGui.SetCursorPos(new Vector2(x, top));
                if (ImGui.InvisibleButton($"##subtab-{i}", new Vector2(w, tabH - top)))
                    _workspaceSubTabSel[key] = i;
                bool hovered = ImGui.IsItemHovered();
                uint fill = active
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.70f, 0.55f, 0.23f, 1f))
                    : ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.12f, 0.09f, 0.97f));
                dl.AddRectFilled(min, max, fill, 7f * s, ImDrawFlags.RoundCornersTop);
                if (hovered && !active)
                    dl.AddRectFilled(min, max, 0x334ab6d8, 7f * s, ImDrawFlags.RoundCornersTop);
                dl.AddRect(min, max, active ? VanillaGold : 0xff33302a, 7f * s,
                    ImDrawFlags.RoundCornersTop, active ? 2f : 1.2f);
                uint textCol = active
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.13f, 0.09f, 0.03f, 1f))
                    : 0xff4ab6d8;
                Vector2 at = new(min.X + (w - textSize.X) * 0.5f,
                    min.Y + (max.Y - min.Y - textSize.Y) * 0.5f);
                dl.AddText(font, px, at, textCol, tabs[i]);
                x += w + 5f * s;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>A grab strip along the deck's top edge; dragging it resizes the
    /// deck. The mapping is absolute (mouse Y → fraction), so there is no
    /// geometry feedback loop.</summary>
    private void DrawWorkspaceDeckSplitter(float width)
    {
        var io = ImGui.GetIO();
        Vector2 keep = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(0f, 0f));
        ImGui.InvisibleButton("##deck-splitter", new Vector2(width, 7f));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(), ImGui.ColorConvertFloat4ToU32(
                    new Vector4(1f, 0.82f, 0.28f, ImGui.IsItemActive() ? 0.8f : 0.4f)));
        }
        if (ImGui.IsItemActive() && io.DisplaySize.Y > 1f)
            Settings.Creator.DeckFraction = Math.Clamp(
                (io.DisplaySize.Y - io.MousePos.Y) / io.DisplaySize.Y, 0.16f, 0.55f);
        if (ImGui.IsItemDeactivated()) SettingsFile?.Save();
        ImGui.SetCursorPos(keep);
    }

    /// <summary>The selected encounter section, full width. Sections draw FLAT
    /// (the rail button is the header), and the big ones subdivide via the
    /// manila folder strip attached to the deck's top edge
    /// (<see cref="DrawWorkspaceDeckTabStrip"/>).</summary>
    private void DrawWorkspaceEncounterDeck()
    {
        if (_workspaceEncSection.Length == 0)
        {
            ImGui.TextDisabled("Pick a section on the left rail.");
            return;
        }
        RefreshProbeReport();
        ImGui.BeginChild("##deck-encounter", new Vector2(0f, 0f));
        ImGui.SetWindowFontScale(CreatorTextScale);
        _encounterSectionsFlat = true;

        switch (_workspaceEncSection)
        {
            case "fight":
                DrawEncounterToolbar();
                DrawEncounterStatusLine();
                DrawEncounterSubjectSection();
                break;
            case "scenario" when _encounterDefinition is null:
                ImGui.TextDisabled("load an encounter first (Fight section)");
                break;
            case "scenario":
                switch (WorkspaceSubTabIndex("scenario", WorkspaceScenarioTabs))
                {
                    case 0: DrawEncounterScenarioPlacement(); break;
                    case 1: DrawEncounterPlaybook(); break;
                    case 2: DrawEncounterScenarioAggroPlan(); break;
                    case 3: DrawEncounterScenarioRoster(); break;
                }
                break;
            case "transport" when _encounterSim is null:
                ImGui.TextDisabled("load an encounter to simulate it");
                break;
            case "transport":
                if (WorkspaceSubTabIndex("transport", WorkspaceTransportTabs) == 0)
                {
                    DrawEncounterStatusLine();
                    DrawEncounterTransportControls(_encounterSim!);
                }
                else DrawEncounterTransportDials();
                break;
            case "timeline": DrawEncounterTimelineSection(); break;
            case "probe": DrawEncounterProbeSection(); break;
            case "overlays": DrawEncounterOverlaySection(); break;
            case "abilities":
                if (WorkspaceSubTabIndex("abilities", WorkspaceAbilityTabs) == 0)
                    DrawEncounterAbilityDeckGrid();
                else DrawEncounterAbilitiesSection();
                break;
            case "coverage": DrawEncounterCoverageSection(); break;
            case "tape": DrawEncounterTapeSection(); break;
        }

        _encounterSectionsFlat = false;
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
    }

    /// <summary>A creator panel's registered sections laid out SIDE BY SIDE as
    /// columns — the deck's width is the point. Popped-out sections keep their
    /// floating windows and show the usual dock-back placeholder.</summary>
    private void DrawWorkspacePanelDeck(string panelId)
    {
        float cs = CreatorUiScale;
        float colW = 420f * cs;
        ImGui.BeginChild("##deck-panel", new Vector2(0f, 0f),
            false, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.SetWindowFontScale(CreatorTextScale);

        bool first = true;
        foreach (string id in OrderedSectionIds(panelId))
        {
            int at = _creatorSectionDefs.FindIndex(d => d.Panel == panelId && d.Id == id);
            if (at < 0) continue;
            var def = _creatorSectionDefs[at];
            if (!first) ImGui.SameLine();
            first = false;

            // No font scale here: the column INHERITS ##deck-panel's scale
            // (ImGui multiplies child scale by parent's) - setting it again
            // would square it.
            ImGui.BeginChild($"##deck-col-{id}", new Vector2(colW, 0f), true);
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), def.Label);
            ImGui.Separator();
            if (IsSectionPopped(panelId, id))
            {
                if (CreatorPoppedPlaceholder(def)) TogglePoppedSection(panelId, id, false);
            }
            else
            {
                ImGui.PushID(id);
                def.Body();
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        if (first) ImGui.TextDisabled("This panel has no sections.");
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
    }
}
