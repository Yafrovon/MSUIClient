using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Protocol-only observations of the same mounted catalogs and GPU asset path used by the player map.
    // Nothing here supplies replacement map data or changes normal gameplay decisions.
    private void RunLiveWorldMapProbe(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5) throw new ArgumentException("worldmap-probe <map> <worldX> <worldY> <expectedArea>");
        uint map = uint.Parse(p[1], CultureInfo.InvariantCulture), expected = uint.Parse(p[4], CultureInfo.InvariantCulture);
        float worldX = float.Parse(p[2], CultureInfo.InvariantCulture), worldY = float.Parse(p[3], CultureInfo.InvariantCulture);
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY)) throw new ArgumentException("worldmap probe coordinates must be finite");
        if (_worldMapReloadPending) ReloadWorldMapData();
        EnsureWorldMapAreas(); EnsureAreaTableForMinimap(); EnsureWorldMapSupportingData();
        if (_mpq is null || _worldMapAreas is null || _worldMapHits is null || _worldMapHighlights is null ||
            !_worldMapAreas.TryGetContinent(map, out var continent) || !_worldMapHits.TryGetMap(map, out var zoneMap))
        {
            Log(false, $"{line} reason=mounted-continent-catalog-missing");
            EmitInterface("world-map", "probe", "FAIL", 0, $"map={map};expected={expected};reason=mounted-continent-catalog-missing");
            return;
        }
        bool onMap = worldY <= continent.Left && worldY >= continent.Right && worldX <= continent.Top && worldX >= continent.Bottom;
        Vector2 point = new(continent.X(worldY), continent.Y(worldX));
        bool resolved = _worldMapHits.TryResolveArea(map, continent, point, out uint actual);
        zoneMap.TryRawAreaId(continent, point, out uint raw);
        bool pass = onMap && (expected == 0 ? !resolved : resolved && actual == expected);
        string name = resolved ? _areas?.ZoneName(actual) ?? "" : "";
        string art = "none";
        if (expected != 0)
        {
            bool haveHighlight = _worldMapHighlights.TryGetArea(expected, out var highlight);
            bool haveMask = _worldMapHighlights.TryLoadMask(_mpq, expected, out var mask);
            uint handle = haveHighlight ? _gameplayArt?.AdditiveHandle(highlight.TexturePath) ?? 0 : 0;
            bool validMask = haveMask && mask.HasVisiblePixels && mask.Width == 128 && mask.Height == highlight.TextureFileHeight;
            bool pointLit = haveMask && mask.Contains(point);
            pass &= !string.IsNullOrWhiteSpace(name) && haveHighlight && validMask && handle != 0;
            art = haveHighlight ? $"{highlight.TexturePath};texture={handle};pixels={(haveMask ? mask.Width : 0)}x{(haveMask ? mask.Height : 0)};" +
                $"logicalHeight={highlight.TexturePixelHeight};mask={validMask};pointLit={pointLit}" : "missing";
        }
        string detail = FormattableString.Invariant($"map={map};world={worldX:F3}|{worldY:F3};expected={expected};raw={raw};area={actual};resolved={resolved};name={name};highlight={art}");
        EmitInterface("world-map", "probe", pass ? "PASS" : "FAIL", 0, detail);
        Log(pass, $"{line} {detail}");
    }

    private void OpenLiveWorldMapContinent(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2) throw new ArgumentException("worldmap-continent <map>");
        uint map = uint.Parse(p[1], CultureInfo.InvariantCulture);
        if (_worldMapReloadPending) ReloadWorldMapData();
        EnsureWorldMapAreas(); EnsureAreaTableForMinimap(); EnsureWorldMapSupportingData();
        if (_worldMapAreas?.TryGetContinent(map, out var continent) != true)
        {
            Log(false, $"{line} reason=mounted-continent-catalog-missing");
            return;
        }
        CloseLiveRunPanels();
        _liveGuiPointer = null;
        _liveGuiDown = _liveGuiRightDown = false;
        _worldMapOpen = true;
        _worldMapSelectedMapId = map;
        _worldMapSelectedAreaId = 0;
        _worldMapZoom = 0;
        CloseWorldMapDropdowns();
        int painted = Enumerable.Range(1, 12).Count(i =>
            _gameplayArt?.Handle($@"Interface\WorldMap\{continent.Directory}\{continent.Directory}{i}.blp") is > 0);
        string detail = $"map={map};directory={continent.Directory};paintedTiles={painted};zoom={_worldMapZoom}";
        EmitInterface("world-map", "continent", painted == 12 ? "OPEN" : "FAIL", 0, detail);
        Log(painted == 12, $"{line} {detail}");
    }

    private void PositionLiveWorldMapHover(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 2 && p[1] == "off")
        {
            _liveGuiPointer = null;
            _liveGuiDown = _liveGuiRightDown = false;
            Log(true, line);
            return;
        }
        if (p.Length != 4) throw new ArgumentException("worldmap-hover <map> <worldX> <worldY> | off");
        uint map = uint.Parse(p[1], CultureInfo.InvariantCulture);
        float x = float.Parse(p[2], CultureInfo.InvariantCulture), y = float.Parse(p[3], CultureInfo.InvariantCulture);
        OpenLiveWorldMapContinent($"worldmap-continent {map}");
        if (_worldMapAreas?.TryGetContinent(map, out var continent) != true ||
            !float.IsFinite(x) || !float.IsFinite(y) || x < continent.Bottom || x > continent.Top || y < continent.Right || y > continent.Left)
        {
            Log(false, $"{line} reason=point-outside-continent");
            return;
        }
        var frame = WorldMapUiLaw.Frame(ImGui.GetIO().DisplaySize);
        Vector2 mapMin = WorldMapUiLaw.MapRect.ScaledMin(frame.LogicalOrigin * frame.Scale, frame.Scale);
        Vector2 mapSize = WorldMapUiLaw.MapRect.ScaledSize(frame.Scale);
        _liveGuiPointer = WorldMapUiLaw.MapPoint(mapMin, mapSize, continent.X(y), continent.Y(x));
        // The existing protocol mouse proxy feeds the ordinary ImGui frame. The real map then resolves
        // hover ownership, paints the ADD art and chooses its label. This command supplies only input.
        string detail = FormattableString.Invariant($"map={map};world={x:F3}|{y:F3};pointer={_liveGuiPointer.Value.X:F2}|{_liveGuiPointer.Value.Y:F2};source=protocol-mouse-input");
        EmitInterface("world-map", "hover-input", "POSITIONED", 0, detail);
        Log(true, $"{line} {detail}");
    }
}
