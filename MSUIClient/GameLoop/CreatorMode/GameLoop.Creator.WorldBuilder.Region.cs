using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: regions, continent stamps, seams and paths (shared_docs/WORLD_BUILDER.md §9).
//
// A ZONE in WoW is seamless continent land; only instances and raids sit behind portals. So a pack can:
//   * stamp stock tiles ONTO a continent (map 0/1) - the stamp replaces that stock tile while the pack is
//     enabled; its edges STITCH into the neighbours (tile doc "stitch": band in yards) and, where the new
//     ground dips under the stock sea, it keeps the stock water (build-side);
//   * move a whole region (docs, placements, sculpt) to another map/place by whole tiles - one undoable op;
//   * edit one tile doc (stitch band, dropped buildings, keep flags);
//   * carve a path: grade the ground along a polyline (a pass through a ridge, a land bridge, a road).
// Each is a panel control (humans) AND a script command (agents): relocate / stamp / tile / carve.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private int _wbRegionToMap, _wbRegionDCol, _wbRegionDRow;
    private float _wbStitchBand = 120f, _wbCarveWidth = 12f, _wbCarveFalloff = 18f;
    private readonly byte[] _wbTileDrop = new byte[128];
    private readonly List<Vector3> _wbCarvePoints = new();
    private int _wbCarveMap = -1;
    private readonly byte[] _wbCarveName = new byte[48];
    private readonly byte[] _wbWorldMapOutlinePath = new byte[1024];
    private string _wbWorldMapOutlineSummary = "";

    private void DrawWbRegionSection()
    {
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("Loading pack content..."); return; }
        WbInitMapForm();
        float w = CreatorControlWidth;
        int map = _config.Start.Map;
        ImGui.TextWrapped("Add connected terrain to an existing continent, or shape a route through your pack's land. Save records the edit; publication builds the ground and its walking paths.");
        if (ImGui.CollapsingHeader("Add terrain to this continent"))
        {
            WbDrawStampSource();
            ImGui.TextWrapped($"Destination: tile {_wbDstCol}, {_wbDstRow} on the current map.");
            ImGui.BeginDisabled(_controller is null);
            if (CreatorButton("Place the terrain at my current tile"))
                (_wbDstCol, _wbDstRow) = WorldBuilderLaw.TileOf(_controller!.Position.X, _controller.Position.Y);
            ImGui.EndDisabled();
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Blend into neighbours (yards)", ref _wbStitchBand);
            ImGui.TextWrapped("The selection replaces those tiles while this pack is enabled. Buildings and terrain can be edited after publication.");
            string? problem = map is not (0 or 1) ? "Connected regions are available on the Eastern Kingdoms and Kalimdor. Use Maps for separate dungeons." :
                !float.IsFinite(_wbStitchBand) || _wbStitchBand < 0 ? "Choose a non-negative blend distance." :
                WorldMapAuthoringLaw.StampProblem(WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow);
            if (problem is not null) ImGui.TextWrapped(problem);
            ImGui.BeginDisabled(problem is not null || _wbOps.Count > 0);
            if (CreatorButton("Save terrain selection"))
                WbPost($"terrain from {WbText(_wbSrcMap)} to map {map}",
                    WbStampItems(map, WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow,
                        _wbStampDoodads, _wbStampWmos, 0, _wbStitchBand));
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader("Create a path or mountain pass", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Walk along the intended route and add points. Their heights define the road, so place points at the height players should walk. The edges blend into the surrounding ground.");
            ImGui.SetNextItemWidth(w); ImGui.InputText("Path name", _wbCarveName, (uint)_wbCarveName.Length);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Path width (yards)", ref _wbCarveWidth);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Edge blending (yards)", ref _wbCarveFalloff);
            ImGui.TextDisabled($"{_wbCarvePoints.Count} points recorded" + (_wbCarveMap >= 0 ? $" on map {_wbCarveMap}" : ""));
            bool sameMap = _wbCarvePoints.Count == 0 || _wbCarveMap == map;
            if (!sameMap) ImGui.TextWrapped("Return to the path's map to add points, or clear the points to start a new path here.");
            ImGui.BeginDisabled(_controller is null || !sameMap);
            if (CreatorButton("Add point where I stand"))
            {
                _wbCarveMap = map;
                var at = _controller!.Position;
                if (_wbCarvePoints.Count == 0 || Vector2.Distance(new(at.X, at.Y), new(_wbCarvePoints[^1].X, _wbCarvePoints[^1].Y)) >= 0.1f)
                    _wbCarvePoints.Add(at);
                else _wbMessage = "Move away from the previous point before adding another.";
            }
            ImGui.EndDisabled();
            ImGui.BeginDisabled(_wbCarvePoints.Count == 0);
            if (CreatorButton("Remove last point")) _wbCarvePoints.RemoveAt(_wbCarvePoints.Count - 1);
            if (CreatorButton("Clear path points")) _wbCarvePoints.Clear();
            ImGui.EndDisabled();
            string? problem = WorldMapAuthoringLaw.PathProblem(WbText(_wbCarveName), _wbCarvePoints, _wbCarveWidth, _wbCarveFalloff);
            if (problem is not null) ImGui.TextWrapped(problem);
            ImGui.BeginDisabled(problem is not null || !sameMap || _wbOps.Count > 0);
            if (CreatorButton("Save path")) WbPostPath(WbText(_wbCarveName).Trim(), _wbCarvePoints.ToList(), _wbCarveWidth, _wbCarveFalloff);
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader("Advanced region tools"))
        {
            ImGui.TextWrapped("Move every piece of this pack on the current map: terrain, placements and content. One column or row is about 533 yards. Review the destination before saving.");
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Destination map", ref _wbRegionToMap, 0);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Columns to move (+east)", ref _wbRegionDCol, 0);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Rows to move (+south)", ref _wbRegionDRow, 0);
            ImGui.BeginDisabled(_wbRegionToMap < 0 || (_wbRegionToMap == map && _wbRegionDCol == 0 && _wbRegionDRow == 0) || _wbOps.Count > 0);
            if (CreatorButton("Move pack region")) WbRelocate(map, _wbRegionToMap, _wbRegionDCol, _wbRegionDRow);
            ImGui.EndDisabled();
            ImGui.Separator();
            ImGui.TextWrapped("Import a traced coastline for the continent map's zone name and hover highlight.");
            ImGui.SetNextItemWidth(w); ImGui.InputText("Outline JSON file", _wbWorldMapOutlinePath, (uint)_wbWorldMapOutlinePath.Length);
            if (CreatorButton("Import world-map outline")) WbLoadWorldMapOutline();
            if (_wbWorldMapOutlineSummary.Length > 0) ImGui.TextWrapped(_wbWorldMapOutlineSummary);
            if (_controller is not null)
            {
                var (col, row) = WorldBuilderLaw.TileOf(_controller.Position.X, _controller.Position.Y);
                var tile = WbTileDoc(map, col, row);
                ImGui.Separator();
                ImGui.TextWrapped(tile is null ? $"Tile {col}, {row} is not part of this pack." : $"Edit this pack's tile {col}, {row}.");
                if (tile is not null)
                {
                    ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Tile edge blend (yards)", ref _wbStitchBand);
                    ImGui.BeginDisabled(!float.IsFinite(_wbStitchBand) || _wbStitchBand < 0);
                    if (CreatorButton("Save tile blending")) WbTileSet(map, col, row, "stitch", _wbStitchBand.ToString(CultureInfo.InvariantCulture));
                    ImGui.EndDisabled();
                    ImGui.SetNextItemWidth(w); ImGui.InputText("Building filenames to remove", _wbTileDrop, (uint)_wbTileDrop.Length);
                    ImGui.TextWrapped("Separate exact source filenames with commas. This removes source buildings when the tile is rebuilt.");
                    if (CreatorButton("Remove named source buildings") && WbText(_wbTileDrop).Length > 0) WbTileSet(map, col, row, "dropWmos", WbText(_wbTileDrop));
                }
            }
        }
    }

    private void WbLoadWorldMapOutline()
    {
        if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
        try
        {
            string path = WbText(_wbWorldMapOutlinePath).Trim();
            if (path.Length == 0) { _wbMessage = "Choose an outline JSON file first."; return; }
            if (!WorldBuilderLaw.TryParseWorldMapOutline(File.ReadAllText(path), out var body, out string problem))
            { _wbMessage = problem; return; }
            int points = ((JsonArray)body!["polygon"]!).Count;
            _wbWorldMapOutlineSummary = $"{body["directory"]}: map {body["map"]}, area {body["area"]}, {points} coastline points. " +
                "Publish the pack to apply the outline.";
            WbPost($"world-map outline {body["directory"]}",
                new JsonArray { new JsonObject { ["kind"] = "worldmap", ["body"] = body } });
        }
        catch (Exception ex) { _wbMessage = "Could not load outline: " + ex.Message; }
    }

    private void WbRelocate(int from, int to, int dCol, int dRow)
    {
        if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
        WbOp(Inv($"move region map {from} -> {to} by ({dCol}, {dRow}) tiles"),
            _wbClient.RelocateAsync(SuiWebAppUrl, _wbPackId, from, to, dCol, dRow), _ => WbRequestDocs());
    }

    /// <summary>Tile docs that stamp stock tiles onto a stock continent (no map/area docs: the map exists).</summary>
    internal static JsonArray WbStampItems(int map, string srcDir, int srcCol, int srcRow, int wide, int tall,
        int dstCol, int dstRow, bool doodads, bool wmos, uint area, float stitch)
    {
        var items = new JsonArray();
        for (int x = 0; x < wide; x++)
            for (int y = 0; y < tall; y++)
            {
                var body = new JsonObject
                {
                    ["map"] = map, ["col"] = dstCol + x, ["row"] = dstRow + y,
                    ["sourceMap"] = srcDir, ["sourceCol"] = srcCol + x, ["sourceRow"] = srcRow + y,
                    ["keepDoodads"] = doodads, ["keepWmos"] = wmos,
                };
                if (area != 0) body["areaId"] = area;
                if (stitch > 0) body["stitch"] = stitch;
                items.Add(new JsonObject { ["kind"] = "tile", ["body"] = body });
            }
        return items;
    }

    private JsonObject? WbTileDoc(int map, int col, int row) =>
        _wbDocs?.OfType<JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "tile" && (int?)d["packId"] == _wbPackId && d["body"] is JsonObject b &&
            (int?)b["map"] == map && (int?)b["col"] == col && (int?)b["row"] == row)?["body"] as JsonObject;

    /// <summary>Set one field of a pack tile doc: stitch (yd), dropWmos (comma names, merged), keepDoodads/keepWmos
    /// (true/false), areaId, or any other number/text - one undoable op.</summary>
    private bool WbTileSet(int map, int col, int row, string key, string value) => WbTilesSet(map, col, col, row, row, key, value);

    /// <summary>The same for every pack tile in a col/row range - ONE op for a whole block.</summary>
    private bool WbTilesSet(int map, int c0, int c1, int r0, int r1, string key, string value)
    {
        var items = new JsonArray();
        for (int col = Math.Min(c0, c1); col <= Math.Max(c0, c1); col++)
            for (int row = Math.Min(r0, r1); row <= Math.Max(r0, r1); row++)
            {
                if (WbTileDoc(map, col, row) is not { } body) continue;
                var edited = (JsonObject)body.DeepClone();
                if (key is "dropWmos" or "dropDoodads")
                {
                    var list = edited[key] as JsonArray ?? new JsonArray();
                    foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!list.Any(n => string.Equals(n?.ToString(), name, StringComparison.OrdinalIgnoreCase))) list.Add(name);
                    edited[key] = list;
                }
                else if (bool.TryParse(value, out bool flag)) edited[key] = flag;
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) edited[key] = number;
                else edited[key] = value;
                items.Add(new JsonObject { ["kind"] = "tile", ["body"] = edited });
            }
        if (items.Count == 0)
        {
            _wbMessage = Inv($"no pack tiles on map {map} in {c0}-{c1} x {r0}-{r1}");
            Console.WriteLine("[wb] " + _wbMessage);
            return false;
        }
        WbPost(Inv($"{items.Count} tile(s) of map {map}: {key} = {value}"), items);
        return true;
    }

    /// <summary>
    /// A graded path (doc kind "path", one undoable op): the build grades the ground along the polyline AFTER
    /// seams are stitched - within half the width it takes the path's height (linear between the waypoints'
    /// z), beyond that it blends back over the falloff. Exact and the same on every tile it crosses; the
    /// verifier's G13 walks it (grade <= 45 degrees, not under water). Shows after publish + download.
    /// </summary>
    private void WbPostPath(string name, IReadOnlyList<Vector3> points, float width, float falloff)
    {
        if (WorldMapAuthoringLaw.PathProblem(name, points, width, falloff) is { } problem) { _wbMessage = problem; return; }
        var pts = new JsonArray();
        foreach (var p in points) pts.Add(new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2)));
        var body = new JsonObject { ["map"] = _config.Start.Map, ["points"] = pts, ["width"] = width, ["falloff"] = falloff };
        WbPost(Inv($"path {name}: {points.Count} point(s), {width:F0} yd wide"),
            new JsonArray { new JsonObject { ["kind"] = "path", ["key"] = name, ["body"] = body } });
    }
}
