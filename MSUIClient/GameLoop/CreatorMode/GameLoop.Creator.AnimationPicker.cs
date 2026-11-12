using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The caster-animation picker (shared_docs/SPELL_IDE_MAP.md, 2026-09-19).
//
// Owner, 2026-09-19: "If I try to take an existing spell and modify it, my
// animation is still the base spell's animation ... how do we choose what our
// animation should be (from existing of course)?"
//
// The log answered the first half: authored=57 requested=57 played=57 on every
// cast - the animation was never changed, because the only control was a raw
// AnimationData id typed into an InputInt under Composition, with "52 SpellCast
// (omni), 57 ReadySpellOmni ..." in a tooltip. Nobody chooses a swing by number.
//
// This is the second half: every AnimationData.dbc row BY NAME, split into the
// ones the caster's model actually carries a sequence for (a kit that names an
// animation the model lacks plays nothing) and the rest; hover a row and the
// body performs it, click and the stage keeps it. It writes the same
// `CreatorStageComposition.AnimationId` the InputInt wrote, so the preview
// (PresentSpellEffect -> CreatorComposedKit) and the export (the session's
// composition block, kit field 2 in the Completer) are unchanged.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private SpellStage? _animPickerStage;
    private readonly byte[] _animPickerSearchBuf = new byte[64];
    private int _animPickerHovered = -1;
    private bool _animPickerHoverSeen;

    /// <summary>The name a stage's caster animation shows on a button: "57 ReadySpellOmni",
    /// "none", with "(authored)" when it is still the source spell's.</summary>
    private string CreatorAnimationLabel(CreatorStageComposition composition)
    {
        if (composition.AnimationId is not { } id || id == 0) return "none";
        string name = _animationData?.Name(id) ?? M2Animator.AnimationName(id);
        return $"{id} {name}";
    }

    /// <summary>The button that opens the picker for one stage: the current animation by
    /// name, gold when it differs from what the source spell authored.</summary>
    private void DrawCreatorAnimationButton(SpellStage stage, CreatorStageComposition composition, float width)
    {
        string label = CreatorAnimationLabel(composition);
        bool changed = composition.AnimationId != composition.AuthoredAnimation;
        if (changed) ImGui.PushStyleColor(ImGuiCol.Text, SpellIdeGold);
        if (LessonMarked($"anim.{stage}", ImGui.Button($"{label}##anim-{stage}", new Vector2(width, 0f))))
            OpenCreatorAnimationPicker(stage);
        if (changed) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the CASTER's body does during this part of the spell - pick " +
                             "any animation by name (hover one to see it performed).\n" +
                             $"Authored by the source spell: {(composition.AuthoredAnimation is { } a && a != 0 ? $"{a} {_animationData?.Name(a) ?? M2Animator.AnimationName(a)}" : "none")}.");
    }

    private void OpenCreatorAnimationPicker(SpellStage stage)
    {
        _animPickerStage = stage;
        _animPickerHovered = -1;
        Array.Clear(_animPickerSearchBuf);
    }

    /// <summary>Set a stage's caster animation and show it at once.</summary>
    private void SetCreatorAnimation(SpellStage stage, ushort? animationId)
    {
        if (_creatorSpell is not { } doc ||
            !doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) return;
        composition.AnimationId = animationId is { } id && id != 0 ? id : null;
        ReapPresentedEffect();
        _creatorLoopNextAt = 0;
        PreviewCreatorAnimation(stage, composition.AnimationId);
    }

    /// <summary>Perform an animation on the acting body right now, the way the stage would:
    /// a held pose for precast/channel, a one-shot for the rest.</summary>
    private void PreviewCreatorAnimation(SpellStage stage, ushort? animationId)
    {
        if (_character is null || ControlledBodyTacticallyFrozen) return;
        if (stage is SpellStage.Precast or SpellStage.Channel) _character.BeginSpellVisual(animationId);
        else _character.ReleaseSpellVisual(animationId);
    }

    private void DrawCreatorAnimationPicker()
    {
        if (_animPickerStage is not { } stage || _creatorSpell is not { } doc ||
            !doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition))
        {
            _animPickerStage = null;
            return;
        }
        bool close = false;
        float cs = CreatorUiScale;
        _animPickerHoverSeen = false;
        ImGui.SetNextWindowSize(new Vector2(460f * cs, 560f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        if (ImGui.Begin("###creator-anim-picker", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome($"Caster animation: {CreatorStageName(stage)}")) close = true;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            CreatorHelp("The body's animation for this part of the spell. Hover a row to watch the " +
                        "caster perform it; click to keep it. The list is every animation in the " +
                        "game (AnimationData.dbc); the first group is the ones THIS body's model " +
                        "actually has - an animation the model lacks plays nothing.");

            ImGui.SetNextItemWidth(220f * cs);
            ImGui.InputText("##anim-picker-search", _animPickerSearchBuf, (uint)_animPickerSearchBuf.Length);
            ImGui.SameLine();
            ImGui.TextDisabled("filter by name or id");
            string query = BufToString(_animPickerSearchBuf).Trim();

            // The two standing choices first.
            bool noneNow = composition.AnimationId is null or 0;
            if (ImGui.Selectable("none - the body keeps doing what it was doing", noneNow))
            {
                SetCreatorAnimation(stage, null);
                close = true;
            }
            if (composition.AuthoredAnimation is { } authored && authored != 0)
            {
                string authoredName = _animationData?.Name(authored) ?? M2Animator.AnimationName(authored);
                if (ImGui.Selectable($"{authored} {authoredName}  (what the source spell authored)",
                        composition.AnimationId == authored))
                {
                    SetCreatorAnimation(stage, authored);
                    close = true;
                }
                if (ImGui.IsItemHovered()) HoverPreviewCreatorAnimation(stage, authored);
            }
            ImGui.Separator();

            IReadOnlyList<int> onModel = _character?.AvailableAnimationIds ?? Array.Empty<int>();
            var onModelSet = new HashSet<int>(onModel);
            IReadOnlyList<AnimationDataRow> rows = _animationData?.Rows ?? Array.Empty<AnimationDataRow>();
            if (rows.Count == 0)
            {
                ImGui.TextDisabled("AnimationData.dbc is not loaded - type an id in Composition instead.");
            }
            else
            {
                bool Matches(AnimationDataRow row) =>
                    query.Length == 0 ||
                    row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.Id.ToString().StartsWith(query, StringComparison.Ordinal);

                int shownOn = 0, shownOff = 0;
                if (BeginCreatorResults("##anim-picker-rows", rows.Count + 2, 0.6f))
                {
                    ImGui.TextDisabled("ON THIS BODY'S MODEL");
                    foreach (AnimationDataRow row in rows)
                    {
                        if (!onModelSet.Contains(row.Id) || !Matches(row)) continue;
                        shownOn++;
                        if (CreatorResultRow($"{row.Id}  {row.Name}", composition.AnimationId == row.Id))
                        {
                            SetCreatorAnimation(stage, row.Id);
                            close = true;
                        }
                        if (ImGui.IsItemHovered()) HoverPreviewCreatorAnimation(stage, row.Id);
                    }
                    if (shownOn == 0) ImGui.TextDisabled("(nothing matches)");

                    ImGui.Spacing();
                    ImGui.TextDisabled("NOT ON THIS MODEL (other races / creatures may have them)");
                    foreach (AnimationDataRow row in rows)
                    {
                        if (onModelSet.Contains(row.Id) || !Matches(row)) continue;
                        shownOff++;
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.6f, 1f));
                        bool pick = CreatorResultRow($"{row.Id}  {row.Name}", composition.AnimationId == row.Id);
                        ImGui.PopStyleColor();
                        if (pick)
                        {
                            SetCreatorAnimation(stage, row.Id);
                            close = true;
                        }
                    }
                    if (shownOff == 0) ImGui.TextDisabled("(nothing matches)");
                }
                EndCreatorResults();
            }

            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        // Leaving the rows re-arms the hover, so coming back to the same row performs it again.
        if (!_animPickerHoverSeen) _animPickerHovered = -1;
        if (close) _animPickerStage = null;
    }

    /// <summary>Hovering a row performs it once; the same row hovered on the next frame
    /// does not restart it, so a slow swing can be watched through.</summary>
    private void HoverPreviewCreatorAnimation(SpellStage stage, int animationId)
    {
        _animPickerHoverSeen = true;
        if (_animPickerHovered == animationId) return;
        _animPickerHovered = animationId;
        PreviewCreatorAnimation(stage, (ushort)animationId);
    }
}
