using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly byte[] _wbTrainerSearch = new byte[80], _wbEquipmentSearch = new byte[80];
    private string _wbTrainerSearchLast = "\u0001", _wbEquipmentSearchLast = "\u0001", _wbTrainerSourceName = "";
    private List<CreatorCreatureTable.Creature>? _wbTrainerHits;
    private List<CreatorItemTable.Item>? _wbEquipmentHits;
    private int _wbEquipmentSlot;
    private readonly Dictionary<uint, string> _wbNpcItemNames = new();

    private bool WbNpcFormSection(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        bool open = ImGui.CollapsingHeader(label, flags);
        ObserveWorldBuilderUiItem(label);
        return open;
    }

    private string WbNpcItemLabel(int entry)
    {
        if (entry <= 0) return "Empty";
        uint id = (uint)entry;
        if (!_wbNpcItemNames.TryGetValue(id, out var name)) _wbNpcItemNames[id] = name = WbItemName(id);
        return name;
    }

    private void DrawWbNpcSaveButton()
    {
        bool ready = _wbPackId != 0 && WbText(_wbNpcName).Length > 0 && !_wbNpcSaving && _wbNpcLoadTask is null && !_wbNpcLoadFailed && _wbNpcCopyBlock.Length == 0;
        ImGui.BeginDisabled(!ready);
        if (CreatorButton(_wbNpcStockSourceEntry != 0 ? "Save this NPC in pack" : _wbNpcEditEntry != 0 ? "Save NPC changes" : "Create NPC")) WbCreateNpc();
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled(_wbNpcSaving ? "Saving..." : "Goes live after publishing");
    }

    private void DrawWbNpcAppearance()
    {
        float w = CreatorControlWidth;
        ImGui.SetNextItemWidth(w);
        ImGui.InputText("Find a look", _wbNpcSearch, (uint)_wbNpcSearch.Length);
        ObserveWorldBuilderUiItem("NPC appearance search");
        string q = WbText(_wbNpcSearch);
        if (q != _wbNpcSearchLast) { _wbNpcSearchLast = q; _wbNpcHits = q.Length >= 2 ? _creatorCreatures?.Search(q, 40) : null; }
        if (_wbNpcHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-npc-hits", new Vector2(-1, 100 * CreatorUiScale)))
        {
            foreach (var c in hits)
            {
                if (ImGui.Selectable($"{c.Name}{(c.SubName.Length > 0 ? " <" + c.SubName + ">" : "")}##look{c.Entry}"))
                { _wbNpcDisplay = c.DisplayId; _wbNpcScale = c.Scale > 0 ? c.Scale : 1; SpawnCreatorCreature(c.Name, c.DisplayId, c.Scale); }
                ObserveWorldBuilderUiItem("Look: " + c.Name);
            }
            ImGui.EndListBox();
        }
        if (CreatorButton("Preview appearance")) SpawnCreatorCreature(WbText(_wbNpcName), _wbNpcDisplay, _wbNpcScale);
        ImGui.SetNextItemWidth(w * .65f); ImGui.SliderFloat("Size", ref _wbNpcScale, .2f, 4, "%.2fx");
        ImGui.SetNextItemWidth(w * .65f); ImGui.InputInt("Level", ref _wbNpcLevel);
        _wbNpcLevel = Math.Clamp(_wbNpcLevel, 1, 63); _wbNpcLevelMax = Math.Max(_wbNpcLevelMax, _wbNpcLevel);
        ImGui.SetNextItemWidth(w * .65f); ImGui.Combo("Rank", ref _wbNpcRank, "Normal\0Elite\0Rare elite\0Boss\0Rare\0");
        if (ImGui.TreeNode("Advanced appearance"))
        {
            int display = (int)_wbNpcDisplay;
            if (ImGui.InputInt("Display ID", ref display)) _wbNpcDisplay = (uint)Math.Max(1, display);
            ImGui.InputInt("Maximum level", ref _wbNpcLevelMax); _wbNpcLevelMax = Math.Clamp(_wbNpcLevelMax, _wbNpcLevel, 63);
            ImGui.TreePop();
        }
    }

    private void DrawWbNpcServices()
    {
        string disposition = _wbNpcFaction switch { 35 => "Friendly", 16 => "Hostile", 7 => "Neutral", _ => "Existing faction (custom)" };
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Disposition", disposition))
        {
            foreach (var (id, label) in new[] { (35, "Friendly"), (16, "Hostile"), (7, "Neutral") })
                if (ImGui.Selectable(label, _wbNpcFaction == id)) _wbNpcFaction = id;
            ImGui.EndCombo();
        }
        int i = 0;
        foreach (var (flag, label) in WbNpcFlags)
        {
            bool on = (_wbNpcFlags & flag) != 0;
            if (i++ % 2 != 0) ImGui.SameLine();
            if (ImGui.Checkbox(label + "##npcflag", ref on)) _wbNpcFlags = on ? _wbNpcFlags | flag : _wbNpcFlags & ~flag;
            ObserveWorldBuilderUiItem("NPC role " + label);
        }
        if (ImGui.TreeNode("Advanced faction")) { ImGui.InputInt("Faction ID", ref _wbNpcFaction); ImGui.TreePop(); }
        if ((_wbNpcFlags & 16) != 0) DrawWbNpcTrainer();
        if ((_wbNpcFlags & 4) != 0) DrawWbNpcInventory();
    }

    private void DrawWbNpcTrainer()
    {
        ImGui.Separator(); ImGui.TextUnformatted("Trainer");
        ImGui.SetNextItemWidth(CreatorControlWidth); ImGui.Combo("Training", ref _wbNpcTrainerType, "Class\0Mount\0Profession\0Pet\0");
        var classes = new[] { (0, "Any class"), (1, "Warrior"), (2, "Paladin"), (3, "Hunter"), (4, "Rogue"), (5, "Priest"), (7, "Shaman"), (8, "Mage"), (9, "Warlock"), (11, "Druid") };
        string current = classes.FirstOrDefault(c => c.Item1 == _wbNpcTrainerClass).Item2 ?? "Existing class";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Class", current))
        {
            foreach (var (id, name) in classes) if (ImGui.Selectable(name, _wbNpcTrainerClass == id)) _wbNpcTrainerClass = id;
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(_wbNpcCopyTrainer == 0 ? $"Keep current spell list ({WbNpcSourceRows("dbrow:npc_trainer").Count()} spells)." : "Copy spell list from " + _wbTrainerSourceName);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Find source trainer", _wbTrainerSearch, (uint)_wbTrainerSearch.Length);
        ObserveWorldBuilderUiItem("NPC trainer search");
        string q = WbText(_wbTrainerSearch);
        if (q != _wbTrainerSearchLast) { _wbTrainerSearchLast = q; _wbTrainerHits = q.Length >= 2 ? _creatorCreatures?.Search(q, 30) : null; }
        if (_wbTrainerHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-trainer-hits", new Vector2(-1, 90 * CreatorUiScale)))
        {
            foreach (var c in hits)
            {
                if (ImGui.Selectable($"{c.Name} <{c.SubName}>##trainer{c.Entry}")) { _wbNpcCopyTrainer = (int)c.Entry; _wbTrainerSourceName = c.Name; }
                ObserveWorldBuilderUiItem("Trainer: " + c.Name);
            }
            ImGui.EndListBox();
        }
        if (_wbNpcCopyTrainer != 0 && CreatorButton("Keep current spells")) _wbNpcCopyTrainer = 0;
        if (ImGui.TreeNode("Advanced trainer"))
        {
            ImGui.InputInt("Source NPC entry", ref _wbNpcCopyTrainer);
            ImGui.InputInt("Trainer class ID", ref _wbNpcTrainerClass);
            ImGui.TreePop();
        }
    }

    private void DrawWbNpcInventory()
    {
        _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
        ImGui.Separator(); ImGui.TextUnformatted($"Shop inventory ({_wbNpcVendor.Count})");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Find item to sell", _wbItemSearch, (uint)_wbItemSearch.Length);
        ObserveWorldBuilderUiItem("NPC vendor item search");
        string q = WbText(_wbItemSearch);
        if (q != _wbItemSearchLast) { _wbItemSearchLast = q; _wbItemHits = q.Length >= 2 ? _wbItems?.Search(q, -2, 30) : null; }
        if (_wbItemHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-item-hits", new Vector2(-1, 90 * CreatorUiScale)))
        {
            foreach (var item in hits)
            {
                if (ImGui.Selectable($"{item.Name}##vendor{item.Entry}") && _wbNpcVendor.All(v => v.Item != item.Entry)) _wbNpcVendor.Add((item.Entry, item.Name));
                ObserveWorldBuilderUiItem("Sell: " + item.Name);
            }
            ImGui.EndListBox();
        }
        for (int i = 0; i < _wbNpcVendor.Count; i++)
        {
            var item = _wbNpcVendor[i]; ImGui.PushID((int)item.Item);
            bool remove = ImGui.SmallButton("Remove"); ObserveWorldBuilderUiItem("Remove vendor item " + item.Item);
            if (remove) { _wbNpcVendor.RemoveAt(i); ImGui.PopID(); break; }
            ImGui.SameLine(); ImGui.TextWrapped(item.Name);
            DrawWbVendorStock(item.Item); ImGui.PopID();
        }
    }

    private void DrawWbNpcEquipment()
    {
        _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
        ImGui.TextUnformatted("Main hand: " + WbNpcItemLabel(_wbNpcMainHand));
        ImGui.TextUnformatted("Off hand: " + WbNpcItemLabel(_wbNpcOffHand));
        ImGui.RadioButton("Main hand", ref _wbEquipmentSlot, 0); ImGui.SameLine(); ImGui.RadioButton("Off hand", ref _wbEquipmentSlot, 1);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Find equipment", _wbEquipmentSearch, (uint)_wbEquipmentSearch.Length);
        ObserveWorldBuilderUiItem("NPC equipment search");
        string q = WbText(_wbEquipmentSearch);
        if (q != _wbEquipmentSearchLast)
        {
            _wbEquipmentSearchLast = q;
            _wbEquipmentHits = q.Length >= 2 ? _wbItems?.Search(q, -1, 80).Where(i => i.InventoryType is 13 or 14 or 15 or 17 or 21 or 22 or 23 or 25 or 26).Take(30).ToList() : null;
        }
        if (_wbEquipmentHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-equipment-hits", new Vector2(-1, 90 * CreatorUiScale)))
        {
            foreach (var item in hits)
            {
                if (ImGui.Selectable($"{item.Name}##equip{item.Entry}"))
                { if (_wbEquipmentSlot == 0) _wbNpcMainHand = (int)item.Entry; else _wbNpcOffHand = (int)item.Entry; }
                ObserveWorldBuilderUiItem("Equip: " + item.Name);
            }
            ImGui.EndListBox();
        }
        if (CreatorButton("Clear this hand")) { if (_wbEquipmentSlot == 0) _wbNpcMainHand = 0; else _wbNpcOffHand = 0; }
        ImGui.TextUnformatted("Greeting");
        ImGui.InputTextMultiline("##wb-npc-gossip", ref _wbNpcGossip, 600, new Vector2(-1, 70 * CreatorUiScale));
        ObserveWorldBuilderUiItem("NPC greeting");
        if (ImGui.TreeNode("Advanced equipment"))
        {
            ImGui.InputInt("Main hand item ID", ref _wbNpcMainHand); ImGui.InputInt("Off hand item ID", ref _wbNpcOffHand);
            ImGui.TreePop();
        }
    }
}
