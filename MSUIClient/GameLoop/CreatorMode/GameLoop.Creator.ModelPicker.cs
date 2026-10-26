using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The composition's model picker: every effect model in the game, reachable by
// the spell that uses it (search a spell, pick one of its models), by this
// spell's own models, or by a typed path.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private (SpellStage? Stage, int Slot)? _modelPickerTarget;
    private readonly byte[] _modelPickerSearchBuf = new byte[64];
    private readonly byte[] _modelPickerPathBuf = new byte[256];
    private List<SpellInfo>? _modelPickerResults;
    private bool _modelPickerDirty;
    private SpellInfo? _modelPickerSpell;
    private List<(string Label, string Path)> _modelPickerModels = new();

    private void OpenCreatorModelPicker(SpellStage? stage, int slot)
    {
        _modelPickerTarget = (stage, slot);
        _modelPickerSpell = null;
        _modelPickerModels.Clear();
        _modelPickerDirty = true;
    }

    private void PickCreatorModel(string path)
    {
        if (_creatorSpell is not { } doc || _modelPickerTarget is not { } target) return;
        if (target.Slot == CreatorTransplantPickerSlot)
        {
            _transplantDonor = path;        // the phase inspector now lists its emitters and trails
            _modelPickerTarget = null;
            return;
        }
        if (target.Slot == CreatorWorldPickerSlot && target.Stage is { } worldStage)
        {
            if (doc.Composition.TryGetValue(worldStage, out CreatorStageComposition? c) && EnsureCreatorModel(doc, path))
            {
                c.WorldEffect = path;
                ReapPresentedEffect();
                _creatorLoopNextAt = 0;
            }
        }
        else if (target.Stage is { } stage) SetCreatorSlot(doc, stage, target.Slot, path);
        else if (target.Slot is CreatorSparksPickerSlot or CreatorTrailPickerSlot) PickSketchExtraModel(target.Slot, path);
        else if (target.Slot == CreatorAreaPickerSlot)
        {
            if (EnsureCreatorModel(doc, path)) { doc.Area.Model = path; RestartCreatorArea(); }
        }
        else SetCreatorMissile(doc, path);
        _modelPickerTarget = null;
    }

    /// <summary>All effect models a spell's visual references, labelled by phase and slot.</summary>
    private List<(string Label, string Path)> CreatorModelsOfSpell(SpellInfo spell)
    {
        var result = new List<(string, string)>();
        if (_spellVisualCatalog?.TryGetStages(spell.VisualId, out SpellVisualStages stages) != true) return result;
        foreach (SpellStage stage in CreatorStages)
        {
            uint kitId = SpellVisualCatalog.KitFor(stages, stage);
            if (kitId == 0 || !_spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit)) continue;
            foreach (SpellVisualKitEffect effect in kit.Effects)
            {
                int slot = Array.IndexOf(SpellVisualCatalog.KitAttachmentIds, effect.AttachmentId);
                string where = slot >= 0 ? CreatorSlotNames[slot] : $"attachment {effect.AttachmentId}";
                result.Add(($"{CreatorStageName(stage)} / {where}: {Path.GetFileName(effect.ModelPath)}", effect.ModelPath));
            }
        }
        if (_spellVisualCatalog.MissilePath(stages) is { Length: > 0 } missile)
            result.Add(($"missile: {Path.GetFileName(missile)}", missile));
        return result;
    }

    private void DrawCreatorModelPicker()
    {
        if (_modelPickerTarget is not { } target || _creatorSpell is not { } doc) return;
        bool close = false;
        float cs = CreatorUiScale;
        ImGui.SetNextWindowSize(new Vector2(520f * cs, 560f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        if (ImGui.Begin("###creator-model-picker", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            string title = target.Stage is { } stage
                ? target.Slot == CreatorWorldPickerSlot
                    ? $"Pick a model: {CreatorStageName(stage)} / world (feet)"
                    : $"Pick a model: {CreatorStageName(stage)} / {CreatorSlotNames[target.Slot]}"
                : target.Slot switch
                {
                    CreatorSparksPickerSlot => "Copy sparks from a model",
                    CreatorTransplantPickerSlot => "Copy an emitter or trail from a model",
                    CreatorTrailPickerSlot => "Copy a trail from a model",
                    CreatorAreaPickerSlot => "Pick the area model",
                    _ => "Pick the missile model",
                };
            if (DrawCreatorPanelChrome(title)) close = true;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            ImGui.TextDisabled("THIS SPELL'S MODELS");
            foreach (CreatorModelDoc model in doc.Models.Values.ToList())
                if (ImGui.Selectable(Path.GetFileName(model.Path))) { PickCreatorModel(model.Path); close = true; }

            ImGui.Spacing();
            ImGui.TextDisabled("FROM ANOTHER SPELL");
            ImGui.SetNextItemWidth(220f * cs);
            if (ImGui.InputText("##model-picker-search", _modelPickerSearchBuf, (uint)_modelPickerSearchBuf.Length))
                _modelPickerDirty = true;
            ImGui.SameLine();
            ImGui.TextDisabled("search a spell by name or id");
            string query = BufToString(_modelPickerSearchBuf);
            if (_modelPickerDirty && _spellCatalog is not null)
            {
                _modelPickerDirty = false;
                _modelPickerResults = query.Length >= 2
                    ? (uint.TryParse(query, out uint asId)
                        ? _spellCatalog.Spells.Where(s => s.Id == asId).ToList()
                        : _spellCatalog.Spells
                            .Where(s => s.VisualId != 0 && s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id).Take(30).ToList())
                    : null;
            }
            if (_modelPickerResults is { Count: > 0 } results && query.Length >= 2)
            {
                if (BeginCreatorResults("##model-picker-spells", results.Count, 0.3f))
                {
                    foreach (SpellInfo spell in results)
                    {
                        string rank = spell.Rank.Length > 0 ? $" ({spell.Rank})" : "";
                        if (CreatorResultRow($"{spell.Id}  {spell.Name}{rank}", _modelPickerSpell?.Id == spell.Id))
                        {
                            _modelPickerSpell = spell;
                            _modelPickerModels = CreatorModelsOfSpell(spell);
                        }
                    }
                }
                EndCreatorResults();
            }
            if (_modelPickerSpell is { } chosen)
            {
                ImGui.TextDisabled($"{chosen.Name}'s models");
                if (_modelPickerModels.Count == 0) ImGui.TextDisabled("(no effect models)");
                foreach (var (label, path) in _modelPickerModels)
                    if (ImGui.Selectable(label)) { PickCreatorModel(path); close = true; }
            }

            ImGui.Spacing();
            ImGui.TextDisabled("OR A PATH");
            ImGui.SetNextItemWidth(320f * cs);
            ImGui.InputText("##model-picker-path", _modelPickerPathBuf, (uint)_modelPickerPathBuf.Length);
            ImGui.SameLine();
            if (ImGui.SmallButton("Use"))
            {
                string typed = SpellVisualCatalog.ModelPath(BufToString(_modelPickerPathBuf).Trim());
                if (typed.Length > 0 && _spellEffects?.ReadOriginalModel(typed) is not null)
                {
                    PickCreatorModel(typed);
                    close = true;
                }
                else _creatorExportStatus = $"No such model in the archives: {typed}";
            }
            CreatorHelp(@"Any M2 path in the game archives, e.g. Spells\Fireball_Missile_Low.m2.");

            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        if (close) _modelPickerTarget = null;
    }
}
