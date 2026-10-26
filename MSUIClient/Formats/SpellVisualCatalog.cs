using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// Vanilla SpellVisual -> kit -> effect-model / body-animation / missile chain.
///
/// SCHEMA (byte-verified against build 5875; re-verified against our own MPQs by
/// tools/spellvis/spellvis.py, 2026-08-02 — that tool is the ORACLE for this
/// file: NIGHT_03/reference/spellvis-reference.csv is generated from the same
/// tables and any disagreement between it and this loader is a bug HERE.)
///
///   SpellVisual.dbc          2165 rec x 16 fields x 64 B, all u32
///     f0  id
///     f1  precastKit   f2 castKit   f3 impactKit   f4 stateKit   f5 channelKit
///     f6  hasMissile   -- gates the GO destination burst. Missile spawn uses Spell.dbc Speed > 0.
///     f7  missile SpellVisualEffectName id
///     f9  missile destination-attachment ORDINAL (index into MissileAttachTable)
///     f10 missile in-flight LOOP sound (SoundEntries id)
///
///   SpellVisualKit.dbc       1772 rec x 35 fields x 140 B, all u32
///     f0  id      f2 AnimationData.dbc id      f13 SoundEntries.dbc id
///     f12 SpellVisualEffectName id: a WORLD effect - a model planted at the unit's feet, not on a
///         bone (Thunder Clap's ring, Frost Nova's ice, Entangling Roots, Net, Web); 37 kits
///     f14 SpellEffectCameraShakes.dbc id -> up to three CameraShakes.dbc rows
///     f3..f11  the NINE SpellVisualEffectName slots (see KitAttachmentIds)
///
///   SpellVisualEffectName.dbc  775 rec x 5 fields x 20 B
///     f0 id   f1 label   f2 model path
///
/// THE NONE-SENTINEL (this is a real trap, found empirically)
///   "No value" is written as EITHER 0 OR 0xFFFFFFFF, inconsistently, on the
///   same table. Of 1772 kits, 41 carry anim 0 and 875 carry 0xFFFFFFFF. Fold
///   BOTH or you silently drop most impact kits. Fk() is that fold and every
///   foreign-key read in this file goes through it.
///
/// THE EXTENSION LAW (this is why nothing rendered)
///   The DBC ships .mdx / .mdl paths. ZERO of the 530 distinct paths resolve as
///   written; 515 resolve after swapping to .m2. The swap lives HERE, at the
///   single point paths leave the catalog, so no consumer can forget it. The 15
///   that never resolve are stale pre-release rows (Particles\Frost_Precast_*,
///   Particles\BloodLust_*, DeathTouchCast.mdl): those are DEAD DBC ROWS, not
///   missing art — the reference client takes its fallback and moves on, so they
///   must NOT be treated as a blocking missing-asset.
///
/// STAGE SEMANTICS
///   The stage does NOT select "which visual" — every populated slot on a
///   reached row fires. The stage selects LIFETIME POLICY only. See StageLife.
///
/// NO GL, NO GAME LOGIC - Formats/ rule.
/// </summary>
public readonly record struct SpellVisualStages(
    uint Precast, uint Cast, uint Impact, uint State, uint Channel,
    uint MissileEffect, ushort MissileAttachment, uint MissileSound, uint StrikeSound,
    uint MissileGate, uint AreaGate, uint AreaEffect, uint AreaKit);

public readonly record struct SpellVisualKitEffect(
    ushort AttachmentId,
    string ModelPath,
    float Scale = 1f);

public readonly record struct SpellVisualCharProc(int Type, float[] Parameters);

public readonly record struct SpellChainProcInfo(
    uint EffectId,
    uint BeamCount,
    bool Persistent,
    int Type);

public readonly record struct SpellChainEffectInfo(
    uint Id,
    float AverageSegmentLength,
    float HalfWidth,
    float NoiseScale,
    float ScrollPeriodSeconds,
    uint BoltLifeMs,
    uint BoltStaggerMs,
    string Texture);

public readonly record struct SpellAreaEmitterInfo(
    int Selector,
    string ModelPath,
    float InstancesPerSecond);

public readonly record struct SpellVisualKitInfo(
    ushort? AnimationId,
    uint? Sound,
    IReadOnlyList<SpellVisualKitEffect> Effects,
    IReadOnlyList<SpellVisualCharProc> CharProcs,
    uint? Shake = null,
    string? WorldEffect = null);

/// <summary>One CameraShakes.dbc row (SPELL_CREATOR_PLAN.md P4.3): a wave on one camera axis.
/// Type 0 = sine; 1 = a rectified (bouncing) sine. Direction 0/1/2 = the camera's right /
/// forward / up. Amplitude is in the table's own unit (see CameraShakeLaw), frequency in Hz,
/// duration in seconds, phase in turns, Coefficient the decay exponent of (1 - t/duration).</summary>
public readonly record struct CameraShakeInfo(
    uint Id, uint Type, uint Direction, float Amplitude, float Frequency,
    float Duration, float Phase, float Coefficient);

public readonly record struct SpellAreaVisualInfo(
    string? LoopingModelPath,
    IReadOnlyList<SpellAreaEmitterInfo> Emitters,
    uint? Sound);

/// <summary>
/// The 1.12 client's type-9 SpellVisualKit dispatch. The selector is not a
/// SpellVisualEffectName foreign key: it indexes this literal client table.
/// A complete build-5875 census finds eight type-9 rows, all with integral
/// selectors in this range and zeroes in parameters 2/3. Selector 3 is shipped
/// by the client table but unused by the mounted DBC.
/// </summary>
public static class SpellAreaVisualLaw
{
    private static readonly string[] ShardModels =
    [
        @"Spells\Blizzard_Impact_Base.m2",
        @"Spells\RainOfFire_Impact_Base.m2",
        @"Spells\CallLightning_Impact.m2",
        @"Spells\FlamestrikeSmall_Impact_Base.m2",
        @"Spells\DeathAndDecay_Area_Base.m2",
        @"Spells\ArcaneShot_Area.m2",
        @"Spells\StarShards_Impact_Base.m2",
    ];
    private static readonly IReadOnlyList<string> ShardModelView = Array.AsReadOnly(ShardModels);

    public static IReadOnlyList<string> ClientShardModels => ShardModelView;

    public static bool TryResolveEmitter(in SpellVisualCharProc proc,
        out SpellAreaEmitterInfo emitter)
    {
        emitter = default;
        if (proc.Type != 9 || proc.Parameters.Length < 2) return false;
        float rawSelector = proc.Parameters[0];
        float rate = proc.Parameters[1];
        if (!float.IsFinite(rawSelector) || !float.IsFinite(rate) || rate <= 0f) return false;

        float integral = MathF.Round(rawSelector);
        if (rawSelector != integral || integral < 0f || integral >= ShardModels.Length) return false;
        int selector = (int)integral;
        emitter = new SpellAreaEmitterInfo(selector, ShardModels[selector], rate);
        return true;
    }

    /// <summary>Uniform by area over the server-owned DynamicObject disc.</summary>
    public static Vector2 NextDiscOffset(ref ulong randomState, float radius)
    {
        if (randomState == 0) randomState = 0x9E3779B97F4A7C15UL;
        float u1 = NextUnit(ref randomState);
        float u2 = NextUnit(ref randomState);
        float safeRadius = float.IsFinite(radius) ? Math.Max(0f, radius) : 0f;
        float distance = safeRadius * MathF.Sqrt(u1);
        float angle = u2 * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
    }

    private static float NextUnit(ref ulong state)
    {
        ulong x = state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        state = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) / 16777216f;
    }
}

/// <summary>
/// Which owner's reap may kill an instance of this stage.
///
/// Persistent instances live until a spell-id-keyed reap; self-terminating ones
/// run out on their own clock (one pass of the model's first sequence). State
/// (aura) instances are deliberately a THIRD case: a cast's GO releasing its
/// precast must not sweep the same spell's aura models — re-eating while the
/// food buff holds must not take the bread with it.
/// </summary>
public enum StageLife { SelfTerminating, Persistent, AuraState }

public enum SpellStage { Precast, Cast, Impact, State, Channel }

public sealed class SpellVisualCatalog
{
    public const string HardcodedLootArt = "HARDCODED Loot Art";
    public const string HardcodedUnitLevelUp = "HARDCODED Unit Level Up";

    /// <summary>
    /// Kit fields 3..11 -> M2 AttachmentID, IN KIT-FIELD ORDER. These are
    /// compile-time immediates in the reference client's slot loop, not data:
    /// Head, Chest, Base, LeftHand, RightHand, Breath, Special1..3.
    /// </summary>
    public static readonly ushort[] KitAttachmentIds =
        [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19];

    /// <summary>
    /// SpellVisual field 9 is an ORDINAL into this table, not an attachment id.
    /// Storing the raw ordinal and using it as a tag (the previous bug) aims
    /// every missile at whatever attachment happens to share that number.
    /// The nine kit tags, then 0x0F and 0x10 at ordinals 9 and 10.
    /// </summary>
    public static readonly ushort[] MissileAttachTable =
        [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19, 0x0F, 0x10];

    /// <summary>
    /// Field 9 ordinals outside the literal table have no first-choice tag. Keep that state
    /// distinct from attachment id 0 so missile targeting begins at the 0x0F/0x13 fallback tail.
    /// </summary>
    public const ushort NoMissileAttachment = ushort.MaxValue;

    /// <summary>The reference client's literal fallback when a missile chain resolves to nothing.</summary>
    public const string ErrorCube = @"Spells\ErrorCube.m2";

    private readonly Dictionary<uint, SpellVisualStages> _visuals = [];
    private readonly Dictionary<uint, (ushort? Anim, uint? Sound, uint[] Effects,
        SpellVisualCharProc[] CharProcs, uint? Shake, uint? World)> _kits = [];
    private readonly Dictionary<uint, CameraShakeInfo> _cameraShakes = [];
    private readonly Dictionary<uint, uint[]> _spellShakes = [];

    /// <summary>A SpellEffectCameraShakes id's waves (the kit's f14), or false.</summary>
    public bool TryGetCameraShake(uint spellShakeId, out IReadOnlyList<CameraShakeInfo> waves)
    {
        waves = Array.Empty<CameraShakeInfo>();
        if (Fk(spellShakeId) is null || !_spellShakes.TryGetValue(spellShakeId, out uint[]? ids)) return false;
        var list = new List<CameraShakeInfo>(3);
        foreach (uint id in ids)
            if (Fk(id) is uint wave && _cameraShakes.TryGetValue(wave, out CameraShakeInfo info)) list.Add(info);
        waves = list;
        return list.Count > 0;
    }

    /// <summary>Every SpellEffectCameraShakes id, for pickers.</summary>
    public IEnumerable<uint> SpellCameraShakeIds => _spellShakes.Keys.OrderBy(k => k);
    private readonly Dictionary<uint, string> _effects = [];
    private readonly Dictionary<string, string> _hardcodedEffects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SpellChainEffectInfo> _chainEffects = [];

    /// <summary>Fold BOTH none-sentinels. See the class remarks.</summary>
    private static uint? Fk(uint raw) => raw is 0 or uint.MaxValue ? null : raw;

    /// <summary>
    /// The extension law. DBC ships .mdx/.mdl; the archives hold .m2. Applied at
    /// every exit point of this catalog so a consumer cannot skip it.
    /// </summary>
    public static string ModelPath(string dbcPath)
    {
        if (string.IsNullOrEmpty(dbcPath)) return "";
        string p = dbcPath.Replace('/', '\\');
        return p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(p.AsSpan(0, p.Length - 4), ".m2")
            : p;
    }

    public static StageLife LifeOf(SpellStage stage) => stage switch
    {
        SpellStage.Precast or SpellStage.Channel => StageLife.Persistent,
        SpellStage.State => StageLife.AuraState,
        _ => StageLife.SelfTerminating,   // Cast, Impact
    };

    public static uint KitFor(in SpellVisualStages s, SpellStage stage) => stage switch
    {
        SpellStage.Precast => s.Precast,
        SpellStage.Cast => s.Cast,
        SpellStage.Impact => s.Impact,
        SpellStage.State => s.State,
        _ => s.Channel,
    };

    public bool TryGetStages(uint id, out SpellVisualStages stages)
        => _visuals.TryGetValue(id, out stages);

    // The GO one-shot uses field 6 == 0, independently of DynamicObject field 11.
    public bool TryGetDestinationBurst(uint visualId, out string path)
    {
        path = "";
        if (!_visuals.TryGetValue(visualId, out var stages) || stages.MissileGate != 0 ||
            Fk(stages.AreaEffect) is not uint effect || !_effects.TryGetValue(effect, out var raw))
            return false;
        path = ModelPath(raw);
        return path.Length != 0;
    }

    /// <summary>
    /// Resolve one of the client-owned SpellVisualEffectName rows (loot sparkle,
    /// unit level-up, resurrection, and their siblings) by its build-5875 label.
    /// These do not belong to a SpellVisual kit, so field 1 is their only stable
    /// lookup key. The returned path obeys the same .mdl/.mdx-to-.m2 law as every
    /// ordinary kit effect.
    /// </summary>
    public bool TryGetHardcodedEffect(string label, out string modelPath)
    {
        if (_hardcodedEffects.TryGetValue(label, out string? dbcPath))
        {
            modelPath = ModelPath(dbcPath);
            return modelPath.Length != 0;
        }
        modelPath = "";
        return false;
    }

    public bool TryGetKit(uint id, out SpellVisualKitInfo kit)
    {
        if (Fk(id) is null || !_kits.TryGetValue(id, out var raw)) { kit = default; return false; }
        var effects = new List<SpellVisualKitEffect>(9);
        for (int i = 0; i < raw.Effects.Length; i++)
        {
            if (Fk(raw.Effects[i]) is not uint effect) continue;
            if (!_effects.TryGetValue(effect, out var visual) || visual.Length == 0) continue;
            effects.Add(new SpellVisualKitEffect(KitAttachmentIds[i], ModelPath(visual)));
        }
        string? world = Fk(raw.World ?? 0) is uint worldId && _effects.TryGetValue(worldId, out string? worldPath) && worldPath.Length > 0
            ? ModelPath(worldPath) : null;
        kit = new SpellVisualKitInfo(raw.Anim, raw.Sound, effects, raw.CharProcs, raw.Shake, world);
        return true;
    }

    /// <summary>
    /// Decode the build-5875 chain-beam CharProc. Type 0 is a live channel beam key, not an empty
    /// slot; type 12 is its one-shot cast sibling. Each integer parameter uses the client's
    /// bit-exact small-int decode rather than a numeric cast.
    /// </summary>
    public static bool TryGetChainProc(in SpellVisualKitInfo kit, out SpellChainProcInfo chain)
    {
        foreach (SpellVisualCharProc proc in kit.CharProcs)
        {
            if (proc.Type is not (0 or 12) || proc.Parameters.Length < 3) continue;
            uint effect = CharProcSmallInt(proc.Parameters[0]);
            if (effect == 0) continue; // shipped padding slot; the reference no-ops it
            chain = new SpellChainProcInfo(effect,
                Math.Min(3u, CharProcSmallInt(proc.Parameters[1])),
                CharProcSmallInt(proc.Parameters[2]) != 0, proc.Type);
            return true;
        }
        chain = default;
        return false;
    }

    public bool TryGetChainEffect(uint id, out SpellChainEffectInfo effect)
        => _chainEffects.TryGetValue(id, out effect);

    public int ChainEffectCount => _chainEffects.Count;

    public static uint CharProcSmallInt(float value)
        => (BitConverter.SingleToUInt32Bits(value + 512f) >> 14) & 0xffu;

    /// <summary>
    /// Resolve the DynamicObject-specific visual machine. This is separate from the ordinary
    /// impact-kit path: SpellVisual fields 11/12 name the looping centre model, while field 13's
    /// first type-9 CharProc names and rates the free one-shot shards scattered over the radius.
    /// </summary>
    public bool TryGetAreaVisual(uint visualId, out SpellAreaVisualInfo area)
    {
        area = default;
        if (!_visuals.TryGetValue(visualId, out SpellVisualStages stages)) return false;

        string? looping = stages.AreaGate != 0 && Fk(stages.AreaEffect) is uint effectId &&
                          _effects.TryGetValue(effectId, out string? path)
            ? ModelPath(path) : null;
        var emitters = new List<SpellAreaEmitterInfo>(4);
        uint? sound = null;
        if (Fk(stages.AreaKit) is uint kitId && _kits.TryGetValue(kitId, out var kit))
        {
            sound = kit.Sound;
            foreach (SpellVisualCharProc proc in kit.CharProcs)
                if (SpellAreaVisualLaw.TryResolveEmitter(proc, out SpellAreaEmitterInfo emitter))
                    emitters.Add(emitter);
        }
        area = new SpellAreaVisualInfo(looping, emitters, sound);
        return looping is not null || emitters.Count != 0 || sound is not null;
    }

    /// <summary>
    /// One stage of one visual, resolved: the kit and the lifetime policy that
    /// stage implies. Consumers should call THIS rather than re-deriving the
    /// stage-to-kit mapping and the lifetime rule at each call site.
    /// </summary>
    public bool TryGetStageKit(uint visualId, SpellStage stage,
        out SpellVisualKitInfo kit, out StageLife life)
    {
        life = LifeOf(stage);
        kit = default;
        return TryGetStages(visualId, out SpellVisualStages stages) &&
               TryGetKit(KitFor(stages, stage), out kit);
    }

    /// <summary>
    /// The projectile model, already extension-swapped. Null means the chain
    /// named nothing: the caller must then take the ammo/weapon ItemDisplayInfo
    /// path, and failing that <see cref="ErrorCube"/>. A missile with no model
    /// at all still flies and still impacts on schedule — it is simply invisible.
    /// NOTE the gate: a missile exists whenever Spell.dbc Speed > 0, regardless
    /// of whether this returns a path.
    /// </summary>
    public string? MissilePath(in SpellVisualStages stages)
        => Fk(stages.MissileEffect) is uint id &&
           _effects.TryGetValue(id, out string? visual) && visual.Length > 0
            ? ModelPath(visual) : null;

    public static SpellVisualCatalog? Load(MpqMount mpq)
    {
        DbcFile? visuals = Parse(mpq, @"DBFilesClient\SpellVisual.dbc");
        DbcFile? kits = Parse(mpq, @"DBFilesClient\SpellVisualKit.dbc");
        DbcFile? names = Parse(mpq, @"DBFilesClient\SpellVisualEffectName.dbc");
        DbcFile? chains = Parse(mpq, @"DBFilesClient\SpellChainEffects.dbc");
        if (visuals is null || kits is null || names is null ||
            chains is null || visuals.FieldCount < 16 || kits.FieldCount < 35 ||
            names.FieldCount < 5 || chains.FieldCount < 8) return null;

        var result = new SpellVisualCatalog();

        for (int row = 0; row < names.RecordCount; row++)
        {
            uint id = names.GetUInt(row, 0);
            string label = names.GetString(row, 1);
            string path = names.GetString(row, 2);
            if (id != 0 && path.Length > 0)
                result._effects[id] = path;
            if (label.StartsWith("HARDCODED ", StringComparison.OrdinalIgnoreCase) &&
                path.Length > 0)
                result._hardcodedEffects[label] = path;
        }

        for (int row = 0; row < kits.RecordCount; row++)
        {
            uint id = kits.GetUInt(row, 0);
            var effects = new uint[9];
            for (int i = 0; i < effects.Length; i++) effects[i] = kits.GetUInt(row, 3 + i);
            var charProcs = new List<SpellVisualCharProc>(4);
            for (int i = 0; i < 4; i++)
            {
                int type = kits.GetInt(row, 15 + i);
                // Type 0 is CHAIN_CHANNEL. -1 is the actual empty/sentinel value.
                if (type < 0) continue;
                // Build 5875 has four parameter arrays, transposed by proc lane:
                // p0=f19..22, p1=f23..26, p2=f27..30, p3=f31..34.
                var parameters = new float[4];
                for (int p = 0; p < parameters.Length; p++)
                    parameters[p] = kits.GetFloat(row, 19 + p * 4 + i);
                charProcs.Add(new SpellVisualCharProc(type, parameters));
            }
            result._kits[id] = (
                Fk(kits.GetUInt(row, 2)) is uint a ? (ushort)a : null,
                Fk(kits.GetUInt(row, 13)),
                effects,
                charProcs.ToArray(),
                Fk(kits.GetUInt(row, 14)),
                Fk(kits.GetUInt(row, 12)));
        }

        // Camera shakes: optional tables - a client without them simply never shakes.
        if (Parse(mpq, @"DBFilesClient\CameraShakes.dbc") is { FieldCount: >= 8 } shakes)
            for (int row = 0; row < shakes.RecordCount; row++)
            {
                uint id = shakes.GetUInt(row, 0);
                result._cameraShakes[id] = new CameraShakeInfo(id, shakes.GetUInt(row, 1), shakes.GetUInt(row, 2),
                    shakes.GetFloat(row, 3), shakes.GetFloat(row, 4), shakes.GetFloat(row, 5),
                    shakes.GetFloat(row, 6), shakes.GetFloat(row, 7));
            }
        if (Parse(mpq, @"DBFilesClient\SpellEffectCameraShakes.dbc") is { FieldCount: >= 4 } spellShakes)
            for (int row = 0; row < spellShakes.RecordCount; row++)
                result._spellShakes[spellShakes.GetUInt(row, 0)] =
                    new[] { spellShakes.GetUInt(row, 1), spellShakes.GetUInt(row, 2), spellShakes.GetUInt(row, 3) };

        for (int row = 0; row < chains.RecordCount; row++)
        {
            uint id = chains.GetUInt(row, 0);
            if (id == 0) continue;
            result._chainEffects[id] = new SpellChainEffectInfo(
                id,
                chains.GetFloat(row, 1),
                chains.GetFloat(row, 2),
                chains.GetFloat(row, 3),
                chains.GetFloat(row, 4),
                chains.GetUInt(row, 5),
                chains.GetUInt(row, 6),
                chains.GetString(row, 7));
        }

        for (int row = 0; row < visuals.RecordCount; row++)
        {
            uint id = visuals.GetUInt(row, 0);
            uint ordinal = visuals.GetUInt(row, 9);
            result._visuals[id] = new SpellVisualStages(
                Precast: visuals.GetUInt(row, 1),
                Cast: visuals.GetUInt(row, 2),
                Impact: visuals.GetUInt(row, 3),
                State: visuals.GetUInt(row, 4),
                Channel: visuals.GetUInt(row, 5),
                MissileEffect: visuals.GetUInt(row, 7),
                // The ordinal maps THROUGH the table. Out of range means no first-choice
                // tag; the missile destination resolver then starts at 0x0F/0x13.
                MissileAttachment: ordinal < (uint)MissileAttachTable.Length
                    ? MissileAttachTable[ordinal] : NoMissileAttachment,
                MissileSound: visuals.GetUInt(row, 10) is var snd && Fk(snd) is uint s ? s : 0,
                StrikeSound: visuals.GetUInt(row, 14) is var strike && Fk(strike) is uint st ? st : 0,
                MissileGate: visuals.GetUInt(row, 6),
                AreaGate: visuals.GetUInt(row, 11),
                AreaEffect: visuals.GetUInt(row, 12),
                AreaKit: visuals.GetUInt(row, 13));
        }

        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
        => mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}
