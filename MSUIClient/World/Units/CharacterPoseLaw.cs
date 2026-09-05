namespace MSUIClient.World.Units;

/// <summary>Pure character-pose boundaries shared by runtime rendering and regression checks.</summary>
public static class CharacterPoseLaw
{
    /// <summary>
    /// Splits a moving strafe between the model heading and upper body. A stationary body/aim
    /// chase is a whole-body heading lag, not a strafe twist; sparse turn clips get their missing
    /// shoulder channels from Stand separately in M2Animator.
    /// </summary>
    public static float TorsoCounterYaw(bool bindPose, bool frozenStandPose, bool splitStyle,
        bool moving, bool forcedDiagnostic, float torsoFollow, float bodyOffsetYaw)
    {
        if (bindPose || frozenStandPose || !splitStyle || !moving && !forcedDiagnostic) return 0f;
        return (Math.Clamp(torsoFollow, 0f, 1f) - 1f) * bodyOffsetYaw;
    }

    /// <summary>
    /// Signed body step for a stationary turn. Steering still enforces the lag ceiling
    /// immediately; after release, catch-up is rate-limited so the shuffle visibly carries the
    /// body back onto the aim instead of closing ninety degrees in roughly four frames.
    /// </summary>
    public static float StandingBodyStep(float deltaYaw, bool steering, float ceilingRadians,
        float dt, float bodyTurnRate, float chaseRate)
    {
        float magnitude = MathF.Abs(deltaYaw);
        float wanted = steering
            ? MathF.Max(magnitude - MathF.Max(0f, ceilingRadians), 0f)
            : magnitude;

        if (!steering)
        {
            float maxStep = MathF.Max(0f, dt) * MathF.Max(0f, bodyTurnRate) *
                MathF.Max(0f, chaseRate);
            wanted = MathF.Min(wanted, maxStep);
        }

        return MathF.CopySign(MathF.Min(wanted, magnitude), deltaYaw);
    }

    /// <summary>
    /// ONESHOT_OVERLAY_WEIGHT as a lerp factor. Benilla arms the masked one-shot node with
    /// <c>set_weight(8.0)</c> (driver.rs:53) and deliberately does NOT mask the base out of the
    /// SpineLow subtree, so the two blend at ~8:1 rather than the overlay replacing the gait
    /// outright - the running torso bleeds through by the remaining ninth, which is what keeps
    /// a cast-while-running from looking pasted on. 8/(8+1).
    /// </summary>
    public const float OneshotOverlayWeight = 8f / 9f;

    /// <summary>
    /// Benilla's <c>route_oneshot</c> committed_lower test (select.rs:813-825). A one-shot
    /// emote/swing/cast plays as a masked upper-body overlay while the lower body is committed,
    /// and replaces the whole body otherwise. Full-body when standing still is CORRECT and is
    /// what the else-branch preserves; this predicate only decides which route a play takes.
    ///
    /// Decided ONCE per play, at arm time, never re-evaluated per frame. Benilla routes on the
    /// live state when the request is armed (driver.rs:563-670) and never reconsiders: a
    /// full-body play is ended by a movement-flag change, a masked play runs its own clock to
    /// completion. Re-deciding every frame instead would pop the legs mid-clip the moment you
    /// stopped running - the base would switch from the gait to the action's own leg keys
    /// partway through.
    ///
    /// <paramref name="turning"/> is Benilla's turn-key half of ROUTE_COMMITTED_MOVE (0x20003f =
    /// dir bits + turn keys + swim). This client has no turn-keys-only signal on UnitState -
    /// its <c>Steering</c> conflates the turn keys with mouse-look, and masking on mouse-look
    /// would swallow full-body casting almost entirely, since a player is mouse-looking nearly
    /// all the time. Callers pass false until UnitState carries the turn bits on their own;
    /// a stationary turning cast stays full-body until then.
    /// </summary>
    public static bool CommittedLower(bool moving, bool turning, bool swimming, bool seated,
        bool mounted, bool combatAnimation, bool falling)
        // Mounted FORCES the mask in Benilla (driver.rs:617-622) - the steed owns the legs.
        => mounted || moving || turning || swimming || seated ||
           (combatAnimation && falling);
}
