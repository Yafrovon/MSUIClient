using ImGuiNET;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

/// <summary>
/// Free View commander console — the WC3-style bottom dock (Drawing 2 +
/// owner design language in CRPG_RTS_MMO_PARTY_COMMAND_UI.md: vanilla skin,
/// WC3 command grammar). Three fixed regions: the SQUAD grid (all ten slots,
/// WC3 group wells), the INFO panel (scope line, then a portrait unit card
/// for a single selection or portrait chips for a group), and the COMMAND
/// CARD — an icon grid whose art is the vanilla pet bar's own idiom
/// (FrameXML: Attack=Ability_GhoulFrenzy, Follow=Ability_Tracking,
/// Wait=Spell_Nature_TimeStop; the rest archive-verified via mpqpeek).
/// Formation and sheath orders are SuperUI-Core order types (8/9/10).
/// </summary>
public sealed partial class GameLoop
{
    // Shelf-level toggle; the next sheath order inverts it. Bots spawn armed.
    private bool _rtsWeaponsSheathed;

    // The PRIMARY of the current selection (WC3-style): the unit whose card + ability row fill the
    // console's middle panel, gold-bordered in the portrait grid. Set by single-clicking a portrait
    // or Q-cycling; resolves to selection[0] whenever it is unset or has left the selection.
    private ulong _rtsPrimaryGuid;
    private ulong RtsPrimaryGuid =>
        _rtsPrimaryGuid != 0 && _freecamSelection.Contains(_rtsPrimaryGuid)
            ? _rtsPrimaryGuid
            : _freecamSelection.Count > 0 ? _freecamSelection[0] : 0;

    /// <summary>A single character's OWN bag windows (the B route) are up — as opposed to the
    /// shared, guid-addressed party-inventory browser. These windows follow ControlledGuid.</summary>
    private bool SingleCharacterBagsOpen =>
        !_partyInventoryOpen && (_backpackOpen || _keyringOpen || _equippedBagOpen.Any(x => x));

    /// <summary>Move the primary one slot through the selected command cards (Q / Shift+Q).
    /// An empty or one-card selection never expands to nearby faction units.</summary>
    private void CycleRtsPrimary(int dir)
    {
        if (_freecamSelection.Count < 2) return;
        int idx = _freecamSelection.IndexOf(RtsPrimaryGuid);
        if (idx < 0) idx = 0;
        int n = _freecamSelection.Count;
        _rtsPrimaryGuid = _freecamSelection[((idx + dir) % n + n) % n];
        // A single character's bag panel follows ControlledGuid, so when one is open Q must hand
        // control to the new primary too — otherwise you keep staring at the previous character's
        // bags. The shared party browser is guid-addressed and needs no switch.
        if (SingleCharacterBagsOpen && _rtsPrimaryGuid != ControlledGuid)
            BeginControlHandover(_rtsPrimaryGuid);
    }

    /// <summary>Take control of a bot (possess-on-open/cast/use), switching from any other body.
    /// No-op if you already drive it or it's your own character. Control lands asynchronously; the
    /// bag windows and bars follow ControlledGuid the moment it does.</summary>
    private void EnsurePossessingBot(ulong bot)
    {
        if (bot == 0 || _controlTargetGuid == bot) return;
        BeginControlHandover(bot);
    }

    /// <summary>
    /// Hand control to <paramref name="subject"/> so a queued primary action (cast / use / bag open)
    /// can fire once control lands: possess a bot — releasing any OTHER bot first — or, when the
    /// subject is your own character, release home. Releases go out toFreecam:FALSE so the release
    /// ACK stays on the CHAINING path (ApplySuiControlAck consumes _controlSwitchQueued → possess the
    /// next bot); toFreecam:true instead hit the "enter free view" early-return that dropped you on
    /// yourself and never possessed the queued bot — the "hop to me, then click again" bug. The
    /// camera stays in the sky either way (staysInFreeView).
    /// </summary>
    private void BeginControlHandover(ulong subject)
    {
        if (RefuseTacticalFreezeLiveCommand("changing control")) return;
        if (subject != LocalPlayerGuid && RefuseTacticalFrozenActor(subject, "take control")) return;
        if (subject == LocalPlayerGuid)
        {
            // Come home to your own body — no possess needed; the release lands on you in the sky.
            if (_controlState == ControlState.Possessing)
            {
                _controlSwitchQueued = 0;
                RequestControlRelease(toFreecam: false);
            }
            return;
        }
        if (_controlState is ControlState.OwnChar or ControlState.FreeCam)
            RequestPossess(subject);
        else if (_controlState == ControlState.Possessing && _controlTargetGuid != subject)
        {
            _controlSwitchQueued = subject;          // possess the subject once the release lands
            RequestControlRelease(toFreecam: false);
        }
    }

    // A bag-open queued while control of the primary is handed over (possess-on-open). The bag panel
    // targets ControlledGuid, so opening before control lands flashes your OWN bags; we wait until
    // control reaches the subject, then open ITS bags. Twin of the possess-on-cast/use pending state.
    private ulong _pendingBagsSubject;
    private bool _pendingBagsAll;
    private double _pendingBagsAt;
    private bool _pendingBagsArmed;

    /// <summary>
    /// Open the PRIMARY's bags from the free view, handing control to it first if needed: possess a
    /// bot, or release back to your own body when the primary is yourself. The window open is
    /// deferred (<see cref="TryFirePendingPrimaryBags"/>) until control lands, so it shows the
    /// subject's bags — never a flash of your own — and Tab-to-self + B returns you to your bags.
    /// </summary>
    private void OpenPrimaryBags(bool all)
    {
        ulong subject = RtsPrimaryGuid != 0 ? RtsPrimaryGuid : LocalPlayerGuid;
        if (subject == ControlledGuid)
        {
            // Already on the subject's body — plain toggle (also how B closes the open bags).
            _pendingBagsArmed = false;
            if (all) ToggleAllBags(); else ToggleBackpack();
            return;
        }
        if (RefuseTacticalFreezeLiveCommand("changing control") ||
            RefuseTacticalFrozenActor(subject, "take control"))
            return;
        EnsurePossessingBot(subject);   // possess a bot, or release to your own char (subject == you)
        _pendingBagsSubject = subject;
        _pendingBagsAll = all;
        _pendingBagsAt = NowSeconds();
        _pendingBagsArmed = true;
    }

    /// <summary>Open the queued possess-on-open bags once control has landed on the subject. Runs
    /// every frame; times out so a denied/slow hand-over leaves nothing armed.</summary>
    private void TryFirePendingPrimaryBags()
    {
        if (!_pendingBagsArmed) return;
        if (NowSeconds() - _pendingBagsAt > 2.5)
        {
            _pendingBagsArmed = false;
            _pendingBagsSubject = 0;
            return;
        }
        if (ControlledGuid != _pendingBagsSubject) return;   // control not there yet — keep waiting
        _pendingBagsArmed = false;
        _pendingBagsSubject = 0;
        // Explicit OPEN (not toggle): after the hand-over the panel is freshly on the subject.
        if (_pendingBagsAll && _entities.TryGet(ControlledGuid, out WorldEntity player))
            SetAllNormalBagWindows(player, true);
        else
            SetBagWindowOpen(0, true);
    }

    // A cast queued while control of the primary is still being handed over (possess-on-cast).
    private ulong _pendingCastPrimary;
    private uint _pendingCastSpellId;
    private ulong _pendingCastExplicitTarget;
    private double _pendingCastAt;

    // A friendly unit spell chosen from a multi-selection waits here until the commander clicks
    // a world body, party frame, player frame, or selection portrait. This is deliberately not
    // _selectionGuid: choosing a heal target must not replace the RTS command selection/focus.
    private ulong _rtsUnitCastPrimary;
    private uint _rtsUnitCastSpellId;
    private ulong _rtsUnitCastTacticalLockId;

    /// <summary>
    /// Cast an ability from the PRIMARY's command card, riding the possess wire (step 2, no new
    /// server hook): if you already drive the primary, cast now; otherwise take control of it —
    /// releasing any other body first — and fire the cast the moment its bars are live. The
    /// primary stays possessed afterwards, so further casts on it are instant.
    /// </summary>
    private void CastPrimaryAbility(ulong primary, uint spellId, bool altPrimaryCast = false,
        ulong explicitTarget = 0)
    {
        if (primary == 0 || spellId == 0) return;
        TacticalLockView? ownedTactical = OwnedActiveTacticalLock;
        // An owned active lock is the one sanctioned queue-authoring mode. A foreign held actor
        // or an owned FIFO that is only draining must not arm a friendly cursor/pending handoff
        // that can turn into a live cast after DRAINED.
        if (ownedTactical is null &&
            (RefuseTacticalFreezeLiveCommand("casting live spells") ||
             RefuseTacticalFrozenActor(primary, "cast live spells")))
        {
            CancelRtsUnitCastTargeting(silent: true);
            CancelPendingPrimaryCast();
            return;
        }
        if (ownedTactical is not null &&
            (!ownedTactical.Members.TryGetValue(primary, out TacticalFreezeMember frozenCaster) ||
             !frozenCaster.Frozen || !frozenCaster.CommandableByRecipient))
        {
            ShowUiError("That frozen unit is read-only.");
            return;
        }
        SpellInfo? targetSpell = _spellCatalog?.TryGet(spellId, out SpellInfo foundSpell) == true
            ? foundSpell : null;
        bool acceptsFriendly = targetSpell is SpellInfo friendlySpell &&
            CastTargetLaw.AcceptsExplicitFriendlyUnit(friendlySpell);
        if (explicitTarget == 0)
        {
            RtsAbilityCastIntent intent = RtsAbilityTargetLaw.Resolve(
                _freecamSelection.Count, altPrimaryCast, acceptsFriendly);
            if (intent == RtsAbilityCastIntent.ChooseFriendlyTarget)
            {
                _groundCastSpell = 0;
                _groundCursorPoint = null;
                CancelItemTargeting();
                _pendingCastPrimary = 0;
                _pendingCastSpellId = 0;
                _pendingCastExplicitTarget = 0;
                _rtsUnitCastPrimary = primary;
                _rtsUnitCastSpellId = spellId;
                _rtsUnitCastTacticalLockId = ownedTactical?.LockId ?? 0;
                string spellName = targetSpell?.Name ?? $"Spell {spellId}";
                SetRtsControlGroupStatus($"{spellName}: choose a friendly target " +
                    "(right-click or Escape cancels). Alt casts on the primary.");
                return;
            }
            if (intent == RtsAbilityCastIntent.CastOnPrimary)
                explicitTarget = primary;
        }
        // Under an owned Tactical Freeze the visible card authors this actor's server queue
        // directly. It must never ride the possession hand-off used by live Command View casts.
        if (TryQueueTacticalSpell(primary, spellId, explicitTarget))
        {
            CancelRtsUnitCastTargeting(silent: true);
            return;
        }
        CancelRtsUnitCastTargeting(silent: true);
        // Already on the subject and able to author it — a driven bot, or your OWN character (which
        // stays castable from the sky: the cast applies to _player, your own self-mover) → cast now.
        // NB the self path requires ControlledGuid == you: if you're driving a bot while yourself is
        // the primary, TryCast would cast as the BOT, so fall through to a release-home first.
        if (ControlledGuid == primary && (CanAuthorControlledGameplay || primary == LocalPlayerGuid))
        {
            TryCast(spellId, explicitTarget);
            return;
        }
        // Otherwise hand control to the primary and fire the cast the moment it lands
        // (TryFirePendingPrimaryCast): possess-on-cast for a bot, or release-home-then-cast for you.
        _pendingCastPrimary = primary;
        _pendingCastSpellId = spellId;
        _pendingCastExplicitTarget = explicitTarget;
        _pendingCastAt = NowSeconds();
        BeginControlHandover(primary);
    }

    /// <summary>Consume a click while a multi-selection heal/buff is awaiting its unit.</summary>
    private bool TryCommitRtsUnitCastTarget(ulong targetGuid)
    {
        if (_rtsUnitCastSpellId == 0) return false;
        if (_rtsUnitCastTacticalLockId != 0 &&
            OwnedActiveTacticalLock?.LockId != _rtsUnitCastTacticalLockId)
        {
            CancelRtsUnitCastTargeting(silent: true);
            ShowUiError("That Tactical Freeze is no longer active.");
            return true;
        }
        if (_rtsUnitCastTacticalLockId == 0 &&
            RefuseTacticalFrozenActor(targetGuid, "target it with a live spell"))
            return true;
        if (_rtsUnitCastPrimary == 0 || !_freecamSelection.Contains(_rtsUnitCastPrimary) ||
            _spellCatalog?.TryGet(_rtsUnitCastSpellId, out SpellInfo spell) != true)
        {
            CancelRtsUnitCastTargeting(silent: false);
            return true;
        }
        if (targetGuid == 0 || !_entities.TryGet(targetGuid, out WorldEntity target))
        {
            ShowUiError("Choose a friendly unit — right-click or Escape cancels.");
            return true;
        }
        CastTargetCandidate candidate = CastCandidate(target,
            isSelf: targetGuid == _rtsUnitCastPrimary);
        CastTargetVerdict verdict = CastTargetLaw.Resolve(spell, candidate, self: null,
            autoSelfCast: false);
        if (verdict.Kind != CastTargetKind.Unit || verdict.Guid != targetGuid)
        {
            ShowUiError("That is not a valid friendly target.");
            return true;
        }

        ulong primary = _rtsUnitCastPrimary;
        uint spellId = _rtsUnitCastSpellId;
        CancelRtsUnitCastTargeting(silent: true);
        CastPrimaryAbility(primary, spellId, explicitTarget: targetGuid);
        return true;
    }

    private bool CancelRtsUnitCastTargeting(bool silent)
    {
        if (_rtsUnitCastSpellId == 0) return false;
        _rtsUnitCastPrimary = 0;
        _rtsUnitCastSpellId = 0;
        _rtsUnitCastTacticalLockId = 0;
        if (!silent) SetRtsControlGroupStatus("Spell targeting cancelled.");
        return true;
    }

    /// <summary>
    /// Fire a possess-on-cast ability once control has landed on the primary and its spellbook has
    /// synced. Run every frame from UpdateControlInput; gives up after a short window so a denied
    /// or slow possession does not leave a stale cast armed.
    /// </summary>
    private void TryFirePendingPrimaryCast()
    {
        if (_pendingCastSpellId == 0) return;
        if (TacticalFreezeBlocksLiveCommands)
        {
            CancelPendingPrimaryCast();
            return;
        }
        if (NowSeconds() - _pendingCastAt > 2.5)
        {
            _pendingCastSpellId = 0;
            _pendingCastPrimary = 0;
            _pendingCastExplicitTarget = 0;
            return;
        }
        // Wait until control has reached the subject — a possessed bot, or your own body after a
        // release-home — and its spellbook has synced. Own char authors without possession.
        if (ControlledGuid != _pendingCastPrimary) return;
        if (!CanAuthorControlledGameplay && _pendingCastPrimary != LocalPlayerGuid) return;
        if (!ActionsFor(_pendingCastPrimary).KnownSpells.Contains(_pendingCastSpellId)) return;
        uint spellId = _pendingCastSpellId;
        ulong explicitTarget = _pendingCastExplicitTarget;
        _pendingCastSpellId = 0;
        _pendingCastPrimary = 0;
        _pendingCastExplicitTarget = 0;
        TryCast(spellId, explicitTarget);
    }

    private void CancelPendingPrimaryCast()
    {
        _pendingCastPrimary = 0;
        _pendingCastSpellId = 0;
        _pendingCastExplicitTarget = 0;
        _pendingCastAt = 0;
    }

    private const int RtsPrimaryAbilityLimit = 8;

    private List<(int Slot, ActionSlot Action, SpellInfo Spell)> RtsPrimaryAbilities(ulong guid)
    {
        var result = new List<(int, ActionSlot, SpellInfo)>(RtsPrimaryAbilityLimit);
        PlayerActions store = ActionsFor(guid);
        for (int slot = 0; slot < 12 && result.Count < RtsPrimaryAbilityLimit; slot++)
            if (store[slot] is ActionSlot action && action.Kind == 0 &&
                _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true)
                result.Add((slot, action, spell));
        return result;
    }

    /// <summary>Route the existing bindable Action Button 1..10 keys to the visible primary
    /// card while in Free View. Returning true means the commander card owns the binding even
    /// when that numbered card slot is empty, so the hidden body bar cannot cast by accident.</summary>
    private bool TryUseRtsPrimaryAbilityBinding(int abilityIndex, bool altPrimaryCast = false)
    {
        if (!_freeView) return false;
        ulong primary = RtsPrimaryGuid;
        if (primary == 0) return true;
        List<(int Slot, ActionSlot Action, SpellInfo Spell)> abilities =
            RtsPrimaryAbilities(primary);
        if ((uint)abilityIndex < (uint)abilities.Count)
            CastPrimaryAbility(primary, abilities[abilityIndex].Action.ActionId, altPrimaryCast);
        return true;
    }

    /// <summary>
    /// "Cast Card Ability on Primary" (RTS Controls) is a supplemental self-cast MODIFIER over
    /// the visible primary card — Alt by default. BindingDown is an exact-chord matcher, so an
    /// ordinary "1" binding does not fire while that modifier is held; match the binding's base
    /// key and its other authored modifiers here while deliberately ignoring the ones the
    /// modifier command itself claims. Reseat it onto Ctrl and Ctrl+1 becomes the self-cast
    /// while Alt+1 goes back to meaning nothing — no other code has to know.
    /// </summary>
    private bool RtsCastOnPrimaryBindingDown(GameBinding binding)
    {
        if (!_freeView || !BindingModifierHeld(GameBinding.RtsCastOnPrimary)) return false;
        (bool maskAlt, bool maskControl, bool maskShift) =
            BindingModifierMask(GameBinding.RtsCastOnPrimary);
        BindingPair pair = BoundKeys(binding);
        return Matches(pair.Primary) || Matches(pair.Secondary);

        bool Matches(BindingChord chord) => chord.IsBound &&
            chord.Pointer == BindingPointerKey.None && InputKeyDown(chord.Key) &&
            (maskAlt || chord.Alt == AltHeld()) &&
            (maskControl || chord.Control == CtrlHeld()) &&
            (maskShift || chord.Shift == ShiftHeld());
    }

    private const byte SuiOrderFormationLine = 8;
    private const byte SuiOrderFormationCircle = 9;
    private const byte SuiOrderSheath = 10;

    // Command-card art. The first three are the vanilla pet bar's own tokens
    // read from the shipped FrameXML (PetActionBarFrame.lua) — the native
    // idiom for commanding an AI companion. The rest exist in the archives.
    private const string ConsoleIconFocus = @"Interface\Icons\ability_hunter_snipershot";
    private const string ConsoleIconRegroup = @"Interface\Icons\spell_frost_stun";
    private const string ConsoleIconHold = @"Interface\Icons\ability_rogue_trip";
    private const string ConsoleIconPatrol = @"Interface\Icons\Ability_Tracking";
    private const string ConsoleIconLine = @"Interface\Icons\spell_nature_moonglow";
    private const string ConsoleIconCircle = @"Interface\Icons\spell_nature_wispsplode";
    private const string ConsoleIconSheathe = @"Interface\Icons\Ability_Warrior_Disarm";
    private const string ConsoleIconDraw = @"Interface\Icons\INV_Sword_04";
    private const string ConsoleIconFreeze = @"Interface\Icons\Spell_Frost_FrostNova";
    private const string ConsoleIconResume = @"Interface\Icons\Spell_Holy_HolyBolt";

    // Console geometry (logical units, × UI scale). Regions are fixed so the
    // dock reads as furniture, never a resizing tooltip.
    private const float ConsoleWidth = 700f;
    private const float ConsoleHeight = 140f;
    private const float ConsoleSquadsX = 10f;
    private const float ConsoleInfoX = 260f;
    private const float ConsoleCardX = 520f;

    private void DrawRtsCommandShelf()
    {
        // The console is the free view's standing furniture — present even with
        // nothing selected, like a WC3 console with an empty info panel.
        if (!_freeView || _net is null) return;
        List<ulong> subjects = [.. RtsControlGroupLaw.NormalizeMembers(_freecamSelection)];

        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        // The commander console owns the bottom edge: the body chrome (action
        // bars, stance, pet, bags, micro menu) stands down in the free view.
        HudFrameResult shelf = HudFrame("command-shelf", "Command shelf",
            HudPlacement.At(HudAnchor.Bottom, 0f, -12f),
            new Vector2(ConsoleWidth, ConsoleHeight));
        if (shelf.Hidden) return;
        DrawTacticalQueueStrip(shelf, scale);
        ImGui.SetNextWindowPos(shelf.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(ConsoleWidth, ConsoleHeight) * scale,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        // NoBringToFrontOnFocus: the shelf is standing HUD furniture, so it must never rise above
        // a panel the player opened. Without it the shelf was display-front while every vanilla
        // panel frame is deliberately display-back, and the Key Bindings footer row landed inside
        // the shelf's rect - FindHoveredWindow returned the shelf and Reset/Unbind/Okay/Cancel
        // went dead in the free view while still drawing. Found by audit, 2026-08-26.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##rts-command-shelf", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        // Warcraft-3 carved-stone tablet in place of the flat tooltip skin: a dark stone body, a
        // near-black ground edge, a gilt inlay that catches the light, and corner studs — the same
        // painted-chrome idiom the square minimap/skill panels already wear.
        DrawRtsConsoleBackdrop(dl, origin, origin + ImGui.GetWindowSize(), scale);
        // Region dividers — carved grooves with a gilt catch, not hairline mullions.
        DrawRtsConsoleDivider(dl, origin, ConsoleInfoX - 16f, scale);
        DrawRtsConsoleDivider(dl, origin, ConsoleCardX - 16f, scale);

        // Left region: the WC3-style portrait grid of the CURRENT selection (primary gold-bordered).
        // The saved control-group "squad" grid (DrawRtsSquadGrid) is shelved for the true-RTS mode.
        DrawRtsSelectionPortraits(dl, origin, scale);
        DrawRtsConsoleInfo(dl, origin, subjects, scale);
        DrawQuickBackpackSlots(dl, origin, scale);
        DrawRtsCommandCard(dl, origin, subjects, scale);
        DrawRtsUnitCastTargetHint(scale);
        ImGui.End();
    }

    private void DrawRtsUnitCastTargetHint(float scale)
    {
        uint targetingSpell = _rtsUnitCastSpellId != 0
            ? _rtsUnitCastSpellId : _tacticalGroundSpellId;
        if (targetingSpell == 0 || _window.MouseCaptured) return;
        string name = _spellCatalog?.TryGet(targetingSpell, out SpellInfo spell) == true
            ? spell.Name ?? "Spell" : "Spell";
        ImGui.GetForegroundDrawList().AddText(
            ImGui.GetIO().MousePos + new Vector2(18f, 14f) * scale, 0xFF00E060,
            _tacticalGroundSpellId != 0
                ? $"{name}: select target area for queue"
                : $"{name}: select friendly target");
    }

    /// <summary>The WC3 command console's carved-stone tablet: dark stone body graduating into
    /// shadow, a near-black ground edge, a bevelled gilt inlay, and corner studs.</summary>
    private void DrawRtsConsoleBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale)
    {
        float rule = MathF.Max(1f, scale);
        float inset = MathF.Max(2f, 3f * scale);
        // Carved stone body, top-lit → shadowed.
        dl.AddRectFilledMultiColor(min, max,
            PainterlyStoneMid, PainterlyStoneMid, PainterlyStoneLow, PainterlyStoneLow);
        // Near-black ground so the stone has an edge against the world, and the outer bevel.
        dl.AddRect(min, max, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule * 2f);
        DrawBevel(dl, min, max, rule, PainterlyStoneTop, PainterlyFrameOuter);
        // Gilt inlay just inside, itself bevelled so it reads as inlaid metal.
        Vector2 gMin = min + new Vector2(inset), gMax = max - new Vector2(inset);
        dl.AddRect(gMin, gMax, PainterlyFrameRule, 0f, ImDrawFlags.None, rule);
        DrawBevel(dl, gMin, gMax, MathF.Max(1f, rule * 0.5f), PainterlyGoldLit, PainterlyGoldShade);
        DrawCornerStuds(dl, min, max, scale);
    }

    /// <summary>A carved groove between console regions: a shadowed cut with a gilt catch on its
    /// lit side, replacing the old hairline mullion.</summary>
    private void DrawRtsConsoleDivider(ImDrawListPtr dl, Vector2 origin, float x, float scale)
    {
        float rule = MathF.Max(1f, scale);
        Vector2 a = origin + new Vector2(x, 12f) * scale;
        Vector2 b = origin + new Vector2(x, ConsoleHeight - 12f) * scale;
        dl.AddLine(a, b, PainterlyFrameOuter, rule * 2f);
        dl.AddLine(a + new Vector2(rule, 0f), b + new Vector2(rule, 0f), PainterlyGoldShade, rule);
    }

    /// <summary>
    /// WC3-style portrait grid of the CURRENT selection — the console's left region. Each cell is a
    /// unit's portrait over a health bar; the PRIMARY (whose card + abilities fill the middle panel)
    /// wears a gold border. Single-click makes a unit primary, double-click centers the camera,
    /// Shift+click drops it. Q / Shift+Q cycle the primary. The saved-group "squad" grid
    /// (DrawRtsSquadGrid) is shelved for the true-RTS mode.
    /// </summary>
    private void DrawRtsSelectionPortraits(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        // Scope header: name the matching saved squad, else a plain selected count (the count is
        // the total even when more units are selected than the grid draws).
        string scope = _freecamSelection.Count == 0 ? "Selection"
            : $"Selection · {_freecamSelection.Count}";
        for (int i = 0; i < _rtsControlGroups.Length; i++)
            if (_rtsControlGroups[i].Count > 0 &&
                SameRtsMembers(_rtsControlGroups[i], _freecamSelection))
            { scope = $"Squad {RtsControlGroupLaw.DisplayNumber(i)} · {_freecamSelection.Count}"; break; }
        GameText.Draw(dl, "GameFontNormalSmall", scope,
            origin + new Vector2(ConsoleSquadsX, 8f) * scale, scale);

        ulong primary = RtsPrimaryGuid;
        var cell = new Vector2(34f, 32f) * scale;
        float barH = 4f * scale;
        float gap = 3f * scale;
        const int cols = 6, maxCells = 18;
        ulong primaryPick = 0, focusPick = 0, dropPick = 0;
        int shown = 0;
        for (int i = 0; i < _freecamSelection.Count && shown < maxCells; i++)
        {
            ulong guid = _freecamSelection[i];
            if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
            Vector2 min = origin + new Vector2(ConsoleSquadsX, 26f) * scale +
                new Vector2(shown % cols * (cell.X + gap), shown / cols * (cell.Y + gap));
            shown++;
            Vector2 max = min + cell;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##sel-portrait-{i}", cell);
            bool hovered = ImGui.IsItemHovered();

            (_, byte classId, _, _) = unit.Fields.Bytes0;
            var bodyMax = new Vector2(max.X, max.Y - barH);
            uint baked = ConsolePortraitHandle(guid);
            if (baked != 0 && !unit.IsDead)
                dl.AddImage((nint)baked, min, bodyMax, new Vector2(0, 1), new Vector2(1, 0));
            else
            {
                dl.AddRectFilled(min, bodyMax, unit.IsDead ? 0xff40444a : ClassChipColor(classId));
                string initial = ResolveUnitName(guid) is { Length: > 0 } name
                    ? name[..1].ToUpperInvariant() : "?";
                Vector2 half = ImGui.CalcTextSize(initial) * 0.5f;
                dl.AddText(new Vector2((min.X + max.X) * 0.5f, (min.Y + bodyMax.Y) * 0.5f) - half,
                    0xe0101418, initial);
            }
            // Class-colour rim on each squad portrait (issue #15); the PRIMARY gets the animated
            // four-dot RTS marker, the rest a static class rim.
            if (guid == primary)
                DrawAnimatedClassPortraitBorder(dl, min, bodyMax, guid, scale);
            else
                DrawClassPortraitBorderRect(dl, min, bodyMax, guid, scale);
            uint maxHp = unit.Fields.MaxHealth;
            float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH), max, 0xff101418);
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH),
                new Vector2(min.X + cell.X * hp, max.Y),
                hp > 0.5f ? 0xff40c040u : hp > 0.2f ? 0xff40c0e0u : 0xff4040d0u);

            // Carved slot: the PRIMARY wears a bevelled gilt inlay; others a stone-bevelled frame
            // that takes a gilt edge on hover.
            bool isPrimary = guid == primary;
            if (isPrimary)
            {
                dl.AddRect(min, max, PainterlyFrameRule, 0, ImDrawFlags.None, MathF.Max(2f, 2f * scale));
                DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyGoldLit, PainterlyGoldShade);
            }
            else
            {
                DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
                dl.AddRect(min, max, hovered ? PainterlyGoldLit : PainterlyFrameOuter,
                    0, ImDrawFlags.None, MathF.Max(1f, scale));
            }

            // Chain chip (owner 2026-09-03): green linked / red unchained / amber world hold,
            // with the anchor's initial beside it. Server truth from the roster.
            if (_suiChain.TryGetValue(guid, out (byte State, ulong Anchor) chain))
            {
                DrawChainGlyph(dl, min + new Vector2(4.5f * scale, 4.5f * scale), 3.5f * scale, chain.State);
                if (chain.Anchor != 0 && ResolveUnitName(chain.Anchor) is { Length: > 0 } anchorName)
                    DrawChainAnchorMedallion(dl, new Vector2(max.X - 4.5f * scale, min.Y + 4.5f * scale),
                        3.5f * scale, anchorName, chain.State, scale);
            }
            if (hovered)
            {
                string chainTip = PartyChainTip(guid);
                HoverTip($"{ResolveUnitName(guid)} — {(int)(hp * 100)}%\n" +
                    (chainTip.Length > 0 ? chainTip + "\n" : "") +
                    "Click: make primary · Double-click: select only this unit · Shift+click: drop");
            }

            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                focusPick = guid;
            }
            else if (ImGui.IsItemClicked())
            {
                if (_rtsUnitCastSpellId != 0)
                    TryCommitRtsUnitCastTarget(guid);
                else if (ImGui.GetIO().KeyShift)
                    dropPick = guid;
                else
                    primaryPick = guid;
            }
        }

        // Mutations after the loop — never mutate the selection mid-iteration.
        if (focusPick != 0)
        {
            _freecamSelection.Clear();
            _freecamSelection.Add(focusPick);
            _rtsPrimaryGuid = focusPick;
        }
        else if (dropPick != 0)
        {
            _freecamSelection.Remove(dropPick);
            if (_rtsPrimaryGuid == dropPick) _rtsPrimaryGuid = 0;   // resolver falls back to [0]
        }
        else if (primaryPick != 0)
            _rtsPrimaryGuid = primaryPick;
    }

    /// <summary>Center the detached camera on a mini-portrait's unit without changing the
    /// current multi-selection, camera facing, pitch, or boom distance.</summary>
    private bool FocusRtsCameraOnUnit(ulong guid)
    {
        if (_controller is null || guid == 0 ||
            !_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead)
            return false;

        _commanderFlySettle = null;
        _controller.Teleport(unit.Position.X, unit.Position.Y, unit.Position.Z);
        _window.Camera.Target = _controller.Position;
        _freecamCamSentAt = 0;
        _rtsWheelRetreatYards = 0f;
        return true;
    }

    /// <summary>
    /// All ten WC3 group slots as a fixed 5×2 grid. A filled well recalls its
    /// squad on click; Ctrl+click on ANY well saves the current selection
    /// there (Shift+1-0 recalls; Ctrl+1-0 saves; plain action keys cast the primary card).
    ///
    /// Shift+click still saves as well. The keyboard chord moved to Ctrl for RTS convention
    /// (2026-08-26), but no other binding competes for a modified click on a well, so the old
    /// gesture keeps working rather than becoming a silent no-op in muscle memory.
    /// </summary>
    private void DrawRtsSquadGrid(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        GameText.Draw(dl, "GameFontNormalSmall", "Squads",
            origin + new Vector2(ConsoleSquadsX, 8f) * scale, scale);
        var cell = new Vector2(25f, 19f) * scale;
        float gap = 2f * scale;
        for (int i = 0; i < _rtsControlGroups.Length; i++)
        {
            Vector2 min = origin + new Vector2(ConsoleSquadsX, 26f) * scale +
                new Vector2(i % 5 * (cell.X + gap), i / 5 * (cell.Y + gap));
            Vector2 max = min + cell;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##squad-well-{i}", cell);
            bool hovered = ImGui.IsItemHovered();
            int count = _rtsControlGroups[i].Count;
            string number = RtsControlGroupLaw.DisplayNumber(i);
            if (count > 0)
            {
                dl.AddRectFilled(min, max, 0xd01a222a);
                string label = $"{number}·{count}";
                Vector2 half = ImGui.CalcTextSize(label) * 0.5f;
                dl.AddText((min + max) * 0.5f - half, 0xffd0b060, label);
            }
            else
            {
                // Empty well: the slot exists (the grid is furniture), dimmed.
                dl.AddRectFilled(min, max, 0x66141a20);
                Vector2 half = ImGui.CalcTextSize(number) * 0.5f;
                dl.AddText((min + max) * 0.5f - half, 0x66aabbcc, number);
            }
            dl.AddRect(min, max, hovered ? 0xffd0b060 : 0xff2a343d,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (hovered)
                HoverTip(count > 0
                    ? $"Squad {number} — {count} member(s)\nClick: select (key {number}) · " +
                      $"Ctrl+click: save current selection here"
                    : $"Squad {number} is empty — Ctrl+click (or Ctrl+{number}) " +
                      "saves the current selection");
            if (ImGui.IsItemClicked())
            {
                if (ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift) AssignRtsControlGroup(i);
                else if (count > 0) RecallRtsControlGroup(i);
                else SetRtsControlGroupStatus($"Group {number} is empty — Ctrl+{number} " +
                    "saves the current selection.");
            }
        }
    }

    /// <summary>The console's center: scope line, then the WC3 info panel —
    /// a portrait unit card for one unit, portrait chips for a group.</summary>
    private void DrawRtsConsoleInfo(ImDrawListPtr dl, Vector2 origin,
        List<ulong> subjects, float scale)
    {
        Vector2 scopePos = origin + new Vector2(ConsoleInfoX, 8f) * scale;
        if (subjects.Count == 0)
        {
            GameText.Draw(dl, "GameFontNormalSmall", "No selection", scopePos, scale);
            dl.AddText(origin + new Vector2(ConsoleInfoX, 30f) * scale, 0xff9aa4ab,
                "Click or drag units in the world,");
            dl.AddText(origin + new Vector2(ConsoleInfoX, 44f) * scale, 0xff9aa4ab,
                "or pick a squad.");
            return;
        }

        // The left portrait grid names the selection now; this panel is the PRIMARY's card.
        // Route readout stays here, right-aligned on the top row: the Patrol draft while one is
        // armed, else the standing chain this selection would patrol.
        string? route = _rtsPatrolAuthoring
            ? $"Drafting route · {_rtsPatrolDraft.Count} pt{(_rtsPatrolDraft.Count == 1 ? "" : "s")}"
            : _rtsWaypointChain.Count > 0 && SameRtsMembers(_rtsWaypointSubjects, subjects)
                ? $"Route · {_rtsWaypointChain.Count} pt{(_rtsWaypointChain.Count == 1 ? "" : "s")}"
                : null;
        if (route is not null)
        {
            float routeW = ImGui.CalcTextSize(route).X;
            dl.AddText(origin + new Vector2(ConsoleCardX - 16f, 9f) * scale -
                new Vector2(routeW, 0f), _rtsPatrolAuthoring ? 0xff60d0f0u : 0xff9aa4abu, route);
        }

        PreparedSharedSpellTooltip? cardTooltip = null;
        Vector2 content = origin + new Vector2(ConsoleInfoX, 16f) * scale;
        ulong primary = RtsPrimaryGuid;
        if (primary != 0 && _entities.TryGet(primary, out WorldEntity cardUnit))
            DrawRtsConsoleUnitCard(primary, cardUnit, content, scale, ref cardTooltip);
        if (cardTooltip is { } preparedCard)
            OfferPreservedSharedGameTooltipRenderer(preparedCard.Owner,
                () => DrawSpellTooltip(preparedCard.Snapshot));
    }

    /// <summary>Portrait, name, level/class, vitals, and the primary's abilities
    /// Clicking an ability casts it: possess-on-cast takes control of
    /// the primary (riding the possess wire) and fires the spell once its bars are live.</summary>
    private void DrawRtsConsoleUnitCard(ulong guid, WorldEntity unit, Vector2 content,
        float scale, ref PreparedSharedSpellTooltip? tooltip)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        (byte race, byte classId, byte gender, byte powerType) = unit.Fields.Bytes0;

        // Portrait: the party frames' own live 3-D bake when it exists (V is
        // flipped — render target, not a BLP), the static stand-in otherwise.
        var portraitSize = new Vector2(44f) * scale;
        uint baked = ConsolePortraitHandle(guid);
        if (baked != 0)
            dl.AddImage((nint)baked, content, content + portraitSize,
                new Vector2(0, 1), new Vector2(1, 0));
        else
        {
            string sex = gender == 1 ? "Female" : "Male";
            string raceName = race == 5 ? "Scourge" : RaceName(race).Replace(" ", "");
            uint standIn = PainterlyArt(
                $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{raceName}");
            if (standIn != 0)
                dl.AddImage((nint)standIn, content, content + portraitSize);
            else
                dl.AddRectFilled(content, content + portraitSize, 0xd01a222a);
        }

        ImGui.SetCursorScreenPos(content);
        ImGui.InvisibleButton($"##primary-camera-{guid}", portraitSize);

        if (ImGui.IsItemClicked())
            FocusRtsCameraOnUnit(guid);

        // Static class-colour frame on the primary selected card (issue #15) — the animated
        // four-dot marker lives on the mini squad portrait instead.
        DrawClassPortraitBorderRect(dl, content, content + portraitSize, guid, scale);
        // Role medallion at the portrait's lower-right corner, fully on the
        // frame — the same disc the party rows wear.
        string unitName = ResolveUnitName(guid);
        DrawRoleMedallion(dl, content + new Vector2(37f, 37f) * scale, 7f * scale,
            LoadBotBars().BotRoles.GetValueOrDefault(unitName, "DPS"), scale);

        Vector2 text = content + new Vector2(52f, 0f) * scale;

        // Who the primary is fighting: selecting an individual surfaces its current target so you
        // keep target awareness without stealing your own focus. On its own line under the vitals.
        if ((unit.Fields.Target ?? unit.CombatTarget) is { } tgtGuid && tgtGuid != 0 &&
            tgtGuid != guid && _entities.TryGet(tgtGuid, out WorldEntity tunit))
        {
            string tname = "▶ " + ResolveWorldUnitName(tgtGuid);
            uint maxT = tunit.Fields.MaxHealth;
            float thp = maxT > 0 ? Math.Clamp(tunit.Fields.Health / (float)maxT, 0f, 1f) : 0f;
            dl.AddText(text + new Vector2(-0f, -5f) * scale,
                tunit.IsDead ? 0xff808890u : 0xff5050e0u,
                maxT > 0 ? $"{tname} ({(int)(thp * 100)}%)" : tname);
        }

        // Name, level · class · state, vitals to the portrait's right.
        Vector2 namePos = text + new Vector2(0f, 8f) * scale;
        GameText.Draw(dl, "GameFontNormalSmall", unitName, namePos, scale);
        string className = ClassIdName(classId);
        string detail = className.Length != 0
            ? $"Lv {unit.Fields.Level} {className}" : $"Lv {unit.Fields.Level}";
        if (RtsEnlisted(guid)) detail += " · enlisted";
        if (_rtsOrderChips.TryGetValue(guid, out string? chipText)) detail += $" · {chipText}";
        dl.AddText(text + new Vector2(0f, 16f) * scale, 0xff9aa4ab, detail);

        Vector2 vmin = text + new Vector2(0f, 32f) * scale;
        float barW = 130f * scale, barH = 5f * scale;
        uint maxHp = unit.Fields.MaxHealth;
        float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
        dl.AddRectFilled(vmin, vmin + new Vector2(barW, barH), 0xff101418);
        dl.AddRectFilled(vmin, vmin + new Vector2(barW * hp, barH), 0xff1db000);
        uint maxPower = unit.Fields.MaxPower(powerType);
        if (maxPower > 0)
        {
            float power = Math.Clamp(unit.Fields.Power(powerType) / (float)maxPower, 0f, 1f);
            Vector2 pmin = vmin + new Vector2(0, barH + 2f * scale);
            dl.AddRectFilled(pmin, pmin + new Vector2(barW, barH), 0xff101418);
            dl.AddRectFilled(pmin, pmin + new Vector2(barW * power, barH), powerType switch
            {
                1 => 0xff0000c0u,   // rage
                3 => 0xff00d1d1u,   // energy
                _ => 0xffde7000u,   // mana
            });
        }

        // The primary's ability row under the portrait — truthful icons; click casts via
        // possess-on-cast (CastPrimaryAbility).
        PlayerActions store = ActionsFor(guid);
        if (store.OccupiedCount == 0) EnsureBotBarForViewing(guid);
        double now = NowSeconds();
        float size = 22f * scale;
        var side = new Vector2(size, size);
        Vector2 rowMin = content + new Vector2(0f, 48f) * scale;
        List<(int Slot, ActionSlot Action, SpellInfo Spell)> abilities =
            RtsPrimaryAbilities(guid);
        for (int drawn = 0; drawn < abilities.Count; drawn++)
        {
            (int slot, ActionSlot action, SpellInfo spell) = abilities[drawn];
            Vector2 min = rowMin + new Vector2(drawn * (size + 3f * scale), 0f);
            Vector2 max = min + side;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##card-{guid}-{slot}", side);
            // Attack's DBC icon is the internal Temp face — the law swaps in
            // the unit's own weapon art (public visible-item entries).
            uint icon = PainterlyArt(ResolveSpellActionIcon(spell, unit));
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            if (store.TryCooldownDisplay(action.ActionId, 0, spell, now,
                    out CooldownDisplay cooldown) && cooldown.SweepFraction is float sweep)
                DrawCooldownSwipe(dl, min, max, sweep);
            DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
            dl.AddRect(min, max, ImGui.IsItemHovered() ? PainterlyFrameRule : PainterlyFrameOuter,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            string binding = BindingText(ActionBinding(drawn));
            if (binding.Length > 0)
                GameText.DrawRightAligned(dl, "NumberFontNormal", binding,
                    max - new Vector2(2f, GameText.EmPixels("NumberFontNormal", scale) + 1f),
                    scale, 0xffffffff);
            if (ImGui.IsItemHovered())
                tooltip = PrepareSharedSpellTooltip(
                    new GameTooltipOwnerKey("console-card", (ulong)slot + 1),
                    spell.Id, scale, SpellTooltipPlacement.DefaultBottomRight);
            if (ImGui.IsItemClicked())
                CastPrimaryAbility(guid, action.ActionId,
                    BindingModifierHeld(GameBinding.RtsCastOnPrimary));
        }
        if (abilities.Count == 0)
            dl.AddText(rowMin + new Vector2(0f, 4f * scale), 0xff9aa4ab,
                "abilities syncing…");
    }

    /// <summary>
    /// Six quick-use slots mirroring the PRIMARY character's first six backpack slots, on the info
    /// region's right — CRPG party-inventory style: Tab to a character, use its own consumables. A
    /// non-own unit's bags come from its SMSG_SUI_SNAPSHOT (party members auto-sync; a faction bot
    /// syncs on possession). Click USES the item via possess-on-use (UsePrimaryQuickSlot).
    /// </summary>
    private void DrawQuickBackpackSlots(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        if (_net is null || _items is null) return;
        ulong primary = RtsPrimaryGuid;
        WorldEntity? primaryEntity =
            primary != 0 && _entities.TryGet(primary, out WorldEntity pe) ? pe : null;
        const float size = 30f;
        const float gap = 3f;
        double now = NowSeconds();
        for (int i = 0; i < 6; i++)
        {
            Vector2 min = origin + new Vector2(
                ConsoleInfoX + i % 6 * (size + gap),
                26f + 2 * (size + gap) + 6f + i / 6 * (size + gap)
                ) * scale;
            Vector2 max = min + new Vector2(size) * scale;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##quickslot-{i}", new Vector2(size) * scale);
            bool hovered = ImGui.IsItemHovered();

            ItemTemplate? tpl = null;
            uint stack = 0;
            if (primaryEntity is { } self)
            {
                ulong g = self.Fields.PlayerBackpackSlot(i);
                if (g != 0 && _entities.TryGet(g, out WorldEntity found))
                {
                    _items.Require(found.Entry, g, _net);
                    _items.TryGet(found.Entry, out tpl);
                    stack = found.Fields.ItemStackCount;
                }
            }

            if (tpl is { } t)
            {
                uint icon = _gameplayArt?.Handle(t.IconPath) ?? 0;
                if (icon != 0) dl.AddImage((nint)icon, min, max);
                if (stack > 1)
                    GameText.DrawRightAligned(dl, "NumberFontNormal", stack.ToString(),
                        new Vector2(max.X - 3f * scale,
                            max.Y - GameText.EmPixels("NumberFontNormal", scale) - 1f * scale), scale);
                if (t.UseSpellId > 0 && ActionsFor(primary).TryCooldownDisplay(t.UseSpellId, t.Entry,
                        t.UseSpellCategory, now, out CooldownDisplay cd) &&
                        cd.SweepFraction is float sweep)
                    DrawCooldownSwipe(dl, min, max, sweep);
                if (hovered)
                    HoverTip(t.InventoryType != 0
                        ? $"{t.Name}\n(equip from the Bags panel — quick slots only use consumables)"
                        : $"{t.Name}\nClick to use");
                if (ImGui.IsItemClicked()) UsePrimaryQuickSlot(i);
            }
            else
            {
                dl.AddRectFilled(min, max, 0x66141a20);   // empty recess
                if (hovered && primaryEntity is not null && primary != LocalPlayerGuid)
                    HoverTip("Empty — or this companion's bags aren't synced yet (possess to sync).");
            }

            DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
            dl.AddRect(min, max, hovered ? PainterlyFrameRule : PainterlyFrameOuter,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
        }
    }

    // A quick-slot use queued while control of the primary is being handed over (possess-on-use).
    private ulong _pendingUsePrimary;
    private int _pendingUseSlot = -1;
    private double _pendingUseAt;

    /// <summary>
    /// Use a quick backpack slot from the PRIMARY's bags — the item twin of CastPrimaryAbility. The
    /// own character uses directly (you are its self-mover, and you cannot possess yourself). For a
    /// bot primary it rides the possess wire: take control, then fire the use once its bags are live
    /// (TryFirePendingPrimaryUse). The server resolves CMSG_USE_ITEM against the possessed unit
    /// (GetSuiActor), so this pops the PRIMARY's item, not yours.
    /// </summary>
    private void UsePrimaryQuickSlot(int slot)
    {
        if (TacticalFreezeBlocksLiveCommands)
        {
            CancelPendingPrimaryItemUse();
            if (OwnedActiveTacticalLock is not null)
                ShowUiError("Items cannot be queued during Tactical Freeze.");
            else
                RefuseTacticalFreezeLiveCommand("using items");
            return;
        }
        ulong primary = RtsPrimaryGuid;
        if (primary == 0) return;
        if (RefuseTacticalFrozenActor(primary, "use its items"))
        {
            CancelPendingPrimaryItemUse();
            return;
        }
        // Already on the subject (a driven bot, or your own char) → use now. As with casting, the
        // self path needs ControlledGuid == you, or the guid-less use would pop the possessed bot's
        // item; otherwise fall through to a release-home first.
        if (ControlledGuid == primary && (CanAuthorControlledGameplay || primary == LocalPlayerGuid))
        {
            SendPrimaryItemUse(primary, slot);
            return;
        }
        _pendingUsePrimary = primary;
        _pendingUseSlot = slot;
        _pendingUseAt = NowSeconds();
        BeginControlHandover(primary);
    }

    /// <summary>Fire a possess-on-use quick slot once control has landed on the primary bot. Runs
    /// every frame from UpdateControlInput; times out so a denied/slow possession leaves nothing
    /// armed.</summary>
    private void TryFirePendingPrimaryUse()
    {
        if (_pendingUseSlot < 0) return;
        // Queue v1 has no item action. If an authoritative lock arrived while possession was in
        // flight, disarm the old live-world use rather than letting it fire into frozen time.
        if (TacticalFreezeBlocksLiveCommands)
        {
            CancelPendingPrimaryItemUse();
            return;
        }
        if (NowSeconds() - _pendingUseAt > 2.5)
        {
            CancelPendingPrimaryItemUse();
            return;
        }
        // Wait until control reaches the subject (a possessed bot, or your own body after a
        // release-home). Own char authors without possession.
        if (ControlledGuid != _pendingUsePrimary) return;
        if (!CanAuthorControlledGameplay && _pendingUsePrimary != LocalPlayerGuid) return;
        // Only clear once the item actually RESOLVES — the possessed bot's inventory snapshot can
        // land a few frames after control does, and firing before it arrived silently dropped the
        // use (the "cast a spell first" symptom). Keep retrying until it resolves or times out.
        if (SendPrimaryItemUse(ControlledGuid, _pendingUseSlot))
            CancelPendingPrimaryItemUse();
    }

    private void CancelPendingPrimaryItemUse()
    {
        _pendingUseSlot = -1;
        _pendingUsePrimary = 0;
        _pendingUseAt = 0;
    }

    /// <summary>
    /// Send CMSG_USE_ITEM for a unit's backpack slot (bag 255 / slot 23+N). The server resolves it
    /// against the possessed unit (GetSuiActor), or the own character when not possessing.
    /// Consumables only; mirrors SendItemUse's cooldown gate on the unit's own store.
    /// </summary>
    /// <returns>true once the slot's item RESOLVED (whether it was used, refused, or on cooldown);
    /// false only when the bags haven't synced yet, so the possess-on-use retry keeps waiting.</returns>
    private bool SendPrimaryItemUse(ulong unit, int slot)
    {
        if (_net is null || _items is null || !_entities.TryGet(unit, out WorldEntity u)) return true;
        ulong g = u.Fields.PlayerBackpackSlot(slot);
        if (g == 0 || !_entities.TryGet(g, out WorldEntity instance) ||
            !_items.TryGet(instance.Entry, out ItemTemplate? tplN) || tplN is not { } tpl)
            return false;   // bags not synced yet — let the caller retry until they are
        if (tpl.InventoryType != 0)
        {
            ShowUiError("Quick slots use consumables — equip gear from the Bags panel.");
            return true;
        }
        ItemSpellTemplate useSpell = tpl.Spells[tpl.UseSpellIndex];
        if (useSpell.SpellId == 0) return true;
        double now = NowSeconds();
        PlayerActions store = ActionsFor(unit);
        SpellInfo? spell = _spellCatalog?.TryGet(useSpell.SpellId, out SpellInfo resolved) == true
            ? resolved : null;
        bool blocked = spell is { } info
            ? store.IsOnCooldown(useSpell.SpellId, tpl.Entry, info, now)
            : store.IsOnCooldown(useSpell.SpellId, tpl.Entry, useSpell.Category, now);
        // Same trace as SendItemUse's gate (GameLoop.Inventory.cs, path=useitem). This is the
        // one item-use gate that is genuinely separate: bag clicks, action-bar presses and
        // hotkeys all share SendItemUse, while the shelf has its own copy AND runs against a
        // per-unit store (ActionsFor) rather than the own-player one - so a block here and a
        // block there are not necessarily the same records.
        Console.WriteLine($"[verdict:item-cooldown] time={NowSeconds():F3} path=shelf " +
            $"unit=0x{unit:X16} entry={tpl.Entry} spell={useSpell.SpellId} " +
            $"name={spell?.Name ?? "?"} category={useSpell.Category} " +
            $"itemCooldownMs={useSpell.CooldownMs} itemCategoryCooldownMs={useSpell.CategoryCooldownMs} " +
            $"dbcRecoveryMs={spell?.RecoveryMs ?? 0} dbcCategoryRecoveryMs={spell?.CategoryRecoveryMs ?? 0} " +
            $"blocked={blocked}");
        if (blocked)
        {
            ShowSpellError(useSpell.SpellId, "LOCAL_ITEM_COOLDOWN", "Item is not ready yet.", "LOCAL_GATE");
            return true;
        }
        if (!_net.UseItem(255, (byte)(23 + slot), tpl.UseSpellIndex)) return true;
        ScheduleControlledInventoryRefresh(unit);   // re-sync a possessed bot's consumed item
        store.StartItemUseCooldown(instance.Entry, useSpell, spell, now);
        if (spell is { } committed) store.StartGlobalCooldown(useSpell.SpellId, committed, now);
        return true;
    }

    /// <summary>
    /// The WC3 command card: an icon grid of the console verbs. Same orders,
    /// voices, and chat lines as the old text shelf — only the dress changed
    /// (owner design language: command-card styling from WC3, art from the
    /// vanilla pet-bar idiom).
    /// </summary>
    private void DrawRtsCommandCard(ImDrawListPtr dl, Vector2 origin,
        List<ulong> subjects, float scale)
    {
        if (_net is null) return;

        bool any = subjects.Count > 0;

        bool hostileTargeted = _selectionGuid != 0 &&
            _entities.TryGet(_selectionGuid, out WorldEntity shelfTarget) &&
            !shelfTarget.IsDead && CanAttack(shelfTarget);

        bool routeReady = _rtsWaypointChain.Count > 0 &&
            SameRtsMembers(_rtsWaypointSubjects, subjects);
        TacticalLockView? ownedFreeze = OwnedActiveTacticalLock;
        TacticalLockView? localFreeze = LocalActiveTacticalLock;
        bool tacticalActive = ownedFreeze is not null;
        bool frozenReadOnly = localFreeze is not null && ownedFreeze is null;
        bool liveCommandsBlocked = TacticalFreezeBlocksLiveCommands ||
            TacticalOrderActorsFrozen(subjects);


        bool SendImmediateOrder(byte orderType, ulong target = 0,
            float x = 0, float y = 0, float z = 0)
        {
            ClearRtsAttackQueue();
            return TrySendLiveSuiOrder(orderType, subjects, target, x, y, z);
        }


        // NEW CLEAN GRID SETUP
        int cellIndex = 0;

        Vector2 side = new Vector2(35f) * scale;
        int columns = 3;
        int rows = 3;
        float gap = 6f * scale;

        float gridWidth = side.X * columns + gap * (columns - 1);
        float gridHeight = side.Y * rows + gap * (rows - 1);

        float sectionWidth = (ConsoleWidth - ConsoleCardX) * scale;

        float gridX = (sectionWidth - gridWidth) * 0.5f;
        float gridY = (ConsoleHeight * scale - gridHeight) * 0.5f;


        bool CardButton(string id, string icon, string tooltip,
            bool enabled, bool lit = false)
        {
            Vector2 min = origin +
                new Vector2(ConsoleCardX - 8f, 0f) * scale +
                new Vector2(
                    gridX + (cellIndex % columns) * (side.X + gap),
                    gridY + (cellIndex / columns) * (side.Y + gap));


            // The grid is authored in RtsOrderBindings order, so the CELL INDEX is the command.
            // Each card's binding (RTS Controls: Order: ...) fires exactly what the button under
            // it fires, under the same enable rule - a hotkey can never send an order the card
            // itself refuses, and one pressed against a disabled card is dropped, not banked.
            bool hotkeyFired =
                (uint)cellIndex < (uint)RtsOrderBindings.Length &&
                ConsumeRtsOrderHotkey(RtsOrderBindings[cellIndex], enabled);

            cellIndex++;

            Vector2 max = min + side;

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton(id, side);

            bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

            uint art = PainterlyArt(icon);
            if (art != 0)
                dl.AddImage((nint)art, min, max);

            if (!enabled)
                dl.AddRectFilled(min, max, 0xaa10141c);

            DrawBevel(dl, min, max,
                MathF.Max(1f, scale),
                PainterlyStoneTop,
                PainterlyFrameOuter);

            dl.AddRect(min, max,
                lit || (hovered && enabled)
                    ? PainterlyFrameRule
                    : PainterlyFrameOuter,
                0,
                ImDrawFlags.None,
                MathF.Max(1f, scale));

            if (hovered)
                HoverTip(tooltip);

            return hotkeyFired || (enabled && ImGui.IsItemClicked());
        }

        if (CardButton("##card-focus", ConsoleIconFocus, hostileTargeted && any
                ? tacticalActive
                    ? "Focus: queue an attack for each commandable selected member"
                    : "Focus: send the selection at your current target"
                : "Focus needs a selection and a hostile target\n(click one in the world first)",
                any && hostileTargeted && (tacticalActive || !liveCommandsBlocked)))
        {
            if (tacticalActive)
                TryQueueTacticalAttack(_selectionGuid);
            else if (SendImmediateOrder(1, _selectionGuid))
            {
                NoteCompanionOrder(1, subjects);
                AddChatMessage($"{OrderSubjectLabel(subjects)}: attack {ResolveWorldUnitName(_selectionGuid)}!");
            }
        }
        if (CardButton("##card-regroup", ConsoleIconRegroup,
                tacticalActive
                    ? "Regroup: queue movement to the body you drive"
                    : "Regroup: abandon the tactical route and\nescort the body you drive",
                any && (tacticalActive || !liveCommandsBlocked)))
        {
            if (tacticalActive)
            {
                if (_entities.TryGet(ControlledGuid, out WorldEntity regroupBody))
                    TryQueueTacticalMove(regroupBody.Position);
            }
            else if (SendImmediateOrder(5, ControlledGuid))
            {
                NoteCompanionOrder(5, subjects);
                AddChatMessage($"{OrderSubjectLabel(subjects)}: regroup on {ResolveUnitName(ControlledGuid)}.");
            }
        }
        if (CardButton("##card-hold", ConsoleIconHold,
                liveCommandsBlocked
                    ? "Hold is unavailable while Tactical Freeze owns the command queue"
                    : "Hold: stop and hold this spot", any && !liveCommandsBlocked) &&
            SendImmediateOrder(2))
        {
            NoteCompanionOrder(2, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: stand your ground.");
        }
        // Patrol is a MODE (owner 2026-08-25): first click arms the draft,
        // right-clicks chain cold waypoints, the second click engages the
        // loop; Escape cancels. A Shift+RightClick route that already exists
        // for this selection still engages directly, as before.
        string patrolTip = _rtsPatrolAuthoring
            ? $"Patrol (armed): right-click ground to chain waypoints — " +
              $"{_rtsPatrolDraft.Count} so far.\nClick again to engage the loop; Escape cancels."
            : routeReady
                ? "Patrol: loop the authored waypoint route"
                : "Patrol: click, then right-click ground points to chain\n" +
                  "a route, then click Patrol again to engage the loop.";
        if (CardButton("##card-patrol", ConsoleIconPatrol,
                liveCommandsBlocked
                    ? "Patrol is unavailable while Tactical Freeze owns the command queue"
                    : patrolTip,
                (any || _rtsPatrolAuthoring) && !liveCommandsBlocked,
                _rtsPatrolAuthoring))
        {
            ClearRtsAttackQueue();
            if (_rtsPatrolAuthoring)
                EngageRtsPatrolDraft();
            else if (routeReady)
                foreach (ulong patrolGuid in subjects)
                    if (_entities.TryGet(patrolGuid, out WorldEntity unit) && !unit.IsDead)
                    {
                        if (SendImmediateOrder(4, 0,
                                unit.Position.X, unit.Position.Y, unit.Position.Z))
                        {
                            NoteCompanionOrder(4, subjects);
                            AddChatMessage($"{OrderSubjectLabel(subjects)}: patrol the route.");
                        }
                        break;
                    }
                    else
                        BeginRtsPatrolAuthoring(subjects);
        }
        if (CardButton("##card-line", ConsoleIconLine,
                "Line: standing army — ranks of five facing you,\nformed where the squad stands",
                any && !liveCommandsBlocked) &&
            SendImmediateOrder(SuiOrderFormationLine))
        {
            NoteCompanionOrder(SuiOrderFormationLine, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: form ranks!");
        }
        if (CardButton("##card-circle", ConsoleIconCircle,
                "Circle: evenly spaced ring, everyone facing outward",
                any && !liveCommandsBlocked) &&
            SendImmediateOrder(SuiOrderFormationCircle))
        {
            NoteCompanionOrder(SuiOrderFormationCircle, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: form a circle!");
        }
        if (CardButton("##card-sheathe", _rtsWeaponsSheathed ? ConsoleIconDraw : ConsoleIconSheathe,
                (_rtsWeaponsSheathed ? "Draw: weapons out" : "Sheathe: weapons away") +
                " — parade discipline.\nEntering combat always draws steel.",
                any && !liveCommandsBlocked))
        {
            bool draw = _rtsWeaponsSheathed;
            if (SendImmediateOrder(SuiOrderSheath, 0, draw ? 1f : 0f))
            {
                _rtsWeaponsSheathed = !draw;
                NoteCompanionOrder(SuiOrderSheath, subjects);
                AddChatMessage($"{OrderSubjectLabel(subjects)}: " +
                    (draw ? "weapons out!" : "weapons away."));
            }
        }

        // The deliberately empty eighth cell is the explicit lock toggle. Command View remains
        // real-time until this card is pressed; an overlapping non-owner sees it lit/read-only.
        bool freezeLit = localFreeze is not null;
        bool canToggleFreeze = _tacticalFreezeAvailable && _tacticalFreezePendingRequest == 0 &&
            (ownedFreeze is not null || (!TacticalFreezePoseLaw.IsFrozen(LocalPlayerGuid) &&
                !ControlledBodyTacticallyFrozen));
        string freezeTip = !_tacticalFreezeAvailable
            ? "Tactical Freeze requires a newer SuperUI-Core"
            : _tacticalFreezePendingRequest != 0
                ? "Waiting for the server…"
                : ownedFreeze is not null
                    ? "Resume: release your radius lock and execute the queued plans"
                    : frozenReadOnly
                        ? $"Frozen by {ResolveTacticalOwner(localFreeze!.OwnerGuid)} — only its owner can Resume"
                        : "Freeze: lock units in the radius, then queue up to five actions per member";
        if (CardButton("##card-tactical-freeze",
                ownedFreeze is not null ? ConsoleIconResume : ConsoleIconFreeze,
                freezeTip, canToggleFreeze, freezeLit))
            RequestTacticalFreezeToggle();
    }

    private static uint ClassChipColor(byte classId) => classId switch
    {
        1 => 0xff6e9cc7,   // warrior tan
        2 => 0xffba8cf5,   // paladin pink
        3 => 0xff73d4ab,   // hunter green
        4 => 0xff69f5ff,   // rogue yellow
        5 => 0xffffffff,   // priest white
        7 => 0xffde7000,   // shaman blue
        8 => 0xfff0cc69,   // mage light blue
        9 => 0xffc98294,   // warlock purple
        11 => 0xff0a7dff,  // druid orange
        _ => 0xff9aa4ab,
    };

    /// <summary>
    /// The WC3 multi-unit panel: one chip per selected companion — its baked
    /// portrait when the party frames have one (class color otherwise), name
    /// initial, live health bar. Click takes the chip as the sole selection;
    /// Shift+click drops it from the set.
    /// </summary>
    private void DrawRtsSelectionChips(float scale)
    {
        const int maxChips = 16;
        var chipSize = new Vector2(18f, 24f) * scale;
        int shown = 0;
        ulong soloPick = 0, dropPick = 0;
        for (int i = 0; i < _freecamSelection.Count && shown < maxChips; i++)
        {
            ulong guid = _freecamSelection[i];
            if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
            shown++;
            if (shown > 1) ImGui.SameLine(0f, 3f * scale);
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + chipSize;
            ImGui.InvisibleButton($"##sel-chip-{i}", chipSize);
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            (_, byte classId, _, _) = unit.Fields.Bytes0;
            float barH = 3f * scale;
            var bodyMax = new Vector2(max.X, max.Y - barH);
            uint baked = PartyPortraitHandle(guid);
            if (baked != 0 && !unit.IsDead)
                // Live bake, V flipped (render target, not a BLP).
                dl.AddImage((nint)baked, min, bodyMax, new Vector2(0, 1), new Vector2(1, 0));
            else
            {
                dl.AddRectFilled(min, bodyMax, unit.IsDead ? 0xff40444a : ClassChipColor(classId));
                string initial = ResolveUnitName(guid) is { Length: > 0 } name
                    ? name[..1].ToUpperInvariant() : "?";
                Vector2 half = ImGui.CalcTextSize(initial) * 0.5f;
                Vector2 center = new((min.X + max.X) * 0.5f, (min.Y + max.Y - barH) * 0.5f);
                dl.AddText(center - half, 0xe0101418, initial);
            }
            uint maxHp = unit.Fields.MaxHealth;
            float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH), max, 0xff101418);
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH),
                new Vector2(min.X + chipSize.X * hp, max.Y),
                hp > 0.5f ? 0xff40c040u : hp > 0.2f ? 0xff40c0e0u : 0xff4040d0u);
            dl.AddRect(min, max, ImGui.IsItemHovered() ? 0xffd0b060 : 0xff2a343d,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (ImGui.IsItemHovered())
                HoverTip($"{ResolveUnitName(guid)} — {(int)(hp * 100)}%\n" +
                    "Click: select only this one · Shift+click: drop from selection");
            if (ImGui.IsItemClicked())
            {
                if (ImGui.GetIO().KeyShift) dropPick = guid;
                else soloPick = guid;
            }
        }
        if (_freecamSelection.Count > maxChips)
        {
            ImGui.SameLine(0f, 3f * scale);
            ImGui.TextDisabled($"+{_freecamSelection.Count - maxChips}");
        }
        // Mutations after the loop — never mutate the list mid-iteration.
        if (soloPick != 0)
        {
            _freecamSelection.Clear();
            _freecamSelection.Add(soloPick);
            PlayCompanionSelectionVoice(soloPick);
        }
        else if (dropPick != 0)
            _freecamSelection.Remove(dropPick);
    }
}
