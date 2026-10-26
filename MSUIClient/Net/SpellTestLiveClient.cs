using System.Text.Json.Nodes;

namespace MSUIClient.Net;

/// <summary>
/// The creator's Test live loop, the network half (shared_docs/SPELL_CREATOR_PLAN.md P0.7, D8):
/// one design → a castable spell on the live server, with no hand steps.
///
///   push      POST /SpellCompleter/Push           the design lands in the inbox
///   complete  POST /SpellCompleter/Complete       spell_template rows (replacing the entry this
///                                                 design was tested as last time, if any)
///   build     POST /Patch/RebuildClientPatch      patch-3.MPQ with every custom spell
///   fetch     GET  /Patch/SpellPatchInfo + Download   the patch, to a temp file beside the live one
///
/// The game thread then hot-swaps the archive and asks the live client to learn the spell
/// (GameLoop.Creator.TestLive.cs). THREADING: one background task; the game thread reads only
/// the volatile Stage string and the immutable Result once published.
/// </summary>
public sealed class SpellTestLiveClient
{
    public sealed record Outcome(bool Ok, string TempName, int SpellEntry, string? Sha1,
        string? DownloadedPath, string? Error, DateTime CompletedUtc,
        IReadOnlyList<(string DesignId, int SpellEntry)>? Children = null);

    /// <summary>A child spell (MSUIClient SPELL_CREATOR_PLAN.md P4.1): completed BEFORE the parent,
    /// whose rules name it by <paramref name="LocalId"/> until then.</summary>
    public sealed record Child(JsonObject Entry, JsonObject Form, int? ReplaceEntry, uint LocalId, string DesignId);

    private readonly SpellPatchClient _patch = new();
    private Task? _running;
    private volatile string _stage = "";
    private volatile Outcome? _result;

    public bool Running => _running is { IsCompleted: false };
    public string Stage => _stage;
    public Outcome? Result => _result;
    public void ClearResult() => _result = null;

    /// <param name="entry">The session entry exactly as the push would send it.</param>
    /// <param name="form">The Completer's data-phase fields (spellName, school, …); the pending id
    /// and the replace entry are filled in here.</param>
    /// <param name="replaceEntry">The spell id this design was last tested as, or null.</param>
    public void Begin(string baseUrl, JsonObject entry, JsonObject form, int? replaceEntry, string dataPath,
        IReadOnlyList<Child>? children = null)
    {
        if (Running) return;
        _result = null;
        string tempName = entry["tempName"]?.GetValue<string>() ?? "";
        string entryJson = entry.ToJsonString();     // serialize on the caller's thread
        string formJson = form.ToJsonString();
        var childJson = (children ?? Array.Empty<Child>())
            .Select(c => (Entry: c.Entry.ToJsonString(), Form: c.Form.ToJsonString(), c.ReplaceEntry, c.LocalId, c.DesignId)).ToList();
        _running = Task.Run(() => Run(baseUrl, tempName, entryJson, formJson, replaceEntry, dataPath, childJson));
    }

    /// <summary>Push one entry and complete it; the spell id it became.</summary>
    private async Task<int> PushAndComplete(string baseUrl, string label, JsonObject entry, JsonObject form, int? replaceEntry)
    {
        _stage = $"pushing {label}";
        JsonObject pushed = await _patch.PostAsync(baseUrl, "SpellCompleter/Push", entry).ConfigureAwait(false);
        string? pendingId = (pushed["spells"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pendingId))
            throw new InvalidOperationException("the inbox accepted the push but named no item");
        _stage = replaceEntry is { } r ? $"completing {label} (replacing #{r})" : $"completing {label} (new spell)";
        form["pendingId"] = pendingId;
        if (replaceEntry is { } replace) form["replaceEntry"] = replace;
        JsonObject completed = await _patch.PostAsync(baseUrl, "SpellCompleter/Complete", form).ConfigureAwait(false);
        int spell = completed["spellEntry"]?.GetValue<int>() ?? 0;
        if (spell <= 0) throw new InvalidOperationException($"Complete returned no spell entry for {label}");
        return spell;
    }

    private async Task Run(string baseUrl, string tempName, string entryJson, string formJson,
        int? replaceEntry, string dataPath,
        List<(string Entry, string Form, int? ReplaceEntry, uint LocalId, string DesignId)> children)
    {
        try
        {
            // Children first: the parent's rules name them by local id until their real ids exist.
            var realIds = new Dictionary<uint, int>();
            var childResults = new List<(string DesignId, int SpellEntry)>();
            foreach ((string childEntry, string childForm, int? childReplace, uint localId, string designId) in children)
            {
                int childSpell = await PushAndComplete(baseUrl, $"child {designId}",
                    (JsonObject)JsonNode.Parse(childEntry)!, (JsonObject)JsonNode.Parse(childForm)!, childReplace).ConfigureAwait(false);
                realIds[localId] = childSpell;
                childResults.Add((designId, childSpell));
                Console.WriteLine($"[test-live] child {designId} (local #{localId}) -> #{childSpell}");
            }

            var entry = (JsonObject)JsonNode.Parse(entryJson)!;
            entry.Remove("children");                                  // the Completer knows nothing of them
            if (realIds.Count > 0 && entry["rules"]?["columns"] is JsonObject columns)
                foreach ((string column, JsonNode? value) in columns.ToList())
                    if (column.StartsWith("effectTriggerSpell", StringComparison.Ordinal) &&
                        value is not null && realIds.TryGetValue((uint)value.GetValue<double>(), out int real))
                        columns[column] = real;
            // The actor's spell, when it is a child (P4.2).
            if (entry["actor"] is JsonObject actor && actor["spell"] is JsonNode actorSpell &&
                realIds.TryGetValue((uint)actorSpell.GetValue<double>(), out int actorReal))
                actor["spell"] = actorReal;
            int spell = await PushAndComplete(baseUrl, "the design", entry, (JsonObject)JsonNode.Parse(formJson)!, replaceEntry)
                .ConfigureAwait(false);

            _stage = $"building patch-3 with #{spell}";
            await _patch.PostAsync(baseUrl, "Patch/RebuildClientPatch", null).ConfigureAwait(false);

            _stage = "downloading patch-3";
            SpellPatchClient.ServerPatch info = await _patch.InfoAsync(baseUrl).ConfigureAwait(false);
            if (!info.Exists || info.Sha1 is null) throw new InvalidOperationException("the server built no patch-3");
            string temp = SpellPatchClient.LocalPath(dataPath) + ".download";
            string got = await _patch.DownloadAsync(baseUrl, temp).ConfigureAwait(false);
            if (!string.Equals(got, info.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"patch download corrupt (sha1 {got[..8]} vs {info.Sha1[..8]})");

            _result = new Outcome(true, tempName, spell, got, temp, null, DateTime.UtcNow, childResults);
            Console.WriteLine($"[test-live] '{tempName}' -> #{spell}, patch-3 {got[..8]} downloaded");
        }
        catch (Exception ex)
        {
            string at = _stage;
            _result = new Outcome(false, tempName, 0, null, null, $"{at}: {ex.GetBaseException().Message}", DateTime.UtcNow);
            Console.WriteLine($"[test-live] '{tempName}' failed while {at}: {ex.GetBaseException().Message}");
        }
        finally { _stage = ""; }
    }
}
