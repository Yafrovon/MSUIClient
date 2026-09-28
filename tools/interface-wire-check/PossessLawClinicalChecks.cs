using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

/// <summary>
/// POSSESS_LAW (shared_docs/POSSESS_LAW.md), client half. The rules that were broken
/// and re-broken on 2026-09-03 are asserted against the SOURCE so they cannot drift back:
/// interaction gates range from the driven body, purses read the driven body, every
/// mirrored server frame is unwrapped, and a control change resets the body-scoped UI.
/// The Core half is tools/possess-law-check.sh (run over ssh against ~/vmangos).
/// </summary>
internal static class PossessLawClinicalChecks
{
    /// <summary>Client files whose gates act for the driven body (rule 2.1). None of them may
    /// range anything from the session body.</summary>
    private static readonly string[] DrivenBodyGateFiles =
    [
        "GameLoop/Panels/GameLoop.Loot.cs",
        "GameLoop/Dev/GameLoop.LiveRaidLoot.cs",
        "GameLoop/Panels/GameLoop.EquipBinding.cs",
        "GameLoop/Panels/GameLoop.Bank.cs",
        "GameLoop/Panels/GameLoop.Taxi.cs",
        "GameLoop/Panels/GameLoop.Mail.cs",
        "GameLoop/Panels/GameLoop.Vendor.Session.cs",
        "GameLoop/Panels/GameLoop.Trainer.cs",
        "GameLoop/Panels/GameLoop.Gossip.cs",
        "GameLoop/Panels/GameLoop.Quest.cs",
        "GameLoop/Panels/GameLoop.Auction.cs",
        "GameLoop/Scene/GameLoop.GameObjects.cs",
        "GameLoop/Scene/GameLoop.Instances.cs",
        "GameLoop/Hud/GameLoop.WorldCursor.cs",
    ];

    /// <summary>Panels whose purse/bags belong to the driven body (rule 2.2).</summary>
    private static readonly string[] DrivenPurseFiles =
    [
        "GameLoop/Panels/GameLoop.Loot.cs",
        "GameLoop/Dev/GameLoop.LiveRaidLoot.cs",
        "GameLoop/Panels/GameLoop.Bank.cs",
        "GameLoop/Panels/GameLoop.Trainer.cs",
        "GameLoop/Panels/GameLoop.Mail.cs",
        "GameLoop/Panels/GameLoop.Taxi.cs",
        "GameLoop/Panels/GameLoop.Auction.cs",
        "GameLoop/Panels/GameLoop.Vendor.Session.cs",
    ];

    /// <summary>The server's MirrorOwnerPacket whitelist as of 2026-09-03 (rule 1.2). Each
    /// must be unwrapped in ApplySuiProxy. When the Core whitelist grows, grow this list —
    /// the box check asserts the same set from the other side.</summary>
    public static readonly string[] MirroredOpcodes =
    [
        "SMSG_ACTION_BUTTONS", "SMSG_INITIAL_SPELLS", "SMSG_LEARNED_SPELL", "SMSG_SUPERCEDED_SPELL",
        "SMSG_REMOVED_SPELL", "SMSG_SPELL_COOLDOWN", "SMSG_COOLDOWN_EVENT", "SMSG_CLEAR_COOLDOWN",
        "SMSG_CAST_RESULT", "SMSG_STANDSTATE_UPDATE",
        "SMSG_GOSSIP_MESSAGE", "SMSG_GOSSIP_COMPLETE", "SMSG_QUESTGIVER_STATUS",
        "SMSG_QUESTGIVER_QUEST_LIST", "SMSG_QUESTGIVER_QUEST_DETAILS", "SMSG_QUESTGIVER_REQUEST_ITEMS",
        "SMSG_QUESTGIVER_OFFER_REWARD", "SMSG_QUESTGIVER_QUEST_INVALID", "SMSG_QUESTGIVER_QUEST_COMPLETE",
        "SMSG_LIST_INVENTORY", "SMSG_SELL_ITEM", "SMSG_BUY_ITEM", "SMSG_BUY_FAILED",
        "SMSG_TRAINER_LIST", "SMSG_TRAINER_BUY_SUCCEEDED", "SMSG_TRAINER_BUY_FAILED",
        "SMSG_LOOT_RESPONSE", "SMSG_LOOT_RELEASE_RESPONSE", "SMSG_LOOT_REMOVED", "SMSG_LOOT_CLEAR_MONEY",
        "SMSG_LOOT_MONEY_NOTIFY", "SMSG_ITEM_PUSH_RESULT",
        "SMSG_PET_SPELLS", "SMSG_PET_MODE", "SMSG_PET_ACTION_FEEDBACK", "SMSG_PET_CAST_FAILED",
        "SMSG_ACTIVATETAXIREPLY", "SMSG_SHOWTAXINODES", "SMSG_TAXINODE_STATUS", "SMSG_NEW_TAXI_PATH",
        "MSG_MOVE_TELEPORT_ACK",
        "SMSG_SHOW_BANK", "MSG_LIST_STABLED_PETS", "MSG_TALENT_WIPE_CONFIRM", "SMSG_BINDER_CONFIRM",
        "SMSG_PLAYERBOUND", "MSG_AUCTION_HELLO",
        "SMSG_TRADE_STATUS", "SMSG_TRADE_STATUS_EXTENDED",
    ];

    /// <summary>Whitelisted mail notices also arrive on the direct dispatch;
    /// their proxy copy is inert by design.</summary>
    private static readonly string[] MirroredButInert =
        ["SMSG_RECEIVED_MAIL", "MSG_QUERY_NEXT_MAIL_TIME"];

    public static void Run()
    {
        Check(ObjectFields.StoredAttackTimeMilliseconds(BitConverter.SingleToUInt32Bits(1900f)) == 1900 &&
              ObjectFields.StoredAttackTimeMilliseconds(BitConverter.SingleToUInt32Bits(2000f)) == 2000 &&
              ObjectFields.StoredAttackTimeMilliseconds(BitConverter.SingleToUInt32Bits(1500.75f)) == 1500 &&
              ObjectFields.StoredAttackTimeMilliseconds(BitConverter.SingleToUInt32Bits(-1f)) == 0 &&
              ObjectFields.StoredAttackTimeMilliseconds(BitConverter.SingleToUInt32Bits(float.NaN)) == 0,
            "raw Core snapshot attack-time floats must become ordinary uint milliseconds");
        string snapshotSource = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient/GameLoop/Scene/GameLoop.Control.cs"));
        Check(snapshotSource.Split("ObjectFields.StoredAttackTimeMilliseconds(r.ReadU32())",
                  StringSplitOptions.None).Length == 4,
            "all three snapshot attack clocks must use the raw-float conversion");
        string root = ClientConfig.FindRepoRoot();
        string Read(string rel) => SourceText.Read(Path.Combine(root, "MSUIClient", rel.Replace('/', Path.DirectorySeparatorChar)));

        string stand = Read("GameLoop/Panels/GameLoop.Chat.cs").Split("private bool TrySetLocalStandState", StringSplitOptions.None)[1].Split("private string ResolveChannelSelector", StringSplitOptions.None)[0];
        Check(stand.Contains("_entities.TryGet(ControlledGuid", StringComparison.Ordinal) && !stand.Contains("ControlledGuid != LocalPlayerGuid", StringComparison.Ordinal), "POSSESS_LAW: stand commands act on the controlled body");
        Check(Read("GameLoop/Scene/GameLoop.Control.cs").Contains("ApplyStandStateUpdate(inner, source)", StringComparison.Ordinal), "POSSESS_LAW: stand reply applies to the mirrored actor");
        string hearthSource = Read("GameLoop/Panels/GameLoop.Hearth.cs");
        Check(hearthSource.Contains("owner == 0 || owner != LocalPlayerGuid || _bindPointAreaId == 0", StringComparison.Ordinal) &&
              Read("GameLoop/Panels/GameLoop.Inventory.cs").Contains("homeAreaName: HearthAreaName(requirementOwner)", StringComparison.Ordinal) &&
              Read("GameLoop/Combat/GameLoop.SpellModifiers.cs").Contains("homeAreaName: HearthAreaName(actor)", StringComparison.Ordinal),
            "a companion's hearth tooltip must never borrow the session character's home");

        InventoryUiLaw.DragPress dragPress = new(0x221, -100, 7, 0x079C);
        Check(InventoryUiLaw.SameDragSource(dragPress, 0x221, -100, 7, 0x079C) &&
              !InventoryUiLaw.SameDragSource(dragPress, 0x223, -100, 7, 0x0686) &&
              !InventoryUiLaw.SameDragSource(dragPress, 0x221, -100, 7, 0x079D) &&
              !InventoryUiLaw.SameDragSource(dragPress, 0x221, 0, 7, 0x079C) &&
              !InventoryUiLaw.SameDragSource(null, 0x221, -100, 7, 0x079C),
            "a held inventory drag must retain its pressed actor, slot and item until cancelled");
        string dragSource = Read("GameLoop/Panels/GameLoop.Inventory.cs");
        int clearDrag = dragSource.IndexOf("private void ClearCarriedItem()", StringComparison.Ordinal);
        int afterClearDrag = dragSource.IndexOf("private void ClearCarriedItemOnEscape()", clearDrag, StringComparison.Ordinal);
        Check(dragSource[clearDrag..afterClearDrag].Contains("_inventoryDragPress = null", StringComparison.Ordinal) &&
              dragSource.Contains("InventoryDragSourcePressed(container, slot, guid) &&", StringComparison.Ordinal) &&
              dragSource.Contains("InventoryDragSourcePressed(InventoryUiLaw.EquipmentContainer, equipmentSlot, guid) &&", StringComparison.Ordinal),
            "cursor reset and both native inventory drag sources must enforce the retained press");

        // ── 2.1 gates range from the driven body ─────────────────────────────
        foreach (string rel in DrivenBodyGateFiles)
            Check(!Read(rel).Contains("TryGetSessionBodyPose(", StringComparison.Ordinal),
                $"POSSESS_LAW 2.1: {rel} ranges from the SESSION body; use TryGetInteractionBodyPose");

        // ── 2.2 purses read the driven body ──────────────────────────────────
        foreach (string rel in DrivenPurseFiles)
            Check(!Read(rel).Contains("_net.PlayerGuid, out WorldEntity", StringComparison.Ordinal) &&
                  !Read(rel).Contains("_net!.PlayerGuid, out WorldEntity", StringComparison.Ordinal),
                $"POSSESS_LAW 2.2: {rel} reads the SESSION player's purse/bags; use ControlledGuid");

        // ── 1.2 every mirrored frame is unwrapped ────────────────────────────
        string raidLoot = Read("GameLoop/Dev/GameLoop.LiveRaidLoot.cs");
        Check(raidLoot.Contains("_partyMasterLooterGuid != ControlledGuid", StringComparison.Ordinal) &&
              raidLoot.Contains("TryGetInteractionBodyPose(out var body)", StringComparison.Ordinal) &&
              raidLoot.Contains("EnumerateActionItemCopies(actor, entry)", StringComparison.Ordinal) &&
              raidLoot.Contains("!CanAuthorControlledOrSelf", StringComparison.Ordinal),
            "POSSESS_LAW 2.1/2.2: raid loot authority, range and equipment must use the actual driven body");

        string control = Read("GameLoop/Scene/GameLoop.Control.cs");
        string objectUse = Read("GameLoop/Scene/GameLoop.GameObjects.cs");
        Check(objectUse.Contains("go.GameObjectType == 17\n        ? 100f", StringComparison.Ordinal) &&
              objectUse.Contains("interactDistance = GameObjectUseDistance(go);", StringComparison.Ordinal) &&
              control.Contains("float limit = GameObjectUseDistance(subject);", StringComparison.Ordinal),
            "2.1 Fishing use and Command View arrival must share the distant bobber range");
        string questRewards = Read("GameLoop/Panels/GameLoop.Quest.cs");
        int objectGateStart = questRewards.IndexOf("else if (_entities.TryGet(guid, out WorldEntity go)", StringComparison.Ordinal);
        int objectGateEnd = questRewards.IndexOf("else if (TryGetInteractionBodyPose", objectGateStart, StringComparison.Ordinal);
        Check(objectGateStart >= 0 && objectGateEnd > objectGateStart &&
              questRewards[objectGateStart..objectGateEnd].Contains("go.IsGameObject && go.GameObjectType == 2", StringComparison.Ordinal) &&
              questRewards[objectGateStart..objectGateEnd].Contains("TryGetInteractionBodyPose(out WorldBodyPose objectActor)", StringComparison.Ordinal) &&
              questRewards[objectGateStart..objectGateEnd].Contains("!CanAuthorControlledGameplay", StringComparison.Ordinal) &&
              questRewards[objectGateStart..objectGateEnd].Contains("distance <= GameObjectInteractDistance", StringComparison.Ordinal) &&
              questRewards.Contains("npc.IsGameObject ? GameObjectInteractDistance * GameObjectInteractDistance", StringComparison.Ordinal),
            "2.1 Object quest givers must gate and leash at the driven body's object interaction range");
        int rewardStart = questRewards.IndexOf("private uint QuestNpcRewardMoney(", StringComparison.Ordinal);
        int rewardEnd = questRewards.IndexOf("private float DrawQuestRewardSet(", rewardStart, StringComparison.Ordinal);
        Check(rewardStart >= 0 && rewardEnd > rewardStart &&
              questRewards[rewardStart..rewardEnd].Contains("TryGet(ControlledGuid, out WorldEntity actor)", StringComparison.Ordinal) &&
              questRewards[rewardStart..rewardEnd].Contains("actor.Level", StringComparison.Ordinal),
            "2.2 NPC quest max-level reward bonus must follow the driven body");
        string commandShelf = Read("GameLoop/Hud/GameLoop.CommandShelf.cs");
        int proxyStart = control.IndexOf("private void ApplySuiProxy(byte[] body)", StringComparison.Ordinal);
        Check(proxyStart >= 0, "ApplySuiProxy is gone");
        string proxy = control[proxyStart..];
        proxy = proxy[..Math.Max(proxy.IndexOf("\n    }\n", StringComparison.Ordinal), 0)];
        foreach (string op in MirroredOpcodes)
            Check(proxy.Contains($"case Op.{op}:", StringComparison.Ordinal),
                $"POSSESS_LAW 1.2: the server mirrors {op} but ApplySuiProxy does not unwrap it — the reply lands nowhere");
        foreach (string op in MirroredButInert)
            Check(!proxy.Contains($"case Op.{op}:", StringComparison.Ordinal),
                $"{op} is documented as inert on the proxy; unwrapping it would double-apply the direct copy");

        // ── 2.3 a control change resets every body-scoped session UI ─────────
        string pet = Read("GameLoop/Panels/GameLoop.Pet.cs");
        int resetStart = pet.IndexOf("private void ResetBodySessionUiOnControlChange()", StringComparison.Ordinal);
        Check(resetStart >= 0, "ResetBodySessionUiOnControlChange is gone");
        int resetEnd = pet.IndexOf("\n    private void StopPetAttackForOldTargetChange",
            resetStart, StringComparison.Ordinal);
        Check(resetEnd > resetStart, "ResetBodySessionUiOnControlChange boundary is gone");
        string reset = pet[resetStart..resetEnd];
        Check(reset.Contains("CancelGroundTargeting();", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: ground item intent must clear on both body-control acknowledgements");
        string itemTargeting = Read("GameLoop/Panels/GameLoop.Inventory.Targeting.cs");
        Check(itemTargeting.Contains("ControlledGuid != intent.Actor", StringComparison.Ordinal) &&
              itemTargeting.Contains("copy.Item.Guid != intent.Item", StringComparison.Ordinal) &&
              itemTargeting.Contains("_groundItemUse = null;", StringComparison.Ordinal) &&
              itemTargeting.Contains("!CanAuthorControlledOrSelf", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: ground-item consumption must retain actor/item identity and clear cancelled intent");
        string groundClicks = Read("GameLoop/Combat/GameLoop.Targeting.cs");
        int groundCursor = groundClicks.IndexOf("if (_groundCastSpell != 0)", StringComparison.Ordinal);
        int commandClicks = groundClicks.IndexOf("HandleFreeCamWorldClick(click, pressPick)", StringComparison.Ordinal);
        Check(groundCursor >= 0 && commandClicks > groundCursor &&
              groundClicks[groundCursor..commandClicks].Contains("CommitGroundCast(armed, latchedGround)", StringComparison.Ordinal) &&
              groundClicks[groundCursor..commandClicks].Contains("CancelGroundTargeting();", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: armed ground gestures must commit/cancel before Command View orders");
        string groundFeedback = Read("GameLoop/Combat/GameLoop.GroundFx.cs");
        Check(groundFeedback.Contains("TryGetWorldBodyPose(actor, out WorldBodyPose pose)", StringComparison.Ordinal) &&
              groundFeedback.Contains("GroundPointAcceptable(spell.Id, ControlledGuid, point)", StringComparison.Ordinal) &&
              groundFeedback.Contains("ActorSpellRange(spell, actor, raw)", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: ground hover range must use the actor's physical pose and range modifiers");
        string itemUse = Read("GameLoop/Panels/GameLoop.Inventory.cs");
        Check(itemUse.Contains("PlayerActions itemActions = ActionsFor(ControlledGuid);", StringComparison.Ordinal) &&
              itemUse.Contains("itemActions.IsItemOnCooldown(template.Entry, useSpell, spell, now)", StringComparison.Ordinal) &&
              itemUse.Contains("itemActions.StartItemUseCooldown(", StringComparison.Ordinal) &&
              itemUse.Contains("ActionsFor(ControlledGuid).TryCooldownDisplay(item.UseSpellId", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: bag cooldowns must stay with their actor, not inspected bars");
        foreach (string call in new[] { "ClearCarriedItem();", "CancelStackSplit();", "ClearActionBarCursorOnEscape();", "ResetMail();", "ResetPetActionBar();", "ClearLootOnControlChange();", "CloseBankSession(playSound: false);", "CloseTaxiMap(playSound: false);", "DiscardServerRideWithoutAck();" })
            Check(reset.Contains(call, StringComparison.Ordinal), $"POSSESS_LAW 2.3: control change no longer does {call}");
        int grantCalls = control.Split("ResetBodySessionUiOnControlChange();").Length - 1;
        Check(grantCalls >= 2, "POSSESS_LAW 2.3: the reset must run on BOTH the grant and the release ack");

        // ── 3.1 the driven bot's ride may own the controller ─────────────────
        string taxi = Read("GameLoop/Panels/GameLoop.Taxi.cs");
        Check(taxi.Contains("possessingEmbodiedBot: _controlState == ControlState.Possessing", StringComparison.Ordinal) &&
              taxi.Contains("move.Guid != ControlledGuid", StringComparison.Ordinal),
            "POSSESS_LAW 3.1: a possessed bot's flight must drive the controller");

        // ── 3.2 the driven bot's near teleport is adopted by the controller ───
        string net = Read("GameLoop/Scene/GameLoop.Net.cs");
        Check(net.Contains("private void ApplyMoveTeleportAck(NetworkClient net, byte[] body)", StringComparison.Ordinal) &&
              net.Contains("moverGuid != ControlledGuid))", StringComparison.Ordinal),
            "POSSESS_LAW 3.2: the same-map teleport handler must accept the controlled guid as mover");

        // ── 3.4 area triggers fire for the driven body ───────────────────────
        string instances = Read("GameLoop/Scene/GameLoop.Instances.cs");
        Check(instances.Contains("_areaTriggers is null || !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", StringComparison.Ordinal),
            "POSSESS_LAW 3.4: area triggers must be scanned at the interaction body");

        // ── 5.1 / 5.2 / 5.3 Command View shape ───────────────────────────────
        Check(control.Contains("BeginCommandViewInteraction(CommandViewInteractKind.Choose, picked, offerNpc);", StringComparison.Ordinal) &&
              !control.Contains("RaiseCommandViewNpcChoice(picked, offers);", StringComparison.Ordinal),
            "POSSESS_LAW 5.1: the NPC chooser must be raised on ARRIVAL, never on click");
        Check(control.Contains("ConfirmPopupUiLaw.NpcOptions(EffectiveNpcFlags(offerNpc), commanderQuests)", StringComparison.Ordinal) &&
              control.Contains("ConfirmPopupUiLaw.NpcOptions(EffectiveNpcFlags(subject), commanderQuests)", StringComparison.Ordinal),
            "POSSESS_LAW 5.2: the chooser must use EffectiveNpcFlags (stale innkeeper bit on bowyers)");
        Check(control.Contains("private void UpdateCommandViewNpcChoiceLifecycle()", StringComparison.Ordinal) &&
              SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs")).Contains("UpdateCommandViewNpcChoiceLifecycle();", StringComparison.Ordinal),
            "POSSESS_LAW 5.3: our dialogs must auto-hide out of range (lifecycle not wired)");
        Check(control.Contains("_ => ControlledGuid,", StringComparison.Ordinal),
            "POSSESS_LAW 2.1: the Command View game-object walker must be the driven body (mailbox/plaque)");
        int primaryCycleStart = commandShelf.IndexOf("private void CycleRtsPrimary(", StringComparison.Ordinal);
        int primaryCycleEnd = commandShelf.IndexOf("private void EnsurePossessingBot(", StringComparison.Ordinal);
        Check(primaryCycleStart >= 0 && primaryCycleEnd > primaryCycleStart,
            "POSSESS_LAW 5.4: the selected-card primary cycle seam is gone");
        string primaryCycle = commandShelf[primaryCycleStart..primaryCycleEnd];
        Check(primaryCycle.Contains("_freecamSelection.Count < 2", StringComparison.Ordinal) &&
              primaryCycle.Contains("_freecamSelection.IndexOf(RtsPrimaryGuid)", StringComparison.Ordinal) &&
              !primaryCycle.Contains("FreeCamSelectableGuids(", StringComparison.Ordinal) &&
              !primaryCycle.Contains("_freecamSelection.Insert(", StringComparison.Ordinal) &&
              !primaryCycle.Contains("CommandViewLocked", StringComparison.Ordinal),
            "POSSESS_LAW 5.4: Q must cycle only the selected command cards, never the local faction roster");

        // ── 2.4 world map: arrow = driven body, dots = the rest ──────────────
        string map = Read("GameLoop/Panels/GameLoop.WorldMap.cs");
        Check(map.Contains("TryGetWorldBodyPose(ControlledGuid, out WorldBodyPose drivenBody)", StringComparison.Ordinal) &&
              map.Contains("private void DrawWorldMapPartyDots(", StringComparison.Ordinal),
            "POSSESS_LAW 2.4: world map arrow/dots drift");

        // ── 7: chain state stays authored, bordered, and paired with WHO ─────
        // These are approved art choices, not interchangeable state-colour hints. The owner
        // selected the silver-square set after rejecting a nearby textured redesign; keeping
        // the approved PNG digest here prevents that exact visual law from drifting again.
        (string FileName, string ResourceName, string ApprovedSha256)[] chainAssets =
        [
            ("party-chain-linked.png", PartyChainBadgeUiLaw.LinkedResource,
                "96D0E6F5B16B74E887514343451F2B3B4E9EEBC86B4B5DC1CB0691004CC2BDA1"),
            ("party-chain-unlinked.png", PartyChainBadgeUiLaw.UnlinkedResource,
                "9B89217BA6D34F311656B831B322AD5BC5E7D3D2E3007789F7970FE32CB4D3FD"),
            ("party-chain-world-hold.png", PartyChainBadgeUiLaw.WorldHoldResource,
                "703588F13B34B923F98BA108C88B85BDF88B1BF095315A6FD193E595F541C8A6"),
        ];
        HashSet<string> embedded = typeof(ClientConfig).Assembly.GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string fileName, string resourceName, string approvedSha256) in chainAssets)
        {
            Check(embedded.Contains(resourceName),
                $"POSSESS_LAW 7: chain badge is not embedded: {resourceName}");
            string assetPath = Path.Combine(root, "MSUIClient", "Assets", "UI", "PartyChain", fileName);
            string actualSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assetPath)));
            Check(actualSha256 == approvedSha256,
                $"POSSESS_LAW 7.5: {fileName} no longer matches the approved silver-square state art");
            using SkiaSharp.SKBitmap? bitmap = SkiaSharp.SKBitmap.Decode(assetPath);
            Check(bitmap is { Width: 64, Height: 64 },
                $"POSSESS_LAW 7: {fileName} must be a decodable 64x64 PNG");
            Check(bitmap.GetPixel(0, 0).Alpha == 0 && bitmap.GetPixel(63, 63).Alpha == 0 &&
                  bitmap.GetPixel(32, 32).Alpha > 0,
                $"POSSESS_LAW 7: {fileName} lost its real transparent border or opaque badge");

            int silverPixels = 0;
            int stateFieldPixels = 0;
            foreach (SkiaSharp.SKColor pixel in bitmap.Pixels)
            {
                if (pixel.Alpha <= 160) continue;
                int brightest = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                int darkest = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                if (darkest > 105 && brightest - darkest < 24)
                    silverPixels++;

                bool isStateField = fileName switch
                {
                    "party-chain-linked.png" =>
                        pixel.Green > pixel.Red + 25 && pixel.Green > pixel.Blue + 15,
                    "party-chain-unlinked.png" =>
                        pixel.Red > pixel.Green + 40 && pixel.Red > pixel.Blue + 35 && pixel.Green < 130,
                    _ => pixel.Red > 150 && pixel.Green > 100 &&
                         pixel.Blue < Math.Min(pixel.Red, pixel.Green) - 60 &&
                         pixel.Red - pixel.Green < 110,
                };
                if (isStateField) stateFieldPixels++;
            }
            Check(silverPixels >= 500,
                $"POSSESS_LAW 7.5: {fileName} must keep the shared silver frame and chain palette");
            Check(stateFieldPixels >= 700,
                $"POSSESS_LAW 7.5: {fileName} lost its green/red/yellow state field");
        }
        Check(PartyChainBadgeUiLaw.ResourceForState(0) == PartyChainBadgeUiLaw.LinkedResource &&
              PartyChainBadgeUiLaw.ResourceForState(1) == PartyChainBadgeUiLaw.UnlinkedResource &&
              PartyChainBadgeUiLaw.ResourceForState(2) == PartyChainBadgeUiLaw.WorldHoldResource,
            "POSSESS_LAW 7: roster chain states no longer select green/red/yellow authored art");

        string botBars = Read("GameLoop/Hud/GameLoop.BotBars.cs");
        int linksStart = botBars.IndexOf("private void DrawPartyChainLinks(", StringComparison.Ordinal);
        int glyphStart = botBars.IndexOf("private void DrawChainGlyph(", StringComparison.Ordinal);
        int medallionStart = botBars.IndexOf("private static void DrawChainAnchorMedallion(", StringComparison.Ordinal);
        Check(linksStart >= 0 && glyphStart > linksStart && medallionStart > glyphStart,
            "POSSESS_LAW 7: party chain badge/anchor rendering seam is gone");
        string links = botBars[linksStart..glyphStart];
        string glyph = botBars[glyphStart..medallionStart];
        Check(glyph.Contains("EmbeddedPngHandle(", StringComparison.Ordinal) &&
              glyph.Contains("PartyChainBadgeUiLaw.ResourceForState(state)", StringComparison.Ordinal) &&
              glyph.Contains("AddImage(", StringComparison.Ordinal) &&
              !glyph.Contains("AddRect(", StringComparison.Ordinal) &&
              !glyph.Contains("AddRectFilled(", StringComparison.Ordinal) &&
              !glyph.Contains("AddLine(", StringComparison.Ordinal) &&
              !glyph.Contains("AddCircle(", StringComparison.Ordinal) &&
              !glyph.Contains("AddCircleFilled(", StringComparison.Ordinal),
            "POSSESS_LAW 7: the bordered bitmap badge regressed to procedural chain geometry");
        Check(links.Contains("DrawChainAnchorMedallion(", StringComparison.Ordinal) &&
              botBars.Contains("anchorName[..1].ToUpperInvariant()", StringComparison.Ordinal),
            "POSSESS_LAW 7.3: the anchor-initial medallion (WHO) must stay beside the chain badge");
        // Owner 2026-09-22: smaller, docked at the bars' right end, drawn on the member frame's own
        // window list so the loot and master-loot windows cover them (never the foreground list).
        Check(links.Contains("new Vector2(122.5f, 25f)", StringComparison.Ordinal) &&
              links.Contains("new Vector2(122.5f, 15.5f)", StringComparison.Ordinal) &&
              !links.Contains("GetForegroundDrawList()", StringComparison.Ordinal) &&
              links.Contains("ImGui.IsWindowHovered()", StringComparison.Ordinal),
            "POSSESS_LAW 7.6: party chain/WHO positions lost their owner-tuned placement");
        Check(!control.Contains("DrawCommandViewChainLines(", StringComparison.Ordinal) &&
              !control.Contains("DrawChainGlyph(draw, pa", StringComparison.Ordinal) &&
              !control.Contains("DrawChainAnchorMedallion(draw, pa", StringComparison.Ordinal),
            "POSSESS_LAW 7.7: chain art or connector lines returned to Command View world models");
        Check(commandShelf.Contains("DrawChainGlyph(dl,", StringComparison.Ordinal) &&
              commandShelf.Contains("DrawChainAnchorMedallion(dl,", StringComparison.Ordinal),
            "POSSESS_LAW 7.7: the small command-card chain indicator must stay");

        // ── 8: Tactical Freeze owner identity is not the driven-body identity ─
        string tactical = Read("GameLoop/Scene/GameLoop.TacticalFreeze.cs");
        string freezeWire = Read("Net/TacticalFreezeWire.cs");
        string poseLaw = Read("World/Units/TacticalFreezePoseLaw.cs");
        string shelf = Read("GameLoop/Hud/GameLoop.CommandShelf.cs");
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(tactical.Contains("view.Active && view.OwnerGuid == LocalPlayerGuid", StringComparison.Ordinal) &&
              tactical.Contains("owned.OwnerGuid != LocalPlayerGuid", StringComparison.Ordinal) &&
              !tactical.Contains("OwnerGuid == ControlledGuid", StringComparison.Ordinal) &&
              !tactical.Contains("OwnerGuid != ControlledGuid", StringComparison.Ordinal),
            "POSSESS_LAW 8.2: Tactical Freeze authority must compare the socket owner to LocalPlayerGuid");
        Check(freezeWire.Contains("anchorBodies != 1", StringComparison.Ordinal) &&
              !freezeWire.Contains("guid != ownerGuid", StringComparison.Ordinal),
            "POSSESS_LAW 8.2: the initiating driven body may differ from the real owner guid");
        Check(control.Contains("PrepareTacticalCommandViewExit()", StringComparison.Ordinal) &&
              tactical.Contains("RequestOwnedTacticalThawForViewExit()", StringComparison.Ordinal) &&
              tactical.Contains("_tacticalFreezePendingDesiredActive", StringComparison.Ordinal) &&
              tactical.Contains("TacticalFreezePoseLaw.ApplyLockSnapshot", StringComparison.Ordinal) &&
              tactical.Contains("ResetTacticalFreezeState()", StringComparison.Ordinal),
            "POSSESS_LAW 8.5: view exit must request thaw while snapshots/session teardown own state");
        Check(program.Contains("controllerTacticalFrozen", StringComparison.Ordinal) &&
              program.Contains("TacticalFreezePoseLaw.IsFrozen(ControlledGuid)", StringComparison.Ordinal),
            "POSSESS_LAW 8.3: another frozen human can still predict movement through the controller");
        int tacticalSpell = shelf.IndexOf("TryQueueTacticalSpell(primary, spellId, explicitTarget)",
            StringComparison.Ordinal);
        int spellHandoff = shelf.IndexOf("BeginControlHandover(primary);", tacticalSpell,
            StringComparison.Ordinal);
        Check(tacticalSpell >= 0 && spellHandoff > tacticalSpell,
            "POSSESS_LAW 8.6: a frozen spell can possession-handoff before explicit queue authorship");
        Check(shelf.Contains("Items cannot be queued during Tactical Freeze.", StringComparison.Ordinal) &&
              shelf.Contains("CancelPendingPrimaryItemUse();", StringComparison.Ordinal),
            "POSSESS_LAW 8.6: item quickslots can still author live-world use while frozen");
        Check(!poseLaw.Contains("using MSUIClient;", StringComparison.Ordinal) &&
              !poseLaw.Contains("GameLoop.", StringComparison.Ordinal),
            "POSSESS_LAW 8.4: lower pose law must not reference the GameLoop layer");

        string petPopup = Read("GameLoop/Hud/GameLoop.UnitPopup.cs");
        Check(petPopup.Contains("pet.Fields.SummonedBy, pet.Fields.CharmedBy, ControlledGuid",
                  StringComparison.Ordinal) &&
              !petPopup.Contains("pet.Fields.SummonedBy == LocalPlayerGuid", StringComparison.Ordinal),
            "POSSESS_LAW 2.3: pet menus must admit the driven body's charms and summons");

        string stable = Read("GameLoop/Scene/GameLoop.Stable.cs");
        string petTraining = Read("GameLoop/Panels/GameLoop.PetTraining.cs");
        Check(petTraining.Contains("pet.Fields.SummonedBy == ControlledGuid", StringComparison.Ordinal),
            "POSSESS_LAW 2.2: pet training requirements must resolve the driven body's pet");
        string stablePanel = Read("GameLoop/Panels/GameLoop.StablePanel.cs");
        Check(stable.Contains("_entities.TryGet(ControlledGuid, out WorldEntity actor)", StringComparison.Ordinal) &&
              stable.Contains("actor.Fields.Coinage >= price", StringComparison.Ordinal) &&
              stable.Contains("CanBuyStableSlot(price)) _net?.BuyStableSlot", StringComparison.Ordinal) &&
              stablePanel.Contains("bool canBuy = CanBuyStableSlot(price);", StringComparison.Ordinal),
            "POSSESS_LAW 2.2: stable prices and purchase gates must use the acting body's purse");
        Check(stable.Contains("TryGetInteractionBodyPose(out WorldBodyPose actor)", StringComparison.Ordinal) &&
              stable.Contains("NpcSessionUiLaw.InRange(Vector3.DistanceSquared(actor.Position, npc.Position))",
                  StringComparison.Ordinal) &&
              stable.Contains("if (!StableSourceInRange(list.NpcGuid)) return;", StringComparison.Ordinal) &&
              stablePanel.Contains("UpdateStableLifecycle();", StringComparison.Ordinal),
            "POSSESS_LAW 2.1/4.4: stable replies and open panels must obey the acting body's range");

        Console.WriteLine("interface-wire-check: PossessLaw PASS");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
