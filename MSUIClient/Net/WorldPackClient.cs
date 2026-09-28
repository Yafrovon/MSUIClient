using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.Net;

/// <summary>
/// MangosSuperUI <c>/WorldPacks/*</c> for the Creator Mode World Builder
/// (shared_docs/WORLD_BUILDER.md). The client never writes the server: every edit goes through
/// the web app, which stores it as an audited, undoable op and ships it only on Publish.
/// All calls are async and thread-agnostic; the game thread polls the returned tasks.
/// </summary>
public sealed class WorldPackClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http = WebAppHttp.Create(TimeSpan.FromSeconds(30));
    private readonly HttpClient _download = WebAppHttp.Create(TimeSpan.FromMinutes(10));

    public string Operator { get; set; } = Environment.MachineName;

    // ── DTOs (camelCase on the wire) ─────────────────────────────────────────

    public sealed class Pack
    {
        public int Id { get; set; }
        public string PackKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public long Placements { get; set; }
        public long SculptVertices { get; set; }
        public long UndoableOps { get; set; }
    }

    public sealed class Placement
    {
        public int Id { get; set; }
        public int PackId { get; set; }
        public int MapId { get; set; }
        public string Kind { get; set; } = "wmo";
        public string ModelPath { get; set; } = "";
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float Scale { get; set; } = 1f;
        public int DoodadSet { get; set; }
        public bool Deleted { get; set; }
        public bool Published { get; set; }
    }

    public sealed class SculptTile
    {
        public int Col { get; set; }
        public int Row { get; set; }
        public Dictionary<int, float> Deltas { get; set; } = new();
    }

    public sealed class BuildInfo
    {
        public int BuildId { get; set; }
        public string? Sha1 { get; set; }
        public long Size { get; set; }
        public List<string>? Packs { get; set; }
    }

    public sealed class State
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int MapId { get; set; }
        public List<Pack> Packs { get; set; } = new();
        public List<Placement> Placements { get; set; } = new();
        public List<SculptTile> Sculpt { get; set; } = new();
        public List<SculptTile> PublishedSculpt { get; set; } = new();
        public BuildInfo? LastBuild { get; set; }
    }

    public sealed class OpResult
    {
        public long OpId { get; set; }
        public long AuditId { get; set; }
        public long? UndoneOpId { get; set; }
        public string? UndoneKind { get; set; }
        public Placement? Placement { get; set; }
    }

    public sealed class Reply
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public OpResult? Result { get; set; }
        public Pack? Pack { get; set; }
        public int BuildId { get; set; }
    }

    public sealed class BuildStatus
    {
        public bool Running { get; set; }
        public int BuildId { get; set; }
        public string? Status { get; set; }
        public string? Phase { get; set; }
        public string? Error { get; set; }
        public string? MpqSha1 { get; set; }
        public long MpqSize { get; set; }
        public int FilesInstalled { get; set; }
        public int FilesRestored { get; set; }
        public bool ServerRestarted { get; set; }
        public List<string> Log { get; set; } = new();
        public BuildInfo? LastBuild { get; set; }
    }

    private sealed class StatusEnvelope { public bool Success { get; set; } public BuildStatus? Build { get; set; } }

    // ── calls ────────────────────────────────────────────────────────────────

    private static string U(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/WorldPacks/" + path;

    public async Task<State> GetStateAsync(string baseUrl, int mapId, int includePackId)
    {
        var s = await _http.GetFromJsonAsync<State>(U(baseUrl, $"State?mapId={mapId}&includePackId={includePackId}"), Json);
        return s ?? new State { Error = "empty reply" };
    }

    public async Task<BuildStatus> GetStatusAsync(string baseUrl)
    {
        var s = await _http.GetFromJsonAsync<StatusEnvelope>(U(baseUrl, "Status"), Json);
        return s?.Build ?? new BuildStatus();
    }

    private async Task<Reply> PostAsync(string baseUrl, string action, object body)
    {
        using var res = await _http.PostAsJsonAsync(U(baseUrl, action), body, Json);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            return new Reply { Error = $"HTTP {(int)res.StatusCode}: {Trim(text)}" };
        return JsonSerializer.Deserialize<Reply>(text, Json) ?? new Reply { Error = "empty reply" };
    }

    public Task<Reply> CreatePackAsync(string baseUrl, string key, string name, string description) =>
        PostAsync(baseUrl, "CreatePack", new { key, name, description, @operator = Operator });

    public Task<Reply> SetEnabledAsync(string baseUrl, int packId, bool enabled) =>
        PostAsync(baseUrl, "SetEnabled", new { packId, enabled, @operator = Operator });

    public Task<Reply> SculptAsync(string baseUrl, int packId, int mapId, string label, IEnumerable<SculptTile> tiles) =>
        PostAsync(baseUrl, "Sculpt", new { packId, mapId, label, tiles, @operator = Operator });

    public Task<Reply> PlaceAsync(string baseUrl, int packId, Placement p) =>
        PostAsync(baseUrl, "Place", new
        {
            packId, p.MapId, p.Kind, p.ModelPath, p.PosX, p.PosY, p.PosZ, p.RotX, p.RotY, p.RotZ, p.Scale, p.DoodadSet,
            @operator = Operator,
        });

    public Task<Reply> MoveAsync(string baseUrl, Placement p) =>
        PostAsync(baseUrl, "Move", new
        {
            placementId = p.Id, p.MapId, p.Kind, p.ModelPath, p.PosX, p.PosY, p.PosZ, p.RotX, p.RotY, p.RotZ, p.Scale, p.DoodadSet,
            @operator = Operator,
        });

    public Task<Reply> DeleteAsync(string baseUrl, int placementId) =>
        PostAsync(baseUrl, "Delete", new { placementId, @operator = Operator });

    /// <summary>Move a pack's region (docs, placements, sculpt) to another map/place by whole ADT tiles - one op.</summary>
    public Task<Reply> RelocateAsync(string baseUrl, int packId, int fromMap, int toMap, int dCol, int dRow) =>
        PostAsync(baseUrl, "Relocate", new { packId, fromMap, toMap, dCol, dRow, @operator = Operator });

    public Task<Reply> UndoAsync(string baseUrl, int packId) =>
        PostAsync(baseUrl, "Undo", new { packId, @operator = Operator });

    public Task<Reply> PublishAsync(string baseUrl, bool restartServer) =>
        PostAsync(baseUrl, "Publish", new { restartServer, @operator = Operator });

    /// <summary>One undoable op over several content docs (world-DB rows "dbrow:&lt;table&gt;", client DBC rows
    /// "dbc:&lt;Name&gt;", "map", "tile"). <paramref name="itemsJson"/> is a JSON array of
    /// { kind, key?, body } objects (body null = delete).</summary>
    public async Task<Reply> ContentAsync(string baseUrl, int packId, string label, string itemsJson)
    {
        string body = $"{{\"packId\":{packId},\"label\":{JsonSerializer.Serialize(label)},\"operator\":{JsonSerializer.Serialize(Operator)},\"items\":{itemsJson}}}";
        using var res = await _http.PostAsync(U(baseUrl, "Content"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return new Reply { Error = $"HTTP {(int)res.StatusCode}: {Trim(text)}" };
        return JsonSerializer.Deserialize<Reply>(text, Json) ?? new Reply { Error = "empty reply" };
    }

    /// <summary>Content docs of a pack (or all packs), optionally filtered by kind prefix. Raw JSON.</summary>
    public async Task<string> DocsAsync(string baseUrl, int? packId, string? kind)
    {
        string q = $"Docs?{(packId is { } p ? $"packId={p}&" : "")}{(kind is null ? "" : $"kind={Uri.EscapeDataString(kind)}")}";
        return await _http.GetStringAsync(U(baseUrl, q));
    }

    /// <summary>Read-only CSV export of stock world-DB rows (e.g. a trainer's spell list to copy).</summary>
    /// <summary>World Pack Verifier report (shared_docs/WORLD_BUILDER.md §7): the last one (publish
    /// runs it after the restart), or a fresh run with <paramref name="rerun"/>. Raw JSON.</summary>
    /// <summary>Pre-flight: content checks + DB column validation on the UNPUBLISHED docs (seconds). Raw JSON.</summary>
    public Task<string> PreflightAsync(string baseUrl) => _http.GetStringAsync(U(baseUrl, "Preflight"));

    public Task<string> VerifyAsync(string baseUrl, bool rerun) =>
        (rerun ? _download : _http).GetStringAsync(U(baseUrl, rerun ? "Verify" : "VerifyReport"));   // a run takes ~1 min

    public Task<string> ExportCsvAsync(string baseUrl, string table, string filterCol, string filterVal) =>
        _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/Database/Export/mangos/{table}?filterCol={Uri.EscapeDataString(filterCol)}&filterVal={Uri.EscapeDataString(filterVal)}");

    /// <summary>Download the published archive to <paramref name="destination"/> (atomic: temp + move).</summary>
    public async Task<long> DownloadPatchAsync(string baseUrl, string destination)
    {
        using var res = await _download.GetAsync(U(baseUrl, "Patch"), HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync())}");
        string temp = destination + ".download";
        await using (var file = File.Create(temp))
            await res.Content.CopyToAsync(file);
        long size = new FileInfo(temp).Length;
        File.Move(temp, destination, overwrite: true);
        return size;
    }

    private sealed class PacksEnvelope { public bool Success { get; set; } public BuildInfo? LastBuild { get; set; } }

    /// <summary>The server's latest published pack build (id + sha1 of its patch-7.MPQ), or null.</summary>
    public async Task<BuildInfo?> LastBuildAsync(string baseUrl) =>
        (await _http.GetFromJsonAsync<PacksEnvelope>(U(baseUrl, "Packs"), Json))?.LastBuild;

    /// <summary>Server files the published packs installed (maps/, vmaps/, mmaps/) - the collision-sync truth.</summary>
    public async Task<List<(string Path, bool Present, long Size, string? Sha1)>> InstalledAsync(string baseUrl) =>
        WorldPackCollisionSync.Parse(await _download.GetStringAsync(U(baseUrl, "Installed")));

    /// <summary>Download one installed server file (atomic: temp + move). Returns its size.</summary>
    public async Task<long> DownloadServerFileAsync(string baseUrl, string path, string destination, CancellationToken ct = default)
    {
        using var res = await _download.GetAsync(U(baseUrl, "ServerFile?path=" + Uri.EscapeDataString(path)), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"{path}: HTTP {(int)res.StatusCode}");
        string temp = destination + ".download";
        await using (var file = File.Create(temp))
            await res.Content.CopyToAsync(file, ct);
        long size = new FileInfo(temp).Length;
        File.Move(temp, destination, overwrite: true);
        return size;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
