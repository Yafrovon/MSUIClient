using System.Numerics;

namespace MSUIClient.Engine;

/// <summary>
/// Third-person orbit camera.
///
/// IMPORTANT — THERE IS NO COORDINATE CONVERSION IN THIS CLIENT.
/// Everything works natively in WoW world space:
///     +X = north      +Y = west       +Z = up
///     orientation = radians CCW about +Z, measured from +X
///
/// The browser build needed a conversion module because three.js hardcodes
/// Y-up. OpenGL does not care: it only needs a consistent view and projection
/// matrix, and "up" is whatever we pass to LookAt. So the entire coords.ts
/// layer — and every bug it could have produced — simply does not exist here.
/// Positions read from ADT, vmaps, DBC and the network all mean the same thing
/// end to end, and the debug HUD can print raw values that match `.gps` in a
/// real client with no translation.
///
/// System.Numerics.Matrix4x4 builds row-vector matrices; GLSL expects
/// column-major. Upload with transpose = false and multiply as
/// `vec4(pos,1) * matrix` in the shader, or transpose here. We transpose here,
/// once, in <see cref="ViewProjection"/>, so shaders stay conventional.
/// </summary>
public sealed class Camera
{
    private readonly CameraFollowController _follow = new();
    /// <summary>Point the camera orbits, in WoW space.</summary>
    public Vector3 Target;

    /// <summary>
    /// Radians CCW about +Z from +X. Matches WoW orientation exactly.
    ///
    /// This is the CHARACTER'S FACING, not the camera's. The controller reads it
    /// straight through as the character's orientation, and it is the value a
    /// movement packet wants in Phase 2. Where the camera actually sits is
    /// <see cref="ViewYaw"/>.
    /// </summary>
    public float Yaw;

    /// <summary>
    /// Camera-only yaw OFFSET from <see cref="Yaw"/>, in radians, kept in
    /// (-pi, pi].
    ///
    /// This is the whole left-button-versus-right-button distinction. Holding
    /// the LEFT button swings the camera around the character without turning
    /// him, so you can walk north and look at your own face - that motion goes
    /// here. Holding the RIGHT button turns the character, so that motion goes
    /// into <see cref="Yaw"/> instead.
    ///
    /// Keeping them separate is what makes both possible from one heading.
    /// Folding the offset back in (see <see cref="FoldOrbitIntoFacing"/>) turns
    /// the character to wherever you had swung the camera, without the camera
    /// moving a pixel - which is exactly what the real client does the instant
    /// you press the right button.
    ///
    /// Signed and wrapped rather than 0..2pi so easing it back to zero always
    /// takes the short way round.
    /// </summary>
    public float OrbitYaw;

    /// <summary>Where the camera actually sits and looks. Facing plus the orbit offset.</summary>
    public float ViewYaw => Yaw + OrbitYaw;

    /// <summary>
    /// Radians of camera ELEVATION ABOVE the target. Positive puts the camera
    /// up high looking down; negative puts it low looking up.
    ///
    /// The sign matters and is easy to get backwards. Screen Y grows DOWNWARD,
    /// so a mouse-look handler that wants standard (non-inverted) behaviour —
    /// mouse up means look up — must ADD the mouse delta, not subtract it:
    /// looking up means the camera drops below the target, which is a SMALLER
    /// pitch. See ClientWindow's MouseMove handler.
    /// </summary>
    public float Pitch = 0.35f;

    /// <summary>Zoom distance the user asked for, via the wheel.</summary>
    public float Distance = 9f;

    /// <summary>
    /// Distance actually used to place the camera. Camera collision writes this
    /// every frame so the camera can be pulled in toward the character without
    /// destroying the zoom level the user chose — let go of the wall and it
    /// eases back out to <see cref="Distance"/>.
    ///
    /// Kept separate rather than clamping Distance directly: overwriting the
    /// user's zoom means walking past a tree permanently zooms you in, which
    /// feels broken in a way that is hard to describe and easy to ship.
    /// </summary>
    public float EffectiveDistance = 9f;

    public float MinDistance = 1.5f;
    public float MaxDistance = 40f;

    public float FieldOfViewDegrees = 70f;
    public float NearPlane = 0.1f;
    public float FarPlane = 2000f;

    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>
    /// Optional authored view used by model portrait cameras. Ordinary world
    /// cameras leave these unset and continue through the orbit properties.
    /// </summary>
    public Vector3? AuthoredPosition { get; set; }
    public Vector3? AuthoredTarget { get; set; }
    public Vector3? AuthoredUp { get; set; }
    public float? AuthoredVerticalFieldOfViewRadians { get; set; }

    /// <summary>Height above <see cref="Target"/> the camera actually looks at.</summary>
    public float EyeHeight = 2.2f;

    public const float PitchLimit = 1.45f;   // ~83 degrees, short of gimbal lock

    public Vector3 EyeTarget => AuthoredTarget ?? (Target + new Vector3(0, 0, EyeHeight));

    /// <summary>
    /// Unit vector from <see cref="EyeTarget"/> toward the camera, in WoW space.
    /// The camera-collision pass marches along this to find the first thing in
    /// the way.
    /// </summary>
    public Vector3 OrbitDirection
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            float yaw = ViewYaw;
            // Behind the target: negate the facing direction.
            return new Vector3(
                -MathF.Cos(yaw) * cp,
                -MathF.Sin(yaw) * cp,
                MathF.Sin(Pitch));
        }
    }

    /// <summary>Camera position in WoW space, derived from yaw/pitch/distance.</summary>
    public Vector3 Position => AuthoredPosition ?? (EyeTarget + OrbitDirection * EffectiveDistance);

    /// <summary>Unit vector the camera looks along, in WoW space.</summary>
    public Vector3 Forward
    {
        get
        {
            if (AuthoredPosition is Vector3 authoredPosition &&
                AuthoredTarget is Vector3 authoredTarget)
            {
                Vector3 authoredForward = authoredTarget - authoredPosition;
                if (authoredForward.LengthSquared() > 1e-12f)
                    return Vector3.Normalize(authoredForward);
            }

            float cp = MathF.Cos(Pitch);
            float yaw = ViewYaw;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(yaw) * cp,
                MathF.Sin(yaw) * cp,
                -MathF.Sin(Pitch)));
        }
    }

    /// <summary>
    /// Horizontal facing, ignoring pitch — what movement input follows.
    /// Deliberately on <see cref="Yaw"/> and NOT ViewYaw: pressing W walks the
    /// character forward, not toward wherever the camera has been swung.
    /// </summary>
    public Vector3 FlatForward => new(MathF.Cos(Yaw), MathF.Sin(Yaw), 0);

    /// <summary>Right of <see cref="FlatForward"/>. In a Z-up left-of-west world this is (sin, -cos, 0).</summary>
    public Vector3 FlatRight => new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0);

    /// <summary>Turn the character, and with him the camera. Right-drag and the arrow keys.</summary>
    public void Rotate(float yawDelta, float pitchDelta)
    {
        Yaw += yawDelta;
        Pitch = Math.Clamp(Pitch + pitchDelta, -PitchLimit, PitchLimit);

        const float tau = MathF.PI * 2f;
        Yaw = ((Yaw % tau) + tau) % tau;
    }

    /// <summary>Swing the camera around the character without turning him. Left-drag.</summary>
    public void RotateView(float yawDelta)
    {
        if (yawDelta == 0f) return;
        OrbitYaw = Wrap(OrbitYaw + yawDelta);
    }

    /// <summary>
    /// Turn the character to wherever the camera has been swung, and drop the
    /// offset. The camera does not move: ViewYaw is unchanged because the same
    /// angle simply moved from one term to the other.
    ///
    /// This is what the real client does the moment you press the right button
    /// after looking at your own face - the character spins to put his back to
    /// you, from your point of view instantly and without the view shifting.
    /// </summary>
    public void FoldOrbitIntoFacing()
    {
        if (OrbitYaw == 0f) return;

        const float tau = MathF.PI * 2f;
        Yaw = (((Yaw + OrbitYaw) % tau) + tau) % tau;
        OrbitYaw = 0f;
    }

    /// <summary>
    /// Restore a character facing without moving the visible camera. This is used when Ice
    /// Block ends: mouse-look during the frozen pose is camera-only, so its displacement becomes
    /// an orbit rather than snapping the character to the view on the next frame.
    /// </summary>
    public void SetFacingKeepingView(float facing)
    {
        float viewYaw = ViewYaw;
        const float tau = MathF.PI * 2f;
        Yaw = ((facing % tau) + tau) % tau;
        OrbitYaw = Wrap(viewYaw - Yaw);
    }

    /// <summary>
    /// Advance vanilla's edge-armed camera return. This only writes the camera-only orbit offset;
    /// the character facing remains the authority for movement and keyboard-turn carry.
    /// </summary>
    public void AdvanceFollow(in CameraFollowInput input, float dt, bool lookHeld)
    {
        if (_follow.Advance(input, ViewYaw, dt, lookHeld) is not float viewYaw) return;
        OrbitYaw = CameraFollowLaw.Wrap(viewYaw - Yaw);
        if (MathF.Abs(OrbitYaw) < CameraFollowLaw.Epsilon) OrbitYaw = 0f;
    }

    /// <summary>Drop camera-follow edge/transition state across a detached/free-view lifecycle.</summary>
    public void ResetFollow() => _follow.Reset();

    /// <summary>Wrap to (-pi, pi], so easing toward zero always takes the short way.</summary>
    private static float Wrap(float radians)
    {
        const float tau = MathF.PI * 2f;
        radians = ((radians % tau) + tau) % tau;
        return radians > MathF.PI ? radians - tau : radians;
    }

    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance - delta, MinDistance, MaxDistance);
        // Zooming IN takes effect immediately; zooming out is left to the
        // collision pass, which eases outward only as far as it is safe.
        if (EffectiveDistance > Distance) EffectiveDistance = Distance;
    }

    /// <summary>
    /// True when the outdoor ADT height field is a shell above an interior
    /// camera target, rather than ground the camera boom may collide with.
    ///
    /// Blackrock Mountain is the canonical case: the character and the WMO
    /// interior are around Z=166 while the outdoor mountain terrain at the same
    /// XY is around Z=275. Marching the camera ray against that height field
    /// collapses the boom to first person on every frame. WMO collision remains
    /// authoritative for the actual interior walls and floors.
    /// </summary>
    public static bool TerrainIsOverhead(float eyeZ, float? terrainZ, float slack)
        => terrainZ is float surfaceZ && surfaceZ - eyeZ > slack;

    /// <summary>A camera shake's displacement this frame (GameLoop.CameraShake.cs). It moves the
    /// VIEW only: Position, collision and everything that measures from the eye keep the true one.</summary>
    public Vector3 ShakeOffset { get; set; }

    public Matrix4x4 View
        => Matrix4x4.CreateLookAt(Position + ShakeOffset, EyeTarget + ShakeOffset, AuthoredUp ?? Vector3.UnitZ);

    /// <summary>
    /// View matrix for camera-relative rendering. Geometry using this matrix
    /// must subtract <see cref="Position"/> from its model translation first.
    /// Keeping both eye and nearby objects close to zero avoids float precision
    /// loss at large map coordinates.
    /// </summary>
    public Matrix4x4 RelativeView
        => Matrix4x4.CreateLookAt(ShakeOffset, EyeTarget - Position + ShakeOffset, AuthoredUp ?? Vector3.UnitZ);

    public Matrix4x4 Projection
        => Matrix4x4.CreatePerspectiveFieldOfView(
            AuthoredVerticalFieldOfViewRadians ?? FieldOfViewDegrees * MathF.PI / 180f,
            AspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// Combined view-projection, in System.Numerics' row-vector convention
    /// (v_clip = v * View * Projection).
    ///
    /// DO NOT transpose this for GLSL. System.Numerics stores Matrix4x4
    /// row-major in memory; glUniformMatrix4fv with transpose = false reads
    /// those same bytes as COLUMN-major, which is exactly the flip GLSL needs.
    /// Uploading the matrix as-is therefore hands the shader the transpose it
    /// wants, and `uViewProjection * vec4(pos, 1.0)` is correct.
    ///
    /// Transposing here as well double-flips it: every vertex lands in garbage
    /// clip space, nothing survives clipping, and the screen shows only the
    /// clear colour. That failure looks exactly like "geometry isn't rendering"
    /// and wastes a lot of time, so leave this alone.
    /// </summary>
    public Matrix4x4 ViewProjection => View * Projection;

    public Matrix4x4 RelativeViewProjection => RelativeView * Projection;

    /// <summary>Unproject a top-left-origin window pixel into a WoW-world ray.</summary>
    public (Vector3 Origin, Vector3 Direction)? ScreenPointToRay(Vector2 pixel, Vector2 size)
    {
        if (size.X <= 0f || size.Y <= 0f || !Matrix4x4.Invert(ViewProjection, out var inverse))
            return null;

        float x = 2f * pixel.X / size.X - 1f;
        float y = 1f - 2f * pixel.Y / size.Y;

        // System.Numerics' perspective matrix uses depth 0..1 on the CPU.
        Vector4 near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        Vector4 far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        if (MathF.Abs(near.W) < 1e-6f || MathF.Abs(far.W) < 1e-6f) return null;
        Vector3 nearWorld = new(near.X / near.W, near.Y / near.W, near.Z / near.W);
        Vector3 farWorld = new(far.X / far.W, far.Y / far.W, far.Z / far.W);
        Vector3 direction = farWorld - nearWorld;
        if (direction.LengthSquared() < 1e-8f) return null;
        return (Position, Vector3.Normalize(direction));
    }

    /// <summary>Project a WoW-world point to top-left-origin window pixels. Returns true
    /// only when the point is in front of the camera AND lands inside the visible viewport
    /// - the historical "is this drawable HERE" contract, right for a single mark or label.
    /// Overlay polylines want <see cref="TryProjectToScreen"/> instead so a path does not
    /// vanish the instant one endpoint scrolls past the window edge.</summary>
    public bool TryWorldToScreen(Vector3 world, Vector2 size, out Vector2 pixel)
        => TryProjectToScreen(world, size, out pixel, out bool onScreen) && onScreen;

    /// <summary>Project a WoW-world point to top-left-origin window pixels, succeeding for
    /// EVERY point in front of the camera. The pixel is valid even when it falls outside the
    /// window - <paramref name="onScreen"/> reports whether it also lands within the viewport
    /// rect. A connecting line drawn to an off-screen-but-in-front point is clipped by the
    /// GPU to its visible portion, so a route/plan stays on screen as the camera flies past
    /// it instead of being culled the moment a waypoint leaves the frame.</summary>
    public bool TryProjectToScreen(Vector3 world, Vector2 size, out Vector2 pixel, out bool onScreen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection);
        if (clip.W <= 1e-5f)   // behind the camera: no meaningful pixel exists
        {
            pixel = default;
            onScreen = false;
            return false;
        }
        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        pixel = new Vector2((x + 1f) * 0.5f * size.X, (1f - y) * 0.5f * size.Y);
        onScreen = x is >= -1f and <= 1f && y is >= -1f and <= 1f;
        return true;
    }

    /// <summary>Project a WORLD-space SEGMENT to screen pixels, clipping it to the camera
    /// plane in homogeneous clip space FIRST. Projecting each endpoint on its own drops the
    /// whole segment the instant ONE end passes behind the camera (its <c>W ≤ 0</c> has no
    /// pixel) — which is why a plan/route line vanishes completely on a small camera pivot,
    /// not merely when it scrolls off the window edge (that case is <see cref="TryProjectToScreen"/>'s
    /// job). Here a segment with one end behind is CUT at the camera plane so its visible half
    /// still draws. Returns false only when the whole segment is behind. Endpoints may land
    /// outside the viewport; the caller's screen-space clip trims the dash walk.</summary>
    public bool TryProjectSegmentToScreen(Vector3 a, Vector3 b, Vector2 size,
        out Vector2 pixelA, out Vector2 pixelB)
    {
        pixelA = default;
        pixelB = default;
        Matrix4x4 vp = ViewProjection;
        Vector4 ca = Vector4.Transform(new Vector4(a, 1f), vp);
        Vector4 cb = Vector4.Transform(new Vector4(b, 1f), vp);
        const float wMin = 1e-4f;

        bool aBehind = ca.W <= wMin;
        bool bBehind = cb.W <= wMin;
        if (aBehind && bBehind) return false;            // whole segment behind the camera
        if (aBehind) ca += (cb - ca) * ((wMin - ca.W) / (cb.W - ca.W));   // cut A to the plane
        else if (bBehind) cb += (ca - cb) * ((wMin - cb.W) / (ca.W - cb.W));

        pixelA = ClipToPixel(ca, size);
        pixelB = ClipToPixel(cb, size);
        return true;
    }

    private static Vector2 ClipToPixel(Vector4 clip, Vector2 size)
    {
        float x = clip.X / clip.W, y = clip.Y / clip.W;
        return new Vector2((x + 1f) * 0.5f * size.X, (1f - y) * 0.5f * size.Y);
    }

    /// <summary>
    /// Conservative AABB test performed directly in homogeneous clip space.
    ///
    /// Plane extraction is compact, but it is also exceptionally easy to get
    /// subtly wrong when a row-vector CPU matrix is uploaded as a column-vector
    /// GLSL matrix. This takes the exact CPU-side transform corresponding to
    /// the shader and rejects a box only when all eight corners lie outside the
    /// same clip plane. A visible box may survive; it may never be culled.
    /// </summary>
    public static bool BoxInFrustum(Matrix4x4 viewProjection, Vector3 min, Vector3 max)
    {
        const int AllPlanes = 0b11_1111;
        int outsideEveryCorner = AllPlanes;

        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector4(
                (c & 1) == 0 ? min.X : max.X,
                (c & 2) == 0 ? min.Y : max.Y,
                (c & 4) == 0 ? min.Z : max.Z,
                1f);

            Vector4 clip = Vector4.Transform(corner, viewProjection);
            int outside = 0;
            if (clip.X < -clip.W) outside |= 1 << 0;
            if (clip.X >  clip.W) outside |= 1 << 1;
            if (clip.Y < -clip.W) outside |= 1 << 2;
            if (clip.Y >  clip.W) outside |= 1 << 3;
            if (clip.Z < -clip.W) outside |= 1 << 4;
            if (clip.Z >  clip.W) outside |= 1 << 5;

            outsideEveryCorner &= outside;
            if (outsideEveryCorner == 0) return true;
        }

        return false;
    }
}
