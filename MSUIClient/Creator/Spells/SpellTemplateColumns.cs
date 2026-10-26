namespace MSUIClient.Creator.Spells;

// ═══════════════════════════════════════════════════════════════════════════
// SPELL TEMPLATE COLUMNS — the vocabulary of a spell's RULES (shared_docs/SPELL_CREATOR_PLAN.md
// P1.1). The client's Spell.dbc and the server's spell_template describe the same spell; this
// is the column-by-column correspondence, under the SERVER's column names, so a rules block
// written here is literally a set of spell_template overrides and the Completer needs no
// translation (it mirrors them back into Spell.dbc through its own copy of this table,
// MangosSuperUI Services/SpellServices/SpellDbcColumnMap.cs - the two MUST stay identical;
// the emitter lab compares them when the web repo is checked out beside this one).
//
// Derived from data, not memory: every column of 21,693 stock spells compared with every
// Spell.dbc field, each entry the field that agrees on all of them (2026-10-25).
// ═══════════════════════════════════════════════════════════════════════════
public static class SpellTemplateColumns
{
    public enum Kind { Int, Float, BigLow, BigHigh }

    public readonly record struct Column(string Name, int Field, Kind Kind, string Label);

    public const int FieldCount = 173;

    public static readonly IReadOnlyList<Column> All = Build();

    private static readonly Dictionary<string, Column> ByName =
        All.Where(c => c.Kind != Kind.BigHigh).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out Column column) => ByName.TryGetValue(name, out column);

    private static List<Column> Build()
    {
        var c = new List<Column>();
        void I(string name, int field, string label) => c.Add(new Column(name, field, Kind.Int, label));
        void F(string name, int field, string label) => c.Add(new Column(name, field, Kind.Float, label));

        I("school", 1, "School (0 physical, 1 holy, 2 fire, 3 nature, 4 frost, 5 shadow, 6 arcane)");
        I("category", 2, "Cooldown category"); I("castUI", 3, "Cast UI"); I("dispel", 4, "Dispel type");
        I("mechanic", 5, "Mechanic"); I("attributes", 6, "Attributes"); I("attributesEx", 7, "Attributes Ex");
        I("attributesEx2", 8, "Attributes Ex2"); I("attributesEx3", 9, "Attributes Ex3");
        I("attributesEx4", 10, "Attributes Ex4"); I("stances", 11, "Required stances");
        I("stancesNot", 12, "Forbidden stances"); I("targets", 13, "Target flags (0x40 = ground point)");
        I("targetCreatureType", 14, "Target creature types"); I("requiresSpellFocus", 15, "Required spell focus");
        I("casterAuraState", 16, "Caster aura state"); I("targetAuraState", 17, "Target aura state");
        I("castingTimeIndex", 18, "Cast time (SpellCastTimes row)"); I("recoveryTime", 19, "Cooldown (ms)");
        I("categoryRecoveryTime", 20, "Category cooldown (ms)"); I("interruptFlags", 21, "Interrupt flags");
        I("auraInterruptFlags", 22, "Aura interrupt flags"); I("channelInterruptFlags", 23, "Channel interrupt flags");
        I("procFlags", 24, "Proc flags"); I("procChance", 25, "Proc chance (%)"); I("procCharges", 26, "Proc charges");
        I("maxLevel", 27, "Max level"); I("baseLevel", 28, "Base level"); I("spellLevel", 29, "Spell level");
        I("durationIndex", 30, "Duration (SpellDuration row)"); I("powerType", 31, "Power type (0 mana, 1 rage, 3 energy)");
        I("manaCost", 32, "Power cost"); I("manCostPerLevel", 33, "Cost per level"); I("manaPerSecond", 34, "Cost per second");
        I("manaPerSecondPerLevel", 35, "Cost per second per level"); I("rangeIndex", 36, "Range (SpellRange row)");
        F("speed", 37, "Missile speed (yd/s)"); I("modelNextSpell", 38, "Next spell"); I("stackAmount", 39, "Stack amount");
        I("totem1", 40, "Totem 1"); I("totem2", 41, "Totem 2");
        for (int i = 1; i <= 8; i++) I($"reagent{i}", 41 + i, $"Reagent {i}");
        for (int i = 1; i <= 8; i++) I($"reagentCount{i}", 49 + i, $"Reagent {i} count");
        I("equippedItemClass", 58, "Required item class (-1 none)"); I("equippedItemSubClassMask", 59, "Required item subclasses");
        I("equippedItemInventoryTypeMask", 60, "Required inventory types");
        for (int i = 1; i <= 3; i++)
        {
            I($"effect{i}", 60 + i, $"Effect {i}: type");
            I($"effectDieSides{i}", 63 + i, $"Effect {i}: die sides");
            I($"effectBaseDice{i}", 66 + i, $"Effect {i}: base dice");
            F($"effectDicePerLevel{i}", 69 + i, $"Effect {i}: dice per level");
            F($"effectRealPointsPerLevel{i}", 72 + i, $"Effect {i}: points per level");
            I($"effectBasePoints{i}", 75 + i, $"Effect {i}: base points");
            I($"effectMechanic{i}", 78 + i, $"Effect {i}: mechanic");
            I($"effectImplicitTargetA{i}", 81 + i, $"Effect {i}: target A");
            I($"effectImplicitTargetB{i}", 84 + i, $"Effect {i}: target B");
            I($"effectRadiusIndex{i}", 87 + i, $"Effect {i}: radius (SpellRadius row)");
            I($"effectApplyAuraName{i}", 90 + i, $"Effect {i}: aura");
            I($"effectAmplitude{i}", 93 + i, $"Effect {i}: tick (ms)");
            F($"effectMultipleValue{i}", 96 + i, $"Effect {i}: multiple value");
            I($"effectChainTarget{i}", 99 + i, $"Effect {i}: chain targets");
            I($"effectItemType{i}", 102 + i, $"Effect {i}: item type");
            I($"effectMiscValue{i}", 105 + i, $"Effect {i}: misc value");
            I($"effectTriggerSpell{i}", 108 + i, $"Effect {i}: trigger spell");
            F($"effectPointsPerComboPoint{i}", 111 + i, $"Effect {i}: points per combo point");
        }
        I("spellVisual2", 116, "Second visual"); I("activeIconId", 118, "Active icon"); I("spellPriority", 119, "Priority");
        I("manaCostPercentage", 156, "Cost (% of base)"); I("startRecoveryCategory", 157, "Global cooldown category");
        I("startRecoveryTime", 158, "Global cooldown (ms)"); I("maxTargetLevel", 159, "Max target level");
        I("spellFamilyName", 160, "Spell family");
        c.Add(new Column("spellFamilyFlags", 161, Kind.BigLow, "Spell family flags"));
        c.Add(new Column("spellFamilyFlags", 162, Kind.BigHigh, "Spell family flags (high)"));
        I("maxAffectedTargets", 163, "Max targets"); I("dmgClass", 164, "Damage class (0 none, 1 magic, 2 melee, 3 ranged)");
        I("preventionType", 165, "Prevention type"); I("stanceBarOrder", 166, "Stance bar order");
        for (int i = 1; i <= 3; i++) F($"dmgMultiplier{i}", 166 + i, $"Effect {i}: damage multiplier");
        I("minFactionId", 170, "Min faction"); I("minReputation", 171, "Min reputation"); I("requiredAuraVision", 172, "Aura vision");
        return c;
    }

    /// <summary>Every rule of one Spell.dbc row, by server column name (spellFamilyFlags as one
    /// 64-bit value). Missing fields read as 0.</summary>
    public static Dictionary<string, double> FromDbcRow(IReadOnlyList<uint> row)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        ulong familyLow = 0, familyHigh = 0;
        foreach (Column column in All)
        {
            uint raw = column.Field < row.Count ? row[column.Field] : 0u;
            switch (column.Kind)
            {
                case Kind.Float: values[column.Name] = BitConverter.UInt32BitsToSingle(raw); break;
                case Kind.BigLow: familyLow = raw; break;
                case Kind.BigHigh: familyHigh = raw; break;
                default: values[column.Name] = unchecked((int)raw); break;
            }
        }
        values["spellFamilyFlags"] = (double)((familyHigh << 32) | familyLow);
        return values;
    }

    /// <summary>The column's value as the JSON number spell_template stores.</summary>
    public static object JsonValue(Column column, double value) => column.Kind == Kind.Float
        ? (object)(float)value
        : (object)(long)value;
}
