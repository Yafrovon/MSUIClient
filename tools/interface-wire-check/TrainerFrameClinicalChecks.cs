using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class TrainerFrameClinicalChecks
{
    public static void Run()
    {
        Check(TrainerFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              TrainerFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              TrainerFrameUiLaw.PortraitOffset == new Vector2(7, 6) &&
              TrainerFrameUiLaw.PortraitSize == 60 &&
              TrainerFrameUiLaw.Title("") == "Trainer" &&
              TrainerFrameUiLaw.Title("  Woo Ping  ") == "Woo Ping" &&
              TrainerFrameUiLaw.PurseRightTop == new Vector2(180, 413) &&
              TrainerFrameUiLaw.DetailCostLabel == new Vector2(30, 340) &&
              TrainerFrameUiLaw.Greeting ==
                  new TrainerFrameUiLaw.LogicalRect(76, 38, 260, 26) &&
              TrainerFrameUiLaw.DetailNameBox ==
                  new TrainerFrameUiLaw.LogicalRect(68, 293, 244, 24) &&
              TrainerFrameUiLaw.DetailRequirementBox ==
                  new TrainerFrameUiLaw.LogicalRect(68, 309, 244, 20) &&
              TrainerFrameUiLaw.DetailDescriptionBox ==
                  new TrainerFrameUiLaw.LogicalRect(30, 360, 290, 30) &&
              TrainerFrameUiLaw.GreetingFont == "GameFontHighlight" &&
              TrainerFrameUiLaw.RowNameFont == "GameFontNormal" &&
              TrainerFrameUiLaw.DetailDescriptionFont == "GameFontHighlightSmall" &&
              TrainerFrameUiLaw.Row(10) ==
                  new TrainerFrameUiLaw.LogicalRect(22, 260, 293, 16) &&
              DropdownCapsuleUiLaw.List(TrainerFrameUiLaw.FilterDropDown, 3) ==
                  new DropdownCapsuleUiLaw.LogicalRect(220, 89, 128, 78) &&
              DropdownCapsuleUiLaw.Row(TrainerFrameUiLaw.FilterDropDown, 2) ==
                  new DropdownCapsuleUiLaw.LogicalRect(237, 136, 96, 16) &&
              TrainerFrameUiLaw.Close ==
                  new TrainerFrameUiLaw.LogicalRect(322, 8, 32, 32) &&
              TrainerFrameUiLaw.CollapseAll ==
                  new TrainerFrameUiLaw.LogicalRect(23, 72, 40, 22) &&
              TrainerFrameUiLaw.CollapseAllIcon ==
                  new TrainerFrameUiLaw.LogicalRect(23, 75, 16, 16) &&
              TrainerFrameUiLaw.CollapseAllLabelCenter == new Vector2(52, 83) &&
              TrainerFrameUiLaw.CollapseAllTabArt.Select(piece => piece.Element)
                  .SequenceEqual(new[]
                  {
                      "TrainerExpandTabLeft", "TrainerExpandTabMiddle",
                      "TrainerExpandTabRight",
                  }) &&
              TrainerFrameUiLaw.CollapseAllTabArt[1].Rect ==
                  new TrainerFrameUiLaw.LogicalRect(23, 64, 38, 32) &&
              TrainerFrameUiLaw.CollapseAllFont == "GameFontNormalSmall" &&
              TrainerFrameUiLaw.CollapseAllMinusPath ==
                  @"Interface\Buttons\UI-MinusButton-Up" &&
              TrainerFrameUiLaw.CollapseAllPlusPath ==
                  @"Interface\Buttons\UI-PlusButton-Up" &&
              TrainerFrameUiLaw.CollapseAllHighlightPath ==
                  @"Interface\Buttons\UI-PlusButton-Hilight" &&
              TrainerFrameUiLaw.FilterDropDown.Frame ==
                  new DropdownCapsuleUiLaw.LogicalRect(212, 64, 146, 32) &&
              TrainerFrameUiLaw.FilterDropDown.Button ==
                  new DropdownCapsuleUiLaw.LogicalRect(106, 1, 24, 24) &&
              // DetailIcon moved from (27,294) to (26,291) to centre the 37px icon in its
              // 64px DetailIconRing at (13,278): (64-37)/2 = 13, so 13+13 = 26 and
              // 278+13 = 291. Bounds follow: (100,200) + (26,291)*2 = (152,782), plus
              // (37,37)*2 = (226,856).
              TrainerFrameUiLaw.DetailTooltipOwnerBounds(new Vector2(100, 200), 2) ==
                  (new Vector2(152, 782), new Vector2(226, 856)),
            "trainer identity/window geometry drift");

        var available = new TrainerFrameUiLaw.ServiceNode(0, 26, "Arms", "Heroic Strike", 0, 1);
        var used = new TrainerFrameUiLaw.ServiceNode(1, 26, "Arms", "Cleave", 2, 20);
        var unavailable = new TrainerFrameUiLaw.ServiceNode(2, 256, "Fury", "Bloodrage", 1, 10);
        IReadOnlyList<TrainerFrameUiLaw.TreeRow> tree = TrainerFrameUiLaw.BuildTree(
            [available, used, unavailable], 0, new HashSet<uint>(), true, true, false);
        Check(tree.Select(row => row.Text).SequenceEqual(
                new[] { "Arms", "Heroic Strike", "Fury", "Bloodrage" }) &&
              TrainerFrameUiLaw.BuildTree([available, used, unavailable], 0,
                  new HashSet<uint> { 26 }, true, true, true)
                  .Select(row => row.Text).SequenceEqual(new[] { "Arms", "Fury", "Bloodrage" }),
            "trainer filter/collapsible skill-line tree drift");

        Check(TrainerFrameUiLaw.HeaderIcon(TrainerFrameUiLaw.Row(0)) ==
                  new TrainerFrameUiLaw.LogicalRect(25, 100, 16, 16) &&
              TrainerFrameUiLaw.RowNameMinimum(new Vector2(22, 100), 16, 12) ==
                  new Vector2(44, 102) &&
              TrainerFrameUiLaw.RowSubtextMinimum(new Vector2(44, 102), 80, 1) ==
                  new Vector2(134, 102) &&
              TrainerFrameUiLaw.RowNameColor(0, false) == 0xff00ff00 &&
              TrainerFrameUiLaw.RowSubtextColor(1, false, false) == 0xff000099 &&
              TrainerFrameUiLaw.RowSubtextColor(1, false, true) == 0xffffffff &&
              TrainerFrameUiLaw.WrapText("one two three", 7, 2,
                  candidate => candidate.Length).SequenceEqual(new[] { "one two", "three" }),
            "trainer authored row typography/color/wrap law drift");

        SpellInfo wrapper = new(Id: 1000, Name: "Teach", Rank: "", IconPath: "",
            Attributes: 0, AttributesEx2: 0, AttributesEx3: 0,
            InterruptFlags: 0, ChannelInterruptFlags: 0, Targets: 0, ImplicitTarget: 0,
            RecoveryMs: 0, CategoryRecoveryMs: 0, PowerType: 0, ManaCost: 0,
            ManaCostPercent: 0, StartRecoveryCategory: 0, StartRecoveryMs: 0,
            VisualId: 0, Speed: 0, Description: "", RangeIndex: 0,
            EffectIds: [36u, 0u, 0u], EffectTriggerSpells: [2457u, 0u, 0u]);
        Check(TrainerFrameUiLaw.TaughtSpell(wrapper) == 2457 &&
              TrainerFrameUiLaw.ServiceGroup(2, 0, wrapper, null).Name == "Recipes",
            "trainer taught-spell/group-type law drift");

        IReadOnlyList<MSUIClient.Net.TrainerSpell> refreshed =
            TrainerFrameUiLaw.MarkServiceUsed(
            [
                new(1424, 0, 100, false, false, 20, 0, 0, 0, 0, 0),
                new(6673, 0, 10, false, false, 1, 0, 0, 0, 0, 0),
            ], 1424);
        Check(refreshed[0].State == TrainerFrameUiLaw.UsedState &&
              refreshed[1].State == TrainerFrameUiLaw.AvailableState,
            "trainer success must retire only the purchased service row");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Trainer.cs"));
        int detailRingDraw = runtime.IndexOf("TrainerFrameUiLaw.DetailIconRing.Min",
            StringComparison.Ordinal);
        int detailIconDraw = runtime.IndexOf("iconMin + TrainerFrameUiLaw.DetailIcon.Size",
            StringComparison.Ordinal);
        Check(detailRingDraw >= 0 && detailIconDraw > detailRingDraw,
            "trainer detail slot surround must draw behind the spell icon");
        Check(runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[4], scale)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawNpcModalTitle", StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerMoney", StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerWrappedText", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.RowNameFont", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.RowSubtextFont", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.HeaderIcon", StringComparison.Ordinal) &&
              !runtime.Contains("DrawWrappedText", StringComparison.Ordinal) &&
              !runtime.Contains("dl.AddText", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.BuildTree", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.CollapseAllTabArt",
                  StringComparison.Ordinal) &&
              runtime.Contains("VanillaCollapseAllButton", StringComparison.Ordinal) &&
              runtime.Contains("VanillaDropdownCapsule", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.FilterDropDown", StringComparison.Ordinal) &&
              !runtime.Contains("VanillaButton(dl, \"##trainer-filter\"",
                  StringComparison.Ordinal) &&
              runtime.Contains("bool collapseEnabled = tree.Any(row => row.Header)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerFilterMenu", StringComparison.Ordinal) &&
              runtime.Contains("DropdownCapsuleUiLaw.RowCheck", StringComparison.Ordinal) &&
              runtime.Contains("DropdownCapsuleUiLaw.RowSound", StringComparison.Ordinal) &&
              runtime.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.DetailTooltipOwnerBounds", StringComparison.Ordinal) &&
              runtime.Contains("SpellTooltipPlacement.OwnerRight", StringComparison.Ordinal) &&
              runtime.Contains("spell:trainer-service", StringComparison.Ordinal) &&
              !runtime.Contains("Requires level {row.RequiredLevel}; skill",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(TrainerFrameUiLaw.OpenSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("CloseTrainerSession", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("DrawCenteredText(dl,origin+new Vector2(192,17)*scale,\"Trainer\"",
                  StringComparison.Ordinal) &&
              !runtime.Contains("$\"Cost: {FormatMoney", StringComparison.Ordinal),
            "trainer production window bypasses title/portrait/money/sound law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
