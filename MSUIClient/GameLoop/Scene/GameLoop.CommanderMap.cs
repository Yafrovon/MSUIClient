using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ── RTS commander map (M while the free view is up) ─────────────────────────
// A purpose-built strategic surface that reuses the vanilla parchment art and
// exact zone silhouettes: dual-continent hover shows the live bots/players
// census (SMSG_SUI_ZONE_INTEL), while drill-in shows the owner's own
// units, and click-to-fly for both units and open ground.
public sealed partial class GameLoop
{
    // ── tier-2 RTS worldstate (SMSG_SUI_RTS_STATE; all zeros in vanilla) ─────
    private byte _rtsMode;                           // 0 vanilla, 1 RTS match
    private byte _rtsModules;                        // bit0 honor, 1 heroes, 2 territory, 3 dungeons
    private readonly RtsFactionWire[] _rtsFactions = new RtsFactionWire[2];
    private readonly List<RtsHeroWire> _rtsHeroes = [];
    private readonly List<RtsDungeonWire> _rtsDungeons = [];
    // The faction force remains separate from the real WoW party roster. It is a
    // zone-scoped, server-filtered census used by Commander and, when capability
    // gated, as the Free View faction-selection affordance. It never makes a bot
    // a real party member or grants authority by itself.
    private readonly Dictionary<ulong, RtsForceUnitWire> _rtsForces = [];
    private readonly Dictionary<ulong, RtsForceUnitWire> _rtsForceStaging = [];
    private uint _rtsForceRequestSeed;
    private uint _rtsForceRequestId;
    private uint _rtsForceRequestZone;
    private uint _rtsForceRequestAfter;
    private bool _rtsForceLoading;
    private double _rtsForceRequestAt;
    private uint _rtsForcePublishedZone;
    private ushort _rtsForcePublishedTotal;
    private double _rtsForceAt;
    private ulong _rtsSelectedForceGuid;

    /// <summary>0 = alliance, 1 = horde (the session character's side).</summary>
    private int OwnTeamIndex => _net?.Player?.Race is 2 or 5 or 6 or 8 ? 1 : 0;

    private bool _commanderMapOpen;
    private uint _commanderMapZone;                  // 0 = both-continent overview, else AreaId of the drilled-in zone
    private readonly Dictionary<uint, RtsTerritoryZoneWire> _zoneIntel = [];
    private readonly List<RtsZoneIntelUnitWire> _commanderUnits = [];
    private double _zoneIntelAt;                     // NowSeconds() of the last SMSG; 0 = never
    private double _zoneIntelSentAt;                 // request throttle; 0 forces an immediate send
    private (float X, float Y, float Alt)? _commanderFlySettle;  // ground snap pending terrain streaming
    private double _commanderFlySettleAt;
    private WorldMapOverlayCatalog? _worldMapOverlays;
    private WorldMapHighlightCatalog? _worldMapHighlights;
    private WorldMapZoneHitCatalog? _worldMapHits;
    private bool _worldMapSupportingDataLoaded;
    private string? _commanderNotice;                // one-line transient message in the footer
    private double _commanderNoticeAt;
    private byte _rtsPendingAction;
    private ulong _rtsPendingSubject;
    private double _rtsPendingActionAt;

    private const double CommanderIntelRefreshSeconds = 5.0;
    private const double RtsForceRequestTimeoutSeconds = 8.0;
    private const double RtsActionTimeoutSeconds = 5.0;
    private const float CommanderFlyAltitude = 60f;  // ground click: commander vantage
    private const float CommanderUnitAltitude = 25f; // unit click: close enough to read the fight

    /// <summary>Toggle from the M binding (UpdateWorldMapInput routes here while the free view is up).</summary>
    private void ToggleCommanderMap()
    {
        _commanderMapOpen = !_commanderMapOpen;
        if (!_commanderMapOpen) return;
        _zoneIntelSentAt = 0;                        // force the census request out this frame
        _commanderMapZone = 0;
    }

    /// <summary>
    /// Session boundary reset. Map transfers retain the same match snapshot, but
    /// logout/disconnect must never let one world's RTS controls leak into the next.
    /// </summary>
    private void ResetCommanderState()
    {
        _commanderMapOpen = false;
        _commanderMapZone = 0;
        _zoneIntel.Clear();
        _commanderUnits.Clear();
        _zoneIntelAt = 0;
        _zoneIntelSentAt = 0;
        _commanderFlySettle = null;
        _commanderFlySettleAt = 0;
        _rtsMode = 0;
        _rtsModules = 0;
        Array.Clear(_rtsFactions);
        _rtsHeroes.Clear();
        _rtsDungeons.Clear();
        ResetRtsForceRoster();
        _rtsPendingAction = 0;
        _rtsPendingSubject = 0;
        _rtsPendingActionAt = 0;
        _commanderNotice = null;
        _commanderNoticeAt = 0;
    }

    /// <summary>
    /// Per-frame commander-map work, called unconditionally from the frame loop:
    /// the census request cadence (map open only) and the fly-to ground-snap
    /// latch, which must keep running after the map closes on a unit click.
    /// </summary>
    private void UpdateCommanderMap()
    {
        double now = NowSeconds();
        if (_rtsPendingAction != 0 && now - _rtsPendingActionAt > RtsActionTimeoutSeconds)
        {
            _rtsPendingAction = 0;
            _rtsPendingSubject = 0;
            _rtsPendingActionAt = 0;
            CommanderShowNotice("No answer from the server (RTS hero action).");
        }

        if (!_freeView)
        {
            _commanderFlySettle = null;              // never teleport a controller that drives a real body
            return;
        }

        if (_commanderFlySettle is (float sx, float sy, float alt))
        {
            if (_terrain?.SampleHeight(sx, sy) is float ground && _controller is not null)
            {
                _controller.Teleport(sx, sy, ground + alt);
                _window.Camera.Target = _controller.Position;
                _freecamCamSentAt = 0;               // the eye must follow the altitude snap immediately
                _commanderFlySettle = null;
            }
            else if (NowSeconds() - _commanderFlySettleAt > 5.0)
                _commanderFlySettle = null;          // hole in the terrain data; 500 is above everything anyway
        }

        if (_commanderMapOpen && _net is { IsInWorld: true })
        {
            if (now - _zoneIntelSentAt > CommanderIntelRefreshSeconds && _net.SuiZoneIntel())
            {
                _zoneIntelSentAt = now;
                _net.SuiRtsState();   // same cadence: mode, honor pools, heroes, objectives
            }

            uint forceZone = _commanderMapZone;
            if (CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules) && forceZone != 0)
            {
                // A page chain belongs to exactly one drilled zone. If the commander
                // changes zones, abandon the hidden staging set immediately; the new
                // request id makes any late page from the old zone harmless.
                if (_rtsForceLoading && _rtsForceRequestZone != forceZone)
                {
                    _rtsForceLoading = false;
                    _rtsForceStaging.Clear();
                }
                if (_rtsForceLoading && now - _rtsForceRequestAt > RtsForceRequestTimeoutSeconds)
                {
                    _rtsForceLoading = false;
                    _rtsForceStaging.Clear();
                    CommanderShowNotice("Faction force roster timed out.");
                }
                if (!_rtsForceLoading &&
                    (_rtsForcePublishedZone != forceZone || now - _rtsForceAt > CommanderIntelRefreshSeconds))
                    BeginRtsForceRosterLoad(forceZone);
            }
        }
    }

    private void ResetRtsForceRoster()
    {
        _rtsForces.Clear();
        _rtsForceStaging.Clear();
        _rtsForceRequestId = 0;
        _rtsForceRequestZone = 0;
        _rtsForceRequestAfter = 0;
        _rtsForceLoading = false;
        _rtsForceRequestAt = 0;
        _rtsForcePublishedZone = 0;
        _rtsForcePublishedTotal = 0;
        _rtsForceAt = 0;
        _rtsSelectedForceGuid = 0;
    }

    private uint NextRtsForceRequestId()
    {
        do { _rtsForceRequestSeed++; } while (_rtsForceRequestSeed == 0);
        return _rtsForceRequestSeed;
    }

    private void BeginRtsForceRosterLoad(uint zoneId)
    {
        if (zoneId == 0 || !CanUseFactionForceRoster() ||
            _net is not { IsInWorld: true }) return;
        uint requestId = NextRtsForceRequestId();
        _rtsForceRequestId = requestId;
        _rtsForceRequestZone = zoneId;
        _rtsForceRequestAfter = 0;
        _rtsForceStaging.Clear();
        _rtsForceLoading = true;
        _rtsForceRequestAt = NowSeconds();
        if (!_net.SuiForceRoster(requestId, zoneId, 0))
        {
            _rtsForceLoading = false;
            _rtsForceStaging.Clear();
        }
    }

    /// <summary>
    /// One strict page of the faction-force roster. Pages accumulate out of view;
    /// the selected zone is replaced only after the server publishes its end cursor.
    /// </summary>
    private void ApplySuiForceRoster(byte[] body)
    {
        try
        {
            RtsForceRosterPage page = RtsWire.ParseForceRoster(body);
            if (!CanUseFactionForceRoster())
                return;
            if (!_rtsForceLoading || page.RequestId != _rtsForceRequestId ||
                page.ZoneId != _rtsForceRequestZone)
                return; // stale response from a zone the commander already left

            foreach (RtsForceUnitWire unit in page.Units)
            {
                uint guidLow = unchecked((uint)unit.Guid);
                if (guidLow <= _rtsForceRequestAfter || !_rtsForceStaging.TryAdd(unit.Guid, unit))
                    throw new InvalidDataException("force roster page repeated or reversed a GUID");
            }

            if (page.NextGuidLow != 0)
            {
                _rtsForceRequestAfter = page.NextGuidLow;
                _rtsForceRequestAt = NowSeconds();
                if (_net?.SuiForceRoster(page.RequestId, page.ZoneId, page.NextGuidLow) != true)
                {
                    _rtsForceLoading = false;
                    _rtsForceStaging.Clear();
                }
                return;
            }

            _rtsForces.Clear();
            foreach ((ulong guid, RtsForceUnitWire unit) in _rtsForceStaging)
                _rtsForces.Add(guid, unit);
            _rtsForceStaging.Clear();
            _rtsForcePublishedZone = page.ZoneId;
            _rtsForcePublishedTotal = page.Total;
            _rtsForceAt = NowSeconds();
            _rtsForceLoading = false;
            if (_rtsSelectedForceGuid != 0 && !_rtsForces.ContainsKey(_rtsSelectedForceGuid))
                _rtsSelectedForceGuid = 0;
        }
        catch (Exception e)
        {
            _rtsForceLoading = false;
            _rtsForceStaging.Clear();
            Console.WriteLine($"[commander] SMSG_SUI_FORCE_ROSTER parse failed: {e.Message}");
        }
    }

    /// <summary>SMSG_SUI_ZONE_INTEL: stride-versioned census + own-unit blocks (see SUI_WIRE_PROTOCOL.md).</summary>
    private void ApplySuiZoneIntel(byte[] body)
    {
        try
        {
            RtsZoneIntelSnapshot next = RtsWire.ParseZoneIntel(body);

            // Publish one complete census. A malformed future packet must not mix
            // partial new zones with units or territory state from the previous one.
            _zoneIntel.Clear();
            foreach (RtsTerritoryZoneWire zone in next.Zones) _zoneIntel.Add(zone.ZoneId, zone);
            _commanderUnits.Clear();
            _commanderUnits.AddRange(next.Units);
            _zoneIntelAt = NowSeconds();
        }
        catch (Exception e)
        {
            // A malformed packet must be loud: silence here is indistinguishable
            // from "the world is empty" (the bot-bags lesson, round 13).
            Console.WriteLine($"[commander] SMSG_SUI_ZONE_INTEL parse failed: {e.Message}");
        }
    }

    /// <summary>SMSG_SUI_RTS_STATE: mode + module flags + stride-versioned faction/hero/dungeon blocks.</summary>
    private void ApplySuiRtsState(byte[] body)
    {
        try
        {
            // Parse the entire packet before publishing any field. A malformed
            // future row cannot mix a new mode/pool with the previous roster.
            RtsStateSnapshot next = RtsWire.ParseState(body);
            _rtsMode = next.Mode;
            _rtsModules = next.Modules;
            Array.Copy(next.Factions, _rtsFactions, _rtsFactions.Length);
            _rtsHeroes.Clear();
            _rtsHeroes.AddRange(next.Heroes);
            _rtsDungeons.Clear();
            _rtsDungeons.AddRange(next.Dungeons);

            if (!CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules))
                ClearRtsTerritoryCapture();

            if (!CanUseFactionForceRoster())
                ResetRtsForceRoster();

            // Names stay lazy. A state push may arrive with Commander closed and
            // must not fan out vanilla name queries for every declared hero.
        }
        catch (Exception e)
        {
            Console.WriteLine($"[commander] SMSG_SUI_RTS_STATE parse failed: {e.Message}");
        }
    }

    /// <summary>SMSG_SUI_RTS_ACTION_RESULT: surface the verdict in the commander notice line.</summary>
    private void ApplySuiRtsActionResult(byte[] body)
    {
        try
        {
            RtsActionResultWire response = RtsWire.ParseActionResult(body);
            byte action = response.Action;
            byte result = response.Result;
            long poolAfter = response.PoolAfter;
            if (_rtsPendingAction == action && _rtsPendingSubject == response.SubjectGuid)
            {
                _rtsPendingAction = 0;
                _rtsPendingSubject = 0;
                _rtsPendingActionAt = 0;
            }
            string what = action switch { 1 => "Declare hero", 2 => "Upgrade hero", 3 => "Revive hero", _ => $"Action {action}" };
            string verdict = result switch
            {
                0 => "done",
                1 => "not enough Honor",
                2 => "no free hero slot",
                3 => "invalid target",
                _ => "not available",
            };
            string honor = CommanderMapUiLaw.ShowHonor(_rtsMode, _rtsModules)
                ? $"  (Honor {poolAfter:n0})" : string.Empty;
            CommanderShowNotice($"{what}: {verdict}{honor}");
            _net?.SuiRtsState(); // authoritative pool/roster refresh without waiting for the 5 s census tick
            _rtsForceAt = 0;     // hero/dead flags on the selected-zone force rows also changed
        }
        catch (Exception e)
        {
            Console.WriteLine($"[commander] SMSG_SUI_RTS_ACTION_RESULT parse failed: {e.Message}");
        }
    }

    /// <summary>
    /// Fly the free-view rig to a world point. If the destination terrain is not
    /// resident yet, travel in at 500 (the CreatorTeleport convention) and let the
    /// settle latch snap down once tiles stream. Zeroing the cam-heartbeat stamp
    /// makes the server's streaming eye follow on the next frame — no new wire.
    /// </summary>
    private void CommanderFlyTo(float worldX, float worldY, float altitude)
    {
        if (_controller is null || !_freeView) return;
        if (_terrain?.SampleHeight(worldX, worldY) is float ground)
        {
            _controller.Teleport(worldX, worldY, ground + altitude);
            _commanderFlySettle = null;
        }
        else
        {
            _controller.Teleport(worldX, worldY, 500f);
            _commanderFlySettle = (worldX, worldY, altitude);
            _commanderFlySettleAt = NowSeconds();
        }
        _window.Camera.Target = _controller.Position;
        _freecamCamSentAt = 0;
    }

    private void CommanderShowNotice(string text)
    {
        _commanderNotice = text;
        _commanderNoticeAt = NowSeconds();
    }

    private void TrySendCommanderHeroAction(
        CommanderMapUiLaw.HeroAction action, ulong subjectGuid)
    {
        if (action == CommanderMapUiLaw.HeroAction.None || subjectGuid == 0 || _net is null)
            return;
        if (RefuseTacticalFreezeLiveCommand("changing RTS hero state")) return;
        if (RefuseTacticalFrozenActor(subjectGuid, "change its RTS hero state")) return;
        if (_rtsPendingAction != 0)
        {
            CommanderShowNotice("An RTS hero action is already waiting for the server.");
            return;
        }
        if (!_net.SuiRtsAction((byte)action, subjectGuid))
        {
            CommanderShowNotice("RTS hero action could not be sent.");
            return;
        }

        _rtsPendingAction = (byte)action;
        _rtsPendingSubject = subjectGuid;
        _rtsPendingActionAt = NowSeconds();
        CommanderShowNotice($"{action} hero: waiting for the server...");
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    private readonly record struct CommanderZoneRect(
        WorldMapAreaInfo Zone, Vector2 Min, Vector2 Max);

    private readonly record struct CommanderOverviewMap(
        uint MapId, string Name, WorldMapAreaInfo Continent,
        Vector2 CardMin, Vector2 CardMax, Vector2 MapMin, Vector2 MapSize,
        Vector4 Crop, IReadOnlyList<CommanderZoneRect> Zones);

    private void DrawCommanderMapFrame()
    {
        if (!_commanderMapOpen || _gameplayArt is null || _net is null) return;
        EnsureWorldMapAreas();
        EnsureWorldMapSupportingData();
        EnsureAreaTableForMinimap();
        if (_worldMapAreas is null) { _commanderMapOpen = false; return; }

        Vector2 disp = ImGui.GetIO().DisplaySize;
        float s = MathF.Min(GameplayUiScale(),
            MathF.Min(disp.X / 1024f, disp.Y / 720f));
        s = Math.Clamp(s, 0.65f, 2.5f);
        float margin = Math.Clamp(MathF.Min(disp.X, disp.Y) * 0.0125f, 12f * s, 24f * s);
        float gap = Math.Clamp(disp.X * 0.008f, 12f * s, 22f * s);
        float headerH = Math.Clamp(disp.Y * 0.055f, 54f * s, 70f * s);
        float footerH = 34f * s;
        double now = NowSeconds();
        bool stale = _zoneIntelAt != 0 && now - _zoneIntelAt > CommanderIntelRefreshSeconds * 3;

        WorldMapAreaInfo drilledZone = default;
        bool drilled = _commanderMapZone != 0 &&
                       _worldMapAreas.TryGetArea(_commanderMapZone, out drilledZone);
        if (!drilled) _commanderMapZone = 0;

        PushCreatorStyle();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(disp);
        ImGui.Begin("###commander-map",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground);

        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, disp, 0xFF111416);

        Vector2 headerMin = new(margin, margin);
        Vector2 headerMax = new(disp.X - margin, margin + headerH);
        dl.AddRectFilled(headerMin, headerMax, 0xFA181410, 6f * s);
        dl.AddRect(headerMin, headerMax, 0x50FFFFFF, 6f * s);
        CmdText(dl, 20f * s, headerMin + new Vector2(14f, 7f) * s,
            VanillaGold, "COMMANDER");
        if (_rtsMode == 1)
        {
            string campaign = CommanderMapUiLaw.CampaignStatus(
                _rtsMode, _rtsModules, _rtsFactions[OwnTeamIndex].HonorPool);
            CmdText(dl, 11f * s, headerMin + new Vector2(14f, 33f) * s,
                0xFF60C0E0, campaign);
        }

        string intelText = _zoneIntelAt == 0 ? "Waiting for population data" :
            stale ? $"Data stale - {now - _zoneIntelAt:0}s" : $"Updated {now - _zoneIntelAt:0}s ago";
        uint intelColor = _zoneIntelAt == 0 ? 0xFF808080 : stale ? 0xFF3E8CE6 : 0xFF9CC49C;
        float intelSize = 12f * s;
        Vector2 close = new(headerMax.X - 34f * s, headerMin.Y + (headerH - 28f * s) * 0.5f);
        float intelWidth = GameText.MeasurePlain(intelText, intelSize, 1f);
        string centreTitle = drilled
            ? $"WORLD OVERVIEW  >  {(drilledZone.MapId == 1 ? "KALIMDOR" : "EASTERN KINGDOMS")}  >  {CommanderZoneName(drilledZone)}"
            : "WORLD OVERVIEW";
        float titleSize = (drilled ? 13f : 15f) * s;
        float titleLeft = headerMin.X + 210f * s;
        // The live Quest Tabletop opens from here (QUEST_TABLETOP.md §12): the same Command View, seen as the table.
        Vector2 tableButtonSize = new(96f, 22f);
        Vector2 tableButtonMin = new(close.X - intelWidth - 26f * s - tableButtonSize.X * s,
            headerMin.Y + (headerH - tableButtonSize.Y * s) * 0.5f);
        float titleRight = tableButtonMin.X - 12f * s;
        string fittedTitle = CommanderFitText(centreTitle,
            MathF.Max(80f * s, titleRight - titleLeft), titleSize);
        DrawCenteredText(dl, new Vector2((titleLeft + titleRight) * 0.5f,
                headerMin.Y + headerH * 0.5f),
            fittedTitle, titleSize, 0xFFF2E8D4);
        CmdText(dl, intelSize,
            new Vector2(close.X - intelWidth - 14f * s, headerMin.Y + (headerH - intelSize) * 0.5f),
            intelColor, intelText);
        if (VanillaButton(dl, "##commander-tabletop", "Tabletop", tableButtonMin, tableButtonSize, s))
            LiveTableOpen();
        DrawImageButton(dl, "##commander-close", close, new Vector2(28f) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _commanderMapOpen = false;

        Vector2 bodyMin = new(margin, headerMax.Y + gap);
        Vector2 bodyMax = new(disp.X - margin, disp.Y - margin - footerH);
        bool showHeroes = CommanderMapUiLaw.ShowHeroes(_rtsMode, _rtsModules);
        bool showTerritory = CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules);
        bool showCampaignRail = showHeroes || showTerritory;
        Vector2 strategicBodyMax = bodyMax;
        Vector2 heroesMin = default;
        if (showCampaignRail)
        {
            float bodyWidth = bodyMax.X - bodyMin.X;
            float railWidth = MathF.Min(bodyWidth * 0.34f,
                Math.Clamp(bodyWidth * 0.24f, 230f * s, 340f * s));
            strategicBodyMax.X -= railWidth + gap;
            heroesMin = new Vector2(strategicBodyMax.X + gap, bodyMin.Y);
        }
        uint playerMap = checked((uint)Math.Max(0, _config.Start.Map));
        if (!drilled)
            DrawCommanderWorldOverview(dl, bodyMin, strategicBodyMax, gap, s, playerMap, stale);
        else
            DrawCommanderDrill(dl, drilledZone, bodyMin, strategicBodyMax, gap, s, playerMap);
        if (showCampaignRail)
            DrawCommanderHeroesPanel(dl, heroesMin, bodyMax, s);

        bool noticeActive = _commanderNotice is not null && now - _commanderNoticeAt < 4.0;
        string hint = noticeActive ? _commanderNotice! : drilled
            ? "Click ground to fly - click a unit to enter the fight - right-click returns - M / Esc closes"
            : "Hover a zone for intel - click it to inspect - both continents are fully revealed - M / Esc closes";
        Vector2 legendPoint = new(margin + 5f * s,
            disp.Y - margin - footerH * 0.45f);
        DrawCommanderDiamond(dl, legendPoint, 4.5f * s, VanillaGold);
        CmdText(dl, 11f * s,
            legendPoint + new Vector2(10f, -5.5f) * s,
            0xFF96918Bu, "Camera  -  B Bots  -  P Players");
        DrawCenteredText(dl, new Vector2(disp.X * 0.5f, disp.Y - margin - footerH * 0.45f),
            hint, 12f * s, noticeActive ? 0xFF3EA6E6u : 0xFFB6B0A8u);

        if (drilled && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _commanderMapZone = 0;

        ImGui.End();
        PopCreatorStyle();
    }

    private void EnsureWorldMapSupportingData()
    {
        if (_worldMapSupportingDataLoaded) return;
        _worldMapSupportingDataLoaded = true;
        try
        {
            if (_mpq is not null)
            {
                _worldMapOverlays = WorldMapOverlayCatalog.Load(_mpq);
                if (_worldMapAreas is not null)
                {
                    _worldMapHighlights = WorldMapHighlightCatalog.Build(_worldMapAreas);
                    _worldMapHits = WorldMapZoneHitCatalog.Load(_mpq, _worldMapAreas);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[world-map] supporting data load failed: {e.Message}");
        }
    }

    private string CommanderZoneName(WorldMapAreaInfo zone) =>
        _areas?.ZoneName(zone.AreaId) is { Length: > 0 } name ? name : zone.Directory;

    private void DrawCommanderHeroesPanel(ImDrawListPtr dl, Vector2 min, Vector2 max, float s)
    {
        dl.AddRectFilled(min, max, 0xF01B1815, 5f * s);
        dl.AddRect(min, max, 0x60FFFFFF, 5f * s, ImDrawFlags.None, 1f * s);
        dl.PushClipRect(min, max, true);

        RtsFactionWire faction = _rtsFactions[OwnTeamIndex];
        float x = min.X + 12f * s;
        float right = max.X - 12f * s;
        float y = min.Y + 10f * s;
        CmdText(dl, 15f * s, new Vector2(x, y), VanillaGold, "CAMPAIGN");
        y += 24f * s;

        if (CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules))
        {
            CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                "STANDING SUPPLY");
            y += 17f * s;
            for (int team = 0; team < _rtsFactions.Length; team++)
            {
                RtsFactionWire row = _rtsFactions[team];
                bool own = team == OwnTeamIndex;
                string side = team == 0 ? "ALLIANCE" : "HORDE";
                uint color = own ? 0xFFF0E6D2u : 0xFF9A948Du;
                CmdText(dl, 11f * s, new Vector2(x, y), color,
                    own ? $"{side} (YOU)" : side);
                DrawCommanderRightAlignedText(dl, right, y,
                    $"{row.ControlledZones} ZONES", 10f * s, color);
                y += 16f * s;
                CmdText(dl, 10f * s, new Vector2(x + 8f * s, y), 0xFF9A948Du,
                    $"Ore {row.Ore:n0}   Skins {row.Skins:n0}   Herbs {row.Herbs:n0}");
                y += 17f * s;
                CmdText(dl, 9f * s, new Vector2(x + 8f * s, y),
                    row.HeroesFielded > row.HeroSlotCap ? 0xFF6E7FE6u : 0xFF77716Bu,
                    CommanderMapUiLaw.HeroCapacityStatus(row.HeroesFielded, row.HeroSlotCap));
                y += 20f * s;
            }
            y += 3f * s;
        }

        if (!CommanderMapUiLaw.ShowHeroes(_rtsMode, _rtsModules))
        {
            dl.PopClipRect();
            return;
        }

        CmdText(dl, 13f * s, new Vector2(x, y), VanillaGold, "HEROES");
        DrawCommanderRightAlignedText(dl, right, y,
            CommanderMapUiLaw.HeroCapacityStatus(faction.HeroesFielded, faction.HeroSlotCap),
            10f * s, faction.HeroesFielded > faction.HeroSlotCap ? 0xFF6E7FE6u : 0xFF9A948Du);
        y += 22f * s;
        if (CommanderMapUiLaw.ShowHonor(_rtsMode, _rtsModules))
        {
            CmdText(dl, 12f * s, new Vector2(x, y), 0xFF60C0E0,
                $"Faction Honor  {faction.HonorPool:n0}");
            y += 22f * s;
        }

        var roster = _rtsHeroes
            .Where(hero => hero.Team == OwnTeamIndex)
            .OrderBy(hero => hero.Dead)
            .ThenByDescending(hero => hero.HeroLevel)
            .ThenBy(hero => CommanderUnitName(hero.Guid), StringComparer.OrdinalIgnoreCase)
            .ToList();

        CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
            "SELECTED FACTION BOT");
        y += 18f * s;
        if (_rtsSelectedForceGuid != 0 &&
            _rtsForces.TryGetValue(_rtsSelectedForceGuid, out RtsForceUnitWire selected))
        {
            string selectedName = CommanderForceName(selected.Guid, request: true);
            RtsHeroWire? selectedHero = null;
            foreach (RtsHeroWire hero in roster)
                if (hero.Guid == selected.Guid) { selectedHero = hero; break; }
            byte heroLevel = selectedHero?.HeroLevel ?? 0;
            bool heroDead = selectedHero?.Dead ?? selected.HeroDead;
            bool heroStatePending = selected.DeclaredHero && selectedHero is null;
            bool actionEligible = !heroStatePending && (selected.Alive || heroDead);
            CommanderMapUiLaw.HeroAction action = CommanderMapUiLaw.HeroActionFor(
                _rtsMode, _rtsModules, ownFaction: true, eligibleBot: actionEligible,
                heroLevel, heroDead,
                declarationCapacityAvailable: faction.HeroesFielded < faction.HeroSlotCap);
            string status = heroStatePending ? "HERO SYNC" : heroLevel > 0
                ? $"HL {heroLevel}" + (heroDead ? " DEAD" : "")
                : $"L{selected.Level} {ClassIdName(selected.Class)}".TrimEnd();
            DrawCommanderHeroRow(dl, selected.Guid, selectedName, status, action,
                x, right, y, s);
            y += 28f * s;
            DrawCommanderTakeControlButton(dl, selected, x, right, y, s);
            y += 30f * s;
        }
        else
        {
            string prompt = !CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules)
                ? "Faction-force control is disabled."
                : _commanderMapZone == 0
                ? "Inspect a zone, then select a bot."
                : _rtsForceLoading ? "Loading this zone's faction force..."
                : "Select a faction bot on the map.";
            CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                CommanderFitText(prompt, right - x, 11f * s));
            y += 27f * s;
        }

        CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A, "DECLARED HEROES");
        y += 18f * s;
        const int maximumRosterRows = 6;
        int rosterVisible = Math.Min(maximumRosterRows, roster.Count);
        if (rosterVisible == 0)
        {
            CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                "No heroes declared.");
            y += 24f * s;
        }
        else
        {
            for (int i = 0; i < rosterVisible && y + 26f * s < max.Y; i++)
            {
                RtsHeroWire hero = roster[i];
                // A hero row exists only after the server's bot-only declaration
                // gate. Party membership and the local entity stream are irrelevant.
                CommanderMapUiLaw.HeroAction action = CommanderMapUiLaw.HeroActionFor(
                    _rtsMode, _rtsModules, ownFaction: true, eligibleBot: true,
                    hero.HeroLevel, hero.Dead);
                DrawCommanderHeroRow(dl, hero.Guid, CommanderForceName(hero.Guid, request: true),
                    hero.Dead ? $"HL {hero.HeroLevel}  DEAD" : $"HL {hero.HeroLevel}",
                    action, x, right, y, s);
                y += 28f * s;
            }
            if (roster.Count > rosterVisible && y + 14f * s < max.Y)
            {
                CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"+{roster.Count - rosterVisible} more heroes");
                y += 18f * s;
            }
        }

        dl.PopClipRect();
    }

    private void DrawCommanderTakeControlButton(ImDrawListPtr dl, RtsForceUnitWire unit,
        float x, float right, float y, float s)
    {
        bool differentInstance = unit.InstanceableMap && !unit.SameMapAndInstance;
        bool pending = _rtsForceTakeControlGuid != 0;
        bool tacticalBlocked = TacticalFreezeBlocksLiveCommands ||
            IsTacticalActorFrozen(unit.Guid);
        bool enabled = unit.Alive && !unit.Busy && unit.ControlEligibleNow &&
            !differentInstance && !pending && !tacticalBlocked;
        string label = !unit.Alive ? "DEAD" : unit.Busy ? "IN USE" :
            differentInstance ? "DIFFERENT INSTANCE" :
            !unit.ControlEligibleNow ? "UNAVAILABLE" :
            tacticalBlocked ? "FROZEN" :
            pending && _rtsForceTakeControlGuid == unit.Guid ? "LOCATING..." :
            pending ? "CONTROL PENDING" : "TAKE CONTROL";

        Vector2 min = new(x, y);
        Vector2 size = new(right - x, 22f * s);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##rts-take-control-{unit.Guid}", size);
        bool hover = ImGui.IsItemHovered() && enabled;
        uint fill = enabled ? (hover ? 0xE0445360u : 0xD034404Au) : 0xA0202020u;
        dl.AddRectFilled(min, min + size, fill, 3f * s);
        dl.AddRect(min, min + size, enabled ? 0xB060C0E0u : 0x30707070u, 3f * s);
        DrawCenteredText(dl, min + size * 0.5f, label, 9f * s,
            enabled ? 0xFFF0ECE4u : 0xFF707070u);
        if (clicked && enabled) BeginRtsForceTakeControl(unit);
    }

    private void DrawCommanderHeroRow(ImDrawListPtr dl, ulong guid, string name, string status,
        CommanderMapUiLaw.HeroAction action, float x, float right, float y, float s)
    {
        // Hero management is driven by the faction-force census, not the unrelated
        // CRPG party marquee. Never make party selection the RTS action subject.
        bool selected = guid == _rtsSelectedForceGuid;
        Vector2 rowMin = new(x - 4f * s, y - 3f * s);
        Vector2 rowMax = new(right + 4f * s, y + 22f * s);
        if (selected) dl.AddRectFilled(rowMin, rowMax, 0x243EA6E6, 3f * s);

        float buttonWidth = 70f * s;
        float buttonLeft = right - buttonWidth;
        CmdText(dl, 12f * s, new Vector2(x, y),
            selected ? 0xFFFFFFFFu : 0xFFE6E6E6u,
            CommanderFitText(name, buttonLeft - x - 54f * s, 12f * s));
        DrawCommanderRightAlignedText(dl, buttonLeft - 7f * s, y + 1f * s,
            status, 9f * s, status.Contains("DEAD", StringComparison.Ordinal) ? 0xFF6E7FE6u : 0xFF9A948Du);

        if (action == CommanderMapUiLaw.HeroAction.None) return;
        bool pending = _rtsPendingAction != 0;
        Vector2 buttonMin = new(buttonLeft, y - 2f * s);
        Vector2 buttonSize = new(buttonWidth, 20f * s);
        ImGui.SetCursorScreenPos(buttonMin);
        bool clicked = ImGui.InvisibleButton($"##rts-hero-{guid}-{(byte)action}", buttonSize);
        bool hover = ImGui.IsItemHovered() && !pending;
        uint fill = pending ? 0xA0202020u : hover ? 0xE0404A52u : 0xC030363Cu;
        dl.AddRectFilled(buttonMin, buttonMin + buttonSize, fill, 3f * s);
        dl.AddRect(buttonMin, buttonMin + buttonSize,
            pending ? 0x30707070u : 0xA060C0E0u, 3f * s);
        string label = pending && _rtsPendingSubject == guid ? "WAIT" : action.ToString().ToUpperInvariant();
        DrawCenteredText(dl, buttonMin + buttonSize * 0.5f, label, 9f * s,
            pending ? 0xFF707070u : 0xFFF0ECE4u);
        if (clicked && !pending) TrySendCommanderHeroAction(action, guid);
    }

    private void DrawCommanderWorldOverview(ImDrawListPtr dl, Vector2 bodyMin, Vector2 bodyMax,
        float gap, float s, uint playerMap, bool stale)
    {
        if (_worldMapAreas is null) return;

        int ekRows = CommanderPresenceRows(0).Count;
        int kalRows = CommanderPresenceRows(1).Count;
        int visibleRows = Math.Min(3, Math.Max(ekRows, kalRows));
        float presenceH = Math.Clamp((58f + Math.Max(1, visibleRows) * 21f) * s,
            84f * s, 132f * s);
        Vector2 stageMax = new(bodyMax.X, MathF.Max(bodyMin.Y + 220f * s,
            bodyMax.Y - presenceH - gap));
        CommanderMapUiLaw.DualViewportLayout layout = CommanderMapUiLaw.LayoutDualViewports(
            bodyMin, stageMax - bodyMin, gap, 420f);

        CommanderOverviewMap? ek = BuildCommanderOverviewMap(0, "EASTERN KINGDOMS",
            layout.EasternKingdoms, 30f * s);
        CommanderOverviewMap? kal = BuildCommanderOverviewMap(1, "KALIMDOR",
            layout.Kalimdor, 30f * s);
        if (ek is null || kal is null) return;

        Vector2 presenceMin = new(bodyMin.X, stageMax.Y + gap);
        Vector2 presenceMax = new(bodyMax.X, bodyMax.Y);
        uint rowHover = DrawCommanderWorldPresence(dl, ek.Value, kal.Value,
            presenceMin, presenceMax, gap, s, stale);
        uint ekHighlight = 0;
        uint kalHighlight = 0;
        if (rowHover != 0 &&
            _worldMapAreas.TryGetArea(rowHover, out WorldMapAreaInfo rowArea))
        {
            if (rowArea.MapId == 1) kalHighlight = rowHover;
            else if (rowArea.MapId == 0) ekHighlight = rowHover;
        }
        DrawCommanderOverviewMap(dl, ek.Value, playerMap, s, stale, ekHighlight);
        DrawCommanderOverviewMap(dl, kal.Value, playerMap, s, stale, kalHighlight);
    }

    private CommanderOverviewMap? BuildCommanderOverviewMap(uint mapId, string name,
        CommanderMapUiLaw.ScreenRect cell, float cardHeaderH)
    {
        if (_worldMapAreas is null || !_worldMapAreas.TryGetContinent(mapId, out WorldMapAreaInfo continent))
            return null;

        var zones = new List<CommanderZoneRect>();
        Vector2 unionMin = Vector2.One;
        Vector2 unionMax = Vector2.Zero;
        foreach (WorldMapAreaInfo zone in _worldMapAreas.Areas)
        {
            if (zone.MapId != mapId || zone.AreaId == 0 || zone.Directory.Length == 0) continue;
            Vector2 first = new(continent.X(zone.Left), continent.Y(zone.Top));
            Vector2 second = new(continent.X(zone.Right), continent.Y(zone.Bottom));
            Vector2 min = Vector2.Min(first, second);
            Vector2 max = Vector2.Max(first, second);
            if (max.X <= min.X || max.Y <= min.Y) continue;
            zones.Add(new CommanderZoneRect(zone, min, max));
            unionMin = Vector2.Min(unionMin, min);
            unionMax = Vector2.Max(unionMax, max);
        }
        if (zones.Count == 0) return null;

        CommanderMapUiLaw.ScreenRect mapCell = new(
            cell.Min + new Vector2(0f, cardHeaderH),
            new Vector2(cell.Size.X, MathF.Max(1f, cell.Size.Y - cardHeaderH)));
        var rawCrop = new CommanderMapUiLaw.NormalizedRect(unionMin, unionMax);
        CommanderMapUiLaw.NormalizedRect padded = CommanderMapUiLaw.PadCrop(rawCrop, new Vector2(0.045f));
        CommanderMapUiLaw.NormalizedRect crop = CommanderGrowCropToAspect(padded, mapCell.Aspect);
        CommanderMapUiLaw.ScreenRect viewport = CommanderMapUiLaw.FitViewportToCrop(mapCell, crop);
        return new CommanderOverviewMap(mapId, name, continent,
            cell.Min, cell.Max, viewport.Min, viewport.Size,
            new Vector4(crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y), zones);
    }

    private static CommanderMapUiLaw.NormalizedRect CommanderGrowCropToAspect(
        CommanderMapUiLaw.NormalizedRect crop, float screenAspect)
    {
        Vector2 min = crop.Min;
        Vector2 max = crop.Max;
        Vector2 size = crop.Size;
        float desiredNormalizedAspect = screenAspect * CommanderMapUiLaw.AuthoredHeight /
                                        CommanderMapUiLaw.AuthoredWidth;
        if (size.X / size.Y < desiredNormalizedAspect)
        {
            float width = MathF.Min(1f, size.Y * desiredNormalizedAspect);
            float centre = (min.X + max.X) * 0.5f;
            min.X = Math.Clamp(centre - width * 0.5f, 0f, 1f - width);
            max.X = min.X + width;
        }
        else
        {
            float height = MathF.Min(1f, size.X / desiredNormalizedAspect);
            float centre = (min.Y + max.Y) * 0.5f;
            min.Y = Math.Clamp(centre - height * 0.5f, 0f, 1f - height);
            max.Y = min.Y + height;
        }
        return new CommanderMapUiLaw.NormalizedRect(min, max);
    }

    private static CommanderMapUiLaw.ScreenRect CommanderViewport(in CommanderOverviewMap map) =>
        new(map.MapMin, map.MapSize);

    private static CommanderMapUiLaw.NormalizedRect CommanderCrop(in CommanderOverviewMap map) =>
        new(new Vector2(map.Crop.X, map.Crop.Y), new Vector2(map.Crop.Z, map.Crop.W));

    private void DrawCommanderOverviewMap(ImDrawListPtr dl, in CommanderOverviewMap map,
        uint playerMap, float s, bool stale, uint forcedHighlightArea)
    {
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        dl.AddRectFilled(map.CardMin, map.CardMax, 0xF01B1815, 5f * s);
        DrawCommanderMapTilesCropped(dl, map.Continent.Directory, viewport, crop, 0xFFFFFFFF);

        // Territory is a strategic layer, not an interaction target. Paint it
        // after parchment and before hover/presence so navigation stays unchanged.
        if (CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules))
            foreach (CommanderZoneRect zone in map.Zones)
                if (_zoneIntel.TryGetValue(zone.Zone.AreaId, out RtsTerritoryZoneWire territory) &&
                    (territory.Owner != RtsTerritoryOwner.Neutral || territory.Contested))
                    DrawCommanderTerritory(dl, map, zone, territory, s);

        ImGui.SetCursorScreenPos(viewport.Min);
        ImGui.InvisibleButton($"##commander-world-{map.MapId}", viewport.Size);
        CommanderZoneRect? hovered = ImGui.IsItemHovered()
            ? ResolveCommanderZone(map, ImGui.GetMousePos()) : null;
        CommanderZoneRect? highlighted = hovered;
        if (highlighted is null && forcedHighlightArea != 0)
        {
            CommanderZoneRect rowZone = map.Zones.FirstOrDefault(
                z => z.Zone.AreaId == forcedHighlightArea);
            if (rowZone.Zone.AreaId != 0) highlighted = rowZone;
        }
        if (highlighted is CommanderZoneRect highlight)
            DrawCommanderZoneHighlight(dl, map, highlight, s, drawLabel: true);
        if (hovered is CommanderZoneRect hover)
        {
            if (ImGui.IsItemClicked()) _commanderMapZone = hover.Zone.AreaId;
        }

        Vector2? cameraPoint = null;
        if (CommanderMapUiLaw.ShowCameraMarker(map.MapId, playerMap) && _controller is not null)
        {
            Vector2 normalized = new(map.Continent.X(_controller.Position.Y),
                map.Continent.Y(_controller.Position.X));
            if (CommanderMapUiLaw.Contains(crop, normalized))
                cameraPoint = CommanderMapUiLaw.ProjectCrop(viewport, crop, normalized);
        }

        var occupied = new List<(Vector2 Min, Vector2 Max)>();
        foreach (CommanderZoneRect zone in map.Zones)
        {
            if (!_zoneIntel.TryGetValue(zone.Zone.AreaId, out RtsTerritoryZoneWire intel) ||
                intel.Bots + intel.Players == 0) continue;
            Vector2 centre = CommanderMapUiLaw.ProjectCrop(viewport, crop,
                (zone.Min + zone.Max) * 0.5f);
            if (!CommanderMapUiLaw.Contains(viewport, centre)) continue;
            occupied.Add(DrawCommanderPill(dl, centre, intel, s, stale, cameraPoint,
                viewport.Min, viewport.Max, occupied));
        }
        if (cameraPoint is Vector2 camera)
            DrawCommanderDiamond(dl, camera, 7f * s, VanillaGold);

        dl.AddRectFilled(map.CardMin, new Vector2(map.CardMax.X, map.MapMin.Y), 0xE0181410);
        CmdText(dl, 14f * s, map.CardMin + new Vector2(12f, 7f) * s,
            VanillaGold, map.Name);
        string summary = highlighted is CommanderZoneRect hz
            ? CommanderZoneSummary(hz.Zone)
            : CommanderContinentSummary(map.MapId);
        DrawCommanderRightAlignedText(dl, map.CardMax.X - 12f * s,
            map.CardMin.Y + 8f * s, summary, 12f * s,
            highlighted is null ? 0xFF9A948Du : 0xFFF0E6D2u);
        dl.AddRect(map.CardMin, map.CardMax, 0x60FFFFFF, 5f * s, ImDrawFlags.None, 1f * s);
    }

    private CommanderZoneRect? ResolveCommanderZone(in CommanderOverviewMap map, Vector2 screen)
    {
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        if (!CommanderMapUiLaw.Contains(viewport, screen)) return null;
        Vector2 normalized = CommanderMapUiLaw.UnprojectCrop(viewport, crop, screen);
        if (_worldMapHits?.TryResolveArea(map.MapId, map.Continent,
                normalized, out uint areaId) != true)
            return null;
        foreach (CommanderZoneRect zone in map.Zones)
            if (zone.Zone.AreaId == areaId) return zone;
        return null;
    }

    private void DrawCommanderZoneHighlight(ImDrawListPtr dl, in CommanderOverviewMap map,
        in CommanderZoneRect zone, float s, bool drawLabel)
    {
        if (_worldMapHighlights?.TryGetArea(
                zone.Zone.AreaId, out WorldMapHighlightInfo highlight) != true)
            return;
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        Vector2 min = CommanderMapUiLaw.ProjectCrop(viewport, crop,
            new Vector2(highlight.Bounds.Left, highlight.Bounds.Top));
        Vector2 max = CommanderMapUiLaw.ProjectCrop(viewport, crop,
            new Vector2(highlight.Bounds.Right, highlight.Bounds.Bottom));
        // World-map highlights are colored ADD-authored art. Preserve their authored hue;
        // AdditiveHandle makes the black field transparent for ImGui without whitening it.
        uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
        dl.PushClipRect(viewport.Min, viewport.Max, true);
        if (texture != 0)
            dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
                Vector2.Zero, new Vector2(highlight.UMax, highlight.VMax),
                0xFFFFFFFF);
        if (drawLabel)
            DrawWorldMapOutlinedCenteredText(dl, (min + max) * 0.5f,
                CommanderZoneName(zone.Zone), 14f * s, VanillaGold);
        dl.PopClipRect();
    }

    private void DrawCommanderTerritory(ImDrawListPtr dl, in CommanderOverviewMap map,
        in CommanderZoneRect zone, in RtsTerritoryZoneWire territory, float s)
    {
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        uint ownerColor = territory.Owner switch
        {
            RtsTerritoryOwner.Alliance => 0x70E68A46u,
            RtsTerritoryOwner.Horde => 0x703D48D8u,
            _ => 0x505F5F5Fu,
        };
        float pulse = 0.5f + 0.5f * MathF.Sin((float)NowSeconds() * 4f);
        uint contestedColor = (uint)(0x48 + (int)(pulse * 0x38)) << 24 | 0x0032AAE6u;

        Vector2 centre = CommanderMapUiLaw.ProjectCrop(viewport, crop,
            (zone.Min + zone.Max) * 0.5f);
        bool drewMask = false;
        if (_worldMapHighlights?.TryGetArea(
                zone.Zone.AreaId, out WorldMapHighlightInfo highlight) == true)
        {
            Vector2 min = CommanderMapUiLaw.ProjectCrop(viewport, crop,
                new Vector2(highlight.Bounds.Left, highlight.Bounds.Top));
            Vector2 max = CommanderMapUiLaw.ProjectCrop(viewport, crop,
                new Vector2(highlight.Bounds.Right, highlight.Bounds.Bottom));
            uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
            if (texture != 0)
            {
                dl.PushClipRect(viewport.Min, viewport.Max, true);
                dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
                    Vector2.Zero, new Vector2(highlight.UMax, highlight.VMax), ownerColor);
                if (territory.Contested)
                    dl.AddRect(Vector2.Min(min, max), Vector2.Max(min, max), contestedColor,
                        2f * s, ImDrawFlags.None, 2f * s);
                dl.PopClipRect();
                drewMask = true;
            }
        }

        // The badge is also the required fallback when a stock zone has no
        // highlight texture; color is never the sole ownership signal.
        string badge = territory.Contested ? "!" : territory.Owner switch
        {
            RtsTerritoryOwner.Alliance => "A",
            RtsTerritoryOwner.Horde => "H",
            _ => "N",
        };
        float radius = (drewMask ? 8f : 10f) * s;
        uint badgeColor = territory.Contested ? contestedColor : ownerColor | 0xC0000000u;
        dl.AddCircleFilled(centre, radius, badgeColor);
        DrawCenteredText(dl, centre, badge, 10f * s, 0xFFFFFFFFu);
    }

    private string CommanderTerritoryStatus(in RtsTerritoryZoneWire zone)
    {
        if (!CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules)) return string.Empty;
        string owner = zone.Owner switch
        {
            RtsTerritoryOwner.Alliance => "Alliance",
            RtsTerritoryOwner.Horde => "Horde",
            _ => "Neutral",
        };
        return zone.Contested ? $"  -  {owner} (CONTESTED)" : $"  -  {owner}";
    }

    private string CommanderZoneSummary(WorldMapAreaInfo zone)
    {
        _zoneIntel.TryGetValue(zone.AreaId, out RtsTerritoryZoneWire intel);
        string control = CommanderTerritoryStatus(intel);
        return $"{CommanderZoneName(zone)}  -  B{intel.Bots}  -  P{intel.Players}{control}";
    }

    private string CommanderContinentSummary(uint mapId)
    {
        int zones = 0, bots = 0, players = 0;
        foreach ((uint areaId, RtsTerritoryZoneWire intel) in _zoneIntel)
        {
            if (_worldMapAreas?.TryGetArea(areaId, out WorldMapAreaInfo area) != true ||
                area.MapId != mapId || intel.Bots + intel.Players == 0) continue;
            zones++;
            bots += intel.Bots;
            players += intel.Players;
        }
        return zones == 0 ? "No players or bots reported" :
            $"{zones} populated zones  -  B{bots}  -  P{players}";
    }

    private List<(WorldMapAreaInfo Area, RtsTerritoryZoneWire Intel, string Name)> CommanderPresenceRows(uint mapId)
    {
        var rows = new List<(WorldMapAreaInfo, RtsTerritoryZoneWire, string)>();
        foreach ((uint areaId, RtsTerritoryZoneWire intel) in _zoneIntel)
        {
            if (_worldMapAreas?.TryGetArea(areaId, out WorldMapAreaInfo area) != true ||
                !CommanderMapUiLaw.ShowWorldPresence(area.MapId, intel.Bots, intel.Players) ||
                area.MapId != mapId) continue;
            rows.Add((area, intel, CommanderZoneName(area)));
        }
        rows.Sort((a, b) => (b.Item2.Bots + b.Item2.Players).CompareTo(
            a.Item2.Bots + a.Item2.Players));
        return rows;
    }

    private uint DrawCommanderWorldPresence(ImDrawListPtr dl, in CommanderOverviewMap ek,
        in CommanderOverviewMap kal, Vector2 min, Vector2 max, float gap, float s, bool stale)
    {
        dl.AddRectFilled(min, max, 0xF01B1815, 5f * s);
        dl.AddRect(min, max, 0x50FFFFFF, 5f * s);
        float half = (max.X - min.X - gap) * 0.5f;
        uint kalHover = DrawCommanderPresenceColumn(dl, 1, kal.Name,
            new Vector2(min.X, min.Y), new Vector2(min.X + half, max.Y), s, stale);
        uint ekHover = DrawCommanderPresenceColumn(dl, 0, ek.Name,
            new Vector2(min.X + half + gap, min.Y), max, s, stale);
        return ekHover != 0 ? ekHover : kalHover;
    }

    private uint DrawCommanderPresenceColumn(ImDrawListPtr dl, uint mapId, string continentName,
        Vector2 min, Vector2 max, float s, bool stale)
    {
        List<(WorldMapAreaInfo Area, RtsTerritoryZoneWire Intel, string Name)> rows = CommanderPresenceRows(mapId);
        int visible = Math.Min(3, rows.Count);
        float x = min.X + 12f * s;
        float y = min.Y + 9f * s;
        CmdText(dl, 12f * s, new Vector2(x, y), VanillaGold,
            continentName + " PRESENCE");
        float botRight = max.X - 62f * s;
        float playerRight = max.X - 12f * s;
        DrawCommanderRightAlignedText(dl, botRight, y, "BOTS", 10f * s, 0xFF8A8A8A);
        DrawCommanderRightAlignedText(dl, playerRight, y, "PLAYERS", 10f * s, 0xFF8A8A8A);
        y += 22f * s;
        uint hovered = 0;
        uint color = stale ? 0xFF8A8A8Au : 0xFFE6E6E6u;
        for (int i = 0; i < visible; i++)
        {
            var row = rows[i];
            ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
            ImGui.InvisibleButton($"##cmd-world-presence-{mapId}-{row.Area.AreaId}",
                new Vector2(max.X - x - 8f * s, 20f * s));
            bool rowHover = ImGui.IsItemHovered();
            if (rowHover)
            {
                hovered = row.Area.AreaId;
                dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                    new Vector2(max.X - 8f * s, y + 17f * s), 0x20FFFFFF, 2f * s);
            }
            CmdText(dl, 12f * s, new Vector2(x, y),
                rowHover ? 0xFFFFFFFFu : color,
                CommanderFitText(row.Name, botRight - x - 48f * s, 12f * s));
            DrawCommanderRightAlignedText(dl, botRight, y, row.Intel.Bots.ToString(), 12f * s, color);
            DrawCommanderRightAlignedText(dl, playerRight, y, row.Intel.Players.ToString(), 12f * s, color);
            if (ImGui.IsItemClicked()) _commanderMapZone = row.Area.AreaId;
            y += 20f * s;
        }
        if (rows.Count == 0)
            CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                _zoneIntelAt == 0 ? "Waiting for population data." : "No players or bots reported.");
        else if (rows.Count > visible)
            CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                $"+{rows.Count - visible} more populated zones");
        return hovered;
    }

    private void DrawCommanderDrill(ImDrawListPtr dl, WorldMapAreaInfo zone,
        Vector2 bodyMin, Vector2 bodyMax, float gap, float s, uint playerMap)
    {
        float backH = 34f * s;
        if (CommanderTab(dl, "##cmd-world-overview", "< World overview", bodyMin,
                new Vector2(150f, 27f), s, false))
            _commanderMapZone = 0;

        Vector2 contentMin = new(bodyMin.X, bodyMin.Y + backH);
        Vector2 contentSize = bodyMax - contentMin;
        float railW = Math.Clamp(contentSize.X * 0.22f, 280f * s, 380f * s);
        float availableW = MathF.Max(200f, contentSize.X - railW - gap);
        float mapW = MathF.Min(availableW,
            contentSize.Y * CommanderMapUiLaw.AuthoredWidth / CommanderMapUiLaw.AuthoredHeight);
        float mapH = mapW * CommanderMapUiLaw.AuthoredHeight / CommanderMapUiLaw.AuthoredWidth;
        Vector2 mapMin = contentMin + new Vector2((availableW - mapW) * 0.5f,
            (contentSize.Y - mapH) * 0.5f);
        DrawCommanderZoneView(dl, zone, playerMap, mapMin, mapW, mapH, s);

        Vector2 panelMin = new(contentMin.X + availableW + gap, contentMin.Y);
        Vector2 panelMax = new(bodyMax.X, bodyMax.Y);
        DrawCommanderIntelPanel(dl, (int)zone.MapId, true, zone, panelMin, panelMax, s, stale: false);
    }

    // Game UI never draws with the ImGui default font (Nico's rule): route this custom RTS-map
    // text through the exact-size FRIZQT bake. `size` is a device-pixel height (the gameplay
    // scale is already folded in), so it is passed as the logical height at uiScale 1f.
    private static void CmdText(ImDrawListPtr dl, float size, Vector2 pos, uint color, string text)
        => GameText.DrawPlain(dl, text, pos, size, 1f, color);

    private static Vector2 CmdMeasure(string text, float size)
        => new(GameText.MeasurePlain(text, size, 1f), GameTextLaw.EmPixels(size, 1f));

    private static void DrawWorldMapOutlinedCenteredText(ImDrawListPtr dl, Vector2 centre,
        string text, float size, uint color)
    {
        Vector2 measured = CmdMeasure(text, size);
        Vector2 at = centre - measured * 0.5f;
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0)
                CmdText(dl, size, at + new Vector2(x, y) * 1.5f,
                    0xE0000000, text);
        CmdText(dl, size, at, color, text);
    }

    private void DrawCommanderZoneView(ImDrawListPtr dl, WorldMapAreaInfo zone, uint playerMap,
        Vector2 mapMin, float mapW, float mapH, float s)
    {
        DrawCommanderMapTiles(dl, zone.Directory, mapMin, mapW, mapH);
        DrawCommanderNoFogOverlays(dl, zone, mapMin, mapW, mapH);
        Vector2 mapSize = new(mapW, mapH);

        string zoneName = _areas?.ZoneName(zone.AreaId) is { Length: > 0 } n ? n : zone.Directory;
        CmdText(dl, 16f * s, mapMin + new Vector2(8f, 6f) * s, 0xFFFFFFFF, zoneName);

        // Own units in this zone. Zone id is authoritative; the rect test rescues
        // units the server stamped with a subzone id the map doesn't know.
        ImGui.SetCursorScreenPos(mapMin);
        ImGui.InvisibleButton("##commander-zone", mapSize);
        bool mapHovered = ImGui.IsItemHovered();
        Vector2 mouse = ImGui.GetMousePos();

        bool forceZoneReady = CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules) &&
            _rtsForcePublishedZone == zone.AreaId;
        RtsZoneIntelUnitWire? hoveredUnit = null;
        foreach (RtsZoneIntelUnitWire unit in _commanderUnits)
        {
            if (forceZoneReady && _rtsForces.ContainsKey(unit.Guid)) continue;
            if (!CommanderUnitInZone(unit, zone, out Vector2 f)) continue;
            Vector2 p = CommanderMapUiLaw.Project(mapMin, mapSize, f);
            bool hover = mapHovered && (mouse - p).Length() < 12f * s;
            if (hover) hoveredUnit = unit;
            DrawCommanderUnitMarker(dl, unit, p, s, hover);
        }

        RtsForceUnitWire? hoveredForce = null;
        if (forceZoneReady)
        {
            foreach (RtsForceUnitWire unit in _rtsForces.Values)
            {
                if (!CommanderForceInZone(unit, zone, out Vector2 f)) continue;
                Vector2 p = CommanderMapUiLaw.Project(mapMin, mapSize, f);
                bool hover = mapHovered && (mouse - p).Length() < 12f * s;
                if (hover) hoveredForce = unit;
                DrawCommanderForceMarker(dl, unit, p, s, hover,
                    unit.Guid == _rtsSelectedForceGuid);
            }
        }

        if (hoveredForce is RtsForceUnitWire force)
        {
            string name = CommanderForceName(force.Guid, request: true);
            string detail = $"Level {force.Level} {ClassIdName(force.Class)}".TrimEnd();
            if (!force.Alive) detail += "  -  dead";
            else if (force.Busy) detail += "  -  currently controlled";
            DrawCommanderFlyout(dl, mouse + new Vector2(18f, 10f) * s, s,
                name, detail, "Click to select this faction bot");
            if (ImGui.IsItemClicked())
                _rtsSelectedForceGuid = force.Guid;
        }
        else if (hoveredUnit is RtsZoneIntelUnitWire hu)
        {
            string name = CommanderUnitName(hu.Guid);
            DrawCommanderFlyout(dl, mouse + new Vector2(18f, 10f) * s, s,
                name + (hu.Alive ? "" : "  (dead)"),
                hu.IsBot ? "Bot — click to take the camera there" : "Player — click to take the camera there");
            if (ImGui.IsItemClicked())
            {
                if (hu.MapId != playerMap)
                    CommanderShowNotice("Camera travel is limited to your current continent.");
                else
                {
                    CommanderFlyTo(hu.Position.X, hu.Position.Y, CommanderUnitAltitude);
                    _commanderMapOpen = false;      // unit click = the deliberate "enter the world" action
                }
            }
        }
        else if (mapHovered && ImGui.IsItemClicked())
        {
            // Ground click: inverse transform through the ZONE rect (fraction.X is
            // worldY, fraction.Y is worldX — the same swap as every projection).
            Vector2 f = CommanderMapUiLaw.Unproject(mapMin, mapSize, mouse);
            float worldY = zone.Left + f.X * (zone.Right - zone.Left);
            float worldX = zone.Top + f.Y * (zone.Bottom - zone.Top);
            if (zone.MapId != playerMap)
                CommanderShowNotice("Camera travel is limited to your current continent.");
            else
                CommanderFlyTo(worldX, worldY, CommanderFlyAltitude);   // map stays open: navigation, not commitment
        }

        // Camera diamond inside the zone map too.
        if (zone.MapId == playerMap && _controller is not null)
        {
            Vector2 f = new(zone.X(_controller.Position.Y), zone.Y(_controller.Position.X));
            if (f.X is > 0f and < 1f && f.Y is > 0f and < 1f)
                DrawCommanderDiamond(dl, CommanderMapUiLaw.Project(mapMin, mapSize, f), 7f * s, VanillaGold);
        }
    }

    private static bool CommanderUnitInZone(RtsZoneIntelUnitWire unit, WorldMapAreaInfo zone, out Vector2 fraction)
    {
        fraction = new Vector2(zone.X(unit.Position.Y), zone.Y(unit.Position.X));
        if (unit.MapId != zone.MapId) return false;
        bool inside = fraction.X is > 0.001f and < 0.999f &&
                      fraction.Y is > 0.001f and < 0.999f;
        return unit.ZoneId == zone.AreaId || inside;
    }

    private static bool CommanderForceInZone(RtsForceUnitWire unit, WorldMapAreaInfo zone,
        out Vector2 fraction)
    {
        fraction = new Vector2(zone.X(unit.Position.Y), zone.Y(unit.Position.X));
        if (unit.MapId != zone.MapId) return false;
        bool inside = fraction.X is > 0.001f and < 0.999f &&
                      fraction.Y is > 0.001f and < 0.999f;
        return unit.ZoneId == zone.AreaId || inside;
    }

    private void DrawCommanderIntelPanel(ImDrawListPtr dl, int mapId, bool drilled, WorldMapAreaInfo drilledZone,
        Vector2 panelMin, Vector2 panelMax, float s, bool stale)
    {
        float line = 20f * s;
        const int maxVisibleRows = 16;
        float x = panelMin.X + 12f * s;
        float y = panelMin.Y + 10f * s;

        if (!drilled)
        {
            var rows = new List<(uint ZoneId, RtsTerritoryZoneWire Intel, WorldMapAreaInfo Area, string Name)>();
            foreach ((uint zoneId, RtsTerritoryZoneWire intel) in _zoneIntel)
            {
                if (_worldMapAreas?.TryGetArea(zoneId, out WorldMapAreaInfo area) != true ||
                    !CommanderMapUiLaw.ShowPresence((uint)mapId, area.MapId, intel.Bots, intel.Players)) continue;
                string name = _areas?.ZoneName(zoneId) is { Length: > 0 } zoneName ? zoneName : area.Directory;
                rows.Add((zoneId, intel, area, name));
            }
            rows.Sort((a, b) => (b.Intel.Bots + b.Intel.Players).CompareTo(a.Intel.Bots + a.Intel.Players));

            int visible = Math.Min(maxVisibleRows, rows.Count);
            bool overflow = rows.Count > visible;
            float desiredHeight = (64f + Math.Max(1, visible) * 20f + (overflow ? 20f : 0f)) * s;
            Vector2 actualMax = new(panelMax.X, MathF.Min(panelMax.Y, panelMin.Y + MathF.Max(112f * s, desiredHeight)));
            dl.AddRectFilled(panelMin, actualMax, 0xF0201C18, 4f * s);
            dl.AddRect(panelMin, actualMax, 0x40FFFFFF, 4f * s);

            CmdText(dl, 14f * s, new Vector2(x, y), VanillaGold, "ZONE PRESENCE");
            y += line * 1.25f;
            float botRight = actualMax.X - 58f * s;
            float playerRight = actualMax.X - 12f * s;
            CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A, "ZONE");
            DrawCommanderRightAlignedText(dl, botRight, y, "BOTS", 10f * s, 0xFF8A8A8A);
            DrawCommanderRightAlignedText(dl, playerRight, y, "PLAYERS", 10f * s, 0xFF8A8A8A);
            y += line * 0.8f;

            uint dim = stale ? 0xFF8A8A8Au : 0xFFE6E6E6u;
            for (int i = 0; i < visible; i++)
            {
                var row = rows[i];
                uint zoneId = row.ZoneId;
                RtsTerritoryZoneWire intel = row.Intel;
                string name = row.Name;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-active-{zoneId}", new Vector2(actualMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(actualMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                string fitted = CommanderFitText(name, botRight - x - 50f * s, 13f * s);
                CmdText(dl, 13f * s, new Vector2(x, y), rowHover ? 0xFFFFFFFFu : dim, fitted);
                DrawCommanderRightAlignedText(dl, botRight, y, intel.Bots.ToString(), 13f * s, dim);
                DrawCommanderRightAlignedText(dl, playerRight, y, intel.Players.ToString(), 13f * s, dim);
                if (ImGui.IsItemClicked())
                    _commanderMapZone = zoneId;
                y += line;
            }
            if (rows.Count == 0)
            {
                string empty = _zoneIntelAt == 0 ? "Waiting for population data." :
                    "No players or bots reported here.";
                CmdText(dl, 12f * s, new Vector2(x, y), 0xFF8A8A8A, empty);
                y += line;
            }
            if (overflow)
                CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"+{rows.Count - visible} more populated zones");
        }
        else
        {
            if (CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules))
            {
                DrawCommanderForceIntelPanel(dl, drilledZone, panelMin, panelMax, s);
                return;
            }

            var units = new List<RtsZoneIntelUnitWire>();
            foreach (RtsZoneIntelUnitWire unit in _commanderUnits)
                if (CommanderUnitInZone(unit, drilledZone, out _)) units.Add(unit);
            int visible = Math.Min(maxVisibleRows, units.Count);
            bool overflow = units.Count > visible;
            float desiredHeight = (58f + Math.Max(1, visible) * 20f + (overflow ? 20f : 0f)) * s;
            Vector2 actualMax = new(panelMax.X, MathF.Min(panelMax.Y, panelMin.Y + MathF.Max(112f * s, desiredHeight)));
            dl.AddRectFilled(panelMin, actualMax, 0xF0201C18, 4f * s);
            dl.AddRect(panelMin, actualMax, 0x40FFFFFF, 4f * s);

            CmdText(dl, 14f * s, new Vector2(x, y), VanillaGold, "YOUR GROUP HERE");
            y += line * 1.15f;
            string zoneName = _areas?.ZoneName(drilledZone.AreaId) is { Length: > 0 } resolvedZoneName
                ? resolvedZoneName : drilledZone.Directory;
            CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                CommanderFitText(zoneName.ToUpperInvariant(), actualMax.X - x - 12f * s, 10f * s));
            y += line * 0.9f;

            for (int i = 0; i < visible; i++)
            {
                RtsZoneIntelUnitWire unit = units[i];
                string name = CommanderUnitName(unit.Guid);
                uint color = unit.Alive ? CommanderClassColor(BotClassName(unit.Guid, name)) : 0xFF707070;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-unit-{unit.Guid}", new Vector2(actualMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(actualMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                dl.AddCircleFilled(new Vector2(x + 5f * s, y + 7f * s), 4f * s, color);
                CmdText(dl, 13f * s, new Vector2(x + 14f * s, y),
                    unit.Alive ? (rowHover ? 0xFFFFFFFFu : 0xFFE6E6E6u) : 0xFF8A8A8Au,
                    CommanderFitText(name + (unit.Alive ? "" : " (dead)"), actualMax.X - x - 30f * s, 13f * s));
                if (ImGui.IsItemClicked())
                {
                    if (unit.MapId != checked((uint)Math.Max(0, _config.Start.Map)))
                        CommanderShowNotice("Camera travel is limited to your current continent.");
                    else
                    {
                        CommanderFlyTo(unit.Position.X, unit.Position.Y, CommanderUnitAltitude);
                        _commanderMapOpen = false;
                    }
                }
                y += line;
            }
            if (units.Count == 0)
                CmdText(dl, 13f * s, new Vector2(x, y), 0xFF8A8A8A, "None of your units are here.");
            else if (overflow)
                CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"+{units.Count - visible} more units");
        }
    }

    private void DrawCommanderForceIntelPanel(ImDrawListPtr dl, WorldMapAreaInfo zone,
        Vector2 panelMin, Vector2 panelMax, float s)
    {
        float line = 20f * s;
        float x = panelMin.X + 12f * s;
        float y = panelMin.Y + 10f * s;
        bool ready = _rtsForcePublishedZone == zone.AreaId;
        List<RtsForceUnitWire> units = ready
            ? _rtsForces.Values.OrderBy(unit => unchecked((uint)unit.Guid)).ToList()
            : [];
        const int maximumRows = 16;
        int visible = Math.Min(maximumRows, units.Count);
        bool overflow = units.Count > visible;
        float desiredHeight = (62f + Math.Max(1, visible) * 20f + (overflow ? 20f : 0f)) * s;
        Vector2 actualMax = new(panelMax.X,
            MathF.Min(panelMax.Y, panelMin.Y + MathF.Max(112f * s, desiredHeight)));
        dl.AddRectFilled(panelMin, actualMax, 0xF0201C18, 4f * s);
        dl.AddRect(panelMin, actualMax, 0x40FFFFFF, 4f * s);

        CmdText(dl, 14f * s, new Vector2(x, y), VanillaGold, "FACTION FORCE");
        y += line * 1.15f;
        string zoneName = _areas?.ZoneName(zone.AreaId) is { Length: > 0 } resolved
            ? resolved : zone.Directory;
        string count = ready ? $"  {_rtsForces.Count}/{_rtsForcePublishedTotal}" :
            _rtsForceLoading ? "  LOADING" : string.Empty;
        CmdText(dl, 10f * s, new Vector2(x, y), 0xFF8A8A8A,
            CommanderFitText(zoneName.ToUpperInvariant() + count,
                actualMax.X - x - 12f * s, 10f * s));
        y += line * 0.9f;

        for (int i = 0; i < visible; i++)
        {
            RtsForceUnitWire unit = units[i];
            bool selected = unit.Guid == _rtsSelectedForceGuid;
            string name = CommanderForceName(unit.Guid, request: true);
            uint color = unit.Alive ? CommanderClassColor(ClassIdName(unit.Class)) : 0xFF707070;
            ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
            ImGui.InvisibleButton($"##cmd-force-{unit.Guid}",
                new Vector2(actualMax.X - x - 8f * s, line));
            bool hover = ImGui.IsItemHovered();
            if (hover || selected)
                dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                    new Vector2(actualMax.X - 8f * s, y - 2f * s + line),
                    selected ? 0x303EA6E6u : 0x18FFFFFFu, 2f * s);
            dl.AddCircleFilled(new Vector2(x + 5f * s, y + 7f * s), 4f * s, color);
            string suffix = !unit.Alive ? " (dead)" : unit.Busy ? " (in use)" : string.Empty;
            CmdText(dl, 13f * s, new Vector2(x + 14f * s, y),
                unit.Alive ? (hover || selected ? 0xFFFFFFFFu : 0xFFE6E6E6u) : 0xFF8A8A8Au,
                CommanderFitText(name + suffix, actualMax.X - x - 30f * s, 13f * s));
            if (ImGui.IsItemClicked()) _rtsSelectedForceGuid = unit.Guid;
            y += line;
        }

        if (!ready)
            CmdText(dl, 12f * s, new Vector2(x, y), 0xFF8A8A8A,
                _rtsForceLoading ? "Loading faction bots..." : "Faction roster unavailable.");
        else if (units.Count == 0)
            CmdText(dl, 12f * s, new Vector2(x, y), 0xFF8A8A8A,
                "No faction bots are in this zone.");
        else if (overflow)
            CmdText(dl, 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                $"+{units.Count - visible} more bots on the map");
    }

    private static void DrawCommanderRightAlignedText(ImDrawListPtr dl, float right, float y,
        string text, float size, uint color)
    {
        float width = GameText.MeasurePlain(text, size, 1f);
        CmdText(dl, size, new Vector2(right - width, y), color, text);
    }

    private static string CommanderFitText(string text, float maxWidth, float size)
    {
        if (maxWidth <= 0f) return string.Empty;
        if (GameText.MeasurePlain(text, size, 1f) <= maxWidth) return text;
        const string ellipsis = "...";
        for (int length = text.Length - 1; length > 0; length--)
        {
            string candidate = text[..length] + ellipsis;
            if (GameText.MeasurePlain(candidate, size, 1f) <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    // ── small drawing helpers ────────────────────────────────────────────────

    private void DrawCommanderMapTiles(ImDrawListPtr dl, string directory, Vector2 mapMin, float mapW, float mapH)
    {
        // The twelve square textures form a padded 1024x768 atlas. Only its
        // upper-left 1002x668 is authored map content: preserve square pixels and
        // clip the 22px right / 100px bottom padding exactly like the vanilla map.
        const uint tint = 0xFFFFFFFF;
        dl.PushClipRect(mapMin, mapMin + new Vector2(mapW, mapH), true);
        dl.AddRectFilled(mapMin, mapMin + new Vector2(mapW, mapH), 0xFF0E0C0A);
        for (int row = 0; row < CommanderMapUiLaw.TileRows; row++)
        for (int col = 0; col < CommanderMapUiLaw.TileColumns; col++)
        {
            int index = row * CommanderMapUiLaw.TileColumns + col + 1;
            uint texture = _gameplayArt!.Handle($@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            (Vector2 min, Vector2 max) = CommanderMapUiLaw.TileBounds(mapMin, mapW, row, col);
            dl.AddImage((nint)texture, min, max, Vector2.Zero, Vector2.One, tint);
        }
        dl.PopClipRect();
        dl.AddRect(mapMin, mapMin + new Vector2(mapW, mapH), 0x40FFFFFF);
    }

    private void DrawCommanderMapTilesCropped(ImDrawListPtr dl, string directory,
        CommanderMapUiLaw.ScreenRect viewport, CommanderMapUiLaw.NormalizedRect crop, uint tint)
    {
        float scaleX = viewport.Size.X / (crop.Size.X * CommanderMapUiLaw.AuthoredWidth);
        float scaleY = viewport.Size.Y / (crop.Size.Y * CommanderMapUiLaw.AuthoredHeight);
        float scale = MathF.Min(scaleX, scaleY);
        float authoredW = CommanderMapUiLaw.AuthoredWidth * scale;
        float authoredH = CommanderMapUiLaw.AuthoredHeight * scale;
        Vector2 authoredMin = viewport.Min - new Vector2(
            crop.Min.X * authoredW, crop.Min.Y * authoredH);

        dl.PushClipRect(viewport.Min, viewport.Max, true);
        dl.AddRectFilled(viewport.Min, viewport.Max, 0xFF0E0C0A);
        for (int row = 0; row < CommanderMapUiLaw.TileRows; row++)
        for (int col = 0; col < CommanderMapUiLaw.TileColumns; col++)
        {
            int index = row * CommanderMapUiLaw.TileColumns + col + 1;
            uint texture = _gameplayArt!.Handle(
                $@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            (Vector2 min, Vector2 max) = CommanderMapUiLaw.TileBounds(
                authoredMin, authoredW, row, col);
            dl.AddImage((nint)texture, min, max, Vector2.Zero, Vector2.One, tint);
        }
        dl.PopClipRect();
        dl.AddRect(viewport.Min, viewport.Max, 0x60FFFFFF);
    }

    private void DrawCommanderNoFogOverlays(ImDrawListPtr dl, WorldMapAreaInfo zone,
        Vector2 mapMin, float mapW, float mapH)
    {
        if (_worldMapOverlays is null) return;
        float scale = mapW / CommanderMapUiLaw.AuthoredWidth;
        dl.PushClipRect(mapMin, mapMin + new Vector2(mapW, mapH), true);
        foreach (WorldMapOverlayChunk chunk in
                 _worldMapOverlays.BuildFullRevealChunks(zone.Id, zone.Directory))
        {
            uint texture = _gameplayArt!.Handle(chunk.TexturePath);
            if (texture == 0) continue;
            Vector2 min = mapMin + new Vector2(chunk.OffsetX, chunk.OffsetY) * scale;
            Vector2 max = min + new Vector2(chunk.PixelWidth, chunk.PixelHeight) * scale;
            dl.AddImage((nint)texture, min, max, Vector2.Zero,
                new Vector2(chunk.UMax, chunk.VMax), 0xFFFFFFFF);
        }
        dl.PopClipRect();
    }

    private static (Vector2 Min, Vector2 Max) DrawCommanderPill(ImDrawListPtr dl,
        Vector2 centre, RtsTerritoryZoneWire intel, float s, bool stale, Vector2? avoid,
        Vector2 frameMin, Vector2 frameMax, IReadOnlyList<(Vector2 Min, Vector2 Max)> occupied)
    {
        string text = $"B{intel.Bots}  ·  P{intel.Players}";
        float size = 12f * s;
        Vector2 measured = CmdMeasure(text, size);
        Vector2 pad = new(6f * s, 3f * s);
        Vector2 source = centre;
        Vector2 min = default, max = default;
        float step = measured.Y + pad.Y * 2f + 5f * s;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            int level = (attempt + 1) / 2;
            int direction = attempt == 0 ? 0 : attempt % 2 == 1 ? -1 : 1;
            centre = source + new Vector2(0, direction * level * step);
            min = centre - measured * 0.5f - pad;
            max = centre + measured * 0.5f + pad;

            Vector2 correction = Vector2.Zero;
            float inset = 4f * s;
            if (min.X < frameMin.X + inset) correction.X += frameMin.X + inset - min.X;
            if (max.X > frameMax.X - inset) correction.X -= max.X - (frameMax.X - inset);
            if (min.Y < frameMin.Y + inset) correction.Y += frameMin.Y + inset - min.Y;
            if (max.Y > frameMax.Y - inset) correction.Y -= max.Y - (frameMax.Y - inset);
            centre += correction;
            min += correction;
            max += correction;

            bool blocked = avoid is Vector2 point && CommanderRectsOverlap(min, max,
                point - new Vector2(8f * s), point + new Vector2(8f * s), 0f);
            if (!blocked)
                foreach ((Vector2 usedMin, Vector2 usedMax) in occupied)
                    if (CommanderRectsOverlap(min, max, usedMin, usedMax, 3f * s))
                    { blocked = true; break; }
            if (!blocked) break;
        }
        if (Vector2.DistanceSquared(source, centre) > 1f)
            dl.AddLine(source, centre, 0x70D2B870, 1f * s);
        dl.AddRectFilled(min, max, stale ? 0x90242424u : 0xC8242C30u, (max.Y - min.Y) * 0.5f);
        CmdText(dl, size, centre - measured * 0.5f,
            stale ? 0xFF9A9A9Au : 0xFFF0E6D2u, text);
        return (min, max);
    }

    private static bool CommanderRectsOverlap(Vector2 aMin, Vector2 aMax,
        Vector2 bMin, Vector2 bMax, float margin) =>
        aMin.X < bMax.X + margin && aMax.X > bMin.X - margin &&
        aMin.Y < bMax.Y + margin && aMax.Y > bMin.Y - margin;

    private static void DrawCommanderDiamond(ImDrawListPtr dl, Vector2 centre, float r, uint color)
    {
        dl.AddQuadFilled(centre + new Vector2(0, -r), centre + new Vector2(r, 0),
            centre + new Vector2(0, r), centre + new Vector2(-r, 0), color);
        dl.AddQuad(centre + new Vector2(0, -r), centre + new Vector2(r, 0),
            centre + new Vector2(0, r), centre + new Vector2(-r, 0), 0xFF202020, 1.5f);
    }

    private void DrawCommanderUnitMarker(ImDrawListPtr dl, RtsZoneIntelUnitWire unit, Vector2 p, float s, bool hover)
    {
        string name = CommanderUnitName(unit.Guid);
        float r = (hover ? 9f : 7f) * s;
        uint color = unit.Alive ? CommanderClassColor(BotClassName(unit.Guid, name)) : 0xFF606060;
        dl.AddCircleFilled(p, r, color);
        dl.AddCircle(p, r, 0xFF101010, 0, 2f * s);
        if (unit.Guid == _net?.PlayerGuid)
            DrawCommanderDiamond(dl, p + new Vector2(0, -r - 6f * s), 4f * s, VanillaGold);
        // Name label with a hard shadow — the map art underneath is busy.
        float size = 12f * s;
        Vector2 measured = CmdMeasure(name, size);
        Vector2 at = p + new Vector2(-measured.X * 0.5f, r + 3f * s);
        CmdText(dl, size, at + new Vector2(1f, 1f) * s, 0xE0000000, name);
        CmdText(dl, size, at, unit.Alive ? 0xFFF0F0F0u : 0xFFA0A0A0u, name);
    }

    private void DrawCommanderForceMarker(ImDrawListPtr dl, RtsForceUnitWire unit,
        Vector2 p, float s, bool hover, bool selected)
    {
        float r = (hover || selected ? 9f : 7f) * s;
        uint color = unit.Alive ? CommanderClassColor(ClassIdName(unit.Class)) : 0xFF606060;
        dl.AddCircleFilled(p, r, color);
        dl.AddCircle(p, r, selected ? VanillaGold : 0xFF101010, 0,
            (selected ? 3f : 2f) * s);
        if (unit.DeclaredHero)
            DrawCommanderDiamond(dl, p + new Vector2(0, -r - 6f * s), 4f * s,
                unit.HeroDead ? 0xFF707070u : VanillaGold);

        // Names are deliberately lazy: a 200-row page creates markers without
        // issuing 200 vanilla name queries. Only a selected row gets a map label.
        if (!selected) return;
        string name = CommanderForceName(unit.Guid, request: true);
        float size = 12f * s;
        Vector2 measured = CmdMeasure(name, size);
        Vector2 at = p + new Vector2(-measured.X * 0.5f, r + 3f * s);
        CmdText(dl, size, at + new Vector2(1f, 1f) * s, 0xE0000000, name);
        CmdText(dl, size, at, unit.Alive ? 0xFFF0F0F0u : 0xFFA0A0A0u, name);
    }

    private static void DrawCommanderFlyout(ImDrawListPtr dl, Vector2 at, float s, params string[] lines)
    {
        float title = 14f * s, body = 13f * s, gap = 4f * s;
        float w = 0, h = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            float size = i == 0 ? title : body;
            Vector2 m = CmdMeasure(lines[i], size);
            w = MathF.Max(w, m.X);
            h += m.Y + (i > 0 ? gap : 0);
        }
        Vector2 pad = new(10f * s, 8f * s);
        Vector2 min = at, max = at + new Vector2(w, h) + pad * 2;
        Vector2 disp = ImGui.GetIO().DisplaySize;
        Vector2 shift = new(MathF.Min(0, disp.X - 8f - max.X), MathF.Min(0, disp.Y - 8f - max.Y));
        min += shift; max += shift;
        dl.AddRectFilled(min, max, 0xF0181410, 4f * s);
        dl.AddRect(min, max, 0xFF60C0E0, 4f * s);
        float y = min.Y + pad.Y;
        for (int i = 0; i < lines.Length; i++)
        {
            float size = i == 0 ? title : body;
            CmdText(dl, size, new Vector2(min.X + pad.X, y),
                i == 0 ? VanillaGold : 0xFFE6E6E6u, lines[i]);
            y += (float)GameTextLaw.EmPixels(size, 1f) + gap;
        }
    }

    private bool CommanderTab(ImDrawListPtr dl, string id, string label, Vector2 min, Vector2 logicalSize,
        float s, bool selected)
    {
        Vector2 size = logicalSize * s;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hover = ImGui.IsItemHovered();
        uint fill = selected ? 0xE0303A40u : hover ? 0xD0303A40u : 0xB0282C30u;
        dl.AddRectFilled(min, min + size, fill, 4f * s);
        dl.AddRect(min, min + size, selected ? 0xFF60C0E0u : 0x50FFFFFFu, 4f * s);
        DrawCenteredText(dl, min + size * 0.5f, label, 13f * s,
            selected ? 0xFFFFFFFFu : hover ? 0xFFF0ECE4u : 0xFFD0CAC2u);
        return clicked;
    }

    private string CommanderUnitName(ulong guid)
    {
        if (guid == _net?.PlayerGuid) return _net.PlayerName is { Length: > 0 } own ? own : "You";
        foreach (PartyMember m in _partyMembers)
            if (m.Guid == guid) return m.Name;
        if (_playerNames.TryGetValue(guid, out string? name) && name.Length > 0) return name;
        return "Unit";
    }

    private string CommanderForceName(ulong guid, bool request)
    {
        if (_playerNames.TryGetValue(guid, out string? name) && name.Length > 0) return name;
        if (request && _queriedPlayerNames.Add(guid)) _net?.NameQuery(guid);
        return $"Bot {unchecked((uint)guid)}";
    }

    private static uint CommanderClassColor(string className) => className switch
    {
        // The vanilla class palette, ABGR.
        "Warrior" => 0xFF6E9CC7, "Paladin" => 0xFFBA8CF5, "Hunter" => 0xFF73D4AB,
        "Rogue" => 0xFF69F5FF, "Priest" => 0xFFFFFFFF, "Shaman" => 0xFFDE7000,
        "Mage" => 0xFFF0CC69, "Warlock" => 0xFFC98294, "Druid" => 0xFF0A7DFF,
        _ => 0xFFD0D0D0,
    };
}
