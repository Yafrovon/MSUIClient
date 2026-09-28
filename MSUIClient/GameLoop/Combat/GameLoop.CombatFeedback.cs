using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const int MaxWorldTextPerUnit = 4;

    private sealed class FloatingCombatText
    {
        public ulong Target;
        public Vector3 Anchor;
        public string Text = "";
        public WorldCombatTextStyle Style;
        public bool Critical;
        public float Age;
        public int Lane;
    }

    private readonly List<FloatingCombatText> _floatingCombatText = new();
    private sealed class CenterText
    {
        public string Text = "";
        public CenterCombatTextStyle Style;
        public bool Critical;
        public float Age;
        public int Lane;
        public float StartOffset;
    }
    private readonly List<CenterText> _centerCombatText = new();
    private float _playerCombatFlash;
    private float _targetCombatFlash;
    private long _worldCombatTextSpawned;
    private long _worldCombatTextDropped;

    private bool PlayerPanelOpen => _worldMapOpen || _characterOpen || _inspectOpen || _spellbookOpen ||
        _questLogOpen || _socialOpen || _helpOpen || _keybindingsOpen || _macroOpen ||
        _guildOpen || _auctionOpen || _mailOpen || _professionOpen || _talentOpen ||
        _tradeOpen || _bankOpen || _trainer is not null || _taxiOpen || _vendor is not null ||
        _gossipMenu is not null || _questList is not null || _questDetails is not null ||
        _questRequestItems is not null || _questOffer is not null || _backpackOpen ||
        _deathRezOpen || _tabardOpen || _loot.IsOpen || _itemTextRead is not null ||
        _itemRefEntry != 0;

    private void ResetCombatFeedback()
    {
        _floatingCombatText.Clear();
        _centerCombatText.Clear();
        _playerCombatFlash = 0f;
        _targetCombatFlash = 0f;
        _worldCombatTextSpawned = 0;
        _worldCombatTextDropped = 0;
        _uiHidden = false;
        _toggleUiWasDown = false;
        _bindingLatches.Clear();
        _creatureHostileVoices.Clear();
        ResetCombatTextState();
        ResetDuel();
    }

    private void QueueCenterCombatText(string text, CenterCombatTextStyle style,
        bool critical = false)
    {
        if (_centerCombatText.Count == 20) _centerCombatText.RemoveAt(0);
        CenterCombatTextRow Row(CenterText item) => new(
            CombatTextStateUiLaw.CenterMessageOffset(item.StartOffset, item.Age, item.Critical), item.Critical);
        float startOffset = CombatTextStateUiLaw.NextCenterStartOffset(
            _centerCombatText.Select(Row).ToArray(), critical);
        // Keep authored motion and lifetime; reserve critical growth and prevent
        // scrolling paths from crossing it. Overflow retires only conflicting rows.
        _centerCombatText.RemoveAll(item => CombatTextStateUiLaw.CenterInsertionConflicts(startOffset,
            critical, Row(item)));
        _centerCombatText.Add(new CenterText
        {
            Text = text,
            Style = style,
            Critical = critical,
            Lane = _centerCombatText.Count % 5,
            StartOffset = startOffset,
        });
    }

    private void ApplyCombatFeedback(CombatEvent combatEvent)
    {
        if (_net is null) return;

        if (combatEvent is CombatXpGain xp) PostCombatXpGain(xp);
        PostCombatNotice(combatEvent);

        foreach (ulong victim in CombatFeedbackLaw.FeedbackVictims(combatEvent))
        {
            if (victim == ControlledGuid) _playerCombatFlash = 0.35f;
            if (victim == _selectionGuid) _targetCombatFlash = 0.35f;
        }

        foreach (WorldCombatTextCue cue in CombatFeedbackLaw.WorldText(combatEvent, ControlledGuid,
                     IsOwnedCombatTextSource, IsMeleeStyledCombatSpell)) QueueWorldCombatText(cue);

        foreach (CenterCombatTextCue cue in CombatFeedbackLaw.CenterText(combatEvent, ControlledGuid))
            QueueCenterCombatText(cue.Text, cue.Style, cue.Critical);
    }

    private bool IsOwnedCombatTextSource(ulong source) =>
        _entities.TryGet(source, out WorldEntity unit) && unit.IsUnit &&
        (unit.Fields.SummonedBy == ControlledGuid || unit.Fields.CreatedBy == ControlledGuid);

    private bool IsMeleeStyledCombatSpell(uint spellId) =>
        _spellCatalog?.TryGet(spellId, out SpellInfo spell) != true || spell.MeleeWhiteDamage;

    private void QueueWorldCombatText(WorldCombatTextCue cue)
    {
        if (!_entities.TryGet(cue.Target, out WorldEntity entity)) return;
        int lane = _floatingCombatText.Count(t => t.Target == cue.Target);
        if (lane >= MaxWorldTextPerUnit)
        {
            _worldCombatTextDropped++;
            return;
        }

        float scale = _creatures?.PickScale(entity) ?? MathF.Max(0.01f, entity.Scale);
        _floatingCombatText.Add(new FloatingCombatText
        {
            Target = cue.Target,
            Anchor = entity.Position + new Vector3(0, 0, MathF.Max(1.5f, 2.2f * scale)),
            Text = cue.Text,
            Style = cue.Style,
            Critical = cue.Critical,
            Lane = lane,
        });
        _worldCombatTextSpawned++;
    }

    private void ApplyPvpCredit(byte[] body)
    {
        PvpCreditPacket credit = PvpCreditPackets.Parse(body);
        // The shipped COMBAT_TEXT_SHOW_HONOR_GAINED default is on. Dishonor and
        // zero-credit notices have their own combat-log treatment, not this row.
        if (credit.Honor <= 0 || LocalPlayerGuid == 0) return;
        QueueWorldCombatText(new(LocalPlayerGuid, PvpCreditPackets.FloatingText(credit),
            WorldCombatTextStyle.Honor));
    }

    private void UpdateCombatFeedback(float dt)
    {
        _playerCombatFlash = MathF.Max(0f, _playerCombatFlash - dt);
        _targetCombatFlash = MathF.Max(0f, _targetCombatFlash - dt);
        for (int i = _floatingCombatText.Count - 1; i >= 0; i--)
        {
            _floatingCombatText[i].Age += dt;
            FloatingCombatText item = _floatingCombatText[i];
            float lifetime = CombatFeedbackLaw.Presentation(
                item.Style, item.Critical, item.Text).LifetimeSeconds;
            if (item.Age >= lifetime)
                _floatingCombatText.RemoveAt(i);
        }
        for (int i = _centerCombatText.Count - 1; i >= 0; i--)
        {
            _centerCombatText[i].Age += dt;
            if (_centerCombatText[i].Age >= 1.9f) _centerCombatText.RemoveAt(i);
        }
    }

    private void DrawCombatHud()
    {
        _window.BeginHardwareCursorFrame();
        BeginSharedGameTooltipFrame(NowSeconds());
        // The HUD frame registry is only ever THIS frame's draw sites (PLAN_21).
        BeginHudFrameRegistry();
        try
        {
            // The reference keeps Point as the gameplay/UI base and lets a more specific hover
            // replace it. A real OS cursor avoids one render-frame of visual input latency.
            TryUseHardwareCursor(WorldCursorKind.Point.ToString());
            BakeDirtyPortraits();
            // TOGGLEUI hides at the draw, not at the producers. Open panels, chat, cooldowns,
            // combat text and tooltip state keep advancing and return unchanged; emitting no
            // player-HUD windows also makes the hidden interface take no mouse input.
            if (_uiHidden) return;
            // The commander map is a full command surface: HUD, free-cam overlays
            // and banner are all suppressed under it, same rule as the world map.
            // Unlike DrawWorldMapFrame it must not require the player ENTITY —
            // in the free view the own character may be unstreamed far away.
            if (_commanderMapOpen && _freeView)
            {
                DrawCommanderMapFrame();
                return;
            }
            // WorldMapFrame is a FULLSCREEN frame in the 1.12 FrameXML.  Nothing from
            // the ordinary HUD is allowed to render over it.
            if (_worldMapOpen)
            {
                DrawWorldMapFrame();
                // Fullscreen map drawing returns early from the ordinary HUD pass. Resolve its
                // offers here or map-owned GameTooltip content is discarded by the finally block.
                ResolveAndDrawSharedGameTooltip();
                return;
            }
            UpdateAndQueueWorldUnitGameTooltip(NowSeconds());
            // After the unit adapter, deliberately: when hover moves from a
            // unit to a gameobject the GO claim must be the one that lands,
            // and the two hovers are exclusive (Program.Targeting.cs).
            UpdateAndQueueWorldGameObjectGameTooltip(NowSeconds());
            DrawFloatingCombatText();
            DrawWorldUnitNames();
            DrawZoneTextSplash();
            DrawAutoFollowStatus();
            DrawPlayerFrame();
            // Player Power Bars sit beside the player frame, not instead of it: the AddOns
            // switch is off by default and either, neither or both may be on.
            DrawPlayerPowerBars();
            DrawSwingTimer();
            DrawThreatMeter();
            DrawTargetFrame();
            DrawPetFrameAndActionBar();
            DrawStanceBar();
            DrawPartyFrames();
            DrawTacticalFreezeNotice();
            DrawControlBanner();
            DrawPartyTacticsPanel();
            DrawPartyInventoryPanel();
            DrawPartyQuestLogPanel();
            DrawRaidInfoPanel();
            DrawStablePanel();
            DrawCompanionsPanel();
            DrawGiverQuestsWindow();
            DrawGiverQuestTextWindow();
            DrawQuestMarkerNumerals();
            DrawFreeCamSelectionOverlay();
            DrawUnitPopup();
            DrawPlayerAuraBar();
            DrawMinimap();
            DrawGameTimeFrame();
            DrawQuestTimerFrame();
            DrawDurabilityFrame();
            DrawQuestWatchFrame();
            DrawChatFrame();
            DrawCenterCombatText();
            DrawRtsTerritoryCapture();
            DrawWorldStateUi();
            DrawCastingBar();
            DrawMirrorTimerFrames();
            DrawActionBars();
            DrawLootFrame();
            DrawGameObjectFrame();
            DrawRestXpFrame();
            DrawTaxiFrame();
            DrawGossipFrame();
            DrawBattlefieldFrame();
            DrawBattlefieldScoreFrame();
            DrawVendorFrame();
            DrawTrainerFrame();
            DrawQuestFrame();
            DrawQuestPartyRail();
            DrawBankFrame();
            DrawMailFrame();
            DrawAuctionFrame();
            DrawProfessionFrame();
            DrawGuildFrame();
            DrawGuildInfoFrame();
            DrawGuildMemberDetailFrame();
            DrawGuildControlFrame();
            DrawSocialFrame();
            DrawTradeFrame();
            DrawKeybindingsFrame();
            DrawMacroFrame();
            DrawTooltipParityFrame();
            DrawUiErrorsParityFrame();
            DrawStaticPopupParityFrame();
            DrawTabardFrame();
            DrawTalentFrame();
            DrawInventory();
            DrawCharacterPage();
            DrawInspectFrame();
            DrawDressUpFrame();
            DrawSpellbook();
            DrawHelpFrame();
            // The reference bottom multibars use frameStrata HIGH. Draw them after ordinary
            // MEDIUM panels (including bags) and before dialog confirmations.
            DrawMultiActionBars();
            // HUD layout Edit Mode overlay: after the last frame it edits (the HIGH stratum
            // above), before tooltips and popups, so it is display-front over its movers.
            DrawHudLayoutEditor();
            DrawGroupLootFrames();
            ResolveAndDrawSharedGameTooltip();
            DrawItemRefTooltip();
            CompleteDeferredShoppingTooltipParityCapture();
            CompleteDeferredPartyTooltipParityCapture();
            DrawDeathRezFrame();
            DrawPartyInvite();
            DrawGuildInvitePopup();
            DrawDuelPopups();
            DrawInstanceBootWarning();
            DrawConfirmPopups();
            DrawDeleteItemConfirmation();
            DrawEquipBinding();
            DrawCharacterBindingsConfirmation();
            DrawSocialNamePopup();
            DrawGuildAddMemberPopup();
            DrawGuildMemberPopups();
            DrawPetMenuPopups();
            DrawGroupLootConfirmation();
            DrawBindConfirmation();
            DrawBankPurchaseConfirmation();
            DrawQuestAbandonConfirmation();
            DrawMailConfirmation();
            DrawEnchantConfirmation();
            DrawScreenshotStatus();
            DrawSkillUnlearnConfirmation();
            DrawWorldHoverCursor();
            if (SkillFrameUiParityCaptureActive) MarkUiParityFrameComplete();
        }
        finally
        {
            _window.EndHardwareCursorFrame();
            EndSharedGameTooltipFrame();
            CompleteDeferredShoppingTooltipParityCapture();
            CompleteDeferredPartyTooltipParityCapture();
        }
    }

    private void DrawPlayerFrame()
    {
        if ((_net is null && !HudPreview) ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        // The frame follows possession: a controlled bot's name, the session name otherwise.
        string name = ControlledGuid == LocalPlayerGuid
            ? _net?.PlayerName ?? "Preview"
            : ResolveUnitName(ControlledGuid);
        DrawVanillaUnitFrame(player, new Vector2(-19, 4), playerFrame: true,
            name, FactionReaction.Friendly,
            // Free view hands 0 so the frame's fallback chain picks the
            // streamed-body booth; embodied uses the rig bake, but only when it
            // is actually a bake OF the driven unit.
            UnitFramePortrait(_playerPortrait, PlayerPortraitCurrent && !_freeView),
            _playerCombatFlash);
    }

    private static void DrawPowerBar(WorldEntity entity)
    {
        if (entity.Fields.ActiveMaxPower == 0) return;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, PowerColor(entity.Fields.PowerType));
        ImGui.ProgressBar(entity.PowerFraction, new Vector2(-1, 12), "");
        ImGui.PopStyleColor();
    }

    private static Vector4 PowerColor(byte powerType) => powerType switch
    {
        1 => new Vector4(1.00f, 0.00f, 0.00f, 1f), // rage
        2 => new Vector4(1.00f, 0.50f, 0.25f, 1f), // focus
        3 => new Vector4(1.00f, 1.00f, 0.00f, 1f), // energy
        4 => new Vector4(0.00f, 1.00f, 1.00f, 1f), // happiness
        _ => new Vector4(0.00f, 0.00f, 1.00f, 1f), // mana
    };

    private static void PushUnitFrameBorder(float flash)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, flash > 0f ? 3f : 1f);
        ImGui.PushStyleColor(ImGuiCol.Border,
            flash > 0f ? new Vector4(1f, 0.12f, 0.08f, 1f) : new Vector4(0.25f, 0.25f, 0.25f, 1f));
    }

    private static void PopUnitFrameBorder()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawFloatingCombatText()
    {
        if (SettingsModalOpen) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float diagonal = MathF.Sqrt(display.X * display.X + display.Y * display.Y);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();

        foreach (FloatingCombatText item in _floatingCombatText)
        {
            WorldCombatTextPresentation presentation = CombatFeedbackLaw.Presentation(
                item.Style, item.Critical, item.Text);
            float t = item.Age / presentation.LifetimeSeconds;
            (float alpha, float shadowAlpha) = CombatFeedbackLaw.Alpha(presentation, item.Age);

            Vector3 point = item.Anchor + new Vector3(
                0, 0, presentation.RiseYards * Math.Clamp(t, 0f, 1f));
            if (!_window.Camera.TryWorldToScreen(point, display, out Vector2 screen)) continue;

            float size = diagonal * CombatFeedbackLaw.Scale(item.Critical, item.Age);

            uint packed = presentation.Color;
            Vector4 baseColor = new(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f,
                alpha);
            // Exact-size FRIZQT from the baked atlas, never the ImGui default font (game UI
            // never uses the ImGui font). World combat text scales continuously, so the nearest
            // baked FRIZQT em is used per item.
            int em = Math.Max(2, (int)MathF.Round(size));
            if (!GameTextLaw.TryGetFont(FontFace.FrizQt, em, false,
                    out ImFontPtr font, out float drawSize)) continue;
            float scaledWidth = GameText.MeasurePlain(item.Text, size, 1f);
            Vector2 pos = CombatTextStateUiLaw.WorldTextPosition(
                screen, scaledWidth, size, item.Lane, t);
            Vector2 shadowOffset = CombatTextStateUiLaw.WorldShadow(display);
            uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, shadowAlpha));
            uint color = ImGui.ColorConvertFloat4ToU32(baseColor);
            draw.AddText(font, drawSize, pos + shadowOffset, shadow, item.Text);
            draw.AddText(font, drawSize, pos, color, item.Text);
        }
    }

    private void DrawCenterCombatText()
    {
        if (SettingsModalOpen) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        float uiScale = display.Y / 768f;

        foreach (CenterText item in _centerCombatText)
        {
            float alpha = item.Age <= 1.3f ? 1f : 1f - (item.Age - 1.3f) / 0.6f;
            alpha = Math.Clamp(alpha, 0f, 1f);
            float size = 25f * uiScale;
            if (item.Critical)
            {
                float grow = Math.Clamp(item.Age / 0.05f, 0f, 1f);
                float shrink = Math.Clamp((item.Age - 0.05f) / 0.15f, 0f, 1f);
                size = (30f + 30f * grow - 30f * shrink) * uiScale;
            }

            int em = Math.Max(2, (int)MathF.Round(size));
            if (!GameTextLaw.TryGetFont(FontFace.FrizQt, em, false,
                    out ImFontPtr font, out float drawSize)) continue;
            float width = GameText.MeasurePlain(item.Text, size, 1f);
            Vector2 pos = CombatTextStateUiLaw.CenterTextPosition(
                display, uiScale, width, item.Lane, item.Age, item.Critical, item.StartOffset);
            Vector4 baseColor = item.Style switch
            {
                CenterCombatTextStyle.Heal => new Vector4(0.10f, 1f, 0.10f, alpha),
                CenterCombatTextStyle.Power => new Vector4(0.35f, 0.45f, 1f, alpha),
                CenterCombatTextStyle.Info => new Vector4(1f, .82f, 0f, alpha),
                _ => new Vector4(1f, 0.12f, 0.08f, alpha),
            };
            uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, alpha * 0.6f));
            draw.AddText(font, drawSize, pos + CombatTextStateUiLaw.CenterShadow(uiScale),
                shadow, item.Text);
            draw.AddText(font, drawSize, pos, ImGui.ColorConvertFloat4ToU32(baseColor), item.Text);
        }
    }
}
