using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Engine.UI;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

public readonly record struct SpellMeshDraw(
    long Id,
    string Path,
    M2Model Model,
    Matrix4x4 Transform,
    float Age,
    int SequenceIndex,
    bool GroundAnchor,
    string? CustomTexture,
    Vector3 Tint,
    float Opacity);

/// <summary>A depth-tested, camera-facing world quad with a bottom-center anchor.</summary>
public readonly record struct WorldBillboardDraw(
    Vector3 BottomCenter,
    float WorldSize,
    string Texture,
    Vector2 UvMin,
    Vector2 UvMax,
    Vector3 Tint,
    float Opacity);

public static class WorldBillboardLaw
{
    /// <summary>The reference quad LUT: TL, TR, BL / BL, TR, BR, bottom-centered.</summary>
    public static float[] Vertices(Vector3 bottom, float size, Vector3 right, Vector3 up,
        Vector2 uvMin, Vector2 uvMax)
    {
        Vector3 halfRight = right * (size * .5f);
        Vector3 fullUp = up * size;
        Vector3 tl = bottom - halfRight + fullUp;
        Vector3 tr = bottom + halfRight + fullUp;
        Vector3 br = bottom + halfRight;
        Vector3 bl = bottom - halfRight;
        float u0 = uvMin.X, v0 = uvMin.Y;
        float u1 = uvMax.X, v1 = uvMax.Y;
        return
        [
            tl.X, tl.Y, tl.Z, u0, v0,
            tr.X, tr.Y, tr.Z, u1, v0,
            bl.X, bl.Y, bl.Z, u0, v1,
            bl.X, bl.Y, bl.Z, u0, v1,
            tr.X, tr.Y, tr.Z, u1, v0,
            br.X, br.Y, br.Z, u1, v1,
        ];
    }
}

/// <summary>
/// Draws the mesh half of SpellVisualEffectName M2s. The particle half stays in
/// ParticleRenderer; both consume the same SpellEffectSource instances so a
/// cast, missile and impact remain spatially locked together.
/// </summary>
public sealed class SpellEffectMeshRenderer : IDisposable
{
    private const int FloatsPerVertex = 16;

    private sealed class GroundQuad
    {
        public int Bone;
        public M2Vertex[] Corners = [];
    }

    private sealed class Batch
    {
        public uint Start, Count;
        public Texture? Texture;
        public int Blend;
        public bool TwoSided;
        public bool NoZWrite;
        public bool NoZTest;
        public bool Unlit;
        public int FogPolicy;
        public GroundQuad? Ground;
        public bool AnimatedAlpha;
        public string? TexturePath;
        public M2AnimTrack<Vector3>? UvTranslation;
        public M2Batch Source = null!;
        public bool Transparent => Blend >= 2 || NoZWrite || AnimatedAlpha;
    }

    private sealed class Mesh : IDisposable
    {
        private readonly GL _gl;
        public uint Vao, Vbo, Ebo;
        public readonly List<Batch> Batches = [];
        public M2Animator? Animator;
        public int BoneCount;
        public Mesh(GL gl) => _gl = gl;
        public void Dispose()
        {
            if (Vao != 0) _gl.DeleteVertexArray(Vao);
            if (Vbo != 0) _gl.DeleteBuffer(Vbo);
            if (Ebo != 0) _gl.DeleteBuffer(Ebo);
        }
    }

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Mesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _textureTints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _modelColorHues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drop the cached mesh for a model so the next draw rebuilds it from the
    /// CURRENT bytes. Meshes bake their batch textures at build time, so without
    /// this a creator byte-patch / texture swap never reaches the mesh-drawn
    /// parts of an effect (the Cone of Cold cloud, glow planes, ground rings) -
    /// only particles updated.
    /// </summary>
    public void InvalidateModel(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        path = path.Replace('/', Path.DirectorySeparatorChar);
        bool had = _meshes.Remove(path, out Mesh? mesh);
        if (had) mesh?.Dispose();
        Console.WriteLine($"[mesh-build] invalidate {Path.GetFileName(path)} (was cached: {had})");
    }

    /// <summary>Texture paths actually bound by mesh batches last frame, tagged
    /// with the model that drew them ("Model.m2:TEXTURE.BLP"). Probe evidence.</summary>
    private readonly HashSet<string> _boundTexturesLastFrame = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> BoundTexturesLastFrame => _boundTexturesLastFrame;

    /// <summary>
    /// Hue-map a model's authored mesh COLOR tracks toward a target (0x00RRGGBB),
    /// or null to restore. Mesh submeshes are colored texture x color-track; the
    /// byte patcher only reaches particle colors, so an authored blue glow plane
    /// stays blue through every texture change without this. A per-texture tint
    /// overrides it for its batches.
    /// </summary>
    public void SetModelColorHue(string modelPath, uint? targetRgb)
    {
        if (string.IsNullOrEmpty(modelPath)) return;
        modelPath = modelPath.Replace('/', Path.DirectorySeparatorChar);
        if (targetRgb is uint want) _modelColorHues[modelPath] = want;
        else _modelColorHues.Remove(modelPath);
    }

    /// <summary>The texture path a mesh batch draws with (GetBatchTexture's
    /// lookup, path only), or null.</summary>
    private static string? BatchTexturePath(M2Model model, M2Batch batch)
    {
        if (batch.TextureIndex < 0 || batch.TextureIndex >= model.TextureLookup.Count) return null;
        int index = model.TextureLookup[batch.TextureIndex];
        if (index < 0 || index >= model.Textures.Count) return null;
        string path = model.Textures[index].Filename.Replace('/', Path.DirectorySeparatorChar);
        return path.Length == 0 ? null : path;
    }
    private Shader? _shader;
    private Shader? _groundShader;
    private Shader? _billboardShader;
    private uint _groundVao, _groundVbo;
    private uint _billboardVao, _billboardVbo;
    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];
    public int DrawnLastFrame { get; private set; }
    public bool Enabled { get; set; } = true;
    private readonly HashSet<string> _drawnPaths = new(StringComparer.OrdinalIgnoreCase);
    public bool WasDrawn(string path) => _drawnPaths.Contains(path);

    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = .35f;
    public Vector3 AmbientColor { get; set; } = Vector3.One;
    public float AmbientIntensity { get; set; } = 1f;
    public Vector3 FogColor { get; set; } = new(.56f, .71f, .85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float FarClip { get; set; } = 777f;
    public bool FogEnabled { get; set; } = true;

    public SpellEffectMeshRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _mpq = mpq;
    }

    public unsafe void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromSource(_gl, "spell-effect-mesh",
            SpellMeshSkinningLaw.VertexShaderSource, FragmentSource);
        _groundShader = Shader.FromSource(_gl, "spell-ground-decal", GroundVertexSource,
            GroundFragmentSource);
        _billboardShader = Shader.FromSource(_gl, "world-billboard", BillboardVertexSource,
            BillboardFragmentSource);
        _groundVao = _gl.GenVertexArray();
        _groundVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_groundVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
        const uint stride = 6 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride,
            (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);

        _billboardVao = _gl.GenVertexArray();
        _billboardVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_billboardVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _billboardVbo);
        const uint billboardStride = 5 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, billboardStride,
            (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, billboardStride,
            (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draw small authored UI cells as real world geometry. The pass depth-tests against terrain,
    /// WMOs and units, skips depth writes for blended edges, and uses a camera-facing unit basis.
    /// </summary>
    public unsafe void RenderWorldBillboards(Camera camera,
        IEnumerable<WorldBillboardDraw> instances)
    {
        if (!Enabled || _billboardShader is null || _billboardVao == 0) return;
        WorldBillboardDraw[] ready = instances
            .Where(x => x.WorldSize > 0f && x.Opacity > 0f && x.Texture.Length > 0)
            .ToArray();
        if (ready.Length == 0) return;

        Vector3 forward = camera.Forward;
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : camera.FlatRight;
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool hadBlend = _gl.IsEnabled(EnableCap.Blend);
        bool hadCull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        ApplyBlend(2);

        _billboardShader.Use();
        _billboardShader.Set("uViewProjection", camera.RelativeViewProjection);
        _billboardShader.Set("uView", camera.RelativeView);
        _billboardShader.Set("uTexture", 0);
        _billboardShader.Set("uFarClip", FarClip);
        _gl.BindVertexArray(_billboardVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _billboardVbo);

        foreach (WorldBillboardDraw draw in ready)
        {
            Texture? texture = ResolveTexture(draw.Texture);
            if (texture is null) continue;
            Vector3 bottom = draw.BottomCenter - camera.Position;
            float[] vertices = WorldBillboardLaw.Vertices(bottom, draw.WorldSize, right, up,
                draw.UvMin, draw.UvMax);
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            texture.Bind(0);
            _billboardShader.Set("uTint", draw.Tint);
            _billboardShader.Set("uOpacity", draw.Opacity);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        if (hadCull) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
        if (hadBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
        if (hadDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
    }

    public unsafe void Render(Camera camera,
        IEnumerable<SpellMeshDraw> instances,
        Func<float, float, float, float?>? sampleGround = null)
    {
        DrawnLastFrame = 0;
        _drawnPaths.Clear();
        _boundTexturesLastFrame.Clear();
        if (!Enabled || _shader is null) return;
        var ready = instances.Select(x => (Source: x, Mesh: Resolve(x.Path, x.Model)))
            .Where(x => x.Mesh is not null).ToArray();
        if (ready.Length == 0) return;

        _shader.Use();
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uView", camera.RelativeView);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uShadowWrap", 1f);
        _shader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFarClip", FarClip);
        _shader.Set("uTexture", 0);

        bool culling = true;
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool depthTest = hadDepthTest;
        var groundDraws = new List<(float[] Vertices, Texture? Texture, int Blend,
            Vector3 Tint, float Opacity, int FogPolicy)>();
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparent = pass == 1;
            if (transparent) { _gl.Enable(EnableCap.Blend); _gl.DepthMask(false); }

            foreach (var item in ready)
            {
                Mesh mesh = item.Mesh!;
                Matrix4x4 model = SpellMeshSkinningLaw.CameraRelativeModel(
                    item.Source.Transform, camera.Position);
                _shader.Set("uModel", model);
                _shader.Set("uModelViewProjection", model * camera.RelativeViewProjection);
                int boneCount = 0;
                if (mesh.Animator is not null && mesh.BoneCount > 0)
                {
                    M2Animator.Clip? clip = mesh.Animator.FindSequenceOrBake(
                        item.Source.SequenceIndex);
                    boneCount = Math.Min(mesh.BoneCount, M2Animator.MaxBones);
                    mesh.Animator.Evaluate(clip, item.Source.Age, item.Source.Age, _skin);
                    SpellMeshSkinningLaw.ApplyBillboardBones(item.Source.Model, item.Source.Transform,
                        camera.Position, camera.Forward, boneCount, _skin);
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                }
                _shader.Set("uBoneCount", boneCount);
                _gl.BindVertexArray(mesh.Vao);

                foreach (Batch batch in mesh.Batches)
                {
                    if (batch.Transparent != transparent) continue;
                    if (batch.TwoSided && culling) { _gl.Disable(EnableCap.CullFace); culling = false; }
                    else if (!batch.TwoSided && !culling) { _gl.Enable(EnableCap.CullFace); culling = true; }
                    Texture? drawTexture = batch.Texture ??
                        (item.Source.CustomTexture is { Length: > 0 } custom ? ResolveTexture(custom) : null);
                    if (drawTexture is { } texture)
                    {
                        texture.Bind(0); _shader.Set("uHasTexture", 1);
                        _shader.Set("uAlphaCutoff", batch.Blend == 1 ? 224f / 255f : 0f);
                    }
                    else { _shader.Set("uHasTexture", 0); _shader.Set("uAlphaCutoff", 0f); }
                    int sequence = item.Source.SequenceIndex;
                    Vector2 uvOffset = Vector2.Zero;
                    if (batch.UvTranslation is not null)
                    {
                        Vector3 offset = M2TrackSampling.Vector(batch.UvTranslation,
                            item.Source.Model, sequence, item.Source.Age, Vector3.Zero);
                        if (float.IsFinite(offset.X) && float.IsFinite(offset.Y))
                            uvOffset = new Vector2(offset.X, offset.Y);
                    }
                    _shader.Set("uUvOffset", uvOffset);
                    Vector3 tint = item.Source.Tint;
                    float opacity = item.Source.Opacity;
                    if (batch.Source.ColorIndex >= 0 && batch.Source.ColorIndex < item.Source.Model.Colors.Count)
                    {
                        M2ColorAnimation color = item.Source.Model.Colors[batch.Source.ColorIndex];
                        tint *= M2TrackSampling.Vector(color.Color, item.Source.Model, sequence,
                            item.Source.Age, Vector3.One);
                        opacity *= M2TrackSampling.Fixed16(color.Alpha, item.Source.Model, sequence,
                            item.Source.Age);
                    }
                    // Creator recolors must move the authored mesh color too: a
                    // batch whose texture is tinted follows that tint; otherwise
                    // the whole-model hue applies. Luminance is preserved.
                    {
                        uint? recolor = null;
                        if (BatchTexturePath(item.Source.Model, batch.Source) is { } batchTex &&
                            _textureTints.TryGetValue(batchTex, out uint texTarget))
                            recolor = texTarget;
                        else if (_modelColorHues.TryGetValue(item.Source.Path, out uint hueTarget))
                            recolor = hueTarget;
                        if (recolor is uint target)
                            tint = BlpRecolor.HueMapColor(tint, target);
                    }
                    if (batch.Source.TextureWeightIndex < item.Source.Model.TransparencyLookup.Count)
                    {
                        int track = item.Source.Model.TransparencyLookup[batch.Source.TextureWeightIndex];
                        if (track >= 0 && track < item.Source.Model.TransparencyTracks.Count)
                            opacity *= M2TrackSampling.Fixed16(item.Source.Model.TransparencyTracks[track],
                                item.Source.Model, sequence, item.Source.Age);
                    }
                    // TEMP diagnostic (Blizzard area-mesh hunt): sample what this batch resolves to.
                    if (Environment.GetEnvironmentVariable("MSUI_FX_TRACE") is not null &&
                        item.Source.Path.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) &&
                        Environment.TickCount64 % 1000 < 20)
                        Console.WriteLine($"[fx-trace] {Path.GetFileName(item.Source.Path)} " +
                            $"age={item.Source.Age:0.00} seq={sequence} " +
                            $"blend={batch.Blend} ground={batch.Ground is not null} " +
                            $"tint=({tint.X:0.00},{tint.Y:0.00},{tint.Z:0.00}) opacity={opacity:0.000}");
                    _shader.Set("uTint", tint);
                    _shader.Set("uOpacity", opacity);
                    _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                    _shader.Set("uFogPolicy", batch.FogPolicy);
                    if (item.Source.GroundAnchor && batch.Ground is { } ground)
                    {
                        // BuildGroundQuad needs the WORLD transform, not the camera-relative
                        // `model`: it samples terrain height in world space and subtracts the camera
                        // itself (once). Passing the already camera-relative `model` here double-
                        // subtracted the eye (~9000yd off) and fed world sampling garbage, which is
                        // why every ground-anchored ring (Frost Nova, Arcane Explosion) drew nothing.
                        float[] projected = BuildGroundQuad(ground, item.Source.Model,
                            item.Source.Transform, camera.Position, sampleGround);
                        if (projected.Length > 0)
                        {
                            groundDraws.Add((projected, drawTexture, batch.Blend, tint, opacity,
                                batch.FogPolicy));
                            _drawnPaths.Add(item.Source.Path);
                            _boundTexturesLastFrame.Add($"{Path.GetFileName(item.Source.Path)}:" +
                                $"{Path.GetFileName(batch.TexturePath ?? "<untextured>")} (ground)");
                        }
                        continue;
                    }
                    if (transparent) ApplyBlend(batch.Blend);
                    bool wantsDepthTest = !batch.NoZTest;
                    if (wantsDepthTest != depthTest)
                    {
                        if (wantsDepthTest) _gl.Enable(EnableCap.DepthTest);
                        else _gl.Disable(EnableCap.DepthTest);
                        depthTest = wantsDepthTest;
                    }
                    _gl.DepthMask(!transparent && !batch.NoZWrite);
                    _gl.DrawElements(PrimitiveType.Triangles, batch.Count,
                        DrawElementsType.UnsignedShort, (void*)(batch.Start * sizeof(ushort)));
                    _drawnPaths.Add(item.Source.Path);
                    _boundTexturesLastFrame.Add($"{Path.GetFileName(item.Source.Path)}:" +
                        $"{Path.GetFileName(batch.TexturePath ?? item.Source.CustomTexture ?? "<untextured>")}");
                }
                DrawnLastFrame++;
            }
            if (transparent) { _gl.Disable(EnableCap.Blend); _gl.DepthMask(true); }
        }
        if (!culling) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        if (!depthTest) { _gl.Enable(EnableCap.DepthTest); depthTest = true; }
        RenderGroundQuads(camera, groundDraws);
        if (!hadDepthTest) _gl.Disable(EnableCap.DepthTest);
    }

    private unsafe Mesh? Resolve(string path, M2Model model)
    {
        if (_meshes.TryGetValue(path, out Mesh? cached)) return cached;
        if (!model.IsValid) return _meshes[path] = null;
        float[] vertices = new float[model.Vertices.Count * FloatsPerVertex];
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            M2Vertex v = model.Vertices[i]; int o = i * FloatsPerVertex;
            vertices[o] = v.PosX; vertices[o + 1] = v.PosY; vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX; vertices[o + 4] = v.NormY; vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU; vertices[o + 7] = v.TexV;
            SpellMeshSkinningLaw.VertexSkin resolved = SpellMeshSkinningLaw.Resolve(v);
            vertices[o + 8] = resolved.Weights.X; vertices[o + 9] = resolved.Weights.Y;
            vertices[o + 10] = resolved.Weights.Z; vertices[o + 11] = resolved.Weights.W;
            vertices[o + 12] = resolved.Indices.X; vertices[o + 13] = resolved.Indices.Y;
            vertices[o + 14] = resolved.Indices.Z; vertices[o + 15] = resolved.Indices.W;
        }
        ushort[] indices = model.Indices.ToArray();
        var mesh = new Mesh(_gl) { Vao = _gl.GenVertexArray(), Vbo = _gl.GenBuffer(), Ebo = _gl.GenBuffer() };
        _gl.BindVertexArray(mesh.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = vertices) _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Ebo);
        fixed (ushort* p = indices) _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
            (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        _gl.BindVertexArray(0);

        mesh.Animator = M2Animator.Build(model, model.Sequences.Select(s => (int)s.AnimationId),
            includeStaticSequences: true);
        mesh.BoneCount = Math.Min(mesh.Animator?.BoneCount ?? 0, M2Animator.MaxBones);

        foreach (M2Batch source in model.Batches)
        {
            if (source.SubmeshIndex >= model.Submeshes.Count) continue;
            M2Submesh sub = model.Submeshes[source.SubmeshIndex];
            if (sub.IndexCount == 0 || sub.IndexStart + sub.IndexCount > indices.Length) continue;
            M2RenderFlag? flags = source.MaterialIndex < model.RenderFlags.Count ? model.RenderFlags[source.MaterialIndex] : null;
            int uvTransform = model.GetTextureTransformForBatch(source);
            M2AnimTrack<Vector3>? uvTranslation =
                uvTransform >= 0 ? model.TextureTransforms[uvTransform].Translation : null;
            if (uvTranslation is not null && uvTranslation.Keys.Count == 0) uvTranslation = null;
            mesh.Batches.Add(new Batch
            {
                Start = sub.IndexStart, Count = sub.IndexCount,
                Texture = ResolveBatchTexture(model, source),
                TexturePath = BatchTexturePath(model, source),
                UvTranslation = uvTranslation,
                Blend = flags?.BlendingMode ?? 0,
                TwoSided = flags?.TwoSided ?? true,
                NoZWrite = flags?.NoZWrite ?? false,
                NoZTest = flags?.NoZTest ?? false,
                Unlit = flags?.Unlit ?? false,
                FogPolicy = flags?.Unfogged == true ? 4 : FogPolicyForBlend(flags?.BlendingMode ?? 0),
                Source = source,
                Ground = FindGroundQuad(model, sub),
                AnimatedAlpha = HasAnimatedAlpha(model, source),
            });
        }
        // Safety net for a file with NO batch table: draw the whole index buffer untextured so a
        // broken model is at least visible. It must not fire for a file whose batches all sit on
        // zero-triangle submeshes - that is exactly what the creator's "hide every layer" law
        // produces (M2MeshParser.HideSubmesh), and it drew Cleave's raw planes as flat colour
        // the moment the last crescent was switched off (owner, 2026-09-10). Authored that way,
        // the real client draws nothing; so do we.
        if (mesh.Batches.Count == 0 && model.Batches.Count == 0)
            mesh.Batches.Add(new Batch { Count = (uint)indices.Length, TwoSided = true,
                Source = new M2Batch() });
        else if (mesh.Batches.Count == 0)
            Console.WriteLine($"[mesh-build] {Path.GetFileName(path)}: every batch is on an empty submesh - nothing to draw");
        Console.WriteLine($"[mesh-build] {Path.GetFileName(path)}: " +
            $"tex=[{string.Join(", ", mesh.Batches.Select(b => Path.GetFileName(b.TexturePath ?? "<none>")).Distinct())}]");
        return _meshes[path] = mesh;
    }

    private static int FogPolicyForBlend(int blend) => blend switch
    {
        3 or 4 => 1, // additive -> black
        5 => 2,      // modulate -> white
        6 => 3,      // modulate-2x -> neutral grey
        _ => 0,      // ordinary scene fog
    };

    private Texture? ResolveBatchTexture(M2Model model, M2Batch batch)
    {
        if (batch.TextureIndex >= model.TextureLookup.Count) return null;
        int index = model.TextureLookup[batch.TextureIndex];
        if (index < 0 || index >= model.Textures.Count) return null;
        string path = model.Textures[index].Filename.Replace('/', '\\');
        if (path.Length == 0) return null;
        // M2 texture flags 1 / 2 = wrap U / V. Every texture used to be uploaded CLAMPED, so a
        // scrolling energy texture slid off its geometry and left a smear of edge pixels
        // (SPELL_CREATOR_PLAN.md P3.6 found it; it applies to Blizzard's own spells as much).
        return ResolveTexture(path, repeat: (model.Textures[index].Flags & 0x3) != 0);
    }

    /// <summary>
    /// Palette-swap a texture toward a target color (0x00RRGGBB), or null to
    /// restore the authored pixels. Mirrors SpellParticleSystem.SetTextureTint
    /// for the mesh-drawn side of an effect (e.g. Blizzard's looping centre).
    /// Meshes cache their batch textures, so the mesh cache is dropped too and
    /// rebuilds lazily - cheap at creator-mode scale.
    /// </summary>
    public void SetTextureTint(string path, uint? targetRgb)
    {
        if (string.IsNullOrEmpty(path)) return;
        path = path.Replace('/', Path.DirectorySeparatorChar);
        bool had = _textureTints.TryGetValue(path, out uint current);
        if (targetRgb is uint want)
        {
            if (had && current == want) return;
            _textureTints[path] = want;
        }
        else
        {
            if (!had) return;
            _textureTints.Remove(path);
        }
        if (_textures.Remove(path, out Texture? old)) old?.Dispose();
        if (_textures.Remove(path + WrapKeySuffix, out Texture? oldWrapped)) oldWrapped?.Dispose();
        foreach (Mesh? mesh in _meshes.Values) mesh?.Dispose();
        _meshes.Clear();
    }

    /// <summary>The cache holds one upload per (path, wrap): the same image can be clamped on one
    /// model and wrapped on another.</summary>
    private const string WrapKeySuffix = "|wrap";

    private Texture? ResolveTexture(string path, bool repeat = false)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar);
        string key = repeat ? path + WrapKeySuffix : path;
        if (_textures.TryGetValue(key, out Texture? cached)) return cached;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null)
        {
            // Once per path: the null is cached above, so this cannot spam. Unlike GameplayArt
            // this path does no ".blp" completion, and a silent null here is indistinguishable
            // from "nothing wanted to draw" — which is how the raid-marker atlas went missing
            // without a single line of evidence.
            Console.WriteLine($"[fx-load] mesh-tex MISSING {path}");
            return _textures[key] = null;
        }
        try
        {
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            if (_textureTints.TryGetValue(path, out uint target))
                BlpRecolor.HueMapBgra(pixels, target);
            Texture texture = Texture.From2D(_gl, pixels, width, height, mipmaps: true, repeat: repeat);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            if (ms > 2) Console.WriteLine($"[fx-load] mesh-tex {Path.GetFileName(path)} {ms:0.0}ms");
            return _textures[key] = texture;
        }
        catch { return _textures[key] = null; }
    }

    private static GroundQuad? FindGroundQuad(M2Model model, M2Submesh sub)
    {
        var unique = new HashSet<ushort>();
        int end = Math.Min(model.Indices.Count, sub.IndexStart + sub.IndexCount);
        for (int i = sub.IndexStart; i < end; i++) unique.Add(model.Indices[i]);
        if (unique.Count != 4) return null;
        int bone = -1;
        var vertices = new List<M2Vertex>(4);
        foreach (ushort index in unique)
        {
            if (index >= model.Vertices.Count) return null;
            M2Vertex v = model.Vertices[index];
            // Original M2 z=0 is Y=0 after the reader's coordinate conversion.
            if (MathF.Abs(v.PosY) > .01f || v.BoneWeight0 < 254 ||
                v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3 != 0) return null;
            if (bone < 0) bone = v.BoneIndex0;
            else if (bone != v.BoneIndex0) return null;
            vertices.Add(v);
        }
        M2Vertex[] ordered = vertices.OrderBy(v => v.PosZ).ThenBy(v => v.PosX).ToArray();
        // Triangle-strip order: minZ/minX, minZ/maxX, maxZ/minX, maxZ/maxX.
        return new GroundQuad { Bone = bone, Corners = ordered };
    }

    private float[] BuildGroundQuad(GroundQuad quad, M2Model source, Matrix4x4 model,
        Vector3 camera, Func<float, float, float, float?>? sampleGround)
    {
        Matrix4x4 joint = quad.Bone >= 0 && quad.Bone < source.Bones.Count
            ? _skin[quad.Bone]
            : Matrix4x4.Identity;
        Matrix4x4 world = joint * model;

        // Corner order from FindGroundQuad: 0=BL(minZ,minX) 1=BR(minZ,maxX) 2=TL(maxZ,minX)
        // 3=TR(maxZ,maxX). The ring is the texture painted across this quad; interpolate positions
        // and UVs bilinearly across the four world-space corners.
        Span<Vector3> cp = stackalloc Vector3[4];
        Span<Vector2> cuv = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            M2Vertex c = quad.Corners[i];
            cp[i] = Vector3.Transform(new Vector3(c.PosX, c.PosY, c.PosZ), world);
            cuv[i] = new Vector2(c.TexU, c.TexV);
        }

        // Handedness normalization (benilla ground_fx.rs:70-83): keep the frame's +y'
        // axis matched to the quad's authored +y edge so the UV bilerp never flips.
        {
            Vector3 ex = cp[1] - cp[0] + cp[3] - cp[2];
            Vector3 ey = cp[2] - cp[0] + cp[3] - cp[1];
            if (ex.X * ey.Y - ex.Y * ey.X < 0)
            {
                (cp[0], cp[2]) = (cp[2], cp[0]); (cp[1], cp[3]) = (cp[3], cp[1]);
                (cuv[0], cuv[2]) = (cuv[2], cuv[0]); (cuv[1], cuv[3]) = (cuv[3], cuv[1]);
            }
        }

        // True projector first: re-emit the terrain triangles under the posed quad.
        if (FitFrame(cp) is { } frame && ProjectDecal(frame, cuv, camera) is { } projected)
            return projected;

        // The reference hides projected ground effects when no projectable terrain exists.
        return [];
    }

    // â”€â”€ Ground-decal projector (benilla decal.rs/ground_fx.rs, trace Â§9) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //
    // The decal is never drawn as its own quad: the REAL terrain triangles inside a
    // fitted frame box are re-emitted with the quad's UVs bilerped across the frame and
    // a vertical-fade alpha. The emitted geometry is exactly coplanar with the drawn
    // ground, so with a strong depth bias the marker/ring can never clip into a slope,
    // and the pattern is never split by terrain curvature.

    /// <summary>Yaw-rotated horizontal rectangle Ã— a vertical slab, centred on Center.</summary>
    private readonly record struct DecalFrame(Vector3 Center, float Sin, float Cos,
        float HalfX, float HalfY, float Vert)
    {
        public (float X, float Y) InFrame(in Vector3 p)
        {
            float dx = p.X - Center.X, dy = p.Y - Center.Y;
            return (dx * Cos - dy * Sin, dy * Cos + dx * Sin);
        }
    }

    /// <summary>Ground triangles inside a world-space projection box, supplied by
    /// the host from both ADT terrain and walkable WMO/doodad collision.</summary>
    public Action<float, float, float, float, float, float,
        List<(Vector3 A, Vector3 B, Vector3 C)>>? GatherGround;

    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _gatherScratch = new(256);
    private List<Vector3> _clipFront = new(16);
    private List<Vector3> _clipBack = new(16);

    /// <summary>Fit the projection frame to 4 posed corners (benilla fit_frame). Null for a
    /// degenerate pose (scale-0 first frame, edge-on tilt).</summary>
    private static DecalFrame? FitFrame(ReadOnlySpan<Vector3> c)
    {
        Vector3 center = (c[0] + c[1] + c[2] + c[3]) * .25f;
        Vector3 ex = (c[1] - c[0] + c[3] - c[2]) * .5f;
        Vector3 ey = (c[2] - c[0] + c[3] - c[1]) * .5f;
        var exh = new Vector2(ex.X, ex.Y);
        float halfX = exh.Length() * .5f;
        float halfY = new Vector2(ey.X, ey.Y).Length() * .5f;
        if (halfX < 1e-3f || halfY < 1e-3f) return null;
        Vector2 d = exh / (halfX * 2f);
        return new DecalFrame(center, -d.Y, d.X, halfX, halfY, 2f * MathF.Max(halfX, halfY));
    }

    private static float PlaneDistance(in DecalFrame f, int plane, in Vector3 p)
    {
        (float x, float y) = f.InFrame(p);
        float z = p.Z - f.Center.Z;
        return plane switch
        {
            0 => x + f.HalfX, 1 => f.HalfX - x,
            2 => y + f.HalfY, 3 => f.HalfY - y,
            4 => z + f.Vert, _ => f.Vert - z,
        };
    }

    /// <summary>Sutherlandâ€“Hodgman clip of one triangle against the frame's 6 half-planes.
    /// Returns the surviving convex polygon (shared scratch list) or null.</summary>
    private List<Vector3>? ClipToFrame(in DecalFrame f, Vector3 a, Vector3 b, Vector3 c)
    {
        _clipFront.Clear(); _clipFront.Add(a); _clipFront.Add(b); _clipFront.Add(c);
        for (int plane = 0; plane < 6 && _clipFront.Count >= 3; plane++)
        {
            _clipBack.Clear();
            int n = _clipFront.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 p0 = _clipFront[i], p1 = _clipFront[(i + 1) % n];
                float d0 = PlaneDistance(f, plane, p0), d1 = PlaneDistance(f, plane, p1);
                if (d0 >= 0) _clipBack.Add(p0);
                if (d0 >= 0 != d1 >= 0) _clipBack.Add(Vector3.Lerp(p0, p1, d0 / (d0 - d1)));
            }
            (_clipFront, _clipBack) = (_clipBack, _clipFront);
        }
        return _clipFront.Count >= 3 ? _clipFront : null;
    }

    /// <summary>
    /// Project the frame onto the terrain: gather real ground triangles under it, clip
    /// each to the frame, and emit camera-relative vertices with bilerped corner UVs
    /// and the vertical-fade alpha trapezoid. Null when no ground is in the slab (the
    /// mid-air gate) or no gatherer is wired.
    /// </summary>
    private float[]? ProjectDecal(in DecalFrame frame, ReadOnlySpan<Vector2> uv, Vector3 camera)
    {
        if (GatherGround is null) return null;
        _gatherScratch.Clear();
        float ext = MathF.Sqrt(frame.HalfX * frame.HalfX + frame.HalfY * frame.HalfY);
        GatherGround(frame.Center.X - ext, frame.Center.Y - ext, frame.Center.Z - frame.Vert,
            frame.Center.X + ext, frame.Center.Y + ext, frame.Center.Z + frame.Vert,
            _gatherScratch);
        if (_gatherScratch.Count == 0) return null;
        var vertices = new List<float>(512);
        foreach ((Vector3 a, Vector3 b, Vector3 c) in _gatherScratch)
        {
            if (ClipToFrame(frame, a, b, c) is not { } poly) continue;
            for (int k = 1; k < poly.Count - 1; k++)
            {
                EmitDecalVert(vertices, frame, uv, poly[0], camera);
                EmitDecalVert(vertices, frame, uv, poly[k], camera);
                EmitDecalVert(vertices, frame, uv, poly[k + 1], camera);
            }
        }
        return vertices.Count >= 18 ? vertices.ToArray() : null;
    }

    private static void EmitDecalVert(List<float> dst, in DecalFrame f,
        ReadOnlySpan<Vector2> uv, Vector3 p, Vector3 camera)
    {
        (float x, float y) = f.InFrame(p);
        float s = (x + f.HalfX) / (2f * f.HalfX);
        float t = (y + f.HalfY) / (2f * f.HalfY);
        Vector2 u = Vector2.Lerp(Vector2.Lerp(uv[0], uv[1], s), Vector2.Lerp(uv[2], uv[3], s), t);
        float dz = MathF.Abs(p.Z - f.Center.Z);
        float alpha = Math.Clamp((f.Vert - dz) / (.75f * f.Vert), 0f, 1f);
        dst.Add(p.X - camera.X); dst.Add(p.Y - camera.Y); dst.Add(p.Z - camera.Z);
        dst.Add(u.X); dst.Add(u.Y); dst.Add(alpha);
    }

    /// <summary>
    /// The 1.12 area-targeting reticle: the acceptable/unacceptable texture projected onto
    /// ground triangles in a fixed two-yard slab, sized by the caller's range verdict.
    /// preserving the texture's authored green and alpha. No ground under the cursor â†’ no marker
    /// (the 1.12 mid-air gate).
    /// </summary>
    public void RenderTargetingMarker(Camera camera, Vector3 centre, float radius, bool acceptable)
    {
        if (_groundShader is null) return;
        Texture? rune = ResolveTexture(acceptable
            ? @"Interface\SpellShadow\Spell-Shadow-Acceptable.blp"
            : @"Interface\SpellShadow\Spell-Shadow-Unacceptable.blp");
        if (rune is null) return;
        var frame = new DecalFrame(centre, 0f, 1f, radius, radius, GroundTargetingLaw.ProjectionHalfHeight);
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        float[]? vertices = ProjectDecal(frame, uv, camera.Position);
        if (vertices is null) return;
        // GxRs blend-mode 2: the texture's authored alpha preserves the fine rings and glyphs.
        // Additive blending blooms those details into the broad neon arcs seen in the old marker.
        RenderGroundQuads(camera, [(vertices, rune, 2, Vector3.One, 1f, 0)]);
    }

    /// <summary>
    /// The stock 1.12 selected-unit ring. UnitSelectTexture's authored fade rotates with the
    /// camera, while the square itself is projected onto the accepted ADT/WMO/doodad ground
    /// triangles. No receiving surface means no ring, matching the reference's mid-air gate.
    /// </summary>
    public bool RenderUnitSelectionRing(Camera camera, Vector3 centre, float radius, Vector3 tint)
    {
        if (_groundShader is null || radius <= 0f) return false;
        Texture? ring = ResolveTexture(@"Textures\UnitSelectTexture.blp");
        if (ring is null) return false;
        (float sin, float cos) = SelectionRingLaw.ProjectorRotation(camera.ViewYaw);
        var frame = new DecalFrame(centre, sin, cos, radius, radius, 2f * radius);
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        float[]? vertices = ProjectDecal(frame, uv, camera.Position);
        if (vertices is null) return false;
        RenderGroundQuads(camera, [(vertices, ring, 3, tint, 1f, 0)], UnitAwareDepthBias);
        return true;
    }

    // ── RTS ground FX (CRPG free view): selection rings + move markers ────────────────────────
    // Same decal machinery as the targeting rune: projected onto real terrain triangles and
    // depth-tested against the already-drawn units, so a ring's far arc disappears behind the
    // character standing on it instead of floating over the model.

    private Texture? _rtsRingTexture;
    private Texture? _rtsChevronTexture;

    public readonly record struct UnitRing(Vector3 Centre, float Radius, Vector3 Tint, float Opacity);
    public readonly record struct MoveMarker(Vector3 Centre, float Age, Vector3 Tint);

    /// <summary>Crisp anti-aliased band + faint inner fill + soft outer glow. White; tinted per draw.</summary>
    private Texture RtsRingTexture()
    {
        if (_rtsRingTexture is not null) return _rtsRingTexture;
        const int size = 256;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) / size * 2f - 1f;
            float dy = (y + 0.5f) / size * 2f - 1f;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float band = SmoothStep(0.78f, 0.83f, d) * (1f - SmoothStep(0.90f, 0.95f, d));
            float inner = d < 0.78f ? 0.08f * (0.25f + 0.75f * d / 0.78f) : 0f;
            float glow = d > 0.95f ? MathF.Max(0f, 1f - (d - 0.95f) / 0.05f) * 0.30f : 0f;
            float a = MathF.Min(1f, band + inner + glow);
            int i = (y * size + x) * 4;
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(a * 255f + 0.5f);
        }
        _rtsRingTexture = Texture.FromRgbaNoMips(_gl, rgba, size, size);
        return _rtsRingTexture;
    }

    /// <summary>A chevron (arrowhead) pointing +Y in texture space; frame rotation aims it.</summary>
    private Texture RtsChevronTexture()
    {
        if (_rtsChevronTexture is not null) return _rtsChevronTexture;
        const int size = 128;
        byte[] rgba = new byte[size * size * 4];
        Vector2 apex = new(0f, 0.55f);
        Vector2 left = new(-0.55f, -0.35f);
        Vector2 right = new(0.55f, -0.35f);
        const float thickness = 0.17f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Texture V grows downward; flip so +Y in frame space points "up" the texture.
            Vector2 p = new((x + 0.5f) / size * 2f - 1f, 1f - (y + 0.5f) / size * 2f);
            float d = MathF.Min(DistanceToSegment(p, left, apex), DistanceToSegment(p, right, apex));
            float a = 1f - SmoothStep(thickness * 0.55f, thickness, d);
            int i = (y * size + x) * 4;
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(a * 255f + 0.5f);
        }
        _rtsChevronTexture = Texture.FromRgbaNoMips(_gl, rgba, size, size);
        return _rtsChevronTexture;
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / MathF.Max(1e-5f, b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public void RenderSelectionRings(Camera camera, IReadOnlyList<UnitRing> rings)
    {
        if (_groundShader is null || rings.Count == 0) return;
        Texture ring = RtsRingTexture();
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        List<(float[], Texture?, int, Vector3, float, int)> draws = [];
        foreach (UnitRing r in rings)
        {
            var frame = new DecalFrame(r.Centre, 0f, 1f, r.Radius, r.Radius, 2f * r.Radius);
            // WMO floors (city streets, interiors) have no terrain triangles to
            // project onto — fall back to a flat disc at feet-Z, like the dev
            // discs, or the selection halo silently vanishes in Stormwind.
            float[]? vertices = ProjectDecal(frame, uv, camera.Position) ??
                                FlatDiscVertices(r.Centre, r.Radius, camera.Position);
            if (vertices is not null)
                draws.Add((vertices, ring, 2, r.Tint, r.Opacity, 0));
        }
        // The one ground FX that a unit is standing IN: the far arc belongs behind the model,
        // the way the reference client's selection circle does.
        RenderGroundQuads(camera, draws, UnitAwareDepthBias);
    }

    /// <summary>
    /// Classic RTS move confirm: a ring pulse collapsing onto the click point while three
    /// chevrons slide inward and the whole thing fades. Callers drop markers older than ~0.9 s.
    /// </summary>
    public void RenderMoveMarkers(Camera camera, IReadOnlyList<MoveMarker> markers)
    {
        if (_groundShader is null || markers.Count == 0) return;
        Texture ring = RtsRingTexture();
        Texture chevron = RtsChevronTexture();
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        List<(float[], Texture?, int, Vector3, float, int)> draws = [];
        foreach (MoveMarker m in markers)
        {
            float t = Math.Clamp(m.Age / 0.9f, 0f, 1f);
            float ease = 1f - (1f - t) * (1f - t);            // fast in, settle out
            float fade = 1f - SmoothStep(0.55f, 1f, t);
            const float baseRadius = 0.85f;

            float ringRadius = baseRadius * (1.45f - 0.55f * ease);
            var ringFrame = new DecalFrame(m.Centre, 0f, 1f, ringRadius, ringRadius, 2f * ringRadius);
            // Same WMO-floor fallback as the selection rings: move confirms must
            // read on city streets and interiors too.
            float[]? ringVertices = ProjectDecal(ringFrame, uv, camera.Position) ??
                                    FlatDiscVertices(m.Centre, ringRadius, camera.Position);
            if (ringVertices is not null)
                draws.Add((ringVertices, ring, 2, m.Tint, fade, 0));

            float slide = baseRadius * (1.7f - 1.05f * ease);
            const float chevronHalf = 0.42f;
            for (int k = 0; k < 3; k++)
            {
                float angle = k * (MathF.PI * 2f / 3f) + 0.5f;
                (float sin, float cos) = MathF.SinCos(angle);
                // Chevron sits out at `slide` along the angle and points back at the centre:
                // +Y in frame space maps to (-sin, -cos)·slide direction via the frame rotation.
                Vector3 at = m.Centre + new Vector3(sin * slide, cos * slide, 0f);
                // Frame rotation of (angle + pi) maps the inward world direction onto frame +Y,
                // which is where the chevron texture points.
                var frame = new DecalFrame(at, -sin, -cos, chevronHalf, chevronHalf, 2f * chevronHalf);
                // Directional decal: the WMO-floor fallback must keep the frame's
                // rotation and UVs (a disc fan cannot), so it gets the flat QUAD.
                float[] chevronVertices = ProjectDecal(frame, uv, camera.Position) ??
                                          FlatFrameVertices(frame, uv, camera.Position);
                draws.Add((chevronVertices, chevron, 2, m.Tint, fade, 0));
            }
        }
        RenderGroundQuads(camera, draws);
    }

    // ── Dev-window ground shapes: terrain-conforming discs, sectors and strips ───────────────
    // All three shapes use the same projector as the RTS rings. Their white mask textures are
    // clipped onto the REAL floor triangles, then tinted per draw. This is especially important
    // for encounter cones: a screen-space polygon at the caster's Z looks like an upright sheet
    // whenever the camera is low, while this pass actually hugs the lair floor.

    /// <summary>A tinted, filled ground annulus. InnerRadius 0 = a full disc.</summary>
    public readonly record struct GroundDisc(
        Vector3 Centre, float InnerRadius, float OuterRadius, Vector3 Tint, float Opacity);

    /// <summary>A filled directional sector. Facing is world radians; Degrees is the full arc.</summary>
    public readonly record struct GroundSector(
        Vector3 Origin, float Radius, float Facing, float Degrees, Vector3 Tint, float Opacity);

    /// <summary>A filled rectangular lane between two world points.</summary>
    public readonly record struct GroundStrip(
        Vector3 Start, Vector3 End, float Width, Vector3 Tint, float Opacity);

    private readonly Dictionary<int, Texture> _devDiscTextures = new();
    private readonly Dictionary<int, Texture> _devSectorTextures = new();
    private Texture? _devStripTexture;

    /// <summary>Filled annulus: soft outer edge, brighter defined rim, translucent body.
    /// White; tinted per draw. Keyed by the inner fraction quantized to 1/32 steps.</summary>
    private Texture DevDiscTexture(float innerFraction)
    {
        int key = Math.Clamp((int)MathF.Round(innerFraction * 32f), 0, 31);
        if (_devDiscTextures.TryGetValue(key, out Texture? cached)) return cached;
        float inner = key / 32f;
        const int size = 256;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) / size * 2f - 1f;
            float dy = (y + 0.5f) / size * 2f - 1f;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            // Body between the inner cut and the outer edge, both softened ~2px.
            float fill = SmoothStep(inner - 0.015f, inner + 0.015f, d)
                         * (1f - SmoothStep(0.965f, 0.995f, d));
            // A defined rim just inside the outer edge so bands separate at a glance.
            float rim = SmoothStep(0.915f, 0.945f, d) * (1f - SmoothStep(0.955f, 0.985f, d));
            float a = MathF.Min(1f, fill * 0.72f + rim * 0.28f);
            int i = (y * size + x) * 4;
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var texture = Texture.FromRgbaNoMips(_gl, rgba, size, size);
        _devDiscTextures[key] = texture;
        return texture;
    }

    /// <summary>A +X-facing sector mask. Arc widths are cached at whole-degree precision;
    /// encounter data is itself degree-valued and this avoids manufacturing a texture per frame.</summary>
    private Texture DevSectorTexture(float degrees)
    {
        int key = Math.Clamp((int)MathF.Round(MathF.Abs(degrees)), 1, 360);
        if (_devSectorTextures.TryGetValue(key, out Texture? cached)) return cached;
        float half = key * .5f * (MathF.PI / 180f);
        const int size = 256;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float fx = (x + .5f) / size * 2f - 1f;
            // Texture rows grow down. The sign is immaterial to a symmetric sector, but this
            // convention keeps +Y in texture space aligned with +Y in DecalFrame.
            float fy = 1f - (y + .5f) / size * 2f;
            float radius = MathF.Sqrt(fx * fx + fy * fy);
            float angle = MathF.Abs(MathF.Atan2(fy, fx));
            float angularFill = key >= 360
                ? 1f
                : 1f - SmoothStep(half - .018f, half + .018f, angle);
            float radialFill = 1f - SmoothStep(.965f, .995f, radius);
            float fill = angularFill * radialFill;

            // A defined outer arc plus both radial edges makes overlapping boss mechanics
            // separable without turning the whole floor into an opaque block.
            float arcRim = SmoothStep(.91f, .945f, radius)
                           * (1f - SmoothStep(.955f, .99f, radius)) * angularFill;
            float edgeDistance = radius * MathF.Abs(MathF.Sin(angle - half));
            float sideRim = key < 360 && MathF.Abs(angle - half) < .12f
                ? (1f - SmoothStep(.012f, .035f, edgeDistance))
                  * SmoothStep(.04f, .12f, radius) * radialFill
                : 0f;
            float a = MathF.Min(1f, fill * .55f + MathF.Max(arcRim, sideRim) * .45f);
            int i = (y * size + x) * 4;
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(a * 255f + .5f);
        }
        var texture = Texture.FromRgbaNoMips(_gl, rgba, size, size);
        _devSectorTextures[key] = texture;
        return texture;
    }

    /// <summary>A softly edged filled rectangle with a crisp inner rim.</summary>
    private Texture DevStripTexture()
    {
        if (_devStripTexture is not null) return _devStripTexture;
        const int size = 128;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float ax = MathF.Abs((x + .5f) / size * 2f - 1f);
            float ay = MathF.Abs((y + .5f) / size * 2f - 1f);
            float edge = MathF.Max(ax, ay);
            float fill = 1f - SmoothStep(.965f, .995f, edge);
            float rim = SmoothStep(.89f, .94f, edge) * (1f - SmoothStep(.955f, .99f, edge));
            float a = MathF.Min(1f, fill * .55f + rim * .45f);
            int i = (y * size + x) * 4;
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(a * 255f + .5f);
        }
        return _devStripTexture = Texture.FromRgbaNoMips(_gl, rgba, size, size);
    }

    /// <summary>
    /// Dev-window aggro/leash discs: projected onto ADT terrain and walkable WMO collision,
    /// depth-tested with the unit-aware bias so bodies standing in a disc occlude its far
    /// half. The flat fallback remains only where neither source has a surface.
    /// </summary>
    public void RenderGroundDiscs(Camera camera, IReadOnlyList<GroundDisc> discs)
    {
        if (_groundShader is null || discs.Count == 0) return;
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        List<(float[], Texture?, int, Vector3, float, int)> draws = [];
        foreach (GroundDisc disc in discs)
        {
            if (disc.OuterRadius < 0.1f) continue;
            Texture texture = DevDiscTexture(
                Math.Clamp(disc.InnerRadius / disc.OuterRadius, 0f, 0.97f));
            var frame = new DecalFrame(disc.Centre, 0f, 1f,
                disc.OuterRadius, disc.OuterRadius, 2f * disc.OuterRadius);
            float[]? vertices = ProjectDecal(frame, uv, camera.Position) ??
                                FlatDiscVertices(disc.Centre, disc.OuterRadius, camera.Position);
            if (vertices is not null)
                draws.Add((vertices, texture, 2, disc.Tint, disc.Opacity, 0));
        }
        RenderGroundQuads(camera, draws, UnitAwareDepthBias);
    }

    /// <summary>Encounter cone previews projected onto terrain and walkable WMO floors.</summary>
    public void RenderGroundSectors(Camera camera, IReadOnlyList<GroundSector> sectors)
    {
        if (_groundShader is null || sectors.Count == 0) return;
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        List<(float[], Texture?, int, Vector3, float, int)> draws = [];
        foreach (GroundSector sector in sectors)
        {
            if (sector.Radius < .1f || MathF.Abs(sector.Degrees) < .1f) continue;
            (float sin, float cos) = MathF.SinCos(sector.Facing);
            // DecalFrame's frame +X maps to world (Cos,-Sin), hence the negated sine.
            var frame = new DecalFrame(sector.Origin, -sin, cos,
                sector.Radius, sector.Radius, 2f * sector.Radius);
            float[] vertices = ProjectDecal(frame, uv, camera.Position) ??
                               FlatFrameVertices(frame, uv, camera.Position);
            draws.Add((vertices, DevSectorTexture(sector.Degrees), 2,
                sector.Tint, sector.Opacity, 0));
        }
        RenderGroundQuads(camera, draws, UnitAwareDepthBias);
    }

    /// <summary>Encounter line/lane previews projected onto terrain and walkable WMO floors.</summary>
    public void RenderGroundStrips(Camera camera, IReadOnlyList<GroundStrip> strips)
    {
        if (_groundShader is null || strips.Count == 0) return;
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        List<(float[], Texture?, int, Vector3, float, int)> draws = [];
        Texture texture = DevStripTexture();
        foreach (GroundStrip strip in strips)
        {
            Vector3 delta = strip.End - strip.Start;
            float length = new Vector2(delta.X, delta.Y).Length();
            if (length < .1f || strip.Width < .1f) continue;
            float facing = MathF.Atan2(delta.Y, delta.X);
            (float sin, float cos) = MathF.SinCos(facing);
            Vector3 centre = (strip.Start + strip.End) * .5f;
            var frame = new DecalFrame(centre, -sin, cos, length * .5f, strip.Width * .5f,
                MathF.Max(length, strip.Width));
            float[] vertices = ProjectDecal(frame, uv, camera.Position) ??
                               FlatFrameVertices(frame, uv, camera.Position);
            draws.Add((vertices, texture, 2, strip.Tint, strip.Opacity, 0));
        }
        RenderGroundQuads(camera, draws, UnitAwareDepthBias);
    }

    /// <summary>The WMO-floor fallback: a flat fan at the centre's Z, camera-relative,
    /// in the ground-quad vertex layout (pos³ + uv² + alpha).</summary>
    private static float[] FlatDiscVertices(Vector3 centre, float radius, Vector3 camera)
    {
        const int segments = 24;
        var dst = new List<float>(segments * 18);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.PI * 2f / segments;
            float a1 = (i + 1) * MathF.PI * 2f / segments;
            EmitFlat(dst, centre, radius, camera, 0f, 0f);
            EmitFlat(dst, centre, radius, camera, MathF.Cos(a0), MathF.Sin(a0));
            EmitFlat(dst, centre, radius, camera, MathF.Cos(a1), MathF.Sin(a1));
        }
        return dst.ToArray();

        // (nx, ny) is the unit offset from the centre; uv maps the disc onto the texture.
        static void EmitFlat(List<float> dst, Vector3 centre, float radius, Vector3 camera,
            float nx, float ny)
        {
            dst.Add(centre.X + nx * radius - camera.X);
            dst.Add(centre.Y + ny * radius - camera.Y);
            dst.Add(centre.Z - camera.Z);
            dst.Add(0.5f + 0.5f * nx);
            dst.Add(0.5f + 0.5f * ny);
            dst.Add(1f);
        }
    }

    /// <summary>The WMO-floor fallback for DIRECTIONAL decals (chevrons): the frame's
    /// rotated rectangle as two flat triangles at the centre's Z, keeping the caller's
    /// corner UVs — a radial disc fan would erase the rotation the texture points along.</summary>
    private static float[] FlatFrameVertices(in DecalFrame frame, ReadOnlySpan<Vector2> uv,
        Vector3 camera)
    {
        ReadOnlySpan<float> xs = [-frame.HalfX, frame.HalfX, -frame.HalfX, frame.HalfX];
        ReadOnlySpan<float> ys = [-frame.HalfY, -frame.HalfY, frame.HalfY, frame.HalfY];
        ReadOnlySpan<int> order = [0, 1, 2, 1, 3, 2];   // CCW from +Z, matching the fan
        var dst = new List<float>(order.Length * 6);
        foreach (int i in order)
        {
            // Inverse of DecalFrame.InFrame: world offset for a frame-space corner.
            float dx = xs[i] * frame.Cos + ys[i] * frame.Sin;
            float dy = ys[i] * frame.Cos - xs[i] * frame.Sin;
            dst.Add(frame.Center.X + dx - camera.X);
            dst.Add(frame.Center.Y + dy - camera.Y);
            dst.Add(frame.Center.Z - camera.Z);
            dst.Add(uv[i].X);
            dst.Add(uv[i].Y);
            dst.Add(1f);
        }
        return dst.ToArray();
    }

    /// <summary>
    /// benilla GROUND_FX_DEPTH_BIAS. Enormous on purpose: a decal projected exactly onto the
    /// terrain triangles is coplanar with them, and this guarantees it wins that fight at any
    /// range. The cost is that it also beats anything else standing nearby, which is why
    /// callers that want to be OCCLUDED by units ask for <see cref="UnitAwareDepthBias"/>.
    /// </summary>
    private const float GroundFxDepthBias = -8192f;

    /// <summary>
    /// Enough to clear the terrain the decal sits on, small enough that a body standing in it
    /// still wins. At RTS camera range the coarse bias pulls a decal several yards toward the
    /// eye — that is what made the far arc of a selection ring draw straight through the model
    /// it was drawn under. The slope term (factor -1) carries the grazing-angle case.
    /// </summary>
    private const float UnitAwareDepthBias = -64f;

    private unsafe void RenderGroundQuads(Camera camera,
        List<(float[] Vertices, Texture? Texture, int Blend, Vector3 Tint, float Opacity,
            int FogPolicy)> draws, float depthBias = GroundFxDepthBias)
    {
        if (_groundShader is null || draws.Count == 0) return;
        _groundShader.Use();
        _groundShader.Set("uViewProjection", camera.RelativeViewProjection);
        _groundShader.Set("uView", camera.RelativeView);
        _groundShader.Set("uTexture", 0);
        _groundShader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _groundShader.Set("uFogColor", FogColor);
        _groundShader.Set("uFogStart", FogStart);
        _groundShader.Set("uFogEnd", FogEnd);
        _groundShader.Set("uFarClip", FarClip);
        _gl.BindVertexArray(_groundVao);
        _gl.Enable(EnableCap.Blend);
        // Depth TEST on, depth WRITE off: the pass runs after the opaque units, so testing is
        // what lets a body occlude the decal under it, and not writing keeps a translucent
        // decal from stamping itself into the buffer. Enabled explicitly rather than inherited
        // — this ran on whatever state the previous pass happened to leave behind.
        bool depthWasEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, depthBias);
        foreach (var draw in draws)
        {
            if (draw.Vertices.Length == 0) continue;
            _groundShader.Set("uHasTexture", draw.Texture is null ? 0 : 1);
            _groundShader.Set("uTint", draw.Tint);
            _groundShader.Set("uOpacity", draw.Opacity);
            _groundShader.Set("uFogPolicy", draw.FogPolicy);
            draw.Texture?.Bind(0);
            ApplyBlend(draw.Blend);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
            fixed (float* p = draw.Vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(draw.Vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(draw.Vertices.Length / 6));
        }
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(0f, 0f);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        if (!depthWasEnabled) _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private static bool HasAnimatedAlpha(M2Model model, M2Batch batch)
    {
        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.Colors.Count &&
            model.Colors[batch.ColorIndex].Alpha.Keys.Any(a => a < 32767)) return true;
        if (batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            int track = model.TransparencyLookup[batch.TextureWeightIndex];
            if (track >= 0 && track < model.TransparencyTracks.Count &&
                model.TransparencyTracks[track].Keys.Any(a => a < 32767)) return true;
        }
        return false;
    }

    private void ApplyBlend(int mode)
    {
        if (mode is 3 or 4) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else if (mode == 5) _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
        else if (mode == 6) _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    public void Dispose()
    {
        foreach (Mesh? mesh in _meshes.Values) mesh?.Dispose();
        foreach (Texture? texture in _textures.Values) texture?.Dispose();
        if (_groundVbo != 0) _gl.DeleteBuffer(_groundVbo);
        if (_groundVao != 0) _gl.DeleteVertexArray(_groundVao);
        if (_billboardVbo != 0) _gl.DeleteBuffer(_billboardVbo);
        if (_billboardVao != 0) _gl.DeleteVertexArray(_billboardVao);
        _meshes.Clear(); _textures.Clear(); _shader?.Dispose();
        _groundShader?.Dispose();
        _billboardShader?.Dispose();
        _rtsRingTexture?.Dispose();
        _rtsChevronTexture?.Dispose();
        foreach (Texture texture in _devDiscTextures.Values) texture.Dispose();
        _devDiscTextures.Clear();
        foreach (Texture texture in _devSectorTextures.Values) texture.Dispose();
        _devSectorTextures.Clear();
        _devStripTexture?.Dispose();
    }

    private const string FragmentSource = @"#version 330 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform vec2 uUvOffset;
uniform int uHasTexture;
uniform float uAlphaCutoff;
uniform vec3 uTint;
uniform float uOpacity;
uniform int uUnlit;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3 uFogColor;
uniform int uFogEnabled;
uniform int uFogPolicy;
uniform float uFarClip;
out vec4 FragColor;
void main() {
    if (uFarClip > 0.0 && vEyeDepth > uFarClip) discard;
    vec4 tex = uHasTexture != 0 ? texture(uTexture, vUV + uUvOffset) : vec4(1.0);
    vec4 base = vec4(tex.rgb * uTint, tex.a * uOpacity);
    if (base.a < uAlphaCutoff || base.a <= 0.001) discard;
    vec3 light = vec3(1.0);
    if (uUnlit == 0) {
        float n = max(dot(normalize(vNormal), normalize(uSunDirection)), 0.0);
        light = uAmbientColor * uAmbientIntensity + uSunColor * uSunIntensity * n;
    }
    vec3 rgb = base.rgb * light;
    if (uFogEnabled != 0 && uFogPolicy != 4) {
        float visibility = clamp((uFogEnd-vEyeDepth) / max(0.001,uFogEnd-uFogStart), 0.0, 1.0);
        vec3 target = uFogColor;
        if (uFogPolicy == 1) target = vec3(0.0);
        else if (uFogPolicy == 2) target = vec3(1.0);
        else if (uFogPolicy == 3) target = vec3(0.50196078);
        rgb = mix(target, rgb, visibility);
    }
    FragColor = vec4(rgb, base.a);
}";

    private const string GroundVertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in float aAlpha;
uniform mat4 uViewProjection;
uniform mat4 uView;
out vec2 vUV;
out float vAlpha;
out float vEyeDepth;
void main(){vUV=aUV;vAlpha=aAlpha;vEyeDepth=-(uView*vec4(aPosition,1.0)).z;gl_Position=uViewProjection*vec4(aPosition,1.0);}";

    private const string GroundFragmentSource = @"#version 330 core
in vec2 vUV;
in float vAlpha;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform int uHasTexture;
uniform vec3 uTint;
uniform float uOpacity;
uniform int uFogEnabled;
uniform int uFogPolicy;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uFarClip;
out vec4 FragColor;
void main(){
    if(uFarClip>0.0 && vEyeDepth>uFarClip)discard;
    vec4 t=uHasTexture!=0?texture(uTexture,vUV):vec4(1.0);
    FragColor=vec4(t.rgb*uTint,t.a*uOpacity*vAlpha);
    if(FragColor.a<=0.001)discard;
    if(uFogEnabled!=0 && uFogPolicy!=4){
        float visibility=clamp((uFogEnd-vEyeDepth)/max(0.001,uFogEnd-uFogStart),0.0,1.0);
        vec3 target=uFogColor;
        if(uFogPolicy==1)target=vec3(0.0);
        else if(uFogPolicy==2)target=vec3(1.0);
        else if(uFogPolicy==3)target=vec3(0.50196078);
        FragColor.rgb=mix(target,FragColor.rgb,visibility);
    }
}";

    private const string BillboardVertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
uniform mat4 uViewProjection;
uniform mat4 uView;
out vec2 vUV;
out float vEyeDepth;
void main(){vUV=aUV;vEyeDepth=-(uView*vec4(aPosition,1.0)).z;gl_Position=uViewProjection*vec4(aPosition,1.0);}";

    private const string BillboardFragmentSource = @"#version 330 core
in vec2 vUV;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform vec3 uTint;
uniform float uOpacity;
uniform float uFarClip;
out vec4 FragColor;
void main(){
    if(uFarClip>0.0 && vEyeDepth>uFarClip)discard;
    vec4 t=texture(uTexture,vUV);
    FragColor=vec4(t.rgb*uTint,t.a*uOpacity);
    if(FragColor.a<=0.001)discard;
}";
}

