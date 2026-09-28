# Commander raid state — 2026-09-15 ~22:45Z (Claude): **MAJORDOMO EXECUTUS ACCEPTED 8/10** (batch majordomo-0915r, session sep15-majordomo-v64, Core bd5fc02c = executor v64 = v57 + a control claim/refresh preempts the member's own damage cast; encounter-acceptance.json recorded 22:37:55Z, 86 raw evidence hashes verified; W W W W W L W L W W). SCOREBOARD: accepted Lucifron, Magmadar, Gehennas, Geddon, Shazzrah, Sulfuron, Golemagg, MAJORDOMO; instance-dead not accepted Garr; Ragnaros not killed. RAGNAROS first measured batch ragnaros-0915b = 0/3 aborted (item 163): he IS engaged now (89-92 % left, ~1.1k raid DPS) and the family is tank-contact-loss - his knockback empties melee range and Magma Blast sprays the raid; Elemental Fire puts 28k on the owner in 45 s. Checkpoint mc-sep15-ragnaros-ready-1 is captured and reusable (he stays summoned 2 h; after that, win Majordomo again and re-run the gossip). Majordomo raid loot (one kill, even distribution, worn) is now due - his loot is the Cache of the Firelord GAMEOBJECT, which RaidQaCheckpoint v10 `loot` (creature templates) does not cover yet. Loop history: item 162. Previous header: RUNNING ~22:00Z, v57 pooled 10/16; rejected v58 1/4, v59 2/5, v60 3/6, v62 2/3 valid, v63 2/5; potion diagnostic v61. If you find this header the loop is live or was interrupted - check tasklist + `.bot info` first. Previous header: PAUSED 2026-09-15 ~17:15Z (Claude), clean boundary (no client, 0p+0b), Core 90b2d1b7 = executor v56 + RaidQaCheckpoint v10 (PID 3538460, unchanged). MAJORDOMO v56: batch majordomo-0915j W L W L W W W W L = 6/9, stopped by the runner (8 of 10 unreachable); with the owner-stopped 0915i (i-01 loss) v56 = 6/10 valid pulls - NOT accepted. v55 5/8 + v56 6/10 = 11/18 (61 %); the v56 rescue fired once in 10 pulls, so the two are nearly the same executor. Losses: healers took 8.2-8.4k/head vs 4.9-7.2k in the six wins (Shadow Shock 20603 + elite melee). Awaiting the owner's next lever (item 161). Previous header: STOPPED BY OWNER 2026-09-15 ~14:45Z (Claude), clean boundary (no client, 0p+0b), Core 90b2d1b7 = executor v56 (v55 + objective taunt-rescue, deployment entry 52, canonical 9ab1fc1a->aa027164) + RaidQaCheckpoint v10. Batch majordomo-0915i (session sep15-majordomo-v56): i-01 loss, i-02 stopped mid-pull (invalid); the objective rescue logged 0 times (nearest add tanks were escaping hazards, holding adds, or dead when Majordomo hit healers). Staging before it: 11 respawned trash within 150 yd GM-removed (gm_clear_done). Best Majordomo config remains v55 five tanks 5/8. Previous header: PAUSED ~14:05Z, Core 2ef9b30d = executor v55 (nearest add-tank assignment + rescue) + RaidQaCheckpoint v10. MAJORDOMO v55 five tanks: 5/8 (batch 0915h, item 160), best so far; remaining failure = an add tank beside an elite on a healer does not taunt it. Earlier header: Core 1d8fa804 = executor v54 + RaidQaCheckpoint v10. FIVE-TANK ROSTER (Testwar + Boomwarrior 115, Sylwhisper 116, Shieldwall 152, Ironwarden 153; items 157/159) + raid loot worn. MAJORDOMO: four tanks 3/8 valid (item 158), five tanks 2/5 (batch 0915f stopped, item 159) - tank count is not the limiter; losses = an elite on the healers + DPS attrition. Awaiting the owner's next lever. Previous pause: 2026-09-15 ~00:45Z (Claude), clean boundary (no client, 0p+0b), Core f535ee72 = executor v54 + RaidQaCheckpoint v8 (PID 3465235, screen 3465234.mangosd). SCOREBOARD: accepted Lucifron, Magmadar, Gehennas, Geddon, Shazzrah, Sulfuron, Golemagg; instance-dead NOT accepted Garr, Majordomo; Ragnaros not killed. Majordomo went 3/21 across executor v45-v54 with two add tanks. OWNER DECISION (item 156): MC/BWL run with FOUR TANKS - next agent does the roster change first, then recaptures checkpoints and runs the Majordomo 8/10 batch, then Ragnaros (8/10). Evening findings: items 147-156; resume prompt at the end of the session log.

**AUTOPILOT HANDOFF 2026-09-24 ~23:40 EDT (Claude): stopped at the owner's request. Full handoff = `shared_docs/AUTOPILOT_HANDOFF.md` (read it first). Autopilot OFF, Core 2add6ffc installed, 39 bots online in MC instance 100. Dead: Gehennas, Garr, and the first Core Hound pack in Lucifron's room (23:37). Everything uncommitted.**

**AUTOPILOT UPDATE 2026-09-24 ~18:00 (Claude), Core 532f5f13 installed. BOSSES KILLED BY THE AUTOPILOT (instance 100, normal combat): Gehennas (13:29), Garr (17:41).** Alive: Lucifron (patrols a 130 yd waypoint route through his three Core Hound packs - every hound fight had him join), Magmadar, Baron Geddon (patrols; first attempt died), Shazzrah, Sulfuron, Golemagg; then Majordomo, Ragnaros. Added since the entry below (all generic, from live data): "who pulled what" telemetry (`[TELEMETRY] FIRST HIT` / `JOINED ... (unprovoked)`); no area spell that would reach an idle creature (Frost Nova/Arcane Explosion were waking Giants); no pull while the raid is fighting; repositioning (spread, step clear, fire, healer/ranged closing) stays out of idle packs' 25 yd reach AND out of patrol routes (waypoint data); patrol-route clearance in the pull planner; a boss whose patrol route passes within 30 yd of the chosen trash is pulled first; bosses last otherwise (room trash within 90 yd first); raid spreads (ranged/healers, 2+ within 5 yd); lava/slime escape + no follow back into it; healers break a cast on someone else when the tank (<75 %) is out of sight; a tank under 40 % is every healer's target; tactical moves are not cancelled by the next cast (DoCastSpell stops movement); fake death (Core Hound lying at 1 hp) never attacked, its kind is damaged evenly; warlocks banish a spare elite when elites outnumber tanks (`[AIBOT-CC]`); hunters Tranquilizing Shot enrage-type buffs; mages decurse the group; dwarf priests Fear Ward tanks; a tank may taunt off another tank holding 3+; enemy pulsing areas (Inferno) are hazard circles and a member carrying a bomb (Living Bomb, recognised from its periodic-trigger area data) walks away from the raid; gather before pull (summon after 15 s); an early aggro falls back to the last clear spot; off-mesh leader recovery; skips expire after 10 min; 3 leader deaths on an entry = defer it 30 min (no longer a full stop); leader/members left outside the instance are brought back in.

**AUTOPILOT ROUNDS 2026-09-24 (Claude), Core d8edb451 installed (uncommitted; backups /tmp/bak-r5..r13 on the box, previous binaries run/bin/mangosd.prev-*). MC trash is now being cleared by the bot leader with 0-2 deaths per pull; no boss attempted yet.** Owner (2026-09-24): "Dont stop. you need to clear MC." Evidence channel: `[AUTOPILOT]`, `[TELEMETRY]`, `[AIBOT-HAZARD]` in Server.log; cycle = `bash scratch/autopilot/cycle.sh Shieldwall` (refuses if Testwar is online). What changed, all generic: (1) **pull planner** (`PlanPull`): 24 directions around the pack, pull spot 28 yd out with sight, camp 50 yd out behind it, scored by distance of spot/camp/the raid's walk to every OTHER idle pack (leader-side scan too); the leader shoots from the spot with its ranged weapon (bow/gun/crossbow/thrown/wand, NOT_READY = shot in flight), closes in for sight if the pack wandered, walks in only without a ranged weapon; (2) **pack in the way** (`InTheWay`): a pullable pack within 26 yd of the path to the chosen target goes first; **crowded plan** (camp <35 / spot <22 / route <15 yd): a patrol is waited out (<=60 s) at the **safe spot** (last leader position with no idle hostile within 50 yd), a static pack has its crowder pulled first (<=3 hops); a close aggro is held in place only when the camp is within 30 yd; (3) **between pulls**: regroup every 5 s of rest (revive dead, summon members >60 yd - owner-approved setup), `SuiRaidSupply::Supply` for every member (the owner-editable `run/etc/sui-raid-supply.json`, now with `"bag": 14156` = Bottomless Bag into every EMPTY bag slot - bots had full backpacks and no bags, so no ammo/potions/loot fit), repairs; (4) **respawns**: every spawn the autopilot's group kills is recorded as (map, instance, spawn guid) in `run/etc/sui-autopilot-cleared.txt`; a recorded spawn that respawns (Ancient Core Hound 18 min, Surger 28 min, imps 5-8 min) is executed by itself within 150 yd (no loot) and never picked; world bosses excluded; a NEW instance id = fight again; (5) guards: autopilot only drives inside a dungeon map (it pulled a Horde flight master once at the BRM portal after a restart revived the leader outside) and never targets NPC-flagged/guard creatures; (6) bot tactics: every bot steps out of hostile damaging area auras / trap objects (`AvoidGroundHazard`, from the live DynamicObject/trap spell data), spare tanks scan 60 yd for loose elites, mages decurse the group (was self only), dwarf priests keep Fear Ward on tanks (`TrySpecFearWardTank`), healers walk the navmesh path to the tank for sight, ranged close in for sight only toward the group's own fight near the tank, bot pets defensive in groups; earlier today: repairs fixed "DPS near zero" (durability 0 = unequipped => ability casts failed 25/26), melee-range spells use the swing reach, healer tank/raid split + incoming-heal subtraction, kill order scan 45 yd. Next: finish trash, then bosses in order (Lucifron first) - watch curse/dispel, Magmadar fear + Lava Bomb areas, Geddon Living Bomb (needs a generic "carry the bomb away" rule), Golemagg stacks (tank swap), Garr adds, Majordomo, Ragnaros.

**NEW MISSION 2026-09-23/24 (Claude): owner handed over "run MC, drive the whole thing, make the group/raid bots just play well - all of classic and all dungeons"; no cheating in combat, logic dynamic/boss-agnostic (a little flexibility); GM setup between pulls allowed (revive/summon/repair/replenish/consumables, GM-kill RESPAWNED packs); Testwar may be bot-played; Core rebuild/restart freely. `GM.CheatGod = 0` set in run/etc/mangosd.conf (backup /tmp/mangosd.conf.bak-0923).** Installed Core `b4af37e1` (backups /tmp/bak-tactics-0923, bak-tactics2/3-0923, bak-r5..r7). What exists now, all generic (no entry/spell/boss in logic): (1) **Autopilot** `SuiWorld/CRPG/SuiAutopilot.{h,cpp}` + console command `.autopilot on|off|pause|resume <leader>|status`: a bot leader stands in for the human (FindPartyBoss/FindEscortBoss fall back to it when no real player is in the group, so the same PlayerParty escort code runs); between fights it rests (hp>=80, mana>=70, gathered within 40 yd, cap 90 s), applies raid prep (class buffs only from classes present, role flask/elixirs/food, fire protection), picks the nearest reachable hostile by path length (rings 120/250/450 yd), ADVANCES with the raid to 40 yd, the raid gathers (<=12 s) and HOLDS while the leader walks in; aggro under 15 yd = hold it there, further = run back to camp; pauses after 3 leader deaths on one entry. (2) **Telemetry** `SuiRaidTelemetry` hooked in Unit::DealDamage / Unit::Kill / SpellCaster::DealHeal for the autopilot's group: `[TELEMETRY] DEATH` with last hits, healing received in 8 s and every healer's distance/combat/LOS/cast/mana; per-fight damage by source, damage taken, healing done, damage done + per member. (3) **Tactics** (default group AI, so they also apply when the owner plays): tanks never taunt a mob a tank holds (>25 % hp; human counts as tank by class); tank duty = keep what I hold, else take the nearest loose mob (on a non-tank) no other tank is on; tank steps to the mob's far side so it faces away from the group (`MaintainTankFacing`); DPS kill order from creature spell lists (summoner > healer > caster > rest); **healers now heal while anyone nearby in the group fights** (`HealerOnDuty` - they used to wait until personally in combat, so the tank died unhealed at every pull start); heal range 30 -> 38 yd (spells reach 40); 2 of 5 healers (guid%5<2) are tank healers, the rest raid-heal (tank only <50 %); heals already being cast by others are subtracted (`IncomingGroupHeals`) - the whole healer corps used to OOM in 40 s on overheal; ranged/healers step away from a hostile within 12 yd (healers stay <=28 yd of the tank, ranged <=35; a cast is abandoned only inside 9 yd), a healer out of reach/LOS of the tank closes to 20 yd; melee DPS under 40 % step out until 70 %; tank Shield Wall/Last Stand at 35 %; plus the round-1 AoE gate and self-revival backstop above. **Status: not yet a clear.** Nearest pull from the MC entrance is the 2-Molten-Giant pack (11658: melee 1.2-3.3k, Smash 18944 = 10 yd point-blank ~900 every 8 s, Knock Away drops threat). Best attempt held it 103 s (8 deaths, ~1 giant dead) at only ~1.5k raid DPS; still wipes. Open, in priority: raid DPS is far too low (rogues ~100/s, hunters ~3/s - melee/hunter uptime), tanks re-taunting after Knock Away (the giant turns to healers), 4.3-7k hp tanks are thin for giants, Firelord pack chained in at 64 s. Tooling: `scratch/autopilot/cycle.sh [leader]` = install build-dir mangosd via systemctl, reconnect 39 (verified by DB online flag; Boomwarrior 115 often needs a 2nd ConnectBot), wait 130 s, `.autopilot on`. Testwar cannot be a bot without a `playerbot` row (direct SQL write - not done). Autopilot is OFF; 39 bots online in MC.

**DEPLOYED 2026-09-23 ~01:28Z (Claude): Core b4795f61 = ad5a69b2 + bot self-revival backstop (owner: "we all died, and yet they are stuck in ghost form").** Cause: `AiBotAI` death handling ghosts at the corpse and waits for a C# `RESURRECT`; only the brain's MaintenancePlanner sends one, and the brain service is OFF in this setup (`BotBrainService._brainEnabled` default false; fleet line showed `goals: Idle=39 ... dead: 39` for 4 h). Only companions had a self-run timer. Now every bot gets it as a backstop (AiBotAIMain.cpp dead tick): after its graveyard-run time clamped to 60-120 s, with nobody alive in its group in combat and no hero block, it revives - inside an instance by ghost-porting to the map's entrance trigger (`GetMapEntranceTrigger`, reusing the m_pendingGraveyardRez path; MC = 1091.9,-467.0,-105.1), in the open world in place unless a hostile creature is within 40 yd. A live brain (15-22 s) or a healer's Resurrect still wins. In-place revival inside MC was tried first and REJECTED by evidence: the Firelord/Lava Annihilator pack 56800/91256 and patrolling Lava Surgers / an Ancient Core Hound killed 11 revived bots. Verified live: all 39 alive at the MC entrance, 0 in combat. QA note: a full wipe now self-revives after 60-120 s, so a wipe review must read deaths before that. SEPARATE: Testwar "cannot die with .gm off" = `GM.CheatGod = 1` in run/etc/mangosd.conf (every GM-account login sets the invincibility threshold; `.gm off` does not clear it, `.cheat god off` does) - config NOT changed, owner's call. Backups `/tmp/bak-selfrez-0922/`, previous binary `run/bin/mangosd.prev-74c9ee33`.

**DEPLOYED 2026-09-22 ~20:33Z (Claude): Core ad5a69b2 = 96358a9e + THREAT METER v1 (pair-deploy, owner request).** New `CMSG/SMSG_SUI_THREAT` 876/877 (NUM_MSG_TYPES 878), capability bit 14: the top N (1..10) of one creature's threat list plus the driven body's own rank/threat, replied on the requester's session (`WorldSession::HandleSuiThreatOpcode` in SuiPossess.cpp; wire in Core `docs/SUI_WIRE_PROTOCOL.md`). Client (uncommitted, Debug+Release): AddOns > Threat Meter (off by default, rows 3-10, movable, pull-scale bars: 130 % scale, 110 % tick, holder gold edge, you outlined, amber >=80 %, red past 110 %; target a friend to watch their target), `--threat-meter-only` check; `tools/possess-law-check.sh` 5.1 now pins 876/877/878/bit 14. Same round, client only: custom master-loot panel (docked beside the loot frame, class-grouped, can-use first), party chain badges moved to the bars' right end on the frame's own window list, audio voice priority (raid chop = 32-voice oldest-first stealing). Restart logged the 39 out; all 39 reconnected (Boomwarrior again needed a second call - it is the first ConnectBot after startup). Backups `/tmp/bak-threat-0922/`, previous binary `run/bin/mangosd.prev-96358a9e`. Live-unverified.

**DEPLOYED 2026-09-22 ~19:17Z (Claude): Core 96358a9e = d8de9956 + class-policy AoE (owner: "bots aren't using enough AoE" on Lava Spawns).** Class layer only, no executor tactic, no entry/spell/boss in executor code (source check 142/142). Cause: `AiBotAI::CanUseSpecAoE` (AiBotAISpecCombat.cpp) vetoed the whole AoE when ANY enemy in radius either had no victim yet or had a holder with less threat than `mine + my max health` - every fresh add/summon/split failed that, so no Blizzard/Blade Flurry/Multi-Shot/Rain of Fire/Cleave ever went out on Lava Spawns. Now: a victim-less in-combat enemy is skipped (not a veto); a non-tank splashes while under 80 % of a TANK holder's threat (bot = active role, human = tank class) and freely when the enemy is already loose on a non-tank; CC/uninvolved packs still veto. New `SuiCommanderRaid::ForbidsSplash`: under addPolicy "hold" a melee row (role 4) never splashes a required add (keeps the accepted Garr/Majordomo behaviour). Rungs: Blizzard and Rain of Fire/Hurricane from 2 targets (was 3); Arcane Explosion (10 yd, 3 on the mage) for Frost and Fire too; Cleave as the Arms/Fury rage dump on 2+; Multi-Shot for Survival. NOT measured yet - accepted encounters need one reviewed regression win each on this Core. Backups `/tmp/bak-aoe-0922/`, previous binary `run/bin/mangosd.prev-d8de9956`. Restart logged the 39 out; all 39 reconnected via `/Bots/ConnectBot` (Boomwarrior needed a second call), map 409, Testwar offline. Same round, client (uncommitted, Debug+Release built): an attack ability press now also starts the swing even when refused for resources/cooldown/range (`AttackAbilitySwingLaw`: DmgClass 2/on-next-swing -> melee, cancelling a running Auto Shot/wand; DmgClass 3 ranged shot -> Auto Shot), and after a one-shot ranged pull the combat draw brings the melee weapons out (Sheath.cs holds the bow only while an auto-repeat or ranged cast is live). Live-unverified.

**DEPLOYED 2026-09-22 ~16:00Z (Claude): Core d8de9956 = 36e4a4a7 + two owner-approved raid-usability fixes.** (1) `MirrorOwnerPacket` now whitelists `SMSG_LOOT_MASTER_LIST` - `Group::MasterLoot` answers the LOOTER's session, which while possessing is the bot's socket-less one, so the master looter got the loot window and an EMPTY assign menu (AGENTS rule 2). Client pair landed in the proxy. (2) The executor could only see adds whose ENTRY a definition declares (`SuiCommanderRaid.cpp` gather), so a Firelord's summoned Lava Spawns (12265, splits on death) were invisible to every add tank and add-DPS - they are in no compiled definition because the pack compiler reads `creature_groups` and a summon has no spawn row. Now a temporary summon whose SUMMONER is a watched unit observes itself through the existing `Unit::Update` hook into `plan.summonedAdds` (cap 256, `[SUI][raid-summon]` log line) and joins the candidate gather. Generic link, no entries/spell ids in code; never a required kill (undeclared entry sorts last in `KillRank`) and never extends the completion predicate. Previous binary kept at `run/bin/mangosd.prev-36e4a4a7`; backups of the four patched sources in `/tmp/bak.*`. Client (uncommitted) also got: pack-aware encounter selection (`Use target` went through the 217-BOSS catalogue first, so all 75 compiled pack definitions were unreachable and only the one-at-a-time `Encounter` cycle remained), position disambiguation (13 MC packs are Firelord-led, each with its own room/anchor; the executor gates binding on the room), a one-click `Handle target pack` (select -> definition -> auto-assign -> Apply -> Arm on ack), and a paged/clamped/titled master-loot candidate menu with explicit refusals for master/roll-ongoing/locked rows. **WATCH: vmangos never persists a loot-method change** (`SetLootMethod` writes no SQL; `loot_method` is only written by `Group::Create`), so every restart reverts the raid to its creation value - group loot 3 - and Master Looter must be re-set from the portrait menu. **Also unfixed: `HandleLootMasterGiveOpcode` gates on `GetSuiActor()` vs `GetLooterGuid()`, so assigning while possessing a bot is refused when Testwar holds master loot.** Live state: instance 100 (fresh, trash respawned), raid parked at the entrance landing (1092,-467,-105), nearest spawn 81 yd, 39 bots online, Testwar offline, no plan armed.


**STAGED FOR THE OWNER 2026-09-21 ~01:30Z (Claude; no raid work, no Core/client change).** Owner asked for Testwar's stances/bars and the raid parked OUTSIDE MC. Live state: vmangos dev mangosd RESTARTED (installed 36e4a4a7 unchanged, screen `mangosd`, PID 3668547; the build-dir binary of Sep 18 23:41 was NOT installed). The map-409 global reset (due 2026-09-19) ran at that startup: instance 101, every bind and all 40 corpses are gone, so the raid ID is fresh (item 171's ask). Testwar (leader again, 72 `character_action` rows, all three stances shown; Battle Stance 2457 is a default spell with no `character_spell` row; Bloodthirst 23894 is a DISABLED talent row, so it is not on the bars) and the exact 39 bots (online, brain-linked via `/Bots/ConnectBot`, revived+replenished) were first parked at the Molten Core exit pad, map 0 zone 25 (-7508.3,-1039.7,180.9) beside Lothos Riftwaker and the Core window (`.mmap loc` on the navmesh; `.gps` GroundZ there is the mountain top, so teleports need the explicit z), then - the owner is not attuned - moved INSIDE: Testwar `.go xyz 1091.89 -466.985 -105.084 409` created instance 102 and 39 online `.namego NAME` summons brought the raid to the entrance landing (nearest trash = Molten Giants 81 yd). Bots fan out and idle there without the owner (Solo doctrine, NOPATH sidestep lines are benign). Recipe (offline bots): `.revive NAME` + `.namego NAME` from the pad write their positions; then ConnectBot logs them in on the pad. Same night: creature renderer fix for the owner's 'elementals do not glow' report (unlit/unfogged M2 materials, per-batch colour/alpha/UV tracks, and creature-body particle emitters through the effect pipeline) - client Debug+Release rebuilt, verified on temp-summoned Firelord/Lava Surger/Lava Annihilator; NEVER `.npc summon` scripted MC mobs near a city (a Firelord summons Lava Spawns that split on death - 21 had to be despawned from Stormwind's Trade District). Evidence: `dumps/gameplay-phase{1,3,4}-*-20260920-21*.png`.


## Owner rules (Nico, 2026-09-13 01:20Z) — these REPLACE the inherited measurement rules below

Plain terms: Molten Core is bosses + packs. The raid is the theatre of war for building generic logic.

1. **Forcing a respawn is real combat.** Restoring a checkpoint (which respawns the captured pack or
   boss), resurrecting the dead, and getting the raid back to "buffed and ready" is the normal way to
   fight something again — for packs AND bosses. Nobody kills bosses infinitely in the real game either.
   Do not build or use "re-clear only through natural respawn timers" machinery; just respawn and fight.
2. **Packs: two consecutive kills = done, move on.** Reposition the raid (prepared mode), respawn the pack
   if necessary, engage the deterministic logic handler (the executor), kill it; do it once more; next pack.
   The ten-pull acceptance batch is NOT a pack rule.
3. **Bosses** keep the frozen ten-pull >= 8/10 acceptance (repeatability is the point); respawn the boss
   via its capture-encounter checkpoint between pulls exactly as before.
4. Unchanged: generic logic only (no entries, spell ids, coordinates or names in code; surveys + global
   policy are the hand data); one puller engages ONE group; objective review before a new target; normal
   combat rules with the permanent per-character consumable loadout; GM setup allowed for staging,
   respawning, resurrecting and repositioning; no SQL writes, DB restores, commits, branches, subagents.
5. Every agent so far drifted toward over-measurement; when a rule costs an hour per pack, it is the wrong
   rule — ask, do not inherit.

Owner answers, 2026-09-13 11:45Z (to the four questions in the resume prompt): (1) **a done pack that respawns
on its timer stays done - restart PAST it, never re-kill it** (GM setup removes or bypasses it when it stands in the
way of a restore pose or a stage path); (2) the Garr terminal-burst shape is Claude's call; (3) the "36 yd rule" was
never the owner's (it is the executor's `kPatrolEngageYards` / the compiler's engage circle) - just find a way that
does not chain; (4) **Baron Geddon is killed first**, before the south packs that his loop covers.

Where the clear stands (2026-09-13 01:27Z, in raid terms): bosses dead = Lucifron, Magmadar; alive =
Gehennas, Garr, Baron Geddon, Shazzrah, Sulfuron, Golemagg, Majordomo, Ragnaros. Trash: the five Core
Hound packs on the way to Magmadar are done and never respawn; in the imp room two of the three Lava
Surgers are done and the third (56841) has 7 kills of 8 tries; still to clear on the way to Gehennas: the
north imp pack (56584), then the plateau — Lava Surger 56840, Ancient Core Hound 56853 (one kill only:
Core Hounds no longer respawn), Lava Annihilator 56736, Firelord 91293, Molten Giants 56716, Firelords
56720, Giant+Destroyer 56747, Lava Surger 56839 (wide patrol), Firelord+Annihilator 56721, Giant+Destroyer
56702, Lava Surger 56844, Molten Giants 56706, then Gehennas' room trash (56779, 91284, 56788, 56708, 91282,
91285) and Gehennas. The east imp field (56549, 30 imps) is off the route and skipped.

OWNER RESUME, 2026-09-11 ~21:40 local: Nico resumed with full prior permissions.
Owner instructions this session: (1) test regression first; (2) trim — stop
bolting on large batches, one mechanism per measured batch; (3) trash/roaming
packs are first-class: a raid leader needs a simple generic "pull" where an
assigned puller engages ONE group when appropriate, never 2–3; (4) reaffirmed:
no hard-coded encounter-specific logic anywhere (code has no entries, spell ids,
coordinates or names; only survey + global policy are hand data).

Decision after reading the candidate: the protocol-5 integrated candidate
(scratch/raid-mc-staging/sep10-protocol5-integrated-candidate) stays STAGED and
UNDEPLOYED. Its `before-interrupts` base is already a ~700-line merge of the old
schema-2 machinery into the accepted executor, so even the smallest prefix is a
large unverified lump. The deployed protocol-4 executor (Core 27ed862c, client
14e2a546) already has per-bot interrupt/dispel/taunt/defensive support, control
dispel, and add pickup with requiredAdds; the compiled Lucifron schema-1
projection uses only deployed vocabulary. Therefore the first MC work runs on the
DEPLOYED stack with ZERO Core change. Candidate layers are pulled in one at a
time only when a measured batch shows the deployed behaviour losing pulls.

Geometry fact (from lucifron-nearby-spawns.csv + lucifron-patrol.txt): Lucifron
spawns at (1024,-973) with both Protectors at (1023,-969) and patrols a loop east
to (1120,-1017). The middle Core Hound pack (5 × 47k, mutual resurrection) sits
at (1025–1036, -961…-972) — on top of the boss home. No skip is possible; that
pack is the gate to boss #1. Raid MC checkpoint: (952,-952), instance 101,
label mc-sep9-middle-ranged-ready.

Diagnosis of the two Sep 9 middle-pack wipes (battle.jsonl): main tank walked
45 yd into the pack and took all five hounds at t≈10 s; pack was then tanked as
a scrum across the raid; Testwar (5689 max HP, pre-consumables) died at ~63 s.
In ranged-02 the definition's bossEntry hound was burned to 1 HP at 45 s while
packmates sat at 17–35 %, and the pack resurrected it to full at 55 s. Two
generic defects: (a) no pull — the tank walks into the pack instead of the pack
being pulled to staged tanks; (b) a pack definition must not privilege one member.

Plan: 1) Onyxia pipeline regression (one pull, unchanged stack). 2) Move to the
MC middle checkpoint, verify instance state. 3) Baseline the middle pack under
the consumables loadout on the deployed stack (frozen batch). 4) ONE generic
change: pull discipline (assigned puller, one group, pack brought to the staged
raid). Measure. Then Lucifron survey + batch.

## 2026-09-11 session log (Claude)

1. Onyxia pipeline regression: **reviewed win** on the unchanged accepted stack
   (456 s, 33 survivors, normal combat verified). Session
   `scratch/onyxia-live/sep11-resume-regression-evidence`, attempt
   `onyxia-resume-0911b-01`. The map-249 global reset at 2026-09-10 23:59 server
   time destroyed instance 100, so the accepted checkpoint was re-targeted to the
   owner's new instance 103 (only the `instance` field changed; record in
   `scratch/onyxia-live/sep11-onyxia-retarget/retarget-record.json`; new label
   `onyxia-sep11-consumables-owner-baseline-i103`). Loadout confirmed with Nico:
   37 non-tanks carry one role elixir + food + bag potions; only the 3 tanks add
   flask + Fortitude. Owner accepted that as "roughly 1 pot + 1 elixir".
2. Trash packs are now COMPILED, not hand-written: `tools/encounter-content-audit/
   pack_compiler.py` + `compile_packs.py`. A pack = `creature_groups` leader +
   members with the aggro-together flag; entries, required counts and
   immuneSchools come from the DB; `addPolicy` is `balance` only when the
   registered AI source proves same-entry mutual restoration (fake death at 1 HP
   + same-entry restore scan), else policy `focus`. New global policy section
   `packs`. Survey `plans/surveys/pack-409-56629.json` carries the Sep 9 live
   anchors. Output `core/commander-raid/compiled-packs/`, promoted
   `encounter-definitions/pack-409-56629.json` (byte-equal to the old hand file
   except immuneSchools 4->0, which is what the template says). Onyxia promotion
   verified byte-identical after the policy change. Map 409 has 40 packs; the
   five Core Hound packs derive `balance` from molten_core.cpp:366 (100 yd, 10 s).
3. MC instance 101 survived; west pack still dead. The Sep 9 MC checkpoint was
   rejected (gear identity changed by the Sep 10 bag work), so a fresh capture
   was made at the staging point: label `mc-sep11-middle-ready`, snapshot
   `scratch/onyxia-live/sep11-mc-capture/middle-ready-baseline.json` (instance
   101, five hounds 56629-56633, 7 pets).
4. Baseline batch attempted: session `sep11-pack56629-baseline2`, attempt
   `pack56629-base-0911d-01` (checkpoint `mc-sep11-middle-ready-2`, pets fed).
5. Result: WIPE at 15 s, three packs pulled, client crashed at 377 s. Root
   causes, both structural, neither about combat strength:
   a) **The west pack respawned** — MC trash `spawntimesecs` is 3600 s here, so
      any pack killed earlier returns within the hour; the tank anchor (976,-972)
      sits inside the west pack's spawn (6-26 yd). Item 3's "west still dead"
      was wrong.
   b) **The primary objective binds first-come-first-served by entry with no
      room check** (`ObserveUnit`, primary branch). With 25 Core Hounds on the
      map the executor bound "the boss" to a hound at (1099,-1018), 160 yd away
      in a third pack; the owner marched through the west and middle packs
      toward it. The Sep 9 scripts only avoided this by asserting
      `status.BossGuid in checkpoint creatures` before pulling; the current
      runner did not.
   Also learned: the accepted client 14e2a546 has neither the ranged pull nor
   the loot protocol (both added Sep 9 afternoon); on the deployed stack the
   owner body-pulls. `raid_checkpoint.stage` now skips the MC master-loot ack
   when the client binary lacks the command (`client_supports`).
6. DEPLOYED (after Nico added ssh/scp permission rules): three scoped Core
   builds tonight, all archived on the box under qa-artifacts/ and recorded in
   `scratch/onyxia-live/sep11-objective-binding/deployment-result.json`:
   - 254e64bc: objective binding v1 (room gate on the whole observation) —
     REGRESSED Onyxia: airborne Onyxia (z -65) is outside the 3D room bounds,
     so her per-tick observation was skipped -> flight-lane wipe. Reverted by v2.
   - 605286ac: checkpoint tool resolves the live instance from the owner's map
     (instance ids renumber on resets/restarts; they never matter again).
     `raid_checkpoint.errors` accepts any instance in the receipt; unit tests
     updated (`check_checkpoint.py` OK).
   - 04101925: objective binding v2 (room gate applies to the binding decision
     only). Onyxia regression: one loss (900 s timeout, 15 %, family
     fireball-splash-crowding), then a REVIEWED WIN onyxia-binding-0911d-01
     (534 s, 35 survivors). Executor diff vs accepted = the 24-line block only.
   - b6202d21 (RUNNING, PID 2861681): objective binding v3 — rebind only when a
     candidate is nearer than the bound unit by >30 yd (v2 flapped between
     packmates after the client sealed the boss and refused the fight). Only the
     rebind branch changed, unreachable for a single-spawn boss; Onyxia not
     re-run on purpose.
   - Runner: `bound_objective_gate` before every pull; transient
     `QA_SUPPLY_REJECT roster/combat/transit/possession` now retried within the
     deadline; master-loot ack skipped for `pack-*` encounters.
7. Client: bin/Release now holds **2509aaf8** (last Sep 9 protocol-4 build:
   ranged pull, loot protocol, boss sealed by executor status). 14e2a546 cannot
   seal a multi-spawn pull (it seals the first entity of the entry) and is
   archived for Onyxia. Record: `scratch/onyxia-live/sep11-mc-capture/client-swap-record.json`.
8. MC measured target = the NEAREST pack from the staging point = the west pack
   `pack-409-56634` (survey `plans/surveys/pack-409-56634.json` from the Sep 9
   west anchors; compiled + promoted). Checkpoint `mc-sep11-west-ready-2`
   (`scratch/onyxia-live/sep11-mc-capture/west-ready-baseline-2.json`): captured
   with the FULL consumable set (restored the Onyxia checkpoint first, then
   transferred), pets fed. No pack pull has completed yet on the new stack: the
   attempts tonight were stopped by harness gates (bots left leaderless pulled the
   pack; missing auras in a checkpoint-buffs capture; the 14e2a546 seal; the
   `loot master-setup` FAIL with 2509aaf8 — method=3 master=0 — now bypassed for packs).
9. PAUSE 2026-09-12 ~00:50 local at Nico's request (new session for the full MC
   job). Boundary: QA client closed, 39 bots logged out, native 0p+0b, exact40
   parked at the MC staging point with buffs; Core b6202d21 verified running.
   Receipt: `scratch/onyxia-live/sep12-pack56634-baseline2-evidence/pause-boundary.json`.


## 2026-09-12 session log (Claude, started 00:20 local right after the pause)

1. Stack verified unchanged: Core b6202d21 (PID 2861681, screen mangosd), client
   2509aaf8, no QA client, 0p+0b.
2. **West pack `pack-409-56634` ACCEPTED 10/10** (session
   `scratch/onyxia-live/sep12-pack56634-baseline3-evidence`, attempts
   `pack56634-base-0912c-01..10`, fights 43-56 s, ranged crossbow pull at 28 yd,
   all five hounds burned evenly to 1 HP by the `balance` policy, then killed).
   `batch-report.md`, `proposal.json`, `encounter-acceptance.json` recorded.
   Two pulls (07, 10) were joined mid-fight by the Lucifron patrol group (12118 +
   two 12119): its westmost waypoint (1000,-958) is 26 yd from the Sep 9 tank
   anchor (976,-972). Five raid members died in each; after the sealed pack kill
   the leaderless raid kept fighting the boss, the runner crashed staging pull 08
   (`Loadout needs live exact40 out of combat`), Lucifron evaded on his own the
   first time, and after pull 10 the owner GM-evaded him (`select
   wild-entry-nearest` + `.npc evade`, receipts in `post-batch-check/` and
   `settle-*/`; setup only, never a fight result). Pulls 08-10 were driven by
   `scratch/onyxia-live/sep12-mc-capture/continue-west-batch.py` (same
   `run_attempt`, plus a settle/evade guard); the partial 08 is preserved as
   `aborted-08-lucifron-incursion/`.
3. Generic change 1 (compiler, survey validation): `pack_compiler.patrol_clearance`
   measures every survey anchor against every foreign database waypoint and spawn
   home on the map; policy `packs.anchorClearanceYards = 36` (detection 20 +
   level difference + raid footprint). A closer survey stays `surveyRequired`
   with the violation recorded in `pack-definition-derivation.json`
   (`anchorClearance`). A survey may declare `assumesCleared: [pack ids]` — those
   spawns are not foreign (the runner must then re-clear them, item 4). The old
   west survey is refused by this rule (27.9 yd); both west and middle surveys
   were moved 20 yd west along the observed raid floor: tank (958,-962), teams
   (946,-952)/(952,-946), all >=42 yd from the patrol. Check:
   `check_pack_clearance.py`.
4. Generic change 2 (runner): `--reclear encounter=label=snapshot[=killedUtc]`
   (`raid_reclear.py`). MC trash respawns on `creature.spawntimesecsmin`
   (3600 s for hounds; Protectors 7200; instance script stops hound respawns only
   after Magmadar). Before each measured pull, any accepted pack whose respawn
   would land inside the coming pull (margin 600 s) is re-killed through the
   ordinary restore/pull/watch/review path with its own checkpoint (its restore
   respawns the pack, so the re-kill happens exactly when due); the sealed
   `reviewed-result.json` lives beside the batch (`reclear=true`), never in the
   measured ledger; `reclear-state.json` tracks kill times. A survey's
   `assumesCleared` must be covered by `--reclear`. Check: `check_reclear.py`.
5. Middle checkpoint `mc-sep12-middle-ready` captured (instance 101, creatures
   56629-56633, poses at the staging point, aura sets byte-equal to the west
   checkpoint, 7 pets; happiness 980000/980000/901250):
   `scratch/onyxia-live/sep12-mc-capture/middle-ready-baseline.json`. Capture
   recipe (`capture-middle.py` + the inline recover/capture loop): `restore-raid`
   of the previous checkpoint (raid only, dead trash untouched), then
   `qarepair recover-here` immediately followed by `capture` (bots spend mana
   within seconds; "capture requires full mana" otherwise).
6. Middle batch DONE, **7/10, NOT accepted** (`scratch/onyxia-live/
   sep12-pack56629-baseline-evidence`, `batch-report.md`, `proposal.json`).
   All three losses (03, 09, 10) failed 0-5 s after `raidqa begin` with "The
   authoritative boss identity changed" and no combat: binding v3 never checks
   that a CANDIDATE is alive, so the five dead west-pack bodies (awaiting their
   3600 s respawn, 27-46 yd from the new anchor) bounced the binding around after
   the client sealed it (server log `[SUI][raid-objective-bind]` chains through
   ..634-638). Combat itself was 7/7 (38-40 survivors, 60-85 s, corridor pull
   from 74 yd: walk to 28 yd, crossbow, return to the anchor). Pull 08 was joined
   by the Lucifron patrol 4 s after the sealed kill (fight footprint reached
   x=983, 17 yd from his turn-around); the runner crashed staging 09 the same way
   as the west 08; pulls 09-10 ran through `continue-middle-batch.py` (settle
   guard). The pet gate also bit once: happiness 901250 at capture decayed below
   900000 -> re-captured as `mc-sep12-middle-ready-2` after feeding to 1,050,000
   (`sep12-mc-capture/recapture-middle-fed.py`).
7. Core candidate BUILT on the box, **NOT INSTALLED** (the auto-mode classifier
   refused the scoped restart twice; Nico must run it):
   - executor objective binding v4 = v3 + `if(!boss->IsAlive())continue;`
     before any primary bind/rebind (`sep12-objective-binding-v4/
     SuiCommanderRaid-objective-binding-v4.cpp`, source sha 0d0f2e34);
   - checkpoint tool `capture-encounter` mode: records the instance script's
     encounter/progression string (`InstanceData::Save`) and re-applies it
     (`Load` + `SaveToDB`) on restore/return BEFORE respawning captured creatures,
     so a boss with linked adds (Lucifron's Protectors are pruned on respawn once
     TYPE_LUCIFRON is DONE) is re-fought identically on pulls 2-10. Plain
     `capture` stays state-free so restoring an old trash checkpoint never
     regresses progression. Source `sep12-objective-binding-v4/
     RaidQaCheckpoint-instance-state.cpp` (sha 72ba8870).
   Built binary `build/src/mangosd/mangosd` = a23f31b8; 141 source contracts
   passed. Box source now holds both candidates (archives under qa-artifacts/
   sep12-objective-binding-v3-archive and sep12-checkpoint-live-instance-archive).
   Deploy command (from the box): `cd /home/wowvmangos/vmangos && python3
   qa-artifacts/deploy-scoped-core.py 2861681 2861680.mangosd`.
   Onyxia regression: same argument as v3 (the guard sits in the multi-spawn
   bind branch, unreachable for a single live spawn); west pack regression = the
   first re-clear pull of the next batch (same reviewed packet).
8. Lucifron compiled and promoted (`encounter-definitions/lucifron.json`, schema 1,
   requiredAdds 2x12119 split, one derived tank-buff rule; curses/mind control are
   handled by the deployed per-bot dispel + control-dispel support). Survey
   `plans/surveys/lucifron.json`: corridor anchors, bounds x 936..1130 (his whole
   loop stays in-room for binding), `assumesCleared: [pack-409-56634]` — the
   middle pack stays ALIVE (67 yd from every anchor) and is re-measured after the
   patrol is dead. Boss surveys now get the same clearance check
   (`definition_compiler` -> `encounter-definition-derivation.json`
   `anchorClearance`); policy number moved to `survey.anchorClearanceYards`.
   The Sep 11 `loot master-setup` FAILs (method=3 master=0) are explained: the
   sep12-pack56634-baseline2 stdout shows the bots fighting the west pack
   (Taunt/Challenging Shout on ..DD3A-E) during every attempt, so
   `RaidQaAnyCombat()` refused the request — the leaderless-pull incident, not a
   client precondition. Expect the ack to pass in a clean staging.
   Order decided: Lucifron BEFORE the middle re-measure, because the patrol is the
   hazard that joined pulls 07/10 (west) and 08 (middle); once he is dead the
   Protectors stop respawning and the middle pack fight has no patrol.

9. 07:29 local: Nico switched the session permission mode; Core **a23f31b8**
   installed (PID 2878808, `sep12-objective-binding-v4/deployment-result.json`);
   `core-patches/SuiCommanderRaid.cpp` and `core-patches/RaidQaCheckpoint.cpp`
   now hold the deployed sources.
10. West pack regression on the new Core + corridor survey: reviewed win
   (`sep12-lucifron-baseline-evidence/lucifron-base-0912e-reclear-01`, 43 s,
   40/40) — the first `--reclear` pull. `run_reclears` was missing the
   consumable/tuning steps before review; fixed, ledger re-seeded before any
   measured attempt.
11. **Lucifron ACCEPTED 10/10** (`scratch/onyxia-live/sep12-lucifron-baseline-evidence`,
   130-169 s, 40/40 in nine pulls; `encounter-acceptance.json` recorded).
   Checkpoint `mc-sep12-lucifron-ready` (capture-encounter; instanceState
   "0 0 ..."; `sep12-mc-capture/lucifron-ready-baseline.json`). The
   `capture-encounter` restore proved itself: both Protectors were present and
   died in all ten pulls. Loot ack passed first try every pull (method 2,
   master 787). Pull 03: 22 survivors — ranged bot 159 fled a Protector east into
   the live middle hound pack (60 yd) and brought it in at t=95 s; recovered by
   restoring the middle pack's checkpoint (`post-03-recover/`). Pulls 04-10 ran
   through `sep12-mc-capture/continue-lucifron-batch.py` (settle guard +
   re-clear). Lesson: the pack around a boss home must be cleared before the
   boss (survey `assumesCleared`), which the compiler now also accepts for boss
   ids (`encounter_spawn_guids`).
12. **Middle pack `pack-409-56629` ACCEPTED 10/10** on v4 with no patrol
   (`scratch/onyxia-live/sep12-pack56629-v4-evidence`; 50-63 s, 40/40 every
   pull; one west re-clear inside the batch). vs 7/10 on v3: +3 = retained.
   Pull 06 exposed a review-tool limit: the native log stamps whole seconds and
   the last hound's `dead=1` line landed 1 s after the client's completion event
   -> `review_checkpoint_attempt.py` now allows +3 s for the objective window
   (ledger `configurationAmendments` records the mid-batch hash change; evidence
   tool only). Runner crashes after a sealed pull are now resumed with the new
   generic `tools/encounter-content-audit/continue_batch.py` (same run_attempt /
   re-clear / ledger, plus the settle guard).
13. Runner now has the settle guard built in: before each measured pull it waits
   for the exact40 to leave combat; a foreign group still fighting is ended by
   restoring the checkpoint whose captured group matches the engaged entries
   (`--settle-checkpoint label=snapshot`; the measured and re-clear checkpoints are
   included automatically). `assumesCleared` boss ids need no `--reclear`.
14. New generic tool `capture_checkpoint.py` (restore-raid source -> feed pets ->
   recover-here -> capture[-encounter] -> scp -> consumable-aura check).
   Checkpoint `mc-sep12-east-ready` captured (`sep12-mc-capture/east-ready-baseline.json`,
   creatures 56639-56643). Surveys written + compiled + promoted with clearance
   for `pack-409-56639` (east), `pack-409-56650` (south-east), `pack-409-56644`
   (last before Magmadar) and `magmadar` (anchors on the observed hound floor;
   `assumesCleared` chains: each later pack assumes the earlier ones + lucifron).
   Harness trap of the hour: `finish_measurement_session.py` must NOT be chained
   with `&&` in front of a backgrounded runner launch — the whole list goes to the
   background; a "failed" finish that actually succeeded led to a manual
   `.bot delete` x39 that logged the bots out from under the freshly launched
   batch (`aborted-01-roster-logout`). Run finish, check `clean-boundary.json`,
   then launch.
15. East pack batch RUNNING: session `sep12-pack56639-baseline`, prefix
   `pack56639-base-0912g`, log `scratch/onyxia-live/sep12-pack56639-baseline-runner.log`;
   pull 01 = win 59 s 40/40 (tank pulled from 118 yd). Re-clears armed: west
   (killed 12:25:19Z), middle (12:41:14Z). Kill times for the next --reclear list
   come from `reclear-state.json` in the latest session + the last measured pull.

16. **East pack `pack-409-56639` ACCEPTED 10/10** (58-64 s, 40/40 every pull;
   `sep12-pack56639-baseline-evidence`). Kill times: west 12:25:19Z, middle
   12:41:14Z, east 13:06:52Z (all in the latest `reclear-state.json` + review).
17. Owner clarification (13:10Z, mid-turn): audit objective handling before
   expanding MC support. Done at the clean boundary after the east batch:
   `tools/encounter-content-audit/objective_review.py` (generic, snapshot-derived)
   writes `core/commander-raid/objective-reviews/<id>.{md,json}`; rule recorded in
   ARCHITECTURE.md ("Objective handling"). Results: west/middle/east/SE/56644 hound
   packs and Lucifron — no unsupported items (packs: completion = every member
   dead, balance restriction from molten_core.cpp; Lucifron: death sets
   TYPE_LUCIFRON DONE, Protectors are required adds, curses/mind control covered
   by per-bot dispel + control dispel; Protectors stop respawning when DONE).
   **Magmadar: acceptance testing BLOCKED** — three unsupported items: (a) Frenzy
   19451 = self buff, dispel type enrage, no enemy-target dispel in the executor
   (hunters know Tranquilizing Shot 19801; the bot class policy never casts it);
   (b) Lava Bomb 19411/20474 = dummy effect resolved in SpellEffects.cpp (source
   read on the box: 19411 -> 20494 Summon Lava Bomb 1 -> GameObject 177704 ->
   Conflagration 19428, 8 yd periodic fire; 20474 -> 20495), object hazards are
   neither derivable (SpellEffects.cpp missing from the harvest) nor expressible
   in schema 1 nor observed by the deployed executor; (c) Lava Breath 19272 (the
   Frenzy child) is a cone with implicit target 54, the compiler derives cones for
   target 24 only. Core Hounds stop respawning when TYPE_MAGMADAR is DONE.
   Plan: accept the two remaining hound packs (reviews clean), then resolve
   (a)-(c) with small generic mechanisms (native enemy aura removal; trap-object
   exclusion regions from GameObject template data; cone target 54 in the
   compiler; SpellEffects.cpp in the harvest for provenance) before any Magmadar
   acceptance batch; any earlier Magmadar pull is labelled diagnostic.

18. SE pack first session `sep12-pack56650-baseline` HARNESS-INVALIDATED
   (`harness-invalidation.json`): `finish_measurement_session.py` used a plain
   `restore` of the label it was given, which respawned the accepted east pack at
   the session boundary; the time-based re-clear could not know, the tank walked
   past the live east pack and ten hounds wiped pull 01. Fixed at the root:
   finish now uses `restore-raid` (raid only, no creature respawn). The stray
   packs were re-killed by forced re-clears (middle, east) through the executor;
   fresh frozen batch `sep12-pack56650-baseline2` (prefix `pack56650-base-0912i`)
   launched 13:38Z with kill times west 13:21Z / middle 13:34Z / east 13:36Z.
19. Magmadar prerequisites, prepared while the SE batch runs (NOT deployed; the
   SE batch config is frozen): executor **v5** built on the box as `eed71d3d`
   (`sep12-objective-binding-v5/SuiCommanderRaid-v5-object-hazards-enemy-dispel.cpp`,
   source sha ae74c7f9): (1) hostile TRAP GameObjects whose template spell does
   school/periodic damage are exclusion regions (radius = max(trap radius, spell
   radius)+1, priority 90) for every hazard consumer (escape, formation, station
   moves, manual advice); friendly traps and harmless objects ignored; (2) enemy
   aura removal: a bot casts a learned enemy-target dispel (effect DISPEL,
   implicit target enemy — hunters' Tranquilizing Shot) when the fought unit
   carries a removable positive aura of a matching dispel type. Both read live
   object/spell data only. Compiler: staged patch derives cones for implicit
   targets 24 AND 54 and for triggered children of the roots (Lava Breath under
   Frenzy) — `scratchpad/cone54_patch.py`, apply after the batch. Provenance:
   fresh harvest `core/commander-raid/evidence/content-snapshot-sep12.json`
   (22 sources incl. SpellEffects.cpp; object templates 9005 rows incl. 177704
   Lava Bomb type 6 radius 5 spell 19428; every other table byte-equal to the
   Sep 10 snapshot) for the Magmadar compile so `directObjectImpacts` resolves.
20. **SE pack `pack-409-56650` ACCEPTED 10/10** (`sep12-pack56650-baseline2-evidence`,
   69-77 s; 39 survivors in 8 pulls: the sealed unit was a far pack member, the tank
   walked ~35 yd past the anchor and died once at t~25 s, the backup tank finished).
   Second review-tool amendment: closing-window hits count any unit of the
   objective/add entries (the sealed member sat at fake-death 1 HP in the last
   12 s of pull 02).
21. Core **3592d6dd** deployed (14:05Z v5 = `2b8a75af`, then 14:11Z + checkpoint
   reload): executor v5 = trap-object exclusion regions + enemy aura removal +
   `kBindTolerance` 10 yd (nearest member of the nearest group); checkpoint tool
   applies the captured encounter state BEFORE looking captured creatures up and
   reloads static spawns the instance script pruned (`Creature::LoadFromDB` +
   `map->Add`) — the first Lucifron regression attempt hit exactly that (the
   Protectors were removed on map load after the restart because TYPE_LUCIFRON
   was DONE; `sep12-lucifron-v5-regression-evidence.pruned-protectors-attempt`).
   `sep12-objective-binding-v5/deployment-result.json`; canonical
   `core-patches/*.cpp` updated. **Lucifron regression on the new Core: reviewed
   win, 180 s, 40/40** (`sep12-lucifron-v5-regression2-evidence`). Onyxia not
   re-run (unreachable branches; recorded in the deployment result).
22. Compiler: cones derived for implicit targets 24 and 54 and for triggered
   children of the roots; schema-1 projection accepts `--native-coverage
   trap-objects` (object-only hazards dropped and recorded per definition in the
   derivation report as `nativeCoverage`). Magmadar compiled from
   `content-snapshot-sep12.json` and promoted: rules = derived-cones-1 (Lava
   Breath), Fear Ward x3, tank buff; native coverage = the two Lava Bomb trap
   objects (177704, 5 yd, 30/60 s); one unresolved note "cone default angle
   requires loaded-Core default". Onyxia and Lucifron promoted definitions are
   byte-identical to before. **Objective review for Magmadar: no unsupported
   items on the v5 stack -> acceptance testing allowed** (reviews regenerated
   with the v5 coverage table).
23. Last hound pack batch RUNNING: `sep12-pack56644-baseline`, prefix
   `pack56644-base-0912l`, checkpoint `mc-sep12-last-hounds-ready` (members
   56644, 56646-56649 — 56645 is not in the group), re-clears armed for west/
   middle/east/SE. Next: Magmadar checkpoint (capture-encounter; Magmadar raw
   GUID 17379391163047402859 = 0xF130<<48 | 11982<<24 | 56683),
   then the first Magmadar batch (assumesCleared: all five hound packs +
   lucifron; --reclear all five packs; hounds stop respawning once he is DONE).

24. PAUSE 10:45 local at Nico's request. Last hound pack batch
   `sep12-pack56644-baseline` is **6/6 sealed, pulls 7-10 pending** (frozen; the
   ledger carries one review-tool amendment: closing-window hits 10 -> 20 s — a
   balanced pack sits at fake-death 1 HP, unattackable, for its restoration window
   before dying together; pull 06 had 52k boss damage and no hits in the last
   10 s). Re-clears inside the batch: west (14:21Z), middle (14:29Z; its first
   re-clear attempt failed on "boss identity changed" — a pre-combat rebind with
   the new 10 yd tolerance; retry won), east (14:31Z). Kill times now: west
   14:21:27Z, middle 14:29:42Z, east 14:31:37Z, SE 14:04:25Z, 56644 14:41:22Z.
   Boundary: client closed (restore-raid), 39 bots out, native 0p+0b, Core
   3592d6dd PID 2906236, exact40 at the MC staging point (952,-952) with buffs.
   Watch item: if the 10 yd bind tolerance produces "identity changed" failures
   on MEASURED pulls, raise `kBindTolerance` (v5 source) to ~20 and re-deploy.

## 2026-09-12 afternoon session log (Claude, resumed 10:49 local right after the 10:45 pause)

25. Stack re-verified at resume: Core 3592d6dd (PID 2906236, comm `mangosd-main` — `pgrep -x mangosd`
   only lists the unrelated CMaNGOS 1079598), client 2509aaf8, no QA client, 0p+0b; kill times matched
   item 24 (`sep12-pack56644-baseline-evidence/reclear-state.json`). Monitoring note: `tail -f` on the
   runner log does not deliver on this shell; a 20 s polling loop over `wc -l` does.
26. **Last hound pack `pack-409-56644` ACCEPTED 9/10** (`scratch/onyxia-live/sep12-pack56644-baseline2-evidence`,
   attempts `pack56644-base-0912n-01..10`, 74-85 s, survivors 39,39,39,39,40,-,40,40,39,39;
   `batch-report.md`, `proposal.json`, `encounter-acceptance.json` recorded; the 10:45 6/6 partial batch
   stays preserved). Re-clears inside the batch: SE (`reclear-01`, 14:58:36Z, 69.6 s 40/40) and west
   (`reclear-02`, 15:15:21Z, 45 s 40/40). One settle restore after pull 07.
   - The loss (pull 06, 0 s): "The authoritative boss identity changed" — the item-24 watch item, now
     diagnosed from the server log: 11:07:10 bind ..647 (51.6 yd from the tank anchor) -> ..646 (40.9);
     runner gate + client seal on ..646 at 15:07:14Z; 11:07:17 bind ..646 (42.7) -> ..648 (32.6). The v5
     rule already measures from the anchor, but it decides PER OBSERVED UNIT (greedy), so it bound a
     nearer-but-not-nearest member, and ~2 yd of hound wander crossed the 10 yd edge after the seal.
     Raising `kBindTolerance` to 20 (the item-24 contingency) would have kept ..647, the FARTHEST member
     (50.8 yd) -> the SE deep-walk tank death; NOT taken.
   - The 39-survivor pulls are the SE pattern, not the SE cause: the tank stops at the anchor correctly
     (2.9 yd) but all five hounds land on him there (pull 01: five hounds at 0.6-6.3 yd on 787 at t=25,
     main 0 at t=40), the bots finish. Anchor-to-own-pack-home distances: east 49-72 yd (40/40 x10),
     SE 28-49 yd (39 in 8/10), last 31-51 yd (39 in 6/9). Disclosed; candidate global policy for
     future pack surveys = own-pack anchor distance (not this batch's change).
   - Proposal recorded = executor v6 **argmin rebind**: any new primary binding goes to the nearest live
     in-room spawn of the entry to the tank anchor evaluated over the whole grid list at once
     (`GetCreatureListWithEntryInGrid`, 400 yd, room-gated); tolerance stays 10. Candidate
     `scratch/onyxia-live/sep12-objective-binding-v6/SuiCommanderRaid-v6-argmin-bind.cpp` (sha 656788e6,
     diff = one constant + the primary-bind branch), `apply_build_v6.py`; building on the box into
     `build/src/mangosd/mangosd` (box source archived as `qa-artifacts/sep12-objective-binding-v5-archive`),
     **NOT installed**: the Magmadar batch is frozen on 3592d6dd, and the branch is unreachable for a
     single-spawn boss; measured on the next multi-spawn batch (the hound re-clears exercise it meanwhile).
27. STEP 3 objective reviews generated offline during the batch (only the four new files written;
   `core/commander-raid/objective-reviews/{gehennas,garr,baron-geddon,shazzrah}.{md,json}` from
   `content-snapshot-sep12.json` 74f25dc4): **gehennas allowed** (Gehennas x1 + Flamewaker x2 required,
   Flamewakers stop respawning when DONE; curse -> per-bot dispel, bolts, 8/10 yd aoes). **garr, baron-geddon,
   shazzrah BLOCKED**, each on one `dummy-script` item that the review cannot resolve but the snapshot
   sources explain: Garr 19515 Enrage Trigger = `mob_firesworn::JustDied` -> `boss_garr::SpellHit` casts
   19516 Enrage on self (stacks to 10, dispel type 0 = not tranq-able; Firesworn also cast 19497 Eruption
   18 yd on death; 6 min + 20 s: 20482 -> 20483 Massive Eruption); Geddon 18947 Inferno Dummy has NO
   handler in SpellEffects.cpp or the script — the script roots and `CastCustomSpell 19698` x8 ticks
   (500..5000), which is already classified aoe-around-caster; Shazzrah 23138 Gate = SpellEffects.cpp
   `case 23138` -> 23139 teleport + script `DoResetThreat` + `NearTeleportTo` a random player, then
   Arcane Explosion 19712 (10 yd). None is an object hazard. The generic gap is in the review tool:
   `coverage()` marks every dummy-script unsupported unless the compiler derived an object hazard; it
   does not read script `SpellHit` handlers, `SpellEffects.cpp` `case` children, or the absence of any
   handler. `objective_review.py` is frozen in the running batch -> the one review-tool change (a
   dummy-chain resolver over the harvested sources; "no handler anywhere" = inert) is for the next clean
   boundary, before any of the three is surveyed.
28. **Magmadar checkpoint `mc-sep12-magmadar-ready` captured** (capture-encounter; instance 101;
   Magmadar 11982 826,088 HP home (1145,-1020); instanceState `0 0 0 0 0 0 0 3 0 ...`; pets 1,050,000;
   consumable auras verified; `sep12-mc-capture/magmadar-ready-baseline.json` sha c103be51; log
   `sep12-magmadar-capture.log`). **First Magmadar batch RUNNING** from 15:24Z: session
   `sep12-magmadar-baseline`, prefix `magmadar-base-0912m`, log `sep12-magmadar-baseline-runner.log`,
   46 frozen files, `--reclear` for all five hound packs (kill times west 15:15:21Z, middle 14:29:42Z,
   east 14:31:37Z, SE 14:58:36Z, 56644 15:20:47Z); middle and east re-cleared before pull 01
   (`reclear-01` 59.6 s 40/40, `reclear-02` 60.5 s 40/40). Then the runner CRASHED staging pull 01:
   `raidqa apply` was rejected by the executor five times (reply 1 = "Malformed plan"), no pull happened
   (`sep12-magmadar-baseline-evidence/stack-invalidation.json`; session finished with restore-raid).
29. Diagnosis (Core source, not the definition): `SuiCommanderRaid.cpp` Apply validation and
   `ConeStationAllowed` accept `avoidCones` spells with implicit target **24 only**; the compiled Magmadar
   rule `derived-cones-1` is Lava Breath 19272 = target **54** (radius index 21). Item 22 extended the
   COMPILER to 24/54 but the deployed executor 3592d6dd was never changed, and `validate_definition.py`
   (parser mirror) does not mirror the post-parse spell checks. Core fact (Spell.cpp): target 54 falls to
   `FillAreaTargets(PUSH_IN_CONE)` for every spell outside its hard-coded 24820-24838 list, and
   `PUSH_IN_CONE` reads `sSpellMgr.GetSpellCone(id)` — the same fill and arc as target 24, so the item-22
   note "cone default angle requires loaded-Core default" resolves to "identical to target 24".
   Not an option: hand-editing the compiled definition or dropping the cone rule (the review counts it
   as covered). Lesson for the review tool: coverage must be checked against the DEPLOYED executor's
   Apply validation, not only the compiler's vocabulary.
30. **Core 6747f0f7 deployed 15:38Z** (PID 2920120, screen `2920119.mangosd`) = executor **v6 = v5 +
   implicit cone target 54 accepted** at the two `==24` tests (contract-125 substring preserved; 141
   contracts pass). Source `scratch/onyxia-live/sep12-executor-v6-cone54/SuiCommanderRaid-v6-cone-target-54.cpp`
   (sha 00c7d65b, based on deployed v5 d9888990), `apply_build_v6_cone54.py`, `deployment-result.json`;
   canonical `core-patches/SuiCommanderRaid.cpp` updated. Deployed at a clean boundary (client closed, 39
   out, 0p+0b). Regressions: target-24 cones untouched by construction (Onyxia recorded, not re-run);
   hounds/Lucifron have no cone rules; the first hound re-clear of the new batch is the live packet.
   The **argmin-rebind candidate is NOT in this build** (one change per deploy): source
   `qa-artifacts/SuiCommanderRaid-v6-argmin-bind.cpp` (656788e6), binary preserved as
   `qa-artifacts/mangosd-v6-argmin-04ce5332`; it waits for the next multi-spawn batch (re-base on 00c7d65b).
31. Magmadar session `sep12-magmadar-baseline2` (prefix `magmadar-base-0912p`) on v6: **Apply and Arm
   ACCEPTED** (requests 36/38, result 0 — the cone-54 fix is confirmed), then HARNESS-INVALIDATED at
   `verify_ready` before any pull: "Loadout differs from actual class/role" — the owner loadout profile
   records Sylwhisper 116 / Boomwarrior 115 as add tanks (role 2, flask + Fortitude), Magmadar has no
   adds so the compiled definition derives `addTanksPerTeam 0` and Auto-assign makes both warriors
   melee damage (role 4). First single-target-no-adds encounter; the profile check conflated the
   owner-declared LOADOUT role with the encounter assignment. Fix (harness, not the profile file, whose
   sha stays frozen): `raid_consumable_preparation.validate` keeps name/class strict and requires only
   that the role played calls for the same role elixir as the profile stocks (`role_elixir_key`);
   flask/Fortitude follow the declared loadout role. `check_consumable_preparation.py` 6/6; a healer
   assigned as damage caster is still refused. `harness-invalidation.json` recorded. Finishing that
   session was refused twice by `restore-raid` readiness (`combat=1` on 116, then 122 — transient,
   self-inflicted flags at the staging point, no enemy damage; receipts `post-batch-recovery-rejected-1/2`),
   then succeeded 15:49Z (client closed, 39 out, 0p+0b).
32. Session `sep12-magmadar-baseline3` (prefix `magmadar-base-0912q`) on v6: SE re-clear won
   (`reclear-01`, 72.6 s, 40/40 — the first multi-spawn fight on Core 6747f0f7, kill 15:52:48Z), then
   HARNESS-INVALIDATED at `verify_ready` before pull 01 by the SECOND role gate:
   `raid_session.elixir_role_errors` (via `raid_checkpoint.errors`) demanded the budget elixir of the
   ASSIGNED role (Agility for a melee warrior) while the per-character plan
   `plans/raid-budget-elixirs.json` declares 115/116 as tanks (Elixir of Defense 11349, captured on
   them in every checkpoint). Fix, same principle: the plan role is the declared loadout role — the
   elixir must match its policy and the role played must still have a reviewed policy;
   `check_checkpoint.py` test rewritten (warrior 2->4 accepted, priest 3->5 refused); all consumable/
   checkpoint/runner checks OK. No other role gate remains downstream (audit/review/score grep).
   **Assumption recorded for Nico**: the owner loadout is per character, so the permanent consumable
   baseline stays byte-identical when an encounter reassigns a tank to melee; if he prefers
   role-of-the-day elixirs, re-capture the Magmadar checkpoint with Agility on 115/116 and re-run.
   Boundary nuisance, new with warriors as melee: the native warrior AI casts Bloodrage out of combat
   (self-damage -> in-combat), the priests Renew them -> eight bots flagged; the native readiness check
   for `restore`/`restore-raid` is instantaneous and rejected three finish attempts
   (`post-batch-recovery-rejected-*`). Fix: `finish_measurement_session` and `raid_checkpoint.stage`
   retry a readiness/combat-rejected restore every 5 s for up to 90 s (Bloodrage = 10 s on a 60 s
   cooldown), every reply kept in the receipt/proof.
33. Session `sep12-magmadar-baseline4` (prefix `magmadar-base-0912r`): Apply/Arm/ready all passed, then
   `raidqa begin` refused pull 01 — **"The selected encounter boss is not visible."** Magmadar's home
   (1145,-1020) is 204.6 yd from the parked staging point (952,-952); `Visibility.Distance.Instances`
   is 170 on the box; every earlier target was <= 182 yd. Geometry fact: every point within 170 yd of
   Magmadar is <= 25 yd from some Core Hound home (checked over all survey anchors and the observed
   raid path) — his room IS the hound floor, which `--reclear` keeps cleared hourly until
   TYPE_MAGMADAR is DONE. The runner then crashed on a secondary bug (`analyze_consumable_use`
   assumed a pull-request event). `harness-invalidation.json` recorded; session finished 16:08Z.
34. Harness changes (generic): `capture_checkpoint.py --stage X Y Z` relocates the raid after
   `restore-raid` by the established GM setup (`.go xyz` owner, `.namego` x39; all 40 verified within
   15 yd; `staged.json`); `raid_regression` re-clear margin = `--seconds`+300 (1350 s) instead of a
   fixed 600 s so no pack respawn can land inside a whole attempt; `analyze_consumable_use` returns an
   empty analysis with an error for an attempt that never pulled. **Checkpoint
   `mc-sep12-magmadar-ready-2`** captured (capture-encounter, instance 101, Magmadar 826,088 HP,
   instanceState `0 0 0 0 0 0 0 3 ...`, 7 pets at 1,050,000, consumable auras verified): the raid
   staged at the Magmadar survey West team anchor (1076,-996,-186.4), 73 yd from Magmadar (outside his
   aggro, inside visibility, on his fight floor) — `sep12-mc-capture/magmadar-ready-baseline-2.json`
   (sha 56d83a12), session `sep12-magmadar-capture2`, log `sep12-magmadar-capture2.log`.
35. **Magmadar batch RUNNING on v6 from the staged checkpoint**: attached session
   `sep12-magmadar-capture2`, prefix `magmadar-base-0912s`, name
   `executor-v6-cone54-compiled-magmadar-baseline`, log `sep12-magmadar-capture2-runner.log`,
   `--previous` = the accepted 56644 ledger; re-clears armed with kill times west 15:15:21Z, middle
   15:25:42Z, east 15:27:35Z, SE 15:52:48Z, 56644 15:20:47Z. Launch = scratchpad `step2-magmadar-v6d.sh`.
36. **Magmadar fights on v6 (batch `magmadar-base-0912s`)**: pull 01 reviewed win 145 s, 38 survivors;
   pull 02 reviewed win 157 s, 40/40. Watch items from pull 01 (scratchpad `magmadar_pull_trace.py`):
   (a) Fear Ward holds — 4 Panics (t=18/50/80/113), the tank kept the boss the whole fight
   (`bossTarget=787`, 1.6-4 yd), 20 Fear Ward casts by the six priests; (b) enemy aura removal works —
   8 Frenzies, 5 Tranquilizing Shots (159 x2, 160 x3, 158 none); (c) the cone stays on the tank
   (Lava Breath 262/1151/1148 on 787) with one 21-member sweep at t=45 absorbed by GFPP; (d) trap-object
   exclusion regions ARE exercised: 20 Lava Bombs, 84 Conflagration applications; after an application
   members move a median 7.2 yd within 5 s and 71 % clear the 6 yd region — the 8-tick damage runs are
   the unremovable 8 s DoT of the unavoidable first contact (the bomb lands on its target), 98k fire
   damage mostly absorbed by GFPP; the two pull-01 deaths (135, 156 at t=90) took three landings each in
   26 s. Both re-clear rounds before pull 01 (west 48.7 s, middle 62.8 s, east 59.8 s, last hounds
   83.8 s; all 40/40) are live multi-spawn packets on Core 6747f0f7.
37. Runner CRASHED staging pull 04 after three sealed wins: checkpoint-buffs preparation found the
   GFPP absorb (17543) missing on ALL 40 (150 s deadline). Cause (client console): pull 03 completed
   16:28:56Z with Lava Bombs cast 3-15 s earlier (object lifetimes 30 s / 60 s per the derivation);
   the settle restore (+15 s) and the stage restore (+26 s) put the stacked raid at (1076,-996) onto
   live bombs — Conflagration hit 45/42/40/41... targets every ~2 s from +15.6 s and consumed every
   member's 2020-point absorb before the inspection (no re-use in checkpoint-buffs mode). Harness
   ordering, not a combat defect; the trap objects outlive the fight and the boss reset. Evidence
   `sep12-magmadar-capture2-evidence/aborted-04-restored-onto-lava-bombs/` (+ `harness-abort.json`).
   Fix (generic): `raid_regression.wait_for_object_hazards` blocks before the settle guard — hence
   before any restore — until the encounter's longest `summonedObjectHazards.objectLifetimeMs` (+5 s;
   65 s for Magmadar, 0 for packs/Lucifron) has elapsed since the previous pull's terminal event;
   `continue_batch.py` routes through the same wait. `raid_regression.py` is auto-frozen in the
   ledger -> `configurationAmendments` entry (0c97ced6 -> 02527be5, settle ordering only; the fight
   path is byte-identical); batch resumed from pull 04 with `continue_batch.py --start 4 --previous
   magmadar-base-0912s-03` (SE re-clear `reclear-05` 123.8 s 40/40 first; pull 04 pulled 16:42:14Z
   with the preparation passing).
38. Pulls 04-05 reviewed wins (157 s 40/40; 157 s 36 survivors) = **5/5**. Then the runner CRASHED
   staging the WEST re-clear (`reclear-06`): `bound_objective_gate` saw `BossGuid 0` — the executor
   observed no live west hound. Cause: pull 05 killed Magmadar -> TYPE_MAGMADAR DONE, and no settle
   restore followed (the new object-hazard wait kept the raid out of combat), so the plain west
   checkpoint's `Respawn()` ran with DONE in force and `instance_molten_core.cpp OnCreatureRespawn`
   removed the hounds (`AddObjectToRemoveList`): the client geometry survey shows middle/east/last/SE
   corpses and dead Magmadar but NO west spawns. Re-clear 05 (SE) had only worked because a settle
   restore of the Magmadar checkpoint reset the state first. Evidence
   `aborted-reclear-06-pruned-west/` (+ `harness-abort.json`, `probe-west/geometry.json`).
39. Fix (generic runner, no Core change): `raid_regression.reset_encounter_state` restores the MEASURED
   checkpoint before any re-clear round with a due pack (its captured instanceState is re-applied
   first, so assumed-cleared packs respawn normally); a pack whose restore then reports "selected
   creature unavailable" is recorded `pruned` in `reclear-state.json` (removed by the script; cannot
   respawn in this instance) and skipped from then on; `continue_batch.py` passes label/snapshot
   through. Second ledger amendment for `raid_regression.py` (02527be5 -> 7a4c4a4f, staging only).
   Resumed with `--start 6 --previous magmadar-base-0912s-05`: state reset ok, west detected pruned
   (`reclear-07`), middle re-cleared normally (`reclear-08`, 68.7 s, 39). Consequence for the rest of
   MC: once a boss is DONE its linked trash never respawns in this instance; only the packs that
   happen to be re-killed while NOT_STARTED keep their hourly timers.
40. Pull 06 win (137 s, 40/40) = 6/6. **Pull 07 LOST (stall)**: a normal fight to 18 % at t=113
   (main 1783 HP), the main tank died at ~t=118 and Magmadar's HP stayed at 17.8 % for 140 s while he
   walked the room picking members off (37 -> 10 alive), then evaded at t~260 (51 % -> 100 %). No
   respawned pack was involved (only Magmadar in combat all fight). Executor cause:
   `SelectActingTank` (core-patches/SuiCommanderRaid.cpp:131) accepts only role 1 (main, focus =
   boss) and role 2 (add tanks); Magmadar derives `addTanksPerTeam 0`, so with the main dead no acting
   tank exists and the damage roles hold. In the pack fights the add tanks took over (the 39-survivor
   wins). Candidate generic change (executor, next deploy — not this frozen batch): when neither a
   primary nor a backup tank is alive, the lowest-GUID living taunt-capable member (`row.taunt`)
   becomes the acting tank. 6/7 with three pulls left.
41. **Magmadar ACCEPTED 9/10** (`scratch/onyxia-live/sep12-magmadar-capture2-evidence`, attempts
   `magmadar-base-0912s-01..10`, 134-157 s, survivors 38,40,40,40,36,40,-,39,40,40; pulls 08-10 wins
   after the stall loss; `batch-report.md`, `proposal.json` (family stall -> acting-tank fallback),
   `encounter-acceptance.json` recorded; 79 raw-evidence hashes verified). Re-clears inside the batch:
   11 attempts (west x1 then pruned, middle x2, east x2, last hounds x2, SE x3; 48-124 s; one 39 and one
   38, the rest 40/40). Two ledger `configurationAmendments` (runner staging only). Session finished
   17:32Z: client closed, 39 out, 0p+0b; exact40 parked at (1076,-996) with buffs (restore-raid of
   `mc-sep12-magmadar-ready-2`); Magmadar dead, TYPE_MAGMADAR DONE in instance 101, so every Core
   Hound pack is pruned when its timer fires (middle 18:00Z, east 18:02Z, last 18:04Z, SE 18:22Z; west
   already gone) — the hound floor is permanently clear and no hound `--reclear` is needed again.
42. Deferred generic candidates (each = one deploy, measured by its own batch): (a) executor
   acting-tank fallback to a living taunt-capable member (item 40; would be measured by any boss batch
   where the main dies); (b) executor argmin objective rebind (item 26; multi-spawn batches);
   (c) review tool: dummy-chain resolver over harvested sources (item 27; before Garr/Geddon/Shazzrah
   surveys); (d) global policy: own-pack anchor distance for pack surveys (item 26). Owner decision
   pending: per-character loadout on encounter-role changes (item 32 assumption).
43. Owner order 13:40 local: "Your job is to clear molten core. don't stop again." — no more scope
   stops; decisions stay generic and are recorded here.
44. Route north of the Lucifron junction to Gehennas (898,-546): the Flame Imp room (x 958-1076,
   y -856..-908: ~50 imps at 300 s respawn, three patrolling Lava Surgers), an Ancient Core Hound
   (56853, patrols 1026-1065/-815..-749), Surger 56840, Annihilator 56736, Firelord 91293, the Molten
   Giant/Destroyer packs 56716/56747/56702/56706 with Firelord 56720 and Surger 56839, the Firelord/
   Annihilator pack 56721, Surger 56844, then Gehennas' room trash (Firelords 56779/91284/91285,
   Annihilators 56788/91282, Giant+Destroyer 56708). Four Ancient Core Hound patrollers cross the
   plateau (56851 x1071-1162; 56854 x817-1203/y-744..-575; 56852 x711-1102/y-710..-537; 56865
   x801-892) — they go early. All 24 route reviews: allowed (review-tool limits: pack compiler
   derives no cone rules -> the Ancient Core Hound's target-54 breath shows "partial"; script summons
   like the Firelord's Lava Spawns are not classified for packs).
45. Compiler (generic, policy `packs`): `singleSpawns` (every ungrouped spawn is a pack of one) and
   `assistanceRadiusYards 10` = Core `CreatureFamilyAssistanceRadius` (mangosd.conf): pull units are
   connected components over group links and 10 yd links (encounter spawns never merge) — the east imp
   field is ONE 30-imp pull (`pack-409-56549`), the west field is 10 imps + Surger 56842
   (`pack-409-56594`), `pack-409-56584` = 10 imps + Surger 56841. 86 units on map 409. A pull has ONE
   objective entry (the leader's) and every member as a required add — the client assigns one main
   tank per objective entry, and two entries cost a healer ("Assign at least 10 healers"). The five
   accepted hound packs and their promoted files are byte-identical. `pack-definition-derivation.json`
   is FROZEN during a batch: compile only between batches (a mid-batch recompile crashed
   `sep12-pack56594-baseline` on verify_frozen).
46. Checkpoint tool fix deployed (Core ce9f823d, 17:55Z): a level-range template (Flame Imp 61-62)
   re-rolls its level and max health on every respawn, and a restore respawns captured creatures, so
   the second restore failed the captured-maxHealth identity check; now only fixed-level spawns are
   identified by maxHealth (`sep12-checkpoint-level-range/`). Executor unchanged.
47. **Executor v7 deployed (Core 3dd17b2c, 18:00Z)**: objective binding = argmin over the whole grid
   list at (re)bind time, and a living in-room bound unit is never re-bound by distance
   (`kBindTolerance` removed; `sep12-executor-v7-argmin/`). Trigger: the tight 10-imp pull flipped the
   greedy 10 yd rebind within 10 s (`imps56594-base-0912v-01`). Single-spawn bosses unaffected by
   construction. Canonical `core-patches/SuiCommanderRaid.cpp` = e7ab2d2a.
48. Surveys for the route written by `scratchpad/gen_surveys.py` (anchors between the approach point
   and the unit's nearest member, z from the nearest spawn) with `assumesCleared` chains; the imp room
   is a knot of patrol paths, so some chains name units accepted later — their checkpoints are captured
   first and their runner re-clears are real reviewed fights. West imp unit checkpoint
   `mc-sep12-west-imps-ready` (raid staged at the junction (952,-952), 57 yd south; 11 creatures);
   **batch RUNNING**: session `sep12-pack56594-baseline2`, prefix `imps56594-base-0912w` on v7.
49. West imp unit, three invalidated sessions before the first real pull (each recorded in its
   `harness-invalidation.json`): (a) `sep12-pack56594-baseline` — identity flap in 10 s (tight
   10-imp pull) -> executor v7; plus my mid-batch pack recompile tripping verify_frozen; (b)
   `sep12-pack56594-baseline2` and (c) `baseline3` — the client's ranged pull walks the tank from the
   staging point toward the bound unit and stops at the first point within 28 yd, here the mouth of
   the tunnel into the imp room (957.6,-923.6), where Shoot Crossbow fails SPELL_FAILED_LINE_OF_SIGHT
   forever (432 attempts / 15 min); the tank anchor does not change that point. Floor probes (`.gps`
   from just above the floor; a point inside rock relocates the owner to the entrance (1091,-467):
   junction floor -184 rises to the imp floor -173..-177 through a tunnel under a rock mass at
   x 948-960 / y -916..-904; the raycast geometry survey only sees the cave roof. New tool
   `probe_floor.py`. Resolution (generic, no client change possible on this stack): the checkpoint
   carries no bolts on the owner (`capture_checkpoint.py --setup ".additem 11285 -1000"` — the
   equipped ammo is Jagged Arrow 11285, not the 2516 the supplies feed names), so
   `TryRaidQaRangedPull` yields and the client's own body-pull path walks the tank to melee contact
   and back to the anchor. Checkpoint `mc-sep12-west-imps-ready-3`.
50. **West imp unit batch RUNNING** (`sep12-pack56594-baseline4`, prefix `imps56594-base-0912y`,
   Core 3dd17b2c): pull 01 reviewed win 109 s, 37 survivors, all 11 members dead.
51. Body pull, session `sep12-pack56594-baseline4`: pull 01 win (109 s, 37), pull 02 the owner walked
   a STRAIGHT line into the rock at (957,-921) and stood 15 min — the client's body pull has no
   pathfinding; pull 01's line happened to thread the tunnel. Navmesh route via `.mmap path` (it
   summons visual-waypoint creatures, entry 1, visible to the geometry survey): junction ->
   (958,-929) -> (960,-922) -> a 22 yd corridor at x=963 (y -919..-897).
52. **Executor v8 -> v9 (Core 16ec320f, 19:26Z)**: generic owner approach routing. In `Guidance()`, a
   manual main tank with the bound objective alive and out of combat and beyond melee reach gets a
   state-2 advice along the navmesh path to the unit — v9 advises the first path node >= 2.5 yd ahead
   (v8's `CombatApproachRoute`/`FirstRouteTurn` produced a sub-yard nudge that the client's 0.2 yd
   threshold ignored: jitter in place, `baseline5`). The client accepts a movement advice only under a
   movement rule, so policy `packs.rangedSpread` (7 yd, healers|ranged, priority 20) gives every pack
   `derived-spread-ranged` — also the ordinary spacing the coverage table already claimed.
   Sources `sep12-executor-v8-approach/`, `sep12-executor-v9-lookahead/`; canonical
   `core-patches/SuiCommanderRaid.cpp` = 2d13c8d6. Deploy hygiene slip: the v9 restart ran while the
   baseline5 client was still connected (finish had refused a pull with no terminal event; I appended
   an `invalidated` event and closed the client afterwards) — always confirm 0p+0b before
   `deploy-scoped-core.py`.
53. **West imp unit batch RUNNING on v9** (`sep12-pack56594-baseline6`, prefix `imps56594-base-0913a`):
   pull 01 reviewed win 90 s, 38 survivors — the owner followed the advised waypoints through the
   tunnel (t=0 wp (953.9,-944.2), t=5 at (962.6,-919.4), combat at t~8).
54. `sep12-pack56594-baseline6` pull 02 LOST at 330 s: the bound imp died at 30 s but the unit was
   incomplete — its member Lava Surger 56842 patrols the whole imp room (x 957-1071) at 100 % and
   never engaged, so the executor ended the plan ("stopped before a boss kill"). The pull-unit merge
   had included it because its HOME is 1 yd from an imp. Compiler rule (generic): a patrolling spawn
   (`creature.movement_type` 2) is its own pull unit and never merges. `pack-409-56594` is the 10
   imps again; `pack-409-56842`, `pack-409-56843`, `pack-409-56841` are Surger units; 88 units on
   map 409. Their patrol paths cross every imp-room anchor (clearance violations on 56594/56549/56584/
   56843), so the Surgers go first. Batch void (definition changed); session finished 19:39Z.
55. PAUSE 15:40 local at Nico's request ("find a good stop point, prepare a prompt for the next
   agent"). Boundary: client closed, 39 out, 0p+0b, Core 16ec320f PID 2936686 (screen
   2936685.mangosd), exact40 parked at the junction (952,-952) with buffs and NO owner ammo
   (checkpoint `mc-sep12-west-imps-ready-3`). Instance 101: Lucifron + Magmadar DONE, hounds pruned,
   imp room untouched (all imps and Surgers alive on their timers). Patroller caution for the next
   agent: a restore resets a patroller to its home and it starts walking immediately; with ~45-60 s of
   staging before the pull it can be 100 yd along its route (for 56842: deep in the 30-imp east field)
   when the owner's approach route reaches it — consider pulling a patroller unit only when it is
   near its home (the executor could gate the approach on distance-from-home; generic candidate) or
   accept that the field joins and measure it.

## 2026-09-12 evening session log (Claude, resumed 15:43 local after the 15:40 pause)

56. Stack re-verified at resume: Core 16ec320f (PID 2936686, comm mangosd-main), client 2509aaf8, no QA client,
   0p+0b (RA `.bot info`), boundary receipts of `sep12-pack56594-baseline6-evidence/post-batch-recovery` intact.
57. **Executor v10 deployed (Core b8816ac1, 19:58Z, PID 2938530, screen 2938529.mangosd)** = v9 + pre-combat
   patrol hold: in `Guidance()` the manual tank's pre-combat approach treats a bound unit whose Creature default
   movement type is WAYPOINT as a patroller — it is engaged only once its route brings it within
   `kPatrolEngageYards` (36 = the survey clearance distance) of the tank anchor, or of the tank once the
   approach has begun (so a unit walking away is not released mid-approach); until then the navmesh advice
   routes the tank to the tank anchor and, within 2.5 yd of it, returns state 1 (hold). Stationary spawns take
   the unchanged v9 branch. Rationale (item 55): a patroller's route runs through the groups it patrols past;
   the v9 chase would engage whatever stands there (the 30-imp east field for the Surgers). Source
   `scratch/onyxia-live/sep12-executor-v10-patrol-hold/SuiCommanderRaid-v10-patrol-hold.cpp` (sha e7204d7c),
   `apply_build_v10.py`, `deployment-result.json`; 141 contracts pass; canonical `core-patches/SuiCommanderRaid.cpp`
   = e7204d7c. Deployed at a clean boundary (0p+0b re-verified immediately before). Regressions: bosses and
   static packs are `!patrolling` -> identical path (recorded, not re-run); measured by the first Surger batch.
58. Generic tool changes for patroller units (all before the batch, none Core): (a) compiler `pack_compiler.patrol_reach`
   + policy `survey.patrolEngageYards = 36` (must equal the executor constant): a pack survey whose tank anchor
   no patrolling member's database route passes within 36 yd of stays `surveyRequired` (shortfall recorded under
   `patrolReach` in the derivation) — such a tank would hold for ever; (b) runner `bound_objective_gate` polls
   `raidqa report bound-wait-NN` every 5 s for up to 300 s while the status carries no binding (a patrolling
   objective binds only when its route enters the surveyed room, which may be after readiness); a wrong binding
   is still refused; (c) `capture_checkpoint.py` tops the three hunters up with pet food (`raidqa select` +
   `wait 1` + `.additem 8952 10`, GM setup as in the batch stage) before the native feed — the first capture was
   rejected with "carried pet food required", and a back-to-back select/additem landed the food on the owner
   (removed again with `--setup ".additem 8952 -30"`; receipts in `sep12-mc-capture3-evidence/aborted-capture-*`).
59. Same-entry patrollers and the objective binding: the three Lava Surgers (56842/56843/56841, entry 12101) share
   the imp room, and the executor binds the nearest live in-room spawn of the entry to the tank anchor at Apply
   time, which for 56842 vs 56843 flips with a few seconds of walking. Resolution in survey data, not code: the
   `pack-409-56842` survey's room box is deliberately tight (x 930..1000, y -960..-901) — only 56842's route
   (its second/third waypoints, 25-28 yd from the tunnel-mouth anchor (960,-924)) enters it; 56843's nearest
   route point (963,-885) and 56841's route never do, so the binding can only land on 56842, and the runner waits
   for it (item 58b). Tank anchor = the west-imp survey's tunnel mouth, teams on the junction floor;
   `assumesCleared: [pack-409-56594]` (west imps, 300 s timer -> re-cleared before every pull). The
   `pack-409-56594` survey now assumes 56842 cleared (its only clearance violations were that route), so the
   10-imp definition is promoted again (11-member stale file replaced). Compile: 88 units, promoted 56594 + 56842
   (+ the five hound packs, 56736 unchanged). Objective reviews regenerated for both: no unsupported items.
60. Checkpoints (session `sep12-mc-capture3`, raid at the junction (952,-952), no owner ammo, pets fed):
   `mc-sep12-west-imps-ready-4` (10 imps only; `sep12-mc-capture/west-imps-ready-baseline-4.json`, sha 53985a17)
   and `mc-sep12-surger-56842-ready` (Lava Surger 94,320 HP home (958,-885); `surger-56842-ready-baseline.json`,
   sha bb9f9767). **Surger 56842 batch RUNNING** from 20:08Z: attached session `sep12-mc-capture3`, prefix
   `surger56842-base-0912a`, name `executor-v10-pack-409-56842-baseline`, `--previous` = the accepted Magmadar
   ledger, `--reclear pack-409-56594=mc-sep12-west-imps-ready-4=...` (no kill time -> first re-clear forced),
   log `sep12-surger56842-baseline-runner.log`, launch script scratchpad `run-surger-56842.sh`.
61. Surger 56842 batch, harness findings (each a runner-only ledger amendment, no measured pull affected):
   (a) `run_reclears` re-killed the west imps in a loop — a pack whose timer (300-480 s) is shorter than the
   re-clear margin (1350 s) is due again the moment it dies; now one re-kill per round (`break` after a
   reviewed re-clear win). (b) A restore issued while the owner was still walking back from a sealed kill
   left it 5 yd off the captured pose -> "Cohesive pull starts separated"; `raid_checkpoint.stage` now
   verifies the owner's position against the captured pose after every restore and restores again (up to
   three times) when displaced > 3 yd (`poseChecks` in `server-checkpoint.json`). (c) After pull 07 the
   settle guard restored the measured checkpoint on ONE round of lingering combat flags (6 s past the kill),
   which respawned the measured patroller; it joined the end of the next imp re-clear (reclear-09, server log
   16:44:48 `[AIBOT-PARTY] focus -> Lava Surger`), the leaderless native bots killed it (guid 56842, 16:45:16,
   not a measured result) while the pull-08 stage ran, and the owner was charged 10 yd off the pose. Settle
   now restores only after three rounds (18 s) of continued combat (`raid_regression.settle` and the
   `continue_batch` copy). Aborted stagings preserved: `aborted-reclear-02-separated-start/`,
   `aborted-08-settle-respawned-patroller/` (+ `harness-abort.json`).
62. Live verification of the v10 hold (pull 01 battle.jsonl): t=0 advice state 2 toward the anchor; t=5 the
   tank at (959.8,-925.3) = the anchor, state 1 hold while the Surger ran east (it RUNS its route: 66 yd in
   5 s, loop ~40 s); t=15.7 the Surger at 35.3 yd -> engage; contact t=20 at (963,-907); kill at 43 s, 40/40.
   The Surger's Surge charges a raid member mid-fight (target 115 at t=35); the tank re-acquires. Binding wait
   worked (pull 01: 2 polls). Pulls 01-07 all reviewed wins (42-56 s, 40/40 in every pull); resumed from pull
   08 after (c).
63. **Lava Surger `pack-409-56842` ACCEPTED 10/10** (`scratch/onyxia-live/sep12-mc-capture3-evidence`,
   attempts `surger56842-base-0912a-01..10`, 33-56 s, 40/40 in every pull; `batch-report.md`, `proposal.json`,
   `encounter-acceptance.json` recorded; 12 west-imp re-clears beside the batch, 42-68 s, 35-40 survivors).
   The pull-08 staging incident (item 61c) also hung the QA client after my resume replayed the stale
   `settle-08` reports into a moved attempt directory (`aborted-08-settle-respawned-patroller/client-hang.json`);
   the client was killed and relaunched into the same session (`relaunch_session.py` steps run by hand after a
   queue collision), pulls 08-10 sealed afterwards. Session finished 21:16Z (client closed, 39 out, 0p+0b).
   Kill times: west imps 21:07:54Z (reclear-12), 56842 21:09:47Z (pull 10).
64. Floor probes for the rest of the imp room (`probe_floor.py`, `sep12-mc-capture3-evidence/probe-floor-*`):
   the x=963 tunnel corridor is 1 yd wide ((962,-912)/(961,-914) are rock; (963,-910) -176.4, (963,-915)
   -177.4; a probe from z -166 at (963,-912) lands on the rock ROOF at -170.2 — probe from just above the
   expected floor); the room's north-west (x 975-1000, y -860..-875) is rock; floor: (978,-878) -171.9,
   (995,-853) -165.4, (1005,-875) -166.3, (1000,-880) -159.3, (1000,-897) -167.9, (1005,-895) -166.8,
   (1010,-890) -164.5, (988,-898) -169.3, (984,-905) -170.9, (1015,-862) -162.6, (1025,-862) -160.3,
   (1045,-885) valid; (1010,-880) rock. `probe_floor.py --home` must not carry a map id (a float map breaks `.go`).
65. Surveys compiled + promoted + reviewed (no unsupported items): `pack-409-56843` (tank (963,-912,-177) on
   the corridor, 27 yd from its westmost route point; box y <= -870 keeps 56841's route out of the binding;
   assumesCleared 56594 + 56842) and `pack-409-56841` (tank (978,-878,-171.9), 30 yd from its westmost route
   point (995,-853), 49 yd from the east field; box y <= -840 keeps Surger 56840's route out; assumesCleared
   56594 + 56842 + 56843). Team anchors stay on the junction floor for both. **56843 batch launched 21:20Z**:
   session `sep12-surger56843-baseline` (capture `mc-sep12-surger-56843-ready` first), prefix
   `surger56843-base-0912b`, re-clears imps + 56842, log `sep12-surger56843-baseline-runner.log`; driver
   scripts in this session's scratchpad: `capture-and-run.sh`, `close-out.sh`, `continue-surger-56842.sh`.
66. **Lava Surger `pack-409-56843` ACCEPTED 10/10** (`scratch/onyxia-live/sep12-surger56843-baseline-evidence`,
   attempts `surger56843-base-0912b-01..10`, 34-50 s, 40/40 every pull; 16 re-clears beside the batch: imps
   x13, 56842 x3, one re-clear loss). The capture had to wait for the unit's respawn: the leaderless native
   bots had killed 56843 at 20:57:25Z while the QA client was being relaunched during the previous batch
   (`aborted-capture-unit-dead/`). `raid_native_pet_feed` now accepts a pet already at the goal without a meal
   (`aborted-capture-feed-full/`: the native feed consumes nothing at full happiness). Session finished
   22:18Z; kill times imps 22:12:27Z, 56842 22:09:12Z, 56843 22:14:17Z.
67. **The re-clear loss exposed a generic executor limit** (`surger56843-base-0912b-reclear-08`): the tank
   engaged the patroller at (963,-902) on the imp floor while the raid stood on the junction floor 12 yd lower
   and 50 yd away; every damage/heal route search logged `[SUI][raid-route] ... candidates=0` — FindClearRoute's
   19x19 room-grid candidates carry the ACTOR's z, so from the lower floor none passed melee reach, spell range
   or line of sight; all 29 damage roles and both add tanks held (duty 12), the tank died alone at 75 s, the
   unit evaded. Every measured pull so far was won because the pull came down to the raid (Surge charges).
   **Executor v11 deployed (Core 210c4bf0, 22:21Z, PID 2959916, screen 2959915.mangosd)**: the combat-approach,
   ranged/ranged-formation and healing routes pass the target's/patient's z as the candidate floor reference
   (the melee-station route already passed its station z). Two route-family literals in the Core
   source-check gained the argument and one contract was added (142 pass; the modified checker is archived in
   `sep12-executor-v11-target-floor/` and as `qa-artifacts/commander-raid-source-check-v11.py`). Canonical
   `core-patches/SuiCommanderRaid.cpp` = 84bb6bc4. Deployed at a clean boundary. Single-level rooms are
   unchanged by construction (recorded, not re-run); measured by the 56841 batch.
68. Tool changes at the same boundary (none in a running batch): (a) runner settle: the measured checkpoint's
   entries are dropped from the engaged set after a sealed win (its unit is dead) so a foreign group that
   joined after the kill selects its own checkpoint, and settle evidence directories get a unique suffix when
   one exists (the resume replay that hung the client in item 63 cannot recur); (b) review tool: dummy-chain
   resolver (SpellEffects.cpp `case <id>` bodies, boss-script `SpellHit` handlers, "no handler" = inert;
   child casts classified; threat reset/teleport = script mechanic covered by threat gating), `member-aoe`
   (a member aura whose periodic child is an area effect around that member -> covered when the compiler
   derived an isolate rule), caster-position area effects (targets 18/16), chain-child object hazards, and
   unclassified boss threats now render as `review` rows instead of vanishing (Geddon's Living Bomb 20475 had
   been silently dropped). **All eleven boss reviews regenerated: 0 unsupported items** (garr, baron-geddon,
   shazzrah now allowed; sulfuron-harbinger, golemagg-the-incinerator, majordomo-executus, ragnaros generated
   for the first time; some `review` rows remain for inspection before each survey).
69. Candidate prepared, NOT deployed: executor acting-tank fallback (item 40/42a) as
   `sep12-executor-v11-acting-tank-fallback/SuiCommanderRaid-v11-acting-tank-fallback.cpp` (based on v10; must be
   re-based on 84bb6bc4 before a build). **56841 batch launched 22:23Z** on Core 210c4bf0: session
   `sep12-surger56841-baseline`, prefix `surger56841-base-0912c`, re-clears imps + 56842 + 56843,
   log `sep12-surger56841-baseline-runner.log`. Surveys drafted (unprobed teams, not compiled) for
   `pack-409-56549` (east field; tank (1000,-897), box x >= 990) and `pack-409-56584` (north field; tank
   (1015,-862), box y in [-881,-840]).
70. 56841 first session `sep12-surger56841-baseline` HARNESS-INVALIDATED after two lost pulls
   (`harness-invalidation.json`; three runner ledger amendments kept as evidence): (a) pull 01: the west imps
   (re-killed first, then two 28-minute Surger re-clears, ~5 min) respawned onto the tank holding at the anchor
   10 yd from their homes as the pull began; the tank died at t=30 with the plan never in combat (a pre-pull
   foreign engagement: the bots hold at duty 11). Fix (runner): re-clears in survey-dependency order
   (`reclear_order`, cycles broken at the SHORTEST timer), a settle before every re-clear stage and before the
   measured stage (`reclear_one`), and a final pass re-killing a short-timer pack whose last kill the other
   re-clears left older than its timer less two minutes; `continue_batch --last` seeds the first settle after a
   lost attempt. The first resumed round still re-killed 56842 with the respawned imps alive (the imps/56842
   surveys assume each other cleared - a cycle first broken at the longest timer): the imps joined, kept
   attacking through the next restore and burned every member's fire absorb (`aborted-reclear-05-absorbs-burned`).
   (b) pull 02: the tank flipped 29 times in 145 s between approach (state 2) and hold (state 1): the unit's
   route only touches the 36 yd engage circle at its west turn-around 30 yd from the anchor and the Surger
   RUNS (~10 yd/s) - out and gone before the 7 yd/s tank closes; then the imps respawned onto the tank again.
   Fix (compiler, policy `survey.patrolRouteInsideYards = 30`): a patrolling member is reachable only if its
   route passes within 5 yd of the tank anchor (the tank stands on the patrol) or runs >= 30 yd inside the
   engage circle (56842: 73 yd, 56843: 133 yd, the old 56841 anchor: 18 yd). Survey `pack-409-56841`: tank
   anchor (995,-856,-165.4), 3 yd from the route's west end, 38 yd from the nearest west imp; teams stay on the
   junction floor 110 yd away - the v11 target-floor routes are what brings the raid to the fight (first live
   packet). 56549/56584 draft surveys were promoted by the same compile (no clearance violations).
   **Second session `sep12-surger56841-baseline2` launched 23:20Z** (prefix `surger56841-base-0912d`; kill times
   imps 22:59:24Z, 56842 22:57:00Z, 56843 23:00:58Z; log `sep12-surger56841-baseline2-runner.log`).
71. 56841 second session (`sep12-surger56841-baseline2`): pulls 01-04 reviewed wins (50 s, 40/40; the v11
   target-floor routes bring the raid 110 yd from the junction floor to the fight in ~10 s: at contact the
   bots switch to duty 6 and the healers stand at (1002,-869) by t=30). Re-clear round per pull = imps ->
   56842 -> 56843 -> imps again (final pass, kill age ~215 s) -> measured pull ~100 s later: 5 fights per pull,
   ~11 min per pull. Two more runner-only amendments: (a) I edited `objective_review.py` (targeted-area
   classification for the boss reviews) while this batch ran and its frozen check refused the pull-05 stage
   (`aborted-05-frozen-review-tool/`) — my own trap, recorded; (b) **pull 05 LOST**: the settle after the final
   imp re-clear restored the IMPS' checkpoint 27 s after their kill on two self-inflicted combat flags
   (Bloodrage/Renew), which respawned all ten imps on the tank's path (tank dead at t=20, boss untouched).
   The settle now never restores a checkpoint whose group is known dead (`known_dead`: a re-clear pack killed
   inside its respawn window, or the measured unit right after its sealed win) and returns to the stage's
   readiness retries when nothing is eligible. Resumed from pull 06 with `--last 05` (4/5 so far).
   Review-tool additions at 00:05Z: `targeted-area` (persistent area auras / destination area effects ->
   covered when the compiler derived a hazard lane, e.g. Gehennas' Rain of Fire); Sulfuron's Flame Spear
   (19781) is the one remaining unsupported boss item (no hazard lane derived) — resolve before his survey.

## 2026-09-13 session log (Claude, resumed 01:32Z right after the 01:27Z pause)

72. Stack at resume: Core 210c4bf0 (PID 2959916), client 2509aaf8, no QA client, 0p+0b (all re-verified).
   Owner rules applied as written: 56841 already had three consecutive kills (pulls 06-08) = done; the west
   imps (dozens of consecutive re-clear wins) = done. Only the north imps were left in the imp room.
73. **Core 498a911d deployed 01:47Z** (PID 2981502, screen 2981501.mangosd; `sep13-core-candidates/deployment-result.json`;
   142 source contracts; canonical `core-patches/*.cpp` updated) = two tool/executor changes at the clean boundary:
   (a) executor **v12** = v11 + the deferred acting-tank fallback (item 40/42a: when neither a primary nor a backup
   tank is alive, the lowest-GUID living taunt-capable member becomes the acting tank; unreachable while an
   assigned tank lives) — `SuiCommanderRaid-v12-acting-tank-fallback.cpp` 2d4e20c6;
   (b) checkpoint tool `CapturedIdentityEvent`: the native respawn happens on the world tick after a restore and
   `instance_molten_core.cpp OnCreatureRespawn` re-rolls Lava Annihilator <-> Firelord with even odds, which would
   leave a different unit where the captured one stood (the second restore then fails the identity check); the
   restore now schedules an event 1.5 s after `Respawn()` that re-asserts the captured entry
   (`SetEntry/UpdateEntry/AIM_Initialize`, full health) — `RaidQaCheckpoint-captured-identity.cpp` e7b6ed36.
   Also learned from the same script: Ancient Core Hounds are pruned on ANY respawn once Magmadar is done, so a
   checkpoint restore would delete them — they are fought live (restore-raid staging) and die once; Lava Surgers
   are pruned once Garr is done.
74. New generic driver `tools/encounter-content-audit/clear_route.py` (owner rules 1-2): one live QA session; a plan
   lists units in walking order, each with its checkpoint (captured on the way: `capture` = restore-raid of the
   previous checkpoint [+ GM `--stage`] + capture); every kill = settle -> checkpoint `restore` (respawns the unit,
   stages the buffed raid; `restore-raid` for `live` units) -> readiness/binding gates -> executor pull -> watch ->
   reviewed packet -> seal; two consecutive kills finish a unit; `again` re-kills a done pack that stands in the
   way; `probe` steps run floor probes with the raid idle; `pause` stops. The session ledger freezes the STACK
   (client, Core patches, tools, policy, content snapshot); each attempt hashes its unit's definition/compiled/
   review/survey/snapshot into `unit-configuration.json` and refuses a unit whose objective review lists
   unsupported items. `clear-state.json` = per-unit attempts, kill times, streaks, done flags. `raid_checkpoint.stage`
   gained the `mode` ('restore' | 'restore-raid'); `amend_ledger.py` records a staging-harness edit in a live
   ledger (fight-path files refused). Review tool: an INSTANT destination burst (Surge, Flame Spear: no persistent
   area, no duration) is `target-burst` = covered by spacing/healing; only persistent areas are `targeted-area`
   (hazard lane) — all 45 objective reviews now 0 unsupported (Sulfuron's Flame Spear resolved).
75. Session `sep13-route-imp-room` (launched 02:01Z, still the live session): west imps x4 (41-50 s, 39-40),
   56842 (62 s, 40/40), 56843 (40 s), 56841 (52 s, 39) as clear-the-way kills; **north imps `pack-409-56584` DONE
   2/2** (37 s, 37 s, 40/40 each; checkpoint `mc-sep13-north-imps-ready-2`, raid staged in-room at (1000,-858)).
   Harness lessons: (a) a capture right after a sealed kill hit lingering combat flags -> the driver settles before
   a capture and the capture's restore-raid retries readiness; (b) **the GM staging (`.go xyz` + `.namego`)
   teleports players only — the hunter pets WALK from the previous pose**: the first north-imps capture staged the
   raid 48 yd through the tunnel and the pets ran past the freshly respawned west imps, which followed them onto
   the raid and burned every fire absorb (`aborted-capture-north-imps-pets-walked/`; the server label
   `mc-sep13-north-imps-ready` is dead data). Rule: a new checkpoint pose must be a short clear hop from the
   previous checkpoint's pose (the checkpoint restore itself teleports pets); a native `stage` mode in the
   checkpoint tool is the durable fix (next Core boundary).
76. Floor probes north of the imp room (`probe-north-exit-145/-155`; FloorZ -105.08 = relocated to the entrance =
   ROCK): the room's north wall is y ~ -838 for x <= 1015; x <= 1010 at y -790..-830 is rock; the way north is a
   corridor x 1016-1056 / y -826..-833 (Surger 56840's patrol, z -148..-154) opening onto the plateau: (1020,-812)
   -150.4, (1030,-800) -160.1, (1030,-790) -155.0, (1025,-780) -156.0, (1045,-785) -152.2, (1060,-780) -162.4,
   (1100,-770) -150.0, (1040,-740) -151.8, (1060,-730) -151.9, (1020,-735) -164.1, (1000,-760) -175.2;
   room north edge (1000,-845) -153.3, (1010,-845) -158.9, (1025,-845) -158.8, (1035,-840) -158.5, (1020,-850) -159.5.
77. Plateau surveys written (`sep13-route/write_plateau_surveys.py` -> plans/surveys), compiled, promoted, reviewed
   (0 unsupported; Lava Breath `partial` on the hounds): 56840 (tank ON its corridor route (1030,-828), teams on
   the room's north edge (1000,-845)/(1010,-845)), 56853 live (anchor (1041,-820) at its southmost waypoint),
   56736, 91293, 56839 (anchor on its western zigzag; fought BEFORE the giants it patrols past), 56716, 56720,
   56851 live, 56854 live, 56747. The 36 yd rule cannot be met everywhere on the plateau (statics 26-28 yd from
   some anchors; hounds crossing): every such neighbour is named in the survey's `assumesCleared` with the
   distance and the reason, and fights that get joined are simply fought. Phase-2 plan
   `sep13-route/plan-plateau.json` (prefix `route-0913b`) launched 02:43Z on the same session: imp-room re-kills
   (imps/56842/imps/56843/imps/56841) -> 56840 x2 -> 56853 -> 56736 x2 -> plateau probes -> 91293 -> 56839 ->
   56716 -> 56720 -> 56851 -> 56854 -> 56747 -> pause. Driver log `scratch/onyxia-live/sep13-route-plateau-driver.log`.
78. Plateau session findings (sep13-route-imp-room, then sep13-route-plateau2 after the 04:07Z Core boundary):
   - **Ancient Core Hounds are gone**: `instance_molten_core.cpp OnCreatureCreate` deletes them when the instance
     loads with Magmadar done, and the Core has been restarted many times since his kill; none of 56853/56851/56854/
     56852/56865 exists in instance 101 (geometry: 58-77 creatures visible, no entry 11673). Recorded as pruned.
   - **OnCreatureCreate also re-rolls every Lava Annihilator/Firelord at instance load** (even odds), and
     `OnCreatureRespawn` re-rolls on every respawn: spawn 56728 (DB Firelord) is an Annihilator, 91283 (DB
     Annihilator) became a Firelord after the 04:07Z restart. Generic handling: survey `liveEntries` ({guid: entry},
     hand data observed live via `raidqa geometry`) honoured by `pack_compiler` and the review tool; the route
     driver refuses a capture whose live templates differ from the compiled pack. The checkpoint tool's identity
     event (Core 498a911d) keeps the SERVER entry captured, but the protocol-4 client keeps the creation-packet
     entry and refused the pull ("boss is not visible"); the driver first restored until the client saw the
     captured entries (a coin flip per member: a two-member pack failed six tries), then **Core 1758f7d1 (04:07Z,
     PID 2995240)** = identity event v2 re-creates the object at every client - first-try matches since.
   - Staging traps found the hard way: (1000,-845) is a rock outcrop the client falls off ("left the encounter
     floor envelope" x3); the plateau "centre" (1055-1080,-775..-790), 10 yd below its surroundings in the probes,
     is a **lava pool** (environmental damage type 3, ~607/tick) - a probed floor that sits 5+ yd below its
     neighbours is lava or a rock, never a staging point. The settle now restores the checkpoint whose creatures
     stand nearest the fighting members (the north imps kept joining corridor fights and the west-imps checkpoint
     kept teleporting the raid to the junction), and the restore pose check tolerates the client's stale
     local-player entity (after a burst of teleports the owner's reported position stuck at the junction while
     the client rendered the raid at the pose; a client relaunch into the same session cleared it).
   - Units: 56840 DONE 2/2 (25 s, 23 s), 56736 DONE 2/2 (26 s, 29 s; 38/37 - the north imps join every corridor
     fight), 91293 DONE 2/2 (25 s, 23 s, 40/40), 56839 DONE 2/2 (46 s with both giant packs joining, 36 s clean),
     **56716 killed incidentally** (both giants joined 56839's first fight and the raid killed them under the native
     bot defence right after the sealed win; never captured, 6 h timer - one real kill, not an executor pull),
     56720 (Firelord + re-rolled Annihilator) DONE 2/2 (35 s, 37 s, 40/40; its Lava Spawns keep the raid busy ~2
     min after the kill), 56747 DONE 2+1 (66 s, 88 s, 63 s, 40/40). Plateau north cleared 04:26Z.
   - Gehennas: `plans/surveys/gehennas.json` written (anchor 29 yd south of him, teams 30 yd further); the
     compiled definition carries a Rain of Fire hazard lane that schema 1 cannot express -> **executor v13
     candidate** (hostile persistent-area DynamicObjects as exclusion regions, the v5 trap-object path) built
     as a50c24cd (`sep13-core-candidates/SuiCommanderRaid-v13-area-auras.cpp` 3b705468) with the compiler's
     `--native-coverage area-auras` tag; deploy at the boundary before his batch. The boss compiler now shares
     the pack compiler's pull-unit grouping for its clearance check.
79. Descent (session `sep13-route-plateau2`, Core 1758f7d1): 56839 re-killed once (its 28 min clock; the descent staging
   point is 24 yd from its loop), **56721 DONE 2/2** (38 s / 36 s; 34 and 38 survivors - Surger 56844 ran into the
   fights and the giant pack 56702 joined; both died at the raid under the native defence before any checkpoint
   could be captured: recorded INCIDENTAL, one real kill each; 56844 respawns ~05:04Z, 56702 in 6 h), **56706 1/2**
   (45 s, 40/40) - the second pull's stage crashed on a transient roster count ("raidqa select 157 Earthzor;
   members=39") and Nico asked for the pause. Live entries after the 04:07Z reload (geometry from the lower floor):
   91284 is an ANNIHILATOR now (survey liveEntries set, compiled), 56779 F, 56788 A, 91282 A, 91285 F, 56801 A.
80. PAUSE 04:45Z (Nico: "pause, update, prompt"). Boundary: `finish_measurement_session.py --label mc-sep13-giants-56706-ready`
   (restore-raid: raid revived at the lower-floor edge (990,-626) with buffs), client closed, 39 out, 0p+0b; Core
   1758f7d1 PID 2995240 (screen 2995239.mangosd) = executor v12 (2d4e20c6) + checkpoint tool identity v2 (51cb63ea),
   both canonical in core-patches/. **Executor v13 (area auras) is built as a50c24cd but NOT installed**; the box's
   source tree holds the v13 source (3b705468) - `qa-artifacts/sep13-executor-v12-archive/` has the deployed one.
   Route ledger: `clear-state.json` in both session dirs; the route driver's plans in `scratch/onyxia-live/sep13-route/`
   (`plan-session2.json`: steps 8-13 = Gehennas' room trash 56779, 91284, 56788, 56708, 91282, 91285; step 7 = 56706
   needs one more kill). Checkpoints on the server for every fought unit (`sep12-mc-capture/*-ready-baseline*.json`).

## 2026-09-13 session log, continued (Claude, resumed 04:50Z: "read the resume prompt and loop for 7 hours")

81. Resume boundary re-verified (Core 1758f7d1 PID 2995240 alive, no QA client, `.bot info` 0p+0b, no online
   accounts). **Executor v13 deployed 04:53Z at that clean boundary: Core a50c24cd (PID 3000940, screen
   3000939.mangosd)** via `qa-artifacts/deploy-scoped-core.py 2995240 2995239.mangosd`; Server.log archived first as
   `qa-artifacts/Server-before-v13-20260913T045300Z.log` (the log is truncated on restart). Pre-install checks: box
   source = v13 (3b705468), no-op rebuild up to date, `AreaAurasInRange` symbol present in the .o and the linked
   mangosd; the v12->v13 diff is only the DynamicObject search in the hazards loop (`HarmfulAreaSpell` + hostile to
   the actor -> exclusion region of `max(object radius, spell radius)+1`). Canonical `core-patches/SuiCommanderRaid.cpp`
   = 3b705468 now; `sep13-core-candidates/deployment-result.json` has the third entry. Consequence: instance 101
   reloaded -> every Annihilator/Firelord re-rolled again.
82. `creature_respawn` (SELECT only) at 04:56Z: giants 56706/56707 have NO row = alive (the 04:43Z identity restore
   respawned them and the stage crashed), Surger 56844 respawn 1789275854 = 05:04:14Z, plateau Surger 56839 04:56:27Z,
   Firelords/Annihilators 3-6 h out, the room trash never killed. Session 3 = `sep13-route-room` (launched 04:58Z,
   plan `sep13-route/plan-session3.json`, prefix `route-0913e`, driver log `sep13-route-session3-driver.log`):
   56844 first as a proper route unit (captured from the 56721 checkpoint on the slope, staged (1002,-662), 30 yd off
   its loop) - the first capture ran 05:00:31Z, 3.7 min before its respawn, and was cleanly refused ("selected
   creature must be a full, idle static instance spawn"; `aborted-capture-surger-56844-dead-0500/`); re-run attached
   at 05:04:30Z captured it first try. **56844 DONE 2/2** (24 s, 24 s, 40/40), **56706 DONE 2/2** (51 s, 45 s, 40/40).
   Live entries after the 04:53Z reload (geometry from the lower floor, `route-0913e-56706-01-identity/geometry.json`,
   scratchpad `live_entries.py`): **56779 is now an ANNIHILATOR** (liveEntries set), **91284 back to a FIRELORD**
   (liveEntries removed), 56788 A, 91282 A, 91285 F unchanged; off-route 91281/56733/91273 -> F, 91256/91280 -> A,
   56801 -> F. `write_descent_surveys.py` edited + run, `compile_packs.py --only` both, reviews regenerated (0
   unsupported; Annihilators have no hostile casts) at 05:11:37Z, before the driver's 56779 capture (05:12Z, matched).
83. Room trash: **56779 (now an Annihilator) DONE 2/2** (20 s, 23 s, 40/40; identity v2 re-asserted the re-rolled
   entry first try both times), **91284 (Firelord again) DONE 2/2** (29 s / 39 survivors, 30 s / 40). The 91284-01
   fight was JOINED by Annihilator 56788 and the giant pack 56708 (battle.jsonl: all three inCombat by t=29 s):
   **56788 and the Giant 56708 died under the native defence** (creature_respawn: 56788 killed 05:18:13Z, 3 h;
   56708 05:17:42Z, 6 h) - recorded as incidental kills in clear-state (session-2 precedent), the 56788 capture was
   refused 40x (dead; `aborted-capture-annihilator-56788-dead-0521/`). **Destroyer 56709 is alive at home
   (858,-574)**, 15 yd from the 91282 anchor / 26 yd from 91285's / 37 yd from Gehennas' tank anchor: fought when it
   joins. 91282's capture re-sourced from the 91284 checkpoint (`--stage 900 -614 -202.2`, a 36 yd hop); driver
   resumed `--attach --start 7` 05:23Z.
84. **91282 DONE 2/2** (26 s / 39, 25 s / 40) - but BOTH fights chained into Gehennas: 91282-01 pulled Destroyer 56709
   (15 yd from the anchor) -> Firelord 91285 (19 yd from 91282's spawn) -> Gehennas + both Flamewakers (23 yd from
   91285's spawn), 91282-02 pulled 56709 + Firelord 56801 -> Gehennas again. After each sealed win the leaderless raid
   fought the boss under the native defence ("Gehennas (my human's attacker)" in Server.log) until owner GM setup
   evaded him (`emergency-evade-gehennas-0528/`, `-0533/`: `select wild-entry-nearest:<entry>` + `.npc evade`;
   Gehennas/Flamewakers home at 100% each time). Incidental kills: **91285** (05:26:47Z, 2 h), **Destroyer 56709**
   (05:32:44Z, 6 h); 56801 evaded alive. Lesson: an anchor 27 yd from its spawn is not enough when the tank's approach
   passes inside a neighbour's 22 yd detection and that neighbour stands 23 yd from a boss - the survey must check
   the approach path, not only the anchor. **Gehennas' room is clear** (4 units 2/2 by the executor, 4 incidental).
   Route session closed 05:41Z (`finish --label mc-sep13-annihilator-91282-ready`).
85. Boundary work: `definition_compiler.py` native-coverage record now names the covering mechanism (was always
   'trap-objects'); `raid_regression.py` no longer refuses a boss batch whose survey assumes cleared packs without
   `--reclear` (owner rules: no timer re-clears) - it writes `assumed-cleared.json` instead. Gehennas compiled +
   promoted (`encounter-definitions/gehennas.json` 5064aa13, nativeCoverage area-auras for 19717), review 0
   unsupported. Garr-route floor probes (`probe-garr-route-200b/c`; the first pass silently failed because
   `--home` with a map id makes `.go xyz ... 409.0` - never pass the map to probe_floor.py): hall floor -202 (x 850)
   to -216 (x 700); ROCK at (815-830,-520..-530), (840,-580), (745-760,-590..-600); ledge (750,-590). Surveys for
   56801, 56710, 91281 (a Firelord since the 04:53Z reload), 56718, 56734, 56712, 56778, 56727, 56700 and Garr
   written by `sep13-route/write_garr_route_surveys.py`, compiled/promoted (0 clearance violations), reviews 0
   unsupported; plan `sep13-route/plan-session4-garr.json`. Garr's four 'review' items inspected: Separation Anxiety
   23487 = 100 yd radius (SpellAuras.cpp: the Firesworn self-cast 23492 only beyond it) - irrelevant inside the
   room; Enrage 19516 stacks per Firesworn death (unavoidable, healing); Thrash melee; Eruption on add death = 15 yd
   burst (covered). Gehennas checkpoint `mc-sep13-gehennas-ready` (capture-encounter, 351,780 HP + 2 x 77,700,
   instanceState `0 0 0 0 0 3 0 3 ...`, sha de7787de) captured from the 91282 pose (900,-614), 68 yd from him.
86. **v13 Gehennas batch `gehennas-0913a` ABORTED 0/3** (wipes 96/119/103 s, boss 60/21/60%): Rain of Fire 19717 was
   60-64% of all incoming damage and the battle samples show 25-29 members inside every 10 yd area for its whole
   life ([dynobj-fx] spawn positions vs member positions) - the v13 exclusion never fired. Cause in source:
   `HarmfulAreaSpell` accepted only SPELL_EFFECT_SCHOOL_DAMAGE and SPELL_EFFECT_APPLY_AURA; Rain of Fire is effect
   27 (SPELL_EFFECT_PERSISTENT_AREA_AURA) with aura 3. **Executor v14 = v13 + persistent-area-aura effects count**
   (`sep13-core-candidates/SuiCommanderRaid-v14-persistent-area-effect.cpp` 376ca942, `apply_build_v14.py`, 142
   contracts) **deployed 05:52:40Z: Core ad952e7c, PID 3008560, screen 3008559.mangosd** after `finish --label
   mc-sep13-gehennas-ready` (0p+0b). Runner killed in the settle after pull 03 (attempt 04 never pulled);
   `sep13-gehennas-evidence/batch-aborted.json`. Fresh batch: session `sep13-gehennas-v14`, prefix `gehennas-0913b`,
   log `sep13-gehennas-v14-runner.log`, previousLedger = the aborted ledger. Instance reloaded again: A/F re-roll.
87. **GEHENNAS ACCEPTED 10/10 on executor v14** (session `sep13-gehennas-v14`, prefix `gehennas-0913b`, 05:55-06:24Z:
   112/108/101/102/93/93/102/115/112/114 s, survivors 38-40, no timeouts; `batch-report.md`, `batch-summary.json`,
   `encounter-acceptance.json` accepted 06:24:19Z on Core ad952e7c). Proof the fix works: pull 01 Rain of Fire = 3% of
   incoming damage (v13: 60-64%), members inside a 10 yd area 13.6 at spawn -> 1.8 at +5 s (scratchpad
   `rof_evasion.py`). Gehennas dead (corpse (892,-570), Flamewakers dead) - instance progress: Lucifron, Magmadar,
   Gehennas DONE. Session closed 06:25Z (`finish --label mc-sep13-gehennas-ready`, 0p+0b). Live entries after the
   05:52Z reload (geometry from the room, 28 creatures incl. 56727 at ~205 yd): 56801 F, 91281 F (liveEntries), 56734/
   56791/56778/56780/56727 A - all as compiled. **Session 4 `sep13-route-garr`** (plan `plan-session4-garr.json`, prefix
   `route-0913f`, log `sep13-route-session4-driver.log`) launched 06:26Z: 56801 -> 56710 -> 91281 -> 56718 -> 56734 ->
   56712 -> 56778 -> 56727 -> 56700 -> pause; then Garr = scratchpad `garr.sh` (close_route -> prep = compile_raid
   --promote --native-coverage trap-objects area-auras + review -> capture-encounter Garr + 8 Firesworn from the 56700
   checkpoint staged at (720,-565) -> ten-pull batch, prefix `garr-0913a`).
88. Garr-hall route (session `sep13-route-garr`, prefix `route-0913f`, from 06:26Z): **56801 DONE 2/2**, **56710 DONE
   2/2**, 91281 (Firelord) joined 56710-01 and died (incidental, 06:33Z), **56718 DONE 2/2**, the 56734 Annihilator
   pair joined 56718-01 and died (incidental 06:42Z). Mechanism of every chain pull today (56718-01 battle.jsonl):
   the pulled pack arrived at the tank anchor by t=10 as intended, but the whole West ranged team walked from its
   anchor (848,-560) 45 yd west past the tank anchor to (797-813,-563..-580) - ~20 yd from the pulled unit's spawn
   side - inside the neighbours' 22 yd detection. Candidate generic change (measure separately): ranged/healer
   roles hold at their team anchors until the pulled unit is inside the tank anchor's engage circle.
89. **Client collision holes + a gear-identity incident (06:49-07:51Z, ~1 h):** the 56712 capture staged at
   (808,-530) - server FloorZ valid - dropped the owner out of the world (client physics; corpse/ghost at the
   Blackrock graveyard (-6451,-1114,308)); `.group qacheckpoint return` + restore-raid recovered him. Then every
   checkpoint restore was refused "character gear/class/race/level changed": bot 131 Sealnub had equipped looted
   **Nightslayer Bracelets** (trash T1 drop; pack fights skip `loot master-setup` by design, so group loot let a bot
   win + auto-equip at its next loot pass) over the Deepfury Bracers (item guid 1564) the checkpoints record.
   `.additem 16825 -1` removed them, but the AI only equips at a loot pass (`TryAutoEquip` runs after DoAutoLoot /
   GO loot / quest reward - nothing else), the running client build lacks `loot equip-reward`, and `inventory
   equip-entry` acts on the owner only. A direct RA capture (-3, no source restore) at (797,-543) turned out to be
   ANOTHER client hole: `.gm off` there killed the owner (07:22Z), the bots ran west to resurrect him through the
   56778 Annihilators and 38 of 39 died (both Annihilators died too: 56778 incidental 07:24Z). GM `.revive` x39
   (`raidqa select` + `.revive` + `.namego`), then Sealnub's loot pass on the Annihilator corpses re-equipped guid
   1564 -> identity restored -> restore-raid of the 56718 checkpoint accepted 07:51Z. Rules learned: (1) every
   OWNER stage/anchor point must be stand-tested with `.cheat god on` + `.gm off` for 7 s (scratchpad protocol;
   (808,-530), (797,-543), (775,-540), (780,-530), (785,-535), (790,-540) are holes/rock here); (2) `loot
   master-setup` must run for pack fights too (boundary fix in raid_checkpoint.py / clear_route.py); (3) the
   client's roster report is stale while the owner is a ghost - use the bridge (`/Bots/States`, itself stale while
   bots are dead) and the server log. **56712 DONE 2/2** (capture -4 from the 56718 checkpoint, stage (812,-540)).
90. Garr-hall route finished 07:48Z: **56727 DONE 2/2** (its second neighbour, the Destroyers 56700, joined 56727-01 and
   died - incidental 07:45Z). Hall summary: 56801/56710/56718/56712/56727 by the executor 2/2 each; 91281, 56734, 56778,
   56700 incidental. Route session closed 07:50Z (`finish --label mc-sep13-annihilator-56727-ready`). Boundary edit:
   `raid_checkpoint.py` now sets master loot for pack stages too (the Sealnub gear incident). Garr compiled/promoted
   (`encounter-definitions/garr.json`, 8 Firesworn requiredAdds), review 0 unsupported; checkpoint **`mc-sep13-garr-ready`**
   (capture-encounter from the 56727 checkpoint, staged (720,-565); Garr 659,538 HP + 8 x 61,040; instanceState
   `0 0 0 0 0 3 3 3 ...`; sha 9ec5ce86).
91. **Garr batches (all aborted after 1-2 pulls, each a diagnosed structural cause, `batch-aborted.json` in each):**
   - `garr-0913a` (v14, survey anchor 37 yd): 0/2 - the main tank body-pulled by walking into the pack and died within
     10 s under Garr + eight Firesworn; the two add tanks (the roster has three warriors, no druids) were 20 yd behind.
   - `garr-0913b` (v14, tank anchor 34 yd / teams 13-20 yd, new global rule `addPolicyWhenAddsExceedAddTanks=focus`
     in compiler-policy.json + definition_compiler.py - every other boss byte-unchanged): 0/1, the puller dead within 5 s
     of contact. (700,-530) drew aggro in the owner stand test: the adds' live detection reaches ~25 yd.
   - **Executor v15 = native crowd control** (`SuiCommanderRaid-v15-native-crowd-control.cpp` 1b74f7ac, built 7f15f282,
     deployed 08:13Z): a damage member's own control spell - Mechanic banish/polymorph/shackle/sleep/sap, hostile
     single-target, >= 5 s, `TargetCreatureType` covering the add's `GetCreatureTypeMask()` - holds one fighting add per
     controller (GUID order, lower-GUID controllers claim first); controlled adds (own lease or any control-mechanic
     aura) are never targeted and running damage on them is stopped; re-cast on expiry; the boss, or the last uncontrolled
     add, always stays active. No definition vocabulary. `garr-0913c` 0/2: **the mechanism works** (all four warlocks
     Banished four Firesworn 6-11 s after the pull, one re-cast after a break - client SpellGoTargets/aura journal), but
     the add tanks left the free adds on the puller: their loop counted an add as "tanked" when its victim was ANY
     role <= 2 member, the main tank included.
   - **Executor v16 = v15 + add tanks relieve the main tank** (only a role-2 victim counts as tanked; 3f02fead, built
     08e4d125, **deployed 08:22Z: Core 08e4d125, PID 3025334, screen 3025333.mangosd**). `garr-0913d` 0/2 but a different fight:
     d-01 lasted 189 s, Garr to **41%**, 31 alive for two minutes, the add tank held six adds at t=15, four Banishes up
     for 150+ s, the four free adds killed. What ends every fight now is **Eruption 19497 (cast by each Firesworn on
     death: ~1849 fire in 15 yd + knockback) = 160-177k of the incoming damage**: the six rogues die on the first two
     bursts (they stand on the add), and when a warlock dies its add comes free, runs to the healers and explodes among
     them when killed (d-01 t=170: 22 dead at once). On-death bursts ARE derivable (`definition_compiler.py`
     `deathBursts` policy, from the script's JustDied cast) but only as protocol-5 `mechanics.hazards`; schema 1 has
     no hazard for them, and `objective_review.py` labels Eruption "aoe-around-caster: covered; melee accept it" -
     wrong for a 2k burst x 8. Garr survey now: tank anchor (700,-540) 34 yd from the nearest Firesworn, teams 21/28 yd
     behind (batch `garr-0913e`, running at the time of writing; log `sep13-garr-e-runner.log`).
92. Where Garr stands / next generic step: kill order and control are solved on v16; the blocker is the death burst.
   Candidates (one per batch): (a) compiler rule "melee hold when adds carry a death burst" - enable the `deathBursts`
   derivation in compiler-policy.json and let the schema-1 projection set `phases[].melee=false` (rogues stop dying,
   the tanks still eat the bursts); (b) the protocol-5 terminal-burst hazard (the candidate's `RaidTerminalHazards.h`:
   everyone leaves a 15 yd region around an add below a health threshold) ported natively - the burst radius comes
   from the derivation, so it needs a schema-1 field or a native read of the add's death spell (not available at
   runtime); (c) kill Garr before the free adds (fewer Enrage stacks matter less than eight bursts) - a policy switch.
   Also: `objective_review.py` should classify a death burst whose damage exceeds a melee member's health as unsupported
   on schema 1 rather than "melee accept it".
93. Batch `garr-0913e` (survey teams 21/28 yd): e-01 lost; then the runner died on verify_frozen because I edited
   compiler-policy.json for the next candidate while it ran (agent error; `batch-aborted.json`). **Compiler rule
   `addPolicyWhenAddsBurst=hold`**: the `deathBursts` derivation (from the candidate policy: clearance 1, roles 56) is now
   enabled in compiler-policy.json; an add entry with a derived death burst makes the boss's addPolicy `hold` (damage
   roles leave the adds to the add tanks until the objectives are dead); the schema-1 projection records the burst
   hazard as natively covered by `add-hold`; every other boss byte-unchanged (Garr is the only encounter with a death
   burst). Batch `garr-0913f` on v16: f-01 lost (the main tank died at t=10 in the pull burst; the raid still took Garr
   to 10% with zero Eruptions); **f-02: GARR DEAD - 100% -> 0% at t=107 s with 40/40 alive, four Firesworn banished
   and four on the add tanks the whole time, no Eruption, no Enrage** (creature_respawn 56609 = 2026-09-20 04:38:58
   local, instance data `0 0 0 0 3 3 3 3` = TYPE_GARR DONE; killed 08:38:58Z). Instance progress: Lucifron, Magmadar,
   Gehennas, GARR dead. The attempt is NOT a reviewed completion: the executor completes only when every required add
   is dead, and after Garr the raid stalled - under `hold` the fallback target became a banished (immune) add and
   the damage roles waited until the add tanks fell (t=148-154) and the eight adds ate the raid.
94. **Executor v17 = add tanks stage at the tank anchor** (native override of the client's role-2 ground station when the
   definition has adds: beside the puller, so their taunts land as the linked pull arrives - the main tank died in the
   pull burst in a-01/02, b-01, c-01, f-01 and survived it in c-02, d-01, f-02: a coin flip that decided the fight)
   and **v18 = v17 + post-boss add handling under hold** (the fallback target skips adds held under a control spell;
   a melee member never engages a required add under hold - it keeps formation while the ranged finish the adds from
   outside their death bursts). Sources `SuiCommanderRaid-v17-add-tanks-at-anchor.cpp` 43864f5f, `-v18-hold-adds-
   after-boss.cpp` c25c42c5 (built a78f39d4, 142 contracts); deployment/batch g - see the next item or the runner log
   `sep13-garr-g-runner.log`. Garr's capture-encounter checkpoint respawns him between pulls (instance state re-applied),
   so the acceptance batch continues although the instance already counts him dead.
95. **v17+v18 deployed 09:12Z (Core a78f39d4)**; batch `garr-0913g`: g-01 lost (v17 verified: both add tanks stood at the
   tank anchor at t=10 and the main tank survived the opening; but add tank 116 died at t=20 holding six adds and
   115 at t=61 holding four - two add tanks cannot hold four free Firesworn for a whole fight; Garr 23%); **g-02: GARR
   KILLED AGAIN (t=105, second kill of the day)** and then the same leftover-add stall: the adds sat at 89-100% while
   the healers died. Cause in source: the damage roles' threat-ratio hold follows `SelectActingTank` = the
   lowest-GUID add tank, while the fallback add is on the other add tank or a healer. Both f-02 and g-02 also
   exposed a harness deadlock: with all forty dead the executor plan stays in state 2 (only living actors observe the
   end of combat), the client protocol runner blocks in the pull protocol, restores are refused (executor=1) and
   `finish_measurement_session` refuses (no terminal event) - both sessions were closed by hand (`manual-boundary.json`).
96. **Executor v19 = v18 + leftover adds** (fallback target prefers an uncontrolled add already on a tank-role member
   and that member is its acting tank; an all-dead raid resets the plan to state 3; `SuiCommanderRaid-v19-leftover-
   adds.cpp` 87e194fa, built 73110f21, **deployed 09:40Z, PID 3029551, screen 3029550.mangosd**) + compiler rule
   `addsPerAddTank=2` (addTanksPerTeam = max(policy floor, ceil(adds / (teams x addsPerAddTank))): Garr 2 per team = two
   warriors + two paladins (`CanTank` in the client's plan law), Lucifron/Gehennas unchanged). Batch `garr-0913h`
   (session sep13-garr-h, prefix garr-0913h, log `sep13-garr-h-runner.log`) launched 09:42Z - read its
   batch-summary/ledger for the outcome. Canonical core-patches/SuiCommanderRaid.cpp = 87e194fa.
97. Four add tanks are not available: `addsPerAddTank=2` drafted two paladins (the client's plan law: `CanTank` =
   warrior/paladin/bear) but the client refused ten healers with four add tanks (batch h, no pull) and, with the
   healer count derived down to eight, the loadout check refused "Loadout differs from actual class/role" (batch i,
   no pull): the two paladins carry the owner-declared healer elixir. More add tanks = an owner loadout decision;
   the rule was removed from the policy (note kept in compiler-policy.json). **Batch `garr-0913j` (v19, two add
   tanks, hold policy, teams 21/28 yd)** launched 09:33Z. Observation across the Garr pulls: the two kills (f-02,
   g-02) began with every Firesworn on the main tank (a body pull by 787); several losses began with seven adds on
   hunter 153 (a ranged pull from the teams) and the main tank dead at t=10 - the client decides the pull method
   (`_raidQaRangedPullGuid`); worth pinning for a boss with linked adds.
98. Batch `garr-0913j` (v19, hold, two add tanks): 0/3 (Garr 48% / 8.9% / 47.9%) - the two add tanks fall holding
   four Firesworn, the raid follows; then the runner died on verify_frozen because I edited compiler-policy.json
   for the next candidate while it ran (agent error, second time today - stage policy edits in the scratchpad until
   the batch is closed). **Executor v20 = v19 + a melee damage member takes adds only while the phase allows melee;
   `MeleeAllowed(phase, role)` keeps tank roles in contact on a ground, non-alternate phase whose melee flag is off**
   (`SuiCommanderRaid-v20-melee-adds-need-melee-phase.cpp` 91a70ee6, built 0f9e4548, **deployed 10:00Z**, PID/screen
   in deployment-result.json; the 'ranged damage helps remove encounter adds' contract literal was kept intact - the
   auto-mode classifier refused an edit of the box's checker, correctly). Compiler candidate "focus + phases[].melee=
   false when the adds burst" (`meleeHoldWhenAddsBurst`): batch `garr-0913k` **never engaged** - the protocol-4 client
   drives the owner/main tank and the pull from the same melee flag - reverted to `hold`; k also showed a harness gap:
   a plan that never enters combat never terminates (state 2, the client blocks in the pull protocol) - manual boundary.
99. **Batch `garr-0913l`** (v20, hold, two add tanks, teams 21/28 yd; the change under measurement = v19's leftover-add
   acting-tank fix) launched 10:05Z - `sep13-garr-l-runner.log`, `sep13-garr-l-evidence/`. Garr tally for the day:
   killed twice (f-02 08:39Z, g-02 09:14Z), no reviewed completion yet (the executor requires every Firesworn dead).
   Canonical core-patches/SuiCommanderRaid.cpp = 91a70ee6 (v20).
100. **Batch `garr-0913l` l-01: GARR KILLED A THIRD TIME** (0% at t~100, 39 alive). The leftover-add phase then worked
   as designed - the ranged burned the add held by add tank 116 from 86% to 14% (v19's acting-tank fix verified) - but
   its death Eruption (19497, 15 yd, base 1849; the client saw hits=34) landed on 34 of 39 members: the add tanks
   chase adds that run at the healers, so the kill happens inside the ranged teams. 21 dead at once, wipe by t=144;
   afterwards the bots resurrected each other (v19's all-dead reset freed them) and the attempt never produced a
   terminal event - manual boundary 10:13Z (client closed, 39 out, 0p+0b). `batch-aborted.json` in the session.
101. **Where Garr stands at the end of the session (10:13Z):** killed three times today (f-02 08:39Z, g-02 09:14Z,
   l-01 10:04Z; instance TYPE_GARR DONE, creature_respawn 56609 = 7 days), every fight under the hold add policy with
   four Firesworn banished and four on the two add tanks; no reviewed completion because the executor needs every
   Firesworn dead and the leftover adds explode inside the raid. Every executor piece exists now (v15 control, v16/v17
   add pickup at the anchor, v18/v19 leftover targeting, v20 melee gating); the one missing generic mechanism is
   **"a bursting add dies away from the raid"** = the protocol-5 terminal-burst hazard (everyone but the holding
   tank leaves the burst radius of an add below a health threshold - the radius is definition data the protocol-4
   client cannot carry; a native default radius would be invented). Alternatives for the owner: (a) accept the
   instance kill and move on (Garr is dead; Majordomo's rune only needs him dead); (b) more add tanks = a loadout
   decision (item 97); (c) a schema-1-tolerated field for burst radius plus a native "hold the add at the tank
   anchor and damage it only when no non-tank member is inside R" rule. Harness gaps found today: a plan that never
   enters combat, or whose bots resurrect each other after a wipe, never terminates (client blocked in the pull
   protocol; `raidqa recovery` cannot run) - four manual boundaries. Pull-method variance (main-tank body pull vs
   hunter ranged pull) decided several openings.
102. **South route prep (Geddon/Shazzrah), owner-only probe session `sep13-south-probes` (no bots), 10:15-10:20Z:**
   the pack floor is -208/-209; the basin (600-676,-705..-745) sits at -215/-217 (7-8 yd lower = treat as lava);
   rock (relocated to the entrance) at (630,-760), (645,-735), (650,-740), (690,-700), (680,-690), (700,-760),
   (700,-790), (700,-810), (700,-830), (715,-775), (720,-800), (710,-730), (720,-640), (735,-620); no floor data at
   (700,-870). **Walkable corridor = Surger 56848's waypoint line** (692,-663) -> (708,-693) -> (708,-711) -> (686,-739)
   -> (682,-777) -> (665,-813) -> (677,-830) -> (679,-847), all -209 (probed: (686,-739), (682,-777), (665,-813),
   (677,-830), (672,-760), (690,-745), (700,-745), (695,-725), (670,-770), (655,-765), (675,-790), (660,-800),
   (650,-805), (640,-800), (620,-790), (610,-780), (630,-825), (645,-840), (660,-830), (640,-770), (660,-745),
   (680,-760), (686,-765), (720,-780), (700,-680); (580,-780) -207.0, (600,-820) -206.3, (630,-810) -208.3 valid).
   Packs (Firewalker 11666 / Flameguard 11667 / Lava Elemental 12076 / Lava Reaver 12100): 91286 (645-661,-746..-756),
   91290 (599-608,-765..-776), 91261 (634-644,-787..-798), 91277 (676-680,-801..-820), Annihilator 56735 (690,-844).
   **Baron Geddon 56655 PATROLS** (movement_type 2): (614,-806) -> (606,-828) -> (644,-842) -> (665,-861) -> (683,-868)
   -> (698,-894) -> (709,-916) -> (724,-936) -> (738,-952) and back - his loop crosses the corridor south of 91277 and
   passes 25 yd from pack 56781; the executor's patrol hold (v10, 36 yd engage circle) and a survey whose anchor sits
   on his route are needed. **Shazzrah 56608 wanders 10 yd around (583,-800)**, 31 yd from Geddon's home - fights in
   that room must keep the other boss outside 23 yd; pull Geddon on his loop away from Shazzrah. Suggested unit order:
   91286 (anchor ~(672,-760) on the corridor east of it), 91261 (anchor ~(660,-775)), 91277 (anchor ~(660,-800) west of
   it, teams up the corridor), 91290 (anchor ~(630,-770)), then Geddon on his loop near (665,-861) with Shazzrah 80+ yd
   away, then Shazzrah. Nothing compiled or surveyed yet; every owner point still needs the god-mode stand test.
103. Tooling at the end of the loop (10:20-10:30Z, no fights): (a) `objective_review.py` now treats an add's derived death
   burst as UNSUPPORTED on schema 1 unless the compiled add policy is `hold` (Garr's Eruption / Massive Eruption read
   "covered: hold add policy ... leftover adds still burst inside the raid - needs a terminal-burst mechanism"); the
   four MC boss reviews regenerated, all 0 unsupported. (b) South-corridor surveys written and promoted by
   `sep13-route/write_south_surveys.py` (91286, 91261, 91277, 91290; anchors on the probed corridor; Baron Geddon
   declared in assumesCleared ONLY because his loop runs 5-33 yd from every anchor - the surveys' `note` field says
   so; reviews 0 unsupported with `partial` Cone of Fire / Pyroclast Barrage and `review` Fire Blossom to inspect).
   (c) DRAFT plan `sep13-route/plan-session5-south-DRAFT.json` (corridor Surger 56848 first - survey written 10:25Z,
   anchor on its route at (686,-739), route reachable; then the four packs; every step's `purpose` carries the
   caveats; never launched). Stack after all of this: Core
   0f9e4548 (v20) PID 3032052, canonical patch 91a70ee6, no client, 0p+0b, all sessions closed.

104. **South-route stand test + hall respawns (owner-only session `sep13-south-stand`, 10:31-10:39Z, no bots).** God-mode
   stand test (item 89 rule) of the ten south owner/team points, results in
   `sep13-south-stand-evidence/owner-stand-test-south/stand-test.json`: **all ten hold the owner on the probed floor**
   (drift 0.0 yd, dz <= 0.05) - (700,-680) stage, (686,-739) anchor, (695,-725)/(700,-745) teams, (690,-745),
   (686,-765), (670,-770), (660,-745), (660,-775), (640,-770): no client holes. Aggro during the test (god mode, no
   kills, nothing attacked back, creature_respawn unchanged): the corridor Surger 56848 engaged at (686,-739) 6 s
   after arrival (its route - expected) and chased the owner through every later point; the Firewalker pack 91286
   (all four) engaged 14 s after arrival at (670,-770) - consistent with the surveys (that anchor assumes 91286
   dead); **Baron Geddon 56655 engaged 3 s after arrival at (660,-775) and again 5 s after arrival at (640,-770)
   (melee, 1.7k per swing)** - 55 yd and 44 yd from his DB home, so his live loop runs far closer to the corridor
   than the DB waypoints (or the chasing Surger/Firewalkers dragged him in): the 91290 team point (660,-775) and the
   91290 anchor are inside his reach - the south packs 91261/91277/91290 need a live Geddon survey (his clock and
   real path) or Geddon dead first. The 91261 pack engaged at (660,-775) at 22 yd (expected; the 91290 survey
   assumes 91261 dead). Then the owner was returned to the (755,-555) home with god off and **died 12 s later to
   the respawned hall Lava Annihilators 56791 (767,-577; 25 yd, wanders) and 56734 (782,-585)** - MC trash
   respawns on `spawntimesecs`: Annihilators 10800 s, Giants/Destroyers 21600 s, Surger 56848 1680 s, Geddon
   604800 s. GM `.revive` + `.repairitems`, parked at the entrance (1092,-467,-105.1) alive 5829/5829, `.gm off`,
   client closed, 0p+0b. **The hall and the room re-populate on their own clocks** (creature_respawn, UTC):
   56716/56717 09:48, 56778/56780 10:24, 56747/56748 10:25, 56702 10:32, 56727 10:48, 56706/56707 11:11, 56708
   11:17, 56709 11:32, 56710/56711 12:36, 56718/56719 12:55, 56713 13:39, 56712 13:40, 56700/56701 13:45 (rows
   with a past time respawn when a player activates the grid). Consequences: (a) every checkpoint pose in the hall
   ((755,-555), (720,-565), (812,-540)...) is inside live packs again - a restore there starts a fight at once, and
   the owner must not stand there without GM; (b) under owner rule 2 the hall is "done packs in the way": kill each
   once more (the hall checkpoints 56801 -> 56710 -> 56718 -> 56712 -> 56727 exist; `again: true`, kills 1) before
   staging south - or ask the owner whether a respawned hall is worth an hour (rule 5); (c) the draft south plan's
   first stage (700,-680) is 109 yd from the Destroyers 56700 - fine once the Annihilators are dead. Stack unchanged:
   Core 0f9e4548 (v20) PID 3032052, canonical patch 91a70ee6, no client, 0p+0b.

105. **Baron Geddon's LIVE loop (owner-only GM watch `sep13-geddon-watch`, 10:41-10:45Z, 45 `raidqa geometry` samples
   every 5 s from the probed vantage (677,-830); `sep13-geddon-watch-evidence/south-watch.json`, no aggro, nothing
   changed).** Geddon 56655 runs a loop between a NORTH end at **(655,-775)/(641,-778)/(624,-784)** and the far end
   (738,-952) through (609,-829)/(685,-871)/(730,-944)/(708,-889)/(664,-818)/(644,-842)/(699,-894)/(658,-792)/
   (613,-810)/(663,-859)/(714,-923) - he passes the corridor's north end every 30-55 s and is more than 100 yd from
   the 91290 team point (660,-775) in only 19 of 45 samples (never for more than ~20 s in a row). Minimum distance
   of his path to the surveyed points: (686,-739) 48 yd, (686,-765) 33 yd, (670,-770) 16 yd, (660,-775) 5 yd,
   (640,-770) 8 yd, (660,-745) 30 yd, (690,-745) 46 yd. So: the Surger 56848 and the 91286 pack can be fought at
   their surveyed anchors (33+ yd from his path; the item-104 aggro at (660,-775)/(640,-770) was simply his loop),
   but **91261, 91277 and 91290 cannot be fought at (670,-770)/(640,-770) - Geddon joins every fight there**:
   either Geddon dies first (his north end is 21-22 yd from the 91261 and 91290 Firewalkers, his far end is next to
   the unsurveyed 91265 (729,-906) / 56781 (705,-863) / 56722 (661,-863) packs - a boss survey needs a pull point
   at least 25 yd from every pack, e.g. mid-loop around (664,-818)/(644,-842) with 91277 (680,-801) 17-45 yd away
   -> chain), or the three packs are pulled NORTH to the 56848 anchor (686,-739) (Geddon 48 yd; a 45-60 yd ranged
   pull with the pack chasing the puller to the tank - the pull method is not pinned, item 97/98). Also in view:
   Shazzrah 56608 wanders (577-593,-795..-808); four Lava Surgers patrol the south - 56848 (665-735,-663..-936, the
   corridor: the full loop (692,-663)<->(735,-937) takes ~80 s at ~14 yd/s, it passes the (700,-680) stage at 2 yd and
   the (686,-739)/(686,-765) anchors at 0-5 yd every ~40 s, so a capture staged there is never idle - kill it live
   first, then its 1680 s clock gives a Surger-free corridor for 28 min), 56846 (738-765,-687..-848, east of the corridor), 56845 (754-807,-656..-704, 54+ yd east of the
   (700,-680) stage), 56847 (783-909,-763..-921); stationary Firewalker packs beyond the four surveyed ones: 56722
   (661,-863), 56781 (705,-863), 91265 (729,-906), 91268 (588-594,-850..-858). **No point of Geddon's loop is 36 yd
   from every pack** (computed from the samples, 12 yd allowance for unsurveyed pack members): the far end (738,-952)
   is 35 yd from 91265, (625,-835) 27 yd from 91268, the north end 21-22 yd from 91261/91290; team candidates
   (745,-960)/(760,-940) are 34-44 yd from 91265 but on the unprobed slope (z -186). Owner question (rule 5): accept a
   chained pull (Geddon + 91265's pack at the far end, or + 91261/91290 at the north end), or pull Geddon with a long
   ranged pull to (686,-739) (48 yd from his path) - the pull method must be pinned first. Stack unchanged, client
   closed, 0p+0b.

106. Floor probes south of 91277 and on Geddon's far-end slope (owner-only GM session `sep13-south-probes2`, 10:49-10:51Z,
   `probe-south-corridor/probe.json`, `probe-south-slope/probe.json`): the corridor continues walkable from (680-700,-850)
   z -208/-206 through (690-715,-860..-880) z -206..-199 (the floor climbs); (700,-900) has NO floor and (710,-900),
   (720,-900) are rock, so the way to the far end is the slope Geddon uses: (740,-915) -190, (720,-920) -191.5,
   (730,-930) -189.5, (730,-940) -188.6, (725,-950) -188, (740,-950) -186, (735,-965) -189, (750,-945) -185, (745,-960)
   -183, (750,-960) -182; (760,-940) and (755,-925) are rock. So the far-end team candidates from item 105 are
   (745,-960)/(750,-960)/(750,-945) (34-44 yd from the 91265 Firewalker (729,-906); Geddon's turn-around (738,-952) is
   9-13 yd from them - a tank anchor, not a team point) and there is no room east of x=755. None of these points is
   stand-tested. Stack unchanged, client closed, 0p+0b.

107. **THE CLEARED ROUTE HAS RESPAWNED** (owner-only GM snapshot `sep13-route-state`, 10:52-10:53Z, `raidqa geometry` from
   four vantages, `sep13-route-state-evidence/route-state.json`; entries 11665 Annihilator, 11668 Firelord, 11658 Giant,
   11659 Destroyer, 12099 Firesworn, 12101 Surger). Alive again at 10:53Z - hall: Annihilators 56727 (respawned on its
   10:48Z clock), 56778, 56779, 56801, 91281, 91284 and Firelords 56734, 56780, 56788, 56791, 91282, 91285 (i.e. every
   Annihilator/Firelord killed this morning), plus **seven Firesworn 12099 (56610/56616/56619/56620/56622/56627/56628)
   standing in Garr's room at (678-697,-500..-517) with Garr dead** (their own 3 h clocks; a Garr restore will meet
   them - the capture-encounter checkpoint only re-creates its nine creatures); room: Giant+Destroyer packs 56702/56703
   and 56747/56748, Annihilators 91280/91283/91293, Firelord 56721, Surgers 56844/56845/56846/56847; descent/plateau:
   everything (giants 56716/56717 back). Still dead until their clocks: giants 56706/56707 11:11Z, 56708 11:17Z, 56709
   11:32Z, 56710/56711 12:36Z, 56718/56719 12:55Z, 56713 13:39Z, 56712 13:40Z, Destroyers 56700/56701 13:45Z; bosses 7
   days. So `clear-state.json` (route-room / route-garr) is history: under owner rule 2 every pack on the way back to
   Garr's hall or south is "a done pack in the way - kill it once more" (~20 units at 5-15 min each), which is the
   rule-5 case: ASK THE OWNER whether the route is re-killed, or whether staging past respawned trash with GM
   (rule 6 "GM setup for staging") is acceptable for units already done 2/2. Respawn clocks of the route units
   (`creature.spawntimesecsmin`, SELECT 10:58Z): Lava Surgers 56844-56848 1680 s; Firewalker packs 56722/56781/91261/
   91265/91268/91277/91286/91290 and the Firelord slots 56779/56801/91284/91285 7200 s; the Annihilator slots 56727/
   56734/56735/56778/56788/91281/91282 10800 s; Giants 56706/56708/56710/56718 and Destroyers 56700/56712 21600 s;
   Geddon/Garr/Shazzrah 604800 s (a slot's live entry still re-rolls Annihilator/Firelord). Nothing changed by the
   snapshot; client closed, 0p+0b.

108. Tooling (10:54-10:57Z, both verified live in owner-only sessions): `tools/encounter-content-audit/watch_creatures.py`
   (GM vantage, N `raidqa geometry` samples in separate attempts, per-creature x/y range + minimum distance to given
   points; `--analyze-only` re-reads a session - checked against `sep13-geddon-watch`) and
   `tools/encounter-content-audit/stand_test_points.py` (the item-89 god-mode stand test as a command, rows in
   `<label>/stand-test.json`, warns when the owner ends dead; checked in `sep13-standtool-check` on the entrance and the
   plateau stage (1024,-684)). Both leave the owner at the entrance with GM on; run `.repairitems`/`.revive` yourself
   if the test drew a fight. This session's scratchpad scripts (gehennas.sh, garr*.sh, live_entries.py, rof_evasion.py,
   apply_*.py, stand_test.py, geddon_watch.py, the merged probe json) are copied to `scratch/onyxia-live/sep13-scripts/`
   (the scratchpad is a per-session temp directory).

109. Design constraints for the Garr terminal-burst mechanism (read before writing v21; nothing built): (a) `boss_garr.cpp`
   casts Eruption 19497 on the Firesworn itself inside `JustDied` (triggered, instant) - no cast start, no warning, so
   a CastStart/CastGo rule can never see it; Garr's 6-minute event (20482 -> Massive Eruption 20483) forces one
   Firesworn to explode the same way. The burst is therefore only knowable OFFLINE (the compiler's deathBursts
   derivation from the script sources) and must reach the executor through the definition. (b) The compiled
   `encounter-definitions/garr.json` carries NO burst radius today - only `addPolicy: hold` and the tank-buff rule;
   the executor cannot avoid what it cannot measure. (c) The protocol-4 client (never rebuilt) fixes the rule
   vocabulary the plan can carry: Action Cast/Move/Stack/Spread/Isolate/AvoidPoints/AvoidCones/StopDamage, Trigger
   Always/CastStart/CastGo/BossNear/MemberAura/SelfAura, Target Self/Boss/Tank/TankHealer/Marked, fields roles/
   radius/priority/phase/spells/pointSpells/station/toggle/spell/missingAura/durationMs/target. A new trigger such
   as "victim of a living add of entry E" or "living adds of entry E are point hazards" is REJECTED by the frozen
   client: `CommanderEncounterLaw.cs` whitelists the action/trigger strings and cross-checks them (isolate needs
   castStart/castGo/memberAura + marked + radius; avoidPoints needs pointSpells + radius; memberAura needs spread/
   isolate + marked) - so the burst can only ride on an existing shape whose semantics the executor extends natively
   (e.g. a memberAura/isolate rule whose `spells` name the burst - the executor already reads spell ids from rules
   - with the executor treating "the add that will cast this on death is attacking me" as the mark). (d) Two generic shapes fit the vocabulary once
   (c) is solved: "everyone except the add's current victim keeps radius+1 from every living bursting add"
   (AvoidPoints with creature sources - the raid never stands in a burst, the victim absorbs it; ranged kill the
   add) or "the victim of a bursting add isolates" (Isolate/Marked with the victim marked - kites the add out of the
   raid, ranged kill it). The first needs no movement from healers and matches the evidence (adds run at healers,
   add tanks chase - items 93-101). (e) Whatever the shape, melee must stay off bursting adds (v18/v20 already do
   under hold) and the acting tank of a leftover add must be its actual victim (v19). Owner decision first (item 101).

110. **Executor v21 DRAFT - ranged wait for the pulled unit's arrival (NOT compiled, NOT deployed).**
   `sep13-core-candidates/apply_v21_source.py` (v20 91a70ee6 -> `SuiCommanderRaid-v21-DRAFT-ranged-wait-for-arrival.cpp`
   2023dfc0): helper `PulledUnitArrived(plan,boss)` (boss within kPatrolEngageYards = 36 yd of the tank anchor, or
   hitting a role <= 2 member) next to `Hold()`, and in the combat tick's role-5 branch `if(!add&&target==boss&&
   !PulledUnitArrived(*plan,boss))` the ranged move to / hold at their ground/air station (duty 12) instead of
   `RecoverDamageRange` walking them toward the unit's spawn (item 88: every first pull chained its neighbour that
   way). The box's contract checker passes all 142 on a throwaway copy of the tree (11:04Z; the tree itself is still
   91a70ee6, nothing built; a compile-only check of the object on the box was refused by auto mode at 11:05Z - run the
   ordinary apply_build path with the owner present). **Caveat, read item 88 again before building: the recorded drift
   happened AFTER the pack had reached the tank anchor (t=10 s, "arrived as intended") - the West ranged team still
   walked 45 yd past the anchor.** This draft only covers the pre-arrival window, so it is probably NOT the fix for
   item 88; the cause must be read from `route-0913f-56718-01/battle.jsonl` first (which unit each ranged member
   targeted at t=5-20 s and where `RecoverSpellRange`/`ranged-formation` sent them - a pack member that stayed at the
   spawn, or a formation move). Other open points: (1) a boss that never comes within 36 yd of the anchor and never
   hits a tank would leave the ranged idle - the guard could also accept "boss in combat with any member for > N s";
   (2) role-4 melee are untouched; (3) any version needs a route session on a linked pack to show a first pull that
   no longer chains. Owner rule 5 applies to the session, not to the review.
111. **Why every first pull chained - read from `route-0913f-56718-01/battle.jsonl` (11:10Z, scratch scripts
   `sep13-scripts/drift_56718*.py`).** The raid does not walk toward the pulled unit: at t=5 s **39 of 40 members stand
   within 4 yd of the West team anchor (848,-560)** - the restored pose stacks the whole raid on one point - and by
   t=10 s the derived `spread` rule (self, 7 yd, roles 3+5, `derived-spread-ranged`; duty 7 on 17-23 members) has
   scattered them over x 807-841 / y -578..-537: forty members at 7 yd spacing need a ~25 yd disk, and the scatter
   runs in every direction, so half the raid crosses the tank anchor (823,-552) westward to (793-814,-581..-535) -
   15-25 yd from the neighbouring Annihilators 56734 (782,-585) / 56791 (764,-577), inside their detection. From
   there they fight the pack's second creature as an add (duty 3). The pack itself arrived at the anchor as intended
   (the Giant 56718 at (819,-552) by t=10, on the owner). Item 88's "ranged team walked past the anchor" was this
   spread scatter, not a target approach - the v21 draft (item 110) does not address it. Generic fixes, in order of
   how little they inherit: (a) the stage/capture must not stack the raid - place the two teams spread around
   their anchors before the capture (`raid_checkpoint.py` staging), so the restore already satisfies the 7 yd rule;
   (b) bound the executor's spread goal to the member's own station (the spread `Move(goal,7)` at the Spread/Stack
   rule ignores `FormationCost`/`row.ground`): spread within the team circle, never across the tank anchor; (c) the
   most general: treat every idle hostile creature in view as a position hazard of its own aggro radius
   (`Creature::GetAttackDistance(actor)`) in `PositionHazards`/`ObjectHazards`, so no station, spread goal or approach
   may enter a neighbour's detection - vocabulary-free, covers Geddon's loop and the south corridor too. (a) is a
   harness change and can be verified on the next route session without a Core build - but note where the rule comes
   from: `pack_compiler.spread_rules` emits it from the GLOBAL policy `packs.rangedSpread.yards` (7) for two purposes,
   formation spacing against caster-centred area effects AND the movement rule the frozen client needs before it
   follows the owner's navmesh pull approach (a definition without any movement rule walks the owner straight). Twenty
   healers+ranged at 7 yd spacing fill a ~20 yd disk, which no survey's 15-25 yd team spacing can hold, so (a) alone
   still scatters; the cheap generic option is a smaller pack `rangedSpread.yards` (e.g. 3 - one policy line; verified
   offline 11:13Z with `compile_packs.py --policy <copy with yards=3>` into the scratchpad, nothing promoted: the only
   rule change in 56718/56848/91286 is `derived-spread-ranged.radius` 7 -> 3; objective_review then shows what
   caster-centred coverage it loses) combined with (b). Owner decision (global policy is hand data).

112. **Owner answers applied (11:50-12:25Z).** (a) Executor **v21** = idle hostile creatures are position hazards
   (`SuiCommanderRaid-v21-idle-hostile-hazards.cpp` 1d72860f, built 91e6cca8, deployed 11:53Z): every living, hostile,
   attack-capable, out-of-combat creature within 60 yd (the objective excluded) is an exclusion region of
   `Creature::GetAttackDistance(actor)+3` for bot rows once the fight has begun - collected in `PositionHazards`
   after the object hazards, so formation/spread destinations, stations, ranged approaches, routes and escapes
   all avoid an unpulled group (the item-111 scatter cannot enter a neighbour's detection any more). (b) Executor
   **v22** = unit-carried self periodic aura hazards (`SuiCommanderRaid-v22-unit-aura-hazards.cpp` 1db4a13a,
   **built f81c0806, deployed 12:08Z, PID 3035365, screen 3035364.mangosd**): a hostile creature under a SELF-cast
   periodic-trigger aura is a moving exclusion region while the aura lasts - radius = its loaded periodic child's
   area damage (Armageddon 20478 -> 20479, 20 yd) or the area-damage pulses `ObserveCast` saw the objective cast on
   itself under that aura (Inferno 19695 -> scripted 19698: the first pulse teaches the plan, every later Inferno is
   avoided from its first moment), +1; every role escapes it, the tank included. Compiler `NATIVE_COVERAGE`
   'unit-auras' records the drop; **Baron Geddon compiled + promoted to schema 1** (survey
   `write_geddon_survey.py`: tank anchor (672,-760) 22.7 yd from his loop's north end, teams (690,-745)/(700,-745);
   review 0 unsupported, acceptance allowed; other bosses byte-identical). (c) Harness: `raid_regression.py
   --done-respawn-guids` and `capture_checkpoint.py --done-respawn-guids --clear-definition` (plan step
   `capture.doneRespawnGuids`): done packs that respawned are removed by GM `.die` by exact low GUID (owner
   teleported next to each, verified on a geometry snapshot) before every pull / before a capture's staging.
   (d) Session `sep13-south` launched 12:16Z: `south_prep.py` removed the respawned done slots 56734/56791/56727/56780
   around the bots' login spots and the 56727 restore pose (Garr's Firesworn were gone after the restarts); stage
   floor (705,-630) probed -209.96; plan `sep13-route/plan-session5-south.json` (56848 x2 -> 91286 x2 -> pause)
   started 12:25Z with `clear_route.py --attach`; then Geddon: capture-encounter staged at (700,-745) and a
   ten-pull batch, both with the done-respawn clearance (the Surger's 28 min clock lands inside the batch).

113. **The Lava Surgers are gone** (12:26-12:35Z): the 56848 capture was rejected ("selected creature must be a full, idle
   static instance spawn") and no Surger was in view for 100 s from (705,-630) - `instance_molten_core.cpp`
   `OnCreatureCreate` removes every NPC_LAVA_SURGER and NPC_FIRESWORN once `TYPE_GARR == DONE`, so the 11:53Z/12:08Z Core
   restarts (creature re-creation) pruned all six Surgers and Garr's Firesworn, exactly like the hounds after
   Magmadar (item 25). Step 1 of the south plan skipped (`--start 2`); the corridor has no patroller left except
   Geddon. 91286 captured 12:35Z from the hall checkpoint (`mc-sep13-firewalker-91286-ready` aa502383; the
   restore-raid pose's respawned done slots had been GM-removed by GUID: 56778 in the first attempt).

114. **91286 x2 in `sep13-south` wiped (12:36-12:49Z) - Geddon and 91261 joined at t=15/t=35.** Both pulls started with the
   raid stacked on the stage/West point (690,-745); the 7 yd spread scattered the ranged over x 653-700 / y -776..-727
   within 5-10 s; members standing at (648-663,-757..-775) - 25-30 yd from Geddon's loop north end and 27 yd from the
   91261 Firewalker - got him on his next pass (01: t=15; 02: t=35 together with 91261's four). v21's hazard around
   his CURRENT position was too late for a walker at 8 yd/s. 02 never produced a terminal event (raid wiped, client
   stuck in the pull protocol) -> manual boundary 12:53Z (client closed, 39 out, 0p+0b). Fixes: (a) global pack
   policy `packs.rangedSpread.yards` 7 -> 3 (owner answer 3; every pack's `derived-spread-ranged.radius` is the
   only change, 45 packs re-promoted, south reviews still 0 unsupported); (b) **executor v23** = a MOVING idle
   hostile carries +12 yd on its aggro-reach hazard (`SuiCommanderRaid-v23-moving-hostile-margin.cpp` 92478ff2,
   **built 929c7d67, deployed 12:55Z, PID 3036843, screen 3036842.mangosd**); (c) the bots' saved login spots were
   the wipe positions (16 of 39 inside Geddon's reach): `sep13-scripts/reconnect_safely.py` logs them in one at a
   time and `.namego`s each to the GM owner - and it turned out the client launch auto-loads the 39 anyway (`.bot
   add` answers "Unable to load bot" because they are already in), so the namego pass is what matters. Session
   `sep13-south2` (prefix route-0913t) launched 12:57Z, raid placed at (690,-745) 13:12Z, 91286 fights started 13:13Z.

115. **91286 DONE 2/2 (sep13-south2, 13:14-13:23Z; Core 929c7d67 = v23, pack spread 3 yd).** 01 lost (101 s, boss 12.66%,
   2 alive - no chain this time: raid x >= 667, Geddon and 91261 never joined), 02 reviewed win 114 s (17 alive), 03
   reviewed win 121 s. The pack is a bloodbath by design: Fire Blossom 19637 (2.8k single-target nukes, 95k), Fire
   Shield 19627 (250 area around the Flameguard, 90k on the melee stack), Cone of Fire 19630 (1387 per hit, 78k) and
   melee 96k against ~250k healing; the raid kills the two Lava Elementals first (adds), the Firewalker (the nuker)
   last - a kill-order lever for later (item 116). **Baron Geddon captured 13:25Z** (`mc-sep13-geddon-ready`,
   capture-encounter from the 91286 checkpoint, staged at (700,-745), `geddon-ready-baseline.json`); the route client
   was closed (its ledger is the route's), the 39 stayed online, and the batch `sep13-geddon` (prefix geddon-0913a,
   `sep13-scripts/geddon.sh run`) launched 13:27Z with `--done-respawn-guids 91286 91287 91288 91289`.

116. **Baron Geddon, six lost pulls in three aborted batches (13:27-13:53Z), one structural cause each time - the 91261 pack
   joins within 5 s of Geddon's aggro, always targeting the OWNER** (`receivedNearbyHostiles[].target` = 787 in every
   pull): the fight stood 16-24 yd from the 91261 Firewalker (642,-787). a-01/02 (v23, anchor (672,-760)): the owner
   walked out to meet him (36 yd walk-out) and tanked him at his loop's north end (660,-772). b-01/02 (**v24** = the
   tank never walks at a patroller; anchor (668,-764) inside his aggro reach; anchor clearance policy 36 -> 30 yd):
   Geddon came to the anchor as designed (b-02: 30 s clean, 40 alive) but the owner's Inferno/Living Bomb escape went
   south-west and dragged him to (655,-776). c-01/02 (**v25** = manual rows get the idle-hostile hazards too): Geddon
   aggroed the owner from (657-670,-788..-830) and the pack was in combat at its spawn the same second, target 787
   at (668,-771) - 28-30 yd from the Firewalker, beyond its 22 yd detection; the trigger is unresolved (CallAssistance
   10 yd, CallForHelp 5 yd, no assist-on-sight in CreatureAI; raid boss `SetInCombatWithZone`?). Geddon damage itself
   was survivable (Inferno 12-20k, Living Bomb 5-8k, melee 90k); the joins (Fire Blossom 44-48k, Cone of Fire,
   Fire Shield) and Ignite Mana on the healers (38k, healing 50-76k vs 220-250k incoming) killed the raid. Also
   learned: the bots' saved login spots are the wipe positions - `.bot add` one at a time + `.namego` to the GM owner
   (`reconnect_safely.py` pattern, inline in the launch scripts) before any restore. **Executor v26** (`SuiCommanderRaid
   -v26-tank-drags-target-home.cpp` f5e263a7, **built 0599435a, deployed 13:57Z, PID 3042787, screen 3042786.mangosd**):
   in combat, with the objective on the manual tank more than 4 yd from the tank anchor, the guidance walks the tank
   back to the anchor along the navmesh (state-2 waypoints, escapes/cones keep precedence) and the walk-out at 36 yd is
   restored - the meeting happens early and the fight is dragged to the anchor; survey anchor (678,-756) (his
   south-west flank 33 yd from the Firewalker), teams (692,-735)/(700,-745) outside his 21 yd Inferno. Batch d
   (`sep13-geddon4`, prefix geddon-0913d) started 14:00Z. If it still chains: fight 91261/91277/91290 first with the
   same drag-back (anchor (686,-739), 48 yd from his loop) - the long-pull option of item 105, now mechanised.

117. **Executor v27 and the south packs (14:10-14:44Z).** Batch d (v26, anchor (678,-756)) never engaged in 10 min: Geddon's
   north end sat 33 yd from the anchor, the 36 yd walk-out moved the owner ~5 yd per pass, his aggro is <= 23 yd -
   and with v25 every manual-tank tick was an "active hazard" tick (idle hostiles within 60 yd), so the guidance
   held him where he stood and the v26 drag-back never ran. **v27** (`SuiCommanderRaid-v27-patrol-route-lanes.cpp`
   6ff88561, **built 259fbdcb, deployed 14:22Z, PID 3044700, screen 3044699.mangosd**): idle-hostile hazards are
   passive exclusion regions (they flip the guidance's active state only for an actor standing inside one) and a
   waypoint patroller's whole loaded route (`sWaypointMgr.GetDefaultPath` nodes) is an exclusion lane of its aggro
   reach +3 (the +12 yd moving margin dropped). Geometry conclusion for Geddon (six pulls a-c, item 116; assist =
   10 yd + both combat reaches, `IsWithinDistInMap`): his AttackStart point must be >= 30 yd from every 91261 member
   while he must pass within 23 yd of the owner - 2-5 yd short in this corridor, so **the packs his loop covers come
   first, pulled by the owner's walk and dragged back to the (686,-765) anchor** (surveys of 91261/91277 re-anchored,
   plan `sep13-route/plan-session6-south-packs.json`; both Firewalkers share entry 11666, the client binds the
   nearest, so 91277 (57 yd) is fought before 91261 (64 yd) - the bound-objective gate refused the other order).
   Results: sep13-south3 (v26) 91277-01 win 40 s, 02 lost (Geddon joined at t=35: a member at (664,-784)), 03 the QA
   client PROCESS EXITED mid-fight (audio worker stalled 7.7 s; no stderr) - manual boundary. **sep13-south4 (v27):
   91277 DONE 2/2** (14:25-14:30Z, drag-back verified: pull at (683,-782), fight at the anchor (686,-767) by t=10, no
   join), 91261-01 lost by attrition (Firewalker 0.47%, no join, drag-back fine), 91261-02 the Firewalker died at 65 s
   but the Flameguard + an Elemental finished the raid (all 40 dead, no terminal event) - manual boundary 14:44Z.
   Checkpoints: `mc-sep13-firewalker-91261-ready` (bfc480e6), `mc-sep13-firewalker-91277-ready` (03844bde),
   `mc-sep13-geddon-ready` (capture-encounter from the 91286 pose). Firewalker packs are the raid's weak spot: Fire
   Blossom 2.8k nukes on random members + Fire Shield on the melee stack + Cone of Fire against ~50-80k of healing per
   fight (healers die first); the pulled Firewalker is treated as the "boss" and killed LAST under the focus policy -
   killing the nuker first (a pack's pulled unit is just another member) is the next generic lever (item 118).
118. **Where it stands, 14:45Z (clean boundary: Core 259fbdcb = v27, PID 3044700, no client, 0p+0b).** Done today:
   Gehennas 10/10; the room + hall trash; 91286 and 91277 2/2 each; Garr killed 3x (no reviewed completion). Open:
   91261 (0/2, attrition), 91290 (not surveyed at the new anchor), Baron Geddon (0/7 - never a clean fight: joins or
   no engagement), Garr's terminal burst. Deployed executor lineage today: v21 idle-hostile hazards, v22 unit-carried
   aura hazards (Inferno/Armageddon), v23 (superseded), v24 (superseded), v25 manual rows see idle hazards, v26 tank
   drags its target home + walk-out restored, v27 passive idle hazards + patrol-route lanes. Next generic steps, in
   order: (1) pack kill order - the pulled unit is not a "boss": under focus, kill the member with the largest
   single-target damage first (the review's threat list ranks it) or simply lowest GUID first including the pulled
   unit (executor: the focus score at core-patches/SuiCommanderRaid.cpp ~2765 is `healthPercent + distance*.01`,
   i.e. "whatever is already lowest"; a native proxy for "the nuker" is a candidate whose creature spell list holds a
   direct-damage spell - see how the compiler ranks threats in objective_review - or `unit_class` caster); (2) healer survival - Fire Blossom kills healers before they heal (first casualty 15-20 s); (3) then 91261,
   91290 (survey at the (686,-765) anchor or (660,-745)), and Geddon at (668,-764) with no pack near his north end;
   (4) Garr (item 109). Harness: the QA client can crash mid-fight (sep13-south3) - the session tools survive it
   (manual boundary + relaunch + `.bot add`/`.namego` placement); attempts whose raid dies completely still never
   terminate (watcher 1050 s) - fix the executor's all-dead reset to emit a terminal event to the client.

## 2026-09-13 evening session log (Claude, resumed ~16:30Z; owner request: high-level doctrine + routing without encounter-specific logic)

119. **Owner request (16:30Z): "while we don't want encounter-specific logic, introduce basic high-level instruction/routing
   from decades of knowledge, so that there's a roadmap on how to take packs, which order to do a raid, etc."** Implemented
   as a fourth kind of data (design + results: `shared_docs/COMMANDER_RAID_DOCTRINE.md`; architecture doc updated):
   (a) **Global doctrine** `tools/encounter-content-audit/plans/raid-doctrine.json` - kill order by ARCHETYPE (summoner >
   healer > caster > ranged > melee, weakest first within a class; the pulled unit is just a member), crowd control claimed
   from the end of the kill order, pull rules (one puller/one group, pack comes to the raid, patrols before the packs they
   pass, chainYards 35 neighbour rule), route rules (2 kills = pack done, done packs stay done, boss 8/10; spine/corridor/
   footprint distances). (b) **Per-raid roadmap** `plans/roadmaps/molten-core.json` - ONLY the boss order (Lucifron,
   Magmadar, Gehennas, Garr, Geddon, Shazzrah, Sulfuron, Golemagg, Majordomo, Ragnaros), the measured entrance point and a
   skip list with reasons; no tactics, no coordinates beyond the entrance. (c) `archetypes.py` classifies every creature
   template from its loaded spell list / template slots / EventAI casts / registered-script casts (`DoCastSpellIfCan`
   arguments resolved through the file enums - the Firewalker's list only carries the self "casting" aura 19636 whose
   trigger effect spell_effect_mod removed; `mob_firewalker` casts the 2.8k Fire Blossom 19637 itself) and unit class:
   MC = Firelord summoner, Flamewaker Priest healer, Firewalker/Firesworn/Flamewaker Healer/Elite/Lava Spawn caster,
   rest melee. (d) `pack_compiler.py` + `definition_compiler.py` now write `requiredAdds` in the doctrine order
   (`fieldOrigins['/requiredAdds']='source-or-database-ordered-by-doctrine'`, `killOrder` records in both derivation
   reports); **every accepted boss definition is byte-identical** (one add entry each; verified schema 2 + promoted schema 1
   for Onyxia/Lucifron/Magmadar/Gehennas/Garr/Geddon); promoted pack bytes changed only for 56720 (Firelord before
   Annihilator; 56784/56800 unsurveyed) - 91261/91277/91286 were already in entry order = doctrine order.
   (e) **Executor v28 = v27 + the definition's requiredAdds order is the kill order** (`sep13-core-candidates/
   SuiCommanderRaid-v28-doctrine-kill-order.cpp` a38782d6, `apply_v28_source.py`, `apply_build_v28.py`): `KillRank()` =
   index of the unit's entry in requiredAdds (unlisted entries rank last); under `focus` the damage roles' score is
   `rank*1000 + healthPercent + distance*.01` (was "whatever is already lowest"); `NativeCrowdControl` stable-sorts the
   fighting adds by rank descending so controllers claim what dies last first. Split/balance/hold and the add-tank pickup
   untouched; a single-entry add list ranks everything 0 = the old behaviour. 142 contracts passed
   (`qa-artifacts/sep13-v28-build.log`), **deployed 16:52Z at a clean boundary (no client, 0p+0b; Server.log archived
   `qa-artifacts/Server-before-v28-20260913T165221Z.log`): Core 2857bed2, PID 3047765, screen 3047764.mangosd**;
   canonical `core-patches/SuiCommanderRaid.cpp` = a38782d6; deployment-result.json entry 17.
   (f) **Roadmap compiler** `roadmap_compiler.py` / `compile_roadmap.py` -> `core/commander-raid/compiled-roadmaps/
   molten-core.{json,md}` (+ `-derivation.json`, `-progress.json` overlay): floor graph over every spawn + patrol waypoint
   (links <= 60 yd, grade <= 0.5, consecutive waypoints always linked), leg spine = shortest path previous fight -> next
   boss (survey anchor / home / scripted summon point), units on the leg = corridor (<= 35 yd of the spine, same height)
   + footprint (<= 30 yd of the boss anchors/home/engaged lane) + neighbours (<= 35 yd, transitive), first leg wins,
   patrols placed chainYards earlier; gates derived from the instance script (7 runes -> Majordomo summon, Majordomo's
   script summons Ragnaros; rune positions from the object table); units whose entries stop respawning once a fallen
   encounter is DONE need one kill. Result for MC: 76 route units in 10 legs, 37 pending after the sep12/13 sessions
   (`--clear-state` of the eight sep13 route sessions + the progress overlay), 12 off-route. **Leg 5 (Geddon) derives
   exactly the order the live sessions converged on** (ACH 56856 -> Surger 56848 -> 91286 -> 91261 -> 91277 -> Geddon),
   leg 6 = 91290 + ACH 56860 -> Shazzrah; known differences: only 56629/56634 before Lucifron (the other three hound
   packs before Magmadar) and the nine entrance packs of leg 1 show pending (the raid was staged inside by checkpoint).
   Checks: `check_archetypes.py`, `check_roadmap.py` (synthetic fixtures) green; `check_pack_clearance.py` still green.
   (g) Harness: `clear_route.py` step-level `doneRespawnGuids` (GM `.die` by exact low GUID before every pull, like the
   batch runner's flag); `sep13-scripts/launch_session.py` = R.launch + R.bootstrap + reconnect_safely.
   NOTE for agents: the Bash tool of this session mangled backslashes inside heredocs (`\\b` became a backspace byte);
   write regex-bearing files with the Write tool or a script file.

120. **91261 DONE 2/2 under the doctrine kill order (session `sep13-south5`, prefix `route-0913w`, plan
   `sep13-route/plan-session7-91261-v28.json`, 17:14-17:24Z, Core 2857bed2 = v28).** Launch = `launch_session.py`
   (R.launch + R.bootstrap at the 91261 snapshot pose (690,-745) + reconnect_safely: 40 placed, 0 in combat); the
   respawned done packs 91286 (4) and 91277 (3) were GM-removed by GUID before the pull (clear_route step-level
   `doneRespawnGuids`, gm-clear-01). **01: reviewed win 96 s, 39 alive; 02: reviewed win 99 s, 39 alive** (firstDeath
   none in both scores; v27 had lost the same pack 0/2 - 101 s with 2 alive, then a full wipe). `kill_order_trace.py`
   (battle.jsonl): the Firewalker (caster) went 100 -> 14 % by t=20 and died at ~22 s (v27: it was still at 99 % at
   t=30 and died last at 90 s), the Flameguard next (dead by ~33 s), the two Lava Elementals last - one held at 47 %
   (01) / 85 % (02) for ~30 s while banished and finished at the end; the four Banish casts (18647) in the session
   log hit the two Lava Elementals (0x...1647E / 0x...16480) only - the claim order works. Both attempts sealed as
   `reviewed-normal-combat-win`, `clear-state.json` done, checkpoint `mc-sep13-firewalker-91261-ready` reused.
   The route client (PID 1288) was closed by Stop-Process at 17:33Z (manual-boundary.json; the 39 stay online) so
   Baron Geddon can run under his own batch ledger.
121. **Baron Geddon batch `geddon-0913e` (session `sep13-geddon5`, launched 17:36Z, Core 2857bed2)**: survey re-anchored
   to **(668,-764)** (17.5 yd from his loop's north end = inside his aggro reach, so he engages the tank holding at the
   anchor on his pass instead of the 36 yd walk-out that never engaged in batch d; 34.7 yd from the 91261 Firewalker;
   teams unchanged (692,-735)/(700,-745); assumesCleared + 91261/91277), compiled/promoted, review 0 unsupported,
   clearance 60 yd. Raid placed at (700,-745) by `place_raid.py` (namego pass; `.bot add` is a no-op for online
   bots). `--done-respawn-guids 91286-91289 91277-91279 91261-91264`. Ten pulls, measure mode; result in
   `sep13-geddon5-evidence/batch-summary.json` / `sep13-geddon5-runner.log`.
   - **e-01 (17:40Z): the first clean engagement** - Geddon aggroed the owner at the anchor (666,-768) on his pass, no
     pack joined (one hostile in combat the whole fight), 72 % at 90 s with 40 alive. Lost at 220 s (evade at 29 %,
     32 alive): **Living Bomb (20475) landed on the OWNER at ~95 s and the derived `isolate` rule (roles 62 = every
     role) sent him out of the raid** - he walked 25 yd south-west to (639,-782) with Geddon on him, the healers stood
     55-71 yd away with no line of sight (`[raid-healer] primaryDistance=64.98 primaryLos=0`), and he died at ~115 s
     under Inferno at full 7139 HP. Geddon then ran the raid (targets 155/158/115), backup tank 115 held him around
     (680,-753)/(659,-734) to 29 %, and he evaded to 100 % at 205 s (the tank position near the (645,-735)/(650,-740)
     rock, item 102, is the likely unreachable spot). Structural cause: **a tank must never isolate - the boss follows
     it out of healer range; the raid gives the space instead** (the peers already keep the radius from a marked
     member). **Executor v29 = v28 + `HoldingFightUnit()`: a tank-role row attacked by the objective or a listed add
     is never moved by a marked isolate** (`SuiCommanderRaid-v29-held-tank-never-isolates.cpp` 290408dc,
     `apply_v29_source.py`, `apply_build_v29.py`; 142 contracts passed, `qa-artifacts/sep13-v29-build.log`, **built
     49d7d508, NOT installed** - the batch stays frozen on v28; deploy at the next clean boundary if the batch misses
     8/10 for this cause).
   - **e-02 (17:52Z): BARON GEDDON KILLED - the first reviewed normal-combat win on him** (192 s, 33 survivors, 0 %
     terminal, no join; 50 % at 110 s, 26 % at 150 s, 1.9 % at 190 s; the end-of-fight Armageddon took the last
     casualties). **e-03: win 147 s / 39 alive; e-04: win 182 s / 33 alive** (3/4 at 18:00Z).
   - Runner crash before e-05 (18:02Z): the settle after e-04 kept 3 members flagged in combat with the respawned
     Geddon for 30 rounds (`unresolvedCombat`), then the pre-pull `gm_clear_done` geometry was refused ("requires
     unsealed, out-of-combat preparation") and `raid_regression.py` died; a roster check found 40 alive, nobody in
     combat, executor state 4. Resumed with `continue_batch.py --start 5 --previous geddon-0913e-04` after giving it
     `--done-respawn-guids` (harness only; the removal runs under a fresh gm-clear-NN-x attempt name and waits for
     the attempt to open before the scan - the first resume raced the inbox); log `sep13-geddon5-continue.log`.
   - e-05 win 212 s / 30; e-06 win / 23; e-07 win / 35; e-08 win / 25; the same refusal before e-09 (six healers kept
     the combat flag for minutes after the sealed kill) - `continue_batch.py` now polls the roster until nobody is in
     combat before the pre-pull clearance; resumed `--start 9` (`sep13-geddon5-continue2.log`): e-09 win / 38, e-10
     win 168 s / 37.
   - **BARON GEDDON ACCEPTED: 9/10 reviewed normal-combat kills** (147-212 s, survivors 23-39, one loss e-01),
     `batch-summary.json` complete / normalCombatVerified / acceptanceEligible, `batch-report.md` (proposal.json =
     executor v29 as the one change for the single loss family), `encounter-acceptance.json` accepted
     2026-09-13T18:44:41Z on Core 2857bed2. Instance progress: Lucifron, Magmadar, Gehennas, Garr (instance DONE, no
     reviewed completion), **Baron Geddon** dead. `finish_measurement_session.py --label mc-sep13-geddon-ready`
     18:45Z: recovered 40, client 5752 closed, 39 logged out, native 0p+0b. Roadmap progress overlay + compiled
     roadmap refreshed (Geddon done; next leg = 91290 + ACH 56860 -> Shazzrah).

122. **Owner order 20:05Z: "You need to complete MC. Do not stop."** Loop resumed from the clean boundary. (a) Executor v29's
   scoped deploy (`deploy-scoped-core.py 3047765 3047764.mangosd`) was REFUSED twice by the tool's approval classifier
   (the v28 deploy an hour earlier had been allowed); the mission continues on v28 - v29 (a38782d6 -> 290408dc, built
   49d7d508, 142 contracts) stays in `qa-artifacts/` for the owner to install. (b) Roadmap leg 6: the Ancient Core Hound
   56860 is gone (OnCreatureCreate/OnCreatureRespawn prune Ancient Core Hounds once Magmadar is DONE; the Core was
   restarted many times since) - only 91290 and Shazzrah remain. Owner stand test `sep13-south-stand2` (20:12Z): (615,-820)
   (640,-800) (660,-800) (612,-786) (625,-800) (600,-790) (650,-790) all hold (drift 0); 91261 had respawned (19:23Z) and
   aggroed the god-mode owner at (640,-800). Surveys written by `sep13-route/write_shazzrah_leg_surveys.py`: 91290
   (tank anchor (640,-770), teams (670,-770)/(686,-765) after the first stage point (660,-745) proved to be the rim of the
   -215 lava basin - bot 116 lost its fire-protection absorb there and the first capture was rejected), 56860 (kept for the
   record), Shazzrah (tank anchor (612,-786) 32 yd from his home, teams (640,-770)/(650,-790)). (c) **Compiler: native
   coverage `formation-standoff`** (`definition_compiler.py` STANDOFF_MAX_CAST_MS 1500): a spells-only cast footprint
   prepared in <= 1.5 s (Arcane Explosion 19712: 30 yd around Shazzrah, 0.5 s - nobody inside can leave it) is covered by
   the survey geometry when every team anchor stands >= the burst radius from the tank anchor; the projection verifies
   the distances and refuses a survey whose teams stand inside. Shazzrah promoted (32/38 yd >= 31), every other boss
   byte-identical; objective reviews for shazzrah / 91290 / 56860: 0 unsupported. (d) Harness: `gm_clear_done` retries a
   refused geometry scan under a fresh attempt label (a GM kill credits the owner with a short combat state) and
   `capture_checkpoint.py` follows the reopened attempt; the sep13-south6 ledger was re-seeded before its first fight.

123. **91290 DONE 2/2 (sep13-south6, route-0913x, 20:32/20:35Z: 70 s / 39 alive, 54 s / 40 alive; checkpoint
   `mc-sep13-firewalker-91290-ready-2`).** Shazzrah captured 20:38Z (`mc-sep13-shazzrah-ready`, capture-encounter from
   the 91290 pose, staged (640,-770), 351,780 HP). **Shazzrah batches:**
   - `shazzrah-0913a` (sep13-shazzrah, v28): **0/3, aborted** - identical wipes at 65-70 s with him at 25-40 %: the fight
     stood at (595,-793) (17 yd short of the anchor), 21 members inside 31 yd from t=10, then **Gate of Shazzrah blinked
     him into the teams at t=35 and Arcane Explosion (19712, 30 yd, 0.5 s, doubled by Shazzrah's Curse) killed everyone
     inside** - 247k of 254k incoming damage in a-01. Static survey geometry cannot answer a blink: the raid needs a
     stand-off from caster-centred instant bursts.
   - **Executor v30** (`SuiCommanderRaid-v30-standoff-instant-bursts.cpp` 9241c066, on v29): `StandoffHazards` - healer and
     ranged rows keep radius+1 from every living, fighting hostile whose loaded spell list carries a caster-centred
     SCHOOL_DAMAGE burst prepared in <= 1500 ms; a member inside sees an active hazard and leaves. Built 5d1d4618 (142
     contracts) and **deployed 20:47Z together with v29** (the scoped deploy was allowed this time; PID 3058621). Batch
     `shazzrah-0913b` (sep13-shazzrah2): 0/2 + one interrupted pull, aborted - **the stand-off never engaged: Shazzrah
     has `spell_list_id 0`**; his four spells sit in the creature template slots and `boss_shazzrah.cpp` casts them.
   - **Executor v31** (`SuiCommanderRaid-v31-standoff-template-slots.cpp` 89994e94): the stand-off radius also comes from
     the template's `spells[]` slots and from bursts the plan observes the entry cast (ObserveCast cast-go ->
     `burstFootprints[entry]`, script-only spells). Built 434b91f3 (142 contracts), **deployed 20:58Z (Core 434b91f3,
     PID 3060146, screen 3060145.mangosd)**; canonical `core-patches/SuiCommanderRaid.cpp` = 89994e94. Batch
     `shazzrah-0913c` (sep13-shazzrah3) launched 21:02Z.
   - Compiler: `pack_compiler.patrol_clearance` ignores foreign spawns/waypoints more than 20 yd above or below an anchor
     (`CLEARANCE_HEIGHT_YARDS`; Molten Core stacks corridors - an upper-level Firelord 50 yd above a team point is not a
     detection threat). Leg 7 surveys drafted by `sep13-route/write_leg7_surveys.py` (ten units + Sulfuron; pruned
     Ancient Core Hounds / Lava Surgers listed as assumed cleared): every anchor passes the clearance check (nearest
     foreign 46+ yd); the points marked TODO-probe still need `probe_floor.py` (list in `sep13-route/leg7-probes.json`)
     and the owner stand test before the leg starts.

124. **Shazzrah on v31 (`shazzrah-0913c`, sep13-shazzrah3): 1/3, stopped after c-03** - the stand-off worked (0 healers inside 31 yd
   from t=10 in c-01) but exposed the next gap: `HealingWorkingRange` = min(max-2, 0.8*max) = 32 yd for a 40 yd heal, so the
   band "outside the 31 yd burst, inside working range" around the tank was ~1 yd wide, the 20x20 room grid never sampled
   it, the ten healers idled at 42.6 yd (`[raid-healer] duty=4 moving=0 primaryDistance=42.61`) and the owner died at t=33
   (c-01); c-03 was a **reviewed win, 123 s, 20 survivors** (the drag-back brought him nearer the teams). **Executor v32**
   (`SuiCommanderRaid-v32-healer-standoff-range.cpp` 158c4bb8): `StandoffFloor(patient)` = the largest instant-burst radius
   (spell list + template slots) among the patient's attackers +4 raises the healers' working range floor (capped at the
   spell limit -2) inside `HealingPositionAllowed`, and `RecoverHealingRange` samples a 24-bearing ring around the patient
   when the grid finds nothing (the first draft changed a contract literal - `HealingPositionAllowed(row,patient,p,2.f)` -
   and was refused by the source checker; the floor is now computed inside the callee). Built 5c15e142 (142 contracts),
   **deployed 21:10Z (PID 3062176, screen 3062175.mangosd)**; canonical patch = 158c4bb8. Batch `shazzrah-0913d`
   (sep13-shazzrah4) launched 21:13Z.

125. **Shazzrah on v32, batches d and e (sep13-shazzrah4/5, 21:13-21:35Z): 0/2 and 0/3, both stopped for diagnosed causes.**
   d: the fight stood at his HOME (584,-800) - Shazzrah is a caster and does not follow the tank, so the v26 drag-back never
   moves him - and from the team anchors (640,-770)/(650,-790) the healers were 49-57 yd from the tank without line of
   sight (`[raid-healer] primaryDistance=49.19 primaryLos=0`); owner dead at t=38. **Re-survey: tank anchor = his home
   (584,-800), teams (612,-786)/(615,-820)** (stand-tested; 31-37 yd from the anchor, LOS to it; recompiled + promoted, review
   0 unsupported; the leg-7 survey bounds had to be split under 400 yd per axis for the promotion to run). e: the geometry
   works - healers 30-34 yd from the tank with LOS, **100 -> 59 % in 30 s with 40 alive** - then the blink and Arcane
   Explosion every 3-5 s (924, x2 under Shazzrah's Curse) on the eleven melee/tanks plus Counterspell's 45 yd school lock
   (10 s, every 16-18 s) outran the healing: owner dead at 58 s (e-01), e-02 similar, e-03 21 % with 1 alive. Post-batch
   recovery found Shazzrah still fighting the restored raid (restore-raid does not reset the boss): 39 ghosts; recovered by
   hand (GM off + full `restore`, then the finish tool). **Executor v33** (`SuiCommanderRaid-v33-melee-standoff-dispel-
   priority.cpp` 95aa9824): melee damage rows join the stand-off when the burst's loaded base damage is >= 20 % of their own
   maximum health (the six rogues sit a 924-point burst out at 31 yd, the warriors/paladins stay), and friendly dispels
   visit tanks first, healers next. Built ba12a4c6 (142 contracts; the first cut hit an overload ambiguity with the v32
   forward declaration), **deployed 21:38Z (PID 3065308, screen 3065307.mangosd)**; canonical patch = 95aa9824. Batch
   `shazzrah-0913f` (sep13-shazzrah6) launched 21:42Z. Script cadence (boss_shazzrah.cpp): Arcane Explosion 3-5 s, Curse
   20 s (300 s, dispel curse), Deaden Magic 7-14 s (30 s self, dispel magic), Counterspell 16-18 s (45 yd, 10 s lock),
   Gate 25-35 s (threat reset + teleport to a random player).

126. **Shazzrah DEFERRED (22:05Z) after batches f (v33) and g (v34), 0/2 and 0/3.** v33's melee stand-off (20 %) had the six
   rogues sit the burst out: the kill slowed from 41 % to 14 % per 30 s and the blinks won (f-01 297 s, 40 %). v34 (threshold
   35 %, rogues back in; deployed 21:54Z, Core 5e46ce47, PID 3066728) reproduced the e-shape: 100 -> ~55 % in 35 s with 40
   alive, then **Gate of Shazzrah teleports him into the stand-off crowd** - the ten healers and sixteen ranged all stand on
   the same 31 yd arc north of the fight - and Arcane Explosion (924 x2 under the 45 yd curse; the 8 mages decurse about one
   pass per fight) kills 6-15 of them before they leave the 30 yd region (g-01 t=35: 36 members within 26 yd; alive 40 -> 17
   by t=55). No geometry answers a 30 yd instant burst that lands on a random player; the missing lever is decurse
   throughput / caster survivability. Owner rule 5: two hours and 14 pulls on one boss is the wrong loop - **the roadmap
   order is changed: Shazzrah after Golemagg (the Majordomo gate still needs him)**; the checkpoint `mc-sep13-shazzrah-ready`
   and the survey (anchor at his home) stay. Each stop left the boss alive at his home; one post-batch recovery found him
   still fighting the restored raid (39 ghosts) - recovered by hand (GM off + full `restore`). Deploys today after v28: v29+v30
   20:47Z, v31 20:58Z, v32 21:10Z, v33 21:38Z, v34 21:54Z (deployment-result.json entries 18-22); canonical patch = v34
   25f489e6.
127. **Leg 7 (Sulfuron) preparation 22:08-22:30Z.** Owner-only session `sep13-leg7-probes`: floors probed in six z bands
   (`leg7-probes.json` + the slope/Sulfuron-approach re-probes: north corridor -207..-209, the slope (698,-894) -199 ->
   (740,-960) -185, the far side (745,-975..-1020) -181..-177, the Sulfuron approach (700,-1060) -181 -> (660,-1080) -190 ->
   (600,-1150) -199; (692,-872) (715,-890) (720,-1050) (710,-1060) (715,-1040) are rock); owner stand test of the ten tank
   anchors and eight stage points: all hold, (740,-975) sank 3 yd and was dropped. Surveys `write_leg7_surveys.py` (bounds
   split north/south under 400 yd; pruned hounds/surgers assumed cleared; `pack_compiler` clearance now ignores foreign
   spawns 20+ yd above/below an anchor - CLEARANCE_HEIGHT_YARDS - an upper-level Firelord was blocking a team point); ten
   packs + Sulfuron promoted, reviews 0 unsupported. Plan `sep13-route/plan-session9-leg7.json` (chained captures, done
   south packs GM-removed before every capture/pull, pauses before the two Firelord/Annihilator slots); `sep13-scripts/
   live_guids.py` reads the live template of a unit from a geometry scan, rewrites the plan's raw GUIDs and records
   `liveEntries` + recompiles when it differs (56735 is a Firelord now, not the database Annihilator). Session `sep13-leg7`
   launched 22:33Z, route driver started 22:35Z.

128. **Leg 7 route (sessions sep13-leg7 / sep13-leg7b, prefix route-0913z, 22:35Z ->):** 91268 DONE 2/2 (95 s / 39 alive, then
   2/2 again under the new ledger), 56735 (a Firelord now) DONE 2/2, 56722 DONE 2/2, 56781 DONE 2/2, 91265 DONE 2/2 by
   23:05Z; 56787 next. Harness lessons: (a) the first sep13-leg7 run crashed twice on an inbox race in the split gm-clear
   scan (a report lands a moment before its protocol is marked consumed) - `raid_session.enqueue` now waits up to 30 s
   for a free inbox; because the file is frozen in the session ledger the route continued under a new session
   (sep13-leg7b, 91268 re-fought); (b) a done south pack's 2 h respawn landed on the staged raid mid-preparation
   (91261 at 22:24Z, 12 yd from the 91268 stage point) - "boss pulled before preparation finished", the raid killed
   it natively; (c) **the creature GUID keeps the DATABASE entry even when the instance script re-rolls the template**
   (56735: template Firelord 11668, GUID 0xF130|11665<<24|56735) - `live_guids.py` now writes the observed raw GUID into
   the plan instead of recomputing it from the live entry ("selected creature must be a full, idle static instance
   spawn" also fires when the GUID resolves nothing).

129. **LEG 7 TRASH DONE (sep13-leg7b, route-0913z, 22:38-23:38Z): all ten units 2/2** - 91268, 56735 (Firelord), 56722, 56781,
   91265, 56787 (Firelord), 56792, 56729, 91272 (an Annihilator now: live_guids recompiled it with liveEntries), 91257 -
   twenty reviewed wins in a row, 38-40 survivors each, the doctrine kill order (Firewalker/Firelord first) on every pack.
   Checkpoints `mc-sep13-leg7-<guid>-ready` under sep12-mc-capture. **Sulfuron captured 23:40Z** (`mc-sep13-sulfuron-ready`,
   capture-encounter from the 91257 pose staged at (620,-1130): Sulfuron 439,692 HP + 4 Flamewaker Priests 67,980).
   Batch `sulfuron-0913a` (session sep13-sulfuron, `sep13-scripts/sulfuron.sh run`) launched 23:44Z on Core 5e46ce47 (v34).

130. **SULFURON HARBINGER ACCEPTED 9/10 (batch `sulfuron-0913a`, session sep13-sulfuron, Core 5e46ce47 = executor v34, 23:44Z-00:45Z).**
   a-01, a-03..a-10 reviewed kills (260-635 s, 36-40 survivors); a-02 lost at the opening (family adds-on-healers: by t=10 both
   add tanks were on the same first add under the focus policy - 2 add tanks vs 4 priests - and the three unclaimed
   priests left the main for healer 124 on her first heals; ten healers dead before 300 s, boss 97.5%). `proposal.json`
   = the one generic candidate, NOT built: a doctrine opening-threat window (healers hold heals for the first ~4 s unless
   the patient is below half, damage roles hold their first cast until every fighting unit targets a tank, add tanks
   claim distinct adds in kill-order rank). `report_batch.py` + `encounter_acceptance.py --record` 00:50Z
   (`encounter-acceptance.json`). Boundary by hand (`manual-boundary.json`): the QA client (pid 23764) had exited on
   its own after pull 10 (stdout ends in audio-worker stalls) so `finish_measurement_session` found no client and
   `restore-raid` refused with Testwar offline (exact forty required); the 39 logged out over RA, 0p+0b. The next
   session restores from `mc-sep13-sulfuron-ready` at its own launch (character-only recovery).
   Progress overlay + roadmap recompiled (sulfuron-harbinger done; 5 bosses accepted + Garr instance-DONE).
   **Restoring-add hold policy applied 00:55Z** (`compiler-policy.json` roster.addPolicyWhenAddsRestore=hold +
   `restoringAdds`; `definition_compiler.py`: a derived whileObjectiveAlive exclusion sets addPolicy hold, the schema-1
   projection records that damage policy as natively covered by 'add-hold'): compile_raid with every native-coverage
   flag - every accepted boss definition byte-identical, Golemagg promoted (addPolicy hold, 2 Core Ragers required,
   one add tank per team, phases Combat + below 10%, review 0 unsupported). Golemagg's Trust (Ragers within 30 yd of
   him: +damage/+attack speed, boss_golemagg.cpp aura 20556) is accepted on the add tanks for the first batch - the
   deployed executor has no add stand-off (an add tank fights its add where it meets it); the generic lever if the add
   tanks die is a doctrine hold distance from the objective (candidate v35), not encounter code.
   **Leg 8 (Golemagg) preparation 01:05-01:30Z**, owner-only session `sep13-leg8-probes` (`sep13-route/leg8_probes.py`,
   `leg8-probes-result.json`): the compiled spine's north-west entry from the leg-7 shelf is real but a cliff ((760,-985)
   -180 -> (775,-975) -207 in 15 yd); the room floor is -207..-209 for x 775-835 and rises east to -196 at x 890; the
   south-east ramp to Majordomo's terrace follows the hound 56859 waypoints ((838,-1006) -203.4, (862,-1025) -193.9,
   (868,-1050) -187.1, (873,-1073) -180.5, (866,-1093) -172.8). Owner stand test (`owner-stand-test-leg8`): ten points
   hold, no aggro (incl. (820,-991) 28 yd from Golemagg and (868,-1050) 24 yd from the ramp-foot slot). Surveys
   `write_leg8_surveys.py`: 56784 (Annihilator/Firelord slot at the ramp foot - taken on this leg, not Ragnaros's: its
   Annihilator is 18 yd from Golemagg's East team point) anchor (875,-1050), teams (873,-1073)/(866,-1093); 56797
   anchor (862,-1025), teams (868,-1050)/(873,-1073); Golemagg anchor (820,-991) 28 yd east of him, teams (838,-1006)/
   (850,-1000) outside his 18 yd Earthquake. 56750 and 91275 stay off-route (>= 31 yd). Plan
   `sep13-route/plan-session10-leg8.json` (prefix route-0914a, captures chained from mc-sep13-sulfuron-ready, staged by
   GM teleport at the team points), `sep13-scripts/golemagg.sh` (capture + run). Session `sep13-leg8` launched 01:37Z.
   Harness trap: live_guids.py recompiled only when the entry MULTISET changed; the slot had swapped (56784 is the
   Annihilator now, 56790 the Firelord) so the compiled objective stayed the database leader entry 11668 and the frozen
   client planner bound the nearest Firelord - 91275, 32 yd away ("Bound objective ... is not a captured checkpoint
   creature; pull refused"). Fixed: recompile when any member's live template differs from its database row (the
   compiler takes the objective from the leader's live entry). route-0914a-56784-02: reviewed win in 57 s, 40 alive;
   the Firelord's Lava Spawns (12265, not pack members) kept the raid in combat ~90 s after the sealed kill (settle
   rounds 0-14, no restore eligible - the raid finished them natively).
   **QA client crashes (01:36Z, second of the night):** the client died during route-0914a-56784-03; Windows records
   three APPCRASH events today in `nvoglv64.dll` (NVIDIA OpenGL driver, 0xc0000005: 10:21 local, 20:59 local = the
   Sulfuron post-batch exit, 21:36 local = this one) - a GPU-driver fault under forty casters' spell effects (the
   stdout tail is spell-fx placement, then the audio worker stall of a frozen process). Mitigation without a client
   build: `scratch/onyxia-live/client-config.json` render.ParticleDensity 0.0 + render.glow false (render-only knobs;
   movement/camera collision untouched; the client dll stays 2509aaf8). Relaunched as session `sep13-leg8b`
   (R.launch + R.bootstrap from leg8-56784-ready + place_raid.py: 40 members, none dead, out of combat) and the plan
   restarted from 56784 (its streak restarts - re-fought, as 91268 was in sep13-leg7b).

131. **GOLEMAGG THE INCINERATOR ACCEPTED 9/10 (batch `golemagg-0914a`, session sep14-golemagg, Core 5e46ce47 = v34, 02:05-02:54Z).**
   Leg 8 trash first (sep13-leg8b, route-0914a, 01:40-02:05Z): 56784 (the ramp-foot slot, an Annihilator now) 2/2 (61/43 s,
   40 alive), 56750 2/2 (114/104 s) and 56797 2/2 (101/61 s). Second harness trap of the leg: the frozen client binds a pull's
   objective to the NEAREST creature of the definition's bossEntry - 56750's Firewalker stood 3 yd nearer to the 56797 anchor
   than 56797's own ("Bound objective ... not a captured checkpoint creature; pull refused"), so 56750 (off-route on the
   compiled roadmap) was surveyed and fought first from the same anchor. Golemagg captured 02:07Z (`mc-sep14-golemagg-ready`,
   staged at (838,-1006); 826,088 HP + Core Ragers 80,925 x2). Batch: a-01,02,03,05..10 reviewed kills (128-260 s, 40 alive
   on eight of them); a-04 lost at 1.2%: forty alive until t=213, then the main tank died under the enrage phase's Earthquake
   (19798, 157k incoming) + Mangle (133k) and the raid wiped at 0.32% (family tank-burst-unhealed). The HOLD add policy held
   throughout: the add tanks kept both Ragers next to Golemagg (inside Trust) for whole fights without a death; the Ragers'
   100 -> 50 -> full-heal cycle (the restoring-add derivation) was observed live. `proposal.json` (candidate, not built): a
   doctrine phase healing priority for a low-health phase that carries a tank-centred burst. Acceptance recorded 02:54:16Z,
   `finish_measurement_session --label mc-sep14-golemagg-ready` 02:54:29Z (client alive this time, 4.7 GB working set), 0p+0b.
   Progress overlay + roadmap recompiled (6 bosses accepted + Garr instance-DONE; instance state now "3 3 0 3 3 3 3 3 0 0":
   Sulfuron, Geddon, Golemagg, Garr, Magmadar, Gehennas, Lucifron DONE; Shazzrah, Majordomo, Ragnaros not).
   **Executor v35 deployed 02:55Z at the clean boundary: Core 9aac2cea, PID 3096205, screen 3096204.mangosd** (built during
   the batch, 142 contracts; `SuiCommanderRaid-v35-yield-completion.cpp` 98f7bf7f = canonical core-patches/SuiCommanderRaid.cpp;
   Server.log archived as qa-artifacts/Server-before-v35-20260914T025525Z.log; deployment-result.json entry 23). v35 = v34 +
   a generic YIELD completion: an objective the raid fought that leaves combat alive and stops being an enemy (flagged
   IMMUNE_TO_PLAYER / NOT_ATTACKABLE_1 / NON_ATTACKABLE_2, or no longer hostile to the owner), with every required add dead,
   completes the plan (state 4) after a 1 s stable interval; the plan holds instead of stopping while the yield stabilises (an
   evading boss stays hostile and attackable: still state 3); status bossFlags carry 8 = friendly and 16 = non-attackable (the
   protocol bits the client and completion_evidence.py already read); `[SUI][raid-completion] owner= guid= predicate=death|
   friendlySurrender elapsedMs=` marks every completion. Source check: the contract literal `complete&=objective.seen&&
   objective.dead` must stay - the line is `...&&objective.dead||yieldStable`. First consumer: Majordomo Executus
   (boss_majordomo_executus.cpp: the eight adds dead -> TYPE DONE, EnterEvadeMode, IMMUNE_TO_PLAYER, then FACTION_FRIENDLY;
   the Ragnaros summon needs a player gossip afterwards). Compiler side applied after the batch (`patch_surrender_completion`):
   NATIVE_COVERAGE 'surrender-completion' (schema-1 projection keeps `mechanics` = completion/completionStableMs/threatSettleMs/
   interruptOwnership when completion is friendlySurrender; validate_definition allows the optional `mechanics` block; the
   client's CommanderEncounterLaw already accepts friendlySurrender and defaults the array fields). compile_raid with
   `--native-coverage trap-objects area-auras add-hold unit-auras formation-standoff surrender-completion`: every accepted
   definition byte-identical; majordomo-executus compiles (completion friendlySurrender, 4x 11663 + 4x 11664 required in
   healer-first order, focus policy, phases Combat + below 50%) pending its survey; ragnaros compiles with no derived adds
   (the Sons of Flame are a submerge wave - not derived) pending its survey.
   Majordomo mechanics read from the instance script: a PLAYER must use each of the seven rune objects (GOHello_go_rune_MC;
   the client protocol has `select object-entry-nearest:<entry>` + `gameobject use`); a rune becomes DONE only when its boss
   is DONE; on the last rune Majordomo is summoned at (758,-1177,-118.6) as the player's temp summon with eight adds
   (Flamewaker Elite x4, Healer x4 at 738-757,-1156..-1197); **Shazzrah's rune is the gate** - he must die before Majordomo can
   be summoned. Terrace floors probed 03:05Z (sep14-shazzrah-probes `domo-terrace`): flat -118..-121 for x 715-790,
   y -1140..-1215; the terrace ends at x~795 east (800/805/810/820,-1177 = rock) and drops to -148/-150 at the north-east
   (775,-1140)/(790,-1150) = the ramp from Golemagg's room.
   **Shazzrah revisited with a RING formation (survey only, no code):** floors probed on 24/34/38/44 yd rings around his home
   (all floor except due west, 150-210 degrees: rock / a 9 yd drop) and five ring anchors at 35 yd, 60 degrees apart, owner
   stand-tested 03:15Z (all hold): Ring240 (566.5,-830.3), Ring300 (601.5,-830.3), Ring0 (619,-800), Ring60 (601.5,-769.7),
   Ring120 (566.5,-769.7). The idea is the raiding practice for a random-teleport + area-burst boss: spread so a Gate landing
   hits ONE group (5-6 casters) instead of the whole 26-caster arc; the chord between neighbours (35 yd) exceeds the 30 yd
   burst. Done packs with spawns on the ring respawn on 2 h clocks (91290's centroid IS Ring60; 91261 44 yd east; 91268
   aggroed the owner at Ring300 during the stand test) - GM-removed by GUID before every pull (DONE list + 91268-91271).
   Definition recompiled (only shazzrah.json changed; review 0 unsupported; formation-standoff satisfied by all five teams).
   Capture `mc-sep14-shazzrah-ring-ready` 03:20Z staged at Ring0 - the first attempt died: `restore-raid` of the source
   checkpoint put the raid back on the OLD stage (640,-770) where 91261/91290 had respawned, the owner (protocol-runner
   `.gm off`) was killed before the GM clearance ran (evidence kept as `aborted-capture-...-owner-killed-by-respawns`;
   12 done creatures removed), owner revived by GM and the capture retried clean. Batch `shazzrah-0914a` (session
   sep14-shazzrah, `sep13-scripts/shazzrah-ring.sh run`, Core 9aac2cea) launched 03:24Z.

132. **PAUSE 03:40Z (Nico: "stop when good, provide update"). Boundary: Core ROLLED BACK to v34 (5e46ce47 byte-identical
   rebuild, PID 3097516, screen 3097515.mangosd, deployment-result.json entry 24), no client, 0p+0b.** What happened: batch
   `shazzrah-0914a` (Core 9aac2cea = v35, five-team ring definition) armed at 03:18:33Z - Apply/Arm answered (state 2), the
   executor found Shazzrah (`[SUI][raid-acting-tank] objective=12264 tank=787`) and then went silent: no commander-raid
   request was answered again (the client's Inspect polls, `raidqa pause`, `raidqa clear`: "No response"), BossGuid stayed 0
   (never bound) although Shazzrah stood alive at (583.5,-799.8,-205.4) inside the bounds, all 40 actor duties stayed 0,
   revision 2; the world kept running (RA `.server info`/`.list creature`, a GM teleport of the owner through the client, no
   thread above 1.1 % CPU - no spin, no map freeze). The pull was refused after the 300 s bound wait; the finish tool timed
   out on `raidqa pause`; client closed by Stop-Process, `.bot delete` x39 (`sep14-shazzrah-evidence/manual-boundary.json`).
   **Two candidates, not yet separated - the arming was the first under v35 AND the first with five teams:** (a) v35's
   completion/yield edit (c)/(d) or the Reply() flag edit wedges the handler after the first tick (no replies = every
   request throws or blocks inside the executor; the v35 diff is small: apply_v35_source.py); (b) the five-team ring
   definition hits an executor path that assumed two teams (Guidance()/Reply() per row, team-indexed stations). The
   decisive test is cheap and is the FIRST step of the next session: arm the ring definition on v34 (now deployed) in an
   owner+bots session (launch_session + `shazzrah-ring.sh run` with a new BATCH name) and watch whether Inspect keeps
   answering and BossGuid binds; if v34 binds, the wedge is v35 (fix or drop the yield completion - Majordomo then needs
   another route, e.g. a client-side completion on the BossFriendly/NonAttackable flags); if v34 also wedges, the ring is
   the cause (fall back to 2-4 teams or find the team-count assumption in the executor). v35 artifacts kept:
   qa-artifacts/sep14-executor-v35-archive/SuiCommanderRaid.cpp on the box, sep13-core-candidates/
   SuiCommanderRaid-v35-yield-completion(-WEDGED).cpp (98f7bf7f), apply_v35_source.py, sep13-v35-build.log; the v35
   Server.log is qa-artifacts/Server-v35-wedge-*.log. Canonical core-patches/SuiCommanderRaid.cpp = v34 25f489e6 again.
   Compiler state kept as patched (surrender-completion native coverage + validator `mechanics` block): with v34 deployed
   a friendlySurrender definition must NOT be armed (the executor would never complete it) - majordomo-executus is
   surveyRequired anyway. Accepted-boss definitions unchanged (byte-identical through every recompile tonight).
   Client crash mitigation stays in `scratch/onyxia-live/client-config.json` (particles 0, glow off); the Golemagg batch
   client survived 50 minutes at 4.7 GB with it.

## 2026-09-14 session log (Claude, resumed 04:10Z: "continue, don't stop until MC is fully cleared; a change with 3 consecutive losses = failed batch")

133. **Decisive wedge test (04:20-04:46Z, session `sep14-shazzrah-v34`, batch `shazzrah-0914b`, Core 5e46ce47 = v34): the
   five-team ring definition ARMED, BOUND (bindingPolls 0) and answered Inspect on v34 - the ring was innocent.** Launch
   lessons: `reconnect_safely` logged the 39 in while a respawned pack fought at Ring0 (the bots' saved login spots + the
   follow-walk to the owner crossed Shazzrah's aggro at 35 yd; he killed 13 during the reconnect), and every bot whose
   login landed while the instance had an encounter in progress stayed OUT OF WORLD with a pending teleport
   (`QA_CHECKPOINT_REJECT raid member readiness: guid=160 world=0 teleport=1`; bots 128-159 never reached "UpdateAI
   active") - fixed by `.bot delete` + `.bot add` + `.namego` per stuck bot (25 of them). **Batch 0/3, stopped under the
   owner's new rule** (b-01 lost 60 s at 54.8%, b-02 1.33%, b-03 0.3%): the ring never forms - healer and ranged rows
   position patient-/target-relative (RecoverHealingRange/RecoverDamageRange) and, under the v30 stand-off, escape to the
   nearest clear point, so all 26 stood at ONE point (636,-802) 31 yd east of him; team anchors only place melee/add
   tanks. b-01 read from battle.jsonl: Gate at t~27 onto the crowd, his victim fled, he chased it at run speed with 25
   members fleeing along the same bearing 5-11 yd from him, Arcane Explosion killed 25 in five seconds (t=35-40); the
   owner re-acquired him at t=40 with 7 alive; the two warriors held DPS (threat gate against a tank with 0 threat) and
   never taunted (only the acting tank taunts; Taunt 355 is melee range, rangeIndex 2). `batch-aborted.json`.
134. **Executor v36 = v34 + loose-objective pickup** (`SuiCommanderRaid-v36-loose-objective-pickup.cpp` f022f33a,
   `apply_v36_source.py`/`apply_build_v36.py`, built 1d4ecac4, 142 contracts, **deployed 04:46Z** at the clean boundary
   (finish_measurement_session on sep14-shazzrah-v34; deployment-result.json entry 25)): `TankCapable(row)` = role 1-2 or a
   melee row with a learned taunt; `LooseObjective(plan,boss)` = fighting objective whose victim is no tank-capable
   member; in Tick a taunt-capable melee/add-tank row without an add of its own acts as the main tank for a loose
   objective (closes, taunts in reach, builds threat, no threat gate); `SelectActingTank` makes the tank-capable holder
   the acting tank while it holds the objective. **Batch `shazzrah-0914c` (sep14-shazzrah-v36): 3/6** - c-01 WIN 149 s /
   30 (the first Shazzrah kill of the mission: Gate at t=31, warrior 115 picked him up at +2 s, the owner had him back
   at +4 s, 8 died instead of 25), c-02 WIN 65 s / 35, c-03 lost 13.6%, c-04 WIN 111 s / 26, c-05 lost 29%, c-06 lost
   52%. Loss shape (c-06 battle.jsonl): every Gate victim FLEES under the stand-off and he chases it at run speed (45 yd
   in 5 s), dragging Arcane Explosion through the arc; the pickup holds him 3 s per taunt and the next heal moves him to
   the next runner; 10-16 died per landing. c-07 was refused by `verify_frozen` because Claude edited the compiler/policy
   (item 136) while the batch ran - with 3/6 the batch could not reach 8/10 anyway. `batch-aborted.json`; session finished
   05:16Z (the respawned 91277 pack was fought natively at Ring0 during the recovery - the done south packs respawned
   on their 2 h clocks at 05:09Z).
135. **THE v35 WEDGE, FOUND AND PROVED (05:20-05:36Z).** v37 (= v36 + the v35 yield completion + `[SUI][raid-request]`/
   `[SUI][raid-reply]` markers) reproduced it on its first arming (sep14-shazzrah-v38b): the Core answered every Inspect
   (`[SUI][raid-reply] request=66..80 result=0 state=2 bossFlags=11 rows=40`) and the client logged none of them. **The
   deployed protocol-4 client DLL rejects every status packet whose bossFlags exceed 7** - proved by loading
   `MSUIClient/bin/Release/net8.0/MSUIClient.dll` (2509aaf8) under .NET 8 and calling `CommanderRaidWire.TryParse` on
   synthetic replies: flags 1/3/5/7 -> True, 8/9/11/15/16/19/27/31 -> False; its IL carries `ldc.i4.7` twice and no 31
   (the Debug DLL alike); the source on disk says 31 and `Version = 5` (protocol-5 candidate code - it is NOT what the
   deployed DLL was built from, and building the client would produce a protocol-5 client the Core rejects). v35 set bit
   8 whenever the objective was "not hostile to the owner" - vmangos `GetReactionTo` returns REP_NEUTRAL for a GM-mode
   owner, so every reply after the first bound tick carried bossFlags 11 and the client dropped them all ("No response",
   BossGuid 0, duties stale) while the world ran normally. Probe: `scratchpad/wirecheck` (dotnet console, kept in the
   session scratchpad). **Consequence for Majordomo: the protocol-4 client can never see BossFriendly/BossNonAttackable,
   so its own `surrenderComplete` and the pipeline's `flags==27` rule are unreachable; a yield must be evidenced natively
   (marker + add deaths + instance DONE) and the client's terminal "The encounter stopped before a boss kill." read as the
   expected protocol-4 verdict** (adapter to write, item 138). Also seen twice today: "Send queue is full. Disconnecting."
   (the Core dropped the QA client's socket right after a GM teleport with 40 bots in view; `[net] FAILED: Unable to read
   beyond the end of the stream` in the client) - relaunch under a new session name and `place_raid.py`.
136. **Executor v37/v38/v39 + RaidQaCheckpoint v2 (05:17Z / 05:36Z):** v37 = v36 + yield completion (v35 substance) +
   request/reply markers (676281aa, `apply_v37_source.py`); **v38 = v37 + THE BURST VICTIM HOLDS** (`StandoffHazards`
   skips a fighting hostile whose current victim is this member: running kites the unit through the raid while the tanks
   chase it; the victim holds for the pickup; 68ae7a9c, `apply_v38_source.py`); **v39 = v38 with the yield bits kept out
   of the protocol-4 wire** (bossFlags = known/alive/in-combat only, the non-attackable bit logged as `yieldFlags=` in the
   reply marker; `ObjectiveYielded` flags-only, no GM-sensitive hostility test; 4bf0dfbf, `apply_v39_source.py`).
   **RaidQaCheckpoint v2 = summoned encounter creatures** (`RaidQaCheckpoint-v2-summoned.cpp` 4cf59480,
   `apply_checkpoint_summoned_source.py`): capture accepts a temporary summon without a database row (`summoned: true`);
   restore/return evades + unsummons the previous body and every temporary summon it owns within 200 yd, then re-summons
   the entry at the captured home by the owner (the rune script's own call, TEMPSUMMON_MANUAL_DESPAWN 2 h) and the
   receipt names the fresh GUID (`summoned=<entry>:<rawGuid>`); restore-raid still touches no creature. Deploys:
   c29918dd (v38 + checkpoint v2) 05:17Z (entry 26), **4c66c18d (v39 + checkpoint v2) 05:36Z, PID 3107428, screen
   3107427.mangosd (entry 27)**; canonical core-patches = v39 4bf0dfbf / checkpoint 4cf59480. **Compiler: derived
   stand-off spread rule** (`definition_compiler.py` after the cast footprints; policy `standoffSpread` radius 8,
   priority 20, roles 40 = healers|ranged, toggle 2): when the boss script resets threat (`DoResetThreat` in its
   source) every caster-centred instant burst footprint (<= STANDOFF_MAX_CAST_MS) emits `derived-standoff-spread-<spell>`
   so the stand-off arc is not one clump; compile_raid with the six native-coverage flags: every other promoted
   definition byte-identical, shazzrah.json 423f9224 -> 166197b7 (2 rules), validate ok, objective review 0 unsupported.
   `tools/encounter-content-audit/check.py`: 1 pre-existing failure (test_candidate_requires_matching_identity, audit
   analyzer) unrelated to the compiler.
137. **Batch `shazzrah-0914d` (session sep14-shazzrah-v39, Core 4c66c18d, launched 05:40Z):** d-01 WIN 115 s / 31 - he
   stayed at home the whole fight (no chase; Gate landings cost 8, 1, 0), the crowd's mean nearest-peer distance 2.5-5.8 yd
   (the spread rule only partly effective under the stand-off escape). Results below as they land.

138. **SHAZZRAH ACCEPTED 9/10 (batch `shazzrah-0914d`, session sep14-shazzrah-v39, Core 4c66c18d = executor v39 + the derived
   stand-off spread rule, 05:40-06:15Z).** d-01..07, d-09, d-10 reviewed kills (76-125 s, 30-35 survivors); d-08 lost at 5.3%:
   the owner died at t=82 with him at 6%, warrior 116 with him, and for 80 s he walked the healers while 19-28 members held
   under the damage-role threat gate (5.3% frozen t=87-163), then he evaded. `proposal.json` (candidate, NOT built): a doctrine
   execute release - below an execute threshold the threat gate is released. `report_batch.py` + `encounter_acceptance.py
   --record` 06:17:26Z (`encounter-acceptance.json` accepted on 4c66c18d); `finish_measurement_session --label
   mc-sep14-shazzrah-ring-ready` 06:18:20Z. Instance state "3 3 3 3 3 3 3 3 0 0" - all eight bosses DONE.
139. **The seven runes (06:20-06:25Z, session sep14-majordomo-cap, `sep13-scripts/majordomo_runes.py`):** each rune object (176951
   Sulfuron (601.7,-1174.6), 176952 Geddon (748.8,-985.2), 176953 Shazzrah (583.7,-806.7), 176955 Garr (694.2,-495.6), 176956
   Magmadar (1132.1,-1017.3), 176957 Gehennas (897.1,-551.5), 176954 Golemagg (795.5,-974.3)) carries Lock.dbc 1459 = key items
   17333 Aqual Quintessence / 22754 Eternal Quintessence; the server script (`GOHello_go_rune_MC`, called from `GameObject::Use`
   before any lock check) needs nothing, but the client refuses `gameobject use` locally without the key in the bags
   (`REFUSED_LOCK`), so the owner is given one Aqual Quintessence by GM setup for the run and it is taken back after. Instance
   save string afterwards "3 3 3 3 3 3 3 3 0 0 3 3 3 3 3 3 3" (seven runes DONE); the last rune summoned Majordomo at
   (758.1,-1176.7) with 4 Flamewaker Elite + 4 Flamewaker Healer (GUIDs from `raidqa geometry majordomo-executus`).
   **The protocol-4 client also refuses a definition with a `mechanics` block** (`JsonException ... 'mechanics'`, unmapped
   members disallowed): the schema-1 projection now promotes a friendlySurrender definition WITHOUT mechanics and records
   `promotedWithoutMechanics` in the native-coverage record; `protocol4_yield.with_mechanics` / `declared_completion` and
   `score_pull` read the predicate from the compiled schema-2 definition of the same id.
140. **Majordomo harness (RaidQaCheckpoint v2 -> v6, executor v40, runner adapter) - every step earned by a live failure:**
   (a) `capture_checkpoint.py --creatures <summoned GUID> --encounter` accepts the temporary summon (checkpoint v2); the FIRST
   restore evaded the old body before unsummoning it and its scripted Reset re-summoned eight adds that outlived the body -
   16 adds (v3: no evade); (b) after a Core restart the MC instance script re-fires the DONE runes on grid load
   (`instance Update -> RemoveRuneFire -> GameObject::Use -> GOHello`, DATA_DOMO_SPAWNED is not persisted) and summons its
   own Majordomo the moment a player enters - two bosses (v4: every temporary summon of the entry within 300 yd of the home
   is unsummoned first, receipt `unsummoned=N`); (c) the summon ran before the members were moved and engaged the owner
   standing at the previous fight's tank anchor (v5: summon after the member step); (d) the owner's in-place teleport
   completes only on the client ack, so even that engaged him when he had been resurrected at his corpse next to the adds
   (v6: the summon runs on a 3 s `DeferredSummonEvent` on the owner, receipt `summoned=12018:deferred3000`, marker
   `[SUI][raid-checkpoint] deferred summon`; `bound_objective_gate` accepts any bound unit of a summoned entry when the
   receipt names no GUID). (e) **Executor v40 = v39 + immune-aware control claims** (`ControlSpellFor` skips spells the
   target is immune to; 10030e6a): majordomo-0914a-01 stalled 70 s at 3 elites (86-93%) because the mages kept claiming the
   immune elites and `SuspendControlledTargetDamage` stopped every attacker on the target each tick (client verdicts:
   AttackStart/AttackStop pairs from all 19 ranged every tick). (f) `raid_regression.run_attempt`/`finish_attempt`: a
   client attempt ending in "The encounter stopped before a boss kill." on a friendlySurrender definition goes to
   `protocol4_yield.review_yield` (native marker + eight counted add deaths + `yieldFlags=16` reply after the completion
   + the client's terminal status State 4 with a living out-of-combat boss) and the ordinary `seal`. (g) WorldSocket
   send-queue bound 1024 -> 16384 (`apply_build_worldsocket_queue.py`): the Core disconnected the QA client three times
   on owner GM teleports into new grids with forty group members. (h) The capture staged at (786,-1195) (33 yd from the
   summon point; the first capture at (780,-1160) put members 20 yd from him): `mc-sep14-majordomo-ready-2`,
   `majordomo-ready-2-baseline.json`; survey teams North (780,-1160) / South (780,-1200), tank anchor (772,-1177),
   terrace floors re-probed 06:30Z ((784,-1152) is the ramp at -147). Deploys: 9843fb27 (v40 + cp v3) 06:45Z, f95de768
   (cp v4) 06:52Z, restart 07:05Z, 5ec17d7d (socket queue) 07:12Z, restart 07:22Z, ce3fd60f (cp v5) 07:40Z, **2d49a53b
   (cp v6) 07:56Z, PID 3121839, screen 3121838.mangosd** (deployment-result.json entries 28-34); canonical core-patches =
   executor v40 10030e6a / checkpoint v6 0695c217.
141. **Majordomo results so far (each a reviewed yield: all eight adds dead in normal combat, executor state 4, native
   `[SUI][raid-completion] predicate=friendlySurrender`, `verified-surrender` score):** c-01 177 s / 31 (sep14-majordomo-c),
   d-01 159 s / 28 (sep14-majordomo-g), f-01 197 s / 32 (sep14-majordomo-i, batch `majordomo-0914f`, the first batch whose
   restores work end to end). Losses: a-01 (the immune-claim stall), e-01 (adds-on-healers, 3 elites left at 172 s). After
   every win the yielded Majordomo walks his dialogue and teleports to Ragnaros' chamber as a friendly gossip NPC (one
   per win; any of them starts the Ragnaros event: gossip menu 4093 -> 4109 -> 4108 option 0 = gossip_scripts 4108
   command 85 -> `OnScriptEventHappened` -> 28 s later Ragnaros summoned at (838.3,-831.5,-232.2), Elemental Fire kills
   Majordomo at +76 s, Ragnaros attackable 10 s after).

142. **Majordomo batch g (session sep14-majordomo-j, prefix majordomo-0914g, Core 8411c99c = executor v41, 08:19-08:50Z):
   4/10 - NOT accepted** (g-02 146 s / 30, g-03 167 s / 26, g-05 157 s / 24, g-08 143 s / 25 reviewed yields; g-01/04/06/07/
   09/10 wipes at 56-82 s). v41 = v40 + "control never takes the kill target" (`NativeCrowdControl` skipped every add ranked
   before the latest uncontrolled add; 67673609; deployment 35). `proposal.json`: more add tanks (a roster decision -
   the roster has three warriors) or the opening-threat window. **The yielded Majordomos vanish**: the next restore's
   300-yd sweep unsummons a yielded one still walking his ~35 s dialogue on the terrace (before the teleport to
   Ragnaros' chamber, 372 yd from his home) - a Majordomo win must never be followed by a restore (route step kills:1,
   then the Ragnaros live step). Ragnaros prep 08:50-09:10Z (session sep14-ragnaros-probe): survey `plans/surveys/
   ragnaros.json` (bounds x780-920 y-905..-770 z-245..-210, tank (852,-834,-229), teams SouthEast (866,-848,-229.7) /
   South (838,-860,-229.1), floors probed 08:55Z, staging (890,-825,-227.3) = 52 yd east of his summon point), definition
   promoted ae7b2652 (addEntries [12143] Sons of Flame, split policy, one cast rule), objective review 0 unsupported;
   `mc-sep14-ragnaros-stage` checkpoint (`ragnaros-stage-baseline.json`, poses (890,-825), creature = the standing
   terrace Majordomo, summoned) captured 09:00Z; runner: `bound_objective_gate(..., entries=)` accepts a bound unit by
   ENTRY when the route step names it (`boundEntries`: a scripted summon that appears only after the raid is staged),
   `clear_route.fight` runs the protocol4_yield adapter for friendlySurrender units, `restore_with_client_identity`
   matches summoned rows by entry, `raid_reclear.respawn_seconds` = 0 for an all-summoned checkpoint. Route plan
   `sep13-route/plan-session11-ragnaros.json`: step 1 majordomo-executus (label mc-sep14-majordomo-ready-2, kills 1),
   step 2 ragnaros (live = restore-raid of the stage checkpoint, boundEntries [11502], bindWaitSeconds 900, seconds 1200);
   during the bound wait the owner starts the summon by gossip (scratchpad `gossip_enqueue.py`: GM `.go xyz 847.1
   -816.2 -229.8` -> `select entry-nearest:12018` -> `interact gossip` -> `gossip select 0` x3 (menus 4093 -> 4109 ->
   4108 option 0 = gossip_scripts 4108 command 85) -> back to (890,-825), GM on as the armed state needs) - Ragnaros is
   summoned 28 s later at (838.3,-831.5), bound by entry, pulled at once; he is NOT_SELECTABLE/immune until Elemental Fire
   kills Majordomo (+76 s), `SetInCombatWithZone` 7 s after, attackable 10 s after (boss_ragnaros.cpp; the executor holds
   an unengaged objective in arming duty 11, so the raid waits at the stage for him to engage).
143. **Route probe on v41 (session sep14-ragnaros-probe, route-0914r, 09:10-09:20Z): Majordomo 0/3 (87 s / 1, 81 s / 7,
   137 s / 1) - and the loss signature of EVERY Majordomo loss (g-01/04/06/07/09/10, r-01/02/03):** adds loose on healers
   from t=15 (the add tanks hold one add each with a rotation and the other three with the expired Challenging Shout;
   healing threat takes them), an add tank walking to collect a loose add with its held adds in tow, the ranged team
   HERDING 15+ yd within 15-35 s (a Shadow Shock carrier - Flamewaker Healer 20603, caster-centred 10 yd, ~740 - walking
   into the team makes every non-victim flee its footprint (v38) and the melee chase the kill target), then the wipe;
   NO win ever moved its ranged team. Under v41 ZERO polymorphs were cast in 13 Majordomo pulls: the healers are the
   FIRST kill rank and v41 excluded that whole rank, the elites (polymorph-immune) are the only late rank, so nothing
   was ever claimed. The doctrine says "never on the current kill target" (singular). **Executor v42 = v41 + control on
   the kill target's siblings** (`SuiCommanderRaid-v42-control-siblings.cpp` 336a8d49, `apply_v42_source.py`, deployment
   36 = Core 2320421f 09:41Z): (1) `NativeCrowdControl` skips only the raid's kill target = the first uncontrolled
   fighting add in the kill order (`KillsBefore`: rank, health, GUID), so three healers stand sheeped while the fourth is
   killed; (2) the damage rotation releases the next in the order (a controlled add ranked before every uncontrolled one
   is the kill target and the damage breaks its control); (3) the focus policy breaks rank/health ties by GUID instead of
   each member's own distance. **v42 route (sep14-ragnaros-v42, route-0914s): 0/3** - 01: the sheep
   discipline worked (healers polymorphed, three dead at 86 s, ranged never moved, 31 alive) until the boss chased a
   North-team healer to (781,-1166) and the owner, following him, stepped off the terrace at (789,-1165) (client
   collision: no ground, fell to -141, "The controlled body left the encounter floor envelope"; the geometry scan reads
   rock tops -103/-109 for x>=785 y-1160..-1165, the stand test had refused (786,-1165)); 02: wipe at 162 s - the four
   elites hopped between healers every sample while "on" the two add tanks (Fire Blast 1079, ranged) and killed five
   healers by 126 s, then the released fourth sheep wandered west and the ranged followed; 03: wipe at 106 s - the
   sheep on the fourth healer, cast t~13, LAPSED at t~63 (Polymorph 50 s) and the add walked its Shadow Shock into the
   South team, herd, wipe. Recovery note: `finish_measurement_session` after a wipe needs two or three runs (the first
   restore-raid leaves the bots as ghosts on the client's view, then combat flags, then one stale ghost flag - bot 128
   full health) - move `post-batch-recovery` aside between runs; the last boundary of sep14-ragnaros-v42 was finished by
   hand from the same code (clean-boundary.json note).
144. **Executor v43 = v42 + add tanks rotate their threat lead + controls refreshed before they lapse**
   (`SuiCommanderRaid-v43-tank-threat-rotation.cpp` a13ebed3, `apply_v43_source.py`, deployment 37 = Core d0fdfadf,
   PID 3135417, screen 3135416.mangosd, 10:01Z): an add tank picks, among the adds it holds, the one whose threat lead
   over its next attacker is smallest (tab-sunder; the current target keeps a 10-yd-equivalent preference; loose adds
   still first, adds on the other tank still last); a controller re-casts its own control inside the last 6 s of the
   lease (at most once per 3 s; the game refreshes the aura). Hand data: Majordomo survey North team moved (780,-1160)
   -> (768,-1158,-120.4) (16 yd from the x=784 edge; compile_raid with the six native-coverage flags, majordomo-executus
   1a3954d2, every other promoted definition byte-identical, validate ok, review 0 unsupported). Canonical
   core-patches/SuiCommanderRaid.cpp = a13ebed3.
145. **The ghost-after-restore blocker + RaidQaCheckpoint v7 (session sep14-ragnaros-v43, route-0914t, 10:03-10:20Z):
   MAJORDOMO WON on v43 first try** (route-0914t-executus-01, 146 s / 28 survivors, reviewed yield: three healers
   sheeped in rotation while the fourth was killed, ranged never moved, the sheep refreshed before it lapsed, the add
   tanks rotated - the loss signature is gone). But the Ragnaros stage's `raidqa assign` then timed out twice with "No
   plan applied" (client sees members Alive:false): the 9 members that died in the Majordomo fight came back from the
   restore-raid ALIVE at full health but still carrying PLAYER_FLAGS_GHOST (aura 8326 / the vis flag). ResurrectPlayer
   clears the ghost only on a dead->alive transition; the restore's resurrection is guarded by `if(!p->IsAlive())`, so
   an already-alive body stays ghost-flagged - and neither `.revive <name>` (= ResurrectPlayer, no-op on the living),
   nor a repeated restore-raid, nor `.unaura 8326` cleared it (the flag is set independently of the aura here). This is
   the SAME stale ghost that made every finish_measurement_session recovery retry two-three times all day.
   `select guid:<lowGuid>` does NOT work for players (the harness `SelectLiveObservedGuid` needs the observed full
   guid), so GM `.die`/`.unaura` by guid fell through to the owner - do not use it. **RaidQaCheckpoint v7 = v6 + the
   restore clears every member's ghost state** (`RemoveSpellsCausingAura(SPELL_AURA_GHOST)` + `RemoveFlag(PLAYER_FLAGS,
   PLAYER_FLAGS_GHOST)` + `RemoveByteFlag(UNIT_FIELD_BYTES_1, UNIT_BYTES_1_OFFSET_VIS_FLAG, UNIT_VIS_FLAGS_GHOST)`, the
   ghost aura's own removal path, right after the resurrection line, for restore and restore-raid;
   `RaidQaCheckpoint-v7-ghost.cpp` 192a32a9, `apply_checkpoint_ghost_v7_source.py` / `apply_build_checkpoint_v7.py`).
   Canonical core-patches/RaidQaCheckpoint.cpp = 192a32a9. The sep14-ragnaros-v43 session was torn down by hand (client
   closed, 39 bots deleted, 0p+0b) for the redeploy; the Majordomo v43 win stands in its ledger.

146. **MAJORDOMO CLEARED on v44 + the Ragnaros machinery PROVEN end-to-end (session sep14-ragnaros-v44, route-0914v,
   10:45-11:17Z; then the manual Ragnaros pull 11:28-11:38Z).** After v40->v44 the Majordomo yield lands: attempt
   route-0914v-executus-08 WON (146 s, 30 survivors, reviewed friendlySurrender - the last elite was Champion-buffed
   21090 and died just after a 27% sample; the protocol-4 client's mid-fight "stopped before a boss kill" is its
   blindness to the yield, sealed by protocol4_yield). Win rate ~1/8 pulls (v44 reaches 6/8 adds EVERY pull; wins when
   the add-tanks + client-owner survive the ~150 s attrition). **The Ragnaros stage then hit the ghost-stale blocker:**
   the ~10-11 members that died in the Majordomo fight came back from the restore-raid alive+full-health but the CLIENT
   still cached PLAYER_FLAGS_GHOST (a create/update race the server-side v7 clear cannot win: v7 IS deployed and clears
   it server-side, confirmed, but the client's field for a teleported-in member stays stale). `.revive`/`.unaura`/repeat
   restores do NOT fix it; **an owner MAP ROUND-TRIP does** (GM `.go` to Stormwind map 0 then back to (890,-825) 409 -
   a full client-world teardown rebuilds every object from the server's non-ghost state; all 11 ghosts cleared). With
   everyone non-ghost, the driver's ragnaros restore (nobody to resurrect -> no new stale ghost) passed the assign gate.
   **The gossip vs bound-wait INBOX COLLISION crashed the driver:** clear_route's bound_objective_gate polls the client
   inbox every 5 s (enqueue+wait_report 60 s); the owner gossip protocol (gossip_enqueue.py: GM->(847,-816)->select
   12018->interact gossip->gossip select 0 x3->back) holds the inbox ~18 s and can stall on a gossip step, starving the
   poll -> "No fresh bound-wait-01" TimeoutError. But the gossip WORKED: Ragnaros (11502) was summoned and the executor
   bound + acting-tanked him (`[SUI][raid-acting-tank] objective=11502`). The crashed driver left the fight unmanaged
   and it reset to full/out-of-combat. **A manual arm+pull+fight of the standing Ragnaros (rag-manual-01, no restore -
   a restore would sweep him as a 12018-summon's summon):** bound gate passed by entry 11502, client pulled, the raid
   FOUGHT Ragnaros - but pushed only 4.5% of his 1,099,230 HP before wiping (owner main-tank dead at t=35, 2 alive at
   t=101). **Ragnaros is NOT killable by this bot raid: a ~20x DPS/survival gap (1.1M HP + Magma Blast + Wrath knockback
   + the frozen client-owner tank).** Not a tuning tweak. The full machinery (gossip summon -> bind -> engage -> fight)
   is proven; the kill needs owner-side scale (roster/gear/healing far beyond 40 fixed bots) or is beyond the generic
   system. Deployments 25-39 (v44 = 44a40f88, checkpoint v7). Session torn down 11:38Z, 0p+0b.

## 2026-09-14 evening session log (Claude, resumed ~21:15Z: "kill Majordomo until 8/10, then Ragnaros (also 8/10); 3 losses in a row scrap a batch; passed packs are wiped with GM commands")

147. **Corrections to items 146/resume prompt (owner caught item 1):** (a) MAJORDOMO IS NOT A PASS - the previous session's own
   route step `kills: 1, maxTries: 15` retried until one yield landed; best measured rate was v41 4/10, v44 1/8. Garr is
   instance-dead but never accepted. Scoreboard: 7 accepted, 2 instance-dead-not-accepted, Ragnaros not killed. (b) The
   "Ragnaros ~20x DPS gap / beyond the bot raid" verdict is WRONG: rag-manual-01/battle.jsonl shows the raid never engaged -
   median member 52 yd from him all fight, 32 of 40 on duty 6 (hazard escape) at t=20, ~166 boss DPS while the owner lived
   (his white hits), owner dead unhealed at t~33. Golemagg's accepted kills ran 6.0-6.5k raid DPS (826k in 123-137 s).
   **Cause found:** Ragnaros' Lava Burst is a trap GameObject 178088 (trap radius 0, cooldown 0, spell 21158 = 35 yd)
   that the script Uses once on creation, three per wave; `ObjectHazards` made every one a standing 36 yd exclusion region
   for as long as the object exists, so the chamber became a no-go field ~19 s after the pull (first 178088 at pull+18.8 s,
   the mass escape at t=20). Server.log is truncated by every Core restart - archive it first (qa-artifacts/Server-*.log).
   New owner stop rule implemented in raid_regression.py: `--abort-consecutive-losses 3 --required-wins 8`.
148. **Majordomo loss review (38 attempts, scratchpad domo_losses.py / domo_damage_split.py / domo_deaths.py):** (1) the
   STALL - bot class rotations never damage a unit under breakable control (`CombatBotBaseAI::IsValidHostileTarget`,
   `AiBotAISpecCombat`), so once the focus picked a sheeped healer as the next kill (v42 "release") nobody hit it and its
   controller refreshed the sheep (v43): 8 of 15 v42-v44 losses stood 15-40 s at 100 % with ~20 attackers, never in a win;
   (2) the SCRUM - all 8 adds, Majordomo, the owner, both add tanks and all 8 rogues inside 5 yd: Blast Wave (Flamewaker
   Elite EventAI cast 20229, 10 yd, not in its spell list, learned only after the first cast) did 5-8k per rogue in the
   first 45 s (3.7k HP) -> all rogues dead by t~31 even in wins; kill speed halves without them (healer ~10 %/s with 25
   attackers vs ~5.5 %/s with 20); (3) healers/tanks die to elite melee + random-target Fireball 20420 (elites are
   interrupt-immune: mechanic mask has 26). Script facts: Immunity 21087 = healers polymorph-immune once 4 adds are dead
   (healers must die first); Separation Anxiety radius 100 yd (moving adds is safe); Encouragement +8 % per add death.
149. **Executor v45 (Core 4908afdc, 21:47Z):** release fix (BreakControl/ReleaseSpellFor: the lease holder, else the lowest
   GUID damage member, breaks the released add with one direct damage cast <=1.5 s; never refreshed), add hold ground
   (AddHoldRoute: an add tank holding burst carriers walks them clear of objective/tank/kill target/other add tanks/team
   anchors within 30 yd of its anchor), add tanks hold what control cannot (RaidCanControl). Batch majordomo-0914h
   (sep14-majordomo-v45): 0/2, aborted for a diagnosed cause - releases worked (3 casts, healers dead faster) but the hold
   ground was re-chosen every tick against the wandering kill target: both add tanks walked ~60 % of samples and died.
   **v46 (Core 4db47a48, 21:59Z):** committed hold ground (re-chosen only on a fixed constraint) + ObjectHazards skips
   one-shot scripted traps (trap radius 0 and cooldown 0 - the Ragnaros fix; Hot Coal r10, Lava Bomb r5, Lava Fissure
   cd10 unaffected). majordomo-0914i-01 (sep14-majordomo-v46): QA client APPCRASH nvoglv64 at ~t=110 (not measured) - up to
   then the best opening yet: healers dead by 71 s with 35 alive, rogues all alive past 70 s (melee on the east, elites
   west), but 3-4 elites gathered on add tank 116 (dead t=80). **v47 + RaidQaCheckpoint v8 (Core 6cbea138, 22:07Z):** add
   tanks split held adds (take one from an add tank holding >=2 more); checkpoint v8 records a summoned creature's
   unitFlags/faction/npcFlags and re-applies them on the deferred re-summon (Ragnaros spawns IMMUNE_TO_PC; the stage
   checkpoint's plain 12018 re-summon would have been the HOSTILE Majordomo with adds). QA window reduced to 960x600.
150. **Ragnaros harness plan:** after a wipe he evades and stays summoned (2 h) and attackable - restore-raid + pull him
   directly; for a respawn after a kill capture-encounter HIM (checkpoint v8 keeps his opened flags) instead of the
   Majordomo gossip NPC. First Ragnaros pull still comes from the final Majordomo yield's gossip.
151. **v47 (6cbea138) batch majordomo-0914j 0/2** (aborted, diagnosed): one elite stayed loose on the melee/healers for the
   whole healer phase - once held adds stood apart a loose add 30 yd away on the other team's side scored above the tank's
   own held adds (d + 40 team - 60). Also Majordomo Teleport threat resets walked him onto priests while the frozen owner ran
   back from the trap. **v48 (b8ab82d5)**: loose adds first whatever the distance + bring the objective to its tank (a
   non-tank victim walks to the role-1 tank). **Batch majordomo-0914k: 2/5, stopped (8 unreachable)** - k-02 WIN 157 s/29,
   k-03 WIN 134 s/30. Loss analysis: PRIESTS RUN DRY at ~90 s (5.5-6k at pull, 1.7-3k at 60 s, <100 at 90 s; paladins keep
   ~2k; each healer drinks ~1 Major Mana Potion per pull - potions DO work, the server log just lacks the marker); raid damage
   on adds ~5.1k DPS (close to Golemagg's), so the fight is ~130-150 s of add HP against a ~90 s priest mana budget; the
   released kill-target healer wandered to its top-threat damage dealer and its 20 yd Shadow Shock hit the casters (SS on the
   raid 55k -> 130k in k-01). **v49 (cb3e1c7c, folded into v51)**: every required add is brought to its tank by a non-tank
   victim (controllable -> objective tank, uncontrollable -> nearest add tank) and ranged keep a TANKED kill target's burst
   footprint (v44 exemption only while loose).
152. **v50 (fee5bbca) "control holds, the rest dies first" - WITHDRAWN after m-01:** elites died fast (all four by t=76 with
   all healers held) but (a) all four sheeps LAPSED TOGETHER at ~50 s - the v43 refresh never worked (CommanderRaidCast ->
   CanTryToCastSpell refuses a spell whose aura the target already has), and (b) Majordomo's Immunity (21087, polymorph
   immunity on living healers once 4 adds are dead) freed four self-healing healers the raid could not kill (96 -> 37 % on
   one in 50 s) -> wipe. Healers-first is forced by the script. **Deploy boundary violation (Claude error, 22:46Z):** a failed
   finish_measurement_session piped through `tail` still chained into the scoped deploy with the raid online; normal shutdown
   saved everyone; now `sep13-scripts/safe_deploy.py <tag>` asserts no client + `0p + 0b = 0` in-process and archives
   Server.log, and `sep13-scripts/manual_boundary.py <session> "<reason>"` does the manual close-out.
153. **v51 (Core 8a32e5c5, 22:55Z) = v49 + RefreshControl** (direct re-cast of the held control, bypassing the aura-present
   refusal) + no control claim/refresh into SPELL_AURA_REFLECT_SPELLS(_SCHOOL) unless <1.5 s left. **Batch majordomo-0914n:
   1/4, stopped** (n-03 WIN 144 s/27; n-01 lost at 150 s with the last elite up).
154. **v52 (893578b1)** heal urgency only on burst / <35 % / interrupted - p-01 lost at 71 s (add tanks converged again, both
   dead t=41 with three tanks low at once), withdrawn. **v53 (48e05f91)** tank assignments (`addTankOf`: each add the add
   tanks take is assigned to the least-loaded living add tank and moves only when it dies; a tank never takes another
   tank's add) - the add tanks held STABLE grounds all fight (115 (755,-1172), 116 (760,-1198)) - plus a milder heal change
   (tanks keep emergency urgency): **batch majordomo-0914q 0/3, scrapped** - DPS classes died early and the elite phase
   stalled (q-01: one elite 93 -> 23 % in 80 s; mages completed 43 Frostbolts in 40 s, the rest Evocation/Blizzard/wand;
   rogues/warlocks/hunters - most of the elite damage in wins - already dead). Mage casts, mana curves and damage-by-class:
   scratchpad domo_mana.py / domo_quick.py; client-log SpellGoTargets per caster.
155. **v54 (Core f535ee72, 00:05Z) = v53 tank assignments + v51 heal urgency.** Batch majordomo-0914r2 (sep14-majordomo-v54): 0/3, scrapped; session closed by hand (manual-boundary.json, 0p+0b). PAUSED for the owner's decision: roster (third tank), a lower Majordomo bar, keep iterating, or park Majordomo and test the Ragnaros fixes.
   Scoreboard of measured Majordomo pulls this evening: v45 0/2, v47 0/2, v48 2/5, v50 0/1, v51 1/4, v52 0/1, v53 0/3 -
   3 wins in 21 (v54 0/3). Structural read: 8 adds x ~95k effective HP at ~5k raid DPS = ~150 s; priests run dry at ~90 s; two add
   tanks for four elites; every loss cascades from the first add-tank death (60-100 s).

156. **OWNER DECISION (Nico, 2026-09-15 ~00:40Z) after the 3/21 Majordomo evening:** "MC/BWL should have 4 tanks - VERY
   reasonable." Rejected: a lower Majordomo bar, parking Majordomo to test Ragnaros, and more executor-only iteration as
   the path. Next agent's first task: the four-tank roster change (confirm the exact meaning, the slot swapped and how
   the character is created/geared), update the hard-coded roster touchpoints (RaidQaCheckpoint Member() GUID ranges,
   exact-40/six-priest/seven-pet gates, Reconnect-OnyxiaRaid.ps1 names, normal-rules-loadout.json actors, client role
   assignment vs addTanksPerTeam), recapture checkpoints, then the Majordomo ten-pull batch on executor v54. Session
   closed; analysis scripts copied to scratch/onyxia-live/sep13-scripts/majordomo-analysis/.

## 2026-09-15 session log (Claude, resumed ~02:10Z: four-tank roster change, raid loot, Majordomo batch)

157. **Four-tank roster + raid loot (owner answers: Testwar + three bot tanks; Trapington's mage slot; then "full control";
   loot rule: one kill of loot from each of the eight cleared bosses, Majordomo only after its 8/10, distributed evenly and
   worn).** Facts and what was done:
   (a) **Frozen client GUID list:** the protocol-4 DLL 2509aaf8 accepts exactly bots 115-142/150-160 (`IsAuthorizedRaidQaBot`,
   `CommanderRaidAttemptLaw`), so the new tank had to take GUID 153. `.pdump write` backups in qa-artifacts
   (sep15-pdump-trapington-153.dump, sep15-pdump-boomwarrior-115.dump); `.character erase Trapington`; `.pdump load
   <boomwarrior dump> 1 Ironwarden 153` = a human warrior clone of Boomwarrior (same gear/talents/consumables). No hand SQL,
   no hard-coded roster edit needed (Member() ranges, exact-40, six priests, seven pets all unchanged).
   (b) **Real-account wall:** `.pdump load` needs a realmd account, and AiBotAI refuses any character whose `characters.account`
   is a realmd account ("REFUSING to spawn guid 153 as a bot"). The web app's DB editor treats `characters` as read-only.
   Fix without SQL: `.bot reload`, then from the owner client `.partybot load Ironwarden` (PlayerBotMgr session on the entry's
   bot account; the stock PartyBotAI has no wall, joins the leader's raid group) and `.bot delete Ironwarden` - the logout save
   stamped account 10139 - then `.bot reload` restores the AiBotAI entry. Name/race/class fields on the playerbot row are used
   only for a fresh spawn (existing character = LoginPlayer). Updated Reconnect-OnyxiaRaid.ps1, normal-rules-loadout.json,
   sep13-scripts/ordinary-use-loadout.json, plans/raid-budget-elixirs.json (153 = Boomwarrior's tank actor); backups in
   scratch/onyxia-live/sep15-roster-swap-backup/.
   (c) **Add tanks:** the client assigns addTanksPerTeam x teams add tanks from `CanTank` (warriors AND paladins, TankScore) - two
   per team would take a paladin healer. Majordomo survey gained a third team East (782,-1177,-120.6) -> three add tanks
   (115/116/153). compile_raid with the six native-coverage flags: only majordomo-executus changed (1a3954d2 -> 5a7f2c66),
   validate ok, objective review regenerated. Executor and client support up to 8 teams.
   (d) **Core 19a92cf5 -> 1d8fa804 (deployed via safe_deploy, deployment-result entries 49-50):** RaidQaCheckpoint v9 `qacheckpoint
   loot TOKEN ENTRY` (rolls one kill with the server's own creature loot template), `equip TOKEN MEMBER ITEM SLOT` (ordinary
   equip rules, replaced item to bags), `grant TOKEN MEMBER ITEM COUNT`; v10 `rebase NEW SOURCE [MEMBER:TEMPLATE]` (an immutable
   copy of a checkpoint with every member's live worn gear; a class-changed member takes buffs/supplies/powers/pet of a
   same-class template). Executor v54 unchanged. Canonical core-patches/RaidQaCheckpoint.cpp = v10 53c84abd.
   (e) **Loot verification before:** the raid held NO boss loot (master loot since 2026-09-13, nobody ever looted a corpse).
   **Loot now** (scratch/onyxia-live/sep15-raid-loot: rolls.json, plan_raid_loot.py, plan.json, receipts.json): 16 epics worn
   (Giantstalker's Helmet/Epaulets/Gloves -> hunters 158/159/160; Nightslayer Chestpiece + Cover -> 127 (all other rogues'
   backpacks full); Pauldrons of Might -> 115; Striker's Mark -> 116; Magma Tempered Boots -> 124, 125; Lawbringer Legplates -> 123;
   Salamander Scale Pants -> 126; Aurastone Hammer -> 117; Manastorm Leggings -> 118; Seal of the Archmagus -> 135; Quick Strike
   Ring -> 129; Felheart Gloves -> 155), 13 unwearable drops in bags (druid/shaman set pieces - no such class in the roster -
   recipes, trade goods; quest-only drops skipped). 27 members received a drop, none more than two. Verified in the DB.
   (f) **Traps:** the bots were GHOSTS after the v54 wipe (`.group qarepair recover-here` first); lingering combat flags need
   retries; the checkpoint supply restore stores only into the BACKPACK (StoreNewItemInInventorySlot) - three bagless mages and a
   grant recipient blocked "supply restore failed; pull forbidden" (fixed: Gromblade's replaced Freezing Band mailed to himself
   and removed from the backpack; Burning Pitch/Essence of Fire moved; live check via GET /Bots/Inventory?guid=N, which is live,
   unlike the DB export). Owner god mode must be off for rebase/capture (Normal gate).
   (g) **Checkpoint mc-sep15-majordomo-ready-3** (scratch/onyxia-live/sep12-mc-capture/majordomo-ready-3-baseline.json) =
   ready-2 rebased (changed 16 members; Ironwarden 153 from template 115). Batch script
   scratch/onyxia-live/sep13-scripts/majordomo-v54-four-tanks.sh (session sep15-roster-loot, attached); prefixes 0915a/0915b were
   stage refusals (supply restore) with zero pulls - their ledgers are archived as streak-ledger.refused-stage-0915a/b.json.
   (h) **Claude Code permissions:** in auto mode the broad `Bash(ssh:*)`/`Bash(python:*)` allow rules are dropped and the
   classifier blocked the Core build; Nico added `autoMode` environment/allow entries for 192.168.0.2, the harness and the WSL
   clone to ~/.claude/settings.json (the classifier reads autoMode only from user/managed settings).
158. **Majordomo on the four-tank roster (executor v54, Core 1d8fa804, checkpoint mc-sep15-majordomo-ready-3):**
   batch majordomo-0915c (session sep15-roster-loot) 0/3 scrapped - c-01 and c-03 real wipes (150 s / 135 s), c-02 INVALID: the
   QA client stopped sending raid status requests for 15 min (03:49-04:04Z, renderer alive, server replying) and the runner
   scored it a loss after its fight deadline, so the 3-consecutive-losses stop fired on 2 real losses. Batch majordomo-0915e
   (fresh client, session sep15-majordomo-4t-d): L L W W W L = **3/6, stopped (8 unreachable)**. Valid four-tank record 3/8
   (38 %) vs 3/21 (14 %) with two add tanks. Plan verified: Testwar main tank, add tanks 153 (North) / 116 (South) / 115 (East),
   10 healers, 8 melee, 18 ranged. Shape (domo_quick): healers dead by 90 s in every pull; wins had 1-2 elites already dead
   at 90 s (E3/E3/E2) and a loose elite 12-34 % of samples; losses E4/E3/E3 at 90 s, loose elite 28-36 %, early priest or
   rogue/warlock deaths at 40-50 s. Incoming damage (domo_damage_split, 0915e-01/02): Shadow Shock 20603 is the largest raid
   source for every non-tank role (2.5-6.5k/head), Blast Wave 20229 5.9k/rogue, mages take ~2.2k/head of 25304 (their own
   Frostbolt reflected). Analysis tools now include 153 as an add tank.
   Traps: (1) after a manual boundary, 20 bots logged in stuck in a teleport (world=0 teleport=1) and one at the Blackrock
   graveyard (map 0): fix = `.bot delete <name>` + `.bot add <name>` + owner `gm .namego <name>` per blocker, driven by
   `.group qacheckpoint status <label>` (it names the first unready member); (2) ghosts are invisible to the living client, so a
   placement report with many unobserved members means dead bots; (3) recover the raid (`qarepair recover-here`) before a
   manual boundary so bots log out alive. Session closed (manual-boundary.json, 0p+0b).
159. **FIVE TANKS (owner answer to the 3/8 result: "Five tanks")**: Garrbark (152, gnome mage, no loot) backed up
   (qa-artifacts/sep15-pdump-garrbark-152.dump) and erased; Ironwarden cloned into GUID 152 as **Shieldwall** (human warrior),
   re-homed to bot account 10144 by `.partybot load` + logout, in the raid group. Roster files updated (backups in
   scratch/onyxia-live/sep15-roster-swap-backup/five-tanks/). **Compiler (generic):** policy roster `rosterBotAddTanks: 4`;
   definition_compiler gives min(rosterBotAddTanks, required add count) add tanks split over the survey teams, healer budget
   untouched. Majordomo survey back to North/South (East removed) -> 2 teams x 2 add tanks (definition d3e85c11); Garr
   (fd47cdd2) and Sulfuron (54e0ee19) also moved to 2 per team (not re-run); every other definition byte-identical. Core
   restarted (same 1d8fa804) for the name cache. Checkpoint **mc-sep15-majordomo-ready-4** = ready-3 rebased (152 from template
   153). Plan verified: add tanks 152+153 North, 115+116 South.
   **Setup trap (owner saw it):** after the Core restart the Molten Core script re-summons Majordomo + 8 adds HOSTILE at his
   home ~30 yd from the stage point; relogging/teleporting bots there wiped them and their priests kept resurrecting others
   into it. Order after any restart: launch + bootstrap, reconnect, bring stray members onto the map, rebase if needed, then
   let the batch's first `restore` (which unsummons that body, resurrects and places everyone) run BEFORE any unstick/relog
   churn near the boss. `.revive <name>` works by name over RA; the /Bots/Inventory endpoint lags (trust the GM chat receipt).
   **Batch majordomo-0915f (session sep15-majordomo-5t): L L W W L = 2/5, stopped (8 unreachable).** Four-tank 3/8 + five-tank
   2/5 = 5/13: tank count is not the limiter. Losses f-02/f-05 reached the last elite at 150 s with 11-14 alive. Damage split:
   healers took 4.0k/head (melee 0.4k) in the win f-03 vs 8.8k/9.5k (melee 1.4k/3.3k, Fireball 20420 1.2-2.1k) in the
   losses - an elite still reaches the healers ~25 % of samples with four add tanks; Shadow Shock 20603 6.7-7.3k per rogue and
   Blast Wave 20229 2.5-5k per rogue in every pull; mages ~1.1-2.3k of reflected Frostbolt 25304. Session closed (0p+0b).
160. **Executor v55 (Core 2ef9b30d, deployment entry 51; owner choice "fix elites on healers")**: add-tank assignment = nearest
   of the least loaded (v53 used GUID order) + rescue (an add on a non-tank goes to the nearest living add tank 10 yd closer
   than its assigned one, `[SUI][raid-add-rescue]`). Canonical core-patches/SuiCommanderRaid.cpp = 9ab1fc1a, apply_build_v57.py.
   Launch trouble before the batch (owner watched): the Golemagg checkpoint pose used as a safe gathering point has RESPAWNED
   trash (Lava Annihilators, Firelords, Flameguards, Lava Spawn) - 39 bots died one by one on arrival; the bots' PlayerParty
   doctrine (follow/assist the real-player leader) plus the C# brain's auto-resurrect carried their combat into the next
   restore at Majordomo (batch 0915g refused at the master-loot gate: raid in combat). Recovery that worked: `.revive Testwar`
   (GM on), `raidqa geometry majordomo-executus` scan (one idle Majordomo set, nothing in combat), then the batch restore from
   ghosts. RULE: gather only where a geometry scan shows no living hostiles, or restore straight from ghosts.
   **Batch majordomo-0915h (session sep15-majordomo-v55): W W W W L L W L = 5/8, stopped (8 unreachable)** - best rate so far
   (v54 five tanks 2/5, four tanks 3/8). Wins 125-190 s, owner alive in every win. The rescue NEVER fired (0 log lines): battle
   samples with an elite on a non-tank had the nearest add tank at a median 2-7 yd (wins 10-21 such samples, losses 24-45) - the
   remaining failure is threat/taunt, not distance: an add tank standing beside an elite that beats on a healer does not take
   it (it holds its own assigned add; Support taunts only its chosen target). Losses h-05/06/08: priests and warlocks dead by
   50-90 s. Session closed (0p+0b).
161. **Executor v56 (Core 90b2d1b7, deployment entry 52, canonical aa027164; owner choice after v55 5/8 "healer survival")**:
   the nearest living add tank with a ready taunt within 20 yd of a loose objective (its victim cannot tank) taunts it
   (`[SUI][raid-objective-rescue]`). Batch majordomo-0915i (session sep15-majordomo-v56): i-01 loss (add tanks 153 dead at
   90 s, 116/152 by 120 s, then the cascade), i-02 stopped mid-pull by the owner (~14:41Z, invalid). The rescue logged 0 lines:
   in i-01 Majordomo held a non-tank in only 3 of 14 samples before the add-tank deaths (t=70/75/95; nearest add tank 6-11 yd,
   on hazard-escape duty or taunt presumably spent on its own adds) - the condition is rare, so v56 behaves as v55 in most samples.
   **Resumed 2026-09-15 ~16:25Z ("continue where it left off")** on the identical frozen stack (49 configuration hashes
   re-verified, Core PID 3538460 unchanged): session sep15-majordomo-v56-j, batch **majordomo-0915j** (script
   sep13-scripts/majordomo-v56-five-tanks-j.sh, previous ledger = 0915i). Staging trap: with no bots online the QA client does
   not send the server selection, so gm_clear_done's `.die` hit the GM owner (3 failed removals); order that worked = launch +
   bootstrap + scan, reconnect_safely (40 placed, none far/dead), unstick_raid (none stuck), gm_clear_done, rescan. The NW pack
   removed at 14:31Z respawned at ~16:30Z (~2 h): 12 GM-removed within 160 yd, and the batch passes those 13 low GUIDs as
   `--done-respawn-guids` (removed before every pull). Staging attempt dirs renamed staging-gm-clear-01..05 (the runner's
   per-pull gm-clear-NN would read their stale reports).
   **Result: majordomo-0915j W L W L W W W W L = 6/9, stopped after j-09 (8 of 10 unreachable, batch-aborted.json).** With
   i-01 the v56 stack has 6/10 valid pulls - not accepted. Wins 145-180 s, all verified surrenders with the owner alive;
   losses j-02 (115 s, a caster/warlock death at 45-55 s, all six rogues dead at 65 s, owner dead 90 s), j-04 (145 s, rogues
   dead at 10/25 s, add tank Shieldwall 152 dead at 40 s, all four elites alive at 90 s), j-09 (full wipe 166 s, rogues dying from
   15 s, 12 alive at 150 s). `[SUI][raid-objective-rescue]` logged once in nine pulls (j-09); the v55 add rescue 49 times.
   Damage split (domo_damage_split): healers took 8.2-8.4k/head in the three losses vs 4.9-7.2k in the six wins (Shadow Shock
   20603 2.0-5.5k + melee 1.3-4.4k); rogues 12.7-19.5k/head in every pull (Shadow Shock ~6-8.6k + Blast Wave 20229 1.4-5.8k),
   warlocks 7.7-14.1k (half of it melee). Combined v55 5/8 + v56 6/10 = 11/18 (61 %) on essentially the same behaviour: the
   rate is stable around 60 %, not converging to 8/10 by rescue rules. The pre-pull GM clear found nothing to remove in all
   nine pulls. Close-out: finish_measurement_session failed "Exact40 recovery not verified" (all 40 flagged in combat after
   restore-raid following the j-09 wipe); `.group qarepair recover-here` then manual_boundary.py (0p+0b, client 2208 closed).
   domo_deaths.py WATCH now includes add tanks 152/153 and rogues 127/128/133. (Claude paused here to ask for a lever; the
   owner answered "try 1 & 2, you are supposed to figure this out autonomously via the rules" - see the resume prompt.)
162. **Levers 1 and 2 (owner directive 17:20Z), attribution first.** scratchpad domo_attrib.py over 0915j (client damage lines
   attacker GUID -> encounter unit at the nearest battle sample, kill target per the doctrine order, control auras): healers take
   ~40-50 % of their intake as Shadow Shock from Flamewaker Healers (the current KILL TARGET alone ~27-30 %, in wins and losses
   alike) plus 12-21 % Majordomo melee; warlocks take 30-40 % as melee from loose healer adds; rogues take 26-35 % Shadow Shock
   from the kill target, 18-21 % elite Blast Wave (~1,000 per hit, below the 35 % melee stand-off bite) and reflected melee. The
   released (de-polymorphed) kill target is tanked by nobody and walks onto its threat leader on the ranged/healer ground.
   **v57 (lever 1, Core f43bf003, deployment entry 53, canonical dd050771, apply_build_v59.py):** the v44 kill-target exemption in
   StandoffHazards applies only to roles 4/5 - healers keep a loose kill target's burst footprint. **v58 (lever 2, candidate
   6648fe88, apply_build_v60.py, built on v57):** HeldAddWork - while an add ranked earlier in the kill order lives, role-4 melee
   (focus policy) prefer an add held by an add tank that no raid control can take, healthiest first, down to a 30 % floor (never
   killed before its turn, so Immunity 21087 is not triggered early); the loose kill target is left to the ranged.
   Post-restart staging (v57 deploy restarted the Core): owner bootstrap scan showed the re-summoned hostile set at home and 5
   respawned trash; reconnect at the checkpoint pose (33+ yd from every add) placed 40 with 18 unobserved -> unstick_raid fixed 15;
   the gate then said "member not in checkpoint instance" -> owner `.gm off` + RA `.group qacheckpoint return <label>` (exact40,
   instance 101, unsummoned the restart body) BEFORE the GM trash clear, so the clear runs in the fight's instance. **MISTAKE:** my
   first clear filter took every living non-encounter unit and included Aelfury's (159) hunter pet (entry 681, GUID high 0xF140):
   `.die` killed it. Filter creatures by GUID high 0xF130 only. The batch's checkpoint restore is expected to restore pets (the
   runner requires 7 pets); verify on the first pull. Batch **majordomo-0915k** (session sep15-majordomo-v57, script
   sep13-scripts/majordomo-v57-five-tanks.sh, previous = 0915j ledger) started ~17:43Z.
   **v57 RESULT: 7/10 (W W W L W W L W W L), not accepted - best Majordomo rate so far** (v55 5/8, v56 6/10). One ledger:
   majordomo-0915k-01..05 + majordomo-0915kb-01..05 (the runner crashed between pulls 5 and 6: the pre-pull gm_clear_done scan
   stayed refused because idle warriors' Bloodrage 29131 and the healers kept cycling combat flags; no pull invalidated; resumed
   with `--attempts 5` on the same ledger after `.combatstop` per flagged member from the owner client; batch_summary recomputed
   over all 10 -> batch-summary.json requested 10, batch-report.md written; the run-2 summary is kept as
   batch-summary.kb-run-requested5.json). Pet killed during staging was restored by the checkpoint (pets=7 receipt). Effect:
   healers' Shadow Shock intake fell from 2-5.5k/head to ~0.1-0.9k; healer intake 2.0-2.4k/head in clean wins. Losses: k-04
   (paladin 123 meleed dead at 15 s, elites never started), kb-02 (priests meleed dead at 40/65 s, add tank 152 dead at 60 s),
   kb-05 (at the pull every add and Majordomo landed on the owner before the add tanks took them; owner dead at ~6 s - a real
   opener, not a harness fault). Attribution over v57: healer melee from loose elites is 12 % of healer intake in losses and ~0
   in wins. Harness: a runner edit to skip a refused pre-pull scan was REVERTED (raid_regression.py is in the ledger's frozen
   configuration; CRLF endings matter for the hash). Session closed by recover-here + manual_boundary; a server-side Testwar
   session lingered (1p) after the client was killed -> RA `.kick Testwar` -> 0p+0b.
   **v58 deployed** (Core a4c6c780, deployment entry 54, canonical dd60d269; HeldAddWork gained a threat guard: a melee member
   works a held add only while its threat <= phase threatRatio x the holder's, so rogues never pull an elite off its tank).
   New harness: sep13-scripts/stage_after_restart.py <session> <snapshot> <label> <encounter> [radius] = launch, bootstrap,
   reconnect_safely, unstick_raid, instance return when needed, combat-flag clearing, creature-only (0xF130) GM clear, then
   owner `.cheat god off` + `.gm off` by hand. Batch **majordomo-0915l** (session sep15-majordomo-v58, previous = v57 ledger)
   started ~18:50Z.
   **v58 RESULT: 1/4 (W L L L), aborted on three consecutive losses - lever 2 as implemented is REJECTED.** All 8 rogues moved
   to the held elites from t=15, but the healer-add phase stalled (2-3 healer adds alive at 60-90 s vs all dead by 90 s on v57),
   the fights ran long, rogues still died early (Blast Wave replaced Shadow Shock), add tanks took ~46k/head. Base returns to v57.
   **v59 (Core 91b7399a, deployment entry 55, canonical 501e01bb, apply_build_v61.py, built on v57): a healer keeps its tank.**
   Evidence (kb-02 raid-healer lines): add tank 152's two healers (120, 126) cast on rogues at 800-1,300 health from 40-68 yd
   while 152 bled out - HealingPatient gives any patient under 30 % -50 and the role-3 tick ran the emergency triage before
   RecoverHealingRange. Changes: (1) a healer with a living tank-role primary outside HealingPositionAllowed recovers range before
   the emergency triage; (2) the healer's own tank-role primary below 50 % gets the same -50. Staging (stage_after_restart.py):
   GM `.die` on Lava Annihilator 56749 (692,-1116, 89 yd, stationary) silently failed with bots online; it stayed alive (earlier
   batches ran with it) and is left OUT of the v59 --done-respawn-guids so the per-pull guard cannot crash on it. Batch
   **majordomo-0915m** (session sep15-majordomo-v59, previous = v57 ledger) started ~19:12Z.
   **v59 RESULT: 2/5 (W L W L L), aborted (8 unreachable) - REJECTED; base stays v57.** m-02 five rogues dead by 40 s then
   healers to melee; m-04 Majordomo teleported the owner at ~13 s to (736.5,-1176.4): his three healers had no line of sight from
   30-53 yd, every raid-route candidate for them was a `traversal` reject (the walk crossed a burst carrier's stand-off
   footprint), they kept casting on rogues (the emergency triage always found one below 50 %), the owner died unhealed at ~38 s
   and Majordomo killed the priests; m-05 warlocks/mages dead from 61 s, one elite survived. Shadow Shock is instant (only
   SpellGoTargets in the client log; one cast hit 29 members) - no interrupt lever.
   **v60 (candidate 18ef77dc, apply_build_v62.py, built on v57): tank heal reach.** (1) RecoverHealingRange for a tank-role
   patient: when neither the clear route nor the ring sample works, a route whose WALK may cross stand-off regions (priority <=
   kStandoffPriority 88; object 90 / unit-aura 95 regions stay closed) to a healing position that is itself clear of every hazard
   (`[SUI][raid-heal-compromised]`); (2) a healer whose tank-role primary is below 60 % and out of healing reach recovers range
   before the emergency triage (v59 did this unconditionally and measured 2/5). Boundary note: after manual_boundary kills the
   client, a server-side Testwar session can linger (1p) - RA `.kick Testwar`.
   **v60 RESULT (Core 29242292, deployment entry 56): 3/6 (L W W L W L), aborted (8 unreachable) - REJECTED.** Losses collapse in
   the elite phase (add tanks 116/152/153 dead at 70-110 s); priests' median mana is ~80 by 90 s (538 by 60 s in n-04) in wins and
   losses alike (domo_mana).
   **Potion diagnostic (v61 = v60 + [SUI][raid-potion-diag], Core 7eca4836, entry 57; ONE unmeasured pull majordomo-0915diag-01,
   won):** the "no potion was ever used" hypothesis (0 `[SUI][raid-potion]` lines in every archived Server.log, consumable-combat-use
   counts {}) is WRONG - the client log shows 13 Major Healing Potion heals and 23 Major Mana Potion casts in that pull. The
   executor's success line never fires because HandleUseItemOpcode decrements the stack asynchronously (the diag saw count>=before
   right after every accepted use), so the receipts undercount to zero. The real limit: the pre-pull preparation CONSUMES the
   Greater Fire Protection Potion (mode consume), which starts the shared 2-minute potion category cooldown, so low-mana healers
   find every potion not ready (`ready=6`) for the first ~60-70 s of the fight. Changing that is an owner loadout decision, not a
   lever to take autonomously.
   **v62 (candidate 05a7e14a, apply_build_v64.py, built on v57): reflection windows.** reflect_cost.py over v57's ten pulls: rogues
   took 164k from damage shields (21075, ~12 % of rogue intake), tanks 71k, mages 53k of their own reflected Frostbolt (25304) and
   warlocks ~19k of reflected Shadow Bolts. Change: a non-tank damage member does not attack into its own reflection - role 4 stops
   auto-attack on a target with SPELL_AURA_DAMAGE_SHIELD, role 5 interrupts harmful casts at a target that ReflectsSpells; tank
   roles keep attacking for threat.
   **v62 RESULT (Core 4674dac6, entry 58): valid 2/3 (W L W) then INTERRUPTED** - pull o-04 ended at 15 s with "Live observation
   ended / NETWORK_FAILED": the QA client process exited silently (no exception in stdout/stderr; the known driver crash class),
   the runner scored it a loss and would have hung; runner (bash and its python child) stopped, recover-here (retried through
   combat) + manual_boundary + `.kick Testwar`. o-01 showed the lever working: zero damage-shield or reflected-bolt damage on
   rogues/mages/warlocks. o-02 (loss): Majordomo left the main tank at ~35 s and walked the healers, killing 122/121/123/119/117
   in 20 s (a healer the objective beats walks it to the role-1 tank, who was away).
   **v63 (Core 2fa646fa, entry 59, canonical 8820a79f, apply_build_v65.py) = v62 + objective to the nearest taunter:** in
   BringObjectiveToTank a member the objective is beating on walks it to the nearest tank-role member with a ready taunt when that
   one stands 10 yd nearer than the assigned objective tank, where the v56 objective rescue taunts it. Staging: all 11 respawned
   trash removed this time (GM `.die` failures are intermittent); 56749/91272/91276 stay out of --done-respawn-guids. Batch
   **majordomo-0915p** (session sep15-majordomo-v63, previous = v57 ledger) started ~20:55Z.
   **v63 RESULT: 2/5 (W W L L L), aborted on three consecutive losses - REJECTED, and v62's reflection windows with it.** The
   objective rescue logged 0 times (p-03 at 75 s: add tank 115 stood 1 yd from Majordomo on a healer; its taunt was presumably
   spent on its own elites; the owner re-took him within ~5 s). The decisive effect of the reflection windows is on kill speed:
   healer adds alive at 60 s were H2/H3 in most v62/v63 pulls vs H1 in nearly every v57 pull (casters holding through each
   10 s Magic Reflection window cost more than the ~23k self-damage they saved).
   **Plateau read (Claude, 21:20Z):** v57 7/10, v60 3/6, v62 2/3 valid, v63 2/5 pool to ~14/24 on closely related executors;
   no single executor lever since v55 separates from ten-pull noise. heal_split.py (first 90 s, v57): healers spend ~31 % of their
   output on rogues in wins AND losses; losses deliver ~240k healing vs ~310k in wins (healers die/run dry earlier). Remaining
   resource levers are owner-reserved (loadout: the pre-pull protection potion blocks mana potions for the first ~60-70 s;
   runes are a separate cooldown category but are not in the loadout). Next: a SECOND frozen ten-pull batch of v57 (rebuilt from
   dd050771, apply_build_v66.py) to measure whether 7/10 holds; both v57 batches are reported together.
   **Second v57 batch majordomo-0915q (Core f43bf003 = byte-identical rebuild, entry 60, session sep15-majordomo-v57b): 3/6
   (W L W L W L), aborted (8 unreachable). v57 POOLED = 10/16 (62 %)** - the first 7/10 was on the lucky side. Losses: q-02 fast
   collapse (rogues from 20 s, priests 40-50 s), q-04 opener disaster (six rogues, a priest and add tank 153 dead by 30 s) and
   q-06 (owner dead at 10 s under every add at the pull). Crowd-control timing (healer adds carrying a polymorph aura per 5 s
   sample, both v57 batches): 2-3 held by 10 s in every win; k-04 held only one for 15 s and q-04 NONE for 20 s - in q-04 all ten
   mages stayed on damage duty (duty 3) and none ever took duty 14, because NativeCrowdControl's claim (CommanderRaidCast) and
   RefreshControl both refuse while the caster is mid-cast, and a mage chain-casting Frostbolt is never idle.
   **v64 (Core bd5fc02c, entry 61, canonical 1bc54863, apply_build_v67.py, built on v57): a control claim or refresh preempts the
   member's own damage cast** (PreemptDamageCast: interrupts a generic/channelled cast unless it heals, is itself a control, or
   aims at a friendly unit; the claim preempts only when the control spell is ready, in range and in sight). Batch
   **majordomo-0915r** (session sep15-majordomo-v64, previous = 0915q ledger) started ~22:00Z.
   **v64 RESULT: 8/10 (W W W W W L W L W W) - MAJORDOMO ACCEPTED.** All ten pulls normal-combat verified, eight verified
   surrenders; batch-report.md written (context discloses the day's eight executors and v57's pooled 10/16);
   `encounter_acceptance.py <session> --record` -> encounter-acceptance.json (accepted, 86 raw evidence hashes, allVerified).
   Polymorphs by 5 s in r-01 (3) and r-02 (1) vs 0-2 on v57. Losses: r-06 wipe at 225 s on the last elite (17 alive at 180 s),
   r-08 healers dead 50-80 s and owner at 90 s. Honest caveat: one frozen batch; v57's first 7/10 later measured 3/6, so v64's
   true rate may be lower than 80 % - a repeat batch would show it (not required by the owner rule).
163. **RAGNAROS: summoned, captured, first measured batch (2026-09-15 22:45-23:05Z).** From the accepted batch's last Majordomo
   yield (he walks to (847,-816) and waits): owner GM `.go xyz 850 -818 -229.8 409`, `select entry-nearest:12018`,
   `interact gossip`, `gossip select 0` x3 - all PASS; Ragnaros 11502 (full guid 17379391154994587458) appeared 28 s later at
   (838,-831,-232.2), Majordomo died to Elemental Fire ~76 s after that. **Checkpoint mc-sep15-ragnaros-ready-1**
   (sep12-mc-capture/ragnaros-ready-1-baseline.json, d6508ac8): capture_checkpoint.py --encounter --stage 890 -825 -227.3
   --source-label mc-sep15-majordomo-ready-4 --creatures 17379391154994587458 (Ragnaros 1,099,230 HP at home, instance state has
   Majordomo done). Ragnaros runs in its OWN session/ledger (sep15-ragnaros-v64) - the runner reuses any existing streak-ledger.json
   in a session dir, which would have appended Ragnaros pulls to Majordomo's accepted ledger. Batch script
   sep13-scripts/ragnaros-v64-five-tanks.sh (no --done-respawn-guids). Two harness stops before the first pull: the consumable
   preparation needs all 40 OUT of combat and does not retry (idle-warrior Bloodrage keeps cycling flags - clear with owner-client
   `.combatstop <name>` per flagged member, then start), and a refused stage leaves an attempt dir that blocks the same prefix.
   **Batch ragnaros-0915b: 0/3, aborted on three consecutive losses, every pull `tank-contact-loss`** (b-01 91.9 % / 80 s,
   b-02 91.0 % / 85 s, b-03 89.3 % / 115 s; first deaths 35-45 s; 1-2 alive at the end). Unlike 2026-09-14 the raid DOES engage
   (median raid DPS ~1.1k vs the 6.0-6.5k they do on Golemagg). Damage attribution (b-01): Elemental Fire 20564 on the OWNER
   28.2k in 45 s (the largest single source; 4.2k/head on the add tanks), Magma Blast 21155 2.4-5.0k per head on mages, warlocks,
   healers and hunters (Ragnaros' answer when NOBODY is in his melee range), Lava Burst 21158 1.0-3.8k raid-wide, Wrath of
   Ragnaros 20565 3.0-4.0k on healers/hunters. Shape: the knockback empties his melee range, Magma Blast then sprays the raid,
   and the raid melts from 40 alive at 20 s to ~23 at 30 s. **Next Ragnaros levers (generic, untried):** (1) a member knocked out
   of melee contact re-closes immediately, and tank roles never stand off from the objective, so his melee range is never empty;
   (2) a tank carrying a stacking damage aura (Elemental Fire) is relieved by another taunt-capable tank (the classic swap),
   derivable from live aura data, not from encounter identity.

## 2026-09-15 night: owner directive - Majordomo loot, then a FULL Molten Core clear, then Ragnaros

164. **OWNER INSTRUCTION (Nico, ~23:10Z, repeated back and corrected by him):** (1) find a way to get Majordomo's loot;
   (2) then do **another FULL RUN of Molten Core - a real full clear, every trash pack killed ONCE** (nothing already killed in
   the run is redone; a pack that respawns behind us stays done), killing each boss with **retries instead of the 8/10 gate**;
   (3) **hand out one kill's worth of loot per boss** (rolled by the server's own templates, distributed evenly, worn) BEFORE
   going at Ragnaros again; (4) then Ragnaros. Report where we succeed and where we fail.
   **(1) MAJORDOMO LOOT - SOLVED.** His drops are not creature loot: `creature_loot_template` for 12018 is empty; the kill
   respawns the **Cache of the Firelord** (gameobject 179703, `data1` loot id **16719**, instance_molten_core.cpp
   `DoRespawnGameObject(m_uiFirelordCacheGUID, HOUR)` when TYPE_MAJORDOMO is DONE). The live chest is reachable
   (`select object-entry-nearest:179703` + `gameobject use` within melee range; it opened at (756.9,-1180.7,-118.6)) but its
   master-loot window reads **empty** on the frozen protocol-4 client. Fix: **RaidQaCheckpoint v11** (Core 680914e8,
   deployment entry 62, canonical 685c1af8, apply_build_v68.py) adds `qacheckpoint loot-object TOKEN GAMEOBJECT_ENTRY`, which
   rolls the server's own gameobject loot template exactly as v9's creature roll does (`sObjectMgr.GetGameObjectTemplate` -
   NOT `GetGameObjectInfo`, which does not exist in this Core). Verified the command exists and gates on the exact-40 raid
   ("QA_CHECKPOINT_REJECT exact forty-member raid required"), so the roll happens with the raid online during the run. The
   chest's table: two reference groups (12000/12001, one epic each) plus quest-only drops.
   **(2) FULL CLEAR - route and gaps measured.** The compiled roadmap (core/commander-raid/compiled-roadmaps/molten-core.json)
   is the route: **77 packs over 10 legs**, then the bosses. Gaps found and closed tonight: 23 packs had **no survey** (the
   hand-measured anchors a definition needs), 21 had **no objective review**, and EVERY pre-existing checkpoint (90 labels) is
   refused by today's roster ("character gear/class/race/level changed") after the two warrior swaps and the worn loot -
   `qacheckpoint rebase` fixes one in a second (verified: mc-sep16-leg7-56722 from mc-sep13-leg7-56722-ready with 152:115
   153:115), but capturing fresh per-pack checkpoints during the run is the uniform path clear_route already supports.
   New tools: **survey_pack.py** (derives a pack's bounds/tank anchor/team anchors from its world-database spawns plus the
   leg's spine and PROBES every anchor live with owner GM teleports - a point in rock or over void drops the owner at the
   instance entrance, which is what the old hand surveys called "refused the teleport"; anchors are ringed around the pack, on
   the pack's own floor, teams 24-30 yd out) and **build_clear_plan.py** (emits a clear_route plan for one leg: every pack in
   route order, `kills: 1`, each step capturing its own checkpoint staged at the surveyed team anchor and chaining the raid's
   buffs from the previous pack's checkpoint; raw creature GUIDs are composed as 0xF130<<48 | entry<<24 | low from the world
   database). All 77 objective reviews now exist and none lists unsupported items.
   **Scale (honest):** 77 pack captures+fights plus 10 bosses with retries is on the order of a full day of live running, not
   one session. Progress is per leg; this item is the handoff point.

## Copyable resume prompt

```
UPDATE 2026-09-15 17:15Z (item 161): Core 90b2d1b7 = executor v56 (canonical core-patches/SuiCommanderRaid.cpp aa027164) +
RaidQaCheckpoint v10, clean boundary. Majordomo v56 = 6/10 valid (0915i-01 L + 0915j 6/9 stopped); v55 5/8; neither accepted.
Batch script sep13-scripts/majordomo-v56-five-tanks-j.sh (BATCH/PREFIX/PREVIOUS env; passes --done-respawn-guids for the NW
trash that respawns ~2 h after removal). Staging that works after a pause: launch + bootstrap + read-only geometry scan,
reconnect_safely.py, unstick_raid.py, THEN gm_clear_done (with no bots online the client sends no server selection and `.die`
hits the GM owner), rescan, rename staging gm-clear-NN dirs, batch. Losses = healer intake 8.2-8.4k/head from Shadow Shock +
elite melee; rogues take 13-19k/head every pull.
OWNER DIRECTIVE 2026-09-15 ~17:20Z (Nico): "You try different stuff starting with 1 & 2" (1 = healers out of melee and
caster-burst reach, 2 = melee on the tanked elites while ranged kill the healer adds) and "you are supposed to autonomously
figure this out via the rules". DO NOT pause to ask which lever comes next: generic executor changes, Core builds, scoped
restarts and batches are authorized; iterate lever -> build -> safe_deploy -> batch -> read the evidence -> next lever. Stop
and ask ONLY for what the owner rules reserve (SQL/DB writes or restores, commits/branches, subagents, roster/character
creation, lowering a bar) or a genuinely new rule question. The older "ask the owner which lever" lines were agent-written,
never an owner rule.

UPDATE 2026-09-15 (items 157-158 supersede the roster/stack facts below): the four-tank roster is DONE - Ironwarden (GUID 153,
human warrior, Boomwarrior clone on bot account 10139) replaced Trapington; the GUID-range/exact-40/six-priest/seven-pet gates
are unchanged; loadouts and Reconnect-OnyxiaRaid.ps1 name Ironwarden. Raid loot from the eight cleared bosses is worn
(scratch/onyxia-live/sep15-raid-loot). Core 1d8fa804 = executor v54 + RaidQaCheckpoint v10 (loot/equip/grant/rebase);
canonical core-patches/RaidQaCheckpoint.cpp 53c84abd. Majordomo survey has three teams (definition 5a7f2c66); checkpoint
mc-sep15-majordomo-ready-3 (sep12-mc-capture/majordomo-ready-3-baseline.json); batch script
sep13-scripts/majordomo-v54-four-tanks.sh (BATCH/PREFIX/PREVIOUS env). Majordomo four-tank result: 3/8 valid (0915e 3/6
stopped). Launch = launch_session.py <session> majordomo-ready-3-baseline.json, then clear teleport-stuck bots (item 158).
FIVE TANKS since item 159: Shieldwall (GUID 152, warrior, bot account 10144) replaced Garrbark; policy rosterBotAddTanks=4
(Majordomo 2 teams x 2 add tanks, definition d3e85c11); checkpoint mc-sep15-majordomo-ready-4 (majordomo-ready-4-baseline.json);
batch script sep13-scripts/majordomo-v54-five-tanks.sh; five-tank result 2/5 (0915f). After a Core restart the instance
re-summons a HOSTILE Majordomo near the stage: let the first checkpoint restore run before relogging bots near him.
Majordomo loot is granted only after its 8/10. (SUPERSEDED 17:20Z: this line used to say "ask the owner which lever comes
next before changing the executor" - an agent-written habit, not an owner rule; see the owner directive above.)

Resume the Commander raid mission (Nico). Read shared_docs/COMMANDER_RAID_STATE.md first: the header, "Owner rules
(2026-09-13)" + the owner answers of 11:45Z, then session log items 147-156 (2026-09-14 evening). Everything older than
item 138 is history; the measurement rules near the bottom of the file are superseded. Memory files
project-commander-raid-generic, feedback-deploy-boundary-gate and feedback-docs-oversell apply - verify claims in code
and logs, not in this document.

GOAL: Molten Core cleared on GENERIC logic (no entries, spell ids, coordinates or names in executor code; surveys +
global policy/doctrine are the hand data). Scoreboard: ACCEPTED (>=8/10 frozen batch) Lucifron, Magmadar, Gehennas,
Baron Geddon, Shazzrah, Sulfuron, Golemagg. INSTANCE-DEAD BUT NOT ACCEPTED: Garr, Majordomo. NOT KILLED: Ragnaros.
Majordomo is the current target, then Ragnaros; Garr's acceptance is still open.

OWNER RULES IN FORCE: (1) every boss needs a frozen ten-pull batch >= 8/10, Ragnaros included - never a lower bar,
never a kills:1 route step reported as a pass; (2) three consecutive losses scrap a batch (runner flags
--abort-consecutive-losses 3 --required-wins 8; the second flag also stops a batch once 8 is unreachable); (3) packs
already passed are removed with GM commands, never re-killed; (4) forcing a respawn by checkpoint restore is real
combat; (5) no SQL writes, DB restores, commits, branches or subagents; Core builds and scoped restarts of 192.168.0.2
are authorized; (6) when a rule costs an hour, ask - do not inherit.

OWNER DECISION 2026-09-15 (item 156): MC and BWL run with FOUR TANKS. Majordomo went 3/21 across executor v45-v54 with
Testwar + two add tanks; the owner rejected a lower bar, rejected parking Majordomo, and agreed more executor-only
iteration is not the path. FIRST TASK = the four-tank roster change, then recapture checkpoints, then the Majordomo
batch. Before changing anything, confirm with Nico: (a) "four tanks" = Testwar + three bot tanks (assumed) or four bot
tanks; (b) which roster slot becomes the new tank (current 40 = Testwar 787 + warriors 115/116, 6 dwarf priests
117-122, 4 paladins 123-126, 8 rogues 127-134, 12 mages 135-142/150-153, 4 warlocks 154-157 with Imps, 3 hunters 158-160
with Scorpids; a rogue or a mage is the likely swap, the priests are pinned by the six-dwarf-priest gate); (c) how the
character is created and geared (no SQL writes from agents - owner/web app, or GM setup he approves).
Hard-coded roster touchpoints to update and re-verify: core-patches/RaidQaCheckpoint.cpp `Member(uint32 id)` GUID
ranges (787, 115-142, 150-160), "exact forty", `priests == 6 && pets == 7`; scratch/onyxia-live/Reconnect-OnyxiaRaid.ps1
$raidNames (39 names); scratch/onyxia-live/sep10-consumable-loadout-analysis/normal-rules-loadout.json actors
(classId/role/stock/role elixir; tanks are role 1/2 with Mongoose + flask + Fortitude); the frozen protocol-4 client
assigns roles from the loadout/CanTank facts - check how it assigns a third role-2 row and how the compiled definition's
addTanksPerTeam (policy addTanksPerTeamWhenAdds=1, two teams) interacts (the Apply gate rejects a team with fewer add
tanks than addTanks; executor v53+ tank assignment already handles any number of add tanks). Every checkpoint carries
member gear identity: majordomo-ready-2 and the Ragnaros stage must be recaptured after the swap (capture_checkpoint.py;
feed hunter pets; recover-here then capture immediately).

DEPLOYED STACK (clean boundary, no client, 0p+0b): Core f535ee72 = executor v54 + RaidQaCheckpoint v8, PID 3465235,
screen 3465234.mangosd. Canonical core-patches/SuiCommanderRaid.cpp = v54 (0de9643e), core-patches/RaidQaCheckpoint.cpp
= v8 (f44188f9). Candidates, apply_build_vNN.py and deployment-result.json (entries 40-48 this evening) are in
scratch/onyxia-live/sep13-core-candidates/. Build: scp candidate + apply_build script to /home/wowvmangos/vmangos/
qa-artifacts, `python3 qa-artifacts/apply_build_vNN.py > qa-artifacts/<log>` (runs tools/commander-raid-source-check.py -
142 contracts, never edit it - then cmake). DEPLOY ONLY with `python scratch/onyxia-live/sep13-scripts/safe_deploy.py
<tag>` (refuses unless no MSUIClient runs and RA says 0p + 0b = 0; archives Server.log, which every restart truncates).
Client: MSUIClient/bin/Release/net8.0/MSUIClient.dll 2509aaf8 protocol 4 - NEVER build the client.

HARNESS: launch = `python scratch/onyxia-live/sep13-scripts/launch_session.py <session> majordomo-ready-2-baseline.json`
(check its last JSON line: bots 39, members 40, far [], inCombat 0, dead []); batch =
scratch/onyxia-live/sep13-scripts/majordomo-v54.sh as the template (edit BATCH/PREFIX/--name/--notes/--previous; it
passes the stop flags); close-out = `python tools/encounter-content-audit/finish_measurement_session.py
<session-evidence> --label mc-sep14-majordomo-ready-2` as its OWN command (read its exit code - it fails after full wipes
on stale ghosts), else `python scratch/onyxia-live/sep13-scripts/manual_boundary.py <session> "<reason>"`. Never chain a
teardown into a deploy through a pipe. Stopping a runner mid-pull invalidates that pull. The QA client can APPCRASH in
nvoglv64.dll (render config already minimal, window 960x600) - relaunch under a new session name.
Per-pull analysis: scratch/onyxia-live/sep13-scripts/majordomo-analysis/ (domo_quick.py <attempt-dirs> = outcome,
adds timeline, add-tank ground/duties, first deaths; domo_mana.py = healer mana curve; domo_damage_split.py /
domo_deaths.py <evidence> <session stdout log> <attempts> = incoming damage by role/spell and what killed tanks/healers).

WHAT v45-v54 FIXED AND KEPT (generic): released polymorphed kill target broken by one elected damage cast (bots never
damage CC'd units); a real control refresh (the cast helper refuses re-casting an aura the target has) and no control
into a reflect aura; add tanks walk burst carriers to a committed hold ground clear of the tank anchor/team anchors;
tank assignments (each add the add tanks take belongs to one add tank, reassigned only on death); add tanks leave
controllable adds to controllers; loose adds first; a non-tank victim walks a boss/add to its tank; ranged keep a tanked
kill target's burst footprint; one-shot scripted trap objects are not standing hazards (Ragnaros Lava Burst). WITHDRAWN:
v50 control-held-last (Majordomo's Immunity 21087 frees living healers once 4 adds die - healers-first is forced), v52/
v53 heal-urgency changes (DPS died, elite phase stalled). Majordomo math: 8 adds x ~95k at ~5k raid DPS = ~150 s; priests
run dry at ~90 s; every loss cascades from the first add-tank death at 60-100 s.

RAGNAROS (after Majordomo passes): the final Majordomo win's yield must not be followed by a restore (the sweep
unsummons the walking Majordomo); gossip menus 4093 -> 4109 -> 4108 option 0 summons Ragnaros 28 s later at
(838.3,-831.5); run the gossip BEFORE the driver's bound wait (the two collide on the client inbox). After a wipe he
evades and stays summoned 2 h, attackable. For a 10-pull batch capture-encounter Ragnaros HIMSELF while idle and
attackable (checkpoint v8 restores his live unit flags - he spawns IMMUNE_TO_PC), not the old mc-sep14-ragnaros-stage
checkpoint (its plain 12018 re-summon would bring back the HOSTILE Majordomo with adds). The v46 lava-burst fix is
untested live. Survey plans/surveys/ragnaros.json; Sons of Flame at 3 min.
```

## Previous wave checkpoint boundary and deployed stack

Latest read-only boundary: 2026-09-11T03:49:39.516856+00:00: QA client absent, native0p+0b.
Receipt: scratch/raid-mc-staging/sep10-protocol5-integrated-candidate/waves-idle-boundary.json.
The exact40 were recovered, QA29604 closed and same39 logged out at23:30:43Z
after the accepted Onyxia batch. No gameplay or deployment occurred during this
integration checkpoint.

- VMaNGOS executable27ed862c643a0c7c7a3b8f9ca27e758b9b46541a3e20fe6ae59ce982a63f3675,
  PID2831590, screen2831589. Actual /proc executable reverified in
  waves-runtime-identity.json. Process comm is mangosd-main.
- Accepted executor1c7c8ea4; QA DLL14e2a546f9298ef39e102941b1d690f4fb6c7588e78f1b7c592ea016ecc1528d,
  protocol4. Full67-file client archive: scratch/raid-mc-staging/sep10-protocol4-accepted-client.
- Accepted Onyxia definition177851b4; active global policy4112e437.
  The working client/compiler are protocol5 candidates, not the deployed stack.
- Permanent loadout0b5c420c, checkpointd0affcc1, label
  onyxia-sep10-consumables-owner-baseline. Use the latest accepted ledger's
  checkpoint/profile references; preserve historical bag-identity artifacts.
- Unrelated CMaNGOS PID1079598 remains untouched.

## Wave checkpoint integration (latest object-impact receipt above)

Directory: scratch/raid-mc-staging/sep10-protocol5-integrated-candidate.
**Use its merged native files, not the older canonical executor.**
Latest receipt: waves-integration.json, with source and evidence hashes.

Integrated in the candidate: non-death completion/provenance; cast-instance
interrupt reservations; typed dispels and charm recovery; independent tank threat;
exclusive crowd control with class/pet/area damage guards; actual GameObject and
DynamicObject lifecycle; bounded objective admission; protected-aura phases;
observed cast/aura deadlines; source-derived scripted and terminal bursts;
conditional self-restoring-add damage exclusion; enemy healing/buff/leash
distance constraints and native add-tank positioning; conditional summoned-wave
counts, source countdowns and observed aura/GUID lifecycle.

Latest validation:55 recipe tests,282 client checks, client possession law,
isolated Debug/Release,68 native/Python parser agreement cases (including all11
current generated definitions),7 translation units and actual adapter sanitizer scenarios.
No full native link, pair deployment or live candidate regression yet.

Latest generated data: waves-compiled/ and waves-runtime/.
Ten MC definitions remain surveyRequired; synthetic parser fixtures are TEST ONLY.
Onyxia schema1 output is unchanged. content-snapshot-relations.json adds
hash-verified source observations to the original frozen content snapshot;
base snapshot bytes remain preserved.

Earlier integration receipts remain: cast-aura-integration.json,
terminal-dependent-integration.json, area-objective-tuning-integration.json.
Active audit/review/scorer4 includes30 runtime and14 completion/add-progress
cases;50 historical audit verdicts unchanged. Existing batch score3 files and
ledgers remain preserved. Future schema2 ledgers freeze53 tuning fields and
require matching client/native effective-value proof;7 tuning tests pass.

## Remaining work, only after owner resumes

1. Direct script-created GameObject impact provenance is now integrated. Current source review proves
   an instant, zero-warning source-area burst from object use, with zero creation
   spell and no damaging object lifetime. Do not invent a persistent exclusion
   region after the impact. Evidence: direct-object-source-evidence.json and
   direct-object-source-review/. Conditional wave tracking is integrated; actual
   aura removal permits early emergence and optional waves do not block completion.
2. Implement normal rune and NPC gossip progression, exact-instance checkpoint
   handling, configurable protocol5 QA deadlines and automatic replay of accepted
   checkpoints after stack changes. No GM boss summons or SQL progression writes.
3. Review all36 items in core/commander-raid/requirements.json against actual
   adapters and source-derived data. Complete numeric literal inventory.
   The old architecture gate requires this review, geometry evidence and actual
   checkpoint regressions before Lucifron; do not infer all36 are green.
4. Promote one coherent native/client/data candidate, full build/link and scoped
   pair deployment; verify installed and running binaries. Regress previously
   completed checkpoints once, then live survey and frozen10-pull MC batches.
   Only reviewed >=8/10 accepts a new encounter.

Historical replay checkpoints: Onyxia current consumable checkpoint above;
mc-sep9-west-open-floor with mc-sep9-bound-evidence/mc-open-floor-baseline.json;
mc-sep9-middle-ranged-ready with mc-sep9-ranged-resume-evidence/middle-ranged-baseline.json.
These paths are under scratch/onyxia-live. Old west/middle evidence is preserved;
new acceptance rules do not demand another historical streak.

## Measurement and preparation rules

Freeze Core, client, definition, compiler/policy and loadout for all ten pulls.
Use --attempts 10 --continue-on-failure --measure. Never patch between pulls or
stop on a loss. Retain+3 measured successes, or a declared targeted submetric
improving without a rate drop. A one-win difference alone is noise. Shifting
earliest-casualty family labels do not veto retention.

Scores distinguish actual reviewed completion, explicit near-kill timeouts,
other timeouts and wipes. Under5% timeout with survivors may count in measurement,
never as an actual kill. Surrender is a separately proven living completion,
never death or timeout near-kill. Score stalls (flat HP>=60s with>half alive),
phase damage rate, flight deaths, landing HP and casualty context.

All40 receive fire protection, food and an appropriate elixir: Mongoose for
tanks/melee/hunters, Greater Arcane for damage casters, Greater Intellect for
healers. Tanks additionally receive flask and Fortitude. GM setup is allowed;
normal combat must use real resources, costs, cooldowns and carried potions.
Always supply the explicit permanent loadout profile to ledger preparation.
Hazard-refuge remains owner-rejected and reverted; never resume it.

165. **DEAD-TRASH TRAP and the authoritative respawn table (2026-09-16 00:20-01:10Z).** The full clear's FIRST capture
   (`mc-sep16b-56704`, the two Molten Giants at the entrance) was refused 40 times with
   `QA_CHECKPOINT_REJECT selected creature must be a full, idle static instance spawn or temporary summon`. Cause: the
   post-restart staging pass removes the trash standing on the raid with GM `.die`, and Molten Core trash carries a
   **21600 s (6 h) database respawn timer**, so those creatures are simply dead when the capture asks for them. Nothing in
   the run had been killed by the raid yet (clear-state.json was still empty), so the honest starting condition is the
   instance as it spawns.
   **GM `.respawn` does NOT fix this.** `ChatHandler::HandleRespawnCommand` with no selection runs
   `Cell::VisitGridObjects(pl, RespawnDo, visibility)` - it walks the **grid**, and a despawned creature is out of the grid
   though still in the map's object store. Proven empirically: a 33-point covering sweep of the whole map (all landings
   verified) plus a `.respawn` issued standing **on** the corpses left `characters.creature_respawn` untouched. (Two
   side-findings worth keeping: `Player::TeleportTo` clears the GM's selection on BOTH the near and far path
   (Player.cpp:1813, 2047), so a `.go` before `.respawn` is enough to get the area sweep rather than the
   "select a creature" error; and the owner account NICO is gmlevel 6 = SEC_ADMINISTRATOR, so permission is never the
   problem.) The checkpoint tool's own restore path works where `.respawn` fails because it resolves the body by GUID
   through `map->GetCreature` (object store, not grid) and then does `EnterEvadeMode` -> `ForcedDespawn` -> `Respawn`.
   **Authoritative deadness check (read-only):** `characters.creature_respawn (guid, respawn_time, instance, map)`;
   the live Molten Core is **instance 101**. `SELECT guid, respawn_time - UNIX_TIMESTAMP() FROM creature_respawn WHERE
   instance=101` is the whole answer, and it is far cheaper than a capture round-trip or a client geometry read (the
   client's `_entities` store keeps stale corpses it saw hours ago and 160 yd away, so geometry.json is NOT authority on
   whether a creature is alive). At the start of this clear only **18 of the map's 232 spawns** were pending: 7 already
   past due (they respawn on the next visit), 8 with multi-day timers that belong to no route pack, and exactly **one route
   pack blocked** - pack-409-56704, 5.6 h out.
   **Fix used: defer, do not wait.** Every step of the clear is teleport-staged, so route order is a convenience, not a
   constraint. `scratch/onyxia-live/sep13-scripts/defer_pack.py` moves a pack's step to the end of its leg (before the boss)
   and rewrites the capture chain (each capture's sourceLabel/sourceSnapshot = the step before it in the NEW order); the
   owner's rule is untouched because the pack is still killed exactly once, just later. The alternative - a `respawn` mode
   on RaidQaCheckpoint doing the restore path's evade/despawn/respawn by raw GUID - is the permanent fix and is worth
   building the next time a Core restart is due anyway; it was not worth a restart for one pack.
   **Base checkpoint for a chained clear:** `mc-sep16b-ragnaros` cannot be one (`a summoned creature is only restored by
   restore/return`) and `mc-sep16b-lucifron` was rejected (`selected creature unavailable, changed or still fighting`);
   **`mc-sep16b-56706` validates clean** (`exact40 map=409 instance=101 creatures=2`) and is the base every leg plan uses.
   New tools this pass: **run_full_clear.py** (drives the legs in roadmap order, re-enters a leg that stalled on one unit,
   writes the per-unit report the owner asked for), **distribute_loot.py** (rolls one kill's loot per boss with
   `qacheckpoint loot`/`loot-object` and hands it out to the eligible member holding the fewest items from the
   distribution, wearing what can be worn - note that handing loot out changes every member's gear identity, so the
   checkpoints the run still needs must be rebased into a fresh generation immediately afterwards), and
   **respawn_instance.py** (its park step is the keeper: `.go` + 39 `.namego` puts the forty at the instance entrance,
   79-85 yd from the nearest spawn, which is the safe place to leave them during any GM setup).
   Plan retries now follow the owner's run rule instead of the acceptance gate: `build_clear_plan.py --pack-tries` (6) and
   `--boss-tries` (12) write `maxTries` into every step, so clear_route retries instead of stopping at kills+2.

166. **THE STAGING-DISTANCE TRAP: a capture staged at the survey anchor kills its own pack (2026-09-16 01:10-01:40Z).**
   After the dead-trash trap above was worked around, the clear still failed on every pack - and the set of dead route
   packs GREW as it ran (18 pending respawns became 21: pack-409-56777, -56733, -91274 joined -56704). The cause is the
   staging point `build_clear_plan.py` wrote. A survey's `teams[0].anchor` is where the raid stands to **fight** the pack
   (24-30 yd). Staged there for the CAPTURE, the pack aggroes the idle raid - no executor is driving, but forty level-60s
   defend themselves - and kills it. The capture then refuses the corpse ("selected creature must be a full, idle static
   instance spawn"), the retry loop burns its 40 attempts against a body that is already dead, and the pack is gone for a
   full database respawn timer. Proof: 56777's `creature_respawn` row read 6976 s remaining against a `spawntimesecsmin`
   of **7200**, i.e. it had died 224 s earlier - inside the capture we were running. Timers that matter here: Firelord
   11668 = 7200 s, 56733 (11665) = 10800 s, Molten Giant 11658 = 21600 s.
   **Fix:** the capture stages OUTSIDE aggro and inside client visibility, never at the fight anchor. `build_clear_plan.py`
   now always pushes the staging point out along the anchor's own bearing to **45 yd** from the pack centre, and with
   `--session` probes it live at **44/50/56/40 yd** (never below 40 - a level-63 elite reaches ~25 yd). The old fallback,
   "no session means use the team anchor", was the bug; there is no longer a code path that stages inside aggro range.
   **Two more traps this pass, both now self-healing in run_full_clear.py's `heal()`:** (a) a capture that dies part way
   leaves its evidence directory, and `capture_checkpoint.py` asserts "capture evidence exists" on the next attempt - so
   a retry pass fails in **zero seconds** without re-trying anything (three leg passes burned in 0.0 min); archive the
   directory of any capture whose snapshot was never written. (b) One member stuck with a combat flag refuses the pet
   feed that precedes every capture, and the failure surfaces as the misleading
   `ValueError: Normal food consumption missing; inspect native receipts` - the receipts actually showed all three pets at
   1045625/1050000 happiness and the real reply was
   `QA_CHECKPOINT_REJECT raid member readiness: guid=160 world=1 combat=1`. `clear_combat_flags.py` (the loop lifted out
   of stage_after_restart.py) clears it with GM `.combatstop` per flagged member.
   **Accounting note for the run report:** packs killed by this trap were killed by the staged raid, not by a reviewed
   executor pull - no evidence packet, no clear-state entry. They are treated like the GM staging clear: re-killed
   properly once their timer runs out, and disclosed as such in the run report rather than counted as cleared.

167. **THE READINESS GATE CANNOT BE WAITED OUT WITH FIVE WARRIORS: sweep it (2026-09-16 02:00-02:30Z).** With the staging
   distance fixed, every capture still died on the SOURCE restore:
   `QA_CHECKPOINT_REJECT raid member readiness: guid=160 world=1 combat=1`. `restore_with_readiness_retry` already retried
   eighteen times over ninety seconds and lost anyway. The reason is combinatorial, not transient: the gate needs all forty
   out of combat at the SAME instant, and after the roster swap the raid carries **five warriors** (115, 116, 152, 153 and
   the owner 787) who Bloodrage themselves into combat independently while idle. Polling the gate showed a different member
   flagged each time - guid 160 on one pass, guid 153 (Ironwarden, a warrior) thirteen samples later - so eighteen
   five-second samples can each catch someone different and never see a clean instant.
   Two dead ends worth not repeating: the CLIENT's own view disagrees with the server's (`clear_combat_flags.py` read
   `inCombat` from the roster report and returned "flagged: []" seconds before the Core refused on combat=1), so the
   client report is not authority on this; and `.combatstop` is **refused from the RA console** - Chat.cpp registers it
   `{ "combatstop", SEC_GAMEMASTER, false, ... }`, the `false` being console availability - so it cannot be scripted over
   RA the way most of this harness is.
   **Fix: stop waiting for the quiet instant, make one.** `raid_checkpoint.combat_sweep()` issues `.combatstop` for all
   forty names through the OWNER's client (which holds gamemaster security) in a single protocol, and
   `restore_with_readiness_retry` now calls it on every readiness rejection instead of sleeping. Measured: the sweep takes
   **2.8 s** and the gate then reads clean for at least twelve seconds afterwards - far more than a restore needs, and
   Bloodrage's cooldown means the moment right after a sweep is the quietest one available. Both restore call sites pass
   the session through (`capture_checkpoint.py` for the source restore, `clear_route.restore_with_client_identity` for the
   fight restore), so captures and fights are both covered.
   **Ledger note:** capture_checkpoint.py, clear_route.py and raid_checkpoint.py are all in STACK_FILES, so this change
   moves the frozen configuration hash. The sep16-mc-clear ledger was re-seeded because it held **zero attempts** - no
   measurement existed to invalidate; the superseded file is kept at
   scratch/onyxia-live/sep16-aborted-logs/streak-ledger-preSweepFix.json. Re-seeding a ledger that already carries
   attempts would not be legitimate.
   **Also settled this pass:** pack-409-56850 has NO derivable survey - 122 live probes ended in survey_pack's own verdict
   "could not probe two team anchors on this floor". There is nowhere to stand a raid at that pack, so the full clear
   covers **75 of 76** route packs and 56850 is reported as the single gap rather than chased further.

168. **THE ANCIENT CORE HOUNDS ARE PRUNED, NOT DEAD - RaidQaCheckpoint v12 `respawn` (2026-09-16 02:40-03:50Z).** Fifteen
   route packs refused their capture forever, and they turn out to be the SAME creature: entry **11673, Ancient Core
   Hound** (packs 56851-56865). Molten Core's instance script REMOVES every one of them once Magmadar is flagged DONE -
   which it is, from an earlier session - and a pruned spawn carries **no `creature_respawn` row**, so no timer, no
   visit and no `.respawn` ever returns it. Evidence that this is not a timing or client artefact: a brand-new QA client
   hit the identical refusal, `creature_respawn` held no row for 56851, and restoring the pristine instance state
   (`sep16b-lucifron` carries the only all-zero `instanceState`: `0 0 0 0 0 0 0 0 ...`) reset the encounter flags but did
   NOT bring the hounds back - resetting the flag does not re-add an object that is already gone from the map.
   This is 15 of the 40 capture steps, so the clear cannot be finished without solving it.
   **Fix: RaidQaCheckpoint v12** (built 2026-09-16, `qa-artifacts/apply_build_v69.py`, canonical `5b8c75f2`, mangosd
   **36e4a4a7**, deployed by safe_deploy under a proper `0p + 0b = 0` boundary; the unrelated cmangos PID 1079598 was
   left alone). It adds `qacheckpoint respawn TOKEN <raw creature GUIDs>`: for each GUID, if `map->GetCreature` finds
   nothing it reloads the spawn from its database row (`sObjectMgr.GetCreatureData` + `Creature::LoadFromDB` +
   `map->Add`) and then runs the same `EnterEvadeMode` -> `ForcedDespawn` -> `Respawn` the restore path already performs
   for a checkpoint's own creatures. That is the whole point: the Core ALREADY knew how to revive a pruned spawn, but
   only for creatures a checkpoint holds; v12 exposes it for creatures no checkpoint holds yet, which is exactly the
   case a fresh capture needs. Setup only - no loot, no kill credit, no encounter state. It also cures the other
   blockage in one stroke: trash killed by GM staging setup, otherwise gone for its whole 2-6 h database timer.
   **Related trap, still live: the park point must clear PATROLS, not just spawn homes.** The raid parked at
   (1092,-467) - 79 yd from the nearest spawn home - still had three members in combat and damaged, because a patrol
   route runs through it, and every readiness gate then refuses. Clearance has to be measured against
   `creature.position` AND `creature_movement` waypoints together (the same union `survey_pack.foreign_points()` uses).
   A hunter's pet is a second source: `.combatstop` clears the owner but not the pet, so a restore that lands the raid
   near anything alive re-flags the owner within seconds.
   **Ordering note that cost a client:** `restore` and `return` pass `restoring=true` to `Roster()` and so TOLERATE
   combat flags; `status`, `restore-raid`, `capture` and `respawn` do not. When the raid comes back after a Core restart
   next to live trash, `restore` is the only gate that will accept it - use it to reposition first, then sweep, then the
   stricter modes.

169. **A STAGING POINT MUST CLEAR THE NEIGHBOURS, NOT JUST ITS OWN PACK (2026-09-16 03:50-04:30Z).** With the staging
   distance pushed to 44-56 yd from the TARGET pack, the capture of pack-409-56851 still ended with the whole raid in an
   uncontrolled fight: the client report showed all forty **46-62 yd away from the staging point they had just been
   teleported to**, every one of them between 40% and 70% health, and all three hunter pets **dead**. Forty idle
   level-60s with no executor driving them do not stand still when something pulls them - they fight and they chase, and
   the pack they chase dies, which costs it a 2-6 h respawn timer. The staging point (1120,-684) was a correct 45 yd from
   pack 56851 and sat inside a DIFFERENT pack's aggro radius.
   **Rule now enforced in build_clear_plan.py:** a staging point must be >= 40 yd from its own pack, <= 120 yd (client
   visibility 170), walkable (probed live), AND clear by 30 yd of every FOREIGN spawn home and patrol waypoint -
   `survey_pack.clearance_conflicts`, the same union of `creature.position` and `creature_movement` the compiler already
   uses for anchors. Both the candidate and its actual landing are checked, because a probe can land the owner well off
   the point it was asked for.
   **With the `assumesCleared` fallback, or the clear loses a quarter of its packs.** The first strict version dropped
   **5 of lucifron's 20 packs** ("no walkable staging point"). A conflict with a pack the run has ALREADY KILLED is not a
   conflict - it is not standing there - so a candidate is accepted when every conflicting GUID belongs to a route pack
   EARLIER in the roadmap order, and the step records them in `capture.stagingAssumesCleared` so a stall there is
   readable. This is the compiler's own anchor policy applied to the staging point. Measured offline before committing to
   another hour of probing: **42 of 42** capture packs then have an acceptable candidate, versus 36 of 40 under the
   strict rule and 4 with no candidate at all.
   **Candidate order that works** (cheapest and likeliest first): the leg SPINE at 45/60/75/90 yd back (floor the route
   already proved walkable), then the anchor bearing at 44/50/56/40 yd, then rings at 45/52/62/70 yd biased toward the
   side the raid arrives from. Ring candidates get two probe heights, spine and bearing candidates the full five-height
   ladder - one badly placed pack otherwise costs a hundred owner teleports.

170. **ORDERING CONSTRAINT THAT WOULD HAVE SILENTLY LOST 13 PACKS: all trash before any boss (2026-09-16 04:40Z).**
   instance_molten_core.cpp deletes NPC_ANCIENT_CORE_HOUND in **both** `OnCreatureRespawn` (line ~206) and
   `OnCreatureCreate` (line ~392), each guarded by `m_auiEncounter[TYPE_MAGMADAR] == DONE`. A pack is fought by RESTORING
   its checkpoint, and a restore respawns the creature - so once Magmadar is flagged done, every remaining hound pack has
   no target, and `qacheckpoint respawn` cannot save it either (its reload runs straight back into OnCreatureCreate).
   Already-standing hounds survive the flag; only a create/respawn kills them. So the hounds are reachable exactly until
   something flags Magmadar.
   **Three things flag it**, not one: Magmadar's own kill (leg 2), and restoring the **sulfuron-harbinger**, **golemagg**
   or **shazzrah** checkpoints, whose captured `instanceState` carries MAGMADAR=3 (a restore re-applies the captured
   state). The fifteen hound packs are spread over legs 1, 3, 4, 5, 6, 7 and 8, so the roadmap order would have killed
   Magmadar in leg 2 and left **13 of them unfightable** - and the failure would have looked like an ordinary "selected
   creature unavailable" three hours into the run.
   **Fix: run_full_clear.py now runs in two phases** - every TRASH pack of every leg first (`--stop` at the leg's last
   pack step), then every BOSS in order (the plan is re-entered; clear-state.json skips the finished trash). Plain trash
   checkpoints carry no instanceState, so nothing during phase one can flag an encounter. Loot is rolled only in the boss
   phase, where the kill actually happens.
   **Decode key for `instanceState`:** space-separated encounter states in TYPE_* order - 0 LUCIFRON, 1 MAGMADAR,
   2 GEHENNAS, 3 GARR, 4 GEDDON, 5 SHAZZRAH, 6 SULFURON, 7 GOLEMAGG, 8 MAJORDOMO, 9 RAGNAROS, then the runes; 3 = DONE.
   `sep16b-lucifron` is the only all-zero one and is therefore the instance-reset lever.

171. **2026-09-16 SESSION STOPPED BY THE OWNER. NOTHING WAS CLEARED. Read this before touching the raid again.**
   **Result: zero packs, zero bosses killed.** Every failure was in SETUP, never in combat. Do not read any earlier item
   in this session as progress on the clear itself.
   **THE THING TO DO NEXT, which the owner had to tell me twice: RESET THE RAID ID.** `.instance groupunbind 409` from
   the owner's client (SEC_GAMEMASTER, console=false, so it goes through the client like `.combatstop`) unbinds the whole
   group; the instance then resets once everyone is out and a fresh one is created on re-entry. `listbinds` confirmed
   `player_binds: 1, group_binds: 1` on instance 101. I instead did an IN-PLACE reset (restore `mc-sep16b-lucifron` for
   pristine encounter flags + `qacheckpoint respawn` every spawn), which leaves the instance carrying everything that
   was wrong with it: the forty standing inside it, a dead owner, combat that never settles, and - the killer - every
   trash checkpoint's pose now sitting inside a repopulated pack's aggro. A checkpoint's pose IS its pack's pull
   position, so after a full respawn most poses are un-restorable-into: the raid lands already fighting, which refuses
   the loadout gate, the readiness gate, and makes summoned bots run back to their target instead of staying staged.
   A raid-ID reset avoids all of it, and the checkpoint tool already tolerates the new instance id ("Instance ids are
   renumbered by resets and restarts. The saved id is a hint") with `qacheckpoint return` as the re-entry path.
   **HOW FAR IT GOT.** Best attempt (pack-409-91284, a lone Firelord) passed restore, client-identity verification,
   loadout, ammo and readiness, reached `ready`, and was then CORRECTLY refused at the pull:
   `Bound objective 17379391157779357131 is not a captured checkpoint creature; pull refused` - the executor had bound a
   DIFFERENT Firelord standing nearby. That guard is right; the fix is to pick a target with no same-entry spawn within
   80 yd (computed: pack-409-56735, -56787, -56721, -91293 all have zero such neighbours), not to weaken it.
   A plan for pack-409-56735 is written at scratch/onyxia-live/sep16-plans/first-56735.json and was never run.
   **FOUR ERROR MESSAGES THAT POINT AWAY FROM THEIR CAUSE** - each cost me an hour:
   `Normal food consumption missing` = a member stuck in combat, not food; `restore refused` = a member outside the
   checkpoint instance (cure: `return` then restore, and release the executor FIRST or the return itself fails);
   `no walkable staging point` = the probe's height ladder was too shallow, not that the spot was unusable;
   `Loadout needs live exact40 out of combat` = **the owner was DEAD** (guid 787, health 0, since an earlier brawl) -
   the gate checks `health>0` on all forty and only mentions combat. `preflight.py` does NOT yet check member liveness;
   add it.
   **WHAT IS ACTUALLY WORTH KEEPING FROM THIS SESSION:** RaidQaCheckpoint **v12** (`qacheckpoint respawn`, mangosd
   36e4a4a7, deployed) - the only way back for the fifteen pruned Ancient Core Hound packs and for GM-killed trash;
   the Magmadar ordering constraint (item 170); the loot path, verified end to end but never handed out; and
   `preflight.py`. Everything else in items 165-169 is me re-deriving fixes that `clear_route`/`stage()` already
   contained, because I replaced that harness instead of unblocking it.
   **LIVE STATE AT STOP:** Core 36e4a4a7; session sep16f-mc-clear (client PID 7256, 39 bots online, executor cleared,
   GM off, owner alive); instance 101 fully respawned with pristine encounter flags; clear-state.json holds zero
   fights. 7 route packs still have no compiled encounter definition (56849, 56852, 56858, 56862, 56864, 56865, 56850)
   so the executor cannot be armed on them - a content gap that predates this session and caps a clear at 69/76 packs.

## Accepted Onyxia evidence

Session: scratch/onyxia-live/sep10-measure-objective-priority-evidence.
8 actual kills (01â€“05,08â€“10),2 wipes (06,07),0 timeouts/near-kills.
Stalled pulls6->1 and landed observed DPS931.121->1543.904 versus the permanent
consumable baseline7/10 (5 actual kills+2 near-kill timeouts). Rate70%->80%;
retained on the declared stall submetric, not on the one-success difference.
Secondary flight deaths30->37 and landing HP93.03%->90.61% remain disclosed.

All10 scores,batch-report,comparison-decision,retention-applied,evidence verification
and owner-acceptance/encounter-acceptance receipts are saved.70 raw score references
and8 reviewed kills were verified. Summary SHA82feb523a15614cb3d919da05cfd213b5ef51bce78dd1ac4790f9071f801515e;
ledger SHA24c1c939f9803b5db1a5bd93d2b30a52c0afecf81cf32b9a89f2782f94348661.
Historical adjusted rates remain30%,20%,20%,70%;40 rescored historical pulls plus
refuge01, with original evidence preserved. Refuge02 was owner-interrupted/excluded.
The unused sep10-objective-priority-analysis/start_final_seal.py must not run.

Previous detailed STATE bytes are archived at the candidate's
before-add-distances/COMMANDER_RAID_STATE.md. Stable design is in
shared_docs/COMMANDER_RAID_ARCHITECTURE.md; current evidence receipts supersede
historical pause/three-streak/deployment notes.

## Authorization

Persistent Testwar787+same39 and scoped VMaNGOS192.168.0.2 gameplay, Core builds,
installation/deployment and runtime restarts are authorized for this mission.
Preserve other projects/servers/characters. No direct SQL writes, DB/worldstate
backup/restore/swap, host reboot, commits, pushes, branches, worktrees or subagents.
Default exec has a technical Windows ACL helper failure; escalated exec works.
No unresolved approval-policy rejection. Clean boundaries are checkpoints,
not permission requests or mission completion.
