namespace MSUIClient.Formats;

// ─────────────────────────────────────────────────────────────────────────────
// The creator-mode item catalogue: a flat dump of vmangos item_template
// (creator-items.tsv at the repo root, generated from MangosSuperUI's
// /Items/Search endpoint). Offline creator mode has no server to ask for item
// names, so the search modal reads this instead. Missing file is not an error -
// the search modal just reports the catalogue is absent.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class CreatorItemTable
{
    public readonly record struct Item(uint Entry, string Name, byte ItemClass, byte Subclass,
        byte Quality, uint DisplayId, int InventoryType, int ReqLevel, int ItemLevel);

    private readonly List<Item> _items;
    public int Count => _items.Count;

    private CreatorItemTable(List<Item> items) => _items = items;

    public static CreatorItemTable? Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "creator-items.tsv");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[creator] item catalogue missing ({path}) - search disabled");
            return null;
        }
        try
        {
            var items = new List<Item>(40000);
            using var reader = new StreamReader(path);
            reader.ReadLine();   // header
            while (reader.ReadLine() is { } line)
            {
                string[] f = line.Split('\t');
                if (f.Length < 9) continue;
                items.Add(new Item(
                    uint.Parse(f[0]), f[1], byte.Parse(f[2]), byte.Parse(f[3]), byte.Parse(f[4]),
                    uint.Parse(f[5]), int.Parse(f[6]), int.Parse(f[7]), int.Parse(f[8])));
            }
            Console.WriteLine($"[creator] item catalogue ready ({items.Count} rows)");
            return new CreatorItemTable(items);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] item catalogue failed to parse: {ex.Message}");
            return null;
        }
    }

    /// <summary>Only inventory types the paper doll can wear (armor slots + weapons).</summary>
    public static bool IsEquippable(int inventoryType) => inventoryType
        is >= 1 and <= 10 or 13 or 14 or 15 or 16 or 17 or 19 or 20 or 21 or 22 or 23 or 25 or 26;

    /// <summary>
    /// Case-insensitive substring search; a purely numeric query is an exact
    /// entry match. invTypeFilter -1 means any equippable type, -2 any item. Results are
    /// ordered highest quality first, then item level, then name.
    /// </summary>
    public List<Item> Search(string query, int invTypeFilter, int limit = 60)
    {
        var result = new List<Item>();
        bool numeric = uint.TryParse(query, out uint entry);
        foreach (var item in _items)
        {
            // -1: any equippable type; -2: any item at all (quest objectives - fangs, letters, reagents).
            if (invTypeFilter >= 0 ? item.InventoryType != invTypeFilter
                                   : invTypeFilter == -1 && !IsEquippable(item.InventoryType)) continue;
            if (numeric)
            {
                if (item.Entry != entry) continue;
            }
            else if (query.Length > 0 &&
                     !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(item);
        }
        result.Sort((a, b) =>
        {
            int c = b.Quality.CompareTo(a.Quality);
            if (c != 0) return c;
            c = b.ItemLevel.CompareTo(a.ItemLevel);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
        return result;
    }
}
