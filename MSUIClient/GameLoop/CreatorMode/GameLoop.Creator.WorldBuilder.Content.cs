using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder content tools (shared_docs/WORLD_BUILDER.md §5): NPC Creator, spawn
// placement, Quest Creator and the pack content browser. Everything is a World
// Content Pack doc posted to /WorldPacks/Content as ONE audited, undoable op per
// action; rows only ever land in the pack-reserved id ranges (templates/quests/texts
// 7,000,000+, spawn guids 1,500,000-7,999,999), so a pack never edits stock data.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const uint WbTemplateBase = 7_000_000;
    // Pack spawn guids: [1,500,000, 8,000,000) - vanilla guids are 24-bit and mangosd needs runtime
    // headroom above the highest DB guid (WorldPackContent.SpawnGuidBase in the web app).
    private const uint WbSpawnBase = 1_500_000;

    /// <summary>npc_flags bits of 1.12 with the label the Creator shows.</summary>
    private static readonly (uint Flag, string Label)[] WbNpcFlags =
    {
        (1, "Gossip"), (2, "Quest giver"), (4, "Vendor"), (16384, "Repair"), (16, "Trainer"),
        (128, "Innkeeper"), (256, "Banker"), (8192, "Stable master"), (8, "Flight master"),
        (32, "Spirit healer"), (4096, "Auctioneer"), (1024, "Tabard designer"),
    };

    /// <summary>Role → the gossip option that reaches it (npc flag, icon, text, broadcast text, option id),
    /// as the stock generic menu 0 has them.</summary>
    private static readonly (uint Flag, int Icon, string Text, int Broadcast, int Option)[] WbGossipOptions =
    {
        (2, 0, "GOSSIP_OPTION_QUESTGIVER", 0, 2), (4, 1, "I want to browse your goods.", 3370, 3),
        (16, 3, "Train me.", 0, 5), (32, 4, "Return me to life.", 0, 6), (128, 5, "Make this inn your home.", 2822, 8),
        (256, 6, "I would like to check my deposit box.", 0, 9), (8, 2, "I need a ride.", 0, 4),
        (8192, 0, "I wish to stable my pet.", 0, 14), (4096, 6, "GOSSIP_OPTION_AUCTIONEER", 0, 13),
        (1024, 8, "I want to create a guild crest.", 0, 11), (16384, 1, "GOSSIP_OPTION_ARMORER", 0, 15),
    };

    // NPC form
    private readonly byte[] _wbNpcName = new byte[80];
    private readonly byte[] _wbNpcSub = new byte[80];
    private string _wbNpcGossip = "";
    private readonly byte[] _wbNpcSearch = new byte[64];
    private readonly byte[] _wbItemSearch = new byte[64];
    private List<CreatorCreatureTable.Creature>? _wbNpcHits;
    private string _wbNpcSearchLast = "\u0001";
    private string _wbItemSearchLast = "\u0001";
    private List<CreatorItemTable.Item>? _wbItemHits;
    private CreatorItemTable? _wbItems;
    private uint _wbNpcDisplay = 1141, _wbNpcFlags = 1;
    private int _wbNpcLevel = 30, _wbNpcLevelMax = 30, _wbNpcFaction = 35, _wbNpcRank, _wbNpcType = 7;
    private int _wbNpcTrainerType, _wbNpcTrainerClass, _wbNpcCopyTrainer, _wbNpcMainHand, _wbNpcOffHand;
    private float _wbNpcScale = 1f;
    private readonly List<(uint Item, string Name)> _wbNpcVendor = new();
    private bool _wbNpcSpawnHere = true;

    // spawn tool (single / linked pack / patrol - the panel and the script commands share WbPackItems/WbPatrolItems)
    private uint _wbSpawnEntry, _wbSpawnMemberEntry;
    private bool _wbSpawnArmed;
    private int _wbSpawnPack;
    private int _wbSpawnMode;                 // 0 single, 1 linked pack, 2 patrol
    private int _wbPackCount = 3, _wbPatrolFollowers = 1;
    private float _wbPackRadius = 5f;
    private readonly List<Vector3> _wbPatrolPoints = new();

    // quest form: GameLoop.Creator.WorldBuilder.Quest.cs

    // content browser
    private Task<string>? _wbDocsTask;
    private JsonArray? _wbDocs;
    private readonly Dictionary<ulong, uint> _wbSpawnPreview = new();   // synthetic guid → spawn guid

    private void RegisterCreatorContentSections()
    {
        CreatorSection("World", "wb-npc", "NPC Creator", false, DrawWbNpcSection);
        CreatorSection("World", "wb-spawn", "Spawn NPCs", false, DrawWbSpawnSection);
        CreatorSection("World", "wb-quest", "Quest Creator", false, DrawWbQuestSection);
        CreatorSection("World", "wb-docs", "Pack content", false, DrawWbDocsSection);
        CreatorSection("World", "wb-maps", "Maps & dungeons", false, DrawWbMapsSection);
        CreatorSection("World", "wb-region", "Regions, seams & paths", false, DrawWbRegionSection);
    }

    // ═══════════════════════════════════════════════════════════════ NPC Creator

    private void DrawWbNpcSection()
    {
        DrawWbNpcBrowser();
        ImGui.BeginDisabled(_wbNpcSaving || _wbNpcLoadTask is not null);
        EnsureCreatorCreatures();
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Name##wb-npc", _wbNpcName, (uint)_wbNpcName.Length);
        ObserveWorldBuilderUiItem("NPC name");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Title##wb-npc", _wbNpcSub, (uint)_wbNpcSub.Length);
        ObserveWorldBuilderUiItem("NPC title");
        DrawWbNpcSaveButton();
        if (_wbNpcEditEntry == 0) ImGui.Checkbox("Also place it where I stand", ref _wbNpcSpawnHere);
        else if (WbNpcFormSection("Position and facing")) DrawWbNpcSpawnEditor();
        if (WbNpcFormSection("Appearance and level")) DrawWbNpcAppearance();
        if (WbNpcFormSection("Roles and services", ImGuiTreeNodeFlags.DefaultOpen)) DrawWbNpcServices();
        if (WbNpcFormSection("Equipment and greeting")) DrawWbNpcEquipment();
        ImGui.EndDisabled();
    }
    /// <summary>Next free id in a reserved range across every doc of a kind (all packs).</summary>
    private uint WbNextId(string kind, string field, uint floor)
    {
        uint max = floor - 1;
        if (_wbDocs is not null)
            foreach (var d in _wbDocs.OfType<JsonObject>())
                if ((string?)d["kind"] == kind && d["body"]?[field] is JsonNode v &&
                    uint.TryParse(WbScalar(v), out uint n) && n > max) max = n;
        return max + 1;
    }

    private static string WbScalar(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    private void WbCreateNpc()
    {
        if (_wbNpcSaving) return;
        if (_wbNpcEditEntry != 0) { WbSaveEditedNpc(); return; }
        if (_wbDocs is null) { WbRequestDocs(); _wbMessage = "loading pack content first - press Create again"; return; }
        uint entry = Math.Max(WbNextId("dbrow:creature_template", "entry", WbTemplateBase), WbReservedEntry + 1);
        WbReservedEntry = entry;
        string name = WbText(_wbNpcName), sub = WbText(_wbNpcSub), gossip = _wbNpcGossip.Trim();
        var items = new JsonArray();
        JsonObject Row(string table, JsonObject body) => new() { ["kind"] = "dbrow:" + table, ["body"] = body };

        var tpl = new JsonObject
        {
            ["entry"] = entry, ["patch"] = 0, ["name"] = name, ["subname"] = sub,
            ["level_min"] = _wbNpcLevel, ["level_max"] = _wbNpcLevelMax, ["faction"] = _wbNpcFaction,
            ["npc_flags"] = _wbNpcFlags, ["display_id1"] = _wbNpcDisplay, ["display_scale1"] = _wbNpcScale,
            ["display_probability1"] = 100, ["display_total_probability"] = 100,
            ["speed_walk"] = 1.0, ["speed_run"] = 1.14286, ["type"] = _wbNpcType, ["rank"] = _wbNpcRank,
            ["unit_class"] = 1, ["xp_multiplier"] = 1.0, ["health_multiplier"] = _wbNpcRank == 3 ? 12.0 : _wbNpcRank is 1 or 2 ? 3.0 : 1.0,
            ["mana_multiplier"] = 1.0, ["armor_multiplier"] = 1.0, ["damage_multiplier"] = _wbNpcRank == 3 ? 4.0 : _wbNpcRank is 1 or 2 ? 2.0 : 1.0,
            ["damage_variance"] = 0.14, ["base_attack_time"] = 2000, ["ranged_attack_time"] = 2000,
            ["inhabit_type"] = 3, ["movement_type"] = 0, ["detection_range"] = 20, ["call_for_help_range"] = 5,
            ["leash_range"] = 0, ["ai_name"] = "", ["script_name"] = "",
            ["trainer_type"] = (_wbNpcFlags & 16) != 0 ? _wbNpcTrainerType : 0,
            ["trainer_class"] = (_wbNpcFlags & 16) != 0 ? _wbNpcTrainerClass : 0,
        };
        if (_wbNpcMainHand > 0 || _wbNpcOffHand > 0)
        {
            tpl["equipment_id"] = entry;
            items.Add(Row("creature_equip_template", new JsonObject
            {
                ["entry"] = entry, ["probability"] = 100, ["item1"] = _wbNpcMainHand, ["item2"] = _wbNpcOffHand, ["item3"] = 0,
                ["patch_min"] = 0, ["patch_max"] = 10,
            }));
        }
        if (gossip.Length > 0)
        {
            // gossip_menu.entry is SMALLINT UNSIGNED in VMaNGOS: menus get their own range (62000+).
            uint menu = Math.Max(WbNextId("dbrow:gossip_menu", "entry", 62_000), WbReservedMenu + 1);
            WbReservedMenu = menu;
            tpl["gossip_menu_id"] = menu;
            tpl["npc_flags"] = _wbNpcFlags | 1;
            items.Add(Row("broadcast_text", new JsonObject { ["entry"] = entry, ["male_text"] = gossip, ["female_text"] = gossip, ["chat_type"] = 0 }));
            items.Add(Row("npc_text", new JsonObject { ["ID"] = entry, ["BroadcastTextID0"] = entry, ["Probability0"] = 1 }));
            items.Add(Row("gossip_menu", new JsonObject { ["entry"] = menu, ["text_id"] = entry, ["script_id"] = 0, ["condition_id"] = 0 }));
            // With its own menu the NPC shows ONLY that menu's options — every role needs its option
            // row or players cannot reach the shop/trainer/inn (web verifier check C4).
            int optionId = 0;
            foreach (var (flag, icon, text, broadcast, option) in WbGossipOptions)
            {
                if (((uint)tpl["npc_flags"]! & flag) == 0) continue;
                items.Add(Row("gossip_menu_option", new JsonObject
                {
                    ["menu_id"] = menu, ["id"] = optionId++, ["option_icon"] = icon, ["option_text"] = text,
                    ["option_broadcast_text"] = broadcast, ["option_id"] = option, ["npc_option_npcflag"] = flag,
                    ["action_menu_id"] = 0, ["action_poi_id"] = 0, ["action_script_id"] = 0, ["box_coded"] = 0,
                    ["box_money"] = 0, ["box_text"] = "", ["box_broadcast_text"] = 0, ["condition_id"] = 0,
                }));
            }
        }
        items.Insert(0, Row("creature_template", tpl));
        if ((_wbNpcFlags & 4) != 0)
            foreach (var (item, _) in _wbNpcVendor)
            {
                var row = _wbNpcVendorRows.TryGetValue(item, out var stock) ? stock.DeepClone().AsObject() : new JsonObject();
                row["entry"] = entry; row["item"] = item;
                foreach (string field in new[] { "maxcount", "incrtime", "itemflags", "condition_id" }) row[field] ??= 0;
                items.Add(Row("npc_vendor", row));
            }
        if (_wbNpcSpawnHere && _controller is not null)
            items.Add(WbSpawnRow(entry, _controller.Position, _controller.Yaw));

        string label = $"NPC {name} ({entry})";
        uint copyFrom = (_wbNpcFlags & 16) != 0 ? (uint)Math.Max(_wbNpcCopyTrainer, 0) : 0;
        int packId = _wbPackId;
        _wbNpcSaving = true;
        _wbNpcSaveTask = copyFrom == 0
            ? _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString())
            : WbWithTrainerCopy(entry, copyFrom, items, label);
        WbOp(label, _wbNpcSaveTask, _ =>
        {
            _wbSpawnEntry = _wbNpcEditEntry = entry; _wbNpcEditPack = packId; _wbNpcStockSourceEntry = 0;
            _wbNpcSpawnBody = items.OfType<JsonObject>().FirstOrDefault(d => d["kind"]?.ToString() == "dbrow:creature")?["body"]?.DeepClone().AsObject();
            _wbNpcEditSpawn = WbNpcUInt(_wbNpcSpawnBody?["guid"]); _wbNpcBrowserOpen = false;
            WbLoadNpcForm(items); _wbDocsTask = null; WbRequestDocs();
        });
    }

    /// <summary>A reserved-range entry handed out this session but not yet visible in the docs list
    /// (two quick creates must not collide while the first POST is in flight).</summary>
    private uint WbReservedEntry;
    private uint WbReservedGuid;
    private uint WbReservedMenu;

    private async Task<WorldPackClient.Reply> WbWithTrainerCopy(uint entry, uint copyFrom, JsonArray items, string label)
    {
        int packId = _wbPackId;
        var source = JsonNode.Parse(await _wbClient.NpcAsync(SuiWebAppUrl, copyFrom))?["docs"] as JsonArray;
        var spells = source?.OfType<JsonObject>().Where(d => d["kind"]?.ToString() == "dbrow:npc_trainer" && d["body"] is JsonObject).ToList() ?? new();
        if (spells.Count == 0) return new WorldPackClient.Reply { Success = false, Error = "The selected source NPC has no trainer spells. Choose another trainer." };
        foreach (var doc in spells)
        {
            var row = doc["body"]!.DeepClone().AsObject();
            row["entry"] = entry;
            items.Add(new JsonObject { ["kind"] = "dbrow:npc_trainer", ["body"] = row });
        }
        return await _wbClient.ContentAsync(SuiWebAppUrl, packId, label + $" + {spells.Count} trainer spell(s)", items.ToJsonString());
    }
    private JsonObject WbSpawnRow(uint entry, Vector3 at, float facing)
    {
        uint guid = Math.Max(WbNextId("dbrow:creature", "guid", WbSpawnBase), WbReservedGuid + 1);
        WbReservedGuid = guid;
        return new JsonObject
        {
            ["kind"] = "dbrow:creature",
            ["body"] = new JsonObject
            {
                ["guid"] = guid, ["id"] = entry, ["map"] = _config.Start.Map,
                ["position_x"] = Math.Round(at.X, 2), ["position_y"] = Math.Round(at.Y, 2), ["position_z"] = Math.Round(at.Z, 2),
                ["orientation"] = Math.Round(facing, 3), ["spawntimesecsmin"] = 300, ["spawntimesecsmax"] = 300,
                ["wander_distance"] = 0, ["health_percent"] = 100, ["mana_percent"] = 100, ["movement_type"] = 0,
                ["spawn_flags"] = 0, ["visibility_mod"] = 0, ["patch_min"] = 0, ["patch_max"] = 10,
            },
        };
    }

    // ═══════════════════════════════════════════════════════════════ spawn tool

    private void DrawWbSpawnSection()
    {
        bool busy = _wbOps.Count > 0 || _wbNpcSaving;
        ImGui.BeginDisabled(_wbPackId == 0 || busy);
        if (CreatorButton("Undo last pack edit")) WbUndo();
        ImGui.EndDisabled();
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("Loading pack NPCs..."); return; }
        WbValidateSpawnSelection();
        var templates = WbNpcPackDocs("dbrow:creature_template", _wbPackId)
            .GroupBy(d => WbNpcUInt(d["body"]?["entry"])).Select(g => g.First()).ToList();
        if (templates.Count == 0) ImGui.TextWrapped("This pack has no NPCs yet. Create an NPC in the editor, then return here to place it.");
        string current = templates.FirstOrDefault(t => WbNpcUInt(t["body"]?["entry"]) == _wbSpawnEntry)?["body"]?["name"]?.ToString() ?? "Choose an NPC";
        ImGui.BeginDisabled(busy);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        bool npcOpen = ImGui.BeginCombo("NPC##wb-spawn", current); ObserveWorldBuilderUiItem("NPC placement picker");
        if (npcOpen)
        {
            foreach (var t in templates)
            {
                uint e = WbNpcUInt(t["body"]?["entry"]); string name = t["body"]?["name"]?.ToString() ?? "NPC";
                if (ImGui.Selectable($"{name}##spawn-{e}", e == _wbSpawnEntry)) { WbCancelNpcPlacement(); _wbSpawnEntry = e; }
                ObserveWorldBuilderUiItem("Place NPC: " + name);
            }
            ImGui.EndCombo();
        }
        foreach (var (mode, name) in new[] { (0, "Single NPC"), (1, "Linked group"), (2, "Patrol route") })
        {
            if (ImGui.RadioButton(name + "##wb-sm", _wbSpawnMode == mode)) { WbCancelNpcPlacement(); _wbSpawnMode = mode; }
            ObserveWorldBuilderUiItem(name);
        }
        ImGui.TextWrapped(_wbSpawnMode switch
        {
            1 => "Place a leader and nearby companions together. The group fights, retreats and respawns together.",
            2 => "Draw a looping route for a leader and optional followers. Add at least two ground points, then save the patrol.",
            _ => "Place one NPC at each ground click. It faces toward you. You can move or edit it afterward.",
        });
        if (_wbSpawnMode != 0)
        {
            string member = _wbSpawnMemberEntry == 0 ? "Same as leader" : templates.FirstOrDefault(t => WbNpcUInt(t["body"]?["entry"]) == _wbSpawnMemberEntry)?["body"]?["name"]?.ToString() ?? "Same as leader";
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool membersOpen = ImGui.BeginCombo(_wbSpawnMode == 1 ? "Members##wb-spawn" : "Followers##wb-spawn", member);
            ObserveWorldBuilderUiItem("NPC placement member picker");
            if (membersOpen)
            {
                if (ImGui.Selectable("Same as leader", _wbSpawnMemberEntry == 0)) _wbSpawnMemberEntry = 0;
                ObserveWorldBuilderUiItem("NPC member: Same as leader");
                foreach (var t in templates)
                {
                    uint e = WbNpcUInt(t["body"]?["entry"]); string name = t["body"]?["name"]?.ToString() ?? "NPC";
                    if (ImGui.Selectable($"{name}##member-{e}", e == _wbSpawnMemberEntry)) _wbSpawnMemberEntry = e;
                    ObserveWorldBuilderUiItem("NPC member: " + name);
                }
                ImGui.EndCombo();
            }
        }
        if (_wbSpawnMode == 1)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderInt("Size##wb-pack", ref _wbPackCount, 2, 6);
            ObserveWorldBuilderUiItem("NPC group size");
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderFloat("Spread##wb-pack", ref _wbPackRadius, 2f, 12f, "%.0f yd");
            ObserveWorldBuilderUiItem("NPC group spread");
        }
        else if (_wbSpawnMode == 2)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderInt("Followers##wb-patrol", ref _wbPatrolFollowers, 0, 4);
            ObserveWorldBuilderUiItem("NPC patrol followers");
            ImGui.TextWrapped($"{_wbPatrolPoints.Count} route point(s). Save creates the whole patrol as one edit. Stop or Escape discards the unsaved route.");
            ImGui.BeginDisabled(_wbPatrolPoints.Count < 2 || _wbSpawnEntry == 0);
            if (CreatorButton("Save patrol"))
            {
                if (WbPatrolItems(_wbSpawnEntry, _wbSpawnMemberEntry == 0 ? _wbSpawnEntry : _wbSpawnMemberEntry, _wbPatrolFollowers,
                        _wbPatrolPoints.Select(p => new Vector2(p.X, p.Y)).ToList()) is { } patrol)
                    WbSaveNpcPlacement($"patrol {_wbSpawnEntry}", patrol, true);
                else _wbMessage = "The route needs open ground at every point. Adjust its points and try again.";
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(_wbPatrolPoints.Count == 0);
            if (CreatorButton("Undo point")) _wbPatrolPoints.RemoveAt(_wbPatrolPoints.Count - 1);
            ImGui.SameLine();
            if (CreatorButton("Clear route")) _wbPatrolPoints.Clear();
            ImGui.EndDisabled();
        }
        ImGui.BeginDisabled(_wbSpawnEntry == 0 || _wbPackId == 0);
        if (CreatorButton(_wbSpawnArmed ? "Stop (Esc)" : _wbSpawnMode == 2 ? "Start route" : "Start placing"))
        {
            if (_wbSpawnArmed) WbCancelNpcPlacement();
            else { WbSetTool(WorldBuilderTool.Select); _wbMoveArmed = _wbNpcMoveArmed = false; _wbSpawnArmed = true; }
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (_wbSpawnArmed) ImGui.TextWrapped(_wbSpawnMode == 2 ? "Click the ground to add route points. Escape cancels this route." : "Click the ground to place. Choose Stop or press Escape when finished.");
        int spawns = WbNpcPackDocs("dbrow:creature", _wbPackId).Count();
        ImGui.TextWrapped($"{spawns} spawn(s) in this pack. Saved placements appear here and go live after publishing. Undo reverses the pack's most recent edit, including edits made on other pages.");
    }

    private bool WbValidateSpawnSelection()
    {
        if (_wbSpawnPack != _wbPackId) { WbCancelNpcPlacement(); _wbSpawnPack = _wbPackId; }
        bool Owns(uint entry) => entry != 0 && WbNpcPackDocs("dbrow:creature_template", _wbPackId).Any(d => WbNpcUInt(d["body"]?["entry"]) == entry);
        if (!Owns(_wbSpawnEntry)) { _wbSpawnEntry = 0; WbCancelNpcPlacement(); }
        if (!Owns(_wbSpawnMemberEntry)) _wbSpawnMemberEntry = 0;
        return _wbPackId != 0 && _wbSpawnEntry != 0;
    }

    private void WbSaveNpcPlacement(string label, JsonArray items, bool patrol = false)
    {
        int pack = _wbPackId;
        WbOp(label, _wbClient.ContentAsync(SuiWebAppUrl, pack, label, items.ToJsonString()), _ =>
        {
            if (patrol && _wbPackId == pack) WbCancelNpcPlacement();
            WbRequestDocs();
        });
    }

    /// <summary>Select-tool click while the spawn tool is armed: one creature row, a linked pack, or a patrol point.</summary>
    private bool WbTrySpawnClick(Vector3 at)
    {
        if (!_wbSpawnArmed) return false;
        if (!WbValidateSpawnSelection() || !_wbSpawnArmed) return true;
        if (_wbOps.Count > 0 || _wbNpcSaving || _controller is null) return true;
        if (_wbSpawnMode == 2) { _wbPatrolPoints.Add(at); return true; }
        if (_wbSpawnMode == 1)
        {
            var entries = new List<uint> { _wbSpawnEntry, _wbSpawnMemberEntry == 0 ? _wbSpawnEntry : _wbSpawnMemberEntry };
            if (WbPackItems(entries, new Vector2(at.X, at.Y), _wbPackRadius, _wbPackCount) is { } pack) WbSaveNpcPlacement($"pack {_wbSpawnEntry} x{_wbPackCount}", pack);
            else _wbMessage = "This group needs more open ground. Choose a clearer spot or reduce its spread.";
            return true;
        }
        float facing = MathF.Atan2(_controller.Position.Y - at.Y, _controller.Position.X - at.X);
        var items = new JsonArray { WbSpawnRow(_wbSpawnEntry, at, facing) };
        WbSaveNpcPlacement($"spawn NPC {_wbSpawnEntry}", items);
        return true;
    }

    /// <summary>CreatureGroups.h OPTION_*: a pack aggroes, evades and respawns together (0x2|0x4|0x8 = 14, the common
    /// stock setting); a patrol's followers also move in formation with the leader (0x1 more = 15).</summary>
    internal const int WbPackFlags = 0x2 | 0x4 | 0x8, WbPatrolFlags = 0x1 | 0x2 | 0x4 | 0x8;

    /// <summary>creature_groups.angle is FLOAT UNSIGNED in the world DB: the angle goes in as [0, 2pi) - an Atan2 result is
    /// negative half the time and MySQL refused the row mid-install (build #28, 2026-09-27).</summary>
    private static JsonObject WbGroupRow(uint leader, uint member, float dist, float angle, int flags) => new()
    {
        ["kind"] = "dbrow:creature_groups",
        ["body"] = new JsonObject
        {
            ["leader_guid"] = leader, ["member_guid"] = member, ["dist"] = Math.Round(dist, 2),
            ["angle"] = Math.Round(MSUIClient.Engine.UI.WorldBuilderLaw.PositiveAngle(angle), 3), ["flags"] = flags,
        },
    };

    /// <summary>
    /// A linked pack as ONE op: <paramref name="count"/> spawns on open ground around the centre (the entries
    /// cycle; the first is the leader, standing at the centre) and their creature_groups rows - the leader lists
    /// itself - so the pack aggroes, evades and respawns as one (a stock dungeon pull, not single mobs).
    /// </summary>
    private JsonArray? WbPackItems(IReadOnlyList<uint> entries, Vector2 centre, float radius, int count)
    {
        var items = new JsonArray();
        var placed = new List<(uint Guid, Vector3 At)>();
        var rng = new Random((int)(centre.X * 7 + centre.Y * 13));
        for (int i = 0; i < count; i++)
        {
            float ang = i * MathF.Tau / Math.Max(1, count - 1) + (float)rng.NextDouble() * 0.5f;
            float r = i == 0 ? 0f : radius * (0.6f + 0.4f * (float)rng.NextDouble());
            var want = centre + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r;
            if (WbOpenGround(want.X, want.Y, 8f) is not { } spot) { Console.WriteLine($"[wbscript] ERROR pack: no open ground near ({want.X:F0}, {want.Y:F0}), member skipped"); continue; }
            float facing = i == 0 ? (float)(rng.NextDouble() * MathF.Tau) : MathF.Atan2(centre.Y - spot.Y, centre.X - spot.X) + MathF.PI;
            var row = WbSpawnRow(entries[i % entries.Count], spot, facing);
            placed.Add(((uint)row["body"]!["guid"]!, spot));
            items.Add(row);
        }
        if (placed.Count < 2) { Console.WriteLine("[wbscript] ERROR pack: fewer than two members found open ground"); return null; }
        var lead = placed[0];
        foreach (var (guid, at) in placed)
        {
            var d = new Vector2(at.X - lead.At.X, at.Y - lead.At.Y);
            items.Add(WbGroupRow(lead.Guid, guid, d.Length(), d.LengthSquared() > 0.01f ? MathF.Atan2(d.Y, d.X) : 0f, WbPackFlags));
        }
        return items;
    }

    /// <summary>
    /// A patrol as ONE op: the leader spawns at the first route point with movement_type 2 and a creature_movement
    /// row per point (snapped to open ground; 3 s pauses at both ends; vanilla waypoints loop back to the first),
    /// its followers walk behind it in formation (creature_groups, formation + linked).
    /// </summary>
    private JsonArray? WbPatrolItems(uint leaderEntry, uint followerEntry, int followers, IReadOnlyList<Vector2> route)
    {
        var pts = new List<Vector3>();
        foreach (var p in route)
        {
            if (WbOpenGround(p.X, p.Y, 6f) is not { } g) { Console.WriteLine($"[wbscript] ERROR patrol: no open ground near route point ({p.X:F0}, {p.Y:F0})"); return null; }
            pts.Add(g);
        }
        if (pts.Count < 2) return null;
        float heading = MathF.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
        var items = new JsonArray();
        var leaderRow = WbSpawnRow(leaderEntry, pts[0], heading);
        leaderRow["body"]!["movement_type"] = 2;
        uint leader = (uint)leaderRow["body"]!["guid"]!;
        items.Add(leaderRow);
        int far = pts.Count / 2;   // the turnaround: pause there and at home
        for (int i = 0; i < pts.Count; i++)
            items.Add(new JsonObject
            {
                ["kind"] = "dbrow:creature_movement",
                ["body"] = new JsonObject
                {
                    ["id"] = leader, ["point"] = i + 1,
                    ["position_x"] = Math.Round(pts[i].X, 2), ["position_y"] = Math.Round(pts[i].Y, 2), ["position_z"] = Math.Round(pts[i].Z, 2),
                    ["orientation"] = 0, ["waittime"] = i == 0 || i == far ? 3000 : 0, ["wander_distance"] = 0, ["script_id"] = 0, ["path_id"] = 0,
                },
            });
        if (followers > 0) items.Add(WbGroupRow(leader, leader, 0f, 0f, WbPatrolFlags));
        for (int k = 0; k < followers; k++)
        {
            // Behind the leader, fanned left/right: angle is relative to the leader's facing (pi = straight behind).
            float angle = MathF.PI + (k % 2 == 0 ? 1 : -1) * (0.35f + 0.25f * (k / 2));
            float dist = 2.5f + 1.2f * (k / 2);
            var want = new Vector2(pts[0].X + MathF.Cos(heading + angle) * dist, pts[0].Y + MathF.Sin(heading + angle) * dist);
            var at = WbOpenGround(want.X, want.Y, 4f) ?? pts[0];
            var row = WbSpawnRow(followerEntry, at, heading);
            items.Add(row);
            items.Add(WbGroupRow(leader, (uint)row["body"]!["guid"]!, dist, angle, WbPatrolFlags));
        }
        return items;
    }

    // ═══════════════════════════════════════════════════════════════ Quest Creator

    // ═══════════════════════════════════════════════════════════════ content browser

    private void WbRequestDocs()
    {
        if (SuiWebAppUrl.Length == 0 || _wbDocsTask is not null) return;
        _wbDocsTask = _wbClient.DocsAsync(SuiWebAppUrl, null, null);
    }

    private void PumpWbDocs()
    {
        PumpWbNpcTasks();
        if (_wbDocsTask is not { IsCompleted: true } t) return;
        _wbDocsTask = null;
        if (t.IsFaulted) { _wbMessage = "docs: " + t.Exception?.GetBaseException().Message; return; }
        try { _wbDocs = JsonNode.Parse(t.Result)?["docs"] as JsonArray; _wbNpcPreviewRevision++; WbRefreshUnchangedNpcForm(); }
        catch (Exception ex) { _wbMessage = "docs: " + ex.Message; }
    }

    private void DrawWbDocsSection()
    {
        if (CreatorButton("Refresh")) WbRequestDocs();
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("loading..."); return; }
        var mine = _wbDocs.OfType<JsonObject>().Where(d => (int?)d["packId"] == _wbPackId).ToList();
        ImGui.TextDisabled($"{mine.Count} content doc(s) in this pack");
        if (!ImGui.BeginChild("##wb-docs", new Vector2(-1f, 220f * CreatorUiScale), true)) { ImGui.EndChild(); return; }
        foreach (var group in mine.GroupBy(d => (string?)d["kind"] ?? ""))
        {
            if (!ImGui.TreeNode($"{group.Key} ({group.Count()})##docs-{group.Key}")) continue;
            foreach (var d in group)
            {
                string key = (string?)d["docKey"] ?? "";
                string name = d["body"]?["name"]?.ToString() ?? d["body"]?["Title"]?.ToString() ?? "";
                ImGui.PushID(group.Key + key);
                if (ImGui.SmallButton("x"))
                {
                    var items = new JsonArray { new JsonObject { ["kind"] = group.Key, ["key"] = key, ["body"] = null } };
                    WbOp($"delete {group.Key} {key}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"delete {group.Key} {key}", items.ToJsonString()),
                        _ => WbRequestDocs());
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{key}  {name}");
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
        ImGui.EndChild();
    }

    /// <summary>Creator preview of every pack spawn on this map as a synthetic unit (offline there
    /// is no server to show them; online, the real spawns appear after publish).</summary>
    private void WbSyncSpawnPreview()
    {
        WbSyncNpcPreviews();
    }
}
