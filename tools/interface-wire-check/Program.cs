using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using System.Numerics;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static void CheckRtsAbilityTargeting(SpellInfo seed)
{
    var friendlyUnitSpell = seed with { Targets = 0x0100, ImplicitTarget = 0 };
    var hostileUnitSpell = seed with { Targets = 0x0080, ImplicitTarget = 0 };
    var genericUnitSpell = seed with { Targets = 0x0002, ImplicitTarget = 0 };
    var implicitSelfSpell = seed with { Targets = 0, ImplicitTarget = 0 };
    Check(CastTargetLaw.AcceptsExplicitFriendlyUnit(friendlyUnitSpell) &&
          !CastTargetLaw.AcceptsExplicitFriendlyUnit(hostileUnitSpell) &&
          !CastTargetLaw.AcceptsExplicitFriendlyUnit(genericUnitSpell) &&
          !CastTargetLaw.AcceptsExplicitFriendlyUnit(implicitSelfSpell),
        "commander friendly-unit spell classification drift");
    Check(RtsAbilityTargetLaw.Resolve(5, altHeld: false,
              acceptsExplicitFriendlyUnit: true) == RtsAbilityCastIntent.ChooseFriendlyTarget &&
          RtsAbilityTargetLaw.Resolve(5, altHeld: true,
              acceptsExplicitFriendlyUnit: true) == RtsAbilityCastIntent.CastOnPrimary &&
          RtsAbilityTargetLaw.Resolve(1, altHeld: false,
              acceptsExplicitFriendlyUnit: true) == RtsAbilityCastIntent.Normal &&
          RtsAbilityTargetLaw.Resolve(5, altHeld: true,
              acceptsExplicitFriendlyUnit: false) == RtsAbilityCastIntent.Normal,
        "commander multi-selection/Alt cast targeting grammar drift");

    string root = ClientConfig.FindRepoRoot();
    string shelf = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Hud", "GameLoop.CommandShelf.cs"));
    string control = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Scene", "GameLoop.Control.cs"));
    string party = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Hud", "GameLoop.PartyFrames.cs"));
    string unitFrames = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Hud", "GameLoop.UnitFrames.cs"));
    string settings = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Panels", "GameLoop.Settings.cs"));
    Check(shelf.Contains("RtsAbilityTargetLaw.Resolve(", StringComparison.Ordinal) &&
          shelf.Contains("CastPrimaryAbility(primary, spellId, explicitTarget: targetGuid);",
              StringComparison.Ordinal) &&
          shelf.Contains("BindingModifierHeld(GameBinding.RtsCastOnPrimary));",
              StringComparison.Ordinal) &&
          control.Contains("TryCommitRtsUnitCastTarget(pressPick.Armed",
              StringComparison.Ordinal) &&
          party.Contains("TryCommitRtsUnitCastTarget(member.Guid);",
              StringComparison.Ordinal) &&
          unitFrames.Contains("TryCommitRtsUnitCastTarget(unit.Guid);",
              StringComparison.Ordinal) &&
          settings.Contains("_rtsUnitCastSpellId != 0", StringComparison.Ordinal),
        "commander friendly-target world/frame/Alt/Escape production wiring drift");
}

static void CheckLightingDefaults()
{
    var lightingDefaults = new GameSettings.LightingSettings();
    Check(lightingDefaults.Mode == LightingMode.Parity112 &&
          lightingDefaults.InteriorSpill ==
              GameSettings.LightingSettings.ParityInteriorSpill &&
          GameSettings.LightingSettings.ParityInteriorSpill == 1.10f,
        "shipped Parity interior-brightness default drift");

    GameSettings shipped = GameSettings.Defaults();
    shipped.ResolveComposites();
    Check(shipped.Version == 13 && shipped.Water.Enabled &&
          shipped.Water.DrawWmoLiquid && !shipped.Water.UseAuthoredColors &&
          shipped.Water.DetailPercent == 70f && !shipped.Water.DetailCustom &&
          shipped.Water.AnimationFps == 12f && shipped.Water.FrameBlend == 0f &&
          shipped.Water.ShoreFade == .85f && shipped.Water.ShoreWidth == 1.2f &&
          shipped.Water.Opacity == 1f && shipped.Water.WaveAmplitude == 0f,
        "shipped water no longer resolves to the build-5875/1.12 baseline");

    string root = ClientConfig.FindRepoRoot();
    string migrationPath = Path.Combine(Path.GetTempPath(),
        $"msui-water-v13-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(migrationPath,
            "{\"Settings\":{\"Version\":12,\"Water\":{\"DetailPercent\":43.75," +
            "\"DetailCustom\":false,\"AnimationFps\":12.75,\"FrameBlend\":0.4375}}," +
            "\"Presets\":[]}");
        GameSettings migrated = SettingsStore.Load(root, migrationPath).Settings;
        Check(migrated.Version == 13 && migrated.Water.DetailPercent == 70f &&
              migrated.Water.AnimationFps == 12f && migrated.Water.FrameBlend == 0f &&
              migrated.Water.ShoreFade == .85f && migrated.Water.ShoreWidth == 1.2f,
            "v13 migration did not move non-custom water onto the shipped 1.12 baseline");

        File.WriteAllText(migrationPath,
            "{\"Settings\":{\"Version\":12,\"Water\":{\"DetailPercent\":43.75," +
            "\"DetailCustom\":true,\"AnimationFps\":9.5,\"FrameBlend\":0.375," +
            "\"ShoreFade\":0.91,\"ShoreWidth\":1.7}},\"Presets\":[]}");
        GameSettings custom = SettingsStore.Load(root, migrationPath).Settings;
        Check(custom.Version == 13 && custom.Water.DetailPercent == 43.75f &&
              custom.Water.AnimationFps == 9.5f && custom.Water.FrameBlend == .375f &&
              custom.Water.ShoreFade == .91f && custom.Water.ShoreWidth == 1.7f,
            "v13 migration overwrote explicitly customized water");
    }
    finally
    {
        if (File.Exists(migrationPath)) File.Delete(migrationPath);
    }
}

static void CheckPaperDollRegressions()
{
    Check(PaperDollUiLaw.AmmoIconPath(0, null) is null &&
          PaperDollUiLaw.AmmoIconPath(0, "Interface\\Icons\\INV_Ammo_Arrow_01") is null &&
          PaperDollUiLaw.AmmoIconPath(2512, null) ==
              @"Interface\Icons\INV_Misc_QuestionMark" &&
          PaperDollUiLaw.AmmoIconPath(2512, "Interface\\Icons\\INV_Ammo_Arrow_01") ==
              "Interface\\Icons\\INV_Ammo_Arrow_01",
        "paper-doll empty ammo must not resolve through the red missing-icon fallback");

    string characterPageSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
        "MSUIClient", "GameLoop", "Panels", "GameLoop.CharacterPage.cs"));
    int rotateLeftHit = characterPageSource.IndexOf(
        "DrawRotationButton(dl, p + new Vector2(65, 78) * s, true", StringComparison.Ordinal);
    int modelDropHit = characterPageSource.IndexOf(
        "ImGui.InvisibleButton(\"##paper-model-drop\"", StringComparison.Ordinal);
    Check(rotateLeftHit >= 0 && modelDropHit > rotateLeftHit &&
          characterPageSource.Contains(
              "PaperDollUiLaw.AmmoIconPath(entry, ammo?.IconPath)", StringComparison.Ordinal),
        "CharacterFrame rotate buttons must win the overlapping model drop target, and empty ammo " +
        "must bypass GameplayArt's missing-icon fallback");
}

static void CheckGameMenuLayout()
{
    static bool Near(Vector2 left, Vector2 right) =>
        Vector2.DistanceSquared(left, right) < .0001f;

    Vector2 authored = GameMenuUiLaw.ResolveOptionsSize(Vector2.Zero, 2f,
        new Vector2(2000f, 1400f));
    Vector2 remembered = GameMenuUiLaw.ResolveOptionsSize(new Vector2(700f, 500f),
        1.5f, new Vector2(1600f, 900f));
    Vector2 smallViewport = GameMenuUiLaw.ResolveOptionsSize(new Vector2(1200f, 900f),
        2f, new Vector2(700f, 500f));
    Vector2 mainDefault = GameMenuUiLaw.ResolveGameMenuSize(Vector2.Zero, 1.2f,
        new Vector2(1600f, 900f));
    Vector2 mainRemembered = GameMenuUiLaw.ResolveGameMenuSize(new Vector2(300f, 400f),
        1.2f, new Vector2(1600f, 900f));
    (Vector2 optionMinimum, Vector2 optionMaximum) = GameMenuUiLaw.WindowSizeLimits(
        gameMenu: false, 1.2f, new Vector2(1600f, 900f));
    Vector2 popupLeft = GameMenuUiLaw.LayoutPopupOrigin(new Vector2(600f, 200f),
        new Vector2(234f, 295.2f),
        new Vector2(GameMenuUiLaw.LayoutPopupWidth, GameMenuUiLaw.LayoutPopupHeight),
        new Vector2(1000f, 900f));
    // c5af6fe widened OptionsDefaultWidth from 450 to 900, so the authored default at scale 2
    // is 1800 wide (under the 2000 * OptionsViewportWidth ceiling). OptionsMinWidth is still
    // 450, which is why the minimum limits below are unchanged.
    Check(authored == new Vector2(1800f, 1150f) &&
          remembered == new Vector2(1050f, 750f) &&
          smallViewport == new Vector2(672f, 460f) &&
          Near(mainDefault, new Vector2(234f, 321.6f)) &&
          Near(mainRemembered, new Vector2(360f, 480f)) &&
          Near(optionMinimum, new Vector2(540f, 432f)) &&
          Near(optionMaximum, new Vector2(1536f, 828f)) &&
          Near(popupLeft, new Vector2(392f, 200f)) &&
          GameMenuUiLaw.ResolveMenuScale(.1f) == GameMenuUiLaw.MenuScaleMinimum &&
          GameMenuUiLaw.ResolveMenuScale(9f) == GameMenuUiLaw.MenuScaleMaximum &&
          GameMenuUiLaw.ToLogicalOptionsSize(remembered, 1.5f) == new Vector2(700f, 500f) &&
          GameMenuUiLaw.CenteredOrigin(new Vector2(1024, 768),
              new Vector2(450, 575)) == new Vector2(287, 96.5f) &&
          !GameMenuUiLaw.OptionsEnvironmentChanged(
              new Vector2(1600f, 900f), new Vector2(1600.1f, 900.1f), 1.5f, 1.5f) &&
          GameMenuUiLaw.OptionsEnvironmentChanged(
              new Vector2(1600f, 900f), new Vector2(1280f, 720f), 1.5f, 1.5f) &&
          GameMenuUiLaw.OptionsEnvironmentChanged(
              new Vector2(1600f, 900f), new Vector2(1600f, 900f), 1.5f, 2f),
        "Escape/Options windows must independently scale, restore size, and clamp to viewport");

    string root = ClientConfig.FindRepoRoot();
    string settingsSource = SourceText.Read(Path.Combine(root,
        "MSUIClient", "Program.Settings.cs"));
    Check(settingsSource.Contains(
              "Settings.MenuLayout?.Scale ?? 1f", StringComparison.Ordinal) &&
          settingsSource.Contains("DrawMenuLayoutGear", StringComparison.Ordinal) &&
          settingsSource.Contains("Menu text", StringComparison.Ordinal) &&
          !settingsSource.Contains("Slider(\"fontscale\"", StringComparison.Ordinal) &&
          settingsSource.Contains(
              "InterfaceScaleLaw.Resolve(Settings.Display.UiScale)",
              StringComparison.Ordinal) &&
          settingsSource.Contains("RememberPageSize(size, io.DisplaySize",
              StringComparison.Ordinal) &&
          !settingsSource.Contains("flags |= ImGuiWindowFlags.NoResize",
              StringComparison.Ordinal),
        "Escape menu renderer lost its independent scale, gear, or resize persistence seam");

    string migrationPath = Path.Combine(Path.GetTempPath(),
        $"msui-menu-scale-migration-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(migrationPath,
            "{\"Settings\":{\"Version\":7,\"Display\":{\"UiScale\":1.125," +
            "\"FontScale\":1.35}," +
            "\"MenuLayout\":{}},\"Presets\":[]}");
        SettingsStore migrated = SettingsStore.Load(root, migrationPath);
        // Pinned to the highest migration step in GameSettings.Migrate, deliberately: adding a
        // step must force someone to confirm the older chrome/text sizes still survive the
        // whole chain.
        Check(migrated.Settings.Version == 13 &&
              MathF.Abs(migrated.Settings.MenuLayout.Scale - 1.125f) < .0001f &&
              MathF.Abs(migrated.Settings.MenuLayout.TextScale - 1.35f) < .0001f,
            "menu layout migration did not preserve its existing chrome and text sizes");
    }
    finally
    {
        if (File.Exists(migrationPath)) File.Delete(migrationPath);
    }
}

static void CheckInterfaceScaleLaw()
{
    float windowed = InterfaceScaleLaw.ResolveForFramebuffer(1600f, 900f, 1.30f);
    float maximized = InterfaceScaleLaw.ResolveForFramebuffer(2560f, 1440f, 1.30f);
    Check(InterfaceScaleLaw.Resolve(1.0752523f) == 1.0752523f &&
          InterfaceScaleLaw.Resolve(0.1f) == InterfaceScaleLaw.Minimum &&
          InterfaceScaleLaw.Resolve(9f) == InterfaceScaleLaw.Maximum &&
          windowed == 1.30f &&
          MathF.Abs(maximized - 2.08f) < .0001f &&
          MathF.Abs(maximized / windowed - 1.6f) < .0001f &&
          TalentFrameUiLaw.TitleCenter == new Vector2(192f, 24f) &&
          TalentFrameUiLaw.TalentPointsBottomRight == new Vector2(252f, 425f) &&
          TalentFrameUiLaw.SpentPointsPrefix("Arms") ==
              "Points spent in Arms Talents: ",
        "Gameplay Interface scale must grow proportionally with the live window");

    // THE MAIN MENU BAR MUST FIT, AT EVERY RESOLUTION AND EVERY ASPECT. The bar spans 1216
    // logical units (1024 strip + 96 of end cap each side) and is centred, so the fit test is
    // span * effectiveScale <= width. Because ResolveForFramebuffer follows the LIMITING
    // dimension, the usable logical width for any aspect at or below 16:9 collapses to
    // ReferenceFramebufferWidth / preference - identical at 1600x900, 1920x1080 and 2560x1440 -
    // which is why the shipped 1.8 preference clipped the caps on every 16:9 panel while a wide
    // enough ultrawide cleared them. Reported by a tester, 2026-08-26.
    static bool BarFits(float width, float height, float preference) =>
        InterfaceScaleLaw.ResolveForFramebuffer(width, height, preference) *
        InterfaceScaleLaw.MainMenuBarSpanWidth <= width + .01f;

    Check(BarFits(1600f, 900f, 1.8f) && BarFits(1920f, 1080f, 1.8f) &&
          BarFits(2560f, 1440f, 1.8f) && BarFits(1366f, 768f, 1.8f) &&
          BarFits(1280f, 1024f, 1.8f) && BarFits(1920f, 1200f, 1.8f) &&
          BarFits(3440f, 1440f, 1.8f) && BarFits(5120f, 1440f, 1.8f) &&
          BarFits(1600f, 900f, 3f) && BarFits(2560f, 1440f, 4f),
        "The main menu bar must never be scaled wider than the framebuffer");

    // The ceiling must BITE only where it has to. Below the fit threshold the proportional law is
    // untouched, so an ultrawide keeps the larger HUD it already had.
    Check(MathF.Abs(InterfaceScaleLaw.ResolveForFramebuffer(1600f, 900f, 1.3f) - 1.3f) < .0001f &&
          MathF.Abs(InterfaceScaleLaw.ResolveForFramebuffer(5120f, 1440f, 1.8f) - 2.88f) < .0001f &&
          InterfaceScaleLaw.ResolveForFramebuffer(1600f, 900f, 1.8f) < 1.8f,
        "The main menu bar fit ceiling must clamp only the configurations that overflow");

    // The slider's advertised ceiling is the same rule expressed in preference units.
    Check(MathF.Abs(InterfaceScaleLaw.MaximumPreferenceForFramebuffer(1600f, 900f) - 1.3158f) < .001f &&
          MathF.Abs(InterfaceScaleLaw.MaximumPreferenceForFramebuffer(2560f, 1440f) - 1.3158f) < .001f &&
          InterfaceScaleLaw.MaximumPreferenceForFramebuffer(5120f, 1440f) > 1.8f,
        "The Interface scale ceiling shown to the player must match the applied ceiling");

    CheckResolutionUiLaw();
}

// ImGui's Begin() contract is UNCONDITIONAL: every call needs a matching End(), including the
// one that returned false. Two panels took the false branch with a bare `return;`, which left the
// window stack unbalanced for the remainder of the frame - the Key Bindings frame lost its
// backdrop, unit frames drew over it, and the free-view banner was emitted twice. Reported
// 2026-08-26. Textual because the fault is a missing statement, which no runtime check reaches
// until that exact frame happens.
static void CheckVanillaWindowsAlwaysEnd()
{
    string root = ClientConfig.FindRepoRoot();
    string client = Path.Combine(root, "MSUIClient");
    var offenders = new List<string>();

    foreach (string file in Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)) continue;

        string text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        int at = 0;
        while ((at = text.IndexOf("BeginVanillaWindow(", at, StringComparison.Ordinal)) >= 0)
        {
            // The helper's own declaration is not a call site.
            int lineStart = text.LastIndexOf('\n', at) + 1;
            string line = text[lineStart..text.IndexOf('\n', at)];
            if (line.Contains("private bool BeginVanillaWindow", StringComparison.Ordinal))
            { at += 19; continue; }

            // Look at the guarded branch: everything up to the end of the statement that closes
            // this call. A false-return branch must say ImGui.End() before it returns.
            int window = Math.Min(text.Length, at + 400);
            string tail = text[at..window];
            int ret = tail.IndexOf(") return;", StringComparison.Ordinal);
            if (ret >= 0 && !tail[..ret].Contains("ImGui.End();", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(file)} @ {line.Trim()}");
            at += 19;
        }
    }

    Check(offenders.Count == 0,
        "a vanilla window returns from a false Begin without ImGui.End(): " +
        string.Join(" | ", offenders));
}

// THE KEY BINDINGS FRAME MUST FIT INSIDE ITS OWN ARTWORK.
//
// UI-KeyBindingFrame-*.blp is a 640x512 canvas whose art does NOT fill it. Decoded alpha:
//   left  solid border x 6..17,   interior fill from x 18
//   top   solid border y 7..52,   interior fill from y 53
//   right interior fill ends 561, border to 593, LAST OPAQUE PIXEL x 597
//   bottom                                        last opaque pixel y 500
// So anything past x=597 is drawn on empty padding and reads as "outside the window", and
// anything above y=53 is drawn on the frame's own border. Both happened at once: the scroll bar
// ran 584..616 and Cancel ran 490..620, while the search box sat at y=8..30. Reported 2026-08-26.
// A WHEEL CATCHER MUST NEVER BE A BUTTON.
//
// The pattern is: submit a full-area ImGui.InvisibleButton over a list so the mouse wheel can be
// read from IsItemHovered. It works for the wheel and silently kills the list. The catcher spans
// everything and is submitted FIRST, so on the press frame it takes ActiveId, and every item
// inside it then fails ItemHoverable ("g.ActiveId != 0 && g.ActiveId != id") - the panel draws
// perfectly and responds to nothing. It cost the Key Bindings frame every click (category +/-
// headers, both key buttons per row) and the Talent frame every point spend. Reported
// 2026-08-26. ImGui.IsMouseHoveringRect reads the same wheel while claiming no id at all.
static void CheckNoWheelCatcherButtons()
{
    string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
    var offenders = new List<string>();
    foreach (string file in Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
        string[] lines = File.ReadAllLines(file);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("InvisibleButton(", StringComparison.Ordinal)) continue;
            if (!lines[i].Contains("wheel", StringComparison.OrdinalIgnoreCase) &&
                !lines[i].Contains("scroll", StringComparison.OrdinalIgnoreCase)) continue;
            // A catcher is one whose only purpose is the wheel: the next couple of lines read
            // MouseWheel off IsItemHovered rather than acting on a click.
            string next = string.Join(" ", lines.Skip(i + 1).Take(2));
            if (next.Contains("MouseWheel", StringComparison.Ordinal) &&
                next.Contains("IsItemHovered", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
        }
    }
    Check(offenders.Count == 0,
        "a full-area InvisibleButton is being used as a wheel catcher (it steals ActiveId and " +
        "makes everything inside it unclickable); use ImGui.IsMouseHoveringRect: " +
        string.Join(" | ", offenders));
}

static void CheckKeyBindingsFrameFitsItsArt()
{
    const float interiorLeft = 18f, interiorTop = 53f, lastOpaqueX = 597f, lastOpaqueY = 500f;
    var bad = new List<string>();

    void Fits(string what, float x, float y, float w, float h, bool interior = true)
    {
        if (x < (interior ? interiorLeft : 0f)) bad.Add($"{what} left {x} < {interiorLeft}");
        if (interior && y < interiorTop) bad.Add($"{what} top {y} < {interiorTop} (on the border)");
        if (x + w > lastOpaqueX) bad.Add($"{what} right {x + w} > {lastOpaqueX} (past the art)");
        if (y + h > lastOpaqueY) bad.Add($"{what} bottom {y + h} > {lastOpaqueY} (past the art)");
    }

    Fits("Search", KeyBindingsUiLaw.Search.X, KeyBindingsUiLaw.Search.Y,
        KeyBindingsUiLaw.Search.Width, KeyBindingsUiLaw.Search.Height);
    Fits("Rows", KeyBindingsUiLaw.Rows.X, KeyBindingsUiLaw.Rows.Y,
        KeyBindingsUiLaw.Rows.Width, KeyBindingsUiLaw.Rows.Height);
    Fits("ScrollBar", KeyBindingsUiLaw.ScrollMinimum.X, KeyBindingsUiLaw.ScrollMinimum.Y,
        16f, KeyBindingsUiLaw.ScrollHeight);
    foreach ((string name, KeyBindingsUiLaw.Rect r) in new[]
             {
                 ("Defaults", KeyBindingsUiLaw.Defaults), ("Unbind", KeyBindingsUiLaw.Unbind),
                 ("Okay", KeyBindingsUiLaw.Okay), ("Cancel", KeyBindingsUiLaw.Cancel),
             })
        Fits(name, r.X, r.Y, r.Width, r.Height, interior: false);

    // The row band drawn at RowPitch must not run into the button row.
    float rowsEnd = KeyBindingsUiLaw.Rows.Y + KeyBindingsUiLaw.VisibleRows * KeyBindingsUiLaw.RowPitch;
    if (rowsEnd > KeyBindingsUiLaw.Defaults.Y)
        bad.Add($"rows end {rowsEnd} overlaps the button row at {KeyBindingsUiLaw.Defaults.Y}");
    // The bar fills the art's carved scroll slot, which spans the frame interior (53..443),
    // not the row band (104..449): 0234019 "keybindings scroll bar fix" anchored it at
    // interiorTop while the rows start lower. Fits() above already keeps the bar inside the
    // artwork, so this rule guards the interior height rather than the shorter row band.
    if (KeyBindingsUiLaw.ScrollHeight > lastOpaqueY - interiorTop + 0.01f)
        bad.Add("scroll bar is taller than the frame interior it sits in");

    Check(bad.Count == 0, "Key Bindings frame does not fit its own artwork: " + string.Join(" | ", bad));

    // Values that come straight from Blizzard's XML rather than from the art.
    Check(KeyBindingsUiLaw.FrameSize == new Vector2(640, 512) &&
          Math.Abs(KeyBindingsUiLaw.FrameTop - 100f) < .01f &&
          KeyBindingsUiLaw.Cancel.X == 460f && KeyBindingsUiLaw.Okay.X == 330f &&
          KeyBindingsUiLaw.Defaults.X == 10f && KeyBindingsUiLaw.Cancel.Y == 469f,
        "Key Bindings geometry drifted from KeyBindingFrame.xml (640x512, TOP -100, " +
        "BOTTOMRIGHT -50/21, BOTTOMLEFT 10/21)");

    // TOP-anchored means centred, never flush left.
    Check(KeyBindingsUiLaw.WindowOrigin(1600f).X == (1600f - 640f) * .5f &&
          KeyBindingsUiLaw.WindowOrigin(640f).X == 0f,
        "Key Bindings frame must centre horizontally like its TOP anchor, not sit at x=0");
}

static void CheckResolutionUiLaw()
{
    (int, int)[] modes = [(1920, 1080), (1920, 1080), (1280, 720), (800, 600), (3840, 2160)];

    // Deduplicated by size (one mode per refresh rate arrives), sorted by area, and never
    // offering a size larger than the panel - a window created off-screen cannot be undone from
    // a menu the player can no longer reach.
    var options = ResolutionUiLaw.Build(modes, native: (1920, 1080), current: (1600, 900));
    Check(options.Count == 4 &&
          options[0] is { Width: 800, Height: 600 } &&
          options[1] is { Width: 1280, Height: 720 } &&
          options[2] is { Width: 1600, Height: 900 } &&
          options[3] is { Width: 1920, Height: 1080, IsNative: true },
        $"Resolution list must dedupe, sort by area, drop above-native and flag native: " +
        $"[{string.Join(", ", options.Select(o => $"{o.Width}x{o.Height}{(o.IsNative ? "*" : "")}"))}]");

    // The saved size survives even when the monitor does not report it - a value carried over
    // from another display must stay selectable rather than silently snapping the player.
    Check(ResolutionUiLaw.IndexOf(options, (1600, 900)) == 2 &&
          ResolutionUiLaw.IndexOf(options, (1234, 567)) == -1,
        "Resolution list must retain and locate the saved size");

    // Aspect is the number that actually decides whether the HUD fits, so it has to be right.
    Check(ResolutionUiLaw.AspectLabel(1920, 1080) == "16:9" &&
          ResolutionUiLaw.AspectLabel(1600, 900) == "16:9" &&
          ResolutionUiLaw.AspectLabel(1280, 800) == "16:10" &&
          ResolutionUiLaw.AspectLabel(1600, 1200) == "4:3" &&
          ResolutionUiLaw.AspectLabel(3440, 1440) == "21:9" &&
          ResolutionUiLaw.AspectLabel(5120, 1440) == "32:9",
        $"Resolution aspect naming drift: 1280x800={ResolutionUiLaw.AspectLabel(1280, 800)}, " +
        $"3440x1440={ResolutionUiLaw.AspectLabel(3440, 1440)}, " +
        $"5120x1440={ResolutionUiLaw.AspectLabel(5120, 1440)}");

    // With no monitor to ask, the page still has to offer something usable.
    var fallback = ResolutionUiLaw.Build(ResolutionUiLaw.Fallback, native: (0, 0), current: (1600, 900));
    Check(fallback.Count == ResolutionUiLaw.Fallback.Length &&
          fallback.All(option => !option.IsNative),
        "Resolution fallback list must survive an unavailable monitor");

    string root = ClientConfig.FindRepoRoot();
    string gameplaySource = SourceText.Read(Path.Combine(root,
        "MSUIClient", "GameLoop", "Hud", "GameLoop.GameplayLayout.cs"));
    Check(gameplaySource.Contains("_window.FramebufferSize", StringComparison.Ordinal) &&
          gameplaySource.Contains("Settings.Display.UiScale", StringComparison.Ordinal) &&
          !gameplaySource.Contains("_skin?.Scale", StringComparison.Ordinal),
        "Gameplay scale must use live window pixels and persisted preference, not menu skin state");
}

static void CheckOptionsSearch()
{
    OptionsSearchUiLaw.Rect authored = OptionsSearchUiLaw.Box(450f);
    OptionsSearchUiLaw.Rect narrow = OptionsSearchUiLaw.Box(200f);
    OptionsSearchUiLaw.Rect clear = OptionsSearchUiLaw.ClearButton(authored);
    Check(authored == new OptionsSearchUiLaw.Rect(50f, 30f, 350f, 22f) &&
          narrow == new OptionsSearchUiLaw.Rect(18f, 30f, 164f, 22f) &&
          clear == new OptionsSearchUiLaw.Rect(380f, 32.5f, 17f, 17f) &&
          OptionsSearchUiLaw.ContentTop == 60f,
        "Options search authored input/clear/content geometry drift");

    Check(OptionsSearchUiLaw.Score("Master Volume", "master volume") == 12 &&
          OptionsSearchUiLaw.Score("Master Volume", "music, volume") == 5 &&
          OptionsSearchUiLaw.Score("Master Volume", "camera") is null,
        "Options search whole-query/token substring scoring drift");

    OptionsSearchGroup[] volume = OptionsSearchUiLaw.Find("volume");
    OptionsSearchGroup[] cameraSound = OptionsSearchUiLaw.Find("camera, sound");
    Check(volume.Length == 1 && volume[0].Page == OptionsSearchPage.Sound &&
          volume[0].Entries.Count == 5 && volume[0].Entries[0].Label == "Volume" &&
          cameraSound.Length == 3 &&
          cameraSound[0].Page == OptionsSearchPage.Video &&
          cameraSound[1].Page == OptionsSearchPage.Interface &&
          cameraSound[2].Page == OptionsSearchPage.Sound,
        $"Options search page grouping, catalog order, or best-score ordering drift: " +
        $"volume=[{string.Join(',', volume.Select(group => $"{group.Page}:{group.Entries.Count}:" +
            string.Join('|', group.Entries.Select(entry => entry.Label))))}] " +
        $"cameraSound=[{string.Join(',', cameraSound.Select(group => $"{group.Page}:{group.BestScore}"))}]");

    string root = ClientConfig.FindRepoRoot();
    string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
        "GameLoop.Settings.cs"));

    // WOWSKIN.SCALE HAS EXACTLY TWO PER-FRAME OWNERS: Gui() assigns the gameplay scale before
    // the HUD draws, DrawSettings assigns S before the menu draws. Nothing may write it from
    // inside a widget helper, because those run MID-DRAW - ApplySettings assigning the gameplay
    // scale from the Slider/Check helpers re-scaled every widget below the one being dragged for
    // the rest of the frame, and the options window appeared to collapse and rebuild under the
    // cursor on every page. Reported 2026-08-26.
    // The chat-frame controls must be REACHABLE. They were absent from the catalog entirely,
    // so searching "chat" in the options page never offered the switch that moves the chat
    // window - and the checkbox itself sat eighth inside the Display box. Reported 2026-08-26.
    // PLAN_21: the chat-only mover became the HUD layout editor on the Interface page, and
    // "chat" must still lead there.
    OptionsSearchGroup[] chat = OptionsSearchUiLaw.Find("chat");
    Check(chat.Any(g => g.Page != OptionsSearchPage.Video &&
              g.Entries.Any(e => e.Label == "Move chat")),
        "options search cannot find 'Move chat' on the Interface page");
    OptionsSearchGroup[] hudLayout = OptionsSearchUiLaw.Find("hud layout");
    Check(hudLayout.Any(g => g.Entries.Any(e => e.Label == "Edit HUD layout")),
        "options search cannot find 'Edit HUD layout'");
    // "quest helper" tokenizes to QUEST HELPER / QUEST / HELPER, so any page carrying a
    // quest word answers it. The intent being guarded is that Quest Helper remains reachable
    // on the AddOns page, not that it is the only thing the query finds.
    OptionsSearchGroup[] questHelper = OptionsSearchUiLaw.Find("quest helper");
    Check(questHelper.Any(group => group.Page == OptionsSearchPage.AddOns &&
              group.Entries.Any(entry => entry.Label == "Quest Helper")),
        "options search cannot find the native Quest Helper on the AddOns page");
    // Same tokenization as above: the QUEST token also reaches the AddOns page's Quest
    // Helper, so this query legitimately returns two groups. Assert reachability on the
    // Interface page rather than exclusivity.
    OptionsSearchGroup[] automaticQuestTracking =
        OptionsSearchUiLaw.Find("automatic quest tracking");
    Check(automaticQuestTracking.Any(group =>
              group.Page == OptionsSearchPage.Interface &&
              group.Entries.Any(entry => entry.Label == "Automatic Quest Tracking")),
        "options search cannot find Automatic Quest Tracking on the Interface page");

    // The Escape menu's layout gear must sit in the frame's INTERIOR. GameMenuFrame.xml declares
    // a Backdrop with EdgeSize 32, and WowSkin.Dialog carries the same 32 (drawn at
    // EdgeSize * Scale), so the outer 32 logical units are border art. The gear was inset 8 -
    // a quarter of the band - and sat on the corner ornament, half lost. Reported 2026-08-26.
    foreach (float menuScale in new[] { 1f, 1.8f, 2.17f, 3f })
    foreach (float frameW in new[] { 195f, 400f, 660f })
    {
        var frameSize = new Vector2(frameW * menuScale, 300f * menuScale);
        Vector2 gear = GameMenuUiLaw.LayoutGearMinimum(Vector2.Zero, frameSize, menuScale);
        float side = GameMenuUiLaw.LayoutGearSide(menuScale);
        float band = GameMenuUiLaw.BackdropEdgeSize * GameMenuUiLaw.ResolveMenuScale(menuScale);
        Check(gear.X >= band - .01f && gear.Y >= band - .01f &&
              gear.X + side <= frameSize.X - band + .01f,
            $"Escape-menu gear sits on the border art at scale {menuScale}, frame {frameW}: " +
            $"gear x {gear.X:F1}..{gear.X + side:F1}, y {gear.Y:F1}, band {band:F1}, " +
            $"frame width {frameSize.X:F1}");
    }

    CheckVanillaWindowsAlwaysEnd();
    CheckKeyBindingsFrameFitsItsArt();
    CheckNoWheelCatcherButtons();

    // Scroll bars are authored by Blizzard, not by eye. Interface\FrameXML\UIPanelTemplates.xml:
    // the up/down buttons and the knob are all 16 x 16, and all three inherit UIPanelScrollBarButton,
    // whose TexCoords are 0.25 .. 0.75 - only the centre half of each texture is drawn. Drawing
    // them 32 x 32 with full UVs made the glyph four times its authored size inside its own
    // padding, which is what put fat blobs against the Key Bindings frame border.
    string vanillaUi = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
        "GameLoop.VanillaUi.cs"));
    Check(vanillaUi.Contains("const float ButtonLogical = 16f;", StringComparison.Ordinal) &&
          vanillaUi.Contains("new Vector2(0.25f, 0.25f)", StringComparison.Ordinal) &&
          vanillaUi.Contains("new Vector2(0.75f, 0.75f)", StringComparison.Ordinal) &&
          !vanillaUi.Contains("Vector2 buttonSize = new Vector2(32) * scale;", StringComparison.Ordinal) &&
          !vanillaUi.Contains("Vector2 knobSize = new Vector2(24, 32) * scale;", StringComparison.Ordinal),
        "scroll bar geometry drifted from UIPanelTemplates.xml (16x16 buttons, TexCoords .25-.75)");

    Check(settings.Contains("_skin.Scale = S;", StringComparison.Ordinal) &&
          !settings.Contains("_skin.Scale = uiScale;", StringComparison.Ordinal) &&
          !settings.Contains("_skin.Scale = v;", StringComparison.Ordinal),
        "the options menu writes WowSkin.Scale mid-draw again; the per-frame owners must keep it");

    Check(settings.Contains("DrawOptionsSearch(dl, min, size);", StringComparison.Ordinal) &&
          settings.Contains("OptionsSearchUiLaw.Find(_optionsSearch)", StringComparison.Ordinal) &&
          settings.Contains("DrawVanillaInputBorder(draw, boxMin, box.Size, S);",
              StringComparison.Ordinal) &&
          settings.Contains("showDefaults: false", StringComparison.Ordinal) &&
          settings.Contains("_optionsSearch = \"\";\n            Go(page);",
              StringComparison.Ordinal) &&
          settings.Contains("Settings.MenuLayout?.Scale ?? 1f", StringComparison.Ordinal) &&
          settings.Contains("RememberPageSize(size, io.DisplaySize", StringComparison.Ordinal),
        "Options search runtime wiring or protected MSUI scaling/resize seam drift");
}

if (args.Contains("--visual-lifecycle-only", StringComparer.Ordinal))
{
    VisualLifecycleClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: VisualLifecycle PASS");
    return;
}

if (args.Contains("--paper-doll-regression-only", StringComparer.Ordinal))
{
    CheckPaperDollRegressions();
    Console.WriteLine("interface-wire-check: PaperDollRegression PASS");
    return;
}

CheckInterfaceScaleLaw();

if (args.Contains("--char-create-only", StringComparer.Ordinal))
{
    CharCreateClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CharCreate PASS");
    return;
}

if (args.Contains("--login-current-only", StringComparer.Ordinal))
{
    LoginClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: LoginCurrent PASS");
    return;
}

if (args.Contains("--options-search-only", StringComparer.Ordinal))
{
    CheckOptionsSearch();
    Console.WriteLine("interface-wire-check: OptionsSearch PASS");
    return;
}

if (args.Contains("--chat-bubble-only", StringComparer.Ordinal))
{
    ChatBubbleClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ChatBubble PASS");
    return;
}

if (args.Contains("--hard-landing-only", StringComparer.Ordinal))
{
    HardLandingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: HardLanding PASS");
    return;
}

if (args.Contains("--honor-credit-only", StringComparer.Ordinal))
{
    HonorCreditClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: HonorCredit PASS");
    return;
}

if (args.Contains("--hardware-cursor-only", StringComparer.Ordinal))
{
    HardwareCursorClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: HardwareCursor PASS");
    return;
}

if (args.Contains("--cooldown-protocol-only", StringComparer.Ordinal))
{
    CooldownProtocolClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CooldownProtocol PASS");
    return;
}

if (args.Contains("--trade-protocol-only", StringComparer.Ordinal))
{
    TradeProtocolClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TradeProtocol PASS");
    return;
}

if (args.Contains("--social-protocol-only", StringComparer.Ordinal))
{
    SocialProtocolClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SocialProtocol PASS");
    return;
}

if (args.Contains("--footstep-only", StringComparer.Ordinal))
{
    FootstepClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Footstep PASS");
    return;
}

if (args.Contains("--melee-sound-only", StringComparer.Ordinal))
{
    MeleeSoundClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MeleeSound PASS");
    return;
}

if (args.Contains("--quest-markers-only", StringComparer.Ordinal))
{
    QuestMarkerClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: QuestMarkers PASS");
    return;
}

if (args.Contains("--quest-log-only", StringComparer.Ordinal))
{
    QuestLogClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: QuestLog PASS");
    return;
}

if (args.Contains("--durability-only", StringComparer.Ordinal))
{
    DurabilityFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DurabilityFrame PASS");
    return;
}

if (args.Contains("--quest-timer-only", StringComparer.Ordinal))
{
    QuestTimerFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: QuestTimerFrame PASS");
    return;
}

if (args.Contains("--game-time-only", StringComparer.Ordinal))
{
    GameTimeClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameTime PASS");
    return;
}

if (args.Contains("--minimap-only", StringComparer.Ordinal))
{
    MinimapClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Minimap PASS");
    return;
}

if (args.Contains("--server-sound-only", StringComparer.Ordinal))
{
    ServerSoundClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ServerSound PASS");
    return;
}

if (args.Contains("--weather-only", StringComparer.Ordinal))
{
    WeatherClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Weather PASS");
    return;
}

if (args.Contains("--realm-logon-only", StringComparer.Ordinal))
{
    RealmLogonClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: RealmLogon PASS");
    return;
}

if (args.Contains("--network-telemetry-only", StringComparer.Ordinal))
{
    NetworkTelemetryClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: NetworkTelemetry PASS");
    return;
}

if (args.Contains("--aura-visual-only", StringComparer.Ordinal))
{
    AuraVisualClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AuraVisual PASS");
    return;
}

if (args.Contains("--model-lighting-only", StringComparer.Ordinal))
{
    ModelLightingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ModelLighting PASS");
    return;
}

if (args.Contains("--mount-sheathe-sound-only", StringComparer.Ordinal))
{
    MountSheatheSoundClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MountSheatheSound PASS");
    return;
}

if (args.Contains("--text-emote-voice-only", StringComparer.Ordinal))
{
    TextEmoteVoiceClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TextEmoteVoice PASS");
    return;
}

if (args.Contains("--npc-greeting-only", StringComparer.Ordinal))
{
    NpcGreetingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: NpcGreeting PASS");
    return;
}

if (args.Contains("--gameobject-sound-only", StringComparer.Ordinal))
{
    GameObjectSoundClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameObjectSound PASS");
    return;
}

if (args.Contains("--zone-text-only", StringComparer.Ordinal))
{
    ZoneTextClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ZoneText PASS");
    return;
}

if (args.Contains("--follow-only", StringComparer.Ordinal))
{
    AutoFollowClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AutoFollow PASS");
    return;
}

if (args.Contains("--area-trigger-only", StringComparer.Ordinal))
{
    AreaTriggerClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AreaTrigger PASS");
    return;
}

if (args.Contains("--gossip-only", StringComparer.Ordinal))
{
    GossipClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Gossip PASS");
    return;
}

if (args.Contains("--audio-device-probe", StringComparer.Ordinal))
{
    AudioDeviceProbe.Run();
    return;
}

if (args.Contains("--imgui-policy-only", StringComparer.Ordinal))
{
    GameplayImguiPolicyClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameplayImguiPolicy PASS");
    return;
}

if (args.Contains("--spell-focus-only", StringComparer.Ordinal))
{
    SpellFocusLayoutClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SpellFocusLayout PASS");
    return;
}

if (args.Contains("--hud-layout-only", StringComparer.Ordinal))
{
    HudLayoutClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: HudLayout PASS");
    return;
}

if (args.Contains("--binder-only", StringComparer.Ordinal))
{
    BinderClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Binder PASS");
    return;
}

if (args.Contains("--npc-session-only", StringComparer.Ordinal))
{
    NpcSessionClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: NpcSession PASS");
    return;
}

if (args.Contains("--loot-frame-only", StringComparer.Ordinal))
{
    LootFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: LootFrame PASS");
    return;
}

if (args.Contains("--trainer-frame-only", StringComparer.Ordinal))
{
    TrainerFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TrainerFrame PASS");
    return;
}

if (args.Contains("--profession-frame-only", StringComparer.Ordinal))
{
    ProfessionFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ProfessionFrame PASS");
    return;
}

if (args.Contains("--stance-bar-only", StringComparer.Ordinal))
{
    StanceBarClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: StanceBar PASS");
    return;
}

if (args.Contains("--player-power-bars-only", StringComparer.Ordinal))
{
    PlayerPowerBarsClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PlayerPowerBars PASS");
    return;
}

if (args.Contains("--hovercast-only", StringComparer.Ordinal))
{
    HovercastClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Hovercast PASS");
    return;
}

if (args.Contains("--swing-timer-only", StringComparer.Ordinal))
{
    SwingTimerClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SwingTimer PASS");
    return;
}

if (args.Contains("--trade-frame-only", StringComparer.Ordinal))
{
    TradeFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TradeFrame PASS");
    return;
}

if (args.Contains("--duel-frame-only", StringComparer.Ordinal))
{
    DuelFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DuelFrame PASS");
    return;
}

if (args.Contains("--item-text-only", StringComparer.Ordinal))
{
    ItemTextFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ItemTextFrame PASS");
    return;
}

if (args.Contains("--auction-frame-only", StringComparer.Ordinal))
{
    AuctionFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AuctionFrame PASS");
    return;
}

if (args.Contains("--char-select-current-only", StringComparer.Ordinal))
{
    CharSelectCurrentClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CharSelectCurrent PASS");
    return;
}

if (args.Contains("--help-frame-only", StringComparer.Ordinal))
{
    HelpFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: HelpFrame PASS");
    return;
}

if (args.Contains("--tabard-frame-only", StringComparer.Ordinal))
{
    TabardFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TabardFrame PASS");
    return;
}

if (args.Contains("--ui-text-markup-only", StringComparer.Ordinal))
{
    UiTextMarkupClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UiTextMarkup/ItemRef PASS");
    return;
}

if (args.Contains("--friends-frame-only", StringComparer.Ordinal))
{
    FriendsFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: FriendsFrame PASS");
    return;
}

if (args.Contains("--gameobject-animation-only", StringComparer.Ordinal))
{
    GameObjectAnimationClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameObjectAnimation PASS");
    return;
}

if (args.Contains("--glue-audio-only", StringComparer.Ordinal))
{
    GlueAudioClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GlueAudio PASS");
    return;
}

if (args.Contains("--fishing-line-only", StringComparer.Ordinal))
{
    FishingLineClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: FishingLine PASS");
    return;
}

if (args.Contains("--opcode-inventory-only", StringComparer.Ordinal))
{
    OpcodeInventoryClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: OpcodeInventory PASS");
    return;
}

if (args.Contains("--target-bindings-only", StringComparer.Ordinal))
{
    TargetBindingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TargetBindings PASS");
    return;
}

if (args.Contains("--actionbar-binding-tail-only", StringComparer.Ordinal))
{
    ActionBarBindingTailClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ActionBarBindingTail PASS");
    return;
}

if (args.Contains("--selection-ring-only", StringComparer.Ordinal))
{
    SelectionRingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SelectionRing PASS");
    return;
}

if (args.Contains("--target-press-pick-only", StringComparer.Ordinal))
{
    TargetPressPickClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TargetPressPick PASS");
    return;
}

if (args.Contains("--target-click-only", StringComparer.Ordinal))
{
    TargetClickClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TargetClick PASS");
    return;
}

if (args.Contains("--feed-pet-only", StringComparer.Ordinal))
{
    FeedPetClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: FeedPet PASS");
    return;
}

if (args.Contains("--target-mesh-pick-only", StringComparer.Ordinal))
{
    TargetMeshPickClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TargetMeshPick PASS");
    return;
}

if (args.Contains("--spatial-audio-only", StringComparer.Ordinal))
{
    SpatialAudioClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SpatialAudio PASS");
    return;
}

if (args.Contains("--water-splash-only", StringComparer.Ordinal))
{
    WaterSplashClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: WaterSplash PASS");
    return;
}

if (args.Contains("--wmo-liquid-point-only", StringComparer.Ordinal))
{
    WmoLiquidPointClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: WmoLiquidPoint PASS");
    return;
}

if (args.Contains("--liquid-ambient-loop-only", StringComparer.Ordinal))
{
    LiquidAmbientLoopClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: LiquidAmbientLoop PASS");
    return;
}

if (args.Contains("--elevator-transport-only", StringComparer.Ordinal))
{
    ElevatorTransportClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ElevatorTransport PASS");
    return;
}

if (args.Contains("--mo-transport-only", StringComparer.Ordinal))
{
    MoTransportClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MoTransport PASS");
    return;
}

if (args.Contains("--wmo-gameobject-only", StringComparer.Ordinal))
{
    WmoGameObjectClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: WmoGameObject PASS");
    return;
}

if (args.Contains("--controlled-transport-only", StringComparer.Ordinal))
{
    ControlledTransportClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ControlledTransport PASS");
    return;
}

if (args.Contains("--talent-frame-only", StringComparer.Ordinal))
{
    TalentFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TalentFrame PASS");
    return;
}

if (args.Contains("--guild-frame-only", StringComparer.Ordinal))
{
    GuildFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GuildFrame PASS");
    return;
}

if (args.Contains("--mail-frame-only", StringComparer.Ordinal))
{
    MailFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MailFrame PASS");
    return;
}

if (args.Contains("--reputation-frame-only", StringComparer.Ordinal))
{
    ReputationFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ReputationFrame PASS");
    return;
}

if (args.Contains("--pet-paper-doll-only", StringComparer.Ordinal))
{
    PetPaperDollClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PetPaperDollFrame PASS");
    return;
}

if (args.Contains("--dress-up-frame-only", StringComparer.Ordinal))
{
    DressUpFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DressUpFrame PASS");
    return;
}

if (args.Contains("--screenshot-status-only", StringComparer.Ordinal))
{
    ScreenshotStatusClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ScreenshotStatus PASS");
    return;
}

if (args.Contains("--pet-spellbook-only", StringComparer.Ordinal))
{
    PetSpellBookClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PetSpellBook PASS");
    return;
}

if (args.Contains("--pet-menu-only", StringComparer.Ordinal))
{
    PetMenuClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PetMenu PASS");
    return;
}

if (args.Contains("--taxi-frame-only", StringComparer.Ordinal))
{
    TaxiFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TaxiFrame PASS");
    return;
}

if (args.Contains("--bank-frame-only", StringComparer.Ordinal))
{
    BankFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: BankFrame PASS");
    return;
}

if (args.Contains("--pet-name-only", StringComparer.Ordinal))
{
    PetNameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PetName PASS");
    return;
}

if (args.Contains("--player-name-only", StringComparer.Ordinal))
{
    PlayerNameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PlayerName PASS");
    return;
}

if (args.Contains("--camera-pose-only", StringComparer.Ordinal))
{
    CameraPoseClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CameraPose PASS");
    return;
}

if (args.Contains("--camera-follow-only", StringComparer.Ordinal))
{
    CameraFollowClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CameraFollow PASS");
    return;
}

if (args.Contains("--equipment-display-only", StringComparer.Ordinal))
{
    EquipmentDisplayPreferenceClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: EquipmentDisplayPreference PASS");
    return;
}

if (args.Contains("--chat-language-only", StringComparer.Ordinal))
{
    ChatLanguageClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ChatLanguage PASS");
    return;
}

if (args.Contains("--gameobject-cast-only", StringComparer.Ordinal))
{
    GameObjectCastClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameObjectCast PASS");
    return;
}

if (args.Contains("--session-transfer-only", StringComparer.Ordinal))
{
    SessionTransferClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SessionTransfer PASS");
    return;
}

if (args.Contains("--exploration-only", StringComparer.Ordinal))
{
    ExplorationClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Exploration PASS");
    return;
}

if (args.Contains("--proficiency-only", StringComparer.Ordinal))
{
    ProficiencyClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Proficiency PASS");
    return;
}

if (args.Contains("--item-tooltip-only", StringComparer.Ordinal))
{
    GameTooltipClinicalChecks.RunItemSnapshotOnly();
    Console.WriteLine("interface-wire-check: ItemTooltip PASS");
    return;
}

if (args.Contains("--group-loot-only", StringComparer.Ordinal))
{
    GroupLootClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GroupLootFrame PASS");
    return;
}

if (args.Contains("--death-frame-only", StringComparer.Ordinal))
{
    DeathFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DeathFrame PASS");
    return;
}

if (args.Contains("--mirror-timer-only", StringComparer.Ordinal))
{
    MirrorTimerClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MirrorTimer PASS");
    return;
}

if (args.Contains("--ui-errors-only", StringComparer.Ordinal))
{
    UiErrorsFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UIErrorsFrame PASS");
    return;
}

if (args.Contains("--inventory-failure-only", StringComparer.Ordinal))
{
    InventoryFailureClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: InventoryFailure PASS");
    return;
}

if (args.Contains("--mount-result-only", StringComparer.Ordinal))
{
    MountResultClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MountResult PASS");
    return;
}

if (args.Contains("--auto-repeat-only", StringComparer.Ordinal))
{
    AutoRepeatClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AutoRepeat PASS");
    return;
}

if (args.Contains("--open-item-only", StringComparer.Ordinal))
{
    OpenItemClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: OpenItem PASS");
    return;
}

if (args.Contains("--item-enchant-only", StringComparer.Ordinal))
{
    ItemEnchantClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ItemEnchant PASS");
    return;
}

if (args.Contains("--delete-item-only", StringComparer.Ordinal))
{
    DeleteItemClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DeleteItem PASS");
    return;
}

if (args.Contains("--stack-split-only", StringComparer.Ordinal))
{
    StackSplitClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: StackSplit PASS");
    return;
}

if (args.Contains("--monster-move-only", StringComparer.Ordinal))
{
    MonsterMoveClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MonsterMove PASS");
    return;
}

if (args.Contains("--remote-movement-only", StringComparer.Ordinal))
{
    RemoteMovementClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: RemoteMovement PASS");
    return;
}

if (args.Contains("--compressed-movement-only", StringComparer.Ordinal))
{
    CompressedMovementClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CompressedMovement PASS");
    return;
}

if (args.Contains("--self-spline-only", StringComparer.Ordinal))
{
    SelfSplineClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SelfSpline PASS");
    return;
}

if (args.Contains("--swimming-only", StringComparer.Ordinal))
{
    SwimmingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Swimming PASS");
    return;
}

if (args.Contains("--drunk-only", StringComparer.Ordinal))
{
    DrunkMovementClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: DrunkMovement PASS");
    return;
}

if (args.Contains("--item-glow-only", StringComparer.Ordinal))
{
    ItemGlowClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ItemGlow PASS");
    return;
}

if (args.Contains("--carried-light-only", StringComparer.Ordinal))
{
    CarriedLightClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CarriedLight PASS");
    return;
}

if (args.Contains("--spell-chain-beam-only", StringComparer.Ordinal))
{
    SpellChainBeamClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SpellChainBeam PASS");
    return;
}

if (args.Contains("--force-speed-only", StringComparer.Ordinal))
{
    ForceSpeedClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ForceSpeed PASS");
    return;
}

if (args.Contains("--observer-speed-only", StringComparer.Ordinal))
{
    ObserverSpeedClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ObserverSpeed PASS");
    return;
}

if (args.Contains("--observer-body-only", StringComparer.Ordinal))
{
    ObserverBodyOwnershipClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ObserverBodyOwnership PASS");
    return;
}

if (args.Contains("--movement-mode-only", StringComparer.Ordinal))
{
    MovementModeClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MovementMode PASS");
    return;
}

if (args.Contains("--combat-text-state-only", StringComparer.Ordinal))
{
    CombatTextStateClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CombatTextState PASS");
    return;
}

if (args.Contains("--ai-reaction-only", StringComparer.Ordinal))
{
    AiReactionClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AiReaction PASS");
    return;
}

if (args.Contains("--stand-state-only", StringComparer.Ordinal))
{
    StandStateClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: StandState PASS");
    return;
}

if (args.Contains("--mount-special-only", StringComparer.Ordinal))
{
    MountSpecialClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MountSpecial PASS");
    return;
}

if (args.Contains("--mount-rendering-only", StringComparer.Ordinal))
{
    MountRenderingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MountRendering PASS");
    return;
}

if (args.Contains("--emote-animation-only", StringComparer.Ordinal))
{
    EmoteAnimationClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: EmoteAnimation PASS");
    return;
}

if (args.Contains("--group-slash-only", StringComparer.Ordinal))
{
    GroupSlashClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GroupSlash PASS");
    return;
}

if (args.Contains("--chat-tab-only", StringComparer.Ordinal))
{
    ChatTabClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ChatTab PASS");
    return;
}

if (args.Contains("--chat-menu-only", StringComparer.Ordinal))
{
    ChatMenuClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ChatMenu PASS");
    return;
}

if (args.Contains("--chat-channel-only", StringComparer.Ordinal))
{
    ChatChannelClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ChatChannel PASS");
    return;
}

if (args.Contains("--acquisition-sound-only", StringComparer.Ordinal))
{
    AcquisitionSoundClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AcquisitionSound PASS");
    return;
}

if (args.Contains("--spellbook-visual-only", StringComparer.Ordinal))
{
    SpellbookVisualClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SpellbookVisual PASS");
    return;
}

if (args.Contains("--bag-state-art-only", StringComparer.Ordinal))
{
    BagStateArtClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: BagStateArt PASS");
    return;
}

if (args.Contains("--combo-frame-only", StringComparer.Ordinal))
{
    ComboFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ComboFrame PASS");
    return;
}

if (args.Contains("--nameplate-only", StringComparer.Ordinal))
{
    NameplateClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Nameplate PASS");
    return;
}

if (args.Contains("--ui-hide-only", StringComparer.Ordinal))
{
    UiHideClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UiHide PASS");
    return;
}

if (args.Contains("--world-map-only", StringComparer.Ordinal))
{
    WorldMapClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: WorldMap PASS");
    return;
}

if (args.Contains("--macro-frame-only", StringComparer.Ordinal))
{
    MacroFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MacroFrame PASS");
    return;
}

if (args.Contains("--binding-chord-only", StringComparer.Ordinal))
{
    BindingChordClinicalChecks.Run();
    UiHighlightBlendClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: BindingChord PASS");
    return;
}

if (args.Contains("--highlight-blend-only", StringComparer.Ordinal))
{
    UiHighlightBlendClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UiHighlightBlend PASS");
    return;
}

if (args.Contains("--target-cycle-only", StringComparer.Ordinal))
{
    TargetCycleClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: TargetCycle PASS");
    return;
}

if (args.Contains("--social-tab-bindings-only", StringComparer.Ordinal))
{
    SocialTabBindingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: SocialTabBindings PASS");
    return;
}

if (args.Contains("--audio-bindings-only", StringComparer.Ordinal))
{
    AudioBindingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: AudioBindings PASS");
    return;
}

if (args.Contains("--minimap-binding-only", StringComparer.Ordinal))
{
    MinimapBindingClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MinimapBinding PASS");
    return;
}

if (args.Contains("--keybinding-registry-only", StringComparer.Ordinal))
{
    KeyBindingRegistryClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: KeyBindingRegistry PASS");
    return;
}

if (args.Contains("--character-bindings-only", StringComparer.Ordinal))
{
    CharacterBindingsClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: CharacterBindings PASS");
    return;
}

if (args.Contains("--scoped-view-only", StringComparer.Ordinal))
{
    ScopedViewClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ScopedView PASS");
    return;
}

if (args.Contains("--view-subject-only", StringComparer.Ordinal))
{
    ViewSubjectClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ViewSubject PASS");
    return;
}

if (args.Contains("--client-control-only", StringComparer.Ordinal))
{
    ClientControlUpdateClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: ClientControlUpdate PASS");
    return;
}

if (args.Contains("--micro-menu-only", StringComparer.Ordinal))
{
    MicroMenuClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MicroMenu PASS");
    return;
}

if (args.Contains("--chat-only", StringComparer.Ordinal))
{
    ChatClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: Chat PASS");
    return;
}

if (args.Contains("--game-menu-layout-only", StringComparer.Ordinal))
{
    CheckGameMenuLayout();
    Console.WriteLine("interface-wire-check: GameMenuLayout PASS");
    return;
}

if (args.Contains("--lighting-default-only", StringComparer.Ordinal))
{
    CheckLightingDefaults();
    Console.WriteLine("interface-wire-check: LightingDefaults PASS");
    return;
}

Check(!ClientWindow.CameraLookRequested(
          leftDown: true, rightDown: false, freeSelectMode: false, leftButtonReserved: true) &&
      ClientWindow.CameraLookRequested(
          leftDown: false, rightDown: true, freeSelectMode: false, leftButtonReserved: true) &&
      ClientWindow.CameraLookRequested(
          leftDown: true, rightDown: false, freeSelectMode: false, leftButtonReserved: false) &&
      !ClientWindow.CameraLookRequested(
          leftDown: true, rightDown: false, freeSelectMode: true, leftButtonReserved: false),
    "world-editor/free-select left ownership leaked into camera look");
Check(new WorldMouseClick(Silk.NET.Input.MouseButton.Left, Vector2.Zero, ShiftDown: true).ShiftDown &&
      !new WorldMouseClick(Silk.NET.Input.MouseButton.Left, Vector2.Zero).ShiftDown,
    "world-editor click lost its gesture-captured Shift modifier");

if (args.Contains("--world-editor-input-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: WorldEditorInput PASS");
    return;
}

if (args.Contains("--party-frame-only", StringComparer.Ordinal))
{
    PartyFrameClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PartyFrame PASS");
    return;
}

if (args.Contains("--group-protocol-only", StringComparer.Ordinal))
{
    GroupProtocolClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GroupProtocol PASS");
    return;
}

if (args.Contains("--ui-foundation-only", StringComparer.Ordinal))
{
    UiFoundationClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UiFoundation PASS");
    return;
}

if (args.Contains("--game-tooltip-only", StringComparer.Ordinal))
{
    GameTooltipClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: GameTooltip PASS");
    return;
}

if (args.Contains("--uipanel-observer-only", StringComparer.Ordinal))
{
    UiPanelOwnershipAdapterClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UiPanelObserver PASS");
    return;
}

if (args.Contains("--merchant-frame-only", StringComparer.Ordinal))
{
    MerchantFrameClinicalChecks.Run();
    MerchantProtocolClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: MerchantFrame PASS");
    return;
}

if (args.Contains("--merchant-tooltip-only", StringComparer.Ordinal))
{
    MerchantFrameClinicalChecks.RunTooltipOnly();
    Console.WriteLine("interface-wire-check: MerchantTooltip PASS");
    return;
}

if (args.Contains("--unit-popup-only", StringComparer.Ordinal))
{
    UnitPopupClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: UnitPopup PASS");
    return;
}

if (args.Contains("--item-template-only", StringComparer.Ordinal))
{
    CheckCurrentItemTemplateParser();
    Console.WriteLine("interface-wire-check: ItemTemplate PASS");
    return;
}

if (args.Contains("--party-member-facts-only", StringComparer.Ordinal))
{
    PartyMemberFactsClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PartyMemberFacts PASS");
    return;
}

if (args.Contains("--party-quest-only", StringComparer.Ordinal))
{
    PartyQuestClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PartyQuest PASS");
    return;
}

if (args.Contains("--party-quest-acts-only", StringComparer.Ordinal))
{
    PartyQuestActsClinicalChecks.Run();
    PartyGiverStatusClinicalChecks.Run();
    PartyLeadClinicalChecks.Run();
    Console.WriteLine("interface-wire-check: PartyQuestActs PASS");
    return;
}

if (args.Contains("--companions-only", StringComparer.Ordinal))
{
    CompanionClinicalChecks.Run();
    return;
}

if (args.Contains("--party-taxi-only", StringComparer.Ordinal))
{
    PartyTaxiClinicalChecks.Run();
    return;
}

if (args.Contains("--tactical-freeze-only", StringComparer.Ordinal))
{
    TacticalFreezeClinicalChecks.Run();
    return;
}

if (args.Contains("--possess-law-only", StringComparer.Ordinal))
{
    PossessLawClinicalChecks.Run();
    return;
}

if (args.Contains("--rts-ability-target-only", StringComparer.Ordinal))
{
    string data = ClientDataRoot.Path;
    using var mpq = new MpqMount(data);
    SpellCatalog spells = SpellCatalog.Load(mpq) ??
        throw new InvalidDataException("Spell DBC unavailable");
    CheckRtsAbilityTargeting(spells.Spells.First());
    SpellInfo RequiredSpell(uint id) => spells.TryGet(id, out SpellInfo spell)
        ? spell : throw new InvalidDataException($"spell {id} missing");
    Check(CastTargetLaw.AcceptsExplicitFriendlyUnit(RequiredSpell(2050)) &&
          CastTargetLaw.AcceptsExplicitFriendlyUnit(RequiredSpell(1243)) &&
          CastTargetLaw.AcceptsExplicitFriendlyUnit(RequiredSpell(2006)) &&
          !CastTargetLaw.AcceptsExplicitFriendlyUnit(RequiredSpell(6673)),
        "real Spell.dbc heal/buff/resurrection/party-wide classification drift");
    Console.WriteLine("interface-wire-check: RtsAbilityTarget PASS");
    return;
}

CharCreateClinicalChecks.Run();
CharSelectCurrentClinicalChecks.Run();
LoginClinicalChecks.Run();
AuctionFrameClinicalChecks.Run();
HelpFrameClinicalChecks.Run();
TabardFrameClinicalChecks.Run();
UiFoundationClinicalChecks.Run();
TalentFrameClinicalChecks.Run();
GuildFrameClinicalChecks.Run();
MailFrameClinicalChecks.Run();
ReputationFrameClinicalChecks.Run();
FeedPetClinicalChecks.Run();
PetPaperDollClinicalChecks.Run();
DressUpFrameClinicalChecks.Run();
ScreenshotStatusClinicalChecks.Run();
PetSpellBookClinicalChecks.Run();
PetMenuClinicalChecks.Run();
GameTooltipClinicalChecks.Run();
UiPanelOwnershipAdapterClinicalChecks.Run();
MerchantFrameClinicalChecks.Run();
MerchantProtocolClinicalChecks.Run();
UnitPopupClinicalChecks.Run();
HardwareCursorClinicalChecks.Run();
CooldownProtocolClinicalChecks.Run();
TradeProtocolClinicalChecks.Run();
SocialProtocolClinicalChecks.Run();
ChatClinicalChecks.Run();
AuraVisualClinicalChecks.Run();
ModelLightingClinicalChecks.Run();
RealmLogonClinicalChecks.Run();
AuthSessionAddonClinicalChecks.Run();
NetworkTelemetryClinicalChecks.Run();
RemoteMovementClinicalChecks.Run();
SwimmingClinicalChecks.Run();
DrunkMovementClinicalChecks.Run();
ItemGlowClinicalChecks.Run();
CarriedLightClinicalChecks.Run();
ObserverSpeedClinicalChecks.Run();
ObserverBodyOwnershipClinicalChecks.Run();
CompressedMovementClinicalChecks.Run();
SelfSplineClinicalChecks.Run();
SpellChainBeamClinicalChecks.Run();
GameObjectAnimationClinicalChecks.Run();
GlueAudioClinicalChecks.Run();
FishingLineClinicalChecks.Run();
OpcodeInventoryClinicalChecks.Run();
TargetBindingClinicalChecks.Run();
ActionBarBindingTailClinicalChecks.Run();
SelectionRingClinicalChecks.Run();
TargetPressPickClinicalChecks.Run();
TargetClickClinicalChecks.Run();
TargetMeshPickClinicalChecks.Run();
SpatialAudioClinicalChecks.Run();
WaterSplashClinicalChecks.Run();
WmoLiquidPointClinicalChecks.Run();
LiquidAmbientLoopClinicalChecks.Run();
ElevatorTransportClinicalChecks.Run();
MoTransportClinicalChecks.Run();
WmoGameObjectClinicalChecks.Run();
ControlledTransportClinicalChecks.Run();

static byte[] BuildMultiActionItemTemplateFixture()
{
    var w = new PacketWriter();
    w.WriteU32(42); w.WriteU32(0); w.WriteU32(0); w.WriteCString("Clinical Item");
    w.WriteCString(""); w.WriteCString(""); w.WriteCString("");
    w.WriteU32(0); // display
    w.WriteU32(1); w.WriteU32(0); w.WriteU32(0); w.WriteU32(0); w.WriteU32(0);
    w.WriteI32(-1); w.WriteI32(-1);
    for (int i = 0; i < 5; i++) w.WriteU32(0); // item/required levels, skills, spell
    for (int i = 0; i < 4; i++) w.WriteU32(0); // honor/city/reputation
    w.WriteU32(0); w.WriteU32(20); w.WriteU32(0); // max count, stack, container slots
    for (int i = 0; i < 10; i++) { w.WriteU32(0); w.WriteI32(0); }
    for (int i = 0; i < 5; i++) { w.WriteF32(0); w.WriteF32(0); w.WriteU32(0); }
    w.WriteU32(0);
    for (int i = 0; i < 6; i++) w.WriteU32(0);
    w.WriteU32(0); w.WriteU32(0); w.WriteF32(0);
    // Block zero is ON_EQUIP with finite charges; block one is the first ON_USE spell.
    w.WriteU32(111); w.WriteU32(1); w.WriteI32(2); w.WriteI32(0); w.WriteU32(0); w.WriteI32(0);
    w.WriteU32(8690); w.WriteU32(0); w.WriteI32(-3); w.WriteI32(0); w.WriteU32(321); w.WriteI32(0);
    for (int block = 2; block < 5; block++)
    { w.WriteU32(0); w.WriteU32(0); w.WriteI32(0); w.WriteI32(0); w.WriteU32(0); w.WriteI32(0); }
    w.WriteU32(0); w.WriteCString("fixture");
    w.WriteU32(0); w.WriteU32(0); w.WriteU32(0); // page/language/material
    w.WriteU32(373); w.WriteU32(0); // start quest / lock
    w.WriteU32(0); w.WriteU32(0); // material / sheath
    w.WriteU32(777); w.WriteU32(0); // random property / block
    w.WriteU32(25); w.WriteU32(40); // item set / max durability
    w.WriteU32(0); w.WriteU32(0); // area / map
    w.WriteU32(9); // bag family
    return w.ToArray();
}

static void CheckCurrentItemTemplateParser()
{
    ItemTemplate parsed = ItemTemplate.Parse(BuildMultiActionItemTemplateFixture())
        ?? throw new InvalidDataException("item-template fixture did not parse");
    Check(parsed.Entry == 42 && parsed.SpellCharges0 == 2 &&
          parsed.UseSpellIndex == 1 && parsed.UseSpellId == 8690 &&
          parsed.UseSpellCharges == -3 && parsed.UseSpellCategory == 321 &&
          parsed.HasNegativeOnUseCharges && parsed.StartQuest == 373 &&
          parsed.Spells[0] == new ItemSpellTemplate(111, 1, 2, 0, 0, 0) &&
          parsed.Spells[1] == new ItemSpellTemplate(8690, 0, -3, 0, 321, 0) &&
          parsed.RandomProperty == 777 && parsed.ItemSet == 25 &&
          parsed.MaxDurability == 40 && parsed.BagFamily == 9,
        "item template five-spell/random-property/item-set/durability parser drift");
}

Check(BuffUiLaw.WarningAlpha(.75, 30) == 1f && BuffUiLaw.WarningAlpha(0, 30) == .3f &&
      BuffUiLaw.WarningAlpha(0, 31) == 1f,
    "BuffFrame shared 31-second flash drift");
Check(BuffUiLaw.DebuffColor(1) == new Vector4(.2f, .6f, 1f, 1f) &&
      BuffUiLaw.DebuffColor(4) == new Vector4(0f, .6f, 0f, 1f) &&
      BuffUiLaw.DebuffColor(0) == new Vector4(.8f, 0f, 0f, 1f),
    "DebuffTypeColor mapping drift");
Check(BuffUiLaw.ButtonSize == 30f && BuffUiLaw.Columns == 8 &&
      BuffUiLaw.HelpfulLimit == 16 && BuffUiLaw.HarmfulLimit == 8 &&
      BuffUiLaw.ColumnStep == 35f && BuffUiLaw.RowStep == 45f &&
      BuffUiLaw.DurationGutter == 15f &&
      BuffUiLaw.DebuffTexCoords == new Vector4(.296875f, 0f, .5703125f, .515625f),
    "BuffFrame 16+8 grid/button/gutter/debuff-UV contract drift");
Vector2 buffFrame = BuffUiLaw.FrameMin(new Vector2(1600, 900));
Vector2 buff0 = BuffUiLaw.ButtonMin(buffFrame, harmful: false, cohort: 0);
Vector2 buff7 = BuffUiLaw.ButtonMin(buffFrame, harmful: false, cohort: 7);
Vector2 buff8 = BuffUiLaw.ButtonMin(buffFrame, harmful: false, cohort: 8);
Vector2 debuff0 = BuffUiLaw.ButtonMin(buffFrame, harmful: true, cohort: 0);
Check(buffFrame == new Vector2(1345, 13) && buff0 == new Vector2(1365, 13) &&
      buff7 == new Vector2(1120, 13) && buff8 == new Vector2(1365, 58) &&
      debuff0 == new Vector2(1365, 103),
    $"BuffFrame preserved horizontal seat or exact 35x45 grid drift: " +
    $"frame={buffFrame};b0={buff0};b7={buff7};b8={buff8};d0={debuff0}");
Vector2 lastBorderMin = BuffUiLaw.ButtonMin(buffFrame, harmful: true, cohort: 7) -
    new Vector2(BuffUiLaw.DebuffBorderExpandX, BuffUiLaw.DebuffBorderExpandY);
Vector2 lastBorderMax = BuffUiLaw.ButtonMin(buffFrame, harmful: true, cohort: 7) +
    new Vector2(BuffUiLaw.ButtonSize + BuffUiLaw.DebuffBorderExpandX,
        BuffUiLaw.ButtonSize + BuffUiLaw.DebuffBorderExpandY);
Vector2 lastDurationMin = debuff0 + new Vector2(0, BuffUiLaw.ButtonSize + 1);
Vector2 lastDurationMax = lastDurationMin + new Vector2(40, BuffUiLaw.DurationTextHeight);
Check(BuffUiLaw.RowStep >= BuffUiLaw.ButtonSize + BuffUiLaw.DurationTextHeight &&
      BuffUiLaw.WithinAuraWindow(buffFrame, lastBorderMin, lastBorderMax) &&
      BuffUiLaw.WithinAuraWindow(buffFrame, lastDurationMin, lastDurationMax),
    "BuffFrame duration/border escaped its zero-padding clip or overlaps the next row");
Check(BuffUiLaw.DurationBelongsToAura(9.0, 10.0) &&
      !BuffUiLaw.DurationBelongsToAura(8.999, 10.0) &&
      BuffUiLaw.DurationBelongsToAura(20.0, 10.0),
    "BuffFrame pre-descriptor duration slack/recycled-slot rejection drift");
Check(BuffUiLaw.PreserveAcrossWorldEnter(0x1234, 0x1234) &&
      !BuffUiLaw.PreserveAcrossWorldEnter(0x1234, 0x5678) &&
      !BuffUiLaw.PreserveAcrossWorldEnter(0, 0),
    "BuffFrame cross-map preservation/session-owner reset drift");
BuffUiLaw.AuraKey[] auraOrder = BuffUiLaw.ReconcileOrder(
    [new(5, 100), new(2, 200), new(7, 300)],
    [new(1, 400), new(2, 200), new(5, 100)]);
Check(auraOrder.SequenceEqual(new BuffUiLaw.AuraKey[]
      { new(5, 100), new(2, 200), new(1, 400) }),
    "BuffFrame survivors lost insertion order or removal/new-slot repacking drifted");
BuffUiLaw.AuraKey[] recycledAuraOrder = BuffUiLaw.ReconcileOrder(
    [new(5, 100), new(2, 200)], [new(2, 200), new(5, 999)]);
Check(recycledAuraOrder.SequenceEqual(new BuffUiLaw.AuraKey[]
      { new(2, 200), new(5, 999) }),
    "BuffFrame recycled slot retained the old spell identity/position");
Check(CastingBarUiLaw.BottomOffset(false, false, false, false) == 60f &&
      CastingBarUiLaw.BottomOffset(true, false, false, false) == 100f &&
      CastingBarUiLaw.BottomOffset(false, true, false, false) == 100f &&
      CastingBarUiLaw.BottomOffset(true, true, true, true) == 149f &&
      CastingBarUiLaw.BottomOffsetForMsui(false, false) == 100f &&
      CastingBarUiLaw.BottomOffsetForMsui(true, true) == 149f,
    "UIParent managed CastingBarFrame bottom stack drift");
Check(CastingBarUiLaw.FlashAlpha(1d / 12d) == .5f &&
      CastingBarUiLaw.FrameAlpha(1d / 6d, false) == 1f &&
      CastingBarUiLaw.FrameAlpha(5d / 6d, false) == 0f &&
      CastingBarUiLaw.FrameAlpha(1d, true) == 1f &&
      CastingBarUiLaw.FrameAlpha(5d / 3d, true) == 0f,
    "CastingBar 30-Hz-normalized flash/hold/fade timing drift");
Check(CastingBarUiLaw.Progress(10, 14, 11, channel: false) == .25f &&
      CastingBarUiLaw.Progress(10, 14, 11, channel: true) == .75f &&
      CastingBarUiLaw.Progress(10, 14, 9, channel: false) == 0f &&
      CastingBarUiLaw.Progress(10, 14, 15, channel: true) == 0f,
    "CastingBar forward/reverse/clamped progress drift");
CastingBarUiLaw.ChannelWindow retimed = CastingBarUiLaw.RetimeChannel(10, 16, 20, 2_000);
Check(retimed.Start == 16 && retimed.End == 22,
    "CastingBar channel update must preserve the original six-second duration");
CastingBarUiLaw.StatusFill quarterFill = CastingBarUiLaw.Fill(.25f);
Check(quarterFill == new CastingBarUiLaw.StatusFill(.25f, 48.75f, .25f) &&
      CastingBarUiLaw.SparkCenter(-1f) == 0f &&
      CastingBarUiLaw.SparkCenter(2f) == CastingBarUiLaw.Width &&
      CastingBarUiLaw.SparkMinY == -11.5f &&
      CastingBarUiLaw.SparkMaxY == 20.5f &&
      CastingBarUiLaw.ArtworkWidth == 256f && CastingBarUiLaw.ArtworkHeight == 64f &&
      CastingBarUiLaw.ArtworkTopOffset == 28f,
    "CastingBar status texture crop or spark endpoint clamp drift");
Check(CastingBarUiLaw.AcceptCastTerminal(true, 133, 133) &&
      !CastingBarUiLaw.AcceptCastTerminal(false, 133, 133) &&
      !CastingBarUiLaw.AcceptCastTerminal(true, 133, 6136),
    "CastingBar stale/proc completion guard drift");
Check(CastingBarUiLaw.TerminalText("INTERRUPTED") == CastingBarUiLaw.InterruptedText &&
      CastingBarUiLaw.TerminalText("You are out of range") == CastingBarUiLaw.FailedText,
    "CastingBar terminal label must stay Failed/Interrupted, not copy center-screen errors");
Check((ushort)Op.MSG_CHANNEL_START == 0x0139 &&
      (ushort)Op.MSG_CHANNEL_UPDATE == 0x013A &&
      (ushort)Op.SMSG_SPELL_DELAYED == 0x01E2,
    "build-5875 cast/channel lifecycle opcodes drift");
SpellDelayedPacket delayedCast = SpellLifecyclePacketParser.ParseDelayed(
    Convert.FromHexString("4200000000000000F4010000"));
SpellChannelStartPacket channelStart = SpellLifecyclePacketParser.ParseChannelStart(
    Convert.FromHexString("2D2A000070170000"));
uint channelRemaining = SpellLifecyclePacketParser.ParseChannelUpdate(
    Convert.FromHexString("B80B0000"));
Check(delayedCast == new SpellDelayedPacket(0x42, 500) &&
      channelStart == new SpellChannelStartPacket(10_797, 6_000) &&
      channelRemaining == 3_000,
    "cast/channel golden wire bodies drift (raw GUID; self-only u32 fields)");
Check((ushort)Op.CMSG_SET_AMMO == 0x0268 &&
      WorldSession.BuildSetAmmoBody(93012).SequenceEqual(Convert.FromHexString("546B0100")),
    "CMSG_SET_AMMO opcode/body drift");
Check(PaperDollUiLaw.ClickAction(true, false, true, false) == PaperDollUiLaw.SlotClickAction.None &&
      PaperDollUiLaw.ClickAction(true, false, true, false, true) == PaperDollUiLaw.SlotClickAction.PickupOrPlace &&
      PaperDollUiLaw.ClickAction(false, true, false, false) == PaperDollUiLaw.SlotClickAction.Use,
    "paper-doll modifier/drag/right-click routing drift");
Check(PaperDollUiLaw.FitsEquipmentSlot(11, 10) && PaperDollUiLaw.FitsEquipmentSlot(11, 11) &&
      PaperDollUiLaw.FitsEquipmentSlot(20, 4) && !PaperDollUiLaw.FitsEquipmentSlot(24, 17) &&
      PaperDollUiLaw.IsAmmo(24),
    "paper-doll inventoryType fit table/ammo fork drift");
CheckPaperDollRegressions();
Check(PaperDollUiLaw.IconTint(true, true) == PaperDollUiLaw.Locked &&
      PaperDollUiLaw.IconTint(false, true) == PaperDollUiLaw.Broken &&
      PaperDollUiLaw.RingTint(true, true) == PaperDollUiLaw.Fits &&
      PaperDollUiLaw.IsBroken(0, 0, 40) && PaperDollUiLaw.IsBroken(0x10, 40, 40) &&
      !PaperDollUiLaw.IsBroken(0x08, 0, 40) && !PaperDollUiLaw.IsBroken(0, 0, 0) &&
      MathF.Abs(PaperDollUiLaw.ClickFacing(0, true) + .12f) < .0001f &&
      MathF.Abs(PaperDollUiLaw.HeldFacing(0, true, 1) - MathF.PI) < .0001f &&
      PaperDollUiLaw.LiveAnimationStep(10.01, 10.0) > .009f &&
      PaperDollUiLaw.LiveAnimationStep(11.0, 10.0) == PaperDollUiLaw.LiveAnimationMaxStep &&
      PaperDollUiLaw.LiveAnimationStep(10.0, 0) == 0f,
    "paper-doll lock/broken/cursor tint or rotation law drift");
Check(PaperDollUiLaw.ModifierTextColor(1, 0) == 0xff20ff20u &&
      PaperDollUiLaw.ModifierTextColor(1, -1) == PaperDollUiLaw.Broken,
    "paper-doll positive/negative stat color drift");
Check(PaperDollUiLaw.ResistanceTextColor(20, -5) == 0xff20ff20u &&
      PaperDollUiLaw.ResistanceTextColor(5, -5) is null &&
      PaperDollUiLaw.ResistanceTextColor(4, -5) == PaperDollUiLaw.Broken,
    "paper-doll resistance magnitude/tie color law drift");
PaperDollUiLaw.DamageTooltipData damageBreakdown = PaperDollUiLaw.DamageTooltip(
    55f, 75f, 10, -2, 1.1f, 2.9f);
Check(damageBreakdown.Damage == "42 - 61 +10 -2 x110%" &&
      MathF.Abs(damageBreakdown.AttackSpeed - 2.9f) < .0001f &&
      MathF.Abs(damageBreakdown.Dps - 22.41379f) < .001f &&
      ObjectFields.PLAYER_FIELD_MOD_DAMAGE_DONE_POS == 1201 &&
      ObjectFields.PLAYER_FIELD_MOD_DAMAGE_DONE_NEG == 1208 &&
      ObjectFields.PLAYER_FIELD_MOD_DAMAGE_DONE_PCT == 1215 &&
      new ObjectFields().AsCreated().DamageDonePercent(0) == 1f,
    $"paper-doll physical damage decomposition/wire defaults drift: {damageBreakdown}");
Check(PaperDollUiLaw.FrameWidth == 384 && PaperDollUiLaw.FrameHeight == 512 &&
      PaperDollUiLaw.PortraitRect == new PaperDollUiLaw.LogicalRect(7, 6, 60, 60) &&
      PaperDollUiLaw.ModelRect == new PaperDollUiLaw.LogicalRect(65, 78, 233, 224) &&
      PaperDollUiLaw.EquipmentSlotRect(0) == new PaperDollUiLaw.LogicalRect(21, 74, 37, 37) &&
      PaperDollUiLaw.EquipmentSlotRect(14) == new PaperDollUiLaw.LogicalRect(21, 197, 37, 37) &&
      PaperDollUiLaw.EquipmentSlotRect(9) == new PaperDollUiLaw.LogicalRect(305, 74, 37, 37) &&
      PaperDollUiLaw.EquipmentSlotRect(17) == new PaperDollUiLaw.LogicalRect(206, 385, 37, 37) &&
      PaperDollUiLaw.EquipmentSlotLabel(14) == "Back" &&
      PaperDollUiLaw.EquipmentSlotLabel(16) == "Off Hand" &&
      PaperDollUiLaw.AmmoHitRect == new PaperDollUiLaw.LogicalRect(258, 390, 27, 27) &&
      PaperDollUiLaw.AmmoBackgroundRect == new PaperDollUiLaw.LogicalRect(251, 383, 41, 41) &&
      PaperDollUiLaw.AmmoOverlayRect == new PaperDollUiLaw.LogicalRect(238, 383, 23, 41),
    "paper-doll authored frame/portrait/model/equipment/ammo geometry drift");
Check(PaperDollUiLaw.ContainerSlotScanCount(0) == 0 &&
      PaperDollUiLaw.ContainerSlotScanCount(22) == 22 &&
      PaperDollUiLaw.ContainerSlotScanCount(42) == PaperDollUiLaw.MaxContainerSlots,
    "paper-doll dynamic bag scan must stay within the 36-slot object-field boundary");
string strengthTooltip = PaperDollUiLaw.PrimaryStatTooltip("Strength", 15, 2, 0);
string agilityTooltip = PaperDollUiLaw.PrimaryStatTooltip("Agility", 20, 0, -2);
Check(strengthTooltip == "Strength 15 (11+2)" && agilityTooltip == "Agility 20 (24 -2)",
    $"paper-doll primary-stat modifier decomposition drift: {strengthTooltip}; {agilityTooltip}");
string armorTooltip = PaperDollUiLaw.ModifierTooltip("Armor", 120, 0, 0);
string attackPowerTooltip = PaperDollUiLaw.ModifierTooltip("Melee Attack Power", 98, 30, -10);
string fireTooltip = PaperDollUiLaw.ResistanceTooltip("Fire Resistance", 20, 25, -5);
Check(armorTooltip == "Armor 120" && attackPowerTooltip == "Melee Attack Power 98 (78+30 -10)" &&
      fireTooltip == "Fire Resistance 20 ( 0 +25 -5 )",
    $"paper-doll modifier/resistance tooltip formatting drift: {armorTooltip}; {attackPowerTooltip}; {fireTooltip}");
string rating0 = PaperDollUiLaw.ResistanceRating(0, 1);
string rating26 = PaperDollUiLaw.ResistanceRating(26, 1);
string rating101 = PaperDollUiLaw.ResistanceRating(101, 20);
Check(rating0 == "None" && rating26 == "Fair" && rating101 == "Excellent",
    $"paper-doll resistance threshold drift: {rating0}; {rating26}; {rating101}");
float armor120 = PaperDollUiLaw.ArmorReductionPercent(120, 12);
float armorNegative = PaperDollUiLaw.ArmorReductionPercent(-100, 1);
Check(MathF.Abs(armor120 - 7.7922f) < .001f && MathF.Abs(armorNegative + 25.974f) < .001f,
    $"paper-doll armor reduction formula drift: {armor120}; {armorNegative}");
string armorSubtext = PaperDollUiLaw.ArmorTooltipSubtext(120, 12);
string resistanceSubtext = PaperDollUiLaw.ResistanceTooltipSubtext("fire", 20, 1);
Check(armorSubtext ==
          "Decreases the amount of damage taken from physical attacks.  The amount of reduction is influenced by the level of the attacker.\nDamage reduction against a level 12 attacker: 7.8%" &&
      resistanceSubtext ==
          "Increases the ability to resist fire-based attacks, spells and abilities.\nResistance against level 20: Poor",
    $"paper-doll dynamic stat/resistance subtext drift: {armorSubtext}; {resistanceSubtext}");
Check(PaperDollUiLaw.TooltipWrapWidth == 260f &&
      PaperDollUiLaw.ComparisonSlotCount(1) == 1 &&
      PaperDollUiLaw.ComparisonSlot(1, 0) == 0 &&
      PaperDollUiLaw.ComparisonSlotCount(11) == 2 &&
      PaperDollUiLaw.ComparisonSlot(11, 0) == 10 &&
      PaperDollUiLaw.ComparisonSlot(11, 1) == 11 &&
      PaperDollUiLaw.ComparisonSlotCount(13) == 2 &&
      PaperDollUiLaw.ComparisonSlot(13, 0) == 15 &&
      PaperDollUiLaw.ComparisonSlot(13, 1) == 16 &&
      PaperDollUiLaw.ComparisonSlotCount(17) == 2 &&
      PaperDollUiLaw.ComparisonSlot(17, 0) == 15 &&
      PaperDollUiLaw.ComparisonSlot(17, 1) == 16 &&
      PaperDollUiLaw.ComparisonSlotCount(28) == 1 &&
      PaperDollUiLaw.ComparisonSlot(28, 0) == 17 &&
      PaperDollUiLaw.ComparisonSlotCount(18) == 0 &&
      PaperDollUiLaw.ComparisonSlotCount(24) == 0 &&
      PaperDollUiLaw.ShowBagItemComparison(true, 0, true, false) &&
      !PaperDollUiLaw.ShowBagItemComparison(false, 0, true, false) &&
      !PaperDollUiLaw.ShowBagItemComparison(true, 3, true, false) &&
      !PaperDollUiLaw.ShowBagItemComparison(true, 0, false, false) &&
      !PaperDollUiLaw.ShowBagItemComparison(true, 0, true, true) &&
      PaperDollUiLaw.ShoppingTooltipAnchor(0) ==
          new PaperDollUiLaw.TooltipAnchor("BOTTOMLEFT", "TOPRIGHT", 0, 1) &&
      PaperDollUiLaw.ShoppingTooltipAnchor(1) ==
          new PaperDollUiLaw.TooltipAnchor("TOPLEFT", "BOTTOMRIGHT", 0, 0),
    "paper-doll shift-compare routing or wrapped tooltip boundary drift");
Check(PaperDollUiLaw.OpenSound == new PaperDollUiLaw.SoundTransition("igCharacterInfoOpen", 1) &&
      PaperDollUiLaw.CloseSound == new PaperDollUiLaw.SoundTransition("igCharacterInfoClose", 1) &&
      PaperDollUiLaw.TabSwitchSound == new PaperDollUiLaw.SoundTransition("igCharacterInfoTab", 2) &&
      PaperDollUiLaw.RotateTapSound == new PaperDollUiLaw.SoundTransition("igInventoryRotateCharacter", 2),
    "CharacterFrame audio cue/count law drift");
string characterPageSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.CharacterPage.cs"));
int portraitDraw = characterPageSource.IndexOf("DrawCharacterPortrait(dl, origin, scale, player, panelClip);",
    StringComparison.Ordinal);
int backgroundDraw = characterPageSource.IndexOf("DrawPaperDollBackground(dl, origin, scale);",
    StringComparison.Ordinal);
Check(portraitDraw >= 0 && backgroundDraw > portraitDraw,
    "CharacterFrame portrait must paint before the page background's authored round aperture");
Check(characterPageSource.Contains("InventoryUiLaw.ItemTooltipSeat(", StringComparison.Ordinal) &&
      characterPageSource.Contains("nextWindowPivot: tooltipSeat.Pivot", StringComparison.Ordinal),
    "equipped-item hover must use the same positioned skinned tooltip seat as bag items");
string portraitSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Portraits.cs"));
string rendererSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "World", "Units", "CharacterRenderer.cs"));
Check(portraitSource.Contains("_paperDollAnimationTime += PaperDollUiLaw.LiveAnimationStep(",
          StringComparison.Ordinal) &&
      portraitSource.Contains("_character.StandPreviewTime = _paperDollAnimationTime;",
          StringComparison.Ordinal) &&
      portraitSource.Contains("_character.MountSeat = null;", StringComparison.Ordinal) &&
      rendererSource.Contains("if (StandPreviewTime is float standTime)",
          StringComparison.Ordinal),
    "CharacterFrame model must rebake a live Stand loop without mutating world animation clocks");

int rangedAttackHit = characterPageSource.IndexOf(
    "DrawCharacterStatTooltipHit(\"CharacterRangedAttackFrame\"", StringComparison.Ordinal);
int rangedPowerGate = characterPageSource.IndexOf(
    "if (ranged is { Class: 2, Subclass: not 19 })", StringComparison.Ordinal);
Check(rangedAttackHit >= 0 && rangedPowerGate > rangedAttackHit &&
      characterPageSource.Contains("offhand?.Class == 2", StringComparison.Ordinal) &&
      characterPageSource.Contains("if (ranged is not null)", StringComparison.Ordinal),
    "CharacterFrame static ranged-attack hover or weapon/wand/offhand tooltip gates drift");
string inventorySource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Inventory.cs"));
Check(inventorySource.Contains("PreparePaperDollComparisonTooltips(item);", StringComparison.Ordinal) &&
      inventorySource.Contains("PaperDollUiLaw.ShowBagItemComparison", StringComparison.Ordinal) &&
      inventorySource.Contains("compact: true", StringComparison.Ordinal) &&
      inventorySource.Contains("compact ? Vector4.One", StringComparison.Ordinal) &&
      inventorySource.Contains("Currently Equipped", StringComparison.Ordinal) &&
      inventorySource.Contains("DrawPreparedPaperDollComparisonTooltips(comparisons)",
          StringComparison.Ordinal) &&
      inventorySource.Contains("HighestLiveComparisonOrdinal(", StringComparison.Ordinal),
    "bag-hover shift compare must remain atomic, paper-doll-visible, live-equipped, and compact");

if (args.Contains("--character-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: CharacterFrame PASS");
    return;
}

// GameMenuFrame clinical contract. The geometry below preserves the build-5875 ladder and pins
// the one intentional native AddOns extension; Escape/lifecycle behavior remains unchanged.
Check(GameMenuUiLaw.FrameWidth == 195f && GameMenuUiLaw.FrameHeight == 268f &&
      GameMenuUiLaw.ButtonWidth == 144f && GameMenuUiLaw.ButtonHeight == 21f &&
      GameMenuUiLaw.HeaderWidth == 256f && GameMenuUiLaw.HeaderHeight == 64f &&
      GameMenuUiLaw.HeaderTop == -12f && GameMenuUiLaw.HeaderTitleTop == 14f &&
      GameMenuUiLaw.HighlightAlpha == .55f,
    "GameMenuFrame native-AddOns geometry/header/highlight contract drift");
Check(Enumerable.Range(0, 9).Select(GameMenuUiLaw.ButtonTop).SequenceEqual(
      new[] { 26.5f, 48.5f, 70.5f, 92.5f, 114.5f, 136.5f, 158.5f, 180.5f, 217.5f }),
    "GameMenuFrame native-AddOns nine-rung ladder drift");
CheckLightingDefaults();
CheckGameMenuLayout();
CheckOptionsSearch();
Check(QuestHelperUiLaw.QuestComplete(1u << 24) &&
      !QuestHelperUiLaw.QuestComplete(0) &&
      QuestHelperUiLaw.ObjectiveProgress(5u << 6, 1, 8) == 5 &&
      QuestHelperUiLaw.ObjectiveEntry(unchecked((uint)-142702)) == 142702 &&
      QuestHelperUiLaw.ObjectiveIsObject(unchecked((uint)-142702)),
    "native Quest Helper objective decoding drift");
var questHelperExports = new Dictionary<string, string>
{
    ["quest_template"] = "entry,patch,QuestLevel,MinLevel,Title,ReqItemId1\n7,10,10,1,Kobold Camp Cleanup,750\n",
    ["creature_questrelation"] = "id,quest,patch_min,patch_max\n197,7,0,10\n",
    ["creature_involvedrelation"] = "id,quest,patch_min,patch_max\n197,7,0,10\n",
    ["creature"] = "guid,id,map,position_x,position_y,patch_min,patch_max\n1,197,0,100,-50,0,10\n",
    ["creature_template"] = "entry,patch,loot_id\n197,10,197\n",
    ["creature_loot_template"] = "entry,item,mincountOrRef,patch_min,patch_max\n197,750,1,0,10\n",
};
QuestHelperDataCatalog questHelperData = QuestHelperDataClient.ParseExports(questHelperExports);
Check(questHelperData.UnitSpawns(197).Single() == new QuestHelperSpawn(0, 100, -50) &&
      questHelperData.ItemSources(750).Units.Contains(197u) &&
      questHelperData.TurnInSources(7).Units.Contains(197u),
    "native Quest Helper live realm-data joins failed");

GameMenuEscapeState idleEscape = new(false, false, false, false, false, false,
    false, false, false, false, false);
GameMenuEscapeLayer[] orderedEscapeLayers =
[
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        PopupOpen = true, OptionsOpen = true, GameMenuOpen = true, StackSplitOpen = true,
        WorldMapOpen = true, OpenMailOpen = true, CancelableSpellCast = true,
        SpellTargeting = true, PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        OptionsOpen = true, GameMenuOpen = true, StackSplitOpen = true, WorldMapOpen = true,
        OpenMailOpen = true, CancelableSpellCast = true, SpellTargeting = true,
        PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        GameMenuOpen = true, StackSplitOpen = true, WorldMapOpen = true, OpenMailOpen = true,
        CancelableSpellCast = true, SpellTargeting = true, PlayerPanelOpen = true,
        TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        StackSplitOpen = true, WorldMapOpen = true, OpenMailOpen = true,
        CancelableSpellCast = true, SpellTargeting = true, PlayerPanelOpen = true,
        TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        WorldMapOpen = true, OpenMailOpen = true, CancelableSpellCast = true,
        SpellTargeting = true, PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        OpenMailOpen = true, CancelableSpellCast = true, SpellTargeting = true,
        PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        CancelableSpellCast = true, SpellTargeting = true, PlayerPanelOpen = true,
        TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        SpellTargeting = true, PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with
    {
        PlayerPanelOpen = true, TargetSelected = true
    }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape with { TargetSelected = true }).Layer,
    GameMenuUiLaw.ResolveEscape(idleEscape).Layer,
];
Check(orderedEscapeLayers.SequenceEqual(new[]
    {
        GameMenuEscapeLayer.Popup, GameMenuEscapeLayer.Options,
        GameMenuEscapeLayer.GameMenu, GameMenuEscapeLayer.StackSplit,
        GameMenuEscapeLayer.WorldMap, GameMenuEscapeLayer.OpenMail,
        GameMenuEscapeLayer.SpellCast, GameMenuEscapeLayer.SpellTargeting,
        GameMenuEscapeLayer.PlayerPanel, GameMenuEscapeLayer.Target,
        GameMenuEscapeLayer.OpenGameMenu,
    }),
    $"GameMenuFrame one-eater Escape ladder drift: {string.Join(',', orderedEscapeLayers)}");
GameMenuEscapePlan carriedEscape = GameMenuUiLaw.ResolveEscape(idleEscape with
    { HasCarriedCursor = true, CancelableSpellCast = true, TargetSelected = true });
Check(carriedEscape.ClearCarriedCursor && carriedEscape.Layer == GameMenuEscapeLayer.SpellCast,
    "GameMenuFrame cursor clear must not eat Escape or skip the first consuming rung");
Check(GameMenuUiLaw.MicroToggle(false) == GameMenuToggleAction.Open &&
      GameMenuUiLaw.MicroToggle(true) == GameMenuToggleAction.Close &&
      GameMenuUiLaw.PlayerPanelMayOpen(false) && !GameMenuUiLaw.PlayerPanelMayOpen(true),
    "GameMenuFrame micro-toggle or center-panel ownership drift");
Check(GameMenuUiLaw.OpenSound == "igMainMenuOpen" &&
      GameMenuUiLaw.EscapeCloseSound == "igMainMenuQuit" &&
      GameMenuUiLaw.PopupVisibilitySound(false, true) == "igMainMenuOpen" &&
      GameMenuUiLaw.PopupVisibilitySound(true, false) == "igMainMenuClose" &&
      GameMenuUiLaw.PopupVisibilitySound(false, false).Length == 0 &&
      GameMenuUiLaw.PopupVisibilitySound(true, true).Length == 0,
    "GameMenuFrame menu/popup cue identity or one-cue-per-edge cardinality drift");

Check((ushort)Op.CMSG_LOGOUT_REQUEST == 0x004B &&
      (ushort)Op.SMSG_LOGOUT_RESPONSE == 0x004C &&
      (ushort)Op.SMSG_LOGOUT_COMPLETE == 0x004D &&
      (ushort)Op.CMSG_LOGOUT_CANCEL == 0x004E &&
      (ushort)Op.SMSG_LOGOUT_CANCEL_ACK == 0x004F,
    "GameMenuFrame build-5875 logout opcode identities drift");
Check(LogoutResponse.Parse(Convert.FromHexString("0000000000")) == new LogoutResponse(0, false) &&
      LogoutResponse.Parse(Convert.FromHexString("0300000001")) == new LogoutResponse(3, true) &&
      LogoutUiLaw.Decide(new LogoutResponse(1, false), quitting: true) ==
          LogoutResponseAction.Refused &&
      LogoutUiLaw.Decide(new LogoutResponse(0, true), quitting: false) ==
          LogoutResponseAction.AwaitCompletion &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: false) ==
          LogoutResponseAction.ShowCampCountdown &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: true) ==
          LogoutResponseAction.ShowQuitCountdown &&
      LogoutUiLaw.CountdownText(false, 20f) == "20 seconds until logout" &&
      LogoutUiLaw.CountdownText(true, .1f) == "1 second until exit" &&
      LogoutUiLaw.Frame == new LogoutUiLaw.LogicalRect(0, 128, 360, 96) &&
      LogoutUiLaw.FrameSize(2f) == new Vector2(720, 192) &&
      LogoutUiLaw.FrameOrigin(new Vector2(1920, 1080), 2f) == new Vector2(600, 256) &&
      LogoutUiLaw.CountdownTextCenter(new Vector2(600, 256), 2f) ==
          new Vector2(960, 324) &&
      LogoutUiLaw.PrimaryButton(false) == new LogoutUiLaw.LogicalRect(116, 66, 128, 20) &&
      LogoutUiLaw.PrimaryButton(true) == new LogoutUiLaw.LogicalRect(42, 66, 128, 20) &&
      LogoutUiLaw.QuitCancel == new LogoutUiLaw.LogicalRect(190, 66, 128, 20) &&
      LogoutUiLaw.ButtonUvMax == new Vector2(1f, .625f),
    "GameMenuFrame logout response/countdown law drift");

string gameMenuSettingsSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Settings.cs"));
string gameMenuActionSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.ActionBars.cs"));
string gameMenuLogoutSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Logout.cs"));
string gameMenuSkinSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Engine", "UI", "WowSkin.cs"));
string gameMenuCaptureSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
string gameMenuLiveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
Check(gameMenuSettingsSource.Contains("InputKeyDown(Silk.NET.Input.Key.Escape)", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("ClearCarriedItemOnEscape();", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("CloseOpenMail(playSound: true, autoDelete: true);", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("TryCancelSpellTargetingOnEscape();", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("TryClearTargetOnEscape();", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("PlayUiSound(GameMenuUiLaw.EscapeCloseSound);", StringComparison.Ordinal),
    "GameMenuFrame production Escape ladder is not wired to the deterministic law");
Check(gameMenuActionSource.Contains("_mainMenuMicroPressedThroughModal", StringComparison.Ordinal) &&
      gameMenuActionSource.Contains("ToggleSettingsFromMicroButton();", StringComparison.Ordinal) &&
      gameMenuActionSource.Contains("GameMenuUiLaw.PlayerPanelMayOpen(_settingsOpen)", StringComparison.Ordinal),
    "GameMenuFrame micro toggle/center-panel click gate wiring drift");
Check(gameMenuLogoutSource.Contains("GameMenuUiLaw.PopupVisibilitySound", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.FrameSize", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.FrameOrigin", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.CountdownTextCenter", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.PrimaryButton", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.QuitCancel", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("LogoutUiLaw.ButtonUvMax", StringComparison.Ordinal) &&
      !gameMenuLogoutSource.Contains("new Vector2", StringComparison.Ordinal) &&
      gameMenuLogoutSource.Contains("SetLogoutDialog(LogoutDialogKind.None)", StringComparison.Ordinal),
    "GameMenuFrame preserved logout popup geometry or sound teardown wiring drift");
Check(gameMenuSkinSource.Contains("out PanelButtonDrawState drawState", StringComparison.Ordinal) &&
      gameMenuSkinSource.Contains("\"DisabledTexture\"", StringComparison.Ordinal) &&
      gameMenuSkinSource.Contains("\"PushedTexture\"", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("\"HIT_TARGET\"", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("InteractionState:drawn.InteractionState", StringComparison.Ordinal) &&
      gameMenuSettingsSource.Contains("BlendMode:\"BLEND\"", StringComparison.Ordinal),
    "GameMenuFrame actual normal/pushed/disabled/highlight capture instrumentation drift");
Check(gameMenuCaptureSource.Contains("stateSource", StringComparison.Ordinal) &&
      gameMenuCaptureSource.Contains("menu-runtime", StringComparison.Ordinal) &&
      gameMenuCaptureSource.Contains("ui-parity-stage", StringComparison.Ordinal) &&
      gameMenuLiveRunSource.Contains("case \"game-menu\":", StringComparison.Ordinal) &&
      gameMenuLiveRunSource.Contains("EmitInterface(\"ui-sound\"", StringComparison.Ordinal),
    "GameMenuFrame observational capture or strict live assertion surface drift");

int gameMenuGoStart = gameMenuSettingsSource.IndexOf(
    "private void Go(MenuPage page)", StringComparison.Ordinal);
int gameMenuGoEnd = gameMenuSettingsSource.IndexOf(
    "private void DrawOptionsSearch", gameMenuGoStart, StringComparison.Ordinal);
Check(gameMenuGoStart >= 0 && gameMenuGoEnd > gameMenuGoStart &&
      gameMenuSettingsSource[gameMenuGoStart..gameMenuGoEnd]
          .Contains("_menuLayoutReflowRequested = true;", StringComparison.Ordinal) &&
      !gameMenuSettingsSource[gameMenuGoStart..gameMenuGoEnd]
          .Contains("CloseCurrentPopup", StringComparison.Ordinal),
    "GameMenu internal pages must resize in place, never close/reopen the modal");

string[] preservedGameMenuLabels =
[
    "Video Options", "Sound Options", "Interface Options", "Key Bindings",
    "Macros", "Logout", "Exit Game", "Return to Game"
];
int priorGameMenuLabel = -1;
foreach (string label in preservedGameMenuLabels)
{
    int at = gameMenuSettingsSource.IndexOf($"\"{label}\"", priorGameMenuLabel + 1,
        StringComparison.Ordinal);
    Check(at > priorGameMenuLabel, $"GameMenuFrame preserved label/order missing: {label}");
    priorGameMenuLabel = at;
}

if (args.Contains("--game-menu-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: GameMenuFrame PASS");
    return;
}

var attackSpell = new SpellInfo(6603, "Attack", "", @"Interface\Icons\Temp.blp",
    0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0,
    EffectIds: [ActionIconLaw.SpellEffectAttack]);
Check(ActionIconLaw.Resolve(attackSpell, @"Interface\Icons\INV_Sword_04.blp", 7) ==
      @"Interface\Icons\INV_Sword_04.blp", "Attack borrows equipped main-hand icon");
Check(ActionIconLaw.Resolve(attackSpell, null, null) == ActionIconLaw.UnarmedAttackIcon,
    "unarmed Attack uses Spell-Reset rather than Temp");
var autoShot = attackSpell with { Id = 75, Name = "Auto Shot", IconPath = @"Interface\Icons\Ability_Whirlwind.blp",
    Attributes = 0x2, AttributesEx2 = 0x20, EffectIds = [0u] };
Check(ActionIconLaw.Resolve(autoShot, @"Interface\Icons\INV_Weapon_Rifle_01.blp", 3) ==
      @"Interface\Icons\INV_Weapon_Rifle_01.blp", "Auto Shot borrows equipped ranged icon");
Check(ActionIconLaw.Resolve(autoShot, @"Interface\Icons\INV_ThrowingKnife_01.blp", 16) ==
      autoShot.IconPath, "thrown weapon keeps ranged spell icon");

var northshire = MinimapProjection.FromWorld(new System.Numerics.Vector3(-8949.95f, -132.493f, 83.5312f));
Check(northshire.TileColumn == 32 && northshire.TileRow == 48 &&
      northshire.ChunkX == 3 && northshire.ChunkY == 12,
      "Northshire world position projects to Azeroth minimap/MCNK coordinates");
Check(WmoMinimapProjection.Stem(
          @"World\wmo\KhazModan\Cities\Ironforge\Ironforge.wmo") ==
      @"wmo\khazmodan\cities\ironforge\ironforge",
      "WMO minimap stem strips World prefix/extension and normalizes case");
Check(WmoMinimapProjection.LogicalTile(
          @"wmo\khazmodan\cities\ironforge\ironforge", 66, 1, 1) ==
      @"wmo\khazmodan\cities\ironforge\ironforge_066_01_01.blp",
      "WMO minimap logical tile key");
Check(WmoMinimapProjection.AxisGrid(20.6f) == (1, 32f) &&
      WmoMinimapProjection.AxisGrid(201f) == (2, 128f) &&
      WmoMinimapProjection.AxisGrid(185.6f) == (2, 128f),
      "WMO minimap group grid matches Ironforge 0.5yd/texel authoring");
Check((ushort)Op.CMSG_ZONEUPDATE == 500, "CMSG_ZONEUPDATE opcode");
Check(WorldSession.BuildZoneUpdateBody(12).SequenceEqual(Convert.FromHexString("0C000000")),
      "zone update body");
string clientData = ClientDataRoot.Path;
using var spellbookMpq = new MpqMount(clientData);
Check(spellbookMpq.ReadFile(@"Interface\Buttons\UI-Debuff-Overlays.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\Icons\INV_Misc_QuestionMark.blp") is not null &&
      spellbookMpq.ReadFile(@"Fonts\FRIZQT__.TTF") is not null,
    "BuffFrame debuff overlay/fallback icon/duration font asset closure missing");
SpellVisualCatalog hardcodedVisuals = SpellVisualCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("SpellVisual DBCs unavailable");
bool lootFxResolved = hardcodedVisuals.TryGetHardcodedEffect(
    SpellVisualCatalog.HardcodedLootArt, out string lootFxPath);
bool levelFxResolved = hardcodedVisuals.TryGetHardcodedEffect(
    SpellVisualCatalog.HardcodedUnitLevelUp, out string levelFxPath);
Check(lootFxResolved && lootFxPath == @"Particles\LootFX.m2" &&
      levelFxResolved && levelFxPath == @"Spells\LevelUp\LevelUp.m2",
    "hardcoded loot/level-up SpellVisualEffectName resolution drift");
var hardcodedFx = new SpellEffectSource(spellbookMpq);
var levelSounds = new List<uint>();
hardcodedFx.AnimationSoundEvent = (sound, _, _) => levelSounds.Add(sound);
var levelFxKit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x13, levelFxPath)], []);
Check(hardcodedFx.SpawnKit(1, 0, levelFxKit, StageLife.SelfTerminating,
          0, "HARDCODED_LEVEL_UP") == 1,
    "hardcoded unit level-up model did not spawn");
hardcodedFx.Tick(0.05, _ => new SpellUnitPose(true, Vector3.Zero, 0,
    Matrix4x4.Identity, null, null));
Check(levelSounds.Contains(888),
    "LevelUp.m2 did not deliver its authored $SND(888) event");
var lootFxKit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x13, lootFxPath)], []);
var lootHardcodedFx = new SpellEffectSource(spellbookMpq);
Check(lootHardcodedFx.SpawnKit(2, uint.MaxValue, lootFxKit, StageLife.AuraState,
          0, "HARDCODED_LOOT") == 1 &&
      lootHardcodedFx.EmitterInstances(0, _ => new SpellUnitPose(true, Vector3.Zero, 0,
          Matrix4x4.Identity, null, null)).Count() == 4,
    "LootFX.m2 did not publish its four corpse-sparkle emitters");
string hardcodedCastingSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "GameLoop", "Combat", "GameLoop.Casting.cs"));
string hardcodedNetSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "GameLoop", "Scene", "GameLoop.Net.cs"));
Check(hardcodedCastingSource.Contains("unit.Fields.ReadsDead && unit.Fields.Lootable",
          StringComparison.Ordinal) &&
      hardcodedCastingSource.Contains("StageLife.AuraState", StringComparison.Ordinal) &&
      hardcodedNetSource.Contains("levelBefore is uint oldLevel", StringComparison.Ordinal) &&
      hardcodedNetSource.Contains("PlayHardcodedUnitLevelUp", StringComparison.Ordinal),
    "hardcoded loot persistence or field-level level-up edge wiring drift");
Check(spellbookMpq.ReadFile(@"Interface\DialogFrame\UI-DialogBox-Background.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\DialogFrame\UI-DialogBox-Border.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\DialogFrame\DialogAlertIcon.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\Buttons\UI-DialogBox-Button-Up.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\Buttons\UI-DialogBox-Button-Down.blp") is not null &&
      spellbookMpq.ReadFile(@"Interface\Buttons\UI-DialogBox-Button-Highlight.blp") is not null,
    "EnchantConfirm StaticPopup backdrop/alert/button asset closure missing");
SpellCatalog spellbookSpells = SpellCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("Spell DBC unavailable");
EnchantCatalog enchantRows = EnchantCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("SpellItemEnchantment DBC unavailable");
Check(enchantRows.Name(2564) == "Agility +15" && enchantRows.Name(1900) == "Crusader",
    "SpellItemEnchantment name-column/locale drift");
Check(!Enum.TryParse("CMSG_REPLACE_ENCHANT", out Op _),
    "build 5875 must not invent a CMSG_REPLACE_ENCHANT opcode");
StaticPopupCoordinatorLaw.Plan enchantShown = StaticPopupCoordinatorLaw.Show(
    StaticPopupCoordinatorLaw.Slots.Empty, EnchantConfirmUiLaw.BindDefinition,
    playerDeadOrGhost: false);
EnchantConfirmUiLaw.PopupLayout enchantLayout = EnchantConfirmUiLaw.Layout(12);
Check(enchantShown.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
      enchantShown.Effects.Any(effect => effect.Kind ==
          StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound) &&
      EnchantConfirmUiLaw.BindDefinition.ShowAlert &&
      EnchantConfirmUiLaw.BindDefinition.HideOnEscape &&
      EnchantConfirmUiLaw.ReplaceDefinition.ShowAlert &&
      EnchantConfirmUiLaw.Visible(enchantShown.Slots) is { Slot: 1 },
    "enchant shared StaticPopup definition/lifecycle drift");
Check(EnchantConfirmUiLaw.FrameWidth == 420f && EnchantConfirmUiLaw.FrameHeight == 72f &&
      EnchantConfirmUiLaw.FrameTop == 128f &&
      enchantLayout.Width == 420 && enchantLayout.Height == 72 &&
      enchantLayout.Text == new EnchantConfirmUiLaw.LogicalRect(65, 16, 290, 12) &&
      enchantLayout.Alert == new EnchantConfirmUiLaw.LogicalRect(12, 4, 64, 64) &&
      enchantLayout.AcceptButton ==
          new EnchantConfirmUiLaw.LogicalRect(76, 36, 128, 20) &&
      enchantLayout.DeclineButton ==
          new EnchantConfirmUiLaw.LogicalRect(217, 36, 128, 20) &&
      EnchantConfirmUiLaw.BindMessage == "Enchanting this item will bind it to you." &&
      string.Format(System.Globalization.CultureInfo.InvariantCulture,
          EnchantConfirmUiLaw.ReplaceMessageFormat, "Agility +15", "Crusader") ==
          "Do you want to replace \"Agility +15\" with \"Crusader\"?",
    "EnchantConfirm Benilla alert layout or exact bind/replace copy drift");
SkillLineCatalog spellbookSkills = SkillLineCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("Skill-line DBCs unavailable");
SpellInfo BookSpell(uint id) => spellbookSpells.TryGet(id, out SpellInfo value) ? value
    : throw new InvalidDataException($"spell {id} missing");
SpellInfo bracerEnchant = BookSpell(7418);
Check(bracerEnchant.EquippedItemClass == 4 &&
      bracerEnchant.EquippedItemInventoryTypeMask == (1u << 9),
    "Spell.dbc equipped-item enchant gates drift");
var itemTargetSpell = bracerEnchant with
{
    Targets = 0x0010,
    ImplicitTarget = 0,
};
Check(CastTargetLaw.Resolve(itemTargetSpell, null, null).Kind == CastTargetKind.Item,
    "item-only target word no longer arms the item cursor");
CheckRtsAbilityTargeting(bracerEnchant);
ulong enchantItemGuid = 0xF470000000123456ul;
var itemCastReader = new PacketReader(WorldSession.BuildCastSpellOnItemBody(
    bracerEnchant.Id, enchantItemGuid));
Check(itemCastReader.ReadU32() == bracerEnchant.Id && itemCastReader.ReadU16() == 0x0010 &&
      itemCastReader.ReadPackedGuid() == enchantItemGuid && itemCastReader.Remaining == 0,
    "CMSG_CAST_SPELL item-target body drift");

SpellInfo bindingEnchant = spellbookSpells.Spells.First(spell =>
{
    uint[] effects = spell.EffectIds ?? [];
    int[] misc = spell.EffectMiscValues ?? [];
    for (int i = 0; i < Math.Min(effects.Length, misc.Length); i++)
        if (effects[i] is 53 or 54 && misc[i] > 0 && enchantRows.BindsItem((uint)misc[i]))
            return true;
    return false;
});
uint[] bindingEffects = bindingEnchant.EffectIds ??
    throw new InvalidDataException("binding enchant effect lanes missing");
int bindingLane = Array.FindIndex(bindingEffects, effect => effect is 53 or 54);
uint bindingEffect = bindingEffects[bindingLane];
uint bindingNewId = (uint)bindingEnchant.EffectMiscValues![bindingLane];
uint matchingSubclass = bindingEnchant.EquippedItemSubclassMask == 0 ? 0u :
    (uint)System.Numerics.BitOperations.TrailingZeroCount(bindingEnchant.EquippedItemSubclassMask);
uint matchingInventoryType = bindingEnchant.EquippedItemInventoryTypeMask == 0 ? 13u :
    (uint)System.Numerics.BitOperations.TrailingZeroCount(bindingEnchant.EquippedItemInventoryTypeMask);
var bareEnchantTarget = new EnchantClickedItem(
    bindingEnchant.EquippedItemSubclassMask == 0 ? 2u : unchecked((uint)bindingEnchant.EquippedItemClass),
    matchingSubclass, matchingInventoryType, AlreadyBound: false);
Check(EnchantConfirmUiLaw.Decide(bindingEnchant, bareEnchantTarget, enchantRows, false).Kind ==
      EnchantBindKind.ConfirmBind,
    "bind-warning leg or SpellItemEnchantment Flags bit drift");
var alreadyEnchantedTarget = bindingEffect == 53
    ? bareEnchantTarget with { PermanentEnchant = bindingNewId }
    : bareEnchantTarget with { TemporaryEnchant = bindingNewId };
EnchantBindVerdict chained = EnchantConfirmUiLaw.Decide(
    bindingEnchant, alreadyEnchantedTarget, enchantRows, bindConfirmed: true);
Check(chained.Kind == EnchantBindKind.ConfirmReplace &&
      chained.ExistingEnchant == bindingNewId && chained.NewEnchant == bindingNewId,
    "bind-accept did not chain into the replacement confirmation");
Check(EnchantConfirmUiLaw.Decide(bindingEnchant,
        bareEnchantTarget with { AlreadyBound = true }, enchantRows, false).Kind ==
      EnchantBindKind.Bind,
    "already-bound item incorrectly raised the bind warning");
const byte Human = 1, Warrior = 1, Mage = 8;
Check(spellbookSkills.SpellTab(6603, Human, Mage) == 0, "Attack did not collapse into General");
Check(spellbookSkills.SpellTab(133, Human, Mage) == 8, "Fireball did not route to Fire");
Check(spellbookSkills.SpellTab(116, Human, Mage) == 6, "Frostbolt did not route to Frost");
Check(spellbookSkills.SpellTab(1459, Human, Mage) == 237, "Arcane Intellect did not route to Arcane");
Check(spellbookSkills.SpellTab(133, Human, Warrior) == 0,
    "cross-class Fireball did not collapse into General");
Check(SpellbookLaw.Eligible(BookSpell(133)), "Fireball failed the spellbook add-gate");
Check(!SpellbookLaw.Eligible(BookSpell(668)), "Common language survived the spellbook add-gate");
Check(!BookSpell(1953).MovementInterrupts,
    "Blink must remain movement-castable (Spell InterruptFlags movement bit is 0x01)");
Check(BookSpell(133).MovementInterrupts,
    "Fireball must retain ordinary movement interruption");
var iceBlockCamera = new MSUIClient.Engine.Camera { Yaw = 1.25f, OrbitYaw = 0.35f };
float iceBlockView = iceBlockCamera.ViewYaw;
iceBlockCamera.Rotate(0.8f, 0f);
iceBlockCamera.SetFacingKeepingView(1.25f);
Check(MathF.Abs(iceBlockCamera.Yaw - 1.25f) < 0.0001f &&
      MathF.Abs(iceBlockCamera.ViewYaw - iceBlockView - 0.8f) < 0.0001f,
    "Ice Block release must restore facing without moving the visible camera");
Check(SpellbookLaw.LeadingRankNumber("Rank 10") == 10 &&
      SpellbookLaw.LeadingRankNumber("Apprentice (75)") == 75, "numeric rank parser drift");
Check(SpellbookLaw.NameFontHeight == 12f && SpellbookLaw.RankFontHeight == 10f &&
      SpellbookLaw.ButtonSize == 37f && SpellbookLaw.NameWidth == 103f &&
      SpellbookLaw.NameMaxLines == 3 && SpellbookLaw.NameAnchorX == 4f &&
      SpellbookLaw.NameAnchorYWithRank == 4f && SpellbookLaw.NameAnchorYWithoutRank == 2f &&
      SpellbookLaw.RankWidth == 79f && SpellbookLaw.RankBoxHeight == 18f &&
      SpellbookLaw.RankAnchorY == 4f && SpellbookLaw.PassiveNameColor == 0xff00a3c4 &&
      SpellbookLaw.RankColor == 0xff003359,
    "SpellButtonTemplate/GameFontNormal/SubSpellFont geometry or color drift");
Check(SpellTooltipLaw.HeaderFontHeight == 14f && SpellTooltipLaw.TextFontHeight == 12f &&
      SpellTooltipLaw.Pad == 10f && SpellTooltipLaw.LineGap == 2f &&
      SpellTooltipLaw.DoubleGap == 40f && SpellTooltipLaw.WrapWidth == 260f,
    "build-5875 GameTooltip line-stack constants drift");
Check(spellbookMpq.ReadFile(SpellbookLaw.GeneralIcon + ".blp") is not null,
    $"General tab icon absent: {SpellbookLaw.GeneralIcon}.blp");
Check(spellbookMpq.ReadFile(@"Interface\Cooldown\star4.blp") is not null,
    "authored cooldown finish-flash texture absent");

static float CooldownQuadArea(CooldownVisualLaw.Quad q)
{
    static float Triangle(Vector2 a, Vector2 b, Vector2 c) =>
        MathF.Abs((b - a).X * (c - a).Y - (b - a).Y * (c - a).X) * 0.5f;
    return Triangle(q.A, q.B, q.C) + Triangle(q.A, q.C, q.D);
}
Vector2 cooldownMin = new(10f, 20f), cooldownMax = new(46f, 56f);
float previousCooldownArea = float.PositiveInfinity;
foreach (float fraction in Enumerable.Range(0, 101).Select(i => i / 100f))
{
    IReadOnlyList<CooldownVisualLaw.Quad> quads =
        CooldownVisualLaw.BuildWipe(cooldownMin, cooldownMax, fraction);
    Check(quads.Count <= 4, "cooldown wipe exceeded its four authored quadrants");
    foreach (Vector2 p in quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }))
        Check(p.X >= cooldownMin.X - 0.001f && p.X <= cooldownMax.X + 0.001f &&
              p.Y >= cooldownMin.Y - 0.001f && p.Y <= cooldownMax.Y + 0.001f,
            $"cooldown wipe escaped its icon at fraction {fraction}: {p}");
    float area = quads.Sum(CooldownQuadArea);
    Check(area <= previousCooldownArea + 0.1f,
        $"cooldown wipe reversed at fraction {fraction}");
    previousCooldownArea = area;
}
foreach ((float fraction, float covered) in new[]
         { (0f, 1f), (0.25f, 0.75f), (0.5f, 0.5f), (0.75f, 0.25f), (1f, 0f) })
    Check(MathF.Abs(CooldownVisualLaw.BuildWipe(cooldownMin, cooldownMax, fraction)
                        .Sum(CooldownQuadArea) - 36f * 36f * covered) < 0.1f,
        $"cooldown quadrant coverage drift at fraction {fraction}");
Check(MathF.Abs(CooldownVisualLaw.FlashScale(0.333f) - 1.853f) < 0.001f &&
      MathF.Abs(CooldownVisualLaw.FlashAlpha(0.4f) - 1f) < 0.001f &&
      CooldownVisualLaw.FlashAlpha(1f) == 0f,
    "cooldown finish-flash authored scale/alpha curve drift");
var cooldownPhases = new PlayerActions();
cooldownPhases.StartCooldown(133, 0, 1_000, 10.0);
Check(cooldownPhases.TryCooldownDisplay(133, 10.25, 0, out CooldownDisplay sweepPhase) &&
      MathF.Abs(sweepPhase.SweepFraction!.Value - 0.25f) < 0.001f &&
      sweepPhase.FlashProgress is null,
    "running cooldown did not expose its authored sweep phase");
Check(!cooldownPhases.IsOnCooldown(133, 11.4) &&
      cooldownPhases.TryCooldownDisplay(133, 11.4, 0, out CooldownDisplay flashPhase) &&
      flashPhase.SweepFraction is null &&
      MathF.Abs(flashPhase.FlashProgress!.Value - 0.4f) < 0.001f,
    "finished cooldown did not become ready while retaining its one-second flash");
Check(!cooldownPhases.TryCooldownDisplay(133, 12.01, 0, out _),
    "finished cooldown display survived beyond its one-second flash");
foreach (uint lineId in new uint[] { 6, 8, 237 })
{
    Check(spellbookSkills.TryGet(lineId, out SkillLineInfo line), $"mage line {lineId} missing");
    string iconPath = line.IconPath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)
        ? line.IconPath : line.IconPath + ".blp";
    Check(!iconPath.Contains(@"Interface\Icons\Interface\Icons", StringComparison.OrdinalIgnoreCase),
        $"mage line {lineId} duplicated its icon prefix: {iconPath}");
    Check(spellbookMpq.ReadFile(iconPath) is not null, $"mage line {lineId} icon absent: {iconPath}");
}
SpellTooltipView arcaneExplosion = SpellTooltipLaw.Build(BookSpell(1449), spellbookSpells, 60);
const string ArcaneExplosionText = "Causes an explosion of arcane magic around the caster, causing 34 to 38 Arcane damage to all targets within 10 yards.";
Check(arcaneExplosion.Description == ArcaneExplosionText,
    $"Arcane Explosion tooltip drift: {arcaneExplosion.Description}; " +
    $"levels={BookSpell(1449).SpellLevel}/{BookSpell(1449).MaxLevel}/{BookSpell(1449).BaseLevel}; " +
    $"real={string.Join(',', BookSpell(1449).EffectRealPointsPerLevel ?? [])}; " +
    $"dice={string.Join(',', BookSpell(1449).EffectDicePerLevel ?? [])}");
Check(arcaneExplosion.Cost == "75 Mana", $"Arcane Explosion cost drift: {arcaneExplosion.Cost}");
Check(arcaneExplosion.CastTime == "Instant cast",
    $"Arcane Explosion cast line drift: {arcaneExplosion.CastTime}");
Check(!arcaneExplosion.Description.Contains('$'), "Arcane Explosion retained a raw tooltip token");
SpellTooltipView fireballTooltip = SpellTooltipLaw.Build(BookSpell(133), spellbookSpells);
Check(fireballTooltip.Description.Contains("14 to 22", StringComparison.Ordinal),
    $"Fireball effect bounds drift: {fireballTooltip.Description}");
Check(fireballTooltip.Range?.Contains("yd range", StringComparison.Ordinal) == true,
    "Fireball range line missing");
string[] unresolvedSpellbookTokens = spellbookSpells.Spells.Where(spell => SpellbookLaw.Eligible(spell))
    .Select(spell => (spell, resolved: SpellTooltipLaw.Substitute(spell.Description, spell, spellbookSpells)))
    .Where(pair =>
    {
        string resolved = pair.resolved;
        return resolved.Contains("$s", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$a", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$d", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$t", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$o", StringComparison.OrdinalIgnoreCase);
    })
    .Select(pair => $"{pair.spell.Id}:{pair.spell.Name} => {pair.resolved}")
    .ToArray();
Check(unresolvedSpellbookTokens.Length == 0,
    $"{unresolvedSpellbookTokens.Length} eligible descriptions retain supported raw tokens: " +
    string.Join(" | ", unresolvedSpellbookTokens));
var northshireAdt = AdtTerrainReader.ReadFromMpq(clientData, "Azeroth", 48, 32);
Check(northshire.AreaId(northshireAdt) == 9,
      "Northshire MCNK resolves live AreaTable ID 9 rather than login zone");

Check((ushort)Op.CMSG_GOSSIP_HELLO == 379, "CMSG_GOSSIP_HELLO opcode");
Check((ushort)Op.CMSG_GOSSIP_SELECT_OPTION == 380, "CMSG_GOSSIP_SELECT_OPTION opcode");
Check((ushort)Op.SMSG_GOSSIP_MESSAGE == 381, "SMSG_GOSSIP_MESSAGE opcode");
Check((ushort)Op.SMSG_GOSSIP_COMPLETE == 382, "SMSG_GOSSIP_COMPLETE opcode");
Check((ushort)Op.SMSG_GOSSIP_POI == 0x0224, "SMSG_GOSSIP_POI opcode");
Check((ushort)Op.CMSG_NPC_TEXT_QUERY == 383, "CMSG_NPC_TEXT_QUERY opcode");
Check((ushort)Op.SMSG_NPC_TEXT_UPDATE == 384, "SMSG_NPC_TEXT_UPDATE opcode");
Check((ushort)Op.CMSG_LIST_INVENTORY == 414 && (ushort)Op.SMSG_LIST_INVENTORY == 415,
    "vendor list opcodes");
Check((ushort)Op.CMSG_SELL_ITEM == 416 && (ushort)Op.CMSG_BUY_ITEM == 418 &&
      (ushort)Op.CMSG_BUYBACK_ITEM == 656, "vendor transaction opcodes");

byte[] menuBytes = Convert.FromHexString(
    "463701FB040030F17B0000000100000000000000030042726F77736500" +
    "010000002A000000020000000A0000004120517565737400");
GossipMenu menu = GossipPackets.ParseMenu(menuBytes);
Check(menu.SourceGuid == 0xF1300004FB013746ul, "gossip menu full GUID");
Check(menu.TextId == 123 && menu.Options.Count == 1 && menu.Quests.Count == 1,
    "gossip menu header/counts");
Check(menu.Options[0] == new GossipOption(0, 3, false, "Browse"), "gossip option shape");
Check(menu.Quests[0] == new GossipQuest(42, 2, 10, "A Quest"), "gossip quest shape");

var textWriter = new PacketWriter();
textWriter.WriteU32(123);
for (int i = 0; i < 8; i++)
{
    textWriter.WriteF32(i == 0 ? 1f : 0f);
    textWriter.WriteCString(i == 0 ? "Hello $N" : "");
    textWriter.WriteCString(i == 0 ? "Hello $N" : "");
    for (int field = 0; field < 7; field++) textWriter.WriteU32(0);
}
NpcText text = GossipPackets.ParseText(textWriter.ToArray());
Check(text.TextId == 123 && text.Blocks.Count == 8 &&
      text.Blocks[0].MaleText == "Hello $N" && text.Blocks[0].FemaleText == "Hello $N",
    "npc text first variant");

try
{
    GossipPackets.ParseMenu(Convert.FromHexString("00000000000000000000000010000000"));
    throw new InvalidDataException("gossip option bound did not reject 16 rows");
}
catch (InvalidDataException ex) when (ex.Message.Contains("exceeds 15")) { }

var vendorWriter=new PacketWriter();
vendorWriter.WriteU64(0xF1300004FB013746ul); vendorWriter.WriteU8(1);
foreach(uint value in new uint[]{1,117,1234,uint.MaxValue,25,0,5}) vendorWriter.WriteU32(value);
VendorInventory vendor=VendorPackets.ParseList(vendorWriter.ToArray());
Check(vendor.VendorGuid==0xF1300004FB013746ul&&vendor.Items.Count==1&&
      vendor.Items[0].ItemId==117&&vendor.Items[0].Price==25&&vendor.Items[0].BuyCount==5,
      "vendor list row shape");

Check((ushort)Op.CMSG_TRAINER_LIST == 432 && (ushort)Op.SMSG_TRAINER_LIST == 433 &&
      (ushort)Op.CMSG_TRAINER_BUY_SPELL == 434 && (ushort)Op.SMSG_TRAINER_BUY_SUCCEEDED == 435 &&
      (ushort)Op.SMSG_TRAINER_BUY_FAILED == 436, "trainer opcodes");
ulong trainerGuid = 0xF13000038F000001ul;
Check(WorldSession.BuildTrainerListBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "trainer list request full guid");
Check(WorldSession.BuildTrainerBuyBody(trainerGuid, 6673)
          .SequenceEqual(Convert.FromHexString("0100008F030030F1111A0000")),
      "trainer buy request full guid plus service spell");
var trainerWriter = new PacketWriter();
trainerWriter.WriteU64(trainerGuid); trainerWriter.WriteU32(0); trainerWriter.WriteU32(1);
trainerWriter.WriteU32(6673); trainerWriter.WriteU8(0); trainerWriter.WriteU32(100);
trainerWriter.WriteU32(0); trainerWriter.WriteU32(0); trainerWriter.WriteU8(1);
for (int i = 0; i < 5; i++) trainerWriter.WriteU32(0);
trainerWriter.WriteCString("Train");
TrainerList trainer = TrainerPackets.ParseList(trainerWriter.ToArray());
Check(trainer.TrainerGuid == trainerGuid && trainer.Spells.Count == 1 &&
      trainer.Spells[0].ServiceSpellId == 6673 && trainer.Spells[0].Cost == 100 &&
      trainer.Spells[0].RequiredLevel == 1 && trainer.Greeting == "Train", "trainer list 38-byte row shape");

Check((ushort)Op.CMSG_QUEST_QUERY == 92 && (ushort)Op.SMSG_QUEST_QUERY_RESPONSE == 93 &&
      (ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY == 386 && (ushort)Op.SMSG_QUESTGIVER_STATUS == 387 &&
      (ushort)Op.CMSG_QUESTGIVER_HELLO == 388 && (ushort)Op.SMSG_QUESTGIVER_QUEST_LIST == 389 &&
      (ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST == 390 && (ushort)Op.SMSG_QUESTGIVER_QUEST_DETAILS == 392 &&
      (ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST == 393 && (ushort)Op.SMSG_QUESTGIVER_QUEST_COMPLETE == 401 &&
      (ushort)Op.SMSG_QUESTUPDATE_ADD_KILL == 409, "quest opcodes");
Check(WorldSession.BuildQuestQueryBody(0x12345678).SequenceEqual(Convert.FromHexString("78563412")),
      "quest query id body");
Check(WorldSession.BuildQuestGuidBody(trainerGuid, 7)
      .SequenceEqual(Convert.FromHexString("0100008F030030F107000000")), "quest guid plus id body");
var questDetailsWriter = new PacketWriter(); questDetailsWriter.WriteU64(trainerGuid); questDetailsWriter.WriteU32(7);
questDetailsWriter.WriteCString("A Quest"); questDetailsWriter.WriteCString("Details"); questDetailsWriter.WriteCString("Objectives");
questDetailsWriter.WriteU32(0); questDetailsWriter.WriteU32(1); questDetailsWriter.WriteU32(117);
questDetailsWriter.WriteU32(5); questDetailsWriter.WriteU32(1); questDetailsWriter.WriteU32(0);
questDetailsWriter.WriteI32(50); questDetailsWriter.WriteU32(0); questDetailsWriter.WriteU32(0);
QuestDetails questDetails = QuestPackets.ParseDetails(questDetailsWriter.ToArray());
Check(questDetails.QuestId == 7 && questDetails.Title == "A Quest" && questDetails.ChoiceRewards.Count == 1 &&
      questDetails.ChoiceRewards[0].ItemId == 117 && questDetails.Money == 50, "quest detail variable rows");
var killWriter = new PacketWriter(); killWriter.WriteU32(7); killWriter.WriteU32(6); killWriter.WriteU32(4);
killWriter.WriteU32(10); killWriter.WriteU64(trainerGuid);
Check(QuestPackets.ParseKill(killWriter.ToArray()) == new QuestKillUpdate(7, 6, 4, 10, trainerGuid),
      "quest kill objective shape");

Check((ushort)Op.CMSG_AUTOSTORE_LOOT_ITEM == 264 && (ushort)Op.CMSG_LOOT == 349 &&
      (ushort)Op.CMSG_LOOT_MONEY == 350 && (ushort)Op.CMSG_LOOT_RELEASE == 351 &&
      (ushort)Op.SMSG_LOOT_RESPONSE == 352 && (ushort)Op.SMSG_LOOT_RELEASE_RESPONSE == 353 &&
      (ushort)Op.SMSG_LOOT_REMOVED == 354 && (ushort)Op.SMSG_LOOT_CLEAR_MONEY == 357 &&
      (ushort)Op.SMSG_ITEM_PUSH_RESULT == 358, "loot opcodes");
ulong lootGuid = 0xF130000006000001ul;
Check(WorldSession.BuildLootGuidBody(lootGuid).SequenceEqual(Convert.FromHexString("01000006000030F1")),
      "loot/release full guid body");
Check(WorldSession.BuildAutostoreLootBody(3).SequenceEqual(new byte[] { 3 }), "loot slot body");
var lootWriter = new PacketWriter(); lootWriter.WriteU64(lootGuid); lootWriter.WriteU8(1);
lootWriter.WriteU32(37); lootWriter.WriteU8(1); lootWriter.WriteU8(3); lootWriter.WriteU32(117);
lootWriter.WriteU32(2); lootWriter.WriteU32(789); lootWriter.WriteU32(0); lootWriter.WriteU32(0); lootWriter.WriteU8(0);
var loot = LootPackets.ParseResponse(lootWriter.ToArray());
Check(loot.Guid == lootGuid && loot.LootType == 1 && loot.Gold == 37 && loot.Items.Count == 1 &&
      loot.Items[0] == new LootItem(3, 117, 2, 789, 0, 0), "loot response row shape");
var lootState = new LootState(); lootState.Open(lootGuid, 1, 37, loot.Items);
lootState.ClearMoney(); Check(!lootState.TakeAutoRelease(), "money clear retains item");
lootState.RemoveSlot(3); Check(lootState.TakeAutoRelease(), "last row arms auto release once");
Check(!lootState.TakeAutoRelease(), "auto release edge is one-shot");
var emptyLootWriter = new PacketWriter(); emptyLootWriter.WriteU64(lootGuid); emptyLootWriter.WriteU8(1);
emptyLootWriter.WriteU32(0); emptyLootWriter.WriteU8(0);
var emptyLoot = LootPackets.ParseResponse(emptyLootWriter.ToArray());
Check(emptyLoot.Gold == 0 && emptyLoot.Items.Count == 0, "empty corpse response shape");

Check(WorldSession.BuildAutoEquipBody(255, 24).SequenceEqual(Convert.FromHexString("FF18")),
      "autoequip bag/slot body");
Check((ushort)Op.CMSG_AUTOSTORE_BAG_ITEM == 0x010B &&
      WorldSession.BuildAutostoreBagItemBody(255, 24, 19)
          .SequenceEqual(Convert.FromHexString("FF1813")),
      "autostore bag item source/destination body");
Check(WorldSession.BuildSwapInventoryBody(15, 25).SequenceEqual(Convert.FromHexString("0F19")),
      "swap inventory source/destination body");
Check(WorldSession.BuildSwapItemsBody(255, 25, 19, 2).SequenceEqual(Convert.FromHexString("FF191302")),
      "swap bag destination/source body");
Check((ushort)Op.CMSG_SPLIT_ITEM == 0x010E,
      "split item opcode");
Check(WorldSession.BuildSplitItemBody(19, 2, 255, 25, 5)
      .SequenceEqual(Convert.FromHexString("1302FF1905")),
      "split item source/destination/count body");

Check((ushort)Op.CMSG_BANKER_ACTIVATE == 439 && (ushort)Op.SMSG_SHOW_BANK == 440 &&
      (ushort)Op.CMSG_BUY_BANK_SLOT == 441 && (ushort)Op.SMSG_BUY_BANK_SLOT_RESULT == 442,
      "bank opcodes");
Check(WorldSession.BuildBankGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "bank open/purchase full guid body");
Check(WorldSession.BuildBuyItemBody(trainerGuid, 5976, 1)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1581700000100")), "vendor buy body");

Check((ushort)Op.CMSG_SEND_MAIL == 568 && (ushort)Op.SMSG_SEND_MAIL_RESULT == 569 &&
      (ushort)Op.CMSG_GET_MAIL_LIST == 570 && (ushort)Op.SMSG_MAIL_LIST_RESULT == 571 &&
      (ushort)Op.CMSG_MAIL_TAKE_MONEY == 581 && (ushort)Op.CMSG_MAIL_TAKE_ITEM == 582 &&
      (ushort)Op.CMSG_MAIL_RETURN_TO_SENDER == 584 && (ushort)Op.CMSG_MAIL_DELETE == 585,
      "mail opcodes");
Check(WorldSession.BuildMailGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "mail list full guid body");
Check(WorldSession.BuildMailActionBody(trainerGuid, 0x78563412)
      .SequenceEqual(Convert.FromHexString("0100008F030030F112345678")), "mail action guid/id body");
Check(WorldSession.BuildMailCreateTextItemBody(trainerGuid, 0x78563412)
      .SequenceEqual(Convert.FromHexString("0100008F030030F11234567800000000")),
      "mail permanent-copy guid/id/template body");
Check(WorldSession.BuildItemTextQueryBody(0x11223344, 0x78563412)
      .SequenceEqual(Convert.FromHexString("443322111234567800000000")),
      "mail item-text query body");
byte[] sendMail = WorldSession.BuildSendMailBody(trainerGuid, "Test", "Subject", "Body", 9, 100, 200);
var mailReader = new PacketReader(sendMail);
Check(mailReader.ReadU64() == trainerGuid && mailReader.ReadCString() == "Test" &&
      mailReader.ReadCString() == "Subject" && mailReader.ReadCString() == "Body" &&
      mailReader.ReadU32() == 41 && mailReader.ReadU32() == 0 && mailReader.ReadU64() == 9 &&
      mailReader.ReadU32() == 100 && mailReader.ReadU32() == 200 && mailReader.ReadU64() == 0 &&
      mailReader.ReadU8() == 0 && mailReader.Remaining == 0, "send mail body order and constants");
Check(MailUiLaw.PageCount(0) == 1 && MailUiLaw.PageCount(7) == 1 &&
      MailUiLaw.PageCount(8) == 2 && MailUiLaw.FirstIndex(2, 8) == 7,
      "mail seven-row paging law");
Check(MailUiLaw.ExpiryText(1.9f) == "1 Day" && MailUiLaw.ExpiryText(2.1f) == "2 Days" &&
      MailUiLaw.ExpiryText(.9f) == "< 1 day", "mail expiry display law");
Check(!MailUiLaw.CanDelete(0, 0, hasItem: true, money: 0) &&
      !MailUiLaw.CanDelete(0, 0, hasItem: false, money: 1) &&
      MailUiLaw.CanDelete(0, 0, hasItem: false, money: 0) &&
      MailUiLaw.CanDelete(0, MailUiLaw.CheckedReturned, hasItem: true, money: 1),
      "mail delete-versus-return law");
Check(MailUiLaw.CanSend("Jaina", "Hi", codMode: false, amount: 1,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "", codMode: false, amount: 0,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "Hi", codMode: true, amount: 1,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "Hi", codMode: true, amount: MailUiLaw.MaxCodCopper + 1,
          hasAttachment: true, pending: false), "mail compose enablement law");
Check(MailUiLaw.NoMailQueryStamp == -1f &&
      MailUiLaw.HasNewMail(0) && !MailUiLaw.HasNewMail(-86400) &&
      !MailUiLaw.HasNewMail(5), "mail pending countdown law");
Check(MailUiLaw.OpenMailOrigin(new Vector2(384, 104), 1f) == new Vector2(758, 104) &&
      MailUiLaw.ConfirmationSize(1.5f) == new Vector2(540, 144) &&
      MailUiLaw.ConfirmationOrigin(new Vector2(1920, 1080), 1.5f) ==
          new Vector2(690, 192),
      "mail child-frame and confirmation positioning law drift");
Check(MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Left, 0) == 60 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Left, 11) == 71 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Right, 0) == 48 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Right, 11) == 59,
      "bottom multibar page/base mapping law");
Check(!MultiActionBarUiLaw.ShowEmptyWell(false) &&
      MultiActionBarUiLaw.ShowEmptyWell(true) &&
      !MultiActionBarUiLaw.InteractiveSlot(hasAction: false, cursorPayloadHeld: false) &&
      MultiActionBarUiLaw.InteractiveSlot(hasAction: true, cursorPayloadHeld: false) &&
      MultiActionBarUiLaw.InteractiveSlot(hasAction: false, cursorPayloadHeld: true),
      "bottom multibar cursor-only empty-grid/interaction law");
Check(MultiActionBarUiLaw.FrameWidth == 500 && MultiActionBarUiLaw.FrameHeight == 38 &&
      MultiActionBarUiLaw.ButtonSize == 36 && MultiActionBarUiLaw.ButtonStep == 42 &&
      MultiActionBarUiLaw.BottomLeftRise == 17 && MultiActionBarUiLaw.BottomBarGap == 10,
      "bottom multibar quoted geometry law");
Check(MultiActionBarUiLaw.InHorizontalButton(0, 2, 0, 0) &&
      MultiActionBarUiLaw.InHorizontalButton(35.999f, 37.999f, 0, 0) &&
      !MultiActionBarUiLaw.InHorizontalButton(36, 2, 0, 0) &&
      !MultiActionBarUiLaw.InHorizontalButton(41.999f, 2, 0, 0) &&
      MultiActionBarUiLaw.InHorizontalButton(42, 2, 0, 0) &&
      MultiActionBarUiLaw.InHorizontalButton(497.999f, 2, 0, 0) &&
      !MultiActionBarUiLaw.InHorizontalButton(498, 2, 0, 0) &&
      !MultiActionBarUiLaw.InHorizontalButton(0, 38, 0, 0),
      "bottom multibar exact 36-pixel buttons/six-pixel click-through gaps drift");

MultiActionKeyTransition multiPress = MultiActionBarUiLaw.AdvanceKey(
    armed: false, wasDown: false, isDown: true, typing: false, inWorld: true);
MultiActionKeyTransition multiHeldWhileTyping = MultiActionBarUiLaw.AdvanceKey(
    multiPress.Armed, wasDown: true, isDown: true, typing: true, inWorld: true);
MultiActionKeyTransition multiReleaseWhileTyping = MultiActionBarUiLaw.AdvanceKey(
    multiHeldWhileTyping.Armed, wasDown: true, isDown: false, typing: true, inWorld: true);
MultiActionKeyTransition multiPressWhileTyping = MultiActionBarUiLaw.AdvanceKey(
    armed: false, wasDown: false, isDown: true, typing: true, inWorld: true);
MultiActionKeyTransition multiFocusLeavesHeld = MultiActionBarUiLaw.AdvanceKey(
    multiPressWhileTyping.Armed, wasDown: true, isDown: true, typing: false, inWorld: true);
MultiActionKeyTransition multiIneligibleRelease = MultiActionBarUiLaw.AdvanceKey(
    multiFocusLeavesHeld.Armed, wasDown: true, isDown: false, typing: false, inWorld: true);
MultiActionKeyTransition multiRepeat = MultiActionBarUiLaw.AdvanceKey(
    armed: true, wasDown: true, isDown: true, typing: false, inWorld: true);
MultiActionKeyTransition multiWorldExit = MultiActionBarUiLaw.AdvanceKey(
    armed: true, wasDown: true, isDown: true, typing: false, inWorld: false);
Check(multiPress == new MultiActionKeyTransition(true, false) &&
      multiHeldWhileTyping == new MultiActionKeyTransition(true, false) &&
      multiReleaseWhileTyping == new MultiActionKeyTransition(false, true) &&
      multiPressWhileTyping == new MultiActionKeyTransition(false, false) &&
      multiFocusLeavesHeld == new MultiActionKeyTransition(false, false) &&
      multiIneligibleRelease == new MultiActionKeyTransition(false, false) &&
      multiRepeat == new MultiActionKeyTransition(true, false) &&
      multiWorldExit == new MultiActionKeyTransition(false, false),
      "bottom multibar eligible-press latch/release/typing/repeat/world-exit law drift");

Check(!MultiActionBarUiLaw.ItemMayBePlaced(0, 0) &&
      MultiActionBarUiLaw.ItemMayBePlaced(0, 8690) &&
      MultiActionBarUiLaw.ItemMayBePlaced(18, 0) &&
      MultiActionBarUiLaw.ShowItemCount(24, false) &&
      MultiActionBarUiLaw.ShowItemCount(25, false) &&
      MultiActionBarUiLaw.ShowItemCount(0, true) &&
      !MultiActionBarUiLaw.ShowItemCount(0, false),
      "bottom multibar item acceptance/count visibility law drift");
Check(MultiActionBarUiLaw.ItemUseRoute(0, false, true) == MultiActionItemRoute.Use &&
      MultiActionBarUiLaw.ItemUseRoute(0, false, false) == MultiActionItemRoute.None &&
      MultiActionBarUiLaw.ItemUseRoute(13, true, true) == MultiActionItemRoute.Use &&
      MultiActionBarUiLaw.ItemUseRoute(13, false, true) == MultiActionItemRoute.Equip &&
      MultiActionBarUiLaw.ItemUseRoute(13, false, false) == MultiActionItemRoute.None,
      "bottom multibar two-stage item equip/use route drift");
Check(MultiActionBarUiLaw.ItemUseDisposition(373, 8690, 1, true) ==
          MultiActionItemUseDisposition.QuestOffer &&
      MultiActionBarUiLaw.ItemUseDisposition(0, 0, 0, false) ==
          MultiActionItemUseDisposition.Nothing &&
      MultiActionBarUiLaw.ItemUseDisposition(0, 8690, 1, true) ==
          MultiActionItemUseDisposition.ToggleCancel &&
      MultiActionBarUiLaw.ItemUseDisposition(0, 8690, 0, true) ==
          MultiActionItemUseDisposition.Use,
      "bottom multibar quest/toggle/cast/nothing item-use fork drift");
Check(!MultiActionBarUiLaw.RequiresLiveCharges(0) &&
      !MultiActionBarUiLaw.RequiresLiveCharges(-1) &&
      MultiActionBarUiLaw.RequiresLiveCharges(1) &&
      MultiActionBarUiLaw.RequiresLiveCharges(-3) &&
      MultiActionBarUiLaw.LiveChargeCandidate(isContainer: true, remainingCharges: 0) &&
      MultiActionBarUiLaw.LiveChargeCandidate(isContainer: false, remainingCharges: null) &&
      !MultiActionBarUiLaw.LiveChargeCandidate(isContainer: false, remainingCharges: 0) &&
      MultiActionBarUiLaw.LiveChargeCandidate(isContainer: false, remainingCharges: 2),
      "bottom multibar finite/live item-charge filter drift");
ItemTemplate parsedMultiItem = ItemTemplate.Parse(BuildMultiActionItemTemplateFixture())
    ?? throw new InvalidDataException("bottom multibar item-template fixture did not parse");
Check(parsedMultiItem.Entry == 42 && parsedMultiItem.SpellCharges0 == 2 &&
      parsedMultiItem.UseSpellIndex == 1 && parsedMultiItem.UseSpellId == 8690 &&
      parsedMultiItem.UseSpellCharges == -3 && parsedMultiItem.UseSpellCategory == 321 &&
      parsedMultiItem.HasNegativeOnUseCharges && parsedMultiItem.StartQuest == 373 &&
      parsedMultiItem.Spells[0] == new ItemSpellTemplate(111, 1, 2, 0, 0, 0) &&
      parsedMultiItem.Spells[1] == new ItemSpellTemplate(8690, 0, -3, 0, 321, 0) &&
      parsedMultiItem.RandomProperty == 777 && parsedMultiItem.ItemSet == 25 &&
      parsedMultiItem.MaxDurability == 40 && parsedMultiItem.BagFamily == 9,
      "item template five-spell/random-property/item-set/durability parser drift");

ActionSlot heldMultiAction = new(ActionSlot.Spell, 1459);
ActionSlot displacedMultiAction = new(ActionSlot.Item, 6948);
Check(MultiActionBarUiLaw.PickupAction(heldMultiAction.Packed) ==
          new MultiActionPlacement(0, heldMultiAction.Packed) &&
      MultiActionBarUiLaw.PlaceAction(heldMultiAction.Packed, displacedMultiAction.Packed) ==
          new MultiActionPlacement(heldMultiAction.Packed, displacedMultiAction.Packed) &&
      WorldSession.BuildSetActionButtonBody(60, heldMultiAction.Packed)
          .SequenceEqual(Convert.FromHexString("3CB3050000")) &&
      WorldSession.BuildSetActionButtonBody(48, displacedMultiAction.Packed)
          .SequenceEqual(Convert.FromHexString("30241B0080")),
      "bottom multibar pickup/place-hop or five-byte SET_ACTION_BUTTON body drift");

string multiActionSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.ActionBars.cs"));
string multiInventorySource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Inventory.cs"));
string multiItemSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Net", "Items.cs"));
string multiSpellSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Formats", "SpellCatalog.cs"));
string multiCaptureSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
string multiLiveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
Check(multiActionSource.Contains("ImGuiWindowFlags.NoMouseInputs", StringComparison.Ordinal) &&
      multiActionSource.Contains("MultiActionBarUiLaw.InteractiveSlot", StringComparison.Ordinal) &&
      multiActionSource.Contains("dl.PushClipRectFullScreen();", StringComparison.Ordinal) &&
      multiActionSource.Contains("Visible: normalTextureVisible", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "InteractionState: \"number-font-normal-small-gray-thick-outline\"",
          StringComparison.Ordinal) &&
      multiActionSource.Contains("InteractionState: \"number-font-normal-outline\"",
          StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "GameText.DrawRightAligned(dl, \"NumberFontNormalSmallGray\"", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "GameText.DrawRightAligned(dl, \"NumberFontNormal\"", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "GameText.BoxCenteredTop(\"NumberFontNormalSmallGray\"", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "buttonMin.Y + 2f * scale, 10f, scale", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "new Vector2(buttonMin.X + 34f * scale, textTop)", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "buttonMax.Y - 2f * scale -", StringComparison.Ordinal) &&
      multiActionSource.Contains(
          "new Vector2(buttonMax.X - 2f * scale, textTop)", StringComparison.Ordinal) &&
      multiActionSource.Contains("ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight",
          StringComparison.Ordinal) &&
      multiActionSource.Contains("PickupActionToCursor(_pressedActionSlot);", StringComparison.Ordinal) &&
      !multiActionSource.Contains("SwapActions(_draggingActionSlot", StringComparison.Ordinal) &&
      multiActionSource.Contains("empty-button-hidden-with-grid-off", StringComparison.Ordinal) &&
      multiActionSource.Contains("no-active-item-or-spell-cooldown", StringComparison.Ordinal) &&
      multiActionSource.Contains("name == \"MultiBarBottomRight\") MarkUiParityFrameComplete()",
          StringComparison.Ordinal),
      "bottom multibar mouse host/both-button drag/hop/two-frame-root production wiring drift");
Check(multiActionSource.Contains("usability = stackCount > 0 || equipped", StringComparison.Ordinal) &&
      !multiActionSource.Contains("HasActionItemCopy(p, actionId)", StringComparison.Ordinal),
      "bottom multibar item usability must preserve the bag-count-or-equipped state-feed rule");
int equipmentWalk = multiInventorySource.IndexOf(
    "for (int slot = 0; slot < 19; slot++)", StringComparison.Ordinal);
int bagWalk = multiInventorySource.IndexOf(
    "for (int bagIndex = 0; bagIndex < 4; bagIndex++)", equipmentWalk,
    StringComparison.Ordinal);
int backpackWalk = multiInventorySource.IndexOf(
    "for (int i = 0; i < 16; i++)", bagWalk, StringComparison.Ordinal);
int keyringWalk = multiInventorySource.IndexOf(
    "PlayerKeyringSlot(i)", backpackWalk, StringComparison.Ordinal);
Check(equipmentWalk >= 0 && bagWalk > equipmentWalk && backpackWalk > bagWalk &&
      keyringWalk > backpackWalk &&
      multiInventorySource.Contains("ItemUseDisposition", StringComparison.Ordinal) &&
      multiInventorySource.Contains("ItemSpellCharges(0)", StringComparison.Ordinal) &&
      multiItemSource.Contains("HasNegativeOnUseCharges", StringComparison.Ordinal) &&
      multiItemSource.Contains("item.StartQuest = r.ReadU32();", StringComparison.Ordinal) &&
      multiSpellSource.Contains("uint activeIconId = spells.GetUInt(row, 118);", StringComparison.Ordinal) &&
      multiSpellSource.Contains("ActiveIconId: activeIconId", StringComparison.Ordinal),
      "bottom multibar depth-first item search/charge/count/quest/toggle production wiring drift");
Check(multiCaptureSource.Contains("MultiBarBottomLeft\" or \"MultiBarBottomRight",
          StringComparison.Ordinal) &&
      multiCaptureSource.Contains("explicit-live-protocol-fixture", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("captureStateMutation", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("captureNetworkMutation", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("interactive && !trace.Visible", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("RestoreMultiActionUiParityFixture();", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_multiActionUiFixtureRestorePending = true;", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_actions.Set(MultiActionBarUiLaw.BottomLeftBase, _multiActionUiFixtureLeft);",
          StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_actions.Set(MultiActionBarUiLaw.BottomRightBase, _multiActionUiFixtureRight);",
          StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_actionCursor = _multiActionUiFixtureActionCursor;", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_draggingSpellId = _multiActionUiFixtureDraggingSpell;", StringComparison.Ordinal) &&
      multiCaptureSource.Contains("_carriedContainer = _multiActionUiFixtureCarriedContainer;",
          StringComparison.Ordinal) &&
      multiCaptureSource.Split("RestoreMultiActionUiParityFixture();", StringSplitOptions.None).Length - 1 >= 4 &&
      multiLiveRunSource.Contains("_multiActionProtocolFixtureStaged = true;",
          StringComparison.Ordinal) &&
      multiLiveRunSource.Contains("provenance={UiParityProvenance}", StringComparison.Ordinal),
      "bottom multibar strong observational/fixture provenance/rollback capture wiring drift");

if (args.Contains("--multi-bars-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: MultiBars PASS");
    return;
}
Check(PetActionBarUiLaw.FrameWidth == 509 && PetActionBarUiLaw.FrameHeight == 43 &&
      PetActionBarUiLaw.BaseX == 36 && PetActionBarUiLaw.BaseTopOffset == 97 &&
      PetActionBarUiLaw.BottomMultiBarStep == 43 && PetActionBarUiLaw.ButtonTop == 11 &&
      PetActionBarUiLaw.ButtonX(0) == 36 && PetActionBarUiLaw.ButtonX(5) == 226 &&
      PetActionBarUiLaw.ButtonX(6) == 263 && PetActionBarUiLaw.ButtonX(9) == 377 &&
      PetActionBarUiLaw.NormalTextureSize == 54 &&
      PetActionBarUiLaw.NormalTextureOffset == new Vector2(0, -1) &&
      PetActionBarUiLaw.CooldownSize == 33 &&
      PetActionBarUiLaw.CooldownOffset == new Vector2(-2, -1) &&
      PetActionBarUiLaw.AutoCastOverlaySize == 58,
      "pet action bar frozen frame/button/managed-seat/overlay geometry law");
uint petSpell = 123u | (1u << 24) | PetActionBarUiLaw.AutocastAllowed;
uint petAttack = 2u | (7u << 24);
Check(PetActionBarUiLaw.Action(petSpell) == 123 && PetActionBarUiLaw.Kind(petSpell) == 1 &&
      PetActionBarUiLaw.Autocastable(petSpell) &&
      PetActionBarUiLaw.Autocastable(petSpell, spellResolved: true) &&
      !PetActionBarUiLaw.Autocastable(petSpell, spellResolved: false) &&
      PetActionBarUiLaw.Active(petAttack, 0, attacking: true) &&
      PetActionBarUiLaw.LatchPress(2, 1u | (7u << 24)) == 0x102 &&
      PetActionBarUiLaw.LatchPress(0x0812_3402, 0u | (7u << 24)) == 0x0800_0002 &&
      PetActionBarUiLaw.LatchPress(0x0812_3402, 1u | (6u << 24)) == 0x0812_3401,
      "pet action packed-word/local command/reaction/attack state law");
Check(PetActionBarUiLaw.ActiveAuraPress(petSpell, 77, matchingCancelableAura: true) &&
      !PetActionBarUiLaw.ActiveAuraPress(petSpell, 0, matchingCancelableAura: true) &&
      !PetActionBarUiLaw.ActiveAuraPress(petSpell, 77, matchingCancelableAura: false) &&
      !PetActionBarUiLaw.ActiveAuraPress(petAttack, 77, matchingCancelableAura: true),
      "pet active-icon/aura/cancel predicate drift");
Check(PetActionBarUiLaw.ActionTarget(0x1234) == 0x1234 &&
      PetActionBarUiLaw.ActionTarget(0) == 0 &&
      PetActionBarUiLaw.StopsAttackOnSelectionChange(true, 7, 9) &&
      PetActionBarUiLaw.StopsAttackOnSelectionChange(true, 7, 0) &&
      !PetActionBarUiLaw.StopsAttackOnSelectionChange(true, 0, 9) &&
      !PetActionBarUiLaw.StopsAttackOnSelectionChange(true, 7, 7) &&
      !PetActionBarUiLaw.StopsAttackOnSelectionChange(false, 7, 9),
      "pet target payload/old-target attack-stop edge drift");
Check(PetActionBarUiLaw.FeedbackKey(1) == "PET_SPELL_NOPATH" &&
      PetActionBarUiLaw.FeedbackKey(2) == "SPELL_FAILED_OUT_OF_RANGE" &&
      PetActionBarUiLaw.FeedbackKey(0) is null &&
      PetActionBarUiLaw.FeedbackKey(3) is null,
      "pet action feedback table drift");
Check(PetActionBarUiLaw.AttackRefusalKey(0, 9, 7, 0x00C6_0000, 4) == "ERR_ATTACK_DEAD" &&
      PetActionBarUiLaw.AttackRefusalKey(1, 9, 7, 0x00C6_0000, 4) == "ERR_ATTACK_CHARMED" &&
      PetActionBarUiLaw.AttackRefusalKey(1, null, 7, 0x0004_0000, 4) == "ERR_ATTACK_STUNNED" &&
      PetActionBarUiLaw.AttackRefusalKey(1, null, 7, 0x0002_0000, 4) == "ERR_ATTACK_PACIFIED" &&
      PetActionBarUiLaw.AttackRefusalKey(1, null, 7, 0x0080_0000, 4) == "ERR_ATTACK_FLEEING" &&
      PetActionBarUiLaw.AttackRefusalKey(1, null, 7, 0x0040_0000, 4) == "ERR_ATTACK_CONFUSED" &&
      PetActionBarUiLaw.AttackRefusalKey(1, null, 7, 0, 4) == "ERR_ATTACK_MOUNTED" &&
      PetActionBarUiLaw.AttackRefusalKey(null, null, 7, 0, 0) is null,
      "pet Attack actor refusal order/global-string key drift");
Check(PetActionBarUiLaw.InteractiveSlot(named: true, cursorPayloadHeld: false) &&
      PetActionBarUiLaw.InteractiveSlot(named: false, cursorPayloadHeld: true) &&
      !PetActionBarUiLaw.InteractiveSlot(named: false, cursorPayloadHeld: false) &&
      PetActionBarUiLaw.PickupAllowed(0) &&
      !PetActionBarUiLaw.PickupAllowed(PetActionBarUiLaw.UnitFlagPossessed) &&
      PetActionBarUiLaw.Usable(0, 0) &&
      !PetActionBarUiLaw.Usable(PetActionBarUiLaw.BarDisabled, 0) &&
      !PetActionBarUiLaw.Usable(0, 0x0004_0000) &&
      PetActionBarUiLaw.Usable(0, PetActionBarUiLaw.UnitFlagPossessed),
      "pet unnamed-slot/possessed-pickup/usability separation drift");
Check(PetActionBarUiLaw.StripPermanentCooldownMarker(0x0800_1234) == 0x1234 &&
      PetActionBarUiLaw.StripPermanentCooldownMarker(30_000) == 30_000,
      "pet category cooldown permanent-marker normalization drift");
Check(PetActionBarUiLaw.SparkleSize(0) == 14.4f &&
      PetActionBarUiLaw.SparkleSize(.5f) == 5.76f &&
      PetActionBarUiLaw.SparkleSize(1) == 2.88f &&
      PetActionBarUiLaw.SparkleColor(0) == new Vector4(.976f, .875f, .192f, 1f) &&
      PetActionBarUiLaw.SparkleColor(.5f) == new Vector4(.996f, .945f, .745f, 1f) &&
      PetActionBarUiLaw.SparkleColor(1) == new Vector4(1f, 1f, 1f, 0f),
      "pet four-emitter M2 three-key linear size/color ramp drift");
uint[] petSlots = [7u << 24, petSpell, 0, 0, 0, 0, 0, 0, 0, 0];
Check(PetActionBarUiLaw.TryAssign(petSlots, 2, petSpell, passive: false, out var petAssign) &&
      petAssign.RelocationSlot == 1 && petSlots[1] == 0 && petSlots[2] == petSpell,
      "pet duplicate-word relocation law");
uint petClaw = 0xC100_0BC2;
uint petGrowl = 0x8100_0EC0;
uint petFiller = 0x8100_0000;
uint petToken = 0x0700_0002;
uint[] displacedSlots = [petClaw & 0xFFFF_0000, petGrowl, petFiller, petToken];
Check(PetActionBarUiLaw.TryAssign(displacedSlots, 1, petClaw, passive: false,
          out var displacedPet) && !displacedPet.Relocated &&
      displacedPet.DisplacedWord == petGrowl && displacedSlots[1] == petClaw,
      "pet blanked-spell drop must displace occupant onto cursor");
uint[] tokenSlots = [petClaw & 0xFFFF_0000, petToken, petFiller, 0x0600_0001];
Check(PetActionBarUiLaw.TryAssign(tokenSlots, 1, petGrowl, passive: false,
          out var relocatedToken) && relocatedToken.RelocationSlot == 0 &&
      tokenSlots[0] == petToken && tokenSlots[1] == petGrowl,
      "pet token occupant must relocate to first filler in one atomic write");
uint[] refusedSlots = [petClaw, petGrowl, petFiller, petToken];
uint[] refusedBefore = refusedSlots.ToArray();
Check(!PetActionBarUiLaw.TryAssign(refusedSlots, 1, petClaw, passive: true, out _) &&
      refusedSlots.SequenceEqual(refusedBefore),
      "pet passive-source refused drop must not mutate slots");

const ulong clinicalPetGuid = 0x0102_0304_0506_0708ul;
const ulong clinicalPetTarget = 0x1112_1314_1516_1718ul;
Check(WorldSession.BuildPetActionBody(clinicalPetGuid, petClaw, clinicalPetTarget)
          .SequenceEqual(Convert.FromHexString(
              "0807060504030201C20B00C11817161514131211")) &&
      WorldSession.BuildPetCancelAuraBody(clinicalPetGuid, 2645)
          .SequenceEqual(Convert.FromHexString("0807060504030201550A0000")) &&
      WorldSession.BuildPetSetActionBody(clinicalPetGuid, [(3u, petClaw)])
          .SequenceEqual(Convert.FromHexString(
              "080706050403020103000000C20B00C1")) &&
      WorldSession.BuildPetSetActionBody(clinicalPetGuid,
          [(3u, petToken), (0u, petClaw)])
          .SequenceEqual(Convert.FromHexString(
              "0807060504030201030000000200000700000000C20B00C1")),
      "pet action/cancel-aura/set-action golden wire bodies drift");
Check((ushort)Op.CMSG_PET_SET_ACTION == 0x174 && (ushort)Op.CMSG_PET_ACTION == 0x175 &&
      (ushort)Op.SMSG_PET_SPELLS == 0x179 && (ushort)Op.SMSG_PET_MODE == 0x17A &&
      (ushort)Op.CMSG_PET_CANCEL_AURA == 0x26B &&
      (ushort)Op.CMSG_PET_STOP_ATTACK == 0x2EA &&
      (ushort)Op.SMSG_SPELL_COOLDOWN == 0x134,
      "pet action protocol opcodes");

string petRoot = ClientConfig.FindRepoRoot();
string petRuntimeSource = SourceText.Read(Path.Combine(petRoot, "MSUIClient", "Program.Pet.cs"));
string petTargetingSource = SourceText.Read(Path.Combine(petRoot, "MSUIClient", "Program.Targeting.cs"));
string petNetSource = SourceText.Read(Path.Combine(petRoot, "MSUIClient", "Program.Net.cs"));
string petLogoutSource = SourceText.Read(Path.Combine(petRoot, "MSUIClient", "Program.Logout.cs"));
string petCaptureSource = SourceText.Read(Path.Combine(petRoot, "MSUIClient",
    "Program.DevTools.UiParity.cs"));
int petCancelAura = petRuntimeSource.IndexOf("PetCancelAura(petGuid, action)",
    StringComparison.Ordinal);
int petOrdinaryAction = petRuntimeSource.IndexOf("PetAction(petGuid, packed",
    petCancelAura, StringComparison.Ordinal);
int petShiftRelease = petRuntimeSource.IndexOf("if (released && ShiftHeld())",
    StringComparison.Ordinal);
int petRightRelease = petRuntimeSource.IndexOf("else if (clickedRight[i]",
    petShiftRelease, StringComparison.Ordinal);
Check(petRuntimeSource.Contains("display.Y - topOffset * s", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("BaseTopOffset + PetActionBarUiLaw.BottomMultiBarStep",
          StringComparison.Ordinal) &&
      petRuntimeSource.Contains("PetActionBarUiLaw.ButtonTop", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("NoMouseInputs", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("PushClipRectFullScreen", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight",
          StringComparison.Ordinal) &&
      petRuntimeSource.Contains("iconPath.Length > 0", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("_petCooldowns.TryCooldownDisplay", StringComparison.Ordinal) &&
      !petRuntimeSource.Contains("_petAttackSelection", StringComparison.Ordinal),
      "pet exact managed seat/zero-clip/both-button/icon-ring/dedicated-cooldown production wiring drift");
Check(petShiftRelease >= 0 && petRightRelease > petShiftRelease &&
      petRuntimeSource.Contains("_petActionPressMouseButton", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("PlacePetAction(hoveredSlot, petGuid, pet)",
          StringComparison.Ordinal),
      "pet shift-priority/both-button drag/drop production routing drift");
Check(petCancelAura >= 0 && petOrdinaryAction > petCancelAura &&
      petRuntimeSource.Contains("PetActionBarUiLaw.ActionTarget(_selectionGuid)",
          StringComparison.Ordinal) &&
      petTargetingSource.Contains("StopPetAttackForOldTargetChange(_selectionGuid, guid)",
          StringComparison.Ordinal),
      "pet active-aura exclusive cancel/current-target/old-target-stop production wiring drift");
Check(petRuntimeSource.Contains("if (guid == 0) ResetPetActionBar();", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("_petCooldowns.Clear();", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("StripPermanentCooldownMarker", StringComparison.Ordinal) &&
      petNetSource.Contains("case Op.SMSG_SPELL_COOLDOWN:", StringComparison.Ordinal) &&
      petNetSource.Contains("ApplyAddressedSpellCooldowns(body);", StringComparison.Ordinal) &&
      petNetSource.Split("ResetPetActionBar();", StringSplitOptions.None).Length - 1 >= 2 &&
      petLogoutSource.Contains("ResetPetActionBar();", StringComparison.Ordinal),
      "pet atomic replace/addressed cooldown/session teardown production wiring drift");
Check(!petRuntimeSource.Contains("StagePetActionBarProof", StringComparison.Ordinal) &&
      petCaptureSource.Contains("pet-action-bar-requires-observed-controlled-unit-state",
          StringComparison.Ordinal) &&
      petCaptureSource.Contains("stateSource=pet-wire-runtime", StringComparison.Ordinal) &&
      petCaptureSource.Contains("captureStateMutation=false;captureNetworkMutation=false",
          StringComparison.Ordinal) &&
      petRuntimeSource.Contains("PetActionBarTexture0", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("bottom-multibars-always-visible", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("hidden-unnamed", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("FOUR_EMITTER_M2_LINEAR_TRAILS", StringComparison.Ordinal) &&
      petRuntimeSource.Contains("parent-not-mouse-enabled", StringComparison.Ordinal),
      "pet observational-only strong capture/census/provenance wiring drift");

if (args.Contains("--pet-action-bar-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: PetActionBar PASS");
    return;
}
Check(QuestFrameUiLaw.Width == 384 && QuestFrameUiLaw.Height == 512 &&
      QuestFrameUiLaw.ScrollX == 23 && QuestFrameUiLaw.ScrollY == 81 &&
      QuestFrameUiLaw.ScrollWidth == 300 && QuestFrameUiLaw.ScrollHeight == 334 &&
      QuestFrameUiLaw.CloseMin == new Vector2(326, 15),
      "quest giver outer/scroll geometry law");
Check(QuestFrameUiLaw.ItemGridOffset(0) == Vector2.Zero &&
      QuestFrameUiLaw.ItemGridOffset(1) == new Vector2(148, 0) &&
      QuestFrameUiLaw.ItemGridOffset(2) == new Vector2(0, 43) &&
      QuestFrameUiLaw.ClampScroll(500, 700) == 366 &&
      !QuestFrameUiLaw.RewardCompleteEnabled(2, -1) &&
      QuestFrameUiLaw.RewardCompleteEnabled(2, 1) &&
      QuestFrameUiLaw.RewardCompleteEnabled(0, -1),
      "quest giver item grid/scroll/reward selection law");
Check(QuestFrameUiLaw.WindowOrigin(2f) == new Vector2(0, 208) &&
      QuestFrameUiLaw.WindowSize(2f) == new Vector2(768, 1024) &&
      QuestFrameUiLaw.ClampQuestLogOffset(99, 20) == 14 &&
      QuestFrameUiLaw.ClampQuestLogOffset(-1, 4) == 0 &&
      QuestFrameUiLaw.QuestLogRowMin(5) == new Vector2(19, 150) &&
      QuestFrameUiLaw.QuestDifficultyColor(20, 25) == new Vector4(1f, .1f, .1f, 1f) &&
      QuestFrameUiLaw.QuestDifficultyColor(20, 23) == new Vector4(1f, .5f, .25f, 1f) &&
      QuestFrameUiLaw.QuestDifficultyColor(20, 20) == new Vector4(1f, 1f, 0f, 1f) &&
      QuestFrameUiLaw.QuestDifficultyColor(20, 17) == new Vector4(.25f, .75f, .25f, 1f) &&
      QuestFrameUiLaw.QuestDifficultyColor(20, 1) == new Vector4(.5f, .5f, .5f, 1f),
      "quest-log modal/list/difficulty law drift");
Check(QuestFrameUiLaw.GreetingPool(3) == QuestGreetingPool.Active &&
      QuestFrameUiLaw.GreetingPool(4) == QuestGreetingPool.Active &&
      new uint[] { 0, 1, 2, 5, 6, uint.MaxValue }.All(icon =>
          QuestFrameUiLaw.GreetingPool(icon) == QuestGreetingPool.Available) &&
      QuestFrameUiLaw.GreetingAction(3) == QuestGreetingAction.Complete &&
      QuestFrameUiLaw.GreetingAction(4) == QuestGreetingAction.Complete &&
      QuestFrameUiLaw.GreetingAction(0) == QuestGreetingAction.Complete &&
      QuestFrameUiLaw.GreetingAction(2) == QuestGreetingAction.Query,
      "quest greeting wire-icon split/one-click routing drift");
Check(QuestFrameUiLaw.Money(50).SequenceEqual([new QuestCoin(2, 50)]) &&
      QuestFrameUiLaw.Money(10_005).SequenceEqual(
          [new QuestCoin(0, 1), new QuestCoin(2, 5)]) &&
      QuestFrameUiLaw.Money(0).Count == 0,
      "quest money must omit zero denominations and retain high-to-low order");
Check(QuestFrameUiLaw.InvalidReasonKey(1) == "ERR_QUEST_FAILED_LOW_LEVEL" &&
      QuestFrameUiLaw.InvalidReasonKey(6) == "ERR_QUEST_FAILED_WRONG_RACE" &&
      QuestFrameUiLaw.InvalidReasonKey(12) == "ERR_QUEST_ONLY_ONE_TIMED" &&
      QuestFrameUiLaw.InvalidReasonKey(13) == "ERR_QUEST_ALREADY_ON" &&
      QuestFrameUiLaw.InvalidReasonKey(20) == "ERR_QUEST_FAILED_MISSING_ITEMS" &&
      QuestFrameUiLaw.InvalidReasonKey(22) == "ERR_QUEST_FAILED_NOT_ENOUGH_MONEY" &&
      new uint[] { 0, 2, 4, 23, uint.MaxValue }.All(reason =>
          QuestFrameUiLaw.InvalidReasonKey(reason) == "ERR_QUEST_NEED_PREREQS") &&
      QuestFrameUiLaw.GiverFailureKey(4) == "ERR_QUEST_FAILED_BAG_FULL_S" &&
      QuestFrameUiLaw.GiverFailureKey(50) == "ERR_QUEST_FAILED_BAG_FULL_S" &&
      QuestFrameUiLaw.GiverFailureKey(17) == "ERR_QUEST_FAILED_MAX_COUNT_S" &&
      QuestFrameUiLaw.GiverFailureKey(0) == "ERR_QUEST_FAILED_S",
      "quest refusal reason/global-string routing table drift");
Check(GuidInfo.IsItem(0x4000_0000_0000_0BADul) &&
      !GuidInfo.IsItem(0xF130_0000_00C5_0001ul),
      "quest item-giver GUID classification drift");
var macroMale = new QuestTextMacroLaw.Subject("Thrall", "Night Elf", "Priest", 0);
var macroFemale = macroMale with { Gender = 1 };
var macroStates = new Dictionary<uint, uint>
{
    [2077] = 12,
    [unchecked(0u - 2077)] = 3,
    [2264] = unchecked((uint)-5),
};
Check(QuestTextMacroLaw.Expand("$N the $c of $R$B$Glad:lass;", macroMale, macroStates) ==
          "Thrall the priest of Night Elf\nlad" &&
      QuestTextMacroLaw.Expand("$G lad : lass ; $2077w/$2077e/$2264W", macroFemale, macroStates) ==
          "lass 12/3/-5" &&
      QuestTextMacroLaw.Expand("A $5X and $N", null, macroStates) == "A $X and $N" &&
      QuestTextMacroLaw.Expand("Broken $G male female;", macroMale, macroStates) ==
          "Broken male female;",
      "quest NPC-text B/C/E/G/N/R/T/W grammar drift");
var initStates = new Dictionary<uint, uint> { [99] = 7, [2077] = 1 };
QuestWorldStateLaw.ApplyInit(initStates, [(2077u, 12u), (0u, 0u)]);
Check(initStates[99] == 7 && initStates[2077] == 12 && initStates[0] == 0,
      "quest world-state init must upsert every pair without inventing a table clear");
var requestItemsWriter = new PacketWriter();
requestItemsWriter.WriteU64(0x42); requestItemsWriter.WriteU32(100);
requestItemsWriter.WriteCString("Title"); requestItemsWriter.WriteCString("Text");
requestItemsWriter.WriteU32(0); requestItemsWriter.WriteU32(0); requestItemsWriter.WriteU32(0);
requestItemsWriter.WriteU32(0); requestItemsWriter.WriteU32(0); requestItemsWriter.WriteU32(0);
requestItemsWriter.WriteU32(1); requestItemsWriter.WriteU32(0); requestItemsWriter.WriteU32(0);
Check(QuestPackets.ParseRequestItems(requestItemsWriter.ToArray()).Completable,
      "quest request-items second completion flag must accept any nonzero value");
var queryWriter = new PacketWriter();
queryWriter.WriteU32(77); queryWriter.WriteU32(2); queryWriter.WriteU32(18);
for (int i = 0; i < 12; i++) queryWriter.WriteU32(0);
for (int i = 0; i < 20; i++) queryWriter.WriteU32(0);
for (int i = 0; i < 4; i++) queryWriter.WriteU32(0);
queryWriter.WriteCString("A Full Query"); queryWriter.WriteCString("Do the work.");
queryWriter.WriteCString("Long details."); queryWriter.WriteCString("Done.");
queryWriter.WriteU32(123); queryWriter.WriteU32(10); queryWriter.WriteU32(0); queryWriter.WriteU32(0);
queryWriter.WriteU32(0); queryWriter.WriteU32(0); queryWriter.WriteU32(456); queryWriter.WriteU32(4);
for (int i = 0; i < 8; i++) queryWriter.WriteU32(0);
queryWriter.WriteCString("Special targets"); queryWriter.WriteCString("");
queryWriter.WriteCString(""); queryWriter.WriteCString("");
QuestTemplate query = QuestPackets.ParseQueryResponse(queryWriter.ToArray());
Check(query.QuestId == 77 && query.Level == 18 && query.Title == "A Full Query" &&
      query.ObjectivesText == "Do the work." && query.Details == "Long details." &&
      query.Objectives[0] == new QuestLogObjective(123, 10, 0, 0, "Special targets") &&
      query.Objectives[1] == new QuestLogObjective(0, 0, 456, 4, ""),
      "quest query fixed-count/template/objective decode drift");
Check((ushort)Op.CMSG_QUESTGIVER_COMPLETE_QUEST == 0x018A &&
      (ushort)Op.CMSG_QUESTGIVER_REQUEST_REWARD == 0x018C &&
      (ushort)Op.SMSG_INIT_WORLD_STATES == 0x02C2 &&
      (ushort)Op.SMSG_UPDATE_WORLD_STATE == 0x02C3,
      "quest completion/reward/world-state opcode identities drift");
string questSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Quest.cs"));
string gossipSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Gossip.cs"));
Check(questSource.Contains("RequestQuestReward();", StringComparison.Ordinal) &&
      questSource.Contains("GuidInfo.IsItem(guid)", StringComparison.Ordinal) &&
      questSource.Contains("CloseQuestNpcFrame(playSound: true);", StringComparison.Ordinal) &&
      questSource.Contains("UI-Quest-BotLeftPatch", StringComparison.Ordinal) &&
      questSource.Contains("QuestFrameUiLaw.GreetingAction(quest.Icon)", StringComparison.Ordinal) &&
      gossipSource.Contains("QuestFrameUiLaw.GreetingAction(row.QuestIcon)",
          StringComparison.Ordinal),
      "quest production routing/item-giver/lifecycle/bottom-patch wiring drift");
int questPortraitDraw = questSource.IndexOf("DrawUnitPortraitImage(dl, giver", StringComparison.Ordinal);
int questPanelArt = questSource.IndexOf("QuestFrameUiLaw.PanelArt(", StringComparison.Ordinal);
Check(questPortraitDraw >= 0 && questPanelArt > questPortraitDraw &&
      questSource.Contains("BenillaQuestFramePortraitAperture", StringComparison.Ordinal),
      "quest portrait must draw beneath panel chrome and retain round-aperture containment telemetry");
string liveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
string uiParitySource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
Check(liveRunSource.Contains("quest[1].Equals(\"assert-wire\"", StringComparison.Ordinal) &&
      liveRunSource.Contains("TryQuestWireSpec", StringComparison.Ordinal) &&
      liveRunSource.Contains("quest[1].Equals(\"assert-panel\"", StringComparison.Ordinal) &&
      liveRunSource.Contains("quest[1].Equals(\"assert-giver-kind\"", StringComparison.Ordinal) &&
      liveRunSource.Contains("quest[1].Equals(\"assert-greeting-counts\"", StringComparison.Ordinal),
      "quest connected protocol must expose exact state/giver/split/wire assertions");
int questObserverReturn = uiParitySource.IndexOf("if (!stageFixture) return;", StringComparison.Ordinal);
int questFixtureStage = uiParitySource.IndexOf("if (panel == \"quest-frame\") StageQuestFrameProof(",
    StringComparison.Ordinal);
Check(questObserverReturn >= 0 && questFixtureStage > questObserverReturn &&
      uiParitySource.Contains("captureCommand = _uiParityFixtureStaged ? \"ui-parity-stage\" : \"ui-parity\"",
          StringComparison.Ordinal) &&
      uiParitySource.Contains("stateSource", StringComparison.Ordinal) &&
      uiParitySource.Contains("captureStateMutation", StringComparison.Ordinal),
      "quest capture must remain observational unless explicit staged fixture mode is requested");

Check((ushort)Op.MSG_AUCTION_HELLO == 597 && (ushort)Op.CMSG_AUCTION_SELL_ITEM == 598 &&
      (ushort)Op.CMSG_AUCTION_REMOVE_ITEM == 599 && (ushort)Op.CMSG_AUCTION_LIST_ITEMS == 600 &&
      (ushort)Op.CMSG_AUCTION_LIST_OWNER_ITEMS == 601 && (ushort)Op.CMSG_AUCTION_PLACE_BID == 602 &&
      (ushort)Op.SMSG_AUCTION_COMMAND_RESULT == 603 && (ushort)Op.SMSG_AUCTION_LIST_RESULT == 604 &&
      (ushort)Op.SMSG_AUCTION_BIDDER_NOTIFICATION == 606 &&
      (ushort)Op.SMSG_AUCTION_OWNER_NOTIFICATION == 607 &&
      (ushort)Op.SMSG_AUCTION_REMOVED_NOTIFICATION == 653,
      "auction opcodes");
Check(WorldSession.BuildAuctionGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "auction hello full guid body");
Check(WorldSession.BuildAuctionBidBody(trainerGuid, 7, 123)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1070000007B000000")), "auction bid body");
Check(WorldSession.BuildAuctionSellBody(trainerGuid, 9, 100, 200, 720).Length == 28,
      "auction sell fixed body");
// BuildAuctionBrowseBody takes the typed query now; the fixture keeps the same
// row-offset / search / no-filter values the old positional call expressed.
var browseReader = new PacketReader(WorldSession.BuildAuctionBrowseBody(trainerGuid,
    new AuctionBrowseQuery(50, "Sword", 0, 0, AuctionBrowseQuery.Any, AuctionBrowseQuery.Any,
        AuctionBrowseQuery.Any, 0, false)));
Check(browseReader.ReadU64() == trainerGuid && browseReader.ReadU32() == 50 && browseReader.ReadCString() == "Sword" &&
      browseReader.ReadU8() == 0 && browseReader.ReadU8() == 0 && browseReader.ReadU32() == uint.MaxValue,
      "auction browse page/search/filter order");
Check(GameLoop.ProfessionSkillColor(1, 25, 70) == "orange" &&
      GameLoop.ProfessionSkillColor(30, 25, 70) == "yellow" &&
      GameLoop.ProfessionSkillColor(60, 25, 70) == "green" &&
      GameLoop.ProfessionSkillColor(70, 25, 70) == "gray", "profession skill-up range colors");
Check((ushort)Op.CMSG_GUILD_ROSTER == 137 && (ushort)Op.SMSG_GUILD_ROSTER == 138 &&
      (ushort)Op.CMSG_GUILD_PROMOTE == 139 && (ushort)Op.CMSG_GUILD_DEMOTE == 140 &&
      (ushort)Op.CMSG_GUILD_LEAVE == 141 && (ushort)Op.CMSG_GUILD_DISBAND == 143 &&
      (ushort)Op.CMSG_GUILD_MOTD == 145 && (ushort)Op.SMSG_GUILD_EVENT == 146 &&
      (ushort)Op.SMSG_GUILD_COMMAND_RESULT == 147, "guild opcodes");
Check(WorldSession.BuildCStringBody("Night").SequenceEqual(Convert.FromHexString("4E6967687400")), "guild CString bodies");

Check((ushort)Op.MSG_SAVE_GUILD_EMBLEM == 497 && (ushort)Op.SMSG_TABARDVENDOR_ACTIVATE == 498,
      "tabard opcodes");
Check(WorldSession.BuildSaveGuildEmblemBody(trainerGuid, 7, 3, 2, 5, 11)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1070000000300000002000000050000000B000000")),
      "tabard save vendor guid plus five u32 fields");
var tabardEquipment = new MSUIClient.World.Units.CharacterEquipment
{
    GuildEmblem = new(7, 3, 2, 5, 11),
};
tabardEquipment.Add("Guild Tabard", 0, MSUIClient.World.Units.CharacterEquipment.Slot.Tabard);
var tabardPaths = new List<string>();
tabardEquipment.Composite(new byte[256 * 256 * 4], 256, 256, path =>
{
    tabardPaths.Add(path); return (new byte[128 * 64 * 4], 128, 64);
});
Check(tabardPaths.SequenceEqual(new[]
{
    @"Textures\GuildEmblems\Background_11_TU_U.blp",
    @"Textures\GuildEmblems\Border_02_05_TU_U.blp",
    @"Textures\GuildEmblems\Emblem_07_03_TU_U.blp",
    @"Textures\GuildEmblems\Background_11_TL_U.blp",
    @"Textures\GuildEmblems\Border_02_05_TL_U.blp",
    @"Textures\GuildEmblems\Emblem_07_03_TL_U.blp",
}), "tabard renderer binds exact six MPQ layers");

Check((ushort)Op.CMSG_UNLEARN_TALENTS == 531 && (ushort)Op.CMSG_LEARN_TALENT == 593 &&
      (ushort)Op.MSG_TALENT_WIPE_CONFIRM == 682, "talent opcodes");
Check((ushort)Op.SMSG_REMOVED_SPELL == 515, "talent reset spell-removal opcode");
Check(WorldSession.BuildLearnTalentBody(124, 0).SequenceEqual(Convert.FromHexString("7C00000000000000")),
      "learn talent id/requested-rank body");
Check(WorldSession.BuildTalentWipeBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "talent wipe full trainer guid body");

Check((ushort)Op.CMSG_GAMEOBJ_USE == 177 && (ushort)Op.SMSG_GAMEOBJECT_CUSTOM_ANIM == 179 &&
      (ushort)Op.CMSG_PAGE_TEXT_QUERY == 90 && (ushort)Op.SMSG_PAGE_TEXT_QUERY_RESPONSE == 91,
      "game object/page opcodes");
Check((ushort)Op.SMSG_FISH_NOT_HOOKED == 456 && (ushort)Op.SMSG_FISH_ESCAPED == 457 &&
      (ushort)Op.SMSG_GAMEOBJECT_DESPAWN_ANIM == 533 &&
      LootPackets.ParseFishingVerdict([], escaped: false) == "ERR_FISH_NOT_HOOKED" &&
      LootPackets.ParseFishingVerdict([], escaped: true) == "ERR_FISH_ESCAPED",
      "game object fishing/despawn opcodes and empty fishing verdicts");
Check((ushort)Op.CMSG_GAMEOBJECT_QUERY == 94 && (ushort)Op.SMSG_GAMEOBJECT_QUERY_RESPONSE == 95,
      "game object template query opcodes");
ulong objectGuid = 0xF110000003000001ul;
Check(WorldSession.BuildGameObjectQueryBody(1731, objectGuid)
      .SequenceEqual(Convert.FromHexString("C306000001000003000010F1")),
      "game object query entry/full-guid body");
Check(WorldSession.BuildGameObjectUseBody(objectGuid).SequenceEqual(Convert.FromHexString("01000003000010F1")),
      "game object use full guid body");
Check(WorldSession.BuildPageTextQueryBody(77, objectGuid)
      .SequenceEqual(Convert.FromHexString("4D00000001000003000010F1")),
      "page text query page/full-guid body");
using (var professionMpq = new MpqMount(clientData))
{
    LockCatalog locks = LockCatalog.Load(professionMpq) ?? throw new InvalidDataException("Lock.dbc missing");
    Check(locks.ResourceLockType(29) == 2 && locks.ResourceLockType(38) == 3,
        "Silverleaf and Copper Lock.dbc skill-slot mapping");
    Check(locks.MatchesResourceMask(29, 0x2) && !locks.MatchesResourceMask(29, 0x4) &&
          locks.MatchesResourceMask(38, 0x4) && !locks.MatchesResourceMask(38, 0x2),
        "resource masks discriminate herb/mineral nodes");
    SpellFocusCatalog foci = SpellFocusCatalog.Load(professionMpq) ??
        throw new InvalidDataException("SpellFocusObject.dbc missing");
    Check(foci.Name(1) == "Anvil" && foci.Name(3) == "Forge" &&
          foci.Name(4) == "Cooking Fire" && foci.Name(543) == "Black Forge",
        "crafting focus names");
}
Check((ushort)Op.SMSG_LEVELUP_INFO == 468, "level-up info opcode");
Check(ObjectFields.PLAYER_REST_STATE_EXPERIENCE == 1175 && ObjectFields.PLAYER_XP == 716,
      "rest/experience build-5875 field indices");
Check(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 == 260,
      "build-5875 public visible-item entry must be creator+2, not the pre-block offset");

var playerFieldsWriter = new PacketWriter();
var playerFieldValues = new SortedDictionary<ushort, uint>
{
    [ObjectFields.UNIT_BYTES_0] = 1u | (8u << 8), // Human Mage male
    [ObjectFields.PLAYER_BYTES] = 2u | (3u << 8) | (4u << 16) | (5u << 24),
    [ObjectFields.PLAYER_BYTES_2] = 6u,
    [ObjectFields.PLAYER_VISIBLE_ITEM_1_0] = 1000u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 1)] = 2564u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 14 * 12)] = 1014u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 14 * 12 + 2)] = 1900u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 18 * 12)] = 1018u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 18 * 12 + 7)] = 846u,
};
const int playerFieldBlocks = 16;
playerFieldsWriter.WriteU8(playerFieldBlocks);
for (int block = 0; block < playerFieldBlocks; block++)
{
    uint mask = 0;
    foreach (ushort field in playerFieldValues.Keys)
        if (field / 32 == block) mask |= 1u << (field & 31);
    playerFieldsWriter.WriteU32(mask);
}
foreach (uint value in playerFieldValues.Values) playerFieldsWriter.WriteU32(value);
ObjectFields streamedFields = ObjectFields.Read(
    new PacketReader(playerFieldsWriter.ToArray())).AsCreated();
Check(streamedFields.Bytes0 == (1, 8, 0, 0) &&
      streamedFields.PlayerAppearance == (2, 3, 4, 5) &&
      streamedFields.PlayerFacialHair == 6,
    "streamed player appearance-byte decode drift");
Check(streamedFields.PlayerVisibleItemEntry(0) == 1000 &&
      streamedFields.PlayerVisibleItemEntry(14) == 1014 &&
      streamedFields.PlayerVisibleItemEntry(18) == 1018,
    "streamed player visible-item stride drift");
Check(streamedFields.PlayerVisibleItemEnchant(0, 0) == 2564 &&
      streamedFields.PlayerVisibleItemEnchant(14, 1) == 1900 &&
      streamedFields.PlayerVisibleItemEnchant(18, 6) == 846 &&
      streamedFields.PlayerVisibleItemEnchant(-1, 0) == 0 &&
      streamedFields.PlayerVisibleItemEnchant(19, 0) == 0 &&
      streamedFields.PlayerVisibleItemEnchant(0, 7) == 0,
    "streamed public visible-item enchant offset/bounds drift");
var streamedPlayer = new WorldEntity
{
    Guid = 0x1234,
    Type = ObjectTypeId.Player,
    Fields = streamedFields,
};
var basePlayerModel = new CreatureModelInfo(@"Character\Human\Male\HumanMale.m2", 1f, 1f,
    [], false, 0, 0, 0, 0, 0, 0, 0, 0, [], "");
Check(CreatureRenderer.TryBuildPlayerModelInfo(streamedPlayer, basePlayerModel,
        entry => (true, new ItemTemplate { Entry = entry, DisplayInfoId = entry + 10_000 }),
        out CreatureModelInfo playerModel) &&
      playerModel.HasExtended && playerModel.IsPlayerAppearance &&
      playerModel.ExtRace == 1 && playerModel.ExtSex == 0 &&
      playerModel.ExtSkin == 2 && playerModel.ExtFace == 3 &&
      playerModel.ExtHairStyle == 4 && playerModel.ExtHairColor == 5 &&
      playerModel.ExtFacialHair == 6 && playerModel.ExtEquipment.Length == 11 &&
      playerModel.ExtEquipment[0] == 11_000 && playerModel.ExtEquipment[9] == 11_018 &&
      playerModel.ExtEquipment[10] == 11_014,
    "remote-player render adapter lost customization or equipment-slot mapping");
// Unsettled templates draw the player provisionally with those slots empty (never
// invisible-until-settled); the settle re-dresses via the appearance key.
Check(CreatureRenderer.TryBuildPlayerModelInfo(streamedPlayer, basePlayerModel,
        _ => (false, null), out CreatureModelInfo provisionalModel) &&
      provisionalModel.HasExtended &&
      provisionalModel.ExtEquipment.All(display => display == 0),
    "remote-player adapter no longer draws provisionally while templates settle");
Check((ushort)Op.SMSG_FORCE_MOVE_ROOT == 0x00E8 &&
      (ushort)Op.CMSG_FORCE_MOVE_ROOT_ACK == 0x00E9 &&
      (ushort)Op.SMSG_FORCE_MOVE_UNROOT == 0x00EA &&
      (ushort)Op.CMSG_FORCE_MOVE_UNROOT_ACK == 0x00EB &&
      (uint)MovementFlags.Root == 0x00001000,
    "build-5875 force-root opcode/flag identities drift");
var rootInfo = new MovementInfo
{
    Flags = (uint)MovementFlags.Root,
    Timestamp = 0x01020304,
    Position = new System.Numerics.Vector3(1.25f, -2.5f, 3.75f),
    Orientation = 1.5f,
    FallTime = 0,
};
var rootAckReader = new PacketReader(WorldSession.BuildMoveRootAckBody(
    0x1122334455667788ul, 0xAABBCCDD, rootInfo));
Check(rootAckReader.ReadU64() == 0x1122334455667788ul &&
      rootAckReader.ReadU32() == 0xAABBCCDD &&
      MovementInfo.Read(rootAckReader) is { } decodedRoot &&
      decodedRoot.Flags == (uint)MovementFlags.Root &&
      decodedRoot.Timestamp == 0x01020304 &&
      decodedRoot.Position == rootInfo.Position &&
      decodedRoot.Orientation == rootInfo.Orientation && rootAckReader.Remaining == 0,
    "force-root acknowledgement body lost guid/counter/rooted MovementInfo");
Check((ushort)Op.CMSG_REPOP_REQUEST == 346 && (ushort)Op.SMSG_RESURRECT_REQUEST == 347 &&
      (ushort)Op.CMSG_RESURRECT_RESPONSE == 348 && (ushort)Op.CMSG_RECLAIM_CORPSE == 466 &&
      (ushort)Op.CMSG_SPIRIT_HEALER_ACTIVATE == 540, "death/resurrection opcodes");
Check(WorldSession.BuildResurrectResponseBody(0x1234, true).SequenceEqual(Convert.FromHexString("341200000000000001")),
      "resurrection response guid/accept body");
Check((ushort)Op.CMSG_BINDER_ACTIVATE == 0x01B5 &&
      (ushort)Op.SMSG_PLAYERBINDERROR == 0x01B6 &&
      (ushort)Op.SMSG_BINDER_CONFIRM == 0x02EB &&
      (ushort)Op.SMSG_BINDPOINTUPDATE == 0x0155 &&
      (ushort)Op.SMSG_PLAYERBOUND == 0x0158, "binder/bind-point opcodes");
Check(WorldSession.BuildBinderBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "binder full guid body");
Check((ushort)Op.SMSG_SHOWTAXINODES == 0x01A9 &&
      (ushort)Op.CMSG_TAXINODE_STATUS_QUERY == 0x01AA &&
      (ushort)Op.CMSG_ACTIVATETAXI == 0x01AD &&
      (ushort)Op.SMSG_ACTIVATETAXIREPLY == 0x01AE, "taxi opcodes");
Check(WorldSession.BuildActivateTaxiBody(0x0102030405060708, 12, 34).SequenceEqual(new byte[]
      { 8,7,6,5,4,3,2,1, 12,0,0,0, 34,0,0,0 }), "taxi activate body");

Check((ushort)Op.CMSG_INITIATE_TRADE == 278 && (ushort)Op.CMSG_BEGIN_TRADE == 279 &&
      (ushort)Op.CMSG_ACCEPT_TRADE == 282 && (ushort)Op.CMSG_SET_TRADE_ITEM == 285 &&
      (ushort)Op.CMSG_SET_TRADE_GOLD == 287 && (ushort)Op.SMSG_TRADE_STATUS == 288 &&
      (ushort)Op.SMSG_TRADE_STATUS_EXTENDED == 289, "trade opcodes");
Check(WorldSession.BuildAcceptTradeBody().SequenceEqual(new byte[] { 1, 0, 0, 0 }),
      "trade accept session marker");
Check(WorldSession.BuildSetTradeItemBody(2, 255, 25).SequenceEqual(new byte[] { 2, 255, 25 }),
      "trade item slot/bag/slot body");
Check(WorldSession.BuildSetTradeGoldBody(0x12345678).SequenceEqual(Convert.FromHexString("78563412")),
      "trade money body");

// GameMenuFrame/logout: both buttons use the same empty request packet; the local quitting bit
// only selects narration and whether completion returns to the roster or exits the process.
Check((ushort)Op.CMSG_LOGOUT_REQUEST == 0x004B &&
      (ushort)Op.SMSG_LOGOUT_RESPONSE == 0x004C &&
      (ushort)Op.SMSG_LOGOUT_COMPLETE == 0x004D &&
      (ushort)Op.CMSG_LOGOUT_CANCEL == 0x004E &&
      (ushort)Op.SMSG_LOGOUT_CANCEL_ACK == 0x004F,
    "build-5875 logout opcode identities drift");
Check(LogoutResponse.Parse(Convert.FromHexString("0000000000")) == new LogoutResponse(0, false) &&
      LogoutResponse.Parse(Convert.FromHexString("0300000001")) == new LogoutResponse(3, true),
    "SMSG_LOGOUT_RESPONSE u32 reason/u8 instant wire shape drift");
Check(LogoutUiLaw.Decide(new LogoutResponse(1, false), quitting: true) == LogoutResponseAction.Refused &&
      LogoutUiLaw.Decide(new LogoutResponse(0, true), quitting: false) == LogoutResponseAction.AwaitCompletion &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: false) == LogoutResponseAction.ShowCampCountdown &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: true) == LogoutResponseAction.ShowQuitCountdown,
    "logout response decision table drift");
Check(LogoutUiLaw.CountdownText(false, 20f) == "20 seconds until logout" &&
      LogoutUiLaw.CountdownText(true, .1f) == "1 second until exit",
    "CAMP/QUIT countdown text drift");

Check(InspectUiLaw.CanInspect(isPlayer: true, isSelf: false, attackable: false,
          distanceSquared: 100f) &&
      !InspectUiLaw.CanInspect(true, false, false, 100.001f) &&
      !InspectUiLaw.CanInspect(true, false, false, float.NaN) &&
      !InspectUiLaw.CanInspect(false, false, false, 0f) &&
      !InspectUiLaw.CanInspect(true, true, false, 0f) &&
      !InspectUiLaw.CanInspect(true, false, true, 0f),
    "inspect player/self/attackable/10-yard gate or preserved NaN rejection drift");
Check(InspectUiLaw.PopupRowEnabled(true, false, false, 99.999f) &&
      !InspectUiLaw.PopupRowEnabled(true, false, false, 100f) &&
      InspectUiLaw.PopupRowEnabled(false, false, false, 10_000f) &&
      InspectUiLaw.PopupRowEnabled(true, true, false, 10_000f) &&
      InspectUiLaw.PopupRowEnabled(true, false, true, 10_000f),
    "inspect UnitPopup strict-distance/default-enabled boundary drift");
Check(MathF.Abs(InspectUiLaw.ClickFacing(.61f, left: true) - .58f) < .0001f &&
      MathF.Abs(InspectUiLaw.ClickFacing(.61f, left: false) - .64f) < .0001f &&
      MathF.Abs(InspectUiLaw.PhysicalTapFacing(.61f, left: true) - .55f) < .0001f &&
      MathF.Abs(InspectUiLaw.PhysicalTapFacing(.61f, left: false) - .67f) < .0001f &&
      MathF.Abs(InspectUiLaw.HeldFacing(0f, left: true, .5f) - MathF.PI * .5f) < .0001f &&
      MathF.Abs(InspectUiLaw.HeldFacing(0f, left: false, .5f) - MathF.PI * 1.5f) < .0001f,
    "inspect click-edge/physical-tap/held facing law drift");
Check(InspectUiLaw.FrameWidth == 384f && InspectUiLaw.FrameHeight == 512f &&
      InspectUiLaw.HitWidth == 354f && InspectUiLaw.HitHeight == 467f &&
      InspectUiLaw.EquipmentSlotCount == 19 && InspectUiLaw.SlotSize == 37f &&
      InspectUiLaw.SlotRingSize == 64f && InspectUiLaw.WeaponRowTop == 385f &&
      InspectUiLaw.PortraitRect == new InspectUiLaw.LogicalRect(7, 6, 60, 60) &&
      InspectUiLaw.ModelRect == new InspectUiLaw.LogicalRect(65, 78, 233, 300) &&
      InspectUiLaw.RotateLeftRect == new InspectUiLaw.LogicalRect(65, 78, 35, 35) &&
      InspectUiLaw.RotateRightRect == new InspectUiLaw.LogicalRect(100, 78, 35, 35) &&
      InspectUiLaw.CloseRect == new InspectUiLaw.LogicalRect(324, 9, 32, 32),
    "inspect frame/hit/portrait/model/slot geometry drift");
InspectBinding targetBinding = InspectBinding.Target;
InspectBinding partyBinding = InspectBinding.Party(2);
Check(targetBinding == new InspectBinding(InspectTokenKind.Target, -1) &&
      partyBinding == new InspectBinding(InspectTokenKind.Party, 2) &&
      InspectUiLaw.RefreshForEvent(targetBinding, false, true) &&
      InspectUiLaw.RefreshForEvent(targetBinding, true, false) &&
      InspectUiLaw.RefreshForEvent(partyBinding, false, true) &&
      InspectUiLaw.RefreshForEvent(partyBinding, true, false),
    "inspect target/party token event re-resolution law drift");
Check(InspectUiLaw.OpenSound == "igCharacterInfoOpen" &&
      InspectUiLaw.CloseSound == "igCharacterInfoClose" &&
      InspectUiLaw.RotateSound == "igInventoryRotateCharacter" &&
      InspectUiLaw.SoundCategory == "ui.inspect" &&
      InspectUiLaw.PhysicalTapSoundCount == 2,
    "inspect open/close/rotation cue identity or physical-tap cardinality drift");
Check(InspectUiLaw.VisibleEnchantTone(0, false) == InspectEnchantTone.Green &&
      InspectUiLaw.VisibleEnchantTone(1, true) == InspectEnchantTone.Red &&
      InspectUiLaw.VisibleEnchantTone(2, false) == InspectEnchantTone.White &&
      InspectUiLaw.VisibleEnchantTone(6, true) == InspectEnchantTone.White &&
      InspectUiLaw.VisibleEnchantsAllowed(0) &&
      !InspectUiLaw.VisibleEnchantsAllowed(0x2000),
    "inspect public enchant color/signable suppression law drift");
Check((ushort)Op.CMSG_INSPECT == 0x0114 && (ushort)Op.SMSG_INSPECT == 0x0115,
    "build-5875 inspect opcode identity drift");
Check(PaperDollUiLaw.EquipmentSlotLabel(2) == "Shoulders" &&
      PaperDollUiLaw.EquipmentSlotLabel(15) == "Main Hand" &&
      PaperDollUiLaw.EquipmentSlotLabel(16) == "Off Hand",
    "inspect empty-slot localized label mapping drift");

string inspectSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Inspect.cs"));
string inspectTargetingSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Targeting.cs"));
string inspectPartySource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.PartyFrames.cs"));
string inspectPopupSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.UnitPopup.cs"));
string inspectCaptureSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
string inspectLiveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
int inspectRequest = inspectSource.IndexOf("private bool RequestInspect", StringComparison.Ordinal);
int inspectRequestClose = inspectSource.IndexOf("CloseInspect(playSound: true);", inspectRequest,
    StringComparison.Ordinal);
int inspectRequestGate = inspectSource.IndexOf("InspectUiLaw.CanInspect", inspectRequest,
    StringComparison.Ordinal);
int inspectPortraitDraw = inspectSource.IndexOf("DrawUnitPortraitImage(dl, player,",
    StringComparison.Ordinal);
int inspectBackgroundDraw = inspectSource.IndexOf("DrawPaperDollBackground(dl, p, s);",
    StringComparison.Ordinal);
int inspectRingDraw = inspectSource.IndexOf("dl.AddImage((nint)ring,", StringComparison.Ordinal);
int inspectHighlightDraw = inspectSource.IndexOf("AdditiveHandle", inspectRingDraw,
    StringComparison.Ordinal);
Check(inspectRequest >= 0 && inspectRequestClose > inspectRequest &&
      inspectRequestGate > inspectRequestClose &&
      !inspectSource.Contains("_inspectOpen && _inspectGuid == guid", StringComparison.Ordinal),
    "inspect repeat/invalid request must hide before gate and never same-guid short-circuit");
Check(inspectPortraitDraw >= 0 && inspectBackgroundDraw > inspectPortraitDraw,
    "inspect square portrait must paint before the page background's round aperture");
Check(inspectSource.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[11], s)",
          StringComparison.Ordinal) &&
      inspectRingDraw >= 0 && inspectHighlightDraw > inspectRingDraw &&
      inspectSource.Contains("enabled: false", StringComparison.Ordinal) &&
      inspectSource.Contains("ImGui.IsItemActivated()", StringComparison.Ordinal) &&
      inspectSource.Contains("ImGui.IsItemDeactivated()", StringComparison.Ordinal) &&
      inspectSource.Contains("PaperDollUiLaw.EquipmentSlotLabel(slot)", StringComparison.Ordinal) &&
      inspectSource.Contains("PlayerVisibleItemEnchant(slot, enchantSlot)", StringComparison.Ordinal) &&
      inspectSource.Contains("OfferPreparedItemTooltip(tooltipOwner, body, max);",
          StringComparison.Ordinal) &&
      inspectSource.Contains("ImGui.SetNextWindowPos(tooltipPosition, ImGuiCond.Always)",
          StringComparison.Ordinal),
    "inspect selected-tab/rotation/slot layer/label/enchant/tooltip production wiring drift");
Check(inspectTargetingSource.Contains(
          "Settings.Controls.WorldPlayerContextMenus", StringComparison.Ordinal) &&
      inspectTargetingSource.Contains(
          "OpenUnitPopup(picked, which, click.Position, InspectBinding.Target);",
          StringComparison.Ordinal) &&
      inspectPartySource.Contains("InspectBinding.Party(hoveredIndex));",
          StringComparison.Ordinal) &&
      inspectPartySource.Contains("_partyRosterRevision++;", StringComparison.Ordinal) &&
      inspectPopupSource.Contains("InspectUiLaw.PopupRowEnabled", StringComparison.Ordinal) &&
      inspectPopupSource.Contains("RequestInspect(guid, _unitPopupInspectBinding);",
          StringComparison.Ordinal),
    "inspect target/party origin seam or strict popup gate production wiring drift");
Check(inspectCaptureSource.Contains("inspect-frame-requires-observed-runtime-state",
          StringComparison.Ordinal) &&
      inspectCaptureSource.Contains("captureNetworkMutation", StringComparison.Ordinal) &&
      inspectCaptureSource.Contains("PLAYER_VISIBLE_ITEM", StringComparison.Ordinal) &&
      inspectLiveRunSource.Contains("ValidateInspectFrameCapture", StringComparison.Ordinal) &&
      inspectLiveRunSource.Contains("case \"inspect\":", StringComparison.Ordinal) &&
      inspectLiveRunSource.Contains("Op.CMSG_INSPECT", StringComparison.Ordinal),
    "inspect observational capture/state/sound/wire assertion surface drift");

if (args.Contains("--inspect-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: InspectFrame PASS");
    return;
}

Check(SkillFrameUiLaw.BindingCommand == "TOGGLECHARACTER1" &&
      SkillFrameUiLaw.BindingLabel == "Toggle Skill Pane" &&
      SkillFrameUiLaw.SkillsTab == 3 && SkillFrameUiLaw.VisibleRows == 12,
    "SkillFrame direct binding identity/tab/visible-row contract drift");
Check(SkillFrameUiLaw.ResolveDirectToggle(false, 0) ==
          SkillFrameUiLaw.ToggleAction.OpenSkills &&
      SkillFrameUiLaw.ResolveDirectToggle(true, 3) ==
          SkillFrameUiLaw.ToggleAction.CloseSkills &&
      SkillFrameUiLaw.ResolveDirectToggle(true, 0) ==
          SkillFrameUiLaw.ToggleAction.SwitchToSkills,
    "SkillFrame K open/close/switch action drift");
Check(SkillFrameUiLaw.FiresDirectBinding(true, false, false, false, false, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(false, false, false, false, false, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, true, false, false, false, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, true, false, false, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, false, true, false, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, false, false, true, false,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, false, false, false, true,
          false, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, false, false, false, false,
          true, true) &&
      !SkillFrameUiLaw.FiresDirectBinding(true, false, false, false, false, false,
          false, false),
    "SkillFrame exact bare K edge/repeat/text-capture/world gate drift");
// `wasDown` remains true while the key is held even if a rejected modifier is released.
Check(!SkillFrameUiLaw.FiresDirectBinding(true, true, false, false, false, false,
          false, true),
    "SkillFrame modifier-release while K held synthesized a forbidden second edge");
Check(SkillFrameUiLaw.ListRect == new SkillFrameUiLaw.LogicalRect(22, 79, 296, 216) &&
      SkillFrameUiLaw.WheelCatcherRect ==
          new SkillFrameUiLaw.LogicalRect(20, 76, 290, 222) &&
      SkillFrameUiLaw.CollapseFrameRect ==
          new SkillFrameUiLaw.LogicalRect(70, 49, 54, 32) &&
      SkillFrameUiLaw.CollapseLeftRect ==
          new SkillFrameUiLaw.LogicalRect(70, 43, 8, 32) &&
      SkillFrameUiLaw.CollapseMiddleRect ==
          new SkillFrameUiLaw.LogicalRect(78, 43, 38, 32) &&
      SkillFrameUiLaw.CollapseRightRect ==
          new SkillFrameUiLaw.LogicalRect(116, 43, 8, 32) &&
      SkillFrameUiLaw.CollapseButtonRect ==
          new SkillFrameUiLaw.LogicalRect(75, 51, 40, 22) &&
      SkillFrameUiLaw.CollapseIconRect ==
          new SkillFrameUiLaw.LogicalRect(78, 54, 16, 16) &&
      SkillFrameUiLaw.CollapseLabelFont == "GameFontHighlight" &&
      SkillFrameUiLaw.CollapseLabelOffsetX == 25 &&
      SkillFrameUiLaw.CollapseTextMin(new Vector2(75, 51), new Vector2(40, 22), 12, 1) ==
          new Vector2(100, 56),
    "SkillFrame list/wheel/collapse-all frozen geometry drift");
Check(SkillFrameUiLaw.ScrollSliderRect ==
          new SkillFrameUiLaw.LogicalRect(324, 95, 16, 184) &&
      SkillFrameUiLaw.ScrollUpRect ==
          new SkillFrameUiLaw.LogicalRect(324, 79, 16, 16) &&
      SkillFrameUiLaw.ScrollDownRect ==
          new SkillFrameUiLaw.LogicalRect(324, 279, 16, 16) &&
      SkillFrameUiLaw.ScrollThumbTravel == 168 &&
      SkillFrameUiLaw.ScrollArrowRows == 6 &&
      SkillFrameUiLaw.MaximumScroll(18) == 6 &&
      SkillFrameUiLaw.ClampScroll(99, 18) == 6 &&
      SkillFrameUiLaw.WheelScroll(3, 18, 1) == 2 &&
      SkillFrameUiLaw.WheelScroll(3, 18, -1) == 4 &&
      SkillFrameUiLaw.ArrowScroll(0, 30, upward: false) == 6 &&
      SkillFrameUiLaw.ArrowScroll(6, 30, upward: true) == 0 &&
      SkillFrameUiLaw.ScrollThumbY(0, 6) == 95 &&
      SkillFrameUiLaw.ScrollThumbY(6, 6) == 263 &&
      SkillFrameUiLaw.ScrollThumbRect(6, 6) ==
          new SkillFrameUiLaw.LogicalRect(324, 263, 16, 16) &&
      SkillFrameUiLaw.ScrollControlUvMin == new Vector2(.25f, .25f) &&
      SkillFrameUiLaw.ScrollControlUvMax == new Vector2(.75f, .75f),
    "SkillFrame inherited scrollbar geometry/step/clamp/thumb law drift");
SkillFrameUiLaw.LogicalRect firstSkillHit = SkillFrameUiLaw.SkillRowHitRect(0);
SkillFrameUiLaw.LogicalRect lastSkillHit = SkillFrameUiLaw.SkillRowHitRect(11);
Check(firstSkillHit == new SkillFrameUiLaw.LogicalRect(33, 70.5f, 281, 32) &&
      lastSkillHit == new SkillFrameUiLaw.LogicalRect(33, 268.5f, 281, 32) &&
      firstSkillHit.Y + firstSkillHit.Height >
          SkillFrameUiLaw.SkillRowHitRect(1).Y &&
      lastSkillHit.Y + lastSkillHit.Height < SkillFrameUiLaw.DividerLeftRect.Y,
    "SkillFrame exact 281x32 overlap precedence or divider clearance drift");
Check(SkillFrameUiLaw.DividerLeftRect ==
          new SkillFrameUiLaw.LogicalRect(15, 305, 256, 16) &&
      SkillFrameUiLaw.DividerRightRect ==
          new SkillFrameUiLaw.LogicalRect(271, 305, 75, 16) &&
      SkillFrameUiLaw.DetailBarRect ==
          new SkillFrameUiLaw.LogicalRect(38, 325, 271, 15) &&
      SkillFrameUiLaw.DetailBorderRect ==
          new SkillFrameUiLaw.LogicalRect(33, 316.5f, 281, 32) &&
      SkillFrameUiLaw.DetailDescriptionRect ==
          new SkillFrameUiLaw.LogicalRect(36, 350, 275, 0) &&
      SkillFrameUiLaw.DetailUnlearnRect ==
          new SkillFrameUiLaw.LogicalRect(312, 317.5f, 32, 32) &&
      SkillFrameUiLaw.UnlearnTooltipSeat(new Vector2(100, 200), new Vector2(32, 32)) ==
          new SkillFrameUiLaw.TooltipSeat(new Vector2(132, 200), Vector2.UnitY) &&
      SkillFrameUiLaw.BarFor(0, true) == SkillFrameUiLaw.BarPresentation.Barless &&
      SkillFrameUiLaw.BarFor(1, false) ==
          SkillFrameUiLaw.BarPresentation.Proficiency &&
      SkillFrameUiLaw.BarFor(75, false) == SkillFrameUiLaw.BarPresentation.Progress,
    "SkillFrame divider/detail or max-zero/proficiency/progress presentation drift");
SkillFrameUiLaw.ScreenRect skillPopup = SkillFrameUiLaw.PopupLayout(new Vector2(1024, 768), 1);
Check(skillPopup.Min == new Vector2(352, 128) &&
      skillPopup.Size == new Vector2(320, 72) &&
      SkillFrameUiLaw.PopupMessageCenter(new Vector2(352, 128), 1, 12) ==
          new Vector2(512, 150) &&
      SkillFrameUiLaw.PopupMessageMin(new Vector2(352, 128), 1, 100) ==
          new Vector2(462, 144) &&
      SkillFrameUiLaw.Clip(new Vector2(10, 20), 384, 512, 2) ==
          new Vector4(10, 20, 778, 1044) &&
      SkillFrameUiLaw.PopupClip(new Vector2(352, 128), 1) ==
          new Vector4(352, 128, 672, 200),
    "SkillFrame rule-owned StaticPopup screen placement drift");
Check(SkillFrameUiLaw.PopupRect ==
          new SkillFrameUiLaw.LogicalRect(0, 128, 320, 72) &&
      SkillFrameUiLaw.PopupTextRect ==
          new SkillFrameUiLaw.LogicalRect(15, 16, 290, 12) &&
      SkillFrameUiLaw.PopupAcceptRect ==
          new SkillFrameUiLaw.LogicalRect(26, 36, 128, 20) &&
      SkillFrameUiLaw.PopupCancelRect ==
          new SkillFrameUiLaw.LogicalRect(167, 36, 128, 20) &&
      SkillFrameUiLaw.UnlearnTimeoutSeconds == 60 &&
      SkillFrameUiLaw.UnlearnQuestionFormat == "Do you want to unlearn {0}?" &&
      SkillFrameUiLaw.UnlearnButtonText == "Unlearn" &&
      SkillFrameUiLaw.CancelButtonText == "Cancel" &&
      SkillFrameUiLaw.UnlearnTooltip == "Unlearn this profession" &&
      SkillFrameUiLaw.PopupOpenSound == "igMainMenuOpen" &&
      SkillFrameUiLaw.PopupCloseSound == "igMainMenuClose" &&
      SkillFrameUiLaw.DirectTabSound == "igCharacterInfoTab" &&
      SkillFrameUiLaw.ScrollButtonSound == "UChatScrollButton",
    "SkillFrame unlearn StaticPopup/copy/timeout or sound identity drift");
Check(SkillFrameUiLaw.InsetUnlearnHitRect(
          new SkillFrameUiLaw.LogicalRect(100, 200, 32, 32)) ==
          new SkillFrameUiLaw.LogicalRect(109, 193, 16, 29),
    "SkillFrame unlearn HitRectInsets law drift");
Check((ushort)Op.CMSG_UNLEARN_SKILL == SkillFrameUiLaw.UnlearnOpcode &&
      WorldSession.BuildUnlearnSkillBody(0x12345678)
          .SequenceEqual(Convert.FromHexString("78563412")),
    "SkillFrame CMSG_UNLEARN_SKILL 0x0202 or exact u32 little-endian body drift");
uint[] primaryProfessionLines = [164, 165, 171, 197, 202, 333, 393];
uint[] protectedSkillLines = [8, 43, 95, 98, 129, 185, 356, 762];
Check(primaryProfessionLines.All(id => spellbookSkills.Abandonable(id, 1, 1)) &&
      protectedSkillLines.All(id => !spellbookSkills.Abandonable(id, 1, 1)) &&
      !spellbookSkills.Abandonable(999999, 1, 1) &&
      !spellbookSkills.Abandonable(164, 0, 1) &&
      !spellbookSkills.Abandonable(164, 1, 0) &&
      SkillLineCatalog.UnlearnableFlag == 0x20,
    "SkillRaceClassInfo 0x20 primary-profession allowlist/conservative gate drift");
string[] skillAssets =
[
    @"Interface\QuestFrame\UI-QuestLogSortTab-Left.blp",
    @"Interface\QuestFrame\UI-QuestLogSortTab-Middle.blp",
    @"Interface\QuestFrame\UI-QuestLogSortTab-Right.blp",
    @"Interface\Buttons\UI-MinusButton-Up.blp",
    @"Interface\Buttons\UI-PlusButton-Up.blp",
    @"Interface\Buttons\UI-PlusButton-Hilight.blp",
    @"Interface\Buttons\UI-ScrollBar-ScrollUpButton-Up.blp",
    @"Interface\Buttons\UI-ScrollBar-ScrollDownButton-Up.blp",
    @"Interface\Buttons\UI-ScrollBar-Knob.blp",
    @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar.blp",
    @"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar.blp",
    @"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder.blp",
    @"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorderHighlight.blp",
    @"Interface\Buttons\CancelButton-Up.blp",
    @"Interface\Buttons\CancelButton-Down.blp",
    @"Interface\Buttons\CancelButton-Highlight.blp",
];
Check(skillAssets.All(path => spellbookMpq.ReadFile(path) is not null),
    "SkillFrame newly ported collapse/scroll/divider/bar/unlearn asset closure missing");

string skillCharacterSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.CharacterPage.cs"));
string skillRuntimeSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.SkillFrame.cs"));
string skillBindingsSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Bindings.cs"));
string skillSettingsSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Settings.cs"));
string skillCaptureSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
string skillLiveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
Check(skillBindingsSource.Contains("GameBinding.OpenSkills, SkillFrameUiLaw.BindingLabel, Key.K",
          StringComparison.Ordinal) &&
      skillCharacterSource.Contains("SkillFrameUiLaw.FiresDirectBinding", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("SkillFrameUiLaw.ResolveDirectToggle", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("PlayUiSound(SkillFrameUiLaw.DirectTabSound, \"ui.skill-frame\")",
          StringComparison.Ordinal),
    "SkillFrame direct K production binding/sound seam drift");
int skillOverlapSearch = skillCharacterSource.IndexOf(
    "for (int visible = Math.Min(11, rows.Count - 1 - _skillScroll);",
    StringComparison.Ordinal);
Check(skillOverlapSearch >= 0 &&
      skillCharacterSource.IndexOf("SkillFrameUiLaw.SkillRowHitRect(visible)",
          skillOverlapSearch, StringComparison.Ordinal) >= 0 &&
      skillCharacterSource.Contains("SkillFrameUiLaw.WheelCatcherRect", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("new Vector2(5, 8.5f)", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("SkillFrameUiLaw.DividerLeftRect", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("SkillFrameUiLaw.DetailBarRect", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("SkillFrameUiLaw.DetailDescriptionRect", StringComparison.Ordinal) &&
      !skillCharacterSource.Contains("new Vector2(65.5f, 315.5f)", StringComparison.Ordinal) &&
      !skillCharacterSource.Contains("new Vector2(211, 15)", StringComparison.Ordinal) &&
      skillCharacterSource.Contains("BarPresentation.Barless", StringComparison.Ordinal),
    "SkillFrame overlap precedence/wheel catcher/border/divider/max-zero production drift");
Check(skillRuntimeSource.Contains("SkillIsCurrentlyAbandonable", StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("_selectedSkill != confirmation.SkillId",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("net.UnlearnSkill(confirmation.SkillId);",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("PLAYER_SKILL_INFO", StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("PlayUiSound(SkillFrameUiLaw.ScrollButtonSound",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.DetailUnlearnRect", StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.UnlearnTooltipSeat", StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.PopupLayout(display, s)",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.ScrollThumbRect",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.PopupMessageCenter",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("GameText.EmPixels(SkillFrameUiLaw.CollapseLabelFont",
          StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.CollapseLabelOffsetX",
          StringComparison.Ordinal) &&
      !skillRuntimeSource.Contains("new Vector2", StringComparison.Ordinal) &&
      !skillRuntimeSource.Contains("Vector4 panelClip = new(", StringComparison.Ordinal) &&
      !skillRuntimeSource.Contains("Vector4 popupClip = new(", StringComparison.Ordinal) &&
      skillRuntimeSource.Contains("SkillFrameUiLaw.PopupClip", StringComparison.Ordinal) &&
      skillSettingsSource.Contains("TryDismissSkillUnlearnConfirmationOnEscape()",
          StringComparison.Ordinal),
    "SkillFrame authoritative unlearn revalidation/no-optimistic-state/escape/sound seam drift");
Check(skillCaptureSource.Contains("skill-frame-requires-observed-player-skill-state",
          StringComparison.Ordinal) &&
      skillCaptureSource.Contains("stateSource\"] = \"player-skill-fields\"",
          StringComparison.Ordinal) &&
      skillCaptureSource.Contains("captureNetworkMutation\"] = false",
          StringComparison.Ordinal) &&
      skillLiveRunSource.Contains("ValidateSkillFrameCapture", StringComparison.Ordinal) &&
      skillLiveRunSource.Contains("case \"skill-frame-capture-assert\"",
          StringComparison.Ordinal),
    "SkillFrame observational-only capture/scenario/runner assertion surface drift");

if (args.Contains("--skill-frame-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: SkillFrame PASS");
    return;
}

Check(PartyFrameUiLaw.MemberY(0) == 128f && PartyFrameUiLaw.MemberY(1) == 191f &&
      PartyFrameUiLaw.MemberY(3) == 317f && PartyFrameUiLaw.FrameWidth == 128f &&
      PartyFrameUiLaw.FrameHeight == 53f,
    "party member frame origin/63-pixel petless cascade drift");

static byte[] PartyRosterFixture(int memberCount, byte ownFlags = 0,
    Func<int, byte>? memberFlags = null)
{
    var writer = new PacketWriter();
    writer.WriteU8(memberCount > 5 ? (byte)1 : (byte)0);
    writer.WriteU8(ownFlags);
    writer.WriteU32((uint)memberCount);
    for (int i = 0; i < memberCount; i++)
    {
        writer.WriteCString($"Member{i + 1}");
        writer.WriteU64(0x1000ul + (ulong)i);
        writer.WriteU8(PartyFrameUiLaw.Online);
        writer.WriteU8(memberFlags?.Invoke(i) ?? ownFlags);
    }
    writer.WriteU64(memberCount == 0 ? 0 : 0x1000ul);
    if (memberCount > 0)
    {
        writer.WriteU8(2);
        writer.WriteU64(0x1000ul + (ulong)(memberCount - 1));
        writer.WriteU8(2);
        writer.WriteU8(0);
    }
    return writer.ToArray();
}

static byte[] PartyStatsFixture(ulong guid, uint mask = 0x7f)
{
    var writer = new PacketWriter();
    writer.WritePackedGuid(guid);
    writer.WriteU32(mask);
    if ((mask & 0x01) != 0) writer.WriteU8(PartyFrameUiLaw.Online);
    if ((mask & 0x02) != 0) writer.WriteU16(15);
    if ((mask & 0x04) != 0) writer.WriteU16(100);
    if ((mask & 0x08) != 0) writer.WriteU8(0);
    if ((mask & 0x10) != 0) writer.WriteU16(70);
    if ((mask & 0x20) != 0) writer.WriteU16(100);
    if ((mask & 0x40) != 0) writer.WriteU16(60);
    return writer.ToArray();
}

for (int count = 0; count <= 5; count++)
{
    PartyRosterWire parsed = PartyFramePacketLaw.ParseRoster(PartyRosterFixture(count));
    Check(parsed.Members.Length == count && parsed.LeaderGuid == (count == 0 ? 0 : 0x1000ul) &&
          parsed.LootMethod == (count == 0 ? 0 : 2),
        $"party roster {count}-member parse/tail law drift");
    int[] compact = PartyFrameUiLaw.CompactRosterIndices(parsed.OwnFlags,
        parsed.Members.Select(member => member.MemberFlags).ToArray());
    Check(compact.Length == Math.Min(count, PartyFrameUiLaw.MemberCount),
        $"party roster {count}-member four-slot cap drift");
}
byte[] oneMemberRoster = PartyRosterFixture(1, 0x41);
for (int length = 0; length < oneMemberRoster.Length; length++)
{
    bool rejected = false;
    try { _ = PartyFramePacketLaw.ParseRoster(oneMemberRoster[..length]); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
    Check(rejected, $"party roster truncation accepted at {length}/{oneMemberRoster.Length}");
}
bool rosterTrailingRejected = false;
try { _ = PartyFramePacketLaw.ParseRoster([.. oneMemberRoster, 0xff]); }
catch (InvalidDataException) { rosterTrailingRejected = true; }
Check(rosterTrailingRejected, "party roster trailing byte accepted");
Check(PartyFrameUiLaw.IsLeaveRoster(
          PartyFramePacketLaw.ParseRoster(PartyRosterFixture(0))) &&
      !PartyFrameUiLaw.IsLeaveRoster(
          PartyFramePacketLaw.ParseRoster(PartyRosterFixture(1))) &&
      PartyFrameUiLaw.IsLeaveRoster(new PartyRosterWire(1, 0x41,
          [new PartyRosterWireMember("StillParsed", 0x1234, 1, 0x41)],
          0, 2, 0x1234)),
    "party leader-zero GROUP_LIST leave edge drift");

byte[] raidRosterBody = PartyRosterFixture(6, 0x41,
    i => new byte[] { 0x41, 0x01, 0x41, 0x42, 0xc1, 0x02 }[i]);
PartyRosterWire raidRoster = PartyFramePacketLaw.ParseRoster(raidRosterBody);
int[] raidCompact = PartyFrameUiLaw.CompactRosterIndices(raidRoster.OwnFlags,
    raidRoster.Members.Select(member => member.MemberFlags).ToArray());
Check(raidCompact.SequenceEqual(new[] { 0, 2, 4 }),
    "frozen Party subgroup view must preserve its wider 0x7f comparison quirk");

byte[] statsBody = PartyStatsFixture(0x1234);
PartyMemberStatsWire parsedStats = PartyFramePacketLaw.ParseMemberStats(statsBody);
Check(parsedStats.Guid == 0x1234 && parsedStats.Snapshot ==
      new PartyMemberStatsSnapshot(PartyFrameUiLaw.Online, 15, 100, 0, 70, 100, 60),
    "party full stats mask/body parse drift");
for (int length = 0; length < statsBody.Length; length++)
{
    bool rejected = false;
    try { _ = PartyFramePacketLaw.ParseMemberStats(statsBody[..length]); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
    Check(rejected, $"party stats truncation accepted at {length}/{statsBody.Length}");
}
bool statsTrailingRejected = false;
try { _ = PartyFramePacketLaw.ParseMemberStats([.. statsBody, 0xff]); }
catch (InvalidDataException) { statsTrailingRejected = true; }
Check(statsTrailingRejected, "party stats trailing byte accepted");
var inviteWriter = new PacketWriter();
inviteWriter.WriteCString("Clinical Inviter");
byte[] inviteBody = inviteWriter.ToArray();
Check(PartyFramePacketLaw.ParseInvite(inviteBody) == "Clinical Inviter",
    "party invitation CString parse drift");
for (int length = 0; length < inviteBody.Length; length++)
{
    bool rejected = false;
    try { _ = PartyFramePacketLaw.ParseInvite(inviteBody[..length]); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
    Check(rejected, $"party invitation truncation accepted at {length}/{inviteBody.Length}");
}
bool inviteTrailingRejected = false;
try { _ = PartyFramePacketLaw.ParseInvite([.. inviteBody, 0xff]); }
catch (InvalidDataException) { inviteTrailingRejected = true; }
Check(inviteTrailingRejected, "party invitation trailing byte accepted");

var previousPartyStats = new PartyMemberStatsSnapshot(1, 90, 100, 0, 40, 100, 60);
var partialPartyStats = new PartyMemberStatsSnapshot(Health: 25);
Check(PartyFrameUiLaw.MergeStats(previousPartyStats, partialPartyStats, fullSnapshot: false) ==
          new PartyMemberStatsSnapshot(1, 25, 100, 0, 40, 100, 60) &&
      PartyFrameUiLaw.MergeStats(previousPartyStats, partialPartyStats, fullSnapshot: true) ==
          new PartyMemberStatsSnapshot(Health: 25),
    "party delta merge or FULL omission-clear snapshot law drift");
Check(PartyFrameUiLaw.EffectiveStatus(0, PartyFrameUiLaw.Online) == 0 &&
      PartyFrameUiLaw.EffectiveStatus(PartyFrameUiLaw.Online, 0) == PartyFrameUiLaw.Online,
    "GROUP_LIST roster status must remain authoritative over delayed stats");
Check(PartyFrameUiLaw.MergedPvp(PartyFrameUiLaw.Pvp, 0) &&
      PartyFrameUiLaw.MergedPvp(0, PartyFrameUiLaw.UnitFlagPvp) &&
      !PartyFrameUiLaw.MergedPvp(0, 0) &&
      !PartyFrameUiLaw.MergedPvp(0, null),
    "party streamed UNIT_FIELD_FLAGS PvP OR roster-PvP merged-view drift");
Check(PartyFrameUiLaw.PvpFaction(2, 1) == "Horde" &&
      PartyFrameUiLaw.PvpFaction(null, 1) == "Alliance" &&
      PartyFrameUiLaw.PvpFaction(null, 8) == "Horde" &&
      PartyFrameUiLaw.PvpFaction(null, null) is null &&
      PartyFrameUiLaw.PvpFaction(0, 0) is null,
    "party faction resolution must not invent Alliance while descriptors are unavailable");

Check(MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(0f) - 1f) < .0001f &&
      MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(.5f) - 127f / 255f) < .0001f &&
      MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(1f) - 1f) < .0001f,
    "party portrait low-health triangle drift");
float partySlot0 = PartyFrameUiLaw.AdvanceLowHealthTimer(0, exists: true,
    connected: true, lowLivingHealth: true, dt: .25f);
float partySlot1 = PartyFrameUiLaw.AdvanceLowHealthTimer(0, exists: true,
    connected: true, lowLivingHealth: true, dt: .1f);
Check(MathF.Abs(partySlot0 - .25f) < .0001f && MathF.Abs(partySlot1 - .1f) < .0001f &&
      PartyFrameUiLaw.AdvanceLowHealthTimer(partySlot0, exists: true,
          connected: false, lowLivingHealth: true, dt: .2f) == partySlot0 &&
      PartyFrameUiLaw.AdvanceLowHealthTimer(partySlot0, exists: false,
          connected: true, lowLivingHealth: true, dt: .2f) == partySlot0 &&
      PartyFrameUiLaw.AdvanceLowHealthTimer(partySlot0, exists: true,
          connected: true, lowLivingHealth: false, dt: .2f) == 0f &&
      PartyFrameUiLaw.AdvanceLowHealthTimer(.9f, exists: true,
          connected: true, lowLivingHealth: true, dt: .2f) < .101f,
    "party frame-slot-local pulse pause/advance/reset/modulo law drift");
Check(MathF.Abs(PartyFrameUiLaw.AdvanceLowHealthTimer(.1f, exists: true,
          connected: true, lowLivingHealth: true, dt: .6f) - .7f) < .0001f,
    "party flashTimer must advance full unclamped frame elapsed");

Check(PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Left) ==
          PartyPointerAction.Target &&
      PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Right) ==
          PartyPointerAction.OpenPartyMenu &&
      PartyFrameUiLaw.ReleaseAction(1, -1, PartyPointerButton.Left) ==
          PartyPointerAction.None &&
      PartyFrameUiLaw.ReleaseAction(1, 2, PartyPointerButton.Right) ==
          PartyPointerAction.None &&
      PartyFrameUiLaw.ReleaseAction(1, 1, PartyPointerButton.Right) ==
          PartyPointerAction.OpenPartyMenu,
    "party ButtonUp fixed-slot/release-outside/rebind-current-occupant click law drift");
Check(PartyFrameUiLaw.InviteButtonPushed(held: true, hovered: true) &&
      !PartyFrameUiLaw.InviteButtonPushed(held: true, hovered: false) &&
      !PartyFrameUiLaw.InviteButtonPushed(held: false, hovered: true) &&
      PartyFrameUiLaw.InviteButtonPushed(held: false, hovered: false, pushedState: true),
    "party invite Button pressed/drag-off/explicit-pushed state drift");
Check(PartyFrameUiLaw.PlayerLevelLine(0, null, null) == "Level 0 (Player)" &&
      PartyFrameUiLaw.PlayerLevelLine(0, "Orc", "Warrior") ==
          "Level 0 Orc Warrior (Player)" &&
      PartyFrameUiLaw.PlayerLevelLine(60, "Orc", "Warrior") ==
          "Level 60 Orc Warrior (Player)" &&
      PartyFrameUiLaw.PlayerLevelLine(60, "Orc", "Warrior", dead: true) ==
          "Level 60 Corpse (Player)" &&
      PartyFrameUiLaw.TooltipNameColor == new Vector4(0, .6f, .1f, 1) &&
      PartyFrameUiLaw.Tooltip("Member", 60, "Orc", "Warrior", false, true, 15, 100) ==
          new PartyTooltipView("Member", "Level 60 Orc Warrior (Player)", "PvP", 15, 100) &&
      PartyFrameUiLaw.Tooltip("Member", 60, "Orc", "Warrior", false, false, 15, 100).PvpLine
          is null &&
      PartyFrameUiLaw.TooltipFadeAlpha(0) == 1f &&
      MathF.Abs(PartyFrameUiLaw.TooltipFadeAlpha(.25) - .5f) < .0001f &&
      PartyFrameUiLaw.TooltipFadeAlpha(.5) == 0f,
    "party SetUnit level/corpse/PvP/reaction/fade tooltip law drift");
Check(PartyFrameUiLaw.TooltipHealth(false, 75, 100) ==
          new PartyTooltipHealthState(false, 0, 0) &&
      PartyFrameUiLaw.TooltipHealth(true, 0, 0) ==
          new PartyTooltipHealthState(true, 1, 0) &&
      PartyFrameUiLaw.TooltipHealth(true, 150, 100) ==
          new PartyTooltipHealthState(true, 100, 100) &&
      PartyFrameUiLaw.MemberHealth(true, 75, 0) ==
          new PartyTooltipHealthState(true, 0, 0) &&
      PartyFrameUiLaw.MemberHealth(false, 75, 100) ==
          new PartyTooltipHealthState(true, 1, 1) &&
      !PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: true, fading: false) &&
      PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: true, fading: true) &&
      PartyFrameUiLaw.BeginTooltipSnapshot(1, 2, hasSnapshot: true, fading: false) &&
      PartyFrameUiLaw.BeginTooltipSnapshot(1, 1, hasSnapshot: false, fading: false) &&
      !PartyFrameUiLaw.BeginTooltipSnapshot(1, -1, hasSnapshot: true, fading: true),
    "party fixed-slot SetUnit snapshot/live-bar/member-health law drift");
PartyTooltipLayout partyTooltipLayout = PartyFrameUiLaw.TooltipLayout(
    [50f, 80f, 30f], [14f, 12f, 12f]);
PartyTooltipLayout partyNarrowTooltipLayout = PartyFrameUiLaw.TooltipLayout([10f], [14f]);
Check(partyTooltipLayout.Width == 100f && partyTooltipLayout.Height == 62f &&
      partyTooltipLayout.RowTops.SequenceEqual([10f, 26f, 40f]) &&
      partyNarrowTooltipLayout.Width == 30f && partyNarrowTooltipLayout.Height == 34f &&
      partyNarrowTooltipLayout.RowTops.SequenceEqual([10f]),
    "party SetUnit header/body font-row/gap/no-minimum-width layout drift");
Check(PartyFrameUiLaw.TooltipRightOffset(false, false) == -13f &&
      PartyFrameUiLaw.TooltipRightOffset(false, true) == -58f &&
      PartyFrameUiLaw.TooltipRightOffset(true, false) == -103f &&
      PartyFrameUiLaw.TooltipRightOffset(true, true) == -103f &&
      PartyFrameUiLaw.TooltipBottomOffset(false, false, false, false) == 70f &&
      PartyFrameUiLaw.TooltipBottomOffset(true, true, false, false) == 97f &&
      PartyFrameUiLaw.TooltipBottomOffset(true, true, true, false) == 120f &&
      PartyFrameUiLaw.TooltipBottomOffset(true, false, false, true) == 106f,
    "party UIParent-managed GameTooltip default anchor drift");
Check(PartyFrameUiLaw.PreservePartyAcrossWorldEnter(socketSessionAlive: true) &&
      !PartyFrameUiLaw.PreservePartyAcrossWorldEnter(socketSessionAlive: false),
    "party roster/invite same-session worldport preservation law drift");

Check(PartyFrameUiLaw.PopupWidth == 320f && PartyFrameUiLaw.PopupTextWidth == 290f &&
      PartyFrameUiLaw.PopupHeight(12) == 72f &&
      PartyFrameUiLaw.PopupButtonTop(12) == 36f &&
      PartyFrameUiLaw.PopupButtonOneX == 26f && PartyFrameUiLaw.PopupButtonTwoX == 167f,
    "PARTY_INVITE ordinary StaticPopup geometry/dynamic-height drift");
StaticPopupCoordinatorLaw.Definition partyInviteDefinition =
    PartyFrameUiLaw.PartyInvitePopupDefinition;
Check(partyInviteDefinition.Type == "PARTY_INVITE" && partyInviteDefinition.WhileDead &&
      partyInviteDefinition.HideOnEscape && partyInviteDefinition.Cancels is null &&
      partyInviteDefinition.HasAccept && partyInviteDefinition.HasCancel &&
      partyInviteDefinition.HasOnShow && partyInviteDefinition.HasOnHide &&
      !partyInviteDefinition.HasOnUpdate && !partyInviteDefinition.HasEditBox &&
      !partyInviteDefinition.UsesTimeoutText && !partyInviteDefinition.UsesDelayText &&
      partyInviteDefinition.TimeoutSeconds == 60 &&
      partyInviteDefinition.StartDelaySeconds is null &&
      partyInviteDefinition.EntrySound == "igPlayerInvite",
    "PARTY_INVITE shared StaticPopup definition drift");
StaticPopupCoordinatorLaw.Plan partyInviteShowPlan = StaticPopupCoordinatorLaw.Show(
    StaticPopupCoordinatorLaw.Slots.Empty, partyInviteDefinition,
    playerDeadOrGhost: true, dataToken: "Clinical Inviter");
var partyInviteVisible = PartyFrameUiLaw.PartyInvitePopup(partyInviteShowPlan.Slots);
Check(partyInviteShowPlan.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
      partyInviteVisible is { Slot: 1 } &&
      partyInviteVisible.Value.Instance.DataToken == "Clinical Inviter" &&
      partyInviteVisible.Value.Instance.TimeLeft == 60 &&
      PartyFrameUiLaw.PartyInvitePopup(new(
          null, partyInviteVisible.Value.Instance))?.Slot == 2 &&
      PartyFrameUiLaw.IsPartyInviteVisible(partyInviteShowPlan.Slots) &&
      !PartyFrameUiLaw.IsPartyInviteVisible(StaticPopupCoordinatorLaw.Slots.Empty) &&
      partyInviteShowPlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.PrepareContent,
          StaticPopupCoordinatorLaw.EffectKind.HideEditBox,
          StaticPopupCoordinatorLaw.EffectKind.EnableAccept,
          StaticPopupCoordinatorLaw.EffectKind.Show,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound,
          StaticPopupCoordinatorLaw.EffectKind.OnShow,
          StaticPopupCoordinatorLaw.EffectKind.Resize,
          StaticPopupCoordinatorLaw.EffectKind.EntrySound,
      ]),
    "PARTY_INVITE authoritative slot/data/show plan drift");
StaticPopupCoordinatorLaw.Plan partyInviteAcceptPlan = StaticPopupCoordinatorLaw.Click(
    partyInviteShowPlan.Slots, 1, buttonIndex: 1);
StaticPopupCoordinatorLaw.Plan partyInviteDeclinePlan = StaticPopupCoordinatorLaw.Click(
    partyInviteShowPlan.Slots, 1, buttonIndex: 2);
StaticPopupCoordinatorLaw.Plan partyInviteEscapePlan =
    StaticPopupCoordinatorLaw.Escape(partyInviteShowPlan.Slots);
StaticPopupCoordinatorLaw.Plan partyInviteTimeoutPlan = StaticPopupCoordinatorLaw.Advance(
    partyInviteShowPlan.Slots, 1, elapsedSeconds: 60);
StaticPopupCoordinatorLaw.Plan partyInviteDirectHidePlan = StaticPopupCoordinatorLaw.HideByType(
    partyInviteShowPlan.Slots, PartyFrameUiLaw.PartyInvitePopupType);
Check(partyInviteAcceptPlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.Accept,
          StaticPopupCoordinatorLaw.EffectKind.Hide,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
          StaticPopupCoordinatorLaw.EffectKind.OnHide,
      ]) &&
      partyInviteDeclinePlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.CancelClicked,
          StaticPopupCoordinatorLaw.EffectKind.Hide,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
          StaticPopupCoordinatorLaw.EffectKind.OnHide,
      ]) &&
      partyInviteEscapePlan.Effects.Select(effect => effect.Kind)
          .SequenceEqual(partyInviteDeclinePlan.Effects.Select(effect => effect.Kind)) &&
      partyInviteTimeoutPlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.CancelTimeout,
          StaticPopupCoordinatorLaw.EffectKind.Hide,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
          StaticPopupCoordinatorLaw.EffectKind.OnHide,
      ]) &&
      partyInviteDirectHidePlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.Hide,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
          StaticPopupCoordinatorLaw.EffectKind.OnHide,
      ]),
    "PARTY_INVITE accept/clicked/Escape/timeout/direct-hide plan ordering drift");
StaticPopupCoordinatorLaw.Plan partyInviteOverridePlan = StaticPopupCoordinatorLaw.Show(
    partyInviteShowPlan.Slots, partyInviteDefinition, playerDeadOrGhost: false,
    dataToken: "Replacement Inviter");
Check(partyInviteOverridePlan.Effects.Select(effect => effect.Kind).SequenceEqual(
      [
          StaticPopupCoordinatorLaw.EffectKind.CancelOverride,
          StaticPopupCoordinatorLaw.EffectKind.Hide,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
          StaticPopupCoordinatorLaw.EffectKind.OnHide,
          StaticPopupCoordinatorLaw.EffectKind.PrepareContent,
          StaticPopupCoordinatorLaw.EffectKind.HideEditBox,
          StaticPopupCoordinatorLaw.EffectKind.EnableAccept,
          StaticPopupCoordinatorLaw.EffectKind.Show,
          StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound,
          StaticPopupCoordinatorLaw.EffectKind.OnShow,
          StaticPopupCoordinatorLaw.EffectKind.Resize,
          StaticPopupCoordinatorLaw.EffectKind.EntrySound,
      ]),
    "PARTY_INVITE same-type override/reuse plan ordering drift");
Check((ushort)Op.SMSG_GROUP_INVITE == 0x006f && (ushort)Op.CMSG_GROUP_ACCEPT == 0x0072 &&
      (ushort)Op.CMSG_GROUP_DECLINE == 0x0073 && (ushort)Op.SMSG_GROUP_LIST == 0x007d &&
      (ushort)Op.SMSG_PARTY_MEMBER_STATS == 0x007e &&
      (ushort)Op.CMSG_REQUEST_PARTY_MEMBER_STATS == 0x027f &&
      (ushort)Op.SMSG_PARTY_MEMBER_STATS_FULL == 0x02f2,
    "build-5875 party invite/roster/stats opcodes drift");

string partyRuntimeSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.PartyFrames.cs"));
string partyLawSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Engine", "UI", "PartyFrameUiLaw.cs"));
string partyNetSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Net.cs"));
string partySettingsSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.Settings.cs"));
string partySessionSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Net", "WorldSession.cs"));
string partyLiveRunSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.LiveRun.cs"));
string partyCaptureSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.DevTools.UiParity.cs"));
string painterlyUiSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.PainterlyUi.cs"));
PartyFrameClinicalChecks.CheckFrozenStaticPopupSources(ClientConfig.FindRepoRoot());
int partyParseRoster = partyRuntimeSource.IndexOf("PartyFramePacketLaw.ParseRoster(body)",
    StringComparison.Ordinal);
int partyCommitRoster = partyRuntimeSource.IndexOf("_partyMembers.Clear();", partyParseRoster,
    StringComparison.Ordinal);
Check(partyParseRoster >= 0 && partyCommitRoster > partyParseRoster &&
      partyNetSource.Contains("ApplyPartyMemberStats(body, fullSnapshot: false)", StringComparison.Ordinal) &&
      partyNetSource.Contains("ApplyPartyMemberStats(body, fullSnapshot: true)", StringComparison.Ordinal),
    "party atomic parser-before-commit or FULL-vs-delta dispatch seam drift");
Check(partySessionSource.Contains(
          "Op.CMSG_GROUP_ACCEPT, BuildGroupAcceptBody()", StringComparison.Ordinal) &&
      partySessionSource.Contains(
          "Op.CMSG_GROUP_DECLINE, BuildGroupDeclineBody()", StringComparison.Ordinal),
    "party accept/decline exact empty outbound bodies drift");
Check(partyRuntimeSource.Contains("InspectBinding.Party(hoveredIndex))",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("action == PartyPointerAction.Target", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("PainterlyRoundArt(portraitPath)", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("party-token-guid-is-not-streamed", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("own.Fields.Bytes0.Race", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("Vector4 portraitColor = new(portraitRgb, portraitAlpha)",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("InviteButtonPushed(held, hovered)", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("bool pvp = hovered.Pvp;", StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("bool pvp = hovered.Pvp ||", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("? \"GameTooltipHeaderText\" : \"GameTooltipText\"",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("PartyFrameUiLaw.TooltipLayout(rowWidths, rowHeights, s)",
          StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("MathF.Max(120 * s", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("UpdateAndQueuePartyTooltip(-1, null, NowSeconds(), capture: false)",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("ImGuiWindowFlags.Tooltip", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("petOrStanceVisible: PetOrStanceActionBarVisible",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("min=0;max={memberHealth.Maximum};value={memberHealth.Value}",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains(
          "min=0;max={view.MaxPower};value={Math.Min(view.Power, view.MaxPower)}",
          StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("zero-health-fraction", StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("zero-power-fraction", StringComparison.Ordinal),
    "party no-retarget PARTY origin/circular fallback/empty out-of-range/Horde/tooltip-health seam drift");
// The party portrait now reaches its circular mask through the painterly art
// path, so the mask guarantee above only holds while that path still degrades
// to the plain masked copy with the mode off. Painterly is a VARIANT of the
// normal HUD, never a replacement for it.
Check(painterlyUiSource.Contains(
          "if (!PainterlyUi || _painterly is null) return _gameplayArt.CircularHandle(path);",
          StringComparison.Ordinal) &&
      painterlyUiSource.Contains(
          "if (!PainterlyUi || _painterly is null) return _gameplayArt.Handle(path);",
          StringComparison.Ordinal),
    "painterly UI art must fall back to the authored handle when the mode is off");
Check(partyRuntimeSource.Contains("PartyFrameUiLaw.IsLeaveRoster(wire)",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("if (leaving) HidePartyInvite();",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("PartyFrameUiLaw.BeginTooltipSnapshot(_partyTooltipSlot",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("PartyMember[] currentSlots = PartyFrameMembers();",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("BuildPartyMemberView(currentSlots[_partyTooltipSlot])",
          StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("party-tooltip-slot-token-is-absent-during-fade",
          StringComparison.Ordinal) &&
      // Same seam as PartyFrameClinicalChecks: the popup button font gained a disabled
      // branch. The GameFont* family is what matters, which the DialogButton* bans keep.
      partyRuntimeSource.Contains(
          "string fontObject = !enabled ? \"GameFontDisable\"\n" +
          "            : hovered ? \"GameFontHighlight\" : \"GameFontNormal\";",
          StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("DialogButtonHighlightText", StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("DialogButtonNormalText", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("party-pvp-faction-is-unresolved", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("MathF.Max(0f, (float)(now - _partyLowHealthLastAt))",
          StringComparison.Ordinal),
    "party leave/slot-tooltip/popup-font/PvP-resolution/full-elapsed seam drift");
int partyWorldStart = partyNetSource.IndexOf("if (_queuedWorldEntry is { } enter", StringComparison.Ordinal);
int partyWorldEnd = partyNetSource.IndexOf("// Drain + dispatch the inbound packet stream",
    partyWorldStart, StringComparison.Ordinal);
int partyInviteLifecycleCall = partyNetSource.IndexOf("UpdatePartyInviteLifecycle();",
    StringComparison.Ordinal);
int partyDisconnectedReset = partyNetSource.IndexOf("ResetParty();", StringComparison.Ordinal);
int partyResetStart = partyRuntimeSource.IndexOf("private void ResetParty()", StringComparison.Ordinal);
int partyResetEnd = partyRuntimeSource.IndexOf("private void ApplyPartyRoster", partyResetStart,
    StringComparison.Ordinal);
Check(partyWorldStart >= 0 && partyWorldEnd > partyWorldStart &&
      !partyNetSource[partyWorldStart..partyWorldEnd].Contains("ResetParty();",
          StringComparison.Ordinal) &&
      partyDisconnectedReset >= 0 && partyInviteLifecycleCall > partyDisconnectedReset &&
      partyInviteLifecycleCall < partyWorldStart &&
      partyResetStart >= 0 && partyResetEnd > partyResetStart &&
      partyRuntimeSource[partyResetStart..partyResetEnd].Contains(
          "HidePartyInvite();", StringComparison.Ordinal) &&
      !partyRuntimeSource[partyResetStart..partyResetEnd].Contains(
          "Array.Clear(_partyLowHealthTimers)", StringComparison.Ordinal) &&
      !partyRuntimeSource[partyResetStart..partyResetEnd].Contains(
          "_partyTooltip = null", StringComparison.Ordinal) &&
      !partyRuntimeSource[partyResetStart..partyResetEnd].Contains(
          "_partyTooltipSlot = -1", StringComparison.Ordinal),
    "party disconnect-only reset/worldport preservation or retained frame state seam drift");
int partyApplyInviteStart = partyRuntimeSource.IndexOf(
    "private void ApplyPartyInvite(byte[] body)", StringComparison.Ordinal);
int partyApplyInviteEnd = partyRuntimeSource.IndexOf("private void ApplyPartyDecline",
    partyApplyInviteStart, StringComparison.Ordinal);
string partyApplyInvite = partyApplyInviteStart >= 0 && partyApplyInviteEnd > partyApplyInviteStart
    ? partyRuntimeSource[partyApplyInviteStart..partyApplyInviteEnd]
    : "";
int partyInviteParseCall = partyApplyInvite.IndexOf("PartyFramePacketLaw.ParseInvite(body)",
    StringComparison.Ordinal);
int partyInviteShowCall = partyApplyInvite.IndexOf("StaticPopupCoordinatorLaw.Show(",
    StringComparison.Ordinal);
Check(partyInviteParseCall >= 0 && partyInviteShowCall > partyInviteParseCall &&
      partyApplyInvite.Contains("PartyFrameUiLaw.PartyInvitePopupDefinition",
          StringComparison.Ordinal) &&
      partyApplyInvite.Contains("dataToken: inviter", StringComparison.Ordinal),
    "PARTY_INVITE parser-before-authoritative-slot Show seam drift");
int partyPopupExecutorStart = partyRuntimeSource.IndexOf(
    "private void ExecuteStaticPopupPlan(", StringComparison.Ordinal);
int partyDirectHideStart = partyRuntimeSource.IndexOf("private void HidePartyInvite()",
    partyPopupExecutorStart, StringComparison.Ordinal);
int partyEscapeDriverStart = partyRuntimeSource.IndexOf(
    "private bool TryDismissStaticPopupOnEscape()", partyDirectHideStart,
    StringComparison.Ordinal);
string partyPopupExecutor = partyPopupExecutorStart >= 0 &&
    partyDirectHideStart > partyPopupExecutorStart
    ? partyRuntimeSource[partyPopupExecutorStart..partyDirectHideStart]
    : "";
int partySlotCommit = partyPopupExecutor.IndexOf("_staticPopupSlots = plan.Slots;",
    StringComparison.Ordinal);
int partyEffectLoop = partyPopupExecutor.IndexOf(
    "foreach (StaticPopupCoordinatorLaw.Effect effect in plan.Effects)",
    StringComparison.Ordinal);
int partyAcceptWire = partyPopupExecutor.IndexOf("_net?.GroupAccept();",
    StringComparison.Ordinal);
int partyAcceptGuard = partyPopupExecutor.IndexOf("_partyInviteAccepted = true;",
    partyAcceptWire, StringComparison.Ordinal);
Check(partyPopupExecutorStart >= 0 && partyDirectHideStart > partyPopupExecutorStart &&
      partyEscapeDriverStart > partyDirectHideStart && partySlotCommit >= 0 &&
      partyEffectLoop > partySlotCommit && partyAcceptWire > partyEffectLoop &&
      partyAcceptGuard > partyAcceptWire &&
      partyPopupExecutor.Contains("if (!_partyInviteAccepted) _net?.GroupDecline();",
          StringComparison.Ordinal) &&
      partyRuntimeSource[partyDirectHideStart..partyEscapeDriverStart].Contains(
          "StaticPopupCoordinatorLaw.HideByType(", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains("StaticPopupCoordinatorLaw.Escape(_staticPopupSlots)",
          StringComparison.Ordinal) &&
      partySettingsSource.Contains("StaticPopupCoordinatorLaw.AnyVisible(_staticPopupSlots)",
          StringComparison.Ordinal) &&
      partySettingsSource.Contains("TryDismissStaticPopupOnEscape()", StringComparison.Ordinal),
    "PARTY_INVITE slot-commit/effect/guard/direct-hide/shared-Escape seam drift");
int partyPopupEscapeLayer = partySettingsSource.IndexOf("case GameMenuEscapeLayer.Popup:",
    StringComparison.Ordinal);
int partyLogoutEscape = partySettingsSource.IndexOf("TryCancelLogoutOnEscape()",
    partyPopupEscapeLayer, StringComparison.Ordinal);
int partySharedPopupEscape = partySettingsSource.IndexOf("TryDismissStaticPopupOnEscape()",
    partyLogoutEscape, StringComparison.Ordinal);
int partyMailEscape = partySettingsSource.IndexOf("TryDismissMailConfirmationOnEscape()",
    partySharedPopupEscape, StringComparison.Ordinal);
int partyEnchantEscape = partySettingsSource.IndexOf("TryDismissEnchantConfirmationOnEscape()",
    partyMailEscape, StringComparison.Ordinal);
int partySkillEscape = partySettingsSource.IndexOf("TryDismissSkillUnlearnConfirmationOnEscape()",
    partyEnchantEscape, StringComparison.Ordinal);
Check(partyPopupEscapeLayer >= 0 && partyLogoutEscape > partyPopupEscapeLayer &&
      partySharedPopupEscape > partyLogoutEscape && partyMailEscape > partySharedPopupEscape &&
      partyEnchantEscape > partyMailEscape && partySkillEscape > partyEnchantEscape,
    "shared StaticPopup insertion changed existing popup Escape precedence");
int partyLifecycleStart = partyRuntimeSource.IndexOf(
    "private void UpdatePartyInviteLifecycle()", StringComparison.Ordinal);
int partyLifecycleEnd = partyRuntimeSource.IndexOf("private PartyMemberView BuildPartyMemberView",
    partyLifecycleStart, StringComparison.Ordinal);
int partyInviteDrawStart = partyRuntimeSource.IndexOf("private void DrawPartyInvite()",
    StringComparison.Ordinal);
int partyInviteDrawEnd = partyRuntimeSource.IndexOf("private bool DrawPartyInviteButton",
    partyInviteDrawStart, StringComparison.Ordinal);
Check(partyLifecycleStart >= 0 && partyLifecycleEnd > partyLifecycleStart &&
      partyRuntimeSource[partyLifecycleStart..partyLifecycleEnd].Contains(
          "StaticPopupCoordinatorLaw.Advance(", StringComparison.Ordinal) &&
      partyRuntimeSource[partyLifecycleStart..partyLifecycleEnd].Contains(
          "_staticPopupLastUpdateTicks = now;", StringComparison.Ordinal) &&
      partyInviteDrawStart >= 0 && partyInviteDrawEnd > partyInviteDrawStart &&
      partyRuntimeSource[partyInviteDrawStart..partyInviteDrawEnd].Contains(
          "PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots)", StringComparison.Ordinal) &&
      !partyRuntimeSource[partyInviteDrawStart..partyInviteDrawEnd].Contains(
          "Stopwatch.GetTimestamp()", StringComparison.Ordinal) &&
      !partyRuntimeSource[partyInviteDrawStart..partyInviteDrawEnd].Contains(
          "StaticPopupCoordinatorLaw.Advance", StringComparison.Ordinal),
    "party coordinator Advance must remain outside the occludable renderer");
Check(partyLawSource.Contains("PartyInvitePopupDefinition = new(", StringComparison.Ordinal) &&
      partyLawSource.Contains("PartyInvitePopup(\n        StaticPopupCoordinatorLaw.Slots slots)",
          StringComparison.Ordinal) &&
      partyLawSource.Contains("IsPartyInviteVisible(StaticPopupCoordinatorLaw.Slots slots)",
          StringComparison.Ordinal) &&
      !partyLawSource.Contains("PartyInviteDismissal", StringComparison.Ordinal) &&
      !partyLawSource.Contains("PartyInviteEffect", StringComparison.Ordinal) &&
      !partyLawSource.Contains("PartyInviteWireCount", StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("_partyInviter", StringComparison.Ordinal) &&
      !partyRuntimeSource.Contains("_partyInviteDeadline", StringComparison.Ordinal) &&
      partyRuntimeSource.Contains(
          "callback-reentry branches remain an explicit later boundary",
          StringComparison.Ordinal),
    "PARTY_INVITE pure definition/query or legacy parallel-state removal drift");
Check(partyLiveRunSource.Contains(
          "party-stage rejected: Party proof requires observed wire/runtime state; no state mutated",
          StringComparison.Ordinal) &&
      partyLiveRunSource.Contains(
          "party-invite-stage rejected: Party invite proof requires an inbound invitation; no state mutated",
          StringComparison.Ordinal) &&
      partyLiveRunSource.Contains(
          "party-clear rejected: command cannot erase authenticated roster/invite state; no state mutated",
          StringComparison.Ordinal) &&
      !partyLiveRunSource.Contains("StagePartyFrameProof", StringComparison.Ordinal) &&
      !partyLiveRunSource.Contains("StagePartyInviteProof", StringComparison.Ordinal),
    "legacy Party proof commands must truthfully reject without mutation");
Check(partyCaptureSource.Contains("party-frame-requires-observed-wire-roster",
          StringComparison.Ordinal) &&
      partyCaptureSource.Contains("party-invite-requires-observed-inbound-invitation",
          StringComparison.Ordinal) &&
      partyCaptureSource.Contains("observed-party-wire-runtime", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("compactSlotSources", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("captureStateMutation\"] = false", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("scenario[\"slot\"]", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("scenario[\"type\"]", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("scenario[\"timeLeftSeconds\"]", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("scenario[\"definitionFlags\"]", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("scenario[\"integratedTypes\"]", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("new[] { PartyInvitePopupType }", StringComparison.Ordinal) &&
      partyCaptureSource.Contains("PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots)",
          StringComparison.Ordinal) &&
      !partyCaptureSource.Contains("_partyInviter", StringComparison.Ordinal) &&
      !partyCaptureSource.Contains("_partyInviteDeadline", StringComparison.Ordinal),
    "Party capture staged/empty rejection or coordinator telemetry drift");

byte[] maskPixels = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
IconApertureMask.ApplyCircularBgra(maskPixels, 8, 8);
int AlphaAt(int x, int y) => maskPixels[(y * 8 + x) * 4 + 3];
Check(AlphaAt(0, 0) == 0 && AlphaAt(7, 0) == 0 && AlphaAt(0, 7) == 0 &&
      AlphaAt(7, 7) == 0 && AlphaAt(3, 3) == 255,
    "party TemporaryPortrait circular edge-alpha containment drift");

if (args.Contains("--party-frame-only", StringComparer.Ordinal))
{
    Console.WriteLine("interface-wire-check: PartyFrame PASS");
    return;
}

Check((ushort)Op.CMSG_FRIEND_LIST == 102 && (ushort)Op.SMSG_FRIEND_LIST == 103 &&
      (ushort)Op.SMSG_FRIEND_STATUS == 104 && (ushort)Op.CMSG_ADD_FRIEND == 105 &&
      (ushort)Op.CMSG_DEL_FRIEND == 106, "social opcodes");

Check((ushort)Op.CMSG_GMTICKET_CREATE == 517 && (ushort)Op.SMSG_GMTICKET_CREATE == 518 &&
      (ushort)Op.CMSG_GMTICKET_UPDATETEXT == 519 && (ushort)Op.CMSG_GMTICKET_GETTICKET == 529 &&
      (ushort)Op.CMSG_GMTICKET_DELETETICKET == 535 && (ushort)Op.CMSG_GMTICKET_SYSTEMSTATUS == 538,
      "help ticket opcodes");

// ---- gameplay text migration fence ---------------------------------------------------------
// Gameplay panels must draw text through GameText/FontObjectLaw (the derived 1.12 text law),
// never raw AddText(ImGui.GetFont(), ...) - that path scales the supersampled atlas and
// reintroduces the unit mismatch and softness the law removed. This is a RATCHET: the baseline
// below is each file's remaining raw-draw count. New raw draws fail the check; migrating a
// panel to zero (or fewer) is allowed and should be followed by lowering its baseline here.
// FontObjectLaw's registry heights are asserted against the shipped Fonts.xml transcription.
Check(FontObjectLaw.Get("GameFontNormal") ==
          new FontObjectSpec(FontFace.FrizQt, 12f, 0xff00d1ff, 0xff000000, 0) &&
      FontObjectLaw.Get("SubSpellFont") ==
          new FontObjectSpec(FontFace.FrizQt, 10f, 0xff003359, null, 0) &&
      FontObjectLaw.Get("GameTooltipHeaderText") ==
          new FontObjectSpec(FontFace.FrizQt, 14f, 0xffffffff, null, 0) &&
      FontObjectLaw.Get("GameTooltipText") ==
          new FontObjectSpec(FontFace.FrizQt, 12f, 0xffffffff, null, 0) &&
      FontObjectLaw.Get("NumberFontNormal") ==
          new FontObjectSpec(FontFace.ArialN, 14f, 0xffffffff, null, 1) &&
      FontObjectLaw.Get("NumberFontNormalSmall").Outline == 2 &&
      FontObjectLaw.Get("NumberFontNormalSmallGray") ==
          new FontObjectSpec(FontFace.ArialN, 12f, 0xff999999, null, 2),
    "FontObjectLaw drift from the build-5875 Fonts.xml transcription");
Check(FontObjectLaw.Get("GameTooltipHeaderText").Height == SpellTooltipLaw.HeaderFontHeight &&
      FontObjectLaw.Get("GameTooltipText").Height == SpellTooltipLaw.TextFontHeight &&
      FontObjectLaw.Get("GameFontNormal").Height == SpellbookLaw.NameFontHeight &&
      FontObjectLaw.Get("SubSpellFont").Height == SpellbookLaw.RankFontHeight,
    "FontObjectLaw heights disagree with the spellbook/tooltip law constants");
var fontBakePairs = FontObjectLaw.DefaultBakePairs().ToHashSet();
Check(fontBakePairs.Contains((FontFace.Morpheus, 18f, false)) &&
      fontBakePairs.Contains((FontFace.Morpheus, 15f, false)) &&
      fontBakePairs.Contains((FontFace.FrizQt, 13f, false)) &&
      UiFont.Morpheus == FontFace.Morpheus,
    "active Quest/Mail font objects lost their exact Morpheus/FRIZQT bake pairs");
string fontBootstrapSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
    "MSUIClient", "Program.cs"));
Check(fontBootstrapSource.Contains("UiFont.Morpheus", StringComparison.Ordinal) &&
      fontBootstrapSource.Contains(
          "FontFace.Morpheus, morpheusFontPath", StringComparison.Ordinal),
    "Morpheus is not extracted/configured before the gameplay font atlas is built");

var rawTextBaseline = new Dictionary<string, int>
{
    ["Program.Auction.cs"] = 5,
    ["Program.Bank.cs"] = 1,
    ["Program.CharacterPage.cs"] = 1,
    ["Program.Chat.cs"] = 3,
    ["Program.GameObjects.cs"] = 1,
    ["Program.Guild.cs"] = 3,
    ["Program.Help.cs"] = 4,
    ["Program.Inventory.cs"] = 0,
    ["Program.Keybindings.cs"] = 3,
    ["Program.Loot.cs"] = 2,
    ["Program.Macro.cs"] = 2,
    ["Program.Mail.cs"] = 0,
    ["Program.Minimap.cs"] = 1,
    ["Program.Professions.cs"] = 6,
    ["Program.Quest.cs"] = 4,
    ["Program.Social.cs"] = 3,
    ["Program.Tabard.cs"] = 1,
    ["Program.Talents.cs"] = 2,
    ["Program.Trade.cs"] = 2,
    ["Program.Trainer.cs"] = 4,
    ["Program.VanillaUi.cs"] = 4,
    ["Program.Vendor.cs"] = 0,
};
string panelSourceDir = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
var rawTextPattern = new System.Text.RegularExpressions.Regex(
    @"AddText\s*\(\s*ImGui\s*\.\s*GetFont\s*\(\s*\)");
foreach (string panelFile in Directory.GetFiles(panelSourceDir, "Program.*.cs"))
{
    string name = Path.GetFileName(panelFile);
    int raw = rawTextPattern.Matches(SourceText.Read(panelFile)).Count;
    int allowed = rawTextBaseline.GetValueOrDefault(name, 0);
    Check(raw <= allowed,
        $"{name}: {raw} raw AddText(ImGui.GetFont()) draw(s) exceed the migration baseline " +
        $"of {allowed}. Draw gameplay text through GameText/FontObjectLaw; " +
        "never add raw default-font draws.");
    if (raw < allowed)
        Console.WriteLine($"[text-fence] {name} is below baseline ({raw}/{allowed}) - " +
                          "lower its entry in interface-wire-check to lock in the migration");
}

TargetCycleClinicalChecks.Run();
Console.WriteLine("interface-wire-check: TargetCycle PASS");
SocialTabBindingClinicalChecks.Run();
Console.WriteLine("interface-wire-check: SocialTabBindings PASS");
AudioBindingClinicalChecks.Run();
Console.WriteLine("interface-wire-check: AudioBindings PASS");
MinimapBindingClinicalChecks.Run();
Console.WriteLine("interface-wire-check: MinimapBinding PASS");
KeyBindingRegistryClinicalChecks.Run();
Console.WriteLine("interface-wire-check: KeyBindingRegistry PASS");
CameraPoseClinicalChecks.Run();
Console.WriteLine("interface-wire-check: CameraPose PASS");
CameraFollowClinicalChecks.Run();
Console.WriteLine("interface-wire-check: CameraFollow PASS");
EquipmentDisplayPreferenceClinicalChecks.Run();
Console.WriteLine("interface-wire-check: EquipmentDisplayPreference PASS");
ChatLanguageClinicalChecks.Run();
Console.WriteLine("interface-wire-check: ChatLanguage PASS");
ScopedViewClinicalChecks.Run();
Console.WriteLine("interface-wire-check: ScopedView PASS");
ViewSubjectClinicalChecks.Run();
Console.WriteLine("interface-wire-check: ViewSubject PASS");
ClientControlUpdateClinicalChecks.Run();
Console.WriteLine("interface-wire-check: ClientControlUpdate PASS");
PartyMemberFactsClinicalChecks.Run();
Console.WriteLine("interface-wire-check: PartyMemberFacts PASS");
PartyQuestClinicalChecks.Run();
Console.WriteLine("interface-wire-check: PartyQuest PASS");
PartyQuestActsClinicalChecks.Run();
PartyGiverStatusClinicalChecks.Run();
PartyLeadClinicalChecks.Run();
Console.WriteLine("interface-wire-check: PartyQuestActs PASS");
PlayerPowerBarsClinicalChecks.Run();
Console.WriteLine("interface-wire-check: PlayerPowerBars PASS");
HovercastClinicalChecks.Run();
Console.WriteLine("interface-wire-check: Hovercast PASS");
CompanionClinicalChecks.Run();
PartyTaxiClinicalChecks.Run();
TacticalFreezeClinicalChecks.Run();
PossessLawClinicalChecks.Run();
SwingTimerClinicalChecks.Run();
Console.WriteLine("interface-wire-check: SwingTimer PASS");
// The ImGui-widget ratchet only ratchets if the DEFAULT run enforces it; behind
// --imgui-policy-only alone, an enrolled panel could regress unnoticed.
GameplayImguiPolicyClinicalChecks.Run();
Console.WriteLine("interface-wire-check: GameplayImguiPolicy PASS");
HudLayoutClinicalChecks.Run();
Console.WriteLine("interface-wire-check: HudLayout PASS");

Console.WriteLine("interface wire checks passed: minimap projection/area/zone + action icons + gossip + vendor + trainer + quest + loot + inventory + bank + mail + auction + profession + guild + social + trade + tabard + talents + gameobjects + taxi opcodes/bodies/bounds/state/render-binding + gameplay-text fence");
