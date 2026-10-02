using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _wbNpcEditEntry, _wbNpcEditSpawn, _wbNpcStockSourceEntry;
    private int _wbNpcEditPack;
    private JsonArray _wbNpcSource = new();
    private JsonObject? _wbNpcTemplate, _wbNpcSpawnBody;
    private readonly Dictionary<uint, JsonObject> _wbNpcVendorRows = new();
    private string _wbNpcOriginalGreeting = "";
    private uint _wbNpcOriginalFlags;
    private int _wbNpcOriginalMain, _wbNpcOriginalOff;
    private bool _wbNpcSaving, _wbNpcMoveArmed;
    private Vector3 _wbNpcSpawnPosition;
    private float _wbNpcSpawnFacing;
    private Task<string>? _wbNpcLoadTask, _wbNpcNearbyTask;
    private Task<WorldPackClient.Reply>? _wbNpcSaveTask;
    private JsonArray? _wbNpcHydrateSource;
    private bool _wbNpcLoadFailed;
    private string _wbNpcCopyBlock = "";
    private string _wbNpcLoadedFormSignature = "";
    private JsonObject _wbNpcLoadedFields = new();
    private Vector3 _wbNpcLoadedSpawnPosition;
    private float _wbNpcLoadedSpawnFacing;
    private int _wbNpcNearbyMap = -1, _wbNpcRequestMap;
    private Vector2 _wbNpcNearbyCentre;
    private JsonArray _wbNpcNearby = new();
    private string _wbNpcNearbyError = "";
    private readonly byte[] _wbNpcBrowseSearch = new byte[96];
    private bool _wbNpcShowPreviews = true, _wbNpcShowNames = true;
    private bool _wbNpcBrowserOpen = true;
    private int _wbNpcPreviewRevision;
    private readonly Dictionary<ulong, string> _wbNpcPreviewSignatures = new();
    private readonly Dictionary<ulong, string> _wbNpcPreviewNames = new();
    private bool WbNpcHasActiveAction => _wbSpawnArmed || _wbNpcMoveArmed;

    private static float WbNpcFloat(JsonNode? n, float fallback = 0) =>
        float.TryParse(n?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;
    private static uint WbNpcUInt(JsonNode? n) => uint.TryParse(n?.ToString(), out var v) ? v : 0;
    private static void WbNpcText(byte[] buffer, string value)
    {
        Array.Clear(buffer);
        Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), buffer.AsSpan(0, buffer.Length - 1), true, out _, out _, out _);
    }
    private IEnumerable<JsonObject> WbNpcPackDocs(string kind, int? pack = null) =>
        (_wbDocs ?? new JsonArray()).OfType<JsonObject>().Where(d => d["kind"]?.ToString() == kind &&
            (pack is null || (int?)d["packId"] == pack) && d["body"] is JsonObject);
    private IEnumerable<JsonObject> WbNpcSourceRows(string kind) =>
        _wbNpcSource.OfType<JsonObject>().Where(d => d["kind"]?.ToString() == kind && d["body"] is JsonObject).Select(d => d["body"]!.AsObject());

    private string WbNpcOtherPackMessage(int packId)
    {
        var owner = _wbState?.Packs.FirstOrDefault(p => p.Id == packId);
        string name = owner?.Name ?? $"pack #{packId}";
        string label = name + (owner is { Enabled: false } ? " (draft only)" : "");
        string state = owner is { Enabled: false }
            ? $"This NPC has a saved draft in \"{name}\". Disabling that pack keeps its drafts."
            : $"This NPC has a pack version in \"{name}\".";
        return state + $" Choose \"{label}\" in the pack selector at the top of World to edit that version.";
    }

    private void WbCancelNpcPlacement()
    {
        _wbSpawnArmed = false;
        _wbNpcMoveArmed = false;
        _wbPatrolPoints.Clear();
    }

    private void WbNewNpc()
    {
        if (_wbNpcSaving) return;
        _wbNpcBrowserOpen = false;
        _wbNpcLoadTask = null; _wbNpcHydrateSource = null; _wbNpcLoadFailed = false;
        _wbNpcCopyBlock = "";
        WbCancelNpcPlacement();
        _wbNpcEditEntry = _wbNpcEditSpawn = _wbNpcStockSourceEntry = 0;
        _wbNpcEditPack = _wbPackId;
        _wbNpcTemplate = _wbNpcSpawnBody = null;
        _wbNpcSource = new();
        _wbNpcVendor.Clear(); _wbNpcVendorRows.Clear();
        _wbNpcGossip = ""; _wbNpcFlags = 1; _wbNpcCopyTrainer = 0;
        _wbNpcMainHand = _wbNpcOffHand = 0;
        _wbNpcDisplay = 1141; _wbNpcScale = 1; _wbNpcFaction = 35;
        _wbNpcLevel = _wbNpcLevelMax = 30; _wbNpcRank = 0; _wbNpcType = 7;
        _wbNpcTrainerType = _wbNpcTrainerClass = 0;
        WbNpcText(_wbNpcName, ""); WbNpcText(_wbNpcSub, "");
    }

    private void DrawWbNpcBrowser()
    {
        if (_wbDocs is null && _wbDocsTask is null) WbRequestDocs();
        if (_wbNpcEditPack != 0 && _wbNpcEditPack != _wbPackId && !_wbNpcSaving) WbNewNpc();
        if (CreatorButton(_wbNpcBrowserOpen ? "Close NPC finder" : "Find / change NPC")) _wbNpcBrowserOpen = !_wbNpcBrowserOpen;
        ImGui.SameLine();
        if (CreatorButton("New NPC")) WbNewNpc();
        bool viewOpen = ImGui.TreeNode("NPC view options"); ObserveWorldBuilderUiItem("NPC view options");
        if (viewOpen)
        {
            ImGui.Checkbox("Show NPCs in world", ref _wbNpcShowPreviews);
            ImGui.SameLine(); ImGui.Checkbox("Names", ref _wbNpcShowNames);
            if (CreatorButton("Refresh nearby NPCs")) { WbRequestDocs(); WbRequestNearbyNpcs(); }
            ImGui.TreePop();
        }
        if (_wbNpcBrowserOpen)
        {
            ImGui.TextWrapped("Click an NPC in the world, or find it below.");
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.InputText("Find NPC##wb-existing", _wbNpcBrowseSearch, (uint)_wbNpcBrowseSearch.Length);
            ObserveWorldBuilderUiItem("NPC search");
            string q = WbText(_wbNpcBrowseSearch);
            bool Match(string name, uint id) => q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase) || id.ToString().Contains(q);
            if (ImGui.BeginListBox("##wb-existing-npcs", new Vector2(-1, 145 * CreatorUiScale)))
            {
                var templates = WbNpcPackDocs("dbrow:creature_template", _wbPackId).ToList();
                foreach (var d in templates)
                {
                    var t = d["body"]!; uint entry = WbNpcUInt(t["entry"]);
                    string name = t["name"]?.ToString() ?? "NPC";
                    if (!Match(name, entry)) continue;
                    if (ImGui.Selectable($"{name}  — pack##wb-edit-{entry}", _wbNpcEditEntry == entry)) WbLoadPackNpc(entry, 0);
                    ObserveWorldBuilderUiItem(name);
                }
                var authoredSpawns = WbNpcPackDocs("dbrow:creature").Select(d => WbNpcUInt(d["body"]?["guid"])).ToHashSet();
                foreach (var n in _wbNpcNearby.OfType<JsonObject>())
                {
                    var s = n["spawn"]!; var t = n["template"]!;
                    uint guid = WbNpcUInt(s["guid"]), entry = WbNpcUInt(s["id"]);
                    if (authoredSpawns.Contains(guid)) continue;
                    string name = t["name"]?.ToString() ?? "NPC";
                    if (!Match(name, entry)) continue;
                    float distance = _controller is null ? 0 : Vector2.Distance(new(_controller.Position.X, _controller.Position.Y), new(WbNpcFloat(s["position_x"]), WbNpcFloat(s["position_y"])));
                    if (ImGui.Selectable($"{name}  — {distance:F0} yd##wb-stock-{guid}", _wbNpcEditSpawn == guid)) WbLoadStockNpc(n);
                    ObserveWorldBuilderUiItem(name);
                }
                ImGui.EndListBox();
            }
        }
        if (_wbNpcNearbyTask is not null || _wbNpcLoadTask is not null) ImGui.TextDisabled("Loading NPCs and services...");
        if (_wbNpcNearbyError.Length > 0) ImGui.TextWrapped(_wbNpcNearbyError);
        if (_wbNpcLoadFailed) ImGui.TextWrapped("NPC services could not be loaded. Select the NPC again to retry before saving.");
        if (_wbNpcCopyBlock.Length > 0) ImGui.TextWrapped(_wbNpcCopyBlock);
        if (_wbNpcStockSourceEntry != 0)
            ImGui.TextWrapped("This is an existing world NPC. Saving creates a pack version for this spawn only; disabling the pack restores the original.");
        else if (_wbNpcEditEntry != 0)
            ImGui.TextWrapped("Editing " + (_wbNpcTemplate?["name"]?.ToString() ?? "NPC"));
        ImGui.Separator();
    }

    private void WbRequestNearbyNpcs()
    {
        if (_wbNpcNearbyTask is not null || SuiWebAppUrl.Length == 0 || _controller is null) return;
        _wbNpcRequestMap = _config.Start.Map;
        if (_wbNpcNearbyMap != _wbNpcRequestMap) _wbNpcNearby = new();
        _wbNpcNearbyCentre = new(_controller.Position.X, _controller.Position.Y);
        _wbNpcNearbyTask = _wbClient.NpcsNearAsync(SuiWebAppUrl, _wbNpcRequestMap, _wbNpcNearbyCentre.X, _wbNpcNearbyCentre.Y);
    }

    private void PumpWbNpcTasks()
    {
        if (_wbNpcSaveTask is { IsCompleted: true }) { _wbNpcSaveTask = null; _wbNpcSaving = false; }
        if (_wbNpcNearbyTask is { IsCompleted: true } nearby)
        {
            _wbNpcNearbyTask = null;
            try
            {
                _wbNpcNearby = JsonNode.Parse(nearby.GetAwaiter().GetResult())?["npcs"] as JsonArray ?? new();
                _wbNpcNearbyMap = _wbNpcRequestMap; _wbNpcNearbyError = ""; _wbNpcPreviewRevision++;
            }
            catch (Exception ex) { _wbNpcNearbyError = "Nearby NPCs: " + ex.Message; _wbNpcNearbyMap = _wbNpcRequestMap; }
        }
        if (_wbNpcLoadTask is { IsCompleted: true } load)
        {
            _wbNpcLoadTask = null;
            try
            {
                var reply = JsonNode.Parse(load.GetAwaiter().GetResult());
                var source = reply?["docs"] as JsonArray ?? throw new InvalidOperationException("NPC content is missing.");
                _wbNpcCopyBlock = _wbNpcStockSourceEntry == 0 ? "" : string.Join(" ", (reply?["copyLimitations"] as JsonArray ?? new()).Select(n => n?.ToString()));
                if (_wbNpcHydrateSource is { } authored)
                {
                    var borrowed = new JsonArray(source.OfType<JsonObject>().Where(d => d["kind"]?.ToString() is
                        "dbrow:creature_equip_template" or "dbrow:gossip_menu" or "dbrow:gossip_menu_option" or "dbrow:npc_text" or "dbrow:broadcast_text").Select(d => d.DeepClone()).ToArray());
                    source = NpcAuthoringLaw.ApplyChanges(borrowed, authored);
                }
                _wbNpcHydrateSource = null; _wbNpcLoadFailed = false;
                WbLoadNpcForm(source);
            }
            catch (Exception ex) { _wbMessage = "NPC: " + ex.Message; _wbNpcLoadFailed = true; }
        }
    }

    private void WbLoadPackNpc(uint entry, uint spawn)
    {
        if (_wbNpcSaving || _wbNpcLoadTask is not null) return;
        var template = WbNpcPackDocs("dbrow:creature_template").FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == entry);
        if (template is null) return;
        int pack = (int?)template["packId"] ?? 0;
        if (pack != _wbPackId) { _wbMessage = WbNpcOtherPackMessage(pack); return; }
        _wbMessage = "";
        _wbNpcBrowserOpen = false;
        WbCancelNpcPlacement();
        _wbNpcLoadFailed = false; _wbNpcHydrateSource = null;
        _wbNpcCopyBlock = "";
        _wbNpcEditEntry = entry; _wbNpcEditSpawn = spawn; _wbNpcStockSourceEntry = 0; _wbNpcEditPack = pack;
        _wbNpcSpawnBody = WbNpcPackDocs("dbrow:creature", pack).FirstOrDefault(d => WbNpcUInt(d["body"]?["guid"]) == spawn)?["body"]?.DeepClone().AsObject();
        var all = (_wbDocs ?? new()).OfType<JsonObject>().Where(d => (int?)d["packId"] == pack).ToList();
        var t = template["body"]!.AsObject();
        uint menu = WbNpcUInt(t["gossip_menu_id"]), equip = WbNpcUInt(t["equipment_id"]);
        var texts = all.Where(d => d["kind"]?.ToString() == "dbrow:gossip_menu" && WbNpcUInt(d["body"]?["entry"]) == menu).Select(d => WbNpcUInt(d["body"]?["text_id"])).ToHashSet();
        var broadcasts = all.Where(d => d["kind"]?.ToString() == "dbrow:npc_text" && texts.Contains(WbNpcUInt(d["body"]?["ID"]))).SelectMany(d => Enumerable.Range(0, 8).Select(i => WbNpcUInt(d["body"]?["BroadcastTextID" + i]))).ToHashSet();
        var selected = all.Where(d => d["kind"]?.ToString() switch
        {
            "dbrow:creature_template" or "dbrow:npc_vendor" or "dbrow:npc_trainer" => WbNpcUInt(d["body"]?["entry"]) == entry,
            "dbrow:creature_questrelation" or "dbrow:creature_involvedrelation" => WbNpcUInt(d["body"]?["id"]) == entry,
            "dbrow:creature_equip_template" => equip > 0 && WbNpcUInt(d["body"]?["entry"]) == equip,
            "dbrow:gossip_menu" => menu > 0 && WbNpcUInt(d["body"]?["entry"]) == menu,
            "dbrow:gossip_menu_option" => menu > 0 && WbNpcUInt(d["body"]?["menu_id"]) == menu,
            "dbrow:npc_text" => texts.Contains(WbNpcUInt(d["body"]?["ID"])),
            "dbrow:broadcast_text" => broadcasts.Contains(WbNpcUInt(d["body"]?["entry"])), _ => false,
        });
        WbLoadNpcForm(new JsonArray(selected.Select(d => d.DeepClone()).ToArray()));
        var replacement = WbNpcPackDocs("npc-replacement", pack).FirstOrDefault(d => WbNpcUInt(d["body"]?["replacementEntry"]) == entry);
        bool missingTexts = texts.Any(id => !all.Any(d => d["kind"]?.ToString() == "dbrow:npc_text" && WbNpcUInt(d["body"]?["ID"]) == id));
        if (replacement is not null && (equip is > 0 and < WbTemplateBase || menu is > 0 and < 62_000 || missingTexts))
        {
            _wbNpcHydrateSource = _wbNpcSource;
            _wbNpcLoadTask = _wbClient.NpcAsync(SuiWebAppUrl, WbNpcUInt(replacement["body"]?["originalEntry"]));
        }
        WbOpenPage("npcs");
    }

    private void WbLoadStockNpc(JsonObject npc)
    {
        if (_wbNpcSaving || _wbNpcLoadTask is not null) return;
        uint guid = WbNpcUInt(npc["spawn"]?["guid"]), entry = WbNpcUInt(npc["spawn"]?["id"]);
        var replacement = WbNpcPackDocs("npc-replacement").FirstOrDefault(d => WbNpcUInt(d["body"]?["spawnGuid"]) == guid);
        if (replacement is not null)
        {
            if ((int?)replacement["packId"] != _wbPackId) { _wbMessage = WbNpcOtherPackMessage((int?)replacement["packId"] ?? 0); return; }
            WbLoadPackNpc(WbNpcUInt(replacement["body"]?["replacementEntry"]), 0); return;
        }
        if (entry >= WbTemplateBase || guid >= WbSpawnBase) { _wbMessage = "Choose the pack that owns this NPC before editing it."; return; }
        _wbMessage = "";
        _wbNpcBrowserOpen = false;
        WbCancelNpcPlacement();
        _wbNpcLoadFailed = false; _wbNpcHydrateSource = null;
        _wbNpcCopyBlock = "";
        _wbNpcEditEntry = _wbNpcStockSourceEntry = entry; _wbNpcEditSpawn = guid; _wbNpcEditPack = _wbPackId;
        _wbNpcSpawnBody = npc["spawn"]!.DeepClone().AsObject();
        WbLoadNpcForm(new JsonArray(new JsonObject { ["kind"] = "dbrow:creature_template", ["body"] = npc["template"]!.DeepClone() }));
        _wbNpcLoadTask = _wbClient.NpcAsync(SuiWebAppUrl, entry);
        WbOpenPage("npcs");
    }

    private void WbLoadNpcForm(JsonArray source)
    {
        _wbNpcSource = (JsonArray)source.DeepClone();
        _wbNpcTemplate = WbNpcSourceRows("dbrow:creature_template").First().DeepClone().AsObject();
        var t = _wbNpcTemplate;
        WbNpcText(_wbNpcName, t["name"]?.ToString() ?? ""); WbNpcText(_wbNpcSub, t["subname"]?.ToString() ?? "");
        _wbNpcDisplay = WbNpcUInt(t["display_id1"]); _wbNpcScale = WbNpcFloat(t["display_scale1"], 1);
        _wbNpcLevel = (int)WbNpcUInt(t["level_min"]); _wbNpcLevelMax = (int)WbNpcUInt(t["level_max"]);
        _wbNpcFaction = (int)WbNpcUInt(t["faction"]); _wbNpcRank = (int)WbNpcUInt(t["rank"]); _wbNpcType = (int)WbNpcUInt(t["type"]);
        _wbNpcFlags = _wbNpcOriginalFlags = WbNpcUInt(t["npc_flags"]);
        _wbNpcTrainerType = (int)WbNpcUInt(t["trainer_type"]); _wbNpcTrainerClass = (int)WbNpcUInt(t["trainer_class"]); _wbNpcCopyTrainer = 0;
        var equip = WbNpcSourceRows("dbrow:creature_equip_template").FirstOrDefault(e => WbNpcUInt(e["entry"]) == WbNpcUInt(t["equipment_id"]));
        _wbNpcMainHand = _wbNpcOriginalMain = (int)WbNpcUInt(equip?["item1"]); _wbNpcOffHand = _wbNpcOriginalOff = (int)WbNpcUInt(equip?["item2"]);
        var menu = WbNpcSourceRows("dbrow:gossip_menu").FirstOrDefault(m => WbNpcUInt(m["entry"]) == WbNpcUInt(t["gossip_menu_id"]));
        var text = WbNpcSourceRows("dbrow:npc_text").FirstOrDefault(n => WbNpcUInt(n["ID"]) == WbNpcUInt(menu?["text_id"]));
        var broadcast = WbNpcSourceRows("dbrow:broadcast_text").FirstOrDefault(b => WbNpcUInt(b["entry"]) == WbNpcUInt(text?["BroadcastTextID0"]));
        _wbNpcGossip = _wbNpcOriginalGreeting = broadcast?["male_text"]?.ToString() ?? text?["Text0_0"]?.ToString() ?? "";
        _wbNpcVendor.Clear(); _wbNpcVendorRows.Clear();
        foreach (var row in WbNpcSourceRows("dbrow:npc_vendor"))
        {
            uint item = WbNpcUInt(row["item"]);
            if (item == 0 || _wbNpcVendorRows.ContainsKey(item)) continue;
            _wbNpcVendor.Add((item, WbItemName(item) + $" ({item})"));
            _wbNpcVendorRows[item] = row.DeepClone().AsObject();
        }
        if (_wbNpcSpawnBody is { } s)
        {
            _wbNpcSpawnPosition = new(WbNpcFloat(s["position_x"]), WbNpcFloat(s["position_y"]), WbNpcFloat(s["position_z"]));
            _wbNpcSpawnFacing = WbNpcFloat(s["orientation"]) * 180 / MathF.PI;
        }
        _wbNpcLoadedFormSignature = WbNpcFormSignature();
        _wbNpcLoadedFields = WbNpcEditableFields();
        _wbNpcLoadedSpawnPosition = _wbNpcSpawnPosition; _wbNpcLoadedSpawnFacing = _wbNpcSpawnFacing;
    }

    private JsonObject WbNpcEditableFields() => new()
    {
        ["name"] = WbText(_wbNpcName), ["subname"] = WbText(_wbNpcSub), ["level_min"] = _wbNpcLevel, ["level_max"] = _wbNpcLevelMax,
        ["faction"] = _wbNpcFaction, ["rank"] = _wbNpcRank, ["type"] = _wbNpcType, ["display_id1"] = _wbNpcDisplay,
        ["display_scale1"] = Math.Clamp(_wbNpcScale, .05f, 20), ["trainer_type"] = _wbNpcTrainerType, ["trainer_class"] = _wbNpcTrainerClass,
    };

    private string WbNpcFormSignature() => string.Join("|", WbText(_wbNpcName), WbText(_wbNpcSub), _wbNpcDisplay,
        _wbNpcScale, _wbNpcLevel, _wbNpcLevelMax, _wbNpcFaction, _wbNpcRank, _wbNpcType, _wbNpcFlags,
        _wbNpcTrainerType, _wbNpcTrainerClass, _wbNpcCopyTrainer, _wbNpcMainHand, _wbNpcOffHand, _wbNpcGossip,
        _wbNpcSpawnPosition, _wbNpcSpawnFacing, string.Join(',', _wbNpcVendor.Select(v => v.Item)),
        string.Join(',', _wbNpcVendorRows.OrderBy(v => v.Key).Select(v => v.Value.ToJsonString())));

    private void WbRefreshUnchangedNpcForm()
    {
        if (_wbNpcEditEntry != 0 && _wbNpcStockSourceEntry == 0 && !_wbNpcSaving && _wbNpcLoadTask is null &&
            _wbNpcEditPack == _wbPackId && WbNpcFormSignature() == _wbNpcLoadedFormSignature)
            WbLoadPackNpc(_wbNpcEditEntry, _wbNpcEditSpawn);
    }

    private void DrawWbVendorStock(uint item)
    {
        if (!ImGui.TreeNode("Stock and restock")) return;
        if (!_wbNpcVendorRows.TryGetValue(item, out var row)) _wbNpcVendorRows[item] = row = new JsonObject();
        int count = (int)WbNpcUInt(row["maxcount"]), seconds = (int)WbNpcUInt(row["incrtime"]);
        if (ImGui.InputInt("Stock (0 = unlimited)", ref count)) row["maxcount"] = Math.Max(0, count);
        if (ImGui.InputInt("Restock seconds", ref seconds)) row["incrtime"] = Math.Max(0, seconds);
        ImGui.TreePop();
    }

    private void DrawWbNpcSpawnEditor()
    {
        if (_wbNpcSpawnBody is null)
        {
            var spawns = WbNpcPackDocs("dbrow:creature", _wbPackId).Where(d => WbNpcUInt(d["body"]?["id"]) == _wbNpcEditEntry).ToList();
            if (spawns.Count > 0 && ImGui.BeginCombo("Placed NPC", "Choose a spawn to move"))
            {
                foreach (var d in spawns)
                {
                    uint guid = WbNpcUInt(d["body"]?["guid"]);
                    if (ImGui.Selectable($"#{guid} — map {d["body"]?["map"]}")) WbLoadPackNpc(_wbNpcEditEntry, guid);
                }
                ImGui.EndCombo();
            }
            return;
        }
        ImGui.TextDisabled($"Spawn #{_wbNpcEditSpawn}");
        if (_wbNpcStockSourceEntry != 0) { ImGui.TextDisabled("The existing spawn keeps its location and route."); return; }
        ImGui.InputFloat3("Position", ref _wbNpcSpawnPosition, "%.2f");
        ImGui.SliderFloat("Facing", ref _wbNpcSpawnFacing, 0, 360, "%.0f degrees");
        if (CreatorButton(_wbNpcMoveArmed ? "Cancel move" : "Pick a new position"))
        {
            bool arm = !_wbNpcMoveArmed; WbSetTool(WorldBuilderTool.Select); _wbSpawnArmed = false; _wbNpcMoveArmed = arm;
        }
        ImGui.SameLine();
        if (CreatorButton("Move to me") && _controller is not null) _wbNpcSpawnPosition = _controller.Position;
        ImGui.TextDisabled("Position is saved together with the NPC changes.");
    }

    private bool WbNpcPointerFrame(ImGuiIOPtr io, bool overUi)
    {
        if (_creatorPanel != CreatorPanel.World || overUi || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return false;
        if (_wbNpcMoveArmed)
        {
            if (TryPickGround(io.MousePos, out var at)) { _wbNpcSpawnPosition = at; _wbNpcMoveArmed = false; }
            return true;
        }
        if (_wbSpawnArmed || _wbMoveArmed || _wbTool is WorldBuilderTool.Sculpt or WorldBuilderTool.Place) return false;
        ulong picked = PickUnit(io.MousePos);
        if (picked == 0) return false;
        if (_wbSpawnPreview.TryGetValue(picked, out uint guid))
        {
            var d = WbNpcPackDocs("dbrow:creature").FirstOrDefault(d => WbNpcUInt(d["body"]?["guid"]) == guid);
            if (d is not null) WbLoadPackNpc(WbNpcUInt(d["body"]?["id"]), guid);
            else if (_wbNpcNearby.OfType<JsonObject>().FirstOrDefault(n => WbNpcUInt(n["spawn"]?["guid"]) == guid) is { } npc) WbLoadStockNpc(npc);
            return true;
        }
        if (_entities.TryGet(picked, out var unit) && unit.IsCreature)
        {
            uint low = (uint)(picked & 0xFFFFFF);
            if (_wbNpcNearby.OfType<JsonObject>().FirstOrDefault(n => WbNpcUInt(n["spawn"]?["guid"]) == low) is { } npc) { WbLoadStockNpc(npc); return true; }
        }
        return false;
    }

    private void WbSyncNpcPreviews()
    {
        if (_creatorPanel == CreatorPanel.World && SuiWebAppUrl.Length > 0)
        {
            if (_wbDocs is null && _wbDocsTask is null) WbRequestDocs();
            if (_controller is not null && (_wbNpcNearbyMap != _config.Start.Map ||
                Vector2.Distance(_wbNpcNearbyCentre, new(_controller.Position.X, _controller.Position.Y)) > 80)) WbRequestNearbyNpcs();
        }
        var want = new HashSet<ulong>();
        var active = _wbState?.Packs.Where(p => p.Enabled || p.Id == _wbPackId).Select(p => p.Id).ToHashSet() ?? new HashSet<int> { _wbPackId };
        var templates = WbNpcPackDocs("dbrow:creature_template").GroupBy(d => WbNpcUInt(d["body"]?["entry"]))
            .ToDictionary(g => g.Key, g => g.First()["body"]!.AsObject());
        bool preview = _wbNpcShowPreviews && _net?.IsInWorld != true;
        void Add(JsonObject spawn, JsonObject template)
        {
            if (!preview || _controller is null || (int?)spawn["map"] != _config.Start.Map) return;
            uint guid = WbNpcUInt(spawn["guid"]), entry = WbNpcUInt(template["entry"]);
            var position = new Vector3(WbNpcFloat(spawn["position_x"]), WbNpcFloat(spawn["position_y"]), WbNpcFloat(spawn["position_z"]));
            if (Vector3.DistanceSquared(position, _controller.Position) > 400 * 400) return;
            uint display = WbNpcUInt(spawn["modelid"]);
            if (display == 0) display = WbNpcUInt(template["display_id1"]);
            if (display == 0) return;
            float scale = WbNpcFloat(template["display_scale1"], 1); if (scale <= 0) scale = 1;
            float facing = WbNpcFloat(spawn["orientation"]);
            ulong synthetic = 0xB1B1_0000_0000_0000UL | guid;
            want.Add(synthetic);
            string signature = FormattableString.Invariant($"{entry}|{display}|{scale}|{position.X}|{position.Y}|{position.Z}|{facing}|{_wbNpcPreviewRevision}");
            _wbNpcPreviewNames[synthetic] = template["name"]?.ToString() ?? "NPC";
            if (_wbNpcPreviewSignatures.TryGetValue(synthetic, out var prior) && prior == signature && _entities.TryGet(synthetic, out _)) return;
            var fields = ObjectFields.ForSyntheticUnit((int)display, scale);
            fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
            fields.SetU32(ObjectFields.UNIT_LEVEL, WbNpcUInt(template["level_min"]));
            fields.SetU32(ObjectFields.UNIT_FACTIONTEMPLATE, WbNpcUInt(template["faction"]));
            fields.SetU32(ObjectFields.UNIT_NPC_FLAGS, WbNpcUInt(template["npc_flags"]));
            _entities.AddSynthetic(new WorldEntity { Guid = synthetic, Type = ObjectTypeId.Unit, Entry = entry, Fields = fields, Position = position, Orientation = facing });
            _wbSpawnPreview[synthetic] = guid; _wbNpcPreviewSignatures[synthetic] = signature;
        }
        var authored = new HashSet<uint>();
        foreach (var doc in WbNpcPackDocs("dbrow:creature").Where(d => active.Contains((int?)d["packId"] ?? 0)))
        {
            var spawn = doc["body"]!.AsObject();
            authored.Add(WbNpcUInt(spawn["guid"]));
            if (templates.TryGetValue(WbNpcUInt(spawn["id"]), out var template)) Add(spawn, template);
        }
        if (_wbNpcNearbyMap == _config.Start.Map)
            foreach (var npc in _wbNpcNearby.OfType<JsonObject>())
            {
                var spawn = npc["spawn"]!.AsObject(); uint guid = WbNpcUInt(spawn["guid"]);
                if (authored.Contains(guid)) continue;
                var replacement = WbNpcPackDocs("npc-replacement").FirstOrDefault(d => active.Contains((int?)d["packId"] ?? 0) && WbNpcUInt(d["body"]?["spawnGuid"]) == guid);
                var template = replacement is not null && templates.TryGetValue(WbNpcUInt(replacement["body"]?["replacementEntry"]), out var changed) ? changed : npc["template"]!.AsObject();
                Add(spawn, template);
            }
        foreach (ulong synthetic in _wbSpawnPreview.Keys.Where(k => !want.Contains(k)).ToList())
        {
            _entities.RemoveSynthetic(synthetic); _wbSpawnPreview.Remove(synthetic);
            _wbNpcPreviewSignatures.Remove(synthetic); _wbNpcPreviewNames.Remove(synthetic);
        }
        if (_creatorPanel == CreatorPanel.World && _wbNpcShowNames)
            foreach (var (guid, name) in _wbNpcPreviewNames)
                if (_entities.TryGet(guid, out var unit) && _creatures?.TryGetSpellPose(guid, out _) == true &&
                    _window.Camera.TryWorldToScreen(unit.Position + new Vector3(0, 0, 2.4f * unit.Scale), ImGui.GetIO().DisplaySize, out var pixel))
                {
                    uint color = _wbSpawnPreview[guid] == _wbNpcEditSpawn ? 0xFF80E8FF : 0xFFE0E5D0;
                    var size = ImGui.CalcTextSize(name);
                    ImGui.GetBackgroundDrawList().AddText(pixel - new Vector2(size.X / 2, 0) + Vector2.One, 0xDD000000, name);
                    ImGui.GetBackgroundDrawList().AddText(pixel - new Vector2(size.X / 2, 0), color, name);
                }
    }

    private void WbSaveEditedNpc()
    {
        if (_wbNpcTemplate is null || _wbNpcSaving || _wbNpcEditPack != _wbPackId || _wbNpcLoadTask is not null || _wbNpcLoadFailed || _wbNpcCopyBlock.Length > 0) return;
        bool importing = _wbNpcStockSourceEntry != 0;
        uint entry = importing ? Math.Max(WbNextId("dbrow:creature_template", "entry", WbTemplateBase), WbReservedEntry + 1) : _wbNpcEditEntry;
        if (importing) WbReservedEntry = entry;
        JsonArray imported = importing ? NpcAuthoringLaw.CloneForSpawn(_wbNpcSource, _wbNpcStockSourceEntry, entry, _wbNpcEditSpawn) : new();
        var latest = WbNpcPackDocs("dbrow:creature_template", _wbPackId).FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == entry)?["body"] as JsonObject;
        var template = importing ? imported.OfType<JsonObject>().First(d => d["kind"]?.ToString() == "dbrow:creature_template")["body"]!.DeepClone().AsObject() : (latest ?? _wbNpcTemplate).DeepClone().AsObject();
        NpcAuthoringLaw.ApplyEditedFields(template, _wbNpcLoadedFields, WbNpcEditableFields());
        uint savedRoles = importing ? _wbNpcFlags : NpcAuthoringLaw.MergeRoles(WbNpcUInt(template["npc_flags"]), _wbNpcOriginalFlags, _wbNpcFlags);
        template["npc_flags"] = savedRoles;
        var items = new JsonArray();
        JsonObject Row(string table, JsonObject body) => new() { ["kind"] = "dbrow:" + table, ["body"] = body };
        var previous = importing ? new List<JsonObject>() : _wbNpcSource.OfType<JsonObject>().ToList();
        foreach (var d in imported.OfType<JsonObject>().Where(d => d["kind"]?.ToString() is not ("dbrow:creature_template" or "dbrow:npc_vendor" or "dbrow:npc_trainer")))
            items.Add(d.DeepClone());

        var vendors = new List<JsonObject>();
        if ((_wbNpcFlags & 4) != 0)
            foreach (var (item, _) in _wbNpcVendor)
            {
                var row = _wbNpcVendorRows.TryGetValue(item, out var existing) ? existing.DeepClone().AsObject() : new JsonObject();
                row["entry"] = entry; row["item"] = item;
                foreach (string field in new[] { "maxcount", "incrtime", "itemflags", "condition_id" }) row[field] ??= 0;
                vendors.Add(row);
            }
        NpcAuthoringLaw.ReplaceRows(items, previous, "dbrow:npc_vendor", vendors, "entry", "item");
        uint trainerCopy = (_wbNpcFlags & 16) != 0 ? (uint)Math.Max(0, _wbNpcCopyTrainer) : 0;
        var trainers = new List<JsonObject>();
        if ((_wbNpcFlags & 16) != 0 && trainerCopy == 0)
            foreach (var r in WbNpcSourceRows("dbrow:npc_trainer")) { var row = r.DeepClone().AsObject(); row["entry"] = entry; trainers.Add(row); }
        NpcAuthoringLaw.ReplaceRows(items, previous, "dbrow:npc_trainer", trainers, "entry", "spell");

        if (_wbNpcMainHand != _wbNpcOriginalMain || _wbNpcOffHand != _wbNpcOriginalOff)
        {
            template["equipment_id"] = _wbNpcMainHand == 0 && _wbNpcOffHand == 0 ? 0 : entry;
            var equipment = new List<JsonObject>();
            if (WbNpcUInt(template["equipment_id"]) > 0)
            {
                var row = WbNpcSourceRows("dbrow:creature_equip_template").FirstOrDefault()?.DeepClone().AsObject() ?? new JsonObject();
                row["entry"] = entry; row["item1"] = _wbNpcMainHand; row["item2"] = _wbNpcOffHand; row["item3"] ??= 0;
                row["probability"] = 100; row["patch_min"] ??= 0; row["patch_max"] ??= 10;
                equipment.Add(row);
            }
            NpcAuthoringLaw.ReplaceRows(items, previous.Where(d => WbNpcUInt(d["body"]?["entry"]) == entry), "dbrow:creature_equip_template", equipment, "entry", "item1", "item2", "item3");
        }
        if (_wbNpcGossip.Trim() != _wbNpcOriginalGreeting.Trim() || _wbNpcFlags != _wbNpcOriginalFlags || importing && WbNpcUInt(_wbNpcTemplate["gossip_menu_id"]) != 0)
        {
            uint menu = Math.Max(WbNextId("dbrow:gossip_menu", "entry", 62_000), WbReservedMenu + 1);
            WbReservedMenu = menu;
            template["gossip_menu_id"] = menu; template["npc_flags"] = savedRoles | 1;
            string greeting = _wbNpcGossip.Trim();
            uint oldMenu = WbNpcUInt(_wbNpcTemplate["gossip_menu_id"]);
            var menus = WbNpcSourceRows("dbrow:gossip_menu").Where(m => WbNpcUInt(m["entry"]) == oldMenu).ToList();
            bool replaceGreeting = greeting != _wbNpcOriginalGreeting.Trim() || menus.Count == 0;
            uint text = 0;
            if (replaceGreeting)
            {
                text = Math.Max(WbNextId("dbrow:npc_text", "ID", WbTemplateBase), WbNextId("dbrow:broadcast_text", "entry", WbTemplateBase));
                var oldText = WbNpcSourceRows("dbrow:npc_text").FirstOrDefault(t => WbNpcUInt(t["ID"]) == WbNpcUInt(menus.FirstOrDefault()?["text_id"]));
                var changedText = oldText?.DeepClone().AsObject() ?? new JsonObject();
                changedText["ID"] = text; changedText["BroadcastTextID0"] = text; changedText["Probability0"] = 1;
                items.Add(Row("broadcast_text", new JsonObject { ["entry"] = text, ["male_text"] = greeting, ["female_text"] = greeting, ["chat_type"] = 0 }));
                items.Add(Row("npc_text", changedText));
            }
            if (menus.Count == 0) menus.Add(new JsonObject { ["script_id"] = 0, ["condition_id"] = 0 });
            for (int m = 0; m < menus.Count; m++)
            {
                var row = menus[m].DeepClone().AsObject(); row["entry"] = menu;
                if (m == 0 && replaceGreeting) row["text_id"] = text;
                items.Add(Row("gossip_menu", row));
            }
            var options = NpcAuthoringLaw.CloneMenuOptions(WbNpcSourceRows("dbrow:gossip_menu_option"), oldMenu, menu,
                savedRoles, WbGossipOptions.Select(s => (s.Flag, s.Option)));
            foreach (var option in options)
            {
                items.Add(Row("gossip_menu_option", option));
            }
            int optionId = options.Count == 0 ? 0 : options.Max(o => (int)WbNpcUInt(o["id"])) + 1;
            foreach (var (flag, icon, label, broadcast, option) in WbGossipOptions)
                if ((savedRoles & flag) != 0 && !options.Any(o => WbNpcUInt(o["option_id"]) == option && (WbNpcUInt(o["npc_option_npcflag"]) & flag) != 0))
                    items.Add(Row("gossip_menu_option", new JsonObject
                    {
                        ["menu_id"] = menu, ["id"] = optionId++, ["option_icon"] = icon, ["option_text"] = label,
                        ["option_broadcast_text"] = broadcast, ["option_id"] = option, ["npc_option_npcflag"] = flag,
                        ["action_menu_id"] = 0, ["action_poi_id"] = 0, ["action_script_id"] = 0,
                        ["box_coded"] = 0, ["box_money"] = 0, ["box_text"] = "", ["box_broadcast_text"] = 0, ["condition_id"] = 0,
                    }));
        }
        items.Insert(0, Row("creature_template", template));
        uint savedSpawn = _wbNpcEditSpawn;
        if (!importing && _wbNpcSpawnBody is { } spawn && (_wbNpcSpawnPosition != _wbNpcLoadedSpawnPosition || _wbNpcSpawnFacing != _wbNpcLoadedSpawnFacing))
        {
            if (!float.IsFinite(_wbNpcSpawnPosition.X) || !float.IsFinite(_wbNpcSpawnPosition.Y) || !float.IsFinite(_wbNpcSpawnPosition.Z))
            { _wbMessage = "NPC position must contain finite coordinates."; return; }
            var row = spawn.DeepClone().AsObject();
            row["position_x"] = _wbNpcSpawnPosition.X; row["position_y"] = _wbNpcSpawnPosition.Y; row["position_z"] = _wbNpcSpawnPosition.Z;
            row["orientation"] = WorldBuilderLaw.PositiveAngle(_wbNpcSpawnFacing * MathF.PI / 180);
            items.Add(Row("creature", row));
        }
        string description = $"edit NPC {WbText(_wbNpcName)} ({entry})";
        _wbNpcSaving = true;
        _wbNpcSaveTask = trainerCopy == 0 ? _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, description, items.ToJsonString()) : WbWithTrainerCopy(entry, trainerCopy, items, description);
        WbOp(description, _wbNpcSaveTask, _ =>
        {
            _wbNpcEditEntry = _wbSpawnEntry = entry; _wbNpcStockSourceEntry = 0;
            var prior = importing ? new JsonArray(_wbNpcSource.OfType<JsonObject>().Where(d => d["kind"]?.ToString() is
                "dbrow:creature_equip_template" or "dbrow:gossip_menu" or "dbrow:gossip_menu_option" or "dbrow:npc_text" or "dbrow:broadcast_text").Select(d => d.DeepClone()).ToArray()) : _wbNpcSource;
            _wbNpcTemplate = template; _wbNpcSource = NpcAuthoringLaw.ApplyChanges(prior, items);
            if (importing) { _wbNpcEditSpawn = 0; _wbNpcSpawnBody = null; }
            else
            {
                _wbNpcEditSpawn = savedSpawn;
                var savedPosition = items.OfType<JsonObject>().FirstOrDefault(d => d["kind"]?.ToString() == "dbrow:creature")?["body"] as JsonObject;
                if (savedPosition is not null) _wbNpcSpawnBody = savedPosition.DeepClone().AsObject();
            }
            WbLoadNpcForm(_wbNpcSource); _wbDocsTask = null; WbRequestDocs(); _wbNpcPreviewRevision++;
        });
    }
}
