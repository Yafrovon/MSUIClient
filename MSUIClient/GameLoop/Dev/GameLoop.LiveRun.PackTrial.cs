using System.Globalization;
using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint[] _packTrialLows = [];
    private readonly Dictionary<uint, ulong> _packTrialGuids = new();
    private readonly HashSet<ulong> _packTrialAdds = new();
    private ulong _packTrialTarget;
    private double _packTrialStart, _packTrialDeadline, _packTrialResolveUntil, _packTrialEngageAt, _packTrialCastAt,
        _packTrialStateAt, _packTrialOutOfReachSince, _packTrialPauseUntil;
    private int _packTrialChases;

    /// <summary>
    /// pack-trial &lt;spell|0&gt; &lt;seconds&gt; &lt;lowGuid,lowGuid,...&gt;: a TRASH pull by the tester and its group, the way a
    /// dungeon group takes a linked pack (or a patrol) - the boss-trial of the pulls between bosses. The members are the
    /// pack's spawn guids (the generator reads them from creature_groups). The tester opens beside the leader (a GM
    /// teleport = movement SETUP; the runner cannot path a moving fight) and its SuperUI group joins on its target (the
    /// PlayerParty doctrine: companions attack the player's target, healers heal); kill order: members attacking the
    /// group, then any member, then adds. It ends CLEARED when every member is dead and nothing else still fights the
    /// group (the only pass), WIPE when every group member in sight is dead, or at the timeout. The PackTrial verdict
    /// carries the numbers: seconds, members killed, adds (other creatures that joined), who died.
    /// </summary>
    private bool AdvancePackTrial(string line)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length != 4 || !uint.TryParse(a[1], out uint spell) ||
            !double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds is <= 0 or > 900)
        { Log(false, line + " invalid arguments"); return true; }
        double now = NowSeconds();
        if (_controller is null || _net is null) { Log(false, line + " no controlled body"); return true; }
        if (_packTrialDeadline == 0)
        {
            if (_packTrialResolveUntil == 0)
            {
                _packTrialLows = a[3].Split(',').Select(s => uint.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                _packTrialGuids.Clear(); _packTrialAdds.Clear();
                _packTrialResolveUntil = now + 5;
            }
            foreach (var u in _entities.Units)
                if (u.IsCreature && Array.IndexOf(_packTrialLows, (uint)(u.Guid & 0xFFFFFF)) >= 0) _packTrialGuids[(uint)(u.Guid & 0xFFFFFF)] = u.Guid;
            var leaders = _packTrialLows.Select(l => _packTrialGuids.TryGetValue(l, out ulong g) && _entities.TryGet(g, out WorldEntity e) && !e.IsDead ? e : null)
                .Where(e => e is not null).Select(e => e!).ToList();
            if (leaders.Count == 0)
            {
                if (now < _packTrialResolveUntil) return false;
                _packTrialResolveUntil = 0;
                Log(false, $"{line} pack not visible: resolved {_packTrialGuids.Count}/{_packTrialLows.Length}, none alive");
                return true;
            }
            _packTrialResolveUntil = 0;
            _packTrialStart = now; _packTrialDeadline = now + seconds; _packTrialStateAt = now;
            _packTrialTarget = 0; _packTrialChases = 0; _packTrialOutOfReachSince = 0; _packTrialCastAt = now;
            CommitSelection(leaders[0].Guid, false);
            _packTrialPauseUntil = now;
        }

        foreach (var u in _entities.Units)   // members that came into sight after the start
            if (u.IsCreature && Array.IndexOf(_packTrialLows, (uint)(u.Guid & 0xFFFFFF)) >= 0) _packTrialGuids[(uint)(u.Guid & 0xFFFFFF)] = u.Guid;
        var group = _partyMembers.Select(m => m.Guid).Append(ControlledGuid).Distinct().ToList();
        bool Engaged(WorldEntity u)
        {
            ulong victim = u.Fields.Target ?? 0;
            return u.IsCreature && !u.IsDead && victim != 0 && group.Contains(victim) && Vector3.Distance(u.Position, _controller.Position) < 45f;
        }
        foreach (var u in _entities.Units)
            if (Engaged(u) && Array.IndexOf(_packTrialLows, (uint)(u.Guid & 0xFFFFFF)) < 0) _packTrialAdds.Add(u.Guid);

        int killed = 0, alive = 0;
        foreach (uint low in _packTrialLows)
        {
            if (!_packTrialGuids.TryGetValue(low, out ulong g) || !_entities.TryGet(g, out WorldEntity m)) { alive++; continue; }   // unseen = not proven dead
            if (m.IsDead) killed++; else alive++;
        }
        bool fighting = _entities.Units.Any(Engaged);
        int seen = 0, dead = 0;
        foreach (ulong g in group)
            if (_entities.TryGet(g, out WorldEntity m)) { seen++; if (m.IsDead) dead++; }
        bool selfDead = _entities.TryGet(ControlledGuid, out WorldEntity me) && me.IsDead;
        string outcome = alive == 0 && !fighting ? "CLEARED" : seen > 0 && dead == seen ? "WIPE" : now >= _packTrialDeadline ? "TIMEOUT" : "";
        if (outcome.Length > 0)
        {
            string entries = string.Join(',', _packTrialLows.Select(l => _packTrialGuids.TryGetValue(l, out ulong g) && _entities.TryGet(g, out WorldEntity e) ? e.Entry : 0u));
            string detail = FormattableString.Invariant(
                $"pack={a[3]};entries={entries};outcome={outcome};seconds={now - _packTrialStart:F0};killed={killed}/{_packTrialLows.Length};adds={_packTrialAdds.Count};groupDead={dead}/{seen};selfDead={selfDead};chases={_packTrialChases}");
            EmitCombat("PackTrial", "verdict", _packTrialGuids.Values.FirstOrDefault(), detail);
            Log(outcome == "CLEARED", $"{line} {detail}");
            if (outcome != "CLEARED") { _currentVantage = $"pack-{_packTrialLows[0]}-end"; ArmGameplayDump(); }   // a picture only of a failure
            _packTrialDeadline = 0;
            return true;
        }
        if (now >= _packTrialStateAt)
        {
            _packTrialStateAt = now + 5;
            string mobs = string.Join(',', _packTrialLows.Select(l => _packTrialGuids.TryGetValue(l, out ulong g) && _entities.TryGet(g, out WorldEntity e)
                ? string.Create(CultureInfo.InvariantCulture, $"{l}@{e.Position.X:F1}|{e.Position.Y:F1}:{e.Fields.Health}/{e.Fields.MaxHealth}{(e.IsDead ? ":dead" : "")}")
                : $"{l}:unseen"));
            string members = string.Join(',', group.Where(g => _entities.TryGet(g, out _)).Select(g =>
            {
                _entities.TryGet(g, out WorldEntity m);
                return string.Create(CultureInfo.InvariantCulture,
                    $"0x{g:X}@{m.Position.X:F1}|{m.Position.Y:F1}:{m.Fields.Health}/{m.Fields.MaxHealth}{(m.IsDead ? ":dead" : "")}");
            }));
            EmitCombat("PackTrialState", "diagnostic", _packTrialTarget, FormattableString.Invariant(
                $"t={now - _packTrialStart:F0};mobs={mobs};adds={_packTrialAdds.Count};group={members}"));
        }
        if (selfDead || now < _packTrialPauseUntil) return false;   // a dead tester waits for the group to finish it

        WorldEntity target;
        if (!(_packTrialTarget != 0 && _entities.TryGet(_packTrialTarget, out target) && !target.IsDead && CanActorAttack(target, ControlledGuid)))
        {
            var living = new List<WorldEntity>();
            foreach (ulong g in _packTrialGuids.Values)
                if (_entities.TryGet(g, out WorldEntity e) && !e.IsDead && CanActorAttack(e, ControlledGuid)) living.Add(e);
            var order = living.Where(Engaged).OrderBy(e => Vector3.Distance(e.Position, _controller.Position))
                .Concat(living.OrderBy(e => Vector3.Distance(e.Position, _controller.Position)))
                .Concat(_entities.Units.Where(u => Engaged(u) && CanActorAttack(u, ControlledGuid)).OrderBy(u => Vector3.Distance(u.Position, _controller.Position)))
                .Take(1).ToList();
            if (order.Count == 0) return false;   // members out of sight and nothing fighting: the timeout decides
            target = order[0];
            _packTrialTarget = target.Guid; _packTrialEngageAt = 0; _packTrialOutOfReachSince = 0;
            if (Vector3.Distance(target.Position, _controller.Position) > 5.5f)
            {
                TeleportBeside(target);   // movement SETUP, as kill-for-quest
                _packTrialPauseUntil = now + 1.0;
                return false;
            }
        }
        float reach = Vector3.Distance(target.Position, _controller.Position);
        if (reach > 5.5f)
        {
            if (_packTrialOutOfReachSince == 0) _packTrialOutOfReachSince = now;
            else if (now - _packTrialOutOfReachSince > 2.5)
            {
                TeleportBeside(target); _packTrialChases++; _packTrialOutOfReachSince = 0;
                _packTrialPauseUntil = now + 1.0; return false;
            }
        }
        else _packTrialOutOfReachSince = 0;
        if (now >= _packTrialEngageAt)
        {
            Vector3 dir = target.Position - _controller.Position;
            if (dir.X != 0f || dir.Y != 0f)
            {
                float yaw = MathF.Atan2(dir.Y, dir.X);
                _controller.Yaw = yaw; _window.Camera.Yaw = yaw; _window.Camera.OrbitYaw = 0;
            }
            if (_selectionGuid != target.Guid || _attackTargetGuid != target.Guid) CommitSelection(target.Guid, true);
            _packTrialEngageAt = now + 1.5;
        }
        if (spell != 0 && now >= _packTrialCastAt) { TryCast(LiveRotationSpell(spell)); _packTrialCastAt = now + 2.1; }
        return false;
    }

    private uint _patrolWatchLow;
    private ulong _patrolWatchGuid;
    private double _patrolWatchStart, _patrolWatchDeadline, _patrolWatchResolveUntil, _patrolWatchStateAt;
    private float _patrolWatchClosest, _patrolWatchTravel;
    private Vector3 _patrolWatchHome;
    private bool _patrolWatchSeenAway;

    /// <summary>
    /// patrol-watch &lt;leaderLowGuid&gt; &lt;seconds&gt; &lt;farX&gt; &lt;farY&gt; [followerLow,...]: a patrol really walks its route -
    /// the leader (movement_type 2, creature_movement points) comes within 6 yd of the far point of its route in time,
    /// and every follower (creature_groups formation) stands within 10 yd of it there. Observed from a GM spot beside
    /// the route (a GM does not pull it). The PatrolWatch verdict: seconds to the far point, travel, followers near.
    /// The leader must be seen AWAY from the point (> 10 yd) before arriving counts: a leader resting at the far point
    /// when the watch began "passed" in 0 s with no travel (2026-09-27). patrol-at &lt;same arguments&gt; is the wait
    /// before a pull: it passes as soon as the leader is at the point (its home, where it pauses).
    /// </summary>
    private bool AdvancePatrolWatch(string line, bool mustTravel = true)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length is < 5 or > 6 || !uint.TryParse(a[1], out uint low) ||
            !double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds is <= 0 or > 900 ||
            !float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx) ||
            !float.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy))
        { Log(false, line + " invalid arguments"); return true; }
        uint[] followers = a.Length == 6 ? a[5].Split(',').Select(s => uint.Parse(s, CultureInfo.InvariantCulture)).ToArray() : [];
        double now = NowSeconds();
        if (_patrolWatchDeadline == 0)
        {
            if (_patrolWatchResolveUntil == 0) { _patrolWatchResolveUntil = now + 5; _patrolWatchLow = low; }
            var found = _entities.Units.Where(u => u.IsCreature && !u.IsDead && (uint)(u.Guid & 0xFFFFFF) == low).Take(1).ToList();
            if (found.Count == 0)
            {
                if (now < _patrolWatchResolveUntil) return false;
                _patrolWatchResolveUntil = 0;
                Log(false, $"{line} patrol leader {low} not visible");
                return true;
            }
            _patrolWatchResolveUntil = 0;
            _patrolWatchGuid = found[0].Guid; _patrolWatchHome = found[0].Position;
            _patrolWatchStart = now; _patrolWatchDeadline = now + seconds; _patrolWatchStateAt = now;
            _patrolWatchClosest = float.MaxValue; _patrolWatchTravel = 0f; _patrolWatchSeenAway = !mustTravel;
        }
        bool present = _entities.TryGet(_patrolWatchGuid, out WorldEntity lead) && !lead.IsDead;
        if (present)
        {
            float d = Vector2.Distance(new(lead.Position.X, lead.Position.Y), new(fx, fy));
            _patrolWatchClosest = MathF.Min(_patrolWatchClosest, d);
            _patrolWatchTravel = MathF.Max(_patrolWatchTravel, Vector3.Distance(lead.Position, _patrolWatchHome));
            if (d > 10f) _patrolWatchSeenAway = true;
            if (d <= 6f && _patrolWatchSeenAway)
            {
                var leadAt = lead.Position;
                int near = followers.Count(f => _entities.Units.Any(u => u.IsCreature && (uint)(u.Guid & 0xFFFFFF) == f && !u.IsDead &&
                                                                          Vector3.Distance(u.Position, leadAt) <= 10f));
                string detail = FormattableString.Invariant(
                    $"leader={low};entry={lead.Entry};reached=true;seconds={now - _patrolWatchStart:F0};travel={_patrolWatchTravel:F0};followersNear={near}/{followers.Length}");
                EmitCombat("PatrolWatch", "verdict", _patrolWatchGuid, detail);
                Log(near == followers.Length, $"{line} {detail}");
                _patrolWatchDeadline = 0;
                return true;
            }
        }
        if (now >= _patrolWatchStateAt && present)
        {
            _patrolWatchStateAt = now + 5;
            EmitCombat("PatrolWatchState", "diagnostic", _patrolWatchGuid, FormattableString.Invariant(
                $"t={now - _patrolWatchStart:F0};at={lead.Position.X:F1}|{lead.Position.Y:F1}|{lead.Position.Z:F1};toFar={_patrolWatchClosest:F1}"));
        }
        // Out of sight is not a failure (a patrol walks away from the watcher and comes back) - only the timeout is.
        if (now >= _patrolWatchDeadline)
        {
            string detail = FormattableString.Invariant(
                $"leader={low};reached=false;present={present};seconds={now - _patrolWatchStart:F0};travel={_patrolWatchTravel:F0};closest={_patrolWatchClosest:F1}");
            EmitCombat("PatrolWatch", "verdict", _patrolWatchGuid, detail);
            Log(false, $"{line} {detail}");
            _patrolWatchDeadline = 0;
            return true;
        }
        return false;
    }
}
