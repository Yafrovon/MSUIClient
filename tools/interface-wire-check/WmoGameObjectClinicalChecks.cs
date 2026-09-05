using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Wmo;

internal static class WmoGameObjectClinicalChecks
{
    public static void Run()
    {
        Matrix4x4 transform = WmoRenderer.DynamicGameObjectTransform(
            new Vector3(100, 200, 30), MathF.PI / 2f, 2f);
        Vector3 origin = Vector3.Transform(Vector3.Zero, transform);
        Vector3 x = Vector3.Transform(Vector3.UnitX, transform);
        Vector3 y = Vector3.Transform(Vector3.UnitY, transform);
        Check(Vector3.DistanceSquared(origin, new Vector3(100, 200, 30)) < 1e-8f &&
              Vector3.DistanceSquared(x, new Vector3(100, 202, 30)) < 1e-8f &&
              Vector3.DistanceSquared(y, new Vector3(98, 200, 30)) < 1e-8f,
            "dynamic WMO GameObject world position/yaw/scale law drifted");

        CheckActualDisplay();

        string root = ClientConfig.FindRepoRoot();
        string render = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.GameObjectRender.cs"));
        string wmo = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Wmo",
            "WmoRenderer.cs"));
        Check(render.Contains("WmoRenderer.DynamicGameObjectTransform", StringComparison.Ordinal) &&
              render.Contains("_wmo?.TryUpdateDynamicTransform", StringComparison.Ordinal) &&
              render.Contains("_wmo?.AddDynamic", StringComparison.Ordinal) &&
              render.Contains("_wmo?.TryPickDynamic", StringComparison.Ordinal) &&
              render.Contains("_wmo?.RemoveDynamic", StringComparison.Ordinal) &&
              render.Contains("SyncDynamicWmoGameObjectProps", StringComparison.Ordinal) &&
              render.Contains("AddDynamicWmoProp", StringComparison.Ordinal) &&
              wmo.Contains("Dictionary<ulong, Instance> _dynamicByGuid", StringComparison.Ordinal) &&
              wmo.Contains("EnumerateDynamicDoodads", StringComparison.Ordinal) &&
              wmo.Contains("if (instance.DynamicGuid != 0) continue;", StringComparison.Ordinal) &&
              wmo.Contains("AppearStart = 0f", StringComparison.Ordinal),
            "dynamic WMO GameObject render/update/pick/lifecycle wiring drifted");
    }

    private static void CheckActualDisplay()
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        byte[] bytes = mpq.ReadFile(GameObjectDisplayTable.MpqPath) ??
            throw new InvalidDataException("GameObjectDisplayInfo.dbc did not load");
        GameObjectDisplayTable table = GameObjectDisplayTable.Parse(bytes) ??
            throw new InvalidDataException("GameObjectDisplayInfo.dbc did not parse");
        string path = table.ModelPath(3015) ?? "";
        Check(path.EndsWith("transportship.wmo", StringComparison.OrdinalIgnoreCase),
            $"actual transport display 3015 was not the stock ship WMO: '{path}'");
        byte[] rootBytes = mpq.ReadFile(path) ??
            throw new InvalidDataException($"stock ship WMO did not load: '{path}'");
        WmoRootData root = WmoReader.ParseRoot(rootBytes) ??
            throw new InvalidDataException("stock ship WMO root did not parse");
        Check(root.DoodadSets.Count == 1 && root.Doodads.Count == 134 &&
              root.DoodadSets[0].DoodadCount == 134,
            $"stock ship set-0 prop census drifted: sets={root.DoodadSets.Count}, " +
            $"props={root.Doodads.Count}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
