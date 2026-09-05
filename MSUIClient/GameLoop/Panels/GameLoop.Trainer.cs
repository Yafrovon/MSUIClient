using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TrainerList? _trainer;
    private HashSet<uint>? _trainerKnownBefore;
    private int _trainerSelected;
    private int _trainerScroll;
    private readonly HashSet<uint> _trainerCollapsedGroups = [];
    private bool _trainerFilterAvailable = true;
    private bool _trainerFilterUnavailable = true;
    private bool _trainerFilterUsed;
    private bool _trainerFilterOpen;

    private bool RequestTrainer(ulong guid)
    {
        if (RefuseTacticalFreezeLiveCommand("opening trainer services")) return false;
        if (RefuseTacticalFrozenActor(guid, "open its trainer service")) return false;
        string outcome = "REFUSED"; string detail = "descriptorMissing";
        if (_net is { IsInWorld: true } &&
            TryGetInteractionBodyPose(out WorldBodyPose sessionBody) &&
            _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcTrainer) != 0)
        {
            Vector3 delta = sessionBody.Position - npc.Position;
            float distance = delta.Length();
            if (NpcSessionUiLaw.InRange(delta.LengthSquared()))
            {
                bool sent = _net.TrainerList(guid); outcome = sent ? "SENT" : "SEND_FAILED";
                detail = $"distance={distance:R};npcFlags=0x{npc.NpcFlags:X8}";
            }
            else { outcome = "REFUSED_RANGE"; detail = $"distance={distance:R};limit={GossipInteractDistance:R}"; }
        }
        EmitInterface("trainer", "list", outcome, guid, detail); return outcome == "SENT";
    }

    private bool UpdateTrainerLifecycle()
    {
        if (_trainer is null ||
            !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)) return false;
        ulong trainerGuid = _trainer.TrainerGuid;
        bool sourceAvailable = _entities.TryGet(trainerGuid, out WorldEntity trainer);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, trainer.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        CloseTrainerSession(playSound: true);
        EmitInterface("trainer", "lifecycle-close", "CLOSED", trainerGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyTrainerList(byte[] body)
    {
        TrainerList incoming = TrainerPackets.ParseList(body);
        bool freshSession = _trainer?.TrainerGuid != incoming.TrainerGuid;
        if (_trainer is not null && freshSession) CloseTrainerSession(playSound: true);
        _trainer = incoming;
        if (freshSession)
        {
            _trainerSelected = 0;
            _trainerScroll = 0;
            _trainerCollapsedGroups.Clear();
            _trainerFilterOpen = false;
            PlayUiSound(TrainerFrameUiLaw.OpenSound, TrainerFrameUiLaw.SoundCategory);
        }
        int available = _trainer.Spells.Count(s => s.State == 0);
        uint money = 0;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player)) money = player.Fields.Coinage;
        EmitInterface("trainer", "list", "DECODED", _trainer.TrainerGuid,
            $"type={_trainer.TrainerType};spells={_trainer.Spells.Count};available={available};money={money};greeting={SanitizeEvidence(_trainer.Greeting)}");
    }

    private bool CloseTrainerSession(bool playSound = true)
    {
        if (_trainer is null) return false;
        ulong guid = _trainer.TrainerGuid;
        _trainer = null;
        _trainerKnownBefore = null;
        if (playSound)
            PlayUiSound(TrainerFrameUiLaw.CloseSound, TrainerFrameUiLaw.SoundCategory);
        EmitInterface("trainer", "close", "CLOSED", guid, $"sound={playSound}");
        return true;
    }

    private void SimulateTrainerList()
    {
        var w = new PacketWriter();
        w.WriteU64(_selectionGuid == 0 ? 0xF13000038Ful : _selectionGuid);
        w.WriteU32(0); w.WriteU32(3);
        WriteTrainerRow(w, 6673, 0, 100, 1);
        WriteTrainerRow(w, 78, 1, 1000, 40);
        WriteTrainerRow(w, 100, 2, 10, 4);
        w.WriteCString("What can I teach you?");
        ApplyTrainerList(w.ToArray());
    }

    private static void WriteTrainerRow(PacketWriter w, uint spell, byte state, uint cost, byte level)
    {
        w.WriteU32(spell); w.WriteU8(state); w.WriteU32(cost);
        w.WriteU32(0); w.WriteU32(0); w.WriteU8(level);
        for (int i = 0; i < 5; i++) w.WriteU32(0);
    }

    private bool BuyTrainerSpell(uint serviceSpellId)
    {
        TrainerSpell? row = _trainer?.Spells.FirstOrDefault(s => s.ServiceSpellId == serviceSpellId);
        if (_trainer is null || row is not { ServiceSpellId: not 0 } spell)
        { EmitInterface("trainer", "buy", "REFUSED_UNKNOWN", _trainer?.TrainerGuid ?? 0, $"spell={serviceSpellId}"); return false; }
        uint money = 0;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player)) money = player.Fields.Coinage;
        if (spell.State != 0 || money < spell.Cost)
        {
            string reason = spell.State != 0 ? $"state={spell.State}" : $"money={money};cost={spell.Cost}";
            EmitInterface("trainer", "buy", "REFUSED_UNAVAILABLE", _trainer.TrainerGuid,
                $"spell={serviceSpellId};{reason}"); return false;
        }
        if (RefuseTacticalFreezeLiveCommand("training a spell")) return false;
        if (RefuseTacticalFrozenActor(_trainer.TrainerGuid, "train through it")) return false;
        _trainerKnownBefore = _actions.KnownSpells.ToHashSet();
        bool sent = _net?.TrainerBuySpell(_trainer.TrainerGuid, serviceSpellId) == true;
        EmitInterface("trainer", "buy", sent ? "SENT" : "SEND_FAILED", _trainer.TrainerGuid,
            $"spell={serviceSpellId};cost={spell.Cost};money={money}");
        return sent;
    }

    private bool BuyFirstAvailableTrainerSpell()
    {
        TrainerSpell? row = _trainer?.Spells.FirstOrDefault(s => s.State == 0);
        if (row is not { ServiceSpellId: not 0 } found) return false;
        return BuyTrainerSpell(found.ServiceSpellId);
    }

    private void ApplyTrainerSuccess(byte[] body)
    {
        TrainerResult result = TrainerPackets.ParseSuccess(body);
        bool refreshed = false;
        bool relisted = false;
        if (_trainer is { } trainer && trainer.TrainerGuid == result.TrainerGuid)
        {
            refreshed = trainer.Spells.Any(spell =>
                spell.ServiceSpellId == result.ServiceSpellId &&
                spell.State != TrainerFrameUiLaw.UsedState);
            _trainer = trainer with
            {
                Spells = TrainerFrameUiLaw.MarkServiceUsed(
                    trainer.Spells, result.ServiceSpellId),
            };

            // MARKING THE BOUGHT ROW USED IS NOT THE WHOLE UPDATE, AND THAT WAS THE BUG.
            // Learning rank 6 is exactly what makes rank 7 learnable, but rank 7's red
            // state byte came off the wire when the list was first sent and nothing here
            // re-derived it — so the next rank stayed red until the frame was closed and
            // reopened, which is the only thing that re-sent CMSG_TRAINER_LIST. Reported
            // 2026-09-01.
            //
            // The reference reaches the same place from the other side: SMSG_TRAINER_LIST
            // carries reqLevel/reqSkill/prev-rank per row precisely so the stock client can
            // recompute state locally on TRAINER_UPDATE (ClassTrainerFrame.lua:14). We ask
            // the server instead, and deliberately:
            //
            //   - vmangos computes state on GetSuiActor(), not on the commander. Under
            //     possession the trainable set, known spells, skills and purse are the
            //     BOT's, and a client-side recompute would have to duplicate that actor
            //     resolution to stay right. This is the commander case, not an edge case.
            //   - the reputation discount rides the same packet, so costs stay honest.
            //   - GetTrainerSpellState reads a spell chain we would otherwise have to
            //     mirror; one round trip is cheaper than a second implementation of it.
            //
            // The reply lands in ApplyTrainerList with a matching guid, so freshSession is
            // false: no open sound, no scroll reset, no lost collapse state. _trainerSelected
            // is a service index into a list the server rebuilds in the same order, so the
            // selection survives — and when the bought rank drops out under the used filter,
            // the visibleServices fallback reselects, which is what vanilla does too.
            relisted = RequestTrainer(result.TrainerGuid);
        }
        EmitInterface("trainer", "buy", "SUCCEEDED", result.TrainerGuid,
            $"serviceSpell={result.ServiceSpellId};knownBefore={_trainerKnownBefore?.Count ?? _actions.KnownSpells.Count};rowRefreshed={refreshed};relisted={relisted}");
    }

    private void ApplyTrainerFailure(byte[] body)
    {
        TrainerResult result = TrainerPackets.ParseFailure(body);
        string reason = result.Error switch { 0 => "UNAVAILABLE", 1 => "NOT_ENOUGH_MONEY", 2 => "NOT_ENOUGH_SKILL", _ => $"ERROR_{result.Error}" };
        EmitInterface("trainer", "buy", "FAILED", result.TrainerGuid,
            $"serviceSpell={result.ServiceSpellId};reason={reason}");
        // The refusal reaches the PLAYER, not just the dev log (it used to be a silent no-op).
        // vmangos sends 0 unavailable / 1 money / 2 skill; the reference raises UI_ERROR_MESSAGE.
        ShowUiError(result.Error switch
        {
            1 => InventoryGlobalString("ERR_NOT_ENOUGH_MONEY", "You don't have enough money."),
            2 => InventoryGlobalString("ERR_CANT_EQUIP_SKILL", "You aren't skilled enough to learn that."),
            _ => InventoryGlobalString("ERR_SPELL_UNLEARNED_S", "You can't learn that yet.").Replace("%s", "").Trim(),
        });
    }

    private void ObserveTrainerLearned(uint spellId)
    {
        if (_trainerKnownBefore is null) return;
        bool added = !_trainerKnownBefore.Contains(spellId) && _actions.KnownSpells.Contains(spellId);
        EmitInterface("trainer", "spellbook-delta", added ? "ADDED" : "UNCHANGED",
            _trainer?.TrainerGuid ?? 0, $"learnedSpell={spellId};knownAfter={_actions.KnownSpells.Count}");
        _trainerKnownBefore = null;
    }

    private void DrawTrainerFrame()
    {
        if (_trainer is null||_gameplayArt is null) return;
        float scale=GameplayUiScale();Vector2 origin=UiPanelFrameOrigin(UiPanelOwnershipRegistry[4], scale),size=TrainerFrameUiLaw.FrameSize(scale);
        PreparedSharedSpellTooltip? hoveredTrainerServiceTooltip = null;
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##trainer",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="trainer"){BeginUiParityFrame(origin,scale);CollectUiParityDraw("ClassTrainerFrame","Frame",origin,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        WorldEntity? trainerNpc = _entities.TryGet(_trainer.TrainerGuid, out WorldEntity foundTrainer)
            ? foundTrainer : null;
        if (trainerNpc is not null)
            DrawUnitPortraitImage(dl, trainerNpc,
                origin + TrainerFrameUiLaw.PortraitOffset * scale,
                TrainerFrameUiLaw.PortraitSize * scale, 0, false);
        foreach (TrainerFrameUiLaw.ArtPiece piece in TrainerFrameUiLaw.ShellArt)
        {
            Vector2 artMin = origin + piece.Rect.Min * scale;
            DrawArt(dl, piece.Path, artMin, piece.Rect.Size, scale);
            if (_uiParityArmed && _uiParityPanel == "trainer")
                CollectUiParityDraw(piece.Element, "Texture", artMin, piece.Rect.Size * scale,
                    "ClassTrainerFrame", new(piece.Path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT",
                        "ClassTrainerFrame", "TOPLEFT", piece.Rect.X, -piece.Rect.Y));
        }
        uint money = 0;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player)) money = player.Fields.Coinage;
        string trainerName = TrainerFrameUiLaw.FallbackTitle;
        if (trainerNpc is not null)
        {
            if (trainerNpc.Entry != 0 && TryBeginCreatureQuery(trainerNpc.Entry))
                _net?.CreatureQuery(trainerNpc.Entry, trainerNpc.Guid);
            trainerName = TrainerFrameUiLaw.Title(
                _creatureNames.GetValueOrDefault(trainerNpc.Entry, ""));
        }
        DrawNpcModalTitle(dl, trainerName,
            origin + TrainerFrameUiLaw.TitleCenter(
                GameText.EmPixels("GameFontNormal", scale) / scale) * scale, scale);
        DrawTrainerMoney(dl, money, origin + TrainerFrameUiLaw.PurseRightTop * scale,
            scale, 0xffffffff, rightAligned: true);
        DrawTrainerWrappedText(dl, TrainerFrameUiLaw.GreetingFont, _trainer.Greeting,
            origin, TrainerFrameUiLaw.Greeting, scale, TrainerFrameUiLaw.GreetingMaxLines);
        List<TrainerFrameUiLaw.ServiceNode> nodes = [];
        for (int serviceIndex = 0; serviceIndex < _trainer.Spells.Count; serviceIndex++)
        {
            TrainerSpell service = _trainer.Spells[serviceIndex];
            if (_spellCatalog?.TryGet(service.ServiceSpellId, out SpellInfo wire) != true) continue;
            (uint groupKey, string groupName) = TrainerFrameUiLaw.ServiceGroup(
                _trainer.TrainerType, service.State, wire, _skillLines);
            nodes.Add(new(serviceIndex, groupKey, groupName, wire.Name,
                service.State, service.RequiredLevel));
        }
        IReadOnlyList<TrainerFrameUiLaw.TreeRow> tree = TrainerFrameUiLaw.BuildTree(nodes,
            _trainer.TrainerType, _trainerCollapsedGroups, _trainerFilterAvailable,
            _trainerFilterUnavailable, _trainerFilterUsed);
        HashSet<int> visibleServices = tree.Where(row => !row.Header)
            .Select(row => row.ServiceIndex).ToHashSet();

        // This used to be visibleServices.FirstOrDefault(-1), and a HashSet has no order to
        // take a first of — bucket order is not tree order, so the row a lost selection landed
        // on was effectively arbitrary. FirstLearnable reads the tree instead and prefers a
        // green row, which is what should happen the moment a trained rank drops out of the
        // list under the used filter.
        if (!visibleServices.Contains(_trainerSelected))
            _trainerSelected = TrainerFrameUiLaw.FirstLearnable(tree);

        foreach (TrainerFrameUiLaw.ArtPiece piece in TrainerFrameUiLaw.CollapseAllTabArt)
        {
            Vector2 artMin = origin + piece.Rect.Min * scale;
            DrawArt(dl, piece.Path, artMin, piece.Rect.Size, scale);
            if (_uiParityArmed && _uiParityPanel == "trainer")
                CollectUiParityDraw(piece.Element, "Texture", artMin, piece.Rect.Size * scale,
                    "ClassTrainerExpandButtonFrame", new(piece.Path, 0xffffffff, "BACKGROUND",
                        "TOPLEFT", "ClassTrainerFrame", "TOPLEFT", piece.Rect.X, -piece.Rect.Y));
        }
        bool collapseEnabled = tree.Any(row => row.Header);
        bool allCollapsed = collapseEnabled && tree.Where(row => row.Header)
            .All(row => _trainerCollapsedGroups.Contains(row.GroupKey));
        if (VanillaCollapseAllButton(dl, "##trainer-collapse-all",
                origin + TrainerFrameUiLaw.CollapseAll.Min * scale,
                TrainerFrameUiLaw.CollapseAll.Size,
                origin + TrainerFrameUiLaw.CollapseAllIcon.Min * scale,
                TrainerFrameUiLaw.CollapseAllIcon.Size,
                origin + TrainerFrameUiLaw.CollapseAllLabelCenter * scale, scale,
                allCollapsed, collapseEnabled, TrainerFrameUiLaw.CollapseAllLabel,
                TrainerFrameUiLaw.CollapseAllFont, TrainerFrameUiLaw.CollapseAllDisabledFont,
                TrainerFrameUiLaw.CollapseAllMinusPath, TrainerFrameUiLaw.CollapseAllPlusPath,
                TrainerFrameUiLaw.CollapseAllHighlightPath))
        {
            uint[] groups = nodes.Select(node => node.GroupKey).Where(key => key != 0).Distinct().ToArray();
            if (groups.Length > 0 && groups.All(_trainerCollapsedGroups.Contains))
                _trainerCollapsedGroups.Clear();
            else
                foreach (uint group in groups) _trainerCollapsedGroups.Add(group);
            _trainerScroll = 0;
        }
        if (VanillaDropdownCapsule(dl, "##trainer-filter", origin, scale,
                TrainerFrameUiLaw.FilterDropDown, "Filter"))
        {
            _trainerFilterOpen = !_trainerFilterOpen;
            PlayUiSound(DropdownCapsuleUiLaw.ToggleSound, TrainerFrameUiLaw.SoundCategory);
        }
        // Claim the dropdown's rows BEFORE the spell list below is submitted. ImGui resolves
        // overlapping widgets by first-submission-wins each frame, not by visual draw order -
        // without this, the list (submitted first, further down) would keep winning every
        // click aimed at the dropdown even though the dropdown is drawn on top of it.
        // DrawTrainerFilterMenu still runs later, after the list, so it still paints on top.
        if (_trainerFilterOpen) HandleTrainerFilterMenuInput(origin, scale);

        int maximum=Math.Max(0,tree.Count-TrainerFrameUiLaw.VisibleRows);
        _trainerScroll=Math.Clamp(_trainerScroll,0,maximum);
        if (ImGui.IsMouseHoveringRect(
                origin + TrainerFrameUiLaw.ListWheel.Min * scale,
                origin + TrainerFrameUiLaw.ListWheel.Max * scale, false))
        {
            float wheel=ImGui.GetIO().MouseWheel;
            if(wheel!=0)_trainerScroll=Math.Clamp(_trainerScroll-(int)MathF.Sign(wheel),0,maximum);
        }
        for(int visible=0;visible<TrainerFrameUiLaw.VisibleRows;visible++)
        {
            int index=_trainerScroll+visible;if(index>=tree.Count)break;
            TrainerFrameUiLaw.TreeRow displayRow=tree[index];
            TrainerFrameUiLaw.LogicalRect logicalRow = TrainerFrameUiLaw.Row(visible);
            Vector2 min = origin + logicalRow.Min * scale;
            if (displayRow.Header)
            {
                ImGui.SetCursorScreenPos(min);
                if (ImGui.InvisibleButton($"##trainer-header-{displayRow.GroupKey}",
                        logicalRow.Size * scale))
                {
                    if (!_trainerCollapsedGroups.Add(displayRow.GroupKey))
                        _trainerCollapsedGroups.Remove(displayRow.GroupKey);
                    _trainerScroll = Math.Min(_trainerScroll, Math.Max(0, tree.Count - 1));
                }
                bool headerHovered = ImGui.IsItemHovered();
                TrainerFrameUiLaw.LogicalRect iconRect =
                    TrainerFrameUiLaw.HeaderIcon(logicalRow);
                Vector2 iconMin = origin + iconRect.Min * scale;
                uint foldArt = _gameplayArt.Handle(displayRow.Expanded
                    ? @"Interface\Buttons\UI-MinusButton-Up"
                    : @"Interface\Buttons\UI-PlusButton-Up");
                if (foldArt != 0)
                    dl.AddImage((nint)foldArt, iconMin, iconMin + iconRect.Size * scale);
                if (headerHovered)
                {
                    uint foldHighlight = _gameplayArt.AdditiveHandle(
                        @"Interface\Buttons\UI-PlusButton-Hilight");
                    if (foldHighlight != 0)
                        dl.AddImage((nint)foldHighlight, iconMin,
                            iconMin + iconRect.Size * scale);
                }
                GameText.Draw(dl, TrainerFrameUiLaw.RowNameFont, displayRow.Text,
                    min + TrainerFrameUiLaw.HeaderTextOffset * scale, scale, VanillaGold);
                continue;
            }
            TrainerSpell row=_trainer.Spells[displayRow.ServiceIndex];
            bool selectedRow = displayRow.ServiceIndex == _trainerSelected;
            if(VanillaListRow(dl,$"##trainer-{row.ServiceSpellId}",min,logicalRow.Size,scale,
                    "", selectedRow))
                _trainerSelected=displayRow.ServiceIndex;
            bool serviceHovered = ImGui.IsItemHovered();
            int rowEm = GameText.EmPixels(TrainerFrameUiLaw.RowNameFont, scale);
            Vector2 rowNameAt = TrainerFrameUiLaw.RowNameMinimum(min,
                logicalRow.Height * scale, rowEm);
            uint nameColor = TrainerFrameUiLaw.RowNameColor(row.State, selectedRow);
            GameText.Draw(dl, TrainerFrameUiLaw.RowNameFont, displayRow.Text,
                rowNameAt, scale, nameColor);
            if (_spellCatalog?.TryGet(row.ServiceSpellId, out SpellInfo serviceInfo) == true &&
                !string.IsNullOrWhiteSpace(serviceInfo.Rank))
            {
                Vector2 subtextAt = TrainerFrameUiLaw.RowSubtextMinimum(rowNameAt,
                    GameText.MeasureWidth(TrainerFrameUiLaw.RowNameFont,
                        displayRow.Text, scale), scale);
                GameText.Draw(dl, TrainerFrameUiLaw.RowSubtextFont, serviceInfo.Rank,
                    subtextAt, scale, TrainerFrameUiLaw.RowSubtextColor(
                        row.State, selectedRow, serviceHovered));
            }
        }
        DrawVanillaScrollBar(dl,"##trainer-scroll",
            origin + TrainerFrameUiLaw.ScrollOrigin * scale,
            TrainerFrameUiLaw.ScrollHeight, scale,
            _trainerScroll,maximum,x=>_trainerScroll=x);
        uint bar=_gameplayArt.Handle(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar");
        if (bar != 0)
        {
            dl.AddImage((nint)bar,
                origin + TrainerFrameUiLaw.HorizontalBarLeft.Min * scale,
                origin + TrainerFrameUiLaw.HorizontalBarLeft.Max * scale,
                TrainerFrameUiLaw.HorizontalBarLeftUvMin,
                TrainerFrameUiLaw.HorizontalBarLeftUvMax);
            dl.AddImage((nint)bar,
                origin + TrainerFrameUiLaw.HorizontalBarRight.Min * scale,
                origin + TrainerFrameUiLaw.HorizontalBarRight.Max * scale,
                TrainerFrameUiLaw.HorizontalBarRightUvMin,
                TrainerFrameUiLaw.HorizontalBarRightUvMax);
        }
        if (_trainerFilterOpen)
            DrawTrainerFilterMenu(dl, origin, scale);
        TrainerSpell selected=_trainerSelected<0||_trainerSelected>=_trainer.Spells.Count
            ?default:_trainer.Spells[_trainerSelected];
        if(selected.ServiceSpellId!=0)
        {
            SpellInfo? info=_spellCatalog?.TryGet(selected.ServiceSpellId,out SpellInfo found)==true?found:null;
            uint icon=_gameplayArt.Handle(info?.IconPath??@"Interface\Icons\INV_Misc_QuestionMark.blp");
            Vector2 iconMin = origin + TrainerFrameUiLaw.DetailIcon.Min * scale;
            DrawArt(dl, @"Interface\Buttons\UI-EmptySlot",
                origin + TrainerFrameUiLaw.DetailIconRing.Min * scale,
                TrainerFrameUiLaw.DetailIconRing.Size, scale);
            if (icon != 0)
                dl.AddImage((nint)icon, iconMin,
                    iconMin + TrainerFrameUiLaw.DetailIcon.Size * scale);
            (Vector2 tooltipMinimum, Vector2 tooltipMaximum) =
                TrainerFrameUiLaw.DetailTooltipOwnerBounds(origin, scale);
            ImGui.SetCursorScreenPos(tooltipMinimum);
            ImGui.InvisibleButton("##trainer-detail-icon", tooltipMaximum - tooltipMinimum);
            if (ImGui.IsItemHovered())
                hoveredTrainerServiceTooltip = PrepareSharedSpellTooltip(
                    new("spell:trainer-service", selected.ServiceSpellId),
                    selected.ServiceSpellId, scale, SpellTooltipPlacement.OwnerRight,
                    tooltipMinimum, tooltipMaximum);
            DrawTrainerWrappedText(dl, TrainerFrameUiLaw.DetailNameFont,
                info?.Name ?? $"Service {selected.ServiceSpellId}", origin,
                TrainerFrameUiLaw.DetailNameBox, scale,
                TrainerFrameUiLaw.DetailNameMaxLines);
            if (selected.RequiredLevel > 0)
                DrawTrainerWrappedText(dl, TrainerFrameUiLaw.DetailRequirementFont,
                    $"Requires level {selected.RequiredLevel}", origin,
                    TrainerFrameUiLaw.DetailRequirementBox, scale,
                    TrainerFrameUiLaw.DetailRequirementMaxLines);
            if (selected.Cost > 0)
            {
                Vector2 costAt = origin + TrainerFrameUiLaw.DetailCostLabel * scale;
                GameText.Draw(dl, TrainerFrameUiLaw.DetailCostFont, "Cost:", costAt, scale);
                float labelWidth = GameText.MeasureWidth(
                    TrainerFrameUiLaw.DetailCostFont, "Cost:", scale);
                DrawTrainerMoney(dl, selected.Cost,
                    origin + TrainerFrameUiLaw.DetailMoneyAt(labelWidth, scale) * scale,
                    scale, money >= selected.Cost ? 0xffffffff : 0xff1a1aff,
                    rightAligned: false);
            }
            DrawTrainerWrappedText(dl, TrainerFrameUiLaw.DetailDescriptionFont,
                info?.Description ?? "", origin, TrainerFrameUiLaw.DetailDescriptionBox,
                scale, TrainerFrameUiLaw.DetailDescriptionMaxLines);
        }
        bool canTrain=selected.ServiceSpellId!=0&&selected.State==0&&money>=selected.Cost;
        if (VanillaButton(dl, "##trainer-train", "Train",
                origin + TrainerFrameUiLaw.Train.Min * scale,
                TrainerFrameUiLaw.Train.Size, scale, canTrain))
            BuyTrainerSpell(selected.ServiceSpellId);
        if (VanillaButton(dl, "##trainer-exit", "Exit",
                origin + TrainerFrameUiLaw.Exit.Min * scale,
                TrainerFrameUiLaw.Exit.Size, scale))
            CloseTrainerSession();
        Vector2 close = origin + TrainerFrameUiLaw.Close.Min * scale;
        DrawImageButton(dl, "##trainer-close", close,
            TrainerFrameUiLaw.Close.Size * scale,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseTrainerSession();
        if(_uiParityArmed&&_uiParityPanel=="trainer")MarkUiParityFrameComplete();
        ImGui.End();
        if (hoveredTrainerServiceTooltip is { } preparedTrainerTooltip)
            OfferPreservedSharedGameTooltipRenderer(preparedTrainerTooltip.Owner,
                () => DrawSpellTooltip(preparedTrainerTooltip.Snapshot));
    }

    private readonly bool[] _trainerFilterRowHovered = new bool[3];

    // Input only, run before the spell list below so this claims the row hover/click first -
    // see the call site's comment. Order here (Available, Unavailable, Already Known) must
    // match DrawTrainerFilterMenu's row order below.
    private void HandleTrainerFilterMenuInput(Vector2 origin, float scale)
    {
        for (int i = 0; i < _trainerFilterRowHovered.Length; i++)
        {
            DropdownCapsuleUiLaw.LogicalRect logicalRow = DropdownCapsuleUiLaw.Row(
                TrainerFrameUiLaw.FilterDropDown, i);
            Vector2 min = origin + logicalRow.Min * scale;
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##trainer-filter-{i}",
                logicalRow.Size * scale);
            _trainerFilterRowHovered[i] = ImGui.IsItemHovered();
            if (clicked)
            {
                if (i == 0) _trainerFilterAvailable = !_trainerFilterAvailable;
                else if (i == 1) _trainerFilterUnavailable = !_trainerFilterUnavailable;
                else _trainerFilterUsed = !_trainerFilterUsed;
                _trainerScroll = 0;
                PlayUiSound(DropdownCapsuleUiLaw.RowSound,
                    TrainerFrameUiLaw.SoundCategory);
            }
        }
    }

    // Visuals only - no InvisibleButton/SetCursorScreenPos here. Runs after the spell list so
    // it paints on top; HandleTrainerFilterMenuInput already handled input for this frame and
    // recorded per-row hover in _trainerFilterRowHovered.
    private void DrawTrainerFilterMenu(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        (string Label, bool Value, uint Color)[] rows =
        [
            ("Available", _trainerFilterAvailable, 0xff20ff20),
            ("Unavailable", _trainerFilterUnavailable, 0xff2020ff),
            ("Already Known", _trainerFilterUsed, 0xff808080),
        ];
        DropdownCapsuleUiLaw.LogicalRect list = DropdownCapsuleUiLaw.List(
            TrainerFrameUiLaw.FilterDropDown, rows.Length);
        Vector2 listMin = origin + list.Min * scale;
        _skin?.DrawBackdrop(draw, listMin, listMin + list.Size * scale, WowSkin.Dialog);
        for (int i = 0; i < rows.Length; i++)
        {
            DropdownCapsuleUiLaw.LogicalRect logicalRow = DropdownCapsuleUiLaw.Row(
                TrainerFrameUiLaw.FilterDropDown, i);
            Vector2 min = origin + logicalRow.Min * scale;
            if (rows[i].Value || _trainerFilterRowHovered[i])
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    DropdownCapsuleUiLaw.RowHighlight) ?? 0;
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + logicalRow.Size * scale);
            }
            if (rows[i].Value)
            {
                uint check = _gameplayArt?.Handle(DropdownCapsuleUiLaw.RowCheck) ?? 0;
                if (check != 0)
                {
                    Vector2 checkMin = min + DropdownCapsuleUiLaw.Check.Min * scale;
                    draw.AddImage((nint)check, checkMin,
                        checkMin + DropdownCapsuleUiLaw.Check.Size * scale);
                }
            }
            GameText.Draw(draw, DropdownCapsuleUiLaw.SelectionFont, rows[i].Label,
                min + DropdownCapsuleUiLaw.RowTextOffset * scale, scale, rows[i].Color);
        }
    }

    private void DrawTrainerWrappedText(ImDrawListPtr draw, string font, string text,
        Vector2 origin, TrainerFrameUiLaw.LogicalRect box, float scale, int maximumLines)
    {
        IReadOnlyList<string> lines = TrainerFrameUiLaw.WrapText(text,
            box.Width * scale, maximumLines,
            candidate => GameText.MeasureWidth(font, candidate, scale));
        float pitch = GameText.LinePitch(font, scale);
        for (int line = 0; line < lines.Count; line++)
            GameText.Draw(draw, font, lines[line],
                TrainerFrameUiLaw.TextLineMinimum(origin, box, scale, line, pitch), scale);
    }

    private void DrawTrainerMoney(ImDrawListPtr draw, uint copper, Vector2 anchor, float scale,
        uint color, bool rightAligned)
    {
        IReadOnlyList<MailUiLaw.MoneyDenomination> denominations = MailUiLaw.Money(copper);
        float width = denominations.Sum(denomination =>
            GameText.MeasureWidth("NumberFontNormal", denomination.Value.ToString(), scale) +
            TrainerFrameUiLaw.MoneyIconSize * scale) +
            Math.Max(0, denominations.Count - 1) * TrainerFrameUiLaw.MoneyGap * scale;
        float x = rightAligned ? anchor.X - width : anchor.X;
        foreach (MailUiLaw.MoneyDenomination denomination in denominations)
        {
            string text = denomination.Value.ToString();
            GameText.Draw(draw, "NumberFontNormal", text,
                TrainerFrameUiLaw.MoneyPoint(x, anchor.Y), scale, color);
            x += GameText.MeasureWidth("NumberFontNormal", text, scale);
            DrawMailCoin(draw, denomination.Icon,
                TrainerFrameUiLaw.MoneyPoint(x, anchor.Y), scale, color);
            x += (TrainerFrameUiLaw.MoneyIconSize + TrainerFrameUiLaw.MoneyGap) * scale;
        }
    }
}
