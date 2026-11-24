# Raid tactic scope law (binding, owner 2026-11-15)

> "Can we please add some guiderails to the build, such that, we don't implement "lossy" logic let's call it, whereby
> you are adding generic level logic that by consequence can affect everything else that we've already been working on -
> that is, keep it contained to the raid it's on. In many general dungeons, maybe that's valid, but, I'd say that it's
> invalid when we are talking about raids where the behavior is pack/boss specific" — Nico, 2026-11-15

Applies to the Raid Autopilot and the SuperUI bot AI in Core (`src/game/SuperUiContent/SuiBots/`,
`SuiWorld/CRPG/SuiAutopilot.cpp`). It does not replace the Commander raid system's own rule
(`COMMANDER_RAID_ARCHITECTURE.md`: no encounter logic in the Commander executor); this is the autopilot's equivalent.

## The rule

1. A behaviour change made because of a raid fight is a **tactic**, and a tactic is **scoped**: it is registered in
   `SuiBots/SuiTacticScope.h` with the raid map(s) it was made for and, when it is about particular creatures, their
   entries (the boss, the pack's kinds). There are no global tactics: a row with no map fails the check.
2. The code applies it only through `SuiTactic::On(me, SuiTactic::X, subject)` (or `OnAnyAttacker`) and keeps the
   **previous behaviour everywhere else**: the old threshold, the old skip, the old target. A tactic never changes
   what other raids, dungeons, bosses or the open world do.
3. Widening a tactic (another raid, another boss) is an edit of its row, made only after that fight showed the need,
   with the evidence in the row's `origin`. Copying the tactic's effect in ungated code is the same violation.
4. Diagnostics, telemetry and crash fixes that change no behaviour carry `[TACTICS-SCOPE-EXEMPT: why]` beside their
   dated comment. Never use it for a behaviour change.
5. Dungeons and the open world are not raids: a generic fix there is still allowed, but never inside a raid's fight
   through the back door. If a fix is truly universal (it is right for every raid fight ever), ask the owner first and
   record the approval in the row's `origin`; the row still lists the raids where it was proven.

## The check (run after every Core change in these files)

```bash
bash tools/raid-tactic-scope-check.sh
```

It fails when a registry row names no map, an enum id has no row (or two), a gate names an unregistered id, a
registered id is gated nowhere, or a comment dated 2026-11-15 or later in `SuiBots/*` or `SuiAutopilot.cpp` has no
`SuiTactic::On` gate within 15 lines before / 40 after (agents date every tactic comment, so a new ungated rule shows).
Negative-tested 2026-11-15 (a planted map-less row and an ungated dated comment both failed).

## What is registered (2026-11-15)

All seventeen tactics of the 2026-11-15 rerun-3 nights (handoff §0.29 items 21-35, the Fear Ward pair, the council's
no-2H rule) are rows: twelve on Molten Core (409), five on Onyxia (249), the council on both. Outside their rows each
falls back to the behaviour it replaced (e.g. a death burst is fire from 20 %, a paladin tank may bubble, healers dodge
their own attacker's front, one threat-wipe window of up to 30 s).

**Not yet contained:** behaviour added before 2026-11-15 (the Onyxia development rounds of 11-14, the MC weeks of 11-03
to 11-13 and the September record) is still generic. It is migrated row by row when one of its fights is next worked
on; the check only enforces the dated comments from 2026-11-15 on.
