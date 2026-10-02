using System.Text.Json.Nodes;
using MSUIClient.Engine.UI;

/// <summary>Quest editing must preserve the existing playable quest, including hidden dependencies.</summary>
internal static class WorldBuilderQuestAuthoringChecks
{
    public static void Run()
    {
        var row = new JsonObject
        {
            ["entry"] = 7000800, ["Title"] = "Follow the tracks", ["QuestLevel"] = 30, ["MinLevel"] = 25,
            ["Details"] = "Find the camp.", ["SpecialFlags"] = 10, ["RequiredCondition"] = 47,
            ["MaxLevel"] = 45, ["Type"] = 1, ["EndText"] = "A hidden place", ["RewMoneyMaxLevel"] = 9876,
            ["RewChoiceItemId1"] = 123, ["RewChoiceItemCount1"] = 3,
            ["ReqItemId1"] = 456, ["ReqItemCount1"] = 6,
            ["RewRepFaction2"] = 72, ["RewRepValue2"] = 75,
        };
        for (int i = 1; i <= 4; i++)
        { row[$"ReqCreatureOrGOId{i}"] = 7000100 + i; row[$"ReqCreatureOrGOCount{i}"] = i; row[$"ObjectiveText{i}"] = "Target " + i; }
        var ctx = new QuestAuthoringLaw.Context();
        ctx.Quests[7000800] = row;
        ctx.GiverRelations.Add(new() { ["id"] = 7000101, ["quest"] = 7000800 });
        ctx.GiverRelations.Add(new() { ["id"] = 7000104, ["quest"] = 7000800 });
        ctx.EnderRelations.Add(new() { ["id"] = 7000102, ["quest"] = 7000800 });
        ctx.GiverRelations.Add(new() { ["id"] = 7000101, ["quest"] = 7000801 });
        ctx.PackTemplates[7000103] = new() { ["entry"] = 7000103, ["loot_id"] = 7000103 };
        ctx.LootRows.Add(new() { ["entry"] = 7000103, ["item"] = 456, ["ChanceOrQuestChance"] = -37.5f });
        ctx.ExploreTriggerOf[7000800] = 7111;
        ctx.ExploreTriggers[7111] = new() { ["id"] = 7111, ["map_id"] = 0, ["x"] = -1234.5f, ["y"] = 1600.25f, ["z"] = 51.75f, ["radius"] = 12.5f };

        var draft = QuestAuthoringLaw.Read(row, ctx);
        Check(draft.Goals.Count(g => g.Kind == QuestAuthoringLaw.ObjectiveKind.Kill) == 4 &&
            draft.Goals.Any(g => g.Kind == QuestAuthoringLaw.ObjectiveKind.Collect && g.DropChance == 37.5f && g.Count == 6),
            "reopening four kill objectives must not truncate the separate item objective or its fractional drop chance");
        var explore = draft.Goals.Single(g => g.Kind == QuestAuthoringLaw.ObjectiveKind.Explore);
        Check(explore.ExploreX == -1234.5f && explore.ExploreY == 1600.25f && explore.ExploreRadius == 12.5f,
            "reopening an exploration quest preserves its location and radius");
        Check(draft.Choices[0] == (123u, 3), "reopening keeps reward quantities");
        var items = QuestAuthoringLaw.Items(draft, ctx).OfType<JsonObject>().ToList();
        var saved = items.Single(i => (string?)i["kind"] == "dbrow:quest_template")["body"]!.AsObject();
        Check((int)saved["RequiredCondition"]! == 47 && (int)saved["RewRepValue2"]! == 75 && (long)saved["SpecialFlags"]! == 10,
            "editing supported fields keeps unrelated conditions, secondary reputation and special flags");
        Check((int)saved["MaxLevel"]! == 45 && (int)saved["Type"]! == 1 && (string?)saved["EndText"] == "A hidden place" && (int)saved["RewMoneyMaxLevel"]! == 9876,
            "ordinary edits preserve advanced level, category, end text and maximum-level money");
        Check(!items.Any(i => (string?)i["kind"] == "dbrow:creature_questrelation" && i["body"] is null),
            "reopening a quest with several givers does not silently remove the extra givers");
        Check((string?)saved["ObjectiveText4"] == "Target 4" && (int)saved["ReqItemCount1"]! == 6,
            "objective text stays aligned with creature slots after reopening a mixed quest");
        Check(items.Any(i => (string?)i["kind"] == "dbc:AreaTrigger" && (string?)i["key"] == "7111" && i["body"] is not null),
            "saving a reopened exploration quest reuses its trigger");

        draft.Giver = 7000199; draft.Ender = 7000198;
        items = QuestAuthoringLaw.Items(draft, ctx).OfType<JsonObject>().ToList();
        Check(items.Any(i => (string?)i["kind"] == "dbrow:creature_questrelation" && (string?)i["key"] == "7000101|7000800" && i["body"] is null) &&
              items.Any(i => (string?)i["kind"] == "dbrow:creature_involvedrelation" && (string?)i["key"] == "7000102|7000800" && i["body"] is null),
            "moving a quest to other NPCs removes its previous giver and return relations");
        Check(!items.Any(i => (string?)i["key"] == "7000101|7000801"), "editing one quest never removes another quest's giver relation");

        draft.Goals = draft.Goals.Where(g => g.Kind != QuestAuthoringLaw.ObjectiveKind.Explore).ToArray();
        draft.Choices[0] = (0, 0);
        items = QuestAuthoringLaw.Items(draft, ctx).OfType<JsonObject>().ToList();
        saved = items.Single(i => (string?)i["kind"] == "dbrow:quest_template")["body"]!.AsObject();
        Check((long)saved["SpecialFlags"]! == 8 && (int)saved["RewChoiceItemId1"]! == 0,
            "removing an objective or reward clears its old fields without removing unrelated flags");
        Check(items.Any(i => (string?)i["kind"] == "dbc:AreaTrigger" && (string?)i["key"] == "7111" && i["body"] is null),
            "deliberately removing exploration removes its former trigger");
        draft.PrevQuest = (int)draft.Entry;
        Check(QuestAuthoringLaw.Problems(draft, ctx).Any(p => p.Contains("require itself")), "self prerequisites are rejected before save");
        draft.PrevQuest = 0;
        draft.Goals = draft.Goals.Append(new() { Kind = QuestAuthoringLaw.ObjectiveKind.Kill, Target = 7000101 }).ToArray();
        Check(QuestAuthoringLaw.Problems(draft, ctx).Any(p => p.Contains("four creature")), "vanilla objective slot overflow is rejected before a bad row is saved");

        Check(WorldMapAuthoringLaw.StampProblem("Azeroth", 63, 32, 2, 1, 30, 30) is not null &&
              WorldMapAuthoringLaw.StampProblem("Azeroth", 30, 30, 0, 1, 30, 30) is not null &&
              WorldMapAuthoringLaw.StampProblem("Azeroth", 30, 30, 1, 1, 63, 63) is null,
            "terrain selections reject empty and out-of-map rectangles while accepting the last valid tile");
        Check(WorldMapAuthoringLaw.PathProblem("Pass", new[] { new System.Numerics.Vector3(10, 10, 5), new System.Numerics.Vector3(10, 10, 8) }, 12, 18) is not null &&
              WorldMapAuthoringLaw.PathProblem("Pass", new[] { new System.Numerics.Vector3(10, 10, 5), new System.Numerics.Vector3(20, 10, 8) }, 12, 18) is null,
            "a path needs horizontal progress instead of duplicate or vertical-only segments");
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        foreach (int team in new[] { 0, 2, 4 })
        {
            object[] args = { 7800u, 850u, 0u, "The Test Coast", 30, team };
            var dbc = (JsonObject)typeof(MSUIClient.GameLoop).GetMethod("WbAreaDbc", flags)!.Invoke(null, args)!;
            var db = (JsonObject)typeof(MSUIClient.GameLoop).GetMethod("WbAreaRow", flags)!.Invoke(null, args)!;
            Check((int)dbc["body"]!["fields"]!["20"]! == team && (int)db["body"]!["team"]! == team,
                "neutral, Alliance and Horde territory agree between the client and server instead of inheriting the clone's faction");
        }
    }

    private static void Check(bool ok, string problem)
    { if (!ok) throw new InvalidOperationException("Quest authoring: " + problem); }
}
