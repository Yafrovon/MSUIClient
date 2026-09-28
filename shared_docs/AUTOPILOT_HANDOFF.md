# Raid Autopilot — handoff for the next agent (written 2026-09-24 ~23:40 EDT; §5–§7 rewritten 2026-09-25 ~20:50 EDT by Claude)

You have no memory of this work. This page tells you what the mission is, what exists, how
to run it, what state the world is in right now, and what to do next. Read it top to bottom
before touching anything. It supersedes the autopilot parts of `COMMANDER_RAID_STATE.md`
(which has a short pointer here) — the Commander *executor* history in that file is older,
different work.

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
