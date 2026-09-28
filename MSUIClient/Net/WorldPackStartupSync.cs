using System.Text.Json;

namespace MSUIClient.Net;

/// <summary>
/// Every client mirrors the server's published World Content Packs before it mounts its archives
/// (shared_docs/WORLD_BUILDER.md §7). The server is the authority: a client whose patch-7.MPQ or
/// collision is older than the server's pack build renders the wrong terrain and falls through pack
/// buildings. So at startup: ask the web app for the latest build; if the local sidecar
/// (patch-7.MPQ.json: build id + sha1, written by every download) differs, download patch-7 and sync
/// the pack collision first. Unreachable web app = keep what we have and say so.
/// </summary>
public static class WorldPackStartupSync
{
    public const string PatchName = "patch-7.MPQ";

    public sealed record LocalBuild(int BuildId, string? Sha1);

    public static LocalBuild? ReadLocal(string dataPath)
    {
        try
        {
            string sidecar = Path.Combine(dataPath, PatchName + ".json");
            if (!File.Exists(sidecar) || !File.Exists(Path.Combine(dataPath, PatchName))) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            return new LocalBuild(doc.RootElement.GetProperty("buildId").GetInt32(),
                doc.RootElement.TryGetProperty("sha1", out var s) ? s.GetString() : null);
        }
        catch { return null; }
    }

    public static void WriteLocal(string dataPath, int buildId, string? sha1) =>
        File.WriteAllText(Path.Combine(dataPath, PatchName + ".json"),
            JsonSerializer.Serialize(new { buildId, sha1, syncedUtc = DateTime.UtcNow }));

    /// <summary>Bring patch-7 + pack collision to the server's latest build. Returns a one-line status.</summary>
    public static string EnsureCurrent(WorldPackClient client, string baseUrl, string dataPath, string vmapDir, string mmapDir,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "world packs: no web app configured - skipped";
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var server = client.LastBuildAsync(baseUrl).WaitAsync(cts.Token).GetAwaiter().GetResult();
            if (server is null) return "world packs: the server has published none";
            var local = ReadLocal(dataPath);
            if (local is not null && local.BuildId == server.BuildId && (server.Sha1 is null || local.Sha1 == server.Sha1))
                return $"world packs: build #{server.BuildId} is current";
            string dest = Path.Combine(dataPath, PatchName);
            long size = client.DownloadPatchAsync(baseUrl, dest).WaitAsync(cts.Token).GetAwaiter().GetResult();
            var sync = WorldPackCollisionSync.SyncAsync(client, baseUrl, server.BuildId, vmapDir, mmapDir, cts.Token)
                .WaitAsync(cts.Token).GetAwaiter().GetResult();
            WriteLocal(dataPath, server.BuildId, server.Sha1);
            return $"world packs: updated to build #{server.BuildId} ({size / 1024} KiB patch-7; {sync.Summary})";
        }
        catch (Exception ex)
        {
            return $"world packs: could not sync ({ex.GetBaseException().Message}) - continuing with build #{ReadLocal(dataPath)?.BuildId.ToString() ?? "none"}";
        }
    }
}
