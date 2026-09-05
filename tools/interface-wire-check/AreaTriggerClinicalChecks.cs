using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Net;

internal static class AreaTriggerClinicalChecks
{
    public static void Run()
    {
        Check(AreaTriggerLaw.Step(542, stillInsideLatched: true, firstContainingId: 527) ==
                  new AreaTriggerLatchStep(542, null) &&
              AreaTriggerLaw.Step(542, stillInsideLatched: false, firstContainingId: 527) ==
                  new AreaTriggerLatchStep(527, 527) &&
              AreaTriggerLaw.Step(542, stillInsideLatched: false, firstContainingId: null) ==
                  new AreaTriggerLatchStep(0, null) &&
              AreaTriggerLaw.Step(0, stillInsideLatched: false, firstContainingId: 708) ==
                  new AreaTriggerLatchStep(708, 708),
            "area-trigger exit latch drift");

        Check(AreaTriggerPackets.BuildReport(542).SequenceEqual(BitConverter.GetBytes(542u)),
            "CMSG_AREATRIGGER body drift");
        const string refusal = "You must be at least level 58 to enter.";
        var writer = new PacketWriter();
        writer.WriteU32((uint)refusal.Length + 1);
        writer.WriteCString(refusal);
        Check(AreaTriggerPackets.ParseMessage(writer.ToArray()) == refusal,
            "SMSG_AREA_TRIGGER_MESSAGE parser drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Instances.cs"));
        string handler = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(runtime.Contains("AreaTriggerLaw.Step", StringComparison.Ordinal) &&
              runtime.Contains("_net?.AreaTrigger((uint)reportId)", StringComparison.Ordinal) &&
              runtime.Contains("_areaTriggers is null || !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", StringComparison.Ordinal) &&
              handler.Contains("AreaTriggerPackets.ParseMessage", StringComparison.Ordinal) &&
              handler.Contains("ShowUiError(text)", StringComparison.Ordinal),
            "area-trigger production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
