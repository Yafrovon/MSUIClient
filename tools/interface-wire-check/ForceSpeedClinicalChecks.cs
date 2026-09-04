using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class ForceSpeedClinicalChecks
{
    public static void Run()
    {
        byte[] serverBody = Convert.FromHexString("01080700000000006041");
        ForceSpeedChange change = MovementSpeedPackets.ParseForceSpeedChange(
            Op.SMSG_FORCE_RUN_SPEED_CHANGE, serverBody);
        Check(change == new ForceSpeedChange(8, MovementSpeedKind.Run, 7, 14f),
            "force-run speed-change parse drift");

        Check(MovementSpeedPackets.AckOpcode(MovementSpeedKind.Walk) ==
                  Op.CMSG_FORCE_WALK_SPEED_CHANGE_ACK &&
              MovementSpeedPackets.AckOpcode(MovementSpeedKind.Run) ==
                  Op.CMSG_FORCE_RUN_SPEED_CHANGE_ACK &&
              MovementSpeedPackets.AckOpcode(MovementSpeedKind.RunBack) ==
                  Op.CMSG_FORCE_RUN_BACK_SPEED_CHANGE_ACK &&
              MovementSpeedPackets.AckOpcode(MovementSpeedKind.Swim) ==
                  Op.CMSG_FORCE_SWIM_SPEED_CHANGE_ACK &&
              MovementSpeedPackets.AckOpcode(MovementSpeedKind.SwimBack) ==
                  Op.CMSG_FORCE_SWIM_BACK_SPEED_CHANGE_ACK &&
              MovementSpeedPackets.AckOpcode(MovementSpeedKind.TurnRate) ==
                  Op.CMSG_FORCE_TURN_RATE_CHANGE_ACK,
            "force-speed kind/opcode map drift");

        var info = new MovementInfo
        {
            Flags = 0,
            Timestamp = 12345,
            Position = new Vector3(1, 2, 3),
            Orientation = 0.5f,
            FallTime = 42,
        };
        byte[] ack = WorldSession.BuildForceSpeedChangeAckBody(8, 7, info, 14f);
        Check(ack.SequenceEqual(Convert.FromHexString(
                "08000000000000000700000000000000393000000000803f0000004000004040" +
                "0000003f2a00000000006041")),
            "force-speed ack full-guid/counter/MovementInfo/speed layout drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string apply = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MovementSpeed.cs"));
        string controller = SourceText.Read(Path.Combine(root, "MSUIClient", "Player",
            "CharacterController.cs"));
        Check(dispatch.Contains("case Op.SMSG_FORCE_TURN_RATE_CHANGE", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyForceSpeedChange(net, (Op)opcode, body)",
                  StringComparison.Ordinal) &&
              apply.Contains("TrySnapshotMovementAck(", StringComparison.Ordinal) &&
              apply.Contains("SyncControlledSpeeds", StringComparison.Ordinal) &&
              controller.Contains("EffectiveRunBackSpeed", StringComparison.Ordinal),
            "force-speed receive/apply/ack wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
