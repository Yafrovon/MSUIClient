using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string _wbPage = "terrain";
    private int _wbNpcPage;
    private int _wbAdvancedPage;
    private bool _wbShowHistory;
    private MSUIClient.Net.WorldPackClient.Placement? _wbPlacementDraft;
    private string _wbPlacementDraftBase = "";
    private bool _wbAllPlacementPacks;

    private void WbOpenPage(string page)
    {
        if (_wbPage != page) WbSetTool(WorldBuilderTool.None);
        _wbPage = page;
        if (page == "npcs") _wbNpcPage = 0;
        _creatorPanel = CreatorPanel.World;
    }

    // The same task-oriented surface is used in the floating window and the deck.
    // Keep the pack, action state and navigation visible while the form scrolls.
    private void DrawWbWorkspaceHeader()
    {
        if (SuiWebAppUrl.Length == 0)
        {
            ImGui.TextWrapped("Connect your Web App on the login screen to save world edits.");
            return;
        }
        ImGui.SetNextItemWidth(MathF.Max(160f, ImGui.GetContentRegionAvail().X - 105f * CreatorUiScale));
        var packs = _wbState?.Packs;
        string selected = packs?.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "Choose a content pack";
        if (ImGui.BeginCombo("##wb-active-pack", selected))
        {
            foreach (var pack in packs ?? new())
                if (ImGui.Selectable(pack.Name + (pack.Enabled ? "" : " (draft only)"), pack.Id == _wbPackId))
                {
                    WbSetTool(WorldBuilderTool.None);
                    _wbPackId = pack.Id;
                    WbRequestState();
                    WbRequestDocs();
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (CreatorButton("Packs...")) WbOpenPage("packs");

        if (_wbTool != WorldBuilderTool.None || WbNpcHasActiveAction || _wbMoveArmed)
        {
            if (CreatorButton("Cancel tool (Esc)")) ConsumeWorldBuilderEscape();
            ImGui.SameLine();
            ImGui.TextWrapped(_wbNpcMoveArmed ? "Moving NPC" : _wbSpawnArmed ? "Placing NPCs" : _wbMoveArmed ? "Moving selection" : _wbTool.ToString());
        }
        else ImGui.TextWrapped("Choose a task. Edits stay drafts until published.");

        var pages = new (string Key, string Label)[]
        {
            ("terrain", "Terrain"), ("buildings", "Buildings"), ("npcs", "NPCs"),
            ("quests", "Quests"), ("publish", "Publish"), ("advanced", "More")
        };
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        bool first = true;
        foreach (var (key, label) in pages)
        {
            float width = ImGui.CalcTextSize(label).X + 36f * CreatorUiScale * CreatorButtonMul;
            if (!first && ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X + width < right)
                ImGui.SameLine();
            bool active = _wbPage == key;
            if (CreatorButton(label)) WbOpenPage(key);
            if (active) ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - Vector2.One,
                ImGui.GetItemRectMax() + Vector2.One, 0xff33ddee, 3f, ImDrawFlags.None, 2f);
            first = false;
        }
        ImGui.Separator();
        if (_wbOps.Count > 0) ImGui.TextColored(new Vector4(1f, .82f, .3f, 1f), "Saving changes...");
        else if (_wbMessage.Length > 0)
        {
            int saved = _wbMessage.IndexOf(": saved as op", StringComparison.Ordinal);
            ImGui.TextWrapped(saved >= 0 ? _wbMessage[..saved] + " saved. Publish to apply in game." : _wbMessage);
        }
    }

    private bool WbRequirePack()
    {
        if (_wbPackId > 0 && _wbState?.Packs.Any(p => p.Id == _wbPackId) == true) return true;
        ImGui.TextWrapped(_wbStateTask is not null ? "Loading content packs..." : "Choose a pack above, or create one to hold your edits.");
        if (CreatorButton("Choose or create a pack")) WbOpenPage("packs");
        return false;
    }

    private void DrawWbWorkspaceBody()
    {
        if (_wbPage == "packs") { DrawWbPackSection(); return; }
        if (!WbRequirePack()) return;
        switch (_wbPage)
        {
            case "terrain": DrawWbSculptSection(); break;
            case "buildings":
                if (ImGui.BeginTabBar("##wb-buildings-tabs"))
                {
                    bool place = ImGui.BeginTabItem("Place a building or prop");
                    ObserveWorldBuilderUiItem("Place a building or prop");
                    if (place) { DrawWbPlaceSection(); ImGui.EndTabItem(); }
                    bool edit = ImGui.BeginTabItem("Edit placed objects");
                    ObserveWorldBuilderUiItem("Edit placed objects");
                    if (edit) { DrawWbListSection(); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
                break;
            case "npcs":
                ImGui.RadioButton("Edit / create NPC", ref _wbNpcPage, 0);
                ObserveWorldBuilderUiItem("Edit / create NPC");
                ImGui.SameLine();
                ImGui.RadioButton("Place NPCs", ref _wbNpcPage, 1);
                ObserveWorldBuilderUiItem("Place NPCs");
                ImGui.Separator();
                if (_wbNpcPage == 0) DrawWbNpcSection(); else DrawWbSpawnSection();
                break;
            case "quests": DrawWbQuestSection(); break;
            case "publish": DrawWbPublishSection(); break;
            case "advanced":
                ImGui.TextWrapped("Tools for larger areas and checking your work.");
                ImGui.SetNextItemWidth(CreatorControlWidth);
                ImGui.Combo("Tool", ref _wbAdvancedPage, new[] { "Maps and dungeons", "Regions and paths", "Check world content", "Pack data" }, 4);
                ImGui.Separator();
                // Reuse registered bodies so there is one implementation for each tool.
                string id = new[] { "wb-maps", "wb-region", "wb-verify", "wb-docs" }[_wbAdvancedPage];
                var definition = _creatorSectionDefs.FirstOrDefault(d => d.Panel == "World" && d.Id == id);
                definition.Body?.Invoke();
                break;
        }
    }
}
