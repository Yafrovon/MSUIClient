namespace MSUIClient.Formats;

/// <summary>
/// The raw rows the Spell Creator's RULES part reads (shared_docs/SPELL_CREATOR_PLAN.md P1.1,
/// P1.3): every Spell.dbc record by id, as the 173 u32 fields the file holds, and the four index
/// tables a spell's numbers point into - SpellCastTimes, SpellDuration, SpellRange, SpellRadius -
/// with every field, so a picker can show "2.5 s", "30 yd", "8 yd radius" instead of a row id.
///
/// Loaded on demand (the workshop's Rules section), separate from SpellCatalog: SpellCatalog is
/// the gameplay lookup and keeps only what play needs; this keeps everything, which is what an
/// author editing ANY field needs. Re-load after a patch hot-swap.
/// </summary>
public sealed class SpellRuleTables
{
    public readonly record struct CastTime(uint Id, int BaseMs, int PerLevelMs, int MinMs);
    public readonly record struct Duration(uint Id, int BaseMs, int PerLevelMs, int MaxMs);
    public readonly record struct Range(uint Id, float Min, float Max, uint Flags, string Name);
    public readonly record struct Radius(uint Id, float Yards, float PerLevel, float Max);

    private readonly Dictionary<uint, uint[]> _spells = new();
    public IReadOnlyDictionary<uint, CastTime> CastTimes { get; private set; } = new Dictionary<uint, CastTime>();
    public IReadOnlyDictionary<uint, Duration> Durations { get; private set; } = new Dictionary<uint, Duration>();
    public IReadOnlyDictionary<uint, Range> Ranges { get; private set; } = new Dictionary<uint, Range>();
    public IReadOnlyDictionary<uint, Radius> Radii { get; private set; } = new Dictionary<uint, Radius>();

    public int SpellCount => _spells.Count;

    public bool TryGetSpellRow(uint id, out uint[] row) => _spells.TryGetValue(id, out row!);

    public IEnumerable<uint> SpellIds => _spells.Keys;

    public static SpellRuleTables? Load(MpqMount mpq)
    {
        if (Parse(mpq, @"DBFilesClient\Spell.dbc") is not { FieldCount: >= 173 } spell) return null;
        var t = new SpellRuleTables();
        for (int r = 0; r < spell.RecordCount; r++)
        {
            var row = new uint[spell.FieldCount];
            for (int f = 0; f < row.Length; f++) row[f] = spell.GetUInt(r, f);
            t._spells[row[0]] = row;
        }

        var cast = new Dictionary<uint, CastTime>();
        if (Parse(mpq, @"DBFilesClient\SpellCastTimes.dbc") is { FieldCount: >= 4 } ct)
            for (int r = 0; r < ct.RecordCount; r++)
                cast[ct.GetUInt(r, 0)] = new CastTime(ct.GetUInt(r, 0), ct.GetInt(r, 1), ct.GetInt(r, 2), ct.GetInt(r, 3));
        t.CastTimes = cast;

        var dur = new Dictionary<uint, Duration>();
        if (Parse(mpq, @"DBFilesClient\SpellDuration.dbc") is { FieldCount: >= 4 } du)
            for (int r = 0; r < du.RecordCount; r++)
                dur[du.GetUInt(r, 0)] = new Duration(du.GetUInt(r, 0), du.GetInt(r, 1), du.GetInt(r, 2), du.GetInt(r, 3));
        t.Durations = dur;

        // SpellRange 1.12: id, min, max, flags, name (8 locales + flags), short name.
        var ranges = new Dictionary<uint, Range>();
        if (Parse(mpq, @"DBFilesClient\SpellRange.dbc") is { FieldCount: >= 5 } ra)
            for (int r = 0; r < ra.RecordCount; r++)
                ranges[ra.GetUInt(r, 0)] = new Range(ra.GetUInt(r, 0), ra.GetFloat(r, 1), ra.GetFloat(r, 2),
                    ra.GetUInt(r, 3), ra.GetString(r, 4));
        t.Ranges = ranges;

        var radii = new Dictionary<uint, Radius>();
        if (Parse(mpq, @"DBFilesClient\SpellRadius.dbc") is { FieldCount: >= 4 } rd)
            for (int r = 0; r < rd.RecordCount; r++)
                radii[rd.GetUInt(r, 0)] = new Radius(rd.GetUInt(r, 0), rd.GetFloat(r, 1), rd.GetFloat(r, 2), rd.GetFloat(r, 3));
        t.Radii = radii;
        return t;
    }

    private static DbcFile? Parse(MpqMount mpq, string path) =>
        mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;

    // ── plain words for an index ───────────────────────────────────────────

    public string CastTimeText(uint id) => id == 0 ? "none" : CastTimes.TryGetValue(id, out var c)
        ? (c.BaseMs <= 0 ? "instant" : $"{c.BaseMs / 1000f:0.##} s") : $"row {id}?";

    public string DurationText(uint id) => id == 0 ? "none" : Durations.TryGetValue(id, out var d)
        ? (d.BaseMs < 0 ? "until cancelled" : $"{d.BaseMs / 1000f:0.##} s") : $"row {id}?";

    public string RangeText(uint id) => Ranges.TryGetValue(id, out var r)
        ? (r.Max <= 0f ? "self" : r.Min > 0f ? $"{r.Min:0.#}-{r.Max:0.#} yd" : $"{r.Max:0.#} yd") + (r.Name.Length > 0 ? $" ({r.Name})" : "")
        : $"row {id}?";

    public string RadiusText(uint id) => id == 0 ? "none" : Radii.TryGetValue(id, out var r)
        ? $"{r.Yards:0.#} yd" : $"row {id}?";

    // ── new rows (decision D5) ─────────────────────────────────────────────

    /// <summary>A row the author ASKED for that no table has carries a local id from here until the
    /// Completer gives it a real one (an equal row, or a new one at 1000+: MangosSuperUI
    /// SpellIndexRows). The two floors must agree.</summary>
    public const uint LocalRowFloor = 900_000;

    public static bool IsLocalRow(uint id) => id >= LocalRowFloor;

    /// <summary>Put a local row in its table, so every reader (pickers, rings, the legality law)
    /// sees it like any other: one value - ms for SpellCastTimes/SpellDuration, yards for
    /// SpellRange/SpellRadius.</summary>
    public void AddLocalRow(string table, uint id, double value)
    {
        int ms = (int)Math.Round(value);
        switch (table)
        {
            case "SpellCastTimes": ((Dictionary<uint, CastTime>)CastTimes)[id] = new CastTime(id, ms, 0, ms); break;
            case "SpellDuration": ((Dictionary<uint, Duration>)Durations)[id] = new Duration(id, ms, 0, ms); break;
            case "SpellRange": ((Dictionary<uint, Range>)Ranges)[id] = new Range(id, 0f, (float)value, 0, ""); break;
            case "SpellRadius": ((Dictionary<uint, Radius>)Radii)[id] = new Radius(id, (float)value, 0f, (float)value); break;
        }
    }

    /// <summary>An existing row that says exactly this value (a fixed one: no per-level growth).</summary>
    public uint ExactRow(string table, double value) => table switch
    {
        "SpellCastTimes" => CastTimes.Values.Where(c => c.BaseMs == (int)Math.Round(value) && c.PerLevelMs == 0).Select(c => c.Id).DefaultIfEmpty(0u).Min(),
        "SpellDuration" => Durations.Values.Where(d => d.BaseMs == (int)Math.Round(value) && d.PerLevelMs == 0).Select(d => d.Id).DefaultIfEmpty(0u).Min(),
        "SpellRange" => Ranges.Values.Where(r => r.Min == 0f && MathF.Abs(r.Max - (float)value) < 0.01f).Select(r => r.Id).DefaultIfEmpty(0u).Min(),
        "SpellRadius" => Radii.Values.Where(r => MathF.Abs(r.Yards - (float)value) < 0.01f && r.PerLevel == 0f).Select(r => r.Id).DefaultIfEmpty(0u).Min(),
        _ => 0u,
    };

    /// <summary>The existing row nearest a wanted value (decision D5: an existing row first; the
    /// Completer appends a new one only when the nearest is not close enough).</summary>
    public static uint Nearest<T>(IReadOnlyDictionary<uint, T> table, Func<T, float> value, float wanted) =>
        table.Count == 0 ? 0 : table.OrderBy(kv => MathF.Abs(value(kv.Value) - wanted)).ThenBy(kv => kv.Key).First().Key;
}
