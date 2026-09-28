using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Lazy MPQ-backed cache for authored FrameXML art and DBC-resolved icons.</summary>
public sealed class GameplayArt : IDisposable
{
    public readonly record struct PreparedTexture(
        string Path, byte[] Pixels, int Width, int Height);

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _embeddedPngTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<QuestHelperPinKind, Texture?> _questHelperMarkers = [];
    private byte[]? _questHelperFrizQt;
    private bool _questHelperFrizQtResolved;
    private readonly Dictionary<string, Texture?> _repeatTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _additiveTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _brightHighlightTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _additiveRegionTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _circularTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _painterlyTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _painterlyCircularTextures = new(StringComparer.OrdinalIgnoreCase);
    private int _painterlyEpoch = -1;

    public GameplayArt(GL gl, MpqMount mpq) { _gl = gl; _mpq = mpq; }

    public Texture? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_textures.TryGetValue(path, out Texture? cached)) return cached;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) return _textures[path] = null;
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            return _textures[path] = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
        }
        catch { return _textures[path] = null; }
    }

    public uint Handle(string path) => Get(path)?.Handle ?? 0;

    /// <summary>
    /// Lazy cache for original project-authored UI PNGs compiled into the executable. This is a
    /// separate path from <see cref="Handle"/> because that path deliberately normalizes every
    /// request to an MPQ-backed BLP. Decode as straight-alpha BGRA: ImGui's ordinary alpha blend
    /// expects unpremultiplied colour, and <see cref="Texture.From2D"/> uploads BGRA bytes.
    /// </summary>
    public uint EmbeddedPngHandle(string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName)) return 0;
        if (_embeddedPngTextures.TryGetValue(resourceName, out Texture? cached))
            return cached?.Handle ?? 0;

        try
        {
            using Stream? stream = typeof(GameplayArt).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                throw new FileNotFoundException("Embedded UI resource is missing", resourceName);
            using SkiaSharp.SKCodec? codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
                throw new InvalidDataException("Skia could not open the embedded PNG");

            var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            SkiaSharp.SKCodecResult decoded = codec.GetPixels(info, bitmap.GetPixels());
            if (decoded != SkiaSharp.SKCodecResult.Success)
                throw new InvalidDataException($"Skia PNG decode returned {decoded}");

            Texture texture = Texture.From2D(_gl, bitmap.Bytes, info.Width, info.Height,
                mipmaps: false, repeat: false);
            _embeddedPngTextures[resourceName] = texture;
            return texture.Handle;
        }
        catch (Exception ex)
        {
            _embeddedPngTextures[resourceName] = null;
            Console.WriteLine($"[ui-art] embedded PNG failed: {resourceName}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Original high-resolution quest-helper art. The same pure rasterizer is exercised by the
    /// clinical visual check, so these cannot drift back to cropped cursor art or tiny gossip
    /// punctuation without failing verification.
    /// </summary>
    public uint QuestHelperMarkerHandle(QuestHelperPinKind kind)
    {
        QuestHelperPinKind visualKind = kind switch
        {
            QuestHelperPinKind.Available => QuestHelperPinKind.Available,
            QuestHelperPinKind.TurnIn => QuestHelperPinKind.TurnIn,
            _ => QuestHelperPinKind.Loot,
        };
        if (_questHelperMarkers.TryGetValue(visualKind, out Texture? cached))
            return cached?.Handle ?? 0;
        try
        {
            if (visualKind is QuestHelperPinKind.Available or QuestHelperPinKind.TurnIn &&
                !_questHelperFrizQtResolved)
            {
                _questHelperFrizQt = _mpq.ReadFile(FontFace.FrizQt);
                _questHelperFrizQtResolved = true;
            }
            byte[] pixels = QuestHelperMarkerArt.RenderBgra(visualKind, _questHelperFrizQt);
            Texture texture = Texture.From2D(_gl, pixels,
                QuestHelperMarkerArt.Width, QuestHelperMarkerArt.Height,
                mipmaps: false, repeat: false);
            _questHelperMarkers[visualKind] = texture;
            return texture.Handle;
        }
        catch
        {
            _questHelperMarkers[visualKind] = null;
            return 0;
        }
    }

    /// <summary>Resolve UI art with repeat addressing (Backdrop edge strips).</summary>
    public uint RepeatHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_repeatTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _repeatTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Texture texture = Texture.From2D(_gl, bgra, width, height,
                mipmaps: false, repeat: true);
            _repeatTextures[path] = texture;
            return texture.Handle;
        }
        catch { _repeatTextures[path] = null; return 0; }
    }

    /// <summary>Return only an already-resolved UI texture; never reads MPQ data.</summary>
    public bool TryHandle(string path, out uint handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (!_textures.TryGetValue(path, out Texture? texture)) return false;
        handle = texture?.Handle ?? 0;
        return true;
    }

    /// <summary>CPU-only MPQ read/BLP decode; safe on the bounded asset pool.</summary>
    public PreparedTexture? Prepare(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
        return new PreparedTexture(path, pixels, width, height);
    }

    /// <summary>Publish one CPU-prepared texture on the owning GL thread.</summary>
    public uint Adopt(PreparedTexture prepared)
    {
        if (_textures.TryGetValue(prepared.Path, out Texture? cached))
            return cached?.Handle ?? 0;
        Texture texture = Texture.From2D(
            _gl, prepared.Pixels, prepared.Width, prepared.Height,
            mipmaps: false, repeat: false);
        _textures[prepared.Path] = texture;
        return texture.Handle;
    }

    /// <summary>True after a path has either loaded or been cached as absent.</summary>
    public bool IsResolved(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        return _textures.ContainsKey(path);
    }

    public void MarkMissing(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        _textures.TryAdd(path, null);
    }

    /// <summary>
    /// Painterly-styled copy of a piece of art, for painterly mode.
    ///
    /// Spell icons, item icons and the stand-in portraits are BLPs drawn
    /// straight onto the UI. The painterly pass runs over the default
    /// framebuffer and an off-screen bake, so it never touches them: a painted
    /// world carried a bar of untouched Blizzard icons, which is the single
    /// loudest way the interface announced it was not part of the picture.
    ///
    /// A COPY, never the original: the same texture is drawn unstyled elsewhere
    /// (tooltips, the spellbook, the cursor payload), so restyling the shared
    /// handle in place would be visible everywhere at once. <paramref name="style"/>
    /// receives (destination framebuffer, source texture, width, height).
    ///
    /// <paramref name="epoch"/> invalidates the whole cache when the painterly
    /// knobs move - without it a slider drag would leave every already-styled
    /// icon frozen at the settings it was first baked with.
    /// </summary>
    /// <summary>
    /// Release every styled copy now.
    ///
    /// <see cref="PainterlyHandle"/> drops them lazily on an epoch change, which
    /// is enough while the mode is ON but never fires when it goes OFF - the
    /// next styled lookup is the trigger, and there is no next one. That left a
    /// full set of styled copies resident for the rest of the session.
    /// </summary>
    public void ClearPainterlyCache()
    {
        foreach (Texture texture in _painterlyTextures.Values.Where(t => t is not null).Distinct()!)
            texture.Dispose();
        foreach (Texture texture in _painterlyCircularTextures.Values.Where(t => t is not null).Distinct()!)
            texture.Dispose();
        _painterlyTextures.Clear();
        _painterlyCircularTextures.Clear();
    }

    public uint PainterlyHandle(string path, int epoch, Action<uint, uint, int, int> style)
    {
        if (epoch != _painterlyEpoch)
        {
            ClearPainterlyCache();
            _painterlyEpoch = epoch;
        }

        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_painterlyTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? Handle(path);

        Texture? source = Get(path);
        if (source is null) { _painterlyTextures[path] = null; return 0; }

        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _painterlyTextures[path] = null; return source.Handle; }
            // Decoded again only to size and seed the destination; the style
            // pass overwrites every texel from the source texture.
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Texture destination = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);

            uint fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, destination.Handle, 0);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete)
                style(fbo, source.Handle, width, height);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.DeleteFramebuffer(fbo);

            _painterlyTextures[path] = destination;
            return destination.Handle;
        }
        catch { _painterlyTextures[path] = null; return source.Handle; }
    }

    /// <summary>
    /// Painterly-styled copy of art drawn inside a ROUND aperture - the party
    /// portraits are the case: stand-in character art, so they belong to the
    /// painted set, but they sit in ring chrome that cannot hide square corners.
    ///
    /// The disc is cut BEFORE the style pass and carried through it, which works
    /// because the pass writes the source alpha back out untouched. Styling
    /// first and masking after would need a readback for no gain.
    /// </summary>
    public uint PainterlyCircularHandle(string path, int epoch, Action<uint, uint, int, int> style)
    {
        if (epoch != _painterlyEpoch)
        {
            ClearPainterlyCache();
            _painterlyEpoch = epoch;
        }

        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_painterlyCircularTextures.TryGetValue(path, out Texture? cached))
            return cached?.Handle ?? CircularHandle(path);

        uint source = CircularHandle(path);
        if (source == 0) { _painterlyCircularTextures[path] = null; return 0; }

        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _painterlyCircularTextures[path] = null; return source; }
            // Decoded again only to size and seed the destination; the style
            // pass overwrites every texel from the masked source texture.
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            IconApertureMask.ApplyCircularBgra(bgra, width, height);
            Texture destination = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);

            uint fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, destination.Handle, 0);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete)
                style(fbo, source, width, height);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.DeleteFramebuffer(fbo);

            _painterlyCircularTextures[path] = destination;
            return destination.Handle;
        }
        catch { _painterlyCircularTextures[path] = null; return source; }
    }

    /// <summary>
    /// Alpha-masked copy for an icon drawn inside circular button chrome. A ring texture cannot
    /// mask its transparent corners, so painting the ordinary square handle beneath it leaks four
    /// square corners. This cache keeps that repair local to controls with a round aperture.
    /// </summary>
    public uint CircularHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_circularTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _circularTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            IconApertureMask.ApplyCircularBgra(bgra, width, height);
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _circularTextures[path] = texture;
            return texture.Handle;
        }
        catch { _circularTextures[path] = null; return 0; }
    }

    /// <summary>
    /// Builds an alpha-safe copy of ADD-authored art for ImGui's regular alpha draw list.
    /// Pure black becomes transparent instead of painting an opaque rectangle - and, for art the
    /// BLP header declares to be ADD (no alpha channel), the colour is also normalised to full
    /// brightness so the DIM mid-tones stop subtracting light from what is under them. Without
    /// that second half a dim glow like UI-CheckBox-Highlight, which peaks at 99/255, darkened
    /// every pixel it covered and read as a black square. See <see cref="UiHighlightBlendLaw"/>
    /// for the arithmetic and for why an ImGui draw list cannot simply blend ADD.
    /// </summary>
    public uint AdditiveHandle(string path)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_additiveTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _additiveTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            UiHighlightBlendLaw.EncodeAdditive(bgra, addArt: !BlpDecoder.HasAlphaChannel(bytes));
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _additiveTextures[path] = texture;
            return texture.Handle;
        }
        catch { _additiveTextures[path] = null; return 0; }
    }

    /// <summary>
    /// Converts grayscale/near-grayscale ADD hover art into a white alpha mask. Ordinary alpha
    /// blending of the source RGB inevitably subtracts some destination light; a white mask can
    /// only brighten, matching ButtonHilight-Square's 1.12 hover appearance in an ImGui draw list.
    /// Keep colored state/equipped glows on <see cref="AdditiveHandle"/> so their hue survives.
    /// </summary>
    public uint BrightHighlightHandle(string path)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_brightHighlightTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _brightHighlightTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            UiHighlightBlendLaw.EncodeBrightMask(bgra);
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _brightHighlightTextures[path] = texture;
            return texture.Handle;
        }
        catch { _brightHighlightTextures[path] = null; return 0; }
    }

    /// <summary>
    /// A sheet whose ADD-ness is declared PER REGION in FrameXML rather than by its own header,
    /// and so cannot be settled by <see cref="BlpDecoder.HasAlphaChannel"/>.
    ///
    /// UI-StateIcon is the case this exists for. One 64x64 file holds four quadrants: the top
    /// half is the Zzz / crossed-swords icons, ordinary alpha art (measured 7-8% opaque), and the
    /// bottom half is their GLOW, which PlayerFrame.xml declares alphaMode="ADD" on
    /// PlayerRestGlow/PlayerAttackGlow and which measures 100% opaque with 71% of it flat black.
    /// The FILE reports alphaDepth 8 because of the icons, so the header test correctly says
    /// "not ADD art" for the sheet while being wrong about half of it - and drawing that half
    /// straight painted a black square over the level number.
    ///
    /// Only uvMin..uvMax is re-encoded; every texel outside it comes out byte-identical to
    /// <see cref="Handle"/>, so one texture can still serve both halves of a sheet like this.
    /// </summary>
    public uint AdditiveRegionHandle(string path, Vector2 uvMin, Vector2 uvMax)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        string key = $"{path}|{uvMin.X},{uvMin.Y}|{uvMax.X},{uvMax.Y}";
        if (_additiveRegionTextures.TryGetValue(key, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _additiveRegionTextures[key] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            int x0 = Math.Clamp((int)MathF.Round(uvMin.X * width), 0, width);
            int x1 = Math.Clamp((int)MathF.Round(uvMax.X * width), x0, width);
            int y0 = Math.Clamp((int)MathF.Round(uvMin.Y * height), 0, height);
            int y1 = Math.Clamp((int)MathF.Round(uvMax.Y * height), y0, height);
            for (int y = y0; y < y1 && x1 > x0; y++)
                UiHighlightBlendLaw.EncodeAdditive(
                    bgra.AsSpan((y * width + x0) * 4, (x1 - x0) * 4), addArt: true);
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _additiveRegionTextures[key] = texture;
            return texture.Handle;
        }
        catch { _additiveRegionTextures[key] = null; return 0; }
    }

    /// <summary>Evict map art, including cached misses, after a pack mount. Call on the GL thread before drawing a frame.</summary>
    public void ClearMapCache()
    {
        var released = new HashSet<Texture>();
        Dictionary<string, Texture?>[] caches = [_textures, _repeatTextures, _additiveTextures,
            _brightHighlightTextures, _additiveRegionTextures, _circularTextures,
            _painterlyTextures, _painterlyCircularTextures];
        foreach (var cache in caches)
            foreach (string key in cache.Keys.Where(path =>
                path.StartsWith(@"Interface\WorldMap\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"textures\Minimap\", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (cache.Remove(key, out Texture? texture) && texture is not null) released.Add(texture);
            }
        foreach (Texture texture in released) texture.Dispose();
    }

    public void Dispose()
    {
        foreach (Texture texture in _textures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _embeddedPngTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _questHelperMarkers.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _repeatTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _additiveTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _brightHighlightTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _additiveRegionTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _circularTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        // Styled copies are owned solely by this cache - nothing else holds them.
        ClearPainterlyCache();
        _textures.Clear();
        _embeddedPngTextures.Clear();
        _questHelperMarkers.Clear();
        _repeatTextures.Clear();
        _additiveTextures.Clear();
        _brightHighlightTextures.Clear();
        _additiveRegionTextures.Clear();
        _circularTextures.Clear();
    }
}
