using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Wmo;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private Dictionary<string, string>? _minimapTileMap;
    private int _minimapZoom = 2;
    private int _minimapInsideZoom = 2;
    private bool _minimapVisible = true;
    private uint _minimapAreaId;
    private uint _minimapReportedZoneId;
    private string _minimapAreaMap = "";
    private WmoAreaCatalog? _wmoAreas;
    private readonly Dictionary<(uint Wmo, uint NameSet, uint Group), uint>
        _minimapWmoAreaCache = [];
    private bool _wmoAreasLoaded;
    private WmoRenderer? _minimapInteriorRenderer;
    private int _minimapInteriorWmoVersion = -1;
    private WmoRenderer.InteriorMinimapContext? _minimapInteriorContext;
    private WmoRenderer.AreaMinimapIdentity? _minimapAreaInterior;
    private float? _minimapInteriorTerrainZ;
    private float _minimapInteriorRadius;
    private double _minimapInteriorNextResolveAt;
    private readonly Dictionary<string, Task<GameplayArt.PreparedTexture?>>
        _minimapPreparedTextureTasks = new(StringComparer.OrdinalIgnoreCase);
    private (WmoRenderer? Renderer, int Version, int Instance, int Group,
        int Zoom, int CellX, int CellY)? _minimapActiveWarmKey;
    private bool _minimapActiveWarmReady;
    private string _minimapInteriorSignature = "";
    private string _minimapResourceSignature = "";
    private MinimapResourceTooltipRuntime? _minimapResourceTooltip;
    private AreaPoiCatalog? _minimapAreaPois;
    private bool _minimapAreaPoisLoaded;
    private InteriorMinimapComposite? _minimapInteriorComposite;

    private readonly record struct MinimapResourceTooltipCandidate(
        ulong Guid, string Name, GameTooltipLine[]? FrameXmlLines = null);
    private readonly record struct MinimapResourceTooltipRuntime(
        GameTooltipOwnerToken Token,
        string Name);

    private void DrawMinimap()
    {
        if ((_net is null && !HudPreview) || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player))
        {
            UpdateAndQueueMinimapResourceTooltip(null);
            return;
        }

        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        // WC3 console law (owner 2026-08-25): in the free view the minimap is
        // command furniture — SQUARE, docked to the bottom-LEFT corner (the
        // chat frame lifts above it); top-right belongs to normal body play.
        // Two authored placements of one registered frame (PLAN_21): top-right flush in body
        // play, 8 px in from the bottom-left corner in the Command View.
        HudFrameResult minimapFrame = HudFrame("minimap", "Minimap",
            HudPlacement.At(HudAnchor.TopRight, 0f, 0f), new Vector2(192f, 192f),
            authoredCommand: HudPlacement.At(HudAnchor.BottomLeft, 8f, -8f));
        if (minimapFrame.Hidden)
        {
            UpdateAndQueueMinimapResourceTooltip(null);
            return;
        }
        Vector2 root = minimapFrame.LogicalOrigin;
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        DrawWorldStateCaptureBars(dl, rootPx, s);

        // ToggleMinimap hides the map body, not its toggle. Keeping this control alive is
        // what lets the stock 1.12 minimap be reopened after it has been collapsed.
        if (!_minimapVisible)
        {
            DrawMinimapTexture(dl, root, Vector2.Zero, new(192, 32),
                @"Interface\Minimap\UI-Minimap-Border", new(.25f, 0), new(1, .125f));
            // Keep the zone name on the collapsed strip so it stays a live readout, and make the
            // whole strip the reopen handle: click the title to bring the map back. No X button
            // here - X is a hide-only control in the open state, so you restore by clicking the
            // strip (or the ToggleMinimap key), never by toggling the same button hide/unhide.
            DrawMinimapZoneText(dl, root, player, s);
            Vector2 stripMin = root * s;
            Vector2 stripMax = (root + new Vector2(192, 32)) * s;
            if (ImGui.IsMouseHoveringRect(stripMin, stripMax, false) &&
                ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SetMinimapVisible(true);
            DrawControlGuideMinimapTab(dl, (root + new Vector2(6f, 34f)) * s, s);
            UpdateAndQueueMinimapResourceTooltip(null);
            return;
        }

        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("MinimapCluster", "Frame", rootPx, new Vector2(192) * s,
                parent: "", point: "TOPRIGHT", strata: "BACKGROUND");
        }

        // Painterly mode presents the map SQUARE: it declines to draw the ring art and frames
        // the same rect, which lets the body grow into the corners the circle throws away.
        // (The old note here claimed vanilla's roundness was just border art painted over the
        // corners. It is not — FrameXML declares `<Minimap name="Minimap">`, a widget type the
        // engine crops to a disc, and the ring's corners are transparent. DrawMovingMinimap
        // does the crop itself now; without it the tile plane leaked past the ornament.)
        bool squareMap = PainterlyUi || _freeView;
        Vector2 mapMin = (root + (squareMap ? new Vector2(12, 26) : new Vector2(35, 22))) * s;
        Vector2 mapMax = mapMin + new Vector2(squareMap ? 168 : 140) * s;
        DrawBattlefieldQueueControl(mapMin, mapMax, s);
        // Local movement is client-authoritative in 1.12. The controller is the
        // continuously updated truth; the object-store entity is only the last
        // server snapshot and can remain unchanged for seconds.
        // Free view turns the controller into a detached camera and drives no
        // unit. The minimap must remain attached to the controlled character;
        // in ordinary/possessed play the controller is the locally-authoritative
        // mover and therefore remains the right source.
        Vector3 playerPosition = _freeView
            ? player.Position
            : _controller?.Position ?? player.Position;
        float playerOrientation = _freeView
            ? player.Orientation
            : _controller?.Yaw ?? player.Orientation;
        MinimapProjection projection = MinimapProjection.FromWorld(playerPosition);
        // Interior membership belongs to the player, not the orbit camera. The
        // +1.7 yd probe begins above the walking floor while still staying below
        // any outdoor terrain shell over underground cities such as Ironforge.
        Vector3 interiorProbe = playerPosition + new Vector3(0f, 0f, 1.7f);
        float? terrainZ = _terrain?.SampleHeight(interiorProbe.X, interiorProbe.Y);
        float interiorRadius = WmoMinimapProjection.ZoomRadiusYards[
            Math.Clamp(_minimapInsideZoom, 0, WmoMinimapProjection.ZoomRadiusYards.Length - 1)];
        WmoRenderer.InteriorMinimapContext? interior = ResolveMinimapInterior(
            playerPosition, interiorProbe, interiorRadius, terrainZ);
        bool insideWmo = interior is not null;
        if (interior is not null)
        {
            var warmKey = (
                _wmo,
                _wmo?.LiquidVersion ?? -1,
                interior.InstanceId,
                interior.GroupIndex,
                _minimapInsideZoom,
                (int)MathF.Floor(playerPosition.X / 8f),
                (int)MathF.Floor(playerPosition.Y / 8f));
            if (_minimapActiveWarmKey != warmKey)
            {
                _minimapActiveWarmKey = warmKey;
                _minimapActiveWarmReady = false;
            }
            if (!_minimapActiveWarmReady)
                _minimapActiveWarmReady = AdvanceMinimapTexturePreparation(
                    interior, playerPosition, interiorRadius);
            DrawInteriorMinimap(dl, mapMin, mapMax, playerPosition, interior,
                circular: !squareMap);
        }
        else
        {
            _minimapActiveWarmKey = null;
            _minimapActiveWarmReady = false;
            DrawMovingMinimap(dl, mapMin, mapMax, projection, circular: !squareMap);
        }
        // Area identity is a separate feet-level faces-only claim. A doorway
        // may seed the display flood without changing the zone-text room.
        UpdateMinimapArea(projection, _minimapAreaInterior);

        // Command View: a GUIDE tab mortised into the square map's lower-left edge. Body play:
        // a CONTROL GUIDE tab hung under the round map, in the band the open guide occupies
        // (GameLoop.Control.cs, ControlGuideBodyTop) - never an ImGui button over the bars.
        Vector2 guideTabSize = ControlGuideMinimapTabSize(s);
        Vector2 guideTabMin = _freeView
            ? new(mapMin.X + 4f * s, mapMax.Y - 26f * s)
            : (root + new Vector2((192f - ControlGuideBodyTabWidth) * .5f, 194f)) * s;
        Vector2 guideTabMax = guideTabMin + guideTabSize;
        bool guideTabHovered = ControlGuideMinimapTabVisible &&
            ImGui.IsMouseHoveringRect(guideTabMin, guideTabMax, false);
        if (ImGui.IsMouseHoveringRect(mapMin, mapMax, false) && !guideTabHovered)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel > 0)
                StepMinimapZoom(zoomIn: true, insideWmo);
            else if (wheel < 0)
                StepMinimapZoom(zoomIn: false, insideWmo);
            // Minimap_OnClick: a left-click on the map while grouped pings the spot for the party.
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().WantCaptureMouse)
                SendMinimapPing(ImGui.GetMousePos(), new Vector2(playerPosition.X, playerPosition.Y),
                    (mapMin + mapMax) * .5f, mapMax.X - mapMin.X,
                    insideWmo ? interiorRadius : MinimapUiLaw.OutdoorRadius(_minimapZoom));
        }

        float? interiorBlipRadius = insideWmo ? interiorRadius : null;
        MinimapResourceTooltipCandidate? landmarkTooltip = DrawMinimapLandmarks(
            dl, playerPosition, mapMin, mapMax, interiorBlipRadius);
        DrawMinimapPartyArrows(dl, playerPosition, mapMin, mapMax, interiorBlipRadius);
        DrawMinimapCorpseArrow(dl, playerPosition, mapMin, mapMax, interiorBlipRadius);
        MinimapResourceTooltipCandidate? gossipPoiTooltip = DrawMinimapGossipPoi(
            dl, playerPosition, mapMin, mapMax, s, interiorBlipRadius);
        DrawMinimapPlayerArrow(dl, playerOrientation, (mapMin + mapMax) * .5f, s);
        DrawMinimapPartyDots(dl, playerPosition, mapMin, mapMax, s, interiorBlipRadius);
        DrawMinimapPings(dl, playerPosition, mapMin, mapMax, s, interiorBlipRadius);
        DrawMinimapCorpseMarker(dl, playerPosition, mapMin, mapMax, s,
            interiorBlipRadius);
        MinimapResourceTooltipCandidate? resourceTooltip =
            DrawMinimapResourceDots(dl, player, playerPosition, mapMin, mapMax, s,
                interiorBlipRadius);
        MinimapResourceTooltipCandidate? creatureTooltip =
            DrawMinimapTrackedCreatureDots(dl, player, playerPosition, mapMin, mapMax, s,
                interiorBlipRadius);
        MinimapResourceTooltipCandidate? questTooltip =
            DrawMinimapQuestDots(dl, playerPosition, mapMin, mapMax, s, interiorBlipRadius);
        MinimapResourceTooltipCandidate? questHelperTooltip =
            DrawMinimapQuestHelperPins(dl, playerPosition, mapMin, mapMax, s,
                interiorBlipRadius);
        UpdateAndQueueMinimapResourceTooltip(
            questHelperTooltip ?? questTooltip ?? gossipPoiTooltip ?? creatureTooltip ?? resourceTooltip ??
            landmarkTooltip);
        if (_uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("Minimap", "Minimap", mapMin, new Vector2(140) * s,
                parent: "MinimapCluster", point: "CENTER", relativePoint: "TOP",
                offsetX: "9", offsetY: "-92", strata: "BACKGROUND");

        if (squareMap) DrawSquareMinimapFrame(dl, mapMin, mapMax, s);
        else DrawMinimapTexture(dl, root, new(0, 20), new(192),
            @"Interface\Minimap\UI-Minimap-Border", new(.25f, .125f), new(1f, .875f));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapBackdrop", "Frame", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "Minimap", point: "CENTER",
                relativeTo: "MinimapCluster", offsetX: "0", offsetY: "-20", strata: "BACKGROUND");
            CollectUiParity("MinimapBorder", "Texture", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "MinimapBackdrop",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.125|1.0|0.875");
        }

        // The stock zoom buttons sit on the CIRCLE's lower-right arc, which is
        // inside the square body once the corners come back. Tuck them under
        // the frame instead.
        Vector2 zoomIn = squareMap ? new Vector2(120, 196) : new Vector2(157, 113);
        Vector2 zoomOut = squareMap ? new Vector2(150, 196) : new Vector2(131, 141);
        int activeZoom = insideWmo ? _minimapInsideZoom : _minimapZoom;
        DrawMinimapZoomButton(dl, root + zoomIn, zoomIn: true,
            MinimapUiLaw.ZoomInEnabled(activeZoom),
            () =>
                StepMinimapZoom(zoomIn: true, insideWmo, playSound: false));
        DrawMinimapZoomButton(dl, root + zoomOut, zoomIn: false,
            MinimapUiLaw.ZoomOutEnabled(activeZoom),
            () =>
                StepMinimapZoom(zoomIn: false, insideWmo, playSound: false));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapZoomIn", "Button", (root + new Vector2(157, 113)) * s,
                new Vector2(32) * s, parent: "MinimapBackdrop", point: "CENTER",
                offsetX: "77", offsetY: "-13", texture: @"Interface\Minimap\UI-Minimap-ZoomInButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoomIn/NormalTexture", "NormalTexture", (root + new Vector2(157, 113)) * s,
                new Vector2(32) * s, parent: "MinimapZoomIn",
                texture: @"Interface\Minimap\UI-Minimap-ZoomInButton-Up", strata: "BACKGROUND");
            CollectUiParity("MinimapZoomOut", "Button", (root + new Vector2(131, 141)) * s,
                new Vector2(32) * s, parent: "MinimapBackdrop", point: "CENTER",
                offsetX: "51", offsetY: "-41", texture: @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoomOut/NormalTexture", "NormalTexture", (root + new Vector2(131, 141)) * s,
                new Vector2(32) * s, parent: "MinimapZoomOut",
                texture: @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up", strata: "BACKGROUND");
        }

        // Square mode draws its own title bar (in DrawSquareMinimapFrame); the
        // stock top piece is curved art that reads as a mistake on a rectangle.
        if (!squareMap)
            DrawMinimapTexture(dl, root, Vector2.Zero, new(192, 32),
                @"Interface\Minimap\UI-Minimap-Border", new(.25f, 0), new(1, .125f));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("MinimapBorderTop", "Texture", rootPx, new Vector2(192, 32) * s,
                parent: "MinimapCluster", point: "TOPRIGHT",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.0|1.0|0.125");

        DrawMinimapZoneText(dl, root, player, s);
        DrawMinimapTracking(dl, root, player, s);
        DrawMinimapMail(dl, root, s);
        DrawMinimapCompanionsButton(dl, root, mapMin, mapMax, s, squareMap);
        DrawControlGuideMinimapTab(dl, guideTabMin, s);

        Vector2 toggleMin = (root + new Vector2(161, -3)) * s;
        DrawMinimapButton(dl, root + new Vector2(161, -3),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", () => SetMinimapVisible(false));
        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapToggleButton", "Button", toggleMin, new Vector2(32) * s,
                parent: "MinimapCluster", point: "CENTER", relativePoint: "TOPRIGHT",
                offsetX: "-15", offsetY: "-13", texture: @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapToggleButton/NormalTexture", "NormalTexture", toggleMin,
                new Vector2(32) * s, parent: "MinimapToggleButton",
                texture: @"Interface\Buttons\UI-Panel-MinimizeButton-Up", strata: "BACKGROUND");
            MarkUiParityFrameComplete();
        }
    }

    /// <summary>The hidden guide restores from a small vanilla tab on the minimap: "GUIDE"
    /// mortised into the square map's lower-left edge in the Command View, "CONTROL GUIDE"
    /// hung under the round map in body play while a body is driven. It remains available when
    /// the map body is collapsed, where its caller places it immediately below the surviving
    /// zone strip. Owner 2026-09-03: the ImGui restore button over the action bars is gone.</summary>
    private bool ControlGuideMinimapTabVisible =>
        ControlGuideApplies && _enableControlGuide && !_showControlGuide;

    private const float ControlGuideBodyTabWidth = 110f;

    private Vector2 ControlGuideMinimapTabSize(float s) =>
        new Vector2(_freeView ? 66f : ControlGuideBodyTabWidth, 22f) * s;

    private void DrawControlGuideMinimapTab(ImDrawListPtr dl, Vector2 min, float s)
    {
        if (!ControlGuideMinimapTabVisible) return;

        Vector2 size = ControlGuideMinimapTabSize(s);
        Vector2 max = min + size;
        bool hovered = ImGui.IsMouseHoveringRect(min, max, false);
        DrawRtsConsoleBackdrop(dl, min, max, s);
        if (hovered)
            dl.AddRect(min + new Vector2(3f) * s, max - new Vector2(3f) * s,
                PainterlyGoldLit, 0f, ImDrawFlags.None, MathF.Max(1f, s));
        GameText.DrawCentered(dl, "GameFontNormalSmall", _freeView ? "GUIDE" : "CONTROL GUIDE",
            (min + max) * .5f, s, hovered ? PainterlyGoldLit : PainterlyFrameRule);

        if (!hovered) return;
        OfferPreservedSharedGameTooltipRenderer(new("commander-guide", 1),
            () => HoverTip(_freeView
                ? "Commander Guide\nShow Command View controls"
                : "Control Guide\nShow direct-control keys"));
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _showControlGuide = true;
    }

    /// <summary>
    /// The square map frame: a painted border with a title strip above it,
    /// drawn from primitives rather than art so it needs no new BLPs. Layered
    /// dark-to-light outward, which is how the reference's stone frames read.
    /// </summary>
    private void DrawSquareMinimapFrame(ImDrawListPtr dl, Vector2 mapMin, Vector2 mapMax, float s)
    {
        float pad = MathF.Max(1f, 3f * s);
        var outerMin = new Vector2(mapMin.X - pad * 2f, mapMin.Y - pad * 2f);
        var outerMax = new Vector2(mapMax.X + pad * 2f, mapMax.Y + pad * 2f);

        // Title strip over the map's top edge, where the zone name already
        // lands - carved from the same stone as the frame, so the cluster reads
        // as one piece rather than a label sitting on a box.
        float rule = MathF.Max(1f, s);
        var titleMin = new Vector2(outerMin.X, outerMin.Y - 22f * s);
        var titleMax = new Vector2(outerMax.X, outerMin.Y);
        dl.AddRectFilledMultiColor(titleMin, titleMax,
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneLow, PainterlyStoneLow);
        dl.AddRect(titleMin, titleMax, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule);
        DrawBevel(dl, titleMin, titleMax, rule, PainterlyStoneTop, PainterlyFrameOuter);
        dl.AddLine(new Vector2(titleMin.X, titleMax.Y - rule),
                   new Vector2(titleMax.X, titleMax.Y - rule), PainterlyFrameRule, rule);

        // Frame proper: the shared carved panel, so map, portrait and skill bar
        // are literally the same stone, inlay and light direction.
        DrawSquarePanel(dl, mapMin, mapMax - mapMin, s);

        // Compass letter under the map, as on the reference's framed map.
        float compassWidth = GameText.MeasurePlain("N", 11f, s);
        GameText.DrawPlain(dl, "N",
            new Vector2((outerMin.X + outerMax.X) * .5f - compassWidth * .5f, outerMax.Y + 2f * s),
            11f, s, UiGoldU32());
    }

    private void DrawMinimapZoneText(ImDrawListPtr dl, Vector2 root, WorldEntity player, float s)
    {
        Vector2 min = (root + new Vector2(29, 7)) * s;
        Vector2 size = new Vector2(128, 12) * s;
        EnsureAreaTableForMinimap();
        uint fallback = _net?.Player?.Zone ?? 0;
        string text = _areas?.AreaName(_minimapAreaId) is { Length: > 0 } subZone ? subZone :
            _areas?.ZoneName(fallback) is { Length: > 0 } zone ? zone : _config.Start.MapName;
        uint leafArea = _minimapAreaId != 0 ? _minimapAreaId : fallback;
        uint zoneArea = _areas?.ParentZoneId(leafArea) ?? 0;
        if (zoneArea == 0) zoneArea = leafArea;
        MinimapZonePvpInfo pvp = ResolveAreaPvp(player, leafArea, zoneArea);
        Vector4 tint = pvp.IsArena ? new Vector4(1f, .1f, .1f, 1f) : pvp.Tint;
        float zoneWidth = GameText.MeasurePlain(text, 12f, s);
        // Vanilla anchors this label to the TOP of its box. The free-view / painterly SQUARE
        // frame draws a 22px title strip above the map (DrawSquareMinimapFrame), where the top
        // anchor sat the descenders on the frame edge and clipped them - so there, center the
        // label vertically in that strip. The round minimap keeps the exact vanilla anchor.
        float textTop = min.Y;
        if (PainterlyUi || _freeView)
        {
            float pad = MathF.Max(1f, 3f * s);
            float stripBottom = (root.Y + 26f) * s - 2f * pad;   // square frame outerMin.Y
            float stripTop = stripBottom - 22f * s;
            textTop = (stripTop + stripBottom) * .5f - 12f * s * .5f;
        }
        GameText.DrawPlain(dl, text, new Vector2(min.X + (size.X - zoneWidth) * .5f, textTop),
            12f, s, ImGui.ColorConvertFloat4ToU32(tint));
        if (ImGui.IsMouseHoveringRect(min, min + size, false))
        {
            var lines = new List<string> { text };
            if (pvp.TerritoryLine is { Length: > 0 } territory) lines.Add(territory);
            if (pvp.IsArena) lines.Add(MinimapUiLaw.ArenaText);
            string prepared = string.Join('\n', lines);
            OfferPreservedSharedGameTooltipRenderer(new("minimap-zone", leafArea),
                () => HoverTip(prepared));
            // Display-only: hiding is the X button's job (open state), restoring is the
            // collapsed strip's job. The label no longer swallows clicks to hide the map -
            // a label that quietly closed the map was a third, non-obvious close path.
        }
        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapZoneTextButton", "Button", min, size,
                parent: "MinimapCluster", point: "CENTER", offsetX: "-3", offsetY: "83",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoneText", "FontString", min, size,
                parent: "MinimapZoneTextButton", point: "TOP", font: "GameFontNormal",
                fontPath: @"Fonts\FRIZQT__.TTF", fontSize: "12", color: "#FFD100FF",
                layer: "BACKGROUND", strata: "BACKGROUND");
        }
    }

    private void DrawMinimapTracking(ImDrawListPtr dl, Vector2 root, WorldEntity player, float s)
    {
        AuraSnapshot? tracking = null;
        SpellInfo trackingSpell = default;
        foreach (AuraSnapshot aura in SnapshotAuras(player).Values.OrderBy(a => a.Slot))
            if (_spellCatalog?.TryGet(aura.SpellId, out SpellInfo spell) == true &&
                spell.AuraIds?.Any(type => type is 44 or 45 or 151) == true)
            { tracking = aura; trackingSpell = spell; }
        if (tracking is not AuraSnapshot active) return;
        uint icon = _gameplayArt!.Handle(trackingSpell.IconPath);
        if (icon == 0) return;
        Vector2 frame = root + new Vector2(20, 22);
        dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
            (frame + new Vector2(33, 32)) * s);
        DrawMinimapTexture(dl, frame, Vector2.Zero, new(64),
            @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
        Vector2 hitMin = (frame + new Vector2(7, 6)) * s;
        Vector2 hitMax = hitMin + new Vector2(33) * s;
        if (ImGui.IsMouseHoveringRect(hitMin, hitMax, false))
        {
            string prepared = trackingSpell.Name;
            OfferPreservedSharedGameTooltipRenderer(new("minimap-tracking", trackingSpell.Id),
                () => HoverTip(prepared));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                CancelPlayerAura(active, "MINIMAP_TRACKING_RIGHT_CLICK");
        }
    }

    private void DrawMinimapMail(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (!_hasNewMail) return;
        // MiniMapMailFrame: TOPRIGHT of Minimap, +21/-38; icon +7/-6 and tracking border.
        Vector2 frame = root + new Vector2(166, 60);
        uint icon = _gameplayArt?.Handle(@"Interface\Icons\INV_Letter_15") ?? 0;
        if (icon != 0)
            dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
                (frame + new Vector2(25, 24)) * s);
        DrawMinimapTexture(dl, frame, Vector2.Zero, new(52),
            @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
        Vector2 hitMin = frame * s;
        if (ImGui.IsMouseHoveringRect(hitMin, hitMin + new Vector2(33) * s, false))
        {
            string prepared = MinimapUiLaw.UnreadMailText;
            OfferPreservedSharedGameTooltipRenderer(new("minimap-mail", 1),
                () => HoverTip(prepared));
        }
    }

    /// <summary>Companions minimap button art: the LibDBIcon look — the stock tracking ring
    /// (52 px art, 33 px button, 18 px icon at +7/+6, exactly the MiniMapMailFrame geometry
    /// above) around a vanilla "prayer" icon that reads as a group of people.</summary>
    private const string CompanionsMinimapIcon = @"Interface\Icons\Spell_Holy_PrayerofFortitude";

    /// <summary>Round view: button centre sits ON the ring at this angle, measured the
    /// LibDBIcon way (degrees, 0 = east/3 o'clock, counter-clockwise, y-up) at radius 80 px
    /// from the map centre. 200° is the lower-left arc, the only stretch free of the
    /// tracking button (~142°), the mail icon (~40°), the zoom pair (~300°-320°), the
    /// toggle X and the zone strip.</summary>
    private const float CompanionsMinimapAngleDegrees = 200f;
    private const float CompanionsMinimapRingRadius = 80f;

    /// <summary>
    /// The Companions window's entry point as an ADDON-STYLE minimap button (owner
    /// 2026-09-02: "attached to the minimap the way addons are"). Round view: a LibDBIcon
    /// button riding the ring's lower-left arc. Command View (square map docked
    /// bottom-left): hugs the frame's RIGHT edge just under the title strip so it reads as
    /// bolted onto the map. Painterly's square map is top-right, so there it hangs off the
    /// LEFT edge instead. Draw-list images + a hit rect, like the other minimap buttons.
    /// Deliberately NOT collected into the FrameXML parity set — it is an addon extra,
    /// not a stock 1.12 element, so a parity diff must never expect it.
    /// </summary>
    private void DrawMinimapCompanionsButton(ImDrawListPtr dl, Vector2 root, Vector2 mapMin,
        Vector2 mapMax, float s, bool squareMap)
    {
        if (_gameplayArt is null) return;
        // `frame` is the 33x33 button's top-left in logical px; the ring art and the icon
        // hang off it with the mail frame's offsets, so the icon centre is frame + (16, 15).
        Vector2 frame;
        if (!squareMap)
        {
            Vector2 mapCenter = (mapMin + mapMax) * .5f / s;
            (float sin, float cos) = MathF.SinCos(CompanionsMinimapAngleDegrees * MathF.PI / 180f);
            // Screen y grows downward, so a y-up angle flips the sine.
            Vector2 center = mapCenter + CompanionsMinimapRingRadius * new Vector2(cos, -sin);
            frame = center - new Vector2(16f, 15f);
        }
        else
        {
            // DrawSquareMinimapFrame's outer edge is the map rect grown by 2*pad (pad = 3 px),
            // with the 22 px title strip above it. The button STRADDLES that edge the way a
            // LibDBIcon button straddles the round ring: icon centre 6 px outside the bevel, so
            // the ring art overlaps the frame and reads as bolted on. A gap beside the frame
            // read as a loose button (owner, 2026-09-02: "not attached to the minimap").
            const float overhang = 6f;
            float pad = MathF.Max(1f, 3f * s) / s;
            float outerTop = mapMin.Y / s - pad * 2f;
            float outerRight = mapMax.X / s + pad * 2f;
            float outerLeft = mapMin.X / s - pad * 2f;
            frame = _freeView
                ? new Vector2(outerRight + overhang - 16f, outerTop + 4f)
                : new Vector2(outerLeft - overhang - 16f, outerTop + 4f);
        }

        bool available = _companionsAvailable;
        uint icon = _gameplayArt.CircularHandle(CompanionsMinimapIcon);
        if (icon != 0)
            dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
                (frame + new Vector2(25, 24)) * s, Vector2.Zero, Vector2.One,
                // Dimmed when the server lacks the bit; still clickable so the window can
                // explain rather than a dead button explaining nothing.
                available ? 0xffffffffu : 0xff6a6a6au);
        DrawMinimapTexture(dl, frame, Vector2.Zero, new(52),
            @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
        Vector2 hitMin = frame * s;
        if (!ImGui.IsMouseHoveringRect(hitMin, hitMin + new Vector2(33) * s, false)) return;
        string prepared = available
            ? "Companions\nSummon your own characters as party members"
            : "Companions\nThis server does not support companions";
        OfferPreservedSharedGameTooltipRenderer(new("minimap-companions", 1),
            () => HoverTip(prepared));
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) ToggleCompanionsPanel();
    }

    private IEnumerable<Vector2> PartyMinimapPositions()
    {
        // The reference's place_party_raid_blips consumes party1..party4: own subgroup, packet
        // order, capped at four. Preserve the existing SuperUI possession projection separately;
        // that custom controlled-subject state is not a Benilla surface to normalize here.
        IEnumerable<PartyMember> members = ControlledGuid == LocalPlayerGuid
            ? PartyFrameMembers()
            : _partyMembers;
        foreach (PartyMember member in members)
        {
            if (_entities.TryGet(member.Guid, out WorldEntity unit))
            {
                Vector3 live = UnitWorldPosition(unit);
                yield return new(live.X, live.Y);
                continue;
            }
            if (_partyStats.TryGetValue(member.Guid, out PartyMemberStatsSnapshot stats) &&
                stats.PositionX is short x && stats.PositionY is short y)
                yield return new(x, y);
        }
    }

    private void DrawMinimapPartyArrows(ImDrawListPtr dl, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float? radiusOverride = null)
    {
        uint arrow = _gameplayArt?.Handle(@"Interface\Minimap\ROTATING-MINIMAPARROW") ?? 0;
        if (arrow == 0) return;
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        Vector2 center = (mapMin + mapMax) * .5f;
        Vector2 player = new(playerPosition.X, playerPosition.Y);
        foreach (Vector2 member in PartyMinimapPositions())
        {
            MinimapPartyBlip blip = MinimapUiLaw.PartyBlip(
                player, member, center, side, radiusYards);
            if (blip.IsArrow)
                AddRotatedMinimapImage(dl, arrow, blip.Center, blip.Size, blip.Rotation);
        }
    }

    private void DrawMinimapPartyDots(ImDrawListPtr dl, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float s, float? radiusOverride = null)
    {
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        Vector2 center = (mapMin + mapMax) * .5f;
        Vector2 player = new(playerPosition.X, playerPosition.Y);
        uint icons = _gameplayArt?.Handle(@"Interface\Minimap\ObjectIcons") ?? 0;
        dl.PushClipRect(mapMin, mapMax, true);
        foreach (Vector2 member in PartyMinimapPositions())
        {
            MinimapPartyBlip blip = MinimapUiLaw.PartyBlip(
                player, member, center, side, radiusYards);
            if (blip.IsArrow) continue;
            Vector2 half = new(blip.Size * .5f);
            if (icons != 0)
                dl.AddImage((nint)icons, blip.Center - half, blip.Center + half,
                    MinimapUiLaw.PartyDotUvMin, MinimapUiLaw.PartyDotUvMax);
            else
                dl.AddCircleFilled(blip.Center, MathF.Max(2f, 2.5f * s), 0xff00a5ff);
        }
        dl.PopClipRect();
    }

    private static void DrawMinimapPlayerArrow(ImDrawListPtr dl, float orientation, Vector2 center, float s)
    {
        // FrameXML supplies this as MinimapArrow.mdx. The world renderer does not render M2s
        // inside ImGui, so preserve its directional behavior with the same centered pointer.
        Vector2 forward = new(-MathF.Sin(orientation), -MathF.Cos(orientation));
        Vector2 right = new(-forward.Y, forward.X);
        Vector2 tip = center + forward * (8f * s);
        Vector2 tail = center - forward * (5f * s);
        dl.AddTriangleFilled(tip, tail + right * (4f * s), tail - right * (4f * s), 0xffffffff);
        dl.AddTriangle(tip, tail + right * (4f * s), tail - right * (4f * s), 0xff202020, 1.25f * s);
    }

    /// <summary>
    /// Vanilla's tracked-resource leg: PLAYER_TRACK_RESOURCES -> GAMEOBJECT_QUERY chest
    /// lockId -> Lock.dbc SKILL LockType -> ObjectIcons cell 0 (gold), 8 px.
    /// </summary>
    private MinimapResourceTooltipCandidate? DrawMinimapResourceDots(
        ImDrawListPtr dl, WorldEntity player, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float s, float? radiusOverride = null)
    {
        uint mask = player.Fields.PlayerTrackResources;
        EnsureLockCatalog();
        if (mask == 0 || _locks is null)
        {
            ReportMinimapResourceSet(mask, []);
            return null;
        }
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float pixelsPerYard = (mapMax.X - mapMin.X) / (radiusYards * 2f);
        Vector2 center = (mapMin + mapMax) * .5f;
        uint icons = _gameplayArt?.Handle(@"Interface\Minimap\ObjectIcons") ?? 0;
        var visible = new List<(WorldEntity Go, GameObjectTemplate Template, float Distance, Vector2 Dot)>();
        foreach (WorldEntity go in _entities.Entities.Values.Where(e => e.IsGameObject))
        {
            RequireGameObjectTemplate(go);
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template) ||
                !_locks.MatchesResourceMask(template.LockId, mask)) continue;
            float distance = Vector3.Distance(go.Position, playerPosition);
            if (distance > radiusYards) continue;
            Vector2 dot = center + new Vector2(
                -(go.Position.Y - playerPosition.Y),
                -(go.Position.X - playerPosition.X)) * pixelsPerYard;
            visible.Add((go, template, distance, dot));
        }
        MinimapResourceTooltipCandidate? hoveredTooltip = null;
        dl.PushClipRect(mapMin, mapMax, true);
        // The aperture is round, so a blip is in or out by RADIUS — a rect clip alone let one
        // sit in a corner outside the ring. Same 66px cut the party dots already use, and the
        // in-range test above is by yards, which is not the same thing once zoom is in play.
        float blipLimit = PainterlyUi ? float.MaxValue : 66f * s;
        foreach (var row in visible)
        {
            if (Vector2.DistanceSquared(row.Dot, center) > blipLimit * blipLimit) continue;
            Vector2 half = new(4f * s);
            uint tint = MinimapBlipTint(row.Go.Position);
            if (icons != 0)
                dl.AddImage((nint)icons, row.Dot - half, row.Dot + half,
                    new Vector2(0f, 0f), new Vector2(.25f, .25f), tint);
            else dl.AddCircleFilled(row.Dot, 3f * s, 0xff00d8ff);
            if (ImGui.IsMouseHoveringRect(row.Dot - half, row.Dot + half, false))
                hoveredTooltip = new(row.Go.Guid, row.Template.Name);
        }
        dl.PopClipRect();
        ReportMinimapResourceSet(mask, visible);
        return hoveredTooltip;
    }

    private MinimapResourceTooltipCandidate? DrawMinimapLandmarks(
        ImDrawListPtr draw, Vector3 playerPosition, Vector2 mapMin, Vector2 mapMax,
        float? radiusOverride = null)
    {
        EnsureMinimapAreaPois();
        if (_minimapAreaPois is null || _gameplayArt is null) return null;

        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        AreaPoiSelection selection = _minimapAreaPois.Select(
            checked((uint)Math.Max(0, _config.Start.Map)),
            new Vector2(playerPosition.X, playerPosition.Y), radiusYards);
        float side = mapMax.X - mapMin.X;
        float pixelsPerYard = side / (radiusYards * 2f);
        Vector2 center = (mapMin + mapMax) * .5f;
        uint poiIcons = _gameplayArt.Handle(@"Interface\Minimap\POIIcons");
        uint arrow = _gameplayArt.Handle(@"Interface\Minimap\ROTATING-MINIMAPARROW");
        MinimapResourceTooltipCandidate? hovered = null;

        draw.PushClipRect(mapMin, mapMax, true);
        foreach (AreaPoiInfo poi in selection.Icons)
        {
            if (poiIcons == 0 || !AreaPoiCatalog.TryIconUv(
                    poi.Icon, out Vector2 uvMin, out Vector2 uvMax)) continue;
            Vector2 offset = new(
                -(poi.Position.Y - playerPosition.Y),
                -(poi.Position.X - playerPosition.X));
            Vector2 at = center + offset * pixelsPerYard;
            Vector2 half = new(MinimapUiLaw.LandmarkIconSize(side) * .5f);
            draw.AddImage((nint)poiIcons, at - half, at + half, uvMin, uvMax);
            if (ImGui.IsMouseHoveringRect(at - half, at + half, false))
                hovered = LandmarkTooltip(poi);
        }
        draw.PopClipRect();

        if (arrow == 0) return hovered;
        foreach (AreaPoiArrow row in selection.Arrows)
        {
            AreaPoiInfo poi = row.Poi;
            Vector2 direction = new(
                -(poi.Position.Y - playerPosition.Y),
                -(poi.Position.X - playerPosition.X));
            Vector2 at = MinimapUiLaw.LandmarkArrowCenter(center, direction, side);
            float size = MinimapUiLaw.LandmarkArrowSize(side);
            Vector2 half = new(size * .5f);
            float rotation = MathF.Atan2(direction.X, -direction.Y);
            AddRotatedMinimapImage(draw, arrow, at, size, rotation);
            if (ImGui.IsMouseHoveringRect(at - half, at + half, false))
                hovered = LandmarkTooltip(poi);
        }
        return hovered;

        static MinimapResourceTooltipCandidate LandmarkTooltip(in AreaPoiInfo poi) =>
            new(0xA000_0000_0000_0000UL | poi.Id, poi.Name);
    }

    private MinimapResourceTooltipCandidate? DrawMinimapGossipPoi(
        ImDrawListPtr draw, Vector3 playerPosition, Vector2 mapMin, Vector2 mapMax,
        float scale, float? radiusOverride = null)
    {
        if (_gossipPoi is not { } poi || _gameplayArt is null ||
            _gossipPoiMapId != checked((uint)Math.Max(0, _config.Start.Map))) return null;

        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        Vector2 center = MinimapUiLaw.GossipPoiCenter(
            new(playerPosition.X, playerPosition.Y), poi.Position,
            (mapMin + mapMax) * .5f, side, radiusYards);
        float size = MinimapUiLaw.LandmarkIconSize(side);
        Vector2 half = new(size * .5f);
        uint texture = _gameplayArt.Handle(@"Interface\Minimap\POIIcons");
        draw.PushClipRect(mapMin, mapMax, true);
        if (texture != 0 && AreaPoiCatalog.TryIconUv(
                poi.Icon, out Vector2 uvMin, out Vector2 uvMax))
            draw.AddImage((nint)texture, center - half, center + half, uvMin, uvMax);
        else
            draw.AddCircleFilled(center, MathF.Max(3f, 4f * scale), 0xff2020ff);
        draw.PopClipRect();

        return ImGui.IsMouseHoveringRect(center - half, center + half, false)
            ? new MinimapResourceTooltipCandidate(0, poi.Name)
            : null;
    }

    private void EnsureMinimapAreaPois()
    {
        if (_minimapAreaPoisLoaded) return;
        _minimapAreaPoisLoaded = true;
        try
        {
            if (_mpq is not null) _minimapAreaPois = AreaPoiCatalog.Load(_mpq);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[minimap] AreaPOI load failed: {error.Message}");
        }
    }

    private static void AddRotatedMinimapImage(ImDrawListPtr draw, uint texture,
        Vector2 center, float size, float clockwiseRadians)
    {
        float half = size * .5f;
        (float sin, float cos) = MathF.SinCos(clockwiseRadians);
        Vector2 Rotate(float x, float y) => center + new Vector2(
            x * cos - y * sin, x * sin + y * cos);
        draw.AddImageQuad((nint)texture,
            Rotate(-half, -half), Rotate(half, -half),
            Rotate(half, half), Rotate(-half, half),
            Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY);
    }

    private MinimapResourceTooltipCandidate? DrawMinimapTrackedCreatureDots(
        ImDrawListPtr draw, WorldEntity player, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float scale, float? radiusOverride = null)
    {
        uint trackMask = player.Fields.PlayerTrackCreatures;
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float pixelsPerYard = (mapMax.X - mapMin.X) / (radiusYards * 2f);
        Vector2 center = (mapMin + mapMax) * .5f;
        float blipLimit = PainterlyUi ? float.MaxValue : 66f * scale;
        uint icons = _gameplayArt?.Handle(@"Interface\Minimap\ObjectIcons") ?? 0;
        MinimapResourceTooltipCandidate? hovered = null;

        draw.PushClipRect(mapMin, mapMax, true);
        foreach (WorldEntity unit in _entities.Entities.Values.Where(candidate =>
                     candidate.Guid != player.Guid && (candidate.IsCreature || candidate.IsPlayer)))
        {
            if (_questStatuses.GetValueOrDefault(unit.Guid) == MinimapUiLaw.QuestRewardStatus)
                continue;
            EnsureUnitNameRequested(unit);
            uint creatureType = MinimapTrackedCreatureType(unit);
            if (!MinimapUiLaw.ShowTrackedCreatureDot(
                    trackMask, creatureType, unit.Fields.DynamicFlags)) continue;
            Vector3 position = UnitWorldPosition(unit);
            if (Vector3.Distance(position, playerPosition) > radiusYards) continue;
            Vector2 dot = center + new Vector2(
                -(position.Y - playerPosition.Y),
                -(position.X - playerPosition.X)) * pixelsPerYard;
            if (Vector2.DistanceSquared(dot, center) > blipLimit * blipLimit) continue;
            Vector2 half = new(4f * scale);
            uint tint = MinimapBlipTint(position);
            if (icons != 0)
                draw.AddImage((nint)icons, dot - half, dot + half,
                    MinimapUiLaw.TrackedCreatureDotUvMin,
                    MinimapUiLaw.TrackedCreatureDotUvMax, tint);
            else
                draw.AddCircleFilled(dot, 3f * scale, tint == 0xffffffff ? 0xff0000ff : tint);
            if (ImGui.IsMouseHoveringRect(dot - half, dot + half, false))
                hovered = new(unit.Guid, unit.IsPlayer
                    ? _playerNames.GetValueOrDefault(unit.Guid, "Player")
                    : ResolveCreatureOrPetName(unit, $"Creature {unit.Entry}"));
        }
        draw.PopClipRect();
        return hovered;
    }

    private uint MinimapTrackedCreatureType(WorldEntity unit)
    {
        byte form = unit.Fields.ShapeshiftForm;
        if (form != 0 && _shapeshiftForms?.TryGet(form, out ShapeshiftFormInfo row) == true)
            return row.CreatureType > 0 ? (uint)row.CreatureType : 7;
        if (unit.IsPlayer) return 7;
        return _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? info) &&
               info is not null ? info.CreatureType : 0;
    }

    /// <summary>Blip indoor verdicts, cached on quantized position: the identity
    /// resolve walks WMO collision geometry, and it used to run PER BLIP PER
    /// FRAME — inside a large building with several tracked blips that alone
    /// cost multiple milliseconds every frame, a big slice of why interiors
    /// felt heavy. A blip moves slowly relative to a quarter second.</summary>
    private readonly Dictionary<(int X, int Y, int Z), (bool Indoors, double Expires)>
        _minimapBlipIndoors = [];

    private uint MinimapBlipTint(Vector3 position)
    {
        bool playerIndoors = _minimapAreaInterior is not null;
        double now = NowSeconds();
        (int, int, int) key = ((int)MathF.Round(position.X),
            (int)MathF.Round(position.Y), (int)MathF.Round(position.Z));
        if (!_minimapBlipIndoors.TryGetValue(key,
                out (bool Indoors, double Expires) verdict) || now >= verdict.Expires)
        {
            if (_minimapBlipIndoors.Count > 512) _minimapBlipIndoors.Clear();
            verdict = (_wmo?.ResolveAreaMinimapIdentity(position,
                _terrain?.SampleHeight(position.X, position.Y)) is not null, now + 0.25);
            _minimapBlipIndoors[key] = verdict;
        }
        return MinimapUiLaw.BlipTint(playerIndoors, verdict.Indoors);
    }

    private void DrawMinimapCorpseMarker(ImDrawListPtr draw, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float scale, float? radiusOverride = null)
    {
        if (_corpseLocation is not { } corpse ||
            !DeathFrameUiLaw.TryMinimapCorpseRect(corpse.DisplayMap,
                checked((uint)Math.Max(0, _config.Start.Map)), playerPosition, corpse.Position,
                mapMin, mapMax, radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom),
                out DeathFrameUiLaw.ScreenRect rect)) return;
        uint texture = _gameplayArt?.Handle(DeathFrameUiLaw.CorpseMarkerPath) ?? 0;
        if (texture == 0) return;
        draw.AddImage((nint)texture, rect.Min, rect.Min + rect.Size,
            DeathFrameUiLaw.CorpseUvMin, DeathFrameUiLaw.CorpseUvMax);
    }

    private void DrawMinimapCorpseArrow(ImDrawListPtr draw, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float? radiusOverride = null)
    {
        if (_corpseLocation is not { } corpse || corpse.DisplayMap < 0 ||
            (uint)corpse.DisplayMap != checked((uint)Math.Max(0, _config.Start.Map))) return;
        uint arrow = _gameplayArt?.Handle(@"Interface\Minimap\ROTATING-MINIMAPARROW") ?? 0;
        if (arrow == 0) return;
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        MinimapPartyBlip blip = MinimapUiLaw.PartyBlip(
            new(playerPosition.X, playerPosition.Y),
            new(corpse.Position.X, corpse.Position.Y),
            (mapMin + mapMax) * .5f, side, radiusYards);
        if (blip.IsArrow)
            AddRotatedMinimapImage(draw, arrow, blip.Center, blip.Size, blip.Rotation);
    }

    /// <summary>
    /// The reference's quest blip is deliberately narrower than its overhead punctuation:
    /// only DIALOG_STATUS_REWARD2 (7) gets ObjectIcons cell 3, as an 8px gold dot.
    /// </summary>
    private MinimapResourceTooltipCandidate? DrawMinimapQuestDots(
        ImDrawListPtr dl, Vector3 playerPosition, Vector2 mapMin, Vector2 mapMax, float s,
        float? radiusOverride = null)
    {
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float pixelsPerYard = (mapMax.X - mapMin.X) / (radiusYards * 2f);
        Vector2 center = (mapMin + mapMax) * .5f;
        uint icons = _gameplayArt?.Handle(@"Interface\Minimap\ObjectIcons") ?? 0;
        MinimapResourceTooltipCandidate? hovered = null;
        float blipLimit = PainterlyUi ? float.MaxValue : 66f * s;

        dl.PushClipRect(mapMin, mapMax, true);
        foreach ((ulong guid, uint status) in _questStatuses)
        {
            if (!MinimapUiLaw.ShowQuestDot(status) ||
                !_entities.TryGet(guid, out WorldEntity npc) || !npc.IsCreature) continue;
            Vector3 position = UnitWorldPosition(npc);
            if (Vector3.Distance(position, playerPosition) > radiusYards) continue;
            Vector2 dot = center + new Vector2(
                -(position.Y - playerPosition.Y),
                -(position.X - playerPosition.X)) * pixelsPerYard;
            if (Vector2.DistanceSquared(dot, center) > blipLimit * blipLimit) continue;
            Vector2 half = new(4f * s);
            uint tint = MinimapBlipTint(position);
            if (icons != 0)
                dl.AddImage((nint)icons, dot - half, dot + half,
                    MinimapUiLaw.QuestDotUvMin, MinimapUiLaw.QuestDotUvMax, tint);
            else
                dl.AddCircleFilled(dot, 3f * s, 0xff00d8ff);
            if (!ImGui.IsMouseHoveringRect(dot - half, dot + half, false)) continue;
            EnsureUnitNameRequested(npc);
            hovered = new(guid,
                _creatureNames.GetValueOrDefault(npc.Entry, $"Creature {npc.Entry}"));
        }
        dl.PopClipRect();
        return hovered;
    }

    private static GameTooltipOwnerKey MinimapResourceGameTooltipOwner(ulong guid)
        => new("minimap-resource-dot", guid);

    private bool UpdateAndQueueMinimapResourceTooltip(
        MinimapResourceTooltipCandidate? hovered)
    {
        // The frame guard must precede Claim so an out-of-stratum minimap pass cannot change
        // ownership. A faded runtime is retained locally only to prepare this frame's callback;
        // callbacks themselves never cross frames or read live world/template state.
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;

        if (hovered is MinimapResourceTooltipCandidate candidate)
        {
            if (candidate.FrameXmlLines is { Length: > 0 } lines)
            {
                _minimapResourceTooltip = null;
                Vector2 anchor = ImGui.GetMousePos() + new Vector2(14f, 16f) * GameplayUiScale();
                return OfferOwnerAnchoredSharedGameTooltip(
                    MinimapResourceGameTooltipOwner(candidate.Guid), lines, anchor, Vector2.Zero);
            }
            GameTooltipOwnerToken token = ClaimSharedGameTooltip(
                MinimapResourceGameTooltipOwner(candidate.Guid));
            if (!ClearSharedGameTooltip(token))
                throw new InvalidOperationException(
                    "A freshly claimed minimap resource tooltip rejected its exact clear.");
            _minimapResourceTooltip = new(token, candidate.Name);
        }
        else if (_minimapResourceTooltip is MinimapResourceTooltipRuntime departing)
        {
            if (!SharedGameTooltipIsOwned(departing.Token))
            {
                _minimapResourceTooltip = null;
                return false;
            }
            BeginSharedGameTooltipFade(departing.Token, _sharedTooltipFrameTime,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_minimapResourceTooltip is not MinimapResourceTooltipRuntime runtime)
            return false;
        if (!SharedGameTooltipIsOwned(runtime.Token))
        {
            _minimapResourceTooltip = null;
            return false;
        }

        GameTooltipLifecycleState lifecycle = SharedGameTooltipSnapshot().Lifecycle;
        if (!lifecycle.Visible || lifecycle.Alpha <= 0f)
        {
            _minimapResourceTooltip = null;
            return false;
        }

        string preparedName = runtime.Name;
        float preparedAlpha = lifecycle.Alpha;
        return QueueSharedGameTooltipRenderer(runtime.Token,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds),
            () => DrawMinimapResourceTooltip(preparedName, preparedAlpha));
    }

    private static void DrawMinimapResourceTooltip(string preparedName, float preparedAlpha)
    {
        // Keep the established SetTooltip cursor seat at full alpha and during fade. The frozen
        // client retains its last pointer seat after departure, but there is no public ImGui
        // position capture for SetTooltip; inventing an offset would move the approved anchor.
        if (preparedAlpha >= 1f)
        {
            ImGui.SetTooltip(preparedName);
            return;
        }

        float alpha = Math.Clamp(preparedAlpha, 0f, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * alpha);
        try
        {
            ImGui.SetTooltip(preparedName);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void ReportMinimapResourceSet(uint mask,
        IReadOnlyList<(WorldEntity Go, GameObjectTemplate Template, float Distance, Vector2 Dot)> visible)
    {
        string signature = $"{mask:X8}:" + string.Join(',', visible.OrderBy(x => x.Go.Guid)
            .Select(x => $"{x.Go.Guid:X16}/{x.Template.LockId}"));
        if (signature == _minimapResourceSignature) return;
        _minimapResourceSignature = signature;
        EmitInterface("gathering", "minimap-nodes", "RENDERED", _net?.PlayerGuid ?? 0,
            $"mask=0x{mask:X8};herbs={visible.Count(x => _locks?.ResourceLockType(x.Template.LockId) == 2)};" +
            $"minerals={visible.Count(x => _locks?.ResourceLockType(x.Template.LockId) == 3)};total={visible.Count};" +
            $"nodes={string.Join('|', visible.OrderBy(x => x.Distance).Select(x => $"{x.Go.Entry}:{SanitizeEvidence(x.Template.Name)}:{x.Distance:F2}"))}");
    }

    private void DrawMinimapTexture(ImDrawListPtr dl, Vector2 root, Vector2 offset, Vector2 size,
        string path, Vector2 uv0, Vector2 uv1)
    {
        uint handle = _gameplayArt?.Handle(path) ?? 0;
        if (handle == 0) return;
        float s = GameplayUiScale();
        Vector2 min = (root + offset) * s;
        dl.AddImage((nint)handle, min, min + size * s, uv0, uv1);
    }

    private void DrawMinimapButton(ImDrawListPtr dl, Vector2 logicalMin, string texture, Action click)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s, max = min + new Vector2(32) * s;
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, max);
        if (ImGui.IsMouseHoveringRect(min, max, false) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();
    }

    private void DrawMinimapZoomButton(ImDrawListPtr dl, Vector2 logicalMin, bool zoomIn,
        bool enabled, Action click)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s, max = min + new Vector2(32) * s;
        bool hovered = enabled && ImGui.IsMouseHoveringRect(min, max, false);
        string stem = zoomIn ? "ZoomIn" : "ZoomOut";
        string state = !enabled ? "Disabled" : hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left)
            ? "Down" : "Up";
        uint handle = _gameplayArt?.Handle($@"Interface\Minimap\UI-Minimap-{stem}Button-{state}") ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, max);
        if (hovered)
        {
            // ADD art (alphaDepth 0: black background, opaque everywhere). Through the plain
            // Handle it was drawn as authored under ImGui's SRC_ALPHA blend, so the black
            // background REPLACED the button - a solid 32x32 black square on hover, which is
            // exactly how it was reported. AdditiveHandle is the re-encode every other
            // highlight in the client already goes through.
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Minimap\UI-Minimap-ZoomButton-Highlight") ?? 0;
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                PlayUiSound(zoomIn ? MinimapUiLaw.ZoomInSound : MinimapUiLaw.ZoomOutSound,
                    "ui.minimap");
                click();
            }
        }
    }

    private void StepMinimapZoom(bool zoomIn, bool insideWmo, bool playSound = true)
    {
        int old = insideWmo ? _minimapInsideZoom : _minimapZoom;
        int next = MinimapUiLaw.StepZoom(old, zoomIn);
        if (insideWmo) _minimapInsideZoom = next;
        else _minimapZoom = next;
        if (playSound && next != old)
            PlayUiSound(zoomIn ? MinimapUiLaw.ZoomInSound : MinimapUiLaw.ZoomOutSound,
                "ui.minimap");
    }

    private void SetMinimapVisible(bool visible)
    {
        if (_minimapVisible == visible) return;
        _minimapVisible = visible;
        PlayUiSound(visible ? MinimapUiLaw.OpenSound : MinimapUiLaw.CloseSound, "ui.minimap");
    }

    private void DrawMovingMinimap(ImDrawListPtr dl, Vector2 mapMin, Vector2 mapMax,
        MinimapProjection player, bool circular)
    {
        EnsureMinimapTileMap();
        float halfTiles = MinimapUiLaw.OutdoorRadius(_minimapZoom) /
            MinimapProjection.TileWorldSize;
        float pixelsPerTile = (mapMax.X - mapMin.X) / (2f * halfTiles);
        Vector2 center = (mapMin + mapMax) * .5f;
        string mapName = _adts?.MapName ?? _config.Start.MapName;

        // The old implementation cropped one BLP and clamped its UVs. At the
        // edge of that BLP the crop stopped moving. Vanilla instead presents a
        // continuous plane of neighboring minimap tiles beneath a fixed arrow.
        //
        // The aperture is a DISC unless painterly has squared it. FrameXML declares the map as
        // `<Minimap name="Minimap">` — a dedicated widget type the engine crops to a circle —
        // and `UI-Minimap-Border` is only the ornament ringing it, so a rectangular clip alone
        // leaks the tile plane into the four corners.
        float circleRadius = circular ? (mapMax.X - mapMin.X) * .5f : 0f;
        Vector3 dayTint = _atmosphere.AuthoredLightingReady
            ? MinimapUiLaw.OutdoorDayTint(_atmosphere.AmbientColor, _atmosphere.SunColor)
            : Vector3.One;
        uint packedTint = ImGui.ColorConvertFloat4ToU32(new Vector4(dayTint, 1f));
        dl.PushClipRect(mapMin, mapMax, true);
        for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
        for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
        {
            int column = player.TileColumn + columnOffset;
            int row = player.TileRow + rowOffset;
            string logical = $@"{mapName}\map{column:00}_{row:00}.blp";
            if (_minimapTileMap?.TryGetValue(logical, out string? hashed) != true) continue;
            uint handle = _gameplayArt!.Handle(@"textures\Minimap\" + hashed);
            if (handle == 0) continue;
            Vector2 min = center + new Vector2(
                column - (player.TileColumn + player.TileU),
                row - (player.TileRow + player.TileV)) * pixelsPerTile;
            Vector2 max = min + new Vector2(pixelsPerTile);
            if (max.X <= mapMin.X || max.Y <= mapMin.Y || min.X >= mapMax.X || min.Y >= mapMax.Y) continue;
            if (circular)
                AddImageCircleClipped(dl, handle, min, max, center, circleRadius,
                    tint: packedTint);
            else dl.AddImage((nint)handle, min, max, Vector2.Zero, Vector2.One, packedTint);
        }
        dl.PopClipRect();
    }

    /// <summary>
    /// Draw the current WMO's authored interior tiles. These replace, rather
    /// than overlay, the outdoor ADT plane. Tiles are baked in model space, so
    /// each local corner is transformed through the one placed WMO instance
    /// before north-up projection; this also handles rotated inns/buildings.
    /// </summary>
    private void DrawInteriorMinimap(
        ImDrawListPtr dl, Vector2 mapMin, Vector2 mapMax, Vector3 playerWorld,
        WmoRenderer.InteriorMinimapContext context, bool circular)
    {
        EnsureMinimapTileMap();
        if (_minimapTileMap is null || _gameplayArt is null ||
            WmoMinimapProjection.Stem(context.InstancePath) is not string stem)
            return;

        float radius = WmoMinimapProjection.ZoomRadiusYards[
            Math.Clamp(_minimapInsideZoom, 0, WmoMinimapProjection.ZoomRadiusYards.Length - 1)];
        Vector2 center = (mapMin + mapMax) * .5f;
        float pixelsPerYard = (mapMax.X - mapMin.X) / (2f * radius);
        float circleRadius = circular ? (mapMax.X - mapMin.X) * .5f : 0f;

        // Current floor last. Other reached floors are sorted low-to-high so
        // the current room wins any overlap exactly as the client does.
        var ordered = context.ReachableGroups
            .OrderBy(group => group.GroupIndex == context.GroupIndex
                ? float.PositiveInfinity
                : (group.LocalMin.Z + group.LocalMax.Z) * .5f - context.LocalPosition.Z)
            .ToArray();

        var tiles = new List<(
            uint Texture,
            Vector2 P00,
            Vector2 P10,
            Vector2 P11,
            Vector2 P01)>();

        foreach (var group in ordered)
        {
            var (columns, spanX) = WmoMinimapProjection.AxisGrid(group.LocalMax.X - group.LocalMin.X);
            var (rows, spanY) = WmoMinimapProjection.AxisGrid(group.LocalMax.Y - group.LocalMin.Y);
            float z = (group.LocalMin.Z + group.LocalMax.Z) * .5f;
            for (int column = 0; column < columns; column++)
            for (int row = 0; row < rows; row++)
            {
                string logical = WmoMinimapProjection.LogicalTile(
                    stem, group.GroupIndex, column, row);
                if (!_minimapTileMap.TryGetValue(logical, out string? hashed)) continue;

                float x0 = group.LocalMin.X + column * spanX;
                float x1 = x0 + spanX;
                float y0 = group.LocalMin.Y + row * spanY;
                float y1 = y0 + spanY;
                Vector2 p00 = WmoMinimapProjection.ToScreen(
                    Vector3.Transform(new Vector3(x0, y0, z), context.LocalToWorld),
                    playerWorld, center, pixelsPerYard);
                Vector2 p10 = WmoMinimapProjection.ToScreen(
                    Vector3.Transform(new Vector3(x1, y0, z), context.LocalToWorld),
                    playerWorld, center, pixelsPerYard);
                Vector2 p11 = WmoMinimapProjection.ToScreen(
                    Vector3.Transform(new Vector3(x1, y1, z), context.LocalToWorld),
                    playerWorld, center, pixelsPerYard);
                Vector2 p01 = WmoMinimapProjection.ToScreen(
                    Vector3.Transform(new Vector3(x0, y1, z), context.LocalToWorld),
                    playerWorld, center, pixelsPerYard);

                float extent = MathF.Max(spanX, spanY) * pixelsPerYard;
                Vector2 tileCenter = (p00 + p10 + p11 + p01) * .25f;
                if (Vector2.Distance(tileCenter, center) > (mapMax.X - mapMin.X) * .5f + extent)
                    continue;

                // The bounded preparation path decodes only nearby tiles and
                // publishes them incrementally. Cull again here so drawing a
                // large connected city stays proportional to this zoom window.
                string artPath = @"textures\Minimap\" + hashed;
                uint texture;
                if (_assetWorkers is null)
                    texture = _gameplayArt.Handle(artPath);
                else if (!_gameplayArt.TryHandle(artPath, out texture))
                    continue;
                if (texture == 0) continue;

                tiles.Add((texture, p00, p10, p11, p01));
            }
        }

        bool anyTile = tiles.Count > 0;
        // Interior and outdoor map families are mutually exclusive. Even a
        // connector room with no authored image clears to black; revealing the
        // underlying ADT here is the original Ironforge mountain-map bug.
        dl.PushClipRect(mapMin, mapMax, true);
        if (circular)
            dl.AddCircleFilled(center, circleRadius, 0xff000000, 64);
        else
            dl.AddRectFilled(mapMin, mapMax, 0xff000000);

        bool compositeDrawn = false;
        if (anyTile && _minimapInteriorComposite is not null)
        {
            float side = mapMax.X - mapMin.X;
            var compositeTiles = tiles.Select(tile => new InteriorMinimapComposite.Tile(
                tile.Texture,
                WmoMinimapProjection.ToCompositeClip(tile.P00, center, side),
                WmoMinimapProjection.ToCompositeClip(tile.P10, center, side),
                WmoMinimapProjection.ToCompositeClip(tile.P11, center, side),
                WmoMinimapProjection.ToCompositeClip(tile.P01, center, side))).ToArray();
            compositeDrawn = _minimapInteriorComposite.Render(compositeTiles);
            if (compositeDrawn)
            {
                float gutter = (1f - WmoMinimapProjection.CompositeBlitFraction) * .5f;
                Vector2 uvMin = new(gutter, 1f - gutter);
                Vector2 uvMax = new(1f - gutter, gutter);
                if (circular)
                    AddImageCircleClipped(dl, _minimapInteriorComposite.TextureHandle,
                        mapMin, mapMax, center, circleRadius, uvMin: uvMin, uvMax: uvMax);
                else
                    dl.AddImage((nint)_minimapInteriorComposite.TextureHandle,
                        mapMin, mapMax, uvMin, uvMax, 0xffffffff);
            }
        }

        if (anyTile && !compositeDrawn)
        {
            foreach (var tile in tiles)
            {
                if (circular)
                    AddImageQuadCircleClipped(dl, tile.Texture,
                        tile.P00, tile.P10, tile.P11, tile.P01, center, circleRadius);
                else
                    dl.AddImageQuad((nint)tile.Texture,
                        tile.P00, tile.P10, tile.P11, tile.P01,
                        Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY,
                        0xffffffff);
            }
        }
        dl.PopClipRect();

        string signature = anyTile
            ? $"{context.InstanceId}:{context.GroupIndex}:{stem}:{ordered.Length}"
            : $"missing:{context.InstanceId}:{context.GroupIndex}:{stem}";
        if (signature != _minimapInteriorSignature)
        {
            _minimapInteriorSignature = signature;
            Console.WriteLine(anyTile
                ? $"[minimap] interior {Path.GetFileName(context.InstancePath)} " +
                  $"group={context.GroupIndex} reached={ordered.Length}"
                : $"[minimap] no authored interior tile for {Path.GetFileName(context.InstancePath)} " +
                  $"group={context.GroupIndex}; keeping interior map black");
        }
    }

    /// <summary>
    /// Arbitrary rotated textured quad clipped to the minimap disc. The outdoor
    /// helper can derive UV from an axis-aligned rect; WMO tiles rotate with
    /// their placement, so this variant carries UV through the polygon clip.
    /// </summary>
    private static void AddImageQuadCircleClipped(
        ImDrawListPtr dl, uint texture,
        Vector2 p00, Vector2 p10, Vector2 p11, Vector2 p01,
        Vector2 centre, float radius, int segments = 48)
    {
        if (texture == 0 || radius <= 0f) return;
        int capacity = segments + 8;
        Span<(Vector2 P, Vector2 Uv)> polygon = stackalloc (Vector2, Vector2)[capacity];
        Span<(Vector2 P, Vector2 Uv)> clipped = stackalloc (Vector2, Vector2)[capacity];
        polygon[0] = (p00, Vector2.Zero);
        polygon[1] = (p10, Vector2.UnitX);
        polygon[2] = (p11, Vector2.One);
        polygon[3] = (p01, Vector2.UnitY);
        int count = 4;

        for (int edge = 0; edge < segments && count >= 3; edge++)
        {
            (float sin, float cos) = MathF.SinCos(edge * MathF.Tau / segments);
            var normal = new Vector2(cos, sin);
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                var current = polygon[i];
                var previous = polygon[(i + count - 1) % count];
                float currentDistance = Vector2.Dot(current.P - centre, normal) - radius;
                float previousDistance = Vector2.Dot(previous.P - centre, normal) - radius;
                if (currentDistance <= 0f)
                {
                    if (previousDistance > 0f)
                        clipped[written++] = Intersect(
                            previous, current, previousDistance, currentDistance);
                    clipped[written++] = current;
                }
                else if (previousDistance <= 0f)
                    clipped[written++] = Intersect(
                        previous, current, previousDistance, currentDistance);
            }
            count = written;
            clipped[..count].CopyTo(polygon);
        }
        if (count < 3) return;

        uint baseIndex = dl._VtxCurrentIdx;
        dl.PushTextureID((nint)texture);
        dl.PrimReserve((count - 2) * 3, count);
        for (int i = 0; i < count; i++)
            dl.PrimWriteVtx(polygon[i].P, polygon[i].Uv, 0xffffffff);
        for (int i = 2; i < count; i++)
        {
            dl.PrimWriteIdx((ushort)baseIndex);
            dl.PrimWriteIdx((ushort)(baseIndex + i - 1));
            dl.PrimWriteIdx((ushort)(baseIndex + i));
        }
        dl.PopTextureID();

        static (Vector2 P, Vector2 Uv) Intersect(
            (Vector2 P, Vector2 Uv) from, (Vector2 P, Vector2 Uv) to,
            float fromDistance, float toDistance)
        {
            float t = fromDistance / (fromDistance - toDistance);
            return (Vector2.Lerp(from.P, to.P, t), Vector2.Lerp(from.Uv, to.Uv, t));
        }
    }

    /// <summary>
    /// Draw an axis-aligned textured rect clipped to a CIRCLE.
    ///
    /// ImGui has no circular clip — clip rects are rectangles — and this backend cannot borrow
    /// AddImageRounded, which emits a single textured fan triangle here (the same finding that
    /// sent the round portraits through a baked alpha mask instead). A render target is the
    /// other way out, but the tiles are draw-list images, not GL draws, so there is nothing to
    /// redirect into one. So the quad is clipped ANALYTICALLY: Sutherland-Hodgman against the
    /// half-planes of a regular polygon whose edges are tangent to the circle, emitted as a
    /// textured fan. UVs are exact rather than approximated — the rect is axis-aligned, so
    /// inverting the map is one lerp per component.
    /// </summary>
    private static void AddImageCircleClipped(ImDrawListPtr dl, uint texture,
        Vector2 rectMin, Vector2 rectMax, Vector2 centre, float radius,
        int segments = 48, uint tint = 0xffffffff,
        Vector2? uvMin = null, Vector2? uvMax = null)
    {
        Vector2 size = rectMax - rectMin;
        if (size.X <= 0f || size.Y <= 0f || radius <= 0f) return;

        // Clipping a convex polygon by N half-planes adds at most one vertex per plane.
        int capacity = segments + 8;
        Span<Vector2> poly = stackalloc Vector2[capacity];
        Span<Vector2> clipped = stackalloc Vector2[capacity];
        poly[0] = rectMin;
        poly[1] = new Vector2(rectMax.X, rectMin.Y);
        poly[2] = rectMax;
        poly[3] = new Vector2(rectMin.X, rectMax.Y);
        int count = 4;

        for (int i = 0; i < segments && count >= 3; i++)
        {
            (float sin, float cos) = MathF.SinCos(i * MathF.Tau / segments);
            var normal = new Vector2(cos, sin);
            int written = 0;
            for (int j = 0; j < count; j++)
            {
                Vector2 current = poly[j];
                Vector2 previous = poly[(j + count - 1) % count];
                float dCurrent = Vector2.Dot(current - centre, normal) - radius;
                float dPrevious = Vector2.Dot(previous - centre, normal) - radius;
                if (dCurrent <= 0f)
                {
                    if (dPrevious > 0f)
                        written = Cross(clipped, written, previous, current, dPrevious, dCurrent);
                    clipped[written++] = current;
                }
                else if (dPrevious <= 0f)
                    written = Cross(clipped, written, previous, current, dPrevious, dCurrent);
            }
            count = written;
            clipped[..count].CopyTo(poly);
        }
        if (count < 3) return;

        Vector2 uv0 = uvMin ?? Vector2.Zero;
        Vector2 uv1 = uvMax ?? Vector2.One;
        // ImGui's own convex-fill contract: capture the base index BEFORE reserving.
        uint baseIndex = dl._VtxCurrentIdx;
        dl.PushTextureID((nint)texture);
        dl.PrimReserve((count - 2) * 3, count);
        for (int i = 0; i < count; i++)
        {
            Vector2 p = poly[i];
            dl.PrimWriteVtx(p,
                uv0 + new Vector2((p.X - rectMin.X) / size.X,
                    (p.Y - rectMin.Y) / size.Y) * (uv1 - uv0), tint);
        }
        for (int i = 2; i < count; i++)
        {
            dl.PrimWriteIdx((ushort)baseIndex);
            dl.PrimWriteIdx((ushort)(baseIndex + i - 1));
            dl.PrimWriteIdx((ushort)(baseIndex + i));
        }
        dl.PopTextureID();

        static int Cross(Span<Vector2> into, int at, Vector2 from, Vector2 to,
            float dFrom, float dTo)
        {
            float t = dFrom / (dFrom - dTo);
            into[at] = from + (to - from) * t;
            return at + 1;
        }
    }

    private void UpdateMinimapArea(
        MinimapProjection projection, WmoRenderer.AreaMinimapIdentity? interior = null)
    {
        EnsureAreaTableForMinimap();
        string mapName = _adts?.MapName ?? _config.Start.MapName;
        if (!string.Equals(mapName, _minimapAreaMap, StringComparison.OrdinalIgnoreCase))
        {
            _minimapAreaMap = mapName;
            _minimapAreaId = 0;
            _minimapReportedZoneId = 0;
            _zoneTextIdentity = null;
        }
        uint areaId = 0;
        if (interior is { } areaInterior)
        {
            EnsureWmoAreaTableForMinimap();
            var wmoAreaKey = (
                areaInterior.RootWmoId, areaInterior.NameSetId, areaInterior.GroupWmoId);
            if (!_minimapWmoAreaCache.TryGetValue(wmoAreaKey, out areaId))
            {
                areaId = _wmoAreas?.Resolve(
                    wmoAreaKey.RootWmoId,
                    wmoAreaKey.NameSetId,
                    wmoAreaKey.GroupWmoId)?.AreaTableId ?? 0;
                _minimapWmoAreaCache[wmoAreaKey] = areaId;
            }
        }
        if (areaId == 0 &&
            _adts?.TryPeek(projection.TileColumn, projection.TileRow, out var adt) == true)
            areaId = projection.AreaId(adt);
        if (areaId == 0)
            areaId = _areas?.SingleZoneForMap((uint)_config.Start.Map) ?? 0;
        if (areaId == 0) return;
        _minimapAreaId = areaId;

        uint zoneId = _areas?.ParentZoneId(areaId) ?? 0;
        if (zoneId == 0) zoneId = areaId;
        string zoneText = _areas?.AreaName(zoneId) ?? "";
        string subZoneText = areaId == zoneId ? "" : _areas?.AreaName(areaId) ?? "";
        if (interior is { } namingInterior && _wmoAreas is not null)
        {
            WmoAreaRow? defaultRow = _wmoAreas.DefaultRow(
                namingInterior.RootWmoId, namingInterior.NameSetId);
            WmoAreaRow? groupRow = _wmoAreas.GroupRow(
                namingInterior.RootWmoId, namingInterior.NameSetId, namingInterior.GroupWmoId);
            if (defaultRow is not null)
            {
                uint defaultArea = defaultRow.AreaTableId != 0 ? defaultRow.AreaTableId : areaId;
                string defaultName = defaultRow.Name.Length > 0
                    ? defaultRow.Name : _areas?.AreaName(defaultArea) ?? "";
                if (defaultName.Length > 0 && defaultName != subZoneText)
                {
                    zoneText = defaultName;
                    subZoneText = "";
                }
            }
            if (groupRow?.Name is { Length: > 0 } groupName &&
                !string.Equals(groupName, zoneText, StringComparison.Ordinal))
                subZoneText = groupName;
        }
        MinimapZonePvpType zonePvp = MinimapZonePvpType.Unknown;
        if (_entities.TryGet(ControlledGuid, out WorldEntity zonePlayer))
        {
            MinimapZonePvpInfo pvp = ResolveAreaPvp(zonePlayer, areaId, zoneId);
            zonePvp = pvp.Type;
            UpdateZoneTextIdentity(new(zoneId, zoneText, subZoneText, interior is not null), pvp);
        }
        // The minimap deliberately follows the commanded body, but
        // CMSG_ZONEUPDATE belongs to the logged-in session character. During
        // possession/Free View those are different roles; reporting this
        // displayed zone would move session-owned PvP/rest/zone bookkeeping to
        // wherever the bot or observer happens to be.
        uint reportedZoneId = ResolveSessionBodyZone(zoneId);
        if (reportedZoneId == 0 || reportedZoneId == _minimapReportedZoneId) return;
        _minimapReportedZoneId = reportedZoneId;
        _net?.ZoneUpdate(reportedZoneId);
        // The reference re-walks its zone channels inside UpdateZoneText on every zone change,
        // right before ZONE_CHANGED_NEW_AREA fires — the same edge as the ZONEUPDATE above.
        RefreshZoneChannels(reportedZoneId);
        EmitInterface("minimap", "area", "UPDATED", _net?.PlayerGuid ?? 0,
            $"map={mapName};tile={projection.TileColumn}|{projection.TileRow};" +
            $"chunk={projection.ChunkX}|{projection.ChunkY};area={areaId};" +
            (interior is not { } logInterior ? "" :
                $"wmo={logInterior.RootWmoId}/{logInterior.NameSetId}/{logInterior.GroupWmoId};") +
            $"subZone={_areas?.AreaName(areaId)};displayZone={zoneId};" +
            $"reportedSessionZone={reportedZoneId};pvp={zonePvp}");
        Console.WriteLine($"[minimap] area={areaId} '{_areas?.AreaName(areaId)}' " +
                          $"displayZone={zoneId} reportedSessionZone={reportedZoneId}");
    }

    /// <summary>
    /// Resolve the zone the session-owned wire may report. The displayed zone
    /// is already exact when the controller embodies the own character; every
    /// detached/possessed state resolves from the streamed session body.
    /// </summary>
    private uint ResolveSessionBodyZone(uint displayedZoneId)
    {
        if (ControlledGuid == LocalPlayerGuid && ControllerOwnsControlledBodyPose)
            return displayedZoneId;
        if (!TryGetSessionBodyPose(out WorldBodyPose sessionBody)) return 0;

        Vector3 position = sessionBody.Position;
        Vector3 probe = position + new Vector3(0f, 0f, 1.7f);
        float? terrainZ = _terrain?.SampleHeight(probe.X, probe.Y);
        uint areaId = 0;

        if (_wmo?.ResolveAreaMinimapIdentity(position, terrainZ) is { } interior)
        {
            EnsureWmoAreaTableForMinimap();
            areaId = _wmoAreas?.Resolve(
                interior.RootWmoId, interior.NameSetId, interior.GroupWmoId)?.AreaTableId ?? 0;
        }

        if (areaId == 0)
        {
            MinimapProjection projection = MinimapProjection.FromWorld(position);
            if (_adts?.TryPeek(projection.TileColumn, projection.TileRow, out var adt) == true)
                areaId = projection.AreaId(adt);
        }

        if (areaId == 0)
            areaId = _areas?.SingleZoneForMap((uint)_config.Start.Map) ?? 0;
        if (areaId == 0) return 0;
        uint zoneId = _areas?.ParentZoneId(areaId) ?? 0;
        return zoneId != 0 ? zoneId : areaId;
    }

    private MinimapZonePvpInfo ResolveAreaPvp(WorldEntity player, uint leafArea, uint zoneArea)
    {
        uint? leafFlags = _areas?.Flags(leafArea);
        uint? zoneFaction = _areas?.FactionGroupMask(zoneArea);
        FactionTemplateRow? ownFaction = null;
        bool factionKnown = _factions?.TryGet(player.Fields.FactionTemplate,
            out ownFaction!) == true;
        return MinimapUiLaw.ZonePvp(
            leafFlags ?? 0, zoneFaction ?? 0,
            factionKnown ? ownFaction!.FriendGroupMask : 0,
            factionKnown ? ownFaction!.EnemyGroupMask : 0,
            leafFlags.HasValue && zoneFaction.HasValue && factionKnown);
    }

    private void EnsureAreaTableForMinimap()
    {
        if (_areasLoaded) return;
        _areasLoaded = true;
        try
        {
            byte[]? bytes = _mpq?.ReadFile(AreaTableCatalog.MpqPath);
            if (bytes is not null) _areas = AreaTableCatalog.Parse(bytes);
        }
        catch (Exception e) { Console.WriteLine($"[minimap] AreaTable load failed: {e.Message}"); }
    }

    /// <summary>
    /// WMO room containment is collision-backed and substantially more expensive
    /// than ordinary HUD projection. Resolve it at 10 Hz (and immediately after
    /// a renderer swap or zoom change), while continuing to
    /// project the cached room tiles around the player's exact per-frame pose.
    /// </summary>
    private WmoRenderer.InteriorMinimapContext? ResolveMinimapInterior(
        Vector3 feet, Vector3 probe, float radius, float? terrainZ)
    {
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        bool rendererChanged = !ReferenceEquals(_minimapInteriorRenderer, _wmo);
        int rendererVersion = _wmo?.LiquidVersion ?? -1;
        bool placementsChanged = rendererVersion != _minimapInteriorWmoVersion;
        bool terrainChanged = terrainZ.HasValue != _minimapInteriorTerrainZ.HasValue ||
            (terrainZ is float currentTerrain && _minimapInteriorTerrainZ is float cachedTerrain &&
             MathF.Abs(currentTerrain - cachedTerrain) > 0.5f);
        if (rendererChanged || placementsChanged || terrainChanged ||
            MathF.Abs(radius - _minimapInteriorRadius) > 0.01f ||
            now >= _minimapInteriorNextResolveAt)
        {
            _minimapInteriorRenderer = _wmo;
            _minimapInteriorWmoVersion = rendererVersion;
            _minimapInteriorTerrainZ = terrainZ;
            _minimapInteriorRadius = radius;
            _minimapInteriorContext = _wmo?.ResolveInteriorMinimapContext(
                probe, radius, terrainZ);
            _minimapAreaInterior = _wmo?.ResolveAreaMinimapIdentity(feet, terrainZ);
            _minimapInteriorNextResolveAt = now + 0.10;
        }
        return _minimapInteriorContext;
    }

    private IReadOnlyList<string> MinimapInteriorArtPaths(
        WmoRenderer.InteriorMinimapContext context, Vector3 playerWorld, float radius)
    {
        EnsureMinimapTileMap();
        if (_minimapTileMap is null ||
            WmoMinimapProjection.Stem(context.InstancePath) is not string stem)
            return [];

        var paths = new List<string>();
        foreach (var group in context.ReachableGroups)
        {
            var (columns, spanX) = WmoMinimapProjection.AxisGrid(
                group.LocalMax.X - group.LocalMin.X);
            var (rows, spanY) = WmoMinimapProjection.AxisGrid(
                group.LocalMax.Y - group.LocalMin.Y);
            float z = (group.LocalMin.Z + group.LocalMax.Z) * .5f;
            for (int column = 0; column < columns; column++)
            for (int row = 0; row < rows; row++)
            {
                Vector3 localCenter = new(
                    group.LocalMin.X + (column + .5f) * spanX,
                    group.LocalMin.Y + (row + .5f) * spanY,
                    z);
                Vector3 worldCenter = Vector3.Transform(localCenter, context.LocalToWorld);
                if (Vector2.Distance(
                        new Vector2(worldCenter.X, worldCenter.Y),
                        new Vector2(playerWorld.X, playerWorld.Y)) >
                    radius + MathF.Max(spanX, spanY))
                    continue;
                string logical = WmoMinimapProjection.LogicalTile(
                    stem, group.GroupIndex, column, row);
                if (_minimapTileMap.TryGetValue(logical, out string? hashed))
                    paths.Add(@"textures\Minimap\" + hashed);
            }
        }
        return paths;
    }

    private bool AdvanceMinimapTexturePreparation(
        WmoRenderer.InteriorMinimapContext context, Vector3 playerWorld, float radius)
    {
        if (_gameplayArt is null || _assetWorkers is null) return true;
        bool ready = true;
        bool adoptedThisFrame = false;
        foreach (string path in MinimapInteriorArtPaths(context, playerWorld, radius))
        {
            if (_gameplayArt.IsResolved(path)) continue;
            if (!_minimapPreparedTextureTasks.TryGetValue(path, out var task))
            {
                task = _assetWorkers.Run(() => _gameplayArt.Prepare(path));
                _minimapPreparedTextureTasks.Add(path, task);
                ready = false;
                continue;
            }
            if (!task.IsCompleted)
            {
                ready = false;
                continue;
            }
            if (task.IsFaulted || task.IsCanceled)
            {
                _ = task.Exception;
                _gameplayArt.MarkMissing(path);
                _minimapPreparedTextureTasks.Remove(path);
                continue;
            }
            if (task.Result is GameplayArt.PreparedTexture prepared)
            {
                if (adoptedThisFrame)
                {
                    ready = false;
                    continue;
                }
                try
                {
                    _gameplayArt.Adopt(prepared);
                }
                catch (Exception ex)
                {
                    // A UI texture upload must not take down the render loop.
                    // Cache the path as absent and keep the authored interior
                    // black, which is the same safe fallback as a missing BLP.
                    _gameplayArt.MarkMissing(path);
                    Console.WriteLine(
                        $"[minimap] texture adoption failed for {path}: {ex.Message}");
                }
                _minimapPreparedTextureTasks.Remove(path);
                adoptedThisFrame = true;
            }
            else
            {
                _gameplayArt.MarkMissing(path);
                _minimapPreparedTextureTasks.Remove(path);
            }
        }
        return ready;
    }

    private void DrainMinimapTexturePreparation()
    {
        try
        {
            Task.WhenAll(_minimapPreparedTextureTasks.Values).GetAwaiter().GetResult();
        }
        catch { /* best-effort UI warmup must never obstruct shutdown */ }
        _minimapPreparedTextureTasks.Clear();
    }

    private void EnsureWmoAreaTableForMinimap()
    {
        if (_wmoAreasLoaded) return;
        _wmoAreasLoaded = true;
        try
        {
            byte[]? bytes = _mpq?.ReadFile(WmoAreaCatalog.MpqPath);
            if (bytes is not null) _wmoAreas = WmoAreaCatalog.Parse(bytes);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[minimap] WMOAreaTable load failed: {e.Message}");
        }
    }

    private void EnsureMinimapTileMap()
    {
        if (_minimapTileMap is not null) return;
        _minimapTileMap = new(StringComparer.OrdinalIgnoreCase);
        byte[]? bytes = _mpq?.ReadFile(@"textures\Minimap\md5translate.trs");
        if (bytes is null) return;
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string[] parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) _minimapTileMap[parts[0]] = parts[1];
        }
    }
}
