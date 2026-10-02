# MSUIClient — agent instructions (any agent: Claude, Codex, Cursor, a human)

## Raid mission state

Read [COMMANDER_RAID_STATE.md](shared_docs/COMMANDER_RAID_STATE.md) first for current authorization status, acceptance gates, evidence and next steps. Its current user pause supersedes historical continuation banners.

This file is the standing brief for whoever works this repo. It exists because
rules that only live in chat get re-broken (owner, 2026-09-03: "I don't need to
repeat myself over and over"). Tracked at the repo root (2026-09-03) so it travels with the code; the content is
agent-agnostic on purpose. Tool-specific loaders may import it.

## Read first: `shared_docs/`

`shared_docs/` is the TRACKED home of the team's design documents and laws (2026-09-04).
`docs/` is git-ignored scratch and never travels; anything another agent or teammate must
read goes in `shared_docs/` and gets a line here. Read the ones for your topic before
touching the code:

- `shared_docs/POSSESS_LAW.md` — possession, companions, Command View, any
  NPC/loot/taxi/mail interaction, the fleet follow. Binding, enforced by
  `dotnet run --project tools/interface-wire-check -- --possess-law-only` and
  `tools/possess-law-check.sh` (Core, over ssh). Both must stay green after any
  change in those areas; add a check with every new rule.
- `shared_docs/CRPG_FREEZE_SYSTEM.md` — the CRPG/RTS freeze system.
- `shared_docs/MACRO_BOOK.md` — the Macro Book: stable macro ids and the legacy
  ranges, the v2 store, the embedded Core command export and how to regenerate it.
- `shared_docs/GAMEPLAY_INTERACTION_CHECKLIST.md` — evidence and verification status
  for subtle gameplay feedback; how to regenerate the archive-driven audit and triage it.
- `shared_docs/FULL_GAME_COVERAGE.md` — full-game coverage inventory, quest/spell
  acceptance rules, background execution batches and evidence gaps.
- `shared_docs/Sept 8, 26 fixes.md` — complete September 7–8 audit recap, all GI entries,
  evidence, open defects and the owner-requested pause handoff.
- `shared_docs/INTERIOR_UNIT_LIGHT.md` — how units, mounts, items and server
  gameobjects are lit inside a WMO (the floor's MOCV under the feet, one law with
  the props); the `MSUI_INTERIORLIGHT_PROBE` offline proof.
- `shared_docs/COMMANDER_RAID_STATE.md` — the one page that is overwritten every session: current status, streaks, hashes, acceptance rule and the next step. Read this first for raid work.
- `shared_docs/AUTOPILOT_HANDOFF.md` — the Raid Autopilot (bot leader runs MC/dungeons with no human; generic tactics, telemetry, pull planner, respawn execution): how to run the deploy/watch loop, code map, current MC state and next steps. Read this first for autopilot work (added 2026-09-24).
- `shared_docs/COMMANDER_RAID_ARCHITECTURE.md` — stable design of the Commander raid system: what is code, what is data, the definition schema, the compiler and its global policy, the checkpoint/QA tools, the regression runner and the acceptance rule.
- `shared_docs/COMMANDER_RAID_PLAN.md` — original design record and research basis for the raid planner and executor (historical detail; ARCHITECTURE is the current summary).
- `shared_docs/COMMANDER_MOLTEN_CORE.md` — generated Core/DB review of the ten Molten Core bosses with the executor primitives each still needs.
- `shared_docs/COMMANDER_RAID_DOCTRINE.md` — Commander raid doctrine (global, archetype-level raiding practice) and per-raid roadmaps (boss order, entrance, skips — never tactics): the fourth kind of data beside derived data, global policy and surveys (added 2026-09-13; line added 2026-09-19 so `--shared-docs-only` is green again).
- The day-by-day raid log (2026-09-06 to 09-09) is frozen at `docs/archive/commander-raid/COMMANDER_RAID_HANDOFF_2026-09-06_to_09-09.md` (git-ignored, local only). Do not append to it; update STATE instead.
- `shared_docs/SPELL_CREATOR_IDE.md` — the Spell Creator IDE: the void stage, the
  effect clock (pause/step/scrub + deterministic replay) and the grid/emitter
  gizmos in Creator Mode's spell workshop; the emitter frame chain and slice order.
- `shared_docs/SPELL_SKETCH.md` — SPEC (unbuilt): free-form effect authoring in the
  Spell Workshop — draw or pick a shape, place it, give it travel/path/spin/fade,
  hang trails and sparks off it — compiled into real vanilla M2s + BLPs so every
  existing dial, handle, clock, composition slot and Completer path applies.
- `shared_docs/SPELL_IDE_MAP.md` — the Spell IDE's map: how the missing things are
  ENUMERATED (schema walk, census, task walk, round trip), the organizing spine
  (the spell's timeline of parts), the field-level inventory with real-use counts
  (`tools/spellvis/spell_visual_census.py`), the task walk and the ordered slice
  list. Read this before adding to or reordering Spell IDE work.
- `shared_docs/WORLD_BUILDER.md` — the World Builder (Creator Mode terrain sculpt + WMO/M2
  placement) and optional World Content Packs: client → web app ops (audited, undoable) →
  `patch-7.MPQ` + real extractor maps/vmaps/mmaps with a vanilla baseline; the NPC Creator and
  Dungeon Maker; why the old browser editor broke mob pathing (added 2026-09-26).
- `shared_docs/WORLD_BUILDER_HANDOFF.md` — the CURRENT World Builder / Gilneas state (rewritten 2026-09-27 night): what is
  built and verified, copy-paste commands for every task (tools/worldpack/: launch-wb/launch-live, generators, navmesh
  planner, make-group), the open items in order and the uncommitted change list.
- `shared_docs/EQUIPMENT_CAPTURE.md` — deterministic weapon/armor capture with the production character renderer, body/view/pose matrix, asset hashes and offline-vs-live acceptance limits (added 2026-09-28).
- `CODE_STRUCTURE_LAW.md` (repo root) — where a `.cs` file goes and how it is named.

`interface-wire-check --shared-docs-only` fails when a file in `shared_docs/` is not
listed above, so adding a document means adding its line.

Also, ignored on this machine only: the day-by-day CRPG/RTS record
`docs/current/CRPG_RTS_WIP.md` (append a dated section per round; never rewrite
history) and the server handoff `docs/current/POSSESSION_ROUTING_HANDOFF.md`.

## Standing rules (short form; the law files have the why)

1. The body you drive is the body that acts. Server: `GetSuiActor()`. Client:
   `TryGetInteractionBodyPose` / `ControlledGuid`. Never `_player` for gameplay,
   never `TryGetSessionBodyPose` / `_net.PlayerGuid` for a gate or a purse.
2. A reply built on the bot's socket-less session is lost unless it is in
   `MirrorOwnerPacket`'s whitelist AND unwrapped in `ApplySuiProxy`. Audit
   `Player::OnGossipSelect` for every routed family. "Silently does nothing"
   does not count as functioning.
3. The rest of the party STAYS: a driven body that flies, ports or is hopped
   away from is never chased by teleport; followers hold, and a hold ends the
   active follow leg.
4. Command View: nothing opens until the acting body is physically at the NPC;
   our own dialogs auto-hide out of range; a chooser only for NPCs with two
   distinct offers (mind the stale innkeeper bit on bowyers).
5. No ImGui widgets in gameplay UI (vanilla primitives only); the
   `--imgui-policy-only` check stays green.
6. Never commit, push, create branches or worktrees without separate consent.
   Build the client in both Debug and Release when changing code. The persistent
   Testwar +39 gameplay grant below permits character operations and their normal
   persistence. The 2026-09-07 full raid/boss scripting mission exception also
   explicitly permits Core builds, installation/deployment and scoped server
   restarts/control across conversations. Database/worldstate restore/swap and
   unrelated servers remain owner-only. Read the mission scope below; do not
   revive the superseded Core build/install/restart approval requirement.
7. Pair-deploy: new opcodes/capability bits change both sides in one round.
8. Probe first, don't theorize: `~/vmangos/run/bin/Server.log` (grep `[SUI]`,
   `released bot`, `catch-up teleport`) and the client `msui-console.log`.

## Find code with the locator -- if not on Yafrovon's Computer, IGNORE THIS SECTION

All three repos (this client, the `MangosSuperUI` web app, the vmangos C++ core on the box)
are indexed by one local service, the **superui-locator**, at `http://127.0.0.1:5077`: every
type and member of both C# repos live from the working tree (a saved file is re-indexed within
a second), the libclang graph of the core, string literals, leading comments, and the
cross-repo seams (SUI opcodes <-> core handlers, bridge message names, twin files). Before any
tree-wide grep, `Select-String`, `sed -n` walk or "where is X" reasoning, ask it; grep only
when it returns nothing after two phrasings, and say so.

- MCP tools (Claude Code, Codex with MCP): `locate(task)`, `search(q, repo?, kind?)`,
  `outline(file|id)`, `neighbours(id, types?)`, `read(id | file,start,end)`, `grep(q)` (core
  tree only), `stats`.
- Any agent: the same as GET routes: `curl -s "http://127.0.0.1:5077/locate?task=..."`,
  `/search?q=...&repo=cli`, `/outline?file=GameLoop.Net.cs`, `/neighbours?id=...&types=calls,seam`,
  `/read?id=...`, `/stats`.
- Ids: `cli:MSUIClient.GameLoop::ControlledGuid`, `core:WorldSession.SuiPossess/HandleOrder`; a
  unique suffix (`GameLoop::ControlledGuid`) is accepted. Order: locate -> outline -> neighbours ->
  read one span at a time (<= 400 lines). Never read whole files to find a method.
**Enforcement (Claude Code):** a PreToolUse hook (`SourceMapper/Locator/hooks/locate-first.py`, registered in
`.claude/settings.json`) denies tree-wide searches (Grep without a file path, recursive grep/rg/Select-String)
until a locator tool has been called in the last 15 minutes; file-scoped searches always pass, and the hook
stands down when the host is not running. Codex/Qwen have no hook: the rule above is the contract.

- If `stats` does not answer, the host is down: start it (`dotnet run -c Release` in
  `C:\Users\user\source\repos\SourceMapper\Locator`, or the `locator` entry in
  `.claude/launch.json`) or tell the owner. Markdown docs (`shared_docs/`, `docs/`, root),
  JS functions and Razor views are indexed with sections/spans; JSON and binary assets are not.

## Box and machine facts

Host names, ssh config, tree paths and the install/restart one-liner are
machine-specific and live in `AGENTS.local.md` (git-ignored). Copy the block
from another machine or ask the owner.


## This is a TEST environment - build and test freely (Nico, 2026-09-27)

The box, its server, its databases' game state and every character on it exist for development and testing. For
World Builder / content work (and any other feature work) agents do what a test needs WITHOUT asking and without
treating the raid whitelist below as a limit:

- **Bots are SuperUI bots, never stock vmangos `.partybot`s** (PartyBotAI is not part of this project - behaviour
  proven on partybots proves nothing, and patching PartyBotAI is wasted work). Need a group? Create NEW SuperUI bots
  (`POST http://192.168.0.2:5000/Bots/AddBots {"spawns":[{"race":"human","cls":"priest","count":1}]}` = `.bot addai`),
  then level them, train their spells, max their weapon skills, gear and spec them, teleport, group, kill and revive
  them with GM commands - whatever the test needs. Delete them when they are no longer useful.
- Create test characters, level/gear/teleport them, GM-complete setup steps (label SETUP in reports).
- The 40-man raid roster (Testwar + the 39 prepared bots) belongs to the raid mission: leave it alone unless the task
  is raid work - make new bots instead of borrowing it.
- Still off-limits: commits/pushes/branches (the owner does git), direct SQL writes, worldstate restores, the
  unrelated CMaNGOS server. Everything else on this box is a sandbox - use your judgement, don't ask permission.

## Persistent owner authorization: Testwar +39 live raid QA

Nico explicitly authorized this exception on 2026-09-06 and requested these docs
be corrected so work can continue in a new conversation. It overrides conflicting
general "owner-only live runtime" language for the following precise scope and
persists until revoked. Do not ask again for actions already covered here.

- Actor scope: Testwar, player GUID 787, and the existing 39 prepared bots (GUIDs
  115-142 and 150-160; names in `scratch/onyxia-live/Reconnect-OnyxiaRaid.ps1`,
  roster facts in `shared_docs/COMMANDER_RAID_STATE.md`). Do not duplicate the raid or touch
  unrelated player characters.
- Allowed: launch/login/control/close the local MSUIClient and agent-owned QA
  processes; use actual client gameplay and the configured web/RA/GM interfaces
  for bot login/logout (`.bot add NAME` included), grouping and leadership,
  levels/specs/gear/inventory/ammunition, repairs, teleports/summons, Hold/movement,
  planner assignment and Apply/Arm/Pause/Clear, combat, killing/reviving these test
  characters, health/mana replenishment, and their QA GM/cheat settings.
- Normal database writes caused by these approved gameplay operations are
  allowed. They are distinct from direct SQL edits or backup/worldstate restores.
  Direct SQL remains read-only absent separate specific approval.
- Allowed: source patches in client/Core, client Debug/Release builds, meaningful
  tests and live observation. Keep ordinary battle results distinct from setup
  commands or deliberately altered diagnostics; do not manufacture a raid clear.
- The 2026-09-07 mission exception below additionally permits scoped Core
  builds, installation/deployment and server restarts/control. Database/worldstate
  backup/restore/swap and unrelated servers remain outside that grant. No
  commits/pushes/branches/worktrees without separate consent.

This matches the corresponding exception in `C:/Users/nico/.codex/AGENTS.md`.
A tool may still enforce its own approval policy; do not bypass an actual tool
rejection. Report the exact rejected action and reason if one remains.

## Mission authorization: full raid/boss scripting — Nico, 2026-09-07

Nico explicitly authorized changing the former owner-only rule so agents may
rebuild, install and reboot/restart Core during this full raid/boss scripting
mission. This is direct owner authorization, persists across conversations for
this mission until completion or revocation, and overrides conflicting older
Core administration prohibitions and historical handoff notes. Do not ask him to
repeat permission for these covered steps.

- Scope: the configured VMaNGOS development checkout `/home/wowvmangos/vmangos`
  and its runtime `/home/wowvmangos/vmangos/run` on `192.168.0.2`, for reusable
  raid/boss scripting and real-game validation, including Onyxia followed by all
  Molten Core with Testwar GUID 787 and the same 39 prepared bots.
- Agents may patch and build Core, install/deploy the resulting server artifacts,
  and stop/start/restart/reload that development game server as needed to apply
  and test mission changes. This includes its normal build/install workflow and
  the specific process/service, RA/console or dedicated screen/tmux operations
  needed for that server's restart. Inspect the actual launch setup first and
  target only that VMaNGOS runtime; preserve normal persistent game state.
- Server reboot/restart in this grant means the scoped development game server;
  it does not grant a host-wide reboot affecting unrelated workloads. The
  unrelated CMaNGOS server and all unrelated projects/processes remain outside
  scope. Perform planned deployment between attempts after safe recovery or
  explicitly invalidate an interrupted attempt; preserve logs and completion
  evidence and verify the installed binary and running process afterward.
- Existing full Testwar +39 gameplay and local-client/QA grants continue. Normal
  gameplay persistence is allowed. Database/worldstate backup/restore/swap and
  direct SQL writes remain outside this grant; SQL stays read-only. No commits,
  pushes, branches or worktrees without separate consent.
- Count clears only from real normal-rules combat and encounter progression.
  Continue using shared encounter data and reusable execution logic, and build
  tools to shorten safe preparation, observation and recovery cycles.
- This is an authorization for this mission, not unrestricted infrastructure
  administration. External tool approval enforcement still applies; do not
  bypass a rejection, and report any actual rejected action and stated reason.
