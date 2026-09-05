using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class ElevatorTransportClinicalChecks
{
    public static void Run()
    {
        ElevatorKeyframe[] frames =
        [
            new(0, Vector3.Zero),
            new(5_000, Vector3.Zero),
            new(10_000, new Vector3(0, 0, -10)),
            new(30_000, Vector3.Zero),
        ];
        Vector3 spawn = new(100, 200, 50);
        ElevatorTransportLaw.Sample dwell = ElevatorTransportLaw.Evaluate(
            frames, spawn, Quaternion.Identity, 2_500);
        ElevatorTransportLaw.Sample descent = ElevatorTransportLaw.Evaluate(
            frames, spawn, Quaternion.Identity, 7_500);
        ElevatorTransportLaw.Sample wrapped = ElevatorTransportLaw.Evaluate(
            frames, spawn, Quaternion.Identity, 30_000);
        ElevatorTransportLaw.Sample almost = ElevatorTransportLaw.Evaluate(
            frames, spawn, Quaternion.Identity, 29_999);
        Check(dwell.Position == spawn && !dwell.Moving &&
              descent.Position == new Vector3(100, 200, 45) && descent.Moving &&
              wrapped.Position == spawn && almost.Moving &&
              MathF.Abs(almost.Position.Z - 50f) < .01f &&
              ElevatorTransportLaw.Period(frames) == 30_000,
            "type-11 dwell/lerp/wrap/period law drifted");

        float half = MathF.PI / 4f;
        Quaternion quarterTurn = new(0, 0, MathF.Sin(half), MathF.Cos(half));
        ElevatorKeyframe[] lateral =
        [
            new(0, new Vector3(10, 0, 0)),
            new(1_000, new Vector3(10, 0, 0)),
        ];
        Vector3 rotated = ElevatorTransportLaw.Evaluate(
            lateral, Vector3.Zero, quarterTurn, 500).Position;
        Check(MathF.Abs(rotated.X) < 1e-4f && MathF.Abs(rotated.Y - 10f) < 1e-4f,
            "type-11 spawn quaternion did not rotate the local offset");

        TransportRiderLaw.WorldPose rider = TransportRiderLaw.Compose(
            new Vector3(100, 200, 30), MathF.PI / 2f,
            new Vector3(4, 2, 3), MathF.PI * 1.75f);
        Check(Vector3.DistanceSquared(rider.Position, new Vector3(98, 204, 33)) < 1e-8f &&
              MathF.Abs(rider.Orientation - MathF.PI * .25f) < 1e-5f,
            "observed rider local-position/facing composition drifted");

        CheckActualCatalog();

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Transports.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string render = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectRender.cs"));
        string doodads = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Doodads",
            "DoodadRenderer.cs"));
        Check(runtime.Contains("go.TransportProgress is not uint progress",
                  StringComparison.Ordinal) &&
              runtime.Contains("template.Type == 11", StringComparison.Ordinal) &&
              runtime.Contains("ReferenceEquals(state.Entity, go)", StringComparison.Ordinal) &&
              runtime.Contains("state.SpawnPosition, state.SpawnRotation",
                  StringComparison.Ordinal) &&
              runtime.Contains("rider.Guid == ControlledGuid", StringComparison.Ordinal) &&
              runtime.Contains("_elevatorTransports.ContainsKey(local.Guid)",
                  StringComparison.Ordinal) &&
              runtime.Contains("TransportRiderLaw.Compose", StringComparison.Ordinal) &&
              runtime.Contains("_doodads?.TryRaycastDynamicCollision", StringComparison.Ordinal) &&
              runtime.Contains("_doodads?.SetDynamicCollisionLive(go.Guid)",
                  StringComparison.Ordinal) &&
              program.Contains("UpdateGameObjectTransports();", StringComparison.Ordinal) &&
              program.IndexOf("UpdateGameObjectTransports();", StringComparison.Ordinal) <
              program.IndexOf("UpdateGameObjectDoodads();", StringComparison.Ordinal) &&
              render.Contains("TryUpdateDynamicTransform(e.Guid, transform)",
                  StringComparison.Ordinal) &&
              render.Contains("liveCollision: _elevatorTransports.ContainsKey(e.Guid)",
                  StringComparison.Ordinal) &&
              doodads.Contains("TryRaycastDynamicCollision", StringComparison.Ordinal) &&
              doodads.Contains("if (instance.LiveCollision) continue;",
                  StringComparison.Ordinal) &&
              doodads.Contains("entry.Instance.Transform = transform;", StringComparison.Ordinal),
            "type-11 anchor/re-create/render-order production wiring drifted");
    }

    private static void CheckActualCatalog()
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        TransportAnimationCatalog catalog = TransportAnimationCatalog.Load(mpq) ??
            throw new InvalidDataException("TransportAnimation.dbc did not load");
        Check(catalog.TryGet(4170, out ElevatorKeyframe[] top) &&
              ElevatorTransportLaw.Period(top) == 30_033 && top[0].TimeMs == 0 &&
              top[0].LocalPosition == Vector3.Zero &&
              top.Zip(top.Skip(1)).All(pair => pair.First.TimeMs <= pair.Second.TimeMs) &&
              top.All(frame => MathF.Abs(frame.LocalPosition.X) < .001f &&
                               MathF.Abs(frame.LocalPosition.Y) < .001f) &&
              MathF.Abs(top.Min(frame => frame.LocalPosition.Z) + 61.244f) < .001f &&
              catalog.TryGet(4171, out ElevatorKeyframe[] bottom) &&
              ElevatorTransportLaw.Period(bottom) == 30_000 &&
              catalog.TryGet(152614, out _) && !catalog.TryGet(999_999, out _),
            "actual build-5875 Mesa/Undercity lift catalog drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
