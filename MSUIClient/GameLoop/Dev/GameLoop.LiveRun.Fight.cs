using System.Globalization;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _liveFightGuid, _liveFightActor;
    private double _liveFightDeadline, _liveFightCastAt, _liveFightCaptureAt;
    private bool _liveFightStartDumped;

    // Bounded combat fixture on an existing selected enemy. Normal cast handlers
    // own gameplay; this observer never creates, kills, moves or heals a unit.
    private double _liveFightReengageAt, _liveFightGroundWaitUntil;

    private bool AdvanceLiveFight(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length != 3 || !uint.TryParse(args[1], out uint spell) ||
            !double.TryParse(args[2], CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > 600)
        { Log(false, line + " invalid arguments"); return true; }
        double now = NowSeconds();
        if (_liveFightDeadline == 0)
        {
            // A body placed on a mob's live spot can still be sliding down a slope (or knocked back as the mob
            // engages) a moment after wait-grounded: give it up to 3 s to land instead of skipping the fight.
            // A skipped kill left Gilnwar's "Wolves at the Wall" at 9/10 and the turn-in refused (2026-09-27).
            if (_controller is not { Grounded: true })
            {
                if (_liveFightGroundWaitUntil == 0) _liveFightGroundWaitUntil = now + 3;
                if (now < _liveFightGroundWaitUntil) return false;
                _liveFightGroundWaitUntil = 0;
                Log(false, line + " fixture requires the actor to be grounded before combat"); return true;
            }
            _liveFightGroundWaitUntil = 0;
            if (!_entities.TryGet(_selectionGuid, out WorldEntity enemy) ||
                !enemy.IsCreature || !CanActorAttack(enemy, ControlledGuid))
            { Log(false, line + " requires a living attackable creature"); return true; }
            _liveFightGuid = _selectionGuid;
            _liveFightActor = ControlledGuid;
            _liveFightDeadline = now + seconds;
            _liveFightCastAt = _liveFightCaptureAt = now;
            _liveFightStartDumped = false;
            _liveFightReengageAt = now + 1.5;
        }
        bool targetPresent = _entities.TryGet(_liveFightGuid, out WorldEntity target);
        bool actorPresent = _entities.TryGet(_liveFightActor, out WorldEntity actor);
        bool dead = targetPresent && target.IsDead;
        bool interrupted = ControlledGuid != _liveFightActor ||
            (_selectionGuid != _liveFightGuid && !dead) ||
            !actorPresent || actor.IsDead || !targetPresent;
        if (dead || interrupted || now >= _liveFightDeadline)
        {
            Log(dead && !interrupted, $"{line} target=0x{_liveFightGuid:X};observedDead={dead};" +
                $"interrupted={interrupted};targetHealth={(targetPresent ? target.Fields.Health : 0)}");
            // One screenshot for the OUTCOME (plus one at the start): the 5-second capture filled the
            // dumps folder with thousands of near-identical frames. The verdict stream keeps the timeline.
            _currentVantage = $"fight-{(targetPresent ? target.Entry : 0)}-end";
            ArmGameplayDump();
            _liveFightDeadline = 0;
            return true;
        }
        if (now >= _liveFightCaptureAt)
        {
            EmitCombat("LiveFightState", "diagnostic", target.Guid,
                $"entry={target.Entry};health={target.Fields.Health};actorHealth={actor.Fields.Health}");
            if (!_liveFightStartDumped)   // first diagnostic of this fight only
            {
                _liveFightStartDumped = true;
                _currentVantage = $"fight-{target.Entry}-start";
                ArmGameplayDump();
            }
            _liveFightCaptureAt = now + 5;
        }
        if (spell != 0 && now >= _liveFightCastAt)
        {
            TryCast(LiveRotationSpell(spell));
            _liveFightCastAt = now + 2.1;
        }
        // The server can end our auto-attack mid-fight (SMSG_ATTACKSTOP with the target alive - seen as a
        // visibility/aura refresh right after a GM teleport onto the mob). A player presses attack again;
        // so does the fixture, visibly, instead of standing there for the rest of the timeout.
        if (_attackTargetGuid == 0 && now >= _liveFightReengageAt && CanActorAttack(target, ControlledGuid))
        {
            _liveFightReengageAt = now + 1.5;
            EmitCombat("LiveFightReengage", "diagnostic", target.Guid, $"entry={target.Entry};health={target.Fields.Health}");
            CommitSelection(_liveFightGuid, true);
        }
        return false;
    }

    private ulong _bossTrialGuid;
    private double _bossTrialStart, _bossTrialDeadline, _bossTrialCastAt, _bossTrialReengageAt, _bossTrialStateAt,
        _bossTrialJoinAt, _bossTrialOutOfReachSince;
    private uint _bossTrialMaxHealth;
    private bool _bossTrialPull;
    private int _bossTrialChases;

    /// <summary>
    /// boss-trial &lt;spell|0&gt; &lt;seconds&gt; [pull]: a BALANCE fight on the selected boss by the tester and its group - no
    /// god mode, no GM damage. Unlike fight-until-dead the tester dying does not end it (the group can still
    /// win): it ends when the boss dies (KILLED, the only pass), when every group member in sight is dead
    /// (WIPE) or at the timeout. The verdict carries the numbers a designer tunes by: seconds, the boss's
    /// health left, who died. "pull": the group gathered 30 yd out; the tester runs in beside the boss (a GM teleport =
    /// movement SETUP, as kill-for-quest), its SuperUI group joins on its target (PlayerParty doctrine), and the tester
    /// follows the boss when it is out of reach 2.5 s (fear, knockback).
    /// </summary>
    private bool AdvanceBossTrial(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "pull") || !uint.TryParse(args[1], out uint spell) ||
            !double.TryParse(args[2], CultureInfo.InvariantCulture, out double seconds) || seconds <= 0 || seconds > 900)
        { Log(false, line + " invalid arguments"); return true; }
        double now = NowSeconds();
        if (_bossTrialDeadline == 0)
        {
            if (!_entities.TryGet(_selectionGuid, out WorldEntity boss) || !boss.IsCreature || !CanActorAttack(boss, ControlledGuid))
            { Log(false, line + " requires a living attackable creature selected"); return true; }
            _bossTrialGuid = boss.Guid; _bossTrialMaxHealth = boss.Fields.MaxHealth;
            _bossTrialStart = _bossTrialCastAt = now; _bossTrialReengageAt = now + 1.5; _bossTrialDeadline = now + seconds;
            _bossTrialPull = args.Length == 4; _bossTrialChases = 0; _bossTrialOutOfReachSince = 0;
            _currentVantage = $"boss-{boss.Entry}-start"; ArmGameplayDump();
            if (_bossTrialPull)
            {
                CommitSelection(boss.Guid, false);
                _bossTrialJoinAt = now + 0.5;
            }
            else CommitSelection(boss.Guid, true);
        }
        bool present = _entities.TryGet(_bossTrialGuid, out WorldEntity target);
        var group = _partyMembers.Select(m => m.Guid).Append(ControlledGuid).Distinct().ToList();
        int seen = 0, dead = 0;
        foreach (ulong g in group)
            if (_entities.TryGet(g, out WorldEntity m)) { seen++; if (m.IsDead) dead++; }
        bool selfDead = _entities.TryGet(ControlledGuid, out WorldEntity self) && self.IsDead;
        string outcome = present && target.IsDead ? "KILLED" : seen > 0 && dead == seen ? "WIPE" : now >= _bossTrialDeadline ? "TIMEOUT" : "";
        if (outcome.Length > 0)
        {
            string detail = FormattableString.Invariant(
                $"entry={(present ? target.Entry : 0)};outcome={outcome};seconds={now - _bossTrialStart:F0};bossHealth={(present ? target.Fields.Health : 0)}/{_bossTrialMaxHealth};groupDead={dead}/{seen};selfDead={selfDead};chases={_bossTrialChases}");
            EmitCombat("BossTrial", "verdict", _bossTrialGuid, detail);
            Log(outcome == "KILLED", $"{line} {detail}");
            _currentVantage = $"boss-{(present ? target.Entry : 0)}-end"; ArmGameplayDump();
            _bossTrialDeadline = 0;
            return true;
        }
        // Every 5 s: where the boss and every group member stand, and their health. A boss that cannot path to its
        // victim for 24 s EVADES and heals to full (Core "[EVADE] ... target unreachable"): Baron Ashbury reset six
        // times on a ranged party bot 15 yd away and the trial timed out at 80% - these lines find that spot.
        if (now >= _bossTrialStateAt)
        {
            _bossTrialStateAt = now + 5;
            var members = group.Where(g => _entities.TryGet(g, out _)).Select(g =>
            {
                _entities.TryGet(g, out WorldEntity m);
                return string.Create(CultureInfo.InvariantCulture,
                    $"0x{g:X}@{m.Position.X:F1}|{m.Position.Y:F1}|{m.Position.Z:F1}:{m.Fields.Health}/{m.Fields.MaxHealth}{(m.IsDead ? ":dead" : "")}");
            });
            EmitCombat("BossTrialState", "diagnostic", _bossTrialGuid, string.Create(CultureInfo.InvariantCulture,
                $"t={now - _bossTrialStart:F0};boss={(present ? target.Fields.Health : 0)}/{_bossTrialMaxHealth}@{(present ? target.Position.X : 0):F1}|{(present ? target.Position.Y : 0):F1}|{(present ? target.Position.Z : 0):F1};bossTarget=0x{(present ? target.Fields.Target ?? 0 : 0):X};group={string.Join(',', members)}"));
        }
        if (!present || selfDead) return false;   // a dead tester waits for the group to finish it
        if (_bossTrialPull && _controller is not null)
        {
            if (now < _bossTrialJoinAt) return false;
            float reach = System.Numerics.Vector3.Distance(target.Position, _controller.Position);
            if (_bossTrialJoinAt > 0 && reach > 5.5f) { TeleportBeside(target); _bossTrialJoinAt = 0; _bossTrialReengageAt = now + 1.0; return false; }
            _bossTrialJoinAt = 0;
            var toBoss = target.Position - _controller.Position;   // face it, as a player does (BADFACING otherwise)
            if (toBoss.X != 0f || toBoss.Y != 0f)
            {
                float yaw = MathF.Atan2(toBoss.Y, toBoss.X);
                _controller.Yaw = yaw; _window.Camera.Yaw = yaw; _window.Camera.OrbitYaw = 0;
            }
            if (reach <= 5.5f) _bossTrialOutOfReachSince = 0;
            else if (_bossTrialOutOfReachSince == 0) _bossTrialOutOfReachSince = now;
            else if (now - _bossTrialOutOfReachSince > 2.5)
            {
                TeleportBeside(target); _bossTrialChases++; _bossTrialOutOfReachSince = 0; _bossTrialReengageAt = now + 1.0;
                return false;
            }
        }
        if (spell != 0 && now >= _bossTrialCastAt) { TryCast(LiveRotationSpell(spell)); _bossTrialCastAt = now + 2.1; }
        if (_attackTargetGuid == 0 && now >= _bossTrialReengageAt && CanActorAttack(target, ControlledGuid))
        {
            _bossTrialReengageAt = now + 1.5;
            CommitSelection(_bossTrialGuid, true);
        }
        return false;
    }
}
