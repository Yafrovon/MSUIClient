using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class PartyFrameClinicalChecks
{
    internal static void CheckFrozenStaticPopupSources(string root)
    {
        const string partyPath = "crates/benilla-app/assets/ui/PartyFrame.xml";
        const string uiPanelsPath = "crates/benilla-app/assets/ui/UiPanels.xml";
        const string partySha256 =
            "c17d929e750812623649a2176813ef602b89622c404f66e64931097cfd324d15";
        const string uiPanelsSha256 =
            "649441e1335c0d23e0e15ad71ff2fb7543d11dbe92958443cdb90aad25974da1";
        string zipPath = Path.Combine(root, "parity", "snapshots", "current",
            "benilla.source.zip");
        // parity/snapshots is gitignored, so most checkouts simply do not have this reference
        // archive and the frozen-source comparison cannot run. Skip rather than fail - but SAY
        // SO. A silent skip is indistinguishable from a pass, which is precisely how this
        // harness went unnoticed while it was red.
        if (!File.Exists(zipPath))
        {
            Console.WriteLine("[skip] PartyFrame frozen Benilla source parity: no " +
                "parity/snapshots/current/benilla.source.zip in this checkout");
            return;
        }
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        (string PartyHash, string PartyText) = ReadFrozenEntry(archive, partyPath);
        (string UiPanelsHash, string UiPanelsText) = ReadFrozenEntry(archive, uiPanelsPath);
        Check(PartyHash == partySha256 && UiPanelsHash == uiPanelsSha256 &&
              PartyText.Contains("function BenillaPartyInviteDriver_OnEvent()",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_FindVisible(which)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_Visible(which)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_Hide(which)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_Resize(dialog, which)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains(
                  "function StaticPopup_Show(which, text_arg1, text_arg2, data)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_OnClick(dialog, index)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_OnShow()", StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_OnHide()", StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_OnUpdate(dialog, elapsed)",
                  StringComparison.Ordinal) &&
              UiPanelsText.Contains("function StaticPopup_EscapePressed()",
                  StringComparison.Ordinal),
            "party/shared StaticPopup frozen zip-entry hash/function fence drift");
    }

    private static (string Hash, string Text) ReadFrozenEntry(
        ZipArchive archive,
        string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ??
            throw new InvalidDataException($"missing frozen source entry {path}");
        using Stream stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        byte[] content = bytes.ToArray();
        return (Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            Encoding.UTF8.GetString(content));
    }

    public static void Run()
    {
        PartyTestSandboxLaw.FixtureMember[] sandbox = PartyTestSandboxLaw.Roster(100, 200);
        Check(sandbox.Length == 4 &&
              sandbox.Select(member => member.Name)
                  .SequenceEqual(["Alice", "Bob", "Carol", "Dave"]) &&
              sandbox.Select(member => member.Guid).SequenceEqual(
                  [0xF001UL, 0xF002UL, 0xF003UL, 0xF004UL]) &&
              sandbox[0].Status == PartyFrameUiLaw.Online &&
              sandbox[1].Status == (PartyFrameUiLaw.Online | PartyFrameUiLaw.Afk) &&
              sandbox[2].Status == (PartyFrameUiLaw.Online | PartyFrameUiLaw.Dead) &&
              sandbox[3].Status == 0 &&
              sandbox[0].Stats == new PartyMemberStatsSnapshot(Health: 820, MaxHealth: 1240,
                  PowerType: 0, Power: 300, MaxPower: 410, Level: 32,
                  PositionX: 130, PositionY: 200) &&
              sandbox[1].Stats.PositionX == 100 && sandbox[1].Stats.PositionY == 280 &&
              sandbox[2].Stats.PositionX == -200 && sandbox[2].Stats.PositionY == 200 &&
              sandbox[3].Stats.PositionX is null && sandbox[3].Stats.PositionY is null,
            "/partytest four-member mixed-state/stats/seat fixture drift");
        ulong[] sandboxMarks = new ulong[8];
        PartyTestSandboxLaw.ApplyRaidTarget(sandboxMarks, PartyTestSandboxLaw.AliceGuid, 8);
        PartyTestSandboxLaw.ApplyRaidTarget(sandboxMarks, PartyTestSandboxLaw.AliceGuid, 7);
        Check(sandboxMarks[6] == PartyTestSandboxLaw.AliceGuid && sandboxMarks[7] == 0 &&
              sandboxMarks.Count(guid => guid == PartyTestSandboxLaw.AliceGuid) == 1,
            "/partytest one-mark-per-unit move drift");
        PartyTestSandboxLaw.ApplyRaidTarget(sandboxMarks, PartyTestSandboxLaw.AliceGuid, 0);
        Check(sandboxMarks.All(guid => guid == 0),
            "/partytest raid-target clear drift");

        Check(PartyFrameUiLaw.MemberY(0) == 128f && PartyFrameUiLaw.MemberY(1) == 191f &&
              PartyFrameUiLaw.MemberY(3) == 317f && PartyFrameUiLaw.FrameWidth == 128f &&
              PartyFrameUiLaw.FrameHeight == 53f && PartyFrameUiLaw.MemberCount == 4,
            "party four-slot geometry/petless cascade drift");

        for (int count = 0; count <= 5; count++)
        {
            PartyRosterWire roster = PartyFramePacketLaw.ParseRoster(RosterFixture(count));
            Check(roster.Members.Length == count &&
                  PartyFrameUiLaw.CompactRosterIndices(roster.OwnFlags,
                      roster.Members.Select(x => x.MemberFlags).ToArray()).Length == Math.Min(4, count),
                $"party roster {count}-member parse/four-slot cap drift");
        }
        byte[] rosterOne = RosterFixture(1, ownFlags: 0x41);
        RejectEveryTruncation(rosterOne, PartyFramePacketLaw.ParseRoster, "roster");
        RejectTrailing(rosterOne, PartyFramePacketLaw.ParseRoster, "roster");
        Check(PartyFrameUiLaw.IsLeaveRoster(
                  PartyFramePacketLaw.ParseRoster(RosterFixture(0))) &&
              !PartyFrameUiLaw.IsLeaveRoster(
                  PartyFramePacketLaw.ParseRoster(RosterFixture(1))) &&
              PartyFrameUiLaw.IsLeaveRoster(new PartyRosterWire(1, 0x41,
                  [new PartyRosterWireMember("StillParsed", 0x1234, 1, 0x41)],
                  0, 2, 0x1234)),
            "party leader-zero GROUP_LIST leave edge drift");

        PartyRosterWire raid = PartyFramePacketLaw.ParseRoster(RosterFixture(6, 0x41,
            i => new byte[] { 0x41, 0x01, 0x41, 0x42, 0xc1, 0x02 }[i]));
        Check(PartyFrameUiLaw.CompactRosterIndices(raid.OwnFlags,
                  raid.Members.Select(x => x.MemberFlags).ToArray()).SequenceEqual([0, 2, 4]),
            "frozen wider 0x7f own-subgroup comparison drift");

        byte[] statsBody = StatsFixture(0x1234);
        PartyMemberStatsWire stats = PartyFramePacketLaw.ParseMemberStats(statsBody);
        Check(stats.Guid == 0x1234 && stats.Snapshot ==
              new PartyMemberStatsSnapshot(PartyFrameUiLaw.Online, 15, 100, 0, 70, 100, 60),
            "party member-stats body drift");
        RejectEveryTruncation(statsBody, PartyFramePacketLaw.ParseMemberStats, "stats");
        RejectTrailing(statsBody, PartyFramePacketLaw.ParseMemberStats, "stats");

        var inviteWriter = new PacketWriter();
        inviteWriter.WriteCString("Clinical Inviter");
        byte[] invite = inviteWriter.ToArray();
        Check(PartyFramePacketLaw.ParseInvite(invite) == "Clinical Inviter",
            "party invite CString drift");
        RejectEveryTruncation(invite, PartyFramePacketLaw.ParseInvite, "invite");
        RejectTrailing(invite, PartyFramePacketLaw.ParseInvite, "invite");

        var previous = new PartyMemberStatsSnapshot(1, 90, 100, 0, 40, 100, 60);
        var partial = new PartyMemberStatsSnapshot(Health: 25);
        Check(PartyFrameUiLaw.MergeStats(previous, partial, fullSnapshot: false) ==
                  new PartyMemberStatsSnapshot(1, 25, 100, 0, 40, 100, 60) &&
              PartyFrameUiLaw.MergeStats(previous, partial, fullSnapshot: true) == partial,
            "party delta merge/FULL omission-clear drift");
        Check(PartyFrameUiLaw.EffectiveStatus(0, PartyFrameUiLaw.Online) == 0 &&
              PartyFrameUiLaw.EffectiveStatus(PartyFrameUiLaw.Online, 0) == PartyFrameUiLaw.Online,
            "GROUP_LIST status authority drift");
        Check(PartyFrameUiLaw.MergedPvp(PartyFrameUiLaw.Pvp, 0) &&
              PartyFrameUiLaw.MergedPvp(0, PartyFrameUiLaw.UnitFlagPvp) &&
              !PartyFrameUiLaw.MergedPvp(0, 0) && !PartyFrameUiLaw.MergedPvp(0, null),
            "party roster/streamed PvP merge drift");
        Check(PartyFrameUiLaw.PvpFaction(2, 1) == "Horde" &&
              PartyFrameUiLaw.PvpFaction(null, 1) == "Alliance" &&
              PartyFrameUiLaw.PvpFaction(null, 8) == "Horde" &&
              PartyFrameUiLaw.PvpFaction(null, null) is null &&
              PartyFrameUiLaw.PvpFaction(0, 0) is null,
            "party FFA-priority/faction-resolution/no-default-Alliance law drift");

        float slot0 = PartyFrameUiLaw.AdvanceLowHealthTimer(0, true, true, true, .25f);
        float slot1 = PartyFrameUiLaw.AdvanceLowHealthTimer(0, true, true, true, .1f);
        Check(Near(slot0, .25f) && Near(slot1, .1f) &&
              PartyFrameUiLaw.AdvanceLowHealthTimer(slot0, true, false, true, .2f) == slot0 &&
              PartyFrameUiLaw.AdvanceLowHealthTimer(slot0, false, true, true, .2f) == slot0 &&
              PartyFrameUiLaw.AdvanceLowHealthTimer(slot0, true, true, false, .2f) == 0f &&
              PartyFrameUiLaw.AdvanceLowHealthTimer(.9f, true, true, true, .2f) < .101f &&
              Near(PartyFrameUiLaw.AdvanceLowHealthTimer(.1f, true, true, true, .6f), .7f) &&
              Near(PartyFrameUiLaw.LowHealthAlpha(0), 1) &&
              Near(PartyFrameUiLaw.LowHealthAlpha(.5f), 127f / 255f) &&
              PartyFrameUiLaw.LowHealthAlpha(slot0) < 1f,
            "party slot-local pause/advance/reset/modulo/alpha drift");

        Check(PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Left) ==
                  PartyPointerAction.Target &&
              PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Right) ==
                  PartyPointerAction.OpenPartyMenu &&
              PartyFrameUiLaw.ReleaseAction(1, -1, PartyPointerButton.Left) ==
                  PartyPointerAction.None &&
              PartyFrameUiLaw.ReleaseAction(1, 2, PartyPointerButton.Right) ==
                  PartyPointerAction.None &&
              PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Right) ==
                  PartyPointerAction.OpenPartyMenu,
            "party release-edge/fixed-slot ownership/rebind-current-occupant drift");
        Check(PartyFrameUiLaw.InviteButtonPushed(held: true, hovered: true) &&
              !PartyFrameUiLaw.InviteButtonPushed(held: true, hovered: false) &&
              !PartyFrameUiLaw.InviteButtonPushed(held: false, hovered: true) &&
              PartyFrameUiLaw.InviteButtonPushed(held: false, hovered: false,
                  pushedState: true),
            "party invite Button pressed/drag-off/explicit-pushed state drift");
        Check(PartyFrameUiLaw.PlayerLevelLine(0, null, null) == "Level 0 (Player)" &&
              PartyFrameUiLaw.PlayerLevelLine(0, "Orc", "Warrior") ==
                  "Level 0 Orc Warrior (Player)" &&
              PartyFrameUiLaw.PlayerLevelLine(60, "Orc", "Warrior", dead: true) ==
                  "Level 60 Corpse (Player)" &&
              PartyFrameUiLaw.TooltipNameColor == new Vector4(0, .6f, .1f, 1) &&
              PartyFrameUiLaw.Tooltip("Member", 60, "Orc", "Warrior", false, true, 0, 0) ==
                  new PartyTooltipView("Member", "Level 60 Orc Warrior (Player)", "PvP", 0, 0) &&
              PartyFrameUiLaw.Tooltip("Member", 60, "Orc", "Warrior", false, false, 0, 0).PvpLine
                  is null &&
              PartyFrameUiLaw.TooltipFadeAlpha(0) == 1 &&
              Near(PartyFrameUiLaw.TooltipFadeAlpha(.25), .5f) &&
              PartyFrameUiLaw.TooltipFadeAlpha(.5) == 0,
            "party SetUnit tooltip lines/color/status/fade drift");
        Check(PartyFrameUiLaw.TooltipHealth(false, 75, 100) ==
                  new PartyTooltipHealthState(false, 0, 0) &&
              PartyFrameUiLaw.TooltipHealth(true, 0, 0) ==
                  new PartyTooltipHealthState(true, 1, 0) &&
              PartyFrameUiLaw.TooltipHealth(true, 150, 100) ==
                  new PartyTooltipHealthState(true, 100, 100) &&
              PartyFrameUiLaw.MemberHealth(true, 75, 0) ==
                  new PartyTooltipHealthState(true, 0, 0) &&
              PartyFrameUiLaw.MemberHealth(false, 75, 100) ==
                  new PartyTooltipHealthState(true, 1, 1) &&
              !PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: true, fading: false) &&
              PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: true, fading: true) &&
              PartyFrameUiLaw.BeginTooltipSnapshot(1, 2, hasSnapshot: true, fading: false) &&
              PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: false, fading: false) &&
              !PartyFrameUiLaw.BeginTooltipSnapshot(1, -1, hasSnapshot: true, fading: true),
            "party fixed-slot SetUnit snapshot/live-bar/member-health law drift");
        Check(PartyFrameUiLaw.TooltipOwner(0) == new GameTooltipOwnerKey("party-member", 1) &&
              PartyFrameUiLaw.TooltipOwner(3) == new GameTooltipOwnerKey("party-member", 4) &&
              PartyFrameUiLaw.TooltipUnitToken(0) == "party1" &&
              PartyFrameUiLaw.TooltipUnitToken(3) == "party4",
            "party tooltip fixed frame owner/live partyN token identity drift");
        ExpectReject(() => PartyFrameUiLaw.TooltipOwner(-1),
            "party tooltip accepted owner slot below PartyMemberFrame1");
        ExpectReject(() => PartyFrameUiLaw.TooltipUnitToken(PartyFrameUiLaw.MemberCount),
            "party tooltip accepted live token beyond PartyMemberFrame4");

        var sharedView = new PartyTooltipView("Snapshot Member",
            "Level 60 Orc Warrior (Player)", "PvP", 150, 100);
        GameTooltipContent sharedContent = PartyFrameUiLaw.SharedTooltipContent(1, sharedView);
        Check(sharedContent.Anchor == GameTooltipAnchorKind.DefaultBottomRight &&
              sharedContent.Lines.SequenceEqual(
              [
                  new GameTooltipLine("Snapshot Member", GameTooltipTextTone.UnitReaction),
                  new GameTooltipLine("Level 60 Orc Warrior (Player)",
                      GameTooltipTextTone.White),
                  new GameTooltipLine("PvP", GameTooltipTextTone.White),
              ]) &&
              sharedContent.LiveUnitToken == "party2" &&
              sharedContent.Health == new GameTooltipHealthState(true, 100, 100) &&
              sharedContent.UnitReaction == PartyFrameUiLaw.TooltipUnitReaction,
            "party tooltip shared rows/anchor/reaction/initial health projection drift");
        GameTooltipContent absentContent = PartyFrameUiLaw.SharedTooltipContent(3,
            sharedView with { PvpLine = null }, tokenExists: false);
        Check(absentContent.Lines.Length == 2 && absentContent.LiveUnitToken == "party4" &&
              absentContent.Health == GameTooltipHealthState.Hidden,
            "party tooltip absent fixed token did not retain rows while hiding health");

        Check(GameTooltipUiLaw.TryLiveUnitHealth(sharedContent.LiveUnitToken,
                  PartyFrameUiLaw.TooltipHealthPush(1, tokenExists: true, 15, 60),
                  out GameTooltipHealthState reboundHealth) &&
              reboundHealth == new GameTooltipHealthState(true, 60, 15) &&
              sharedContent.Lines[0].Text == "Snapshot Member",
            "party same-slot occupant rebind rebuilt rows or missed live health");
        Check(!GameTooltipUiLaw.TryLiveUnitHealth(sharedContent.LiveUnitToken,
                  PartyFrameUiLaw.TooltipHealthPush(2, tokenExists: true, 50, 100), out _) &&
              GameTooltipUiLaw.TryLiveUnitHealth(sharedContent.LiveUnitToken,
                  PartyFrameUiLaw.TooltipHealthPush(1, tokenExists: false, 0, 0),
                  out GameTooltipHealthState absentHealth) &&
              absentHealth == GameTooltipHealthState.Hidden,
            "party tooltip mismatched slot push or disconnect health-hide token law drift");
        PartyTooltipLayout tooltipLayout = PartyFrameUiLaw.TooltipLayout(
            [50f, 80f, 30f], [14f, 12f, 12f]);
        PartyTooltipLayout narrowTooltipLayout = PartyFrameUiLaw.TooltipLayout([10f], [14f]);
        Check(tooltipLayout.Width == 100f && tooltipLayout.Height == 62f &&
              tooltipLayout.RowTops.SequenceEqual([10f, 26f, 40f]) &&
              narrowTooltipLayout.Width == 30f && narrowTooltipLayout.Height == 34f &&
              narrowTooltipLayout.RowTops.SequenceEqual([10f]),
            "party SetUnit header/body font-row/gap/no-minimum-width layout drift");
        Check(PartyFrameUiLaw.TooltipRightOffset(false, false) == -13 &&
              PartyFrameUiLaw.TooltipRightOffset(false, true) == -58 &&
              PartyFrameUiLaw.TooltipRightOffset(true, false) == -103 &&
              PartyFrameUiLaw.TooltipRightOffset(true, true) == -103 &&
              PartyFrameUiLaw.TooltipBottomOffset(false, false, false, false) == 70 &&
              PartyFrameUiLaw.TooltipBottomOffset(true, true, false, false) == 97 &&
              PartyFrameUiLaw.TooltipBottomOffset(true, true, true, false) == 120 &&
              PartyFrameUiLaw.TooltipBottomOffset(true, false, false, true) == 106,
            "party UIParent-managed default tooltip anchor drift");

        Check(PartyFrameUiLaw.PopupWidth == 320 && PartyFrameUiLaw.PopupTextWidth == 290 &&
              PartyFrameUiLaw.PopupHeight(12) == 72 && PartyFrameUiLaw.PopupButtonTop(12) == 36,
            "party ordinary StaticPopup geometry drift");
        StaticPopupCoordinatorLaw.Definition inviteDefinition =
            PartyFrameUiLaw.PartyInvitePopupDefinition;
        Check(inviteDefinition.Type == "PARTY_INVITE" && inviteDefinition.WhileDead &&
              inviteDefinition.HideOnEscape && inviteDefinition.Cancels is null &&
              inviteDefinition.HasAccept && inviteDefinition.HasCancel &&
              inviteDefinition.HasOnShow && inviteDefinition.HasOnHide &&
              !inviteDefinition.HasOnUpdate && !inviteDefinition.HasEditBox &&
              !inviteDefinition.UsesTimeoutText && !inviteDefinition.UsesDelayText &&
              inviteDefinition.TimeoutSeconds == 60 &&
              inviteDefinition.StartDelaySeconds is null &&
              inviteDefinition.EntrySound == "igPlayerInvite",
            "PARTY_INVITE shared StaticPopup definition drift");

        StaticPopupCoordinatorLaw.Plan inviteShow = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, inviteDefinition,
            playerDeadOrGhost: true, dataToken: "Clinical Inviter");
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? visibleInvite =
            PartyFrameUiLaw.PartyInvitePopup(inviteShow.Slots);
        Check(inviteShow.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              visibleInvite is { Slot: 1 } &&
              visibleInvite.Value.Instance.DataToken == "Clinical Inviter" &&
              visibleInvite.Value.Instance.TimeLeft == 60 &&
              PartyFrameUiLaw.PartyInvitePopup(new(
                  null, visibleInvite.Value.Instance))?.Slot == 2 &&
              PartyFrameUiLaw.IsPartyInviteVisible(inviteShow.Slots) &&
              !PartyFrameUiLaw.IsPartyInviteVisible(StaticPopupCoordinatorLaw.Slots.Empty) &&
              PopupKinds(inviteShow).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.PrepareContent,
                  StaticPopupCoordinatorLaw.EffectKind.HideEditBox,
                  StaticPopupCoordinatorLaw.EffectKind.EnableAccept,
                  StaticPopupCoordinatorLaw.EffectKind.Show,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnShow,
                  StaticPopupCoordinatorLaw.EffectKind.Resize,
                  StaticPopupCoordinatorLaw.EffectKind.EntrySound,
              ]),
            "PARTY_INVITE slot-one/data/show/open/OnShow/entry-sound plan drift");

        StaticPopupCoordinatorLaw.Plan inviteAccept = StaticPopupCoordinatorLaw.Click(
            inviteShow.Slots, 1, buttonIndex: 1);
        StaticPopupCoordinatorLaw.Plan inviteDecline = StaticPopupCoordinatorLaw.Click(
            inviteShow.Slots, 1, buttonIndex: 2);
        StaticPopupCoordinatorLaw.Plan inviteEscape =
            StaticPopupCoordinatorLaw.Escape(inviteShow.Slots);
        StaticPopupCoordinatorLaw.Plan inviteTimeout = StaticPopupCoordinatorLaw.Advance(
            inviteShow.Slots, 1, elapsedSeconds: 60);
        StaticPopupCoordinatorLaw.Plan inviteDirectHide = StaticPopupCoordinatorLaw.HideByType(
            inviteShow.Slots, PartyFrameUiLaw.PartyInvitePopupType);
        Check(PopupKinds(inviteAccept).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.Accept,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
              ]) &&
              PopupKinds(inviteDecline).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.CancelClicked,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
              ]) &&
              PopupKinds(inviteEscape).SequenceEqual(PopupKinds(inviteDecline)) &&
              PopupKinds(inviteTimeout).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.CancelTimeout,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
              ]) &&
              PopupKinds(inviteDirectHide).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
              ]),
            "PARTY_INVITE accept/clicked/Escape/timeout/direct-hide plan ordering drift");

        StaticPopupCoordinatorLaw.Plan inviteOverride = StaticPopupCoordinatorLaw.Show(
            inviteShow.Slots, inviteDefinition, playerDeadOrGhost: false,
            dataToken: "Replacement Inviter");
        Check(PopupKinds(inviteOverride).SequenceEqual(
              [
                  StaticPopupCoordinatorLaw.EffectKind.CancelOverride,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
                  StaticPopupCoordinatorLaw.EffectKind.PrepareContent,
                  StaticPopupCoordinatorLaw.EffectKind.HideEditBox,
                  StaticPopupCoordinatorLaw.EffectKind.EnableAccept,
                  StaticPopupCoordinatorLaw.EffectKind.Show,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnShow,
                  StaticPopupCoordinatorLaw.EffectKind.Resize,
                  StaticPopupCoordinatorLaw.EffectKind.EntrySound,
              ]) &&
              PartyFrameUiLaw.PartyInvitePopup(inviteOverride.Slots)?.Instance.DataToken ==
                  "Replacement Inviter",
            "PARTY_INVITE same-type override/reuse plan ordering drift");

        Check(PartyFrameUiLaw.PreservePartyAcrossWorldEnter(socketSessionAlive: true) &&
              !PartyFrameUiLaw.PreservePartyAcrossWorldEnter(socketSessionAlive: false),
            "party same-session zoning preservation law drift");
        Check((ushort)Op.SMSG_GROUP_INVITE == 0x006f &&
              (ushort)Op.CMSG_GROUP_ACCEPT == 0x0072 &&
              (ushort)Op.CMSG_GROUP_DECLINE == 0x0073 &&
              (ushort)Op.SMSG_GROUP_LIST == 0x007d &&
              (ushort)Op.SMSG_PARTY_MEMBER_STATS == 0x007e &&
              (ushort)Op.CMSG_REQUEST_PARTY_MEMBER_STATS == 0x027f &&
              (ushort)Op.SMSG_PARTY_MEMBER_STATS_FULL == 0x02f2,
            "build-5875 party opcode drift");

        string root = ClientConfig.FindRepoRoot();
        string partyLaw = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "PartyFrameUiLaw.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.PartyFrames.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.Net.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.Settings.cs"));
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldSession.cs"));
        string live = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.LiveRun.cs"));
        string capture = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.DevTools.UiParity.cs"));
        string sandboxLaw = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "PartyTestSandboxLaw.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string popup = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitPopup.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));

        Check(sandboxLaw.Contains("public static FixtureMember[] Roster", StringComparison.Ordinal) &&
              runtime.Contains("PartyTestSandboxLaw.Roster(playerX, playerY)",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (_partyTestSandbox) ClearPartyTestNames();",
                  StringComparison.Ordinal) &&
              chat.Contains("if (command == \"/partytest\")", StringComparison.Ordinal) &&
              chat.Contains("ShowPartyTestInvite();", StringComparison.Ordinal) &&
              chat.Contains("ApplyPartyTestRoster(lead: mode == \"lead\")",
                  StringComparison.Ordinal) &&
              popup.Contains("TryPartyTestUninvite(guid)", StringComparison.Ordinal) &&
              popup.Contains("TryPartyTestLoot", StringComparison.Ordinal) &&
              bindings.Contains("TryPartyTestRaidTarget(_selectionGuid, requested)",
                  StringComparison.Ordinal),
            "/partytest fixture/provenance/local-intent production wiring drift");

        CheckFrozenStaticPopupSources(root);

        int worldStart = net.IndexOf("if (_queuedWorldEntry is { } enter", StringComparison.Ordinal);
        int worldEnd = net.IndexOf("// Drain + dispatch the inbound packet stream", worldStart,
            StringComparison.Ordinal);
        int inviteLifecycleCall = net.IndexOf("UpdatePartyInviteLifecycle();",
            StringComparison.Ordinal);
        int disconnectedReset = net.IndexOf("ResetParty();", StringComparison.Ordinal);
        Check(worldStart >= 0 && worldEnd > worldStart &&
              !net[worldStart..worldEnd].Contains("ResetParty();", StringComparison.Ordinal) &&
              disconnectedReset >= 0 && inviteLifecycleCall > disconnectedReset &&
              inviteLifecycleCall < worldStart,
            "party disconnect reset/worldport preservation/always-pumped invite lifecycle drift");
        int resetStart = runtime.IndexOf("private void ResetParty()", StringComparison.Ordinal);
        int resetEnd = runtime.IndexOf("private void ApplyPartyRoster", resetStart,
            StringComparison.Ordinal);
        string reset = resetStart >= 0 && resetEnd > resetStart
            ? runtime[resetStart..resetEnd]
            : "";
        int resetInviteHide = reset.IndexOf("HidePartyInvite();", StringComparison.Ordinal);
        int resetRosterClear = reset.IndexOf("_partyMembers.Clear();", StringComparison.Ordinal);
        Check(reset.Length > 0 &&
              resetInviteHide >= 0 && resetRosterClear > resetInviteHide &&
              reset.Contains("BeginPartyTooltipDeparture(NowSeconds(), tokenExists: false);",
                  StringComparison.Ordinal) &&
              !reset.Contains("Array.Clear(_partyLowHealthTimers)",
                  StringComparison.Ordinal) &&
              !reset.Contains("_partyTooltip = null", StringComparison.Ordinal) &&
              !reset.Contains("_partyTooltipSlot = -1", StringComparison.Ordinal) &&
              !reset.Contains("_partyTooltipOwnerToken = default", StringComparison.Ordinal),
            "party reset must retain flash/rows/fixed-slot lease while arming absent departure");
        Check(runtime.Contains("Vector4 portraitColor = new(portraitRgb, portraitAlpha)",
                  StringComparison.Ordinal) &&
              runtime.Contains("InviteButtonPushed(held, hovered)", StringComparison.Ordinal) &&
              runtime.Contains("bool pvp = hovered.Pvp;", StringComparison.Ordinal) &&
              !runtime.Contains("bool pvp = hovered.Pvp ||", StringComparison.Ordinal) &&
              runtime.Contains("? \"GameTooltipHeaderText\" : \"GameTooltipText\"",
                  StringComparison.Ordinal) &&
              runtime.Contains("PartyFrameUiLaw.TooltipLayout(rowWidths, rowHeights, s)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("MathF.Max(120 * s", StringComparison.Ordinal) &&
              runtime.Contains("UpdateAndQueuePartyTooltip(-1, null, NowSeconds(), capture: false)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ImGuiWindowFlags.Tooltip", StringComparison.Ordinal) &&
              runtime.Contains("TooltipRightOffset(\n            multiBarLeftVisible",
                  StringComparison.Ordinal) &&
              runtime.Contains("petOrStanceVisible: PetOrStanceActionBarVisible",
                  StringComparison.Ordinal) &&
              runtime.Contains("min=0;max={memberHealth.Maximum};value={memberHealth.Value}",
                  StringComparison.Ordinal) &&
              runtime.Contains("min=0;max={view.MaxPower};value={Math.Min(view.Power, view.MaxPower)}",
                  StringComparison.Ordinal) &&
              !runtime.Contains("zero-health-fraction", StringComparison.Ordinal) &&
              !runtime.Contains("zero-power-fraction", StringComparison.Ordinal),
            "party independent alpha/stale-tooltip/top-strata/managed-anchor/status-host seam drift");
        Check(runtime.Contains("PartyFrameUiLaw.IsLeaveRoster(wire)", StringComparison.Ordinal) &&
              runtime.Contains("if (leaving) HidePartyInvite();",
                  StringComparison.Ordinal) &&
              runtime.Contains("PartyFrameUiLaw.BeginTooltipSnapshot(_partyTooltipSlot",
                  StringComparison.Ordinal) &&
              runtime.Contains("PartyMember[] currentSlots = PartyFrameMembers();",
                  StringComparison.Ordinal) &&
              runtime.Contains("BuildPartyMemberView(currentSlots[_partyTooltipSlot])",
                  StringComparison.Ordinal) &&
              runtime.Contains("party-tooltip-slot-token-is-absent-during-fade",
                  StringComparison.Ordinal) &&
              // The popup button font gained a disabled branch; the GameFont* family
              // is still what matters, which the DialogButton* bans below enforce.
              runtime.Contains("string fontObject = !enabled ? \"GameFontDisable\"\n" +
                  "            : hovered ? \"GameFontHighlight\" : \"GameFontNormal\";",
                  StringComparison.Ordinal) &&
              !runtime.Contains("DialogButtonHighlightText", StringComparison.Ordinal) &&
              !runtime.Contains("DialogButtonNormalText", StringComparison.Ordinal) &&
              runtime.Contains("party-pvp-faction-is-unresolved", StringComparison.Ordinal) &&
              runtime.Contains("MathF.Max(0f, (float)(now - _partyLowHealthLastAt))",
                  StringComparison.Ordinal),
            "party leave/slot-tooltip/popup-font/PvP-resolution/full-elapsed seam drift");

        int tooltipUpdateStart = runtime.IndexOf(
            "private bool UpdateAndQueuePartyTooltip(", StringComparison.Ordinal);
        int tooltipDepartureStart = runtime.IndexOf(
            "private void BeginPartyTooltipDeparture(", tooltipUpdateStart,
            StringComparison.Ordinal);
        int tooltipClearStart = runtime.IndexOf("private void ClearPartyTooltipRuntime()",
            tooltipDepartureStart, StringComparison.Ordinal);
        int tooltipCompletionStart = runtime.IndexOf(
            "private void CompleteDeferredPartyTooltipParityCapture()", tooltipClearStart,
            StringComparison.Ordinal);
        int tooltipRendererStart = runtime.IndexOf("private void DrawPartyUnitTooltip(",
            tooltipCompletionStart, StringComparison.Ordinal);
        int tooltipRendererEnd = runtime.IndexOf("private void DrawPartyInvite()",
            tooltipRendererStart, StringComparison.Ordinal);
        Check(tooltipUpdateStart >= 0 && tooltipDepartureStart > tooltipUpdateStart &&
              tooltipClearStart > tooltipDepartureStart &&
              tooltipCompletionStart > tooltipClearStart &&
              tooltipRendererStart > tooltipCompletionStart &&
              tooltipRendererEnd > tooltipRendererStart,
            "party shared GameTooltip adapter/source hunk map drift");
        string tooltipUpdate = runtime[tooltipUpdateStart..tooltipDepartureStart];
        string tooltipDeparture = runtime[tooltipDepartureStart..tooltipClearStart];
        string tooltipCompletion = runtime[tooltipCompletionStart..tooltipRendererStart];
        string tooltipRenderer = runtime[tooltipRendererStart..tooltipRendererEnd];

        Check(tooltipUpdate.Contains("PartyFrameUiLaw.TooltipOwner(hoveredSlot)",
                  StringComparison.Ordinal) &&
              tooltipUpdate.Contains("PartyFrameUiLaw.SharedTooltipContent(hoveredSlot",
                  StringComparison.Ordinal) &&
              Count(tooltipUpdate, "ClaimSharedGameTooltip(") == 1 &&
              Count(tooltipUpdate, "PublishSharedGameTooltip(") == 1 &&
              !tooltipUpdate.Contains(".Guid", StringComparison.Ordinal),
            "party tooltip owner must be fixed PartyMemberFrame1..4, never occupant GUID");
        int livePush = tooltipUpdate.IndexOf("TryRefreshSharedGameTooltipUnit(",
            StringComparison.Ordinal);
        int refreshedSnapshot = tooltipUpdate.IndexOf("shared = SharedGameTooltipSnapshot();",
            livePush, StringComparison.Ordinal);
        int immutableRuntime = tooltipUpdate.IndexOf(
            "PartyTooltipRuntime rendererRuntime = _partyTooltip;", refreshedSnapshot,
            StringComparison.Ordinal);
        int queueRenderer = tooltipUpdate.IndexOf("QueueSharedGameTooltipRenderer(",
            immutableRuntime, StringComparison.Ordinal);
        int drawRenderer = tooltipUpdate.IndexOf("DrawPartyUnitTooltip(rendererRuntime",
            queueRenderer, StringComparison.Ordinal);
        Check(tooltipUpdate.Contains("PartyMember[] currentSlots = PartyFrameMembers();",
                  StringComparison.Ordinal) &&
              tooltipUpdate.Contains("BuildPartyMemberView(currentSlots[_partyTooltipSlot])",
                  StringComparison.Ordinal) &&
              tooltipUpdate.Contains("PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot",
                  StringComparison.Ordinal) &&
              livePush >= 0 && refreshedSnapshot > livePush &&
              immutableRuntime > refreshedSnapshot && queueRenderer > immutableRuntime &&
              drawRenderer > queueRenderer &&
              tooltipUpdate.Contains(
                  "SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds)",
                  StringComparison.Ordinal) &&
              Count(tooltipUpdate, "DrawPartyUnitTooltip(") == 1 &&
              Count(runtime, "DrawPartyUnitTooltip(") == 2 &&
              !runtime.Contains("_partyTooltipFadeStartedAt", StringComparison.Ordinal),
            "party live-health/shared-alpha/immutable deferred renderer seam drift");
        Check(tooltipDeparture.Contains(
                  "PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot, false, 0, 0)",
                  StringComparison.Ordinal) &&
              tooltipDeparture.Contains("BeginSharedGameTooltipFade(_partyTooltipOwnerToken, now",
                  StringComparison.Ordinal) &&
              tooltipDeparture.Contains("GameTooltipUiLaw.WorldFadeSeconds",
                  StringComparison.Ordinal) &&
              !tooltipDeparture.Contains("ClearPartyTooltipRuntime", StringComparison.Ordinal) &&
              !tooltipDeparture.Contains("_partyTooltip = null", StringComparison.Ordinal),
            "party disconnect/reset departure must hide health and fade retained rows");
        Check(tooltipRenderer.Contains("PartyTooltipHealthState tooltipHealth, float alpha, " +
                  "bool fading, bool capture", StringComparison.Ordinal) &&
              tooltipRenderer.Contains("_skin.DrawBackdrop(dl, pos, pos + size, " +
                  "WowSkin.Tooltip, fadeTint, fadeTint);", StringComparison.Ordinal) &&
              tooltipRenderer.Contains(
                  @"Interface\TargetingFrame\UI-TargetingFrame-BarFill",
                  StringComparison.Ordinal) &&
              tooltipRenderer.Contains("GameTooltipStatusBar", StringComparison.Ordinal) &&
              tooltipRenderer.Contains("tooltipHealth.Visible", StringComparison.Ordinal) &&
              tooltipRenderer.Contains("InteractionState: fading ?", StringComparison.Ordinal) &&
              !tooltipRenderer.Contains("SharedGameTooltip", StringComparison.Ordinal),
            "party preserved tooltip rows/backdrop/status-bar renderer drift");
        Check(tooltipCompletion.Contains("_partyTooltipParityCompletionPending",
                  StringComparison.Ordinal) &&
              tooltipCompletion.Contains("_partyTooltipParityRendererCollected",
                  StringComparison.Ordinal) &&
              tooltipCompletion.Contains(
                  "shared-tooltip-owner-replaced-before-tooltip-stratum",
                  StringComparison.Ordinal) &&
              tooltipCompletion.Contains("MarkUiParityFrameComplete();",
                  StringComparison.Ordinal),
            "party deferred parity completion/replacement fallback drift");
        int applyInviteStart = runtime.IndexOf("private void ApplyPartyInvite(byte[] body)",
            StringComparison.Ordinal);
        int applyInviteEnd = runtime.IndexOf("private void ApplyPartyDecline", applyInviteStart,
            StringComparison.Ordinal);
        string applyInvite = applyInviteStart >= 0 && applyInviteEnd > applyInviteStart
            ? runtime[applyInviteStart..applyInviteEnd]
            : "";
        int inviteParse = applyInvite.IndexOf("PartyFramePacketLaw.ParseInvite(body)",
            StringComparison.Ordinal);
        int inviteShowCall = applyInvite.IndexOf("StaticPopupCoordinatorLaw.Show(",
            StringComparison.Ordinal);
        Check(inviteParse >= 0 && inviteShowCall > inviteParse &&
              applyInvite.Contains("PartyFrameUiLaw.PartyInvitePopupDefinition",
                  StringComparison.Ordinal) &&
              applyInvite.Contains("dataToken: inviter", StringComparison.Ordinal),
            "PARTY_INVITE must parse atomically before the shared slot Show plan");

        int executorStart = runtime.IndexOf("private void ExecuteStaticPopupPlan(",
            StringComparison.Ordinal);
        int directHideStart = runtime.IndexOf("private void HidePartyInvite()", executorStart,
            StringComparison.Ordinal);
        int escapeStart = runtime.IndexOf("private bool TryDismissStaticPopupOnEscape()",
            directHideStart, StringComparison.Ordinal);
        int lifecycleStart = runtime.IndexOf("private void UpdatePartyInviteLifecycle()",
            escapeStart, StringComparison.Ordinal);
        int lifecycleEnd = runtime.IndexOf("private PartyMemberView BuildPartyMemberView",
            lifecycleStart, StringComparison.Ordinal);
        Check(executorStart >= 0 && directHideStart > executorStart && escapeStart > directHideStart &&
              lifecycleStart > escapeStart && lifecycleEnd > lifecycleStart,
            "PARTY_INVITE coordinator runtime hunk map drift");
        string executor = runtime[executorStart..directHideStart];
        string directHide = runtime[directHideStart..escapeStart];
        string escapeDriver = runtime[escapeStart..lifecycleStart];
        string lifecycle = runtime[lifecycleStart..lifecycleEnd];
        int slotCommit = executor.IndexOf("_staticPopupSlots = plan.Slots;",
            StringComparison.Ordinal);
        int effectLoop = executor.IndexOf(
            "foreach (StaticPopupCoordinatorLaw.Effect effect in plan.Effects)",
            StringComparison.Ordinal);
        int acceptWire = executor.IndexOf("_net?.GroupAccept();", StringComparison.Ordinal);
        int acceptGuard = executor.IndexOf("_partyInviteAccepted = true;", acceptWire,
            StringComparison.Ordinal);
        Check(slotCommit >= 0 && effectLoop > slotCommit && acceptWire > effectLoop &&
              acceptGuard > acceptWire && Count(executor, "_net?.GroupDecline();") == 2 &&
              executor.Contains("if (!_partyInviteAccepted) _net?.GroupDecline();",
                  StringComparison.Ordinal) &&
              Count(executor, "_partyInviteAccepted = false;") == 2 &&
              executor.Contains("PlayUiSound(effect.Value)", StringComparison.Ordinal) &&
              executor.Contains("effect.Type, PartyInvitePopupType", StringComparison.Ordinal),
            "PARTY_INVITE slot-commit/callback/accepted-guard/sound executor drift");
        Check(directHide.Contains("StaticPopupCoordinatorLaw.HideByType(",
                  StringComparison.Ordinal) &&
              escapeDriver.Contains("StaticPopupCoordinatorLaw.Escape(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              escapeDriver.Contains("ExecuteStaticPopupPlan(plan);", StringComparison.Ordinal) &&
              // The Escape precedence no longer singles out PARTY_INVITE: it asks the
              // coordinator whether ANY slot is visible, which is what the layer needs as
              // more popup types are integrated.
              settings.Contains("StaticPopupCoordinatorLaw.AnyVisible(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              settings.Contains("TryDismissStaticPopupOnEscape()", StringComparison.Ordinal),
            "PARTY_INVITE direct-hide/shared-Escape/settings precedence seam drift");
        int popupEscapeLayer = settings.IndexOf("case GameMenuEscapeLayer.Popup:",
            StringComparison.Ordinal);
        int logoutEscape = settings.IndexOf("TryCancelLogoutOnEscape()", popupEscapeLayer,
            StringComparison.Ordinal);
        int sharedPopupEscape = settings.IndexOf("TryDismissStaticPopupOnEscape()", logoutEscape,
            StringComparison.Ordinal);
        int mailEscape = settings.IndexOf("TryDismissMailConfirmationOnEscape()",
            sharedPopupEscape, StringComparison.Ordinal);
        int enchantEscape = settings.IndexOf("TryDismissEnchantConfirmationOnEscape()",
            mailEscape, StringComparison.Ordinal);
        int skillEscape = settings.IndexOf("TryDismissSkillUnlearnConfirmationOnEscape()",
            enchantEscape, StringComparison.Ordinal);
        Check(popupEscapeLayer >= 0 && logoutEscape > popupEscapeLayer &&
              sharedPopupEscape > logoutEscape && mailEscape > sharedPopupEscape &&
              enchantEscape > mailEscape && skillEscape > enchantEscape,
            "shared StaticPopup insertion changed existing popup Escape precedence");
        int readClock = lifecycle.IndexOf("long now = Stopwatch.GetTimestamp();",
            StringComparison.Ordinal);
        int commitClock = lifecycle.IndexOf("_staticPopupLastUpdateTicks = now;",
            StringComparison.Ordinal);
        int advance = lifecycle.IndexOf("StaticPopupCoordinatorLaw.Advance(",
            StringComparison.Ordinal);
        Check(readClock >= 0 && commitClock > readClock && advance > commitClock &&
              lifecycle.Contains("StaticPopupCoordinatorLaw.SlotCount", StringComparison.Ordinal) &&
              lifecycle.Contains("now >= previous", StringComparison.Ordinal) &&
              // HideByType used to be banned outright here, when the lifecycle was purely
              // the Advance pump. It now also dismisses a stale DELETE_ITEM confirmation
              // once the carried item is gone, which does not weaken the pump because it
              // runs after it. Assert that ordering instead of banning the call: the pump
              // must still be reached unconditionally on every lifecycle tick.
              lifecycle.IndexOf("StaticPopupCoordinatorLaw.Advance(",
                  StringComparison.Ordinal) >= 0 &&
              (lifecycle.IndexOf("HideByType", StringComparison.Ordinal) < 0 ||
               lifecycle.IndexOf("HideByType", StringComparison.Ordinal) >
                   lifecycle.IndexOf("StaticPopupCoordinatorLaw.Advance(",
                       StringComparison.Ordinal)),
            "PARTY_INVITE always-pumped monotonic two-slot Advance drift");

        int inviteDrawStart = runtime.IndexOf("private void DrawPartyInvite()",
            StringComparison.Ordinal);
        int inviteDrawEnd = runtime.IndexOf("private bool DrawPartyInviteButton", inviteDrawStart,
            StringComparison.Ordinal);
        string inviteDraw = inviteDrawStart >= 0 && inviteDrawEnd > inviteDrawStart
            ? runtime[inviteDrawStart..inviteDrawEnd]
            : "";
        Check(inviteDraw.Contains("PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              inviteDraw.Contains("visible.Instance.DataToken", StringComparison.Ordinal) &&
              Count(inviteDraw, "StaticPopupCoordinatorLaw.Click(") == 2 &&
              !inviteDraw.Contains("Stopwatch.GetTimestamp()", StringComparison.Ordinal) &&
              !inviteDraw.Contains("StaticPopupCoordinatorLaw.Advance", StringComparison.Ordinal),
            "PARTY_INVITE renderer/click must consume the current instance without timeout logic");
        Check(partyLaw.Contains("PartyInvitePopupDefinition = new(", StringComparison.Ordinal) &&
              partyLaw.Contains("PartyInvitePopup(\n        StaticPopupCoordinatorLaw.Slots slots)",
                  StringComparison.Ordinal) &&
              partyLaw.Contains("IsPartyInviteVisible(StaticPopupCoordinatorLaw.Slots slots)",
                  StringComparison.Ordinal) &&
              !partyLaw.Contains("PartyInviteDismissal", StringComparison.Ordinal) &&
              !partyLaw.Contains("PartyInviteEffect", StringComparison.Ordinal) &&
              !partyLaw.Contains("PartyInviteWireCount", StringComparison.Ordinal) &&
              !runtime.Contains("_partyInviter", StringComparison.Ordinal) &&
              !runtime.Contains("_partyInviteDeadline", StringComparison.Ordinal),
            "PARTY_INVITE pure definition/query or legacy parallel-state removal drift");
        // Every coordinator call site in this file, named so a third one is a real signal:
        //   Show        x2  the SMSG invite path and ShowPartyTestInvite - BOTH pass
        //                   PartyInvitePopupDefinition, so no second production type is
        //                   shown here; the second is the /partytest fixture.
        //   HideByType  x2  the PARTY_INVITE direct hide, and the DELETE_ITEM stale
        //                   dismiss in the lifecycle (the item left the cursor).
        //   Escape/Advance x1, Click x2 - unchanged.
        Check(Count(runtime, "StaticPopupCoordinatorLaw.Show(") == 2 &&
              Count(runtime, "StaticPopupCoordinatorLaw.HideByType(") == 2 &&
              Count(runtime, "StaticPopupCoordinatorLaw.Escape(") == 1 &&
              Count(runtime, "StaticPopupCoordinatorLaw.Advance(") == 1 &&
              // Three prose pins stood here, asserting that particular comments existed in
              // the runtime. Two were deleted in 98aab83 with the behaviour untouched. That
              // is the mirror of banning a word: the check breaks when documentation is
              // reworded, and a reworder can satisfy it without changing anything real.
              // The Count() terms above are the actual guard - admitting a second production
              // popup type means another Show(/HideByType(/Escape(/Advance( call site.
              Count(runtime, "StaticPopupCoordinatorLaw.Click(") == 2,
            "bounded coordinator slice admitted another production type or deferred renderer");
        int parseRoster = runtime.IndexOf("PartyFramePacketLaw.ParseRoster(body)",
            StringComparison.Ordinal);
        int leaveDecision = runtime.IndexOf("PartyFrameUiLaw.IsLeaveRoster(wire)", parseRoster,
            StringComparison.Ordinal);
        int leaveInviteHide = runtime.IndexOf("if (leaving) HidePartyInvite();", leaveDecision,
            StringComparison.Ordinal);
        int commitRoster = runtime.IndexOf("_partyMembers.Clear();", parseRoster,
            StringComparison.Ordinal);
        Check(parseRoster >= 0 && leaveDecision > parseRoster &&
              leaveInviteHide > leaveDecision && commitRoster > leaveInviteHide &&
              net.Contains("ApplyPartyMemberStats(body, fullSnapshot: false)", StringComparison.Ordinal) &&
              net.Contains("ApplyPartyMemberStats(body, fullSnapshot: true)", StringComparison.Ordinal),
            "party atomic roster/FULL-vs-delta dispatch drift");
        // The binding is handed to OpenUnitPopup now instead of being assigned to a field.
        Check(runtime.Contains("InspectBinding.Party(hoveredIndex));",
                  StringComparison.Ordinal) &&
              runtime.Contains("action == PartyPointerAction.Target", StringComparison.Ordinal) &&
              // Through the painterly art path, which falls back to
              // CircularHandle with the mode off - asserted in Program.cs.
              runtime.Contains("PainterlyRoundArt(portraitPath)", StringComparison.Ordinal) &&
              runtime.Contains("party-token-guid-is-not-streamed", StringComparison.Ordinal) &&
              runtime.Contains("own.Fields.Bytes0.Race", StringComparison.Ordinal) &&
              runtime.Contains("hovered.Dead", StringComparison.Ordinal) &&
              runtime.Contains("bool pvp = view.Pvp", StringComparison.Ordinal),
            "party no-retarget/circular fallback/merged-state seam drift");
        Check(session.Contains("Op.CMSG_GROUP_ACCEPT, BuildGroupAcceptBody()",
                   StringComparison.Ordinal) &&
              session.Contains("Op.CMSG_GROUP_DECLINE, BuildGroupDeclineBody()",
                   StringComparison.Ordinal),
            "party exact empty accept/decline bodies drift");
        Check(live.Contains(
                  "party-stage rejected: Party proof requires observed wire/runtime state; no state mutated",
                  StringComparison.Ordinal) &&
              live.Contains(
                  "party-invite-stage rejected: Party invite proof requires an inbound invitation; no state mutated",
                  StringComparison.Ordinal) &&
              live.Contains(
                  "party-clear rejected: command cannot erase authenticated roster/invite state; no state mutated",
                  StringComparison.Ordinal) &&
              !live.Contains("StagePartyFrameProof", StringComparison.Ordinal) &&
              !live.Contains("StagePartyInviteProof", StringComparison.Ordinal),
            "legacy Party fixture commands must reject without mutation");
        Check(capture.Contains("party-frame-requires-observed-wire-roster", StringComparison.Ordinal) &&
              capture.Contains("party-invite-requires-observed-inbound-invitation",
                  StringComparison.Ordinal) &&
              capture.Contains("observed-party-wire-runtime", StringComparison.Ordinal) &&
              capture.Contains("compactSlotSources", StringComparison.Ordinal) &&
              capture.Contains("captureStateMutation\"] = false", StringComparison.Ordinal) &&
              capture.Contains("scenario[\"slot\"]", StringComparison.Ordinal) &&
              capture.Contains("scenario[\"type\"]", StringComparison.Ordinal) &&
              capture.Contains("scenario[\"timeLeftSeconds\"]", StringComparison.Ordinal) &&
              capture.Contains("scenario[\"definitionFlags\"]", StringComparison.Ordinal) &&
              capture.Contains("scenario[\"integratedTypes\"]", StringComparison.Ordinal) &&
              capture.Contains("new[] { PartyInvitePopupType }", StringComparison.Ordinal) &&
              capture.Contains("PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              !capture.Contains("_partyInviter", StringComparison.Ordinal) &&
              !capture.Contains("_partyInviteDeadline", StringComparison.Ordinal),
            "party capture observational provenance/coordinator telemetry drift");

        byte[] mask = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
        IconApertureMask.ApplyCircularBgra(mask, 8, 8);
        int Alpha(int x, int y) => mask[(y * 8 + x) * 4 + 3];
        Check(Alpha(0, 0) == 0 && Alpha(7, 0) == 0 && Alpha(0, 7) == 0 &&
              Alpha(7, 7) == 0 && Alpha(3, 3) == 255,
            "party TemporaryPortrait circular edge-alpha containment drift");
    }

    private static IEnumerable<StaticPopupCoordinatorLaw.EffectKind> PopupKinds(
        StaticPopupCoordinatorLaw.Plan plan) => plan.Effects.Select(effect => effect.Kind);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < .0001f;

    private static int Count(string source, string value)
    {
        int count = 0;
        int at = 0;
        while ((at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += value.Length;
        }
        return count;
    }

    private static void RejectEveryTruncation<T>(byte[] body, Func<byte[], T> parse, string label)
    {
        for (int length = 0; length < body.Length; length++)
        {
            bool rejected = false;
            try { _ = parse(body[..length]); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            { rejected = true; }
            Check(rejected, $"party {label} truncation accepted at {length}/{body.Length}");
        }
    }

    private static void RejectTrailing<T>(byte[] body, Func<byte[], T> parse, string label)
    {
        bool rejected = false;
        try { _ = parse([.. body, 0xff]); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, $"party {label} trailing byte accepted");
    }

    private static void ExpectReject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidDataException(message);
    }

    private static byte[] RosterFixture(int memberCount, byte ownFlags = 0,
        Func<int, byte>? memberFlags = null)
    {
        var writer = new PacketWriter();
        writer.WriteU8(memberCount > 5 ? (byte)1 : (byte)0);
        writer.WriteU8(ownFlags);
        writer.WriteU32((uint)memberCount);
        for (int i = 0; i < memberCount; i++)
        {
            writer.WriteCString($"Member{i + 1}");
            writer.WriteU64(0x1000ul + (ulong)i);
            writer.WriteU8(PartyFrameUiLaw.Online);
            writer.WriteU8(memberFlags?.Invoke(i) ?? ownFlags);
        }
        writer.WriteU64(memberCount == 0 ? 0 : 0x1000ul);
        if (memberCount > 0)
        {
            writer.WriteU8(2);
            writer.WriteU64(0x1000ul + (ulong)(memberCount - 1));
            writer.WriteU8(2);
            writer.WriteU8(0);
        }
        return writer.ToArray();
    }

    private static byte[] StatsFixture(ulong guid)
    {
        var writer = new PacketWriter();
        writer.WritePackedGuid(guid);
        writer.WriteU32(0x7f);
        writer.WriteU8(PartyFrameUiLaw.Online);
        writer.WriteU16(15);
        writer.WriteU16(100);
        writer.WriteU8(0);
        writer.WriteU16(70);
        writer.WriteU16(100);
        writer.WriteU16(60);
        return writer.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
