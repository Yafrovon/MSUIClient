using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

// ════════════════════════════════════════════════════════════════════════════
// MOUNTS — the steed under a rider, and where the rider sits on it
// ════════════════════════════════════════════════════════════════════════════
//
// 1.12 HAS NO VEHICLE SYSTEM. Seats, passengers and vehicle-relative movement
// arrive in 3.0. Everything you can "drive" in vanilla is one of three older
// things: a transport gameobject (boats, zeppelins, the tram), a charmed unit
// (the Steam Tonk, a Mind Control Cap victim), or A MOUNT — and a mount is just
// UNIT_FIELD_MOUNTDISPLAYID holding a CreatureDisplayInfo id. The Mirage
// Raceway rocket cars are mounts in every way that matters: display 10318
// (Creature\GoblinRocketCar) and 2490 (Creature\GnomeRocketCar) carry the same
// attachment set as Creature\Horse — 0 (seat), 1/2 (rider's hands) — plus
// Stand/Walk/Run sequences. Blizzard authored them rideable and scripted NPCs
// onto them; nothing in the protocol distinguishes them from a Riding Horse.
//
// SO THE WHOLE SYSTEM IS:
//   * draw the mount's creature model at the unit's ground position/facing,
//     animated from the unit's own ground speed (its wheels/legs must move)
//   * take attachment 0's matrix out of the mount's evaluated skin — that IS
//     the saddle, in mount model space — and hand it back as the rider's
//     instance transform
//   * the rider draws there instead of on the ground, playing animation 91
//     ("Mount" in AnimationData.dbc; every character M2 ships it)
//
// which is the same parent-a-model-to-a-bone trick AttachedItemRenderer uses
// for pauldrons, one level up: here the CHARACTER is the attached model.
//
// NAMING TRAP: AttachedItemRenderer.Mount is a piece of GEAR hanging off an
// attachment point (a helm, a pauldron) and has nothing to do with a steed.
// This file is the only place "mount" means the thing you ride.
//
// WHY IT LIVES IN CreatureRenderer: a mount is a plain creature model, so the
// model cache, the appearance cache, the async load path, the animator and the
// draw loop already exist here and are already budgeted. A separate renderer
// would have duplicated all of it to draw a horse.

/// <summary>
/// Per-steed look corrections, supplied by the dev toolkit (Program.Mount.Toolkit.cs).
///
/// All three vectors are in the MOUNT'S MODEL SPACE, in yards: X forward (the way it
/// faces), Y up, Z right. That is the space `M2Attachment.Position` is authored in after
/// M2Reader's conversion, so a nudge here composes with the artist's saddle position
/// instead of fighting it.
/// </summary>
public readonly record struct MountTuning(
    Vector3 SeatOffset,
    Vector3 RiderRotationDegrees,   // X roll (forward axis), Y yaw (up), Z pitch (right)
    float RiderScale,
    Vector3 MountOffset,
    float MountScale,
    float AnimationRate)
{
    public static readonly MountTuning Neutral =
        new(Vector3.Zero, Vector3.Zero, 1f, Vector3.Zero, 1f, 1f);
}

public sealed partial class CreatureRenderer
{
    /// <summary>AnimationData.dbc 91 "Mount" — the seated rider pose.</summary>
    public const int RiderAnimationId = 91;

    /// <summary>
    /// Where per-display corrections come from. Null (or a null return) is neutral, which is
    /// the shipped behaviour: draw exactly what the artist authored.
    /// </summary>
    public Func<int, MountTuning>? TuningFor { get; set; }

    /// <summary>The saddle. Same id GlueScene uses to seat the char-select body on its stage.</summary>
    private const int MountSeatAttachment = 0;

    private readonly Matrix4x4[] _mountSkin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _mountPacked = new float[M2Animator.MaxBones * 12];
    private readonly Dictionary<ulong, float> _mountAnimTime = [];
    private readonly Dictionary<ulong, (int Sequence, float Time)> _mountFootstepTime = [];
    private readonly Dictionary<ulong, float> _mountFlourishTime = [];
    private readonly Dictionary<ulong, float> _mountFreezeEvaluationTime = [];
    private readonly Dictionary<ulong, (M2Animator.Clip? Clip, float Time)> _mountLastVisual = [];
    private readonly Dictionary<ulong, (M2Animator.Clip? Clip, float Time)> _mountFreezeVisuals = [];
    private readonly Dictionary<ulong, MountDraw> _mountsDrawn = [];
    private readonly Stopwatch _selfMountClock = Stopwatch.StartNew();
    private double _selfMountLastSeconds;

    /// <summary>
    /// What the last completed mount draw left behind for the rider and the HUD.
    /// <paramref name="SeatHeight"/> is the saddle's height above the unit's own ground
    /// position, and <paramref name="Scale"/> the steed's render scale, which the rider
    /// inherits through the seat — the two things a nameplate needs to clear a rider's head.
    /// </summary>
    internal readonly record struct MountDraw(
        Matrix4x4 Seat, float GroundRadius, float SeatHeight, float Scale, float LastSeenAt,
        SpellUnitPose Pose);

    /// <summary>
    /// Steeds drawn over the whole frame. Accumulated rather than reset in <see cref="Render"/>,
    /// because the local player's mount is drawn BEFORE that loop and zeroing there would
    /// have swallowed it.
    /// </summary>
    public int MountsDrawnLastFrame { get; private set; }
    private int _mountsDrawnAccumulator;

    /// <summary>Play AnimationData 94 on a rider's mount child, locally or from the SMSG.</summary>
    public void TriggerMountFlourish(ulong riderGuid)
    {
        if (riderGuid != 0 && !TacticalFreezePoseLaw.IsFrozen(riderGuid))
            _mountFlourishTime[riderGuid] = 0f;
    }

    /// <summary>Publish the frame's mount count. Called once, at the end of the unit pass.</summary>
    private void PublishMountCount()
    {
        MountsDrawnLastFrame = _mountsDrawnAccumulator;
        _mountsDrawnAccumulator = 0;
    }

    /// <summary>
    /// The rider's instance transform, in world space, for a unit whose mount drew this
    /// frame. False means "draw it on the ground as usual" — an unmounted unit, or a mount
    /// whose model is still streaming in.
    /// </summary>
    public bool TryGetMountSeat(ulong guid, out Matrix4x4 seat)
    {
        if (_mountsDrawn.TryGetValue(guid, out MountDraw drawn)) { seat = drawn.Seat; return true; }
        seat = Matrix4x4.Identity;
        return false;
    }

    /// <summary>
    /// Footprint of the steed under a rider — what its ground shadow and its selection ring
    /// should both be sized from, since the horse is what is standing on the grass.
    /// </summary>
    public bool TryGetMountGroundRadius(ulong guid, out float radius)
    {
        if (_mountsDrawn.TryGetValue(guid, out MountDraw drawn)) { radius = drawn.GroundRadius; return true; }
        radius = 0f;
        return false;
    }

    /// <summary>The mount half of the rider's last-drawn targetable geometry.</summary>
    public bool TryGetMountSpellPose(ulong guid, out SpellUnitPose pose)
    {
        if (_mountsDrawn.TryGetValue(guid, out MountDraw drawn))
        {
            pose = drawn.Pose;
            return pose.Found;
        }
        pose = SpellUnitPose.Missing;
        return false;
    }

    /// <summary>
    /// The local player is drawn by CharacterRenderer, on its own predicted position, in its
    /// own pass BEFORE the streamed units. So its steed cannot ride along in the loop below —
    /// it is drawn here, from Program's character pass, and hands back the same seat.
    /// Pass mountDisplayId 0 when on foot: that retires the steed's animation clock.
    /// </summary>
    public bool TryDrawSelfMount(Camera camera, ulong guid, int mountDisplayId,
        Vector3 position, float orientation, float travelSpeed, float walkSpeed, bool flying,
        bool grounded, float fallTimeMs, float bodyAlpha, Vector3 bodyTint, bool freezeAnimation,
        out Matrix4x4 seat)
    {
        seat = Matrix4x4.Identity;
        if (mountDisplayId <= 0 || !Ok || !Enabled || _shader is null)
        {
            ForgetMount(guid);
            return false;
        }

        double nowS = _selfMountClock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(nowS - _selfMountLastSeconds, 0.0, 0.1);
        _selfMountLastSeconds = nowS;

        BeginUnitShader(camera);
        bool drew = TryDrawMount(camera, guid, mountDisplayId, position, orientation,
            travelSpeed, walkSpeed, flying, grounded, fallTimeMs, dt, true, false,
            bodyAlpha, bodyTint, freezeAnimation, out MountDraw drawn);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        if (drew) seat = drawn.Seat;
        return drew;
    }

    /// <summary>
    /// Per-frame shader + GL state for the unit pass. Shared so the self-mount draw, which
    /// runs outside <see cref="Render"/>, cannot drift from what the loop sets up.
    /// </summary>
    private void BeginUnitShader(Camera camera)
    {
        if (_shader is null) return;
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _shader.Use();
        _shader.Set("uViewProj", camera.RelativeViewProjection);
        _shader.Set("uSunDir", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uTex", 0);
        CarriedLightFrame.Upload(_shader, camera.Position);
        ApplyAttachmentAtmosphere();
    }

    /// <summary>
    /// Draw one steed and work out its saddle. Assumes the unit shader is already bound
    /// (<see cref="BeginUnitShader"/>) and leaves it bound, so the rider can draw straight after.
    /// </summary>
    private bool TryDrawMount(Camera camera, ulong guid, int mountDisplayId, Vector3 position,
        float orientation, float travelSpeed, float walkSpeed, bool flying, bool grounded,
        float fallTimeMs, float dt, bool emitAnimationEvents, bool highlight, float bodyAlpha,
        Vector3 bodyTint, bool freezeAnimation, out MountDraw drawn)
    {
        drawn = default;
        if (_shader is null || _resolver is null || mountDisplayId <= 0) return false;
        if (!_resolver.TryResolve(mountDisplayId, out CreatureModelInfo info)) return false;

        // Streaming: a mount that is not resident yet must not stall the rider. The rider
        // draws on the ground for those few frames, exactly as it did before this system.
        if (!_modelCache.TryGetValue(info.ModelPath, out LoadedModel? model))
        {
            if (LoadMillisecondsThisFrame >= FinalizeBudgetMs) return false;
            if (!TryAcquireModel(info, allowFinalize: true, out model, out _)) return false;
        }
        if (model is null) return false;

        string appearanceKey = AppearanceKey(info);
        if (!_appearanceCache.TryGetValue(appearanceKey, out Appearance? appearance))
        {
            if (LoadMillisecondsThisFrame >= FinalizeBudgetMs) return false;
            if (!TryAcquireAppearance(model, info, allowFinalize: true, out appearance, out _))
                return false;
        }
        if (appearance is null) return false;

        MountTuning tune = TuningFor?.Invoke(mountDisplayId) ?? MountTuning.Neutral;
        bool tacticallyFrozen = TacticalFreezePoseLaw.IsFrozen(guid);
        float evaluationGlobalTime;
        if (freezeAnimation)
        {
            if (!_mountFreezeEvaluationTime.TryGetValue(guid, out evaluationGlobalTime))
                _mountFreezeEvaluationTime[guid] = evaluationGlobalTime = tacticallyFrozen
                    ? EnsureTacticalFreezeStartedAt(guid)
                    : _globalTime;
        }
        else
        {
            _mountFreezeEvaluationTime.Remove(guid);
            evaluationGlobalTime = _globalTime;
        }

        // The steed stands on the ground, so IT takes the render multiplier and the DBC
        // scale (display x model, which is all a mount has — there is no UNIT_FIELD_SCALE_X
        // for it). The rider is then scaled by its own unit scale RELATIVE TO THE SEAT, or
        // ScaleMultiplier would be applied twice down the chain.
        //
        // The tuned offset goes in BEFORE the scale and the heading: that puts it in model
        // units, alongside the bone translations it exists to cancel, and lets "forward"
        // stay the way the steed faces however it is turned. At scale 1 a unit is a yard.
        float scale = UnitRenderScale(info.Scale, ScaleMultiplier) * MathF.Max(0.05f, tune.MountScale);
        float heading = orientation + HeadingOffsetDegrees * MathF.PI / 180f;
        Matrix4x4 mountWorld = Matrix4x4.CreateTranslation(tune.MountOffset)
            * Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationY(heading)
            * Basis
            * Matrix4x4.CreateTranslation(position);
        Matrix4x4 m = mountWorld;
        m.M41 -= camera.Position.X; m.M42 -= camera.Position.Y; m.M43 -= camera.Position.Z;
        _shader.Set("uModel", m);
        _shader.Set("uHighlight", highlight ? 64f / 255f : 0f);
        bodyAlpha = Math.Clamp(bodyAlpha, 0f, 1f);
        bool bodyTranslucent = bodyAlpha < 1f - AuraVisualLaw.AlphaSettledEpsilon;
        _shader.Set("uBodyAlpha", bodyAlpha);
        _shader.Set("uBodyTint", bodyTint);

        int boneCount = 0;
        M2Animator.Clip? pickClip = null;
        if (Animate && model.Animator is not null && model.BoneCount > 0 &&
            Vector3.Distance(position, camera.Position) <= AnimateDistance)
        {
            if (!_mountAnimTime.TryGetValue(guid, out float at)) at = InitialPhase(guid);
            M2Animator.Clip? clip;
            float rate;
            if (_mountFlourishTime.TryGetValue(guid, out float flourishAt) &&
                model.Animator.Resolve($"mount:{mountDisplayId}", BaseAnimationTrack, 94, true) is { } flourish)
            {
                if (!freezeAnimation) flourishAt += dt;
                if (flourishAt < flourish.DurationSeconds)
                {
                    clip = flourish;
                    rate = 1f;
                    at = flourishAt;
                    _mountFlourishTime[guid] = flourishAt;
                }
                else
                {
                    _mountFlourishTime.Remove(guid);
                    clip = SelectMountClip(model.Animator, mountDisplayId,
                        travelSpeed, walkSpeed, flying, grounded, fallTimeMs, out rate);
                    if (!freezeAnimation)
                        at += dt * rate * MathF.Max(0.05f, tune.AnimationRate);
                }
            }
            else
            {
                _mountFlourishTime.Remove(guid);
                clip = SelectMountClip(model.Animator, mountDisplayId,
                    travelSpeed, walkSpeed, flying, grounded, fallTimeMs, out rate);
                if (!freezeAnimation)
                    at += dt * rate * MathF.Max(0.05f, tune.AnimationRate);
            }

            // Hold the steed's exact clip identity as well as its clocks. Re-selecting from a
            // newly received speed/flying state while frozen would visibly switch Run to Stand
            // even though evaluation time and clip-local time were zero-rate.
            if (freezeAnimation)
            {
                if (!_mountFreezeVisuals.TryGetValue(guid, out var held))
                {
                    held = _mountLastVisual.GetValueOrDefault(guid, (clip, at));
                    _mountFreezeVisuals[guid] = held;
                }
                clip = held.Clip;
                at = held.Time;
            }
            else
            {
                _mountFreezeVisuals.Remove(guid);
                _mountLastVisual[guid] = (clip, at);
            }
            if (float.IsNaN(at) || float.IsInfinity(at)) at = 0f;
            _mountAnimTime[guid] = at;

            if (clip is not null)
            {
                pickClip = clip;
                if (emitAnimationEvents && !freezeAnimation)
                    EmitFootstepEvents(guid, mountDisplayId, position,
                        scale, model.Source, clip, at, mount: true);
                boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
                model.Animator.Evaluate(clip, at, evaluationGlobalTime, _mountSkin);
                M2Animator.Pack(_mountSkin, boneCount, _mountPacked);
                _shader.SetVec4Array("uBones", _mountPacked, boneCount * 3);
                AnimatedLastFrame++;
            }
        }
        _shader.Set("uBoneCount", boneCount);

        bool filter = GeosetFilter && appearance.VisibleGeosets is not null;
        _gl.BindVertexArray(model.Vao);
        _gl.Enable(EnableCap.CullFace);
        bool cullingOn = true;
        for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
        {
            DrawBatch b = model.Batches[batchIndex];
            if (filter && !appearance.VisibleGeosets!.Contains(b.GeosetId)) continue;

            ApplyBatchCulling(b, ref cullingOn);
            bool additive = b.Blend is 3 or 4;
            bool alphaKey = b.Blend == 1;
            if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
            else if (bodyTranslucent || b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
            else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
            _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0f);
            appearance.Textures[batchIndex]?.Bind(0);
            DrawElements(b.Start, b.Count);
        }
        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _mountsDrawnAccumulator++;

        Matrix4x4[] poseSkin = new Matrix4x4[model.Source.Bones.Count];
        IReadOnlyList<Matrix4x4> sourceSkin = boneCount > 0 ? _mountSkin : _bindSkin;
        for (int poseIndex = 0; poseIndex < poseSkin.Length; poseIndex++)
            poseSkin[poseIndex] = poseIndex < sourceSkin.Count
                ? sourceSkin[poseIndex] : Matrix4x4.Identity;
        var pose = new SpellUnitPose(true, position, orientation, mountWorld,
            model.Source, poseSkin, GeosetFilter ? appearance.VisibleGeosets : null,
            pickClip?.BoundsCenter ?? Vector3.Zero,
            pickClip?.BoundsRadius ?? 0f);

        Matrix4x4 seat = SeatTransform(model, mountWorld, boneCount > 0, tune);
        drawn = new MountDraw(
            Seat: seat,
            GroundRadius: GroundShadowRadius(model.HorizontalRadius, scale),
            SeatHeight: MathF.Max(0f, seat.M43 - position.Z),
            Scale: scale,
            LastSeenAt: _globalTime,
            Pose: pose);
        _mountsDrawn[guid] = drawn;
        return true;
    }

    /// <summary>
    /// Attachment 0 in mount model space, lifted through the bone it hangs off and then
    /// through the mount's own instance transform — the AttachedItemRenderer chain, with
    /// the rider standing in for the pauldron.
    ///
    /// A mount with no attachment 0 (the driverless Steam Tonk is the vanilla example)
    /// seats the rider at the model origin, which is the honest answer: nobody authored a
    /// place for them to sit.
    /// </summary>
    private Matrix4x4 SeatTransform(LoadedModel model, Matrix4x4 mountWorld, bool animated,
        in MountTuning tune)
    {
        // The rider's own scale and lean live in the seat rather than at the two call sites,
        // so the streamed path and the local player cannot drift apart. Both still apply
        // their unit scale on the outside, which multiplies cleanly with this one.
        Matrix4x4 rider = Matrix4x4.CreateScale(MathF.Max(0.05f, tune.RiderScale)) * RiderLean(tune);

        M2Attachment? seat = null;
        foreach (M2Attachment a in model.Source.Attachments)
            if (a.Id == MountSeatAttachment) { seat = a; break; }
        if (seat is null)
            return rider * Matrix4x4.CreateTranslation(tune.SeatOffset) * mountWorld;

        Matrix4x4[] skin = animated ? _mountSkin : _bindSkin;
        int bone = (int)seat.BoneIndex;
        Matrix4x4 boneMatrix = bone >= 0 && bone < skin.Length ? skin[bone] : Matrix4x4.Identity;
        return rider
             * Matrix4x4.CreateTranslation(seat.Position + tune.SeatOffset)
             * boneMatrix
             * mountWorld;
    }

    /// <summary>Rider lean, about its own origin: roll on forward, yaw on up, pitch on right.</summary>
    private static Matrix4x4 RiderLean(in MountTuning tune)
    {
        Vector3 r = tune.RiderRotationDegrees;
        if (r == Vector3.Zero) return Matrix4x4.Identity;
        const float rad = MathF.PI / 180f;
        return Matrix4x4.CreateRotationX(r.X * rad)
             * Matrix4x4.CreateRotationZ(r.Z * rad)
             * Matrix4x4.CreateRotationY(r.Y * rad);
    }

    /// <summary>
    /// A steed's gait comes from how fast the RIDER is travelling — the mount has no spline
    /// of its own. Flying mounts use their travelling wing cycle even when the controller's
    /// planar-speed accumulator is zero (taxi movement is server-spline driven). Ground
    /// mounts use Stand / Walk / Run with the authored stride rate.
    /// </summary>
    private static M2Animator.Clip? SelectMountClip(M2Animator animator, int mountDisplayId,
        float travelSpeed, float walkSpeed, bool flying, bool grounded, float fallTimeMs,
        out float rate)
    {
        rate = 1f;
        string unit = $"mount:{mountDisplayId}";
        if (flying)
        {
            M2Animator.Clip? flight = travelSpeed > MovingEpsilon
                ? animator.Resolve(unit, BaseAnimationTrack, 135, true, 193, 40, 5, 4, 0)
                : animator.Resolve(unit, BaseAnimationTrack, 193, true, 135, 40, 0);
            if (travelSpeed > MovingEpsilon && flight is not null && flight.MoveSpeed > 0.01f)
                rate = Math.Clamp(travelSpeed / flight.MoveSpeed, 0.25f, 3f);
            return flight;
        }

        // Airborne (jumping/falling): a short launch pose (37, JumpStart) for its own authored
        // duration, then the sustained hang pose (38, Jump) for the rest of the arc - the
        // on-foot character does the same two-phase thing, just with its own richer clip-time
        // bookkeeping. Looping stays on throughout (unlike the on-foot version) so this can
        // read fallTimeMs directly without separately tracking a held end-of-clip time; Jump
        // clips are pose-only (zero MoveSpeed), so looping never causes a visible foot-slide.
        if (!grounded)
        {
            M2Animator.Clip? jumpStart = animator.Resolve(unit, BaseAnimationTrack, 37, true);
            if (jumpStart is not null && fallTimeMs < jumpStart.DurationSeconds * 1000f)
                return jumpStart;
            return animator.Resolve(unit, BaseAnimationTrack, 38, true, 40, 0);
        }

        if (travelSpeed <= MovingEpsilon)
            return animator.Resolve(unit, BaseAnimationTrack, 0, true);

        float walk = walkSpeed > 0f ? walkSpeed : DefaultWalkSpeed;
        M2Animator.Clip? clip = travelSpeed > 2f * walk
            ? animator.Resolve(unit, BaseAnimationTrack, 5, true, 4, 0)
            : animator.Resolve(unit, BaseAnimationTrack, 4, true, 5, 0);

        if (clip is not null && clip.MoveSpeed > 0.01f)
            rate = Math.Clamp(travelSpeed / clip.MoveSpeed, 0.25f, 3f);
        return clip;
    }

    /// <summary>What a display id resolves to, for the toolkit's readout.</summary>
    public bool TryDescribeMount(int displayId, out string modelPath, out float dbcScale)
    {
        modelPath = "";
        dbcScale = 1f;
        if (_resolver is null || !_resolver.TryResolve(displayId, out CreatureModelInfo info))
            return false;
        modelPath = info.ModelPath;
        dbcScale = info.Scale;
        return true;
    }

    /// <summary>
    /// How far this model draws from its own origin, in model units — the constant the
    /// rocket cars carry in their root bone (SYSTEM_MOUNTS.md §7). Both the mesh and the
    /// saddle inherit it, so negating it into <see cref="MountTuning.MountOffset"/> puts the
    /// whole assembly back over the unit that is riding it.
    ///
    /// Evaluated on demand from the model's own idle pose, not read from whatever drew last,
    /// so the answer does not depend on what happened to be on screen. That clobbers the
    /// shared mount skin, which every steed rewrites before it draws anyway.
    /// </summary>
    public bool TryMeasureMountOrigin(int displayId, out Vector3 drift)
    {
        drift = Vector3.Zero;
        if (_resolver is null || !_resolver.TryResolve(displayId, out CreatureModelInfo info))
            return false;
        if (!_modelCache.TryGetValue(info.ModelPath, out LoadedModel? model) || model is null)
            return false;
        if (model.Animator is null || model.BoneCount <= 0) return false;

        M2Animator.Clip? idle = model.Animator.Resolve($"mount:{displayId}", BaseAnimationTrack, 0, true);
        if (idle is null) return false;
        model.Animator.Evaluate(idle, 0f, _globalTime, _mountSkin);

        // Walk from the saddle to the root of its chain. That root's skinning translation is
        // what the whole model hangs off, which is exactly the offset being measured.
        int bone = -1;
        foreach (M2Attachment a in model.Source.Attachments)
            if (a.Id == MountSeatAttachment) { bone = (int)a.BoneIndex; break; }
        if (bone < 0) bone = 0;

        for (int guard = 0; guard < 64; guard++)
        {
            if (bone < 0 || bone >= model.Source.Bones.Count) return false;
            int parent = model.Source.Bones[bone].ParentBone;
            if (parent < 0) break;
            bone = parent;
        }
        if (bone < 0 || bone >= _mountSkin.Length) return false;

        Matrix4x4 root = _mountSkin[bone];
        drift = new Vector3(root.M41, root.M42, root.M43);
        return drift.LengthSquared() > 1e-6f;
    }

    /// <summary>Retire a rider's steed state the moment it dismounts or leaves the world.</summary>
    private void ForgetMount(ulong guid)
    {
        _mountsDrawn.Remove(guid);
        _mountAnimTime.Remove(guid);
        _mountFlourishTime.Remove(guid);
        _mountFootstepTime.Remove(guid);
        _mountFreezeEvaluationTime.Remove(guid);
        _mountLastVisual.Remove(guid);
        _mountFreezeVisuals.Remove(guid);
    }

    /// <summary>Name a multi-crossing tick, at most once a second, so "many units
    /// walking" and "one unit firing repeatedly" stop looking the same in a log.</summary>
    private void ReportFootstepBurst(ulong guid, int count)
    {
        long now = Environment.TickCount64;
        if (now - _lastFootstepBurstReportMs < 1000) return;
        _lastFootstepBurstReportMs = now;
        Console.WriteLine($"[footstep] {count} crossings in one tick for {guid:X16} " +
                          "- collapsed to one sound");
    }

    private long _lastFootstepBurstReportMs;

    private void ForgetAnimationEventClocks(ulong guid)
    {
        _footstepTime.Remove(guid);
        _mountFootstepTime.Remove(guid);
    }

    private void EmitFootstepEvents(ulong guid, int displayId, Vector3 position,
        float renderScale, M2Model model, M2Animator.Clip clip, float at, bool mount)
    {
        var clocks = mount ? _mountFootstepTime : _footstepTime;
        if (!clocks.TryGetValue(guid, out var previous) ||
            previous.Sequence != clip.SequenceIndex || at < previous.Time)
        {
            clocks[guid] = (clip.SequenceIndex, at);
            return;
        }

        int count = FootstepAnimationLaw.CountCrossings(model, clip, previous.Time, at);
        foreach (string identifier in CreatureAnimationSoundLaw.CrossedVocalEvents(
                     model, clip, previous.Time, at))
        {
            CreatureAnimationSoundEvent?.Invoke(guid, displayId, position, identifier);
            if (!mount) CombatAnimationSoundEvent?.Invoke(guid, identifier);
        }
        clocks[guid] = (clip.SequenceIndex, at);
        // ONE FOOTFALL PER UNIT PER TICK. A tick is a frame; nobody takes four
        // steps in sixteen milliseconds, and collapsing a catch-up burst also
        // prevents an artificial wall of identical transients in the shared mix.
        // Crossings can legitimately come in bursts (a long frame, several $FSD
        // markers inside one window) - the EVENT count stays honest and only the
        // SOUND is collapsed, which is the half a listener can tell apart.
        if (count > 1) ReportFootstepBurst(guid, count);
        if (count > 0)
            FootstepAnimationEvent?.Invoke(guid, displayId, position, renderScale);
    }
}
