using System.Globalization;
using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// WoW Karting live protocol commands (shared_docs/WOW_KARTING.md). They drive the
// real controller with the real W key, exactly like walk-to, so what they measure is
// what a driver gets: the server-granted kart speed, a Real Portal crossing at that
// speed, and where the far side puts you.
//
//   kart-assert <mountDisplay> <minRunSpeed>
//       the acting body rides that display and the server granted at least that run speed
//   drive <timeout> <radius> x,y [x,y ...]
//       drive the route; fails when stuck 5 s, out of time, or a teleport/map change interrupts it
//   drive-through <map> <timeout> x,y [x,y ...]
//       drive the route INTO a portal: passes when the map becomes <map> (a same-map portal: when
//       the body jumps > 40 yd in one frame); logs speed, crossing time and the arrival
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private readonly Queue<Vector2> _liveDriveRoute = new();
    // Waypoints written "x,y,j": press jump on reaching them (kart gap jumps, course v2 rooftops).
    private readonly HashSet<Vector2> _liveDriveJumps = new();
    private double _liveDriveJumpUntil;
    private string _liveDriveLine = "";
    private bool _liveDriveThrough;
    private int _liveDriveTargetMap, _liveDriveStartMap;
    private float _liveDriveRadius, _liveDriveBest, _liveDriveMaxSpeed, _liveDriveDistance;
    private double _liveDriveDeadline, _liveDriveStartedAt, _liveDriveProgressAt, _liveDriveSampleAt;
    private Vector3 _liveDriveSamplePos, _liveDriveLastPos;

    private bool LiveDriveActive => _liveDriveLine.Length > 0;

    /// <summary>
    /// roof-scan &lt;x0&gt; &lt;y0&gt; &lt;x1&gt; &lt;y1&gt; &lt;step&gt; &lt;name&gt;: the highest collision surface every step yards over a
    /// rectangle (rays straight down through the client's collision world - WMO roofs included, which the
    /// server's terrain-only height query cannot see), written to dumps/roofscan-&lt;name&gt;.csv as x,y,z,normalZ.
    /// The area must be loaded: stand in it. For laying kart track on rooftops (WoW Karting course v2).
    /// </summary>
    private void LiveRoofScan(string line)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 7 || _collision is null) { Log(false, line + " (needs 6 args and a loaded collision world)"); return; }
        float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
        float x0 = MathF.Min(F(1), F(3)), x1 = MathF.Max(F(1), F(3)), y0 = MathF.Min(F(2), F(4)), y1 = MathF.Max(F(2), F(4));
        float step = MathF.Max(0.5f, F(5));
        var sb = new System.Text.StringBuilder("x,y,z,nz,zr\n");
        int hits = 0, total = 0;
        for (float x = x0; x <= x1; x += step)
            for (float y = y0; y <= y1; y += step)
            {
                total++;
                if (_collision.Raycast(new Vector3(x, y, 400f), -Vector3.UnitZ, 900f) is { } hit)
                {
                    hits++;
                    float? rendered = _wmo?.RaycastRendered(new Vector3(x, y, 400f), -Vector3.UnitZ, 900f);
                    string zr = rendered is float t ? (400f - t).ToString("F2", CultureInfo.InvariantCulture) : "";
                    sb.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1},{hit.Point.Z:F2},{hit.Normal.Z:F3},{zr}\n");
                }
            }
        string path = Path.Combine(_config.RepoRoot, "dumps", $"roofscan-{SafeCaptureName(a[6])}.csv");
        File.WriteAllText(path, sb.ToString());
        Log(true, $"{line} hits={hits}/{total} -> {path}");
    }

    private void LiveKartAssert(string line)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int display = int.Parse(a[1], CultureInfo.InvariantCulture);
        float minSpeed = float.Parse(a[2], CultureInfo.InvariantCulture);
        int mounted = _entities.TryGet(ControlledGuid, out WorldEntity body) ? body.MountDisplayId : 0;
        float speed = _controller?.EffectiveRunSpeed ?? 0f;
        Log(mounted == display && speed >= minSpeed,
            FormattableString.Invariant($"{line} mount={mounted};runSpeed={speed:F2}"));
    }

    private void StartLiveDrive(string line, bool through, double now)
    {
        string[] a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 1;
        _liveDriveThrough = through;
        _liveDriveTargetMap = through ? int.Parse(a[i++], CultureInfo.InvariantCulture) : -1;
        _liveDriveDeadline = now + double.Parse(a[i++], CultureInfo.InvariantCulture);
        _liveDriveRadius = through ? 2f : float.Parse(a[i++], CultureInfo.InvariantCulture);
        _liveDriveRoute.Clear();
        _liveDriveJumps.Clear();
        for (; i < a.Length; i++)
        {
            string[] xy = a[i].Split(',');
            var point = new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture),
                float.Parse(xy[1], CultureInfo.InvariantCulture));
            _liveDriveRoute.Enqueue(point);
            if (xy.Length > 2 && xy[2] == "j") _liveDriveJumps.Add(point);
        }
        _liveDriveLine = line;
        _liveDriveStartMap = _config.Start.Map;
        _liveDriveBest = float.MaxValue;
        _liveDriveStartedAt = _liveDriveProgressAt = _liveDriveSampleAt = now;
        _liveDriveSamplePos = _liveDriveLastPos = _controller?.Position ?? Vector3.Zero;
        _liveDriveMaxSpeed = 0f;
        _liveDriveDistance = 0f;
    }

    private void EndLiveDrive(bool pass, string verdict)
    {
        _liveHeld.Remove("W");
        Vector3 at = _controller?.Position ?? Vector3.Zero;
        double seconds = NowSeconds() - _liveDriveStartedAt;
        float average = seconds > 0 ? _liveDriveDistance / (float)seconds : 0f;
        float yaw = _controller?.Yaw ?? 0f;
        Log(pass, string.Create(CultureInfo.InvariantCulture,
            $"{_liveDriveLine.Split(' ')[0]} {verdict} map={_config.Start.Map} at ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) yaw={yaw:F2} t={seconds:F1}s driven={_liveDriveDistance:F0}yd avg={average:F1} max={_liveDriveMaxSpeed:F1} runSpeed={_controller?.EffectiveRunSpeed ?? 0:F1} waypointsLeft={_liveDriveRoute.Count}"));
        // A failed drive captures where it stopped: the blocker (a trunk, a rock, a lip) is rarely in the numbers.
        if (!pass) { _currentVantage = "drive-" + verdict; ArmGameplayDump(); }
        _liveDriveLine = "";
        _liveDriveRoute.Clear();
        _liveDriveJumps.Clear();
        _liveStep++;
    }

    /// <summary>One frame of drive / drive-through; true while the step is still driving.</summary>
    private bool AdvanceLiveDrive(double now)
    {
        if (!LiveDriveActive || _controller is null) return false;
        Vector3 pos = _controller.Position;
        bool mapChanged = _config.Start.Map != _liveDriveStartMap;
        bool jumped = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(_liveDriveLastPos.X, _liveDriveLastPos.Y)) > 40f;
        if (!jumped && !mapChanged)
            _liveDriveDistance += Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(_liveDriveLastPos.X, _liveDriveLastPos.Y));
        _liveDriveLastPos = pos;

        if (_liveDriveThrough && (mapChanged ? _config.Start.Map == _liveDriveTargetMap : jumped && _liveDriveTargetMap == _liveDriveStartMap))
        {
            EndLiveDrive(true, "crossed");
            return true;
        }
        if (mapChanged || jumped)
        {
            EndLiveDrive(false, mapChanged ? "interrupted-by-map-change" : "interrupted-by-teleport");
            return true;
        }

        if (now - _liveDriveSampleAt >= 0.25)
        {
            float speed = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(_liveDriveSamplePos.X, _liveDriveSamplePos.Y)) /
                (float)(now - _liveDriveSampleAt);
            _liveDriveMaxSpeed = MathF.Max(_liveDriveMaxSpeed, speed);
            _liveDriveSamplePos = pos;
            _liveDriveSampleAt = now;
        }

        var here = new Vector2(pos.X, pos.Y);
        while (_liveDriveRoute.Count > 0)
        {
            Vector2 next = _liveDriveRoute.Peek();
            float left = Vector2.Distance(here, next);
            // A drive-through keeps its LAST point (the far side of the portal) as the aim until it crosses.
            bool last = _liveDriveRoute.Count == 1;
            bool jumpPoint = _liveDriveJumps.Contains(next);
            if (left <= (last && _liveDriveThrough ? 0f : jumpPoint ? 1.5f : MathF.Max(_liveDriveRadius, 6f)))
            {
                if (jumpPoint) _liveDriveJumpUntil = now + 0.2;
                _liveDriveRoute.Dequeue();
                _liveDriveBest = float.MaxValue;
                _liveDriveProgressAt = now;
                continue;
            }
            if (left < _liveDriveBest - 0.5f) { _liveDriveBest = left; _liveDriveProgressAt = now; }
            if (now - _liveDriveProgressAt > 5) { EndLiveDrive(false, "stuck"); return true; }
            if (now >= _liveDriveDeadline) { EndLiveDrive(false, "timeout"); return true; }
            float yaw = MathF.Atan2(next.Y - here.Y, next.X - here.X);
            _controller.Yaw = yaw; _window.Camera.Yaw = yaw; _window.Camera.OrbitYaw = 0;
            _liveHeld.Add("W");
            if (now < _liveDriveJumpUntil) _liveHeld.Add("SPACE"); else _liveHeld.Remove("SPACE");
            return true;
        }
        EndLiveDrive(!_liveDriveThrough, _liveDriveThrough ? "route-ended-without-crossing" : "arrived");
        return true;
    }
}
