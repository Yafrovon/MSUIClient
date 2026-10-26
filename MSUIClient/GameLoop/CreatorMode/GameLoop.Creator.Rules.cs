using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator.Spells;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// RULES — what the spell DOES, authored in the workshop (shared_docs/SPELL_CREATOR_PLAN.md
// P1.4, decision D1). The first part of a spell: who it targets and from how far, how long it
// takes, what it costs, and up to three effects - each a real vmangos effect with a real
// handler, pointed at real targets, sized by real SpellRadius/SpellCastTimes/SpellDuration/
// SpellRange rows shown in yards and seconds.
//
// Every value is a spell_template column (Creator/Spells/SpellTemplateColumns.cs) held in the
// doc's SpellRules beside its baseline; only what differs from the baseline leaves in the
// session's `rules` block, and the Completer writes it straight into spell_template (the
// full Spell.dbc mirror carries it to every client). The legality law (SpellLegality) runs
// on every frame the section is open and says, per field, what will and will not work.
//
// RECIPES (P1.6) start a spell from a real one: "Ground missile" is Fiery Burst's rules made
// slow and deferred, "Burst + linger" is Flamestrike's, and so on - never numbers from memory.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private SpellRuleTables? _spellRuleTables;
    private bool _spellRuleTablesTried;
    private string _rulesStatus = "";
    private int _rulesCopySpell;
    private bool _rulesAdvancedOpen;

    private SpellRuleTables? SpellRuleTablesLoaded
    {
        get
        {
            if (_spellRuleTables is null && !_spellRuleTablesTried && _mpq is not null)
            {
                _spellRuleTablesTried = true;
                _spellRuleTables = SpellRuleTables.Load(_mpq);
                Console.WriteLine(_spellRuleTables is null
                    ? "[creator] rules: Spell.dbc unreadable - the Rules part is unavailable"
                    : $"[creator] rules: {_spellRuleTables.SpellCount} spell rows, {_spellRuleTables.CastTimes.Count} cast times, " +
                      $"{_spellRuleTables.Durations.Count} durations, {_spellRuleTables.Ranges.Count} ranges, {_spellRuleTables.Radii.Count} radii");
            }
            // D5: the open spell's new rows sit in the tables like any other (re-added after a
            // patch hot-swap re-reads them).
            if (_spellRuleTables is { } tables && _creatorSpell?.Rules is { } rules)
                foreach (SpellRules.NewIndexRow row in rules.NewIndexRows)
                    tables.AddLocalRow(row.Table, row.LocalId, row.Value);
            return _spellRuleTables;
        }
    }

    /// <summary>A patch hot-swap re-reads the tables on next use.</summary>
    private void InvalidateSpellRuleTables()
    {
        _spellRuleTables = null;
        _spellRuleTablesTried = false;
    }

    /// <summary>The doc's rules, created on first use from its baseline: the spell's own row, or
    /// the from-zero template for a new spell.</summary>
    private SpellRules? CreatorRules(CreatorSpellDoc doc)
    {
        if (doc.Rules is not null) return doc.Rules;
        if (SpellRuleTablesLoaded is not { } tables) return null;
        uint baseline = IsCreatorNewSpellId(doc.Info.Id) ? SpellRules.FromZeroTemplateSpell : doc.Info.Id;
        if (!tables.TryGetSpellRow(baseline, out uint[] row)) return null;
        doc.Rules = SpellRules.FromDbcRow(baseline, row);
        return doc.Rules;
    }

    // ── recipes ─────────────────────────────────────────────────────────────

    private sealed record CreatorRecipe(string Name, uint Source, string Words, SpellStage[] Plan, bool Missile,
        Action<SpellRules>? Adjust = null);

    /// <summary>Columns a recipe never copies: identity and class wiring stay the new spell's own.</summary>
    private static readonly HashSet<string> RecipeKeepColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "spellFamilyName", "spellFamilyFlags", "category", "spellVisual2", "activeIconId",
    };

    private static readonly CreatorRecipe[] CreatorRecipes =
    {
        new("Bolt", 133, "Fireball: a missile at an enemy unit, damage on hit.",
            new[] { SpellStage.Precast, SpellStage.Cast, SpellStage.Impact }, true),
        new("Ground missile", 13900, "Fiery Burst made slow and deferred: the player picks a spot, a missile " +
            "flies there at 8 yd/s, the enemies within 10 yd take the damage WHEN IT LANDS.",
            new[] { SpellStage.Cast, SpellStage.Impact }, true,
            r => { r.Set("speed", 8); r.SetDeferred(true); r.Set("castingTimeIndex", 4); r.Set("rangeIndex", 4); }),
        new("Burst + linger", 2120, "Flamestrike: damage at a picked spot, then a burning patch that ticks.",
            new[] { SpellStage.Cast, SpellStage.Impact }, false),
        new("Rain", 10, "Blizzard: a channelled area at a picked spot that ticks while you channel.",
            new[] { SpellStage.Channel }, false),
        new("Chain", 421, "Chain Lightning: a beam that jumps between enemies.",
            new[] { SpellStage.Cast, SpellStage.Impact }, false),
        new("Melee strike", 845, "Cleave: a weapon swing that hits the target and one more.",
            new[] { SpellStage.Cast, SpellStage.Impact }, false),
        new("Self buff", 168, "Frost Armor: an aura on yourself for minutes.",
            new[] { SpellStage.Cast, SpellStage.State }, false),
        new("Heal", 2050, "Lesser Heal: a cast that heals a friendly unit.",
            new[] { SpellStage.Precast, SpellStage.Cast, SpellStage.Impact }, false),
        new("Channel", 689, "Drain Life: a channelled drain on an enemy unit.",
            new[] { SpellStage.Channel, SpellStage.State }, false),
    };

    private void ApplyCreatorRecipe(CreatorSpellDoc doc, SpellRules rules, CreatorRecipe recipe)
    {
        if (SpellRuleTablesLoaded is not { } tables || !tables.TryGetSpellRow(recipe.Source, out uint[] row))
        {
            _rulesStatus = $"Recipe '{recipe.Name}': spell {recipe.Source} is not in this client's Spell.dbc.";
            return;
        }
        rules.CopyFrom(row, SpellTemplateColumns.All.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !RecipeKeepColumns.Contains(c)));
        recipe.Adjust?.Invoke(rules);
        doc.PlanStarted = true;
        doc.PlannedStages.Clear();
        foreach (SpellStage stage in recipe.Plan) doc.PlannedStages.Add(stage);
        doc.PlannedMissile = recipe.Missile;
        string source = _spellCatalog?.TryGet(recipe.Source, out SpellInfo s) == true ? s.Name : $"#{recipe.Source}";
        _rulesStatus = $"Recipe '{recipe.Name}': the rules of {source} (#{recipe.Source}) - now make them yours.";
    }

    // ── the body ────────────────────────────────────────────────────────────

    private static readonly string[] RulesSchoolNames = { "Physical", "Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane" };
    private static readonly string[] RulesPowerNames = { "Mana", "Rage", "Focus", "Energy", "Happiness" };
    private static readonly string[] RulesDmgClassNames = { "None", "Magic", "Melee", "Ranged" };

    private void DrawCreatorRulesBody()
    {
        float cs = CreatorUiScale;
        if (_creatorSpell is not { } doc) { ImGui.TextDisabled("Pick or start a spell first."); return; }
        if (CreatorRules(doc) is not { } rules)
        {
            ImGui.TextDisabled("This client's Spell.dbc could not be read, so the rules cannot be shown.");
            return;
        }
        SpellRuleTables tables = SpellRuleTablesLoaded!;
        SpellLegality.Data legal = SpellLegality.Load();

        CreatorHelp("What the spell DOES on the server: who it targets, from how far, how long it takes, " +
                    "what it costs and its three effects. Every field is a real spell_template column; only " +
                    "what you change leaves in the session, and the Completer writes it as it is. The lines at " +
                    "the bottom say what this server will and will not do with it.");
        ImGui.TextDisabled($"baseline: #{rules.BaselineSpell} " +
                           (_spellCatalog?.TryGet(rules.BaselineSpell, out SpellInfo baseInfo) == true ? baseInfo.Name : "") +
                           $"   changed: {rules.ChangedColumns.Count()}");

        // RECIPES
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "START FROM A REAL SPELL");
        float shelfRight = ImGui.GetWindowContentRegionMax().X;
        for (int i = 0; i < CreatorRecipes.Length; i++)
        {
            CreatorRecipe recipe = CreatorRecipes[i];
            if (i > 0)
            {
                float next = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X +
                             ImGui.CalcTextSize(recipe.Name).X + 12f * cs;
                if (next < shelfRight) ImGui.SameLine();
            }
            if (ImGui.SmallButton(recipe.Name + "##recipe")) ApplyCreatorRecipe(doc, rules, recipe);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(recipe.Words + $"\n\nCopies spell #{recipe.Source}'s rules and sets the parts plan.");
        }
        if (_rulesStatus.Length > 0) ImGui.TextWrapped(_rulesStatus);

        // TARGET
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "TARGET");
        bool ground = rules.GroundTargeted;
        if (ImGui.Checkbox("Player picks a spot on the ground", ref ground))
        {
            uint mask = (uint)rules.GetInt("targets");
            rules.Set("targets", ground ? mask | SpellRules.TargetFlagDestLocation : mask & ~SpellRules.TargetFlagDestLocation);
        }
        CreatorHelp("The ground cursor (target flag 0x40). The effects then usually target the point: 16 = enemies " +
                    "around it, 18 = the point itself (summons, triggers), 28 = a lasting area.");
        if (rules.GroundTargeted)
        {
            if (ImGui.SmallButton(_creatorGroundPickArmed ? "click the ground..." : "Pick the spot"))
                _creatorGroundPickArmed = !_creatorGroundPickArmed;
            ImGui.SameLine();
            ImGui.TextDisabled(_creatorGroundPoint is { } spot && _controller is not null
                ? $"spot {Vector3.Distance(spot, _controller.Position):0.#} yd away"
                : "spot 15 yd ahead (default)");
            if (_creatorGroundPoint is not null) { ImGui.SameLine(); if (ImGui.SmallButton("reset spot")) _creatorGroundPoint = null; }
            CreatorHelp("Where the preview casts to. The missile flies there at the spell's speed and the impact " +
                        "plays there - the rings on the stage show the range (blue, around you) and the blast (amber).");
        }
        RulesIndexCombo("Range##rules", rules, "rangeIndex", tables.Ranges.Keys.OrderBy(k => tables.Ranges[k].Max),
            tables.RangeText, cs);

        // TIMING
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "TIMING");
        RulesIndexCombo("Cast time##rules", rules, "castingTimeIndex",
            tables.CastTimes.Keys.OrderBy(k => tables.CastTimes[k].BaseMs), tables.CastTimeText, cs);
        RulesIndexCombo("Duration##rules", rules, "durationIndex",
            new uint[] { 0 }.Concat(tables.Durations.Keys.OrderBy(k => tables.Durations[k].BaseMs < 0 ? int.MaxValue : tables.Durations[k].BaseMs)),
            tables.DurationText, cs);
        RulesInt("Cooldown (ms)##rules", rules, "recoveryTime", 0, 3_600_000, cs);
        RulesInt("Global cooldown (ms)##rules", rules, "startRecoveryTime", 0, 1500, cs);
        float speed = rules.Speed;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Missile speed (yd/s)##rules", ref speed, 0f, 60f, speed <= 0f ? "no missile" : "%.1f yd/s"))
            rules.Set("speed", MathF.Round(speed, 1));
        CreatorHelp("0 = the effect lands instantly. Above 0 a missile flies from the caster, and the effect waits " +
                    "for it: distance / speed. 5 to 10 is slow and readable, Fireball is 24, an arrow about 40.");
        bool deferred = rules.Deferred;
        bool canDefer = rules.GroundTargeted && rules.Speed > 0f;
        if (!canDefer) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Land, THEN hit (deferred destination)##rules", ref deferred)) rules.SetDeferred(deferred);
        if (!canDefer) ImGui.EndDisabled();
        CreatorHelp("SuperUI's Core flag (SPELL_CUSTOM_DEFERRED_DEST): the enemies at the spot are chosen, and the " +
                    "lasting area / summon / triggered spell placed, when the missile ARRIVES - not at cast time, " +
                    "which is what stock vmangos does. Only for a ground-targeted spell with a speed.");

        // COST & SCHOOL
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "COST & SCHOOL");
        RulesCombo("School##rules", rules, "school", RulesSchoolNames, cs);
        RulesCombo("Damage class##rules", rules, "dmgClass", RulesDmgClassNames, cs);
        RulesCombo("Power##rules", rules, "powerType", RulesPowerNames, cs);
        RulesInt("Cost##rules", rules, "manaCost", 0, 100_000, cs);
        RulesInt("Spell level##rules", rules, "spellLevel", 0, 60, cs);

        // EFFECTS
        for (int e = 1; e <= 3; e++) DrawCreatorRulesEffect(rules, tables, legal, e, cs);

        // COPY FROM ANY SPELL
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "COPY FROM ANY SPELL");
        ImGui.SetNextItemWidth(110f * cs);
        ImGui.InputInt("spell id##rules-copy", ref _rulesCopySpell);
        string copyName = _rulesCopySpell > 0 && _spellCatalog?.TryGet((uint)_rulesCopySpell, out SpellInfo ci) == true ? ci.Name : "";
        ImGui.SameLine();
        ImGui.TextDisabled(copyName.Length > 0 ? copyName : "(type a spell id)");
        if (copyName.Length > 0 && tables.TryGetSpellRow((uint)_rulesCopySpell, out uint[] copyRow))
        {
            if (ImGui.SmallButton("targeting")) rules.CopyFrom(copyRow, new[] { "targets", "rangeIndex" }.Concat(
                Enumerable.Range(1, 3).SelectMany(i => new[] { $"effectImplicitTargetA{i}", $"effectImplicitTargetB{i}", $"effectRadiusIndex{i}" })));
            ImGui.SameLine();
            if (ImGui.SmallButton("timing")) rules.CopyFrom(copyRow, new[] { "castingTimeIndex", "durationIndex", "recoveryTime",
                "startRecoveryTime", "startRecoveryCategory", "speed", "interruptFlags", "auraInterruptFlags", "channelInterruptFlags" });
            ImGui.SameLine();
            if (ImGui.SmallButton("effects")) rules.CopyFrom(copyRow, SpellTemplateColumns.All
                .Where(c => c.Name.StartsWith("effect", StringComparison.Ordinal)).Select(c => c.Name));
            ImGui.SameLine();
            if (ImGui.SmallButton("everything")) rules.CopyFrom(copyRow, SpellTemplateColumns.All.Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !RecipeKeepColumns.Contains(c)));
        }

        // THE LAW
        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "WILL IT WORK");
        List<SpellLegality.Line> lines = SpellLegality.Validate(rules, tables,
            id => tables.TryGetSpellRow(id, out _));
        foreach (SpellLegality.Line line in lines.OrderByDescending(l => l.Severity))
        {
            Vector4 colour = line.Severity switch
            {
                SpellLegality.Severity.Error => new Vector4(0.95f, 0.4f, 0.4f, 1f),
                SpellLegality.Severity.Warning => new Vector4(0.95f, 0.75f, 0.35f, 1f),
                _ => new Vector4(0.6f, 0.8f, 0.6f, 1f),
            };
            ImGui.TextColored(colour, (line.Severity == SpellLegality.Severity.Error ? "ERROR  " :
                line.Severity == SpellLegality.Severity.Warning ? "warn   " : "ok     ") + line.Message);
        }

        // ADVANCED: every column
        ImGui.Spacing();
        if (ImGui.Checkbox("Every column (advanced)##rules", ref _rulesAdvancedOpen)) { }
        CreatorHelp("All 134 spell_template columns the client and the server share, plus customFlags. For the " +
                    "fields the cards above do not name - attributes, procs, reagents, mechanics, stances.");
        if (_rulesAdvancedOpen)
            foreach (string column in SpellTemplateColumns.All.Where(c => c.Kind != SpellTemplateColumns.Kind.BigHigh)
                         .Select(c => c.Name).Append("customFlags"))
            {
                double value = rules.Get(column);
                ImGui.SetNextItemWidth(120f * cs);
                if (ImGui.InputDouble($"##adv-{column}", ref value, 0, 0, "%.6g")) rules.Set(column, value);
                ImGui.SameLine();
                string label = SpellTemplateColumns.TryGet(column, out var meta) ? meta.Label : "SuperUI custom flags (0x4000 = deferred destination)";
                if (rules.Changed(column)) ImGui.TextColored(SpellIdeGold, $"{column}  *  {label}");
                else ImGui.TextDisabled($"{column}  {label}");
            }
    }

    private void DrawCreatorRulesEffect(SpellRules rules, SpellRuleTables tables, SpellLegality.Data legal, int e, float cs)
    {
        ImGui.Spacing();
        ImGui.PushID($"rules-effect-{e}");
        int type = rules.EffectType(e);
        ImGui.TextColored(SpellIdeGold, $"EFFECT {e}" + (type == 0 ? "  (empty)" : $"  {legal.EffectName(type)}"));

        // Type: implemented effects, the most used first, with their use counts.
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Type", type == 0 ? "none" : $"{type} {legal.EffectName(type)}"))
        {
            if (ImGui.Selectable("none", type == 0)) rules.Set(SpellRules.Column("effect", e), 0);
            foreach (SpellLegality.Entry entry in legal.Effects.Where(x => x.Id != 0 && x.Implemented).OrderByDescending(x => x.Uses))
                if (ImGui.Selectable($"{entry.Id} {entry.Short("SPELL_EFFECT_")}  ({entry.Uses} uses)", entry.Id == type))
                    rules.Set(SpellRules.Column("effect", e), entry.Id);
            ImGui.EndCombo();
        }
        if (type == 0) { ImGui.PopID(); return; }

        if (legal.AuraEffects.Contains(type))
        {
            int aura = rules.EffectAura(e);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.BeginCombo("Aura", aura == 0 ? "none" : $"{aura} {legal.AuraName(aura)}"))
            {
                foreach (SpellLegality.Entry entry in legal.Auras.Where(x => x.Id != 0 && x.Implemented).OrderByDescending(x => x.Uses))
                    if (ImGui.Selectable($"{entry.Id} {entry.Short("SPELL_AURA_")}  ({entry.Uses} uses)", entry.Id == aura))
                        rules.Set(SpellRules.Column("effectApplyAuraName", e), entry.Id);
                ImGui.EndCombo();
            }
            RulesInt("Tick (ms)", rules, SpellRules.Column("effectAmplitude", e), 0, 60_000, cs);
        }

        foreach ((string column, string label) in new[] { ("effectImplicitTargetA", "Target A"), ("effectImplicitTargetB", "Target B") })
        {
            int target = rules.GetInt(SpellRules.Column(column, e));
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.BeginCombo(label, target == 0 ? "none" : $"{target} {legal.TargetName(target)}"))
            {
                if (ImGui.Selectable("none", target == 0)) rules.Set(SpellRules.Column(column, e), 0);
                foreach (SpellLegality.Entry entry in legal.Targets.Where(x => x.Id != 0).OrderByDescending(x => x.Uses))
                    if (ImGui.Selectable($"{entry.Id} {entry.Short("TARGET_")}  ({entry.Uses} uses)", entry.Id == target))
                        rules.Set(SpellRules.Column(column, e), entry.Id);
                ImGui.EndCombo();
            }
        }
        RulesIndexCombo("Radius", rules, SpellRules.Column("effectRadiusIndex", e),
            new uint[] { 0 }.Concat(tables.Radii.Keys.OrderBy(k => tables.Radii[k].Yards)), tables.RadiusText, cs);

        (int min, int max) = rules.Points(e);
        int[] pts = { min, max };
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.InputInt2("Points min..max", ref pts[0])) rules.SetPoints(e, pts[0], pts[1]);
        CreatorHelp("The number the effect works with: damage, healing, the aura's amount per tick. The server rolls " +
                    "min..max (base points + 1..die sides).");
        RulesInt("Misc value", rules, SpellRules.Column("effectMiscValue", e), int.MinValue, int.MaxValue, cs);
        if (_creatorSpell is { } triggerDoc)
        {
            if (IsTotemEffect(type)) DrawCreatorActorRow(triggerDoc, rules, e, cs);
            else DrawCreatorTriggerRow(triggerDoc, rules, e, cs);
        }
        RulesInt("Chain targets", rules, SpellRules.Column("effectChainTarget", e), 0, 20, cs);
        ImGui.PopID();
    }

    // ── small widgets over one column ───────────────────────────────────────

    private void RulesInt(string label, SpellRules rules, string column, int min, int max, float cs)
    {
        int value = rules.GetInt(column);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.InputInt(label, ref value)) rules.Set(column, Math.Clamp(value, min, max));
        if (rules.Changed(column)) { ImGui.SameLine(); if (ImGui.SmallButton($"reset##{column}")) rules.Reset(column); }
    }

    private void RulesCombo(string label, SpellRules rules, string column, string[] names, float cs)
    {
        int value = Math.Clamp(rules.GetInt(column), 0, names.Length - 1);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.Combo(label, ref value, names, names.Length)) rules.Set(column, value);
    }

    private readonly Dictionary<string, float> _rulesCustomValue = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Point an index column at a value: an existing exact row, else a new local row (D5).</summary>
    private static uint SetRulesIndexValue(SpellRules rules, SpellRuleTables tables, string column, double value)
    {
        string table = SpellRules.IndexTableOf(column)!;
        uint row = tables.ExactRow(table, value);
        if (row == 0 || SpellRuleTables.IsLocalRow(row))
        {
            row = rules.AddIndexRow(table, value);
            tables.AddLocalRow(table, row, value);
        }
        rules.Set(column, row);
        return row;
    }

    private void RulesIndexCombo(string label, SpellRules rules, string column, IEnumerable<uint> rows,
        Func<uint, string> text, float cs)
    {
        uint current = (uint)rules.GetInt(column);
        static string Row(uint id) => SpellRuleTables.IsLocalRow(id) ? "new row" : $"row {id}";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo(label, $"{text(current)}  [{Row(current)}]"))
        {
            foreach (uint row in rows)
                if (ImGui.Selectable($"{text(row)}  [{Row(row)}]##{row}", row == current)) rules.Set(column, row);
            ImGui.EndCombo();
        }
        if (rules.Changed(column)) { ImGui.SameLine(); if (ImGui.SmallButton($"reset##{column}")) rules.Reset(column); }
        // D5: any value - an existing row when one says it exactly, else a new row the Completer adds.
        if (SpellRules.IndexTableOf(column) is { } table)
        {
            bool time = table is "SpellCastTimes" or "SpellDuration";
            ImGui.SameLine();
            if (!_rulesCustomValue.TryGetValue(column, out float custom)) custom = 0f;
            ImGui.SetNextItemWidth(70f * cs);
            ImGui.InputFloat($"##custom{column}", ref custom, 0f, 0f, time ? "%.2f s" : "%.1f yd");
            _rulesCustomValue[column] = custom;
            ImGui.SameLine();
            if (ImGui.SmallButton($"use##{column}") && custom >= 0f && SpellRuleTablesLoaded is { } tables)
                SetRulesIndexValue(rules, tables, column, time ? custom * 1000.0 : custom);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Any value: the game's own row when one says exactly this, otherwise a NEW row " +
                                 "that the build adds to the game's tables (both the server's and the client's).");
        }
    }
}
