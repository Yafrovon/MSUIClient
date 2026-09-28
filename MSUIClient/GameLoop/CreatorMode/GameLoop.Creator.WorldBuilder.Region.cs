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
    private readonly byte[] _wbCarveName = new byte[48];
    private readonly byte[] _wbWorldMapOutlinePath = new byte[1024];
    private string _wbWorldMapOutlineSummary = "";

    private void DrawWbRegionSection()
    {
        float w = CreatorControlWidth;
        int map = _config.Start.Map;
        ImGui.TextDisabled($"Move this pack's region (from map {map})");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("To map##wb-region", ref _wbRegionToMap);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Shift cols (+east)", ref _wbRegionDCol);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Shift rows (+south)", ref _wbRegionDRow);
        if (CreatorButton("Move region")) WbRelocate(map, _wbRegionToMap, _wbRegionDCol, _wbRegionDRow);

        ImGui.Separator();
        ImGui.TextDisabled("Stamp onto this continent (seamless land; uses the Maps & dungeons stamp fields)");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Stitch band (yd)", ref _wbStitchBand);
        bool continent = map is 0 or 1;
        if (!continent) ImGui.BeginDisabled();
        if (CreatorButton("Stamp here"))
            WbPost($"stamp {WbText(_wbSrcMap)} {_wbSrcCol},{_wbSrcRow} x{_wbSrcW}x{_wbSrcH} onto map {map} at {_wbDstCol},{_wbDstRow}",
                WbStampItems(map, WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow,
                    _wbStampDoodads, _wbStampWmos, (uint)_wbMapArea, _wbStitchBand));
        if (!continent) ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("World map coastline and hover highlight");
        ImGui.SetNextItemWidth(w);
        ImGui.InputText("Outline JSON file", _wbWorldMapOutlinePath, (uint)_wbWorldMapOutlinePath.Length);
        if (CreatorButton("Load world-map outline")) WbLoadWorldMapOutline();
        if (_wbWorldMapOutlineSummary.Length > 0) ImGui.TextWrapped(_wbWorldMapOutlineSummary);

        ImGui.Separator();
        if (_controller is not null)
        {
            var (col, row) = WorldBuilderLaw.TileOf(_controller.Position.X, _controller.Position.Y);
            var tile = WbTileDoc(map, col, row);
            ImGui.TextDisabled(tile is null ? $"Tile {col},{row}: not a pack tile" :
                $"Tile {col},{row} <- {(string?)tile["sourceMap"]} {(int?)tile["sourceCol"]},{(int?)tile["sourceRow"]}, stitch {(double?)tile["stitch"] ?? 0:F0} yd");
            if (tile is not null)
            {
                if (CreatorButton("Stitch this tile")) WbTileSet(map, col, row, "stitch", _wbStitchBand.ToString(CultureInfo.InvariantCulture));
                ImGui.SetNextItemWidth(w); ImGui.InputText("Drop buildings (file names, comma)", _wbTileDrop, (uint)_wbTileDrop.Length);
                if (CreatorButton("Drop them") && WbText(_wbTileDrop).Length > 0) WbTileSet(map, col, row, "dropWmos", WbText(_wbTileDrop));
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Carve a path ({_wbCarvePoints.Count} point(s)) - a pass, a land bridge, a road");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Width##wb-carve", ref _wbCarveWidth);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Falloff##wb-carve", ref _wbCarveFalloff);
        if (CreatorButton("Add point here") && _controller is not null) _wbCarvePoints.Add(_controller.Position);
        ImGui.SameLine();
        if (CreatorButton("Clear points")) _wbCarvePoints.Clear();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputText("Path name", _wbCarveName, (uint)_wbCarveName.Length);
        if (CreatorButton("Save path") && _wbCarvePoints.Count >= 2 && WbText(_wbCarveName).Length > 0)
        {
            WbPostPath(WbText(_wbCarveName), _wbCarvePoints.ToList(), _wbCarveWidth, _wbCarveFalloff);
            _wbMessage = "path saved - it grades the ground on the next publish (verifier G13 walks it)";
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
        _wbDocs?.OfType<JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "tile" && d["body"] is JsonObject b &&
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
        var pts = new JsonArray();
        foreach (var p in points) pts.Add(new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2)));
        var body = new JsonObject { ["map"] = _config.Start.Map, ["points"] = pts, ["width"] = width, ["falloff"] = falloff };
        WbPost(Inv($"path {name}: {points.Count} point(s), {width:F0} yd wide"),
            new JsonArray { new JsonObject { ["kind"] = "path", ["key"] = name, ["body"] = body } });
    }
}
