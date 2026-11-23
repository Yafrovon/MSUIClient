using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.Net;

/// <summary>
/// Read-only client for the web app's Quest Atlas data (shared_docs/QUEST_TABLETOP.md §3): the zone list and the zone
/// bundle (the quest graph in one response). GETs only; the client never queries MySQL. Shapes are the web contract
/// (camelCase; dictionary keys are entry ids as strings) - extend additively, never rename.
/// </summary>
public sealed class QuestAtlasClient
{
    private readonly HttpClient _http = WebAppHttp.Create(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static string U(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/QuestAtlas/" + path;

    public async Task<List<ZoneSummary>> ZonesAsync(string baseUrl)
        => await _http.GetFromJsonAsync<List<ZoneSummary>>(U(baseUrl, "Zones"), Json) ?? new();

    /// <summary>The zone bundle, or null when the web app does not know the area (404).</summary>
    public async Task<ZoneBundle?> ZoneAsync(string baseUrl, uint areaId)
    {
        using var res = await _http.GetAsync(U(baseUrl, $"Zone?areaId={areaId}"));
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ZoneBundle>(Json);
    }

    /// <summary>
    /// Live facts for the quest guardrails (QUEST_TABLETOP.md §13.2): GET /QuestAtlas/Facts. Returns the "facts" object
    /// (creatures/objects/quests/followers/items/holders keyed by id string), or throws with the web app's error.
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonObject> FactsAsync(string baseUrl, IEnumerable<uint> creatures, IEnumerable<uint> objects,
        IEnumerable<uint> quests, IEnumerable<uint> items, IEnumerable<uint> holders, IEnumerable<uint>? texts = null)
    {
        static string L(IEnumerable<uint> ids) => string.Join(",", ids.Where(i => i != 0).Distinct());
        string url = U(baseUrl, $"Facts?creatures={L(creatures)}&objects={L(objects)}&quests={L(quests)}&items={L(items)}&holders={L(holders)}&texts={L(texts ?? Array.Empty<uint>())}");
        using var res = await _http.GetAsync(url);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("this web app has no /QuestAtlas/Facts yet (update it)");
        res.EnsureSuccessStatusCode();
        var root = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync()) as System.Text.Json.Nodes.JsonObject;
        if (root?["success"]?.GetValue<bool>() != true || root["facts"] is not System.Text.Json.Nodes.JsonObject facts)
            throw new InvalidOperationException((string?)root?["error"] ?? "no facts in the reply");
        return facts;
    }

    /// <summary>Spawn guids and places of one entry on a map (GET /QuestAtlas/Spawns): [{ guid, x, y, z, o, movement }].</summary>
    public async Task<System.Text.Json.Nodes.JsonArray> SpawnsAsync(string baseUrl, string table, uint entry, int map)
    {
        using var res = await _http.GetAsync(U(baseUrl, $"Spawns?table={table}&entry={entry}&map={map}"));
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("this web app has no /QuestAtlas/Spawns yet (update it)");
        res.EnsureSuccessStatusCode();
        var root = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync()) as System.Text.Json.Nodes.JsonObject;
        if (root?["success"]?.GetValue<bool>() != true || root["spawns"] is not System.Text.Json.Nodes.JsonArray spawns)
            throw new InvalidOperationException((string?)root?["error"] ?? "no spawns in the reply");
        return spawns;
    }

    /// <summary>A stock quest as the editor loads it (GET /QuestAtlas/StockQuest): row, givers/enders, override possible.</summary>
    public async Task<System.Text.Json.Nodes.JsonObject> StockQuestAsync(string baseUrl, uint entry)
    {
        using var res = await _http.GetAsync(U(baseUrl, $"StockQuest?entry={entry}"));
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("this web app has no /QuestAtlas/StockQuest yet (update it)");
        res.EnsureSuccessStatusCode();
        var root = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync()) as System.Text.Json.Nodes.JsonObject;
        if (root?["success"]?.GetValue<bool>() != true || root["quest"] is not System.Text.Json.Nodes.JsonObject q)
            throw new InvalidOperationException((string?)root?["error"] ?? "no quest in the reply");
        return q;
    }

    // ── §3.1 ────────────────────────────────────────────────────────────────
    public sealed record ZoneSummary(uint AreaId, int MapId, string Name, int Team, int Quests, int MinLevel, int MaxLevel, bool HasMapRect);

    // ── §3.2 ────────────────────────────────────────────────────────────────
    public sealed class ZoneBundle
    {
        public ZoneInfo Zone { get; set; } = new();
        public List<Quest> Quests { get; set; } = new();
        public List<Stub> Stubs { get; set; } = new();
        public Dictionary<string, Npc> Npcs { get; set; } = new();
        public Dictionary<string, GameObject> Objects { get; set; } = new();
        public Dictionary<string, Item> Items { get; set; } = new();
        public Dictionary<string, Trigger> Triggers { get; set; } = new();
        public List<Line> Lines { get; set; } = new();
        public List<Greeting> Greetings { get; set; } = new();
    }

    public sealed class ZoneInfo
    {
        public uint AreaId { get; set; }
        public string Name { get; set; } = "";
        public int MapId { get; set; }
        public int Team { get; set; }
        public int Level { get; set; }
        public List<uint> SubAreas { get; set; } = new();
        public Rect? Rect { get; set; }
    }

    /// <summary>World yards: top = max X, bottom = min X, left = max Y, right = min Y.</summary>
    public sealed record Rect(float Top, float Bottom, float Left, float Right);

    public sealed class Quest
    {
        public uint Id { get; set; }
        public int Patch { get; set; }
        public bool Custom { get; set; }
        public string Title { get; set; } = "";
        public int Level { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public int ZoneOrSort { get; set; }
        public bool Home { get; set; }
        public int Races { get; set; }
        public int Classes { get; set; }
        public int Type { get; set; }
        public int Method { get; set; }
        public long QuestFlags { get; set; }
        public long SpecialFlags { get; set; }
        public int SuggestedPlayers { get; set; }
        public int LimitTime { get; set; }
        public int Prev { get; set; }
        public int Next { get; set; }
        public int NextInChain { get; set; }
        public int ExclusiveGroup { get; set; }
        public int BreadcrumbFor { get; set; }
        public uint SrcItem { get; set; }
        public uint SrcSpell { get; set; }
        public uint StartScript { get; set; }
        public uint CompleteScript { get; set; }
        public QuestText Text { get; set; } = new();
        public List<Objective> Objectives { get; set; } = new();
        public Rewards Rewards { get; set; } = new();
        public List<Ref> Givers { get; set; } = new();
        public List<Ref> Enders { get; set; } = new();
    }

    public sealed class QuestText
    {
        public string? Details { get; set; }
        public string? Objectives { get; set; }
        public string? RequestItems { get; set; }
        public string? OfferReward { get; set; }
        public string? End { get; set; }
    }

    /// <summary>kind: kill | object | item | explore | reputation.</summary>
    public sealed record Objective(string Kind, uint Id, int Count, string? Text, uint Spell);

    public sealed class Rewards
    {
        public int Xp { get; set; }
        public int Money { get; set; }
        public uint Spell { get; set; }
        public uint SpellCast { get; set; }
        public List<ItemCount> Choice { get; set; } = new();
        public List<ItemCount> Items { get; set; } = new();
        public List<Rep> Rep { get; set; } = new();
    }

    public sealed record ItemCount(uint Id, int Count);
    public sealed record Rep(int Faction, int Value);
    /// <summary>kind: npc | object.</summary>
    public sealed record Ref(string Kind, uint Entry);
    public sealed record Stub(uint Id, string Title, int Level, int ZoneOrSort, string? ZoneName, uint TopZone, int ExclusiveGroup);

    public sealed class Npc
    {
        public uint Entry { get; set; }
        public string Name { get; set; } = "";
        public string? Subname { get; set; }
        public int LevelMin { get; set; }
        public int LevelMax { get; set; }
        public int Faction { get; set; }
        public long NpcFlags { get; set; }
        public int Rank { get; set; }
        public int SpawnTotal { get; set; }
        /// <summary>[map, x, y, z] per spawn (≤300).</summary>
        public List<float[]> Spawns { get; set; } = new();
    }

    public sealed class GameObject
    {
        public uint Entry { get; set; }
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public int SpawnTotal { get; set; }
        public List<float[]> Spawns { get; set; } = new();
    }

    public sealed class Item
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public int Quality { get; set; }
        public int ItemClass { get; set; }
        public int Subclass { get; set; }
        public List<Source> Sources { get; set; } = new();
    }

    public sealed record Source(string Kind, uint Entry, float Chance);

    public sealed class Trigger
    {
        public uint Id { get; set; }
        public string? Name { get; set; }
        public int Map { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Radius { get; set; }
        public float BoxX { get; set; }
        public float BoxY { get; set; }
    }

    /// <summary>TextId: the broadcast_text a talk line speaks (0 for other commands; absent from older web apps).</summary>
    public sealed record Line(uint Script, string Phase, int Delay, int Command, int TargetType, uint TargetParam, int? ChatType, string? Text, string? Comments, uint TextId = 0);
    public sealed record Greeting(string Kind, uint Entry, string? Text);
}
