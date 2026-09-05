using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class TradeFrameClinicalChecks
{
    public static void Run()
    {
        Check(TradeFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              TradeFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              TradeFrameUiLaw.PlayerPortrait == new TradeFrameUiLaw.LogicalRect(7, 6, 60, 60) &&
              TradeFrameUiLaw.RecipientPortrait ==
                  new TradeFrameUiLaw.LogicalRect(183, 6, 60, 60) &&
              TradeFrameUiLaw.TradeButton == new TradeFrameUiLaw.LogicalRect(186, 435, 85, 22) &&
              TradeFrameUiLaw.CancelButton == new TradeFrameUiLaw.LogicalRect(274, 435, 77, 22),
            "trade shell/portrait/action geometry drift");

        Check(TradeFrameUiLaw.Slot(player: true, 0) ==
                  new TradeFrameUiLaw.LogicalRect(26, 104, 153, 37) &&
              TradeFrameUiLaw.Slot(player: true, 5).Y == 324 &&
              TradeFrameUiLaw.Slot(player: true, 6).Y == 389 &&
              TradeFrameUiLaw.Slot(player: false, 0).X == 195 &&
              TradeFrameUiLaw.EmptySlot(player: true, 0) ==
                  new TradeFrameUiLaw.LogicalRect(13, 91, 64, 64) &&
              TradeFrameUiLaw.NameFrame(player: false, 6).X == 226 &&
              TradeFrameUiLaw.PlayerGoldInput ==
                  new TradeFrameUiLaw.LogicalRect(26, 72, 55, 20) &&
              TradeFrameUiLaw.PlayerSilverInput ==
                  new TradeFrameUiLaw.LogicalRect(107, 72, 30, 20) &&
              TradeFrameUiLaw.PlayerCopperCoin == new Vector2(170, 76) &&
              TradeFrameUiLaw.RecipientMoneyRightTop == new Vector2(344, 80) &&
              TradeFrameUiLaw.EnchantLabel == "Will Be Enchanted" &&
              TradeFrameUiLaw.NonTradedLabel == "Will Not Be Traded",
            "trade six-row/separated-enchant-slot geometry drift");

        Check(TradeFrameUiLaw.PlayerHighlight ==
                  new TradeFrameUiLaw.LogicalRect(19, 100, 161, 266) &&
              TradeFrameUiLaw.PlayerEnchantHighlight ==
                  new TradeFrameUiLaw.LogicalRect(19, 370, 161, 61) &&
              TradeFrameUiLaw.HighlightSlices(TradeFrameUiLaw.PlayerHighlight) is
                  { Length: 3 } playerSlices &&
              playerSlices[0].Rect ==
                  new TradeFrameUiLaw.LogicalRect(19, 100, 161, 16) &&
              playerSlices[1].Rect ==
                  new TradeFrameUiLaw.LogicalRect(19, 116, 161, 234) &&
              playerSlices[2].Rect ==
                  new TradeFrameUiLaw.LogicalRect(19, 350, 161, 16) &&
              playerSlices[0].UvMax == new Vector2(.62890625f, .0625f) &&
              TradeFrameUiLaw.CountPosition(new Vector2(100, 100), 12, 1.5f) ==
                  new Vector2(97, 85) &&
              TradeFrameUiLaw.ComposeMoney(12, 34, 56) == 123456 &&
              TradeFrameUiLaw.SplitMoney(123456) == (12, 34, 56) &&
              TradeFrameUiLaw.CoinUvMin(2) == new Vector2(.5f, 0) &&
              TradeFrameUiLaw.CoinUvMax(2) == new Vector2(.75f, 1) &&
              TradeFrameUiLaw.CoinSize(1.5f) == new Vector2(19.5f) &&
              TradeFrameUiLaw.CancelClick(accepted: true) ==
                  TradeFrameUiLaw.CancelAction.Unaccept &&
              TradeFrameUiLaw.CancelClick(accepted: false) ==
                  TradeFrameUiLaw.CancelAction.Close &&
              TradeFrameUiLaw.StatusCloses(3) && TradeFrameUiLaw.StatusCloses(21) &&
              !TradeFrameUiLaw.StatusCloses(4) && !TradeFrameUiLaw.StatusCloses(7) &&
              !TradeFrameUiLaw.StatusCloses(13),
            "trade accept/status state law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Trade.cs"));
        Check(runtime.Contains("UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[3])",
                  StringComparison.Ordinal) &&
              runtime.Contains("AnswerTradeRequest(wire.Partner);", StringComparison.Ordinal) &&
              runtime.Contains("_net?.BeginTrade();", StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawTradeAcceptHighlight", StringComparison.Ordinal) &&
              runtime.Contains("TradeFrameUiLaw.HighlightSlices", StringComparison.Ordinal) &&
              runtime.Contains("TradeFrameUiLaw.CountPosition", StringComparison.Ordinal) &&
              runtime.Contains("DrawTradeMoneyInputs", StringComparison.Ordinal) &&
              runtime.Contains("TradeFrameUiLaw.PlayerGoldInput", StringComparison.Ordinal) &&
              runtime.Contains("TradeFrameUiLaw.RecipientMoneyRightTop", StringComparison.Ordinal) &&
              runtime.Contains("TradeFrameUiLaw.MoneyIconPath", StringComparison.Ordinal) &&
              runtime.Contains("row.Count.ToString()", StringComparison.Ordinal) &&
              runtime.Contains("OfferPreparedItemTooltip", StringComparison.Ordinal) &&
              runtime.Contains("_net?.UnacceptTrade()", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##trade\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("_tradeInviteGuid", StringComparison.Ordinal) &&
              !runtime.Contains("DrawTradeInvitation", StringComparison.Ordinal),
            "trade production renderer bypasses geometry/protocol law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
