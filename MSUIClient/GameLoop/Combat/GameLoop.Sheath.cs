using System.Numerics;
using MSUIClient.Net;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _sheathKeyWasDown;
    private byte _visualSheathState;
    private byte _lastServerSheathState = byte.MaxValue;
    private byte? _pendingCeremonialSheathState;
    // The state we last volunteered through CMSG_SETSHEATHED, held until the server echoes it
    // back. While it is set the level-triggered adopt below stands down, so an optimistic local
    // draw is not reverted — and not re-sent — on every frame of the round trip.
    private byte? _volunteeredSheathState;
    // Tactical Freeze holds the attachment mirror and any in-flight ceremony at its sampled
    // frame. On thaw the ordinary authoritative adoption gets one forced comparison without
    // cancelling a still-valid ceremony whose server echo arrived while the body was held.
    private bool _sheathTacticalFreezeHeld;
    // False until the mirror has taken the server's byte once for this body. Guards the resync
    // adoption from sounding; every later transition, whoever caused it, is audible.
    private bool _sheathSoundSynced;
    private SheatheSoundCatalog? _sheatheSounds;
    private bool _sheatheSoundsLoaded;

    private void UpdateSheathInput(bool typing)
    {
        if (_net is null || !_net.IsInWorld || _character is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        if (ControlledBodyTacticallyFrozen)
        {
            _sheathTacticalFreezeHeld = true;
            // Consume the physical edge while held. A Z press made during the lock must not
            // become a delayed draw/sheath command merely because the key is still down at thaw.
            _sheathKeyWasDown = BindingBaseDown(GameBinding.Sheath);
            return;
        }
        if (_sheathTacticalFreezeHeld)
        {
            _sheathTacticalFreezeHeld = false;
            // Fields may have advanced while the visual mirror was intentionally held. Force
            // one silent authoritative comparison now; matching ceremonies continue from their
            // sampled frame, while a conflicting server byte follows the normal cancellation path.
            _lastServerSheathState = byte.MaxValue;
            _sheathSoundSynced = false;
        }

        byte serverState = player.Fields.SheathState;
        byte adopted = serverState <= 2 ? serverState : (byte)0;
        bool liveAuthorshipBlocked = TacticalFreezeBlocksLiveCommands;
        // Decided once per pass, not inside the writes: the mirror can already agree with the
        // server on the resync frame, and if the flag only flipped when a write happened to
        // occur, the first REAL server-driven change afterwards would be swallowed as a resync.
        bool resync = !_sheathSoundSynced;
        _sheathSoundSynced = true;
        // Combat outranks the server's byte. Without this the two rules fought each frame while
        // parked or flying — the adopt below took the server's stow, the block further down
        // forced the draw straight back, and with the cue attached that is two sounds a frame.
        bool combatForcesDrawn = !_freeView && player.Engaged;
        if (serverState != _lastServerSheathState)
        {
            _lastServerSheathState = serverState;
            // Our volunteer echo may arrive before the hand reaches the weapon. Keep the old
            // placement pinned until the authored event; a different inbound state is external
            // authority and cancels the local ceremony immediately.
            if (_pendingCeremonialSheathState is not byte pending || adopted != pending)
            {
                _pendingCeremonialSheathState = null;
                _character.CancelSheathCeremony();
                if (!combatForcesDrawn) SetSheathVisualState(adopted, audible: !resync);
            }
        }
        if (_volunteeredSheathState == adopted) _volunteeredSheathState = null;

        // Level, not edge. The block above only fires when the server's byte CHANGES, so once
        // the mirror had drifted — the engaged draw below, a ceremony that degraded, a free-view
        // round trip — nothing ever pulled it back, and the body stayed with weapons drawn while
        // the server had them stowed. Held off while one of our own volunteers is in flight and
        // while a ceremony owns the placement, so neither is undone mid-swing.
        if (!combatForcesDrawn && _volunteeredSheathState is null &&
            _pendingCeremonialSheathState is null &&
            !_character.SheathCeremonyActive && _visualSheathState != adopted)
            SetSheathVisualState(adopted, audible: !resync);

        if (_pendingCeremonialSheathState is byte ceremonialState &&
            _character.ConsumeSheathSwap())
        {
            _pendingCeremonialSheathState = null;
            SetSheathVisualState(ceremonialState, audible: true);
        }

        // Melee engagement is an instant draw in the reference client. Keep it
        // optimistic so the hand mount moves in the attack-start frame, then
        // volunteer the pose for other clients through UNIT_FIELD_BYTES_2.
        bool controllerOwnsBody = ControllerOwnsControlledBodyPose;
        // !_freeView rather than controllerOwnsBody: the latter is also false while parked or
        // flying, and the body is still drawn from _character in both (Program.cs gates that
        // draw on _freeView alone), so a controllerOwnsBody gate would leave a flying character
        // sheathed through a fight. Only in the free view is the rig a camera rather than a
        // body, and there the mirror is left alone so it can re-adopt the server's byte.
        // The ranged pose is held only while a ranged action is live: combat does not overrule
        // Auto Shot or a shot mid-cast. Without that hold the ranged pose survived a single frame
        // before being forced back to melee — and with the cue attached, starting Auto Shot in
        // melee range sounded a spurious sword draw every time. Once the shot is done (a
        // one-shot Shoot Bow/Throw pull, or Auto Shot cancelled by a melee ability), melee
        // engagement draws the melee weapons (owner 2026-09-22: the bow stayed in hand for
        // the whole fight after a ranged pull).
        bool rangedPoseHeld = _visualSheathState == 2 && RangedActionLive();
        if (!liveAuthorshipBlocked && combatForcesDrawn && !rangedPoseHeld &&
            (_visualSheathState != 1 ||
            _pendingCeremonialSheathState is not null))
        {
            _pendingCeremonialSheathState = null;
            _character.CancelSheathCeremony();
            SetSheathVisualState(1, audible: true);
            if (controllerOwnsBody)
            {
                _net.SetSheathed(1);
                _volunteeredSheathState = 1;
            }
        }


        // BindingDown applies the global exact-chord-first, one-fallback dispatch law. Track the
        // base key separately so releasing a modifier before the base cannot manufacture an edge.
        bool physicalDown = BindingBaseDown(GameBinding.Sheath);
        bool acceptedDown = BindingDown(GameBinding.Sheath);
        if (!liveAuthorshipBlocked && controllerOwnsBody && acceptedDown && !_sheathKeyWasDown &&
            !typing && !player.Engaged && !_character.SheathCeremonyActive)
        {
            byte next = _visualSheathState == 0 ? (byte)1 : (byte)0;
            _net.SetSheathed(next);
            _volunteeredSheathState = next;
            if (_character.BeginSheathCeremony())
            {
                // The attachment remains at its old location while the live gait continues
                // underneath the arm overlays. ConsumeSheathSwap moves it at $SHL/$SHR.
                _pendingCeremonialSheathState = next;
            }
            else
            {
                // No equipped hand, clip, or arm mask: the pose snaps instead of playing the
                // draw. The cue still fires — the reference sounds the transition, not the
                // animation, and gating it on the ceremony is what made most bodies silent.
                SetSheathVisualState(next, audible: true);
            }
        }
        // Track the physical binding, not the accepted bare-key chord. Releasing Alt before Z
        // after TOGGLEUI must not manufacture a new bare-Z edge and unexpectedly sheath.
        _sheathKeyWasDown = physicalDown;
        _character.SheathState = _visualSheathState;
    }

    /// <summary>
    /// Move the mirror and sound the transition exactly once.
    ///
    /// The cue used to hang off the $SHL/$SHR ceremony event alone, so a body without complete
    /// hand-to-shoulder arm masks — which the dumps show is common — degraded to a silent snap,
    /// and the combat draw and the ranged/casting poses never made a sound at all. Every path
    /// that moves the pose comes through here instead.
    ///
    /// <paramref name="audible"/> is false only for the first adoption of the server's byte
    /// after a body hand-off: that one is a resync, not a transition, and sounding it would
    /// clack on every login, possession and free-view toggle.
    /// </summary>
    private void SetSheathVisualState(byte state, bool audible)
    {
        byte previous = _visualSheathState;
        _visualSheathState = state;
        if (audible && previous != state) PlaySheatheSounds(previous, state);
    }

    /// <summary>
    /// Drop the local mirror when the body changes hands — free view on or off, possession
    /// granted or released — so the next frame re-adopts the server's byte instead of carrying
    /// a stale prediction across the transition. Deliberately does NOT force state 0: that would
    /// stow the weapons for a frame on every hand-off.
    /// </summary>
    private void ResetSheathMirror()
    {
        _lastServerSheathState = byte.MaxValue;
        _pendingCeremonialSheathState = null;
        _volunteeredSheathState = null;
        _sheathTacticalFreezeHeld = false;
        _sheathSoundSynced = false;
        _character?.CancelSheathCeremony();
    }

    /// <summary>
    /// SheatheSoundLookups.dbc keys the cue on the item's MATERIAL, not its subclass: every
    /// weapon subclass resolves to the same pair within a material, and the audible difference
    /// is metal (SheathMetal/UnsheathMetal) against wood (SheathWood/UnSheathWood), with shields
    /// carrying their own pair on a don't-care material.
    ///
    /// Not behind AudioFeaturePolicy. That quarantine exists for renderer-event emitters that
    /// can burst dozens of voices at once; this is at most two voices on a pose change the
    /// player asked for, the same shape as the interface cues that were never quarantined.
    /// </summary>
    private void PlaySheatheSounds(byte previousState, byte destinationState)
    {
        if (_spellSounds is null || _character is null || _controller is null) return;
        if (!_sheatheSoundsLoaded)
        {
            _sheatheSoundsLoaded = true;
            if (_mpq is not null) _sheatheSounds = SheatheSoundCatalog.Load(_mpq);
        }
        if (_sheatheSounds is null) return;

        // State 2 is the ranged pose: drawn, not stowed. Testing == 1 played the STOW cue every
        // time a bow came up. The ranged weapon also lives in its own slot, and EITHER end of
        // the transition being state 2 makes it the ranged one — putting a bow away moves from
        // 2 to 0, so keying the slot off the destination alone sounded the melee hands for it.
        bool drawing = destinationState != 0;
        bool ranged = destinationState == 2 || previousState == 2;
        Vector3 listener = _controller.Position;
        foreach (int equipmentSlot in ranged ? new[] { 17 } : new[] { 15, 16 })
        {
            CharacterEquipment.Piece? item = _character.Equipment.Pieces
                .FirstOrDefault(piece => piece.EquipmentSlot == equipmentSlot);
            if (item is null || !_sheatheSounds.TryGet(
                    item.ItemClass, item.ItemSubclass, item.Material, out SheatheSoundPair pair))
                continue;
            uint kit = drawing ? pair.Unsheathe : pair.Sheathe;
            _spellSounds.Play(kit, ControlledGuid, listener, listener,
                forceLoop: false, trackHold: false, category: "sfx");
        }
    }

    /// <summary>An auto-repeat is running, or a ranged-weapon cast is still in flight.</summary>
    private bool RangedActionLive() =>
        _autoRepeatSpell != 0 ||
        (_pendingCastSpell != 0 && _spellCatalog is not null &&
         _spellCatalog.TryGet(_pendingCastSpell, out SpellInfo pending) && pending.Ranged);

    private void SetVisualSheath(byte state, bool volunteer = true)
    {
        if (state > 2) return;
        // Late combat/cast packets are presentation events, not permission to change the sampled
        // body pose. UpdateSheathInput re-adopts the server byte on the first frame after thaw.
        if (ControlledBodyTacticallyFrozen) return;
        bool ceremonyInFlight = _pendingCeremonialSheathState is not null ||
            _character?.SheathCeremonyActive == true;
        _pendingCeremonialSheathState = null;
        _character?.CancelSheathCeremony();
        if (_visualSheathState == state && !ceremonyInFlight) return;
        SetSheathVisualState(state, audible: true);
        if (volunteer && !TacticalFreezeBlocksLiveCommands && ControllerOwnsControlledBodyPose)
        {
            _net?.SetSheathed(state);
            _volunteeredSheathState = state;
        }
        if (_character is not null) _character.SheathState = state;
    }
}
