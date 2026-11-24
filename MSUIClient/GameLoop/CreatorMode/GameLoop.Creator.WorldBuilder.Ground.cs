using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: the editing camera, the ground grid and road surfaces (shared_docs/WORLD_BUILDER.md §4).
//
// Fly camera = the existing Free view (Ctrl+F); while a World Builder tool owns the left button its box-selection is
// suppressed, and tools reach ground far below the camera. The ground grid draws the terrain's real vertex cells
// (about 4.2 yd) and chunk borders (33.3 yd) around the pointer, highlights the picked cell and can snap points to
// it. Road surfaces paint a vanilla Tileset texture along the path in the editor - and along the pack's saved paths,
// on every World Builder page - with TerrainPaintLaw (the same rule the build must use) straight into the loaded
// tile's texture layers, then rebuild that tile. The original layers are kept per tile and put back whenever a road
// changes, the World Builder closes or a tile re-streams.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private bool _wbShowGrid = true, _wbSnapGrid;
    private const float WbPickReach = 900f;
    private const float WbFlyStartPitchDegrees = 60f;   // the fly camera opens looking down at the ground

    /// <summary>
    /// The World Builder's editing camera: the free view (however it was entered - the page's Fly camera button, the
    /// top bar's Free view, Ctrl+F) while the World page is open. Its tilt is free - the mouse and the View angle slider
    /// own it, the Command View's angle knob does not (players tune that one for commanding units).
    /// </summary>
    private bool WbFreeCameraActive => _freeView && _creatorWorldRequested && _creatorPanel == CreatorPanel.World;

    // ── fly camera free look: right-drag turns AND tilts the camera where it is ──────────────────────────────────
    // The Command View camera sits on a boom behind its rig, so a plain right-drag swings the camera around the rig,
    // and the knob schemes never tilt it at all. In the World Builder a right-drag is a free look instead: the camera
    // stays exactly where it is and the view turns (yaw) and tilts (pitch, straight down to straight up) about the
    // camera itself. Each frame the rig is placed back along the boom so the camera point does not move. WASD, the
    // wheel and edge pan still fly while the button is held, and carry the camera point with them.
    private bool _wbLooking;
    private Vector3 _wbLookEye;
    private Vector3 _wbLookRigLast;

    /// <summary>The World Builder camera owns this right-drag as an in-place free look.</summary>
    private bool WbOrbitClaimsRightDrag =>
        WbFreeCameraActive && !CommandViewLocked && !_commanderMapOpen && _window.MouseRightDown;

    /// <summary>
    /// One free-look frame, called where the Command View picks its camera target - after the window applied this
    /// frame's mouse yaw/pitch and after every rig move. Returns the rig when the look owns it, otherwise null (and ends
    /// any look). Camera = eye target + OrbitDirection * EffectiveDistance, so keeping the camera at the held point
    /// means rig = point - OrbitDirection * EffectiveDistance - eye height.
    /// </summary>
    private Vector3? WbUpdateFlyOrbit()
    {
        if (!WbOrbitClaimsRightDrag || !_window.MouseCaptured || _controller is null)
        {
            _wbLooking = false;
            return null;
        }
        var cam = _window.Camera;
        if (!_wbLooking)
        {
            _wbLookEye = cam.Position;
            _wbLookRigLast = _controller.Position;
            _wbLooking = true;
        }

        // Flying while holding the button moves the camera point with the rig.
        _wbLookEye += _controller.Position - _wbLookRigLast;

        Vector3 rig = _wbLookEye - cam.OrbitDirection * cam.EffectiveDistance - new Vector3(0f, 0f, cam.EyeHeight);
        // The free view's floor clamp (FlyFloorClearance, 2 yd) would lift a rig placed near the ground on its next
        // update, and the carry above would read that lift as flying - the camera would creep upward frame by frame.
        // Lift it here instead: looking steeply down from low over the ground shifts the view a little, nothing builds.
        if (!_controller.UnderTerrainShell && _terrain?.SampleHeight(rig.X, rig.Y) is float floorZ &&
            rig.Z < floorZ + 2.01f)
            rig.Z = floorZ + 2.01f;
        _controller.Position = rig;
        _wbLookRigLast = rig;
        return rig;
    }

    // vanilla ground textures
    private List<string>? _wbGroundTextures;
    private string[] _wbGroundFolders = Array.Empty<string>();
    private readonly byte[] _wbTexSearch = new byte[64];
    private int _wbTexFolder;

    // road paint preview
    private sealed class WbPaintSnapshot
    {
        public required AdtTerrainReader.AdtResult Adt;
        public required List<string> Textures;
        public required AdtTerrainReader.MclyLayer[][] Layers;
    }
    private readonly Dictionary<(int col, int row), WbPaintSnapshot> _wbPaintSnapshots = new();
    private string _wbPaintSig = "";
    private double _wbPaintAt;

    /// <summary>A World Builder action owns the left button (no Free view box-selection, no camera look).</summary>
    private bool WbOwnsLeftClick => _creatorWorldRequested && (_wbTool != WorldBuilderTool.None || WbNpcHasActiveAction);

    // ── camera + grid controls (workspace header) ────────────────────────────

    private void DrawWbViewControls()
    {
        if (CreatorButton(_freeView ? "Back to my character" : "Fly camera")) WbToggleFlyCamera();
        ObserveWorldBuilderUiItem("Fly camera");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_freeView
                ? "Return the camera to your character (Ctrl+F)."
                : "Detach the camera and fly: W A S D move, Space up, Caps Lock down, Shift faster, right-drag looks\n" +
                  "around where you are (turn and tilt), mouse wheel flies forward or back.\n" +
                  "The pointer stays free for the World Builder tools. (Ctrl+F)");
        CreatorSameLineIfFits(CreatorCheckboxWidth("Grid"));   // wrap rather than run off a narrow sidebar
        ImGui.Checkbox("Grid", ref _wbShowGrid);
        ObserveWorldBuilderUiItem("Ground grid");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the terrain's cells and chunk borders around the pointer while a tool is active.");
        CreatorSameLineIfFits(CreatorCheckboxWidth("Snap"));
        ImGui.Checkbox("Snap", ref _wbSnapGrid);
        ObserveWorldBuilderUiItem("Snap to grid");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Path points, buildings and objects land exactly on terrain grid corners.");
        if (WbFreeCameraActive)
        {
            // The camera's own tilt, live: it follows a right-drag, and dragging it tilts the camera.
            // Positive looks down, negative looks up.
            float limit = Camera.PitchLimit * 180f / MathF.PI;
            float degrees = _window.Camera.Pitch * 180f / MathF.PI;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("View angle", ref degrees, -limit, limit, "%.0f deg"))
                _window.Camera.Pitch = Math.Clamp(degrees, -limit, limit) * MathF.PI / 180f;
            ObserveWorldBuilderUiItem("View angle");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Camera tilt: positive looks down at the ground, negative looks up. Right-drag in the world does the same.");
        }
    }

    /// <summary>Enter or leave the fly camera. Entering lifts the camera well above trees and roofs and looks down.</summary>
    private void WbToggleFlyCamera()
    {
        if (_freeView) { ToggleFreeView(); return; }
        ToggleFreeView();
        if (!_freeView || _controller is null) return;
        _window.Camera.Pitch = WbFlyStartPitchDegrees * MathF.PI / 180f;
        var at = _controller.Position;
        float ground = _terrain?.SampleHeight(at.X, at.Y) ?? at.Z;
        _controller.Teleport(at.X, at.Y, MathF.Max(at.Z, ground + 40f));
    }

    /// <summary>Tools pick ground under the pointer even from a high fly camera: the normal pick first (floors, roofs),
    /// then the terrain alone out to <see cref="WbPickReach"/> yards.</summary>
    private bool WbPickGround(Vector2 pixel, bool terrainOnly, out Vector3 hit)
    {
        hit = default;
        if (!terrainOnly && TryPickGround(pixel, out hit)) return true;
        return _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize) is { } ray &&
               TryPickTerrainSurface(ray.Origin, ray.Direction, WbPickReach, out hit, out _);
    }

    /// <summary>The nearest terrain grid corner (outer vertex), on the ground.</summary>
    private Vector3 WbSnapToGrid(Vector3 p)
    {
        float u = WorldBuilderLaw.Unit, c = WorldBuilderLaw.Corner;
        float x = c - MathF.Round((c - p.X) / u) * u, y = c - MathF.Round((c - p.Y) / u) * u;
        return new Vector3(x, y, _terrain?.SampleHeight(x, y) ?? p.Z);
    }

    /// <summary>The terrain's own grid around the pointer: cell lines (faint), chunk borders (bright), the picked cell
    /// filled, and where it is (tile, chunk, cell).</summary>
    private void WbDrawGroundGrid()
    {
        if (!_wbShowGrid || _wbCursor is not { } at || _terrain is null || _creatorPanel != CreatorPanel.World) return;
        if (_wbTool == WorldBuilderTool.None && !_wbPathDragging) return;
        const int reach = 7;
        float u = WorldBuilderLaw.Unit, corner = WorldBuilderLaw.Corner;
        var draw = ImGui.GetBackgroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        float gx = (corner - at.X) / u, gy = (corner - at.Y) / u;      // global vertex coordinates
        int i0 = (int)MathF.Floor(gx), j0 = (int)MathF.Floor(gy);
        Vector3 V(int i, int j)
        {
            float x = corner - i * u, y = corner - j * u;
            return new Vector3(x, y, (_terrain.SampleHeight(x, y) ?? at.Z) + 0.12f);
        }
        uint Colour(int index, int from, float baseAlpha)
        {
            float fade = 1f - MathF.Min(1f, MathF.Abs(index - from) / (reach + 1f));
            bool chunk = ((index % 8) + 8) % 8 == 0;
            uint a = (uint)(255 * baseAlpha * fade * (chunk ? 1f : 0.55f));
            return (a << 24) | (chunk ? 0x0040E0FFu : 0x00FFFFFFu);
        }
        for (int i = i0 - reach; i <= i0 + reach + 1; i++)
        {
            Vector2? prev = null;
            for (int j = j0 - reach; j <= j0 + reach + 1; j++)
            {
                Vector2? px = cam.TryProjectToScreen(V(i, j), display, out var s, out _) ? s : null;
                if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, Colour(i, i0, 0.8f), ((i % 8) + 8) % 8 == 0 ? 2f : 1f);
                prev = px;
            }
        }
        for (int j = j0 - reach; j <= j0 + reach + 1; j++)
        {
            Vector2? prev = null;
            for (int i = i0 - reach; i <= i0 + reach + 1; i++)
            {
                Vector2? px = cam.TryProjectToScreen(V(i, j), display, out var s, out _) ? s : null;
                if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, Colour(j, j0, 0.8f), ((j % 8) + 8) % 8 == 0 ? 2f : 1f);
                prev = px;
            }
        }
        if (cam.TryProjectToScreen(V(i0, j0), display, out var q0, out _) && cam.TryProjectToScreen(V(i0, j0 + 1), display, out var q1, out _) &&
            cam.TryProjectToScreen(V(i0 + 1, j0 + 1), display, out var q2, out _) && cam.TryProjectToScreen(V(i0 + 1, j0), display, out var q3, out _))
            draw.AddQuadFilled(q0, q1, q2, q3, 0x5040E0FFu);
        if (_wbSnapGrid && _wbTool is WorldBuilderTool.Path or WorldBuilderTool.Place or WorldBuilderTool.GameObject &&
            cam.TryWorldToScreen(at + new Vector3(0, 0, 0.12f), display, out var snapped))
            draw.AddCircle(snapped, 7f, 0xFFFFFFFFu, 12, 2f);

        var (col, row) = WorldBuilderLaw.TileOf(at.X, at.Y);
        int chunkX = Math.Clamp((((j0 % 128) + 128) % 128) / 8, 0, 15), chunkY = Math.Clamp((((i0 % 128) + 128) % 128) / 8, 0, 15);
        string where = Inv($"tile {col},{row}  chunk {chunkX},{chunkY}  cell {((j0 % 8) + 8) % 8},{((i0 % 8) + 8) % 8}  height {at.Z:F1}");
        var mouse = ImGui.GetIO().MousePos + new Vector2(16f, 40f * CreatorUiScale);
        draw.AddText(mouse + Vector2.One, 0xD0000000u, where);
        draw.AddText(mouse, 0xFFB0E8FFu, where);
    }

    // ── vanilla ground texture picker ────────────────────────────────────────

    private static readonly string[] WbTextureChips = { "road", "dirt", "path", "cobble", "stone", "rock", "grass", "sand", "mud", "snow" };

    private void WbEnsureGroundTextures()
    {
        if (_wbGroundTextures is not null || _mpq is null) return;
        _wbGroundTextures = _mpq.ListedFiles()
            .Where(f => f.StartsWith("Tileset\\", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        _wbGroundFolders = new[] { "All zones" }.Concat(_wbGroundTextures
            .Select(f => f.Split('\\') is { Length: > 2 } parts ? parts[1] : "")
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Thumbnail grid of the game's own ground textures (Tileset\Zone\*.blp). True when the choice changed.</summary>
    private bool DrawWbTexturePicker(ref string? selected)
    {
        WbEnsureGroundTextures();
        if (_wbGroundTextures is null) { ImGui.TextDisabled("Game textures are not loaded."); return false; }
        bool changed = false;
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        ImGui.Combo("Zone##wb-tex", ref _wbTexFolder, _wbGroundFolders, _wbGroundFolders.Length);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Search textures##wb-tex", _wbTexSearch, (uint)_wbTexSearch.Length);
        ObserveWorldBuilderUiItem("Texture search");
        bool firstChip = true;
        foreach (var chip in WbTextureChips)
        {
            float width = ImGui.CalcTextSize(chip).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if (!firstChip && ImGui.GetItemRectMax().X + width < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
            if (ImGui.SmallButton(chip + "##texchip")) WbSetBuf(_wbTexSearch, chip);
            firstChip = false;
        }
        string folder = _wbTexFolder > 0 && _wbTexFolder < _wbGroundFolders.Length ? _wbGroundFolders[_wbTexFolder] : "";
        var words = WbText(_wbTexSearch).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _wbGroundTextures
            .Where(f => folder.Length == 0 || f.StartsWith("Tileset\\" + folder + "\\", StringComparison.OrdinalIgnoreCase))
            .Where(f => words.All(w => f.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(120).ToList();
        float thumb = 64f * CreatorUiScale;
        if (ImGui.BeginChild("##wb-tex-grid", new Vector2(-1f, thumb * 2.6f + 8f), true))
        {
            float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            for (int i = 0; i < hits.Count; i++)
            {
                string path = hits[i];
                if (i > 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + thumb < right) ImGui.SameLine();
                uint handle = _gameplayArt?.Handle(path) ?? 0;
                if (handle != 0) ImGui.Image((nint)handle, new Vector2(thumb, thumb));
                else ImGui.Dummy(new Vector2(thumb, thumb));
                bool isSelected = string.Equals(path, selected, StringComparison.OrdinalIgnoreCase);
                if (isSelected) ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - Vector2.One * 2, ImGui.GetItemRectMax() + Vector2.One * 2, 0xFF33DDEEu, 0f, ImDrawFlags.None, 3f);
                string name = Path.GetFileNameWithoutExtension(path);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
                if (ImGui.IsItemClicked()) { selected = path; changed = true; }
                ObserveWorldBuilderUiItem("Texture: " + name);
            }
        }
        ImGui.EndChild();   // always, whatever BeginChild returned
        ImGui.TextDisabled(hits.Count == 120 ? "First 120 shown; narrow the search." : $"{hits.Count} textures");
        return changed;
    }

    // ── road paint preview ───────────────────────────────────────────────────

    /// <summary>One road to preview: its name (paint order), its route, and the texture passes laid along it - the shoulder
    /// first when it has one, then the road (web GradedPath.PaintSurface paints the same passes in the same order).</summary>
    private sealed record WbRoadSpec(string Key, List<Vector3> Points, List<(string Texture, TerrainPaintLaw.RoadLook Look)> Passes)
    {
        public float Reach => Passes.Max(p => p.Look.Reach);
    }

    /// <summary>The paint passes of one road surface: the shoulder (road width plus <paramref name="extra"/> each side, the
    /// road's wobble, its own pattern) under the road. The same construction as web PathSurface.Look/ShoulderLook.</summary>
    private static List<(string Texture, TerrainPaintLaw.RoadLook Look)> WbRoadPasses(string texture, float width, float soft, float strength,
        float ragged, float patchy, int seed, string? shoulderTexture, float extra, float shoulderSoft, float shoulderStrength, float shoulderPatchy)
    {
        var passes = new List<(string, TerrainPaintLaw.RoadLook)>();
        if (shoulderTexture is not null && extra > 0f)
            passes.Add((shoulderTexture, new TerrainPaintLaw.RoadLook(width + 2f * extra, MathF.Max(0f, shoulderSoft), Math.Clamp(shoulderStrength, 0f, 1f),
                MathF.Max(0f, ragged), Math.Clamp(shoulderPatchy, 0f, 1f), unchecked(seed + 101))));
        passes.Add((texture, new TerrainPaintLaw.RoadLook(width, MathF.Max(0f, soft), Math.Clamp(strength, 0f, 1f), MathF.Max(0f, ragged),
            Math.Clamp(patchy, 0f, 1f), seed)));
        return passes;
    }

    /// <summary>
    /// The road surfaces painted into the loaded tiles while the World Builder is open. The pack's saved paths on this
    /// map stay painted on EVERY World Builder page while "Show saved paths in the world" is ticked - a saved road
    /// must not vanish because another page was opened. The path in the editor is painted live while the Paths page is
    /// open; off that page the saved version of it shows instead. Roads paint in name order (ordinal), as the build does,
    /// so where two roads cross the preview shows the same one on top as the published pack will.
    /// </summary>
    private List<WbRoadSpec> WbRoadSpecsToPreview(int map)
    {
        var specs = new List<WbRoadSpec>();
        if (_creatorPanel != CreatorPanel.World) return specs;
        bool editorLive = WbPathPageOpen && _wbRoadTexture is not null && _wbCarvePoints.Count >= 2 && _wbCarveMap == map &&
                          float.IsFinite(_wbRoadWidth) && _wbRoadWidth > 0;
        if (_wbPathShowSaved)
            foreach (var doc in WbPathDocs(_wbPackId))
            {
                string key = (string?)doc["docKey"] ?? "";
                if (editorLive && key == _wbPathEditKey) continue;   // the live version replaces it
                if (doc["body"] is not JsonObject body || (int?)body["map"] != map) continue;
                if (body["surface"] is not JsonObject s || s["texture"]?.ToString() is not { Length: > 0 } texture) continue;
                var pts = WbPathPoints(body);
                float width = WbNpcFloat(s["width"], WbNpcFloat(body["width"], 6f));
                if (pts.Count < 2 || !float.IsFinite(width) || width <= 0f) continue;
                var sh = s["shoulder"] as JsonObject;
                specs.Add(new WbRoadSpec(key, pts, WbRoadPasses(texture, width, WbNpcFloat(s["softEdge"], 1.5f), WbNpcFloat(s["strength"], 1f),
                    WbNpcFloat(s["ragged"], 0f), WbNpcFloat(s["patchy"], 0f), (int?)s["seed"] ?? 0,
                    sh?["texture"]?.ToString() is { Length: > 0 } verge ? verge : null, WbNpcFloat(sh?["extra"], 0f),
                    WbNpcFloat(sh?["softEdge"], 2f), WbNpcFloat(sh?["strength"], 1f), WbNpcFloat(sh?["patchy"], 0f))));
            }
        if (editorLive)
        {
            string name = WbText(_wbCarveName).Trim();
            specs.Add(new WbRoadSpec(name.Length > 0 ? name : _wbPathEditKey ?? "", _wbCarvePoints.ToList(),
                WbRoadPasses(_wbRoadTexture!, _wbRoadWidth, _wbRoadSoft, _wbRoadStrength, _wbRoadRagged, _wbRoadPatchy, _wbRoadSeed,
                    _wbShoulderTexture, _wbShoulderExtra, _wbShoulderSoft, _wbShoulderStrength, 0f)));
        }
        return specs.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Paint the previewed road surfaces (<see cref="WbRoadSpecsToPreview"/>) into the loaded tiles
    /// (throttled while dragging); restore the original layers whenever the set changes or nothing is shown.
    /// Called every World Builder frame.</summary>
    private void WbUpdateRoadPaintPreview()
    {
        int map = _config.Start.Map;
        // A re-streamed tile comes back as a fresh, unpainted instance: drop its snapshot and repaint.
        foreach (var (key, snap) in _wbPaintSnapshots.ToList())
            if (!_adts!.TryPeek(key.col, key.row, out var now) || !ReferenceEquals(now, snap.Adt))
            { _wbPaintSnapshots.Remove(key); _wbPaintSig = "\u0001"; }

        var specs = _adts is null ? new List<WbRoadSpec>() : WbRoadSpecsToPreview(map);
        bool active = specs.Count > 0;
        string sig = active
            ? Inv($"{map}|") + string.Join("||", specs.Select(s =>
                string.Join("+", s.Passes.Select(p => Inv($"{p.Texture}|{p.Look}"))) + "|" + string.Join(";", s.Points.Select(p => Inv($"{p.X},{p.Y}")))))
            : "";
        if (sig == _wbPaintSig) return;
        double t = ImGui.GetTime();
        if (sig.Length > 0 && t - _wbPaintAt < 0.25) return;   // a full tile rebuild per change: keep drags responsive
        _wbPaintAt = t;
        _wbPaintSig = sig;

        var rebuild = new HashSet<(int col, int row)>();
        foreach (var (key, snap) in _wbPaintSnapshots)
        {
            snap.Adt.Textures = new List<string>(snap.Textures);
            if (snap.Adt.Chunks is { } chunks)
                for (int i = 0; i < chunks.Length && i < snap.Layers.Length; i++)
                    if (chunks[i] is { } chunk) chunk.Layers = snap.Layers[i];
            rebuild.Add(key);
        }
        _wbPaintSnapshots.Clear();

        int painted = 0;
        foreach (var spec in specs)
        {
            var pts = spec.Points;
            float reach = spec.Reach;
            float minX = pts.Min(p => p.X) - reach, maxX = pts.Max(p => p.X) + reach;
            float minY = pts.Min(p => p.Y) - reach, maxY = pts.Max(p => p.Y) + reach;
            var (c0, r0) = WorldBuilderLaw.TileOf(maxX, maxY);
            var (c1, r1) = WorldBuilderLaw.TileOf(minX, minY);
            for (int col = c0; col <= c1; col++)
                for (int row = r0; row <= r1; row++)
                {
                    if (!_adts!.TryPeek(col, row, out var adt) || adt?.Chunks is not { } chunks) continue;
                    // The snapshot is the tile as it was before ANY road touched it this round.
                    var snap = _wbPaintSnapshots.TryGetValue((col, row), out var taken) ? taken : new WbPaintSnapshot
                    {
                        Adt = adt, Textures = new List<string>(adt.Textures),
                        Layers = chunks.Select(c => c?.Layers ?? Array.Empty<AdtTerrainReader.MclyLayer>()).ToArray(),
                    };
                    // Each pass keeps the one before it: the road never evicts the shoulder it was just laid on.
                    int n = 0;
                    string? keep = null;
                    foreach (var (texture, look) in spec.Passes) { n += WbPaintRoadTile(adt, col, row, texture, look, pts, keep); keep = texture; }
                    if (n == 0) continue;
                    painted += n;
                    _wbPaintSnapshots[(col, row)] = snap;
                    rebuild.Add((col, row));
                }
        }
        if (WbScriptPath is not null && active)
            Console.WriteLine($"[wb] road paint preview: {specs.Count} road(s), {painted} chunk(s) on {_wbPaintSnapshots.Count} tile(s)");
        foreach (var key in rebuild)
            if (_terrain?.IsResident(key) == true && _adts!.TryPeek(key.col, key.row, out var a) && a is not null)
                _terrain.RebuildTile(key, a);
    }

    /// <summary>Paint one tile's chunks that the road reaches; returns how many chunks changed. New layer objects
    /// replace the old ones (never mutated), so the snapshot can put the originals back.</summary>
    private int WbPaintRoadTile(AdtTerrainReader.AdtResult adt, int col, int row, string texture, TerrainPaintLaw.RoadLook look, List<Vector3> pts,
        string? keep = null)
    {
        float reach = look.Reach;
        int texIndex = adt.Textures.FindIndex(t => string.Equals(t, texture, StringComparison.OrdinalIgnoreCase));
        int keepIndex = keep is null ? -1 : adt.Textures.FindIndex(t => string.Equals(t, keep, StringComparison.OrdinalIgnoreCase));
        bool added = false;
        int changed = 0;
        foreach (var chunk in adt.Chunks!)
        {
            if (chunk is null || chunk.Layers.Length == 0) continue;
            var centre = TerrainPaintLaw.PixelWorld(col, row, chunk.IndexX, chunk.IndexY, 31, 31);
            if (WorldBuilderLaw.PathDistance(pts, centre).Distance > reach + TerrainPaintLaw.ChunkSize * 0.75f) continue;
            if (texIndex < 0) { texIndex = adt.Textures.Count; added = true; }
            var textures = chunk.Layers.Select(l => l.TextureIndex).ToList();
            var alphas = chunk.Layers.Select((l, i) => i == 0 ? null : l.AlphaMap).ToList();
            int cx = chunk.IndexX, cy = chunk.IndexY;
            bool paintedChunk = TerrainPaintLaw.PaintChunk(textures, alphas, texIndex, (r, c) =>
            {
                var p = TerrainPaintLaw.PixelWorld(col, row, cx, cy, r, c);
                return TerrainPaintLaw.RoadAmountAt(p, WorldBuilderLaw.PathDistance(pts, p).Distance, look);
            }, keepIndex);
            if (!paintedChunk) continue;
            var old = chunk.Layers;
            chunk.Layers = textures.Select((tex, i) =>
            {
                var prior = old.FirstOrDefault(l => l.TextureIndex == tex);
                uint flags = (prior?.Flags ?? 0u) & ~0x300u;
                return new AdtTerrainReader.MclyLayer
                {
                    TextureIndex = tex,
                    Flags = i == 0 ? flags : flags | 0x100u,
                    OffsetInMcal = i == 0 ? 0u : (uint)((i - 1) * 2048),
                    EffectId = prior?.EffectId ?? 0,
                    AlphaMap = i == 0 ? null : alphas[i],
                };
            }).ToArray();
            changed++;
        }
        if (added && changed > 0) adt.Textures.Add(texture);
        return changed;
    }
}
