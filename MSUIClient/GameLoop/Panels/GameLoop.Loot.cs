using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Solo corpse looting: the CMSG_LOOT / SMSG_LOOT_RESPONSE flow and the authored 1.12
/// LootFrame. Modeled on benilla ui_loot.rs + net/apply/loot.rs and LootFrame.xml.
/// </summary>
public sealed partial class GameLoop
{
    private readonly LootState _loot = new();
    private ulong _lootPendingGuid;
    /// <summary>The streamed body currently wearing the loot kneel (0 = none); cleared when
    /// control moves to another body so a released bot never stays knelt.</summary>
    private ulong _lootKneelBodyGuid;
    private Vector3 _lootOpenedAt;
    private bool _lootOpenedAtKnown;
    private int _lootPage = 1;
    private readonly List<(uint Entry, uint Count, int TriesLeft)> _pendingReceives = new();
    private uint _lootMoneyBefore;
    private uint _lootMoneyExpected;
    private bool _lootMoneyPending;

    private const int LootRowsPerFrame = 4;

    private void ResetLoot()
    {
        _loot.Clear();
        _groupLootRolls.Clear();
        _pendingGroupLootLines.Clear();
        _groupLootConfirm = null;
        _lootPendingGuid = 0;
        _lootOpenedAtKnown = false;
        RefreshLootKneel();
        _lootPage = 1;
        _pendingReceives.Clear();
        _lootMoneyPending = false;
        _previousCoinage = null;
    }

    /// <summary>Right-click on a dead, lootable corpse. The kneel plays optimistically at the
    /// send (the reference arms its loot latch at the CMSG_LOOT send site, 0x5df253).</summary>
    private bool _lootAutoAllArmed;

    private bool RequestLoot(ulong guid)
    {
        if (RefuseTacticalFreezeLiveCommand("looting")) return false;
        if (RefuseTacticalFrozenActor(guid, "loot it")) return false;
        // Auto Loot (Interface Options): decided at request time, Shift inverts.
        _lootAutoAllArmed = Settings.Controls.AutoLoot != ImGui.GetIO().KeyShift;
        // [SUI] Loot acts as the DRIVEN body: the server's LootHandler runs as
        // GetSuiActor() and mirrors the bot's loot frames back (SMSG_SUI_PROXY), so a
        // possessed bot kneels, takes into ITS bags and releases. Range gates at that
        // body — the parked commander may be a continent away.
        bool eligible = _entities.TryGet(guid, out WorldEntity source) && source.IsCreature &&
                        source.IsDead && source.Fields.Lootable;
        if (_net is null || !eligible)
        {
            EmitInterface("loot", "request", "REFUSED", guid,
                $"inWorld={_net?.IsInWorld == true};entity={source is not null};dead={source?.IsDead == true};lootable={source?.Fields.Lootable == true}");
            return false;
        }
        if (!TryGetInteractionBodyPose(out WorldBodyPose sessionBody) ||
            !_entities.TryGet(ControlledGuid, out WorldEntity sessionPlayer))
        {
            EmitInterface("loot", "request", "REFUSED_NO_BODY", guid,
                $"interactionBody=false;controlled=0x{ControlledGuid:X}");
            return false;
        }
        float distanceSquared = Vector3.DistanceSquared(sessionBody.Position, source.Position);
        float reachSquared = WorldCursorUiLaw.UnitMeleeReachSquared(
            sessionPlayer.Fields.CombatReach, source.Fields.CombatReach);
        if (distanceSquared > reachSquared)
        {
            ShowUiError("You are too far away!");
            EmitInterface("loot", "request", "REFUSED_RANGE", guid,
                $"distanceSquared={distanceSquared:R};reachSquared={reachSquared:R}");
            return false;
        }
        _lootPendingGuid = guid;
        bool sent = _net.Loot(guid);
        EmitInterface("loot", "request", sent ? "SENT" : "SEND_FAILED", guid,
            $"dead={source.IsDead};lootable={source.Fields.Lootable};body={Convert.ToHexString(WorldSession.BuildLootGuidBody(guid))}");
        if (!sent) { _lootPendingGuid = 0; RefreshLootKneel(); return false; }
        RefreshLootKneel();
        return true;
    }

    private void RefreshLootKneel()
    {
        LootLatchLaw.TargetKind kind = LootLatchLaw.TargetKind.Unresolved;
        uint gameObjectType = 0;
        uint unitHealth = 0;
        if (_lootPendingGuid != 0 && _entities.TryGet(_lootPendingGuid, out WorldEntity target))
        {
            if (target.IsGameObject)
            {
                kind = LootLatchLaw.TargetKind.GameObject;
                gameObjectType = target.GameObjectType;
            }
            else if (target.IsUnit)
            {
                kind = LootLatchLaw.TargetKind.Unit;
                unitHealth = target.Fields.Health;
            }
            else if (target.Type is ObjectTypeId.Item or ObjectTypeId.Container)
                kind = LootLatchLaw.TargetKind.Item;
            else kind = LootLatchLaw.TargetKind.Other;
        }
        bool kneeling = LootLatchLaw.ShouldKneel(
            _lootPendingGuid, kind, gameObjectType, unitHealth);
        // The kneel belongs to the body that sent CMSG_LOOT: the driven bot while
        // possessing (== LocalPlayerGuid otherwise). A body that knelt under the
        // previous control state stands up first.
        if (_lootKneelBodyGuid != 0 && _lootKneelBodyGuid != ControlledGuid)
        {
            _creatures?.SetLootKneel(_lootKneelBodyGuid, false);
            _lootKneelBodyGuid = 0;
        }
        if (ControlledBodyIsStreamed)
        {
            if (_character is not null) _character.LootKneel = false;
            _creatures?.SetLootKneel(ControlledGuid, kneeling);
            _lootKneelBodyGuid = kneeling ? ControlledGuid : 0;
        }
        else
        {
            _creatures?.SetLootKneel(ControlledGuid, false);
            _lootKneelBodyGuid = 0;
            if (_character is not null) _character.LootKneel = kneeling;
        }
    }

    /// <summary>
    /// [SUI] Control changed hands (possess / release): the loot window, its latch and
    /// the kneel belong to the body that opened it. Local only — the server has already
    /// force-released a possessed bot's loot and the own character's window, if any,
    /// is the unattended AI's to close.
    /// </summary>
    private void ClearLootOnControlChange()
    {
        if (_loot.IsOpen) EmitInterface("loot", "release", "CONTROL_CHANGE", _loot.Source, "local=true");
        _loot.Clear();
        _lootPendingGuid = 0;
        _lootOpenedAtKnown = false;
        _lootMoneyPending = false;
        _lootAutoAllArmed = false;
        RefreshLootKneel();
    }

    private void ApplyLootResponse(byte[] body)
    {
        var (guid, lootType, error, gold, items) = LootPackets.ParseResponse(body);
        if (_gameObjectGuid == guid || (_entities.TryGet(guid, out WorldEntity gameObject) && gameObject.IsGameObject))
            EmitInterface("gameobject", "response", lootType == 0 ? "REFUSED" : "LOOT_OPEN", guid,
                $"lootType={lootType};error={error};money={gold};items={items.Count}");
        if (lootType == 0)
        {
            // A walk-then-loot still retrying (Command View): a range/facing refusal is the race
            // being retried, not news for the player.
            bool retrying = _cvPendingInteractGuid == guid && error is 4 or 5;
            // The error shape. Refusals surface as the red error text the 1.12 client shows.
            if (!retrying) ShowUiError(LootPackets.ErrorText(error));
            if (_cvPendingInteractGuid == guid && !retrying) _cvPendingInteractGuid = 0;
            if (_lootPendingGuid == guid) _lootPendingGuid = 0;
            RefreshLootKneel();
            EmitInterface("loot", "response", "REFUSED", guid, $"error={error};text={SanitizeEvidence(LootPackets.ErrorText(error))}");
            return;
        }
        LootLatchLaw.ResponsePlan admission = LootLatchLaw.AdmitResponse(
            _lootPendingGuid, guid, lootType);
        if (!admission.Accept)
        {
            bool releaseSent = !admission.SendRelease || _net?.LootRelease(guid) == true;
            _lootPendingGuid = admission.NextLatch;
            RefreshLootKneel();
            EmitInterface("loot", "response", "REFUSED", guid,
                $"reason=latch-admission;lootType={lootType};releaseSent={releaseSent}");
            return;
        }
        _lootPendingGuid = admission.NextLatch;
        RefreshLootKneel();
        _loot.Open(guid, lootType, gold, items);
        if (_cvPendingInteractGuid == guid) _cvPendingInteractGuid = 0;   // walk-then-loot delivered
        if (_lootAutoAllArmed)
        {
            _lootAutoAllArmed = false;
            TakeAllLoot();
        }
        LootFrameUiLaw.OpenPresentation openPresentation =
            LootFrameUiLaw.OnShow(lootType, items.Count, gold);
        if (openPresentation.SoundCue is { } cue)
            PlayUiSound(cue, LootFrameUiLaw.SoundCategory);
        _lootPage = 1;
        _lootOpenedAtKnown = TryGetInteractionBodyPose(out WorldBodyPose sessionBody);
        if (_lootOpenedAtKnown) _lootOpenedAt = sessionBody.Position;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
            _lootMoneyBefore = player.Fields.Coinage;
        EmitInterface("loot", "response", items.Count == 0 && gold == 0 ? "EMPTY" : "OPEN", guid,
            $"type={lootType};money={gold};items={items.Count};slots={string.Join(',', items.Select(i => i.Slot))}");
        // Ask for every row's template once so names/quality colors resolve; icons come
        // straight off the wire displayInfoId and never wait.
        if (_items is not null && _net is not null)
            foreach (LootItem item in items)
                _items.Require(item.ItemId, guid, _net);
    }

    private void ApplyLootRemoved(byte[] body)
    {
        if (body.Length < 1) return;
        _loot.RemoveSlot(body[0]);
        EmitInterface("loot", "item", "REMOVED", _loot.Source, $"slot={body[0]};remaining={_loot.Items.Count}");
    }

    private void ApplyLootClearMoney()
    {
        uint cleared = _loot.Gold;
        _loot.ClearMoney();
        EmitInterface("loot", "money", "CLEARED", _loot.Source, $"amount={cleared};remainingItems={_loot.Items.Count}");
    }

    private void ApplyLootReleaseResponse(byte[] body)
    {
        // Idempotent, guid-matched: a stale release for corpse A must not clear a
        // fresh session on corpse B (benilla ui_loot.rs:209-214).
        if (body.Length < 8) return;
        ulong guid = BitConverter.ToUInt64(body, 0);
        if (_loot.Source == guid)
        {
            PredictGameObjectAnimationState(guid, GameObjectAnimationLaw.StateReady);
            _loot.Clear();
        }
        _lootPendingGuid = LootLatchLaw.ClearFor(_lootPendingGuid, guid);
        RefreshLootKneel();
        EmitInterface("loot", "release", "RELEASED", guid, "guidMatched=true");
    }

    /// <summary>SMSG_ITEM_PUSH_RESULT — the one reliable "it landed" signal for solo loot.
    /// Drives the green "You receive loot: [Name] xN." line once the template resolves.</summary>
    private void ApplyItemPushResult(byte[] body)
    {
        if (_net is null || body.Length < 41) return;
        var r = new PacketReader(body);
        ulong player = r.ReadU64();
        // The driven bot's pushes arrive mirrored with ITS guid (the server skips the
        // commander's direct group-broadcast copy while possessing).
        if (player != _net.PlayerGuid && player != ControlledGuid) return;
        uint received = r.ReadU32();
        uint created = r.ReadU32();
        uint showInChat = r.ReadU32();
        byte bagSlot = r.ReadU8();
        uint itemSlot = r.ReadU32(); // 0xFFFFFFFF = stacked onto an existing slot
        uint entry = r.ReadU32();
        uint suffixFactor = r.ReadU32();
        uint randomProperty = r.ReadU32();
        uint count = r.ReadU32();
        if (entry == 0) return;
        uint actualCount = Math.Max(1, count);
        if (_items is not null) _items.Require(entry, 0, _net);
        TriggerItemPushAnimation(bagSlot, itemSlot, entry);
        _pendingReceives.Add((entry, actualCount, 120));
        EmitInterface("loot", "item", "RECEIVED", player,
            $"item={entry};count={actualCount};received={received};created={created};show={showInChat};" +
            $"bag={bagSlot};slot={itemSlot};suffix={suffixFactor};random={randomProperty};bytes={body.Length}");
        if (created != 0)
            ObserveProfessionCreatedItemPush(entry, actualCount);
    }

    /// <summary>Escape closes the loot window before it reaches the game menu.</summary>
    private bool TryCloseLootOnEscape()
    {
        if (!_loot.IsOpen) return false;
        ReleaseLoot();
        return true;
    }

    private void ReleaseLoot()
    {
        if (!_loot.IsOpen) return;
        ulong source = _loot.Source;
        bool sent = _net?.LootRelease(source) == true;
        EmitInterface("loot", "release", sent ? "SENT" : "SEND_FAILED", source,
            $"body={Convert.ToHexString(WorldSession.BuildLootGuidBody(source))}");
        PredictGameObjectAnimationState(source, GameObjectAnimationLaw.StateReady);
        _loot.Clear(); // optimistic; SMSG_LOOT_RELEASE_RESPONSE clears again idempotently
        _lootPendingGuid = LootLatchLaw.ClearFor(_lootPendingGuid, source);
        RefreshLootKneel();
    }

    private bool TakeLootMoney()
    {
        if (RefuseTacticalFreezeLiveCommand("taking loot")) return false;
        if (_net is null || !_loot.IsOpen || _loot.Gold == 0) return false;
        if (RefuseTacticalFrozenActor(_loot.Source, "take loot from it")) return false;
        if (_entities.TryGet(ControlledGuid, out WorldEntity player)) _lootMoneyBefore = player.Fields.Coinage;
        _lootMoneyExpected = _loot.Gold;
        _lootMoneyPending = _net.LootMoney();
        EmitInterface("loot", "money", _lootMoneyPending ? "SENT" : "SEND_FAILED", _loot.Source,
            $"amount={_lootMoneyExpected};moneyBefore={_lootMoneyBefore};body=EMPTY");
        return _lootMoneyPending;
    }

    private bool TakeFirstLootItem()
    {
        if (RefuseTacticalFreezeLiveCommand("taking loot")) return false;
        if (_net is null || !_loot.IsOpen || _loot.Items.Count == 0) return false;
        if (RefuseTacticalFrozenActor(_loot.Source, "take loot from it")) return false;
        byte slot = _loot.Items[0].Slot;
        PlayItemPickupSound(_loot.Items[0].DisplayInfoId);
        bool sent = _net.AutostoreLootItem(slot);
        EmitInterface("loot", "item", sent ? "SENT" : "SEND_FAILED", _loot.Source,
            $"slot={slot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(slot))}");
        return sent;
    }

    private bool TakeAllLoot()
    {
        if (RefuseTacticalFreezeLiveCommand("taking loot")) return false;
        if (_net is null || !_loot.IsOpen) return false;
        if (RefuseTacticalFrozenActor(_loot.Source, "take loot from it")) return false;
        bool sent = true;
        if (_loot.Gold > 0) sent &= TakeLootMoney();
        foreach (LootItem item in _loot.Items.ToArray())
        {
            PlayItemPickupSound(item.DisplayInfoId);
            bool itemSent = _net.AutostoreLootItem(item.Slot);
            sent &= itemSent;
            EmitInterface("loot", "item", itemSent ? "SENT" : "SEND_FAILED", _loot.Source,
                $"slot={item.Slot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(item.Slot))};batch=all");
        }
        EmitInterface("loot", "take-all", sent ? "SENT" : "SEND_FAILED", _loot.Source,
            $"money={_loot.Gold};items={_loot.Items.Count}");
        return sent;
    }

    private void SimulateLootFlow(bool empty = false)
    {
        ulong guid = _net?.PlayerGuid ?? 0xF130000006000001ul;
        var w = new PacketWriter(); w.WriteU64(guid); w.WriteU8(1); w.WriteU32(empty ? 0u : 37u);
        w.WriteU8(empty ? (byte)0 : (byte)2);
        if (!empty)
            foreach ((byte slot, uint item, uint count, uint display) in new[] { ((byte)0, 117u, 2u, 789u), ((byte)3, 159u, 1u, 790u) })
            { w.WriteU8(slot); w.WriteU32(item); w.WriteU32(count); w.WriteU32(display); w.WriteU32(0); w.WriteU32(0); w.WriteU8(0); }
        ApplyLootResponse(w.ToArray());
        EmitInterface("loot", empty ? "simulate-empty" : "simulate", empty ? "EMPTY" : "OPEN", guid,
            empty ? "money=0;items=0;autoRelease=false" : "money=37;items=2;wireSlots=0,3");
    }

    /// <summary>Per-frame loot upkeep, called from the HUD pass.</summary>
    private void UpdateLoot()
    {
        // Resolve queued "You receive loot" lines as their templates land (~2 s budget).
        for (int i = _pendingReceives.Count - 1; i >= 0; i--)
        {
            var (entry, count, tries) = _pendingReceives[i];
            if (_items?.TryGet(entry, out ItemTemplate? template) == true && template is not null)
            {
                string suffix = count > 1 ? $" x{count}" : "";
                if (Settings.Controls.ShowLootAcquisitionText)
                    PushCenterText($"You receive loot: [{template.Name}]{suffix}.",
                        CenterCombatTextStyle.Heal);
                _pendingReceives.RemoveAt(i);
            }
            else if (tries <= 0) _pendingReceives.RemoveAt(i);
            else _pendingReceives[i] = (entry, count, tries - 1);
        }

        if (_lootMoneyPending && _net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player) &&
            player.Fields.Coinage >= _lootMoneyBefore + _lootMoneyExpected)
        {
            EmitInterface("loot", "economy", "VERIFIED", ControlledGuid,
                $"money={_lootMoneyBefore}->{player.Fields.Coinage};expected={_lootMoneyExpected}");
            _lootMoneyPending = false;
        }

        if (!_loot.IsOpen) return;

        // The engine closes the window itself when the last row leaves (vmangos releases only
        // in HandleLootReleaseOpcode; the client is the one that decides the window is done).
        if (_loot.TakeAutoRelease()) { ReleaseLoot(); return; }

        // Corpse despawned under us, or the player walked away: release. The real client
        // stands you up and closes on movement; distance is our movement proxy.
        if (!_entities.TryGet(_loot.Source, out _)) { ReleaseLoot(); return; }
        if (_lootOpenedAtKnown && TryGetInteractionBodyPose(out WorldBodyPose sessionBody) &&
            Vector3.DistanceSquared(sessionBody.Position, _lootOpenedAt) > 2.25f)
            ReleaseLoot();
    }

    private void ShowUiError(string text) => _uiErrors.Push(text, UiMessageKind.Error, NowSeconds());
    private void ShowUiInfo(string text) => _uiErrors.Push(text, UiMessageKind.Info, NowSeconds());

    private void ApplyFishingVerdict(byte[] body, bool escaped)
    {
        string key = LootPackets.ParseFishingVerdict(body, escaped);
        string text = InventoryGlobalString(key,
            escaped ? "Your fish got away!" : "No fish are hooked.");
        ShowUiInfo(text);
        EmitInterface("loot", "fishing-verdict", escaped ? "ESCAPED" : "NOT_HOOKED", 0,
            $"key={key};body=<empty>");
    }

    private void PushCenterText(string text, CenterCombatTextStyle style)
    {
        QueueCenterCombatText(text, style);
    }

    // ── the authored LootFrame ───────────────────────────────────────────────

    private readonly record struct LootRow(bool IsCoin, byte WireSlot, uint ItemId,
        string Name, string IconPath, uint Count, Vector4 NameColor, uint RandomPropertyId = 0);

    private void DrawLootFrame()
    {
        UpdateLoot(); // also drains the receive-line queue while no window is open
        if (!_loot.IsOpen || _gameplayArt is null) return;

        float s = GameplayUiScale();
        Vector2 p = UiPanelFrameOrigin(UiPanelOwnershipRegistry[9], s) +
            LootFrameUiLaw.FrameOffset * s;
        Vector2 size = LootFrameUiLaw.Frame.ScaledSize(s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##loot-frame", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="loot"){BeginUiParityFrame(p,s);CollectUiParityDraw("LootFrame","Frame",p,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",LootFrameUiLaw.TraceAbsoluteOffset.X,LootFrameUiLaw.TraceAbsoluteOffset.Y));}

        // Panel slab, the skull showing through its ring cut-out, and the title.
        DrawArt(dl, LootFrameUiLaw.PanelPath, p, LootFrameUiLaw.Frame.Size, s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootFrame/Texture","Texture",p,size,"LootFrame",new(LootFrameUiLaw.PanelPath,0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",0,0));
        string overlayPath = LootFrameUiLaw.OnShow(
            _loot.LootType, _loot.Items.Count, _loot.Gold).OverlayPath;
        Vector2 portraitMin = LootFrameUiLaw.PortraitOverlay.ScaledMin(p, s);
        DrawArt(dl, overlayPath, portraitMin, LootFrameUiLaw.PortraitOverlay.Size, s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootFramePortraitOverlay","Texture",portraitMin,LootFrameUiLaw.PortraitOverlay.ScaledSize(s),"LootFrame",new(overlayPath,0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",LootFrameUiLaw.PortraitOverlay.X,-LootFrameUiLaw.PortraitOverlay.Y));
        GameText.DrawCentered(dl, LootFrameUiLaw.TitleFont, "Items",
            p + LootFrameUiLaw.TitleCenter * s, s);

        List<LootRow> rows = BuildLootRows();

        // Pagination (LootFrame.xml:111-131): 4 rows fit; >4 spends one seat on the pager.
        int perPage = rows.Count > LootRowsPerFrame ? LootRowsPerFrame - 1 : LootRowsPerFrame;
        int maxPage = Math.Max(1, (rows.Count + perPage - 1) / perPage);
        _lootPage = Math.Clamp(_lootPage, 1, maxPage);
        int first = (_lootPage - 1) * perPage;

        for (int visual = 0; visual < perPage && first + visual < rows.Count; visual++)
        {
            LootRow row = rows[first + visual];
            Vector2 rowMin = LootFrameUiLaw.Row(visual).ScaledMin(p, s);
            DrawLootRow(dl, row, rowMin, s, visual);
        }

        if (maxPage > 1)
        {
            DrawLootPagerButton(dl, LootFrameUiLaw.PagerUp.ScaledMin(p, s), s, up: true,
                enabled: _lootPage > 1);
            DrawLootPagerButton(dl, LootFrameUiLaw.PagerDown.ScaledMin(p, s), s, up: false,
                enabled: _lootPage < maxPage);
        }

        // Close button (UI-Panel-MinimizeButton, centered on TOPRIGHT (-81,-26)).
        Vector2 closeMin = LootFrameUiLaw.CloseArt.ScaledMin(p, s);
        DrawArt(dl, LootFrameUiLaw.CloseUpPath, closeMin, LootFrameUiLaw.CloseArt.Size, s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootCloseButton","Button",closeMin,LootFrameUiLaw.CloseArt.ScaledSize(s),"LootFrame",new(LootFrameUiLaw.CloseUpPath,0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",LootFrameUiLaw.CloseArt.X,-LootFrameUiLaw.CloseArt.Y));
        ImGui.SetCursorScreenPos(LootFrameUiLaw.CloseHit.ScaledMin(p, s));
        if (ImGui.InvisibleButton("##loot-close", LootFrameUiLaw.CloseHit.ScaledSize(s))) ReleaseLoot();
        else if (ImGui.IsItemHovered())
            DrawArt(dl, LootFrameUiLaw.CloseHighlightPath, closeMin,
                LootFrameUiLaw.CloseArt.Size, s);

        if(_uiParityArmed&&_uiParityPanel=="loot")MarkUiParityFrameComplete();ImGui.End();
    }

    private List<LootRow> BuildLootRows()
    {
        var rows = new List<LootRow>(_loot.Items.Count + 1);
        if (_loot.Gold > 0)
        {
            // Coin icon by the highest non-zero denomination; white name (COIN_QUALITY 1).
            string coinIcon = _loot.Gold >= 10000 ? @"Interface\Icons\INV_Misc_Coin_01"
                : _loot.Gold >= 100 ? @"Interface\Icons\INV_Misc_Coin_03"
                : @"Interface\Icons\INV_Misc_Coin_05";
            rows.Add(new LootRow(true, 0, 0, FormatMoney(_loot.Gold), coinIcon, 1, Vector4.One));
        }
        foreach (LootItem item in _loot.Items)
        {
            string name = "...";
            Vector4 color = Vector4.One;
            string icon = _items?.IconForDisplay(item.DisplayInfoId)
                ?? @"Interface\Icons\INV_Misc_QuestionMark.blp";
            if (_items?.TryGet(item.ItemId, out ItemTemplate? template) == true && template is not null)
            {
                name = _itemRandomProperties?.ItemName(template.Name, unchecked((int)item.RandomPropertyId)) ?? template.Name;
                color = ItemQualityColor(template.Quality);
                if (template.IconPath.Length > 0) icon = template.IconPath;
            }
            rows.Add(new LootRow(false, item.Slot, item.ItemId, name, icon, item.Count, color, item.RandomPropertyId));
        }
        return rows;
    }

    private void DrawLootRow(ImDrawListPtr dl, in LootRow row, Vector2 rowMin, float s, int visual)
    {
        Vector2 iconMin = rowMin;
        Vector2 iconMax = rowMin + LootFrameUiLaw.RowIconSize * s;

        // Parchment name plate behind the text (UI-QuestItemNameFrame, 130x62 at LEFT+30).
        DrawArt(dl, LootFrameUiLaw.NamePlatePath,
            LootFrameUiLaw.RowNamePlate.ScaledMin(rowMin, s),
            LootFrameUiLaw.RowNamePlate.Size, s);

        uint icon = _gameplayArt!.Handle(row.IconPath);
        if (icon != 0) dl.AddImage((nint)icon, iconMin, iconMax);
        if (row.Count > 1)
        {
            string label = row.Count.ToString();
            GameText.DrawRightAligned(dl, LootFrameUiLaw.CountFont, label,
                LootFrameUiLaw.CountRightTop(rowMin, s), s);
        }

        float namePitch = GameText.LinePitch(LootFrameUiLaw.NameFont, s);
        int maximumNameLines = Math.Max(1,
            (int)MathF.Floor(LootFrameUiLaw.RowNameBox.Height * s / namePitch));
        IReadOnlyList<string> nameLines = LootFrameUiLaw.WrapName(row.Name,
            LootFrameUiLaw.RowNameBox.Width * s, maximumNameLines,
            candidate => GameText.MeasureWidth(LootFrameUiLaw.NameFont, candidate, s));
        Vector2 nameBoxMin = LootFrameUiLaw.RowNameBox.ScaledMin(rowMin, s);
        dl.PushClipRect(nameBoxMin,
            nameBoxMin + LootFrameUiLaw.RowNameBox.ScaledSize(s), true);
        uint nameColor = ImGui.ColorConvertFloat4ToU32(row.NameColor);
        for (int line = 0; line < nameLines.Count; line++)
            GameText.Draw(dl, LootFrameUiLaw.NameFont, nameLines[line],
                LootFrameUiLaw.NameLineMin(rowMin, s, line, nameLines.Count, namePitch),
                s, nameColor);
        dl.PopClipRect();

        ImGui.SetCursorScreenPos(rowMin);
        ImGui.InvisibleButton($"##loot-row-{visual}", LootFrameUiLaw.RowHitSize * s);
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left) ||
            ImGui.IsItemClicked(ImGuiMouseButton.Right);
        ItemTemplate? rowTemplate = null;
        if (!row.IsCoin && row.ItemId != 0)
            _items?.TryGet(row.ItemId, out rowTemplate);
        if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, iconMin, iconMax);
            LootFrameUiLaw.TooltipSeat tooltipSeat =
                LootFrameUiLaw.RowTooltipSeat(rowMin, s);
            if (rowTemplate is not null)
            {
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(rowTemplate, row.Count, liveInstance: RemoteTooltipInstance(
                        unchecked((int)row.RandomPropertyId)));
                OfferPreparedItemTooltip(new("item:loot-row", (ulong)visual), tooltipBody,
                    tooltipSeat.Anchor, nextWindowPivot: tooltipSeat.Pivot);
            }
            else if (row.IsCoin)
            {
                OfferOwnerAnchoredSharedGameTooltip(new("loot-coin-row", (ulong)visual),
                    [new(row.Name, GameTooltipTextTone.White)],
                    tooltipSeat.Anchor, tooltipSeat.Pivot);
            }
        }
        string itemLink = rowTemplate is null ? "" :
            LootFrameUiLaw.ItemLink(row.ItemId, rowTemplate.Name, rowTemplate.Quality);
        switch (LootFrameUiLaw.ClickAction(clicked, ImGui.GetIO().KeyCtrl,
                    ImGui.GetIO().KeyShift, ImGui.GetIO().KeyAlt, _chatEditOpen,
                    !row.IsCoin, itemLink.Length > 0))
        {
            case LootFrameUiLaw.RowClickAction.None:
                return;
            case LootFrameUiLaw.RowClickAction.DressUp:
                TryOnDressUp(row.ItemId);
                return;
            case LootFrameUiLaw.RowClickAction.InsertChat:
                InsertChatText(itemLink);
                return;
        }
        if (_net is null) return;
        if (RefuseTacticalFreezeLiveCommand("taking loot")) return;
        if (RefuseTacticalFrozenActor(_loot.Source, "take loot from it")) return;
        if (row.IsCoin) TakeLootMoney();
        else
        {
            uint displayInfoId = 0; byte slotType = 0;
            foreach (LootItem item in _loot.Items)
                if (item.Slot == row.WireSlot) { displayInfoId = item.DisplayInfoId; slotType = item.SlotType; break; }
            // LOOT_SLOT_MASTER (2): the master looter ASSIGNS this row rather than taking it —
            // the candidate menu (SMSG_LOOT_MASTER_LIST) picks who, then CMSG_LOOT_MASTER_GIVE.
            // A plain take on a master row was refused by the server without a word.
            if (slotType == 2)
            {
                if (_lootMasterCandidates.Count > 0)
                {
                    _lootMasterMenuSlot = row.WireSlot;
                    _lootMasterMenuItemId = row.ItemId;
                    // Docked to the loot frame (not the mouse): recover the frame origin from
                    // this row's seat so the panel sits beside the item it assigns.
                    _lootMasterMenuFrameOrigin = rowMin - LootFrameUiLaw.Row(visual).ScaledMin(Vector2.Zero, s);
                    _lootMasterMenuRowTop = rowMin.Y;
                    EmitInterface("loot", "master-menu", "OPEN", _loot.Source, $"slot={row.WireSlot}");
                    return;
                }
                // The row IS master loot but no candidate list arrived, so there is nobody to
                // assign it to. Sending the take anyway earned a silent server refusal, which is
                // exactly the "I can't loot and nothing happens" report.
                ShowUiError("Only the master looter can assign this item.");
                EmitInterface("loot", "master-menu", "NO-CANDIDATES", _loot.Source, $"slot={row.WireSlot}");
                return;
            }
            // LOOT_SLOT_ROLL_ONGOING (1): a group-loot roll owns this row until it resolves. The
            // roll window is the only way to claim it; a take is refused without a word.
            if (slotType == 1)
            {
                ShowUiError("A roll is already in progress for that item.");
                EmitInterface("loot", "row", "ROLL-ONGOING", _loot.Source, $"slot={row.WireSlot}");
                return;
            }
            // LOOT_SLOT_LOCKED (3): shown in red, never takeable by this viewer.
            if (slotType == 3)
            {
                ShowUiError("You don't have permission to loot that item.");
                EmitInterface("loot", "row", "LOCKED", _loot.Source, $"slot={row.WireSlot}");
                return;
            }
            if (displayInfoId != 0) PlayItemPickupSound(displayInfoId);
            bool sent = _net.AutostoreLootItem(row.WireSlot);
            EmitInterface("loot", "item", sent ? "SENT" : "SEND_FAILED", _loot.Source,
                $"slot={row.WireSlot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(row.WireSlot))}");
        }
    }

    private void DrawLootPagerButton(ImDrawListPtr dl, Vector2 min, float s, bool up, bool enabled)
    {
        DrawArt(dl, LootFrameUiLaw.PagerPath(up, enabled), min,
            LootFrameUiLaw.PagerUp.Size, s);
        if (!enabled) return;
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton(up ? "##loot-page-up" : "##loot-page-down",
                LootFrameUiLaw.PagerUp.ScaledSize(s)))
            _lootPage += up ? -1 : 1;
    }

    private static Vector4 ItemQualityColor(uint quality) => quality switch
    {
        0 => new Vector4(0.62f, 0.62f, 0.62f, 1f),
        2 => new Vector4(0.12f, 1f, 0f, 1f),
        3 => new Vector4(0f, 0.44f, 0.87f, 1f),
        4 => new Vector4(0.64f, 0.21f, 0.93f, 1f),
        5 => new Vector4(1f, 0.50f, 0f, 1f),
        6 => new Vector4(0.90f, 0.80f, 0.50f, 1f),
        _ => Vector4.One,
    };

    /// <summary>"4 Gold 25 Silver 3 Copper", dropping zero denominations; 0 = "0 Copper".</summary>
    private static string FormatMoney(uint copper)
    {
        uint gold = copper / 10000, silver = copper / 100 % 100, coin = copper % 100;
        var parts = new List<string>(3);
        if (gold > 0) parts.Add($"{gold} Gold");
        if (silver > 0) parts.Add($"{silver} Silver");
        if (coin > 0 || parts.Count == 0) parts.Add($"{coin} Copper");
        return string.Join(" ", parts);
    }
}
