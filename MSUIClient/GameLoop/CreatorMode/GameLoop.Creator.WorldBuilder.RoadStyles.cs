using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: road styles (shared_docs/ROAD_STYLES.md, shared_docs/WORLD_BUILDER.md "Road looks").
//
// Every road and trail of the stock world was measured by tools/road-survey into Data/road-styles.json: the texture, its
// width, soft edge, strength, how ragged and patchy it is, and the shoulder texture under it. The Paths page offers them
// as "Road style" (searchable, this map first), and "Copy a road from the ground" samples the textures under a click and
// picks the style that matches - so a new road can be made to look exactly like the road next to it.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private void DrawWbRoadStylePicker(float controlWidth)
    {
        var styles = RoadStyleCatalog.Load();
        var current = _wbRoadStyleId is null ? null : styles.FirstOrDefault(s => s.Id == _wbRoadStyleId);
        string label = current is null ? "Custom" : current.Name + (_wbRoadStyleTuned ? " (adjusted)" : "");
        int map = _config.Start.Map;
        ImGui.SetNextItemWidth(MathF.Max(controlWidth, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Road style").X - 16f * CreatorUiScale));
        if (ImGui.BeginCombo("Road style", label, ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Search").X - 12f * CreatorUiScale);
            ImGui.InputText("Search##road-style-filter", _wbRoadStyleFilter, (uint)_wbRoadStyleFilter.Length);
            ObserveWorldBuilderUiItem("Road style search");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("A zone, \"road\", \"trail\" or part of a texture name.");
            string filter = WbText(_wbRoadStyleFilter).Trim();
            string? lastMap = null;
            foreach (var style in styles
                         .Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                     s.Texture.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(s => s.Map == map ? 0 : 1).ThenBy(s => s.Map).ThenBy(s => s.Zone, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(s => s.Kind == "road" ? 0 : s.Kind == "trail" ? 1 : 2).ThenByDescending(s => s.MeasuredLength))
            {
                string mapName = style.Map < 0 ? "Other" : _maps?.Get(style.Map)?.Name ?? $"Map {style.Map}";
                if (mapName != lastMap)
                {
                    ImGui.TextDisabled(mapName);
                    lastMap = mapName;
                }
                if (ImGui.Selectable($"{style.Name}##{style.Id}", style.Id == _wbRoadStyleId)) WbApplyRoadStyle(style);
                ObserveWorldBuilderUiItem("Road style: " + style.Id);   // scripted QA picks a style by its catalog id
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(WbRoadStyleTip(style));
            }
            ImGui.EndCombo();
        }
        ObserveWorldBuilderUiItem("Road style");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Looks measured from every road and trail in the game: texture, width, edge, wear and the dirt shoulder under it.\n" +
                             "Pick one to start from; every number below can still be adjusted.");

        WbPickButton("Copy a road from the ground", "a road to copy", WbCopyRoadAt);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click an existing road in the world: its look is picked from the measured styles.");
        if (current?.Example is { } example && current.Map == map && _controller is not null)
        {
            CreatorSameLineIfFits(CreatorButtonWidth("See it in the world"));
            if (CreatorButton("See it in the world")) _controller.Teleport(example.X, example.Y, example.Z + 25f);
            ObserveWorldBuilderUiItem("See it in the world");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fly to the longest stretch of this road the survey measured.");
        }

        if (_wbRoadSample is { Count: > 0 } sample)
        {
            ImGui.TextDisabled("Ground at the copied spot:");
            foreach (var (texture, weight) in sample)
            {
                ImGui.PushID(texture);
                ImGui.TextUnformatted($"{weight * 100f:0}%  {Path.GetFileNameWithoutExtension(texture)}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Road")) { _wbRoadTexture = _wbRoadLastTexture = texture; _wbRoadStyleTuned = _wbRoadStyleId is not null; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use this texture for the road itself.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Shoulder")) { _wbShoulderTexture = _wbShoulderLastTexture = texture; _wbRoadStyleTuned = _wbRoadStyleId is not null; }
                CreatorNoteItemRight();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use this texture for the shoulder under the road.");
                ImGui.PopID();
            }
        }
    }

    private static string WbRoadStyleTip(RoadStyle s)
    {
        string kind = s.Kind switch
        {
            "trail" => "A plain dirt that forms road-like trails in this zone.",
            "surface" => "A paving texture used here, but never as a long road (plazas, short spurs): a default look.",
            _ => "A road texture, measured where it forms roads in this zone.",
        };
        string verge = s.Shoulder is { } sh ? $"\nShoulder: {Path.GetFileNameWithoutExtension(sh.Texture)}, {sh.Extra:0.#} yd wider each side" : "\nNo shoulder";
        string measured = s.MeasuredLength > 0 ? $"\nMeasured over {s.MeasuredLength:0} yd of road" : "";
        return $"{kind}\n{s.Texture}\nWidth {s.Width:0.#} yd, soft edge {s.SoftEdge:0.#} yd, strength {s.Strength:0.##}, " +
               $"ragged {s.Ragged:0.#} yd, patchy {s.Patchy:0.##}{verge}{measured}";
    }

    private void WbApplyRoadStyle(RoadStyle style)
    {
        _wbRoadTexture = _wbRoadLastTexture = style.Texture;
        _wbRoadWidth = style.Width;
        _wbRoadSoft = style.SoftEdge;
        _wbRoadStrength = style.Strength;
        _wbRoadRagged = style.Ragged;
        _wbRoadPatchy = style.Patchy;
        if (_wbRoadSeed == 0) _wbRoadSeed = WbNewRoadSeed();
        if (style.Shoulder is { } sh)
        {
            _wbShoulderTexture = _wbShoulderLastTexture = sh.Texture;
            _wbShoulderExtra = sh.Extra;
            _wbShoulderSoft = sh.SoftEdge;
            _wbShoulderStrength = sh.Strength;
        }
        else _wbShoulderTexture = null;
        _wbRoadStyleId = style.Id;
        _wbRoadStyleTuned = false;
        _wbRoadPickerOpen = _wbShoulderPickerOpen = false;
        _wbMessage = $"Road style: {style.Name}.";
    }

    private static int WbNewRoadSeed(int previous = 0)
    {
        int seed;
        do seed = Random.Shared.Next(1, int.MaxValue); while (seed == previous);
        return seed;
    }

    /// <summary>"Copy a road from the ground": sample the stock textures under the click and take the matching style.</summary>
    private void WbCopyRoadAt(Vector3 at)
    {
        var sample = WbGroundTexturesAt(at);
        _wbRoadSample = sample;
        if (sample is null || sample.Count == 0) { _wbMessage = "No ground texture there (is the terrain loaded?)."; return; }
        var weights = sample.ToDictionary(s => s.Texture, s => s.Weight, StringComparer.OrdinalIgnoreCase);
        if (RoadStyleCatalog.Match(weights, _config.Start.Map, new Vector2(at.X, at.Y)) is { } style)
        {
            WbApplyRoadStyle(style);
            _wbMessage = $"Copied the road look: {style.Name}.";
        }
        else
        {
            _wbRoadTexture = _wbRoadLastTexture = sample[0].Texture;
            _wbRoadStyleId = null;
            _wbMessage = $"No measured road uses these textures; the road texture is now {Path.GetFileNameWithoutExtension(sample[0].Texture)}. " +
                         "Use the Road / Shoulder buttons to pick others.";
        }
    }

    /// <summary>Visible weight of each STOCK ground texture around a point (5x5 alpha pixels, ~2.5 yd), strongest first. A
    /// tile carrying the road preview is read from its saved original layers, so the preview never copies itself.</summary>
    private List<(string Texture, float Weight)>? WbGroundTexturesAt(Vector3 at)
    {
        var (col, row) = WorldBuilderLaw.TileOf(at.X, at.Y);
        if (_adts is null || !_adts.TryPeek(col, row, out var adt) || adt?.Chunks is not { } chunks) return null;
        _wbPaintSnapshots.TryGetValue((col, row), out var snap);
        var textures = snap?.Textures ?? adt.Textures;
        float px = TerrainPaintLaw.ChunkSize / TerrainPaintLaw.AlphaSize;
        float ox = (32 - row) * WorldBuilderLaw.Tile - at.X, oy = (32 - col) * WorldBuilderLaw.Tile - at.Y;
        int cy = Math.Clamp((int)(ox / TerrainPaintLaw.ChunkSize), 0, 15), cx = Math.Clamp((int)(oy / TerrainPaintLaw.ChunkSize), 0, 15);
        int pr = Math.Clamp((int)((ox - cy * TerrainPaintLaw.ChunkSize) / px), 0, 63), pc = Math.Clamp((int)((oy - cx * TerrainPaintLaw.ChunkSize) / px), 0, 63);
        int index = Array.FindIndex(chunks, c => c is not null && c.IndexX == cx && c.IndexY == cy);
        if (index < 0) return null;
        var layers = snap is not null && index < snap.Layers.Length ? snap.Layers[index] : chunks[index]!.Layers;
        if (layers.Length == 0) return null;
        var w = TerrainPaintLaw.Weights(layers.Select((l, i) => i == 0 ? null : l.AlphaMap).ToList());
        var sum = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        for (int r = Math.Max(0, pr - 2); r <= Math.Min(63, pr + 2); r++)
            for (int c = Math.Max(0, pc - 2); c <= Math.Min(63, pc + 2); c++)
            {
                n++;
                for (int i = 0; i < layers.Length; i++)
                    if (layers[i].TextureIndex >= 0 && layers[i].TextureIndex < textures.Count)
                    {
                        string name = textures[layers[i].TextureIndex].Replace('/', '\\');
                        sum[name] = sum.GetValueOrDefault(name) + w[i][r * 64 + c];
                    }
            }
        return sum.Select(kv => (kv.Key, kv.Value / Math.Max(1, n))).Where(s => s.Item2 >= 0.02f)
            .OrderByDescending(s => s.Item2).ToList();
    }
}
