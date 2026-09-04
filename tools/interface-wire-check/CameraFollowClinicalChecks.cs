using MSUIClient;
using MSUIClient.Engine;

internal static class CameraFollowClinicalChecks
{
    private const float Dt = 1f / 120f;
    private const float Offset = MathF.PI * .5f;

    public static void Run()
    {
        Check(CameraFollowLaw.FromStoredValue(0) == CameraFollowStyle.Never &&
              CameraFollowLaw.FromStoredValue(1) == CameraFollowStyle.Smart &&
              CameraFollowLaw.FromStoredValue(2) == CameraFollowStyle.Always &&
              CameraFollowLaw.FromStoredValue(3) == CameraFollowStyle.Never &&
              CameraFollowLaw.FromStoredValue(99) == CameraFollowStyle.Smart &&
              CameraFollowLaw.DisplayOrder.SequenceEqual(new[]
              {
                  CameraFollowStyle.Smart, CameraFollowStyle.Always, CameraFollowStyle.Never,
              }),
            "camera follow engine values or reference dropdown order drift");

        Check(CameraFollowLaw.State(0, false) == CameraFollowState.Idle &&
              CameraFollowLaw.State(0, true) == CameraFollowState.Stop &&
              CameraFollowLaw.State(CameraFollowCommand.RightMouse, false) == CameraFollowState.Turn &&
              CameraFollowLaw.State(CameraFollowCommand.RightMouse | CameraFollowCommand.TurnLeft,
                  false) == CameraFollowState.Turn &&
              CameraFollowLaw.State(CameraFollowCommand.StrafeLeft, false) == CameraFollowState.Strafe &&
              CameraFollowLaw.State(CameraFollowCommand.RightMouse | CameraFollowCommand.LeftMouse,
                  false) == CameraFollowState.Turn &&
              CameraFollowLaw.State(CameraFollowCommand.LeftMouse, false) == CameraFollowState.Idle &&
              CameraFollowLaw.State(CameraFollowCommand.Autorun, false) == CameraFollowState.Move &&
              CameraFollowLaw.State(CameraFollowCommand.Track, false) == CameraFollowState.Track &&
              CameraFollowLaw.State(CameraFollowCommand.Track | CameraFollowCommand.Forward,
                  false) == CameraFollowState.Move &&
              CameraFollowLaw.State(CameraFollowCommand.Fear | CameraFollowCommand.Forward,
                  false) == CameraFollowState.Fear,
            "camera follow command-word priority classifier drift");

        var mixed = new CameraFollowInput(
            new CameraFollowConfig(CameraFollowStyle.Never, CameraFollowStyle.Always, 180f),
            0f, CameraFollowCommand.Track | CameraFollowCommand.Forward);
        Check(CameraFollowLaw.Style(mixed) == CameraFollowStyle.Always &&
              CameraFollowLaw.State(mixed.Command, false) == CameraFollowState.Move,
            "tracking selector must win when Track/Fear is merely present");

        CameraFollowConfig smart = new(CameraFollowStyle.Smart, CameraFollowStyle.Smart, 180f);
        CameraFollowConfig always = new(CameraFollowStyle.Always, CameraFollowStyle.Always, 180f);
        CameraFollowConfig never = new(CameraFollowStyle.Never, CameraFollowStyle.Never, 180f);

        var rig = new CameraFollowController();
        float parked = Run(rig, smart, 0, Offset, 1f);
        Check(parked == Offset, "Smart must leave a standing camera where the hand placed it");
        float halfway = Run(rig, smart, CameraFollowCommand.Forward, parked, .25f);
        Check(halfway < Offset * .75f && halfway > Offset * .25f,
            "Smart movement edge did not start the cosine-smoothed return");
        float home = Run(rig, smart, CameraFollowCommand.Forward, halfway, .3f);
        Check(MathF.Abs(home) < .0001f, "Smart movement edge did not finish directly behind");
        Check(Run(rig, smart, CameraFollowCommand.Forward, home + .4f, 1f) == home + .4f,
            "held command re-armed an edge-owned camera return");

        rig = new CameraFollowController();
        float held = Run(rig, smart, CameraFollowCommand.Forward, 0f, .1f);
        float released = Run(rig, smart, 0, held + Offset, .5f);
        Check(released == held + Offset, "Smart Stop/Idle rows must cancel, not return");

        rig = new CameraFollowController();
        held = Run(rig, always, CameraFollowCommand.Forward, 0f, .1f);
        Check(MathF.Abs(Run(rig, always, 0, held + Offset, 1f)) < .0001f,
            "Always must return on the same standing edge Smart cancels");

        rig = new CameraFollowController();
        _ = Run(rig, never, 0, Offset, .1f);
        Check(Run(rig, never, CameraFollowCommand.Forward, Offset, 2f) == Offset,
            "Never must remain inert on every command edge");

        rig = new CameraFollowController();
        float small = 5f * MathF.PI / 180f;
        _ = Run(rig, smart, 0, small, Dt);
        float smallHalf = Run(rig, smart, CameraFollowCommand.Forward, small, .05f);
        Check(MathF.Abs(smallHalf) > .0001f &&
              MathF.Abs(Run(rig, smart, CameraFollowCommand.Forward, smallHalf, .06f)) < .0001f,
            "camera follow lost the 0.1-second duration floor");

        rig = new CameraFollowController();
        _ = Run(rig, smart, 0, Offset, Dt);
        float delayed = Run(rig, smart, CameraFollowCommand.Track, Offset, .3f);
        float crawling = Run(rig, smart, CameraFollowCommand.Track, delayed, .6f);
        Check(delayed == Offset && crawling > Offset * .5f &&
              MathF.Abs(Run(rig, smart, CameraFollowCommand.Track, crawling, 2f)) < .0001f,
            "Smart Track/Fear delayed factor-10 return or 2-second cap drift");

        rig = new CameraFollowController();
        _ = Run(rig, smart, 0, Offset, Dt);
        float inFlight = Run(rig, smart, CameraFollowCommand.Forward, Offset, .1f);
        var dragging = new CameraFollowInput(smart, 0f,
            CameraFollowCommand.Forward | CameraFollowCommand.LeftMouse);
        Check(rig.Advance(dragging, inFlight, Dt, lookHeld: true) is null && !rig.Armed,
            "entering mouse-look must cancel the active return");
        float afterRelease = Run(rig, smart, CameraFollowCommand.Forward, inFlight, 1f);
        Check(MathF.Abs(afterRelease) < .0001f,
            "mouse-release command edge did not arm a fresh return");

        CheckRuntimeWiring();
        CheckSettingsMigration();
    }

    private static float Run(CameraFollowController rig, CameraFollowConfig config,
        uint command, float viewYaw, float seconds)
    {
        float yaw = viewYaw;
        int frames = Math.Max((int)MathF.Round(seconds / Dt), 0);
        for (int i = 0; i < frames; i++)
        {
            var input = new CameraFollowInput(config, 0f, command);
            if (rig.Advance(input, yaw, Dt, lookHeld: false) is float next) yaw = next;
        }
        return yaw;
    }

    private static void CheckRuntimeWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string camera = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "Camera.cs"));
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        string search = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "OptionsSearchUiLaw.cs"));
        Check(camera.Contains("_follow.Advance(input, ViewYaw, dt, lookHeld)",
                  StringComparison.Ordinal) &&
              !camera.Contains("EaseOrbitBehind", StringComparison.Ordinal) &&
              host.Contains("CameraFollowCommand.Track", StringComparison.Ordinal) &&
              host.Contains("CameraFollowCommand.Fear", StringComparison.Ordinal) &&
              host.Contains("_window.Camera.AuthoredTarget is not null", StringComparison.Ordinal) &&
              host.Contains("_window.Camera.ResetFollow();", StringComparison.Ordinal) &&
              !host.Contains("Camera.EaseOrbitBehind", StringComparison.Ordinal),
            "camera follow runtime edge word, far-sight, free-view, or legacy chase drift");
        Check(settings.Contains("CameraFollowStyleRow(s.Controls);", StringComparison.Ordinal) &&
              settings.Contains("CameraFollowLaw.DisplayOrder", StringComparison.Ordinal) &&
              search.Contains("\"Camera Following Style\"", StringComparison.Ordinal),
            "Camera Following Style Options row/search seam drift");
    }

    private static void CheckSettingsMigration()
    {
        string root = ClientConfig.FindRepoRoot();
        string path = Path.Combine(Path.GetTempPath(), $"msui-camera-follow-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\"Settings\":{\"Version\":9,\"Controls\":{}},\"Presets\":[]}");
            SettingsStore migrated = SettingsStore.Load(root, path);
            // Pinned to the highest migration step (v12, HudLayoutLaw.Migrate11To12). The point
            // of loading a v9 file is to prove the v10 camera seeding still survives the whole
            // chain, so a new step should force that to be re-confirmed rather than pass by >=.
            Check(migrated.Settings.Version == 12 &&
                  migrated.Settings.Controls.CameraFollowStyle == CameraFollowStyle.Smart &&
                  migrated.Settings.Controls.CameraFollowTrackingStyle == CameraFollowStyle.Smart &&
                  migrated.Settings.Controls.CameraFollowYawSpeed ==
                      CameraFollowLaw.DefaultYawSpeedDegrees,
                "v10 camera follow migration did not seed the current/reference defaults");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
