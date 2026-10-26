using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Tabletop;

/// <summary>
/// The Quest Tabletop's table (shared_docs/QUEST_TABLETOP.md §2, §4 far layer): the zone as a miniature on a
/// walnut table - the compiled relief per ADT tile painted with its minimap tile, the cut-earth skirt around the
/// board, per-chunk water, a warm hemisphere + north-west sun with soft shadows, linear fog, and the miniature
/// lens (tilt-shift) + ACES tone map as post passes. Everything is in world yards (X north, Y west, Z up); the
/// relief exaggeration is a vertex-shader uniform (z' = baseZ + (z − baseZ)·k), so the slider costs nothing and
/// k = 1 is the identity. This class only draws: it never touches the game camera, the world or the network.
///
/// Pipeline per frame: shadow map (relief + skirt from the sun) → far layer, near layer (real world) and far water into a
/// multisampled RGBA16F target holding display values (the board shader tone-maps per fragment: three.js ACES + sRGB;
/// the world renderers already write display values) → resolve → [tilt-shift horizontal → vertical] → copy out.
/// </summary>
public sealed class TabletopRenderer : IDisposable
{
    /// <summary>One board tile as the caller prepared it off-thread: relief (null = sea) and minimap pixels (BGRA, may be null).</summary>
    public sealed record TileInput(int Col, int Row, ReliefTile? Relief, byte[]? MinimapBgra, int MinimapWidth, int MinimapHeight);

    private const int Stride = 11;                 // pos3 normal3 uv2 colour3
    private const int ShadowSize = 2048;
    private const float NormalBias = 6f;           // web: 0.6 scene units
    private const float ShadowBias = -0.0004f;

    private readonly GL _gl;
    private readonly Shader _board, _depth, _tilt, _output;
    private readonly uint _fullscreenVao;
    private readonly Texture _tableTexture;

    private sealed class Mesh
    {
        public uint Vao, Vbo, Ebo;
        public int Count;
        public Texture? Texture;
        public (int Col, int Row) Key;
    }

    private readonly List<Mesh> _tiles = new();
    private readonly List<Mesh> _waters = new();   // per tile, so the near layer can take a tile's water over
    private Mesh? _skirt, _table;
    private readonly Dictionary<(int Col, int Row), ReliefTile> _relief = new();

    private uint _shadowFbo, _shadowTex;
    private uint _msFbo, _msColor, _msDepth;
    private uint _fboA, _texA, _fboB, _texB;
    private int _w, _h, _samples;

    public TabletopLaw.Board? Board { get; private set; }
    public float BaseZ { get; private set; }
    public float Exaggeration { get; set; } = TabletopLaw.DefaultExaggeration;
    public bool Miniature { get; set; } = true;
    public bool ShowWater { get; set; } = true;
    public int TileCount => _tiles.Count;
    public int MinimapCount { get; private set; }

    public TabletopRenderer(GL gl)
    {
        _gl = gl;
        _board = Shader.FromSource(gl, "tabletop_board", BoardVert, BoardFrag);
        _depth = Shader.FromSource(gl, "tabletop_depth", DepthVert, DepthFrag);
        _tilt = Shader.FromSource(gl, "tabletop_tilt", FullscreenVert, TiltFrag);
        _output = Shader.FromSource(gl, "tabletop_output", FullscreenVert, OutputFrag);
        _fullscreenVao = gl.GenVertexArray();
        _tableTexture = Texture.From2D(gl, WalnutPixels(512), 512, 512, mipmaps: true, repeat: true);
        CreateShadowTarget();
    }

    // ── board ───────────────────────────────────────────────────────────────

    /// <summary>Build the board's meshes. Call on the GL thread; the inputs are prepared off-thread.</summary>
    public void LoadBoard(TabletopLaw.Board board, IReadOnlyList<TileInput> tiles)
    {
        ClearBoard();
        Board = board;
        foreach (var t in tiles)
            if (t.Relief is { } r) _relief[(t.Col, t.Row)] = r;

        float minZ = float.PositiveInfinity;
        bool sea = false;
        foreach (var t in tiles)
        {
            if (t.Relief is { } r) minZ = MathF.Min(minZ, r.MinSurface);
            else sea = true;
        }
        BaseZ = TabletopLaw.BaseZ(minZ, sea);

        MinimapCount = 0;
        foreach (var t in tiles)
        {
            var water = new List<float>();
            float northX = (32 - t.Row) * TabletopLaw.TileYards, westY = (32 - t.Col) * TabletopLaw.TileYards;
            float xHi = MathF.Min(board.Top, northX), xLo = MathF.Max(board.Bottom, northX - TabletopLaw.TileYards);
            float yHi = MathF.Min(board.Left, westY), yLo = MathF.Max(board.Right, westY - TabletopLaw.TileYards);
            if (xHi <= xLo || yHi <= yLo) continue;
            if (t.Relief is not { } relief)
            {
                WaterQuad(water, xHi, xLo, yHi, yLo, 0f);
                var seaMesh = Upload(water.ToArray(), null);
                seaMesh.Key = (t.Col, t.Row);
                _waters.Add(seaMesh);
                continue;
            }

            const int n = ReliefCompiler.Samples;
            var v = new float[n * n * Stride];
            var bare = Linear(TabletopLaw.Rgb(TabletopLaw.BareGround));
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float wx = Math.Clamp(northX - j * ReliefCompiler.Step, xLo, xHi);
                float wy = Math.Clamp(westY - i * ReliefCompiler.Step, yLo, yHi);
                var nrm = NormalAt(wx, wy);
                int o = (j * n + i) * Stride;
                v[o] = wx; v[o + 1] = wy; v[o + 2] = ReliefCompiler.HeightIn(relief, wx, wy);
                v[o + 3] = nrm.X; v[o + 4] = nrm.Y; v[o + 5] = nrm.Z;
                v[o + 6] = (westY - wy) / TabletopLaw.TileYards;    // image-right = east
                v[o + 7] = (northX - wx) / TabletopLaw.TileYards;   // image-down = south (rows uploaded top first)
                v[o + 8] = bare.X; v[o + 9] = bare.Y; v[o + 10] = bare.Z;
            }
            var idx = new List<uint>((n - 1) * (n - 1) * 6);
            for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                uint a = (uint)(j * n + i), c = a + 1, d = a + (uint)n, e = d + 1;
                // Counter-clockwise seen from above (+Z): a (NW) → d (SW) → c (NE), c → d → e.
                idx.AddRange(new[] { a, d, c, c, d, e });
            }
            var mesh = Upload(v, idx.ToArray());
            mesh.Key = (t.Col, t.Row);
            if (t.MinimapBgra is { } px && t.MinimapWidth > 0 && t.MinimapHeight > 0)
            {
                mesh.Texture = Texture.From2D(_gl, px, t.MinimapWidth, t.MinimapHeight, mipmaps: true, repeat: false);
                MinimapCount++;
            }
            _tiles.Add(mesh);

            const float C = TabletopLaw.TileYards / 16f;
            for (int cr = 0; cr < 16; cr++)
            for (int cc = 0; cc < 16; cc++)
            {
                if (relief.Water[cr * 16 + cc] is not float level) continue;
                float x0 = MathF.Min(xHi, northX - cr * C), x1 = MathF.Max(xLo, northX - (cr + 1) * C);
                float y0 = MathF.Min(yHi, westY - cc * C), y1 = MathF.Max(yLo, westY - (cc + 1) * C);
                if (x0 > x1 && y0 > y1) WaterQuad(water, x0, x1, y0, y1, level);
            }
            if (water.Count > 0)
            {
                var wm = Upload(water.ToArray(), null);
                wm.Key = (t.Col, t.Row);
                _waters.Add(wm);
            }
        }
        BuildSkirt(board);
        BuildTable(board);
    }

    /// <summary>Relief height (unexaggerated world Z) at a world position; 0 off the compiled tiles (sea).</summary>
    public float HeightAt(float x, float y)
    {
        int col = (int)MathF.Floor(32f - y / TabletopLaw.TileYards), row = (int)MathF.Floor(32f - x / TabletopLaw.TileYards);
        return _relief.TryGetValue((col, row), out var t) ? ReliefCompiler.HeightIn(t, x, y) : 0f;
    }

    /// <summary>Chunk water level at a world position, or null (dry, or off the compiled tiles).</summary>
    public float? WaterAt(float x, float y)
    {
        int col = (int)MathF.Floor(32f - y / TabletopLaw.TileYards), row = (int)MathF.Floor(32f - x / TabletopLaw.TileYards);
        return _relief.TryGetValue((col, row), out var t) ? ReliefCompiler.WaterIn(t, x, y) : null;
    }

    /// <summary>Exaggerated board height (what is on screen) at a world position.</summary>
    public float ShownHeightAt(float x, float y) => TabletopLaw.Exaggerate(HeightAt(x, y), BaseZ, Exaggeration);

    /// <summary>Where a ray hits the shown board (exaggerated relief, inside the rectangle), or null.</summary>
    public Vector3? Pick(Vector3 origin, Vector3 dir, float maxDistance)
    {
        if (Board is not { } b) return null;
        float step = MathF.Max(2f, b.Span / 600f);
        Vector3 prev = origin;
        bool prevAbove = true;
        for (float t = step; t <= maxDistance; t += step)
        {
            var p = origin + dir * t;
            bool above = !b.Contains(p.X, p.Y) || p.Z > ShownHeightAt(p.X, p.Y);
            if (!above && prevAbove && t > step)
            {
                // Bisect between the last point above and this one below.
                Vector3 lo = prev, hi = p;
                for (int k = 0; k < 20; k++)
                {
                    var m = (lo + hi) * 0.5f;
                    if (b.Contains(m.X, m.Y) && m.Z <= ShownHeightAt(m.X, m.Y)) hi = m; else lo = m;
                }
                return hi;
            }
            prev = p;
            prevAbove = above;
        }
        return null;
    }

    private Vector3 NormalAt(float x, float y)
    {
        const float s = ReliefCompiler.Step;
        float dzdx = (HeightAt(x + s, y) - HeightAt(x - s, y)) / (2 * s);
        float dzdy = (HeightAt(x, y + s) - HeightAt(x, y - s)) / (2 * s);
        return Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
    }

    private void WaterQuad(List<float> w, float x0, float x1, float y0, float y1, float z)
    {
        var c = Linear(TabletopLaw.Rgb(TabletopLaw.Water));
        void V(float x, float y) => w.AddRange(new[] { x, y, z, 0f, 0f, 1f, 0f, 0f, c.X, c.Y, c.Z });
        V(x0, y0); V(x1, y0); V(x0, y1);
        V(x0, y1); V(x1, y0); V(x1, y1);
    }

    /// <summary>The cut earth around the board edge (§2): a vertical strip from the relief down to the slab base.</summary>
    private void BuildSkirt(TabletopLaw.Board b)
    {
        var pts = new List<(float X, float Y, Vector2 Out)>();
        void Walk(float x0, float y0, float x1, float y1, Vector2 outward)
        {
            int n = Math.Max(2, (int)MathF.Ceiling(MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / ReliefCompiler.Step));
            for (int k = 0; k <= n; k++) pts.Add((x0 + (x1 - x0) * k / n, y0 + (y1 - y0) * k / n, outward));
        }
        Walk(b.Top, b.Left, b.Top, b.Right, new Vector2(1, 0));        // north edge, outward = +X
        Walk(b.Top, b.Right, b.Bottom, b.Right, new Vector2(0, -1));   // east edge, outward = −Y
        Walk(b.Bottom, b.Right, b.Bottom, b.Left, new Vector2(-1, 0)); // south edge
        Walk(b.Bottom, b.Left, b.Top, b.Left, new Vector2(0, 1));      // west edge

        Vector3 top = Linear(TabletopLaw.Rgb(TabletopLaw.SkirtTop)), bottom = Linear(TabletopLaw.Rgb(TabletopLaw.SkirtBottom));
        var v = new List<float>();
        var idx = new List<uint>();
        for (int k = 0; k < pts.Count; k++)
        {
            var (x, y, o) = pts[k];
            // The cut runs from the shown surface (land, or the water over a sea floor) down to the slab base.
            float surface = MathF.Max(MathF.Max(HeightAt(x, y), WaterAt(x, y) ?? float.MinValue), BaseZ);
            v.AddRange(new[] { x, y, surface, o.X, o.Y, 0f, 0f, 0f, top.X, top.Y, top.Z });
            v.AddRange(new[] { x, y, BaseZ, o.X, o.Y, 0f, 0f, 0f, bottom.X, bottom.Y, bottom.Z });
            if (k + 1 < pts.Count && pts[k + 1].Out == o)
            {
                uint a = (uint)(k * 2), c = a + 1, d = a + 2, e = a + 3;
                idx.AddRange(new[] { a, c, d, d, c, e });
            }
        }
        _skirt = Upload(v.ToArray(), idx.ToArray());
    }

    /// <summary>The walnut table: a large plane half a yard under the slab base (web: 6000 scene units, texture ×10).</summary>
    private void BuildTable(TabletopLaw.Board b)
    {
        const float half = 30000f, repeat = 10f;
        var c = b.Centre;
        float z = BaseZ - 0.5f;
        var white = new Vector3(1f);
        float[] V(float x, float y, float u, float t) => new[] { x, y, z, 0f, 0f, 1f, u, t, white.X, white.Y, white.Z };
        var v = new List<float>();
        v.AddRange(V(c.X + half, c.Y + half, 0, 0)); v.AddRange(V(c.X - half, c.Y + half, 0, repeat));
        v.AddRange(V(c.X + half, c.Y - half, repeat, 0)); v.AddRange(V(c.X - half, c.Y - half, repeat, repeat));
        _table = Upload(v.ToArray(), new uint[] { 0, 1, 2, 2, 1, 3 });
        _table.Texture = _tableTexture;
    }

    private unsafe Mesh Upload(float[] vertices, uint[]? indices)
    {
        var m = new Mesh { Vao = _gl.GenVertexArray(), Vbo = _gl.GenBuffer() };
        _gl.BindVertexArray(m.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
        fixed (float* p = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        if (indices is not null)
        {
            m.Ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.Ebo);
            fixed (uint* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
            m.Count = indices.Length;
        }
        else m.Count = vertices.Length / Stride;
        uint stride = Stride * sizeof(float);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.BindVertexArray(0);
        return m;
    }

    private unsafe void Draw(Mesh m)
    {
        _gl.BindVertexArray(m.Vao);
        if (m.Ebo != 0) _gl.DrawElements(PrimitiveType.Triangles, (uint)m.Count, DrawElementsType.UnsignedInt, (void*)0);
        else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)m.Count);
    }

    public void ClearBoard()
    {
        foreach (var m in _tiles) DeleteMesh(m);
        _tiles.Clear();
        foreach (var m in _waters) DeleteMesh(m);
        _waters.Clear();
        if (_skirt is not null) DeleteMesh(_skirt);
        if (_table is not null) { _table.Texture = null; DeleteMesh(_table); }
        _skirt = _table = null;
        _relief.Clear();
        Board = null;
    }

    private void DeleteMesh(Mesh m)
    {
        _gl.DeleteVertexArray(m.Vao);
        _gl.DeleteBuffer(m.Vbo);
        if (m.Ebo != 0) _gl.DeleteBuffer(m.Ebo);
        m.Texture?.Dispose();
    }

    // ── frame ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Draw the table with <paramref name="camera"/> (its own camera, never the game's) into the currently bound
    /// default framebuffer, over the live viewport. <paramref name="near"/> draws the real-world near layer into the same
    /// colour + depth target between the far layer's opaque pass and its water; the far tiles in
    /// <paramref name="nearTiles"/> are left to it. Leaves the opaque defaults (depth on, cull on, blend off) behind.
    /// </summary>
    /// <summary>Far relief tiles left to the near layer last frame (diagnostics).</summary>
    public int FarTilesHidden { get; private set; }

    public unsafe void Render(Camera camera, Action? near = null, IReadOnlySet<(int col, int row)>? nearTiles = null)
    {
        int* vp = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, vp);
        int x = vp[0], y = vp[1], w = vp[2], h = vp[3];
        if (w <= 0 || h <= 0) return;
        EnsureTargets(w, h);

        bool hadSrgb = _gl.IsEnabled(EnableCap.FramebufferSrgb);
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);

        var board = Board;
        float span = board?.Span ?? 2000f;
        var centre = board is { } bb ? new Vector3(bb.Centre.X, bb.Centre.Y, BaseZ) : camera.EyeTarget;
        var sunPos = centre + TabletopLaw.SunOffset(span);
        var sunDir = Vector3.Normalize(sunPos - centre);
        float ext = TabletopLaw.ShadowHalfExtent(span);
        var lightView = Matrix4x4.CreateLookAt(sunPos, centre, Vector3.UnitX);
        var lightVp = lightView * Matrix4x4.CreateOrthographicOffCenter(-ext, ext, -ext, ext, 10f, span * 4f);

        // 1. Shadow map: relief + skirt cast (web castShadow), from the sun.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, ShadowSize, ShadowSize);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        if (board is not null)
        {
            _depth.Use();
            _depth.Set("uLightVP", lightVp);
            _depth.Set("uBaseZ", BaseZ);
            _depth.Set("uK", Exaggeration);
            foreach (var m in _tiles) Draw(m);
            if (_skirt is not null) Draw(_skirt);
        }

        // 2. Scene, linear HDR, multisampled.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        var bg = Linear(TabletopLaw.Rgb(TabletopLaw.Background));
        var bgShown = TabletopLaw.ToneMap(bg);
        _gl.ClearColor(bgShown.X, bgShown.Y, bgShown.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (board is not null)
        {
            var eye = camera.Position;
            _board.Use();
            _board.Set("uViewProj", camera.RelativeViewProjection);
            _board.Set("uCamera", eye);
            _board.Set("uForward", Vector3.Normalize(camera.EyeTarget - eye));
            _board.Set("uLightVP", lightVp);
            _board.Set("uSunDir", sunDir);
            _board.Set("uSunColor", Linear(TabletopLaw.Rgb(TabletopLaw.Sun)) * TabletopLaw.SunIntensity);
            _board.Set("uSky", Linear(TabletopLaw.Rgb(TabletopLaw.HemiSky)) * TabletopLaw.HemiIntensity);
            _board.Set("uGround", Linear(TabletopLaw.Rgb(TabletopLaw.HemiGround)) * TabletopLaw.HemiIntensity);
            _board.Set("uFogColor", bg);
            _board.Set("uFogNear", span * TabletopLaw.FogNearSpans);
            _board.Set("uFogFar", span * TabletopLaw.FogFarSpans);
            _board.Set("uShadowTexel", 1f / ShadowSize);
            _board.Set("uNormalBias", NormalBias);
            _board.Set("uShadowBias", ShadowBias);
            _board.Set("uTex", 0);
            _board.Set("uShadow", 1);
            _board.Set("uExposure", TabletopLaw.ToneExposure);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _shadowTex);

            void Surface(Mesh m, float k, bool textured, bool doubleSided, float alpha, float spec)
            {
                _board.Set("uBaseZ", BaseZ);
                _board.Set("uK", k);
                _board.Set("uTextured", textured && m.Texture is not null ? 1 : 0);
                _board.Set("uDoubleSided", doubleSided ? 1 : 0);
                _board.Set("uAlpha", alpha);
                _board.Set("uSpecular", spec);
                if (m.Texture is not null) m.Texture.Bind(0);
                Draw(m);
            }

            if (_table is not null) Surface(_table, 1f, true, false, 1f, 0f);
            FarTilesHidden = 0;
            foreach (var m in _tiles)
                if (nearTiles is null || !nearTiles.Contains(m.Key)) Surface(m, Exaggeration, true, false, 1f, 0f);
                else FarTilesHidden++;
            if (_skirt is not null) Surface(_skirt, Exaggeration, false, true, 1f, 0f);

            if (near is not null)
            {
                near();
                // The world renderers leave their own state; put back what the far layer's water and the post passes
                // expect. Report the first time something unusual was left (scissor, masks, another target).
                ReportLeftState();
                _gl.Disable(EnableCap.ScissorTest);
                _gl.Disable(EnableCap.StencilTest);
                _gl.ColorMask(true, true, true, true);
                _gl.DepthMask(true);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
                _gl.Viewport(0, 0, (uint)w, (uint)h);
                _gl.Disable(EnableCap.CullFace);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.Disable(EnableCap.FramebufferSrgb);
                _board.Use();
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _shadowTex);
            }

            if (ShowWater && _waters.Count > 0)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.DepthMask(false);
                foreach (var m in _waters)
                    if (nearTiles is null || !nearTiles.Contains(m.Key)) Surface(m, Exaggeration, false, true, TabletopLaw.WaterAlpha, 1f);
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
            }
        }

        // 3. Resolve, 4. miniature lens, 5. tone map into the presentation framebuffer.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fboA);
        _gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_fullscreenVao);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        uint final = _texA;
        if (Miniature)
        {
            _tilt.Use();
            _tilt.Set("uTex", 0);
            _tilt.Set("uFocus", TabletopLaw.TiltFocus);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fboB);
            Bind(_texA);
            _tilt.Set("uStep", new Vector2(TabletopLaw.TiltBlurPixels / w, 0f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fboA);
            Bind(_texB);
            _tilt.Set("uStep", new Vector2(0f, TabletopLaw.TiltBlurPixels / h));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            final = _texA;
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(x, y, (uint)w, (uint)h);
        _output.Use();
        _output.Set("uTex", 0);
        Bind(final);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Opaque defaults for whatever draws next (ImGui sets its own state).
        _gl.BindVertexArray(0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        if (hadSrgb) _gl.Enable(EnableCap.FramebufferSrgb);
    }

    private bool _leftStateReported;

    private unsafe void ReportLeftState()
    {
        if (_leftStateReported) return;
        int fbo = 0;
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, &fbo);
        bool* mask = stackalloc bool[4];
        _gl.GetBoolean(GLEnum.ColorWritemask, mask);
        bool scissor = _gl.IsEnabled(EnableCap.ScissorTest), stencil = _gl.IsEnabled(EnableCap.StencilTest);
        if (fbo == _msFbo && !scissor && !stencil && mask[0] && mask[1] && mask[2]) return;
        _leftStateReported = true;
        Console.WriteLine($"[tabletop] near layer left GL state: draw fbo {fbo} (scene {_msFbo}), scissor {scissor}, stencil {stencil}, " +
                          $"colour mask {mask[0]}/{mask[1]}/{mask[2]}/{mask[3]} - reset");
    }

    private void Bind(uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    // ── targets ─────────────────────────────────────────────────────────────

    private unsafe void CreateShadowTarget()
    {
        _shadowTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _shadowTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, ShadowSize, ShadowSize, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);
        _shadowFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _shadowTex, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        Check("shadow");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private unsafe void EnsureTargets(int w, int h)
    {
        if (w == _w && h == _h && _msFbo != 0) return;
        DeleteTargets();
        _w = w; _h = h;
        int max = 0;
        _gl.GetInteger(GLEnum.MaxSamples, &max);
        _samples = Math.Clamp(max, 1, 4);

        _msColor = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msColor);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.Rgba16f, (uint)w, (uint)h);
        _msDepth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msDepth);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        _msFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msColor);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _msDepth);
        Check("scene");
        (_fboA, _texA) = ColourTarget(w, h);
        (_fboB, _texB) = ColourTarget(w, h);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
    }

    private unsafe (uint Fbo, uint Tex) ColourTarget(int w, int h)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        Check("post");
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return (fbo, tex);
    }

    private void Check(string what)
    {
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) Console.WriteLine($"[tabletop] {what} framebuffer incomplete: {status}");
    }

    private void DeleteTargets()
    {
        if (_msFbo != 0) _gl.DeleteFramebuffer(_msFbo);
        if (_msColor != 0) _gl.DeleteRenderbuffer(_msColor);
        if (_msDepth != 0) _gl.DeleteRenderbuffer(_msDepth);
        if (_fboA != 0) _gl.DeleteFramebuffer(_fboA);
        if (_fboB != 0) _gl.DeleteFramebuffer(_fboB);
        if (_texA != 0) _gl.DeleteTexture(_texA);
        if (_texB != 0) _gl.DeleteTexture(_texB);
        _msFbo = _msColor = _msDepth = _fboA = _fboB = _texA = _texB = 0;
    }

    public void Dispose()
    {
        ClearBoard();
        DeleteTargets();
        if (_shadowFbo != 0) _gl.DeleteFramebuffer(_shadowFbo);
        if (_shadowTex != 0) _gl.DeleteTexture(_shadowTex);
        _gl.DeleteVertexArray(_fullscreenVao);
        _tableTexture.Dispose();
        _board.Dispose(); _depth.Dispose(); _tilt.Dispose(); _output.Dispose();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Vector3 Linear(Vector3 srgb)
    {
        static float L(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new Vector3(L(srgb.X), L(srgb.Y), L(srgb.Z));
    }

    /// <summary>The walnut grain (web #table): #3a281a with 260 dark wavy strokes, BGRA, deterministic.</summary>
    private static byte[] WalnutPixels(int size)
    {
        var rgb = new float[size * size * 3];
        var base_ = TabletopLaw.Rgb(TabletopLaw.Table);
        for (int k = 0; k < size * size; k++) { rgb[k * 3] = base_.X; rgb[k * 3 + 1] = base_.Y; rgb[k * 3 + 2] = base_.Z; }
        var rng = new Random(20261014);
        for (int i = 0; i < 260; i++)
        {
            float y0 = (float)rng.NextDouble() * size;
            float r = (20 + (float)rng.NextDouble() * 40) / 255f, g = (12 + (float)rng.NextDouble() * 20) / 255f, b = 6 / 255f;
            float a = 0.15f + (float)rng.NextDouble() * 0.25f;
            float width = 0.5f + (float)rng.NextDouble() * 2.5f;
            for (int x = 0; x < size; x++)
            {
                float yc = y0 + MathF.Sin(x / 70f + i) * 3f;
                for (int yy = (int)MathF.Floor(yc - width); yy <= (int)MathF.Ceiling(yc + width); yy++)
                {
                    float cover = Math.Clamp(width * 0.5f + 0.5f - MathF.Abs(yy + 0.5f - yc), 0f, 1f) * a;
                    if (cover <= 0f) continue;
                    int py = ((yy % size) + size) % size, o = (py * size + x) * 3;
                    rgb[o] += (r - rgb[o]) * cover; rgb[o + 1] += (g - rgb[o + 1]) * cover; rgb[o + 2] += (b - rgb[o + 2]) * cover;
                }
            }
        }
        var bgra = new byte[size * size * 4];
        for (int k = 0; k < size * size; k++)
        {
            bgra[k * 4] = (byte)(rgb[k * 3 + 2] * 255f + 0.5f);
            bgra[k * 4 + 1] = (byte)(rgb[k * 3 + 1] * 255f + 0.5f);
            bgra[k * 4 + 2] = (byte)(rgb[k * 3] * 255f + 0.5f);
            bgra[k * 4 + 3] = 255;
        }
        return bgra;
    }

    // ── shaders ─────────────────────────────────────────────────────────────

    private const string BoardVert = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec3 aColour;
uniform mat4 uViewProj;   // camera-relative view-projection
uniform vec3 uCamera;
uniform float uBaseZ;
uniform float uK;         // relief exaggeration: z' = baseZ + (z - baseZ) * k
out vec3 vWorld;
out vec3 vNormal;
out vec2 vUv;
out vec3 vColour;
void main()
{
    vec3 p = aPos;
    p.z = uBaseZ + (p.z - uBaseZ) * uK;
    vWorld = p;
    vNormal = vec3(aNormal.xy, aNormal.z / uK);   // inverse-transpose of the z scale
    vUv = aUv;
    vColour = aColour;
    gl_Position = uViewProj * vec4(p - uCamera, 1.0);
}";

    private const string BoardFrag = @"#version 330 core
in vec3 vWorld;
in vec3 vNormal;
in vec2 vUv;
in vec3 vColour;
uniform sampler2D uTex;
uniform sampler2DShadow uShadow;
uniform int uTextured;
uniform int uDoubleSided;
uniform float uAlpha;
uniform float uSpecular;
uniform vec3 uCamera;
uniform vec3 uForward;
uniform mat4 uLightVP;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform vec3 uSky;
uniform vec3 uGround;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uShadowTexel;
uniform float uNormalBias;
uniform float uShadowBias;
uniform float uExposure;
out vec4 frag;
const float PI = 3.14159265;
vec3 rrtOdt(vec3 v)
{
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}
// three.js ACESFilmicToneMapping, then the sRGB transfer: the target holds display values, like the world renderers'.
vec3 shown(vec3 c)
{
    const mat3 inM = mat3(vec3(0.59719, 0.07600, 0.02840), vec3(0.35458, 0.90834, 0.13383), vec3(0.04823, 0.01566, 0.83777));
    const mat3 outM = mat3(vec3(1.60475, -0.10208, -0.00327), vec3(-0.53108, 1.10813, -0.07276), vec3(-0.07367, -0.00605, 1.07602));
    c *= uExposure / 0.6;
    c = clamp(outM * rrtOdt(inM * c), 0.0, 1.0);
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
vec3 toLinear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
float shadowAt(vec3 world, vec3 n)
{
    vec4 lp = uLightVP * vec4(world + n * uNormalBias, 1.0);
    vec3 c = lp.xyz / lp.w * 0.5 + 0.5;
    if (c.x < 0.0 || c.x > 1.0 || c.y < 0.0 || c.y > 1.0 || c.z > 1.0) return 1.0;
    c.z += uShadowBias;
    float s = 0.0;
    for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
            s += texture(uShadow, vec3(c.xy + vec2(x, y) * uShadowTexel, c.z));
    return s / 25.0;
}
void main()
{
    vec3 albedo = uTextured == 1 ? toLinear(texture(uTex, vUv).rgb) : vColour;
    vec3 n = normalize(vNormal);
    if (uDoubleSided == 1 && !gl_FrontFacing) n = -n;
    float ndl = max(dot(n, uSunDir), 0.0);
    float sh = ndl > 0.0 ? shadowAt(vWorld, n) : 1.0;
    vec3 hemi = mix(uGround, uSky, 0.5 * n.z + 0.5);
    vec3 col = albedo / PI * (hemi + uSunColor * ndl * sh);
    if (uSpecular > 0.0)
    {
        vec3 v = normalize(uCamera - vWorld);
        vec3 h = normalize(v + uSunDir);
        float e = 300.0;
        col += uSunColor * sh * ndl * 0.08 * (e + 2.0) / (8.0 * PI) * pow(max(dot(n, h), 0.0), e);
    }
    float depth = dot(vWorld - uCamera, uForward);
    col = mix(col, uFogColor, smoothstep(uFogNear, uFogFar, depth));
    frag = vec4(shown(col), uAlpha);
}";

    private const string DepthVert = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uLightVP;
uniform float uBaseZ;
uniform float uK;
void main()
{
    vec3 p = aPos;
    p.z = uBaseZ + (p.z - uBaseZ) * uK;
    gl_Position = uLightVP * vec4(p, 1.0);
}";

    private const string DepthFrag = @"#version 330 core
void main() { }";

    private const string FullscreenVert = @"#version 330 core
out vec2 vUv;
void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}";

    // three.js Horizontal/VerticalTiltShiftShader: 9 taps, blur grows with the distance from the focus row.
    private const string TiltFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec2 uStep;    // (blur / width, 0) or (0, blur / height)
uniform float uFocus;  // focus row, 0 = bottom
out vec4 frag;
void main()
{
    vec2 d = uStep * abs(uFocus - vUv.y);
    vec4 sum = texture(uTex, vUv - 4.0 * d) * 0.051;
    sum += texture(uTex, vUv - 3.0 * d) * 0.0918;
    sum += texture(uTex, vUv - 2.0 * d) * 0.12245;
    sum += texture(uTex, vUv - 1.0 * d) * 0.1531;
    sum += texture(uTex, vUv) * 0.1633;
    sum += texture(uTex, vUv + 1.0 * d) * 0.1531;
    sum += texture(uTex, vUv + 2.0 * d) * 0.12245;
    sum += texture(uTex, vUv + 3.0 * d) * 0.0918;
    sum += texture(uTex, vUv + 4.0 * d) * 0.051;
    frag = sum;
}";

    // The scene target already holds display values (tone-mapped per fragment); the last pass only copies.
    private const string OutputFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
out vec4 frag;
void main()
{
    frag = vec4(texture(uTex, vUv).rgb, 1.0);
}";
}
