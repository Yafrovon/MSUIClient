using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ItemTemplateCache? _items;
    private ItemDisplayTable? _itemDisplays;
    private ItemGroupSoundsCatalog? _itemGroupSounds;
    private uint? _previousCoinage;
    private ulong _previousCoinageGuid;
    private bool _backpackOpen;
    private bool _backpackKeyWasDown;
    private bool _openBagsKeyWasDown;
    private bool _partyInventoryKeyWasDown;
    private int _carriedContainer = InventoryUiLaw.EmptyContainer;
    private int _carriedSlot = -1;
    private int? _carriedCount;
    private readonly bool[] _equippedBagOpen = new bool[4];
    private readonly bool[] _bankBagOpen = new bool[InventoryUiLaw.BankBagCount];
    private bool _keyringOpen;
    private readonly List<int> _bagWindowOrder = [];
    private readonly Dictionary<int, Vector2> _bagWindowPositions = [];
    private readonly Dictionary<(int Container, int Slot), PendingBagLock> _pendingBagLocks = [];
    private int _splitContainer = InventoryUiLaw.EmptyContainer;
    private int _splitSlot = -1;
    private int _splitMaximum;
    private Vector2 _splitOwnerTopRight;
    private bool _splitOwnerVisible;
    private bool _splitTyped;
    private bool _shoppingTooltipParityCompletionPending;
    private bool _shoppingTooltipParityRendererCollected;
    private ImmutableArray<ShoppingTooltipParityExpectation>
        _shoppingTooltipParityExpectations = [];
    private int _splitCount = 1;
    private int _itemPushContainer = InventoryUiLaw.EmptyContainer;
    private uint _itemPushEntry;
    private double _itemPushStartedAt = double.NegativeInfinity;
    private readonly Dictionary<int, Vector2> _bagButtonPositions = [];
    private int _liveEquipmentSignature;

    /// <summary>
    /// Force the next SyncLiveEquipmentModel pass to rebuild. Every roster-built kit
    /// (ApplyServerCharacter / ApplyControlledCharacter) reinstalls sheath 0 on the local body
    /// without touching the inventory GUIDs the signature is taken over, so without this the
    /// sync early-returns and the good sheath bytes never come back — which is what made a
    /// free-view or possession round trip snap the weapons back into the hands.
    /// </summary>
    private void InvalidateLiveEquipment() => _liveEquipmentSignature = 0;
    private InventoryTransition? _pendingInventoryTransition;
    private long _pendingBagOperation;
    private readonly Dictionary<string, string> _inventoryGlobalStrings = [];
    private bool _inventoryGlobalStringsLoaded;
    private string? _inventoryGlobalStringsSource;
    private readonly ItemEnchantTimerState _itemEnchantTimers = new();
    private readonly Dictionary<uint, uint> _itemProficiencies = [];

    /// <summary>
    /// Inventory opcodes do not carry an actor guid: the server always applies them to the
    /// logged-in character. Plain Free View and possessed-body inspection therefore stay
    /// read-only even though possession may author guid-addressed controlled-body actions.
    /// </summary>
    private bool CanAuthorSessionInventory =>
        CanAuthorControlledGameplay && ControlledGuid == LocalPlayerGuid;

    private sealed record InventoryTransition(string Kind, ulong ItemGuid, uint Entry, int SourceSlot,
        int DestinationSlot, double SentAt);
    private sealed record PendingBagLock(ulong Guid, uint Count, double SentAt, long Operation);

    private void InitInventory()
    {
        if (_mpq is null) return;
        ItemDisplayTable? displays = null;
        try
        {
            byte[]? bytes = _mpq.ReadFile(ItemDisplayTable.MpqPath);
            displays = bytes is null ? null : ItemDisplayTable.Parse(bytes);
        }
        catch (Exception ex) { Console.WriteLine($"[items] display catalog failed: {ex.Message}"); }
        _itemDisplays = displays;
        try { _itemGroupSounds = ItemGroupSoundsCatalog.Load(_mpq); }
        catch (Exception ex) { Console.WriteLine($"[items] sound catalog failed: {ex.Message}"); }
        _items = new ItemTemplateCache(displays);
        if (_creatures is not null)
            _creatures.PlayerItemResolver = entry =>
            {
                // Require-then-lookup: the renderer no longer depends on DiscoverItemTemplates
                // having walked this entity first (ask-once cache, so re-asking is free).
                if (_items is not null && _net is not null) _items.Require(entry, 0, _net);
                return (_items!.TryGet(entry, out ItemTemplate? item), item);
            };
        InitBank();
        InitMail();
        InitAuction();
        InitProfessions();
        InitGuild();
        InitTabard();
        InitTalents();
    }

    private void UpdateInventoryInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenBackpack);
        if (down && !_backpackKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
            bool all = InventoryUiLaw.BindingAction(shift) ==
                InventoryUiLaw.BagBindingAction.ToggleAllBags;
            // Free view: B / Shift+B opens the PRIMARY's bags — possess-on-open (owner 2026-08-27),
            // editable because the server routes swaps/use via GetSuiActor. The open is DEFERRED
            // until control actually reaches the subject (a bot possessed, or your own body released
            // back to when the primary is yourself), so the panel shows the subject's bags instead
            // of flashing your own before the hand-over lands — and Tab-to-self + B now returns you
            // to your own bags. A body view keeps the plain vanilla toggle.
            if (_freeView) OpenPrimaryBags(all);
            else if (all) ToggleAllBags();
            else ToggleBackpack();
        }
        _backpackKeyWasDown = down;

        // I IS THE INVENTORY KEY, and what that means follows the camera. Under the commander
        // camera the thing you actually want is everyone's bags side by side, so I is the party
        // logistics view there; on a body there is no party pane to open, so it falls back to
        // opening all of your own bags.
        //
        // The personal path uses its own open/close rule, not vanilla's Shift+B one:
        // see ShouldOpenEveryCarriedBag.
        bool bagsDown = BindingDown(GameBinding.OpenBags);
        if (bagsDown && !_openBagsKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            if (_freeView)
            {
                if (_partyInventoryOpen) _partyInventoryOpen = false;
                else OpenPartyInventory(_freecamSelection.Count == 1
                    ? _freecamSelection[0] : LocalPlayerGuid);
            }
            else ToggleEveryCarriedBag();
        }
        _openBagsKeyWasDown = bagsDown;

        bool partyBagsDown = BindingDown(GameBinding.OpenPartyInventory);
        if (partyBagsDown && !_partyInventoryKeyWasDown && !typing &&
            _net is { IsInWorld: true })
        {
            if (_partyInventoryOpen) _partyInventoryOpen = false;
            else OpenPartyInventory(_freecamSelection.Count == 1
                ? _freecamSelection[0] : LocalPlayerGuid);
        }
        _partyInventoryKeyWasDown = partyBagsDown;
    }

    private void DiscoverItemTemplates()
    {
        if (_net is null || _items is null) return;
        foreach (WorldEntity entity in _entities.Entities.Values)
        {
            if (entity.Type is ObjectTypeId.Item or ObjectTypeId.Container)
                _items.Require(entity.Entry, entity.Guid, _net);
            if (entity.IsPlayer)
                for (int slot = 0; slot < 19; slot++)
                    _items.Require(entity.Fields.PlayerVisibleItemEntry(slot), 0, _net);
        }
        SyncLiveEquipmentModel();
        ObserveInventoryTransition();
        ObserveBagLocks();
        ObserveBankTransition();
        ObserveSkillRankUps();
        ObserveProfessionSkillTransition();
        ObserveProfessionProductTransition();
        ObserveTalentTransition();
        ObserveMoneySound();
    }

    private void ObserveMoneySound()
    {
        // [SUI] P4b: the purse that dings is the purse the UI shows — the driven
        // bot's while possessing (its coinage arrives via re-snapshots after every
        // buy/sell/repair), the session character's otherwise. Watching only the
        // logged-in character left possessed vendoring silent. A body switch
        // reseeds, so the hand-off itself never plays a phantom coin.
        ulong subject = ControlledGuid;
        if (_net is null || !_entities.TryGet(subject, out WorldEntity player))
        {
            _previousCoinage = null;
            return;
        }
        if (subject != _previousCoinageGuid)
        {
            _previousCoinageGuid = subject;
            _previousCoinage = null;
        }
        uint money = player.Fields.Coinage;
        uint? previous = _previousCoinage;
        _previousCoinage = money;
        if (World.Sound.AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
            AcquisitionSoundLaw.PlayCoin(previous, money))
            PlayUiSound(AcquisitionSoundLaw.CoinCue, AcquisitionSoundLaw.CoinCategory);
    }

    private void PlayItemPickupSound(uint displayInfoId)
    {
        if (!World.Sound.AudioFeaturePolicy.ExpandedWorldAudioEnabled) return;
        uint? kit = AcquisitionSoundLaw.PickupKit(displayInfoId, _itemDisplays, _itemGroupSounds);
        if (kit is null) return;
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        _spellSounds?.Play(kit, LocalPlayerGuid, listener, listener,
            forceLoop: false, trackHold: false, category: AcquisitionSoundLaw.ItemPickupCategory);
    }

    private void ObserveInventoryTransition()
    {
        if (_pendingInventoryTransition is not { } pending || _net is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        int equipped = Enumerable.Range(0, 19).FirstOrDefault(i => player.Fields.PlayerInventorySlot(i) == pending.ItemGuid, -1);
        int backpack = Enumerable.Range(0, 16).FirstOrDefault(i => player.Fields.PlayerBackpackSlot(i) == pending.ItemGuid, -1);
        bool complete = pending.Kind == "equip" ? equipped >= 0 : backpack >= 0;
        if (complete)
        {
            EmitInterface("inventory", "equipment", pending.Kind == "equip" ? "EQUIPPED" : "UNEQUIPPED",
                pending.ItemGuid, $"item={pending.Entry};from={pending.SourceSlot};equipped={equipped};backpack={backpack}");
            _pendingInventoryTransition = null;
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("inventory", "equipment", "TIMEOUT", pending.ItemGuid,
                $"kind={pending.Kind};item={pending.Entry};from={pending.SourceSlot};to={pending.DestinationSlot}");
            _pendingInventoryTransition = null;
        }
    }

    private bool EquipBackpackEntry(uint entry)
    {
        if (!CanAuthorControlledGameplay || _net is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        if (!CanAuthorSessionInventory) return false;
        for (int slot = 0; slot < 16; slot++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(slot);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || item.Entry != entry) continue;
            bool sent = _net.AutoEquipItem(255, (byte)(23 + slot));
            EmitInterface("inventory", "equip-send", sent ? "SENT" : "SEND_FAILED", guid,
                $"item={entry};bag=255;slot={23 + slot};body={Convert.ToHexString(WorldSession.BuildAutoEquipBody(255, (byte)(23 + slot)))}");
            if (sent) _pendingInventoryTransition = new("equip", guid, entry, 23 + slot, -1, NowSeconds());
            return sent;
        }
        EmitInterface("inventory", "equip-send", "REFUSED", 0, $"item={entry};reason=not-in-backpack");
        return false;
    }

    private bool UnequipSlot(int slot)
    {
        if (!CanAuthorSessionInventory || _net is null || slot is < 0 or >= 19 ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        ulong guid = player.Fields.PlayerInventorySlot(slot);
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item)) return false;
        int empty = Enumerable.Range(0, 16).FirstOrDefault(i => player.Fields.PlayerBackpackSlot(i) == 0, -1);
        if (empty < 0) { EmitInterface("inventory", "unequip-send", "REFUSED", guid, "reason=backpack-full"); return false; }
        byte destination = (byte)(23 + empty);
        bool sent = _net.SwapInventoryItems((byte)slot, destination);
        EmitInterface("inventory", "unequip-send", sent ? "SENT" : "SEND_FAILED", guid,
            $"item={item.Entry};from={slot};to={destination};body={Convert.ToHexString(WorldSession.BuildSwapInventoryBody((byte)slot, destination))}");
        if (sent) _pendingInventoryTransition = new("unequip", guid, item.Entry, slot, destination, NowSeconds());
        return sent;
    }

    private bool InspectCharacterInventory()
    {
        if (_net is null || _items is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        ObjectFields f = player.Fields;
        EmitInterface("character", "stats", "WIRE-SNAPSHOT", ControlledGuid,
            $"level={player.Level};health={f.Health}/{f.MaxHealth};stats={string.Join(',', Enumerable.Range(0, 5).Select(f.Stat))};armor={f.Resistance(0)};attack={f.AttackPower};damage={f.MinDamage:R}-{f.MaxDamage:R};coin={f.Coinage}");
        int equipped = 0, backpack = 0, resolved = 0;
        foreach (int slot in Enumerable.Range(0, 19).Concat(Enumerable.Range(23, 16)))
        {
            ulong guid = slot < 19 ? f.PlayerInventorySlot(slot) : f.PlayerBackpackSlot(slot - 23);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity instance)) continue;
            if (slot < 19) equipped++; else backpack++;
            _items.Require(instance.Entry, guid, _net);
            if (!_items.TryGet(instance.Entry, out ItemTemplate? item) || item is null) continue;
            resolved++;
            EmitInterface("inventory", "item-string", "VERIFIED", guid,
                $"slot={slot};entry={item.Entry};count={Math.Max(1, instance.Fields.ItemStackCount)};name={SanitizeEvidence(item.Name)};" +
                $"quality={item.Quality};inventoryType={item.InventoryType};itemLevel={item.ItemLevel};requiredLevel={item.RequiredLevel};" +
                $"armor={item.Armor};stats={string.Join(',', item.Stats.Select(x => $"{x.Type}:{x.Value}"))};" +
                $"durability={instance.Fields.ItemDurability}/{instance.Fields.ItemMaxDurability}");
        }
        EmitInterface("inventory", "snapshot", "COMPLETE", ControlledGuid,
            $"equipped={equipped};backpack={backpack};resolved={resolved}");
        return true;
    }

    private void SyncLiveEquipmentModel()
    {
        // The session character only. Every field this pass reads — PLAYER_FIELD_INV_SLOT_*
        // and the item objects it points at — is private and exists for no one else, so run
        // against a possessed bot it would resolve nineteen empty slots and install a naked
        // kit over the good one ApplyControlledCharacter built from the bot's public entries.
        if (_net is null || _items is null || _character is not { Loaded: true } ||
            ControlledGuid != LocalPlayerGuid ||
            !_entities.TryGet(LocalPlayerGuid, out WorldEntity player)) return;
        // An async appearance diff queued by ApplyServerCharacter is still in flight. It
        // finalizes with the kit it was QUEUED with - the roster fallback (sheath 0) whenever
        // the item templates had not landed yet - and installs that kit over whatever is on the
        // body at that moment. Installing the inventory-object kit now would be undone 100-200 ms
        // later while the signature stayed settled, so nothing ever put it back: the staff sat
        // on the over-the-shoulder point instead of the large-weapon back point (2026-09-02).
        // Wait for the job to land, then this pass wins by running last.
        if (!_character.AppearanceReady) return;
        var resolved = new List<(int Slot, ItemTemplate Item)>();
        var hash = new HashCode();
        for (int slot = 0; slot < 19; slot++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(slot);
            hash.Add(guid);
            if (guid == 0) continue;
            // A slot whose item object or template has not landed yet skips itself; it must not
            // abort the pass. These were whole-pass `return`s, and one unresolved slot out of
            // nineteen was enough to leave the local body on BuildEquipment's kit indefinitely.
            // Every piece there carries sheath 0, which ResolveAttachment maps to -1 — so a
            // sheathed weapon was not moved to the back, it was not drawn at all.
            if (!_entities.TryGet(guid, out WorldEntity instance)) continue;
            for (int enchantSlot = 0; enchantSlot < 7; enchantSlot++)
                hash.Add(instance.Fields.ItemEnchantmentId(enchantSlot));
            _items.Require(instance.Entry, instance.Guid, _net);
            if (!_items.TryGet(instance.Entry, out ItemTemplate? item) || item is null) continue;
            resolved.Add((slot, item));
        }
        // Part of the signature so an incomplete pass retries exactly once per slot that lands,
        // instead of either rebuilding every frame or never upgrading again.
        hash.Add(resolved.Count);
        int signature = hash.ToHashCode();
        if (signature == _liveEquipmentSignature) return;
        var equipment = new CharacterEquipment { GuildEmblem = _tabardDesign };
        foreach (var resolvedItem in resolved)
        {
            ItemTemplate item = resolvedItem.Item;
            int heldSlot = resolvedItem.Slot switch { 15 => 0, 16 => 1, 17 => 2, _ => -1 };
            byte querySheath = (byte)item.Sheath;
            byte liveSheath = heldSlot >= 0 ? player.Fields.VirtualItemSheath(heldSlot) : (byte)0;
            byte sheath = liveSheath != 0 ? liveSheath : querySheath;
            equipment.Add(item.Name, item.DisplayInfoId, (int)item.InventoryType, resolvedItem.Slot,
                (byte)item.Class, (byte)item.Subclass, (byte)item.Material, sheath,
                player.Fields.PlayerInventorySlot(resolvedItem.Slot) is ulong itemGuid &&
                _entities.TryGet(itemGuid, out WorldEntity instance)
                    ? Enumerable.Range(0, 7)
                        .Select(enchantSlot => instance.Fields.ItemEnchantmentId(enchantSlot))
                        .ToArray()
                    : []);
        }
        if (EquipmentVisuallyMatches(_character.Equipment, equipment))
        {
            // The character-select renderer already composited this exact
            // outfit. Live item GUIDs arrive later and change the transport
            // signature, not the visible model; rebuilding here allocated
            // 120-145 MB and forced gen2 during Terrain.
            _liveEquipmentSignature = signature;
            return;
        }
        ReportLocalKit("inventory-objects", equipment);
        _character.Equipment = equipment;
        _character.ApplyEquipment();
        _liveEquipmentSignature = signature;
        _playerPortraitDirty = true;
        _paperDollDirty = true;
    }

    private static bool EquipmentVisuallyMatches(CharacterEquipment current,
        CharacterEquipment incoming)
    {
        if (current.Pieces.Count != incoming.Pieces.Count) return false;
        foreach (CharacterEquipment.Piece piece in incoming.Pieces)
        {
            bool found = current.Pieces.Any(existing =>
                existing.DisplayId == piece.DisplayId &&
                existing.InventoryType == piece.InventoryType &&
                existing.EquipmentSlot == piece.EquipmentSlot &&
                existing.ItemClass == piece.ItemClass &&
                existing.ItemSubclass == piece.ItemSubclass &&
                existing.Material == piece.Material &&
                existing.Sheath == piece.Sheath &&
                existing.Enchants.SequenceEqual(piece.Enchants));
            if (!found) return false;
        }
        return true;
    }

    private void DrawInventory()
    {
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        _splitOwnerVisible = false;
        LayoutBagWindows(player);
        DrawBagBar();
        DrawBackpack();
        DrawEquippedBagWindows();
        DrawBankBagWindows();
        DrawKeyringWindow(player);
        DrawStackSplit();
        DrawItemPushAnimation();
        DrawCarriedItem(player, GameplayUiScale());
        TryOpenDeleteItemConfirmation();
    }

    private void DrawBackpack()
    {
        if (!_backpackOpen || _net is null || _items is null || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        float scale = GameplayUiScale();
        Vector2 frameSize = new(192f, 240f);
        if (!_bagWindowPositions.TryGetValue(0, out Vector2 frameMin)) return;
        Vector2 windowMin = frameMin - new Vector2(64f, 0) * scale;

        if (_uiParityArmed && _uiParityPanel == "backpack")
        {
            BeginUiParityFrame(frameMin, scale);
            CollectUiParityDraw("ContainerFrame1", "Frame", frameMin, frameSize * scale, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT", 0, 70));
            CollectUiParityDraw("ContainerFrame1Portrait", "Texture", frameMin + new Vector2(7, 5) * scale,
                new Vector2(40) * scale, "ContainerFrame1",
                new(@"Interface\Buttons\Button-Backpack-Up", 0xffffffff, "BACKGROUND", "TOPLEFT",
                    "ContainerFrame1", "TOPLEFT", 7, -5));
            CollectUiParityDraw("ContainerFrame1BackgroundTop", "Texture", windowMin,
                new Vector2(256) * scale, "ContainerFrame1",
                new(@"Interface\ContainerFrame\UI-BackpackBackground", 0xffffffff, "ARTWORK", "TOPRIGHT",
                    "ContainerFrame1", "TOPRIGHT", 0, 0));
            CollectUiParityDraw("ContainerFrame1Name", "FontString", frameMin + new Vector2(47, 10) * scale,
                new Vector2(112, 12) * scale, "ContainerFrame1",
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", "ContainerFrame1", "TOPLEFT", 47, -10,
                    @"Fonts\FRIZQT__.TTF", 12));
            CollectUiParityDraw("ContainerFrame1CloseButton", "Button", frameMin + new Vector2(160, 1) * scale,
                new Vector2(32) * scale, "ContainerFrame1",
                new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", "ContainerFrame1", "TOPRIGHT", 0, -1,
                    Enabled: true, InteractionState: "normal",
                    HitMin: frameMin + new Vector2(160, 1) * scale,
                    HitMax: frameMin + new Vector2(192, 33) * scale));
            CollectUiParityDraw("ContainerFrame1CloseButton/NormalTexture", "NormalTexture",
                frameMin + new Vector2(160, 1) * scale, new Vector2(32) * scale, "ContainerFrame1CloseButton",
                new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up", 0xffffffff, "ARTWORK", "CENTER",
                    "ContainerFrame1CloseButton", "CENTER", 0, 0));
        }

        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256f, 256f) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##backpack", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        uint portrait = _gameplayArt.Handle(@"Interface\Buttons\Button-Backpack-Up.blp");
        if (portrait != 0)
        {
            Vector2 min = frameMin + new Vector2(7f, 5f) * scale;
            dl.AddImage((nint)portrait, min, min + new Vector2(40f) * scale);
        }
        uint background = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-BackpackBackground.blp");
        if (background != 0)
            dl.AddImage((nint)background, windowMin, windowMin + new Vector2(256f) * scale);

        GameText.Draw(dl, "GameFontNormal", "Backpack",
            frameMin + new Vector2(47f, 10f) * scale, scale);

        Vector2 closeMin = frameMin + new Vector2(160, 1) * scale;
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * scale);
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton("##backpack-close", new Vector2(32) * scale);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(0, false);

        for (int gameSlot = 0; gameSlot < 16; gameSlot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(16, gameSlot, 240f, backpack: true);
            Vector2 slotMin = frameMin + new Vector2(cell.X, cell.Y) * scale;
            DrawInventorySlot(dl, player, 0, gameSlot, slotMin, scale, $"pack-{gameSlot}");
        }

        DrawMoney(dl, frameMin, player.Fields.Coinage, scale);
        if (_uiParityArmed && _uiParityPanel == "backpack") MarkUiParityFrameComplete();
        ImGui.End();

    }

    private void DrawBagBar()
    {
        if (_freeView) return;   // commander console: no body chrome
        if (_net is not { IsInWorld: true } || _gameplayArt is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 barMin = GameplayBarMin(display, s);
        Vector2 backpackMin = barMin + new Vector2(981f, 14f) * s;
        Vector2 firstBagMin = backpackMin - new Vector2(168f, 0f) * s;
        bool hasKey = HasKey(player);
        Vector2 windowMin = barMin + new Vector2(hasKey ? 774f : 798f, 0f) * s;
        float windowWidth = hasKey ? 250f : 226f;
        CollectGameplayLayout("bag-cluster", hasKey ? 774f : 798f, 715f, windowWidth, 53f,
            windowMin, new Vector2(windowWidth, 53f) * s);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(windowWidth, 53) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##bag-bar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool parityProof = _uiParityArmed && _uiParityPanel == "bag-bar";
        const string parityRoot = "MainMenuBarBagButtons";
        if (parityProof)
        {
            BeginUiParityFrame(windowMin, s);
            CollectUiParityDraw(parityRoot, "Frame", windowMin, new Vector2(windowWidth, 53) * s, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "MainMenuBar", "BOTTOMRIGHT",
                    hasKey ? -250 : -226, 0));
        }
        _bagButtonPositions.Clear();
        int[] containers = [4, 3, 2, 1, 0];
        for (int i = 0; i < containers.Length; i++)
        {
            int container = containers[i];
            Vector2 min = container == 0 ? backpackMin : firstBagMin + new Vector2(i * 42f, 0f) * s;
            _bagButtonPositions[container] = min;
            float buttonSize = container == 0 ? 37f : 36f;
            float authoredX = container == 0 ? 981f : 813f + i * 42f;
            string layoutId = container == 0 ? "backpack" : $"bag-slot-{container}";
            CollectGameplayLayout(layoutId, authoredX, 729f, buttonSize, buttonSize,
                min, new Vector2(buttonSize) * s);
            string art = container == 0 ? @"Interface\Buttons\Button-Backpack-Up" : @"Interface\Paperdoll\UI-PaperDoll-Slot-Bag";
            ItemTemplate? bagTemplate = null;
            ulong bagGuid = 0;
            int equipmentSlot = -1;
            if (container != 0)
            {
                int bagIndex = container - 1;
                equipmentSlot = 19 + bagIndex;
                bagGuid = player.Fields.PlayerInventorySlot(equipmentSlot);
                if (bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag))
                {
                    _items.Require(bag.Entry, bag.Guid, _net);
                    if (_items.TryGet(bag.Entry, out ItemTemplate? template) && template is not null)
                    { bagTemplate = template; art = template.IconPath; }
                }
            }
            bool locked = container != 0 && IsInventorySlotLocked(InventoryUiLaw.EquipmentContainer, equipmentSlot);
            bool menuDisabled = _settingsOpen && InventoryUiLaw.DisableWithGameMenu(container);
            uint tint = menuDisabled || locked ? 0xff666666 : 0xffffffff;
            uint icon = container == 0 ? _gameplayArt.Handle(art) : _gameplayArt.CircularHandle(art);
            string parityButton = container == 0 ? "MainMenuBarBackpackButton" : $"CharacterBag{container - 1}Slot";
            bool checkedState = container == 0 ? _backpackOpen : _equippedBagOpen[container - 1];
            if (parityProof)
            {
                CollectUiParityDraw(parityButton, "CheckButton", min, new Vector2(buttonSize) * s,
                    parityRoot, new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", parityRoot, "TOPLEFT",
                        (min.X - windowMin.X) / s, -((min.Y - windowMin.Y) / s),
                        Enabled: !menuDisabled && !locked,
                        InteractionState: menuDisabled || locked ? "disabled" : checkedState ? "checked" : "normal",
                        HitMin: min, HitMax: min + new Vector2(buttonSize) * s));
                CollectUiParityDraw(parityButton + "IconTexture",
                    container == 0 ? "Texture" : "MaskedTexture", min, new Vector2(buttonSize) * s,
                    parityButton, new(art, tint, "BACKGROUND", "CENTER", parityButton, "CENTER", 0, 0,
                        TexCoords: "0|0|1|1",
                        ClipRect: new Vector4(min.X, min.Y, min.X + buttonSize * s, min.Y + buttonSize * s),
                        ClipMask: container == 0 ? "" : "ALPHA_CIRCLE_INSCRIBED",
                        BlendMode: "BLEND", Visible: icon != 0));
            }
            BagIconContainmentLaw.Geometry barProof = BagIconContainmentLaw.BagBar;
            bool drawDynamicIcon = container == 0 || BagContainmentDrawIcon(parityButton + "IconTexture",
                min, new Vector2(buttonSize) * s,
                min - new Vector2(barProof.ApertureOffset) * s,
                new Vector2(barProof.CaptureSize) * s);
            if (icon != 0 && drawDynamicIcon) dl.AddImage((nint)icon, min, min + new Vector2(buttonSize) * s,
                Vector2.Zero, Vector2.One, tint);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##bag-button-{container}", new Vector2(buttonSize) * s);
            bool hovered = ImGui.IsItemHovered();
            if (!_settingsOpen && ImGui.IsItemClicked())
            {
                switch (InventoryUiLaw.BagBarAction(container, HasCarriedItem, bagGuid != 0))
                {
                    case InventoryUiLaw.BagBarClickAction.ToggleBackpack: ToggleBackpack(); break;
                    case InventoryUiLaw.BagBarClickAction.ToggleBag:
                        SetBagWindowOpen(container, !_equippedBagOpen[container - 1]); break;
                    case InventoryUiLaw.BagBarClickAction.PickupOrPlace:
                        PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, bagGuid); break;
                }
            }
            if (container != 0 && !_settingsOpen)
                HandleBagBarDrag(container, equipmentSlot, bagGuid, bagTemplate, min, buttonSize, s);
            uint ring = container != 0 ? _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2") : 0;
            if (ring != 0)
            {
                Vector2 center = min + new Vector2(18f) * s, half = new(33f * s);
                dl.AddImage((nint)ring, center - half, center + half);
                if (parityProof)
                    CollectUiParityDraw(parityButton + "NormalTexture", "NormalTexture",
                        center - half, half * 2, parityButton,
                        new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff, "ARTWORK", "CENTER",
                            parityButton, "CENTER", 0, 0, TexCoords: "0|0|1|1",
                            BlendMode: "BLEND", Visible: ring != 0));
            }
            if (checkedState)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + new Vector2(buttonSize) * s);
                if (parityProof)
                    CollectUiParityDraw(parityButton + "CheckedTexture", "CheckedTexture", min,
                        new Vector2(buttonSize) * s, parityButton,
                        new(@"Interface\Buttons\CheckButtonHilight", 0xffffffff, "OVERLAY", "CENTER",
                            parityButton, "CENTER", 0, 0));
            }
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                if (highlight != 0) dl.AddImage((nint)highlight, min, min + new Vector2(buttonSize) * s);
                if (parityProof)
                    CollectUiParityDraw(parityButton + "HighlightTexture", "HighlightTexture", min,
                        new Vector2(buttonSize) * s, parityButton,
                        new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "OVERLAY", "CENTER",
                            parityButton, "CENTER", 0, 0));
                string tooltipText = container == 0
                    ? $"Backpack ({BindingText(GameBinding.OpenBackpack)})"
                    : bagTemplate?.Name ?? "Equip Container";
                GameTooltipOwnerKey tooltipOwner = container == 0
                    ? new("bag-button", 0)
                    : new("item:inventory-bag-bar", (ulong)container);
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipText);
                    ImGui.EndTooltip();
                });
            }
        }
        if (hasKey)
        {
            Vector2 min = firstBagMin - new Vector2(24f, 1.5f) * s;
            _bagButtonPositions[InventoryUiLaw.KeyringContainer] = min;
            if (parityProof)
            {
                CollectUiParityDraw("KeyRingButton", "CheckButton", min, new Vector2(18f, 39f) * s,
                    parityRoot, new("", 0, "IMGUI_HIT_TARGET", "RIGHT", "CharacterBag3Slot", "LEFT", -6, 0,
                        Enabled: !_settingsOpen,
                        InteractionState: _settingsOpen ? "disabled" : _keyringOpen ? "checked" : "normal",
                        HitMin: min, HitMax: min + new Vector2(18f, 39f) * s));
                CollectUiParityDraw("KeyRingButtonNormalTexture", "NormalTexture", min,
                    new Vector2(18f, 39f) * s, "KeyRingButton",
                    new(@"Interface\Buttons\UI-Button-KeyRing", 0xffffffff, "ARTWORK", "CENTER",
                        "KeyRingButton", "CENTER", 0, 0,
                        TexCoords: "0|0|0.5625|0.609375", BlendMode: "BLEND"));
            }
            uint icon = _gameplayArt.Handle(InventoryUiLaw.KeyringNormalTexture);
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(18f, 39f) * s,
                Vector2.Zero, InventoryUiLaw.KeyringUvMaximum, 0xffffffff);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##keyring-button", new Vector2(18f, 39f) * s);
            bool keyringPushed = ImGui.IsItemActive();
            bool keyringHovered = ImGui.IsItemHovered();
            if (!_settingsOpen && ImGui.IsItemClicked())
            {
                if (HasCarriedItem) PutCarriedItemInKeyring(player);
                else SetBagWindowOpen(InventoryUiLaw.KeyringContainer, !_keyringOpen);
            }
            if (keyringPushed)
            {
                uint pushed = _gameplayArt.Handle(InventoryUiLaw.KeyringPushedTexture);
                if (pushed != 0) dl.AddImage((nint)pushed, min, min + new Vector2(18f, 39f) * s,
                    Vector2.Zero, InventoryUiLaw.KeyringUvMaximum);
            }
            if (_keyringOpen)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + new Vector2(18f, 39f) * s);
            }
            if (keyringHovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(InventoryUiLaw.KeyringHighlightTexture);
                if (highlight != 0) dl.AddImage((nint)highlight, min,
                    min + new Vector2(18f, 39f) * s, Vector2.Zero,
                    InventoryUiLaw.KeyringUvMaximum);
                OfferPreservedSharedGameTooltipRenderer(new("keyring-button", 0), () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Keyring");
                    ImGui.EndTooltip();
                });
            }
            if (!_settingsOpen) HandleKeyringDropTarget(player);
        }
        if (parityProof) MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawEquippedBagWindows()
    {
        if (_net is null || _gameplayArt is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            if (!_equippedBagOpen[bagIndex]) continue;
            ulong guid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag) ||
                bag.Type is not ObjectTypeId.Container) { SetBagWindowOpen(bagIndex + 1, false); continue; }
            _items.Require(bag.Entry, bag.Guid, _net);
            _items.TryGet(bag.Entry, out ItemTemplate? template);
            int slots = (int)Math.Clamp(bag.Fields.ContainerNumSlots, 1, InventoryUiLaw.MaxContainerSlots);
            if (!_bagWindowPositions.TryGetValue(bagIndex + 1, out Vector2 frameMin)) continue;
            DrawContainerBagWindow(frameMin, s, bagIndex + 1, bag, template, slots);
        }
    }

    private void DrawBankBagWindows()
    {
        if (!_bankOpen || _net is null || _gameplayArt is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        float scale = GameplayUiScale();
        for (int index = 0; index < InventoryUiLaw.BankBagCount; index++)
        {
            int container = InventoryUiLaw.BankBagContainerFirst + index;
            if (!_bankBagOpen[index]) continue;
            ulong guid = player.Fields.PlayerBankBagSlot(index);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag) ||
                bag.Type is not ObjectTypeId.Container)
            {
                SetBagWindowOpen(container, false);
                continue;
            }
            _items.Require(bag.Entry, bag.Guid, _net);
            _items.TryGet(bag.Entry, out ItemTemplate? template);
            int slots = (int)Math.Clamp(bag.Fields.ContainerNumSlots, 1,
                InventoryUiLaw.MaxContainerSlots);
            if (!_bagWindowPositions.TryGetValue(container, out Vector2 frameMin)) continue;
            DrawContainerBagWindow(frameMin, scale, container, bag, template, slots);
        }
    }

    private void DrawContainerBagWindow(Vector2 p, float s, int container, WorldEntity bag,
        ItemTemplate? bagTemplate, int slots)
    {
        InventoryUiLaw.BackgroundGeometry geometry = InventoryUiLaw.Background(slots);
        float height = geometry.Height;
        ImGui.SetNextWindowPos(p - new Vector2(64, 0) * s, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin($"##bag-window-{container}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 artMin = p - new Vector2(64, 0) * s;
        Vector2 portraitMin = p + new Vector2(7, 5) * s;
        bool parityProof = _uiParityArmed && _uiParityPanel == "equipped-bag" &&
            container is >= 1 and <= 4 && container == _uiParityEquippedBagContainer;
        string parityRoot = $"ContainerFrameBag{container}";
        if (parityProof)
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw(parityRoot, "Frame", p, new Vector2(192, height) * s, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT", 0, 70));
            CollectUiParityDraw(parityRoot + "Portrait", "MaskedTexture", portraitMin,
                new Vector2(40) * s, parityRoot,
                new(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up", 0xffffffff,
                    "BACKGROUND", "TOPLEFT", parityRoot, "TOPLEFT", 7, -5,
                    TexCoords: "0|0|1|1",
                    ClipRect: new Vector4(portraitMin.X, portraitMin.Y,
                        portraitMin.X + 40 * s, portraitMin.Y + 40 * s),
                    ClipMask: "ALPHA_CIRCLE_INSCRIBED", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "BackgroundTop", "TextureUv", artMin,
                new Vector2(256, geometry.TopHeight) * s, parityRoot,
                new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                    "TOPRIGHT", parityRoot, "TOPRIGHT", 0, 0,
                    TexCoords: $"0|{geometry.TopUvY.X:R}|1|{geometry.TopUvY.Y:R}", BlendMode: "BLEND"));
            if (geometry.MiddleHeight > 0)
                CollectUiParityDraw(parityRoot + "BackgroundMiddle", "TextureUv",
                    artMin + new Vector2(0, geometry.TopHeight) * s,
                    new Vector2(256, geometry.MiddleHeight) * s, parityRoot,
                    new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                        "TOPRIGHT", parityRoot + "BackgroundTop", "BOTTOMRIGHT", 0, 0,
                        TexCoords: $"0|{geometry.MiddleUvY.X:R}|1|{geometry.MiddleUvY.Y:R}", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "BackgroundBottom", "TextureUv",
                artMin + new Vector2(0, height - 10) * s, new Vector2(256, 10) * s, parityRoot,
                new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                    "TOPRIGHT", geometry.MiddleHeight > 0 ? parityRoot + "BackgroundMiddle" : parityRoot + "BackgroundTop",
                    "BOTTOMRIGHT", 0, 0,
                    TexCoords: $"0|{geometry.BottomUvY.X:R}|1|{geometry.BottomUvY.Y:R}", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "Name", "FontString", p + new Vector2(47, 10) * s,
                new Vector2(112, 12) * s, parityRoot,
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", parityRoot, "TOPLEFT", 47, -10,
                    @"Fonts\FRIZQT__.TTF", 12));
        }
        // The portrait is a BACKGROUND layer in ContainerFrame.xml. Its derived handle is alpha
        // masked because the ring has transparent corners and cannot contain a square icon alone;
        // the UI-Bag-Components ARTWORK still draws over it in the authored layer order.
        uint portrait = _gameplayArt!.CircularHandle(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up");
        BagIconContainmentLaw.Geometry portraitProof = BagIconContainmentLaw.HeaderPortrait;
        bool drawPortrait = BagContainmentDrawIcon(parityRoot + "Portrait", portraitMin,
            new Vector2(portraitProof.ApertureSize) * s,
            portraitMin - new Vector2(portraitProof.ApertureOffset) * s,
            new Vector2(portraitProof.CaptureSize) * s);
        if (portrait != 0 && drawPortrait)
            dl.AddImage((nint)portrait, portraitMin, portraitMin + new Vector2(40) * s);
        uint bg = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-Bag-Components");
        if (bg != 0)
        {
            float topHeight = geometry.TopHeight, middleHeight = geometry.MiddleHeight;
            dl.AddImage((nint)bg, artMin, artMin + new Vector2(256, topHeight) * s,
                new Vector2(0, geometry.TopUvY.X), new Vector2(1, geometry.TopUvY.Y));
            if (middleHeight > 0) dl.AddImage((nint)bg, artMin + new Vector2(0, topHeight) * s,
                artMin + new Vector2(256, topHeight + middleHeight) * s,
                new Vector2(0, geometry.MiddleUvY.X), new Vector2(1, geometry.MiddleUvY.Y));
            dl.AddImage((nint)bg, artMin + new Vector2(0, height - 10) * s, artMin + new Vector2(256, height) * s,
                new Vector2(0, geometry.BottomUvY.X), new Vector2(1, geometry.BottomUvY.Y));
        }
        GameText.Draw(dl, "GameFontNormal", bagTemplate?.Name ?? "Bag",
            p + new Vector2(47, 10) * s, s);

        for (int slot = 0; slot < slots; slot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(slots, slot, height, backpack: false);
            Vector2 min = p + new Vector2(cell.X, cell.Y) * s;
            DrawInventorySlot(dl, bag, container, slot, min, s, $"bag-{container}-{slot}");
        }
        Vector2 closeMin = p + new Vector2(160, 1) * s;
        if (parityProof)
        {
            CollectUiParityDraw(parityRoot + "CloseButton", "Button", closeMin, new Vector2(32) * s,
                parityRoot, new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", parityRoot, "TOPRIGHT", 0, -1,
                    Enabled: true, InteractionState: "normal", HitMin: closeMin,
                    HitMax: closeMin + new Vector2(32) * s));
            CollectUiParityDraw(parityRoot + "CloseButton/NormalTexture", "NormalTexture", closeMin,
                new Vector2(32) * s, parityRoot + "CloseButton",
                new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up", 0xffffffff, "ARTWORK", "CENTER",
                    parityRoot + "CloseButton", "CENTER", 0, 0));
        }
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * s);
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton($"##bag-close-{container}", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(container, false);
        if (parityProof) MarkUiParityFrameComplete();
        ImGui.End();
    }

    private bool HasCarriedItem => _carriedContainer != InventoryUiLaw.EmptyContainer;

    private void PickupOrPlaceItem(int container, int slot, ulong guid, bool ignoreModifiers = false)
    {
        // Swap/split are threaded through GetSuiActor server-side (v1.1), so a possessed bot's bags
        // are editable; CanAuthorControlledGameplay already allows possession (and blocks a detached
        // Free View cursor that drives no body).
        if (!CanAuthorControlledOrSelf || _net is null) return;
        if (!HasCarriedItem)
        {
            if (guid != 0 && !IsInventorySlotLocked(container, slot))
            {
                _carriedContainer = container;
                _carriedSlot = slot;
                _carriedCount = null;
            }
            return;
        }
        WorldEntity? carried = ResolveCarriedItem();
        WorldEntity? target = ResolveInventoryItem(container, slot);
        InventoryUiLaw.MovePlan plan = InventoryUiLaw.PlanMove(_carriedContainer, _carriedSlot,
            container, slot, _carriedCount, carried?.Entry ?? 0, target?.Entry ?? 0);
        if (plan.Kind == InventoryUiLaw.MoveKind.Cancel) { ClearCarriedItem(); return; }
        if (plan.Kind == InventoryUiLaw.MoveKind.Refuse) return;
        bool sent;
        if (plan.Kind == InventoryUiLaw.MoveKind.Split)
            sent = _net.SplitItem(plan.Source.Bag, plan.Source.Slot, plan.Destination.Bag,
                plan.Destination.Slot, plan.Count);
        else if (plan.Kind == InventoryUiLaw.MoveKind.SwapInventory)
            sent = _net.SwapInventoryItems(plan.Source.Slot, plan.Destination.Slot);
        else
            sent = _net.SwapItems(plan.Destination.Bag, plan.Destination.Slot,
                plan.Source.Bag, plan.Source.Slot);
        if (!sent) return;
        long operation = ++_pendingBagOperation;
        AddPendingBagLock(_carriedContainer, _carriedSlot, operation);
        AddPendingBagLock(container, slot, operation);
        ClearCarriedItem();
    }

    private void ClearCarriedItem()
    {
        _carriedContainer = InventoryUiLaw.EmptyContainer;
        _carriedSlot = -1;
        _carriedCount = null;
    }

    private void ClearCarriedItemOnEscape()
    {
        if (HasCarriedItem) ClearCarriedItem();
        ClearActionBarCursorOnEscape();
    }

    private WorldEntity? ResolveCarriedItem()
    {
        return HasCarriedItem ? ResolveInventoryItem(_carriedContainer, _carriedSlot) : null;
    }

    private bool PlaceCarriedItemOnAction(int actionSlot)
    {
        if (!HasCarriedItem) return false;
        // A held cursor payload owns the click even when the destination refuses it. Falling
        // through to UseAction would cast/use the action underneath a silently refused item.
        if (_net is null || ResolveCarriedItem() is not { } item) return true;
        _items?.Require(item.Entry, item.Guid, _net);
        if (_items?.TryGet(item.Entry, out ItemTemplate? template) != true || template is null)
            return true;
        if (!MultiActionBarUiLaw.ItemMayBePlaced(template.InventoryType, template.UseSpellId))
            return true;

        var action = new ActionSlot(ActionSlot.Item, item.Entry);
        PlaceActionPayload(actionSlot, action);
        ClearCarriedItem();
        return true;
    }

    private void UseItemAction(uint entry)
    {
        // CMSG_USE_ITEM is threaded through GetSuiActor server-side (HEAD "useable 6 slots"), so a
        // possessed bot's own items are usable and the required-level gate is the BOT's — same
        // relaxation as the drag path (line ~822). CanAuthorControlledGameplay allows possession
        // and still blocks a detached Free View cursor. Delete/mail/bank stay on the strict gate.
        if (!CanAuthorControlledOrSelf || _net is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        List<(byte Bag, byte Slot, WorldEntity Item, bool Worn)> all =
            EnumerateActionItemCopies(player, entry).ToList();
        if (all.Count == 0) return;
        WorldEntity exemplar = all[0].Item;
        _items.Require(entry, exemplar.Guid, _net);
        if (!_items.TryGet(entry, out ItemTemplate? template) || template is null) return;

        bool chargeFilter = template.InventoryType == 0 &&
            MultiActionBarUiLaw.RequiresLiveCharges(template.SpellCharges0);
        (byte Bag, byte Slot, WorldEntity Item, bool Worn)? any = null;
        foreach (var candidate in all)
        {
            if (chargeFilter && !MultiActionBarUiLaw.LiveChargeCandidate(
                    candidate.Item.Fields.ContainerNumSlots > 0,
                    candidate.Item.Fields.ItemSpellCharges(0))) continue;
            any = candidate;
            break;
        }
        (byte Bag, byte Slot, WorldEntity Item, bool Worn)? equipped =
            all.FirstOrDefault(candidate => candidate.Worn) is { Worn: true } worn
                ? worn : null;
        MultiActionItemRoute route = MultiActionBarUiLaw.ItemUseRoute(template.InventoryType,
            equipped is not null, any is not null);
        if (route == MultiActionItemRoute.Use)
        {
            (byte Bag, byte Slot, WorldEntity Item, bool Worn) at = template.InventoryType != 0
                ? equipped!.Value : any!.Value;
            uint activeIconId = _spellCatalog?.TryGet(template.UseSpellId, out SpellInfo spell) == true
                ? spell.ActiveIconId : 0;
            bool matchingCancelableAura = player.Fields.Auras().Any(aura =>
                aura.SpellId == template.UseSpellId && (aura.Flags & 0x1) != 0);
            switch (MultiActionBarUiLaw.ItemUseDisposition(template.StartQuest,
                        template.UseSpellId, activeIconId, matchingCancelableAura))
            {
                case MultiActionItemUseDisposition.QuestOffer:
                    _net.QuestgiverQuery(at.Item.Guid, template.StartQuest);
                    break;
                case MultiActionItemUseDisposition.ToggleCancel:
                    _net.CancelAura(template.UseSpellId);
                    break;
                case MultiActionItemUseDisposition.Use:
                    SendItemUse(at.Bag, at.Slot, at.Item, template);
                    break;
            }
        }
        else if (route == MultiActionItemRoute.Equip && any is { } at)
            _net.AutoEquipItem(at.Bag, at.Slot);
    }

    /// <summary>
    /// The shared CMSG_USE_ITEM commit tail: query the item/spell pair before sending, then arm
    /// the item-authored recovery and the selected spell's GCD as separate history nodes.
    /// </summary>
    private bool SendItemUse(byte bag, byte slot, WorldEntity instance, ItemTemplate template)
    {
        // Possession-aware (server routes CMSG_USE_ITEM via GetSuiActor): a possessed bot uses its
        // own item, gated by ITS level. Strict session gate only guarded the pre-threading server.
        if (!CanAuthorControlledOrSelf || _net is null) return false;
        ItemSpellTemplate useSpell = template.Spells[template.UseSpellIndex];
        SpellInfo? spell = _spellCatalog?.TryGet(useSpell.SpellId, out SpellInfo resolved) == true
            ? resolved : null;
        double now = NowSeconds();
        bool blocked = spell is { } info
            ? _actions.IsOnCooldown(useSpell.SpellId, template.Entry, info, now)
            : _actions.IsOnCooldown(useSpell.SpellId, template.Entry, useSpell.Category, now);
        // Which side authored the recovery this gate is about to enforce. item* are the SERVER's
        // item_template columns (spellcooldown / spellcategorycooldown, -1 meaning "use the
        // spell's own"); dbc* are the Spell.dbc fallbacks the client substitutes for a -1. The
        // gate below never reaches the wire, so without this line a local block is
        // indistinguishable from the server refusing the use.
        // path=useitem, NOT "bag": SendItemUse is the shared CMSG_USE_ITEM tail and serves a bag
        // click AND an action-bar press or hotkey (UseItemAction routes here). Only the command
        // shelf has a gate of its own, and it reports path=shelf.
        Console.WriteLine($"[verdict:item-cooldown] time={NowSeconds():F3} path=useitem entry={template.Entry} " +
            $"spell={useSpell.SpellId} name={spell?.Name ?? "?"} category={useSpell.Category} " +
            $"itemCooldownMs={useSpell.CooldownMs} itemCategoryCooldownMs={useSpell.CategoryCooldownMs} " +
            $"dbcRecoveryMs={spell?.RecoveryMs ?? 0} dbcCategoryRecoveryMs={spell?.CategoryRecoveryMs ?? 0} " +
            $"blocked={blocked}");
        if (useSpell.SpellId != 0 && blocked)
        {
            ShowSpellError(useSpell.SpellId, "LOCAL_ITEM_COOLDOWN", "Item is not ready yet.",
                "LOCAL_GATE");
            return false;
        }
        if (!_net.UseItem(bag, slot, template.UseSpellIndex)) return false;
        ScheduleControlledInventoryRefresh(ControlledGuid);   // re-sync a possessed bot's consumed item
        if (useSpell.SpellId == 0) return true;
        _actions.StartItemUseCooldown(instance.Entry, useSpell, spell, now);
        if (spell is { } committed) _actions.StartGlobalCooldown(useSpell.SpellId, committed, now);
        return true;
    }

    /// <summary>
    /// The reference's mode-0x47 inventory walk. Order is observable when duplicate copies have
    /// different remaining charges, and its wire bag bytes are not UI container ids.
    /// </summary>
    private IEnumerable<(byte Bag, byte Slot, WorldEntity Item, bool Worn)>
        EnumerateActionItemCopies(WorldEntity player, uint entry)
    {
        (WorldEntity Item, bool Hit) Resolve(ulong guid) =>
            guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry
                ? (item, true) : (null!, false);

        for (int slot = 0; slot < 19; slot++)
            if (Resolve(player.Fields.PlayerInventorySlot(slot)) is { Hit: true } worn)
                yield return (255, (byte)slot, worn.Item, true);
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            byte bagSlot = (byte)(19 + bagIndex);
            ulong bagGuid = player.Fields.PlayerInventorySlot(bagSlot);
            if (Resolve(bagGuid) is { Hit: true } bagObject)
                yield return (255, bagSlot, bagObject.Item, false);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(bag.Fields.ContainerNumSlots, 36);
            for (int slot = 0; slot < slots; slot++)
                if (Resolve(bag.Fields.ContainerSlot(slot)) is { Hit: true } content)
                    yield return (bagSlot, (byte)slot, content.Item, false);
        }
        for (int i = 0; i < 16; i++)
            if (Resolve(player.Fields.PlayerBackpackSlot(i)) is { Hit: true } backpack)
                yield return (255, (byte)(23 + i), backpack.Item, false);
        for (int i = 0; i < 16; i++)
            if (Resolve(player.Fields.PlayerKeyringSlot(i)) is { Hit: true } key)
                yield return (255, (byte)(81 + i), key.Item, false);
    }

    private void UseBackpackItem(int slot, ItemTemplate item)
    {
        // Possession-aware use/equip (both opcodes threaded via GetSuiActor server-side).
        if (!CanAuthorControlledOrSelf || _net is null) return;
        if (item.InventoryType != 0) _net.AutoEquipItem(255, (byte)(23 + slot));
        else if (ResolveInventoryItem(0, slot) is { } instance)
            SendItemUse(255, (byte)(23 + slot), instance, item);
    }

    private void DrawCarriedItem(WorldEntity player, float scale)
    {
        if (!HasCarriedItem || _items is null || _gameplayArt is null) return;
        if (ResolveCarriedItem() is not { } item ||
            !_items.TryGet(item.Entry, out ItemTemplate? template) || template is null) return;
        uint icon = _gameplayArt.Handle(template.IconPath);
        if (icon == 0) return;
        Vector2 min = ImGui.GetIO().MousePos + new Vector2(12f) * scale;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32f) * scale,
            Vector2.Zero, Vector2.One, 0xccffffff);
        if (_carriedCount is int count)
            GameText.DrawRightAligned(ImGui.GetForegroundDrawList(), "NumberFontNormal",
                count.ToString(), min + new Vector2(32f, 18f) * scale, scale);
    }

    private static Vector4 ItemTooltipQualityColor(uint quality) => quality switch
    {
        0 => new Vector4(0.62f, 0.62f, 0.62f, 1), 2 => new Vector4(0.12f, 1f, 0, 1),
        3 => new Vector4(0, 0.44f, 0.87f, 1), 4 => new Vector4(0.64f, 0.21f, 0.93f, 1),
        5 => new Vector4(1f, 0.50f, 0, 1), 6 => new Vector4(0.90f, 0.80f, 0.50f, 1),
        _ => Vector4.One,
    };

    private enum PreparedItemTooltipPaintKind
    {
        Plain,
        Disabled,
        Colored,
        Paired,
        Separator,

        /// <summary>The vanilla money row: coins, no text, no label. See
        /// <see cref="GameTooltipUiLaw.MoneyRowLabelNote"/>.</summary>
        Money,
    }

    private readonly record struct PreparedItemTooltipPaintOp(
        PreparedItemTooltipPaintKind Kind,
        string Text,
        Vector4 Color,
        string? RightText = null,
        Vector4 RightColor = default,
        bool Wrap = false,
        uint Copper = 0);

    private readonly record struct ItemTooltipBodySnapshot(
        ImmutableArray<PreparedItemTooltipPaintOp> Operations);

    private readonly record struct PreparedInventoryTooltipLine(
        string FontObject,
        string Text,
        Vector2 Position,
        uint Color,
        string? RightText = null,
        Vector2 RightPosition = default,
        uint RightColor = 0);

    private sealed record PreparedInventoryTooltipRenderer(
        WowSkin Skin,
        Vector2 Position,
        Vector2 Size,
        float Scale,
        ImmutableArray<PreparedInventoryTooltipLine> Lines,
        Vector4 BackdropFillTint,
        Vector4 BackdropEdgeTint,
        Vector2 ThickenMinimum,
        Vector2 ThickenMaximum,
        Vector4 ThickenTint,
        ImmutableArray<PreparedSharedGameTooltipMoneyCoin> MoneyCoins,
        uint MoneyTexture);

    private readonly record struct PreparedPaperDollComparisonTooltip(
        int TooltipNumber,
        int EquipmentSlot,
        ItemTooltipBodySnapshot Body,
        Vector2 WindowPosition,
        Vector2 WindowPivot,
        float Scale,
        string AnchorPoint,
        string RelativePoint,
        string ParentElement,
        bool CaptureParity);

    private readonly record struct ShoppingTooltipParityExpectation(
        int TooltipNumber,
        string ParentElement);

    private static GameTooltipOwnerKey InventoryItemGameTooltipOwner(
        int container,
        int physicalButton)
    {
        if (physicalButton <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalButton));
        return new($"item:inventory-container:{container}", (ulong)physicalButton);
    }

    private static int HighestLiveComparisonOrdinal(IEnumerable<int> tooltipNumbers)
        => tooltipNumbers.DefaultIfEmpty(0).Max();

    private static PreparedItemTooltipPaintOp PreparedItemTooltipPlain(string text)
        => new(PreparedItemTooltipPaintKind.Plain, text, default);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipDisabled(string text)
        => new(PreparedItemTooltipPaintKind.Disabled, text, default);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipColored(
        string text,
        Vector4 color,
        bool wrap = false)
        => new(PreparedItemTooltipPaintKind.Colored, text, color, Wrap: wrap);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipPair(
        string left,
        Vector4 leftColor,
        string right,
        Vector4 rightColor)
        => new(PreparedItemTooltipPaintKind.Paired, left, leftColor, right, rightColor);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipSeparator()
        => new(PreparedItemTooltipPaintKind.Separator, "", default);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipMoney(uint copper)
        => new(PreparedItemTooltipPaintKind.Money, "", Vector4.One, Copper: copper);

    private static ItemTooltipBodySnapshot AppendPreparedItemTooltipBody(
        in ItemTooltipBodySnapshot body,
        params PreparedItemTooltipPaintOp[] tail)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        ArgumentNullException.ThrowIfNull(tail);
        return new(body.Operations.AddRange(tail));
    }

    /// <summary>
    /// The value the open merchant will pay for this complete stack, as 1.12 renders it:
    /// a bare money row of coins with NO label (there is no such string in 1.12 —
    /// <see cref="GameTooltipUiLaw.MoneyRowLabelNote"/>), or the ITEM_UNSELLABLE line
    /// when the price is zero. This used to print an invented "Sell Price" label with the
    /// amount spelled out in words and right-aligned to the plate edge, which is three
    /// separate departures from the game.
    /// </summary>
    private static ItemTooltipBodySnapshot AppendVendorSellPrice(
        in ItemTooltipBodySnapshot body, ItemTemplate item, uint count)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Zero is not "say nothing": at an open merchant the engine prints the
        // unsellable line, which is how the player learns the vendor will not take it.
        if (item.SellPrice == 0)
            return AppendPreparedItemTooltipBody(body,
                PreparedItemTooltipColored(GameTooltipUiLaw.UnsellableText, Vector4.One));
        uint stackCount = Math.Max(1u, count);
        uint value = (uint)Math.Min(uint.MaxValue, (ulong)item.SellPrice * stackCount);
        return AppendPreparedItemTooltipBody(body, PreparedItemTooltipMoney(value));
    }

    /// <summary>
    /// The item tooltip's money, both branches of ContainerFrameItemButton_OnEnter in one
    /// place: in repair mode a DAMAGED item gets the REPAIR_COST label and its own repair
    /// money; otherwise an open merchant gets the engine's sell price. With no merchant
    /// open there is no money row at all - vanilla never prices an item in your bag until
    /// you are standing at someone who would pay for it.
    /// </summary>
    private ItemTooltipBodySnapshot AppendVendorMoneyRow(
        in ItemTooltipBodySnapshot body, ItemTemplate item, uint count, WorldEntity? instance)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_vendor is null) return body;
        if (_vendorRepairMode)
        {
            // Same cost the repair-all tooltip totals, per item; zero means undamaged or
            // unrepairable, which is vanilla's silence rather than a "0" row.
            uint repair = instance is { } damaged ? MerchantRepairItemCost(damaged) : 0;
            return repair == 0
                ? body
                : AppendPreparedItemTooltipBody(body,
                    PreparedItemTooltipColored(GameTooltipUiLaw.RepairCostText, Vector4.One),
                    PreparedItemTooltipMoney(repair));
        }
        return AppendVendorSellPrice(body, item, count);
    }

    private ItemTooltipBodySnapshot PrepareItemTooltipBodySnapshot(
        ItemTemplate item,
        uint count,
        uint durability = 0,
        uint maxDurability = 0,
        bool compact = false,
        uint? instanceFlags = null,
        WorldEntity? liveInstance = null,
        ulong ownerGuid = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        var operations = ImmutableArray.CreateBuilder<PreparedItemTooltipPaintOp>();

        // Resolve every mutable ItemTemplate field, Stats entry, and Damages entry into an
        // immutable paint operation before the terminal tooltip stratum can invoke a renderer.
        operations.Add(PreparedItemTooltipColored(item.Name,
            compact ? Vector4.One : ItemTooltipQualityColor(item.Quality)));
        Vector4 white = Vector4.One;
        Vector4 red = new(1f, 32f / 255f, 32f / 255f, 1f);
        Vector4 green = new(0f, 1f, 0f, 1f);
        Vector4 gold = new(1f, 210f / 255f, 0f, 1f);
        uint actualInstanceFlags = instanceFlags ?? liveInstance?.Fields.ItemFlags ?? 0;
        if ((item.Flags & 0x0000_2000) != 0)
            operations.Add(PreparedItemTooltipColored("<Right Click for Details>", green));
        if ((item.Flags & 0x0000_0002) != 0)
            operations.Add(PreparedItemTooltipPlain("Conjured Item"));
        switch (item.Bonding)
        {
            case 1: operations.Add(PreparedItemTooltipPlain("Binds when picked up")); break;
            case 2: operations.Add(PreparedItemTooltipPlain("Binds when equipped")); break;
            case 3: operations.Add(PreparedItemTooltipPlain("Binds when used")); break;
            case 4:
            case 5: operations.Add(PreparedItemTooltipPlain("Quest Item")); break;
        }
        if (item.MaxCount == 1)
            operations.Add(PreparedItemTooltipPlain("Unique"));
        else if (item.MaxCount > 1)
            operations.Add(PreparedItemTooltipPlain($"Unique ({item.MaxCount})"));
        if (item.StartQuest != 0)
            operations.Add(PreparedItemTooltipPlain("This Item Begins a Quest"));
        if (item.LockId != 0 && (actualInstanceFlags &
                InventoryUiLaw.ItemDynamicUnlocked) == 0)
            operations.Add(PreparedItemTooltipColored("Locked", red));
        if (item.ContainerSlots > 0)
        {
            operations.Add(PreparedItemTooltipPlain($"{item.ContainerSlots} Slot Bag"));
        }
        else
        {
            string? slot = InventoryUiLaw.InventoryTypeName(item.InventoryType);
            ItemSubClassTooltipInfo subclassInfo =
                _itemSubClasses?.TooltipInfo(item.Class, item.Subclass) ?? default;
            string? type = item.InventoryType == 16 || subclassInfo.HidesDisplayName
                ? null
                : _itemSubClasses?.DisplayName(item.Class, item.Subclass);
            if (string.IsNullOrWhiteSpace(type)) type = null;
            if (slot is not null || type is not null)
            {
                // Weapon/armor proficiency red is judged against WHOSE bags are shown, but we only
                // have the LOGIN character's proficiencies client-side (streamed on the session
                // wire; a possessed bot's are not). So for anyone else we do NOT paint the type as
                // unusable — the server still enforces equip, and level/class/race above are already
                // owner-correct. Fixes gear reading red on a bot you can actually equip it to.
                bool ownerIsLocal = ownerGuid == 0 || ownerGuid == LocalPlayerGuid;
                InventoryUiLaw.ProficiencyColors proficiency = default;
                if (ownerIsLocal)
                {
                    bool canDualWield = _spellCatalog is not null && OwnActions.KnownSpells.Any(id =>
                        _spellCatalog.TryGet(id, out SpellInfo spell) &&
                        spell.EffectIds is { Length: > 0 } && spell.EffectIds[0] == 40);
                    proficiency = InventoryUiLaw.ItemProficiencyColors(item.Class, item.Subclass,
                        item.InventoryType, _itemProficiencies,
                        subclassInfo.ProficiencyAlternative, canDualWield);
                }
                if (slot is not null && type is not null)
                    operations.Add(PreparedItemTooltipPair(slot,
                        proficiency.SlotRed ? red : Vector4.One, type,
                        proficiency.TypeRed ? red : Vector4.One));
                else if (slot is not null)
                    operations.Add(PreparedItemTooltipColored(slot,
                        proficiency.SlotRed ? red : Vector4.One));
                else if (type is not null)
                    operations.Add(PreparedItemTooltipColored(type,
                        proficiency.TypeRed ? red : Vector4.One));
            }
        }
        ItemDamage[] damages = item.Damages.Where(static damage => damage.Max > 0f).ToArray();
        if (damages.Length > 0)
        {
            static string DamageText(in ItemDamage damage, bool extra)
            {
                long minimum = (long)Math.Floor(damage.Min + .5f);
                long maximum = (long)Math.Floor(damage.Max + .5f);
                string school = damage.School switch
                {
                    1 => " Holy", 2 => " Fire", 3 => " Nature", 4 => " Frost",
                    5 => " Shadow", 6 => " Arcane", _ => "",
                };
                return $"{(extra ? "+ " : "")}{minimum} - {maximum}{school} Damage";
            }
            double speed = item.DelayMs / 1000d;
            string firstDamage = DamageText(damages[0], extra: false);
            if (speed > 0d)
                operations.Add(PreparedItemTooltipPair(firstDamage, white,
                    $"Speed {speed.ToString("0.00", CultureInfo.InvariantCulture)}", white));
            else
                operations.Add(PreparedItemTooltipPlain(firstDamage));
            foreach (ItemDamage extraDamage in damages.Skip(1))
                operations.Add(PreparedItemTooltipPlain(DamageText(extraDamage, extra: true)));
            if (speed > 0d && item.Class == 2)
            {
                double average = damages.Sum(static damage =>
                    ((double)damage.Min + damage.Max) * .5d);
                operations.Add(PreparedItemTooltipPlain(
                    $"({(average / speed).ToString("0.0", CultureInfo.InvariantCulture)} damage per second)"));
            }
        }
        if (item.Armor > 0)
            operations.Add(PreparedItemTooltipPlain($"{item.Armor} Armor"));
        if (item.Block > 0)
            operations.Add(PreparedItemTooltipPlain($"{item.Block} Block"));
        uint[] statOrder = [4, 3, 7, 5, 6, 1, 0];
        foreach (uint wanted in statOrder)
            foreach (ItemStat stat in item.Stats)
            {
                string? name = stat.Type switch
                {
                    0 => "Mana", 1 => "Health", 3 => "Agility", 4 => "Strength",
                    5 => "Intellect", 6 => "Spirit", 7 => "Stamina", _ => null,
                };
                if (stat.Type != wanted || stat.Value == 0 || name is null) continue;
                operations.Add(PreparedItemTooltipPlain(
                    $"{(stat.Value > 0 ? "+" : "-")}{Math.Abs((long)stat.Value)} {name}"));
            }
        uint firstResistance = item.Resistances.Length == 0 ? 0 : item.Resistances[0];
        if (firstResistance != 0 && item.Resistances.All(value => value == firstResistance))
        {
            operations.Add(PreparedItemTooltipPlain(
                $"+{firstResistance} to All Resistances"));
        }
        else
        {
            string[] resistanceNames = ["Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane"];
            for (int i = 1; i < Math.Min(item.Resistances.Length, resistanceNames.Length); i++)
                if (item.Resistances[i] != 0)
                    operations.Add(PreparedItemTooltipPlain(
                        $"+{item.Resistances[i]} {resistanceNames[i]} Resistance"));
        }
        if ((item.Flags & 0x0000_2000) == 0 &&
            liveInstance is not null && _enchantCatalog is not null)
        {
            for (int slot = 0; slot < 7; slot++)
            {
                int signedId = unchecked((int)liveInstance.Fields.ItemEnchantmentId(slot));
                if (signedId == 0) continue;
                uint id = (uint)Math.Abs((long)signedId);
                if (!_enchantCatalog.TryGet(id, out EnchantInfo enchant) ||
                    enchant.HidesTooltipName || enchant.Name.Length == 0) continue;
                ulong? remaining = _itemEnchantTimers.RemainingMilliseconds(
                    liveInstance.Guid, (uint)slot,
                    liveInstance.Fields.ItemEnchantmentDuration(slot), NowSeconds());
                string text = ItemEnchantUiLaw.Text(enchant.Name, remaining,
                    liveInstance.Fields.ItemEnchantmentCharges(slot));
                operations.Add(PreparedItemTooltipColored(text,
                    ItemEnchantUiLaw.Color(slot, signedId)));
            }
        }
        bool noEnchantIdSource = liveInstance is null ||
            (actualInstanceFlags & InventoryUiLaw.ItemDynamicWrapped) != 0;
        if ((item.Flags & 0x0000_2000) == 0 && noEnchantIdSource &&
            item.RandomProperty != 0)
            operations.Add(PreparedItemTooltipColored("<Random enchantment>", green));
        uint authoredMaximum = maxDurability > 0 ? maxDurability : item.MaxDurability;
        if (authoredMaximum > 0)
        {
            uint current = maxDurability > 0 ? durability : authoredMaximum;
            operations.Add(PreparedItemTooltipColored(
                $"Durability {current} / {authoredMaximum}", current == 0 ? red : white));
        }

        // Level / class / race requirements colour against WHOSE bags are shown — the possessed bot
        // when you're driving one — not always the commander. ownerGuid 0 keeps the vanilla self
        // perspective for every other surface (vendor, inspect, paperdoll comparisons). (Required
        // SKILL still reads the session player's own skill lines — a bot's aren't streamed.)
        ulong requirementOwner = ownerGuid != 0 ? ownerGuid : LocalPlayerGuid;
        WorldEntity? player = null;
        if (_entities is not null && requirementOwner != 0 &&
            _entities.TryGet(requirementOwner, out WorldEntity foundPlayer))
            player = foundPlayer;
        byte playerRace = 0, playerClass = 0;
        uint playerLevel = 0;
        if (player is not null)
        {
            (playerRace, playerClass, _, _) = player.Fields.Bytes0;
            playerLevel = player.Level;
        }
        static int FullMask((int Id, string Name)[] values) =>
            values.Aggregate(0, static (mask, value) => mask | 1 << (value.Id - 1));
        void AddMaskLine(string label, int mask, (int Id, string Name)[] values, byte ownId)
        {
            if (mask <= 0 || mask == FullMask(values)) return;
            string[] names = values
                .Where(value => (mask & 1 << (value.Id - 1)) != 0)
                .Select(static value => value.Name)
                .ToArray();
            if (names.Length == 0) return;
            bool allowed = ownId > 0 && (mask & 1 << (ownId - 1)) != 0;
            operations.Add(PreparedItemTooltipColored(
                $"{label}: {string.Join(", ", names)}", allowed ? white : red));
        }
        AddMaskLine("Classes", item.AllowableClass,
            [(1, "Warrior"), (2, "Paladin"), (3, "Hunter"), (4, "Rogue"),
             (5, "Priest"), (7, "Shaman"), (8, "Mage"), (9, "Warlock"),
             (11, "Druid")], playerClass);
        AddMaskLine("Races", item.AllowableRace,
            [(1, "Human"), (2, "Orc"), (3, "Dwarf"), (4, "Night Elf"),
             (5, "Undead"), (6, "Tauren"), (7, "Gnome"), (8, "Troll")], playerRace);
        if (item.RequiredLevel > 1)
            operations.Add(PreparedItemTooltipColored($"Requires Level {item.RequiredLevel}",
                playerLevel >= item.RequiredLevel ? white : red));
        if (item.RequiredSkill != 0 &&
            _skillLines?.TryGet(item.RequiredSkill, out SkillLineInfo skill) == true)
        {
            bool hasSkill = GetSkillValue(item.RequiredSkill, out ushort value, out _) &&
                value >= Math.Max(1u, item.RequiredSkillRank);
            string line = item.RequiredSkillRank > 0
                ? $"Requires {skill.Name} ({item.RequiredSkillRank})"
                : $"Requires {skill.Name}";
            operations.Add(PreparedItemTooltipColored(line, hasSkill ? white : red));
        }
        if (item.RequiredSpell != 0 &&
            _spellCatalog?.TryGet(item.RequiredSpell, out SpellInfo requiredSpell) == true)
        {
            bool known = _actionsByGuid is not null &&
                _actionsByGuid.TryGetValue(LocalPlayerGuid, out PlayerActions? ownActions) &&
                ownActions.KnownSpells.Contains(item.RequiredSpell);
            operations.Add(PreparedItemTooltipColored($"Requires {requiredSpell.Name}",
                known ? white : red));
        }

        IReadOnlySet<uint> knownSpells = _actionsByGuid is not null &&
            _actionsByGuid.TryGetValue(LocalPlayerGuid, out PlayerActions? actions)
                ? actions.KnownSpells
                : new HashSet<uint>();
        if (item.RequiredReputationFaction != 0 && _factionCatalog is not null &&
            _factionCatalog.TryGetName(item.RequiredReputationFaction,
                out string? factionName) && factionName.Length > 0)
        {
            string[] standings =
                ["Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored",
                 "Revered", "Exalted"];
            uint requiredRank = Math.Min(item.RequiredReputationRank, 7u);
            byte currentRank = CurrentReputationRank(item.RequiredReputationFaction,
                playerRace, playerClass);
            operations.Add(PreparedItemTooltipColored(
                $"Requires {factionName} - {standings[(int)requiredRank]}",
                currentRank >= requiredRank ? white : red));
        }

        if (item.Spells.Any(spell => spell.Trigger == 6 && spell.SpellId != 0 &&
                knownSpells.Contains(spell.SpellId)))
            operations.Add(PreparedItemTooltipColored("Already known", red));

        if (_spellCatalog is not null)
            foreach (ItemSpellTemplate itemSpell in item.Spells)
            {
                string? prefix = itemSpell.Trigger switch
                {
                    0 or 5 => "Use: ",
                    1 => "Equip: ",
                    2 => "Chance on hit: ",
                    _ => null,
                };
                if (prefix is null || itemSpell.SpellId == 0 ||
                    !_spellCatalog.TryGet(itemSpell.SpellId, out SpellInfo spell) ||
                    string.IsNullOrEmpty(spell.Description))
                    continue;
                string description = SpellTooltipLaw.Substitute(spell.Description, spell,
                    _spellCatalog, playerLevel);
                if (description.Length != 0)
                    operations.Add(PreparedItemTooltipColored(prefix + description, green,
                        wrap: true));
            }

        int charges = item.Spells
            .Where(static spell => spell.SpellId != 0 && spell.Charges is not 0 and not -1)
            .Select(static spell => (int)Math.Min(int.MaxValue, Math.Abs((long)spell.Charges)))
            .FirstOrDefault();
        if (charges > 0)
            operations.Add(PreparedItemTooltipPlain(
                charges == 1 ? "1 Charge" : $"{charges} Charges"));

        if (item.ItemSet != 0 && _itemSets?.TryGet(item.ItemSet, out ItemSetInfo set) == true)
        {
            Vector4 gray = new(128f / 255f, 128f / 255f, 128f / 255f, 1f);
            Vector4 cream = new(1f, 1f, 151f / 255f, 1f);
            var equippedEntries = new HashSet<uint>();
            if (player is not null && _entities is not null)
                for (int slot = 0; slot < 19; slot++)
                {
                    ulong guid = player.Fields.PlayerInventorySlot(slot);
                    if (guid != 0 && _entities.TryGet(guid, out WorldEntity equippedItem))
                        equippedEntries.Add(equippedItem.Entry);
                }
            int owned = set.Members.Count(equippedEntries.Contains);
            operations.Add(PreparedItemTooltipColored("", gold, wrap: true));
            operations.Add(PreparedItemTooltipColored(
                $"{set.Name} ({owned}/{set.Members.Length})", gold));
            bool setSkillMet = set.RequiredSkill == 0;
            if (set.RequiredSkill != 0 &&
                _skillLines?.TryGet(set.RequiredSkill, out SkillLineInfo setSkill) == true)
            {
                setSkillMet = GetSkillValue(set.RequiredSkill, out ushort setValue, out _) &&
                    setValue >= set.RequiredSkillRank;
                string setRequirement = set.RequiredSkillRank > 0
                    ? $"Requires {setSkill.Name} ({set.RequiredSkillRank})"
                    : $"Requires {setSkill.Name}";
                operations.Add(PreparedItemTooltipColored(setRequirement,
                    setSkillMet ? white : red));
            }
            foreach (uint member in set.Members)
            {
                if (_items is not null && _net is not null) _items.Require(member, 0, _net);
                if (_items?.TryGet(member, out ItemTemplate? memberItem) != true ||
                    memberItem is null || memberItem.Name.Length == 0)
                    continue;
                operations.Add(PreparedItemTooltipColored($"  {memberItem.Name}",
                    equippedEntries.Contains(member) ? cream : gray));
            }
            operations.Add(PreparedItemTooltipColored("", gold, wrap: true));
            if (_spellCatalog is not null)
                foreach ((uint threshold, uint spellId) in set.Bonuses
                             .OrderBy(static bonus => bonus.Threshold))
                {
                    if (!_spellCatalog.TryGet(spellId, out SpellInfo bonusSpell) ||
                        string.IsNullOrEmpty(bonusSpell.Description))
                        continue;
                    string bonus = SpellTooltipLaw.Substitute(bonusSpell.Description,
                        bonusSpell, _spellCatalog, playerLevel);
                    if (bonus.Length == 0) continue;
                    operations.Add(PreparedItemTooltipColored(
                        $"({threshold}) Set: {bonus}",
                        setSkillMet && (uint)owned >= threshold ? green : gray, wrap: true));
                }
        }

        if (!compact && !string.IsNullOrWhiteSpace(item.Description))
            operations.Add(PreparedItemTooltipColored($"\"{item.Description}\"", gold,
                wrap: true));
        if (!compact && liveInstance is not null)
        {
            ulong creator = liveInstance.Fields.ItemCreator;
            if ((liveInstance.Fields.ItemFlags & InventoryUiLaw.ItemDynamicWrapped) == 0 &&
                creator != 0 && _playerNames.TryGetValue(creator, out string? creatorName) &&
                creatorName.Length > 0)
            {
                string creatorLine = liveInstance.Fields.ItemTextId != 0
                    ? $"Written by {creatorName}"
                    : $"<Made by {creatorName}>";
                operations.Add(PreparedItemTooltipColored(creatorLine,
                    liveInstance.Fields.ItemTextId != 0 ? white : green));
            }
            if (InventoryUiLaw.ShowsOpenLine(item.Flags, item.LockId, actualInstanceFlags))
                operations.Add(PreparedItemTooltipColored("<Right Click to Open>", green));
            else if (item.PageText != 0 || liveInstance.Fields.ItemTextId != 0)
                operations.Add(PreparedItemTooltipColored("<Right Click to Read>", green));
        }

        return new(operations.ToImmutable());
    }

    private static void DrawPreparedItemTooltipBody(in ItemTooltipBodySnapshot body)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        int pairIndex = 0;
        foreach (PreparedItemTooltipPaintOp operation in body.Operations)
        {
            switch (operation.Kind)
            {
                case PreparedItemTooltipPaintKind.Plain:
                    if (operation.Wrap) ImGui.TextWrapped(operation.Text);
                    else ImGui.TextUnformatted(operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Disabled:
                    ImGui.TextDisabled(operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Colored:
                    if (operation.Wrap)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, operation.Color);
                        ImGui.TextWrapped(operation.Text);
                        ImGui.PopStyleColor();
                    }
                    else ImGui.TextColored(operation.Color, operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Paired:
                    if (ImGui.BeginTable($"##prepared-item-tooltip-pair-{pairIndex++}", 2,
                            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(operation.Color, operation.Text);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(operation.RightColor, operation.RightText ?? "");
                        ImGui.EndTable();
                    }
                    break;
                case PreparedItemTooltipPaintKind.Money:
                    // The debug fallback plate has no draw list to hang coins off; the
                    // positioned vanilla renderer below is the one the player sees.
                    ImGui.TextUnformatted(GameTooltipUiLaw.MoneyString(operation.Copper));
                    break;
                case PreparedItemTooltipPaintKind.Separator:
                    ImGui.Separator();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown prepared item tooltip paint kind {operation.Kind}.");
            }
        }
    }

    private PreparedInventoryTooltipRenderer? PrepareInventoryItemTooltipRenderer(
        in ItemTooltipBodySnapshot body,
        Vector2 anchor,
        Vector2 pivot)
    {
        if (_skin is null || body.Operations.IsDefault || body.Operations.Length == 0)
            return null;

        float scale = GameplayUiScale();
        float padding = GameTooltipUiLaw.Padding * scale;
        float gap = GameTooltipUiLaw.LogicalRowGap * scale;
        float wrapWidth = SpellTooltipLaw.WrapWidth * scale;
        var widths = new float[body.Operations.Length];
        var heights = new float[body.Operations.Length];
        var fonts = new string[body.Operations.Length];
        var physicalLines = new string[body.Operations.Length][];
        var moneyGeometries = new GameTooltipMoneyRowGeometry?[body.Operations.Length];
        var moneyTexts = new string[body.Operations.Length][];
        float contentWidth = 0f;
        float contentHeight = 0f;
        for (int i = 0; i < body.Operations.Length; i++)
        {
            PreparedItemTooltipPaintOp operation = body.Operations[i];
            string font = i == 0 ? "GameTooltipHeaderText" : "GameTooltipText";
            fonts[i] = font;
            if (operation.Kind == PreparedItemTooltipPaintKind.Separator)
            {
                heights[i] = gap;
                physicalLines[i] = [];
            }
            else if (operation.Kind == PreparedItemTooltipPaintKind.Money)
            {
                // SetTooltipMoney's blank line: the row carries no text of its own, and
                // the STATIC small money frame supplies both its width and its content.
                GameTooltipMoneyParts parts = GameTooltipUiLaw.Money(operation.Copper)!.Value;
                moneyTexts[i] = [.. parts.VisibleCoins().Select(coin => coin.Amount.ToString())];
                float[] numberWidths = [.. moneyTexts[i].Select(text =>
                    GameText.MeasureWidth(GameTooltipUiLaw.MoneyFontObject, text, scale))];
                moneyGeometries[i] = GameTooltipUiLaw.MoneyRowGeometry(parts, numberWidths, scale);
                physicalLines[i] = [];
                widths[i] = moneyGeometries[i]!.ContentWidth;
                heights[i] = GameText.EmPixels(font, scale);
            }
            else
            {
                string[] wrapped = operation.Wrap
                    ? GameTooltipUiLaw.WrapText(operation.Text, wrapWidth,
                        text => GameText.MeasureWidth(font, text, scale))
                    : [operation.Text];
                physicalLines[i] = wrapped;
                float left = wrapped.Length == 0 ? 0f : wrapped.Max(text =>
                    GameText.MeasureWidth(font, text, scale));
                float right = operation.RightText is null
                    ? 0f
                    : GameText.MeasureWidth(font, operation.RightText, scale);
                widths[i] = operation.RightText is null ? left : left + 20f * scale + right;
                heights[i] = Math.Max(1, wrapped.Length) * GameText.LinePitch(font, scale);
            }
            contentWidth = MathF.Max(contentWidth, widths[i]);
            contentHeight += heights[i];
            if (i + 1 < body.Operations.Length) contentHeight += gap;
        }

        Vector2 size = new(contentWidth + padding * 2f, contentHeight + padding * 2f);
        Vector2 position = anchor - new Vector2(size.X * pivot.X, size.Y * pivot.Y);
        position = SharedGameTooltipClampToScreen(position, size, ImGui.GetIO().DisplaySize);
        var lines = ImmutableArray.CreateBuilder<PreparedInventoryTooltipLine>(
            body.Operations.Length);
        float y = position.Y + padding;
        var coins = ImmutableArray.CreateBuilder<PreparedSharedGameTooltipMoneyCoin>();
        for (int i = 0; i < body.Operations.Length; i++)
        {
            PreparedItemTooltipPaintOp operation = body.Operations[i];
            if (operation.Kind == PreparedItemTooltipPaintKind.Money)
            {
                GameTooltipMoneyRowGeometry geometry = moneyGeometries[i]!;
                float coinSize = GameTooltipUiLaw.MoneyCoinSize * scale;
                float contentLeft = position.X + padding;
                float iconTop = y + (heights[i] - coinSize) * .5f;
                float numberTop = GameText.BoxCenteredTop(GameTooltipUiLaw.MoneyFontObject,
                    iconTop, GameTooltipUiLaw.MoneyCoinSize, scale);
                for (int coin = 0; coin < geometry.Coins.Length; coin++)
                {
                    GameTooltipMoneyCoinGeometry placed = geometry.Coins[coin];
                    coins.Add(new(moneyTexts[i][coin],
                        new Vector2(contentLeft + placed.NumberX, numberTop),
                        new Vector2(contentLeft + placed.IconX, iconTop),
                        new Vector2(contentLeft + placed.IconX + coinSize, iconTop + coinSize),
                        new Vector2(placed.TexCoords.Left, placed.TexCoords.Top),
                        new Vector2(placed.TexCoords.Right, placed.TexCoords.Bottom),
                        0xffffffff));
                }
            }
            else if (operation.Kind != PreparedItemTooltipPaintKind.Separator)
            {
                Vector4 leftColor = operation.Kind switch
                {
                    PreparedItemTooltipPaintKind.Plain => Vector4.One,
                    PreparedItemTooltipPaintKind.Disabled => new(.5f, .5f, .5f, 1f),
                    _ => operation.Color,
                };
                uint leftTint = ImGui.ColorConvertFloat4ToU32(leftColor);
                string? rightText = operation.RightText;
                Vector2 rightPosition = default;
                uint rightTint = 0;
                if (rightText is not null)
                {
                    float rightWidth = GameText.MeasureWidth(fonts[i], rightText, scale);
                    rightPosition = new(position.X + size.X - padding - rightWidth, y);
                    rightTint = ImGui.ColorConvertFloat4ToU32(operation.RightColor);
                }
                string[] wrapped = physicalLines[i];
                float linePitch = GameText.LinePitch(fonts[i], scale);
                for (int lineIndex = 0; lineIndex < Math.Max(1, wrapped.Length); lineIndex++)
                {
                    string text = wrapped.Length == 0 ? "" : wrapped[lineIndex];
                    Vector2 leftPosition = new(position.X + padding,
                        y + lineIndex * linePitch);
                    lines.Add(new(fonts[i], text, leftPosition, leftTint,
                        lineIndex == 0 ? rightText : null, rightPosition, rightTint));
                }
            }
            y += heights[i];
            if (i + 1 < body.Operations.Length) y += gap;
        }

        (Vector4 fillTint, Vector4 edgeTint) = SharedGameTooltipBackdropTints(1f);
        Vector2 thickenInset = new(5f * scale);
        uint moneyTexture = coins.Count == 0
            ? 0
            : _gameplayArt?.Handle(GameTooltipUiLaw.MoneyTexturePath) ?? 0;
        return new(_skin, position, size, scale, lines.ToImmutable(), fillTint, edgeTint,
            position + thickenInset, position + size - thickenInset,
            new Vector4(.09f, .09f, .19f, .4f), coins.ToImmutable(), moneyTexture);
    }

    private static void DrawPreparedInventoryItemTooltip(
        PreparedInventoryTooltipRenderer prepared)
    {
        ImGui.SetNextWindowPos(prepared.Position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(prepared.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin("##prepared-inventory-item-tooltip",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.Tooltip);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        float savedScale = prepared.Skin.Scale;
        try
        {
            prepared.Skin.Scale = prepared.Scale;
            prepared.Skin.DrawBackdrop(draw, prepared.Position,
                prepared.Position + prepared.Size, WowSkin.Tooltip,
                prepared.BackdropFillTint, prepared.BackdropEdgeTint);
        }
        finally
        {
            prepared.Skin.Scale = savedScale;
        }
        draw.AddRectFilled(prepared.ThickenMinimum, prepared.ThickenMaximum,
            ImGui.ColorConvertFloat4ToU32(prepared.ThickenTint));
        foreach (PreparedInventoryTooltipLine line in prepared.Lines)
        {
            GameText.Draw(draw, line.FontObject, line.Text, line.Position,
                prepared.Scale, line.Color);
            if (line.RightText is not null)
                GameText.Draw(draw, line.FontObject, line.RightText, line.RightPosition,
                    prepared.Scale, line.RightColor);
        }
        foreach (PreparedSharedGameTooltipMoneyCoin coin in prepared.MoneyCoins)
            if (prepared.MoneyTexture != 0)
                draw.AddImage((nint)prepared.MoneyTexture, coin.IconMinimum, coin.IconMaximum,
                    coin.UvMinimum, coin.UvMaximum, coin.Tint);
        foreach (PreparedSharedGameTooltipMoneyCoin coin in prepared.MoneyCoins)
            GameText.Draw(draw, GameTooltipUiLaw.MoneyFontObject, coin.AmountText,
                coin.NumberPosition, prepared.Scale, coin.Tint);
        draw.PopClipRect();
        ImGui.End();
    }

    private bool OfferPreparedItemTooltip(
        in GameTooltipOwnerKey owner,
        in ItemTooltipBodySnapshot body,
        Vector2? nextWindowPosition = null,
        int comparisonCount = 0,
        Action? preparedFollowingRenderer = null,
        Vector2? nextWindowPivot = null)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        Vector2? preparedPosition = nextWindowPosition;
        Vector2 preparedPivot = nextWindowPivot ?? Vector2.Zero;
        Action? preparedFollowing = preparedFollowingRenderer;
        PreparedInventoryTooltipRenderer? inventoryRenderer = preparedPosition is { } at
            ? PrepareInventoryItemTooltipRenderer(body, at, preparedPivot)
            : null;
        ItemTooltipBodySnapshot preparedBody = body;
        bool offered = OfferPreservedSharedGameTooltipRenderer(owner, () =>
        {
            if (inventoryRenderer is not null)
                DrawPreparedInventoryItemTooltip(inventoryRenderer);
            else
            {
                ImGui.BeginTooltip();
                DrawPreparedItemTooltipBody(preparedBody);
                ImGui.EndTooltip();
            }
            preparedFollowing?.Invoke();
        });
        if (!offered) return false;

        GameTooltipOwnerToken token = CurrentSharedGameTooltipOwnerToken();
        if (!SetSharedGameTooltipComparisonCount(token, comparisonCount))
            throw new InvalidOperationException(
                "A freshly offered item tooltip rejected its comparison ordinal.");
        return true;
    }

    private ImmutableArray<PreparedPaperDollComparisonTooltip>
        PreparePaperDollComparisonTooltips(ItemTemplate hoveredItem)
    {
        // Freeze the complete SHOW_COMPARE_TOOLTIP verdict at producer time. Equipped-item,
        // ammo, and inspect adapters never enter this method, preserving the self-compare rule.
        bool shift = ImGui.GetIO().KeyShift;
        bool show = PaperDollUiLaw.ShowBagItemComparison(_characterOpen, _characterTab, shift,
            sourceIsEquipped: false);
        if (!show || _net is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player))
            return [];

        float scale = GameplayUiScale();
        Vector2 frameOrigin = new(0, 104f * scale);
        bool captureParity = _uiParityArmed && _uiParityPanel == "character-frame";
        int candidateCount = PaperDollUiLaw.ComparisonSlotCount(hoveredItem.InventoryType);
        var prepared =
            ImmutableArray.CreateBuilder<PreparedPaperDollComparisonTooltip>(candidateCount);
        for (int ordinal = 0; ordinal < candidateCount; ordinal++)
        {
            int slot = PaperDollUiLaw.ComparisonSlot(hoveredItem.InventoryType, ordinal);
            // Preserve the authored arm before its live slot listener decides whether content
            // exists. Missing ordinal one cannot compact a surviving ShoppingTooltip2.
            int tooltipNumber = ordinal + 1;
            ulong equippedGuid = player.Fields.PlayerInventorySlot(slot);
            if (equippedGuid == 0 || !_entities.TryGet(equippedGuid, out WorldEntity equipped))
                continue;
            _items.Require(equipped.Entry, equipped.Guid, _net);
            if (!_items.TryGet(equipped.Entry, out ItemTemplate? equippedTemplate) ||
                equippedTemplate is null)
                continue;

            PaperDollUiLaw.LogicalRect logical = PaperDollUiLaw.EquipmentSlotRect(slot);
            Vector2 slotMin = frameOrigin + new Vector2(logical.X, logical.Y) * scale;
            PaperDollUiLaw.TooltipAnchor anchor = PaperDollUiLaw.ShoppingTooltipAnchor(ordinal);
            Vector2 windowAt = slotMin + new Vector2(logical.Width,
                ordinal == 0 ? 0f : logical.Height) * scale;
            prepared.Add(new(tooltipNumber, slot,
                PrepareItemTooltipBodySnapshot(equippedTemplate,
                    equipped.Fields.ItemStackCount, equipped.Fields.ItemDurability,
                    equipped.Fields.ItemMaxDurability, compact: true,
                    liveInstance: equipped),
                windowAt, new Vector2(anchor.PivotX, anchor.PivotY), scale,
                anchor.Point, anchor.RelativePoint, PaperDollSlotElement(slot), captureParity));
        }
        return prepared.ToImmutable();
    }

    private void DrawPreparedPaperDollComparisonTooltips(
        ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                 ImGuiWindowFlags.AlwaysAutoResize |
                                 ImGuiWindowFlags.NoInputs |
                                 ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.NoFocusOnAppearing |
                                 ImGuiWindowFlags.NoNav;
        bool collected = false;
        foreach (PreparedPaperDollComparisonTooltip comparison in comparisons)
        {
            ImGui.SetNextWindowPos(comparison.WindowPosition, ImGuiCond.Always,
                comparison.WindowPivot);
            ImGui.Begin($"##paper-doll-comparison-{comparison.TooltipNumber}", flags);
            ImGui.SetWindowFontScale(
                Math.Max(.5f, 10f * comparison.Scale / ImGui.GetFontSize()));
            ImGui.TextDisabled("Currently Equipped");
            DrawPreparedItemTooltipBody(comparison.Body);
            if (comparison.CaptureParity)
            {
                Vector2 windowMin = ImGui.GetWindowPos();
                Vector2 windowSize = ImGui.GetWindowSize();
                Vector4 content = new(windowMin.X, windowMin.Y,
                    windowMin.X + windowSize.X, windowMin.Y + windowSize.Y);
                CollectUiParityDraw($"ShoppingTooltip{comparison.TooltipNumber}", "Frame",
                    windowMin, windowSize, comparison.ParentElement,
                    new("", 0, "TOOLTIP", comparison.AnchorPoint,
                        comparison.ParentElement, comparison.RelativePoint, 0, 0,
                        @"Fonts\FRIZQT__.TTF", 10, ContentRect: content, ClipRect: content,
                        ClipMask: "COMPACT_NO_DESCRIPTION", Visible: true, Enabled: false,
                        InteractionState:
                            $"shift-compare-live-slot:{comparison.EquipmentSlot}",
                        Strata: "TOOLTIP"));
                collected = true;
            }
            ImGui.End();
        }
        if (collected) _shoppingTooltipParityRendererCollected = true;
    }

    private void ArmDeferredShoppingTooltipParityCapture(
        ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons)
    {
        ImmutableArray<ShoppingTooltipParityExpectation> expectations = comparisons
            .Where(comparison => comparison.CaptureParity)
            .Select(comparison => new ShoppingTooltipParityExpectation(
                comparison.TooltipNumber, comparison.ParentElement))
            .ToImmutableArray();
        if (expectations.IsEmpty) return;
        _shoppingTooltipParityCompletionPending = true;
        _shoppingTooltipParityRendererCollected = false;
        _shoppingTooltipParityExpectations = expectations;
    }

    private void CompleteDeferredShoppingTooltipParityCapture()
    {
        if (!_shoppingTooltipParityCompletionPending) return;
        bool collected = _shoppingTooltipParityRendererCollected;
        ImmutableArray<ShoppingTooltipParityExpectation> expectations =
            _shoppingTooltipParityExpectations;
        _shoppingTooltipParityCompletionPending = false;
        _shoppingTooltipParityRendererCollected = false;
        _shoppingTooltipParityExpectations = [];

        if (!collected)
            foreach (ShoppingTooltipParityExpectation expectation in expectations)
                ClassifyUiParity($"ShoppingTooltip{expectation.TooltipNumber}", "Frame",
                    expectation.ParentElement, "NOT-DRAWN",
                    "shared-tooltip-owner-replaced-before-tooltip-stratum");
        MarkUiParityFrameComplete();
    }

    private void DrawInventorySlot(ImDrawListPtr dl, WorldEntity owner, int container, int slot,
        Vector2 min, float scale, string id)
    {
        if (_items is null || _gameplayArt is null || _net is null) return;
        Vector2 max = min + new Vector2(37f) * scale;
        ulong guid = ResolveSlotGuid(owner, container, slot);
        WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
        ItemTemplate? item = null;
        if (instance is not null)
        {
            _items.Require(instance.Entry, instance.Guid, _net);
            _items.TryGet(instance.Entry, out item);
        }
        bool locked = IsInventorySlotLocked(container, slot);
        bool parityProof = _uiParityArmed &&
            (_uiParityPanel == "backpack" && container == 0 ||
             _uiParityPanel == "equipped-bag" && container == _uiParityEquippedBagContainer);
        string parityRoot = container == 0 ? "ContainerFrame1" : $"ContainerFrameBag{container}";
        int liveSize = container switch
        {
            0 => InventoryUiLaw.BackpackSlots,
            InventoryUiLaw.KeyringContainer => InventoryUiLaw.KeyringSize(owner.Level),
            _ => (int)Math.Clamp(owner.Fields.ContainerNumSlots, 1,
                InventoryUiLaw.MaxContainerSlots),
        };
        int physical = liveSize - slot;
        string parityButton = $"{parityRoot}Item{physical}";
        if (parityProof)
        {
            CollectUiParityDraw(parityButton, "Button", min, max - min, parityRoot,
                new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", parityRoot, "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / scale, -((min.Y - _uiParityOrigin.Y) / scale),
                    Enabled: !locked, InteractionState: locked ? "locked" : "normal",
                    HitMin: min, HitMax: max));
            if (item is not null)
                CollectUiParityDraw(parityButton + "Icon", "Texture", min, max - min, parityButton,
                    new(item.IconPath, locked ? 0xff666666 : 0xffffffff, "BACKGROUND", "CENTER",
                        parityButton, "CENTER", 0, 0));
            else
                ClassifyUiParity(parityButton + "Icon", "Texture", parityButton, "NOT-DRAWN",
                    "EMPTY_SLOT_NO_ITEM_TEXTURE");
            Vector2 ringCenter = (min + max) * .5f + new Vector2(0, -scale);
            CollectUiParityDraw(parityButton + "NormalTexture", "NormalTexture",
                ringCenter - new Vector2(32f * scale), new Vector2(64f * scale), parityButton,
                new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff, "ARTWORK", "CENTER",
                    parityButton, "CENTER", 0, -1));
        }
        if (item is not null)
        {
            uint icon = _gameplayArt.Handle(item.IconPath);
            if (icon != 0) dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
                locked ? 0xff666666 : 0xffffffff);
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##{id}", max - min,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        bool repairReleased = _vendorRepairMode && hovered &&
            ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsItemDeactivated();
        // A possessed bot's bags are now editable: swap/split/equip are threaded through GetSuiActor
        // server-side, so they act on the driven bot. Left-click drag/pickup and context-equip route
        // there; the un-threaded context actions (mail/bank/delete/gift/quest) re-check the strict
        // CanAuthorSessionInventory and stay blocked while possessing.
        // CanAuthorControlledOrSelf: a possessed bot's bags (threaded via GetSuiActor) OR your own
        // character even from the free-view sky — so your own bag items are usable there too, not
        // just the quick slots. Session-only actions (mail/bank/delete/gift/quest) still re-check
        // the strict CanAuthorSessionInventory below and stay blocked on a bot.
        bool interactive = CanAuthorControlledOrSelf;
        bool leftClicked = interactive && !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool rightClicked = interactive && !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool dressUpClick = leftClicked && ImGui.GetIO().KeyCtrl && instance is not null;
        if (dressUpClick)
        {
            TryOnDressUp(instance!.Entry);
            leftClicked = rightClicked = false;
        }
        if (repairReleased) TryRepairMerchantItem(instance?.Guid ?? 0);
        if (_itemCastSpell != 0)
        {
            if (rightClicked)
            {
                CancelItemTargeting();
                leftClicked = rightClicked = false;
            }
            else if (leftClicked)
            {
                if (instance is not null) TryBindItemCast(instance, item, bindConfirmed: false);
                // Empty slots keep the cursor armed, and occupied slots are consumed by the
                // item-target binder even when its local gate refuses or opens a confirmation.
                leftClicked = rightClicked = false;
            }
        }
        if (_enchantConfirmation is not null) leftClicked = rightClicked = false;
        bool tradePlacement = _tradeOpen && _tradePlaceSlot >= 0 &&
            InventoryUiLaw.ToWire(container, slot) is not null;
        InventoryUiLaw.SlotClickAction click = InventoryUiLaw.ClickAction(leftClicked, rightClicked,
            ImGui.GetIO().KeyShift, HasCarriedItem, instance is not null,
            instance?.Fields.ItemStackCount ?? 0, locked, tradePlacement);
        if (click == InventoryUiLaw.SlotClickAction.TradePlace &&
            InventoryUiLaw.ToWire(container, slot) is { } trade)
            PlaceTradeItem(trade.Bag, trade.Slot, instance);
        else if (click == InventoryUiLaw.SlotClickAction.Split)
            OpenStackSplit(container, slot, (int)(instance?.Fields.ItemStackCount ?? 0),
                new(max.X, min.Y));
        else if (click == InventoryUiLaw.SlotClickAction.PickupOrPlace)
        {
            CancelStackSplit();
            PickupOrPlaceItem(container, slot, guid);
        }
        else if (click == InventoryUiLaw.SlotClickAction.ClearCarried)
        {
            CancelStackSplit();
            ClearCarriedItem();
        }
        else if (click == InventoryUiLaw.SlotClickAction.ContextAction)
        {
            CancelStackSplit();
            if (instance is not null && item is not null && InventoryUiLaw.ToWire(container, slot) is { } wire)
            {
                if (_vendor is not null) SellToOpenVendor(instance.Guid);
                else if (_bankOpen)
                {
                    // Direction follows the SOURCE: an item already in the bank (vault or a bank
                    // bag, containers 5..10) comes OUT with CMSG_AUTOSTORE_BANK_ITEM; anything
                    // else goes IN with CMSG_AUTOBANK_ITEM. Bank-bag right-clicks used to send
                    // the deposit opcode for an item that was already deposited. 2026-09-01.
                    bool withdrawing = container == InventoryUiLaw.BankContainer || container is >= 5 and <= 10;
                    if (withdrawing)
                    {
                        if (!RefuseTacticalFreezeLiveCommand("withdrawing a bank item") &&
                            !RefuseTacticalFrozenActor(_bankSource, "withdraw through it"))
                            _net.AutostoreBankItem(wire.Bag, wire.Slot);
                    }
                    else DepositBankItem(wire.Bag, wire.Slot, instance);
                }
                else if (_mailOpen && _mailTab == 1) AttachMailItem(instance.Guid, instance.Entry);
                else if (_auctionOpen && _auctionTab == 2) StageAuctionSellItem(container, slot, instance);
                else if (instance.Fields.ItemTextId != 0)
                    OpenItemTextLetter(instance, item);
                else if (item.PageText != 0)
                    OpenItemTextPages(instance.Guid, item.Name, item.PageText, item.PageMaterial);
                else if (InventoryUiLaw.UnwrapsGift(item.Flags, instance.Fields.ItemFlags))
                {
                    if (CanAuthorSessionInventory) _net.OpenItem(wire.Bag, wire.Slot);
                }
                else if (item.StartQuest != 0)
                {
                    if (CanAuthorSessionInventory)
                        _net.QuestgiverQuery(instance.Guid, item.StartQuest);
                }
                else if (InventoryUiLaw.OpensLoot(item.Flags))
                {
                    // Cracking open a lootable item (clam / lockbox): the server answers
                    // SendLoot(item guid, LOOT_CORPSE) — the item's OWN guid, loot type 1. Arm the
                    // loot latch to that guid so the SMSG_LOOT_RESPONSE is ADMITTED (an unlatched
                    // type-1 response is otherwise rejected, so the window never opened). It is a
                    // session action (loot flows to _player, server requires IsSelfMover) — allow it
                    // for your OWN character even from the free-view sky, but never while possessing
                    // a bot (that would misroute its loot to you, and the server drops it anyway).
                    if (ControlledGuid == LocalPlayerGuid)
                    {
                        AddPendingBagLock(container, slot, ++_pendingBagOperation);
                        _lootPendingGuid = instance.Guid;
                        _net.OpenItem(wire.Bag, wire.Slot);
                    }
                }
                else if (item.InventoryType == InventoryUiLaw.InventoryTypeAmmo)
                {
                    // Ammo loads the quiver slot with CMSG_SET_AMMO, never AUTOEQUIP (the
                    // reference's single auto-equip sender forks INVTYPE_AMMO out first; sending
                    // the equip opcode for arrows just earned a server refusal). 2026-09-01.
                    if (CanAuthorControlledOrSelf) _net.SetAmmo(item.Entry);
                }
                else if (item.InventoryType != 0)
                {
                    // Equip is threaded via GetSuiActor server-side, so a possessed bot equips its
                    // own gear — same gate as the drag-to-equip-slot path.
                    if (CanAuthorControlledOrSelf) _net.AutoEquipItem(wire.Bag, wire.Slot);
                }
                else SendItemUse(wire.Bag, wire.Slot, instance, item);
            }
        }
        if (!dressUpClick && !_vendorRepairMode && _itemCastSpell == 0 && _enchantConfirmation is null)
            HandleInventoryDrag(container, slot, guid, item);

        uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f + new Vector2(0, -scale), half = new(32f * scale);
            dl.AddImage((nint)ring, center - half, center + half);
        }
        if (ImGui.IsItemActive())
        {
            uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
            if (depress != 0) dl.AddImage((nint)depress, min, max);
            if (parityProof)
                CollectUiParityDraw(parityButton + "PushedTexture", "PushedTexture", min, max - min,
                    parityButton, new(@"Interface\Buttons\UI-Quickslot-Depress", 0xffffffff,
                        "ARTWORK", "CENTER", parityButton, "CENTER", 0, 0));
        }
        if (hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
            if (parityProof)
                CollectUiParityDraw(parityButton + "HighlightTexture", "HighlightTexture", min, max - min,
                    parityButton, new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff,
                        "OVERLAY", "CENTER", parityButton, "CENTER", 0, 0));
        }
        if (item?.UseSpellId > 0 &&
            _actions.TryCooldownDisplay(item.UseSpellId, item.Entry, item.UseSpellCategory,
                NowSeconds(), out CooldownDisplay cooldown))
        {
            Vector2 cdMin = min + new Vector2(.5f, .5f) * scale;
            Vector2 cdMax = cdMin + new Vector2(36f) * scale;
            if (cooldown.SweepFraction is float sweep) DrawCooldownSwipe(dl, cdMin, cdMax, sweep);
            if (cooldown.FlashProgress is float flash) DrawCooldownFlash(dl, cdMin, cdMax, flash);
        }
        uint count = instance?.Fields.ItemStackCount ?? 0;
        if (_splitContainer == container && _splitSlot == slot)
        {
            _splitOwnerVisible = true;
            _splitOwnerTopRight = new(max.X, min.Y);
            if (count < 2)
                CancelStackSplit();
            else
            {
                _splitMaximum = (int)count;
                _splitCount = StackSplitUiLaw.Clamp(_splitCount, _splitMaximum);
            }
        }
        if (count > 1)
        {
            // ItemButtonTemplate: Count BOTTOMRIGHT (-5, 2) — the parity proof below says so too.
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                new Vector2(max.X - 5f * scale,
                    max.Y - GameText.EmPixels("NumberFontNormal", scale) - 2f * scale), scale);
            if (parityProof)
                CollectUiParityDraw(parityButton + "Count", "FontString", min, max - min, parityButton,
                    new("", 0xffffffff, "OVERLAY", "BOTTOMRIGHT", parityButton, "BOTTOMRIGHT", -5, 2,
                        @"Fonts\FRIZQT__.TTF", 12));
        }
        if (hovered && item is not null)
        {
            InventoryUiLaw.TooltipSeat tooltipSeat = InventoryUiLaw.ItemTooltipSeat(
                min, max, ImGui.GetIO().DisplaySize.X);
            ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(item, count,
                instance?.Fields.ItemDurability ?? 0,
                instance?.Fields.ItemMaxDurability ?? 0,
                instanceFlags: instance?.Fields.ItemFlags,
                liveInstance: instance,
                ownerGuid: ControlledGuid);   // requirements read the shown unit (possessed bot)
            body = AppendVendorMoneyRow(body, item, count, instance);
            ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons =
                PreparePaperDollComparisonTooltips(item);
            Action? drawComparisons = comparisons.IsEmpty
                ? null
                : () => DrawPreparedPaperDollComparisonTooltips(comparisons);
            bool offered = OfferPreparedItemTooltip(
                InventoryItemGameTooltipOwner(container, physical), body, tooltipSeat.Position,
                HighestLiveComparisonOrdinal(
                    comparisons.Select(comparison => comparison.TooltipNumber)),
                drawComparisons, tooltipSeat.Pivot);
            if (offered) ArmDeferredShoppingTooltipParityCapture(comparisons);
            string? cursor = _vendorRepairMode ? "Repair" :
                InventoryUiLaw.HoverCursor(_vendor is not null,
                    instance?.Fields.ItemTextId != 0);
            if (cursor is not null) DrawBagHoverCursor(cursor);
        }
    }

    private void HandleInventoryDrag(int container, int slot, ulong guid, ItemTemplate? item)
    {
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            CancelStackSplit();
            if (!HasCarriedItem) PickupOrPlaceItem(container, slot, guid, ignoreModifiers: true);
            ImGui.SetDragDropPayload("MSUI_INVENTORY_ITEM", IntPtr.Zero, 0);
            ImGui.TextUnformatted(item?.Name ?? "Item");
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
                PickupOrPlaceItem(container, slot, guid, ignoreModifiers: true);
            ImGui.EndDragDropTarget();
        }
    }

    private void HandleBagBarDrag(int container, int equipmentSlot, ulong guid,
        ItemTemplate? item, Vector2 min, float size, float scale)
    {
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            if (!HasCarriedItem)
                PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, guid, true);
            ImGui.SetDragDropPayload("MSUI_INVENTORY_ITEM", IntPtr.Zero, 0);
            ImGui.TextUnformatted(item?.Name ?? "Equip Container");
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
                PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, guid, true);
            ImGui.EndDragDropTarget();
        }
    }

    private void HandleKeyringDropTarget(WorldEntity player)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
            PutCarriedItemInKeyring(player);
        ImGui.EndDragDropTarget();
    }

    private readonly Dictionary<string, HardwareCursorImage?> _hardwareCursorImages =
        new(StringComparer.OrdinalIgnoreCase);

    private bool TryUseHardwareCursor(string stem)
    {
        if (_gameplayArt is null || string.IsNullOrWhiteSpace(stem)) return false;
        float scale = Math.Clamp(GameplayUiScale() * Settings.Display.CursorScale, .5f, 4f);
        int cursorPixels = Math.Clamp((int)MathF.Round(32f * scale), 16, 128);
        string cacheKey = $"{stem}@{cursorPixels}";
        if (!_hardwareCursorImages.TryGetValue(cacheKey, out HardwareCursorImage? image))
        {
            GameplayArt.PreparedTexture? prepared =
                _gameplayArt.Prepare($@"Interface\Cursor\{stem}");
            image = prepared is { } pixels
                ? HardwareCursorLaw.ResizeNearest(
                    HardwareCursorLaw.FromBgra(pixels.Pixels, pixels.Width, pixels.Height),
                    Math.Max(1, (int)MathF.Round(pixels.Width * scale)),
                    Math.Max(1, (int)MathF.Round(pixels.Height * scale)))
                : null;
            _hardwareCursorImages[cacheKey] = image;
        }
        if (_window.CursorTrace)
            Console.WriteLine($"[cursor] try stem={stem} image={(image is null ? "null" : $"{image.Value.Width}x{image.Value.Height}")} captured={_window.MouseCaptured}");
        if (image is not { } resolved || !_window.UseHardwareCursor(cacheKey, resolved)) return false;
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
        return true;
    }

    private void DrawBagHoverCursor(string stem)
    {
        if (_gameplayArt is null) return;
        // Command View: software cursor only. The OS cursor is rebuilt on every stem change and
        // a click-storm over the field (Point/Attack/Point...) produced a black square at the
        // pointer and a sword that showed only half the time (owner, 2026-09-02). The drawn
        // cursor is one frame behind and never wrong.
        if (!_freeView && TryUseHardwareCursor(stem)) return;
        uint cursor = _gameplayArt.Handle($@"Interface\Cursor\{stem}");
        if (cursor == 0) return;
        ImGui.GetIO().ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
        ImGui.SetMouseCursor(ImGuiMouseCursor.None);
        if (_freeView) _window.RequestSoftwareCursor();
        Vector2 min = ImGui.GetIO().MousePos;
        float size = 32f * Math.Clamp(GameplayUiScale() * Settings.Display.CursorScale, .5f, 4f);
        ImGui.GetForegroundDrawList().AddImage((nint)cursor, min, min + new Vector2(size));
    }

    private ulong ResolveSlotGuid(WorldEntity owner, int container, int slot) => container switch
    {
        0 => owner.Fields.PlayerBackpackSlot(slot),
        InventoryUiLaw.KeyringContainer => owner.Fields.PlayerKeyringSlot(slot),
        >= 1 and <= InventoryUiLaw.BankBagContainerLast => owner.Fields.ContainerSlot(slot),
        InventoryUiLaw.EquipmentContainer => owner.Fields.PlayerInventorySlot(slot),
        _ => 0,
    };

    private WorldEntity? ResolveInventoryItem(int container, int slot)
    {
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return null;
        ulong guid;
        if (container == InventoryUiLaw.BankContainer) guid = player.Fields.PlayerBankSlot(slot);
        else if (container == InventoryUiLaw.BankBagEquipmentContainer)
            guid = player.Fields.PlayerBankBagSlot(slot);
        else if (container == 0) guid = player.Fields.PlayerBackpackSlot(slot);
        else if (container == InventoryUiLaw.KeyringContainer) guid = player.Fields.PlayerKeyringSlot(slot);
        else if (container == InventoryUiLaw.EquipmentContainer) guid = player.Fields.PlayerInventorySlot(slot);
        else if (container is >= 1 and <= 4)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(18 + container);
            guid = bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag)
                ? bag.Fields.ContainerSlot(slot) : 0;
        }
        else if (container is >= InventoryUiLaw.BankBagContainerFirst and
                 <= InventoryUiLaw.BankBagContainerLast)
        {
            ulong bagGuid = player.Fields.PlayerBankBagSlot(
                container - InventoryUiLaw.BankBagContainerFirst);
            guid = bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag)
                ? bag.Fields.ContainerSlot(slot) : 0;
        }
        else guid = 0;
        return guid != 0 && _entities.TryGet(guid, out WorldEntity item) ? item : null;
    }

    private bool IsInventorySlotLocked(int container, int slot) =>
        HasCarriedItem && _carriedContainer == container && _carriedSlot == slot ||
        _pendingBagLocks.ContainsKey((container, slot));

    private void AddPendingBagLock(int container, int slot, long operation)
    {
        WorldEntity? item = ResolveInventoryItem(container, slot);
        _pendingBagLocks[(container, slot)] = new(item?.Guid ?? 0,
            item?.Fields.ItemStackCount ?? 0, NowSeconds(), operation);
    }

    private void ResetPendingInventoryOps()
    {
        if (DeleteItemUiLaw.Visible(_staticPopupSlots) is { } openDestroy)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, openDestroy.Instance.Definition.Type));
        _deleteItemConfirmation = null;
        _pendingBagLocks.Clear();
        _pendingInventoryTransition = null;
        _pendingBankTransition = null;
        _itemEnchantTimers.Clear();
    }

    private void ApplyItemEnchantTime(byte[] body)
    {
        ItemEnchantTimePacket packet = ItemEnchantTimePackets.Parse(body);
        _itemEnchantTimers.Set(packet.ItemGuid, packet.Slot, packet.Seconds, NowSeconds());
        EmitInterface("inventory", "enchant-time", packet.Seconds == 0 ? "CLEARED" : "UPDATED",
            packet.ItemGuid,
            $"slot={packet.Slot};seconds={packet.Seconds};player={packet.PlayerGuid:X16}");
    }

    private void ApplyInventoryChangeFailure(byte[] body)
    {
        InventoryChangeFailurePacket packet = InventoryFailurePackets.Parse(body);
        if (packet.Reason == 0) return;

        HashSet<long> matchedOperations = packet.ItemGuid == 0
            ? []
            : _pendingBagLocks.Values
                .Where(value => value.Guid == packet.ItemGuid)
                .Select(value => value.Operation)
                .ToHashSet();
        if (matchedOperations.Count == 0)
            _pendingBagLocks.Clear();
        else
            foreach ((int Container, int Slot) key in _pendingBagLocks
                         .Where(pair => matchedOperations.Contains(pair.Value.Operation))
                         .Select(pair => pair.Key).ToArray())
                _pendingBagLocks.Remove(key);

        _pendingInventoryTransition = null;
        _pendingBankTransition = null;
        EmitInterface("inventory", "change-failure", "REFUSED", packet.ItemGuid,
            $"reason={packet.Reason};requiredLevel={packet.RequiredLevel ?? 0};bagSlot={packet.BagSlot};" +
            $"locks={_pendingBagLocks.Count}");

        string text = InventoryFailureText(packet);
        if (text.Length > 0) ShowUiError(text);
    }

    private string InventoryFailureText(InventoryChangeFailurePacket packet)
    {
        if (InventoryErrorUiLaw.IsSilent(packet.Reason)) return "";

        string? family = packet.Reason == 16 ? InventoryFailureBagFamily(packet.BagSlot) : null;
        string key = family is null
            ? InventoryErrorUiLaw.GlobalStringKey(packet.Reason)
            : "ERR_WRONG_BAG_TYPE_SUBCLASS";
        string format = InventoryGlobalString(key);
        if (format.Length == 0) return "";
        if (packet.RequiredLevel is uint level)
            format = format.Replace("%d", level.ToString(), StringComparison.Ordinal);
        if (family is not null)
            format = format.Replace("%s", family, StringComparison.Ordinal);
        return format;
    }

    private string? InventoryFailureBagFamily(byte bagSlot)
    {
        if (bagSlot == byte.MaxValue || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return null;
        ulong bagGuid = player.Fields.PlayerInventorySlot(bagSlot);
        if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag) ||
            !_items.TryGet(bag.Entry, out ItemTemplate? template) || template is null) return null;
        return InventoryErrorUiLaw.BagFamilyName(template.BagFamily);
    }

    private string InventoryGlobalString(string key, string fallback = "That item can't be moved.")
    {
        if (!_inventoryGlobalStringsLoaded)
        {
            _inventoryGlobalStringsLoaded = true;
            byte[]? bytes = _mpq?.ReadFile(@"Interface\FrameXML\GlobalStrings.lua");
            if (bytes is not null)
            {
                string luaSource = System.Text.Encoding.UTF8.GetString(bytes);
                _inventoryGlobalStringsSource = luaSource;
                HashSet<string> keys = Enumerable.Range(1, 255)
                    .Select(reason => InventoryErrorUiLaw.GlobalStringKey((byte)reason))
                    .Append("ERR_WRONG_BAG_TYPE_SUBCLASS")
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string wanted in keys)
                    if (TryReadLuaString(luaSource, wanted, out string loadedValue))
                        _inventoryGlobalStrings[wanted] = loadedValue;
            }
        }

        if (_inventoryGlobalStrings.TryGetValue(key, out string? value)) return value;
        if (_inventoryGlobalStringsSource is { } cachedSource &&
            TryReadLuaString(cachedSource, key, out string dynamicValue))
        {
            _inventoryGlobalStrings[key] = dynamicValue;
            return dynamicValue;
        }
        return key == "ERR_CANT_BE_DISENCHANTED" ? "" :
            InventoryErrorFallbacks.GetValueOrDefault(key, fallback);
    }

    private static readonly IReadOnlyDictionary<string, string> InventoryErrorFallbacks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ERR_CANT_EQUIP_LEVEL_I"] = "You must reach level %d to use that item.",
            ["ERR_WRONG_SLOT"] = "That item does not go in that slot.",
            ["ERR_BAG_FULL"] = "That bag is full.",
            ["ERR_INV_FULL"] = "Inventory is full.",
            ["ERR_BANK_FULL"] = "Your bank is full",
            ["ERR_WRONG_BAG_TYPE"] = "That item doesn't go in that container.",
            ["ERR_WRONG_BAG_TYPE_SUBCLASS"] = "Only %s can be placed in that.",
            ["ERR_ITEM_LOCKED"] = "Item is locked.",
            ["ERR_NOT_ENOUGH_MONEY"] = "You don't have enough money.",
            ["ERR_PLAYER_DEAD"] = "You can't do that when you're dead.",
        };

    private void ObserveBagLocks()
    {
        double now = NowSeconds();
        foreach (((int container, int slot) key, PendingBagLock pending) in _pendingBagLocks.ToArray())
        {
            WorldEntity? item = ResolveInventoryItem(key.container, key.slot);
            if (now - pending.SentAt > 5 || item?.Guid != pending.Guid ||
                (item?.Fields.ItemStackCount ?? 0) != pending.Count)
                _pendingBagLocks.Remove(key);
        }
    }

    private void OpenStackSplit(int container, int slot, int stackCount, Vector2 ownerTopRight)
    {
        if (stackCount < 2)
        {
            CancelStackSplit();
            return;
        }
        _splitContainer = container;
        _splitSlot = slot;
        _splitMaximum = stackCount;
        _splitCount = 1;
        _splitTyped = false;
        _splitOwnerTopRight = ownerTopRight;
        _splitOwnerVisible = true;
    }

    private void CancelStackSplit()
    {
        _splitContainer = InventoryUiLaw.EmptyContainer;
        _splitSlot = -1;
        _splitMaximum = 0;
        _splitTyped = false;
    }

    private bool TryCancelStackSplitOnEscape()
    {
        if (_splitContainer == InventoryUiLaw.EmptyContainer) return false;
        CancelStackSplit();
        return true;
    }

    private void DrawStackSplit()
    {
        if (_splitContainer == InventoryUiLaw.EmptyContainer) return;
        if (!_splitOwnerVisible || _gameplayArt is null)
        {
            CancelStackSplit();
            return;
        }

        float scale = GameplayUiScale();
        StackSplitUiLaw.ScreenRect frame = StackSplitUiLaw.Frame(_splitOwnerTopRight, scale);
        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##stack-split-frame", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint plate = _gameplayArt.Handle(StackSplitUiLaw.PlatePath);
        if (plate != 0)
            draw.AddImage((nint)plate, frame.Min, frame.Max, Vector2.Zero,
                StackSplitUiLaw.PlateUvMax);
        GameText.DrawRightAligned(draw, "GameFontHighlight", _splitCount.ToString(),
            StackSplitUiLaw.Point(frame.Min, StackSplitUiLaw.CountRightEdge, scale), scale);

        bool left = DrawStackSplitArrow(draw, left: true, frame.Min, scale,
            enabled: _splitCount > 1);
        bool right = DrawStackSplitArrow(draw, left: false, frame.Min, scale,
            enabled: _splitCount < _splitMaximum);
        bool okay = VanillaButton(draw, "##stack-split-okay", "OKAY",
            StackSplitUiLaw.Point(frame.Min, StackSplitUiLaw.OkayButton, scale),
            StackSplitUiLaw.ButtonSize, scale);
        bool cancel = VanillaButton(draw, "##stack-split-cancel", "CANCEL",
            StackSplitUiLaw.Point(frame.Min, StackSplitUiLaw.CancelButton, scale),
            StackSplitUiLaw.ButtonSize, scale);

        bool enter = ImGui.IsKeyPressed(ImGuiKey.Enter, false) ||
            ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
        if (left || ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false))
            _splitCount = StackSplitUiLaw.Clamp(_splitCount - 1, _splitMaximum);
        if (right || ImGui.IsKeyPressed(ImGuiKey.RightArrow, false))
            _splitCount = StackSplitUiLaw.Clamp(_splitCount + 1, _splitMaximum);
        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
            (_splitCount, _splitTyped) = StackSplitUiLaw.Backspace(_splitCount, _splitMaximum);
        for (int digit = 0; digit <= 9; digit++)
            if (StackSplitDigitPressed(digit))
                (_splitCount, _splitTyped) = StackSplitUiLaw.AppendDigit(
                    _splitCount, _splitTyped, digit, _splitMaximum);

        ImGui.End();
        if (okay || enter)
        {
            _carriedContainer = _splitContainer;
            _carriedSlot = _splitSlot;
            _carriedCount = _splitCount;
            CancelStackSplit();
        }
        else if (cancel) CancelStackSplit();
    }

    private bool DrawStackSplitArrow(ImDrawListPtr draw, bool left, Vector2 origin, float scale,
        bool enabled)
    {
        StackSplitUiLaw.ScreenRect arrow = StackSplitUiLaw.Arrow(origin, left, scale);
        ImGui.SetCursorScreenPos(arrow.Min);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton(left ? "##stack-split-left" : "##stack-split-right",
            arrow.Size);
        bool held = enabled && ImGui.IsItemActive();
        if (!enabled) ImGui.EndDisabled();
        string stem = left ? StackSplitUiLaw.LeftArrowStem : StackSplitUiLaw.RightArrowStem;
        string suffix = !enabled ? "-Disabled" : held ? "-Down" : "-Up";
        uint art = _gameplayArt?.Handle(stem + suffix) ?? 0;
        if (art != 0) draw.AddImage((nint)art, arrow.Min, arrow.Max);
        return enabled && clicked;
    }

    private static bool StackSplitDigitPressed(int digit)
    {
        ImGuiKey row = digit switch
        {
            0 => ImGuiKey._0, 1 => ImGuiKey._1, 2 => ImGuiKey._2, 3 => ImGuiKey._3,
            4 => ImGuiKey._4, 5 => ImGuiKey._5, 6 => ImGuiKey._6, 7 => ImGuiKey._7,
            8 => ImGuiKey._8, _ => ImGuiKey._9,
        };
        ImGuiKey keypad = digit switch
        {
            0 => ImGuiKey.Keypad0, 1 => ImGuiKey.Keypad1, 2 => ImGuiKey.Keypad2,
            3 => ImGuiKey.Keypad3, 4 => ImGuiKey.Keypad4, 5 => ImGuiKey.Keypad5,
            6 => ImGuiKey.Keypad6, 7 => ImGuiKey.Keypad7, 8 => ImGuiKey.Keypad8,
            _ => ImGuiKey.Keypad9,
        };
        return ImGui.IsKeyPressed(row, false) || ImGui.IsKeyPressed(keypad, false);
    }

    private string BindingText(GameBinding binding)
    {
        BindingPair keys = BoundKeys(binding);
        string[] names = new[] { keys.Primary, keys.Secondary }
            .Where(chord => chord.IsBound).Select(chord => FriendlyHotkey(chord)).ToArray();
        return names.Length == 0 ? "Unbound" : string.Join(" / ", names);
    }

    private bool HasKey(WorldEntity player)
    {
        for (int i = 0; i < 32; i++) if (player.Fields.PlayerKeyringSlot(i) != 0) return true;
        foreach (ulong guid in EnumeratePlayerInventoryGuids(player))
        {
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || _items is null) continue;
            _items.TryGet(item.Entry, out ItemTemplate? template);
            if (template?.BagFamily == 9) return true;
        }
        return false;
    }

    private IEnumerable<ulong> EnumeratePlayerInventoryGuids(WorldEntity player)
    {
        for (int i = 0; i < 23; i++) yield return player.Fields.PlayerInventorySlot(i);
        for (int i = 0; i < 16; i++) yield return player.Fields.PlayerBackpackSlot(i);
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            for (int slot = 0; slot < bag.Fields.ContainerNumSlots; slot++) yield return bag.Fields.ContainerSlot(slot);
        }
    }

    private void PutCarriedItemInKeyring(WorldEntity player)
    {
        bool[] occupied = Enumerable.Range(0, InventoryUiLaw.KeyringAddressableSlots)
            .Select(i => player.Fields.PlayerKeyringSlot(i) != 0).ToArray();
        int slot = InventoryUiLaw.FirstEmptyKeyringSlot(player.Level, occupied);
        if (slot >= 0) { PickupOrPlaceItem(InventoryUiLaw.KeyringContainer, slot, 0, true); return; }
        ShowUiError("Your keyring is full.");
    }

    private void LayoutBagWindows(WorldEntity player)
    {
        int[] visible = [.. _bagWindowOrder.Where(IsBagWindowOpen)];
        foreach (int container in new[] { 0, 1, 2, 3, 4, InventoryUiLaw.KeyringContainer })
            if (IsBagWindowOpen(container) && !visible.Contains(container)) visible = [.. visible, container];
        for (int container = InventoryUiLaw.BankBagContainerFirst;
             container <= InventoryUiLaw.BankBagContainerLast; container++)
            if (IsBagWindowOpen(container) && !visible.Contains(container))
                visible = [.. visible, container];
        _bagWindowOrder.Clear(); _bagWindowOrder.AddRange(visible);
        var windows = new List<InventoryUiLaw.StackWindow>();
        foreach (int container in visible)
        {
            float height = container switch
            {
                0 => 240f,
                InventoryUiLaw.KeyringContainer => InventoryUiLaw.Background(
                    InventoryUiLaw.KeyringSize(player.Level)).Height,
                _ => BagContainerHeight(player, container),
            };
            if (height > 0) windows.Add(new(container, height));
        }
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        _bagWindowPositions.Clear();
        foreach (InventoryUiLaw.StackPlacement placement in
                 InventoryUiLaw.LayoutStack(display.Y / scale, windows))
            _bagWindowPositions[placement.Container] = new(
                display.X - (placement.RightOffset + InventoryUiLaw.ContainerWidth) * scale,
                display.Y - (placement.BottomOffset + placement.Height) * scale);
    }

    private float BagContainerHeight(WorldEntity player, int container)
    {
        ulong guid = container switch
        {
            >= 1 and <= 4 => player.Fields.PlayerInventorySlot(18 + container),
            >= InventoryUiLaw.BankBagContainerFirst and <= InventoryUiLaw.BankBagContainerLast =>
                player.Fields.PlayerBankBagSlot(container - InventoryUiLaw.BankBagContainerFirst),
            _ => 0,
        };
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag)) return 0;
        return InventoryUiLaw.Background((int)Math.Clamp(bag.Fields.ContainerNumSlots, 1,
            InventoryUiLaw.MaxContainerSlots)).Height;
    }

    private bool IsBagWindowOpen(int container) => container switch
    {
        0 => _backpackOpen,
        InventoryUiLaw.KeyringContainer => _keyringOpen,
        >= 1 and <= 4 => _equippedBagOpen[container - 1],
        >= InventoryUiLaw.BankBagContainerFirst and <= InventoryUiLaw.BankBagContainerLast =>
            _bankBagOpen[container - InventoryUiLaw.BankBagContainerFirst],
        _ => false,
    };

    private bool SetBagWindowOpen(int container, bool open, bool playSound = true)
    {
        bool was = IsBagWindowOpen(container);
        if (was == open) return false;
        switch (container)
        {
            case 0: _backpackOpen = open; break;
            case InventoryUiLaw.KeyringContainer: _keyringOpen = open; break;
            case >= 1 and <= 4: _equippedBagOpen[container - 1] = open; break;
            case >= InventoryUiLaw.BankBagContainerFirst and <= InventoryUiLaw.BankBagContainerLast:
                _bankBagOpen[container - InventoryUiLaw.BankBagContainerFirst] = open;
                break;
            default: return false;
        }
        _bagWindowOrder.Remove(container);
        if (open) _bagWindowOrder.Add(container);
        if (playSound)
            PlayBagSound(container == InventoryUiLaw.KeyringContainer
                ? open ? "KeyRingOpen" : "KeyRingClose"
                : open ? "igBackPackOpen" : "igBackPackClose");
        return true;
    }

    private void PlayBagSound(string name)
        => PlayUiSound(name, "ui.inventory");

    private static readonly bool SuppressUiAudioForDiagnostics =
        Environment.GetEnvironmentVariable("MSUI_UI_AUDIO_OFF") == "1";
    private bool _uiAudioSuppressionAnnounced;

    private void PlayUiSound(string name, string category = "ui")
    {
        if (SuppressUiAudioForDiagnostics)
        {
            if (!_uiAudioSuppressionAnnounced)
            {
                _uiAudioSuppressionAnnounced = true;
                Console.WriteLine("[audio] MSUI_UI_AUDIO_OFF=1 - interface cues suppressed");
            }
            return;
        }
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        _spellSounds?.Play(name, ControlledGuid, listener, listener, category);
    }

    private void ToggleBackpack() => SetBagWindowOpen(0, !_backpackOpen);

    private bool SetAllNormalBagWindows(WorldEntity player, bool open)
    {
        bool changed = SetBagWindowOpen(0, open, playSound: false);
        for (int container = 1; container <= 4; container++)
        {
            bool exists = player.Fields.PlayerInventorySlot(18 + container) != 0;
            if (open && !exists) continue;
            changed |= SetBagWindowOpen(container, open, playSound: false);
        }
        if (changed) PlayBagSound(open ? "igBackPackOpen" : "igBackPackClose");
        return changed;
    }

    private bool ToggleAllBags()
    {
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        bool open = InventoryUiLaw.ShouldOpenAllBags(_backpackOpen, _equippedBagOpen);
        return SetAllNormalBagWindows(player, open);
    }

    /// <summary>
    /// The dedicated all-bags key. Same windows as <see cref="ToggleAllBags"/>, different
    /// open/close decision — it opens the rest instead of closing what is already up.
    /// </summary>
    private bool ToggleEveryCarriedBag()
    {
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        // Slot 18+container is the equipped-bag slot, matching SetAllNormalBagWindows' own test.
        var carried = new bool[4];
        for (int container = 1; container <= 4; container++)
            carried[container - 1] = player.Fields.PlayerInventorySlot(18 + container) != 0;
        bool open = InventoryUiLaw.ShouldOpenEveryCarriedBag(
            _backpackOpen, _equippedBagOpen, carried);
        return SetAllNormalBagWindows(player, open);
    }

    private bool CloseAllBagWindows()
    {
        if (!_backpackOpen && !_keyringOpen && !_equippedBagOpen.Any(x => x) &&
            !_bankBagOpen.Any(x => x)) return false;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
            SetAllNormalBagWindows(player, false);
        else
        {
            SetBagWindowOpen(0, false, playSound: false);
            for (int container = 1; container <= 4; container++)
                SetBagWindowOpen(container, false, playSound: false);
        }
        SetBagWindowOpen(InventoryUiLaw.KeyringContainer, false);
        for (int container = InventoryUiLaw.BankBagContainerFirst;
             container <= InventoryUiLaw.BankBagContainerLast; container++)
            SetBagWindowOpen(container, false, playSound: false);
        return true;
    }

    /// <summary>Close ordinary/bank bag windows while preserving the keyring.</summary>
    private bool CloseAllNormalBagWindows()
    {
        if (!_backpackOpen && !_equippedBagOpen.Any(x => x) && !_bankBagOpen.Any(x => x))
            return false;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
            SetAllNormalBagWindows(player, false);
        else
        {
            SetBagWindowOpen(0, false, playSound: false);
            for (int container = 1; container <= 4; container++)
                SetBagWindowOpen(container, false, playSound: false);
        }
        for (int container = InventoryUiLaw.BankBagContainerFirst;
             container <= InventoryUiLaw.BankBagContainerLast; container++)
            SetBagWindowOpen(container, false, playSound: false);
        return true;
    }

    private void TriggerItemPushAnimation(byte wireBag, uint wireSlot, uint entry)
    {
        int container = InventoryUiLaw.PushContainer(wireBag, wireSlot);
        if (container is not (0 or 1 or 2 or 3 or 4 or InventoryUiLaw.KeyringContainer)) return;
        _itemPushContainer = container;
        _itemPushEntry = entry;
        _itemPushStartedAt = NowSeconds();
    }

    private void DrawItemPushAnimation()
    {
        if (_itemPushContainer == InventoryUiLaw.EmptyContainer || _gameplayArt is null || _items is null ||
            !_bagButtonPositions.TryGetValue(_itemPushContainer, out Vector2 buttonMin)) return;
        float elapsed = (float)(NowSeconds() - _itemPushStartedAt);
        InventoryUiLaw.ItemPushSample sample = InventoryUiLaw.SampleItemPush(elapsed);
        if (!sample.Visible) { _itemPushContainer = InventoryUiLaw.EmptyContainer; return; }
        if (!_items.TryGet(_itemPushEntry, out ItemTemplate? item) || item is null) return;
        uint icon = _gameplayArt.Handle(item.IconPath);
        if (icon == 0) return;
        float scale = GameplayUiScale();
        Vector2 center = buttonMin + new Vector2(18f) * scale + sample.Offset * scale;
        Vector2 half = new(sample.Size * .5f * scale);
        uint alpha = (uint)Math.Clamp((int)(sample.Alpha * 255f), 0, 255);
        uint tint = (alpha << 24) | 0x00ffffff;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, center - half, center + half,
            Vector2.Zero, Vector2.One, tint);
    }

    private void DrawKeyringWindow(WorldEntity player)
    {
        if (!_keyringOpen || _gameplayArt is null || _items is null || _net is null ||
            !_bagWindowPositions.TryGetValue(InventoryUiLaw.KeyringContainer, out Vector2 p)) return;
        float s = GameplayUiScale();
        int slots = InventoryUiLaw.KeyringSize(player.Level);
        InventoryUiLaw.BackgroundGeometry geometry = InventoryUiLaw.Background(slots);
        float height = geometry.Height;
        Vector2 artMin = p - new Vector2(64f, 0) * s;
        ImGui.SetNextWindowPos(artMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256f, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##keyring-window", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        // As with equipped bags, the keyring portrait sits below the frame artwork so its
        // square source texture is clipped by the authored circular opening.
        uint portrait = _gameplayArt.Handle(@"Interface\ContainerFrame\KeyRing-Bag-Icon");
        if (portrait != 0) dl.AddImage((nint)portrait, p + new Vector2(7, 5) * s,
            p + new Vector2(47, 45) * s);
        uint bg = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-Bag-Components-Keyring");
        if (bg != 0)
        {
            dl.AddImage((nint)bg, artMin, artMin + new Vector2(256f, geometry.TopHeight) * s,
                new Vector2(0, geometry.TopUvY.X), new Vector2(1, geometry.TopUvY.Y));
            if (geometry.MiddleHeight > 0)
                dl.AddImage((nint)bg, artMin + new Vector2(0, geometry.TopHeight) * s,
                    artMin + new Vector2(256f, geometry.TopHeight + geometry.MiddleHeight) * s,
                    new Vector2(0, geometry.MiddleUvY.X), new Vector2(1, geometry.MiddleUvY.Y));
            dl.AddImage((nint)bg, artMin + new Vector2(0, height - 10f) * s,
                artMin + new Vector2(256f, height) * s,
                new Vector2(0, geometry.BottomUvY.X), new Vector2(1, geometry.BottomUvY.Y));
        }
        GameText.Draw(dl, "GameFontNormal", "Keyring", p + new Vector2(47, 10) * s, s);
        for (int slot = 0; slot < slots; slot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(slots, slot, height, false);
            DrawInventorySlot(dl, player, InventoryUiLaw.KeyringContainer, slot,
                p + new Vector2(cell.X, cell.Y) * s, s, $"keyring-{slot}");
        }
        Vector2 closeMin = p + new Vector2(160, 1) * s;
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * s);
        ImGui.SetCursorScreenPos(closeMin); ImGui.InvisibleButton("##keyring-close", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(InventoryUiLaw.KeyringContainer, false);
        ImGui.End();
    }

    private void DrawMoney(ImDrawListPtr dl, Vector2 frameMin, uint copper, float scale)
    {
        uint icons = _gameplayArt?.Handle(@"Interface\MoneyFrame\UI-MoneyIcons.blp") ?? 0;
        float right = frameMin.X + 177f * scale;
        float top = frameMin.Y + 216f * scale;
        foreach (InventoryUiLaw.MoneyDenomination denomination in InventoryUiLaw.Money(copper))
        {
            string text = denomination.Value.ToString();
            float numberWidth = GameText.MeasureWidth("NumberFontNormal", text, scale);
            Vector2 iconMin = new(right - 13f * scale, top);
            if (icons != 0)
            {
                float uvLeft = denomination.Index * .25f;
                dl.AddImage((nint)icons, iconMin, iconMin + new Vector2(13f) * scale,
                    new Vector2(uvLeft, 0), new Vector2(uvLeft + .25f, 1));
            }
            float textTop = GameText.BoxCenteredTop("NumberFontNormal", top, 13f, scale);
            GameText.DrawRightAligned(dl, "NumberFontNormal", text,
                new Vector2(iconMin.X, textTop), scale);
            right = iconMin.X - numberWidth - 4f * scale;
        }
    }
}
