using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum CastBarPhase { Hidden, Casting, Channel, Success, Failed }
    private CastBarPhase _castBarPhase;
    private uint _castBarSpell;
    private string _castBarText = "";
    private double _castBarStarted;
    private double _castBarEnds;
    private double _castBarDisplayUntil;
    private double _castBarFinishedAt;

    /// <summary>
    /// The unit the cast-bar / pending-cast state belongs to. That state is one set of fields
    /// keyed only by "packet.Caster == ControlledGuid" at packet time, so a control switch
    /// mid-cast (Ctrl+Tab, Alt+click on a party member, a possess-on-cast hand-over) used to
    /// strand the previous body's bar: its SPELL_GO / failure then arrived for a guid that was
    /// no longer controlled, nothing completed or failed it, the bar sat there, and the local
    /// pending lock refused every cast on the new body ("Another action is in progress")
    /// until something else happened to overwrite the fields (Ctrl+F, in practice). Owner
    /// report 2026-09-03. UpdateControlInput resets the state whenever the owner changes; the
    /// old body's cast still finishes server-side, it just stops being ours to show.
    /// </summary>
    private ulong _castStateOwner;

    private void ResetCastStateOnControlChange()
    {
        ulong owner = ControlledGuid;
        if (owner == _castStateOwner) return;
        bool live = _castBarPhase != CastBarPhase.Hidden || _pendingCastSpell != 0 ||
            _queuedMeleeSpell != 0 || _autoRepeatSpell != 0;
        _castStateOwner = owner;
        // Reset local intent only: a handoff must not cancel the new actor on the server.
        _autoRepeatSpell = 0;
        CancelGroundTargeting();
        CancelItemTargeting();
        CancelGiftWrapping();
        if (!live) return;
        if (_castBarPhase != CastBarPhase.Hidden)
            EmitCastBarVerdict("CONTROL_CHANGED", _castBarSpell);
        _castBarPhase = CastBarPhase.Hidden;
        _castBarSpell = 0;
        _castBarText = "";
        _castBarPushbackTotalMs = 0;
        _pendingCastSpell = 0;
        _queuedMeleeSpell = 0;
        _castMovementWasActive = false;
    }

    private bool _castMovementWasActive;
    private readonly HashSet<(ulong Unit, uint Spell)> _activeAuraStateFx = [];
    private readonly Dictionary<ulong, uint> _activeObservedChannels = [];
    private readonly HashSet<ulong> _lootableCorpseVisualsSeen = [];
    private readonly HashSet<ulong> _activeLootableCorpseFx = [];

    // Hardcoded effects are client-owned rather than real spell casts. Give the
    // persistent corpse sparkle its own AuraState key so BeginCast cannot sweep
    // it together with a unit's precast/channel hold.
    private const uint LootableCorpseVisualKey = uint.MaxValue;
    private const uint UnitLevelUpVisualKey = uint.MaxValue - 1;

    private void ApplySpellStart(SpellStartPacket packet)
    {
        MarkAnimationSequenceStage(packet.SpellId, "PRECAST", packet.Caster);
        BeginRealPortalCastPrewarm(packet);
        if (_net is not null && packet.Caster == ControlledGuid)
            EmitSpellServerResult(packet.SpellId, "SMSG_SPELL_START");
        SpellInfo? info = _spellCatalog?.TryGet(packet.SpellId, out SpellInfo found) == true ? found : null;
        uint visual = EffectiveSpellVisual(info, packet.Caster);
        SpellVisualKitInfo? kit = ResolveSpellKit(visual, static s => s.Precast);
        ushort? anim = kit?.AnimationId;
        _spellEffects?.BeginCast(packet.Caster);
        _spellChainBeams?.BeginCast(packet.Caster);
        _spellSounds?.StopHold(packet.Caster);
        PlaySpellSound(packet.Caster, kit?.Sound);
        if (kit is { } precastKit)
            _spellEffects?.SpawnKit(packet.Caster, packet.SpellId, precastKit,
                StageLife.Persistent, NowSeconds(), "PRECAST");
        if (_net is not null && packet.Caster == ControlledGuid)
        {
            if (ControlledBodyIsStreamed) _creatures?.BeginSpellVisual(packet.Caster, anim);
            else if (!ControlledBodyTacticallyFrozen) _character?.BeginSpellVisual(anim);
            if (info is { } startedInfo)
                EmitSpellAnimation(startedInfo, "PRECAST", SpellStageKitId(startedInfo.VisualId, "precast"), anim, "SERVER_START");
            if (info?.Ranged == true) SetVisualSheath(2);
            ApplyControlledCastStart(packet.SpellId, packet.CastTimeMs, info);
        }
        else _creatures?.BeginSpellVisual(packet.Caster, anim);
    }

    private void ApplySpellGo(SpellGoPacket packet)
    {
        MarkAnimationSequenceStage(packet.SpellId, "CAST", packet.Caster);
        ObserveRealPortalCastGo(packet);
        // Encounter Lab tape: passive ground-truth recording. No-op unless the Lab
        // window is open AND recording is armed (instrumentation-hazard rule).
        RecordEncounterTapeCast(packet);
        double now = NowSeconds();
        SpellInfo? info = _spellCatalog?.TryGet(packet.SpellId, out SpellInfo found) == true ? found : null;
        // The reference's second SetGoState caller: an open-lock SPELL_GO
        // names the GameObject whose lid/door becomes ACTIVE. This is observer-
        // safe and deliberately happens at cast launch, not when loot arrives.
        if (packet.Targets.Unit is ulong goTarget &&
            info?.EffectIds?.Any(effect => effect is 33 or 59) == true &&
            _entities.TryGet(goTarget, out WorldEntity go) && go.IsGameObject)
            PredictGameObjectAnimationState(goTarget, GameObjectAnimationLaw.StateActive);
        if (packet.Caster == LocalPlayerGuid && packet.Targets.Unit is ulong lootTarget &&
            info?.EffectIds?.Any(effect => effect == 33) == true &&
            _entities.TryGet(lootTarget, out WorldEntity lootObject) && lootObject.IsGameObject)
        {
            _lootPendingGuid = lootTarget;
            RefreshLootKneel();
        }
        uint visual = EffectiveSpellVisual(info, packet.Caster);
        SpellVisualKitInfo? kit = ResolveSpellKit(visual, static s => s.Cast);
        ushort? anim = kit?.AnimationId;
        _spellChainBeams?.StoreHops(packet.Caster, packet.Hits);
        _spellEffects?.Reap(packet.Caster, packet.SpellId, StageLife.Persistent);
        _spellChainBeams?.Reap(packet.Caster, packet.SpellId);
        _spellSounds?.StopHold(packet.Caster);
        PlaySpellSound(packet.Caster, kit?.Sound);
        if (kit is { } castKit)
        {
            _spellEffects?.SpawnKit(packet.Caster, packet.SpellId, castKit,
                StageLife.SelfTerminating, NowSeconds(), "CAST");
            _spellChainBeams?.Play(packet.Caster, packet.SpellId, visual, castKit,
                liveChannelSpell: 0, liveChannelObject: null, now);
        }
        if (_net is not null && packet.Caster == ControlledGuid)
        {
            EmitSpellServerResult(packet.SpellId, "SMSG_SPELL_GO");
            if (_pendingCastSpell == packet.SpellId) _pendingCastSpell = 0;
            if (_queuedMeleeSpell == packet.SpellId) _queuedMeleeSpell = 0;
            if (ControlledBodyIsStreamed) _creatures?.ReleaseSpellVisual(packet.Caster, anim);
            else if (!ControlledBodyTacticallyFrozen) _character?.ReleaseSpellVisual(anim);
            if (info is { } completedInfo)
                EmitSpellAnimation(completedInfo, "CAST", SpellStageKitId(completedInfo.VisualId, "cast"), anim, "SERVER_GO");
            if (info?.Ranged == true) SetVisualSheath(2);
            // Auto Shot / wand Shoot reload: the rail restarts on the real SPELL_GO,
            // identified by the auto-repeat attribute, not every ranged ability.
            if (info is { } rangedInfo)
                NoteSwingTimerRanged(packet.SpellId, rangedInfo, packet.Caster);
            CompleteCastBar(packet.SpellId);
            ObserveProfessionSpellGo(packet.SpellId);
            ObserveHearthSpellGo(packet.SpellId, packet.Caster);
            if (info is { } completed)
            {
                uint rangedAttackTimeMs = completed.RangedSpeedCooldown &&
                    _entities.TryGet(packet.Caster, out WorldEntity cooldownCaster)
                        ? cooldownCaster.Fields.RangedAttackTime : 0;
                StartActorSpellCooldown(_actions, packet.Caster, completed,
                    rangedAttackTimeMs, now);
            }
        }
        else _creatures?.ReleaseSpellVisual(packet.Caster, anim);

        EmitCombat("SpellGoTargets", "server-packet", packet.Targets.Unit ?? 0,
            $"spell={packet.SpellId};caster=0x{packet.Caster:X16};mask=0x{packet.Targets.Mask:X4};hits={packet.Hits.Length};" +
            $"hitGuids={string.Join('|', packet.Hits.Select(guid => $"0x{guid:X16}"))};" +
            $"misses={packet.Misses.Length};missInfo={string.Join('|', packet.Misses.Select(miss => $"0x{miss.Guid:X16}:{miss.Reason}:reflect={miss.ReflectionReason?.ToString() ?? "none"}"))};explicitUnit=" +
            (packet.Targets.Unit is { } explicitUnit ? $"0x{explicitUnit:X16}" : "none"));

        SpellVisualStages visualStages = default;
        bool hasStages = _spellVisualCatalog?.TryGetStages(visual, out visualStages) == true;
        string? missilePath = hasStages ? _spellVisualCatalog!.MissilePath(visualStages) : null;
        if (hasStages && visualStages.MissileEffect != 0 && missilePath is null)
            missilePath = SpellVisualCatalog.ErrorCube;
        bool ammoFallback = missilePath is null;
        missilePath ??= _spellEffects?.AmmoModelPath(packet.AmmoDisplayId);
        string? ammoTexture = ammoFallback ? _spellEffects?.AmmoTexturePath(packet.AmmoDisplayId) : null;

        // The destination has its own presentation even when no units were hit.
        // A no-missile visual births its one-shot at GO; the pure location missile
        // instead uses the normal release gate and hands off its area sound on arrival.
        if (packet.Targets.Destination is { } destination)
        {
            if (_spellVisualCatalog?.TryGetDestinationBurst(visual, out string burst) == true)
                _spellEffects?.SpawnGroundBurst(packet.Caster, packet.SpellId, burst, destination, now);
            if (packet.Hits.Length == 0 && packet.Misses.Length == 0 &&
                info is { Speed: > 0 } groundInfo && _spellEffects is not null)
            {
                long groundVoice = 0;
                _spellEffects.SpawnMissile(packet.Caster, packet.SpellId, missilePath, 0,
                    SpellVisualCatalog.NoMissileAttachment, groundInfo.Speed, now, false, 0,
                    anim, SpellEffectUnitPose,
                    (_, _, _, _) => PlaySpellSoundAt(packet.Caster,
                        ResolveSpellKit(visual, static s => s.AreaKit)?.Sound, destination,
                        forceLoop: false, trackHold: false),
                    ammoTexture,
                    launched: () => groundVoice = PlaySpellSound(packet.Caster,
                        hasStages ? visualStages.MissileSound : 0, forceLoop: true, trackHold: false),
                    ended: () => _spellSounds?.Stop(groundVoice), destination: destination);
            }
        }

        foreach ((ulong target, bool missed, byte reason) in
            packet.Hits.Select(g => (g, false, (byte)0))
                .Concat(packet.Misses.Select(m => (m.Guid, true, m.Reason))))
        {
            if (info is not { Speed: > 0 } || _spellEffects is null)
            {
                OnSpellMissileArrived(target, packet.SpellId, visual, missed, reason);
                continue;
            }
            long missileVoice = 0;
            _spellEffects.SpawnMissile(packet.Caster, packet.SpellId, missilePath, target,
                hasStages ? visualStages.MissileAttachment : SpellVisualCatalog.NoMissileAttachment,
                info.Value.Speed, now, missed, reason, anim, SpellEffectUnitPose,
                (arrivedTarget, arrivedSpell, wasMissed, missReason) =>
                    OnSpellMissileArrived(arrivedTarget, arrivedSpell, visual, wasMissed, missReason),
                ammoTexture,
                launched: () => missileVoice = PlaySpellSound(packet.Caster,
                    hasStages ? visualStages.MissileSound : 0, forceLoop: true,
                    trackHold: false),
                ended: () => _spellSounds?.Stop(missileVoice));
        }
    }

    private void OnSpellMissileArrived(ulong target, uint spellId, uint visual,
        bool missed, byte reason)
    {
        if (!missed) { ApplySpellImpact(target, spellId, visual); return; }
        if (reason is not (3 or 5)) return; // Benilla: Dodge and Block only.
        if (_net is not null && target == ControlledGuid && !ControlledBodyIsStreamed)
        {
            if (!ControlledBodyTacticallyFrozen)
                _character?.TriggerCombatReaction(reason, landedHit: false);
        }
        else _creatures?.TriggerCombatReaction(target, reason, landedHit: false);
    }

    private void ApplySpellImpact(ulong target, uint spellId, uint visualOverride = 0)
    {
        uint visual = visualOverride != 0 ? visualOverride :
            (_spellCatalog?.TryGet(spellId, out SpellInfo info) == true ? info.VisualId : 0);
        SpellVisualKitInfo? impact = ResolveSpellKit(visual, static s => s.Impact);
        if (impact is { } impactKit)
            _spellEffects?.SpawnKit(target, spellId, impactKit,
                StageLife.SelfTerminating, NowSeconds(), "IMPACT");
        PlaySpellSound(target, impact?.Sound);

        // Reference law (benilla creature_anim/driver/wound.rs:14-15 + net/apply/combat_log.rs):
        // a spell impact animates its victim ONLY through the kit's own authored animation id,
        // and only the CombatWound family (8 StandWound / 9 CombatWound / 10 CombatCritical)
        // routes through the wound reaction. A kit with no animation plays NOTHING - the old
        // "no anim => synthesize a wound" fallback here is what made the server's login visual
        // (LOGINEFFECT, spell 836) flinch the local player the moment the world became visible.
        void PlayBodyAnimation(ushort? animation)
        {
            if (animation is not { } anim || anim == 0) return;
            bool wound = anim is 8 or 9 or 10;
            if (_net is not null && target == ControlledGuid && !ControlledBodyIsStreamed)
            {
                if (!ControlledBodyTacticallyFrozen)
                {
                    if (wound) _character?.TriggerCombatReaction(0, landedHit: true);
                    else _character?.TriggerOneShot(anim);
                }
            }
            else if (wound)
                _creatures?.TriggerCombatReaction(target, 0, landedHit: true);
            else
                _creatures?.ReleaseSpellVisual(target, anim);
        }

        // Benilla's impact hand-off is ordered stage 1 then stage 2. State
        // effect models are owned separately by the aura-slot watcher below,
        // but its authored body animation is still a discrete arrival event.
        PlayBodyAnimation(impact?.AnimationId);
        SpellVisualKitInfo? state = ResolveSpellKit(visual, static s => s.State);
        PlayBodyAnimation(state?.AnimationId);
        if (_net is not null && target == ControlledGuid) PlaySpellSound(target, state?.Sound);
    }

    private SpellVisualKitInfo? ResolveSpellKit(uint visualId,
        Func<SpellVisualStages, uint> stage)
    {
        if (_spellVisualCatalog is null ||
            !_spellVisualCatalog.TryGetStages(visualId, out SpellVisualStages stages)) return null;
        uint kitId = stage(stages);
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) ? kit : null;
    }

    private uint SpellStageKitId(uint visualId, string stage)
    {
        if (_spellVisualCatalog?.TryGetStages(visualId, out SpellVisualStages stages) != true) return 0;
        return stage == "precast" ? stages.Precast : stage == "channel" ? stages.Channel : stages.Cast;
    }

    /// <summary>
    /// Cancel the CONTROLLED unit's held cast animation on whichever renderer
    /// owns its skeleton (see ControlledBodyIsStreamed). Start/Go already
    /// branch this way; every cancel path must too, or an interrupted cast in
    /// the free view leaves the streamed body looping its cast-state forever —
    /// _character isn't drawn there, so cancelling it changes nothing visible.
    /// </summary>
    private void CancelControlledSpellVisual()
    {
        if (ControlledBodyIsStreamed) _creatures?.CancelSpellVisual(ControlledGuid);
        else if (!ControlledBodyTacticallyFrozen) _character?.CancelSpellVisual();
    }

    private void ApplySpellCastFailureResult(uint spellId, byte reason, ulong? source = null,
        SpellCastFailureContext? context = null)
    {
        ulong caster = source ?? ControlledGuid;
        // The event retains its origin across queued control acknowledgements.
        if (caster != ControlledGuid)
        {
            ApplySpellFailure(caster, spellId, "FAILED");
            return;
        }
        FailRealPortalCastPrewarmResult(spellId);
        string name = SpellCastResultNames.Name(reason);
        EmitSpellServerResult(spellId, name);
        string power = _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true
            ? PowerName((byte)spell.PowerType) : "POWER";
        string text = ContextualSpellFailureText(reason, power, context);
        ShowSpellError(spellId, name, text, "SMSG_CAST_RESULT");
        ObserveProfessionSpellFailure(spellId, name);
        ApplySpellFailure(caster, spellId,
            reason is 0x23 or 0x24 ? "INTERRUPTED" : text.Length > 0 ? text : "FAILED");
        RestorePermanentCastFailureCooldown(caster, spellId, context);
    }

    private void ApplySpellFailure(ulong caster, uint spellId, string text)
    {
        FailRealPortalCastPrewarm(caster, spellId);
        if (_net is not null && caster == ControlledGuid)
        {
            if (_pendingCastSpell == spellId) _pendingCastSpell = 0;
            if (_queuedMeleeSpell == spellId) _queuedMeleeSpell = 0;
            if (_autoRepeatSpell == spellId) _autoRepeatSpell = 0;
            _globalCooldownUntil = 0;
            PlayerActions actions = ActionsFor(caster);
            actions.ClearGlobalCooldown(spellId);
            actions.ClearCooldown(spellId);
            CancelControlledSpellVisual();
            FailCastBar(spellId, text);
        }
        else _creatures?.CancelSpellVisual(caster);
        _spellSounds?.StopHold(caster);
        _spellEffects?.Reap(caster, spellId, StageLife.Persistent);
        _spellChainBeams?.Reap(caster, spellId);
    }

    /// <summary>
    /// Escape follows the 1.12 stop-casting order: stop a ranged auto-repeat
    /// first; otherwise cancel an ordinary in-flight cast. A channel is not an
    /// Escape target in the original client (movement cancels it separately).
    /// Returning true keeps the same key press from opening the game menu.
    /// </summary>
    private bool TryCancelSpellOnEscape()
    {
        if (!CanAuthorControlledGameplay || _net is not { IsInWorld: true }) return false;
        if (_autoRepeatSpell != 0)
        {
            _net.CancelAutoRepeat();
            _autoRepeatSpell = 0;
            SetVisualSheath(0);
            CancelControlledSpellVisual();
            if (_net is not null) _spellSounds?.StopHold(ControlledGuid);
            return true;
        }
        uint spell = _pendingCastSpell;
        if (spell == 0 && _castBarPhase == CastBarPhase.Casting) spell = _castBarSpell;
        if (spell == 0) return false;
        EmitCastBarVerdict("CANCEL_SEND", spell, cancelSource: "ESCAPE");
        _net.CancelCast(spell);
        ApplySpellFailure(ControlledGuid, spell, "INTERRUPTED");
        return true;
    }

    /// <summary>Send the movement-only cancel exactly once on the stopped-to-moving edge.</summary>
    private void UpdateCastMovementInput(bool active)
    {
        bool edge = active && !_castMovementWasActive;
        _castMovementWasActive = active;
        if (!edge || _net is not { IsInWorld: true }) return;

        if (_castBarPhase == CastBarPhase.Channel &&
            (_spellCatalog?.TryGet(_castBarSpell, out SpellInfo channel) != true ||
             channel.MovementInterruptsChannel))
        {
            EmitCastBarVerdict("CANCEL_SEND", _castBarSpell, cancelSource: "MOVEMENT_CHANNEL");
            EmitChannelVerdict("CANCEL", remainingMs: (uint)Math.Max(0,
                (_castBarEnds - NowSeconds()) * 1000.0), source: "MOVEMENT_CHANNEL");
            _net.CancelChannelling(_castBarSpell);
            return; // channel owns the one movement-cancel edge
        }

        uint spell = _pendingCastSpell;
        if (spell == 0 && _castBarPhase == CastBarPhase.Casting) spell = _castBarSpell;
        if (spell == 0) return;
        if (_spellCatalog?.TryGet(spell, out SpellInfo info) == true && !info.MovementInterrupts)
            return;
        EmitCastBarVerdict("CANCEL_SEND", spell, cancelSource: "MOVEMENT_CAST");
        _net.CancelCast(spell);
        ApplySpellFailure(ControlledGuid, spell, "INTERRUPTED");
    }

    private void ApplyServerCombatCancelled(ulong caster)
    {
        _combat.ApplySnapshot(caster, null, _entities);
        if (caster != ControlledGuid) return;
        _attackTargetGuid = 0;
        _queuedMeleeSpell = 0;
        if (_autoRepeatSpell != 0)
        {
            if (_pendingCastSpell == _autoRepeatSpell) _pendingCastSpell = 0;
            ApplyAutoRepeatCancelled();
        }
    }

    private void ApplyAutoRepeatCancelled()
    {
        _autoRepeatSpell = 0;
        SetVisualSheath(0);
        CancelControlledSpellVisual();
    }

    private void UpdateSpellPresentation()
    {
        double now = SpellClockNow;
        UpdateLootableCorpseVisuals(now);
        // Paused creator clock: no tick, so self-terminating kits are not reaped mid-hold.
        if (!CreatorSpellPaused) _spellEffects?.Tick(now, SpellEffectUnitPose);
        UpdateAuraStateVisuals(now);
        UpdateObservedChannels(now);
        UpdateDynamicObjectVisuals(now);
        UpdateCreatureBodyLoops();
        // The driven body's sounds outrank the crowd's when the mix is full (AudioVoicePriorityLaw).
        if (_audioMixer is not null) _audioMixer.PriorityOwner = ControlledGuid;
        _spellSounds?.Tick(_controller?.Position ?? Vector3.Zero, guid =>
        {
            SpellUnitPose pose = SpellEffectUnitPose(guid);
            return (pose.Found, pose.Position);
        });
        if (_castBarPhase == CastBarPhase.Channel && now >= _castBarEnds)
        {
            _castBarPhase = CastBarPhase.Success;
            _castBarFinishedAt = _castBarEnds;
            _castBarDisplayUntil = _castBarFinishedAt + CastingBarUiLaw.FlashSeconds +
                CastingBarUiLaw.FadeSeconds;
            EmitCastBarVerdict("CHANNEL_STOP", _castBarSpell);
        }
        if (_castBarPhase is CastBarPhase.Success or CastBarPhase.Failed && now >= _castBarDisplayUntil)
            _castBarPhase = CastBarPhase.Hidden;
    }

    /// <summary>
    /// Benilla's client-owned corpse effect. Loot art follows the viewer-filtered
    /// UNIT_DYNFLAG_LOOTABLE bit for exactly as long as the corpse is lootable.
    /// </summary>
    private void UpdateLootableCorpseVisuals(double now)
    {
        if (_spellEffects is null || _spellVisualCatalog is null) return;

        _lootableCorpseVisualsSeen.Clear();
        foreach (WorldEntity unit in _entities.Units)
        {
            ulong guid = unit.Guid;
            _lootableCorpseVisualsSeen.Add(guid);

            bool wantsLootArt = unit.Fields.ReadsDead && unit.Fields.Lootable;
            if (wantsLootArt && !_activeLootableCorpseFx.Contains(guid) &&
                _spellVisualCatalog.TryGetHardcodedEffect(
                    SpellVisualCatalog.HardcodedLootArt, out string lootPath))
            {
                var kit = new SpellVisualKitInfo(null, null,
                    [new SpellVisualKitEffect(0x13, lootPath)], []);
                if (_spellEffects.SpawnKit(guid, LootableCorpseVisualKey, kit,
                        StageLife.AuraState, now, "HARDCODED_LOOT") > 0)
                    _activeLootableCorpseFx.Add(guid);
            }
            else if (!wantsLootArt && _activeLootableCorpseFx.Remove(guid))
            {
                _spellEffects.Reap(guid, LootableCorpseVisualKey, StageLife.AuraState);
            }

        }

        foreach (ulong guid in _activeLootableCorpseFx
                     .Where(guid => !_lootableCorpseVisualsSeen.Contains(guid)).ToArray())
        {
            _activeLootableCorpseFx.Remove(guid);
            _spellEffects.Reap(guid, LootableCorpseVisualKey, StageLife.AuraState);
        }
    }

    private void PlayHardcodedUnitLevelUp(ulong guid, uint level)
    {
        if (level == 0 || _spellEffects is null || _spellVisualCatalog is null ||
            !_spellVisualCatalog.TryGetHardcodedEffect(
                SpellVisualCatalog.HardcodedUnitLevelUp, out string levelPath)) return;
        var kit = new SpellVisualKitInfo(null, null,
            [new SpellVisualKitEffect(0x13, levelPath)], []);
        _spellEffects.SpawnKit(guid, UnitLevelUpVisualKey, kit,
            StageLife.SelfTerminating, NowSeconds(), "HARDCODED_LEVEL_UP");
    }

    private long PlaySpellSound(ulong unit, uint? soundId, bool forceLoop = false,
        bool trackHold = true)
    {
        SpellUnitPose pose = SpellEffectUnitPose(unit);
        Vector3 source = pose.Found ? pose.Position : _controller?.Position ?? Vector3.Zero;
        return PlaySpellSoundAt(unit, soundId, source, forceLoop, trackHold);
    }

    private long PlaySpellSoundAt(ulong unit, uint? soundId, Vector3 source,
        bool forceLoop = false, bool trackHold = true)
    {
        if (_spellSounds is null || soundId is not uint id || id == 0) return 0;
        Vector3 listener = _controller?.Position ?? source;
        return _spellSounds.Play(id, unit, source, listener, forceLoop, trackHold);
    }

    private void ApplyControlledCastStart(uint spell, uint durationMs, SpellInfo? info)
    {
        // Aimed Shot and other timed ranged abilities have ordinary cast bars.
        // Auto-repeat's start announces persistent firing intent, not a timed cast.
        if (durationMs > 0 && info?.AutoRepeat != true)
            BeginCastBar(spell, durationMs, channel: false);
    }

    private void BeginCastBar(uint spell, uint durationMs, bool channel)
    {
        double now = NowSeconds();
        _castBarSpell = spell;
        _castBarText = _spellCatalog?.TryGet(spell, out SpellInfo info) == true ? info.Name : $"Spell {spell}";
        _castBarStarted = now;
        _castBarEnds = now + durationMs / 1000.0;
        _castBarPhase = channel ? CastBarPhase.Channel : CastBarPhase.Casting;
        _castBarPushbackTotalMs = 0;
        EmitCastBarVerdict(channel ? "CHANNEL_START" : "CAST_START", spell, durationMs);
    }

    private void CompleteCastBar(uint spell)
    {
        if (!CastingBarUiLaw.AcceptCastTerminal(
                _castBarPhase == CastBarPhase.Casting, _castBarSpell, spell)) return;
        _castBarPhase = CastBarPhase.Success;
        _castBarText = _spellCatalog?.TryGet(spell, out SpellInfo info) == true ? info.Name : _castBarText;
        _castBarFinishedAt = NowSeconds();
        // CastingBar.xml: flash grows at .2 per 30 Hz tick, then the frame fades at .05/tick.
        _castBarDisplayUntil = _castBarFinishedAt + CastingBarUiLaw.FlashSeconds +
            CastingBarUiLaw.FadeSeconds;
        EmitCastBarVerdict("CAST_COMPLETE", spell);
    }

    private void FailCastBar(uint spell, string text)
    {
        if (!CastingBarUiLaw.AcceptCastTerminal(
                _castBarPhase == CastBarPhase.Casting, _castBarSpell, spell)) return;
        _castBarPhase = CastBarPhase.Failed;
        _castBarText = CastingBarUiLaw.TerminalText(text);
        _castBarFinishedAt = NowSeconds();
        _castBarDisplayUntil = _castBarFinishedAt + CastingBarUiLaw.FailureHoldSeconds +
            CastingBarUiLaw.FadeSeconds;
        EmitCastBarVerdict("CAST_FAILED", spell, cancelSource: text);
    }

    private void DelayCastBar(uint delayMs)
    {
        if (_castBarPhase != CastBarPhase.Casting) return;
        _castBarStarted += delayMs / 1000.0;
        _castBarEnds += delayMs / 1000.0;
        _castBarPushbackTotalMs += delayMs;
        EmitCastBarVerdict("PUSHBACK", _castBarSpell, delayMs);
    }

    private void UpdateChannel(uint remainingMs)
    {
        if (remainingMs == 0)
        {
            if (_castBarPhase == CastBarPhase.Channel)
            {
                _castBarPhase = CastBarPhase.Success;
                _castBarFinishedAt = NowSeconds();
                _castBarDisplayUntil = _castBarFinishedAt + CastingBarUiLaw.FlashSeconds +
                    CastingBarUiLaw.FadeSeconds;
                EmitCastBarVerdict("CHANNEL_STOP", _castBarSpell);
                EmitChannelVerdict("STOP", source: "MSG_CHANNEL_UPDATE");
            }
            CancelControlledSpellVisual();
            if (_net is not null)
            {
                _spellSounds?.StopHold(ControlledGuid);
                _spellEffects?.Reap(ControlledGuid, _castBarSpell, StageLife.Persistent);
                _spellChainBeams?.Reap(ControlledGuid, _castBarSpell);
            }
            return;
        }
        CastingBarUiLaw.ChannelWindow window = CastingBarUiLaw.RetimeChannel(
            _castBarStarted, _castBarEnds, NowSeconds(), remainingMs);
        _castBarStarted = window.Start;
        _castBarEnds = window.End;
        EmitCastBarVerdict("CHANNEL_UPDATE", _castBarSpell, remainingMs);
        EmitChannelVerdict("UPDATE", remainingMs: remainingMs, source: "MSG_CHANNEL_UPDATE");
    }

    private void BeginChannel(uint spellId, uint durationMs)
    {
        BeginCastBar(spellId, durationMs, channel: true);
        EmitChannelVerdict("START", durationMs, durationMs, source: "MSG_CHANNEL_START");
        uint visual = _spellCatalog?.TryGet(spellId, out SpellInfo info) == true ? info.VisualId : 0;
        SpellVisualKitInfo? channelResolved = ResolveSpellKit(visual, static s => s.Channel);
        ushort? animation = channelResolved?.AnimationId;
        if (_net is not null)
        {
            _spellSounds?.StopHold(ControlledGuid);
            PlaySpellSound(ControlledGuid, channelResolved?.Sound);
        }
        if (ControlledBodyIsStreamed) _creatures?.BeginSpellVisual(ControlledGuid, animation);
        else if (!ControlledBodyTacticallyFrozen) _character?.BeginSpellVisual(animation);
        if (_spellCatalog?.TryGet(spellId, out SpellInfo channelInfo) == true &&
            _spellVisualCatalog?.TryGetStages(channelInfo.VisualId, out SpellVisualStages channelStages) == true)
            EmitSpellAnimation(channelInfo, "CHANNEL", channelStages.Channel, animation, "SERVER_CHANNEL");
        if (ResolveSpellKit(visual, static s => s.Channel) is { } channelKit && _net is not null)
        {
            _spellEffects?.SpawnKit(ControlledGuid, spellId, channelKit, persistent: true, NowSeconds(), "CHANNEL");
            ulong? channelObject = _entities.TryGet(ControlledGuid, out WorldEntity controlled)
                ? controlled.Fields.ChannelObject : null;
            _spellChainBeams?.Play(ControlledGuid, spellId, visual, channelKit,
                spellId, channelObject, NowSeconds());
        }
    }

    private void ApplySpellChainTargets(in SpellChainTargetsPacket packet)
    {
        if (_spellChainBeams is null) return;
        _spellChainBeams.StoreHops(packet.Caster, packet.Targets);

        uint channelSpell = 0;
        ulong? channelObject = null;
        if (_entities.TryGet(packet.Caster, out WorldEntity unit))
        {
            channelSpell = unit.Fields.ChannelSpell;
            channelObject = unit.Fields.ChannelObject;
        }
        if (packet.Caster == ControlledGuid && _castBarPhase == CastBarPhase.Channel)
            channelSpell = _castBarSpell;
        if (channelSpell != packet.SpellId) return;

        SpellInfo? info = _spellCatalog?.TryGet(packet.SpellId, out SpellInfo found) == true
            ? found : null;
        uint visual = EffectiveSpellVisual(info, packet.Caster);
        if (ResolveSpellKit(visual, static stages => stages.Channel) is { } kit)
            _spellChainBeams.Play(packet.Caster, packet.SpellId, visual, kit,
                channelSpell, channelObject, NowSeconds());
    }

    private void ApplyPushedVisual(ulong unit, uint kitId)
    {
        if (_spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
        _spellEffects?.SpawnKit(unit, 0, kit, persistent: false, NowSeconds(), "PUSHED");
        PlaySpellSound(unit, kit.Sound);
        if (_net is not null && unit == ControlledGuid && !ControlledBodyIsStreamed)
        {
            if (!ControlledBodyTacticallyFrozen)
                _character?.ReleaseSpellVisual(kit.AnimationId);
        }
        else
            _creatures?.ReleaseSpellVisual(unit, kit.AnimationId);
    }

    private void DrawCastingBar()
    {
        if (_castBarPhase == CastBarPhase.Hidden || _gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(CastingBarUiLaw.ArtworkWidth, CastingBarUiLaw.ArtworkHeight);
        // Both bottom multibars are always drawn, including their empty-slot rings. Their
        // visibility, not whether an action happens to occupy a slot, drives UIParent's
        // bottomEither term. The old occupancy check left an empty but visible row crossing
        // the cast bar at the unmanaged 60 px offset.
        bool petOrStance = PetOrStanceActionBarVisible;
        float bottom = CastingBarUiLaw.BottomOffsetForMsui(petOrStance, reputation: false);
        Vector2 barMin = new((display.X - CastingBarUiLaw.Width * s) * .5f,
            display.Y - (bottom + CastingBarUiLaw.Height) * s);
        Vector2 p = barMin - new Vector2(
            (CastingBarUiLaw.ArtworkWidth - CastingBarUiLaw.Width) * .5f,
            CastingBarUiLaw.ArtworkTopOffset) * s;
        Vector2 authored = p / s;
        CollectGameplayLayout("cast-bar", authored.X, authored.Y, size.X, size.Y, p, size * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs;
        if (!ImGui.Begin("##casting-bar", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 barSize = new(CastingBarUiLaw.Width, CastingBarUiLaw.Height);
        bool parityProof = _uiParityArmed && _uiParityPanel == "cast-bar";
        if (parityProof) BeginUiParityFrame(barMin, s);
        Vector4 windowClip = new(p.X, p.Y, p.X + size.X * s, p.Y + size.Y * s);
        double now = NowSeconds();
        double finishedElapsed = Math.Max(0d, now - _castBarFinishedAt);
        float frameAlpha = _castBarPhase switch
        {
            CastBarPhase.Success => CastingBarUiLaw.FrameAlpha(finishedElapsed, failed: false),
            CastBarPhase.Failed => CastingBarUiLaw.FrameAlpha(finishedElapsed, failed: true),
            _ => 1f,
        };
        uint whiteTint = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, frameAlpha));
        uint backgroundTint = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0, 0, 0, .5f * frameAlpha));
        dl.AddRectFilled(barMin, barMin + barSize * s, backgroundTint);
        if (parityProof)
            CollectUiParityDraw("CastingBarFrameBackground", "ColorTexture", barMin,
                barSize * s, "CastingBarFrame",
                new("", backgroundTint, "BACKGROUND", "TOPLEFT", "CastingBarFrame", "TOPLEFT",
                    0, 0, ContentRect: new Vector4(barMin.X, barMin.Y,
                        barMin.X + barSize.X * s, barMin.Y + barSize.Y * s),
                    ClipRect: windowClip, BlendMode: "BLEND", Visible: true,
                    InteractionState: _castBarPhase.ToString().ToLowerInvariant(), Strata: "MEDIUM"));
        float fraction = _castBarPhase switch
        {
            CastBarPhase.Casting => CastingBarUiLaw.Progress(
                _castBarStarted, _castBarEnds, now, channel: false),
            CastBarPhase.Channel => CastingBarUiLaw.Progress(
                _castBarStarted, _castBarEnds, now, channel: true),
            _ => 1f,
        };
        // Observational capture records the live spell lifecycle fraction. Only the explicit
        // `ui-parity-stage cast-bar` fixture pins the authored half-progress sample.
        if (parityProof && _uiParityFixtureStaged) fraction = .5f;
        if (parityProof) SnapshotUiParityScenario(now, fraction);
        Vector4 color = _castBarPhase switch
        {
            CastBarPhase.Success => new(0, 1, 0, frameAlpha),
            CastBarPhase.Failed => new(1, 0, 0, frameAlpha),
            _ => new(1, .7f, 0, frameAlpha),
        };
        CastingBarUiLaw.StatusFill fill = CastingBarUiLaw.Fill(fraction);
        uint fillTint = ImGui.ColorConvertFloat4ToU32(color);
        uint status = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-StatusBar");
        if (status != 0 && fill.Fraction > 0)
        {
            Vector2 fillMax = new(barMin.X + fill.Width * s, barMin.Y + barSize.Y * s);
            dl.AddImage((nint)status, barMin, fillMax, Vector2.Zero,
                new Vector2(fill.U1, 1), fillTint);
            if (parityProof)
                CollectUiParityDraw("CastingBarFrame", "StatusBar", barMin, barSize * s,
                    "UIParent", new(@"Interface\TargetingFrame\UI-StatusBar", fillTint,
                        "BORDER", "BOTTOM", "UIParent", "BOTTOM", 0, bottom,
                        TexCoords: $"0|0|{fill.U1:R}|1",
                        ContentRect: new Vector4(barMin.X, barMin.Y, fillMax.X, fillMax.Y),
                        ClipRect: windowClip, BlendMode: "BLEND", Visible: true,
                        InteractionState: _castBarPhase.ToString().ToLowerInvariant(),
                        Strata: "MEDIUM"));
        }
        uint border = _gameplayArt.Handle(@"Interface\CastingBar\UI-CastingBar-Border");
        if (border != 0)
        {
            dl.AddImage((nint)border, p, p + size * s, Vector2.Zero, Vector2.One, whiteTint);
            if (parityProof)
                CollectUiParityDraw("CastingBarBorder", "Texture", p, size * s,
                    "CastingBarFrame", new(@"Interface\CastingBar\UI-CastingBar-Border",
                            whiteTint, "ARTWORK", "TOP", "CastingBarFrame", "TOP", 0,
                            CastingBarUiLaw.ArtworkTopOffset,
                        TexCoords: "0|0|1|1", ContentRect: windowClip, ClipRect: windowClip,
                        BlendMode: "BLEND", Visible: true, Strata: "MEDIUM"));
        }
        float textSize = 12f * s;
        Vector2 textExtent = ImGui.CalcTextSize(_castBarText) *
            (textSize / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 textCenter = barMin + new Vector2(CastingBarUiLaw.Width * .5f, 3f) * s;
        Vector2 textMin = textCenter - textExtent * .5f;
        string castFontPath = !string.IsNullOrEmpty(_window.UiFontPath) &&
            File.Exists(_window.UiFontPath) ? FontFace.FrizQt : "";
        DrawCenteredText(dl, textCenter, _castBarText, textSize, whiteTint);
        if (parityProof)
            CollectUiParityDraw("CastingBarText", "FontString", textMin, textExtent,
                "CastingBarFrame", new("", whiteTint, "ARTWORK", "CENTER",
                    "CastingBarFrame", "CENTER", 0, 3.5f, castFontPath, 12,
                    ContentRect: new Vector4(textMin.X, textMin.Y,
                        textMin.X + textExtent.X, textMin.Y + textExtent.Y),
                    ClipRect: windowClip, BlendMode: "BLEND", Visible: true,
                    InteractionState: _castBarPhase.ToString().ToLowerInvariant(),
                    Strata: "MEDIUM"));
        if (_castBarPhase == CastBarPhase.Success)
        {
            float flashAlpha = CastingBarUiLaw.FlashAlpha(finishedElapsed) * frameAlpha;
            uint flash = _gameplayArt.AdditiveHandle(@"Interface\CastingBar\UI-CastingBar-Flash");
            if (flash != 0)
            {
                uint flashTint = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(1f, 1f, 1f, flashAlpha));
                dl.AddImage((nint)flash, p, p + size * s, Vector2.Zero, Vector2.One,
                    flashTint);
                if (parityProof)
                    CollectUiParityDraw("CastingBarFlash", "Texture", p, size * s,
                        "CastingBarFrame", new(@"Interface\CastingBar\UI-CastingBar-Flash",
                            flashTint, "OVERLAY", "TOP", "CastingBarFrame", "TOP", 0,
                            CastingBarUiLaw.ArtworkTopOffset,
                            TexCoords: "0|0|1|1", ContentRect: windowClip,
                            ClipRect: windowClip, BlendMode: "ADD", Visible: true,
                            InteractionState: "success", Strata: "MEDIUM"));
            }
        }
        if (_castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel)
        {
            // CastingBar.xml: the 32x32 moving spark is alphaMode="ADD".
            uint spark = _gameplayArt.AdditiveHandle(@"Interface\CastingBar\UI-CastingBar-Spark");
            if (spark != 0)
            {
                float x = barMin.X + CastingBarUiLaw.SparkCenter(fraction) * s;
                // CastingBarFrame_OnUpdate seats the spark CENTER on the bar's LEFT edge at
                // y=+2 (FrameXML coordinates are Y-up). The old -9.5/+22.5 bounds centered it
                // on the bar instead, two pixels below the authored moving anchor.
                Vector2 sparkMin = new(x - CastingBarUiLaw.SparkSize * .5f * s,
                    barMin.Y + CastingBarUiLaw.SparkMinY * s);
                Vector2 sparkMax = new(x + CastingBarUiLaw.SparkSize * .5f * s,
                    barMin.Y + CastingBarUiLaw.SparkMaxY * s);
                dl.AddImage((nint)spark, sparkMin, sparkMax, Vector2.Zero, Vector2.One);
                if (parityProof)
                    CollectUiParityDraw("CastingBarSpark", "Texture", sparkMin,
                        sparkMax - sparkMin, "CastingBarFrame",
                        new(@"Interface\CastingBar\UI-CastingBar-Spark", 0xffffffff,
                            "OVERLAY", "CENTER", "CastingBarFrame", "LEFT",
                            CastingBarUiLaw.SparkCenter(fraction), CastingBarUiLaw.SparkOffsetY,
                            TexCoords: "0|0|1|1",
                            ContentRect: new Vector4(sparkMin.X, sparkMin.Y,
                                sparkMax.X, sparkMax.Y), ClipRect: windowClip,
                            BlendMode: "ADD", Visible: true,
                            InteractionState: _castBarPhase.ToString().ToLowerInvariant(),
                            Strata: "MEDIUM"));
            }
        }
        if (_uiParityArmed && _uiParityPanel == "cast-bar") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private bool TryUnitPosition(ulong guid, out Vector3 position)
    {
        if (guid == ControlledGuid && TryGetWorldBodyPose(guid, out WorldBodyPose bodyPose))
        {
            position = bodyPose.Position;
            return true;
        }
        if (_entities.TryGet(guid, out WorldEntity unit))
        {
            position = unit.Type == ObjectTypeId.DynamicObject
                ? DynamicObjectWorldPosition(unit) : unit.Position;
            return true;
        }
        position = default; return false;
    }

    private SpellUnitPose SpellEffectUnitPose(ulong guid)
    {
        // The first-person body's pose is the CONTROLLER's — which in the free view is the
        // fly rig, i.e. the middle of the screen. A spell cast from up there has to come out
        // of the caster standing in the world, so fall through to its streamed pose.
        if (guid == ControlledGuid && ControllerOwnsControlledBodyPose && !ControlledUsesDisplayModel)
            return _character?.SpellPose(BuildUnitState()) ?? SpellUnitPose.Missing;
        if (_creatures?.TryGetSpellPose(guid, out SpellUnitPose pose) == true)
            return pose;
        if (_entities.TryGet(guid, out WorldEntity unit))
        {
            Vector3 position = unit.Type == ObjectTypeId.DynamicObject
                ? DynamicObjectWorldPosition(unit) : unit.Position;
            return new SpellUnitPose(true, position, unit.Orientation,
                Matrix4x4.CreateTranslation(position), null, null);
        }
        return SpellUnitPose.Missing;
    }

    private float? SpellGroundHeight(float x, float y, float authoredZ)
    {
        float? terrain = _terrain?.SampleHeight(x, y);
        float? collision = _collision?.Raycast(new Vector3(x, y, authoredZ + 3f),
            -Vector3.UnitZ, 6f)?.Point.Z;
        if (collision is float solid && MathF.Abs(solid - authoredZ) <= 3f) return solid;
        return terrain;
    }

    private float? SpellParticleGroundHeight(float x, float y, float authoredZ)
    {
        float? solid = _collision?.Raycast(new Vector3(x, y, authoredZ + .01f),
            -Vector3.UnitZ, 20.01f)?.Point.Z;
        float? terrain = _terrain?.SampleHeight(x, y);
        float? best = null;
        if (solid is float s && authoredZ - s is >= 0f and <= 20f) best = s;
        if (terrain is float t && authoredZ - t is >= 0f and <= 20f &&
            (best is null || t > best.Value)) best = t;
        return best;
    }

    /// <summary>
    /// Weather casts from its kind's fixed spawn plane and must see the highest
    /// terrain/WMO/doodad roof below that plane. A miss is left to the weather
    /// law's exact 200-yard fallback rather than being confused with sea level.
    /// </summary>
    private float? WeatherGroundHeight(float x, float y, float castZ)
    {
        float? solid = _collision?.Raycast(new Vector3(x, y, castZ),
            -Vector3.UnitZ, WeatherPrecipitationLaw.RetireDistance + 50f)?.Point.Z;
        float? terrain = _terrain?.SampleHeight(x, y);
        float? best = null;
        if (solid is float s && s <= castZ) best = s;
        if (terrain is float t && t <= castZ && (best is null || t > best.Value)) best = t;
        return best;
    }

    private void UpdateAuraStateVisuals(double now)
    {
        var seen = new HashSet<(ulong Unit, uint Spell)>();
        foreach (WorldEntity unit in _entities.Entities.Values.Where(e => e.IsUnit))
        {
            var body = new List<AuraBodySpell>();
            foreach (uint spell in unit.Fields.Auras().Select(a => a.SpellId).Distinct())
            {
                var key = (unit.Guid, spell);
                seen.Add(key);
                uint visual = _spellCatalog?.TryGet(spell, out SpellInfo info) == true
                    ? EffectiveSpellVisual(info, unit.Guid) : 0;
                if (_spellVisualCatalog?.TryGetStageKit(visual, SpellStage.State,
                        out SpellVisualKitInfo stateKit, out StageLife life) != true) continue;

                AuraBodyNode[] nodes = AuraVisualLaw.Nodes(stateKit.CharProcs);
                if (nodes.Length != 0) body.Add(new AuraBodySpell(spell, nodes));

                if (_spellEffects is null || _activeAuraStateFx.Contains(key)) continue;
                int spawned = _spellEffects.SpawnKit(unit.Guid, spell, stateKit, life, now, "STATE");
                // A failed resolve/load is not an active state. Leaving the key absent makes the
                // descriptor watcher retry instead of suppressing the aura for its whole lifetime.
                if (spawned > 0) _activeAuraStateFx.Add(key);
            }
            unit.AuraVisual.Reconcile(_creatures?.DisplayBaseAlpha(unit.DisplayId) ?? 1f,
                body, now);
        }
        foreach (var stale in _activeAuraStateFx.Where(k => !seen.Contains(k)).ToArray())
        {
            _spellEffects?.Reap(stale.Unit, stale.Spell, StageLife.AuraState);
            _activeAuraStateFx.Remove(stale);
        }
    }

    /// <summary>
    /// Dynamic-object area visuals. SpellVisual fields 11/12 provide the looping centre model;
    /// field 13's area kit provides the type-9 shard emitter and its looping sound. The two visual
    /// paths are deliberately distinct in the 1.12 client.
    /// </summary>
    private void UpdateDynamicObjectVisuals(double now)
    {
        if (_spellEffects is null) return;
        HashSet<ulong>? seen = null;
        foreach (WorldEntity obj in _entities.Entities.Values
                     .Where(e => e.Type == ObjectTypeId.DynamicObject))
        {
            (seen ??= []).Add(obj.Guid);
            uint spell = obj.Fields.GetU32(9) ?? 0;                       // DYNAMICOBJECT_SPELLID
            float rawRadius = obj.Fields.GetF32(10) ?? 0f;              // DYNAMICOBJECT_RADIUS
            float radius = float.IsFinite(rawRadius) ? Math.Max(0f, rawRadius) : 0f;
            Vector3 position = DynamicObjectWorldPosition(obj);          // DYNAMICOBJECT_POS_X/Y/Z
            uint visual = _spellCatalog?.TryGet(spell, out SpellInfo info) == true
                ? info.VisualId : 0;
            if (spell == 0 ||
                _spellVisualCatalog?.TryGetAreaVisual(visual, out SpellAreaVisualInfo area) != true)
            {
                if (_activeDynObjectFx.Remove(obj.Guid))
                {
                    _spellEffects.ReapArea(obj.Guid);
                    _spellSounds?.StopHold(obj.Guid);
                }
                continue;
            }

            var identity = new DynamicAreaFxIdentity(spell, visual);
            if (_activeDynObjectFx.TryGetValue(obj.Guid, out DynamicAreaFxIdentity current) &&
                current == identity)
            {
                _spellEffects.UpdateAreaVisual(obj.Guid, spell, position, radius);
                continue;
            }

            if (_activeDynObjectFx.Remove(obj.Guid))
            {
                _spellEffects.ReapArea(obj.Guid);
                _spellSounds?.StopHold(obj.Guid);
            }
            bool loopingSound = _spellSounds?.IsAuthoredLoop(area.Sound) == true;
            Action<uint, ulong, Vector3>? birthSound = !loopingSound && area.Emitters.Count != 0
                ? (sound, key, at) => PlaySpellSoundAt(key, sound, at, trackHold: false)
                : null;
            int spawned = _spellEffects.SpawnAreaVisual(
                obj.Guid, spell, area, position, radius, now, birthSound);
            if (loopingSound)
                PlaySpellSoundAt(obj.Guid, area.Sound, position, forceLoop: true);
            else if (area.Emitters.Count == 0)
                PlaySpellSoundAt(obj.Guid, area.Sound, position, trackHold: false);
            _activeDynObjectFx[obj.Guid] = identity;
            Console.WriteLine($"[dynobj-fx] spawn guid=0x{obj.Guid:X12} spell={spell} " +
                $"radius={radius:0.0} emitters={area.Emitters.Count} " +
                $"rate={area.Emitters.Sum(e => e.InstancesPerSecond):0.0}/s loaded={spawned} " +
                $"pos=({position.X:0.0},{position.Y:0.0},{position.Z:0.0})");
        }
        if (_activeDynObjectFx.Count == 0) return;
        foreach (ulong stale in _activeDynObjectFx.Keys.Where(g => seen?.Contains(g) != true).ToArray())
        {
            _spellEffects.ReapArea(stale);
            _spellSounds?.StopHold(stale);
            _activeDynObjectFx.Remove(stale);
        }
    }

    private readonly record struct DynamicAreaFxIdentity(uint Spell, uint Visual);
    private readonly Dictionary<ulong, DynamicAreaFxIdentity> _activeDynObjectFx = [];

    private static Vector3 DynamicObjectWorldPosition(WorldEntity obj)
    {
        float? x = obj.Fields.GetF32(11), y = obj.Fields.GetF32(12), z = obj.Fields.GetF32(13);
        return x is float px && y is float py && z is float pz &&
               float.IsFinite(px) && float.IsFinite(py) && float.IsFinite(pz)
            ? new Vector3(px, py, pz) : obj.Position;
    }

    private void UpdateObservedChannels(double now)
    {
        if (_spellEffects is null) return;
        var seen = new HashSet<ulong>();
        foreach (WorldEntity unit in _entities.Entities.Values.Where(e => e.IsUnit))
        {
            if (_net is not null && unit.Guid == ControlledGuid) continue;
            uint spell = unit.Fields.ChannelSpell;
            if (spell == 0) continue;
            seen.Add(unit.Guid);
            if (_activeObservedChannels.GetValueOrDefault(unit.Guid) == spell) continue;
            if (_activeObservedChannels.Remove(unit.Guid, out uint old))
            {
                _spellEffects.Reap(unit.Guid, old, StageLife.Persistent);
                _spellChainBeams?.Reap(unit.Guid, old);
                _spellSounds?.StopHold(unit.Guid);
            }
            SpellInfo? info = _spellCatalog?.TryGet(spell, out SpellInfo found) == true ? found : null;
            uint visual = EffectiveSpellVisual(info, unit.Guid);
            SpellVisualKitInfo? channel = ResolveSpellKit(visual, static s => s.Channel);
            if (channel is { } kit)
            {
                _spellEffects.SpawnKit(unit.Guid, spell, kit, StageLife.Persistent, now, "CHANNEL");
                _spellChainBeams?.Play(unit.Guid, spell, visual, kit,
                    spell, unit.Fields.ChannelObject, now);
                _creatures?.BeginSpellVisual(unit.Guid, kit.AnimationId);
                PlaySpellSound(unit.Guid, kit.Sound);
            }
            _activeObservedChannels[unit.Guid] = spell;
        }
        foreach (var stale in _activeObservedChannels.Keys.Where(g => !seen.Contains(g)).ToArray())
        {
            uint spell = _activeObservedChannels[stale];
            _spellEffects.Reap(stale, spell, StageLife.Persistent);
            _spellChainBeams?.Reap(stale, spell);
            _spellSounds?.StopHold(stale);
            _creatures?.CancelSpellVisual(stale);
            _activeObservedChannels.Remove(stale);
        }
    }

    private uint EffectiveSpellVisual(SpellInfo? info, ulong caster)
    {
        if (info is null) return 0;
        if (_spellVisualCatalog?.TryGetStages(info.Value.VisualId, out _) == true ||
            !info.Value.Ranged) return info.Value.VisualId;
        uint display = 0;
        if (_net is not null && caster == ControlledGuid &&
            !ControlledBodyIsStreamed && _character is not null)
            display = _character.Equipment.Pieces.LastOrDefault(p =>
                p.EquipmentSlot == 17 || p.InventoryType is 15 or 25 or 26)?.DisplayId ?? 0;
        else if (_entities.TryGet(caster, out WorldEntity unit))
            display = unit.Fields.VirtualItemDisplay(2);
        return _spellEffects?.ItemSpellVisual(display) ?? 0;
    }

    private static double NowSeconds() => MovementInfo.ClientUptimeMs() / 1000.0;
}
