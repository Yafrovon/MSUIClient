namespace MSUIClient.Formats;

public readonly record struct SkillLineInfo(uint Id, uint CategoryId, string Name,
    string Description, string IconPath);
public readonly record struct SkillCategoryInfo(uint Id, string Name, uint DisplayOrder);
public readonly record struct SkillRecipeInfo(uint SpellId, uint SkillLineId, uint Minimum,
    uint TrivialLow, uint TrivialHigh);

/// <summary>One SkillLineAbility.dbc row with its learn-routing columns: which
/// race/class bit-sets learn the spell, and the forward id that supersedes it at
/// the next rank. Build-5875 columns: [3]=raceMask [4]=classMask [5]=raceMaskNot
/// [6]=classMaskNot [8]=forwardSpellId [9]=acquireMethod (vmangos
/// SkillLineAbilityEntry order). Talent-granted actives and their trainer rank
/// upgrades carry classMask 0, so ALL rows are kept and the class resolver
/// decides admission.</summary>
public readonly record struct ClassAbilityRow(uint SpellId, uint SkillLineId,
    uint RaceMask, uint ClassMask, uint RaceMaskNot, uint ClassMaskNot,
    uint ForwardSpellId, uint AcquireMethod);

/// <summary>Names, categories, descriptions and icons from the client's build-5875 DBCs.</summary>
public sealed class SkillLineCatalog
{
    public const string LinesPath = @"DBFilesClient\SkillLine.dbc";
    public const string CategoriesPath = @"DBFilesClient\SkillLineCategory.dbc";
    public const string IconsPath = @"DBFilesClient\SpellIcon.dbc";
    public const string AbilitiesPath = @"DBFilesClient\SkillLineAbility.dbc";
    public const string RaceClassPath = @"DBFilesClient\SkillRaceClassInfo.dbc";
    public const uint DisplaySortedFlag = 0x80;
    public const uint UnlearnableFlag = 0x20;
    public const uint SilentSkillUpFlags = 0x402;
    private readonly record struct RaceClassRoute(uint RaceMask, uint ClassMask, uint Flags);
    private readonly Dictionary<uint, SkillLineInfo> _lines = new();
    private readonly Dictionary<uint, SkillCategoryInfo> _categories = new();
    private readonly Dictionary<uint, uint> _spellLines = new();
    private readonly Dictionary<uint, uint> _spellRankLines = new();
    private readonly Dictionary<uint, uint> _trainingPointCosts = new();
    private readonly Dictionary<uint, uint> _abilityPredecessors = new();
    private readonly Dictionary<uint, SkillRecipeInfo> _recipes = new();
    private readonly Dictionary<uint, List<SkillRecipeInfo>> _recipesByLine = new();
    private readonly Dictionary<uint, List<RaceClassRoute>> _raceClassRoutes = new();
    private readonly List<ClassAbilityRow> _classAbilities = new();

    /// <summary>Every ability row with its learn routing — the offline source of
    /// "which class knows which spells" that trainer packets provide online.</summary>
    public IReadOnlyList<ClassAbilityRow> AbilityRows => _classAbilities;

    public bool TryGet(uint id, out SkillLineInfo line) => _lines.TryGetValue(id, out line);
    public IEnumerable<SkillLineInfo> Lines => _lines.Values;
    public bool TryGetCategory(uint id, out SkillCategoryInfo category) => _categories.TryGetValue(id, out category);
    // Core builds its rank map in ascending SkillLineAbility row-ID order.
    public uint SpellRankLine(uint spellId) => _spellRankLines.GetValueOrDefault(spellId);
    public uint SpellLine(uint spellId) => _spellLines.GetValueOrDefault(spellId);
    public uint TrainingPointCost(uint spellId) => _trainingPointCosts.GetValueOrDefault(spellId);
    public uint AbilityChainRoot(uint spellId)
    {
        var visited = new HashSet<uint>();
        while (visited.Add(spellId) && _abilityPredecessors.TryGetValue(spellId, out uint previous))
            spellId = previous;
        return spellId;
    }
    /// <summary>Rank of a spell in its ability chain: 1 for the root, +1 per predecessor.</summary>
    public int AbilityRank(uint spellId)
    {
        int rank = 1;
        var visited = new HashSet<uint>();
        while (visited.Add(spellId) && _abilityPredecessors.TryGetValue(spellId, out uint previous))
        { spellId = previous; rank++; }
        return rank;
    }
    /// <summary>
    /// The 1.12 spellbook tab for a known spell. Generic/racial/proficiency lines whose matching
    /// SkillRaceClassInfo row carries DISPLAY_SORTED, missing lines, and lines with no admitting
    /// row collapse into General (0). Class combat lines retain their SkillLine id.
    /// </summary>
    public uint SpellTab(uint spellId, byte race, byte @class)
    {
        uint line = SpellLine(spellId);
        if (line == 0) return 0;
        if (race is 0 or > 32 || @class is 0 or > 32 || _raceClassRoutes.Count == 0)
            return line;
        uint raceBit = 1u << (race - 1), classBit = 1u << (@class - 1);
        if (!_raceClassRoutes.TryGetValue(line, out List<RaceClassRoute>? routes)) return 0;
        foreach (RaceClassRoute route in routes)
            if ((route.RaceMask == 0 || (route.RaceMask & raceBit) != 0) &&
                (route.ClassMask == 0 || (route.ClassMask & classBit) != 0))
                return (route.Flags & DisplaySortedFlag) != 0 ? 0u : line;
        return 0;
    }

    /// <summary>
    /// Whether the character's admitting SkillRaceClassInfo row permits abandoning this line.
    /// Unknown identity, missing routing data, and a missing admitting row are deliberately false:
    /// vmangos treats an invalid CMSG_UNLEARN_SKILL as an anticheat violation.
    /// </summary>
    public bool Abandonable(uint lineId, byte race, byte @class)
    {
        if (race is 0 or > 32 || @class is 0 or > 32 ||
            !_raceClassRoutes.TryGetValue(lineId, out List<RaceClassRoute>? routes))
            return false;
        uint raceBit = 1u << (race - 1), classBit = 1u << (@class - 1);
        foreach (RaceClassRoute route in routes)
            if ((route.RaceMask == 0 || (route.RaceMask & raceBit) != 0) &&
                (route.ClassMask == 0 || (route.ClassMask & classBit) != 0))
                return (route.Flags & UnlearnableFlag) != 0;
        return false;
    }
    public bool TryGetRecipe(uint spellId, out SkillRecipeInfo recipe) => _recipes.TryGetValue(spellId, out recipe);
    /// <summary>Build-5875 skill messages use the admitting race/class row's 0x402 gate.</summary>
    public bool AnnouncesSkillUps(uint lineId, byte race, byte @class)
    {
        if (!_lines.ContainsKey(lineId) || race is 0 or > 32 || @class is 0 or > 32 ||
            !_raceClassRoutes.TryGetValue(lineId, out List<RaceClassRoute>? routes)) return false;
        uint raceBit = 1u << (race - 1), classBit = 1u << (@class - 1);
        foreach (RaceClassRoute route in routes)
            if ((route.RaceMask == 0 || (route.RaceMask & raceBit) != 0) &&
                (route.ClassMask == 0 || (route.ClassMask & classBit) != 0))
                return (route.Flags & SilentSkillUpFlags) == 0;
        return false;
    }
    public IReadOnlyList<SkillRecipeInfo> Recipes(uint skillLineId) =>
        _recipesByLine.TryGetValue(skillLineId, out List<SkillRecipeInfo>? recipes) ? recipes : [];

    public static SkillLineCatalog? Load(MpqMount mpq)
    {
        DbcFile? lines = Parse(mpq, LinesPath), categories = Parse(mpq, CategoriesPath), icons = Parse(mpq, IconsPath),
            abilities = Parse(mpq, AbilitiesPath), raceClass = Parse(mpq, RaceClassPath);
        if (lines is null || categories is null || lines.FieldCount < 22 || categories.FieldCount < 11)
            return null;
        var iconMap = new Dictionary<uint, string>();
        if (icons is { FieldCount: >= 2 })
            for (int row = 0; row < icons.RecordCount; row++)
                iconMap[icons.GetUInt(row, 0)] = icons.GetString(row, 1);

        var result = new SkillLineCatalog();
        for (int row = 0; row < categories.RecordCount; row++)
        {
            uint id = categories.GetUInt(row, 0);
            result._categories[id] = new SkillCategoryInfo(id, categories.GetString(row, 1),
                categories.GetUInt(row, 10));
        }
        for (int row = 0; row < lines.RecordCount; row++)
        {
            uint id = lines.GetUInt(row, 0), category = lines.GetUInt(row, 1);
            iconMap.TryGetValue(lines.GetUInt(row, 21), out string? icon);
            result._lines[id] = new SkillLineInfo(id, category, lines.GetString(row, 3),
                lines.GetString(row, 12), string.IsNullOrWhiteSpace(icon) ? "" : icon);
        }
        var firstRankRowIds = new Dictionary<uint, uint>();
        if (abilities is { FieldCount: >= 15 })
            for (int row = 0; row < abilities.RecordCount; row++)
            {
                uint spell = abilities.GetUInt(row, 2);
                if (spell == 0) continue;
                uint line = abilities.GetUInt(row, 1);
                result._spellLines.TryAdd(spell, line);
                uint rowId = abilities.GetUInt(row, 0);
                if (!firstRankRowIds.TryGetValue(spell, out uint previousRow) || rowId < previousRow)
                {
                    firstRankRowIds[spell] = rowId;
                    result._spellRankLines[spell] = line;
                    result._trainingPointCosts[spell] = abilities.GetUInt(row, 14);
                }
                uint nextAbility = abilities.GetUInt(row, 8);
                if (nextAbility != 0) result._abilityPredecessors.TryAdd(nextAbility, spell);
                var recipe = new SkillRecipeInfo(spell, line, abilities.GetUInt(row, 7),
                    abilities.GetUInt(row, 11), abilities.GetUInt(row, 10));
                result._classAbilities.Add(new ClassAbilityRow(spell, line,
                    abilities.GetUInt(row, 3), abilities.GetUInt(row, 4),
                    abilities.GetUInt(row, 5), abilities.GetUInt(row, 6),
                    abilities.GetUInt(row, 8), abilities.GetUInt(row, 9)));
                result._recipes[spell] = recipe;
                if (!result._recipesByLine.TryGetValue(line, out List<SkillRecipeInfo>? list))
                    result._recipesByLine[line] = list = [];
                list.Add(recipe);
            }
        if (raceClass is { FieldCount: >= 8 })
            for (int row = 0; row < raceClass.RecordCount; row++)
            {
                uint line = raceClass.GetUInt(row, 1);
                if (line == 0) continue;
                if (!result._raceClassRoutes.TryGetValue(line, out List<RaceClassRoute>? routes))
                    result._raceClassRoutes[line] = routes = [];
                routes.Add(new RaceClassRoute(raceClass.GetUInt(row, 2),
                    raceClass.GetUInt(row, 3), raceClass.GetUInt(row, 4)));
            }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
    {
        byte[]? bytes = mpq.ReadFile(path);
        return bytes is null ? null : DbcFile.Parse(bytes);
    }
}
