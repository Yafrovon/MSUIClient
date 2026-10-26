using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// TEST LIVE — the creator half (shared_docs/SPELL_CREATOR_PLAN.md P0.7, decision D8).
//
// One button turns the design on screen into a spell the LIVE server runs and the live
// client can cast: push → complete (replacing the id this design was last tested as) →
// build patch-3 → download → hot-swap it here → hand the spell to the live client.
//
// Two client processes take part, because Creator Mode is an offline sandbox and the live
// server needs a logged-in character: this one (the creator) does everything up to the
// patch on disk, then writes spell-test-request.json beside the session file; the live
// client (GameLoop.SpellTestLive.cs, launched from the same folder) sees it, re-mounts the
// same patch, reloads its spell catalogs and asks the server - as the GM character it is
// logged in as - to reload spell_template and teach the spell.
//
// spell-test-entries.json remembers designId → spell id, so iterating a design keeps ONE
// spell (one action-bar slot) instead of minting a new spell per try.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    public const string SpellTestRequestFileName = "spell-test-request.json";
    private const string SpellTestEntriesFileName = "spell-test-entries.json";

    private SpellTestLiveClient? _testLive;
    private SpellTestLiveClient TestLive => _testLive ??= new SpellTestLiveClient();
    private string _testLiveStatus = "";
    private string? _testLiveDesignId;
    private bool _testLiveReloadCreatures;
    private bool _testLiveReloadSpellIndex;

    private static string SpellTestRequestPath => Path.Combine(CreatorSessionDir, SpellTestRequestFileName);
    private static string SpellTestEntriesPath => Path.Combine(CreatorSessionDir, SpellTestEntriesFileName);

    private static Dictionary<string, int> LoadSpellTestEntries()
    {
        try
        {
            if (File.Exists(SpellTestEntriesPath) &&
                JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(SpellTestEntriesPath)) is { } map)
                return map;
        }
        catch (Exception ex) { Console.WriteLine($"[test-live] {SpellTestEntriesFileName} unreadable: {ex.Message}"); }
        return new Dictionary<string, int>();
    }

    private void BeginCreatorTestLive(CreatorSpellDoc doc, string tempName)
    {
        if (SuiWebAppUrl.Length == 0)
        {
            _testLiveStatus = "No MSUI Web connection - set it on the login screen first.";
            return;
        }
        if (BuildCreatorSpellEntry(doc, tempName) is not { } entry)
        {
            _testLiveStatus = _creatorExportStatus;
            return;
        }
        bool isNew = IsCreatorNewSpellId(doc.Info.Id);
        // The data phase, at its plainest: the design's own name, the source's school and
        // icon (or the school icon for a new spell), no trainers, no rank chain. The rules
        // block (P1) will carry everything else.
        var form = new JsonObject
        {
            ["spellName"] = tempName,
            ["nameSubtext"] = "",
            ["school"] = -1,
            ["iconSource"] = isNew ? "school" : "source",
            ["copySourceTrainers"] = false,
            ["generateAllRanks"] = false,
        };
        Dictionary<string, int> knownEntries = LoadSpellTestEntries();
        int? replace = knownEntries.TryGetValue(doc.DesignId, out int known) ? known : null;
        _testLiveDesignId = doc.DesignId;
        // Child spells (P4.1): each completed first, under its own remembered id.
        var children = new List<SpellTestLiveClient.Child>();
        if (entry["children"] is JsonArray childNodes)
            foreach (JsonNode? node in childNodes)
            {
                if (node?["entry"] is not JsonObject childEntry) continue;
                uint localId = node["localId"]?.GetValue<uint>() ?? 0;
                string childDesign = childEntry["designId"]?.GetValue<string>() ?? $"{doc.DesignId}.child";
                string childName = childEntry["tempName"]?.GetValue<string>() ?? "child";
                var childForm = new JsonObject
                {
                    ["spellName"] = childName,
                    ["nameSubtext"] = "",
                    ["school"] = -1,
                    ["iconSource"] = "school",
                    ["copySourceTrainers"] = false,
                    ["generateAllRanks"] = false,
                };
                children.Add(new SpellTestLiveClient.Child((JsonObject)childEntry.DeepClone(), childForm,
                    knownEntries.TryGetValue(childDesign, out int childKnown) ? childKnown : null, localId, childDesign));
            }
        _testLiveReloadCreatures = entry["actor"] is not null;
        // D5: a new index row (this spell's or a child's) must reach the live server's stores first.
        _testLiveReloadSpellIndex = entry["rules"]?["newIndexRows"] is not null ||
                                    children.Any(c => c.Entry["rules"]?["newIndexRows"] is not null);
        TestLive.Begin(SuiWebAppUrl, entry, form, replace, _config.ClientDataPath, children);
        _testLiveStatus = "";
    }

    /// <summary>Per frame in the creator world: when the network half finishes, install the patch
    /// here and hand the spell to the live client. Game thread only (the mount is shared state).</summary>
    private void UpdateCreatorTestLive()
    {
        if (_testLive?.Result is not { } outcome) return;
        _testLive.ClearResult();
        if (!outcome.Ok)
        {
            _testLiveStatus = $"Test live failed - {outcome.Error}";
            return;
        }

        // Remember the id for the next iteration of this design.
        if (_testLiveDesignId is { } designId)
        {
            Dictionary<string, int> entries = LoadSpellTestEntries();
            entries[designId] = outcome.SpellEntry;
            foreach ((string childDesign, int childEntry) in outcome.Children ?? Array.Empty<(string, int)>())
                entries[childDesign] = childEntry;
            File.WriteAllText(SpellTestEntriesPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }

        string installed = InstallSpellPatch(outcome.DownloadedPath!, outcome.Sha1!, "test-live");
        File.WriteAllText(SpellTestRequestPath, new JsonObject
        {
            ["spellEntry"] = outcome.SpellEntry,
            ["tempName"] = outcome.TempName,
            ["patchSha1"] = outcome.Sha1,
            ["patchPath"] = _testLiveMountedPath,
            // An actor's creature row is new: the live server must re-read creature_template (P4.2).
            ["reloadCreatures"] = _testLiveReloadCreatures,
            ["reloadSpellIndex"] = _testLiveReloadSpellIndex,
            ["requestedUtc"] = DateTime.UtcNow.ToString("o"),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _testLiveStatus = $"#{outcome.SpellEntry} '{outcome.TempName}' is built ({installed}). " +
                          "A live client launched from this folder learns it within two seconds.";
        Console.WriteLine($"[test-live] {_testLiveStatus}");
    }

    /// <summary>Put a downloaded patch-3 in place and mount it. Normally that is a move over the
    /// installed file. When another client process holds it in a way Windows will not replace
    /// (measured 2026-10-25: "Access to the path is denied" with a live client running), the
    /// bytes go to a VERSIONED copy under Data\spellpatch\ and that copy is mounted instead; the
    /// sidecar is left alone, so the next start's sync installs the canonical file. An identical
    /// patch (same sha1) is not installed at all - re-opening it is enough.</summary>
    private string InstallSpellPatch(string downloaded, string sha1, string cause)
    {
        string dest = SpellPatchClient.LocalPath(_config.ClientDataPath);
        if (string.Equals(SpellPatchClient.LocalSha1(_config.ClientDataPath), sha1, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(dest))
        {
            TryDelete(downloaded);
            bool same = _mpq?.ReplaceArchive(SpellPatchClient.PatchName, dest) == true;
            ReloadSpellCatalogs(cause);
            return same ? $"patch-3 {sha1[..8]} unchanged, re-opened" : "patch-3 unchanged but could not be re-opened";
        }

        string mountPath = dest;
        try
        {
            _mpq?.ReplaceArchive(SpellPatchClient.PatchName, null);
            File.Move(downloaded, dest, overwrite: true);
            SpellPatchClient.WriteSidecar(_config.ClientDataPath, sha1);
        }
        catch (Exception ex)
        {
            mountPath = SpellPatchClient.VersionedPath(_config.ClientDataPath, sha1);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mountPath)!);
                File.Move(downloaded, mountPath, overwrite: true);
                Console.WriteLine($"[test-live] {SpellPatchClient.PatchName} is held open elsewhere ({ex.Message}); " +
                                  $"mounting the versioned copy {mountPath}");
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[test-live] could not install {SpellPatchClient.PatchName}: {inner.Message}");
                if (File.Exists(dest)) _mpq?.ReplaceArchive(SpellPatchClient.PatchName, dest);
                return $"install failed: {inner.Message}";
            }
        }
        bool mounted = _mpq?.ReplaceArchive(SpellPatchClient.PatchName, mountPath) == true;
        ReloadSpellCatalogs(cause);
        _testLiveMountedPath = mountPath;
        return mounted ? $"patch-3 {sha1[..8]} mounted" : "patch-3 installed but could not be mounted";
    }

    /// <summary>Where this client mounted patch-3 from last (canonical or versioned), so the
    /// request can tell a live client in the same folder exactly which file to open.</summary>
    private string? _testLiveMountedPath;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a leftover download is harmless */ }
    }

    /// <summary>The Test live row under the session buttons.</summary>
    private void DrawCreatorTestLiveRow(CreatorSpellDoc doc, string tempName, bool nameOk, bool webOk)
    {
        bool busy = TestLive.Running || SpellPush.Pushing;
        if (!nameOk || !webOk || busy) ImGui.BeginDisabled();
        if (CreatorButton("Test live")) BeginCreatorTestLive(doc, tempName);
        if (!nameOk || !webOk || busy) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Build this design into a real spell on the live server and teach it to the " +
                             "character a live client is logged in as (launched from this same folder).\n\n" +
                             "Push, complete, build patch-3, install it here, then the live client " +
                             "reloads spell_template and learns the spell. Testing the same design " +
                             "again replaces the same spell id.");
        if (TestLive.Running)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{TestLive.Stage}…");
        }
        if (_testLiveStatus.Length > 0)
        {
            bool failed = _testLiveStatus.StartsWith("Test live failed", StringComparison.Ordinal) ||
                          _testLiveStatus.StartsWith("No MSUI", StringComparison.Ordinal);
            ImGui.TextColored(failed ? new Vector4(0.9f, 0.45f, 0.45f, 1f) : new Vector4(0.45f, 0.85f, 0.45f, 1f),
                _testLiveStatus);
        }
    }
}
