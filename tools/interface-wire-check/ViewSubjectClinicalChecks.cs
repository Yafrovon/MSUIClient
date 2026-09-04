using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Net;

internal static class ViewSubjectClinicalChecks
{
    public static void Run()
    {
        float character = ViewSubjectLaw.PivotHeight(1.0f, -1f, 9f, 2f);
        float fallback = ViewSubjectLaw.PivotHeight(null, -0.5f, 1.5f, 1f);
        ViewSubjectLaw.PlayerFarSightOwnership entered =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                freeView: true, awaitingFreeViewClear: false, anchor: 0);
        ViewSubjectLaw.PlayerFarSightOwnership streaming =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                freeView: true, entered.AwaitClear, anchor: 0x1234);
        ViewSubjectLaw.PlayerFarSightOwnership landedStale =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                freeView: false, streaming.AwaitClear, anchor: 0x1234);
        ViewSubjectLaw.PlayerFarSightOwnership cleared =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                freeView: false, landedStale.AwaitClear, anchor: 0);
        ViewSubjectLaw.PlayerFarSightOwnership ordinary =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                freeView: false, cleared.AwaitClear, anchor: 0x5678);
        Check(MathF.Abs(character - 2.1944f) < .00001f &&
              MathF.Abs(fallback - 1.8f) < .00001f &&
              ViewSubjectLaw.PivotHeight(0f, 0f, 0f, 0f) == ViewSubjectLaw.PivotFloor &&
              ViewSubjectLaw.EyeTarget(new Vector3(100, 20, 3), 2) ==
                  new Vector3(100, 20, 5) &&
              !entered.MayOwnCamera && entered.AwaitClear &&
              !streaming.MayOwnCamera && streaming.AwaitClear &&
              !landedStale.MayOwnCamera && landedStale.AwaitClear &&
              cleared.MayOwnCamera && !cleared.AwaitClear &&
              ordinary.MayOwnCamera && !ordinary.AwaitClear &&
              ViewSubjectLaw.VoteBody(true).SequenceEqual(new byte[] { 1 }) &&
              ViewSubjectLaw.VoteBody(false).SequenceEqual(new byte[] { 0 }),
            "far-sight pivot/target/free-view hand-off/vote law drift");

        var created = new ObjectFields().AsCreated();
        created.SetGuid(ObjectFields.PLAYER_FARSIGHT, 0x0000567800001234ul);
        Check(created.PlayerFarsight == 0x0000567800001234ul &&
              ObjectFields.PLAYER_FARSIGHT == ViewSubjectLaw.PlayerFarsightField &&
              (ushort)Op.CMSG_FAR_SIGHT == 0x027A,
            "PLAYER_FARSIGHT descriptor or CMSG_FAR_SIGHT opcode drift");

        string root = ClientConfig.FindRepoRoot();
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.ViewSubject.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string sound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        int authoredTargetClear = host.IndexOf("camera.AuthoredTarget = null",
            StringComparison.Ordinal);
        int freeViewFence = host.IndexOf(
            "if (!ownership.MayOwnCamera) return;",
            StringComparison.Ordinal);
        int farSightVote = host.IndexOf("_net?.FarSight(anchor != 0)",
            StringComparison.Ordinal);
        Check(host.Contains("_entities.TryGet(net.PlayerGuid", StringComparison.Ordinal) &&
              !host.Contains("ControlledGuid", StringComparison.Ordinal) &&
              authoredTargetClear >= 0 && freeViewFence > authoredTargetClear &&
              farSightVote > freeViewFence &&
              host.Contains("_farSightAwaitingFreeViewClear = ownership.AwaitClear",
                  StringComparison.Ordinal) &&
              host.Contains("_entities.TryGet(anchor", StringComparison.Ordinal) &&
              program.Contains(": _controller.Position;\n        UpdateViewSubject();",
                  StringComparison.Ordinal) &&
              sound.Contains("SpatialAudioLaw.CharacterListener(_controller.Position)",
                  StringComparison.Ordinal),
            "far-sight local-owner/free-view hand-off/edge-vote/body-fallback/character-audio wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
