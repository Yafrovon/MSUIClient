using System.Globalization;
using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// The half of a portal the client does not have: where a trigger sends you.
///
/// WHY THIS FILE EXISTS AT ALL
///   `AreaTrigger.dbc` ships 432 trigger VOLUMES and says nothing about
///   destinations - not the target map, not the position, not even which
///   direction a trigger points. Vanilla's client never needed to know: you
///   walk into the volume, you tell the server, the server teleports you.
///
///   Ruled out before reaching for the server, so nobody re-checks them:
///   `AreaPOI.dbc` is landmarks ("Echo Ridge Mine", "Sentinel Hill"), and
///   pfQuest's `areatrigger.lua` carries only map-pin percentages.
///
///   PLAN_13 H3 said this table was "which we do not have" and used a derived
///   spawn instead. That was wrong: this project talks to a VMaNGOS server, so
///   the table was always available - it just was not in the repo. It is now,
///   dumped straight out of the world database.
///
/// WHEN P2 LANDS THIS BECOMES A DEBUG AFFORDANCE. A real server sends the
/// teleport itself. Keep this file; do not build anything on top of it that
/// the server will have to fight.
///
/// FORMAT — tab-separated, exactly as `mysql -B` emits it, header row first:
///   id  patch  name  message  required_level  required_condition
///   target_map  target_position_x  target_position_y  target_position_z
///   target_orientation
///
/// THE KEY IS (id, patch), NOT id. VMaNGOS gates content by patch, and six of
/// Dire Maul's entrances appear twice - patch 0 with "You Shall Not Pass!" at
/// level 61, patch 1 with the real level-45 requirement. 1.12.1 is the last
/// vanilla patch, so the highest patch row for an id is the one that applies.
/// Taking the first row instead would lock Dire Maul at level 61 forever, which
/// is the kind of bug that looks like content rather than parsing.
/// </summary>
public sealed class AreaTriggerTeleport
{
    public int Id { get; init; }
    public int Patch { get; init; }

    /// <summary>VMaNGOS's own label, e.g. "Deadmines - Entrance". Never parsed for meaning.</summary>
    public string Name { get; init; } = "";

    public string Message { get; init; } = "";
    public int RequiredLevel { get; init; }
    public int TargetMap { get; init; }
    public Vector3 TargetPosition { get; init; }

    /// <summary>Facing on arrival, radians, WoW convention: 0 = +X, pi/2 = +Y.</summary>
    public float TargetOrientation { get; init; }

    public override string ToString()
        => $"{Id} '{Name}' -> map {TargetMap} " +
           $"({TargetPosition.X:F1},{TargetPosition.Y:F1},{TargetPosition.Z:F1}) o{TargetOrientation:F2}";
}

public sealed class AreaTriggerTeleportTable
{
    public const string FileName = "areatrigger_teleport.tsv";

    private readonly Dictionary<int, AreaTriggerTeleport> _byId = [];
    public IReadOnlyDictionary<int, AreaTriggerTeleport> ById => _byId;
    public int Count => _byId.Count;

    public AreaTriggerTeleport? Get(int triggerId)
        => _byId.TryGetValue(triggerId, out var t) ? t : null;

    /// <summary>
    /// Read the table from data/reference under the repo root. Missing file is not an error - the
    /// client works without it, portals simply do not exist. Same contract as
    /// VantageStore and VisibilityOverrides: never throw on read.
    /// </summary>
    public static AreaTriggerTeleportTable Load(string repoRoot)
    {
        var table = new AreaTriggerTeleportTable();
        string path = Path.Combine(repoRoot, "data", "reference", FileName);

        if (!File.Exists(path))
        {
            Console.WriteLine($"[teleport] {path} not found - portals disabled. " +
                              "Dump it with: mysql -B -e 'SELECT * FROM areatrigger_teleport' <worlddb>");
            return table;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                Console.WriteLine($"[teleport] {path} has no rows");
                return table;
            }
            table.AddRows(lines, FileName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[teleport] could not read {path} - portals disabled ({ex.Message})");
        }

        return table;
    }

    /// <summary>MPQ path of the World Content Packs' portal table (MangosSuperUI
    /// WorldPackBuildService.TeleportsMpqPath): same TSV format, written into patch-7.MPQ.</summary>
    public const string PackMpqPath = @"WorldPacks\areatrigger_teleport.tsv";

    /// <summary>
    /// Merge the pack portals shipped inside patch-7.MPQ over the stock reference table: without them a
    /// pack portal is only a trigger VOLUME (AreaTrigger.dbc) - no name, no destination for the loading
    /// screen or the destination prewarm. Pack ids (7000+) never collide with stock ones. Never throws.
    /// </summary>
    public void MergePack(byte[]? tsv)
    {
        if (tsv is null) return;
        try
        {
            string[] lines = System.Text.Encoding.UTF8.GetString(tsv).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r')).ToArray();
            if (lines.Length >= 2) AddRows(lines, "patch-7 (World Content Packs)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[teleport] could not merge the pack portal table ({ex.Message})");
        }
    }

    private void AddRows(string[] lines, string source)
    {
        // Read the header rather than assuming column order. The schema
        // drifts between MaNGOS forks and this is one line of defence for
        // the price of one dictionary.
        var header = lines[0].Split('\t');
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) col[header[i].Trim()] = i;

        int Need(string name)
            => col.TryGetValue(name, out int i) ? i : -1;

        int cId = Need("id"), cPatch = Need("patch"), cName = Need("name"),
            cMsg = Need("message"), cLevel = Need("required_level"),
            cMap = Need("target_map"), cX = Need("target_position_x"),
            cY = Need("target_position_y"), cZ = Need("target_position_z"),
            cO = Need("target_orientation");

        if (cId < 0 || cMap < 0 || cX < 0 || cY < 0 || cZ < 0)
        {
            Console.WriteLine($"[teleport] {source} header is missing required columns " +
                              $"(saw: {string.Join(", ", header)}). NOT LOADED.");
            return;
        }

        int superseded = 0, added = 0;
        var patchOf = new Dictionary<int, int>();

        // Bound on every column this loop indexes, not just two of them.
        // The header is read precisely because the schema drifts, so the
        // guard must not quietly assume target_orientation is last: one
        // short row would throw, and the catch would discard the whole
        // table as "portals disabled".
        int maxCol = 0;
        foreach (int c in new[] { cId, cPatch, cName, cMsg, cLevel, cMap, cX, cY, cZ, cO })
            if (c > maxCol) maxCol = c;

        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length <= maxCol) continue;
            if (!int.TryParse(f[cId], out int id)) continue;

            // D8: mysql -B prints a NULL as the literal text "NULL", and an
            // empty field fails to parse too. Defaulting either to 0 would
            // leave a corrupt row looking like a perfectly good portal to
            // Eastern Kingdoms. Drop it instead.
            if (!int.TryParse(f[cMap], out int targetMap)) continue;

            int patch = cPatch >= 0 && int.TryParse(f[cPatch], out int p) ? p : 0;

            // Highest patch wins - see the class summary on Dire Maul.
            if (patchOf.TryGetValue(id, out int have) && have >= patch)
            {
                superseded++;
                continue;
            }
            if (patchOf.ContainsKey(id)) superseded++;

            var entry = new AreaTriggerTeleport
            {
                Id = id,
                Patch = patch,
                Name = cName >= 0 ? f[cName] : "",
                Message = cMsg >= 0 ? f[cMsg] : "",
                RequiredLevel = cLevel >= 0 && int.TryParse(f[cLevel], out int lv) ? lv : 0,
                TargetMap = targetMap,
                TargetPosition = new Vector3(F(f[cX]), F(f[cY]), F(f[cZ])),
                TargetOrientation = cO >= 0 ? F(f[cO]) : 0f,
            };

            patchOf[id] = patch;
            _byId[id] = entry;
            added++;
        }

        Console.WriteLine($"[teleport] {added} destination(s) from {source}" +
                          (superseded > 0 ? $" ({superseded} superseded by a later patch)" : ""));
    }

    /// <summary>
    /// InvariantCulture on purpose. The file holds "-14.5732" whatever the
    /// machine's locale thinks a decimal separator is, and a comma-decimal
    /// locale would silently parse it as -145732.
    /// </summary>
    private static float F(string s)
        => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : 0f;
}
