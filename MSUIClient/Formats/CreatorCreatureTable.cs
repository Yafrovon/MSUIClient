namespace MSUIClient.Formats;

// ─────────────────────────────────────────────────────────────────────────────
// The creator-mode creature catalogue: a flat dump of vmangos creature_template
// (creator-creatures.tsv at the repo root, generated from MangosSuperUI's
// GET /QuestAtlas/CreatureCatalogue - one row per entry, highest patch wins,
// entries with no display id dropped). The client downloads it from the web app
// itself once per session (GameLoop.EnsureCreatorCreatures) and keeps the file
// as the offline copy; nobody makes it by hand. Missing file is not an error -
// the browser just reports the catalogue is absent until the download lands.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class CreatorCreatureTable
{
    public readonly record struct Creature(uint Entry, string Name, string SubName,
        int LevelMin, int LevelMax, byte Rank, byte Type, uint DisplayId, float Scale);

    private readonly List<Creature> _creatures;
    private Dictionary<uint, Creature>? _byEntry;
    public int Count => _creatures.Count;

    /// <summary>One creature by entry (the Quest Tabletop maps census spawns to display ids this way).</summary>
    public bool TryGet(uint entry, out Creature creature)
    {
        _byEntry ??= _creatures.GroupBy(c => c.Entry).ToDictionary(g => g.Key, g => g.First());
        return _byEntry.TryGetValue(entry, out creature);
    }

    private CreatorCreatureTable(List<Creature> creatures) => _creatures = creatures;

    /// <summary>Vanilla creature_template rank names, indexed by the rank column.</summary>
    public static string RankName(byte rank) => rank switch
    {
        1 => "Elite", 2 => "Rare Elite", 3 => "Boss", 4 => "Rare", _ => "",
    };

    public const string FileName = "creator-creatures.tsv";

    public static string PathIn(string repoRoot) => Path.Combine(repoRoot, FileName);

    public static CreatorCreatureTable? Load(string repoRoot)
    {
        string path = PathIn(repoRoot);
        if (!File.Exists(path))
        {
            Console.WriteLine($"[creator] creature catalogue missing ({path}) - waiting for the web app download");
            return null;
        }
        using var reader = new StreamReader(path);
        return Parse(reader, path);
    }

    /// <summary>Parse the TSV (header line first). Null when it does not parse - never throws.</summary>
    public static CreatorCreatureTable? Parse(TextReader reader, string source)
    {
        try
        {
            var creatures = new List<Creature>(12000);
            if (reader.ReadLine() is not { } header || !header.StartsWith("entry	", StringComparison.Ordinal))
            {
                Console.WriteLine($"[creator] creature catalogue {source}: not a catalogue (bad header)");
                return null;
            }
            while (reader.ReadLine() is { } line)
            {
                string[] f = line.Split('\t');
                if (f.Length < 9) continue;
                creatures.Add(new Creature(
                    uint.Parse(f[0]), f[1], f[2], int.Parse(f[3]), int.Parse(f[4]),
                    byte.Parse(f[5]), byte.Parse(f[6]), uint.Parse(f[7]),
                    float.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture)));
            }
            Console.WriteLine($"[creator] creature catalogue ready ({creatures.Count} rows, {source})");
            return new CreatorCreatureTable(creatures);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] creature catalogue failed to parse: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Case-insensitive substring search over name and subname; a purely numeric
    /// query is an exact entry match. Results are ordered name-prefix matches
    /// first, then alphabetically.
    /// </summary>
    public List<Creature> Search(string query, int limit = 60)
    {
        var result = new List<Creature>();
        if (query.Length == 0) return result;
        bool numeric = uint.TryParse(query, out uint entry);
        foreach (var creature in _creatures)
        {
            if (numeric)
            {
                if (creature.Entry != entry) continue;
            }
            else if (!creature.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                     !creature.SubName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(creature);
        }
        result.Sort((a, b) =>
        {
            bool ap = a.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase);
            bool bp = b.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase);
            if (ap != bp) return ap ? -1 : 1;
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Entry.CompareTo(b.Entry);
        });
        if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
        return result;
    }
}
