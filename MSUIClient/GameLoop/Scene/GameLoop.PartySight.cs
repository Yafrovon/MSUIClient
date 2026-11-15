using System.Numerics;
using MSUIClient.World;

namespace MSUIClient;

/// <summary>
/// Command View party sight (World/PartySight.cs): the primary's own view, reprojected to the
/// camera. This half feeds the pass each frame and mirrors its verdict on the CPU for picking,
/// so a click through the opened hillside lands on the cave floor the primary sees.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>The primary's eye while the Command View is up and the toggle is on; null = off.
    /// The creator sandbox has no entity stream: its "primary" is where the character stood when
    /// the view went up (ToggleFreeView parks it there and returns it there).</summary>
    private Vector3? PartySightEye()
    {
        // The World Builder camera sees the whole world, not one unit's view of it.
        if (!_freeView || !Settings.Controls.CommandViewPartySightExperimental || WbFreeCameraActive) return null;
        Vector3? feet = _net is { IsInWorld: true }
            ? CommandViewPrimarySubject()
            : CreatorInWorld ? _creatorFreeViewReturn : null;
        return feet is Vector3 f ? f + new Vector3(0f, 0f, PartySightPass.EyeHeight) : null;
    }

    /// <summary>A probe's stand-in for the mouse: the pixel the pick readback samples.</summary>
    private Vector2? _partySightCursorOverride;

    private void UpdatePartySight()
    {
        if (_partySight is null) return;
        // The pick readback wants the cursor pixel; a click next frame reads it back. Probes
        // may pin a pixel of their own (the headless run has no mouse over the world).
        _partySight.Cursor = _freeView ? _partySightCursorOverride ?? _window.MousePosition : null;
        _partySight.Update(PartySightEye(), _collision, _terrain, _window.Camera,
            _window.FramebufferSize, NowSeconds());
    }

    // ── CPU mirror of the shader rule, for picking ────────────────────────────────────────────
    // GPU: a fragment the primary cannot see, nearer the camera than a surface it can, is gone.
    // CPU: the same question for a candidate pick point, using the collision raycasts the cube
    // was rendered from plus a terrain height-field crossing test (the terrain mesh is in the
    // cube too). Approximate at the yard level, exact in spirit: what the picture cut, the
    // pick skips.

    /// <summary>Furthest a pick may look behind a cut point for the surface the primary sees.</summary>
    private const float PartySightMarchYards = 250f;

    private bool PartySightCutAway(Vector3 point)
    {
        if (!_freeView || _partySight is not { Engaged: true } sight) return false;
        if (PartySightSees(sight, point)) return false;
        Vector3 camera = _window.Camera.Position;
        Vector3 toPoint = point - camera;
        if (toPoint.LengthSquared() < 1e-6f) return false;
        Vector3 direction = Vector3.Normalize(toPoint);
        Vector3 from = point + direction * 0.1f;
        float remaining = PartySightMarchYards;
        for (int pass = 0; pass < 8 && remaining > 0f; pass++)
        {
            if (PartySightNextSolid(from, direction, remaining) is not Vector3 hit) return false;
            if (PartySightSees(sight, hit)) return true;
            float advance = Vector3.Distance(from, hit) + 0.1f;
            from = hit + direction * 0.1f;
            remaining -= advance;
        }
        return false;
    }

    /// <summary>Whether the primary's eye has an unblocked line to <paramref name="point"/>:
    /// nothing in the collision world and no terrain crossing along the way (within the bias).</summary>
    private bool PartySightSees(PartySightPass sight, Vector3 point)
    {
        Vector3 eye = sight.Eye;
        Vector3 delta = point - eye;
        float distance = delta.Length();
        if (distance > sight.Range || distance < 1e-3f) return true;
        float reach = distance - sight.Bias;
        if (reach <= 0f) return true;
        Vector3 direction = delta / distance;
        if (_collision?.AnyHit(eye, direction, reach) == true) return false;
        return PartySightTerrainCrossing(eye, direction, reach) is null;
    }

    /// <summary>The first solid along a ray: collision hit or terrain crossing, whichever is nearer.</summary>
    private Vector3? PartySightNextSolid(Vector3 from, Vector3 direction, float reach)
    {
        float best = float.PositiveInfinity;
        Vector3 hit = default;
        if (_collision?.Raycast(from, direction, reach) is { } collisionHit)
        {
            best = collisionHit.Distance;
            hit = collisionHit.Point;
        }
        if (PartySightTerrainCrossing(from, direction, MathF.Min(reach, best)) is Vector3 ground)
            return ground;
        return float.IsFinite(best) ? hit : null;
    }

    /// <summary>
    /// Where a ray crosses the terrain height field, or null. A CROSSING (the sample changes
    /// side), not "under the surface": a cave interior sits below the height field, and the
    /// primary inside it must still see its own floor.
    /// </summary>
    private Vector3? PartySightTerrainCrossing(Vector3 origin, Vector3 direction, float reach)
    {
        if (_terrain is null || reach <= 0f) return null;
        const float step = 0.75f;
        float? previous = null;
        float previousT = 0f;
        for (float t = 0f; t <= reach; t += step)
        {
            Vector3 p = origin + direction * t;
            if (_terrain.SampleHeight(p.X, p.Y) is not float g) { previous = null; previousT = t; continue; }
            float side = p.Z - g;
            if (previous is float last && (last > 0f) != (side > 0f))
            {
                // Refine between the two samples.
                float lo = previousT, hi = t;
                for (int i = 0; i < 6; i++)
                {
                    float mid = (lo + hi) * .5f;
                    Vector3 m = origin + direction * mid;
                    float ms = m.Z - (_terrain.SampleHeight(m.X, m.Y) ?? m.Z);
                    if ((last > 0f) == (ms > 0f)) lo = mid; else hi = mid;
                }
                Vector3 found = origin + direction * hi;
                return found with { Z = _terrain.SampleHeight(found.X, found.Y) ?? found.Z };
            }
            previous = side;
            previousT = t;
        }
        return null;
    }
}
