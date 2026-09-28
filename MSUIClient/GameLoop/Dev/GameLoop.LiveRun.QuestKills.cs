using System.Globalization;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _questKillTarget, _questKillLootGuid;
    private double _questKillDeadline, _questKillStart, _questKillCastAt, _questKillEngageAt, _questKillPauseUntil,
        _questKillNoteAt, _questKillOutOfReachSince, _questKillLootUntil;
    private Vector3 _questKillAnchor;
    private int _questKillPulls, _questKillAdds, _questKillDeaths, _questKillRests, _questKillChases, _questKillLoots;
    private uint _questKillStartProgress;
    private bool _questKillLootRequested;

    /// <summary>
    /// kill-for-quest &lt;quest&gt; &lt;kill:N|item:ID&gt; &lt;entry&gt; &lt;count&gt; &lt;spell|0&gt; &lt;seconds&gt; [radius]: earn one
    /// objective the way a player does, from the camp the step starts in (the anchor), until the quest log's kill
    /// counter N (or the carried count of item ID) reaches count. Each frame: fight whatever is ATTACKING the tester
    /// first (camps aggro together); else pull the nearest living &lt;entry&gt; within radius of the anchor - the GM
    /// teleport (movement SETUP) lands BESIDE it, never idle inside the camp (the per-spawn protocol stood 12 s in
    /// the Ripper's Den being hit and died before it swung, 2026-09-27); none alive = wait at the anchor for the
    /// respawn (a camp smaller than the kill count is a design finding, the verdict shows the wait). Item
    /// objectives loot every kill of the entry. Between pulls, below 60% health: ".replenish" (SETUP for eating);
    /// a death: ".revive" + ".replenish" (SETUP), counted. The QuestKills verdict carries the balance numbers.
    /// </summary>
    private bool AdvanceQuestKills(string line)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool itemMode = a.Length > 2 && a[2].StartsWith("item:", StringComparison.OrdinalIgnoreCase);
        if (a.Length is < 7 or > 8 || !uint.TryParse(a[1], out uint quest) ||
            !(a[2].StartsWith("kill:", StringComparison.OrdinalIgnoreCase) || itemMode) ||
            !uint.TryParse(a[2][(a[2].IndexOf(':') + 1)..], out uint objective) || (!itemMode && objective > 3) ||
            !uint.TryParse(a[3], out uint entry) || !uint.TryParse(a[4], out uint count) || count == 0 ||
            !uint.TryParse(a[5], out uint spell) ||
            !double.TryParse(a[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds is <= 0 or > 3600)
        { Log(false, line + " invalid arguments"); return true; }
        float radius = a.Length == 8 && float.TryParse(a[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : 80f;
        double now = NowSeconds();
        if (_net is null || _controller is null || !_entities.TryGet(ControlledGuid, out WorldEntity me))
        { _questKillDeadline = 0; Log(false, line + " no controlled body"); return true; }

        uint? counters = null;
        foreach (var q in me.Fields.QuestLog()) if (q.QuestId == quest) counters = q.Counters;
        uint progress = itemMode ? Math.Min(CarriedAmmoCount(me, objective), count)
            : counters is { } packed ? QuestHelperUiLaw.ObjectiveProgress(packed, (int)objective, count) : 0;
        if (_questKillDeadline == 0)
        {
            if (counters is null) { Log(false, line + " quest not in the log"); return true; }
            _questKillDeadline = now + seconds; _questKillStart = now; _questKillAnchor = _controller.Position;
            _questKillTarget = _questKillLootGuid = 0; _questKillLootRequested = false;
            _questKillPulls = _questKillAdds = _questKillDeaths = _questKillRests = _questKillChases = _questKillLoots = 0;
            _questKillStartProgress = progress; _questKillPauseUntil = _questKillNoteAt = _questKillOutOfReachSince = 0;
        }
        bool done = progress >= count;
        if (done || now >= _questKillDeadline)
        {
            if (_loot.IsOpen) ReleaseLoot();
            string detail =
                string.Create(CultureInfo.InvariantCulture, $"quest={quest};objective={a[2]};entry={entry};progress={progress}/{count};gained={progress - _questKillStartProgress};") +
                string.Create(CultureInfo.InvariantCulture, $"pulls={_questKillPulls};adds={_questKillAdds};deaths={_questKillDeaths};rests={_questKillRests};") +
                string.Create(CultureInfo.InvariantCulture, $"chases={_questKillChases};loots={_questKillLoots};seconds={now - _questKillStart:F0}");
            EmitCombat("QuestKills", "verdict", 0, detail);
            Log(done, $"{line} {detail}");
            _questKillDeadline = 0; _questKillTarget = 0;
            return true;
        }
        if (now < _questKillPauseUntil) return false;

        if (me.IsDead)
        {
            // SETUP: a fair tester stands back up at full health (".revive" alone is 50%); the death is the finding.
            _questKillDeaths++; _questKillTarget = 0; _questKillLootGuid = 0;
            SendGmCommand($".revive {_net.PlayerName}", "protocol-runner-quest-kills-revive");
            CommitSelection(ControlledGuid, false);
            SendGmCommand(".replenish", "protocol-runner-quest-kills-replenish");
            _questKillPauseUntil = now + 3;
            return false;
        }

        WorldEntity? target = null;
        if (_questKillTarget != 0 && _entities.TryGet(_questKillTarget, out WorldEntity current) && !current.IsDead &&
            CanActorAttack(current, ControlledGuid))
            target = current;
        else if (_questKillTarget != 0)
        {
            // The target died (or left): an item objective loots a kill of its entry before anything else.
            if (itemMode && _entities.TryGet(_questKillTarget, out WorldEntity killed) && killed.IsDead && killed.Entry == entry)
            { _questKillLootGuid = killed.Guid; _questKillLootRequested = false; _questKillLootUntil = now + 4; }
            _questKillTarget = 0;
            _questKillPauseUntil = now + 0.8;   // the corpse's lootable flag and the next attacker arrive
            return false;
        }

        if (target is null)
        {
            WorldEntity? attacker = _entities.Units
                .Where(u => u.IsCreature && !u.IsDead && u.Fields.Target == ControlledGuid && CanActorAttack(u, ControlledGuid) &&
                            Vector3.Distance(u.Position, _controller.Position) < 40f)
                .OrderBy(u => Vector3.Distance(u.Position, _controller.Position)).FirstOrDefault();
            if (attacker is not null) { target = attacker; _questKillAdds++; }
            else if (_questKillLootGuid != 0 || (_loot.IsOpen && now < _questKillLootUntil))
            {
                // Nothing attacking: loot the last kill of an item objective - request once the corpse is lootable,
                // take all, release a moment later (the take-all packets go first).
                if (_questKillLootGuid == 0) return false;
                if (now >= _questKillLootUntil) { if (_loot.IsOpen) ReleaseLoot(); _questKillLootGuid = 0; }
                else if (_loot.IsOpen && _loot.Source == _questKillLootGuid)
                { TakeAllLoot(); _questKillLoots++; _questKillLootUntil = now + 0.6; _questKillLootGuid = 0; }
                else if (!_questKillLootRequested && _entities.TryGet(_questKillLootGuid, out WorldEntity corpse) && corpse.Fields.Lootable)
                    _questKillLootRequested = RequestLoot(_questKillLootGuid);
                return false;
            }
            else if (_loot.IsOpen) { ReleaseLoot(); return false; }
            else if (me.Fields.MaxHealth > 0 && me.Fields.Health < me.Fields.MaxHealth * 0.6f)
            {
                // SETUP stand-in for eating between pulls.
                CommitSelection(ControlledGuid, false);
                SendGmCommand(".replenish", "protocol-runner-quest-kills-rest");
                _questKillRests++; _questKillPauseUntil = now + 1.5;
                return false;
            }
            else
            {
                Vector2 anchor = new(_questKillAnchor.X, _questKillAnchor.Y);
                WorldEntity? prey = _entities.Units
                    .Where(u => u.IsCreature && !u.IsDead && u.Entry == entry && CanActorAttack(u, ControlledGuid) &&
                                Vector2.Distance(new(u.Position.X, u.Position.Y), anchor) <= radius)
                    .OrderBy(u => Vector3.Distance(u.Position, _controller.Position)).FirstOrDefault();
                if (prey is null)
                {
                    if (Vector2.Distance(new(_controller.Position.X, _controller.Position.Y), anchor) > 30f)
                        SendGmCommand(string.Create(CultureInfo.InvariantCulture,
                            $".go xyz {_questKillAnchor.X:R} {_questKillAnchor.Y:R} {_questKillAnchor.Z + 0.5f:R} {_config.Start.Map}"),
                            "protocol-runner-quest-kills-anchor");
                    if (now >= _questKillNoteAt)
                    {
                        EmitCombat("QuestKillsWaiting", "diagnostic", 0, FormattableString.Invariant(
                            $"quest={quest};entry={entry};progress={progress}/{count};reason=no-living-{entry}-within-{radius:F0}yd;waited={now - _questKillStart:F0}s"));
                        _questKillNoteAt = now + 15;
                    }
                    _questKillPauseUntil = now + 2;
                    return false;
                }
                TeleportBeside(prey);
                target = prey; _questKillPulls++;
                _questKillTarget = prey.Guid; _questKillEngageAt = 0; _questKillOutOfReachSince = 0;
                _questKillPauseUntil = now + 1.0;   // land beside it before swinging
                return false;
            }
            _questKillTarget = target.Guid; _questKillEngageAt = 0; _questKillOutOfReachSince = 0;
        }

        // Fighting: face it, (re)start auto-attack, the rotation every 2.1 s; a target out of melee reach for 2.5 s
        // (it fled, or a caster kept its distance) is followed by teleport - a player would run to it.
        float reach = Vector3.Distance(target.Position, _controller.Position);
        if (reach > 5.5f)
        {
            if (_questKillOutOfReachSince == 0) _questKillOutOfReachSince = now;
            else if (now - _questKillOutOfReachSince > 2.5)
            {
                TeleportBeside(target); _questKillChases++; _questKillOutOfReachSince = 0;
                _questKillPauseUntil = now + 1.0; return false;
            }
        }
        else _questKillOutOfReachSince = 0;
        if (now >= _questKillEngageAt)
        {
            Vector3 dir = target.Position - _controller.Position;
            if (dir.X != 0f || dir.Y != 0f)
            {
                float yaw = MathF.Atan2(dir.Y, dir.X);
                _controller.Yaw = yaw; _window.Camera.Yaw = yaw; _window.Camera.OrbitYaw = 0;
            }
            if (_selectionGuid != target.Guid || _attackTargetGuid != target.Guid) CommitSelection(target.Guid, true);
            _questKillEngageAt = now + 1.5;
        }
        if (spell != 0 && now >= _questKillCastAt) { TryCast(LiveRotationSpell(spell)); _questKillCastAt = now + 2.1; }
        return false;
    }

    /// <summary>
    /// A rotation names a spell by ONE rank; cast the highest rank of that chain the character knows. A trained
    /// level-42 warrior knows Heroic Strike rank 6 (11565), which supersedes the rank 5 (11564) the protocol named:
    /// every cast was refused "You have not learned that spell" and every fight ran on auto-attack alone.
    /// </summary>
    private uint LiveRotationSpell(uint spell)
    {
        if (spell == 0 || _actions.KnownSpells.Contains(spell)) return spell;
        if (_mpq is not null) _skillLines ??= SkillLineCatalog.Load(_mpq);
        if (_skillLines is null) return spell;
        uint root = _skillLines.AbilityChainRoot(spell);
        uint best = spell; int bestRank = 0;
        foreach (uint known in _actions.KnownSpells)
            if (_skillLines.AbilityChainRoot(known) == root && _skillLines.AbilityRank(known) is var rank && rank > bestRank)
            { best = known; bestRank = rank; }
        return best;
    }

    /// <summary>GM teleport (movement SETUP) to 2.5 yd from the unit on the side the tester comes from.</summary>
    private void TeleportBeside(WorldEntity unit)
    {
        if (_controller is null) return;
        Vector2 away = new(_controller.Position.X - unit.Position.X, _controller.Position.Y - unit.Position.Y);
        away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX;
        Vector3 spot = new(unit.Position.X + away.X * 2.5f, unit.Position.Y + away.Y * 2.5f, unit.Position.Z + 0.5f);
        SendGmCommand(string.Create(CultureInfo.InvariantCulture, $".go xyz {spot.X:R} {spot.Y:R} {spot.Z:R} {_config.Start.Map}"),
            "protocol-runner-quest-kills-approach");
    }
}
