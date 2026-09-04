using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class BinderClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_BINDER_ACTIVATE == 0x01B5 &&
              (ushort)Op.SMSG_PLAYERBINDERROR == 0x01B6 &&
              (ushort)Op.SMSG_BINDER_CONFIRM == 0x02EB &&
              (ushort)Op.SMSG_BINDPOINTUPDATE == 0x0155 &&
              (ushort)Op.SMSG_PLAYERBOUND == 0x0158,
            "binder opcode drift");

        var confirmWriter = new PacketWriter();
        confirmWriter.WriteU64(0xF130000127000001);
        Check(BinderPackets.ParseConfirm(confirmWriter.ToArray()).BinderGuid ==
              0xF130000127000001, "binder confirm body drift");

        var boundWriter = new PacketWriter();
        boundWriter.WriteU64(0xF130000127000001);
        boundWriter.WriteU32(1519);
        PlayerBoundPacket bound = BinderPackets.ParsePlayerBound(boundWriter.ToArray());
        Check(bound.BinderGuid == 0xF130000127000001 && bound.AreaId == 1519,
            "player-bound body drift");

        var pointWriter = new PacketWriter();
        pointWriter.WriteF32(-9464.5f);
        pointWriter.WriteF32(62.1f);
        pointWriter.WriteF32(56f);
        pointWriter.WriteU32(0);
        // SMSG_BINDPOINTUPDATE carries a trailing AreaTable id after mapId (vmangos
        // BindpointUpdate::AppendBodyTo). It was missing from the packet record, so
        // RequireConsumed threw "4 trailing byte(s)" on every login and the bind point was
        // never stored (reported 2026-09-01). The record gained AreaId; this fixture never
        // did, so it fed a 16-byte body to a 20-byte parse.
        pointWriter.WriteU32(1519);
        BindPointPacket point = BinderPackets.ParseBindPoint(pointWriter.ToArray());
        Check(point.Position == new Vector3(-9464.5f, 62.1f, 56f) && point.MapId == 0 &&
              point.AreaId == 1519,
            "bind-point body drift");

        Check(BinderConfirmUiLaw.Prompt("Stormwind City") ==
              "Do you want to make Stormwind City your new home?" &&
              BinderConfirmUiLaw.Prompt("") ==
              "Do you want to make your inn your new home?" &&
              BinderConfirmUiLaw.PlayerBoundText("Goldshire") ==
              "Goldshire is now your home." &&
              BinderConfirmUiLaw.PlayerBoundText("") is null,
            "binder text drift");

        BinderConfirmUiLaw.ScreenRect rect = BinderConfirmUiLaw.PopupRect(
            new Vector2(1920, 1080), 1.5f, 14f);
        Check(rect.Min == new Vector2(720, 192) && rect.Size.X == 480 &&
              BinderConfirmUiLaw.ButtonMin(1, 14f) == new Vector2(26, 38) &&
              BinderConfirmUiLaw.ButtonMin(2, 14f) == new Vector2(167, 38) &&
              BinderConfirmUiLaw.ButtonSize(1.5f) == new Vector2(192, 30) &&
              BinderConfirmUiLaw.ButtonUvMax == new Vector2(1f, .625f),
            "binder StaticPopup geometry drift");

        Check(BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, false,
                  BinderConfirmUiLaw.ServiceRange) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, false,
                  MathF.BitIncrement(BinderConfirmUiLaw.ServiceRange)) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, false, true, false, 0) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, true, 0),
            "binder service-range lifetime drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Hearth.cs"));
        Check(runtime.Contains("BinderConfirmUiLaw.PopupRect", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonMin", StringComparison.Ordinal) &&
              !runtime.Contains("DrawHearthFrame", StringComparison.Ordinal) &&
              !runtime.Contains("Inn & Hearthstone", StringComparison.Ordinal) &&
              !runtime.Contains("ImGuiCond.FirstUseEver", StringComparison.Ordinal) &&
              !runtime.Contains("logicalDisplay", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonSize", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonUvMax", StringComparison.Ordinal),
            "binder renderer bypasses its modal law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
