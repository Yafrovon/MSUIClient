using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator-mode world tools: teleport presets + a click-to-teleport world map,
// and the target spawner - a searchable creature browser over the vmangos
// creature_template dump (creator-creatures.tsv), spawning locally synthesized
// creatures that animate, can be clicked and targeted, and never fight back.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    // Preset coordinates: capitals + racial starts carry an exact Z (cities sit
    // on WMO floors a terrain sample would miss); outdoor spots ground-snap.
    private static readonly (string Group, string Name, int Map, float X, float Y, float? Z)[]
        CreatorTeleports =
    {
        ("Capitals", "Stormwind", 0, -8842.09f, 626.358f, 94.0867f),
        ("Capitals", "Ironforge", 0, -4981.25f, -881.542f, 501.66f),
        ("Capitals", "Darnassus", 1, 9947.52f, 2482.73f, 1316.21f),
        ("Capitals", "Orgrimmar", 1, 1502.71f, -4415.42f, 22.5573f),
        ("Capitals", "Thunder Bluff", 1, -1277.37f, 124.804f, 131.287f),
        ("Capitals", "Undercity", 0, 1586.48f, 239.562f, -52.149f),
        ("Zones", "Northshire (Elwynn)", 0, -8949.95f, -132.493f, 83.5312f),
        ("Zones", "Goldshire (Elwynn)", 0, -9464f, 62f, null),
        ("Zones", "Crossroads (Barrens)", 1, -443.128f, -2598.87f, null),
        ("Zones", "Ratchet (Barrens)", 1, -956.635f, -3754.71f, null),
        ("Zones", "Sentinel Hill (Westfall)", 0, -10628f, 1037f, null),
        ("Zones", "Lakeshire (Redridge)", 0, -9217f, -2149f, null),
        ("Zones", "Darkshire (Duskwood)", 0, -10515f, -1263f, null),
        ("Zones", "Astranaar (Ashenvale)", 1, 2749f, -404f, null),
        ("Zones", "Auberdine (Darkshore)", 1, 6457f, 484f, null),
        ("Zones", "Gadgetzan (Tanaris)", 1, -7153.9f, -3849.29f, null),
        ("Zones", "Booty Bay (Stranglethorn)", 0, -14297.2f, 530.976f, null),
        ("Zones", "Everlook (Winterspring)", 1, 6721.44f, -4659.09f, null),
        ("Racial starts", "Orc / Troll", 1, -618.518f, -4251.67f, 38.718f),
        ("Racial starts", "Dwarf / Gnome", 0, -6240.32f, 331.033f, 382.758f),
        ("Racial starts", "Night Elf", 1, 10311.3f, 831.463f, 1326.41f),
        ("Racial starts", "Undead", 0, 1676.35f, 1677.45f, 121.67f),
        ("Racial starts", "Tauren", 1, -2917.58f, -257.98f, 52.9968f),
    };

    private int _creatorMapContinent;      // 0 Azeroth, 1 Kalimdor
    private int _creatorMapZoneIndex;      // 0 = whole continent, 1.. = zone list index
    private List<WorldMapAreaInfo>? _creatorMapZones;
    private int _creatorMapZonesFor = -1;

    /// <summary>Teleport with an optional ground-snap: a null Z travels in at 500 and
    /// then snaps to the sampled terrain height once the destination tiles are live.</summary>
    private bool CreatorTeleport(int mapId, float x, float y, float? z, string why)
    {
        EnsureInstanceData();
        bool ok = TravelToMapId(mapId, new Vector3(x, y, z ?? 500f), _controller?.Yaw ?? 0f, why);
        if (ok && z is null && _controller is not null &&
            _terrain?.SampleHeight(x, y) is float ground)
        {
            _controller.Teleport(x, y, ground + 0.5f);
            _window.Camera.Target = _controller.Position;
        }
        if (ok)
        {
            _creatorPanel = CreatorPanel.None;   // get the menus out of the view
            UpdateCreatorLocationPersist(force: true);   // a teleport is worth remembering immediately
        }
        return ok;
    }

    private partial void RegisterCreatorTeleportSections()
    {
        foreach (string group in CreatorTeleports.Select(p => p.Group).Distinct())
        {
            string g = group;
            CreatorSection("Teleport", $"tp-{g}", g, g == "Capitals", () => DrawCreatorTeleportGroup(g));
        }
        RegisterCreatorDestinationSections();
        CreatorSection("Teleport", "tp-map", "World Map - click anywhere to go there", false,
            DrawCreatorMapPicker);
    }

    /// <summary>One preset group as a button grid. Columns follow the live window
    /// width, so widening the panel packs more destinations per row.</summary>
    private void DrawCreatorTeleportGroup(string group)
    {
        EnsureInstanceData();
        NotePanelField("buttons");
        float cs = CreatorUiScale;
        float columnW = CreatorColumnWidth(CreatorTeleports.Select(p => p.Name)) * CreatorButtonMul;
        float rowH = CreatorButtonHeight;
        int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (columnW + 24f * cs)));
        int column = 0;
        foreach (var preset in CreatorTeleports.Where(p => p.Group == group))
        {
            if (column % columns != 0) ImGui.SameLine(column % columns * (columnW + 24f * cs));
            column++;
            if (CreatorAnchoredButton(preset.Name, new Vector2(columnW, rowH)))
                CreatorTeleport(preset.Map, preset.X, preset.Y, preset.Z, preset.Name);
        }
    }

    /// <summary>Continent/zone tile map with click-to-teleport, driven by WorldMapArea.dbc
    /// bounds (the same inversion the vanilla world map's player marker uses, reversed).</summary>
    private void DrawCreatorMapPicker()
    {
        EnsureInstanceData();
        EnsureWorldMapAreas();
        if (_worldMapAreas is null || _gameplayArt is null)
        {
            ImGui.TextDisabled("WorldMapArea.dbc or map art unavailable.");
            return;
        }

        float cs = CreatorUiScale;
        string[] continents = { "Eastern Kingdoms", "Kalimdor" };
        ImGui.SetNextItemWidth(CreatorComboWidth(continents));
        if (ImGui.Combo("##continent", ref _creatorMapContinent, continents, continents.Length))
            _creatorMapZoneIndex = 0;

        int mapId = _creatorMapContinent == 0 ? 0 : 1;
        if (_creatorMapZonesFor != mapId)
        {
            _creatorMapZonesFor = mapId;
            _creatorMapZones = _worldMapAreas.Areas
                .Where(a => a.MapId == (uint)mapId && a.Directory.Length > 0)
                .OrderBy(a => a.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _creatorMapZoneIndex = 0;
        }

        var zoneLabels = new string[1 + (_creatorMapZones?.Count ?? 0)];
        zoneLabels[0] = "(whole continent)";
        for (int i = 0; i < (_creatorMapZones?.Count ?? 0); i++)
            zoneLabels[i + 1] = _creatorMapZones![i].Directory;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(CreatorComboWidth(zoneLabels));
        ImGui.Combo("##zone", ref _creatorMapZoneIndex, zoneLabels, zoneLabels.Length);

        WorldMapAreaInfo area;
        if (_creatorMapZoneIndex > 0 && _creatorMapZones is not null)
            area = _creatorMapZones[_creatorMapZoneIndex - 1];
        else if (!_worldMapAreas.TryGetContinent((uint)mapId, out area))
        {
            ImGui.TextDisabled("No continent entry in WorldMapArea.dbc.");
            return;
        }

        // The authored map is 1002x668 inside 12 256px tiles. Drawn to fit the
        // live window width (freely resizable), keeping the authored aspect.
        float mapW = MathF.Max(ImGui.GetContentRegionAvail().X - 4f * cs, 240f * cs);
        float mapH = mapW * (668f / 1002f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        // THE TILE GRID IS 1024x768; ONLY ITS TOP-LEFT 1002x668 IS THE MAP.
        // Stretching the grid to fill the widget instead (tileW = mapW/4,
        // tileH = mapH/3) squeezed the art 2% across and 13% down, so the world
        // position a click reported drifted further off the further down the map
        // it was picked - which is what "the coords are off" looked like. Draw at
        // the true tile size and clip, exactly like the vanilla world map frame
        // (GameLoop.WorldMap.cs), so the widget rect IS the 1002x668 rect the
        // WorldMapArea.dbc bounds below describe.
        float tile = mapW / 1002f * 256f;
        dl.PushClipRect(origin, origin + new Vector2(mapW, mapH), true);
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt.Handle(
                $@"Interface\WorldMap\{area.Directory}\{area.Directory}{index}.blp");
            if (texture == 0) continue;
            Vector2 min = origin + new Vector2(col * tile, row * tile);
            dl.AddImage((nint)texture, min, min + new Vector2(tile, tile));
        }
        dl.PopClipRect();

        ImGui.InvisibleButton("##creator-map", new Vector2(mapW, mapH));
        if (ImGui.IsItemHovered())
        {
            Vector2 f = (ImGui.GetMousePos() - origin) / new Vector2(mapW, mapH);
            float worldY = area.Left + f.X * (area.Right - area.Left);
            float worldX = area.Top + f.Y * (area.Bottom - area.Top);
            ImGui.SetTooltip($"({worldX:F0}, {worldY:F0})");
            if (ImGui.IsItemClicked())
                CreatorTeleport((int)area.MapId, worldX, worldY, null,
                    $"map click {area.Directory}");
        }
    }

    // ── Target spawner ───────────────────────────────────────────────────────
    // Synthetic guids count up from the base so any number of creatures can
    // stand around at once; every spawn is tracked for despawn and as a missile
    // target.

    private const ulong CreatorDummyGuid = 0xF000_0000_0000_0100UL;

    private CreatorCreatureTable? _creatorCreatures;
    private bool _creatorCreaturesTried;
    private Task<CreatorCreatureTable?>? _creatorCreaturesFetch;
    private string _creatorCreaturesStatus = "";
    private readonly byte[] _creatorCreatureSearchBuf = new byte[64];
    private List<CreatorCreatureTable.Creature>? _creatorCreatureResults;
    private bool _creatorCreatureSearchDirty = true;
    private CreatorCreatureTable.Creature? _creatorSelectedCreature;
    private float _creatorSpawnScale = 1f;
    private int _creatorDummyDisplayId = 1141;   // manual display-id spawn

    /// <summary>
    /// The steed every new spawn is seated on — UNIT_FIELD_MOUNTDISPLAYID with no server to
    /// set it. Defaults to 0 (no mount) so spawns stand on their own feet; dial in a display
    /// id to seat new spawns. 10318 is the Mirage Raceway goblin rocket car, which is a mount
    /// like every other 1.12 "vehicle". Riding one yourself is the mount toolkit's job.
    /// </summary>
    private int _creatorMountDisplayId = 0;
    private ulong _creatorNextSpawnGuid = CreatorDummyGuid;
    private readonly List<(ulong Guid, string Name, uint DisplayId)> _creatorSpawns = new();

    private partial void RegisterCreatorTargetSections()
    {
        CreatorSection("Target", "spawn-browse", "Browse Creatures", true, DrawCreatorCreatureBrowser);
        CreatorSection("Target", "spawn-active",
            _creatorSpawns.Count > 0 ? $"Spawned ({_creatorSpawns.Count})" : "Spawned", true,
            DrawCreatorSpawnList);
        CreatorSection("Target", "spawn-advanced", "Advanced (raw display id)", false,
            DrawCreatorSpawnAdvanced);
    }

    /// <summary>The creature catalogue: the local copy at once, and a fresh one from the web app
    /// (GET /QuestAtlas/CreatureCatalogue) swapped in when its download lands - once per session, so a machine
    /// without the file gets it by itself and newly published World Pack creatures appear. The download is
    /// written back as the offline copy.</summary>
    private void EnsureCreatorCreatures()
    {
        if (_creatorCreaturesFetch is { IsCompleted: true } fetch)
        {
            _creatorCreaturesFetch = null;
            if (fetch.IsCompletedSuccessfully && fetch.Result is { } fresh)
            {
                _creatorCreatures = fresh;
                _creatorCreaturesStatus = "";
            }
            else
            {
                _creatorCreaturesStatus = "download failed: " + (fetch.Exception?.GetBaseException().Message ?? "not a catalogue");
                Console.WriteLine($"[creator] creature catalogue {_creatorCreaturesStatus}");
            }
        }
        if (_creatorCreaturesTried) return;
        _creatorCreaturesTried = true;
        _creatorCreatures = CreatorCreatureTable.Load(_config.RepoRoot);
        if (string.IsNullOrWhiteSpace(SuiWebAppUrl)) { _creatorCreaturesStatus = "no web app configured"; return; }
        string url = SuiWebAppUrl.TrimEnd('/') + "/QuestAtlas/CreatureCatalogue";
        string path = CreatorCreatureTable.PathIn(_config.RepoRoot);
        _creatorCreaturesStatus = "downloading from the web app...";
        _creatorCreaturesFetch = Task.Run(async () =>
        {
            using var http = WebAppHttp.Create(TimeSpan.FromSeconds(60));
            string text = await http.GetStringAsync(url);
            var table = CreatorCreatureTable.Parse(new StringReader(text), url);
            if (table is null || table.Count == 0) return null;
            // Atomic replace: a half-written catalogue must never become the offline copy.
            string tmp = path + ".download";
            await File.WriteAllTextAsync(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return table;
        });
    }

    /// <summary>Type a name (or an entry id), browse the matches, spawn them.
    /// Click selects, double-click spawns immediately.</summary>
    private void DrawCreatorCreatureBrowser()
    {
        EnsureCreatorCreatures();
        float cs = CreatorUiScale;

        if (_creatorCreatures is null)
        {
            ImGui.TextWrapped("Creature catalogue: " + (_creatorCreaturesStatus.Length > 0 ? _creatorCreaturesStatus : "not loaded") +
                              (_creatorCreaturesFetch is null ? " (needs the web app's /QuestAtlas/CreatureCatalogue once)" : ""));
            return;
        }

        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.InputText("##creature-search", _creatorCreatureSearchBuf,
                (uint)_creatorCreatureSearchBuf.Length))
            _creatorCreatureSearchDirty = true;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f * cs);
        ImGui.SliderFloat("Scale", ref _creatorSpawnScale, 0.25f, 4f, "%.2fx");

        string query = BufToString(_creatorCreatureSearchBuf);
        if (_creatorCreatureSearchDirty)
        {
            _creatorCreatureSearchDirty = false;
            _creatorCreatureResults = query.Length >= 2 ? _creatorCreatures.Search(query) : null;
        }

        if (query.Length < 2)
            ImGui.TextDisabled($"Type at least 2 letters ({_creatorCreatures.Count} creatures).");
        else
            ImGui.TextDisabled($"{_creatorCreatureResults?.Count ?? 0} result(s) - " +
                               "click to select, double-click to spawn");

        if (_creatorCreatureResults is { Count: > 0 } results)
        {
            if (BeginCreatorResults("##creature-results", results.Count, 0.60f))
            {
                foreach (var creature in results)
                {
                    bool selected = _creatorSelectedCreature?.Entry == creature.Entry;
                    string rank = CreatorCreatureTable.RankName(creature.Rank);
                    string levels = creature.LevelMin == creature.LevelMax
                        ? $"{creature.LevelMin}" : $"{creature.LevelMin}-{creature.LevelMax}";
                    string sub = creature.SubName.Length > 0 ? $" <{creature.SubName}>" : "";
                    string tag = rank.Length > 0 ? $"  [{rank}]" : "";
                    if (CreatorResultRow($"{creature.Name}{sub}  (lvl {levels}){tag}##{creature.Entry}", selected))
                        _creatorSelectedCreature = creature;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"entry {creature.Entry}  display {creature.DisplayId}");
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            _creatorSelectedCreature = creature;
                            SpawnCreatorCreature(creature.Name, creature.DisplayId, creature.Scale);
                        }
                    }
                }
            }
            EndCreatorResults();
        }

        bool canSpawn = _creatorSelectedCreature is not null;
        if (!canSpawn) ImGui.BeginDisabled();
        if (CreatorButton("Spawn", 100f * cs) && _creatorSelectedCreature is { } pick)
            SpawnCreatorCreature(pick.Name, pick.DisplayId, pick.Scale);
        if (!canSpawn) ImGui.EndDisabled();
        if (_creatorSelectedCreature is { } sel)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"{sel.Name}  (display {sel.DisplayId})");
        }
    }

    /// <summary>Everything currently spawned, with per-row despawn.</summary>
    private void DrawCreatorSpawnList()
    {
        float cs = CreatorUiScale;
        if (_creatorSpawns.Count == 0)
        {
            ImGui.TextDisabled("Nothing spawned. They stand there, animate, and can be " +
                               "clicked and targeted - they never fight back.");
            return;
        }

        ulong? removeGuid = null;
        foreach (var spawn in _creatorSpawns)
        {
            ImGui.PushID((int)(spawn.Guid & 0xFFFF));
            if (ImGui.SmallButton("x")) removeGuid = spawn.Guid;
            ImGui.SameLine();
            bool targeted = _selectionGuid == spawn.Guid;
            ImGui.TextUnformatted($"{spawn.Name}  (display {spawn.DisplayId}){(targeted ? "  [targeted]" : "")}");
            ImGui.PopID();
        }
        if (removeGuid is { } gone) DespawnCreatorCreature(gone);

        ImGui.Spacing();
        if (CreatorButton("Despawn all", 120f * cs))
            while (_creatorSpawns.Count > 0)
                DespawnCreatorCreature(_creatorSpawns[^1].Guid);
    }

    /// <summary>Spawn any CreatureDisplayInfo id directly, for ids the template
    /// dump does not cover.</summary>
    private void DrawCreatorSpawnAdvanced()
    {
        float cs = CreatorUiScale;
        ImGui.SetNextItemWidth(120f * cs);
        ImGui.InputInt("Display id", ref _creatorDummyDisplayId);
        ImGui.SameLine();
        if (CreatorButton("Spawn id", 90f * cs) && _creatorDummyDisplayId > 0)
            SpawnCreatorCreature($"Display {_creatorDummyDisplayId}", (uint)_creatorDummyDisplayId, 0f);
        ImGui.TextDisabled("Spawns use the Scale dial from the browser above.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(120f * cs);
        ImGui.InputInt("Mount display", ref _creatorMountDisplayId);
        ImGui.TextDisabled("Non-zero seats every NEW spawn on that steed. " +
                           "10318 goblin rocket car, 2490 gnome, 2404 riding horse.");
        if (CreatorButton("Mount toolkit", 140f * cs))
        {
            _mountToolkitOpen = true;
            if (_creatorMountDisplayId > 0) Settings.Mounts.RideDisplayId = _creatorMountDisplayId;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("ride anything, move the seat, dial the handling");
    }

    /// <summary>Place a creature in front of the player, on the ground, fanned out
    /// a little so consecutive spawns stand side by side instead of stacking.</summary>
    private void SpawnCreatorCreature(string name, uint displayId, float dbScale)
    {
        if (_controller is null) return;
        float yaw = _controller.Yaw;
        // Fan alternating left/right of straight ahead: 0, +25, -25, +50, ...
        int n = _creatorSpawns.Count;
        float fan = (n + 1) / 2 * 0.44f * ((n & 1) == 0 ? 1f : -1f);
        float angle = yaw + fan;
        var forward = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
        Vector3 spot = _controller.Position + forward * 8f;
        if (_terrain?.SampleHeight(spot.X, spot.Y) is float ground) spot.Z = ground;

        float scale = (dbScale <= 0f ? 1f : dbScale) * _creatorSpawnScale;
        ulong guid = _creatorNextSpawnGuid++;
        ObjectFields fields = ObjectFields.ForSyntheticUnit((int)displayId, scale);
        if (_creatorMountDisplayId > 0)
            fields.SetU32(ObjectFields.UNIT_MOUNTDISPLAYID, (uint)_creatorMountDisplayId);
        _entities.AddSynthetic(new WorldEntity
        {
            Guid = guid,
            Type = ObjectTypeId.Unit,
            Fields = fields,
            Position = spot,
            Orientation = angle + MathF.PI,   // face the player
        });
        _creatorSpawns.Add((guid, name, displayId));
        Console.WriteLine($"[creator] spawned '{name}' display {displayId} scale {scale:F2} " +
                          $"at ({spot.X:F1}, {spot.Y:F1}, {spot.Z:F1})");
    }

    private void DespawnCreatorCreature(ulong guid)
    {
        if (_selectionGuid == guid) CommitSelection(0, beginAttack: false);
        _entities.RemoveSynthetic(guid);
        _creatorSpawns.RemoveAll(s => s.Guid == guid);
    }

    /// <summary>The missile target: the targeted spawn if one is targeted, else the
    /// most recent spawn, else nothing (the caller fires straight ahead).</summary>
    private ulong CreatorMissileTargetGuid()
    {
        foreach (var spawn in _creatorSpawns)
            if (spawn.Guid == _selectionGuid) return spawn.Guid;
        return _creatorSpawns.Count > 0 ? _creatorSpawns[^1].Guid : 0;
    }
}
