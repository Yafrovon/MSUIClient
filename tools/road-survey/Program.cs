// road-survey: every road and trail painted on the ground of every vanilla map, measured from the stock ADTs, and
// turned into World Builder road styles (shared_docs/ROAD_STYLES.md, shared_docs/WORLD_BUILDER.md "Road looks").
//
//   dotnet run -c Release --project tools/road-survey             survey + write the catalog and the report
//   dotnet run -c Release --project tools/road-survey -- --pins   print the pinned RoadAmountAt values (the client wire
//                                                                 check and the web app's tests assert these)
//
// Reads GameData/Data PAST patch-7.MPQ (World Content Packs), so the survey sees the stock world, not your edits.
//
// Per tile, every ground texture becomes a 512x512 visible-weight grid (about 1 yd per cell; TerrainPaintLaw.Weights).
// A texture's >= 50% cells split into connected pieces; a piece is ROAD-LIKE when it is a long band: width 2A/P between
// 2 and 16 yd, length >= 30 yd and at least 5x its width. Named road textures (road, path, cobble, brick, ...) are styles
// wherever they are used; an unnamed texture (a plain dirt) becomes a TRAIL style where it forms enough road-like bands.
// For each (texture, zone) the road-like pieces give the look:
//   width      length-weighted mean of 2A/P
//   strength   mean weight in the band's interior
//   soft edge  yards outside the 50% edge until the texture fades below 8%, doubled (the 50% point is mid-fade)
//   ragged     edge wobble from the perimeter ratio raw / smoothed:  a = (14 / 2pi) * sqrt(r^2 - 1)
//   patchy     share of the closed band where the texture is below 50%
//   shoulder   the texture most ENRICHED in the first rings outside the band compared to 16 yd out (a dirt verge);
//              its extra width is where that excess falls below half its peak
//   ground     the texture that dominates 14-16 yd out
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Contains("--pins"))
{
    Pins.Print();
    return 0;
}

string root = ClientConfig.FindRepoRoot();
string dataDir = Path.Combine(root, "GameData", "Data");
const string PackArchive = "patch-7.MPQ";
using var mpq = new MpqMount(dataDir);
var maps = MapTable.Parse(mpq.ReadFile(MapTable.MpqPath) ?? throw new InvalidOperationException("Map.dbc missing"))!;
var areas = AreaTableCatalog.Parse(mpq.ReadFile(AreaTableCatalog.MpqPath) ?? throw new InvalidOperationException("AreaTable.dbc missing"))!;

var groups = new ConcurrentDictionary<(string Texture, int Map, string Zone), Group>();
var used = new ConcurrentDictionary<string, TextureUse>(StringComparer.OrdinalIgnoreCase);
var mapSummary = new List<(int Id, string Name, int Tiles)>();
int onlyMap = args.SkipWhile(a => a != "--map").Skip(1).Select(int.Parse).DefaultIfEmpty(-1).First();

// Unplayable test content: the Development map and the unfinished Emerald Dream. (Designer Island and Programmer Isle,
// the GM islands on the continents, are dropped by zone below.)
var devMaps = new HashSet<int> { 169, 451 };
foreach (var map in maps.All)
{
    if (onlyMap >= 0 && map.Id != onlyMap) continue;
    if (devMaps.Contains(map.Id)) continue;
    var wdtBytes = mpq.ReadFileExcluding($@"World\Maps\{map.Directory}\{map.Directory}.wdt", PackArchive);
    var wdt = wdtBytes is null ? null : WdtFile.Parse(wdtBytes);
    if (wdt is null || wdt.UsesGlobalWmo || wdt.TileCount == 0) continue;
    int scanned = 0;
    Parallel.ForEach(wdt.Tiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, tile =>
    {
        var bytes = mpq.ReadFileExcluding($@"World\Maps\{map.Directory}\{map.Directory}_{tile.Col}_{tile.Row}.adt", PackArchive);
        var adt = bytes is null ? null : AdtTerrainReader.Parse(bytes, tile.Row, tile.Col);
        if (adt?.Chunks is null) return;
        Interlocked.Increment(ref scanned);
        Survey.Tile(map.Id, tile.Col, tile.Row, adt, areas, groups, used);
    });
    Console.WriteLine($"map {map.Id} {map.Name} ({map.Directory}): {scanned} tile(s)");
    if (scanned > 0) mapSummary.Add((map.Id, map.Name, scanned));
}

// ── styles ──────────────────────────────────────────────────────────────────────────────────────────────────────────
var styles = new List<Style>();
foreach (var ((texture, mapId, zone), g) in groups)
{
    bool named = Survey.NamedRoad(texture);
    double minLength = named ? 60 : 1500;          // an unnamed dirt needs real road-like runs to count as a trail
    if (g.Length < minLength || (!named && zone.Length == 0)) continue;
    var style = g.ToStyle(texture, mapId, zone, named ? "road" : "trail");
    if (style is not null) styles.Add(style);
}
// Coverage: a named road texture that is used somewhere but never formed a measurable band (plazas, short spurs) still
// gets one style, from its overall use, so every road texture of the world can be picked.
foreach (var (texture, use) in used)
{
    if (!Survey.NamedRoad(texture) || styles.Any(s => s.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase))) continue;
    if (use.Cells * Survey.Cell * Survey.Cell < 100) continue;   // listed in a tile's MTEX but never actually visible
    var best = groups.Where(kv => kv.Key.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(kv => kv.Value.Length).Select(kv => kv).FirstOrDefault();
    Style? s = best.Value is { Length: > 0 } g2 ? g2.ToStyle(texture, best.Key.Map, best.Key.Zone, "surface") : null;
    styles.Add(s ?? Style.Fallback(texture, use));
}
styles = styles.OrderBy(s => s.Map).ThenBy(s => s.Zone, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Kind).ThenByDescending(s => s.Length).ToList();
var ids = new HashSet<string>();
foreach (var st in styles)
{
    string id = Survey.Slug($"{st.Zone}-{Path.GetFileNameWithoutExtension(st.Texture)}");
    for (int n = 2; !ids.Add(id); n++) id = Survey.Slug($"{st.Zone}-{Path.GetFileNameWithoutExtension(st.Texture)}") + "-" + n;
    st.Id = id;
}

var json = new JsonObject
{
    ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    ["source"] = "tools/road-survey over GameData/Data (stock archives, patch-7 excluded)",
    ["maps"] = new JsonArray(mapSummary.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["tiles"] = m.Tiles }).ToArray()),
    ["styles"] = new JsonArray(styles.Select(st => (JsonNode)st.ToJson()).ToArray()),
};
string jsonPath = Path.Combine(root, "MSUIClient", "Data", "road-styles.json");
File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
string reportPath = Path.Combine(root, "shared_docs", "ROAD_STYLES.md");
File.WriteAllText(reportPath, Report.Markdown(styles, used, mapSummary));
Console.WriteLine($"{styles.Count} style(s): {styles.Count(x => x.Kind == "road")} road, {styles.Count(x => x.Kind == "trail")} trail, " +
                  $"{styles.Count(x => x.Kind == "surface")} surface-only");
Console.WriteLine($"wrote {jsonPath}");
Console.WriteLine($"wrote {reportPath}");
return 0;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

internal sealed class TextureUse
{
    public long Cells;
    public readonly ConcurrentDictionary<string, long> Zones = new();
}

/// <summary>Everything measured for one (texture, map, zone).</summary>
internal sealed class Group
{
    public const int Rings = 16;
    private readonly object _gate = new();
    public double Length, WidthLen, Area;           // yd, yd*yd (width weighted by length), cells
    public double InteriorSum, InteriorCells;
    public double PerimRaw, PerimSmooth;
    public double HoleCells, ClosedCells;
    public readonly double[] RingCells = new double[Rings + 1];
    public readonly double[] RoadRing = new double[Rings + 1];
    public readonly Dictionary<string, double[]> OtherRing = new(StringComparer.OrdinalIgnoreCase);
    public double BestLength;
    public Vector3 Example;
    public int Pieces;

    public void Merge(Action<Group> add) { lock (_gate) add(this); }

    public Style? ToStyle(string texture, int map, string zone, string kind)
    {
        if (Length <= 0 || InteriorCells <= 0) return null;
        float width = (float)(WidthLen / Length);
        float strength = (float)Math.Clamp(InteriorSum / InteriorCells, 0.2, 1.0);
        // Soft edge: rings are 1 cell (~1.04 yd) apart; the band's edge is the 50% point, so double the fade-out. The
        // texture's own background level (16 yd out: other patches of the same dirt nearby) is not part of the fade.
        double background = RingCells[Rings] > 0 ? RoadRing[Rings] / RingCells[Rings] : 0;
        double edgeExcess = Math.Max(0.05, 0.5 - background);
        int fade = 1;
        for (int d = 1; d <= Rings; d++) { fade = d; if (RingCells[d] > 0 && RoadRing[d] / RingCells[d] - background < 0.1 * edgeExcess) break; }
        float soft = (float)Math.Clamp(2.0 * fade * Survey.Cell, 0.5, 10.0);
        double r = PerimSmooth > 0 ? PerimRaw / PerimSmooth : 1.0;
        float ragged = (float)Math.Clamp(14.0 / (2 * Math.PI) * Math.Sqrt(Math.Max(0, r * r - 1)), 0, 3);
        float patchy = (float)Math.Clamp(ClosedCells > 0 ? HoleCells / ClosedCells * 2.5 : 0, 0, 0.8);

        double Mean(double[] ring, int d) => RingCells[d] > 0 ? ring[d] / RingCells[d] : 0;
        string? ground = OtherRing.OrderByDescending(kv => Mean(kv.Value, 14) + Mean(kv.Value, 15) + Mean(kv.Value, 16))
            .Select(kv => kv.Key).FirstOrDefault();
        Shoulder? shoulder = null;
        foreach (var (other, ring) in OtherRing)
        {
            if (string.Equals(other, ground, StringComparison.OrdinalIgnoreCase)) continue;
            double far = Mean(ring, Rings), peak = 0; int at = 0;
            for (int d = 1; d <= 6; d++) if (Mean(ring, d) - far > peak) { peak = Mean(ring, d) - far; at = d; }
            if (peak < 0.12 || (shoulder is not null && peak <= shoulder.Excess)) continue;
            int reach = at;
            for (int d = at; d <= Rings; d++) { if (Mean(ring, d) - far >= peak / 2) reach = d; else break; }
            shoulder = new Shoulder(other, (float)Math.Clamp(reach * Survey.Cell, 1, 12), (float)Math.Clamp(soft, 1, 6),
                (float)Math.Clamp(Mean(ring, at) * 1.4, 0.3, 1.0), peak);
        }
        return new Style
        {
            Texture = texture, Map = map, Zone = zone, Kind = kind,
            Width = Survey.Round(Math.Clamp(width, 1.5f, 30f)), SoftEdge = Survey.Round(soft), Strength = Survey.Round(strength),
            Ragged = Survey.Round(ragged), Patchy = Survey.Round(patchy), Shoulder = shoulder, Ground = ground,
            Length = Math.Round(Length), Pieces = Pieces, Example = Example,
        };
    }
}

internal sealed record Shoulder(string Texture, float Extra, float SoftEdge, float Strength, double Excess);

internal sealed class Style
{
    public string Id = "", Texture = "", Zone = "", Kind = "";
    public string? Ground;
    public int Map, Pieces;
    public float Width, SoftEdge, Strength, Ragged, Patchy;
    public Shoulder? Shoulder;
    public double Length;
    public Vector3 Example;

    public static Style Fallback(string texture, TextureUse use) => new()
    {
        Texture = texture, Kind = "surface",
        Zone = use.Zones.Where(z => z.Key.Length > 0).OrderByDescending(z => z.Value).Select(z => z.Key).FirstOrDefault()
               ?? Path.GetFileName(Path.GetDirectoryName(texture)) ?? "",
        Width = 6, SoftEdge = 2, Strength = 1, Ragged = 1, Patchy = 0.1f, Map = -1,
    };

    public string Name
    {
        get
        {
            string leaf = Regex.Replace(Path.GetFileNameWithoutExtension(Texture), "(?<=[a-z0-9])(?=[A-Z])|_", " ");
            string what = Kind switch { "trail" => "trail", "surface" => "paving", _ => "road" };
            return $"{Zone}: {what} ({leaf})";
        }
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["name"] = Name, ["kind"] = Kind, ["map"] = Map, ["zone"] = Zone, ["texture"] = Texture,
            ["width"] = Width, ["softEdge"] = SoftEdge, ["strength"] = Strength, ["ragged"] = Ragged, ["patchy"] = Patchy,
        };
        if (Shoulder is { } sh)
            o["shoulder"] = new JsonObject
            {
                ["texture"] = sh.Texture, ["extra"] = Survey.Round(sh.Extra), ["softEdge"] = Survey.Round(sh.SoftEdge),
                ["strength"] = Survey.Round(sh.Strength),
            };
        if (Ground is not null) o["ground"] = Ground;
        o["measuredLength"] = Length;
        o["pieces"] = Pieces;
        if (Map >= 0) o["example"] = new JsonArray(Math.Round(Example.X, 1), Math.Round(Example.Y, 1), Math.Round(Example.Z, 1));
        return o;
    }
}

internal static class Survey
{
    public const int N = 512;                                 // cells per tile side
    private static readonly HashSet<string> DevZones = new(StringComparer.OrdinalIgnoreCase) { "Designer Island", "Programmer Isle" };
    public const float Cell = WorldBuilderLaw.Tile / N;       // ~1.04 yd

    private static readonly Regex Named = new(@"road|path|cobble|brick|street|pave|footprint|floor|mosaic|tile(?!set)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotRoad = new(@"ocean|underwater", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Ground covers that form long bands for other reasons: shores, river mud, swamp muck, blight, snow and rock edges.
    private static readonly Regex NotTrail = new(
        @"sand|shore|beach|water|lava|snow|ice|grass|moss|leaf|flower|crop|fern|plant|root|weed|web|slime|corrupt|creep|black|blue|" +
        @"green|red\b|yellow|grey|charcoal|checker|caustic|vine|brush|bush|needle|straw|thorn|barnacle|seaweed|sparkle|glow|rock|cliff|" +
        @"crack|shale|salt|glacier|ocean|underwater|sawdust|mud|muck|blight",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool NamedRoad(string texture) =>
        Named.IsMatch(Path.GetFileNameWithoutExtension(texture)) && !NotRoad.IsMatch(Path.GetFileNameWithoutExtension(texture));
    public static bool TrailCandidate(string texture) => !NotTrail.IsMatch(Path.GetFileNameWithoutExtension(texture));
    public static float Round(float v) => MathF.Round(v * 20f) / 20f;
    public static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static void Tile(int map, int col, int row, AdtTerrainReader.AdtResult adt, AreaTableCatalog areas,
        ConcurrentDictionary<(string, int, string), Group> groups, ConcurrentDictionary<string, TextureUse> used)
    {
        // Visible weight per texture, 512 x 512 (2x2 alpha pixels per cell); the zone of each chunk.
        var grids = new Dictionary<int, float[]>();
        var zoneOfChunk = new string[256];
        float[] chunkZ = new float[256];
        foreach (var chunk in adt.Chunks!)
        {
            if (chunk is null || chunk.Layers.Length == 0) continue;
            int cx = chunk.IndexX, cy = chunk.IndexY;
            string zoneName = areas.ZoneName(areas.ParentZoneId(chunk.AreaId) is var z and > 0 ? z : chunk.AreaId);
            if (DevZones.Contains(zoneName)) continue;   // the GM test islands: not part of the world anyone walks
            zoneOfChunk[cy * 16 + cx] = zoneName;
            chunkZ[cy * 16 + cx] = chunk.BaseZ;
            var alphas = chunk.Layers.Select((l, i) => i == 0 ? null : l.AlphaMap).ToList();
            var w = TerrainPaintLaw.Weights(alphas);
            for (int li = 0; li < chunk.Layers.Length; li++)
            {
                int tex = chunk.Layers[li].TextureIndex;
                if (tex < 0 || tex >= adt.Textures.Count) continue;
                if (!grids.TryGetValue(tex, out var grid)) grids[tex] = grid = new float[N * N];
                var wl = w[li];
                for (int pr = 0; pr < 64; pr += 2)
                    for (int pc = 0; pc < 64; pc += 2)
                    {
                        float v = (wl[pr * 64 + pc] + wl[pr * 64 + pc + 1] + wl[(pr + 1) * 64 + pc] + wl[(pr + 1) * 64 + pc + 1]) * 0.25f;
                        grid[(cy * 32 + pr / 2) * N + cx * 32 + pc / 2] += v;
                    }
            }
        }

        // Merge duplicate MTEX entries (same file twice) by name.
        var byName = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tex, grid) in grids)
        {
            string name = adt.Textures[tex].Replace('/', '\\');
            if (byName.TryGetValue(name, out var have)) for (int i = 0; i < have.Length; i++) have[i] += grid[i];
            else byName[name] = grid;
        }
        foreach (var (name, grid) in byName)
        {
            var use = used.GetOrAdd(name, _ => new TextureUse());
            var perChunk = new long[256];
            for (int i = 0; i < grid.Length; i++) if (grid[i] >= 0.5f) perChunk[(i / N / 32) * 16 + (i % N) / 32]++;
            long cells = 0;
            for (int ch = 0; ch < 256; ch++)
            {
                if (perChunk[ch] == 0) continue;
                cells += perChunk[ch];
                long add = perChunk[ch];
                use.Zones.AddOrUpdate(zoneOfChunk[ch] ?? "", add, (_, c) => c + add);
            }
            Interlocked.Add(ref use.Cells, cells);
        }

        foreach (var (name, grid) in byName)
        {
            bool named = NamedRoad(name);
            if (!named && !TrailCandidate(name)) continue;
            AnalyseTexture(map, col, row, name, grid, byName, zoneOfChunk, chunkZ, groups);
        }
    }

    private static string ZoneAt(string[] zoneOfChunk, int cell) => zoneOfChunk[(cell / N / 32) * 16 + (cell % N) / 32] ?? "";

    private static void AnalyseTexture(int map, int col, int row, string name, float[] grid, Dictionary<string, float[]> all,
        string[] zoneOfChunk, float[] chunkZ, ConcurrentDictionary<(string, int, string), Group> groups)
    {
        // Connected pieces of the >= 50% mask.
        var label = new int[N * N];
        var queue = new int[N * N];
        int next = 0;
        var roadPieces = new List<(int Id, double Length, double Width, int Cx, int Cy, string Zone)>();
        for (int start = 0; start < N * N; start++)
        {
            if (grid[start] < 0.5f || label[start] != 0) continue;
            int id = ++next, head = 0, tail = 0;
            queue[tail++] = start; label[start] = id;
            long area = 0, perim = 0, sx = 0, sy = 0;
            var zoneVotes = new Dictionary<string, int>();
            while (head < tail)
            {
                int p = queue[head++];
                int r = p / N, c = p % N;
                area++; sx += c; sy += r;
                if ((area & 15) == 1) { string z = ZoneAt(zoneOfChunk, p); zoneVotes[z] = zoneVotes.GetValueOrDefault(z) + 1; }
                // Perimeter: edges to a cell below 50% (tile borders do not count: a road cut by the border is not thinner).
                if (c > 0) { int q = p - 1; if (grid[q] < 0.5f) perim++; else if (label[q] == 0) { label[q] = id; queue[tail++] = q; } }
                if (c < N - 1) { int q = p + 1; if (grid[q] < 0.5f) perim++; else if (label[q] == 0) { label[q] = id; queue[tail++] = q; } }
                if (r > 0) { int q = p - N; if (grid[q] < 0.5f) perim++; else if (label[q] == 0) { label[q] = id; queue[tail++] = q; } }
                if (r < N - 1) { int q = p + N; if (grid[q] < 0.5f) perim++; else if (label[q] == 0) { label[q] = id; queue[tail++] = q; } }
            }
            if (area < 30 || perim == 0) continue;
            double width = 2.0 * area / perim * Cell, length = area * Cell * Cell / width;
            if (width < 2 || width > 16 || length < 30 || length < 5 * width) continue;
            string zone = zoneVotes.OrderByDescending(v => v.Value).First().Key;
            roadPieces.Add((id, length, width, (int)(sx / area), (int)(sy / area), zone));
        }
        if (roadPieces.Count == 0) return;

        foreach (var zoneGroup in roadPieces.GroupBy(p => p.Zone))
        {
            var ids = zoneGroup.Select(p => p.Id).ToHashSet();
            var mask = new bool[N * N];
            for (int i = 0; i < mask.Length; i++) mask[i] = label[i] != 0 && ids.Contains(label[i]);
            Measure(map, col, row, name, grid, all, mask, zoneGroup.ToList(), zoneGroup.Key, chunkZ, groups);
        }
    }

    private static void Measure(int map, int col, int row, string name, float[] grid, Dictionary<string, float[]> all, bool[] mask,
        List<(int Id, double Length, double Width, int Cx, int Cy, string Zone)> pieces, string zone, float[] chunkZ,
        ConcurrentDictionary<(string, int, string), Group> groups)
    {
        // Interior strength, edge perimeter (of the band with its 1-2 cell pinholes filled, so patchiness does not read
        // as a ragged edge), smoothed perimeter (7-cell box blur), holes inside the closed band.
        var edgeMask = Erode(Dilate(mask, 1), 1);
        double interiorSum = 0, interiorCells = 0, perimRaw = 0;
        for (int r = 1; r < N - 1; r++)
            for (int c = 1; c < N - 1; c++)
            {
                int p = r * N + c;
                if (mask[p] && mask[p - 1] && mask[p + 1] && mask[p - N] && mask[p + N]) { interiorSum += Math.Min(grid[p], 1f); interiorCells++; }
                if (edgeMask[p])
                    perimRaw += (edgeMask[p - 1] ? 0 : 1) + (edgeMask[p + 1] ? 0 : 1) + (edgeMask[p - N] ? 0 : 1) + (edgeMask[p + N] ? 0 : 1);
            }
        var blurred = Blur(edgeMask, 3);
        double perimSmooth = 0;
        for (int r = 1; r < N - 1; r++)
            for (int c = 1; c < N - 1; c++)
            {
                int p = r * N + c;
                if (blurred[p] < 0.5f) continue;
                perimSmooth += (blurred[p - 1] < 0.5f ? 1 : 0) + (blurred[p + 1] < 0.5f ? 1 : 0) + (blurred[p - N] < 0.5f ? 1 : 0) + (blurred[p + N] < 0.5f ? 1 : 0);
            }
        var closed = Erode(Dilate(mask, 3), 3);
        double hole = 0, closedCells = 0;
        for (int i = 0; i < closed.Length; i++) if (closed[i]) { closedCells++; if (grid[i] < 0.5f) hole++; }

        // Rings outside the band: chamfer distance (in cells) up to the ring limit.
        var dist = Distance(mask, Group.Rings);
        var ringCells = new double[Group.Rings + 1];
        var roadRing = new double[Group.Rings + 1];
        var other = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in all.Keys) if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) other[key] = new double[Group.Rings + 1];
        for (int i = 0; i < dist.Length; i++)
        {
            int d = dist[i];
            if (d < 1 || d > Group.Rings) continue;
            ringCells[d]++;
            roadRing[d] += grid[i];
            foreach (var (key, ring) in other) ring[d] += all[key][i];
        }

        var longest = pieces.OrderByDescending(p => p.Length).First();
        var ex = TerrainPaintLaw.PixelWorld(col, row, longest.Cx / 32, longest.Cy / 32, (longest.Cy % 32) * 2, (longest.Cx % 32) * 2);
        float exZ = chunkZ[(longest.Cy / 32) * 16 + longest.Cx / 32];
        double length = pieces.Sum(p => p.Length), widthLen = pieces.Sum(p => p.Width * p.Length);

        var g = groups.GetOrAdd((name, map, zone), _ => new Group());
        g.Merge(x =>
        {
            x.Length += length; x.WidthLen += widthLen; x.Pieces += pieces.Count;
            x.InteriorSum += interiorSum; x.InteriorCells += interiorCells;
            x.PerimRaw += perimRaw; x.PerimSmooth += perimSmooth;
            x.HoleCells += hole; x.ClosedCells += closedCells;
            for (int d = 0; d <= Group.Rings; d++) { x.RingCells[d] += ringCells[d]; x.RoadRing[d] += roadRing[d]; }
            foreach (var (key, ring) in other)
            {
                if (!x.OtherRing.TryGetValue(key, out var acc)) x.OtherRing[key] = acc = new double[Group.Rings + 1];
                for (int d = 0; d <= Group.Rings; d++) acc[d] += ring[d];
            }
            if (longest.Length > x.BestLength) { x.BestLength = longest.Length; x.Example = new Vector3(ex.X, ex.Y, exZ); }
        });
    }

    private static float[] Blur(bool[] mask, int radius)
    {
        var tmp = new float[N * N];
        var outp = new float[N * N];
        float k = 1f / (2 * radius + 1);
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                float s = 0;
                for (int d = -radius; d <= radius; d++) { int cc = Math.Clamp(c + d, 0, N - 1); if (mask[r * N + cc]) s += 1; }
                tmp[r * N + c] = s * k;
            }
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                float s = 0;
                for (int d = -radius; d <= radius; d++) s += tmp[Math.Clamp(r + d, 0, N - 1) * N + c];
                outp[r * N + c] = s * k;
            }
        return outp;
    }

    private static bool[] Dilate(bool[] m, int radius) => Morph(m, radius, true);
    private static bool[] Erode(bool[] m, int radius) => Morph(m, radius, false);

    private static bool[] Morph(bool[] m, int radius, bool dilate)
    {
        var tmp = new bool[N * N];
        var outp = new bool[N * N];
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                bool v = !dilate;
                for (int d = -radius; d <= radius && v != dilate; d++) { int cc = Math.Clamp(c + d, 0, N - 1); if (m[r * N + cc] == dilate) v = dilate; }
                tmp[r * N + c] = v;
            }
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                bool v = !dilate;
                for (int d = -radius; d <= radius && v != dilate; d++) if (tmp[Math.Clamp(r + d, 0, N - 1) * N + c] == dilate) v = dilate;
                outp[r * N + c] = v;
            }
        return outp;
    }

    /// <summary>Chamfer (1, 1.4) distance from the mask, in whole cells, capped at <paramref name="cap"/> + 1.</summary>
    private static int[] Distance(bool[] mask, int cap)
    {
        var d = new float[N * N];
        float inf = cap + 2;
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] ? 0 : inf;
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                int p = r * N + c;
                if (d[p] == 0) continue;
                float v = d[p];
                if (c > 0) v = Math.Min(v, d[p - 1] + 1);
                if (r > 0) { v = Math.Min(v, d[p - N] + 1); if (c > 0) v = Math.Min(v, d[p - N - 1] + 1.4f); if (c < N - 1) v = Math.Min(v, d[p - N + 1] + 1.4f); }
                d[p] = v;
            }
        for (int r = N - 1; r >= 0; r--)
            for (int c = N - 1; c >= 0; c--)
            {
                int p = r * N + c;
                if (d[p] == 0) continue;
                float v = d[p];
                if (c < N - 1) v = Math.Min(v, d[p + 1] + 1);
                if (r < N - 1) { v = Math.Min(v, d[p + N] + 1); if (c < N - 1) v = Math.Min(v, d[p + N + 1] + 1.4f); if (c > 0) v = Math.Min(v, d[p + N - 1] + 1.4f); }
                d[p] = v;
            }
        var outp = new int[N * N];
        for (int i = 0; i < d.Length; i++) outp[i] = (int)MathF.Round(d[i]);
        return outp;
    }
}

internal static class Report
{
    public static string Markdown(List<Style> styles, ConcurrentDictionary<string, TextureUse> used, List<(int Id, string Name, int Tiles)> maps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Road styles (generated)");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.UtcNow:yyyy-MM-dd} by `dotnet run -c Release --project tools/road-survey` from the stock archives in");
        sb.AppendLine("`GameData/Data` (World Content Packs in `patch-7.MPQ` excluded). Do not edit by hand: rerun the tool. The same run");
        sb.AppendLine("writes `MSUIClient/Data/road-styles.json`, the list the World Builder's Paths page offers under **Road style**.");
        sb.AppendLine("How each number is measured is at the top of `tools/road-survey/Program.cs`; the paint rule that uses them is");
        sb.AppendLine("`TerrainPaintLaw.RoadAmountAt` (client) and its verbatim copy `TerrainPaint.RoadAmountAt` (web app builder).");
        sb.AppendLine();
        sb.AppendLine("- **road**: a texture named as a road/path/cobble/brick/paving, measured where it forms long bands in that zone.");
        sb.AppendLine("- **trail**: a plain dirt-type texture that forms long road-like bands in that zone (an unnamed dirt road).");
        sb.AppendLine("- **surface**: a named paving texture used in the world but never as a measurable band (plazas, short spurs);");
        sb.AppendLine("  it still gets a style so every road texture of the world can be picked.");
        sb.AppendLine();
        sb.AppendLine("Maps surveyed: " + string.Join(", ", maps.Select(m => $"{m.Name} ({m.Tiles} tiles)")) + ".");
        sb.AppendLine();
        sb.AppendLine($"**{styles.Count} styles** - {styles.Count(s => s.Kind == "road")} road, {styles.Count(s => s.Kind == "trail")} trail, " +
                      $"{styles.Count(s => s.Kind == "surface")} surface.");
        sb.AppendLine();
        sb.AppendLine("| Zone | Kind | Texture | Width | Soft | Strength | Ragged | Patchy | Shoulder (+yd) | Measured |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in styles)
            sb.AppendLine($"| {s.Zone} | {s.Kind} | `{s.Texture}` | {s.Width:0.#} | {s.SoftEdge:0.#} | {s.Strength:0.##} | {s.Ragged:0.#} | {s.Patchy:0.##} | " +
                          (s.Shoulder is { } sh ? $"`{Path.GetFileNameWithoutExtension(sh.Texture)}` +{sh.Extra:0.#}" : "-") +
                          $" | {(s.Length > 0 ? $"{s.Length:0} yd" : "-")} |");
        sb.AppendLine();
        sb.AppendLine("## Coverage: every road-named ground texture in use");
        sb.AppendLine();
        sb.AppendLine("Textures whose area is under 100 sq yd are listed in a tile's MTEX but barely visible; they get no style.");
        sb.AppendLine();
        sb.AppendLine("| Texture | Area (sq yd) | Zones | Styles |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (texture, use) in used.Where(u => Survey.NamedRoad(u.Key)).OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
        {
            int n = styles.Count(s => s.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase));
            string zones = string.Join(", ", use.Zones.Where(z => z.Key.Length > 0).OrderByDescending(z => z.Value).Take(4).Select(z => z.Key));
            sb.AppendLine($"| `{texture}` | {use.Cells * Survey.Cell * Survey.Cell:0} | {zones} | {n} |");
        }
        return sb.ToString();
    }
}

internal static class Pins
{
    /// <summary>Fixed inputs whose outputs both copies of the paint rule must reproduce exactly.</summary>
    public static readonly (float X, float Y, float D, TerrainPaintLaw.RoadLook Look)[] Cases =
    [
        (-8914.25f, -135.5f, 2.5f, new(6f, 2f, 1f, 1.5f, 0.3f, 12345)),
        (-8914.25f, -135.5f, 3.9f, new(6f, 2f, 1f, 1.5f, 0.3f, 12345)),
        (1500.75f, -4400.125f, 4.4f, new(5f, 1.5f, 0.85f, 2.5f, 0.6f, 987654321)),
        (-100f, 100f, 1f, new(8f, 3f, 0.9f, 0f, 0.5f, 7)),
        (0.5f, 0.5f, 0f, new(4f, 1f, 1f, 3f, 0f, 2147483647)),
    ];

    public static void Print()
    {
        foreach (var (x, y, d, look) in Cases)
            Console.WriteLine($"RoadAmountAt(({x}f, {y}f), {d}f, new({look.Width}f, {look.SoftEdge}f, {look.Strength}f, {look.Ragged}f, {look.Patchy}f, {look.Seed})) " +
                              $"= {TerrainPaintLaw.RoadAmountAt(new Vector2(x, y), d, look):R}");
        Console.WriteLine($"SeedFor(\"Road QA\") = {TerrainPaintLaw.SeedFor("Road QA")}");
        Console.WriteLine($"Fbm(12.5, -3.25, 99) = {TerrainPaintLaw.Fbm(12.5f, -3.25f, 99):R}");
    }
}
