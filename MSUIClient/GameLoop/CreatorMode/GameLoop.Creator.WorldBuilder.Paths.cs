using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: the path maker (shared_docs/WORLD_BUILDER.md §4, "Paths").
//
// A path is a pack doc (kind "path", key = its name): a polyline of world points, a width and an edge blend.
// Publishing grades the ground along it after seams/coasts (web GradedPath) and clears props standing in the
// lane. This page is the human face of it: click points on the ground, drag them, insert/remove, see the road
// and its edges drawn in the world and the regraded ground PREVIEWED before saving; reopen and edit saved paths.
// The preview is one more layer in the terrain preview law (stock + server sculpt + stroke + path in the
// editor), computed with WorldBuilderLaw.PathWeight - the build's own rule.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private string? _wbPathEditKey;              // saved path open in the editor (null = a new path)
    private JsonObject? _wbPathEditBody;         // its saved body: fields this editor does not show are kept
    private int _wbPathSelected = -1;
    private bool _wbPathDragging;
    private float _wbPathDragLift;               // the dragged point's height above the ground, kept while dragging
    private bool _wbPathClear = true, _wbPathPreviewOn = true, _wbPathShowSaved = true;
    private readonly Dictionary<(int col, int row), float[]> _wbPathPreview = new();
    private string _wbPathPreviewSig = "";
    private double _wbPathPreviewAt;
    private bool _wbPathPreviewMissing;
    private bool _wbPathGrade;                   // level the ground along the route (a pass/ramp); off = follow the land
    private string? _wbRoadTexture, _wbRoadLastTexture;   // Tileset texture painted along the route; null = none
    private float _wbRoadWidth = 6f, _wbRoadSoft = 1.5f, _wbRoadStrength = 1f;
    private bool _wbRoadPickerOpen;
    // Road looks (2026-11-15; TerrainPaintLaw.RoadLook): the wandering edge, the patches, the pattern seed, the dirt
    // shoulder under the road, and the measured style they came from (shared_docs/ROAD_STYLES.md).
    private float _wbRoadRagged, _wbRoadPatchy;
    private int _wbRoadSeed;
    private string? _wbRoadStyleId;
    private bool _wbRoadStyleTuned;
    private string? _wbShoulderTexture, _wbShoulderLastTexture;
    private float _wbShoulderExtra = 3f, _wbShoulderSoft = 2f, _wbShoulderStrength = 1f;
    private bool _wbShoulderPickerOpen;
    private readonly byte[] _wbRoadStyleFilter = new byte[64];
    private List<(string Texture, float Weight)>? _wbRoadSample;   // the textures under the last "copy from the ground" click

    private bool WbPathPageOpen => _creatorPanel == CreatorPanel.World && _wbPage == "paths";

    private void DrawWbPathSection()
    {
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("Loading pack content..."); return; }
        int map = _config.Start.Map;
        float w = CreatorControlWidth;
        bool armed = _wbTool == WorldBuilderTool.Path;
        var step = new Vector4(1f, .82f, .3f, 1f);
        ImGui.TextWrapped("Make a road, trail or mountain pass: draw its route on the ground, give it a surface from the game's own ground textures, and optionally level the ground under it. Tip: Fly camera (top) gives a better view.");

        // 1. Route
        ImGui.Separator();
        ImGui.TextColored(step, "1. Draw the route");
        bool otherMap = _wbCarvePoints.Count > 0 && _wbCarveMap != map;
        if (otherMap) ImGui.TextWrapped($"The path in the editor is on map {_wbCarveMap}. Return there to edit it, or choose New path.");
        ImGui.BeginDisabled(otherMap);
        if (CreatorButton(armed ? "Stop drawing (Esc)" : _wbCarvePoints.Count == 0 ? "Start drawing" : "Continue drawing"))
            WbSetTool(armed ? WorldBuilderTool.None : WorldBuilderTool.Path);
        ObserveWorldBuilderUiItem("Path drawing");
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (CreatorButton("New path")) WbPathNew();
        ImGui.TextWrapped(armed
            ? "Click the ground to add points. Drag a point to move it, Ctrl+click inserts a point into the route, Delete removes the selected point, [ and ] change the width."
            : "Press Start drawing, then click on the ground where the road should go.");
        float length = 0f;
        for (int i = 1; i < _wbCarvePoints.Count; i++)
            length += Vector2.Distance(new(_wbCarvePoints[i].X, _wbCarvePoints[i].Y), new(_wbCarvePoints[i - 1].X, _wbCarvePoints[i - 1].Y));
        ImGui.TextDisabled(_wbCarvePoints.Count == 0 ? "No points yet." : Inv($"{_wbCarvePoints.Count} points, {length:F0} yd long"));
        if (_wbPathSelected >= _wbCarvePoints.Count) _wbPathSelected = _wbCarvePoints.Count - 1;
        if (_wbPathSelected >= 0)
        {
            var p = _wbCarvePoints[_wbPathSelected];
            ImGui.TextUnformatted(Inv($"Selected point {_wbPathSelected + 1}"));
            ImGui.SameLine();
            if (CreatorButton("Remove point")) WbPathRemoveSelected();
        }
        ImGui.BeginDisabled(_wbCarvePoints.Count == 0);
        if (CreatorButton("Reverse direction")) { _wbCarvePoints.Reverse(); _wbPathSelected = -1; }
        ImGui.SameLine();
        if (CreatorButton("Clear points")) { _wbCarvePoints.Clear(); _wbPathSelected = -1; }
        ImGui.EndDisabled();

        // 2. Surface
        ImGui.Separator();
        ImGui.TextColored(step, "2. Road surface");
        bool painted = _wbRoadTexture is not null;
        if (ImGui.Checkbox("Paint a road surface", ref painted))
        {
            if (!painted) _wbRoadTexture = null;
            else { _wbRoadTexture ??= _wbRoadLastTexture; _wbRoadPickerOpen = _wbRoadTexture is null; }
        }
        ObserveWorldBuilderUiItem("Paint a road surface");
        if (painted || _wbRoadPickerOpen)
        {
            DrawWbRoadStylePicker(w);
            if (_wbRoadTexture is { } chosen)
            {
                uint handle = _gameplayArt?.Handle(chosen) ?? 0;
                if (handle != 0) { ImGui.Image((nint)handle, new Vector2(40f, 40f) * CreatorUiScale); ImGui.SameLine(); }
                ImGui.TextWrapped(Path.GetFileNameWithoutExtension(chosen) + "\n" + Path.GetDirectoryName(chosen));
            }
            if (CreatorButton(_wbRoadPickerOpen ? "Hide textures" : "Choose texture")) _wbRoadPickerOpen = !_wbRoadPickerOpen;
            ObserveWorldBuilderUiItem("Choose texture");
            if (_wbRoadPickerOpen)
            {
                string? pick = _wbRoadTexture;
                if (DrawWbTexturePicker(ref pick) && pick is not null) { _wbRoadTexture = _wbRoadLastTexture = pick; _wbRoadPickerOpen = false; _wbRoadStyleId = null; }
            }
            bool tuned = false;
            ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Surface width (yd)", ref _wbRoadWidth, 1f, 40f, "%.1f");
            ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Soft edge (yd)", ref _wbRoadSoft, 0f, 10f, "%.1f");
            ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Strength", ref _wbRoadStrength, 0.1f, 1f, "%.2f");
            ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Ragged edge (yd)", ref _wbRoadRagged, 0f, 4f, "%.1f");
            ObserveWorldBuilderUiItem("Ragged edge");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("How far the road's edge wanders in and out, like a hand-painted vanilla road. 0 = a clean band.");
            ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Patchy", ref _wbRoadPatchy, 0f, 0.8f, "%.2f");
            ObserveWorldBuilderUiItem("Patchy");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much of the ground underneath shows through the road in patches (worn stones, dirt between them).");
            if ((_wbRoadRagged > 0f || _wbRoadPatchy > 0f) && _wbRoadSeed == 0) _wbRoadSeed = WbNewRoadSeed();
            if (_wbRoadRagged > 0f || _wbRoadPatchy > 0f)
            {
                if (CreatorButton("New pattern")) _wbRoadSeed = WbNewRoadSeed(_wbRoadSeed);
                ObserveWorldBuilderUiItem("New pattern");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Same look, a different wander of the edge and different patches.");
            }

            // The verge: a second texture (usually the zone's dirt) laid first, wider than the road.
            bool shoulder = _wbShoulderTexture is not null;
            if (ImGui.Checkbox("Shoulder under the road", ref shoulder))
            {
                if (!shoulder) _wbShoulderTexture = null;
                else { _wbShoulderTexture ??= _wbShoulderLastTexture; _wbShoulderPickerOpen = _wbShoulderTexture is null; }
                tuned = true;
            }
            ObserveWorldBuilderUiItem("Shoulder under the road");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Most vanilla roads run on a band of dirt that is wider than the stones. This paints it first.");
            if (shoulder || _wbShoulderPickerOpen)
            {
                ImGui.Indent(10f * CreatorUiScale);
                if (_wbShoulderTexture is { } verge)
                {
                    uint handle = _gameplayArt?.Handle(verge) ?? 0;
                    if (handle != 0) { ImGui.Image((nint)handle, new Vector2(32f, 32f) * CreatorUiScale); ImGui.SameLine(); }
                    ImGui.TextWrapped(Path.GetFileNameWithoutExtension(verge));
                }
                if (CreatorButton(_wbShoulderPickerOpen ? "Hide textures##verge" : "Choose shoulder texture")) _wbShoulderPickerOpen = !_wbShoulderPickerOpen;
                ObserveWorldBuilderUiItem("Choose shoulder texture");
                if (_wbShoulderPickerOpen)
                {
                    ImGui.PushID("verge");
                    string? pick = _wbShoulderTexture;
                    if (DrawWbTexturePicker(ref pick) && pick is not null) { _wbShoulderTexture = _wbShoulderLastTexture = pick; _wbShoulderPickerOpen = false; tuned = true; }
                    ImGui.PopID();
                }
                ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Shoulder extra (yd)", ref _wbShoulderExtra, 0.5f, 12f, "%.1f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much wider than the road the shoulder is, on each side.");
                ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Shoulder soft edge (yd)", ref _wbShoulderSoft, 0f, 10f, "%.1f");
                ImGui.SetNextItemWidth(w); tuned |= ImGui.SliderFloat("Shoulder strength", ref _wbShoulderStrength, 0.1f, 1f, "%.2f");
                ImGui.Unindent(10f * CreatorUiScale);
            }
            if (tuned && _wbRoadStyleId is not null) _wbRoadStyleTuned = true;   // still "based on" the style, now adjusted
            ImGui.TextDisabled("The road is painted on the ground right away, before saving.");
        }

        // 3. Ground shape
        ImGui.Separator();
        ImGui.TextColored(step, "3. Shape the ground (optional)");
        ImGui.Checkbox("Level the ground along the road", ref _wbPathGrade);
        ObserveWorldBuilderUiItem("Level the ground");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Grades the ground between the points: hills between them are cut and dips filled.\nUse it for passes, ramps and land bridges; leave it off for a road that follows the land.");
        if (_wbPathGrade)
        {
            ImGui.SetNextItemWidth(w); ImGui.SliderFloat("Level width (yd)", ref _wbCarveWidth, 2f, 60f, "%.0f");
            ImGui.SetNextItemWidth(w); ImGui.SliderFloat("Blend into the land (yd)", ref _wbCarveFalloff, 0f, 80f, "%.0f");
            ImGui.Checkbox("Preview the leveled ground", ref _wbPathPreviewOn);
            if (_wbPathPreviewOn && _wbPathPreviewMissing)
                ImGui.TextWrapped("The height preview needs the latest published build downloaded (Publish page, step 3). Saving still works.");
            if (_wbPathSelected >= 0)
            {
                var p = _wbCarvePoints[_wbPathSelected];
                float z = p.Z;
                ImGui.SetNextItemWidth(w * 0.6f);
                if (ImGui.InputFloat("Selected point height", ref z, 0.25f, 2f, "%.2f") && float.IsFinite(z))
                    _wbCarvePoints[_wbPathSelected] = p with { Z = z };
                if (CreatorButton("Put point on the ground")) _wbCarvePoints[_wbPathSelected] = p with { Z = WbBaseGroundZ(p.X, p.Y) ?? p.Z };
            }
            ImGui.BeginDisabled(_wbCarvePoints.Count < 3);
            if (CreatorButton("Even out the slope")) WbReplacePathPoints(WorldBuilderLaw.EvenGrade(_wbCarvePoints));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Keep the first and last heights and make one smooth climb between them.");
            ImGui.SameLine();
            ImGui.BeginDisabled(_wbCarvePoints.Count == 0);
            if (CreatorButton("All points on the ground"))
                for (int i = 0; i < _wbCarvePoints.Count; i++)
                    _wbCarvePoints[i] = _wbCarvePoints[i] with { Z = WbBaseGroundZ(_wbCarvePoints[i].X, _wbCarvePoints[i].Y) ?? _wbCarvePoints[i].Z };
            ImGui.EndDisabled();
        }
        ImGui.Checkbox("Remove trees and props on the road", ref _wbPathClear);

        // 4. Save
        ImGui.Separator();
        ImGui.TextColored(step, "4. Name and save");
        ImGui.SetNextItemWidth(w); ImGui.InputText("Path name", _wbCarveName, (uint)_wbCarveName.Length);
        ObserveWorldBuilderUiItem("Path name");
        string name = WbText(_wbCarveName).Trim();
        string? problem = WorldMapAuthoringLaw.PathProblem(name, _wbCarvePoints, _wbPathGrade ? _wbCarveWidth : _wbRoadWidth, _wbCarveFalloff);
        if (problem is null && _wbRoadTexture is null && !_wbPathGrade)
            problem = "Give the path a road surface, or tick Level the ground, before saving.";
        if (problem is null && name != _wbPathEditKey && WbPathDocs(_wbPackId).Any(d => (string?)d["docKey"] == name))
            problem = "Another path already uses this name. Choose a different name, or open that path below.";
        if (problem is null && (_wbRoadTexture is not null || !_wbPathGrade) && _wbState?.RoadSurfaceSupported != true)
            problem = "Painted roads preview here, but the web app cannot save them yet (it needs the road-surface update). " +
                      "To save today, untick Paint a road surface and tick Level the ground.";
        if (problem is null && _wbRoadTexture is not null && (_wbRoadRagged > 0f || _wbRoadPatchy > 0f || _wbShoulderTexture is not null) &&
            _wbState?.RoadLookSupported != true)
            problem = "This road's ragged edge, patches or shoulder preview here, but the web app would build it as a plain band " +
                      "(it needs the road-looks update). Set Ragged and Patchy to 0 and untick the shoulder to save it today.";
        if (problem is not null && _wbCarvePoints.Count > 0) ImGui.TextWrapped(problem);
        ImGui.BeginDisabled(problem is not null || otherMap || _wbOps.Count > 0);
        if (CreatorButton(_wbPathEditKey is null ? "Save path" : "Save path changes")) WbPathSave(name);
        ObserveWorldBuilderUiItem("Save path");
        ImGui.EndDisabled();
        ImGui.TextDisabled("Saved paths are drafts until you publish the pack.");

        ImGui.Separator();
        var saved = WbPathDocs(_wbPackId).ToList();
        ImGui.Checkbox("Show saved paths in the world", ref _wbPathShowSaved);
        ObserveWorldBuilderUiItem("Show saved paths");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Saved road surfaces stay painted on the ground on every World Builder page; their route lines show on this page.\n" +
                             "This is a preview in your client. Publish the pack to put the roads into the game.");
        if (saved.Count == 0) ImGui.TextDisabled("This pack has no saved paths yet.");
        else
        {
            bool listOpen = ImGui.BeginChild("##wb-paths", new Vector2(0, 120f * CreatorUiScale), true);
            foreach (var doc in listOpen ? saved : new())
            {
                string key = (string?)doc["docKey"] ?? "";
                var body = doc["body"] as JsonObject;
                var pts = WbPathPoints(body);
                int pathMap = (int?)body?["map"] ?? -1;
                string where = pathMap == map ? (_controller is { } c && pts.Count > 0
                    ? Inv($"{Vector2.Distance(new(c.Position.X, c.Position.Y), new(pts[0].X, pts[0].Y)):F0} yd away") : "this map")
                    : $"map {pathMap}";
                ImGui.PushID(key);
                if (ImGui.Selectable($"{key}  ({pts.Count} points, {where})", key == _wbPathEditKey)) WbPathOpen(key, body!);
                ObserveWorldBuilderUiItem("Path: " + key);
                ImGui.PopID();
            }
            ImGui.EndChild();   // always, whatever BeginChild returned
        }
        if (_wbPathEditKey is { } open && saved.Any(d => (string?)d["docKey"] == open))
        {
            if (_controller is not null && _wbCarvePoints.Count > 0 && _wbCarveMap == map)
            {
                if (CreatorButton("Go to path")) _controller.Teleport(_wbCarvePoints[0].X, _wbCarvePoints[0].Y, _wbCarvePoints[0].Z + 20f);
                ImGui.SameLine();
            }
            ImGui.BeginDisabled(_wbOps.Count > 0);
            bool delete = CreatorButton($"Delete path \"{open}\"");
            ObserveWorldBuilderUiItem("Delete path");
            if (delete)
            {
                var items = new JsonArray { new JsonObject { ["kind"] = "path", ["key"] = open, ["body"] = null } };
                WbOp($"delete path {open}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"delete path {open}", items.ToJsonString()),
                    _ => { _wbDocsTask = null; WbRequestDocs(); WbPathNew(); });
            }
            ImGui.EndDisabled();
        }
    }

    private IEnumerable<JsonObject> WbPathDocs(int? pack) =>
        _wbDocs?.OfType<JsonObject>().Where(d => (string?)d["kind"] == "path" && (pack is null || (int?)d["packId"] == pack)) ?? [];

    private static List<Vector3> WbPathPoints(JsonObject? body) =>
        (body?["points"] as JsonArray)?.OfType<JsonArray>().Where(p => p.Count >= 3)
            .Select(p => new Vector3(WbNpcFloat(p[0]), WbNpcFloat(p[1]), WbNpcFloat(p[2]))).ToList() ?? new();

    private void WbPathNew()
    {
        if (_wbTool == WorldBuilderTool.Path) WbSetTool(WorldBuilderTool.None);
        _wbCarvePoints.Clear();
        _wbCarveMap = -1;
        _wbPathSelected = -1;
        _wbPathEditKey = null;
        _wbPathEditBody = null;
        _wbPathClear = true;
        _wbPathGrade = false;
        _wbRoadSeed = 0;          // a new road gets its own pattern (the look itself carries over as the last one used)
        _wbRoadSample = null;
        WbSetBuf(_wbCarveName, "");
    }

    private void WbPathOpen(string key, JsonObject body)
    {
        if (_wbTool == WorldBuilderTool.Path) WbSetTool(WorldBuilderTool.None);
        _wbPathEditKey = key;
        _wbPathEditBody = (JsonObject)body.DeepClone();
        WbReplacePathPoints(WbPathPoints(body));
        _wbCarveMap = (int?)body["map"] ?? _config.Start.Map;
        _wbCarveWidth = WbNpcFloat(body["width"], 12f);
        _wbCarveFalloff = WbNpcFloat(body["falloff"], 20f);
        _wbPathClear = (bool?)body["clear"] ?? true;
        _wbPathGrade = (bool?)body["grade"] ?? true;        // paths saved before road surfaces always graded
        _wbRoadTexture = body["surface"]?["texture"]?.ToString();
        _wbShoulderTexture = null;
        _wbRoadSample = null;
        if (_wbRoadTexture is not null)
        {
            var surface = body["surface"]!;
            _wbRoadLastTexture = _wbRoadTexture;
            _wbRoadWidth = WbNpcFloat(surface["width"], _wbRoadWidth);
            _wbRoadSoft = WbNpcFloat(surface["softEdge"], _wbRoadSoft);
            _wbRoadStrength = WbNpcFloat(surface["strength"], 1f);
            _wbRoadRagged = WbNpcFloat(surface["ragged"], 0f);
            _wbRoadPatchy = WbNpcFloat(surface["patchy"], 0f);
            _wbRoadSeed = (int?)surface["seed"] ?? 0;
            _wbRoadStyleId = surface["style"]?.ToString();
            _wbRoadStyleTuned = (bool?)surface["styleAdjusted"] ?? false;
            if (surface["shoulder"] is JsonObject sh && sh["texture"]?.ToString() is { Length: > 0 } verge)
            {
                _wbShoulderTexture = _wbShoulderLastTexture = verge;
                _wbShoulderExtra = WbNpcFloat(sh["extra"], 3f);
                _wbShoulderSoft = WbNpcFloat(sh["softEdge"], 2f);
                _wbShoulderStrength = WbNpcFloat(sh["strength"], 1f);
            }
        }
        _wbPathSelected = -1;
        WbSetBuf(_wbCarveName, key);
    }

    /// <summary>One undoable op: the path doc (unknown body fields kept), plus removal of the old key on a rename.</summary>
    private void WbPathSave(string name)
    {
        var body = (JsonObject?)_wbPathEditBody?.DeepClone() ?? new JsonObject();
        var pts = new JsonArray();
        foreach (var p in _wbCarvePoints) pts.Add(new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2)));
        body["map"] = _wbCarveMap >= 0 ? _wbCarveMap : _config.Start.Map;
        body["points"] = pts;
        body["width"] = Math.Round(_wbPathGrade ? _wbCarveWidth : _wbRoadWidth, 2);
        body["falloff"] = Math.Round(_wbCarveFalloff, 2);
        body["clear"] = _wbPathClear;
        body["grade"] = _wbPathGrade;
        if (_wbRoadTexture is { } texture)
        {
            var surface = new JsonObject
            {
                ["texture"] = texture, ["width"] = Math.Round(_wbRoadWidth, 2),
                ["softEdge"] = Math.Round(_wbRoadSoft, 2), ["strength"] = Math.Round(_wbRoadStrength, 2),
            };
            // Road looks: only written when used, so a plain road's doc stays exactly what older builds read.
            if (_wbRoadRagged > 0f || _wbRoadPatchy > 0f)
            {
                if (_wbRoadSeed == 0) _wbRoadSeed = WbNewRoadSeed();
                surface["ragged"] = Math.Round(_wbRoadRagged, 2);
                surface["patchy"] = Math.Round(_wbRoadPatchy, 2);
            }
            if (_wbRoadSeed != 0) surface["seed"] = _wbRoadSeed;
            if (_wbShoulderTexture is { } verge)
                surface["shoulder"] = new JsonObject
                {
                    ["texture"] = verge, ["extra"] = Math.Round(_wbShoulderExtra, 2),
                    ["softEdge"] = Math.Round(_wbShoulderSoft, 2), ["strength"] = Math.Round(_wbShoulderStrength, 2),
                };
            if (_wbRoadStyleId is { } style)
            {
                surface["style"] = style;                    // informational: which measured look it started from
                if (_wbRoadStyleTuned) surface["styleAdjusted"] = true;
            }
            body["surface"] = surface;
        }
        else body.Remove("surface");
        var items = new JsonArray { new JsonObject { ["kind"] = "path", ["key"] = name, ["body"] = body } };
        string? renamed = _wbPathEditKey is { } old && old != name ? old : null;
        if (renamed is not null) items.Add(new JsonObject { ["kind"] = "path", ["key"] = renamed, ["body"] = null });
        // The width the road actually has: the levelled lane when graded, otherwise the painted surface.
        string label = Inv($"path {name}: {_wbCarvePoints.Count} point(s), {(_wbPathGrade ? _wbCarveWidth : _wbRoadWidth):0.#} yd wide") +
                       (renamed is null ? "" : $" (renamed from {renamed})");
        var saveBody = (JsonObject)body.DeepClone();
        WbOp(label, _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString()), _ =>
        {
            _wbPathEditKey = name;
            _wbPathEditBody = saveBody;
            _wbDocsTask = null;
            WbRequestDocs();
        });
    }

    private void WbReplacePathPoints(IEnumerable<Vector3> points)
    {
        var copy = points.ToList();
        _wbCarvePoints.Clear();
        _wbCarvePoints.AddRange(copy);
    }

    private void WbPathRemoveSelected()
    {
        if (_wbPathSelected < 0 || _wbPathSelected >= _wbCarvePoints.Count) return;
        _wbCarvePoints.RemoveAt(_wbPathSelected);
        _wbPathSelected = Math.Min(_wbPathSelected, _wbCarvePoints.Count - 1);
        _wbPathDragging = false;
    }

    // ── pointer ──────────────────────────────────────────────────────────────

    private void WbPathFrame(ImGuiIOPtr io, bool overUi)
    {
        int map = _config.Start.Map;
        if (_wbCarvePoints.Count > 0 && _wbCarveMap != map) { _wbToolHint = "This path is on another map."; return; }
        if (_wbPathDragging)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || _wbPathSelected < 0 || _wbPathSelected >= _wbCarvePoints.Count)
                _wbPathDragging = false;
            else if (_wbCursor is { } to)
            {
                float ground = WbBaseGroundZ(to.X, to.Y) ?? to.Z;
                _wbCarvePoints[_wbPathSelected] = new Vector3(to.X, to.Y, ground + _wbPathDragLift);
            }
            return;
        }
        if (!io.WantTextInput && _wbPathSelected >= 0 &&
            (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false)))
            WbPathRemoveSelected();
        if (overUi || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        int hit = WbPathPointUnderMouse(io.MousePos);
        if (hit >= 0)
        {
            _wbPathSelected = hit;
            var p = _wbCarvePoints[hit];
            _wbPathDragLift = p.Z - (WbBaseGroundZ(p.X, p.Y) ?? p.Z);
            _wbPathDragging = true;
            return;
        }
        if (_wbCursor is not { } at) return;
        var point = new Vector3(at.X, at.Y, WbBaseGroundZ(at.X, at.Y) ?? at.Z);
        int index = _wbCarvePoints.Count;
        if (io.KeyCtrl && _wbCarvePoints.Count >= 2)
        {
            // Insert into the segment nearest the click.
            float best = float.MaxValue;
            for (int i = 0; i + 1 < _wbCarvePoints.Count; i++)
            {
                float d = WorldBuilderLaw.PathDistance(new[] { _wbCarvePoints[i], _wbCarvePoints[i + 1] }, new Vector2(at.X, at.Y)).Distance;
                if (d < best) { best = d; index = i + 1; }
            }
        }
        bool tooClose = (index > 0 && Vector2.Distance(new(point.X, point.Y), new(_wbCarvePoints[index - 1].X, _wbCarvePoints[index - 1].Y)) < 0.5f) ||
                        (index < _wbCarvePoints.Count && Vector2.Distance(new(point.X, point.Y), new(_wbCarvePoints[index].X, _wbCarvePoints[index].Y)) < 0.5f);
        if (tooClose) { _wbMessage = "That is on top of an existing point; drag the point instead."; return; }
        _wbCarveMap = map;
        _wbCarvePoints.Insert(index, point);
        _wbPathSelected = index;
    }

    private int WbPathPointUnderMouse(Vector2 mouse)
    {
        var display = ImGui.GetIO().DisplaySize;
        int best = -1;
        float bestD = 12f * CreatorUiScale;
        for (int i = 0; i < _wbCarvePoints.Count; i++)
            if (_window.Camera.TryWorldToScreen(_wbCarvePoints[i] + new Vector3(0, 0, 0.5f), display, out var px) &&
                Vector2.Distance(px, mouse) is var d && d < bestD) { bestD = d; best = i; }
        return best;
    }

    /// <summary>Ground height without the path preview layer: stock + saved sculpt + a stroke in hand, bilinear on the
    /// outer vertex grid. Falls back to the rendered terrain where the preview base is not loaded.</summary>
    private float? WbBaseGroundZ(float x, float y)
    {
        var (col, row) = WorldBuilderLaw.TileOf(x, y);
        if (WbStockFor((col, row)) is not null)
        {
            var origin = WorldBuilderLaw.VertexWorld(col, row, 0, 0);
            float fr = Math.Clamp((origin.X - x) / WorldBuilderLaw.Unit, 0f, 128f), fc = Math.Clamp((origin.Y - y) / WorldBuilderLaw.Unit, 0f, 128f);
            int r0 = Math.Min((int)fr, 127), c0 = Math.Min((int)fc, 127);
            float tr = fr - r0, tc = fc - c0;
            float h00 = WbHeight(col, row, r0, c0), h01 = WbHeight(col, row, r0, c0 + 1);
            float h10 = WbHeight(col, row, r0 + 1, c0), h11 = WbHeight(col, row, r0 + 1, c0 + 1);
            return (h00 * (1 - tc) + h01 * tc) * (1 - tr) + (h10 * (1 - tc) + h11 * tc) * tr;
        }
        return _terrain?.SampleHeight(x, y);   // no preview base here, so no path layer either
    }

    // ── terrain preview layer ────────────────────────────────────────────────

    /// <summary>Recompute the path layer when the editor's path changes (throttled while dragging) and re-apply the
    /// tiles it touched before and after. Called every World Builder frame.</summary>
    private void WbUpdatePathPreview()
    {
        int map = _config.Start.Map;
        bool active = WbPathPageOpen && _wbPathGrade && _wbPathPreviewOn && _wbCarvePoints.Count >= 2 && _wbCarveMap == map &&
                      _wbStateMap == map && float.IsFinite(_wbCarveWidth) && _wbCarveWidth > 0 && float.IsFinite(_wbCarveFalloff) && _wbCarveFalloff >= 0;
        string sig = active
            ? Inv($"{map}|{_wbCarveWidth}|{_wbCarveFalloff}|{_wbServerSculpt.Count}|") + string.Join(";", _wbCarvePoints.Select(p => Inv($"{p.X},{p.Y},{p.Z}")))
            : "";
        if (sig == _wbPathPreviewSig) return;
        double now = ImGui.GetTime();
        if (sig.Length > 0 && now - _wbPathPreviewAt < 0.08) return;
        _wbPathPreviewAt = now;
        _wbPathPreviewSig = sig;
        foreach (var key in _wbPathPreview.Keys) _wbDirtyTiles.Add(key);
        _wbPathPreview.Clear();
        _wbPathPreviewMissing = false;
        if (!active) return;

        var pts = _wbCarvePoints;
        float reach = _wbCarveWidth / 2f + _wbCarveFalloff;
        float minX = pts.Min(p => p.X) - reach, maxX = pts.Max(p => p.X) + reach;
        float minY = pts.Min(p => p.Y) - reach, maxY = pts.Max(p => p.Y) + reach;
        var (c0, r0) = WorldBuilderLaw.TileOf(maxX, maxY);
        var (c1, r1) = WorldBuilderLaw.TileOf(minX, minY);
        for (int col = c0; col <= c1; col++)
            for (int row = r0; row <= r1; row++)
            {
                if (WbStockFor((col, row)) is null) { _wbPathPreviewMissing = true; continue; }
                var origin = WorldBuilderLaw.VertexWorld(col, row, 0, 0);
                int gr0 = Math.Clamp((int)MathF.Floor((origin.X - maxX) / WorldBuilderLaw.Unit), 0, 128);
                int gr1 = Math.Clamp((int)MathF.Ceiling((origin.X - minX) / WorldBuilderLaw.Unit), 0, 128);
                int gc0 = Math.Clamp((int)MathF.Floor((origin.Y - maxY) / WorldBuilderLaw.Unit), 0, 128);
                int gc1 = Math.Clamp((int)MathF.Ceiling((origin.Y - minY) / WorldBuilderLaw.Unit), 0, 128);
                float[]? layer = null;
                for (int gr = gr0; gr <= gr1; gr++)
                    for (int gc = gc0; gc <= gc1; gc++)
                    {
                        var v = WorldBuilderLaw.VertexWorld(col, row, gr, gc);
                        var (d, z) = WorldBuilderLaw.PathDistance(pts, v);
                        float weight = WorldBuilderLaw.PathWeight(d, _wbCarveWidth, _wbCarveFalloff);
                        if (weight <= 0f) continue;
                        float delta = (z - WbHeight(col, row, gr, gc)) * weight;
                        if (MathF.Abs(delta) < 0.001f) continue;
                        (layer ??= new float[WorldBuilderLaw.VertexCount])[gr * WorldBuilderLaw.Side + gc] = delta;
                    }
                if (layer is null) continue;
                _wbPathPreview[(col, row)] = layer;
                _wbDirtyTiles.Add((col, row));
                _wbPreviewTiles.Add((col, row));
            }
        if (WbScriptPath is not null)
            Console.WriteLine(Inv($"[wb] path preview: {pts.Count} points, {_wbPathPreview.Count} tile(s), max change {(_wbPathPreview.Count == 0 ? 0f : _wbPathPreview.Values.Max(l => l.Max(MathF.Abs))):F3} yd") +
                              (_wbPathPreviewMissing ? ", some terrain not loaded" : ""));
    }

    // ── world overlay ────────────────────────────────────────────────────────

    /// <summary>The editor path (centre, lane edges, blend edges, numbered points) and, faintly, the pack's other
    /// saved paths on this map. Drawn behind the panels while the Paths page is open.</summary>
    private void WbDrawPathOverlay()
    {
        if (!WbPathPageOpen || _terrain is null) return;
        int map = _config.Start.Map;
        var draw = ImGui.GetBackgroundDrawList();
        if (_wbPathShowSaved)
            foreach (var doc in WbPathDocs(_wbPackId))
            {
                if ((string?)doc["docKey"] == _wbPathEditKey || doc["body"] is not JsonObject body || (int?)body["map"] != map) continue;
                var pts = WbPathPoints(body);
                WbDrawPathLine(draw, pts, 0f, 0x90A0A0A0u, 2f, true);
                if (pts.Count > 0 && _window.Camera.TryWorldToScreen(pts[0] + new Vector3(0, 0, 2f), ImGui.GetIO().DisplaySize, out var label))
                    draw.AddText(label, 0xC0C0C0C0u, (string?)doc["docKey"] ?? "");
            }
        if (_wbCarvePoints.Count == 0 || _wbCarveMap != map) return;
        float half = (_wbPathGrade ? _wbCarveWidth : _wbRoadWidth) / 2f;
        WbDrawPathLine(draw, _wbCarvePoints, 0f, 0xE040D0FFu, 3f, true);
        WbDrawPathLine(draw, _wbCarvePoints, half, 0xF030A8FFu, 2.5f, false);
        WbDrawPathLine(draw, _wbCarvePoints, -half, 0xF030A8FFu, 2.5f, false);
        if (!_wbPathGrade && _wbRoadTexture is not null && _wbShoulderTexture is not null && _wbShoulderExtra > 0f)
        {
            // The shoulder's outer edge: where the verge texture under the road ends.
            WbDrawPathLine(draw, _wbCarvePoints, half + _wbShoulderExtra, 0x9050C8E8u, 1.5f, false);
            WbDrawPathLine(draw, _wbCarvePoints, -half - _wbShoulderExtra, 0x9050C8E8u, 1.5f, false);
        }
        if (_wbPathGrade && _wbCarveFalloff > 0.5f)
        {
            WbDrawPathLine(draw, _wbCarvePoints, half + _wbCarveFalloff, 0x9030A8FFu, 1.5f, false);
            WbDrawPathLine(draw, _wbCarvePoints, -half - _wbCarveFalloff, 0x9030A8FFu, 1.5f, false);
        }
        var display = ImGui.GetIO().DisplaySize;
        int hover = _wbTool == WorldBuilderTool.Path && !ImGui.GetIO().WantCaptureMouse ? WbPathPointUnderMouse(ImGui.GetIO().MousePos) : -1;
        for (int i = 0; i < _wbCarvePoints.Count; i++)
        {
            if (!_window.Camera.TryWorldToScreen(_wbCarvePoints[i] + new Vector3(0, 0, 0.5f), display, out var px)) continue;
            bool selected = i == _wbPathSelected;
            float r = (selected ? 8f : i == hover ? 7f : 5.5f) * CreatorUiScale;
            draw.AddCircleFilled(px, r + 1.5f, 0xE0000000u);
            draw.AddCircleFilled(px, r, selected ? 0xFFFFFFFFu : i == 0 ? 0xFF40FF40u : 0xFF40D0FFu);
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            draw.AddText(px + new Vector2(r + 2f, -r - 2f) + Vector2.One, 0xE0000000u, n);
            draw.AddText(px + new Vector2(r + 2f, -r - 2f), 0xFFFFFFFFu, n);
        }
    }

    /// <summary>A polyline offset sideways by <paramref name="offset"/> yards. The centre line follows the path's own
    /// heights; offset lines follow the (previewed) ground so the lane edges sit on the terrain.</summary>
    private void WbDrawPathLine(ImDrawListPtr draw, IReadOnlyList<Vector3> pts, float offset, uint colour, float thickness, bool centre)
    {
        if (pts.Count < 2 || _terrain is null) return;
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        Vector3? me = _controller?.Position;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            var dir = new Vector2(b.X - a.X, b.Y - a.Y);
            float len = dir.Length();
            if (len < 0.01f) continue;
            // Skip segments far from the viewer; overlays only need to read nearby.
            if (me is { } m && WorldBuilderLaw.PathDistance(new[] { a, b }, new Vector2(m.X, m.Y)).Distance > 500f) continue;
            var side = new Vector2(-dir.Y, dir.X) / len * offset;
            int steps = Math.Clamp((int)(len / 4f), 1, 200);
            Vector2? prev = null;
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                float x = a.X + (b.X - a.X) * t + side.X, y = a.Y + (b.Y - a.Y) * t + side.Y;
                float z = centre ? a.Z + (b.Z - a.Z) * t : _terrain.SampleHeight(x, y) ?? a.Z + (b.Z - a.Z) * t;
                Vector2? px = cam.TryProjectToScreen(new Vector3(x, y, z + 0.35f), display, out var pixel, out _) ? pixel : null;
                if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, colour, thickness);
                prev = px;
            }
        }
    }
}
