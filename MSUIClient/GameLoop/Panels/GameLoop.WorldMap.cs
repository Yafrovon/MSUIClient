using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _worldMapOpen;
    private bool _worldMapKeyWasDown;
    private int _worldMapZoom = 1;
    private uint _worldMapSelectedAreaId;
    private uint? _worldMapSelectedMapId;
    private bool _worldMapContinentMenuOpen;
    private bool _worldMapZoneMenuOpen;
    private WorldMapAreaCatalog? _worldMapAreas;
    private bool _worldMapAreasLoaded;
    private bool _worldMapReloadPending;

    private void UpdateWorldMapInput(bool typing)
    {
        // A pack download completes while Creator UI is drawing. Release old GL handles on the next
        // update, before the next frame can submit map/minimap draw commands using those handles.
        if (_worldMapReloadPending) ReloadWorldMapData();
        bool down = BindingDown(GameBinding.OpenWorldMap);
        if (down && !_worldMapKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            // In the free view the same binding opens the RTS commander map; the
            // vanilla map stays exactly what it always was everywhere else.
            if (_freeView) ToggleCommanderMap();
            else ToggleWorldMap();
        }
        _worldMapKeyWasDown = down;

        // The dedicated Commander Map command (RTS Controls) ships UNBOUND: the free-view
        // overload above is how that map has always opened, and this is simply the seat for a
        // player who would rather not overload their World Map key.
        if (BindingPressedEdge(GameBinding.RtsCommanderMap, typing) &&
            _freeView && _net is { IsInWorld: true })
            ToggleCommanderMap();
    }

    private void ReloadWorldMapData()
    {
        _worldMapReloadPending = false;
        _worldMapAreasLoaded = false;
        _worldMapAreas = null;
        _worldMapSupportingDataLoaded = false;
        _worldMapOverlays = null;
        _worldMapHighlights = null;
        _worldMapHits = null;
        _worldMapDirTiles.Clear();
        _areasLoaded = false;
        _areas = null;
        _minimapTileMap = null;
        _minimapPreparedTextureTasks.Clear();
        _minimapActiveWarmKey = null;
        _minimapActiveWarmReady = false;
        _gameplayArt?.ClearMapCache();
    }

    private void ToggleWorldMap()
    {
        _worldMapOpen = !_worldMapOpen;
        if (!_worldMapOpen) return;
        // Vanilla WorldMapFrame OnShow always starts at the character's current zone.
        _worldMapZoom = 1;
        _worldMapSelectedAreaId = 0;
        _worldMapSelectedMapId = null;
        _worldMapContinentMenuOpen = false;
        _worldMapZoneMenuOpen = false;
    }

    private void DrawWorldMapFrame()
    {
        if (!_worldMapOpen || _gameplayArt is null || _net is null) return;
        // The exploration mask is the SESSION character's private field set (a possessed
        // bot's PLAYER_EXPLORED_ZONES never streams to the commander), so the overlays stay
        // yours whoever you are driving. It is not a gate on the frame: in the Command View
        // the own body can be unstreamed far away, and the map must still open.
        bool haveSessionPlayer = _entities.TryGet(_net.PlayerGuid, out WorldEntity sessionPlayer);
        Vector2 display = ImGui.GetIO().DisplaySize;
        WorldMapUiLaw.FrameLayout frame = WorldMapUiLaw.Frame(display);
        if (!BeginVanillaWindow("##world-map", frame.LogicalOrigin, frame.LogicalSize,
                out ImDrawListPtr dl, out Vector2 origin, out float s, frame.Scale))
        { ImGui.End(); return; }
        // FULLSCREEN_DIALOG strata: submit on the foreground list so both the
        // letterbox and authored map cover every ordinary or developer window.
        dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, display, 0xff000000);
        string[] shellRows = ["Top", "Middle", "Bottom"];
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            WorldMapUiLaw.LogicalRect tile = WorldMapUiLaw.ShellTile(row, col);
            DrawArt(dl, $@"Interface\WorldMap\UI-WorldMap-{shellRows[row]}{col + 1}",
                tile.ScaledMin(origin, s), tile.Size, s);
        }

        EnsureWorldMapAreas();
        EnsureAreaTableForMinimap();
        EnsureWorldMapSupportingData();
        // LIVE map, not the roster row. _net.Player is the SMSG_CHAR_ENUM snapshot, written
        // once at character pick and never again; the map you are actually on is adopted
        // into _config.Start.Map on LOGIN_VERIFY_WORLD / NEW_WORLD and by every local travel
        // path (portals, instances, creator). Reading the snapshot here meant the marker
        // compared the displayed zone's map against the LOGIN map, so the first boat, portal
        // or instance killed the player arrow for the rest of the session. Reported 2026-09-01.
        uint playerMap = checked((uint)Math.Max(0, _config.Start.Map));
        uint currentZoneId = _areas?.ParentZoneId(_minimapAreaId) ?? 0;
        // The roster zone is only meaningful while still on the roster map.
        if (currentZoneId == 0 && _net.Player is { } roster && roster.Map == playerMap)
            currentZoneId = roster.Zone;
        uint viewedAreaId = _worldMapSelectedAreaId != 0
            ? _worldMapSelectedAreaId : currentZoneId;
        WorldMapAreaInfo area = default;
        bool haveArea = _worldMapZoom > 0 &&
                        _worldMapAreas?.TryGetArea(viewedAreaId, out area) == true;
        bool haveMapArea = haveArea;
        List<(int Col, int Row, uint Texture)>? mosaic = null;
        if (!haveArea)
        {
            uint displayMap = _worldMapSelectedMapId ?? playerMap;
            if (_worldMapSelectedMapId is null && _worldMapAreas?.TryGetArea(currentZoneId,
                    out WorldMapAreaInfo currentZoneArea) == true)
                displayMap = currentZoneArea.MapId;
            haveMapArea = _worldMapAreas?.TryGetContinent(displayMap, out area) == true ||
                          TryWorldMapMosaic(displayMap, playerMap, out area, out mosaic) ||
                          _worldMapAreas?.TryGetContinent(playerMap, out area) == true ||
                          _worldMapAreas?.TryGetContinent(0, out area) == true;
        }
        string directory = !haveMapArea || string.IsNullOrWhiteSpace(area.Directory)
            ? (playerMap == 1 ? "Kalimdor" : "Azeroth") : area.Directory;
        Vector2 mapMin = WorldMapUiLaw.MapRect.ScaledMin(origin, s);
        Vector2 mapSize = WorldMapUiLaw.MapRect.ScaledSize(s);
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        int painted = 0;
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt.Handle(
                $@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            painted++;
            WorldMapUiLaw.LogicalRect tile = WorldMapUiLaw.DetailTile(row, col);
            Vector2 min = tile.ScaledMin(mapMin, s);
            dl.AddImage((nint)texture, min, min + tile.ScaledSize(s));
        }
        // A pack map: its own minimap tiles, placed by the same bounds the marker and dots use.
        DrawWorldMapTiles(dl, area, mapMin, mapSize, mosaic ?? []);
        // A pack ZONE on a continent (Gilneas on the Eastern Kingdoms): its WorldMapArea row is generated by the
        // pack build and has no painted art - draw its map's minimap tiles inside the row's bounds.
        if (mosaic is null && painted == 0 && haveMapArea && WorldMapMapDirectory(area.MapId) is { } zoneDir)
        {
            // Every minimap tile in view (the zone and its neighbours), and where Blizzard has NO minimap (open
            // ocean without an ADT) the open-sea image it shares across its empty tiles - the most-shared
            // texture of the map. 2026-09-27: those gaps showed the frame's golden parchment beside Gilneas.
            // A map with no widely shared image (a small dungeon) has no sea and gets no fill.
            var all = WorldMapDirectoryTiles(zoneDir);
            uint sea = WorldMapUiLaw.SharedSeaTexture(all.Select(t => t.Texture));
            var have = all.Select(t => (t.Col, t.Row)).ToHashSet();
            var zoneTiles = all.Where(t => WorldMapTileVisible(area, t.Col, t.Row)).ToList();
            if (sea != 0)
                for (int col = 0; col < 64; col++)
                for (int row = 0; row < 64; row++)
                    if (!have.Contains((col, row)) && WorldMapTileVisible(area, col, row)) zoneTiles.Add((col, row, sea));
            DrawWorldMapTiles(dl, area, mapMin, mapSize, zoneTiles);
            WorldMapMosaicVerdict(area, zoneDir, zoneTiles.Count, "zone");
        }
        // Painted continents retain their authored coastlines. Pack zones join their normal ZMP hover
        // ownership and highlight data; minimap rectangles must never cover the continent artwork.
        dl.PopClipRect();

        if (haveArea && haveSessionPlayer)
            DrawWorldMapExploredOverlays(dl, area, sessionPlayer.Fields, mapMin, mapSize, s);

        ImGui.SetCursorScreenPos(mapMin);
        ImGui.InvisibleButton("##world-map-detail", mapSize);
        WorldMapAreaInfo[] continentRows = WorldMapContinents();
        WorldMapAreaInfo[] zoneRows = WorldMapZones(area.MapId);
        WorldMapDropdownGeometry continentDrop = WorldMapUiLaw.Continent(
            origin, s, continentRows.Length);
        WorldMapDropdownGeometry zoneDrop = WorldMapUiLaw.Zone(origin, s, zoneRows.Length);
        bool dropdownOwnsPointer = continentDrop.Contains(ImGui.GetMousePos(),
                                       _worldMapContinentMenuOpen) ||
                                   zoneDrop.Contains(ImGui.GetMousePos(),
                                       _worldMapZoneMenuOpen);
        bool mapHovered = ImGui.IsItemHovered() && !dropdownOwnsPointer;
        if (mapHovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
                           ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            CloseWorldMapDropdowns();
        WorldMapAreaInfo hoveredArea = default;
        bool haveHoveredArea = false;
        if (haveMapArea && !haveArea && mapHovered &&
            _worldMapHits?.TryResolveArea(area.MapId, area,
                CommanderMapUiLaw.Unproject(mapMin, mapSize, ImGui.GetMousePos()),
                out uint hoverAreaId) == true &&
            _worldMapAreas?.TryGetArea(hoverAreaId, out hoveredArea) == true)
        {
            haveHoveredArea = true;
            DrawWorldMapAreaHighlight(dl, hoveredArea, mapMin, mapSize);
            if (ImGui.IsItemClicked())
            {
                _worldMapSelectedAreaId = hoveredArea.AreaId;
                _worldMapSelectedMapId = hoveredArea.MapId;
                _worldMapZoom = 1;
                CloseWorldMapDropdowns();
            }
        }
        else if (haveArea && mapHovered && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _worldMapZoom = 0;
            _worldMapSelectedMapId = area.MapId;
            CloseWorldMapDropdowns();
        }

        DrawBattlefieldMapPositions(dl, haveMapArea, area, playerMap, mapMin, mapSize, s);

        // The marker is the body being DRIVEN, read the same way the minimap reads it: the
        // controller while it owns the body (client-authoritative movement), the entity
        // stream otherwise (free view, possessed bot pose while rooted, etc). Possessing a
        // bot, that is the bot. The object-store entity for the own body stops updating the
        // moment control moves elsewhere, which froze the arrow at the possession spot.
        if (haveMapArea && !string.IsNullOrWhiteSpace(area.Directory) &&
            TryGetWorldBodyPose(ControlledGuid, out WorldBodyPose drivenBody) &&
            WorldMapUiLaw.TryPlayerMarker(playerMap, area.MapId, drivenBody.Position,
                area.Left, area.Right, area.Top, area.Bottom,
                mapMin, mapSize, out Vector2 marker))
        {
            DrawMinimapPlayerArrow(dl, drivenBody.Orientation, marker, s);
        }
        DrawWorldMapPartyDots(dl, haveMapArea, area, playerMap, mapMin, mapSize, s);
        DrawWorldMapQuestHelperPins(dl, haveMapArea, area, mapMin, mapSize, s);
        DrawWorldMapGossipPoi(dl, haveMapArea, area, mapMin, mapSize, s);
        DrawWorldMapCorpseMarker(dl, haveMapArea, area, mapMin, mapSize, s);

        GameText.DrawCentered(dl, WorldMapUiLaw.TitleFont, "World Map",
            WorldMapUiLaw.At(origin, WorldMapUiLaw.TitleCenter, s), s);
        DrawWorldMapDropdowns(dl, origin, s, area.MapId, viewedAreaId,
            continentRows, zoneRows, continentDrop, zoneDrop);
        if (haveHoveredArea)
        {
            string hoverLabel = _areas?.ZoneName(hoveredArea.AreaId) ?? hoveredArea.Directory;
            GameText.DrawCentered(dl, WorldMapUiLaw.HoverLabelFont, hoverLabel,
                WorldMapUiLaw.HoverLabel(mapMin, mapSize, s), s);
        }
        if (haveArea && VanillaButton(dl, "##world-map-zoomout", "Zoom Out",
                WorldMapUiLaw.ViewAction.ScaledMin(origin, s),
                WorldMapUiLaw.ViewAction.Size, s))
        {
            _worldMapZoom = 0;
            _worldMapSelectedMapId = area.MapId;
            CloseWorldMapDropdowns();
        }
        else if (!haveArea && VanillaButton(dl, "##world-map-zone", "Current Zone",
                     WorldMapUiLaw.ViewAction.ScaledMin(origin, s),
                     WorldMapUiLaw.ViewAction.Size, s))
        {
            _worldMapSelectedAreaId = 0;
            _worldMapSelectedMapId = null;
            _worldMapZoom = 1;
            CloseWorldMapDropdowns();
        }
        Vector2 close = WorldMapUiLaw.Close.ScaledMin(origin, s);
        DrawImageButton(dl, "##world-map-close", close, WorldMapUiLaw.Close.ScaledSize(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked())
        {
            _worldMapOpen = false;
            CloseWorldMapDropdowns();
        }
        ImGui.End();
    }

    /// <summary>
    /// A World Content Pack map (id 800+) with no WorldMapArea row: the world map is the mosaic of its
    /// own minimap tiles (md5translate "dir:" entries the pack build writes - verifier G11), bounded by
    /// WorldMapUiLaw.TryMinimapMosaicBounds. Only the map the body is ON (its directory is the loaded
    /// one); stock maps and stock instances keep their vanilla behaviour exactly.
    /// </summary>
    private bool TryWorldMapMosaic(uint displayMap, uint playerMap, out WorldMapAreaInfo area,
        out List<(int Col, int Row, uint Texture)>? mosaic)
    {
        area = default;
        mosaic = null;
        if (displayMap < WorldMapUiLaw.PackMapIdBase || displayMap != playerMap || _gameplayArt is null) return false;
        EnsureMinimapTileMap();
        string directory = _adts?.MapName ?? _config.Start.MapName;
        if (_minimapTileMap is null || string.IsNullOrWhiteSpace(directory)) return false;
        string prefix = directory + @"\map";
        var tiles = new List<(int Col, int Row, uint Texture)>();
        foreach (var (logical, hashed) in _minimapTileMap)
        {
            if (!logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !logical.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;
            string[] cr = logical[prefix.Length..^4].Split('_');
            if (cr.Length != 2 || !int.TryParse(cr[0], out int col) || !int.TryParse(cr[1], out int row)) continue;
            uint texture = _gameplayArt.Handle(@"textures\Minimap\" + hashed);
            if (texture != 0) tiles.Add((col, row, texture));
        }
        if (!WorldMapUiLaw.TryMinimapMosaicBounds(tiles.Select(t => (t.Col, t.Row)),
                out float left, out float right, out float top, out float bottom))
            return false;
        area = new WorldMapAreaInfo(0, displayMap, 0, directory, left, right, top, bottom);
        mosaic = tiles;
        if (Environment.TickCount64 - _worldMapMosaicEmitMs > 2000)
        {
            _worldMapMosaicEmitMs = Environment.TickCount64;
            EmitInterface("world-map", "mosaic", "DRAWN", 0,
                FormattableString.Invariant($"map={displayMap};dir={directory};tiles={tiles.Count};left={left:F0};right={right:F0};top={top:F0};bottom={bottom:F0}"));
        }
        return true;
    }
    private long _worldMapMosaicEmitMs;

    /// <summary>Minimap tiles on the world map by the area's bounds (the same ones the marker and dots use).</summary>
    private static void DrawWorldMapTiles(ImDrawListPtr dl, WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize,
        IEnumerable<(int Col, int Row, uint Texture)> tiles)
    {
        const float tileYards = 533.33333f;
        foreach (var (col, row, texture) in tiles)
        {
            float u0 = ((32 - col) * tileYards - area.Left) / (area.Right - area.Left);   // west edge
            float u1 = ((31 - col) * tileYards - area.Left) / (area.Right - area.Left);   // east edge
            float v0 = ((32 - row) * tileYards - area.Top) / (area.Bottom - area.Top);    // north edge
            float v1 = ((31 - row) * tileYards - area.Top) / (area.Bottom - area.Top);    // south edge
            dl.AddImage((nint)texture, WorldMapUiLaw.MapPoint(mapMin, mapSize, u0, v0),
                WorldMapUiLaw.MapPoint(mapMin, mapSize, u1, v1));
        }
    }

    private static bool WorldMapTileVisible(WorldMapAreaInfo area, int col, int row)
    {
        const float tileYards = 533.33333f;
        float west = (32 - col) * tileYards, east = (31 - col) * tileYards, north = (32 - row) * tileYards, south = (31 - row) * tileYards;
        return west > Math.Min(area.Left, area.Right) && east < Math.Max(area.Left, area.Right) &&
               north > Math.Min(area.Top, area.Bottom) && south < Math.Max(area.Top, area.Bottom);
    }

    private string? WorldMapMapDirectory(uint mapId) =>
        mapId == (uint)Math.Max(0, _config.Start.Map) && (_adts?.MapName ?? _config.Start.MapName) is { Length: > 0 } here
            ? here : EnsureMapNames()?.Get((int)mapId)?.Directory;

    private readonly Dictionary<string, List<(int Col, int Row, uint Texture)>> _worldMapDirTiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every minimap tile of a map directory (md5translate "Dir\mapCC_RR.blp"), textures resolved once.</summary>
    private List<(int Col, int Row, uint Texture)> WorldMapDirectoryTiles(string directory)
    {
        if (_worldMapDirTiles.TryGetValue(directory, out var cached)) return cached;
        var tiles = new List<(int Col, int Row, uint Texture)>();
        EnsureMinimapTileMap();
        string prefix = directory + @"\map";
        foreach (var (logical, hashed) in _minimapTileMap ?? new())
        {
            if (!logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !logical.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;
            string[] cr = logical[prefix.Length..^4].Split('_');
            if (cr.Length != 2 || !int.TryParse(cr[0], out int col) || !int.TryParse(cr[1], out int row)) continue;
            uint texture = _gameplayArt?.Handle(@"textures\Minimap\" + hashed) ?? 0;
            if (texture != 0) tiles.Add((col, row, texture));
        }
        return _worldMapDirTiles[directory] = tiles;
    }

    private void WorldMapMosaicVerdict(WorldMapAreaInfo area, string directory, int tiles, string kind)
    {
        if (Environment.TickCount64 - _worldMapMosaicEmitMs <= 2000) return;
        _worldMapMosaicEmitMs = Environment.TickCount64;
        EmitInterface("world-map", "mosaic", "DRAWN", 0, FormattableString.Invariant(
            $"kind={kind};map={area.MapId};area={area.AreaId};dir={directory};tiles={tiles};left={area.Left:F0};right={area.Right:F0};top={area.Top:F0};bottom={area.Bottom:F0}"));
    }

    private WorldMapAreaInfo[] WorldMapContinents() => _worldMapAreas?.Continents
        .Where(static continent => continent.MapId is 0 or 1)
        .ToArray() ?? [];

    private WorldMapAreaInfo[] WorldMapZones(uint mapId) => _worldMapAreas?.Areas
        .Where(zone => zone.MapId == mapId)
        .OrderBy(zone => _areas?.ZoneName(zone.AreaId) ?? zone.Directory,
            StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private string WorldMapContinentName(uint mapId) => mapId switch
    {
        0 => "Eastern Kingdoms",
        1 => "Kalimdor",
        // A pack map names itself (Map.dbc, patch-7) - "Gilneas", "Greymane Fortress".
        >= WorldMapUiLaw.PackMapIdBase when EnsureMapNames()?.Get((int)mapId)?.Name is { Length: > 0 } name => name,
        _ => "Continent",
    };

    private MapTable? EnsureMapNames()
    {
        EnsureInstanceData();
        return _maps;
    }

    private void CloseWorldMapDropdowns()
    {
        _worldMapContinentMenuOpen = false;
        _worldMapZoneMenuOpen = false;
    }

    private void DrawWorldMapDropdowns(ImDrawListPtr draw, Vector2 origin, float scale,
        uint viewedMapId, uint viewedAreaId, WorldMapAreaInfo[] continents,
        WorldMapAreaInfo[] zones, in WorldMapDropdownGeometry continentGeometry,
        in WorldMapDropdownGeometry zoneGeometry)
    {
        DrawWorldMapDropdownCapsule(draw, "continent", "Continent",
            WorldMapContinentName(viewedMapId), continentGeometry, scale,
            ref _worldMapContinentMenuOpen, ref _worldMapZoneMenuOpen);
        DrawWorldMapDropdownCapsule(draw, "zone", "Zone",
            _worldMapZoom > 0 ? _areas?.ZoneName(viewedAreaId) ?? "" : "",
            zoneGeometry, scale, ref _worldMapZoneMenuOpen,
            ref _worldMapContinentMenuOpen);

        if (_worldMapContinentMenuOpen)
            DrawWorldMapDropdownList(draw, "continent", continentGeometry, scale,
                continents.Select(row => (WorldMapContinentName(row.MapId),
                    row.MapId == viewedMapId, (Action)(() =>
                    {
                        _worldMapSelectedMapId = row.MapId;
                        _worldMapSelectedAreaId = 0;
                        _worldMapZoom = 0;
                        CloseWorldMapDropdowns();
                    }))).ToArray());
        if (_worldMapZoneMenuOpen)
            DrawWorldMapDropdownList(draw, "zone", zoneGeometry, scale,
                zones.Select(row => (_areas?.ZoneName(row.AreaId) ?? row.Directory,
                    row.AreaId == viewedAreaId && _worldMapZoom > 0, (Action)(() =>
                    {
                        _worldMapSelectedMapId = row.MapId;
                        _worldMapSelectedAreaId = row.AreaId;
                        _worldMapZoom = 1;
                        CloseWorldMapDropdowns();
                    }))).ToArray());
    }

    private void DrawWorldMapDropdownCapsule(ImDrawListPtr draw, string id, string label,
        string selected, in WorldMapDropdownGeometry geometry, float scale,
        ref bool open, ref bool otherOpen)
    {
        bool toggled = VanillaDropdownCapsule(draw, $"##world-map-{id}-dropdown",
            geometry.FrameMin, scale, WorldMapUiLaw.Capsule, selected);
        GameText.Draw(draw, "GameFontNormalSmall", label,
            WorldMapUiLaw.At(geometry.FrameMin, WorldMapUiLaw.CapsuleLabel, scale), scale);
        if (toggled)
        {
            open = !open;
            otherOpen = false;
            PlayUiSound(DropdownCapsuleUiLaw.ToggleSound, "ui.world-map");
        }
    }

    private void DrawWorldMapDropdownList(ImDrawListPtr draw, string id,
        in WorldMapDropdownGeometry geometry, float scale,
        (string Text, bool Checked, Action Select)[] rows)
    {
        if (_skin is null || rows.Length == 0) return;
        _skin.DrawBackdrop(draw, geometry.ListMin, geometry.ListMin + geometry.ListSize,
            WowSkin.Dialog);
        int count = Math.Min(rows.Length, WorldMapUiLaw.MaximumRows);
        for (int i = 0; i < count; i++)
        {
            Vector2 rowMin = WorldMapUiLaw.RowMin(geometry, i, scale);
            Vector2 rowSize = WorldMapUiLaw.DropdownRow.ScaledSize(scale);
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##world-map-{id}-row-{i}", rowSize);
            if (rows[i].Checked || ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt!.AdditiveHandle(
                    DropdownCapsuleUiLaw.RowHighlight);
                if (highlight != 0) draw.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            if (rows[i].Checked)
            {
                uint check = _gameplayArt!.Handle(DropdownCapsuleUiLaw.RowCheck);
                if (check != 0)
                {
                    Vector2 checkMin = rowMin + DropdownCapsuleUiLaw.Check.Min * scale;
                    draw.AddImage((nint)check, checkMin,
                        checkMin + DropdownCapsuleUiLaw.Check.Size * scale);
                }
            }
            GameText.Draw(draw, DropdownCapsuleUiLaw.SelectionFont, rows[i].Text,
                rowMin + DropdownCapsuleUiLaw.RowTextOffset * scale, scale);
            if (clicked)
            {
                rows[i].Select();
                PlayUiSound(DropdownCapsuleUiLaw.RowSound, "ui.world-map");
            }
        }
    }

    /// <summary>The party as the stock yellow party dots (owner 2026-09-03: "we should see the
    /// rest of our party on the map"): every member except the driven body (that one is the
    /// arrow), placed from the entity stream when streamed, else from the last
    /// SMSG_PARTY_MEMBER_STATS position — the same two sources the minimap blips use.</summary>
    private void DrawWorldMapPartyDots(ImDrawListPtr draw, bool haveMapArea, WorldMapAreaInfo area,
        uint playerMap, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea) return;
        uint texture = _gameplayArt?.Handle(@"Interface\WorldMap\WorldMapPartyIcon") ?? 0;
        Vector2 size = Vector2.One * (16f * scale);
        var members = _partyMembers.Select(member => (member.Guid, member.Name)).ToList();
        if (LocalPlayerGuid != 0 && members.All(member => member.Guid != LocalPlayerGuid))
            members.Add((LocalPlayerGuid, ResolveUnitName(LocalPlayerGuid)));
        foreach ((ulong guid, string name) in members)
        {
            if (guid == 0 || guid == ControlledGuid || BattlefieldMapHasPlayer(guid)) continue;
            Vector3 position;
            if (_entities.TryGet(guid, out WorldEntity unit))
                position = UnitWorldPosition(unit);
            else if (_partyStats.TryGetValue(guid, out PartyMemberStatsSnapshot stats) &&
                     stats.PositionX is short x && stats.PositionY is short y)
                position = new(x, y, 0f);
            else continue;
            if (!WorldMapUiLaw.TryPlayerMarker(playerMap, area.MapId, position,
                    area.Left, area.Right, area.Top, area.Bottom, mapMin, mapSize, out Vector2 center))
                continue;
            Vector2 min = center - size * .5f;
            if (texture != 0) draw.AddImage((nint)texture, min, min + size);
            else draw.AddCircleFilled(center, MathF.Max(2f, 3f * scale), 0xff00d7ff);
            if (ImGui.IsMouseHoveringRect(min, min + size, false))
            {
                WorldMapUiLaw.TooltipSeat seat = WorldMapUiLaw.CorpseTooltipSeat(min, size, mapMin, mapSize);
                OfferOwnerAnchoredSharedGameTooltip(new("world-map-party", guid),
                    [new(string.IsNullOrEmpty(name) ? ResolveUnitName(guid) : name, GameTooltipTextTone.White)],
                    seat.Anchor, seat.Pivot);
            }
        }
    }

    private void DrawWorldMapCorpseMarker(ImDrawListPtr draw, bool haveMapArea,
        WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea || _corpseLocation is not { } corpse ||
            !DeathFrameUiLaw.TryWorldMapFraction(corpse.DisplayMap, area.MapId,
                corpse.Position, area.Left, area.Right, area.Top, area.Bottom,
                out Vector2 fraction)) return;
        uint texture = _gameplayArt?.Handle(DeathFrameUiLaw.CorpseMarkerPath) ?? 0;
        Vector2 size = Vector2.One * (DeathFrameUiLaw.WorldMapCorpseSize * scale);
        Vector2 center = mapMin + fraction * mapSize;
        Vector2 min = center - size * .5f;
        if (texture != 0)
            draw.AddImage((nint)texture, min, min + size,
                DeathFrameUiLaw.CorpseUvMin, DeathFrameUiLaw.CorpseUvMax);
        if (ImGui.IsMouseHoveringRect(min, min + size, false))
        {
            WorldMapUiLaw.TooltipSeat tooltipSeat =
                WorldMapUiLaw.CorpseTooltipSeat(min, size, mapMin, mapSize);
            OfferOwnerAnchoredSharedGameTooltip(new("world-map-corpse", 0),
                [new(DeathFrameUiLaw.CorpseTooltip, GameTooltipTextTone.Red)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
    }

    private void DrawWorldMapGossipPoi(ImDrawListPtr draw, bool haveMapArea,
        WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea || _gossipPoi is not { } poi) return;
        Vector3 poiPosition = new(poi.Position.X, poi.Position.Y, 0f);
        if (!DeathFrameUiLaw.TryWorldMapFraction((int)_gossipPoiMapId, area.MapId,
                poiPosition, area.Left, area.Right, area.Top, area.Bottom,
                out Vector2 fraction)) return;

        Vector2 size = Vector2.One * (WorldMapUiLaw.PoiIconSize * scale);
        Vector2 center = WorldMapUiLaw.MapPoint(mapMin, mapSize, fraction.X, fraction.Y);
        Vector2 min = center - size * .5f;
        uint texture = _gameplayArt?.Handle(@"Interface\Minimap\POIIcons") ?? 0;
        if (texture != 0 && AreaPoiCatalog.TryIconUv(
                poi.Icon, out Vector2 uvMin, out Vector2 uvMax))
            draw.AddImage((nint)texture, min, min + size, uvMin, uvMax);
        else
            draw.AddCircleFilled(center, MathF.Max(3f, 4f * scale), 0xff2020ff);

        if (ImGui.IsMouseHoveringRect(min, min + size, false))
        {
            WorldMapUiLaw.TooltipSeat tooltipSeat =
                WorldMapUiLaw.CorpseTooltipSeat(min, size, mapMin, mapSize);
            OfferOwnerAnchoredSharedGameTooltip(new("world-map-gossip-poi", poi.Data),
                [new(poi.Name, GameTooltipTextTone.White)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
    }

    private void DrawWorldMapAreaHighlight(ImDrawListPtr dl, WorldMapAreaInfo area,
        Vector2 mapMin, Vector2 mapSize)
    {
        if (_worldMapHighlights?.TryGetArea(area.AreaId,
                out WorldMapHighlightInfo highlight) != true) return;
        uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
        if (texture == 0) return;
        Vector2 min = WorldMapUiLaw.MapPoint(mapMin, mapSize,
            highlight.Bounds.Left, highlight.Bounds.Top);
        Vector2 max = WorldMapUiLaw.MapPoint(mapMin, mapSize,
            highlight.Bounds.Right, highlight.Bounds.Bottom);
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
            Vector2.Zero, WorldMapUiLaw.UvMaximum(highlight.UMax, highlight.VMax), 0xffffffff);
        dl.PopClipRect();
    }

    private void DrawWorldMapExploredOverlays(ImDrawListPtr dl, WorldMapAreaInfo area,
        ObjectFields fields, Vector2 mapMin, Vector2 mapSize, float s)
    {
        if (_worldMapOverlays is null || _areas is null) return;
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        foreach (WorldMapOverlayInfo overlay in _worldMapOverlays.ForMapArea(area.Id))
        {
            if (!overlay.IsExplored(areaId =>
                    _areas.ExplorationFlag(areaId) is uint flag &&
                    fields.PlayerHasExplored(flag))) continue;
            foreach (WorldMapOverlayChunk chunk in overlay.BuildChunks(area.Directory))
            {
                uint texture = _gameplayArt!.Handle(chunk.TexturePath);
                if (texture == 0) continue;
                WorldMapUiLaw.ScreenRect chunkRect = WorldMapUiLaw.PixelRect(mapMin,
                    chunk.OffsetX, chunk.OffsetY, chunk.PixelWidth, chunk.PixelHeight, s);
                Vector2 min = chunkRect.Min;
                Vector2 max = min + chunkRect.Size;
                dl.AddImage((nint)texture, min, max, Vector2.Zero,
                    WorldMapUiLaw.UvMaximum(chunk.UMax, chunk.VMax), 0xffffffff);
            }
        }
        dl.PopClipRect();
    }

    private void EnsureWorldMapAreas()
    {
        if(_worldMapAreasLoaded)return;_worldMapAreasLoaded=true;
        try{if(_mpq is not null)_worldMapAreas=WorldMapAreaCatalog.Load(_mpq);}
        catch(Exception e){Console.WriteLine($"[world-map] WorldMapArea load failed: {e.Message}");}
    }
}
