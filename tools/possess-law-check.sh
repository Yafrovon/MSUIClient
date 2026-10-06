#!/usr/bin/env bash
# POSSESS_LAW (shared_docs/POSSESS_LAW.md), Core half. Run from this repo:
#   bash tools/possess-law-check.sh            # over ssh against 192.168.0.2:~/vmangos
#   bash tools/possess-law-check.sh --local ~/vmangos   # on the box itself
# Greps the Core SOURCE for the rules that were broken and re-broken on 2026-09-03.
# Exit 1 on the first violation; prints PASS otherwise. Pair with
#   dotnet run --project tools/interface-wire-check -- --possess-law-only   (client half)
set -u
if [ "${1:-}" = "--local" ]; then
  TREE="${2:-$HOME/vmangos}"; RUN=(bash -c)
  run() { bash -c "cd '$TREE' && $1"; }
else
  run() { ssh 192.168.0.2 "cd ~/vmangos && $1"; }
fi
fail() { echo "POSSESS_LAW FAIL: $1"; exit 1; }
G=src/game

# 1.1 routed handler families act as GetSuiActor(): every Handle*Opcode in these files
#     must resolve its player through GetSuiActor (counted per file: handlers vs actor sites).
for f in Handlers/LootHandler.cpp Handlers/PetHandler.cpp Handlers/TaxiHandler.cpp \
         Handlers/QuestHandler.cpp Handlers/TradeHandler.cpp Handlers/MailHandler.cpp \
         Handlers/AuctionHouseHandler.cpp; do
  n=$(run "grep -c 'GetSuiActor()' $G/$f" | tr -d '\r')
  [ "${n:-0}" -ge 1 ] || fail "1.1 $f has no GetSuiActor() site"
done
for fn in HandleBankerActivateOpcode HandleBuyBankSlotOpcode HandleAutoBankItemOpcode \
          HandleAutoStoreBankItemOpcode HandleAutoStoreBagItemOpcode HandleListStabledPetsOpcode \
          HandleStablePet HandleUnstablePet HandleBuyStableSlot HandleStableSwapPet \
          HandleBinderActivateOpcode HandleGameObjectUseOpcode HandleAreaTriggerOpcode \
          HandleTalentWipeConfirmOpcode HandleActivateTaxiOpcode HandleTaxiQueryAvailableNodes; do
  ok=$(run "awk '/void WorldSession::$fn\\(/{f=1} f&&/GetSuiActor\\(\\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/*.cpp" | tr -d '\r')
  [ "$ok" = "yes" ] || fail "1.1 $fn does not act as GetSuiActor()"
done

# 1.2 every frame the client unwraps is whitelisted, and vice versa (the same list lives in
#     tools/interface-wire-check/PossessLawClinicalChecks.cs).
for op in SMSG_LOOT_RESPONSE SMSG_LOOT_RELEASE_RESPONSE SMSG_LOOT_REMOVED SMSG_LOOT_CLEAR_MONEY \
          SMSG_LOOT_MONEY_NOTIFY SMSG_ITEM_PUSH_RESULT SMSG_PET_SPELLS SMSG_PET_MODE \
          SMSG_PET_ACTION_FEEDBACK SMSG_PET_CAST_FAILED SMSG_ACTIVATETAXIREPLY SMSG_SHOWTAXINODES \
          SMSG_TAXINODE_STATUS SMSG_NEW_TAXI_PATH MSG_MOVE_TELEPORT_ACK SMSG_SHOW_BANK \
          MSG_LIST_STABLED_PETS MSG_TALENT_WIPE_CONFIRM SMSG_BINDER_CONFIRM SMSG_PLAYERBOUND \
          MSG_AUCTION_HELLO SMSG_GOSSIP_MESSAGE SMSG_LIST_INVENTORY SMSG_TRAINER_LIST; do
  ok=$(run "awk '/void MirrorOwnerPacket\\(/{f=1} f&&/case $op:/{print \"yes\"; exit} f&&/default:/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" | tr -d '\r')
  [ "$ok" = "yes" ] || fail "1.2 $op is not in MirrorOwnerPacket's whitelist"
done

# 1.3 every OnGossipSelect reply opcode of a routed family is whitelisted (the switch answers
#     on the bot's session): SendShowBank/SendStablePet/SendTaxiMenu/SendTalentWipeConfirm/
#     SendAuctionHello/SendBindPoint are covered by the opcodes above; assert the calls exist
#     so a renamed helper is noticed.
for helper in SendShowBank SendStablePet SendTaxiMenu SendTalentWipeConfirm SendAuctionHello SendBindPoint SendListInventory SendTrainerList; do
  ok=$(run "grep -c '$helper' $G/Objects/Player.cpp" | tr -d '\r')
  [ "${ok:-0}" -ge 1 ] || fail "1.3 OnGossipSelect no longer calls $helper — re-audit the gossip switch"
done

# 1.4 routed edits re-snapshot
for f in Handlers/LootHandler.cpp Handlers/ItemHandler.cpp Handlers/NPCHandler.cpp Handlers/SkillHandler.cpp; do
  n=$(run "grep -c 'ResnapshotControlled' $G/$f" | tr -d '\r')
  [ "${n:-0}" -ge 1 ] || fail "1.4 $f never re-snapshots after an edit"
done

# 1.6 mail storage and delayed sends stay with the acting body.
for fn in HandleSendMail HandleSendMailCallback HandleMailMarkAsRead HandleMailDelete HandleMailReturnToSender HandleMailTakeItem HandleMailTakeMoney HandleGetMailList HandleMailCreateTextItem HandleQueryNextMailTime; do
  run "awk '/void WorldSession::'$fn'\\(/{f=1} f&&/pActor->GetSession\\(\\)->GetMasterPlayer\\(\\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MailHandler.cpp | grep -q yes" || fail "1.6 $fn reads the session mailbox instead of the acting body's store"
done
run "grep -q 'pActor->GetObjectGuid() != req->senderGuid' $G/Handlers/MailHandler.cpp && grep -q 'CheckMailBox(req->mailboxGuid)' $G/Handlers/MailHandler.cpp" || fail "1.6 delayed mail does not revalidate its sender and mailbox"

run "awk '/void WorldSession::HandleSendMail\\(/{f=1} f&&/IsSessionGameplayFrozen\\(this\\)/{g=1;next} g&&/SendMailResult\\(0, MAIL_SEND, MAIL_ERR_INTERNAL_ERROR\\)/{print \"yes\";exit} g&&/return;/{exit}' $G/Handlers/MailHandler.cpp | grep -q yes" || fail "1.6 frozen send is silently dropped without resolving pending mail"
for op in CMSG_SEND_MAIL CMSG_SUI_CONTROL_RELEASE CMSG_SUI_TACTICAL_FREEZE; do
  run "grep 'DEFINE_HANDLER($op,' $G/Server/Protocol/Opcodes.cpp | grep -q PACKET_PROCESS_WORLD" || fail "1.6 $op does not share the ordered mail/control queue"
done

# 1.7 owner fields and stable services refresh on both control edges.
run "grep -q 'GetPossessor(controlled) == observer' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp && grep -c 'SuiPossess::IsControlledOwnerOf(ToUnit(), target)' $G/Objects/Object.cpp | grep -q '^2$'" || fail "1.7 possessed pet owner fields/health do not check the active pair"
run "grep -q 'RefreshActorVisibility(session, possessor->GetObjectGuid())' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp && grep -q 'RefreshActorVisibility(session, botGuid)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "1.7 grant/release does not refresh actor-dependent visibility"
run "grep -Fq '(fieldFlags[index] & UF_FLAG_OWNER_ONLY) && !(fieldFlags[index] & visibleFlags)' $G/Objects/Object.cpp" || fail "1.7 forced updates can disclose revoked pet owner fields"
run "grep -q 'actor->GetClass() != CLASS_HUNTER' $G/Objects/Object.cpp && ! grep -q 'target->GetClass() != CLASS_HUNTER' $G/Objects/Object.cpp" || fail "1.7 stable visibility uses the session class"
run "awk '/bool WorldSession::CheckStableMaster/{f=1} f&&/pActor->GetNPCIfCanInteractWith/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q yes" || fail "1.7 stable mutations range from the session body"

for fn in HandleStablePet HandleUnstablePet HandleStableSwapPet; do
  run "awk '/void WorldSession::'$fn'\\(/{f=1} f&&/Unsummon|LoadPetFromDB/{print} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q pActor" || fail "1.7 $fn does not pass the acting pet owner"
  run "! awk '/void WorldSession::'$fn'\\(/{f=1} f&&/Unsummon|LoadPetFromDB/{print} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q _player" || fail "1.7 $fn passes the session pet owner"
done

# 3.1 flights never release; the landing does not teleport a driven flyer
run "grep -q 'lastPointReached && !SuiPossess::IsSuiPossessed(this)' $G/Objects/Player.cpp" || fail "3.1 TaxiStepFinished teleports a driven flyer (breaks possession)"
run "grep -q 'IsTaxiFlying' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "3.1 the fleet AI has no in-flight guard"

# 3.2 a near teleport of the possessed bot keeps the pair; the bot AI stands down its ack
run "grep -q 'void OnPlayerTeleport(Player\\* player, bool farTeleport)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "3.2 OnPlayerTeleport lost its near/far split"
run "grep -q 'IsBeingTeleportedNear() && !SuiPossess::IsSuiPossessed(me)' $G/PlayerBots/PlayerBotAI.cpp" || fail "3.2 PlayerBotAI acks near teleports while possessed"

# 3.3 a same-map party member is granted in place
run "grep -q '!visible && !(partyAuthorized && sameMapInstance)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "3.3 TryBegin relocates the main for a same-map party member"

# 4.x the rest of the party stays
run "grep -q 'm_suiLandedHold' $G/SuperUiContent/SuiBots/AiBotAIMain.h" || fail "4.1 the left-behind hold is gone"
run "grep -q 'SuiPossess::OnTaxiLanded(&player)' $G/Movement/WaypointMovementGenerator.cpp" || fail "4.1 landing no longer sets the hold"
run "grep -q 'HoldIfLeftBehind(possessor, bot)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.1 the main is not held on a far grant"
run "grep -q 'HoldIfLeftBehind(bot, possessor)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.1 a released bot is not held when far"
run "grep -q 'SuiStopFollowForHold' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "4.2 a hold does not end the active follow leg"
run "grep -q 'driving) near-teleports: possession kept' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.3 the main's chain catch-up teleport breaks the possession"
run "grep -q 'packet.guid == _player->GetObjectGuid() && _player->IsBeingTeleportedNear()' $G/Handlers/MovementHandler.cpp" || fail "4.3 the main's own near-teleport ack is refused while the mover is the bot"
run "grep -q '(bossChanged || bossPorted) && dist > AIBOT_PARTY_CATCHUP_TELEPORT' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "4.3 a distant anchor port must set world hold"
run "awk '/if \\(pBoss->GetMap\\(\\) != me->GetMap\\(\\)\\)/{f=1} f&&/TeleportTo/{exit 1} f&&/SuiStopFollowForHold\\(\\)/{print \"yes\"; exit}' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp | grep -q yes" || fail "4.3 crossing a map/instance must end follow, not summon the chain"
run "grep -q 'm_suiBossFlewAway' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "4.3 a boss that flew away is not turned into a hold"

# 4.4 party flight wire
run "grep -q 'CMSG_SUI_PARTY_TAXI *= 868' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "4.4 party-flight opcode drifted from 868"
run "grep -q 'CAPABILITY_PARTY_TAXI_V1' $G/SuperUiContent/SuiWorld/Bridge/SuiPortal.cpp" || fail "4.4 party-flight capability bit is not advertised"

# 5.x Command View tactical freeze. This is intentionally clinical: the lock
#     owner is the real socket, while its fixed center/bit-3 anchor is the body
#     currently driven under POSSESS_LAW 1.  Do not collapse those identities.
TF=$G/SuperUiContent/SuiWorld/CRPG/SuiTacticalFreeze.cpp
TH=$G/SuperUiContent/SuiWorld/CRPG/SuiTacticalFreeze.h
run "grep -q 'CMSG_SUI_TACTICAL_FREEZE *= 870' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 tactical-freeze opcode drifted from 870"
run "grep -q 'SMSG_SUI_TACTICAL_QUEUE *= 873' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 tactical-queue opcode drifted from 873"
# The existing commander-raid request/reply occupy 874/875 after the freeze family.
run "grep -q 'CMSG_SUI_COMMANDER_RAID *= 874' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 commander-raid request boundary drifted"
run "grep -q 'SMSG_SUI_COMMANDER_RAID *= 875' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 commander-raid reply boundary drifted"
run "grep -q 'CMSG_SUI_THREAT *= 876' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 threat-meter request drifted from 876"
run "grep -q 'SMSG_SUI_THREAT *= 877' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 threat-meter reply drifted from 877"
run "grep -q 'CAPABILITY_THREAT_METER_V1 = 1u << 14' $G/SuperUiContent/SuiWorld/Bridge/SuiPortal.h" || fail "5.1 threat-meter capability is not bit 14"
run "grep -q 'NUM_MSG_TYPES *= 880' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "5.1 NUM_MSG_TYPES is not 880"
run "! grep -q 'NUM_MSG_TYPES.*868' docs/SUI_WIRE_PROTOCOL.md" || fail "5.1 wire docs still advertise stale NUM_MSG_TYPES 868"
run "grep -q 'CAPABILITY_TACTICAL_FREEZE_V1 = 1u << 12' $G/SuperUiContent/SuiWorld/Bridge/SuiPortal.h" || fail "5.1 tactical capability is not bit 12"
run "grep -q 'constexpr uint8 WIRE_VERSION = 1' $TH" || fail "5.1 tactical bodies lost explicit version 1"
run "grep -q 'static constexpr size_t WIRE_SIZE = 14' $G/Server/Packets/SuiControl.h" || fail "5.1 freeze request is not exact 14 bytes"
run "grep -q 'static constexpr size_t RECORD_SIZE = 37' $G/Server/Packets/SuiControl.h" || fail "5.1 queue records are not fixed 37 bytes"
run "grep -c '!requestId' $TF | grep -q '^2$'" || fail "5.1 tactical CMSG requestId zero is not rejected in both handlers"
run "grep -q '!std::isfinite(row.x)' $TF && grep -q '!std::isfinite(row.z)' $TF" || fail "5.1 tactical queue accepts NaN/Inf coordinates"

run "grep -q 'Player\* anchor = session->GetSuiActor()' $TF" || fail "5.2 freeze center is not sampled from the driven body"
run "grep -q 'lock.ownerGuid = ownerGuid' $TF" || fail "5.2 lock authority is not the real socket owner"
run "grep -q 'lock.anchorGuid = Raw(anchor->GetObjectGuid())' $TF" || fail "5.2 anchor identity is not preserved separately"
run "grep -q 'dx \* dx + dy \* dy + dz \* dz <= radiusSq' $TF" || fail "5.2 membership is not full 3-D"
run "grep -q 'RADIUS_YARDS = 100.0f' $TH" || fail "5.2 radius is not fixed at 100 yards"
run "grep -q 'SuiPossess::IsFreecamEye(unit)' $TF" || fail "5.2 the SUI streaming eye is frozen as a gameplay member"
run "grep -q 'pair.second == guid' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.2 freecam exclusion is inferred from a blanket creature entry"
run "grep -q 'recursive_mutex s_freecamEyesMutex' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.2 freecam registry is raced by concurrent map updates"
n=$(run "grep -c 'lock_guard<std::recursive_mutex> guard(s_freecamEyesMutex)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" | tr -d '\r')
[ "${n:-0}" -eq 4 ] || fail "5.2 not every freecam registry accessor holds its mutex"
run "grep -q 'MAX_MEMBERS = 0xFFFFu' $TH" || fail "5.2 tactical field has an artificial member cap below its u16 wire ceiling"
run "grep -q 'thawing without a partial field' $TF" || fail "5.2 member overflow can leave a partially frozen radius"
run "grep -q 'm_suiTacticalFreezeRefs.fetch_add' $G/Objects/Unit.cpp" || fail "5.3 overlap-safe unit membership refcount is gone"
run "grep -q 'DelayCooldowns' $G/Objects/Unit.cpp" || fail "5.3 cooldown deadlines are not rebased on final thaw"
run "awk '/void Map::UpdateSessionsMovementAndSpellsIfNeeded\(\)/{f=1} f&&/PACKET_PROCESS_MOVEMENT/{m=1} m&&/SuiTacticalFreeze::UpdateMap\(this\)/{l=1} l&&/PACKET_PROCESS_SPELLS/{print \"yes\"; exit} f&&/^}/{exit}' $G/Maps/Map.cpp | grep -q yes" || fail "5.3 a boundary-crossing player can cast before entrant latching"
run "grep -q 'bool LatchEntrant(Unit\* unit)' $TH && grep -q 'bool LatchEntrant(Unit\* unit)' $TF" || fail "5.3 synchronous actor motion has no precise entrant latch"
run "awk '/void Unit::Update\(uint32/{f=1} f&&/UpdateMotionAsync\(p_time\)/{m=1} m&&/SuiTacticalFreeze::LatchEntrant\(this\)/{l=1} l&&/WorldObject::Update/{print \"yes\"; exit} f&&/^}/{exit}' $G/Objects/Unit.cpp | grep -q yes" || fail "5.3 a synchronous spline/MotionMaster entrant can run post-motion gameplay"
run "awk '/void Creature::Update\(uint32/{f=1} f&&/Unit::Update\(update_diff, diff\)/{u=1} u&&/IsSuiTacticallyFrozen\(\)/{l=1} l&&/AI\(\)->UpdateAI/{print \"yes\"; exit} f&&/^}/{exit}' $G/Objects/Creature.cpp | grep -q yes" || fail "5.3 a newly-latched creature reaches AI in the same tick"
run "awk '/void Player::Update\(uint32/{f=1} f&&/Unit::Update\(update_diff, p_time\)/{u=1} u&&/IsSuiTacticallyFrozen\(\)/{l=1} l&&/m_AI->UpdateAI/{print \"yes\"; exit} f&&/^}/{exit}' $G/Objects/Player.cpp | grep -q yes" || fail "5.3 a newly-latched player reaches bot AI in the same tick"
run "grep -q 'IsInWorld() && !(\*it)->IsSuiTacticallyFrozen()' $G/Maps/Map.cpp" || fail "5.3 queued async motion can advance after the actor is frozen"

for f in Objects/Unit.cpp Objects/Player.cpp Objects/Creature.cpp Objects/Pet.cpp Objects/TemporarySummon.cpp Objects/Totem.cpp; do
  run "grep -q 'IsSuiTacticallyFrozen()' $G/$f" || fail "5.4 $f advances a frozen actor clock"
done
for f in Handlers/MovementHandler.cpp Handlers/SpellHandler.cpp Handlers/CombatHandler.cpp Handlers/PetHandler.cpp; do
  run "grep -q 'IsSessionGameplayFrozen' $G/$f" || fail "5.5 $f lacks frozen-session ingress suppression"
done
run "awk '/void WorldSession::HandleStandStateChangeOpcode/{f=1} f&&/Player.*actor.*=.*GetSuiActor\(\)/{a=1} f&&a&&/actor->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5 stand-state input can replace its frozen acting-body pose"
run "awk '/void WorldSession::HandleEmoteOpcode/{f=1} f&&/GetPlayer\(\)->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/ChatHandler.cpp | grep -q yes" || fail "5.5 emote input can replace its frozen target pose"
run "awk '/void WorldSession::HandleTextEmoteOpcode/{f=1} f&&/if \(!GetPlayer\(\)->IsSuiTacticallyFrozen\(\)\)/{print \"yes\"; exit} f&&/EmoteChatBuilder/{exit}' $G/Handlers/ChatHandler.cpp | grep -q yes" || fail "5.5 text-emote animation can replace its frozen target pose"
run "awk '/void WorldSession::HandleTextEmoteOpcode/{f=1} f&&/!GetPlayer\(\)->IsSuiTacticallyFrozen\(\).*unit/{g=1} g&&/ReceiveEmote/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/ChatHandler.cpp | grep -q yes" || fail "5.5 frozen text emote can still trigger CreatureAI gameplay"
run "awk '/void WorldSession::HandleMountSpecialAnimOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MovementHandler.cpp | grep -q yes" || fail "5.5 mount-special input can replace a frozen pose"
run "awk '/void WorldSession::HandleSummonResponseOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MovementHandler.cpp | grep -q yes" || fail "5.5 summon response can relocate a frozen body"
run "awk '/void WorldSession::HandleLootOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/LootHandler.cpp | grep -q yes" || fail "5.5 loot-open can interrupt a frozen cast/pose"
run "awk '/void WorldSession::HandleAreaTriggerOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5 area trigger can relocate a frozen body"
for fn in HandleActivateTaxiExpressOpcode HandleActivateTaxiOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/TaxiHandler.cpp | grep -q yes" || fail "5.5 $fn can start frozen-body flight"
done
for fn in HandleAutostoreLootItemOpcode HandleLootMoneyOpcode HandleLootMasterGiveOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/LootHandler.cpp | grep -q yes" || fail "5.5 $fn mutates loot during tactical lock/drain"
done
run "awk '/void WorldSession::HandleLootRoll/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/GroupHandler.cpp | grep -q yes" || fail "5.5 loot roll mutates during tactical lock/drain"
for fn in HandleTabardVendorActivateOpcode HandleBankerActivateOpcode HandleTrainerListOpcode HandleTrainerBuySpellOpcode HandleGossipHelloOpcode HandleGossipSelectOptionOpcode HandleSpiritHealerActivateOpcode HandleBinderActivateOpcode HandleListStabledPetsOpcode HandleStablePet HandleUnstablePet HandleBuyStableSlot HandleStableRevivePet HandleStableSwapPet HandleRepairItemOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q yes" || fail "5.5 $fn bypasses tactical lock/drain"
done
run "awk '/void WorldSession::HandleSaveGuildEmblemOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/GuildHandler.cpp | grep -q yes" || fail "5.5 guild-emblem mutation bypasses tactical lock/drain"
for fn in HandleSplitItemOpcode HandleSwapInvItemOpcode HandleAutoEquipItemSlotOpcode HandleSwapItem HandleAutoEquipItemOpcode HandleDestroyItemOpcode HandleSellItemOpcode HandleBuybackItem HandleBuyItemInSlotOpcode HandleBuyItemOpcode HandleListInventoryOpcode HandleAutoStoreBagItemOpcode HandleBuyBankSlotOpcode HandleAutoBankItemOpcode HandleAutoStoreBankItemOpcode HandleSetAmmoOpcode HandleWrapItemOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/ItemHandler.cpp | grep -q yes" || fail "5.5 $fn mutates inventory during tactical lock/drain"
done
for fn in HandleQuestgiverHelloOpcode HandleQuestgiverAcceptQuestOpcode HandleQuestgiverChooseRewardOpcode HandleQuestgiverRequestRewardOpcode HandleQuestLogSwapQuest HandleQuestLogRemoveQuest HandleQuestConfirmAccept HandleQuestgiverCompleteQuest HandlePushQuestToParty HandleQuestPushResult; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/QuestHandler.cpp | grep -q yes" || fail "5.5 $fn mutates quests during tactical lock/drain"
done
for fn in HandleSendMail HandleMailMarkAsRead HandleMailDelete HandleMailReturnToSender HandleMailTakeItem HandleMailTakeMoney HandleGetMailList HandleMailCreateTextItem; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MailHandler.cpp | grep -q yes" || fail "5.5 $fn mutates/opens mail during tactical lock/drain"
done
for fn in HandleAuctionHelloOpcode HandleAuctionSellItem HandleAuctionPlaceBid HandleAuctionRemoveItem; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/AuctionHouseHandler.cpp | grep -q yes" || fail "5.5 $fn mutates/opens auction during tactical lock/drain"
done
for fn in HandleAcceptTradeOpcode HandleBeginTradeOpcode HandleInitiateTradeOpcode HandleSetTradeGoldOpcode HandleSetTradeItemOpcode HandleClearTradeItemOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/TradeHandler.cpp | grep -q yes" || fail "5.5 $fn mutates trade during tactical lock/drain"
done
run "grep -q 'void PrepareMemberForFreeze' $TF && grep -q 'player->TradeCancel(true)' $TF" || fail "5.5 an already-accepted trade can commit after its participant freezes"
run "grep -q 'Preflight every fallible invariant before trade cleanup' $TF" || fail "5.5 denied acquisition can still cancel an unrelated open trade"
run "awk '/void WorldSession::HandleAcceptTradeOpcode/{f=1} f&&/IsSessionGameplayFrozen\(trader->GetSession\(\)\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/TradeHandler.cpp | grep -q yes" || fail "5.5 trade commit does not fence a frozen/draining counterparty"
run "awk '/void WorldSession::HandleSetSelectionOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5 server selection mutates during tactical lock/drain"
run "awk '/void HandleCompanion\(WorldSession\* session/{f=1} f&&/ACTION_SUMMON.*ACTION_DISMISS/{m=1} m&&/IsSessionGameplayFrozen\(session\)/{print \"yes\"; exit} f&&/switch \(action\)/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiCompanion.cpp | grep -q yes" || fail "5.5 companion summon/dismiss can mutate frozen membership"
for fn in HandlePartyQuest HandleMemberItemMove HandlePartyLead; do
  run "awk '/void '$fn'\(WorldSession\* session/{f=1} f&&/IsSessionGameplayFrozen\(session\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp | grep -q yes" || fail "5.5 $fn mutates state during tactical freeze"
done
run "awk '/void HandlePartyTaxi\(WorldSession\* session/{f=1} f&&/IsSessionGameplayFrozen\(session\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiTaxi.cpp | grep -q yes" || fail "5.5 party taxi mutates movement during tactical freeze"
run "awk '/void HandleRtsAction\(WorldSession\* session/{f=1} f&&/IsSessionGameplayFrozen\(session\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/SuperUiContent/SuiWorld/RTS/SuiRts.cpp | grep -q yes" || fail "5.5 RTS hero action mutates state during tactical freeze"
run "grep -q 'only CMSG_SUI_TACTICAL_QUEUE may add actions' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.5 ordinary SUI orders bypass the tactical queue"
run "grep -q 'HandleRequest(WorldSession' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp && grep -q 'DENY_REQUESTER_STATE' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.5 possession handoff is not fenced"
run "awk '/static AckResult TryBegin/{f=1} f&&/bot->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp | grep -q yes" || fail "5.5 an outside session can possess a bot held by another field"
run "grep -q 'Preflight the complete subject set' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp && grep -q '!pMember || pMember->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.5 ordinary multi-orders can author post-thaw intent on a frozen subject"
for fn in HandlePetAction HandlePetSetAction HandlePetRename HandlePetAbandon HandlePetStopAttack HandlePetUnlearnOpcode HandlePetSpellAutocastOpcode HandlePetCastSpellOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/PetHandler.cpp | grep -q yes" || fail "5.5 $fn can mutate a frozen remote pet/charmed Unit"
done
run "grep -q 'companion->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/CRPG/SuiCompanion.cpp" || fail "5.5 an outside owner can dismiss a frozen companion"
run "grep -q 'member->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/CRPG/SuiTaxi.cpp" || fail "5.5 party taxi can board a frozen remote member"
run "grep -q 'subject && subject->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/RTS/SuiRts.cpp" || fail "5.5 RTS can mutate a frozen remote hero"
run "grep -q 'leader->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp && grep -q 'from->IsSuiTacticallyFrozen() || to->IsSuiTacticallyFrozen()' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.5 party lead/item mutations ignore frozen remote subjects"
run "awk '/void WorldSession::HandleAttackSwingOpcode/{f=1} f&&/pEnemy->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/CombatHandler.cpp | grep -q yes" || fail "5.5 an outside actor can seed autoattack intent against a frozen target"
run "awk '/void WorldSession::HandleCastSpellOpcode/{f=1} f&&/target->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/SpellHandler.cpp | grep -q yes" || fail "5.5 a new cast can target a frozen Unit and masquerade as a pre-freeze delayed hit"
run "grep -q 'pTarget && pTarget->IsSuiTacticallyFrozen()' $G/Handlers/PetHandler.cpp && grep -q 'pUnitTarget && pUnitTarget->IsSuiTacticallyFrozen()' $G/Handlers/PetHandler.cpp" || fail "5.5 pet commands can seed post-thaw intent against a frozen target"

# 5.5a The field seals physical/service targets too.  An unfrozen outsider must
#       not use a frozen NPC/player/corpse as a back door into new gameplay.
run "grep -q 'bool IsInteractionTargetFrozen' $TH && grep -q 'guid.IsPlayer()' $TF && grep -q 'guid.IsCorpse()' $TF" || fail "5.5a the shared frozen interaction-target resolver is incomplete"
for fn in HandleTabardVendorActivateOpcode HandleBankerActivateOpcode HandleTrainerListOpcode HandleTrainerBuySpellOpcode HandleGossipHelloOpcode HandleGossipSelectOptionOpcode HandleSpiritHealerActivateOpcode HandleBinderActivateOpcode HandleListStabledPetsOpcode HandleRepairItemOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsInteractionTargetFrozen\(this, packet\./{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q yes" || fail "5.5a $fn can use a frozen physical service target"
done
run "awk '/bool WorldSession::CheckStableMaster/{f=1} f&&/IsInteractionTargetFrozen\(this, guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/NPCHandler.cpp | grep -q yes" || fail "5.5a stable follow-ups can mutate through a frozen stable master"
for fn in HandleSellItemOpcode HandleBuybackItem HandleBuyItemInSlotOpcode HandleBuyItemOpcode HandleListInventoryOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsInteractionTargetFrozen\(this, packet\./{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/ItemHandler.cpp | grep -q yes" || fail "5.5a $fn can use a frozen vendor"
done
run "grep -q 'creature->IsSuiTacticallyFrozen()' $G/Objects/Player.cpp && awk '/bool WorldSession::CheckBanker/{f=1} f&&/IsInteractionTargetFrozen\(this, guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/ItemHandler.cpp | grep -q yes" || fail "5.5a stored banker operations can mutate through a frozen banker"
run "awk '/AuctionHouseEntry const\* WorldSession::GetCheckedAuctionHouseForAuctioneer/{f=1} f&&/IsInteractionTargetFrozen\(this, guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/AuctionHouseHandler.cpp | grep -q yes" || fail "5.5a auction follow-ups can use a frozen auctioneer"
for fn in HandleAuctionListBidderItems HandleAuctionListOwnerItems HandleAuctionListItems; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/AuctionHouseHandler.cpp | grep -q yes" || fail "5.5a $fn can replace frozen pose/state"
done
for fn in HandleTaxiQueryAvailableNodes HandleActivateTaxiExpressOpcode HandleActivateTaxiOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsInteractionTargetFrozen\(this, packet\./{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/TaxiHandler.cpp | grep -q yes" || fail "5.5a $fn can use a frozen flight master"
done
for fn in HandleQuestgiverHelloOpcode HandleQuestgiverAcceptQuestOpcode HandleQuestgiverQueryQuestOpcode HandleQuestgiverChooseRewardOpcode HandleQuestgiverRequestRewardOpcode HandleQuestgiverCompleteQuest; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsInteractionTargetFrozen\(this, packet\.guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/QuestHandler.cpp | grep -q yes" || fail "5.5a $fn can use a frozen quest giver"
done
run "awk '/void WorldSession::HandleSaveGuildEmblemOpcode/{f=1} f&&/IsInteractionTargetFrozen\(this, packet.vendorGuid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/GuildHandler.cpp | grep -q yes" || fail "5.5a guild emblem can use a frozen tabard vendor"
run "grep -q '#include \"SuiTacticalFreeze.h\"' $G/Handlers/SkillHandler.cpp && awk '/void WorldSession::HandleLearnTalentOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/SkillHandler.cpp | grep -q yes" || fail "5.5a talent learning bypasses the freeze/drain fence"
run "awk '/void WorldSession::HandleTalentWipeConfirmOpcode/{f=1} f&&/IsInteractionTargetFrozen\(this, packet.guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/SkillHandler.cpp | grep -q yes" || fail "5.5a talent reset can use a frozen trainer"
run "grep -c 'player->GetSession()->DoLootRelease' $G/Handlers/LootHandler.cpp | grep -q '^2$'" || fail "5.5a a stored frozen loot source is not closed before mutation"
run "awk '/void WorldSession::HandleLootOpcode/{f=1} f&&/IsInteractionTargetFrozen\(this, packet.guid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/LootHandler.cpp | grep -q yes" || fail "5.5a loot can open on a frozen source"
run "awk '/void WorldSession::HandleLootMasterGiveOpcode/{f=1} f&&/packet.lootGuid/{s=1} s&&/packet.playerGuid/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/LootHandler.cpp | grep -q yes" || fail "5.5a master loot ignores a frozen source or recipient"
run "awk '/void WorldSession::HandleLootRoll/{f=1} f&&/IsInteractionTargetFrozen\(this, packet.lootedTarget\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/GroupHandler.cpp | grep -q yes" || fail "5.5a loot roll can mutate a frozen source"
run "grep -q 'bool IsTacticalTradePartnerBlocked' $G/Handlers/TradeHandler.cpp && grep -c 'IsTacticalTradePartnerBlocked(pActor)' $G/Handlers/TradeHandler.cpp | grep -q '^5$'" || fail "5.5a stored trade mutations do not revalidate the counterparty"
run "awk '/void WorldSession::HandleInitiateTradeOpcode/{f=1} f&&/pOther->IsSuiTacticallyFrozen\(\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/TradeHandler.cpp | grep -q yes" || fail "5.5a trade can start against a frozen player"
run "grep -q 'bool IsTacticalPartyTargetBlocked' $G/Handlers/GroupHandler.cpp" || fail "5.5a party authority changes do not share a frozen/draining target fence"
for fn in HandleGroupInviteOpcode HandleGroupAcceptOpcode HandleGroupUninviteGuidOpcode HandleGroupUninviteOpcode HandleGroupSetLeaderOpcode HandleGroupDisbandOpcode HandleLootMethodOpcode HandleGroupRaidConvertOpcode HandleGroupChangeSubGroupOpcode HandleGroupSwapSubGroupOpcode HandleGroupAssistantLeaderOpcode HandleRaidReadyCheckOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/GroupHandler.cpp | grep -q yes" || fail "5.5a $fn changes party commandability during freeze/drain"
done
run "awk '/void WorldSession::HandleDuelAcceptedOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{s=1} s&&/plTarget->IsSuiTacticallyFrozen/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/DuelHandler.cpp | grep -q yes" || fail "5.5a duel accept ignores a frozen participant"
run "awk '/void WorldSession::HandleSummonResponseOpcode/{f=1} f&&/IsInteractionTargetFrozen\(this, packet.summonerGuid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MovementHandler.cpp | grep -q yes" || fail "5.5a summon accept ignores a frozen summoner"
for fn in HandleRepopRequestOpcode HandleReclaimCorpseOpcode HandleResetInstancesOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5a $fn mutates frozen world state"
done
run "awk '/void WorldSession::HandleResurrectResponseOpcode/{f=1} f&&/!packet.accept/{d=1} d&&/IsSessionGameplayFrozen\(this\)/{s=1} s&&/IsInteractionTargetFrozen\(this, packet.resurrectorGuid\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5a resurrection accept ignores a frozen body/caster or blocks decline cleanup"
run "awk '/void WorldSession::HandleSetFactionAtWarOpcode/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/CharacterHandler.cpp | grep -q yes && awk '/void WorldSession::HandleTogglePvP/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/MiscHandler.cpp | grep -q yes" || fail "5.5a hostility toggles bypass the tactical fence"
run "grep -q '!unit->IsSuiTacticallyFrozen() && unit->IsCreature()' $G/Handlers/ChatHandler.cpp" || fail "5.5a an outside text emote can trigger a frozen creature AI"
for fn in HandleBattlemasterHelloOpcode HandleBattlefieldJoinOpcode HandleBattlemasterJoinOpcode HandleBattleFieldPortOpcode HandleLeaveBattlefieldOpcode HandleAreaSpiritHealerQueueOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/BattleGroundHandler.cpp | grep -q yes" || fail "5.5a $fn mutates battleground state during freeze/drain"
done
run "grep -q 'bool IsTacticalQueueMemberBlocked' $G/Handlers/BattleGroundHandler.cpp && grep -q 'IsInteractionTargetFrozen(this, battlemaster)' $G/Handlers/BattleGroundHandler.cpp" || fail "5.5a battleground group/physical targets bypass the tactical boundary"
for fn in HandleMeetingStoneJoinOpcode HandleMeetingStoneLeaveOpcode; do
  run "awk '/void WorldSession::'$fn'/{f=1} f&&/IsSessionGameplayFrozen\(this\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/LFG/LFGHandler.cpp | grep -q yes" || fail "5.5a $fn mutates LFG queue during freeze/drain"
done
run "awk '/sWorld.GetMessager\(\).AddMessage/{f=1} f&&/GetSecurity\(\) == SEC_PLAYER/{s=1} s&&/IsSessionGameplayFrozen\(session\)/{print \"yes\"; exit} f&&/^        }\);/{exit}' $G/Chat/Chat.cpp | grep -q yes" || fail "5.5a async player dot commands can race tactical latching"
run "grep -q 'Physical interaction targets are sealed' docs/SUI_WIRE_PROTOCOL.md" || fail "5.5a physical target boundary is undocumented"

run "grep -q 'IsSuiTacticallyFrozen() || pVictim->IsSuiTacticallyFrozen()' $G/Objects/Unit.cpp" || fail "5.6 direct damage crosses a frozen boundary"
run "grep -q 'suppressed immediate spell' $G/Spells/Spell.cpp" || fail "5.6 immediate spell effects are not suppressed"
run "grep -q 'retry = t_offset + 50' $G/Spells/Spell.cpp" || fail "5.6 delayed explicit spell hits are not deferred"
run "grep -q 'boundaryCaster->IsSuiTacticallyFrozen' $G/Maps/GridNotifiersImpl.h" || fail "5.6 persistent area effects originate across the boundary"

for reason in FREEZE_RELEASED_VIEW FREEZE_RELEASED_LOGOUT FREEZE_RELEASED_MAP_CHANGE FREEZE_RELEASED_DEATH; do
  run "grep -q '$reason' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "5.7 lifecycle thaw $reason is absent"
done
run "awk '/void OnPlayerTeleport\(Player\* player, bool farTeleport\)/{f=1} f&&/ReleaseForPlayer\(player/{print \"yes\"; exit} f&&/if \(farTeleport\)/{exit 1}' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp | grep -q yes" || fail "5.7 near teleport can move an active lock anchor without thawing"
run "grep -q 'MAX_ACTIONS_PER_ACTOR = 5' $TH" || fail "5.8 per-actor FIFO cap is not five"
run "grep -q 'MAX_REQUEST_RECORDS = 40' $TH" || fail "5.8 raid-wide queue request cap is not forty"
run "grep -q 'SuiCompanion::MayCommand(owner, actor)' $TF" || fail "5.8 queue authority bypasses the companion law"
run "grep -q 'target && target->IsSuiTacticallyFrozen()' $TF" || fail "5.8 queued target intent is consumed while an overlapping lock still holds it"
run "grep -q 'Wait without starting the' $TF" || fail "5.8 overlap wait incorrectly burns the queued action retry budget"
run "awk '/QueueResult IssueAction/{f=1} f&&/case ACTION_CAST:/{c=1} c&&/ai->StopMoving\(\)/{print \"yes\"; exit}' $TF | grep -q yes" || fail "5.8 queued cast does not safely supersede pre-freeze motion"
run "grep -q 'bool HasOlderQueuedAction' $TF && grep -q 'HasOlderQueuedAction(lock.id, actorGuid)' $TF" || fail "5.8 reacquired plans can execute concurrently on one actor"
run "grep -q 'bool IsSessionPlanDraining' $TH && grep -q 'return IsSessionPlanDraining(session)' $TF" || fail "5.8 live input is not fenced while an owned plan drains"
run "awk '/void HandleFreeze\(WorldSession\* session/{f=1} f&&/IsSessionGameplayFrozen\(session\)/{exit 1} f&&/bool bodyFrozen = owner->IsSuiTacticallyFrozen\(\)/{b=1} f&&/void HandleQueue/{exit !b}' $TF" || fail "5.8 freeze reacquire is incorrectly denied by an older draining plan"
run "grep -q 'Reassert it on every tick' $TF" || fail "5.8 Command View hand-back can release AI during queue drain"
run "grep -q 'previousRtsHold = ai->m_suiRtsHold' $TF && grep -q 'ai->m_suiRtsHold = queue.previousRtsHold' $TF" || fail "5.8 tactical drain permanently changes the actor's prior RTS hold discipline"
run "grep -q 'A second real person.*read-only' $TF" || fail "5.8 other human characters are commandable"
run "grep -q 'Queues are private command state' $TF" || fail "5.8 queue details are not owner-only"
run "grep -q 'Every real SUI session in the same map' $TF" || fail "5.9 freeze observer snapshots are not map-wide"

echo "possess-law-check: PASS"
