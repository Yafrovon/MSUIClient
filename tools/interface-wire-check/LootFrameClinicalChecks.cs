using MSUIClient;
using MSUIClient.Engine.UI;

internal static class LootFrameClinicalChecks
{
    public static void Run()
    {
        LootFrameUiLaw.OpenPresentation empty = LootFrameUiLaw.OnShow(1, 0, 0);
        LootFrameUiLaw.OpenPresentation emptyFishing = LootFrameUiLaw.OnShow(3, 0, 0);
        LootFrameUiLaw.OpenPresentation fishing = LootFrameUiLaw.OnShow(3, 1, 0);
        LootFrameUiLaw.OpenPresentation corpse = LootFrameUiLaw.OnShow(1, 1, 0);
        LootFrameUiLaw.OpenPresentation coins = LootFrameUiLaw.OnShow(3, 0, 1);
        Check(empty.SoundCue == LootFrameUiLaw.EmptyOpenSound &&
              empty.OverlayPath == LootFrameUiLaw.CorpseOverlay &&
              emptyFishing == empty &&
              fishing.SoundCue == LootFrameUiLaw.FishingOpenSound &&
              fishing.OverlayPath == LootFrameUiLaw.FishingOverlay &&
              corpse.SoundCue is null && corpse.OverlayPath == LootFrameUiLaw.CorpseOverlay &&
              coins.SoundCue == LootFrameUiLaw.FishingOpenSound,
            "LootFrame OnShow empty/fishing precedence drift");

        Check(LootFrameUiLaw.FrameOffset == new System.Numerics.Vector2(16, 12) &&
              LootFrameUiLaw.TraceAbsoluteOffset == new System.Numerics.Vector2(16, 116) &&
              LootFrameUiLaw.Frame == new LootFrameUiLaw.LogicalRect(0, 0, 256, 256) &&
              LootFrameUiLaw.PortraitOverlay ==
                  new LootFrameUiLaw.LogicalRect(10, 8, 58, 58) &&
              LootFrameUiLaw.TitleCenter == new System.Numerics.Vector2(116, 26) &&
              LootFrameUiLaw.Row(0) == new LootFrameUiLaw.LogicalRect(24, 80, 160, 37) &&
              LootFrameUiLaw.Row(3) == new LootFrameUiLaw.LogicalRect(24, 203, 160, 37) &&
              LootFrameUiLaw.RowTooltipSeat(
                  new System.Numerics.Vector2(24, 80), 2) ==
                  new LootFrameUiLaw.TooltipSeat(
                      new System.Numerics.Vector2(98, 80),
                      new System.Numerics.Vector2(0, 1)) &&
              LootFrameUiLaw.RowNamePlate ==
                  new LootFrameUiLaw.LogicalRect(30, -12.5f, 130, 62) &&
              LootFrameUiLaw.RowNameBox ==
                  new LootFrameUiLaw.LogicalRect(45, -.5f, 93, 38) &&
              LootFrameUiLaw.StackCountBox ==
                  new LootFrameUiLaw.LogicalRect(4, 22.5f, 34, 12) &&
              LootFrameUiLaw.TitleFont == "GameFontNormal" &&
              LootFrameUiLaw.NameFont == "GameFontNormal" &&
              LootFrameUiLaw.CountFont == "NumberFontNormal" &&
              LootFrameUiLaw.NameLineMin(new System.Numerics.Vector2(24, 80), 2, 0, 2, 24) ==
                  new System.Numerics.Vector2(114, 93) &&
              LootFrameUiLaw.PagerUp == new LootFrameUiLaw.LogicalRect(25, 208, 32, 32) &&
              LootFrameUiLaw.PagerDown == new LootFrameUiLaw.LogicalRect(111, 208, 32, 32) &&
              LootFrameUiLaw.CloseArt == new LootFrameUiLaw.LogicalRect(159, 10, 32, 32) &&
              LootFrameUiLaw.CloseHit == new LootFrameUiLaw.LogicalRect(165, 16, 20, 20) &&
              LootFrameUiLaw.PagerPath(true, false).EndsWith("ScrollUp-Disabled",
                  StringComparison.Ordinal),
            "LootFrame authored window/row/pager geometry drift");

        IReadOnlyList<string> wrappedName = LootFrameUiLaw.WrapName(
            "Plans Thorium Shield Spike", 55, 2, candidate => candidate.Length * 5);
        Check(wrappedName.SequenceEqual(new[] { "Plans", "Thorium" }),
            "LootFrame fixed name-box wrapping drift");

        Check(LootFrameUiLaw.ItemLink(2589, "Wool Cloth", 1) ==
                  "|cffffffff|Hitem:2589:0:0:0|h[Wool Cloth]|h|r" &&
              LootFrameUiLaw.ClickAction(true, true, false, false, false, true, true) ==
                  LootFrameUiLaw.RowClickAction.DressUp &&
              LootFrameUiLaw.ClickAction(true, false, true, false, true, true, true) ==
                  LootFrameUiLaw.RowClickAction.InsertChat &&
              LootFrameUiLaw.ClickAction(true, false, true, false, false, true, true) ==
                  LootFrameUiLaw.RowClickAction.None &&
              LootFrameUiLaw.ClickAction(true, false, false, true, true, true, true) ==
                  LootFrameUiLaw.RowClickAction.None &&
              LootFrameUiLaw.ClickAction(true, false, true, false, true, false, false) ==
                  LootFrameUiLaw.RowClickAction.None &&
              LootFrameUiLaw.ClickAction(true, false, false, false, false, false, false) ==
                  LootFrameUiLaw.RowClickAction.Loot,
            "LootFrame row modifier/right-click fork drift");

        Check(LootLatchLaw.AdmitResponse(0x10, 0x10, 1) ==
                  new LootLatchLaw.ResponsePlan(true, false, 0x10) &&
              LootLatchLaw.AdmitResponse(0, 0x20, 2) ==
                  new LootLatchLaw.ResponsePlan(true, false, 0x20) &&
              LootLatchLaw.AdmitResponse(0, 0x20, 3).Accept &&
              LootLatchLaw.AdmitResponse(0, 0x20, 4).Accept &&
              LootLatchLaw.AdmitResponse(0, 0x20, 1) ==
                  new LootLatchLaw.ResponsePlan(false, true, 0) &&
              LootLatchLaw.AdmitResponse(0x10, 0x20, 1) ==
                  new LootLatchLaw.ResponsePlan(false, true, 0) &&
              LootLatchLaw.ShouldKneel(0x10, LootLatchLaw.TargetKind.GameObject, 3, 0) &&
              LootLatchLaw.ShouldKneel(0x10, LootLatchLaw.TargetKind.GameObject, 17, 0) &&
              LootLatchLaw.ShouldKneel(0x10, LootLatchLaw.TargetKind.Unit, 0, 1) &&
              // A bag item (clam / lockbox) is a menu action with no world loot emote, so it
              // must NOT kneel, unlike a corpse or a ground object. This assertion previously
              // demanded the opposite.
              !LootLatchLaw.ShouldKneel(0x10, LootLatchLaw.TargetKind.Item, 0, 0) &&
              !LootLatchLaw.ShouldKneel(0, LootLatchLaw.TargetKind.Unit, 0, 0) &&
              LootLatchLaw.ClearFor(0x10, 0x10) == 0 &&
              LootLatchLaw.ClearFor(0x10, 0x20) == 0x10,
            "loot latch admission/kneel predicate drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        string casting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        Check(runtime.Contains("PlayUiSound(cue, LootFrameUiLaw.SoundCategory)",
                  StringComparison.Ordinal) &&
              runtime.Contains("LootLatchLaw.AdmitResponse(", StringComparison.Ordinal) &&
              runtime.Contains("LootLatchLaw.ClearFor(_lootPendingGuid, source)",
                  StringComparison.Ordinal) &&
              runtime.Contains("bool kneeling = LootLatchLaw.ShouldKneel(",
                  StringComparison.Ordinal) &&
              !runtime.Contains("TriggerOneShot(50)", StringComparison.Ordinal) &&
              casting.Contains("_lootPendingGuid = lootTarget", StringComparison.Ordinal) &&
              casting.Contains("effect => effect == 33", StringComparison.Ordinal) &&
              character.Contains("if (LootKneel)", StringComparison.Ordinal) &&
              character.Contains("BaseAnimationTrack, 50, false, 0", StringComparison.Ordinal) &&
              runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[9], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.FrameOffset", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.Row(visual)", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.TitleFont", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.NameFont", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.CountFont", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.NameLineMin", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.CountRightTop", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.CloseHit", StringComparison.Ordinal) &&
              runtime.Contains("ImGui.IsItemClicked(ImGuiMouseButton.Right)",
                  StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.ClickAction", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.ItemLink", StringComparison.Ordinal) &&
              runtime.Contains("LootFrameUiLaw.RowTooltipSeat(rowMin, s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("nextWindowPivot: tooltipSeat.Pivot",
                  StringComparison.Ordinal) &&
              runtime.Contains("new(\"loot-coin-row\", (ulong)visual)",
                  StringComparison.Ordinal) &&
              runtime.Contains("new(row.Name, GameTooltipTextTone.White)",
                  StringComparison.Ordinal) &&
              runtime.Contains("OfferOwnerAnchoredSharedGameTooltip(",
                  StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.BeginTooltip", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.SetTooltip", StringComparison.Ordinal) &&
              runtime.Contains("InsertChatText(itemLink)", StringComparison.Ordinal) &&
              runtime.Contains(".OverlayPath", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("DrawArt(dl, @\"Interface\\TargetingFrame\\TargetDead\"",
                  StringComparison.Ordinal),
            "LootFrame open presentation bypasses its law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
