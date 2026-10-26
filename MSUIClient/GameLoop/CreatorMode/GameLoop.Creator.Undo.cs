using System.Text.Json.Nodes;
using ImGuiNET;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// UNIVERSAL UNDO (SPELL_CREATOR_PLAN.md P3.7, decision D7).
//
// The handle undo (GameLoop.Creator.Handles.cs) only ever knew drag handles: a card, a dial, a
// rule, a composition slot, a sketch piece - none of them could be taken back. This undoes them
// ALL by undoing the DESIGN: the same snapshot the session export carries (CaptureCreatorDesign
// - rules, composition, area, sketches, every model's edits, audio, imports), so anything that
// survives a save is on the stack, and nothing needs its own undo code.
//
//   commit   twice a second while no mouse button is down and no sketch recompile is queued,
//            the design is captured; when it differs from the last commit, the last commit is
//            pushed. A drag, a slider sweep, a typed number is therefore ONE step (a gesture),
//            not sixty.
//   undo     the current state (committed first if it moved) goes to redo; the previous
//            snapshot is re-applied through ApplyCreatorDesign - the same code that reopens a
//            saved design, so an undo is exactly as faithful as a save.
//   redo     the mirror. Any new edit clears it. 64 deep each way.
//
// A snapshot keeps REFERENCES to the doc's imported texture and audio bytes (no copies): an
// undo across a re-import restores the old bytes too. Switching spells starts a fresh history.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private sealed record DesignSnapshot(string Json, Dictionary<string, byte[]> Imports, Dictionary<string, byte[]> Audio);

    private const int DesignUndoDepth = 64;
    private const double DesignCommitInterval = 0.5;
    private readonly List<DesignSnapshot> _designUndo = new();
    private readonly List<DesignSnapshot> _designRedo = new();
    private DesignSnapshot? _designCommitted;
    private object? _designCommittedDoc;
    private double _designNextCommit;

    private DesignSnapshot CaptureDesignSnapshot(CreatorSpellDoc doc) => new(
        CaptureCreatorDesign(doc).ToJsonString(),
        new Dictionary<string, byte[]>(doc.ImportedTextures, StringComparer.OrdinalIgnoreCase),
        doc.Audio.ToDictionary(a => a.Key.ToString().ToLowerInvariant(), a => a.Value.Bytes, StringComparer.OrdinalIgnoreCase));

    /// <summary>Driven from the creator update, after the sketch recompile.</summary>
    private void UpdateCreatorDesignUndo()
    {
        if (_creatorSpell is not { } doc)
        {
            _designCommitted = null; _designCommittedDoc = null;
            return;
        }
        if (!ReferenceEquals(doc, _designCommittedDoc))
        {
            // A different spell (or a reopen that was not ours): a fresh history.
            _designUndo.Clear(); _designRedo.Clear();
            _designCommitted = CaptureDesignSnapshot(doc);
            _designCommittedDoc = doc;
            return;
        }
        double now = NowSeconds();
        if (now < _designNextCommit) return;
        _designNextCommit = now + DesignCommitInterval;
        if (DesignGestureInProgress) return;
        CommitDesignIfChanged(doc);
    }

    private bool DesignGestureInProgress =>
        ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right) ||
        _gizmoDrag is not null || _sketchRebuildPending;

    private void CommitDesignIfChanged(CreatorSpellDoc doc)
    {
        DesignSnapshot current = CaptureDesignSnapshot(doc);
        if (_designCommitted is null) { _designCommitted = current; return; }
        if (current.Json == _designCommitted.Json) return;
        Push(_designUndo, _designCommitted);
        _designRedo.Clear();
        _designCommitted = current;
    }

    private static void Push(List<DesignSnapshot> stack, DesignSnapshot snapshot)
    {
        stack.Add(snapshot);
        if (stack.Count > DesignUndoDepth) stack.RemoveAt(0);
    }

    private bool CanUndoDesign => _designUndo.Count > 0;
    private bool CanRedoDesign => _designRedo.Count > 0;

    private void UndoCreatorDesign() => StepCreatorDesign(_designUndo, _designRedo, "Undid");
    private void RedoCreatorDesign() => StepCreatorDesign(_designRedo, _designUndo, "Redid");

    private void StepCreatorDesign(List<DesignSnapshot> from, List<DesignSnapshot> to, string verb)
    {
        if (_creatorSpell is not { } doc || _designCommitted is null) return;
        // An edit not yet committed (the user pressed Ctrl+Z within half a second) is a step too.
        if (from == _designUndo) CommitDesignIfChanged(doc);
        if (from.Count == 0) return;
        DesignSnapshot target = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, _designCommitted);
        string what = DescribeDesignChange(_designCommitted.Json, target.Json);
        if (JsonNode.Parse(target.Json) is not JsonObject design) return;
        string verdict = ApplyCreatorDesign(design, target.Imports, target.Audio);
        if (_creatorSpell is { } reopened)
        {
            _designCommitted = CaptureDesignSnapshot(reopened);
            _designCommittedDoc = reopened;
        }
        _creatorExportStatus = $"{verb}: {what}." + (verdict.Contains("missing", StringComparison.OrdinalIgnoreCase) || !verdict.StartsWith("Reopened", StringComparison.Ordinal)
            ? $" ({verdict})" : "");
        Console.WriteLine($"[creator-undo] {verb.ToLowerInvariant()} {what} ({_designUndo.Count} back, {_designRedo.Count} forward)");
    }

    /// <summary>"the sketch, the rules": which parts of the design differ - the undo button's label.</summary>
    private static string DescribeDesignChange(string a, string b)
    {
        if (JsonNode.Parse(a) is not JsonObject x || JsonNode.Parse(b) is not JsonObject y) return "the design";
        var parts = new List<string>();
        foreach (string key in x.Select(k => k.Key).Union(y.Select(k => k.Key)))
        {
            string left = x[key]?.ToJsonString() ?? "", right = y[key]?.ToJsonString() ?? "";
            if (left == right) continue;
            parts.Add(key switch
            {
                "sketches" => "the sketch",
                "rules" => "the rules",
                "composition" => "the composition",
                "models" => "a model's edits",
                "area" => "the area",
                "missile" => "the missile",
                "audio" => "the audio",
                "imports" => "an imported image",
                "plan" => "the plan",
                "spell" => "the spell's name",
                _ => key,
            });
        }
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z), unless a text field has the keyboard.</summary>
    private void HandleCreatorUndoKeys()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.WantTextInput || _gizmoDrag is not null) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            if (io.KeyShift) RedoCreatorDesign(); else UndoCreatorDesign();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Y, false)) RedoCreatorDesign();
    }
}
