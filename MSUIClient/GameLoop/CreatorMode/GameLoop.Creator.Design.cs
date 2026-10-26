using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MSUIClient.Creator;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// THE DESIGN — everything an author decided about a spell, as one JSON object
// (shared_docs/SPELL_CREATOR_PLAN.md P0.2/P0.3, decision D7).
//
// A CreatorSpellDoc is a LIVE object: parsed model snapshots, renderer overrides,
// the compiled sketch bytes. Almost all of that is derived. What is not derived is
// small - which spell, which parts, which model in which slot, every dial turned on
// every model, the sketch pieces - and that is what this file captures and
// restores. The session entry carries it as its `design` block, beside the bytes
// the Completer needs; reopening a saved spell is "start from the same source,
// then apply the design".
//
// What the design does NOT hold, on purpose:
//   • compiled sketch M2s and their BLPs (output: recompiled from `sketches`);
//   • imported images and imported audio BYTES (the entry already ships them, in
//     `tintedBlps` reason import and `audio[].fileBase64`; the design names them).
// So a design is small enough to snapshot on every edit, which is what the undo
// stack will do with it (P3.7).
//
// LAW: Apply(Capture(doc)) on a fresh doc from the same source reproduces the
// same composed preview and the same exported bytes. Capture never reads a
// renderer; Apply only writes through the same setters the dials use.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private const int CreatorDesignVersion = 1;

    // ── capture ─────────────────────────────────────────────────────────────

    private JsonObject CaptureCreatorDesign(CreatorSpellDoc doc)
    {
        bool isNew = IsCreatorNewSpellId(doc.Info.Id);
        var design = new JsonObject
        {
            ["version"] = CreatorDesignVersion,
            ["designId"] = doc.DesignId,
            ["spell"] = new JsonObject
            {
                ["isNew"] = isNew,
                ["id"] = doc.Info.Id,
                ["name"] = doc.Info.Name,
            },
            ["plan"] = new JsonObject
            {
                ["started"] = doc.PlanStarted,
                ["stages"] = new JsonArray(doc.PlannedStages.OrderBy(s => s)
                    .Select(s => (JsonNode)CreatorStageName(s)).ToArray()),
                ["missile"] = doc.PlannedMissile,
            },
        };

        // The COMPLETE composition of every stage, not only the changed ones: a design is a
        // statement of what the spell is, and "unchanged from the source" is only true until
        // the source is edited.
        var composition = new JsonObject();
        foreach (SpellStage stage in CreatorStages)
        {
            if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? c)) continue;
            var slots = new JsonArray();
            for (int slot = 0; slot < 9; slot++)
                if (c.Slots[slot] is { Length: > 0 } path)
                    slots.Add(new JsonObject { ["slot"] = slot, ["modelPath"] = path, ["scale"] = c.Scales[slot] });
            composition[CreatorStageName(stage)] = new JsonObject
            {
                ["animationId"] = c.AnimationId is { } anim ? anim : null,
                ["slots"] = slots,
                ["cameraShake"] = c.Shake is { } shake ? shake : null,
                ["sound"] = c.Sound is { } sound ? sound : null,
                ["worldEffect"] = c.WorldEffect,
                ["body"] = CreatorBodyJson(c.Body),
            };
        }
        design["composition"] = composition;
        design["missile"] = new JsonObject
        {
            ["override"] = doc.MissileOverride,
            ["scale"] = doc.MissileScale,
            ["sound"] = doc.MissileSound is { } missileSound ? missileSound : null,
        };
        if (CreatorAreaJson(doc) is { } area) design["area"] = area;

        var models = new JsonArray();
        foreach (CreatorModelDoc model in doc.Models.Values)
            if (CaptureCreatorModelEdits(model) is { } edits) models.Add(edits);
        design["models"] = models;

        design["imports"] = new JsonArray(doc.ImportedTextures.Keys.Select(p => (JsonNode)p).ToArray());

        var audio = new JsonArray();
        foreach ((CreatorAudioCue cue, CreatorAudioTrack track) in doc.Audio)
            audio.Add(new JsonObject
            {
                ["cue"] = cue.ToString().ToLowerInvariant(),
                ["mpqPath"] = track.MpqPath,
                ["sourcePath"] = track.SourcePath,
                ["volume"] = track.Volume,
                ["looping"] = track.Looping,
                ["noDuplicates"] = track.NoDuplicates,
                ["soundType"] = track.SoundType,
                ["extraFlags"] = track.ExtraFlags,
                ["eax"] = track.Eax,
                ["minDistance"] = track.MinDistance,
                ["cutoffDistance"] = track.CutoffDistance,
            });
        design["audio"] = audio;

        var sketches = new JsonObject();
        foreach ((SpellStage stage, SketchDoc sketch) in doc.Sketches)
            if (sketch.Pieces.Count > 0) sketches[CreatorStageName(stage)] = SketchJson.ToJson(sketch);
        design["sketches"] = sketches;
        if (doc.Rules is { } rules && rules.ChangedColumns.Any()) design["rules"] = rules.ToJson();
        if (doc.Children.Count > 0) design["children"] = CaptureCreatorChildren(doc);
        if (CreatorActorJson(doc.Actor) is { } actor) design["actor"] = actor;
        return design;
    }

    /// <summary>One model's authored edits, or null when the model is untouched. Every
    /// collection the rebuild reads, by name; the patch records serialize as themselves.</summary>
    /// <summary>Has the author turned anything on this model? (The same list the design
    /// records; a sketch recompile uses it to decide whether its dials must be re-applied.)</summary>
    private static bool CreatorModelHasEdits(CreatorModelDoc m) =>
        m.HueShift || m.RateMul != 1f || m.ScaleMul != 1f || m.LifeMul != 1f ||
        m.SpeedMul != 1f || m.GravityAdd != 0f ||
        m.Edits.Count > 0 || m.TextureSwaps.Count > 0 ||
        m.TextureHues.Any(h => h.Value.On) || m.TextureTints.Any(t => t.Value.On) ||
        m.DisabledEmitters.Count > 0 || m.AddedEmitters.Count > 0 ||
        m.HiddenSubmeshes.Count > 0 || m.MeshEdits.Count > 0 ||
        m.DisabledRibbons.Count > 0 || m.RibbonEdits.Count > 0 || m.BoneEdits.Count > 0 ||
        m.AddedRibbons.Count > 0 || m.AddedLayers.Count > 0 || m.TrackEdits.Count > 0 || m.Transplants.Count > 0;

    private static JsonObject? CaptureCreatorModelEdits(CreatorModelDoc m)
    {
        if (!CreatorModelHasEdits(m)) return null;

        static JsonNode? Ser<T>(T value) => JsonSerializer.SerializeToNode(value, CreatorJson.Options);
        static JsonArray Hues(Dictionary<int, CreatorTexHue> hues) => new(hues
            .Where(h => h.Value.On)
            .Select(h => (JsonNode)new JsonObject { ["slot"] = h.Key, ["color"] = Ser(h.Value.Color) })
            .ToArray());

        return new JsonObject
        {
            ["path"] = m.Path,
            ["dials"] = new JsonObject
            {
                ["hueShift"] = m.HueShift,
                ["hueColor"] = Ser(m.HueColor),
                ["rateMul"] = m.RateMul,
                ["scaleMul"] = m.ScaleMul,
                ["lifeMul"] = m.LifeMul,
                ["speedMul"] = m.SpeedMul,
                ["gravityAdd"] = m.GravityAdd,
            },
            ["edits"] = Ser(m.Edits.Values.OrderBy(e => e.EmitterIndex).ToList()),
            ["textureSwaps"] = new JsonArray(m.TextureSwaps.OrderBy(s => s.Key)
                .Select(s => (JsonNode)new JsonObject { ["slot"] = s.Key, ["path"] = s.Value }).ToArray()),
            ["textureHues"] = Hues(m.TextureHues),
            ["textureTints"] = Hues(m.TextureTints),
            ["disabledEmitters"] = Ser(m.DisabledEmitters.OrderBy(i => i).ToList()),
            ["addedEmitters"] = new JsonArray(m.AddedEmitters
                .Select(a => (JsonNode)new JsonObject { ["sourceIndex"] = a.SourceIndex, ["textureSlot"] = a.TextureSlot })
                .ToArray()),
            ["hiddenSubmeshes"] = Ser(m.HiddenSubmeshes.OrderBy(i => i).ToList()),
            ["meshEdits"] = Ser(m.MeshEdits.Values.OrderBy(e => e.BatchIndex).ToList()),
            ["disabledRibbons"] = Ser(m.DisabledRibbons.OrderBy(i => i).ToList()),
            ["ribbonEdits"] = Ser(m.RibbonEdits.Values.OrderBy(e => e.RibbonIndex).ToList()),
            ["boneEdits"] = Ser(m.BoneEdits.Values.OrderBy(e => e.BoneIndex).ToList()),
            ["addedRibbons"] = Ser(m.AddedRibbons.ToList()),
            ["addedLayers"] = Ser(m.AddedLayers.ToList()),
            ["transplants"] = m.Transplants.Count == 0 ? null : new JsonArray(m.Transplants.Select(t => (JsonNode)new JsonObject
            {
                ["donor"] = t.Donor, ["index"] = t.Index, ["ribbon"] = t.Ribbon,
            }).ToArray()),
            ["trackEdits"] = m.TrackEdits.Count == 0 ? null : new JsonObject(m.TrackEdits.OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => new KeyValuePair<string, JsonNode?>(t.Key, new JsonObject
                {
                    ["interp"] = t.Value.Interpolation,
                    ["global"] = t.Value.GlobalSequence,
                    ["ranges"] = t.Value.HadRanges,
                    ["times"] = new JsonArray(t.Value.Times.Select(x => (JsonNode)x).ToArray()),
                    ["values"] = new JsonArray(t.Value.Values.Select(v => (JsonNode)new JsonArray(v.Select(f => (JsonNode)f).ToArray())).ToArray()),
                }))),
        };
    }

    // ── apply ───────────────────────────────────────────────────────────────

    /// <summary>Open a saved session entry as the current spell: the same source (or a new
    /// spell with the same local id), then the design on top. Bytes the design names come
    /// from the entry itself. Returns a one-line verdict for the status bar.</summary>
    private string OpenCreatorSessionEntry(JsonObject entry)
    {
        if (entry["design"] is not JsonObject design)
            return "This entry was saved before designs were recorded - it can be pushed, not reopened.";
        if ((design["version"]?.GetValue<int>() ?? 0) is < 1 or > CreatorDesignVersion)
            return "This entry was saved by a newer build.";

        var importBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (entry["tintedBlps"] is JsonArray blps)
            foreach (JsonNode? blp in blps)
                if (blp?["reason"]?.GetValue<string>() == "import" &&
                    blp["path"]?.GetValue<string>() is { Length: > 0 } path &&
                    blp["blpBase64"]?.GetValue<string>() is { Length: > 0 } b64)
                    importBytes[path] = Convert.FromBase64String(b64);
        var audioBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (entry["audio"] is JsonArray audio)
            foreach (JsonNode? track in audio)
                if (track?["cue"]?.GetValue<string>() is { Length: > 0 } cue &&
                    track["fileBase64"]?.GetValue<string>() is { Length: > 0 } b64)
                    audioBytes[cue] = Convert.FromBase64String(b64);

        string verdict = ApplyCreatorDesign(design, importBytes, audioBytes);
        if (entry["tempName"]?.GetValue<string>() is { Length: > 0 } tempName)
        {
            Array.Clear(_creatorSessionNameBuf);
            byte[] name = System.Text.Encoding.UTF8.GetBytes(tempName);
            Array.Copy(name, _creatorSessionNameBuf, Math.Min(name.Length, _creatorSessionNameBuf.Length - 1));
        }
        return verdict;
    }

    private string ApplyCreatorDesign(JsonObject design,
        IReadOnlyDictionary<string, byte[]> importBytes, IReadOnlyDictionary<string, byte[]> audioBytes)
    {
        // 1. The source: a real spell from the archives, or a new spell under its old local id.
        JsonNode? spell = design["spell"];
        bool isNew = spell?["isNew"]?.GetValue<bool>() ?? false;
        uint id = spell?["id"]?.GetValue<uint>() ?? 0;
        string name = spell?["name"]?.GetValue<string>() ?? "spell";
        if (isNew) StartNewCreatorSpell(name, id);
        else if (_spellCatalog?.TryGet(id, out SpellInfo info) == true) SelectCreatorSpell(info);
        else return $"Spell {id} is not in this client's Spell.dbc - cannot reopen.";
        if (_creatorSpell is not { } doc) return "The spell could not be opened.";
        if (design["designId"]?.GetValue<string>() is { Length: > 0 } designId) doc.DesignId = designId;

        int problems = 0;

        // 1b. Its children (designs + last exports), before the rules that name them.
        ApplyCreatorChildren(doc, design["children"]);
        doc.Actor = ApplyCreatorActorJson(design["actor"]);

        // 2. The plan.
        if (design["plan"] is JsonObject plan)
        {
            doc.PlanStarted = plan["started"]?.GetValue<bool>() ?? false;
            doc.PlannedMissile = plan["missile"]?.GetValue<bool>() ?? false;
            doc.PlannedStages.Clear();
            if (plan["stages"] is JsonArray stages)
                foreach (JsonNode? s in stages)
                    if (Enum.TryParse(s?.GetValue<string>(), ignoreCase: true, out SpellStage stage))
                        doc.PlannedStages.Add(stage);
        }

        // 3. Imported art, before anything names it.
        if (design["imports"] is JsonArray imports)
            foreach (JsonNode? node in imports)
                if (node?.GetValue<string>() is { Length: > 0 } path)
                {
                    if (!importBytes.TryGetValue(path, out byte[]? bytes)) { problems++; continue; }
                    _mpq.SetOverride(path, bytes);
                    doc.ImportedTextures[path] = bytes;
                }

        // 4. Sketches: recompile, so the custom model paths exist before the composition names
        //    them. Placement comes from the composition below, never from the window's slot.
        if (design["sketches"] is JsonObject sketches)
            foreach ((string stageName, JsonNode? node) in sketches)
            {
                if (!Enum.TryParse(stageName, ignoreCase: true, out SpellStage stage) ||
                    SketchJson.FromJson(node) is not { } sketch) { problems++; continue; }
                sketch.Stage = CreatorStageName(stage);
                doc.Sketches[stage] = sketch;
                if (CompileSketchModel(doc, sketch, loopForEditing: false) is null) problems++;
            }

        // 5. The composition, every stage complete.
        if (design["composition"] is JsonObject composition)
            foreach ((string stageName, JsonNode? node) in composition)
            {
                if (!Enum.TryParse(stageName, ignoreCase: true, out SpellStage stage) ||
                    !doc.Composition.TryGetValue(stage, out CreatorStageComposition? c) ||
                    node is not JsonObject stageNode) continue;
                c.AnimationId = stageNode["animationId"]?.GetValue<ushort>();
                c.Shake = stageNode["cameraShake"]?.GetValue<uint>() is uint shake && shake != 0 ? shake : null;
                c.Sound = stageNode["sound"]?.GetValue<uint>() is uint sound && sound != 0 ? sound : c.AuthoredSound;
                c.WorldEffect = stageNode["worldEffect"]?.GetValue<string>() is { Length: > 0 } world && EnsureCreatorModel(doc, world) ? world : null;
                c.Body = stageNode["body"] is JsonObject bodyNode ? CreatorBodyFromJson(bodyNode) : c.AuthoredBody;
                for (int i = 0; i < 9; i++) { c.Slots[i] = null; c.Scales[i] = 1f; }
                if (stageNode["slots"] is JsonArray slots)
                    foreach (JsonNode? slotNode in slots)
                    {
                        int slot = slotNode?["slot"]?.GetValue<int>() ?? -1;
                        string? path = slotNode?["modelPath"]?.GetValue<string>();
                        if (slot is < 0 or > 8 || path is not { Length: > 0 }) continue;
                        if (!EnsureCreatorModel(doc, path)) { problems++; continue; }
                        c.Slots[slot] = path;
                        c.Scales[slot] = slotNode!["scale"]?.GetValue<float>() ?? 1f;
                    }
            }
        if (design["missile"] is JsonObject missile)
        {
            string? path = missile["override"]?.GetValue<string>();
            if (path is { Length: > 0 } && !EnsureCreatorModel(doc, path)) { problems++; path = null; }
            doc.MissileOverride = path is { Length: > 0 } ? path : null;
            doc.MissileScale = missile["scale"]?.GetValue<float>() ?? 1f;
            doc.MissileSound = missile["sound"]?.GetValue<uint>() is uint ms && ms != 0 ? ms : null;
        }
        RefreshCreatorPhaseModels(doc);
        if (design["area"] is JsonObject areaNode)
        {
            if (areaNode["modelPath"]?.GetValue<string>() is { Length: > 0 } areaModel && !EnsureCreatorModel(doc, areaModel))
                problems++;
            ApplyCreatorAreaJson(doc, areaNode);
        }

        // 6. Every model's edits, then one rebuild each.
        if (design["models"] is JsonArray models)
            foreach (JsonNode? node in models)
            {
                if (node is not JsonObject edits || edits["path"]?.GetValue<string>() is not { Length: > 0 } path)
                    continue;
                if (!doc.Models.TryGetValue(path, out CreatorModelDoc? model) &&
                    !(EnsureCreatorModel(doc, path) && doc.Models.TryGetValue(path, out model)))
                { problems++; continue; }
                ApplyCreatorModelEdits(model!, edits);
                RebuildCreatorModel(model!, replay: false);
                foreach ((int texIndex, CreatorTexHue tint) in model!.TextureTints)
                    if (tint.On) SetCreatorTextureTint(model, texIndex, tint.Color);
            }

        // 7. Audio.
        if (design["audio"] is JsonArray audio)
            foreach (JsonNode? node in audio)
            {
                if (node?["cue"]?.GetValue<string>() is not { Length: > 0 } cueName ||
                    !Enum.TryParse(cueName, ignoreCase: true, out CreatorAudioCue cue)) continue;
                if (!audioBytes.TryGetValue(cueName, out byte[]? bytes)) { problems++; continue; }
                doc.Audio[cue] = new CreatorAudioTrack
                {
                    SourcePath = node["sourcePath"]?.GetValue<string>() ?? "",
                    MpqPath = node["mpqPath"]?.GetValue<string>() ?? "",
                    Bytes = bytes,
                    Volume = node["volume"]?.GetValue<float>() ?? 1f,
                    Looping = node["looping"]?.GetValue<bool>() ?? false,
                    NoDuplicates = node["noDuplicates"]?.GetValue<bool>() ?? false,
                    SoundType = node["soundType"]?.GetValue<uint>() ?? 1u,
                    ExtraFlags = node["extraFlags"]?.GetValue<uint>() ?? 0u,
                    Eax = node["eax"]?.GetValue<uint>() ?? 0u,
                    MinDistance = node["minDistance"]?.GetValue<float>() ?? 10f,
                    CutoffDistance = node["cutoffDistance"]?.GetValue<float>() ?? 80f,
                };
            }

        // 8. The rules, over the doc's own baseline.
        if (design["rules"] is JsonObject savedRules && CreatorRules(doc) is { } rules)
            rules.ApplyJson(savedRules);

        // Placement for the sketch window: the stage and slot a sketch already occupies.
        foreach ((SpellStage stage, SketchDoc sketch) in doc.Sketches)
        {
            string path = SketchDoc.ModelPath((int)doc.Info.Id, doc.Info.Name, sketch.Stage);
            if (doc.Composition.TryGetValue(stage, out CreatorStageComposition? c))
            {
                int slot = Array.FindIndex(c.Slots, s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase));
                if (slot >= 0) { _sketchStage = stage; _sketchSlot = slot; break; }
            }
        }
        if (doc.Sketches.Values.Any(s => s.Pieces.Count > 0)) SketchChanged();   // recompile for the editing loop

        ReapPresentedEffect();
        _creatorLoopNextAt = 0;
        SpellIdeSelectionReset();
        string pieces = doc.Sketches.Values.Sum(s => s.Pieces.Count) is var n and > 0 ? $", {n} sketch piece(s)" : "";
        return problems == 0
            ? $"Reopened \"{name}\" ({doc.Models.Count} model(s){pieces})."
            : $"Reopened \"{name}\" with {problems} part(s) missing - see msui-console.log.";
    }

    private static void ApplyCreatorModelEdits(CreatorModelDoc m, JsonObject e)
    {
        static T? De<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(CreatorJson.Options);

        if (e["dials"] is JsonObject d)
        {
            m.HueShift = d["hueShift"]?.GetValue<bool>() ?? false;
            m.HueColor = De<Vector3?>(d["hueColor"]) ?? m.HueColor;
            m.RateMul = d["rateMul"]?.GetValue<float>() ?? 1f;
            m.ScaleMul = d["scaleMul"]?.GetValue<float>() ?? 1f;
            m.LifeMul = d["lifeMul"]?.GetValue<float>() ?? 1f;
            m.SpeedMul = d["speedMul"]?.GetValue<float>() ?? 1f;
            m.GravityAdd = d["gravityAdd"]?.GetValue<float>() ?? 0f;
        }

        m.Edits.Clear();
        foreach (EmitterPatch patch in De<List<EmitterPatch>>(e["edits"]) ?? []) m.Edits[patch.EmitterIndex] = patch;

        m.TextureSwaps.Clear();
        if (e["textureSwaps"] is JsonArray swaps)
            foreach (JsonNode? s in swaps)
                if (s?["slot"]?.GetValue<int>() is { } slot && s["path"]?.GetValue<string>() is { Length: > 0 } path)
                    m.TextureSwaps[slot] = path;

        static void Hues(Dictionary<int, CreatorTexHue> into, JsonNode? node)
        {
            into.Clear();
            if (node is not JsonArray list) return;
            foreach (JsonNode? h in list)
                if (h?["slot"]?.GetValue<int>() is { } slot)
                    into[slot] = new CreatorTexHue { On = true, Color = De<Vector3?>(h["color"]) ?? Vector3.One };
        }
        Hues(m.TextureHues, e["textureHues"]);
        Hues(m.TextureTints, e["textureTints"]);

        m.DisabledEmitters.Clear();
        foreach (int i in De<List<int>>(e["disabledEmitters"]) ?? []) m.DisabledEmitters.Add(i);

        m.AddedEmitters.Clear();
        if (e["addedEmitters"] is JsonArray added)
            foreach (JsonNode? a in added)
                m.AddedEmitters.Add(new CreatorAddedEmitter
                {
                    SourceIndex = a?["sourceIndex"]?.GetValue<int>() ?? 0,
                    TextureSlot = a?["textureSlot"]?.GetValue<int>() ?? 0,
                });

        m.HiddenSubmeshes.Clear();
        foreach (int i in De<List<int>>(e["hiddenSubmeshes"]) ?? []) m.HiddenSubmeshes.Add(i);
        m.MeshEdits.Clear();
        foreach (MeshPatch patch in De<List<MeshPatch>>(e["meshEdits"]) ?? []) m.MeshEdits[patch.BatchIndex] = patch;
        m.DisabledRibbons.Clear();
        foreach (int i in De<List<int>>(e["disabledRibbons"]) ?? []) m.DisabledRibbons.Add(i);
        m.RibbonEdits.Clear();
        foreach (RibbonPatch patch in De<List<RibbonPatch>>(e["ribbonEdits"]) ?? []) m.RibbonEdits[patch.RibbonIndex] = patch;
        m.BoneEdits.Clear();
        foreach (BonePatch patch in De<List<BonePatch>>(e["boneEdits"]) ?? []) m.BoneEdits[patch.BoneIndex] = patch;
        m.AddedRibbons.Clear();
        m.AddedRibbons.AddRange(De<List<int>>(e["addedRibbons"]) ?? []);
        m.AddedLayers.Clear();
        m.AddedLayers.AddRange(De<List<int>>(e["addedLayers"]) ?? []);
        m.Transplants.Clear();
        if (e["transplants"] is JsonArray transplants)
            foreach (JsonNode? t in transplants)
                if (t?["donor"]?.GetValue<string>() is { Length: > 0 } donor)
                    m.Transplants.Add(new CreatorTransplant(donor, t["index"]?.GetValue<int>() ?? 0, t["ribbon"]?.GetValue<bool>() ?? false));
        m.TrackEdits.Clear();
        if (e["trackEdits"] is JsonObject trackEdits)
            foreach ((string id, JsonNode? node) in trackEdits)
            {
                if (node is not JsonObject t) continue;
                var keys = new Creator.TrackKeys
                {
                    Interpolation = t["interp"]?.GetValue<ushort>() ?? 1,
                    GlobalSequence = t["global"]?.GetValue<short>() ?? -1,
                    HadRanges = t["ranges"]?.GetValue<bool>() ?? true,
                };
                if (t["times"] is JsonArray times) keys.Times.AddRange(times.Select(x => x?.GetValue<uint>() ?? 0));
                if (t["values"] is JsonArray values)
                    keys.Values.AddRange(values.Select(v => (v as JsonArray)?.Select(f => f?.GetValue<float>() ?? 0f).ToArray() ?? Array.Empty<float>()));
                if (keys.Times.Count == keys.Values.Count) m.TrackEdits[id] = keys;
            }
    }
}
