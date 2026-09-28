using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Doodads;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator Mode World Builder (shared_docs/WORLD_BUILDER.md §4).
//
// Terrain sculpt + WMO/M2 placement, authored against MangosSuperUI World Content
// Packs. The client never writes the server: every stroke/placement/move/delete is
// a POST to /WorldPacks/*, stored as an audited, undoable op, and reaches the game
// only through Publish (patch-7.MPQ + real extractor maps/vmaps/mmaps).
//
// PREVIEW LAW
//   Terrain: height = STOCK (read past patch-7) + server sum of enabled packs (+ the
//   selected pack) + the stroke in hand. Always from stock, so it is right whether or
//   not an up-to-date patch-7 is mounted.
//   Placements: an unpublished (or not-yet-downloaded) placement draws as a dynamic
//   ghost; a published one is already static in the mounted patch-7 ADT.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum WorldBuilderTool { None, Sculpt, Place, Select }

    private const string WbPatchName = "patch-7.MPQ";
    private const ulong WbCursorGhostKey = 0xB0B0_FFFF_FFFF_FFFFUL;

    private readonly WorldPackClient _wbClient = new();
    private WorldBuilderTool _wbTool;
    private int _wbPackId;

    // server state
    private WorldPackClient.State? _wbState;
    private Task<WorldPackClient.State>? _wbStateTask;
    private int _wbStateMap = -1;
    private double _wbStateAt;
    private readonly Dictionary<(int col, int row), float[]> _wbServerSculpt = new();

    // ops in flight + messages
    private readonly List<(string Label, Task<WorldPackClient.Reply> Task)> _wbOps = new();
    private string _wbMessage = "";

    // sculpt
    private WorldBuilderLaw.BrushMode _wbBrush = WorldBuilderLaw.BrushMode.Raise;
    private float _wbRadius = 18f, _wbStrength = 6f, _wbHardness = 0.35f;
    private readonly Dictionary<(int col, int row), float[]> _wbStroke = new();
    private bool _wbStroking;
    private float _wbFlattenTarget;
    private Vector3? _wbCursor;
    private double _wbLastDab, _wbLastRebuild;
    private readonly HashSet<(int col, int row)> _wbDirtyTiles = new();

    // stock terrain + what the preview currently owns
    private sealed class WbStockTile
    {
        public required Dictionary<(int x, int y), (float[] Heights, sbyte[]? Normals, float BaseZ)> Chunks;
    }
    private readonly Dictionary<(int col, int row), WbStockTile?> _wbStock = new();
    private readonly Dictionary<(int col, int row), AdtTerrainReader.AdtResult> _wbAppliedTo = new();
    private readonly HashSet<(int col, int row)> _wbPreviewTiles = new();
    private int? _wbStockBuild;   // last publish the pack-map preview bases were derived for

    // placement
    private List<string>? _wbCatalogue;
    private readonly byte[] _wbSearchBuf = new byte[96];
    private string _wbSearchLast = "\u0001";
    private List<string> _wbSearchHits = new();
    private string? _wbModel;
    private float _wbYaw, _wbPitch, _wbRoll, _wbScale = 1f, _wbZOffset;
    private int _wbDoodadSet;
    private readonly Dictionary<int, string> _wbGhosts = new();     // placement id → signature
    private bool _wbGhostsHidden;
    private string? _wbCursorGhostSig;
    private int _wbSelected;
    private bool _wbMoveArmed;

    // publish / download
    private bool _wbRestartOnPublish = true;
    private Task<WorldPackClient.BuildStatus>? _wbStatusTask;
    private WorldPackClient.BuildStatus? _wbStatus;
    private double _wbStatusAt;
    private Task<long>? _wbDownloadTask;
    private Task<WorldPackCollisionSync.Result>? _wbCollisionTask;   // vmaps/mmaps after the patch
    private int? _wbMountedBuild;
    private bool _wbMountedRead;
    private bool _wbReservedLeft;

    // new pack form
    private readonly byte[] _wbNewKey = new byte[64];
    private readonly byte[] _wbNewName = new byte[96];

    private static string WbText(byte[] buf) => Encoding.UTF8.GetString(buf).TrimEnd('\0').Trim();

    // ═══════════════════════════════════════════════════════════════ sections

    private void RegisterCreatorWorldSections()
    {
        CreatorSection("World", "wb-pack", "World Content Pack", true, DrawWbPackSection);
        CreatorSection("World", "wb-sculpt", "Sculpt terrain", true, DrawWbSculptSection);
        CreatorSection("World", "wb-place", "Place buildings & doodads", true, DrawWbPlaceSection);
        CreatorSection("World", "wb-list", "Placements near you", false, DrawWbListSection);
        CreatorSection("World", "wb-publish", "Publish & download", true, DrawWbPublishSection);
        RegisterCreatorVerifySection();
        RegisterCreatorContentSections();
    }

    private string WorldBuilderStatus()
    {
        string pack = _wbState?.Packs.FirstOrDefault(p => p.Id == _wbPackId) is { } p
            ? $"{p.PackKey}{(p.Enabled ? "" : " (disabled)")}" : "no pack";
        string tool = _wbTool == WorldBuilderTool.None ? "" : $"  |  {_wbTool}";
        return $"{pack}{tool}{(_wbOps.Count > 0 ? $"  |  {_wbOps.Count} saving" : "")}";
    }

    private void DrawWbPackSection()
    {
        if (SuiWebAppUrl.Length == 0)
        {
            ImGui.TextWrapped("Set the MangosSuperUI address first (login screen > Web App). " +
                              "World Builder edits are stored and audited by the web app.");
            return;
        }
        if (_wbState is null)
        {
            ImGui.TextDisabled(_wbStateTask is null ? "not loaded" : "loading packs...");
            if (CreatorButton("Load")) WbRequestState();
            return;
        }

        var packs = _wbState.Packs;
        string current = packs.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "(choose a pack)";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Pack", current))
        {
            foreach (var p in packs)
                if (ImGui.Selectable($"{p.Name}  [{p.PackKey}]{(p.Enabled ? "" : "  - disabled")}", p.Id == _wbPackId))
                {
                    _wbPackId = p.Id;
                    WbRequestState();
                }
            ImGui.EndCombo();
        }

        if (packs.FirstOrDefault(p => p.Id == _wbPackId) is { } sel)
        {
            bool enabled = sel.Enabled;
            if (ImGui.Checkbox("Enabled (ships on the next publish)", ref enabled))
                WbOp(enabled ? $"enable {sel.PackKey}" : $"disable {sel.PackKey}",
                    _wbClient.SetEnabledAsync(SuiWebAppUrl, sel.Id, enabled));
            ImGui.TextDisabled($"{sel.Placements} placement(s), {sel.SculptVertices:N0} sculpted vertices, {sel.UndoableOps} undoable op(s)");
            if (CreatorButton("Undo last (Ctrl+Z)")) WbUndo();
            ImGui.SameLine();
            if (CreatorButton("Refresh")) WbRequestState();
        }

        ImGui.Separator();
        ImGui.TextDisabled("New pack");
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        ImGui.InputText("key##wb-newkey", _wbNewKey, (uint)_wbNewKey.Length);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("name##wb-newname", _wbNewName, (uint)_wbNewName.Length);
        if (CreatorButton("Create pack") && WbText(_wbNewKey).Length > 0)
        {
            var task = _wbClient.CreatePackAsync(SuiWebAppUrl, WbText(_wbNewKey), WbText(_wbNewName), "created in Creator Mode");
            WbOp($"create pack {WbText(_wbNewKey)}", task, r => { if (r.Pack is { } p) _wbPackId = p.Id; });
        }
        if (_wbMessage.Length > 0) ImGui.TextWrapped(_wbMessage);
    }

    private void DrawWbSculptSection()
    {
        bool armed = _wbTool == WorldBuilderTool.Sculpt;
        if (ImGui.Checkbox("Sculpt tool (hold left mouse on terrain)", ref armed))
            WbSetTool(armed ? WorldBuilderTool.Sculpt : WorldBuilderTool.None);
        int mode = (int)_wbBrush;
        ImGui.RadioButton("Raise", ref mode, 0); ImGui.SameLine();
        ImGui.RadioButton("Lower", ref mode, 1); ImGui.SameLine();
        ImGui.RadioButton("Smooth", ref mode, 2); ImGui.SameLine();
        ImGui.RadioButton("Flatten", ref mode, 3);
        _wbBrush = (WorldBuilderLaw.BrushMode)mode;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Radius (yd)", ref _wbRadius, 3f, 120f, "%.0f");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat(_wbBrush <= WorldBuilderLaw.BrushMode.Lower ? "Strength (yd/s)" : "Strength", ref _wbStrength, 0.5f, 40f, "%.1f");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Hardness", ref _wbHardness, 0f, 0.95f, "%.2f");
        ImGui.TextDisabled("Shift inverts Raise/Lower. [ ] resize. A stroke is one undoable op.");
    }

    private void DrawWbPlaceSection()
    {
        bool armed = _wbTool == WorldBuilderTool.Place;
        if (ImGui.Checkbox("Place tool (left click drops the model)", ref armed))
            WbSetTool(armed ? WorldBuilderTool.Place : WorldBuilderTool.None);

        _wbCatalogue ??= WbBuildCatalogue();
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("search##wb-search", _wbSearchBuf, (uint)_wbSearchBuf.Length);
        string q = WbText(_wbSearchBuf);
        if (q != _wbSearchLast)
        {
            _wbSearchLast = q;
            var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _wbSearchHits = _wbCatalogue.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(300).ToList();
        }
        if (ImGui.BeginListBox("##wb-models", new Vector2(-1f, 150f * CreatorUiScale)))
        {
            foreach (var path in _wbSearchHits)
                if (ImGui.Selectable(path, path == _wbModel)) { _wbModel = path; _wbCursorGhostSig = null; }
            ImGui.EndListBox();
        }
        ImGui.TextDisabled($"{_wbSearchHits.Count} shown of {_wbCatalogue.Count} (WMO roots + World M2s)");
        ImGui.TextWrapped(_wbModel ?? "(pick a model)");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Heading", ref _wbYaw, 0f, 360f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Tilt X", ref _wbPitch, -45f, 45f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Tilt Z", ref _wbRoll, -45f, 45f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Height offset", ref _wbZOffset, -30f, 30f, "%.1f yd");
        if (WbModelIsM2(_wbModel))
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.SliderFloat("Scale (M2)", ref _wbScale, 0.2f, 6f, "%.2f");
        }
        else
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.InputInt("Doodad set", ref _wbDoodadSet);
            _wbDoodadSet = Math.Clamp(_wbDoodadSet, 0, 32);
        }
        ImGui.TextDisabled("[ ] rotate 15 deg (Shift 1 deg) while the tool is armed.");
    }

    private void DrawWbListSection()
    {
        if (_wbState is null || _controller is null) { ImGui.TextDisabled("not loaded"); return; }
        Vector3 me = _controller.Position;
        var near = _wbState.Placements
            .Where(p => !p.Deleted)
            .Select(p => (p, d: Vector2.Distance(new Vector2(p.PosX, p.PosY), new Vector2(me.X, me.Y))))
            .Where(x => x.d < 600f)
            .OrderBy(x => x.d)
            .Take(80)
            .ToList();
        bool select = _wbTool == WorldBuilderTool.Select;
        if (ImGui.Checkbox("Select tool (click near a placement)", ref select))
            WbSetTool(select ? WorldBuilderTool.Select : WorldBuilderTool.None);
        foreach (var (p, d) in near)
        {
            string packKey = _wbState.Packs.FirstOrDefault(x => x.Id == p.PackId)?.PackKey ?? "?";
            string label = $"#{p.Id} {Path.GetFileNameWithoutExtension(p.ModelPath)}  {d:F0} yd  [{packKey}]{(p.Published ? "" : " *")}";
            if (ImGui.Selectable(label, p.Id == _wbSelected)) _wbSelected = p.Id;
        }
        ImGui.TextDisabled("* = not published yet");

        if (_wbState.Placements.FirstOrDefault(p => p.Id == _wbSelected && !p.Deleted) is { } sel)
        {
            ImGui.Separator();
            ImGui.Text($"#{sel.Id} {sel.ModelPath}");
            ImGui.TextDisabled($"at ({sel.PosX:F1}, {sel.PosY:F1}, {sel.PosZ:F1})  heading {sel.RotY:F0}");
            float heading = sel.RotY;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Heading##sel", ref heading, 0f, 360f, "%.0f deg")) sel.RotY = heading;
            if (ImGui.IsItemDeactivatedAfterEdit())
                WbOp($"turn #{sel.Id}", _wbClient.MoveAsync(SuiWebAppUrl, sel));
            if (CreatorButton(_wbMoveArmed ? "Click the ground..." : "Move (click new spot)")) { _wbMoveArmed = true; WbSetTool(WorldBuilderTool.Select); }
            ImGui.SameLine();
            if (CreatorButton("Delete")) WbOp($"delete #{sel.Id}", _wbClient.DeleteAsync(SuiWebAppUrl, sel.Id));
            ImGui.SameLine();
            if (CreatorButton("Go to")) _controller.Teleport(sel.PosX, sel.PosY, sel.PosZ + 30f);
        }
    }

    private void DrawWbPublishSection()
    {
        if (SuiWebAppUrl.Length == 0) return;
        WbReadMountedBuild();
        var last = _wbStatus?.LastBuild ?? _wbState?.LastBuild;
        ImGui.TextWrapped($"Server: {(last is null ? "never published" : $"build #{last.BuildId} ({last.Size / 1024} KiB)")}" +
                          $"   Client {WbPatchName}: {(_wbMountedBuild is { } b ? $"build #{b}" : "not mounted")}");
        ImGui.Checkbox("Restart mangosd after publish (loads new collision/navmesh)", ref _wbRestartOnPublish);
        bool running = _wbStatus?.Running == true;
        if (!running && CreatorButton("Publish enabled packs"))
            WbOp("publish", _wbClient.PublishAsync(SuiWebAppUrl, _wbRestartOnPublish), _ => _wbStatusAt = 0);
        if (last is not null && last.BuildId != _wbMountedBuild && _wbDownloadTask is null && !running)
        {
            if (!running) ImGui.SameLine();
            if (CreatorButton($"Download & mount build #{last.BuildId}")) WbStartDownload();
        }
        if (_wbDownloadTask is not null) ImGui.TextDisabled("downloading patch-7.MPQ...");

        if (_wbStatus is { } st && (st.Running || st.Log.Count > 0))
        {
            ImGui.TextDisabled($"build #{st.BuildId}: {st.Status} - {st.Phase}" +
                               (st.Error is { } e ? $" - {e}" : ""));
            if (ImGui.BeginChild("##wb-log", new Vector2(-1f, 140f * CreatorUiScale), true))
            {
                foreach (var line in st.Log) ImGui.TextUnformatted(line);
                if (st.Running) ImGui.SetScrollHereY(1f);
            }
            ImGui.EndChild();
        }
    }

    // ═══════════════════════════════════════════════════════════════ per-frame

    /// <summary>Called from DrawCreatorHud every creator frame (ImGui is live).</summary>
    private void UpdateWorldBuilder()
    {
        bool panelOpen = _creatorPanel == CreatorPanel.World;
        if (!panelOpen && _wbTool != WorldBuilderTool.None) WbSetTool(WorldBuilderTool.None);
        PumpWbTasks();
        if (_wbState is null && _wbStateTask is null && panelOpen && SuiWebAppUrl.Length > 0) WbRequestState();
        if (_wbState is not null && _config.Start.Map != _wbStateMap && _wbStateTask is null) WbRequestState();

        WbKeepPreviewApplied();
        WbSyncGhosts();
        PumpWbDocs();
        PumpWbVerify();
        PumpWbQuestCheck();
        WbSyncSpawnPreview();
        if (_wbTool == WorldBuilderTool.None) { WbSetCursorGhost(null); return; }

        var io = ImGui.GetIO();
        bool overUi = io.WantCaptureMouse;
        _wbCursor = !overUi && TryPickGround(io.MousePos, out Vector3 hit) ? hit : null;

        if (!io.WantTextInput)
        {
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) WbUndo();
            float step = io.KeyShift ? 1f : 15f;
            if (_wbTool == WorldBuilderTool.Sculpt)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbRadius = MathF.Max(3f, _wbRadius / 1.15f);
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbRadius = MathF.Min(120f, _wbRadius * 1.15f);
            }
            else if (_wbTool == WorldBuilderTool.Place)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbYaw = (_wbYaw - step + 360f) % 360f;
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbYaw = (_wbYaw + step) % 360f;
            }
        }

        switch (_wbTool)
        {
            case WorldBuilderTool.Sculpt: WbSculptFrame(io, overUi); break;
            case WorldBuilderTool.Place: WbPlaceFrame(io, overUi); break;
            case WorldBuilderTool.Select: WbSelectFrame(io, overUi); break;
        }
        WbDrawCursorOverlay();
    }

    private void WbSetTool(WorldBuilderTool tool)
    {
        if (_wbStroking) WbFinishStroke();
        _wbTool = tool;
        if (tool != WorldBuilderTool.Select) _wbMoveArmed = false;
        bool reserve = tool != WorldBuilderTool.None;
        if (reserve && !_wbReservedLeft) { _window.LeftButtonReservedForWorldClicks = true; _wbReservedLeft = true; }
        else if (!reserve && _wbReservedLeft) { _window.LeftButtonReservedForWorldClicks = false; _wbReservedLeft = false; }
        if (tool != WorldBuilderTool.Place) WbSetCursorGhost(null);
    }

    /// <summary>World clicks are swallowed while a World Builder tool is armed (no stray targeting).</summary>
    private bool HandleWorldBuilderClick(WorldMouseClick click) =>
        _creatorWorldRequested && _wbTool != WorldBuilderTool.None;

    // ── sculpt ───────────────────────────────────────────────────────────────

    private void WbSculptFrame(ImGuiIOPtr io, bool overUi)
    {
        double now = ImGui.GetTime();
        if (_wbStroking && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) WbFinishStroke();
        if (!_wbStroking && !overUi && _wbCursor is { } start && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
            _wbStroking = true;
            _wbStroke.Clear();
            _wbFlattenTarget = start.Z;
            _wbLastDab = now;
        }
        if (_wbStroking && _wbCursor is { } at)
        {
            float dt = (float)Math.Clamp(now - _wbLastDab, 0.0, 0.1);
            _wbLastDab = now;
            var mode = _wbBrush;
            if (io.KeyShift && mode == WorldBuilderLaw.BrushMode.Raise) mode = WorldBuilderLaw.BrushMode.Lower;
            else if (io.KeyShift && mode == WorldBuilderLaw.BrushMode.Lower) mode = WorldBuilderLaw.BrushMode.Raise;
            float amount = mode <= WorldBuilderLaw.BrushMode.Lower ? _wbStrength * dt : Math.Clamp(_wbStrength * 0.15f * dt * 10f, 0f, 1f);
            WbDab(new Vector2(at.X, at.Y), mode, amount);
        }
        if (_wbDirtyTiles.Count > 0 && now - _wbLastRebuild > 0.12)
        {
            _wbLastRebuild = now;
            foreach (var key in _wbDirtyTiles.ToArray()) WbApplyTilePreview(key);
            _wbDirtyTiles.Clear();
        }
    }

    private void WbDab(Vector2 centre, WorldBuilderLaw.BrushMode mode, float amount)
    {
        var verts = WorldBuilderLaw.BrushVertices(centre, _wbRadius, _wbHardness);
        foreach (var (col, row, gr, gc, w) in verts)
        {
            if (WbStockFor((col, row)) is null) continue;
            if (!_wbStroke.TryGetValue((col, row), out var stroke))
                _wbStroke[(col, row)] = stroke = new float[WorldBuilderLaw.VertexCount];
            float h = WbHeight(col, row, gr, gc);
            float mean = 0f;
            if (mode == WorldBuilderLaw.BrushMode.Smooth)
                mean = (WbHeight(col, row, Math.Max(gr - 1, 0), gc) + WbHeight(col, row, Math.Min(gr + 1, 128), gc) +
                        WbHeight(col, row, gr, Math.Max(gc - 1, 0)) + WbHeight(col, row, gr, Math.Min(gc + 1, 128))) * 0.25f;
            stroke[gr * WorldBuilderLaw.Side + gc] += WorldBuilderLaw.Dab(mode, w, amount, h, mean, _wbFlattenTarget);
            _wbDirtyTiles.Add((col, row));
        }
    }

    /// <summary>Absolute outer-vertex height the brush sees: stock + server sum + stroke.</summary>
    private float WbHeight(int col, int row, int gr, int gc)
    {
        var stock = WbStockFor((col, row));
        if (stock is null) return 0f;
        int ix = Math.Min(gc / 8, 15), iy = Math.Min(gr / 8, 15);
        if (!stock.Chunks.TryGetValue((ix, iy), out var ch)) return 0f;
        int r = gr - iy * 8, c = gc - ix * 8;
        int g = gr * WorldBuilderLaw.Side + gc;
        return ch.BaseZ + ch.Heights[r * 17 + c]
               + (_wbServerSculpt.TryGetValue((col, row), out var s) ? s[g] : 0f)
               + (_wbStroke.TryGetValue((col, row), out var k) ? k[g] : 0f);
    }

    private void WbFinishStroke(string? label = null)
    {
        _wbStroking = false;
        if (_wbStroke.Count == 0) return;
        var tiles = new List<WorldPackClient.SculptTile>();
        foreach (var ((col, row), deltas) in _wbStroke)
        {
            var t = new WorldPackClient.SculptTile { Col = col, Row = row };
            for (int i = 0; i < deltas.Length; i++)
                if (MathF.Abs(deltas[i]) > 0.001f) t.Deltas[i] = deltas[i];
            if (t.Deltas.Count > 0) tiles.Add(t);
        }
        // Fold the stroke into the local server mirror now; the refresh after the POST confirms it.
        foreach (var ((col, row), deltas) in _wbStroke)
        {
            if (!_wbServerSculpt.TryGetValue((col, row), out var s))
                _wbServerSculpt[(col, row)] = s = new float[WorldBuilderLaw.VertexCount];
            for (int i = 0; i < deltas.Length; i++) s[i] += deltas[i];
            _wbPreviewTiles.Add((col, row));
            _wbDirtyTiles.Add((col, row));
        }
        _wbStroke.Clear();
        if (tiles.Count == 0) return;
        int n = tiles.Sum(t => t.Deltas.Count);
        WbOp(label is null ? $"sculpt ({_wbBrush}, {n} vertices)" : $"{label} ({n} vertices)",
            _wbClient.SculptAsync(SuiWebAppUrl, _wbPackId, _config.Start.Map, label ?? $"{_wbBrush} r{_wbRadius:F0}", tiles));
    }

    // ── terrain preview ──────────────────────────────────────────────────────

    private HashSet<(int, int)>? _wbPublishedStampCache;
    private uint _wbPublishedStampMap = uint.MaxValue;

    /// <summary>Published stamp ownership selects the sculpt preview's base terrain, never map artwork.</summary>
    private HashSet<(int, int)> WbPublishedStampedTiles(uint mapId)
    {
        if (_wbPublishedStampCache is not null && _wbPublishedStampMap == mapId) return _wbPublishedStampCache;
        var set = new HashSet<(int, int)>();
        try
        {
            if (_mpq?.ReadFile(@"WorldPacks\build.json") is { } bytes)
            {
                using var manifest = System.Text.Json.JsonDocument.Parse(bytes);
                if (manifest.RootElement.TryGetProperty("stamps", out var stamps))
                    foreach (var e in stamps.EnumerateArray())
                        if (e.GetString()?.Split(':', '_') is [var m, var c, var r] && uint.TryParse(m, out uint mm) && mm == mapId &&
                            int.TryParse(c, out int col) && int.TryParse(r, out int row))
                            set.Add((col, row));
            }
        }
        catch { }
        _wbPublishedStampMap = mapId;
        return _wbPublishedStampCache = set;
    }

    private WbStockTile? WbStockFor((int col, int row) key)
    {
        if (_wbStock.TryGetValue(key, out var cached)) return cached;
        WbStockTile? tile = null;
        if (_mpq is not null)
        {
            string path = $"World\\Maps\\{_config.Start.MapName}\\{_config.Start.MapName}_{key.col}_{key.row}.adt";
            // A pack map (Gilneas 800) has NO stock tile, and a continent tile a pack STAMPED over has the wrong
            // one (the replaced sea): the base is the mounted patch-7 tile minus the sculpt it was published with
            // (preview = base + the same sculpt = exactly the published ground: stamp, sculpt, stitch, paths).
            // "Stamped" = the PUBLISHED manifest says so (patch-7's build.json, always there) or a tile doc does.
            // 2026-09-27: the docs arrive AFTER the first sculpt state, so a doc-only test took the stock SEA
            // tile as the base and cached it: Keel Harbor showed flat at each chunk's base height + sculpt, 2-4
            // yd off the published ground, and tier 2 judged spawns against that ("fixed" two dockhands under it).
            bool packStamp = WbTileDoc(_config.Start.Map, key.col, key.row) is not null ||
                             WbPublishedStampedTiles((uint)_config.Start.Map).Contains(key);
            // A continent tile the docs may still claim: do not cache a stock guess before they have loaded.
            if (!packStamp && _wbDocs is null && _config.Start.Map < 800) return null;
            var bytes = packStamp ? null : _mpq.ReadFileExcluding(path, WbPatchName);
            float[]? published = null;
            // Only valid when the mounted patch IS the last publish (its baked sculpt = PublishedSculpt).
            bool mountedIsLatest = _wbMountedBuild is { } mb && mb == _wbState?.LastBuild?.BuildId;
            if (bytes is null && !mountedIsLatest)
                Console.WriteLine($"[worldbuilder] preview of {path} waits for the latest patch-7 (download it)");
            if (bytes is null && mountedIsLatest && _mpq.ReadFile(path) is { } patched)
            {
                bytes = patched;
                published = new float[WorldBuilderLaw.VertexCount];
                foreach (var t in _wbState?.PublishedSculpt.Where(t => t.Col == key.col && t.Row == key.row) ?? [])
                    foreach (var (i, dz) in t.Deltas)
                        if ((uint)i < (uint)published.Length) published[i] += dz;
            }
            var adt = bytes is null ? null : AdtTerrainReader.Parse(bytes, key.row, key.col);
            if (adt?.Chunks is { } chunks)
            {
                var map = new Dictionary<(int x, int y), (float[] Heights, sbyte[]? Normals, float BaseZ)>();
                foreach (var c in chunks)
                    if (c?.Heights is { } h)
                    {
                        var heights = (float[])h.Clone();
                        if (published is not null)
                        {
                            // Undo exactly what AdtDocument.ApplySculpt added: outer vertices their own delta,
                            // inner vertices the mean of their four corners.
                            for (int r = 0; r <= 8; r++)
                                for (int cc = 0; cc <= 8; cc++)
                                    heights[r * 17 + cc] -= published[(c.IndexY * 8 + r) * 129 + c.IndexX * 8 + cc];
                            for (int r = 0; r < 8; r++)
                                for (int cc = 0; cc < 8; cc++)
                                {
                                    int g = (c.IndexY * 8 + r) * 129 + c.IndexX * 8 + cc;
                                    heights[r * 17 + 9 + cc] -= (published[g] + published[g + 1] + published[g + 129] + published[g + 130]) * 0.25f;
                                }
                        }
                        map[(c.IndexX, c.IndexY)] = (heights, (sbyte[]?)c.Normals?.Clone(), c.BaseZ);
                    }
                tile = new WbStockTile { Chunks = map };
            }
            else if (_config.Start.Map >= 800 || packStamp)
                Console.WriteLine($"[worldbuilder] no preview base for {path}: sculpt on this tile shows only after publish + download");
        }
        _wbStock[key] = tile;
        return tile;
    }

    /// <summary>Rewrite the shared AdtCache instance of a tile to stock + deltas and rebuild its mesh.</summary>
    private void WbApplyTilePreview((int col, int row) key)
    {
        if (_adts is null || _terrain is null) return;
        var stock = WbStockFor(key);
        var adt = _adts.Get(key.col, key.row);
        if (stock is null || adt?.Chunks is null) return;
        var total = new float[WorldBuilderLaw.VertexCount];
        bool any = false;
        if (_wbServerSculpt.TryGetValue(key, out var s)) { for (int i = 0; i < total.Length; i++) total[i] += s[i]; any = true; }
        if (_wbStroke.TryGetValue(key, out var k)) { for (int i = 0; i < total.Length; i++) total[i] += k[i]; any = true; }

        // Absolute outer grid for normals.
        var outer = new float[WorldBuilderLaw.VertexCount];
        foreach (var c in adt.Chunks)
        {
            if (c?.Heights is null || !stock.Chunks.TryGetValue((c.IndexX, c.IndexY), out var st)) continue;
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    int g = (c.IndexY * 8 + r) * WorldBuilderLaw.Side + c.IndexX * 8 + cc;
                    outer[g] = c.BaseZ + st.Heights[r * 17 + cc] + total[g];
                }
        }

        foreach (var c in adt.Chunks)
        {
            if (c?.Heights is null || !stock.Chunks.TryGetValue((c.IndexX, c.IndexY), out var st)) continue;
            bool touched = false;
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    float d = total[(c.IndexY * 8 + r) * WorldBuilderLaw.Side + c.IndexX * 8 + cc];
                    c.Heights[r * 17 + cc] = st.Heights[r * 17 + cc] + d;
                    if (d != 0f) touched = true;
                }
            for (int r = 0; r < 8; r++)
                for (int cc = 0; cc < 8; cc++)
                {
                    float d = WorldBuilderLaw.InnerDelta(total, c.IndexY * 8 + r, c.IndexX * 8 + cc);
                    c.Heights[r * 17 + 9 + cc] = st.Heights[r * 17 + 9 + cc] + d;
                }
            if (c.Normals is null) continue;
            if (!touched && st.Normals is { } sn) { Array.Copy(sn, c.Normals, Math.Min(sn.Length, c.Normals.Length)); continue; }
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    int gr = c.IndexY * 8 + r, gc = c.IndexX * 8 + cc;
                    int r0 = Math.Max(gr - 1, 0), r1 = Math.Min(gr + 1, 128), c0 = Math.Max(gc - 1, 0), c1 = Math.Min(gc + 1, 128);
                    float dRow = (outer[r1 * 129 + gc] - outer[r0 * 129 + gc]) / ((r1 - r0) * WorldBuilderLaw.Unit);
                    float dCol = (outer[gr * 129 + c1] - outer[gr * 129 + c0]) / ((c1 - c0) * WorldBuilderLaw.Unit);
                    WbWriteNormal(c.Normals, r * 17 + cc, dRow, dCol);
                }
            for (int r = 0; r < 8; r++)
                for (int cc = 0; cc < 8; cc++)
                {
                    int gr = c.IndexY * 8 + r, gc = c.IndexX * 8 + cc;
                    float dRow = ((outer[(gr + 1) * 129 + gc] + outer[(gr + 1) * 129 + gc + 1]) - (outer[gr * 129 + gc] + outer[gr * 129 + gc + 1])) * 0.5f / WorldBuilderLaw.Unit;
                    float dCol = ((outer[gr * 129 + gc + 1] + outer[(gr + 1) * 129 + gc + 1]) - (outer[gr * 129 + gc] + outer[(gr + 1) * 129 + gc])) * 0.5f / WorldBuilderLaw.Unit;
                    WbWriteNormal(c.Normals, r * 17 + 9 + cc, dRow, dCol);
                }
        }
        if (any) _wbPreviewTiles.Add(key);
        _wbAppliedTo[key] = adt;
        _terrain.RebuildTile(key, adt);
    }

    private static void WbWriteNormal(sbyte[] normals, int vertex, float dRow, float dCol)
    {
        var (b0, b1, b2) = WorldBuilderLaw.EncodeNormal(dRow, dCol);
        if (vertex * 3 + 2 >= normals.Length) return;
        normals[vertex * 3] = b0; normals[vertex * 3 + 1] = b1; normals[vertex * 3 + 2] = b2;
    }

    /// <summary>A preview tile that streamed out and back in comes back as a fresh cache instance:
    /// re-apply. Also re-applies tiles whose server deltas changed on a state refresh.</summary>
    private void WbKeepPreviewApplied()
    {
        if (_adts is null || _terrain is null) return;
        foreach (var key in _wbPreviewTiles)
        {
            if (!_terrain.IsResident(key)) continue;
            if (!_adts.TryPeek(key.col, key.row, out var adt) || adt is null) continue;
            if (!_wbAppliedTo.TryGetValue(key, out var applied) || !ReferenceEquals(applied, adt))
                _wbDirtyTiles.Add(key);
        }
        if (!_wbStroking && _wbDirtyTiles.Count > 0)
        {
            foreach (var key in _wbDirtyTiles.ToArray()) WbApplyTilePreview(key);
            _wbDirtyTiles.Clear();
        }
    }

    // ── placement ────────────────────────────────────────────────────────────

    private static bool WbModelIsM2(string? path) =>
        path is not null && (path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase));

    private List<string> WbBuildCatalogue()
    {
        if (_mpq is null) return new();
        var list = new List<string>();
        foreach (var name in _mpq.ListedFiles())
        {
            if (name.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            {
                // Skip group files (Name_000.wmo).
                string stem = Path.GetFileNameWithoutExtension(name);
                int us = stem.LastIndexOf('_');
                if (us > 0 && stem.Length - us == 4 && stem[(us + 1)..].All(char.IsDigit)) continue;
                list.Add(name);
            }
            else if (name.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) &&
                     name.StartsWith("World\\", StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void WbPlaceFrame(ImGuiIOPtr io, bool overUi)
    {
        if (_wbModel is null || _wbCursor is not { } at) { WbSetCursorGhost(null); return; }
        var world = at + new Vector3(0, 0, _wbZOffset);
        var candidate = new WorldPackClient.Placement
        {
            MapId = _config.Start.Map,
            Kind = WbModelIsM2(_wbModel) ? "m2" : "wmo",
            ModelPath = _wbModel,
            PosX = world.X, PosY = world.Y, PosZ = world.Z,
            RotX = _wbPitch, RotY = _wbYaw, RotZ = _wbRoll,
            Scale = WbModelIsM2(_wbModel) ? _wbScale : 1f,
            DoodadSet = _wbDoodadSet,
        };
        WbSetCursorGhost(candidate);
        if (!overUi && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
            WbOp($"place {Path.GetFileName(_wbModel)}", _wbClient.PlaceAsync(SuiWebAppUrl, _wbPackId, candidate),
                r => { if (r.Result?.Placement is { } p) _wbSelected = p.Id; });
        }
    }

    private void WbSelectFrame(ImGuiIOPtr io, bool overUi)
    {
        if (overUi || _wbCursor is not { } at || !ImGui.IsMouseClicked(ImGuiMouseButton.Left) || _wbState is null) return;
        if (WbTrySpawnClick(at)) return;
        if (_wbMoveArmed && _wbState.Placements.FirstOrDefault(p => p.Id == _wbSelected) is { } sel)
        {
            _wbMoveArmed = false;
            sel.PosX = at.X; sel.PosY = at.Y; sel.PosZ = at.Z + _wbZOffset;
            WbOp($"move #{sel.Id}", _wbClient.MoveAsync(SuiWebAppUrl, sel));
            return;
        }
        var nearest = _wbState.Placements.Where(p => !p.Deleted)
            .OrderBy(p => Vector3.Distance(new Vector3(p.PosX, p.PosY, p.PosZ), at))
            .FirstOrDefault();
        if (nearest is not null && Vector3.Distance(new Vector3(nearest.PosX, nearest.PosY, nearest.PosZ), at) < 40f)
            _wbSelected = nearest.Id;
    }

    private static string WbSignature(WorldPackClient.Placement p) =>
        $"{p.ModelPath}|{p.PosX:F2}|{p.PosY:F2}|{p.PosZ:F2}|{p.RotX:F1}|{p.RotY:F1}|{p.RotZ:F1}|{p.Scale:F2}";

    private static Matrix4x4 WbTransform(WorldPackClient.Placement p)
    {
        var pos = WorldBuilderLaw.WorldToPlacement(new Vector3(p.PosX, p.PosY, p.PosZ));
        var rot = new Vector3(p.RotX, p.RotY, p.RotZ);
        return p.Kind == "m2" ? DoodadRenderer.MddfTransform(pos, rot, p.Scale) : WmoRenderer.ModfTransform(pos, rot);
    }

    /// <summary>true when the ghost is (now) on screen.</summary>
    private bool WbAddGhost(ulong key, WorldPackClient.Placement p)
    {
        var t = WbTransform(p);
        if (p.Kind == "m2")
        {
            string m2 = Path.ChangeExtension(p.ModelPath, ".m2");
            return _doodads?.AddDynamic(key, m2, t) == DoodadRenderer.DynamicPlacement.Placed;
        }
        return _wmo?.AddDynamic(key, p.ModelPath, t) == WmoRenderer.DynamicPlacement.Placed;
    }

    private void WbRemoveGhost(ulong key)
    {
        _wmo?.RemoveDynamic(key);
        _doodads?.RemoveDynamic(key);
    }

    private void WbSetCursorGhost(WorldPackClient.Placement? p)
    {
        if (p is null)
        {
            if (_wbCursorGhostSig is not null) WbRemoveGhost(WbCursorGhostKey);
            _wbCursorGhostSig = null;
            return;
        }
        string sig = WbSignature(p);
        if (sig == _wbCursorGhostSig) return;
        bool sameModel = _wbCursorGhostSig is not null && _wbCursorGhostSig.StartsWith(p.ModelPath + "|", StringComparison.Ordinal);
        if (sameModel)
        {
            bool moved = p.Kind == "m2"
                ? _doodads?.TryUpdateDynamicTransform(WbCursorGhostKey, WbTransform(p)) == true
                : _wmo?.TryUpdateDynamicTransform(WbCursorGhostKey, WbTransform(p)) == true;
            if (moved) { _wbCursorGhostSig = sig; return; }
        }
        WbRemoveGhost(WbCursorGhostKey);
        _wbCursorGhostSig = WbAddGhost(WbCursorGhostKey, p) ? sig : null;
    }

    /// <summary>Unpublished (or not-yet-downloaded) placements draw as dynamic models.</summary>
    private void WbSyncGhosts()
    {
        var want = new Dictionary<int, WorldPackClient.Placement>();
        if (_wbState is not null && _wbStateMap == _config.Start.Map && !_wbGhostsHidden)
        {
            var enabled = _wbState.Packs.Where(p => p.Enabled || p.Id == _wbPackId).Select(p => p.Id).ToHashSet();
            bool patchCurrent = _wbMountedBuild is { } b && _wbState.LastBuild?.BuildId == b;
            foreach (var p in _wbState.Placements)
                if (!p.Deleted && enabled.Contains(p.PackId) && !(p.Published && patchCurrent))
                    want[p.Id] = p;
        }
        foreach (var id in _wbGhosts.Keys.Where(id => !want.ContainsKey(id)).ToList())
        {
            WbRemoveGhost(WorldBuilderLaw.GhostKey(id));
            _wbGhosts.Remove(id);
        }
        foreach (var (id, p) in want)
        {
            string sig = WbSignature(p);
            if (_wbGhosts.TryGetValue(id, out var have) && have == sig) continue;
            if (WbAddGhost(WorldBuilderLaw.GhostKey(id), p)) _wbGhosts[id] = sig;
            else _wbGhosts.Remove(id);   // pending model: retried next frame
        }
    }

    private void WbDrawCursorOverlay()
    {
        if (_wbCursor is not { } at || _terrain is null) return;
        var draw = ImGui.GetForegroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        uint colour = _wbTool == WorldBuilderTool.Sculpt ? 0xE040D0FFu : 0xE0FFD040u;
        float radius = _wbTool == WorldBuilderTool.Sculpt ? _wbRadius : _wbSpawnArmed && _wbSpawnMode == 1 ? _wbPackRadius : 2f;
        // The patrol route being clicked out: markers joined in order, closed back to the first (waypoints loop).
        if (_wbSpawnArmed && _wbSpawnMode == 2 && _wbPatrolPoints.Count > 0)
        {
            var route = _wbPatrolPoints.Select(p => cam.TryProjectToScreen(p + new Vector3(0, 0, 0.5f), display, out var s, out _) ? s : (Vector2?)null).ToList();
            for (int i = 0; i < route.Count; i++)
            {
                if (route[i] is not { } a0) continue;
                draw.AddCircleFilled(a0, 5f, i == 0 ? 0xE040FF40u : 0xE0FFD040u);
                if (route.Count > 1 && route[(i + 1) % route.Count] is { } b0) draw.AddLine(a0, b0, i + 1 == route.Count ? 0x80FFD040u : 0xE0FFD040u, 2f);
            }
        }
        Vector2? prev = null;
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.Tau;
            float x = at.X + MathF.Cos(a) * radius, y = at.Y + MathF.Sin(a) * radius;
            float z = _terrain.SampleHeight(x, y) ?? at.Z;
            Vector2? px = cam.TryProjectToScreen(new Vector3(x, y, z + 0.3f), display, out var pixel, out _) ? pixel : null;
            if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, colour, 2f);
            prev = px;
        }
        string hint = _wbTool switch
        {
            WorldBuilderTool.Sculpt => $"{_wbBrush}  r {_wbRadius:F0}  (hold LMB, Shift inverts, [ ] size)",
            WorldBuilderTool.Place => $"{Path.GetFileNameWithoutExtension(_wbModel ?? "pick a model")}  heading {_wbYaw:F0}  (LMB place, [ ] rotate)",
            _ => _wbMoveArmed ? "click the new spot"
                : _wbSpawnArmed ? _wbSpawnMode switch
                {
                    1 => $"click: linked pack of {_wbPackCount}",
                    2 => $"click: patrol point {_wbPatrolPoints.Count + 1} (Save patrol in the panel)",
                    _ => "click: spawn here",
                }
                : "click near a placement to select it",
        };
        var mouse = ImGui.GetIO().MousePos + new Vector2(16f, 18f);
        draw.AddText(mouse + Vector2.One, 0xD0000000, hint);
        draw.AddText(mouse, colour, hint);
    }

    // ── web ──────────────────────────────────────────────────────────────────

    private void WbRequestState()
    {
        if (SuiWebAppUrl.Length == 0 || _wbStateTask is not null) return;
        int map = _config.Start.Map;
        _wbStateTask = _wbClient.GetStateAsync(SuiWebAppUrl, map, _wbPackId);
    }

    private void WbOp(string label, Task<WorldPackClient.Reply> task, Action<WorldPackClient.Reply>? then = null)
    {
        _wbOps.Add((label, then is null ? task : task.ContinueWith(t =>
        {
            var r = t.GetAwaiter().GetResult();
            if (r.Success) _wbPendingThen.Enqueue(() => then(r));
            return r;
        })));
        _wbMessage = $"saving: {label}...";
    }

    private readonly Queue<Action> _wbPendingThen = new();

    private void WbUndo()
    {
        if (_wbPackId == 0 || _wbStroking) return;
        WbOp("undo", _wbClient.UndoAsync(SuiWebAppUrl, _wbPackId));
    }

    private void PumpWbTasks()
    {
        while (_wbPendingThen.Count > 0) _wbPendingThen.Dequeue()();

        bool anyDone = false;
        for (int i = _wbOps.Count - 1; i >= 0; i--)
        {
            var (label, task) = _wbOps[i];
            if (!task.IsCompleted) continue;
            _wbOps.RemoveAt(i);
            anyDone = true;
            if (task.IsFaulted || !task.Result.Success)
            {
                _wbMessage = task.IsFaulted ? $"{label}: {task.Exception?.GetBaseException().Message}" : $"{label}: {task.Result.Error}";
                // A script must never lose a rejected op silently (it would run on as if saved).
                if (WbScriptPath is not null) Console.WriteLine($"[wbscript] ERROR op rejected: {_wbMessage}");
            }
            else
            {
                var r = task.Result.Result;
                _wbMessage = r?.UndoneOpId is { } u ? $"undid op #{u} ({r.UndoneKind}) - audit #{r.AuditId}"
                           : r is not null ? $"{label}: saved as op #{r.OpId}, audit #{r.AuditId}"
                           : $"{label}: ok";
            }
        }
        if (anyDone) { _wbStateTask = null; WbRequestState(); }

        if (_wbStateTask is { IsCompleted: true } st)
        {
            _wbStateTask = null;
            if (st.IsFaulted) _wbMessage = "web app: " + st.Exception?.GetBaseException().Message;
            else if (!st.Result.Success) _wbMessage = "web app: " + st.Result.Error;
            else WbAcceptState(st.Result);
        }

        // Build status: poll every 2 s while a build runs (and once after a Publish click).
        double now = ImGui.GetTime();
        if (_creatorPanel == CreatorPanel.World && SuiWebAppUrl.Length > 0 && _wbStatusTask is null &&
            (now - _wbStatusAt > (_wbStatus?.Running == true ? 2.0 : 15.0)))
        {
            _wbStatusAt = now;
            _wbStatusTask = _wbClient.GetStatusAsync(SuiWebAppUrl);
        }
        if (_wbStatusTask is { IsCompleted: true } bt)
        {
            _wbStatusTask = null;
            if (!bt.IsFaulted)
            {
                bool wasRunning = _wbStatus?.Running == true;
                _wbStatus = bt.Result;
                if (wasRunning && !_wbStatus.Running)
                {
                    _wbMessage = $"build #{_wbStatus.BuildId} {_wbStatus.Status}" + (_wbStatus.Error is { } e ? $": {e}" : "");
                    WbRequestState();
                }
            }
        }

        PumpWbCollisionSync();
        if (_wbDownloadTask is { IsCompleted: true } dl)
        {
            _wbDownloadTask = null;
            WbFinishDownload(dl);
        }
    }

    private void WbAcceptState(WorldPackClient.State s)
    {
        bool mapChanged = s.MapId != _wbStateMap;
        _wbState = s;
        _wbStateMap = s.MapId;
        if (_wbPackId == 0 && s.Packs.Count > 0) _wbPackId = s.Packs[0].Id;
        if (mapChanged) { _wbStock.Clear(); _wbAppliedTo.Clear(); _wbPreviewTiles.Clear(); }
        // A pack map's preview base is derived from the published sculpt: a new publish invalidates it.
        if (s.LastBuild?.BuildId != _wbStockBuild) { _wbStockBuild = s.LastBuild?.BuildId; _wbStock.Clear(); _wbAppliedTo.Clear(); }

        var previous = _wbServerSculpt.Keys.ToHashSet();
        _wbServerSculpt.Clear();
        foreach (var t in s.Sculpt)
        {
            var grid = new float[WorldBuilderLaw.VertexCount];
            foreach (var (i, d) in t.Deltas) if ((uint)i < (uint)grid.Length) grid[i] = d;
            _wbServerSculpt[(t.Col, t.Row)] = grid;
        }
        // Every tile that has or had deltas (incl. published ones) is re-derived from stock.
        foreach (var key in previous.Concat(_wbServerSculpt.Keys).Concat(s.PublishedSculpt.Select(t => (t.Col, t.Row))))
        {
            _wbPreviewTiles.Add(key);
            _wbDirtyTiles.Add(key);
        }
        _wbAppliedTo.Clear();
    }

    private void WbReadMountedBuild()
    {
        if (_wbMountedRead || _mpq is null) return;
        _wbMountedRead = true;
        _wbMountedBuild = null;
        if (!_mpq.IsMounted(WbPatchName)) return;
        var hit = _mpq.ReadFileFromSupplier("WorldPacks\\build.json", WbPatchName);
        if (hit is null) return;
        try
        {
            using var doc = JsonDocument.Parse(hit.Value.Data);
            if (doc.RootElement.TryGetProperty("buildId", out var id)) _wbMountedBuild = id.GetInt32();
        }
        catch { }
    }

    private void WbStartDownload()
    {
        if (_mpq is null) return;
        // Close our handle first so the file can be replaced on disk (Windows locks open files).
        _mpq.ReplaceArchive(WbPatchName, null);
        string dest = Path.Combine(_config.ClientDataPath, WbPatchName);
        _wbDownloadTask = _wbClient.DownloadPatchAsync(SuiWebAppUrl, dest);
        _wbMessage = "downloading patch-7.MPQ...";
    }

    private void WbFinishDownload(Task<long> dl)
    {
        string dest = Path.Combine(_config.ClientDataPath, WbPatchName);
        if (dl.IsFaulted)
        {
            _wbMessage = "download failed: " + dl.Exception?.GetBaseException().Message;
            if (File.Exists(dest)) _mpq?.ReplaceArchive(WbPatchName, dest);   // keep the old one mounted
            _wbMountedRead = false;
            return;
        }
        bool ok = _mpq?.ReplaceArchive(WbPatchName, dest) == true;
        _wbMountedRead = false;
        _wbPublishedStampCache = null;   // the new build's manifest may stamp other tiles
        WbReadMountedBuild();
        _wbMessage = ok ? $"mounted {WbPatchName} ({dl.Result / 1024} KiB), build #{_wbMountedBuild} - reloading the world"
                        : $"{WbPatchName} downloaded but could not be mounted";
        if (ok) WbReloadWorld();
        // patch-7 is only what the client renders: MSUIClient's live collision reads the server-format
        // vmaps, so pull what the packs installed too (a player otherwise falls through pack buildings).
        // The same sidecar the startup sync reads: this client now holds build N.
        if (ok && _wbMountedBuild is { } mounted)
            WorldPackStartupSync.WriteLocal(_config.ClientDataPath, mounted,
                _wbState?.LastBuild is { } lb && lb.BuildId == mounted ? lb.Sha1 : null);
        if (ok && _wbCollisionTask is null)
            _wbCollisionTask = WorldPackCollisionSync.SyncAsync(_wbClient, SuiWebAppUrl, _wbMountedBuild,
                WbServerDataDir(_config.VmapPath, "vmaps"), WbServerDataDir(_config.MmapPath, "mmaps"));
    }

    /// <summary>A configured vmaps/mmaps folder (relative to the repo root), or GameData\{name}.</summary>
    private string WbServerDataDir(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured) ? configured : Path.Combine(_config.RepoRoot, configured);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_config.ClientDataPath))!, name);
    }

    private void PumpWbCollisionSync()
    {
        if (_wbCollisionTask is not { IsCompleted: true } t) return;
        _wbCollisionTask = null;
        _wbMessage = t.IsFaulted ? "collision sync failed: " + t.Exception?.GetBaseException().Message : t.Result.Summary;
        Console.WriteLine($"[worldbuilder] {_wbMessage}");
    }

    /// <summary>Tear down and re-stream the current map at the current spot, so terrain, WMO/M2
    /// placements and collision all come from the freshly mounted archive.</summary>
    private void WbReloadWorld()
    {
        _worldMapReloadPending = true;
        if (_controller is null) return;
        foreach (var id in _wbGhosts.Keys.ToList()) WbRemoveGhost(WorldBuilderLaw.GhostKey(id));
        _wbGhosts.Clear();
        WbSetCursorGhost(null);
        _wbStock.Clear();
        _wbAppliedTo.Clear();
        // Map.dbc / WDTs may have changed with the archive (pack maps): re-read them.
        ResetInstanceData();
        EnsureInstanceData();
        var row = _maps?.Get(_config.Start.Map);
        var wdt = row is null ? null : _mapWdts?.GetValueOrDefault(_config.Start.Map);
        if (row is null || wdt is null) { _wbMessage += " (reload refused: no map row)"; return; }
        var at = _controller.Position;
        // TravelTo's SetMap is a no-op on the same map; the cached parses predate the mount.
        _adts?.Invalidate();
        TravelTo(row, wdt, at, _controller.Yaw, recordReturn: false);

        foreach (var key in _wbServerSculpt.Keys) { _wbPreviewTiles.Add(key); _wbDirtyTiles.Add(key); }
    }
}
