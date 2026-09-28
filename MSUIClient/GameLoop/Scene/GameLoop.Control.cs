using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// CRPG/RTS control model. The session always belongs to the logged-in character
    /// (<see cref="LocalPlayerGuid"/>); <see cref="ControlledGuid"/> is the unit the player is
    /// actually driving — their own character, or a possessed party bot granted by the server
    /// (SMSG_SUI_CONTROL_ACK). All gameplay UI (bars, bags, talents, unit frames, cast feedback)
    /// keys off ControlledGuid; wire identity, session services (mail, quests, trainer), and
    /// telemetry stay on the session character.
    /// </summary>
    internal enum ControlState : byte
    {
        /// <summary>Driving the logged-in character (default).</summary>
        OwnChar,
        /// <summary>CMSG_SUI_CONTROL_REQUEST sent; input parked until the ACK resolves.</summary>
        PossessPending,
        /// <summary>Server granted control of <see cref="_controlTargetGuid"/>.</summary>
        Possessing,
        /// <summary>CMSG_SUI_CONTROL_RELEASE sent; input parked until the ACK resolves.</summary>
        ReleasePending,
        /// <summary>Detached fly camera; nobody is driven, the whole party runs on AI.</summary>
        FreeCam,
    }

    private enum RtsForceTakeControlPhase : byte
    {
        None,
        AwaitingStream,
        AwaitingRelocationAck,
        AwaitingFinalAck,
    }

    private ControlState _controlState = ControlState.OwnChar;
    private ulong _controlTargetGuid;
    private ulong _rtsForceTakeControlGuid;
    private uint _rtsForceTakeControlMap;
    private RtsForceTakeControlPhase _rtsForceTakeControlPhase;
    private double _rtsForceTakeControlAt;
    private const double RtsForceTakeControlTimeoutSeconds = 120.0;


    // RTS Control Guide Help State
    private bool _showControlGuide = true;      // Current visibility
    private bool _enableControlGuide = true;    // Whether it exists at all

    /// <summary>
    /// The RTS camera is up: detached fly rig, marquee selection, order clicks, ground FX.
    ///
    /// DELIBERATELY INDEPENDENT of <see cref="_controlState"/>. Clicking an eligible toon from
    /// the sky takes control of it — its bars, bags and spells become the live HUD — but the
    /// camera STAYS in the sky, because possession is a control decision and the Command View is
    /// a camera decision. Ctrl+F is the only thing that puts the camera down. (Before this,
    /// <see cref="ControlState.FreeCam"/> conflated the two and every possess dropped you
    /// out of the Command View.)
    /// </summary>
    private bool _freeView;

    /// <summary>
    /// Raise or lower the Command View, TELLING THE SERVER about the transition.
    ///
    /// The server has to know, because the Command View is not just a client camera to it: while
    /// it is up the server keeps a streaming eye under the camera, and treats a possessed bot
    /// as commanded-remotely — which deliberately runs that bot's OWN AI, on the reasoning
    /// that the client's movement stream is parked and nothing else would move it.
    ///
    /// Landing without saying so leaves both switches on. The bot's AI then keeps driving it
    /// while you believe you are: your movement is local-only prediction, your swings never
    /// land, nobody follows you, and the moment control is released the server's position wins
    /// and you snap back to the group. Every transition goes through here for that reason.
    /// </summary>
    private void SetFreeView(bool up)
    {
        if (_freeView == up) return;
        _freeView = up;
        _tacticalCommandViewExitAuthorized = false;
        ResetSheathMirror();
        RefreshLootKneel();
        _freeViewExitRequested = false;
        // A vanilla world map left open would hijack the whole HUD via its
        // fullscreen early-return; the Command View has its own map (M → commander).
        if (up) _worldMapOpen = false;
        // Force the next heartbeat: on the way up it rebuilds the eye the possess tore down,
        // and on the way down this IS the notification.
        _freecamCamSentAt = 0;
        if (!up) _net?.SuiCam(0f, 0f, 0f, active: false);
    }

    /// <summary>The unit whose data the gameplay UI shows and whose movement input drives.</summary>
    internal ulong ControlledGuid =>
        _controlState is ControlState.Possessing or ControlState.ReleasePending && _controlTargetGuid != 0
            ? _controlTargetGuid
            : LocalPlayerGuid;

    /// <summary>
    /// Guid excluded from the streamed-player render pass because the first-person
    /// CharacterRenderer body stands in for it. 0 in the Command View: the rig is not standing
    /// anywhere, so everyone — own character and any possessed bot alike — renders from the
    /// entity stream where their body actually is.
    /// </summary>
    internal ulong RenderSelfGuid => _freeView ? 0UL : ControlledGuid;

    // ── Per-unit action stores ────────────────────────────────────────────────────────────────
    // The session socket only ever streams the logged-in character's spellbook/bars/cooldowns;
    // a possessed bot's arrive wrapped in SMSG_SUI_PROXY and land in its own store. Same pattern
    // as the pet bar's separate cooldown store (Program.Pet.cs).

    private readonly Dictionary<ulong, PlayerActions> _actionsByGuid = new();

    private PlayerActions ActionsFor(ulong guid)
    {
        if (!_actionsByGuid.TryGetValue(guid, out PlayerActions? actions))
        {
            actions = new PlayerActions();
            _actionsByGuid[guid] = actions;
        }
        return actions;
    }

    /// <summary>The logged-in character's store — the target of un-proxied wire feeds.</summary>
    private PlayerActions OwnActions => ActionsFor(LocalPlayerGuid);

    /// <summary>
    /// The guid whose bars/spellbook/talents the HUD displays. In the Command View a single
    /// selected party bot swaps its bars in (read-only inspection); otherwise the
    /// controlled unit's, exactly as before.
    /// </summary>
    internal ulong BarsGuid =>
        // Read-only inspection is the fallback, NOT a competitor: while you are commanding a
        // toon its bars stay live and castable no matter what the marquee is highlighting,
        // because commanding a character from the sky has to give you the same character you
        // would have driving it directly. Selecting someone else picks who your right-click
        // ORDERS — it does not quietly demote the character you are playing to a spectator.
        // Inspection therefore only applies when nobody is commanded.
        _freeView && _controlState != ControlState.Possessing &&
        _freecamSelection.Count == 1 && _freecamSelection[0] != ControlledGuid
            ? _freecamSelection[0]
            : ControlledGuid;

    /// <summary>
    /// Whether a real unit, rather than the observer or a pending hand-off, owns direct action
    /// authoring. Possession remains actionable from the sky by design; plain FreeCam commands
    /// the party only through the explicit SUI order route.
    /// </summary>
    private bool CanAuthorControlledGameplay =>
        !TacticalFreezeBlocksLiveCommands &&
        (_controlState == ControlState.Possessing ||
         _controlState == ControlState.OwnChar && !_freeView);

    /// <summary>
    /// Author the unit in <see cref="ControlledGuid"/>: either you can author it as a driven body
    /// (<see cref="CanAuthorControlledGameplay"/> — possessing, or your own char in first person),
    /// OR it is your OWN logged-in character even from the free-view sky. The latter is always safe:
    /// guid-less opcodes apply to <c>_player</c> = you, you remain your own self-mover, and the
    /// server accepts it (confirmed: no self-mover guard on cast, GetSuiActor→_player). Only a
    /// detached cursor commanding someone ELSE's body without possession is excluded. Use this for
    /// the cast/item-use send gates so your own skills and bag items work in Command View.
    /// </summary>
    private bool CanAuthorControlledOrSelf =>
        !TacticalFreezeBlocksLiveCommands &&
        (CanAuthorControlledGameplay || ControlledGuid == LocalPlayerGuid);

    /// <summary>Inspection and observer bars display state but never author gameplay.</summary>
    private bool BarsReadOnly => BarsGuid != ControlledGuid || !CanAuthorControlledGameplay;

    /// <summary>
    /// Primary Command View subject: the primary selection's feet (or the controlled unit's)
    /// while Command View is up. The roof plane and the camera-side slice (Engine/WorldCut.cs)
    /// are gated by their own Settings toggles in Program.cs.
    /// </summary>
    private Vector3? CommandViewPrimarySubject()
    {
        if (!_freeView) return null;
        // Creator sandbox: no entity stream; the "primary" is where the character stood when
        // the view went up (ToggleFreeView parks it there and returns it there).
        if (_net is not { IsInWorld: true }) return CreatorInWorld ? _creatorFreeViewReturn : null;
        ulong guid = RtsPrimaryGuid != 0 ? RtsPrimaryGuid : ControlledGuid;
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity unit)) return null;
        return unit.Position;
    }

    /// <summary>
    /// Divinity-style cutaway subject: the commanded toon's position (eye height
    /// added for the cell drop test) while the Command View is up and the Settings
    /// checkbox is on; null otherwise. Fed to WmoRenderer.SetCutawaySubject once
    /// per frame — the single integration point for the whole feature.
    /// </summary>
    private Vector3? FreeViewCutawaySubject()
    {
        if (!_freeView || !Settings.Controls.FreeViewCutaway) return null;
        if (_controlState is not ControlState.Possessing || _controlTargetGuid == 0) return null;
        if (!_entities.TryGet(_controlTargetGuid, out WorldEntity bot)) return null;
        return bot.Position + new Vector3(0f, 0f, 1.5f);
    }

    /// <summary>
    /// Which renderer owns the CONTROLLED unit's skeleton. Normally the first-person
    /// CharacterRenderer; in the Command View that body is not drawn at all — the driven unit
    /// streams in like any other player and CreatureRenderer owns it. Body animations
    /// (cast, channel, one-shots, wound reactions) have to follow, or they play on a body
    /// nobody is looking at and the commanded toon casts without moving a muscle.
    /// </summary>
    private bool ControlledBodyIsStreamed => _freeView || ControlledUsesDisplayModel;

    private readonly record struct WorldBodyPose(Vector3 Position, float Orientation);

    /// <summary>
    /// True only while the controller is physically driving the controlled body. Command View,
    /// pending hand-offs, parked movement, and fly rigs all leave body pose on the entity stream.
    /// </summary>
    private bool ControllerOwnsControlledBodyPose =>
        _controller is not null &&
        WorldBodyPoseLaw.ControllerOwnsPose(
            _freeView,
            _controlState is ControlState.OwnChar or ControlState.Possessing,
            queriedControlledBody: true,
            controllerMovementAuthoritative:
                !_movementSender.Parked && !_controller.Flying);

    /// <summary>
    /// Resolve a unit's actual world pose without ever substituting the observer camera.
    ///
    /// There are deliberately two identities above this primitive:
    /// <see cref="ControlledGuid"/> is the combat/action body, while
    /// <see cref="LocalPlayerGuid"/> is the logged-in session body that owns loot, quests,
    /// mail, NPC services, resurrection, and other non-proxied interactions. Both identities
    /// use this same pose rule. Only a stably embodied controlled unit may read the controller;
    /// pending SUI states and Command View read the streamed entity instead.
    /// </summary>
    private bool TryGetWorldBodyPose(ulong guid, out WorldBodyPose pose)
    {
        if (guid != 0 && guid == ControlledGuid && ControllerOwnsControlledBodyPose &&
            _controller is { } bodyController)
        {
            pose = new(bodyController.Position, bodyController.Yaw);
            return true;
        }

        if (guid != 0 && _entities.TryGet(guid, out WorldEntity body))
        {
            pose = new(body.Position, body.Orientation);
            return true;
        }

        // Creator Command View has no server stream. The body remains exactly where the observer
        // detached from it; the fly rig must still never become its interaction pose.
        if (_net is null && _freeView && guid == CreatorLocalGuid)
        {
            pose = new(_creatorFreeViewReturn, _creatorFreeViewReturnYaw);
            return true;
        }

        pose = default;
        return false;
    }

    private bool TryGetControlledBodyPose(out WorldBodyPose pose) =>
        TryGetWorldBodyPose(ControlledGuid, out pose);

    private bool TryGetSessionBodyPose(out WorldBodyPose pose) =>
        TryGetWorldBodyPose(LocalPlayerGuid, out pose);

    /// <summary>
    /// The body a same-session NPC interaction (gossip / quest-giver range gate)
    /// acts from. While driving a party bot that is the BOT, so the vanilla frames
    /// open and their range checks pass at the bot's feet instead of the parked
    /// commander's; unpossessed it is the session body, unchanged. [SUI] P4b — the
    /// server runs these handlers as GetSuiActor() (the bot), so the client must
    /// gate them at the same body or the accept/turn-in follow-ups refuse on range.
    /// </summary>
    private bool TryGetInteractionBodyPose(out WorldBodyPose pose) =>
        _controlState == ControlState.Possessing
            ? TryGetControlledBodyPose(out pose)
            : TryGetSessionBodyPose(out pose);

    /// <summary>
    /// The store the action-bar/spellbook/talent UI reads. Deliberately keeps the historical
    /// field name: every existing read of the single-character store now follows possession
    /// (and, in the Command View, the inspected selection).
    /// </summary>
    private PlayerActions _actions => ActionsFor(BarsGuid);

    /// <summary>Enter-world reset: drop every per-unit store (replaces `_actions.Clear()`).</summary>
    private void ResetActionStores() => _actionsByGuid.Clear();

    /// <summary>
    /// Session-loss reset. Possession and the Command View both normally end on a server ACK;
    /// with the socket gone that ACK is never coming, so the client returns itself to its
    /// own character on the ground rather than stranding the fly rig with a parked stream.
    /// </summary>
    private void ResetSuiControl()
    {
        ResetTacticalFreezeState();
        SetFreeView(false);
        _controlState = ControlState.OwnChar;
        _controlTargetGuid = 0;
        _controlSwitchQueued = 0;
        _controlledBodyPending = false;
        _freecamRequested = false;
        _freeViewExitRequested = false;
        _freeViewExitAttempts = 0;
        _freecamSelection.Clear();
        ClearRtsWaypointChain();
        ClearRtsAttackQueue();
        CancelRtsPatrolAuthoring(silent: true);
        _suiRoster.Clear();
        ClearRtsForceTakeControl();
        ResetRtsControlGroups();
        ResetCompanionVoiceState();
        ResetPartyMemberFacts();
        ResetCommanderRaid();
        ResetPartyQuestFacts();
        ResetRaidInfo();
        ResetStable();
        ResetPartyGiverStatus();
        ResetPartyGiverQuests();
        ResetPartyLead();
        ResetThreatMeter();
        ResetCompanions();
        ResetPartyQuestActs();
        PurgeSuiSnapshot();
        _movementSender.Parked = false;
        // Session loss has no later seating edge from which to adopt a body.
        // Drop every per-mover grant/override now so the next character cannot
        // inherit root, water-walk, hover, or a stale aura/mount speed table.
        ResetMovementModes();
        ResetControlledSpeeds();
        _walkToggled = false;
        _autorunToggled = false;
        if (_controller is not null) _controller.Flying = false;
        if (_character is not null) _character.Enabled = true;
        _window.FreeSelectMode = false;
    }

    // ── SMSG_SUI_CONTROL_ACK result codes (SuperUI-Core SuiPossess.h) ─────────────────────────
    private const byte SuiAckOk = 0;
    private const byte SuiAckFirstRelease = 16;    // 16.. are releases, solicited or forced
    private const byte SuiAckReleasedFreecam = 17;

    // ── Control session state ─────────────────────────────────────────────────────────────────
    private readonly List<(ulong Guid, byte Flags)> _suiRoster = [];
    private double _controlPendingSince;
    private ControlState _controlPendingReturn = ControlState.OwnChar;   // watchdog fallback
    private ulong _controlSwitchQueued;      // cycle target waiting for the in-flight release ACK
    private bool _controlledBodyPending;     // possessed body rebuild waiting on entity stream-in

    // ── Free-view marquee selection (party + advertised faction bots) ───────────────────────────
    private Vector2? _freecamDragOrigin;         // left button went down here, over the world
    private bool _freecamDragActive;             // travel exceeded the click threshold
    private bool _freecamMarqueeConsumedClick;   // swallow the release's queued world click
    private readonly List<ulong> _freecamSelection = [];
    private const float FreecamDragThresholdPixels = 6f;
    private readonly List<(Vector3 Pos, double Born, Vector3 Tint)> _rtsMoveMarkers = [];
    private readonly List<Vector3> _rtsWaypointChain = [];   // Shift+RightClick chain dots
    private readonly List<ulong> _rtsWaypointSubjects = [];  // who the chain was issued to
    private readonly List<ulong> _rtsAttackQueue = [];        // Shift+click hostile sequence
    private readonly List<ulong> _rtsAttackSubjects = [];     // frozen selection for that sequence
    private double _rtsAttackIssuedAt;
    private const double RtsAttackStaleSeconds = 90.0;
    // Patrol button flow (owner 2026-08-25): Patrol ARMS a draft, right-clicks
    // chain COLD waypoints (nothing ordered yet), Patrol again queues every leg
    // and engages the loop; Escape cancels. Subjects freeze at arm time.
    private bool _rtsPatrolAuthoring;
    private readonly List<Vector3> _rtsPatrolDraft = [];
    private readonly List<ulong> _rtsPatrolDraftSubjects = [];
    private double _rtsWaypointProgressAt;                   // last leg consumed / chain issued
    private const float RtsWaypointReachedYards = 3.5f;
    private const double RtsWaypointStaleSeconds = 45.0;
    // Client-side coalesce for plain move (type 0) orders. Rapid right-click spam floods the
    // server, where every move pays a pathfinding storm and each interrupt fakes an arrival
    // (see the SuiPossess flood diagnosis), so a single unit "hangs up." A human dragging a
    // destination never means more than a handful of distinct points per second, so we send at
    // most one move per interval and let the newest destination win. Other order types
    // (waypoint/attack/formation/hold) stay immediate - they are discrete, not spammed.
    private const double RtsMoveOrderMinInterval = 0.12;     // seconds; ~8 move sends/sec ceiling
    private List<ulong>? _pendingMoveSubjects;
    private Vector3 _pendingMovePoint;
    private bool _hasPendingMoveOrder;
    private double _lastMoveOrderSentAt;
    private Vector3 _freecamCamSentPosition;                 // last CMSG_SUI_CAM position
    private double _freecamCamSentAt;                        // and when it went out
    private static readonly Vector3 RtsFriendlyTint = new(0.30f, 0.95f, 0.45f);
    private static readonly Vector3 RtsHostileTint = new(0.95f, 0.30f, 0.22f);
    private static readonly Vector3 RtsNeutralTint = new(0.95f, 0.85f, 0.25f);
    private bool _freecamRequested;          // the in-flight release asked for the Command View
    // Right-button camera-look latch: true when the current right-hold flew the camera by
    // keyboard (WASD). The window's click-vs-drag test only measures MOUSE travel, so a
    // stationary right-hold + WASD would otherwise read as a click and fire a phantom order.
    private bool _freecamRightPanned;
    private bool _freecamRightWasDown;
    private bool _freeViewExitRequested;     // ...and this one asks to LEAVE it

    /// <summary>
    /// When the outstanding CMSG_SUI_CONTROL_RELEASE(0) went out, and how many times we have
    /// asked. The Command View exit is the ONE control transition with no ControlState pending
    /// value to hang the ordinary watchdog on - see UpdateControlInput.
    /// </summary>
    private double _freeViewExitRequestedAt;
    private int _freeViewExitAttempts;
    private const int FreeViewExitMaxAttempts = 3;
    private double _freecamPanAt;            // last edge-pan tick, for its own dt
    private const float FreecamEdgePanMargin = 14f;   // px from a screen edge that starts a pan
    private const float RtsMaxWheelAltitudeYards = 120f;
    private float _rtsWheelRetreatYards;
    private const byte SuiRosterControllable = 0x01;
    private const byte SuiRosterPossessed = 0x02;
    // COMPANIONS v1: this member is one of YOUR summoned alts. Only ever set for the
    // owner (other humans see 0); the server also sets 0x01 on it so possession and
    // orders work unchanged.
    private const byte SuiRosterOwnCompanion = 0x08;
    private const double ControlAckTimeoutSeconds = 3.0;

    /// <summary>True when the control roster marks this guid as one of the local player's own companions.</summary>
    private bool IsOwnCompanion(ulong guid)
    {
        if (guid == 0) return false;
        foreach ((ulong rosterGuid, byte flags) in _suiRoster)
            if (rosterGuid == guid) return (flags & SuiRosterOwnCompanion) != 0;
        return false;
    }

    /// <summary>SMSG_SUI_CONTROL_ROSTER: which group members are possessable bots.</summary>
    /// <summary>Chain truth per member (SMSG_SUI_CONTROL_ROSTER row v2): state 0 linked,
    /// 1 unlinked by you (holds until re-linked), 2 world hold (clears when its anchor is back
    /// in range); anchor = the body its formation keys on (0 = none / a real player).</summary>
    private readonly Dictionary<ulong, (byte State, ulong Anchor)> _suiChain = [];
    private const byte SuiChainLinked = 0, SuiChainUnlinked = 1, SuiChainWorldHold = 2;

    private bool IsPartyMemberGuid(ulong guid) =>
        guid != 0 && (guid == LocalPlayerGuid || _partyMembers.Any(member => member.Guid == guid));

    private static uint SuiChainColor(byte state, byte alpha = 0xFF) =>
        (uint)alpha << 24 | (state == SuiChainLinked ? 0x60D080u : state == SuiChainWorldHold ? 0x40B0F0u : 0x5060E0u);

    /// <summary>The chain words for a tooltip ("" when the roster carries no chain row).</summary>
    private string PartyChainTip(ulong guid)
    {
        if (!_suiChain.TryGetValue(guid, out (byte State, ulong Anchor) chain)) return "";
        string who = chain.Anchor != 0 ? ResolveUnitName(chain.Anchor) : "";
        return chain.State switch
        {
            SuiChainLinked => who.Length > 0 ? $"Chained to {who}" : "Chained",
            SuiChainWorldHold => who.Length > 0 ? $"Holding — waits for {who} to come back in range" : "Holding",
            _ => "Unchained — stands its ground until re-linked",
        };
    }

    private void ApplySuiControlRoster(byte[] body)
    {
        var r = new PacketReader(body);
        int count = r.ReadU8();
        // Row v2 (18 bytes: + u8 chain, u64 anchor) since 2026-09-03; an older core still
        // sends 9-byte rows. The body length says which.
        bool v2 = count > 0 && body.Length == 1 + 18 * count;
        _suiRoster.Clear();
        _suiChain.Clear();
        for (int i = 0; i < count && r.Remaining >= (v2 ? 18 : 9); i++)
        {
            ulong guid = r.ReadU64();
            byte flags = r.ReadU8();
            _suiRoster.Add((guid, flags));
            if (v2) _suiChain[guid] = (r.ReadU8(), r.ReadU64());
        }
    }

    /// <summary>
    /// SMSG_SUI_CONTROL_ACK. Grants and denials answer our requests; release codes (16+) can
    /// also arrive UNSOLICITED (bot died, group broke, teleport) and must be honoured in any
    /// state. The carried position is authoritative for whichever unit we drive next.
    /// </summary>
    private void ApplySuiControlAck(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte result = r.ReadU8();
        float x = r.ReadF32(), y = r.ReadF32(), z = r.ReadF32(), o = r.ReadF32();

        // A zero-guid request is the backwards-compatible feature probe used by
        // REAL_PORTALS. Old cores return an ordinary denial; new cores append a
        // capability trailer. Neither response is a possession attempt or a UI
        // error, so consume it before the control state machine below.
        if (ApplyRealPortalCapabilityAck(guid, r)) return;

        if (result == SuiAckOk)
        {
            bool rtsForceTakeControl = _rtsForceTakeControlGuid == guid;
            if (rtsForceTakeControl)
            {
                // Commander Take Control is an explicit commitment to the body, unlike
                // ordinary CRPG party possession from the sky. Land only this dedicated
                // path; Tier-1 party/free-view possession keeps its existing camera law.
                _commanderMapOpen = false;
                SetFreeView(false);
                ClearRtsForceTakeControl();
            }
            // Whoever we were driving is about to start drawing from the entity stream instead
            // of from the controller — file where they actually stand first.
            SyncDrivenEntityToController();
            _controlTargetGuid = guid;
            _controlState = ControlState.Possessing;
            // [SUI] The server answers quest-giver status as GetSuiActor(), so every
            // cached !/? became stale the instant control changed hands. Without this
            // the world kept wearing the PREVIOUS character's markers until an
            // unrelated quest packet forced a re-ask (in practice: the new bot's
            // first kill). Same for the party overlay's pull throttle.
            BumpQuestStatusReask();
            PokePartyGiverStatus();
            // No-op on the camera while the Command View is up (see SeatControllerOnControlled).
            SeatControllerOnControlled(x, y, z, o);
            // [SUI] A same-map grant far outside the resident scene (a party member two zones
            // away, granted in place without relocating the main): load the destination
            // behind the curtain the way a far same-map teleport does, or gravity runs on
            // ground that has not streamed yet (owner 2026-09-03: "fell through the floor").
            if (!_freeView) PrepareFarControlArrival(new Vector3(x, y, z));
            _net?.SetActiveMover(guid);
            EnterPlayerAuraWorld(guid);
            // [SUI] Body-scoped session UI changes hands with the body: the pet bar is
            // rebuilt from the driven bot's SMSG_PET_SPELLS (pushed after the grant
            // snapshot), the loot window and bank session belong to the body that
            // opened them (the server force-releases a possessed bot's loot on grant).
            ResetBodySessionUiOnControlChange();
            ApplyControlledCharacter();
            AddChatMessage(_freeView
                ? $"Commanding {ResolveUnitName(guid)} — bars and bags are live, camera stays up."
                : $"You take control of {ResolveUnitName(guid)}.");
            return;
        }

        // RTS force control can move the owner's real body to an outdoor bot's
        // continent. Stock NEW_WORLD owns map adoption; retain the target and
        // retry 828 only after that exact player entity has streamed back in.
        if (result == 7 && guid != 0 && guid == _rtsForceTakeControlGuid)
        {
            _controlState = _controlPendingReturn;
            _movementSender.Parked = false;
            _controlSwitchQueued = 0;
            _freecamRequested = false;
            _rtsForceTakeControlPhase = RtsForceTakeControlPhase.AwaitingStream;
            _rtsForceTakeControlAt = NowSeconds();
            CommanderShowNotice("Moving to that bot's continent; control resumes after streaming.");
            return;
        }

        // Both solicited release codes honour a pending free-view request; only the
        // forced codes (18+: death, teleport, group change) override it. A server
        // answering 16 to a mode-1 release must not snap the camera back to the
        // character — that read as a momentary floor-drop.
        if (_freecamRequested && result is SuiAckFirstRelease or SuiAckReleasedFreecam)
        {
            // Enter the Command View: nobody is driven, the whole party (own character
            // included) runs on AI. The controller becomes the fly rig where it stands;
            // RenderSelfGuid goes 0 so everyone renders from the entity stream.
            // Same hand-off as a possess: RenderSelfGuid goes 0, so the driven body stops being
            // drawn from the controller and starts being drawn from its (stale) streamed entity.
            SyncDrivenEntityToController();
            _freecamRequested = false;
            _controlTargetGuid = 0;
            SetFreeView(true);
            _controlState = ControlState.FreeCam;
            _movementSender.Parked = false;       // the Flying branch parks it from here
            if (_controller is not null) _controller.Flying = true;
            EnterPlayerAuraWorld(LocalPlayerGuid);
            // Snapshots survive the Command View: possession synced them, the
            // Party Inventory browser keeps reading them with an age stamp.
            // Every chord in this line is now a binding, so the line READS the bindings.
            // A player who reseats Take Direct Control must not be told to Alt+click.
            AddChatMessage($"Command View: {BindingHint(GameBinding.RtsSelect)} or drag to select " +
                $"faction bots, {BindingHint(GameBinding.RtsSelectAdd)} adds, " +
                $"{BindingHint(GameBinding.RtsOrderMove)} moves/attacks, " +
                $"{BindingHint(GameBinding.CrpgTakeControl)} directly controls one, " +
                $"{BindingHint(GameBinding.RtsOrderQueueWaypoint)} chains waypoints, " +
                $"{BindingHint(GameBinding.RtsToggleFreeView)} returns.");
            return;
        }

        if (result >= SuiAckFirstRelease)
        {
            bool wasPossessing = _controlState is ControlState.Possessing or ControlState.ReleasePending;
            // The bot we were driving reverts to a streamed body on this line; file where we
            // actually walked it to, or it snaps back to where we picked it up.
            SyncDrivenEntityToController();
            _controlTargetGuid = 0;

            // A SOLICITED release (16/17) inside the Command View is a control change only —
            // clicking your own toon, or the release half of a bot-to-bot switch. The camera
            // stays in the sky. Only the FORCED codes (18+: death, teleport, group change)
            // mean the server has put you back in your body, which ends the Command View.
            //
            // ...UNLESS the release WAS the Ctrl+F that asked to leave. Both arrive as 16, so
            // the reason code cannot tell them apart — only the client knows which it sent,
            // and without that flag Ctrl+F answered its own exit by staying put.
            bool staysInFreeView = _freeView && !_freeViewExitRequested &&
                result <= SuiAckReleasedFreecam;
            if (!staysInFreeView) SetFreeView(false);
            _freeViewExitRequested = false;
            _freeViewExitAttempts = 0;
            _controlState = staysInFreeView ? ControlState.FreeCam : ControlState.OwnChar;
            // Same staleness on the way out: statuses answered for the released bot
            // must be re-asked for whoever the session acts as now.
            BumpQuestStatusReask();
            PokePartyGiverStatus();
            SeatControllerOnControlled(x, y, z, o);
            _net?.SetActiveMover(LocalPlayerGuid);
            EnterPlayerAuraWorld(LocalPlayerGuid);
            // [SUI] Same hand-over on the way out: the server re-sends the own
            // character's pet bar after this ack and released the bot's loot before it.
            ResetBodySessionUiOnControlChange();
            // Retention: the released bot's snapshot stays for the browser.
            ApplyControlledCharacter();
            if (wasPossessing)
                AddChatMessage(result switch
                {
                    18 => "Control lost: your character died... no wait — the bot died.",
                    19 => "Control released: teleport.",
                    20 => "Control released: group changed.",
                    21 => "Control released: logout.",
                    _ when staysInFreeView => "Commanding no one — the party runs itself.",
                    _ => "Control returned to your character.",
                });
            // A queued cycle switch survives the voluntary release that preceded it.
            if (_controlSwitchQueued != 0)
            {
                ulong next = _controlSwitchQueued;
                _controlSwitchQueued = 0;
                if (result is >= SuiAckFirstRelease and <= SuiAckReleasedFreecam)
                    RequestPossess(next);
            }
            return;
        }

        // Denial: fall back to whatever we were before the request (a free-view
        // possess click drops back into the Command View, not onto the own character).
        if (_controlState == ControlState.PossessPending)
        {
            _controlState = _controlPendingReturn == ControlState.FreeCam
                ? ControlState.FreeCam
                : ControlState.OwnChar;
            _movementSender.Parked = false;
        }
        else if (_controlState == ControlState.FreeCam)
        {
            // The server refused the free-view exit (it no longer holds any control
            // session for us). Land locally on the own character rather than staying
            // stuck in the fly rig with no way back.
            ExitFreeCamLocally();
            return;
        }
        _controlSwitchQueued = 0;
        if (_rtsForceTakeControlGuid == guid) ClearRtsForceTakeControl();
        PlayCompanionEmoteVoice(guid, CompanionVoiceLaw.EmoteNo);
        ShowUiError(result switch
        {
            2 => "That character is not a controllable bot.",
            3 => "That bot is not in your group or authorized faction force.",
            4 => "Someone is already controlling that bot.",
            5 => "That bot cannot be controlled right now.",
            6 => "You cannot take control right now.",
            7 => "That RTS relocation could not be completed.",
            8 => "That bot is in a different instance.",
            _ => "Cannot take control.",
        });
    }

    /// <summary>
    /// SMSG_SUI_PROXY: an owner-only packet of the possessed bot (bars, spellbook,
    /// cooldowns, cast results), re-wrapped by the server with the bot's guid. Routed
    /// into the bot's per-guid store via the normal parsers; stragglers whose source
    /// is not the currently controlled unit are dropped (possession-boundary races).
    /// </summary>
    private void ApplySuiProxy(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 10) return;
        ulong source = r.ReadU64();
        var innerOp = (Op)r.ReadU16();
        byte[] inner = r.ReadBytes(r.Remaining);
        if (source == 0 || source == LocalPlayerGuid || source != ControlledGuid) return;

        PlayerActions store = ActionsFor(source);
        // The main dispatch consults QuestStatusRefreshLaw for every DIRECT packet;
        // proxied inner frames bypass that loop, so a quest completed AS the bot
        // never invalidated the cached giver statuses without this.
        if (QuestStatusRefreshLaw.PacketReasks(innerOp)) BumpQuestStatusReask();
        switch (innerOp)
        {
            case Op.SMSG_SET_FLAT_SPELL_MODIFIER:
            case Op.SMSG_SET_PCT_SPELL_MODIFIER:
                ApplySpellModifier(inner, source, innerOp == Op.SMSG_SET_PCT_SPELL_MODIFIER);
                break;
            case Op.SMSG_SET_PROFICIENCY:
                ApplyItemProficiency(inner, source);
                break;
            case Op.SMSG_CANCEL_COMBAT:
                if (inner.Length != 0) throw new InvalidDataException("Cancel combat requires empty body");
                EnqueueSpellPresentation(new SpellCombatCancelledEvent(source));
                break;
            case Op.SMSG_CANCEL_AUTO_REPEAT:
                if (inner.Length != 0) throw new InvalidDataException("Cancel repeat requires empty body");
                EnqueueSpellPresentation(new SpellAutoRepeatCancelledEvent(source));
                break;
            case Op.MSG_CHANNEL_START:
                var channel = SpellLifecyclePacketParser.ParseChannelStart(inner);
                EnqueueSpellPresentation(new SpellChannelStartEvent(source, channel.SpellId, channel.DurationMs));
                break;
            case Op.MSG_CHANNEL_UPDATE:
                EnqueueSpellPresentation(new SpellChannelUpdateEvent(source,
                    SpellLifecyclePacketParser.ParseChannelUpdate(inner)));
                break;
            case Op.SMSG_UPDATE_AURA_DURATION:
                ApplyAuraDuration(inner, source);
                break;
            case Op.SMSG_INVENTORY_CHANGE_FAILURE:
                ApplyInventoryChangeFailure(inner);
                break;
            case Op.SMSG_START_MIRROR_TIMER:
                ApplyMirrorTimerStart(inner, source);
                break;
            case Op.SMSG_PAUSE_MIRROR_TIMER:
                ApplyMirrorTimerPause(inner, source);
                break;
            case Op.SMSG_STOP_MIRROR_TIMER:
                ApplyMirrorTimerStop(inner, source);
                break;
            case Op.SMSG_FEIGN_DEATH_RESISTED:
                EnqueueFeignDeathResisted(source, inner);
                break;
            case Op.SMSG_SET_FORCED_REACTIONS:
                _forcedReactions.Apply(source, inner);
                break;
            case Op.SMSG_ACTION_BUTTONS:
                store.ApplyButtons(inner);
                // Bots own no server-side buttons; an empty wire bar hands the
                // slots to the layered client bars (base / class / per-bot).
                if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
                    PopulateBotBar(source);
                break;
            case Op.SMSG_INITIAL_SPELLS:
                // Paired Core replays all nonzero modifier cells immediately after this snapshot.
                // Direct main login can receive modifiers before INITIAL_SPELLS, so only reset the proxy owner here.
                _spellModifiers?.Remove(source);
                store.ApplyInitialSpells(inner, MovementInfo.ClientUptimeMs() / 1000.0);
                if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
                    PopulateBotBar(source);
                break;
            case Op.SMSG_LEARNED_SPELL:
                store.Learn(new PacketReader(inner).ReadU16());
                break;
            case Op.SMSG_SUPERCEDED_SPELL:
                {
                    var ir = new PacketReader(inner);
                    store.Supercede(ir.ReadU16(), ir.ReadU16());
                }
                break;
            case Op.SMSG_REMOVED_SPELL:
                store.Remove(new PacketReader(inner).ReadU16());
                break;
            case Op.SMSG_SPELL_COOLDOWN:
                // Addressed packet — self-routes to the right store by embedded caster guid.
                ApplyAddressedSpellCooldowns(inner);
                break;
            case Op.SMSG_ITEM_COOLDOWN:
                ApplyItemCooldown(inner, store);
                break;
            case Op.SMSG_COOLDOWN_EVENT:
                ApplyCooldownEvent(inner, clear: false);
                break;
            case Op.SMSG_CLEAR_COOLDOWN:
                ApplyCooldownEvent(inner, clear: true);
                break;
            case Op.SMSG_COOLDOWN_CHEAT:
                ApplyCooldownCheat(inner);
                break;
            case Op.SMSG_STANDSTATE_UPDATE:
                ApplyStandStateUpdate(inner, source);
                break;
            case Op.SMSG_CAST_RESULT:
                {
                    var result = SpellPacketParser.ParseResult(inner);
                    if (result.Status == 2)
                        EnqueueSpellPresentation(new SpellCastResultEvent(source, result.SpellId, result.Reason) { Context = result.Context });
                }
                break;
            // [SUI] P4b: NPC-interaction reply frames of the driven bot. The server
            // runs the quest/gossip handler family as the bot and mirrors these
            // (MirrorOwnerPacket); route them into the very parsers the direct
            // dispatch uses so the vanilla quest/gossip frame opens for the commander
            // driving the bot. The frame's own accept/query/turn-in follow-ups gate
            // at the controlled body (TryGetInteractionBodyPose) and are redirected
            // to the bot server-side, so the round trip stays on the bot end to end.
            case Op.SMSG_GOSSIP_MESSAGE:
                ApplyGossipMenu(inner, source);
                break;
            case Op.SMSG_INITIALIZE_FACTIONS:
            case Op.SMSG_SET_FACTION_VISIBLE:
            case Op.SMSG_SET_FACTION_STANDING:
            case Op.SMSG_SET_FACTION_ATWAR:
                ApplyReputationPacket(innerOp, inner, source);
                break;
            case Op.SMSG_WEATHER:
                ApplyActorWeather(inner, source);
                break;
            case Op.SMSG_PET_UNLEARN_CONFIRM:
                ApplyPetUnlearnConfirm(inner, source);
                break;
            case Op.SMSG_GOSSIP_COMPLETE:
                EmitInterface("gossip", "complete", "RECEIVED", source, "serverClosed=true;proxied=true");
                CompleteGossip();
                _pendingItemRead = null;
                CloseQuestNpcFrame(playSound: true);
                break;
            // Partner-side trade replies address the bot's socket-less session and have no direct duplicate.
            case Op.SMSG_TRADE_STATUS:
                ApplyTradeStatus(inner);
                break;
            case Op.SMSG_TRADE_STATUS_EXTENDED:
                ApplyTradeExtended(inner);
                break;
            case Op.SMSG_GOSSIP_POI:
                ApplyGossipPoi(inner);
                break;
            case Op.SMSG_QUESTGIVER_STATUS:
                ApplyQuestStatus(inner);
                break;
            case Op.SMSG_QUESTGIVER_QUEST_LIST:
                ApplyQuestList(inner);
                break;
            case Op.SMSG_QUESTGIVER_QUEST_DETAILS:
                ApplyQuestDetails(inner);
                break;
            case Op.SMSG_QUESTGIVER_REQUEST_ITEMS:
                ApplyQuestRequestItems(inner);
                break;
            case Op.SMSG_QUESTGIVER_OFFER_REWARD:
                ApplyQuestOffer(inner);
                break;
            case Op.SMSG_QUESTGIVER_QUEST_COMPLETE:
            case Op.SMSG_QUESTGIVER_QUEST_INVALID:
            case Op.SMSG_QUESTGIVER_QUEST_FAILED:
            case Op.SMSG_QUESTLOG_FULL:
            case Op.SMSG_QUESTUPDATE_COMPLETE:
            case Op.SMSG_QUESTUPDATE_FAILED:
            case Op.SMSG_QUESTUPDATE_FAILEDTIMER:
            case Op.SMSG_QUESTUPDATE_ADD_ITEM:
            case Op.SMSG_QUESTUPDATE_ADD_KILL:
                ApplyQuestNotice(innerOp, inner, source);
                break;
            // [SUI] P4b vendor/trainer/repair: the driven bot's shop and trainer reply
            // frames, when the reply routed through the bot's socket (a gossip
            // "browse goods" / "train" option, or an internal buy error). The direct
            // vendor/trainer-frame requests already answer on the commander's own
            // socket; either way these feed the same parsers. Bag/coin edits refresh
            // via the bot's re-snapshot, not here.
            case Op.SMSG_LIST_INVENTORY:
                ApplyVendorList(inner);
                break;
            case Op.SMSG_SELL_ITEM:
                ApplyVendorSellFailure(inner);
                break;
            case Op.SMSG_BUY_ITEM:
                ApplyVendorStockUpdate(inner);
                break;
            case Op.SMSG_BUY_FAILED:
                ApplyVendorBuyFailure(inner);
                break;
            case Op.SMSG_TRAINER_LIST:
                ApplyTrainerList(inner);
                break;
            case Op.SMSG_TRAINER_BUY_SUCCEEDED:
                ApplyTrainerSuccess(inner);
                break;
            case Op.SMSG_TRAINER_BUY_FAILED:
                ApplyTrainerFailure(inner);
                break;
            // [SUI] loot: LootHandler runs as the driven bot and Player::SendLoot* emit
            // on the LOOTER's session, so the whole loot window round-trip of a corpse
            // the bot kneels at arrives here. Same parsers as the direct dispatch; the
            // loot state is session-wide (one window), keyed by the corpse guid.
            case Op.SMSG_LOOT_RESPONSE:
                ApplyLootResponse(inner);
                break;
            // Group::MasterLoot answers the LOOTER's session, which is the driven bot's, so the
            // candidate list arrives here exactly like the loot window itself. Without this the
            // master looter saw the items and had nobody to assign them to.
            // Pair: SuiPossess.cpp MirrorOwnerPacket must whitelist SMSG_LOOT_MASTER_LIST.
            case Op.SMSG_LOOT_MASTER_LIST:
                ApplyLootMasterList(inner);
                break;
            case Op.SMSG_LOOT_RELEASE_RESPONSE:
                ApplyLootReleaseResponse(inner);
                break;
            case Op.SMSG_LOOT_REMOVED:
                ApplyLootRemoved(inner);
                break;
            case Op.SMSG_LOOT_CLEAR_MONEY:
                ApplyLootClearMoney();
                break;
            case Op.SMSG_LOOT_MONEY_NOTIFY:
                break; // the bot's purse rides its re-snapshot; nothing to do
            case Op.SMSG_ITEM_PUSH_RESULT:
                // The server skips the commander's direct group-broadcast copy while
                // possessing, so this mirrored one is the only "You receive loot" line.
                ApplyItemPushResult(inner);
                break;
            // [SUI] pet: PetHandler runs as the driven bot; its pet bar/mode/feedback
            // frames address the pet owner's (the bot's) session. The bar is reset on
            // every control change (ApplySuiControlAck) and rebuilt from these.
            case Op.SMSG_PET_SPELLS:
                ApplyActorPetPacket(Op.SMSG_PET_SPELLS, inner, source);
                break;
            case Op.SMSG_PET_MODE:
                ApplyActorPetPacket(Op.SMSG_PET_MODE, inner, source);
                break;
            case Op.SMSG_UPDATE_INSTANCE_OWNERSHIP:
                ApplyInstanceOwnership(inner, source);
                break;
            case Op.SMSG_UPDATE_LAST_INSTANCE:
                ApplyLastInstance(inner, source);
                break;
            case Op.SMSG_RAID_GROUP_ONLY:
                ApplyRaidGroupOnly(inner, source);
                break;
            case Op.SMSG_OPEN_CONTAINER:
                ApplyServerContainerOpen(inner);
                break;
            case Op.SMSG_ITEM_TIME_UPDATE:
                ApplyItemTime(inner);
                break;
            case Op.SMSG_GAMEOBJECT_PAGETEXT:
                ApplyGameObjectPageText(inner, source);
                break;
            case Op.SMSG_PET_TAME_FAILURE:
            case Op.SMSG_PET_NAME_INVALID:
            case Op.SMSG_PET_BROKEN:
                ApplyPetNotice(innerOp, inner, source);
                break;
            case Op.SMSG_PET_ACTION_SOUND:
                ApplyPetActionSound(inner, source);
                break;
            case Op.SMSG_PET_ACTION_FEEDBACK:
                ApplyActorPetPacket(Op.SMSG_PET_ACTION_FEEDBACK, inner, source);
                break;
            case Op.SMSG_PET_CAST_FAILED:
                ApplyActorPetPacket(Op.SMSG_PET_CAST_FAILED, inner, source);
                break;
            // [SUI] taxi: the driven bot took (or was refused) a flight; the verdict left
            // on ITS session. The flight spline itself is a public monster-move on the
            // bot that ObserveServerRideSpline lets drive the controller while possessing.
            case Op.SMSG_ACTIVATETAXIREPLY:
                ApplyTaxiReply(inner);
                break;
            // A gossip "I need a ride" is answered by Player::OnGossipSelect on the
            // driven bot's session; the direct map query answers on this socket.
            case Op.SMSG_SHOWTAXINODES:
                ApplyTaxiNodes(inner);
                break;
            case Op.SMSG_TAXINODE_STATUS:
                ApplyTaxiNodeStatus(inner);
                break;
            case Op.SMSG_NEW_TAXI_PATH:
                ApplyNewTaxiPath(inner);
                break;
            // [SUI] The driven bot near-teleported (area-trigger portal, script): snap the
            // controller and ack for it; the server keeps the possession for same-map hops.
            case Op.MSG_MOVE_TELEPORT_ACK:
                if (_net is not null) ApplyMoveTeleportAck(_net, inner);
                break;
            // [SUI] The rest of the gossip-answered frames of the routed families: a
            // gossip pick runs as the driven bot server-side and answers on ITS session.
            case Op.SMSG_SHOW_BANK:
                ApplyShowBank(inner);
                break;
            case Op.MSG_LIST_STABLED_PETS:
                ApplyStableList(inner);
                break;
            case Op.MSG_TALENT_WIPE_CONFIRM:
                ApplyTalentWipeConfirm(inner);
                break;
            case Op.SMSG_BINDER_CONFIRM:
                ApplyBinderConfirm(inner, source);
                break;
            case Op.SMSG_PLAYERBOUND:
                ApplyPlayerBound(inner, source);
                break;
            case Op.MSG_AUCTION_HELLO:
                ApplyAuctionHello(inner);
                break;
            default:
                break;
        }
    }

    // ── SMSG_SUI_SNAPSHOT: read-only bags/talents (M4; member-facts widened) ──────────────────
    // The wire never streams owner-only fields to a non-owner; the snapshot is injected into
    // the bot's local WorldEntity fields + synthetic item entities so the already-parameterized
    // inventory/talent UI renders it unchanged. Originally possession-only; a member-facts
    // server also pushes it for non-possessed party/raid AiBots (GameLoop.MemberFacts.cs).

    // [SUI] Inventory snapshots are RETAINED per bot after release: possession is
    // the sync gesture, and the Party Inventory browser shows every synced
    // companion side-by-side stamped with its age. Re-possessing a bot replaces
    // its snapshot (its old synthetic items are purged first); session teardown
    // purges everything.
    private readonly Dictionary<ulong, List<WorldEntity>> _suiSnapshotItemsByBot = [];
    private readonly Dictionary<ulong, double> _suiSnapshotAtByBot = [];

    /// <summary>Seconds since this bot's last inventory snapshot; null = never synced.</summary>
    private double? SuiSnapshotAgeSeconds(ulong bot) =>
        _suiSnapshotAtByBot.TryGetValue(bot, out double at) ? NowSeconds() - at : null;

    private void ApplySuiSnapshot(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 18) return;
        ulong source = r.ReadU64();
        uint talentPoints = r.ReadU32();
        uint coinage = r.ReadU32();
        int count = r.ReadU16();
        // The snapshot is the ONLY way a bot's bags/gold/talents can exist client side
        // (the wire never streams owner-only fields to a non-owner), so a silent drop
        // here is indistinguishable from "the bot has nothing". Say which it was.
        // Accepted sources: the possessed bot (the original M4 wire), the session
        // player (the party-item-move server re-snapshots both endpoints), and any
        // party/raid member pushed by the member-facts wire. Rejecting the session
        // player while a bot was controlled left an item handed to the main player
        // invisible until another bag operation happened to update that slot.
        bool forControlled = source == ControlledGuid;
        bool forSessionPlayer = source == LocalPlayerGuid;
        if ((!forControlled && !forSessionPlayer && !IsPartyMemberFactsSubject(source)) ||
            !_entities.TryGet(source, out WorldEntity bot))
        {
            Console.WriteLine($"[sui] snapshot DROPPED for 0x{source:X} " +
                $"(controlled=0x{ControlledGuid:X}, party={IsPartyMemberFactsSubject(source)}, " +
                $"resident={_entities.TryGet(source, out _)}), " +
                $"{count} items, {coinage} copper");
            return;
        }
        Console.WriteLine($"[sui] snapshot for {ResolveUnitName(source)}: {count} items, " +
            $"{coinage / 10000}g{coinage % 10000 / 100}s{coinage % 100}c, {talentPoints} talent pts");

        PurgeSuiSnapshotFor(source);
        List<WorldEntity> snapshotItems = _suiSnapshotItemsByBot[source] = [];
        bot.Fields.SetU32(ObjectFields.PLAYER_CHARACTER_POINTS1, talentPoints);
        bot.Fields.SetU32(ObjectFields.PLAYER_COINAGE, coinage);

        // A snapshot REPLACES the carried inventory wholesale, so every slot guid
        // must be cleared before re-filing. These are owner-only fields the wire
        // never zeroes for a non-owner: a slot emptied server-side (an item SOLD
        // from a possessed bot's bag) kept its stale guid here, and once the
        // buyback section recreated that same item as a buyback entity, the
        // "sold" item visibly stayed in the bag until the next control swap.
        for (int slot = 0; slot <= 38; slot++)          // equipment, bag slots, backpack
            bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_INV_SLOT_HEAD + slot * 2), 0);
        // [SUI] Snapshot v4 (2026-09-03): bank items 39-62 and bank bags 63-68 ride
        // the same bag-255 rows (owner-only PLAYER_BANK_SLOT_* / PLAYER_BANK_BAG_SLOT_*
        // fields), so the BankFrame renders a driven bot's bank. Zeroed the same way.
        for (int slot = 39; slot <= 68; slot++)         // bank items, bank bag slots
            bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_INV_SLOT_HEAD + slot * 2), 0);
        for (int slot = 81; slot <= 96; slot++)         // keyring
            bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_INV_SLOT_HEAD + slot * 2), 0);
        int containersSized = 0;
        int baggedItems = 0, orphanedBagItems = 0;
        bool statsApplied = false;

        for (int i = 0; i < count && r.Remaining >= 19; i++)
        {
            byte bag = r.ReadU8();
            byte slot = r.ReadU8();
            ulong itemGuid = r.ReadU64();
            uint entry = r.ReadU32();
            uint stack = r.ReadU32();
            byte bagSlots = r.ReadU8();

            WorldEntity itemEntity;
            bool hasExisting = _entities.TryGet(itemGuid, out WorldEntity existing);
            bool existingIsSnapshot = hasExisting && _suiSnapshotItemsByBot.Values.Any(items =>
                items.Any(item => ReferenceEquals(item, existing)));
            if (hasExisting && !existingIsSnapshot &&
                existing.Type is ObjectTypeId.Item or ObjectTypeId.Container)
            {
                // The logged-in player may already have the authoritative item
                // entity from UPDATE_OBJECT. Keep its enchant/durability fields and
                // use the snapshot only to refresh ownership and stack state.
                itemEntity = existing;
                itemEntity.Entry = entry;
                itemEntity.Fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                itemEntity.Fields.SetU32(ObjectFields.ITEM_STACK_COUNT, stack);
                if (bagSlots > 0)
                    itemEntity.Fields.SetU32(ObjectFields.CONTAINER_NUM_SLOTS, bagSlots);
            }
            else
            {
                var fields = new ObjectFields().AsCreated();
                fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                fields.SetU32(ObjectFields.ITEM_STACK_COUNT, stack);
                if (bagSlots > 0)
                    fields.SetU32(ObjectFields.CONTAINER_NUM_SLOTS, bagSlots);
                itemEntity = new WorldEntity
                {
                    Guid = itemGuid,
                    Type = bagSlots > 0 ? ObjectTypeId.Container : ObjectTypeId.Item,
                    Entry = entry,
                    Fields = fields,
                };
                _entities.AddSynthetic(itemEntity);
                snapshotItems.Add(itemEntity);
            }
            // The bag UI sizes windows from CONTAINER_NUM_SLOTS and enumerates
            // contents with Math.Min(numSlots, 36) — a synthetic container
            // without the field reads 0 slots, so the items filed into its
            // CONTAINER_SLOT fields were never even looked at.
            if (bagSlots > 0)
            {
                containersSized++;
            }

            if (bag == 255)
            {
                // Character-held: equipment 0-18, bag slots 19-22, backpack 23-38, keyring 81+ —
                // one contiguous guid array from PLAYER_INV_SLOT_HEAD.
                bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_INV_SLOT_HEAD + slot * 2), itemGuid);
            }
            else
            {
                // Contents of an equipped bag; the bag row itself came earlier in the
                // stream. If it did NOT, the item cannot be filed anywhere and simply
                // vanishes -- indistinguishable from "the bag is empty", so count it.
                // Equipped bags sit at slots 19-22, bank bags at 63-68 (snapshot v4).
                ulong bagGuid = bag is >= 63 and <= 68
                    ? bot.Fields.PlayerBankBagSlot(bag - 63)
                    : bot.Fields.PlayerInventorySlot(bag);
                if (bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bagEntity))
                {
                    bagEntity.Fields.SetGuid((ushort)(ObjectFields.CONTAINER_SLOT_1 + slot * 2), itemGuid);
                    baggedItems++;
                }
                else orphanedBagItems++;
            }
            if (_net is not null) _items?.Require(entry, itemGuid, _net);
        }

        // ── Snapshot v2: the paper-doll stat block (optional trailing bytes). ──
        // UNIT_FIELD stats/resists/AP/damage are owner-only on the vanilla wire —
        // never streamed for another player — so a possessed bot's character
        // sheet rendered all zeros until the snapshot carried the raw values.
        // Attack times are stored floats on Core; normalize their raw snapshot bits to
        // the uint milliseconds used by ordinary object updates and field consumers.
        if (r.Remaining >= 19 * 4 + 6 * 4)
        {
            for (int i = 0; i < 5; i++)
                bot.Fields.SetU32((ushort)(ObjectFields.UNIT_STAT0 + i), r.ReadU32());
            for (int i = 0; i < 7; i++)
                bot.Fields.SetU32((ushort)(ObjectFields.UNIT_RESISTANCES + i), r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_ATTACK_POWER, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_ATTACK_POWER_MODS, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_RANGED_ATTACK_POWER, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_RANGED_ATTACK_POWER_MODS, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_BASEATTACKTIME, ObjectFields.StoredAttackTimeMilliseconds(r.ReadU32()));
            bot.Fields.SetU32((ushort)(ObjectFields.UNIT_BASEATTACKTIME + 1), ObjectFields.StoredAttackTimeMilliseconds(r.ReadU32()));
            bot.Fields.SetU32(ObjectFields.UNIT_RANGEDATTACKTIME, ObjectFields.StoredAttackTimeMilliseconds(r.ReadU32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MINDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MINOFFHANDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXOFFHANDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MINRANGEDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXRANGEDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            statsApplied = true;
        }

        // ── Snapshot v3: the buyback shelf (optional trailing bytes). ──────────
        // PLAYER_VENDOR_BUYBACK_SLOT/PRICE/TIMESTAMP are owner-only fields, and
        // the items they point at live outside any bag — so a possessed bot's
        // Merchant Buyback tab rendered empty while its bags worked. Zero all
        // twelve first: a re-snapshot after a buyback must retire the row, not
        // leave a ghost pointing at a repossessed item.
        for (int i = 0; i < 12; i++)
        {
            bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_VENDOR_BUYBACK_SLOT_1 + i * 2), 0);
            bot.Fields.SetU32((ushort)(ObjectFields.PLAYER_FIELD_BUYBACK_PRICE_1 + i), 0);
            bot.Fields.SetU32((ushort)(ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + i), 0);
        }
        int buybackApplied = 0;
        if (r.Remaining >= 1)
        {
            int buybackCount = r.ReadU8();
            for (int i = 0; i < buybackCount && r.Remaining >= 25; i++)
            {
                byte index = r.ReadU8();
                ulong itemGuid = r.ReadU64();
                uint entry = r.ReadU32();
                uint stack = r.ReadU32();
                uint price = r.ReadU32();
                uint timestamp = r.ReadU32();
                if (index >= 12 || itemGuid == 0 || entry == 0) continue;
                WorldEntity buybackItem;
                bool hasExisting = _entities.TryGet(itemGuid, out WorldEntity existing);
                bool existingIsSnapshot = hasExisting && _suiSnapshotItemsByBot.Values.Any(items =>
                    items.Any(item => ReferenceEquals(item, existing)));
                if (hasExisting && !existingIsSnapshot && existing.Type == ObjectTypeId.Item)
                {
                    buybackItem = existing;
                    buybackItem.Entry = entry;
                    buybackItem.Fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                    buybackItem.Fields.SetU32(ObjectFields.ITEM_STACK_COUNT, stack);
                }
                else
                {
                    var fields = new ObjectFields().AsCreated();
                    fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                    fields.SetU32(ObjectFields.ITEM_STACK_COUNT, stack);
                    buybackItem = new WorldEntity
                    {
                        Guid = itemGuid,
                        Type = ObjectTypeId.Item,
                        Entry = entry,
                        Fields = fields,
                    };
                    _entities.AddSynthetic(buybackItem);
                    snapshotItems.Add(buybackItem);
                }
                bot.Fields.SetGuid(
                    (ushort)(ObjectFields.PLAYER_VENDOR_BUYBACK_SLOT_1 + index * 2), itemGuid);
                bot.Fields.SetU32(
                    (ushort)(ObjectFields.PLAYER_FIELD_BUYBACK_PRICE_1 + index), price);
                bot.Fields.SetU32(
                    (ushort)(ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + index), timestamp);
                if (_net is not null) _items?.Require(entry, itemGuid, _net);
                buybackApplied++;
            }
        }

        // Version marker: this line existing at all proves the round-13 client is
        // running; its numbers say whether the wire carried the v2/v3 payloads.
        Console.WriteLine($"[sui] snapshot v2: stats={(statsApplied ? "applied" : "ABSENT")}, " +
            $"containers sized={containersSized}, bag items filed={baggedItems}, " +
            $"buyback rows={buybackApplied}" +
            (orphanedBagItems > 0
                ? $", ORPHANED={orphanedBagItems} (contents arrived before the bag row)"
                : ""));

        _suiSnapshotAtByBot[source] = NowSeconds();

        // Gear templates may have just resolved — rebuild the possessed body once
        // more. A member-facts snapshot for a non-possessed party member never
        // touches the driven body.
        if (forControlled) ApplyControlledCharacter();
    }

    private void PurgeSuiSnapshot()
    {
        foreach (List<WorldEntity> items in _suiSnapshotItemsByBot.Values)
            foreach (WorldEntity item in items)
                _entities.RemoveSynthetic(item);
        _suiSnapshotItemsByBot.Clear();
        _suiSnapshotAtByBot.Clear();
    }

    private void PurgeSuiSnapshotFor(ulong bot)
    {
        if (!_suiSnapshotItemsByBot.Remove(bot, out List<WorldEntity>? items)) return;
        foreach (WorldEntity item in items)
            _entities.RemoveSynthetic(item);
        _suiSnapshotAtByBot.Remove(bot);
    }

    /// <summary>
    /// Leave the Command View without a server ACK position: seat on the own character's
    /// streamed entity (or just drop the rig in place when it isn't resident).
    /// </summary>
    private void ExitFreeCamLocally()
    {
        SetFreeView(false);
        _controlState = ControlState.OwnChar;
        _controlTargetGuid = 0;
        if (_entities.TryGet(LocalPlayerGuid, out WorldEntity self))
            SeatControllerOnControlled(self.Position.X, self.Position.Y, self.Position.Z, self.Orientation);
        else
        {
            if (_controller is not null) _controller.Flying = false;
            if (_character is not null) _character.Enabled = true;
            _movementSender.Parked = false;
            AdoptControlledMovementModes();
            AdoptControlledSpeeds();
        }
        _net?.SetActiveMover(LocalPlayerGuid);
        EnterPlayerAuraWorld(LocalPlayerGuid);
        ApplyControlledCharacter();
    }

    /// <summary>
    /// Publish the controller's position into the entity of whichever unit it is DRIVING.
    ///
    /// Movement is client-authoritative for the unit you drive: the controller is the
    /// continuously updated truth, and that unit's object-store entity is only the last SERVER
    /// snapshot — the server does not echo your own mover's movement back to you, so it stays
    /// frozen at wherever the unit stood when you took control. It never shows while you are
    /// driving, because RenderSelfGuid excludes that unit from the streamed pass and the
    /// controller draws its body. The moment control hands off, it starts drawing from the
    /// stale entity and snaps back to the spot you picked it up at, then jumps forward again
    /// as soon as its AI emits a real move.
    ///
    /// Applies to ANY driven unit, not just the own character — releasing a bot you walked
    /// across the zone strands it the same way. Called at every hand-off, BEFORE the controller
    /// is re-seated onto someone else.
    /// </summary>
    private void SyncDrivenEntityToController()
    {
        // In the Command View the controller is a CAMERA and drives nobody. Writing its position
        // into a character would file that character in the sky — worse than the staleness.
        if (_freeView || _controller is null) return;
        // ONLY while the movement stream is actually live. Parked or flying means the server is
        // hearing nothing from us, so ITS position is the truth and ours is local fiction —
        // publishing anyway would paint the fiction over the fact and hide a desync behind a
        // selection ring that follows you perfectly while the character has not moved at all.
        if (_movementSender.Parked || _controller.Flying) return;
        if (!_entities.TryGet(ControlledGuid, out WorldEntity driven)) return;
        driven.Position = _controller.Position;
        driven.Orientation = _controller.Yaw;
        // A control/free-view hand-off also changes who owns transport composition. Preserve
        // the embodied mover's rider-local tail on the streamed entity before the controller
        // becomes an observer rig; otherwise ReconcileControlledTransportRider clears its
        // private cache and a NEW_WORLD boat seam has no honest local frame to compose.
        driven.Transport = _controller.Transport;
    }

    /// <summary>
    /// Snap the local, client-authoritative controller onto the ACK position.
    ///
    /// NO-OP ON THE CAMERA IN THE Command View, and that is the whole mechanism behind
    /// "possessing from the sky does not land you": every possess/release path funnels
    /// through here, so guarding it once keeps the fly rig, the parked movement stream
    /// and the hidden first-person body intact no matter which of them fired. The
    /// portrait bakes still have to be invalidated — the HUD identity did change.
    /// </summary>
    /// <summary>Same guarantee the far same-map teleport gives (GameLoop.Net.cs
    /// MSG_MOVE_TELEPORT_ACK): a non-resident arrival tears the scene down and loads the
    /// destination tile behind the curtain; a resident one needs nothing.</summary>
    private void PrepareFarControlArrival(Vector3 arrival)
    {
        if (_gl is null || MainWorldHasArrivalSupport(arrival)) return;
        AbortServerRideForTeleport();
        TearDownWorldContent();
        _residentCentre = null;
        _hitch.SuppressFor(5.0);
        _window.Camera.EffectiveDistance = _window.Camera.Distance;
        _config.Start.X = arrival.X;
        _config.Start.Y = arrival.Y;
        _config.Start.Z = arrival.Z;
        try
        {
            BeginWorldLoad(_gl);
            var tile = MSUIClient.World.TerrainRenderer.TileAt(arrival.X, arrival.Y);
            Console.WriteLine($"[sui] far control grant is non-resident; loading tile [{tile.col},{tile.row}] behind the curtain");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sui] far control grant: world loader failed: {ex.Message}");
        }
    }

    private void SeatControllerOnControlled(float x, float y, float z, float o)
    {
        ResetControlledHardLandingArc();
        if (_freeView)
        {
            _freecamRequested = false;
            // The server tears the streaming eye down on possess and rebuilds it on the next
            // CMSG_SUI_CAM. Force that heartbeat out on the very next frame rather than
            // waiting up to 2 s for the idle cadence, so the gap where nothing streams the
            // world in around the camera closes immediately.
            _freecamCamSentAt = 0;
            _playerPortraitDirty = true;
            _paperDollDirty = true;
            return;
        }
        _movementSender.Parked = false;
        _freecamRequested = false;
        // A control hand-off always seats you RUNNING. The walk toggle is easy
        // to flip unnoticed around control chords (Slash sits next to the
        // Shift/Ctrl cluster in use during control jumping), and a toggle left
        // on sticks invisibly to the next body.
        _walkToggled = false;
        _autorunToggled = false;
        if (_controller is not null) _controller.Flying = false;   // exits the free-view fly rig
        AdoptControlledMovementModes();
        AdoptControlledSpeeds();
        if (_character is not null) _character.Enabled = true;
        _controller?.Teleport(x, y, z);
        if (_controller is not null) _controller.Yaw = o;
        _window.Camera.Yaw = o;
        _window.Camera.OrbitYaw = 0f;
        if (_controller is not null) _window.Camera.Target = _controller.Position;
        _movementSender.Reset(o);
        _playerPortraitDirty = true;
        _paperDollDirty = true;
    }

    /// <summary>
    /// Ask the server for control of a bot (portrait Alt+click / cycle key /
    /// explicit Command View Alt+click on a server-advertised same-faction bot).
    /// </summary>
    internal void RequestPossess(ulong guid)
    {
        if (_net is not { IsInWorld: true } || _controller is null) return;
        if (guid == 0 || guid == LocalPlayerGuid) return;
        if (_controlState is not (ControlState.OwnChar or ControlState.FreeCam)) return;
        // This is the final possession send edge. Callers also disable their affordances, but an
        // already-open popup, a queued switch, or a dev command must not bypass the lock/drain law.
        if (RefuseTacticalFreezeLiveCommand("changing control")) return;
        if (RefuseTacticalFrozenActor(guid, "take control")) return;
        // Flush a MSG_MOVE_STOP at the own character's position BEFORE the request so no
        // in-flight movement straggles into the mover swap, then park the stream. (From
        // the Command View the stream is already silent — flags are clear, nothing flushes.)
        _movementSender.ParkForRoot(_net, _controller);
        _movementSender.Parked = true;
        _controlPendingReturn = _controlState;
        _controlState = ControlState.PossessPending;
        _controlPendingSince = NowSeconds();
        _net.SuiControlRequest(guid);
    }

    /// <summary>
    /// RTS-only force control. Same-map targets first move the streaming eye and
    /// wait for an entity create. Outdoor cross-map targets let the server move
    /// the owner's body, then follow the same stream-before-retry law after NEW_WORLD.
    /// </summary>
    private void BeginRtsForceTakeControl(RtsForceUnitWire unit)
    {
        // Refuse before closing the commander map, moving its camera, or arming the delayed
        // stream-then-possess state. Otherwise a click made during drain can fire after DRAINED.
        if (RefuseTacticalFreezeLiveCommand("changing control") ||
            RefuseTacticalFrozenActor(unit.Guid, "take control"))
            return;
        if (!CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules))
        {
            CommanderShowNotice("Faction-force control is disabled in this world.");
            return;
        }
        if (_net is not { IsInWorld: true } || !_freeView ||
            _controlState is not (ControlState.OwnChar or ControlState.FreeCam))
        {
            CommanderShowNotice("Take Control requires the free camera with no request pending.");
            return;
        }
        if (!unit.Alive || unit.Busy)
        {
            CommanderShowNotice(!unit.Alive ? "That bot is dead." : "That bot is already controlled.");
            return;
        }
        if (unit.InstanceableMap && !unit.SameMapAndInstance)
        {
            CommanderShowNotice("That bot is in a different instance.");
            return;
        }
        if (!unit.ControlEligibleNow)
        {
            CommanderShowNotice("That bot cannot be controlled right now.");
            return;
        }

        _rtsForceTakeControlGuid = unit.Guid;
        _rtsForceTakeControlMap = unit.MapId;
        _rtsForceTakeControlAt = NowSeconds();
        _commanderMapOpen = false;

        uint currentMap = checked((uint)Math.Max(0, _config.Start.Map));
        if (unit.MapId == currentMap)
        {
            _rtsForceTakeControlPhase = RtsForceTakeControlPhase.AwaitingStream;
            CommanderFlyTo(unit.Position.X, unit.Position.Y, CommanderUnitAltitude);
            CommanderShowNotice("Locating faction bot; control begins when its body streams in.");
            return;
        }

        _rtsForceTakeControlPhase = RtsForceTakeControlPhase.AwaitingRelocationAck;
        RequestPossess(unit.Guid);
        if (_controlState != ControlState.PossessPending)
        {
            ClearRtsForceTakeControl();
            CommanderShowNotice("Take Control could not start.");
        }
    }

    private void ClearRtsForceTakeControl()
    {
        _rtsForceTakeControlGuid = 0;
        _rtsForceTakeControlMap = 0;
        _rtsForceTakeControlPhase = RtsForceTakeControlPhase.None;
        _rtsForceTakeControlAt = 0;
    }

    private void UpdateRtsForceTakeControl()
    {
        if (_rtsForceTakeControlGuid == 0) return;
        if (NowSeconds() - _rtsForceTakeControlAt > RtsForceTakeControlTimeoutSeconds)
        {
            ClearRtsForceTakeControl();
            CommanderShowNotice("Take Control timed out while locating that bot.");
            return;
        }
        if (_rtsForceTakeControlPhase != RtsForceTakeControlPhase.AwaitingStream ||
            _net is not { IsInWorld: true } ||
            checked((uint)Math.Max(0, _config.Start.Map)) != _rtsForceTakeControlMap ||
            !_entities.TryGet(_rtsForceTakeControlGuid, out WorldEntity target) || !target.IsPlayer)
            return;

        ulong guid = _rtsForceTakeControlGuid;
        _rtsForceTakeControlPhase = RtsForceTakeControlPhase.AwaitingFinalAck;
        _rtsForceTakeControlAt = NowSeconds();
        RequestPossess(guid);
        if (_controlState != ControlState.PossessPending)
        {
            ClearRtsForceTakeControl();
            CommanderShowNotice("Take Control could not be requested after streaming.");
        }
    }

    /// <summary>Give control back (cycle to self). The bot's AI resumes server-side.</summary>
    internal void RequestControlRelease(bool toFreecam)
    {
        if (_net is null || _controller is null) return;
        if (_controlState != ControlState.Possessing) return;
        // Command View exit has its own ordered owner-thaw path below. Every other release is a
        // control handoff and is refused at the actual packet edge while frozen or draining.
        if (RefuseTacticalFreezeLiveCommand("changing control")) return;
        _movementSender.ParkForRoot(_net, _controller);   // stops the bot dead at its position
        _movementSender.Parked = true;
        _controlState = ControlState.ReleasePending;
        _controlPendingReturn = ControlState.Possessing;
        _controlPendingSince = NowSeconds();
        _freecamRequested = toFreecam;
        // Mode 1 means "I am NOT taking my body back". Releasing while the Command View is up is
        // exactly that, so it must go out as 1 even though the caller only asked to release:
        // SuiPossess::DoRelease answers mode 0 by running DetachUnattendedAI on the own
        // character and RemoveFreecamEye on the session. That is what left the abandoned
        // character with no AI to obey RTS orders — the client said "move to", the server had
        // nobody listening — and killed the streaming eye out from under the camera.
        SendSuiControlRelease(toFreecam || _freeView ? (byte)1 : (byte)0,
            commandViewExit: false);
    }

    /// <summary>
    /// The only release exception while Tactical Freeze owns live authorship: lowering Command
    /// View. The caller first requests the owner's authoritative thaw; retries stay on this same
    /// exit-only seam. Opening Command View or switching bodies never uses the exception.
    /// </summary>
    private bool SendSuiControlRelease(byte mode, bool commandViewExit)
    {
        if (_net is null) return false;
        if (!commandViewExit)
        {
            if (RefuseTacticalFreezeLiveCommand("changing control")) return false;
        }
        else
        {
            if (!_freeView || !TacticalCommandViewExitAuthorizationValid())
            {
                RefuseTacticalFreezeLiveCommand("leaving Command View");
                return false;
            }
        }
        return _net.SuiControlRelease(mode);
    }

    /// <summary>
    /// Ctrl+F: raise/lower the CRPG Command View. From the own character or a possessed bot
    /// the server keeps/attaches the unattended AI (release mode 1); from the Command View a
    /// plain release (mode 0) returns to manual control of the own character.
    /// </summary>
    /// <summary>Where the creator-sandbox character stood when the Command View went
    /// up, so the way down is an exact return — the offline twin of the live
    /// path's release-ACK teleport home.</summary>
    private Vector3 _creatorFreeViewReturn;
    private float _creatorFreeViewReturnYaw;
    private bool _creatorFreeViewWasFlying;

    private void ToggleFreeView()
    {
        if (_controller is null) return;

        // Creator sandbox: no server, no control stream to park or release — the
        // Command View is PURELY a camera decision. Seat the rig where the character
        // stands on the way up; put the character back exactly there on the way
        // down. Everything else (marquee, edge pan, wheel-fly, encounter-raid
        // orders) is already client-side and just works.
        if (_net is not { IsInWorld: true })
        {
            if (!CreatorInWorld) return;
            if (_freeView)
            {
                SetFreeView(false);
                _controller.Flying = _creatorFreeViewWasFlying;
                _controller.Teleport(_creatorFreeViewReturn.X, _creatorFreeViewReturn.Y,
                    _creatorFreeViewReturn.Z);
                _controller.Yaw = _creatorFreeViewReturnYaw;
            }
            else
            {
                _creatorFreeViewReturn = _controller.Position;
                _creatorFreeViewReturnYaw = _controller.Yaw;
                _creatorFreeViewWasFlying = _controller.Flying;
                SetFreeView(true);
                // Rise so the first frame already reads as the sky rig.
                _controller.Teleport(_controller.Position.X, _controller.Position.Y,
                    _controller.Position.Z + 18f);
            }
            return;
        }

        // Freeze is a button state inside Command View, not Command View itself. When the owner
        // lowers the camera, ask to thaw before the ordinary control-release packet. Neither send
        // mutates local membership; the authoritative snapshot/session teardown does that.
        bool exitingCommandView = _freeView;
        if (exitingCommandView)
        {
            if (!PrepareTacticalCommandViewExit()) return;
        }
        else if (RefuseTacticalFreezeLiveCommand("opening Command View")) return;

        // Already commanding a toon from the sky: Ctrl+F is purely a camera decision, so it
        // just lands on the unit whose bars are already live. No release, no server round
        // trip — you keep driving what you were already driving.
        if (_freeView && _controlState == ControlState.Possessing)
        {
            SetFreeView(false);
            if (_entities.TryGet(_controlTargetGuid, out WorldEntity bot))
                SeatControllerOnControlled(bot.Position.X, bot.Position.Y, bot.Position.Z,
                    bot.Orientation);
            else
            {
                if (_controller is not null) _controller.Flying = false;
                if (_character is not null) _character.Enabled = true;
                _movementSender.Parked = false;
                AdoptControlledMovementModes();
                AdoptControlledSpeeds();
            }
            ApplyControlledCharacter();
            AddChatMessage($"You take control of {ResolveUnitName(_controlTargetGuid)}.");
            return;
        }

        switch (_controlState)
        {
            case ControlState.OwnChar:
                _movementSender.ParkForRoot(_net, _controller);
                _movementSender.Parked = true;
                _controlState = ControlState.ReleasePending;
                _controlPendingReturn = ControlState.OwnChar;
                _controlPendingSince = NowSeconds();
                _freecamRequested = true;
                if (!SendSuiControlRelease(1, commandViewExit: exitingCommandView))
                {
                    _controlState = ControlState.OwnChar;
                    _movementSender.Parked = false;
                }
                break;
            case ControlState.Possessing:
                RequestControlRelease(toFreecam: true);
                break;
            case ControlState.FreeCam:
                // No pending ControlState: we stay in the fly rig until the ACK teleports us
                // home. That means nothing local changes when this is sent, so an ACK that
                // never arrives is indistinguishable from a keypress that never registered -
                // which is exactly what it looked like. Stamped and watchdogged below; a
                // fresh press is a fresh request, so the retry budget starts over.
                _freeViewExitRequested = true;
                _freeViewExitRequestedAt = NowSeconds();
                _freeViewExitAttempts = 0;
                if (!SendSuiControlRelease(0, commandViewExit: true))
                {
                    _freeViewExitRequested = false;
                    _freeViewExitAttempts = 0;
                }
                break;
        }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab cycles control across [own character + controllable party
    /// bots], hard-coded like the F fly toggle. Runs every frame from the input chain.
    /// Also watches the pending-state ACK timeout (server without SUI support, packet loss).
    /// </summary>
    private void UpdateControlInput(bool typing)
    {
        RefreshControlledCharacterScale();
        // The cast bar and the local pending-cast lock belong to whoever was controlled when
        // the cast started; a body switch retires them (GameLoop.Casting.cs).
        ResetCastStateOnControlChange();
        UpdateRtsForceTakeControl();
        UpdateRtsControlGroups(typing);
        UpdatePartyMemberFacts();
        UpdatePartyQuestFacts();

        // Pending-state watchdog: never strand the movement stream parked.
        if (_controlState is ControlState.PossessPending or ControlState.ReleasePending &&
            NowSeconds() - _controlPendingSince > ControlAckTimeoutSeconds)
        {
            _controlState = _controlPendingReturn;
            _movementSender.Parked = false;
            _controlSwitchQueued = 0;
            _freecamRequested = false;
            ClearRtsForceTakeControl();
            ShowUiError("No answer from the server (SUI control).");
        }

        // THE COMMAND VIEW EXIT NEEDS ITS OWN WATCHDOG, because it is the one transition that
        // does not park input or enter a pending ControlState - so the block above can never
        // cover it. Ctrl+F out of the sky rig sends CMSG_SUI_CONTROL_RELEASE(0) and changes
        // nothing locally until the ACK comes back; if that ACK is late or lost the client sits
        // in FreeCam forever, and every further press silently re-sends into the same hole.
        // Reported 2026-09-01: "in big battles I can't Ctrl+F back to direct control, nothing
        // happens" - which is the shape of an ACK starved by the same flood the order path
        // already struggles with, not of a missed keypress.
        //
        // Re-ask before giving up: a dropped packet deserves another go, and the release is
        // idempotent server-side (a second one on an already-released session is a no-op).
        if (_freeViewExitRequested && _net is not null &&
            NowSeconds() - _freeViewExitRequestedAt > ControlAckTimeoutSeconds)
        {
            if (_freeViewExitAttempts < FreeViewExitMaxAttempts)
            {
                _freeViewExitAttempts++;
                _freeViewExitRequestedAt = NowSeconds();
                SendSuiControlRelease(0, commandViewExit: true);
                Console.WriteLine("[control] command-view exit unanswered after " +
                    $"{ControlAckTimeoutSeconds:F0}s - re-asking " +
                    $"({_freeViewExitAttempts}/{FreeViewExitMaxAttempts})");
            }
            else
            {
                // Give the key back rather than leaving it dead. The server owns where the
                // body is, so forcing a local exit would desync; say so and let Ctrl+F retry.
                _freeViewExitRequested = false;
                _freeViewExitAttempts = 0;
                Console.WriteLine("[control] command-view exit ABANDONED after " +
                    $"{FreeViewExitMaxAttempts} attempt(s) - still in the sky rig");
                ShowUiError("No answer from the server (SUI control).");
            }
        }

        // A possess granted before the bot's entity streamed in leaves the body
        // rebuild pending; retry until the fields are resident.
        if (_controlledBodyPending && _controlState == ControlState.Possessing)
            ApplyControlledCharacter();

        UpdateFreeCamSelection();
        FlushPendingRtsMoveOrder();   // deliver the newest coalesced move once its throttle elapses

        // Four ordinary bindings now (CRPG Controls / RTS Controls), defaulting to exactly the
        // chords that used to be hard-coded here: Ctrl+Tab and Ctrl+Shift+Tab cycle which BODY
        // you drive, Q and Shift+Q cycle the command-card PRIMARY strictly through the current
        // selection. The card cycle only runs while Command View is up.
        //
        // All four edges are taken unconditionally: BindingPressedEdge owns the was-down state,
        // so a frame that skips the call would let a key held across it fire again on release.
        bool cycleNext = BindingPressedEdge(GameBinding.CrpgCycleControlNext, typing);
        bool cyclePrevious = BindingPressedEdge(GameBinding.CrpgCycleControlPrevious, typing);
        if ((cycleNext || cyclePrevious) && _net is { IsInWorld: true })
            CycleControl(cyclePrevious ? -1 : +1);

        bool cardNext = BindingPressedEdge(GameBinding.RtsCyclePrimaryNext, typing);
        bool cardPrevious = BindingPressedEdge(GameBinding.RtsCyclePrimaryPrevious, typing);
        if (_freeView && (cardNext || cardPrevious))
            CycleRtsPrimary(cardPrevious ? -1 : +1);

        // Ctrl+A (RTS Controls: Select Whole Party): the whole party becomes the selection,
        // exactly as a marquee drawn over all of them would. Command View only.
        if (BindingPressedEdge(GameBinding.RtsSelectAllParty, typing) && _freeView)
            SelectAllPartyMembers();

        if (BindingPressedEdge(GameBinding.RtsLockCameraPrimary, typing) && _freeView)
            ToggleCommandViewLock();

        if (BindingPressedEdge(GameBinding.RtsFocusPrimary, typing))
        {
            Console.WriteLine($"[RTS] Focus binding fired freeView={_freeView}");
            if (_freeView)
                FocusRtsPrimaryCamera();
        }

        // Possess-on-cast / possess-on-use / possess-on-open: fire a queued command-card ability,
        // quick-slot item, or bag window once control lands on the primary.
        TryFirePendingPrimaryCast();
        TryFirePendingPrimaryUse();
        TryFirePendingPrimaryBags();
        UpdateControlledInventoryRefresh();   // re-sync a possessed bot's bags after a consumable use

        // Command View toggle, Ctrl+F by default (plain F stays the local fly toggle — Program.cs
        // asks THIS binding whether the press was already spoken for, so a rebind moves both
        // halves together). Works in the creator sandbox too: there it is purely a camera
        // decision, and it is how the Encounter Lab's raid gets commanded.
        if (BindingPressedEdge(GameBinding.RtsToggleFreeView, typing) &&
            (_net is { IsInWorld: true } || CreatorInWorld))
            ToggleFreeView();

        // Ctrl+N: the NPC dev window (spawn/pathing/aggro overlays). Same edge
        // pattern; no in-world gate so it also opens in creator mode.
        UpdateDevWindowInput(typing);
        UpdateEncounterLabInput(typing);
    }

    /// <summary>
    /// Free-view marquee lifecycle, run every frame from the input chain. The window's
    /// FreeSelectMode keeps the LEFT button out of camera look while the Command View is up,
    /// so a left-drag over the world becomes the RTS selection rectangle. The queued
    /// world click that the release still produces is swallowed via
    /// <see cref="_freecamMarqueeConsumedClick"/> (drag travel never accumulates when the
    /// mouse isn't captured, so the window classifies the release as a click).
    /// </summary>
    private void UpdateFreeCamSelection()
    {
        _window.FreeSelectMode = _freeView;
        if (!_freeView)
        {
            SyncManualPrimary();              // hands the last primary back to its AI
            _cvMirroredTarget = 0;
            _window.LookPitchLocked = false;
            _cvSmoothedTarget = null;         // next Command View starts on the rig, no glide-in
            _cvFollowGuid = 0;
            _cvFollowRigLast = null;
            _window.TakeFreeFlightScroll();   // discard a leftover final-frame wheel tick
            _freecamDragOrigin = null;
            _freecamDragActive = false;
            _freecamMarqueeConsumedClick = false;
            _freecamSelection.Clear();
            CancelRtsUnitCastTargeting(silent: true);
            ClearRtsWaypointChain();
            ClearRtsAttackQueue();
            CancelRtsPatrolAuthoring(silent: true);
            _commanderMapOpen = false;      // the commander map is a free-view surface only
            _freecamPanAt = 0;
            _rtsWheelRetreatYards = 0;
            if (_controller is not null)
            {
                _controller.FlyFloorClearance = null;
                _controller.FlyCollide = false;
            }
            return;
        }

        EnforceStickyLock();

        // Re-assert the rig every frame rather than only on entry: a possess from the sky
        // runs ApplyControlledCharacter, which puts the controller back on the ground.
        // _character.Enabled is deliberately NOT touched — the world pass skips the body on
        // _freeView instead, so the portrait booth (same Render method) keeps working.
        if (_controller is not null)
        {
            _controller.Flying = true;
            // The RTS camera never sinks beneath the map; plain F fly stays unclamped.
            _controller.FlyFloorClearance = 2f;
            // Command View scheme law (Interface Options → Command View): under the knob
            // schemes the mouse may not tilt the view, and the knob's angle is re-asserted
            // every frame — a possess-from-the-sky or a tilt in first person cannot leak in.
            // Cut plane up: the rig stays above the cut, so the camera never looks up at the
            // sliced faces from underneath (the "weird underneath" view).
            if (_wmo?.ActiveCut is WorldCut activeCut &&
                _controller.Position.Z < activeCut.RigFloor(_window.Camera.Distance, _window.Camera.Pitch))
                _controller.Position = new Vector3(_controller.Position.X, _controller.Position.Y,
                    activeCut.RigFloor(_window.Camera.Distance, _window.Camera.Pitch));
            CommandViewScheme scheme = Settings.Controls.CommandViewScheme;
            bool pitchLocked = CommandViewLaw.PitchLocked(scheme);
            _window.LookPitchLocked = pitchLocked;
            if (pitchLocked)
                _window.Camera.Pitch = CommandViewLaw.PitchRadians(
                    Settings.Controls.CommandViewPitchDegrees);
            // The round-15 floating-body camera (walls/ceilings stop the rig) is OFF:
            // detaching under any WMO geometry — indoors, gate arches, city overhangs —
            // hit an invisible "fake ceiling" (owner, 2026-08-11 evening). The rig is a
            // ghost again. Hard false, not the saved setting: the old default persisted
            // `true` into settings files, and honoring it would keep the ceiling.
            _controller.FlyCollide = false;
        }

        // RTS altitude on the wheel: fly the RIG along the camera look direction
        // (wheel up = toward what you look at, wheel down = pull back and away).
        // The detached rig has its own ceiling: without one repeated wheel-down
        // steps can retreat forever even though the ordinary orbit boom is capped.
        // Steps scale with altitude like the edge pan, and go through FlyMove so
        // the wheel cannot ghost through what the keys cannot.
        // While the commander map is up the mouse belongs to the map: no rig
        // wheel-fly, no edge pan, no marquee. The heartbeat below keeps running —
        // the map's click-to-fly depends on it (TakeFreeFlightScroll still runs
        // so a wheel tick over the map is consumed, not banked for landing).
        // Interface Options -> Command View -> Zoom speed scales every wheel tick here: the
        // rig's fly / elevator step and the boom zoom alike (owner feedback 2026-09-03: the
        // wheel was far too sensitive in the Command View and Alt was only a workaround).
        float wheel = _window.TakeFreeFlightScroll() * Settings.Controls.CommandViewZoomSpeed;
        // Alt+wheel zooms the orbit boom instead of flying the rig. Command View otherwise freezes
        // the boom at whatever distance it held on entry - plain wheel is spent on altitude, and
        // the CAMERAZOOMIN/OUT binding is gated off while Command View is up - so without this you
        // can never pull in closer than the distance you toggled in at. Wheel up (positive) =
        // zoom in, matching normal camera mode, under the same Min/MaxDistance clamp.
        // Which wheel command owns this tick is a BINDING question now (RTS Controls: Commander
        // Zoom In/Out, Fly Camera Forward/Back). The chords are matched exactly against the
        // modifiers held beside the tick — the Command View spends its wheel through
        // ClientWindow.TakeFreeFlightScroll, a different accumulator from the one the global
        // latch pulses, so this cannot go through BindingDown.
        BindingPointerKey wheelDirection = wheel > 0f
            ? BindingPointerKey.WheelUp : BindingPointerKey.WheelDown;
        bool boomZoom = BindingClaimsPointerNow(wheel > 0f
            ? GameBinding.RtsBoomZoomIn : GameBinding.RtsBoomZoomOut, wheelDirection);
        bool rigFly = BindingClaimsPointerNow(wheel > 0f
            ? GameBinding.RtsRigForward : GameBinding.RtsRigBackward, wheelDirection);
        if (wheel != 0f && !_commanderMapOpen && boomZoom)
        {
            _window.Camera.Zoom(wheel);
        }
        else if (wheel != 0f && !_commanderMapOpen && CommandViewLocked)
        {
            // Locked on the primary: the rig may not leave it, so the wheel works the boom.
            _window.Camera.Zoom(wheel);
        }
        else if (wheel != 0f && !_commanderMapOpen && rigFly && _controller is not null)
        {
            // Pulling toward the battlefield also shortens the orbit boom. Previously the
            // boom stayed frozen at its pre-RTS distance, so moving the rig forward could
            // never get the view closer than the zoom level used before Ctrl+F.
            if (wheel > 0f) _window.Camera.Zoom(wheel);
            // Under a terrain shell (Ironforge, Undercity, a cave) the height field is OVERHEAD,
            // so "Z minus terrain" is a negative number and this collapsed to its 2-yard floor -
            // the wheel crawled the moment you raised the Command View indoors. Keep the mid-altitude
            // fallback there, exactly as when the sample misses entirely.
            float? ground = _controller.GroundZ;
            if (ground is null && !_controller.UnderTerrainShell)
                ground = _terrain?.SampleHeight(_controller.Position.X, _controller.Position.Y);
            float altitude = ground is float floor
                ? MathF.Max(2f, _controller.Position.Z - floor) : 10f;
            float step = Math.Clamp(altitude * 0.30f, 2.5f, 40f);
            // RTS scheme: the wheel is an elevator (wheel up = descend toward the field), not a
            // flight along the view — Warcraft's zoom, with the view angle left to the knob.
            Vector3 delta = CommandViewLaw.WheelIsVertical(Settings.Controls.CommandViewScheme)
                ? new Vector3(0f, 0f, -wheel * step)
                : _window.Camera.Forward * (wheel * step);
            if (wheel < 0f)
            {
                // Also cap cumulative wheel retreat. This closes the horizontal-look and
                // missing-ground cases where an altitude-only ceiling could still retreat
                // forever without gaining Z.
                float retreatRemaining = RtsMaxWheelAltitudeYards - _rtsWheelRetreatYards;
                float requested = delta.Length();
                if (retreatRemaining <= 0f)
                    delta = Vector3.Zero;
                else if (requested > retreatRemaining)
                    delta *= retreatRemaining / requested;

                if (ground is not null)
                {
                    float altitudeRemaining = RtsMaxWheelAltitudeYards - altitude;
                    if (altitudeRemaining <= 0f)
                        delta = Vector3.Zero;
                    else if (delta.Z > altitudeRemaining)
                        delta *= altitudeRemaining / delta.Z;
                }
            }
            if (delta != Vector3.Zero)
            {
                _controller.FlyMove(delta);
                if (wheel < 0f) _rtsWheelRetreatYards += delta.Length();
                else _rtsWheelRetreatYards = MathF.Max(0f,
                    _rtsWheelRetreatYards - delta.Length());
            }
        }

        if (!_commanderMapOpen && Settings.Controls.CommandViewEdgePan && !CommandViewLocked)
            UpdateFreeCamEdgePan();
        UpdateRtsWaypointProgress();
        UpdateRtsAttackQueue();

        // Keep the server's streaming eye under the camera: heartbeat every 2 s, and
        // whenever the rig has flown more than a few yards since the last send.
        if (_controller is not null)
        {
            double now = NowSeconds();
            Vector3 rig = _controller.Position;
            if (now - _freecamCamSentAt > 2.0 ||
                Vector3.DistanceSquared(rig, _freecamCamSentPosition) > 5f * 5f)
            {
                if (_net?.SuiCam(rig.X, rig.Y, rig.Z) == true)
                {
                    _freecamCamSentPosition = rig;
                    _freecamCamSentAt = now;
                }
            }
        }
        SyncManualPrimary();   // after the cam heartbeat: the server attaches your body's AI on it
        UpdateCommandViewPendingInteraction();
        MirrorPrimaryTarget();

        bool leftDown = _window.MouseLeftDown;
        Vector2 mouse = _window.MousePosition;

        // Right-hold + WASD flies the camera; the release must not become an order. The window
        // only counts MOUSE travel for click-vs-drag, so latch a pan flag (reset on each fresh
        // right-press) that HandleFreeCamWorldClick consults - the keyboard analogue of how a
        // mouse-rotate already disqualifies the click by accumulating travel.
        bool rightDown = _window.MouseRightDown;
        if (rightDown && !_freecamRightWasDown) _freecamRightPanned = false;
        if (rightDown && IsMoving()) _freecamRightPanned = true;
        _freecamRightWasDown = rightDown;

        if (leftDown && _freecamDragOrigin is null && !_commanderMapOpen && !_hudEditMode &&
            _rtsUnitCastSpellId == 0 &&
            !_window.MouseCaptured && !ImGui.GetIO().WantCaptureMouse)
            _freecamDragOrigin = mouse;

        if (leftDown && !_freecamDragActive && _freecamDragOrigin is Vector2 origin &&
            (mouse - origin).Length() > FreecamDragThresholdPixels)
            _freecamDragActive = true;

        if (!leftDown && _freecamDragOrigin is Vector2 anchor)
        {
            if (_freecamDragActive)
            {
                CommitMarqueeSelection(anchor, mouse);
                _freecamMarqueeConsumedClick = true;
            }
            _freecamDragOrigin = null;
            _freecamDragActive = false;
        }
    }

    /// <summary>
    /// Retire chain legs as the party walks them. The route is drawn from
    /// <see cref="_rtsWaypointChain"/>, and the server consumes its own copy on arrival, so
    /// without this the dots and the dashed line stayed on screen over a party standing at
    /// the end of the route it had already finished. The client cannot see the server's
    /// arrival callback, so it watches the same thing the eye does: the leading leg is spent
    /// once any ordered subject stands on it.
    /// </summary>
    private void UpdateRtsWaypointProgress()
    {
        if (_rtsWaypointChain.Count == 0) return;

        while (_rtsWaypointChain.Count > 0)
        {
            Vector3 leg = _rtsWaypointChain[0];
            bool reached = false;
            foreach (ulong guid in _rtsWaypointSubjects.Count > 0
                         ? _rtsWaypointSubjects : FreeCamSelectableGuids())
            {
                if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
                // Horizontal only: a member on the abbey steps is at the waypoint even
                // though its Z is a storey off.
                float dx = unit.Position.X - leg.X, dy = unit.Position.Y - leg.Y;
                if (dx * dx + dy * dy <= RtsWaypointReachedYards * RtsWaypointReachedYards)
                { reached = true; break; }
            }
            if (!reached) break;
            _rtsWaypointChain.RemoveAt(0);
            _rtsWaypointProgressAt = NowSeconds();
        }

        // Nobody is coming (stuck path, dead subject, order overridden server-side). Drop the
        // route rather than leaving a permanent decoration on the ground.
        if (_rtsWaypointChain.Count > 0 &&
            NowSeconds() - _rtsWaypointProgressAt > RtsWaypointStaleSeconds)
            ClearRtsWaypointChain();
    }

    private void ClearRtsWaypointChain()
    {
        _rtsWaypointChain.Clear();
        _rtsWaypointSubjects.Clear();
        _rtsWaypointProgressAt = 0;
    }

    private void ClearRtsAttackQueue()
    {
        _rtsAttackQueue.Clear();
        _rtsAttackSubjects.Clear();
        _rtsAttackIssuedAt = 0;
    }

    /// <summary>Append a hostile to the current selection's kill sequence. The first target is
    /// issued immediately; later entries wait until the active target dies or despawns.</summary>
    private void QueueRtsAttack(List<ulong> subjects, ulong target)
    {
        // Refuse before mutating the local retry queue. In particular, an owned plan can be
        // draining after its pose lock has thawed; stashing an unissued target in that interval
        // would otherwise let UpdateRtsAttackQueue send it as soon as DRAINED reopens authorship.
        if (RefuseTacticalFreezeLiveCommand("issuing live attack orders")) return;
        if (RefuseTacticalFrozenActors(TacticalOrderActors(subjects),
                "issue live attack orders") ||
            RefuseTacticalFrozenActor(target, "target it with a live attack order"))
            return;
        if (target == 0 || _rtsAttackQueue.Contains(target)) return;
        if (_rtsAttackQueue.Count > 0 && !SameRtsMembers(_rtsAttackSubjects, subjects))
            ClearRtsAttackQueue();
        if (_rtsAttackQueue.Count == 0)
        {
            _rtsAttackSubjects.AddRange(subjects);
            ClearRtsWaypointChain();
        }

        _rtsAttackQueue.Add(target);
        int number = _rtsAttackQueue.Count;
        AddChatMessage($"{OrderSubjectLabel(subjects)}: queued {number} — " +
            $"{ResolveWorldUnitName(target)}.");
        if (number == 1) IssueCurrentRtsAttack();
    }

    private void IssueCurrentRtsAttack()
    {
        if (_rtsAttackQueue.Count == 0) return;
        ulong target = _rtsAttackQueue[0];
        if (!TrySendLiveSuiOrder(1, _rtsAttackSubjects, target, 0, 0, 0)) return;
        NoteCompanionOrder(1, _rtsAttackSubjects);
        _rtsAttackIssuedAt = NowSeconds();
        if (_entities.TryGet(target, out WorldEntity unit))
            _rtsMoveMarkers.Add((unit.Position, _rtsAttackIssuedAt, RtsHostileTint));
    }

    private void UpdateRtsAttackQueue()
    {
        if (_rtsAttackQueue.Count == 0) return;
        if (_rtsAttackIssuedAt == 0)
        {
            IssueCurrentRtsAttack();
            return;
        }
        double now = NowSeconds();
        ulong active = _rtsAttackQueue[0];
        bool finished = _entities.TryGet(active, out WorldEntity target)
            ? target.IsDead
            : _rtsAttackIssuedAt > 0 && now - _rtsAttackIssuedAt > 2.0;
        if (!finished)
        {
            if (_rtsAttackIssuedAt > 0 && now - _rtsAttackIssuedAt > RtsAttackStaleSeconds)
                ClearRtsAttackQueue();
            return;
        }

        _rtsAttackQueue.RemoveAt(0);
        while (_rtsAttackQueue.Count > 0 &&
            (!_entities.TryGet(_rtsAttackQueue[0], out WorldEntity next) || next.IsDead))
            _rtsAttackQueue.RemoveAt(0);
        if (_rtsAttackQueue.Count == 0)
        {
            ClearRtsAttackQueue();
            return;
        }
        IssueCurrentRtsAttack();
        AddChatMessage($"{OrderSubjectLabel(_rtsAttackSubjects)}: next target — " +
            $"{ResolveWorldUnitName(_rtsAttackQueue[0])}.");
    }

    /// <summary>
    /// Plain move (type 0) through the flood coalescer: at most one send per
    /// <see cref="RtsMoveOrderMinInterval"/>, newest destination wins. A move to a different
    /// subject set flushes the pending one first, so no unit's order is silently dropped. The
    /// subjects list is a fresh per-click allocation, so stashing the reference is safe.
    /// </summary>
    private void IssueRtsMoveOrder(List<ulong> subjects, Vector3 point)
    {
        // The coalescer is presentation state with a delayed wire edge. Gate before clearing or
        // stashing anything so a click made during an owned drain cannot fire after DRAINED.
        if (RefuseTacticalFreezeLiveCommand("issuing live move orders")) return;
        if (RefuseTacticalFrozenActors(TacticalOrderActors(subjects),
                "issue live move orders"))
            return;
        ClearRtsAttackQueue();
        if (_hasPendingMoveOrder && _pendingMoveSubjects is not null &&
            !SameRtsMembers(_pendingMoveSubjects, subjects))
            FlushPendingRtsMoveOrder(force: true);

        double now = NowSeconds();
        if (now - _lastMoveOrderSentAt >= RtsMoveOrderMinInterval)
            SendRtsMoveOrder(subjects, point, now);
        else
        {
            _pendingMoveSubjects = subjects;
            _pendingMovePoint = point;
            _hasPendingMoveOrder = true;
        }
    }

    /// <summary>
    /// The actual move dispatch, run only for orders that clear the coalescer. Prediction,
    /// wire send, chip label, ground marker and chat all fire together here so the client's
    /// predicted lurch and the chat log match the order that was really sent, not the
    /// intermediate clicks the throttle dropped.
    /// </summary>
    private void SendRtsMoveOrder(List<ulong> subjects, Vector3 point, double now)
    {
        if (!TrySendLiveSuiOrder(0, subjects, 0, point.X, point.Y, point.Z))
        {
            _hasPendingMoveOrder = false;
            _pendingMoveSubjects = null;
            return;
        }
        BeginRtsMovePresentation(subjects, point);
        NoteCompanionOrder(0, subjects);
        _rtsMoveMarkers.Add((point, now, RtsFriendlyTint));
        ClearRtsWaypointChain();
        AddChatMessage($"{OrderSubjectLabel(subjects)}: move to ({point.X:F0}, {point.Y:F0}).");
        _lastMoveOrderSentAt = now;
        _hasPendingMoveOrder = false;
    }

    /// <summary>
    /// Send the coalesced move once its interval has elapsed (or immediately when forced by a
    /// set change). Called every frame from <see cref="UpdateControlInput"/> so the newest
    /// destination still lands if the player stops clicking mid-throttle.
    /// </summary>
    private void FlushPendingRtsMoveOrder(bool force = false)
    {
        if (!_hasPendingMoveOrder || _pendingMoveSubjects is null) return;
        double now = NowSeconds();
        if (!force && now - _lastMoveOrderSentAt < RtsMoveOrderMinInterval) return;
        SendRtsMoveOrder(_pendingMoveSubjects, _pendingMovePoint, now);
    }

    // ── Patrol draft (the armed Patrol button) ────────────────────────────────

    private void BeginRtsPatrolAuthoring(List<ulong> subjects)
    {
        _rtsPatrolAuthoring = true;
        _rtsPatrolDraft.Clear();
        _rtsPatrolDraftSubjects.Clear();
        _rtsPatrolDraftSubjects.AddRange(subjects);
        SetRtsControlGroupStatus("Patrol armed: right-click ground to chain waypoints — " +
            "Patrol again engages the loop, Escape cancels.");
    }

    private void CancelRtsPatrolAuthoring(bool silent = false)
    {
        if (!_rtsPatrolAuthoring) return;
        _rtsPatrolAuthoring = false;
        _rtsPatrolDraft.Clear();
        _rtsPatrolDraftSubjects.Clear();
        if (!silent) SetRtsControlGroupStatus("Patrol draft canceled.");
    }

    /// <summary>Escape pre-gate (the doc's unwind order puts an unfinished
    /// route draft ahead of every menu layer): an armed draft spends the press.</summary>
    private bool ConsumeRtsPatrolDraftEscape()
    {
        if (!_rtsPatrolAuthoring) return false;
        CancelRtsPatrolAuthoring();
        return true;
    }

    /// <summary>The armed Patrol button's second click: queue every drafted leg
    /// cold (the bots stood still while it was authored), then close the loop.</summary>
    private void EngageRtsPatrolDraft()
    {
        if (RefuseTacticalFreezeLiveCommand("authoring a patrol"))
        {
            CancelRtsPatrolAuthoring(silent: true);
            return;
        }
        List<ulong> subjects = [.. _rtsPatrolDraftSubjects];
        if (_rtsPatrolDraft.Count == 0 || _net is null)
        {
            CancelRtsPatrolAuthoring();
            return;
        }
        foreach (Vector3 leg in _rtsPatrolDraft)
            TrySendLiveSuiOrder(3, subjects, 0, leg.X, leg.Y, leg.Z,
                reportRefusal: false);
        bool engaged = false;
        foreach (ulong guid in subjects)
            if (_entities.TryGet(guid, out WorldEntity unit) && !unit.IsDead)
            {
                engaged = TrySendLiveSuiOrder(4, subjects, 0,
                    unit.Position.X, unit.Position.Y, unit.Position.Z);
                break;
            }

        // The engaged draft becomes the standing chain: same dots, same dashed
        // route, same arrival retirement as a Shift+RightClick route.
        _rtsWaypointSubjects.Clear();
        _rtsWaypointSubjects.AddRange(subjects);
        _rtsWaypointChain.Clear();
        _rtsWaypointChain.AddRange(_rtsPatrolDraft);
        _rtsWaypointProgressAt = NowSeconds();
        int points = _rtsPatrolDraft.Count;
        _rtsPatrolDraft.Clear();
        _rtsPatrolDraftSubjects.Clear();
        _rtsPatrolAuthoring = false;

        if (engaged)
        {
            NoteCompanionOrder(4, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: patrol the route " +
                $"({points} point{(points == 1 ? "" : "s")}).");
        }
        else
            SetRtsControlGroupStatus("Patrol loop not engaged — no living subject.");
    }

    /// <summary>
    /// RTS edge scroll: the pointer within a few pixels of a screen edge slides the fly rig
    /// that way, camera-relative, so the Command View is drivable without touching the keyboard.
    ///
    /// Speed scales with altitude above the ground the way every RTS does it — a map-level
    /// camera has to cover map-level distances, and the same yards/second that reads as a
    /// nudge at 5 yards up reads as a crawl at 60. Suppressed while a marquee drag is live
    /// (the rectangle is anchored in SCREEN space, so panning under it selects a lie) and
    /// whenever ImGui owns the pointer, which keeps the bottom edge usable as a UI strip.
    /// </summary>
    private void UpdateFreeCamEdgePan()
    {
        if (_controller is null) return;

        double now = NowSeconds();
        double previous = _freecamPanAt;
        _freecamPanAt = now;
        if (previous <= 0) return;                       // first frame in the view: no dt yet
        float dt = (float)Math.Clamp(now - previous, 0.0, 0.1);
        if (dt <= 0f) return;

        // HUD layout Edit Mode: a frame dragged toward an edge must not pan the camera.
        if (_hudEditMode) return;
        if (_freecamDragActive || _window.MouseCaptured || ImGui.GetIO().WantCaptureMouse) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mouse = _window.MousePosition;
        if (display.X < 1f || display.Y < 1f) return;
        // Pointer outside the window (alt-tabbed, or dragged onto a second monitor): no pan.
        if (mouse.X < 0f || mouse.Y < 0f || mouse.X > display.X || mouse.Y > display.Y) return;

        float x = (mouse.X <= FreecamEdgePanMargin ? -1f : 0f) +
                  (mouse.X >= display.X - FreecamEdgePanMargin ? 1f : 0f);
        float y = (mouse.Y <= FreecamEdgePanMargin ? 1f : 0f) +
                  (mouse.Y >= display.Y - FreecamEdgePanMargin ? -1f : 0f);
        if (x == 0f && y == 0f) return;

        float yaw = _window.Camera.Yaw;
        // Same basis the controller uses, so a pan and a WASD fly agree on which way is which.
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);

        // Off-map or unloaded terrain returns null; fall back to a mid-altitude rate rather
        // than crawling because the height sample happened to miss.
        float altitude = 10f;
        if (!_controller.UnderTerrainShell &&
            _terrain?.SampleHeight(_controller.Position.X, _controller.Position.Y) is float ground)
            altitude = MathF.Max(2f, _controller.Position.Z - ground);
        float speed = _config.Movement.FlySpeed * Math.Clamp(altitude / 12f, 0.45f, 3.0f);

        Vector3 pan = forward * y + right * x;
        if (pan.LengthSquared() > 1e-6f)
            // Through the same wall test as WASD flight — a direct Position write
            // here would let the edge pan ghost through what the keys cannot.
            _controller.FlyMove(Vector3.Normalize(pan) * speed * dt);
    }

    /// <summary>
    /// RTS scheme turning: swing the rig around the ground point it was looking at so a turn
    /// keeps the battlefield under the camera (<see cref="CommandViewLaw.OrbitRig"/>).
    /// <paramref name="yawDelta"/> is this frame's whole heading change (keys + mouse), already
    /// applied to the camera. Goes through FlyMove like every other rig move.
    /// </summary>
    private void OrbitCommandViewRig(float yawDelta)
    {
        if (_controller is null || yawDelta == 0f || !float.IsFinite(yawDelta)) return;
        float? ground = _controller.GroundZ;
        if (ground is null && !_controller.UnderTerrainShell)
            ground = _terrain?.SampleHeight(_controller.Position.X, _controller.Position.Y);
        float altitude = ground is float floor ? MathF.Max(0f, _controller.Position.Z - floor) : 10f;
        Vector3 target = CommandViewLaw.OrbitRig(_controller.Position,
            _window.Camera.Yaw - yawDelta, yawDelta, altitude, _window.Camera.Pitch);
        Vector3 delta = target - _controller.Position;
        if (delta.LengthSquared() > 1e-8f) _controller.FlyMove(delta);
    }

    // ── Command View camera glide + primary lock ──────────────────────────────────────────────
    private float _commandViewYawDelta;          // this frame's heading change (keys + mouse)
    private Vector3? _cvSmoothedTarget;          // where the camera is (eased), null = not started
    private ulong _cvFollowGuid;                 // unit the lock is riding, 0 = not engaged
    private Vector3? _cvFollowRigLast;           // rig position the lock last wrote

    // ── Command View order gate ───────────────────────────────────────────────────────────────
    /// <summary>Furthest a move or waypoint may land from the primary INSIDE an instance (owner,
    /// 2026-09-02: "I shouldn't be allowed to click anything that isn't somehow nearish me").
    /// Open-world maps have no reach limit: a commander orders across a whole zone, and a
    /// "Too far from X" refusal there only stopped a walk the server would have pathed
    /// (owner, 2026-09-02: "No reason to stop the pathing").</summary>
    private const float CommandViewInstanceOrderReachYards = 40f;

    /// <summary>Whether the current map is instanced (dungeon/raid/battleground) per Map.dbc.
    /// Unknown map = treated as open world, so a missing row never locks the commander down.</summary>
    private bool CommandViewOnInstanceMap() =>
        _maps?.Get(_config.Start.Map)?.IsInstance == true;

    /// <summary>Whether a ground pick may become an order: only the instance reach limit is judged
    /// here; whether the spot can be walked to is judged by the server's navigation mesh.</summary>
    private bool CommandViewOrderPointAllowed(Vector3 point, bool onTerrain, out string refusal)
    {
        refusal = "";
        if (!CommandViewOnInstanceMap()) return true;
        ulong primary = RtsPrimaryGuid != 0 ? RtsPrimaryGuid : ControlledGuid;
        if (primary == 0 || !_entities.TryGet(primary, out WorldEntity unit)) return true;
        float dx = point.X - unit.Position.X, dy = point.Y - unit.Position.Y;
        if (dx * dx + dy * dy > CommandViewInstanceOrderReachYards * CommandViewInstanceOrderReachYards)
        {
            refusal = $"Too far from {ResolveUnitName(primary)}.";
            return false;
        }
        // Reachability itself is the SERVER's call: the navigation mesh lives there, and an RTS
        // move is now validated against it (ORDER_MOVE: complete path or walk to the reachable
        // end; no path = refused with a message). No roof/height guesswork here.
        return true;
    }

    // ── Command View target mirror ────────────────────────────────────────────────────────────
    // The unit that fights is the primary, and its target lives server-side (UNIT_FIELD_TARGET);
    // the client's selection is the commander's view. Casting a hotkey with no client selection
    // failed "no target" while the primary was plainly swinging at something (owner, 2026-09-02).
    // While the commander has not picked a target of his own, the primary's victim becomes it.
    private ulong _cvMirroredTarget;

    private void MirrorPrimaryTarget()
    {
        ulong primary = RtsPrimaryGuid != 0 ? RtsPrimaryGuid : ControlledGuid;
        if (primary == 0 || !_entities.TryGet(primary, out WorldEntity unit)) return;
        ulong served = unit.Fields.Target ?? 0;
        if (served == 0 || served == primary || served == _selectionGuid) return;
        if (_selectionGuid != 0 && _selectionGuid != _cvMirroredTarget) return;   // commander's own pick wins
        if (!_entities.TryGet(served, out WorldEntity victim) || victim.IsDead) return;
        CommitSelection(served, beginAttack: false);
        _cvMirroredTarget = served;
    }

    // ── Command View walk-then-interact ───────────────────────────────────────────────────────
    // A right-click on a corpse, a service NPC (vendor / trainer / banker / auctioneer / flight
    // master / gossip) or a usable game object in the Command View is an INTERACTION, never a
    // ground move: the acting body is sent walking and the interaction opens on arrival. Before
    // this only corpses walked; a vendor click fell through to a ground order under the NPC and
    // the server answered "no path to that spot" (owner, 2026-09-02: "it should just path there
    // and open it ... same fix, applied really to any interaction").
    // Quests = the commander quest window (Model B): the PARTY walks to the giver and the window
    // opens once they are there (owner, 2026-09-03: "that shouldn't happen until the characters
    // are at the NPC").
    // Choose: an NPC with more than one offer — the acting body walks up and the chooser
    // is raised ON ARRIVAL (owner 2026-09-03: "no dialog until we are physically there").
    private enum CommandViewInteractKind { Loot, Service, GameObject, Quests, Choose }

    private ulong _cvPendingInteractGuid;
    private CommandViewInteractKind _cvPendingInteractKind;
    private double _cvPendingInteractAt;
    private double _cvPendingInteractNotBefore;
    private int _cvPendingInteractAttempts;
    /// <summary>Quests only: everyone ordered to the giver (your own character first).</summary>
    private readonly List<ulong> _cvPendingInteractWalkers = [];

    /// <summary>How close the walker stops short of the subject: a corpse is stood on (melee
    /// reach), a person or object is approached to talking distance.</summary>
    private const float CommandViewServiceApproachYards = 2.5f;

    /// <summary>The body that performs the interaction: loot is always the session character;
    /// an NPC service is the driven bot while possessing, else the session character (the
    /// server runs the handlers on it); a game object follows the same rule UseGameObject uses
    /// (mail and text are session-scoped, everything else is the controlled body).</summary>
    private ulong CommandViewInteractWalker(CommandViewInteractKind kind, WorldEntity subject) =>
        kind switch
        {
            CommandViewInteractKind.Loot => LocalPlayerGuid,
            // The server resolves the commander quest window against the REQUESTER and demands
            // the requester at the giver; the companions only need to be near the requester.
            CommandViewInteractKind.Quests => LocalPlayerGuid,
            CommandViewInteractKind.Service or CommandViewInteractKind.Choose =>
                _controlState == ControlState.Possessing ? ControlledGuid : LocalPlayerGuid,
            // A mailbox / plaque is used by the driven body too (the mail handlers run as
            // GetSuiActor); unpossessed, ControlledGuid IS the session character.
            _ => ControlledGuid,
        };

    private bool CommandViewInteractInReach(CommandViewInteractKind kind, WorldEntity subject)
    {
        ulong walker = CommandViewInteractWalker(kind, subject);
        if (!TryGetWorldBodyPose(walker, out WorldBodyPose body)) return false;
        float distanceSquared = Vector3.DistanceSquared(body.Position, subject.Position);
        switch (kind)
        {
            case CommandViewInteractKind.Loot:
                return _entities.TryGet(walker, out WorldEntity self) &&
                    distanceSquared <= WorldCursorUiLaw.UnitMeleeReachSquared(
                        self.Fields.CombatReach, subject.Fields.CombatReach);
            case CommandViewInteractKind.Service:
                // A party flight opens its map once the PARTY stands at the flight master.
                return NpcSessionUiLaw.InRange(distanceSquared) &&
                    (!CommandViewPartyWalksToSubject(kind) || CommandViewPartyAtSubject(subject));
            case CommandViewInteractKind.Choose:
                return NpcSessionUiLaw.InRange(distanceSquared);
            case CommandViewInteractKind.Quests:
                return NpcSessionUiLaw.InRange(distanceSquared) && CommandViewPartyAtSubject(subject);
            default:
                float limit = GameObjectUseDistance(subject);
                return distanceSquared <= limit * limit;
        }
    }

    private bool CommandViewInteractSubjectValid(CommandViewInteractKind kind, WorldEntity subject) =>
        kind switch
        {
            CommandViewInteractKind.Loot => subject.IsCreature && subject.IsDead && subject.Fields.Lootable,
            CommandViewInteractKind.Service =>
                subject.IsCreature && !subject.IsDead && WorldCursorServiceKind(subject) is not null,
            CommandViewInteractKind.Choose => subject.IsCreature && !subject.IsDead,
            CommandViewInteractKind.Quests =>
                subject.IsCreature && !subject.IsDead && (subject.NpcFlags & NpcQuestGiver) != 0,
            _ => subject.IsGameObject,
        };

    /// <summary>Whether the pending interaction has visibly landed, so the arrival retry stops.
    /// Loot is cleared by ApplyLootResponse itself; a service is delivered once any NPC panel is
    /// up; a game object once its use was queued (UseGameObject's return clears the pending) or
    /// the mail panel opened. Only consulted AFTER the first ask, so a panel left open from
    /// before the click never swallows the interaction.</summary>
    private bool CommandViewInteractDelivered(CommandViewInteractKind kind, ulong guid) =>
        kind switch
        {
            CommandViewInteractKind.Service =>
                _vendor is not null || _trainer is not null || _gossipMenu is not null ||
                _bankOpen || _auctionOpen || _mailOpen || _taxiMasterGuid == guid,
            CommandViewInteractKind.GameObject => _mailOpen,
            CommandViewInteractKind.Quests => _giverQuestsOpen && _giverQuestsGiverGuid == guid,
            _ => false,
        };

    /// <summary>The route the pending interaction was CHOSEN for (the NPC chooser), else null =
    /// the NPC's own cursor verdict. A flight master with a quest verdicts Speak, and Speak
    /// would open gossip, not the flight map the player picked.</summary>
    private ConfirmPopupUiLaw.NpcServiceRoute? _cvPendingInteractRoute;

    // ── An NPC that offers more than one thing (owner, 2026-09-02: "FP Map or Quest List?";
    // 2026-09-03: "apply the flight master fix to any NPC that has more than 1 option") ────────
    // Thor in Sentinel Hill is a flight master AND a quest giver: the commander quest window
    // (Model B) took every quest-giver click, so the flight path could never be discovered from
    // the sky; a vendor who also trains hid the training the same way. Any NPC with two or more
    // distinct offers raises a chooser (one button each) instead of guessing.
    private ulong _cvGiverChoiceGuid;
    private List<ConfirmPopupUiLaw.NpcOption> _cvGiverChoiceOptions = [];
    private int _cvGiverChoicePicked = -1;

    /// <summary>The NPC's flags as the world means them. The database stamps the innkeeper bit
    /// (0x80) on every bowyer (npc_flags 16516 = vendor | innkeeper | repair) and the server
    /// serves them the vendor list regardless; the gossip path already ignores that bit unless
    /// the creature is named an innkeeper (owner 2026-09-03: "why are these npcs giving me
    /// the innkeeper?"). The chooser uses the same law so a bowyer is a plain vendor.</summary>
    private uint EffectiveNpcFlags(WorldEntity npc) =>
        (npc.NpcFlags & NpcInnkeeper) != 0 && !IsInnkeeper(npc)
            ? npc.NpcFlags & ~NpcInnkeeper
            : npc.NpcFlags;

    /// <summary>The chooser is an NPC session like the vendor or bank window (owner 2026-09-03:
    /// "should automatically disappear if we path too far away without clicking anything"):
    /// once the acting body leaves talking range of the NPC, or the NPC is gone, it hides.
    /// Same rule for the party-flight confirm, which rides the taxi map's session.</summary>
    private void UpdateCommandViewNpcChoiceLifecycle()
    {
        if (_cvGiverChoiceGuid != 0)
        {
            ulong acting = _controlState == ControlState.Possessing ? ControlledGuid : LocalPlayerGuid;
            bool bodyKnown = TryGetWorldBodyPose(acting, out WorldBodyPose body);
            bool sourceAvailable = _entities.TryGet(_cvGiverChoiceGuid, out WorldEntity npc) && !npc.IsDead;
            float distanceSquared = sourceAvailable && bodyKnown
                ? Vector3.DistanceSquared(body.Position, npc.Position) : float.PositiveInfinity;
            if (NpcSessionUiLaw.ShouldClose(true, bodyKnown, sourceAvailable, distanceSquared))
            {
                ulong guid = _cvGiverChoiceGuid;
                ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                    _staticPopupSlots, ConfirmPopupUiLaw.GiverChoicePopupType));
                _cvGiverChoiceGuid = 0;
                _cvGiverChoicePicked = -1;
                EmitInterface("command-view", "npc-choose", "LIFECYCLE_CLOSED", guid,
                    sourceAvailable ? $"distanceSquared={distanceSquared:R}" : "source-gone");
            }
        }
        if (_partyTaxiPendingChain is not null && !_taxiOpen &&
            ConfirmPopupUiLaw.Visible(_staticPopupSlots, ConfirmPopupUiLaw.PartyFlightPopupType) is not null)
        {
            // The taxi map closed under the confirm (walked away, flight master gone): the
            // question no longer has a map behind it.
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, ConfirmPopupUiLaw.PartyFlightPopupType));
            _partyTaxiPendingChain = null;
            EmitInterface("taxi", "party-confirm", "LIFECYCLE_CLOSED", _partyTaxiPendingMaster, "taxiOpen=false");
        }
    }

    private void RaiseCommandViewNpcChoice(ulong guid, List<ConfirmPopupUiLaw.NpcOption> options)
    {
        _cvGiverChoiceGuid = guid;
        _cvGiverChoiceOptions = options;
        _cvGiverChoicePicked = -1;
        CommitSelection(guid, beginAttack: false);
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, ConfirmPopupUiLaw.GiverChoiceDefinition, playerDeadOrGhost: false,
            dataToken: guid.ToString()));
    }

    /// <summary>Accept = the option recorded by whichever chooser button was clicked (every
    /// chooser button is an Accept): walk up and open it. Escape or a replacing popup = nothing.</summary>
    private void ApplyCommandViewNpcChoice(StaticPopupCoordinatorLaw.EffectKind kind)
    {
        // The coordinator streams every lifecycle effect here (Show, PrepareContent, OnShow...);
        // only the answers consume the pending NPC.
        ulong guid = _cvGiverChoiceGuid;
        switch (kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
                if (_cvGiverChoicePicked >= 0 && _cvGiverChoicePicked < _cvGiverChoiceOptions.Count &&
                    _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead)
                    BeginCommandViewNpcRoute(guid, npc, _cvGiverChoiceOptions[_cvGiverChoicePicked].Route);
                _cvGiverChoiceGuid = 0;
                _cvGiverChoicePicked = -1;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
            case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
            case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
            case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                _cvGiverChoiceGuid = 0;
                _cvGiverChoicePicked = -1;
                break;
        }
    }

    /// <summary>Walk up and open one of the NPC's offers: the commander quest window walks the
    /// party (Quests); every other route is the acting body's Service interaction.</summary>
    private void BeginCommandViewNpcRoute(ulong guid, WorldEntity npc, ConfirmPopupUiLaw.NpcServiceRoute route)
    {
        if (route == ConfirmPopupUiLaw.NpcServiceRoute.CommanderQuests)
            BeginCommandViewInteraction(CommandViewInteractKind.Quests, guid, npc,
                walkers: CommandViewQuestWalkers());
        else if (route == ConfirmPopupUiLaw.NpcServiceRoute.Taxi && _partyTaxiAvailable)
            // Owner 2026-09-03: the flight map pops when the PARTY is next to the flight
            // master, like the quest window — the destination click then flies everyone.
            BeginCommandViewInteraction(CommandViewInteractKind.Service, guid, npc, route,
                walkers: CommandViewTaxiWalkers());
        else
            BeginCommandViewInteraction(CommandViewInteractKind.Service, guid, npc, route);
    }

    /// <summary>Who walks to a flight master for a party flight: the acting body first (the
    /// driven bot from the sky, else your own character — the one the map opens for), then
    /// your own character and every party member; the server flies only those it lets you
    /// command, and refuses the walk order for the rest.</summary>
    private List<ulong> CommandViewTaxiWalkers()
    {
        var walkers = new List<ulong>();
        ulong acting = _controlState == ControlState.Possessing ? ControlledGuid : LocalPlayerGuid;
        if (acting != 0) walkers.Add(acting);
        if (LocalPlayerGuid != 0 && !walkers.Contains(LocalPlayerGuid)) walkers.Add(LocalPlayerGuid);
        foreach (ulong guid in RtsControlGroupLaw.NormalizeMembers(_partyMembers.Select(member => member.Guid)))
            if (guid != 0 && !walkers.Contains(guid)) walkers.Add(guid);
        return walkers;
    }

    private bool CommandViewPartyWalksToSubject(CommandViewInteractKind kind) =>
        kind == CommandViewInteractKind.Quests ||
        (kind == CommandViewInteractKind.Service &&
         _cvPendingInteractRoute == ConfirmPopupUiLaw.NpcServiceRoute.Taxi);

    /// <summary>Who walks to a quest giver for the commander quest window: the highlighted set
    /// (the RTS convention), or the whole party when nothing is highlighted — and ALWAYS your
    /// own character, because the server resolves the window against the requester and demands
    /// the requester at the giver (companions need only be within quest-share distance of
    /// you).</summary>
    private List<ulong> CommandViewQuestWalkers()
    {
        var walkers = new List<ulong>();
        if (LocalPlayerGuid != 0) walkers.Add(LocalPlayerGuid);
        IEnumerable<ulong> chosen = _freecamSelection.Count > 0
            ? _freecamSelection
            : _partyMembers.Select(member => member.Guid);
        foreach (ulong guid in RtsControlGroupLaw.NormalizeMembers(chosen))
            if (guid != 0 && !walkers.Contains(guid)) walkers.Add(guid);
        return walkers;
    }

    /// <summary>Route a service NPC to its panel, the same dispatch the grounded right-click
    /// uses: vendor, flight master, banker, auctioneer, otherwise gossip.</summary>
    private bool OpenCommandViewService(ulong guid, WorldEntity npc)
    {
        if ((_cvPendingInteractRoute ?? CommandViewDefaultRoute(npc)) is not { } route) return false;
        CommitSelection(guid, beginAttack: false);
        return route switch
        {
            ConfirmPopupUiLaw.NpcServiceRoute.Vendor => RequestVendor(guid),
            ConfirmPopupUiLaw.NpcServiceRoute.Taxi => RequestTaxiMap(guid),
            ConfirmPopupUiLaw.NpcServiceRoute.Bank => RequestBank(guid),
            // Possessed: the driven bot walks up to the auctioneer and the server's
            // GetSuiActor-threaded auction handlers act on ITS purse and bags.
            ConfirmPopupUiLaw.NpcServiceRoute.Auction => RequestAuction(guid),
            _ => RequestGossip(guid),
        };
    }

    /// <summary>The NPC's own cursor verdict as a route (lowest flag bit wins, as on the ground).</summary>
    private ConfirmPopupUiLaw.NpcServiceRoute? CommandViewDefaultRoute(WorldEntity npc) =>
        WorldCursorServiceKind(npc) switch
        {
            null => null,
            WorldCursorKind.Pickup => ConfirmPopupUiLaw.NpcServiceRoute.Vendor,
            WorldCursorKind.Taxi => ConfirmPopupUiLaw.NpcServiceRoute.Taxi,
            WorldCursorKind.Buy when (npc.NpcFlags & NpcBanker) != 0 => ConfirmPopupUiLaw.NpcServiceRoute.Bank,
            WorldCursorKind.Buy when (npc.NpcFlags & NpcAuctioneer) != 0 => ConfirmPopupUiLaw.NpcServiceRoute.Auction,
            _ => ConfirmPopupUiLaw.NpcServiceRoute.Gossip,
        };

    /// <summary>Arm a walk-then-interact on <paramref name="guid"/>: in reach it opens on the
    /// next frame; out of reach the acting body is ordered to walk up to it first. The walk
    /// stops short of a person or object at talking distance rather than on top of it. A Quests
    /// interaction orders every one of <paramref name="walkers"/> (the party) and opens once
    /// they are all there.</summary>
    private void BeginCommandViewInteraction(CommandViewInteractKind kind, ulong guid, WorldEntity subject,
        ConfirmPopupUiLaw.NpcServiceRoute? route = null, IReadOnlyList<ulong>? walkers = null)
    {
        if (RefuseTacticalFreezeLiveCommand("interacting")) return;
        if (RefuseTacticalFrozenActor(guid, "interact with it")) return;
        _cvPendingInteractGuid = guid;
        _cvPendingInteractKind = kind;
        _cvPendingInteractRoute = route;
        _cvPendingInteractAt = NowSeconds();
        _cvPendingInteractNotBefore = 0;
        _cvPendingInteractAttempts = 0;
        _cvPendingInteractWalkers.Clear();
        if (walkers is not null) _cvPendingInteractWalkers.AddRange(walkers);
        if (CommandViewInteractInReach(kind, subject)) return;
        ulong walker = CommandViewInteractWalker(kind, subject);
        Vector3 destination = subject.Position;
        if (kind != CommandViewInteractKind.Loot &&
            TryGetWorldBodyPose(walker, out WorldBodyPose body))
        {
            Vector3 toBody = body.Position - subject.Position;
            toBody.Z = 0f;
            float length = toBody.Length();
            if (length > CommandViewServiceApproachYards)
                destination += toBody * (CommandViewServiceApproachYards / length);
        }
        List<ulong> ordered = CommandViewPartyWalksToSubject(kind) && _cvPendingInteractWalkers.Count > 0
            ? _cvPendingInteractWalkers
            : new List<ulong> { walker };
        if (!TrySendLiveSuiOrder(0 /* ORDER_MOVE */, ordered, 0,
                destination.X, destination.Y, destination.Z))
        {
            _cvPendingInteractGuid = 0;
            _cvPendingInteractWalkers.Clear();
            return;
        }
        if (ordered.Count > 1) NoteCompanionOrder(0, ordered);
        _rtsMoveMarkers.Add((subject.Position, NowSeconds(), RtsNeutralTint));
        if (kind is CommandViewInteractKind.Service or CommandViewInteractKind.Quests or CommandViewInteractKind.Choose)
            AddChatMessage($"{OrderSubjectLabel(ordered)}: to {ResolveWorldUnitName(guid)}.");
    }

    /// <summary>"The party is here" radius for a Quests interaction: companions stand a little
    /// wider than talking distance (anti-stacking spreads them), still well inside the server's
    /// quest-share distance from the requester.</summary>
    private const float CommandViewPartyArrivalYards = 8f;

    /// <summary>How long a walk may take before the pending interaction is dropped. A party
    /// walk to a giver is longer than one body's stroll to a vendor.</summary>
    private double CommandViewInteractDeadlineSeconds(CommandViewInteractKind kind) =>
        CommandViewPartyWalksToSubject(kind) && _cvPendingInteractWalkers.Count > 1 ? 45.0 : 15.0;

    private bool CommandViewPartyAtSubject(WorldEntity subject)
    {
        foreach (ulong guid in _cvPendingInteractWalkers)
        {
            if (guid == LocalPlayerGuid || !TryGetWorldBodyPose(guid, out WorldBodyPose body)) continue;
            if (Vector3.DistanceSquared(body.Position, subject.Position) >
                CommandViewPartyArrivalYards * CommandViewPartyArrivalYards) return false;
        }
        return true;
    }

    private void UpdateCommandViewPendingInteraction()
    {
        if (_cvPendingInteractGuid == 0) return;
        // A walk-to-service armed before a lock is delayed local state just like the move
        // coalescer. Retire it while active/draining so arrival cannot open loot, an NPC session,
        // or a game object immediately after DRAINED under an obsolete pre-plan click.
        if (TacticalFreezeBlocksLiveCommands)
        {
            _cvPendingInteractGuid = 0;
            _cvPendingInteractWalkers.Clear();
            return;
        }
        if (IsTacticalActorFrozen(_cvPendingInteractGuid))
        {
            _cvPendingInteractGuid = 0;
            _cvPendingInteractRoute = null;
            _cvPendingInteractWalkers.Clear();
            return;
        }
        double now = NowSeconds();
        CommandViewInteractKind kind = _cvPendingInteractKind;
        ulong guid = _cvPendingInteractGuid;
        if (!_entities.TryGet(guid, out WorldEntity subject) ||
            !CommandViewInteractSubjectValid(kind, subject))
        {
            _cvPendingInteractGuid = 0;
            return;
        }
        bool expired = now - _cvPendingInteractAt > CommandViewInteractDeadlineSeconds(kind);
        bool questsAllowed = kind != CommandViewInteractKind.Quests ||
            (_controlState != ControlState.Possessing && _partyGiverQuestsAvailable);
        if (!_freeView || expired || _cvPendingInteractAttempts >= 8 || !questsAllowed ||
            (_cvPendingInteractAttempts > 0 && CommandViewInteractDelivered(kind, guid)))
        {
            // A party walk that ran out the clock with a straggler still opens for whoever made
            // it: the requester at the giver is all the server demands, and a far member simply
            // shows out of range on its card. Better than a click that silently did nothing.
            if (expired && _freeView && questsAllowed && kind == CommandViewInteractKind.Quests &&
                _cvPendingInteractAttempts == 0 &&
                TryGetWorldBodyPose(LocalPlayerGuid, out WorldBodyPose requester) &&
                NpcSessionUiLaw.InRange(Vector3.DistanceSquared(requester.Position, subject.Position)))
            {
                CommitSelection(guid, beginAttack: false);
                RequestGiverQuests(guid);
            }
            // Same for a party flight whose stragglers ran out the clock: the map opens for
            // whoever made it; the confirm names the rest as too far.
            else if (expired && _freeView && kind == CommandViewInteractKind.Service &&
                CommandViewPartyWalksToSubject(kind) && _cvPendingInteractAttempts == 0 &&
                TryGetWorldBodyPose(CommandViewInteractWalker(kind, subject), out WorldBodyPose acting) &&
                NpcSessionUiLaw.InRange(Vector3.DistanceSquared(acting.Position, subject.Position)))
                OpenCommandViewService(guid, subject);
            _cvPendingInteractGuid = 0;
            return;
        }
        // The body is walked by a server spline the client only mirrors: ask once it has STOPPED
        // beside the subject, and keep asking on a short cadence until the interaction opens -
        // the first ask on arrival raced the server's own position and came back "too far away"
        // (owner, 2026-09-02).
        if (!CommandViewInteractInReach(kind, subject)) return;
        ulong walker = CommandViewInteractWalker(kind, subject);
        if (_entities.TryGet(walker, out WorldEntity body) && body.IsMoving) return;
        if (now < _cvPendingInteractNotBefore) return;
        _cvPendingInteractNotBefore = now + 0.5;
        _cvPendingInteractAttempts++;
        switch (kind)
        {
            case CommandViewInteractKind.Loot:
                RequestLoot(guid);                      // cleared by ApplyLootResponse
                break;
            case CommandViewInteractKind.Service:
                OpenCommandViewService(guid, subject);  // cleared once a panel is up
                break;
            case CommandViewInteractKind.Choose:
            {
                // Arrived: NOW ask which of the NPC's offers to open. The chosen route re-enters
                // BeginCommandViewInteraction already in reach, so it opens at once (a party
                // flight or party quest window may still order the others over first).
                bool commanderQuests = _controlState != ControlState.Possessing && _partyGiverQuestsAvailable;
                List<ConfirmPopupUiLaw.NpcOption> offers = ConfirmPopupUiLaw.NpcOptions(EffectiveNpcFlags(subject), commanderQuests);
                _cvPendingInteractGuid = 0;
                if (ConfirmPopupUiLaw.NeedsChooser(offers)) RaiseCommandViewNpcChoice(guid, offers);
                else if (offers.Count == 1) BeginCommandViewNpcRoute(guid, subject, offers[0].Route);
                else OpenCommandViewService(guid, subject);
                break;
            }
            case CommandViewInteractKind.Quests:
                CommitSelection(guid, beginAttack: false);
                RequestGiverQuests(guid);               // cleared once the window is up
                break;
            default:
                if (UseGameObject(guid)) _cvPendingInteractGuid = 0;
                break;
        }
    }

    // ── Manual primary (owner, 2026-09-01: "primary should always be user controlled") ────────
    // The server bot brain that lets a unit take RTS orders also fought for it. ORDER_MANUAL
    // stands that brain down for the primary selection; ORDER_AUTO hands a unit back the moment
    // it stops being the primary. Re-sent every 3 s so an AI attached after the order (your own
    // body gets one on free-view entry) hears it.
    private const byte SuiOrderManual = 13;
    private const byte SuiOrderAuto = 14;
    private ulong _cvManualGuid;
    private double _cvManualSentAt;

    /// <summary>
    /// Line-of-sight cut subjects: every selectable party unit alive and within range of the
    /// camera, chest height, capped at the shader's segment count. Empty unless the Command View
    /// is up and the toggle is on.
    /// </summary>
    private void CollectCommandViewSightTargets(Vector3 camera, List<Vector3> into)
    {
        if (!_freeView || !Settings.Controls.CommandViewSightCut) return;
        float range2 = WorldCut.SightRangeYards * WorldCut.SightRangeYards;
        // Party / raid only (owner, 2026-09-02): the faction force roster - same-faction bots the
        // server lets you command - is NOT the party, and the world must not open up for them.
        foreach (ulong guid in CommandViewPartyGuids())
        {
            if (into.Count >= WorldCut.MaxSightLines) break;
            if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
            Vector3 chest = unit.Position + new Vector3(0f, 0f, 1.2f);
            if (Vector3.DistanceSquared(chest, camera) > range2) continue;
            into.Add(chest);
        }
    }

    /// <summary>Your own character, your party or raid, and the unit you are possessing. The
    /// faction force roster is deliberately excluded.</summary>
    private IEnumerable<ulong> CommandViewPartyGuids()
    {
        var seen = new HashSet<ulong>();
        if (LocalPlayerGuid != 0 && seen.Add(LocalPlayerGuid)) yield return LocalPlayerGuid;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid != 0 && seen.Add(member.Guid)) yield return member.Guid;
        if (_controlState == ControlState.Possessing && _controlTargetGuid != 0 &&
            seen.Add(_controlTargetGuid))
            yield return _controlTargetGuid;
    }

    /// <summary>Tablet / options: flip whether the primary's AI fights for it.</summary>
    private void ToggleCommandViewPrimaryAi()
    {
        Settings.Controls.CommandViewPrimaryAi = !Settings.Controls.CommandViewPrimaryAi;
        _cvManualSentAt = 0;   // resend the order now
        CommitSettings();
    }

    private void SyncManualPrimary()
    {
        if (_net is null) return;
        if (TacticalFreezeBlocksLiveCommands) return;
        ulong primary = _freeView && !Settings.Controls.CommandViewPrimaryAi
            ? (RtsPrimaryGuid != 0 ? RtsPrimaryGuid : _cvFollowGuid) : 0;
        double now = NowSeconds();
        if (primary == _cvManualGuid && (primary == 0 || now - _cvManualSentAt < 3.0)) return;
        if (_cvManualGuid != 0 && _cvManualGuid != primary && _entities.TryGet(_cvManualGuid, out _))
            TrySendLiveSuiOrder(SuiOrderAuto, [_cvManualGuid], 0, 0f, 0f, 0f,
                reportRefusal: false);
        if (primary != 0)
            TrySendLiveSuiOrder(SuiOrderManual, [primary], 0, 0f, 0f, 0f,
                reportRefusal: false);
        _cvManualGuid = primary;
        _cvManualSentAt = now;
    }

    /// <summary>The camera is riding the primary selection (setting on, free view up, a live primary).</summary>
    private bool CommandViewLocked =>
        _freeView && Settings.Controls.CommandViewLockOnPrimary && (_cvFollowGuid != 0 || RtsPrimaryGuid != 0);

    /// <summary>
    /// STICKY lock (owner, 2026-09-01): while locked, the ridden unit cannot be un-selected by a
    /// stray click or marquee, and stays the primary. Only an explicit primary change (Q or a
    /// portrait click, which set <see cref="_rtsPrimaryGuid"/> to another SELECTED unit) retargets
    /// the lock; releasing it is the only way off. Runs every free-view frame.
    /// </summary>
    private void EnforceStickyLock()
    {
        if (!Settings.Controls.CommandViewLockOnPrimary || _cvFollowGuid == 0) return;
        if (!_entities.TryGet(_cvFollowGuid, out WorldEntity ridden) || ridden.IsDead)
            return;                                    // gone or dead: CommandViewCameraTarget drops it
        bool explicitRetarget = _rtsPrimaryGuid != 0 && _rtsPrimaryGuid != _cvFollowGuid &&
            _freecamSelection.Contains(_rtsPrimaryGuid);
        if (explicitRetarget) return;                  // Q / portrait click: the lock follows
        if (!_freecamSelection.Contains(_cvFollowGuid)) _freecamSelection.Insert(0, _cvFollowGuid);
        _rtsPrimaryGuid = _cvFollowGuid;
    }

    /// <summary>
    /// The Command View's camera target for this frame. Unlocked: the rig, eased through
    /// <see cref="CommandViewLaw.Smooth"/> when smoothing is on (off = the old hard coupling).
    /// Locked: the rig RIDES the primary — unit position plus a framing offset that every rig move
    /// (keys, wheel, edge pan, focus) adjusts and every turn rotates, so the mouse orbits the unit
    /// and the party stays tracked as it walks. Always eased: that is what makes it nice.
    /// </summary>
    private Vector3 CommandViewCameraTarget(float dt, float yawDelta)
    {
        if (_controller is null) return _window.Camera.Target;
        Vector3 rig = _controller.Position;

        if (Settings.Controls.CommandViewLockOnPrimary &&
            _entities.TryGet(RtsPrimaryGuid != 0 ? RtsPrimaryGuid : _cvFollowGuid, out WorldEntity unit) &&
            !unit.IsDead)
        {
            if (_cvFollowGuid != unit.Guid)
            {
                _cvFollowGuid = unit.Guid;
                _cvFollowRigLast = rig;
            }
            if (_cvFollowRigLast is Vector3 last &&
                (rig - last).Length() > CommandViewLaw.FollowBreakYards)
            {
                // Commander-map fly: they meant to leave. Let go rather than yank them back.
                Settings.Controls.CommandViewLockOnPrimary = false;
                _cvFollowGuid = 0;
                _cvFollowRigLast = null;
                _cvSmoothedTarget = rig;
                return rig;
            }

            // Parked dead on the unit: movement input is zeroed upstream (Program.cs), the wheel
            // zooms the boom, and every turn (A/D or the mouse) orbits this point.
            Vector3 anchor = unit.Position;
            // Cut plane up: the eye stays above the slice even while parked on a unit under it.
            if (_wmo?.ActiveCut is WorldCut lockCut)
                anchor.Z = MathF.Max(anchor.Z, lockCut.RigFloor(_window.Camera.Distance, _window.Camera.Pitch));
            Vector3 eased = CommandViewLaw.Smooth(_cvSmoothedTarget ?? anchor, anchor, dt,
                CommandViewLaw.FollowTau);
            _controller.Position = eased;     // the rig itself rides along: wheel/pan/heartbeat agree
            _cvFollowRigLast = eased;
            _cvSmoothedTarget = eased;
            return eased;
        }

        _cvFollowGuid = 0;
        _cvFollowRigLast = null;
        if (!Settings.Controls.CommandViewSmoothing)
        {
            _cvSmoothedTarget = rig;
            return rig;
        }
        Vector3 glide = CommandViewLaw.Smooth(_cvSmoothedTarget ?? rig, rig, dt,
            CommandViewLaw.SmoothingTau);
        _cvSmoothedTarget = glide;
        return glide;
    }

    /// <summary>Ctrl+L / tablet button: lock or release the camera on the primary selection.</summary>
    private void ToggleCommandViewLock()
    {
        if (!_freeView) return;
        bool on = !Settings.Controls.CommandViewLockOnPrimary;
        if (on && RtsPrimaryGuid == 0)
        {
            ShowUiError("Select a unit to lock the camera on.");
            return;
        }
        Settings.Controls.CommandViewLockOnPrimary = on;
        if (!on) _cvFollowGuid = 0;
        CommitSettings();
    }

    // ── Command View camera tablet: lock row + view-angle knob ────────────────────────────────
    private const float AngleKnobWidth = 176f;    // logical px, × UI scale
    private const float AngleKnobHeight = 30f;    // one row

    /// <summary>
    /// The small "View angle" slider at the bottom right of the free view (Interface Options →
    /// Command View → "View-angle knob on screen"). Only the knob schemes show it: under
    /// Classic the mouse owns the pitch. Returns its scaled height so the control guide can
    /// stack above it, or 0 when it is not drawn.
    /// </summary>
    private float DrawCommandViewAngleKnob()
    {
        if (!_freeView || !Settings.Controls.CommandViewAngleKnob) return 0f;
        // The angle row only under the knob schemes; the lock row is always there.
        bool angleRow = CommandViewLaw.PitchLocked(Settings.Controls.CommandViewScheme);

        float scale = GameplayUiScale();
        var io = ImGui.GetIO();
        Vector2 size = new Vector2(AngleKnobWidth, AngleKnobHeight * (angleRow ? 3f : 2f)) * scale;
        HudFrameResult knob = HudFrame("angle-knob", "Camera tablet",
            HudPlacement.At(HudAnchor.BottomRight, -20f, -20f), size / scale);
        if (knob.Hidden) return 0f;   // hidden in the active HUD layout: the guide drops into its seat
        ImGui.SetNextWindowPos(knob.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##cv-angle-knob", flags)) { ImGui.End(); return size.Y; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + size;
        // Same carved-stone tablet as the command console: this is furniture, not a tooltip.
        DrawRtsConsoleBackdrop(dl, min, max, scale);

        float rule = MathF.Max(1f, scale);

        // Row 1: lock on primary. A gilt pip that fills when the camera is riding the unit.
        {
            bool locked = CommandViewLocked;
            ulong primary = RtsPrimaryGuid;
            string label = locked ? $"Locked: {ResolveUnitName(primary)}"
                : primary != 0 ? "Lock on primary" : "Lock on primary (select a unit)";
            label = GameText.EllipsizeToBox("GameFontNormalSmall", label, AngleKnobWidth - 40f, AngleKnobHeight, scale);
            Vector2 rowMin = min;
            Vector2 rowMax = new Vector2(max.X, min.Y + AngleKnobHeight * scale);
            ImGui.SetCursorScreenPos(rowMin);
            ImGui.InvisibleButton("##cv-lock-row", rowMax - rowMin);
            if (ImGui.IsItemClicked()) ToggleCommandViewLock();
            bool hot = ImGui.IsItemHovered();
            var pip = new Vector2(min.X + 16f * scale, min.Y + AngleKnobHeight * 0.5f * scale);
            dl.AddCircleFilled(pip, 6f * scale,
                locked ? PainterlyGoldLit : hot ? PainterlyGoldShade : PainterlyFrameInner);
            dl.AddCircle(pip, 6f * scale, locked ? PainterlyFrameRule : PainterlyFrameOuter, 0, rule);
            GameText.Draw(dl, "GameFontNormalSmall", label,
                new Vector2(min.X + 28f * scale, min.Y + 8f * scale), scale,
                locked ? PainterlyGoldLit : null);
            if (hot) HoverTip(locked
                ? "Release the camera (Ctrl+L)."
                : "Park the camera on the primary selection: it tracks the unit as it\n" +
                  "moves, A/D and the mouse orbit around it, the wheel zooms. Movement " +
                  "keys are parked until you release (Ctrl+L).");
        }
        // Row 2: primary AI on/off.
        {
            float aiTop = min.Y + AngleKnobHeight * scale;
            dl.AddLine(new Vector2(min.X + 6f * scale, aiTop), new Vector2(max.X - 6f * scale, aiTop),
                PainterlyFrameOuter, rule);
            bool aiOn = Settings.Controls.CommandViewPrimaryAi;
            ImGui.SetCursorScreenPos(new Vector2(min.X, aiTop));
            ImGui.InvisibleButton("##cv-primary-ai", new Vector2(size.X, AngleKnobHeight * scale));
            if (ImGui.IsItemClicked()) ToggleCommandViewPrimaryAi();
            bool aiHot = ImGui.IsItemHovered();
            var aiPip = new Vector2(min.X + 16f * scale, aiTop + AngleKnobHeight * 0.5f * scale);
            dl.AddCircleFilled(aiPip, 6f * scale,
                aiOn ? PainterlyGoldLit : aiHot ? PainterlyGoldShade : PainterlyFrameInner);
            dl.AddCircle(aiPip, 6f * scale, aiOn ? PainterlyFrameRule : PainterlyFrameOuter, 0, rule);
            GameText.Draw(dl, "GameFontNormalSmall", aiOn ? "Primary AI: on" : "Primary AI: off (yours)",
                new Vector2(min.X + 28f * scale, aiTop + 8f * scale), scale, aiOn ? PainterlyGoldLit : null);
            if (aiHot) HoverTip(aiOn
                ? "The primary's AI fights for it. Click: the primary does only what you order and press."
                : "The primary is yours: it moves on orders and does nothing else until you press a key. Click: let its AI fight.");
        }
        if (!angleRow) { ImGui.End(); return size.Y; }

        float rowTop = min.Y + AngleKnobHeight * 2f * scale;
        dl.AddLine(new Vector2(min.X + 6f * scale, rowTop), new Vector2(max.X - 6f * scale, rowTop),
            PainterlyFrameOuter, rule);
        GameText.Draw(dl, "GameFontNormalSmall", "View angle", new Vector2(min.X + 9f * scale, rowTop + 8f * scale), scale);

        float trackX0 = min.X + 78f * scale;
        float trackX1 = max.X - 42f * scale;
        float trackY = rowTop + AngleKnobHeight * 0.5f * scale;
        dl.AddRectFilled(new Vector2(trackX0, trackY - 2f * scale), new Vector2(trackX1, trackY + 2f * scale),
            PainterlyFrameOuter);
        dl.AddLine(new Vector2(trackX0, trackY + 2f * scale), new Vector2(trackX1, trackY + 2f * scale),
            PainterlyGoldShade, rule);

        // The track is the hit area: press or drag anywhere along it to set the angle. The
        // ImGui window under the pointer already keeps the press from becoming a world click.
        ImGui.SetCursorScreenPos(new Vector2(trackX0 - 8f * scale, rowTop));
        ImGui.InvisibleButton("##cv-angle-track", new Vector2(trackX1 - trackX0 + 16f * scale, AngleKnobHeight * scale));
        if (ImGui.IsItemActive())
        {
            float u = Math.Clamp((io.MousePos.X - trackX0) / MathF.Max(1f, trackX1 - trackX0), 0f, 1f);
            Settings.Controls.CommandViewPitchDegrees = CommandViewLaw.MinPitchDegrees +
                u * (CommandViewLaw.MaxPitchDegrees - CommandViewLaw.MinPitchDegrees);
        }
        if (ImGui.IsItemDeactivated()) CommitSettings();

        float degrees = CommandViewLaw.ClampPitchDegrees(Settings.Controls.CommandViewPitchDegrees);
        float t = (degrees - CommandViewLaw.MinPitchDegrees) /
            (CommandViewLaw.MaxPitchDegrees - CommandViewLaw.MinPitchDegrees);
        var handle = new Vector2(trackX0 + t * (trackX1 - trackX0), trackY);
        dl.AddCircleFilled(handle, 6f * scale, ImGui.IsItemHovered() || ImGui.IsItemActive()
            ? PainterlyGoldLit : PainterlyFrameRule);
        dl.AddCircle(handle, 6f * scale, PainterlyFrameOuter, 0, rule);

        GameText.DrawRightAligned(dl, "GameFontNormalSmall", $"{degrees:0}°",
            new Vector2(max.X - 9f * scale, trackY - GameText.EmPixels("GameFontNormalSmall", scale) * 0.5f),
            scale);
        ImGui.End();
        return size.Y;
    }

    /// <summary>
    /// Own/party characters plus genuine same-faction bots advertised by the
    /// server. The force roster is an affordance only; the server revalidates
    /// possession and every explicit order.
    /// </summary>
    private IEnumerable<ulong> FreeCamSelectableGuids()
    {
        var seen = new HashSet<ulong>();
        if (LocalPlayerGuid != 0 && seen.Add(LocalPlayerGuid))
            yield return LocalPlayerGuid;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid != 0 && seen.Add(member.Guid))
                yield return member.Guid;
        // A force page marks a possessed unit busy. Keep the body this session
        // already controls selectable so it can still join marquee orders and
        // temporary groups while the camera stays detached.
        if (_controlState == ControlState.Possessing && _controlTargetGuid != 0 &&
            seen.Add(_controlTargetGuid))
            yield return _controlTargetGuid;
        if (CanUseFactionForceRoster())
            foreach ((ulong guid, RtsForceUnitWire force) in _rtsForces)
                if (guid != 0 && force.Alive && force.SameMapAndInstance && seen.Add(guid))
                    yield return guid;
    }

    /// <summary>
    /// Select Whole Party (Ctrl+A by default): you and every party member whose body is
    /// streamed and alive become the selection, in party order - the same set a marquee drawn
    /// over all of them would produce, without having to fit them on screen first.
    /// </summary>
    private void SelectAllPartyMembers()
    {
        var all = new List<ulong>();
        var seen = new HashSet<ulong>();
        if (LocalPlayerGuid != 0 && seen.Add(LocalPlayerGuid)) all.Add(LocalPlayerGuid);
        foreach (PartyMember member in _partyMembers)
            if (member.Guid != 0 && seen.Add(member.Guid)) all.Add(member.Guid);
        all.RemoveAll(guid => !_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead);
        if (all.Count == 0)
        {
            ShowUiError("No party members nearby to select.");
            return;
        }
        // A route belongs to the selection that authored its first leg (SelectRtsControlGroup).
        if (_rtsWaypointChain.Count > 0 && !SameRtsMembers(_rtsWaypointSubjects, all))
            ClearRtsWaypointChain();
        _freecamSelection.Clear();
        _freecamSelection.AddRange(all);
        PlayCompanionSelectionVoice(_freecamSelection[0]);
        if (_freecamSelection.Count == 1) EnsureBotBarForViewing(_freecamSelection[0]);
        SetRtsControlGroupStatus(all.Count == 1
            ? "Selected: you (no party members nearby)."
            : $"Selected the whole party: {all.Count} members.");
    }

    private void CommitMarqueeSelection(Vector2 a, Vector2 b)
    {
        _freecamSelection.Clear();
        Vector2 min = Vector2.Min(a, b);
        Vector2 max = Vector2.Max(a, b);
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (ulong guid in FreeCamSelectableGuids())
        {
            if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
            if (!_window.Camera.TryWorldToScreen(unit.Position, display, out Vector2 screen)) continue;
            if (screen.X >= min.X && screen.X <= max.X && screen.Y >= min.Y && screen.Y <= max.Y)
                _freecamSelection.Add(guid);
        }
        // Encounter Lab raid puppets marquee like anything else; the order router
        // sends a puppet-carrying selection to the sim instead of the server.
        AddEncounterPuppetsToMarquee(min, max, display);
        if (_freecamSelection.Count == 1 &&
            EncounterRaidPuppetKey(_freecamSelection[0]) is null)
            EnsureBotBarForViewing(_freecamSelection[0]);
        if (_freecamSelection.Count > 0)
        {
            // The marquee's spokesman is the first swept COMPANION — the own body
            // enumerates first in FreeCamSelectableGuids and would mute the hello.
            foreach (ulong swept in _freecamSelection)
                if (swept != ControlledGuid)
                {
                    PlayCompanionSelectionVoice(swept);
                    break;
                }
            AddChatMessage($"Selected {_freecamSelection.Count}: " +
                $"{BindingHint(GameBinding.RtsOrderMove)} the ground to move, a hostile to " +
                $"attack, {BindingHint(GameBinding.RtsOrderQueueWaypoint)} to chain waypoints.");
        }
        else if (!CanUseFactionForceRoster() && _partyMembers.Count == 0)
            // The silent empty marquee was the confusing half of the closed gate: bots
            // stand right there, the box sweeps them, nothing selects, no word why.
            AddChatMessage("Marquee found no commandable bots: this server build does not " +
                "advertise faction-control-groups-v1 (update SuperUI-Core), and no party " +
                "bots are present.");
    }

    /// <summary>
    /// Free-view world click (routed from the targeting click queue). Left selects (a
    /// server-advertised controllable character joins the highlighted set; empty ground
    /// clears it). RightClick
    /// orders the HIGHLIGHTED set: hostile under cursor → attack, ground → move.
    /// Shift+RightClick appends a waypoint for that exact selection; an empty explicit
    /// list retains the legacy whole-real-party meaning.
    /// </summary>
    private void HandleFreeCamWorldClick(WorldMouseClick click, TargetPressPick pressPick = default)
    {
        // HUD layout Edit Mode: clicks move frames, never dispatch orders.
        if (_hudEditMode) return;
        // A live waypoint-orient spin: the next click of ANY button sets the facing and
        // ends the session (the grab began on an earlier Shift+Right-click on the dot).
        if (_encounterOrientSpinning) { EndEncounterOrientSpin(commit: true); return; }

        // A live orbit-drag: the next click commits (left = set the what-if) or cancels
        // (right). The grab began on an earlier Shift+Left-click ON the body.
        if (_encounterOrbitDragging)
        { EndEncounterOrbitDrag(commit: click.Button == MouseButton.Left); return; }

        // A command-card heal/buff owns the next world click without changing the RTS selection.
        // Right-click is cancellation; left-click binds the press-latched unit under the cursor.
        if (_rtsUnitCastSpellId != 0)
        {
            if (click.Button == MouseButton.Right)
                CancelRtsUnitCastTargeting(silent: false);
            else if (click.Button == MouseButton.Left)
                TryCommitRtsUnitCastTarget(pressPick.Armed
                    ? pressPick.UnitGuid : PickUnit(click.Position));
            return;
        }

        // The Command View's gestures are bindings now (RTS Controls / CRPG Controls). Resolve
        // them once, here, against the modifiers CAPTURED WITH THE CLICK: the queued-click
        // drain runs a frame or more after the press that classified it, so the live keyboard
        // is the wrong question (releasing Shift before the button already had to be handled).
        // Defaults reproduce the old hard-coded grammar exactly - Button1 selects, Shift+Button1
        // adds, Alt+Button1 takes control, Button2 orders, Shift+Button2 chains.
        bool gestureSelect = BindingClaimsClick(GameBinding.RtsSelect, click);
        bool gestureAdd = BindingClaimsClick(GameBinding.RtsSelectAdd, click);
        bool gestureControl = BindingClaimsClick(GameBinding.CrpgTakeControl, click);
        bool gestureOrder = BindingClaimsClick(GameBinding.RtsOrderMove, click);
        bool gestureQueue = BindingClaimsClick(GameBinding.RtsOrderQueueWaypoint, click);

        // Both guards stay a SUPERSET of the physical button. Sub-handlers inside each branch
        // are not gestures of their own — the NPC-dev focus set (Ctrl+LeftClick), the encounter
        // puppet select, the armed patrol draft's right-clicks — and gating them behind a
        // rebindable chord would strand them the moment someone reseats Select or Move Order.
        if (click.Button == MouseButton.Left || gestureSelect || gestureAdd || gestureControl)
        {
            if (_freecamMarqueeConsumedClick)
            {
                _freecamMarqueeConsumedClick = false;
                return;
            }
            // Benilla targeting freezes the hovered subject on mouse-down. Command View shares
            // that gesture law: the normal target router has already consumed the press pick,
            // so re-picking here on release would lose moving bots and units whose posed mesh
            // changed while the button was held.
            ulong pickedUnit = pressPick.Armed
                ? pressPick.UnitGuid
                : PickUnit(click.Position);
            // NPC dev window focus set: Ctrl+LeftClick multi-selects creatures for the
            // "Selected only" overlay scope and consumes the click (ahead of the
            // take-command and marquee-clear behaviour below).
            if (HandleDevFocusClick(pickedUnit)) return;
            // Shift+Left-click ON a raid puppet GRABS it for an orbit sweep around the boss.
            // Must beat the ground-staging order below, which would otherwise stage a
            // waypoint at the body's own feet instead of grabbing it.
            if (HandleEncounterOrbitGrab(click, pickedUnit)) return;
            // Shift+LeftClick with raid bodies selected stages a waypoint — the
            // owner's original gesture ("shift click the floor"); right-click
            // still works. Must run before selection handling, or this click
            // would CLEAR the very selection it is ordering.
            if (gestureAdd && HandleEncounterRtsOrder(click)) return;
            // Shift+LeftClick on hostiles is the kill-queue gesture. It deliberately
            // precedes ordinary target selection so adding an enemy never drops the
            // highlighted command subjects.
            if (gestureAdd && pickedUnit != 0 &&
                _entities.TryGet(pickedUnit, out WorldEntity queuedTarget) &&
                !queuedTarget.IsDead && CanAttack(queuedTarget))
            {
                if (TryQueueTacticalAttack(pickedUnit)) return;
                List<ulong> queuedSubjects =
                    [.. RtsControlGroupLaw.NormalizeMembers(_freecamSelection)];
                QueueRtsAttack(queuedSubjects, pickedUnit);
                return;
            }
            // Encounter Lab raid puppet: clicking a sim body SELECTS it for orders,
            // never takes command — there is no character behind it to command.
            if (HandleEncounterPuppetSelect(pickedUnit)) return;
            // RTS selection and direct body possession are separate operations. Plain click
            // selects, Shift+click edits the set, and Alt+click explicitly takes control.
            if (pickedUnit != 0)
                foreach (ulong guid in FreeCamSelectableGuids())
                    if (guid == pickedUnit)
                    {
                        if (gestureControl)
                        {
                            _freecamSelection.Clear();
                            _freecamSelection.Add(guid);
                            EnsureBotBarForViewing(guid);
                            if (guid != LocalPlayerGuid && IsRtsDirectlyControllableBot(guid))
                                SwitchControlTo(guid);
                            else if (guid != LocalPlayerGuid)
                                ShowUiError("That faction bot is selectable but cannot be directly controlled right now.");
                            return;
                        }
                        if (gestureAdd)
                        {
                            if (!_freecamSelection.Remove(guid))
                                _freecamSelection.Add(guid);
                        }
                        else
                        {
                            _freecamSelection.Clear();
                            _freecamSelection.Add(guid);
                            PlayCompanionSelectionVoice(guid);
                        }
                        if (_freecamSelection.Count == 1)
                            EnsureBotBarForViewing(_freecamSelection[0]);
                        return;
                    }
            // Targeting an enemy (a unit that is not one of your commandable subjects) sets it as
            // the focus target but must NOT drop the command selection — you keep control of your
            // group so Focus/attack orders still have subjects, and the card can show who the
            // primary is fighting. Only an empty-ground click, which has nothing to target, clears
            // the group (deselect-all).
            CommitSelection(pickedUnit, beginAttack: false);
            if (pickedUnit == 0) _freecamSelection.Clear();
            return;
        }
        if (click.Button != MouseButton.Right && !gestureOrder && !gestureQueue) return;

        // A right-hold that flew the camera by keyboard (WASD, mouse still) is a camera gesture,
        // not an order - suppress the whole right-click order path. Mouse-rotate already
        // disqualifies itself through pixel travel; this is the keyboard-pan equivalent.
        if (_freecamRightPanned) { _freecamRightPanned = false; return; }

        // Shift+Right-click ON a waypoint dot orients it (assign if none, else spin 45°) —
        // right-click because the left button is busy with selection/marquee, which was
        // eating the click. On empty ground Shift+Right falls through to the order below.
        if (HandleEncounterWaypointOrient(click)) return;

        // A selection carrying Encounter Lab raid puppets orders the SIM, not the
        // server — the whole gesture routes there and stops. Sim orders land at
        // the scrub head and the fight replays around them instantly.
        if (HandleEncounterRtsOrder(click)) return;

        // An armed Patrol draft owns right-clicks: ground picks CHAIN cold
        // waypoints (nothing is ordered until the second Patrol click), and
        // attack picks are swallowed so a stray click cannot break the flow.
        if (_rtsPatrolAuthoring)
        {
            if (TryPickGround(click.Position, out System.Numerics.Vector3 draftPoint))
            {
                _rtsPatrolDraft.Add(draftPoint);
                _rtsMoveMarkers.Add((draftPoint, NowSeconds(), RtsNeutralTint));
                SetRtsControlGroupStatus($"Patrol draft: {_rtsPatrolDraft.Count} point" +
                    $"{(_rtsPatrolDraft.Count == 1 ? "" : "s")} — Patrol engages, Escape cancels.");
            }
            return;
        }

        // SHIFT, not Ctrl, is the shipped default for queue-this-order: Ctrl is the
        // control-chord modifier (Ctrl+F, Ctrl+Tab), so entering the Command View with Ctrl still
        // down turned the very first right-click into a chained waypoint instead of a move.
        // Shift is also what every RTS uses, so the collision fix is the conventional binding —
        // and it is now only the DEFAULT of Chain Waypoint, which the player may reseat.
        bool queue = gestureQueue;
        // The commanded toon is ordered like any other member — no filtering. In the Command View
        // a possessed bot IS orderable: SuiPossess::orderBot waives its IsPossessed() bail when
        // the possessor holds a freecam eye, because the conflict that bail guards (a server
        // MOVE_TO fighting the client's movement stream) cannot arise when the client's
        // controller is a detached camera and its stream is parked. Commanding a character
        // from the sky gives you the same character you would have driving it directly.
        List<ulong> subjects = [.. RtsControlGroupLaw.NormalizeMembers(_freecamSelection)];
        if (_freecamSelection.Count > subjects.Count)
            ShowUiError($"Orders are limited to {RtsControlGroupLaw.MaximumWireSubjects} " +
                "explicit bots; this order uses the first entries in the selection.");

        float pickedUnitHit = float.PositiveInfinity;
        ulong picked = pressPick.Armed
            ? pressPick.UnitGuid
            : PickUnit(click.Position, out pickedUnitHit);

        // While the explicit lock is active every ordinary Command View move/attack gesture becomes
        // a KOTOR queue record. The same click is consumed for a frozen non-owner, who is read-only.
        if (TacticalFreezeBlocksLiveCommands)
        {
            // Only the owner of a currently active lock authors queue records. A foreign lock or
            // an owned FIFO that is merely draining consumes the gesture without staging any of
            // the legacy move/attack/interaction buffers below.
            if (OwnedActiveTacticalLock is not null)
            {
                if (picked != 0 && _entities.TryGet(picked, out WorldEntity tacticalTarget) &&
                    !tacticalTarget.IsDead && CanAttack(tacticalTarget))
                    TryQueueTacticalAttack(picked);
                else if (pressPick.Armed && pressPick.GroundPoint is Vector3 tacticalLatched)
                    TryQueueTacticalMove(tacticalLatched);
                else if (TryPickGround(click.Position, out Vector3 tacticalGround))
                    TryQueueTacticalMove(tacticalGround);
                else
                    FrozenInputConsumed();
            }
            else
                FrozenInputConsumed();
            return;
        }
        // Right-click on a lootable corpse (owner, 2026-09-02): loot it as your own character.
        // In reach it opens now; out of reach the character walks there and the loot opens on
        // arrival (RTS style), since loot is session-scoped and cannot be done "as a bot".
        if (picked != 0 && !queue && ControlledGuid == LocalPlayerGuid &&
            _entities.TryGet(picked, out WorldEntity lootCorpse) && lootCorpse.IsCreature &&
            lootCorpse.IsDead && lootCorpse.Fields.Lootable)
        {
            BeginCommandViewInteraction(CommandViewInteractKind.Loot, picked, lootCorpse);
            return;
        }

        // Any living, non-hostile NPC that offers MORE THAN ONE thing (a quest giver who is also
        // a flight master / vendor / trainer / banker / innkeeper, a vendor who also trains...)
        // asks first (owner, 2026-09-02: Thor's flight path could not be discovered because the
        // quest window took the click; 2026-09-03: "any NPC that has more than 1 option"): one
        // button per offer, and nothing walks or opens until one is picked.
        // [SUI] Model B: NOT driving anyone — a right-click on a QUEST GIVER opens the
        // commander quest window (per-member eligibility cards + accept-for-party), no
        // possession needed — but not before the party is AT the giver (owner, 2026-09-03: "that
        // shouldn't happen until the characters are at the NPC"): the highlighted set (or the
        // whole party) walks up and the window opens on arrival, like every other interaction.
        if (picked != 0 && !queue && _entities.TryGet(picked, out WorldEntity offerNpc) &&
            offerNpc.IsCreature && !offerNpc.IsDead &&
            ReactionTargetTowardPlayer(offerNpc) != FactionReaction.Hostile &&
            (offerNpc.NpcFlags & GossipNpcFlags) != 0)
        {
            bool commanderQuests = _controlState != ControlState.Possessing && _partyGiverQuestsAvailable;
            List<ConfirmPopupUiLaw.NpcOption> offers =
                ConfirmPopupUiLaw.NpcOptions(EffectiveNpcFlags(offerNpc), commanderQuests);
            if (ConfirmPopupUiLaw.NeedsChooser(offers))
            {
                // Walk up first; the chooser is raised on arrival (owner 2026-09-03: "immediate
                // dialog on click" from across the field was wrong — "no dialog until we are
                // physically there").
                EmitInterface("command-view", "npc-choose", "WALKING", picked,
                    $"npcFlags=0x{offerNpc.NpcFlags:X8};offers={string.Join('|', offers.Select(o => o.Caption))}");
                BeginCommandViewInteraction(CommandViewInteractKind.Choose, picked, offerNpc);
                return;
            }
            if (commanderQuests && (offerNpc.NpcFlags & NpcQuestGiver) != 0)
            {
                BeginCommandViewInteraction(CommandViewInteractKind.Quests, picked, offerNpc,
                    walkers: CommandViewQuestWalkers());
                return;
            }
            // A single offer: the service branch below routes it from the cursor verdict.
        }
        // A right-click on a SERVICE NPC (vendor / trainer / banker / auctioneer / flight master /
        // gossip) is an interaction, never a ground order: the acting body walks up and the panel
        // opens on arrival. Driving a party bot ([SUI] P4b) the interaction is the bot's — the
        // server runs the handlers as GetSuiActor; a plain commander interacts as the logged-in
        // character, which is what standing next to a vendor from the sky means (owner,
        // 2026-09-02: "it should just path there and open it").
        if (picked != 0 && !queue && _entities.TryGet(picked, out WorldEntity svcNpc) &&
            svcNpc.IsCreature && !svcNpc.IsDead && WorldCursorServiceKind(svcNpc) is not null)
        {
            BeginCommandViewInteraction(CommandViewInteractKind.Service, picked, svcNpc);
            return;
        }
        // A usable game object (mailbox, chest, lever, portal...) strictly in front of any unit:
        // the same walk-then-use, gated by UseGameObject's own type/lock/body rules on arrival.
        ulong goPicked = pressPick.Armed
            ? pressPick.GameObjectGuid
            : PickGameObject(click.Position, picked == 0 ? float.PositiveInfinity : pickedUnitHit, out _);
        if (goPicked != 0 && !queue && _entities.TryGet(goPicked, out WorldEntity clickedGo) &&
            clickedGo.IsGameObject && GameObjectHighlightable(clickedGo))
        {
            BeginCommandViewInteraction(CommandViewInteractKind.GameObject, goPicked, clickedGo);
            return;
        }
        // Chain (owner 2026-09-03: "chain 2 players and 2 others — not just main to main"):
        // right-click a PARTY MEMBER with a selection and the selection follows THAT member.
        // The server stores the anchor per unit and the roster reports it back to the
        // party-portrait and command-card indicators.
        if (picked != 0 && !queue && subjects.Count > 0 && IsPartyMemberGuid(picked) &&
            _entities.TryGet(picked, out WorldEntity anchorUnit) && !anchorUnit.IsDead)
        {
            List<ulong> followers = subjects.Where(guid => guid != picked).ToList();
            if (followers.Count == 0) return;
            if (!TrySendLiveSuiOrder(5, followers, picked, 0, 0, 0)) return;
            NoteCompanionOrder(5, followers);
            _rtsMoveMarkers.Add((anchorUnit.Position, NowSeconds(), RtsFriendlyTint));
            AddChatMessage($"{OrderSubjectLabel(followers)}: chain to {ResolveWorldUnitName(picked)}.");
            return;
        }
        if (picked != 0 && _entities.TryGet(picked, out WorldEntity target) &&
            !target.IsDead && CanAttack(target))
        {
            if (subjects.Count == 0 && !queue) return;   // nothing highlighted, no accidental orders
            if (queue)
                QueueRtsAttack(subjects, picked);
            else
            {
                ClearRtsAttackQueue();
                if (!TrySendLiveSuiOrder(1, subjects, picked, 0, 0, 0)) return;
                NoteCompanionOrder(1, subjects);
                _rtsMoveMarkers.Add((target.Position, NowSeconds(), RtsHostileTint));
                ClearRtsWaypointChain();
                AddChatMessage($"{OrderSubjectLabel(subjects)}: attack {ResolveWorldUnitName(picked)}!");
            }
        }
        else if (TryPickGround(click.Position, out System.Numerics.Vector3 point, out bool onTerrain))
        {
            if (!CommandViewOrderPointAllowed(point, onTerrain, out string refusal))
            {
                ShowUiError(refusal);
                return;
            }
            ClearRtsAttackQueue();
            if (queue)
            {
                // Shift+RightClick chains a waypoint (whole party when nothing is highlighted).
                if (_rtsWaypointChain.Count > 0 &&
                    !SameRtsMembers(_rtsWaypointSubjects, subjects))
                    ClearRtsWaypointChain();
                if (!TrySendLiveSuiOrder(3, subjects, 0, point.X, point.Y, point.Z)) return;
                NoteCompanionOrder(3, subjects);
                // Re-anchor the chain's ownership on every leg: the highlighted set can change
                // between clicks, and the route belongs to whoever was last told to walk it.
                _rtsWaypointSubjects.Clear();
                _rtsWaypointSubjects.AddRange(subjects);
                _rtsWaypointProgressAt = NowSeconds();
                _rtsWaypointChain.Add(point);
                _rtsMoveMarkers.Add((point, NowSeconds(), RtsNeutralTint));
                AddChatMessage($"{OrderSubjectLabel(subjects)}: waypoint {_rtsWaypointChain.Count} " +
                    $"({point.X:F0}, {point.Y:F0}).");
            }
            else
            {
                if (subjects.Count == 0) return;   // plain move needs a highlighted set
                // Coalesced: rapid move-spam collapses to the newest destination (throttled in
                // IssueRtsMoveOrder) instead of flooding the server one packet per click.
                IssueRtsMoveOrder(subjects, point);
            }
        }
    }

    /// <summary>
    /// Apply the immediate, presentation-only half of a direct movement order. The server still
    /// owns pathfinding and every position update; locally we only cancel stale action poses,
    /// restart locomotion phase, and face each subject toward the clicked destination. Queued
    /// waypoints deliberately do not call this: a future leg must not turn a body off its current
    /// path before the server starts that leg.
    /// </summary>
    private void BeginRtsMovePresentation(IReadOnlyList<ulong> subjects, Vector3 destination)
    {
        IEnumerable<ulong> ordered = subjects.Count == 0 ? FreeCamSelectableGuids() : subjects;
        var seen = new HashSet<ulong>();
        foreach (ulong guid in ordered)
        {
            if (!seen.Add(guid)) continue;
            _entities.PredictServerMoveFacing(guid, destination);
            _creatures?.InterruptActionForMovement(guid);
        }
    }

    private string OrderSubjectLabel(List<ulong> subjects) => subjects.Count switch
    {
        0 => "Party",
        1 => ResolveUnitName(subjects[0]),
        _ => $"Selection ({subjects.Count})",
    };

    private string ResolveWorldUnitName(ulong guid)
    {
        if (_playerNames.TryGetValue(guid, out string? playerName)) return playerName;
        if (_entities.TryGet(guid, out WorldEntity unit) && unit.IsCreature &&
            ResolveCreatureOrPetName(unit, "") is { Length: > 0 } creatureName) return creatureName;
        return "target";
    }

    private void CycleControl(int direction)
    {
        // Own character first, then the roster's controllable bots in roster order.
        List<ulong> ring = [LocalPlayerGuid];
        foreach ((ulong guid, byte flags) in _suiRoster)
            if ((flags & SuiRosterControllable) != 0 &&
                ((flags & SuiRosterPossessed) == 0 || guid == _controlTargetGuid))
                ring.Add(guid);
        if (ring.Count <= 1)
        {
            ShowUiError("No controllable party bots.");
            return;
        }

        int index = ring.IndexOf(ControlledGuid);
        if (index < 0) index = 0;
        ulong next = ring[(index + direction + ring.Count) % ring.Count];
        SwitchControlTo(next);
    }

    /// <summary>Jump control to a unit, chaining release→possess when already possessing.</summary>
    internal void SwitchControlTo(ulong guid)
    {
        if (guid == ControlledGuid) return;
        if (RefuseTacticalFreezeLiveCommand("changing control")) return;
        switch (_controlState)
        {
            case ControlState.OwnChar when guid != LocalPlayerGuid:
            case ControlState.FreeCam when guid != LocalPlayerGuid:
                RequestPossess(guid);
                break;
            case ControlState.FreeCam:
                // Clicked the own character with nobody possessed. In the Command View that is
                // already the state; landing on it is Ctrl+F's job, not a click's.
                break;
            case ControlState.Possessing when guid == LocalPlayerGuid:
                // Hand the bot back. From the sky this drops to commanding nobody and the
                // camera stays up; on the ground it returns you to your own body as before.
                RequestControlRelease(toFreecam: false);
                break;
            case ControlState.Possessing:
                _controlSwitchQueued = guid;      // resumes in ApplySuiControlAck
                RequestControlRelease(toFreecam: false);
                break;
        }
    }

    private string ResolveUnitName(ulong guid) =>
        _playerNames.TryGetValue(guid, out string? name) ? name : $"unit {guid:X}";

    /// <summary>
    /// Free-view selection overlay — only the live marquee rectangle lives in screen
    /// space now. Selection rings are world-space ground decals (RenderRtsGroundFx) and
    /// the members inside the rectangle light up through the renderer highlight.
    /// </summary>
    private void DrawFreeCamSelectionOverlay()
    {
        if (!_freeView) return;
        var draw = ImGui.GetForegroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;

        // Dashed connector through the waypoint chain, so the route reads at a glance.
        if (_rtsWaypointChain.Count > 1)
        {
            Vector2? previous = null;
            foreach (Vector3 waypoint in _rtsWaypointChain)
            {
                if (!_window.Camera.TryWorldToScreen(waypoint, display, out Vector2 screen))
                { previous = null; continue; }
                if (previous is Vector2 from) DrawDashedLine(draw, from, screen, 0xAA55D8F0, 10f, 7f);
                previous = screen;
            }
        }

        // The armed Patrol draft draws in gold: authored, not yet ordered.
        if (_rtsPatrolDraft.Count > 1)
        {
            Vector2? previous = null;
            foreach (Vector3 waypoint in _rtsPatrolDraft)
            {
                if (!_window.Camera.TryWorldToScreen(waypoint, display, out Vector2 screen))
                { previous = null; continue; }
                if (previous is Vector2 from) DrawDashedLine(draw, from, screen, 0xAA00D1FF, 10f, 7f);
                previous = screen;
            }
        }

        // Dynamic red numerals use the same vanilla quest-marker font as the !/?
        // companion labels. The active target is 1; remaining targets follow in order.
        float markerScale = GameplayUiScale();
        for (int i = 0; i < _rtsAttackQueue.Count; i++)
        {
            if (!_entities.TryGet(_rtsAttackQueue[i], out WorldEntity queued) || queued.IsDead)
                continue;
            Vector3 anchor = UnitWorldPosition(queued) + new Vector3(0f, 0f,
                UnitOverheadHeight(queued) + QuestMarkerUiLaw.NumeralClearanceYards);
            if (_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen))
                GameText.DrawCentered(draw, QuestMarkerUiLaw.NumeralFontObject,
                    (i + 1).ToString(), screen, markerScale, 0xff4c4cff);
        }

        if (!_freecamDragActive || _freecamDragOrigin is not Vector2 origin) return;
        Vector2 mouse = _window.MousePosition;
        Vector2 min = Vector2.Min(origin, mouse);
        Vector2 max = Vector2.Max(origin, mouse);
        draw.AddRectFilled(min, max, 0x2240E080);
        draw.AddRect(min, max, 0xCC40E080);
    }

    private static void DrawDashedLine(ImDrawListPtr draw, Vector2 from, Vector2 to,
        uint color, float dash, float gap, float thickness = 2f)
    {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length < 1f) return;
        Vector2 dir = delta / length;
        for (float at = 0f; at < length; at += dash + gap)
        {
            float end = MathF.Min(at + dash, length);
            draw.AddLine(from + dir * at, from + dir * end, color, thickness);
        }
    }

    /// <summary>Party members the live marquee rectangle currently covers (drag preview).</summary>
    private void AddMarqueePreview(ISet<ulong> set)
    {
        if (!_freeView ||
            !_freecamDragActive || _freecamDragOrigin is not Vector2 origin) return;
        Vector2 mouse = _window.MousePosition;
        Vector2 min = Vector2.Min(origin, mouse);
        Vector2 max = Vector2.Max(origin, mouse);
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (ulong guid in FreeCamSelectableGuids())
        {
            if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
            if (!_window.Camera.TryWorldToScreen(unit.Position, display, out Vector2 feet)) continue;
            if (feet.X >= min.X && feet.X <= max.X && feet.Y >= min.Y && feet.Y <= max.Y)
                set.Add(guid);
        }
    }

    /// <summary>
    /// World-space RTS ground FX: depth-tested selection rings under every selected unit
    /// (the model occludes the far arc) and the animated move-confirm markers. Runs in the
    /// 3-D render pass after units have populated depth — never from the HUD.
    /// </summary>
    private void RenderRtsGroundFx()
    {
        if (_spellEffectMeshes is null) return;
        _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
        double now = NowSeconds();
        _rtsMoveMarkers.RemoveAll(m => now - m.Born > 0.9);

        if (_freeView)
        {
            List<SpellEffectMeshRenderer.UnitRing> rings = [];
            float pulse = 0.80f + 0.15f * MathF.Sin((float)(now * 3.0));
            foreach (ulong guid in _freecamSelection)
            {
                if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
                float radius = 1.05f * MathF.Max(0.5f, unit.Scale <= 0f ? 1f : unit.Scale);
                rings.Add(new(unit.Position, radius, RtsFriendlyTint, pulse));
            }
            // A non-party pick (mob clicked from the sky) rings in its reaction colour.
            if (_selectionGuid != 0 && !_freecamSelection.Contains(_selectionGuid) &&
                _entities.TryGet(_selectionGuid, out WorldEntity target) && !target.IsDead)
            {
                FactionReaction reaction = ReactionTargetTowardPlayer(target);
                Vector3 tint = SelectionRingLaw.TargetRgb(reaction, isDead: false,
                    combatFlash: _attackTargetGuid == target.Guid,
                    MovementInfo.ClientUptimeMs());
                float radius = _creatures?.SelectionRadius(target) ??
                    1.05f * MathF.Max(0.5f, target.Scale <= 0f ? 1f : target.Scale);
                rings.Add(new(target.Position, radius, tint, pulse));
            }
            // Waypoint-chain dots: small persistent rings until a plain move/stop replaces them.
            foreach (Vector3 waypoint in _rtsWaypointChain)
                rings.Add(new(waypoint, 0.40f, RtsNeutralTint, 0.55f));
            // Patrol-draft dots pulse brighter: authored, awaiting the engage click.
            foreach (Vector3 waypoint in _rtsPatrolDraft)
                rings.Add(new(waypoint, 0.40f, RtsNeutralTint, 0.90f));

            if (rings.Count > 0)
                _spellEffectMeshes.RenderSelectionRings(_window.Camera, rings);
        }

        if (_rtsMoveMarkers.Count > 0)
        {
            List<SpellEffectMeshRenderer.MoveMarker> markers = [];
            foreach ((Vector3 pos, double born, Vector3 tint) in _rtsMoveMarkers)
                markers.Add(new(pos, (float)(now - born), tint));
            _spellEffectMeshes.RenderMoveMarkers(_window.Camera, markers);
        }
    }

    /// <summary>Body-play seat of the control guide: just under the 192-tall minimap cluster.
    /// The minimap's restore tab for a hidden guide hangs in the same band.</summary>
    private const float ControlGuideBodyTop = 200f;

    /// <summary>The control guide has something to say: the Command View is up, or a body
    /// other than your own is (about to be) driven. Shared with the minimap's restore tab.</summary>
    private bool ControlGuideApplies =>
        _freeView ||
        _controlState == ControlState.Possessing ||
        _controlState == ControlState.PossessPending ||
        _controlState == ControlState.ReleasePending;


    // Command View Widget Buttons
    private const string CommandViewIconInventory = @"Interface\Icons\INV_Misc_Bag_08";
    private const string CommandViewIconTactics = @"Interface\Icons\INV_Misc_Map_01";

    private void DrawInventoryButton()
    {
        if (!_freeView)
            return;

        float scale = GameplayUiScale();

        HudFrameResult frame = HudFrame(
            "inventory-button",
            "Inventory",
            HudPlacement.At(HudAnchor.BottomLeft, 203f, -5f),
            new Vector2(40f, 40f));

        if (frame.Hidden)
            return;

        ImGui.SetNextWindowPos(frame.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.ScreenSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        ImGui.Begin("##inventory-button",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoNav);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = new Vector2(40f, 40f) * scale;
        Vector2 max = pos + size;

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##inventory-button-hitbox", size);

        bool hovered = ImGui.IsItemHovered();

        uint art = PainterlyArt(CommandViewIconInventory);
        if (art != 0)
            dl.AddImage((nint)art, pos, max);

        DrawBevel(
            dl,
            pos,
            max,
            MathF.Max(1f, scale),
            PainterlyStoneTop,
            PainterlyFrameOuter);

        dl.AddRect(
            pos,
            max,
            hovered ? PainterlyFrameRule : PainterlyFrameOuter,
            0,
            ImDrawFlags.None,
            MathF.Max(1f, scale));

        if (hovered)
            HoverTip("Inventory");

        if (ImGui.IsItemClicked())
        {
            if (_partyInventoryOpen)
                _partyInventoryOpen = false;
            else
                OpenPartyInventory(
                    RtsPrimaryGuid != 0 ? RtsPrimaryGuid : LocalPlayerGuid);
        }

        ImGui.End();
    }

    private void DrawTacticsButton()
    {
        if (!_freeView)
            return;

        ulong tacticsBot = _freecamSelection.FirstOrDefault(IsRtsGroupableBot);

        if (tacticsBot == 0)
            tacticsBot = _partyMembers
                .FirstOrDefault(m => IsRtsGroupableBot(m.Guid))
                ?.Guid ?? 0;

        float scale = GameplayUiScale();

        HudFrameResult frame = HudFrame(
            "tactics-button",
            "Tactics",
            HudPlacement.At(HudAnchor.BottomLeft, 243f, -5f),
            new Vector2(40f, 40f));

        if (frame.Hidden)
            return;

        ImGui.SetNextWindowPos(frame.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.ScreenSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        ImGui.Begin("##tactics-button",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoNav);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetWindowPos();

        Vector2 size = new Vector2(40f, 40f) * scale;
        Vector2 max = pos + size;

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##tactics-icon", size);

        bool hovered = ImGui.IsItemHovered();

        uint art = PainterlyArt(CommandViewIconTactics);

        if (art != 0)
            dl.AddImage((nint)art, pos, max);

        DrawBevel(
            dl,
            pos,
            max,
            MathF.Max(1f, scale),
            PainterlyStoneTop,
            PainterlyFrameOuter);

        dl.AddRect(
            pos,
            max,
            hovered ? PainterlyFrameRule : PainterlyFrameOuter,
            0,
            ImDrawFlags.None,
            MathF.Max(1f, scale));

        if (hovered)
        {
            HoverTip(tacticsBot != 0
                ? "Rotations & Raid Plan - choose your role, then Auto-assign"
                : "Rotations & Raid Plan - prepare your encounter");
        }

        if (ImGui.IsItemClicked())
        {
            if (_partyTacticsOpen)
                _partyTacticsOpen = false;
            else
                OpenPartyTactics(tacticsBot);
        }

        ImGui.End();
    }

    // RTS Control Guide Overlay
    private void DrawControlBanner()
    {
        if (!ControlGuideApplies)
            return;

        DrawRtsControlGroups();
        DrawRtsCommandShelf();
        DrawInventoryButton();
        DrawTacticsButton();
        // The view-angle knob is its own furniture (Interface Options -> Command View), NOT part
        // of the help panel: it must survive the guide's Hide/Disable. The guide stacks above it.
        float knobHeight = DrawCommandViewAngleKnob();

        // Only the help panel is controlled by this
        if (!_enableControlGuide)
            return;

        var io = ImGui.GetIO();

        // Collapsed state: the restore affordance is a vanilla tab on the minimap in BOTH
        // views (GameLoop.Minimap.cs, DrawControlGuideMinimapTab) - mortised into the square
        // map's lower-left edge in the Command View, hung under the round map in body play.
        // Owner 2026-09-03: no ImGui button floating over the action bars when Hide is pressed.
        if (!_showControlGuide)
            return;


        // Expanded state: fixed carved furniture instead of ImGui's default debug-window skin.
        float scale = GameplayUiScale();
        // Command View: the panel sizes itself to its longest line (no line may clip) and to its
        // line count; the player may drag its left edge wider, never past the screen's centre
        // (owner, 2026-09-02: "too wide ... resizable, min width the longest sentence").
        string[] guideLines = _freeView ? CommandViewGuideLines() : [];
        string guideStatus = _freeView ? $"COMMAND VIEW  ·  {CommandViewGuideStatus()}" : "";
        float guideMinWidth = 0f, guideMaxWidth = 0f;
        Vector2 guideSize;
        if (_freeView)
        {
            float content = GameText.MeasureWidth("GameFontNormalSmall", guideStatus, scale) / scale;
            foreach (string line in guideLines)
                content = MathF.Max(content, MeasureControlGuideLine(line, scale) / scale);
            float header = 14f + GameText.MeasureWidth("GameFontNormal", "COMMANDER GUIDE", scale) / scale
                + 8f + ControlGuideHeaderButtonsWidth;
            guideMinWidth = MathF.Ceiling(MathF.Max(content + 2f * ControlGuideTextInset, header));
            guideMaxWidth = MathF.Max(guideMinWidth, (io.DisplaySize.X * 0.5f - 20f) / scale);
            float width = Settings.Controls.CommandViewGuideWidth > 0f
                ? Math.Clamp(Settings.Controls.CommandViewGuideWidth, guideMinWidth, guideMaxWidth)
                : guideMinWidth;
            guideSize = new Vector2(width, 69f + guideLines.Length * ControlGuideLinePitch + 13f) * scale;
        }
        else
            guideSize = new Vector2(330f, 112f) * scale;
        // Two authored placements of one registered frame. Body play: under the minimap, top
        // right, clear of the action bars and bags it used to cover when a Ctrl+Tab brought it
        // up (owner, 2026-09-03). Command View: stacked above the camera tablet when that is
        // drawn, else in the tablet's corner - its OWN frame in the layout registry (owner,
        // 2026-09-03: the guide is moved on its own, it does not ride the tablet).
        float guideLift = knobHeight > 0f ? knobHeight / scale + 8f : 0f;
        HudFrameResult guide = HudFrame("control-guide",
            _freeView ? "Commander guide" : "Control guide",
            HudPlacement.At(HudAnchor.TopRight, -8f, ControlGuideBodyTop),
            guideSize / scale,
            authoredCommand: HudPlacement.At(HudAnchor.BottomRight, -20f, -20f - guideLift));
        if (guide.Hidden) return;   // hidden in the active HUD layout (Edit Mode's Hide)
        ImGui.SetNextWindowPos(guide.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(guideSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        ImGui.Begin("##control-guide",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus);

        ImDrawListPtr guideDraw = ImGui.GetWindowDrawList();
        Vector2 guideMin = ImGui.GetWindowPos();
        Vector2 guideMax = guideMin + guideSize;
        DrawRtsConsoleBackdrop(guideDraw, guideMin, guideMax, scale);

        float rule = MathF.Max(1f, scale);
        float headerBottom = guideMin.Y + 32f * scale;
        guideDraw.AddRectFilled(guideMin + new Vector2(5f) * scale,
            new Vector2(guideMax.X - 5f * scale, headerBottom), PainterlyFrameTitle);
        guideDraw.AddLine(new Vector2(guideMin.X + 7f * scale, headerBottom),
            new Vector2(guideMax.X - 7f * scale, headerBottom), PainterlyFrameOuter, rule * 2f);
        guideDraw.AddLine(new Vector2(guideMin.X + 7f * scale, headerBottom - rule),
            new Vector2(guideMax.X - 7f * scale, headerBottom - rule), PainterlyGoldShade, rule);
        GameText.Draw(guideDraw, "GameFontNormal", _freeView ? "COMMANDER GUIDE" : "CONTROL GUIDE",
            guideMin + new Vector2(14f, 9f) * scale, scale, PainterlyGoldLit);

        if (VanillaButton(guideDraw, "##control-guide-hide", "Hide",
                new Vector2(guideMax.X - 124f * scale, guideMin.Y + 6f * scale),
                new Vector2(48f, 20f), scale))
            _showControlGuide = false;
        // Disable asks through the stock StaticPopup (GameLoop.Confirms.cs answers it), not an
        // ImGui modal - owner 2026-09-03, same rule as the restore tab.
        if (VanillaButton(guideDraw, "##control-guide-disable", "Disable",
                new Vector2(guideMax.X - 70f * scale, guideMin.Y + 6f * scale),
                new Vector2(62f, 20f), scale))
        {
            bool dead = _entities.TryGet(ControlledGuid, out WorldEntity guidePlayer) && guidePlayer.IsDead;
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
                _staticPopupSlots, ConfirmPopupUiLaw.DisableControlGuideDefinition, dead));
        }


        if (_freeView)
        {
            GameText.Draw(guideDraw, "GameFontNormalSmall", guideStatus,
                guideMin + new Vector2(ControlGuideTextInset, 42f) * scale, scale, PainterlyGoldLit);
            float statusRuleY = guideMin.Y + 61f * scale;
            guideDraw.AddLine(new Vector2(guideMin.X + 12f * scale, statusRuleY),
                new Vector2(guideMax.X - 12f * scale, statusRuleY), PainterlyFrameOuter, rule);

            Vector2 linePos = guideMin + new Vector2(ControlGuideTextInset, 69f) * scale;
            foreach (string line in guideLines)
            {
                DrawControlGuideLine(guideDraw, line, linePos, scale);
                linePos.Y += ControlGuideLinePitch * scale;
            }
            DrawControlGuideResizeGrip(guideDraw, guideMin, guideMax, scale, guideMinWidth, guideMaxWidth);
        }
        else
        {
            Vector2 linePos = guideMin + new Vector2(15f, 43f) * scale;
            switch (_controlState)
            {
                case ControlState.Possessing:
                    GameText.Draw(guideDraw, "GameFontNormalSmall",
                        $"CONTROLLING  ·  {ResolveUnitName(_controlTargetGuid)}", linePos, scale,
                        PainterlyGoldLit);
                    linePos.Y += 22f * scale;
                    DrawControlGuideLine(guideDraw,
                        $"{BindingHint(GameBinding.CrpgCycleControlNext)}: Switch character", linePos, scale);
                    linePos.Y += 16f * scale;
                    DrawControlGuideLine(guideDraw,
                        $"{BindingHint(GameBinding.RtsToggleFreeView)}: Command View", linePos, scale);
                    break;

                case ControlState.PossessPending:
                    GameText.Draw(guideDraw, "GameFontNormalSmall", "Taking control…", linePos,
                        scale, PainterlyGoldLit);
                    break;

                case ControlState.ReleasePending:
                    GameText.Draw(guideDraw, "GameFontNormalSmall", "Releasing control…", linePos,
                        scale, PainterlyGoldLit);
                    break;
            }
        }

        ImGui.End();

        if (_controlState == ControlState.Possessing)
            DrawBotBarLayerToggle(io.DisplaySize.Y - 80);
    }


    /// <summary>Draw a guide row in the UI's FrameXML font objects: the binding through the first
    /// colon is gilt, while the action remains the warmer highlight text used by vanilla panels.</summary>
    private static void DrawControlGuideLine(ImDrawListPtr dl, string text, Vector2 pos, float scale)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0)
        {
            GameText.Draw(dl, "GameFontHighlightSmall", text, pos, scale);
            return;
        }

        string binding = text[..(colon + 1)];
        string action = text[(colon + 1)..];
        GameText.Draw(dl, "GameFontNormalSmall", binding, pos, scale, PainterlyGoldLit);
        GameText.Draw(dl, "GameFontHighlightSmall", action,
            pos + new Vector2(GameText.MeasureWidth("GameFontNormalSmall", binding, scale), 0f), scale);
    }

    /// <summary>Device-pixel width of a guide row as <see cref="DrawControlGuideLine"/> lays it out.</summary>
    private static float MeasureControlGuideLine(string text, float scale)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0) return GameText.MeasureWidth("GameFontHighlightSmall", text, scale);
        return GameText.MeasureWidth("GameFontNormalSmall", text[..(colon + 1)], scale)
            + GameText.MeasureWidth("GameFontHighlightSmall", text[(colon + 1)..], scale);
    }

    // Commander Guide layout (logical px): text inset from the panel edge, row pitch, the room
    // the Hide/Disable pair takes from the right edge, and the width of the left-edge drag grip.
    private const float ControlGuideTextInset = 15f;
    private const float ControlGuideLinePitch = 16f;
    private const float ControlGuideHeaderButtonsWidth = 124f;
    private const float ControlGuideGripWidth = 8f;
    private bool _controlGuideResizing;
    private float _controlGuideResizeStartMouseX;
    private float _controlGuideResizeStartWidth;

    /// <summary>The Commander Guide's status line: who the Command View is acting for.</summary>
    private string CommandViewGuideStatus() =>
        _controlState == ControlState.Possessing
            ? $"Commanding {ResolveUnitName(_controlTargetGuid)}"
            : BarsReadOnly
                ? $"{ResolveUnitName(BarsGuid)} Selected"
                : "Selection ready";

    /// <summary>The Commander Guide's rows. Movement lines follow the scheme picked in Interface
    /// Options → Command View; the lock row follows the camera's lock state.</summary>
    private string[] CommandViewGuideLines()
    {
        CommandViewScheme guideScheme = Settings.Controls.CommandViewScheme;
        bool guideSwap = CommandViewLaw.TurnKeysStrafe(guideScheme);
        return
        [
            $"{BindingHint(GameBinding.RtsSelect)}/drag: Select    {BindingHint(GameBinding.RtsSelectAdd)}: Add",
            $"{BindingHint(GameBinding.CrpgTakeControl)}: Direct control",
            $"{BindingHint(GameBinding.RtsMoveForward)}/{BindingHint(GameBinding.RtsMoveBackward)}: " +
                (CommandViewLaw.OrbitsFocus(guideScheme) ? "Pan forward/back" : "Fly forward/back"),
            guideSwap
                ? $"{BindingHint(GameBinding.RtsTurnLeft)}/{BindingHint(GameBinding.RtsTurnRight)}: Sidestep"
                : $"{BindingHint(GameBinding.RtsTurnLeft)}/{BindingHint(GameBinding.RtsTurnRight)}: Turn    " +
                  $"{BindingHint(GameBinding.RtsStrafeLeft)}/{BindingHint(GameBinding.RtsStrafeRight)}: Sidestep",
            CommandViewLaw.PitchLocked(guideScheme)
                ? (CommandViewLaw.OrbitsFocus(guideScheme)
                    ? "Right-drag: Orbit    PageUp/PageDown: View angle"
                    : "Right-drag: Turn    PageUp/PageDown: View angle")
                : "Right-drag: Look around",
            CommandViewLaw.WheelIsVertical(guideScheme)
                ? "Wheel: Raise/lower camera"
                : "Wheel: Fly toward/away",
            $"{BindingHint(GameBinding.TargetNearestEnemy)}: Target enemy    " +
                $"{BindingHint(GameBinding.RtsCyclePrimaryNext)}: Next selected card",
            CommandViewLocked
                ? $"{BindingHint(GameBinding.RtsLockCameraPrimary)}: Release camera    " +
                  $"{BindingHint(GameBinding.RtsTurnLeft)}/{BindingHint(GameBinding.RtsTurnRight)}: Orbit"
                : $"{BindingHint(GameBinding.RtsLockCameraPrimary)}: Lock camera on primary",
            $"{BindingHint(GameBinding.RtsSelectAllParty)}: Select whole party",
            $"{BindingHint(GameBinding.RtsOrderMove)}: Move / attack",
            $"{BindingHint(GameBinding.RtsOrderQueueWaypoint)}: Chain waypoints",
            $"{BindingHint(GameBinding.RtsRecallGroup1)}-{BindingHint(GameBinding.RtsRecallGroup10)}: Select control group",
            $"{BindingHint(GameBinding.RtsSaveGroup1)}-{BindingHint(GameBinding.RtsSaveGroup10)}: Set control group",
            $"{BindingHint(GameBinding.RtsToggleFreeView)}: Exit Command View",
        ];
    }

    /// <summary>
    /// Drag the Commander Guide's LEFT edge (the panel is anchored bottom-right) to set its width;
    /// the result persists in Settings. Clamped to the content width and the screen centre, so a
    /// row can never clip and the panel can never cross the middle of the screen. No ImGui widget:
    /// a hover rect, the mouse buttons and a gilt rule on the draw list.
    /// </summary>
    private void DrawControlGuideResizeGrip(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale,
        float minWidth, float maxWidth)
    {
        Vector2 mouse = ImGui.GetIO().MousePos;
        Vector2 gripMax = new(min.X + ControlGuideGripWidth * scale, max.Y);
        bool hovered = ImGui.IsMouseHoveringRect(min, gripMax, false);
        if (hovered && !_controlGuideResizing && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _controlGuideResizing = true;
            _controlGuideResizeStartMouseX = mouse.X;
            _controlGuideResizeStartWidth = (max.X - min.X) / scale;
        }
        if (_controlGuideResizing)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                // Right-anchored: dragging LEFT widens.
                float width = _controlGuideResizeStartWidth +
                    (_controlGuideResizeStartMouseX - mouse.X) / scale;
                Settings.Controls.CommandViewGuideWidth = Math.Clamp(width, minWidth, maxWidth);
            }
            else
            {
                _controlGuideResizing = false;
                CommitSettings();
            }
        }
        if (!hovered && !_controlGuideResizing) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        float x = min.X + 3f * scale;
        dl.AddLine(new Vector2(x, min.Y + 8f * scale), new Vector2(x, max.Y - 8f * scale),
            PainterlyGoldLit, MathF.Max(1f, scale));
    }

    /// <summary>
    /// While driving a bot, bar edits are client-persisted; this picks the layer they land
    /// on — the named bot's override map, or the customization shared by its whole class.
    /// </summary>
    private void DrawBotBarLayerToggle(float y)
    {
        if (_freeView) return; // Hide bot bar edit target UI in Command Camera

        var io = ImGui.GetIO();
        string className = BotClassName(ControlledGuid, ResolveUnitName(ControlledGuid));
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, y - 20), ImGuiCond.Always,
            new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);

        if (ImGui.Begin("##botbar-layer", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextUnformatted("Bar edits save to:");
            ImGui.SameLine();
            if (ImGui.RadioButton("this bot", !_botBarSaveToClass))
                _botBarSaveToClass = false;

            ImGui.SameLine();

            if (ImGui.RadioButton(className.Length != 0 ? $"all {className}s" : "class",
                _botBarSaveToClass))
                _botBarSaveToClass = true;
        }

        ImGui.End();
    }

    private void FocusRtsPrimaryCamera()
    {
        ulong guid = RtsPrimaryGuid;
        if (guid == 0) return;

        FocusRtsCameraOnUnit(guid);
    }

}


