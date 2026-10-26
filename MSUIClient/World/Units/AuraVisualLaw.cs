using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

public enum AuraBodyNodeKind { Alpha, Tint, AnimationRate, Scale }

public readonly record struct AuraBodyNode(AuraBodyNodeKind Kind, float Value, Vector3 Tint);
public readonly record struct AuraBodySpell(uint SpellId, IReadOnlyList<AuraBodyNode> Nodes);

/// <summary>Build-5875 state-kit CharProc dispatch for effects on the unit body itself.</summary>
public static class AuraVisualLaw
{
    public const int TintProc = 1;
    /// <summary>p0 = a factor on the model's scale (wowdev DB/SpellVisualKit, procedure 2 "Scale";
    /// Bloodlust 1.5, Tiger's Fury 1.1, Berserk 1.0). SPELL_CREATOR_PLAN.md P4.3.</summary>
    public const int ScaleProc = 2;
    public const int AnimationRateProc = 11;
    public const int AlphaProc = 14;
    public const double AlphaFadeSeconds = 1.0;
    public const float AlphaSettledEpsilon = 1f / 128f;

    public static AuraBodyNode[] Nodes(IReadOnlyList<SpellVisualCharProc> procs)
    {
        var result = new List<AuraBodyNode>(procs.Count);
        foreach (SpellVisualCharProc proc in procs)
        {
            float value = proc.Parameters.Length > 0 ? proc.Parameters[0] : 0f;
            if (!float.IsFinite(value)) continue;
            switch (proc.Type)
            {
                case AlphaProc:
                    result.Add(new AuraBodyNode(AuraBodyNodeKind.Alpha, value, Vector3.One));
                    break;
                case TintProc:
                    uint packed = (uint)Math.Clamp(MathF.Round(value), 0f, 0xff_ffff);
                    result.Add(new AuraBodyNode(AuraBodyNodeKind.Tint, value,
                        new Vector3((packed >> 16) & 0xff, (packed >> 8) & 0xff,
                            packed & 0xff) / 255f));
                    break;
                case ScaleProc when value > 0f:
                    result.Add(new AuraBodyNode(AuraBodyNodeKind.Scale, value, Vector3.One));
                    break;
                case AnimationRateProc:
                    result.Add(new AuraBodyNode(AuraBodyNodeKind.AnimationRate, value,
                        Vector3.One));
                    break;
            }
        }
        return result.ToArray();
    }

    public static float Fade(float from, float to, double elapsed)
    {
        float t = (float)Math.Clamp(elapsed / AlphaFadeSeconds, 0.0, 1.0);
        return from + (to - from) * t * t * t;
    }
}

/// <summary>
/// Per-unit head-node lists and the reference one-second cubic body-alpha ramp. Auras are keyed by
/// spell id; new nodes link at the head, removal reveals the next node, and only the head contributes.
/// </summary>
public sealed class AuraVisualState
{
    private readonly List<(uint Spell, float Value)> _alpha = [];
    private readonly List<(uint Spell, Vector3 Value)> _tint = [];
    private readonly List<(uint Spell, float Value)> _rate = [];
    private readonly List<(uint Spell, float Value)> _scale = [];
    private readonly Dictionary<uint, AuraBodyNode[]> _active = [];
    private bool _initialized;
    private float _baseAlpha = 1f;
    private float _currentAlpha = 1f;
    private float _fromAlpha = 1f;
    private float _targetAlpha = 1f;
    private double _rampStarted;

    public float Alpha => Math.Clamp(_currentAlpha, 0f, 1f);
    public Vector3 Tint => _tint.Count > 0 ? _tint[0].Value : Vector3.One;
    public float? AnimationRate => _rate.Count > 0 ? _rate[0].Value : null;
    /// <summary>The head scale node's factor on the model (1 = none).</summary>
    public float Scale => _scale.Count > 0 ? _scale[0].Value : 1f;
    public bool Frozen => AnimationRate == 0f;
    public bool Translucent => Alpha < 1f - AuraVisualLaw.AlphaSettledEpsilon;
    public float BaseAlpha => _baseAlpha;
    public float TargetAlpha => _targetAlpha;

    public void Reconcile(float baseAlpha, IReadOnlyList<AuraBodySpell> spells, double now)
    {
        baseAlpha = Math.Clamp(float.IsFinite(baseAlpha) ? baseAlpha : 1f, 0f, 1f);
        if (!_initialized)
        {
            _initialized = true;
            bool hasNodes = spells.Any(spell => spell.Nodes.Count != 0);
            _baseAlpha = hasNodes ? baseAlpha : 1f;
            _currentAlpha = _fromAlpha = _targetAlpha = _baseAlpha;
            _rampStarted = now;
        }

        Tick(now);
        HashSet<uint> seen = spells.Select(spell => spell.SpellId).ToHashSet();
        foreach (uint stale in _active.Keys.Where(spell => !seen.Contains(spell)).ToArray())
            Reap(stale, now);

        if (MathF.Abs(_baseAlpha - baseAlpha) > float.Epsilon)
        {
            _baseAlpha = baseAlpha;
            Retarget(now);
        }

        foreach (AuraBodySpell spell in spells)
        {
            AuraBodyNode[] nodes = spell.Nodes.ToArray();
            if (_active.TryGetValue(spell.SpellId, out AuraBodyNode[]? prior) &&
                prior.SequenceEqual(nodes)) continue;
            Install(spell.SpellId, nodes, now);
        }
        Tick(now);
    }

    public void Tick(double now) =>
        _currentAlpha = AuraVisualLaw.Fade(_fromAlpha, _targetAlpha, now - _rampStarted);

    private void Install(uint spell, AuraBodyNode[] nodes, double now)
    {
        Remove(spell);
        _active[spell] = nodes;
        foreach (AuraBodyNode node in nodes)
        {
            switch (node.Kind)
            {
                case AuraBodyNodeKind.Alpha: _alpha.Insert(0, (spell, node.Value)); break;
                case AuraBodyNodeKind.Tint: _tint.Insert(0, (spell, node.Tint)); break;
                case AuraBodyNodeKind.AnimationRate: _rate.Insert(0, (spell, node.Value)); break;
                case AuraBodyNodeKind.Scale: _scale.Insert(0, (spell, node.Value)); break;
            }
        }
        Retarget(now);
    }

    private void Reap(uint spell, double now)
    {
        Remove(spell);
        _active.Remove(spell);
        Retarget(now);
    }

    private void Remove(uint spell)
    {
        _alpha.RemoveAll(node => node.Spell == spell);
        _tint.RemoveAll(node => node.Spell == spell);
        _rate.RemoveAll(node => node.Spell == spell);
        _scale.RemoveAll(node => node.Spell == spell);
    }

    private void Retarget(double now)
    {
        float target = Math.Clamp(_baseAlpha * (_alpha.Count > 0 ? _alpha[0].Value : 1f),
            0f, 1f);
        if (MathF.Abs(target - _targetAlpha) <= float.Epsilon) return;
        _fromAlpha = _currentAlpha;
        _targetAlpha = target;
        _rampStarted = now;
    }
}
