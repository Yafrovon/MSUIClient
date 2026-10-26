using System.Text.Json.Nodes;

namespace MSUIClient.Creator.Spells;

// ═══════════════════════════════════════════════════════════════════════════
// SPELL RULES — what a spell DOES, as the author edits it (shared_docs/SPELL_CREATOR_PLAN.md
// P1.1). One value per spell_template column (SpellTemplateColumns), held next to the BASELINE
// it started from: the source spell's own row for a derived spell, the from-zero template
// (18282 "Dummy Spell") for a new one. The session's `rules` block is exactly the columns that
// differ from the baseline - spell_template overrides the Completer writes as they are - so a
// derived spell never re-writes the 1% of fields vmangos corrected, and a new spell carries
// every decision its author made.
//
// Pure: no GameLoop, no renderer. The workshop reads and writes it through Get/Set and the
// typed helpers below; the legality law (SpellLegality) reads the same values.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class SpellRules
{
    /// <summary>The template a spell built from nothing starts from (MangosSuperUI
    /// SpellCreatorService.FromZeroTemplateSpell - the two must agree).</summary>
    public const uint FromZeroTemplateSpell = 18282;

    /// <summary>Custom flag: missile effects land at the destination on arrival (the Core's
    /// SPELL_CUSTOM_DEFERRED_DEST, decision D3). Lives in spell_template.customFlags, which has no
    /// Spell.dbc field - the server reads it, the client never needs to.</summary>
    public const long DeferredDestination = 0x4000;

    /// <summary>Server-only columns the rules may set that Spell.dbc does not carry.</summary>
    public static readonly string[] ServerOnlyColumns = { "customFlags", "script_name" };

    public uint BaselineSpell { get; private set; }
    private readonly Dictionary<string, double> _baseline;
    private readonly Dictionary<string, double> _values;
    private readonly Dictionary<string, double> _serverOnly = new(StringComparer.OrdinalIgnoreCase);

    private SpellRules(uint baselineSpell, Dictionary<string, double> baseline)
    {
        BaselineSpell = baselineSpell;
        _baseline = baseline;
        _values = new Dictionary<string, double>(baseline, StringComparer.OrdinalIgnoreCase);
    }

    public static SpellRules FromDbcRow(uint baselineSpell, IReadOnlyList<uint> row) =>
        new(baselineSpell, SpellTemplateColumns.FromDbcRow(row));

    public double Get(string column) =>
        _serverOnly.TryGetValue(column, out double server) ? server
        : _values.TryGetValue(column, out double v) ? v : 0;

    public int GetInt(string column) => (int)Get(column);
    public float GetFloat(string column) => (float)Get(column);

    public void Set(string column, double value)
    {
        if (Array.Exists(ServerOnlyColumns, c => c.Equals(column, StringComparison.OrdinalIgnoreCase)))
            _serverOnly[column] = value;
        else _values[column] = value;
    }

    public bool Changed(string column) =>
        _serverOnly.ContainsKey(column) ||
        (_values.TryGetValue(column, out double v) && (!_baseline.TryGetValue(column, out double b) || b != v));

    public IEnumerable<string> ChangedColumns =>
        _values.Keys.Where(Changed).Concat(_serverOnly.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Back to the baseline value for one column.</summary>
    public void Reset(string column)
    {
        _serverOnly.Remove(column);
        if (_baseline.TryGetValue(column, out double b)) _values[column] = b;
    }

    /// <summary>Copy a set of columns from another spell's row - "like Flare" for a card.</summary>
    public void CopyFrom(IReadOnlyList<uint> row, IEnumerable<string> columns)
    {
        Dictionary<string, double> other = SpellTemplateColumns.FromDbcRow(row);
        foreach (string column in columns)
            if (other.TryGetValue(column, out double v)) _values[column] = v;
    }

    // ── new index rows (decision D5) ───────────────────────────────────────

    /// <summary>A row the author asked for that no table has: the table, its local id
    /// (SpellRuleTables.LocalRowFloor+) and its one value (ms or yards).</summary>
    public readonly record struct NewIndexRow(string Table, uint LocalId, double Value);

    private readonly List<NewIndexRow> _newRows = new();

    /// <summary>The table a spell_template column points into, or null (MangosSuperUI
    /// SpellIndexRows.TableOfColumn - the two must agree).</summary>
    public static string? IndexTableOf(string column) =>
        column.Equals("castingTimeIndex", StringComparison.OrdinalIgnoreCase) ? "SpellCastTimes"
        : column.Equals("durationIndex", StringComparison.OrdinalIgnoreCase) ? "SpellDuration"
        : column.Equals("rangeIndex", StringComparison.OrdinalIgnoreCase) ? "SpellRange"
        : column.StartsWith("effectRadiusIndex", StringComparison.OrdinalIgnoreCase) ? "SpellRadius"
        : null;

    /// <summary>The new rows some index column still names (an abandoned one is not exported).</summary>
    public IReadOnlyList<NewIndexRow> NewIndexRows => _newRows.Where(row =>
        _values.Any(kv => IndexTableOf(kv.Key) == row.Table && (uint)kv.Value == row.LocalId)).ToList();

    /// <summary>A local row for a value no table has (reused when this spell already asked for it).</summary>
    public uint AddIndexRow(string table, double value)
    {
        foreach (NewIndexRow row in _newRows)
            if (row.Table == table && Math.Abs(row.Value - value) < 0.001) return row.LocalId;
        uint id = Formats.SpellRuleTables.LocalRowFloor + 1 + (uint)_newRows.Count;
        _newRows.Add(new NewIndexRow(table, id, value));
        return id;
    }

    // ── the session block ──────────────────────────────────────────────────

    /// <summary>{ "baselineSpell": n, "columns": { column: value } } - changed columns only.</summary>
    public JsonObject ToJson()
    {
        var columns = new JsonObject();
        foreach (string column in ChangedColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            double v = Get(column);
            // Typed primitives only: JsonValue.Create(object) makes a value that a plain
            // JsonSerializerOptions (the session file's) cannot write - it threw on the first save.
            columns[column] = SpellTemplateColumns.TryGet(column, out var meta) && meta.Kind == SpellTemplateColumns.Kind.Float
                ? JsonValue.Create((float)v)
                : JsonValue.Create((long)v);
        }
        var block = new JsonObject { ["baselineSpell"] = BaselineSpell, ["columns"] = columns };
        if (NewIndexRows is { Count: > 0 } rows)
            block["newIndexRows"] = new JsonArray(rows.Select(row => (JsonNode)new JsonObject
            {
                ["table"] = row.Table, ["localId"] = row.LocalId, ["value"] = row.Value,
            }).ToArray());
        return block;
    }

    /// <summary>Apply a saved block's columns over this baseline (reopen).</summary>
    public void ApplyJson(JsonNode? node)
    {
        if (node?["newIndexRows"] is JsonArray rows)
            foreach (JsonNode? row in rows)
                if (row?["table"]?.GetValue<string>() is { Length: > 0 } table && row["localId"] is JsonValue id &&
                    row["value"] is JsonValue value && Number(id) is { } localId && Number(value) is { } v &&
                    !_newRows.Exists(r => r.LocalId == (uint)localId))
                    _newRows.Add(new NewIndexRow(table, (uint)localId, v));
        if (node?["columns"] is not JsonObject columns) return;
        foreach ((string column, JsonNode? value) in columns)
            if (value is JsonValue jv && Number(jv) is { } d) Set(column, d);
    }

    /// <summary>A JSON number of any CLR shape: a parsed file hands back JsonElement-backed
    /// values, a block still in memory holds JsonValue&lt;long&gt;/&lt;float&gt; - and
    /// TryGetValue&lt;double&gt; is false on those (an in-process reopen restored no rules).</summary>
    private static double? Number(JsonValue value)
    {
        if (value.TryGetValue(out double d)) return d;
        if (value.TryGetValue(out long l)) return l;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out float f)) return f;
        if (value.TryGetValue(out uint u)) return u;
        return null;
    }

    // ── typed views the cards use ──────────────────────────────────────────

    public const uint TargetFlagDestLocation = 0x40;
    public const uint TargetFlagUnit = 0x2;

    public bool GroundTargeted => ((uint)GetInt("targets") & TargetFlagDestLocation) != 0;
    public float Speed => GetFloat("speed");
    public bool Deferred => ((long)Get("customFlags") & DeferredDestination) != 0;

    public void SetDeferred(bool on)
    {
        long flags = (long)Get("customFlags");
        Set("customFlags", on ? flags | DeferredDestination : flags & ~DeferredDestination);
    }

    public static string Column(string stem, int effect) => $"{stem}{effect}";

    public int EffectType(int effect) => GetInt(Column("effect", effect));
    public int EffectAura(int effect) => GetInt(Column("effectApplyAuraName", effect));

    /// <summary>The roll the server makes for an effect, as min..max (vmangos SpellCaster.cpp:
    /// basePoints + irand(baseDice, dieSides), or + baseDice when dieSides ≤ 1).</summary>
    public (int Min, int Max) Points(int effect)
    {
        int bp = GetInt(Column("effectBasePoints", effect));
        int dice = GetInt(Column("effectBaseDice", effect));
        int sides = GetInt(Column("effectDieSides", effect));
        return sides <= 1 ? (bp + dice, bp + dice) : (bp + Math.Min(dice, sides), bp + Math.Max(dice, sides));
    }

    /// <summary>Write a min..max roll (base dice 1, the stock convention - the from-zero template
    /// has 0, and a cast of 99 + 1 side rolled 99 instead of 100 until this wrote the dice too).</summary>
    public void SetPoints(int effect, int min, int max)
    {
        if (max < min) (min, max) = (max, min);
        Set(Column("effectBasePoints", effect), min - 1);
        Set(Column("effectBaseDice", effect), 1);
        Set(Column("effectDieSides", effect), max > min ? max - min + 1 : 1);
    }
}
