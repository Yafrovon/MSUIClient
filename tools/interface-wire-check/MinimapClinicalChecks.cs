using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class MinimapClinicalChecks
{
    public static void Run()
    {
        MinimapZonePvpInfo friendly = MinimapUiLaw.ZonePvp(0, 2, 2, 4);
        MinimapZonePvpInfo hostile = MinimapUiLaw.ZonePvp(0, 4, 2, 4);
        MinimapZonePvpInfo contestedArena = MinimapUiLaw.ZonePvp(0x80, 0, 2, 4);
        MinimapZonePvpInfo unknown = MinimapUiLaw.ZonePvp(0, 0, 0, 0, inputsKnown: false);
        Check(friendly.Type == MinimapZonePvpType.Friendly &&
              friendly.FactionName == "Alliance" &&
              friendly.TerritoryLine == "Alliance Territory" &&
              friendly.Tint == new Vector4(.1f, 1f, .1f, 1f) &&
              hostile.Type == MinimapZonePvpType.Hostile &&
              hostile.FactionName == "Horde" &&
              hostile.Tint == new Vector4(1f, .1f, .1f, 1f) &&
              contestedArena.Type == MinimapZonePvpType.Contested &&
              contestedArena.TerritoryLine == "Contested Territory" &&
              contestedArena.IsArena &&
              unknown.Type == MinimapZonePvpType.Unknown && unknown.TerritoryLine is null,
            "minimap PvP territory/tint law drift");

        Check(MinimapUiLaw.ZoomInEnabled(0) && !MinimapUiLaw.ZoomInEnabled(5) &&
              !MinimapUiLaw.ZoomOutEnabled(0) && MinimapUiLaw.ZoomOutEnabled(5) &&
              MinimapUiLaw.StepZoom(2, zoomIn: true) == 3 &&
              MinimapUiLaw.StepZoom(2, zoomIn: false) == 1 &&
              MinimapUiLaw.StepZoom(5, zoomIn: true) == 5 &&
              MinimapUiLaw.StepZoom(0, zoomIn: false) == 0 &&
              MathF.Abs(MinimapUiLaw.OutdoorRadius(0) - 233.33333f) < .001f &&
              MathF.Abs(MinimapUiLaw.OutdoorRadius(2) - 166.66667f) < .001f &&
              MathF.Abs(MinimapUiLaw.OutdoorRadius(5) - 66.66667f) < .001f &&
              MinimapUiLaw.OpenSound == "igMiniMapOpen" &&
              MinimapUiLaw.CloseSound == "igMiniMapClose" &&
              MinimapUiLaw.ZoomInSound == "igMiniMapZoomIn" &&
              MinimapUiLaw.ZoomOutSound == "igMiniMapZoomOut" &&
              MinimapUiLaw.UnreadMailText == "You have unread mail" &&
              MinimapUiLaw.ArenaText == "PvP Area" &&
              MinimapUiLaw.ShowQuestDot(7) && !MinimapUiLaw.ShowQuestDot(6) &&
              MinimapUiLaw.QuestDotUvMin == new Vector2(.75f, 0f) &&
              MinimapUiLaw.QuestDotUvMax == new Vector2(1f, .25f) &&
              MinimapUiLaw.TrackedCreatureDotUvMin == new Vector2(.25f, 0f) &&
              MinimapUiLaw.TrackedCreatureDotUvMax == new Vector2(.5f, .25f) &&
              MinimapUiLaw.PartyDotUvMin == new Vector2(0f, .25f) &&
              MinimapUiLaw.PartyDotUvMax == new Vector2(.25f, .5f) &&
              MinimapUiLaw.ShowTrackedCreatureDot(1u << 6, 7, 0) &&
              !MinimapUiLaw.ShowTrackedCreatureDot(1u << 5, 7, 0) &&
              MinimapUiLaw.ShowTrackedCreatureDot(0, 0, 0x2) &&
              MinimapUiLaw.BlipTint(false, true) == 0xffb0b0b0 &&
              MinimapUiLaw.BlipTint(true, true) == 0xffffffff &&
              MathF.Abs(MinimapUiLaw.LandmarkIconSize(140.8f) - 16f) < .001f &&
              MathF.Abs(MinimapUiLaw.LandmarkArrowSize(140.8f) - 38.4f) < .001f &&
              Vector2.Distance(MinimapUiLaw.LandmarkArrowCenter(
                  Vector2.Zero, Vector2.UnitX, 140.8f), new Vector2(56.32f, 0)) < .001f &&
              Vector2.Distance(MinimapUiLaw.GossipPoiCenter(Vector2.Zero,
                  new Vector2(20, 0), new Vector2(70.4f), 140.8f, 100f),
                  new Vector2(70.4f, 56.32f)) < .001f &&
              Vector2.Distance(MinimapUiLaw.GossipPoiCenter(Vector2.Zero,
                  new Vector2(-300, 0), new Vector2(70.4f), 140.8f, 100f),
                  new Vector2(70.4f, 126.72f)) < .001f &&
              Vector3.Distance(MinimapUiLaw.OutdoorDayTint(
                  Vector3.Zero, Vector3.Zero), new Vector3(.28125f)) < .0001f &&
              MinimapUiLaw.OutdoorDayTint(Vector3.One, Vector3.One) == Vector3.One,
            "minimap zoom/sound/tooltip law drift");

        MinimapPartyBlip partyDot = MinimapUiLaw.PartyBlip(
            Vector2.Zero, new Vector2(30, 0), new Vector2(70.4f), 140.8f, 100f);
        MinimapPartyBlip boundaryDot = MinimapUiLaw.PartyBlip(
            Vector2.Zero, new Vector2(0, 80), new Vector2(70.4f), 140.8f, 100f);
        MinimapPartyBlip partyArrow = MinimapUiLaw.PartyBlip(
            Vector2.Zero, new Vector2(-300, 0), new Vector2(70.4f), 140.8f, 100f);
        // Centres are compared by distance, as the partyArrow term below already did:
        // exact float equality fails on 49.280003 and 14.080002, which are the same
        // projection to well within a pixel.
        Check(!partyDot.IsArrow &&
              Vector2.Distance(partyDot.Center, new Vector2(70.4f, 49.28f)) < .001f &&
              MathF.Abs(partyDot.Size - 10.4f) < .001f &&
              !boundaryDot.IsArrow &&
              Vector2.Distance(boundaryDot.Center, new Vector2(14.08f, 70.4f)) < .001f &&
              partyArrow.IsArrow &&
              Vector2.Distance(partyArrow.Center, new Vector2(70.4f, 126.72f)) < .001f &&
              MathF.Abs(partyArrow.Size - 38.4f) < .001f &&
              // Due west is +PI or -PI depending on the sign of the zero atan2 receives,
              // and the two render identically. Accept either branch of the wrap.
              MathF.Abs(MathF.Abs(partyArrow.Rotation) - MathF.PI) < .001f,
            "minimap party dot/0.8 split/rim-arrow projection drift");

        Check(WmoMinimapProjection.CompositeSize == 256 &&
              WmoMinimapProjection.CompositeHalfExtentScale == 1.5f &&
              MathF.Abs(WmoMinimapProjection.CompositeBlitFraction - 2f / 3f) < .0001f &&
              MathF.Abs(WmoMinimapProjection.InteriorAlphaReference - 224f / 255f) < .0001f &&
              WmoMinimapProjection.ToCompositeClip(
                  new Vector2(70), new Vector2(70), 140f) == Vector2.Zero &&
              Vector2.Distance(WmoMinimapProjection.ToCompositeClip(
                  new Vector2(0, 70), new Vector2(70), 140f),
                  new Vector2(-2f / 3f, 0f)) < .0001f &&
              Vector2.Distance(WmoMinimapProjection.ToCompositeClip(
                  new Vector2(140, 70), new Vector2(70), 140f),
                  new Vector2(2f / 3f, 0f)) < .0001f,
            "interior minimap fixed-target/minify/crop law drift");

        AreaPoiInfo[] pois =
        [
            Poi(1, 3, 0x5, 46, "Northshire Abbey"),
            Poi(2, 0, 0x4, 237, "Echo Ridge Mine"),
            Poi(3, 3, 0x1d, 556, "Stormwind"),
            Poi(4, 3, 0x5, 601, "Goldshire"),
            Poi(5, 0, 0x3, 50, "Tower", icon: 9),
        ];
        AreaPoiSelection landmarks = AreaPoiCatalog.Select(
            pois, 0, Vector2.Zero, 133f);
        Check(landmarks.Icons.Select(row => row.Name).SequenceEqual(["Tower"]) &&
              landmarks.Arrows.Select(row => row.Poi.Name)
                  .SequenceEqual(["Stormwind", "Goldshire"]) &&
              AreaPoiCatalog.TryIconUv(9, out Vector2 uvMin, out Vector2 uvMax) &&
              uvMin == new Vector2(.125f, .125f) &&
              uvMax == new Vector2(.25f, .25f) &&
              !AreaPoiCatalog.TryIconUv(64, out _, out _),
            "AreaPOI candidacy, 0.8 split, signed importance rank or atlas law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Minimap.cs"));
        string areas = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats", "AreaTable.cs"));
        string poisSource = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "AreaPoiCatalog.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string composite = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "InteriorMinimapComposite.cs"));
        Check(runtime.Contains("MinimapUiLaw.ZonePvp", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapZoomButton", StringComparison.Ordinal) &&
              runtime.Contains("SetMinimapVisible", StringComparison.Ordinal) &&
              runtime.Contains("MinimapUiLaw.UnreadMailText", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapQuestDots", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapTrackedCreatureDots", StringComparison.Ordinal) &&
              runtime.Contains("MinimapTrackedCreatureType", StringComparison.Ordinal) &&
              runtime.Contains("MinimapBlipTint", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapLandmarks", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapGossipPoi", StringComparison.Ordinal) &&
              runtime.Contains("MinimapUiLaw.GossipPoiCenter", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapPartyArrows", StringComparison.Ordinal) &&
              runtime.Contains("DrawMinimapCorpseArrow", StringComparison.Ordinal) &&
              runtime.Contains("PartyMinimapPositions", StringComparison.Ordinal) &&
              runtime.Contains("ControlledGuid == LocalPlayerGuid\n            ? PartyFrameMembers()",
                  StringComparison.Ordinal) &&
              runtime.Contains("MinimapUiLaw.PartyBlip", StringComparison.Ordinal) &&
              runtime.Contains("ROTATING-MINIMAPARROW", StringComparison.Ordinal) &&
              runtime.Contains("AddRotatedMinimapImage", StringComparison.Ordinal) &&
              runtime.Contains("MinimapUiLaw.OutdoorDayTint", StringComparison.Ordinal) &&
              runtime.Contains("tint: packedTint", StringComparison.Ordinal) &&
              runtime.Contains("_minimapInteriorComposite.Render", StringComparison.Ordinal) &&
              runtime.Contains("WmoMinimapProjection.CompositeBlitFraction", StringComparison.Ordinal) &&
              runtime.Contains("uvMin: uvMin, uvMax: uvMax", StringComparison.Ordinal) &&
              composite.Contains("InternalFormat.Rgba8", StringComparison.Ordinal) &&
              composite.Contains("_gl.Disable(EnableCap.Blend)", StringComparison.Ordinal) &&
              composite.Contains("uAlphaReference", StringComparison.Ordinal) &&
              composite.Contains("if(t.a<uAlphaReference) discard", StringComparison.Ordinal) &&
              runtime.Contains("minimap-tracking", StringComparison.Ordinal) &&
              runtime.Contains("StepMinimapZoom(bool zoomIn, bool insideWmo",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.MinimapZoomIn, \"Minimap Zoom In\", Key.KeypadAdd",
                  StringComparison.Ordinal) &&
              bindings.Contains("UpdateMinimapZoomBindings(bool typing)",
                  StringComparison.Ordinal) &&
              program.Contains("UpdateMinimapZoomBindings(typing);", StringComparison.Ordinal) &&
              areas.Contains("FactionGroupMask", StringComparison.Ordinal) &&
              areas.Contains("public uint? Flags", StringComparison.Ordinal) &&
              poisSource.Contains("dbc.GetUInt(row, 28)", StringComparison.Ordinal) &&
              poisSource.Contains("unchecked((int)left.Poi.Importance)", StringComparison.Ordinal) &&
              poisSource.Contains("if (arrows.Count > 3)", StringComparison.Ordinal),
            "minimap production wiring drift");
    }

    private static AreaPoiInfo Poi(uint id, uint importance, uint flags, float x,
        string name, uint icon = 6) => new(id, importance, icon, 0,
        new Vector3(x, 0, 0), 0, flags, 0, name, "", 0);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
