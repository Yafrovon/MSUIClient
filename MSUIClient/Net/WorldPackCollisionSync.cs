using System.Text.Json;

namespace MSUIClient.Net;

/// <summary>
/// Brings a client's server-format collision (vmaps) and navmesh (mmaps) in line with what the
/// published World Content Packs installed on the server (shared_docs/WORLD_BUILDER.md §7).
///
/// patch-7.MPQ only carries what the game client renders. MSUIClient's live collision reads the
/// server-format vmaps under <c>GameData\vmaps</c>, so without this a player falls through every pack
/// building, bridge and fortress floor (found by the tier-3 walk-through, 2026-09-26: a Bloodfang
/// Stalker on a ledge stood 8.7 yd above a player who could never reach it).
///
/// Law: the server's /WorldPacks/Installed list is the truth. Every listed vmaps/ and mmaps/ file is
/// downloaded; an original it replaces is kept once in <c>.worldpack-baseline/</c>; a file a previous
/// sync wrote that the server no longer installs is restored from that baseline (or deleted when there
/// was none, e.g. a pack map's tiles). <c>worldpack-sync.json</c> in each folder records the last sync.
/// </summary>
public sealed class WorldPackCollisionSync
{
    private const string Manifest = "worldpack-sync.json";
    private const string BaselineDir = ".worldpack-baseline";

    public sealed record Result(int Downloaded, int Restored, long Bytes, string Summary);

    private sealed class SyncManifest
    {
        public int? BuildId { get; set; }
        public List<string> Files { get; set; } = new();
    }

    public static async Task<Result> SyncAsync(WorldPackClient client, string baseUrl, int? buildId,
        string vmapDir, string mmapDir, CancellationToken ct = default)
    {
        var installed = await client.InstalledAsync(baseUrl);
        var dirs = new Dictionary<string, string> { ["vmaps/"] = vmapDir, ["mmaps/"] = mmapDir };
        int downloaded = 0, restored = 0, unchanged = 0;
        long bytes = 0;
        foreach (var (prefix, dir) in dirs)
        {
            Directory.CreateDirectory(dir);
            string baseline = System.IO.Path.Combine(dir, BaselineDir);
            string manifestPath = System.IO.Path.Combine(dir, Manifest);
            var previous = Load(manifestPath);
            var wanted = installed.Where(f => f.Present && f.Path.StartsWith(prefix, StringComparison.Ordinal))
                                  .Select(f => f.Path[prefix.Length..]).ToList();
            var serverSha = installed.Where(f => f.Sha1 is not null && f.Path.StartsWith(prefix, StringComparison.Ordinal))
                                     .ToDictionary(f => f.Path[prefix.Length..], f => f.Sha1!);
            foreach (var name in wanted)
            {
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) continue;   // flat folders only
                string local = System.IO.Path.Combine(dir, name);
                string keep = System.IO.Path.Combine(baseline, name);
                if (File.Exists(local) && !File.Exists(keep) && !previous.Files.Contains(name))
                {
                    Directory.CreateDirectory(baseline);
                    File.Copy(local, keep);
                }
                // Already the server's bytes (a new build re-installs whole map sets, most files unchanged).
                if (serverSha.TryGetValue(name, out string? want) &&
                    File.Exists(local) && string.Equals(Sha1Of(local), want, StringComparison.OrdinalIgnoreCase))
                { unchanged++; continue; }
                bytes += await client.DownloadServerFileAsync(baseUrl, prefix + name, local, ct);
                downloaded++;
            }
            foreach (var gone in previous.Files.Except(wanted))
            {
                string local = System.IO.Path.Combine(dir, gone);
                string keep = System.IO.Path.Combine(baseline, gone);
                if (File.Exists(keep)) File.Copy(keep, local, overwrite: true);
                else if (File.Exists(local)) File.Delete(local);
                restored++;
            }
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new SyncManifest { BuildId = buildId, Files = wanted }), ct);
        }
        return new Result(downloaded, restored, bytes,
            $"collision synced: {downloaded} file(s) ({bytes / 1024 / 1024} MiB), {unchanged} already current, {restored} restored to stock");
    }

    /// <summary>Build id of the last collision sync in <paramref name="vmapDir"/>, or null.</summary>
    public static int? SyncedBuild(string vmapDir) => Load(System.IO.Path.Combine(vmapDir, Manifest)).BuildId;

    private static SyncManifest Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SyncManifest>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    internal static List<(string Path, bool Present, long Size, string? Sha1)> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => (f.GetProperty("path").GetString() ?? "", f.GetProperty("present").GetBoolean(), f.GetProperty("size").GetInt64(),
                          f.TryGetProperty("sha1", out var h) ? h.GetString() : null))
            .ToList();
    }

    private static string? Sha1Of(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream)).ToLowerInvariant();
        }
        catch { return null; }
    }
}
