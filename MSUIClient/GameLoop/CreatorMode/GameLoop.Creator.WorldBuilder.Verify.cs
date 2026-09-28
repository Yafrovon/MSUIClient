using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Player;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Pack Verifier — client half (shared_docs/WORLD_BUILDER.md §7).
//
//   Tier 1 (web app, every publish): content integrity, stamped-tile fidelity, placed-building
//          clipping/grounding/roads, spawn + portal ground, server-log complaints. This section
//          shows that report and jumps to any finding.
//   Tier 2 (here, needs resident geometry): every pack spawn and portal arrival against the real
//          client collision — floor under the feet, head room, not inside a wall — and every covered
//          terrain hole of the report (G12): something must catch a player dropped into it.
//   Tier 3 (live, as a real character): talk/buy/train/quest/kill through the live protocol.
//
// Script: verify [rerun] | verify-spawns | survey <x> <y> <distance> <name>
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private Task<string>? _wbVerifyTask;
    private JsonObject? _wbVerify;
    private string _wbVerifyFilter = "error";
    private readonly List<(string Severity, string Subject, string Message, Vector3 At)> _wbClientFindings = new();
    private int _wbClientChecked;
    private readonly HashSet<string> _wbClientSeen = new();
    private HashSet<string>? _wbVerifyScope;   // targets of the running script pass (null = all)

    /// <summary>What passed the last client pass(es): a per-machine convenience, not evidence.</summary>
    private static string WbVerifiedPath => Path.Combine(AppContext.BaseDirectory, "worldpack-verified.json");

    private static string WbTargetKey(string subject, Vector3 at) => Inv($"{subject}@{at.X:F2},{at.Y:F2},{at.Z:F2}");

    /// <summary>Geometry that can move a spawn's floor: every placement (model + pose) AND every
    /// sculpted vertex (a pad or brush lowers/raises the ground under standing creatures). Keyed so a
    /// change of either re-queues the targets around it.</summary>
    private List<(string Key, Vector3 At)> WbPlacementKeys()
    {
        var keys = (_wbState?.Placements ?? new()).Where(p => !p.Deleted && p.MapId == _config.Start.Map)
            .Select(p => (Inv($"{p.Id}:{p.ModelPath}@{p.PosX:F2},{p.PosY:F2},{p.PosZ:F2},{p.RotY:F1}"), new Vector3(p.PosX, p.PosY, p.PosZ))).ToList();
        foreach (var t in _wbState?.Sculpt ?? new())
            foreach (var (i, dz) in t.Deltas)
            {
                if (MathF.Abs(dz) < 0.05f) continue;
                var v = WorldBuilderLaw.VertexWorld(t.Col, t.Row, i / 129, i % 129);
                keys.Add((Inv($"sculpt:{_config.Start.Map}:{t.Col}:{t.Row}:{i}={dz:F2}"), new Vector3(v.X, v.Y, 0)));
            }
        return keys;
    }

    private (HashSet<string> Spawns, HashSet<string> Placements) WbLoadVerified()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(WbVerifiedPath)) is JsonObject o)
                return ((o["spawns"] as JsonArray ?? new()).Select(n => n!.ToString()).ToHashSet(),
                        (o["placements"] as JsonArray ?? new()).Select(n => n!.ToString()).ToHashSet());
        }
        catch { }
        return (new(), new());
    }

    /// <summary>After a pass: every checked target with no error/warning is remembered as clean.</summary>
    private void WbSaveVerified()
    {
        var (spawns, placements) = WbLoadVerified();
        foreach (var (subject, at, _, _) in WbVerifyTargets())
        {
            if (!_wbClientSeen.Contains(subject)) continue;
            spawns.RemoveWhere(k => k.StartsWith(subject + "@", StringComparison.Ordinal));
            if (!_wbClientFindings.Any(f => f.Subject == subject && f.Severity != "info")) spawns.Add(WbTargetKey(subject, at));
        }
        if (!_wbClientFindings.Any(f => f.Severity != "info"))
            foreach (var p in WbPlacementKeys()) placements.Add(p.Key);
        try
        {
            File.WriteAllText(WbVerifiedPath, new JsonObject
            {
                ["spawns"] = new JsonArray(spawns.Select(x => (JsonNode)x).ToArray()),
                ["placements"] = new JsonArray(placements.Select(x => (JsonNode)x).ToArray()),
            }.ToJsonString());
        }
        catch (Exception ex) { Console.WriteLine($"[verify-client] could not save {WbVerifiedPath}: {ex.Message}"); }
    }
    private JsonArray? _wbFixItems;   // non-null during "verify-spawns fix": corrected rows, posted as one op
    private readonly Queue<string> _wbScriptInject = new();

    private void RegisterCreatorVerifySection() =>
        CreatorSection("World", "wb-verify", "Verify", true, DrawWbVerifySection);

    private void WbRequestVerify(bool rerun)
    {
        if (SuiWebAppUrl.Length == 0 || _wbVerifyTask is not null) return;
        _wbVerifyTask = _wbClient.VerifyAsync(SuiWebAppUrl, rerun);
    }

    /// <summary>Content checks on the unpublished docs - run before a publish, not after it.</summary>
    private void WbRequestPreflight()
    {
        if (SuiWebAppUrl.Length == 0 || _wbVerifyTask is not null) return;
        _wbVerifyTask = _wbClient.PreflightAsync(SuiWebAppUrl);
    }

    private void PumpWbVerify()
    {
        if (_wbVerifyTask is not { IsCompleted: true } t) return;
        _wbVerifyTask = null;
        if (t.IsFaulted) { _wbMessage = "verify: " + t.Exception?.GetBaseException().Message; return; }
        try
        {
            _wbVerify = JsonNode.Parse(t.Result)?["report"] as JsonObject;
            if (WbScriptPath is not null && _wbVerify is not null) WbPrintVerify(_wbVerify);
        }
        catch (Exception ex) { _wbMessage = "verify: " + ex.Message; }
    }

    private static IEnumerable<JsonObject> WbFindings(JsonObject report) =>
        (report["findings"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    private static void WbPrintVerify(JsonObject r)
    {
        Console.WriteLine($"[verify] build {r["buildId"]?.ToString() ?? "-"}: {r["errors"]} error(s), {r["warnings"]} warning(s)");
        foreach (var f in WbFindings(r).OrderBy(f => (string?)f["severity"] switch { "error" => 0, "warn" => 1, _ => 2 }))
            Console.WriteLine($"[verify] {(string?)f["severity"],-5} {(string?)f["check"],-5} {(string?)f["subject"]}: {(string?)f["message"]}" +
                              (f["x"] is JsonValue ? $"  @{f["map"]} ({(float)f["x"]!:F0}, {(float)f["y"]!:F0}, {(float)f["z"]!:F0})" : ""));
    }

    private void DrawWbVerifySection()
    {
        PumpWbVerify();
        ImGui.TextDisabled("Every publish is verified on the server; the client checks spawns against real collision.");
        if (CreatorButton(_wbVerifyTask is null ? "Load report" : "loading...")) WbRequestVerify(false);
        ImGui.SameLine();
        if (CreatorButton("Pre-flight")) WbRequestPreflight();
        ImGui.SameLine();
        if (CreatorButton("Re-run on server")) WbRequestVerify(true);
        ImGui.SameLine();
        if (CreatorButton("Check spawns here")) { _wbClientSeen.Clear(); WbVerifySpawnsNear(_controller?.Position ?? Vector3.Zero, 180f); }
        ImGui.SameLine();
        if (CreatorButton("Heal all walk-offs")) WbHealAllWalkOffs();
        if (_wbVerify is null) { if (_wbVerifyTask is null) WbRequestVerify(false); return; }

        ImGui.TextUnformatted($"Build {_wbVerify["buildId"]?.ToString() ?? "(on demand)"}: {_wbVerify["errors"]} error(s), {_wbVerify["warnings"]} warning(s)");
        foreach (var sev in new[] { "error", "warn", "info" })
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(sev + "##wbv", _wbVerifyFilter == sev)) _wbVerifyFilter = sev;
        }
        if (ImGui.BeginChild("##wb-verify-list", new Vector2(-1f, 220f * CreatorUiScale)))
        {
            int n = 0;
            foreach (var f in WbFindings(_wbVerify).Where(f => (string?)f["severity"] == _wbVerifyFilter))
            {
                ImGui.PushID(n++);
                if (f["x"] is JsonValue && (int?)f["map"] == _config.Start.Map && ImGui.SmallButton("go"))
                    WbGoLook(new Vector3((float)f["x"]!, (float)f["y"]!, (float)f["z"]!));
                if (f["x"] is JsonValue) ImGui.SameLine();
                ImGui.TextWrapped($"{(string?)f["check"]} {(string?)f["subject"]}: {(string?)f["message"]}");
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        if (_wbClientChecked > 0)
        {
            ImGui.TextUnformatted($"Client collision pass: {_wbClientChecked} checked, {_wbClientFindings.Count(f => f.Severity == "error")} error(s)");
            foreach (var (sev, subject, msg, at) in _wbClientFindings.Where(f => f.Severity != "info").Take(30))
            {
                ImGui.PushID(subject);
                if (ImGui.SmallButton("go")) WbGoLook(at);
                ImGui.SameLine();
                if (subject.StartsWith("terrain hole", StringComparison.Ordinal))
                {
                    if (ImGui.SmallButton("heal")) WbHealHole(at.X, at.Y);
                    ImGui.SameLine();
                }
                ImGui.TextWrapped($"{sev} {subject}: {msg}");
                ImGui.PopID();
            }
        }
    }

    private void WbGoLook(Vector3 at)
    {
        if (_controller is null) return;
        _controller.Teleport(at.X, at.Y, at.Z + 2f);
        _window.Camera.Target = _controller.Position;
    }

    // ═══════════════════════════════════════════════════════════════ tier 2: collision

    /// <summary>Pack spawns + portal arrivals on the current map from the loaded docs, and the covered
    /// terrain holes of the loaded verifier report (G12 info findings; none until a report is loaded).</summary>
    private List<(string Subject, Vector3 At, string Kind, JsonObject Body)> WbVerifyTargets()
    {
        var list = new List<(string, Vector3, string, JsonObject)>();
        if (_wbDocs is null) return list;
        int map = _config.Start.Map;
        foreach (var d in _wbDocs.OfType<JsonObject>())
        {
            string kind = (string?)d["kind"] ?? "";
            if (d["body"] is not JsonObject b) continue;
            float Fv(string c) => b[c] is JsonNode n && float.TryParse(WbScalar(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
            if (kind is "dbrow:creature" or "dbrow:gameobject" && (int)Fv("map") == map)
                list.Add(($"{kind[6..]} {WbScalar(b["guid"]!)} (entry {WbScalar(b["id"]!)})", new Vector3(Fv("position_x"), Fv("position_y"), Fv("position_z")), kind, b));
            else if (kind == "dbrow:areatrigger_teleport" && (int)Fv("target_map") == map)
                list.Add(($"portal {WbScalar(b["id"]!)} arrival", new Vector3(Fv("target_position_x"), Fv("target_position_y"), Fv("target_position_z")), kind, b));
        }
        if (_wbVerify is not null)
            foreach (var f in WbFindings(_wbVerify))
                if ((string?)f["check"] == "G12" && (string?)f["subject"] == "hole" && (int?)f["map"] == map && f["x"] is JsonValue)
                {
                    var at = new Vector3((float)f["x"]!, (float)f["y"]!, (float)f["z"]!);
                    list.Add((Inv($"terrain hole ({at.X:F0}, {at.Y:F0})"), at, "hole", new JsonObject()));
                }
        return list;
    }

    /// <summary>
    /// Real-collision check of every target within <paramref name="radius"/> of <paramref name="centre"/>
    /// (their tiles must be resident): the highest floor (terrain or WMO/M2) at most 1.5 yd above
    /// the feet must be within 1.2 yd below them; no ceiling within 2 yd; not boxed in by walls.
    /// </summary>
    private int WbVerifySpawnsNear(Vector3 centre, float radius)
    {
        int n = 0;
        bool inHole = false;
        foreach (var (subject, at, kind, body) in WbVerifyTargets())
        {
            if (_wbVerifyScope is not null && !_wbVerifyScope.Contains(subject)) continue;
            if (Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(centre.X, centre.Y)) > radius) continue;
            float? sampled = _terrain?.SampleHeight(at.X, at.Y, out inHole);
            if (sampled is null && !inHole) continue;   // tile not resident (a hole is resident: only the building can hold you there)
            if (!_wbClientSeen.Add(subject)) continue;   // cells overlap: one check per target per pass
            _wbClientFindings.RemoveAll(f => f.Subject == subject);
            n++;
            _wbClientChecked++;
            if (kind == "hole") { WbCheckHoleChunk(subject, at); continue; }
            float? floor = sampled is { } ground && ground <= at.Z + 1.5f ? ground : null;
            if (_collision?.Raycast(at + new Vector3(0, 0, 1.5f), -Vector3.UnitZ, 40f) is { } down && (floor is null || down.Point.Z > floor))
                floor = down.Point.Z;
            if (floor is null)
            {
                WbClientFinding("error", subject, "no floor under the feet", at);
                // BURIED: the terrain is above the feet (the ground was raised after the spawn was placed - a
                // stitch, a path, a sculpt). Fix mode lifts it onto the surface: the highest floor under a point
                // just above the terrain (a building there wins over the grass).
                if (_wbFixItems is not null && sampled is { } above && above > at.Z)
                {
                    float top = above;
                    if (_collision?.Raycast(new Vector3(at.X, at.Y, above + 1.5f), -Vector3.UnitZ, 40f) is { } hit && hit.Point.Z > top) top = hit.Point.Z;
                    WbFixTo(subject, kind, body, at, new Vector3(at.X, at.Y, top));
                }
                continue;
            }
            float terrain = sampled ?? floor.Value;   // over a hole the building floor IS the ground
            float gap = at.Z - floor.Value;
            if (gap < -0.5f) WbClientFinding("error", subject, $"{-gap:F1} yd below the floor", at);
            else if (gap > 1.2f) WbClientFinding("warn", subject, $"{gap:F1} yd above the floor (falls on spawn)", at);
            var feet = new Vector3(at.X, at.Y, floor.Value);
            // Roof: the floor is building geometry well above the terrain and nothing is overhead
            // (an upper storey has a ceiling). A roof is a valid floor physically - hence its own rule.
            bool roof = floor.Value > terrain + 2.5f &&
                        _collision?.Raycast(feet + new Vector3(0, 0, 0.3f), Vector3.UnitZ, 40f) is null &&
                        _collision?.Raycast(feet - new Vector3(0, 0, 0.6f), -Vector3.UnitZ, floor.Value - terrain - 0.8f) is not null;   // a room below; a dock/bridge has none
            if (roof && kind != "dbrow:areatrigger_teleport")
                WbClientFinding("error", subject, $"standing on a roof ({floor.Value - terrain:F1} yd above the terrain, open sky above)", at);
            if (_collision?.Raycast(feet + new Vector3(0, 0, 0.3f), Vector3.UnitZ, 1.9f) is { } up)
                WbClientFinding("error", subject, $"ceiling {up.Distance + 0.3f:F1} yd over the feet (inside geometry)", at);
            int walls = WbWallsAround(feet);
            // Only terrain can be too steep (a WMO floor is flat by construction).
            float slope = MathF.Abs(floor.Value - terrain) < 0.3f ? WbSlopeDegrees(at.X, at.Y) : 0f;
            if (WbWaterLevel(at.X, at.Y) is { } water && water - floor.Value > 1.5f && kind != "dbrow:areatrigger_teleport")
                WbClientFinding("error", subject, $"stands {water - floor.Value:F1} yd under water", at);
            bool underwater = WbWaterLevel(at.X, at.Y) is { } w2 && w2 - floor.Value > 1.5f;
            if (slope > 50f && kind != "dbrow:areatrigger_teleport")
                WbClientFinding("error", subject, $"on a {slope:F0} degree slope (walkable ~50): players slide off and it cannot chase", at);
            if (walls >= 5) WbClientFinding("error", subject, $"inside a wall/object ({walls}/8 directions blocked within 0.7 yd)", at);
            else if (walls >= 3) WbClientFinding("warn", subject, $"pressed against geometry ({walls}/8 directions blocked)", at);
            // Fix mode: floor problems snap z to the floor; boxed-in spots move to the nearest open ground.
            if (_wbFixItems is not null && _wbClientFindings.Any(f => f.Subject == subject && f.Severity != "info"))
            {
                bool boxed = walls >= 3 || roof || slope > 50f || underwater || _wbClientFindings.Any(f => f.Subject == subject && f.Message.StartsWith("ceiling"));
                // Widen the search step by step: the smallest move that clears the problem wins.
                Vector3? target = boxed ? WbOpenGround(at.X, at.Y, 10f) ?? WbOpenGround(at.X, at.Y, 20f) ?? WbOpenGround(at.X, at.Y, 30f) : feet;
                if (target is not { } to) { WbClientFinding("error", subject, "no open ground within 30 yd - move it by hand", at); continue; }
                WbFixTo(subject, kind, body, at, to);
            }
        }
        return n;
    }

    /// <summary>Fix mode: the row moved to <paramref name="to"/>, collected into the ONE op the pass posts.</summary>
    private void WbFixTo(string subject, string kind, JsonObject body, Vector3 at, Vector3 to)
    {
        var fixedBody = (JsonObject)body.DeepClone();
        string px = kind == "dbrow:areatrigger_teleport" ? "target_position_" : "position_";
        fixedBody[px + "x"] = Math.Round(to.X, 2);
        fixedBody[px + "y"] = Math.Round(to.Y, 2);
        fixedBody[px + "z"] = Math.Round(to.Z + (kind == "dbrow:areatrigger_teleport" ? 0.3f : 0f), 2);
        _wbFixItems!.Add(new JsonObject { ["kind"] = kind, ["body"] = fixedBody });
        Console.WriteLine($"[verify-client] fix {subject}: ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) -> ({to.X:F1}, {to.Y:F1}, {to.Z:F1}), moved {Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(to.X, to.Y)):F1} yd");
    }

    /// <summary>Tier 2 → tier 3 hand-off: per checked hole chunk, a point where the building's floor
    /// catches a body (the live protocol drops the character there) and the reachable void, if any.</summary>
    private readonly Dictionary<string, JsonObject> _wbHoleResults = new();
    private int? _wbHolePassMap;   // set by a full (not changed-only) pass: its map's old entries are replaced
    private static string WbHolesPath => Path.Combine(AppContext.BaseDirectory, "worldpack-holes.json");

    // ── the probe body: the player's own CharacterController, off-screen ──────────────────────────
    private CharacterController? _wbProbeBody;

    private CharacterController WbProbeBody()
    {
        var b = _wbProbeBody ??= new CharacterController(_terrain!, _config.Movement);
        b.RebindTerrain(_terrain!);
        b.Collision = _collision;
        b.LiquidSurfaceProbe = _controller?.LiquidSurfaceProbe;
        return b;
    }

    /// <summary>Drop a real body at <paramref name="p"/>: where does it come to rest after
    /// <paramref name="seconds"/>, and is it standing?</summary>
    private (Vector3 End, bool Grounded, string Source) WbProbeDrop(Vector3 p, float seconds = 2.5f)
    {
        var b = WbProbeBody();
        b.Teleport(p.X, p.Y, p.Z);
        b.Flying = false;
        var still = new MovementInput();
        for (float t = 0; t < seconds; t += 1f / 30f) b.Update(1f / 30f, still);
        return (b.Position, b.Grounded, b.GroundSource);
    }

    /// <summary>Stand a real body at <paramref name="from"/>, let it settle, run it toward
    /// <paramref name="to"/> (0.6 yd past it) and let it settle again: where does it end?</summary>
    private (Vector3 End, string Source) WbProbeWalk(Vector3 from, Vector3 to, bool trace = false)
    {
        var b = WbProbeBody();
        b.Teleport(from.X, from.Y, from.Z + 0.2f);
        b.Flying = false;
        var still = new MovementInput();
        for (int i = 0; i < 12; i++) b.Update(1f / 30f, still);
        var d = to - b.Position;
        float dist = new Vector2(d.X, d.Y).Length() + 0.6f;
        var run = new MovementInput { Forward = 1f, Yaw = MathF.Atan2(d.Y, d.X) };
        int frame = 0;
        void Trace() { if (trace && frame++ % 3 == 0) Console.WriteLine(Inv($"[walkprobe] f{frame - 1} ({b.Position.X:F2}, {b.Position.Y:F2}, {b.Position.Z:F2}) ground={b.GroundSource} z={b.GroundZ?.ToString("F2") ?? "-"} grounded={b.Grounded} hole={b.InTerrainHole}")); }
        if (trace) Console.WriteLine(Inv($"[walkprobe] settled at ({b.Position.X:F2}, {b.Position.Y:F2}, {b.Position.Z:F2}) ground={b.GroundSource}"));
        for (float t = 0; t < dist / MathF.Max(1f, _config.Movement.RunSpeed); t += 1f / 30f) { b.Update(1f / 30f, run); Trace(); }
        for (int i = 0; i < 60; i++) { b.Update(1f / 30f, still); Trace(); }
        return (b.Position, b.GroundSource);
    }

    /// <summary>
    /// Every hole cell of the terrain chunk around <paramref name="at"/> (G12 reports one per chunk),
    /// judged by the player's OWN physics - a probe CharacterController on the resident terrain and
    /// collision: each hole cell (0.83 yd) gets a real drop; a cell whose body falls out of the world
    /// is VOID. A void cell is a defect only when a body can WALK into it: from every standable
    /// neighbour (terrain, or a hole cell the building catches) the probe runs toward it, and falling
    /// 20+ yd proves the path (walls, seams, body width, step-ups all decided by the same code that
    /// moves the player). Void sealed inside a building (a crypt's wall niche) is info - only a
    /// teleport lands there. 2026-09-27: the fortress's Arathi-style entrance left the corner of one
    /// hole square open under its rock arch - walkable, and a dropped player fell to z -48.
    /// </summary>
    private void WbCheckHoleChunk(string subject, Vector3 at)
    {
        const float chunk = WorldBuilderLaw.Tile / 16f, step = chunk / 40f;
        float x0 = MathF.Ceiling(at.X / chunk) * chunk;   // the chunk's max-X (north) corner
        float y0 = MathF.Ceiling(at.Y / chunk) * chunk;   // ...and max-Y (west) corner
        const int n = 42;                                 // 40 cells + one ring outside
        var hole = new bool[n, n];
        var stand = new float?[n, n];                     // where a body stands on this cell, null = can't
        var fell = new bool[n, n];
        Vector3 P(int i, int j) => new(x0 - (i - 0.5f) * step, y0 - (j - 0.5f) * step, 0f);
        int cells = 0, voids = 0;
        Vector3? catchAt = null; float catchBest = float.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var p = P(i, j);
                bool inHole = false;
                float? h = _terrain?.SampleHeight(p.X, p.Y, out inHole);
                hole[i, j] = inHole;
                if (!inHole) { stand[i, j] = h; continue; }
                cells++;
                var (end, grounded, source) = WbProbeDrop(p with { Z = at.Z + 2.5f });
                // Held only by the client's hole void guard = nothing of the building under it.
                if (end.Z < at.Z - 40f || source is "hole-void-guard" or "shell-void-guard") { fell[i, j] = true; voids++; continue; }
                if (!grounded) continue;
                stand[i, j] = end.Z;
                float dist = Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(at.X, at.Y));
                if (dist < catchBest && MathF.Abs(end.Z - at.Z) < 20f) { catchBest = dist; catchAt = end; }
            }
        // Walk-offs: FELL = out of the world (the guard did not hold - an error on any client);
        // GUARDED = this client's hole void guard held the body on the invisible height field (a
        // visible gap; a stock 1.12 client or an older build falls) - heal or cover it.
        var reachable = new List<(Vector3 From, Vector3 Void, bool Guarded)>();
        for (int i = 1; i < n - 1 && reachable.Count < 12; i++)
            for (int j = 1; j < n - 1 && reachable.Count < 12; j++)
            {
                if (!fell[i, j]) continue;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (stand[i + di, j + dj] is not { } s || MathF.Abs(s - at.Z) > 30f) continue;
                    var from = P(i + di, j + dj) with { Z = s };
                    var (end, source) = WbProbeWalk(from, P(i, j) with { Z = s });
                    bool guarded = source is "hole-void-guard" or "shell-void-guard" && end.Z > s - 20f;
                    if (end.Z > s - 20f && !guarded) continue;   // held by the world: a wall, a lip, the edge
                    reachable.Add((from, P(i, j) with { Z = s }, guarded));
                    break;
                }
            }
        reachable = reachable.OrderBy(r => r.Guarded).ToList();   // real falls first
        _wbHoleResults[subject] = new JsonObject
        {
            ["map"] = _config.Start.Map, ["x"] = at.X, ["y"] = at.Y, ["z"] = at.Z,
            ["cells"] = cells, ["void"] = voids, ["reachableVoid"] = reachable.Count,
            ["catch"] = catchAt is { } c ? new JsonObject { ["x"] = c.X, ["y"] = c.Y, ["z"] = c.Z } : null,
            ["walkOff"] = reachable.Count > 0 ? new JsonObject
            {
                ["fromX"] = reachable[0].From.X, ["fromY"] = reachable[0].From.Y, ["fromZ"] = reachable[0].From.Z,
                ["toX"] = reachable[0].Void.X, ["toY"] = reachable[0].Void.Y, ["guarded"] = reachable[0].Guarded,
            } : null,
            // Every open spot a walking body reached: "healhole all" closes the hole squares that contain them.
            ["voids"] = new JsonArray(reachable.Select(r => (JsonNode)new JsonArray(Math.Round(r.Void.X, 2), Math.Round(r.Void.Y, 2))).ToArray()),
        };
        if (reachable.Count > 0)
        {
            var (f, v, guarded) = reachable[0];
            if (guarded)
                WbClientFinding("warn", subject, $"a player walking from ({f.X:F1}, {f.Y:F1}, {f.Z:F1}) toward ({v.X:F1}, {v.Y:F1}) steps onto an open hole corner " +
                    $"with nothing under it; only this client's void guard holds them (a visible gap - other clients fall) - heal it: healhole {v.X:F1} {v.Y:F1}", v);
            else
                WbClientFinding("error", subject, $"a player walking from ({f.X:F1}, {f.Y:F1}, {f.Z:F1}) toward ({v.X:F1}, {v.Y:F1}) falls out of the world " +
                    $"({voids} of {cells} hole sample(s) have nothing under them) - heal it: healhole {v.X:F1} {v.Y:F1}", v);
        }
        else if (voids > 0)
            WbClientFinding("info", subject, $"{cells} hole sample(s): {voids} drop out of the world but no walk reaches them (sealed in - only a teleport lands there)", at);
        else
            WbClientFinding("info", subject, $"{cells} hole sample(s), every drop caught by the building", at);
    }

    /// <summary>
    /// Close the terrain hole square under (x, y) on a stamped pack tile: one audited, undoable op that
    /// adds the point to the tile doc's healHoles (the build clears that quarter-chunk's hole bit;
    /// verifier G12 confirms it on the published tile). The human face is the "heal" button on a hole
    /// finding, the agent face the script command "healhole x y" - one code path.
    /// </summary>
    private bool WbHealHole(float x, float y)
    {
        int map = _config.Start.Map;
        var (col, row) = WorldBuilderLaw.TileOf(x, y);
        var doc = _wbDocs?.OfType<JsonObject>().FirstOrDefault(d => (string?)d["kind"] == "tile" && d["body"] is JsonObject b &&
            (int?)b["map"] == map && (int?)b["col"] == col && (int?)b["row"] == row);
        if (doc?["body"] is not JsonObject body)
        {
            _wbMessage = Inv($"heal hole: tile {map}:{col},{row} is not a stamped pack tile (a stock-map hole needs a patched tile first)");
            Console.WriteLine("[wb] " + _wbMessage);
            return false;
        }
        var edited = (JsonObject)body.DeepClone();
        var heals = edited["healHoles"] as JsonArray ?? new JsonArray();
        heals.Add(new JsonObject { ["x"] = Math.Round(x, 2), ["y"] = Math.Round(y, 2) });
        edited["healHoles"] = heals;
        WbPost(Inv($"heal terrain hole at ({x:F1}, {y:F1})"), new JsonArray { new JsonObject { ["kind"] = "tile", ["body"] = edited } });
        return true;
    }

    /// <summary>
    /// Close every hole square a walking body reached an open corner of in the last tier-2 hole pass
    /// (worldpack-holes.json "voids", this map) - ONE op, grouped per tile. The human face is the Verify
    /// section's "Heal all walk-offs"; the agent face is "healhole all". Verify again afterwards: tier 2 must
    /// report the squares caught, and a survey shows the drawn ground does not poke into the building.
    /// </summary>
    private int WbHealAllWalkOffs()
    {
        int map = _config.Start.Map;
        JsonObject? all = null;
        try { all = File.Exists(WbHolesPath) ? JsonNode.Parse(File.ReadAllText(WbHolesPath)) as JsonObject : null; } catch { }
        if (all is null) { _wbMessage = "no hole results yet - run verify-spawns first"; return 0; }
        const float quarter = WorldBuilderLaw.Tile / 64f;   // a hole square = a quarter chunk
        var squares = new Dictionary<(int col, int row), Dictionary<(int, int), (float x, float y)>>();
        foreach (var (_, node) in all)
        {
            if (node is not JsonObject h || (int?)h["map"] != map || h["voids"] is not JsonArray voids) continue;
            foreach (var v in voids.OfType<JsonArray>())
            {
                float x = (float)(double)v[0]!, y = (float)(double)v[1]!;
                var tile = WorldBuilderLaw.TileOf(x, y);
                // One heal per square: its index in the world grid (x and y in quarter-chunk units).
                var sq = ((int)MathF.Floor(x / quarter), (int)MathF.Floor(y / quarter));
                if (!squares.TryGetValue(tile, out var set)) squares[tile] = set = new();
                set.TryAdd(sq, (x, y));
            }
        }
        var items = new JsonArray();
        int n = 0;
        foreach (var ((col, row), set) in squares)
        {
            if (WbTileDoc(map, col, row) is not { } body) { Console.WriteLine(Inv($"[wb] heal: tile {col},{row} is not a pack tile - skipped")); continue; }
            var edited = (JsonObject)body.DeepClone();
            var heals = edited["healHoles"] as JsonArray ?? new JsonArray();
            foreach (var (x, y) in set.Values)
            {
                // (double): a value this loop just added is a double-backed JsonValue - a (float) cast throws on it.
                bool known = heals.OfType<JsonObject>().Any(e => Math.Floor((double)e["x"]! / quarter) == Math.Floor(x / quarter) &&
                                                                 Math.Floor((double)e["y"]! / quarter) == Math.Floor(y / quarter));
                if (known) continue;
                heals.Add(new JsonObject { ["x"] = Math.Round(x, 2), ["y"] = Math.Round(y, 2) });
                n++;
            }
            edited["healHoles"] = heals;
            items.Add(new JsonObject { ["kind"] = "tile", ["body"] = edited });
        }
        if (n == 0) { _wbMessage = "no new hole squares to heal"; Console.WriteLine("[wb] " + _wbMessage); return 0; }
        WbPost(Inv($"heal {n} terrain hole square(s) walking players could step off into"), items);
        return n;
    }

    private void WbSaveHoleResults()
    {
        if (_wbHoleResults.Count == 0 && _wbHolePassMap is null) return;
        try
        {
            var all = File.Exists(WbHolesPath) ? JsonNode.Parse(File.ReadAllText(WbHolesPath)) as JsonObject ?? new() : new JsonObject();
            // A FULL pass is the whole truth for its map: a healed chunk (no hole left) is not checked again,
            // and its old walk-off must not survive into the tier-3 protocol.
            if (_wbHolePassMap is { } fullMap)
                foreach (var key in all.Where(e => (int?)e.Value?["map"] == fullMap).Select(e => e.Key).ToList()) all.Remove(key);
            _wbHolePassMap = null;
            foreach (var (k, v) in _wbHoleResults) all[Inv($"{(int?)v["map"]}:{k}")] = v.DeepClone();
            _wbHoleResults.Clear();
            File.WriteAllText(WbHolesPath, all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[verify-client] hole results -> {WbHolesPath}");
        }
        catch (Exception ex) { Console.WriteLine($"[verify-client] could not save {WbHolesPath}: {ex.Message}"); }
    }

    /// <summary>Steepest terrain slope around a point (degrees, sampled 1.5 yd out in 8 directions).
    /// Vanilla walks up to ~50 degrees: steeper ground under a spawn makes players slide off it and
    /// leaves the creature unable to path to anyone (a Gilneas wolf on a cliff lip, 2026-09-26).</summary>
    private float WbSlopeDegrees(float x, float y)
    {
        if (_terrain?.SampleHeight(x, y) is not { } h) return 0f;
        float worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            if (_terrain.SampleHeight(x + MathF.Cos(a) * 1.5f, y + MathF.Sin(a) * 1.5f) is { } n)
                worst = MathF.Max(worst, MathF.Abs(n - h) / 1.5f);
        }
        return MathF.Atan(worst) * 180f / MathF.PI;
    }

    /// <summary>Liquid surface at a point from the ADT's MCLQ (the cell must render liquid), or null.</summary>
    private float? WbWaterLevel(float x, float y)
    {
        var (col, row) = WorldBuilderLaw.TileOf(x, y);
        if (_adts?.Get(col, row)?.Chunks is not { } chunks) return null;
        float gr = ((32 - row) * WorldBuilderLaw.Tile - x) / WorldBuilderLaw.Unit;
        float gc = ((32 - col) * WorldBuilderLaw.Tile - y) / WorldBuilderLaw.Unit;
        int iy = Math.Clamp((int)(gr / 8), 0, 15), ix = Math.Clamp((int)(gc / 8), 0, 15);
        var chunk = chunks.FirstOrDefault(c => c?.IndexX == ix && c.IndexY == iy);
        if (chunk?.Liquid is not { Count: > 0 } layers) return null;
        int cell = Math.Clamp((int)(gr - iy * 8), 0, 7) * 8 + Math.Clamp((int)(gc - ix * 8), 0, 7);
        foreach (var l in layers)
            if (l.TileRender.Length == 64 && l.TileRender[cell]) return l.MaxHeight;
        return null;
    }

    /// <summary>How many of 8 horizontal directions hit geometry within 0.7 yd at chest height.</summary>
    private int WbWallsAround(Vector3 feet)
    {
        int walls = 0;
        for (int i = 0; i < 8; i++)
        {
            var dir = new Vector3(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4), 0);
            if (_collision?.Raycast(feet + new Vector3(0, 0, 1.0f), dir, 0.7f) is not null) walls++;
        }
        return walls;
    }

    private void WbClientFinding(string sev, string subject, string msg, Vector3 at)
    {
        _wbClientFindings.Add((sev, subject, msg, at));
        if (WbScriptPath is not null)
            Console.WriteLine($"[verify-client] {sev,-5} {subject}: {msg}  @({at.X:F0}, {at.Y:F0}, {at.Z:F1})");
    }

    /// <summary>Script: visit every 160-yd cell holding targets so its tiles load, then check it.</summary>
    /// <summary>
    /// Script: visit every 160-yd cell holding targets so its tiles load, then check it. With
    /// <paramref name="changedOnly"/> only targets whose row changed since their last CLEAN check, or
    /// that stand within 80 yd of a placement added/moved since then, are visited
    /// (<see cref="WbVerifiedPath"/> remembers what passed).
    /// </summary>
    private void WbQueueSpawnVerify(bool fix, bool changedOnly = false)
    {
        _wbFixItems = fix ? new JsonArray() : null;
        _wbHolePassMap = changedOnly ? null : _config.Start.Map;
        _wbClientFindings.Clear();
        _wbClientSeen.Clear();
        _wbClientChecked = 0;
        _wbHoleResults.Clear();
        var all = WbVerifyTargets();
        var targets = all;
        if (changedOnly)
        {
            var (spawnsOk, placementsOk) = WbLoadVerified();
            var moved = WbPlacementKeys().Where(p => !placementsOk.Contains(p.Key)).Select(p => p.At).ToList();
            targets = all.Where(t => !spawnsOk.Contains(WbTargetKey(t.Subject, t.At)) ||
                                     moved.Any(m => Vector2.Distance(new Vector2(m.X, m.Y), new Vector2(t.At.X, t.At.Y)) < 80f)).ToList();
            Console.WriteLine($"[verify-client] changed-only: {targets.Count} of {all.Count} target(s) ({moved.Count} placement(s)/sculpted vertices new or changed since the last clean pass)");
        }
        _wbVerifyScope = targets.Select(t => t.Subject).ToHashSet();
        var cells = targets.GroupBy(t => ((int)MathF.Floor(t.At.X / 160f), (int)MathF.Floor(t.At.Y / 160f)))
            .OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2).ToList();
        Console.WriteLine($"[verify-client] {cells.Sum(c => c.Count())} target(s) in {cells.Count} cell(s) on map {_config.Start.Map}" +
                          (_wbVerify is null ? " - no verifier report loaded, terrain holes NOT included (run verify first)"
                                             : $", {targets.Count(t => t.Kind == "hole")} of them terrain holes"));
        _wbScriptInject.Enqueue("fly on");
        foreach (var c in cells)
        {
            var mid = c.Aggregate(Vector3.Zero, (a, t) => a + t.At) / c.Count();
            _wbScriptInject.Enqueue(Inv($"goto {mid.X:F1} {mid.Y:F1} {mid.Z + 30f:F1}"));
            _wbScriptInject.Enqueue("wait 5");
            _wbScriptInject.Enqueue(Inv($"vcheck {mid.X:F1} {mid.Y:F1} 160"));
        }
        _wbScriptInject.Enqueue("vsummary");
    }

    /// <summary>
    /// Script: four eye-level shots of a point, one from each side: the character stands OUTSIDE,
    /// <paramref name="distance"/> yd from the point, faces it, and the camera sits just behind the
    /// character (a camera orbiting the point itself ends up inside the building it should show).
    /// Camera forward is (cos yaw, sin yaw) in world X/Y.
    /// </summary>
    private void WbQueueSurvey(float x, float y, float distance, string name)
    {
        _wbScriptInject.Enqueue("fly off");
        foreach (var (yawDeg, side) in new[] { (0, "s"), (90, "e"), (180, "n"), (270, "w") })
        {
            float yaw = yawDeg * MathF.PI / 180f;
            float px = x - MathF.Cos(yaw) * distance, py = y - MathF.Sin(yaw) * distance;
            _wbScriptInject.Enqueue(Inv($"goto {px:F1} {py:F1} ground {yaw:F4}"));
            _wbScriptInject.Enqueue(Inv($"cam {yawDeg} 8 7"));
            _wbScriptInject.Enqueue("wait 3");
            _wbScriptInject.Enqueue($"shot {name}-{side}");
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
