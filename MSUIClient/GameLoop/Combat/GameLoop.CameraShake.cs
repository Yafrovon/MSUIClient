using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// CAMERA SHAKE (SPELL_CREATOR_PLAN.md P4.3).
//
// A SpellVisualKit's f14 names a SpellEffectCameraShakes row, which names up to three
// CameraShakes rows - one wave each on the camera's right / forward / up axis. Whenever such a
// kit spawns (a cast, an impact, a ground burst - SpellEffectSource.KitShake), its waves start at
// the kit's position; every frame the live waves sum into Camera.ShakeOffset, which moves the
// VIEW only (collision, picking distances and the listener keep the true eye).
//
// CameraShakeLaw is the pure arithmetic: a wave is A * w(2π(f t + phase)) * (1 - t/D)^coefficient,
// w = sin (type 0) or |sin| (type 1, a bounce), A scaled to yards, faded by distance from the
// camera's target so a quake across the zone does not rattle the screen.
// ═══════════════════════════════════════════════════════════════════════════
public static class CameraShakeLaw
{
    /// <summary>Table amplitude units to yards. The table's amplitudes run 2..7; a 4 (Earthquake's
    /// up wave) moves the eye about a fifth of a yard, which reads as a real jolt without making
    /// the world unreadable.</summary>
    public const float YardsPerAmplitude = 0.05f;

    /// <summary>Full strength within this many yards of the camera's target, none beyond FadeTo.</summary>
    public const float FullWithin = 15f, FadeTo = 60f;

    public static float Wave(CameraShakeInfo wave, float t)
    {
        if (t < 0f || wave.Duration <= 0f || t >= wave.Duration) return 0f;
        float s = MathF.Sin(MathF.Tau * (wave.Frequency * t + wave.Phase));
        if (wave.Type == 1) s = MathF.Abs(s);
        float envelope = MathF.Pow(1f - t / wave.Duration, MathF.Max(wave.Coefficient, 0f));
        return wave.Amplitude * YardsPerAmplitude * s * envelope;
    }

    public static float Falloff(float distance) =>
        distance <= FullWithin ? 1f : distance >= FadeTo ? 0f : 1f - (distance - FullWithin) / (FadeTo - FullWithin);

    /// <summary>The summed offset in camera axes (x right, y forward, z up).</summary>
    public static Vector3 Offset(IEnumerable<(IReadOnlyList<CameraShakeInfo> Waves, float Age, float Strength)> shakes)
    {
        Vector3 sum = Vector3.Zero;
        foreach ((IReadOnlyList<CameraShakeInfo> waves, float age, float strength) in shakes)
            foreach (CameraShakeInfo wave in waves)
            {
                float v = Wave(wave, age) * strength;
                switch (wave.Direction)
                {
                    case 0: sum.X += v; break;
                    case 1: sum.Y += v; break;
                    default: sum.Z += v; break;
                }
            }
        return sum;
    }

    public static float Longest(IReadOnlyList<CameraShakeInfo> waves) =>
        waves.Count == 0 ? 0f : waves.Max(w => w.Duration);
}

public sealed partial class GameLoop
{
    private readonly List<(IReadOnlyList<CameraShakeInfo> Waves, double Started, Vector3 Source)> _cameraShakes = new();
    private float _cameraShakePeak;

    /// <summary>A kit's world effect (f12, SPELL_CREATOR_PLAN.md P4.3): planted where the unit stands.</summary>
    private void PlantKitWorldEffect(string path, ulong unit, uint spell, StageLife life, double now, string stage)
    {
        if (SpellEffectUnitPose(unit) is not { Found: true } pose) return;
        if (_spellEffects?.SpawnWorldEffect(path, unit, spell, life, pose.Position, now, stage) == true)
            Console.WriteLine($"[spell-world] {Path.GetFileName(path)} for spell {spell} at ({pose.Position.X:0.#},{pose.Position.Y:0.#},{pose.Position.Z:0.#}) {life}");
    }

    private void StartKitCameraShake(uint shakeId, ulong unit, Vector3? position)
    {
        if (_spellVisualCatalog?.TryGetCameraShake(shakeId, out IReadOnlyList<CameraShakeInfo> waves) != true) return;
        Vector3 source = position ?? (SpellEffectUnitPose(unit) is { Found: true } pose
            ? pose.Position : _window.Camera.EyeTarget);
        // One impact kit on five victims is ONE jolt, not five stacked: the same shake started
        // within a tenth of a second is the same event.
        double now = SpellClockNow;
        if (_cameraShakes.Any(s => s.Waves.SequenceEqual(waves) && now - s.Started < 0.1)) return;
        _cameraShakes.Add((waves, now, source));
        Console.WriteLine($"[camera-shake] {shakeId}: {waves.Count} wave(s) at ({source.X:0.#},{source.Y:0.#},{source.Z:0.#})");
    }

    private void UpdateCameraShake()
    {
        Camera camera = _window.Camera;
        if (_cameraShakes.Count == 0) { camera.ShakeOffset = Vector3.Zero; return; }
        double now = SpellClockNow;
        int before = _cameraShakes.Count;
        _cameraShakes.RemoveAll(s => now - s.Started > CameraShakeLaw.Longest(s.Waves));
        if (_cameraShakes.Count < before && _cameraShakes.Count == 0)
        {
            Console.WriteLine($"[camera-shake] settled: peak {_cameraShakePeak:0.###} yd");
            _cameraShakePeak = 0f;
        }
        Vector3 local = CameraShakeLaw.Offset(_cameraShakes.Select(s => (s.Waves, (float)(now - s.Started),
            CameraShakeLaw.Falloff(Vector3.Distance(s.Source, camera.EyeTarget)))));
        Vector3 forward = camera.EyeTarget - camera.Position;
        forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitX;
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitY;
        Vector3 up = Vector3.Cross(right, forward);
        camera.ShakeOffset = right * local.X + forward * local.Y + up * local.Z;
        _cameraShakePeak = MathF.Max(_cameraShakePeak, camera.ShakeOffset.Length());
    }
}
