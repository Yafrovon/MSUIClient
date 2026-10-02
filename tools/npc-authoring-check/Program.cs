using System.Text.Json.Nodes;
using MSUIClient.Engine.UI;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
JsonObject Doc(string kind, JsonObject row) => new() { ["kind"] = "dbrow:" + kind, ["body"] = row };
var source = new JsonArray
{
    Doc("creature_template", new() { ["entry"] = 123, ["patch"] = 10, ["name"] = "Town merchant", ["vendor_id"] = 10,
        ["trainer_id"] = 11, ["gossip_menu_id"] = 12, ["equipment_id"] = 13, ["loot_id"] = 14, ["script_name"] = "custom_greeting", ["health_multiplier"] = 2.7 }),
    Doc("npc_vendor", new() { ["entry"] = 123, ["item"] = 456, ["maxcount"] = 3, ["incrtime"] = 120, ["condition_id"] = 17 }),
    Doc("npc_trainer", new() { ["entry"] = 123, ["spell"] = 789, ["spellcost"] = 99 }),
    Doc("creature_questrelation", new() { ["id"] = 123, ["quest"] = 90 }),
    Doc("gossip_menu", new() { ["entry"] = 12, ["text_id"] = 91 }),
    Doc("creature_loot_template", new() { ["entry"] = 14, ["item"] = 55, ["ChanceOrQuestChance"] = -25, ["mincountOrRef"] = -90, ["groupid"] = 2 }),
};
string untouched = source.ToJsonString();
var cloned = NpcAuthoringLaw.CloneForSpawn(source, 123, 7_000_010, 42);
Check(source.ToJsonString() == untouched, "Import mutated the original world data.");
var template = cloned[0]!["body"]!;
Check((uint)template["entry"]! == 7_000_010 && (int)template["patch"]! == 0, "Template identity was not reserved.");
Check((int)template["vendor_id"]! == 0 && (int)template["trainer_id"]! == 0, "Inherited services would still be shared.");
Check((int)template["gossip_menu_id"]! == 12 && (int)template["equipment_id"]! == 13 && (string?)template["script_name"] == "custom_greeting", "Unedited stock references were lost.");
Check(cloned.OfType<JsonObject>().Single(d => d["kind"]!.ToString() == "dbrow:creature_questrelation")["body"]!["quest"]!.ToString() == "90", "Existing quest link changed.");
var replacement = cloned.OfType<JsonObject>().Single(d => d["kind"]!.ToString() == "npc-replacement")["body"]!;
Check((uint)replacement["spawnGuid"]! == 42 && (uint)replacement["originalEntry"]! == 123, "Import was not scoped to the selected spawn.");
Check(!cloned.OfType<JsonObject>().Any(d => d["kind"]!.ToString() == "dbrow:gossip_menu"), "Import tried to overwrite a stock menu.");
var loot = cloned.OfType<JsonObject>().Single(d => d["kind"]!.ToString() == "dbrow:creature_loot_template")["body"]!;
Check((uint)template["loot_id"]! == 7_000_010 && (uint)loot["entry"]! == 7_000_010, "Imported loot still shares stock ownership.");
Check((int)loot["mincountOrRef"]! == -90 && (int)loot["groupid"]! == 2 && (int)loot["ChanceOrQuestChance"]! == -25, "Loot reference, group or quest chance changed.");

var owned = new JsonArray
{
    new JsonObject { ["kind"] = "dbrow:npc_vendor", ["docKey"] = "7000010|456", ["body"] = new JsonObject { ["entry"] = 7_000_010, ["item"] = 456, ["maxcount"] = 3 } },
    Doc("npc_vendor", new() { ["entry"] = 7_000_010, ["item"] = 457, ["maxcount"] = 0 }),
    Doc("broadcast_text", new() { ["entry"] = 7_000_050, ["male_text"] = "Welcome, traveller." }),
};
var changes = new JsonArray();
NpcAuthoringLaw.ReplaceRows(changes, owned.OfType<JsonObject>(), "dbrow:npc_vendor",
    new[] { new JsonObject { ["entry"] = 7_000_010, ["item"] = 457, ["maxcount"] = 2, ["incrtime"] = 60 } }, "entry", "item");
Check(changes.OfType<JsonObject>().Any(d => d["key"]?.ToString() == "7000010|456" && d["body"] is null), "Removed shop item did not produce a deletion.");
var applied = NpcAuthoringLaw.ApplyChanges(owned, changes);
Check(applied.OfType<JsonObject>().Count(d => d["kind"]!.ToString() == "dbrow:npc_vendor") == 1, "Deleted shop item survived the form reload.");
Check(applied.OfType<JsonObject>().Any(d => d["body"]?["male_text"]?.ToString() == "Welcome, traveller."), "Saving inventory erased an unedited greeting.");
Check(applied.OfType<JsonObject>().Single(d => d["kind"]!.ToString() == "dbrow:npc_vendor")["body"]!["maxcount"]!.ToString() == "2", "Stock edit failed to round-trip.");
Check(NpcAuthoringLaw.MergeRoles(7, 5, 5) == 7, "Renaming a vendor erased a quest-giver role added on another page.");
Check(NpcAuthoringLaw.MergeRoles(7, 5, 1) == 3, "Disabling vendor also disabled a concurrently added quest-giver role.");
var menuOptions = new[]
{
    new JsonObject { ["menu_id"] = 12, ["id"] = 1, ["option_id"] = 1, ["npc_option_npcflag"] = 1, ["action_menu_id"] = 12,
        ["action_script_id"] = 99, ["action_poi_id"] = 33, ["box_money"] = 250, ["condition_id"] = 17 },
    new JsonObject { ["menu_id"] = 12, ["id"] = 2, ["option_id"] = 3, ["npc_option_npcflag"] = 4 },
    new JsonObject { ["menu_id"] = 12, ["id"] = 3, ["option_id"] = 5, ["npc_option_npcflag"] = 16 },
};
var menuCopy = NpcAuthoringLaw.CloneMenuOptions(menuOptions, 12, 62001, 5, new[] { (4u, 3), (16u, 5) });
Check(menuCopy.Count == 2 && (int)menuCopy[0]["action_script_id"]! == 99 && (int)menuCopy[0]["box_money"]! == 250 &&
    (int)menuCopy[0]["condition_id"]! == 17 && (int)menuCopy[0]["action_poi_id"]! == 33, "Editing roles destroyed custom gossip behavior.");
Check((uint)menuCopy[0]["action_menu_id"]! == 62001 && (int)menuOptions[0]["action_menu_id"]! == 12, "Menu self-link was not safely cloned.");
var latest = new JsonObject { ["name"] = "A long source name beyond the displayed input length", ["faction"] = 84, ["health_multiplier"] = 3.2, ["level_max"] = 60 };
var controls = new JsonObject { ["name"] = "A long source name", ["faction"] = 35, ["level_max"] = 30 };
NpcAuthoringLaw.ApplyEditedFields(latest, controls, controls.DeepClone().AsObject());
Check((int)latest["faction"]! == 84 && (int)latest["level_max"]! == 60 && latest["name"]!.ToString().Contains("beyond"), "Untouched controls overwrote newer or undisplayed source fields.");
var edited = controls.DeepClone().AsObject(); edited["name"] = "New vendor name";
NpcAuthoringLaw.ApplyEditedFields(latest, controls, edited);
Check(latest["name"]!.ToString() == "New vendor name" && (double)latest["health_multiplier"]! == 3.2, "A rename lost unrelated template data.");
Console.WriteLine($"NPC authoring: {checks} checks PASS");
