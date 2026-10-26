using System.Text.Json.Nodes;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// TEST LIVE — the live-client half (shared_docs/SPELL_CREATOR_PLAN.md P0.7).
//
// A creator client launched from the same folder writes spell-test-request.json when it has
// built a design into a real spell (GameLoop.Creator.TestLive.cs). A live client that is in
// the world picks it up here: make sure its patch-3 is the one the request names (re-open the
// file the creator installed, or download it when this client runs from another folder),
// reload its spell catalogs, then - as the GM character it is - ask the server to reload
// spell_template and learn the spell. Two seconds between polls; nothing runs in the creator
// world or when no request is newer than the last one handled.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private double _spellTestNextPoll;
    private DateTime _spellTestHandledStamp;
    private Task<string>? _spellTestDownload;
    private (int Entry, string Sha1, string Name)? _spellTestPending;
    private double _spellTestLearnAt;
    private bool _spellTestReloadCreatures;
    private bool _spellTestReloadSpellIndex;
    private readonly DateTime _bootUtc = DateTime.UtcNow;

    private void UpdateSpellTestLiveWatcher()
    {
        if (_creatorWorldRequested || _net is not { IsInWorld: true }) return;
        double now = NowSeconds();

        // A queued learn: the reload went out first; give the server a beat to finish it.
        if (_spellTestPending is { } pending && _spellTestDownload is null && _spellTestLearnAt > 0 && now >= _spellTestLearnAt)
        {
            _spellTestLearnAt = 0;
            _spellTestPending = null;
            bool sent = SendGmCommand($".learn {pending.Entry}", "spell-test-live");
            AddChatMessage(sent
                ? $"[Test live] learned #{pending.Entry} {pending.Name} - it is in your spellbook."
                : $"[Test live] could not send .learn {pending.Entry}.");
            return;
        }

        // A download for a request whose patch this folder did not have.
        if (_spellTestDownload is { IsCompleted: true } download && _spellTestPending is { } waiting)
        {
            _spellTestDownload = null;
            if (download.IsFaulted || !string.Equals(download.Result, waiting.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                AddChatMessage($"[Test live] patch-3 download failed for #{waiting.Entry}: " +
                               (download.Exception?.GetBaseException().Message ?? "sha1 mismatch"));
                _spellTestPending = null;
                return;
            }
            InstallSpellPatch(SpellPatchClient.LocalPath(_config.ClientDataPath) + ".download", waiting.Sha1, "test-live (download)");
            QueueSpellTestLearn(waiting);
            return;
        }

        if (now < _spellTestNextPoll || _spellTestDownload is not null || _spellTestPending is not null) return;
        _spellTestNextPoll = now + 2.0;

        string path = SpellTestRequestPath;
        DateTime stamp;
        try
        {
            if (!File.Exists(path)) return;
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch { return; }
        if (stamp <= _spellTestHandledStamp) return;
        // Requests older than this session are history, not instructions.
        if (_spellTestHandledStamp == default && stamp < _bootUtc) { _spellTestHandledStamp = stamp; return; }
        _spellTestHandledStamp = stamp;

        JsonNode? request;
        try { request = JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { Console.WriteLine($"[test-live] request unreadable: {ex.Message}"); return; }
        int entry = request?["spellEntry"]?.GetValue<int>() ?? 0;
        string sha1 = request?["patchSha1"]?.GetValue<string>() ?? "";
        string name = request?["tempName"]?.GetValue<string>() ?? "";
        if (entry <= 0 || sha1.Length == 0) return;
        _spellTestReloadCreatures = request?["reloadCreatures"]?.GetValue<bool>() ?? false;
        _spellTestReloadSpellIndex = request?["reloadSpellIndex"]?.GetValue<bool>() ?? false;
        var job = (entry, sha1, name);
        _spellTestPending = job;

        string? named = request?["patchPath"]?.GetValue<string>();
        if (named is { Length: > 0 } && File.Exists(named) &&
            string.Equals(SpellPatchClient.Sha1Of(named), sha1, StringComparison.OrdinalIgnoreCase))
        {
            // The creator installed it in this folder - canonical or, when the canonical file
            // was held open, a versioned copy. Open exactly that file.
            bool mounted = _mpq?.ReplaceArchive(SpellPatchClient.PatchName, named) == true;
            ReloadSpellCatalogs("test-live");
            if (!mounted) Console.WriteLine($"[test-live] {named} could not be opened");
            QueueSpellTestLearn(job);
        }
        else if (string.Equals(SpellPatchClient.LocalSha1(_config.ClientDataPath), sha1, StringComparison.OrdinalIgnoreCase))
        {
            // The creator already installed it in this folder: re-open the new file (our handle
            // still reads the bytes it replaced) and reload.
            string dest = SpellPatchClient.LocalPath(_config.ClientDataPath);
            bool mounted = _mpq?.ReplaceArchive(SpellPatchClient.PatchName, dest) == true;
            ReloadSpellCatalogs("test-live");
            if (!mounted) Console.WriteLine("[test-live] patch-3 is on disk but could not be re-opened");
            QueueSpellTestLearn(job);
        }
        else if (SuiWebAppUrl.Length > 0)
        {
            string temp = SpellPatchClient.LocalPath(_config.ClientDataPath) + ".download";
            string url = SuiWebAppUrl;
            _spellTestDownload = Task.Run(() => new SpellPatchClient().DownloadAsync(url, temp));
            AddChatMessage($"[Test live] fetching patch-3 for #{entry} {name}…");
        }
        else
        {
            AddChatMessage($"[Test live] #{entry} needs patch-3 {sha1[..8]} and there is no web connection to fetch it.");
            _spellTestPending = null;
        }
    }

    private void QueueSpellTestLearn((int Entry, string Sha1, string Name) job)
    {
        // D5: new SpellCastTimes/Duration/Range/Radius rows first, so no spell names a missing row.
        if (_spellTestReloadSpellIndex) SendGmCommand(".reload spell_index", "spell-test-live");
        bool sent = SendGmCommand(".reload spell_template", "spell-test-live");
        // A spell that plants an actor brought a new creature row (P4.2).
        if (_spellTestReloadCreatures) SendGmCommand(".reload creature_template", "spell-test-live");
        AddChatMessage(sent
            ? $"[Test live] #{job.Entry} {job.Name}: patch-3 {job.Sha1[..8]} mounted, reloading spell_template…"
            : $"[Test live] #{job.Entry}: could not send .reload spell_template.");
        _spellTestPending = job;
        _spellTestLearnAt = NowSeconds() + 1.5;
    }
}
