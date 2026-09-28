using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

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
    private int _wbMapId = 800, _wbMapType, _wbMapPlayers = 5, _wbMapArea = 7001, _wbMapLevel = 30;
    private readonly byte[] _wbMapDir = new byte[40];
    private readonly byte[] _wbMapName = new byte[64];
    private readonly byte[] _wbSrcMap = new byte[40];
    private int _wbSrcCol = 27, _wbSrcRow = 28, _wbSrcW = 4, _wbSrcH = 5, _wbDstCol = 30, _wbDstRow = 30;
    private bool _wbStampDoodads = true, _wbStampWmos;
    private readonly byte[] _wbSubName = new byte[64];
    private int _wbSubArea = 7002;
    private float _wbSubRadius = 120f;
    private int _wbPortalId = 7001, _wbPortalTargetMap, _wbPortalMinLevel;
    private float _wbPortalRadius = 6f, _wbPortalTx, _wbPortalTy, _wbPortalTz, _wbPortalTo;
    private bool _wbMapFormInit;

    private void DrawWbMapsSection()
    {
        if (!_wbMapFormInit)
        {
            _wbMapFormInit = true;
            WbSetBuf(_wbMapDir, "Gilneas"); WbSetBuf(_wbMapName, "Gilneas"); WbSetBuf(_wbSrcMap, "Azeroth");
        }
        float w = CreatorControlWidth;
        ImGui.TextDisabled("New map (stamped from stock terrain)");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Map id (800+)", ref _wbMapId);
        ImGui.SetNextItemWidth(w); ImGui.InputText("Directory##wb-map", _wbMapDir, (uint)_wbMapDir.Length);
        ImGui.SetNextItemWidth(w); ImGui.InputText("Name##wb-map", _wbMapName, (uint)_wbMapName.Length);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.Combo("Type", ref _wbMapType, "Continent\0Dungeon (5)\0Raid\0");
        if (_wbMapType > 0) { ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Player limit", ref _wbMapPlayers); }
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Zone area id (7000+)", ref _wbMapArea);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Zone level", ref _wbMapLevel);
        ImGui.SetNextItemWidth(w); ImGui.InputText("Stamp from map dir##wb-map", _wbSrcMap, (uint)_wbSrcMap.Length);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Source col", ref _wbSrcCol);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Source row", ref _wbSrcRow);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Tiles wide", ref _wbSrcW);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Tiles tall", ref _wbSrcH);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Place at col", ref _wbDstCol);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Place at row", ref _wbDstRow);
        ImGui.Checkbox("Keep trees & props", ref _wbStampDoodads); ImGui.SameLine();
        ImGui.Checkbox("Keep buildings", ref _wbStampWmos);
        if (_controller is not null)
        {
            var (c, r) = MSUIClient.Engine.UI.WorldBuilderLaw.TileOf(_controller.Position.X, _controller.Position.Y);
            ImGui.TextDisabled($"You stand in {_config.Start.MapName} tile {c},{r}.");
        }
        bool ready = _wbPackId != 0 && WbText(_wbMapDir).Length > 0 && _wbMapId >= 800 && _wbMapArea >= 7000;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton("Create map")) WbPost($"map {WbText(_wbMapName)} ({_wbMapId})", WbNewMapItems(
            _wbMapId, WbText(_wbMapDir), WbText(_wbMapName), _wbMapType, _wbMapPlayers, (uint)_wbMapArea, _wbMapLevel,
            WbText(_wbSrcMap), _wbSrcCol, _wbSrcRow, _wbSrcW, _wbSrcH, _wbDstCol, _wbDstRow, _wbStampDoodads, _wbStampWmos));
        if (!ready) ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Subzone around you");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Area id##wb-sub", ref _wbSubArea);
        ImGui.SetNextItemWidth(w); ImGui.InputText("Name##wb-sub", _wbSubName, (uint)_wbSubName.Length);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Radius##wb-sub", ref _wbSubRadius);
        if (CreatorButton("Add subzone here") && _controller is not null && WbText(_wbSubName).Length > 0)
            WbAddSubzone((uint)_wbSubArea, WbText(_wbSubName), _controller.Position, _wbSubRadius);

        ImGui.Separator();
        ImGui.TextDisabled("Portal from where you stand");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Trigger id (7000+)", ref _wbPortalId);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Radius##wb-portal", ref _wbPortalRadius);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("To map", ref _wbPortalTargetMap);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("To x", ref _wbPortalTx);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("To y", ref _wbPortalTy);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("To z", ref _wbPortalTz);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("To facing", ref _wbPortalTo);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Min level", ref _wbPortalMinLevel);
        if (CreatorButton("Create portal here") && _controller is not null)
            WbPost($"portal {_wbPortalId}", WbPortalItems((uint)_wbPortalId, $"World Pack portal {_wbPortalId}", _config.Start.Map,
                _controller.Position, _wbPortalRadius, _wbPortalTargetMap, new Vector3(_wbPortalTx, _wbPortalTy, _wbPortalTz),
                _wbPortalTo, _wbPortalMinLevel));
        ImGui.TextDisabled("Dungeon = a Dungeon-type map + a portal in + a portal out.");
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
        string srcDir, int srcCol, int srcRow, int wide, int tall, int dstCol, int dstRow, bool doodads, bool wmos)
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
            WbAreaDbc(area, (uint)mapId, 0, name, level),
            new JsonObject { ["kind"] = "dbrow:map_template", ["body"] = new JsonObject
            {
                ["entry"] = mapId, ["patch"] = 0, ["parent"] = 0, ["map_type"] = type == 0 ? 0 : type == 1 ? 1 : 2,
                ["linked_zone"] = type == 0 ? 0 : (int)area, ["player_limit"] = type == 0 ? 0 : players,
                ["reset_delay"] = type == 2 ? 7 : 0, ["ghost_entrance_map"] = -1, ["ghost_entrance_x"] = 0, ["ghost_entrance_y"] = 0,
                ["map_name"] = name, ["script_name"] = "",
            } },
            WbAreaRow(area, (uint)mapId, 0, name, level),
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
    internal static JsonObject WbAreaDbc(uint area, uint map, uint parent, string name, int level) =>
        new() { ["kind"] = "dbc:AreaTable", ["key"] = area.ToString(CultureInfo.InvariantCulture), ["body"] = new JsonObject
        {
            ["cloneFrom"] = 130,
            ["fields"] = new JsonObject { ["1"] = map, ["2"] = parent, ["3"] = WbAreaBit(area), ["10"] = level, ["11"] = name },
        } };

    internal static JsonObject WbAreaRow(uint area, uint map, uint parent, string name, int level) =>
        new() { ["kind"] = "dbrow:area_template", ["body"] = new JsonObject
        {
            ["entry"] = area, ["map_id"] = map, ["zone_id"] = parent, ["explore_flag"] = WbAreaBit(area),
            ["flags"] = 64, ["area_level"] = level, ["name"] = name, ["team"] = 0, ["liquid_type"] = 0,
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
        items.Add(WbAreaDbc(area, (uint)map, zone, name, _wbMapLevel));
        items.Add(WbAreaRow(area, (uint)map, zone, name, _wbMapLevel));
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
