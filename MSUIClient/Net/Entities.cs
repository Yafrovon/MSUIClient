using System.Numerics;
using MSUIClient.World.Units;

namespace MSUIClient.Net;

// The client-side world model: entities keyed by GUID, built from SMSG_UPDATE_OBJECT.
// Game-thread-owned (populated by the game loop draining NetworkClient.Inbound), so
// no locking — the net worker only does socket I/O + framing.

public sealed class WorldEntity
{
    public ulong Guid;
    public ObjectTypeId Type;
    public uint Entry;                     // creature/GO template entry
    public ObjectFields Fields = new();
    public Vector3 Position;               // raw WoW space
    public float Orientation;
    public float[]? Speeds;                // [walk, run, run_back, swim, swim_back, turn]
    /// <summary>Rider pose in the named transport's local frame; null while not aboard.</summary>
    public TransportPose? Transport;
    /// <summary>Transport GO path-domain progress received in its latest movement block.</summary>
    public uint? TransportProgress;
    /// <summary>Local monotonic receipt time paired with <see cref="TransportProgress"/>.</summary>
    public long TransportProgressReceivedMs;
    /// <summary>
    /// Client-driven transport heading. Type-15 boats and zeppelins rotate along
    /// their TaxiPathNode spline, so their static spawn quaternion cannot own yaw.
    /// Null for every ordinary gameobject and for parked/unarmed transports.
    /// </summary>
    public float? TransportFacingOverride;
    public AuraVisualState AuraVisual { get; } = new();

    // The active SMSG_MONSTER_MOVE spline this creature is walking, if any. Advanced
    // each frame by EntityStore.TickSplines; cleared when it finishes or an
    // authoritative position snapshot supersedes it.
    public CreatureSpline? Spline;
    public ObservedJump? AirborneMotion;
    public float MovementPitch;
    public JumpInfo? MovementJump;

    /// <summary>
    /// Persistent airborne presentation state, independent of whether a movement spline is
    /// currently active. Encounter Lab puppets set this from their simulation actor so a flyer
    /// keeps hovering between path legs; live units continue to use spline/movement flags until
    /// the wire path exposes an equivalent durable flag.
    /// </summary>
    public bool Flying;

    public bool IsPlayer => Type == ObjectTypeId.Player;
    public bool IsCreature => Type == ObjectTypeId.Unit;
    public bool IsUnit => Type is ObjectTypeId.Unit or ObjectTypeId.Player;
    public bool IsGameObject => Type == ObjectTypeId.GameObject;

    public int DisplayId => Fields.DisplayId;

    /// <summary>
    /// UNIT_FIELD_MOUNTDISPLAYID: the CreatureDisplayInfo id of the steed this unit is
    /// riding, 0 when on foot. 1.12 has no vehicle system — a "vehicle" (the Mirage
    /// Raceway rocket cars included) is a mount display like any other, and the rider is
    /// seated on the mount M2's attachment 0. See CreatureRenderer.Mounts.cs.
    /// </summary>
    public int MountDisplayId => (int)Fields.MountDisplayId;

    public uint Level => Fields.Level;
    public float HealthFraction => Fields.HealthFraction;
    public float PowerFraction => Fields.PowerFraction;
    public bool IsDead => Fields.IsDead;
    public bool InCombat => Fields.InCombat;
    /// <summary>OBJECT_FIELD_SCALE_X times an aura's scale CharProc (AuraVisualLaw.ScaleProc).</summary>
    public float Scale => Fields.Scale * AuraVisual.Scale;
    public uint NpcFlags => Fields.NpcFlags;
    public uint GameObjectType => Fields.GameObjectType;

    /// <summary>
    /// Render yaw for a gameobject. Build 5875 carries the facing twice: the
    /// GAMEOBJECT_ROTATION quaternion (fields 10-13; vmangos writes
    /// z=sin(yaw/2), w=cos(yaw/2), x=y=0 for every static spawn) and the
    /// movement block's Orientation. Prefer the quaternion when it is set —
    /// it is the authoritative descriptor — else fall back to Orientation.
    /// </summary>
    public float GameObjectFacing
    {
        get
        {
            if (TransportFacingOverride is float transportFacing) return transportFacing;
            System.Numerics.Quaternion q = Fields.GameObjectRotation;
            return q.Z != 0f || q.W != 0f ? 2f * MathF.Atan2(q.Z, q.W) : Orientation;
        }
    }

    /// <summary>Auto-attack engagement bracketed by SMSG_ATTACKSTART/STOP.</summary>
    public bool Engaged { get; internal set; }
    public ulong? CombatTarget { get; internal set; }

    /// <summary>ClientUptime ms of the last remote MSG_MOVE_* applied to this unit (observer interpolation).</summary>
    internal long LastMoveMs;

    /// <summary>Last MSG_MOVE_* movement flags for a remote unit — direction (fwd/back/strafe/turn/swim)
    /// for gait selection. Speed comes from the interpolation spline; direction can only come from here.</summary>
    internal uint MoveFlags;

    /// <summary>
    /// True (creatures): the active spline drives facing toward the travel direction. False (remote
    /// players): facing comes from the authoritative MSG_MOVE_* orientation instead, so a backpedalling
    /// or strafing player keeps facing their aim rather than snapping to the movement vector.
    /// </summary>
    internal bool FacingFromSpline = true;

    /// <summary>True while a spline is actively moving this unit (drives future walk/run animation choice).</summary>
    public bool IsMoving => RenderMotionSpeed is > 0.01f || Spline is not null || AirborneMotion is not null;
    public bool IsAirborne => Spline?.Falling == true || (MoveFlags & (uint)MovementFlags.Falling) != 0;
    public bool IsHovering => (MoveFlags & (uint)MovementFlags.Hover) != 0;
    public bool IsWalking => (MoveFlags & (uint)MovementFlags.WalkMode) != 0;

    // A renderer-only view shares the immutable-for-this-frame descriptor/aura
    // but carries the predicted local pose without overwriting network state.
    public float? RenderMotionSpeed { get; private set; }

    /// <summary>
    /// A renderer-only copy at another position (the Quest Tabletop's live table lifts every unit onto its exaggerated
    /// ground). Unlike <see cref="WithRenderPose"/> it keeps the spline, flags and speeds, so walk/run, mounts and
    /// facing animate exactly as in the world; descriptor fields and aura visuals are shared, never written.
    /// </summary>
    public WorldEntity WithViewPosition(Vector3 position)
    {
        var view = (WorldEntity)MemberwiseClone();
        view.Position = position;
        return view;
    }

    public WorldEntity WithRenderPose(Vector3 position, float yaw, float speed, uint flags, bool flying)
    {
        var view = (WorldEntity)MemberwiseClone();
        view.Position = position;
        view.Orientation = yaw;
        view.RenderMotionSpeed = speed;
        view.MoveFlags = flags;
        view.Flying = flying;
        view.Spline = null;
        view.AirborneMotion = null;
        return view;
    }
}

public sealed class EntityStore
{
    private readonly Dictionary<ulong, WorldEntity> _entities = new();
    // Monotonic spline-clock pause origin for each tactical-frozen streamed entity. The
    // authoritative membership itself lives in TacticalFreezePoseLaw; this store owns only the
    // clock rebasing required by its CreatureSpline instances.
    private readonly Dictionary<ulong, long> _tacticalFreezeStartedMs = [];

    public IReadOnlyDictionary<ulong, WorldEntity> Entities => _entities;
    public int Count => _entities.Count;
    public int UnitCount => _entities.Values.Count(e => e.IsUnit);
    public int CreatureCount => _entities.Values.Count(e => e.IsCreature);
    public int PlayerCount => _entities.Values.Count(e => e.IsPlayer);
    public int MovingCount => _entities.Values.Count(e => e.IsMoving);

    public bool TryGet(ulong guid, out WorldEntity entity) => _entities.TryGetValue(guid, out entity!);

    /// <summary>Creator/DevTools: place (or replace) a locally synthesized entity. Never
    /// used on the wire path - the server's create/delete blocks go through Apply.</summary>
    public void AddSynthetic(WorldEntity entity) => _entities[entity.Guid] = entity;

    /// <summary>
    /// Values arrived for a guid we do not hold. Harmless on its own - the block is dropped -
    /// but it is the footprint of a visibility race, so name it once per guid. Whoever reads
    /// msui-console.log after the next "my portrait turned into a gnoll" gets the guid that
    /// went missing instead of a theory about it.
    /// </summary>
    private readonly HashSet<ulong> _valuesForUnknownLogged = [];

    private void NoteValuesForUnknownObject(ulong guid)
    {
        const int Cap = 32;
        if (_valuesForUnknownLogged.Count >= Cap || !_valuesForUnknownLogged.Add(guid)) return;
        Console.WriteLine(
            $"[entities] values update for unstreamed object {guid:X16} - dropped " +
            $"(no CREATE held; awaiting one)" +
            (_valuesForUnknownLogged.Count == Cap ? " [further guids silenced]" : ""));
    }

    /// <summary>Creator/DevTools: remove a locally synthesized entity.</summary>
    public bool RemoveSynthetic(ulong guid) => _entities.Remove(guid);

    /// <summary>Remove a synthesized entity only if it is still the value stored for its GUID.
    /// A later authoritative CREATE may already have replaced the snapshot object.</summary>
    public bool RemoveSynthetic(WorldEntity expected) =>
        _entities.TryGetValue(expected.Guid, out WorldEntity? current) &&
        ReferenceEquals(current, expected) && _entities.Remove(expected.Guid);

    public void SetEngaged(ulong guid, bool engaged, ulong? victim = null)
    {
        if (_entities.TryGetValue(guid, out var entity))
        {
            entity.Engaged = engaged;
            entity.CombatTarget = engaged ? victim : null;
        }
    }

    public void ApplySplineMovementMode(SplineMovementModeChange change)
    {
        if (!_entities.TryGetValue(change.Guid, out WorldEntity? entity) || !entity.IsUnit) return;
        entity.MoveFlags = change.Apply ? entity.MoveFlags | (uint)change.Flag :
            entity.MoveFlags & ~(uint)change.Flag;
        if (change.Apply && change.Flag == MovementFlags.Root)
        {
            entity.MoveFlags &= ~(LocomotionMask | (uint)(MovementFlags.Falling |
                MovementFlags.FallingFar | MovementFlags.SplineEnabled));
            StopMovement(change.Guid);
        }
    }

    public void StopMovement(ulong guid)
    {
        if (!TacticalFreezePoseLaw.IsFrozen(guid) &&
            _entities.TryGetValue(guid, out var entity))
        {
            entity.Spline = null;
            entity.AirborneMotion = null;
        }
    }

    /// <summary>
    /// Predict only the facing edge of a server-driven RTS move. Position remains untouched until
    /// SMSG_MONSTER_MOVE supplies the authoritative path, but the selected body can square toward
    /// its destination on the command frame instead of holding its prior cast/combat bearing.
    /// </summary>
    public void PredictServerMoveFacing(ulong guid, Vector3 destination)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid) ||
            !_entities.TryGetValue(guid, out WorldEntity? entity) || entity.IsDead) return;
        float dx = destination.X - entity.Position.X;
        float dy = destination.Y - entity.Position.Y;
        if (dx * dx + dy * dy > 1e-6f)
            entity.Orientation = MathF.Atan2(dy, dx);

        // A player body can alternate between client MSG_MOVE_* and server AI splines across
        // possession/free-view hand-offs. This order puts it back under spline-facing policy.
        entity.FacingFromSpline = true;
    }

    public void Apply(ObjectUpdate u, long nowMs = 0)
    {
        switch (u.Kind)
        {
            case UpdateKind.CreateObject:
            case UpdateKind.CreateObject2:
            {
                var e = new WorldEntity
                {
                    Guid = u.Guid,
                    Type = u.Type,
                    Fields = u.Fields ?? new ObjectFields(),
                };
                e.Entry = e.Fields.Entry ?? GuidInfo.Entry(u.Guid) ?? 0;
                if (u.Movement?.Position is { } p)
                {
                    e.Position = p;
                    e.Orientation = u.Movement.Orientation;
                    e.Speeds = u.Movement.Speeds;
                }
                e.Engaged = e.IsUnit && u.Movement?.MeleeTarget is > 0;
                e.CombatTarget = e.Engaged ? u.Movement!.MeleeTarget : null;
                e.Transport = u.Movement?.Transport;
                e.TransportProgress = u.Movement?.TransportProgress;
                if (e.TransportProgress is not null) e.TransportProgressReceivedMs = nowMs;
                if (!e.IsDead && u.Movement?.Spline is { } createSpline)
                {
                    e.FacingFromSpline = true;
                    e.Spline = CreatureSpline.Resume(createSpline, nowMs, e.Transport?.Guid ?? 0);
                }
                if (u.Movement?.Living is { } living) ApplyLivingState(e, living, nowMs);
                if (e.IsDead) e.Spline = null;
                _entities[u.Guid] = e;   // a fresh create drops any prior spline with the old entity
                break;
            }
            case UpdateKind.Values:
                if (u.Fields is not null)
                {
                    if (_entities.TryGetValue(u.Guid, out var ent))
                    {
                        ent.Fields.Merge(u.Fields);
                        if (ent.IsDead && !TacticalFreezePoseLaw.IsFrozen(u.Guid))
                        {
                            ent.Spline = null;
                            ent.AirborneMotion = null;
                        }
                    }
                    // A VALUES BLOCK IS NOT AN OBJECT ANNOUNCEMENT, AND TREATING IT AS ONE
                    // BUILT A HUSK THAT THE UI THEN WORE.
                    //
                    // This used to fabricate a WorldEntity here. Nothing set Type, so it
                    // defaulted to ObjectTypeId.Object — leaving IsPlayer, IsCreature AND
                    // IsUnit all false on an object holding whatever partial fields that one
                    // block happened to carry: no level, no PLAYER_VISIBLE_ITEM_*.
                    //
                    // DrawPlayerFrame resolves its subject with TryGet(ControlledGuid), so if
                    // the husk landed on the guid you were driving the lookup SUCCEEDED and
                    // the frame drew the husk: !IsPlayer sent the portrait down the non-player
                    // fallback to TemporaryPortrait-Monster — which is a gnoll, read as Hogger
                    // — with no level beside it, and the body rendered with no equipment.
                    // Reported 2026-09-01 from RTS/Command View, where the camera routinely
                    // ranges past the 100-yard visibility bubble and objects churn in and out.
                    //
                    // The server only sends values for objects already in your known set, so a
                    // values block for a guid we do not hold is a visibility race against an
                    // OutOfRange we already applied, not a new object. Dropping it is what the
                    // class contract already said should happen: see AddSynthetic, "never used
                    // on the wire path". The next CREATE brings the real object, typed.
                    else NoteValuesForUnknownObject(u.Guid);
                }
                break;
            case UpdateKind.Movement:
                if (_entities.TryGetValue(u.Guid, out var em) && u.Movement is { } movement)
                {
                    if (TacticalFreezePoseLaw.IsFrozen(u.Guid)) break;
                    em.Transport = movement.Transport;
                    em.TransportProgress = movement.TransportProgress;
                    if (movement.TransportProgress is not null)
                        em.TransportProgressReceivedMs = nowMs;
                    if (movement.Position is { } mp)
                    {
                        // An authoritative position snapshot supersedes an in-progress spline.
                        em.Spline = null;
                        em.AirborneMotion = null;
                        em.Position = mp;
                        em.Orientation = movement.Orientation;
                        if (movement.Speeds is not null) em.Speeds = movement.Speeds;
                        if (movement.Living is { } living) ApplyLivingState(em, living, nowMs);
                    }
                }
                break;
            case UpdateKind.OutOfRange:
                if (u.Guids is not null)
                    foreach (var g in u.Guids)
                    {
                        _entities.Remove(g);
                        _tacticalFreezeStartedMs.Remove(g);
                    }
                break;
            case UpdateKind.Near:
                break; // objects entering range; the CREATE that follows carries their data
        }
    }

    private static void ApplyLivingState(WorldEntity entity, MovementInfo movement, long nowMs)
    {
        entity.MoveFlags = movement.Flags;
        entity.MovementPitch = movement.Pitch;
        entity.MovementJump = movement.Jump;
        if (!entity.IsDead && entity.Spline is null && movement.Transport is null &&
            (movement.Flags & (uint)MovementFlags.Falling) != 0 && movement.Jump is not null)
        {
            entity.AirborneMotion = new ObservedJump(movement, nowMs);
            entity.FacingFromSpline = false;
        }
    }

    /// <summary>Attach a freshly parsed SMSG_MONSTER_MOVE to its unit (replacing any in-progress spline).</summary>
    public void ApplyMonsterMove(MonsterMove mm, long nowMs)
    {
        if (!_entities.TryGetValue(mm.Guid, out var e)) return;   // unknown guid — not streamed to us
        if (TacticalFreezePoseLaw.IsFrozen(mm.Guid)) return;      // exact pose owns the old spline

        e.AirborneMotion = null;
        if (mm.TransportGuid == 0) e.Transport = null;
        if (mm.Stop || mm.DurationMs == 0 || mm.Points.Length < 2)
        {
            e.Spline = null;
            float? dictatedFacing = ResolveSplineFacing(mm.Facing, mm.Start, mm.TransportGuid);
            ApplySplinePose(e, mm.Start, dictatedFacing, mm.TransportGuid);
            return;
        }
        // SMSG_MONSTER_MOVE is a server-owned path even when its subject is a Player bot.
        // It supersedes the client-facing policy that ApplyRemotePlayerMove installs while a
        // human drives that same body, so turns follow the new path after a free-view order.
        e.FacingFromSpline = true;
        e.Spline = new CreatureSpline(mm.Points, mm.DurationMs, mm.Flying, nowMs,
            mm.SplineId, mm.Cyclic, mm.Facing, mm.Falling, mm.TransportGuid);
    }

    /// <summary>Position-changing movement flags (turning/pitch alone do not relocate a unit).</summary>
    private const uint LocomotionMask =
        (uint)(MovementFlags.Forward | MovementFlags.Backward |
               MovementFlags.StrafeLeft | MovementFlags.StrafeRight);

    /// <summary>
    /// Apply a broadcast MSG_MOVE_* for ANOTHER player, with observer interpolation. While the player
    /// is locomoting we glide from their current interpolated spot to the newly reported one over the
    /// inter-packet interval (a short 2-point <see cref="CreatureSpline"/>), so heartbeats ~500 ms apart
    /// read as smooth motion and the derived AverageSpeed drives walk vs run. A stop / turn-in-place /
    /// fall-land snaps to the authoritative pose. Facing always comes from the packet, not the spline.
    /// </summary>
    public void ApplyRemotePlayerMove(ulong guid, MovementInfo mi, long nowMs)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid) ||
            !_entities.TryGetValue(guid, out var e) || !e.IsUnit) return;
        e.FacingFromSpline = false;   // players face their reported aim, never the travel vector
        e.MoveFlags = mi.Flags;       // direction bits for gait selection (speed comes from the spline)
        e.Transport = mi.Transport;   // full rider-local frame follows every ON_TRANSPORT relay

        e.MovementPitch = mi.Pitch;
        e.MovementJump = mi.Jump;
        e.AirborneMotion = null;
        if (!e.IsDead && (mi.Flags & (uint)MovementFlags.Falling) != 0 && mi.Jump is not null &&
            mi.Transport is null)
        {
            e.Position = mi.Position;
            e.Orientation = mi.Orientation;
            e.Spline = null;
            e.AirborneMotion = new ObservedJump(mi, nowMs);
            e.LastMoveMs = nowMs;
            return;
        }
        if (e.IsDead || (mi.Flags & LocomotionMask) == 0)
        {
            // Dead, or a stop / turn-only / fall-land / reface: snap to the authoritative pose.
            e.Spline = null;
            e.Position = mi.Position;
            e.Orientation = mi.Orientation;
            e.LastMoveMs = nowMs;
            return;
        }

        long last = e.LastMoveMs;
        long dt = nowMs - last;
        e.Orientation = mi.Orientation;
        e.LastMoveMs = nowMs;

        // Facing/heartbeat with negligible positional delta while already gliding: keep the current
        // spline so we don't stutter to idle mid-run.
        if (e.Spline is not null && Vector3.DistanceSquared(e.Position, mi.Position) < 0.04f)
            return;

        if (last == 0 || dt <= 0 || dt > 1000)
        {
            // First move after sighting/idle, or a gap too large to interpolate cleanly: snap.
            e.Spline = null;
            e.Position = mi.Position;
            return;
        }

        // Glide from wherever we currently have them to the freshly reported spot over the packet gap.
        e.Spline = new CreatureSpline(new[] { e.Position, mi.Position }, (uint)dt, flying: false, nowMs);
    }

    /// <summary>Observer teleport is an immediate pose replacement, even with locomotion bits set.</summary>
    public void ApplyRemoteTeleport(ulong guid, MovementInfo movement, long nowMs)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid) ||
            !_entities.TryGetValue(guid, out WorldEntity? entity)) return;
        entity.Spline = null;
        entity.AirborneMotion = null;
        entity.Position = movement.Position;
        entity.Orientation = movement.Orientation;
        entity.MoveFlags = movement.Flags;
        entity.Transport = movement.Transport;
        entity.MovementPitch = movement.Pitch;
        entity.MovementJump = movement.Jump;
        entity.LastMoveMs = nowMs;
    }

    /// <summary>A server-authored move for the locally driven mover is an immediate correction.</summary>
    public void ApplyServerAuthoredMove(ulong guid, MovementInfo mi, long nowMs)
    {
        if (TacticalFreezePoseLaw.IsFrozen(guid) ||
            !_entities.TryGetValue(guid, out WorldEntity? entity)) return;
        entity.Spline = null;
        entity.AirborneMotion = null;
        entity.MovementPitch = mi.Pitch;
        entity.MovementJump = mi.Jump;
        entity.Position = mi.Position;
        entity.Orientation = mi.Orientation;
        entity.MoveFlags = ServerAuthoredMovementLaw.MergeFlags(entity.MoveFlags, mi.Flags);
        entity.Transport = mi.Transport;
        entity.LastMoveMs = nowMs;
    }

    public float? ResolveSplineFacing(MonsterMoveFacing facing, Vector3 position, ulong transportGuid) =>
        MonsterMoveFacingLaw.Resolve(facing, position, guid =>
        {
            if (!_entities.TryGetValue(guid, out WorldEntity? target)) return null;
            if (transportGuid == 0) return target.Position;
            return _entities.TryGetValue(transportGuid, out WorldEntity? parent)
                ? TransportRiderLaw.ToLocal(parent.Position, parent.GameObjectFacing, target.Position) : null;
        });

    public bool TryComposeTransportPose(ulong guid, Vector3 localPosition, float localYaw,
        out TransportRiderLaw.WorldPose world)
    {
        if (guid == 0) { world = new(localPosition, localYaw); return true; }
        if (_entities.TryGetValue(guid, out WorldEntity? parent))
        {
            world = TransportRiderLaw.Compose(parent.Position, parent.GameObjectFacing, localPosition, localYaw);
            return true;
        }
        world = default;
        return false;
    }

    private void ApplySplinePose(WorldEntity entity, Vector3 position, float? facing, ulong transportGuid)
    {
        if (transportGuid == 0)
        {
            entity.Position = position;
            if (facing is { } worldYaw) entity.Orientation = worldYaw;
            return;
        }
        float localYaw = facing ?? (entity.Transport is { } old && old.Guid == transportGuid
            ? old.Orientation : _entities.TryGetValue(transportGuid, out WorldEntity? parent)
                ? entity.Orientation - parent.GameObjectFacing : 0);
        entity.Transport = new TransportPose(transportGuid, position, localYaw);
        if (TryComposeTransportPose(transportGuid, position, localYaw, out var world))
        {
            entity.Position = world.Position;
            entity.Orientation = world.Orientation;
        }
    }

    /// <summary>Advance every active spline. Call once per frame with a monotonic ms clock.</summary>
    public void TickSplines(long nowMs)
    {
        foreach (var e in _entities.Values)
        {
            if (TacticalFreezePoseLaw.IsFrozen(e.Guid))
            {
                _tacticalFreezeStartedMs.TryAdd(e.Guid, nowMs);
                continue;
            }
            if (_tacticalFreezeStartedMs.Remove(e.Guid, out long frozenAt))
            {
                e.Spline?.RebaseAfterPause(Math.Max(0, nowMs - frozenAt));
                e.AirborneMotion?.RebaseAfterPause(Math.Max(0, nowMs - frozenAt));
            }
            if (e.IsDead) { e.Spline = null; e.AirborneMotion = null; continue; }
            if (e.AirborneMotion is { } airborne)
            {
                e.Position = airborne.Sample(nowMs);
                continue;
            }
            if (e.Spline is null) continue;
            bool running = e.Spline.Sample(nowMs, out Vector3 pos, out float? facing);
            float? poseFacing = e.FacingFromSpline ? facing : null;
            if (!running)
                poseFacing = ResolveSplineFacing(e.Spline.FinalFacing, pos, e.Spline.TransportGuid) ?? poseFacing;
            ApplySplinePose(e, pos, poseFacing, e.Spline.TransportGuid);
            if (!running) e.Spline = null;
        }
    }

    /// <summary>
    /// Idle creatures locally square up on UNIT_FIELD_TARGET. vmangos SetInFront
    /// is server-side only, so no MONSTER_MOVE is expected for this turn.
    /// </summary>
    public void FaceIdleTargets(float dt, ulong playerGuid, Vector3 playerPosition)
    {
        float maxStep = 8f * MathF.Max(0f, dt);
        foreach (WorldEntity entity in _entities.Values)
        {
            if (TacticalFreezePoseLaw.IsFrozen(entity.Guid) || !entity.IsCreature ||
                entity.IsDead || entity.IsMoving) continue;
            ulong? targetGuid = entity.Fields.Target ?? entity.CombatTarget;
            if (targetGuid is null) continue;

            Vector3 targetPosition;
            if (targetGuid.Value == playerGuid) targetPosition = playerPosition;
            else if (_entities.TryGetValue(targetGuid.Value, out WorldEntity? target)) targetPosition = target.Position;
            else continue;

            float dx = targetPosition.X - entity.Position.X;
            float dy = targetPosition.Y - entity.Position.Y;
            if (dx * dx + dy * dy < 1e-4f) continue;
            entity.Orientation = TurnToward(entity.Orientation, MathF.Atan2(dy, dx), maxStep);
        }
    }

    public static float TurnToward(float current, float goal, float maxStep)
    {
        const float tau = MathF.PI * 2f;
        float delta = ((goal - current) % tau + tau) % tau;
        float direction = 1f;
        if (delta > MathF.PI) { delta = tau - delta; direction = -1f; }
        if (delta < 1e-4f) return ((current % tau) + tau) % tau;
        float result = current + direction * MathF.Min(delta, MathF.Max(0f, maxStep));
        return ((result % tau) + tau) % tau;
    }

    public void Remove(ulong guid)
    {
        _entities.Remove(guid);
        _tacticalFreezeStartedMs.Remove(guid);
    }

    public void Clear()
    {
        _entities.Clear();
        _tacticalFreezeStartedMs.Clear();
    }

    /// <summary>Worldport seam: retain one client-simulated transport while
    /// dropping every map-local entity. The destination create refreshes it in
    /// place after the ACK.</summary>
    public void ClearExcept(ulong guid)
    {
        if (!_entities.TryGetValue(guid, out WorldEntity? retained))
        {
            _entities.Clear();
            _tacticalFreezeStartedMs.Clear();
            return;
        }
        _entities.Clear();
        _entities[guid] = retained;
        foreach (ulong frozenGuid in _tacticalFreezeStartedMs.Keys.Where(key => key != guid).ToArray())
            _tacticalFreezeStartedMs.Remove(frozenGuid);
    }

    public IEnumerable<WorldEntity> Units => _entities.Values.Where(e => e.IsUnit);

    /// <summary>The nearest units to a point (for a HUD list / targeting).</summary>
    public List<WorldEntity> NearestUnits(Vector3 from, int max) =>
        _entities.Values.Where(e => e.IsUnit)
            .OrderBy(e => Vector3.DistanceSquared(e.Position, from))
            .Take(max)
            .ToList();
}
