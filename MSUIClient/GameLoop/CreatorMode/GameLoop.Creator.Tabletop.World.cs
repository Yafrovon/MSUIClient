using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Tabletop;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P2 - the real world on the table (shared_docs/QUEST_TABLETOP.md §4): the near layer (a world of its
// own: terrain, WMO buildings, M2 props, water) streamed around the camera's ground target with the table's
// exaggeration and board cut, plus the real creature models standing where the world DB spawns them. Creature spawns
// come read-only from the web app's census (GET /QuestAtlas/Creatures for the board), and so do their models: each
// template carries its displayId and scale, so the table needs nothing local and works on any machine (a web app older
// than that falls back to the creator catalogue, creator-creatures.tsv). Creatures are view-only synthetic units owned
// by the table - never added to the game's entity store.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private TabletopNearScene? _ttNear;
    private bool _ttNearFailed;
    private readonly List<(WorldEntity Unit, Vector3 Spawn, float BaseScale, bool Critter)> _ttCreatures = new();
    private float _ttFigureScaleShown = 1f;
    private readonly List<WorldEntity> _ttUnitsThisFrame = new();
    private Task<(List<(uint Entry, Vector3 Pos, float Facing)> Spawns, Dictionary<uint, CreatorCreatureTable.Creature> Templates)>? _ttCensus;
    /// <summary>The census' templates for the current board (name, levels, rank, model): what the tooltips and cards read.</summary>
    private Dictionary<uint, CreatorCreatureTable.Creature> _ttCensusTemplates = new();
    private string _ttCensusStatus = "";
    private double _ttLastRenderAt;

    /// <summary>A new board: point the near layer at its map and fetch the creatures standing on it.</summary>
    private void TabletopNearBoard(TabletopLaw.Board board)
    {
        // The live table's near layer is the game's own world (LiveWorld.cs): no second scene, only the census.
        if (!_liveTableOpen)
        {
            if (!TabletopEnsureNear()) return;
            _ttNear!.SetMap(board.MapDirectory);
            _ttNear.View.SetBoard(board.Top, board.Bottom, board.Left, board.Right);
        }
        _ttCreatures.Clear();
        _ttCensusTemplates = new();
        _ttCensus = null;
        if (string.IsNullOrWhiteSpace(SuiWebAppUrl)) { _ttCensusStatus = "creatures: no web app configured"; return; }
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{SuiWebAppUrl.TrimEnd('/')}/QuestAtlas/Creatures?mapId={board.MapId}&top={board.Top}&bottom={board.Bottom}&left={board.Left}&right={board.Right}");
        _ttCensusStatus = "creatures: loading...";
        _ttCensus = Task.Run(async () =>
        {
            using var http = WebAppHttp.Create(TimeSpan.FromSeconds(30));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var models = new Dictionary<uint, CreatorCreatureTable.Creature>();
            if (doc.RootElement.TryGetProperty("templates", out var templates))
                foreach (var t in templates.EnumerateObject())
                    if (uint.TryParse(t.Name, out uint entry))
                    {
                        var v = t.Value;
                        string Str(string k) => v.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                        int Int(string k) => v.TryGetProperty(k, out var e) && e.TryGetInt32(out int i) ? i : 0;
                        models[entry] = new CreatorCreatureTable.Creature(entry, Str("name"), Str("subname"), Int("levelMin"), Int("levelMax"),
                            (byte)Int("rank"), (byte)Int("type"),
                            v.TryGetProperty("displayId", out var d) && d.TryGetUInt32(out uint display) ? display : 0u,
                            v.TryGetProperty("scale", out var sc) && sc.TryGetSingle(out float s) && s > 0 ? s : 1f);
                    }
            var list = new List<(uint, Vector3, float)>();
            foreach (var row in doc.RootElement.GetProperty("spawns").EnumerateArray())
            {
                var a = row.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (a.Length < 5) continue;
                uint entry = (uint)a[0];
                list.Add((entry, new Vector3(a[1], a[2], a[3]), a[4]));
            }
            return (list, models);
        });
    }

    private bool TabletopEnsureNear()
    {
        if (_ttNear is not null) return true;
        if (_ttNearFailed || _gl is null || _uploads is null || _assetWorkers is null || _mpq is null) return false;
        try
        {
            _ttNear = new TabletopNearScene(_gl, _config, _uploads, _assetWorkers, _mpq, radius: Math.Max(1, _config.Start.TileRadius));
            return true;
        }
        catch (Exception e)
        {
            _ttNearFailed = true;
            Console.WriteLine($"[tabletop] near layer unavailable: {e.Message}");
            return false;
        }
    }

    /// <summary>Per frame: follow the ground target, advance the staged load, adopt the creature census.</summary>
    private void TabletopNearUpdate()
    {
        if (_tabletop?.Board is not { } board) return;
        if (_ttNear is not null && !_liveTableOpen)
        {
            _ttNear.View.K = _tabletop.Exaggeration;
            _ttNear.View.BaseZ = _tabletop.BaseZ;
        }
        if (Settings.Creator.Tabletop.World && _ttNear is not null && !_liveTableOpen)
        {
            var focus = new Vector2(Math.Clamp(_ttTarget.X, board.Bottom, board.Top), Math.Clamp(_ttTarget.Y, board.Right, board.Left));
            _ttNear.Focus(focus);
            _ttNear.Step();
        }

        if (_ttCensus is { IsCompleted: true } census)
        {
            _ttCensus = null;
            if (census.IsFaulted) { _ttCensusStatus = "creatures: " + census.Exception?.GetBaseException().Message; return; }
            int missing = 0, fromCatalogue = 0;
            ulong n = 0;
            _ttCensusTemplates = census.Result.Templates;
            foreach (var (entry, pos, facing) in census.Result.Spawns)
            {
                _ttCensusTemplates.TryGetValue(entry, out var t);
                uint display = t.DisplayId;
                float scale = t.Scale;
                if (display == 0)
                {
                    // An older web app sends no displayId: the local creator catalogue, if this machine has one.
                    EnsureCreatorCreatures();
                    if (_creatorCreatures is not null && _creatorCreatures.TryGet(entry, out var c) && c.DisplayId != 0) { display = c.DisplayId; fromCatalogue++; }
                }
                if (display == 0) { missing++; continue; }
                float baseScale = scale > 0 ? scale : 1f;
                var fields = ObjectFields.ForSyntheticUnit((int)display, baseScale);
                fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                var unit = new WorldEntity
                {
                    Guid = 0xF130_7AB1_0000_0000UL | ++n, Type = ObjectTypeId.Unit, Entry = entry, Fields = fields,
                    Position = pos, Orientation = facing,
                };
                bool critter = (t.Type != 0 ? t.Type : TabletopCreatureInfo(entry).Type) == TabletopLaw.CritterType;
                _ttCreatures.Add((unit, pos, baseScale, critter));
            }
            _ttCensusStatus = $"creatures: {_ttCreatures.Count} on the board" + (fromCatalogue > 0 ? $" ({fromCatalogue} models from the local catalogue)" : "")
                + (missing > 0 ? $" ({missing} without a model)" : "");
            Console.WriteLine($"[tabletop] {_ttCensusStatus}");
        }
    }

    /// <summary>A creature's name, levels and rank for the table's tooltips and cards: the board's census first (any
    /// machine, live DB), the local creator catalogue only when the census does not know the entry.</summary>
    private CreatorCreatureTable.Creature TabletopCreatureInfo(uint entry)
    {
        if (_ttCensusTemplates.TryGetValue(entry, out var c) && !string.IsNullOrEmpty(c.Name)) return c;
        EnsureCreatorCreatures();
        return _creatorCreatures is not null && _creatorCreatures.TryGet(entry, out var cc) ? cc : default;
    }

    /// <summary>Creatures inside the near layer's loaded tiles, standing on the shown (exaggerated) ground, at the
    /// figure scale for the current zoom (TabletopLaw.FigureScale); critters only with their layer.</summary>
    private IReadOnlyList<WorldEntity> TabletopUnits()
    {
        _ttUnitsThisFrame.Clear();
        var s = Settings.Creator.Tabletop;
        IReadOnlySet<(int col, int row)>? tiles = _liveTableOpen ? _liveTableTiles : _ttNear?.ReadyTiles;
        if (tiles is null || tiles.Count == 0) return _ttUnitsThisFrame;
        float figure = TabletopLaw.FigureScale(s.FigureScale, _ttRadius);
        _ttFigureScaleShown = figure;
        // On the live table the world's renderer applies the figure scale itself (ScaleMultiplier), so no unit - live
        // or figure - carries it in its fields there; the offline near scene writes it per figure.
        float fieldFigure = _liveTableOpen ? 1f : figure;
        // The live world first (the truth inside the eye's bubble), then the living board's bots.
        if (_liveTableOpen && s.Creatures) LiveTableAppendUnits(_ttUnitsThisFrame);
        LiveAppendUnits(_ttUnitsThisFrame, tiles, fieldFigure);
        if (!s.Creatures) return _ttUnitsThisFrame;
        foreach (var (unit, spawn, baseScale, critter) in _ttCreatures)
        {
            if (critter && !s.Critters) continue;
            if (!tiles.Contains(TabletopLawTile(spawn))) continue;
            if (LiveTableInBubble(spawn)) continue;   // the live world shows who is really there
            float ground = TtGroundAt(spawn.X, spawn.Y) ?? spawn.Z;
            // Feet stay on the shown ground (spawn height above it is true yards; a grown figure grows upward).
            unit.Position = spawn with { Z = spawn.Z + TtLift(ground) };
            unit.Fields.SetU32(ObjectFields.OBJECT_SCALE_X, BitConverter.SingleToUInt32Bits(baseScale * fieldFigure));
            _ttUnitsThisFrame.Add(unit);
            if (_ttUnitsThisFrame.Count >= 1500) break;
        }
        return _ttUnitsThisFrame;
    }

    /// <summary>True ground under (x, y) in the table's near layer: the live world's terrain on the live table, the
    /// offline near scene's otherwise.</summary>
    private float? TtGroundAt(float x, float y) => _liveTableOpen ? _terrain?.SampleHeight(x, y) : _ttNear?.GroundAt(x, y);

    /// <summary>How far the table lifts something standing on <paramref name="ground"/>.</summary>
    private float TtLift(float ground) => _liveTableOpen ? _liveTableView.Lift(ground) : _ttNear?.View.Lift(ground) ?? 0f;

    private static (int col, int row) TabletopLawTile(Vector3 p)
        => ((int)MathF.Floor(32f - p.Y / TabletopLaw.TileYards), (int)MathF.Floor(32f - p.X / TabletopLaw.TileYards));

    /// <summary>The table's warm light in the world renderers' terms (their colours are display values).</summary>
    private TabletopNearScene.NearLighting TabletopNearLighting()
    {
        float span = _tabletop?.Board?.Span ?? 3000f;
        var sun = Vector3.Normalize(TabletopLaw.SunOffset(span));
        // Fog fades to the table's background exactly as the far layer shows it (tone-mapped).
        var fog = TabletopLaw.ToneMap(TabletopLaw.SrgbToLinear(TabletopLaw.Rgb(TabletopLaw.Background)));
        var sky = TabletopLaw.Rgb(TabletopLaw.HemiSky);
        var ground = TabletopLaw.Rgb(TabletopLaw.HemiGround);
        return new TabletopNearScene.NearLighting(sun, TabletopLaw.Rgb(TabletopLaw.Sun), 0.95f,
            Vector3.Lerp(ground, sky, 0.7f), 0.55f, fog, span * TabletopLaw.FogNearSpans, span * TabletopLaw.FogFarSpans,
            TabletopLaw.ShownWater(sun.Z), TabletopLaw.WaterAlpha);
    }

    /// <summary>The near layer's draw, run inside the table's scene pass.</summary>
    private Action? TabletopNearDraw()
    {
        if (_ttNear is null || !Settings.Creator.Tabletop.World) return null;
        double now = NowSeconds();
        float dt = _ttLastRenderAt > 0 ? (float)Math.Clamp(now - _ttLastRenderAt, 0, 0.25) : 0f;
        _ttLastRenderAt = now;
        var units = TabletopUnits();
        var light = TabletopNearLighting();
        return () => _ttNear.Render(_ttCamera, light, units, dt);
    }
}
