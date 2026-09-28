using System.Globalization;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder script driver (shared_docs/WORLD_BUILDER.md §4, "scripted").
//
//   MSUI_WB_SCRIPT=<file>   the client boots straight into the creator world and runs the file
//                           line by line; lines appended later are picked up (a live console).
//
// Every command goes through the SAME code the World Builder panel uses (WbOp → the web
// app's /WorldPacks API → audited op), so a script is a repeatable proof of the UI path and
// the way whole zones get laid out without clicking 300 times.
//
//   enter | panel world|none | pack <key> [name...] | enable <key> on|off
//   goto <x> <y> <z|ground> [facing] | fly on|off | cam <yawDeg> <pitchDeg> <distance>
//   place <modelPath|"path with spaces"> <x> <y> <z|ground> [heading] [scale] [zOffset]
//   move <placementId> <x> <y> <z|ground> [heading] | pad <placementId> [margin] [falloff]
//   sculpt raise|lower|smooth|flatten <x> <y> <radius> <amount> [dabs]
//   undo | refresh | publish [restart 0|1] | wait-build | download | reload
//   wait <seconds> | wait-idle | shot <name> | log <text> | hour <0..24> | quit
//   spawn/spawnz/spawnring (pack creatures) | preflight | verify [rerun] | verify-spawns [fix] [changed]
//   mobpack <entry[,entry..]> <count> <cx> <cy> <spread> (linked pack) | movespawn <guid> <x> <y> [facing]
//   patrol <leaderEntry> <followerEntry> <followers> <x1> <y1> <x2> <y2> [...] (looping route, formation)
//   survey <x> <y> <dist> <name>
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string? WbScriptPath = Environment.GetEnvironmentVariable("MSUI_WB_SCRIPT");

    private int _wbScriptLine;
    private double _wbScriptWaitUntil;
    private string? _wbScriptWaitFor;      // "build" | "download" | "idle" | "world"
    private double _wbScriptPolledAt;
    private bool _wbScriptBooted;
    private int _wbScriptBuildBefore;   // wait-build waits for a build NEWER than this

    private void UpdateWorldBuilderScript()
    {
        if (WbScriptPath is null) return;
        double now = NowSeconds();

        if (!_wbScriptBooted)
        {
            if (_gl is null) return;
            if (!_worldLoadStarted && now > 1.0)
            {
                Console.WriteLine("[wbscript] entering creator world");
                EnterOfflineWorld();
            }
            if (_worldLoading || !_creatorWorldRequested || _controller is null) return;
            _wbScriptBooted = true;
            _wbScriptWaitUntil = now + 2.0;
            _creatorPanel = CreatorPanel.World;
            WbRequestState();
            _wbScriptWaitFor = "idle";
            Console.WriteLine($"[wbscript] world ready; running {WbScriptPath}");
        }

        if (now < _wbScriptWaitUntil) return;
        if (_wbScriptWaitFor is { } waitFor)
        {
            bool done = waitFor switch
            {
                "build" => _wbStatus is { Running: false } s && s.BuildId > _wbScriptBuildBefore && _wbStatusTask is null && now - _wbScriptPolledAt > 3,
                "download" => _wbDownloadTask is null && _wbCollisionTask is null && !_travelInProgress,
                "idle" => _wbOps.Count == 0 && _wbStateTask is null,
                "world" => !_worldLoading && !_travelInProgress,
                "docs" => _wbDocsTask is null && _wbDocs is not null,
                "verify" => _wbVerifyTask is null,
                _ => true,
            };
            if (waitFor == "build" && _wbStatusTask is null && now - _wbStatusAt > 2.0)
            {
                _wbStatusAt = now;
                _wbStatusTask = _wbClient.GetStatusAsync(SuiWebAppUrl);
            }
            if (!done) return;
            if (waitFor == "build")
                Console.WriteLine($"[wbscript] build #{_wbStatus!.BuildId} {_wbStatus.Status}" +
                                  (_wbStatus.Error is { } e ? $": {e}" : "") +
                                  $" (installed {_wbStatus.FilesInstalled}, restored {_wbStatus.FilesRestored}, restart {_wbStatus.ServerRestarted})");
            _wbScriptWaitFor = null;
        }

        // Commands a script command expanded into (verify-spawns, survey) run before the next file line.
        if (_wbScriptInject.Count > 0)
        {
            string injected = _wbScriptInject.Dequeue();
            Console.WriteLine($"[wbscript] >> {injected}");
            try { RunWbScriptCommand(injected, now); }
            catch (Exception ex) { Console.WriteLine($"[wbscript] ERROR: {ex.Message}"); }
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(WbScriptPath); }
        catch { _wbScriptWaitUntil = now + 1.0; return; }
        if (_wbScriptLine >= lines.Length) { _wbScriptWaitUntil = now + 0.5; return; }

        string line = lines[_wbScriptLine++].Trim();
        if (line.Length == 0 || line.StartsWith('#')) return;
        Console.WriteLine($"[wbscript] > {line}");
        try { RunWbScriptCommand(line, now); }
        catch (Exception ex) { Console.WriteLine($"[wbscript] ERROR: {ex.Message}"); }
    }

    /// <summary>Ground under a point: WMO/M2 collision (resident tiles), else the rendered terrain,
    /// else the raw ADT outer grid (any tile, resident or not).</summary>
    private float WbGroundZ(float x, float y)
    {
        float? terrain = _terrain?.SampleHeight(x, y);
        float top = (terrain ?? 0f) + 60f;
        if (_collision?.Raycast(new Vector3(x, y, top), -Vector3.UnitZ, 200f) is { } hit &&
            (terrain is null || hit.Point.Z >= terrain.Value - 0.5f))
            return hit.Point.Z;
        if (terrain is { } t) return t;
        var (col, row) = WorldBuilderLaw.TileOf(x, y);
        var adt = _adts?.Get(col, row);
        if (adt?.Chunks is null) return 0f;
        int gr = Math.Clamp((int)(((32 - row) * WorldBuilderLaw.Tile - x) / WorldBuilderLaw.Unit), 0, 128);
        int gc = Math.Clamp((int)(((32 - col) * WorldBuilderLaw.Tile - y) / WorldBuilderLaw.Unit), 0, 128);
        var ch = adt.Chunks.FirstOrDefault(c => c?.IndexY == Math.Min(gr / 8, 15) && c.IndexX == Math.Min(gc / 8, 15));
        return ch?.Heights is null ? 0f : ch.WorldHeightAt(gc - ch.IndexX * 8, gr - ch.IndexY * 8);
    }

    /// <summary>Nearest open-ground spot searched in widening rings up to <paramref name="maxRadius"/>:
    /// no roof over it, feet on the real floor (terrain or low collision — the SAME rule the tier-2
    /// verifier checks), and not pressed against walls. Needs the tiles resident.</summary>
    private Vector3? WbOpenGround(float x, float y, float maxRadius = 24f)
    {
        for (float r = 0f; r <= maxRadius; r += 3f)
        {
            int steps = r == 0f ? 1 : 12;
            for (int i = 0; i < steps; i++)
            {
                float px = x + MathF.Cos(i * MathF.Tau / steps) * r, py = y + MathF.Sin(i * MathF.Tau / steps) * r;
                float? t = _terrain?.SampleHeight(px, py);
                if (t is null) continue;
                var top = _collision?.Raycast(new Vector3(px, py, t.Value + 60f), -Vector3.UnitZ, 200f);
                if (top is { } h && h.Point.Z > t.Value + 0.8f) continue;               // under a roof / on a building
                var feet = new Vector3(px, py, top is { } low ? MathF.Max(t.Value, low.Point.Z) : t.Value);
                if (WbWallsAround(feet) >= 3) continue;                                   // squeezed against geometry
                if (WbSlopeDegrees(px, py) > 45f) continue;                               // cliff lip: slides off, cannot chase
                if (WbWaterLevel(px, py) is { } water && water - feet.Z > 1.0f) continue;  // in a lake or the sea
                return feet;
            }
        }
        return null;   // never fall back to the topmost surface: that is a roof
    }

    private void RunWbScriptCommand(string line, double now)
    {
        // Tokens split on spaces; "double quotes" keep a model path with spaces ("Collidable Doodads") whole.
        var a = System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]*)\"|(\\S+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
        int N(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
        float Z(int i, float x, float y) => a[i].Equals("ground", StringComparison.OrdinalIgnoreCase)
            ? _terrain?.SampleHeight(x, y) ?? throw new InvalidOperationException($"no terrain under ({x}, {y})")
            : F(i);

        switch (a[0].ToLowerInvariant())
        {
            case "log":
                Console.WriteLine("[wbscript] " + line[3..].Trim());
                break;
            case "wait":
                _wbScriptWaitUntil = now + F(1);
                break;
            case "wait-idle":
                _wbScriptWaitFor = "idle";
                break;
            case "panel":
                _creatorPanel = a[1] == "world" ? CreatorPanel.World : CreatorPanel.None;
                break;
            case "refresh":
                WbRequestState();
                _wbScriptWaitFor = "idle";
                break;
            case "pack":
            {
                string key = a[1];
                string name = a.Length > 2 ? string.Join(' ', a[2..]) : key;
                var existing = _wbState?.Packs.FirstOrDefault(p => p.PackKey == key);
                if (existing is not null) { _wbPackId = existing.Id; WbRequestState(); }
                else WbOp($"create pack {key}", _wbClient.CreatePackAsync(SuiWebAppUrl, key, name, "created by World Builder script"),
                        r => { if (r.Pack is { } p) _wbPackId = p.Id; });
                _wbScriptWaitFor = "idle";
                break;
            }
            case "enable":
            {
                var pack = _wbState?.Packs.FirstOrDefault(p => p.PackKey == a[1]) ?? throw new InvalidOperationException($"no pack {a[1]}");
                WbOp($"enable {a[1]}", _wbClient.SetEnabledAsync(SuiWebAppUrl, pack.Id, a[2] == "on"));
                _wbScriptWaitFor = "idle";
                break;
            }
            case "goto":
            {
                float x = F(1), y = F(2);
                float z = a[3] == "ground" ? (_terrain?.SampleHeight(x, y) ?? 200f) + 2f : F(3);
                _controller!.Teleport(x, y, z);
                if (a.Length > 4) _controller.Yaw = F(4);
                _window.Camera.Target = _controller.Position;
                _wbScriptWaitUntil = now + 2.0;
                break;
            }
            case "fly":
                _controller!.Flying = a[1] == "on";
                break;
            case "hour":
                // hour <0..24>: pin the time of day (settings' "Fixed" source) - surveys in daylight.
                Settings.Lighting.TimeSource = TimeSource.Fixed;
                Settings.Lighting.TimeOfDay = F(1);
                _timeSource = TimeSource.Fixed;
                _devTimePin = false;
                _atmosphere.TimeOfDayHours = F(1);
                break;
            case "cam":
                _window.Camera.OrbitYaw = 0f;
                _window.Camera.Yaw = F(1) * MathF.PI / 180f;
                _window.Camera.Pitch = F(2) * MathF.PI / 180f;
                _window.Camera.Distance = F(3);
                break;
            case "place":
            {
                string model = a[1];
                float x = F(2), y = F(3);
                float z = Z(4, x, y) + (a.Length > 7 ? F(7) : 0f);
                var p = new WorldPackClient.Placement
                {
                    MapId = _config.Start.Map,
                    Kind = WbModelIsM2(model) ? "m2" : "wmo",
                    ModelPath = model,
                    PosX = x, PosY = y, PosZ = z,
                    RotY = a.Length > 5 ? F(5) : 0f,
                    Scale = a.Length > 6 ? F(6) : 1f,
                };
                if (_wbPackId == 0) throw new InvalidOperationException("no pack selected");
                WbOp($"place {Path.GetFileName(model)}", _wbClient.PlaceAsync(SuiWebAppUrl, _wbPackId, p),
                    r => { if (r.Result?.Placement is { } placed) { _wbSelected = placed.Id; Console.WriteLine($"[wbscript] placed #{placed.Id} at ({placed.PosX:F1}, {placed.PosY:F1}, {placed.PosZ:F1})"); } });
                _wbScriptWaitFor = "idle";
                break;
            }
            case "move":
            {
                // move <placementId> <x> <y> <z|ground> [heading]: one audited Move op (undoable).
                int id = int.Parse(a[1], CultureInfo.InvariantCulture);
                var cur = _wbState?.Placements.FirstOrDefault(p => p.Id == id && !p.Deleted)
                          ?? throw new InvalidOperationException($"no live placement #{id} on this map");
                float x = F(2), y = F(3);
                var moved = new WorldPackClient.Placement
                {
                    Id = cur.Id, PackId = cur.PackId, MapId = cur.MapId, Kind = cur.Kind, ModelPath = cur.ModelPath,
                    PosX = x, PosY = y, PosZ = Z(4, x, y), RotX = cur.RotX, RotY = a.Length > 5 ? F(5) : cur.RotY,
                    RotZ = cur.RotZ, Scale = cur.Scale, DoodadSet = cur.DoodadSet,
                };
                WbOp($"move #{id}", _wbClient.MoveAsync(SuiWebAppUrl, moved), _ => WbRequestState());
                _wbScriptWaitFor = "idle";
                break;
            }
            case "pad":
            {
                // pad <placementId> [margin 2] [falloff 8]: level the terrain under a building to its floor.
                int id = int.Parse(a[1], CultureInfo.InvariantCulture);
                int n = WbPadPlacement(id, a.Length > 2 ? F(2) : 2f, a.Length > 3 ? F(3) : 8f);
                Console.WriteLine(n < 0 ? $"[wbscript] ERROR pad #{id}: placement/model/terrain unavailable (tiles resident?)"
                                        : $"[wbscript] pad #{id}: {n} vertex/vertices levelled");
                _wbScriptWaitFor = "idle";
                break;
            }
            case "sculpt":
            {
                var mode = a[1].ToLowerInvariant() switch
                {
                    "raise" => WorldBuilderLaw.BrushMode.Raise,
                    "lower" => WorldBuilderLaw.BrushMode.Lower,
                    "smooth" => WorldBuilderLaw.BrushMode.Smooth,
                    _ => WorldBuilderLaw.BrushMode.Flatten,
                };
                var centre = new Vector2(F(2), F(3));
                float radius = F(4), amount = F(5);
                int dabs = a.Length > 6 ? int.Parse(a[6], CultureInfo.InvariantCulture) : 1;
                if (_wbPackId == 0) throw new InvalidOperationException("no pack selected");
                float saveRadius = _wbRadius, saveHard = _wbHardness;
                _wbRadius = radius;
                _wbBrush = mode;
                _wbStroke.Clear();
                _wbFlattenTarget = _terrain?.SampleHeight(centre.X, centre.Y) ?? 0f;
                for (int i = 0; i < dabs; i++) WbDab(centre, mode, amount);
                WbFinishStroke();
                _wbRadius = saveRadius;
                _wbHardness = saveHard;
                _wbScriptWaitFor = "idle";
                break;
            }
            case "undo":
                WbUndo();
                _wbScriptWaitFor = "idle";
                break;
            case "publish":
                _wbScriptBuildBefore = Math.Max(_wbStatus?.BuildId ?? 0, _wbStatus?.LastBuild?.BuildId ?? _wbState?.LastBuild?.BuildId ?? 0);
                WbOp("publish", _wbClient.PublishAsync(SuiWebAppUrl, a.Length < 2 || a[1] != "0"));
                _wbStatus = null;
                _wbScriptPolledAt = now;
                _wbScriptWaitUntil = now + 3.0;
                break;
            case "wait-build":
                _wbScriptPolledAt = now;
                _wbScriptWaitFor = "build";
                break;
            case "download":
                WbStartDownload();
                _wbScriptWaitFor = "download";
                break;
            case "reload":
                WbReloadWorld();
                _wbScriptWaitFor = "world";
                break;
            case "shot":
                _currentVantage = "wb-" + a[1];
                ArmGameplayDump();
                _wbScriptWaitUntil = now + 1.5;
                break;
            case "ghosts":
                _wbGhostsHidden = a[1] == "off";
                _wbScriptWaitUntil = now + 1.0;
                break;
            case "status":
                Console.WriteLine($"[wbscript] status: pack {_wbPackId}, map {_config.Start.Map}, " +
                                  $"{_wbState?.Placements.Count(p => !p.Deleted) ?? 0} placement(s), " +
                                  $"{_wbGhosts.Count} ghost(s) drawn, {_wbServerSculpt.Count} sculpted tile(s), " +
                                  $"{_wbPreviewTiles.Count} preview tile(s), mounted build {_wbMountedBuild?.ToString() ?? "none"}, " +
                                  $"player ({_controller?.Position.X:F1}, {_controller?.Position.Y:F1}, {_controller?.Position.Z:F1}), " +
                                  $"ground {_terrain?.SampleHeight(_controller?.Position.X ?? 0, _controller?.Position.Y ?? 0):F2}");
                break;
            case "map":
            {
                // map <id> <x> <y> <z|ground>: cross-map travel through the creator's TravelTo.
                EnsureInstanceData();
                int mapId = int.Parse(a[1], CultureInfo.InvariantCulture);
                float x = F(2), y = F(3), z = a[4] == "ground" ? 200f : F(4);
                if (!TravelToMapId(mapId, new Vector3(x, y, z), 0f, "wbscript"))
                    Console.WriteLine($"[wbscript] ERROR: travel to map {mapId} refused");
                _wbScriptWaitFor = "world";
                _wbScriptWaitUntil = now + 3.0;
                break;
            }
            case "newmap":
            {
                // newmap <id> <dir> <type 0|1|2> <players> <area> <level> <srcDir> <srcCol> <srcRow> <w> <h> <dstCol> <dstRow> <doodads 0|1> <wmos 0|1> <name...>
                int I(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
                string name = string.Join(' ', a[16..]);
                var items = WbNewMapItems(I(1), a[2], name, I(3), I(4), (uint)I(5), I(6), a[7], I(8), I(9), I(10), I(11), I(12), I(13),
                    a[14] == "1", a[15] == "1");
                WbPost($"map {name} ({a[1]})", items);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "subzone":
            {
                // subzone <area> <x> <y> <radius> <name...>  (docs must be loaded: 'docs' first)
                string name = string.Join(' ', a[5..]);
                WbAddSubzone(uint.Parse(a[1], CultureInfo.InvariantCulture), name, new Vector3(F(2), F(3), 0), F(4));
                _wbScriptWaitFor = "idle";
                break;
            }
            case "portal":
            {
                // portal <id> <map> <x> <y> <z> <radius> <toMap> <tx> <ty> <tz> <facing> <minLevel> <name...>
                int I(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
                string name = string.Join(' ', a[13..]);
                WbPost($"portal {name} ({a[1]})", WbPortalItems((uint)I(1), name, I(2), new Vector3(F(3), F(4), F(5)), F(6),
                    I(7), new Vector3(F(8), F(9), F(10)), F(11), I(12)));
                _wbScriptWaitFor = "idle";
                break;
            }
            case "docs":
                _wbDocs = null;
                WbRequestDocs();
                _wbScriptWaitFor = "docs";
                break;
            case "travel":
            {
                // travel <map> <x> <y> [z]: after a mount, reach a (possibly pack) map.
                ResetInstanceData();
                EnsureInstanceData();
                float z = a.Length > 4 ? F(4) : 300f;
                if (!TravelToMapId(int.Parse(a[1], CultureInfo.InvariantCulture), new Vector3(F(2), F(3), z), 0f, "wbscript"))
                    Console.WriteLine($"[wbscript] ERROR: travel to map {a[1]} refused");
                _wbScriptWaitFor = "world";
                _wbScriptWaitUntil = now + 3.0;
                break;
            }
            case "content":
            {
                // content <file.json> [label...]: a JSON array of { kind, key?, body } posted as ONE op.
                string file = a[1];
                if (!Path.IsPathRooted(file)) file = Path.Combine(Path.GetDirectoryName(WbScriptPath!)!, file);
                string json = File.ReadAllText(file);
                string label = a.Length > 2 ? string.Join(' ', a[2..]) : Path.GetFileNameWithoutExtension(file);
                if (_wbPackId == 0) throw new InvalidOperationException("no pack selected");
                WbOp($"content {label}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, json),
                    r => Console.WriteLine($"[wbscript] content '{label}': op #{r.Result?.OpId}, audit #{r.Result?.AuditId}"));
                _wbScriptWaitFor = "idle";
                break;
            }
            case "find":
            {
                _wbCatalogue ??= WbBuildCatalogue();
                var words = a[1..].Select(w => w.ToLowerInvariant()).ToArray();
                var hits = _wbCatalogue.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(40).ToList();
                Console.WriteLine($"[wbscript] find {string.Join(' ', words)}: {hits.Count} hit(s)");
                foreach (var h in hits) Console.WriteLine($"[wbscript]   {h}");
                break;
            }
            case "xray":
                SetXrayActive(a[1] == "on");
                _wbScriptWaitUntil = now + 4.0;
                break;
            case "xraynav":
                _xrayShowNav = a[1] == "on";
                if (_xrayShowNav && !_xrayActive) SetXrayActive(true);
                if (_xrayShowNav) BeginXrayNavBuild();
                _wbScriptWaitUntil = now + 6.0;
                break;
            case "probe":
            {
                // Straight-down ray against the client's static collision (WMO/M2 BVH) and terrain.
                float x = F(1), y = F(2);
                var hit = _collision?.Raycast(new Vector3(x, y, 400f), -Vector3.UnitZ, 800f);
                Console.WriteLine($"[wbscript] probe ({x}, {y}): collision {(hit is { } h ? $"z={h.Point.Z:F2}" : "none")}, " +
                                  $"terrain {_terrain?.SampleHeight(x, y):F2}");
                break;
            }
            case "spawn":
            {
                // spawn <entry> <x> <y> <facing|face> [wander] [zOffset]: one pack creature row, ground-snapped.
                uint entry = uint.Parse(a[1], CultureInfo.InvariantCulture);
                var open = WbOpenGround(F(2), F(3)) ?? throw new InvalidOperationException($"no open ground within 24 yd of ({F(2)}, {F(3)}) - pick another spot or use spawnz");
                float x = open.X, y = open.Y;
                float z = open.Z + (a.Length > 6 ? F(6) : 0f);
                float facing = a[4] == "face" && _controller is not null
                    ? MathF.Atan2(_controller.Position.Y - y, _controller.Position.X - x) : F(4);
                float wander = a.Length > 5 ? F(5) : 0f;
                var row = WbSpawnRow(entry, new Vector3(x, y, z), facing);
                if (wander > 0) { row["body"]!["wander_distance"] = wander; row["body"]!["movement_type"] = 1; }
                WbPost($"spawn {entry}", new System.Text.Json.Nodes.JsonArray { row });
                _wbScriptWaitFor = "idle";
                break;
            }
            case "spawnz":
            {
                // spawnz <entry> <x> <y> <z> <facing>: on the floor just under z (inside buildings, on upper floors).
                uint entry = uint.Parse(a[1], CultureInfo.InvariantCulture);
                float x = F(2), y = F(3), zHint = F(4);
                float z = _collision?.Raycast(new Vector3(x, y, zHint + 1.5f), -Vector3.UnitZ, 10f) is { } hit ? hit.Point.Z : zHint;
                var row = WbSpawnRow(entry, new Vector3(x, y, z), F(5));
                WbPost($"spawn {entry}", new System.Text.Json.Nodes.JsonArray { row });
                _wbScriptWaitFor = "idle";
                break;
            }
            case "verify":
                WbRequestVerify(a.Length > 1 && a[1] == "rerun");
                _wbScriptWaitFor = "verify";
                break;
            case "verify-spawns":
                // verify-spawns [fix] [changed]: fix posts corrections as one op; changed skips what passed before.
                WbQueueSpawnVerify(a.Contains("fix"), a.Contains("changed"));
                break;
            case "questui":
            {
                // questui <draft.json>: the Quest Creator's own save path (QuestAuthoringLaw) from a file.
                string file = a[1];
                if (!Path.IsPathRooted(file)) file = Path.Combine(Path.GetDirectoryName(WbScriptPath!)!, file);
                WbScriptQuestUi(file);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "preflight":
                WbRequestPreflight();
                _wbScriptWaitFor = "verify";
                break;
            case "vcheck":
                Console.WriteLine($"[verify-client] cell ({F(1):F0}, {F(2):F0}): {WbVerifySpawnsNear(new Vector3(F(1), F(2), 0), F(3))} checked");
                break;
            case "terrain":
            {
                // terrain <x> <y>: where this client's ground under a point comes from - the archive that supplies
                // the tile, the file's own chunk heights, what the AdtCache holds (a World Builder preview rewrites
                // it) and the height grid the controller stands on. Two tiers disagreeing about the ground = run this.
                float x = F(1), y = F(2);
                var (col, row) = WorldBuilderLaw.TileOf(x, y);
                string path = $"World\\Maps\\{_config.Start.MapName}\\{_config.Start.MapName}_{col}_{row}.adt";
                var supplied = _mpq?.ReadFileWithSupplier(path);
                float cs = WorldBuilderLaw.Tile / 16f;
                float fx = ((32 - row) * WorldBuilderLaw.Tile - x) / cs, fy = ((32 - col) * WorldBuilderLaw.Tile - y) / cs;
                int iy = (int)fx, ix = (int)fy, gr = (int)((fx - iy) * 8), gc = (int)((fy - ix) * 8);
                string Corners(MSUIClient.Formats.AdtTerrainReader.AdtResult? adt)
                {
                    var c = adt?.Chunks?.FirstOrDefault(k => k is not null && k.IndexX == ix && k.IndexY == iy);
                    if (c?.Heights is null) return "none";
                    return Inv($"base {c.BaseZ:F2} corners {c.WorldHeightAt(gc, gr):F2} {c.WorldHeightAt(gc + 1, gr):F2} {c.WorldHeightAt(gc, gr + 1):F2} {c.WorldHeightAt(gc + 1, gr + 1):F2}");
                }
                var file = supplied is { } sf ? MSUIClient.Formats.AdtTerrainReader.Parse(sf.Data, row, col) : null;
                Console.WriteLine(Inv($"[terrain] ({x:F1}, {y:F1}) tile {col},{row} chunk {ix},{iy} cell {gc},{gr} supplier {supplied?.Supplier ?? "none"} ({supplied?.Data.Length ?? 0} bytes)"));
                Console.WriteLine($"[terrain] file  {Corners(file)}");
                Console.WriteLine($"[terrain] cache {Corners(_adts?.Get(col, row))} preview={_wbPreviewTiles.Contains((col, row))} applied={_wbAppliedTo.ContainsKey((col, row))}");
                Console.WriteLine(Inv($"[terrain] grid  {(_terrain?.SampleHeight(x, y) is { } gz ? gz.ToString("F2") : "none")}"));
                break;
            }
            case "rayprobe":
            {
                // rayprobe <x> <y> <z>: straight-down rays on a 5x5 grid (0.5 yd apart) from z+4 - what a
                // falling body could land on around one point (a capsule catches edges a centre ray misses).
                float x = F(1), y = F(2), z = F(3);
                var c = _collision;
                Console.WriteLine($"[rayprobe] ({x:F1}, {y:F1}, {z:F1}) collision {(c is null ? "none" : $"{c.TriangleCount} tris, bounds {c.BoundsMin} .. {c.BoundsMax}")}");
                // The terrain under the centre too (collision rays do not see it): height, and whether a hole cuts it.
                float? th = _terrain?.SampleHeight(x, y, out _);
                Console.WriteLine(Inv($"[rayprobe] terrain {(th is { } tz ? tz.ToString("F2") : "none")} hole={(th is null ? false : _terrain!.IsHoleAt(x, y))} through-holes {(_terrain?.SampleHeightThroughHoles(x, y) is { } thz ? thz.ToString("F2") : "none")}"));
                for (int j = -2; j <= 2; j++)
                    Console.WriteLine("[rayprobe] " + string.Join(" ", Enumerable.Range(-2, 5).Select(i =>
                        c?.Raycast(new Vector3(x + i * 0.5f, y + j * 0.5f, z + 4f), -Vector3.UnitZ, 64f) is { } h ? $"{h.Point.Z,7:F1}" : "   void")));
                break;
            }
            case "faces":
                // faces <x> <y> <z> <radius>: every authored WMO face near a point with its MOPY flags
                // and whether it reached walking collision (WmoRenderer.DumpFacesNear).
                _wmo?.DumpFacesNear(new Vector3(F(1), F(2), F(3)), F(4));
                _doodads?.DumpInstancesNear(new Vector3(F(1), F(2), F(3)), F(4) * 3f);   // M2 props with collision too
                break;
            case "relocate":
                // relocate <fromMap> <toMap> <dCol> <dRow>: move this pack's region by whole tiles (one op).
                WbRelocate(N(1), N(2), N(3), N(4));
                _wbScriptWaitFor = "idle";
                break;
            case "stamp":
            {
                // stamp <srcDir> <srcCol> <srcRow> <wide> <tall> <dstCol> <dstRow> [stitchYd] [areaId] [keepBuildings]:
                // stamp stock tiles onto THIS continent (seamless land), trees kept, buildings dropped unless asked.
                // A tile stamped onto ITSELF (an in-place edit: drop a prop, heal a hole) keeps everything.
                bool identity = string.Equals(a[1], _config.Start.MapName, StringComparison.OrdinalIgnoreCase) && N(2) == N(6) && N(3) == N(7);
                bool keepWmos = identity || (a.Length > 10 && bool.Parse(a[10]));
                var items = WbStampItems(_config.Start.Map, a[1], N(2), N(3), N(4), N(5), N(6), N(7), true, keepWmos,
                    a.Length > 9 ? (uint)N(9) : 0u, a.Length > 8 ? F(8) : 0f);
                WbPost(Inv($"stamp {a[1]} {N(2)},{N(3)} onto map {_config.Start.Map} at {N(6)},{N(7)}"), items);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "tile":
            {
                // tile <col>[-<col2>] <row>[-<row2>] <key> <value>: set a field of the pack tile docs on this map
                // (stitch, dropWmos, keepDoodads, ...) - one op for the whole range.
                static (int, int) Range(string t) => t.Split('-') is [var x, var y] && x.Length > 0
                    ? (int.Parse(x, CultureInfo.InvariantCulture), int.Parse(y, CultureInfo.InvariantCulture))
                    : (int.Parse(t, CultureInfo.InvariantCulture), int.Parse(t, CultureInfo.InvariantCulture));
                var (c0, c1) = Range(a[1]);
                var (r0, r1) = Range(a[2]);
                if (WbTilesSet(_config.Start.Map, c0, c1, r0, r1, a[3], string.Join(' ', a[4..]))) _wbScriptWaitFor = "idle";
                break;
            }
            case "carve":
            {
                // carve <name> <width> <falloff> x1 y1 z1|ground x2 y2 z2|ground ...: a graded path doc (pass,
                // land bridge, road) the build applies after the seams; "ground" = today's height there.
                var pts = new List<Vector3>();
                for (int k = 4; k + 2 < a.Length; k += 3)
                {
                    float px = F(k), py = F(k + 1);
                    float pz = a[k + 2] == "ground" ? (_terrain?.SampleHeight(px, py) ?? 0f) : F(k + 2);
                    pts.Add(new Vector3(px, py, pz));
                }
                WbPostPath(a[1], pts, F(2), F(3));
                _wbScriptWaitFor = "idle";
                break;
            }
            case "walkprobe":
            {
                // walkprobe <fx> <fy> <fz> <tx> <ty>: the player's own controller, off-screen, stands at
                // from, runs to 0.6 yd past to, settles 2 s - traced every 3 frames (ground source, hole).
                var (end, source) = WbProbeWalk(new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(3)), trace: true);
                Console.WriteLine(Inv($"[walkprobe] end ({end.X:F2}, {end.Y:F2}, {end.Z:F2}) ground={source}"));
                break;
            }
            case "healhole":
                // healhole <x> <y>: close the terrain hole square under the point (tile doc healHoles).
                // healhole all: every square the last tier-2 hole pass found a walk-off into (one op).
                if (a[1] == "all" ? WbHealAllWalkOffs() > 0 : WbHealHole(F(1), F(2))) _wbScriptWaitFor = "idle";
                break;
            case "column":
                // column <x> <y>: every WMO face over that XY, top to bottom (WmoRenderer.DumpFacesUnder).
                _wmo?.DumpFacesUnder(F(1), F(2));
                break;
            case "vsummary":
                Console.WriteLine($"[verify-client] SUMMARY map {_config.Start.Map}: {_wbClientChecked} checked, " +
                                  $"{_wbClientFindings.Count(f => f.Severity == "error")} error(s), {_wbClientFindings.Count(f => f.Severity == "warn")} warning(s)");
                WbSaveVerified();
                WbSaveHoleResults();
                _wbVerifyScope = null;
                if (_wbFixItems is { Count: > 0 } fixes)
                {
                    WbPost($"verifier fixes: {fixes.Count} spawn(s)/portal(s) on map {_config.Start.Map}", fixes);
                    _wbScriptWaitFor = "idle";
                }
                _wbFixItems = null;
                break;
            case "survey":
                WbQueueSurvey(F(1), F(2), F(3), a[4]);
                break;
            case "spawnring":
            {
                // spawnring <entry> <cx> <cy> <radius> <count> <wander>: a mob camp as ONE op.
                uint entry = uint.Parse(a[1], CultureInfo.InvariantCulture);
                float cx = F(2), cy = F(3), radius = F(4), wander = F(6);
                int count = int.Parse(a[5], CultureInfo.InvariantCulture);
                var rng = new Random((int)(entry * 31 + cx * 7 + cy));
                var items = new System.Text.Json.Nodes.JsonArray();
                for (int i = 0; i < count; i++)
                {
                    float ang = i * MathF.Tau / count + (float)rng.NextDouble() * 0.6f;
                    float r = radius * (0.35f + 0.65f * (float)rng.NextDouble());
                    float x = cx + MathF.Cos(ang) * r, y = cy + MathF.Sin(ang) * r;
                    if (WbOpenGround(x, y, 12f) is not { } spot) { Console.WriteLine($"[wbscript] ERROR camp {entry}: no open ground near ({x:F0}, {y:F0}), skipped"); continue; }
                    var row = WbSpawnRow(entry, spot, (float)(rng.NextDouble() * MathF.Tau));
                    if (wander > 0) { row["body"]!["wander_distance"] = wander; row["body"]!["movement_type"] = 1; }
                    items.Add(row);
                }
                WbPost($"camp {entry} x{count}", items);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "mobpack":
            {
                // mobpack <entry[,entry...]> <count> <cx> <cy> <spread>: a LINKED pack as one op (the panel's
                // "Linked pack" mode): the first entry leads from the centre, the list cycles; aggro/evade/respawn
                // together (creature_groups flags 14).
                var entries = a[1].Split(',').Select(s => uint.Parse(s, CultureInfo.InvariantCulture)).ToList();
                if (WbPackItems(entries, new Vector2(F(3), F(4)), F(5), int.Parse(a[2], CultureInfo.InvariantCulture)) is not { } pack)
                    throw new InvalidOperationException($"pack at ({F(3)}, {F(4)}) found no open ground");
                WbPost($"pack {a[1]} x{a[2]}", pack);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "patrol":
            {
                // patrol <leaderEntry> <followerEntry> <followers> <x1> <y1> <x2> <y2> [...]: a patrol as one op (the
                // panel's "Patrol" mode): route points snapped to open ground, looping; followers in formation.
                var route = new List<Vector2>();
                for (int i = 4; i + 1 < a.Length; i += 2) route.Add(new Vector2(F(i), F(i + 1)));
                if (route.Count < 2) throw new InvalidOperationException("a patrol needs at least two route points");
                if (WbPatrolItems(uint.Parse(a[1], CultureInfo.InvariantCulture), uint.Parse(a[2], CultureInfo.InvariantCulture),
                        int.Parse(a[3], CultureInfo.InvariantCulture), route) is not { } patrol)
                    throw new InvalidOperationException("patrol route has a point with no open ground");
                WbPost($"patrol {a[1]} ({route.Count} points)", patrol);
                _wbScriptWaitFor = "idle";
                break;
            }
            case "movespawn":
            {
                // movespawn <guid> <x> <y> [facing]: move a pack spawn to open ground there (one op, undoable) - e.g.
                // a boss standing so close to the next that ranged members pull both.
                string guid = a[1];
                var doc = _wbDocs?.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "dbrow:creature" && WbScalar(d["body"]!["guid"]!) == guid)
                          ?? throw new InvalidOperationException($"no pack spawn {guid} (run 'docs' first)");
                var open = WbOpenGround(F(2), F(3)) ?? throw new InvalidOperationException($"no open ground within 24 yd of ({F(2)}, {F(3)})");
                var body = doc["body"]!.DeepClone().AsObject();
                body["position_x"] = Math.Round(open.X, 2); body["position_y"] = Math.Round(open.Y, 2); body["position_z"] = Math.Round(open.Z, 2);
                if (a.Length > 4) body["orientation"] = Math.Round(F(4), 3);
                WbPost($"move spawn {guid}", new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["kind"] = "dbrow:creature", ["body"] = body } });
                _wbScriptWaitFor = "idle";
                break;
            }
            case "heightmap":
            {
                // heightmap <xNorth> <yWest> <xSouth> <yEast> <step>: ASCII terrain survey straight
                // from the ADTs (no residency needed). Rows run north→south, columns west→east.
                //   ~ water/sea (<0)  . low (<40)  - mid (<90)  + high (<140)  ^ ridge (<200)  # peak
                float x0 = F(1), y0 = F(2), x1 = F(3), y1 = F(4), step = F(5);
                if (_adts is null) break;
                var sb = new System.Text.StringBuilder();
                Console.WriteLine($"[wbscript] heightmap {_config.Start.MapName} x {x0}..{x1} y {y0}..{y1} step {step}");
                for (float x = x0; x >= x1; x -= step)
                {
                    sb.Clear();
                    for (float y = y0; y >= y1; y -= step)
                    {
                        var (col, row) = WorldBuilderLaw.TileOf(x, y);
                        var adt = _adts.Get(col, row);
                        if (adt?.Chunks is null) { sb.Append(' '); continue; }
                        int gr = Math.Clamp((int)(((32 - row) * WorldBuilderLaw.Tile - x) / WorldBuilderLaw.Unit), 0, 128);
                        int gc = Math.Clamp((int)(((32 - col) * WorldBuilderLaw.Tile - y) / WorldBuilderLaw.Unit), 0, 128);
                        var ch = adt.Chunks.FirstOrDefault(c => c?.IndexY == Math.Min(gr / 8, 15) && c.IndexX == Math.Min(gc / 8, 15));
                        if (ch?.Heights is null) { sb.Append(' '); continue; }
                        float h = ch.WorldHeightAt(gc - ch.IndexX * 8, gr - ch.IndexY * 8);
                        sb.Append(h < 0 ? '~' : h < 40 ? '.' : h < 90 ? '-' : h < 140 ? '+' : h < 200 ? '^' : '#');
                    }
                    Console.WriteLine($"[hm] {x,7:F0} {sb}");
                }
                break;
            }
            case "height":
                Console.WriteLine($"[wbscript] height at ({F(1)}, {F(2)}) = {_terrain?.SampleHeight(F(1), F(2)):F2}");
                break;
            case "quit":
                Console.WriteLine("[wbscript] quit");
                _quitRequested = true;
                break;
            default:
                Console.WriteLine($"[wbscript] unknown command '{a[0]}'");
                break;
        }
    }
}
