using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.Formats;

internal static class TaxiFrameClinicalChecks
{
    public static void Run()
    {
        var gated = new PacketWriter();
        gated.WriteU32(0);
        ShowTaxiNodesPacket gatedPacket = TaxiPackets.ParseShowNodes(gated.ToArray());
        Check(gatedPacket.Gate == 0 && gatedPacket.FlightMasterGuid == 0 &&
              gatedPacket.KnownMask.Length == TaxiPackets.MaskWords,
            "gated-off SHOWTAXINODES body drift");

        var shown = new PacketWriter();
        shown.WriteU32(1);
        shown.WriteU64(0xF130000160000001);
        shown.WriteU32(2);
        for (int i = 0; i < TaxiPackets.MaskWords; i++)
            shown.WriteU32(i == 0 ? 0x22u : 0u);
        ShowTaxiNodesPacket map = TaxiPackets.ParseShowNodes(shown.ToArray());
        Check(map.Gate == 1 && map.FlightMasterGuid == 0xF130000160000001 &&
              map.NearestNode == 2 && map.KnownMask[0] == 0x22 && map.KnownMask.Length == 8,
            "full SHOWTAXINODES body drift");

        TaxiPackets.RequireNewPathBody(Array.Empty<byte>());
        TaxiNodeStatusPacket status = TaxiPackets.ParseNodeStatus(
            Convert.FromHexString("F0DEBC9A7856341201"));
        Check((ushort)Op.SMSG_NEW_TAXI_PATH == 0x01AF &&
              (ushort)Op.CMSG_ACTIVATETAXIEXPRESS == 0x0312 &&
              TaxiFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              TaxiFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              TaxiFrameUiLaw.Frame == new TaxiFrameUiLaw.LogicalRect(0, 0, 384, 512) &&
              TaxiFrameUiLaw.ShellPieces.Length == 4 &&
              TaxiFrameUiLaw.ShellPieces[0] ==
                  new TaxiFrameUiLaw.LogicalRect(0, 0, 256, 256) &&
              TaxiFrameUiLaw.ShellPieces[3] ==
                  new TaxiFrameUiLaw.LogicalRect(256, 256, 128, 256) &&
              TaxiFrameUiLaw.PortraitOffset == new Vector2(8, 9) &&
              TaxiFrameUiLaw.PortraitSize == 58 &&
              TaxiFrameUiLaw.Portrait == new TaxiFrameUiLaw.LogicalRect(8, 9, 58, 58) &&
              TaxiFrameUiLaw.MapOffset == new Vector2(21, 75) &&
              TaxiFrameUiLaw.MapSize == new Vector2(316, 352) &&
              TaxiFrameUiLaw.Map == new TaxiFrameUiLaw.LogicalRect(21, 75, 316, 352) &&
              TaxiFrameUiLaw.Close == new TaxiFrameUiLaw.LogicalRect(323, 8, 32, 32) &&
              TaxiFrameUiLaw.NodeTooltipSeat(new Vector2(100, 200), 2f) ==
                  new TaxiFrameUiLaw.TooltipSeat(new Vector2(116, 184), Vector2.UnitY) &&
              status.FlightMasterGuid == 0x123456789ABCDEF0 && status.Known &&
              TaxiPackets.ParseActivateReply(Convert.FromHexString("03000000")) == 3 &&
              TaxiFrameUiLaw.ActivateErrorText(3) == "You don't have enough money!" &&
              TaxiFrameUiLaw.NoConnectedFlightPaths.Contains('’'),
            "taxi opcode/window/string drift");

        Check(Convert.ToHexString(TaxiPackets.BuildActivateExpressBody(
                  0x123456789ABCDEF0, 110, new uint[] { 2, 3, 4 })) ==
              "F0DEBC9A785634126E00000003000000020000000300000004000000" &&
              Convert.ToHexString(TaxiPackets.BuildActivateExpressBody(
                  0x123456789ABCDEF0, 0, Array.Empty<uint>())) ==
              "F0DEBC9A785634120000000000000000",
            "CMSG_ACTIVATETAXIEXPRESS body drift");

        var known = new HashSet<uint> { 1, 2, 4 };
        var graph = new Dictionary<uint, IReadOnlyList<TaxiPathInfo>>
        {
            [1] = new TaxiPathInfo[] { new(10, 1, 4, 10), new(11, 1, 2, 5) },
            [2] = new TaxiPathInfo[] { new(12, 2, 4, 20) },
        };
        var distances = new Dictionary<(uint, uint), float>
        {
            [(1, 4)] = 150, [(1, 2)] = 40, [(2, 4)] = 60,
        };
        TaxiResolvedRoute? route = TaxiRoutePlanner.ShortestRoute(known,
            node => graph.GetValueOrDefault(node, Array.Empty<TaxiPathInfo>()),
            (from, to) => distances[(from, to)], 1, 4);
        Check(route is { Fare: 25 } && route.Value.Chain.SequenceEqual(new uint[] { 1, 2, 4 }),
            "taxi geographic route metric/fare carry drift");

        TaxiContinentInfo nonSquare = new(0, Vector2.Zero, new Vector2(100, 50));
        Check(nonSquare.Project(new Vector3(50, 0, 0)) == new Vector2(.5f, 1f) &&
              TaxiFrameUiLaw.NodeCenter(new Vector2(.25f, .75f), Vector2.Zero, 1f) ==
                  new Vector2(79, 88),
            "taxi authored projection/bottom-left screen conversion drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Taxi.cs"));
        Check(runtime.Contains("TaxiPackets.ParseShowNodes", StringComparison.Ordinal) &&
              runtime.Contains("TaxiPackets.RequireNewPathBody", StringComparison.Ordinal) &&
              runtime.Contains("ShowUiInfo(TaxiFrameUiLaw.DiscoveredText)",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(TaxiFrameUiLaw.DiscoveredSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[6], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("TaxiRoutePlanner.BuildVisible", StringComparison.Ordinal) &&
              runtime.Contains("TaxiFrameUiLaw.ReachableIcon", StringComparison.Ordinal) &&
              runtime.Contains("DrawTaxiRouteLine", StringComparison.Ordinal) &&
              runtime.Contains("OfferTaxiNodeTooltip", StringComparison.Ordinal) &&
              runtime.Contains("TaxiFrameUiLaw.NodeTooltipSeat(center, s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("tooltipSeat.Anchor, tooltipSeat.Pivot",
                  StringComparison.Ordinal) &&
              runtime.Contains("ActivateTaxiExpress", StringComparison.Ordinal) &&
              runtime.Contains("TryBetween", StringComparison.Ordinal) &&
              // POSSESS_LAW 2.1 lists taxi. Three gates in this file use the interaction
              // pose and none legitimately uses the session pose, so ban the forbidden
              // call outright - a positive match alone would miss one gate regressing.
              !runtime.Contains("TryGetSessionBodyPose", StringComparison.Ordinal) &&
              runtime.Contains("!TryGetInteractionBodyPose(out WorldBodyPose sessionBody)",
                  StringComparison.Ordinal) &&
              runtime.Contains("Vector3.DistanceSquared(sessionBody.Position, unit.Position)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("if (body.Length < 20)", StringComparison.Ordinal) &&
              !runtime.Contains("UI-Taxi-Icon-Yellow", StringComparison.Ordinal) &&
              !runtime.Contains("float minX=continent", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("TaxiFrameUiLaw.ShellPieces", StringComparison.Ordinal) &&
              runtime.Contains("TaxiFrameUiLaw.TitleCenter", StringComparison.Ordinal) &&
              runtime.Contains("TaxiFrameUiLaw.RouteUvA", StringComparison.Ordinal) &&
              !runtime.Contains("##taxi-content", StringComparison.Ordinal) &&
              !runtime.Contains("Current node:", StringComparison.Ordinal) &&
              !runtime.Contains("Fly to node", StringComparison.Ordinal) &&
              !runtime.Contains("_taxiStart = move.Points[0]; _taxiOpen = true",
                  StringComparison.Ordinal),
            "taxi production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
