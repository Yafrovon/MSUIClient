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

### 0.30 RERUN 4: RELIABILITY FROM WEEK 0, RAID MAP + TACTIC LEDGER (2026-11-18 →, Claude, in progress)

Owner 2026-11-18: "Start again at week 0, and focus on stabilizing the fights, the packs ... We want reliability. By the
time we hit week 4-5 - we should never die to Onyxia nor the first bosses in MC." and "We also need to make sure we
don't ping pong ... establish the full raid as a map - such that we know what happens where, the patrols".

**Read first, every raid change:** `shared_docs/RAID_MAP.md` (GENERATED by `bash tools/raidmap/regen.sh` from the world
DB + fight records: packs `P<lowest guid>`, bosses `B-<name>`, routes `R<guid>`, hazards such as P91261 joining 36 of 68
Geddon fights, Lucifron's walk R56605 through his hound packs, the 18-min Ancient Core Hound patrols, the random
Firelord/Lava Annihilator spawns) and `shared_docs/RAID_TACTIC_LEDGER.md` (binding: one row per change, its evidence and
its result over N pulls; "kept" only after ≥ 3 pulls; known reverts listed so nobody re-tries them blind). AGENTS.md
rule 10.

**Harness added:**
- `scratch/autopilot/ony-drill.sh LABEL [MAXPULLS=12]` — Onyxia reliability drill: one fresh-lockout pull per round via
  `r3-night.sh onyxia` (MAXWIPES=0, DRILL=1), tally "LABEL DRILL pull i: … last 10: …", stops at 8 of the last 10; then
  takes every raid-only item that appeared back (`ony_drill_loot.py snapshot/diff`; drill kills are QA, never loot).
- `week-loop.sh` honours `DRILL=1` (no loot wait, no Majordomo cache, no council).
- Death analysis over the rotated logs (`/tmp/Server-MMDD-HHMM.log` = the log of the run BEFORE that restart): `scratch/autopilot/`
  `onydeaths.py` (deaths by cause per pull) and `onycones.py` (mass Flame Breaths: hit count, her target, angles).

**Onyxia fixes (all `{ kOnyxia }, { 10184 }` rows in SuiTacticScope.h):**
1. `deep-breath-escape` — each member inside a forecast Deep Breath impact (+3 yd) takes the nearest pathable spot
   outside all of them once and holds it 14 s (`EscapeBreathForecast`, `[AIBOT-BREATH]`); the facing-based "off the line"
   step is off for her airborne breath.
2. `whelp-posts-at-spawns` — every tank but her landing pair holds a post 8 yd inside a whelp spawn point, takes only
   whelps within 16 yd, Challenging/Demoralizing Shout there (`WhelpPostFor/HoldWhelpPost`, `[AIBOT-WHELP]`).
3. One landing pair for the fight (`OnyxiaLandingPair`, read by StageForLanding and the posts), chosen once airborne at
   ≤ 42 %, a dead one replaced (`[AIBOT-TANK] X is one of Onyxia's two landing tanks`).
4. `landing-head-spot` (23:41) — landed, her pair keeps the head spot (7 yd off the landing spot toward the nearest wall)
   and never steps out of her front; the member she is on walks her there (`[AIBOT-HAZARD] X: Onyxia is on me - to her
   head spot`); no off-pair tank chases her; her victim keeps its place in the whelp post order.

**Results:** R4W0 Onyxia night 39 / 30 / 23 % then KILL att. 4 (363 s, 2 dead; council Halo → Ashbeard/Vexmark/Nerret,
Helm of Wrath → Stormdoom). Drill 1 (fixes 1-3): 3 kills in 12 — P3 landing was the killer (landing target a ranged
member for 4-38 s, 10-25 hit by one Flame Breath in 9 pulls). Drill 2 (fix 4 + amendments, 23:42-03:40): 3 kills in 12
(ledger: head direction = fewest members in front; flanks off the head direction; tanks excluded from flanks, no
off-pair chase from pair choice). Frozen pulls 6-12: 1 kill in 7.
5. `landing-descent-hold` (03:46) — her script (`boss_onyxia` PhaseTransition) calls `DoResetThreat()` as she turns to
   land at < 40 %, not at touch-down; from < 40 % in the air damage dealers hold until a tank holds her after the landing.
6. `pair-meets-her` (03:46) — turning west of x -40 she flies to her south point (-59.9,-214.9) and then chases her
   victim from there; landed > 12 yd from (-8.86,-212.75), the main pair tank goes to her, takes her, and walks her to
   the head spot (`[AIBOT-TANK] … landed N yd from her landing spot - to her` / `… walking her home`).

Drill 3 (03:48-07:30): 0 kills in 12 across seven builds; drill 4 (07:31-11:25, A/B): 3 kills in 12. Rows added in
drill 3 and kept (ledger has each one's evidence and result): pair holds the head spot through her cast breath and walks
back after a fear/Knock Away; flanks measured from her position toward the head spot (her melee reach is ~26 yd, she
does not walk to it); descent hold tested before the victim check (no victim on her landing flight);
`whelps-walked-to-tanks`, `no-splash-on-loose-whelps` (mages' first hits on loose whelps 47 → 0), `whelp-spawn-keep-out`
(healers/casters 20 yd off the spawns, spot in sight of her landing spot). REVERTED: `deep-breath-lane-clear` (0/8 on vs
3/12 off). Harness: `sui-tactics-off.txt` row switch (build 5455e520+), `onyp2.py` (pre-landing deaths by cause),
`ony-drill.sh` last-10 count fixed. Open (ledger order): Deep Breath kills whoever stands deep in a lane (all cross the
centre; impacts 3-5 s after the cast), P3 attrition, P1 healer sight of the main tank. **R4W0 MC night: 6/10**
(Lucifron, Magmadar, Gehennas, Garr att. 2, Geddon, Shazzrah att. 2), 6 trash wipes filed by map id in the ledger.
**R4W1: Onyxia KILL att. 2; MC 6/10 + Golemagg 11 %.** MC rows added between wipes: `corridor-clear-of-packs` (a corridor
pull plan needs pull spot and camp 20 yd clear of idle packs; a patrol whose plan is refused is waited for, then skipped
10 min) and the Core Hound burst floor 18 → 10 % (ledger parameter history). Open MC: Garr's paladin tank at the pull
(6 occurrences), patrols joining (R56852/R56844/R56865), healer closing walks into idle packs, imp first hits by mages.

**R4W4 (11-20): Onyxia KILL att. 4; MC 3/10** (Lucifron, Magmadar, Gehennas; 3 trash wipes filed: P56777, P56720, P56634), then a 78-min loop by Garr:
R56855/R56857 (crossing ACH walks) crowded each other's ambush under `patrol-ambush-clear`, `skip-unambushable-patrol` skipped them and
`RoomTrash` (ignores skips by design) re-picked them ~4400 times. Row `patrol-walks-overlap` (build 20:15): another lone trash patrol counts
where it stands in an ambush score, not along its walk. Lead changed hands 20:10 (the previous agent was cut off after the MC night).
Open drills from the ledger: a Firelord pair (one raid-wide Lava Spawn target) and a Core Hound pack (last hound outliving the burst).
**R4W5 Onyxia KILL att. 1 (310 s, 6 dead).** Row `raid-wide-spawn-target` (build 20:20; one shared Lava Spawn target per
raid, `[AIBOT-SPAWN]` log line). Firelord-pair QA drill 20:43-22:41 (`scratch/autopilot/r4w5-firelord-drill.sh`, old R4W4 lockout,
P56720): 9 pair pulls, 9 won, 8 clean, 1 contained runaway (#177, 57 spawns, 124 s, 1 dead). The drill used to score only the four
Lava-pack kinds (`DrillScore` kPack list), so Firelord/Core Hound drills never counted. It now scores every fight pulled in its circle
(built, live from the next restart). **R4W5 MC 23:08-03:08 = 6/10** (Lucifron, Magmadar, Gehennas, Geddon, Shazzrah att. 1; Garr att. 3). Two Core Hound pack wipes
(P56634 23:45 leader death, P56639 00:00 40/40) → row `floored-riser-stop-swing` (no white swing and no cast in progress on a floored hound
while a kin is above 20 %), deployed 00:04 between attempts; all later hound fights 0 dead. Garr's last ACH R56855 looped (414 re-choices)
→ row `room-patrol-tag-to-camp` (a patrol a boss waits for, no clear ambush: tagged once nothing idle is within 22 yd of it, fought at the camp),
deployed 01:43, unexercised. **Mid-night deploy recipe**: stop the runner (TaskStop + kill week-loop/waitreview/retry), then
`bash r4w5-mc-resume.sh REASON` (recover ×2, restart = install, autopilot on, week-loop to the old end, then marks "== R4W5 mc done").
A restart under a live week-loop ends the night (waitreview sees SERVER STARTUP). Last pull: Lava pack P56729 + Surger R56849, 40/40 (filed).
**R4W6 (11-21): Onyxia KILL att. 1, 0 dead; MC 8/10, all att. 1** (Majordomo only after the budget). New rows that night: `room-patrol-tag-to-camp`
reworked to tag at the clearest point of the hound's walk (07:52), `crowded-patrol-times-pull` (09:15: a patrol with an active no-clear-ambush mark
whose walk passes ≤ 30 yd from the next pack times that pull). Open, by cost: Lava packs by Golemagg/Sulfuron (P56729, the 56750/56775 pair),
Shazzrah (melee in Arcane Explosion, 16 dead), P56786 corridor (no plan → walked in), Majordomo (untried in budget for two weeks). Next: R4W7.
Resume scripts: `r4w6-mc-resume.sh REASON` (copy for a new week: sed the week label, end time and out file).
**R4W7 (11-21): Onyxia KILL att. 5** (att. 2-4 lost to phase-3 whelps on healers: posts take only whelps within 16 yd). **MC 7/10 in budget** + Sulfuron after it.
Fixed in the night: `spawner-first-for-ranged` now also sees a Firelord in combat within 80 yd (the ranged had spread past 45 yd and killed spawns forever:
156/188-spawn runaways). Open, by cost per week: Garr (3 lost attempts; all Firesworn must stay on holders - 4 banishes + 5 tanks for 8 adds + Garr has no margin),
Onyxia P3 whelps (4 attempts), Shazzrah (16-18 dead per kill, Arcane Explosion after Blink; decursing is active), Lava packs by Geddon/Golemagg (P56729, P91261).
**R4W8 (11-21): Onyxia KILL att. 2** (row `whelp-post-sweeps-healers` deployed between attempts: post tanks take whelps on healers ≤ 25 yd from their post; 0 whelp deaths in the kill).
**MC 7/10 in budget, all att. 1** (+ Sulfuron 80 s after). `room-patrol-tag-to-camp` (clearest point of the walk) worked live by Garr. Built, not yet live: `shazzrah-stand-and-cast`
(no clear step / dropped cast beside Shazzrah - his Arcane Explosion is 30 yd and follows his Blink). Open: Lava packs (P56729 4 wipes: pull timed around the wrong Surger -
time around EVERY lone patrol passing ≤ 30 yd; P56750 6/19 wipes even when planned → drill), Lucifron joining hound fights, Garr holders.
**R4W9 (11-21/22): Onyxia att. 1. MC 9/10 in budget - Majordomo defeated att. 1; Ragnaros 45 % (att. 1), 34 % (att. 2, after the budget).**
`shazzrah-stand-and-cast` REVERTED (40/40 in 80 s - the step-out is what keeps people alive) via `run/etc/sui-tactics-off.txt`; the code was removed 11-22 01:25
(SuiTacticScope.h row + the AiBotAICombat.cpp early return; off-file entry cleared). P56729 won pulled by its Lava Elemental. Open: room-patrol tag must keep the CAMP
outside idle Lava packs' 43-52 yd reach (R56860/P91268); planned route vs walked path (P91257); Ragnaros (the target now).
**R4W10 (11-22): Onyxia att. 3; MC 7/10 in budget, all att. 1** (+ Sulfuron 3 min after). Built 02:33 / 05:18, live from the R4W11 restart: `crowded-patrol-times-pull`
never applies to a target that patrols itself; row `lava-pack-camp-clear` (a camp < 55 yd from an idle Lava pack is crowded - that pack goes first).
**R4W11 (11-22): Onyxia att. 2 (att. 1 3 %, whelps); MC 8/10 in budget, all att. 1, Majordomo never pulled** - a corridor-skipped Lava Surger (R56845) was un-skipped by `InTheWay` every round ("Lava Surger stands in the way of Lava Surger"), 10:38-10:59. Row `corridor-skip-holds` (SuiAutopilot.cpp: `Pilot::corridorSkip`, `hopBlocked`; built 12:44, md5 81e0100a). `waitreview.sh` now takes a deadline epoch (3rd arg) from `week-loop.sh`, so a night with no review pause still ends on its budget.
**R4W12 (11-22): Onyxia KILL att. 2** (att. 1 57 % - Deep Breath killed the healers, one of them walked back into the lane by the heal-close walk; att. 2 305 s, 7 dead). **MC 13:19-17:20 = 9/10 in budget** (Lucifron 4 dead, Magmadar 23 dead (won), Gehennas 0, Garr att. 2 (att. 1 89 %, 40/40), Geddon 3, Shazzrah 9, Golemagg 0, Sulfuron 0, **Majordomo submitted att. 2** at 17:22 (att. 1 82 %, 40/40 - he joined unprovoked at +3 s on the first Elite pull)). Ragnaros was being summoned at 17:24 when the budget ended - 0 attempts. One trash wipe (14:58, Gehennas' room again: Firelord pull 30 from camp (903,-614), P56708 woke at +55 s, P56788 at +60 s, ~150 Lava Spawns split - 2nd week in a row at that spot). `corridor-skip-holds` never fired (no 'waits with it' line) - Majordomo was reached without it. Healer-lane amendment (e25fa1a1) went live with the 17:13 retry restart. Open: Gehennas' room camp (903,-614) vs P56708/P56788 (2 weeks running), Ragnaros needs MC done by ~3 h 30.
**R4W13 MC (18:40-22:48) = 6/10** (Lucifron 3 dead, Magmadar 2, Gehennas 0, Garr att. 2 (att. 1 96 %), Geddon 10 (won), Shazzrah 4 at 21:58). Two trash wipes in the imp room (P56549: 19:32 with a Lava Surger, 20:25). `giant-pack-before-firelord` fired 8 times (P56714, P56702, P56708, P56713, P56700), no Giant/Firelord wipe. **Stall 22:20-22:48**: P56797 (Firewalker 56797 / Flameguard 56799, Golemagg's room) put off because "the walk passes Golemagg (27-31 yd) - it waits", and `RoomTrash` un-skipped it the next second - 500+ picks, no pull, Golemagg/Sulfuron/Majordomo/Ragnaros never reached. → row **`boss-pass-skip-holds`** (build dbdd9a1d, live from the R4W14 restart): room trash put off that way is passed over for another pack member (R4W12 pulled the same pack through its Lava Reaver 56798 from (855,-1012)) or another room pack; old behaviour only when nothing else is left. Rows today: `corridor-skip-holds`, `giant-pack-before-firelord`, `boss-pass-skip-holds` (SuiAutopilot.cpp `Pilot::bossPassSkip`, RoomTrash).
**R4W14 (11-22/23): Onyxia KILL att. 2** (261 s, 0 dead; att. 1 43 %, phase-2 whelps). **MC 23:22-03:30 = 8/10** (all att. 1 but Garr att. 2; Majordomo att. 1 95 % at the budget's end, Golemagg/Sulfuron 0 dead). Wipes: P56786 23:36 and P56721 00:43 - both a Firelord pair pulled **with no plan** (the leader kept walking while PlanPull refused). → row **`no-plan-no-walk-in`** (SuiAutopilot.cpp `Pilot::noPlanMs/noPlanSkipped`, crowdedButClear; build 4ed2367d 03:33, live from the R4W15 restart). `boss-pass-skip-holds` not exercised (nothing was put off in Golemagg's room). Time sinks: the two wipes (~35 min with recovery), Shazzrah -> Golemagg 27 min.
**R4W15 (11-23): Onyxia KILL att. 3** (295 s, 0 dead; att. 1 71 % phase-2 whelps on 7 healers, att. 2 41 % phase-1 Cleave/breath). **MC 04:15-08:23 = 5/10** (Shazzrah 13 % at the end). `no-plan-no-walk-in` worked (8 holds, corridor 0 deaths). Lost: imp room 37 min (back-off loop + self-timed wait) → rows `timed-walker-goes-first`, `timed-wait-not-self` (build d663d88b, 08:30); P56629+P56639 double hound wipe 05:45 (3rd time - plan them as one); Garr att. 1 paladin tank dead at +0 s.
**R4W16 (11-23): Onyxia KILL att. 1** (342 s, 25 dead - 6 healers to phase-2 whelps, then a Flame Breath 9) → row **`whelps-off-healers`** (the doctrine peel skipped elites; her whelps are elite). **MC = 8/10, 0 trash wipes** (corridor 18 fights 0 dead; Garr att. 3; Majordomo att. 1 91 % at the end). The night first stopped at 09:41 on a Lucifron evade at 100 % (week-loop did not handle a non-Majordomo `(boss )` pause - fixed) and was resumed 09:45 on the same lockout (`r4w16-mc-resume.sh`, f2844787).
**R4W17 (11-23): Onyxia KILL att. 1** (286 s, 5 dead; first `whelps-off-healers` night, 3 healers to whelps vs 4/7/6). **MC 13:16-17:24 = 9/10 in budget, Ragnaros 39 % / 32 %** (best yet). Wipes: P56721 trash 14:36 (no-plan skip undone by `InTheWay` → row `no-plan-skip-holds`, build 4ea18ccb), Garr att. 1 (Firesworn; Garr died with the raid), Geddon att. 1 95 % (Lava pack joined), Majordomo att. 1 97 % (Healers' Shadow Shock). `timed-walker-goes-first` fired once in the imp room (16 min, 0 wipes).
**R4W18 (11-23): Onyxia KILL att. 1** (405 s, 16 dead, 0 to whelps). **MC 17:57-21:58 = 6/10**: 3 trash wipes (corridor 18:05 + 18:17, P91268 21:13) cost ~75 min. Rows: `no-plan-skip-holds` fired once (P56721 safe); new `ambush-skip-holds` (build e5b0672e) for the 18:05 wipe. Open: wait/walk positions inside idle packs' reach (18:17, R4W11 08:35) and the call-for-help wait on a patroller's walk (21:13).

**Next (older):** drill 3 to 8 of 10 (one build per N pulls, ledger rule 1), then the R4W0 MC night `bash r3-night.sh mc R4W0 4`,
every wipe filed against a RAID_MAP id, wiping areas drilled to 8 of 10; then weeks R4W1…R4W5. Open MC areas from the
ledger: Garr's first pull, Lucifron's hound packs (P56634), Firelord pairs waking a second Firelord, the Ancient Core
Hound wait by Shazzrah.

### 0.29 RERUN 3: ONYXIA AND MC EACH THEIR OWN 4 H NIGHT (2026-11-14 →, Claude, in progress)
**Owner (11-14):** "Previous agent misunderstood. Onyxia is its own 4 hour attempt on every week, and MC is its own. Your
first task is to get Onyxia working in week 0, and then restart week 0 to week 3 doing both raids." Answers (asked the
same day): stop rerun 2's week 4; make Onyxia killable in pre-raid gear first (dev attempts on fresh lockouts, dev loot
stripped), then the clean rerun; **week 0 has both raids too**; each week = **Onyxia night (fresh lockout, 4 h, ends at
her kill), then MC night (fresh lockout, 4 h)**; **loot = upgrade-first loot council** (below). Rerun 2's §0.28 1-h cap
was the misunderstanding. Tally lines `R3DEV`, `R3W0`..`R3W3` in `scratch/autopilot/week0-setup-1111.log`.
- **Pre-raid reset (SETUP 09:07):** rerun 2's week 4 loop stopped 09:07 (autopilot off, raid recovered). 73 raid-only
  items taken from 23 members (`raid_loot_held.py` + `mc_refill_pick.py`, cmds `r3w0-strip-cmds.txt`); 14 casters had
  **no cloak at all** (lost in earlier swaps) - refilled with Cloak of the Cosmos 18389 (`/tmp/refill_empty.py`, new:
  empty-slot refills by the same rules). Learned data and playbook kept.
- **What killed Onyxia attempts (found from rerun 2 W4 + six dev attempts):**
  - Onyxia is **taunt-immune** (`creature_template.immunity_flags` 14 has TAUNT); every warrior "taunt" on her was a failed
    Mocking Blow. Phase 3 begins with `DoResetThreat`; the casters who held her kept casting (the old hold exempted the
    holder) - ten turns in 30 s, a Flame Breath with each.
  - **Her Fireball triggers Engulfing Flames** (`spell_effect_mod` 18392 eff 1 -> 20019) on all in its 8 yd splash; each
    carrier pulses 250/s (20021) at FRIENDS within 5 yd. A clump hit once dies in 2 s (14 dead in attempt 4; deaths
    "killed by <member>" with empty last-hits are this).
  - Phase 2: healers anchored on her Fireball target (ActiveTank scored the airborne boss's victim), dodged "her front"
    while she hovered, stepped clear of every whelp, and closed on tanks onto the SAME ring spot (pairs stacked).
  - Phase 3: Bellowing Roar fears the tank; Fear Ward went to any tank; off-tanks held whelps inside her front cone
    (tanks were exempt from cone avoidance), and tanks walked their Engulfing Flames away, turning her.
- **Core fixes (generic, uncommitted, build installed; backups `/tmp/bak-r3/`):**
  1. Threat discipline rewrite (`AiBotAIMain.cpp` ThreatHoldFor): melee hold at 75 % / ranged 80 % of the tank's threat;
     a world boss whose threat list is wiped is the tanks' until one has held it 4 s (`[AIBOT-THREAT] ... threat list
     wiped`); a world boss loose on a non-tank with a tank within 40 yd: every DPS holds (the holder too, which uses
     Feign Death / Fade / Feint), max 10 s; no hold against a flying boss; a hold also stops white swings and Auto Shot.
  2. `ActiveTank` ignores an airborne world boss's victim; no "out of the front" step under an airborne unit; no clear
     step from small adds beating on someone else.
  3. Fear Ward: the tank holding a world boss first, re-warded as soon as the ward is spent, one priest per 3 s.
  4. Spread no longer refuses every spot when the anchor/target is already out of range; healers closing on a tank
     avoid other members and each other's claimed spots.
  5. A tank holding a world boss does no bomb walk; off-tanks keep out of a coned boss's front/tail (only its target stays).
  6. **`.autopilot council <leader> [ids]`** (SuiAutopilot.cpp): every epic weapon/armour of item level >= 66 in a
     member's bags goes to the member whose own `ScoreItem` gains most (vs. that slot, normalised by the member's average
     worn piece) and is worn (`[AUTOPILOT] LOOT ...`). Tested with Sapphiron Drape (taken back).
- **Dev attempts (instance 101/102, pre-raid gear):** 44 % (all P2) -> 29 % -> 35 % -> 47 % -> 15 % -> **KILL 10:51:32,
  423 s, 5/40 dead, 0 rezzes** (dev attempt 6). Dev loot (Bloodfang Hood, Dragonstalker's Helm, Sapphiron Drape, backpack,
  Head of Onyxia, all on Briarshard) taken.
- **Helpers:** `r3-night.sh onyxia|mc WEEK [HOURS]` (revive, outside, install + restart, newlockout, raid in, autopilot,
  `week-loop.sh ... 99`, recover); `r3-week.sh WEEK` = Onyxia night then MC night. `week-loop.sh` now runs the council
  after every kill (Majordomo: `.autopilot loot` twice for the cache first), logged as `WEEK LOOT ...`.
- **Week 0 started 12:02, ABORTED 14:26** after 10 wipes (37/32/29/9/24/43/4/18/16/1 %): one kill in six dev pulls was
  not a reliable build, and a frozen 4 h loop does not adjust strategy (owner 11-14: "are you adjusting the strat between
  failures?"). Back to dev pulls, one wipe each (`MAXWIPES=0 bash r3-night.sh onyxia R3DEVn 1`).
- **Second dev round (attempts 7-16, 15:22-19:08), each a fix for what the previous pull's telemetry showed:**
  7. `[TELEMETRY] THREAT` (boss top-8 threat every 5 s): in P3 the spare tanks sat at 1.5-1.9k on whelps behind casters
     at 6-10k; her tank died and she went down the casters. -> **backup tank** (`AiBotBackupTankFor`): the living spare
     tank with most threat on a tanked boss hits it (TankDuty), holds at 80 % of the tank; DPS also hold at 95 % of the
     backup once it has 25 % of the tank. Result 19 %: backup second all P3, three tank-to-tank turns.
  8. Melee under an airborne boss spread like casters (>= splash + 1, Fireball splash is 8 yd). Result 32 %, P2 clean.
  9. A lone boss that wakes on the walk-in is fought where it woke (the fallback dragged her through the casters); Fear
     Ward also on the backup tank; threat-wipe window lasts until the tank held 4 s AND has 1.5x the top non-tank.
  10-11. `[TELEMETRY] CONEHIT` (angle/distance of every boss-cone hit on a non-target): victims stood 20-50 deg off her
     facing while she was tanked - `AvoidFrontalCone` found no flank spot at the bot's own distance and gave up
     silently. -> flank spots also nearer/farther (10-33 yd) and at 80 deg; non-target breath hits 22 -> 6.
  12. Bomb carriers claim their escape spot (four Engulfing Flames carriers walked to the same corner).
  13. Landing (airborne -> ground) opens the tanks-first window like a wipe: she had landed with NO tank on her list
     (Fireballs keep threat low, so no wipe showed) and casters held her 30 s.
  14. **3 %** (same build as 13+landing): tank had her 9 s after landing, 29-31 alive through P3; lost to whelps on the
     tank and his backup, then the casters.
  15. Same build: **87 %** - fought where she woke, flanks walled: 1024 "no flank spot", ranged behind her, Tail Sweep
     205k. -> wide flank-arc fallback (75-105 deg, 8-33 yd), rejection reasons in the log.
  16. 24 %: no-spot 1024 -> 24; 7 dead at the pull; P3 collapse again.
- **Open at 19:10:** phase-3 whelps (script: one every 1-10 s on a random attacker - the longer P3, the worse); P3 DPS
  is capped by tank threat (Knock Away -25 %); deaths at the pull; healers move 50-60 % of the fight. Same-build
  variance is large (3 % vs 87 %), so one pull proves little.
- **Third dev round (attempts 17-19, 21:40 →), checked against the standard Onyxia strategy (owner 11-14: "Are we
  using wiki/common knowledge to help speed this up?" - until then every fix came from logs only; the two biggest finds
  below are first-page guide advice):**
  17. **Tanks keep casting Sunder Armor at a full stack** (`AiBotAISpecCombat.cpp` UpdateSpecCombatWarrior, both tank
     branches): the stacking rung refuses 5/5, so tanks had only Revenge/Shield Slam/Heroic Strike - ~90 threat/s, and
     every DPS was capped under it (raid ~2.7k dps). Rank 5 = 261 threat a cast. **Melee skip small adds standing in a
     grounded cone boss's front/tail** (`AiBotDoctrinePlayerParty.cpp` KillPriority; same zones as AvoidFrontalCone).
     Result **10 %**: no pull deaths, tank threat ~210/s, raid 3487 dps (melee 437 -> 1120); P3 lost to Flame Breath on
     healers who followed the leader's whelp ("my human's victim" -> converge -> out of her front, every 1-2 s).
  18. **Healer threat hold** (`AiBotHealerThreatHold`, AiBotAIMain.cpp; called from CombatBotBaseAI SelectHealTarget /
     SelectPeriodicHealTarget / FindAndPreHealTarget): while a grounded world boss's tanks-first window is open, or at
     90 % of the tank's threat, healers heal only members under 40 %. **The pull opens the tanks-first window** (first
     sample of a boss not seen for 60 s, top threat < 5000; log `pulled - tanks only ...`). **A cone world boss is held
     with its head to the nearest wall** (MaintainTankFacing: 24 rays at head height, first LOS break 12-30 yd, facing
     locked per boss until it moves 12 yd; log `holds Onyxia with its head to the wall`). Result **KILL 22:15:32, 346 s,
     0/40 dead**, raid 3765 dps. Dev loot (Sapphiron Drape on Stormdoom, Halo of Transcendence on Vexmark) taken; their
     own cape/helm worn again (`.autopilot wear`).
  19. Healers do not follow the leader onto a small add mid-fight (AiBotAIMain.cpp converge). Result **KILL 22:41, 420 s,
     13 dead** (10 to one Deep Breath at +202 s). Then **Deep Breath counts as a breath** (AvoidFrontalCone: an airborne
     world boss holding still with no target = boss_onyxia's 5 s Deep Breath; the spell is cast triggered, so the cast
     test never saw it).
- **Clean week 0 (2026-11-14 22:45 →, dev loot stripped, `raid_loot_held.py` = 0).** Onyxia: the first pull was voided
  at +115 s (63 %, 0 dead) by **the server's own scheduled map-249 reset** (`characters.instance_reset`, 23:00 EST; next
  249 Nov 19, 409/469/531 Nov 20 - no night may span one; `waitreview.sh` now also stops on `outside an instance` /
  `DungeonReset`). Re-run 01:03: 23 %, 15 %, **KILL 01:43 (480 s, 7 dead)**; council Halo of Transcendence -> Vexmark,
  Eskhandar's Collar -> Stormdoom, Netherwind Crown -> Doctormaster. Both wipes: phase-3 Bellowing Roar feared an
  unwarded tank, she turned, one Flame Breath killed 6-10 casters; the first Fear Ward came 35 s after she landed.
  20. **Fear Ward before the pull** (`UpdateOutOfCombatAI`: a priest in a raid wards every tank first; the ward lasts
     10 min) **and on every tank** (`TrySpecFearWardTank`: once the boss tank and its backup are warded the rest get one).
     Deployed with the MC week 0 night (from ~01:50, `MAXWIPES=1`). `r3-resume.sh onyxia|mc WEEK END` continues a night on
     the same lockout after a between-wipes fix (retry.sh deploys the build-dir mangosd).
- **MC week 0 (from 01:54, one wipe per run, fixed between):** Lucifron killed 03:22 (attempt 3), Magmadar 03:49 (1, 8 dead),
  Gehennas 04:04 (1, 0 dead). Wipes and fixes:
  21. Lucifron hound pack 1: two tanks ran 45 yd from the camp to the hounds on the paladin, 10 yd off Lucifron's walk;
     he walked in. **CampHold**: a tank is let off the camp only when its own victim is at the camp too (SuiAutopilot.cpp).
  22. Hound pack 2 (pulled with Lucifron 112 yd off, held at the camp) took 167 s: the tanks' own Heroic Strike / Shield
     Slam / Revenge floored their hounds one at a time (kin at 58-71 %, each rose again). **DamageOnFlooredRiser**
     (AiBotAISpecCombat.cpp TrySpecSpell): no direct-damage spell on a riser at <= 20 % while a kin stands above 20 %;
     Sunder/Taunt still go out. Next pack: 53 s and 60 s, 0 dead.
  23. Garr (393 s, 15 %): 4 rogues inside a dying Firesworn's Eruption - told to step out at 20 %, but Magma Shackles
     (-60 % speed) held them there until it died 3 s later; Massive Eruption from 6 min finished the raid. **Death-burst
     circles count from 35 % (50 % while the member is slowed)** (AiBotAICombat.cpp hazard list). An earlier "Garr wipe"
     (100 %) was really the Molten Destroyer pair + Annihilator + two patrolling Ancient Core Hounds drifting into his room.
     Result: **Garr KILL 04:44 (attempt 2, 200 s, 0 dead)** - five Firesworn had died in the wipe and stayed dead.
  24. Baron Geddon (65 s, 98 %, 40 dead): a hunter's cat bit him at his walk point 25 yd from the Firewalker pack (the
     lure is 42 yd from it, clearance 35) before he reached the camp; the pack joined at +1 s. The owners were held at the
     camp (CampHold), their pets were not. **HoldPetAtCamp** (AiBotAICombat.cpp, used by the legacy hunter/warlock pet
     order and CommandSpecPet): while CampHold holds the owner, its pet follows and drops a target > 15 yd off the camp.
  25. Geddon attempts 2-3 (98 %, ~60 s, 40 dead each): he noticed the raid from ~38 yd while running past the Firewalker
     pack (spawns 91261-64, ~10 yd from his walk points 15/16); creatures call for help 10 yd around them 1.5 s later, so
     the pack joined at +2 s. (The Firewalker's own AI then zone-combats the raid.) **Hot walk**: in the playbook-lure
     plan, every walk point within 20 yd of an idle pack is "hot"; the camp and the raid's rear spot must stand beyond
     his aggro reach (+6 yd) of all of them; the camp search reaches 38 yd from the lure. The plan notes the counts.
     Result: 49 % and 67 % (~2 min fights); the pack then joined mid-fight (+47 s, +93 s) with healers 21-29 yd from it
     (CollectIdleHostiles credits an elite 28 yd, but ordinary healer moves do not consult it). **R3W0 MC = 4/10.**
  26. **Idle reach is fire for healers and casters** (AiBotAICombat.cpp step-out routine): in a raid fight a healer or
     ranged damage dealer inside CollectIdleHostiles' reach (elite 28 yd, wander circle, patrol route) steps out of it like
     out of a ground hazard. Built 05:51, live from the R3W1 Onyxia night.
- **R3W1 Onyxia: KILL 06:10, attempt 1 (344 s, 11 dead)**; council Halo of Transcendence -> Ashbeard, Ring of Binding and
  Helm of Wrath -> Stormdoom (worn). 27. Council: a tank is never offered a two-handed weapon (Earthshaker was "handed" to
  the tank Stormdoom at every council since Magmadar and stayed in his bags; no 2H mace user in the roster).
- **R3W1 MC night 1 (06:19-08:43): Lucifron (att. 2), Magmadar, Gehennas, Garr (att. 2) = 4/10**, then two trash wipes on
  the Geddon pack 91286-89 (2 Lava Elemental + Firewalker + Flameguard; 40/40 and 38/40 dead, pack at 33-100 %). This
  pack has wiped the raid in most lockouts since 11-03. Telemetry: the elites sat on healers and warlocks, who stepped
  "out of the front" every 1-2 s and cast almost nothing; the warriors' **Taunt is melee range** (rangeIndex 2) and was
  "gate" ~2000 times per log, ok 0-1, since at least 11-05 - the chasing tanks never reached. Firewalker and Flameguard
  are immune to Banish (only Lava Elementals can be).
  28. **PeelToTank** (AiBotAICombat.cpp, called before AvoidFrontalCone): in a raid a healer or ranged damage dealer that a
     non-boss elite is hitting (within 12 yd) walks to the nearest living tank (45 yd), finishing a cast unless under
     50 % health, and stops once the mob is within 6 yd of the tank; `[AIBOT-PEEL]` lines. Nobody steps out of the front
     of the mob that is hitting them any more (it turns with them). The `[AIBOT-TAUNT]` diagnostic now prints every taunt
     spell (it printed only Mocking Blow, which never fits Defensive Stance). Built 08:50, live from the R3W1 MC resume.
     Result: the Geddon pack and the Lava Reaver pack then died clean (0 dead). Taunt's refusal is confirmed as reach.
  29. **Idle reach is a hazard for every raid role** (item 26 widened to tanks and melee). Geddon wiped at 60 % and 100 %
     because the tanks stepped out of his Inferno to (637,-818) and (641,-808), inside his Firewalker pack's reach (joined
     +117 s, +44 s). Built 09:18. **Result: BARON GEDDON KILLED 09:29 (attempt 3) - R3W1 MC 5/10**; Flameguard Gauntlets
     -> Stormdoom, Seal of the Archmagus -> Gankreaper (worn).
  30. **The front-dodge refuses an idle boss's whole roam** (AvoidFrontalCone adds CollectIdleBosses, 35 yd + wander):
     Shazzrah's pack 91269-71 was pulled to a camp 51 yd from his home; a mage's cone step to (622,-808), 40 yd from it
     (he wanders 10; the elite credit was 28 + 10), woke him at +13 s - 40/40 dead at 100 %. Built 10:00. Also open: a
     107 s trash wipe on Geddon's own Firewalker pack 91261-64 after his kill (09:50), not yet analysed.
     Result: Shazzrah's pack died in 23 s. Shazzrah 18 % (423 s) and 40 % (260 s) at 680-808 raid dps.
  31. **Repeated threat wipes hold 4 s** (AiBotAIMain.cpp ThreatHoldFor / BossThreatWatch.shortWindow): Shazzrah's Gate
     wipes threat every ~45 s and each wipe held every damage dealer "tanks first" for up to 30 s (411 holds in one
     attempt). Guides fight him as a damage race; a boss's second and later wipes within 120 s now hold 4 s. Pulls,
     landings (Onyxia) and the first wipe keep the full window. Built 10:27, live from R3W2.
- **R3W1 MC RESULT = 5/10** (Lucifron, Magmadar, Gehennas, Garr, Baron Geddon). R3W2 Onyxia night from 10:28.
- **R3W2 Onyxia:** wipes 23 % and 29 %. Attempt 1: the paladin tank Bubbleguy cast Divine Shield at 23 % at +16 s
  while holding her; she left him for the next on her list (four non-tanks in 12 s) and one Flame Breath hit 21
  (the R3W1 kill had the same bubble at +16 s and survived it).
  32. **No Divine Shield under a boss** (AiBotAISpecCombat.cpp UpdateSpecCombatPaladin; legacy UpdateInCombatAI_Paladin
     never bubbles a tank): a paladin tank held by a world boss uses Lay on Hands below 25 % instead; under ordinary
     elites the bubble stays (they keep swinging at it). Built 11:05.
  Attempt 2 (45 % in phase 2, logged 73 %): seven rogues and healers on one whelp took one Fireball's Engulfing Flames
  together, 15 dead in a second. Attempt 3 (15 %, no bubble): in phase 3 the healers "closing on" the tank walked from
  her tail across her front; one Flame Breath killed 8 at 21-40 yd.
  33. **Melee spread over the adds while the boss is airborne** (AiBotDoctrinePlayerParty.cpp ResolvePartyFocus): a melee
     whose kill-order pick is a small add already held by two melee takes the nearest same-kind add with fewer.
  34. **A healer's closing walk never crosses a breather's other arc** (AiBotAICombat.cpp heal-range closing): the
     path is sampled every 2 yd; it may leave the arc it starts in (front or tail) but not enter the other one.
     Built 11:31.
- **R3W2 Onyxia: KILL 11:55, attempt 6 (386 s, 8 dead)** (attempt 5 23 %). Council: Nightslayer Bracelets -> Wyntank,
  Vambraces of Prophecy -> Clawknight, Helm of Wrath -> Boomwarrior, Nemesis Skullcap -> Earthzor (worn). Open:
  Earthshaker still goes to Stormdoom's bags every council (he counts as Arms melee off duty; his Arms profile wants a
  2H axe), Ancient Cornerstone Grimoire stayed in Xanvale's bags. R3W2 MC night from 11:58.
- **R3W2 MC:** two trash wipes on Firelord pairs (+86 and +211 Lava Spawns; a won pair took 156 s against 11-51 s in
  W0/W1). PeelToTank (item 28) fired 87 times on Lava Spawns - elite summons - so healers and mages walked instead of
  casting while the spawns multiplied.
  35. **PeelToTank only for real elites** (no temporary summons, nothing with a creator, no known multipliers; the older
     "X is on me - to tank" rule still covers Firelords). Built 12:34.
- **OWNER LAW 2026-11-15 12:40: raid tactics are contained** (`shared_docs/RAID_TACTIC_SCOPE_LAW.md`). Every tactic is a
  row in Core `SuiBots/SuiTacticScope.h` (raid maps + boss/pack entries) and applies only through `SuiTactic::On`; the
  old behaviour stays everywhere else. Items 21-35, the Fear Ward pair and the council's no-2H rule were all moved
  behind rows (12 Molten Core, 5 Onyxia, the council on both); `bash tools/raid-tactic-scope-check.sh` PASS and
  negative-tested. Built 12:43, live from the next MC retry. The item 35 regression (peel reaching Lava Spawns) is the
  kind of thing the law prevents: peel is now only Geddon's four elite kinds on map 409. Pre-11-15 behaviour is still
  generic and is migrated when its fight is next worked on.
- **R3W2 MC (12:04 → 16:04): 5/10** - Lucifron (att. 2, 157 s, 7 dead), Magmadar (att. 2, 164 s, 2 dead), Gehennas
  (75 s, 0), Garr (att. 2, 248 s, 13), Baron Geddon (164 s, 9). Wipes: two Firelord pairs (item 35), Geddon's Firewalker
  pack twice (14:48, 15:31), Shazzrah 15:44 44 % after 305 s at **646 raid dps**: 154 damage-dealer and 28 healer threat
  holds, most of them "mine 0, tank 0" (the 11-14 Onyxia hold rules - a zero-threat list holds everyone) while Arcane
  Explosion did 452k. The 11-04/11-06 Shazzrah kills (149 s) were before those rules.
  36. **Shazzrah is a damage race** (registry `DamageRaceNoThreatHold`, map 409, entry 12264; ThreatHoldFor and
     AiBotHealerThreatHold skip him): no threat holds on him at all, as the guides fight him (tanks taunt after Blink and
     Gate). Built 15:48; scope check PASS, 18 tactics. Not yet checked: whether mages' Remove Lesser Curse actually lands
     on Shazzrah's Curse (the explosions hit 1,000 then 2,000 - doubled); dispels are not logged.
     **Result: Shazzrah KILLED 15:57, attempt 1 (246 s, 12 dead, 0 holds). R3W2 MC = 6/10.** `[AIBOT-DISPEL]` log added
     (diagnostic) for the curse question.
  37. **Peel stops outside Fire Shield** (`PeelOutsideFireShield`, 409 / Flameguard 11667): a peeled healer/caster stops
     11.5 yd clear of every Flameguard on its line to the tank (Fire Shield 19627 = 249 fire / 2 s within 10 yd) - three
     Geddon-pack wipes (14:48, 15:31, 16:12) had the peeled healers dying at the tank's feet.
  38. **Tanks stage for Onyxia's landing** (`TanksStageForLanding`, 249 / 10184; `AiBotAI::StageForLanding`): while she
     is airborne under 45 % her two top-threat tanks walk to her script landing spot (-8.86, -212.75), main tank 7 yd
     toward the nearest wall, backup 4 yd beside. R3W3 att. 2 wiped at 9 % (she landed 66-88 yd from every tank);
     **att. 3 KILL 17:04 (331 s, 8 dead).**
  39. **Banish the pack's elementals at the pull** (`BanishPackElementals`, 409 / Lava Elemental 12076, Lava Reaver
     12100; `AiBotAI` warlock banish): R3W3 MC took 5/10 by 19:27, then the Firewalker + Flameguard + two Lava Elemental
     packs before Shazzrah wiped the raid at 19:15, 19:38 and 19:56 (also three times on R3W2). The old banish waited
     for more elites than half the six tanks and skipped targets three people hit, so it fired only after a Lava
     Annihilator joined. The DB shows the elementals/reavers can be banished but the Firewalker/Flameguard cannot. As
     the guides do it, a warlock now banishes each of them at >= 40 % while a non-banishable elite still fights the raid.
     Resumed on the same lockout at 20:00 (until 21:13). **Result: Shazzrah 20:09, Golemagg 20:35, Sulfuron 20:46 -
     R3W3 MC = 8/10**; the next seven Lava pack fights were all wins (0-5 dead). Majordomo attempt 1 broke off at 94 %.
  40. **The 11-14 threat rules are Onyxia's** (`OnyxiaThreatDiscipline`, 249 / 10184; ThreatHoldFor and
     AiBotHealerThreatHold). Owner question 11-15: why did MC fall from 8/9 a night (rerun 1) to 4-6/10? Rerun 1 had no
     Lava pack trash wipes; rerun 3 had about 11. The Onyxia dev rounds' threat rewrite (item 1: tanks-first windows of
     up to 30 s at every boss pull and threat wipe, backup tank, melee 75 % / ranged 80 % caps) and the healer hold
     (item 18: heals only under 40 % during that window) applied to every MC boss and tank-held trash elite. Everywhere
     but Onyxia the rerun 1 rule is back: a damage dealer holds at 90 % of whoever holds the mob (now logged
     "90 %"; the old rule never logged, so rerun 1's "0 holds" was not a measurement). Peel (item 28) was already
     scoped to MC's four Lava pack kinds at 12:43; the drill measures it.
  41. **Drill mode** (owner 11-15: "rerun this stuck area until we have cleared it 8/10 times ... any 8 clears within a
     rolling 10 fight window ... that's got to be our strat going forward"). `.autopilot drill <leader> on X Y Z R | off |
     reset | status` (SuiAutopilot.cpp: Pullable refuses creatures outside the circle; an empty circle walks the leader
     there, waits for the raid, respawns its dead non-boss spawns and forgets them as cleared; `DrillScore` writes each
     pack fight to `run/etc/sui-autopilot-drill.txt`; clear = leader alive and <= 5 dead; 8 of the last 10 logs
     `[DRILL] PASSED` and pauses). `scratch/autopilot/drill-loop.sh LABEL X Y Z R [hours] [reset]` recovers, restarts and
     re-arms after each wipe. First drill: the Geddon-Shazzrah Lava packs, 115 yd around (655, -835), on the finished
     R3W3 lockout, from 21:01. Drill kills are QA, not progression. **Result: PASSED 21:29, 9 of the last 10** (11
     fights, 2 respawn rounds of 27 and 19 creatures; 0-2 dead in each clear). The one fail (fight 9, 10 dead) was a
     double pull: a neighbouring pack's Flameguard (91279) joined at +4 s "after we hit it" and the melee died in two Fire
     Shields - watch for it on the real nights.
  42. **R3W4 MC Geddon** (Onyxia att. 2 kill; MC Lucifron/Magmadar/Gehennas/Garr att. 1-2). Geddon att. 1 98 % (48 s):
     the walk to the Firewalker pack 91290 crossed his patrol, the back-off began at the leader's 51 yd while a rogue was
     9 yd from him; pack 91261-64 joined. Att. 2 20 % (339 s): each Inferno step pulled him after his tank and nothing
     walked him back - 90 yd south in 140 s, onto the healers at (660, -830), a corner; at +145 s ten (all 4 tanks, 5
     healers) logged "in the fire and no way out found". Fixes (MC + entry 12056 only): `TankHoldsBossAtCamp` (the tank
     he is on walks back to `FightAnchor` = the camp between pulses, `[AIBOT-TANK] ... walks Baron Geddon back to the
     camp`, AiBotAICombat.cpp `AvoidGroundHazard`) and `PatrolBackoffWholeRaid` (the timed-walker back-off also when any
     member is within 50 yd, SuiAutopilot.cpp). Build 00:41, scope check PASS 24. Att. 3 56 %: the drag-back held him
     13-24 yd from the camp for 2 min, then a mage's step after a Living Bomb stopped 29 yd from pack 56722-24 (28 yd
     credited) and it aggroed -> `WideIdlePackReach` (MC Lava pack kinds 34 yd). **Att. 4 KILL** (144 s, 3 dead).
     Shazzrah att. 1 100 % (751 dps, 141 threat holds - the 11-15 Onyxia scoping fell back to the 90 % hold *before*
     the `DamageRaceNoThreatHold` check; fixed, 0 holds after) and att. 2/3 15 % / 32 % (1036 / 834 dps). Across 16
     logged Shazzrah fights every kill ran 1290-1787 raid dps and every recent wipe 646-1036; early rogue deaths do not
     decide it (a kill lost 7 rogues by +60 s). Deaden Magic (19714, magic, -51 % damage taken, recast 7-14 s) halves our
     casters; `TrySpecPurgeEnemy` should take it off but never logged, so `[AIBOT-PURGE]` now logs casts and misses
     (diagnostic). **W4 MC = 5/10, the 9/10-by-W4 target missed.** Shazzrah is the next stuck boss: check the purge log
     first, then the DPS gap (gear, rotation gates), and drill him if he keeps wiping.
  43. **R3W5** (2026-11-16). Onyxia att. 1 34 % (Deep Breath on the tanks staged on her landing spot from 45 %) ->
     `TanksStageForLanding` threshold 42 %; **att. 2 KILL** (549 s, 14 dead). MC: Lucifron 40/40 - the timed-walker
     back-off kept walking toward his Core Hounds (`MoveToSafe` gives no order while moving) -> `BackoffAwayFromWalker`
     (replaces the walk, 20 yd straight away; verified 52 -> 83 yd); Garr 98 % (a Firesworn planned as trash) and 94 %
     (the paladin tank walked out with the puller, six Firesworn killed him at the pull, banishes broke) ->
     `GarrTanksAtCamp` + Firesworn 12099 in `BanishPackElementals`; **Garr att. 4 KILL** (180 s, 6 dead), **Geddon att. 1
     KILL** (225 s, 11 dead), **Shazzrah att. 1 KILL** (273 s, 18 dead, 1290 dps, 25 purges of Deaden Magic, 22 misses
     at 23-30 yd). Then the leader re-chose Lava Surger 56847 ("no clear way to an ambush") 1786 times for 35 min and
     never reached Golemagg -> `SkipUnambushablePatrol` (third time running = skipped 15 min). **W5 MC = 6/10.**
     Loot: the council's picks sat "in bags" (an Arms warrior scored every non-axe -100000, so his sword+shield read
     -200000 and Staff of Dominance / Earthshaker +10^7 %; and the 2H judged against the main hand alone) and
     Helm of Wrath / Belt + Sabatons of Might / Eskhandar's Collar were lost to "Sylwhisper has no bag room" ->
     `CouncilWearsChoice` (rejected pieces are no upgrade; the council puts the piece on in the chosen slot, a full-bag
     member's old piece goes to the holder). Build 08:17, scope check PASS 29. W6 Onyxia from 08:18.
  44. **R3W6** (2026-11-16). Onyxia att. 1 20 %: the tanks taunted her in the air, Taunt was on cooldown when she
     landed with her threat wiped, a hunter held her and one Flame Breath killed 6 -> `SaveTauntForLanding` (no taunt on
     her while airborne); **att. 2 KILL 329 s, 1/40 dead** (best Onyxia of the rerun). Council: Sylwhisper's bags held 80
     pieces of vendor trash and a warlock with a staff got three Grimoires -> `CouncilWearsChoice` throws one grey item
     away for room and gives no off-hand beside a two-hander; from Lucifron on 22-24 pieces a council went on, all worn
     (checked in `character_inventory`). **MC = 9/10, every boss on attempt 1** (Lucifron 110 s 0 dead, Magmadar 138 s,
     Gehennas 78 s, Garr 225 s - 40/40 after his death, the Firesworn tanks died at 5-2 % with the healers dry -,
     Geddon 142 s, Shazzrah 208 s 1692 dps, Golemagg 120 s 0 dead, Sulfuron 148 s, Majordomo 143 s). Ragnaros not
     reached: the night ended 11 min after Majordomo. Time lost: a 09:10 trash wipe (a Firelord pull woke a second
     Firelord, a Surger and an Ancient Core Hound; Lava Spawns double every 10 s - EventAI Split - and reached +117) and a
     12:06 one (the Lava Surger skipped by `SkipUnambushablePatrol` walked into the next pack) -> a skipped patroller
     within 60 yd of the next pack now times that pull like a walking boss (build 12:51, PASS 30). week-loop.sh read
     Majordomo's submit ("boss-alive", "(boss )") as a stop: now his encounter field (instance data 9 = 3) makes it his
     kill, so the cache, the council and Ragnaros follow in the same night. Rag-by-W6 MISSED; next W7.
  45. **R3W7** (2026-11-16). Onyxia att. 1 33 % (her tank turned her head to the nearest wall, 28 yd west, where the
     healers stood; two Flame Breaths killed 6) -> `WallAwayFromRaid` (the wall with the fewest members inside her
     50-degree cone); **att. 2 KILL** (346 s, 6 dead). MC 5/10 on attempt 1 each (the skipped Surger now times the next
     pulls - "pulled while the skipped Lava Surger walks away" - no Surger wipe); Shazzrah att. 1 10 % (the fight record
     says 44 %: read after his regen), att. 2 31 %: five priests dead by +50 s to Arcane Explosions doubled by his Curse
     (19712 radius 30 yd); the mages' 30 yd decurse never reached the priests (1-4 decurses each, paladins 13-18) ->
     `HealersDecursedFirst` (healers' curses x3; a decurser walks to a cursed healer, 22 yd from it and 33+ yd from him).
     `drill-loop.sh` respawns only non-boss spawns, so a boss's drill is the weekly attempt. `deploy.sh` now waits for
     `/tmp/sui-build.lock` and `build-locked.sh` builds under it (no more restart/build races).
  46. **R3W8** (2026-11-16). Onyxia att. 1 18 % (Flame Breath hits up to 59 deg off her facing, the raid 21-39 yd in
     front of her - phase 3 is still the Onyxia wall), **att. 2 KILL** (402 s, 5 dead). MC: Lucifron wiped twice - the raid
     waited at a camp on his walk ("waiting for Lucifron to walk away", camp 91 -> 67 -> 43 yd) -> `BackoffAwayFromWalker`
     widened (closing inside 60 yd: the leader leaves the camp 30 yd away and re-plans; seen on Geddon 19:48); night
     resumed 18:19. **Shazzrah att. 1 KILL** (240 s, 10 dead, no healer dead; 6-10 mages walked to the same priest ->
     one decurser per healer, 6 s claim, build 20:27). **MC = 8/10** (Golemagg, Sulfuron); Majordomo not reached, budget.
  47. **R3W9** (2026-11-16/17). Onyxia att. 1 33 % and att. 2 wipe: every phase-3 wall turn swept her Flame Breath over
     members already moving out -> `NoTurnAfterLanding` (below 40 % her tank holds her where she faces); **att. 3 KILL
     282 s, 2/40 dead** (resumed night). MC 4/10 (Lucifron att. 2, Magmadar, Gehennas, Garr att. 2), stopped 00:51:
     a Core Hound pack's fight closed while hounds lay down and Lucifron joined the risen pack -> `RiserKeepsFightOpen`
     (a hound lying down alive keeps the fight open); a two-Firelord pull ran away into 200+ Lava Spawns (both nights it
     began with a second Firelord woken on the run back); Garr 6 % (the paladin tank at the camp took all six Firesworn at
     +1 s - also W8); Geddon 24 % after a Lava-pack wipe with a Surger joining. Open: two-Firelord pulls, Garr's first
     second, the 4 h budget (a clean night ends near Majordomo).
  48. **R3W10/R3W11** (2026-11-17). Onyxia NO KILL both weeks: W10 26 % (a phase-2 Deep Breath killed 6, 3 healers) and
     29 % (whelps killed 4 healers); W11 33 % and 11 % (a Deep Breath at +131 s killed 6, then phase-3 breaths). The open
     Onyxia work is phase 2: a whelp team (off-tanks + AoE at the cave mouths, the guide's answer) and the Deep Breath
     escape (6 dead per breath twice). W10 MC 6/10 (Lucifron..Shazzrah; Garr's first pull again lost the paladin tank at
     +1-8 s - his Consecration took all six Firesworn -> `NoConsecrateNear` (MC 12099), build 03:22), stopped on two 40/40
     wipes on the Lava pack pair at (847-891, -979..-995) near Golemagg (also W8) -> drill DRILL-GOLEPACKS PASSED 9/10
     (05:26; its one fail = both packs at once from a camp 12 yd from one). W11 MC 0/10: a spurious 1 s "wipe" (7 dead,
     06:21) and then a 595 s Core Hound pack fight before Lucifron (07:13, 33/40, 1.5M damage, 27 "[AIBOT-RISER] ... is
     down" - the last hounds at 4-6 % never died inside the 10 s rise window, the pack rose at full again and again until
     the healers were dry). **Next:** the Core Hound pack's kill timing (all five to the floor together, then the last
     ones burst inside 10 s) - `RiserKeepsFightOpen` (build 23:04) keeps such a fight open, so a failing balance now
     shows as one long fight instead of a closed fight and a rise on the walk-off; check whether to keep it.
  49. **R3W12** (2026-11-17). **Onyxia KILL att. 1** (489 s, 7 dead). MC 3/10 (Lucifron, Magmadar, Gehennas). Core Hound
     burst (08:24): the last two at 5-6 % outlived the window while ~60 target switches went out in 3 s - each bot's share
     (`low[guid % size]`) reshuffled at every fall -> `RiserBurstFocus` (MC 11671: a bot stays on its low hound, then all
     on the lowest left; build 08:25, unproven). Garr: `NoConsecrateNear` made it worse (98 %: Firesworn + Garr on the
     healers at +6-9 s - the paladin's Consecration had been holding them) -> REVERTED 09:26; att. 2 45 %. The paladin
     tank dying at the pull is the price of holding the Firesworn; the real answer is more holders at the pull (Challenging
     Shout / more tanks on Firesworn) - open. BWL (due from W12) is not prepared: no playbook, no attunement check.
  50. **R3W13** (2026-11-17). Onyxia KILL att. 2. MC: `RiserBurstFocus` WORKS (a five-hound pack down in 8 s, two hound
     packs won in 106 s with 0 dead). **Lucifron is now the #1 blocker** (wipes W8 x2, W9, W13 x3; attempt 1 kills W5-W7):
     he walks through his hound packs' room; the planner loops "Lucifron patrols past Core Hound - the boss goes first" /
     "Core Hound crowds the ambush of Lucifron - it goes first"; the camp back-off logged "the raid leaves it" while the
     leader never moved (the next tick re-planned the same camp -> 6 s `planHoldMs`, build 11:19); then an unplanned pull
     (a hound pack aggroed 22 yd from the camp) brought him and five hounds. Next: a clean Lucifron plan from the guide
     (he and his two Protectors are pulled first, at a camp off every hound pack's reach, the packs after him), and check
     whether the W8 camp back-off should stay.
  51. **R3W14** (2026-11-17). **Onyxia KILL att. 1** (429 s). **MC 9/10**: Lucifron now has a playbook lure (12118:
     at (1000,-945), camp (955,-928) -> searched (973,-918), clearance 20 yd - 25 refused for a pack at 22 yd; in
     run/etc AND src/mangosd sui-raid-playbook.json.dist) -> **met first, KILL att. 1** (192 s, 8 dead, no hound pack
     joined); his hound packs after him with 0 dead (`RiserBurstFocus`). Garr att. 2; Geddon, Shazzrah, Golemagg,
     Sulfuron att. 1; Majordomo counted by the week-loop submit check (cache opened, council). Ragnaros not reached: the
     night's 4 h ended 12 min after Majordomo. Open: Garr's first pull, Onyxia phase 2, the 4 h budget for Ragnaros, BWL.
     **Ragnaros attempts (first of rerun 3): 49 % (182 s) and 64 % (145 s, the retry ran past 16:19).**
  52. **R3W15** (2026-11-17). Onyxia KILL att. 2 (att. 1 at 3 %). MC 0/10: (a) 17:33-17:50 stall - a skipped Lava
     Surger was the timed walker of its own pull, "Lava Surger comes near on the walk to Lava Surger - backing off" every
     second -> no back-off from the pull target itself (build 17:50); (b) the resume's restart was REFUSED (retry.sh ->
     restart.sh without --force: "dead or away from the entrance"), so the old binary ran on - check md5 after every
     resume; forced at 18:27; (c) a Core Hound pack + imps wipe at (966,-898) near the Lucifron lure camp; (d) Lucifron
     via the lure (19:03, pulled to (973,-918)) wiped at 100 % in 177 s - look at that fight (joins? his Protectors? the
     camp vs pack A at 982-1002,-965..-979) before W16's Lucifron.
     -> it was pack A joining: the lure clearance had been lowered 25 -> 20 yd at 13:03 W14, so the lure was taken with
     pack A alive. Back to 25 (W14 order: pack A first while he is away, then the lure) in both playbook copies.
  53. **R3W16** (2026-11-17). Onyxia KILL att. 2. MC 2/10 (Lucifron: pack A first, then the lure - KILL att. 1; Magmadar).
     Night stops that were not the raid: a risen hound killed the leader (1 s, 1 dead) -> week-loop.sh now revives + resumes
     a "wipe" with <= 5 dead; a map 509 DungeonReset line stopped the loop -> waitreview.sh stops only for maps 409/249.
     A third two-Firelord runaway (+223 spawns) -> `SpawnerFirstForRanged` (MC 11668: the ranged kill the Firelord first,
     the melee keep the spawn down; build 22:19, unproven). Some Core Hound packs still take 6 min (358/370 s) before
     they fall together. Garr 29 % ended the night (two wipes). W17 from ~22:5x.
  54. **R3W17** (2026-11-17/18, the mission's last week). **Onyxia KILL att. 1** (399 s, 5 dead). MC: Lucifron (pack A
     first + lure), Magmadar, Gehennas, **Garr att. 1**, Geddon - 5/10 by 01:18, then a stall from 01:37: "waiting for
     Ancient Core Hound to walk clear of the camp" (camp (643,-764) by Shazzrah) - the 300 s ambush wait skips him only 5
     min, nothing else is pullable, so it waits again. Next fix: time the next pulls around him like the skipped Surger
     (`SkipUnambushablePatrol`'s timed-pull block) instead of the ambush wait. Mission status: Onyxia kills every week
     (attempt 1 in W12, W14, W17); MC best 9/10 (W6, W14); Ragnaros attempted once (W14: 49 % / 64 %), never killed;
     BWL not started (no playbook, no attunement).
- **Guide items still not built:** a dedicated whelp team (off-tanks at the two cave mouths + mage AoE) in P2/P3; the
  wall facing is lost when she lands away from her P1 spot (18: "no wall within 30 yd" after she moved 20 yd).

### 0.28 RERUN 2: ONYXIA FIRST EVERY WEEK (2026-11-13 14:29 → 11-14 09:07, STOPPED by the owner: wrong schedule, see §0.29)
**Owner (11-13):** "Starting week 0 the raid should also be doing Onyxia ... start with Onyxia week 0, then go to MC, and
every week start there to reinforce the gear improvement. Let's start week 0 again, back to pre-bis, start Onyxia."
Rules as §0.24 plus: week 0 = Onyxia only; every later week = fresh Onyxia + MC lockouts, **Onyxia first for at most
1 h** (my call: one ~4 h night per week, so Onyxia cannot eat the MC time), then MC to 4 h after the start.
Tally lines `R2W0`, `R2W1`, ... in `scratch/autopilot/week0-setup-1111.log`.
- **Pre-raid reset (SETUP 14:25):** `/tmp/raid_loot_held.py` (box, read-only; generalises `mc_loot_held.py` to every raid
  map 409/249/469/309/509/531/533 + rewards of quests that need a raid-only item, e.g. Head of Onyxia) found 117
  raid-only items on 32 members (9 worn; 22 Fiery + 12 Lava Cores, 3 Tomes of Tranquilizing Shot); taken, 9 slots
  refilled by `/tmp/mc_refill_pick.py` (own bag piece ≥ ilvl 55, else best 60 dungeon rare). After `.saveall`: 0.
  Commands kept in `scratch/autopilot/r2w0-strip-cmds.txt`. Learned data and playbook kept.
- **Helpers:** `recover.sh`/`restart.sh` take `RAID=onyxia` (entrance (29,-71) map 249, tele `onyxia`); `week-loop.sh`
  knows Onyxia, `STOP_AFTER_KILL=1`, and checks END as a box epoch (nights cross midnight; it used to start one more
  attempt at END). `r2-week-start.sh W` (revive, everyone to `OnyxiasLair`, install + restart, `newlockout` 249 and
  409, raid into Onyxia, autopilot on); `r2-night.sh W [ONYMIN=60] [MCWIPES=4]` runs a whole week night.
- **Core fixes (generic):**
  - **`.autopilot equip` never destroys** (11-13 morning: full bags had destroyed three earned pieces during the FR swap):
    with no bag room the slot keeps its piece and the item is refused. Tested on a new throwaway bot with a full
    backpack (15/15 shirts accounted for). Build 4d405b42.
  - **A walker that timed out at its pull spot is closed on the next time** (`walkerTimedOut` in `SuiAutopilot.cpp`):
    week 0 lost 75 min (14:29-15:44) to Onyxian Warder 52050 - 48 waits of 90 s, "stands in the way" re-picking it.
    Week 1: one timeout, then closed on; the five Warders died in 7 min. Build 1b3528b0.
  - **Tank gather** (`TankDuty`, `AiBotDoctrinePlayerParty.cpp`): a tank holding a small (non-elite) add takes the next
    small add loose on the raid. Onyxia's records (18 fights 11-13): 685 of 717 deaths in phase 2; whelp melee 362,
    Fireball 198 (120 splash on a neighbour), Deep Breath 106 (bunches of 5-6 at one second); 75-200 whelps a fight,
    all killed. From week 2 on. **No measurable effect:** whelp time on tanks was 14-61 % (avg ~43 %) before and
    31-61 % (avg ~45 %) after - the "2072 s vs 329 s" first quoted was one fight (19:55), not the average.
  - **The real phase-2 gap (found 11-14):** Onyxia loses ~2.5 % per 5 s on the ground and ~0.2 % per 5 s in the air
    (60 -> 53 % in five minutes). The ranged log "focus -> Onyxian Whelp (kill order)": `KillPriority` lists foes
    within 45 yd, and she hovers/moves round the room beyond it. Patch (built after week 2): a ranged damage dealer
    also lists a world boss in combat within 100 yd (whelps on itself still first, the peel rule).
    **No measurable effect either** (week 3: 4-10 % lost in phase 2's first 3 min vs 8-9 % in week 2): the ranged
    now saw her, but whelps still out-ranked her - 2956 "focus -> Onyxian Whelp (kill order)" in week 3's hour.
  - **The cause (fixed for week 4):** `KillPriority`'s rankOf returned 10 (last) for ANY world boss in the fight
    whenever one was present - the rule was written for "the boss that orders its adds" (Majordomo, the only
    playbook `killOrder`). Now the boss goes last only behind playbook-ordered or **elite** adds: every MC boss's
    adds are elite (Firesworn, Protectors, Flamewakers, Sons of Flame), so MC is unchanged; Onyxia's whelps are rank 0.
  - Seen in passing: `sui-autopilot-rebounders.txt` holds Ragnaros 11502 and Majordomo 12018 since 11-10 20:22 (false
    "heals itself whole" - they evade/submerge). Effect: no splash counted on them, kill-order last while adds live.
- **Week 0 (14:29-18:42):** Onyxia **0/13**, left at 57 48 50 59 58 48 60 47 57 53 50 61 53 % (best 47 %). Phase 3
  (40 %) never reached. Attempt 13 began 4 min past END (fixed).
- **Week 1 (18:50-22:52):** Onyxia **0/5** in her hour (54 58 50 52 50 %); MC **4/8**: Lucifron (1), Magmadar (2),
  Gehennas (1), Garr (2), all 0 rezzes inside the fight; wipes Magmadar 22 %, Garr 58 %, Geddon 98/97/72/73/? %
  (MAXWIPES reached). Rerun 1's week 1 (4 h of MC, no Onyxia) was 5/8.
- **Week 2 (23:02-03:04):** Onyxia **0/4** (53 50 52 57 %); MC **5/9**: Lucifron (1), Magmadar (1), Gehennas (1),
  Garr (3), Baron Geddon (3). The loop first stopped MC at 00:30 on an imp-room trash wipe (it retried only boss
  wipes) and logged an Onyxia wipe as a stale "Baron Geddon 73 %" line (it read the `--night` list, which mixes
  renumbered instances) - both fixed in `week-loop.sh`, MC resumed 00:36. Rerun 1's week 2 was 5/9.
- **Week 3 (03:14-07:14):** Onyxia **0/5** (57 46 50 56 **45** %); MC **6/9**: Lucifron (1; he died, the fight ended
  38/40 dead), Magmadar (1), Gehennas (1), Garr (2), Baron Geddon (1), Shazzrah (1). All 0 rezzes inside the fight.
  Rerun 1's week 3 was 8/9 - with no Onyxia hour.

### 0.27 WEEK 4 NIGHT 2 (2026-11-13 06:54 → 10:50, same lockout, instance 100) — MAJORDOMO KILLED (9/9), RAGNAROS 0/18, BEST 45 %
**Pick up here:** owner "Yes, second night". Build **b1c1de06**, no code changes. State at 10:50: autopilot OFF, raid
recovered at Ragnaros, FR kit swapped back (0 FR pieces after `.saveall`), lockout data `3 3 3 3 3 3 3 3 3 0 ...` (Rag
open until the weekly reset 2026-11-13 23:00). Tally lines `W4N2` in `scratch/autopilot/week0-setup-1111.log`.
**Majordomo:** attempts 1–3 wiped (best 4 of 8 adds dead); **attempt 4 killed, 07:41:25**: all 8 adds dead,
13/40 dead, 0 rezzes inside the fight, data index 8 = 3. Rerun weeks: W3 needed 2 nights for 9/9, so does W4.
**Cache of the Firelord** (SETUP): the leader's ghost was far away, so the first `.autopilot loot` opened a Dark Iron
Deposit (1 Dark Iron Ore, slip). After revive + `.tele name Boomwarrior ragna2` it opened the cache: Gloves of the
Hypnotic Flame → Doctormaster (equipped), The Eye of Divinity → Ashbeard (the give scorer kept it in bags, worn with
`.autopilot wear`), Wild Growth Spaulders kept in Boomwarrior's bags (no druid in the raid).
**Ragnaros:** FR kit on at 07:44 (155 pieces). `LABEL=W4N2 rag-loop.sh` 1–14 then 15–18: **18 wipes**, Ragnaros at
59 59 67 51 60 55 56 66 49 59 **45** 51 64 46 58 55 49 95 % (median ~57 %; attempt 18's 95 % is an outlier). Raid DPS
2.3–3.7k/s, fights 137–194 s, 38–40/40 dead every time: the same wall as W3N2 (44 % best), DPS against ~1.1M health
and healer mana. The FR kit again made no measurable difference.
**Gear slip found at the swap-back:** `.autopilot equip` DESTROYS the replaced piece when no bag has room (Core
`SuiAutopilot.cpp` `stash`). Three own pieces were gone (Tooth of Gnarr on Wildsap and Chargesniper, Jungle Ring on
Keldfel), not in mail; when (W3 or W4 equip) is unknown because `Server.log` resets at restarts. Re-granted with
`.autopilot equip` (SETUP). Fix before the next FR swap: refuse the swap (or mail the piece) when the bags are full.

### 0.26 WEEK 4 (2026-11-12 13:55 → 17:59, fresh lockout) — 7/9 INSIDE THE 4 H BUDGET, SULFURON 1 MIN PAST IT (8/9)
**Pick up here:** owner "start week 4". Fresh lockout by the §0.1 recipe (`tele-all TheMoltenSpan` → `newlockout
Boomwarrior 409` → leader in → `tele-all mc`): instance 101, group bind 115 → 101, renumbered to **100** at the first
restart. Own rerun loot kept; the FR kit is off (it goes on for Ragnaros only). Build **b1c1de06**, no code changes this
night. State at 17:59: autopilot OFF, raid at Sulfuron's spot, lockout data `3 3 3 3 3 3 3 3 0 0 ...` (8/9). The global
weekly reset is **2026-11-13 23:00**, so a second night for Majordomo and Ragnaros has to happen before then.
**Result:** Lucifron (attempt 1, 0 dead), Magmadar (2, 35 dead), Gehennas (1), Garr (2, 384 s), Baron Geddon (4, 1 dead),
Shazzrah (1, 21 dead), Golemagg (1, 0 dead) inside 13:55:55–17:55:55; Sulfuron (1, 0 dead) pulled at ~17:57. Every kill
had 0 rezzes inside the fight (`bosswin.sh`). Rerun comparison: W1 5/8, W2 5/9, W3 8/9 (4 h), **W4 7/9 (+Sulfuron)**.
The 23 trash fights to Lucifron were all wins, the imp room included.
Wipes: Magmadar ×1 (16 %), Garr ×1 (75 %), Geddon ×3 (97, 96, 9 %), trash ×2 after Geddon (Firewalker packs; one let a
Firelord join with his Lava Spawns). Geddon's open problem is unchanged: on attempts 1–2 the Firewalker/Lava Elemental pack 38 yd
west of the lure (`clearance` 35 leaves it) was in combat by +2–5 s.
New helper `scratch/autopilot/week-loop.sh W HH:MM:SS [END] [MAXWIPES]`: a boss kill is rez-checked, logged and resumed;
a boss wipe is logged and retried up to MAXWIPES times in a row; anything else, or passing END, stops it. END is checked
only after a kill, which is why Sulfuron was pulled past the budget.

### 0.25 WEEK 3 NIGHT 2 (2026-11-12 09:25 → 12:25, same lockout, instance 100) — MAJORDOMO KILLED (9/9 + Rag 0), RAGNAROS BEST 44 %
**Pick up here:** owner "Yes, second night" (Majordomo, then Ragnaros, in the rerun's week 3 lockout). State at 12:25:
autopilot OFF, all 40 revived (SETUP) in MC, lockout data `3 3 3 3 3 3 3 3 3 0 ...` (Majordomo DONE, Ragnaros not),
build **b1c1de06** installed = running (under gdb), the fire resistance kit swapped back 12:50 (own pieces worn again, FR pieces removed; `scratch/autopilot/rag-fr-unequip-cmds.txt`, re-equip with `rag-fr-equip-cmds.txt`), nothing committed.
Tally lines `W3N2 ...` in `scratch/autopilot/week0-setup-1111.log`.

Core fixes (all generic, uncommitted; backups `/tmp/bak-1109/*.pre-cn`, `*.pre-co`):
- **cn — rune state** (`SuiAutopilot.cpp` NextScriptedObject): a loaded scripted object counts as used when its state
  differs from its spawn's `go_state`, not when it is "not READY". MC's runes spawn ACTIVE (startOpen) and a douse flips
  them to READY, so the old test skipped every undoused rune and after Sulfuron the leader explored instead.
- **co — remembered sheep** (`AiBotAISpecCombat.cpp` TrySpecPolymorphSpareElite): `gMySheep` (mage -> target, under
  `gPolyClaimLock`) puts the mage's own last sheep back into the renew search. It used to be found only by a 45 yd
  search, so three of four mages fighting at the Elites' spot never renewed theirs and a Healer woke at +57 s
  (attempt 2). With co all four renew it, walking in to 14–30 yd when needed.
- **`.autopilot equip` keeps the replaced piece in the bags** (destroyed only with no room). New verb
  **`.autopilot wear NAME id...`** puts carried pieces back on by id (paired ring/trinket slots handled).

Majordomo: 7 attempts. 1–6 wiped with 40 dead: all four Elites by 83–111 s, then 0–1 Healer. **Attempt 7 killed all
eight adds in normal combat** (E34 E53 E71 E90 H160 H208 H233 H262, 17 dead; 10:58:37; 0 rezzes inside the fight;
encounter 8 = 3). The remaining wall is the four immune Healers waking together; melee take Shadow Shocks in the lava.

SETUP 11:01, the Ragnaros fire resistance kit (owner: pre-raid obtainable only; `scratch/autopilot/rag-fr-equip-cmds.txt`):
Wildfire Cape 12905 (BRS), Drakefire Amulet 16309 (Onyxia attunement quest), Elemental Circle 17001 (Ramstein quest);
legs Wizardweave 14132 (cloth), Volcanic 15054 (rogues), Black Dragonscale 15052 (hunters), Runic Plate 12614
(paladins); warriors keep their legs. Left out on purpose: Onyxia Scale, Chromatic Cloak (BWL), Hydraxian rings (need MC).
155 pieces worn; the replaced ones are in each member's bags — swap back with `.autopilot wear`.

Ragnaros: 8 attempts, all wipes in 162–209 s: Ragnaros left at 88, 55, 60, 55, 52, 49, 56, **44 %** (2.6–3.6k/s; the
historical best stays 34 %, §0.23). The deaths (attempt 8): ranged and healers die to **Intense Heat** (Might of Ragnaros
on a random mana user, 2000 + knockback, 20 yd, every 9–14 s) on top of **Lava Burst** (waves of three every 15–20 s at the
nine fixed spots in `boss_ragnaros.cpp`; the radius covers the room, so it is a healing load, not something to dodge) and
**Wrath of Ragnaros** reaching ranged. The tanks die to Elemental Fire around 190–220 s, then Magma Blast. Paladin healers
are at 1–2 % mana by ~100 s. The FR kit did not change the outcome measurably (best 44 % vs 34 % before, within
variance). Conclusion: raid DPS (~2.8k/s against ~1.1M health) and healing throughput, not tactics. A kill wants
damage and healing gear from more weeks; the lockout resets 2026-11-13 23:00.

### 0.24 CLEAN RERUN, WEEK 0 → 3 (2026-11-11 12:50 → 11-12 04:24) — DONE: W1 5/8, W2 5/9, W3 8/9
**Pick up here:** the rerun ended week 3 at its 4 h budget with **8/9** (Majordomo and Ragnaros not reached). State at
04:24: autopilot OFF, the raid at Sulfuron's room in week 3's lockout (instance 100 after renumbering, data `3 3 3 3 3 3 3 3
0 ...`), build **2163911b** installed = running (under gdb), nothing committed. Owner decision pending: a second night in
week 3's lockout (Majordomo + Ragnaros, as the original weeks used several nights per lockout) or stop here. For Ragnaros
the pre-raid fire-resistance gear (owner-approved 11-11) goes on before his pull - not chosen yet (see §0.23 for the summon
/ playbook setup: `rag-ready.sh` does NOT fit a mid-lockout continuation, it resets the encounters; use `retry.sh`).
**Tally:** week 1 5/8 (13:09-17:22), week 2 5/9 (3 h 50 min around the honor restart), week 3 8/9 (00:27-04:24) - every
kill checked rez-free (`bosswin.sh`, log `scratch/autopilot/week0-setup-1111.log`). Original weeks for comparison: week 1
1 boss on night 1 (8/8 over three nights + a morning); week 2 4/9 on night 1 (8/9 after night 2); week 3 4/9 on night 1.
Fixes found by the rerun, all generic: ci, cj, ck, cl (below) and cm (week 3, the two-Firelord packs: a multiplier's
spawns trigger neither "clear" steps nor splash spreading - 120-200 Lava Spawns had the casters moving most of the fight;
after it two Firelords died in 13-45 s). Week 3 wipes: two Firelords x2 (before/after the first half of cm), a Core Hound
pack that took 356 s (won, leader died), Garr x1 (20 %), Geddon x3.
Owner (11-11): **"allow fire res gear that is obtainable as pre-raid, yes. However ... a clean week 0 to week 3 rerun now,
so that we can verify what has been done thus far."** Answers: week 0 = a prep week before week 1; **MC loot REMOVED**
from the raid; learned files and the playbook KEPT. Week rules as 11-03 (§0): a fresh lockout + one ~4 h night per week,
retries allowed, review stop after every boss/wipe, gear only from that rerun's own kills; week 1 = bosses 1-8, week 2 =
1-9, week 3 = 1-9 + Ragnaros (pre-raid fire resistance gear put on for Ragnaros only, as raids swap it).
- **Week 0 (SETUP, 12:50-13:08, log `scratch/autopilot/week0-setup-1111.log`):** MC-only items = everything reachable from
  MC creatures'/chests' loot tables (references expanded) minus anything reachable from loot outside MC
  (`/tmp/mc_loot_held.py` on the box, read-only): 166 entries; the raid held 158 instances on 36 members (34 equipped, incl.
  tier 1, epics, Fiery/Lava Cores, Tome of Tranquilizing Shot, plans). All taken with the new **`.autopilot take <member>
  <item ids...>`** (equipped + bags + bank). 34 emptied slots refilled with `.autopilot equip` (`/tmp/mc_refill_pick.py`):
  the member's own bag piece of that slot if item level >= 55, else the best RARE from 60 5-man dungeon loot (BRD, LBRS/
  UBRS, Scholomance, Stratholme, Dire Maul) of its armour type / the removed weapon's subclass, scored for its raid role.
  New **`.autopilot unlearn <member> <spell>`**: Tranquilizing Shot (19801, learned only from the MC tome) off Fensap,
  Aelfury, Renewlock. Verified after: 0 MC-only items on the raid (Testwar holds none). Testwar untouched.
- Build **e7927cee** (= 073628ea + take/unlearn + the GamePay rate hooks), deployed 13:03.
- **Week 1:** `newlockout` 13:08 -> **instance 101** (fresh: no data, no cleared spawns), all 40 inside, autopilot on 13:09.
  Its fight records start at line 487 of `sui-autopilot-fights.jsonl`. NOTE: vmangos renumbers instances at every startup,
  so after the first restart the week's instance is **100** again and `fights.py --night` merges it with the old week-3
  lockout's records - the week log (`week0-setup-1111.log`) is the tally.
  **WEEK 1 RESULT (13:09 -> 17:22): 5/8 - Lucifron (attempt 2), Magmadar (1), Gehennas (1), Garr (3), Baron Geddon (7)**;
  Shazzrah attempt 1 (begun past the 4 h budget) 31 %. Wipes: imp room (Lava Surger + a whole Flame Imp group), two
  Firelords' Lava Spawn swarm, Lucifron's Core Hounds rising + his patrol, Garr x2 (room Destroyers fought in place beside
  him; 6 of 8 Firesworn died in attempt 2 and stayed dead), Geddon x6 (neighbour packs joining; twice Shazzrah). Every
  kill: no rez inside the fight (`bosswin.sh`). Original week 1 (11-03): 1 boss on night 1, 4/8 after night 2.
  - ci. **An idle world boss's reach is never a fire-escape spot** (`CollectIdleBosses` in `AiBotAICombat.cpp`: 30 yd round
    it and 35 yd + as far as it has roamed round its home; the escape spot search skips them; other idle mobs keep the +100
    score). Found twice in Geddon's fights: a healer's and the off-tank's "out of the fire" steps pulled Shazzrah (all 40
    dead). Builds 42887311 -> **bf36fb14**. Geddon died two attempts later.
  - cj. **The cross-map teleport crash, found** (`Assertion in GetMap: m_currMap`, 11-10 x2 and 11-11 17:27 while moving
    the raid into week 2's instance; gdb `/tmp/mangosd-gdb-1111-1647.txt`): `CombatBotBaseAI::SelectHealTarget` (a bot
    healing out of combat) called `pMember->GetMap()` on a member BETWEEN maps (no current map -> assert -> abort). 25
    group-member comparisons `X->GetMap() ==/!= me|leader->GetMap()` in CombatBotBaseAI, AiBotAIMain/SpecCombat/Combat,
    SuiAutopilot, SuiRaidTelemetry rewritten as `X->IsInMap(me|leader)` (IsInWorld + FindMap, never asserts). Build
    **470b716d**. The 2 s teleport gaps in `tele-all.ps1`/`recover.sh` were only making it rarer.
- **Week 2 (fresh lockout 17:32, instance 101 -> renumbered 100):** **RESULT 5/9 - Lucifron (attempt 3), Magmadar (1, 28
  dead), Gehennas (1), Garr (2, 25 dead), Baron Geddon (6, 11 dead)**; Shazzrah attempt 1 98 %. Played 17:32-19:15 and
  22:16-00:23 (3 h 50 min): the owner's normal **honor restart at 19:15** (mangosd exit code 2) stopped the server - under
  the gdb launcher nothing restarts it, so it stayed down until 22:07; the Gehennas pull in progress was void, and trash
  that respawned during the outage chased the raid to the entrance (one wipe, not a pull). Fixes found by week 2:
  - ck. **A trash patrol walking past a standing pack goes first, planned pull or not** (`TrashPatrolPast`; the old
    patrol-first rule only ran for a crowded PLAN): the imp-room wipe repeated in week 2 exactly as in week 1 (no plan, a
    Lava Surger's walk woke all 16 imps); after it "Lava Surger patrols past Flame Imp - it goes first" and the room went
    clean. Build df54f2df.
  - cl. **The Living Bomb walk also keeps out of idle bosses' roam** (`CollectIdleBosses` added to its spot exclusion): a
    mage carried Geddon's bomb into Shazzrah. Build **9767f0f1**; Geddon died next attempt.
  - Open: Lucifron's Core Hound pack crowding his ambush is pulled unplanned and he often joins (2 wipes each week);
    Geddon's neighbour packs join as Inferno moves the fight (5-6 tries each week).
  - Helpers (git-ignored): `retry.sh` (recover x2 + restart + autopilot on), `waitreview.sh HH:MM:SS [maxmin]` (block to the
    next review pause / a restart / mangosd gone, print the fights since), `bosswin.sh <label>` (rez check of the last
    fight, log, resume).

### 0.23 RAGNAROS (2026-11-10 19:58 → 23:10, same lockout, instance 100) — 13 ATTEMPTS, NO KILL, STOPPED AT A GEAR WALL
**Pick up here (supersedes §0.22):** state at 23:10 - autopilot OFF, all 40 alive at the MC entrance, instance 100 data
`3 3 3 3 3 3 3 3 3 0 …` (the real result: Majordomo DONE 11-09, Ragnaros not), runes 16-22 = 3, installed = running =
build **073628ea** under gdb (no crash since the bw fix), playbook in run/etc = src. Lockout resets **2026-11-13 23:00**.
Last attempt A13 (Fire Resistance Aura on all four paladins): 54 % left after 151 s. Nothing committed.
**Next (owner decision):** Ragnaros needs fire resistance / healing beyond this roster's own-loot gear (see "Why no kill"
below) - either allow that (FR gear, a bigger healer share) or leave Ragnaros for later weeks. Without that, the next
levers are small: static ranged spots instead of the reactive spread (Intense Heat), and a tank-swap rotation before
Elemental Fire stacks kill the tank. A run of attempts: `rag-ready.sh <playbook>` + `waitpause.sh`, ~10 min each.
Owner: **"once that's done, tackle rag, do not stop to ask me".** Per attempt: `scratch/autopilot/rag-ready.sh <playbook>`
(recover, encounter 8 = 3 + runes = 3, deferrals cleared, playbook into run/etc AND src, restart, autopilot on; refuses to
enable after a refused restart) then `waitpause.sh`. A restart usually removes a standing Ragnaros (not always - attempt 2
found him), so each attempt starts from the summon. **The summon works end to end** (by - talk to the friendly Majordomo in
the chamber, 3 options, Ragnaros rises, kills Majordomo, becomes fightable).
- Attempt 1 (build 0c0f7150, planned pull from the west platform): **Ragnaros 100 -> 34 % in 162 s** (66 % in ~120 s, 36 of
  40 alive at 119 s), raid DPS 4.6k. Collapse at 119 s: ONE Intense Heat killed ten stacked ranged in the same second.
  Then Lava Burst, tanks dead to Elemental Fire (six tanks gone 65-147 s), and with no melee in reach Magma Blast wiped.
  After it, "fights" 2-7 = the web brain rezzing bots at their corpses beside him (out of combat), dying again.
- Mechanics (boss_ragnaros.cpp, spell data): **Might of Ragnaros** every 9-14 s on a random MANA user summons a Flame of
  Ragnaros that casts **Intense Heat 21155: 2000 fire + knockback, 20 yd** (one-shots mages at 1.7-2.3k). Elemental Fire
  aura on him = fire DoT on whoever he hits (the tank killer). Magma Blast 6000 when nobody is in melee reach. Wrath of
  Ragnaros = melee knockback. Lava Burst = trap objects at 9 fixed spots (`PositionOfLavaBursts`), burst once on spawn - the
  bot hazard code already skips them (trap radius 0, cooldown 0). Submerge at 3 min (8 Sons of Flame, 90 s). Rooted.
- ca. **Playbook `spread`** (per boss, yards; `SuiRaidPlaybook::SpreadYards`, joins the learned splash in the bots' spread
  rule in `AiBotAICombat.cpp`): Ragnaros 20. Attempt 4: no Intense Heat deaths at all.
- cb. **No rez beside a living hostile raid boss** (bridge RESURRECT gate, 100 yd) - ends the post-wipe death loop.
- cc. Summon step: never a setup move beside a boss already standing (cell search at the NPC's spot, `FindNearPoint`); after
  the gossip the raid **waits until the boss is in combat** (it rises immune, not unselectable); no rune step once the next
  boss is gossip-summoned and never a rune whose live object is used (the leader had teleported round all seven doused runes).
- cd. **`summonGossip.standAt`**: where the raid waits and pulls from = the west platform (815, -838, -229). Attempt 4
  (gathered by Majordomo on the east side, on three Lava Burst spots, inside the knockback): 79 %, ranged cast 15 % of the
  time (34 % moving: "clear"/"hazard"), tanks dead by 78 s.
- Builds: 0c0f7150 -> f8f4c6f0 (ca, cb) -> 063f9852 -> d33a22ba (cc) -> c5709096 (cd). `recover.sh` now teleports BEFORE it
  revives (revived beside a standing Ragnaros, members pulled him before their teleport).
- **Open walls:** the tanks (5.8k health, no fire resistance) die to Elemental Fire + melee in 30-80 s each, six tanks gone
  by ~150 s; a kill needs ~190 s at attempt 1's pace AND the submerge at 180 s.
- ce. **`standInPulse`** (playbook flag; `AiBotAICombat.cpp` pulse-keep): the raid does not keep clear of the boss's learned
  pulse circle. Ragnaros' Wrath (25 yd, ~1000 + knockback, every ~26 s) was learned as a pulse after attempt 1; keeping 26 yd
  off him made the ranged shuttle between 26 yd and their reach ("clear" steps 80/fight, cast 54 %).
- cf. **Heal stop-cast** (`AiBotAI::StopNearlyFullHeal`, called on every 4 Hz sub-tick, mid-cast included - the spec rotation
  is not called while casting, so the first version in `UpdateSpecCombatAI` never fired): a direct heal in its last 0.5 s
  whose target misses (health missing + the melee about to land - other healers' casts in flight) less than half of it is
  interrupted (`[AIBOT-HEALSTOP]`; mana is spent only on completion). Attempt 9: 54 stops, overheal 30-50 % -> 12-24 %.
- Results (Ragnaros % left / seconds / main killers): A1 34 / 162 (Intense Heat x10 at once); A2 81 (raid gathered beside
  him); A4 79 (east side); A5 63 / 130 (spread 20 + circle kept: Lava Burst x9); A6 68 / 155 (standInPulse, spread 20:
  Intense Heat x16); A7 56 / 146 (spread 10); A8 47 / 150 (same, 3.97k/s); A9 54 / 129 (+ heal stop: Intense Heat x16, Lava
  Burst x9, Wrath x5). Builds faeb8e96 (ce) -> 5d44b8aa -> 780ce6c7 (cf).
- A10 54 / 179 (spread 15; first to reach the submerge - Sons of Flame at ~180 s); A11 42 / 185 (same + prePotions); A12 68 /
  143 (identical to A11). **Variance between identical attempts is ±15-25 points** - single attempts cannot rank small
  changes; best so far A1 34, A11 42, A8 47.
- cg. **`prePotions`** (playbook item list; drunk by every member during the summon wait, `DrinkPrePotions`, log line counts
  worn/cooldown/none/busy/refused): Ragnaros 13457 Greater Fire Protection Potion. Finding: 37 of 40 already WORE the
  shield before the pull (the quartermaster's potions, drunk earlier) - not the missing lever.
- ch. **`paladinAura`** (playbook spell ids, highest rank first; every paladin's `m_spells.paladin.pAura` set during the
  summon wait): Ragnaros Fire Resistance Aura 19900/19899/19891 (the bots pick one of seven at random otherwise).
- Build 634b8d92 also carries the GamePay session's rate hooks (`.sui rates`, XP/rep/honor; inert without conf keys) -
  coordinated with that session, it verified `hooks=1`. Builds 5ebcbc86 (cg diagnostics) -> 073628ea (ch).
- **Why no kill (the honest read):** fire damage on 1.7-2.3k-health casters (Intense Heat 2000 within 20 yd of a random
  mana user every ~11 s - 26 mana users within ~33 yd of him cannot all be 20 yd apart - plus Lava Burst ~1000 at 35 yd)
  and the tanks' 30-80 s lives under Elemental Fire. A kill needs ~240 s at the best pace plus the Sons phase. That is a
  gear (fire resistance) / healing-throughput wall, not a positioning bug.

### 0.22 The kill crash + Ragnaros summon step (2026-11-10 16:20 →, same lockout, instance 100) — batch C done (2/10)
**Pick up here (supersedes §0.21's "Pick up here"): every Majordomo "kill" of 11-10 is VOID.** The web app's living-world
brain (switched on by the web app at 10:23) answers each bot death with a RESURRECT ~15-25 s later, and the Core rezzed the
bot at its corpse **mid-fight**: B1 had 118 deaths and 118 rezzes inside the fight, B3 56/56, B4 575/575 (Sylwhisper died
five times in one pull). So A1, A3, B1, B3 (all "8 of 8") and B4/B5 (7 and 5, 12-16 min fights) were not normal-rules
attempts. The 11-09 runs had 0 rezzes (brain off): §0.20's 0/10 and attempt 89's kill stand.
- bz. **RESURRECT held while the raid fights** (`AiBotAIBridge.cpp`, RESURRECT handler): a bot in a raid group in a dungeon
  ignores RESURRECT (rez and ghost port) while any living member on its map is in combat; logs `[AIBOT] X: RESURRECT held -
  the raid is fighting` (once per 30 s per bot). The autopilot's regroup revives between fights as before, and the bot's own
  fallback recovers in place once the group is out of combat. Build **0c0f7150** (18:00) = b2d6c1d9 + bz.
- `domo-verify.sh` now prints `rezzed N held N deaths N` (since the run's start; rezzes AFTER the fight ended are normal) -
  a run is valid only with no rez timestamp inside the fight. **Batch C (C1–C10) started 18:02** on 0c0f7150.
- **Batch C result: 2 of 10 valid kills** (every rez checked: all at/after the fight's close; no crash in any run, two
  kills included = bw verified). C1 E47 E121 E134 E170; C2 E19 E46 E67 E90 H173; C3 E28 E55 E108 E140; C4 E26 E72 E84 E111;
  C5 E48 E64 E84 E145 H210; C6 E19 E38 E58 E91; **C7 KILL** E22 E78 E93 E119 H153 H178 H232 H269 (17 dead); **C8 KILL** E83
  E103 E124 E174 H226 H253 H271 H308 (6 dead); C9 E19 E37 E53 E93; C10 E44 E53 E75 E95 H143. Compare §0.20: 0 of 10. The wall
  is unchanged in the eight wipes: the four Elites die, the four Healers wake Polymorph-immune and their Shadow Shocks
  (top damage source, 130-190k) kill the raid in 30-60 s. Overheal 33-60 % per healer.
- Owner (11-10, during batch C): **"once that's done, tackle rag, do not stop to ask me".** 19:58: lockout put back to the
  real result (`rag-ready.sh`: encounter 8 = 3, runes 16-22 = 3, deferrals cleared), the Ragnaros playbook installed in
  BOTH `run/etc` and `src/mangosd` (src is now in sync = Elites first), restart, autopilot on.
B1 (old build c870a2c5) was a "kill" (E36 E54 E75 E99 H225 H251 H298
H329) and mangosd **crashed at the kill** (16:21:02), the same moment as the unexplained 14:46 SIGSEGV after A3's kill. gdb
(`/tmp/mangosd-gdb-1110-1609.txt`): `AiBotAI::UpdateSpecCombatAI -> Unit::HasBreakableByDamageCrowdControlAura` on a
**dangling cast target**. Majordomo's adds are `TEMPSUMMON_DEAD_DESPAWN` (deleted the moment they die); a member still
casting or auto-shooting at the last add keeps the spell's raw `m_targets.getUnitTarget()` pointer until its spell updates,
and the bot's "stop a damaging cast on a sheeped target" loop (and four other spots) dereferenced it.
- bw. **`CastTarget(me, cast)`** (`AiBotAISpecCombat.cpp`): every bot read of a cast's target looks it up by guid on the map
  (null once removed); no raw `getUnitTarget()` left in SuiBots. No combat behaviour change, so B1 still counts.
- bx. **`doneData`** (playbook, `SuiRaidPlaybook::BossDead`): a boss whose instance data index reads DONE (3) is beaten -
  for bosses that yield or are script-summoned. Code only; the data goes in after the batch (below).
- by. **`summonGossip`** (playbook) + `UseSummonGossip` (`SuiAutopilot.cpp`, idle branch before the rune step): when the
  first living boss has one and nothing else is left, the leader goes to the NPC (setup move, like the runes), the raid
  gathers 20 s (Regroup), the leader picks option 0 `selects` times from the NPC's default menu, then holds 3 min while the
  boss is NOT_SELECTABLE; once it is fightable the normal fight/pull logic takes it. Untested live.
The batch loop was stopped, build **b2d6c1d9** (= c870a2c5 + bw-by) installed 16:30 under gdb, and **B2–B10 restarted
16:33** from this session (`domo-verify.sh B$i`, results `scratch/autopilot/domo-B*.txt`, read as §0.21 says). Backups
`/tmp/bak-1109/*.pre-1110d`. **B2 = harness failure:** B1's leader died 3 times on an Elite, the area deferral is written to
`run/etc/sui-autopilot-deferred.txt` with a **120 min** wall-clock expiry (the log line says "30 min" - the in-memory
deferral is 30, the file 120; a small inconsistency, not fixed) and survives the restart, so B2's raid stood 20 min "in a
deferred zone". `domo-verify.sh` now empties both deferral files before the restart (appended to `*.qa-cleared`). Loop
restarted 17:00 as **B3–B11** (ten real runs = B1 + B3–B11).
**Playbook found out of sync:** the installed `run/etc/sui-raid-playbook.json.dist` (13:55, Elites first: killOrder
[11664, 11663]) is newer than `src/mangosd/sui-raid-playbook.json.dist` (Healers first) - a `make install` would have put
Healers first back. The Ragnaros draft (Majordomo `doneData` 8; Ragnaros 11502 `doneData` 9, `summonAt`, `summonGossip`
npc 12018 at (847.1, -816.2, -229.8) selects 3) is built on the INSTALLED file, in this session's scratchpad
(`rag/sui-raid-playbook.json.dist`); after the batch it goes to both places.

### 0.21 Majordomo fixes + 10-run batch B (2026-11-10 11:10 → 16:10, same lockout, instance 100) — see §0.22
Owner (11-10): **"Fix Majordomo first"** (then Ragnaros), and **"Yes, pull them back"** (the raid bots into MC, see below).
**Pick up here:** a 10-run verification batch (B1–B10) was started 16:08 on build **c870a2c5** (installed = running, mangosd
UNDER GDB, see ops) from the previous agent's session: `for i in 1..10: scratch/autopilot/domo-verify.sh B$i >
scratch/autopilot/domo-B$i.txt`. If that session is gone the loop is gone too - check `ls -t scratch/autopilot/domo-B*.txt`
and `pgrep -af run/bin/mangosd` on the box, then restart the missing runs. **Read the results:**
`grep -a "KILL\|over after\|^3 \|start\|NOT\|ERROR" scratch/autopilot/domo-B*.txt` - a run is a KILL when its last line before
"start" reads `3 3 3 3 3 3 3 3 3 0 …` (index 8 = 3) AND it has eight `KILL` lines; `3 … 3 0 0 …` = wipe; no KILL lines + "NOT
RESTARTING"/"Connection refused" = harness failure (not a run). Compare with §0.20 (0 of 10 on the old build).
Probe runs on this build before the batch: **A1 KILL** (E17 E45 E60 E80 H317 H355 H406 H442, 3 dead), **A3 KILL** (E17 E47 E60
E81 H121 H193 H231 H255); A2 and the first A3s were harness failures (no Majordomo, fixed below).

What happened first (11:10–12:25): at 10:23 the web app started a living-world run (`.bot add Aerhorn, Aggrocrits, …` over
RA) and its brain gave every bot GRIND tasks - the raid roster included, because the raid group has no human online (Testwar
left it 11-09), so `pparty` was 0. Boomwarrior hearthed out of MC ("stranded escape") and the 40 spread over the Burning
Steppes, Duskwood, Kalimdor, Winterspring. My `recover.sh` teleports then crashed mangosd twice (`Assertion in GetMap:
m_currMap`, 11:13 and 12:15, both on back-to-back cross-map `.tele name X mc`); with 3 s between teleports under gdb, no crash.

Fixes (Core, uncommitted). Letters continue §0.19:
- bs. **A raid group is the raid's, not the brain's** (`AiBotAIBridge.cpp`, STATE `pparty`): a bot in a raid group reports
  pparty=1, so the C# brain holds it Idle. The web app never forms raid groups. Verified: after the restart the brain sent the
  40 only IDLE.
- bt. **Free tanks stand by the sheeps as soon as one add is left awake, at any health** (`SleepersMayWake(around, anyHealth)`;
  the tank branch passes `!PlaybookReleaseTarget(me)`, so Healers-first releases are untouched). §0.20 V10: the last Elite
  died 14 s after the third and only one tank reached a sheep.
- bu. Diagnostics: `[AIBOT-TAUNT]` (why a taunt was not cast; first spell of the list only - Mocking Blow is always "form 86",
  wrong stance, ignore it) and `[AUTOPILOT] nearest idle hostile X: <reason>` (which `Pullable` check refuses the nearest idle
  hostile within 100 yd when the pick finds nothing). **Taunt is NOT broken**: the live probe `.autopilot why <tank> 355`
  shows canTry=1 in reach and ready=0 after it fired; the `355:gate` counts are refusals out of reach.
- bv. **Script-summoned bosses: `bosses.<entry>.summonAt [x,y,z]`** (playbook, `SuiRaidPlaybook::BossSpawn` falls back to it
  when a boss has no spawn row) - Majordomo `[758.089, -1176.71, -118.64]` in both `.dist` files. Without it exploration knew
  only spawn rows and never walked to his room. NOTE: a summonAt boss counts as alive forever (`BossDead` cannot judge a
  summon), so after Majordomo is DONE the raid walks to his empty spot when idle - harmless now, must be solved for Ragnaros.
- QA verbs: `.autopilot unit <leader> <data64 id>` (MC 14 = Majordomo: alive/position/faction/flags/distance) and
  `.autopilot encounter` (§0.20). **`encounter` always WRITES** - there is no read-only form; to read a flag you must write it
  back (I wrote 9 into four flags once by mistake and restored them).
- **Harness facts (MC instance script)**: Majordomo is summoned the moment the last rune is doused (`GOHello_go_rune_MC`),
  hostile if encounter 8 ≠ 3, and sets DATA_DOMO_SPAWNED (23, not saved). Rune states 16–22 ARE saved/loaded (the
  `SetData(+16)` loop in `Load` is dead code, the stream reads them). After a kill all seven are saved doused.
  `domo-verify.sh` therefore sets, BEFORE the restart: 8 = 0, runes 16 17 18 19 21 = 3 (doused), 20 22 = 0 (Blaz, Mohn): the
  autopilot douses Mohn and Blaz, he appears, the raid walks to him via summonAt. `GridUnload = 0` on this server.

Ops changes (`scratch/autopilot/`, git-ignored): `recover.sh` and `tele-all.ps1` wait 2 s between teleports (crashes);
`tele-all.ps1` includes Stormdoom; `deploy.sh` tolerates a dead server and **starts mangosd under gdb when `~/sui-gdb-on`
exists on the box** (it does now): a crash leaves `thread apply all bt` in `/tmp/mangosd-gdb-<MMDD-HHMM>.txt` - read the newest
one after any "Connection refused". A **SIGSEGV at 14:46:10** (right after A3's kill, no trace - cpptrace is off) is unexplained;
the gdb run is there to catch the next one. `taunt-probe.sh` = live Taunt probe (not needed any more).
Backups: `/tmp/bak-1109/*.pre-1110a … pre-1110c`. Nothing committed.

### 0.20 Majordomo verification: 0 of 10 (2026-11-09 20:39 → 22:41, same lockout, instance 100)
Owner (11-09): **"rerun majordomo 10 times (pre kill), so that we can verify it, then we try rag."** Same build as the kill
(fixes bm–br) plus one QA verb, no behaviour change between runs. **Result: 0 kills in 10.** Adds killed per run (E = Elite,
H = Healer, s after the pull): V1 E19 E48 E66 E80 H124 H150; V2 E65 E81 E107 E140; V3 E91 E119 E136 E153; V4 E29 E54 E90 E117
H162; V5 E25 E45 E74 E139; V6 E33 E45 E69 E87 H142 H173; V7 E50 E76 E92 E112; V8 E24 E44 E70 E108; V9 E47 E54 E80 E102 H149;
V10 E28 E49 E66 E80 H148. So: **all four Elites die in every run; 0–2 Healers after that**; the raid collapses 30–60 s after
the wake-up, often several deaths in one 5 s window, and in several runs (V5, V8, V10) with the healers NOT out of mana. The
attempt-89 kill was the lucky tail. Overheal 30–59 % per healer in every run.
- **`.autopilot encounter <leader> <type> <value>`** (QA, `SuiAutopilot.cpp`): sets one encounter of the leader's instance and
  saves it. Order matters: on startup the MC script re-douses the runes itself and summons Majordomo at once — hostile only if
  encounter 8 already reads 0, otherwise the friendly one in Ragnaros' chamber — so the reset goes BEFORE the restart.
  `scratch/autopilot/domo-verify.sh <label>` = recover, `encounter Boomwarrior 8 0`, restart, autopilot on, wait, print kills
  and the instance data (results in `scratch/autopilot/domo-V*.txt`).
- After V10 the lockout was put back to the real result: `encounter Boomwarrior 8 3`, restart; instance 100 data =
  `3 3 3 3 3 3 3 3 3 0 …` (Majordomo DONE from attempt 89, Ragnaros not started). The verification kills would have been QA,
  not progression; none happened, and no Cache was opened.

### 0.19 MAJORDOMO DEFEATED (2026-11-09 19:20 → 20:20, same lockout, instance 100) — 9/9
Owner (11-09, answering §0.10 item 0): **"keep own loot, go week 3"** = healers stay on own-loot gear, keep going in this
(week 3) lockout. Attempts 86–89; **attempt 89 (pull ~19:59:40, last add 20:05:26) killed all eight adds in normal combat:
E22 E43 E70 E95 H170 H239 H258 H315, 15 of 40 alive at the end**. Majordomo yielded (stock script: `SetData(TYPE_MAJORDOMO,
DONE)`, evade, immune-to-player); `characters.instance` 100 data = `3 3 3 3 3 3 3 3 3 0 …` (index 8 = Majordomo DONE, 9 =
Ragnaros not started). The fight record says "wipe … Majordomo 100 %" because he never dies (fights.py does not know a
yield) — the instance data is the proof. Kills per attempt: 86 E86 E106 E121 E147 (one tank by the sheeps, wake-up lost);
87 E35 E54 E73 E92 (wake-up clean, four dead in 1 s to Shadow Shock 21 s later); 88 E20 E48 E81 E99 H129 (tanks died from 150 s,
healers dry); 89 the kill.

Fixes (Core, uncommitted). Letters continue §0.18:
- bm. **A held healer kind nobody can sheep any more is held like any elite** (`TankDuty`: `heldHealer` needs
  `!IsImmuneToMechanic(MECHANIC_POLYMORPH)`), and **a held elite is not left for a loose boss while that boss's own off-tank
  lives** (`bossTankAlive`). Attempt 85: tanks left their woken Healers for Majordomo after each threat wipe; the loose Healers
  killed four casters in 13 s.
- bn. **Free tanks look 80 yd for a sheep to stand by** (was 45: the tanks drag their Elites away from the sheeps, fix bk), and
  **`SleepersMayWake` fires with the last awake add under 50 %** (was 35 %: they walk 60 yd and the last Elite fell from 35 % in
  8 s). Attempt 86: one tank by four sheeps → 87: three tanks by three sheeps.
- bo. **A wounded melee leaves the whole burst circle and does so while chasing**: `!woundedMelee` skipped the pulse-radius
  keep (it stepped to 14 yd inside a 20 yd Shadow Shock), and the spacing step returned early whenever the bot was moving -
  a melee chasing a Healer the tank drags is always moving. Attempt 87: three rogues at 20–48 % took two more Shocks.
- bp. Telemetry: **overheal per healer** (`overhealByHealer`, `"overheal"` per member in the fight record, `overNN%` on the
  fights.py healer line). Attempt 89: 23–47 % per healer.
- bq. **`.autopilot loot <member> [restore]`**: bots loot only corpses, so the Cache of the Firelord (a chest) was never opened.
  First call: the member is moved to the nearest chest spawn on the map with a loot table (setup move; NOTE mining nodes are
  chests too - put the member closer to the wanted chest than to any node first, e.g. `.tele name X ragna2` for the cache);
  second call opens it the way `Player::SendLoot`'s chest branch does and stores the items in the member's bags. `restore`
  re-shows a chest the instance script spawned once and a server restart hid (vmangos keeps no state for it).
- br. **`.autopilot give <from> <to> <item id>`**: one item moves from bags to bags; the receiver runs its own `TryAutoEquip`.

**Loot (SETUP-labelled steps around our own loot):** the restart that installed bq hid the cache (spawntimesecs -600, spawned
only by `SetData DONE`), so `.autopilot loot Boomwarrior restore` re-showed it once (log: `chest Cache of the Firelord (spawn
362148) shown again after the restart (setup)`); its loot is rolled only on opening, so this is the same roll it would have
had. Contents: **Wristguards of True Flight (18812) → Fensap (equipped), Core Hound Tooth (18805) → Keldimp (he equipped it in
the MAIN hand over Dal'Rend's Sacred Charge — his own scoring; it breaks the Dal'Rend set and Sword Specialization, review),
The Eye of Divinity (18646) → Ashbeard (bags; priest quest item)**. Seen in passing: Galengaze and Noodlebandit carry Talisman
of Ephemeral Power (18820, a caster trinket) in their bags from earlier rolls.

State at the end: autopilot OFF (not enabled after the last restart), all 40 online in MC instance 100 at the entrance,
installed = running = build **8b6f7b8f**. Backups: `/tmp/bak-1109/*.pre-1109ad … *.pre-1109ah`. Nothing committed.

### 0.18 Majordomo, off-tank in tank gear + Elites first again (2026-11-09 14:20 → 16:25, same lockout, instance 100) — still 8/9
Owner (11-09, answering §0.10 item 0): **"the OTs probably don't have any epics yet, but they likely have their pre-raid
BIS"**. SETUP: `.autopilot equip Stormdoom 17705 12602 18289 13397 14620 18500 11669 13966 11810` = Thrash Blade, Draconian
Deflector, Barbed Thorn Necklace, Stoneskin Gargoyle Cape, Deathbone Girdle, Tarnished Elven Ring, Naglering, Mark of Tyranny,
Force of Will (all quality 3; Stockade Pauldrons is an epic, so the Valor shoulders stay; Blackhand's Breadth, an epic, is gone).
He keeps the Valor set and the Arms talents; health stays ~5.3k, but he held Majordomo 125-170 s in every attempt.

Attempts 77–85 (9), all wipes. Kills (E/H, s after the pull):
- Healers first: 77 E59 H122 H142; **78 H39 H93 H128** (40 alive to 85 s); 79 E79 E93 H161 (Boomwarrior dead at 9 s, one Healer
  never sheeped, so the fallback killed Elites); **80 H49 H94 H128**; 81 H93 H133. Raid dps 2.0-3.6k. In all five the healers
  were under 10 % mana from ~100 s and the raid collapsed at 130-150 s.
- Elites first (playbook switched back after 81): 82 E44 E44 E60 E83 (dps 6.6k, 40 alive to 83 s; nine dead in 15 s at the
  wake-up, the melee in overlapping Shadow Shock circles); 83 E18 E35 E52 E77 (wake-up: three dead, then the Healers loose on the
  casters); **84 E16 E42 E65 E102 H123 H166 = six of eight**; 85 E22 E53 E69 E100 H181 (**clean wake-up**: three free tanks
  at three sheeps, nobody dead for 27 s; then the four awake Healers took 81 s for the first kill, ranged 384 clear steps).
Installed = running = build **902508ee** (with bl, ran attempt 85). Backups: `/tmp/bak-1109/*.pre-1109u … *.pre-1109ac`, playbook
`/tmp/bak-1109/sui-raid-playbook.json.dist.pre-1109z`.

Fixes (Core, uncommitted). Letters continue §0.17:
- be. `.autopilot equip`: a second ring/trinket in the same call goes to the paired slot (it destroyed the first one).
- bf. **A released add stays "released" 10 min, not 60 s** (`PlaybookReleaseTarget`): the record is refreshed only while it
  sleeps; a woken Healer that took 117 s to die stopped counting at 60 s and the floor let its Elites die (attempt 77).
- bg. `[AIBOT-FLOOR]` probe: logs (5 s throttle) a melee/ranged pick of a later kind under the 45 % floor with every ordered
  add in reach (guid, hp, comb/cc/rel). It showed the fallback working as designed in 79 (no released add, no free tank).
- bh. **Sight moves**: a cast the game refused for LINE_OF_SIGHT (`m_losRefusedGuid/Ms`, set in `TrySpecSpell`) lets the
  ranged sight move run although the target is >45 yd from the anchor (Majordomo's Healers are tanked 50+ yd from the leader
  on the boss); when the straight path has no spot, a ring round the target (outside its burst and every other awake burster's,
  ≤29 yd, walkable, nearest 4 tried) is used; `[AIBOT-SIGHT]` when nothing is found. **Ranged LOS refusals 883 (79) → 95 (85)**
  against ~600 casts.
- bi. **The full-fight off-tank is the boss's tank** (`IsBossOffTank`; `TankDuty` returns the world boss first, no claim so the
  nearest tank still takes him while the off-tank walks back from a port). Before, `TankDuty` gave Stormdoom an Elite and
  Majordomo's resets put him on the healers (80: three healers dead in 25 s).
- bj. **Melee hold out of a second awake wide burster's circle** (`MaintainRangedSpacing` heldBurstMelee): while my target
  bursts ≥18 yd, another awake one within its reach + 4 yd holds me outside (82: six rogues dead at the wake-up).
- bk. **Tanks take held mobs away from sleeping bursters** (`MaintainTankFacing`): a held non-boss mob within a sheep's circle
  + 8 yd is walked 8 yd away from the sheeps' middle (TankStepOk rules); `[AIBOT-TANK] … takes X away from N sleeping
  burster(s)`. The raid follows its tanks, so the sheeps no longer lie among the casters at the wake-up.
- bl. **`SleepersMayWake` ignores awake adds of a sleeping kind**: one Healer broke its sheep early and was held 36 yd off; it
  counted as a second awake add, so no tank stood by the sheeps and nobody cleared their circles (84: six dead). After (85):
  `[AIBOT-TANK] stands by the sleeping …` for three sheeps and a clean wake-up.
- Data: Majordomo's `killOrder` is **Elites first** again ([11664, 11663]; reason appended to its `why`).

**The wall now is healer mana** (owner's gear rule): the nine healers have 3.2–3.9k health (own loot only), start at 100 %
mana and are under 10 % (`oom` in fights.py) by 80–110 s in every attempt, Healers first or Elites first. They already
downrank by deficit (Holy Light ranks 1–9, Greater Heal 1–5 all seen) and drink one mana potion (17531; the next is on
cooldown). At today's dps a kill needs 200+ s: Elites first = all Elites by 77–100 s, then four awake Healers; Healers first
= three Healers by 120–130 s, then four Elites.

### 0.17 Majordomo with an off-tank (2026-11-09 08:37 → 12:55, same lockout, instance 100) — still 8/9
Owner (11-09, asked at the start): **Shield Wall/Last Stand cooldowns: leave them, fix the pull**; **6th tank: "rather than a
tank, a warrior/druid who OTs"** → a NEW warrior bot; **run until Majordomo dies** (before the 11-13 23:00 reset).
None of the 39 is a spare warrior or a druid, so (all SETUP, labelled as such):
- **Stormdoom (guid 171)**, human warrior: `POST /Bots/AddBots {"spawns":[{"race":"human","cls":"warrior","count":1}]}`,
  `.character level Stormdoom 60` (the level-up repair learned Arms 31/20, Taunt, Defensive Stance, max skills),
  `.autopilot equip Stormdoom 16731 16733 16730 16735 16737 16736 16732 16734 15411 13340 13098 17713 11815 13965 12784`
  (Valor set + pre-raid blues + Arcanite Reaper - the Arms profile needs a two-handed axe; all quality 3 here).
- The raid group was full (39 bots + **Testwar, offline**): `.autopilot join Boomwarrior Stormdoom Testwar` took Testwar out
  of the group (offline only) and put Stormdoom in; `.tele name Stormdoom mc`. **Testwar is no longer in the raid group** -
  re-invite him when he plays. `scratch/autopilot/{reconnect.ps1,recover.sh,restart.sh}` now include 171 ("all 40 online").
- Two new setup verbs (`SuiAutopilot.cpp`): `.autopilot equip <name> <item...>` (replaces what each slot holds; bots read no
  mail) and `.autopilot join <leader> <member> [offline member to make room]`.

Attempts 53–67 (15), all wipes. Kills (E = Elite, H = Healer, s after the pull): 53 H46 E72 E128 H137; 54 E26 H71 H105; 55 E21
H75 E147 H154; 56 E20 H92 H128 E136; **57 E21 E55 H74 H100 H149 E159 H222 = seven of eight (336 s, Majordomo to 79 %)**; 58 E21
H52 E55 H115; 59 E17 H75 H112 H145 (first clean E,H,H,H); 60 E23 H61 E76 H117; 61 H51 E60 E81 H99; 62 E54 E62 H85 E108 H146; 63 H65
E72 E109 H136; 64 E18 H50 H114; 65 E17 H56 E60 H86 H133 (40 alive to ~100 s, raid 5.0k dps); 66 E114 E151 H164; 67 E19 E37 H73 E105.
Installed = running = build **7da8ab72** (after 67). Backups of every patched source: `/tmp/bak-1109/*.pre-1109a … *.pre-1109m`.

Fixes (Core, uncommitted). Letters continue §0.16:
- al. **Off-tank on demand** (`AiBotAI::UpdateOffTankDuty`, run at the top of the 1 Hz behaviour tick; `IsOffTankOnDuty`;
  `GetCombatActiveRole` returns TANK while on duty): a melee damage dealer with a taunt (Taunt 355 / Growl 6795) tanks while
  the fight has more awake elites on the group than living tanks for 6 s, or an elite/boss has been loose on a non-tank for
  3 s (not in the fight's first 8 s - the opening belongs to the sheeps); back to damage after 8 s of enough tanks with nothing
  on it. Logs `[AIBOT-OFFTANK]`. Every tank rule then counts it (TankDuty, healers' tank deal, Defensive Stance rotation).
  Seen working: it took Majordomo off the healers (attempt 55, held him 75 s on 5.2k health) and loose Healers at the pull.
- am. **A wanted crowd control is begun before any clear step** (`MaintainRangedSpacing`, mage Polymorph / warlock Banish, above
  half health): attempt 52's mages stepped out of an Elite's circle for 10 s at the pull and sheeped one Healer of four.
  After: four sheeps by +3 s (53, 54, 58, 59).
- an. The release gate counts an off-tank only while it holds something and never as a free taunter; an off-tank never wakes
  a released sheep (it had let a Healer go at +5 s, attempt 53). Mages treat an off-tank's add as loose (sheep it).
- ao. Sheeps in flight are not "awake" for the off-tank trigger.
- ap. A healer finishes a heal that lands within 1.2 s before a clear step (above half health).
- aq. **A tank with nothing to hold follows the kill order** (`ResolvePartyFocus`): it used to stack on another tank's Elite via
  "a member's attacker", past the floor (attempt 55).
- ar. A melee's kill-order pick is kept 4 s while above half (rogues switched Healer↔Elite every 1-2 s: 30-54 switches each).
- as. Telemetry `[TELEMETRY] KILL <foe> (guid) at +Ns, under 30 % by: member / ability dmg; ...` (elites) - who finished it.
- at. **Majordomo's Champion is not a rebound**: at one add left his script casts Champion (21090) and the add stands at full;
  `ObserveRebound` learned the Elite as "heals itself whole" → left for last → the raid hit Majordomo 110 s beside the last
  Elite (attempt 57, the 7/8). A rebound under any aura another creature cast is not learned. 11664 removed from
  `run/etc/sui-autopilot-rebounders.txt` (backup `.bak-1109`).
- au. **Death-order floor** (`KillPriority`): a later kind is not picked below **45 %** (was 30: ten casters' bolts in flight
  carried an Elite 30 % → dead) while any earlier kind is up - released **or awake** (a loose Healer counts); a floor skip marks
  the bot held; **held off = stop the swing** (`IDoctrine::HoldsOffVictim`, main tick stops attack/chase and skips the solo
  reselect - rogues' white swings and the anchor rungs had finished floored Elites, 61-62).
- av. The order boss is looked for 100 yd out (`KillPriority`, `PlaybookReleaseTarget` 60→100): a rogue on an Elite 50 yd from
  Majordomo applied no playbook rule at all (63). After au+av no Elite died under the floor (64, 65).
- aw. Melee step out of a bursting kind below 65 % (back at 90 %; was 50/80): four rogues died in one Blast Wave (64).
- ax. ~~`SleepersMayWake` also when any awake add is under 20 %~~ **reverted after 70**: an Elite parked at the floor kept it
  true, the free tanks stood by the sheeps 30 s and nobody woke the released Healer (70 killed nothing).
- ay. au's "any awake earlier kind" counted a Healer loose from the pull and held the first Elite (the one whose death frees
  a tank) at the floor until 108 s (68): **back to released ones only**; 45 % and the hold-off stay.
- az. Melee wait out an Elite's Damage Shield only below 75 % health (the shield is up a third of the time); the off-tank's
  loose trigger is 1 s (not 3) when the loose one is a world boss.
- ba. **Stock unreachable-target evade** (`Creature.cpp`, outside SuperUiContent; backup `/tmp/bak-1109/Creature.cpp.pre-1109q`):
  a victim within 5 yd never counts as unreachable. Attempt 71: Majordomo teleported his tank, evaded at 0.2 yd after 24 s
  ("target unreachable 24 s") and re-summoned all eight adds beside the living ones. (Majordomo has no
  `CREATURE_FLAG_EXTRA_NO_UNREACHABLE_EVADE`; setting it would be a DB write.)
- bb. **The off-tank tanks the whole fight of a boss whose playbook orders its adds** (`m_offTankBoss`; `IsOffTankOnDuty` = the
  stopgap only): a 6th full tank frees one from the pull, so the plan can kill the Healers H,H,H,H before any Elite - the 4th
  death is then the last Healer and none is left to wake immune. Attempt 73: released at +6 s as planned, but H1 took 61 s.
- bc. A sleeping sheep counts for the death-order floor and the melee hold (`IsInCombat() || HasBreakableByDamageCrowdControlAura()`):
  the released Healer slept out its 12 s, the floor never saw it, the rogues' white swings killed an Elite 3rd (73).

- bd. A sheepable add **held by a tank** is not ranked "re-sheep, don't kill" (rank 9) while no tank is free: it is as good as
  released (no other is released while it is awake), and the raid killed Elites beside a tanked Healer instead (74).

Attempts 68-76: 68 H96 E108 E137 H209; 69 E41 H83 H111 (37 present); 70 nothing (ax); 71 E17 H39 + Majordomo evade/re-summon (ba);
72 E22 H65 E72 H145 E156; 73 H67 E96 H142 E144; 74 E53 H104; 75 H55 E75 H116 E121 (Boomwarrior dead at 18 s); 76 H72 E119
(**Stormdoom dead at 5 s**: as a full tank from the pull it took Elites with 5.2k health, blues, no shield).
**Stopped 12:55** (24 attempts, no kill): autopilot OFF, all 40 alive at the MC entrance (instance 100), installed = running =
build **00fd3a09**, Testwar offline and out of the raid group.

**Owner decision pending (the 6th tank's gear):** bb works as a plan (73: Healer released at +6 s) but the off-tank cannot hold
an Elite: Stormdoom is an Arms warrior in pre-raid blues with a two-hander (5.2k health). Either gear him as a tank (shield,
defense pieces - more GM gear than the "modest" setup), or keep him a stopgap: then let bb start only after the opening (the
`m_offTankFightMs >= 8000` gate the loose trigger already uses) - not built.

What limits it now: (1) **the 4th add death** - the remaining Healers turn immune, their sheeps break and they wake untanked
in the casters; the raid dies within ~60 s (59, 60, 64, 65); (2) Majordomo loose on healers after his threat resets (on-tank
47-82 %; Bubbleguy has no taunt, the off-tank helps only after 3 s loose); (3) melee: ~120 dps a rogue (white-swing uptime
~50 %, Sinister Strike gated by energy ~350× a fight) for ~95k damage taken a fight; (4) healer throughput 2.4-4k hps; (5) the
off-tank (5.2k health, blues, no shield) dies at 60-130 s. Opening and death order are no longer the main losses.

### 0.16 Majordomo, the tank swap round (2026-11-08 15:45 → 17:25, same lockout, instance 100) — still 8/9
Owner (11-08 afternoon): **"Yes, tank swap"** (the §0.10 item 1 road). Five tanks, Healers first. Attempts 44–52, all
wipes; best = five of eight adds (44 E22 H57 H83 H123 E144; 46 E17 E88 H108 H139 E165 with the first raid death at
148 s; 45 and 48 also five). Stopped 17:25: autopilot OFF, 39 alive and online at the MC entrance (instance 100),
Testwar offline, installed = running = build **e0ed93bf**. No better than §0.15's best (7/8); variance between
openings is bigger than any single change measured here.

Fixes (Core, uncommitted; backups `/tmp/bak-1107a/*.pre-1108ab … *.pre-1108ak`). Letters continue §0.15:
- ab. **Tank swap.** `AiBotAI::TankCanTaunt` (Taunt 355 / Growl 6795). `PlaybookReleaseTarget` wakes a sheep only when
  a taunting tank holds nothing (log `stays sheeped: … N of M taunting tanks free`); `TankDuty` lets a tank without a
  taunt wake one only if no taunting tank is alive within 80 yd; a free taunt-less tank with nothing loose takes the
  boss a taunting tank holds (`[AIBOT-TANK] X: no taunt, nothing loose - takes Majordomo Executus off Y`) - his threat
  resets every 20–30 s, the paladin gets him, the warrior is free for the next sheep. Seen live (46, 51); after it the
  warriors woke every released Healer.
- ac. **Lay on Hands mid-cast** (`TryLayOnHandsSave`, called from the main tick while casting): healers were casting
  80 % of their ticks, so fix x never ran. Cuts the heal short, a tank first; fired 7 times in 45–52 (`(cast cut
  short)`); one paladin per target for 8 s (attempt 45 spent two on one priest).
- ad. Tank separation: the tank whose mob has the smaller pulse circle moves; the higher guid only breaks ties.
- ae/af/ai/ak. **Melee and the 20 yd Healers**: a melee damage dealer leaves an ordered add with a pulse ≥ 18 yd
  (Shadow Shock) while a tanked later-kind add is above half, and takes those to half; under half it leaves them while
  an earlier kind lives, unless a tank must be freed and no earlier kind is awake. A melee held off everything in
  45 yd looks 90 yd out, else stands down (`focus -> (none) (held off by the kill order)`) - the sticky rung kept seven
  rogues on an Elite the hold refused (attempt 50). Results mixed: 46 first death at 148 s; 47 five melee dead to Elite
  Blast Wave / Damage Shield by 60 s; the last fix (ak) ran only in 51–52, whose openings collapsed - **unverified**.
  Revert = restore `AiBotDoctrinePlayerParty.cpp.pre-1108ae` and re-apply ah (the sheeping guard) if it is not wanted.
- ag. **Sheep renewal out of sight**: in reach but out of LOS, the renew walk aimed 22 yd from the sheep on the mage's
  own line - where she stood - and Halddawn "walked" 12 s without moving until it ran out (attempt 46). Now 8–15 yd
  out, round the sheep, to a spot that sees it (`IsWithinLOSAtPosition`).
- ah. `TankDuty`: a loose add a mage of ours is casting Polymorph on is not loose (a free tank's Seal of Righteousness
  broke a fresh re-sheep at once and an Elite then died out of order, attempt 46).
- aj. A Protection paladin under 25 % held by an elite casts **Divine Shield** (its mobs keep swinging at it in this
  version), else Lay on Hands on itself under 15 % (Bubbleguy went 96 % → dead in 6 s, attempt 49). Not yet seen fire.

What limits it now: (1) **the opening** - 45, 47, 51, 52 lost a tank or had a Healer loose in the first 5–20 s: the
puller (Boomwarrior) takes all nine mobs ("9 elites on the raid") and died in 6 s (51); his Shield Wall (30 min) and
Last Stand (10 min) are on cooldown between ~11 min attempts (871:gate 16); a Healer hit by a rogue at +2 s before any
sheep (47). (2) Five tanks with no spare: any tank death before ~120 s forces an Elite to die out of order and the
fourth death wakes two immune Healers (49, 50). (3) The four rogues that died together in lava at 138 s (48) - not
looked at.

### 0.15 Majordomo, fixing the AI further (2026-11-08 11:37 → 14:42, same lockout, instance 100) — still 8/9
Owner (11-08 midday): **"keep fixing the AI - dont need to ask me - take a road and try it."** Five tanks, Healers
first, same release plan as §0.14. 17 attempts (27–43), all wipes; best = **seven of eight adds** (attempt 39, 322 s;
one Elite left). Stopped 14:42: autopilot OFF, 39 alive and online in MC instance 100, Testwar offline, installed =
running = build **24e152f9**, playbook Healers first. Attempt 33 had the fastest six (Elite 15 s, Healers 37/62/84/115 s, Elite 119 s, raid 4.2k dps). Typical: 4–5.

Fixes (Core, uncommitted; backups `/tmp/bak-1107a/*.pre-1108m … *.pre-1108x` on the box). Letters continue §0.14:
- m. **Majordomo's threat resets** (`boss_majordomo_executus.cpp`: his teleport calls `DoResetThreat` every 20–30 s).
  (1) `MaintainTankFacing` re-chases a mob that turned on someone else (before: only one still on me), log
  `[AIBOT-TANK] X: Y turned on Z - chases it again`; Ironwarden stood 30 s on his facing point while Majordomo killed
  two priests. (2) `TankDuty`: a tank whose elite is on a non-tank takes it back before waking a released sheep.
  Majordomo on-tank 57 % → 77–98 %.
- n. Probe `[AIBOT-CLEAR] X (role): from Y entry E [asleep] on Z, d of keep yd [circle]` (4 s per bot) - which enemy
  each ranged/healer clear step runs from. Attempt 29: 186 of 357 from the one awake Healer's 21 yd circle, 100
  from Elites' 14 yd.
- o. (1) **Healers never got their loadout**: the autopilot called `SuiRaidSupply::Supply(.., 0)` for everyone, so
  healers fell in the caster bucket - the Demonic Runes listed for them since 11-07 were never handed out. Now the
  bot's role is passed (1 tank, 3 healer, 4 melee). (2) A clear step out of a burst circle lands 5 yd outside, not
  on its rim (the tank walks the Healer a yard and the ring steps again).
- p. **Clear steps keep sight**: ranged casts failed LINE_OF_SIGHT (result 42) **3,286 times against 375 cast**
  (attempt 30, rotation telemetry). The clear step now tries ±45°/±90° spots that still see the target (a healer:
  its tank) within 33 yd first, then the old way. Next attempt: 17 refusals.
- q. Sheep guards: Multi-Shot checks 20 + 6 yd (it jumps 10 yd twice); **any** damaging cast in flight at a unit
  that became crowd-controlled is stopped (an Immolate landed 2 s after a mage re-sheeped its target).
- r. `KillPriority` always counts a released add within 90 yd: tanked 25–30 yd from the stack it fell out of some
  bots' 45 yd list, ten switched to an Elite "by kill order", and that Elite was the 4th death (attempt 32).
- s. `SelectHealTarget` (PlayerBots base): **every healer is a tank healer while fewer than three non-tanks are
  hurt** (before: 2 in 5 always). After the Healers die the Elites carry Encouragement (+8 % damage per add
  death, `21086`) and three tanks died in 12 s with healers topping scratched dps.
- t. `TankStepOk`: a tank step may not end more than 35 yd from the healers' middle unless it gets closer (a
  paladin tank held an Elite 52–73 yd from eight healers); 28 yd stacked the Healer on Majordomo (both tanks in its
  Shock), so 35. `ActiveTank` keeps a healer's dealt tank 15 s while it lives (before: re-dealt every few seconds).
- v. **Interrupt duty** (`TrySpecInterruptNearby`): mages Counterspell, rogues Kick any nearby add casting a
  silenceable spell, one interrupt per cast (1.5 s claim), log `[AIBOT-INTERRUPT]`. Elite Fireball fell out of the
  top damage sources (47–64k → not listed).
- x. Paladin healers **Lay on Hands** (10310) a tank under 15 % held by an elite, log `[AIBOT-HEAL] X: Lay on Hands`.
- y. Damage dealers leave a still-sleeping released sheep alone **12 s** (was 6): the free tank 24 yd off needed 8 s
  and a warlock's Immolate woke it first; the Healer spent 77 % of its life off a tank (attempt 40).
- z. **A healer holds its ground** (no clear step) while a tank within 36 yd and in sight is under 50 %, the healer is
  over 50 % and nothing is hitting it, log `[AIBOT-HEAL] X: holds its ground`. Four healers within 15 yd of
  Bubbleguy, two idle at full mana, stepped out of a Healer's circle while he died with 756 healed (attempt 41).
- aa. Damage dealers leave a woken released add alone until a tank holds it (at most 15 s from the release): a
  paladin's Judgement woke one from 10 yd and it spent 98 % of its life off the tanks (attempt 42).

Records (E = Elite, H = Healer, seconds after the pull): 27 — E16 H41 H67 H103, the immune 4th H died at 165;
Majordomo's resets killed healers (→ m). 28 — E21 H67 H116; a 4th Healer woke 2 s after its re-sheep cast began
(resisted re-sheep?) and six rogues died in 8 s. 29 — E23 H55 H107, the leader tank died (6 of 10 healers no sight),
gate closed, an Elite died 4th. 30 — runes handed out from here; E17 H63, H2 never died. 31 — LOS refusals 17; a
Multi-Shot and an Immolate broke sheeps (→ q). 32 — 3.5k dps, Majordomo 98 % on tank; Sylwhisper died at 27 s, E15
H39 E58 E107. **33 — best: E15 H37 H62 H84 H115 E119, then three tanks died 124–136 s to Encouraged Elites (→ s).**
34 — clean order E17 H48 H81 H108; Bubbleguy died 98 s, the last (immune) H ran loose. 35–38 — 4/8 each: opening
variance (a tank left its woken Healer for a loose Elite; a sheep broken 5 s in), Bubbleguy (5.4k health, the
paladin tank) died at 44 s twice, H kills slowed to 41–77 s. **39 — best so far, seven of eight: E21 H68 E89 H125
H163 H243 E282**, 38 alive at 140 s, first tank death at 160 s (Bubbleguy), 322 s fight; one Elite left when the
last tank died (Majordomo only submits at AddCount 0, `SummonedCreatureJustDied`). The order went E, H, E, H, so
the last two Healers were Polymorph-immune - and still died, tanked. Lay on Hands (fix x) never fired. **The
record's `dead@0s` foes are NOT kills** (evaded/despawned at the wipe; attempt 39's fourth Elite shows it) - count
kills from `diedAt > 0`. 40 — E16 H42 H72, the third release broken by an Immolate before its tank arrived (→ y),
the 4th (immune) Healer loose. 41 — E20 H54 H100 E128; Bubbleguy dead at 62 s, healers beside him idle (→ z). 42 —
E25 H68 H149 E182; healing 2.5k/s, Bubbleguy lived to 135 s; a Blizzard broke a sheep at 57 s; the first Healer, woken
by Bubbleguy's Judgement, was on a tank 2 % of its life (→ aa). 43 — E19 H60 H107; Bubbleguy woke the second Healer
in melee and it was still on a tank only 17 %: **a paladin tank (no taunt, low threat) cannot keep a woken Healer**;
all five tanks died 145–173 s.

What limits it now: (1) **the first tank death** (Bubbleguy first in 4 of 6) - the add cycle needs every tank
alive to ~120 s; he stands in stacks of Majordomo + two Elites (two Blast Waves + melee) because only the
higher-guid tank moves in a separation and he has the highest guid; (2) Healer kill speed varies 22–77 s; (3) the
endgame: two Elites with 6+ Encouragement stacks after the Healers. Fights record: `fights.py` splits mid-fight
again (dead@0s) - use Server.log.

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

### 0.10 Next steps (in order; updated 2026-11-10 16:10, see §0.21)
Same lockout (instance 100) until **2026-11-13 23:00**: **9/9 — Majordomo defeated 11-09 20:05 (attempt 89)**; only Ragnaros
is left. Five tanks + the off-tank warrior Stormdoom (171, pre-raid tank gear, Majordomo's tank all fight), **Elites first**
(playbook), fixes m–ak (§0.15-0.16), al–bd (§0.17), be–bl (§0.18), bm–br (§0.19), bs–bv (§0.21). Owner: healers keep own-loot
gear; "Fix Majordomo first", then Ragnaros.
-1. **Finish and read batch B (§0.21 "Pick up here").** If it is clearly better than §0.20's 0/10 (A1 and A3 were kills),
   report it to the owner and move to Ragnaros (item 0). If not, read the wipes (`/tmp/Server-1110-HHMM.log` per run,
   `fights.py -n 10`) - the Healer phase after the wake-up is the wall (§0.20). **After the batch: put the lockout back to the
   real result** - `.autopilot encounter Boomwarrior 8 3` and runes 16–22 = 3, then restart (QA kills are not progression; no
   Cache opened in any QA run).
-2. Any crash: newest `/tmp/mangosd-gdb-*.txt` on the box has the backtrace (the 14:46 SIGSEGV and the GetMap assertion).
0. **Owner: Ragnaros or not.** The owner's goal was "until Majordomo dies"; failing Ragnaros for 1–3 weeks is fine (§0). He
   is summoned by Majordomo's gossip in his lair (stock script, `boss_majordomo_executus.cpp` dialogue); the autopilot has no
   gossip step yet, and Ragnaros needs knockback/Magma Blast/Sons of Flame handling (Sept Commander notes in
   `COMMANDER_RAID_STATE.md`). Ask before starting.
0a. **Majordomo repeatability: 0 of 10 on the kill's build (§0.20)** - the owner's plan was "verify, then Rag"; verification
   failed, so the owner decides between more Majordomo work (the Healer phase after the wake-up, item 1) and Ragnaros anyway.
   Run any fix as a 10-run batch with `domo-verify.sh`. **Healer overheal in attempt 89 (bp): 23–47 % per healer (Chargesniper 47, Gankreaper 39,
   most 29–35)** - a third of the healers' mana lands on full health; the next mana lever (heal choice / incoming-heal
   prediction / cancelling a cast whose target is full).
0b. Keldimp's Core Hound Tooth in the main hand (§0.19 loot): check `ScoreItem` for set bonuses / weapon specialization.
1. **The four-Healer phase** (Elites first, after the clean wake-up of 85): 81 s to the first Healer kill. Ranged made 384
   clear steps between four awake 20 yd circles; tanks should separate the Healers (the separation step exists for two held
   bursters, §0.13) and park them so one is killed while the others' circles stay off the casters.
2. **Damage taken** = healer mana: Shadow Shock 100–200k a fight is the top source (anyone inside 20 yd of an awake Healer).
3. **Melee uptime** in Healers first: melee idle most of the fight (held off the 20 yd Healers, Elites only to half).
4. **Endgame**: the last two Elites carry 4-7 Encouragement stacks.
5. Death order holds; with Healers first an Elite dies 2nd only when no tank is free (79: tank dead at 9 s).
6. **Tanks into Damage Shield**: Boomwarrior lost ~1.9k in 2.6 s to his own swings into an Elite's shield. A tank
   cannot simply AttackStop (it drops its victim and `TankDuty`'s "held" with it) - needs a swing pause that keeps
   the victim, and only with a safe threat lead.
7. Healer mana: runes now handed out (fix o); healers still dry at 30–70 s in long fights.
8. Shazzrah: 7 of 8 melee dead in 19 s to Arcane Explosion (30 yd, every ~6 s) — melee should not stand in it.
9. Magmadar's Conflagration (Lava Bomb GO 177704, trap radius 5, spell 19428: 400/s for 8 s, NOT dispellable) killed
   10 tonight and topped weeks 1–2: the burn continues after stepping out, so it is a healing problem (victims had
   healed8 0).
10. Healer mana and throughput: dry at Geddon (Ignite Mana, oom 85–109 s) and Magmadar. In the 476 s Geddon wipe the
   mages spent up to 200 s out of mana; Ignite Mana (19659, magic, 5 min) is valued by `DispelValue`, but no dispel is
   logged, so whether priests/paladins actually remove it is unverified.
11. Rogues die to Flame Imp Fire Nova in a 17-imp pull (7 dead, a win) and to Firesworn melee at Garr.

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
