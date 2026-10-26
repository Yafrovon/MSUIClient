using System.Numerics;
using MSUIClient.Formats;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Net;
using MSUIClient.Creator.Sketch;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The spell SESSION: the design-phase product of creator mode. Each tuned spell
// is fully specified here - complete tuning metadata PLUS the patched M2 bytes,
// tinted BLPs and custom audio, base64-embedded, so nothing downstream depends
// on this machine's archives. MangosSuperUI's Spell Completer takes it from
// there for the data phase: real name, class, damage, ranks - and the unified
// patch build.
//
// A finished design leaves here two ways, and BOTH produce the same document:
//
//   PUSH  - POST straight to MangosSuperUI (/SpellCompleter/Push), where it lands
//           in the Completer's inbox ready to be named and costed. The direct
//           path, and the only one that carries the custom audio all the way.
//   FILE  - appended to one ongoing spell-session.json in the directory
//           MSUIClient was launched from. The offline record and the fallback
//           when this machine cannot reach the web app; the Completer still
//           accepts it by hand.
//
// Design phase lives here; data phase lives in MangosSuperUI.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    public const string CreatorSessionFileName = "spell-session.json";
    private const int CreatorSessionFormatVersion = 2;

    /// <summary>The launch directory, captured before anything can chdir - "the
    /// directory we launched MSUIClient from" is the contract for where the
    /// session file lands.</summary>
    private static readonly string CreatorSessionDir = Environment.CurrentDirectory;

    private readonly byte[] _creatorSessionNameBuf = new byte[48];
    private List<(string Name, int Models, int Audio)>? _creatorSessionEntries;   // null = reload

    private SpellPushClient? _spellPush;
    private SpellPushClient SpellPush => _spellPush ??= new SpellPushClient();

    private static string CreatorSessionPath => Path.Combine(CreatorSessionDir, CreatorSessionFileName);

    // ── file plumbing ────────────────────────────────────────────────────────

    private static JsonObject LoadCreatorSession()
    {
        try
        {
            if (File.Exists(CreatorSessionPath) &&
                JsonNode.Parse(File.ReadAllText(CreatorSessionPath)) is JsonObject existing &&
                existing["spells"] is JsonArray)
                return existing;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] session file unreadable, starting fresh: {ex.Message}");
        }
        return new JsonObject
        {
            ["format"] = "msui-spell-session",
            ["version"] = CreatorSessionFormatVersion,
            ["exportedBy"] = "MSUIClient creator mode",
            ["spells"] = new JsonArray(),
        };
    }

    private void SaveCreatorSession(JsonObject session)
    {
        File.WriteAllText(CreatorSessionPath, session.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        _creatorSessionEntries = null;   // re-list on next draw
    }

    private List<(string Name, int Models, int Audio)> CreatorSessionEntries()
    {
        if (_creatorSessionEntries is not null) return _creatorSessionEntries;
        _creatorSessionEntries = [];
        if (File.Exists(CreatorSessionPath) &&
            LoadCreatorSession()["spells"] is JsonArray spells)
            foreach (JsonNode? node in spells)
                if (node?["tempName"]?.GetValue<string>() is { Length: > 0 } name)
                    _creatorSessionEntries.Add((name,
                        (node["models"] as JsonArray)?.Count ?? 0,
                        (node["audio"] as JsonArray)?.Count ?? 0));
        return _creatorSessionEntries;
    }

    // ── the export itself ────────────────────────────────────────────────────

    /// <summary>Append (or replace, when the temp name already exists) one fully
    /// specified spell into the session file - the offline record of the design.</summary>
    private void AddCreatorSpellToSession(CreatorSpellDoc doc, string tempName)
    {
        if (BuildCreatorSpellEntry(doc, tempName) is not { } spellEntry) return;

        JsonObject session = LoadCreatorSession();
        session["version"] = CreatorSessionFormatVersion;
        var spells = (JsonArray)session["spells"]!;
        int existing = IndexOfSessionSpell(spells, tempName);
        if (existing >= 0) spells[existing] = spellEntry;
        else spells.Add(spellEntry);
        SaveCreatorSession(session);

        _creatorExportStatus = $"{(existing >= 0 ? "Replaced" : "Added")} '{tempName}' " +
            $"({CountOf(spellEntry, "models")} model(s), {CountOf(spellEntry, "tintedBlps")} tinted BLP(s), " +
            $"{CountOf(spellEntry, "audio")} audio track(s)) in {CreatorSessionPath}";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    /// <summary>Send this design straight to MangosSuperUI's Spell Completer, where
    /// it becomes a card on the page instead of a file someone has to carry. The
    /// SAME document the session file would have received - so a spell that was
    /// pushed and a spell that was uploaded are indistinguishable downstream.</summary>
    private void PushCreatorSpell(CreatorSpellDoc doc, string tempName)
    {
        if (BuildCreatorSpellEntry(doc, tempName) is not { } spellEntry) return;

        string url = SuiWebAppUrl;
        if (url.Length == 0)
        {
            // BeginPush("") would build "/SpellCompleter/Push" and HttpClient throws the
            // opaque "An invalid request URI was provided" - say the useful thing instead.
            _creatorExportStatus = "No MSUI Web connection - set the web app address on " +
                "the login screen (MSUI Web Connection), or in Data source below.";
            Console.WriteLine($"[creator] {_creatorExportStatus}");
            return;
        }
        SpellPush.BeginPush(url, spellEntry);
        _creatorExportStatus = $"Pushing '{tempName}' to {url}…";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    private static int CountOf(JsonObject entry, string key) => (entry[key] as JsonArray)?.Count ?? 0;

    /// <summary>Build one fully specified spell: the tuning metadata per model AND
    /// the patched M2 bytes AND the tinted BLPs AND the custom phase audio - the
    /// authoritative product of the design phase, and the single document both the
    /// session file and the push send onward. Null when there is nothing to send,
    /// with the reason already in the status line.</summary>
    private JsonObject? BuildCreatorSpellEntry(CreatorSpellDoc doc, string tempName)
    {
        // Sketched models are AUTHORED: there is no archive file behind their path, so they
        // always ship, always with bytes - recompiled one-shot with the author's dials on top
        // (BuildSketchExport). They used to be skipped: a sketch's preview original and working
        // copy are the same bytes, and only a model whose working copy differed was sent.
        var sketchBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var sketchTextures = new List<SketchWriter.SketchTextureFile>();
        foreach (SketchDoc sketch in doc.Sketches.Values)
        {
            if (sketch.Pieces.Count == 0) continue;
            if (BuildSketchExport(doc, sketch) is not { } built)
            {
                _creatorExportStatus = $"The {sketch.Stage} sketch could not be compiled - nothing was sent.";
                return null;
            }
            sketchBytes[SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage)] = built.Model;
            sketchTextures.AddRange(built.Textures);
        }

        var modified = doc.Models.Values
            .Where(m => m.Modified || sketchBytes.ContainsKey(m.Path)).ToList();
        bool composed = CreatorCompositionChanged(doc);
        bool ruled = doc.Rules is { } authoredRules && authoredRules.ChangedColumns.Any();
        if (modified.Count == 0 && doc.Audio.Count == 0 && !composed && !ruled)
        {
            _creatorExportStatus = "Nothing modified - tune the look, the composition or the audio before adding it.";
            return null;
        }

        var models = new JsonArray();
        foreach (var model in modified)
        {
            bool authored = sketchBytes.TryGetValue(model.Path, out byte[]? authoredBytes);
            bool byteModified = authored || !model.Working.AsSpan().SequenceEqual(model.Original);
            byte[] shipped = authoredBytes ?? model.Working;
            var entry = new JsonObject
            {
                ["path"] = model.Path,
                ["phases"] = CreatorModelPhases(doc, model),
                ["byteModified"] = byteModified,
                ["authored"] = authored ? "sketch" : null,
                // The patched model at its full fidelity - the consumer writes
                // these bytes (at a cloned path) instead of re-deriving them.
                ["m2Base64"] = byteModified ? Convert.ToBase64String(shipped) : null,
                ["tuning"] = JsonSerializer.SerializeToNode(CreatorModelTuningPayload(model)),
                ["textures"] = JsonSerializer.SerializeToNode(model.Textures.Select(t => new
                {
                    slotIndex = t.Index,
                    filename = t.Filename,
                    effective = EffectiveTexturePath(model, t.Index),
                    particleEmitters = t.ReferencedByEmitters,
                })),
            };
            models.Add(entry);
        }

        var audio = new JsonArray();
        foreach ((CreatorAudioCue cue, CreatorAudioTrack track) in doc.Audio
                     .OrderBy(pair => Array.IndexOf(CreatorAudioCueOrder, pair.Key)))
        {
            audio.Add(new JsonObject
            {
                ["cue"] = cue.ToString().ToLowerInvariant(),
                ["sourceSoundId"] = CreatorAuthoredSound(doc, cue),
                ["mpqPath"] = track.MpqPath,
                ["sourceFile"] = Path.GetFileName(track.SourcePath),
                ["volume"] = track.Volume,
                ["looping"] = cue == CreatorAudioCue.Missile || track.Looping,
                ["noDuplicates"] = track.NoDuplicates,
                ["soundType"] = track.SoundType,
                ["extraFlags"] = track.ExtraFlags,
                ["eax"] = track.Eax,
                ["minDistance"] = track.MinDistance,
                ["cutoffDistance"] = track.CutoffDistance,
                ["fileBase64"] = Convert.ToBase64String(track.Bytes),
            });
        }

        // Tinted BLPs, re-encoded once here so the completer needs no access to
        // this machine's game archives. Keyed by the ORIGINAL path; the consumer
        // decides the isolated path it re-homes them to.
        var blps = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The sketch's own images first, from the same one-shot compile as its model.
        foreach (SketchWriter.SketchTextureFile texture in sketchTextures)
        {
            if (!written.Add(texture.Path)) continue;
            blps.Add(new JsonObject
            {
                ["path"] = texture.Path,
                ["reason"] = "sketch",
                ["blpBase64"] = Convert.ToBase64String(texture.Blp),
            });
        }
        foreach (var model in modified)
            foreach (var (texIndex, tint) in model.TextureTints)
            {
                if (!tint.On) continue;
                string path = EffectiveTexturePath(model, texIndex);
                if (path.Length == 0 || !written.Add(path)) continue;
                byte[]? blp = BuildTintedBlp(path, PackArgb(tint.Color) & 0x00FFFFFF);
                if (blp is null)
                {
                    Console.WriteLine($"[creator] session: tinted BLP encode failed for {path}");
                    continue;
                }
                blps.Add(new JsonObject
                {
                    ["path"] = path,
                    ["tintColor"] = $"#{PackArgb(tint.Color) & 0xFFFFFF:x6}",
                    ["blpBase64"] = Convert.ToBase64String(blp),
                });
            }

        // Whole-model hue on mesh/ribbon art: those color tracks are keyframed
        // data the byte patcher cannot reach (in the workshop the hue rides the
        // renderers' color layers), so the export bakes it by hue-mapping each
        // mesh/ribbon BLP - the completer treats these exactly like tints. An
        // explicit user tint on the same image wins (the `written` set).
        foreach (var model in modified)
        {
            if (!model.HueShift) continue;
            foreach (var tex in model.Textures)
            {
                if (tex.ReferencedByEmitters.Count > 0) continue;
                string path = EffectiveTexturePath(model, tex.Index);
                if (path.Length == 0 || !written.Add(path)) continue;
                byte[]? blp = BuildTintedBlp(path, PackArgb(model.HueColor) & 0x00FFFFFF);
                if (blp is null)
                {
                    Console.WriteLine($"[creator] session: mesh-hue BLP encode failed for {path}");
                    continue;
                }
                blps.Add(new JsonObject
                {
                    ["path"] = path,
                    ["tintColor"] = $"#{PackArgb(model.HueColor) & 0xFFFFFF:x6}",
                    ["reason"] = "meshRibbonHue",
                    ["blpBase64"] = Convert.ToBase64String(blp),
                });
            }
        }

        // Imported art: an extra file at its custom path, exactly how tinted BLPs travel;
        // the M2 texture tables already name the path through the swap patch.
        foreach ((string path, byte[] bytes) in doc.ImportedTextures)
        {
            if (!written.Add(path)) continue;
            blps.Add(new JsonObject
            {
                ["path"] = path,
                ["reason"] = "import",
                ["blpBase64"] = Convert.ToBase64String(bytes),
            });
        }

        // The kit level (SPELL_CREATOR_IDE §2.8): only stages the design changed, each with
        // its animation and its complete slot list (an absent slot clears the kit field);
        // the missile when its model or scale changed.
        var composition = new JsonObject();
        foreach (SpellStage stage in CreatorStages)
        {
            if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? stageComposition) ||
                !stageComposition.Changed) continue;
            var slots = new JsonArray();
            for (int slot = 0; slot < 9; slot++)
                if (stageComposition.Slots[slot] is { Length: > 0 } slotPath)
                    slots.Add(new JsonObject
                    {
                        ["slot"] = slot,
                        ["modelPath"] = slotPath,
                        ["scale"] = stageComposition.Scales[slot],
                    });
            composition[CreatorStageName(stage)] = new JsonObject
            {
                ["animationId"] = stageComposition.AnimationId is { } anim ? anim : null,
                ["slots"] = slots,
                // Kit f14 (P4.4): 0 = no shake; the Completer writes it as given.
                ["cameraShake"] = stageComposition.Shake ?? 0u,
                // Kit f13 (P3.9): a picked game sound; absent = keep the kit's (custom audio still wins).
                ["sound"] = stageComposition.Sound != stageComposition.AuthoredSound && stageComposition.Sound is { } picked ? picked : null,
                // Kit f12: "" removes the world effect, a path sets it, absent keeps the source's.
                ["worldEffect"] = string.Equals(stageComposition.WorldEffect ?? "", stageComposition.AuthoredWorldEffect ?? "", StringComparison.OrdinalIgnoreCase)
                    ? null : stageComposition.WorldEffect ?? "",
                // Kit f15-34 (P4.4 body dials): all four lanes, positional; absent = keep the source's.
                ["charProcs"] = CreatorProcLanesJson(stageComposition),
            };
        }
        if (CreatorAreaJson(doc) is { } areaJson) composition["area"] = areaJson;
        if (CreatorMissileChanged(doc))
            composition["missile"] = new JsonObject
            {
                ["modelPath"] = CreatorEffectiveMissile(doc),
                ["scale"] = doc.MissileScale,
                ["sound"] = doc.MissileSound is { } missileSound ? missileSound : null,
            };

        // A new spell has NO source: its local id (9,000,001+) is a session counter, and the
        // Completer must not read it as "clone spell 9000001".
        bool isNew = IsCreatorNewSpellId(doc.Info.Id);
        return new JsonObject
        {
            ["tempName"] = tempName,
            ["designId"] = doc.DesignId,
            ["sourceSpellId"] = isNew ? 0u : doc.Info.Id,
            ["sourceSpellName"] = isNew ? "" : doc.Info.Name,
            ["sourceVisualId"] = isNew ? 0u : doc.Info.VisualId,
            ["localSpellId"] = doc.Info.Id,
            ["exportedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["models"] = models,
            ["tintedBlps"] = blps,
            ["audio"] = audio,
            ["composition"] = composition.Count > 0 ? composition : null,
            // What the spell DOES: spell_template columns that differ from the baseline
            // (SPELL_CREATOR_PLAN.md P1.7) - the Completer writes them as they are.
            ["rules"] = doc.Rules is { } rules && rules.ChangedColumns.Any() ? rules.ToJson() : null,
            // Everything needed to reopen this design editable (GameLoop.Creator.Design.cs).
            ["design"] = CaptureCreatorDesign(doc),
            // The totem it plants (P4.2): the Completer writes its creature row.
            ["actor"] = CreatorActorJson(doc.Actor),
            // Spells this one triggers, each a complete entry (P4.1); Test live completes them first.
            ["children"] = doc.Children.Count > 0
                ? new JsonArray(doc.Children.Where(c => c.Entry is not null)
                    .Select(c => (JsonNode)new JsonObject { ["localId"] = c.LocalId, ["entry"] = c.Entry!.DeepClone() }).ToArray())
                : null,
        };
    }

    private void RemoveCreatorSessionSpell(string tempName)
    {
        JsonObject session = LoadCreatorSession();
        var spells = (JsonArray)session["spells"]!;
        int index = IndexOfSessionSpell(spells, tempName);
        if (index < 0) return;
        spells.RemoveAt(index);
        SaveCreatorSession(session);
        _creatorExportStatus = $"Removed '{tempName}' from the session.";
    }

    private static int IndexOfSessionSpell(JsonArray spells, string tempName)
    {
        for (int i = 0; i < spells.Count; i++)
            if (string.Equals(spells[i]?["tempName"]?.GetValue<string>(), tempName,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Which phases of the spell draw this model ("precast+cast",
    /// "missile", "geometry (in host.m2)") - context the completer shows.</summary>
    private static string CreatorModelPhases(CreatorSpellDoc doc, CreatorModelDoc model)
    {
        if (doc.GeometryHosts.TryGetValue(model.Path, out string? host))
            return $"geometry (in {host})";
        string phases = string.Join("+", doc.PhaseModels
            .Where(p => string.Equals(p.Path, model.Path, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Stage.ToString().ToLowerInvariant()).Distinct());
        if (phases.Length == 0 &&
            string.Equals(CreatorEffectiveMissile(doc), model.Path, StringComparison.OrdinalIgnoreCase))
            phases = "missile";
        return phases;
    }

    // ── the SESSION area of the Export section ───────────────────────────────

    private void DrawCreatorSessionBody(CreatorSpellDoc doc)
    {
        float cs = CreatorUiScale;
        ImGui.Spacing();
        ImGui.TextDisabled("SESSION (hand this spell to the Spell Completer)");
        CreatorHelp("The design-phase product: give this tuned spell a TEMP name, then " +
            "PUSH it to MangosSuperUI - it appears on the Spell Completer page under " +
            "Gameplay Tuning > Spells, ready for the data phase: proper name, class, " +
            "damage, ranks, and the final patch.\n\n" +
            "'Add to session' writes the same design to a local file instead, for when " +
            "this machine cannot reach the web app - the Completer still accepts that " +
            "file by hand, but only a PUSH carries the custom audio through.\n\n" +
            "Either way the design is complete: tuning data, patched models, recolored " +
            "images and custom audio. Same temp name = replace.\n\n" +
            "Push target: " + (SuiWebAppUrl.Length > 0
                ? SuiWebAppUrl
                : "not set up - login screen > MSUI Web Connection") +
            "\nFile: " + CreatorSessionPath);

        ImGui.TextDisabled(SuiWebAppUrl.Length > 0
            ? $"Push target: {SuiWebAppUrl}"
            : "No MSUI Web connection - pushing is off. Set the address on the login " +
              "screen under 'MSUI Web Connection', or here in Data source.");

        ImGui.SetNextItemWidth(180f * cs);
        ImGui.InputText("##session-name", _creatorSessionNameBuf,
            (uint)_creatorSessionNameBuf.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Temp name for this spell in the session");
        ImGui.SameLine();
        string tempName = BufToString(_creatorSessionNameBuf).Trim();
        bool nameOk = tempName.Length > 0;
        bool pushing = SpellPush.Pushing;
        // Gated on the address being SET, never on a verify outcome: a probe can fail for
        // reasons a push would survive, and the user must stay able to just try.
        bool webOk = SuiWebAppUrl.Length > 0;
        if (!nameOk || pushing || !webOk) ImGui.BeginDisabled();
        if (CreatorButton("Push to Completer"))
            PushCreatorSpell(doc, tempName);
        if (!nameOk || pushing || !webOk) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(webOk
                ? $"Send this design to {SuiWebAppUrl}\n" +
                  "It shows up under Gameplay Tuning > Spells > Spell Completer.\n" +
                  "Address: login screen > MSUI Web Connection."
                : "No MSUI Web connection. Set the web app address on the login screen " +
                  "under 'MSUI Web Connection', or in Data source below.");

        ImGui.SameLine();
        if (!nameOk) ImGui.BeginDisabled();
        if (CreatorButton("Add to session"))
            AddCreatorSpellToSession(doc, tempName);
        if (!nameOk) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Write it to the local session file instead (no audio downstream)");

        DrawCreatorTestLiveRow(doc, tempName, nameOk, webOk);

        if (!nameOk)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("name it first");
        }
        else if (!webOk)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("no web connection");
        }

        // The push outcome, held until the next push replaces it. A failure names
        // the server, because the usual cause is that this machine cannot reach it.
        if (pushing)
        {
            ImGui.TextDisabled($"pushing to {SuiWebAppUrl}…");
        }
        else if (SpellPush.Result is { } push)
        {
            if (push.Ok)
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f),
                    $"'{push.TempName}' is waiting in the Spell Completer.");
            else
                ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.45f, 1f),
                    $"Push of '{push.TempName}' failed: {push.Error}");
            ImGui.SameLine();
            if (ImGui.SmallButton("dismiss")) SpellPush.ClearResult();
        }

        var entries = CreatorSessionEntries();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("Session is empty.");
            return;
        }
        foreach ((string name, int modelCount, int audioCount) in entries)
        {
            ImGui.PushID($"sess-{name}");
            if (ImGui.SmallButton("x"))
            {
                RemoveCreatorSessionSpell(name);
                ImGui.PopID();
                break;   // the list just changed - redraw next frame
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this spell from the session");
            ImGui.SameLine();
            if (ImGui.SmallButton("open"))
            {
                JsonObject session = LoadCreatorSession();
                var spells = (JsonArray)session["spells"]!;
                int index = IndexOfSessionSpell(spells, name);
                _creatorExportStatus = index >= 0 && spells[index] is JsonObject saved
                    ? OpenCreatorSessionEntry(saved)
                    : $"'{name}' is no longer in the session file.";
                Console.WriteLine($"[creator] {_creatorExportStatus}");
                ImGui.PopID();
                break;   // the spell just changed under the window - redraw next frame
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reopen this design in the workshop, editable: the same source, every " +
                                 "dial, the composition, the audio and the sketch pieces.");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name}  ({modelCount} model(s), {audioCount} audio)");
            ImGui.PopID();
        }
    }
}
