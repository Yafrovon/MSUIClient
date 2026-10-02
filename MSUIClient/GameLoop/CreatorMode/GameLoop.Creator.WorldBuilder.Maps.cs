using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: Map Forge + Dungeon Maker (shared_docs/WORLD_BUILDER.md §5).
//
// A pack map is a brand-new map id (800+) whose tiles are STAMPED from stock ADTs
// (terrain, textures, water, optionally trees/props and buildings) and then shaped with
// the ordinary sculpt/place tools. One "create" posts every piece as ONE undoable op:
//   map doc + tile docs                      → WDT/ADTs in patch-7, extractors, navmesh
//   dbc:Map + dbc:AreaTable                  → the client knows the map and its zone
//   dbrow:map_template + dbrow:area_template → the server knows them
// A portal is dbc:AreaTrigger (the client detects entering it) + areatrigger_template
// (the server's copy of the volume) + areatrigger_teleport (where it leads).
// A dungeon = an instance map (map_template.map_type 1) + an entrance portal in the
// world + an exit portal inside.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private int _wbMapId = 800, _wbMapType = 1, _wbMapPlayers = 5, _wbMapArea = 7001, _wbMapLevel = 30;
    private readonly byte[] _wbMapDir = new byte[40];
    private readonly byte[] _wbMapName = new byte[64];
    private readonly byte[] _wbSrcMap = new byte[40];
    private int _wbSrcCol = 27, _wbSrcRow = 28, _wbSrcW = 1, _wbSrcH = 1, _wbDstCol = 30, _wbDstRow = 30;
    private bool _wbStampDoodads = true, _wbStampWmos;
    private readonly byte[] _wbSubName = new byte[64];
    private int _wbSubArea = 7002;
    private float _wbSubRadius = 120f;
    private int _wbPortalId = 7001, _wbPortalTargetMap, _wbPortalMinLevel;
    private float _wbPortalRadius = 6f, _wbPortalTx, _wbPortalTy, _wbPortalTz, _wbPortalTo;
    private bool _wbMapFormInit, _wbPortalHasTarget;
    private int _wbMapTeam;
    private readonly byte[] _wbPortalName = new byte[64];

    private void WbInitMapForm()
    {
        if (_wbMapFormInit) return;
        _wbMapFormInit = true;
        WbSetBuf(_wbSrcMap, "Azeroth");
        if (_controller is not null)
        {
            (_wbSrcCol, _wbSrcRow) = WorldBuilderLaw.TileOf(_controller.Position.X, _controller.Position.Y);
            WbSetBuf(_wbSrcMap, _config.Start.MapName);
        }
        _wbMapId = (int)WbNextId("map", "mapId", 800);
        _wbMapArea = (int)WbNextId("dbrow:area_template", "entry", 7000);
        _wbSubArea = _wbMapArea;
        _wbPortalId = (int)WbNextId("dbrow:areatrigger_template", "id", 7000);
    }

    private bool WbIdUsed(string kind, string field, int id) => _wbDocs?.OfType<JsonObject>().Any(d =>
        (string?)d["kind"] == kind && WbNum(d["body"]?[field]) == id) == true;

    private void DrawWbMapsSection()
    {
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("Loading pack content..."); return; }
        WbInitMapForm();
        float w = CreatorControlWidth;
        ImGui.TextWrapped("Create a separate dungeon or map from a piece of existing terrain. For land connected to a continent, use Regions. Saved changes appear after publication.");
        if (ImGui.CollapsingHeader("Create a dungeon or map", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(w); ImGui.InputText("Name##wb-map", _wbMapName, (uint)_wbMapName.Length);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.Combo("Type", ref _wbMapType, "Outdoor map\0Dungeon\0Raid\0");
            if (_wbMapType > 0) { ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Player limit", ref _wbMapPlayers, 0); }
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Suggested level", ref _wbMapLevel, 0);
            ImGui.SetNextItemWidth(w * 0.5f); ImGui.Combo("Territory", ref _wbMapTeam, "Neutral\0Alliance\0Horde\0");
            WbDrawStampSource();
            if (ImGui.TreeNode("Advanced map identifiers"))
            {
                ImGui.TextWrapped("Allocated automatically. Change these only when coordinating content across packs.");
                ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Map ID", ref _wbMapId, 0);
                ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Zone ID", ref _wbMapArea, 0);
                ImGui.InputText("Directory override", _wbMapDir, (uint)_wbMapDir.Length);
                ImGui.TreePop();
            }
            string name = WbText(_wbMapName).Trim();
            string dir = WbText(_wbMapDir).Trim();
            if (dir.Length == 0) dir = WorldMapAuthoringLaw.DirectoryFromName(name);
            string? problem = name.Length == 0 ? "Give the map a name." : dir.Length == 0 || dir.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ? "Use letters, numbers or underscores for the map directory." :
                _wbMapId < 800 || _wbMapArea < 7000 ? "Custom map IDs start at 800; zone IDs start at 7000." :
                _wbMapLevel < 1 || _wbMapLevel > 63 || (_wbMapType > 0 && _wbMapPlayers < 1) ? "Choose a level from 1 to 63 and at least one player." :
                WbIdUsed("map", "mapId", _wbMapId) || WbIdUsed("dbrow:area_template", "entry", _wbMapArea) ? "That map or zone ID already belongs to saved content. Choose an unused ID." :
                _wbDocs.OfType<JsonObject>().Any(d => (string?)d["kind"] == "map" && string.Equals((string?)d["body"]?["directory"], dir, StringComparison.OrdinalIgnoreCase)) ? "That map directory already exists. Choose another name." :
                WorldMapAuthoringLaw.StampProblem(WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow);
            if (problem is not null) ImGui.TextWrapped(problem);
            ImGui.BeginDisabled(problem is not null || _wbPackId == 0 || _wbOps.Count > 0);
            if (CreatorButton("Save new map")) WbPost($"map {name}", WbNewMapItems(
                _wbMapId, dir, name, _wbMapType, _wbMapPlayers, (uint)_wbMapArea, _wbMapLevel,
                WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow, _wbStampDoodads, _wbStampWmos, _wbMapTeam * 2));
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader("Name a place within your map"))
        {
            ImGui.TextWrapped("Stand in the place you want to name. Its name will appear as a subzone inside the surrounding zone.");
            ImGui.SetNextItemWidth(w); ImGui.InputText("Place name", _wbSubName, (uint)_wbSubName.Length);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Radius (yards)", ref _wbSubRadius);
            if (ImGui.TreeNode("Advanced subzone ID")) { ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Area ID", ref _wbSubArea, 0); ImGui.TreePop(); }
            bool ready = _controller is not null && WbText(_wbSubName).Trim().Length > 0 && _wbSubArea >= 7000 &&
                !WbIdUsed("dbrow:area_template", "entry", _wbSubArea) && float.IsFinite(_wbSubRadius) && _wbSubRadius > 0;
            if (!ready) ImGui.TextWrapped("Enter a name and positive radius, and use an unused subzone ID.");
            ImGui.BeginDisabled(!ready || _wbOps.Count > 0);
            if (CreatorButton("Save subzone around me")) WbAddSubzone((uint)_wbSubArea, WbText(_wbSubName).Trim(), _controller!.Position, _wbSubRadius);
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader("Connect two places with a portal"))
        {
            ImGui.TextWrapped("First stand at the arrival point and capture it. Then travel to the entrance and save the portal there. Create a second portal for the return journey.");
            ImGui.SetNextItemWidth(w); ImGui.InputText("Portal name", _wbPortalName, (uint)_wbPortalName.Length);
            ImGui.BeginDisabled(_controller is null);
            if (CreatorButton("Use where I stand as the arrival"))
            {
                _wbPortalTargetMap = _config.Start.Map;
                var at = _controller!.Position; _wbPortalTx = at.X; _wbPortalTy = at.Y; _wbPortalTz = at.Z;
                _wbPortalHasTarget = true;
            }
            ImGui.EndDisabled();
            if (_wbPortalHasTarget) ImGui.TextWrapped(Inv($"Arrival: map {_wbPortalTargetMap}, {_wbPortalTx:F1}, {_wbPortalTy:F1}, {_wbPortalTz:F1}"));
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputFloat("Entrance radius (yards)", ref _wbPortalRadius);
            ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Minimum player level", ref _wbPortalMinLevel, 0);
            if (ImGui.TreeNode("Advanced portal coordinates and ID"))
            {
                ImGui.SetNextItemWidth(w * 0.6f); ImGui.InputInt("Trigger ID", ref _wbPortalId, 0);
                ImGui.SetNextItemWidth(w * 0.6f); bool changed = ImGui.InputInt("Arrival map", ref _wbPortalTargetMap, 0);
                ImGui.SetNextItemWidth(w * 0.6f); changed |= ImGui.InputFloat("Arrival X", ref _wbPortalTx);
                ImGui.SetNextItemWidth(w * 0.6f); changed |= ImGui.InputFloat("Arrival Y", ref _wbPortalTy);
                ImGui.SetNextItemWidth(w * 0.6f); changed |= ImGui.InputFloat("Arrival Z", ref _wbPortalTz);
                ImGui.SetNextItemWidth(w * 0.6f); changed |= ImGui.InputFloat("Arrival facing", ref _wbPortalTo);
                if (changed) _wbPortalHasTarget = true;
                ImGui.TreePop();
            }
            bool ready = _controller is not null && _wbPortalHasTarget && _wbPortalTargetMap >= 0 && _wbPortalId >= 7000 &&
                !WbIdUsed("dbrow:areatrigger_template", "id", _wbPortalId) && WbText(_wbPortalName).Trim().Length > 0 &&
                float.IsFinite(_wbPortalRadius) && _wbPortalRadius > 0 && float.IsFinite(_wbPortalTx) && float.IsFinite(_wbPortalTy) &&
                float.IsFinite(_wbPortalTz) && float.IsFinite(_wbPortalTo) && _wbPortalMinLevel is >= 0 and <= 63;
            if (!ready) ImGui.TextWrapped("Name the portal, capture an arrival, and use a positive radius with an unused trigger ID.");
            ImGui.BeginDisabled(!ready || _wbOps.Count > 0);
            if (CreatorButton("Save entrance where I stand")) WbPost($"portal {WbText(_wbPortalName)}", WbPortalItems((uint)_wbPortalId,
                WbText(_wbPortalName).Trim(), _config.Start.Map, _controller!.Position, _wbPortalRadius, _wbPortalTargetMap,
                new Vector3(_wbPortalTx, _wbPortalTy, _wbPortalTz), _wbPortalTo, _wbPortalMinLevel));
            ImGui.EndDisabled();
        }
    }

    private void WbDrawStampSource()
    {
        ImGui.TextWrapped($"Terrain source: {WbText(_wbSrcMap)}, tile {_wbSrcCol}, {_wbSrcRow}. Start with one tile, then extend the selection if needed.");
        ImGui.BeginDisabled(_controller is null);
        if (CreatorButton("Copy terrain from where I stand"))
        {
            (_wbSrcCol, _wbSrcRow) = WorldBuilderLaw.TileOf(_controller!.Position.X, _controller.Position.Y);
            WbSetBuf(_wbSrcMap, _config.Start.MapName);
        }
        ImGui.EndDisabled();
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Tiles wide", ref _wbSrcW, 0);
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Tiles tall", ref _wbSrcH, 0);
        ImGui.Checkbox("Include trees and small props", ref _wbStampDoodads);
        ImGui.Checkbox("Include existing buildings", ref _wbStampWmos);
        if (ImGui.TreeNode("Advanced source and destination tiles"))
        {
            ImGui.InputText("Source directory", _wbSrcMap, (uint)_wbSrcMap.Length);
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Source column", ref _wbSrcCol, 0);
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Source row", ref _wbSrcRow, 0);
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Destination column", ref _wbDstCol, 0);
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f); ImGui.InputInt("Destination row", ref _wbDstRow, 0);
            ImGui.TreePop();
        }
    }

    private static void WbSetBuf(byte[] buf, string s)
    {
        Array.Clear(buf);
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        Array.Copy(b, buf, Math.Min(b.Length, buf.Length - 1));
    }

    private void WbPost(string label, JsonArray items)
    {
        if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
        WbOp(label, _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString()), _ => WbRequestDocs());
    }

    /// <summary>Every doc a new map needs (shared by the panel and the zone scripts).</summary>
    internal static JsonArray WbNewMapItems(int mapId, string dir, string name, int type, int players, uint area, int level,
        string srcDir, int srcCol, int srcRow, int wide, int tall, int dstCol, int dstRow, bool doodads, bool wmos, int team = 0)
    {
        var items = new JsonArray
        {
            new JsonObject { ["kind"] = "map", ["body"] = new JsonObject { ["mapId"] = mapId, ["directory"] = dir, ["name"] = name, ["instanceType"] = type, ["areaId"] = area } },
            // Map.dbc: clone Kalimdor for a continent, Shadowfang Keep for an instance.
            new JsonObject { ["kind"] = "dbc:Map", ["key"] = mapId.ToString(CultureInfo.InvariantCulture), ["body"] = new JsonObject
            {
                ["cloneFrom"] = type == 0 ? 1 : 33,
                // Field 38 (loading screen) is inherited from the clone: none for a continent, the
                // Shadowfang Keep art for an instance - better than a black curtain.
                ["fields"] = new JsonObject { ["1"] = dir, ["2"] = type, ["4"] = name, ["19"] = type == 0 ? 0 : area },
            } },
            WbAreaDbc(area, (uint)mapId, 0, name, level, team),
            new JsonObject { ["kind"] = "dbrow:map_template", ["body"] = new JsonObject
            {
                ["entry"] = mapId, ["patch"] = 0, ["parent"] = 0, ["map_type"] = type == 0 ? 0 : type == 1 ? 1 : 2,
                ["linked_zone"] = type == 0 ? 0 : (int)area, ["player_limit"] = type == 0 ? 0 : players,
                ["reset_delay"] = type == 2 ? 7 : 0, ["ghost_entrance_map"] = -1, ["ghost_entrance_x"] = 0, ["ghost_entrance_y"] = 0,
                ["map_name"] = name, ["script_name"] = "",
            } },
            WbAreaRow(area, (uint)mapId, 0, name, level, team),
        };
        for (int x = 0; x < wide; x++)
            for (int y = 0; y < tall; y++)
                items.Add(new JsonObject { ["kind"] = "tile", ["body"] = new JsonObject
                {
                    ["map"] = mapId, ["col"] = dstCol + x, ["row"] = dstRow + y,
                    ["sourceMap"] = srcDir, ["sourceCol"] = srcCol + x, ["sourceRow"] = srcRow + y,
                    ["keepDoodads"] = doodads, ["keepWmos"] = wmos, ["areaId"] = area,
                } });
        return items;
    }

    /// <summary>AreaTable.dbc row cloned from Silverpine Forest (130). Explore bits above the stock
    /// maximum (1076) come from the area id so they never collide.</summary>
    internal static JsonObject WbAreaDbc(uint area, uint map, uint parent, string name, int level, int team = 0) =>
        new() { ["kind"] = "dbc:AreaTable", ["key"] = area.ToString(CultureInfo.InvariantCulture), ["body"] = new JsonObject
        {
            ["cloneFrom"] = 130,
            ["fields"] = new JsonObject { ["1"] = map, ["2"] = parent, ["3"] = WbAreaBit(area), ["10"] = level, ["11"] = name, ["20"] = team },
        } };

    internal static JsonObject WbAreaRow(uint area, uint map, uint parent, string name, int level, int team = 0) =>
        new() { ["kind"] = "dbrow:area_template", ["body"] = new JsonObject
        {
            ["entry"] = area, ["map_id"] = map, ["zone_id"] = parent, ["explore_flag"] = WbAreaBit(area),
            ["flags"] = 64, ["area_level"] = level, ["name"] = name, ["team"] = team, ["liquid_type"] = 0,
        } };

    internal static uint WbAreaBit(uint area) => 1100 + (area - 7000) % 900;

    private void WbAddSubzone(uint area, string name, Vector3 at, float radius)
    {
        if (_wbDocs is null) { WbRequestDocs(); _wbMessage = "loading pack content first - press again"; return; }
        int map = _config.Start.Map;
        uint zone = 0;
        var items = new JsonArray();
        foreach (var d in _wbDocs.OfType<JsonObject>().Where(d => (string?)d["kind"] == "tile" && (int?)d["packId"] == _wbPackId))
        {
            var body = d["body"]!.AsObject();
            if ((int?)body["map"] != map) continue;
            int col = (int)body["col"]!, row = (int)body["row"]!;
            // Only tiles the circle can reach.
            float x0 = (32 - row) * MSUIClient.Engine.UI.WorldBuilderLaw.Tile, y0 = (32 - col) * MSUIClient.Engine.UI.WorldBuilderLaw.Tile;
            float nx = Math.Clamp(at.X, x0 - MSUIClient.Engine.UI.WorldBuilderLaw.Tile, x0), ny = Math.Clamp(at.Y, y0 - MSUIClient.Engine.UI.WorldBuilderLaw.Tile, y0);
            if ((nx - at.X) * (nx - at.X) + (ny - at.Y) * (ny - at.Y) > radius * radius) continue;
            zone = (uint?)body["areaId"] ?? zone;
            var copy = JsonNode.Parse(body.ToJsonString())!.AsObject();
            var paints = copy["areaPaint"] as JsonArray ?? new JsonArray();
            paints.Add(new JsonObject { ["areaId"] = area, ["x"] = Math.Round(at.X, 1), ["y"] = Math.Round(at.Y, 1), ["radius"] = radius });
            copy["areaPaint"] = paints;
            items.Add(new JsonObject { ["kind"] = "tile", ["body"] = copy });
        }
        if (items.Count == 0) { _wbMessage = "No pack tiles of this map near you (subzones are for pack maps)."; return; }
        int team = (int)WbNum(WbDocBodies("dbrow:area_template").FirstOrDefault(a => WbNum(a["entry"]) == zone)?["team"]);
        items.Add(WbAreaDbc(area, (uint)map, zone, name, _wbMapLevel, team));
        items.Add(WbAreaRow(area, (uint)map, zone, name, _wbMapLevel, team));
        WbPost($"subzone {name} ({area})", items);
    }

    /// <summary>AreaTrigger.dbc + areatrigger_template + areatrigger_teleport for one sphere trigger.</summary>
    internal static JsonArray WbPortalItems(uint id, string name, int map, Vector3 at, float radius,
        int targetMap, Vector3 target, float facing, int minLevel)
    {
        // DBC float fields travel as { "f": value } so they can never be mistaken for integers.
        JsonObject Fl(float v) => new() { ["f"] = Math.Round(v, 3) };
        return new JsonArray
        {
            new JsonObject { ["kind"] = "dbc:AreaTrigger", ["key"] = id.ToString(CultureInfo.InvariantCulture), ["body"] = new JsonObject
            {
                ["fields"] = new JsonObject { ["1"] = map, ["2"] = Fl(at.X), ["3"] = Fl(at.Y), ["4"] = Fl(at.Z), ["5"] = Fl(radius) },
            } },
            new JsonObject { ["kind"] = "dbrow:areatrigger_template", ["body"] = new JsonObject
            {
                ["id"] = id, ["build"] = 5875, ["name"] = name, ["map_id"] = map,
                ["x"] = Math.Round(at.X, 3), ["y"] = Math.Round(at.Y, 3), ["z"] = Math.Round(at.Z, 3), ["radius"] = radius,
                ["box_x"] = 0, ["box_y"] = 0, ["box_z"] = 0, ["box_orientation"] = 0, ["cooldown"] = 0,
                ["condition_id"] = 0, ["script_id"] = 0, ["script_name"] = "",
            } },
            new JsonObject { ["kind"] = "dbrow:areatrigger_teleport", ["body"] = new JsonObject
            {
                ["id"] = id, ["patch"] = 0, ["name"] = name,
                ["message"] = minLevel > 0 ? $"You must be at least level {minLevel} to enter." : "",
                ["required_level"] = minLevel, ["required_condition"] = 0, ["target_map"] = targetMap,
                ["target_position_x"] = Math.Round(target.X, 3), ["target_position_y"] = Math.Round(target.Y, 3),
                ["target_position_z"] = Math.Round(target.Z, 3), ["target_orientation"] = Math.Round(facing, 3),
            } },
        };
    }
}
