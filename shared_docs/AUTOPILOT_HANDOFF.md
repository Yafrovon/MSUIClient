# Raid Autopilot — handoff for the next agent (written 2026-09-24 ~23:40 EDT; §5–§7 rewritten 2026-09-25 ~20:50 EDT by Claude)

You have no memory of this work. This page tells you what the mission is, what exists, how
to run it, what state the world is in right now, and what to do next. Read it top to bottom
before touching anything. It supersedes the autopilot parts of `COMMANDER_RAID_STATE.md`
(which has a short pointer here) — the Commander *executor* history in that file is older,
different work.

---

## 0. 2026-11-03 — MC as guild progression (read this first; §1–§9 are the September record)

**Owner reframe (Nico, 2026-11-03):** stop aiming for zero hard-coding. MC is run like a guild's
progression: **week 1 = bosses 1–8, week 2+ = 1–9 (Majordomo); failing Ragnaros for 1–3 weeks is
fine.** Be involved: don't let scripts run unattended; when data is missing, build the harness first.
Decisions (asked and answered the same day):
- A "week" = a **fresh MC lockout + one raid night (~4 h)**, retries after wipes allowed.
- Boss-specific logic is allowed as a **per-boss data playbook keyed by creature entry** on top of the
  generic autopilot; code only adds reusable primitives. (Playbook = `sui-raid-playbook.json`, §0.4.)
- Gear = **loot from our own kills only** (the bots' existing Need/Greed group-loot rolls); no GM gear.
- Bot leader **Boomwarrior (115)** leads; trash runs on its own; the autopilot **pauses after every
  boss fight and every wipe** for review (`.autopilot resume Boomwarrior` continues).

### 0.1 The harness (built 2026-11-03)
- **Fight record**: every fight appends one JSON line to `~/vmangos/run/etc/sui-autopilot-fights.jsonl`
  (`SuiRaidTelemetry::FlushFight`, sampled once a second by `SuiRaidTelemetry::Sample`): outcome
  (win / wipe / boss-alive), every foe (entry, spawn guid, rank, max hp, final hp, **lowest hp** — a
  wipe's evade-to-full hides it otherwise —, died-at, first-at, unprovoked, seconds on a tank vs not,
  victim swaps, boss hp trace every 5 s), every death (role, killer, last hit, healing in last 8 s),
  every member's living/moving/casting seconds, damage/healing/taken, alive-count trace, top sources.
  The record closes when the raid is out of combat **even after the leader died** (the dead leader's
  tick keeps sampling; `NoteLeaderDeath` no longer flushes).
- **Review stops**: `.autopilot review <leader> on|off` (default on) — pause after any fight with a
  world-boss-rank foe or a wipe (leader died or half the raid dead).
- **`[TELEMETRY] WOKE`** line per spawned creature joining a fight: where it stood, how far from home,
  who struck first, its victim and the nearest member (this is how every overpull below was found).
- **`[AUTOPILOT] no plan for X: refused ...`** histogram when the pull planner finds no spot, and
  pull notes now say the camp, its distance, planned/unplanned, and the nearest other idle hostile.
- Local tools (git-ignored, `scratch/autopilot/`): `fights.py` (`-n 3` full review blocks, `--night`
  timeline), `waitfight.sh N MAXSEC` (block until N fights end or a review pause; prints the notes),
  `restart.sh [leader]` (install build mangosd, restart, ConnectBot the 39, verify one instance, enable).
- **`.autopilot newlockout <leader> <map>`**: what the weekly reset does, for this group only (every
  member outside first; drops member + group binds, keeps the group, clears that map's deferrals).
  Week-start recipe: `tele-all.ps1 -Where TheMoltenSpan` → `newlockout Boomwarrior 409` →
  `.tele name Boomwarrior mc` → `tele-all.ps1 -Where mc` → `.saveall` + check `characters.instance`.

### 0.2 Core fixes found by the harness (2026-11-03, all on the box, uncommitted; backups /tmp/bak-r18)
1. **Cleared-spawn file vs instance renumbering**: vmangos renumbers instance ids at every startup
   (`PackInstances`); a fresh instance became 100 and inherited September's instance-100 clears, so
   the autopilot GM-"executed" 242 untouched spawns (that run was voided). Now `PackInstances` calls
   `SuiAutopilot::RemapInstances` (clears follow their instance; gone instances dropped). The Sept file
   is archived as `sui-autopilot-cleared.txt.bak-0925`, the voided run's records as `...fights.jsonl.invalid-1103`.
2. **Multipliers** (Lava Spawn splits every 10 s in combat): learned when a summon's summoner is its own
   kind (`AiBotAI::ObserveMultiplier`, `sui-autopilot-multipliers.txt`), kill rank above everything,
   all DPS on the oldest. Before: 172k damage over 122 spawns, 0 killed; after: each dead 4–8 s after it appeared.
3. **Rebounder false positives**: a creature evading home after a wipe (and a splitting spawn) was
   learned as "heals itself whole → kill last" — Lava Spawn, Onyxia, Shazzrah, Flameguard, Lava
   Elemental, Flamewaker Priest/Healer were all in the file. Evading/victimless/multiplier kinds are
   now ignored; file pruned to Core Rager 11672 (backup `.bak-1103`).
4. **Burst-pack check counted neighbours**: two Molten Giants 20 yd from an Annihilator made it "a pack
   bursting every 4 s" → fought at the pull spot beside them. Now only the target's creature group and
   summons count (`SamePack`).
5. **Pull planner**: refusal histogram, a relaxed second pass, and a **corridor plan** (pull spot +
   camp on the pack's own path back to the last clear spot) when no ring direction fits — MC's
   passages refuse ~30 of 48 ring spots as off-mesh/lava.
6. **Unplanned approach** stops at the first idle hostile within 30 yd, which becomes the pull (it
   used to walk at one Flame Imp through the others at 1 yd).
7. **Patrols**: a waypoint route that never leaves 35 yd of its spawn (Garr) counts as standing
   (`Patrols()`); ambush wait capped 120 s; a timed-out ambush pulls the patrol where it walks if
   nothing idle is within 40 yd; a crowded ambush (route < 15 / camp < 20 yd) clears the crowder
   first unless the boss itself patrols past that crowder; "boss goes first" no longer hops to a pack
   the boss walks past.
8. **One crowd control per caster**: warlocks re-banished a new Firesworn every 2–3 s, each cast
   freeing the last (Garr). A caster holding its own Banish/Polymorph now casts no new one.

### 0.3 Week 1, night 1 (instance 100 after renumbering, 16:39–20:10)
55 fights, 40 min in combat, 10 wipes. **Bosses: Gehennas killed (17:52, 83 s, 0 deaths, loot
rolled).** Trash after the first fixes (16:55 on): 45 fights, 2 wipes (a crowded Firelord pull, the
imp room — both fixed). Boss wipes: Garr ×4 (one caused by my restart while the raid stood on his
path), Baron Geddon ×1 and Lucifron ×1 (both walked/patrolled into, not real attempts).
**Not reached this night: Magmadar, Shazzrah, Sulfuron, Golemagg, Majordomo.** Autopilot is PAUSED
after the last Garr wipe; server running the latest build.

### 0.4 Night 2 (2026-11-03 21:45 → 11-04 01:10, same lockout, instance 100)
**Bosses killed this lockout: Gehennas (night 1), Lucifron 23:04, Magmadar 00:10, Garr 00:23 = 4/8.**
Still alive: Baron Geddon, Shazzrah, Golemagg, Sulfuron (then Majordomo, Ragnaros). Week 1 target
(1–8 in one night) is therefore **missed**; the lockout stays open for the next session. Honest notes:
Garr's 8 Firesworn died across the earlier attempts and stayed dead (normal lockout rules), so the kill
was Garr alone (78 s, 0 dead); Magmadar died with 5 of 39 standing (real kill, loot rolled). Geddon:
6 wipes, all of them his patrol walking into a pack fight or the raid walking into a pack on his walk.

New harness fields (all in the fight record and `fights.py`): seconds each foe spent banished/sheeped
(`cc`), why each member repositioned (`repos`: cone/clear/spread/healrange/hazard/bomb/sight/breath),
damage on a riser held at its floor (`riserLow`), why a world boss turned from its target (`swapWhy`
+ `[TELEMETRY] SWAP` lines: feared/dead/charmed/stunned/threat), and self/ground damage (lava, falling,
and spells cast by game objects — those reach `DealDamage` as the victim hitting itself, which is why
Magmadar deaths read "killed by himself, no last hit").

Fixes, all on the box, uncommitted (generic unless marked DATA):
1. **Playbook (DATA)**: new `SuiWorld/CRPG/SuiRaidPlaybook.{h,cpp}` + `src/mangosd/sui-raid-playbook.json.dist`
   (installed beside mangosd.conf; a live `sui-raid-playbook.json` overrides; re-read on change). First
   content: **MC boss order** Lucifron, Magmadar, Gehennas, Garr, Geddon, Shazzrah, Golemagg, Sulfuron,
   Majordomo, Ragnaros. A boss and its pack are not pullable while an earlier boss is alive; exploration
   skips their rooms; target picks and exploration head for the first living boss of the order; a
   rune is never walked to while its boss lives. Why: the Ancient Core Hounds around Garr respawn every
   18 min until Magmadar dies (instance script removes all Core Hounds/Ancient Core Hounds on Magmadar DONE).
2. **Ambush shot** fires with nothing idle 18 yd from the patrol (was 32: creatures call for help 10 yd);
   a big creature is held at its pull spot only with nothing idle within 45 yd (was 30).
3. **Room trash only skipped** (abandoned ambush) still blocks its boss; any walk stops at an idle pack of
   another pull within 32 yd (it becomes the pull; never back to the one it left; never a boss's pack —
   then the target waits); "in the way" sees skipped packs; far "no path" picks are not skipped.
4. **Timed pulls around a patrolling boss**: if his clearest walk spot is < 22 yd from other packs (or his
   ambush plan is crowded), the packs on his walk go first, each pulled only while he is ≥ 75 yd away
   AND walking away (or ≥ 170 yd); the raid backs off when he comes within 60 yd on the walk in. A
   crowding pack on his own walk is cleared first the same way; a crowded ambush is never walked into.
5. **Risers (Core Hound)**: every hound down to 18 % first (only hounds above the floor are hit, never
   one lying down), then one burst, each bot its share — packs went from 261 s to 38–52 s.
6. **Death bursts** learned separately from pulses (`sui-autopilot-pulses.txt.bursts`, Firesworn 12099
   15 yd): below 20 % such a creature is a hazard circle for everyone but its holder. The Firesworn had
   been learned as a 15 yd *pulse*; risers are no longer learned as rebounders (Core Hound was re-added).
7. Warlocks never banish what ≥ 3 members are hitting or anything under 80 % (the raid's kill target).

### 0.5 Night 3 (2026-11-04 17:47 → 21:25, same lockout, instance 100) — see §0.6 for 11-05
**Bosses killed: Baron Geddon 20:33 (169 s, 11 dead), Shazzrah 20:52 (149 s, 15 dead) = 6/8 this lockout.**
Alive: Golemagg, Sulfuron (then Majordomo, Ragnaros). Both kills are normal combat; between attempts
the raid was revived and teleported to the entrance (setup, `recover.sh`). ~20 wipes on the way.

What Geddon really is (the old §0.5 was wrong): **he always runs** (`flags_extra` 0x40,
CREATURE_FLAG_EXTRA_ALWAYS_RUN, 16 yd/s): his ~470 yd loop takes **~30 s**, not 3 min. No timing keeps
him away for long; a fight within ~45 yd of his walk is met every loop. Detection is plain proximity
(20 yd + level); he was never "called". Firewalkers call `SetInCombatWithZone` on aggro, so one tag
puts the whole raid in combat at once.

Fixes, all on the box, uncommitted (generic unless marked DATA):
1. **Boss walk is a wall**: `RouteGap`/`PatrolBossWalkNear` (route sampled every 5 yd); safe spots are
   recorded ≥ 53 yd off a patrolling boss's walk; Idle and `MoveToSafe` leave a boss's walk first; a
   pull next to such a walk is always timed around him and camps ≥ 50 yd off it (`kBossWalkGap`).
2. **Dodges never step onto a boss's walk**: `CollectIdleHostiles` adds the walk of every patrolling
   world boss within 320 yd (not just within 60 yd — the walk mattered while he was at the far end),
   sampled every 5 yd, r 26; idle elites count 28 yd (was 25).
3. **Timed pulls**: away from pack, camp AND leader, two ticks running (the nearest-of-three flipped);
   the leader *runs* out; out of the camp the pull is committed — if the boss comes at the pull spot he
   runs back to the camp, never stands still beside the walk.
4. **The fight stays at the camp**: tanks hold the camp on timed/lured pulls (`HoldForPull`), the raid a
   rear spot 14–16 yd beside it (`RearSpot`, out of the cones); `CampHold` keeps members (melee chase
   `ChaseInPlace`, ranged `RangedChaseInPlace`, running chases in AiBotAIMain) at the camp while the
   leader runs the pack back and for the first 20 s while their target has not come to the camp (a
   Firewalker roots itself casting blossoms 30 yd out).
5. **Boss ambush clearance** for world bosses: camp ≥ 40 yd and ambush point ≥ 28 yd from idle packs.
6. **Lure (DATA + primitive)**: `bosses.<entry>.lure.at` in the playbook — the leader waits beside a
   patrolling boss's walk for it to notice him; camp and rear are searched around it (same level, in
   sight, walkable for the boss — a hand-picked camp on a rock made Geddon evade). Used only when the
   lure is ≥ 45 yd from idle packs (`LureClearance`), else those packs go first, timed. Geddon: lure
   (680, -806, -209). In the end Geddon died to a plain "passing" pull once packs B/D/A were dead.
7. Ranged keep 5 yd outside a known cone's reach (cap 29 yd); `Note` lines carry the leader's position.
8. **Harness**: fight record per member `manaStart` / `oom` (s under 10 %), shown by `fights.py`.
New ops scripts (git-ignored, `scratch/autopilot/`): `waitpause.sh HH:MM:SS` (wait for the next pause
and print the notes), `recover.sh` (revive + teleport every member not alive at the entrance),
`build.sh <scratch core> <files>` (upload + build, **exit 1 on a compile error** — chaining a restart
after a failed build silently redeployed the old binary once).

### 0.6 Session 4 (2026-11-05 08:29 → 09:57, same lockout, instance 100) — WEEK 1 DONE
**Bosses killed: Golemagg 09:39 (103 s, 0 dead), Sulfuron Harbinger 09:53 (239 s, 8 dead; the record says
"wipe" only because the leader died at 77 s — 31 alive, all four priests dead) = 8/8 this lockout.**
Golemagg's first attempt reached 13 % (199 s, healers dry at ~130 s). Firewalker packs wiped the raid 5×
before the fixes below; after them every pack died. Autopilot OFF (installed with `restart.sh Boomwarrior
--no-enable`), all 39 alive at the entrance, binary = build (md5 abbf4069…).

What the records showed (all generic fixes, on the box, uncommitted, backups `*.bak-1105a`):
1. **Tanks ran from their own mob's aura**: a Flameguard's Fire Shield (19626 → 19627, 250 every 2 s, 10 yd)
   was a learned "pulse", so its tank stepped "out of the fire" every 2 s, the Flameguard followed and swung
   Cone of Fire through the healers (15–26 target swaps per wipe; in every win it had 0). Now a trash
   creature's pulse whose hit (biggest seen, or the listed damage of the spell its aura fires) is under a
   quarter of the member's health is not a hazard (`LightPulse` in AiBotAICombat.cpp); world bosses always
   count (Geddon's Inferno is scripted and ramps). Tank hazard moves went from ~60/fight to 0.
2. **Tank steps into the next pack**: the tank separation/facing steps (`TankStepOk`) now refuse a spot
   within an idle creature's reach (`NearIdleHostile`); a separation step had put a Flameguard's tank 20 yd
   from the next pack's Lava Reaver (all 39 dead).
3. **Area spells on rebounders**: `CanUseSpecAoE` neither counts nor blocks on a learned rebounder — at
   Golemagg the two Core Ragers made "three targets" and mages/warlocks channelled Blizzard/Rain of Fire
   ~80 s of 199 s. Ranged dps on Golemagg 2,344 → 5,206, raid 4,529 → 8,337.
4. **Raid priests don't nuke**: holy/disc priests cast no Smite/Holy Fire/self-Power Infusion in a raid group
   (they cast 8–20 each per fight while the healers ran dry).
5. Review pause: a world boss already dead when first noted (the last boss's corpse) no longer makes a
   trash fight a "boss killed" pause (installed with the final restart, not yet seen live).
6. Harness: `[TELEMETRY] fight N heals <member>: <spell rank> xN total` (which ranks landed); `[TELEMETRY]
   AGGRO` once a second for a spawn in combat that has not joined the fight yet — where it is, its victim,
   and the member or pet nearest its home (a Lava Reaver woke 32 yd from every held mob and no logged step
   explained it; the probe has not fired on a mystery yet).
7. `waitpause.sh` compares date + time (a bare HH:MM:SS means today); the old version matched yesterday's
   pauses at once.

Findings that were NOT the problem (don't re-investigate): priest heal-rank choice is sane (Greater Heal
2–5, Flash Heal 7); per heal, priests and paladins landed similar amounts; Fire Blossom (19637) is a
single-target 2.8–3.2k nuke on a random member every second ×6, not an area.

### 0.8 Week 2, night 1 (2026-11-05 14:41 → 18:55, fresh lockout; instance 101, renumbered to 100 at the first restart)
**Killed (normal combat): Lucifron 16:24 (110 s, 0 dead — he walked into the raid on his patrol), Magmadar 17:58 (25 of
39 standing; 826,205 damage = his max hp, the hp trace has a sampling gap), Gehennas 18:03 (86 s, 0 dead), Garr 18:22
(262 s, 9 dead; three Firesworn had died in an earlier wipe and stayed dead) = 4/9.** Baron Geddon wiped at 13 % (360 s):
Inferno 172k, Ignite Mana 70k (paladins cleansed ~70 times, priests rarely — Dispel Magic sits after the heals in their
rotation), Living Bomb 67k; every healer was out of mana for 2–3 min. 74 fights, 11 wipes, 72 min in combat. Stopped at
the 4 h night budget; autopilot OFF, 39 alive at the entrance, installed binary = build (md5 34f09882…).

Fixes (generic, Core, uncommitted; backups `*.bak-1105b`), each from a wipe's record:
1. **Call for help**: a neighbour of another pack inside the target's 10 yd assistance radius (in sight) holds the pull
   shot until it wanders off (60 s cap); one that can never wander out (home gap + both wander radii ≤ 10 yd) counts
   as the same pack (`AssistNeighbour`, `SamePack`). A Firelord wandering 8–18 yd from an Annihilator had come with it.
2. **Trash patrol crowding a pull is killed first** (its own ambush) instead of waited out for 120 s; a Lava Surger
   walked into the imp pull after the wait.
3. **Pull spot outside every standing creature's aggro reach** (`AggroReach` = attack distance by level + both
   bounding radii, +3 yd; standing targets only — around a walker it refused 40 of 48 spots and broke Lucifron's
   ambush plan). A Lava Annihilator 27 yd from the pull spot had woken.
4. **Long patrols count by their route** (`ForeignIdle` looks 320 yd for walkers whose route enters the 110 yd plan
   area): an Ancient Core Hound 285 yd from its spawn walked through a camp.
5. **Respawns executed during fights too** (Flame Imps respawn every 5 min; a cleared pack came back beside a fight).
6. **Cone dodge**: the back arc is closed only for dragon-sized bodies (combat reach ≥ 15). At Magmadar the front+back
   rule left 40° wedges; healers dodged every 3.5 s (731 moves). After: ~170 moves, one target swap, kill.
7. **A walking target is waited for at the pull spot, never chased** (90 s, then skipped 5 min); the leader had followed
   an Ancient Core Hound 65 yd into a Molten Destroyer pack.
8. **On the way to the next boss = by the walk**: a pick must be a detour of ≤ 40 yd on the path to the boss goal
   (straight line let the raid drift 580 yd west into Garr's room, then Geddon's).
9. **Walking packs count by their loop in `IdleNeighbours`** (group leader's route, +10 yd for members): a hound held
   "where it stands" because nothing idle was within 45 yd; Garr's loop brought a Firesworn back — whole pack joined.
10. **Tanks never hold at the camp while the pack is on the leader within 25 yd of it** (`CampHold`, `HoldForPull`):
   Fire Blossom rooted the leader 13 yd short of a timed-pull camp and he tanked a Firewalker pack alone (dead at 12 s).
   After: both Firewalker packs died (7 and 6 dead).

### 0.9 Week 2, night 2 (2026-11-06 10:24 → 13:25, + Majordomo work 15:04 → 16:17; same lockout, instance 100) — 8/9
**Killed (normal combat): Baron Geddon 10:53 (148 s, 13 dead — 11 of them to Armageddon, his dying explosion), Shazzrah
11:24 (142 s, 16 dead, Arcane Explosion 268k), Golemagg 11:45 (114 s, 1 dead), Sulfuron Harbinger 12:03 (150 s, 0 dead)
= 8/9 this lockout.** Majordomo Executus: 9 attempts, all wipes (see below). Trash between bosses: no wipe after the
Shazzrah-room fix. Autopilot OFF, 39 alive at the entrance, installed binary = build. Backups `*.bak-1106*`.

Fixes (Core, uncommitted), each from a record:
1. **The timed-pull retreat turns round** (`SuiAutopilot.cpp`): "back to the camp before he arrives" only issued the
   move when the leader stood still, so the walk out continued 14 yd to the pull spot and Geddon took him there (the
   week-2 night-1 wipe). Now the move is re-issued unless the current spline already ends at the camp. (The retreat
   alone is still too slow against Geddon, 16 yd/s; he was finally pulled from the playbook lure, planned.)
2. **Area reach = radius + both bodies' sizes** (`CollectGroundHazards`, melee pulse keep-out, healer `pulseKeepOut`):
   the server's area search measures edge to edge; with a 1.5 yd margin three healers stepped "out of the fire" to
   21–22 yd of Geddon's centre and died to Inferno's last 5000 tick. Inferno damage 172k → 68k.
3. **Healer heal-range walk refuses a path through fire** (not only its end): two paladins walked through Inferno to
   the tank on the far side.
4. **A wanderer's keep-out is its whole wander circle** (`CollectIdleHostiles`: spawn point, r + wander distance): a
   mage's cone step kept 28 yd off Shazzrah, who wandered 20 yd toward him and joined a Lava Reaver pack (all dead).
5. **Rebound learning ignores rebounds under a creature healer** (`ObserveRebound` + `KindHeals`): Sulfuron went
   30 → 91 % under his priests' Dark Mending and was learned as a "rebounder"; the Flamewaker Priest (11662) had been
   learned the same way in week 1 — rebounders are skipped by the kill order, so nobody focused the priests (wipe).
   11662 and 12098 removed from `run/etc/sui-autopilot-rebounders.txt` (backup `.bak-1106`). Next attempt: 0 dead.
6. **Crowd-controlled mobs die after every awake one** (`KillPriority` rank 9): bots halt any attack on a CC'd
   victim (`UpdateSpecCombat`), so a kill order pointing at a sheep left the damage dealers idle.
7. **Mages sheep a healer kind first; tanks take an elite that hits before a healer kind**; a tank never picks a
   CC'd mob; mages never sheep what ≥ 3 members are hitting; a mage renews its own sheep in its last 5.5 s (cast
   straight — the spell gate refuses a target already wearing the aura; no renewal logged yet, uptime rose anyway).
8. **Playbook `killOrder`** (DATA, `bosses.<entry>.killOrder` = add entries, first dies first, used while that boss is
   in the fight) + **release target** (`AiBotAI::PlaybookReleaseTarget`): a sheeped add of an earlier kind than every
   awake one may be woken and killed (lowest guid first) while the rest stay held; its mage does not renew it.

**Majordomo (12018), what the records show:** all nine engage together. Flamewaker Elites (11664) are immune to
Polymorph; Flamewaker Healers (11663) have no heal (Shadow Bolt + Shadow Shock, an instant 20 yd burst) and turn
immune to Polymorph when half the adds are dead (Immunity 21087, mechanic 17 — it breaks standing sheeps). Majordomo
teleports his tank into the lava pit out of every healer's sight (the leader died twice with 0 healing in 8 s).
Four tanks for five tank targets: something is always loose. Best try = Elites first (`killOrder [11664, 11663]`):
all four Elites dead by 108 s with 38 alive, then the four Healers woke together and killed 34 in 35 s. Healers first
(released one at a time) left the first Healer awake and untanked from the pull (85k into it, wipe at 93 s). Attempt 9
(Elites first again): all nine landed on the leader at the pull spot (714, -1170, z -119) and he died at 5 s — the
healers stood 18–20 yd back at z -121..-123 below the platform step and 8 of 10 had no line of sight to him. The
September Commander executor won this fight 8/10 with FIVE tanks (Testwar + four bots, state page item 160).

**Afternoon (15:04 → 16:17, owner: "keep 4 tanks, fix AI first"), attempts 10–17, all wipes.** More fixes (Core):
9. **Line of sight for a spot = the game's own test** (`SeesFrom` in AiBotAICombat.cpp: from the spot at my eye height
   to the other's chest, `IsWithinLOSAtPosition(..., 1.0f)`). The heal-range, spread, ranged-sight, cone-flank and
   tank-step checks used `other->IsWithinLOS(x, y, z + 1.5f)`, which adds 2 yd itself — a point 3.5 yd up — and passed
   spots below Majordomo's platform step; healers "closed in" again and again without sight.
10. **Burst keep-out in ranged spacing measured edge to edge once** (it subtracted both sizes from a distance that
   already was edge to edge — same law as fix 2).
11. **An elite on me → run to the nearest tank** (3 yd past it), not straight away (`[AIBOT-HAZARD] X is on me - to
   tank`, reposition "totank"). Majordomo resets threat on every teleport (`DoResetThreat`, every 20–30 s) and went for
   healers, who stepped away from the tanks; Taunt here is melee range.
12. **Playbook kill order beats the leader's target** (`anchorRank 99` when the fight's boss orders its adds): the tank
   leader swaps between boss and adds — 103 focus changes in 96 s, damage spread over four Elites, none died.
13. **A wounded tank (< 75 %) that no healer sees pulls its nearest healer into sight** (heal-range block): an add
   tank died with 0 healing in 8 s and 9 of 10 healers out of sight, all on the main tank.
14. **No magic into a reflecting enemy** (`TrySpecSpell`: `SPELL_AURA_REFLECT_SPELLS(_SCHOOL)` and DmgClass magic →
   refused, telemetry -4); Majordomo's Magic Reflection (aura 28, his adds within 12 yd, 10 s of every 30; first at
   30 s). Sheep renewal starts 12 s ahead and waits out a reflection. Renewal now logs (`renews the polymorph`).
15. **Sheep claims** (2.5 s, first mage to start a Polymorph owns the target) and "don't sheep the raid's kill target"
   only once it is below 90 %: at the pull every damage dealer's first pick counted as "hitting" and blocked the sheeps
   for 14 s; mages deciding in one tick put two sheeps on one Healer and left another awake.

Results: Elites first (attempts 10–15) twice killed all four Elites (by 148 s after the pull; tanks alive past 190 s
in attempt 13), then the four Healers woke immune together — Shadow Shock 100–170k — and the raid died. Healers first
(attempts 16–17, release machinery) killed one Healer in 41–82 s but the released one runs loose and raid damage
fell to ~1.5–1.8k/s: worse. Playbook left at Elites first. Variance between identical attempts is large (0–4 Elites).

### 0.14 Majordomo, Healers first with a working release (2026-11-08 07:34 → 09:35, same lockout, instance 100) — still 8/9
Owner (11-08 morning): **"Yes, Healers first"**. Five tanks (Bubbleguy still Protection, revision 1). 12 attempts
(15–26), all wipes; best = **five of the eight adds dead** (attempt 22). Stopped 09:35: autopilot OFF, 39 alive and
online in MC instance 100, Testwar offline, installed = running binary **ac827d21**, playbook Majordomo
`killOrder [11663, 11664]` (Healers first).

**The plan that now runs.** With five tanks, four Elites + Majordomo + a released Healer are six targets, so pure
Healers first always leaves one untanked (attempt 14). The release now waits for a free tank, and the order plays as:
Elite 1 dies (frees a tank) → Healer 1 released, that tank wakes it → Healer 2 → Healer 3 → the 4th death leaves one
Polymorph-immune Healer and three tanked Elites. Every log line: `[AIBOT-RELEASE] X (guid) released|stays sheeped: N
tanks for M awake`.

Fixes (Core, uncommitted; backups `/tmp/bak-1107a/*.pre-1108a … *.pre-1108l` on the box). Letters = 11-08 patch order:
- a. **Release only for a free tank** (`PlaybookReleaseTarget`): tanks in the group within 100 yd of the boss must
  outnumber the awake enemies round him (boss included).
- b. **A loose sheepable add is re-sheeped, not killed, while no tank is free** (`KillPriority` rank 9; not for one
  released on purpose, not for a Polymorph-immune one). Ranged opened on the Healers at the pull and an Auto Shot in
  flight broke a fresh sheep 2 s in (attempt 15).
- c. **Consecration needs no sheep within 20 yd** (`ConsecrationNearSheep`): it lies 8 s; the cast-time check (area
  + 6 yd) missed a sheep that wandered in. Bubbleguy broke two at 16 s (attempt 16).
- d. **A crowd-control cast in flight counts as a sheep** for every splash check (`CrowdControlBeingCastNear`: Polymorph,
  Shackle, Hibernate, Sap, Gouge being cast on a unit). One Blizzard at 2 s broke two fresh sheeps (attempt 17).
- e. **The boss that orders its adds comes after all of them** (rank 10). Majordomo's own spell list ranked him 2,
  ahead of the Elites (4), and once every Healer slept 70 s of raid damage went into him (87 % → 50 %, attempt 18).
- f. **A tank step keeps a healer in sight** (`TankStepOk`: some healer within 38 yd with sight of the new spot). The
  separation of fix aa walked Shieldwall down Majordomo's terrace 1–3 yd at a time until no healer saw him (attempt 19).
- g. **The free tank wakes the released sheep itself** (`TankDuty`, claimed 4 s), and damage dealers leave a released
  sheep alone for its first 6 s (`PlaybookReleaseAgeMs`). Before, a rogue broke it, the Healer turned on the melee
  60 % of its life and needed 41 s to die (attempt 20). Now its first enemy is a tank (Ironwarden, 1–4 s after release).
- h. **Ranged spacing gives up a futile step** (`[AIBOT-SPACING] X: two clear steps ... holds and casts 8 s`): a mage
  re-picked one unreachable spot 13 times in 26 s, dropping each cast (1,390 re-grounds in attempt 21). Fires rarely.
- i. **Melee damage dealers wait out an elite's Damage Shield** (`[AIBOT-MELEE] X: waits out ...`, aura type 15;
  Majordomo's adds get 21075 for 10 s every 30 s, ~110 back per hit). Five rogues died within 4 s to it plus Shadow
  Shock (attempt 21).
- j. **A later kind is not finished below 30 % while an earlier one is released** (`KillPriority`): the second Elite,
  low from splash, died while the second Healer was killed, so the 4th death woke two immune Healers (attempt 22).
- k. **A wounded tank out of heal RANGE counts as unseen** (healer rescue, now up to 80 yd): two add tanks 50–60 yd
  from all nine healers, in plain sight, died in the same second with nothing healed in 8 s (attempt 23). Works, but
  fires below 75 % health — 5 s too late for a tank 74 yd out (attempt 24).
- l. **Healers are dealt out over the tanks** (`ActiveTank` for a healer: tanks holding an elite or boss, by guid,
  healer i → tank i mod n) instead of all following the member with the most attackers. Attempt 25: tanks lasted to
  133 s (before: 51–65 s).

Records (attempt — what happened; E = Elite, H = Healer, times after the pull):
15 — gate held, but a hunter's Auto Shot broke a sheep at 2 s, the loose H killed two melee; tanks died 5 → 3 as fast as
Elites did, so no release ever opened; E 42/82/107 s + that H = 4 deaths → three immune Healers → wipe 166 s.
16 — Blizzard at 4 s and Consecration at 16 s broke three sheeps → 0 adds, 124 s. 17 — one Blizzard broke two sheeps at
2 s; rogues' Gouge/Kick on a sheep → 116 s. 18 — sheeps held 100–115 s, but the damage went into Majordomo → 0 adds.
**19 — first clean run of the plan: E 27, H 51 (released at 27), H released at 51 → 102, E 119; then tanks out of healer
sight died.** 20 — E 22, H 63 (41 s, untanked 60 %), E 100. 21 — the tank woke the release itself, but five rogues and
Ironwarden died in 4 s (Damage Shield + Shadow Shock), ranged cast 13 %; the H never died. **22 — best: E 19, H 53, E 75,
H 88, H 136 = five of eight; the 4th death (H at 88 s) came after the second E, so two Healers woke immune; wipe 199 s.**
23 — E 23, H 75; two tanks 50–78 yd from every healer died at 63 s, the gate closed, Elites died (98, 120) → three Healers
woke. 24 — E 22, H 41 (19 s after release); Shieldwall died at 51 s, healer rescue arrived 5 s late. 25 — E 39, H 80, H
144, E 173 (healer split; tanks lasted to 133 s), raid 2.5k dps, ranged cast 36 %. 26 — control, Elites first with all
fixes: Elites 46/77/131/165 s with 28 alive, then the wake-up wiped the raid (204 s).

What limits it now: (1) **tank deaths** at 50–130 s to Elite melee + Fireball/Fire Blast/Blast Wave; every tank death
closes the gate and the raid falls back to killing Elites, which breaks the death order; (2) **slow kills** — 19–64 s per
Healer (98k hp), raid 2.3–3.9k dps, ranged casting 36–48 % with 199–299 "clear" steps a fight (six awake burst circles);
(3) opening variance (attempts 16–17). Last night's 6,350 raid dps (attempt 3) was one short fight, not a regression.

### 0.13 Majordomo with FIVE tanks (2026-11-07 21:45 → 11-08 00:05, same lockout, instance 100) — still 8/9
Owner (11-07 evening): **"Allow a 5th tank"**. The raid has only four warriors, so **Bubbleguy (paladin, guid 125) was
respecced Holy → Protection** through the web app (`POST /Bots/CombatLoadout/125`, `{"expectedRevision":0,"specTab":1,
"activeRole":3,"resetTalents":true,"confirmReset":true}`; revision is now 1; the Protection rotation already existed).
Nine healers remain. To go back: the same call with `"expectedRevision":1,"specTab":0,"activeRole":4`. Every tank rule
picks tanks by ROLE, so nothing else needed changing. 14 Majordomo attempts, all wipes (one invalid). Stopped 00:05:
autopilot OFF, 39 alive and online in MC instance 100, Testwar offline, installed binary = build (md5 d2fa74fe…),
playbook Majordomo `killOrder [11664, 11663]` (Elites first).

Fixes (Core, uncommitted; backups `/tmp/bak-1107a/*.pre-q … *.pre-aa` on the box — the last `AiBotDoctrinePlayerParty.cpp`
edit (bb) has no own backup: `.pre-v` plus the p1107v change is the state before it). Letters = patch order:
- q. **A burster held by a tank is still sheeped** (`TrySpecPolymorphSpareElite`): the fifth tank picked up the fourth
  Healer, and a tank-held add scored 0 ("never sheep a tanked one"), so it stayed awake and Shadow Shocked from 12 s.
  A crowd-controllable burster the raid is not killing (hitters < 3) now counts as loose and ignores the elites-vs-tanks
  cap. Result: all four Healers sheeped within 3 s of the pull, sheep time ~180 s.
- r. **A sleeping burster's circle matters only once it may wake** (`SleepersMayWake` in `AiBotAICombat.cpp`: at most one
  awake non-boss enemy within reach and it under 35 %). It gates the ranged/healer keep-out and the melee hold of §0.12
  fix 4. With four sheeps round the tanks the ranged had nowhere to stand (458 "clear" steps, 737 dps, first Elite dead
  at 117 s); with the gate: **6350 raid dps, Elites dead at 20/50/65/83 s with 39 alive** (attempt 3).
- s. **Released adds are remembered 60 s** (`RecentlyReleased`): no mage sheeps the playbook's next kill again.
- t, v, bb. **The release target finally reaches the damage dealers** (`KillPriority`): a sheep has no victim (the
  candidate loop wanted a victim in the group), it may stand beyond the 45 yd scan, and `IsValidAssistTarget` →
  `IsValidHostileTarget` refuses ANY unit under breakable CC. All three skipped the released Healer, so **the release has
  never worked since it was built on 11-06** (every "Healers first" result before bb measures sheeps running out).
- u. **The release is judged round the boss** (90 yd around him, the same for every member): from a hunter's own 60 yd
  the awake fourth Healer looked absent and he shot a sheep "released" at 2 s. And **no sheep inside a group member's
  channelled area** (`InGroupChannelArea`): one Blizzard broke two fresh sheeps at its next tick.
- v. `TryCombatPotion` also takes trade-goods items (Demonic Rune 12662 / Dark Rune 20520: mana on their own cooldown) and
  skips one that hurts below 60 % health. The healer rune line in `run/etc/sui-raid-supply.json` was **not delivered**:
  the autopilot's raid prep casts buffs and food; the bag potions came from an earlier quartermaster order.
- w. **A free tank stands by a sleeping burster that may wake, one tank to each** (`[AIBOT-TANK] X: stands by the sleeping
  Y`), so it is taken the moment it wakes. Works (tanks took the woken Healers in < 1 s) but see attempt 11.
- x. **A mage walks into reach to renew its sheep** (own sheep looked for within 45 yd; out of 28 yd or sight with < 12 s
  left → walk in): a sheep 30–45 yd from its mage ran out unrenewed and woke 20 s before the last Elite died.
- y. Diagnostic `[AIBOT-POTION] X: mana N% and nothing drunk: <spell>:cooldown|castN` (every 10 s per bot while mana is
  short). Noisy: remove it once mana is understood.
- z. **The shared potion cooldown goes to mana** (`needShield`): a healer never drinks a protection potion in combat, any
  other mana user only at ≥ 60 % mana. Before, EVERY Major Mana Potion was refused on cooldown (the Greater Fire
  Protection top-ups had taken it) — nine healers dry by 100–120 s with ten potions each. After: healer time out of
  mana 30–66 s → 0–14 s.
- aa. **Two held bursters are taken out of each other's circle** (`MaintainTankFacing`: apart = max(12, pulse + 4); the
  "pulse kind, no cone → no turning" return now comes after the separation; ±45/90° alternatives; no step into lava).

Majordomo records (attempt — order — what happened): 1 — Elites — before q: three sheeps, the tank-held fourth Healer
killed from 12 s, Elites dead 62–185 s, wipe 203 s. 2 — Elites — q: sheeps hold, 0 deaths for 100 s, but 737 dps.
**3 — Elites — r: Elites 20/50/65/83 s, 39 alive at the wake-up; the four immune Healers then killed 39 in 36 s (no
tank on them).** 4 — Healers — first Healer 30 s, an Elite 43 s, the second Healer 80 s (it woke when its sheep ran out),
healers dry ~100 s, wipe 152 s. 5 — Healers — the 2 s "release" (u), Blizzard/Arcane Explosion broke sheeps, collapse.
6 — Healers — clean opening, Healers 25 s and 90 s, healers dry, wipe 157 s. 7 — Healers — all four sheeped at the pull,
melee splash broke three in 6 s, 13 dead by 88 s. 8 — Elites — Elites by 81 s, 38 alive; one sheep ran out (x), so the
staging gate never opened; the paladins at 1–3 % mana. 9 — Elites — Elites by 114 s; y showed every mana potion on
cooldown. **10 — Elites — z: Elites 19/36/62/78 s, 39 alive at 65 s; three tanks staged and took the woken Healers at
once, but they stood within 10 yd of each other, so each took all four Shadow Shocks (Boomwarrior 96 % → 29 % in 9 s);
wipe 127 s.** 11 — Elites — aa: Elites by 90 s; all four tanks staged; **the sheeps lie within 20 yd of each other, so
the Healers' first Shadow Shocks hit every staged tank (two dead in 2 s)**; Majordomo was loose early (killed three
healers at 41–73 s). 12 — invalid (resumed without a restart: the leader detoured and 11 were down before the pull).
13 — Healers — first Healer 19 s, an Elite 35 s, the second Healer 142 s → led to bb. 14 — Healers — bb: the awake
first Healer (the only one not sheeped) killed all eight melee with Shadow Shock in 12 s; collapse, no release seen.

### 0.12 Week 3, night 1 continued (2026-11-07 14:16 → ~17:50, same lockout, instance 100) — 8/9
Owner (11-07 afternoon): "Yes, continue" (fix Geddon's room order and go on in this lockout).
**Killed (normal combat): Baron Geddon 14:54 (134 s, 1 dead — met at the playbook lure, 3rd attempt), Shazzrah 15:13
(294 s, 18 dead; 7 of the 8 melee died to Arcane Explosion by 19 s; the leader died, so the record says wipe), Golemagg
15:35 (134 s, 6 dead), Sulfuron Harbinger 15:48 (178 s, 0 dead) = 8/9 this lockout.** Trash on the way: one Firewalker
pack cost 11 (pack killed). Majordomo Executus: 13 attempts 15:51–17:32, all wipes (below). Testwar stayed offline.
Stopped 17:35: autopilot OFF, 39 alive at the entrance, instance 100, installed binary = build (md5 47dc93f3…, the
reverted "untanked burster" rule is NOT in it).

Fixes (Core, uncommitted; backups `/tmp/bak-1107a/*.pre-h … *.pre-p` on the box), each from a record:
1. **A lured boss goes before its room trash** (`SuiAutopilot.cpp` `LuresFirst` in `PickNext`): with the playbook lure
   clear (`LureClearYards`), `RoomTrash` no longer sends the raid at the packs on Geddon's 30 s loop
   (`[AUTOPILOT] X's lure is clear - he is met there before his room's packs`). When the lure is crowded the nearest
   pack is named (`X's lure is N yd from Y (entry, spawn, motion) at (x, y)`).
2. **A lured boss is reachable when the walk to its LURE exists** (`LurePathOk`): the path to Geddon's running position
   failed 168 yd out, so "the boss goes first" was skipped, the Flameguard pack beside his walk was timed instead and he
   took the leader alone at the pull spot. With both fixes he died at the first planned lure pull.
3. **A caster finishes a crowd-control cast before any step** (`MaintainRangedSpacing`: Polymorph/Banish/Shackle/Sleep
   in the generic slot → no reposition; `[AIBOT-CC] X: holds still to finish Polymorph`): the same mage began Polymorph
   on one Healer three times in 2 s while it hit him. Sheep time per Healer went from 47–51 s to 106–216 s.
4. **Melee keep out of a SHEEPED burster's circle, and hold while their own target stands in it** (`heldBurstMelee`,
   also cutting a chase — the spacing check skipped every moving body, and chasing melee always move): attempt 8 killed
   all four Elites by 119 s and **nobody died at the wake-up** (attempts 1 and 4: all eight melee in 2 s).
5. **Kill-order ties compare health in tenths, then guid** (`KillPriority`): four Healers near full flipped "lowest %"
   every tick. Churn remains (49 Healer→Healer switches in attempt 10), so this is not the whole cause.
Tried and reverted: Healers first (data, attempt 2: one Healer died at 26 s, two awake beside the melee by 56 s, worse
again with the sheeps holding); "melee avoid ANY untanked burster" (attempts 11–12: Elites are bursters too and one is
usually loose with four tanks — melee damage 120–140/s, the Elite phase failed; kept as `AiBotAICombat.cpp.with-p`).

Majordomo records (fight #, Elites, outcome): 1 — all 4 by 108 s, 39 alive, then 8 melee dead 2 s after the 4th death
(wake-up beside them). 4 — all 4 by 86 s, the same (fix 4's chase hole). 8 — all 4 by 119 s, 0 deaths at the wake-up,
then **no Healer died in 100 s** (focus churn) → wipe at 218 s. 10 — all 4 by 134 s, four melee ran at the waking
Healers and died at 136 s. 3, 5, 6, 7, 9, 11, 12 — the opening failed: the main tank dead at 22–54 s, an Elite or two on
non-tanks (on-tank 0–26 %), Healers loose before the sheeps. About half the openings fail with four tanks for
Majordomo + four Elites.

### 0.11 Week 3, night 1 (2026-11-07 07:09 → 10:50, fresh lockout; instance 101, renumbered to 100 at the first restart) — 4/9
Week 2's lockout reset on its own at 2026-11-06 23:00 (the `[DungeonReset] ... ScheduleReset` lines at 22:00–22:59 are
the countdown); the 39 were sent to their binds in Elwynn and Dun Morogh and no MC save was left. Owner (11-07): start
week 3 now, **keep 4 tanks and fix the AI**, Testwar stays offline. Week start = the §0.1 recipe (`tele-all
TheMoltenSpan` → `newlockout` → leader in → `tele-all mc`). Next global MC reset: **2026-11-13 23:00**.
**Killed (normal combat): Lucifron 08:11 (110 s, 0 dead), Magmadar 08:23 (11 dead, 10 of them to Conflagration; the
leader died, so the record says wipe), Gehennas 08:41 (106 s, 0 dead), Garr 09:34 (2nd attempt; 4 Firesworn had died
in the 1st and stayed dead) = 4/9.** Baron Geddon: 6 wipes 09:47–10:44 (best: Geddon alone at 28 %, then he evaded to
94 % as the tanks died). Stopped at the 4 h budget: autopilot OFF, 39 alive at the entrance, instance 100, binary =
build (md5 2601b325…). Backups of every patched source: `/tmp/bak-1107a/` on the box.

Fixes (Core, uncommitted), each from a record:
1. **Splash never reaches a sheep** (`AiBotAISpecCombat.cpp`): from yesterday's 17 Majordomo records, 89 broken sheeps,
   68 of them splashes (Blade Flurry 23, Blizzard 21, Multi-Shot 9, Frost Nova 6, Rain of Fire 5). `CanUseSpecAoE` now
   looks for a breakable-CC enemy 6 yd past the area (`kSplashCcWander`: sheeps wander). A channelled area (Blizzard,
   Rain of Fire, Hurricane, Volley) is stopped, and Blade Flurry / Sweeping Strikes are dropped, when a sheep comes into
   them (`StopSplashNearCrowdControl`, `[AIBOT-CC] ... stops X` / `drops splash buff`). The halt on a sheeped victim also
   interrupts a wand / Auto Shot / damage cast at it (7 wand breaks). The old rotation path's Multi-Shot and Blade
   Flurry go through the same gate. **Not yet seen live**: no sheep fight came after it went in.
2. **Ranged/healers keep out of a SHEEPED burster's circle** (`MaintainRangedSpacing`): Majordomo attempt 15, the 4th
   add died at 148 s, all Healers woke immune, and at 149 s one Shadow Shock killed four casters standing beside the
   sheeps (the keep-out skipped CC'd mobs). Not yet seen live.
3. **Ranged stand outside a narrow burst circle** (radius + 1.2 ≤ 28 yd, e.g. Geddon's Inferno 20, the Healers' Shadow
   Shock 20); only a circle as wide as Shazzrah's 30 yd is still stood in. Geddon is ROOTED during Inferno
   (`boss_baron_geddon.cpp`, `UNIT_STATE_ROOT`), and ranged died to "Inferno 2000" at 14–20 yd.
4. **Unplanned approach always stops 30 yd from the nearest idle mob, the target included, sight or not**
   (`SuiAutopilot.cpp`): no sight into the imp pit until 0 yd, so the walk went on into 20 imps + a Surger, 39 dead in
   50 s. The Pull phase closes in for sight alone.
5. **A rear spot has a short navmesh walk to the camp and sees where the pack meets the leader** (8 yd from the camp
   toward the pull spot): a rear spot in a hollow 4 yd below a camp in Lucifron's room had no way out. Ten healers and
   eight melee stood 113 s without sight (healer HPS 132 in total), and the tanks died with 0 healing.
6. **Ambush shot: a pack's loop counts 12 yd (+10 member slack), live positions still 18 yd**
   (`IdleNeighbours(..., loopR)`): Garr's short loop, 26 yd from the Ancient Core Hound's ambush point, held the shot
   for 14 min of abandoned 120 s waits, and "cleared before Garr" re-picked the hound every time.
7. **The walk back to the clear spot must be clear too** (`MoveToSafe`: path within 25 yd of an idle hostile → stay,
   `the way to the clear spot passes X - staying here`): stepping off Geddon's walk, the leader walked into a Firelord;
   Geddon and a Firewalker pack joined.
8. **Lure clearance is playbook data** (`bosses.<entry>.lure.clearance`, default 45; Geddon 35, in both `.dist` files):
   the Firewalker pack 39 yd from Geddon's lure always went first, timed, and he ran into every such pull.
   **Not enough on its own**: the last wipe shows the Firewalker pack is picked first anyway as Geddon's ROOM TRASH
   (bosses-last), before the lure is ever considered.

### 0.10 Next steps (in order; updated 2026-11-08 09:35)
Same lockout (instance 100) until **2026-11-13 23:00**: only Majordomo Executus is alive (8/9). Five tanks, Healers
first with the free-tank release (§0.14).
1. **Tank survival in the Elite-held phase** — the main limiter: tanks die at 50–130 s, the gate closes and the death
   order breaks. Read the tank DEATH lines (`healers:` distances) of the next attempts with fix l (healer split) in;
   if tanks still die in range, look at heal targeting (healers casting long heals on others while a tank drops) and
   whether a healer's own tank gets priority; consider the rescue threshold (75 %) higher or distance-scaled (fix k).
2. **Kill speed of a released Healer** (19–64 s for 98k): ranged cast 36–48 %. Log which enemy each ranged "clear"
   step runs from (reason + entry) to see whether the 10 yd Elite circles or the 20 yd Healer circles fence them in;
   tanked Elites' circles may not need a ranged keep-out at 15+ yd.
3. **Death order** (fix j): confirm the 4th death is the third Healer when the tanks live; if a tank dies mid-cycle the
   raid falls back to Elites — an Elite whose death would be the 4th while two Healers still sleep might be held at
   30 % until a Healer dies (extend j beyond "a release is up").
4. **Majordomo's tank loses him**: Majordomo melee killed healers in several attempts (25: two priests at ~97 s).
5. Mana: `[AIBOT-POTION]` still in; healers ran out 0–50 s in attempt 25 — runes never handed out.
6. If the owner wants: a 6th tank (another Protection paladin) would make the release cycle independent of one death.
7. Shazzrah: 7 of 8 melee dead in 19 s to Arcane Explosion (30 yd, every ~6 s) — melee should not stand in it.
8. Magmadar's Conflagration (Lava Bomb GO 177704, trap radius 5, spell 19428: 400/s for 8 s, NOT dispellable) killed
   10 tonight and topped weeks 1–2: the burn continues after stepping out, so it is a healing problem (victims had
   healed8 0).
9. Healer mana and throughput: dry at Geddon (Ignite Mana, oom 85–109 s) and Magmadar. In the 476 s Geddon wipe the
   mages spent up to 200 s out of mana; Ignite Mana (19659, magic, 5 min) is valued by `DispelValue`, but no dispel is
   logged, so whether priests/paladins actually remove it is unverified.
10. Rogues die to Flame Imp Fire Nova in a 17-imp pull (7 dead, a win) and to Firesworn melee at Garr.

### 0.7 Next steps after week 1 (historical)
1. Week 2 = fresh lockout (`newlockout`, §0.1 recipe) and the full order 1–9; Majordomo Executus (boss 9) is
   untouched in this lockout and could be tried first if the owner wants it before the reset.
2. Geddon's area will need the timed/lure dance again (now generic + the lure entry); the Firewalker packs
   should now hold (fixes 1–2) — watch `fights.py` swaps on Flameguards.
3. Sulfuron: the leader tank died at 77 s to a Flamewaker Priest (Dark Strike 134k total, the top source);
   interrupts/priority on the priests would shorten it.
4. Lucifron's Impending Doom (dispel), Magmadar healer mana — unchanged from night 2.
5. Ops: restart only with all 39 alive at the entrance (`recover.sh`, then `restart.sh`).

---

## 1. The mission (owner's words, 2026-09-23/24)

Nico (the owner) said, verbatim-ish:

- "I need you to run MC, drive the whole thing, and make it so that the group/raid bots just
  play well ... I want to raid the entirety of classic and all dungeons. If I'm a tank, and I'm
  tanking a mob, another tank shouldn't steal that from me unless there's a stack."
- Constraints: "1. We aren't cheating 2. We try our best to keep the logic dynamic / boss
  agnostic (but again, I'm a little flexible)".
- Setup between pulls is allowed: "if a pack is cleared, it's cleared. If it respawns, save
  time and gm execute them." Revive/summon/repair/replenish/consumables between pulls = OK.
  Combat itself = normal rules, no GM help.
- Testwar (the owner's character, guid 787) may be bot-played (not done — see §9).
- "you are allowed to change and rebuild the server for this project no issues."
- Latest: "Dont stop. you need to clear MC." Then (this stop): "find a good stop, write a very
  detailed handoff."

Standing repo rules still apply (AGENTS.md): **never commit / push / branch / worktree** without
explicit consent; direct SQL is **read-only**; no DB/worldstate restore; only the vmangos dev
server may be restarted (never the cmangos one, PID 1079598); only restart while Testwar is
offline (the cycle script checks). Everything below is **uncommitted**.

## 2. Where things are

| What | Where |
|---|---|
| Box | `ssh wowvmangos@192.168.0.2` (key already set up for this Windows user; `-o BatchMode=yes`) |
| Core source (git, branch development-ish, HEAD b6a9f7e1c) | `~/vmangos` on the box — **all autopilot code is here, uncommitted** |
| Build | `cd ~/vmangos/build && cmake --build . -j32` (≈1–3 min; grep output for ` error` / `Built target mangosd`) |
| Running server | `~/vmangos/run/bin/mangosd`, systemd unit `mangosd` (runs inside `screen -S mangosd`) |
| Log (the evidence channel) | `~/vmangos/run/bin/Server.log` (rotates on restart — older lines vanish) |
| World DB (read-only!) | `mysql -umangos -pmangos mangos`; characters DB: `... characters` |
| Owner-editable data | `~/vmangos/run/etc/sui-raid-supply.json` (quartermaster policy) |
| Autopilot state files | `~/vmangos/run/etc/sui-autopilot-cleared.txt`, `sui-autopilot-deferred.txt`, `sui-autopilot-deferred.txt.entries`, `sui-autopilot-pulses.txt` (§5) |
| Local tooling (git-ignored) | `C:\Users\nico\source\repos\MSUIClient\scratch\autopilot\` |
| This session's patch scripts (history only) | `scratch/autopilot/patches/*.py` — each is a python search/replace applied on the box. The box source is the truth; these are just the trail. |
| Backups of sources before each round | `/tmp/bak-r5` … `/tmp/bak-r17` on the box (volatile — /tmp); previous binaries `run/bin/mangosd.prev-<sha8>` |
| Web app (SuperUI) | `http://192.168.0.2:5000` — `/Home/SendCommand` = GM console, `/Bots/ConnectBot` = log a bot in |

Installed binary right now: sha256 prefix **2add6ffcc768add4**, running as PID 3875073.
**The autopilot is OFF** (I stopped it). 39 bots are online in Molten Core, instance **100**.

## 3. The work loop (how to iterate)

All from Git Bash on the Windows machine (the Bash tool), repo root
`C:/Users/nico/source/repos/MSUIClient`:

1. Edit Core on the box. What worked reliably: write a python search/replace script locally
   (Write tool), `scp` it to `/tmp/`, `ssh ... 'python3 /tmp/x.py'`. Each `sub(a,b)` asserts the
   anchor text occurs exactly once. **Do not** put code with single quotes inside `ssh '...'`
   one-liners (it breaks bash quoting); **do not** use quoted heredocs through ssh for code.
   Back up first: `cp file /tmp/bak-rNN/`.
2. Build: `ssh ... 'cd ~/vmangos/build && cmake --build . -j32 2>&1 | grep -E " error|Built target mangosd"'`.
3. Deploy + restart + reconnect + enable:
   `bash scratch/autopilot/cycle.sh Shieldwall` — refuses if Testwar (787) is online; runs
   `deploy.sh` on the box (saveall, graceful shutdown, copies build binary over the run binary
   keeping `mangosd.prev-<sha8>`, `systemctl start mangosd`), waits for startup, runs
   `reconnect.ps1` (ConnectBot for guids 115–142, 150–160, verified by the DB online flag;
   Boomwarrior 115 usually needs a second call), sleeps 130 s (bots revive), then
   `.autopilot on Shieldwall`. Takes ~5–7 min. It can exceed the 600 s tool limit when chained
   with a wait — run the wait and the cycle as separate calls.
4. Watch: wait on the box with a bounded loop (the Bash tool blocks `sleep N` chains; keep each
   call < 600 s):
   ```
   ssh -o BatchMode=yes wowvmangos@192.168.0.2 'for i in $(seq 1 110); do n=$(awk "/HH:MM:S/{f=1} f" ~/vmangos/run/bin/Server.log | grep -c "fight [0-9]* over\|leader died"); [ "$n" -ge 4 ] && break; sleep 5; done; awk "/HH:MM:S/{f=1} f" ~/vmangos/run/bin/Server.log | grep -E "\[AUTOPILOT\]|JOINED|TELEMETRY\] fight [0-9]+ (\(|damage done)" | grep -v "raid prep\|executed\|plan for\|regroup\|pick found\|rest cap\|exploring\|backing" | cut -c12-260 | tail -25'
   ```
   (replace `HH:MM:S` with the enable time's prefix, e.g. `23:34:3`).
5. GM console from Windows: `powershell -NoProfile -File scratch/autopilot/gm.ps1 -Command ".autopilot status"`
   — **one command per invocation** (bash→powershell joins an array into one string).
6. Useful commands: `.autopilot on|off|pause|resume <leader>`, `.autopilot status`,
   `.autopilot why <bot> <firstRankSpellId>` (prints every cast-gate condition — this is how the
   mage OOM problem was found), `.tele name <char> mc` (MC entrance; works on bots).

## 4. What the autopilot is (code map)

All generic: no creature entries, spell ids or coordinates in decision logic (a few class spell
ids for class abilities: Fear Ward 6346, Tranquilizing Shot 19801, ranged shoot spells).

### 4.1 `src/game/SuperUiContent/SuiWorld/CRPG/SuiAutopilot.{h,cpp}` (new, ~2000 lines)
A bot leader (Shieldwall, a warrior tank, guid 152) stands in for the human. Other bots escort it
because `FindPartyBoss`/`FindEscortBoss` fall back to `SuiAutopilot::LeaderFor()` when no real
player is in the group, so the ordinary PlayerParty doctrine runs. `TickLeader()` is called from
`AiBotAIMain.cpp` before `[OVERPULL]`.

Phases: Idle → Rest → Approach → Pull → Return → Fight. Key pieces (search the function names):
- **Prepare** (≤ every 20 s): repair all, class buffs from classes present, role consumables,
  `SuiRaidSupply::Supply()` per member (quartermaster policy incl. `"bag": 14156` Bottomless Bag
  into empty bag slots — bots had full backpacks and no bags, so ammo/potions never fit).
- **Regroup**: revive dead members, summon members > 40 yd, bring members on another map back in.
- **Assess / rest**: hp ≥ 80, mana ≥ 70, gathered within 40 yd, no unsettled hostiles; 90 s cap.
- **PickNext**: rings 120/250/450/800/1200 yd; up to 40 nearest pullables probed until 8 usable
  (pathable, not crossing a deferred zone), score = walk + 60 × idle neighbours. Then:
  bosses-last (`BossOf`/`RoomTrash`: trash within 90 yd of a boss first), in-the-way
  (`InTheWay`: a pack within 26 yd of the walk goes first). `PatrollingBossNear` ("boss goes
  first") is **disabled** via `if (false && ...)` — see §7.
- **Pullable** excludes: in combat, npc-flagged/guards, cleared spawns, skipped (10 min),
  deferred entries/zones.
- **ExploreTarget**: only nearby grids are loaded, so when nothing loaded is pullable, the leader
  walks toward the nearest live, uncleared, hostile, non-deferred spawn from
  `sObjectMgr.GetCreatureDataMap()`, stopping 120 yd short and re-scanning every second.
- **PlanPull**: 24 directions; pull spot 28 yd, camp 50 yd out; scored by distance to every other
  idle pack **and patrol routes** (`ForEachRoutePoint` from `sWaypointMgr`) and to the raid's walk.
  Crowded plan (camp < 35 / spot < 22 / route < 15): a waypoint patrol target is waited out up to
  120 s at the **safe spot** (last spot with no idle hostile within 50 yd and no patrol route
  within 35 yd); a static target gets its crowder pulled first (≤ 3 hops); a patrolling crowder
  is waited for until ≥ 70 yd from camp.
- **Pull**: ranged shot with the leader's ranged weapon (NOT_READY = shot in flight), close in for
  sight if needed, walk in only without a ranged weapon. Aggro < 15 yd with camp < 30 yd = hold
  here, else Return to camp. Early aggro during Approach = fall back (whole raid) to the safe spot.
- Pull held while the raid is already fighting; never pulls outside a dungeon map; gather before
  pull (≤ 45 s, summon stragglers after 15 s); raid stacks within ~5 yd of the leader while
  waiting (`StackOnLeader`, avoids lava slots).
- **Respawns** (`NoteKill` hooked in `Unit::Kill`): kills by the autopilot's group are recorded as
  (map, instance, spawn guid) in `sui-autopilot-cleared.txt`; a recorded spawn that respawns is
  executed by itself (no loot) within 150 yd and never picked. World bosses excluded. A new
  instance id = everything fights again.
- **Wipe handling** (`NoteLeaderDeath`): 3 leader deaths on an entry → defer that entry, its boss
  and 80/60 yd zones around target/leader for **120 min**, persisted (unix time) in
  `sui-autopilot-deferred.txt[.entries]`, reloaded on `.autopilot on`.
- Off-mesh recovery (verified by pathing to the safe spot / entrance), rejoin if outside.

### 4.2 `SuiRaidTelemetry.{h,cpp}` (new)
Hooked in `Unit::DealDamage`, `Unit::Kill`, `SpellCaster::DealHeal`. For the autopilot's group:
`[TELEMETRY] DEATH` (last 6 hits, heals received, every healer's distance/LOS/cast/mana), per-fight
flush (damage by source, taken, healing, done by member and ability, rotation ticks + cast outcomes
per spell: `ok`, `gate` = our CanTryToCastSpell refused, `rNN` = SpellCastResult), and
`FIRST HIT` / `JOINED ... (unprovoked)` = **who pulled what** (this found the AoE-wakes-packs bug
and Lucifron's patrol). Also the **pulse detector**: a creature whose self-centred area damage spell
hits the raid twice within 2.5 s is a hazard (`PulseRadius`), and short (≤ 30 s) periodic-trigger
auras worn while pulsing are learned (`[TELEMETRY] PULSE learned`) into `sui-autopilot-pulses.txt`
(currently: `19695 20` = Geddon's Inferno).

### 4.3 Bot behaviour (default group AI — also applies when a human leads)
Files: `SuiBots/AiBotAICombat.cpp`, `AiBotAIMain.{h,cpp}`, `AiBotAISpecCombat.cpp`,
`AiBotDoctrinePlayerParty.cpp`, `AiBotDoctrine.cpp`, `PlayerBots/CombatBotBaseAI.cpp`.
- Healers: on duty whenever a member within 40 yd fights; range 38; 2 of 5 (guid%5<2) tank
  healers; incoming-heal subtraction; tank < 40 % = everyone's target; break a cast on someone
  else to regain sight of a tank < 75 %; walk the navmesh path to the tank.
- Repositioning is never cancelled by the next cast (`m_repositionUntilMs`; DoCastSpell stops
  movement for any cast-time spell — this was pinning healers).
- Tanks: keep a held elite, take loose mobs (elites first, 60 yd scan), split stacks (≥ 2 on one
  tank), taunt only off a tank that is < 25 % **or holds 3+** (owner's "unless there's a stack"),
  face the mob away from the group, Shield Wall/Last Stand at 35 %.
- DPS: kill order from spell lists (summoner > healer > caster > rest), ranged close for sight only
  toward the group's own fight, ranged/healers step out from hostiles within 14 yd, spread when
  2+ others within 5 yd, wounded melee step out; all repositioning avoids idle packs' 25 yd reach,
  patrol routes, lava.
- Hazards (`AvoidGroundHazard`): hostile DynamicObject areas and traps (spell data), lava/slime,
  enemy pulsing auras (data + observed pulses), a member carrying a bomb (harmful aura whose
  periodic trigger is an area damage spell centred on the carrier — Living Bomb) → the carrier
  walks away from everyone; melee/tanks wait outside while their target stands in a pulse.
- No area spell may reach an idle creature (`AreaWouldWakeIdle`) nor floor a low "riser" early
  (`AreaWouldFloorRiserEarly`).
- **Risers** (Core Hounds: lie at 1 hp, get up with full health unless every in-combat hound is
  down within 10 s): never attack a lying enemy; learned per entry (`NoteRiser`); DPS balance the
  pack (healthiest first, 10-point hysteresis) until all ≤ 8 %, then finish lowest first;
  `[AIBOT-RISER]` logs the kin's health when one falls.
- Class support: mages decurse the group; dwarf priests Fear Ward tanks; hunters Tranquilizing
  Shot enrage-type buffs; warlocks banish a spare elite when elites outnumber tanks
  (`[AIBOT-CC]`); bot pets defensive; potions in combat (mana < 25 %, health < 30 %, from item
  spell data); mage mana discipline (Blizzard > 30 %, Arcane Explosion > 25 %, Evocation < 20 %).

## 5. State of the world right now (2026-09-25 21:25, owner-requested stop)

**STOPPED at 21:25:** `.autopilot off Boomwarrior` issued; mangosd still running with the latest
build (all patches below deployed); the 39 bots are logged in inside Onyxia's Lair after a wipe.
Last log: `/tmp/Server-0925-2125-final.log` on the box (every earlier attempt: `/tmp/Server-0925-*.log`).
Patch trail (python search/replace scripts, re-runnable only on unpatched source):
`scratch/autopilot/patches/*.py` (git-ignored).

**Molten Core, instance 100:** every boss up to Majordomo is dead by normal combat — Lucifron,
Magmadar, Gehennas, Garr, Baron Geddon, Shazzrah, Golemagg, Sulfuron. The runes are doused and
Majordomo Executus has spawned. **Majordomo is not solved**: he comes with 9 adds (Flamewaker
Healers/Elites with Shadow Shock, Blast Wave), and the leader dies within ~15–75 s. The autopilot's
deferral lines for entry 11664 were cleared by hand for tests. After Majordomo: Ragnaros (summoned
through Majordomo's gossip — not built yet).

**Onyxia's Lair, instance 101 (map 249), current focus.** Onyxia has 1.1M hp (3331 × 330). Best
attempt: 558k telemetry damage on her (20:20). **Caution:** telemetry damage has over-counted real
HP before (~1.75× on 09-24), so the hard facts are only the phase markers: every recent attempt
reached phase 2 (≥ 35 % real damage), none reached phase 3 (60 %). Get her real hp% (e.g. log
`GetHealthPercent()` at the leader's death) before quoting percentages. Onyxia attempts today, damage on her: 223k → 450k → 168k →
158k → 489k → 448k → 558k → 507k → 133k → 246k → 544k → 443k (last, 21:25, only 16 deaths but
the leader died at 279 s). Phase 1 is now reliable (35 % in ~80–180 s, few deaths). Phase 2
(she flies, Deep Breath, Fireball, whelps) is where it breaks; phase 3 has not been reached.
Run it: `ENTRANCE=1 WHERE=onyxia bash scratch/autopilot/cycle.sh Boomwarrior`.
Kills in this lair never write `creature_respawn` rows, so the 5 Onyxian Warders are alive again
after every restart; the autopilot executes them at enable. One came back mid-run once (20:40,
spawn 52048) — unexplained, watch for `JOINED Onyxian Warder`.

Leader is now **Boomwarrior** (115, 9.5k hp). Testwar (787) is offline and not in play.

Learned-and-persisted knowledge (`~/vmangos/run/etc/`, all generic, keyed by creature entry or
aura): `sui-autopilot-pulses.txt` (aura → pulse radius), `.entries` (entry → pulse radius),
`.cones` (entry → cone reach; Onyxia 10184 = 45), `.rhythm` (entry → burst interval),
`sui-autopilot-risers.txt`, `sui-autopilot-rebounders.txt`, `sui-autopilot-cleared.txt`,
`sui-autopilot-deferred.txt[.entries]`. Splash radii (Onyxia's Fireball 8 yd) are learned in
memory only (`[TELEMETRY] SPLASH learned`), relearned on the first cast after a restart.

## 6. What was fixed on 2026-09-25 (all generic, all in Core, uncommitted)

MC: value-ranked dispels incl. charmed members; school-absorb potions from recent damage schools;
riser balance by 100 yd kin pool; rebounders (Golemagg's dogs) learned and left alone; pulse
keep-out measured from the creature's centre (radius + our size) with a light-burst exemption;
forecast hazards; frontal-cone dodge incl. long cast lines; healers close on the tank through a
ring search with path checks; ambush for patrolling bosses; pull planner rejects lava camps,
routes past packs, unreachable camps; scripted BUTTON objects (the MC runes) are used when
nothing is left to fight.

Onyxia (in order of effect):
1. **Dormant scripted traps** — a trap with no trigger radius (Onyxia's 50 lava fissures) is not
   a hazard until one of its kind has been seen going off in the instance. They had herded the
   whole raid into one corner behind her tail.
2. **Hostile proximity traps are hazards whatever they cast** (the eggs hatch whelps), measured
   in 3D (the eggs lie in a pit 10 yd below the entry ledge; a 2D circle blocked the way in).
3. **Long-reach creatures** (combat reach ≥ 8; Onyxia 23.4 reach on a 1.8 yd body): melee stand
   10 yd out, tanks 7 yd out in front (`ChaseInPlace`, used by every melee chase and by the
   tank-facing step). Everyone had stood inside her body at 3 yd — Cleave chained through all.
   Cone kinds (learned cone) get their melee at ±105° (clear of front and 120° tail cone).
4. A lone long-reach creature that wakes far out is fought where it is, not led back to camp.
5. **Threat discipline** (`ThreatHoldFor`, AiBotAIMain.cpp): a damage dealer skips its rotation
   while at ≥ 90 % of the holder's threat on an elite/boss. Onyxia no longer turns to casters.
6. Hazard escapes never cross a circle they are not already in; a new target (kill-order focus)
   no longer cancels an escape under way (`m_repositionUntilMs` guard in `AttackStart`) — the
   Deep Breath deaths were bots thrashing between escape and chase.
7. Healers' closing ring excludes the front/rear arcs of a cone kind.
8. **Splash spread**: ranged/healers stand splash+1 apart from everyone while a creature that
   bursts around its target is in the fight.
9. **Ranged peel only for themselves**: small adds on others are melee's; ranged stay on the boss
   (P2 had taken 6 minutes with every caster on whelps). Deployed at 20:39, one attempt so far
   (ended early by an unrelated leader death).

10. Tank-facing step for a cone kind puts the group at the mob's **side** (tank square to the
   group), not behind it: the plain "far side from the group" rule put the raid in her Tail Sweep
   (108k in 77 s → 19k after the fix).

Telemetry added: `[TELEMETRY] SPLASH learned`, `[AIBOT-HAZARD] ... carrying a bomb (N yd, aura A
from CASTER)`, rotation line column `hold` = threat hold ticks.

**Engulfing Flames (aura 20019) is Onyxia's**: 259 bomb lines in the last attempt, all "from
Onyxia" (cast by the core, not in any DB script; the script's own constant is commented out). It
pulses fire (20021) 5 yd round the carrier **onto other members** — 7 of 34 deaths at 21:05 were
"killed by <member>". The carriers already walk away from the raid; several carriers standing
together still kill each other.

## 7. Suggested next steps (in order)

0. Restart the loop: `ENTRANCE=1 WHERE=onyxia bash scratch/autopilot/cycle.sh Boomwarrior`
   (Testwar must be offline), then watch with `scratch/autopilot/watch.sh` or the wait loop in
   the transcript (grep `fight [0-9]* over\|leader died`). One attempt ≈ 10 min incl. restart.
1. **Healer throughput is the biggest general weakness** (all fights): at 21:05 ten healers did
   ~1.2k HPS in total; rotation ticks show priests casting only 10–20 % of the time and ~60 % of
   ticks spent repositioning (dodges, spacing, bomb escapes, closing). One priest (Ashbeard) spent
   the fight stranded at z −15 in the entry tunnel. Reduce needless moves for healers (e.g. no
   spread/splash moves while the tank is < 50 %, finish the heal before a cone dodge unless the
   cone is being cast) and log heals per healer (heals are not in the rotation telemetry).
 Onyxia phase 2: read the next attempts' P2 (`FORECAST` breaths; `killed by Onyxian Whelp`;
   ranged damage on Onyxia vs whelps). Whelps (~12/min) wear down the tanks and healers; the
   warrior Protection rotation already has Demoralizing Shout/Cleave behind `CanUseSpecAoE`.
   Deep Breath forecasts arrive ~5–7 s early; whether the escapes now finish in time is the open
   question. Phase 3 (fissures erupt, Bellowing Roar fear — dwarf priests' Fear Ward) untested.
   Engulfing Flames carriers: make carriers also avoid *each other* (they do avoid members, but
   the escape spot search can end with two carriers together) — check `nearest()` in the bomb
   branch of `AvoidGroundHazard`.
2. Main-tank healing: several leader deaths show "healed 0 in last 8 s" with healers in range
   casting on others. Look at tank-healer assignment (2 of 5 by guid) under raid-wide damage.
3. Majordomo (MC): 9 adds; banish/polymorph caps are (tanks+1)/2; the leader dies first.
4. Then Ragnaros, then BWL and the rest. 5-man bosses are rank 1 — bosses-last does not see them.
5. Clean-ups owed: the `if (false && ...)` dead branches in `SuiAutopilot.cpp`.

## 8. Gotchas that cost time today

- The log rotates on every restart — grab evidence before redeploying.
- `awk "/HH:MM:S/{f=1} f"` needs a timestamp that actually occurs in the log.
- `grep` of "no path to" lines appears at LOG_LVL_BASIC; `[AUTOPILOT]` notes are MINIMAL.
- After a restart while the leader or members were dead, they come back outside MC (the
  autopilot now pulls them back in by itself; `.tele name X mc` works manually).
- Deferrals/zones in memory are lost on restart unless in the files (they are persisted now).
- The Artifact/Monitor tools are not needed; plain ssh loops are enough.
- A `.bot add Testwar` makes a stock PlayerBotAI without a `playerbot` row (would need an SQL
  write) — it was deleted again with `.bot delete Testwar`. Testwar is not bot-driven.

## 9. Things deliberately not done

No commits. No SQL writes (only reads). No DB restore. No gear upgrades beyond the
quartermaster's bags/consumables/ammo (bot gear is untouched). No GM combat help (the only GM
actions are between pulls: revive, summon, repair, buffs/consumables, executing *respawned*
already-cleared spawns, teleporting the leader back into the instance).
