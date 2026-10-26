using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MSUIClient.Net;

/// <summary>
/// The custom-spell patch (patch-3.MPQ: every Completer spell's Spell.dbc / SpellVisual rows,
/// models, images and sounds) on this machine, kept equal to the web app's
/// (shared_docs/SPELL_CREATOR_PLAN.md P0.7). Two users:
///
///   • startup, before the archives mount (like WorldPackStartupSync for patch-7): a client
///     whose patch-3 is older than the server's shows custom spells with the wrong name, the
///     wrong targeting and no visual - or not at all;
///   • the creator's Test live loop, which rebuilds the patch and then hot-swaps it.
///
/// The local copy carries a sidecar (patch-3.MPQ.json: sha1 + when) written by every
/// download, so "is it current" is one small GET, never a re-download.
///
/// THREADING: every method here is plain async I/O on the caller's task; nothing touches the
/// game. Mounting is the caller's business (MpqMount.ReplaceArchive on the game thread).
/// </summary>
public sealed class SpellPatchClient
{
    public const string PatchName = "patch-3.MPQ";

    private readonly HttpClient _http = WebAppHttp.Create(TimeSpan.FromMinutes(3));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record ServerPatch(bool Exists, long Size, string? Sha1, string? BuiltUtc);

    public async Task<ServerPatch> InfoAsync(string baseUrl, CancellationToken ct = default)
    {
        string body = await _http.GetStringAsync(U(baseUrl, "Patch/SpellPatchInfo"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        bool exists = root.TryGetProperty("exists", out var e) && e.GetBoolean();
        return new ServerPatch(exists,
            root.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
            root.TryGetProperty("sha1", out var h) ? h.GetString() : null,
            root.TryGetProperty("builtUtc", out var b) ? b.GetString() : null);
    }

    /// <summary>Download the server's patch-3 to <paramref name="destination"/> (a temp name next
    /// to the live archive - the mounted file cannot be overwritten while it is open). Returns the
    /// sha1 of what arrived.</summary>
    public async Task<string> DownloadAsync(string baseUrl, string destination, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync(U(baseUrl, $"Patch/Download?file={PatchName}"),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var file = File.Create(destination))
            await resp.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        return Sha1Of(destination);
    }

    /// <summary>POST a JSON body to the web app and return the parsed reply; throws with the
    /// server's own error text when it answers success:false.</summary>
    public async Task<JsonObject> PostAsync(string baseUrl, string route, JsonObject? body, CancellationToken ct = default)
    {
        using var content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _http.PostAsync(U(baseUrl, route), content, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{route}: HTTP {(int)resp.StatusCode} {Trim(text)}");
        if (JsonNode.Parse(text) is not JsonObject reply)
            throw new InvalidOperationException($"{route}: unreadable reply {Trim(text)}");
        if (reply["success"]?.GetValue<bool>() == false)
            throw new InvalidOperationException($"{route}: {reply["error"]?.ToString() ?? Trim(text)}");
        return reply;
    }

    // ── the local copy ───────────────────────────────────────────────────────

    public static string LocalPath(string dataPath) => Path.Combine(dataPath, PatchName);

    /// <summary>A sha-named copy outside the archive tier (Data\spellpatch\ is never enumerated
    /// as an MPQ at mount time), used when the canonical file cannot be replaced while open.</summary>
    public static string VersionedPath(string dataPath, string sha1) =>
        Path.Combine(dataPath, "spellpatch", $"patch-3-{sha1[..12]}.MPQ");

    public static string? LocalSha1(string dataPath)
    {
        try
        {
            string sidecar = LocalPath(dataPath) + ".json";
            if (!File.Exists(sidecar) || !File.Exists(LocalPath(dataPath))) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            return doc.RootElement.TryGetProperty("sha1", out var s) ? s.GetString() : null;
        }
        catch { return null; }
    }

    public static void WriteSidecar(string dataPath, string sha1) =>
        File.WriteAllText(LocalPath(dataPath) + ".json",
            JsonSerializer.Serialize(new { sha1, syncedUtc = DateTime.UtcNow }, Json));

    public static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Startup: bring patch-3 to the server's before anything mounts. Unreachable web app
    /// or no server patch = keep what is there and say so. Blocking by design (the archives are
    /// opened right after), bounded by the two timeouts.</summary>
    public static string EnsureCurrent(string baseUrl, string dataPath, TimeSpan checkTimeout, TimeSpan downloadTimeout)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "spell patch: no web app configured - skipped";
        var client = new SpellPatchClient();
        try
        {
            ServerPatch server;
            using (var check = new CancellationTokenSource(checkTimeout))
                server = client.InfoAsync(baseUrl, check.Token).GetAwaiter().GetResult();
            if (!server.Exists || server.Sha1 is null) return "spell patch: the server has built none";
            string? local = LocalSha1(dataPath);
            if (string.Equals(local, server.Sha1, StringComparison.OrdinalIgnoreCase))
                return $"spell patch: current ({server.Sha1[..8]})";
            string temp = LocalPath(dataPath) + ".download";
            using (var download = new CancellationTokenSource(downloadTimeout))
            {
                string got = client.DownloadAsync(baseUrl, temp, download.Token).GetAwaiter().GetResult();
                if (!string.Equals(got, server.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temp);
                    return $"spell patch: download did not match the server's sha1 ({got[..8]} vs {server.Sha1[..8]}) - kept the old one";
                }
            }
            File.Move(temp, LocalPath(dataPath), overwrite: true);
            WriteSidecar(dataPath, server.Sha1);
            return $"spell patch: updated to {server.Sha1[..8]} ({server.Size / 1024} KB)";
        }
        catch (Exception ex)
        {
            return $"spell patch: sync skipped ({ex.GetBaseException().Message}) - using what is on disk";
        }
    }

    private static string U(string baseUrl, string route) => $"{baseUrl.TrimEnd('/')}/{route}";

    private static string Trim(string s) => s.Length <= 240 ? s : s[..240] + "…";
}
