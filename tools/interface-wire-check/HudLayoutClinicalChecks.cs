using System.Numerics;
using System.Text.Json;
using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// PLAN_21 HUD layout editor guard: the placement law round-trips and never throws, the
/// nearest-anchor re-pick never moves a rect, snapping prefers frame edges over grid lines
/// and stays inert beyond its threshold, an edit session undoes and redoes a drag sequence,
/// the Version 11 chat offset migrates into a Custom layout with no legacy keys left on
/// disk, per-frame Hide / Show is a per-context, undoable, JSON-stable layout property,
/// and the phase-1 draw sites are routed through the registry and honour Hidden (source ratchet).
///
/// Run standalone: interface-wire-check --hud-layout-only
/// </summary>
internal static class HudLayoutClinicalChecks
{
    private static readonly Vector2[] Displays = [new(1600f, 900f), new(3840f, 2160f)];

    public static void Run()
    {
        RunPlacement();
        RunSnap();
        RunSession();
        RunVisibility();
        RunMigration();
        RunSourceRatchet();
    }

    // ── placement law ────────────────────────────────────────────────────────────────────

    private static void RunPlacement()
    {
        Vector2 size = new(200f, 50f);
        foreach (Vector2 display in Displays)
            foreach (HudAnchor anchor in Enum.GetValues<HudAnchor>())
                foreach (HudAnchor pivot in Enum.GetValues<HudAnchor>())
                {
                    var placement = new HudPlacement(anchor, pivot, 13f, -7f);
                    Vector2 origin = HudLayoutLaw.Resolve(placement, Vector2.Zero, display, size);
                    Check(float.IsFinite(origin.X) && float.IsFinite(origin.Y),
                        $"Resolve produced a non-finite origin for {anchor}/{pivot} at {display}");
                    // Re-expressing the same rect against the SAME anchor must not move it.
                    HudPlacement again = HudLayoutLaw.Reanchor(origin, size, display, anchor);
                    Vector2 back = HudLayoutLaw.Resolve(again, Vector2.Zero, display, size);
                    Check((back - origin).Length() < 1e-3f,
                        $"Resolve/Reanchor round-trip moved the rect for {anchor}/{pivot} at {display}");
                    Check(again.Anchor == anchor && again.Pivot == anchor,
                        "Reanchor must pivot on the anchor it was given");
                }

        foreach (Vector2 display in Displays)
        {
            (Vector2 Origin, HudAnchor Expected)[] parked =
            [
                (Vector2.Zero, HudAnchor.TopLeft),
                (new Vector2((display.X - size.X) * .5f, 0f), HudAnchor.Top),
                (new Vector2(display.X - size.X, 0f), HudAnchor.TopRight),
                (new Vector2(0f, (display.Y - size.Y) * .5f), HudAnchor.Left),
                ((display - size) * .5f, HudAnchor.Center),
                (new Vector2(display.X - size.X, (display.Y - size.Y) * .5f), HudAnchor.Right),
                (new Vector2(0f, display.Y - size.Y), HudAnchor.BottomLeft),
                (new Vector2((display.X - size.X) * .5f, display.Y - size.Y), HudAnchor.Bottom),
                (display - size, HudAnchor.BottomRight),
            ];
            foreach ((Vector2 origin, HudAnchor expected) in parked)
            {
                Check(HudLayoutLaw.NearestAnchor(origin, size, display) == expected,
                    $"NearestAnchor drift: a frame parked at {expected} on {display} was not read as {expected}");
                HudPlacement re = HudLayoutLaw.Reanchor(origin, size, display);
                Vector2 back = HudLayoutLaw.Resolve(re, Vector2.Zero, display, size);
                Check((back - origin).Length() < 1e-3f,
                    $"re-anchoring on drop moved a frame parked at {expected} on {display}");
            }
        }

        // A child's offset is measured from its parent's rect.
        Vector2 parentMin = new(1000f, 700f), parentSize = new(176f, 60f);
        Vector2 child = HudLayoutLaw.Resolve(
            HudPlacement.At(HudAnchor.TopRight, 0f, -8f, pivot: HudAnchor.BottomRight),
            parentMin, parentSize, new Vector2(330f, 112f));
        Check(child == new Vector2(1000f + 176f - 330f, 700f - 8f - 112f),
            "child placement must resolve against the parent's rect (control guide over the tablet)");

        // Clamp: never throws on a degenerate display, pins a normal frame to the edges.
        foreach (Vector2 degenerate in new[] { new Vector2(1f, 1f), Vector2.Zero, new Vector2(float.NaN, 5f) })
        {
            Vector2 c = HudLayoutLaw.Clamp(new Vector2(500f, 500f), size, degenerate);
            Check(float.IsFinite(c.X) && float.IsFinite(c.Y),
                $"Clamp degenerated on a {degenerate} display");
        }
        Check(HudLayoutLaw.Clamp(new Vector2(-40f, 5000f), size, new Vector2(1600f, 900f)) ==
              new Vector2(0f, 850f),
            "Clamp must pin an off-screen box to the screen edges");
        Check(HudLayoutLaw.Clamp(new Vector2(float.PositiveInfinity, 10f), size, new Vector2(1600f, 900f)).X
              is >= 0f and <= 1400f,
            "Clamp must swallow an infinite coordinate");

        // Nudge and grid cycling.
        Check(HudLayoutLaw.Nudge(new Vector2(5f, 5f), 1, -1, large: false) == new Vector2(6f, 4f) &&
              HudLayoutLaw.Nudge(new Vector2(5f, 5f), 1, -1, large: true) == new Vector2(15f, -5f),
            "Nudge step drift (1 / Shift 10)");
        Check(HudLayoutLaw.NextGridSize(8) == 16 && HudLayoutLaw.NextGridSize(16) == 32 &&
              HudLayoutLaw.NextGridSize(32) == 8 && HudLayoutLaw.NextGridSize(7) == 8,
            "grid size must cycle 8 / 16 / 32");
    }

    // ── snapping ─────────────────────────────────────────────────────────────────────────

    private static void RunSnap()
    {
        Vector2 display = new(1600f, 900f);
        Vector2 size = new(100f, 40f);
        var others = new List<HudLayoutLaw.SnapBox>
        {
            new(new Vector2(300f, 300f), new Vector2(120f, 60f)),   // right edge at 420
        };

        // 4 px from the other frame's right edge: the frame edge wins over the grid line at 416.
        HudLayoutLaw.SnapResult r = HudLayoutLaw.Snap(new Vector2(424f, 500f), size, display, others,
            snapToFrames: true, snapToGrid: true, gridSize: 16);
        Check(MathF.Abs(r.Origin.X - 420f) < 1e-3f &&
              r.Guides.Any(g => g.Vertical && MathF.Abs(g.At - 420f) < 1e-3f),
            "Snap must prefer a frame edge over a grid line inside the threshold");

        // Beyond 6 px on both axes, with the grid off: raw origin, no guides.
        r = HudLayoutLaw.Snap(new Vector2(437f, 507f), size, display, others,
            snapToFrames: true, snapToGrid: false, gridSize: 16);
        Check(r.Origin == new Vector2(437f, 507f) && r.Guides.Count == 0,
            "Snap beyond the threshold must return the raw origin with no guides");

        // Grid only: the closest of the box's three edges per axis decides (left 437->432,
        // bottom 547->544 is 3 away, centre 527->528 is 1 away: the centre wins -> +1).
        r = HudLayoutLaw.Snap(new Vector2(437f, 507f), size, display, others,
            snapToFrames: false, snapToGrid: true, gridSize: 16);
        Check(MathF.Abs(r.Origin.X - 432f) < 1e-3f && MathF.Abs(r.Origin.Y - 508f) < 1e-3f &&
              r.Guides.Count == 2,
            "grid snapping must pick the nearest edge/centre per axis");

        // Screen centre outranks the grid: a box centred at 802 lands on 800.
        r = HudLayoutLaw.Snap(new Vector2(752f, 100f), size, display, [],
            snapToFrames: false, snapToGrid: true, gridSize: 16);
        Check(MathF.Abs(r.Origin.X - 750f) < 1e-3f &&
              r.Guides.Any(g => g.Vertical && MathF.Abs(g.At - 800f) < 1e-3f),
            "screen centre must outrank the grid");

        // Alt (no snapping) is the caller's job; the law with everything off still honours the
        // screen edges, which every layout tool does.
        r = HudLayoutLaw.Snap(new Vector2(3f, 3f), size, display, [],
            snapToFrames: false, snapToGrid: false, gridSize: 16);
        Check(r.Origin == Vector2.Zero, "screen edges must snap even with frames and grid off");
    }

    // ── edit session ─────────────────────────────────────────────────────────────────────

    private static void RunSession()
    {
        var live = new HudLayoutSettings();
        HudEditSession session = HudLayoutEditLaw.Begin(live, HudLayoutContext.Command, null);
        Check(session.Snapshot.Layouts.Count == 0 && HudLayoutLaw.IsDefaultActive(session.Snapshot),
            "entering a session must snapshot the pre-edit block");

        HudPlacement[] steps =
        [
            HudPlacement.At(HudAnchor.TopLeft, 10f, 10f),
            HudPlacement.At(HudAnchor.Top, 0f, 20f),
            HudPlacement.At(HudAnchor.TopRight, -30f, 40f),
        ];
        foreach (HudPlacement step in steps)
            session.Push(HudLayoutEditLaw.SetPlacement(live, HudLayoutContext.Command, "minimap", step));
        Check(live.ActiveLayout == HudLayoutLaw.CustomLayoutName &&
              HudLayoutLaw.Override(live, HudLayoutContext.Command, "minimap") == steps[2],
            "editing Default must fork to Custom and land the last placement");
        Check(HudLayoutLaw.Override(live, HudLayoutContext.Body, "minimap") is null,
            "Command edits must not leak into the Body layout");
        Check(session.Snapshot.Layouts.Count == 0, "the entry snapshot must not follow live edits");

        for (int i = 0; i < 3; i++)
        {
            HudEditChange? change = session.Undo();
            Check(change is not null, "undo stack shorter than the drag sequence");
            HudLayoutEditLaw.Apply(live, change!, undo: true);
        }
        Check(HudLayoutLaw.Override(live, HudLayoutContext.Command, "minimap") is null && !session.CanUndo,
            "undo x3 must restore the authored placement");
        for (int i = 0; i < 3; i++)
        {
            HudEditChange? change = session.Redo();
            Check(change is not null, "redo stack shorter than the drag sequence");
            HudLayoutEditLaw.Apply(live, change!, undo: false);
        }
        Check(HudLayoutLaw.Override(live, HudLayoutContext.Command, "minimap") == steps[2] && !session.CanRedo,
            "redo x3 must land the last placement again");

        // A new change after an undo discards the redo branch.
        HudLayoutEditLaw.Apply(live, session.Undo()!, undo: true);
        session.Push(HudLayoutEditLaw.SetPlacement(live, HudLayoutContext.Command, "chat",
            HudPlacement.At(HudAnchor.Bottom, 0f, -40f)));
        Check(!session.CanRedo, "a fresh change must truncate the redo branch");

        // Reset all clears the context in one undoable step.
        HudEditChange? reset = HudLayoutEditLaw.ResetAll(live, HudLayoutContext.Command);
        Check(reset is not null && reset.Entries.Count == 2 &&
              HudLayoutLaw.Overrides(live, HudLayoutContext.Command)!.Count == 0,
            "Reset all must clear every override in the context");
        HudLayoutEditLaw.Apply(live, reset!, undo: true);
        Check(HudLayoutLaw.Override(live, HudLayoutContext.Command, "minimap") == steps[1],
            "undoing Reset all must restore every override");
        Check(HudLayoutEditLaw.ResetAll(new HudLayoutSettings(), HudLayoutContext.Body) is null,
            "Reset all on Default must be a no-op");

        // Layout cycling: Default -> Custom -> Default.
        Check(HudLayoutLaw.NextLayoutName(live) == HudLayoutLaw.DefaultLayoutName,
            "layout cycle from Custom must return to Default");
        live.ActiveLayout = HudLayoutLaw.DefaultLayoutName;
        Check(HudLayoutLaw.NextLayoutName(live) == HudLayoutLaw.CustomLayoutName &&
              HudLayoutLaw.Override(live, HudLayoutContext.Command, "minimap") is null,
            "Default must be immutable: no overrides while it is active");

        // Drag geometry and card side.
        session.DragStartOrigin = new Vector2(100f, 100f);
        session.DragStartMouse = new Vector2(500f, 500f);
        Check(HudLayoutEditLaw.DragOrigin(session, new Vector2(540f, 480f), 2f) == new Vector2(120f, 90f),
            "drag origin must divide the pointer delta by the UI scale");
        Check(HudLayoutEditLaw.CardOnLeft(new Vector2(1200f, 400f), new Vector2(1600f, 900f)) &&
              !HudLayoutEditLaw.CardOnLeft(new Vector2(200f, 400f), new Vector2(1600f, 900f)),
            "the settings card must sit at the edge farthest from the selection");
    }

    // ── visibility (Hide / Show) ─────────────────────────────────────────────────────────

    private static void RunVisibility()
    {
        var live = new HudLayoutSettings();
        HudEditSession session = HudLayoutEditLaw.Begin(live, HudLayoutContext.Body, null);
        Check(!HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap") &&
              HudLayoutLaw.Hidden(live, HudLayoutContext.Body) is null,
            "Default must hide nothing");

        HudEditChange? hide = HudLayoutEditLaw.SetHidden(live, HudLayoutContext.Body, "minimap", true);
        Check(hide is not null && hide.Entries.Count == 1 && hide.Entries[0].IsVisibility &&
              hide.Entries[0].HiddenBefore == false && hide.Entries[0].HiddenAfter == true &&
              live.ActiveLayout == HudLayoutLaw.CustomLayoutName &&
              HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap"),
            "hiding on Default must fork to Custom and hide the frame");
        Check(!HudLayoutLaw.IsHidden(live, HudLayoutContext.Command, "minimap"),
            "hidden is per context: a Body hide must not reach the Command View");
        Check(HudLayoutEditLaw.SetHidden(live, HudLayoutContext.Body, "minimap", true) is null,
            "hiding an already hidden frame is not a change");
        session.Push(hide!);
        HudLayoutEditLaw.Apply(live, session.Undo()!, undo: true);
        Check(!HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap"), "undo must show the frame again");
        HudLayoutEditLaw.Apply(live, session.Redo()!, undo: false);
        Check(HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap"), "redo must hide it again");
        Check(session.Snapshot.Layouts.Count == 0, "the entry snapshot must not follow visibility edits");

        // Reset all clears hidden flags together with the overrides, in one undoable step.
        session.Push(HudLayoutEditLaw.SetPlacement(live, HudLayoutContext.Body, "chat",
            HudPlacement.At(HudAnchor.Bottom, 0f, -40f)));
        HudEditChange? reset = HudLayoutEditLaw.ResetAll(live, HudLayoutContext.Body);
        Check(reset is not null && reset.Entries.Count == 2 &&
              !HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap") &&
              HudLayoutLaw.Overrides(live, HudLayoutContext.Body)!.Count == 0,
            "Reset all must clear the hidden flags with the overrides");
        HudLayoutEditLaw.Apply(live, reset!, undo: true);
        Check(HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap") &&
              HudLayoutLaw.Override(live, HudLayoutContext.Body, "chat") is not null,
            "undoing Reset all must restore the hidden flags");

        HudLayoutSettings clone = live.Clone();
        clone.Layouts[0].BodyHidden.Clear();
        Check(HudLayoutLaw.IsHidden(live, HudLayoutContext.Body, "minimap"),
            "Clone must deep-copy the hidden sets (Revert relies on it)");

        var json = new JsonSerializerOptions { WriteIndented = false };
        string text = JsonSerializer.Serialize(live, json);
        Check(text.Contains("\"BodyHidden\":[\"minimap\"]", StringComparison.Ordinal),
            "a hidden set must serialize as a string array");
        HudLayoutSettings back = JsonSerializer.Deserialize<HudLayoutSettings>(text, json)!;
        Check(HudLayoutLaw.IsHidden(back, HudLayoutContext.Body, "minimap") &&
              !HudLayoutLaw.IsHidden(back, HudLayoutContext.Command, "minimap"),
            "hidden sets must round-trip through JSON");
        HudLayoutSettings old = JsonSerializer.Deserialize<HudLayoutSettings>(
            "{\"ActiveLayout\":\"Custom\",\"Layouts\":[{\"Name\":\"Custom\"}]}", json)!;
        Check(!HudLayoutLaw.IsHidden(old, HudLayoutContext.Body, "minimap") &&
              old.Layouts[0].BodyHidden.Count == 0 && old.Layouts[0].CommandHidden.Count == 0,
            "a layout saved before Hide / Show must read as nothing hidden");
    }

    // ── migration ────────────────────────────────────────────────────────────────────────

    private static void RunMigration()
    {
        var s = new HudLayoutSettings { ChatUnlocked = true, ChatOffsetX = 40f, ChatOffsetY = -30f };
        HudLayoutLaw.Migrate11To12(s);
        Check(s.ActiveLayout == HudLayoutLaw.CustomLayoutName && s.Layouts.Count == 1,
            "Migrate11To12 must create and activate a Custom layout for a dragged chat frame");
        HudPlacement? body = HudLayoutLaw.Override(s, HudLayoutContext.Body, HudLayoutLaw.ChatFrameId);
        HudPlacement? command = HudLayoutLaw.Override(s, HudLayoutContext.Command, HudLayoutLaw.ChatFrameId);
        Check(body == HudPlacement.At(HudAnchor.BottomLeft, 72f, -115f) &&
              command == HudPlacement.At(HudAnchor.BottomLeft, 72f, -239f),
            "migrated chat placement drift (authored BOTTOMLEFT (32,85) + offset; Command lifted 124)");
        Check(!s.ChatUnlocked && s.ChatOffsetX == 0f && s.ChatOffsetY == 0f,
            "Migrate11To12 must zero the legacy keys");

        var json = new JsonSerializerOptions { WriteIndented = false };
        string text = JsonSerializer.Serialize(s, json);
        Check(!text.Contains("ChatOffset", StringComparison.Ordinal) &&
              !text.Contains("ChatUnlocked", StringComparison.Ordinal) &&
              text.Contains("\"BottomLeft\"", StringComparison.Ordinal) &&
              text.Contains("\"Custom\"", StringComparison.Ordinal),
            "migrated settings must serialize without legacy keys and with string anchors");
        HudLayoutSettings back = JsonSerializer.Deserialize<HudLayoutSettings>(text, json)!;
        Check(HudLayoutLaw.Override(back, HudLayoutContext.Command, HudLayoutLaw.ChatFrameId) == command &&
              back.GridSize == 16 && back.SnapToGrid && back.SnapToFrames,
            "HudLayoutSettings must round-trip through JSON");
        // The legacy keys still READ, so a Version 11 file migrates.
        HudLayoutSettings legacy = JsonSerializer.Deserialize<HudLayoutSettings>(
            "{\"ChatUnlocked\":true,\"ChatOffsetX\":5,\"ChatOffsetY\":6}", json)!;
        Check(legacy.ChatUnlocked && legacy.ChatOffsetX == 5f && legacy.ChatOffsetY == 6f,
            "Version 11 chat keys must still deserialize for the migration");

        var none = new HudLayoutSettings();
        HudLayoutLaw.Migrate11To12(none);
        Check(none.Layouts.Count == 0 && HudLayoutLaw.IsDefaultActive(none),
            "a zero chat offset must not create a layout");

        // The client's own settings file version carries the step.
        string settingsSource = Source("MSUIClient/Engine/GameSettings.cs");
        Check(settingsSource.Contains("public int Version { get; set; } = 13;", StringComparison.Ordinal) &&
              settingsSource.Contains("HudLayoutLaw.Migrate11To12(s.HudLayout)", StringComparison.Ordinal),
            "GameSettings must be Version 13 with the HudLayout migration step");
    }

    // ── source ratchet ───────────────────────────────────────────────────────────────────

    private static void RunSourceRatchet()
    {
        (string File, string Must, string MustNot)[] sites =
        [
            ("MSUIClient/GameLoop/Hud/GameLoop.CommandShelf.cs",
                "HudFrame(\"command-shelf\"", "display.Y - 12f * scale"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs",
                "HudFrame(\"control-group-rail\"", "78f * scale), ImGuiCond.Always"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs",
                "HudFrame(\"command-palette\"", "display.X - 338f * scale"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs",
                "HudFrame(\"control-groups-restore\"", "display.X - (175f * scale)"),
            ("MSUIClient/GameLoop/Scene/GameLoop.Control.cs",
                "HudFrame(\"angle-knob\"", "io.DisplaySize.X - 20f, io.DisplaySize.Y - 20f"),
            ("MSUIClient/GameLoop/Scene/GameLoop.Control.cs",
                "HudFrame(\"control-guide\"", ""),
            ("MSUIClient/GameLoop/Hud/GameLoop.Minimap.cs",
                "HudFrame(\"minimap\"", "logicalDisplay.Y - 200f"),
            ("MSUIClient/GameLoop/Panels/GameLoop.Chat.cs",
                "HudFrame(HudLayoutLaw.ChatFrameId", "DrawChatMover"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsTerritory.cs",
                "HudFrame(\"territory-strip\"", "72f * s);"),
            ("MSUIClient/GameLoop/Panels/GameLoop.Companions.cs",
                "HudFrame(\"companions\"", ""),
            ("MSUIClient/GameLoop/Hud/GameLoop.PartyFrames.cs",
                "HudFrame(\"party-frames\"", "PartyFrameUiLaw.FirstX * s, PartyFrameUiLaw.FirstY * s"),
            ("MSUIClient/GameLoop/Hud/GameLoop.UnitFrames.cs",
                "HudFrame(playerFrame ? \"player-frame\" : \"target-frame\"",
                "Vector2 p = authoredOrigin * s;\n        Vector2 size = new(232, 100);"),
        ];
        foreach ((string file, string must, string mustNot) in sites)
        {
            string source = Source(file);
            Check(source.Contains(must, StringComparison.Ordinal),
                $"{file} is not routed through the HUD layout registry ({must} missing)");
            Check(mustNot.Length == 0 || !source.Contains(mustNot, StringComparison.Ordinal),
                $"{file} still carries its old inline position ({mustNot})");
        }

        // Every hideable draw site honours the registry's Hidden flag; the two panels the
        // player opens on purpose (companions, the control-group palette) opt out instead.
        (string File, string Must)[] hideSites =
        [
            ("MSUIClient/GameLoop/Hud/GameLoop.CommandShelf.cs", "if (shelf.Hidden) return;"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs", "if (rail.Hidden)"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs", "if (restore.Hidden) return;"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs", "_rtsControlGroupPaletteSize, hideable: false"),
            ("MSUIClient/GameLoop/Scene/GameLoop.Control.cs", "if (knob.Hidden) return 0f;"),
            ("MSUIClient/GameLoop/Scene/GameLoop.Control.cs", "if (guide.Hidden) return;"),
            ("MSUIClient/GameLoop/Hud/GameLoop.Minimap.cs", "if (minimapFrame.Hidden)"),
            ("MSUIClient/GameLoop/Panels/GameLoop.Chat.cs", "if (chatFrame.Hidden)"),
            ("MSUIClient/GameLoop/Hud/GameLoop.RtsTerritory.cs", "if (strip.Hidden) return;"),
            ("MSUIClient/GameLoop/Panels/GameLoop.Companions.cs", "hideable: false"),
            ("MSUIClient/GameLoop/Hud/GameLoop.PartyFrames.cs", "if (partyFrame.Hidden)"),
            ("MSUIClient/GameLoop/Hud/GameLoop.UnitFrames.cs", "if (unitFrame.Hidden) return;"),
        ];
        foreach ((string file, string must) in hideSites)
            Check(Source(file).Contains(must, StringComparison.Ordinal),
                $"{file} does not honour the HUD frame's Hidden flag ({must} missing)");
        // Hidden chat still takes a typed line.
        {
            string chat = Source("MSUIClient/GameLoop/Panels/GameLoop.Chat.cs");
            int chatGate = chat.IndexOf("if (chatFrame.Hidden)", StringComparison.Ordinal);
            Check(chatGate >= 0 && chat.IndexOf("if (_chatEditOpen) DrawChatEditBox(dl, root, s);", chatGate,
                      StringComparison.Ordinal) is int box && box - chatGate < 400,
                "a hidden chat frame must still draw its edit box");
        }
        // The target frame exists for the editor even with nothing targeted.
        Check(Source("MSUIClient/GameLoop/Combat/GameLoop.Targeting.cs")
                  .Contains("_hudEditMode ? ControlledGuid : 0", StringComparison.Ordinal),
            "DrawTargetFrame must stand the controlled character in as the target in Edit Mode");

        // The party stack's satellites (medallions, chain rail, drag feedback) follow the frame.
        string botBars = Source("MSUIClient/GameLoop/Hud/GameLoop.BotBars.cs");
        Check(!botBars.Contains("PartyFrameUiLaw.MemberY(", StringComparison.Ordinal) &&
              botBars.Contains("PartyMemberLogicalOrigin(", StringComparison.Ordinal) &&
              botBars.Contains("_playerFrameOrigin", StringComparison.Ordinal),
            "bot-bar overlays must measure from the resolved party/player frame origins");

        // Gates: edge pan, marquee and order dispatch stand down while editing.
        string control = Source("MSUIClient/GameLoop/Scene/GameLoop.Control.cs");
        int edgePan = control.IndexOf("private void UpdateFreeCamEdgePan()", StringComparison.Ordinal);
        Check(edgePan >= 0 && control.IndexOf("_hudEditMode", edgePan, StringComparison.Ordinal) is int gate &&
              gate >= 0 && gate - edgePan < 1200,
            "GameLoop.Control.cs must gate UpdateFreeCamEdgePan on _hudEditMode");
        int clickDrain = control.IndexOf("private void HandleFreeCamWorldClick(", StringComparison.Ordinal);
        Check(clickDrain >= 0 && control.IndexOf("if (_hudEditMode) return;", clickDrain, StringComparison.Ordinal)
                  is int drainGate && drainGate - clickDrain < 400,
            "GameLoop.Control.cs must gate HandleFreeCamWorldClick on _hudEditMode");
        Check(control.Contains("!_commanderMapOpen && !_hudEditMode", StringComparison.Ordinal),
            "GameLoop.Control.cs must gate the marquee on _hudEditMode");

        // Draw order: registry cleared at the top of DrawCombatHud, overlay after the HIGH
        // stratum (multibars) and before popups.
        string hud = Source("MSUIClient/GameLoop/Combat/GameLoop.CombatFeedback.cs");
        int registry = hud.IndexOf("BeginHudFrameRegistry();", StringComparison.Ordinal);
        int multibars = hud.IndexOf("DrawMultiActionBars();", StringComparison.Ordinal);
        int editor = hud.IndexOf("DrawHudLayoutEditor();", StringComparison.Ordinal);
        int death = hud.IndexOf("DrawDeathRezFrame();", StringComparison.Ordinal);
        Check(registry >= 0 && multibars > registry && editor > multibars && death > editor,
            "DrawCombatHud must clear the registry first and draw the editor after the multibars, before popups");

        // Escape, binding row, slash command, options entry.
        Check(Source("MSUIClient/GameLoop/Panels/GameLoop.Settings.cs")
                  .Contains("ConsumeHudEditEscape()", StringComparison.Ordinal),
            "the Escape ladder must spend Escape on HUD Edit Mode first");
        Check(Source("MSUIClient/GameLoop/Panels/GameLoop.Settings.cs")
                  .Contains("BeginBox(\"hud-layout\"", StringComparison.Ordinal),
            "Options must offer Edit HUD layout");
        Check(Source("MSUIClient/GameLoop/Panels/GameLoop.Bindings.cs")
                  .Contains("GameBinding.ToggleHudEditMode, \"Edit HUD Layout\"", StringComparison.Ordinal),
            "Key Bindings must list Edit HUD Layout");
        Check(Source("MSUIClient/GameLoop/Panels/GameLoop.Chat.cs")
                  .Contains("case \"/editui\"", StringComparison.Ordinal),
            "/editui must reach the editor");
        Check(Source("MSUIClient/Program.cs").Contains("UpdateHudEditInput(typing);", StringComparison.Ordinal),
            "Program.Update must poll the Edit HUD Layout binding");

        // The editor is vanilla chrome only, and enrolled in the ImGui-policy ratchet.
        string editorSource = Source("MSUIClient/GameLoop/Hud/GameLoop.HudLayoutEditor.cs");
        IReadOnlyList<GameplayImguiPolicyLaw.Usage> usages = GameplayImguiPolicyLaw.Scan(editorSource);
        Check(usages.Count == 0, GameplayImguiPolicyLaw.Describe("GameLoop.HudLayoutEditor.cs", usages));
        Check(Source("tools/interface-wire-check/GameplayImguiPolicyClinicalChecks.cs")
                  .Contains("GameLoop.HudLayoutEditor.cs", StringComparison.Ordinal),
            "GameLoop.HudLayoutEditor.cs must be enrolled in the ImGui-policy clean list");
        Check(!editorSource.Contains("ImGuiWindowFlags.NoBringToFrontOnFocus", StringComparison.Ordinal),
            "the overlay must never carry NoBringToFrontOnFocus (it has to be display-front)");
        Check(editorSource.Contains("##hud-edit-hide", StringComparison.Ordinal) &&
              editorSource.Contains("ImGuiKey.H, false", StringComparison.Ordinal) &&
              editorSource.Contains("\" (hidden)\"", StringComparison.Ordinal),
            "the editor must offer Hide / Show (card button, H key) and mark hidden frames");

        // The laws never reach into GameLoop (CODE_STRUCTURE_LAW section 1).
        foreach (string law in new[] { "MSUIClient/Engine/UI/HudLayoutLaw.cs", "MSUIClient/Engine/UI/HudLayoutEditLaw.cs" })
            Check(!Source(law).Contains("GameLoop.", StringComparison.Ordinal) &&
                  !Source(law).Contains("ImGui", StringComparison.Ordinal),
                $"{law} must stay pure (no GameLoop / ImGui reference)");
    }

    private static string Source(string relative)
    {
        string root = ClientConfig.FindRepoRoot();
        return SourceText.Read(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
