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
        EnsureCreatorCreatures();
        float w = CreatorControlWidth;
        ImGui.TextDisabled("Start from any creature's look, then make it yours.");
        ImGui.SetNextItemWidth(w);
        ImGui.InputText("look like##wb-npc-search", _wbNpcSearch, (uint)_wbNpcSearch.Length);
        string q = WbText(_wbNpcSearch);
        if (q != _wbNpcSearchLast) { _wbNpcSearchLast = q; _wbNpcHits = q.Length >= 2 ? _creatorCreatures?.Search(q, 40) : null; }
        if (_wbNpcHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-npc-hits", new Vector2(-1f, 110f * CreatorUiScale)))
        {
            foreach (var c in hits)
                if (ImGui.Selectable($"{c.Name}{(c.SubName.Length > 0 ? $" <{c.SubName}>" : "")}  (lvl {c.LevelMin}, display {c.DisplayId})##{c.Entry}"))
                {
                    _wbNpcDisplay = c.DisplayId;
                    _wbNpcScale = c.Scale > 0 ? c.Scale : 1f;
                    _wbNpcLevel = c.LevelMin; _wbNpcLevelMax = c.LevelMax;
                    _wbNpcRank = c.Rank; _wbNpcType = c.Type;
                    SpawnCreatorCreature(c.Name, c.DisplayId, c.Scale);   // live look preview
                }
            ImGui.EndListBox();
        }

        ImGui.SetNextItemWidth(w); ImGui.InputText("Name##wb-npc", _wbNpcName, (uint)_wbNpcName.Length);
        ImGui.SetNextItemWidth(w); ImGui.InputText("Title <...>##wb-npc", _wbNpcSub, (uint)_wbNpcSub.Length);
        int display = (int)_wbNpcDisplay;
        ImGui.SetNextItemWidth(w * 0.5f); if (ImGui.InputInt("Display id", ref display)) _wbNpcDisplay = (uint)Math.Max(display, 1);
        ImGui.SameLine();
        if (ImGui.SmallButton("preview")) SpawnCreatorCreature(WbText(_wbNpcName), _wbNpcDisplay, _wbNpcScale);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputFloat("Scale", ref _wbNpcScale, 0.05f, 0.25f, "%.2f");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Level", ref _wbNpcLevel);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Level max", ref _wbNpcLevelMax);
        _wbNpcLevel = Math.Clamp(_wbNpcLevel, 1, 63); _wbNpcLevelMax = Math.Clamp(Math.Max(_wbNpcLevelMax, _wbNpcLevel), 1, 63);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Faction", ref _wbNpcFaction);
        ImGui.SameLine(); ImGui.TextDisabled("35 friendly, 16 hostile, 7 neutral");
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.Combo("Rank", ref _wbNpcRank, "Normal\0Elite\0Rare elite\0Boss\0Rare\0");

        ImGui.TextDisabled("Role");
        int i = 0;
        foreach (var (flag, label) in WbNpcFlags)
        {
            bool on = (_wbNpcFlags & flag) != 0;
            if (i++ % 3 != 0) ImGui.SameLine();
            if (ImGui.Checkbox(label + "##npcflag", ref on)) _wbNpcFlags = on ? _wbNpcFlags | flag : _wbNpcFlags & ~flag;
        }

        if ((_wbNpcFlags & 16) != 0)
        {
            ImGui.SetNextItemWidth(w * 0.5f); ImGui.Combo("Trainer type", ref _wbNpcTrainerType, "Class\0Mount\0Profession\0Pet\0");
            ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Trainer class", ref _wbNpcTrainerClass);
            ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Copy spells from trainer entry", ref _wbNpcCopyTrainer);
            ImGui.TextDisabled("e.g. 5113 Stormwind warrior trainer; the spell list is copied at save.");
        }

        if ((_wbNpcFlags & 4) != 0)
        {
            _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
            ImGui.TextDisabled($"Vendor list ({_wbNpcVendor.Count})");
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("add item##wb-vendor", _wbItemSearch, (uint)_wbItemSearch.Length);
            string iq = WbText(_wbItemSearch);
            if (iq != _wbItemSearchLast) { _wbItemSearchLast = iq; _wbItemHits = iq.Length >= 2 ? _wbItems?.Search(iq, -1, 30) : null; }
            if (_wbItemHits is { Count: > 0 } items && ImGui.BeginListBox("##wb-item-hits", new Vector2(-1f, 90f * CreatorUiScale)))
            {
                foreach (var it in items)
                    if (ImGui.Selectable($"{it.Name}  ({it.Entry})##item{it.Entry}") && _wbNpcVendor.All(v => v.Item != it.Entry))
                        _wbNpcVendor.Add((it.Entry, it.Name));
                ImGui.EndListBox();
            }
            for (int v = 0; v < _wbNpcVendor.Count; v++)
            {
                ImGui.PushID(v);
                if (ImGui.SmallButton("x")) { _wbNpcVendor.RemoveAt(v); ImGui.PopID(); break; }
                ImGui.SameLine(); ImGui.TextUnformatted(_wbNpcVendor[v].Name);
                ImGui.PopID();
            }
        }

        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Main hand item", ref _wbNpcMainHand);
        ImGui.SetNextItemWidth(w * 0.5f); ImGui.InputInt("Off hand item", ref _wbNpcOffHand);
        ImGui.TextDisabled("Greeting (gossip text)");
        ImGui.InputTextMultiline("##wb-npc-gossip", ref _wbNpcGossip, 600, new Vector2(-1f, 60f * CreatorUiScale));
        ImGui.Checkbox("Also spawn it where I stand", ref _wbNpcSpawnHere);

        bool ready = _wbPackId != 0 && WbText(_wbNpcName).Length > 0;
        if (!ready) ImGui.BeginDisabled();
        if (CreatorButton("Create NPC")) WbCreateNpc();
        if (!ready) ImGui.EndDisabled();
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
        foreach (var (item, _) in _wbNpcVendor)
            items.Add(Row("npc_vendor", new JsonObject { ["entry"] = entry, ["item"] = item, ["maxcount"] = 0, ["incrtime"] = 0, ["itemflags"] = 0, ["condition_id"] = 0 }));
        if (_wbNpcSpawnHere && _controller is not null)
            items.Add(WbSpawnRow(entry, _controller.Position, _controller.Yaw));

        string label = $"NPC {name} ({entry})";
        uint copyFrom = (_wbNpcFlags & 16) != 0 ? (uint)Math.Max(_wbNpcCopyTrainer, 0) : 0;
        var task = copyFrom == 0
            ? _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString())
            : WbWithTrainerCopy(entry, copyFrom, items, label);
        WbOp(label, task, _ => { _wbSpawnEntry = entry; WbRequestDocs(); });
    }

    /// <summary>A reserved-range entry handed out this session but not yet visible in the docs list
    /// (two quick creates must not collide while the first POST is in flight).</summary>
    private uint WbReservedEntry;
    private uint WbReservedGuid;
    private uint WbReservedMenu;

    private async Task<WorldPackClient.Reply> WbWithTrainerCopy(uint entry, uint copyFrom, JsonArray items, string label)
    {
        var csv = await _wbClient.ExportCsvAsync(SuiWebAppUrl, "npc_trainer", "entry", copyFrom.ToString(CultureInfo.InvariantCulture));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            var head = lines[0].Split(',');
            for (int l = 1; l < lines.Length; l++)
            {
                var f = lines[l].Split(',');
                var row = new JsonObject();
                for (int c = 0; c < head.Length && c < f.Length; c++)
                    row[head[c].Trim('"')] = long.TryParse(f[c].Trim('"'), out long n) ? n : f[c].Trim('"');
                row["entry"] = entry;
                items.Add(new JsonObject { ["kind"] = "dbrow:npc_trainer", ["body"] = row });
            }
        }
        return await _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label + $" + {lines.Length - 1} trainer spell(s)", items.ToJsonString());
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
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("loading pack content..."); return; }
        var templates = _wbDocs.OfType<JsonObject>().Where(d => (string?)d["kind"] == "dbrow:creature_template").ToList();
        if (templates.Count == 0) { ImGui.TextDisabled("No pack NPCs yet - create one above."); return; }
        string current = templates.FirstOrDefault(t => WbScalar(t["body"]!["entry"]!) == _wbSpawnEntry.ToString())?["body"]?["name"]?.ToString() ?? "(choose)";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("NPC##wb-spawn", current))
        {
            foreach (var t in templates)
            {
                uint e = uint.Parse(WbScalar(t["body"]!["entry"]!));
                if (ImGui.Selectable($"{t["body"]!["name"]}  ({e})", e == _wbSpawnEntry)) _wbSpawnEntry = e;
            }
            ImGui.EndCombo();
        }
        ImGui.RadioButton("Single##wb-sm", ref _wbSpawnMode, 0); ImGui.SameLine();
        ImGui.RadioButton("Linked pack##wb-sm", ref _wbSpawnMode, 1); ImGui.SameLine();
        ImGui.RadioButton("Patrol##wb-sm", ref _wbSpawnMode, 2);
        if (_wbSpawnMode != 0)
        {
            string member = _wbSpawnMemberEntry == 0 ? "(same NPC)" : templates.FirstOrDefault(t => WbScalar(t["body"]!["entry"]!) == _wbSpawnMemberEntry.ToString())?["body"]?["name"]?.ToString() ?? "(same NPC)";
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.BeginCombo(_wbSpawnMode == 1 ? "Members##wb-spawn" : "Followers##wb-spawn", member))
            {
                if (ImGui.Selectable("(same NPC)", _wbSpawnMemberEntry == 0)) _wbSpawnMemberEntry = 0;
                foreach (var t in templates)
                {
                    uint e = uint.Parse(WbScalar(t["body"]!["entry"]!));
                    if (ImGui.Selectable($"{t["body"]!["name"]}  ({e})", e == _wbSpawnMemberEntry)) _wbSpawnMemberEntry = e;
                }
                ImGui.EndCombo();
            }
        }
        if (_wbSpawnMode == 1)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderInt("Size##wb-pack", ref _wbPackCount, 2, 6);
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderFloat("Spread##wb-pack", ref _wbPackRadius, 2f, 12f, "%.0f yd");
            ImGui.TextDisabled("Click the ground: the pack stands there, linked (aggro, evade and respawn together).");
        }
        else if (_wbSpawnMode == 2)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.SliderInt("Followers##wb-patrol", ref _wbPatrolFollowers, 0, 4);
            ImGui.TextDisabled($"Click the ground to add route points ({_wbPatrolPoints.Count}); the route loops back to the first.");
            if (CreatorButton("Save patrol") && _wbPatrolPoints.Count >= 2 && _wbSpawnEntry != 0)
            {
                if (WbPatrolItems(_wbSpawnEntry, _wbSpawnMemberEntry == 0 ? _wbSpawnEntry : _wbSpawnMemberEntry, _wbPatrolFollowers,
                        _wbPatrolPoints.Select(p => new Vector2(p.X, p.Y)).ToList()) is { } patrol)
                { WbPost($"patrol {_wbSpawnEntry}", patrol); _wbPatrolPoints.Clear(); }
            }
            ImGui.SameLine();
            if (CreatorButton("Undo point") && _wbPatrolPoints.Count > 0) _wbPatrolPoints.RemoveAt(_wbPatrolPoints.Count - 1);
            ImGui.SameLine();
            if (CreatorButton("Clear route")) _wbPatrolPoints.Clear();
        }
        ImGui.Checkbox(_wbSpawnMode == 2 ? "Click the ground to add route points" : "Click the ground to place", ref _wbSpawnArmed);
        if (_wbSpawnArmed && _wbTool != WorldBuilderTool.Select) WbSetTool(WorldBuilderTool.Select);
        int spawns = _wbDocs.OfType<JsonObject>().Count(d => (string?)d["kind"] == "dbrow:creature");
        ImGui.TextDisabled($"{spawns} pack spawn(s); they are previewed here and go live on publish.");
    }

    /// <summary>Select-tool click while the spawn tool is armed: one creature row, a linked pack, or a patrol point.</summary>
    private bool WbTrySpawnClick(Vector3 at)
    {
        if (!_wbSpawnArmed || _wbSpawnEntry == 0 || _wbPackId == 0 || _controller is null) return false;
        if (_wbSpawnMode == 2) { _wbPatrolPoints.Add(at); return true; }
        if (_wbSpawnMode == 1)
        {
            var entries = new List<uint> { _wbSpawnEntry, _wbSpawnMemberEntry == 0 ? _wbSpawnEntry : _wbSpawnMemberEntry };
            if (WbPackItems(entries, new Vector2(at.X, at.Y), _wbPackRadius, _wbPackCount) is { } pack) WbPost($"pack {_wbSpawnEntry} x{_wbPackCount}", pack);
            return true;
        }
        float facing = MathF.Atan2(_controller.Position.Y - at.Y, _controller.Position.X - at.X);
        var items = new JsonArray { WbSpawnRow(_wbSpawnEntry, at, facing) };
        WbOp($"spawn {_wbSpawnEntry}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"spawn NPC {_wbSpawnEntry}", items.ToJsonString()),
            _ => WbRequestDocs());
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
        if (_wbDocsTask is not { IsCompleted: true } t) return;
        _wbDocsTask = null;
        if (t.IsFaulted) { _wbMessage = "docs: " + t.Exception?.GetBaseException().Message; return; }
        try { _wbDocs = JsonNode.Parse(t.Result)?["docs"] as JsonArray; }
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
        if (_wbDocs is null || _net is not null) return;
        var displays = _wbDocs.OfType<JsonObject>().Where(d => (string?)d["kind"] == "dbrow:creature_template")
            .ToDictionary(d => WbScalar(d["body"]!["entry"]!), d => (display: uint.Parse(WbScalar(d["body"]!["display_id1"]!)),
                scale: float.Parse(WbScalar(d["body"]!["display_scale1"] ?? 1), CultureInfo.InvariantCulture),
                name: d["body"]!["name"]!.ToString()));
        var want = new HashSet<uint>();
        foreach (var d in _wbDocs.OfType<JsonObject>().Where(d => (string?)d["kind"] == "dbrow:creature"))
        {
            var b = d["body"]!;
            if (int.Parse(WbScalar(b["map"]!)) != _config.Start.Map) continue;
            uint guid = uint.Parse(WbScalar(b["guid"]!));
            want.Add(guid);
            ulong synthetic = 0xB1B1_0000_0000_0000UL | guid;
            if (_wbSpawnPreview.ContainsKey(synthetic)) continue;
            if (!displays.TryGetValue(WbScalar(b["id"]!), out var look)) continue;
            _entities.AddSynthetic(new WorldEntity
            {
                Guid = synthetic,
                Type = ObjectTypeId.Unit,
                Fields = ObjectFields.ForSyntheticUnit((int)look.display, look.scale),
                Position = new Vector3(float.Parse(WbScalar(b["position_x"]!), CultureInfo.InvariantCulture),
                    float.Parse(WbScalar(b["position_y"]!), CultureInfo.InvariantCulture),
                    float.Parse(WbScalar(b["position_z"]!), CultureInfo.InvariantCulture)),
                Orientation = float.Parse(WbScalar(b["orientation"]!), CultureInfo.InvariantCulture),
            });
            _wbSpawnPreview[synthetic] = guid;
        }
        foreach (var (synthetic, guid) in _wbSpawnPreview.ToList())
            if (!want.Contains(guid)) { _entities.RemoveSynthetic(synthetic); _wbSpawnPreview.Remove(synthetic); }
    }
}
