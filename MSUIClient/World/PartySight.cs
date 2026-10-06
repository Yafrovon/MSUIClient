using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.World.Collision;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World;

/// <summary>
/// Command View "party sight": the picture is the camera's own view plus the primary's view,
/// reprojected - and nothing else. Owner, 2026-09-02: "My view should be my camera's view + all
/// the summed views of my party members, then kind of triangulated back to me." The roof plane
/// and the sight tunnels only know WHERE the unit stands; this knows what it SEES, so a
/// character at a cave mouth opens the hillside over the cave floor it is looking at, and what
/// the hole would otherwise expose beyond that view is fogged, not shown.
///
/// HOW
///   1. A distance CUBE MAP from the primary's eye, rendered from the collision world (the
///      same faces the character's line of sight is judged on) plus the terrain mesh: every
///      direction stores how far the nearest solid is. Re-rendered only when the eye moves or
///      the world changes, since it is anchored in world space around the unit.
///   2. Two distance buffers from the commander camera, same geometry: PLAIN (the nearest solid
///      the camera would see with no cut at all) and SEEN (the nearest surface the primary can
///      see - unblocked from its eye, and facing it).
///   3. The world shaders (terrain / wmo / doodad) keep every fragment the primary sees. A
///      fragment it cannot see is CUT when it lies nearer than the SEEN surface under its pixel
///      (it hides the primary's view), kept when it is what the camera would see anyway, and
///      FOGGED (drawn dark) when it is only visible because a cut opened onto it - the far end
///      of a cave, the other side of the map through a hole in a hill.
///   4. Picking reads the same buffers back under the cursor: the click lands on what the
///      picture shows, seen surface first, else the plain one.
///
/// Primary only for now (the owner's "party members" is the next step: one cube per member,
/// visible if ANY sees it). GL 3.3: one R32F cube map, no cube arrays.
/// </summary>
public sealed class PartySightPass : IDisposable
{
    /// <summary>The eye sits this far above the unit's feet.</summary>
    public const float EyeHeight = 1.6f;

    /// <summary>Cube map edge in texels. 512 keeps a 1-yd feature resolvable at 60 yd.</summary>
    private const int CubeSize = 512;

    private const int CubeUnit = 6;
    private const int SeenUnit = 7;
    private const int PlainUnit = 8;
    private const int SeenDilatedUnit = 9;

    /// <summary>How far the primary's sight reaches. Beyond it every direction reads as blocked,
    /// so distant scenery is never the primary's view.</summary>
    public float Range { get; set; } = 90f;

    /// <summary>Slack between the collision faces the cube is built from and the render faces
    /// tested against it (collision meshes are simplified; walls coincide, props do not).</summary>
    public float Bias { get; set; } = 0.45f;

    /// <summary>The eye moves this far before the cube is re-rendered.</summary>
    public float RefreshDistance { get; set; } = 0.35f;

    /// <summary>The cube is re-rendered at least this often (streamed geometry).</summary>
    public double RefreshSeconds { get; set; } = 1.0;

    /// <summary>True between <see cref="Update"/> with an eye and <see cref="EndFrame"/>: the
    /// world shaders apply the rule only while the buffers describe THIS frame's camera.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether the last <see cref="Update"/> produced a verdict (an eye was given and
    /// the buffers rendered). Unlike <see cref="Active"/> this survives the frame: the CPU pick
    /// mirror runs during input handling, between frames, and needs the last verdict.</summary>
    public bool Engaged { get; private set; }

    /// <summary>World-space eye the cube was rendered from.</summary>
    public Vector3 Eye { get; private set; }

    /// <summary>Window pixel (top-left origin) to read the pick distance under, next Update.</summary>
    public Vector2? Cursor { get; set; }

    public double CubeMilliseconds { get; private set; }
    public double PrePassMilliseconds { get; private set; }
    public int CubeRenders { get; private set; }

    private readonly GL _gl;
    private Shader? _distanceShader;
    private Shader? _seenShader;

    private uint _cubeTex, _cubeFbo, _cubeDepthRb;
    private uint _seenTex, _seenFbo, _seenDepthRb;
    private uint _plainTex, _plainFbo, _plainDepthRb;
    private int _targetWidth, _targetHeight;

    private uint _vao, _vbo;
    private int _vertexCount;
    private CollisionWorld? _uploadedWorld;

    private bool _cubeValid;
    private double _cubeRenderedAt;
    private bool _ready;
    private bool _failed;

    // Last readback under the cursor: what the picture shows there.
    private Vector2 _pickPixel;
    private float _pickDistance;       // 0 = nothing
    private bool _pickValid;

    public PartySightPass(GL gl) => _gl = gl;

    // ── GLSL ─────────────────────────────────────────────────────────────────────────────────
    // All passes share one vertex stage: world-space positions (collision VBO and the terrain
    // VAO both put them at attribute 0), made relative to an eye for float precision.
    // aTarget (collision VBO only): 1 = may be a sight target, 0 = blocker only (doodad hull).
    // Terrain VAOs carry a normal at attribute 1, so uForceTarget = 1 overrides it for them.
    private const string DepthVert = """
        #version 330 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in float aTarget;
        uniform mat4 uViewProjection;
        uniform vec3 uEye;
        uniform int uForceTarget;
        out vec3 vRel;
        out float vTarget;
        void main()
        {
            vRel = aPosition - uEye;
            vTarget = uForceTarget == 1 ? 1.0 : aTarget;
            gl_Position = uViewProjection * vec4(vRel, 1.0);
        }
        """;

    // Distance from the eye: the cube faces (a point-light shadow map storing distance, so the
    // lookup needs no per-face projection maths) and the PLAIN camera buffer.
    private const string DistanceFrag = """
        #version 330 core
        in vec3 vRel;
        out vec4 FragColor;
        void main() { FragColor = vec4(length(vRel), 0.0, 0.0, 1.0); }
        """;

    // SEEN camera buffer: distance of the nearest surface the primary can see. Unblocked from
    // its eye AND facing it: the side the camera looks at must be the side the eye is on, or a
    // thin roof's top face counts as seen because its underside is (the "slivers", 2026-09-02).
    // The face normal comes from screen derivatives, computed before any discard.
    private const string SeenFrag = """
        #version 330 core
        in vec3 vRel;
        in float vTarget;
        uniform samplerCube uPartySightCube;
        uniform vec3  uPartySightEye;
        uniform float uPartySightBias;
        out vec4 FragColor;
        void main()
        {
            vec3 n = normalize(cross(dFdx(vRel), dFdy(vRel)));
            if (vTarget < 0.5) discard;
            vec3 d = vRel - uPartySightEye;
            float dist = length(d);
            if (dist > texture(uPartySightCube, d).r + uPartySightBias) discard;
            if (dot(n, -vRel) < 0.0) n = -n;
            if (dot(n, -d) <= 0.0) discard;
            FragColor = vec4(length(vRel), 0.0, 0.0, 1.0);
        }
        """;

    // Dilation of the SEEN buffer: a separable max filter over +-DilateRadius pixels. The cut
    // is judged per pixel against the seen surface under it, so a roof fragment whose own ray
    // just misses the seen floor survived as a one-pixel line along the opening ("bits of the
    // cave bleeding into the empty space", owner 2026-09-02). Growing the seen region by a few
    // pixels takes those with it; the pick still reads the exact, undilated buffer.
    private const int DilateRadius = 3;

    private const string FullscreenVert = """
        #version 330 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string DilateFrag = """
        #version 330 core
        uniform sampler2D uSrc;
        uniform vec2 uStep;
        uniform int uRadius;
        out vec4 FragColor;
        void main()
        {
            ivec2 px = ivec2(gl_FragCoord.xy);
            ivec2 size = textureSize(uSrc, 0);
            ivec2 step = ivec2(uStep);
            float m = 0.0;
            for (int i = -uRadius; i <= uRadius; i++)
            {
                ivec2 q = clamp(px + step * i, ivec2(0), size - ivec2(1));
                m = max(m, texelFetch(uSrc, q, 0).r);
            }
            FragColor = vec4(m, 0.0, 0.0, 1.0);
        }
        """;

    private Shader? _dilateShader;
    private uint _fullscreenVao;
    private uint _seenDilTex, _seenDilFbo, _seenDilDepthRb;   // dilated seen (what the world shaders read)
    private uint _seenTmpTex, _seenTmpFbo, _seenTmpDepthRb;   // horizontal pass scratch

    private bool EnsureReady()
    {
        if (_ready) return true;
        if (_failed) return false;
        try
        {
            _distanceShader = Shader.FromSource(_gl, "party-sight-distance", DepthVert, DistanceFrag);
            _seenShader = Shader.FromSource(_gl, "party-sight-seen", DepthVert, SeenFrag);
            _dilateShader = Shader.FromSource(_gl, "party-sight-dilate", FullscreenVert, DilateFrag);
            _fullscreenVao = _gl.GenVertexArray();
            CreateCube();
            _ready = true;
            Console.WriteLine($"[party-sight] ready: cube {CubeSize}px, range {Range:0} yd");
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[party-sight] unavailable - {ex.Message}");
            return false;
        }
    }

    private unsafe void CreateCube()
    {
        _cubeTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, _cubeTex);
        for (int face = 0; face < 6; face++)
            _gl.TexImage2D((TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face), 0,
                InternalFormat.R32f, CubeSize, CubeSize, 0, PixelFormat.Red, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.TextureCubeMap, 0);

        _cubeDepthRb = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _cubeDepthRb);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, CubeSize, CubeSize);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        _cubeFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cubeFbo);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _cubeDepthRb);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.TextureCubeMapPositiveX, _cubeTex, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"cube framebuffer incomplete ({status})");
    }

    /// <summary>One R32F distance target with a depth renderbuffer, screen-sized.</summary>
    private unsafe (uint Fbo, uint Tex, uint DepthRb) CreateDistanceTarget(int width, int height, string name)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f,
            (uint)width, (uint)height, 0, PixelFormat.Red, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        uint depth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"{name} framebuffer incomplete ({status})");
        return (fbo, tex, depth);
    }

    private void EnsureCameraTargets(int width, int height)
    {
        if (_seenFbo != 0 && _targetWidth == width && _targetHeight == height) return;
        ReleaseCameraTargets();
        _targetWidth = width;
        _targetHeight = height;
        (_seenFbo, _seenTex, _seenDepthRb) = CreateDistanceTarget(width, height, "seen");
        (_plainFbo, _plainTex, _plainDepthRb) = CreateDistanceTarget(width, height, "plain");
        (_seenDilFbo, _seenDilTex, _seenDilDepthRb) = CreateDistanceTarget(width, height, "seen-dilated");
        (_seenTmpFbo, _seenTmpTex, _seenTmpDepthRb) = CreateDistanceTarget(width, height, "seen-scratch");
    }

    private void ReleaseCameraTargets()
    {
        if (_seenFbo != 0) { _gl.DeleteFramebuffer(_seenFbo); _gl.DeleteTexture(_seenTex); _gl.DeleteRenderbuffer(_seenDepthRb); }
        if (_plainFbo != 0) { _gl.DeleteFramebuffer(_plainFbo); _gl.DeleteTexture(_plainTex); _gl.DeleteRenderbuffer(_plainDepthRb); }
        if (_seenDilFbo != 0) { _gl.DeleteFramebuffer(_seenDilFbo); _gl.DeleteTexture(_seenDilTex); _gl.DeleteRenderbuffer(_seenDilDepthRb); }
        if (_seenTmpFbo != 0) { _gl.DeleteFramebuffer(_seenTmpFbo); _gl.DeleteTexture(_seenTmpTex); _gl.DeleteRenderbuffer(_seenTmpDepthRb); }
        _seenFbo = _plainFbo = _seenDilFbo = _seenTmpFbo = 0;
    }

    /// <summary>Grow the seen buffer by <see cref="DilateRadius"/> pixels: horizontal max into the
    /// scratch target, vertical max into the dilated target. Depth test off, fullscreen triangle.</summary>
    private void DilateSeen(int width, int height)
    {
        _gl.Disable(EnableCap.DepthTest);
        _dilateShader!.Use();
        _dilateShader.Set("uSrc", 0);
        _dilateShader.Set("uRadius", DilateRadius);
        _gl.BindVertexArray(_fullscreenVao);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _seenTmpFbo);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.BindTexture(TextureTarget.Texture2D, _seenTex);
        _dilateShader.Set("uStep", new Vector2(1, 0));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _seenDilFbo);
        _gl.BindTexture(TextureTarget.Texture2D, _seenTmpTex);
        _dilateShader.Set("uStep", new Vector2(0, 1));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Upload the collision world's triangles as a position + target list. Re-done
    /// only when the world object itself changes (a streamed rebuild hands over a new one).</summary>
    private unsafe void EnsureCollisionUpload(CollisionWorld? world)
    {
        if (ReferenceEquals(world, _uploadedWorld)) return;
        _uploadedWorld = world;
        _cubeValid = false;
        if (_vao == 0)
        {
            _vao = _gl.GenVertexArray();
            _vbo = _gl.GenBuffer();
        }
        _vertexCount = 0;
        if (world is null || world.IsEmpty) return;

        float[] positions = world.CopyPositions();      // x, y, z, target per vertex
        _vertexCount = positions.Length / 4;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = positions)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(positions.Length * sizeof(float)), p,
                BufferUsageARB.StaticDraw);
        const uint stride = 4 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
        Console.WriteLine($"[party-sight] collision upload {_vertexCount / 3:N0} triangles");
    }

    /// <summary>
    /// Once per frame, before the world renders: refresh the cube if the eye moved, then render
    /// the two camera buffers and read the pick distance under <see cref="Cursor"/>.
    /// <paramref name="eye"/> null = feature off this frame.
    /// </summary>
    public unsafe void Update(Vector3? eye, CollisionWorld? collision, TerrainRenderer? terrain,
        Camera camera, Vector2 framebufferSize, double nowSeconds)
    {
        Active = false;
        Engaged = false;
        _pickValid = false;
        if (eye is not Vector3 e || !EnsureReady()) return;
        int width = (int)framebufferSize.X, height = (int)framebufferSize.Y;
        if (width <= 0 || height <= 0) return;

        EnsureCollisionUpload(collision);
        if (_vertexCount == 0 && terrain is null) return;

        // ── Save the caller's state ──────────────────────────────────────────────────────────
        int* vp = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, vp);
        int* iv = stackalloc int[1];
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, iv); uint savedDrawFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, iv); uint savedReadFbo = (uint)iv[0];
        bool savedCull = _gl.IsEnabled(EnableCap.CullFace);
        bool savedDepth = _gl.IsEnabled(EnableCap.DepthTest);
        bool savedBlend = _gl.IsEnabled(EnableCap.Blend);

        _gl.Disable(EnableCap.CullFace);       // collision winding is not consistent
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.ClearColor(0f, 0f, 0f, 1f);         // 0 = nothing here, in every distance buffer

        // ── Cube: the primary's view of the solid world ──────────────────────────────────────
        bool refresh = !_cubeValid ||
            Vector3.DistanceSquared(e, Eye) > RefreshDistance * RefreshDistance ||
            nowSeconds - _cubeRenderedAt > RefreshSeconds;
        if (refresh)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Eye = e;
            RenderCube(terrain);
            _cubeValid = true;
            _cubeRenderedAt = nowSeconds;
            CubeRenders++;
            CubeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // ── Camera buffers: PLAIN (uncut view) and SEEN (the primary's view, reprojected) ────
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureCameraTargets(width, height);
            Matrix4x4 viewProjection = camera.RelativeViewProjection;

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _plainFbo);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _distanceShader!.Use();
            _distanceShader.Set("uViewProjection", viewProjection);
            _distanceShader.Set("uEye", camera.Position);
            DrawSolidWorld(_distanceShader, viewProjection, camera.Position, float.MaxValue, terrain);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _seenFbo);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _seenShader!.Use();
            _seenShader.Set("uViewProjection", viewProjection);
            _seenShader.Set("uEye", camera.Position);
            _seenShader.Set("uPartySightCube", CubeUnit);
            _seenShader.Set("uPartySightEye", Eye - camera.Position);
            _seenShader.Set("uPartySightBias", Bias);
            _gl.ActiveTexture(TextureUnit.Texture0 + CubeUnit);
            _gl.BindTexture(TextureTarget.TextureCubeMap, _cubeTex);
            _gl.ActiveTexture(TextureUnit.Texture0);
            DrawSolidWorld(_seenShader, viewProjection, camera.Position, float.MaxValue, terrain);

            DilateSeen(width, height);
            ReadPickUnderCursor(width, height);
            PrePassMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // ── Restore ──────────────────────────────────────────────────────────────────────────
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, savedDrawFbo);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, savedReadFbo);
        _gl.Viewport(vp[0], vp[1], (uint)vp[2], (uint)vp[3]);
        if (savedCull) _gl.Enable(EnableCap.CullFace);
        if (!savedDepth) _gl.Disable(EnableCap.DepthTest);
        if (savedBlend) _gl.Enable(EnableCap.Blend);
        _gl.BindVertexArray(0);
        Active = true;
        Engaged = true;
    }

    // Asynchronous readback: the read is queued into a pixel buffer and collected the NEXT
    // frame, from the other buffer of the pair, so the CPU never waits on the GPU (a direct
    // ReadPixels cost ~3 ms of stall per frame). One frame of pick latency is invisible.
    private readonly uint[] _pickPbo = new uint[2];
    private readonly Vector2?[] _pickPboPixel = new Vector2?[2];
    private int _pickPboWrite;

    /// <summary>What the picture shows under the cursor: the seen surface if any, else the plain
    /// one. Collects last frame's read, then queues this frame's.</summary>
    private unsafe void ReadPickUnderCursor(int width, int height)
    {
        if (_pickPbo[0] == 0)
        {
            for (int i = 0; i < 2; i++)
            {
                _pickPbo[i] = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pickPbo[i]);
                _gl.BufferData(BufferTargetARB.PixelPackBuffer, 2 * sizeof(float), null, BufferUsageARB.StreamRead);
            }
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        }

        // Collect the read queued last frame (the other buffer).
        int read = 1 - _pickPboWrite;
        if (_pickPboPixel[read] is Vector2 queuedPixel)
        {
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pickPbo[read]);
            float* mapped = (float*)_gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, 2 * sizeof(float),
                MapBufferAccessMask.ReadBit);
            if (mapped != null)
            {
                float seen = mapped[0], plain = mapped[1];
                _gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
                _pickPixel = queuedPixel;
                _pickDistance = seen > 0f ? seen : plain;
                _pickValid = _pickDistance > 0f;
            }
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            _pickPboPixel[read] = null;
        }

        // Queue this frame's read.
        _pickPboPixel[_pickPboWrite] = null;
        if (Cursor is Vector2 cursor)
        {
            int x = (int)cursor.X, y = height - 1 - (int)cursor.Y;     // GL reads bottom-up
            if (x >= 0 && y >= 0 && x < width && y < height)
            {
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pickPbo[_pickPboWrite]);
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _seenFbo);
                _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                _gl.ReadPixels(x, y, 1, 1, PixelFormat.Red, PixelType.Float, (void*)0);
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _plainFbo);
                _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                _gl.ReadPixels(x, y, 1, 1, PixelFormat.Red, PixelType.Float, (void*)sizeof(float));
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
                _pickPboPixel[_pickPboWrite] = cursor;
            }
        }
        _pickPboWrite = read;
    }

    /// <summary>The distance along the pixel's ray to what the picture shows there, if the last
    /// frame read it under (about) that pixel.</summary>
    public bool TryPickDistance(Vector2 pixel, out float distance)
    {
        distance = _pickDistance;
        return Engaged && _pickValid && Vector2.DistanceSquared(pixel, _pickPixel) <= 9f;
    }

    /// <summary>After the world passes that consult the buffers: later passes (portal previews,
    /// composites) must not read a stale verdict.</summary>
    public void EndFrame() => Active = false;

    // ── Cut void fill ────────────────────────────────────────────────────────────────────────
    // A cave sits in a HOLE in the terrain: slice its wall and there is nothing behind it but
    // sky, which reads as broken (purple patches, owner 2026-09-03). This draws the cut volume
    // as a dark box at the far plane, so it lands only on pixels nothing else painted - real
    // scenery seen through the bubble is never covered.
    private const string VoidVert = """
        #version 330 core
        layout (location = 0) in vec3 aPosition;
        uniform mat4 uViewProjection;
        void main() { gl_Position = uViewProjection * vec4(aPosition, 1.0); }
        """;

    private const string VoidFrag = """
        #version 330 core
        uniform vec3 uColor;
        out vec4 FragColor;
        void main()
        {
            gl_FragDepth = 1.0;
            FragColor = vec4(uColor, 1.0);
        }
        """;

    private Shader? _voidShader;
    private uint _voidVao, _voidVbo;
    private readonly float[] _voidVertices = new float[36 * 3];

    /// <summary>Paint the cut volume (footprint x [floorZ, cutZ + 30]) in <paramref name="colour"/>
    /// wherever the frame is still empty. Call after the world passes, before the HUD.</summary>
    public unsafe void DrawCutVoid(Camera camera, WorldCut cut, float floorZ, Vector3 colour)
    {
        if (_failed) return;
        if (_voidShader is null)
        {
            try
            {
                _voidShader = Shader.FromSource(_gl, "party-sight-void", VoidVert, VoidFrag);
                _voidVao = _gl.GenVertexArray();
                _voidVbo = _gl.GenBuffer();
                _gl.BindVertexArray(_voidVao);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _voidVbo);
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_voidVertices.Length * sizeof(float)), null,
                    BufferUsageARB.StreamDraw);
                _gl.EnableVertexAttribArray(0);
                _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
                _gl.BindVertexArray(0);
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.WriteLine($"[party-sight] void fill unavailable - {ex.Message}");
                return;
            }
        }

        Vector3 c = camera.Position;
        Vector3 min = new(cut.Min.X - c.X, cut.Min.Y - c.Y, floorZ - c.Z);
        Vector3 max = new(cut.Max.X - c.X, cut.Max.Y - c.Y, cut.CutZ + 30f - c.Z);
        int w = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e)
        {
            foreach (Vector3 v in new[] { a, b, d, a, d, e })
            { _voidVertices[w++] = v.X; _voidVertices[w++] = v.Y; _voidVertices[w++] = v.Z; }
        }
        Vector3 p000 = new(min.X, min.Y, min.Z), p100 = new(max.X, min.Y, min.Z);
        Vector3 p010 = new(min.X, max.Y, min.Z), p110 = new(max.X, max.Y, min.Z);
        Vector3 p001 = new(min.X, min.Y, max.Z), p101 = new(max.X, min.Y, max.Z);
        Vector3 p011 = new(min.X, max.Y, max.Z), p111 = new(max.X, max.Y, max.Z);
        Quad(p000, p100, p110, p010);   // bottom
        Quad(p001, p011, p111, p101);   // top
        Quad(p000, p001, p101, p100);   // -Y
        Quad(p010, p110, p111, p011);   // +Y
        Quad(p000, p010, p011, p001);   // -X
        Quad(p100, p101, p111, p110);   // +X

        _gl.BindVertexArray(_voidVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _voidVbo);
        fixed (float* p = _voidVertices)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_voidVertices.Length * sizeof(float)), p);

        bool savedCull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);   // depth forced to 1.0: only the empty pixels pass
        _gl.DepthMask(false);
        _voidShader.Use();
        _voidShader.Set("uViewProjection", camera.RelativeViewProjection);
        _voidShader.Set("uColor", colour);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
        if (savedCull) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    private static readonly (Vector3 Dir, Vector3 Up)[] CubeFaces =
    [
        (new Vector3(1, 0, 0), new Vector3(0, -1, 0)),
        (new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),
        (new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
        (new Vector3(0, -1, 0), new Vector3(0, 0, -1)),
        (new Vector3(0, 0, 1), new Vector3(0, -1, 0)),
        (new Vector3(0, 0, -1), new Vector3(0, -1, 0)),
    ];

    private void RenderCube(TerrainRenderer? terrain)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cubeFbo);
        _gl.Viewport(0, 0, CubeSize, CubeSize);
        // Clear to the range: a direction with nothing solid within reach reads as "seen out
        // to the range and no further", so distant scenery never counts as the primary's view
        // (it did at first, and every rock in front of a far, culled hillside was cut to sky).
        _gl.ClearColor(Range, 0f, 0f, 1f);
        _distanceShader!.Use();
        _distanceShader.Set("uEye", Eye);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1f, 0.05f, Range);
        for (int face = 0; face < 6; face++)
        {
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face), _cubeTex, 0);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            Matrix4x4 viewProjection =
                Matrix4x4.CreateLookAt(Vector3.Zero, CubeFaces[face].Dir, CubeFaces[face].Up) * projection;
            _distanceShader.Set("uViewProjection", viewProjection);
            DrawSolidWorld(_distanceShader, viewProjection, Eye, Range, terrain);
        }
        _gl.ClearColor(0f, 0f, 0f, 1f);
    }

    /// <summary>Collision faces, then terrain, through <paramref name="shader"/> (already in use
    /// with uViewProjection/uEye set). Terrain culls per chunk against the given frustum.</summary>
    private void DrawSolidWorld(Shader shader, Matrix4x4 viewProjection, Vector3 eye, float range,
        TerrainRenderer? terrain)
    {
        if (_vertexCount > 0)
        {
            shader.Set("uForceTarget", 0);          // the VBO carries its own target flag
            _gl.BindVertexArray(_vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertexCount);
            _gl.BindVertexArray(0);
        }
        shader.Set("uForceTarget", 1);              // terrain is always a target
        terrain?.RenderDepth(viewProjection, eye, range);
    }

    /// <summary>
    /// The party-sight sampler units and an inactive verdict, for a world shader drawn WITHOUT a pass. A renderer
    /// with no PartySight must still call this: left at their default unit 0 the samplerCube and sampler2Ds
    /// collide with the shader's own unit-0 texture and the driver REJECTS every draw (GL_INVALID_OPERATION
    /// "program texture usage"). A Real Portals prepared world is built without a pass, so after a crossing the
    /// whole ground silently stopped drawing (WoW Karting, 2026-10-04: "floor gone").
    /// </summary>
    public static void BindInactive(Shader shader)
    {
        BindSamplerUnits(shader);
        shader.Set("uPartySightActive", 0);
    }

    private static void BindSamplerUnits(Shader shader)
    {
        shader.Set("uPartySightCube", CubeUnit);
        shader.Set("uPartySeenDepth", SeenUnit);
        shader.Set("uPartyPlainDepth", PlainUnit);
        shader.Set("uPartySeenDilated", SeenDilatedUnit);
    }

    /// <summary>
    /// Bind the verdict for a world shader (terrain / wmo / doodad) that is already in use.
    /// The sampler units are set every time, active or not: a samplerCube and a sampler2D
    /// left on the default unit 0 together is a GL error at draw time.
    /// </summary>
    public void Apply(Shader shader, Vector3 cameraPosition)
    {
        BindSamplerUnits(shader);
        shader.Set("uPartySightActive", Active ? 1 : 0);
        if (!Active) return;
        shader.Set("uPartySightEye", Eye - cameraPosition);
        shader.Set("uPartySightBias", Bias);
        _gl.ActiveTexture(TextureUnit.Texture0 + CubeUnit);
        _gl.BindTexture(TextureTarget.TextureCubeMap, _cubeTex);
        _gl.ActiveTexture(TextureUnit.Texture0 + SeenUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _seenTex);         // exact: the cut
        _gl.ActiveTexture(TextureUnit.Texture0 + PlainUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _plainTex);
        _gl.ActiveTexture(TextureUnit.Texture0 + SeenDilatedUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _seenDilTex);      // grown: the fogged rim
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        if (_cubeFbo != 0) _gl.DeleteFramebuffer(_cubeFbo);
        if (_cubeDepthRb != 0) _gl.DeleteRenderbuffer(_cubeDepthRb);
        if (_cubeTex != 0) _gl.DeleteTexture(_cubeTex);
        ReleaseCameraTargets();
        for (int i = 0; i < 2; i++) if (_pickPbo[i] != 0) _gl.DeleteBuffer(_pickPbo[i]);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_fullscreenVao != 0) _gl.DeleteVertexArray(_fullscreenVao);
        if (_voidVao != 0) _gl.DeleteVertexArray(_voidVao);
        if (_voidVbo != 0) _gl.DeleteBuffer(_voidVbo);
        _voidShader?.Dispose();
        _distanceShader?.Dispose();
        _seenShader?.Dispose();
        _dilateShader?.Dispose();
    }
}
