using System.Diagnostics;
using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.World.Spells;

namespace MSUIClient.World.Units;

/// <summary>
/// The single runtime owner for every SpellVisual model instance. Kits,
/// channels, aura state, missiles, and impacts all leave this class through the
/// same mesh/particle/ribbon feeds and therefore share attachment, animation,
/// lifetime, and fallback behavior.
/// </summary>
public sealed class SpellEffectSource
{
    private sealed class Asset
    {
        public string Path = "";
        public M2Model Model = null!;
        public M2ParticleEmitter[] Emitters = [];
        public string[] EmitterTextures = [];
        public M2Animator? Animator;
        public Matrix4x4[] Skin = [];
    }

    private sealed class Instance
    {
        public long Id;
        public Asset? Asset;
        public ulong Unit;
        public uint Spell;
        public StageLife Life;
        public ushort Attachment;
        public double Started;
        public double Ends;
        public string Stage = "";
        public SpellEffectPlayback Playback;
        public double LastEventAge = -1e-9;

        /// <summary>Fixed world-point anchor (dynamic-object area visuals): the instance sits
        /// at <see cref="Position"/> with identity rotation, no unit pose involved.</summary>
        public bool Area;
        public bool Missile;
        public ulong Target;
        public ushort DestinationAttachment;
        public Vector3 Position;
        public Vector3 Direction = Vector3.UnitX;
        public Vector3? FixedDestination;
        public float Speed;
        public double ReleaseAt;
        public bool ReleaseStrictlyAfter;
        public double LaunchedAt;
        public double Remaining;
        public double TravelSeconds;
        public double LastMotionAt;
        public bool Launched;
        public bool Missed;
        public byte MissReason;
        public bool HasReleaseMarker;
        public ushort ReleaseBone;
        public Vector3 ReleasePosition;
        public Action<ulong, uint, bool, byte>? Arrived;
        public Action? LaunchEvent;
        public Action? EndEvent;
        public string? CustomTexture;
        /// <summary>Effect scale (SpellVisualEffectName scale): premultiplied into the transform.</summary>
        public float Scale = 1f;
    }

    private sealed class AreaEmitter
    {
        public Asset Asset = null!;
        public ulong Key;
        public uint Spell;
        public Vector3 Position;
        public float Radius;
        public float Rate;
        public float Accumulator;
        public double LastTick;
        public ulong RandomState;
        public uint? BirthSound;
        public Action<uint, ulong, Vector3>? BirthSoundEvent;
    }

    private sealed class ItemGlowInstance
    {
        public long Id;
        public Asset Asset = null!;
        public Matrix4x4 Transform;
        public double Started;
        public SpellEffectPlayback Playback;
        public bool RenderMesh;
        public bool Seen;
    }

    /// <summary>
    /// A unit's own body emitters (UnitModelEffectPlacement). Unlike an item glow this has
    /// no transform or skin of its own: every frame it reads the unit's live pose, so the
    /// flames sit on the animating bones and the emission gate follows the unit's clip.
    /// </summary>
    private sealed class UnitModelEffectInstance
    {
        public long Id;
        public Asset Asset = null!;
        public double Started;
        public bool Seen;
    }

    private readonly MpqMount _mpq;
    private readonly ItemDisplayTable? _itemDisplays;
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _assetFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private readonly List<AreaEmitter> _areaEmitters = [];
    private readonly Dictionary<string, ItemGlowInstance> _itemGlows =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, UnitModelEffectInstance> _unitModelEffects = [];
    private long _nextId;

    public SpellEffectSource(MpqMount mpq)
    {
        _mpq = mpq;
        _itemDisplays = mpq.ReadFile(ItemDisplayTable.MpqPath) is { } bytes
            ? ItemDisplayTable.Parse(bytes) : null;
    }
    public int ActiveCount => _instances.Count;

    /// <summary>Creator clock (shared_docs/SPELL_CREATOR_IDE.md §2.2): the live instances'
    /// spans, for the scrubber and the replay floor. Read-only.</summary>
    public readonly record struct LiveInstance(long Id, string Stage, string Path, double Started,
        double Ends, bool Missile, bool Launched, double LaunchedAt);

    /// <summary>One posed bone of a live effect instance: the world position of its pivot, its
    /// parent's index, its full world matrix (skin[b] x instance transform - a model-space point
    /// on this bone lands in the world through it) and its parent's (the instance transform for
    /// a root), for the creator's skeleton gizmo, ribbon markers and bone handles.</summary>
    public readonly record struct BoneFrame(string Path, int Bone, int Parent, Vector3 World,
        Matrix4x4 Frame, Matrix4x4 ParentFrame);

    public IEnumerable<BoneFrame> BoneFrames(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || asset.Animator is not { } animator ||
                !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            double age = InstanceAge(instance, now);
            Matrix4x4[] skin = asset.Skin;
            animator.Evaluate(animator.FindSequenceOrBake(instance.Playback.SequenceIndex),
                (float)age, (float)age, skin);
            string key = $"spell:{asset.Path}#{instance.Id}";
            int count = Math.Min(asset.Model.Bones.Count, skin.Length);
            for (int b = 0; b < count; b++)
            {
                M2Bone bone = asset.Model.Bones[b];
                Vector3 posed = Vector3.Transform(bone.Pivot, skin[b]);
                Matrix4x4 parentFrame = bone.ParentBone >= 0 && bone.ParentBone < count
                    ? skin[bone.ParentBone] * transform : transform;
                yield return new BoneFrame(key, b, bone.ParentBone, Vector3.Transform(posed, transform),
                    skin[b] * transform, parentFrame);
            }
        }
    }

    public IEnumerable<LiveInstance> LiveInstances()
    {
        foreach (Instance instance in _instances)
            yield return new LiveInstance(instance.Id, instance.Stage, instance.Asset?.Path ?? "",
                instance.Started, instance.Ends, instance.Missile, instance.Launched,
                instance.LaunchedAt);
    }

    /// <summary>
    /// Replace the frame's held-item glow placements. Stable keys retain effect age and live
    /// particles across ordinary movement and sheath swaps; gear/enchant changes retire them.
    /// </summary>
    public void SyncItemGlows(IEnumerable<ItemGlowPlacement> placements, double now)
    {
        foreach (ItemGlowInstance glow in _itemGlows.Values) glow.Seen = false;
        foreach (ItemGlowPlacement placement in placements)
        {
            Asset? asset = Load(placement.Path);
            if (asset is null) continue;
            if (!_itemGlows.TryGetValue(placement.Key, out ItemGlowInstance? glow) ||
                !string.Equals(glow.Asset.Path, asset.Path, StringComparison.OrdinalIgnoreCase))
            {
                glow = new ItemGlowInstance
                {
                    Id = ++_nextId,
                    Asset = asset,
                    Started = now,
                    Playback = SpellEffectPlaybackLaw.Resolve(asset.Model, missile: false),
                };
                _itemGlows[placement.Key] = glow;
            }
            glow.Transform = placement.Transform;
            glow.RenderMesh = placement.RenderMesh;
            glow.Seen = true;
        }
        foreach (string key in _itemGlows.Where(pair => !pair.Value.Seen)
                     .Select(pair => pair.Key).ToArray())
            _itemGlows.Remove(key);
    }

    /// <summary>
    /// Replace the frame's unit-body effect placements (creature M2s that author their own
    /// particle/ribbon emitters). Keyed by unit GUID so a unit keeps its live particles
    /// across frames; a unit that leaves the drawn set retires its cloud.
    /// </summary>
    public void SyncUnitModelEffects(IEnumerable<UnitModelEffectPlacement> placements, double now)
    {
        foreach (UnitModelEffectInstance effect in _unitModelEffects.Values) effect.Seen = false;
        foreach (UnitModelEffectPlacement placement in placements)
        {
            Asset? asset = Load(placement.ModelPath);
            if (asset is null || asset.Emitters.Length == 0 && asset.Model.RibbonEmitters.Count == 0) continue;
            if (!_unitModelEffects.TryGetValue(placement.Guid, out UnitModelEffectInstance? effect) ||
                !string.Equals(effect.Asset.Path, asset.Path, StringComparison.OrdinalIgnoreCase))
            {
                effect = new UnitModelEffectInstance { Id = ++_nextId, Asset = asset, Started = now };
                _unitModelEffects[placement.Guid] = effect;
            }
            effect.Seen = true;
        }
        foreach (ulong guid in _unitModelEffects.Where(pair => !pair.Value.Seen)
                     .Select(pair => pair.Key).ToArray())
            _unitModelEffects.Remove(guid);
    }

    /// <summary>Effect-model $SND/$DSL/$DSO markers crossed by live playback.</summary>
    public Action<uint, ulong, Vector3>? AnimationSoundEvent { get; set; }

    public uint ItemSpellVisual(uint displayId) => _itemDisplays?.Find(displayId)?.SpellVisualId ?? 0;

    public string? AmmoModelPath(uint? displayId)
    {
        if (displayId is not uint id || _itemDisplays?.Find(id) is not { } row) return null;
        string model = row.ModelName1;
        string folder = "Weapon";
        if (model.Length == 0) { model = row.ModelName2; folder = "Ammo"; }
        if (model.Length == 0) return null;
        string stem = Path.GetFileNameWithoutExtension(model);
        return $@"Item\ObjectComponents\{folder}\{stem}.m2";
    }

    public string? AmmoTexturePath(uint? displayId)
    {
        if (displayId is not uint id || _itemDisplays?.Find(id) is not { } row) return null;
        string texture = row.ModelName1.Length > 0 ? row.ModelTexture1 : row.ModelTexture2;
        string folder = row.ModelName1.Length > 0 ? "Weapon" : "Ammo";
        if (texture.Length == 0) return null;
        return $@"Item\ObjectComponents\{folder}\{Path.GetFileNameWithoutExtension(texture)}.blp";
    }

    public IReadOnlyList<string> ActiveModelPaths(uint spell) => _instances
        .Where(i => i.Spell == spell && i.Asset is not null)
        .Select(i => i.Asset!.Path)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int SpawnKit(ulong unit, uint spell, SpellVisualKitInfo kit, bool persistent,
        double now, string stage, double? lifetime = null)
        => SpawnKit(unit, spell, kit, persistent ? StageLife.Persistent : StageLife.SelfTerminating,
            now, stage, lifetime);

    /// <summary>A kit with a camera shake (f14) just spawned: (shake id, unit, ground position or
    /// null for the unit's own). GameLoop.CameraShake.cs listens.</summary>
    public Action<uint, ulong, Vector3?>? KitShake { get; set; }

    /// <summary>A kit with a WORLD effect (f12) spawned on a unit: (model path, unit, spell, life,
    /// now, stage). The owner knows where the unit stands and plants it (SpawnWorldEffect).</summary>
    public Action<string, ulong, uint, StageLife, double, string>? KitWorldEffect { get; set; }

    /// <summary>A kit with a weapon-trail CharProc (type 8) spawned on a unit: (unit, spell, packed
    /// RGB, duration ms, alpha %, now). GameLoop.WeaponTrail.cs.</summary>
    public Action<ulong, uint, uint, float, float, double>? KitWeaponTrail { get; set; }

    /// <summary>A kit's world effect: the model at a point on the ground, tied to the unit and spell so
    /// the kit's state reaps it (Reap matches unit + spell whatever the anchor).</summary>
    public bool SpawnWorldEffect(string path, ulong unit, uint spell, StageLife life, Vector3 position, double now, string stage)
    {
        if (Load(path) is not { } asset || !SpellAttachment.HasVisibleContent(asset.Model)) return false;
        _instances.Add(new Instance
        {
            Id = ++_nextId, Asset = asset, Unit = unit, Spell = spell, Life = life, Started = now,
            Ends = life == StageLife.SelfTerminating ? now + SpellAttachment.SelfTerminatingSpan(asset.Model) : double.PositiveInfinity,
            Stage = stage + "_WORLD", Area = true, Position = position,
            Playback = SpellEffectPlaybackLaw.Resolve(asset.Model, missile: false),
        });
        return true;
    }

    public int SpawnKit(ulong unit, uint spell, SpellVisualKitInfo kit, StageLife life,
        double now, string stage, double? lifetime = null)
    {
        if (kit.Shake is uint shake) KitShake?.Invoke(shake, unit, null);
        if (kit.WorldEffect is { Length: > 0 } worldEffect) KitWorldEffect?.Invoke(worldEffect, unit, spell, life, now, stage);
        foreach (SpellVisualCharProc proc in kit.CharProcs)
            if (proc.Type == 8 && proc.Parameters.Length >= 4 && float.IsFinite(proc.Parameters[0]))
                KitWeaponTrail?.Invoke(unit, spell, (uint)Math.Clamp(MathF.Round(proc.Parameters[0]), 0f, 0xFFFFFF),
                    proc.Parameters[2], proc.Parameters[3], now);
        if (life == StageLife.Persistent)
            _instances.RemoveAll(i => !i.Area && i.Unit == unit && i.Life == StageLife.Persistent);
        else if (life == StageLife.AuraState)
            _instances.RemoveAll(i => i.Unit == unit && i.Spell == spell &&
                i.Life == StageLife.AuraState);
        int spawned = 0;
        foreach (SpellVisualKitEffect effect in kit.Effects)
        {
            ushort attachment = effect.AttachmentId;
            string path = effect.ModelPath;
            if (Load(path) is not { } asset || !SpellAttachment.HasVisibleContent(asset.Model)) continue;
            double span = lifetime ?? SpellAttachment.SelfTerminatingSpan(asset.Model);
            _instances.Add(new Instance
            {
                Id = ++_nextId,
                Asset = asset,
                Unit = unit,
                Spell = spell,
                Life = life,
                Attachment = attachment,
                Started = now,
                Ends = life == StageLife.SelfTerminating ? now + span : double.PositiveInfinity,
                Stage = stage,
                Scale = effect.Scale,
                Playback = SpellEffectPlaybackLaw.Resolve(asset.Model, missile: false),
            });
            spawned++;
        }
        return spawned;
    }

    /// <summary>Legacy fixed-endpoint entry used by diagnostics.</summary>
    public void SpawnMissile(ulong caster, uint spell, string path, Vector3 from, Vector3 to,
        double now, double duration, float scale = 1f)
    {
        Asset? asset = Load(path);
        _instances.Add(new Instance
        {
            Id = ++_nextId, Asset = asset, Unit = caster, Spell = spell,
            Life = StageLife.SelfTerminating, Started = now, Ends = now + Math.Max(.05, duration),
            Missile = true, Launched = true, Position = from, Direction = to - from,
            FixedDestination = to, Target = 0, ReleaseAt = now,
            LaunchedAt = now,
            Remaining = Math.Max(.05, duration), TravelSeconds = Math.Max(.05, duration),
            LastMotionAt = now,
            Stage = "MISSILE", DestinationAttachment = 0x22, Scale = scale,
            Playback = asset is null ? default : SpellEffectPlaybackLaw.Resolve(asset.Model, missile: true),
        });
    }

    /// <summary>
    /// Queue one projectile. Its visible launch waits for the caster model's authored release
    /// event (or the animation-end/.25s backstop). At that edge, travel time is derived from the
    /// current source/destination and reduced by time already queued since GO. The destination is
    /// then re-resolved from the target's live pose every frame.
    /// </summary>
    public void SpawnMissile(ulong caster, uint spell, string? path, ulong target,
        ushort destinationAttachment, float speed, double now, bool missed, byte missReason,
        ushort? castAnimation, Func<ulong, SpellUnitPose> unitPose,
        Action<ulong, uint, bool, byte> arrived, string? customTexture = null,
        Action? launched = null, Action? ended = null, Vector3? destination = null)
    {
        SpellUnitPose source = unitPose(caster);
        Vector3 from = source.Position;
        SpellMissileLaw.Release release = SpellMissileLaw.ResolveRelease(source.Model, castAnimation);
        Asset? asset = string.IsNullOrWhiteSpace(path) ? null : Load(path);
        _instances.Add(new Instance
        {
            Id = ++_nextId,
            Asset = asset,
            Unit = caster,
            Spell = spell,
            Life = StageLife.SelfTerminating,
            Started = now,
            Ends = double.PositiveInfinity,
            Missile = true,
            Target = target,
            FixedDestination = destination,
            DestinationAttachment = destinationAttachment,
            Position = from,
            Speed = speed,
            ReleaseAt = now + release.DelaySeconds,
            ReleaseStrictlyAfter = release.StrictlyAfterDelay,
            LastMotionAt = now,
            Missed = missed,
            MissReason = missReason,
            HasReleaseMarker = release.UsesMarker,
            ReleaseBone = release.Bone,
            ReleasePosition = release.Position,
            Arrived = arrived,
            LaunchEvent = launched,
            EndEvent = ended,
            CustomTexture = customTexture,
            Stage = "MISSILE",
            Playback = asset is null ? default : SpellEffectPlaybackLaw.Resolve(asset.Model, missile: true),
        });
    }

    /// <summary>A GO destination burst plays once at its wire point, independently of a DynamicObject.</summary>
    public bool SpawnGroundBurst(ulong caster, uint spell, string path, Vector3 position, double now)
    {
        if (Load(path) is not { } asset || !SpellAttachment.HasVisibleContent(asset.Model)) return false;
        _instances.Add(new Instance
        {
            Id = ++_nextId, Asset = asset, Unit = caster, Spell = spell,
            Life = StageLife.SelfTerminating, Started = now,
            Ends = now + SpellAttachment.SelfTerminatingSpan(asset.Model),
            Stage = "GROUND_BURST", Area = true, Position = position,
            Playback = SpellEffectPlaybackLaw.Resolve(asset.Model, missile: false),
        });
        return true;
    }

    /// <summary>A whole kit played once at a world point (the impact of a ground missile that hit
    /// no unit, shared_docs/SPELL_CREATOR_PLAN.md P2.2): every slot's model at the point, at its
    /// kit scale - there is no body to attach to, so the attachment is the point itself.</summary>
    public int SpawnGroundKit(ulong caster, uint spell, SpellVisualKitInfo kit, Vector3 position, double now, string stage)
    {
        if (kit.Shake is uint shake) KitShake?.Invoke(shake, caster, position);
        if (kit.WorldEffect is { Length: > 0 } worldEffect)
            SpawnWorldEffect(worldEffect, caster, spell, StageLife.SelfTerminating, position, now, stage);
        int spawned = 0;
        foreach (SpellVisualKitEffect effect in kit.Effects)
        {
            if (Load(effect.ModelPath) is not { } asset || !SpellAttachment.HasVisibleContent(asset.Model)) continue;
            _instances.Add(new Instance
            {
                Id = ++_nextId, Asset = asset, Unit = caster, Spell = spell,
                Life = StageLife.SelfTerminating, Started = now,
                Ends = now + SpellAttachment.SelfTerminatingSpan(asset.Model),
                Stage = stage, Area = true, Position = position, Scale = effect.Scale,
                Playback = SpellEffectPlaybackLaw.Resolve(asset.Model, missile: false),
            });
            spawned++;
        }
        return spawned;
    }

    /// <summary>
    /// Arm both halves of a DynamicObject's area visual: its looping centre model and its
    /// data-driven type-9 shard emitter. Shards are free, self-terminating instances scattered
    /// uniformly across the wire radius; stopping the anchor does not cut their visible tails.
    /// </summary>
    public int SpawnAreaVisual(ulong key, uint spell, SpellAreaVisualInfo visual,
        Vector3 position, float radius, double now,
        Action<uint, ulong, Vector3>? birthSoundEvent = null)
    {
        float safeRadius = float.IsFinite(radius) ? Math.Max(0f, radius) : 0f;
        _instances.RemoveAll(i => i.Area && i.Unit == key &&
            i.Life != StageLife.SelfTerminating);
        _areaEmitters.RemoveAll(e => e.Key == key);
        int spawned = 0;
        if (visual.LoopingModelPath is { Length: > 0 } loopPath &&
            Load(loopPath) is { } loopAsset && SpellAttachment.HasVisibleContent(loopAsset.Model))
        {
            _instances.Add(new Instance
            {
                Id = ++_nextId, Asset = loopAsset, Unit = key, Spell = spell,
                Life = StageLife.Persistent, Attachment = 0x13, Started = now,
                Ends = double.PositiveInfinity, Stage = "AREA_LOOP", Area = true,
                Position = position,
                Playback = SpellEffectPlaybackLaw.Resolve(loopAsset.Model, missile: false),
            });
            spawned++;
        }
        for (int lane = 0; lane < visual.Emitters.Count; lane++)
        {
            SpellAreaEmitterInfo emitter = visual.Emitters[lane];
            if (Load(emitter.ModelPath) is not { } shardAsset ||
                !SpellAttachment.HasVisibleContent(shardAsset.Model)) continue;
            _areaEmitters.Add(new AreaEmitter
            {
                Asset = shardAsset, Key = key, Spell = spell, Position = position,
                Radius = safeRadius, Rate = emitter.InstancesPerSecond,
                LastTick = now,
                RandomState = 0x9E3779B97F4A7C15UL ^ key ^ ((ulong)(lane + 1) * 0xD1B54A32D192ED03UL),
                BirthSound = visual.Sound,
                BirthSoundEvent = birthSoundEvent,
            });
            spawned++;
        }
        return spawned;
    }

    /// <summary>
    /// Apply live DynamicObject field changes without restarting model clocks or
    /// moving shards that have already been born into the world.
    /// </summary>
    public void UpdateAreaVisual(ulong key, uint spell, Vector3 position, float radius)
    {
        float safeRadius = float.IsFinite(radius) ? Math.Max(0f, radius) : 0f;
        foreach (Instance instance in _instances)
            if (instance.Area && instance.Life != StageLife.SelfTerminating &&
                instance.Unit == key && instance.Spell == spell)
                instance.Position = position;
        foreach (AreaEmitter emitter in _areaEmitters)
            if (emitter.Key == key && emitter.Spell == spell)
            {
                emitter.Position = position;
                emitter.Radius = safeRadius;
            }
    }

    public readonly record struct VisualInstance(long Id, string Stage, string Path, Vector3 Position,
        bool Missile, float Progress, int Emitters, bool MeshValid, int Ribbons);

    public IReadOnlyList<VisualInstance> Snapshot(uint spell, double now,
        Func<ulong, SpellUnitPose> unitPose)
    {
        var rows = new List<VisualInstance>();
        foreach (Instance instance in _instances.Where(i => i.Spell == spell))
        {
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            float progress = instance.Missile && instance.TravelSeconds > 0
                ? (float)Math.Clamp(1 - instance.Remaining / instance.TravelSeconds, 0, 1)
                : 0;
            rows.Add(new(instance.Id, instance.Stage, instance.Asset?.Path ?? "(invisible)",
                new Vector3(transform.M41, transform.M42, transform.M43), instance.Missile,
                progress, instance.Asset?.Emitters.Length ?? 0,
                instance.Asset?.Model.IsValid == true, instance.Asset?.Model.RibbonEmitters.Count ?? 0));
        }
        return rows;
    }

    /// <summary>Stop a despawned DynamicObject's loop and emitter. Already-fired shards finish.</summary>
    public void ReapArea(ulong key)
    {
        _areaEmitters.RemoveAll(e => e.Key == key);
        _instances.RemoveAll(i => i.Area && i.Unit == key && i.Life != StageLife.SelfTerminating);
    }

    public void Reap(ulong unit, uint spell, StageLife? life = null)
        => _instances.RemoveAll(i => i.Unit == unit && i.Spell == spell &&
            i.Life != StageLife.SelfTerminating && (life is null || i.Life == life));

    /// <summary>
    /// Starting any new cast releases the unit's previous precast/channel hold,
    /// even when the new spell has no effect model of its own. Aura-state and
    /// dynamic-object owners are separate and survive this transition.
    /// </summary>
    public void BeginCast(ulong unit)
        => _instances.RemoveAll(i => !i.Area && i.Unit == unit &&
            i.Life == StageLife.Persistent);

    public void Tick(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        TickAreaEmitters(now);
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            Instance instance = _instances[i];
            if (!instance.Missile)
            {
                FireAnimationSounds(instance, now, unitPose);
                if (instance.Life == StageLife.SelfTerminating && now >= instance.Ends)
                    _instances.RemoveAt(i);
                continue;
            }

            if (!instance.Launched)
            {
                if (instance.ReleaseStrictlyAfter
                    ? now <= instance.ReleaseAt
                    : now < instance.ReleaseAt) continue;
                SpellUnitPose launchPose = unitPose(instance.Unit);
                SpellUnitPose targetPose = unitPose(instance.Target);
                if (!launchPose.Found || instance.Target != 0 && !targetPose.Found)
                {
                    _instances.RemoveAt(i);
                    instance.EndEvent?.Invoke();
                    continue;
                }
                instance.Position = instance.HasReleaseMarker
                    ? ResolveModelPoint(launchPose, instance.ReleaseBone, instance.ReleasePosition)
                    : launchPose.Position;
                Vector3 launchDestination = instance.Target == 0
                    ? (instance.FixedDestination is { } fixedLaunch ? ViewOf(fixedLaunch) : instance.Position)
                    : ResolveMissileDestination(targetPose, instance.DestinationAttachment);
                double queuedSeconds = Math.Max(0, now - instance.Started);
                instance.Remaining = SpellMissileLaw.RemainingAtRelease(
                    Vector3.Distance(instance.Position, launchDestination), instance.Speed,
                    queuedSeconds);
                instance.TravelSeconds = Math.Max(0, instance.Remaining);
                if (instance.Remaining <= 0)
                {
                    _instances.RemoveAt(i);
                    instance.Arrived?.Invoke(instance.Target, instance.Spell, instance.Missed,
                        instance.MissReason);
                    continue;
                }
                instance.Launched = true;
                instance.LaunchedAt = now;
                instance.LastMotionAt = now;
                instance.LaunchEvent?.Invoke();
            }

            FireAnimationSounds(instance, now, unitPose);

            SpellUnitPose liveTarget = instance.Target == 0 ? default : unitPose(instance.Target);
            if (instance.Target != 0 && !liveTarget.Found)
            {
                _instances.RemoveAt(i);
                instance.EndEvent?.Invoke();
                continue;
            }
            Vector3 destination = instance.Target == 0
                ? (instance.FixedDestination is { } fixedDestination ? ViewOf(fixedDestination) : instance.Position)
                : ResolveMissileDestination(liveTarget, instance.DestinationAttachment);

            SpellMissileLaw.Motion motion = SpellMissileLaw.Advance(instance.Position,
                destination, instance.Direction, instance.Remaining, now - instance.LastMotionAt);
            if (motion.Arrived)
            {
                // The impact handoff happens before a final visual snap, matching the reference
                // projectile integrator and avoiding a one-frame teleport on moving targets.
                _instances.RemoveAt(i);
                instance.EndEvent?.Invoke();
                instance.Arrived?.Invoke(instance.Target, instance.Spell, instance.Missed,
                    instance.MissReason);
                continue;
            }
            instance.Position = motion.Position;
            instance.Direction = motion.Direction;
            instance.Remaining = motion.RemainingSeconds;
            instance.LastMotionAt = now;
        }
    }

    private void FireAnimationSounds(Instance instance, double now,
        Func<ulong, SpellUnitPose> unitPose)
    {
        if (AnimationSoundEvent is null || instance.Asset is not { } asset ||
            instance.Missile && !instance.Launched)
            return;
        double age = InstanceAge(instance, now);
        if (instance.Life == StageLife.SelfTerminating && !instance.Missile &&
            double.IsFinite(instance.Ends))
            age = Math.Min(age, Math.Max(0, instance.Ends - instance.Started));
        SpellEffectPlayback eventPlayback = instance.Life == StageLife.SelfTerminating &&
            !instance.Missile
            ? instance.Playback with { Looping = false }
            : instance.Playback;
        IReadOnlyList<SpellEffectSoundEvent> crossed =
            SpellEffectPlaybackLaw.CrossedSoundEvents(asset.Model, eventPlayback,
                instance.LastEventAge, age);
        instance.LastEventAge = age;
        if (crossed.Count == 0) return;
        Vector3 position = instance.Area || instance.Missile
            ? instance.Position
            : unitPose(instance.Unit).Position;
        foreach (SpellEffectSoundEvent sound in crossed)
            AnimationSoundEvent(sound.SoundId, instance.Unit, position);
    }

    private void TickAreaEmitters(double now)
    {
        foreach (AreaEmitter emitter in _areaEmitters)
        {
            emitter.Accumulator += emitter.Rate * (float)Math.Max(0.0, now - emitter.LastTick);
            emitter.LastTick = now;
            while (emitter.Accumulator >= 1f)
            {
                emitter.Accumulator -= 1f;
                Vector2 offset = SpellAreaVisualLaw.NextDiscOffset(
                    ref emitter.RandomState, emitter.Radius);
                double span = SpellAttachment.SelfTerminatingSpan(emitter.Asset.Model);
                Vector3 position = emitter.Position + new Vector3(offset, 0f);
                _instances.Add(new Instance
                {
                    Id = ++_nextId, Asset = emitter.Asset, Unit = emitter.Key,
                    Spell = emitter.Spell, Life = StageLife.SelfTerminating,
                    Attachment = 0x13, Started = now, Ends = now + span,
                    Stage = "AREA_SHARD", Area = true,
                    Position = position,
                    Playback = SpellEffectPlaybackLaw.Resolve(emitter.Asset.Model, missile: false),
                });
                if (emitter.BirthSound is uint sound)
                    emitter.BirthSoundEvent?.Invoke(sound, emitter.Key, position);
            }
        }
    }

    public IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
        int EmitterIndex, string TexturePath, double AnimationTime, int SequenceIndex,
        Vector3? LocalOrigin, Matrix4x4? LocalFrame, bool RootCarriesCloud,
        bool HostAttachmentRotatesCloud)> EmitterInstances(double now,
        Func<ulong, SpellUnitPose> unitPose, bool billboardJointPoseB = false,
        Vector3 cameraWorld = default, Vector3 cameraForwardWorld = default)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            double age = InstanceAge(instance, now);
            int sequence = instance.Playback.SequenceIndex;
            Matrix4x4[]? effectSkin = null;
            if (asset.Animator is { } animator)
            {
                effectSkin = asset.Skin;
                M2Animator.Clip? clip = animator.FindSequenceOrBake(sequence);
                animator.Evaluate(clip, (float)age, (float)age, effectSkin);
                SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(billboardJointPoseB,
                    asset.Model, transform, cameraWorld, cameraForwardWorld,
                    animator.BoneCount, effectSkin);
            }
            for (int i = 0; i < asset.Emitters.Length; i++)
            {
                M2ParticleEmitter emitter = asset.Emitters[i];
                Vector3? origin = null;
                Matrix4x4? frame = null;
                if (effectSkin is not null && emitter.Bone < effectSkin.Length &&
                    emitter.Bone < asset.Model.Bones.Count)
                {
                    Vector3 emitterPosition = new(emitter.PosX, emitter.PosY, emitter.PosZ);
                    Vector3 pivot = asset.Model.Bones[emitter.Bone].Pivot;
                    // The reference gives a joint-owned emitter the joint's complete live
                    // Transform, not only its quaternion. Rebuild that TRS from the posed global
                    // so animated/non-unit bone scale survives particle birth and model-space draw.
                    // It also rebases the emitter record by the joint pivot before composing that
                    // TRS; using the raw skin matrix for origin while decomposing vectors would
                    // retain hierarchy shear in only half of the placement law.
                    Matrix4x4 global = Matrix4x4.CreateTranslation(pivot) *
                        effectSkin[emitter.Bone];
                    if (Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion rotation,
                        out Vector3 translation))
                    {
                        frame = Matrix4x4.CreateScale(scale) *
                            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
                        origin = SpellParticleFrameLaw.RebasedEmitterOrigin(emitterPosition, pivot,
                            translation, frame.Value);
                    }
                    else origin = Vector3.Transform(emitterPosition, effectSkin[emitter.Bone]);
                }
                yield return ($"spell:{asset.Path}#{instance.Id}", transform,
                    emitter, i, asset.EmitterTextures[i], age, sequence, origin, frame,
                    // Free missiles bake ordinary births into world history: old sparks and ice
                    // motes remain behind the moving projectile instead of being translated with
                    // its live root. Hosted/area effects retain their root anchor. This split is
                    // empirically required by the 1.12 Fireball/Frostbolt trails and matches the
                    // earlier MSUI lane that produced them correctly.
                    !instance.Missile,
                    !instance.Missile && !instance.Area);
            }
        }

        foreach (ItemGlowInstance glow in _itemGlows.Values)
        {
            Asset asset = glow.Asset;
            double age = Math.Max(0, now - glow.Started);
            int sequence = glow.Playback.SequenceIndex;
            Matrix4x4[]? effectSkin = null;
            if (asset.Animator is { } animator)
            {
                effectSkin = asset.Skin;
                M2Animator.Clip? clip = animator.FindSequenceOrBake(sequence);
                animator.Evaluate(clip, (float)age, (float)age, effectSkin);
                SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(billboardJointPoseB,
                    asset.Model, glow.Transform, cameraWorld, cameraForwardWorld,
                    animator.BoneCount, effectSkin);
            }
            for (int i = 0; i < asset.Emitters.Length; i++)
            {
                M2ParticleEmitter emitter = asset.Emitters[i];
                Vector3? origin = null;
                Matrix4x4? frame = null;
                if (effectSkin is not null && emitter.Bone < effectSkin.Length &&
                    emitter.Bone < asset.Model.Bones.Count)
                {
                    Vector3 emitterPosition = new(emitter.PosX, emitter.PosY, emitter.PosZ);
                    Vector3 pivot = asset.Model.Bones[emitter.Bone].Pivot;
                    Matrix4x4 global = Matrix4x4.CreateTranslation(pivot) * effectSkin[emitter.Bone];
                    if (Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion rotation,
                        out Vector3 translation))
                    {
                        frame = Matrix4x4.CreateScale(scale) *
                            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
                        origin = SpellParticleFrameLaw.RebasedEmitterOrigin(emitterPosition, pivot,
                            translation, frame.Value);
                    }
                    else origin = Vector3.Transform(emitterPosition, effectSkin[emitter.Bone]);
                }
                yield return ($"item-glow:{asset.Path}#{glow.Id}", glow.Transform,
                    emitter, i, asset.EmitterTextures[i], age, sequence, origin, frame,
                    true, true);
            }
        }

        foreach ((ulong guid, UnitModelEffectInstance effect) in _unitModelEffects)
        {
            SpellUnitPose pose = unitPose(guid);
            if (!pose.Found || pose.Skin is null || pose.Model is null) continue;
            Asset asset = effect.Asset;
            double age = Math.Max(0, now - effect.Started);
            // The unit's live clip gates emission (a dying elemental's flames go out on the
            // Death sequence's own enable track); the monotonic age keeps looping and
            // global-sequence tracks continuous across clip changes.
            int sequence = pose.SequenceIndex >= 0 && pose.SequenceIndex < asset.Model.Sequences.Count
                ? pose.SequenceIndex : (asset.Model.Sequences.Count > 0 ? 0 : -1);
            for (int i = 0; i < asset.Emitters.Length; i++)
            {
                M2ParticleEmitter emitter = asset.Emitters[i];
                Vector3? origin = null;
                Matrix4x4? frame = null;
                if (emitter.Bone < pose.Skin.Count && emitter.Bone < pose.Model.Bones.Count)
                {
                    Vector3 emitterPosition = new(emitter.PosX, emitter.PosY, emitter.PosZ);
                    Vector3 pivot = pose.Model.Bones[emitter.Bone].Pivot;
                    Matrix4x4 global = Matrix4x4.CreateTranslation(pivot) * pose.Skin[emitter.Bone];
                    if (Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion rotation,
                        out Vector3 translation))
                    {
                        frame = Matrix4x4.CreateScale(scale) *
                            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
                        origin = SpellParticleFrameLaw.RebasedEmitterOrigin(emitterPosition, pivot,
                            translation, frame.Value);
                    }
                    else origin = Vector3.Transform(emitterPosition, pose.Skin[emitter.Bone]);
                }
                // World-space clouds stay where they were born (a walking elemental trails
                // its fire); model-space emitters (flag 0x10) ride the body by their own flag.
                yield return ($"unit-model:{asset.Path}#{guid}", pose.UnitTransform,
                    emitter, i, asset.EmitterTextures[i], age, sequence, origin, frame,
                    false, false);
            }
        }
    }

    /// <summary>
    /// Monotonic effect age. Individual authored tracks wrap or clamp from the
    /// selected sequence flags; global sequences keep this independent clock.
    /// </summary>
    private static double InstanceAge(Instance instance, double now)
    {
        return Math.Max(0, now - (instance.Missile ? instance.LaunchedAt : instance.Started));
    }

    public IEnumerable<SpellMeshDraw> MeshInstances(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || !asset.Model.IsValid ||
                !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            yield return new SpellMeshDraw(instance.Id, asset.Path, asset.Model, transform,
                (float)InstanceAge(instance, now), instance.Playback.SequenceIndex,
                !instance.Missile && instance.Attachment == 0x13, instance.CustomTexture,
                Vector3.One, 1f);
        }
        foreach (ItemGlowInstance glow in _itemGlows.Values)
        {
            Asset asset = glow.Asset;
            if (!glow.RenderMesh || !asset.Model.IsValid) continue;
            yield return new SpellMeshDraw(glow.Id, asset.Path, asset.Model, glow.Transform,
                (float)Math.Max(0, now - glow.Started), glow.Playback.SequenceIndex,
                false, null, Vector3.One, 1f);
        }
    }

    public IEnumerable<(long Id, string Path, M2Model Model, Matrix4x4 Transform,
        float Age, int SequenceIndex)> RibbonInstances(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || asset.Model.RibbonEmitters.Count == 0 ||
                !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            yield return (instance.Id, asset.Path, asset.Model, transform,
                (float)InstanceAge(instance, now), instance.Playback.SequenceIndex);
        }
        foreach (ItemGlowInstance glow in _itemGlows.Values)
            if (glow.Asset.Model.RibbonEmitters.Count > 0)
                yield return (glow.Id, glow.Asset.Path, glow.Asset.Model, glow.Transform,
                    (float)Math.Max(0, now - glow.Started), glow.Playback.SequenceIndex);
        foreach ((ulong guid, UnitModelEffectInstance effect) in _unitModelEffects)
        {
            if (effect.Asset.Model.RibbonEmitters.Count == 0) continue;
            SpellUnitPose pose = unitPose(guid);
            if (!pose.Found) continue;
            int sequence = pose.SequenceIndex >= 0 && pose.SequenceIndex < effect.Asset.Model.Sequences.Count
                ? pose.SequenceIndex : 0;
            yield return (effect.Id, effect.Asset.Path, effect.Asset.Model, pose.UnitTransform,
                (float)Math.Max(0, now - effect.Started), sequence);
        }
    }

    /// <summary>
    /// A view of FIXED world points - area/ground effects and ground-targeted missile destinations - for a renderer that
    /// shows the world transformed (the Quest Tabletop's live table lifts everything onto its exaggerated ground). Unit-
    /// attached effects already follow the lifted unit poses; only points that come from the world itself need this.
    /// Null (the game) = the identity.
    /// </summary>
    public Func<Vector3, Vector3>? FixedPointView { get; set; }

    private Vector3 ViewOf(Vector3 world) => FixedPointView is { } view ? view(world) : world;

    private bool TryTransform(Instance instance, Func<ulong, SpellUnitPose> unitPose,
        out Matrix4x4 transform)
    {
        if (!TryTransformUnscaled(instance, unitPose, out transform)) return false;
        if (instance.Scale != 1f && instance.Scale > 0f)
            transform = Matrix4x4.CreateScale(instance.Scale) * transform;
        return true;
    }

    private bool TryTransformUnscaled(Instance instance, Func<ulong, SpellUnitPose> unitPose,
        out Matrix4x4 transform)
    {
        if (instance.Area)
        {
            // Model space is Y-up ((x, z, -y) from WoW; M2Reader convention) — the world is
            // Z-up, so a bare translation lays the effect ON ITS SIDE: Blizzard's snow bones,
            // authored 7-30 units up local +Y, land N yards NORTH at ankle height (proven by
            // the particle census). RotationX(+90°) stands the model up: local +Y → world +Z.
            transform = Matrix4x4.CreateRotationX(MathF.PI / 2f) *
                Matrix4x4.CreateTranslation(ViewOf(instance.Position));
            return true;
        }
        if (instance.Missile)
        {
            if (!instance.Launched) { transform = default; return false; }
            transform = SpellMissileLaw.FlightTransform(instance.Position, instance.Direction);
            return true;
        }

        SpellUnitPose pose = unitPose(instance.Unit);
        if (!pose.Found) { transform = default; return false; }
        if (pose.Model is not null && SpellAttachment.Resolve(pose.Model, instance.Attachment) is { } point)
            transform = SpellAttachment.World(pose.Model, point, pose.UnitTransform, pose.BoneMatrix);
        else transform = pose.UnitTransform;
        return true;
    }

    private static Vector3 ResolveUnitPoint(in SpellUnitPose pose, ushort attachment)
    {
        if (!pose.Found) return pose.Position;
        Matrix4x4 transform = pose.UnitTransform;
        if (pose.Model is not null && SpellAttachment.Resolve(pose.Model, attachment) is { } point)
            transform = SpellAttachment.World(pose.Model, point, pose.UnitTransform, pose.BoneMatrix);
        return new Vector3(transform.M41, transform.M42, transform.M43);
    }

    /// <summary>Resolve the authored destination attachment and its normal 0x0f/0x13 fallback.</summary>
    private static Vector3 ResolveMissileDestination(in SpellUnitPose pose, ushort attachment)
        => pose.Found ? ResolveUnitPoint(pose, attachment) : pose.Position;

    private static Vector3 ResolveModelPoint(in SpellUnitPose pose, ushort bone, Vector3 local)
    {
        if (!pose.Found) return pose.Position;
        // The event-marker Position is MODEL SPACE (same convention as M2Attachment.Position), so
        // it rides the bone's RAW skinning matrix â€” subtract the pivot before the posed frame
        // (T(pivot)Â·Skin) exactly like SpellAttachment.World, or the launch lands ~a pivot off the
        // hand (the release marker sat ~1yd in front of the caster's hand before this).
        if (pose.BoneMatrix(bone) is not { } model) return pose.Position;
        Vector3 pivot = pose.Model is { } m && bone < m.Bones.Count ? m.Bones[bone].Pivot : Vector3.Zero;
        Matrix4x4 world = Matrix4x4.CreateTranslation(local - pivot) * model * pose.UnitTransform;
        return new Vector3(world.M41, world.M42, world.M43);
    }

    // Creator-mode override layer: patched M2 bytes keyed by resolved model path.
    // Load() prefers these over the archive, so a reap + respawn picks up an
    // in-memory patch with no MPQ rebuild - the realtime tuning loop.
    private readonly Dictionary<string, byte[]> _modelOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replace (or with null bytes, clear) the bytes behind a spell-effect model
    /// and drop its cached asset so the next spawn parses the override.</summary>
    public void SetModelOverride(string rawPath, byte[]? bytes)
    {
        string path = SpellVisualCatalog.ModelPath(rawPath);
        if (bytes is null) _modelOverrides.Remove(path);
        else _modelOverrides[path] = bytes;
        _assets.Remove(path);
        _assetFailures.Remove(path);
        Console.WriteLine($"[fx-load] override {(bytes is null ? "cleared" : $"set ({bytes.Length}b)")} " +
            $"for {Path.GetFileName(path)}");

        // Long-lived holders captured their Asset reference at spawn: the area
        // shard emitters (each newly ticked shard reads emitter.Asset), the
        // looping AREA_LOOP centre, and persistent precast/channel holds. Left
        // stale, a byte-patch shows up only in instances spawned through Load()
        // afterwards - the creator's Blizzard recolor changed the one respawning
        // impact while the continuing rain kept the old colors. Re-resolve them
        // against the fresh bytes in place.
        bool referenced = _areaEmitters.Any(e =>
                string.Equals(e.Asset.Path, path, StringComparison.OrdinalIgnoreCase)) ||
            _instances.Any(i => i.Asset is { } held &&
                string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase));
        if (referenced && Load(path) is { } fresh)
        {
            foreach (AreaEmitter emitter in _areaEmitters)
                if (string.Equals(emitter.Asset.Path, path, StringComparison.OrdinalIgnoreCase))
                    emitter.Asset = fresh;
            foreach (Instance instance in _instances)
                if (instance.Asset is { } held &&
                    string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase))
                    instance.Asset = fresh;
        }
    }

    /// <summary>The original archive bytes for a spell-effect model (override ignored).</summary>
    public byte[]? ReadOriginalModel(string rawPath) =>
        _mpq.ReadFile(SpellVisualCatalog.ModelPath(rawPath));

    private Asset? Load(string rawPath)
    {
        string path = SpellVisualCatalog.ModelPath(rawPath);
        if (_assets.TryGetValue(path, out Asset? cached)) return cached;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (_assetFailures.TryGetValue(path, out double failedAt) && now - failedAt < 10.0)
            return null;
        long t0 = Stopwatch.GetTimestamp();
        byte[]? bytes = _modelOverrides.TryGetValue(path, out byte[]? overridden)
            ? overridden : _mpq.ReadFile(path);
        long t1 = Stopwatch.GetTimestamp();
        M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
        long t2 = Stopwatch.GetTimestamp();
        if (model is null) { _assetFailures[path] = now; return null; }
        var asset = new Asset { Path = path, Model = model, Emitters = model.ParticleEmitters.ToArray(),
            Animator = M2Animator.Build(model, model.Sequences.Select(s => (int)s.AnimationId),
                includeStaticSequences: true) };
        long t3 = Stopwatch.GetTimestamp();
        double ToMs(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
        if (ToMs(t0, t3) > 2)
            Console.WriteLine($"[fx-load] asset {Path.GetFileName(path)} " +
                $"read={ToMs(t0, t1):0.0}ms parse={ToMs(t1, t2):0.0}ms animator={ToMs(t2, t3):0.0}ms");
        if (asset.Animator is { } animator)
            asset.Skin = new Matrix4x4[animator.BoneCount];
        asset.EmitterTextures = new string[asset.Emitters.Length];
        for (int i = 0; i < asset.Emitters.Length; i++)
        {
            int texture = asset.Emitters[i].Texture;
            asset.EmitterTextures[i] = texture >= 0 && texture < model.Textures.Count
                ? model.Textures[texture].Filename : "";
        }
        if (_modelOverrides.ContainsKey(path))
            Console.WriteLine($"[fx-load] {Path.GetFileName(path)} loaded from OVERRIDE: " +
                $"emitterTex=[{string.Join(", ", asset.EmitterTextures.Select((t, i) => $"e{i}={Path.GetFileName(t)}" +
                    (asset.Emitters[i].GeometryModel.Length > 0 ? $"(geo:{Path.GetFileName(asset.Emitters[i].GeometryModel)})" : "")))}]");
        _assetFailures.Remove(path);
        _assets[path] = asset;
        return asset;
    }
}

