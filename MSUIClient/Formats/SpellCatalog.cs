namespace MSUIClient.Formats;

/// <summary>The 1.12 Spell.dbc display/cast subset used by the spellbook and action bar.</summary>
public readonly record struct SpellInfo(
    uint Id, string Name, string Rank, string IconPath,
    uint Attributes, uint AttributesEx2, uint AttributesEx3,
    uint InterruptFlags, uint ChannelInterruptFlags, uint Targets, uint ImplicitTarget,
    uint RecoveryMs, uint CategoryRecoveryMs,
    uint PowerType, uint ManaCost, uint ManaCostPercent,
    uint StartRecoveryCategory, uint StartRecoveryMs, uint VisualId, float Speed, string Description,
    uint RangeIndex, uint School = 0, uint CastingTimeIndex = 0, int CastTimeMs = 0,
    uint DurationIndex = 0, int DurationMs = 0, uint[]? EffectIds = null,
    uint[]? AuraIds = null, uint[]? ImplicitTargetsA = null, uint[]? ImplicitTargetsB = null,
    uint[]? EffectRadiusIndices = null, int[]? EffectMiscValues = null,
    uint[]? EffectItemTypes = null,
    uint RequiredFocus = 0, uint Category = 0,
    uint CastUi = 0, uint ProcChance = 0, string AuraDescription = "",
    int[]? EffectDieSides = null, int[]? EffectBaseDice = null,
    int[]? EffectBasePoints = null, uint[]? EffectAmplitudes = null,
    float[]? EffectMultipleValues = null, uint[]? EffectChainTargets = null,
    uint MaxLevel = 0, uint SpellLevel = 0, uint BaseLevel = 0,
    float[]? EffectDicePerLevel = null, float[]? EffectRealPointsPerLevel = null,
    uint DispelType = 0,
    int EquippedItemClass = -1, uint EquippedItemSubclassMask = 0,
    uint EquippedItemInventoryTypeMask = 0,
    uint ActiveIconId = 0, string ActiveIconPath = "",
    uint[]? EffectTriggerSpells = null,
    int StanceBarOrder = 0,
    bool CategoryWildcard = false,
    uint AttributesEx = 0, uint Stances = 0, uint StancesNot = 0, uint DamageClass = 0,
    uint CasterAuraState = 0, uint TargetAuraState = 0, uint ManaCostPerLevel = 0,
    uint ProcCharges = 0, uint StackAmount = 0, uint MaxTargetLevel = 0,
    uint MaxAffectedTargets = 0, float[]? EffectDamageMultipliers = null,
    float[]? EffectPointsPerComboPoint = null, uint SpellFamily = 0, ulong SpellFamilyFlags = 0)
{
    public bool UsesAllPower => (AttributesEx & 0x2) != 0;
    public bool Passive => (Attributes & 0x40) != 0;
    public bool HiddenClientSide => (Attributes & 0x80) != 0;
    public bool TradeSkill => (Attributes & 0x20) != 0;
    public bool InSpellbook => !HiddenClientSide && !TradeSkill && CastUi == 0;
    public bool Ranged => (Attributes & 0x2) != 0 || AutoRepeat;
    public bool AutoRepeat => (AttributesEx2 & 0x20) != 0;
    /// <summary>
    /// SPELL_ATTR3_NORMAL_RANGED_ATTACK. Build 5875 routes these spell-log numbers through
    /// the melee color branch (Auto Shot/Throw), despite their non-melee wire opcode.
    /// </summary>
    public bool MeleeWhiteDamage => (AttributesEx3 & 0x8000) != 0;
    public bool OnNextSwing => (Attributes & 0x404) != 0;
    public bool CooldownOnEvent => (Attributes & 0x0200_0000) != 0;
    /// <summary>Build-5875 ranged shots add the live ranged-weapon speed to category recovery.</summary>
    public bool RangedSpeedCooldown =>
        (Attributes & 0x2) != 0 && (AttributesEx2 & 0x0002_0000) == 0;
    /// <summary>Attack and trade-skill openers are excluded at the cooldown getter head.</summary>
    public bool CooldownQueryExcluded => EffectIds?.FirstOrDefault() is 47 or 78;
    // SpellRec InterruptFlags: bit 0 is SPELL_INTERRUPT_FLAG_MOVEMENT. Bit 3 belongs
    // to a different interrupt reason; treating it as movement made instant, movement-
    // castable spells such as Blink send CMSG_CANCEL_CAST on the next move edge.
    public bool MovementInterrupts => (InterruptFlags & 0x01) != 0;
    public bool MovementInterruptsChannel => (ChannelInterruptFlags & 0x08) != 0;
    // Build-5875 AttributesEx declares a channel; its interrupt policy can also
    // be populated on ordinary casts such as Placing Bear Trap (9437).
    public string CastClassification => (AttributesEx & 0x44) != 0 ? "CHANNEL" :
        CastTimeMs > 0 ? "CAST_TIME" : "INSTANT";
}

/// <summary>One SpellRange.dbc row: min/max in yards; flags bit0 marks the melee row.</summary>
public readonly record struct SpellRangeRow(float Min, float Max, bool Melee);
/// <summary>One build-5875 SpellRadius.dbc row, in yards.</summary>
public readonly record struct SpellRadiusRow(float Radius, float RadiusPerLevel, float RadiusMax);
public readonly record struct SpellReagent(uint ItemId, uint Count);

public sealed class SpellCatalog
{
    public const string SpellPath = @"DBFilesClient\Spell.dbc";
    /// <summary>World Content Pack spell rows (MangosSuperUI WorldPackContent.PackRowsPath("Spell")).</summary>
    public const string PackSpellPath = @"WorldPacks\Spell.dbc";
    /// <summary>The pack spell range, [38,000, 40,000) (MangosSuperUI WorldPackContent.SpellIdBase).</summary>
    public const uint PackSpellIdBase = 38_000, PackSpellIdCeiling = 40_000;
    public const string IconPath = @"DBFilesClient\SpellIcon.dbc";
    public const string RangePath = @"DBFilesClient\SpellRange.dbc";
    public const string RadiusPath = @"DBFilesClient\SpellRadius.dbc";
    public const string CategoryPath = @"DBFilesClient\SpellCategory.dbc";
    /// <summary>
    /// A location-targeted spell with no positive authored effect radius still needs a usable
    /// placement cursor. Eight yards preserves the pre-radius implementation for that data-hole;
    /// it is never substituted for a populated SpellRadius row.
    /// </summary>
    public const float MissingTargetRadiusFallback = 8f;
    private readonly Dictionary<uint, SpellInfo> _spells = new();
    private readonly Dictionary<uint, SpellRangeRow> _ranges = new();
    private readonly Dictionary<uint, SpellRadiusRow> _radii = new();
    private readonly Dictionary<uint, int> _castTimes = new();
    private readonly Dictionary<uint, int> _durations = new();
    private readonly Dictionary<uint, SpellReagent[]> _reagents = new();
    private readonly Dictionary<uint, uint> _createdItems = new();
    private readonly Dictionary<uint, uint[]> _tools = new();
    private readonly Dictionary<uint, uint> _declaredLanguages = new();
    private readonly Dictionary<uint, uint> _modifierFamilies = new();
    public uint ModifierFamilyForClass(uint classId) => _modifierFamilies.GetValueOrDefault(classId);

    public int Count => _spells.Count;
    public IEnumerable<SpellInfo> Spells => _spells.Values;
    public bool TryGet(uint id, out SpellInfo spell) => _spells.TryGetValue(id, out spell);
    public SpellInfo? FindKnownByName(string name, IReadOnlySet<uint> known)
    {
        (string baseName, string? explicitRank) = SplitCastName(name);
        IEnumerable<SpellInfo> matches = _spells.Values.Where(x => known.Contains(x.Id) &&
            x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        if (explicitRank is not null)
            matches = matches.Where(x => x.Rank.Equals(explicitRank,
                StringComparison.OrdinalIgnoreCase));
        return matches.OrderByDescending(x => x.SpellLevel).ThenByDescending(x => x.Id)
            .Cast<SpellInfo?>().FirstOrDefault();
    }

    public static (string Name, string? Rank) SplitCastName(string text)
    {
        string value = text.Trim();
        if (!value.EndsWith(')')) return (value, null);
        int open = value.LastIndexOf('(');
        if (open <= 0) return (value, null);
        string rank = value[(open + 1)..^1].Trim();
        string name = value[..open].TrimEnd();
        return name.Length > 0 && rank.Length > 0 ? (name, rank) : (value, null);
    }
    public bool TryGetRange(uint rangeIndex, out SpellRangeRow range) => _ranges.TryGetValue(rangeIndex, out range);
    public bool TryGetRadius(uint radiusIndex, out SpellRadiusRow radius) =>
        _radii.TryGetValue(radiusIndex, out radius);

    /// <summary>
    /// Resolve the visible footprint of a location-targeted spell. All three populated effect
    /// lanes contribute: mixed-radius spells such as build-5875 Flamestrike (8/5 yd) and Spice
    /// Mortar (10/20 yd) must display the complete affected area, not whichever lane happens to
    /// occur first. SpellRadius's per-level term is exactly zero in every mounted 5875 row.
    /// </summary>
    public bool TryGetTargetingRadius(in SpellInfo spell, out float radius)
    {
        radius = 0f;
        if (spell.EffectIds is null || spell.EffectRadiusIndices is null) return false;
        int lanes = Math.Min(spell.EffectIds.Length, spell.EffectRadiusIndices.Length);
        for (int i = 0; i < lanes; i++)
        {
            if (spell.EffectIds[i] == 0 ||
                !_radii.TryGetValue(spell.EffectRadiusIndices[i], out SpellRadiusRow row) ||
                !float.IsFinite(row.Radius) || row.Radius <= 0f) continue;
            radius = Math.Max(radius, row.Radius);
        }
        return radius > 0f;
    }

    public float TargetingRadius(in SpellInfo spell) =>
        TryGetTargetingRadius(spell, out float radius) ? radius : MissingTargetRadiusFallback;
    public IReadOnlyList<SpellReagent> Reagents(uint spellId) =>
        _reagents.TryGetValue(spellId, out SpellReagent[]? reagents) ? reagents : [];
    public uint CreatedItem(uint spellId) => _createdItems.GetValueOrDefault(spellId);
    public IReadOnlyList<uint> Tools(uint spellId) =>
        _tools.TryGetValue(spellId, out uint[]? tools) ? tools : [];
    public uint DeclaredLanguage(uint spellId) => _declaredLanguages.GetValueOrDefault(spellId);

    public static SpellCatalog? Load(MpqMount mpq)
    {
        byte[]? spellBytes = mpq.ReadFile(SpellPath);
        byte[]? iconBytes = mpq.ReadFile(IconPath);
        DbcFile? stockSpells = spellBytes is null ? null : DbcFile.Parse(spellBytes);
        DbcFile? icons = iconBytes is null ? null : DbcFile.Parse(iconBytes);
        if (stockSpells is null || icons is null || stockSpells.FieldCount < 173 || icons.FieldCount < 2)
            return null;

        var iconMap = new Dictionary<uint, string>();
        for (int row = 0; row < icons.RecordCount; row++)
        {
            uint id = icons.GetUInt(row, 0);
            string path = icons.GetString(row, 1);
            if (id != 0 && path.Length > 0)
                iconMap[id] = path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        }

        var result = new SpellCatalog();
        DbcFile? classes = Parse(mpq, @"DBFilesClient\ChrClasses.dbc");
        if (classes is not null && classes.FieldCount >= 16)
            for (int row = 0; row < classes.RecordCount; row++)
                result._modifierFamilies[classes.GetUInt(row, 0)] = classes.GetUInt(row, 15);

        // SpellCategory flags bit 0x2 makes that category match every cooldown query. In the
        // shipped 5875 data this is category 351 (wand Shoot), but read the authored table
        // instead of baking the row id into behavior.
        var wildcardCategories = new HashSet<uint>();
        DbcFile? categories = Parse(mpq, CategoryPath);
        if (categories is not null && categories.FieldCount >= 2)
            for (int row = 0; row < categories.RecordCount; row++)
                if ((categories.GetUInt(row, 1) & 0x2) != 0)
                    wildcardCategories.Add(categories.GetUInt(row, 0));

        DbcFile? castTimes = Parse(mpq, @"DBFilesClient\SpellCastTimes.dbc");
        if (castTimes is not null && castTimes.FieldCount >= 4)
            for (int row = 0; row < castTimes.RecordCount; row++)
                result._castTimes[castTimes.GetUInt(row, 0)] = Math.Max(0, castTimes.GetInt(row, 1));
        DbcFile? durations = Parse(mpq, @"DBFilesClient\SpellDuration.dbc");
        if (durations is not null && durations.FieldCount >= 4)
            for (int row = 0; row < durations.RecordCount; row++)
                result._durations[durations.GetUInt(row, 0)] = durations.GetInt(row, 1);

        // SpellRange.dbc: [0]=id, [1]=min f32, [2]=max f32, [3]=flags (bit0 = melee row).
        byte[]? rangeBytes = mpq.ReadFile(RangePath);
        DbcFile? ranges = rangeBytes is null ? null : DbcFile.Parse(rangeBytes);
        if (ranges is not null && ranges.FieldCount >= 4)
        {
            for (int row = 0; row < ranges.RecordCount; row++)
            {
                uint id = ranges.GetUInt(row, 0);
                result._ranges[id] = new SpellRangeRow(ranges.GetFloat(row, 1),
                    ranges.GetFloat(row, 2), (ranges.GetUInt(row, 3) & 0x1) != 0);
            }
        }

        DbcFile? radii = Parse(mpq, RadiusPath);
        if (radii is not null && radii.FieldCount >= 4)
        {
            for (int row = 0; row < radii.RecordCount; row++)
            {
                uint id = radii.GetUInt(row, 0);
                if (id == 0) continue;
                result._radii[id] = new SpellRadiusRow(radii.GetFloat(row, 1),
                    radii.GetFloat(row, 2), radii.GetFloat(row, 3));
            }
        }

        // World Content Pack spells (kart mounts, portal teleports) arrive as a ROWS-ONLY Spell.dbc in
        // patch-7: a whole Spell.dbc there would hide patch-3's custom spells (MPQ resolves whole files).
        var sources = new List<DbcFile> { stockSpells };
        if (Parse(mpq, PackSpellPath) is { FieldCount: >= 173 } packSpells)
            sources.Add(packSpells);
        foreach (DbcFile spells in sources)
        for (int row = 0; row < spells.RecordCount; row++)
        {
            uint id = spells.GetUInt(row, 0);
            if (id == 0) continue;
            if (!ReferenceEquals(spells, stockSpells) && id is < PackSpellIdBase or >= PackSpellIdCeiling)
                continue;
            if (spells.GetUInt(row, 61) == 39 && spells.GetInt(row, 106) > 0)
                result._declaredLanguages[id] = (uint)spells.GetInt(row, 106);
            iconMap.TryGetValue(spells.GetUInt(row, 117), out string? icon);
            uint activeIconId = spells.GetUInt(row, 118);
            iconMap.TryGetValue(activeIconId, out string? activeIcon);
            uint castTimeIndex = spells.GetUInt(row, 18), durationIndex = spells.GetUInt(row, 30);
            var classification = SpellClassificationColumns.Read(spells, row);
            result._spells[id] = new SpellInfo(
                id, spells.GetString(row, 120), spells.GetString(row, 129), icon ?? "",
                spells.GetUInt(row, 6), spells.GetUInt(row, 8), spells.GetUInt(row, 9),
                spells.GetUInt(row, 21), spells.GetUInt(row, 23),
                spells.GetUInt(row, 13), spells.GetUInt(row, 82),
                spells.GetUInt(row, 19), spells.GetUInt(row, 20),
                spells.GetUInt(row, 31), spells.GetUInt(row, 32), spells.GetUInt(row, 156),
                spells.GetUInt(row, 157), spells.GetUInt(row, 158), spells.GetUInt(row, 115),
                spells.GetFloat(row, 37), spells.GetString(row, 138),
                spells.GetUInt(row, 36), spells.GetUInt(row, 1), castTimeIndex,
                result._castTimes.GetValueOrDefault(castTimeIndex), durationIndex,
                result._durations.GetValueOrDefault(durationIndex),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 61 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 91 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 82 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 85 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 88 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetInt(row, 106 + i)).ToArray(),
                Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 103 + i)).ToArray(),
                classification.RequiredFocus, classification.Category,
                CastUi: spells.GetUInt(row, 3), ProcChance: spells.GetUInt(row, 25),
                AuraDescription: spells.GetString(row, 147),
                EffectDieSides: Enumerable.Range(0, 3).Select(i => spells.GetInt(row, 64 + i)).ToArray(),
                EffectBaseDice: Enumerable.Range(0, 3).Select(i => spells.GetInt(row, 67 + i)).ToArray(),
                EffectBasePoints: Enumerable.Range(0, 3).Select(i => spells.GetInt(row, 76 + i)).ToArray(),
                EffectAmplitudes: Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 94 + i)).ToArray(),
                EffectMultipleValues: Enumerable.Range(0, 3).Select(i => spells.GetFloat(row, 97 + i)).ToArray(),
                EffectChainTargets: Enumerable.Range(0, 3).Select(i => spells.GetUInt(row, 100 + i)).ToArray(),
                // Column order in build 5875 is maxLevel(27), baseLevel(28),
                // spellLevel(29) — vmangos SpellEntry is the reference. These
                // were swapped here for a while; the tooltip level leg wants the
                // true spellLevel column.
                MaxLevel: spells.GetUInt(row, 27), SpellLevel: spells.GetUInt(row, 29),
                BaseLevel: spells.GetUInt(row, 28),
                EffectDicePerLevel: Enumerable.Range(0, 3).Select(i => spells.GetFloat(row, 70 + i)).ToArray(),
                EffectRealPointsPerLevel: Enumerable.Range(0, 3).Select(i => spells.GetFloat(row, 73 + i)).ToArray(),
                DispelType: classification.DispelType,
                EquippedItemClass: spells.GetInt(row, 58),
                EquippedItemSubclassMask: spells.GetUInt(row, 59),
                EquippedItemInventoryTypeMask: spells.GetUInt(row, 60),
                ActiveIconId: activeIconId, ActiveIconPath: activeIcon ?? "",
                EffectTriggerSpells: Enumerable.Range(0, 3)
                    .Select(i => spells.GetUInt(row, 109 + i)).ToArray(),
                StanceBarOrder: spells.GetInt(row, 166),
                CategoryWildcard: wildcardCategories.Contains(classification.Category),
                AttributesEx: spells.GetUInt(row, 7),
                Stances: spells.GetUInt(row, 11), StancesNot: spells.GetUInt(row, 12), DamageClass: spells.GetUInt(row, 164),
                CasterAuraState: spells.GetUInt(row, 16), TargetAuraState: spells.GetUInt(row, 17), ManaCostPerLevel: spells.GetUInt(row, 33),
                ProcCharges: spells.GetUInt(row, 26), StackAmount: spells.GetUInt(row, 39),
                MaxTargetLevel: spells.GetUInt(row, 159), MaxAffectedTargets: spells.GetUInt(row, 163),
                EffectDamageMultipliers: Enumerable.Range(0, 3).Select(i => spells.GetFloat(row, 167 + i)).ToArray(),
                EffectPointsPerComboPoint: Enumerable.Range(0, 3).Select(i => spells.GetFloat(row, 112 + i)).ToArray(),
                SpellFamily: spells.GetUInt(row, 160),
                SpellFamilyFlags: spells.GetUInt(row, 161) | ((ulong)spells.GetUInt(row, 162) << 32));
            var requirements = SpellRequirementColumns.Read(spells, row);
            if (requirements.Tools.Length > 0) result._tools[id] = requirements.Tools;
            if (requirements.Reagents.Length > 0) result._reagents[id] = requirements.Reagents;
            for (int i = 0; i < 3; i++)
                if (spells.GetUInt(row, 61 + i) == 24 && spells.GetUInt(row, 103 + i) != 0)
                { result._createdItems[id] = spells.GetUInt(row, 103 + i); break; }
        }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
        => mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}
