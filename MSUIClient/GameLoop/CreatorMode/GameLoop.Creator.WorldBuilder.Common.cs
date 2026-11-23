using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: shared human-tool pieces (shared_docs/WORLD_BUILDER.md §4, 2026-10-14 usability pass).
//
//   * Pick a spot: any page asks "click the ground for X" (WbBeginPick); the pointer shows the grid and an optional
//     radius ring, Escape cancels. Replaces every "use where I stand" input - with the fly camera the controller is
//     the camera rig, so "where I stand" was a point in the air.
//   * Markers: pages announce in-world pins/rings for what they are editing (WbMarker); drawn the next frame.
//   * Confirm: destructive buttons ask first (WbConfirmButton).
//   * Undo: one button for every page that says WHAT it will undo and why it cannot right now (WbUndoButton).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private string _wbPickWhat = "";
    private Action<Vector3>? _wbPickDone;
    private float _wbPickRadius;
    private readonly List<(Vector3 At, string Label, uint Colour, float Radius)> _wbMarkers = new(), _wbMarkersNext = new();

    private string? _wbConfirmId;
    private string _wbConfirmQuestion = "";

    // last undoable op of the selected pack (label), refreshed after every saved edit
    private Task<string>? _wbLastOpTask;
    private int _wbLastOpPack = -1;
    private string _wbLastOpLabel = "";
    private bool _wbLastOpStale = true;
    private string? _wbPendingUndoMessage;

    // ── pick a spot ──────────────────────────────────────────────────────────

    /// <summary>Arm the pick tool: the next ground click calls <paramref name="done"/>. <paramref name="radius"/> &gt; 0 draws
    /// that ring around the pointer (a trigger area, a subzone).</summary>
    private void WbBeginPick(string what, Action<Vector3> done, float radius = 0f)
    {
        WbSetTool(WorldBuilderTool.Pick);
        _wbPickWhat = what;
        _wbPickDone = done;
        _wbPickRadius = radius;
        _wbMessage = $"Click the ground: {what}. Escape cancels.";
    }

    private void WbPickFrame(ImGuiIOPtr io, bool overUi)
    {
        if (overUi || _wbCursor is not { } at || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        var done = _wbPickDone;
        string what = _wbPickWhat;
        WbSetTool(WorldBuilderTool.None);
        _wbPickDone = null;
        done?.Invoke(at);
        if (_wbMessage.StartsWith("Click the ground:", StringComparison.Ordinal)) _wbMessage = $"Picked {what}.";
    }

    /// <summary>A button that arms the pick tool, shown as "Picking..." while armed.</summary>
    private bool WbPickButton(string label, string what, Action<Vector3> done, float radius = 0f)
    {
        // On the Quest Tabletop the pick lands on the board (GameLoop.Creator.Tabletop.Edit.cs), not the game world.
        if (TabletopActive) return TtGroundPickButton(label.Replace("on the ground", "on the board", StringComparison.Ordinal), what, done);
        bool armed = _wbTool == WorldBuilderTool.Pick && _wbPickWhat == what;
        bool clicked = CreatorButton(armed ? "Click the ground... (Esc)" : label);
        ObserveWorldBuilderUiItem(label);
        if (clicked)
        {
            if (armed) WbSetTool(WorldBuilderTool.None);
            else WbBeginPick(what, done, radius);
        }
        return clicked;
    }

    // ── markers ──────────────────────────────────────────────────────────────

    /// <summary>Show a labelled pin (and a ground ring when <paramref name="radius"/> &gt; 0) while the page that calls this is open.</summary>
    private void WbMarker(Vector3 at, string label, uint colour = 0xFF40D0FFu, float radius = 0f) =>
        _wbMarkersNext.Add((at, label, colour, radius));

    private readonly List<(int Col, int Row, int Wide, int Tall, uint Colour, string Label)> _wbOutlines = new(), _wbOutlinesNext = new();

    /// <summary>Outline whole ADT tiles on the ground (a terrain selection, a destination) while the calling page is open.</summary>
    private void WbTileOutline(int col, int row, int wide, int tall, string label, uint colour = 0xFF40D0FFu) =>
        _wbOutlinesNext.Add((col, row, Math.Max(1, wide), Math.Max(1, tall), colour, label));

    private void WbDrawMarkers()
    {
        _wbMarkers.Clear();
        _wbMarkers.AddRange(_wbMarkersNext);
        _wbMarkersNext.Clear();
        _wbOutlines.Clear();
        _wbOutlines.AddRange(_wbOutlinesNext);
        _wbOutlinesNext.Clear();
        if (_terrain is null || _creatorPanel != CreatorPanel.World) return;
        var draw = ImGui.GetBackgroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        foreach (var (col, row, wide, tall, colour, label) in _wbOutlines)
        {
            // Tile (c, r) spans world X ((32-r-1)T, (32-r)T], Y ((32-c-1)T, (32-c)T].
            float xN = (32 - row) * WorldBuilderLaw.Tile, xS = (32 - row - tall) * WorldBuilderLaw.Tile;
            float yW = (32 - col) * WorldBuilderLaw.Tile, yE = (32 - col - wide) * WorldBuilderLaw.Tile;
            var corners = new[] { new Vector2(xN, yW), new Vector2(xN, yE), new Vector2(xS, yE), new Vector2(xS, yW), new Vector2(xN, yW) };
            for (int e = 0; e < 4; e++)
            {
                Vector2? prev = null;
                for (int k = 0; k <= 64; k++)
                {
                    var p = Vector2.Lerp(corners[e], corners[e + 1], k / 64f);
                    float z = _terrain.SampleHeight(p.X, p.Y) ?? (_controller?.Position.Z ?? 0f);
                    Vector2? px = cam.TryProjectToScreen(new Vector3(p.X, p.Y, z + 1f), display, out var sp, out _) ? sp : null;
                    if (prev is { } a0 && px is { } a1) draw.AddLine(a0, a1, colour, 3f);
                    prev = px;
                }
            }
            var mid = (corners[0] + corners[2]) / 2f;
            if (cam.TryWorldToScreen(new Vector3(mid.X, mid.Y, (_terrain.SampleHeight(mid.X, mid.Y) ?? 0f) + 4f), display, out var lp))
                draw.AddText(lp, colour, label);
        }
        foreach (var (at, label, colour, radius) in _wbMarkers)
        {
            if (radius > 0f)
            {
                Vector2? prev = null;
                for (int i = 0; i <= 48; i++)
                {
                    float a = i / 48f * MathF.Tau;
                    float x = at.X + MathF.Cos(a) * radius, y = at.Y + MathF.Sin(a) * radius;
                    float z = _terrain.SampleHeight(x, y) ?? at.Z;
                    Vector2? px = cam.TryProjectToScreen(new Vector3(x, y, z + 0.3f), display, out var p, out _) ? p : null;
                    if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, colour, 2f);
                    prev = px;
                }
            }
            if (!cam.TryWorldToScreen(at + new Vector3(0, 0, 0.2f), display, out var foot) ||
                !cam.TryWorldToScreen(at + new Vector3(0, 0, 3.5f), display, out var head)) continue;
            draw.AddLine(foot, head, colour, 3f);
            draw.AddCircleFilled(head, 6f, colour);
            draw.AddCircleFilled(foot, 3f, colour);
            var size = ImGui.CalcTextSize(label);
            var tp = head - new Vector2(size.X / 2f, size.Y + 6f);
            draw.AddText(tp + Vector2.One, 0xE0000000u, label);
            draw.AddText(tp, 0xFFFFFFFFu, label);
        }
    }

    // ── confirm ──────────────────────────────────────────────────────────────

    private string? _wbConfirmApproved;   // label whose confirmation was accepted; the button returns true once

    /// <summary>A destructive button: the first click asks <paramref name="question"/> in one shared top-level dialog
    /// (WbDrawConfirmDialog); the button returns true once, on its next draw after "Yes". Drawing the dialog outside the
    /// page keeps it independent of the form state around the button.</summary>
    private bool WbConfirmButton(string label, string question)
    {
        if (_wbConfirmApproved == label) { _wbConfirmApproved = null; return true; }
        bool clicked = CreatorButton(label);
        ObserveWorldBuilderUiItem(label);
        if (clicked)
        {
            _wbConfirmId = label; _wbConfirmQuestion = question; _wbConfirmOpenPending = true;
            if (WbScriptPath is not null) Console.WriteLine($"[wb-ui] confirm asked: {label}");
        }
        return false;
    }

    private bool _wbConfirmOpenPending;

    /// <summary>The confirm dialog, drawn once per frame at the top level (UpdateWorldBuilder).</summary>
    private void WbDrawConfirmDialog()
    {
        if (_wbConfirmId is null) return;
        const string id = "Please confirm##wb-confirm";
        if (_wbConfirmOpenPending) { ImGui.OpenPopup(id); _wbConfirmOpenPending = false; }
        bool open = true;
        // Fixed width, height fitted to the text: an auto-resizing modal shrank to one word per line and clipped its buttons.
        float width = MathF.Min(ImGui.GetIO().DisplaySize.X * 0.8f, MathF.Max(560f, CreatorControlWidth * 2.2f));
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        PushCreatorStyle();
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
        {
            PopCreatorStyle();
            if (!open || !ImGui.IsPopupOpen(id)) _wbConfirmId = null;
            return;
        }
        ImGui.SetWindowFontScale(CreatorTextScale);
        ImGui.TextWrapped(_wbConfirmQuestion);
        ImGui.Spacing();
        if (CreatorButton("Yes, " + _wbConfirmId.Split("##")[0].ToLowerInvariant()))
        {
            if (WbScriptPath is not null) Console.WriteLine($"[wb-ui] confirmed: {_wbConfirmId}");
            _wbConfirmApproved = _wbConfirmId; _wbConfirmId = null; ImGui.CloseCurrentPopup();
        }
        ObserveWorldBuilderUiItem("Confirm");
        ImGui.SameLine();
        if (CreatorButton("Cancel")) { _wbConfirmId = null; ImGui.CloseCurrentPopup(); }
        ObserveWorldBuilderUiItem("Cancel");
        ImGui.SetWindowFontScale(1f);
        ImGui.EndPopup();
        PopCreatorStyle();
    }

    // ── undo ─────────────────────────────────────────────────────────────────

    /// <summary>Why Undo cannot run right now, or "" when it can.</summary>
    private string WbUndoProblem() =>
        _wbPackId == 0 ? "Choose a pack first."
        : _wbStroking ? "Finish the stroke first."
        : _wbSculptSaveTask is not null || _wbSculptAwaitingState || _wbOps.Count > 0 ? "Wait for saving to finish."
        : _wbLastOpLabel.Length == 0 && !_wbLastOpStale ? "Nothing to undo in this pack."
        : "";

    /// <summary>The one Undo button: names the pack's most recent edit (from any page) and undoes it.</summary>
    private void WbUndoButton()
    {
        WbPumpLastOp();
        string problem = WbUndoProblem();
        string label = _wbLastOpLabel.Length > 0 ? $"Undo: {WbShort(_wbLastOpLabel, 38)}" : "Undo last edit";
        ImGui.BeginDisabled(problem.Length > 0);
        if (CreatorButton(label + "##wb-undo")) WbUndo();
        ImGui.EndDisabled();
        ObserveWorldBuilderUiItem("Undo");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(problem.Length > 0 ? problem
                : "Undoes this pack's most recent saved edit, whichever page it was made on (Ctrl+Z).");
    }

    private static string WbShort(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void WbPumpLastOp()
    {
        if (_wbLastOpPack != _wbPackId) { _wbLastOpPack = _wbPackId; _wbLastOpLabel = ""; _wbLastOpStale = true; }
        if (_wbLastOpStale && _wbLastOpTask is null && _wbPackId > 0 && SuiWebAppUrl.Length > 0 && _wbOps.Count == 0)
        {
            _wbLastOpStale = false;
            _wbLastOpTask = _wbClient.OpsAsync(SuiWebAppUrl, _wbPackId, 40);
        }
        if (_wbLastOpTask is not { IsCompleted: true } t) return;
        _wbLastOpTask = null;
        try
        {
            // Undo reverses the newest op that is neither an undo itself nor already undone.
            var ops = JsonNode.Parse(t.Result)?["ops"] as JsonArray;
            var last = ops?.OfType<JsonObject>().FirstOrDefault(o => o["kind"]?.ToString() != "undo" && o["undoneBy"] is null);
            _wbLastOpLabel = last?["label"]?.ToString() ?? "";
        }
        catch { _wbLastOpLabel = ""; }
    }
}
