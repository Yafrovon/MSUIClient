using System.Text.Json.Nodes;

namespace MSUIClient.Engine.UI;

/// <summary>Copy an existing spawn into an optional pack without changing shared stock records.</summary>
public static class NpcAuthoringLaw
{
    /// <summary>Do not overwrite fields changed elsewhere, or normalize/truncate untouched values through UI controls.</summary>
    public static void ApplyEditedFields(JsonObject target, JsonObject loadedControls, JsonObject editedControls)
    {
        foreach (var (field, value) in editedControls)
            if (!JsonNode.DeepEquals(value, loadedControls[field])) target[field] = value?.DeepClone();
    }

    public static uint MergeRoles(uint current, uint loaded, uint edited)
    {
        uint changed = loaded ^ edited;
        return (current & ~changed) | (edited & changed);
    }

    public static List<JsonObject> CloneMenuOptions(IEnumerable<JsonObject> source, uint originalMenu, uint newMenu,
        uint roles, IEnumerable<(uint Flag, int Option)> services)
    {
        static uint N(JsonNode? n) => uint.TryParse(n?.ToString(), out var value) ? value : 0;
        var result = new List<JsonObject>();
        foreach (var option in source.Where(o => N(o["menu_id"]) == originalMenu))
        {
            if (services.Any(s => N(option["option_id"]) == s.Option && (N(option["npc_option_npcflag"]) & s.Flag) != 0 && (roles & s.Flag) == 0)) continue;
            var copy = option.DeepClone().AsObject();
            copy["menu_id"] = newMenu;
            if (originalMenu != 0 && N(copy["action_menu_id"]) == originalMenu) copy["action_menu_id"] = newMenu;
            result.Add(copy);
        }
        return result;
    }

    public static JsonArray CloneForSpawn(JsonArray source, uint originalEntry, uint replacementEntry, uint spawnGuid)
    {
        if (originalEntry == 0 || replacementEntry < 7_000_000 || spawnGuid == 0)
            throw new ArgumentException("A stock spawn and a reserved replacement entry are required.");
        var sourceTemplate = source.OfType<JsonObject>().FirstOrDefault(d => d["kind"]?.ToString() == "dbrow:creature_template" && d["body"]?["entry"]?.ToString() == originalEntry.ToString())?["body"];
        string? lootId = sourceTemplate?["loot_id"]?.ToString();
        var result = new JsonArray();
        foreach (var doc in source.OfType<JsonObject>())
        {
            string kind = doc["kind"]?.ToString() ?? "";
            string? owner = kind switch
            {
                "dbrow:creature_template" or "dbrow:npc_vendor" or "dbrow:npc_trainer" or "dbrow:creature_loot_template" => "entry",
                "dbrow:creature_questrelation" or "dbrow:creature_involvedrelation" => "id",
                _ => null,
            };
            if (owner is null || doc["body"] is not JsonObject original) continue;
            if (original[owner]?.ToString() != (kind == "dbrow:creature_loot_template" ? lootId : originalEntry.ToString())) continue;
            var row = (JsonObject)original.DeepClone();
            row[owner] = replacementEntry;
            if (kind == "dbrow:creature_template")
            {
                // Effective shared vendor/trainer lists were expanded by the read endpoint.
                row["patch"] = 0;
                row["vendor_id"] = 0;
                row["trainer_id"] = 0;
                if (lootId is not null and not "0") row["loot_id"] = replacementEntry;
            }
            result.Add(new JsonObject { ["kind"] = kind, ["body"] = row });
        }
        if (!result.OfType<JsonObject>().Any(d => d["kind"]?.ToString() == "dbrow:creature_template"))
            throw new ArgumentException("The source NPC template is missing.");
        result.Add(new JsonObject
        {
            ["kind"] = "npc-replacement", ["key"] = spawnGuid.ToString(),
            ["body"] = new JsonObject { ["spawnGuid"] = spawnGuid, ["originalEntry"] = originalEntry, ["replacementEntry"] = replacementEntry },
        });
        return result;
    }

    /// <summary>Keep the loaded form consistent until its asynchronous document refresh arrives.</summary>
    public static JsonArray ApplyChanges(JsonArray source, JsonArray changes)
    {
        string Key(JsonObject doc)
        {
            string kind = doc["kind"]?.ToString() ?? "";
            if (doc["key"] is not null) return kind + ":" + doc["key"];
            if (doc["docKey"] is not null) return kind + ":" + doc["docKey"];
            string[] columns = kind switch
            {
                "dbrow:creature_template" => new[] { "entry", "patch" },
                "dbrow:npc_vendor" => new[] { "entry", "item" },
                "dbrow:creature_loot_template" => new[] { "entry", "item" },
                "dbrow:npc_trainer" => new[] { "entry", "spell" },
                "dbrow:creature_equip_template" => new[] { "entry", "item1", "item2", "item3" },
                "dbrow:creature_questrelation" or "dbrow:creature_involvedrelation" => new[] { "id", "quest" },
                "dbrow:gossip_menu" => new[] { "entry", "text_id" },
                "dbrow:gossip_menu_option" => new[] { "menu_id", "id" },
                "dbrow:npc_text" => new[] { "ID" }, "dbrow:creature" => new[] { "guid" },
                "npc-replacement" => new[] { "spawnGuid" }, _ => new[] { "entry" },
            };
            return kind + ":" + string.Join('|', columns.Select(k => doc["body"]?[k]?.ToString() ?? "0"));
        }
        var docs = source.OfType<JsonObject>().GroupBy(Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (var doc in changes.OfType<JsonObject>())
            if (doc["body"] is null) docs.Remove(Key(doc)); else docs[Key(doc)] = doc;
        return new JsonArray(docs.Values.Select(d => d.DeepClone()).ToArray());
    }

    /// <summary>Diff one owned row family, including deletions: removing a vendor item must remove its document.</summary>
    public static void ReplaceRows(JsonArray output, IEnumerable<JsonObject> previous, string kind,
        IEnumerable<JsonObject> desired, params string[] keyColumns)
    {
        string Key(JsonObject row) => string.Join('|', keyColumns.Select(k => row[k]?.ToString() ?? "0"));
        var next = desired.ToDictionary(Key);
        foreach (var doc in previous.Where(d => d["kind"]?.ToString() == kind && d["body"] is JsonObject))
        {
            string key = Key(doc["body"]!.AsObject());
            if (!next.ContainsKey(key)) output.Add(new JsonObject { ["kind"] = kind, ["key"] = doc["docKey"]?.ToString() ?? key, ["body"] = null });
        }
        foreach (var row in next.Values) output.Add(new JsonObject { ["kind"] = kind, ["body"] = row.DeepClone() });
    }
}
