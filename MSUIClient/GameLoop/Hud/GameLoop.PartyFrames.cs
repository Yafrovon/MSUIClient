using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const string PartyInvitePopupType = PartyFrameUiLaw.PartyInvitePopupType;

    // Keep the complete wire roster. Social/Raid and Minimap consume every subgroup; the four
    // PartyMemberFrame tokens are a derived own-subgroup view and must never replace this list.
    private sealed record PartyMember(ulong Guid, string Name, byte Status, byte Subgroup, byte Flags);

    private sealed record PartyMemberView(
        PartyMember Member,
        WorldEntity? Unit,
        byte Status,
        bool Connected,
        bool Dead,
        bool Ghost,
        bool Pvp,
        uint Health,
        uint MaxHealth,
        byte PowerType,
        uint Power,
        uint MaxPower,
        uint Level);

    private sealed record PartyTooltipRuntime(PartyTooltipView View, Vector4 NameColor);

    private readonly List<PartyMember> _partyMembers = [];
    private readonly Dictionary<ulong, PartyMemberStatsSnapshot> _partyStats = [];
    private readonly ulong[] _partyRaidTargets = new ulong[8];
    private bool _partyInGroup;
    private byte _partyGroupType;
    private byte _partyOwnFlags;
    private ulong _partyLeaderGuid;
    private ulong _partyMasterLooterGuid;
    private byte _partyLootMethod;
    private byte _partyLootThreshold;
    // Provenance, not presentation: true only while /partytest owns the local mirror. Any real
    // SMSG_GROUP_LIST or session reset clears it, so synthetic rows can never outrank the wire.
    private bool _partyTestSandbox;
    private int _partyRosterRevision;
    // False until the first roster of a session has been seen. That first one is a relog into a
    // group you were already in, not a join, and must not sound the drum. Every later roster is a
    // real membership change — including the one you caused by accepting an invite, because
    // ResetParty (and therefore this flag) only runs on session teardown, never mid-session.
    private bool _partyRosterCueArmed;
    private StaticPopupCoordinatorLaw.Slots _staticPopupSlots =
        StaticPopupCoordinatorLaw.Slots.Empty;
    private long _staticPopupLastUpdateTicks = Stopwatch.GetTimestamp();
    private bool _partyInviteAccepted;
    /// <summary>The inviter resolved from the name-only vanilla packet, when already known.</summary>
    private ulong _partyInviteGuid;
    private string _partyInviteName = "";
    private readonly float[] _partyLowHealthTimers = new float[PartyFrameUiLaw.MemberCount];
    private double _partyLowHealthLastAt;
    private int _partyPressIndex = -1;
    private PartyPointerButton _partyPressButton;
    private PartyTooltipRuntime? _partyTooltip;
    private int _partyTooltipSlot = -1;
    private GameTooltipOwnerToken _partyTooltipOwnerToken;
    private bool _partyTooltipParityCompletionPending;
    private bool _partyTooltipParityRendererCollected;

    /// <summary>
    /// The party frames as seen from whoever is being DRIVEN, not from the session character.
    ///
    /// The wire roster always excludes the recipient, which is right while you are your own
    /// character. Possess a bot and the two halves swap: the bot becomes the player frame,
    /// so it has to leave the party list, and your abandoned body — now just another member
    /// running on AI — has to join it. Without this the possessed bot appeared TWICE (player
    /// frame plus its old party slot) and your real character vanished from the HUD entirely.
    /// </summary>
    private PartyMember[] PartyFrameMembers()
    {
        IEnumerable<PartyMember> roster = _partyMembers
            .Where(member => (member.Subgroup & 0x7f) == (_partyOwnFlags & 0x7f));

        ulong driven = ControlledGuid;
        // _partyInGroup, not _partyMembers.Count: the roster above is already subgroup-filtered,
        // so a raid subgroup empty of others would still pass a count check and draw a lone self
        // row. Without any gate an emptied roster kept one frame alive for the session character,
        // which read as "still in a group after leaving".
        if (driven != LocalPlayerGuid && _partyInGroup)
            roster = OwnCharacterPartyRow().Concat(roster.Where(member => member.Guid != driven));

        return roster.Take(PartyFrameUiLaw.MemberCount).ToArray();
    }

    /// <summary>
    /// The session character as a party row, for the possessed case. It is never in the wire
    /// roster (the server excludes the recipient), so the row is synthesised: online, own
    /// subgroup, no flags. Name comes from the same store the frames already resolve against.
    /// </summary>
    private IEnumerable<PartyMember> OwnCharacterPartyRow()
    {
        if (LocalPlayerGuid == 0) yield break;
        string name = _playerNames.TryGetValue(LocalPlayerGuid, out string? known)
            ? known : _net?.Player?.Name ?? "You";
        yield return new PartyMember(LocalPlayerGuid, name, 1, _partyOwnFlags, 0);
    }

    private ulong PartyFrameMemberGuid(int zeroBasedIndex)
    {
        PartyMember[] slots = PartyFrameMembers();
        return zeroBasedIndex >= 0 && zeroBasedIndex < slots.Length ? slots[zeroBasedIndex].Guid : 0;
    }

    private void ClearPartyTestNames()
    {
        _playerNames.Remove(PartyTestSandboxLaw.AliceGuid);
        _playerNames.Remove(PartyTestSandboxLaw.BobGuid);
        _playerNames.Remove(PartyTestSandboxLaw.CarolGuid);
        _playerNames.Remove(PartyTestSandboxLaw.DaveGuid);
    }

    private void ResetParty()
    {
        bool rosterChanged = _partyMembers.Count != 0 || _partyStats.Count != 0 ||
            _partyLeaderGuid != 0 || _partyMasterLooterGuid != 0 || _partyLootMethod != 0 ||
            _partyLootThreshold != 0 || _partyGroupType != 0 || _partyInGroup ||
            _partyOwnFlags != 0 || _partyRaidTargets.Any(guid => guid != 0);
        HidePartyInvite();
        _partyInviteGuid = 0;
        _partyInviteName = "";
        _partyMembers.Clear();
        _partyStats.Clear();
        Array.Clear(_partyRaidTargets);
        _partyInGroup = false;
        _partyGroupType = 0;
        _partyOwnFlags = 0;
        _partyLeaderGuid = 0;
        _partyMasterLooterGuid = 0;
        _partyLootMethod = 0;
        _partyLootThreshold = 0;
        if (_partyTestSandbox) ClearPartyTestNames();
        _partyTestSandbox = false;
        _partyRosterCueArmed = false;
        if (rosterChanged) _partyRosterRevision++;
        // PartyMemberFrame objects retain flashTimer while their unit token is absent. Session
        // teardown pauses the four slot timers; it does not recreate the frames.
        _partyLowHealthLastAt = 0;
        ClearPartyPress();
        BeginPartyTooltipDeparture(NowSeconds(), tokenExists: false);
        // The retained PartyMemberFrame token runs OnLeave after the roster disappears. Keep its
        // SetUnit row snapshot and slot token while the departure path immediately hides the
        // now-absent live health bar and starts the 0.5s fade.
    }

    // Build-5875 SMSG_GROUP_LIST: group type, local flags, recipient-excluded members, leader,
    // and (only when count > 0) loot method/master/threshold plus dungeon difficulty.
    private void ApplyPartyRoster(byte[] body)
    {
        PartyRosterWire wire = PartyFramePacketLaw.ParseRoster(body);
        if (_partyTestSandbox) ClearPartyTestNames();
        _partyTestSandbox = false;
        PartyRosterWireMember[] previous = _partyMembers
            .Select(member => new PartyRosterWireMember(member.Name, member.Guid, member.Status,
                (byte)(member.Subgroup | member.Flags)))
            .ToArray();
        string[] systemLines = GroupUiLaw.RosterLines(_partyGroupType, previous, wire);
        bool leaving = PartyFrameUiLaw.IsLeaveRoster(wire);
        // The all-zero leave reply clears GroupState. Its retained feed edge hides any pending
        // PARTY_INVITE popup, which closes first and then emits the OnHide decline intent.
        if (leaving) HidePartyInvite();
        var next = wire.Members.Select(member => new PartyMember(member.Guid, member.Name,
            member.Status, (byte)(member.MemberFlags & 0x7f),
            (byte)(member.MemberFlags & 0x80))).ToList();

        _partyMembers.Clear();
        _partyMembers.AddRange(next);
        foreach (PartyMember member in next)
        {
            // The roster carries the NAME, so the query is not asked for the name: it is asked
            // for the (race, class, gender) triple behind the party frame's portrait stand-in
            // and the raid grid's class column. Seeding the name cache alone suppressed the
            // ask-once query, so an unstreamed member had no portrait. 2026-09-01.
            if (!_playerTraits.ContainsKey(member.Guid)) _net?.NameQuery(member.Guid);
            if (!_playerNames.ContainsKey(member.Guid)) _playerNames[member.Guid] = member.Name;
        }
        _partyOwnFlags = wire.OwnFlags;
        _partyRosterRevision++;
        bool lootRulesChanged = _partyLeaderGuid != wire.LeaderGuid ||
            _partyLootMethod != wire.LootMethod ||
            _partyMasterLooterGuid != wire.MasterLooterGuid ||
            _partyLootThreshold != wire.LootThreshold;
        _partyLeaderGuid = wire.LeaderGuid;
        _partyLootMethod = wire.LootMethod;
        _partyMasterLooterGuid = wire.MasterLooterGuid;
        _partyLootThreshold = wire.LootThreshold;
        // SMSG_GROUP_LIST is the ONLY thing the server says about loot rules - 1.12 ships
        // ERR_SET_LOOT_MASTER but no FrameXML prints it and vmangos sends no chat line - so this
        // trace is how a change is provable without reading pixels off the player frame.
        if (lootRulesChanged && !leaving)
            EmitInterface("party", "loot-rules", "APPLIED", wire.LeaderGuid,
                $"method={wire.LootMethod};master=0x{wire.MasterLooterGuid:X16};" +
                $"threshold={wire.LootThreshold}");
        _partyInGroup = !leaving;
        _partyGroupType = leaving ? (byte)0 : wire.GroupType;
        if (leaving) Array.Clear(_partyRaidTargets);

        HashSet<ulong> retained = next.Select(x => x.Guid).ToHashSet();
        foreach (ulong guid in _partyStats.Keys.Where(x => !retained.Contains(x)).ToArray())
            _partyStats.Remove(guid);
        foreach (PartyMember member in next)
            if (PartyFrameUiLaw.Has(member.Status, PartyFrameUiLaw.Online) &&
                !_entities.TryGet(member.Guid, out _))
                _net?.RequestPartyMemberStats(member.Guid);
        foreach (string line in systemLines) AddChatMessage(line);

        bool anyoneJoined = wire.Members.Any(member =>
            !previous.Any(old => old.Guid == member.Guid));
        if (_partyRosterCueArmed && !leaving && anyoneJoined)
            PlayUiSound(PartyFrameUiLaw.MemberJoinedSound, "ui.party");
        _partyRosterCueArmed = true;

        // Icons set before this client joined are never re-announced; the board only ever fills
        // from a live delta. iconId 0xFF is the server's pull (GroupHandler.cpp:438 ->
        // Group::SendTargetIconList), and it is the only way to learn the existing marks.
        if (!leaving) _net?.RequestRaidTargets();
    }

    private void ApplyPartyTestRoster(bool lead)
    {
        short? playerX = null;
        short? playerY = null;
        if (_entities.TryGet(LocalPlayerGuid, out WorldEntity ownPlayer))
        {
            Vector3 position = UnitWorldPosition(ownPlayer);
            playerX = unchecked((short)position.X);
            playerY = unchecked((short)position.Y);
        }
        PartyTestSandboxLaw.FixtureMember[] fixture =
            PartyTestSandboxLaw.Roster(playerX, playerY);
        PartyRosterWireMember[] previous = _partyMembers
            .Select(member => new PartyRosterWireMember(member.Name, member.Guid, member.Status,
                (byte)(member.Subgroup | member.Flags)))
            .ToArray();
        var wire = new PartyRosterWire(0, 0,
            fixture.Select(member => new PartyRosterWireMember(member.Name, member.Guid,
                member.Status, 0)).ToArray(),
            lead ? (_net?.PlayerGuid ?? LocalPlayerGuid) : PartyTestSandboxLaw.AliceGuid,
            2, PartyTestSandboxLaw.CarolGuid, 3);
        string[] lines = GroupUiLaw.RosterLines(_partyGroupType, previous, wire);

        _partyMembers.Clear();
        _partyStats.Clear();
        foreach (PartyTestSandboxLaw.FixtureMember member in fixture)
        {
            _partyMembers.Add(new PartyMember(member.Guid, member.Name, member.Status, 0, 0));
            _partyStats[member.Guid] = member.Stats;
            _playerNames[member.Guid] = member.Name;
        }
        Array.Clear(_partyRaidTargets);
        _partyInGroup = true;
        _partyGroupType = 0;
        _partyOwnFlags = 0;
        _partyLeaderGuid = wire.LeaderGuid;
        _partyLootMethod = wire.LootMethod;
        _partyMasterLooterGuid = wire.MasterLooterGuid;
        _partyLootThreshold = wire.LootThreshold;
        _partyTestSandbox = true;
        _partyRosterRevision++;
        foreach (string line in lines) AddChatMessage(line);
    }

    private void ShowPartyTestInvite()
    {
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots,
            PartyFrameUiLaw.PartyInvitePopupDefinition,
            playerDeadOrGhost: false,
            dataToken: "Partner"));
    }

    private bool TryPartyTestUninvite(ulong guid)
    {
        if (!_partyTestSandbox) return false;
        _partyMembers.RemoveAll(member => member.Guid == guid);
        _partyStats.Remove(guid);
        _playerNames.Remove(guid);
        for (int i = 0; i < _partyRaidTargets.Length; i++)
            if (_partyRaidTargets[i] == guid) _partyRaidTargets[i] = 0;
        _partyRosterRevision++;
        return true;
    }

    private bool TryPartyTestPromote(ulong guid)
    {
        if (!_partyTestSandbox) return false;
        if (_partyMembers.Any(member => member.Guid == guid)) _partyLeaderGuid = guid;
        return true;
    }

    private bool TryPartyTestLeave()
    {
        if (!_partyTestSandbox) return false;
        ResetParty();
        return true;
    }

    private bool TryPartyTestLoot(byte method, ulong master, byte threshold)
    {
        if (!_partyTestSandbox) return false;
        _partyLootMethod = method;
        _partyMasterLooterGuid = method == 2 ? master : 0;
        _partyLootThreshold = threshold;
        return true;
    }

    private bool TryPartyTestRaidTarget(ulong guid, byte requested)
    {
        if (!_partyTestSandbox) return false;
        PartyTestSandboxLaw.ApplyRaidTarget(_partyRaidTargets, guid, requested);
        return true;
    }

    private void ApplyPartyMemberStats(byte[] body, bool fullSnapshot)
    {
        PartyMemberStatsWire wire = PartyFramePacketLaw.ParseMemberStats(body);
        _partyStats.TryGetValue(wire.Guid, out PartyMemberStatsSnapshot previous);
        _partyStats[wire.Guid] = PartyFrameUiLaw.MergeStats(previous, wire.Snapshot, fullSnapshot);
    }

    private void ApplyPartyInvite(byte[] body)
    {
        string inviter = PartyFramePacketLaw.ParseInvite(body);
        _partyInviteName = inviter;
        _partyInviteGuid = _playerNames.FirstOrDefault(pair =>
            pair.Value.Equals(inviter, StringComparison.OrdinalIgnoreCase)).Key;
        AddChatMessage(GroupUiLaw.InvitedLine(inviter));
        bool playerDeadOrGhost = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots,
            PartyFrameUiLaw.PartyInvitePopupDefinition,
            playerDeadOrGhost,
            dataToken: inviter));
    }

    private void ApplyPartyDecline(byte[] body) =>
        AddChatMessage(GroupUiLaw.DeclinedLine(PartyFramePacketLaw.ParseDecline(body)));

    private void ApplyPartyUninvited(byte[] body)
    {
        PartyFramePacketLaw.ParseEmptyNotice(body, nameof(Op.SMSG_GROUP_UNINVITE));
        AddChatMessage(GroupUiLaw.UninvitedLine);
    }

    private void ApplyPartyDestroyed(byte[] body)
    {
        PartyFramePacketLaw.ParseEmptyNotice(body, nameof(Op.SMSG_GROUP_DESTROYED));
        foreach (string line in GroupUiLaw.DestroyedLines(_partyInGroup)) AddChatMessage(line);
    }

    private void ApplyPartyLeaderChanged(byte[] body)
    {
        string name = PartyFramePacketLaw.ParseLeaderChanged(body);
        AddChatMessage(GroupUiLaw.LeaderChangedLine(name, _net?.PlayerName));
    }

    private void ApplyPartyCommandResult(byte[] body)
    {
        PartyCommandResultWire wire = PartyFramePacketLaw.ParseCommandResult(body);
        foreach (string line in GroupUiLaw.CommandResultLines(wire)) AddChatMessage(line);
    }

    private void ApplyPartyRaidTargetUpdate(byte[] body)
    {
        PartyRaidTargetUpdateWire wire = PartyFramePacketLaw.ParseRaidTargetUpdate(body);
        if (wire.IsDelta)
            GroupUiLaw.ApplyRaidTarget(_partyRaidTargets, wire.Icon, wire.Guid);
        else
            GroupUiLaw.ApplyRaidTargetList(_partyRaidTargets, wire.Entries);
    }

    private void ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Plan plan)
    {
        // Slot ownership changes atomically before any callback, sound, or wire effect runs.
        _staticPopupSlots = plan.Slots;
        foreach (StaticPopupCoordinatorLaw.Effect effect in plan.Effects)
        {
            if (effect.Kind is StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound or
                StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound or
                StaticPopupCoordinatorLaw.EffectKind.EntrySound)
            {
                if (!string.IsNullOrEmpty(effect.Value)) PlayUiSound(effect.Value);
                continue;
            }
            if (effect.Type == GossipCodeUiLaw.PopupType)
            {
                ApplyGossipCodePopupEffect(effect);
                continue;
            }
            if (IsBattlefieldInvite(effect.Type))
            {
                ApplyBattlefieldPopupEffect(effect);
                continue;
            }
            if (effect.Type == AreaSpiritHealerUiLaw.PopupType)
            {
                ApplyAreaSpiritHealerPopupEffect(effect);
                continue;
            }
            if (effect.Type == PetUnlearnUiLaw.PopupType)
            {
                ApplyPetUnlearnPopupEffect(effect);
                continue;
            }
            if (effect.Type == TrainerServiceUiLaw.PopupType)
            {
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept) AcceptTrainerConfirmation();
                else if (effect.Kind is StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason or
                    StaticPopupCoordinatorLaw.EffectKind.CancelOverride or StaticPopupCoordinatorLaw.EffectKind.CancelClicked or
                    StaticPopupCoordinatorLaw.EffectKind.CancelTimeout) _trainerConfirmation = null;
                continue;
            }
            if (EquipBindingUiLaw.IsType(effect.Type))
            {
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept) AcceptEquipBinding();
                else if (effect.Kind is StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason or
                    StaticPopupCoordinatorLaw.EffectKind.CancelOverride or StaticPopupCoordinatorLaw.EffectKind.CancelClicked or
                    StaticPopupCoordinatorLaw.EffectKind.CancelTimeout or StaticPopupCoordinatorLaw.EffectKind.OnHide)
                    _pendingEquipBinding = null;
                continue;
            }
            if (DeleteItemUiLaw.IsDeletePopupType(effect.Type))
            {
                switch (effect.Kind)
                {
                    case StaticPopupCoordinatorLaw.EffectKind.Accept:
                        AcceptDeleteItem();
                        break;
                    case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
                    case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
                    case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
                    case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                        CancelDeleteItem();
                        break;
                }
                continue;
            }
            if (string.Equals(effect.Type, DuelFrameUiLaw.RequestedPopupType,
                    StringComparison.Ordinal))
            {
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
                {
                    if (!RefuseTacticalFreezeLiveCommand("accepting a duel") &&
                        !RefuseTacticalFrozenActor(_duelPopupChallenger,
                            "accept a duel from them"))
                        _net?.DuelAccepted(_duelArbiter);
                    _duelPopupChallenger = 0;
                }
                else if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.CancelClicked)
                {
                    _net?.DuelCancelled(_duelArbiter);
                    _duelPopupChallenger = 0;
                }
                else if (effect.Kind is StaticPopupCoordinatorLaw.EffectKind.OnHide or
                         StaticPopupCoordinatorLaw.EffectKind.CancelTimeout or
                         StaticPopupCoordinatorLaw.EffectKind.CancelOverride or
                         StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason)
                    _duelPopupChallenger = 0;
                // Timeout, replacement and a no-free-slot refusal are silent in the 1.12 entry.
                continue;
            }
            if (string.Equals(effect.Type, DuelFrameUiLaw.OutOfBoundsPopupType,
                    StringComparison.Ordinal))
                continue;
            if (effect.Type is EnchantConfirmUiLaw.BindPopupType or
                    EnchantConfirmUiLaw.ReplacePopupType)
            {
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
                    AcceptEnchantConfirmation();
                else if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Hide &&
                         _enchantConfirmation is { } enchant &&
                         string.Equals(EnchantPopupType(enchant), effect.Type,
                             StringComparison.Ordinal))
                    _enchantConfirmation = null;
                // The entries have button2 but no OnCancel callback. Their shared Hide lifecycle
                // clears the local pending question; no wire action is sent.
                continue;
            }
            if (FriendsFrameUiLaw.IsNamePopup(effect.Type))
            {
                switch (effect.Kind)
                {
                    case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                        Array.Clear(_friendNameInput);
                        break;
                    case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                        _socialPopupFocusRequested = true;
                        break;
                    case StaticPopupCoordinatorLaw.EffectKind.Hide:
                    case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                        _socialPopupFocusRequested = false;
                        _socialPopupEditFocused = false;
                        break;
                    case StaticPopupCoordinatorLaw.EffectKind.Accept:
                    case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                        SubmitSocialNamePopup(effect.Type);
                        break;
                }
                continue;
            }
            if (string.Equals(effect.Type, CharacterBindingsUiLaw.PopupType,
                    StringComparison.Ordinal))
            {
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
                    AcceptDeleteCharacterSpecificBindings();
                continue;
            }
            if (PetMenuUiLaw.IsPetPopup(effect.Type))
            {
                ApplyPetMenuPopupEffect(effect);
                continue;
            }
            if (string.Equals(effect.Type, GuildFrameUiLaw.InvitePopupType,
                    StringComparison.Ordinal))
            {
                ApplyGuildInvitePopupEffect(effect);
                continue;
            }
            if (string.Equals(effect.Type, GuildFrameUiLaw.AddMemberPopupType,
                    StringComparison.Ordinal))
            {
                ApplyGuildAddMemberPopupEffect(effect);
                continue;
            }
            if (GuildFrameUiLaw.IsMemberPopup(effect.Type))
            {
                ApplyGuildMemberPopupEffect(effect);
                continue;
            }
            if (string.Equals(effect.Type, GuildFrameUiLaw.AddRankPopupType,
                    StringComparison.Ordinal))
            {
                ApplyGuildAddRankPopupEffect(effect);
                continue;
            }
            if (ConfirmPopupUiLaw.IsConfirmPopup(effect.Type))
            {
                ApplyConfirmPopupEffect(effect);
                continue;
            }
            if (!string.Equals(effect.Type, PartyInvitePopupType, StringComparison.Ordinal)) continue;
            switch (effect.Kind)
            {
                case StaticPopupCoordinatorLaw.EffectKind.Accept:
                    if (_partyInviteGuid == 0 && _partyInviteName.Length != 0)
                        _partyInviteGuid = _playerNames.FirstOrDefault(pair =>
                            pair.Value.Equals(_partyInviteName,
                                StringComparison.OrdinalIgnoreCase)).Key;
                    if (!RefuseTacticalFreezeLiveCommand("accepting a party invitation") &&
                        !RefuseTacticalFrozenActor(_partyInviteGuid,
                            "accept a party invitation from them"))
                    {
                        _net?.GroupAccept();
                        _partyInviteAccepted = true;
                    }
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
                case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
                case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
                case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                    _net?.GroupDecline();
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                    _partyInviteAccepted = false;
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.OnHide:
                    if (!_partyInviteAccepted) _net?.GroupDecline();
                    _partyInviteAccepted = false;
                    _partyInviteGuid = 0;
                    _partyInviteName = "";
                    break;
            }
        }
        if (!AnyPetMenuPopupVisible() && _petPopupGuid != 0)
            ClearPetMenuPopupState();
    }

    private void HidePartyInvite() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, PartyInvitePopupType));

    private bool TryDismissStaticPopupOnEscape()
    {
        StaticPopupCoordinatorLaw.Plan plan;
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? social =
            FriendsFrameUiLaw.NamePopup(_staticPopupSlots);
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? petRename =
            PetMenuUiLaw.Visible(_staticPopupSlots, PetMenuUiLaw.RenamePopupType);
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? guildAdd =
            GuildFrameUiLaw.AddMemberPopup(_staticPopupSlots);
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? guildMember =
            GuildFrameUiLaw.MemberPopup(_staticPopupSlots);
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? guildRank =
            GuildFrameUiLaw.Popup(_staticPopupSlots, GuildFrameUiLaw.AddRankPopupType);
        var gossipCode = ConfirmPopupUiLaw.Visible(_staticPopupSlots, GossipCodeUiLaw.PopupType);
        if (_gossipCodeEditFocused && gossipCode is { } codeFocused)
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots, codeFocused.Slot);
        else if (_petRenameEditFocused && petRename is { } petFocused)
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots,
                petFocused.Slot);
        else if (_guildAddMemberEditFocused && guildAdd is { } guildFocused)
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots,
                guildFocused.Slot);
        else if (guildMember is { } guildMemberFocused &&
                 _guildNoteEditFocused[guildMemberFocused.Slot - 1])
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots,
                guildMemberFocused.Slot);
        else if (_guildAddRankEditFocused && guildRank is { } guildRankFocused)
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots,
                guildRankFocused.Slot);
        else if (_socialPopupEditFocused && social is { } focused)
            plan = StaticPopupCoordinatorLaw.EditBoxEscape(_staticPopupSlots, focused.Slot);
        else
            plan = StaticPopupCoordinatorLaw.Escape(_staticPopupSlots);
        ExecuteStaticPopupPlan(plan);
        return plan.Outcome != StaticPopupCoordinatorLaw.Outcome.NothingVisible;
    }

    private void UpdatePartyInviteLifecycle()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _staticPopupLastUpdateTicks;
        _staticPopupLastUpdateTicks = now;
        double elapsedSeconds = now >= previous
            ? (now - previous) / (double)Stopwatch.Frequency
            : 0;
        for (int slot = 1; slot <= StaticPopupCoordinatorLaw.SlotCount; slot++)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Advance(
                _staticPopupSlots, slot, elapsedSeconds));
        if (DeleteItemUiLaw.Visible(_staticPopupSlots) is { } stale && !HasCarriedItem)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, stale.Instance.Definition.Type));
    }

    private PartyMemberView BuildPartyMemberView(PartyMember member)
    {
        WorldEntity? unit = _entities.TryGet(member.Guid, out WorldEntity found) ? found : null;
        _partyStats.TryGetValue(member.Guid, out PartyMemberStatsSnapshot stats);
        byte status = PartyFrameUiLaw.EffectiveStatus(member.Status, stats.Status);
        bool connected = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Online);
        bool dead = unit?.IsDead == true || PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Dead);
        bool ghost = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Ghost);
        bool pvp = PartyFrameUiLaw.MergedPvp(status, unit?.Fields.UnitFlags);
        uint health = unit?.Fields.Health ?? stats.Health ?? 0;
        uint maxHealth = unit?.Fields.MaxHealth ?? stats.MaxHealth ?? 0;
        byte powerType = unit?.Fields.PowerType ?? stats.PowerType ?? 0;
        uint power = unit?.Fields.ActivePower ?? stats.Power ?? 0;
        uint maxPower = unit?.Fields.ActiveMaxPower ?? stats.MaxPower ?? 0;
        uint level = unit?.Fields.Level ?? stats.Level ?? 0;
        return new(member, unit, status, connected, dead, ghost, pvp, health, maxHealth,
            powerType, power, maxPower, level);
    }

    /// <summary>Resolved logical origin of PartyMemberFrame1 (HUD layout registry, PLAN_21).
    /// The member stack, role medallions, chain rail and drag feedback all hang off it.</summary>
    private Vector2 _partyFramesOrigin = new(PartyFrameUiLaw.FirstX, PartyFrameUiLaw.FirstY);

    private Vector2 PartyMemberLogicalOrigin(int index) =>
        _partyFramesOrigin + new Vector2(0f, PartyFrameUiLaw.PetlessStride * index);

    private void DrawPartyFrames()
    {
        if (_net is null || _gameplayArt is null) return;
        PartyMember[] members = PartyFrameMembers();
        if (members.Length == 0)
        {
            _partyLowHealthLastAt = 0;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
                ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ClearPartyPress();
            // Hiding the final owner frame runs its OnLeave path. Advance an existing tooltip's
            // fade even though there is no Party parity surface to arm or complete this frame.
            UpdateAndQueuePartyTooltip(-1, null, NowSeconds(), capture: false);
            return;
        }

        bool capture = _uiParityArmed && _uiParityPanel == "party-frame";
        float s = GameplayUiScale();
        HudFrameResult partyFrame = HudFrame("party-frames", "Party frames",
            HudPlacement.At(HudAnchor.TopLeft, PartyFrameUiLaw.FirstX, PartyFrameUiLaw.FirstY),
            new Vector2(PartyFrameUiLaw.FrameWidth,
                PartyFrameUiLaw.PetlessStride * (PartyFrameUiLaw.MemberCount - 1) + PartyFrameUiLaw.FrameHeight));
        _partyFramesOrigin = partyFrame.LogicalOrigin;
        if (partyFrame.Hidden)
        {
            // Hidden in the active HUD layout: the same stand-down as an empty party - release
            // a held press, let a tooltip fade - and the satellites (medallions, chain rail,
            // command strips) stay down with the stack because they draw from here.
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
                ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ClearPartyPress();
            UpdateAndQueuePartyTooltip(-1, null, NowSeconds(), capture: false);
            return;
        }
        Vector2 origin = _partyFramesOrigin * s;
        if (capture)
        {
            BeginUiParityFrame(origin, s);
            _partyTooltipParityCompletionPending = false;
            _partyTooltipParityRendererCollected = false;
        }
        double now = NowSeconds();
        float dt = _partyLowHealthLastAt <= 0 ? 0f :
            MathF.Max(0f, (float)(now - _partyLowHealthLastAt));
        _partyLowHealthLastAt = now;

        _partyChainRoster = members;
        int hoveredIndex = -1;
        PartyMemberView? hoveredView = null;
        for (int i = 0; i < members.Length; i++)
        {
            PartyMemberView view = BuildPartyMemberView(members[i]);
            if (DrawPartyMemberFrame(i, view, dt, capture))
            {
                hoveredIndex = i;
                hoveredView = view;
            }
        }
        if (capture)
            for (int i = members.Length; i < PartyFrameUiLaw.MemberCount; i++)
                ClassifyUiParity($"PartyMemberFrame{i + 1}", "Button", "UIParent", "NOT-DRAWN",
                    "own-subgroup-slot-is-empty");
        ClassifyUiParity("PartyMemberFrame5", "Button", "UIParent", "NOT-DRAWN",
            "reference-caps-party-slots-at-four");

        DrawRtsCommandStrips(members);
        DrawPartyDragFeedback(members);
        ResolvePartyPointerRelease(hoveredIndex, members);
        bool tooltipQueued = UpdateAndQueuePartyTooltip(hoveredIndex, hoveredView, now, capture);
        if (capture && !tooltipQueued) MarkUiParityFrameComplete();
    }

    private void ResolvePartyPointerRelease(int hoveredIndex, PartyMember[] members)
    {
        ImGuiMouseButton released = ImGuiMouseButton.COUNT;
        if (_partyPressIndex >= 0)
        {
            ImGuiMouseButton expected = _partyPressButton == PartyPointerButton.Left
                ? ImGuiMouseButton.Left : ImGuiMouseButton.Right;
            if (ImGui.IsMouseReleased(expected)) released = expected;
        }
        if (released == ImGuiMouseButton.COUNT) return;

        // [RTS] Divinity-style chain. Dropping a dragged portrait on ANOTHER portrait (or
        // the player frame) LINKS the member into the chain; dragging it away and letting
        // go in the open BREAKS its link — it stands its ground until re-chained.
        if (_partyPressButton == PartyPointerButton.Left &&
            _partyPressIndex >= 0 && _partyPressIndex < members.Length)
        {
            if ((hoveredIndex >= 0 && hoveredIndex != _partyPressIndex &&
                 hoveredIndex < members.Length) ||
                (hoveredIndex < 0 && MouseOverPlayerFrame()))
            {
                // Dropped on ANOTHER portrait: chain to THAT member (owner 2026-09-03); on the
                // player frame: back to the default anchor (the driven body).
                SetPartyChainAnchor(members[_partyPressIndex],
                    hoveredIndex >= 0 ? members[hoveredIndex].Guid : 0);
                SetPartyLink(members[_partyPressIndex], linked: true);
                ClearPartyPress();
                return;
            }
            if (hoveredIndex < 0 && PartyDragDistance(_partyPressIndex) > 60f)
            {
                SetPartyLink(members[_partyPressIndex], linked: false);
                ClearPartyPress();
                return;
            }
        }

        // PartyMemberFrameN is the pointer token. If its roster occupant rebinds while held,
        // ButtonUp still acts on that slot's current occupant, exactly like frame.unit.
        PartyPointerAction action = PartyFrameUiLaw.ReleaseAction(_partyPressIndex,
            hoveredIndex, _partyPressButton);
        if (action != PartyPointerAction.None && hoveredIndex >= 0 &&
            hoveredIndex < members.Length)
        {
            PartyMember member = members[hoveredIndex];
            if (_rtsUnitCastSpellId != 0)
            {
                if (_partyPressButton == PartyPointerButton.Right)
                    CancelRtsUnitCastTargeting(silent: false);
                else
                    TryCommitRtsUnitCastTarget(member.Guid);
                ClearPartyPress();
                return;
            }
            // Take Direct Control (CRPG Controls, Alt+Left Mouse by default) reaches the party
            // portraits too — same command, second surface. The portrait is an ImGui hit rather
            // than a world click, so the chord is matched against the live modifiers.
            if (action == PartyPointerAction.Target &&
                BindingClaimsPointerNow(GameBinding.CrpgTakeControl, BindingPointerKey.Button1))
                SwitchControlTo(member.Guid);
            else if (action == PartyPointerAction.Target)
                CommitSelection(member.Guid, beginAttack: false);
            else
            {
                // PARTY origin/token is explicit. Right-click intentionally leaves selection alone.
                OpenUnitPopup(member.Guid, UnitPopupWhich.Party,
                    (PartyMemberLogicalOrigin(hoveredIndex) + new Vector2(47, 15)) * GameplayUiScale(),
                    InspectBinding.Party(hoveredIndex));
            }
        }
        ClearPartyPress();
    }

    private void ClearPartyPress()
    {
        _partyPressIndex = -1;
    }

    private bool DrawPartyMemberFrame(int index, PartyMemberView view, float dt, bool capture)
    {
        if (_gameplayArt is null) return false;
        float s = GameplayUiScale();
        Vector2 p = PartyMemberLogicalOrigin(index) * s;
        Vector2 frameSize = new(PartyFrameUiLaw.FrameWidth * s, PartyFrameUiLaw.FrameHeight * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frameSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        bool begun = ImGui.Begin($"##vanilla-party-member-{index + 1}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return false; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector4 fullClip = new(0, 0, display.X, display.Y);
        string root = $"PartyMemberFrame{index + 1}";

        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"##party-member-hit-{index + 1}", frameSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        // The party frames are the surface hovercast exists for - healing without ever
        // moving your target off the thing you are fighting.
        NoteHovercastFrameHover(view.Member.Guid, hovered);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _partyPressIndex = index;
            _partyPressButton = PartyPointerButton.Left;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _partyPressIndex = index;
            _partyPressButton = PartyPointerButton.Right;
        }
        string interaction = ImGui.IsItemActive() ? "pressed" : hovered ? "hovered" : "normal";
        if (capture)
            CollectUiParityDraw(root, "Button", p, frameSize, "UIParent",
                new("", 0, "HOST", "TOPLEFT", "UIParent", "TOPLEFT",
                    PartyFrameUiLaw.FirstX, -PartyFrameUiLaw.MemberY(index),
                    ContentRect: new(p.X, p.Y, p.X + frameSize.X, p.Y + frameSize.Y),
                    ClipRect: fullClip, ClipMask: "UIParent", Visible: true, Enabled: true,
                    InteractionState: interaction, HitMin: p, HitMax: p + frameSize, Strata: "LOW"));

        float healthFraction = view.MaxHealth > 0
            ? Math.Clamp((float)view.Health / view.MaxHealth, 0, 1) : 0;
        float powerFraction = view.MaxPower > 0
            ? Math.Clamp((float)view.Power / view.MaxPower, 0, 1) : 0;
        bool low = view.MaxHealth > 0 && view.Health > 0 && healthFraction <= .2f;
        _partyLowHealthTimers[index] = PartyFrameUiLaw.AdvanceLowHealthTimer(
            _partyLowHealthTimers[index], exists: true, connected: view.Connected,
            lowLivingHealth: low, dt: dt);
        // FrameXML's OnUpdate changes alpha independently from the status RGB. A disconnect
        // pauses the slot timer and therefore retains its current alpha; a low-health ghost
        // remains blue while pulsing.
        float portraitAlpha = PartyFrameUiLaw.LowHealthAlpha(_partyLowHealthTimers[index]);
        Vector3 portraitRgb = !view.Connected ? new(.5f, .5f, .5f)
            : view.Dead ? new(.35f, .35f, .35f)
            : view.Ghost ? new(.2f, .2f, .75f)
            : low ? new(1, 0, 0)
            : Vector3.One;
        Vector4 portraitColor = new(portraitRgb, portraitAlpha);
        uint portraitTint = ImGui.ColorConvertFloat4ToU32(portraitColor);
        Vector2 portraitMin = p + new Vector2(7, 6) * s;
        Vector2 portraitSize = new Vector2(37) * s;

        dl.PushClipRectFullScreen();
        string portraitPath = "";
        uint bakedPortrait = PartyPortraitHandle(view.Member.Guid);
        if (bakedPortrait != 0)
        {
            // The real member: a live bake of its streamed body (geosets/hair/gear),
            // circular-masked like every round portrait. Tint carries the offline/
            // dead/ghost/low-health states unchanged.
            //
            // V IS FLIPPED. This is a render target, not a BLP: OpenGL's framebuffer
            // origin is bottom-left and ImGui's image origin is top-left, so the flat-art
            // UVs the fallback branch below uses turn a baked portrait upside down (which
            // is what made party members read as a slab of robe instead of a face).
            // Same convention as DrawPortrait and DrawUnitPortraitImage's live path.
            dl.AddImage((nint)bakedPortrait, portraitMin, portraitMin + portraitSize,
                new Vector2(0, 1), new Vector2(1, 0), portraitTint);
            if (capture)
                CollectUiParityDraw(root + "Portrait", "Texture", portraitMin, portraitSize, root,
                    new("party-member-baked-portrait", portraitTint, "BACKGROUND", "TOPLEFT", root,
                        "TOPLEFT", 7, -6, TexCoords: "0|0|1|1", ClipRect: fullClip,
                        ClipMask: "circular-alpha-mask;UIParent", BlendMode: "BLEND", Strata: "LOW"));
        }
        else if (view.Unit is not null)
        {
            (byte race, _, byte gender, _) = view.Unit.Fields.Bytes0;
            string sex = gender == 1 ? "Female" : "Male";
            string raceName = race == 5 ? "Scourge" : RaceName(race).Replace(" ", "");
            portraitPath = $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{raceName}";
            // Through the painterly path: these are character stand-ins, so they
            // belong to the painted set when the mode is on, and resolve to the
            // identical masked copy when it is off.
            uint portrait = PainterlyRoundArt(portraitPath);
            if (portrait != 0)
                dl.AddImage((nint)portrait, portraitMin, portraitMin + portraitSize,
                    Vector2.Zero, Vector2.One, portraitTint);
            if (capture && portrait != 0)
                CollectUiParityDraw(root + "Portrait", "Texture", portraitMin, portraitSize, root,
                    new(portraitPath, portraitTint, "BACKGROUND", "TOPLEFT", root, "TOPLEFT", 7, -6,
                        TexCoords: "0|0|1|1", ClipRect: fullClip,
                        ClipMask: "circular-alpha-mask;UIParent", BlendMode: "BLEND", Strata: "LOW"));
            else if (capture)
                ClassifyUiParity(root + "Portrait", "Texture", root, "NOT-DRAWN",
                    "temporary-player-portrait-asset-unavailable");
        }
        else if (_playerTraits.TryGetValue(view.Member.Guid, out PlayerTraits traits) && traits.Race != 0)
        {
            // Out of range (owner 2026-09-03): the stock client shows the generic portrait for
            // the member's race and sex, not a hollow ring. The name query already told us
            // both (SMSG_NAME_QUERY_RESPONSE carries race/class/gender), so draw the same
            // TemporaryPortrait the streamed fallback uses — the entity is simply not here.
            string sex = traits.Gender == 1 ? "Female" : "Male";
            string raceName = traits.Race == 5 ? "Scourge" : RaceName(traits.Race).Replace(" ", "");
            portraitPath = $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{raceName}";
            uint portrait = PainterlyRoundArt(portraitPath);
            if (portrait != 0)
                dl.AddImage((nint)portrait, portraitMin, portraitMin + portraitSize,
                    Vector2.Zero, Vector2.One, portraitTint);
            if (capture && portrait != 0)
                CollectUiParityDraw(root + "Portrait", "Texture", portraitMin, portraitSize, root,
                    new(portraitPath, portraitTint, "BACKGROUND", "TOPLEFT", root, "TOPLEFT", 7, -6,
                        TexCoords: "0|0|1|1", ClipRect: fullClip,
                        ClipMask: "circular-alpha-mask;UIParent", BlendMode: "BLEND", Strata: "LOW"));
            else if (capture)
                ClassifyUiParity(root + "Portrait", "Texture", root, "NOT-DRAWN",
                    "temporary-player-portrait-asset-unavailable");
        }
        else if (capture)
            ClassifyUiParity(root + "Portrait", "Texture", root, "NOT-DRAWN",
                "party-token-guid-is-not-streamed-and-unnamed");

        Vector2 healthMin = p + new Vector2(47, 12) * s;
        Vector2 barSize = new Vector2(70, 8) * s;
        float drawnHealth = view.Connected ? healthFraction : 1f;
        PartyTooltipHealthState memberHealth = PartyFrameUiLaw.MemberHealth(
            view.Connected, view.Health, view.MaxHealth);
        DrawVanillaStatusBar(dl, healthMin, barSize, drawnHealth,
            view.Connected ? new Vector4(0, 1, 0, 1) : new Vector4(.5f, .5f, .5f, 1));
        if (capture)
        {
            Vector4 color = view.Connected ? new(0, 1, 0, 1) : new(.5f, .5f, .5f, 1);
            CollectUiParityDraw(root + "HealthBar", "StatusBar", healthMin, barSize, root,
                new(@"Interface\TargetingFrame\UI-StatusBar", ImGui.ColorConvertFloat4ToU32(color),
                    "BARS", "TOPLEFT", root, "TOPLEFT", 47, -12,
                    TexCoords: $"0|0|{drawnHealth:R}|1",
                    ContentRect: new(healthMin.X, healthMin.Y,
                        healthMin.X + barSize.X * drawnHealth, healthMin.Y + barSize.Y),
                    ClipRect: fullClip, ClipMask: "UIParent", BlendMode: "BLEND",
                    Visible: true,
                    InteractionState:
                        $"min=0;max={memberHealth.Maximum};value={memberHealth.Value}",
                    Strata: "LOW"));
        }

        Vector2 powerMin = p + new Vector2(47, 21) * s;
        if (view.MaxPower > 0)
        {
            Vector4 powerColor = PowerColor(view.PowerType);
            DrawVanillaStatusBar(dl, powerMin, barSize, powerFraction, powerColor);
            if (capture)
                CollectUiParityDraw(root + "ManaBar", "StatusBar", powerMin, barSize, root,
                    new(@"Interface\TargetingFrame\UI-StatusBar",
                        ImGui.ColorConvertFloat4ToU32(powerColor), "BARS", "TOPLEFT", root,
                        "TOPLEFT", 47, -21, TexCoords: $"0|0|{powerFraction:R}|1",
                        ContentRect: new(powerMin.X, powerMin.Y,
                            powerMin.X + barSize.X * powerFraction, powerMin.Y + barSize.Y),
                        ClipRect: fullClip, ClipMask: "UIParent", BlendMode: "BLEND",
                        Visible: true,
                        InteractionState:
                            $"min=0;max={view.MaxPower};value={Math.Min(view.Power, view.MaxPower)}",
                        Strata: "LOW"));
        }
        else if (capture)
            ClassifyUiParity(root + "ManaBar", "StatusBar", root, "NOT-DRAWN",
                "max-power-is-zero");

        Vector2 artMin = p + new Vector2(0, 2) * s;
        Vector2 artSize = new Vector2(128, 64) * s;
        DrawArt(dl, @"Interface\TargetingFrame\UI-PartyFrame", artMin, new Vector2(128, 64), s);
        // Class-colour ring on the gilded portrait frame (issue #15) - read every member's
        // class at a glance. Drawn after the frame art so it lands on the ring, not under it.
        DrawClassPortraitRing(dl, portraitMin + portraitSize * 0.5f, portraitSize.X * 0.5f,
            view.Member.Guid, s, view.Member.Name);
        if (capture)
            CollectUiParityDraw(root + "/Frame/FrameTexture", "Texture", artMin, artSize, root,
                new(@"Interface\TargetingFrame\UI-PartyFrame", 0xffffffff, "ART", "TOPLEFT",
                    root, "TOPLEFT", 0, -2, ClipRect: fullClip, ClipMask: "UIParent",
                    BlendMode: "BLEND", Strata: "LOW"));

        (Vector2 nameMin, Vector2 nameSize) = DrawUnitFrameText(dl,
            p + new Vector2(83, 8) * s, view.Member.Name, 10 * s, UiGoldU32());
        if (capture)
            CollectUiParityDraw(root + "Name", "FontString", nameMin, nameSize, root,
                new("", UiGoldU32(), "TEXT", "CENTER", root, "TOPLEFT", 83, -8,
                    FontSize: 10, ClipRect: fullClip, ClipMask: "UIParent", Strata: "LOW"));

        TracePartyIcon(dl, capture, root, p, root + "LeaderIcon",
            @"Interface\GroupFrame\UI-Group-LeaderIcon", p, new Vector2(16) * s,
            view.Member.Guid == _partyLeaderGuid, fullClip, "member-is-not-leader");
        TracePartyIcon(dl, capture, root, p, root + "MasterIcon",
            @"Interface\GroupFrame\UI-Group-MasterLooter", p + new Vector2(32, 0) * s,
            new Vector2(16) * s,
            _partyLootMethod == 2 && view.Member.Guid == _partyMasterLooterGuid,
            fullClip, "member-is-not-master-looter");
        TracePartyIcon(dl, capture, root, p, root + "Disconnect",
            @"Interface\CharacterFrame\Disconnect-Icon", p + new Vector2(-7, -5) * s,
            new Vector2(64) * s, !view.Connected, fullClip, "member-is-connected");

        bool ffa = PartyFrameUiLaw.Has(view.Status, PartyFrameUiLaw.PvpFfa);
        bool pvp = view.Pvp;
        string pvpPath = @"Interface\TargetingFrame\UI-PVP-FFA";
        bool pvpIconVisible = ffa;
        string pvpHiddenReason = "member-is-not-pvp-flagged";
        if (!ffa && pvp)
        {
            byte? memberRace = view.Unit?.Fields.Bytes0.Race;
            byte? ownRace = _entities.TryGet(_net!.PlayerGuid, out WorldEntity own)
                ? own.Fields.Bytes0.Race : null;
            string? faction = PartyFrameUiLaw.PvpFaction(memberRace, ownRace);
            if (faction is not null)
            {
                pvpPath = $@"Interface\GroupFrame\UI-Group-PVP-{faction}";
                pvpIconVisible = true;
            }
            else pvpHiddenReason = "party-pvp-faction-is-unresolved";
        }
        TracePartyIcon(dl, capture, root, p, root + "PVPIcon", pvpPath,
            p + new Vector2(-9, 15) * s, new Vector2(32) * s, pvpIconVisible,
            fullClip, pvpHiddenReason);
        DrawPartyChainLinks(dl, index);
        dl.PopClipRect();
        ImGui.End();
        return hovered;
    }

    private void TracePartyIcon(ImDrawListPtr dl, bool capture, string root, Vector2 rootMin,
        string element, string path, Vector2 min, Vector2 size, bool visible, Vector4 clip,
        string hiddenReason)
    {
        if (visible) DrawArt(dl, path, min, size / GameplayUiScale(), GameplayUiScale());
        if (!capture) return;
        if (!visible)
        {
            ClassifyUiParity(element, "Texture", root, "NOT-DRAWN", hiddenReason);
            return;
        }
        CollectUiParityDraw(element, "Texture", min, size, root,
            new(path, 0xffffffff, "OVERLAY", "TOPLEFT", root, "TOPLEFT",
                (min.X - rootMin.X) / GameplayUiScale(),
                -(min.Y - rootMin.Y) / GameplayUiScale(),
                ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "LOW"));
    }

    private bool UpdateAndQueuePartyTooltip(int hoveredSlot, PartyMemberView? hovered,
        double now, bool capture)
    {
        GameTooltipRuntimeSnapshot shared = SharedGameTooltipSnapshot();
        bool exactOwner = _partyTooltip is not null &&
            SharedGameTooltipIsOwned(_partyTooltipOwnerToken);
        bool fading = exactOwner && shared.Lifecycle.FadeStartedAt is not null;

        if (hovered is not null)
        {
            bool beginSnapshot = PartyFrameUiLaw.BeginTooltipSnapshot(_partyTooltipSlot,
                hoveredSlot, _partyTooltip is not null, fading) || !exactOwner;
            if (beginSnapshot)
            {
                string? race = null, @class = null;
                if (hovered.Unit is { IsPlayer: true } unit)
                {
                    var bytes = unit.Fields.Bytes0;
                    if (bytes.Race != 0) race = RaceName(bytes.Race);
                    if (bytes.Class != 0) @class = ClassName(bytes.Class);
                }
                // Frozen SetUnit prints the PvP line from the ordinary pvp field only. FFA is a
                // separate icon state and must not manufacture this tooltip line.
                bool pvp = hovered.Pvp;
                Vector4 reaction = PartyFrameUiLaw.TooltipNameColor;
                _partyTooltip = new(PartyFrameUiLaw.Tooltip(hovered.Member.Name, hovered.Level,
                    race, @class, hovered.Dead,
                    pvp, hovered.Health, hovered.MaxHealth), reaction);
                _partyTooltipSlot = hoveredSlot;
                _partyTooltipOwnerToken = ClaimSharedGameTooltip(
                    PartyFrameUiLaw.TooltipOwner(hoveredSlot));
                if (!PublishSharedGameTooltip(_partyTooltipOwnerToken,
                        PartyFrameUiLaw.SharedTooltipContent(hoveredSlot, _partyTooltip.View)))
                {
                    ClearPartyTooltipRuntime();
                    if (capture)
                        ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                            "party-tooltip-publication-rejected");
                    return false;
                }
            }
        }
        else if (exactOwner)
        {
            BeginSharedGameTooltipFade(_partyTooltipOwnerToken, now,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_partyTooltip is null)
        {
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "no-party-member-hover-or-fade");
            return false;
        }
        if (!SharedGameTooltipIsOwned(_partyTooltipOwnerToken))
        {
            shared = SharedGameTooltipSnapshot();
            string reason = shared.Lifecycle.Owner is null
                ? "party-tooltip-fade-complete"
                : "shared-tooltip-owner-replaced";
            ClearPartyTooltipRuntime();
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                reason);
            return false;
        }

        PartyMember[] currentSlots = PartyFrameMembers();
        bool tokenExists = _partyTooltipSlot >= 0 && _partyTooltipSlot < currentSlots.Length;
        uint health = 0, maxHealth = 0;
        if (tokenExists)
        {
            PartyMemberView current = BuildPartyMemberView(currentSlots[_partyTooltipSlot]);
            health = current.Health;
            maxHealth = current.MaxHealth;
        }
        if (!TryRefreshSharedGameTooltipUnit(_partyTooltipOwnerToken,
                PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot, tokenExists,
                    health, maxHealth)))
        {
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-live-token-rejected");
            return false;
        }

        shared = SharedGameTooltipSnapshot();
        if (!shared.Lifecycle.Visible || shared.Lifecycle.Alpha <= 0f)
        {
            ClearPartyTooltipRuntime();
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-fade-complete");
            return false;
        }

        PartyTooltipRuntime rendererRuntime = _partyTooltip;
        PartyTooltipHealthState rendererHealth = new(shared.Health.Visible,
            shared.Health.Maximum, shared.Health.Value);
        float rendererAlpha = shared.Lifecycle.Alpha;
        bool rendererFading = shared.Lifecycle.FadeStartedAt is not null;
        bool queued = QueueSharedGameTooltipRenderer(_partyTooltipOwnerToken,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds), () =>
            {
                DrawPartyUnitTooltip(rendererRuntime, rendererHealth, rendererAlpha,
                    rendererFading, capture);
                if (capture) _partyTooltipParityRendererCollected = true;
            });
        if (queued && capture) _partyTooltipParityCompletionPending = true;
        if (!queued && capture)
            ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-renderer-queue-rejected");
        return queued;
    }

    private void BeginPartyTooltipDeparture(double now, bool tokenExists)
    {
        if (_partyTooltip is null || _partyTooltipSlot < 0 ||
            !SharedGameTooltipIsOwned(_partyTooltipOwnerToken))
            return;
        if (!tokenExists)
            TryRefreshSharedGameTooltipUnit(_partyTooltipOwnerToken,
                PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot, false, 0, 0));
        BeginSharedGameTooltipFade(_partyTooltipOwnerToken, now,
            GameTooltipUiLaw.WorldFadeSeconds);
    }

    private void ClearPartyTooltipRuntime()
    {
        _partyTooltip = null;
        _partyTooltipSlot = -1;
        _partyTooltipOwnerToken = default;
    }

    private void CompleteDeferredPartyTooltipParityCapture()
    {
        if (!_partyTooltipParityCompletionPending) return;
        bool rendererCollected = _partyTooltipParityRendererCollected;
        _partyTooltipParityCompletionPending = false;
        _partyTooltipParityRendererCollected = false;
        if (!rendererCollected)
            ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "shared-tooltip-owner-replaced-before-tooltip-stratum");
        MarkUiParityFrameComplete();
    }

    private void DrawPartyUnitTooltip(PartyTooltipRuntime runtime,
        PartyTooltipHealthState tooltipHealth, float alpha, bool fading, bool capture)
    {
        if (_skin is null || _gameplayArt is null) return;
        float s = GameplayUiScale();
        string[] lines = runtime.View.PvpLine is null
            ? [runtime.View.Name, runtime.View.LevelLine!]
            : [runtime.View.Name, runtime.View.LevelLine!, runtime.View.PvpLine];
        string[] fontObjects = lines.Select((_, index) => index == 0
            ? "GameTooltipHeaderText" : "GameTooltipText").ToArray();
        float[] rowWidths = lines.Select((line, index) =>
            GameText.MeasureWidth(fontObjects[index], line, s)).ToArray();
        float[] rowHeights = fontObjects.Select(font => (float)GameText.EmPixels(font, s)).ToArray();
        PartyTooltipLayout layout = PartyFrameUiLaw.TooltipLayout(rowWidths, rowHeights, s);
        float width = layout.Width;
        float height = layout.Height;
        Vector2 display = ImGui.GetIO().DisplaySize;
        bool multiBarLeftVisible = Enumerable.Range(36, 12).Any(slot => _actions[slot] is not null);
        bool multiBarRightVisible = Enumerable.Range(24, 12).Any(slot => _actions[slot] is not null);
        float tooltipRightOffset = PartyFrameUiLaw.TooltipRightOffset(
            multiBarLeftVisible, multiBarRightVisible);
        // Both bottom multibar frames are always present in MSUI. No normal-runtime reputation
        // watch exists; the parity-only fixture is not allowed to move an observational tooltip.
        float tooltipBottomOffset = PartyFrameUiLaw.TooltipBottomOffset(
            bottomLeftVisible: true, bottomRightVisible: true,
            petOrStanceVisible: PetOrStanceActionBarVisible, reputationVisible: false);
        Vector2 pos = new(display.X + tooltipRightOffset * s - width,
            display.Y - tooltipBottomOffset * s - height);
        Vector2 size = new(width, height);
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin("##party-unit-tooltip",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.Tooltip);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        var fadeTint = new Vector4(1, 1, 1, alpha);
        _skin.DrawBackdrop(dl, pos, pos + size, WowSkin.Tooltip, fadeTint, fadeTint);
        for (int i = 0; i < lines.Length; i++)
        {
            Vector4 color = i == 0 ? runtime.NameColor : Vector4.One;
            color.W *= alpha;
            GameText.Draw(dl, fontObjects[i], lines[i],
                pos + new Vector2(PartyFrameUiLaw.TooltipPadding * s, layout.RowTops[i]), s,
                ImGui.ColorConvertFloat4ToU32(color));
        }
        Vector2 barMin = pos + new Vector2(2 * s, size.Y + s);
        Vector2 barSize = new(size.X - 4 * s, 8 * s);
        float healthFraction = tooltipHealth.Visible
            ? Math.Clamp((float)tooltipHealth.Value / tooltipHealth.Maximum, 0, 1)
            : 0;
        uint bar = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill");
        if (tooltipHealth.Visible && bar != 0 && healthFraction > 0)
            dl.AddImage((nint)bar, barMin,
                new Vector2(barMin.X + barSize.X * healthFraction, barMin.Y + barSize.Y),
                Vector2.Zero, new Vector2(healthFraction, 1),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, alpha)));

        if (capture)
        {
            Vector4 clip = new(0, 0, display.X, display.Y);
            CollectUiParityDraw("GameTooltip", "GameTooltip", pos, size, "UIParent",
                new("", 0, "TOOLTIP", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT",
                    tooltipRightOffset, tooltipBottomOffset,
                    ClipRect: clip, ClipMask: "UIParent", Visible: true,
                    InteractionState: fading ? $"fading:{alpha:R}" : "shown",
                    Strata: "TOOLTIP"));
            CollectUiParityDraw("GameTooltip/Backdrop", "Backdrop", pos, size, "GameTooltip",
                new(@"Interface\Tooltips\UI-Tooltip-Background", 0xffffffff, "BACKGROUND",
                    "TOPLEFT", "GameTooltip", "TOPLEFT", 0, 0, ClipRect: clip,
                    ClipMask: "GameTooltip", BlendMode: "BLEND", Strata: "TOOLTIP"));
            for (int i = 0; i < lines.Length; i++)
            {
                Vector2 lineMin = pos + new Vector2(
                    PartyFrameUiLaw.TooltipPadding * s, layout.RowTops[i]);
                Vector2 lineSize = new(rowWidths[i], rowHeights[i]);
                Vector4 color = i == 0 ? runtime.NameColor : Vector4.One;
                color.W *= alpha;
                FontObjectSpec font = FontObjectLaw.Get(fontObjects[i]);
                CollectUiParityDraw($"GameTooltipTextLeft{i + 1}", "FontString", lineMin,
                    lineSize, "GameTooltip", new("", ImGui.ColorConvertFloat4ToU32(color),
                        "TEXT", "TOPLEFT", "GameTooltip", "TOPLEFT",
                        PartyFrameUiLaw.TooltipPadding, -(layout.RowTops[i] / s),
                        FontPath: font.Face, FontSize: font.Height,
                        ClipRect: clip, ClipMask: "GameTooltip", Strata: "TOOLTIP"));
            }
            if (tooltipHealth.Visible)
                CollectUiParityDraw("GameTooltipStatusBar", "StatusBar", barMin, barSize,
                    "GameTooltip", new(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill",
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, alpha)), "BARS",
                        "TOPLEFT", "GameTooltip", "BOTTOMLEFT", 2, -1,
                        TexCoords: $"0|0|{healthFraction:R}|1",
                        ContentRect: new(barMin.X, barMin.Y,
                            barMin.X + barSize.X * healthFraction, barMin.Y + barSize.Y),
                        ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND",
                        Visible: true, InteractionState:
                            $"min=0;max={tooltipHealth.Maximum};value={tooltipHealth.Value}",
                        Strata: "TOOLTIP"));
            else
                ClassifyUiParity("GameTooltipStatusBar", "StatusBar", "GameTooltip",
                    "NOT-DRAWN", "party-tooltip-slot-token-is-absent-during-fade");
        }
        dl.PopClipRect();
        ImGui.End();
    }

    private void DrawPartyInvite()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        string text = $"{visible.Instance.DataToken ?? ""} invites you to a group.";
        string[] lines = WrapTooltipText(text, "GameFontHighlight", s,
            PartyFrameUiLaw.PopupTextWidth * s).ToArray();
        float textHeight = lines.Length * GameText.LinePitch("GameFontHighlight", s);
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(PartyFrameUiLaw.PopupWidth * s,
            PartyFrameUiLaw.PopupHeight(textHeight / s) * s);
        Vector2 origin = StaticPopupOrigin(visible.Slot, PartyFrameUiLaw.PopupWidth, s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##party-invite", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool capture = _uiParityArmed && _uiParityPanel == "party-invite";
        if (capture) BeginUiParityFrame(origin, s);
        Vector4 clip = new(0, 0, display.X, display.Y);
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(dl, "GameFontHighlight", lines[i],
                origin + new Vector2(PartyFrameUiLaw.PopupWidth * .5f,
                    PartyFrameUiLaw.PopupTextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * s, s);
        float buttonTop = PartyFrameUiLaw.PopupButtonTop(textHeight / s);
        bool accept = DrawPartyInviteButton(dl, "StaticPopup1Button1", "Accept",
            origin + new Vector2(PartyFrameUiLaw.PopupButtonOneX, buttonTop) * s,
            s, capture, clip);
        bool decline = DrawPartyInviteButton(dl, "StaticPopup1Button2", "Decline",
            origin + new Vector2(PartyFrameUiLaw.PopupButtonTwoX, buttonTop) * s,
            s, capture, clip);
        if (capture)
        {
            CollectUiParityDraw("StaticPopup1", "Frame", origin, size, "UIParent",
                new("", 0, "HOST", "TOP", "UIParent", "TOP", 0, -128,
                    ClipRect: clip, ClipMask: "UIParent", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/Backdrop", "Backdrop", origin, size, "StaticPopup1",
                new(@"Interface\DialogFrame\UI-DialogBox-Background", 0xffffffff,
                    "BACKGROUND", "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0,
                    ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/BackdropBorder", "Texture", origin, size,
                "StaticPopup1", new(@"Interface\DialogFrame\UI-DialogBox-Border", 0xffffffff,
                    "BORDER", "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0,
                    ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "DIALOG"));
            for (int i = 0; i < lines.Length; i++)
            {
                Vector2 lineSize = new(GameText.MeasureWidth("GameFontHighlight", lines[i], s),
                    GameText.LinePitch("GameFontHighlight", s));
                Vector2 lineMin = origin + new Vector2((size.X - lineSize.X) * .5f,
                    PartyFrameUiLaw.PopupTextTop * s + i * lineSize.Y);
                CollectUiParityDraw($"StaticPopup1Text/Line{i + 1}", "FontString", lineMin,
                    lineSize, "StaticPopup1", new("", FontObjectLaw.Get("GameFontHighlight").Color,
                        "TEXT", "TOP", "StaticPopup1", "TOP", 0,
                        -(PartyFrameUiLaw.PopupTextTop +
                          i * GameText.LinePitch("GameFontHighlight", 1)),
                        FontPath: FontObjectLaw.Get("GameFontHighlight").Face,
                        FontSize: FontObjectLaw.Get("GameFontHighlight").Height,
                        ClipRect: clip, ClipMask: "UIParent", Strata: "DIALOG"));
            }
            ClassifyUiParity("StaticPopup1AlertIcon", "Texture", "StaticPopup1", "NOT-DRAWN",
                "PARTY_INVITE-showAlert-is-absent");
            MarkUiParityFrameComplete();
        }
        dl.PopClipRect();
        ImGui.End();
        // PARTY_INVITE callbacks neither keep the dialog open nor synchronously replace its type;
        // the shared click driver's callback-reentry branches remain an explicit later boundary.
        if (accept)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (decline)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }

    /// <summary>
    /// <paramref name="enabled"/> false renders the button inert and greyed - the destroy
    /// popup withholds Yes until the confirmation word is typed. The art here goes straight
    /// onto the draw list, which BeginDisabled cannot dim, so the caption carries the state.
    /// </summary>
    private bool DrawPartyInviteButton(ImDrawListPtr dl, string element, string caption,
        Vector2 min, float s, bool capture, Vector4 clip, bool enabled = true)
    {
        Vector2 size = new Vector2(PartyFrameUiLaw.PopupButtonWidth,
            PartyFrameUiLaw.PopupButtonHeight) * s;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton($"##party-{caption}", size);
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) { ImGui.EndDisabled(); clicked = false; }
        bool pushed = PartyFrameUiLaw.InviteButtonPushed(held, hovered);
        string statePath = pushed
            ? @"Interface\Buttons\UI-DialogBox-Button-Down"
            : @"Interface\Buttons\UI-DialogBox-Button-Up";
        uint art = _skin!.TextureHandle(pushed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint hi = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + size,
                Vector2.Zero, new Vector2(1f, .625f));
        }
        string fontObject = !enabled ? "GameFontDisable"
            : hovered ? "GameFontHighlight" : "GameFontNormal";
        GameText.DrawCentered(dl, fontObject, caption, min + size * .5f, s);
        if (capture)
        {
            string interaction = pushed ? "pressed" : hovered ? "hovered" : "normal";
            CollectUiParityDraw(element, "Button", min, size, "StaticPopup1",
                new("", 0, "FRAMES", "TOPLEFT", "StaticPopup1", "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / s,
                    -((min.Y - _uiParityOrigin.Y) / s), ClipRect: clip,
                    ClipMask: "UIParent", Visible: true, Enabled: true,
                    InteractionState: interaction, HitMin: min, HitMax: min + size,
                    Strata: "DIALOG"));
            CollectUiParityDraw(element + (pushed ? "/PushedTexture" : "/NormalTexture"),
                pushed ? "PushedTexture" : "NormalTexture", min, size, element,
                new(statePath, 0xffffffff, "ARTWORK", "TOPLEFT", element, "TOPLEFT", 0, 0,
                    TexCoords: "0|0|1|0.625", ClipRect: clip, ClipMask: "UIParent",
                    BlendMode: "BLEND", Strata: "DIALOG"));
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", min,
                    size, element, new(@"Interface\Buttons\UI-DialogBox-Button-Highlight",
                        0xffffffff, "HIGHLIGHT", "TOPLEFT", element, "TOPLEFT", 0, 0,
                        TexCoords: "0|0|1|0.625", ClipRect: clip, ClipMask: "UIParent",
                        BlendMode: "ADD", Strata: "DIALOG"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", "button-is-not-hovered");
            FontObjectSpec font = FontObjectLaw.Get(fontObject);
            Vector2 textSize = new(GameText.MeasureWidth(fontObject, caption, s),
                GameText.EmPixels(fontObject, s));
            Vector2 textMin = min + (size - textSize) * .5f;
            CollectUiParityDraw(element + "/Text", "FontString", textMin, textSize, element,
                new("", font.Color, "OVERLAY", "CENTER", element, "CENTER", 0, 0,
                    FontPath: font.Face, FontSize: font.Height, ClipRect: clip,
                    ClipMask: "UIParent", Strata: "DIALOG"));
        }
        return clicked;
    }
}
