using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Sound;
using MSUIClient.World.Wmo;

internal static class LiquidAmbientLoopClinicalChecks
{
    public static void Run()
    {
        Vector3 player = new(10f, 20f, 30f);
        Vector3 clamped = LiquidAmbientLoopLaw.NearClamp(player, player);
        Vector3 near = LiquidAmbientLoopLaw.NearClamp(player + Vector3.UnitY, player);
        Vector3 far = player + Vector3.UnitY * 8f;
        Check(Near(Vector3.Distance(clamped, player), LiquidAmbientLoopLaw.NearClampDistance) &&
              Near(Vector3.Distance(near, player), LiquidAmbientLoopLaw.NearClampDistance) &&
              LiquidAmbientLoopLaw.NearClamp(far, player) == far,
            "liquid-loop near-field clamp drifted");

        Vector3 slewed = LiquidAmbientLoopLaw.Slew(Vector3.Zero, Vector3.UnitX * 2f);
        Check(Near(slewed.X, 1f / 6f) &&
              LiquidAmbientLoopLaw.Slew(Vector3.Zero, Vector3.UnitX * .1f) ==
                  Vector3.UnitX * .1f &&
              Near(LiquidAmbientLoopLaw.FadeStep(1), .2f) &&
              LiquidAmbientLoopLaw.TriggerRadius == 9f &&
              LiquidAmbientLoopLaw.MaxConcurrent == 2,
            "liquid-loop slew, fade, radius, or concurrency law drifted");

        WmoLiquid liquid = new()
        {
            XVerts = 3,
            YVerts = 2,
            XTiles = 2,
            YTiles = 1,
            TileFlags = [0x0f, 0x06],
            VertexHeights = new float[6],
        };
        Vector3[] vertices =
        [
            new(0, 0, 1), new(1, 0, 2), new(2, 0, 3),
            new(0, 1, 4), new(1, 1, 5), new(2, 1, 6),
        ];
        WmoLiquidSurface authored = new(
            1, "fixture.wmo", 0, "pool", -10f, 0x0fu, liquid, vertices);
        WmoLiquidSurface overridden = new(
            1, "fixture.wmo", 0, "pool", -10f, 0x02u, liquid, vertices);
        Check(authored.SoundNibble == 6 && overridden.SoundNibble == 2 &&
              authored.SoundBoundsMin == new Vector2(1, 0) &&
              authored.SoundBoundsMax == new Vector2(2, 1) &&
              authored.SoundFallbackHeight == 6f,
            "WMO first-wet/group-override sound nibble or wet bounds drifted");

        CheckActualCatalog();

        string root = ClientConfig.FindRepoRoot();
        string parser = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "AdtTerrainReader.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "LiquidRenderer.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string transport = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "LiquidAmbientLoopSystem.cs"));
        Check(parser.Contains("nibble != 0x0f", StringComparison.Ordinal) &&
              parser.Contains("SoundNibble = soundNibble", StringComparison.Ordinal) &&
              renderer.Contains("NearestSoundSources", StringComparison.Ordinal) &&
              renderer.Contains("foreach (WmoLiquidSurface surface in _wmoSurfaces)",
                  StringComparison.Ordinal) &&
              runtime.Contains("LiquidAmbientLoopLaw.TriggerRadius", StringComparison.Ordinal) &&
              runtime.Contains("submerged && WmoLiquidPointLaw.IsWater(liquidType)",
                  StringComparison.Ordinal) &&
              transport.Contains("budget = LiquidAmbientLoopLaw.MaxConcurrent",
                  StringComparison.Ordinal) &&
              transport.Contains("StartWhenSilent: true", StringComparison.Ordinal),
            "liquid-loop catalog/query/transport production wiring drifted");
    }

    private static void CheckActualCatalog()
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        SoundWaterTypeCatalog catalog = SoundWaterTypeCatalog.Load(mpq) ??
            throw new InvalidDataException("SoundWaterType.dbc did not load");
        Check(catalog.Count == 12 &&
              Kit(catalog, 0) == 1111 && Kit(catalog, 4) == 1112 &&
              Kit(catalog, 8) == 1113 && Kit(catalog, 1) == 1114 &&
              Kit(catalog, 5) == 1114 && Kit(catalog, 2) == 3072 &&
              Kit(catalog, 6) == 3052 && Kit(catalog, 3) == 3880 &&
              Kit(catalog, 7) == 3880 && !catalog.TryGetKit(0x0f, out _),
            "actual build-5875 SoundWaterType mapping drifted");
    }

    private static uint Kit(SoundWaterTypeCatalog catalog, byte nibble) =>
        catalog.TryGetKit(nibble, out uint kit) ? kit : 0;

    private static bool Near(float left, float right) => MathF.Abs(left - right) < 1e-5f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
