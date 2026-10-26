using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _presentedEffectSpell;
    /// <summary>The stage the presented kit is ("STATE", ...): the creator's body preview reads it.</summary>
    private string _presentedEffectStage = "";

    /// <summary>The unit the current presented kit is anchored to. Usually the local
    /// player; the creator loop anchors IMPACT on the spawned target instead.</summary>
    private ulong _presentedEffectGuid;

    /// <summary>Reap the presented effect wherever it was spawned (player and/or target).</summary>
    private void ReapPresentedEffect()
    {
        if (_presentedEffectSpell == 0 || _spellEffects is null) return;
        _spellEffects.Reap(LocalPlayerGuid, _presentedEffectSpell);
        if (_presentedEffectGuid != 0 && _presentedEffectGuid != LocalPlayerGuid)
            _spellEffects.Reap(_presentedEffectGuid, _presentedEffectSpell);
        _presentedEffectSpell = 0;
        _presentedEffectGuid = 0;
        _presentedEffectStage = "";
    }

    /// <summary>Present one visual-kit stage. <paramref name="onGuid"/> anchors the kit
    /// on another unit (the creator loop lands impact on the spawned target); the
    /// character animation always plays on the local player either way.</summary>
    private bool PresentSpellEffect(uint spellId, string stage, ulong? onGuid = null, System.Numerics.Vector3? groundPoint = null)
    {
        if (_spellEffects is null) return false;

        // A spell the creator INVENTED is not in Spell.dbc and has no SpellVisual row, so both
        // lookups below fail and the whole method used to give up here - before ever reaching
        // the composed-kit branch that exists precisely to draw a stage nothing authored. That
        // is why a brand-new spell showed nothing at all. When the creator's own document owns
        // this id, it IS the catalog.
        bool creatorOwned = _creatorWorldRequested && _creatorSpell is { } owned &&
                            owned.Info.Id == spellId;
        SpellInfo info;
        SpellVisualStages stages;
        if (creatorOwned)
        {
            info = _creatorSpell!.Info;
            stages = _creatorSpell.Stages;
        }
        else if (_spellCatalog?.TryGet(spellId, out info) != true ||
                 _spellVisualCatalog?.TryGetStages(info.VisualId, out stages) != true)
        {
            return false;
        }
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "impact" => stages.Impact,
            "state" => stages.State,
            "channel" => stages.Channel,
            _ => 0,
        };
        SpellVisualKitInfo kit = default;
        bool haveKit = kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out kit);
        // The creator's composed kit replaces the authored one (a stage the source never
        // authored can be filled from scratch) - GameLoop.Creator.Composition.cs.
        if (_creatorWorldRequested && _creatorSpell is { } composed && composed.Info.Id == spellId &&
            Enum.TryParse(stage, ignoreCase: true, out SpellStage composedStage))
        {
            kit = CreatorComposedKit(composed, composedStage, haveKit
                ? kit
                : new SpellVisualKitInfo(null, null, Array.Empty<SpellVisualKitEffect>(), Array.Empty<SpellVisualCharProc>()));
            // A phase that only changes the BODY (tint, see-through, a weapon trail) plays too.
            haveKit = kit.Effects.Count > 0 || kit.CharProcs.Count > 0;
        }
        if (!haveKit) return false;
        ReapPresentedEffect();
        ulong anchor = onGuid ?? LocalPlayerGuid;
        _presentedEffectSpell = spellId;
        _presentedEffectGuid = anchor;
        _presentedEffectStage = stage.ToUpperInvariant();
        if (groundPoint is { } point)
        {
            // A ground spell's impact plays AT the spot, the way the live client plays a ground
            // missile's arrival (SpellEffectSource.SpawnGroundKit, SPELL_CREATOR_PLAN.md P2.4).
            _spellEffects.SpawnGroundKit(anchor, spellId, kit, point, SpellClockNow, stage.ToUpperInvariant());
            return true;
        }
        // A phase being sketched is held on screen: an editor needs the thing to sit still long
        // enough to be grabbed, and a half-second cast in a two-second loop does not.
        _spellEffects.SpawnKit(anchor, spellId, kit,
            persistent: stage is "precast" or "state" or "channel" || SketchHoldsStage(stage),
            SpellClockNow, stage.ToUpperInvariant());
        if (anchor != LocalPlayerGuid)
        {
            // Anchored on another unit (creator-loop impact on the spawned
            // target): the kit's animation is the VICTIM's reaction - play it on
            // that unit, exactly like the networked impact path, and leave the
            // local character alone (ReleaseSpellVisual would make the PLAYER
            // flinch with the victim's wound animation).
            if (kit.AnimationId is { } victimAnim && victimAnim != 0)
                _creatures?.ReleaseSpellVisual(anchor, victimAnim);
            else
                // Kit authors no victim animation - fall back to the plain
                // landed-hit flinch (CombatWound) so the impact still reads.
                _creatures?.TriggerCombatReaction(anchor, 0, landedHit: true);
        }
        else if (!ControlledBodyTacticallyFrozen)
        {
            if (stage.Equals("precast", StringComparison.OrdinalIgnoreCase) ||
                stage.Equals("channel", StringComparison.OrdinalIgnoreCase))
                _character?.BeginSpellVisual(kit.AnimationId);
            else
                _character?.ReleaseSpellVisual(kit.AnimationId);
        }
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, kit.AnimationId, "DBC_EFFECT_SUPPLIER");
        return true;
    }

    private bool PresentSpellAnimation(uint spellId, string stage, string source)
    {
        if (ControlledBodyTacticallyFrozen ||
            _spellCatalog?.TryGet(spellId, out SpellInfo info) != true || _character is null)
            return false;
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "channel" => stages.Channel,
            _ => 0,
        };
        if (kitId == 0 || _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) != true)
            return false;
        if (stage.Equals("precast", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("channel", StringComparison.OrdinalIgnoreCase))
            _character.BeginSpellVisual(kit.AnimationId);
        else
            _character.ReleaseSpellVisual(kit.AnimationId);
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, kit.AnimationId, source);
        return true;
    }

    private bool SampleSpellAnimation(uint spellId, string stage, string source)
    {
        if (_spellCatalog?.TryGet(spellId, out SpellInfo info) != true || _character is null ||
            _spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "channel" => stages.Channel,
            _ => 0,
        };
        ushort? animation = kitId != 0 &&
            _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) ? kit.AnimationId : null;
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, animation, source);
        return kitId != 0;
    }

    private void EmitSpellAnimation(in SpellInfo info, string stage, uint kitId,
        ushort? authoredAnimation, string source)
    {
        int track = stage is "PRECAST" or "CHANNEL" ? 2 : 1;
        (int Requested, int Played, AnimChoiceKind Kind) state =
            _lastAnimChoices.TryGetValue(("player", track), out var found)
                ? found : (-1, -1, AnimChoiceKind.Missing);
        var verdict = new SpellAnimationVerdict(NowSeconds(), _net?.PlayerName ?? "",
            info.Id, info.Name, SchoolName(info.School), stage, kitId,
            authoredAnimation ?? -1, state.Requested, state.Played, state.Kind,
            _character?.CurrentPresentationAnimation ?? "none",
            (_character?.GroundSpeed ?? 0) > 0.3f,
            info.CastClassification == "INSTANT" || !info.MovementInterrupts,
            info.MovementInterrupts,
            _character?.CurrentBaseAnimation ?? "none",
            _character?.PreviousBaseAnimation ?? "none",
            _character?.CurrentActionAnimation ?? "none",
            _character?.CurrentSpellHoldAnimation ?? "none",
            _character?.CurrentBlendWeight ?? 1f,
            source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-animation] {verdict.ToLine()}");
    }
}
