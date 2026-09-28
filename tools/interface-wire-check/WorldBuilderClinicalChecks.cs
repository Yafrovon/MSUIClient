using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// World Builder (shared_docs/WORLD_BUILDER.md): the pure coordinate/brush law and the wiring
/// that keeps the client from ever writing the server directly. Run standalone:
/// interface-wire-check --world-builder-only
/// </summary>
internal static class WorldBuilderClinicalChecks
{
    public static void Run()
    {
        // Coordinates: Northshire Abbey sits in Azeroth_32_48, and the tile/vertex mapping round-trips.
        var (col, row) = WorldBuilderLaw.TileOf(-8914f, -135f);
        Check(col == 32 && row == 48, $"TileOf(Northshire) = {col},{row}, expected 32,48");
        Vector2 v = WorldBuilderLaw.VertexWorld(32, 48, 64, 64);
        Check(WorldBuilderLaw.TileOf(v.X - 0.01f, v.Y - 0.01f) == (32, 48), "tile centre vertex maps back to its tile");
        Vector3 w = new(-8914f, -135f, 82f);
        Vector3 back = WorldBuilderLaw.PlacementToWorld(WorldBuilderLaw.WorldToPlacement(w));
        Check(Vector3.Distance(w, back) < 0.01f, "world <-> MODF placement space must round-trip");
        Vector3 p = WorldBuilderLaw.WorldToPlacement(w);
        Check((int)(p.X / WorldBuilderLaw.Tile) == 32 && (int)(p.Z / WorldBuilderLaw.Tile) == 48,
            "placement X follows the ADT column, placement Z the ADT row");

        // Brush: centre at full weight, rim at zero, border vertices reported once per owning tile.
        Check(WorldBuilderLaw.Falloff(0f, 10f, 0.3f) == 1f && WorldBuilderLaw.Falloff(10f, 10f, 0.3f) == 0f,
            "falloff is 1 at the centre and 0 at the rim");
        var edge = WorldBuilderLaw.VertexWorld(32, 48, 0, 64);        // on the tile's north edge
        var hits = WorldBuilderLaw.BrushVertices(edge, 6f, 0.5f);
        Check(hits.Any(h => h.col == 32 && h.row == 48 && h.gridRow == 0) &&
              hits.Any(h => h.col == 32 && h.row == 47 && h.gridRow == 128),
            "a brush on a tile border must edit the shared vertex in BOTH tiles (no seams)");
        Check(WorldBuilderLaw.Dab(WorldBuilderLaw.BrushMode.Raise, 1f, 2f, 10f, 0f, 0f) == 2f &&
              WorldBuilderLaw.Dab(WorldBuilderLaw.BrushMode.Lower, 0.5f, 2f, 10f, 0f, 0f) == -1f &&
              WorldBuilderLaw.Dab(WorldBuilderLaw.BrushMode.Flatten, 1f, 1f, 10f, 0f, 4f) == -6f,
            "raise/lower/flatten dab arithmetic");
        var grid = new float[WorldBuilderLaw.VertexCount];
        grid[0] = 4f; grid[1] = 0f; grid[129] = 0f; grid[130] = 0f;
        Check(WorldBuilderLaw.InnerDelta(grid, 0, 0) == 1f, "inner vertex takes the mean of its four corners");

        // MCNR file order is (worldX, worldY, up): flat ground encodes as (0, 0, 127).
        var (b0, b1, b2) = WorldBuilderLaw.EncodeNormal(0f, 0f);
        Check(b0 == 0 && b1 == 0 && b2 == 127, "flat ground normal must encode as (0,0,127)");

        // Wiring: every edit goes through the web app's audited /WorldPacks API.
        string root = ClientConfig.FindRepoRoot();
        string panel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.cs"));
        string client = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldPackClient.cs"));
        Check(client.Contains("\"/WorldPacks/\"", StringComparison.Ordinal),
            "WorldPackClient must talk to the web app's /WorldPacks API");
        Check(!panel.Contains("SendGmCommand", StringComparison.Ordinal) && !panel.Contains("MySql", StringComparison.Ordinal),
            "the World Builder panel must never write the server directly (web app ops only)");
        Check(panel.Contains("ReadFileExcluding(path, WbPatchName)", StringComparison.Ordinal),
            "terrain preview must be derived from STOCK heights (read past patch-7)");
        string ui = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.Ui.cs"));
        Check(ui.Contains("RegisterCreatorWorldSections();", StringComparison.Ordinal) &&
              ui.Contains("UpdateWorldBuilder();", StringComparison.Ordinal),
            "the World panel must be registered and updated from DrawCreatorHud");

        // Hole void law (2026-09-27, proven live: 10 walk-offs out of the world before, 0 after): a terrain
        // hole with nothing walkable under the feet holds the body on its height field, and a body under
        // the terrain shell with nothing below goes back onto the surface. Tier 2 judges holes with the
        // player's own controller and writes the tier-3 hand-off; script runs never log the account in.
        string controller = SourceText.Read(Path.Combine(root, "MSUIClient", "Player", "CharacterController.cs"));
        Check(controller.Contains("GroundSource = \"hole-void-guard\"", StringComparison.Ordinal) &&
              controller.Contains("GroundSource = \"shell-void-guard\"", StringComparison.Ordinal) &&
              controller.Contains("!WalkableBelow(", StringComparison.Ordinal),
            "CharacterController must keep the hole + shell void guards (walkable-below test, not any-hit)");
        string verify = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.Verify.cs"));
        Check(verify.Contains("new CharacterController(", StringComparison.Ordinal) && verify.Contains("WbHolesPath", StringComparison.Ordinal),
            "tier-2 hole check must drop/walk the player's own controller and write worldpack-holes.json for tier 3");
        Check(verify.Contains("[\"voids\"]", StringComparison.Ordinal) && verify.Contains("WbHealAllWalkOffs", StringComparison.Ordinal),
            "the hole hand-off must list every walk-off spot and \"healhole all\" / \"Heal all walk-offs\" must close them in one op");
        string fight = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev", "GameLoop.LiveRun.Fight.cs"));
        Check(fight.Contains("\"KILLED\"", StringComparison.Ordinal) && fight.Contains("\"WIPE\"", StringComparison.Ordinal) &&
              !fight.Contains(".damage", StringComparison.Ordinal),
            "boss-trial is a balance fight: KILLED / WIPE / TIMEOUT by the group, never a GM damage finish");
        Check(fight.Contains("_liveFightGroundWaitUntil = now + 3", StringComparison.Ordinal),
            "a fight waits for a sliding body to land (a skipped kill leaves a kill quest one short and its turn-in refused)");
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.Net.cs"));
        Check(net.Contains("WbScriptPath is null", StringComparison.Ordinal),
            "a World Builder script run must not auto-login (it kicks a live protocol session on the same account)");
        // Linked packs and patrols: one code path for the panel (Linked pack / Patrol modes) and the script
        // (mobpack / patrol), one op each; the leader lists itself (stock convention), followers move in formation.
        string content = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.Content.cs"));
        string script = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.Script.cs"));
        Check(content.Contains("private JsonArray? WbPackItems(", StringComparison.Ordinal) && content.Contains("private JsonArray? WbPatrolItems(", StringComparison.Ordinal) &&
              content.Contains("WbPackItems(entries, new Vector2(at.X, at.Y)", StringComparison.Ordinal) && content.Contains("_wbPatrolPoints.Select(p => new Vector2(p.X, p.Y))", StringComparison.Ordinal) &&
              script.Contains("case \"mobpack\":", StringComparison.Ordinal) && script.Contains("case \"patrol\":", StringComparison.Ordinal) &&
              script.Contains("case \"movespawn\":", StringComparison.Ordinal),
            "linked packs and patrols have a human tool (panel modes) AND an agent path (mobpack/patrol) through the same builders");
        Check(content.Contains("WbPackFlags = 0x2 | 0x4 | 0x8, WbPatrolFlags = 0x1 | 0x2 | 0x4 | 0x8", StringComparison.Ordinal) &&
              content.Contains("WbGroupRow(leader, leader, 0f, 0f, WbPatrolFlags)", StringComparison.Ordinal),
            "packs aggro/evade/respawn together (14), patrol followers add formation (15), the leader lists itself");
        Check(File.Exists(Path.Combine(root, "tools", "worldpack", "gen-live.py")) && File.Exists(Path.Combine(root, "tools", "worldpack", "gen-wb-verify.py")) &&
              File.Exists(Path.Combine(root, "tools", "worldpack", "navmesh.py")) && File.Exists(Path.Combine(root, "tools", "worldpack", "make-group.py")) &&
              File.Exists(Path.Combine(root, "tools", "worldpack", "launch-wb.ps1")) && File.Exists(Path.Combine(root, "tools", "worldpack", "launch-live.ps1")),
            "the agent tools stay in the repo: generators (gen-live.py, gen-wb-verify.py), navmesh planner, make-group, launch-wb/launch-live");
        // Fair tester = a real level-N character, not a GM-levelled level-1 one (weapon skill 5, defense 1: the first
        // fair run's wolf never left 100%), and a stand-up is full health (".revive" alone is 50%, even alive).
        string genLive = SourceText.Read(Path.Combine(root, "tools", "worldpack", "gen-live.py"));
        string liveRun = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev", "GameLoop.LiveRun.cs"));
        Check(liveRun.Contains("case \"assert-skills-capped\":", StringComparison.Ordinal) &&
              liveRun.Contains("info.CategoryId != 6", StringComparison.Ordinal),
            "the live runner proves weapon skills + defense are at level x 5 from the player's own skill fields");
        Check(genLive.Contains("gm .learn all_trainer", StringComparison.Ordinal) && genLive.Contains("\"gm .maxskill\"", StringComparison.Ordinal) &&
              genLive.Contains("\"assert-skills-capped\"", StringComparison.Ordinal) && genLive.Contains("QUEST_SPELLS", StringComparison.Ordinal),
            "the fair SETUP trains the level's spells + quest abilities, maxes skills and asserts them before any fight");
        Check(genLive.Contains("gm .reset items", StringComparison.Ordinal) && genLive.Contains("inventory ensure-bag", StringComparison.Ordinal) &&
              liveRun.Contains("\"ensure-bag\"", StringComparison.Ordinal),
            "the fair SETUP starts from no items with four bags (a re-applied premade set filled the backpack)");
        Check(liveRun.Contains("case \"kill-for-quest\":", StringComparison.Ordinal) && genLive.Contains("def kill_for_quest(", StringComparison.Ordinal) &&
              genLive.Contains("kill_for_quest(qid, f\"kill:", StringComparison.Ordinal) && genLive.Contains("kill_for_quest(qid, f\"item:", StringComparison.Ordinal),
            "fair quest objectives are earned by kill-for-quest (attackers first, pull beside, wait for respawn, loot items) - " +
            "never a fixed per-spawn list that idles in a camp and revisits dead spawns");
        string questKills = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev", "GameLoop.LiveRun.QuestKills.cs"));
        Check(questKills.Contains("TryCast(LiveRotationSpell(spell))", StringComparison.Ordinal) &&
              fight.Split("TryCast(LiveRotationSpell(spell))").Length == 3 && !fight.Contains("TryCast(spell)", StringComparison.Ordinal),
            "a rotation casts the highest KNOWN rank of its spell's chain (trained Heroic Strike 6 superseded the named rank 5: " +
            "every cast was refused and every fight ran on auto-attack)");
        Check(genLive.Contains("def stand_up(", StringComparison.Ordinal) && genLive.Contains("\"gm .replenish\"", StringComparison.Ordinal) &&
              genLive.Contains("gm .group replenish", StringComparison.Ordinal),
            "every stand-up is .revive + .replenish (revive alone leaves 50% health) and each boss pull starts topped up");
        Check(genLive.Contains("\"gm .group revive\"", StringComparison.Ordinal),
            "each boss trial starts with the whole group standing (a wipe left the bots dead: the next 'trial' was the tester alone)");
        // Dungeon trash and patrols are proven like bosses (owner 2026-09-27: "the dungeon and the packs must feel good"):
        // every linked pack / patrol is a pack-trial with the group before its boss, every patrol is watched walking its
        // loop, and every pull opens the way a group opens it - gathered 30 yd out, the tank bot pulls, the tester joins.
        string packTrial = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev", "GameLoop.LiveRun.PackTrial.cs"));
        Check(liveRun.Contains("case \"pack-trial\":", StringComparison.Ordinal) && liveRun.Contains("case \"patrol-watch\":", StringComparison.Ordinal) &&
              packTrial.Contains("\"CLEARED\"", StringComparison.Ordinal) && packTrial.Contains("\"WIPE\"", StringComparison.Ordinal) &&
              !packTrial.Contains(".damage", StringComparison.Ordinal) && packTrial.Contains("followersNear=", StringComparison.Ordinal),
            "pack-trial (CLEARED/WIPE/TIMEOUT by the group, never a GM damage finish) and patrol-watch (far point reached, followers with it) exist");
        Check(liveRun.Contains("case \"patrol-at\":", StringComparison.Ordinal) && packTrial.Contains("if (d <= 6f && _patrolWatchSeenAway)", StringComparison.Ordinal),
            "a patrol proves it walks only after it was seen away from the point (a watch that began at the far point passed in 0 s)");
        Check(genLive.Contains("f\"pack-trial {ROTATION} 240", StringComparison.Ordinal) && genLive.Contains("f\"patrol-watch {leader}", StringComparison.Ordinal) &&
              genLive.Contains("def gather(", StringComparison.Ordinal) && genLive.Contains("f\"boss-trial {ROTATION} {seconds} pull\"", StringComparison.Ordinal) &&
              genLive.Contains("gm .namego {b}", StringComparison.Ordinal) &&
              !genLive.Contains(".partybot", StringComparison.Ordinal) && !fight.Contains(".partybot", StringComparison.Ordinal) &&
              !packTrial.Contains(".partybot", StringComparison.Ordinal),
            "the fair dungeon run pulls every pack/patrol before its boss with a SuperUI bot group gathered 30 yd out " +
            "(.namego) - NEVER stock vmangos .partybot bots, they are not part of this project (owner, 2026-09-27)");
        Check(content.Contains("WorldBuilderLaw.PositiveAngle(angle)", StringComparison.Ordinal) &&
              Math.Abs(WorldBuilderLaw.PositiveAngle(-1.9f) - (MathF.Tau - 1.9f)) < 1e-4f && WorldBuilderLaw.PositiveAngle(MathF.Tau) == 0f,
            "creature_groups.angle is FLOAT UNSIGNED: pack/patrol angles go in as [0, 2pi) (build #28 was refused mid-install)");

        // Pack-map world map = the mosaic of its minimap tiles (no WorldMapArea row, no painted art):
        // 3:2 bounds, every tile inside, west/north on the left/top like a stock WorldMapArea row.
        var gilneas = Enumerable.Range(30, 4).SelectMany(c => Enumerable.Range(30, 5).Select(r => (c, r))).ToList();
        Check(WorldMapUiLaw.TryMinimapMosaicBounds(gilneas, out float ml, out float mr, out float mt, out float mb) &&
              MathF.Abs((ml - mr) / (mt - mb) - 1002f / 668f) < 0.01f &&
              ml >= (32 - 30) * WorldBuilderLaw.Tile && mr <= (31 - 33) * WorldBuilderLaw.Tile &&
              mt >= (32 - 30) * WorldBuilderLaw.Tile && mb <= (31 - 34) * WorldBuilderLaw.Tile,
            "the pack-map world map mosaic must be 3:2 and contain every tile (left = west = larger Y, top = north = larger X)");
        Check(!WorldMapUiLaw.TryMinimapMosaicBounds([], out _, out _, out _, out _), "no tiles = no mosaic");
        string worldMap = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.WorldMap.cs"));
        Check(worldMap.Contains("zoneTiles.Add((col, row, sea))", StringComparison.Ordinal),
            "a pack zone's view fills tiles without a minimap with the shared open-sea image (not the frame's parchment)");
        Check(worldMap.Contains("WorldMapUiLaw.SharedSeaTexture(", StringComparison.Ordinal) &&
              WorldMapUiLaw.SharedSeaTexture([11u, 12u, 13u, 14u]) == 0 &&
              WorldMapUiLaw.SharedSeaTexture([11u, 11u, 12u, 13u]) == 0 &&
              WorldMapUiLaw.SharedSeaTexture(Enumerable.Repeat(7u, 165).Concat([11u, 12u, 11u])) == 7u,
            "the open-sea fill is an image shared by >= MinSharedSeaTiles tiles; a small dungeon (4 unique tiles) " +
            "gets no fill, never a copy of one of its own tiles around the edges");
        Check(worldMap.Contains("displayMap < WorldMapUiLaw.PackMapIdBase", StringComparison.Ordinal),
            "the minimap mosaic is for pack maps only - stock maps and instances keep their vanilla world map");
        Check(!worldMap.Contains("WorldMapStampedTiles", StringComparison.Ordinal) &&
              !worldMap.Contains("WbPublishedStampedTiles", StringComparison.Ordinal) &&
              !worldMap.Contains(@"WorldPacks\build.json", StringComparison.Ordinal) &&
              worldMap.Contains("mosaic is null && painted == 0 && haveMapArea", StringComparison.Ordinal),
            "painted continent maps must not receive stamped minimap rectangles; zone mosaics are only a fallback for missing art");
        Check(worldMap.Contains("_worldMapHits?.TryResolveArea(", StringComparison.Ordinal) &&
              worldMap.Contains("DrawWorldMapAreaHighlight(dl, hoveredArea, mapMin, mapSize)", StringComparison.Ordinal),
            "pack zone hover and highlights use the continent's normal ZMP and WorldMapArea data");
        Check(panel.Contains("WbPublishedStampedTiles((uint)_config.Start.Map).Contains(key)", StringComparison.Ordinal) &&
              panel.Contains("_wbPublishedStampCache = null", StringComparison.Ordinal),
            "sculpt previews retain published stamp ownership and refresh it after mounting a new pack build");
        Check(panel.Contains("_worldMapReloadPending = true", StringComparison.Ordinal) &&
              worldMap.Contains("if (_worldMapReloadPending) ReloadWorldMapData()", StringComparison.Ordinal) &&
              worldMap.Contains("_worldMapHits = null", StringComparison.Ordinal) &&
              worldMap.Contains("_worldMapHighlights = null", StringComparison.Ordinal) &&
              worldMap.Contains("_worldMapAreasLoaded = false", StringComparison.Ordinal) &&
              worldMap.Contains("_minimapTileMap = null", StringComparison.Ordinal) &&
              worldMap.Contains("_gameplayArt?.ClearMapCache()", StringComparison.Ordinal),
            "a pack mount refreshes map bounds, hover ownership, highlights and minimap art before the next frame");
        const string outline = """
            {"map":0,"area":7001,"directory":"Gilneas","polygon":[[-1100,1500],[-1100,2100],[-1700,1700]],"terrain":{"seaLevel":0}}
            """;
        Check(WorldBuilderLaw.TryParseWorldMapOutline(outline, out var outlineBody, out _) &&
              outlineBody?["terrain"] is System.Text.Json.Nodes.JsonObject,
            "world-map outline import accepts world-coordinate polygons and preserves build-side terrain settings");
        Check(!WorldBuilderLaw.TryParseWorldMapOutline(outline.Replace("7001", "130"), out _, out _) &&
              !WorldBuilderLaw.TryParseWorldMapOutline(outline.Replace("Gilneas", "../Gilneas"), out _, out _) &&
              !WorldBuilderLaw.TryParseWorldMapOutline(outline.Replace("[-1700,1700]", "[1e999,1700]"), out _, out _) &&
              !WorldBuilderLaw.TryParseWorldMapOutline("{", out _, out _),
            "outline import refuses stock area IDs, path escapes, nonfinite coordinates and malformed JSON");
        string region = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.Region.cs"));
        Check(region.Contains("Load world-map outline", StringComparison.Ordinal) &&
              region.Contains("WorldBuilderLaw.TryParseWorldMapOutline(", StringComparison.Ordinal) &&
              region.Contains("[\"kind\"] = \"worldmap\"", StringComparison.Ordinal),
            "world-map outline import is available in Creator Mode and saves through the audited content API");

        // Pack data the client needs beyond the archive's own formats travels INSIDE patch-7 or the sync:
        // portal names/destinations (the reference TSV format) and a sha1-keyed collision sync.
        string instances = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.Instances.cs"));
        Check(instances.Contains("MergePack(", StringComparison.Ordinal),
            "pack portals must be merged into the teleport table from patch-7 (else they are nameless volumes)");
        string sync = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldPackCollisionSync.cs"));
        Check(sync.Contains("already current", StringComparison.Ordinal) && sync.Contains("Sha1Of(local)", StringComparison.Ordinal),
            "the collision sync must skip files already byte-identical to the server's (sha1)");

        QuestCreator();
    }

    /// <summary>Quest Creator law: a draft yields everything the quest needs to WORK (verifier C3/C4/C8).</summary>
    private static void QuestCreator()
    {
        static long N(System.Text.Json.Nodes.JsonNode? n) => n is null ? 0 : (long)double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        static double D(System.Text.Json.Nodes.JsonNode? n) => n is null ? 0 : double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        var giver = new System.Text.Json.Nodes.JsonObject { ["entry"] = 7000900u, ["npc_flags"] = 1u, ["gossip_menu_id"] = 62900u, ["loot_id"] = 0u };
        var wolf = new System.Text.Json.Nodes.JsonObject { ["entry"] = 7000901u, ["npc_flags"] = 0u, ["gossip_menu_id"] = 0u, ["loot_id"] = 0u };
        var ctx = new QuestAuthoringLaw.Context { NextTriggerId = 7150 };
        ctx.PackTemplates[7000900] = giver;
        ctx.PackTemplates[7000901] = wolf;
        var d = new QuestAuthoringLaw.Draft { Entry = 7000950, Title = "T", Giver = 7000900 };
        d.Goals[0] = new() { Kind = QuestAuthoringLaw.ObjectiveKind.Collect, Item = 5637, Count = 6, DropFrom = 7000901, DropChance = 40 };
        d.Goals[1] = new() { Kind = QuestAuthoringLaw.ObjectiveKind.Explore, ExploreMap = 800, ExploreX = 1, ExploreY = 2, ExploreZ = 3, ExploreRadius = 15 };
        d.Goals[2] = new() { Kind = QuestAuthoringLaw.ObjectiveKind.UseObject, Target = 12345, Count = 1 };
        Check(QuestAuthoringLaw.Problems(d, ctx).Count == 0, "a complete draft has no problems");
        var items = QuestAuthoringLaw.Items(d, ctx).OfType<System.Text.Json.Nodes.JsonObject>().ToList();
        System.Text.Json.Nodes.JsonObject? Body(string kind, Func<System.Text.Json.Nodes.JsonObject, bool>? where = null) =>
            items.Where(i => (string?)i["kind"] == kind).Select(i => (System.Text.Json.Nodes.JsonObject)i["body"]!)
                 .FirstOrDefault(b => where?.Invoke(b) ?? true);
        var q = Body("dbrow:quest_template")!;
        Check(N(q["ReqItemId1"]) == 5637 && N(q["ReqItemCount1"]) == 6, "collect objective fills item slot 1");
        Check(N(q["ReqCreatureOrGOId1"]) == -12345, "use-object objective is a NEGATIVE creature/GO id");
        Check(N(q["SpecialFlags"]) == 2, "an explore objective sets the exploration flag");
        var loot = Body("dbrow:creature_loot_template")!;
        Check(N(loot["entry"]) == 7000901 && D(loot["ChanceOrQuestChance"]) < 0,
            "a drop source without loot_id gets loot_id = its entry, and the drop is quest-only (negative chance)");
        Check(Body("dbrow:creature_template", b => N(b["entry"]) == 7000901)?["loot_id"] is { } l && N(l) == 7000901,
            "the drop source's template is re-saved with its new loot_id");
        Check(items.Any(i => (string?)i["kind"] == "dbc:AreaTrigger" && (string?)i["key"] == "7150") &&
              Body("dbrow:areatrigger_template", b => N(b["id"]) == 7150) is not null &&
              Body("dbrow:areatrigger_involvedrelation", b => N(b["id"]) == 7150 && N(b["quest"]) == 7000950) is not null,
            "an explore objective needs the client trigger (AreaTrigger.dbc), the server trigger and the relation");
        Check(items.First(i => (string?)i["kind"] == "dbc:AreaTrigger")["body"]!["fields"]!.AsObject().All(f => int.Parse(f.Key) >= 1),
            "a DBC doc never sets field 0 - the build takes the id from the doc key (build #14 failed on that)");
        var giverTpl = Body("dbrow:creature_template", b => N(b["entry"]) == 7000900);
        Check(giverTpl is not null && (N(giverTpl["npc_flags"]) & 2) != 0, "a pack giver without the quest-giver flag gets it");
        Check(Body("dbrow:gossip_menu_option", b => N(b["menu_id"]) == 62900 && N(b["npc_option_npcflag"]) == 2) is not null,
            "a pack giver with its own gossip menu gets the QUESTGIVER option (or its quests never list)");
        // Editing a saved quest reuses its trigger instead of allocating another.
        var edit = new QuestAuthoringLaw.Context { NextTriggerId = 7300 };
        edit.PackTemplates[7000900] = giver; edit.PackTemplates[7000901] = wolf;
        edit.ExploreTriggerOf[7000950] = 7150;
        Check(QuestAuthoringLaw.Items(d, edit).OfType<System.Text.Json.Nodes.JsonObject>().Any(i => (string?)i["kind"] == "dbc:AreaTrigger" && (string?)i["key"] == "7150"),
            "an edited quest keeps its exploration trigger id");
        d.Goals[0].DropFrom = 12345;   // stock creature
        Check(QuestAuthoringLaw.Problems(d, ctx).Any(p => p.Contains("drop source")), "a stock creature cannot be made a drop source");
        Check(QuestAuthoringLaw.Preview("Hail, $N the $C.$BGo $Ghe:she;.") == "Hail, Testwar the Warrior.\nGo he.",
            "preview substitutes $N $C $B $G");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("WorldBuilder: " + message);
    }
}
