using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class QuestLogClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_QUESTLOG_SWAP_QUEST == 0x0193 &&
              WorldSession.BuildQuestLogSwapBody(3, 7).SequenceEqual(new byte[] { 3, 7 }),
            "CMSG_QUESTLOG_SWAP_QUEST opcode or two-u8 body drift");
        Check(QuestFrameUiLaw.WindowOrigin(2f) == new Vector2(0, 208) &&
              QuestFrameUiLaw.WindowSize(2f) == new Vector2(768, 1024) &&
              QuestFrameUiLaw.ClampQuestLogOffset(99, 20) == 14 &&
              QuestFrameUiLaw.ClampQuestLogOffset(-1, 4) == 0 &&
              QuestFrameUiLaw.QuestLogDetailRect == new QuestLogicalRect(19, 175, 300, 261) &&
              QuestFrameUiLaw.ClampQuestLogDetailScroll(999, 500) == 239 &&
              QuestFrameUiLaw.QuestLogCloseRect == new QuestLogicalRect(322, 8, 32, 32) &&
              QuestFrameUiLaw.QuestLogCountPillRect(50) ==
                  new QuestLogicalRect(271, 41, 66, 20) &&
              QuestFrameUiLaw.QuestLogCollapseLeftRect ==
                  new QuestLogicalRect(64, 40, 8, 32) &&
              QuestFrameUiLaw.QuestLogTrackRect == new QuestLogicalRect(129, 44, 20, 20) &&
              QuestFrameUiLaw.QuestLogDetailScrollBarRect ==
                  new QuestLogicalRect(325, 175, 16, 261) &&
              QuestFrameUiLaw.QuestLogDetailThumbY(239, 500) == 404 &&
              QuestFrameUiLaw.QuestLogAbandonRect ==
                  new QuestLogicalRect(17, 437, 125, 21) &&
              QuestFrameUiLaw.QuestLogShareRect == new QuestLogicalRect(141, 437, 123, 21) &&
              QuestFrameUiLaw.QuestLogExitRect == new QuestLogicalRect(264, 437, 77, 21) &&
              QuestFrameUiLaw.QuestLogRowMin(5) == new Vector2(19, 150) &&
              QuestFrameUiLaw.AbandonPopupOrigin(new Vector2(1920, 1080), 2f) ==
                  new Vector2(640, 256) &&
              QuestFrameUiLaw.AbandonPopupAcceptRect == new QuestLogicalRect(26, 36, 128, 20) &&
              QuestFrameUiLaw.AbandonPopupCancelRect == new QuestLogicalRect(167, 36, 128, 20) &&
              QuestFrameUiLaw.QuestWatchTopRight(new Vector2(1920, 1080), 2f) ==
                  new Vector2(1920, 384) &&
              QuestFrameUiLaw.QuestWatchLineTop(0, true, true) == 14 &&
              QuestFrameUiLaw.QuestWatchLineTop(26, true, false) == 31 &&
              QuestFrameUiLaw.QuestWatchLineTop(26, false, false) == 27 &&
              QuestFrameUiLaw.AutoQuestWatchSeconds == 300,
            "quest-log modal/list law drift");
        QuestLogicalRect countPill = QuestFrameUiLaw.QuestLogCountPillRect(50);
        Vector2 countText = QuestFrameUiLaw.QuestLogCountTextMin(countPill, 30, 20);
        Check(QuestFrameUiLaw.QuestLogListRect == new QuestLogicalRect(19, 75, 300, 91) &&
              QuestFrameUiLaw.QuestLogTitleCenter == new Vector2(192, 22) &&
              QuestFrameUiLaw.QuestLogRowRect(5) == new QuestLogicalRect(19, 150, 300, 16) &&
              QuestFrameUiLaw.QuestLogFoldIconRect(5) ==
                  new QuestLogicalRect(22, 150, 16, 16) &&
              QuestFrameUiLaw.QuestLogWatchCheckRect(2, 50) ==
                  new QuestLogicalRect(78, 105, 16, 16) &&
              countText == new Vector2(278, 45) &&
              QuestFrameUiLaw.QuestLogCountValueMin(countText, 30) == new Vector2(311, 45) &&
              QuestFrameUiLaw.QuestLogCollapseArt.Length == 3 &&
              QuestFrameUiLaw.QuestLogCollapseArt[1].Rect ==
                  QuestFrameUiLaw.QuestLogCollapseMiddleRect &&
              QuestFrameUiLaw.QuestLogDetailThumbRect(239, 500) ==
                  new QuestLogicalRect(325, 404, 16, 16) &&
              QuestFrameUiLaw.QuestLogRadioCheckUvMax == new Vector2(.5f, 1f),
            "quest-log control/list/scroll geometry drift");
        QuestScreenRect detailClip = QuestFrameUiLaw.QuestLogDetailClip(
            new Vector2(100, 200), 2);
        Vector2 detailContent = QuestFrameUiLaw.QuestLogDetailContentOrigin(
            new Vector2(100, 200), 30, 2);
        Vector2 spellMin = QuestFrameUiLaw.QuestLogRewardSpellMin(detailContent, 300, 2);
        Check(detailClip.Min == new Vector2(138, 550) &&
              detailClip.Max == new Vector2(738, 1072) &&
              detailContent == new Vector2(100, 140) &&
              QuestFrameUiLaw.QuestLogDetailTextMin(detailContent, 228, 2) ==
                  new Vector2(148, 596) &&
              QuestFrameUiLaw.QuestLogDetailGridOrigin(detailContent, 2) ==
                  new Vector2(148, 140) &&
              QuestFrameUiLaw.QuestLogDetailMoneyMin(detailContent, 300, 2) ==
                  new Vector2(480, 740) &&
              spellMin == new Vector2(148, 740) &&
              QuestFrameUiLaw.QuestLogRewardSpellTextMin(spellMin, 2) ==
                  new Vector2(198, 748) &&
              QuestFrameUiLaw.QuestLogRewardSpellSize == new Vector2(20),
            "quest-log detail clip/content/reward geometry drift");
        IReadOnlyList<QuestFrameArtSeat> logArt = QuestFrameUiLaw.PanelArt(true, false);
        IReadOnlyList<QuestFrameArtSeat> greetingArt = QuestFrameUiLaw.PanelArt(false, true);
        Check(QuestFrameUiLaw.FrameRect == new QuestLogicalRect(0, 0, 384, 512) &&
              QuestFrameUiLaw.NpcPortraitRect == new QuestLogicalRect(7, 6, 60, 60) &&
              QuestFrameUiLaw.NpcNameFont == "GameFontHighlight" &&
              QuestFrameUiLaw.NpcNameCenter == new Vector2(192, 30) &&
              QuestFrameUiLaw.NpcNameFrameRect == new QuestLogicalRect(42, 16, 300, 14) &&
              QuestFrameUiLaw.NpcNameTextRect == new QuestLogicalRect(74.5f, 20, 235, 20) &&
              QuestFrameUiLaw.CloseRect(false) == new QuestLogicalRect(326, 15, 32, 32) &&
              QuestFrameUiLaw.CloseRect(true) == QuestFrameUiLaw.QuestLogCloseRect &&
              logArt.Count == 5 && logArt[0] == new QuestFrameArtSeat(
                  @"Interface\QuestFrame\UI-QuestLog-BookIcon",
                  new QuestLogicalRect(4, 4, 64, 64)) &&
              logArt[4].Rect == new QuestLogicalRect(256, 256, 128, 256) &&
              greetingArt.Count == 5 && greetingArt[4] == new QuestFrameArtSeat(
                  @"Interface\QuestFrame\UI-Quest-BotLeftPatch",
                  new QuestLogicalRect(22, 380, 128, 64)),
            "quest shared log/NPC shell geometry drift");
        QuestScreenRect npcClip = QuestFrameUiLaw.NpcScrollClip(
            new Vector2(100, 200), 2);
        Check(QuestFrameUiLaw.NpcScrollRect == new QuestLogicalRect(23, 81, 300, 334) &&
              QuestFrameUiLaw.NpcScrollBarRect == new QuestLogicalRect(329, 81, 16, 334) &&
              QuestFrameUiLaw.NpcScrollDownRect == new QuestLogicalRect(329, 399, 16, 16) &&
              QuestFrameUiLaw.NpcScrollTrackRect == new QuestLogicalRect(329, 97, 16, 302) &&
              npcClip.Min == new Vector2(146, 362) &&
              npcClip.Max == new Vector2(746, 1030) &&
              QuestFrameUiLaw.NpcScrollContentOrigin(npcClip.Min, 30, 2) ==
                  new Vector2(146, 302) &&
              QuestFrameUiLaw.NpcScrollContentSize(500, 2) == new Vector2(600, 1000) &&
              QuestFrameUiLaw.NpcScrollThumbRect(83, 500) ==
                  new QuestLogicalRect(329, 240, 16, 16) &&
              QuestFrameUiLaw.NpcGreetingGoodbyeRect ==
                  new QuestLogicalRect(267, 417, 78, 22) &&
              QuestFrameUiLaw.NpcDetailAcceptRect == new QuestLogicalRect(23, 418, 77, 22) &&
              QuestFrameUiLaw.NpcProgressPrimaryRect ==
                  new QuestLogicalRect(22, 418, 120, 22) &&
              QuestFrameUiLaw.NpcRewardCancelRect ==
                  new QuestLogicalRect(267, 417, 78, 22) &&
              QuestFrameUiLaw.AbandonPopupTextCenter(new Vector2(100, 200), 2) ==
                  new Vector2(420, 244),
            "quest NPC scroll/buttons and abandon popup geometry drift");
        Check(QuestFrameUiLaw.NpcContentInitialY == 10 &&
              QuestFrameUiLaw.NpcContentBodyWidth == 270 &&
              QuestFrameUiLaw.NpcRewardBodyWidth == 275 &&
              QuestFrameUiLaw.NpcContentTextMin(new Vector2(100, 200), 10, 2) ==
                  new Vector2(110, 220) &&
              QuestFrameUiLaw.NpcReceiveTextMin(new Vector2(100, 200), 20, 2) ==
                  new Vector2(116, 240) &&
              QuestFrameUiLaw.NpcWrappedLineMin(new Vector2(100, 200), 2, 24) ==
                  new Vector2(100, 248) &&
              QuestFrameUiLaw.NpcScreenTextSize(55, 12) == new Vector2(55, 12) &&
              QuestFrameUiLaw.NpcTraceSize(295, 18, 2) == new Vector2(590, 18) &&
              QuestFrameUiLaw.NpcInlineMoneyMin(
                  new Vector2(100, 200), 40, 5, 80, 10, 2) == new Vector2(210, 280),
            "quest NPC content-column geometry drift");
        QuestMoneyCoinSeat moneySeat = QuestFrameUiLaw.MoneyCoinSeat(
            new Vector2(100, 200), 120, 18, 2);
        Check(moneySeat.NumberMin == new Vector2(120, 200) &&
              moneySeat.IconMin == new Vector2(138, 200) &&
              moneySeat.FrameSize == new Vector2(44, 26) &&
              moneySeat.NumberSize == new Vector2(18, 26) &&
              moneySeat.IconSize == new Vector2(26) &&
              moneySeat.NextX == 172 &&
              QuestFrameUiLaw.MoneyAnchorOffset(0, true) == 10 &&
              QuestFrameUiLaw.MoneyAnchorOffset(0, false) == 15 &&
              QuestFrameUiLaw.MoneyAnchorOffset(1, true) == 4,
            "quest money coin-chain geometry drift");
        Vector2 greetingRow = QuestFrameUiLaw.GreetingRowMin(
            new Vector2(100, 200), 40, 2);
        Check(QuestFrameUiLaw.GreetingTextMin(new Vector2(100, 200), 10, 2) ==
                  new Vector2(120, 220) &&
              QuestFrameUiLaw.GreetingBreakRect(50) ==
                  new QuestLogicalRect(22, 60, 256, 32) &&
              greetingRow == new Vector2(100, 280) &&
              QuestFrameUiLaw.GreetingRowSize(16, 2) == new Vector2(570, 36) &&
              QuestFrameUiLaw.GreetingTitleMin(greetingRow, 2) == new Vector2(140, 280) &&
              QuestFrameUiLaw.GreetingTitleTraceSize(10, 2) == new Vector2(550, 32) &&
              QuestFrameUiLaw.GreetingBulletRect == new QuestLogicalRect(0, 0, 16, 16),
            "quest NPC greeting content/row geometry drift");
        Check(QuestFrameUiLaw.QuestDifficultyColor(20, 25) == new Vector4(1f, .1f, .1f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 23) == new Vector4(1f, .5f, .25f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 20) == new Vector4(1f, 1f, 0f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 17) == new Vector4(.25f, .75f, .25f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 1) == new Vector4(.5f, .5f, .5f, 1f),
            "quest-log difficulty bands drift");
        Check(QuestFrameUiLaw.ItemLink(2024, "Militia Hammer", 1) ==
                  "|cffffffff|Hitem:2024:0:0:0|h[Militia Hammer]|h|r" &&
              QuestFrameUiLaw.ItemLink(2000, "Another Helm", 2)
                  .StartsWith("|cff1eff00", StringComparison.Ordinal) &&
              QuestFrameUiLaw.ItemClickAction(true, true, false, false, false, true) ==
                  QuestItemClickAction.DressUp &&
              QuestFrameUiLaw.ItemClickAction(true, false, true, true, false, true) ==
                  QuestItemClickAction.InsertChat &&
              QuestFrameUiLaw.ItemClickAction(true, false, true, false, true, true) ==
                  QuestItemClickAction.None &&
              QuestFrameUiLaw.ItemClickAction(true, false, false, false, true, true) ==
                  QuestItemClickAction.Select &&
              QuestFrameUiLaw.ItemClickAction(true, false, false, false, false, true) ==
                  QuestItemClickAction.None,
            "quest reward-row modifier/link fork drift");
        Vector2 itemMin = QuestFrameUiLaw.ItemGridRowMin(
            new Vector2(100, 200), 300, 3, 2);
        Check(itemMin == new Vector2(390, 886) &&
              QuestFrameUiLaw.ItemHitRect == new QuestLogicalRect(0, 0, 147, 41) &&
              QuestFrameUiLaw.ItemIconRect == new QuestLogicalRect(0, 0, 39, 39) &&
              QuestFrameUiLaw.ItemNameFrameRect.ScaledMin(itemMin, 2) ==
                  new Vector2(448, 862) &&
              QuestFrameUiLaw.ItemNameFrameRect.ScaledSize(2) == new Vector2(256, 128) &&
              QuestFrameUiLaw.ItemNameTextMin(itemMin, 2) == new Vector2(478, 910) &&
              QuestFrameUiLaw.ItemCountMin(itemMin, new Vector2(40, 18), 2) ==
                  new Vector2(420, 936) &&
              QuestFrameUiLaw.ItemHighlightRect.ScaledMin(itemMin, 2) ==
                  new Vector2(374, 872) &&
              QuestFrameUiLaw.ItemHighlightRect.ScaledSize(2) == new Vector2(512, 128) &&
              QuestFrameUiLaw.ItemTooltipSeat(itemMin, new Vector2(294, 82)) ==
                  new QuestTooltipSeat(new Vector2(684, 886), Vector2.UnitY),
            "quest reward-item row geometry drift");
        IReadOnlyList<QuestLogHeaderGroup> groups = QuestFrameUiLaw.GroupQuestLogHeaders(
            ["Westfall", "Quests", "Westfall", "Alchemy"]);
        Check(groups.Count == 3 && groups[0].Header == "Alchemy" &&
              groups[1].Header == "Quests" && groups[2].Header == "Westfall" &&
              groups[2].QuestIndexes.SequenceEqual([0, 2]) &&
              QuestFrameUiLaw.QuestLogFoldIconMin(5) == new Vector2(22, 150) &&
              QuestFrameUiLaw.SecondsToTime(86400) == "24 Hrs " &&
              QuestFrameUiLaw.SecondsToTime(90061) == "1 Day 1 Hr " &&
              QuestFrameUiLaw.SecondsToTime(61) == "1 Min 1 Sec ",
            "quest-log header grouping/fold law drift");
        var priorQuestIds = new HashSet<uint> { 11, 22 };
        Check(QuestFrameUiLaw.QuestAddedSound == "QUESTADDED" &&
              !QuestFrameUiLaw.ShouldPlayQuestAddedSound(false, new HashSet<uint>(),
                  new HashSet<uint> { 11, 22 }) &&
              !QuestFrameUiLaw.ShouldPlayQuestAddedSound(true, priorQuestIds,
                  new HashSet<uint> { 11, 22 }) &&
              !QuestFrameUiLaw.ShouldPlayQuestAddedSound(true, priorQuestIds,
                  new HashSet<uint> { 11 }) &&
              QuestFrameUiLaw.ShouldPlayQuestAddedSound(true, priorQuestIds,
                  new HashSet<uint> { 11, 22, 33 }),
            "quest-added sound edge/baseline law drift");
        Check(QuestFrameUiLaw.ObjectiveItemLabel("Tough Wolf Meat") ==
                  "Tough Wolf Meat" &&
              QuestFrameUiLaw.ObjectiveItemLabel(null) == "..." &&
              QuestFrameUiLaw.ObjectiveItemLabel("") == "...",
            "quest objective item-name/loading label drift");

        var writer = new PacketWriter();
        writer.WriteU32(77); writer.WriteU32(2); writer.WriteU32(18);
        writer.WriteI32(-24);
        for (int i = 0; i < 6; i++) writer.WriteU32(0);
        writer.WriteI32(125); writer.WriteU32(0); writer.WriteU32(42);
        writer.WriteU32(0); writer.WriteU32(0);
        writer.WriteU32(6948); writer.WriteU32(1);
        for (int i = 1; i < 4; i++) { writer.WriteU32(0); writer.WriteU32(0); }
        writer.WriteU32(117); writer.WriteU32(5);
        for (int i = 1; i < 6; i++) { writer.WriteU32(0); writer.WriteU32(0); }
        for (int i = 0; i < 4; i++) writer.WriteU32(0);
        writer.WriteCString("A Full Query"); writer.WriteCString("Do the work.");
        writer.WriteCString("Long details."); writer.WriteCString("Done.");
        writer.WriteU32(123); writer.WriteU32(10); writer.WriteU32(0); writer.WriteU32(0);
        writer.WriteU32(0); writer.WriteU32(0); writer.WriteU32(456); writer.WriteU32(4);
        for (int i = 0; i < 8; i++) writer.WriteU32(0);
        writer.WriteCString("Special targets"); writer.WriteCString("");
        writer.WriteCString(""); writer.WriteCString("");
        QuestTemplate query = QuestPackets.ParseQueryResponse(writer.ToArray());
        Check(query.QuestId == 77 && query.Level == 18 && query.ZoneOrSort == -24 &&
              query.Title == "A Full Query" &&
              query.Money == 125 && query.RewardSpell == 42 &&
              query.FixedRewards.SequenceEqual([new QuestRewardItem(6948, 1, 0)]) &&
              query.ChoiceRewards.SequenceEqual([new QuestRewardItem(117, 5, 0)]) &&
              query.ObjectivesText == "Do the work." && query.Details == "Long details." &&
              query.Objectives[0] == new QuestLogObjective(123, 10, 0, 0, "Special targets") &&
              query.Objectives[1] == new QuestLogObjective(0, 0, 456, 4, ""),
            "quest query fixed-count/template/objective decode drift");

        string runtime = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Quest.cs"));
        string questFacts = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Scene", "GameLoop.QuestFacts.cs"));
        string partyQuestLog = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Panels", "GameLoop.PartyQuestLog.cs"));
        string session = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Net", "WorldSession.cs"));
        string client = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Net", "NetworkClient.cs"));
        int npcRendererStart = runtime.IndexOf(
            "private static float DrawQuestWrappedText", StringComparison.Ordinal);
        int npcRendererEnd = runtime.IndexOf(
            "private void DrawQuestNpcScrollbar", StringComparison.Ordinal);
        Check(npcRendererStart >= 0 && npcRendererEnd > npcRendererStart,
            "quest NPC renderer source slice drift");
        string npcRenderer = runtime[npcRendererStart..npcRendererEnd];
        Check(session.Contains("Op.CMSG_QUESTLOG_SWAP_QUEST", StringComparison.Ordinal) &&
              client.Contains("QuestLogSwap(byte firstSlot, byte secondSlot)",
                  StringComparison.Ordinal) &&
              runtime.Contains(
                  "UiPanelFrameOrigin(UiPanelOwnershipRegistry[logMode ? 8 : 7], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.AbandonPopupOrigin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.PanelArt(", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcPortraitRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcNameFrameRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcNameFont", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.CloseRect(logMode)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcScrollClip", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcScrollContentOrigin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcScrollContentSize", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcScrollThumbRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcGreetingGoodbyeRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcDetailAcceptRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcProgressPrimaryRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcRewardPrimaryRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.AbandonPopupTextCenter", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcContentTextMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcReceiveTextMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcWrappedLineMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcScreenTextSize", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcTraceSize", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.NpcInlineMoneyMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.MoneyCoinSeat", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.MoneyAnchorOffset", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingTextMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingBreakRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingRowMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingRowSize", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingTitleMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GreetingTitleTraceSize", StringComparison.Ordinal) &&
              !runtime.Contains("origin + new Vector2(7, 6)", StringComparison.Ordinal) &&
              !runtime.Contains("origin + new Vector2(42, 16)", StringComparison.Ordinal) &&
              !runtime.Contains("origin + new Vector2(329, 81)", StringComparison.Ordinal) &&
              !runtime.Contains("origin + new Vector2(267, 417)", StringComparison.Ordinal) &&
              !runtime.Contains("p+new Vector2(5,y)", StringComparison.Ordinal) &&
              !runtime.Contains("p + new Vector2(5, y)", StringComparison.Ordinal) &&
              !runtime.Contains("p + new Vector2(10, y)", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(285, textHeight + 2)", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(numberWidth+13*s,13*s)", StringComparison.Ordinal) &&
              !runtime.Contains("x += 17 * s", StringComparison.Ordinal) &&
              !npcRenderer.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("private readonly record struct QuestAbandonConfirmation(",
                  StringComparison.Ordinal) &&
              runtime.Contains("ulong Subject, uint QuestId, string Title", StringComparison.Ordinal) &&
              runtime.Contains("QuestLogForSubject(confirmation.Subject)", StringComparison.Ordinal) &&
              runtime.Contains("AbandonQuest(confirmation.Subject, confirmation.QuestId)",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (ShiftHeld()) HandleQuestLogShiftClick",
                  StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestWatchTopRight", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GroupQuestLogHeaders(headers)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogFoldIconRect(row)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogListRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogRowRect(row)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogWatchCheckRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogCollapseArt", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailThumbRect", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(QuestFrameUiLaw.QuestLogListX",
                  StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(300, 16)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.SecondsToTime(secondsLeft.Value)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemClickAction", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemLink", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemGridRowMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemHitRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemIconRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemNameFrameRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemNameTextMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemCountMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemHighlightRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ItemTooltipSeat", StringComparison.Ordinal) &&
              runtime.Contains("tooltipPanel == QuestNpcPanel.None", StringComparison.Ordinal) &&
              !runtime.Contains("min + new Vector2(29, -12)", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(147, 41)", StringComparison.Ordinal) &&
              !runtime.Contains("min + new Vector2(248, 57)", StringComparison.Ordinal) &&
              runtime.Contains("InsertChatText(itemLink)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailRect", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailClip", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailContentOrigin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailTextMin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailGridOrigin", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogRewardSpellTextMin", StringComparison.Ordinal) &&
              !runtime.Contains("contentOrigin + new Vector2(24", StringComparison.Ordinal) &&
              !runtime.Contains("contentOrigin + new Vector2(190", StringComparison.Ordinal) &&
              runtime.Contains("template.ChoiceRewards", StringComparison.Ordinal) &&
              runtime.Contains("template.FixedRewards", StringComparison.Ordinal) &&
              runtime.Contains("template.RewardSpell", StringComparison.Ordinal) &&
              runtime.Contains("AutoWatchQuest(value.QuestId);", StringComparison.Ordinal) &&
              runtime.Contains("ExpireAutomaticQuestWatches();", StringComparison.Ordinal) &&
              runtime.Contains("var quests = DisplayedQuestLog();", StringComparison.Ordinal) &&
              runtime.Contains("? ControlledGuid : _net?.PlayerGuid", StringComparison.Ordinal) &&
              questFacts.Contains("private (byte Slot, uint QuestId, uint Counters, uint Timer)[] DisplayedQuestLog()",
                  StringComparison.Ordinal) &&
              questFacts.Contains("AppendMemberQuestFacts(projected, [], MemberQuestEntries(subject));",
                  StringComparison.Ordinal) &&
              questFacts.Contains("destination.Add((entry.Slot, entry.QuestId,",
                  StringComparison.Ordinal) &&
              runtime.Contains("_questWatches.RemoveAll(id => !watchableNow.Contains(id));",
                  StringComparison.Ordinal) &&
              runtime.Contains("_questAutoWatchPending.Add(questId);", StringComparison.Ordinal) &&
              runtime.Contains("if (!Settings.Controls.AutomaticQuestTracking)",
                  StringComparison.Ordinal) &&
              runtime.Contains("AppendPartyQuestWatchLines(lines, owners);",
                  StringComparison.Ordinal) &&
              runtime.Contains("$\" - {name}: {text}\"", StringComparison.Ordinal) &&
              runtime.Contains("private readonly HashSet<uint> _questWatchCollapsed",
                  StringComparison.Ordinal) &&
              runtime.Contains("private readonly List<QuestWatchTitleHit> _questWatchTitleHits",
                  StringComparison.Ordinal) &&
              !runtime.Contains("MaxQuestWatches", StringComparison.Ordinal) &&
              !runtime.Contains("You may only watch", StringComparison.Ordinal) &&
              runtime.Contains("private bool TryToggleQuestWatchAt(Vector2 position, bool leftClick)",
                  StringComparison.Ordinal) &&
              runtime.Contains("_questWatchTitleHits.Add(hit);", StringComparison.Ordinal) &&
              !runtime.Contains("##quest-watch-title-", StringComparison.Ordinal) &&
              // The collapse toggle now hit-tests _questWatchTitleHits, so the id comes
              // from hit rather than line. Assert the whole remove-else-add toggle
              // rather than the bare call, since the toggle is the behaviour that matters.
              runtime.Contains("if (!_questWatchCollapsed.Remove(hit.QuestId))\n" +
                  "                _questWatchCollapsed.Add(hit.QuestId);",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (!collapsed && lines.Count < QuestFrameUiLaw.MaxQuestWatchLines)",
                  StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.ShouldPlayQuestAddedSound(",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(QuestFrameUiLaw.QuestAddedSound);",
                  StringComparison.Ordinal) &&
              runtime.Contains("_questLogSnapshotKnown = false;", StringComparison.Ordinal),
            "quest-log modal/abandon/watch production wiring drift");
        Check(runtime.Contains(".Where(o => o.ItemId != 0 && o.ItemCount > 0)",
                  StringComparison.Ordinal) &&
              runtime.Contains("_items.Require(itemId, 0, _net);",
                  StringComparison.Ordinal) &&
              runtime.Contains("label = QuestObjectiveItemLabel(objective.ItemId);",
                  StringComparison.Ordinal) &&
              partyQuestLog.Contains("label = QuestObjectiveItemLabel(objective.ItemId);",
                  StringComparison.Ordinal) &&
              !runtime.Contains("$\"Item {objective.ItemId}\"", StringComparison.Ordinal) &&
              !partyQuestLog.Contains("$\"Item {objective.ItemId}\"",
                  StringComparison.Ordinal),
            "quest objective item-template request/name wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
