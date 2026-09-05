using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // _actions moved to Program.Control.cs: it is now a per-unit store keyed by ControlledGuid.
    private SpellCatalog? _spellCatalog;
    private EnchantCatalog? _enchantCatalog;
    private SpellVisualCatalog? _spellVisualCatalog;
    private ShapeshiftFormCatalog? _shapeshiftForms;
    private GameplayArt? _gameplayArt;
    private readonly bool[] _actionKeyWasDown = new bool[12];
    private readonly bool[] _multiActionKeyWasDown = new bool[24];
    private readonly bool[] _multiActionKeyArmed = new bool[24];
    private readonly bool[] _shapeshiftKeyWasDown = new bool[10];
    private readonly bool[] _bonusActionKeyWasDown = new bool[10];
    private readonly bool[] _bonusActionKeyArmed = new bool[10];
    private bool _toggleActionBarLockWasDown;
    private long _actionUses;
    private int _pressedActionSlot = -1;
    private ImGuiMouseButton _actionPressMouseButton = ImGuiMouseButton.Left;
    private ActionSlot? _actionCursor;
    private bool _actionCursorChangedThisFrame;
    private bool _mainMenuMicroPressedThroughModal;
    private readonly bool[] _microBindingWasDown = new bool[3];
    private bool _partyQuestLogKeyWasDown;
    private Vector2 _actionPressPosition;
    private PreparedSharedSpellTooltip? _hoveredActionSpellTooltip;
    private int _hoveredActionSlot = -1;
    private uint _pendingCastSpell;
    private uint _autoRepeatSpell;
    private uint _queuedMeleeSpell;
    private double _globalCooldownUntil;
    private int _actionPage = 1;
    private const int ActionPageCount = 6;
    private readonly ActionButtonVerdict?[] _lastActionButtonVerdicts =
        new ActionButtonVerdict?[120];


    private void InitGameplayUi(GL gl)
    {
        if (_mpq is null) return;
        try
        {
            _spellCatalog = SpellCatalog.Load(_mpq);
            _enchantCatalog = EnchantCatalog.Load(_mpq);
            _spellVisualCatalog = SpellVisualCatalog.Load(_mpq);
            _shapeshiftForms = ShapeshiftFormCatalog.Load(_mpq);
            _gameplayArt = new GameplayArt(gl, _mpq);
            // These immutable catalogs are needed by the first WMO-interior
            // minimap frame. Pay their MPQ read/parse cost here behind startup
            // UI rather than on the seamless frame after a portal promotion.
            EnsureWmoAreaTableForMinimap();
            EnsureMinimapTileMap();
            Console.WriteLine(_spellCatalog is null
                ? "[actions] Spell/SpellIcon DBC unavailable"
                : $"[actions] spell catalog ready ({_spellCatalog.Count} rows)");
            Console.WriteLine(_spellVisualCatalog is null
                ? "[spell-fx] SpellVisual chain unavailable"
                : "[spell-fx] SpellVisual/Kit/EffectName chain ready");
        }
        catch (Exception ex) { Console.WriteLine($"[actions] UI initialization failed: {ex.Message}"); }
    }

    private void UpdateActionBarInput(bool typing)
    {
        for (int i = 0; i < _actionKeyWasDown.Length; i++)
        {
            bool altPrimaryCast = RtsCastOnPrimaryBindingDown(ActionBinding(i));
            bool down = BindingDown(ActionBinding(i)) || altPrimaryCast;
            if (down && !_actionKeyWasDown[i] && !typing &&
                !RtsControlGroupClaimsBinding(ActionBinding(i)))
            {
                // In Free View the bindable Action Button 1..10 keys belong to the
                // primary character card (the visible card currently supports eight).
                // Outside it, preserve the ordinary mount/action-bar routing.
                if (!TryUseRtsPrimaryAbilityBinding(i, altPrimaryCast) &&
                    !TryMountKitNumberKey(i + 1) && _net is { IsInWorld: true })
                    UseAction(ActionWireSlot(i));
            }
            _actionKeyWasDown[i] = down;
        }
        for (int barIndex = 0; barIndex < 2; barIndex++)
        {
            BottomMultiActionBar bar = barIndex == 0
                ? BottomMultiActionBar.Left : BottomMultiActionBar.Right;
            for (int i = 0; i < MultiActionBarUiLaw.ButtonsPerBar; i++)
            {
                int stateIndex = barIndex * MultiActionBarUiLaw.ButtonsPerBar + i;
                bool down = BindingDown(MultiActionBinding(bar, i));
                MultiActionKeyTransition transition = MultiActionBarUiLaw.AdvanceKey(
                    _multiActionKeyArmed[stateIndex], _multiActionKeyWasDown[stateIndex], down,
                    typing || RtsControlGroupClaimsBinding(MultiActionBinding(bar, i)),
                    _net is { IsInWorld: true });
                _multiActionKeyArmed[stateIndex] = transition.Armed;
                if (transition.Fire)
                    UseAction(MultiActionBarUiLaw.WireSlot(bar, i));
                _multiActionKeyWasDown[stateIndex] = down;
            }
        }
        UpdateMicroMenuBindingInput(typing);
        UpdateSocialTabBindings(typing);
        UpdateActionBarTailBindings(typing);
    }

    private void UpdateActionBarTailBindings(bool typing)
    {
        IReadOnlyList<SpellInfo> forms = CurrentStanceForms();
        WorldEntity? player = _entities.TryGet(ControlledGuid, out WorldEntity self) ? self : null;
        for (int i = 0; i < _shapeshiftKeyWasDown.Length; i++)
        {
            bool down = BindingDown(ShapeshiftBinding(i));
            if (down && !_shapeshiftKeyWasDown[i] && !typing && i < forms.Count)
            {
                SpellInfo spell = forms[i];
                ActivateStanceSpell(spell, StanceSpellActive(spell, player));
            }
            _shapeshiftKeyWasDown[i] = down;
        }

        bool inWorld = _net is { IsInWorld: true };
        for (int i = 0; i < _bonusActionKeyWasDown.Length; i++)
        {
            bool down = BindingDown(BonusActionBinding(i));
            // The pet bar defaults to Ctrl+1..0, which in the free view is the control-group
            // ASSIGN chord. Yield the numerals there exactly as the main and multi bars do:
            // without this, Ctrl+1 saved a group and fired pet action 1 in the same frame.
            MultiActionKeyTransition transition = MultiActionBarUiLaw.AdvanceKey(
                _bonusActionKeyArmed[i], _bonusActionKeyWasDown[i], down,
                typing || RtsControlGroupClaimsBinding(BonusActionBinding(i)), inWorld);
            _bonusActionKeyArmed[i] = transition.Armed;
            _bonusActionKeyWasDown[i] = down;
            if (transition.Fire)
            {
                WorldEntity? pet = _entities.TryGet(_petGuid, out WorldEntity entity) &&
                    entity.IsUnit ? entity : null;
                UsePetAction(i, _petGuid, pet);
            }
        }

        bool lockDown = BindingDown(GameBinding.ToggleActionBarLock);
        if (lockDown && !_toggleActionBarLockWasDown && !typing)
            Settings.Controls.LockActionBars =
                ActionBarLockLaw.Toggle(Settings.Controls.LockActionBars);
        _toggleActionBarLockWasDown = lockDown;
    }

    private void UpdateMicroMenuBindingInput(bool typing)
    {
        bool partyQuestLogDown = BindingDown(GameBinding.OpenPartyQuestLog);
        if (partyQuestLogDown && !_partyQuestLogKeyWasDown && !typing &&
            _net is { IsInWorld: true })
        {
            if (_partyQuestLogOpen) _partyQuestLogOpen = false;
            else OpenPartyQuestLog();
        }
        _partyQuestLogKeyWasDown = partyQuestLogDown;

        (GameBinding Binding, MicroMenuButtonId Button)[] bindings =
        [
            (GameBinding.OpenTalents, MicroMenuButtonId.Talents),
            (GameBinding.OpenQuestLog, MicroMenuButtonId.QuestLog),
            (GameBinding.OpenSocial, MicroMenuButtonId.Social),
        ];
        for (int i = 0; i < bindings.Length; i++)
        {
            bool down = BindingDown(bindings[i].Binding);
            if (down && !_microBindingWasDown[i] && !typing && _net is { IsInWorld: true })
            {
                // Commander view (PLAN_20 P1): L is the party questing key —
                // everyone's log merged, the same way B became party bags. The
                // fork lives here, not in ActivateMicroMenuButton, which the
                // micro-menu mouse clicks also route through.
                if (_freeView && bindings[i].Button == MicroMenuButtonId.QuestLog)
                {
                    if (_partyQuestLogOpen) _partyQuestLogOpen = false;
                    else OpenPartyQuestLog();
                }
                else ActivateMicroMenuButton(bindings[i].Button);
            }
            _microBindingWasDown[i] = down;
        }
    }

    private void UseAction(int wireSlot)
    {
        if (_net is null || _actions[wireSlot] is not { } slot) return;
        if (BarsReadOnly) return;   // free-view inspection: bars display, never cast
        _actionUses++;
        switch (slot.Kind)
        {
            case ActionSlot.Spell when slot.ActionId == 6603:
                // 1.12: pressing Attack with nothing selected picks the nearest valid enemy
                // within a sensible range, rather than silently doing nothing.
                if (_selectionGuid == 0) AutoAcquireAttackTarget();
                if (_selectionGuid != 0) CommitSelection(_selectionGuid, beginAttack: true);
                break;
            case ActionSlot.Spell:
                // Hovercast (AddOns page, off by default) rebinds this one press onto the
                // unit under the cursor and returns 0 whenever it should not. Every bar,
                // key and mouse press already funnels through UseAction, so this is the
                // whole hook - the same single-chokepoint the 1.12 addon relied on.
                TryCast(slot.ActionId, HovercastTarget(slot));
                break;
            case ActionSlot.Item:
                UseItemAction(slot.ActionId);
                break;
            case ActionSlot.Macro:
                ExecuteMacro(slot.ActionId);
                break;
        }
    }

    /// <summary>Nearest valid enemy within <see cref="TargetCycleLaw.AttackAcquireRange"/>, or a
    /// no-op if none qualifies. Deliberately simpler than <see cref="CycleEnemyTarget"/>: this is
    /// a one-shot pick, not a cycle, so it skips that method's screen-off-center weighting and
    /// recent-history bookkeeping and just takes the closest eligible unit by distance.</summary>
    private void AutoAcquireAttackTarget()
    {
        if (!TryGetControlledBodyPose(out WorldBodyPose body)) return;
        ulong nearest = 0;
        float nearestDistance = float.PositiveInfinity;
        foreach (WorldEntity unit in _entities.Units)
        {
            if (unit.Guid == ControlledGuid || unit.Fields.ReadsDead || !CanAttack(unit)) continue;
            if (unit.IsCreature &&
                _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? query) &&
                query?.CreatureType == 8)
                continue;
            float distance = Vector3.Distance(unit.Position, body.Position);
            if (distance > TargetCycleLaw.AttackAcquireRange || distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = unit.Guid;
        }
        if (nearest != 0) CommitSelection(nearest, beginAttack: false);
    }

    private void TryCast(uint spellId, ulong explicitTarget = 0)
    {
        if (_net is null) return;
        if (_spellCatalog is null || !_spellCatalog.TryGet(spellId, out SpellInfo spell))
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, 0, sent: false);
            return;
        }
        if (TryOpenProfession(spellId))
        {
            EmitCastVerdict(spellId, CastTargetReason.ProfessionWindow, 0, sent: false);
            return;
        }
        // Your own logged-in character stays castable from the sky: a guid-less CMSG_CAST_SPELL
        // applies to _player, and you remain its self-mover while not possessing a bot. Only a
        // detached Free View cursor commanding someone else's body (ControlledGuid != you, no
        // possession) is refused. A possessed bot is covered by CanAuthorControlledGameplay.
        if (!CanAuthorControlledOrSelf)
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, 0, sent: false);
            RefuseCast(spellId, "LOCAL_OBSERVER", "Cannot cast while in Free View.");
            return;
        }
        if (spell.Passive)
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, 0, sent: false);
            return;
        }
        if (!_actions.KnownSpells.Contains(spellId))
        {
            EmitCastVerdict(spellId, CastTargetReason.UnknownSpell, 0, sent: false);
            RefuseCast(spellId, "LOCAL_UNKNOWN_SPELL", "You have not learned that spell.");
            return;
        }
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        if (spell.AutoRepeat && _autoRepeatSpell == spellId)
        {
            _net.CancelAutoRepeat();
            _autoRepeatSpell = 0;
            SetVisualSheath(0);
            CancelControlledSpellVisual();
            return;
        }
        if (spell.OnNextSwing && _queuedMeleeSpell == spellId)
        {
            EmitCastVerdict(spellId, CastTargetReason.AlreadyQueued, 0, sent: false);
            return;
        }
        if (_actions.IsOnCooldown(spellId, 0, spell, now))
        {
            EmitCastVerdict(spellId, CastTargetReason.CooldownOrGlobalCooldown, 0, sent: false);
            RefuseCast(spellId, "LOCAL_COOLDOWN", "Spell is not ready yet.");
            return;
        }
        if (!spell.AutoRepeat && !spell.OnNextSwing && _pendingCastSpell != 0)
        {
            // This gate is only reached once the GCD has elapsed (checked above), so the
            // only cast still legitimately in flight here is a timed one whose cast bar
            // outlasts the global cooldown. If no cast bar / channel is up, the pending
            // lock is stale: its SMSG_SPELL_GO was never received (observed with Arcane
            // Explosion 1449, whose only server GO in a run was the Clearcasting proc
            // 12536, never spell 1449 itself). Clear it so one dropped GO cannot deadlock
            // every future cast (the baseline symptom: Frost Nova refused "Another action
            // is in progress" 2.4s after the AoE, GCD already ready).
            if (_castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel)
            {
                if (_pendingCastSpell != spellId) RefuseCast(spellId, "LOCAL_SPELL_IN_PROGRESS", "Another action is in progress");
                EmitCastVerdict(spellId, CastTargetReason.PendingCast, 0, sent: false);
                return;
            }
            _pendingCastSpell = 0;
        }
        if (_entities.TryGet(ControlledGuid, out WorldEntity caster))
        {
            if (caster.Fields.MountDisplayId != 0 && (spell.Attributes & 0x0100_0000u) == 0)
            {
                EmitCastVerdict(spellId, CastTargetReason.Mounted, 0, sent: false);
                RefuseCast(spellId, "LOCAL_MOUNTED", "You are mounted");
                return;
            }
        }

        SpellReagent? missingReagent = _spellCatalog.Reagents(spellId)
            .FirstOrDefault(reagent => CarriedCount(reagent.ItemId) < reagent.Count);
        if (missingReagent is { ItemId: not 0 } reagent)
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingReagent, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_REAGENT",
                $"Missing reagent {reagent.ItemId} ({CarriedCount(reagent.ItemId)}/{reagent.Count}).");
            return;
        }
        uint missingTool = _spellCatalog.Tools(spellId).FirstOrDefault(tool => CarriedCount(tool) == 0);
        if (missingTool != 0)
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingTool, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_TOOL", $"Requires item {missingTool}.");
            return;
        }
        if (!HasNearbySpellFocus(spell.RequiredFocus))
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingSpellFocus, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_FOCUS",
                $"Requires {SpellFocusName(spell.RequiredFocus)}.");
            return;
        }

        CastTargetVerdict targetVerdict = ResolveCastTarget(spell, explicitTarget);
        if (_itemCastSpell != 0 && _itemCastSpell != spellId) CancelItemTargeting();
        ulong target = targetVerdict.Guid;
        if (targetVerdict.Kind == CastTargetKind.Ground)
        {
            // 1.12 targeting-cursor mode: the cast is armed, not sent — the next world
            // left-click binds a terrain point and commits (Program.Targeting.cs), a
            // right-click cancels. All the gates above have already passed.
            CancelItemTargeting();
            _groundCastSpell = spellId;
            EmitCastVerdict(spellId, CastTargetReason.GroundTargeting, 0, sent: false);
            return;
        }
        if (targetVerdict.Kind == CastTargetKind.Item)
        {
            _groundCastSpell = 0;
            ClearEnchantConfirmation();
            _itemCastSpell = spellId;
            EmitCastVerdict(spellId, CastTargetReason.ItemTargeting, 0, sent: false);
            return;
        }
        if (targetVerdict.Kind == CastTargetKind.Refused)
        {
            EmitCastVerdict(spellId, targetVerdict.Reason, target, sent: false);
            RefuseCast(spellId, targetVerdict.Reason.ToString(),
                explicitTarget == 0 && _selectionGuid == 0
                    ? "You have no target." : "Invalid target");
            return;
        }
        if (target != ControlledGuid &&
            CastRangeRefusal(spell, target) is { } rangeFailure)
        {
            EmitCastVerdict(spellId, rangeFailure.Reason, target, sent: false);
            RefuseCast(spellId, $"LOCAL_{rangeFailure.Reason}", rangeFailure.Text);
            return;
        }
        if (!SpellResourceGate(spell, out _, out _))
        {
            EmitCastVerdict(spellId, CastTargetReason.NotEnoughPower, target, sent: false);
            RefuseCast(spellId, "LOCAL_NO_POWER", $"Not enough {PowerName((byte)spell.PowerType).ToLowerInvariant()}");
            return;
        }
        CommitCastSend(spell, spellId, target, ground: null, targetVerdict.Reason);
    }

    /// <summary>
    /// The send tail shared by unit/self casts and ground-bound casts: ship the packet,
    /// record the verdict, then arm pending/auto-repeat state and the GCD.
    /// </summary>
    private void CommitCastSend(in SpellInfo spell, uint spellId, ulong target,
        Vector3? ground, CastTargetReason reason)
    {
        if (_net is null || _actions is null) return;
        // Your own logged-in character stays castable from the sky (server accepts it — GetSuiActor
        // resolves to _player and there's no self-mover guard). Only a detached Free View cursor
        // commanding someone else's body without possession is blocked. Mirrors the TryCast gate.
        if (!CanAuthorControlledOrSelf)
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, target, sent: false);
            return;
        }
        if (target != 0 && RefuseTacticalFrozenActor(target, "target it with a live spell"))
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, target, sent: false);
            return;
        }
        bool sent = ground is { } dest
            ? _net.CastSpellAtLocation(spellId, dest)
            : _net.CastSpell(spellId, target);
        EmitCastVerdict(spellId, reason, target, sent);
        if (!sent) return;
        if (spell.AutoRepeat) _autoRepeatSpell = spellId;
        else if (spell.OnNextSwing) _queuedMeleeSpell = spellId;
        else _pendingCastSpell = spellId;
        if (spell.StartRecoveryMs > 0)
        {
            double now = NowSeconds();
            _globalCooldownUntil = now + spell.StartRecoveryMs / 1000.0;
            _actions.StartGlobalCooldown(spellId, spell, now);
        }
    }

    /// <summary>Commit an armed ground-target cast at a bound world point.</summary>
    private void CommitGroundCast(uint spellId, Vector3 dest)
    {
        _groundCastSpell = 0;
        if (_spellCatalog is null || !_spellCatalog.TryGet(spellId, out SpellInfo spell)) return;
        CommitCastSend(spell, spellId, 0, dest, CastTargetReason.GroundTargeting);
    }

    /// <summary>Armed ground-target spell awaiting a terrain click; 0 = not targeting.</summary>
    private uint _groundCastSpell;

    /// <summary>Armed item-target spell awaiting an occupied bag or paper-doll click.</summary>
    private uint _itemCastSpell;

    private void CommitItemCast(uint spellId, ulong itemGuid)
    {
        if (!CanAuthorControlledGameplay || _net is null || _spellCatalog is null ||
            !_spellCatalog.TryGet(spellId, out SpellInfo spell)) return;
        bool sent = _net.CastSpellOnItem(spellId, itemGuid);
        EmitCastVerdict(spellId, CastTargetReason.ItemTargeting, itemGuid, sent);
        if (!sent) return;
        _itemCastSpell = 0;
        ClearEnchantConfirmation();
        if (spell.AutoRepeat) _autoRepeatSpell = spellId;
        else if (spell.OnNextSwing) _queuedMeleeSpell = spellId;
        else _pendingCastSpell = spellId;
        if (spell.StartRecoveryMs > 0)
        {
            double now = NowSeconds();
            _globalCooldownUntil = now + spell.StartRecoveryMs / 1000.0;
            _actions.StartGlobalCooldown(spellId, spell, now);
        }
    }

    private void CancelItemTargeting()
    {
        _itemCastSpell = 0;
        ClearEnchantConfirmation();
    }

    private bool TryCancelSpellTargetingOnEscape()
    {
        if (CancelRtsUnitCastTargeting(silent: false)) return true;
        if (_tacticalGroundSpellId != 0)
        {
            CancelTacticalGroundCast(silent: false);
            return true;
        }
        if (_groundCastSpell != 0)
        {
            _groundCastSpell = 0;
            _groundCursorPoint = null;
            return true;
        }
        if (_itemCastSpell == 0) return false;
        CancelItemTargeting();
        return true;
    }

    private void RefuseCast(uint spellId, string reason, string text) =>
        ShowSpellError(spellId, reason, text, "LOCAL_GATE");

    private (string Text, CastTargetReason Reason)? CastRangeRefusal(in SpellInfo spell,
        ulong targetGuid)
    {
        if (_net is null || !TryGetControlledBodyPose(out WorldBodyPose controlledBody) ||
            _spellCatalog is null || targetGuid == 0 ||
            !_entities.TryGet(targetGuid, out WorldEntity target) ||
            !_spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow row)) return null;
        float selfReach = _entities.TryGet(ControlledGuid, out WorldEntity self)
            ? self.Fields.CombatReach : 1.5f;
        float targetReach = target.Fields.CombatReach;
        float min = row.Min, max = row.Max;
        if (row.Melee) { min = 0f; max = MathF.Max(selfReach + targetReach + 1.3333f, 5f); }
        else
        {
            if (min <= 0f && max <= 0f) return null;
            max += selfReach + targetReach;
            if (min != 0f) min += selfReach + targetReach;
        }
        float d2 = Vector3.DistanceSquared(controlledBody.Position, target.Position);
        if (min > 0f && d2 < min * min)
            return ("Target too close", CastTargetReason.TooClose);
        return d2 > max * max ? ("Out of range.", CastTargetReason.OutOfRange) : null;
    }

    // Benilla cast_target.rs transcribes Spell_C::ArmCast/BindTarget: seed the target word from
    // Spell.dbc Targets, apply EffectImplicitTargetA[0], then satisfy every unit-shaped bit.
    // A hostile selection therefore cannot receive Holy Light; autoSelfCast binds the player.
    private CastTargetVerdict ResolveCastTarget(in SpellInfo spell, ulong explicitTarget = 0)
    {
        CastTargetCandidate? selected = null, self = null;
        ulong selectedGuid = explicitTarget != 0 ? explicitTarget : _selectionGuid;
        if (selectedGuid != 0 && _entities.TryGet(selectedGuid, out WorldEntity selectedEntity))
        {
            selected = CastCandidate(selectedEntity, selectedGuid == ControlledGuid);
            EmitCombat("SpellTargetCandidate", "cast-acting-path", selectedEntity.Guid,
                $"spell={spell.Id};mask=0x{CastTargetLaw.TargetMask(spell):X4};isSelf={selected.Value.IsSelf};" +
                $"friendly={selected.Value.Friendly};attackable={selected.Value.Attackable};dead={selected.Value.Dead};" +
                $"unitFlags=0x{selectedEntity.Fields.UnitFlags:X8};faction={selectedEntity.Fields.FactionTemplate};" +
                $"reaction={ReactionPlayerToward(selectedEntity)}");
        }
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
            self = CastCandidate(player, isSelf: true);
        return CastTargetLaw.Resolve(spell, selected, self, autoSelfCast: explicitTarget == 0);
    }

    private void EmitCastVerdict(uint spellId, CastTargetReason reason, ulong resolvedGuid, bool sent)
    {
        var verdict = new CastVerdict(
            NowSeconds(), spellId, reason, _selectionGuid, resolvedGuid, sent);
        _verdicts.Add(verdict);
        EmitSpellSweep(spellId, reason, resolvedGuid, sent);
        if (!sent || resolvedGuid != _selectionGuid)
            Console.WriteLine($"[verdict:cast] {verdict.ToLine()}");
    }

    private CastTargetCandidate CastCandidate(WorldEntity candidate, bool isSelf) => new(
        candidate.Guid, isSelf,
        isSelf || ReactionPlayerToward(candidate) == FactionReaction.Friendly,
        !isSelf && CanAttack(candidate), candidate.IsDead);

    private void DrawActionBars()
    {
        if ((_net is not { IsInWorld: true } && !HudPreview) || _gameplayArt is null) return;
        // The free view is the commander console (Ctrl+F is a costume change):
        // body-driving chrome stands down. The selected unit's abilities show on
        // the console's unit card instead, and the numerals are group keys there.
        if (_freeView) return;
        _hoveredActionSpellTooltip = null;
        _hoveredActionSlot = -1;
        _actionCursorChangedThisFrame = false;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float scale = GameplayUiScale();
        Vector2 barMin = GameplayBarMin(display, scale);
        CollectGameplayLayout("action-bar", 0f, 715f, 1024f, 53f,
            barMin, new Vector2(1024f, 53f) * scale);
        ImDrawListPtr bg = ImGui.GetBackgroundDrawList();

        if (_uiParityArmed && _uiParityPanel == "action-bar")
        {
            BeginUiParityFrame(barMin, scale);
            CollectUiParity("MainMenuBar", "Frame", barMin, new Vector2(1024, 53) * scale,
                parent: "", point: "BOTTOM", strata: "");
            CollectUiParity("MainMenuExpBar", "StatusBar", barMin, new Vector2(1024, 13) * scale,
                parent: "MainMenuBar", point: "TOP", texture: @"Interface\TargetingFrame\UI-StatusBar",
                strata: "");
            (string Name, float X, string Tex)[] xp =
            [
                ("MainMenuXPBarTexture0", 0, "0|0.79296875|1.0|0.83203125"),
                ("MainMenuXPBarTexture1", 256, "0|0.54296875|1.0|0.58203125"),
                ("MainMenuXPBarTexture2", 512, "0|0.29296875|1.0|0.33203125"),
                ("MainMenuXPBarTexture3", 768, "0|0.04296875|1.0|0.08203125"),
            ];
            foreach (var row in xp)
                CollectUiParity(row.Name, "Texture", barMin + new Vector2(row.X, 0) * scale,
                    new Vector2(256, 10) * scale, parent: "MainMenuExpBar", point: "BOTTOM",
                    offsetX: (row.X - 384).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    offsetY: "3", texture: @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf",
                    layer: "OVERLAY", strata: "", texCoords: row.Tex);
            (string Name, float X, string Tex)[] art =
            [
                ("MainMenuBarTexture0", 0, "0|0.83203125|1.0|1.0"),
                ("MainMenuBarTexture1", 256, "0|0.58203125|1.0|0.75"),
                ("MainMenuBarTexture2", 512, "0|0.33203125|1.0|0.5"),
                ("MainMenuBarTexture3", 768, "0|0.08203125|1.0|0.25"),
            ];
            foreach (var row in art)
                CollectUiParity(row.Name, "Texture", barMin + new Vector2(row.X, 10) * scale,
                    new Vector2(256, 43) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM",
                    offsetX: (row.X - 384).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf",
                    layer: "ARTWORK", strata: "", texCoords: row.Tex);
            CollectUiParity("MainMenuBarLeftEndCap", "Texture", barMin + new Vector2(-96, -75) * scale,
                new Vector2(128) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM", offsetX: "-544",
                offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf",
                layer: "OVERLAY", strata: "");
            CollectUiParity("MainMenuBarRightEndCap", "Texture", barMin + new Vector2(992, -75) * scale,
                new Vector2(128) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM", offsetX: "544",
                offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf",
                layer: "OVERLAY", strata: "", texCoords: "1.0|0.0|0.0|1.0");
            CollectUiParity("MainMenuBarPerformanceBarFrame", "Frame", barMin + new Vector2(781, -1) * scale,
                new Vector2(16, 64) * scale, parent: "MainMenuBar", point: "BOTTOMRIGHT", offsetX: "-227",
                offsetY: "-10", strata: "LOW");
            CollectUiParity("MainMenuBarPerformanceBar", "Texture", barMin + new Vector2(777, -1) * scale,
                new Vector2(20, 66) * scale, parent: "MainMenuBarPerformanceBarFrame", point: "TOPRIGHT",
                texture: @"Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar", layer: "BACKGROUND", strata: "LOW");
        }

        // FrameXML child order: XP and the LOW-strata latency meter sit beneath the dwarf art.
        if (_uiParityArmed && _uiParityPanel == "reputation-bar") DrawReputationWatchBar(bg, barMin, scale);
        else DrawExpBar(bg, barMin, scale);
        DrawPerformanceMeter(bg, barMin, scale, display);
        DrawMainMenuBarArt(bg, barMin, scale);

        // The host used to be 86 tall, which reached 29 of the 36 logical pixels into the
        // MultiBarBottomLeft/Right button row above it. Both this window and the ##MultiBar*-hit-*
        // slot hosts are NoBringToFrontOnFocus, and ImGui push_fronts such a window to the BOTTOM
        // of the display order at creation - so the later-created slot hosts sat UNDERNEATH this
        // one, FindHoveredWindow returned ##main-action-bar for every pixel at or below
        // display.Y - 86*scale, and only a 7px strip along the top of each multibar button still
        // took a click. Nothing here is painted or submitted above display.Y - 54*scale (the
        // quickslot ring, the highest of them), so the clamp costs no pixel and no hit rect.
        Vector2 inputMin = new(barMin.X, display.Y - MainActionBarHostHeight * scale);
        ImGui.SetNextWindowPos(inputMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(544f, MainActionBarHostHeight) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##main-action-bar", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        // Ground-targeting cursor hint. Drawn here (inside the ImGui frame) rather than in
        // UpdateTargeting, which runs pre-NewFrame where draw-list access is an access
        // violation in native ImGui.
        if (_groundCastSpell != 0 && !_window.MouseCaptured)
            ImGui.GetForegroundDrawList().AddText(
                ImGui.GetIO().MousePos + new Vector2(18f, 14f) * scale, 0xFF00E060,
                "Select target area");
        if (_pressedActionSlot >= 0 &&
            ActionBarLockLaw.DragGestureAllowed(Settings.Controls.LockActionBars) &&
            ImGui.IsMouseDown(_actionPressMouseButton) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _actionPressPosition) > 6f * scale)
        {
            // ActionButton_OnDragStart always calls PickupAction, for either mouse button and
            // independently of Shift. The source is cleared immediately; a displaced target
            // remains on the cursor after PlaceAction instead of being swapped back to source.
            PickupActionToCursor(_pressedActionSlot);
            _pressedActionSlot = -1;
        }

        // Reference: an empty slot swaps to the UI-Quickslot grid ring only while a cursor
        // payload is held (BENILLA_ACTIONBAR_GRID_SHOWN); otherwise it keeps UI-Quickslot2.
        bool gridShown = HasCarriedItem || HasActionBarCursor;
        // Attack/auto-repeat flash is a plain 0.4 s show/hide toggle (ATTACK_BUTTON_FLASH_TIME).
        bool flashPhase = now % 0.8 < 0.4;
        WorldEntity? player = _entities.TryGet(ControlledGuid, out WorldEntity self) ? self : null;

        for (int i = 0; i < 12; i++)
        {
            int wireSlot = ActionWireSlot(i);
            Vector2 buttonMin = new(barMin.X + (8f + 42f * i) * scale, display.Y - 40f * scale);
            Vector2 buttonMax = buttonMin + new Vector2(36f, 36f) * scale;
            CollectGameplayLayout($"action-slot-{i + 1}", 8f + 42f * i, 728f, 36f, 36f,
                buttonMin, buttonMax - buttonMin);
            ActionSlot? slot = _actions[wireSlot];

            if (i == 0 && _uiParityArmed && _uiParityPanel == "action-button")
            {
                BeginUiParityFrame(buttonMin, scale);
                CollectUiParity("ActionButton1", "CheckButton", buttonMin, new Vector2(36) * scale,
                    parent: "", point: "BOTTOMLEFT", offsetX: "8", offsetY: "4",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "");
                CollectUiParity("ActionButton1Icon", "Texture", buttonMin, new Vector2(36) * scale,
                    parent: "ActionButton1", layer: "BACKGROUND", strata: "");
                CollectUiParity("ActionButton1HotKey", "FontString", buttonMin + new Vector2(-2, 2) * scale,
                    new Vector2(36, 10) * scale, parent: "ActionButton1", point: "TOPLEFT", offsetX: "-2",
                    offsetY: "-2", font: "NumberFontNormalSmallGray", fontPath: @"Fonts\ARIALN.TTF",
                    fontSize: "12", color: "#999999FF", layer: "ARTWORK", strata: "");
                CollectUiParity("ActionButton1NormalTexture", "NormalTexture",
                    buttonMin + new Vector2(-15, -14) * scale, new Vector2(66) * scale,
                    parent: "ActionButton1", point: "CENTER", offsetX: "0", offsetY: "-1",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "");
            }

            ImGui.SetCursorScreenPos(buttonMin);
            bool clicked = ImGui.InvisibleButton($"##action-{i}", buttonMax - buttonMin);
            // AllowWhenBlockedByActiveItem: during a drag the source button (spellbook,
            // macro pane, another slot) is ImGui's active item, which suppresses plain
            // IsItemHovered() on every other item — hoveredSlot stayed -1 for the whole
            // drag and the drop landed nowhere. This flag is what makes drop targets work.
            bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            bool activated = ImGui.IsItemActivated();
            bool pushed = ImGui.IsItemActive() || BindingDown(ActionBinding(i));
            if (hovered) _hoveredActionSlot = wireSlot;

            if (slot is { } action)
            {
                string iconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
                string title = $"Action {action.ActionId}";
                SpellInfo? spellInfo = null;
                ItemTemplate? itemInfo = null;
                if (action.Kind == ActionSlot.Spell && _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true)
                {
                    spellInfo = spell;
                    iconPath = ResolveSpellActionIcon(spell, player);
                    title = spell.Rank.Length > 0 ? $"{spell.Name} ({spell.Rank})" : spell.Name;
                }
                else if (action.Kind == ActionSlot.Item && _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null)
                {
                    itemInfo = item;
                    iconPath = item.IconPath;
                    title = item.Name;
                }
                else if (action.Kind == ActionSlot.Macro)
                {
                    iconPath = MacroIcon(action.ActionId);
                    title = MacroName(action.ActionId);
                }

                // ── the reference three-way usability verdict (ActionButton_UpdateUsable) ──
                // usable: icon+ring white; not enough power: icon+ring (0.5,0.5,1);
                // otherwise unusable: icon (0.4,0.4,0.4), ring reset to white.
                ActionButtonVerdict verdict = ComputeButtonVerdict(
                    wireSlot, action, spellInfo, player, pushed, hovered, gridShown);
                CollectGameplayAction(verdict);
                EmitActionButtonVerdict(verdict);
                uint iconTint = verdict.Usability switch
                {
                    ButtonUsability.NotEnoughPower => 0xffff8080u,
                    ButtonUsability.Usable => 0xffffffffu,
                    _ => 0xff666666u,
                };
                uint icon = PainterlyArt(iconPath);
                if (icon != 0) dl.AddImage((nint)icon, buttonMin, buttonMax, Vector2.Zero, Vector2.One, iconTint);

                if (verdict.Flashing && flashPhase)
                {
                    uint flash = _gameplayArt.Handle(@"Interface\Buttons\UI-QuickslotRed");
                    if (flash != 0) dl.AddImage((nint)flash, buttonMin, buttonMax);
                }

                CooldownDisplay cooldown = default;
                bool hasCooldown = verdict.IsItem && itemInfo is { UseSpellId: not 0 }
                    ? _actions.TryCooldownDisplay(itemInfo.UseSpellId, itemInfo.Entry,
                        itemInfo.UseSpellCategory, now, out cooldown)
                    : spellInfo is { } cooldownSpell &&
                      _actions.TryCooldownDisplay(verdict.ActionId, 0, cooldownSpell, now,
                          out cooldown);
                if (hasCooldown)
                {
                    if (cooldown.SweepFraction is { } sweep)
                        DrawCooldownSwipe(dl, buttonMin, buttonMax, sweep);
                    else if (cooldown.FlashProgress is { } flash)
                        DrawCooldownFlash(dl, buttonMin, buttonMax, flash);
                }

                // NormalTexture is below Pushed/Highlight/Checked in FrameXML. Drawing the ring
                // after the hover layer partially masks the bright outline and reads as dimming.
                DrawSlotRing(dl, buttonMin, buttonMax, @"Interface\Buttons\UI-Quickslot2", scale,
                    verdict.Usability == ButtonUsability.NotEnoughPower ? 0xffff8080u : 0xffffffffu);

                if (activated && !HasCarriedItem && _actionCursor is null &&
                    _draggingSpellId == 0 && _draggingMacroId == 0 &&
                    !_draggingPetAction.HasValue)
                {
                    _pressedActionSlot = wireSlot;
                    _actionPressPosition = ImGui.GetIO().MousePos;
                    _actionPressMouseButton = ImGuiMouseButton.Left;
                }
                if (clicked && !ConsumeActionButtonClick(wireSlot, ShiftHeld()))
                    UseAction(wireSlot);

                // PUSHED replaces the normal state while the mouse or the bound key is down.
                if (verdict.Pushed)
                {
                    uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                    if (depress != 0) dl.AddImage((nint)depress, buttonMin, buttonMax);
                }
                if (verdict.Hover)
                {
                    uint highlight = _gameplayArt.BrightHighlightHandle(@"Interface\Buttons\ButtonHilight-Square");
                    if (highlight != 0) dl.AddImage((nint)highlight, buttonMin, buttonMax);
                    if (spellInfo is not null)
                    {
                        // ActionButton_OnEnter uses GameTooltip_SetDefaultAnchor + SetAction.
                        // The shared spell renderer supplies SetAction's full spell data and
                        // same-line rank instead of the old generic "Action N" ImGui popup.
                        GameTooltipOwnerKey tooltipOwner =
                            new("action-main", (ulong)(i + 1));
                        _hoveredActionSpellTooltip = PrepareSharedSpellTooltip(
                            tooltipOwner, action.ActionId, scale,
                            SpellTooltipPlacement.DefaultBottomRight);
                    }
                    else
                    {
                        GameTooltipOwnerKey tooltipOwner =
                            new("action-main", (ulong)(i + 1));
                        string tooltipTitle = title;
                        string tooltipAction = $"Action {wireSlot + 1}";
                        OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(tooltipTitle);
                            ImGui.TextDisabled(tooltipAction);
                            ImGui.EndTooltip();
                        });
                    }
                }
                if (verdict.Checked)
                {
                    uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                    if (check != 0) dl.AddImage((nint)check, buttonMin, buttonMax);
                }
                if (verdict.EquippedBorder)
                {
                    uint border = _gameplayArt.AdditiveHandle(@"Interface\Buttons\UI-ActionButton-Border");
                    if (border != 0)
                    {
                        Vector2 center = (buttonMin + buttonMax) * 0.5f;
                        Vector2 half = new(31f * scale); // 62x62, centered
                        dl.AddImage((nint)border, center - half, center + half, Vector2.Zero,
                            Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 1f, 0f, 0.35f)));
                    }
                }

                // Hotkey: red (1.0,0.1,0.1) while the selection is out of range, grey otherwise.
                uint hotkeyColor = verdict.Range == ButtonRange.OutOfRange
                    ? 0xff1a1affu : 0xff999999u;
                DrawActionText(dl, buttonMin,
                    FriendlyHotkey(BoundKeys(ActionBinding(i)).Primary), scale, hotkeyColor);

                if (verdict.IsItem && verdict.StackCount > 0)
                    DrawActionCount(dl, buttonMax, verdict.StackCount, scale);
            }
            else
            {
                ActionButtonVerdict verdict = ComputeButtonVerdict(
                    wireSlot, null, null, player, pushed, hovered, gridShown);
                CollectGameplayAction(verdict);
                EmitActionButtonVerdict(verdict);
                if (clicked) ConsumeActionButtonClick(wireSlot, ShiftHeld());
                DrawSlotRing(dl, buttonMin, buttonMax,
                    verdict.CarriedGrid
                        ? @"Interface\Buttons\UI-Quickslot"
                        : @"Interface\Buttons\UI-Quickslot2", scale, emptySlot: true);
                DrawActionText(dl, buttonMin,
                    FriendlyHotkey(BoundKeys(ActionBinding(i)).Primary), scale, 0xff999999);
            }
        }

        // ActionBar.xml anchors are Y-up. ImGui is Y-down, so XML y=-22/-42 become +22/+42.
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 22f), "Up", 0))
            ChangeActionPage(1);
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 42f), "Down", 1))
            ChangeActionPage(-1);
        ImGui.End();

        DrawMicroMenu(barMin, scale);

        if (_uiParityArmed && _uiParityPanel is "action-bar" or "action-button")
            MarkUiParityFrameComplete();

        DrawActionCursorPayload(player, scale);
    }

    private void DrawMultiActionBars()
    {
        if ((_net is not { IsInWorld: true } && !HudPreview) || _gameplayArt is null) return;
        if (_freeView) return;   // commander console: no body chrome
        Vector2 display = ImGui.GetIO().DisplaySize;
        float scale = GameplayUiScale();
        Vector2 barMin = GameplayBarMin(display, scale);
        bool proof = _uiParityArmed && _uiParityPanel == "multi-action-bar";
        bool gridShown = HasCarriedItem || HasActionBarCursor;
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        bool flashPhase = now % 0.8 < 0.4;
        WorldEntity? player = _entities.TryGet(ControlledGuid, out WorldEntity self) ? self : null;
        (string Name, int FirstSlot, bool Vertical, Vector2 Origin, BottomMultiActionBar? BindingBar)[] bars =
        [
            ("MultiBarBottomLeft", MultiActionBarUiLaw.BottomLeftBase, false,
                new Vector2(barMin.X + 8 * scale, display.Y - MultiActionBarUiLaw.BottomRowRise * scale), BottomMultiActionBar.Left),
            ("MultiBarBottomRight", MultiActionBarUiLaw.BottomRightBase, false,
                new Vector2(barMin.X + 518 * scale, display.Y - MultiActionBarUiLaw.BottomRowRise * scale), BottomMultiActionBar.Right),
            // These two were already implemented by MSUI. They are outside Benilla's requested
            // bottom-bar scope, so preserve them instead of deleting a present MSUI feature.
            ("MultiBarRight", 24, true,
                new Vector2(display.X - 45 * scale, display.Y - 598 * scale), null),
            ("MultiBarLeft", 36, true,
                new Vector2(display.X - 88 * scale, display.Y - 598 * scale), null),
        ];
        foreach (var bar in bars)
        {
            bool populated = Enumerable.Range(bar.FirstSlot, MultiActionBarUiLaw.ButtonsPerBar)
                .Any(slot => _actions[slot] is not null);
            bool bottomReferenceBar = bar.BindingBar is not null;
            if (!bottomReferenceBar && !populated) continue;
            DrawMultiActionBar(bar.Name, bar.FirstSlot, bar.Vertical, bar.Origin, scale, proof,
                gridShown, now, flashPhase, player, bar.BindingBar);
        }

        if (_hoveredActionSpellTooltip is { } prepared)
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner,
                () => DrawSpellTooltip(prepared.Snapshot));
        FinishActionDrag();
    }

    private void DrawMultiActionBar(string name, int firstSlot, bool vertical,
        Vector2 origin, float scale, bool proof, bool gridShown, double now, bool flashPhase,
        WorldEntity? player, BottomMultiActionBar? bindingBar)
    {
        if (_gameplayArt is null) return;
        Vector2 logicalSize = vertical ? new Vector2(38, 500) : new Vector2(500, 38);
        bool[] clickedSlots = new bool[MultiActionBarUiLaw.ButtonsPerBar];
        bool[] hoveredSlots = new bool[MultiActionBarUiLaw.ButtonsPerBar];
        bool[] activatedSlots = new bool[MultiActionBarUiLaw.ButtonsPerBar];
        bool[] pushedSlots = new bool[MultiActionBarUiLaw.ButtonsPerBar];
        ImGuiWindowFlags inputFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        // The authored 500x38 parent is not mouse-enabled. Give only live buttons a 36x36 input
        // host so hidden empty slots and the six-pixel gaps genuinely pass through to the world.
        // Zeroing the three window styles is what makes that host actually BE 36x36: the default
        // WindowPadding <8,8> insets a window's InnerClipRect (which clips the hover test) by 4px
        // per side, and WindowMinSize <32,32> floors the host below UI scale 0.89. The stance bar
        // (GameLoop.StanceBar.cs) and the pet bar (GameLoop.Pet.cs) already do this; this loop was
        // the one that did not. The pushes must not straddle the ##{name} art window below.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        for (int i = 0; i < MultiActionBarUiLaw.ButtonsPerBar; i++)
        {
            int slot = firstSlot + i;
            if (!MultiActionBarUiLaw.InteractiveSlot(_actions[slot] is not null, gridShown))
                continue;
            Vector2 min = origin + (vertical
                ? new Vector2(2, i * MultiActionBarUiLaw.ButtonStep)
                : new Vector2(i * MultiActionBarUiLaw.ButtonStep, 2)) * scale;
            Vector2 size = new Vector2(MultiActionBarUiLaw.ButtonSize) * scale;
            ImGui.SetNextWindowPos(min, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0);
            if (ImGui.Begin($"##{name}-hit-{i}", inputFlags))
            {
                ImGui.SetCursorScreenPos(min);
                clickedSlots[i] = ImGui.InvisibleButton($"##hit-{i}", size,
                    ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                hoveredSlots[i] = ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                activatedSlots[i] = ImGui.IsItemActivated();
                pushedSlots[i] = ImGui.IsItemActive();
            }
            ImGui.End();
        }
        ImGui.PopStyleVar(3);

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMouseInputs;
        if (!ImGui.Begin($"##{name}", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();

        bool proofBar = proof && bindingBar is not null;
        if (proofBar && name == "MultiBarBottomLeft")
        {
            BeginUiParityFrame(origin, scale);
        }
        if (proofBar)
            CollectUiParityDraw(name, "Frame", origin, logicalSize * scale, "",
                name == "MultiBarBottomLeft"
                    ? new("", 0, "IMGUI_HOST", "BOTTOMLEFT", "ActionButton1", "TOPLEFT", 0,
                        MultiActionBarUiLaw.BottomLeftRise, Visible: true, Enabled: false,
                        InteractionState: "parent-not-mouse-enabled", Strata: "HIGH")
                    : new("", 0, "IMGUI_HOST", "LEFT", "MultiBarBottomLeft", "RIGHT",
                        MultiActionBarUiLaw.BottomBarGap, 0, Visible: true, Enabled: false,
                        InteractionState: "parent-not-mouse-enabled", Strata: "HIGH"));

        for (int i = 0; i < MultiActionBarUiLaw.ButtonsPerBar; i++)
        {
            Vector2 buttonMin = origin + (vertical
                ? new Vector2(2, i * MultiActionBarUiLaw.ButtonStep)
                : new Vector2(i * MultiActionBarUiLaw.ButtonStep, 2)) * scale;
            Vector2 buttonMax = buttonMin + new Vector2(MultiActionBarUiLaw.ButtonSize) * scale;
            int slotNumber = firstSlot + i;
            string button = name + "Button" + (i + 1);
            ActionSlot? slotAction = _actions[slotNumber];

            bool interactive = MultiActionBarUiLaw.InteractiveSlot(slotAction is not null, gridShown);
            bool clicked = clickedSlots[i];
            bool hovered = hoveredSlots[i];
            bool activated = activatedSlots[i];
            bool pushed = pushedSlots[i] || bindingBar is { } bindBar &&
                _multiActionKeyArmed[(bindBar == BottomMultiActionBar.Left ? 0 : 1) *
                    MultiActionBarUiLaw.ButtonsPerBar + i];
            if (hovered) _hoveredActionSlot = slotNumber;
            if (proofBar)
                CollectUiParityDraw(button, "CheckButton", buttonMin,
                    new Vector2(MultiActionBarUiLaw.ButtonSize) * scale, name,
                    new("", 0, "IMGUI_HIT_TARGET", i == 0 ? "BOTTOMLEFT" : "LEFT",
                        i == 0 ? name : name + "Button" + i,
                        i == 0 ? "BOTTOMLEFT" : "RIGHT", i == 0 ? 0 : 6, 0,
                        Visible: interactive, Enabled: interactive,
                        InteractionState: !interactive ? "hidden-empty" : pushed ? "pushed" :
                            hovered ? "hovered" : "normal",
                        HitMin: interactive ? buttonMin : null,
                        HitMax: interactive ? buttonMax : null, Strata: "HIGH"));

            if (slotAction is { } action)
            {
                string iconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
                SpellInfo? spellInfo = null;
                ItemTemplate? itemInfo = null;
                string fallbackTitle = $"Action {action.ActionId}";
                if (action.Kind == ActionSlot.Spell &&
                    _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true)
                {
                    spellInfo = spell;
                    iconPath = ResolveSpellActionIcon(spell, player);
                    fallbackTitle = spell.Rank.Length > 0
                        ? $"{spell.Name} ({spell.Rank})" : spell.Name;
                }
                else if (action.Kind == ActionSlot.Item &&
                         _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null)
                {
                    itemInfo = item;
                    iconPath = item.IconPath;
                    fallbackTitle = item.Name;
                }
                else if (action.Kind == ActionSlot.Macro)
                {
                    iconPath = MacroIcon(action.ActionId);
                    fallbackTitle = MacroName(action.ActionId);
                }

                ActionButtonVerdict verdict = ComputeButtonVerdict(
                    slotNumber, action, spellInfo, player, pushed, hovered, gridShown);
                CollectGameplayAction(verdict);
                EmitActionButtonVerdict(verdict);
                uint iconTint = verdict.Usability switch
                {
                    ButtonUsability.NotEnoughPower => 0xffff8080u,
                    ButtonUsability.Usable => 0xffffffffu,
                    _ => 0xff666666u,
                };
                uint icon = PainterlyArt(iconPath);
                if (icon != 0) dl.AddImage((nint)icon, buttonMin, buttonMax,
                    Vector2.Zero, Vector2.One, iconTint);
                if (proofBar)
                    CollectUiParityDraw(button + "Icon", "Texture", buttonMin,
                        buttonMax - buttonMin, button,
                        new(iconPath, iconTint, "BACKGROUND", "CENTER", button, "CENTER", 0, 0,
                            Visible: icon != 0, BlendMode: "BLEND", Strata: "HIGH"));
                bool flashVisible = verdict.Flashing && flashPhase;
                if (flashVisible)
                {
                    uint flash = _gameplayArt.Handle(@"Interface\Buttons\UI-QuickslotRed");
                    if (flash != 0) dl.AddImage((nint)flash, buttonMin, buttonMax);
                    if (proofBar)
                        CollectUiParityDraw(button + "Flash", "Texture", buttonMin,
                            buttonMax - buttonMin, button,
                            new(@"Interface\Buttons\UI-QuickslotRed", 0xffffffff, "ARTWORK",
                                "CENTER", button, "CENTER", 0, 0, Visible: flash != 0,
                                BlendMode: "BLEND", Strata: "HIGH"));
                }
                else if (proofBar)
                    ClassifyUiParity(button + "Flash", "Texture", button, "NOT-DRAWN",
                        "action-not-flashing-or-off-phase");
                bool cooldownVisible = false;
                CooldownDisplay cooldown = default;
                bool hasCooldown = verdict.IsItem && itemInfo is { UseSpellId: not 0 }
                    ? _actions.TryCooldownDisplay(itemInfo.UseSpellId, itemInfo.Entry,
                        itemInfo.UseSpellCategory, now, out cooldown)
                    : spellInfo is { } cooldownSpell &&
                      _actions.TryCooldownDisplay(verdict.ActionId, 0, cooldownSpell, now,
                          out cooldown);
                if (hasCooldown)
                {
                    if (cooldown.SweepFraction is { } sweep)
                    {
                        cooldownVisible = true;
                        DrawCooldownSwipe(dl, buttonMin, buttonMax, sweep);
                        if (proofBar)
                            CollectUiParityDraw(button + "Cooldown", "Cooldown", buttonMin,
                                buttonMax - buttonMin, button,
                                new("", 0x99000000, "ARTWORK", "CENTER", button, "CENTER", 0, -1,
                                    ClipMask: $"RADIAL_SWEEP:{sweep:R}", BlendMode: "BLEND",
                                    InteractionState: "cooldown-sweep", Strata: "HIGH"));
                    }
                    else if (cooldown.FlashProgress is { } flash)
                    {
                        cooldownVisible = true;
                        DrawCooldownFlash(dl, buttonMin, buttonMax, flash);
                        if (proofBar)
                            CollectUiParityDraw(button + "Cooldown", "Cooldown", buttonMin,
                                buttonMax - buttonMin, button,
                                new("", 0xffffffff, "ARTWORK", "CENTER", button, "CENTER", 0, -1,
                                    ClipMask: $"COOLDOWN_FINISH_FLASH:{flash:R}", BlendMode: "ADD",
                                    InteractionState: "cooldown-finish-flash", Strata: "HIGH"));
                    }
                }
                if (proofBar && !cooldownVisible)
                    ClassifyUiParity(button + "Cooldown", "Cooldown", button, "NOT-DRAWN",
                        "no-active-item-or-spell-cooldown");
                uint ringTint = verdict.Usability == ButtonUsability.NotEnoughPower
                    ? 0xffff8080u : 0xffffffffu;
                bool normalTextureVisible = DrawSlotRing(dl, buttonMin, buttonMax,
                    @"Interface\Buttons\UI-Quickslot2", scale, ringTint);
                if (proofBar)
                {
                    Vector2 ringCenter = (buttonMin + buttonMax) * .5f + new Vector2(0, scale);
                    Vector2 ringHalf = new(33f * scale);
                        CollectUiParityDraw(button + "NormalTexture", "NormalTexture",
                            ringCenter - ringHalf, ringHalf * 2, button,
                            new(@"Interface\Buttons\UI-Quickslot2", ringTint, "ARTWORK", "CENTER",
                            button, "CENTER", 0, -1, BlendMode: "BLEND",
                            Visible: normalTextureVisible, Strata: "HIGH"));
                }
                if (activated && !HasCarriedItem && _actionCursor is null &&
                    _draggingSpellId == 0 && _draggingMacroId == 0 &&
                    !_draggingPetAction.HasValue)
                {
                    _pressedActionSlot = slotNumber;
                    _actionPressPosition = ImGui.GetIO().MousePos;
                    _actionPressMouseButton = ImGui.IsMouseDown(ImGuiMouseButton.Right)
                        ? ImGuiMouseButton.Right : ImGuiMouseButton.Left;
                }
                if (clicked && !ConsumeActionButtonClick(slotNumber, ShiftHeld()))
                    UseAction(slotNumber);
                if (verdict.Pushed)
                {
                    uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                    if (depress != 0) dl.AddImage((nint)depress, buttonMin, buttonMax);
                    if (proofBar)
                        CollectUiParityDraw(button + "PushedTexture", "PushedTexture", buttonMin,
                            buttonMax - buttonMin, button,
                            new(@"Interface\Buttons\UI-Quickslot-Depress", 0xffffffff, "ARTWORK",
                                "CENTER", button, "CENTER", 0, 0, Visible: depress != 0,
                                BlendMode: "BLEND", Strata: "HIGH"));
                }
                else if (proofBar)
                    ClassifyUiParity(button + "PushedTexture", "PushedTexture", button,
                        "NOT-DRAWN", "button-not-pushed");
                if (verdict.Hover)
                {
                    uint highlight = _gameplayArt.BrightHighlightHandle(@"Interface\Buttons\ButtonHilight-Square");
                    if (highlight != 0) dl.AddImage((nint)highlight, buttonMin, buttonMax);
                    if (proofBar)
                        CollectUiParityDraw(button + "HighlightTexture", "HighlightTexture",
                            buttonMin, buttonMax - buttonMin, button,
                            new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                                "CENTER", button, "CENTER", 0, 0, Visible: highlight != 0,
                                BlendMode: "ADD", Strata: "HIGH"));
                    if (spellInfo is not null)
                    {
                        GameTooltipOwnerKey tooltipOwner = ActionBarGameTooltipOwner(name, i);
                        _hoveredActionSpellTooltip = PrepareSharedSpellTooltip(
                            tooltipOwner, action.ActionId, scale,
                            SpellTooltipPlacement.DefaultBottomRight);
                    }
                    else if (itemInfo is not null)
                    {
                        GameTooltipOwnerKey tooltipOwner = ActionBarGameTooltipOwner(name, i);
                        ItemTooltipBodySnapshot tooltipBody =
                            PrepareItemTooltipBodySnapshot(itemInfo, 1);
                        OfferPreparedItemTooltip(tooltipOwner, tooltipBody);
                    }
                    else
                    {
                        GameTooltipOwnerKey tooltipOwner = ActionBarGameTooltipOwner(name, i);
                        string tooltipTitle = fallbackTitle;
                        string tooltipAction = $"Action {slotNumber + 1}";
                        OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(tooltipTitle);
                            ImGui.TextDisabled(tooltipAction);
                            ImGui.EndTooltip();
                        });
                    }
                }
                else if (proofBar)
                    ClassifyUiParity(button + "HighlightTexture", "HighlightTexture", button,
                        "NOT-DRAWN", "button-not-hovered");
                if (verdict.Checked)
                {
                    uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                    if (check != 0) dl.AddImage((nint)check, buttonMin, buttonMax);
                    if (proofBar)
                        CollectUiParityDraw(button + "CheckedTexture", "CheckedTexture", buttonMin,
                            buttonMax - buttonMin, button,
                            new(@"Interface\Buttons\CheckButtonHilight", 0xffffffff, "ARTWORK",
                                "CENTER", button, "CENTER", 0, 0, Visible: check != 0,
                                BlendMode: "ADD", Strata: "HIGH"));
                }
                else if (proofBar)
                    ClassifyUiParity(button + "CheckedTexture", "CheckedTexture", button,
                        "NOT-DRAWN", "action-not-checked");
                if (verdict.EquippedBorder)
                {
                    uint border = _gameplayArt.AdditiveHandle(@"Interface\Buttons\UI-ActionButton-Border");
                    Vector2 center = (buttonMin + buttonMax) * .5f;
                    Vector2 half = new(31f * scale);
                    if (border != 0)
                    {
                        dl.AddImage((nint)border, center - half, center + half, Vector2.Zero,
                            Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, .35f)));
                    }
                    if (proofBar)
                        CollectUiParityDraw(button + "Border", "Texture", center - half,
                            half * 2, button,
                            new(@"Interface\Buttons\UI-ActionButton-Border",
                                ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, .35f)),
                                "OVERLAY", "CENTER", button, "CENTER", 0, 0,
                                Visible: border != 0, BlendMode: "ADD", Strata: "HIGH"));
                }
                else if (proofBar)
                    ClassifyUiParity(button + "Border", "Texture", button, "NOT-DRAWN",
                        "item-action-not-equipped");
                GameBinding? binding = bindingBar is { } hotkeyBar
                    ? MultiActionBinding(hotkeyBar, i) : null;
                string hotkey = binding is { } command
                    ? FriendlyHotkey(BoundKeys(command).Primary) : "";
                if (hotkey.Length == 0 && verdict.Range == ButtonRange.OutOfRange) hotkey = "·";
                uint hotkeyColor = verdict.Range == ButtonRange.OutOfRange
                    ? 0xff1a1affu : 0xff999999u;
                DrawActionText(dl, buttonMin, hotkey, scale, hotkeyColor);
                if (proofBar && hotkey.Length > 0)
                {
                    Vector2 extent = new(
                        GameText.MeasureWidth("NumberFontNormalSmallGray", hotkey, scale),
                        GameText.EmPixels("NumberFontNormalSmallGray", scale));
                    float textTop = GameText.BoxCenteredTop("NumberFontNormalSmallGray",
                        buttonMin.Y + 2f * scale, 10f, scale);
                    Vector2 textMin = new(buttonMin.X + 34f * scale - extent.X, textTop);
                    CollectUiParityDraw(button + "HotKey", "FontString", textMin, extent, button,
                        new("", hotkeyColor, "ARTWORK", "TOPRIGHT", button, "TOPRIGHT", -2, -2,
                            @"Fonts\ARIALN.TTF", 12, Visible: true,
                            InteractionState: "number-font-normal-small-gray-thick-outline",
                            Strata: "HIGH"));
                }
                else if (proofBar)
                    ClassifyUiParity(button + "HotKey", "FontString", button, "NOT-DRAWN",
                        "unbound-and-not-out-of-range");
                bool showCount = itemInfo is not null && MultiActionBarUiLaw.ShowItemCount(
                    itemInfo.InventoryType, itemInfo.HasNegativeOnUseCharges);
                if (showCount)
                {
                    DrawActionCount(dl, buttonMax, verdict.StackCount, scale);
                    if (proofBar)
                    {
                        string countText = verdict.StackCount.ToString();
                        Vector2 extent = new(
                            GameText.MeasureWidth("NumberFontNormal", countText, scale),
                            GameText.EmPixels("NumberFontNormal", scale));
                        Vector2 textMin = new(buttonMax.X - 2f * scale - extent.X,
                            buttonMax.Y - 2f * scale - extent.Y);
                        CollectUiParityDraw(button + "Count", "FontString", textMin, extent, button,
                            new("", 0xffffffff, "OVERLAY", "BOTTOMRIGHT", button, "BOTTOMRIGHT",
                                -2, 2, @"Fonts\ARIALN.TTF", 14,
                                InteractionState: "number-font-normal-outline", Strata: "HIGH"));
                    }
                }
                else if (proofBar)
                    ClassifyUiParity(button + "Count", "FontString", button, "NOT-DRAWN",
                        "item-action-is-not-consumable");
            }
            else
            {
                if (clicked) ConsumeActionButtonClick(slotNumber, ShiftHeld());
                if (MultiActionBarUiLaw.ShowEmptyWell(gridShown))
                {
                    bool normalTextureVisible = DrawSlotRing(dl, buttonMin, buttonMax,
                        @"Interface\Buttons\UI-Quickslot", scale, emptySlot: true);
                    if (proofBar)
                    {
                        Vector2 ringCenter = (buttonMin + buttonMax) * .5f + new Vector2(0, scale);
                        Vector2 ringHalf = new(33f * scale);
                        CollectUiParityDraw(button + "NormalTexture", "NormalTexture",
                            ringCenter - ringHalf, ringHalf * 2, button,
                            new(@"Interface\Buttons\UI-Quickslot", 0xffffffff, "ARTWORK", "CENTER",
                                button, "CENTER", 0, -1, BlendMode: "BLEND",
                                Visible: normalTextureVisible, Strata: "HIGH"));
                    }
                }
                else if (proofBar)
                    ClassifyUiParity(button + "NormalTexture", "NormalTexture", button,
                        "NOT-DRAWN", "empty-button-hidden-with-grid-off");
                if (proofBar)
                {
                    ClassifyUiParity(button + "Icon", "Texture", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "Flash", "Texture", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "HotKey", "FontString", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "Count", "FontString", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "Border", "Texture", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "Cooldown", "Cooldown", button, "NOT-DRAWN",
                        "empty-action");
                    ClassifyUiParity(button + "PushedTexture", "PushedTexture", button,
                        "NOT-DRAWN", "empty-action");
                    ClassifyUiParity(button + "HighlightTexture", "HighlightTexture", button,
                        "NOT-DRAWN", "empty-action");
                    ClassifyUiParity(button + "CheckedTexture", "CheckedTexture", button,
                        "NOT-DRAWN", "empty-action");
                }
            }
        }
        dl.PopClipRect();
        if (proofBar && name == "MultiBarBottomRight") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private bool ShiftHeld() => InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
    private bool CtrlHeld() => InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
    private bool AltHeld() => InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight);

    private bool HasActionBarCursor => _actionCursor is not null || _draggingSpellId != 0 ||
        _draggingMacroId != 0 || _draggingPetAction.HasValue;

    private void ClearActionBarCursorOnEscape()
    {
        _actionCursor = null;
        _draggingSpellId = 0;
        _draggingMacroId = 0;
        _pressedSpellId = 0;
        _pressedMacroId = 0;
        _pressedActionSlot = -1;
        ClearPetActionCursor();
    }

    /// <summary>
    /// A cursor payload consumes the button release even when its acceptance filter refuses it;
    /// otherwise the action underneath would incorrectly fire. Spell/macro/action cursors are
    /// committed by FinishActionDrag after every bar has had a chance to claim the hover.
    /// </summary>
    private bool ConsumeActionButtonClick(int slot, bool shift)
    {
        if (HasCarriedItem) return PlaceCarriedItemOnAction(slot);
        if (_actionCursor is not null || _draggingSpellId != 0 || _draggingMacroId != 0 ||
            _draggingPetAction.HasValue)
            return true;
        return shift && PickupActionToCursor(slot);
    }

    private bool MouseOverActionBarDropTarget()
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mouse = ImGui.GetIO().MousePos;
        float scale = GameplayUiScale();
        Vector2 barMin = GameplayBarMin(display, scale);
        bool InRow(Vector2 origin) => MultiActionBarUiLaw.InHorizontalButton(
            mouse.X / scale, mouse.Y / scale, origin.X / scale, origin.Y / scale);
        Vector2 mainOrigin = new(barMin.X + 8 * scale, display.Y - 42 * scale);
        Vector2 leftOrigin = new(barMin.X + 8 * scale, display.Y - MultiActionBarUiLaw.BottomRowRise * scale);
        Vector2 rightOrigin = new(barMin.X + 518 * scale, display.Y - MultiActionBarUiLaw.BottomRowRise * scale);
        return InRow(mainOrigin) || InRow(leftOrigin) || InRow(rightOrigin);
    }

    private bool PickupActionToCursor(int slot)
    {
        // Free-view inspection of another unit's bars never edits.
        if (BarsReadOnly) return false;
        if (_net is null || _actionCursor is not null || _actions[slot] is not { } action)
            return false;
        MultiActionPlacement transition = MultiActionBarUiLaw.PickupAction(action.Packed);
        _actions.Set(slot, null);
        // A possessed bot's edits persist to the layered client bars: CMSG_SET_ACTION_BUTTON
        // acts on the SESSION character server-side, so the wire is only for the own bar.
        if (ControlledGuid == LocalPlayerGuid)
            _net.SetActionButton((byte)slot, transition.DestinationPacked);
        else
            SaveBotBarSlot(slot, 0);
        _actionCursor = action;
        _actionCursorChangedThisFrame = true;
        return true;
    }

    /// <summary>
    /// PlaceAction is client-authoritative. The held payload replaces the destination with one
    /// five-byte wire intent; an occupied destination hops to the cursor instead of disappearing.
    /// </summary>
    private void PlaceActionPayload(int slot, ActionSlot held)
    {
        if (_net is null) return;
        if (BarsReadOnly) return;   // free-view inspection never edits
        ActionSlot? displaced = _actions[slot];
        MultiActionPlacement transition = MultiActionBarUiLaw.PlaceAction(
            held.Packed, displaced?.Packed ?? 0);
        _actions.Set(slot, held);
        if (ControlledGuid == LocalPlayerGuid)
            _net.SetActionButton((byte)slot, transition.DestinationPacked);
        else
            SaveBotBarSlot(slot, held.Packed);   // layered bot bars, chosen layer
        _actionCursor = displaced;
        _actionCursorChangedThisFrame = true;
    }

    private void DrawActionCursorPayload(WorldEntity? player, float scale)
    {
        if (_gameplayArt is null) return;
        ActionSlot? cursor = _actionCursor;
        if (cursor is null && _draggingSpellId != 0)
            cursor = new ActionSlot(ActionSlot.Spell, _draggingSpellId);
        if (cursor is null && _draggingMacroId != 0)
            cursor = new ActionSlot(ActionSlot.Macro, _draggingMacroId);
        if (cursor is not { } action) return;

        string iconPath = action.Kind == ActionSlot.Spell &&
            _spellCatalog?.TryGet(action.ActionId, out SpellInfo info) == true
                ? ResolveSpellActionIcon(info, player)
                : action.Kind == ActionSlot.Item &&
                  _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null
                    ? item.IconPath : action.Kind == ActionSlot.Macro ? MacroIcon(action.ActionId)
                    : @"Interface\Icons\INV_Misc_QuestionMark.blp";
        uint icon = _gameplayArt.Handle(iconPath);
        if (icon == 0) return;
        Vector2 min = ImGui.GetIO().MousePos + new Vector2(10f) * scale;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, min,
            min + new Vector2(32f) * scale, Vector2.Zero, Vector2.One, 0xccffffff);
    }

    private void FinishActionDrag()
    {
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
            !ImGui.IsMouseReleased(ImGuiMouseButton.Right)) return;
        int receiveSlot = ActionBarLockLaw.ReceiveDragAllowed(Settings.Controls.LockActionBars)
            ? _hoveredActionSlot : -1;
        if (_draggingMacroId != 0)
        {
            if (receiveSlot >= 0)
            {
                var macroAction = new ActionSlot(ActionSlot.Macro, _draggingMacroId);
                PlaceActionPayload(receiveSlot, macroAction);
            }

            _draggingMacroId = 0;
            _pressedMacroId = 0;
        }
        else if (_draggingSpellId != 0)
        {
            if (receiveSlot >= 0)
            {
                if (_spellCatalog?.TryGet(_draggingSpellId, out SpellInfo spell) != true)
                {
                    // The live cursor remains held until its item query/catalog dependency is
                    // known; never guess that an unknown spell is safe to put on the bar.
                }
                else if (spell.Passive)
                {
                    ShowSpellError(_draggingSpellId, "ERR_PASSIVE_ABILITY",
                        "You can't put a passive ability in the action bar.", "LOCAL_GATE");
                }
                else
                {
                    var spellAction = new ActionSlot(ActionSlot.Spell, _draggingSpellId);
                    PlaceActionPayload(receiveSlot, spellAction);

                }
            }

            _draggingSpellId = 0;
        }
        else if (_actionCursor is { } held && !_actionCursorChangedThisFrame)
        {
            if (receiveSlot >= 0)
                PlaceActionPayload(receiveSlot, held);

            _actionCursor = null;
        }
        else if (HasCarriedItem)
        {
            // Inventory items use the carried-item cursor, not the action cursor.
            // Claim the release here so dropping onto a hotbar slot does not require
            // a second click to route through ConsumeActionButtonClick().
            if (receiveSlot >= 0)
                PlaceCarriedItemOnAction(receiveSlot);
        }

        _pressedActionSlot = -1;
    }

    private void DrawMicroMenu(Vector2 barMin, float scale)
    {
        if (_gameplayArt is null) return;
        Vector2 windowMin = barMin + new Vector2(552f, -5f) * scale;
        CollectGameplayLayout("micro-cluster", 552f, 710f, 211f, 58f,
            windowMin, new Vector2(211f, 58f) * scale);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(211f, 58f) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##micro-menu", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint playerLevel = _entities.TryGet(ControlledGuid, out WorldEntity player)
            ? player.Level : 0;
        MicroMenuButtonSpec[] buttons = [.. MicroMenuUiLaw.VisibleButtons(playerLevel)];

        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            Vector2 min = windowMin + new Vector2(MicroMenuUiLaw.ButtonX(i), 0f) * scale;
            Vector2 max = min + new Vector2(29f, 58f) * scale;
            CollectGameplayLayout($"micro-{button.Art.ToLowerInvariant()}",
                552f + 26f * i, 710f, 29f, 58f, min, max - min);
            Vector2 mouse = ImGui.GetIO().MousePos;
            bool held = ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
                mouse.X >= min.X && mouse.X <= max.X &&
                mouse.Y >= min.Y + 18f * scale && mouse.Y <= max.Y;
            bool pushed = MicroMenuButtonPushed(button.Id) || held;
            string state = pushed ? "Down" : "Up";
            string path = button.Id == MicroMenuButtonId.Character
                ? $@"Interface\Buttons\UI-MicroButtonCharacter-{state}"
                : $@"Interface\Buttons\UI-MicroButton-{button.Art}-{state}";
            uint texture = _gameplayArt.Handle(path);
            if (texture != 0) dl.AddImage((nint)texture, min, max);
            // MicroMenu.xml puts MicroButtonPortrait in OVERLAY, above the button state art.
            if (button.Id == MicroMenuButtonId.Character)
                DrawCharacterMicroPortrait(dl, min, scale, pushed);

            // The transparent top 18 pixels are authored decoration, not part of the hit rect.
            Vector2 hitMin = min + new Vector2(0f, 18f) * scale;
            Vector2 hitMax = max;
            ImGui.SetCursorScreenPos(hitMin);
            bool clicked = ImGui.InvisibleButton($"##micro-{button.Id}", hitMax - hitMin);

            // BeginPopupModal correctly blocks every underlying HUD control, but MainMenuButton's
            // authored contract is a true toggle. Remember only a press that began in its real
            // 29x40 hit rectangle and consume only the matching release; no other micro button is
            // allowed to click through the modal.
            if (button.Id == MicroMenuButtonId.MainMenu && _settingsOpen)
            {
                bool over = mouse.X >= hitMin.X && mouse.X <= hitMax.X &&
                            mouse.Y >= hitMin.Y && mouse.Y <= hitMax.Y;
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && over)
                    _mainMenuMicroPressedThroughModal = true;
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    clicked |= _mainMenuMicroPressedThroughModal && over;
                    _mainMenuMicroPressedThroughModal = false;
                }
            }
            else if (button.Id == MicroMenuButtonId.MainMenu &&
                     ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                _mainMenuMicroPressedThroughModal = false;

            if (clicked)
            {
                if (button.Id != MicroMenuButtonId.MainMenu &&
                    !GameMenuUiLaw.PlayerPanelMayOpen(_settingsOpen)) continue;
                ActivateMicroMenuButton(button.Id);
            }
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\UI-MicroButton-Hilight");
                if (highlight != 0) dl.AddImage((nint)highlight, min, max);
                GameTooltipOwnerKey tooltipOwner = new("micro-button", (ulong)button.Id + 1);
                string tooltipLabel = MicroMenuUiLaw.TooltipTitle(button.Label,
                    MicroMenuBindingText(button.Id));
                string newbieText = button.NewbieText;
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipLabel);
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 300f * scale);
                    ImGui.TextWrapped(newbieText);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                });
            }
        }
        ImGui.End();
    }

    private bool MicroMenuButtonPushed(MicroMenuButtonId button) => button switch
    {
        MicroMenuButtonId.Character => _characterOpen,
        MicroMenuButtonId.Spellbook => _spellbookOpen,
        MicroMenuButtonId.Talents => _talentOpen,
        MicroMenuButtonId.QuestLog => _questLogOpen,
        MicroMenuButtonId.Social => _socialOpen,
        MicroMenuButtonId.WorldMap => _worldMapOpen,
        MicroMenuButtonId.MainMenu => _settingsOpen,
        MicroMenuButtonId.Help => _helpOpen,
        _ => false,
    };

    private string? MicroMenuBindingText(MicroMenuButtonId button)
    {
        GameBinding? binding = button switch
        {
            MicroMenuButtonId.Character => GameBinding.OpenCharacter,
            MicroMenuButtonId.Spellbook => GameBinding.OpenSpellbook,
            MicroMenuButtonId.Talents => GameBinding.OpenTalents,
            MicroMenuButtonId.QuestLog => GameBinding.OpenQuestLog,
            MicroMenuButtonId.Social => GameBinding.OpenSocial,
            MicroMenuButtonId.WorldMap => GameBinding.OpenWorldMap,
            _ => null,
        };
        return binding is { } action ? FriendlyHotkey(BoundKeys(action).Primary) : null;
    }

    private void ActivateMicroMenuButton(MicroMenuButtonId button)
    {
        switch (button)
        {
            case MicroMenuButtonId.Character:
                ToggleCharacterPageThroughUiPanel();
                break;
            case MicroMenuButtonId.Spellbook:
                ToggleSpellbookThroughUiPanel();
                break;
            case MicroMenuButtonId.Talents:
                if (_talentOpen) _talentOpen = false; else OpenTalentPanel();
                break;
            case MicroMenuButtonId.QuestLog:
                _questLogOpen = !_questLogOpen;
                if (_questLogOpen)
                {
                    CloseQuestNpcFrame(playSound: true);
                    // The overflow half of the log lives only on the wire, so the
                    // panel that displays it has to ask. Rate-limited.
                    RequestPartyQuestFacts("quest log opened");
                }
                break;
            case MicroMenuButtonId.Social:
                if (_socialOpen) _socialOpen = false; else OpenSocial();
                break;
            case MicroMenuButtonId.WorldMap:
                ToggleWorldMap();
                break;
            case MicroMenuButtonId.MainMenu:
                ToggleSettingsFromMicroButton();
                break;
            case MicroMenuButtonId.Help:
                if (_helpOpen) _helpOpen = false; else OpenHelp();
                break;
        }
    }

    private void DrawCharacterMicroPortrait(ImDrawListPtr dl, Vector2 buttonMin,
        float scale, bool pushed)
    {
        // Round copy: this is a crop of the bake laid over round button art, and
        // the crop's own corners fall OUTSIDE the inscribed circle - the square
        // bake shows booth black in them.
        uint portrait = _freeView
            ? PartyPortraitHandle(ControlledGuid)
            : RoundAperturePortrait(_playerPortrait, PlayerPortraitCurrent);
        if (portrait == 0) return;

        // MicroMenu.xml:161-199,244-257: this is a crop of the same player portrait bake,
        // 18x25 at the button TOP -28. Render targets are vertically flipped for ImGui.
        Vector2 min = buttonMin + new Vector2(5.5f, 28f) * scale;
        Vector2 max = min + new Vector2(18f, 25f) * scale;
        Vector2 uv0 = pushed ? new Vector2(0.2666f, 0.8333f) : new Vector2(0.2f, 0.9f);
        Vector2 uv1 = pushed ? new Vector2(0.8666f, 0f) : new Vector2(0.8f, 0.0666f);
        uint tint = pushed ? 0x80ffffffu : 0xffffffffu;
        dl.AddImage((nint)portrait, min, max, uv0, uv1, tint);
    }

    private void DrawMainMenuBarArt(ImDrawListPtr dl, Vector2 barMin, float scale)
    {
        if (PainterlyUi)
        {
            // Flat strip instead of the sculpted dwarf plate and end caps.
            DrawPainterlyBarBacking(dl, barMin + new Vector2(0f, 10f) * scale,
                new Vector2(GameplayBarWidth, 43f) * scale, scale);
            DrawCenteredActionText(dl,
                barMin + new Vector2(GameplayBarWidth * 0.5f + 30f, GameplayBarHeight * 0.5f + 5f) * scale,
                _actionPage.ToString(), 11f * scale, UiGoldU32());
            return;
        }

        uint dwarf = _gameplayArt!.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf.blp");
        if (dwarf != 0)
        {
            float[] top = [0.83203125f, 0.58203125f, 0.33203125f, 0.08203125f];
            for (int i = 0; i < 4; i++)
            {
                Vector2 min = barMin + new Vector2(i * 256f, 10f) * scale;
                Vector2 max = min + new Vector2(256f, 43f) * scale;
                dl.AddImage((nint)dwarf, min, max, new Vector2(0, top[i]), new Vector2(1, top[i] + 0.16796875f));
            }
        }

        uint cap = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf.blp");
        if (cap != 0)
        {
            Vector2 size = new(128f * scale);
            // FrameXML anchors their centers at bar center +/-544, with bottoms flush to screen.
            Vector2 left = barMin + new Vector2(-96f, GameplayBarHeight - 128f) * scale;
            Vector2 right = barMin + new Vector2(992f, GameplayBarHeight - 128f) * scale;
            dl.AddImage((nint)cap, left, left + size);
            dl.AddImage((nint)cap, right, right + size, new Vector2(1, 0), new Vector2(0, 1));
        }

        float pageSize = 11f * scale;
        DrawCenteredActionText(dl,
            barMin + new Vector2(GameplayBarWidth * 0.5f + 30f, GameplayBarHeight * 0.5f + 5f) * scale,
            _actionPage.ToString(), pageSize, UiGoldU32());
    }

    private void DrawExpBar(ImDrawListPtr dl, Vector2 barMin, float scale)
    {
        if (_gameplayArt is null || _net is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        uint current = player.Fields.Experience;
        uint maximum = player.Fields.NextLevelExperience;
        float fraction = maximum > 0 ? Math.Clamp((float)current / maximum, 0f, 1f) : 0f;
        Vector2 size = new(GameplayBarWidth * scale, 13f * scale);
        dl.AddRectFilled(barMin, barMin + size, 0x80000000u);
        DrawVanillaStatusBar(dl, barMin, size, fraction, new Vector4(0.58f, 0f, 0.55f, 1f));
        uint rested = player.Fields.RestStateExperience;
        if (maximum > 0 && rested > 0)
        {
            float restedFraction = Math.Clamp((float)(current + Math.Min(rested, maximum)) / maximum, fraction, 1f);
            Vector2 restedMin = new(barMin.X + size.X * fraction, barMin.Y);
            Vector2 restedMax = new(barMin.X + size.X * restedFraction, barMin.Y + size.Y);
            dl.AddRectFilled(restedMin, restedMax, 0xB0B06000u);
        }

        uint dwarf = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf.blp");
        if (dwarf != 0)
        {
            float center = barMin.X + GameplayBarWidth * 0.5f * scale;
            (float X, float Top)[] notches =
            [
                (-384f, 0.79296875f), (-128f, 0.54296875f),
                (128f, 0.29296875f), (384f, 0.04296875f),
            ];
            foreach ((float x, float top) in notches)
            {
                Vector2 min = new(center + (x - 128f) * scale, barMin.Y);
                Vector2 max = min + new Vector2(256f, 10f) * scale;
                dl.AddImage((nint)dwarf, min, max, new Vector2(0f, top),
                    new Vector2(1f, top + 0.0390625f));
            }
        }

        Vector2 mouse = ImGui.GetIO().MousePos;
        if (mouse.X >= barMin.X && mouse.X <= barMin.X + size.X &&
            mouse.Y >= barMin.Y && mouse.Y <= barMin.Y + size.Y)
        {
            GameTooltipOwnerKey tooltipOwner = new("main-menu-xp", 1);
            string experienceText = $"Experience: {current} / {maximum}";
            string restedText = $"Rested bonus: {rested} ({RestStateName(player.Fields.RestState)})";
            OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(experienceText);
                ImGui.TextUnformatted(restedText);
                ImGui.EndTooltip();
            });
        }
    }

    private void DrawPerformanceMeter(ImDrawListPtr dl, Vector2 barMin, float scale, Vector2 display)
    {
        if (_gameplayArt is null) return;
        uint texture = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar.blp");
        if (texture == 0) return;

        Vector2 frameBottomRight = new(
            barMin.X + (GameplayBarWidth - 227f) * scale,
            display.Y + 10f * scale);
        Vector2 frameTopLeft = frameBottomRight - new Vector2(16f, 64f) * scale;
        Vector2 textureTopRight = new(frameBottomRight.X, frameTopLeft.Y);
        Vector2 textureTopLeft = textureTopRight - new Vector2(20f * scale, 0f);
        Vector2 textureBottomRight = textureTopLeft + new Vector2(20f, 66f) * scale;

        int latency = _net?.LatencyMs ?? 0;
        Vector4 tint = latency > 600 ? new Vector4(1f, 0f, 0f, 1f)
            : latency > 300 ? new Vector4(1f, 1f, 0f, 1f)
            : new Vector4(0f, 1f, 0f, 1f);
        dl.AddImage((nint)texture, textureTopLeft, textureBottomRight,
            Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(tint));

        Vector2 mouse = ImGui.GetIO().MousePos;
        if (mouse.X >= frameTopLeft.X && mouse.X <= frameBottomRight.X &&
            mouse.Y >= frameTopLeft.Y && mouse.Y <= frameBottomRight.Y)
        {
            GameTooltipOwnerKey tooltipOwner = new("main-menu-performance", 1);
            string latencyText = $"Latency: {latency}ms";
            OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(latencyText);
                ImGui.EndTooltip();
            });
        }
    }

    private static GameTooltipOwnerKey ActionBarGameTooltipOwner(
        string surfaceName,
        int buttonIndex)
    {
        if ((uint)buttonIndex >= MultiActionBarUiLaw.ButtonsPerBar)
            throw new ArgumentOutOfRangeException(nameof(buttonIndex));
        string surface = surfaceName switch
        {
            "MultiBarBottomLeft" => "action-multi-bottom-left",
            "MultiBarBottomRight" => "action-multi-bottom-right",
            "MultiBarRight" => "action-multi-right",
            "MultiBarLeft" => "action-multi-left",
            _ => throw new ArgumentOutOfRangeException(nameof(surfaceName), surfaceName,
                "Unknown multi-action-bar tooltip surface."),
        };
        return new GameTooltipOwnerKey(surface, (ulong)(buttonIndex + 1));
    }

    private bool DrawPageArrowButton(ImDrawListPtr dl, Vector2 barMin, float scale,
        Vector2 center, string direction, int id)
    {
        Vector2 min = barMin + (center - new Vector2(16f)) * scale;
        Vector2 max = min + new Vector2(32f) * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##action-page-{id}", max - min);
        bool pushed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        uint texture = _gameplayArt?.Handle(
            $@"Interface\MainMenuBar\UI-MainMenu-Scroll{direction}Button-{(pushed ? "Down" : "Up")}") ?? 0;
        if (texture != 0) dl.AddImage((nint)texture, min, max);
        if (hovered)
        {
            // ActionBar.xml marks this texture ADD. GameplayArt converts its black additive
            // background to transparent for ImGui's alpha compositor.
            uint highlight = _gameplayArt?.AdditiveHandle(
                $@"Interface\MainMenuBar\UI-MainMenu-Scroll{direction}Button-Highlight") ?? 0;
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
        }
        return clicked;
    }

    private int ActionWireSlot(int button) => (_actionPage - 1) * 12 + button;

    private void ChangeActionPage(int delta)
    {
        _actionPage = ((_actionPage - 1 + delta + ActionPageCount) % ActionPageCount) + 1;
        _pressedActionSlot = -1;
    }

    private static uint UiGoldU32() =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0f, 1f));

    // size is a device-pixel height. Exact-size FRIZQT from the baked atlas with the classic
    // +1,-shadow, never the ImGui default font (game UI never uses the ImGui font). DrawPlain's
    // shadow offset is floor(uiScale) = 1 device px, matching the old manual +(1,1) shadow.
    private static void DrawCenteredActionText(ImDrawListPtr dl, Vector2 center, string text,
        float size, uint color)
        => GameText.DrawPlainCentered(dl, text, center, size, 1f, color, 0xff000000u);

    private bool DrawSlotRing(ImDrawListPtr dl, Vector2 buttonMin, Vector2 buttonMax,
        string art, float scale, uint tint = 0xffffffffu, bool emptySlot = false)
    {
        // Painterly slots are square. This is the one place both the main bar
        // and every multi-bar ask for slot chrome, so squaring it here squares
        // all of them. The usability tint still carries - it moves onto the
        // frame's rule instead of the ring, so "not enough power" and
        // "unusable" read exactly as before.
        if (PainterlyUi)
        {
            // fill ONLY for an empty slot: an occupied one already has its icon
            // drawn underneath, and backing it would black the icon out.
            // No corner studs either - four per slot across twelve slots reads
            // as clutter, and the bar's own frame carries the ornament.
            DrawSquarePanel(dl, buttonMin, buttonMax - buttonMin, scale, fill: emptySlot,
                ruleColor: tint == 0xffffffffu ? PainterlyFrameRule : tint, studs: false);
            return true;
        }

        uint ring = _gameplayArt!.Handle(art);
        if (ring == 0) return false;
        // NormalTexture is 66x66, centered on the 36x36 button with a (0,-1) offset.
        // FrameXML Y is up: its -1 anchor offset moves the texture one pixel down in screen space.
        Vector2 center = (buttonMin + buttonMax) * 0.5f + new Vector2(0, scale);
        Vector2 half = new(33f * scale);
        dl.AddImage((nint)ring, center - half, center + half, Vector2.Zero, Vector2.One, tint);
        return true;
    }

    /// <summary>Hotkey label: right-justified in the top corner (reference offset (-2,-2)),
    /// with the authored 1.12 two-pixel THICK outline.</summary>
    private static void DrawActionText(ImDrawListPtr dl, Vector2 buttonMin, string text, float scale,
        uint color)
    {
        if (string.IsNullOrEmpty(text)) return;
        float textTop = GameText.BoxCenteredTop("NumberFontNormalSmallGray",
            buttonMin.Y + 2f * scale, 10f, scale);
        GameText.DrawRightAligned(dl, "NumberFontNormalSmallGray", text,
            new Vector2(buttonMin.X + 34f * scale, textTop), scale, color);
    }

    /// <summary>Stack count for an ITEM action, bottom-right (reference offset (-2,2)).</summary>
    private static void DrawActionCount(ImDrawListPtr dl, Vector2 buttonMax, int count, float scale)
    {
        string text = count.ToString();
        float textTop = buttonMax.Y - 2f * scale -
            GameText.EmPixels("NumberFontNormal", scale);
        GameText.DrawRightAligned(dl, "NumberFontNormal", text,
            new Vector2(buttonMax.X - 2f * scale, textTop), scale);
    }

    private ActionButtonVerdict ComputeButtonVerdict(
        int slot,
        ActionSlot? action,
        SpellInfo? spell,
        WorldEntity? player,
        bool pushed,
        bool hover,
        bool carriedGrid)
    {
        bool isItem = action is { Kind: ActionSlot.Item };
        uint actionId = action?.ActionId ?? 0;
        ButtonUsability usability = action is null ? ButtonUsability.Unusable : ButtonUsability.Usable;
        int powerCost = 0, currentPower = 0, baseMana = 0, stackCount = 0;
        bool equipped = false;

        if (player is { } p)
        {
            baseMana = (int)Math.Min(p.Fields.BaseMana, int.MaxValue);
            if (spell is { } sp)
            {
                if (p.IsDead)
                {
                    usability = ButtonUsability.Unusable;
                }
                else
                {
                    byte powerType = (byte)sp.PowerType;
                    uint baseAmount = sp.ManaCostPercent == 0 ? 0u
                        : powerType == 0 ? p.Fields.BaseMana
                        : p.Fields.MaxPower(powerType);
                    uint cost = sp.ManaCost + baseAmount * sp.ManaCostPercent / 100;
                    uint power = p.Fields.Power(powerType);
                    powerCost = (int)Math.Min(cost, int.MaxValue);
                    currentPower = (int)Math.Min(power, int.MaxValue);
                    if (cost > 0 && power < cost)
                        usability = ButtonUsability.NotEnoughPower;
                }
            }
            else if (isItem)
            {
                stackCount = CountItemInBags(p, actionId);
                equipped = IsItemEquipped(p, actionId);
                // Preserve the reference/MSUI state-feed rule: item-button greying follows the
                // bag count or a worn copy. The broader mode-0x47 walk belongs only to UseAction;
                // using it here would incorrectly light an equipped-bag object or keyring-only item.
                usability = stackCount > 0 || equipped
                    ? ButtonUsability.Usable : ButtonUsability.Unusable;
            }
        }

        ButtonRange range = ButtonRange.NoCheck;
        int rangeIndex = spell is { } indexed
            ? (int)Math.Min(indexed.RangeIndex, int.MaxValue) : 0;
        float rangeMin = 0f, rangeMax = 0f, distance = -1f;
        if (spell is { } rangeSpell)
            (range, rangeMin, rangeMax, distance) = ComputeButtonRange(rangeSpell);

        bool isAttack = spell is { Id: 6603 };
        bool engaged = isAttack && _net is not null && _combat.IsEngaged(ControlledGuid);
        bool autoRepeat = spell is { } repeat && repeat.Id == _autoRepeatSpell;
        bool checkedState = engaged || autoRepeat ||
            (spell is { } pending &&
             (pending.Id == _pendingCastSpell || pending.Id == _queuedMeleeSpell));

        return new ActionButtonVerdict(
            NowSeconds(), slot, isItem, actionId, usability, range,
            pushed, hover, checkedState, engaged || autoRepeat, carriedGrid, equipped,
            powerCost, currentPower, baseMana, rangeIndex, rangeMin, rangeMax, distance, stackCount);
    }

    private (ButtonRange Range, float Min, float Max, float Distance)
        ComputeButtonRange(in SpellInfo spell)
    {
        if (_net is null || !TryGetControlledBodyPose(out WorldBodyPose controlledBody) ||
            _spellCatalog is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) ||
            !_spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow row))
            return (ButtonRange.NoCheck, 0f, 0f, -1f);

        float selfReach = _entities.TryGet(ControlledGuid, out WorldEntity self)
            ? self.Fields.CombatReach : 1.5f;
        float targetReach = target.Fields.CombatReach;
        float min = row.Min, max = row.Max;
        if (row.Melee)
        {
            min = 0f;
            max = MathF.Max(selfReach + targetReach + 1.3333f, 5f);
        }
        else
        {
            if (min <= 0f && max <= 0f)
                return (ButtonRange.NoCheck, min, max, -1f);
            max += selfReach + targetReach;
            if (min != 0f) min += selfReach + targetReach;
        }

        float distanceSquared = Vector3.DistanceSquared(controlledBody.Position, target.Position);
        float distance = MathF.Sqrt(distanceSquared);
        ButtonRange range = distanceSquared >= min * min && distanceSquared <= max * max
            ? ButtonRange.InRange : ButtonRange.OutOfRange;
        return (range, min, max, distance);
    }

    private void EmitActionButtonVerdict(in ActionButtonVerdict verdict)
    {
        ActionButtonVerdict? previous = _lastActionButtonVerdicts[verdict.Slot];
        bool changed = previous is not { } old ||
            old.Usability != verdict.Usability ||
            old.Range != verdict.Range ||
            old.Flashing != verdict.Flashing ||
            old.Checked != verdict.Checked;
        _lastActionButtonVerdicts[verdict.Slot] = verdict;
        if (!changed) return;
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:action] {verdict.ToLine()}");
    }

    /// <summary>Total stack count of an item entry across the backpack and equipped bags
    /// (the GetActionCount an ITEM action shows bottom-right).</summary>
    private int CountItemInBags(WorldEntity player, uint entry)
    {
        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                total += (int)Math.Max(1, item.Fields.ItemStackCount);
        }
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(bag.Fields.ContainerNumSlots, 36);
            for (int slot = 0; slot < slots; slot++)
            {
                ulong guid = bag.Fields.ContainerSlot(slot);
                if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                    total += (int)Math.Max(1, item.Fields.ItemStackCount);
            }
        }
        return total;
    }

    /// <summary>IsEquippedAction: any worn slot 0..18 holds this entry (green ADD border).</summary>
    private bool IsItemEquipped(WorldEntity player, uint entry)
    {
        for (int slot = 0; slot < 19; slot++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                return true;
        }
        return false;
    }

    private static void DrawCooldownSwipe(ImDrawListPtr dl, Vector2 min, Vector2 max, float elapsedFraction)
    {
        uint shade = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0, 0, 0, CooldownVisualLaw.WipeAlpha));
        foreach (CooldownVisualLaw.Quad q in CooldownVisualLaw.BuildWipe(min, max, elapsedFraction))
            dl.AddQuadFilled(q.A, q.B, q.C, q.D, shade);
    }

    private void DrawCooldownFlash(ImDrawListPtr dl, Vector2 min, Vector2 max, float progress)
    {
        uint star = _gameplayArt?.AdditiveHandle(@"Interface\Cooldown\star4") ?? 0;
        if (star == 0) return;
        Vector2 center = (min + max) * 0.5f;
        Vector2 half = (max - min) * 0.5f * CooldownVisualLaw.FlashScale(progress);
        uint tint = ImGui.ColorConvertFloat4ToU32(
            new Vector4(1f, 1f, 1f, CooldownVisualLaw.FlashAlpha(progress)));
        dl.AddImage((nint)star, center - half, center + half, Vector2.Zero, Vector2.One, tint);
    }

    private void DisposeGameplayUi()
    {
        DrainMinimapTexturePreparation();
        _gameplayArt?.Dispose();
        _gameplayArt = null;
    }
}
