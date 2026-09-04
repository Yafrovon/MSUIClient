using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class UnitPopupClinicalChecks
{
    public static void Run()
    {
        UnitPopupRow[] partyLeaderSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: true, isRaid: false,
            canCooperate: true, unitInParty: true);
        UnitPopupRow[] raidLeaderSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: true, isRaid: true,
            canCooperate: true, unitInParty: true);
        UnitPopupRow[] partyMemberSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: false, isRaid: false,
            canCooperate: true, unitInParty: true);
        UnitPopupRow[] masterLootLeaderRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Party,
            inParty: true, isLeader: true, isRaid: false, canCooperate: true,
            unitInParty: true, lootMethod: 2, unitIsLootMaster: false);
        UnitPopupRow[] playerRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Player,
            inParty: false, isLeader: false, isRaid: false, canCooperate: true,
            unitInParty: false);
        UnitPopupRow[] guildLeaderOtherRows = UnitPopupUiLaw.VisibleGuildRows(
            unitInParty: false, guildLeader: true, self: false);
        UnitPopupRow[] guildSelfRows = UnitPopupUiLaw.VisibleGuildRows(
            unitInParty: false, guildLeader: true, self: true);
        Check(partyLeaderSelfRows.SequenceEqual(new[]
              { UnitPopupRow.LootMethod, UnitPopupRow.LootThreshold, UnitPopupRow.Leave,
                UnitPopupRow.RaidTargetIcon, UnitPopupRow.Cancel }) &&
              raidLeaderSelfRows.SequenceEqual(new[]
                  { UnitPopupRow.LootMethod, UnitPopupRow.LootThreshold, UnitPopupRow.Leave,
                    UnitPopupRow.RaidTargetIcon, UnitPopupRow.Cancel }) &&
              partyMemberSelfRows.SequenceEqual(new[]
                  { UnitPopupRow.LootMethod, UnitPopupRow.LootThreshold,
                    UnitPopupRow.ClaimLead, UnitPopupRow.Leave, UnitPopupRow.Cancel }) &&
              masterLootLeaderRows.Contains(UnitPopupRow.LootPromote) &&
              masterLootLeaderRows.SequenceEqual(new[]
                  { UnitPopupRow.Whisper, UnitPopupRow.Promote, UnitPopupRow.LootPromote,
                    UnitPopupRow.Uninvite, UnitPopupRow.Inspect, UnitPopupRow.Trade,
                    UnitPopupRow.Follow, UnitPopupRow.Duel, UnitPopupRow.RaidTargetIcon,
                    UnitPopupRow.Cancel }) &&
              playerRows.SequenceEqual(new[]
                  { UnitPopupRow.Whisper, UnitPopupRow.Inspect, UnitPopupRow.Invite,
                    UnitPopupRow.Trade, UnitPopupRow.Follow, UnitPopupRow.Duel,
                    UnitPopupRow.Cancel }) &&
              guildLeaderOtherRows.SequenceEqual(new[]
                  { UnitPopupRow.Whisper, UnitPopupRow.Invite,
                    UnitPopupRow.GuildPromote, UnitPopupRow.Cancel }) &&
              guildSelfRows.SequenceEqual(new[]
                  { UnitPopupRow.Whisper, UnitPopupRow.Invite,
                    UnitPopupRow.GuildLeave, UnitPopupRow.Cancel }) &&
              UnitPopupUiLaw.RowText(UnitPopupRow.GuildPromote) ==
                  "Promote to Guild Master" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.GuildLeave) == "Leave Guild" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.Follow) == "Follow" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.ClaimLead) == "Claim Party Lead" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.LootMethod, 4) == "Need Before Greed" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.LootThreshold, 0, 3) == "Rare" &&
              UnitPopupUiLaw.HasArrow(UnitPopupRow.LootMethod, isLeader: true) &&
              !UnitPopupUiLaw.HasArrow(UnitPopupRow.LootMethod, isLeader: false),
            "UnitPopup current loot/raid parent menu gating drift");

        Check(UnitPopupUiLaw.CardWidth(20f) == 65f &&
              UnitPopupUiLaw.CardWidth(135f) == 180f &&
              UnitPopupUiLaw.CardWidth(1000f) == 1045f &&
              UnitPopupUiLaw.CardWidth(float.NaN) == UnitPopupUiLaw.MinimumCardWidth &&
              UnitPopupUiLaw.CardHeight(3) == 94f &&
              UnitPopupUiLaw.RowOrigin(0) == new Vector2(15f, 31f) &&
              UnitPopupUiLaw.RowTextOrigin(0) == new Vector2(15f, 34f) &&
              UnitPopupUiLaw.RowSize(120f) == new Vector2(105f, 16f) &&
              UnitPopupUiLaw.MenuHeight(4, hasTitle: false) == 94f &&
              UnitPopupUiLaw.RowOrigin(0, hasTitle: false, checkable: true) ==
                  new Vector2(11f, 15f) &&
              UnitPopupUiLaw.RowTextOrigin(0, hasTitle: false, checkable: true) ==
                  new Vector2(38f, 18f) &&
              UnitPopupUiLaw.CardWidth([new(100f, false, true, true)]) == 195f,
            "UnitPopup UIDropDownMenu MENU-mode geometry drift");
        Check(UnitPopupUiLaw.MenuBackdropFillTint == new Vector4(.09f, .09f, .19f, 1f) &&
              UnitPopupUiLaw.MenuBackdropEdgeTint == Vector4.One &&
              UnitPopupUiLaw.CheckSize == new Vector2(24) &&
              UnitPopupUiLaw.RaidIconSize == new Vector2(15) &&
              UnitPopupUiLaw.ArrowSize == new Vector2(16),
            "UnitPopup MENU backdrop globals/tint drift");

        Check(UnitPopupUiLaw.ClampOrigin(new Vector2(790f, 590f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(676f, 516f) &&
              UnitPopupUiLaw.ClampOrigin(new Vector2(-20f, -10f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(4f, 4f) &&
              UnitPopupUiLaw.ClampOrigin(new Vector2(200f, 150f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(200f, 150f),
            "UnitPopup viewport-edge clamping drift");

        Check(UnitPopupUiLaw.LootMethodValue(UnitPopupRow.GroupLoot) == 3 &&
              UnitPopupUiLaw.QualityValue(UnitPopupRow.Quality4) == 4 &&
              UnitPopupUiLaw.RaidTargetValue(UnitPopupRow.RaidTarget8) == 8 &&
              UnitPopupUiLaw.IsChecked(UnitPopupRow.RaidTargetNone, 0, 2, 0) &&
              UnitPopupUiLaw.RaidIconUv(UnitPopupRow.RaidTarget6) ==
                  (new Vector2(.25f, .25f), new Vector2(.5f, .5f)),
            "UnitPopup level-2 loot/quality/raid decoration law drift");

        Check(QuestMarkerUiLaw.Style(0) is null &&
              QuestMarkerUiLaw.Style(1) is { ModelPath: @"Interface\Buttons\TalkToMeGrey.m2" } &&
              QuestMarkerUiLaw.Style(3) is { ModelPath: @"Interface\Buttons\TalkToMeQuestion_Grey.m2" } &&
              QuestMarkerUiLaw.Style(4) is { ModelPath: @"Interface\Buttons\TalkToMeQuestion_LTBlue.m2" } &&
              QuestMarkerUiLaw.Style(5) is { ModelPath: @"Interface\Buttons\TalkToMe.m2" } &&
              QuestMarkerUiLaw.Style(6) is { ModelPath: @"Interface\Buttons\TalkToMeQuestionMark.m2" } &&
              QuestMarkerUiLaw.Style(7) is not null &&
              QuestMarkerUiLaw.UnknownFlightMaster is
                  { ModelPath: @"Interface\Buttons\TalkToMeGreen.m2" },
            "quest dialog-status TalkToMe marker mapping drift");

        Check(WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Gossip |
                  WorldCursorUiLaw.Vendor, null) == WorldCursorKind.Speak &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Questgiver, 0) is null &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Questgiver, 5) ==
                  WorldCursorKind.Speak &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Vendor, null) ==
                  WorldCursorKind.Pickup &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.FlightMaster, null) ==
                  WorldCursorKind.Taxi &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Trainer, null) ==
                  WorldCursorKind.Trainer &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Innkeeper, null) ==
                  WorldCursorKind.Interact &&
              WorldCursorUiLaw.ServiceKind(WorldCursorUiLaw.Banker, null) ==
                  WorldCursorKind.Buy &&
              new WorldCursorState(WorldCursorKind.Pickup, true).Stem == "UnablePickup",
            "world NPC cursor service priority/stem drift");

        Check(WorldCursorUiLaw.GameObject(19, 0, 0, 0, false, false, false, true, 1) ==
                  new WorldCursorState(WorldCursorKind.Mail, false) &&
              WorldCursorUiLaw.GameObject(9, 0, 0, 0, false, false, false, true, 31) ==
                  new WorldCursorState(WorldCursorKind.Inspect, true) &&
              WorldCursorUiLaw.GameObject(3, 0, 0, 3, false, false, false, true, 1) ==
                  new WorldCursorState(WorldCursorKind.Mine, false) &&
              WorldCursorUiLaw.GameObject(3, 0, 0, 2, false, false, false, true, 31) ==
                  new WorldCursorState(WorldCursorKind.GatherHerbs, true) &&
              WorldCursorUiLaw.GameObject(3, WorldCursorUiLaw.GameObjectLocked, 0,
                  1, false, false, false, false, 100) ==
                  new WorldCursorState(WorldCursorKind.PickLock, false) &&
              WorldCursorUiLaw.GameObject(5, 0, 0, 0, false, false, false, true, 1) is null &&
              WorldCursorUiLaw.GameObject(3, WorldCursorUiLaw.GameObjectInteractCondition,
                  0, 0, false, false, false, true, 1) is null &&
              WorldCursorUiLaw.GameObject(17, 0, 0, 0, false, false, false, true, 1) is null &&
              new WorldCursorState(WorldCursorKind.GatherHerbs, true).Stem ==
                  "UnableGatherHerbs",
            "world GameObject cursor type/lock/highlight/range law drift");

        Check(!WorldCursorUiLaw.HighlightableGameObject(5, 0, 0, false, false, false) &&
              WorldCursorUiLaw.MouseoverEligibleGameObject(5, 0, 0, 1, false, false, false) &&
              !WorldCursorUiLaw.MouseoverEligibleGameObject(5, 0, 0, 0, false, false, false) &&
              WorldCursorUiLaw.MouseoverEligibleGameObject(8, 0, 0, null, false, false, false) &&
              !WorldCursorUiLaw.BrightensGameObject(8, 0, 0, null, false, false, false) &&
              !WorldCursorUiLaw.MouseoverEligibleGameObject(11, 0, 0, null, false, false, false) &&
              !WorldCursorUiLaw.HighlightableGameObject(3, 0, 0, true, false, false) &&
              WorldCursorUiLaw.HighlightableGameObject(6, 0, 0, true, false, false) &&
              !WorldCursorUiLaw.HighlightableGameObject(6, 0, 0, false, false, false) &&
              WorldCursorUiLaw.HighlightableGameObject(23, 0x11, 0, true, false, false),
            "world GameObject mouseover/highlight/faction strategy separation drift");

        LockSlot quickOpen = new(LockCatalog.KeySkill, 10, 0, 0);
        LockSlot skeletonKey = new(LockCatalog.KeyItem, 13704, 0, 1);
        Check(quickOpen.Available(LockCatalog.StateReady, false) &&
              !quickOpen.Available(LockCatalog.StateReady, true) &&
              skeletonKey.Available(LockCatalog.StateReady, true) &&
              !skeletonKey.Available(LockCatalog.StateReady, false),
            "Lock.dbc Action open/unlock gate drift");
        var quickOpening = new SpellInfo(6247, "Opening", "", "", 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0,
            EffectIds: [33u], EffectMiscValues: [10], EffectBasePoints: [99],
            EffectBaseDice: [1], EffectDicePerLevel: [0f], EffectRealPointsPerLevel: [0f]);
        GameObjectLockOutcome gated = GameObjectLockLaw.Resolve(
            [skeletonKey, new(LockCatalog.KeySkill, 1, 280, 1), quickOpen], [6247],
            id => id == 6247 ? quickOpening : null, _ => 300, _ => false,
            LockCatalog.StateReady, flagLocked: true, gameObjectLevel: 60);
        GameObjectLockOutcome keyed = GameObjectLockLaw.Resolve(
            [skeletonKey, quickOpen], [6247], id => id == 6247 ? quickOpening : null,
            _ => 300, entry => entry == 13704, LockCatalog.StateReady,
            flagLocked: true, gameObjectLevel: 60);
        var pickLock = quickOpening with
        {
            Id = 1804, EffectMiscValues = [1], EffectBasePoints = [4],
            EffectBaseDice = [1], EffectDicePerLevel = [5f], BaseLevel = 1,
        };
        GameObjectLockOutcome skilled = GameObjectLockLaw.Resolve(
            [new(LockCatalog.KeySkill, 1, 280, 1)], [1804],
            id => id == 1804 ? pickLock : null, _ => 300, _ => false,
            LockCatalog.StateReady, flagLocked: true, gameObjectLevel: 60);
        Check(gated.Kind == GameObjectLockOutcomeKind.Unmet && gated.BlocksUsable(true) &&
              !gated.BlocksUsable(false) &&
              keyed == new GameObjectLockOutcome(GameObjectLockOutcomeKind.OpenByKey, 13704) &&
              skilled == new GameObjectLockOutcome(GameObjectLockOutcomeKind.OpenBySpell, 1804),
            "complete ordered Lock.dbc key/skill/action usability resolver drift");
        string data = ClientDataRoot.Path;
        if (Directory.Exists(data))
        {
            using var mpq = new MpqMount(data);
            LockCatalog actual = LockCatalog.Load(mpq) ??
                throw new InvalidDataException("actual Lock.dbc did not parse");
            IReadOnlyList<LockSlot> scholomance = actual.Slots(1159);
            Check(scholomance.Count == 8 &&
                  scholomance[0] == new LockSlot(LockCatalog.KeyItem, 13704, 0, 1) &&
                  scholomance[1] == new LockSlot(LockCatalog.KeySkill, 1, 280, 1) &&
                  scholomance[2] == new LockSlot(LockCatalog.KeySkill, 10, 0, 0),
                "actual Scholomance Lock.dbc key/Pick Lock/Quick Open Action rows drift");
        }

        string root = ClientConfig.FindRepoRoot();
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string picker = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectRender.cs"));
        string doodads = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Doodads",
            "DoodadRenderer.cs"));
        string cursor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.WorldCursor.cs"));
        string gameObjects = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjects.cs"));
        string tooltip = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.GameTooltip.WorldGameObject.cs"));
        Check(targeting.Contains("PickGameObject(_window.MousePosition", StringComparison.Ordinal) &&
              targeting.Contains("UseGameObject(goClicked)", StringComparison.Ordinal) &&
              targeting.Contains("GameObjectHighlightable(clickedGo)", StringComparison.Ordinal) &&
              targeting.Contains("GameObjectBrightens(hoveredGo)", StringComparison.Ordinal) &&
              picker.Contains("TryPickDynamic", StringComparison.Ordinal) &&
              picker.Contains("GameObjectMouseoverEligible(go)", StringComparison.Ordinal) &&
              picker.Contains("IsWorldPointNearDynamicPickBounds", StringComparison.Ordinal) &&
              doodads.Contains("RayDynamicRenderMesh", StringComparison.Ordinal) &&
              doodads.Contains("model.PickIndices", StringComparison.Ordinal) &&
              !doodads.Contains("TryGetDynamicPickBounds", StringComparison.Ordinal) &&
              cursor.Contains("FirstCursorLockType", StringComparison.Ordinal) &&
              cursor.Contains("WorldCursorUiLaw.GameObject(", StringComparison.Ordinal) &&
              cursor.Contains("ResolveGameObjectLock(hoveredGo)", StringComparison.Ordinal) &&
              gameObjects.Contains("GameObjectLockLaw.Resolve", StringComparison.Ordinal) &&
              gameObjects.Contains("GameObjectLockOutcomeKind.Unmet", StringComparison.Ordinal) &&
              tooltip.Contains("RequireGameObjectTemplate(candidate)", StringComparison.Ordinal) &&
              tooltip.Contains("TryShowWorldGameObjectGameTooltip", StringComparison.Ordinal),
            "world GameObject picker/use/tooltip/cursor wiring drift");

        Check((ushort)Op.CMSG_TOGGLE_PVP == 0x0253 &&
              WorldSession.BuildTogglePvpBody().Length == 0,
            "CMSG_TOGGLE_PVP opcode or empty-body contract drift");

        Check(UnitFrameUiLaw.PvpIcon(1, UnitFrameUiLaw.UnitFlagPvp, 0) ==
                  @"Interface\TargetingFrame\UI-PVP-Alliance" &&
              UnitFrameUiLaw.PvpIcon(2, UnitFrameUiLaw.UnitFlagPvp, 0) ==
                  @"Interface\TargetingFrame\UI-PVP-Horde" &&
              UnitFrameUiLaw.PvpIcon(1, UnitFrameUiLaw.UnitFlagPvp,
                  UnitFrameUiLaw.PlayerFlagFfaPvp) ==
                  @"Interface\TargetingFrame\UI-PVP-FFA" &&
              UnitFrameUiLaw.PvpIcon(1, 0, 0) is null,
            "PlayerFrame/TargetFrame PvP icon branch order drift");

        Check(UnitFrameUiLaw.TargetFrameTexture(0) ==
                  @"Interface\TargetingFrame\UI-TargetingFrame" &&
              UnitFrameUiLaw.TargetFrameTexture(1) ==
                  @"Interface\TargetingFrame\UI-TargetingFrame-Elite" &&
              UnitFrameUiLaw.TargetFrameTexture(2) ==
                  @"Interface\TargetingFrame\UI-TargetingFrame-Elite" &&
              UnitFrameUiLaw.TargetFrameTexture(3) ==
                  @"Interface\TargetingFrame\UI-TargetingFrame-Elite" &&
              UnitFrameUiLaw.TargetFrameTexture(4) ==
                  @"Interface\TargetingFrame\UI-TargetingFrame-Rare",
            "TargetFrame classification border mapping drift");

        Check(UnitFrameUiLaw.Status(UnitFrameUiLaw.PlayerFlagResting, true, true) ==
                  PlayerFrameStatus.Resting &&
              UnitFrameUiLaw.Status(0, true, true) == PlayerFrameStatus.Attacking &&
              UnitFrameUiLaw.Status(0, false, true) == PlayerFrameStatus.HateList &&
              UnitFrameUiLaw.StatusPulse(0) == 1f &&
              UnitFrameUiLaw.StatusPulse(.5) == 55f / 255f &&
              UnitFrameUiLaw.StatusPulse(1) == 1f,
            "PlayerFrame resting/combat status priority or pulse drift");

        // MSUI's own layer over that reference priority, and its ONE deviation: the resting icon
        // stands down at the cap. Reported 2026-08-30 - PlayerFrame.lua's IsResting() has no
        // level test, so 1.12 shows the Zzz in an inn at 60 for a rested bonus that cannot exist.
        Check(UnitFrameUiLaw.MaxPlayerLevel == 60 &&
              UnitFrameUiLaw.VisibleStatus(PlayerFrameStatus.Resting, 59) ==
                  PlayerFrameStatus.Resting &&
              UnitFrameUiLaw.VisibleStatus(PlayerFrameStatus.Resting, 60) ==
                  PlayerFrameStatus.None &&
              UnitFrameUiLaw.VisibleStatus(PlayerFrameStatus.Attacking, 60) ==
                  PlayerFrameStatus.Attacking &&
              UnitFrameUiLaw.VisibleStatus(PlayerFrameStatus.HateList, 60) ==
                  PlayerFrameStatus.HateList,
            "the max-level rest-icon stand-down drifted, or it swallowed a combat state");

        // The level number and the state icon are CONCENTRIC in the reference - PlayerLevelText
        // is CENTER (-63,-16) = (53,66) on the 232x100 frame, PlayerRestIcon is TOPLEFT (37,-49)
        // at 31x33, centre (52.5,65.5) - and the rest quadrant is only 7% opaque, so both drawn
        // is mush rather than an occlusion. Exactly one of them may hold the slot.
        Check(UnitFrameUiLaw.ShowsLevelText(PlayerFrameStatus.None) &&
              !UnitFrameUiLaw.ShowsLevelText(PlayerFrameStatus.Resting) &&
              !UnitFrameUiLaw.ShowsLevelText(PlayerFrameStatus.Attacking) &&
              !UnitFrameUiLaw.ShowsLevelText(PlayerFrameStatus.HateList),
            "the level number and a state icon can share the (53,66) slot again");

        string frames = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Hud", "GameLoop.UnitFrames.cs"));
        Check(frames.Contains("UnitFrameUiLaw.VisibleStatus(", StringComparison.Ordinal) &&
              frames.Contains("UnitFrameUiLaw.ShowsLevelText(playerStatus)", StringComparison.Ordinal),
            "the player frame stopped routing its state icon and level text through one decision");

        string runtime = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Hud", "GameLoop.UnitPopup.cs"));
        Check(runtime.Contains("_net?.GroupLootMethod(method, master, _partyLootThreshold);",
                  StringComparison.Ordinal) &&
              runtime.Contains("_net?.SetRaidTarget(checked((byte)(requested - 1)), _unitPopupGuid);",
                  StringComparison.Ordinal) &&
              runtime.Contains("case UnitPopupRow.ClaimLead:", StringComparison.Ordinal) &&
              runtime.Contains("RequestPartyLeadClaim();", StringComparison.Ordinal),
            "UnitPopup level-2 loot or raid-target action is not wired");
        Check(runtime.Contains("UnitPopupUiLaw.MenuBackdropFillTint",
                  StringComparison.Ordinal) &&
              runtime.Contains("UnitPopupUiLaw.MenuBackdropEdgeTint",
                  StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(\"igMainMenuOpen\")", StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(\"UChatScrollButton\")", StringComparison.Ordinal) &&
              !runtime.Contains("dl.AddRectFilled(origin, origin + size * s, 0xee080808",
                  StringComparison.Ordinal) &&
              !runtime.Contains("VanillaButton(dl, $\"##unit-popup", StringComparison.Ordinal),
            "UnitPopup regressed from the vanilla MENU-mode backdrop/text rows to the black card");
        Check(runtime.Contains(
                  "_unitPopupAutoCloseAt = now + UnitPopupUiLaw.AutoCloseSeconds;",
                  StringComparison.Ordinal) &&
              runtime.Contains("bool clickedOutside =", StringComparison.Ordinal),
            "UnitPopup hover-away timeout or click-away dismissal wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
