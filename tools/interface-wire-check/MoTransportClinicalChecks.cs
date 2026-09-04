using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;

internal static class MoTransportClinicalChecks
{
    private readonly record struct Golden(uint PathId, uint PeriodMs,
        float Speed = 30f, float Acceleration = 1f);

    private static readonly Golden[] Goldens =
    [
        new(241, 350_818),
        new(302, 356_284),
        new(285, 303_463),
        new(292, 329_313),
        new(293, 316_251),
        new(295, 295_579),
        new(301, 333_044),
        new(303, 317_040),
        new(436, 1_208_014, 1f, 1f),
    ];

    public static void Run()
    {
        CheckActualCatalog();

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.Transports.cs"));
        string entity = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "Entities.cs"));
        string render = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.GameObjectRender.cs"));
        Check(runtime.Contains("TaxiPathNodeCatalog", StringComparison.Ordinal) &&
              runtime.Contains("template.Type == 15", StringComparison.Ordinal) &&
              runtime.Contains("MoTransportTimetable", StringComparison.Ordinal) &&
              runtime.Contains("sample.MapId != unchecked((uint)Math.Max(0, _config.Start.Map))",
                  StringComparison.Ordinal) &&
              entity.Contains("TransportFacingOverride", StringComparison.Ordinal) &&
              render.Contains("_offMapTransports.Contains(e.Guid)", StringComparison.Ordinal),
            "type-15 timetable/map/facing production wiring drifted");
    }

    private static void CheckActualCatalog()
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        TaxiPathNodeCatalog catalog = TaxiPathNodeCatalog.Load(mpq) ??
            throw new InvalidDataException("TaxiPathNode.dbc did not load");
        Check(catalog.Count > 100 && catalog.TryGet(302, out TaxiPathNode[] path302) &&
              path302.Length == 36 && path302.Zip(path302.Skip(1)).All(pair =>
                  pair.First.NodeIndex < pair.Second.NodeIndex) &&
              path302.Select(node => node.MapId).Distinct().Order().SequenceEqual([0u, 1u]) &&
              path302.Count(node => node.Flags == 2) == 2 &&
              path302.Where(node => node.Flags == 2).All(node => node.DelaySeconds == 60) &&
              catalog.TryGet(285, out TaxiPathNode[] path285) && path285.Length == 27 &&
              path285.Where(node => node.Flags == 2).Select(node => node.NodeIndex)
                  .SequenceEqual([4u, 20u]),
            "actual build-5875 TaxiPathNode catalog layout drifted");

        foreach (Golden golden in Goldens)
        {
            Check(catalog.TryGet(golden.PathId, out TaxiPathNode[] nodes),
                $"actual path {golden.PathId} is missing");
            uint? period = MoTransportTimetable.ClientPeriodMs(nodes,
                golden.Speed, golden.Acceleration);
            Check(period == golden.PeriodMs,
                $"path {golden.PathId} client period was {period}, expected {golden.PeriodMs}");
            MoTransportTimetable timetable = MoTransportTimetable.Build(nodes,
                golden.Speed, golden.Acceleration) ??
                throw new InvalidDataException($"path {golden.PathId} did not build");
            Check(timetable.PeriodMs == golden.PeriodMs &&
                  nodes.Select(node => node.MapId).Distinct().All(timetable.TouchesMap),
                $"path {golden.PathId} timetable period/map coverage drifted");
            MoTransportSample first = timetable.Sample(0);
            MoTransportSample wrapped = timetable.Sample(golden.PeriodMs);
            Check(first.MapId == wrapped.MapId && first.Position == wrapped.Position &&
                  first.Heading == wrapped.Heading && first.Moving == wrapped.Moving,
                $"path {golden.PathId} timetable does not wrap exactly");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
