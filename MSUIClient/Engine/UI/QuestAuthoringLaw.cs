using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Quest Creator law (shared_docs/WORLD_BUILDER.md §5, verifier C3/C4/C8): a quest DRAFT becomes the
/// exact World Content Pack docs that make it work in game - not just the quest row. Pure; the panel,
/// the script driver and interface-wire-check all go through <see cref="Items"/>.
///
///   quest_template            every field the draft sets (objectives 1-4, 6 choice + 4 fixed rewards,
///                             reputation, chain, exploration flag)
///   creature_questrelation    giver     /  creature_involvedrelation   turn-in
///   creature_loot_template    a Collect objective's drop source: quest-only (negative chance) on the
///                             source creature's loot_id; a pack creature without one gets loot_id = its entry
///   AreaTrigger.dbc + areatrigger_template + areatrigger_involvedrelation   an Explore objective
///   creature_template (+ gossip_menu_option)   a PACK giver/ender that is not flagged quest giver gets the
///                             flag, and - with its own gossip menu - the QUESTGIVER option that lists quests
/// Stock creatures cannot be changed by a pack: they must already be quest givers / drop sources.
/// </summary>
public static class QuestAuthoringLaw
{
    public enum ObjectiveKind { None, Kill, Collect, UseObject, Explore }

    public sealed class Objective
    {
        public ObjectiveKind Kind { get; set; }
        public uint Target { get; set; }          // Kill: creature entry; UseObject: gameobject entry
        public int Count { get; set; } = 1;
        public uint Item { get; set; }            // Collect: the item
        public uint DropFrom { get; set; }        // Collect: creature that drops it (0 = obtained elsewhere)
        public float DropChance { get; set; } = 35f;
        public int ExploreMap { get; set; }       // Explore: where the trigger sits
        public float ExploreX { get; set; }
        public float ExploreY { get; set; }
        public float ExploreZ { get; set; }
        public float ExploreRadius { get; set; } = 10f;
        public string Text { get; set; } = "";    // optional ObjectiveTextN (quest log line)
    }

    public sealed class Draft
    {
        public uint Entry { get; set; }
        public string Title { get; set; } = "";
        public string Details { get; set; } = "";
        public string Objectives { get; set; } = "";
        public string RequestText { get; set; } = "";
        public string CompleteText { get; set; } = "";
        public int Level { get; set; } = 30;
        public int MinLevel { get; set; } = 25;
        public int Zone { get; set; }
        public uint Giver { get; set; }
        public uint Ender { get; set; }
        public Objective[] Goals { get; set; } = { new(), new(), new(), new() };
        public (uint Item, int Count)[] Choices { get; set; } = new (uint, int)[6];
        public (uint Item, int Count)[] Fixed { get; set; } = new (uint, int)[4];
        public int Xp { get; set; } = 2000;
        public int Money { get; set; } = 5000;
        public int RepFaction { get; set; }
        public int RepValue { get; set; }
        public int PrevQuest { get; set; }        // > 0: must be completed first; < 0: must be active
        public int NextInChain { get; set; }
        public int ExclusiveGroup { get; set; }
    }

    /// <summary>What the pack already has, so the law can extend instead of duplicate.</summary>
    public sealed class Context
    {
        /// <summary>Pack creature_template rows by entry (stock creatures are absent).</summary>
        public Dictionary<uint, JsonObject> PackTemplates { get; init; } = new();
        /// <summary>Pack gossip_menu_option rows by menu id.</summary>
        public Dictionary<long, List<JsonObject>> MenuOptions { get; init; } = new();
        /// <summary>Next free AreaTrigger id in the pack range (7000+).</summary>
        public uint NextTriggerId { get; set; } = 7100;
        /// <summary>Exploration trigger a saved quest already owns (edits reuse it, never pile up new ones).</summary>
        public Dictionary<uint, uint> ExploreTriggerOf { get; init; } = new();
    }

    public const uint NpcQuestGiver = 0x2;
    public const int SpecialFlagExploration = 2;

    /// <summary>Problems that make the draft unsaveable (the panel disables Save and says why).</summary>
    public static List<string> Problems(Draft d, Context ctx)
    {
        var p = new List<string>();
        if (d.Title.Trim().Length == 0) p.Add("the quest needs a title");
        if (d.Giver == 0) p.Add("pick who gives the quest");
        if (d.Goals.All(g => g.Kind == ObjectiveKind.None)) p.Add("add at least one objective");
        if (d.MinLevel > d.Level) p.Add("min level is above the quest level");
        foreach (var (g, i) in d.Goals.Select((g, i) => (g, i + 1)))
        {
            switch (g.Kind)
            {
                case ObjectiveKind.Kill or ObjectiveKind.UseObject when g.Target == 0:
                    p.Add($"objective {i}: pick the target"); break;
                case ObjectiveKind.Collect when g.Item == 0:
                    p.Add($"objective {i}: pick the item"); break;
                case ObjectiveKind.Collect when g.DropFrom != 0 && !ctx.PackTemplates.ContainsKey(g.DropFrom):
                    p.Add($"objective {i}: the drop source must be a creature of this pack (stock loot cannot be changed)"); break;
                case ObjectiveKind.Explore when g.ExploreRadius <= 0:
                    p.Add($"objective {i}: explore radius must be positive"); break;
            }
            if (g.Kind is ObjectiveKind.Kill or ObjectiveKind.Collect or ObjectiveKind.UseObject && g.Count <= 0)
                p.Add($"objective {i}: count must be at least 1");
        }
        if (d.Goals.Count(g => g.Kind == ObjectiveKind.Explore) > 1) p.Add("one exploration objective per quest (vanilla has a single area-trigger slot)");
        return p;
    }

    /// <summary>The content items (JSON array for /WorldPacks/Content) that make the draft a working quest.</summary>
    public static JsonArray Items(Draft d, Context ctx)
    {
        var items = new JsonArray();
        JsonObject Row(string table, JsonObject body) => new() { ["kind"] = "dbrow:" + table, ["body"] = body };

        var q = new JsonObject
        {
            ["entry"] = d.Entry, ["patch"] = 0, ["Method"] = 2, ["ZoneOrSort"] = d.Zone, ["MinLevel"] = d.MinLevel, ["MaxLevel"] = 0,
            ["QuestLevel"] = d.Level, ["Type"] = 0, ["Title"] = d.Title.Trim(), ["Details"] = d.Details.Trim(),
            ["Objectives"] = d.Objectives.Trim(), ["OfferRewardText"] = d.CompleteText.Trim(),
            ["RequestItemsText"] = d.RequestText.Trim(), ["EndText"] = "",
            ["RewXP"] = d.Xp, ["RewOrReqMoney"] = d.Money, ["RewMoneyMaxLevel"] = d.Money,
            ["PrevQuestId"] = d.PrevQuest, ["NextQuestInChain"] = d.NextInChain, ["ExclusiveGroup"] = d.ExclusiveGroup,
            ["RewRepFaction1"] = d.RepFaction, ["RewRepValue1"] = d.RepFaction != 0 ? d.RepValue : 0,
            ["SpecialFlags"] = d.Goals.Any(g => g.Kind == ObjectiveKind.Explore) ? SpecialFlagExploration : 0,
        };
        // Creature/GO slots and item slots are counted separately (vanilla: 4 of each).
        int unitSlot = 0, itemSlot = 0;
        var touchedTemplates = new Dictionary<uint, JsonObject>();
        foreach (var (g, i) in d.Goals.Select((g, i) => (g, i + 1)))
        {
            if (g.Text.Trim().Length > 0) q[$"ObjectiveText{i}"] = g.Text.Trim();
            switch (g.Kind)
            {
                case ObjectiveKind.Kill:
                    unitSlot++;
                    q[$"ReqCreatureOrGOId{unitSlot}"] = g.Target; q[$"ReqCreatureOrGOCount{unitSlot}"] = g.Count;
                    break;
                case ObjectiveKind.UseObject:
                    unitSlot++;
                    q[$"ReqCreatureOrGOId{unitSlot}"] = -(long)g.Target; q[$"ReqCreatureOrGOCount{unitSlot}"] = g.Count;
                    break;
                case ObjectiveKind.Collect:
                    itemSlot++;
                    q[$"ReqItemId{itemSlot}"] = g.Item; q[$"ReqItemCount{itemSlot}"] = g.Count;
                    if (g.DropFrom != 0 && ctx.PackTemplates.TryGetValue(g.DropFrom, out var src))
                    {
                        var tpl = touchedTemplates.GetValueOrDefault(g.DropFrom) ?? (JsonObject)src.DeepClone();
                        long lootId = Num(tpl["loot_id"]);
                        if (lootId == 0) { lootId = g.DropFrom; tpl["loot_id"] = g.DropFrom; touchedTemplates[g.DropFrom] = tpl; }
                        // Negative chance = quest-only: it drops only for players who need it.
                        items.Add(Row("creature_loot_template", new JsonObject
                        {
                            ["entry"] = lootId, ["item"] = g.Item, ["ChanceOrQuestChance"] = -MathF.Abs(g.DropChance),
                            ["groupid"] = 0, ["mincountOrRef"] = 1, ["maxcount"] = 1, ["condition_id"] = 0, ["patch_min"] = 0, ["patch_max"] = 10,
                        }));
                    }
                    break;
                case ObjectiveKind.Explore:
                {
                    uint trig = ctx.ExploreTriggerOf.TryGetValue(d.Entry, out var owned) ? owned : ctx.NextTriggerId++;
                    items.Add(new JsonObject
                    {
                        ["kind"] = "dbc:AreaTrigger", ["key"] = trig.ToString(CultureInfo.InvariantCulture),
                        ["body"] = new JsonObject
                        {
                            // Field 0 is the id - the build takes it from the doc key. Box fields 6-9 stay 0.
                            ["fields"] = new JsonObject
                            {
                                ["1"] = g.ExploreMap, ["2"] = new JsonObject { ["f"] = g.ExploreX },
                                ["3"] = new JsonObject { ["f"] = g.ExploreY }, ["4"] = new JsonObject { ["f"] = g.ExploreZ },
                                ["5"] = new JsonObject { ["f"] = g.ExploreRadius },
                            },
                        },
                    });
                    items.Add(Row("areatrigger_template", new JsonObject
                    {
                        ["id"] = trig, ["build"] = 5875, ["name"] = $"{d.Title.Trim()} (explore)", ["map_id"] = g.ExploreMap,
                        ["x"] = g.ExploreX, ["y"] = g.ExploreY, ["z"] = g.ExploreZ, ["radius"] = g.ExploreRadius,
                        ["box_x"] = 0, ["box_y"] = 0, ["box_z"] = 0, ["box_orientation"] = 0,
                    }));
                    items.Add(Row("areatrigger_involvedrelation", new JsonObject { ["id"] = trig, ["quest"] = d.Entry }));
                    break;
                }
            }
        }
        for (int i = 0; i < 6; i++)
            if (i < d.Choices.Length && d.Choices[i].Item != 0)
            { q[$"RewChoiceItemId{i + 1}"] = d.Choices[i].Item; q[$"RewChoiceItemCount{i + 1}"] = Math.Max(1, d.Choices[i].Count); }
        for (int i = 0; i < 4; i++)
            if (i < d.Fixed.Length && d.Fixed[i].Item != 0)
            { q[$"RewItemId{i + 1}"] = d.Fixed[i].Item; q[$"RewItemCount{i + 1}"] = Math.Max(1, d.Fixed[i].Count); }
        items.Insert(0, Row("quest_template", q));
        // An edit that dropped the exploration objective removes the trigger the quest owned.
        if (!d.Goals.Any(g => g.Kind == ObjectiveKind.Explore) && ctx.ExploreTriggerOf.TryGetValue(d.Entry, out var stale))
        {
            string k = stale.ToString(CultureInfo.InvariantCulture);
            items.Add(new JsonObject { ["kind"] = "dbc:AreaTrigger", ["key"] = k, ["body"] = null });
            items.Add(new JsonObject { ["kind"] = "dbrow:areatrigger_template", ["key"] = $"{k}|5875", ["body"] = null });
            items.Add(new JsonObject { ["kind"] = "dbrow:areatrigger_involvedrelation", ["key"] = $"{k}|{d.Entry}", ["body"] = null });
        }

        uint ender = d.Ender != 0 ? d.Ender : d.Giver;
        items.Add(Row("creature_questrelation", new JsonObject { ["id"] = d.Giver, ["quest"] = d.Entry, ["patch_min"] = 0, ["patch_max"] = 10 }));
        items.Add(Row("creature_involvedrelation", new JsonObject { ["id"] = ender, ["quest"] = d.Entry, ["patch_min"] = 0, ["patch_max"] = 10 }));

        // Pack givers/enders must be reachable as quest givers (C3 flag, C4 menu option).
        foreach (uint npc in new[] { d.Giver, ender }.Distinct())
        {
            if (!ctx.PackTemplates.TryGetValue(npc, out var src)) continue;
            var tpl = touchedTemplates.GetValueOrDefault(npc) ?? (JsonObject)src.DeepClone();
            uint flags = (uint)Num(tpl["npc_flags"]);
            if ((flags & NpcQuestGiver) == 0) { tpl["npc_flags"] = flags | NpcQuestGiver; touchedTemplates[npc] = tpl; }
            long menu = Num(tpl["gossip_menu_id"]);
            if (menu != 0)
            {
                var options = ctx.MenuOptions.GetValueOrDefault(menu) ?? new();
                if (!options.Any(o => (Num(o["npc_option_npcflag"]) & NpcQuestGiver) != 0))
                {
                    long id = options.Count == 0 ? 0 : options.Max(o => Num(o["id"])) + 1;
                    var opt = new JsonObject
                    {
                        ["menu_id"] = menu, ["id"] = id, ["option_icon"] = 0, ["option_text"] = "GOSSIP_OPTION_QUESTGIVER",
                        ["option_broadcast_text"] = 0, ["option_id"] = 2, ["npc_option_npcflag"] = NpcQuestGiver,
                        ["action_menu_id"] = 0, ["action_poi_id"] = 0, ["action_script_id"] = 0, ["box_coded"] = 0,
                        ["box_money"] = 0, ["box_text"] = "", ["box_broadcast_text"] = 0, ["condition_id"] = 0,
                    };
                    items.Add(Row("gossip_menu_option", opt));
                    options.Add(opt);
                    ctx.MenuOptions[menu] = options;
                }
            }
        }
        foreach (var tpl in touchedTemplates.Values) items.Add(Row("creature_template", tpl));
        return items;
    }

    /// <summary>Quest text as the player reads it: $N name, $C class, $R race, $B line break, $G male:female;.</summary>
    public static string Preview(string text, string name = "Testwar", string cls = "Warrior", string race = "Human", bool female = false)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '$' || i + 1 >= text.Length) { sb.Append(c); continue; }
            char k = char.ToUpperInvariant(text[i + 1]);
            switch (k)
            {
                case 'N': sb.Append(name); i++; break;
                case 'C': sb.Append(cls); i++; break;
                case 'R': sb.Append(race); i++; break;
                case 'B': sb.Append('\n'); i++; break;
                case 'G':
                {
                    int colon = text.IndexOf(':', i), semi = text.IndexOf(';', i);
                    if (colon < 0 || semi < colon) { sb.Append(c); break; }
                    sb.Append(female ? text[(colon + 1)..semi] : text[(i + 2)..colon].Trim());
                    i = semi;
                    break;
                }
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static long Num(JsonNode? n) => n is JsonValue v &&
        long.TryParse(v.TryGetValue<string>(out var s) ? s : v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
}
