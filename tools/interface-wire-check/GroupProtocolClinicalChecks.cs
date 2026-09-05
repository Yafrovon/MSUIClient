using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

internal static class GroupProtocolClinicalChecks
{
    public static void Run()
    {
        CheckOpcodesAndBuilders();
        CheckRosterWireAndStateLines();
        CheckNotificationWireAndLines();
        CheckMemberStatsWireAndMerge();
        CheckMinimapRaidTargetAndReadyWire();
        CheckRuntimeRoutes();
    }

    private static void CheckOpcodesAndBuilders()
    {
        Check((ushort)Op.CMSG_GROUP_INVITE == 0x006e &&
              (ushort)Op.SMSG_GROUP_INVITE == 0x006f &&
              (ushort)Op.CMSG_GROUP_ACCEPT == 0x0072 &&
              (ushort)Op.CMSG_GROUP_DECLINE == 0x0073 &&
              (ushort)Op.SMSG_GROUP_DECLINE == 0x0074 &&
              (ushort)Op.CMSG_GROUP_UNINVITE == 0x0075 &&
              (ushort)Op.CMSG_GROUP_UNINVITE_GUID == 0x0076 &&
              (ushort)Op.SMSG_GROUP_UNINVITE == 0x0077 &&
              (ushort)Op.CMSG_GROUP_SET_LEADER == 0x0078 &&
              (ushort)Op.SMSG_GROUP_SET_LEADER == 0x0079 &&
              (ushort)Op.CMSG_LOOT_METHOD == 0x007a &&
              (ushort)Op.CMSG_GROUP_DISBAND == 0x007b &&
              (ushort)Op.SMSG_GROUP_DESTROYED == 0x007c &&
              (ushort)Op.SMSG_GROUP_LIST == 0x007d &&
              (ushort)Op.SMSG_PARTY_MEMBER_STATS == 0x007e &&
              (ushort)Op.SMSG_PARTY_COMMAND_RESULT == 0x007f,
            "build-5875 base group opcode family drift");
        Check((ushort)Op.MSG_MINIMAP_PING == 0x01d5 &&
              (ushort)Op.CMSG_GROUP_CHANGE_SUB_GROUP == 0x027e &&
              (ushort)Op.CMSG_REQUEST_PARTY_MEMBER_STATS == 0x027f &&
              (ushort)Op.CMSG_GROUP_SWAP_SUB_GROUP == 0x0280 &&
              (ushort)Op.CMSG_GROUP_RAID_CONVERT == 0x028e &&
              (ushort)Op.CMSG_GROUP_ASSISTANT_LEADER == 0x028f &&
              (ushort)Op.SMSG_PARTY_MEMBER_STATS_FULL == 0x02f2 &&
              (ushort)Op.MSG_RAID_TARGET_UPDATE == 0x0321 &&
              (ushort)Op.MSG_RAID_READY_CHECK == 0x0322,
            "build-5875 extended group opcode family drift");

        const ulong guid = 0x1234_5678_9abc_def0;
        byte[] guidBody = Hx("f0debc9a78563412");
        Check(WorldSession.BuildGroupInviteBody("Bob").SequenceEqual(Hx("426f6200")) &&
              WorldSession.BuildGroupUninviteBody("Bob").SequenceEqual(Hx("426f6200")),
            "group invite/uninvite CString body drift");
        Check(WorldSession.BuildGroupAcceptBody().Length == 0 &&
              WorldSession.BuildGroupDeclineBody().Length == 0 &&
              WorldSession.BuildGroupDisbandBody().Length == 0 &&
              WorldSession.BuildGroupRaidConvertBody().Length == 0,
            "group null-client-packet body drift");
        Check(WorldSession.BuildGroupUninviteGuidBody(guid).SequenceEqual(guidBody) &&
              WorldSession.BuildGroupSetLeaderBody(guid).SequenceEqual(guidBody) &&
              WorldSession.BuildRequestPartyMemberStatsBody(guid).SequenceEqual(guidBody),
            "group full-guid body drift");
        Check(WorldSession.BuildGroupLootMethodBody(2, guid, 3).SequenceEqual(
                  Hx("02000000f0debc9a7856341203000000")),
            "CMSG_LOOT_METHOD body drift");
        Check(WorldSession.BuildGroupChangeSubGroupBody("Bar", 5).SequenceEqual(
                  Hx("4261720005")) &&
              WorldSession.BuildGroupSwapSubGroupBody("A", "B").SequenceEqual(
                  Hx("41004200")),
            "raid subgroup change/swap body drift");
        Check(WorldSession.BuildGroupAssistantLeaderBody(guid, true).SequenceEqual(
                  Hx("f0debc9a7856341201")) &&
              WorldSession.BuildGroupAssistantLeaderBody(guid, false).SequenceEqual(
                  Hx("f0debc9a7856341200")),
            "raid assistant body drift");
        Check(WorldSession.BuildGroupMinimapPingBody(1, 2).SequenceEqual(
                  Hx("0000803f00000040")),
            "outbound minimap ping body drift");
        Check(WorldSession.BuildRaidTargetSetBody(3, guid).SequenceEqual(
                  Hx("03f0debc9a78563412")) &&
              WorldSession.BuildRaidTargetSetBody(1, 0).SequenceEqual(
                  Hx("010000000000000000")) &&
              WorldSession.BuildRaidTargetRequestBody().SequenceEqual(Hx("ff")),
            "raid target set/clear/request body drift");
        Check(WorldSession.BuildReadyCheckStartBody().Length == 0 &&
              WorldSession.BuildReadyCheckAnswerBody(true).SequenceEqual(Hx("01")) &&
              WorldSession.BuildReadyCheckAnswerBody(false).SequenceEqual(Hx("00")),
            "ready-check start/answer body drift");
    }

    private static void CheckRosterWireAndStateLines()
    {
        byte[] partyBody = RosterFixture(0, 0,
            [new("Alice", 0x2222, 1, 0), new("Bob", 0x3333, 0x41, 0)],
            0x1111, 2, 0x3333, 3, 0);
        PartyRosterWire party = PartyFramePacketLaw.ParseRoster(partyBody);
        Check(party.GroupType == 0 && party.OwnFlags == 0 && party.Members.Length == 2 &&
              party.Members[0] == new PartyRosterWireMember("Alice", 0x2222, 1, 0) &&
              party.LeaderGuid == 0x1111 && party.LootMethod == 2 &&
              party.MasterLooterGuid == 0x3333 && party.LootThreshold == 3 &&
              party.DungeonDifficulty == 0,
            "party roster/master-loot body drift");
        RejectEveryTruncation(partyBody, b => _ = PartyFramePacketLaw.ParseRoster(b),
            "party roster");
        RejectTrailing(partyBody, b => _ = PartyFramePacketLaw.ParseRoster(b), "party roster");

        byte[] raidBody = RosterFixture(1, 0x82,
            [new("Assistant", 0x5555, 1, 0x82)], 0x4444, 3, 0, 2, 0);
        PartyRosterWire raid = PartyFramePacketLaw.ParseRoster(raidBody);
        Check(raid.GroupType == 1 && raid.OwnFlags == 0x82 &&
              raid.Members[0].MemberFlags == 0x82 && raid.LeaderGuid == 0x4444 &&
              raid.LootMethod == 3 && raid.LootThreshold == 2,
            "raid roster/assistant body drift");

        byte[] emptyBody = RosterFixture(0, 0, [], 0, 0, 0, 0, 0);
        PartyRosterWire empty = PartyFramePacketLaw.ParseRoster(emptyBody);
        Check(emptyBody.Length == 14 && empty.Members.Length == 0 && empty.LeaderGuid == 0 &&
              empty.LootMethod == 0 && empty.LootThreshold == 0 &&
              PartyFrameUiLaw.IsLeaveRoster(empty),
            "14-byte leave roster shape drift");

        // What vmangos actually sends when you stop being in a group: a fixed 24-byte all-zero
        // body from Group::RemoveMember (Group.cpp:506) and Group::Disband (:602). The 14-byte
        // shape above is the client-side minimum, never a packet observed on the wire. Rejecting
        // the real one dropped the only packet that clears the roster, so the party frame kept
        // showing a group after leaving while the chat line (SMSG_PARTY_COMMAND_RESULT) arrived.
        byte[] serverLeaveBody = new byte[24];
        PartyRosterWire serverLeave = PartyFramePacketLaw.ParseRoster(serverLeaveBody);
        Check(serverLeave.Members.Length == 0 && serverLeave.LeaderGuid == 0 &&
              serverLeave.GroupType == 0 && serverLeave.OwnFlags == 0 &&
              PartyFrameUiLaw.IsLeaveRoster(serverLeave),
            "24-byte vmangos leave roster must parse");

        // The tail is padding, not fields: only all-zero is a leave. Anything else is corruption
        // or a shape this parser does not know, and must still be refused.
        byte[] dirtyPadding = new byte[24];
        dirtyPadding[20] = 0xa5;
        ExpectReject(() => _ = PartyFramePacketLaw.ParseRoster(dirtyPadding),
            "leave roster with non-zero padding must be rejected");
        ExpectReject(() => _ = PartyFramePacketLaw.ParseRoster(new byte[15]),
            "leave roster of the wrong length must be rejected");
        ExpectReject(() => _ = PartyFramePacketLaw.ParseRoster(new byte[25]),
            "leave roster with an extra trailing byte must be rejected");

        PartyRosterWire first = new(0, 0,
            [new("Alice", 1, 1, 0), new("Bob", 2, 1, 0)], 99, 3, 0, 2);
        Check(GroupUiLaw.RosterLines(0, [], first).SequenceEqual(
                  ["Alice joins the party.", "Bob joins the party."]),
            "first roster must announce every existing other member");
        PartyRosterWire changed = new(0, 0,
            [new("Bob", 2, 1, 0), new("Carol", 3, 1, 0)], 99, 3, 0, 2);
        Check(GroupUiLaw.RosterLines(0, first.Members, changed).SequenceEqual(
                  ["Carol joins the party.", "Alice leaves the party."]),
            "roster diff additions-before-removals/order drift");
        Check(GroupUiLaw.RosterLines(0, changed.Members, empty).SequenceEqual(
                  ["Bob leaves the party.", "Carol leaves the party."]),
            "empty roster must emit one stale-member leave line");
        PartyRosterWire converted = changed with { GroupType = 1 };
        Check(GroupUiLaw.RosterLines(0, changed.Members, converted).SequenceEqual(
                  ["You have joined a raid group"]),
            "party-to-raid conversion line drift");
        PartyRosterWire raidAdd = converted with
        {
            Members = [.. converted.Members, new("Dave", 4, 1, 0)]
        };
        Check(GroupUiLaw.RosterLines(1, converted.Members, raidAdd).SequenceEqual(
                  ["Dave has joined the raid group"]),
            "raid roster wording drift");
        Check(GroupUiLaw.RosterLines(1, raidAdd.Members, empty).SequenceEqual(
                  ["Bob has left the raid group", "Carol has left the raid group",
                      "Dave has left the raid group"]),
            "raid leave-reset wording drift");
    }

    private static void CheckNotificationWireAndLines()
    {
        byte[] bob = Hx("426f6200");
        byte[] alice = Hx("416c69636500");
        Check(PartyFramePacketLaw.ParseInvite(bob) == "Bob" &&
              PartyFramePacketLaw.ParseDecline(alice) == "Alice" &&
              PartyFramePacketLaw.ParseLeaderChanged(bob) == "Bob",
            "group name-notification CString drift");
        RejectEveryTruncation(alice, b => _ = PartyFramePacketLaw.ParseDecline(b),
            "group decline");
        RejectTrailing(bob, b => _ = PartyFramePacketLaw.ParseLeaderChanged(b),
            "group leader notice");
        PartyFramePacketLaw.ParseEmptyNotice([], "SMSG_GROUP_UNINVITE");
        ExpectReject(() => PartyFramePacketLaw.ParseEmptyNotice([0], "SMSG_GROUP_DESTROYED"),
            "empty group notice accepted trailing byte");

        var resultBody = new PacketWriter();
        resultBody.WriteU32(GroupUiLaw.OperationInvite);
        resultBody.WriteCString("Bob");
        resultBody.WriteU32(GroupUiLaw.ResultAlreadyInGroup);
        PartyCommandResultWire result = PartyFramePacketLaw.ParseCommandResult(resultBody.ToArray());
        Check(result == new PartyCommandResultWire(0, "Bob", 4),
            "SMSG_PARTY_COMMAND_RESULT field order drift");
        RejectEveryTruncation(resultBody.ToArray(),
            b => _ = PartyFramePacketLaw.ParseCommandResult(b), "party command result");
        RejectTrailing(resultBody.ToArray(),
            b => _ = PartyFramePacketLaw.ParseCommandResult(b), "party command result");

        Check(GroupUiLaw.InvitedLine("Bob") == "Bob has invited you to join a group." &&
              GroupUiLaw.DeclinedLine("Bob") == "Bob declines your group invitation." &&
              GroupUiLaw.UninvitedLine == "You have been removed from the group." &&
              GroupUiLaw.DestroyedLines(true).SequenceEqual(
                  ["Your group has been disbanded."]) &&
              GroupUiLaw.DestroyedLines(false).Length == 0 &&
              GroupUiLaw.LeaderChangedLine("Me", "Me") ==
                  "You are now the group leader." &&
              GroupUiLaw.LeaderChangedLine("Bob", "Me") ==
                  "Bob is now the group leader.",
            "packet-specific group system line drift");
        Check(GroupUiLaw.CommandResultLines(new(0, "Bob", 0)).SequenceEqual(
                  ["You have invited Bob to join your group."]) &&
              GroupUiLaw.CommandResultLines(new(2, "", 0)).SequenceEqual(
                  ["You leave the group."]) &&
              GroupUiLaw.CommandResultLines(new(9, "", 0)).Length == 0,
            "successful party command result law drift");
        string[] errors =
        [
            "Cannot find 'Bob'.",
            "Bob is not in your party.",
            "Your party is full.",
            "Bob is already in a group.",
            "You aren't in a party.",
            "You are not the party leader.",
            "Target is not part of your alliance.",
            "Bob is ignoring you.",
        ];
        for (uint code = 1; code <= 8; code++)
            Check(GroupUiLaw.CommandResultLines(new(0, "Bob", code)).Single() ==
                  errors[code - 1], $"party command error {code} text drift");
        Check(GroupUiLaw.CommandResultLines(new(0, "Bob", 99)).Single() ==
              "Party command failed (99).", "unknown party command result drift");
    }

    private static void CheckMemberStatsWireAndMerge()
    {
        byte[] deltaBody = Hx("012aff000000016400960000c800fa003c00ef05");
        PartyMemberStatsWire delta = PartyFramePacketLaw.ParseMemberStats(deltaBody);
        Check(delta.Guid == 0x2a && delta.Snapshot.Status == 1 &&
              delta.Snapshot.Health == 100 && delta.Snapshot.MaxHealth == 150 &&
              delta.Snapshot.PowerType == 0 && delta.Snapshot.Power == 200 &&
              delta.Snapshot.MaxPower == 250 && delta.Snapshot.Level == 60 &&
              delta.Snapshot.Zone == 1519,
            "party stats delta status-through-zone drift");
        RejectEveryTruncation(deltaBody, b => _ = PartyFramePacketLaw.ParseMemberStats(b),
            "party member stats delta");
        RejectTrailing(deltaBody, b => _ = PartyFramePacketLaw.ParseMemberStats(b),
            "party member stats delta");

        byte[] fullBody = FullStatsFixture();
        PartyMemberStatsWire full = PartyFramePacketLaw.ParseMemberStats(fullBody);
        PartyMemberStatsSnapshot s = full.Snapshot;
        Check(full.Guid == 0x7f && s.PositionX == 1234 && s.PositionY == -5678 &&
              s.Auras!.SequenceEqual(new ushort[] { 133, 116 }) &&
              s.NegativeAuras!.SequenceEqual(new ushort[] { 8050 }) &&
              s.PetGuid == 0x1122_3344_5566_7788 && s.PetName == "Fido" &&
              s.PetModelId == 618 && s.PetHealth == 40 && s.PetMaxHealth == 50 &&
              s.PetPowerType == 0 && s.PetPower == 80 && s.PetMaxPower == 100 &&
              s.PetAuras!.SequenceEqual(new ushort[] { 1126 }) &&
              s.PetNegativeAuras!.SequenceEqual(new ushort[] { 770 }) &&
              s.Status is null && s.Level is null,
            "party stats full position/aura/pet block drift");
        RejectEveryTruncation(fullBody, b => _ = PartyFramePacketLaw.ParseMemberStats(b),
            "party member stats full");

        PartyMemberStatsWire offline = PartyFramePacketLaw.ParseMemberStats(
            Hx("015c0100000000"));
        Check(offline.Guid == 0x5c && offline.Snapshot.Status == 0 &&
              offline.Snapshot.Health is null,
            "offline full-stats miss shape drift");

        var prior = new PartyMemberStatsSnapshot(Status: 1, Health: 90, MaxHealth: 100,
            Level: 60, Zone: 1519, Auras: [133], PetName: "Fido", PetHealth: 40);
        var partial = new PartyMemberStatsSnapshot(Health: 25, Auras: []);
        PartyMemberStatsSnapshot merged = PartyFrameUiLaw.MergeStats(prior, partial, false);
        Check(merged.Health == 25 && merged.MaxHealth == 100 && merged.Zone == 1519 &&
              merged.Auras is { Length: 0 } && merged.PetName == "Fido" &&
              merged.PetHealth == 40,
            "party stats delta merge across complete snapshot drift");
        PartyMemberStatsSnapshot replaced = PartyFrameUiLaw.MergeStats(prior, partial, true);
        Check(replaced.Health == 25 && replaced.MaxHealth is null && replaced.Zone is null &&
              replaced.PetName is null,
            "party stats FULL must replace and clear omitted fields");
    }

    private static void CheckMinimapRaidTargetAndReadyWire()
    {
        var pingBody = new PacketWriter(16);
        pingBody.WriteU64(0x77);
        pingBody.WriteF32(3.5f);
        pingBody.WriteF32(-4.25f);
        PartyMinimapPingWire ping = PartyFramePacketLaw.ParseMinimapPing(pingBody.ToArray());
        Check(ping == new PartyMinimapPingWire(0x77, 3.5f, -4.25f),
            "inbound minimap ping full-GUID/f32 body drift");
        RejectEveryTruncation(pingBody.ToArray(), b => _ = PartyFramePacketLaw.ParseMinimapPing(b),
            "minimap ping");
        RejectTrailing(pingBody.ToArray(), b => _ = PartyFramePacketLaw.ParseMinimapPing(b),
            "minimap ping");

        var deltaBody = new PacketWriter(10);
        deltaBody.WriteU8(0);
        deltaBody.WriteU8(3);
        deltaBody.WriteU64(0x99);
        PartyRaidTargetUpdateWire delta =
            PartyFramePacketLaw.ParseRaidTargetUpdate(deltaBody.ToArray());
        Check(delta.IsDelta && delta.Icon == 3 && delta.Guid == 0x99 &&
              delta.Entries.Length == 0,
            "raid-target delta shape drift");
        RejectEveryTruncation(deltaBody.ToArray(),
            b => _ = PartyFramePacketLaw.ParseRaidTargetUpdate(b), "raid-target delta");
        RejectTrailing(deltaBody.ToArray(),
            b => _ = PartyFramePacketLaw.ParseRaidTargetUpdate(b), "raid-target delta");

        var listBody = new PacketWriter(19);
        listBody.WriteU8(1);
        listBody.WriteU8(0); listBody.WriteU64(0x11);
        listBody.WriteU8(7); listBody.WriteU64(0x22);
        PartyRaidTargetUpdateWire list =
            PartyFramePacketLaw.ParseRaidTargetUpdate(listBody.ToArray());
        Check(!list.IsDelta && list.Entries.SequenceEqual(
                  [new PartyRaidTargetEntry(0, 0x11), new PartyRaidTargetEntry(7, 0x22)]),
            "raid-target full-list shape drift");
        for (int length = 2; length < listBody.Length; length++)
        {
            if ((length - 1) % 9 == 0) continue;
            int n = length;
            ExpectReject(() => _ = PartyFramePacketLaw.ParseRaidTargetUpdate(
                    listBody.ToArray()[..n]),
                $"raid-target list accepted partial entry {n}/{listBody.Length}");
        }
        PartyRaidTargetUpdateWire empty = PartyFramePacketLaw.ParseRaidTargetUpdate([1]);
        Check(!empty.IsDelta && empty.Entries.Length == 0,
            "raid-target empty full-list shape drift");

        var board = new ulong[8];
        GroupUiLaw.ApplyRaidTarget(board, 7, 0x99);
        Check(GroupUiLaw.RaidTargetIndex(board, 0x99) == 8,
            "raid-target delta/reverse lookup drift");
        GroupUiLaw.ApplyRaidTarget(board, 7, 0);
        Check(GroupUiLaw.RaidTargetIndex(board, 0x99) == 0,
            "raid-target clear drift");
        board[3] = 0x44;
        GroupUiLaw.ApplyRaidTargetList(board,
            [new PartyRaidTargetEntry(0, 0x22), new PartyRaidTargetEntry(5, 0x33)]);
        Check(board[0] == 0x22 && board[5] == 0x33 && board[3] == 0,
            "raid-target list must reset absent icons");

        RaidMarkerUv star = RaidMarkerUiLaw.AtlasUv(1);
        RaidMarkerUv skull = RaidMarkerUiLaw.AtlasUv(8);
        Check(star == new RaidMarkerUv(new(0f, 0f), new(.25f, .25f)) &&
              skull == new RaidMarkerUv(new(.75f, .25f), new(1f, .5f)),
            "raid-target 4-column atlas cells drift");
        Check(RaidMarkerUiLaw.OverheadRect(new(100f, 200f), 40f) ==
                  new RaidMarkerRect(new(80f, 160f), new(120f, 200f)) &&
              RaidMarkerUiLaw.NameplateRect(50f, 20f, 40f, 1000f) ==
                  new RaidMarkerRect(new(30f, 20f), new(50f, 40f)),
            "raid-target overhead/nameplate seat geometry drift");
        float[] quad = WorldBillboardLaw.Vertices(new(0f, 0f, 0f), 1f,
            Vector3.UnitX, Vector3.UnitZ, star.Min, star.Max);
        Check(quad.SequenceEqual(new float[]
              {
                  -.5f, 0f, 1f, 0f, 0f,
                   .5f, 0f, 1f, .25f, 0f,
                  -.5f, 0f, 0f, 0f, .25f,
                  -.5f, 0f, 0f, 0f, .25f,
                   .5f, 0f, 1f, .25f, 0f,
                   .5f, 0f, 0f, .25f, .25f,
              }),
            "raid-target fixed one-world-unit bottom-seated quad LUT drift");

        PartyReadyCheckWire started = PartyFramePacketLaw.ParseReadyCheck([]);
        var answerBody = new PacketWriter(9);
        answerBody.WriteU64(0x88);
        answerBody.WriteU8(1);
        PartyReadyCheckWire answer = PartyFramePacketLaw.ParseReadyCheck(answerBody.ToArray());
        Check(started.Started && !answer.Started && answer.Guid == 0x88 && answer.Ready == 1,
            "ready-check started/answer server shapes drift");
        for (int length = 1; length < answerBody.Length; length++)
        {
            int n = length;
            ExpectReject(() => _ = PartyFramePacketLaw.ParseReadyCheck(answerBody.ToArray()[..n]),
                $"ready-check answer accepted truncation {n}/{answerBody.Length}");
        }
        RejectTrailing(answerBody.ToArray(), b => _ = PartyFramePacketLaw.ParseReadyCheck(b),
            "ready check answer");
    }

    private static void CheckRuntimeRoutes()
    {
        string root = FindRepoRoot();
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.Net.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.PartyFrames.cs"));
        string raidMarks = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.RaidMarks.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "SpellEffectMeshRenderer.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string confirms = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Confirms.cs"));
        // MSG_MINIMAP_PING and MSG_RAID_READY_CHECK are still dispatched from Net.cs, but their
        // handlers - and the PartyFramePacketLaw parses inside them - moved to
        // GameLoop.Confirms.cs. Assert both halves rather than expecting the parse in Net.cs:
        // the dispatch here, the parse there. Asserting only one half would let the other rot.
        foreach (string route in new[]
                 {
                     "ApplyPartyDecline(body);", "ApplyPartyUninvited(body);",
                     "ApplyPartyLeaderChanged(body);", "ApplyPartyDestroyed(body);",
                     "ApplyPartyCommandResult(body);", "ApplyPartyRaidTargetUpdate(body);",
                     "ApplyMinimapPing(body);", "ApplyReadyCheck(body);",
                 })
            Check(net.Contains(route, StringComparison.Ordinal), $"missing runtime route: {route}");
        foreach (string parse in new[]
                 {
                     "PartyFramePacketLaw.ParseMinimapPing(body)",
                     "PartyFramePacketLaw.ParseReadyCheck(body)",
                 })
            Check(confirms.Contains(parse, StringComparison.Ordinal),
                $"missing confirms parse: {parse}");
        Check(runtime.Contains("GroupUiLaw.RosterLines(_partyGroupType, previous, wire)",
                  StringComparison.Ordinal) &&
              runtime.Contains("_partyLootThreshold = wire.LootThreshold;",
                  StringComparison.Ordinal) &&
              runtime.Contains("GroupUiLaw.ApplyRaidTargetList(_partyRaidTargets, wire.Entries);",
                  StringComparison.Ordinal) &&
              // This was a bare ban on the word "partytest", which now fires on the comment
              // documenting the provenance flag - banning a word instead of asserting the
              // behaviour. The /partytest fixture legitimately lives in this file (it clears
              // the fixture GUIDs and builds the roster). What must actually hold is that a
              // real roster clears the sandbox first, so synthetic rows never outrank the wire.
              runtime.Contains("PartyFramePacketLaw.ParseRoster(body);\n" +
                  "        if (_partyTestSandbox) ClearPartyTestNames();\n" +
                  "        _partyTestSandbox = false;", StringComparison.Ordinal),
            "group state/composer integration or synthetic-preserve law drift");
        Check(raidMarks.Contains("IReadOnlyList<WorldBillboardDraw> RaidMarkerBillboards()",
                  StringComparison.Ordinal) &&
              raidMarks.Contains("new WorldBillboardDraw(anchor, RaidMarkerUiLaw.WorldSize",
                  StringComparison.Ordinal) &&
              !raidMarks.Contains("ImGui", StringComparison.Ordinal) &&
              !raidMarks.Contains("TryWorldToScreen", StringComparison.Ordinal) &&
              !raidMarks.Contains("ProjectedWorldPitch", StringComparison.Ordinal) &&
              !raidMarks.Contains("AddImage", StringComparison.Ordinal),
            "overhead raid marks regressed to projected UI instead of world billboards");
        Check(renderer.Contains("public unsafe void RenderWorldBillboards", StringComparison.Ordinal) &&
              renderer.Contains("_gl.Enable(EnableCap.DepthTest);", StringComparison.Ordinal) &&
              renderer.Contains("_gl.DepthMask(false);", StringComparison.Ordinal) &&
              renderer.Contains("WorldBillboardLaw.Vertices(bottom, draw.WorldSize",
                  StringComparison.Ordinal) &&
              program.Contains("RenderWorldBillboards(_window.Camera, RaidMarkerBillboards())",
                  StringComparison.Ordinal),
            "depth-tested fixed-world-size raid billboard pass is not wired");
    }

    private static byte[] RosterFixture(byte groupType, byte ownFlags,
        PartyRosterWireMember[] members, ulong leader, byte method, ulong master,
        byte threshold, byte difficulty)
    {
        var w = new PacketWriter();
        w.WriteU8(groupType);
        w.WriteU8(ownFlags);
        w.WriteU32((uint)members.Length);
        foreach (PartyRosterWireMember member in members)
        {
            w.WriteCString(member.Name);
            w.WriteU64(member.Guid);
            w.WriteU8(member.Status);
            w.WriteU8(member.MemberFlags);
        }
        w.WriteU64(leader);
        if (members.Length > 0)
        {
            w.WriteU8(method);
            w.WriteU64(master);
            w.WriteU8(threshold);
            w.WriteU8(difficulty);
        }
        return w.ToArray();
    }

    private static byte[] FullStatsFixture()
    {
        const uint mask = 0x001f_ff00;
        var w = new PacketWriter();
        w.WritePackedGuid(0x7f);
        w.WriteU32(mask);
        w.WriteU16(1234);
        w.WriteU16(unchecked((ushort)(short)-5678));
        w.WriteU32(0x21); w.WriteU16(133); w.WriteU16(116);
        w.WriteU16(0x04); w.WriteU16(8050);
        w.WriteU64(0x1122_3344_5566_7788);
        w.WriteCString("Fido");
        w.WriteU16(618); w.WriteU16(40); w.WriteU16(50);
        w.WriteU8(0); w.WriteU16(80); w.WriteU16(100);
        w.WriteU32(0x08); w.WriteU16(1126);
        w.WriteU16(0x02); w.WriteU16(770);
        return w.ToArray();
    }

    private static void RejectEveryTruncation(byte[] body, Action<byte[]> parse, string name)
    {
        for (int length = 0; length < body.Length; length++)
        {
            int n = length;
            ExpectReject(() => parse(body[..n]), $"{name} accepted truncation {n}/{body.Length}");
        }
    }

    private static void RejectTrailing(byte[] body, Action<byte[]> parse, string name)
    {
        byte[] trailing = [.. body, 0xa5];
        ExpectReject(() => parse(trailing), $"{name} accepted trailing byte");
    }

    private static void ExpectReject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (EndOfStreamException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException(message);
    }

    private static byte[] Hx(string hex) => Convert.FromHexString(hex);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MSUIClient.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MSUIClient repo root");
    }
}
