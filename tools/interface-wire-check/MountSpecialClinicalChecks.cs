using MSUIClient;
using MSUIClient.Net;

internal static class MountSpecialClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_MOUNTSPECIAL_ANIM == 0x0171 &&
              (ushort)Op.SMSG_MOUNTSPECIAL_ANIM == 0x0172 &&
              MountSpecialPackets.ParseGuid([0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11]) ==
                  0x1122_3344_5566_7788UL,
            "mount-special opcodes or raw-u64 SMSG body drift");
        CheckThrows(() => MountSpecialPackets.ParseGuid(new byte[7]));
        CheckThrows(() => MountSpecialPackets.ParseGuid(new byte[9]));

        string root = ClientConfig.FindRepoRoot();
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldSession.cs"));
        string input = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string mount = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.Mounts.cs"));
        Check(session.Contains("CMSG_MOUNTSPECIAL_ANIM, ReadOnlySpan<byte>.Empty", StringComparison.Ordinal) &&
              input.Contains("!translating && _controller.Grounded", StringComparison.Ordinal) &&
              input.Contains("_creatures?.TriggerMountFlourish(LocalPlayerGuid)", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_MOUNTSPECIAL_ANIM:", StringComparison.Ordinal) &&
              dispatch.Contains("if (rider != LocalPlayerGuid || ControlledBodyIsStreamed)", StringComparison.Ordinal) &&
              mount.Contains("BaseAnimationTrack, 94, true", StringComparison.Ordinal),
            "mounted key gate, local animation, empty send, or observer receive is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed mount-special body was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
