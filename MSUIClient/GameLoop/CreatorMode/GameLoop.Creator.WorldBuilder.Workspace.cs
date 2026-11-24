using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string _wbPage = "terrain";
    private static readonly (string Key, string Label)[] WbPages =
    {
        ("terrain", "Terrain"), ("paths", "Paths"), ("buildings", "Buildings"), ("gameobjects", "Objects"),
        ("npcs", "NPCs"), ("quests", "Quests"), ("publish", "Publish"), ("advanced", "More"),
    };
    private int _wbNpcPage;
    private int _wbAdvancedPage;
    private bool _wbShowHistory;
    private MSUIClient.Net.WorldPackClient.Placement? _wbPlacementDraft;
    private string _wbPlacementDraftBase = "";
    private bool _wbAllPlacementPacks;

    private void WbOpenPage(string page)
    {
        bool arriving = _wbPage != page;
        if (arriving) WbSetTool(WorldBuilderTool.None);
        _wbPage = page;
        if (page == "npcs" && arriving) _wbNpcPage = 0;   // re-opening the NPC page keeps its sub-tab (Place NPCs after Create)
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
        // The pack list takes what "Packs..." leaves; too narrow for both and the button wraps under it, never off the edge.
        float packsW = CreatorButtonWidth("Packs...");
        ImGui.SetNextItemWidth(MathF.Max(160f * CreatorUiScale,
            ImGui.GetContentRegionAvail().X - packsW - ImGui.GetStyle().ItemSpacing.X));
        var packs = _wbState?.Packs;
        string selected = packs?.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "Choose a content pack";
        if (ImGui.BeginCombo("##wb-active-pack", selected))
        {
            foreach (var pack in packs ?? new())
                if (ImGui.Selectable(pack.Name + (pack.Enabled ? "" : " (draft only)") + "##wbpack" + pack.Id, pack.Id == _wbPackId))
                {
                    WbSetTool(WorldBuilderTool.None);
                    _wbPackId = pack.Id;
                    WbRequestState();
                    WbRequestDocs();
                }
            ImGui.EndCombo();
        }
        CreatorSameLineIfFits(packsW);
        if (CreatorButton("Packs...")) WbOpenPage("packs");
        DrawWbViewControls();

        if (_wbTool != WorldBuilderTool.None || WbNpcHasActiveAction || _wbMoveArmed)
        {
            if (CreatorButton("Cancel tool (Esc)")) ConsumeWorldBuilderEscape();
            ImGui.SameLine();
            ImGui.TextWrapped(_wbNpcMoveArmed ? "Moving NPC" : _wbSpawnArmed ? "Placing NPCs" : _wbMoveArmed || _wbGoMoveArmed ? "Moving selection"
                : _wbGoPicking ? "Selecting an object" : _wbTool switch
                {
                    WorldBuilderTool.Sculpt => "Sculpting", WorldBuilderTool.Place => "Placing a model",
                    WorldBuilderTool.Path => "Drawing a path", WorldBuilderTool.GameObject => "Placing objects",
                    WorldBuilderTool.Pick => "Picking: " + _wbPickWhat,
                    _ => "Selecting",
                });
        }

        // Page chips: one text line tall, wrapping onto a second line when narrow, all always visible. The old three rows
        // of big buttons filled a default-size panel; a tab strip hid half the pages off its edge (2026-10-14 tours).
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var pad = ImGui.GetStyle().FramePadding;
        bool firstPage = true;
        foreach (var (key, label) in WbPages)
        {
            var size = ImGui.CalcTextSize(label) + new Vector2(18f * CreatorUiScale, 6f * CreatorUiScale);
            if (!firstPage && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + size.X < right) ImGui.SameLine();
            bool active = _wbPage == key;
            // Atlas look: the open page is a gold tint with gold text (.qa-seg button.on); classic: the red plate.
            int pushed = 0;
            if (active && CreatorAtlasLook) { ImGui.PushStyleColor(ImGuiCol.Header, AtlasA(AtlasGold, 0.16f)); ImGui.PushStyleColor(ImGuiCol.Text, AtlasGold); pushed = 2; }
            else if (active) { ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.62f, 0.12f, 0.08f, 1f)); pushed = 1; }
            if (ImGui.Selectable(label + "##wb-page-" + key, active, ImGuiSelectableFlags.None, size)) WbOpenPage(key);
            if (pushed > 0) ImGui.PopStyleColor(pushed);
            ObserveWorldBuilderUiItem(label);
            firstPage = false;
        }
        ImGui.Separator();
        if (_wbOps.Count > 0) ImGui.TextColored(new Vector4(1f, .82f, .3f, 1f), "Saving changes...");
        else if (_wbMessage.Length > 0)
        {
            int saved = _wbMessage.IndexOf(": saved as op", StringComparison.Ordinal);   // "...: saved as op #N, audit #M" -> no numbers
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
            case "paths": DrawWbPathSection(); break;
            case "gameobjects": DrawWbGameObjectSection(); break;
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
                ImGui.Combo("Tool", ref _wbAdvancedPage, new[] { "Maps and dungeons", "Regions and continents", "Check world content", "Pack data" }, 4);
                ImGui.Separator();
                // Reuse registered bodies so there is one implementation for each tool.
                string id = new[] { "wb-maps", "wb-region", "wb-verify", "wb-docs" }[_wbAdvancedPage];
                var definition = _creatorSectionDefs.FirstOrDefault(d => d.Panel == "World" && d.Id == id);
                definition.Body?.Invoke();
                break;
        }
    }
}
