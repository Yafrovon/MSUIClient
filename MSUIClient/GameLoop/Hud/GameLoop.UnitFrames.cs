using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _pvpFeedbackOwner;
    private string? _pvpFeedbackIcon;

    private void UpdatePlayerPvpFeedback(ulong owner, string? icon)
    {
        if (_pvpFeedbackOwner != owner)
        {
            _pvpFeedbackOwner = owner;
            _pvpFeedbackIcon = icon;
            return;
        }
        if (icon != _pvpFeedbackIcon && icon is not null)
            PlayUiSound("igPVPUpdate", "ui.pvp");
        _pvpFeedbackIcon = icon;
    }
    /// <summary>Resolved logical origin of the PlayerFrame (HUD layout registry, PLAN_21); the
    /// bot-bar hover test and the party chain rail measure from it.</summary>
    private Vector2 _playerFrameOrigin = new(-19, 4);

    /// <summary>The build-5875 PlayerFrame/TargetFrame geometry and authored texture.</summary>
    private void DrawVanillaUnitFrame(WorldEntity unit, Vector2 authoredOrigin, bool playerFrame,
        string name, FactionReaction reaction, uint portraitTexture, float combatFlash)
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 size = new(232, 100);
        // The root frame is 232x100. The target's aura children deliberately
        // extend below it and ImGui needs a taller transparent host to avoid clipping.
        Vector2 windowSize = playerFrame ? size : new Vector2(232, 110);
        // The authored TOPLEFT origin is the frame's default placement in the HUD layout
        // registry; the player's own position, if any, comes back through it, and everything
        // below - art, hit host, parity rows - keys off the resolved origin.
        HudFrameResult unitFrame = HudFrame(playerFrame ? "player-frame" : "target-frame",
            playerFrame ? "Player frame" : "Target frame",
            HudPlacement.At(HudAnchor.TopLeft, authoredOrigin.X, authoredOrigin.Y), windowSize);
        authoredOrigin = unitFrame.LogicalOrigin;
        if (playerFrame) _playerFrameOrigin = authoredOrigin;
        if (unitFrame.Hidden) return;   // hidden in the active HUD layout (Edit Mode's Hide)
        Vector2 p = authoredOrigin * s;
        CollectGameplayLayout(playerFrame ? "player-frame" : "target-frame",
            authoredOrigin.X, authoredOrigin.Y, windowSize.X, windowSize.Y, p, windowSize * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        // PlayerFrame/TargetFrame are BACKGROUND-LOW portrait art and must never rise over a
        // UIPanel. NoBringToFrontOnFocus pins them to the display-back furniture band alongside
        // their own ##player-frame-hit / ##target-frame-hit hosts; without it the target frame
        // re-appears on every target change, and ImGui raises a re-appearing window to the
        // absolute display front - over whatever panel the player already had open. These windows
        // are NoInputs, so pinning them costs no click.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
                                 ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin(playerFrame ? "##vanilla-player-frame" : "##vanilla-target-frame", flags))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        string parityPanel = playerFrame ? "player-frame" : "target-frame";
        if (_uiParityArmed && _uiParityPanel == parityPanel)
            BeginUiParityFrame(p, s);

        string root = playerFrame ? "PlayerFrame" : "TargetFrame";
        if (_uiParityArmed && _uiParityPanel == parityPanel)
            CollectUiParityDraw(root, "Button", p, size * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", authoredOrigin.X, authoredOrigin.Y));

        float barX = playerFrame ? 106f : 7f;
        Vector2 troughMin = p + new Vector2(barX, 22) * s;
        const uint troughColor = 0x80000000;
        Vector2 troughSize = new Vector2(119, 41) * s;
        dl.AddRectFilled(troughMin, troughMin + troughSize, troughColor);
        if (_uiParityArmed && _uiParityPanel == parityPanel)
            CollectUiParityDraw(playerFrame ? "PlayerFrameBackground" : "TargetFrameBackground", "Texture",
                troughMin, troughSize, root, new("", troughColor, "IMGUI_RECT_FILLED", "TOPLEFT", root, "TOPLEFT", barX, -22));

        if (!playerFrame)
        {
            string platePath = @"Interface\TargetingFrame\UI-TargetingFrame-LevelBackground";
            uint plate = _gameplayArt.Handle(platePath);
            if (plate != 0)
            {
                uint tint = ReactionColorU32(reaction, unit.IsPlayer, unit.IsDead);
                dl.AddImage((nint)plate, troughMin, troughMin + new Vector2(119, 19) * s,
                    Vector2.Zero, Vector2.One, tint);
                if (_uiParityArmed && _uiParityPanel == parityPanel)
                    CollectUiParityDraw("TargetFrameNameBackground", "Texture", troughMin, new Vector2(119, 19) * s,
                        root, new(platePath, tint, "IMGUI_IMAGE", "TOPRIGHT", root, "TOPRIGHT", -106, -22));
            }
        }

        float portraitX = playerFrame ? 42f : 126f;
        Vector2 portraitMin = p + new Vector2(portraitX, 12) * s;
        DrawUnitPortraitImage(dl, unit, portraitMin, 64f * s, portraitTexture, playerFrame);
        if (_uiParityArmed && _uiParityPanel == parityPanel)
            CollectUiParityDraw(playerFrame ? "PlayerPortrait" : "TargetPortrait", "Texture", portraitMin,
                new Vector2(64) * s, root, new("", 0xffffffff, "IMGUI_IMAGE", playerFrame ? "TOPLEFT" : "TOPRIGHT",
                    root, playerFrame ? "TOPLEFT" : "TOPRIGHT", playerFrame ? 42 : -42, -12));

        DrawVanillaStatusBar(dl, p + new Vector2(barX, 41) * s, new Vector2(119, 12) * s,
            unit.HealthFraction, new Vector4(0, 1, 0, 1));
        if (unit.Fields.ActiveMaxPower > 0)
            DrawVanillaStatusBar(dl, p + new Vector2(barX, 52) * s, new Vector2(119, 12) * s,
                unit.PowerFraction, PowerColor(unit.Fields.PowerType));

        uint creatureRank = 0;
        if (!playerFrame && unit.IsCreature &&
            _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? creatureInfo) &&
            creatureInfo is not null)
            creatureRank = creatureInfo.Rank;
        string framePath = playerFrame
            ? @"Interface\TargetingFrame\UI-TargetingFrame"
            : UnitFrameUiLaw.TargetFrameTexture(creatureRank);
        uint frame = _gameplayArt.Handle(framePath);
        if (PainterlyUi)
        {
            // Square chrome. The authored UI-TargetingFrame is a round gilded
            // ring; over a painted world it reads as a different game, and its
            // transparent corners are the only reason the portrait bake has to
            // be circle-cut at all (see Program.Portraits). Drawing the frame
            // from primitives keeps both square and needs no new art.
            DrawSquarePanel(dl, p + new Vector2(portraitX, 12) * s, new Vector2(64) * s, s);
            DrawSquarePanel(dl, troughMin, troughSize, s);
        }
        else if (frame != 0)
        {
            Vector2 uv0 = playerFrame ? new Vector2(1f, 0) : new Vector2(0.09375f, 0);
            Vector2 uv1 = playerFrame ? new Vector2(0.09375f, 0.78125f) : new Vector2(1f, 0.78125f);
            dl.AddImage((nint)frame, p, p + size * s, uv0, uv1);
            if (_uiParityArmed && _uiParityPanel == parityPanel)
                CollectUiParityDraw(playerFrame ? "PlayerFrameTexture" : "TargetFrameTexture", "Texture", p,
                    size * s, playerFrame ? "PlayerFrame/Frame/Frame" : "TargetFrameTextureFrame",
                        new(framePath, 0xffffffff, "IMGUI_IMAGE", "ANCHOR:ABSOLUTE", "", "", authoredOrigin.X, authoredOrigin.Y));
        }

        // Class-colour ring on your own gilded portrait (issue #15), matching the party frames.
        if (playerFrame && !PainterlyUi && unit.IsPlayer)
            DrawClassPortraitRing(dl, p + new Vector2(portraitX + 32f, 44f) * s, 32f * s,
                unit.Guid, s);

        // Resolved once, here, because the LEVEL TEXT below depends on it: a state icon and the
        // level number occupy the same (53,66) in the reference, so exactly one of them draws.
        PlayerFrameStatus playerStatus = playerFrame
            ? UnitFrameUiLaw.VisibleStatus(
                UnitFrameUiLaw.Status(unit.Fields.PlayerFlags, _attackTargetGuid != 0,
                    unit.InCombat), unit.Level)
            : PlayerFrameStatus.None;
        if (playerFrame && !PainterlyUi)
            DrawPlayerFrameStatus(dl, unit, p, s, playerStatus);
        if (playerFrame) DrawPlayerFrameGroupIcons(dl, unit, p, s);

        string? pvpPath = UnitFrameUiLaw.PvpIcon(unit.Fields.Bytes0.Race,
            unit.Fields.UnitFlags, unit.Fields.PlayerFlags);
        if (playerFrame && !HudPreview) UpdatePlayerPvpFeedback(unit.Guid, pvpPath);
        if (pvpPath is not null)
        {
            Vector2 pvpMin = p + new Vector2(playerFrame ? 18f : 171f, 20f) * s;
            uint pvpTexture = _gameplayArt.Handle(pvpPath);
            if (pvpTexture != 0)
                dl.AddImage((nint)pvpTexture, pvpMin, pvpMin + new Vector2(64f) * s);
            if (_uiParityArmed && _uiParityPanel == parityPanel)
                CollectUiParityDraw(playerFrame ? "PlayerPVPIcon" : "TargetPVPIcon",
                    "Texture", pvpMin, new Vector2(64f) * s, root,
                    new(pvpPath, 0xffffffff, "ARTWORK",
                        playerFrame ? "TOPLEFT" : "TOPRIGHT", root,
                        playerFrame ? "TOPLEFT" : "TOPRIGHT", playerFrame ? 18 : 3, -20));
        }

        // TargetFrame_UpdateRaidTargetIcon: the reference shows the mark on the TARGET frame
        // only — PlayerFrame and PartyMemberFrame have no raid-mark texture in build 5875.
        byte raidMark = playerFrame
            ? (byte)0 : GroupUiLaw.RaidTargetIndex(_partyRaidTargets, unit.Guid);
        if (raidMark > 0)
        {
            RaidMarkerRect markRect = RaidMarkerUiLaw.TargetFrameRect(p, s);
            uint markTexture = _gameplayArt.Handle(RaidMarkerUiLaw.Texture);
            if (markTexture != 0)
            {
                RaidMarkerUv markUv = RaidMarkerUiLaw.AtlasUv(raidMark);
                dl.AddImage((nint)markTexture, markRect.Min, markRect.Max, markUv.Min, markUv.Max);
            }
            if (_uiParityArmed && _uiParityPanel == parityPanel)
                CollectUiParityDraw("TargetRaidTargetIcon", "Texture", markRect.Min,
                    new Vector2(RaidMarkerUiLaw.TargetFrameSize) * s, root,
                    new(RaidMarkerUiLaw.Texture, 0xffffffff, "ARTWORK", "CENTER", root,
                        "TOPRIGHT", -73, -14));
        }

        Vector2 nameCenter = p + new Vector2(playerFrame ? 166 : 66, 31) * s;
        uint nameColor = UiGoldU32();
        (Vector2 nameMin, Vector2 nameSize) = DrawUnitFrameText(dl, nameCenter, name, 10f * s, nameColor);
        if (_uiParityArmed && _uiParityPanel == parityPanel)
            CollectUiParityDraw(playerFrame ? "PlayerName" : "TargetName", "FontString", nameMin, nameSize,
                playerFrame ? "PlayerFrame/Frame/Frame" : "TargetFrameTextureFrame",
                new("", nameColor, "IMGUI_TEXT", "ANCHOR:ABSOLUTE", "", "", nameMin.X / s, nameMin.Y / s, "", 10));
        Vector2 levelCenter = p + new Vector2(playerFrame ? 53 : 179, 66) * s;
        bool showLevel = unit.Level > 0 &&
            (!playerFrame || UnitFrameUiLaw.ShowsLevelText(playerStatus));
        if (showLevel)
        {
            // TargetFrame_CheckLevel (1.12 FrameXML): an attackable target's level takes GetDifficultyColor
            // against the acting body's level (red/orange/yellow/green/grey); anything else is gold.
            // Never the reaction colour - that painted a level-36 mob red for a level-60 player.
            uint levelColor = UiGoldU32();
            if (!playerFrame && CanActorAttack(unit, ControlledGuid) &&
                _entities.TryGet(ControlledGuid, out WorldEntity actingBody))
                levelColor = ImGui.ColorConvertFloat4ToU32(QuestFrameUiLaw.QuestDifficultyColor(actingBody.Level, unit.Level));
            (Vector2 levelMin, Vector2 levelSize) = DrawUnitFrameText(dl, levelCenter, unit.Level.ToString(), 10f * s, levelColor);
            if (_uiParityArmed && _uiParityPanel == parityPanel)
                CollectUiParityDraw(playerFrame ? "PlayerLevelText" : "TargetLevelText", "FontString", levelMin, levelSize,
                    playerFrame ? "PlayerFrame/Frame/Frame" : "TargetFrameTextureFrame",
                    new("", levelColor, "IMGUI_TEXT", "ANCHOR:ABSOLUTE", "", "", levelMin.X / s, levelMin.Y / s, "", 10));
        }
        else if (!playerFrame && unit.Level == 0)
            DrawArt(dl, @"Interface\TargetingFrame\UI-TargetingFrame-Skull",
                levelCenter - new Vector2(8) * s, new Vector2(16), s);

        if (!playerFrame && unit.IsDead)
            DrawUnitFrameText(dl, p + new Vector2(66, 47) * s, "DEAD", 10f * s, UiGoldU32());

        if (combatFlash > 0)
            dl.AddCircle(portraitMin + new Vector2(32) * s, 29f * s,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.12f, 0.08f,
                    Math.Clamp(combatFlash / 0.35f, 0, 1))), 48, 2f * s);
        if (!playerFrame)
        {
            DrawComboFrame(dl, p, s);
        }
        if (_uiParityArmed && _uiParityPanel == parityPanel)
        {
            string[] absent = playerFrame
                ? ["PlayerFrame/Frame", "PlayerFrame/Frame/Frame", "PlayerFrameHealthBarText", "PlayerFrameManaBarText",
                    "PlayerHitIndicator", "PlayerPVPIconHitArea",
                    "PlayerPlayTime", "PlayerPlayTimeIcon", "PlayerFrameDropDown",
                    ]
                : ["TargetFrameTextureFrame", "TargetDeadText", "TargetHighLevelTexture",
                    "TargetFrameDropDown", .. Enumerable.Range(1,16).Select(i=>$"TargetFrameDebuff{i}"),
                    .. Enumerable.Range(1,5).Select(i=>$"TargetFrameBuff{i}")];
            foreach (string element in absent) ClassifyUiParity(element, "", root, "NOT-DRAWN");
            if (pvpPath is null)
                ClassifyUiParity(playerFrame ? "PlayerPVPIcon" : "TargetPVPIcon",
                    "Texture", root, "NOT-DRAWN", "unit-is-not-pvp-flagged");
            if (!playerFrame && raidMark == 0)
                ClassifyUiParity("TargetRaidTargetIcon", "Texture", root, "NOT-DRAWN",
                    "unit-carries-no-raid-mark");
            MarkUiParityFrameComplete();
        }
        ImGui.End();
        DrawUnitFrameHitRect(unit, authoredOrigin, playerFrame, s);
        if (!playerFrame) DrawTargetAuras(unit, p, s);
    }

    /// <summary>
    /// The status is resolved by the CALLER (DrawVanillaUnitFrame) because the level number
    /// shares this icon's slot and the two decisions have to agree. PLAYER_REGEN_DISABLED /
    /// ENABLED is derived from UNIT_FLAG_IN_COMBAT in current Benilla.
    /// </summary>
    /// <summary>
    /// PlayerFrame.xml's own PlayerLeaderIcon and PlayerMasterIcon, 16x16, anchored TOPLEFT at
    /// (44,-10) and (80,-10). FrameXML measures Y UPWARD, so those authored -10s are ten pixels
    /// DOWN the frame, not ten above its top edge - anchored the other way both icons drew off
    /// the top of the screen and nothing appeared at all.
    ///
    /// The party frames already drew both for OTHER members, so the one person who could never
    /// see that they held the crown or the loot master role was the player holding it. There is
    /// no chat line to fall back on: 1.12 ships ERR_SET_LOOT_MASTER / ERR_NEW_LOOT_MASTER_S but
    /// no FrameXML file prints them, and vmangos answers a loot-method change with SMSG_GROUP_LIST
    /// alone. These icons ARE the confirmation.
    /// </summary>
    private void DrawPlayerFrameGroupIcons(ImDrawListPtr dl, WorldEntity player, Vector2 frameMin,
        float scale)
    {
        if (!_partyInGroup) return;
        var size = new Vector2(16) * scale;
        void Icon(string path, float x, bool shown)
        {
            if (!shown) return;
            uint art = _gameplayArt?.Handle(path) ?? 0;
            if (art == 0) return;
            Vector2 min = frameMin + new Vector2(x, 10) * scale;
            dl.AddImage((nint)art, min, min + size);
        }
        Icon(@"Interface\GroupFrame\UI-Group-LeaderIcon", 44f, player.Guid == _partyLeaderGuid);
        Icon(@"Interface\GroupFrame\UI-Group-MasterLooter", 80f,
            _partyLootMethod == 2 && player.Guid == _partyMasterLooterGuid);
    }

    private void DrawPlayerFrameStatus(ImDrawListPtr dl, WorldEntity player, Vector2 frameMin,
        float scale, PlayerFrameStatus status)
    {
        if (status == PlayerFrameStatus.None) return;

        bool resting = status == PlayerFrameStatus.Resting;
        bool attacking = status == PlayerFrameStatus.Attacking;
        bool pulsing = resting || attacking;
        float alpha = pulsing ? UnitFrameUiLaw.StatusPulse(NowSeconds()) : 1f;
        if (pulsing)
        {
            string ringPath = @"Interface\CharacterFrame\UI-Player-Status";
            uint ring = _gameplayArt!.AdditiveHandle(ringPath);
            Vector2 ringMin = frameMin + new Vector2(35, 8) * scale;
            Vector4 ringColor = resting
                ? new Vector4(1f, .88f, .25f, alpha)
                : new Vector4(1f, 0f, 0f, alpha);
            if (ring != 0)
                dl.AddImage((nint)ring, ringMin, ringMin + new Vector2(190, 66) * scale,
                    Vector2.Zero, new Vector2(.74609375f, .53125f),
                    ImGui.ColorConvertFloat4ToU32(ringColor));
        }

        if (attacking)
        {
            uint background = _gameplayArt!.Handle(
                @"Interface\TargetingFrame\UI-TargetingFrame-AttackBackground");
            Vector2 backgroundMin = frameMin + new Vector2(37, 50) * scale;
            if (background != 0)
                dl.AddImage((nint)background, backgroundMin,
                    backgroundMin + new Vector2(32) * scale, Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.8f, .1f, .1f, .4f)));
        }

        // TWO kinds of art in one 64x64 sheet. The top half is the Zzz / crossed-swords icons on
        // ordinary alpha; the BOTTOM half is their glow, which PlayerFrame.xml declares
        // alphaMode="ADD" on PlayerRestGlow and PlayerAttackGlow. That half measures 100% opaque
        // with 71% of it flat black, so drawing it straight painted a hard black 32x32 square
        // centred on (53,66) - exactly where the level number and the Zzz icon sit.
        //
        // The sheet's own header says alphaDepth 8 (because of the icons), so AdditiveHandle's
        // header test would rightly decline to touch it. AdditiveRegionHandle re-encodes ONLY the
        // ADD half and leaves the icon half byte-identical, so one texture serves both draws.
        uint state = _gameplayArt!.AdditiveRegionHandle(
            @"Interface\CharacterFrame\UI-StateIcon", new Vector2(0f, 0.5f), Vector2.One);
        Vector2 iconMin = frameMin + new Vector2(resting ? 37 : 38, resting ? 49 : 50) * scale;
        Vector2 iconSize = new(resting ? 31 : 32, resting ? 33 : 32);
        Vector2 uv0 = resting ? Vector2.Zero : new Vector2(.5f, 0f);
        Vector2 uv1 = resting ? new Vector2(.5f, .421875f) : new Vector2(1f, .5f);
        if (state != 0)
            dl.AddImage((nint)state, iconMin, iconMin + iconSize * scale, uv0, uv1);

        if (pulsing && state != 0)
        {
            Vector2 glowUv0 = resting ? new Vector2(0f, .5f) : new Vector2(.5f, .5f);
            Vector2 glowUv1 = resting ? new Vector2(.5f, 1f) : Vector2.One;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));
            dl.AddImage((nint)state, iconMin, iconMin + new Vector2(32) * scale,
                glowUv0, glowUv1, tint);
        }
    }

    /// <summary>
    /// The frame's authored HitRectInsets as a transparent input window — the art window stays
    /// NoInputs so the dead margin keeps letting clicks through to the world. PlayerFrame
    /// insets are 6/0/4/9 and TargetFrame's 96/6/4/9 (their FrameXML), and click-up acts like
    /// PlayerFrame_OnClick / TargetFrame_OnClick.
    /// </summary>
    private void DrawUnitFrameHitRect(WorldEntity unit, Vector2 authoredOrigin, bool playerFrame,
        float s)
    {
        Vector2 hitOffset = playerFrame ? new Vector2(6, 4) : new Vector2(96, 4);
        Vector2 hitSize = playerFrame ? new Vector2(226, 87) : new Vector2(130, 87);
        Vector2 hitMin = (authoredOrigin + hitOffset) * s;
        ImGui.SetNextWindowPos(hitMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(hitSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
        bool begun = ImGui.Begin(playerFrame ? "##player-frame-hit" : "##target-frame-hit", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImGui.SetCursorScreenPos(hitMin);
        bool released = ImGui.InvisibleButton(
            playerFrame ? "##player-frame-click" : "##target-frame-click", hitSize * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        // Hovercast reads the authored HitRect, not the art window: the dead margin that
        // lets clicks through to the world must not count as hovering the unit either.
        NoteHovercastFrameHover(unit.Guid, ImGui.IsItemHovered());
        ImGui.End();
        if (!released) return;
        if (_rtsUnitCastSpellId != 0)
        {
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                CancelRtsUnitCastTargeting(silent: false);
            else
                TryCommitRtsUnitCastTarget(unit.Guid);
            return;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            // Ref anchor: the DropDownList TOPLEFT sits (106,27) / (120,10) up from the frame's
            // BOTTOMLEFT (the ToggleDropDownMenu calls in PlayerFrame/TargetFrame OnClick).
            Vector2 anchor = playerFrame ? new Vector2(106, 100 - 27) : new Vector2(120, 100 - 10);
            if (UnitFrameMenuWhich(unit) is { } which)
                OpenUnitPopup(unit.Guid, which, (authoredOrigin + anchor) * s,
                    InspectBinding.Target);
        }
        else if (playerFrame)
            CommitSelection(unit.Guid, beginAttack: false); // TargetUnit("player")
    }

    /// <summary>
    /// TargetFrameDropDown_Initialize's menu pick. The reference's non-player branch opens
    /// RAID_TARGET_ICON and nothing else, which is how a mob gets marked at all.
    /// </summary>
    private UnitPopupWhich? UnitFrameMenuWhich(WorldEntity unit)
    {
        // ControlledGuid, not PlayerGuid: a possessed bot's own portrait is the SELF menu.
        if (_net is not null && unit.Guid == ControlledGuid) return UnitPopupWhich.Self;
        if (!unit.IsPlayer) return UnitPopupWhich.Target;
        return _partyMembers.Any(member => member.Guid == unit.Guid)
            ? UnitPopupWhich.Party : UnitPopupWhich.Player;
    }

    private static (Vector2 Min, Vector2 Size) DrawUnitFrameText(ImDrawListPtr dl, Vector2 center,
        string? text, float size, uint color)
    {
        // A missing name draws nothing rather than taking the client down.
        // ImGui.CalcTextSize throws ArgumentNullException on null, so every
        // unnamed unit - one whose name query has not come back yet, or a
        // synthetic one - was a crash waiting on the player frame.
        if (string.IsNullOrEmpty(text)) return (center, Vector2.Zero);

        // Exact-size FRIZQT from the baked atlas, never the ImGui default font (game UI never
        // uses the ImGui font). The size-ratio shadow and WowSkin outline are kept as-is; only
        // the font glyph source changes.
        int em = Math.Max(2, (int)MathF.Round(size));
        if (!GameTextLaw.TryGetFont(FontFace.FrizQt, em, false,
                out ImFontPtr font, out float drawSize))
            return (center, Vector2.Zero);
        Vector2 measured = new(GameText.MeasurePlain(text, size, 1f), size);
        Vector2 pos = center - measured * 0.5f;
        float shadow = MathF.Max(1f, MathF.Round(size * GlueTune.ShadowOffsetRatio));
        dl.AddText(font, drawSize, pos + new Vector2(shadow),
            ImGui.ColorConvertFloat4ToU32(GlueTune.ShadowColor), text);
        WowSkin.OutlineText(dl, font, drawSize, pos, text);
        dl.AddText(font, drawSize, pos, color, text);
        return (pos, measured);
    }

    private void DrawPlayerAuraBar()
    {
        if (_net is null || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 logicalDisplay = display / s;
        Vector2 frameMin = BuffUiLaw.FrameMin(logicalDisplay);
        Vector2 framePhysical = frameMin * s;
        // Own the buff region with a real transparent ImGui window. Raw screen-rectangle tests
        // are not reliable when another HUD window owns hover; an InvisibleButton per icon gives
        // tooltip and right-click cancellation the same input semantics as every other UI button.
        Vector2 windowMin = BuffUiLaw.AuraWindowMin(frameMin) * s;
        Vector2 windowSize = new Vector2(BuffUiLaw.AuraWindowWidth,
            BuffUiLaw.AuraWindowHeight) * s;
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
        // This window is an input owner, not layout chrome. Default ImGui padding/border clipped
        // the left-most icon/border and narrowed its hit rectangle. Zero them so the measured
        // window rectangle is the actual clip rectangle.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool began = ImGui.Begin("##player-aura-bar", flags);
        ImGui.PopStyleVar(2);
        if (!began)
        {
            ImGui.End();
            return;
        }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool parityCapture = _uiParityArmed && _uiParityPanel == "buff-frame";
        Vector4 windowClip = new(windowMin.X, windowMin.Y,
            windowMin.X + windowSize.X, windowMin.Y + windowSize.Y);
        if (parityCapture)
        {
            BeginUiParityFrame(framePhysical, s);
            CollectUiParityDraw("BuffFrame", "Frame", framePhysical,
                new Vector2(BuffUiLaw.FrameWidth, BuffUiLaw.FrameHeight) * s, "",
                new("", 0, "FRAME", "TOPRIGHT", "UIParent", "TOPRIGHT",
                    -BuffUiLaw.FrameRightInset, -BuffUiLaw.FrameTopInset,
                    ContentRect: new Vector4(framePhysical.X, framePhysical.Y,
                        framePhysical.X + BuffUiLaw.FrameWidth * s,
                        framePhysical.Y + BuffUiLaw.FrameHeight * s),
                    ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                    Visible: true, Strata: "LOW"));
        }
        int shown = 0, buffShown = 0, debuffShown = 0;
        HashSet<int> drawnButtons = [];
        AuraSnapshot? hoveredAura = null;
        SpellInfo? hoveredSpell = null;
        AuraTimer? hoveredTimer = null;
        int hoveredButtonIndex = -1;
        double now = NowSeconds();
        foreach (AuraSnapshot aura in OrderedAuras(player))
        {
            if (!TryVisibleAuraSpell(aura.SpellId, out SpellInfo? spell)) continue;
            uint icon = _gameplayArt.Handle(spell?.IconPath ?? "");
            if (icon == 0) continue;
            bool harmful = aura.Slot >= 32;
            int cohort = harmful ? debuffShown++ : buffShown++;
            if (harmful ? cohort >= BuffUiLaw.HarmfulLimit : cohort >= BuffUiLaw.HelpfulLimit)
                continue;
            int buttonIndex = harmful ? BuffUiLaw.HelpfulLimit + cohort : cohort;
            drawnButtons.Add(buttonIndex);
            Vector2 min = BuffUiLaw.ButtonMin(frameMin, harmful, cohort) * s;
            Vector2 max = min + new Vector2(BuffUiLaw.ButtonSize) * s;
            AuraTimer? activeTimer = null;
            double remaining = double.PositiveInfinity;
            if (TryPlayerAuraTimer(aura, out AuraTimer timer))
            {
                activeTimer = timer;
                remaining = Math.Max(0, timer.Expires - now);
            }
            byte alpha = (byte)Math.Clamp(MathF.Round(
                BuffUiLaw.WarningAlpha(now, remaining) * 255f), 0, 255);
            ImGui.SetCursorScreenPos(min);
            bool cancelReleased = ImGui.InvisibleButton(
                $"##player-aura-{aura.Slot}-{aura.SpellId}", max - min,
                ImGuiButtonFlags.MouseButtonRight);
            bool itemHovered = ImGui.IsItemHovered();

            string button = $"BuffButton{buttonIndex}";
            if (parityCapture)
            {
                string point, relativeTo, relativePoint;
                float offsetX, offsetY;
                if (buttonIndex == 0)
                    (point, relativeTo, relativePoint, offsetX, offsetY) =
                        ("TOPRIGHT", "BuffFrame", "TOPRIGHT", 0, 0);
                else if (buttonIndex == BuffUiLaw.Columns)
                    (point, relativeTo, relativePoint, offsetX, offsetY) =
                        ("TOP", "BuffButton0", "BOTTOM", 0, -BuffUiLaw.DurationGutter);
                else if (buttonIndex == BuffUiLaw.HelpfulLimit)
                    (point, relativeTo, relativePoint, offsetX, offsetY) =
                        ("TOPRIGHT", $"BuffButton{BuffUiLaw.Columns}", "BOTTOMRIGHT", 0,
                            -BuffUiLaw.DurationGutter);
                else
                    (point, relativeTo, relativePoint, offsetX, offsetY) =
                        ("RIGHT", $"BuffButton{buttonIndex - 1}", "LEFT", -5, 0);
                CollectUiParityDraw(button, "Button", min, max - min, "BuffFrame",
                    new("", 0, "FRAME", point, relativeTo, relativePoint, offsetX, offsetY,
                        ContentRect: new Vector4(min.X, min.Y, max.X, max.Y),
                        ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                        Visible: true, Enabled: true,
                        InteractionState: itemHovered ? "hovered" : "normal",
                        HitMin: min, HitMax: max, Strata: "LOW"));
            }

            string iconPath = string.IsNullOrWhiteSpace(spell?.IconPath)
                ? @"Interface\Icons\INV_Misc_QuestionMark" : spell.Value.IconPath;
            uint iconColor = (uint)(alpha << 24) | 0x00ffffffu;
            dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
                iconColor);
            if (parityCapture)
                CollectUiParityDraw(button + "Icon", "Texture", min, max - min, button,
                    new(iconPath, iconColor, "BACKGROUND", "TOPLEFT", button, "TOPLEFT", 0, 0,
                        TexCoords: "0|0|1|1",
                        ContentRect: new Vector4(min.X, min.Y, max.X, max.Y),
                        ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                        BlendMode: "BLEND", Visible: true,
                        InteractionState: remaining < BuffUiLaw.WarningSeconds
                            ? "warning-pulse" : activeTimer is null ? "permanent" : "timed",
                        Strata: "LOW"));
            if (activeTimer is not null)
            {
                string durationText = AuraTimeText(remaining);
                Vector2 durationMin = new(min.X, max.Y + 1f * s);
                GameText.Draw(dl, "GameFontNormalSmall", durationText, durationMin, s);
                if (parityCapture)
                {
                    Vector2 durationSize = new(
                        GameText.MeasureWidth("GameFontNormalSmall", durationText, s),
                        GameText.EmPixels("GameFontNormalSmall", s));
                    CollectUiParityDraw(button + "Duration", "FontString", durationMin,
                        durationSize, "BuffFrame",
                        new("", FontObjectLaw.Get("GameFontNormalSmall").Color, "ARTWORK",
                            "TOPLEFT", button, "BOTTOMLEFT", 0, -1,
                            FontPath: FontObjectLaw.Get("GameFontNormalSmall").Face,
                            FontSize: FontObjectLaw.Get("GameFontNormalSmall").Height,
                            ContentRect: new Vector4(durationMin.X, durationMin.Y,
                                durationMin.X + durationSize.X, durationMin.Y + durationSize.Y),
                            ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                            Visible: true, Strata: "LOW"));
                }
            }
            else if (parityCapture)
                ClassifyUiParity(button + "Duration", "FontString", "BuffFrame", "NOT-DRAWN");
            if (harmful)
            {
                uint border = _gameplayArt.Handle(@"Interface\Buttons\UI-Debuff-Overlays");
                if (border != 0)
                {
                    Vector2 borderMin = min - new Vector2(BuffUiLaw.DebuffBorderExpandX,
                        BuffUiLaw.DebuffBorderExpandY) * s;
                    Vector2 borderMax = borderMin + new Vector2(BuffUiLaw.DebuffBorderWidth,
                        BuffUiLaw.DebuffBorderHeight) * s;
                    uint borderColor = ImGui.ColorConvertFloat4ToU32(
                        BuffUiLaw.DebuffColor(spell?.DispelType ?? 0));
                    dl.AddImage((nint)border, borderMin, borderMax,
                        new Vector2(BuffUiLaw.DebuffTexCoords.X, BuffUiLaw.DebuffTexCoords.Y),
                        new Vector2(BuffUiLaw.DebuffTexCoords.Z, BuffUiLaw.DebuffTexCoords.W),
                        borderColor);
                    if (parityCapture)
                        CollectUiParityDraw(button + "Border", "Texture", borderMin,
                            borderMax - borderMin, button,
                            new(@"Interface\Buttons\UI-Debuff-Overlays", borderColor, "OVERLAY",
                                "CENTER", button, "CENTER", 0, 0,
                                TexCoords: "0.296875|0|0.5703125|0.515625",
                                ContentRect: new Vector4(borderMin.X, borderMin.Y,
                                    borderMax.X, borderMax.Y),
                                ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                                BlendMode: "BLEND", Visible: true,
                                InteractionState: "dispel-border", Strata: "LOW"));
                }
            }
            if (aura.Stacks > 1)
            {
                string countText = aura.Stacks.ToString();
                Vector2 countMin = max - new Vector2(9, 13) * s;
                dl.AddText(countMin, 0xffffffff, countText);
                if (parityCapture)
                {
                    Vector2 countSize = ImGui.CalcTextSize(countText);
                    CollectUiParityDraw(button + "Count", "FontString", countMin, countSize,
                        button, new("", 0xffffffff, "BACKGROUND", "BOTTOMRIGHT", button,
                            "BOTTOMRIGHT", -2, 2, FontSize: ImGui.GetFontSize() / s,
                            ContentRect: new Vector4(countMin.X, countMin.Y,
                                countMin.X + countSize.X, countMin.Y + countSize.Y),
                            ClipRect: windowClip, ClipMask: "window:player-aura-bar",
                            Visible: true, Strata: "LOW"));
                }
            }
            else if (parityCapture)
                ClassifyUiParity(button + "Count", "FontString", button, "NOT-DRAWN");

            if (itemHovered)
            {
                hoveredAura = aura;
                hoveredSpell = spell;
                hoveredTimer = activeTimer;
                hoveredButtonIndex = buttonIndex;
            }
            if (cancelReleased)
                CancelPlayerAura(aura, "UI_RIGHT_CLICK");
            if (++shown >= 24) break;
        }
        if (parityCapture)
        {
            for (int i = 0; i < BuffUiLaw.HelpfulLimit + BuffUiLaw.HarmfulLimit; i++)
                if (!drawnButtons.Contains(i))
                    ClassifyUiParity($"BuffButton{i}", "Button", "BuffFrame", "NOT-DRAWN");
            if (shown > 0) MarkUiParityFrameComplete();
        }
        ImGui.End();

        if (hoveredAura is { } hovered && hoveredButtonIndex >= 0)
        {
            PreparedPlayerAuraTooltip prepared = PreparePlayerAuraTooltip(
                hovered, hoveredSpell, hoveredTimer, now);
            GameTooltipOwnerKey owner = PlayerAuraGameTooltipOwner(hoveredButtonIndex);
            OfferPreservedSharedGameTooltipRenderer(owner,
                () => DrawPlayerAuraTooltip(prepared));
        }
    }

    private readonly record struct PreparedPlayerAuraTooltip(
        string Title,
        string? StackLine,
        string? Description,
        string? RemainingLine,
        string? HelpfulLine);

    private static GameTooltipOwnerKey PlayerAuraGameTooltipOwner(int buttonIndex)
    {
        if ((uint)buttonIndex >= BuffUiLaw.HelpfulLimit + BuffUiLaw.HarmfulLimit)
            throw new ArgumentOutOfRangeException(nameof(buttonIndex));
        return new("player-aura-button", (ulong)buttonIndex);
    }

    private PreparedPlayerAuraTooltip PreparePlayerAuraTooltip(
        AuraSnapshot aura,
        SpellInfo? spell,
        AuraTimer? timer,
        double now)
    {
        string name = spell?.Name ?? $"Spell {aura.SpellId}";
        string rank = spell?.Rank ?? "";
        string title = string.IsNullOrWhiteSpace(rank) ? name : $"{name} ({rank})";
        string? stackLine = aura.Stacks > 1 ? $"{aura.Stacks} stacks" : null;
        string? description = null;
        if (spell is { } info && _spellCatalog is not null)
        {
            string substituted = SpellTooltipLaw.Substitute(info.Description, info,
                _spellCatalog, aura.Level);
            if (!string.IsNullOrWhiteSpace(substituted)) description = substituted;
        }

        string? remainingLine = null;
        if (timer is { } active)
        {
            double remaining = Math.Max(0, active.Expires - now);
            remainingLine = $"{AuraTimeText(remaining)} remaining";
        }
        string? helpfulLine = aura.Helpful
            ? aura.Cancelable ? "Right-click to cancel" : "Cannot be cancelled"
            : null;
        return new(title, stackLine, description, remainingLine, helpfulLine);
    }

    private static void DrawPlayerAuraTooltip(in PreparedPlayerAuraTooltip prepared)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(prepared.Title);
        if (prepared.StackLine is not null) ImGui.TextDisabled(prepared.StackLine);
        if (prepared.Description is not null)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + SpellTooltipLaw.WrapWidth);
            ImGui.TextUnformatted(prepared.Description);
            ImGui.PopTextWrapPos();
        }
        if (prepared.RemainingLine is not null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(prepared.RemainingLine);
        }
        if (prepared.HelpfulLine is not null) ImGui.TextDisabled(prepared.HelpfulLine);
        ImGui.EndTooltip();
    }

    private void DrawUnitPortraitImage(ImDrawListPtr dl, WorldEntity unit, Vector2 min, float size,
        uint liveTexture, bool playerFrame, uint tint = 0xffffffff)
    {
        uint texture = liveTexture;
        Vector2 uv0 = new(0, 1), uv1 = new(1, 0);
        // NPC interaction panels know the exact WorldEntity but historically passed texture=0,
        // which dropped every creature onto TemporaryPortrait-Monster. Right-click interaction
        // also selects that NPC, so reuse the already baked target portrait by GUID. The GUID
        // check is essential: a cached portrait for the previous target must never leak into a
        // newly opened merchant/quest/gossip frame.
        if (texture == 0 && _portraitTargetGuid == unit.Guid)
            texture = RoundAperturePortrait(_targetPortrait, _targetPortraitUsable);
        if (texture == 0)
            texture = PetPortraitHandle(unit.Guid);
        // Free view: the streamed-body booth is the authority for EVERY player
        // face (owner 2026-08-28) — the rig bake only fills in while it bakes.
        if (texture == 0 && unit.IsPlayer && (_freeView || unit.Fields.HasDisplayTransform))
            texture = PartyPortraitHandle(unit.Guid);
        // Only when the bake is actually OF this unit (PlayerPortraitCurrent) —
        // a stale bake of the previously driven bot must fall through to the
        // party booth, never be worn by whoever holds the frame now.
        if (texture == 0 && unit.IsPlayer && _net is not null &&
            unit.Guid == ControlledGuid && PlayerPortraitCurrent)
            texture = RoundAperturePortrait(_playerPortrait, _playerPortraitUsable);
        if (texture == 0 && unit.IsPlayer)
            texture = PartyPortraitHandle(unit.Guid);
        if (texture == 0 && _gameplayArt is not null)
        {
            string fallback;
            if (unit.IsPlayer)
            {
                var b = unit.Fields.Bytes0;
                string sex = b.Gender == 1 ? "Female" : "Male";
                // The streamed stand-in art uses the asset race token, which is "Scourge"
                // for the undead (benilla portrait/mod.rs temporary_portrait race map).
                string race = b.Race == 5 ? "Scourge" : RaceName(b.Race).Replace(" ", "");
                fallback = $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{race}";
            }
            else fallback = @"Interface\CharacterFrame\TemporaryPortrait-Monster";
            // Through the painterly art path: the stand-in is a flat BLP, not a
            // render target, so it is the one portrait the bake-time styling in
            // Program.Portraits can never reach.
            texture = PainterlyArt(fallback);
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
        }
        if (texture != 0)
        {
            // UI-TargetingFrame is the authored circular chrome and is drawn after this quad.
            // Its corners are TRANSPARENT (a thin ring band), so the square bake cannot hide
            // behind it: the caller passes the round copy of the bake for that chrome
            // (UnitFramePortrait -> PortraitRenderTarget.CircularTextureHandle), matching the
            // reference client's shader-side circular cut, and the square bake only when
            // painterly's square panel is what gets drawn instead. ImGui.NET's rounded-image
            // path emitted only one textured fan triangle on this backend (the face-shaped
            // wedge captured in-game), so it cannot serve as a stencil.
            dl.AddImage((nint)texture, min, min + new Vector2(size), uv0, uv1, tint);
        }
    }

    private void DrawVanillaStatusBar(ImDrawListPtr dl, Vector2 min, Vector2 size,
        float fraction, Vector4 color)
    {
        uint texture = _gameplayArt?.Handle(@"Interface\TargetingFrame\UI-StatusBar") ?? 0;
        fraction = Math.Clamp(fraction, 0, 1);
        if (texture == 0 || fraction <= 0) return;
        Vector2 max = new(min.X + size.X * fraction, min.Y + size.Y);
        dl.AddImage((nint)texture, min, max, Vector2.Zero, new Vector2(fraction, 1),
            ImGui.ColorConvertFloat4ToU32(color));
    }

    private static uint ReactionColorU32(FactionReaction reaction, bool player, bool dead)
    {
        Vector4 color = dead && !player ? new Vector4(.498f, .498f, .498f, 1)
            : player ? new Vector4(.376f, .376f, 1, 1)
            : reaction switch
            {
                FactionReaction.Hostile => new Vector4(1, 0, 0, 1),
                FactionReaction.Friendly => new Vector4(0, 1, 0, 1),
                _ => new Vector4(1, 1, 0, 1),
            };
        return ImGui.ColorConvertFloat4ToU32(color);
    }
}
