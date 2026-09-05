using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.Player;
using MSUIClient.World;
using MSUIClient.World.Collision;
using MSUIClient.World.Doodads;
using MSUIClient.World.Particles;
using MSUIClient.World.Units;
using MSUIClient.World.Wmo;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using System.Diagnostics;
using System.Numerics;
using static MSUIClient.Engine.UI.StaticPopupCoordinatorLaw;

namespace MSUIClient;

/// <summary>
/// MSUI Client — native C# client for VMaNGOS 1.12.1 (client build 5875).
///
/// Phase 1: load Northshire straight out of the local MPQs and walk around it.
/// No asset server, no bake, no HTTP, no coordinate conversion.
/// </summary>
public static partial class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("MSUI Client — VMaNGOS 1.12.1 (build 5875)");
        Console.WriteLine();

        // Hybrid laptops: ask Windows for the discrete GPU (self-registering,
        // path-keyed, so it also covers published copies; see GpuPreference).
        GpuPreference.RegisterHighPerformance();

        PortraitBatchOptions? portraitBatch = null;
        VariantBatchOptions? variantBatch = null;
        MovementSuiteOptions? movementSuite = null;
        LiveRunOptions? liveRun = null;
        string? configPath;
        string? argumentError;
        bool movementRequested = args.Contains("--movement-suite", StringComparer.OrdinalIgnoreCase);
        bool variantRequested = args.Contains("--variant-batch", StringComparer.OrdinalIgnoreCase);
        bool liveRequested = args.Contains("--live-bootstrap", StringComparer.OrdinalIgnoreCase) ||
                             args.Contains("--live-protocol", StringComparer.OrdinalIgnoreCase);
        bool parsed = liveRequested
            ? TryParseLiveRunArgs(args, out liveRun, out configPath, out argumentError)
            : movementRequested
            ? TryParseMovementSuiteArgs(args, out movementSuite, out configPath, out argumentError)
            : variantRequested
            ? TryParseVariantBatchArgs(args, out variantBatch, out configPath, out argumentError)
            : TryParsePortraitBatchArgs(args, out portraitBatch, out configPath, out argumentError);
        if (!parsed)
        {
            Console.Error.WriteLine($"[{(movementRequested ? "movement-suite" : variantRequested ? "variant-batch" : "batch")}] {argumentError}");
            if (movementRequested) PrintMovementSuiteUsage();
            else if (variantRequested) PrintVariantBatchUsage();
            else PrintPortraitBatchUsage();
            return 2;
        }

        ClientConfig config;
        try
        {
            config = ClientConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return 1;
        }

        // A live protocol is evidence about the authenticated server session. Letting it boot
        // with networking disabled opens the offline world viewer, which looks runnable but can
        // never satisfy that contract and is dangerously easy to mistake for a live test.
        if (liveRun is not null && !config.Server.Enabled)
        {
            Console.Error.WriteLine(
                "[live-run] LIVE_CONFIG_DISABLED: the selected config has server.enabled=false; " +
                "no window was opened and no live evidence was produced");
            return 2;
        }

        // Offline diagnostic: parse one or more M2 models straight from the MPQs
        // and print their particle-emitter records (blend/texture/tiles/color ramp).
        // No window, no server. Usage: --dump-emitters "Spells\Foo.mdx" [more...]
        if (args.Contains("--dump-emitters", StringComparer.OrdinalIgnoreCase))
        {
            using var mount = new Formats.MpqMount(config.ClientDataPath);
            foreach (string modelPath in args.Where(a =>
                a.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
            {
                byte[]? bytes = mount.ReadFile(modelPath)
                    ?? mount.ReadFile(Path.ChangeExtension(modelPath, ".mdx"))
                    ?? mount.ReadFile(Path.ChangeExtension(modelPath, ".m2"));
                Console.WriteLine($"===== {modelPath} ({(bytes is null ? "NOT FOUND" : bytes.Length + " bytes")}) =====");
                if (bytes is null) continue;
                Formats.M2Model? model = Formats.M2Reader.Parse(bytes);
                if (model is null) { Console.WriteLine("  parse returned null"); continue; }
                Console.WriteLine($"  textures={model.Textures.Count} emitters={model.ParticleEmitters.Count} ribbons={model.RibbonEmitters.Count}");
                Console.WriteLine($"  attachments[{model.Attachments.Count}]: {string.Join(" ", model.Attachments.Select(a => $"0x{a.Id:X}(bone{a.BoneIndex})"))}");
                // Parent chain for the hand-attach bones (spell-hand tags 0x15/0x16/0x11 and real hands 0x1/0x2).
                foreach (ushort tag in new ushort[] { 0x1, 0x2, 0x11, 0x15, 0x16 })
                {
                    Formats.M2Attachment? at = model.Attachments.FirstOrDefault(a => a.Id == tag);
                    if (at is null) continue;
                    int b = (int)at.BoneIndex; var chain = new List<int>();
                    for (int g = 0; b >= 0 && b < model.Bones.Count && g < 20; g++) { chain.Add(b); b = model.Bones[b].ParentBone; }
                    Console.WriteLine($"  attach 0x{tag:X}: bone{at.BoneIndex} parentChain={string.Join("<-", chain)}");
                }
                for (int i = 0; i < model.ParticleEmitters.Count; i++)
                {
                    Formats.M2ParticleEmitter e = model.ParticleEmitters[i];
                    string tex = e.Texture < model.Textures.Count ? model.Textures[e.Texture].Filename : "<oob>";
                    string Rgba(uint v) => $"({(v >> 16) & 0xFF},{(v >> 8) & 0xFF},{v & 0xFF},a{(v >> 24) & 0xFF})";
                    Console.WriteLine($"  emitter[{i}] blend={e.BlendingType} shape={e.Shape} flags=0x{e.Flags:X} " +
                        $"texIdx={e.Texture} tex='{(tex.Length == 0 ? "<empty/replaceable>" : tex)}' " +
                        $"tiles={e.TextureRows}x{e.TextureCols} rate={e.EmissionRate:R} life={e.Lifespan:R}");
                    Console.WriteLine($"    colorKeys RGB {Rgba(e.ColorKeys[0])} {Rgba(e.ColorKeys[1])} {Rgba(e.ColorKeys[2])} scale {e.ScaleKeys[0]:R}/{e.ScaleKeys[1]:R}/{e.ScaleKeys[2]:R}");
                    Console.WriteLine($"    physics: emitSpeed={e.EmissionSpeed:R} speedVar={e.SpeedVariation:R} vRange={e.VerticalRange:R} hRange={e.HorizontalRange:R} gravity={e.Gravity:R} areaLen={e.EmissionAreaLength:R} areaWid={e.EmissionAreaWidth:R} bone={e.Bone} pos=({e.PosX:R},{e.PosY:R},{e.PosZ:R})");
                }
                // Decode each unique emitter texture and report real pixel stats -
                // the one thing the emitter record cannot show. Green/garbage here
                // (while the color ramp is orange) localizes the bug to BLP decode.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in model.ParticleEmitters)
                {
                    string tp = e.Texture < model.Textures.Count ? model.Textures[e.Texture].Filename : "";
                    if (tp.Length == 0 || !seen.Add(tp)) continue;
                    byte[]? tb = mount.ReadFile(tp);
                    if (tb is null) { Console.WriteLine($"    TEX '{tp}' NOT FOUND"); continue; }
                    try
                    {
                        byte[] px = Formats.BlpDecoder.GetPixels(tb, 0, out int tw, out int th);
                        long sb = 0, sg = 0, sr = 0, sa = 0; int n = tw * th;
                        for (int q = 0; q < n; q++) { sb += px[q * 4]; sg += px[q * 4 + 1]; sr += px[q * 4 + 2]; sa += px[q * 4 + 3]; }
                        int ci = (th / 2 * tw + tw / 2) * 4;
                        Console.WriteLine($"    TEX '{tp}' {tw}x{th} enc={tb[8]} avgBGRA=({sb / n},{sg / n},{sr / n},{sa / n}) centerBGRA=({px[ci]},{px[ci + 1]},{px[ci + 2]},{px[ci + 3]})");
                    }
                    catch (Exception tex) { Console.WriteLine($"    TEX '{tp}' DECODE FAILED: {tex.Message}"); }
                }
                // Mesh / geoset structure — the half the emitter dump ignored. Zero-emitter
                // effect models (AoE rings, buff glows, ArcaneIntellect) are pure MESH and live
                // entirely here; this is where a missing ground ring or floating glow is diagnosed.
                Console.WriteLine($"  MESH bones={model.Bones.Count} seqs={model.Sequences.Count} " +
                    $"submeshes={model.Submeshes.Count} batches={model.Batches.Count} verts={model.Vertices.Count} " +
                    $"idx={model.Indices.Count} renderFlags={model.RenderFlags.Count} colors={model.Colors.Count} " +
                    $"transp={model.TransparencyTracks.Count}");
                for (int s = 0; s < model.Sequences.Count; s++)
                {
                    Formats.M2Sequence sq = model.Sequences[s];
                    Console.WriteLine($"    seq[{s}] anim={sq.AnimationId} var={sq.VariationId} " +
                        $"start={sq.StartTimestamp} end={sq.EndTimestamp} dur={sq.DurationMs}ms loop={sq.IsLooping}");
                }
                {
                    int animScale = 0, animTrans = 0, animRot = 0, globalSeqBones = 0;
                    foreach (Formats.M2Bone bn in model.Bones)
                    {
                        if (bn.Scale.Keys.Count > 1) animScale++;
                        if (bn.Translation.Keys.Count > 1) animTrans++;
                        if (bn.Rotation.Keys.Count > 1) animRot++;
                        if (bn.Scale.GlobalSequence >= 0 || bn.Translation.GlobalSequence >= 0 ||
                            bn.Rotation.GlobalSequence >= 0) globalSeqBones++;
                    }
                    Console.WriteLine($"    bones animated: scale={animScale} translation={animTrans} " +
                        $"rotation={animRot} globalSeq={globalSeqBones}   billboardBones=" +
                        model.Bones.Count(b => (b.Flags & 0x78) != 0));
                }
                for (int b = 0; b < model.Batches.Count; b++)
                {
                    Formats.M2Batch ba = model.Batches[b];
                    Formats.M2Submesh? sub = ba.SubmeshIndex < model.Submeshes.Count
                        ? model.Submeshes[ba.SubmeshIndex] : null;
                    Formats.M2RenderFlag? rf = ba.MaterialIndex < model.RenderFlags.Count
                        ? model.RenderFlags[ba.MaterialIndex] : null;
                    string tex = "?";
                    if (ba.TextureIndex < model.TextureLookup.Count)
                    {
                        int ti = model.TextureLookup[ba.TextureIndex];
                        tex = ti >= 0 && ti < model.Textures.Count
                            ? (model.Textures[ti].Filename.Length == 0 ? "<replaceable>" : model.Textures[ti].Filename)
                            : "<oob>";
                    }
                    // Local-space bbox + z=0-flatness of the submesh: a flat quad at z=0 is a ground
                    // decal candidate; a vertical/tall bbox is normal ring/burst geometry.
                    float mnx = 1e9f, mny = 1e9f, mnz = 1e9f, mxx = -1e9f, mxy = -1e9f, mxz = -1e9f;
                    var uniq = new HashSet<ushort>();
                    if (sub is not null)
                        for (int q = sub.IndexStart; q < sub.IndexStart + sub.IndexCount && q < model.Indices.Count; q++)
                        {
                            ushort vi = model.Indices[q]; uniq.Add(vi);
                            if (vi >= model.Vertices.Count) continue;
                            Formats.M2Vertex v = model.Vertices[vi];
                            mnx = MathF.Min(mnx, v.PosX); mny = MathF.Min(mny, v.PosY); mnz = MathF.Min(mnz, v.PosZ);
                            mxx = MathF.Max(mxx, v.PosX); mxy = MathF.Max(mxy, v.PosY); mxz = MathF.Max(mxz, v.PosZ);
                        }
                    // Reader converts WoW Z-up -> Y-up, so the ground plane is PosY≈0.
                    bool flatGround = sub is not null && MathF.Abs(mny) < 0.05f && MathF.Abs(mxy) < 0.05f;
                    Console.WriteLine($"    batch[{b}] sub={ba.SubmeshIndex} subId={sub?.Id} verts={uniq.Count} " +
                        $"idx={sub?.IndexCount} blend={rf?.BlendingMode} unlit={rf?.Unlit} 2side={rf?.TwoSided} " +
                        $"noZW={rf?.NoZWrite} colorIdx={ba.ColorIndex} texWt={ba.TextureWeightIndex} tex='{tex}' " +
                        $"bbox=({mnx:F2},{mny:F2},{mnz:F2})..({mxx:F2},{mxy:F2},{mxz:F2})" +
                        (flatGround ? "  [FLAT-GROUND]" : ""));
                }
                // Animation probe: skin the model through M2Animator exactly like
                // SpellEffectMeshRenderer does, and report the whole-model world size over the
                // sequence. An AoE ring authored at ~0.1yd that must GROW via bone scale shows up
                // here as an expanding extent; a flat/collapsed extent means the scale isn't applied.
                if (model.Sequences.Count > 0 && model.Bones.Count > 0)
                {
                    var animator = World.Units.M2Animator.Build(model,
                        model.Sequences.Select(s => (int)s.AnimationId), includeStaticSequences: true);
                    if (animator is { BoneCount: > 0 } anim)
                    {
                        var clip = anim.Find(model.Sequences[0].AnimationId) ?? anim.Clips.Values.FirstOrDefault();
                        var skin = new System.Numerics.Matrix4x4[anim.BoneCount];
                        float dur = model.Sequences[0].DurationMs / 1000f;
                        Console.WriteLine($"    ANIM probe clip='{clip?.Name}' dur={dur:F3}s boneCount={anim.BoneCount}");
                        foreach (float frac in new[] { 0f, 0.25f, 0.5f, 0.9f })
                        {
                            float ageS = dur * frac;
                            anim.Evaluate(clip, ageS, ageS, skin);
                            float wmnx = 1e9f, wmny = 1e9f, wmnz = 1e9f, wmxx = -1e9f, wmxy = -1e9f, wmxz = -1e9f;
                            float smin = 1e9f, smax = -1e9f;
                            for (int bi = 0; bi < anim.BoneCount && bi < model.Bones.Count; bi++)
                            {
                                var m = skin[bi];
                                float sc = new System.Numerics.Vector3(m.M11, m.M12, m.M13).Length();
                                smin = MathF.Min(smin, sc); smax = MathF.Max(smax, sc);
                            }
                            foreach (var vtx in model.Vertices)
                            {
                                int bone = vtx.BoneIndex0 < anim.BoneCount ? vtx.BoneIndex0 : 0;
                                var wp = System.Numerics.Vector3.Transform(
                                    new System.Numerics.Vector3(vtx.PosX, vtx.PosY, vtx.PosZ), skin[bone]);
                                wmnx = MathF.Min(wmnx, wp.X); wmny = MathF.Min(wmny, wp.Y); wmnz = MathF.Min(wmnz, wp.Z);
                                wmxx = MathF.Max(wmxx, wp.X); wmxy = MathF.Max(wmxy, wp.Y); wmxz = MathF.Max(wmxz, wp.Z);
                            }
                            Console.WriteLine($"      t={ageS:F3}s ({frac:P0})  boneScale[{smin:F3}..{smax:F3}]  " +
                                $"worldBBox=({wmnx:F2},{wmny:F2},{wmnz:F2})..({wmxx:F2},{wmxy:F2},{wmxz:F2})  " +
                                $"span=({wmxx - wmnx:F2},{wmxy - wmny:F2},{wmxz - wmnz:F2})");
                        }
                    }
                }
            }
            return 0;
        }

        Console.WriteLine($"[start] map {config.Start.Map} ({config.Start.MapName}) " +
                          $"at ({config.Start.X:F1}, {config.Start.Y:F1}, {config.Start.Z:F1})");
        if (liveRun?.Character is { Length: > 0 }) config.Server.Character = liveRun.Character;

        // The player's settings are read BEFORE the window exists, because four
        // of them are decided at window creation and cannot be changed after it:
        // the resolution, the requested multisample COUNT, the initial vsync
        // request, and the anisotropy the texture uploader selects once. Those
        // are folded into the ClientConfig the window is about to read.
        // Everything else is pushed onto the live renderers by InitSettings.
        //
        // A missing or corrupt settings.json is not an error - SettingsStore.Load
        // logs a line and starts from the shipped defaults.
        var settings = SettingsStore.Load(config.RepoRoot,
            Environment.GetEnvironmentVariable("MSUI_SETTINGS_PATH"));
        ApplyStartupSettings(config, settings.Settings);

        // The committed movement suite is an offline instrument. It exercises
        // the live local controller/animator path but must never log in or send
        // scripted movement to a realm.
        if (movementSuite is not null)
        {
            config.Server.Enabled = false;
            config.Server.AutoConnect = false;
            config.Window.VSync = false;
        }

        // WoW's own UI typeface, straight out of fonts.MPQ. It has to happen
        // before the window exists: ImGui rasterises its glyph atlas when the
        // controller is constructed, and there is no supported way to swap the
        // font afterwards. Null falls back to ImGui's bitmap font.
        string? uiFontPath = MSUIClient.Engine.UI.UiFont.Extract(config.ClientDataPath);
        string? arialFontPath = MSUIClient.Engine.UI.UiFont.Extract(
            config.ClientDataPath, MSUIClient.Engine.UI.UiFont.ArialN);
        string? morpheusFontPath = MSUIClient.Engine.UI.UiFont.Extract(
            config.ClientDataPath, MSUIClient.Engine.UI.UiFont.Morpheus);

        // The gameplay text law needs the extracted faces and the bake set BEFORE the window
        // builds its atlas: gameplay panels draw text by FrameXML font-object name (GameText /
        // FontObjectLaw) from fonts rasterised at their exact on-screen pixel size. The em
        // targets themselves are seeded from the REAL framebuffer in ClientWindow.HandleLoad
        // and retargeted per frame. See Engine/UI/GameTextLaw.cs.
        var gameplayFaces = new List<(string Face, string Path)>();
        if (uiFontPath is not null)
            gameplayFaces.Add((MSUIClient.Engine.UI.FontFace.FrizQt, uiFontPath));
        if (arialFontPath is not null)
            gameplayFaces.Add((MSUIClient.Engine.UI.FontFace.ArialN, arialFontPath));
        if (morpheusFontPath is not null)
            gameplayFaces.Add((MSUIClient.Engine.UI.FontFace.Morpheus, morpheusFontPath));
        if (gameplayFaces.Count > 0)
        {
            MSUIClient.Engine.UI.GameTextLaw.Configure(gameplayFaces);
            MSUIClient.Engine.UI.GameTextLaw.SetBakeRequests(
                MSUIClient.Engine.UI.FontObjectLaw.DefaultBakePairs());
        }

        using var window = new ClientWindow(config)
        {
            UiFontPath = uiFontPath,
            UiFontSize = MSUIClient.Engine.UI.UiFont.SizeFor(config.Window.UiScale),
            // Same live-framebuffer proportional law GameplayUiScale applies at runtime.
            GameplayTextScaleRule = (w, h) =>
                GameLoop.GameplayUiScaleFor(w, h, config.Window.UiScale),
        };

        var game = new GameLoop(window, config, portraitBatch, variantBatch, movementSuite, liveRun) { SettingsFile = settings };

        window.OnLoad += game.Load;
        window.OnUpdate += game.Update;
        if (portraitBatch is null && variantBatch is null)
        {
            window.OnRender += game.Render;
            window.OnGui += game.Gui;
            window.OnOverlay += game.Overlay;
            window.OnOverlayTop += game.OverlayTop;
        }
        window.OnClosing += game.Dispose;

        try
        {
            window.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] {ex}");
            return portraitBatch is null && variantBatch is null ? 2 : 1;
        }

        game.Dispose();
        if (movementSuite is not null) return game.MovementSuiteExitCode;
        if (liveRun is not null) return game.LiveRunExitCode;
        if (variantBatch is not null) return game.VariantBatchExitCode;
        return portraitBatch is null ? 0 : game.PortraitBatchExitCode;
    }

    /// <summary>
    /// The restart-scoped half of the settings: values the window and the texture
    /// uploader read once, at creation, and can never be told about again.
    ///
    /// They are folded into ClientConfig rather than read from GameSettings
    /// directly so that exactly one type describes "what this window was built
    /// with" - a second source of truth for the resolution is how a window ends
    /// up disagreeing with itself after a resize.
    /// </summary>
    private static void ApplyStartupSettings(ClientConfig config, GameSettings settings)
    {
        config.Window.Width = Math.Clamp(settings.Display.WindowWidth, 640, 7680);
        config.Window.Height = Math.Clamp(settings.Display.WindowHeight, 480, 4320);
        config.Window.VSync = settings.Display.VSync;
        config.Window.Fullscreen = settings.Display.Fullscreen;
        config.Window.Maximized = settings.Display.Maximized;
        config.Window.UiScale = Math.Clamp(settings.Display.UiScale, 0.5f, 4f);
        config.Window.FontScale = Math.Clamp(
            settings.MenuLayout?.TextScale ?? settings.Display.FontScale, 0.5f, 3f);

        config.Render.MsaaSamples = Math.Clamp(settings.Display.MsaaSamples, 1, 16);
        config.Render.Anisotropy = Math.Clamp(settings.Display.Anisotropy, 1f, 16f);
        config.Render.FieldOfView = Math.Clamp(settings.View.FieldOfView, 30f, 110f);
        config.Render.NearPlane = settings.View.NearPlane;
        config.Render.FarPlane = settings.View.FarPlane;

        config.Start.TileRadius = Math.Clamp(settings.Streaming.TileRadius, 1, 3);
        config.Start.WmoPreloadRadius = Math.Clamp(settings.Streaming.WmoPreloadRadius, 1, 4);
        config.Start.DrainPreloadsAtStartup = settings.Streaming.DrainPreloadsAtStartup;

        Console.WriteLine($"[settings] window {config.Window.Width}x{config.Window.Height} " +
                          $"vsync {(config.Window.VSync ? "on" : "off")} " +
                          $"msaa {config.Render.MsaaSamples}x " +
                          $"aniso {config.Render.Anisotropy:F0}x " +
                          $"ui {config.Window.UiScale:F2}x");
    }
}

/// <summary>
/// Phase 1 loop: a character walking on real terrain, with vmap collision when
/// the vmaps are present.
///
/// The camera orbits the character rather than flying independently — its Target
/// is the character's feet and Camera.EyeHeight lifts the look-at point. Camera
/// yaw is the single source of facing: it feeds straight into the controller as
/// the character's orientation, which is also the value a movement packet wants
/// in Phase 2. Nothing is converted anywhere.
///
/// Free-fly survives as an F-key toggle. It is the fastest way to tell a
/// movement bug from a world bug: if a place looks wrong on foot, fly to it.
/// </summary>
public sealed partial class GameLoop : IDisposable
{
    private readonly ClientWindow _window;
    private readonly ClientConfig _config;

    private TerrainRenderer? _terrain;
    private CharacterController? _controller;
    private bool _movementRooted;
    private float? _serverTurnRate;
    private bool _iceBlockFrozen;
    private float _iceBlockFacing;
    private CollisionWorld? _collision;
    private VmapCollisionLoader? _vmaps;
    private MpqMount? _mpq;
    private GpuUploadWorker? _uploads;
    private AssetWorkerPool? _assetWorkers;
    private WmoRenderer? _wmo;
    private DoodadRenderer? _doodads;
    private LiquidRenderer? _liquid;

    private FoliageRenderer? _foliage;
    private AdtCache? _adts;
    private CollisionDebugRenderer? _collisionDebug;
    /// <summary>Command View party sight (World/PartySight.cs): the primary's view reprojected.</summary>
    private PartySightPass? _partySight;
    private CharacterRenderer? _character;
    private UnitShadowRenderer? _unitShadows;
    private GpuFrameProfiler? _gpuProfiler;
    private readonly WorldAtmosphere _atmosphere = new();

    /// <summary>Last crosshair WMO-group pick result, shown in the HUD.</summary>
    private List<World.Wmo.WmoRenderer.GroupHit> _lastPick = new();

    /// <summary>Where the world clock comes from (settings.Lighting.TimeSource).</summary>
    private TimeSource _timeSource = TimeSource.Server;

    /// <summary>Server game time from SMSG_LOGIN_SETTIMESPEED, advanced locally;
    /// local wall clock until one arrives. See Program.LightProbe.UpdateWorldClock.</summary>
    private readonly WorldClock _worldClock = new();

    /// <summary>Dev override: freezes the world clock at whatever the HUD slider
    /// or a vantage set, WITHOUT touching the persisted TimeSource preference.
    /// Cleared by "Resume clock" in the HUD or by re-picking a source in settings.</summary>
    private bool _devTimePin;

    private bool _coupleFarPlaneToFog = true;
    private float _gameHoursPerMinute = 1f;
    private SkyRenderer? _sky;
    private SkyboxRenderer? _skybox;                 // PLAN_18 Phase 2 - the zone skybox model
    private uint _activeSkyboxId;                    // resolved from the dominant LightParams each frame
    private double _worldRenderMilliseconds;
    private double _foliageRenderMilliseconds;

    /// <summary>
    /// The periodic full re-scatter. Zero on the frames that skip it, so an
    /// average over frames is meaningless - read it on the frames it fires.
    /// </summary>
    private double _foliageScatterMilliseconds;
    private ParticleRenderer? _particles;
    private FfxGlow? _glow;
    private PainterlyPass? _painterly;
    private double _particleSimulateMilliseconds;
    private double _particleDrawMilliseconds;

    /// <summary>Drawing foliage. Every frame.</summary>
    private double _foliageDrawMilliseconds;

    private double _liquidRenderMilliseconds;
    private readonly List<WorldEntity> _visibleWorldUnits = [];
    private int _visibleUnitKnownLastFrame;
    private int _visibleUnitDistanceCulledLastFrame;
    private int _visibleUnitFrustumCulledLastFrame;
    private int _visibleUnitPortalCulledLastFrame;
    private int _remoteWakeSampledLastFrame;
    private int _visualUnitAdmissionLogFrames;
    private double _characterRenderMilliseconds;
    private double _creatureRenderMilliseconds;
    private double _selectionRenderMilliseconds;
    private double _spellEffectRenderMilliseconds;
    private double _debugRenderMilliseconds;
    private double _updateMilliseconds;
    private double _movementMilliseconds;
    private double _residencyMilliseconds;
    private double _preloadMilliseconds;
    private double _characterUpdateMilliseconds;
    private double _cameraCollisionMilliseconds;

    // Update had three untimed regions at its head. hitch-32-49-3 reported
    // "update 100.3 (move 0.1 resid 0.0 preload 0.0)" - 100 ms inside Update
    // that no sub-timer covered, which is the same lie the [stream] timer told
    // by starting late. Named now, plus an unaccounted residual so a hole here
    // can never hide again.
    private double _pumpPreloadsMilliseconds;
    private double _acceptCollisionMilliseconds;
    private double _doodadCollisionSnapshotMilliseconds;

    // The preload block is four different jobs sharing one timer, and one of
    // them ate 96 ms. Same lesson as every other split so far.
    private double _outdoorPlacementMilliseconds;
    private double _interiorPlacementMilliseconds;
    private int _placementsRequested;
    private double _discoverMilliseconds;
    private double _doodadDemandMilliseconds;
    private double _warmMilliseconds;

    /// <summary>Edge detection for the fly toggle — IsDown reports held, not pressed.</summary>
    private bool _flyKeyDown;
    private bool _mountSpecialJumpDown;
    private bool _pickButtonDown;
    private bool _debugWireframeDown;

    /// <summary>
    /// Draw the character capsule. OFF now that there is a model - the capsule
    /// is drawn solid and on top, so leaving it on hides the thing it was built
    /// to help verify. Tick "Show player capsule" in the HUD to bring it back;
    /// it is still the fastest way to confirm the model stands where the physics
    /// thinks it does.
    /// </summary>
    private bool _showPlayerMarker;

    /// <summary>
    /// Keyboard turn rate while standing, radians per second. 180 degrees a
    /// second, which is the reference client's own rate.
    ///
    /// Overwritten from Controls.TurnSpeedDegrees the moment settings load, so
    /// this initializer only ever matters before that; it used to say 2.8 (160
    /// deg/s) and disagreeing with the setting it is about to be handed is a
    /// trap worth not leaving lying around.
    /// </summary>
    private float _turnSpeed = MathF.PI;

    /// <summary>
    /// What the turn rate is multiplied by while the character is translating.
    ///
    /// Turning is slower on the move in the reference, and this is the constant.
    /// Without it a running turn is about twenty per cent too fast, which reads
    /// as the character being weightless in a way no single frame shows.
    /// </summary>
    private const float TurnRateMoving = 0.75f;

    // Last frame's movement intent, handed to the animation layer. See
    // CharacterRenderer.UnitState for why it is passed rather than measured.
    private float _moveForward;
    private float _moveStrafe;
    private bool _steering;

    /// <summary>Edge-detect for the movement/jump auto-stand correction below -
    /// see where it's set for the full explanation.</summary>
    private bool _wasStandTriggerActiveLastFrame;

    /// <summary>Whether the Tier 1 set is on. Toggling re-composites the atlas.</summary>
    private bool _dressed = true;


    /// <summary>Last frame's walk modifier, so the animator can pick Walk over Run.</summary>
    private bool _walking;

    private double _collisionBuildSeconds;
    private Task<(int Generation, CollisionWorld World, double Seconds)>? _collisionBuildTask;
    private int _collisionGeneration;
    // Doodads stream in after the startup collision build, so the initial world
    // has few/none of them. When new placements arrive we flag the collision
    // world dirty and rebuild it (coalesced) so trees/fences become solid.
    private bool _doodadCollisionDirty;

    /// <summary>
    /// Solid doodad placements that have appeared since the last collision
    /// build. A rebuild re-expands the WHOLE world (~509,000 triangles) and
    /// rebuilds the whole BVH (0.4-1.2s of worker time), so doing it for ten
    /// new props is almost pure waste - it re-derives geometry it already had.
    /// Until collision is per-tile and spliced (PLAN_08 D3), requiring a
    /// meaningful delta is the cheap way to stop paying that every few seconds.
    /// </summary>
    private int _doodadCollisionPending;

    /// <summary>
    /// Rebuild anyway after this long, so a trickle of stragglers still becomes
    /// solid rather than waiting forever below the threshold.
    /// </summary>
    private const float DoodadCollisionMaxDeferSeconds = 15f;

    private float _doodadCollisionDeferredFor;
    private float _doodadCollisionRebuildCooldown;
    private double _lastStreamSeconds;
    private (int col, int row)? _residentCentre;
    private bool _preloadWmoFirst;
    private readonly Queue<(int col, int row)> _backgroundDiscovery = new();
    private Task<AdtTerrainReader.AdtResult?>? _backgroundAdtLoad;
    private (int col, int row) _backgroundAdtTile;
    private float _backgroundDiscoveryDelay = 0.5f;
    private float _doodadDemandDelay;

    /// <summary>
    /// Where the last demand scan ran. The scan re-derives EVERY placement in
    /// residency range - 7,562 of them - plus a LINQ sort over every MODD entry
    /// in radius, and it was doing that four times a second forever, moving or
    /// not, changed or not. That is the "I cross the same spot, nothing changes
    /// visually, and it still hitches" report: the work is not caused by the
    /// crossing, it is simply always running.
    /// </summary>
    private Vector2? _lastDemandCentre;
    private readonly List<string> _newDoodadModels = [];
    private readonly HashSet<string> _newDoodadModelKeys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far the player must move before a rescan can find anything new.</summary>
    private const float DemandRescanDistance = 24f;
    private bool _demandStreamDoodads = true;

    // Developer tools: reproducible viewpoints and diagnostic commands.
    // These are all bindable commands now; the default bindings (if any) live
    // in GameLoop.Bindings.cs. These fields only track edge transitions so a
    // held binding fires once instead of every frame.
    private VantageStore? _vantages;
    private string _vantageNameInput = "";

    private bool _devReloadVantageDown;
    private bool _devDumpSceneDown;
    private bool _devGameplayDumpDown;
    private bool _devPainterlyComparisonDown;

    private bool _devOverlayDown;

    /// <summary>
    /// Whether the developer overlay panels are visible. Visibility only — the
    /// developer instruments continue running regardless of whether the panels
    /// are shown.
    /// </summary>
    private bool _devOverlayVisible;

    /// <summary>
    /// One-frame request set when the developer overlay is opened, so the UI
    /// stack can receive focus without stealing startup/gameplay focus.
    /// </summary>
    private bool _devOverlayFocusRequested;

    private string? _currentVantage;

    // Curated visibility overrides: core data, loaded and honoured always (even in
    // a release build); the click-to-author UI is dev-only. See PLAN_04.
    private VisibilityOverrides? _overrides;

    private bool _disposed;

    /// <summary>Last window fullscreen state the persistence watcher accepted;
    /// null until the window first agrees with the saved setting (boot guard).</summary>
    private bool? _observedFullscreen;

    // A player can stand half a tile diagonal from its centre. Keeping objects
    // for that reach plus draw distance and a small large-model margin means a
    // tile transition never reveals an object that was not resident already.
    private float ObjectResidencyRadius
        => (_doodads?.DrawDistance ?? _config.Render.DoodadDistance)
         + TerrainRenderer.GridSize * 0.7071068f + 50f;

    // Models outside this radius are not parsed, decoded or uploaded. The
    // small lead hides normal walking latency without turning a discovered WMO
    // into a request for every piece of furniture it contains.
    private float DoodadDemandRadius
        => MathF.Min(_atmosphere.VisibilityDistance,
            (_doodads?.DrawDistance ?? _config.Render.DoodadDistance) + 100f);

    private int WmoPreloadRadius
        => Math.Max(_config.Start.TileRadius + 1, _config.Start.WmoPreloadRadius);

    // Collision is built from the already-loaded client geometry (WMO + doodads)
    // unless the config points at server vmaps. Only in this mode do streamed-in
    // doodads need to trigger a collision rebuild (vmap collision is per-tile).
    private bool ClientGeometryCollision
        => _config.Movement.Collision
         && !string.Equals(_config.Movement.CollisionSource, "vmaps",
                StringComparison.OrdinalIgnoreCase)
         && _wmo is not null;

    public GameLoop(ClientWindow window, ClientConfig config,
        PortraitBatchOptions? portraitBatch = null, VariantBatchOptions? variantBatch = null,
        MovementSuiteOptions? movementSuite = null, LiveRunOptions? liveRun = null)
    {
        _window = window;
        _config = config;
        _portraitBatchOptions = portraitBatch;
        _variantBatchOptions = variantBatch;
        _movementSuiteOptions = movementSuite;
        _liveRunOptions = liveRun;
        _atmosphere.FogEnd = Math.Clamp(config.Render.WmoDistance, 100f, config.Render.FarPlane);
        _atmosphere.FogStart = MathF.Min(350f, _atmosphere.FogEnd - 1f);
    }

    public void Load(GL gl)
    {
        var startup = Stopwatch.StartNew();

        if (_variantBatchOptions is not null)
        {
            InitVariantBatch(gl);
            return;
        }
        if (_portraitBatchOptions is not null)
        {
            InitPortraitBatch(gl);
            return;
        }

        _window.Camera.Target = new Vector3(_config.Start.X, _config.Start.Y, _config.Start.Z);
        _window.Camera.Yaw = _config.Start.Orientation;

        // Mount the archives once and point AdtTerrainReader's extractor hook at
        // them. Without this every file read reopens up to fifteen MPQs.
        _mpq = new MpqMount(_config.ClientDataPath);
        AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;

        _uploads = _window.CreateGpuUploadWorker();
        _assetWorkers = new AssetWorkerPool();
        _gpuProfiler = new GpuFrameProfiler(gl);
        Console.WriteLine("[stream] dedicated shared-context GPU uploader ready");

        _terrain = new TerrainRenderer(gl, _config, _uploads, _assetWorkers);

        // Shaders are copied next to the exe by the csproj; fall back to the
        // source tree so editing a .frag and hitting F5 picks it up.
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(shaderDir, "terrain.vert")))
            shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
        _terrain.LoadShaders(shaderDir);

        // One parse per tile, shared by terrain, buildings and doodads.
        _adts = new AdtCache(_config.ClientDataPath, _config.Start.MapName);
        if (_config.DevTools) _vantages = VantageStore.Load(_config.RepoRoot);
        InitHitchRecorder();

        _residentCentre = TerrainRenderer.TileAt(_config.Start.X, _config.Start.Y);

        // Every renderer below is created EMPTY here (cheap: shader compile + GL
        // objects) and filled incrementally by the world loader (Program.Loading.cs)
        // across the following frames, behind the loading screen. The multi-second
        // work - terrain tiles, building models, the collision BVH - is NO LONGER
        // done in this callback, so the render loop starts and presents the loading
        // curtain immediately instead of freezing on a frozen window.
        try
        {
            _wmo = new WmoRenderer(gl, _config, _uploads, _assetWorkers);
            _wmo.LoadShaders(shaderDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo] FAILED - {ex.Message}");
            _wmo = null;
        }

        // Curated visibility overrides are core: loaded and honoured regardless of
        // DevTools, so a shipped build gets the hand-authored fixes.
        _overrides = VisibilityOverrides.Load(_config.RepoRoot);
        if (_wmo is not null) _wmo.Overrides = _overrides;

        _liquid = new LiquidRenderer(gl);
        _liquid.LoadShaders(shaderDir);
        _liquid.LoadLiquidTextures(_config.ClientDataPath);

        _sky = new SkyRenderer(gl);
        _sky.LoadShaders(shaderDir);

        try
        {
            _weatherPrecipitation = new WeatherPrecipitationRenderer(gl, _config);
            _weatherPrecipitation.LoadShaders(shaderDir);
        }
        catch (Exception ex)
        {
            _weatherPrecipitation?.Dispose();
            _weatherPrecipitation = null;
            Console.WriteLine($"[weather] precipitation renderer unavailable: {ex.Message}");
        }

        _skybox = new SkyboxRenderer(gl, _config);
        _skybox.LoadShaders(shaderDir);

        _foliage = new FoliageRenderer(gl, _config, _uploads, _assetWorkers);
        _foliage.LoadShaders(shaderDir);
        _foliage.LoadDbcs();

        try
        {
            _particles = new ParticleRenderer(gl, _config);
            _particles.DensityScale = _config.Render.ParticleDensity;
            _particles.LoadShaders(shaderDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[particles] FAILED - {ex.Message}");
            _particles = null;
        }

        // FFXGlow whole-scene gamma-byte composite (Engine/FfxGlow.cs).
        // render.glowGain is the per-zone weight;
        // render.glow = false disables the pass entirely.
        try
        {
            _glow = _config.Render.Glow
                ? new FfxGlow(gl) { Gain = _config.Render.GlowGain }
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[glow] FAILED - {ex.Message}");
            _glow = null;
        }

        // Painterly mode (Engine/PainterlyPass.cs) - whole-scene illustrated
        // restyle. Always constructed (two small shaders) so the debug-panel
        // checkbox can live-toggle it; render.painterly only seeds Enabled.
        try
        {
            _painterly = new PainterlyPass(gl)
            {
                Enabled = _config.Render.Painterly,
                Bands = _config.Render.PainterlyBands,
                BandStrength = _config.Render.PainterlyBandStrength,
                Detail = _config.Render.PainterlyDetail,
                Ink = _config.Render.PainterlyInk,
                InkThreshold = _config.Render.PainterlyInkThreshold,
                Silhouette = _config.Render.PainterlySilhouette,
                DepthFade = _config.Render.PainterlyDepthFade,
                CalmStart = _config.Render.PainterlyCalmStart,
                CalmEnd = _config.Render.PainterlyCalmEnd,
                Saturation = _config.Render.PainterlySaturation,
                Contrast = _config.Render.PainterlyContrast,
                Lift = _config.Render.PainterlyLift,
                Warmth = _config.Render.PainterlyWarmth,
                Grain = _config.Render.PainterlyGrain,
                Dither = _config.Render.PainterlyDither,
                CanvasHeight = _config.Render.PainterlyCanvasHeight,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[painterly] FAILED - {ex.Message}");
            _painterly = null;
        }

        // Exterior lighting's authored data (PLAN_09). Applied every frame by
        // UpdateExteriorLighting once loading is done.
        InitLightProbe();

        if (_config.Render.Doodads)
        {
            try
            {
                _doodads = new DoodadRenderer(gl, _config, _uploads, _assetWorkers)
                {
                    DrawDistance = _config.Render.DoodadDistance,
                    CollisionBasisIndex = _config.Render.DoodadCollisionBasis,
                    // Always demand-stream now: the loader queues the near models
                    // and they arrive through the normal streaming path, faded in.
                    DemandStreaming = true,
                };
                if (_wmo is not null)
                    _doodads.PortalVisibility = _wmo.IsDoodadPortalVisible;
                _doodads.LoadShaders(shaderDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[doodad] FAILED - {ex.Message}");
                _doodads = null;
            }
        }

        // PLAN_17 I2 queue taps. Delegates are installed once; each dequeue only
        // writes into a fixed ten-entry trace while a load record is active.
        _terrain.PreloadDequeued = NoteLoadTerrainDequeue;
        if (_wmo is not null)
            _wmo.PreloadDequeued = (path, distanceSq) =>
                NoteLoadAssetDequeue("wmo", path, distanceSq);
        if (_doodads is not null)
            _doodads.PreloadDequeued = NoteLoadAssetDequeue;
        if (_foliage is not null)
            _foliage.PreloadDequeued = path =>
                NoteLoadAssetDequeue("foliage", path, 0f);

        _mpq?.Report();

        // The controller exists now so Update/Render run and the loader can drive
        // the build across frames. Collision is null until the loader's off-thread
        // BVH lands; the ground snap happens in the loader once terrain is resident.
        _controller = new CharacterController(_terrain, _config.Movement)
        {
            Collision = null,
            MovingGroundProbe = ProbeMovingTransportGround,
            DynamicCollisionProbe = ProbeStatefulGameObjectCollision,
            LiquidSurfaceProbe = point =>
                TryGetBodyLiquidSurface(point, out float height, out _) ? height : null,
            Yaw = _config.Start.Orientation,
        };
        _controller.Teleport(_config.Start.X, _config.Start.Y, _config.Start.Z);
        _window.Camera.Target = _controller.Position;

        // The character model. Independent of the world build, so it stays in the
        // fast shell setup.
        try
        {
            _character = new CharacterRenderer(gl, _config, _assetWorkers, _uploads);
            _character.AnimationResolved = CaptureAnimationChoice;
            _character.EmoteAnimResolver = ResolveEmoteAnim;
            _character.LoadShaders(shaderDir);

            if (!_character.Load("Human", "Male"))
            {
                _character.Dispose();
                _character = null;
            }
            else
            {
                _character.Equipment = CharacterEquipment.BattlegearOfMight();
                _character.ApplyEquipment();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[character] FAILED - {ex.Message}");
            _character = null;
        }

        try
        {
            if (_mpq is not null) _unitShadows = new UnitShadowRenderer(gl, _mpq);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[unit-shadow] renderer unavailable - {ex.Message}");
            _unitShadows = null;
        }

        try
        {
            _collisionDebug = new CollisionDebugRenderer(gl);
            _collisionDebug.LoadShaders(shaderDir);
            // A SECOND instance for the creator X-ray's vmap view, so the
            // server mesh and the Ctrl+C movement-collision view never fight
            // over one GPU buffer.
            _xrayDebug = new CollisionDebugRenderer(gl);
            _xrayDebug.LoadShaders(shaderDir);
            // Party sight: the pass renders the collision world + terrain from the primary's
            // eye; the three world renderers consult it through the same object.
            _partySight = new PartySightPass(gl);
            if (_terrain is not null) _terrain.PartySight = _partySight;
            if (_wmo is not null) _wmo.PartySight = _partySight;
            if (_doodads is not null) _doodads.PartySight = _partySight;
            _xrayNavDebug = new CollisionDebugRenderer(gl);
            _xrayNavDebug.LoadShaders(shaderDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[collision] debug renderer FAILED - {ex.Message}");
            _collisionDebug = null;
            _xrayDebug = null;
            _xrayNavDebug = null;
        }

        // The settings modal's skin needs GL and the MPQ mount, so it is built
        // last; applying the saved settings needs every renderer to exist.
        InitSettings(gl);

        Console.WriteLine($"[startup] shell ready in {startup.Elapsed.TotalSeconds:F2}s - " +
                          "streaming the world in behind the loading screen");

        // Hand off to the budgeted, per-frame world loader (Program.Loading.cs).
        // It queues the terrain/WMO streaming, then StepWorldLoad advances it a
        // bounded slice per frame while DrawLoadingScreen covers it.
        // Phase 2: create the network client, then start the world load ONLY when offline.
        // Networked mode stays stateless (no world, no character) until SMSG_LOGIN_VERIFY_WORLD
        // assigns a spawn point; PumpNet then sets _config.Start and calls BeginWorldLoad.
        InitNet(gl);
        InitRealPortals(gl, shaderDir);
        // Interactive serverless boots sit at the glue front door (mode select +
        // Enter World); only batch instruments still load the world immediately.
        if (!_config.Server.Enabled && !GlueFrontDoorActive)
            BeginWorldLoad(gl);
    }

    private void PopulateDoodads((int col, int row) centreTile, bool reportDiagnostics,
        IReadOnlySet<string>? modelFilter = null, bool includeOutdoor = true,
        bool includeInterior = true)
    {
        if (_doodads is null || _terrain is null || _adts is null) return;

        Vector2 centre = _globalWmoPlacement is null
            ? TerrainRenderer.TileCenter(centreTile.col, centreTile.row)
            : new Vector2(_controller?.Position.X ?? _config.Start.X,
                          _controller?.Position.Y ?? _config.Start.Y);
        float radius = ObjectResidencyRadius;

        // Two very different jobs share this method's cost: walking nine ADTs'
        // MDDF lists, and enumerating every MODD placement of every resident
        // WMO. At 7,562 placements this measured 71 ms and we do not yet know
        // which half. Split before optimizing - that has been right every time.
        if (includeOutdoor && _globalWmoPlacement is null)
        {
            long doodadPhase = Stopwatch.GetTimestamp();
            _doodads.LoadForTiles(
                _terrain.LoadedTiles, _adts, centre, radius, reportDiagnostics, modelFilter);
            _outdoorPlacementMilliseconds = Stopwatch.GetElapsedTime(doodadPhase).TotalMilliseconds;
        }

        // Furniture. A huge WMO can touch the terrain ring while most of its
        // MODD placements are far outside doodad draw range. Resolve only the
        // furniture that could become visible before the next tile crossing.
        if (_wmo is null || !includeInterior) return;

        var interiors = Stopwatch.StartNew();
        int requested = 0, placed = 0;
        foreach (var (path, transform, light, wmoInstanceId, ownerGroups) in
                 _wmo.EnumerateDoodads(centre, radius))
        {
            if (modelFilter is not null &&
                !modelFilter.Contains(DoodadRenderer.ModelCacheKey(path))) continue;
            requested++;
            if (_doodads.AddPlaced(path, transform, light, wmoInstanceId, ownerGroups)) placed++;
        }
        _interiorPlacementMilliseconds = interiors.Elapsed.TotalMilliseconds;
        _placementsRequested = requested;

        if (reportDiagnostics && requested > 0)
            _doodads.ReportInterior(requested, placed, interiors.Elapsed.TotalSeconds);

        if (reportDiagnostics)
        {
            Console.WriteLine($"[load] doodad placement outdoor {_outdoorPlacementMilliseconds:F2} ms, " +
                              $"interior {_interiorPlacementMilliseconds:F2} ms");
            Console.WriteLine($"[stream] object residency [{centreTile.col},{centreTile.row}] " +
                              $"radius {radius:F0} yd");
        }
    }

    private void UpdateWorldResidency()
    {
        if (_controller is null || _terrain is null || _adts is null) return;

        // Global-WMO instances have no ADT grid. Their single WDT placement is
        // resident for the whole map; treating movement as tile crossings would
        // reset that placement and replace BRD with an empty terrain ring.
        if (_globalWmoPlacement is not null) return;

        var next = TerrainRenderer.TileAt(_controller.Position.X, _controller.Position.Y);
        if (_residentCentre == next) return;

        // The timer starts HERE, not after the readiness gate. It used to start
        // below and reported 0.06s for a crossing the hitch recorder measured at
        // 0.17s - a phase timer that starts late lies quietly (handbook 8.7).
        var timer = Stopwatch.StartNew();

        var terrainLead = TerrainRenderer.TileRing(
            next.col, next.row, _config.Start.TileRadius + 1);
        _terrain.QueuePreload(terrainLead, _adts);
        var desiredTerrain = TerrainRenderer.TileRing(
            next.col, next.row, _config.Start.TileRadius);
        if (!_terrain.PreloadReady(desiredTerrain)) return;

        double gateSeconds = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"[stream] crossing to tile [{next.col},{next.row}]");

        try
        {
            // Sub-phase timing. The crossing is one synchronous block that
            // rebuilds every placement in the resident ring from scratch, and
            // NO amount of waiting before the boundary avoids it - that is
            // Nico's observation and it rules out "the async work had not
            // finished" as the explanation. So the question is only WHICH
            // rebuild dominates, and that is a measurement, not a theory.
            long t0 = Stopwatch.GetTimestamp();
            _terrain.SetResidency(next.col, next.row, _config.Start.TileRadius, _adts);
            double terrainMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            _wmo?.ResetPlacements();
            _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);
            double wmoMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            _liquid?.LoadForTiles(_terrain.LoadedTiles, _adts);
            double liquidMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var preloadRing = TerrainRenderer.TileRing(next.col, next.row, WmoPreloadRadius);

            t0 = Stopwatch.GetTimestamp();
            _wmo?.QueuePreloadForTiles(preloadRing, _adts);
            double wmoQueueMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            _doodads?.ResetPlacements();
            PopulateDoodads(next, reportDiagnostics: false);
            double doodadMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            _adts.Retain(preloadRing);
            double retainMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            _residentCentre = next;

            t0 = Stopwatch.GetTimestamp();
            BeginCollisionBuild();
            double collisionMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            _lastStreamSeconds = timer.Elapsed.TotalSeconds;
            Console.WriteLine($"[stream] tile [{next.col},{next.row}] ready: " +
                              $"{_terrain.TileCount} terrain, {_wmo?.InstanceCount ?? 0} WMO, " +
                              $"{_doodads?.InstanceCount ?? 0} doodad placement(s), " +
                              $"{_lastStreamSeconds:F2}s (queue/gate {gateSeconds:F2}s)");
            Console.WriteLine($"[stream]   terrain {terrainMs:F1} ms  wmo {wmoMs:F1}  " +
                              $"liquid {liquidMs:F1}  wmoQueue {wmoQueueMs:F1}  " +
                              $"doodads {doodadMs:F1}  retain {retainMs:F1}  " +
                              $"collisionSnapshot {collisionMs:F1}  " +
                              $"(ring deferred {_wmo?.DeferredRingTiles ?? 0})");
            Console.WriteLine($"[stream]   doodads {doodadMs:F1} = outdoor " +
                              $"{_outdoorPlacementMilliseconds:F1} + wmoInterior " +
                              $"{_interiorPlacementMilliseconds:F1} over " +
                              $"{_placementsRequested:N0} MODD placement(s); " +
                              $"residency radius {ObjectResidencyRadius:F0} yd, " +
                              $"draw {_doodads?.DrawDistance ?? 0:F0} yd");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stream] FAILED entering [{next.col},{next.row}]: {ex.Message}");
        }
    }

    private void SetCollisionDebugEnabled(bool enabled)
    {
        if (_collisionDebug is null) return;

        if (enabled && _collisionDebug.TriangleCount == 0 && _collision is not null)
        {
            var timer = Stopwatch.StartNew();
            _collisionDebug.Build(_collision);
            Console.WriteLine($"[collision] debug upload deferred from startup: " +
                              $"{timer.Elapsed.TotalSeconds:F2}s");
        }

        _collisionDebug.Enabled = enabled;
    }

    /// <summary>
    /// Cross-check the rendered buildings against the collision meshes of the
    /// same buildings.
    ///
    /// These arrive by two completely independent routes — MODF placements read
    /// out of the ADT and transformed here, versus vmtile spawns extracted by
    /// the server and transformed by the collision loader. They describe the
    /// same objects, so any disagreement means one transform is wrong, and the
    /// SHAPE of the disagreement says which.
    ///
    /// A constant offset across every building points at a systematic error in
    /// one of the two placement chains — fixable in one line. Deltas that vary
    /// per building, especially with rotation, point at the rotation
    /// convention instead. Either way this turns "collision feels a few yards
    /// off" into a vector.
    /// </summary>
    /// <summary>
    /// Draw, in yellow and over everything, the exact triangles the character
    /// controller is standing on and blocked by — pulled by index from the same
    /// array the raycast intersected.
    ///
    /// The bulk wireframe answers "where is the collision world". This answers
    /// "where is the surface I am actually standing on", which is the only
    /// question that matters when movement disagrees with the picture.
    /// </summary>
    private void HighlightPhysicsTriangles()
    {
        if (_collisionDebug is null || _collision is null || _controller is null) return;
        if (!_collisionDebug.Enabled) return;

        var corners = new List<Vector3>(6);

        if (_collision.TryGetTriangle(_controller.GroundTriangle, out var a, out var b, out var c))
        {
            corners.Add(a); corners.Add(b); corners.Add(c);
        }

        if (_controller.HasBlock &&
            _collision.TryGetTriangle(_controller.LastBlockTriangle, out var d, out var e, out var f))
        {
            corners.Add(d); corners.Add(e); corners.Add(f);
        }

        if (corners.Count >= 3) _collisionDebug.RenderHighlight(_window.Camera, corners);
    }

    private void CompareWmoToCollision()
    {
        if (_wmo is null || _vmaps is null || _vmaps.WmoSpawnBounds.Count == 0) return;

        var deltas = new List<Vector3>();
        var originDeltas = new List<Vector3>();

        foreach (var (path, rMin, rMax, rOrigin) in _wmo.Placements)
        {
            string name = Path.GetFileName(path);
            var renderCentre = (rMin + rMax) * 0.5f;

            // Match by model name, then by proximity — a model placed several
            // times needs the nearest instance, not the first one.
            (string Name, Vector3 Min, Vector3 Max, Vector3 Origin)? best = null;
            float bestDistance = float.MaxValue;

            foreach (var spawn in _vmaps.WmoSpawnBounds)
            {
                if (!string.Equals(Path.GetFileName(spawn.Name), name, StringComparison.OrdinalIgnoreCase))
                    continue;

                float d = (((spawn.Min + spawn.Max) * 0.5f) - renderCentre).Length();
                if (d >= bestDistance) continue;

                bestDistance = d;
                best = spawn;
            }

            if (best is null) continue;

            var collisionCentre = (best.Value.Min + best.Value.Max) * 0.5f;
            var delta = collisionCentre - renderCentre;
            deltas.Add(delta);

            // ORIGIN vs ORIGIN. Bounding boxes depend on which triangles each
            // extractor kept, so they can never separate "the mesh is
            // different" from "the placement is different". The origins are
            // pure placement: the same building's MODF entry and its vmtile
            // spawn describe one position, so any difference here is a
            // transform bug and nothing else.
            var originDelta = best.Value.Origin - rOrigin;
            if (originDelta.Length() > 0.05f)
                Console.WriteLine(
                    $"[align] {name,-28} ORIGIN differs by ({originDelta.X,7:F2}," +
                    $"{originDelta.Y,7:F2},{originDelta.Z,7:F2}) = {originDelta.Length():F2} yd");
            originDeltas.Add(originDelta);

            if (delta.Length() > 1.5f)
            {
                // Size as well as centre. If the two footprints are the same
                // size but offset, the error is a translation. If the sizes
                // differ, the geometry is rotated differently and no amount of
                // shifting will line them up.
                var renderSize = rMax - rMin;
                var collisionSize = best.Value.Max - best.Value.Min;
                var sizeDelta = collisionSize - renderSize;

                Console.WriteLine(
                    $"[align] {name,-28} centre ({delta.X,7:F2},{delta.Y,7:F2},{delta.Z,7:F2}) " +
                    $"{delta.Length(),6:F2} yd   size ({sizeDelta.X,7:F2},{sizeDelta.Y,7:F2},{sizeDelta.Z,7:F2})");
            }
        }

        if (deltas.Count == 0)
        {
            Console.WriteLine("[align] no buildings matched between render and collision");
            return;
        }

        var mean = deltas.Aggregate(Vector3.Zero, (a, d) => a + d) / deltas.Count;
        double spread = deltas.Average(d => (double)(d - mean).Length());

        Console.WriteLine(
            $"[align] {deltas.Count} building(s) compared: mean offset " +
            $"({mean.X:F2}, {mean.Y:F2}, {mean.Z:F2}), magnitude {mean.Length():F2} yd, " +
            $"spread {spread:F2} yd");

        Console.WriteLine(spread < 1.0
            ? "[align] centre offset is consistent across buildings"
            : "[align] centre offset varies - expected, since the two meshes are not the same triangles");

        if (originDeltas.Count > 0)
        {
            var originMean = originDeltas.Aggregate(Vector3.Zero, (a, d) => a + d) / originDeltas.Count;
            float worst = originDeltas.Max(d => d.Length());

            Console.WriteLine(
                $"[align] ORIGINS: mean ({originMean.X:F3}, {originMean.Y:F3}, {originMean.Z:F3}), " +
                $"worst {worst:F3} yd");

            Console.WriteLine(worst < 0.05f
                ? "[align] origins AGREE - both chains place buildings at the same point, so any "
                  + "remaining misalignment is ROTATION or the mesh itself"
                : "[align] origins DISAGREE - one chain's translation is wrong, and that is the bug");
        }
    }

    /// <summary>
    /// Load vmap collision for exactly the tiles terrain loaded. Every failure
    /// here is non-fatal and printed: without vmaps you still walk on terrain,
    /// you just walk through buildings.
    /// </summary>
    private void LoadCollision()
    {
        if (_terrain is null) return;

        _collision = null;
        _vmaps = null;
        if (_controller is not null) _controller.Collision = null;
        _collisionDebug?.Clear();

        if (!_config.Movement.Collision)
        {
            Console.WriteLine("[collision] disabled in config (movement.collision = false)");
            return;
        }

        bool useClient = !string.Equals(_config.Movement.CollisionSource, "vmaps",
            StringComparison.OrdinalIgnoreCase);

        if (!useClient && !_config.HasVmaps)
        {
            Console.WriteLine("[collision] collisionSource is vmaps but none are configured — terrain only");
            return;
        }

        var started = DateTime.UtcNow;

        try
        {
            _collision = new CollisionWorld();

            if (useClient)
            {
                // The buildings the renderer already loaded. No second parse,
                // no second transform, no GameData\vmaps needed.
                if (_wmo is null)
                {
                    Console.WriteLine("[collision] no buildings loaded — terrain only");
                    _collision = null;
                    return;
                }

                _wmo.AppendCollision(_collision);
                _doodads?.AppendCollision(_collision);
            }
            else
            {
                _vmaps = new VmapCollisionLoader(_config.VmapPath!);

                foreach (var (col, row) in _terrain.LoadedTiles)
                    _vmaps.LoadTile(_collision, _config.Start.Map, col, row, _config.Movement.IncludeM2);
            }

            _collision.Build();
            _collisionBuildSeconds = (DateTime.UtcNow - started).TotalSeconds;

            if (_vmaps is not null) Console.WriteLine($"[collision] {_vmaps.Summary()}");
            Console.WriteLine(
                $"[collision] BVH {_collision.NodeCount:N0} nodes over " +
                $"{_collision.TriangleCount:N0} triangles, " +
                $"{_collision.DegenerateSkipped} degenerate skipped, " +
                $"{_collisionBuildSeconds:F1}s");

            // Bounds are the cheapest possible check that the spawn transform is
            // right: if this box does not straddle the loaded tiles, the geometry
            // is real but in the wrong place, and no amount of walking into
            // things will reveal that.
            var lo = _collision.BoundsMin;
            var hi = _collision.BoundsMax;
            Console.WriteLine(
                $"[collision] bounds X {lo.X:F0}..{hi.X:F0}  Y {lo.Y:F0}..{hi.Y:F0}  Z {lo.Z:F0}..{hi.Z:F0}");

            if (_collision.IsEmpty)
            {
                Console.WriteLine("[collision] WARNING no geometry loaded — check the unresolved names above");
                _collision = null;
            }

            if (_controller is not null) _controller.Collision = _collision;
            if (_collisionDebug is { Enabled: true } && _collision is not null)
                _collisionDebug.Build(_collision);
        }
        catch (Exception ex)
        {
            // Loudly. A silent failure here would present later as a physics bug.
            Console.WriteLine($"[collision] FAILED — {ex.Message}");
            Console.WriteLine("[collision] continuing with terrain collision only");
            _collision = null;
        }
    }

    /// <summary>
    /// Rebuild client-geometry collision without stopping movement. Triangle
    /// collection is a bounded snapshot on the render thread; the expensive
    /// BVH partition/sort runs on a worker while the previous world remains
    /// attached to the controller.
    /// </summary>
    private void BeginCollisionBuild()
    {
        bool useClient = !string.Equals(_config.Movement.CollisionSource, "vmaps",
            StringComparison.OrdinalIgnoreCase);
        if (!_config.Movement.Collision || !useClient || _wmo is null)
        {
            LoadCollision();
            return;
        }

        // Snapshot ONLY the placement list here: a reference to each model's
        // immutable triangle array plus its transform. A few thousand tiny
        // structs, sub-millisecond.
        //
        // What used to be here was the full expansion - ~509,000 triangles,
        // three Vector3.Transform calls each - measured by the hitch recorder
        // at 92.9 ms and fired on a timer every few seconds while doodads
        // streamed, to add a handful of props. Nothing on screen changed
        // because nothing on screen HAD changed; the work was re-deriving
        // geometry it already had.
        //
        // Handbook 5.4 forbids a worker reading live renderer placement
        // collections while they mutate. That applies to the LIST, which is
        // copied here, not to model geometry, which is immutable once loaded -
        // so the expansion belongs with the BVH build on the worker.
        var batches = new List<CollisionBatch>(8192);
        int buildings = _wmo.SnapshotCollision(batches);
        int props = _doodads?.SnapshotCollision(batches) ?? 0;
        _collisionDebug?.Clear();

        int generation = ++_collisionGeneration;
        _collisionBuildTask = Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            var next = new CollisionWorld();

            int triangles = 0, skipped = 0;
            foreach (var batch in batches)
            {
                var tris = batch.Triangles;
                int source = next.RegisterSource(Path.GetFileName(batch.Path));
                var m = batch.Transform;

                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    next.AddTriangle(
                        Vector3.Transform(tris[i], m),
                        Vector3.Transform(tris[i + 1], m),
                        Vector3.Transform(tris[i + 2], m),
                        source);
                    triangles++;
                }

                skipped += batch.Skipped;
            }

            double expandSeconds = timer.Elapsed.TotalSeconds;
            next.Build();

            Console.WriteLine(
                $"[collision] {buildings} building(s) + {props} doodad(s), " +
                $"{triangles:N0} triangles expanded in {expandSeconds * 1000:F0}ms " +
                $"off-thread ({skipped:N0} detail excluded)");

            return (generation, next, timer.Elapsed.TotalSeconds);
        });
    }

    private void AcceptReadyCollision()
    {
        if (_collisionBuildTask is not { IsCompleted: true } task) return;
        _collisionBuildTask = null;

        try
        {
            var ready = task.GetAwaiter().GetResult();
            if (ready.Generation != _collisionGeneration) return;

            _collision = ready.World.IsEmpty ? null : ready.World;
            _collisionBuildSeconds = ready.Seconds;
            if (_controller is not null) _controller.Collision = _collision;

            Console.WriteLine(
                $"[collision-async] BVH {ready.World.NodeCount:N0} nodes over " +
                $"{ready.World.TriangleCount:N0} triangles, {ready.Seconds:F2}s off-thread");

            if (_collision is not null)
            {
                var lo = _collision.BoundsMin;
                var hi = _collision.BoundsMax;
                Console.WriteLine(
                    $"[collision-async] bounds X {lo.X:F0}..{hi.X:F0}  " +
                    $"Y {lo.Y:F0}..{hi.Y:F0}  Z {lo.Z:F0}..{hi.Z:F0}");
                if (_collisionDebug is { Enabled: true }) _collisionDebug.Build(_collision);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[collision-async] FAILED - {ex.Message}; keeping previous collision");
        }
    }

    public void Update(float dt)
    {
        if (_variantBatchOptions is not null)
        {
            StepVariantBatch();
            return;
        }
        if (_portraitBatchOptions is not null)
        {
            StepPortraitBatch();
            return;
        }

        if (_movementSuiteFinished)
        {
            _window.Close();
            return;
        }

        // Frame boundary. Update-entry to Update-entry is the only placement
        // where a frame's Update AND its Render both fall inside the period
        // being measured - see HitchRecorder.FrameBoundary for why the obvious
        // alternative silently blames the wrong frame.
        HitchRecorder.FramePhases completedPhases = CurrentFramePhases();
        if (_hitch.FrameBoundary(completedPhases)) WriteHitchRecord();
        NoteLoadFrame(_hitch.LastCompleted);
        PollLoadTimelineCompletion();

        // Hovercast reads the hover the unit frames published during the previous Render,
        // so the promotion belongs on this boundary - before any binding poll can cast.
        BeginHovercastFrame();

        // Quitting is deferred out of the GUI pass and lands HERE, between
        // frames, before anything is touched. ClientWindow.Close tears the GL
        // context down synchronously - it raises Closing, which runs
        // GameLoop.Dispose and deletes every texture and buffer we own - so
        // calling it from inside a button handler left the rest of that ImGui
        // frame drawing into freed memory. That is an AccessViolationException on
        // the next widget, and the stack points at the widget rather than at the
        // button, which is what makes it worth a comment.
        if (ConsumeQuitRequest()) return;

        // Scripted creator-mode texture-swap reproduction (MSUI_CREATOR_PROBE).
        UpdateCreatorProbe();
        UpdateXrayProbe();

        // Scripted Encounter Lab raid proof (MSUI_ENCLAB_PROBE).
        UpdateEncounterLabProbe();

        // Scripted Command View party-sight proof (MSUI_PARTYSIGHT_PROBE).
        UpdatePartySightProbe();

        // Scripted offline mount check (MSUI_MOUNT_PROBE).
        UpdateMountProbe();

        // Scripted offline swimming-collision proof (MSUI_SWIM_PROBE).
        UpdateSwimProbe();

        // Cart-kit charges, cooldowns and the slows they applied.
        UpdateMountKit(NowSeconds());

        // Zone music + ambience. Before the loading-state early returns, so
        // leaving the world resets the transport instead of stranding a bed.
        UpdateWorldSoundscape();

        // Weather uses the real elapsed clock and keeps advancing behind loading
        // covers, matching CMapWeather rather than tying a ten-second ramp to
        // simulation dt or world readiness.
        _weatherVisual.Resolve(NowSeconds());

        long updateStarted = Stopwatch.GetTimestamp();
        _loadNetPumpMilliseconds = 0;
        _loadStepMilliseconds = 0;
        if (_controller is null) return;

        // Advance the appear-fade clock every frame - during the loading build and
        // after - so streamed-in doodads/buildings ease in instead of popping.
        UpdateAppearFadeClock(dt);

        SnapshotLoadUnitsBeforePump();
        long loadNetStarted = Stopwatch.GetTimestamp();
        PumpNet(dt); // Phase 2 networking pump (no-op unless server.enabled)
        UpdateMail(dt);
        _loadNetPumpMilliseconds = Stopwatch.GetElapsedTime(loadNetStarted).TotalMilliseconds;
        UpdateIceBlockFreezeState();
        _character?.PumpAppearanceUpdate();
        // Live protocols must own their wall-clock before world entry as well as
        // in game. Keeping this below the world-ready guard made a bad realm or
        // character selection hang forever instead of producing a diagnostic.
        AdvanceLiveRun(dt);
        UpdateGlueScreenshotInput();

        // LOGIN_VERIFY_WORLD only schedules this ownership transfer. Bootstrap
        // and avatar adoption each get their own curtain frame, outside the
        // network-pump bracket and without stacking a loader phase behind them.
        long transitionStarted = Stopwatch.GetTimestamp();
        if (PumpWorldEntryTransition())
        {
            _loadStepMilliseconds = Stopwatch.GetElapsedTime(transitionStarted).TotalMilliseconds;
            _updateMilliseconds = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
            return;
        }

        // Portal retirement owns shared-context completions and main-context
        // VAO cleanup even while parked at character select. Pump it before all
        // loading/stateless early returns so logout cannot strand the slot.
        StepRealPortalRetirement();

        // Keep the socket and movement heartbeat alive behind the curtain. The
        // deferred transition may enter BeginWorldLoad, so test the flag afterward.
        if (_worldLoading)
        {
            UpdateMovementSenderDuringLoad();
            long loadStepStarted = Stopwatch.GetTimestamp();
            StepWorldLoad(dt);
            _loadStepMilliseconds = Stopwatch.GetElapsedTime(loadStepStarted).TotalMilliseconds;
            _updateMilliseconds = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
            return;
        }

        // Networked: stay stateless (no world/character) until login assigns a spawn point.
        if (_config.Server.Enabled && !_worldLoadStarted) return;

        // Glue front door: equally stateless until Enter World arms the load.
        if (GlueFrontDoorActive) return;

        if (_movementSuiteOptions is not null)
        {
            if (!EnsureMovementSuiteStarted()) return;
            dt = _movementScript!.FixedDt;
        }

        // Adopting ready terrain creates VAOs and installs height grids on this
        // thread; five tiles can land in one frame.
        long headStarted = Stopwatch.GetTimestamp();
        _terrain?.PumpPreloads();
        _pumpPreloadsMilliseconds = Stopwatch.GetElapsedTime(headStarted).TotalMilliseconds;

        headStarted = Stopwatch.GetTimestamp();
        AcceptReadyCollision();
        _acceptCollisionMilliseconds = Stopwatch.GetElapsedTime(headStarted).TotalMilliseconds;

        _doodadCollisionSnapshotMilliseconds = 0;

        // Fold newly-streamed doodads into the collision world once a build slot
        // is free. Coalesced (one build at a time, at most ~1/s) so a burst of
        // streaming near a busy tile doesn't rebuild the BVH every frame. This is
        // what keeps trees/fences solid in demand-stream mode; the world settles
        // to include all resident doodads within ~a second of them appearing.
        _doodadCollisionRebuildCooldown -= dt;
        if (_doodadCollisionDirty) _doodadCollisionDeferredFor += dt;

        // Two gates, not one. The cooldown limits how OFTEN; the delta limits
        // whether it is worth doing at all. A rebuild costs a full ~509,000
        // triangle re-expansion plus a from-scratch BVH, so firing it because
        // ten props arrived re-derives the entire world to add a rounding
        // error. The defer timer is the safety valve: a slow trickle still
        // becomes solid, just later.
        // Rebuild when streaming has SETTLED, not while it is in flight. Each
        // rebuild is a ~500,000 triangle expansion plus a from-scratch BVH -
        // 0.6 to 1.1 s of worker time - and firing one every couple of seconds
        // during a stream-in kept a core pegged and the memory bus busy
        // continuously. On an integrated GPU sharing that bus with the render
        // thread, that is not free even though it is "off the main thread":
        // the present-swap stalls in the same logs are what it looks like.
        bool streamingSettled = (_doodads?.PendingPreloads ?? 0) == 0;
        bool worthRebuilding =
            (streamingSettled && _doodadCollisionPending > 0) ||
            _doodadCollisionDeferredFor >= DoodadCollisionMaxDeferSeconds;

        if (_doodadCollisionDirty && _collisionBuildTask is null
            && _doodadCollisionRebuildCooldown <= 0f && worthRebuilding)
        {
            _doodadCollisionDirty = false;
            _doodadCollisionPending = 0;
            _doodadCollisionDeferredFor = 0f;
            // 1.0s meant a full ~1.17M-triangle main-thread re-walk EVERY SECOND
            // while doodads streamed in - visible as the repeated
            // "[collision] from client geometry" spam in every log. Until the
            // snapshot is per-tile and spliced (PLAN_08 D3), coalescing harder
            // is the cheap win: trees become solid a couple of seconds later,
            // which no one can perceive, and the stutter happens a third as often.
            _doodadCollisionRebuildCooldown = 3.0f;

            // NOT free, and until now not measured: this is the same ~500k
            // triangle main-thread walk the crossing pays, fired on a timer
            // while doodads stream. Timed separately from the crossing's copy
            // (which is inside residency) so neither is double-counted.
            long snapshotStarted = Stopwatch.GetTimestamp();
            BeginCollisionBuild();
            _doodadCollisionSnapshotMilliseconds =
                Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds;
        }

        // Advance the world clock - server game time, the pinned hour or the
        // debug cycle - before the lighting resolve below consumes it, so both
        // lighting modes light the world at the same instant.
        UpdateWorldClock(dt);

        // Resolve what Light.dbc says here, now, and APPLY it. This is core and
        // runs in every build - the comment that used to sit here said "Read-only:
        // it feeds the probe panel and nothing else", which was false, and is how
        // the DevTools seam violation survived several readings. In Update rather
        // than Render so the render pass stays free of work that is not drawing.
        UpdateExteriorLighting();

        // Escape. It used to close the client outright; it now opens the game
        // menu, and quitting is a button inside it. See Program.Settings.cs.
        UpdateSettingsInput();

        // A focused action button also sets WantCaptureKeyboard. Treating that broad flag as
        // "typing" zeroed movement for one frame whenever an action was mouse-clicked, making
        // Run switch to Stand and immediately back to Run. Only real text entry and explicit
        // modal/key-capture states own gameplay movement.
        bool typing = GameplayInputLaw.BlocksMovement(
            ImGui.GetIO().WantCaptureKeyboard,
            ImGui.GetIO().WantTextInput,
            _settingsOpen,
            _bindingCapture is not null);
        UpdateBindingLatches(typing);
        UpdateHudEditInput(typing);
        UpdateAutorunBinding(typing);
        UpdateStandStateBinding(typing);
        UpdateFollowTargetBinding(typing);
        UpdateChatBindings(typing);
        UpdateCameraZoomBindings(typing);
        UpdateMinimapZoomBindings(typing);
        UpdateMinimapVisibilityBinding(typing);
        UpdateAudioBindings(typing);
        UpdateSpellFxInspectorInput(typing);
        UpdateActionBarInput(typing);
        UpdateInventoryInput(typing);
        UpdateCharacterPageInput(typing);
        UpdateSpellbookInput(typing);
        UpdateWorldMapInput(typing);
        UpdateTargetBinding(typing);
        UpdateDirectTargetBindings(typing);
        UpdateNameplateInput(typing);
        UpdateUiHideBinding(typing);
        UpdateScreenshotBinding(typing);
        UpdateControlInput(typing);
        UpdateCommanderMap();
        UpdateRunBinding(typing);
        UpdateSheathInput(typing);
        UpdatePortraitLabInput(typing);
        UpdateQuestNpcLifecycle();
        UpdateVendorLifecycle();
        UpdateGossipLifecycle();
        UpdateTrainerLifecycle();
        UpdateTaxiLifecycle();
        UpdateCommandViewNpcChoiceLifecycle();
        UpdateBankLifecycle();
        UpdateAuctionLifecycle();
        UpdateTabardLifecycle();
        UpdateNpcGreetingLifecycle();
        // Encounter Lab playback: the ONLY place wall clock reaches the simulator,
        // and it only ever decides how many fixed steps to take. No-op while the
        // window is closed or paused.
        UpdateEncounterLab(dt);
        ObserveUiPanelOwnership();

        // F toggles free-fly. Edge-triggered so holding it doesn't strobe.
        // The CRPG free view (UpdateControlInput) owns Ctrl+F by default; without this
        // exclusion the same press also flipped the local fly rig, and the two
        // toggles fought — most visibly as a floor-drop when leaving the free view.
        // Ask the BINDING rather than testing Ctrl, so reseating Free View in Key Bindings
        // moves both halves of that agreement together: whatever chord raises the free view
        // stops raising the fly rig, and plain F keeps working the moment it is free again.
        // The edge tracker follows the physical key so releasing a modifier first
        // doesn't retrigger the local toggle mid-hold.
        bool flyKey = InputKeyDown(Key.F);
        bool flyCtrlHeld = BindingDown(GameBinding.RtsToggleFreeView);
        if (flyKey && !_flyKeyDown && !typing && !flyCtrlHeld)
        {
            _controller.Flying = !_controller.Flying;
            Console.WriteLine($"[move] {(_controller.Flying ? "flying" : "walking")}");
        }
        else if (flyKey && !_flyKeyDown)
        {
            // A SWALLOWED F LOOKS EXACTLY LIKE A BROKEN ONE, and the three gates
            // that can eat it are all invisible from inside the world: an ImGui
            // text field that kept the caret after its panel closed, the game
            // menu, a keybinding capture. Say which one, once per press, rather
            // than leaving "F stopped working" to be guessed at.
            Console.WriteLine("[move] F ignored - " +
                (flyCtrlHeld ? $"the CRPG free view owns this press ({BindingHint(GameBinding.RtsToggleFreeView)})"
                 : $"the keyboard is owned elsewhere (textInput={ImGui.GetIO().WantTextInput} " +
                   $"settings={_settingsOpen} bindingCapture={_bindingCapture is not null})"));
        }
        _flyKeyDown = flyKey;

        // Debug wireframe toggle is a bindable command.
        // Defaults to unbound; users can assign it in Key Bindings.
        bool wireframePressed = BindingDown(GameBinding.DebugWireframe);

        if (wireframePressed &&
            !_debugWireframeDown &&
            _collisionDebug is not null &&
            _config.DevTools &&
            !typing)
        {
            SetCollisionDebugEnabled(!_collisionDebug.Enabled);
            Console.WriteLine($"[collision] wireframe {(_collisionDebug.Enabled ? "on" : "off")}");
        }

        _debugWireframeDown = wireframePressed;

        // Middle-click identifies the WMO group(s) under the cursor. Edge-triggered,
        // and skipped when the pointer is over the HUD.
        bool pickButton = _window.MouseMiddleDown;
        if (pickButton && !_pickButtonDown && !ImGui.GetIO().WantCaptureMouse && _config.DevTools)
            ScreenPick(_window.MousePosition);
        _pickButtonDown = pickButton;

        // Toggles the ImGui developer overlay ("MSUI Client", "Server" and every
        // instrument panel they own). This is a visibility-only toggle; DevTools
        // continue running and retain their state while the overlay is hidden.
        // Edge-triggered so holding the binding does not repeatedly toggle it.

        bool devOverlayPressed = BindingDown(GameBinding.DevToggleOverlay);

        if (devOverlayPressed &&
            !_devOverlayDown &&
            !typing &&
            _config.DevTools)
        {
            _devOverlayVisible = !_devOverlayVisible;
            _devOverlayFocusRequested = _devOverlayVisible;
            Console.WriteLine(
                $"[dev] overlay {(_devOverlayVisible ? "shown" : "hidden")}");
        }

        _devOverlayDown = devOverlayPressed;

        // Developer tool: Reloads the current vantage - snap back to the saved viewpoint.
        // Edge-triggered so holding the binding does not repeatedly teleport.
        bool reloadVantagePressed = BindingDown(GameBinding.DevReloadVantage);

        if (reloadVantagePressed &&
            !_devReloadVantageDown &&
            _currentVantage is not null &&
            _vantages is not null &&
            _config.DevTools)
        {
            var saved = _vantages.Find(_currentVantage);
            if (saved is not null)
                ApplyVantage(saved);
        }

        _devReloadVantageDown = reloadVantagePressed;


        // Developer tool: write a scene dump. Edge-triggered
        bool dumpScenePressed = BindingDown(GameBinding.DevDumpScene);

        if (dumpScenePressed &&
            !_devDumpSceneDown &&
            _config.DevTools)
        {
            DumpScene();
        }

        _devDumpSceneDown = dumpScenePressed;


        // Developer tool: arms a gameplay-plane dump for the next complete HUD frame
        bool gameplayDumpPressed = BindingDown(GameBinding.DevGameplayDump);

        if (gameplayDumpPressed &&
            !_devGameplayDumpDown &&
            _config.DevTools)
        {
            ArmGameplayDump();
        }

        _devGameplayDumpDown = gameplayDumpPressed;


        // Developer tool: capture painterly comparison batch.\
        // records a clean five-frame painterly comparison and restores the
        // live profile afterwards. It is intentionally a batch: camera, light,
        // animation time and resolution stay effectively identical.
        bool painterlyComparisonPressed = BindingDown(GameBinding.DevPainterlyComparison);

        if (painterlyComparisonPressed &&
            !_painterlyComparisonKeyDown &&
            _config.DevTools)
        {
            ArmPainterlyComparison();
        }

        _painterlyComparisonKeyDown = painterlyComparisonPressed;

        // Transport poses are client-computed. Advance them and rigidly carry an
        // attached mover before input reads the camera/facing for this frame.
        UpdateGameObjectTransports();
        CarryControlledTransportRider();

        bool shift = _window.IsDown(Key.ShiftLeft) || _window.IsDown(Key.ShiftRight);

        // A and D TURN, they do not strafe. That is vanilla's default bind and
        // it is what the hands expect; strafe lives on Q and E.
        //
        // Camera yaw IS the character's facing here - the controller takes
        // input.Yaw straight from it - so turning the camera turns the
        // character. There is only one heading in this client and this is it.
        //
        // Holding the RIGHT mouse button swaps the two, exactly as the real
        // client does: you are already steering with the mouse, so A and D are
        // free to become strafe and your hand does not have to move to Q and E
        // mid-fight.
        bool mouseSteering = _window.MouseRightDown;

        // Command View schemes (Interface Options → Command View): every scheme but Classic
        // puts the sidestep on the Turn keys (A/D) while the free view is up and leaves the
        // Strafe keys (Q/E) OUT of the camera entirely — owner call 2026-09-01: people want
        // those keys for command hotkeys, so turning is the right-drag. First person untouched.
        CommandViewScheme commandViewScheme = Settings.Controls.CommandViewScheme;
        bool commandViewSwap = _freeView && CommandViewLaw.TurnKeysStrafe(commandViewScheme);

        // The Command View camera has its OWN key rows (RTS Controls: Camera Forward / Back /
        // Left / Right / Sidestep), so a rebind in one mode never touches the other (owner
        // feedback 2026-09-03). Same axes and the same scheme law below; only the rows differ.
        GameBinding keyForward = _freeView ? GameBinding.RtsMoveForward : GameBinding.MoveForward;
        GameBinding keyBackward = _freeView ? GameBinding.RtsMoveBackward : GameBinding.MoveBackward;
        GameBinding keyTurnLeft = _freeView ? GameBinding.RtsTurnLeft : GameBinding.TurnLeft;
        GameBinding keyTurnRight = _freeView ? GameBinding.RtsTurnRight : GameBinding.TurnRight;
        GameBinding keyStrafeLeft = _freeView ? GameBinding.RtsStrafeLeft : GameBinding.StrafeLeft;
        GameBinding keyStrafeRight = _freeView ? GameBinding.RtsStrafeRight : GameBinding.StrafeRight;

        bool bindingTurnLeft = !typing && BindingDown(keyTurnLeft);
        bool bindingTurnRight = !typing && BindingDown(keyTurnRight);
        bool bindingStrafeLeft = !typing && BindingDown(keyStrafeLeft);
        bool bindingStrafeRight = !typing && BindingDown(keyStrafeRight);

        float turn = 0f;
        if (!typing && !mouseSteering && !commandViewSwap)
            turn += BindingAxis(keyTurnLeft, keyTurnRight);
        turn = Math.Clamp(turn, -1f, 1f);

        float strafe = typing ? 0f : commandViewSwap
            ? BindingAxis(keyTurnRight, keyTurnLeft)
            : BindingAxis(keyStrafeRight, keyStrafeLeft);
        if (!typing && mouseSteering && !commandViewSwap)
            strafe += BindingAxis(keyTurnRight, keyTurnLeft);
        strafe = Math.Clamp(strafe, -1f, 1f);

        // Up and down arrows walk, like vanilla. Combined with W/S rather than
        // replacing it, and clamped so holding both does not double the speed.
        bool bindingForward = !typing && BindingDown(keyForward);
        bool bindingBackward = !typing && BindingDown(keyBackward);
        float forward = typing ? 0f : Math.Clamp(
            BindingCommandLaw.ForwardAxis(bindingForward, bindingBackward,
                bothButtons: _window.MouseLeftDown && _window.MouseRightDown,
                autorun: _autorunToggled),
            -1f, 1f);

        // Command View lock: the rig is PARKED on the primary. No translation at all until the
        // lock is released, and A/D become the orbit (whatever the scheme), so you can walk the
        // camera around the primary's epicentre while the tracking holds. Q/E stay free.
        if (_freeView && CommandViewLocked)
        {
            forward = 0f;
            strafe = 0f;
            turn = typing ? 0f : Math.Clamp(BindingAxis(keyTurnLeft, keyTurnRight), -1f, 1f);
            // Under the sidestep schemes A means "camera goes left around the unit" - the eye
            // moves left, so the heading swings RIGHT. Classic keeps A = turn left, as its
            // A/D already mean turning. Owner call 2026-09-01: "standard with locked needs A/D swapped".
            if (commandViewSwap) turn = -turn;
        }

        ApplyAutoFollowInput(ref forward, dt, typing, mouseSteering);

        bool scriptedJump = false;
        OverrideMovementInput(ref forward, ref strafe, ref turn, ref shift, ref scriptedJump);
        ApplyTaxiInputLockout(ref forward, ref strafe, ref turn, ref scriptedJump);
        ApplyVanillaControlLockout(ref forward, ref strafe, ref turn, ref scriptedJump);
        bool vanillaControlLocked = VanillaSelfControlLocksMover;
        // Body status can keep changing while Ctrl+F is detached. Root, Ice Block, drunkenness,
        // and cast interruption belong to that streamed body; none may seize the observer rig.
        bool controllerRooted = ControllerOwnsControlledBodyPose && _movementRooted;
        bool controllerIceBlockFrozen = ControllerOwnsControlledBodyPose && _iceBlockFrozen;
        bool controlledBodyTacticallyFrozen = TacticalFreezePoseLaw.IsFrozen(ControlledGuid);
        bool tacticalLiveAuthorshipBlocked = TacticalFreezeBlocksLiveCommands;
        // Command View's controller is deliberately still a live camera. Everywhere else the
        // displayed driven body follows this controller even while parked/flying. The full live
        // authorship law also stays closed after Resume while an owned FIFO drains; pose rendering
        // thaws, but controller prediction and movement packets cannot race that plan.
        bool controllerTacticalFrozen = !_freeView && tacticalLiveAuthorshipBlocked;

        // Root and Ice Block are intentionally separate. A normal root removes translation and
        // jump while leaving turning/casting live. Ice Block additionally freezes facing and the
        // character pose; its aura state owns that stronger gate below.
        if (controllerRooted)
        {
            forward = 0f;
            strafe = 0f;
            scriptedJump = false;
        }
        if (controllerIceBlockFrozen || controllerTacticalFrozen)
        {
            forward = 0f;
            strafe = 0f;
            turn = 0f;
            scriptedJump = false;
        }

        // TURNING IS SLOWER WHILE YOU ARE MOVING, and that is not a detail. The
        // reference turns at 180 deg/s planted and three quarters of that once
        // you are translating, which is why running a tight circle in vanilla
        // has a radius and running one here did not. The keyboard rate has to
        // come after the movement axes are known, hence the ordering.
        //
        // Mouse steering is deliberately NOT rate-limited: it is a direct
        // pointing device and the client does not throttle it either.
        bool translating = MathF.Abs(forward) > 0.01f || MathF.Abs(strafe) > 0.01f;
        float baseTurnRate = ControllerOwnsControlledBodyPose
            ? _serverTurnRate ?? _turnSpeed
            : _turnSpeed;
        float turnRate = baseTurnRate * (translating ? TurnRateMoving : 1f) * MountTurnMultiplier();

        if (turn != 0f) _window.Camera.Rotate(turn * turnRate * dt, 0f);
        // RTS scheme: a turn — keyboard here, the mouse's share already applied by the window —
        // orbits the rig around the ground it looks at instead of spinning it in place.
        _commandViewYawDelta = turn * turnRate * dt + _window.AppliedLookYaw;
        if (_freeView && CommandViewLaw.OrbitsFocus(commandViewScheme) && !CommandViewLocked)
            OrbitCommandViewRig(_commandViewYawDelta);

        // Drunkenness belongs to the logged-in player even while another unit is possessed.
        // Current Benilla adds this pulse to movement facing (unless a keyboard turn is held)
        // and to active swim pitch; it deliberately has no normal-play FOV effect.
        byte drunkByte = ControllerOwnsControlledBodyPose &&
            _entities.TryGet(LocalPlayerGuid, out WorldEntity sessionPlayer)
            ? sessionPlayer.Fields.PlayerDrunkByte
            : (byte)0;
        float drunkWobble = translating
            ? DrunkMovementLaw.Wobble(MovementInfo.ClientUptimeMs(),
                DrunkMovementLaw.Fraction(drunkByte))
            : 0f;
        float drunkFacing = DrunkMovementLaw.FacingWobble(drunkWobble, translating,
            keyboardTurning: MathF.Abs(turn) > 0.01f);
        if (drunkFacing != 0f) _window.Camera.Rotate(drunkFacing, 0f);

        // Look up and down without the mouse. Rotate clamps pitch either way.
        float tilt = typing ? 0f : _window.Axis(Key.PageUp, Key.PageDown);
        if (tilt != 0f)
        {
            // Under the knob schemes the view angle IS the knob: PageUp/PageDown turn it, and
            // the free-view frame re-asserts the camera pitch from it.
            if (_freeView && CommandViewLaw.PitchLocked(commandViewScheme))
                Settings.Controls.CommandViewPitchDegrees = CommandViewLaw.ClampPitchDegrees(
                    Settings.Controls.CommandViewPitchDegrees +
                    tilt * _turnSpeed * 0.6f * dt * 180f / MathF.PI);
            else
                _window.Camera.Rotate(0f, tilt * _turnSpeed * 0.6f * dt);
        }

        // The animation layer is driven from intent, not from displacement, so
        // it needs to know what was pressed and whether the aim is being steered.
        _moveForward = forward;
        _moveStrafe = strafe;
        _steering = !controllerIceBlockFrozen && !controllerTacticalFrozen &&
            !vanillaControlLocked &&
            (turn != 0f || mouseSteering);

        // Cam confirmed from 1.12 play: a seated character only stands back up on
        // one of three triggers - the sit/stand key (SubmitStandStateChange's own
        // toggle handles that one), a jump, or starting to move. Edge-triggered on
        // either signal so this fires once per sit, not every frame the key/stick
        // stays held - the server round trip that actually clears StandState is
        // slower than this, and CharacterRenderer.ChooseClip's own !state.Moving
        // check already renders the stand-up locally without waiting for it; this
        // is just keeping the server's copy honest.
        bool sessionBodyTacticallyFrozen = TacticalFreezePoseLaw.IsFrozen(LocalPlayerGuid);
        bool standTriggerNow = !tacticalLiveAuthorshipBlocked && !sessionBodyTacticallyFrozen &&
            !TacticalFreezePoseLaw.IsFrozen(ControlledGuid) &&
            (translating || BindingDown(GameBinding.Jump));
        if (standTriggerNow && !_wasStandTriggerActiveLastFrame &&
            _entities.TryGet(LocalPlayerGuid, out WorldEntity selfSeated) &&
            selfSeated.Fields.StandState is (byte)UnitStandState.Sit or (byte)UnitStandState.Kneel
                or (byte)UnitStandState.Sleep)
            _net?.SendStandStateChange(UnitStandState.Stand);
        _wasStandTriggerActiveLastFrame = standTriggerNow;

        // A scripted or harness-held SPACE (movement suite, live-run, probes) owns Jump AND the
        // swim/fly vertical axis; the keyboard only when nothing is scripting.
        bool scriptedInput = _movementScript is not null || _liveHeld.Count > 0;
        var input = new MovementInput
        {
            Forward = forward,
            Strafe = strafe,
            Up = typing || controllerRooted || controllerIceBlockFrozen || controllerTacticalFrozen || vanillaControlLocked ? 0f : ((scriptedInput ? scriptedJump : BindingDown(GameBinding.Jump)) ? 1f : 0f) -
                                (InputKeyDown(Key.CapsLock) ? 1f : 0f),
            Yaw = controllerIceBlockFrozen ? _iceBlockFacing :
                controllerTacticalFrozen ? _controller.Yaw :
                vanillaControlLocked ? _controller.Yaw : _window.Camera.Yaw,
            Pitch = -_window.Camera.Pitch + DrunkMovementLaw.SwimPitchWobble(
                drunkWobble, _controller.Swimming, translating),
            Jump = !controllerRooted && !controllerIceBlockFrozen && !controllerTacticalFrozen &&
                !vanillaControlLocked && (scriptedInput
                ? scriptedJump : !typing && BindingDown(GameBinding.Jump)),
            Walking = (_walkToggled || shift) && !_controller.Flying,
            Boost = shift && _controller.Flying,
        };

        // Vanilla diverts a grounded, idle mounted Jump press into MountSpecial(94). Turning
        // alone consumes the press silently; translating keeps the ordinary jump. The opcode is
        // guidless, so never volunteer the logged-in character's flourish while driving a bot.
        bool jumpCommandDown = input.Jump;
        if (ControllerOwnsControlledBodyPose && ControlledGuid == LocalPlayerGuid &&
            SelfMountDisplayId() != 0 &&
            input.Jump && !translating && _controller.Grounded)
        {
            input.Jump = false;
            input.Up = 0f;
            if (!_mountSpecialJumpDown && MathF.Abs(turn) <= 0.01f && _net?.MountSpecial() == true)
                _creatures?.TriggerMountFlourish(LocalPlayerGuid);
        }
        _mountSpecialJumpDown = jumpCommandDown;

        UpdateCastMovementInput(ControllerOwnsControlledBodyPose &&
            (translating || input.Jump));

        ApplyMountHandling();

        // The 0.75-depth swim line is a fraction of this controlled display's
        // own collision height, not the controller's human-sized debug capsule.
        float controlledCollisionHeight = CreatureVoiceCatalog.DefaultCollisionHeight;
        if (_entities.TryGet(ControlledGuid, out WorldEntity controlledDisplay))
        {
            float scale = MathF.Max(controlledDisplay.Scale, 0.01f);
            controlledCollisionHeight = _creatureVoices?.CollisionHeight(
                (uint)Math.Max(0, controlledDisplay.DisplayId), scale) ??
                CreatureVoiceCatalog.DefaultCollisionHeight * scale;
        }
        _controller.CollisionHeight = controlledCollisionHeight;

        _controller.ExternalWalkableSurfaceZ = null;
        _controller.LiquidSurfaceZ = null;
        if (TryGetBodyLiquidSurface(_controller.Position, out float movementLiquidZ, out _))
            _controller.LiquidSurfaceZ = movementLiquidZ;
        if (_controller.WaterWalking &&
            TryGetBodyLiquidSurface(_controller.Position, out float walkableLiquidZ, out _))
            _controller.ExternalWalkableSurfaceZ = walkableLiquidZ;

        bool movementWasGrounded = _controller.Grounded;
        bool movementWasFlying = _controller.Flying;
        bool movementWasSwimming = _controller.Swimming;
        float movementPreviousFallMs = _controller.FallTimeMs;
        Vector3 movementPreviousPosition = _controller.Position;
        long phaseStarted = Stopwatch.GetTimestamp();
        bool serverRideHeldByTacticalFreeze =
            UpdateServerRideTacticalFreeze(tacticalLiveAuthorshipBlocked);
        bool serverRideActive = serverRideHeldByTacticalFreeze ||
            (!tacticalLiveAuthorshipBlocked && UpdateServerRide());
        if (!serverRideActive && !controllerTacticalFrozen) _controller.Update(dt, input);
        UpdatePredictedBreath();
        ReconcileControlledTransportRider();
        ResolveRealPortalMovement(movementPreviousPosition);
        // The unit we drive is client-authoritative, so its ENTITY is the one thing the server
        // never updates for us. Publish every frame, not just at control hand-offs: anything
        // that reads the entity rather than the controller otherwise renders it standing still
        // where we picked it up — the selection ring is the visible one, sitting on the ground
        // behind you as you run off.
        SyncDrivenEntityToController();
        ObserveControlledHardLanding(
            movementWasGrounded, _controller.Grounded,
            movementWasFlying, _controller.Flying,
            movementPreviousPosition.Z, _controller.Position.Z);
        if (_net is { IsInWorld: true } && !controllerTacticalFrozen)
        {
            bool movementJumped = input.Jump && _controller.Velocity.Z > 0f &&
                (movementWasGrounded && !_controller.Grounded ||
                 movementWasSwimming && !_controller.Swimming);
            bool movementLanded = !movementWasGrounded && _controller.Grounded;
            bool movementStartedFalling = movementWasGrounded && !_controller.Grounded && !movementJumped;
            float fallMs = movementLanded
                ? movementPreviousFallMs + dt * 1000f
                : _controller.FallTimeMs;
            _movementSender.Update(
                _net, _controller, input, turn,
                movementJumped, movementLanded, movementStartedFalling,
                (uint)Math.Clamp(MathF.Round(fallMs), 0f, uint.MaxValue),
                _config.Movement.JumpVelocity,
                _movementRooted ? MovementFlags.Root : MovementFlags.None,
                MovementInfo.ClientUptimeMs() / 1000.0);
        }
        _movementMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        UpdateWorldResidency();

        // Portals (PLAN_13 stage 2b). AFTER residency, so the volume test runs
        // against the map we are actually on rather than the one we were on
        // when the frame began.
        UpdateAreaTriggers();
        _residencyMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // WoWee gives ready assets a small main-thread integration budget.
        // Alternate priority so neither queue starves, and never begin the
        // second GL upload/build after the first has consumed this frame.
        phaseStarted = Stopwatch.GetTimestamp();
        var preloadBudget = Stopwatch.StartNew();

        long subStarted = Stopwatch.GetTimestamp();
        DiscoverNextBackgroundTile(dt);
        _discoverMilliseconds = Stopwatch.GetElapsedTime(subStarted).TotalMilliseconds;

        subStarted = Stopwatch.GetTimestamp();
        QueueVisibleDoodadDemand(dt);
        // Server gameobjects (signs, mailboxes, chests) ride the same doodad
        // renderer as dynamic per-GUID placements, resynced every frame.
        UpdateGameObjectDoodads();
        UpdateGameObjectSounds();
        _doodadDemandMilliseconds = Stopwatch.GetElapsedTime(subStarted).TotalMilliseconds;

        // NOTE the budget only gates the SECOND WMO/doodad warm call. Those
        // legacy finalizers can still exceed it; foliage warming below is
        // deliberately non-blocking and does not consume that second slot.
        subStarted = Stopwatch.GetTimestamp();
        // Foliage warming is strictly non-blocking: this call starts CPU jobs or
        // adopts already-fenced shared-context uploads. It must run every frame
        // so a model discovered by last frame's scatter can make progress.
        _foliage?.WarmNextPreload();
        if (_preloadWmoFirst) _wmo?.WarmNextPreload();
        else _doodads?.WarmNextPreload();
        if (preloadBudget.Elapsed.TotalMilliseconds < 6)
        {
            if (_preloadWmoFirst) _doodads?.WarmNextPreload();
            else _wmo?.WarmNextPreload();
        }
        _warmMilliseconds = Stopwatch.GetElapsedTime(subStarted).TotalMilliseconds;
        _preloadWmoFirst = !_preloadWmoFirst;
        _preloadMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // Destination-scene streaming owns a separate residency set and its own
        // small finalisation budget; keep it out of the active doodad timing.
        UpdateRealPortals(dt);

        _walking = input.Walking;
        phaseStarted = Stopwatch.GetTimestamp();
        _character?.Update(dt, BuildUnitState());
        SampleMovementTrace(dt, input, turn);
        SampleCombatTrace(dt);
        AdvanceMovementSuiteAfterSample();
        _characterUpdateMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // The camera orbits the character's feet; Camera.EyeHeight does the rest. In the
        // Command View the target is the eased rig, or the locked primary (GameLoop.Control.cs).
        _window.Camera.Target = _freeView
            ? CommandViewCameraTarget(dt, _commandViewYawDelta)
            : _controller.Position;
        UpdateViewSubject();

        // 1.12 cameraSmoothStyle: command-word EDGES arm one cosine-smoothed return. This is not
        // the old per-moving-frame exponential chase. Either mouse-look button cancels the active
        // return, and far sight reads as Never while a remote subject is actually streamed. The
        // protected SUI free-view/possession camera remains outside this ordinary player law.
        if (_freeView)
        {
            _window.Camera.ResetFollow();
        }
        else
        {
            uint followCommand = 0;
            void FollowBit(bool set, uint bit)
            {
                if (set) followCommand |= bit;
            }

            FollowBit(_window.MouseRightDown, CameraFollowCommand.RightMouse);
            FollowBit(_window.MouseLeftDown, CameraFollowCommand.LeftMouse);
            FollowBit(bindingForward || _autoFollowGuid != 0, CameraFollowCommand.Forward);
            FollowBit(bindingBackward, CameraFollowCommand.Backward);
            FollowBit(bindingStrafeLeft, CameraFollowCommand.StrafeLeft);
            FollowBit(bindingStrafeRight, CameraFollowCommand.StrafeRight);
            FollowBit(bindingTurnLeft, CameraFollowCommand.TurnLeft);
            FollowBit(bindingTurnRight, CameraFollowCommand.TurnRight);
            FollowBit(_autorunToggled, CameraFollowCommand.Autorun);
            FollowBit(serverRideActive, CameraFollowCommand.Track);
            FollowBit(vanillaControlLocked, CameraFollowCommand.Fear);

            CameraFollowStyle ordinaryStyle = Settings.Controls.CameraFollowStyle;
            CameraFollowStyle trackingStyle = Settings.Controls.CameraFollowTrackingStyle;
            if (_window.Camera.AuthoredTarget is not null)
                ordinaryStyle = trackingStyle = CameraFollowStyle.Never;
            _window.Camera.AdvanceFollow(new CameraFollowInput(
                    new CameraFollowConfig(ordinaryStyle, trackingStyle,
                        Settings.Controls.CameraFollowYawSpeed),
                    _window.Camera.Yaw,
                    followCommand),
                dt,
                _window.MouseLeftDown || _window.MouseRightDown);
        }

        phaseStarted = Stopwatch.GetTimestamp();
        ResolveCameraCollision(dt);
        UpdateScopedView();
        _cameraCollisionMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // Portal membership must use the exact camera that this frame renders,
        // after player movement, target following and camera collision. The old
        // placement above movement made every doorway transition one frame stale.
        var portalEye = _window.Camera.Position;
        // Divinity-style cutaway: hand the renderer the commanded toon's position
        // (or null) BEFORE the cell update resolves this frame's seeds.
        Vector3? cutawaySubject = FreeViewCutawaySubject();
        _wmo?.SetCutawaySubject(cutawaySubject, cutawaySubject is Vector3 cutXy
            ? _terrain?.SampleHeight(cutXy.X, cutXy.Y) : null);
        Vector3? primarySubject = CommandViewPrimarySubject();
        Vector3? cutPlaneSubject = Settings.Controls.CommandViewCutPlane ? primarySubject : null;
        _wmo?.SetCutPlaneSubject(cutPlaneSubject, Settings.Controls.CommandViewCutHeight,
            cutPlaneSubject is Vector3 cutFeet ? _terrain?.SampleHeight(cutFeet.X, cutFeet.Y) : null);
        // Party sight (World/PartySight.cs): the subject's own rooms must draw for the cut to
        // open onto them. Same feed point as the roof plane, before the cell update resolves.
        // The proximity roof cut needs the same "draw the rooms near the unit" set: a cave mouth
        // has no interior cell to flood from, so the cut subject feeds it when party sight is off.
        Vector3? sightFeet = PartySightEye() is Vector3 sightEye
            ? sightEye - new Vector3(0f, 0f, PartySightPass.EyeHeight) : cutPlaneSubject;
        _wmo?.SetPartySightSubject(sightFeet,
            sightFeet is Vector3 sf ? _terrain?.SampleHeight(sf.X, sf.Y) : null);
        _wmo?.UpdateCameraCell(portalEye, _terrain?.SampleHeight(portalEye.X, portalEye.Y));
        // The cut resolved by the cell update is one world-space rule shared by three renderers.
        WorldCut? activeCut = _wmo?.ActiveCut;
        if (_terrain is not null) _terrain.Cut = activeCut;
        if (_doodads is not null) _doodads.Cut = activeCut;
        if (_wmo is not null && _doodads is not null)
        {
            _wmo.SightTargets.Clear();
            _doodads.SightTargets.Clear();
            CollectCommandViewSightTargets(_window.Camera.Position, _wmo.SightTargets);
            _doodads.SightTargets.AddRange(_wmo.SightTargets);
            // The camera-side slice around the primary (WorldSlice): part of "Cut what hides the
            // party", and it needs the roof cut's footprint, so it is off whenever that is.
            _wmo.Slice = activeCut is not null && Settings.Controls.CommandViewSightCut &&
                primarySubject is Vector3 sliceFeet
                    ? WorldSlice.From(_window.Camera.Position, _window.Camera.Forward, sliceFeet)
                    : null;
            _doodads.Slice = _wmo.Slice;
            if (_terrain is not null)
            {
                // The terrain leg of the roof cut stops at the commanded unit's depth: the hill
                // between the camera and the unit opens, the hill behind the unit stays, so the
                // cut is never a window onto the world beyond (owner, 2026-09-02). Terrain gets
                // no sight tunnels: the boom march keeps the camera-to-rig line above ground.
                // ...but the whole cut FOOTPRINT must open, however the camera sits: zoomed out
                // at a low angle the far half of the bubble is well past the unit's own distance
                // and stayed roofed ("if I zoom in, works" - owner, 2026-09-03). The cap is the
                // unit's distance plus the footprint's reach from the unit, not a fixed 6 yd.
                float footprintReach = 6f;
                if (activeCut is WorldCut reachCut && cutPlaneSubject is Vector3 reachSubject)
                {
                    float dx = MathF.Max(reachCut.Max.X - reachSubject.X, reachSubject.X - reachCut.Min.X);
                    float dy = MathF.Max(reachCut.Max.Y - reachSubject.Y, reachSubject.Y - reachCut.Min.Y);
                    footprintReach = MathF.Min(MathF.Sqrt(dx * dx + dy * dy), 45f) + 4f;
                }
                _terrain.CutMaxDistance = cutPlaneSubject is Vector3 cutSubject
                    ? Vector3.Distance(cutSubject, _window.Camera.Position) + footprintReach
                    : float.MaxValue;
            }
            _doodads.CanopyCutHeight = Settings.Controls.CommandViewCutHeight;
        }

        bool weatherCameraInterior = _wmo?.CameraGroup is { IsExterior: false };
        bool weatherExteriorVisible = _wmo?.CameraExteriorPortalVisible ?? true;
        _weatherPrecipitation?.Update(dt, _weatherVisual, portalEye,
            _controller.Position, _window.Camera.FlatForward, _controller.PlanarSpeed,
            WeatherPrecipitationLaw.IndoorBlocked(
                weatherCameraInterior, weatherExteriorVisible), WeatherGroundHeight);

        // Target picking uses the final camera and final collision world for this frame.
        UpdateTargeting();
        UpdateCombatFeedback(dt);
        UpdateDuel();
        UpdateSpellPresentation();
        UpdateCreatorSpellLoop();
        UpdateCreatorLocationPersist();

        // Alt+Enter's live fullscreen toggle must stick across sessions - but the
        // watcher must not fire during BOOT, where the window reports windowed
        // for a few frames while GLFW is still entering fullscreen (a naive
        // mismatch-save clobbered the setting back to false). It arms only once
        // the observed state has agreed with the setting, then persists real
        // transitions.
        // A scripted probe run is not the user's session: never persist display
        // state from it (a probe boot clobbered Fullscreen back to false once).
        // ...and an ALT-TAB is not a transition either. A backgrounded fullscreen window can
        // report windowed while it is not the foreground app, which is the same false reading the
        // boot guard above already exists to survive - it just arrives later, when the watcher is
        // armed and willing to persist it. Requiring focus keeps the observation to moments the
        // window state actually means something; Alt+Enter is always taken while focused, so the
        // gesture this watcher exists for is unaffected.
        bool fullscreenNow = _window.Fullscreen;
        if (ProbeSpec is not null || !_window.IsFocused) { }
        else if (_observedFullscreen is null)
        {
            if (fullscreenNow == Settings.Display.Fullscreen) _observedFullscreen = fullscreenNow;
        }
        else if (fullscreenNow != _observedFullscreen)
        {
            _observedFullscreen = fullscreenNow;
            if (Settings.Display.Fullscreen != fullscreenNow)
            {
                Settings.Display.Fullscreen = fullscreenNow;
                SettingsFile?.Save();
            }
        }

        _updateMilliseconds = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
    }

    private void UpdateMovementSenderDuringLoad()
    {
        if (_net is not { IsInWorld: true } net || _controller is null) return;

        MovementInput idle = default;
        _movementSender.Update(
            net, _controller, idle, 0f,
            jumped: false, landed: false, startedFalling: false,
            (uint)Math.Clamp(MathF.Round(_controller.FallTimeMs), 0f, uint.MaxValue),
            _config.Movement.JumpVelocity,
            _movementRooted ? MovementFlags.Root : MovementFlags.None,
            MovementInfo.ClientUptimeMs() / 1000.0);
    }

    private void DiscoverNextBackgroundTile(float dt)
    {
        if (_terrain is null || _adts is null || _assetWorkers is null) return;

        if (_backgroundAdtLoad is { IsCompleted: true } completed)
        {
            try { completed.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[preload-discovery] tile [{_backgroundAdtTile.col}," +
                                  $"{_backgroundAdtTile.row}] failed - {ex.Message}");
            }

            _terrain.QueuePreload([_backgroundAdtTile], _adts);
            _wmo?.QueuePreloadForTiles([_backgroundAdtTile], _adts);
            Console.WriteLine($"[preload-discovery] tile [{_backgroundAdtTile.col}," +
                              $"{_backgroundAdtTile.row}], " +
                              $"{_backgroundDiscovery.Count} outer tile(s) remaining");
            _backgroundAdtLoad = null;
        }

        if (_backgroundAdtLoad is not null || _backgroundDiscovery.Count == 0) return;
        _backgroundDiscoveryDelay -= dt;
        if (_backgroundDiscoveryDelay > 0f) return;
        _backgroundDiscoveryDelay = 0.05f;

        _backgroundAdtTile = _backgroundDiscovery.Dequeue();
        _backgroundAdtLoad = _adts.QueueLoad(
            _backgroundAdtTile.col, _backgroundAdtTile.row, _assetWorkers);
    }

    private void QueueVisibleDoodadDemand(float dt)
    {
        if (!_demandStreamDoodads || _doodads is null || _terrain is null ||
            _adts is null || _controller is null || _residentCentre is null) return;

        _doodadDemandDelay -= dt;
        if (_doodadDemandDelay > 0f) return;
        _doodadDemandDelay = 0.25f;

        var centre = new Vector2(_controller.Position.X, _controller.Position.Y);

        // Nothing can have changed if no model is still in flight to acquire
        // placements AND the player has not moved far enough to bring anything
        // new into range. Skipping is not an optimization here - running was
        // the bug. Back the interval off while idle so a stationary player
        // costs nothing at all.
        bool streamingInFlight = _doodads.PendingPreloads > 0;
        if (!streamingInFlight && _lastDemandCentre is { } last &&
            Vector2.DistanceSquared(centre, last) < DemandRescanDistance * DemandRescanDistance)
        {
            _doodadDemandDelay = 1.0f;
            return;
        }

        float radius = DoodadDemandRadius;
        _doodads.QueuePreloadForTiles(_terrain.LoadedTiles, _adts, centre, radius);
        if (_wmo is not null)
            _doodads.QueuePreloadModels(
                _wmo.EnumerateDoodads(centre, radius)
                    .OrderBy(d => Vector2.DistanceSquared(
                        new Vector2(d.Transform.M41, d.Transform.M42), centre))
                    .Select(d => d.ModelPath));

        _doodads.DrainNewlyReadyModelPaths(_newDoodadModels);
        bool movedEnough = _lastDemandCentre is not { } previous ||
            Vector2.DistanceSquared(centre, previous) >=
                DemandRescanDistance * DemandRescanDistance;
        if (_newDoodadModels.Count == 0 && !movedEnough) return;

        IReadOnlySet<string>? modelFilter = null;
        if (!movedEnough)
        {
            _newDoodadModelKeys.Clear();
            foreach (string path in _newDoodadModels)
                _newDoodadModelKeys.Add(DoodadRenderer.ModelCacheKey(path));
            modelFilter = _newDoodadModelKeys;
        }
        _lastDemandCentre = centre;

        // A model that completed since the previous pass can now acquire its
        // placements. Both outdoor and WMO placement keys are idempotent.
        int placementsBefore = _doodads.InstanceCount;
        PopulateDoodads(_residentCentre.Value, reportDiagnostics: false, modelFilter);

        // Newly-resident doodads are not yet in the collision world (it was built
        // at startup / last tile-cross, before they streamed in). Flag it so the
        // Update loop folds them in. Without this, doodad collision is missing
        // for everything that streams in after the initial build.
        if (_doodads.InstanceCount != placementsBefore && ClientGeometryCollision)
        {
            _doodadCollisionPending += Math.Abs(_doodads.InstanceCount - placementsBefore);
            _doodadCollisionDirty = true;
        }
    }

    // Vantage capture / apply / echo moved to Program.DevTools.cs
    // (developer-only layer).

    /// <summary>
    /// Identify the WMO group(s) under a screen pixel. Builds a pinhole ray from
    /// the camera basis (no projection-matrix inversion, so it can't be tripped
    /// by the GL clip-Z convention) and asks the WMO renderer what boxes it hits.
    /// Result goes to the HUD and the console so it can be copied out.
    /// </summary>
    private void ScreenPick(Vector2 screen)
    {
        if (_wmo is null) return;

        var size = _window.FramebufferSize;
        if (size.X <= 1f || size.Y <= 1f) return;

        // Pixel -> normalised device coords. Screen Y grows down; NDC Y grows up.
        float ndcX = screen.X / size.X * 2f - 1f;
        float ndcY = 1f - screen.Y / size.Y * 2f;

        var cam = _window.Camera;
        var fwd = cam.Forward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        var up = Vector3.Cross(right, fwd);
        float tanHalf = MathF.Tan(cam.FieldOfViewDegrees * MathF.PI / 180f * 0.5f);
        var dir = Vector3.Normalize(
            fwd + right * (ndcX * tanHalf * cam.AspectRatio) + up * (ndcY * tanHalf));

        _lastPick = _wmo.PickGroups(cam, cam.Position, dir);

        Console.WriteLine($"[pick] {_lastPick.Count} WMO group(s) under cursor (near->far):");
        foreach (var h in _lastPick)
            Console.WriteLine(
                $"[pick]   {h.Path} [{h.GroupIndex}] '{h.Name}' 0x{h.Flags:X8} " +
                $"{(h.Interior ? "INT" : "ext")}{(h.Shell ? " LOD" : "")} " +
                $"v={h.VertexCount} {h.Distance:F0}yd -> {h.Reason}");
    }

    /// <summary>
    /// What the unit renderer needs to know about the player, in the same shape
    /// it will need for every other unit once packets arrive.
    /// </summary>
    private CharacterRenderer.UnitState BuildUnitState(bool includeAuraVisual = false)
    {
        AuraVisualState? aura = includeAuraVisual &&
            _entities.TryGet(ControlledGuid, out WorldEntity visualUnit)
                ? visualUnit.AuraVisual : null;
        return new()
        {
        Guid = ControlledGuid,
        Position = _controller?.Position ?? Vector3.Zero,
        Yaw = _controller?.Yaw ?? 0f,
        Grounded = _controller?.Grounded ?? true,
        VerticalVelocity = _controller?.Velocity.Z ?? 0f,
        Swimming = _controller?.Swimming ?? false,
        SwimPitch = _controller?.SwimPitch ?? 0f,
        FallTimeMs = _controller?.FallTimeMs ?? 0f,
        Walking = _walking,
        Flying = _controller?.Flying ?? false,
        Engaged = _net is not null && _combat.IsEngaged(ControlledGuid),
        StandState = _entities.TryGet(ControlledGuid, out WorldEntity poseUnit)
            ? poseUnit.Fields.UnitStandState : (byte)0,
        EmoteState = _entities.TryGet(ControlledGuid, out WorldEntity emoteUnit)
            ? emoteUnit.Fields.NpcEmoteState : 0,
        FreezePose = _iceBlockFrozen || aura?.Frozen == true ||
            TacticalFreezePoseLaw.IsFrozen(ControlledGuid),
        ApplyBodyVisual = aura is not null,
        BodyAlpha = aura?.Alpha ?? 1f,
        BodyTint = aura?.Tint ?? Vector3.One,

        Forward = _moveForward,
        Strafe = _moveStrafe,
        Speed = _controller?.PlanarSpeed ?? 0f,
        Steering = _steering,
        HasIntent = _controller is not null,
        // Gated on ServerRideMayOwnController for the same reason the ride itself is: in SUI
        // modes a spline addressed to the session body must not drive a controller that is
        // currently a possessed bot or the detached camera rig, so it must not animate one
        // either. Same source the mount gait already reads.
        CarriedSpeed = ServerRideMayOwnController ? _serverRideSpline?.AverageSpeed ?? 0f : 0f,
        };
    }

    /// <summary>
    /// Keep the camera out of the world.
    ///
    /// Two probes from the eye point outward along the orbit direction: a
    /// collision raycast for buildings and trees, and a march against the
    /// terrain height grid. Whichever is closer sets the distance, floored at
    /// MinDistance.
    ///
    /// The terrain part has to be a march rather than a single test at the
    /// camera position, because the camera can clear a ridge while the straight
    /// line between it and the character passes through it — you would see the
    /// character through the hill.
    ///
    /// MinDistance (1.5) times sin(PitchLimit) is about 1.49, comfortably under
    /// EyeHeight, so even fully pitched down the pulled-in camera stays above
    /// the character's feet. Steep terrain immediately behind you can still
    /// clip; so does the real client.
    /// </summary>
    private void ResolveCameraCollision(float dt)
    {
        var cam = _window.Camera;

        // Command View: the rig is a ghost (FlyCollide is hard-false) and the boom ignores
        // buildings and trees - a boom that ducked trees behind the rig pushed the camera "in
        // front" while panning, and with the cut plane up it ducked under a roof that is no
        // longer in the picture. TERRAIN still stops it (owner, 2026-09-02): a boom that put the
        // eye inside a hillside turned the hill into a window onto the world beyond, because a
        // height field has no inside to draw. The march below pulls the eye in along the boom
        // until it clears the ground, so the rig stays framed and the shot simply tightens.
        // A rig under the terrain (a cave) keeps the overhead exemption below.
        if (!_config.Camera.Collision)
        {
            cam.EffectiveDistance = cam.Distance;
            return;
        }

        var eye = cam.EyeTarget;
        var dir = cam.OrbitDirection;
        float clearance = _config.Camera.Clearance;
        float allowed = cam.Distance;

        if (!_freeView && _collision is { IsEmpty: false })
        {
            var hit = _collision.Raycast(eye, dir, cam.Distance + clearance);
            if (hit is not null) allowed = MathF.Min(allowed, hit.Value.Distance - clearance);
        }

        // ADT terrain is only a camera obstacle while the target is on its
        // outdoor side. Interiors such as Blackrock Mountain sit beneath that
        // height field: at the BRM entrance the eye is near Z=168 and the ADT
        // mountain shell is near Z=275. Treating that overhead shell as ground
        // makes the first march sample collide and forces EffectiveDistance to
        // FirstPersonDip forever, even though wheel input still changes the
        // requested Distance. WMO collision above remains active and supplies
        // the real interior walls.
        var terrain = _terrain;
        float? terrainAtEye = terrain?.SampleHeight(eye.X, eye.Y);
        float terrainOverheadSlack = _controller?.UndergroundSlack ?? 1f;
        bool terrainIsOverhead = Camera.TerrainIsOverhead(
            eye.Z, terrainAtEye, terrainOverheadSlack);

        if (terrain is not null && !terrainIsOverhead)
        {
            const int steps = 10;
            float step = cam.Distance / steps;

            for (int i = 1; i <= steps; i++)
            {
                float d = step * i;
                if (d > allowed) break;

                var p = eye + dir * d;
                float? ground = terrain.SampleHeight(p.X, p.Y);
                if (ground is null) continue;

                if (p.Z < ground.Value + clearance)
                {
                    allowed = d - step;
                    break;
                }
            }
        }

        // The collision pass may collapse the boom PAST the user zoom floor —
        // MinDistance is a zoom-wheel convention, not a physics licence. Clamping
        // to it here parked the camera INSIDE any wall closer than 1.5 yd behind
        // the character, which is exactly "press your back to a building and see
        // through it". Vanilla answers the cornered case by dipping into first
        // person: the boom collapses toward the head, and the body render is
        // suppressed below FirstPersonBodyHide at its call site.
        allowed = Math.Clamp(allowed, ScopedViewLaw.FirstPersonDistance, cam.Distance);

        // In immediately, out gradually.
        cam.EffectiveDistance = allowed < cam.EffectiveDistance
            ? allowed
            : MathF.Min(allowed, cam.EffectiveDistance + _config.Camera.RestoreSpeed * dt);
    }

    /// <summary>Boom length the camera-collision pass may collapse to when cornered
    /// (just clear of the 0.1 near plane). The zoom wheel still stops at MinDistance.</summary>
    /// <summary>Below this boom length the first-person body is not drawn — the
    /// camera is effectively inside the character (vanilla hides the model at
    /// full zoom-in for the same reason).</summary>
    private const float FirstPersonBodyHide = 0.6f;

    public void Render(float dt)
    {
        long renderSpanStarted = Stopwatch.GetTimestamp();
        BeginPainterlyComparisonFrame();

        // The loading art is fully exclusive until Fade begins. Do not spend
        // frame time drawing a world that an alpha-1 curtain will discard. The
        // first Fade frame renders the world while alpha is still 1, before the
        // following update can lower it, preventing a black/half-scene reveal.
        if (_worldLoading && _loadPhase != WorldLoadPhase.Fade)
        {
            _worldRenderMilliseconds = 0;
            _foliageRenderMilliseconds = 0;
            _foliageScatterMilliseconds = 0;
            _foliageDrawMilliseconds = 0;
            _particleSimulateMilliseconds = 0;
            _particleDrawMilliseconds = 0;
            _characterRenderMilliseconds = 0;
            _creatureRenderMilliseconds = 0;
            _selectionRenderMilliseconds = 0;
            _spellEffectRenderMilliseconds = 0;
            _liquidRenderMilliseconds = 0;
            _debugRenderMilliseconds = 0;
            DrawLoadingScreen();
            _renderSpanMilliseconds = Stopwatch.GetElapsedTime(renderSpanStarted).TotalMilliseconds;
            return;
        }

        bool stagedLoadWarmup = _worldLoading &&
                                _loadPhase == WorldLoadPhase.Fade &&
                                _loadCurtainAlpha >= 1f &&
                                _loadFadeWarmStage < LoadLayerWarmStageCount;
        bool WarmStage(int stage) => !stagedLoadWarmup || _loadFadeWarmStage == stage;

        ApplyAtmosphere();
        // Creator X-ray owns the layer switches while active: renderer enables,
        // the black background (overriding the SkyColor just set above), and
        // acceptance of a finished off-thread vmap build.
        if (_xrayActive) ApplyXrayLayers();
        // A prepared portal owns a fully isolated destination scene. Render it
        // before the source world so the pass can restore GL/atmosphere state,
        // then composite its completed texture later through the aperture.
        RenderRealPortalPreview(dt);
        _gpuProfiler?.BeginFrame();

        long worldStarted = Stopwatch.GetTimestamp();

        // Sky first, depth writes off, so everything else draws over it. This
        // replaces nothing yet - ClientWindow still clears to the fog colour, so
        // if the sky pass is disabled the old flat behaviour is exactly what is
        // left (PLAN_09 §1.1: do not remove the clear-colour trick before the
        // replacement is proven, or the far clip becomes a visible edge).
        if (WarmStage(0)) _sky?.Render(_window.Camera, _atmosphere, _weatherVisual.StormBlend);

        // The zone skybox model (PLAN_18 Phase 2): over the gradient/clouds, before
        // the world. The active model follows the dominant LightParams' skybox id
        // (resolved in UpdateExteriorLighting), or a dev override; SetModel no-ops
        // when the path is unchanged.
        if (WarmStage(0) && _skybox is not null && _mpq is not null)
        {
            _skybox.SetModel(_mpq, _skybox.ForceModelPath ?? _exteriorLight.SkyboxPath(_activeSkyboxId));
            _skybox.Render(_window.Camera);
        }

        // Party sight cube + pre-pass (World/PartySight.cs), before the first world pass that
        // consults them and after the portal preview, whose separate scene must not see them.
        UpdatePartySight();

        _gpuProfiler?.Begin(GpuFrameProfiler.Pass.Terrain);
        BeginXrayTerrainWireframe();
        if (WarmStage(0)) _terrain?.Render(_window.Camera);
        else _terrain?.NoteNotRendered();
        EndXrayTerrainWireframe();
        _gpuProfiler?.End(GpuFrameProfiler.Pass.Terrain);

        UpdatePortalFillLight();

        _gpuProfiler?.Begin(GpuFrameProfiler.Pass.Wmo);
        if (_wmo is not null) _wmo.OcclusionWorld = _collision;
        if (WarmStage(1)) _wmo?.Render(_window.Camera);
        else _wmo?.NoteNotRendered();
        _gpuProfiler?.End(GpuFrameProfiler.Pass.Wmo);

        _gpuProfiler?.Begin(GpuFrameProfiler.Pass.Doodads);
        if (WarmStage(2)) _doodads?.Render(_window.Camera);
        else _doodads?.NoteNotRendered();
        _gpuProfiler?.End(GpuFrameProfiler.Pass.Doodads);
        // The three passes above are the only consumers; nothing later may read the verdict.
        _partySight?.EndFrame();
        // A roof cut that opened onto a hole in the world (a cave in a terrain hole) shows sky
        // through the wall; paint the cut volume dark wherever nothing else was drawn.
        if (_wmo?.ActiveCut is WorldCut voidCut && CommandViewPrimarySubject() is Vector3 voidFeet)
            _partySight?.DrawCutVoid(_window.Camera, voidCut, voidFeet.Z - 8f, _atmosphere.FogColor * 0.35f);

        // Ground-effect foliage: scatter near the camera (throttled internally),
        // then draw it into the opaque world pass so terrain/water depth-interact.
        long foliageStarted = Stopwatch.GetTimestamp();
        if (WarmStage(0) && _foliage is not null && _adts is not null && _terrain is not null)
        {
            _foliage.Time += dt;
            _foliage.Scatter(_window.Camera, _adts, _terrain.LoadedTiles, _terrain);
            _foliage.Render(_window.Camera);
        }
        _foliageRenderMilliseconds = Stopwatch.GetElapsedTime(foliageStarted).TotalMilliseconds;

        // Scatter and Render are separate jobs on separate schedules - Render
        // every frame and cheap, Scatter about once a second while walking and
        // a full rebuild. Averaged together they read as a small constant cost
        // and hide a periodic spike, so they are now reported apart. The
        // combined number stays as the bracket that must still sum.
        // ThisFrame, not the sticky value. See FoliageRenderer's note: feeding
        // the sticky one to the phase split made DominantPhase call every hitch
        // "foliage-rescatter" on the strength of a number bigger than the frame.
        _foliageScatterMilliseconds = _foliage?.ScatterMillisecondsThisFrame ?? 0;

        _foliageDrawMilliseconds = _foliage?.DrawMilliseconds ?? 0;

        _worldRenderMilliseconds = Stopwatch.GetElapsedTime(worldStarted).TotalMilliseconds;

        // WMO.Render above completed this frame's room PVS. Reuse that verdict for every
        // visual unit consumer below instead of making each subsystem walk the global store.
        BuildVisibleWorldUnits();

        long characterStarted = Stopwatch.GetTimestamp();
        _gpuProfiler?.Begin(GpuFrameProfiler.Pass.Character);
        // Before the body, because the steed's saddle IS the body's transform. Deliberately
        // not behind the first-person body hide below: ride into the camera and the mount
        // stays on screen, it is the body underneath you that goes.
        DrawSelfMount(WarmStage(3));
        // Not in the free view: there the rig is a camera, not a body, and the driven unit
        // draws from the entity stream where it actually stands (RenderSelfGuid goes 0).
        // Suppressed HERE rather than by clearing _character.Enabled, because Enabled also
        // gates CharacterRenderer.Render — and the portrait booth goes through that same
        // method, so hiding the body that way silently froze the player-frame portrait on
        // whatever face was baked last.
        _character?.BeginItemGlowFrame();
        _creatures?.BeginItemGlowFrame();
        if (WarmStage(3) && _character is not null && _controller is not null && !_freeView &&
            _window.Camera.EffectiveDistance > FirstPersonBodyHide)
            _character.Render(_window.Camera, BuildUnitState(includeAuraVisual: true));
        _gpuProfiler?.End(GpuFrameProfiler.Pass.Character);
        _characterRenderMilliseconds = Stopwatch.GetElapsedTime(characterStarted).TotalMilliseconds;

        // Streamed creatures/NPCs (networked). Opaque M2s like the player, so they
        // belong here in the opaque pass, before transparent water/particles blend.
        long creatureStarted = Stopwatch.GetTimestamp();
        // Finish advances to Fade during Update, so this first Fade render still
        // has an alpha-1 curtain. Do not stack the first synchronous creature
        // adoption on the same frame as Finish and the world's first render;
        // the following Fade update lowers alpha and its otherwise-idle frame
        // carries the one-model curtained budget.
        if (WarmStage(4) &&
            (!_worldLoading || _loadCurtainAlpha < 1f || stagedLoadWarmup)) DrawCreatures();
        else _creatures?.NoteKnownNotDrawn(_entities);
        if (WarmStage(4)) DrawUnitShadows();
        _creatureRenderMilliseconds = Stopwatch.GetElapsedTime(creatureStarted).TotalMilliseconds;
        NoteLoadCreatureDraw(_creatures?.DrawnLastFrame ?? 0);

        if (WarmStage(5)) DrawFishingLines();

        long selectionStarted = Stopwatch.GetTimestamp();
        if (WarmStage(5)) DrawSelectionRing();
        _selectionRenderMilliseconds = Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds;

        // Spell particles need this frame's published unit/effect skeletons before they simulate.
        // Run them before the spell-mesh pass so geometry-model particles join the same opaque /
        // transparent M2 material ordering as ordinary kit and missile meshes.
        double spellNow = MovementInfo.ClientUptimeMs() / 1000.0;
        if (_spellEffects is not null)
        {
            IEnumerable<ItemGlowPlacement> itemGlows =
                _character?.ItemGlowPlacements ?? Array.Empty<ItemGlowPlacement>();
            if (_creatures is not null)
                itemGlows = itemGlows.Concat(_creatures.ItemGlowPlacements);
            _spellEffects.SyncItemGlows(itemGlows, spellNow);
        }
        IEnumerable<CarriedLightPlacement> carriedLights =
            _character?.CarriedLightPlacements ?? Array.Empty<CarriedLightPlacement>();
        if (_creatures is not null)
            carriedLights = carriedLights.Concat(_creatures.CarriedLightPlacements);
        CarriedLightFrame.Commit(carriedLights, _window.Camera.Position);
        if (WarmStage(5) && _spellParticles is not null && _spellEffects is not null)
        {
            var eye = _window.Camera.Position;
            _spellParticles.Simulate(dt, eye, _spellEffects.EmitterInstances(
                spellNow, SpellEffectUnitPose, _spellFxBillboardJointPoseB,
                eye, _window.Camera.Forward), SpellParticleGroundHeight);
        }

        long spellEffectStarted = Stopwatch.GetTimestamp();
        if (WarmStage(5) && _spellEffects is not null && _spellEffectMeshes is not null)
        {
            // Ground decals follow ADT terrain and indoor WMO collision floors.
            _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
            IEnumerable<SpellMeshDraw> spellMeshes = _spellEffects.MeshInstances(
                spellNow, SpellEffectUnitPose);
            if (_spellParticles is not null)
                spellMeshes = spellMeshes.Concat(_spellParticles.GeometryInstances());
            spellMeshes = spellMeshes.Concat(QuestMarkerMeshInstances(spellNow));
            _spellEffectMeshes.Render(_window.Camera, spellMeshes, SpellGroundHeight);
            _spellEffectMeshes.RenderWorldBillboards(_window.Camera, RaidMarkerBillboards());
            // Armed location-target reticle: the rune circle follows the cursor and spans the
            // largest populated Spell.dbc effect radius. The compatibility fallback is reached
            // only by placement spells whose effect lanes author no radius at all.
            if (_groundCastSpell != 0 && _groundCursorPoint is { } reticle &&
                _spellCatalog?.TryGet(_groundCastSpell, out SpellInfo groundSpell) == true)
                _spellEffectMeshes.RenderTargetingMarker(_window.Camera, reticle,
                    _spellCatalog.TargetingRadius(groundSpell));
        }
        // CRPG free-view ground FX: selection rings + move markers share the decal machinery
        // and depth-test against the units drawn above, so rings tuck behind the models.
        if (WarmStage(5) && _spellEffectMeshes is not null)
            RenderRtsGroundFx();
        // NPC dev window (Ctrl+N): aggro discs + through-wall aggro beams. Same decal
        // machinery and bias as the RTS rings; no-op while the window is closed.
        if (WarmStage(5))
            RenderDevOverlays3D();
        // Encounter Lab footprints: same decal path, same depth bias, same no-op
        // rule while the window is closed.
        if (WarmStage(5))
            RenderEncounterLab3D();
        if (WarmStage(5) && _spellEffects is not null && _spellRibbons is not null)
            _spellRibbons.Render(_window.Camera, _spellEffects.RibbonInstances(
                spellNow, SpellEffectUnitPose), _spellFxBillboardJointPoseB);
        if (WarmStage(5) && _spellChainBeams is not null && _spellChainBeamRenderer is not null)
            _spellChainBeamRenderer.Render(_window.Camera, spellNow,
                _spellChainBeams.Snapshot(spellNow, SpellEffectUnitPose), SpellEffectUnitPose);
        _spellEffectRenderMilliseconds = Stopwatch.GetElapsedTime(spellEffectStarted).TotalMilliseconds;

        // Transparent particles are simulated after the unit draws have
        // published this frame's skeletons, then drawn after every opaque unit
        // and effect mesh has populated depth. This is both attachment-correct
        // and the intended transparent ordering.
        // Spell-effect emitters are the primary visible content of most spell
        // visuals, so they must not be coupled to the doodad renderer. Simulate
        // whenever particles exist, gathering doodad emitters only if that
        // renderer is present; otherwise spell particles vanish whenever doodads
        // are off at startup or DoodadRenderer construction failed.
        // Doodad/world particles go through the shared (portal-tuned) renderer.
        // Spell-effect particles go through the SEPARATE benilla-faithful
        // SpellParticleSystem (World/Spells/) so no portal tuning ever touches them.
        if (WarmStage(5) && _particles is not null)
        {
            var eye = _window.Camera.Position;
            if (_doodads is not null)
                _particles.Simulate(dt, eye,
                    _doodads.EmitterInstances(eye, _particles.SimulationDistance));
            _particles.Render(_window.Camera);
            _particleSimulateMilliseconds = _particles.SimulateMilliseconds;
            _particleDrawMilliseconds = _particles.DrawMilliseconds;
        }
        if (WarmStage(5) && _spellParticles is not null && _spellEffects is not null)
        {
            // TEMP diagnostic: mute spell particles to expose the effect-mesh layer alone.
            if (Environment.GetEnvironmentVariable("MSUI_MUTE_SPELL_PARTICLES") is null)
                _spellParticles.Render(_window.Camera);
        }

        // Weather is late transparent world geometry: drops depth-test against
        // the completed opaque scene, and its ground layer shares the collision
        // floor/roof verdict used by spell particles. Indoor gating freezes both
        // simulation and draw; an exterior group in the camera portal PVS keeps
        // the storm visible through a doorway.
        if (WarmStage(5) && _weatherPrecipitation is not null)
            _weatherPrecipitation.Render(_window.Camera, _atmosphere.FogColor,
                _atmosphere.FogStart, _atmosphere.FogEnd);

        // Water draws AFTER the character, on purpose. It tests depth but does not
        // write it, so a surface in front of a submerged character blends over him
        // (you see the waterline climb his body) while the near bank still occludes
        // it. Then, if the camera eye itself is below a water surface, tint the
        // whole screen so it reads as being underwater.
        long liquidStarted = Stopwatch.GetTimestamp();
        if (WarmStage(5) && _liquid is not null)
        {
            _liquid.Time += dt;

            // Build-5875 CWater0Ripple input. Pass the true feet-to-surface depth
            // and the display's collision height: the reference emits while
            // wading AND surface swimming, stopping only after a real dive below
            // two body heights.
            _remoteWakeSampledLastFrame = 0;

            if (_liquid.Enabled && _liquid.WakeEnabled)
            {
                _liquid.BeginWakeFrame();
                if (TryGetControlledBodyPose(out WorldBodyPose wakeBody))
                {
                    var feet = wakeBody.Position;
                    float? waterDepth =
                        TryGetBodyLiquidSurface(feet, out float wakeZ, out _, waterOnly: true) &&
                        float.IsFinite(wakeZ)
                            ? wakeZ - feet.Z
                            : null;
                    float renderScale = 1f;
                    float collisionHeight = CreatureVoiceCatalog.DefaultCollisionHeight;
                    if (_entities.TryGet(ControlledGuid, out WorldEntity wakeEntity))
                    {
                        renderScale = MathF.Max(wakeEntity.Scale, 0.01f);
                        collisionHeight = _creatureVoices?.CollisionHeight(
                            (uint)wakeEntity.DisplayId, renderScale) ??
                            CreatureVoiceCatalog.DefaultCollisionHeight * renderScale;
                    }

                    _liquid.UpdateWake(feet, wakeBody.Orientation, dt,
                        waterDepth, collisionHeight, renderScale);
                }

                // Remote wakes consume the same admitted list as bodies, shadows and names.
                // The previous path queried every known unit in a global WMO; UBRS measured
                // 224 triangle probes per frame for three nearby models.
                foreach (WorldEntity foamUnit in _visibleWorldUnits)
                {
                    if (foamUnit.Guid == ControlledGuid || foamUnit.DisplayId <= 0) continue;
                    Vector3 feet = foamUnit.Position;
                    float renderScale = MathF.Max(foamUnit.Scale, 0.01f);
                    _remoteWakeSampledLastFrame++;
                    float? waterDepth =
                        TryGetBodyLiquidSurface(feet, out float foamSurfaceZ, out _, waterOnly: true) &&
                        float.IsFinite(foamSurfaceZ)
                            ? foamSurfaceZ - feet.Z
                            : null;
                    float collisionHeight = _creatureVoices?.CollisionHeight(
                        (uint)foamUnit.DisplayId, renderScale) ??
                        CreatureVoiceCatalog.DefaultCollisionHeight * renderScale;
                    _liquid.UpdateOtherWake(foamUnit.Guid, feet, foamUnit.Orientation, dt,
                        waterDepth, collisionHeight, renderScale);
                }
                _liquid.EndWakeFrame();
            }
            else
                _liquid.ClearWake();

            if (_config.DevTools && ++_visualUnitAdmissionLogFrames >= 600)
            {
                _visualUnitAdmissionLogFrames = 0;
                Console.WriteLine(
                    $"[visual-unit-cull] frame {_window.FrameMs:F2} ms ({_window.Fps:F1} fps), " +
                    $"known {_visibleUnitKnownLastFrame}, admitted {_visibleWorldUnits.Count}, " +
                    $"distance {_visibleUnitDistanceCulledLastFrame}, " +
                    $"frustum {_visibleUnitFrustumCulledLastFrame}, " +
                    $"portal {_visibleUnitPortalCulledLastFrame}, wake-sampled {_remoteWakeSampledLastFrame}, " +
                    $"liquid {_liquidRenderMilliseconds:F2} ms");
            }

            // WMO liquid (MLIQ). A per-frame INT COMPARE against LiquidVersion,
            // not a tile-crossing event: groups are adopted several frames after
            // their instance is placed, so an event-driven rebuild would run
            // before Model.Liquids is populated and never retry (SYSTEM_WATER.md
            // §7.6). When the version has not moved this line costs nothing.
            if (_wmo is not null)
                _liquid.UpdateWmoLiquid(_wmo.LiquidVersion, _wmo.EnumerateLiquid());

            _liquid.Render(_window.Camera);

            var eye = _window.Camera.Position;
            if (TryGetEyeLiquidSurface(eye, out float surfaceZ, out byte liquidType)
                && eye.Z < surfaceZ)
            {
                _liquid.RenderUnderwater(surfaceZ - eye.Z, liquidType);
            }
        }
        _liquidRenderMilliseconds = Stopwatch.GetElapsedTime(liquidStarted).TotalMilliseconds;

        // Benilla's ordinary overhead identity batch is late world geometry, not an ImGui
        // overlay. Drawing after liquid keeps names readable at the waterline while the live
        // depth buffer still lets terrain, WMO walls, doodads, and units occlude them.
        if (WarmStage(5)) RenderWorldUnitNames();

        // Last, so it draws over the world it describes.
        long debugStarted = Stopwatch.GetTimestamp();
        _gpuProfiler?.Begin(GpuFrameProfiler.Pass.Debug);
        _collisionDebug?.Render(
            _window.Camera,
            MathF.Cos(_config.Movement.MaxSlopeDegrees * MathF.PI / 180f),
            _collision?.Offset ?? Vector3.Zero);

        HighlightPhysicsTriangles();
        RenderXray();
        DrawPortalDebug();

        if (_showPlayerMarker && _collisionDebug is not null && _controller is not null)
            _collisionDebug.RenderPlayerMarker(
                _window.Camera,
                _controller.Position,
                _config.Movement.Radius,
                _config.Movement.Height,
                _controller.Yaw);
        _gpuProfiler?.End(GpuFrameProfiler.Pass.Debug);
        _gpuProfiler?.EndFrame();
        _debugRenderMilliseconds = Stopwatch.GetElapsedTime(debugStarted).TotalMilliseconds;

        // FFXGlow whole-scene bloom over the finished world + particles, before the
        // curtain and HUD so neither blooms (benilla composites its UI over an
        // already-glowed world). The pass replaces the scene with its gamma-byte combine.
        DrawGlueScene(); // Phase 2 login glue scene (UI_MainMenu), networked + pre-world only
        DrawCharacterSelectScene(); // character-select per-race booth (UI_<Race>), CharacterSelect only
        _glow?.Apply();

        // Painterly restyle, AFTER the glow.
        //
        // It ran before the glow originally, on the theory that bloom glazing
        // over the illustration reads like varnish. In practice that left spell
        // effects looking untouched by the mode: they are the brightest thing
        // on screen, so what you actually see of a cast is mostly the ADDITIVE
        // bloom - and compositing that raw, after the styling, re-covered the
        // painted frame with an unpainted layer exactly where the eye was
        // looking. Styling last means the bloom is banded, graded and calmed
        // with everything else, so casts belong to the picture.
        //
        // Still gated on a live terrain, and the glue scenes above are pre-world
        // only (terrain null), so they are never styled; the loading curtain and
        // the whole HUD are drawn after this and stay untouched.
        if (_terrain is not null)
            _painterly?.Apply(_window.Camera.NearPlane, _window.Camera.FarPlane);

        // The loading curtain over the still-streaming world. Drawn last so it
        // covers everything beneath it; fades out when the world is ready.
        if (_loadScreen is not null) DrawLoadingScreen();

        _renderSpanMilliseconds = Stopwatch.GetElapsedTime(renderSpanStarted).TotalMilliseconds;

        // Self-test stall, deliberately after the render span closes so it
        // lands in no measured phase and reports as unaccounted time - which is
        // exactly the field it exists to prove works (PLAN_07 section 7).
        if (_hitch.PendingForcedStallMs > 0)
        {
            int ms = (int)_hitch.PendingForcedStallMs;
            _hitch.PendingForcedStallMs = 0;
            System.Threading.Thread.Sleep(ms);
        }
    }

    /// <summary>
    /// The steed under the local player. Drawn from the character pass, not the streamed unit
    /// loop, because the body it carries stands on the client-predicted position rather than
    /// the entity stream's. Called every frame: a display id of 0 is how the renderer learns
    /// the player dismounted. See CreatureRenderer.Mounts.cs.
    /// </summary>
    private void DrawSelfMount(bool warm)
    {
        if (_character is null) return;
        _character.MountSeat = null;

        CreatureRenderer? creatures = _creatures;
        CharacterController? controller = _controller;
        if (creatures is null || controller is null || _freeView || !warm) return;

        ulong guid = RenderSelfGuid;
        int display = SelfMountDisplayId();

        float walkSpeed = 0f;
        if (guid != 0 && _entities.TryGet(guid, out WorldEntity self) &&
            self.Speeds is { Length: > 0 } speeds)
            walkSpeed = speeds[0];

        // The offline sandbox has no session guid until a character is in world, while the
        // body on screen is real and rideable — so its own local id keys the animation clock.
        if (guid == 0 && display > 0) guid = CreatorLocalGuid;
        if (guid == 0) return;

        float travelSpeed = _serverRideSpline?.AverageSpeed ?? controller.PlanarSpeed;
        bool flying = _serverRideSpline?.Flying == true;
        AuraVisualState? aura = _entities.TryGet(RenderSelfGuid, out WorldEntity auraUnit)
            ? auraUnit.AuraVisual : null;
        bool tacticalMountFrozen = TacticalFreezePoseLaw.IsFrozen(ControlledGuid);

        // A mount has no strafe gait, so it must instead turn to face wherever it is actually
        // travelling - the same reason a diagonal walk leans a foot character rather than
        // sliding it sideways with a forward-facing gait. Falls back to plain facing when
        // nearly stationary (a near-zero velocity carries no meaningful direction) or under a
        // server-driven ride spline, whose travel direction isn't available as a vector here.
        const float minTravelSpeedForHeading = 0.3f;
        float heading = _serverRideSpline is null && controller.PlanarSpeed > minTravelSpeedForHeading
            ? MathF.Atan2(controller.HorizontalVelocity.Y, controller.HorizontalVelocity.X)
            : controller.Yaw;

        if (creatures.TryDrawSelfMount(_window.Camera, guid, display, controller.Position,
                heading, travelSpeed, walkSpeed, flying, controller.Grounded, controller.FallTimeMs,
                aura?.Alpha ?? 1f, aura?.Tint ?? Vector3.One,
                aura?.Frozen == true || tacticalMountFrozen, out Matrix4x4 seat))
            _character.MountSeat = seat;
    }

    private void DrawUnitShadows()
    {
        if (_unitShadows is null) return;

        UnitShadowCaster? local = null;
        if (_character is { Enabled: true } &&
            _controller is { Grounded: true, Flying: false } controller)
        {
            float radius = MathF.Max(0.5f, _config.Movement.Radius * 1.45f);
            if (RenderSelfGuid != 0 && _entities.TryGet(RenderSelfGuid, out WorldEntity self))
            {
                float scale = CreatureRenderer.UnitRenderScale(self.Scale);
                radius = MathF.Max(0.35f, (self.IsCreature ? 0.7f : radius) * scale);
            }
            // Riding: the shadow belongs to what is actually touching the ground.
            if (_creatures is not null &&
                _creatures.TryGetMountGroundRadius(RenderSelfGuid, out float mountRadius))
                radius = mountRadius;
            local = new UnitShadowCaster(controller.Position, radius);
        }

        _unitShadows.Render(_window.Camera, local, _creatures?.ShadowCasters);
    }

    private void ApplyAtmosphere()
    {
        _atmosphere.Evaluate();
        _window.SkyColor = _atmosphere.SkyColor;
        _window.Camera.FarPlane = _coupleFarPlaneToFog && _atmosphere.CullAtFogEnd
            ? MathF.Min(_config.Render.FarPlane, _atmosphere.FogEnd + 50f)
            : _config.Render.FarPlane;

        void ApplyTerrain(TerrainRenderer renderer)
        {
            renderer.SunDirection = _atmosphere.SunDirection;
            renderer.SunColor = _atmosphere.SunColor;
            renderer.SunIntensity = _atmosphere.SunIntensity;
            renderer.AmbientColor = _atmosphere.AmbientColor;
            renderer.AmbientIntensity = _atmosphere.AmbientIntensity;
            renderer.FogColor = _atmosphere.FogColor;
            renderer.FogStart = _atmosphere.ShaderFogStart;
            renderer.FogEnd = _atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = _atmosphere.VisibilityDistance;
        }

        void ApplyWmo(WmoRenderer renderer)
        {
            renderer.SunDirection = _atmosphere.SunDirection;
            renderer.NightFraction = _atmosphere.NightFraction;
            renderer.SunColor = _atmosphere.SunColor;
            renderer.SunIntensity = _atmosphere.SunIntensity;
            renderer.AmbientColor = _atmosphere.AmbientColor;
            renderer.AmbientIntensity = _atmosphere.AmbientIntensity;
            renderer.FogColor = _atmosphere.FogColor;
            renderer.FogStart = _atmosphere.ShaderFogStart;
            renderer.FogEnd = _atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = _atmosphere.VisibilityDistance;
        }

        void ApplyDoodads(DoodadRenderer renderer)
        {
            renderer.SunDirection = _atmosphere.SunDirection;
            renderer.SunColor = _atmosphere.SunColor;
            renderer.SunIntensity = _atmosphere.SunIntensity;
            renderer.AmbientColor = _atmosphere.AmbientColor;
            renderer.AmbientIntensity = _atmosphere.AmbientIntensity;
            renderer.FogColor = _atmosphere.FogColor;
            renderer.FogStart = _atmosphere.ShaderFogStart;
            renderer.FogEnd = _atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = _atmosphere.VisibilityDistance;
        }

        void ApplyCharacter(CharacterRenderer renderer)
        {
            renderer.SunDirection = _atmosphere.SunDirection;
            renderer.SunColor = _atmosphere.SunColor;
            renderer.SunIntensity = _atmosphere.SunIntensity;
            renderer.AmbientColor = _atmosphere.AmbientColor;
            renderer.AmbientIntensity = _atmosphere.AmbientIntensity;
            renderer.FogColor = _atmosphere.FogColor;
            renderer.FogStart = _atmosphere.ShaderFogStart;
            renderer.FogEnd = _atmosphere.ShaderFogEnd;
        }

        void ApplyCreature(CreatureRenderer renderer)
        {
            renderer.SunDirection = _atmosphere.SunDirection;
            renderer.SunColor = _atmosphere.SunColor;
            renderer.SunIntensity = _atmosphere.SunIntensity;
            renderer.AmbientColor = _atmosphere.AmbientColor;
            renderer.AmbientIntensity = _atmosphere.AmbientIntensity;
            renderer.FogColor = _atmosphere.FogColor;
            renderer.FogStart = _atmosphere.ShaderFogStart;
            renderer.FogEnd = _atmosphere.ShaderFogEnd;
        }

        if (_terrain is not null) ApplyTerrain(_terrain);
        if (_wmo is not null) ApplyWmo(_wmo);
        if (_doodads is not null) ApplyDoodads(_doodads);
        if (_character is not null) ApplyCharacter(_character);
        if (_creatures is not null) ApplyCreature(_creatures);
        if (_unitShadows is not null)
        {
            _unitShadows.FogStart = _atmosphere.ShaderFogStart;
            _unitShadows.FogEnd = _atmosphere.ShaderFogEnd;
        }

        float effectFarClip = _atmosphere.CullAtFogEnd ? _atmosphere.FogEnd : 0f;
        if (_spellParticles is not null)
        {
            _spellParticles.FogEnabled = _atmosphere.FogEnabled;
            _spellParticles.FogColor = _atmosphere.FogColor;
            _spellParticles.FogStart = _atmosphere.ShaderFogStart;
            _spellParticles.FogEnd = _atmosphere.ShaderFogEnd;
            _spellParticles.FarClip = effectFarClip;
        }
        if (_spellRibbons is not null)
        {
            _spellRibbons.FogEnabled = _atmosphere.FogEnabled;
            _spellRibbons.FogColor = _atmosphere.FogColor;
            _spellRibbons.FogStart = _atmosphere.ShaderFogStart;
            _spellRibbons.FogEnd = _atmosphere.ShaderFogEnd;
            _spellRibbons.FarClip = effectFarClip;
        }
        if (_spellChainBeamRenderer is not null)
            _spellChainBeamRenderer.FarClip = effectFarClip;
        if (_spellEffectMeshes is not null)
        {
            _spellEffectMeshes.SunDirection = _atmosphere.SunDirection;
            _spellEffectMeshes.SunColor = _atmosphere.SunColor;
            _spellEffectMeshes.SunIntensity = _atmosphere.SunIntensity;
            _spellEffectMeshes.AmbientColor = _atmosphere.AmbientColor;
            _spellEffectMeshes.AmbientIntensity = _atmosphere.AmbientIntensity;
            _spellEffectMeshes.FogEnabled = _atmosphere.FogEnabled;
            _spellEffectMeshes.FogColor = _atmosphere.FogColor;
            _spellEffectMeshes.FogStart = _atmosphere.ShaderFogStart;
            _spellEffectMeshes.FogEnd = _atmosphere.ShaderFogEnd;
            _spellEffectMeshes.FarClip = effectFarClip;
        }

        if (_liquid is not null)
        {
            _liquid.SunDirection = _atmosphere.SunDirection;
            _liquid.SunColor = _atmosphere.SunColor;
            _liquid.SunIntensity = _atmosphere.SunIntensity;
            _liquid.AmbientColor = _atmosphere.AmbientColor;
            _liquid.AmbientIntensity = _atmosphere.AmbientIntensity;
            _liquid.FogColor = _atmosphere.FogColor;
            _liquid.FogStart = _atmosphere.ShaderFogStart;
            _liquid.FogEnd = _atmosphere.ShaderFogEnd;

            // PLAN_12. Water colours arrive through the same object and the same
            // gate as the sky, so turning authored lighting off takes the water
            // with it - they cannot disagree about whether to believe the data.
            _liquid.HasAuthoredColors = _atmosphere.AuthoredWaterReady;
            _liquid.OceanClose = _atmosphere.OceanCloseColor;
            _liquid.OceanFar = _atmosphere.OceanFarColor;
            _liquid.RiverClose = _atmosphere.RiverCloseColor;
            _liquid.RiverFar = _atmosphere.RiverFarColor;
            _liquid.OceanAlphaShallow = _atmosphere.OceanShallowAlpha;
            _liquid.OceanAlphaDeep = _atmosphere.OceanDeepAlpha;
            _liquid.RiverAlphaShallow = _atmosphere.RiverShallowAlpha;
            _liquid.RiverAlphaDeep = _atmosphere.RiverDeepAlpha;
        }

        if (_foliage is not null)
        {
            _foliage.SunDirection = _atmosphere.SunDirection;
            _foliage.SunColor = _atmosphere.SunColor;
            _foliage.SunIntensity = _atmosphere.SunIntensity;
            _foliage.AmbientColor = _atmosphere.AmbientColor;
            _foliage.AmbientIntensity = _atmosphere.AmbientIntensity;
            _foliage.FogColor = _atmosphere.FogColor;
            _foliage.FogStart = _atmosphere.ShaderFogStart;
            _foliage.FogEnd = _atmosphere.ShaderFogEnd;
        }
    }

    public void Gui()
    {
        // Enter World is clicked during this GUI pass, after Render has already run.
        // Remember whether a native curtain existed at entry so a newly armed one can
        // cover this otherwise-unavoidable handoff frame with an ImGui veil below.
        bool curtainOwnedAtGuiEntry = _worldLoading || _loadScreen is not null;

        // The gameplay text atlases must track the scale actually being rendered - a maximise
        // or a UI-scale change retargets the em sizes and ClientWindow rebuilds the atlas
        // between frames. Without this, gameplay text silently upscales the nearest bake and
        // goes soft (the exact defect GameTextLaw exists to remove).
        float gameplayScale = GameplayUiScale();
        _window.EnsureGameplayTextScale(gameplayScale);
        // WowSkin is shared chrome state. Establish gameplay's proportional value before NetHud
        // draws, then the Escape menu may temporarily replace it with its independent gear scale.
        if (_skin is not null) _skin.Scale = gameplayScale;

        // The native loading curtain is an exclusive screen. ImGui is composited
        // after the world pass, so allowing it to run here would paint gameplay
        // bars, auras, unit frames and developer windows over the loading art.
        bool preWorldPrime = !_worldLoading && _loadScreen is not null &&
                             !_preWorldHudPrimed;
        bool hiddenPrime = preWorldPrime ||
                           (_worldLoading && _loadPhase == WorldLoadPhase.Fade &&
                            _loadCurtainAlpha >= 1f && _loadFadeWarmStage == 5);
        if ((_worldLoading || _loadScreen is not null) && !hiddenPrime) return;

        if (hiddenPrime) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0f);
        try
        {
            BuildGui();
            if (preWorldPrime) _preWorldHudPrimed = true;
        }
        finally { if (hiddenPrime) ImGui.PopStyleVar(); }

        // ArmEnterWorldCurtain runs from DrawCharacterSelect, too late for this frame's
        // native GL pass. Cover everything already submitted to ImGui for exactly this
        // one frame; the next Render draws the real loading art as the exclusive screen.
        if (!curtainOwnedAtGuiEntry && _loadScreen is not null)
        {
            ImGui.GetForegroundDrawList().AddRectFilled(
                Vector2.Zero, ImGui.GetIO().DisplaySize,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f)));
        }
    }

    private void BuildGui()
    {

        // Arm before any gameplay widgets draw; OverlayTop writes after the frame.
        BeginGameplayDumpFrame();

        if (PainterlyComparisonHidesUi) return;

        // THE SETTINGS MODAL IS DRAWN FIRST, AND DELIBERATELY ABOVE THE RETURN
        // BELOW. It is the PLAYER's surface - the Escape menu - so it must exist
        // in a shipping build where all the developer tooling is off. Moving this
        // call after the DevTools check would reproduce exactly the seam
        // violation the handbook already records against UpdateLightProbe.
        NetHud(); // Phase 2 network status window (draws only when server.enabled)

        // A modal DIALOG frame owns the foreground. Do not submit developer windows
        // behind its translucent backdrop; vanilla shows the world, not diagnostics,
        // through UI-DialogBox-Background.
        if (LogoutUiActive)
        {
            DrawLogoutModal();
            return;
        }
        if (SettingsModalOpen || _escapePressed)
        {
            DrawSettingsModal();
            return;
        }

        // The NPC dev window is deliberately ABOVE the creator-mode return below:
        // unlike the F1 instrument stack it serves both live and creator mode
        // (Ctrl+N). Never over the glue front door - which by itself covers the
        // launch screens in both modes. This gate also carried
        // !CreatorLaunchActive, misread as "the launch screen is up"; it actually
        // means "this SESSION was launched as creator" and holds for the whole
        // sandbox session - so every dev window here was input-toggled but never
        // drawn in creator mode, ever (found 2026-08-17, Ctrl+E "not registering").
        if (!GlueFrontDoorActive)
        {
            DrawDevWindow();
            DrawDevOverlayLabels();
            // The Encounter Lab (Ctrl+E) sits beside it for the same reason: it is
            // mode-neutral, and it is MORE useful in creator mode than live, because
            // the simulator needs no server at all.
            DrawEncounterLab();
            DrawEncounterActionPanel();
            // One mode-neutral host for every dev/encounter window's gear popup.
            // Keeping it outside the individual windows lets a pop-out remain tunable
            // after its parent closes and prevents the popup being submitted twice.
            if (!_creatorWorldRequested) DrawCreatorPanelTunePopup();
            DrawEncounterLabOverlay();
        }

        // Creator mode replaces the whole developer instrument stack with its own
        // menus (drawn from NetHud) - the dev overlay never shows in the sandbox,
        // and no mode shows it over the glue front door.
        if (_creatorWorldRequested || CreatorLaunchActive || GlueFrontDoorActive) return;

        // Master dev-tooling switch: the whole
        // in-game overlay is developer tooling and is skipped in a release build.
        if (!_config.DevTools || PlayerPanelOpen) return;
        if (_uiParityArmed) return;
        if (!_devOverlayVisible) return;   // F1

        ImGui.SetNextWindowPos(new Vector2(12, 12), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);
        if (_devOverlayFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            _devOverlayFocusRequested = false;
        }

        if (ImGui.Begin("MSUI Client"))
        {
            ImGui.Text($"{_window.Fps:F0} fps   {_window.FrameMs:F2} ms");
            if (ImGui.Button("Spell FX inspector (F7)")) _spellFxInspectorOpen = true;

            // VSync and multisampling moved to the settings modal (Escape).
            // They are preferences, not instruments - PLAN_11 section 6. VSync is
            // still worth flipping as a DIAGNOSTIC (SYSTEM_STREAMING.md 5A.17),
            // but the modal is one keypress away and two copies of a switch is
            // how two surfaces start disagreeing about the truth.
            ImGui.TextDisabled("Esc - game menu / video settings");
            ImGui.TextDisabled("F1 - hide these developer panels");

            DrawUiSkinPanel();
            DrawVerdictsPanel();
            DrawGmConsolePanel();
            DrawMovementInstrumentsPanel();
            DrawCombatInstrumentsPanel();
            DrawPortraitLabPanel();

            if (ImGui.CollapsingHeader("Scene and vantage", ImGuiTreeNodeFlags.DefaultOpen))
            {
            _vantages ??= VantageStore.Load(_config.RepoRoot);

            ImGui.InputText("Name##vantage", ref _vantageNameInput, 64u);
            ImGui.SameLine();
            if (ImGui.Button("Save##vantage") && !string.IsNullOrWhiteSpace(_vantageNameInput))
            {
                var saved = CaptureVantage(_vantageNameInput.Trim());
                _vantages.Upsert(saved);
                _currentVantage = saved.Name;
                Console.WriteLine(VantageLine("saved", saved));
            }
            ImGui.SameLine();
            if (ImGui.Button("Dump scene (F9)")) DumpScene();
            ImGui.SameLine();
            if (ImGui.Button("Dump gameplay (F10)")) ArmGameplayDump();
            ImGui.SameLine();
            if (ImGui.Button("Painterly A/B (F11)")) ArmPainterlyComparison();

            if (_currentVantage is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reload (F8)"))
                {
                    var cur = _vantages.Find(_currentVantage);
                    if (cur is not null) ApplyVantage(cur);
                }
                ImGui.TextDisabled($"current: {_currentVantage}");
            }

            if (_vantages.All.Count > 0)
            {
                foreach (var saved in _vantages.All)
                {
                    if (ImGui.Button($"Load##v_{saved.Name}")) ApplyVantage(saved);
                    ImGui.SameLine();
                    ImGui.Text(saved.Name);
                }
            }
            }

            DrawHitchPanel();
            DrawLightProbePanel();

            if (ImGui.CollapsingHeader("Performance - CPU draw submission", ImGuiTreeNodeFlags.DefaultOpen))
            {
            ImGui.Text($"  update {_updateMilliseconds,6:F2} ms  " +
                       $"move {_movementMilliseconds,5:F2}  residency {_residencyMilliseconds,5:F2}");
            ImGui.Text($"  preload {_preloadMilliseconds,5:F2} ms  " +
                       $"unit {_characterUpdateMilliseconds,5:F2}  camera {_cameraCollisionMilliseconds,5:F2}");
            ImGui.Text($"  world {_worldRenderMilliseconds,6:F2} ms  " +
                       $"character {_characterRenderMilliseconds,5:F2} ms  " +
                       $"debug {_debugRenderMilliseconds,5:F2} ms");
            if (_terrain is not null)
                ImGui.Text($"  terrain {_terrain.RenderMilliseconds,6:F2} ms  " +
                           $"{_terrain.DrawCallsLastFrame,5:N0} calls  " +
                           $"{_terrain.TrianglesLastFrame,10:N0} tris");
            if (_wmo is not null)
            {
                ImGui.Text($"  WMO     {_wmo.RenderMilliseconds,6:F2} ms  " +
                           $"{_wmo.DrawCallsLastFrame,5:N0} calls  " +
                           $"{_wmo.TrianglesLastFrame,10:N0} tris");
                ImGui.Text($"          {_wmo.DrawnLastFrame:N0} instance(s), " +
                           $"{_wmo.VisibleGroupsLastFrame:N0} group(s)");
            }
            if (_doodads is not null)
                ImGui.Text($"  doodad  {_doodads.RenderMilliseconds,6:F2} ms  " +
                           $"{_doodads.DrawCallsLastFrame,5:N0} calls  " +
                           $"{_doodads.TrianglesLastFrame,10:N0} tris  " +
                           $"{_doodads.DrawnLastFrame:N0} inst");

            ImGui.Text("GPU time - delayed, non-blocking");
            if (_gpuProfiler is { HasResults: true } gpu)
            {
                ImGui.Text($"  measured {gpu.MeasuredTotalMilliseconds,6:F2} ms  " +
                           $"terrain {gpu[GpuFrameProfiler.Pass.Terrain],5:F2}  " +
                           $"WMO {gpu[GpuFrameProfiler.Pass.Wmo],5:F2}");
                ImGui.Text($"  doodad {gpu[GpuFrameProfiler.Pass.Doodads],5:F2} ms  " +
                           $"character {gpu[GpuFrameProfiler.Pass.Character],5:F2}  " +
                           $"debug {gpu[GpuFrameProfiler.Pass.Debug],5:F2}");
            }
            else
            {
                ImGui.Text("  warming up...");
            }
            }

            if (ImGui.CollapsingHeader("Atmosphere and visibility test", ImGuiTreeNodeFlags.DefaultOpen))
            {

            // Time of day is the ONE control both surfaces keep. It is a
            // preference when it is cycling and an instrument when it is pinned,
            // and pinning it to compare two frames is worth not having to open a
            // modal for. Everything else that used to live here - sun and ambient
            // strength, the three fog controls, far-plane coupling and the
            // vanilla-visibility preset - is now Escape / Graphics.
            float time = _atmosphere.TimeOfDayHours;
            if (ImGui.SliderFloat("Time of day", ref time, 0f, 24f, "%.2f h"))
                PinWorldClockAt(time);

            if (ImGui.Button("Noon")) PinWorldClockAt(12f);
            ImGui.SameLine();
            if (ImGui.Button("Sunset")) PinWorldClockAt(18.25f);
            ImGui.SameLine();
            if (ImGui.Button("Night")) PinWorldClockAt(0f);
            if (_devTimePin)
            {
                ImGui.SameLine();
                if (ImGui.Button("Resume clock")) _devTimePin = false;
            }
            ImGui.TextDisabled($"clock: {WorldClockDescription()}");

            ImGui.TextDisabled(
                $"fog {_atmosphere.FogStart:F0} -> {_atmosphere.FogEnd:F0} yd" +
                $"{(_atmosphere.FogEnabled ? "" : " (off)")}" +
                $"{(_atmosphere.CullAtFogEnd ? ", culling past" : "")}");
            ImGui.TextDisabled(
                $"sun x{_atmosphere.SunStrength:F2}  ambient x{_atmosphere.AmbientStrength:F2}  " +
                $"{(_atmosphere.HasAuthored ? "authored" : "INVENTED constants")}");

            if (_terrain is not null)
            {
                ImGui.Text($"startup preload: " +
                           $"{(_config.Start.DrainPreloadsAtStartup ? "blocking outer ring" : "visible first / background outer ring")}");
                ImGui.Text($"tiles {_terrain.TileCount}   drawn {_terrain.DrawnLastFrame}");
                ImGui.Text($"triangles {_terrain.TotalTriangles:N0}");
                if (_residentCentre is { } resident)
                    ImGui.Text($"resident [{resident.col},{resident.row}]  " +
                               $"objects {ObjectResidencyRadius:F0} yd  " +
                               $"last {_lastStreamSeconds:F2}s");
                if (_wmo is not null)
                    ImGui.Text($"WMO preload {WmoPreloadRadius * 2 + 1}x{WmoPreloadRadius * 2 + 1}  " +
                               $"{_wmo.PendingPreloads} queued");
                if (_doodads is not null)
                {
                    ImGui.Text($"M2 preload {_doodads.PendingPreloads} queued  " +
                               $"discovery {_backgroundDiscovery.Count} tile(s)");
                    ImGui.Text($"M2 demand {(_demandStreamDoodads ? "on" : "off")}, " +
                               $"radius {DoodadDemandRadius:F0} yd");
                }
            }

            if (_controller is not null)
            {
                var p = _controller.Position;

                ImGui.Separator();
                ImGui.Text("Position (WoW space)");
                ImGui.Text($"  X {p.X,10:F2}   north");
                ImGui.Text($"  Y {p.Y,10:F2}   west");
                ImGui.Text($"  Z {p.Z,10:F2}   up");

                var (col, row) = TerrainRenderer.TileAt(p.X, p.Y);
                ImGui.Text($"  tile [{col}, {row}]");
                ImGui.Text($"  facing {_controller.Yaw * 180f / MathF.PI,5:F0} deg");

                ImGui.Separator();
                ImGui.Text("Movement");

                ImGui.Text(_controller.GroundZ is float g
                    ? $"  ground {g,10:F2}   (delta {p.Z - g,6:F2})"
                    : "  ground     (no data)");

                // WHICH of the two is holding you up. This is the line that
                // separates a misplaced collision mesh from terrain doing it.
                ImGui.Text(_controller.TerrainGroundZ is float tz
                    ? $"    terrain   {tz,9:F2}"
                    : "    terrain     (none)");
                if (_controller.TerrainGroundZ is not null)
                {
                    Vector3 terrainNormal = _controller.TerrainGroundNormal;
                    float terrainSlope = MathF.Acos(Math.Clamp(terrainNormal.Z, -1f, 1f)) *
                                         180f / MathF.PI;
                    ImGui.Text($"      normal ({terrainNormal.X:F3}, {terrainNormal.Y:F3}, " +
                               $"{terrainNormal.Z:F3})  slope {terrainSlope:F1} deg");
                    if (_controller.TerrainGroundSteep)
                        ImGui.TextColored(new Vector4(1f, 0.45f, 0.2f, 1f),
                            "      unwalkable terrain face");
                    if (_controller.TerrainChunkImpassable)
                        ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                            "      authored impassable chunk");
                }
                ImGui.Text(_controller.CollisionGroundZ is float cz
                    ? $"    collision {cz,9:F2}"
                    : "    collision   (none)");
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                    $"    standing on {_controller.GroundSource}");

                // Terrain holes: the MCNK 0x3C mask, which is how a dungeon
                // entrance is cut through a hillside. Standing in one means the
                // height grid deliberately has no answer and collision has to.
                if (_controller.InTerrainHole)
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                        "    in terrain hole (dungeon cut)");
                if (_controller.GroundTriangle >= 0 && _collision is not null)
                    ImGui.Text($"    surface tri {_controller.GroundTriangle} " +
                               $"({_collision.SourceOf(_controller.GroundTriangle)})");

                if (_controller.GroundProbeOffset.LengthSquared() > 1e-6f)
                    ImGui.Text($"    support probe ({_controller.GroundProbeOffset.X,5:F2}," +
                               $" {_controller.GroundProbeOffset.Y,5:F2})");

                if (_controller.GroundProbesLastFrame > 1)
                    ImGui.Text($"    support fan {_controller.GroundProbesLastFrame} probes");

                if (_controller.GroundAdhesion)
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                        "    ground adhesion");

                ImGui.Text($"  state  {(_controller.Flying ? "flying" : _controller.Grounded ? "grounded" : "airborne")}");
                ImGui.Text($"  vz     {_controller.Velocity.Z,10:F2}");

                if (_controller.FallTimeMs > 0)
                    ImGui.Text($"  fall   {_controller.FallTimeMs,10:F0} ms");

                float groundSnap = _config.Movement.GroundSnapDistance;
                if (ImGui.SliderFloat("Ground snap down", ref groundSnap, 0f, 1.5f, "%.2f yd"))
                    _config.Movement.GroundSnapDistance = groundSnap;

                float fallDelay = _config.Movement.FallAnimationDelayMs;
                if (ImGui.SliderFloat("Fall animation delay", ref fallDelay, 0f, 500f, "%.0f ms"))
                    _config.Movement.FallAnimationDelayMs = fallDelay;

                float runSpeed = _config.Movement.RunSpeed;
                if (ImGui.SliderFloat("Run speed", ref runSpeed, 1f, 12f, "%.2f yd/s"))
                    _config.Movement.RunSpeed = runSpeed;

                float walkSpeed = _config.Movement.WalkSpeed;
                if (ImGui.SliderFloat("Walk speed", ref walkSpeed, 0.5f, 6f, "%.2f yd/s"))
                    _config.Movement.WalkSpeed = walkSpeed;

                float backwardSpeed = _config.Movement.BackwardSpeed;
                if (ImGui.SliderFloat("Backward speed", ref backwardSpeed, 0.5f, 8f, "%.2f yd/s"))
                    _config.Movement.BackwardSpeed = backwardSpeed;

                // This one matters: it is the loud version of the failure that
                // once looked like a physics bug for 23 seconds of falling.
                if (_controller.NoGroundBelow)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        "  NO GROUND — off tiles or missing MCVT");

                // Dungeon entry. These two switches together are the difference
                // between walking into the gold mine and climbing the mountain
                // it is dug into, so they get their own group rather than
                // hiding among the speed sliders.
                ImGui.Separator();
                ImGui.Text("Dungeon entry (1.12)");

                if (_terrain is not null)
                {
                    bool holes = _terrain.ApplyHoles;
                    if (ImGui.Checkbox("Terrain holes cut ground", ref holes))
                        _terrain.ApplyHoles = holes;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "MCNK 0x3C holes: a uint16 per chunk, 4x4 bits, each bit\n" +
                            "covering a 2x2 block of the chunk's 8x8 quads. The mesh has\n" +
                            "always skipped these; the height grid did not, so the player\n" +
                            "stood on invisible ground across a mine mouth.\n" +
                            "Off = the old behaviour.");

                    ImGui.TextDisabled($"  {_terrain.HoleQuadCount} holed quad(s) loaded");
                }

                bool precedence = _controller.VanillaHeightPrecedence;
                if (ImGui.Checkbox("Vanilla height precedence", ref precedence))
                    _controller.VanillaHeightPrecedence = precedence;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Map::GetHeight: the collision surface wins when it is HIGHER\n" +
                        "than terrain, or when you are already under terrain and it is\n" +
                        "CLOSER. Off = highest-surface-wins, which can never put you in\n" +
                        "a tunnel because a mine floor is below the mountain above it.");

                float slack = _controller.UndergroundSlack;
                if (ImGui.SliderFloat("Underground slack", ref slack, 0.05f, 4f, "%.2f yd"))
                    _controller.UndergroundSlack = slack;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "How far below terrain the feet must be before the closer-surface\n" +
                        "rule may pick a lower floor. Vanilla's server constant is 0.05,\n" +
                        "but walking uphill puts the feet ~0.16 yd under terrain for a\n" +
                        "frame, so that value here drops you through the world.");
            }
            }

            DrawPortalPanel();
            DrawInstancesPanel();
            DrawParticlesPanel();

            if (ImGui.CollapsingHeader("Buildings", ImGuiTreeNodeFlags.DefaultOpen))
            {
            if (_wmo is not null)
            {
                ImGui.Text($"  {_wmo.InstanceCount} placed, {_wmo.DrawnLastFrame} drawn");
                ImGui.Text($"  {_wmo.ModelCount} model(s), {_wmo.TextureCount} texture(s)");
                ImGui.Text($"  {_wmo.TotalTriangles:N0} triangles");

                // Every switch and slider that used to live here is now
                // Escape / Graphics / Advanced - buildings. What is left is the
                // readout half, which is what actually answers "why is that
                // building missing" (PLAN_11 section 6).
                ImGui.TextDisabled(
                    $"  draw {(_wmo.Enabled ? "on" : "OFF")}  " +
                    $"frustum {(_wmo.FrustumCulling ? "on" : "off")}  " +
                    $"shells {(_wmo.UseDistanceLodShells ? "on" : "off")}  " +
                    $"two-sided {(_wmo.ForceTwoSided ? "on" : "off")}");
                ImGui.TextDisabled(
                    $"  distance {_wmo.DrawDistance:F0} yd  alpha {_wmo.AlphaCutoff:F2}  " +
                    $"MOCV {(_wmo.UseVertexColors ? $"x{_wmo.VertexColorScale:F2}" : "off")}");
                ImGui.TextDisabled(
                    $"  impostor <= {_wmo.ImpostorMaxVertices} verts  " +
                    $"inside margin {_wmo.InsideInstanceMargin:F0}  " +
                    $"interior cull {_wmo.InteriorCullDistance:F0}  " +
                    $"guard {_wmo.ShellNearGuard:F0}");

                ImGui.Text($"  LOD shells hidden nearby: {_wmo.LodGroupsCulledLastFrame}");
                ImGui.Text($"  occluded groups: {_wmo.OccludedGroupsLastFrame}" +
                           $"{(_wmo.OcclusionCulling ? "" : "  (occlusion off)")}");

                // Persisted since settings v6 as Lighting.InteriorSpill (it is
                // the doorway-glow knob, per-mode defaults on the mode switch).
                // Written through the settings object so this slider and the
                // modal's Advanced slider are one value, not two.
                float interiorBright = _wmo.InteriorBrightness;
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderFloat("Interior spill brightness", ref interiorBright, 0.5f, 3f, "%.2f"))
                {
                    _wmo.InteriorBrightness = interiorBright;
                    Settings.Lighting.InteriorSpill = interiorBright;
                }
                ImGui.TextDisabled("   brightens MOCV-lit interiors and their doorway glow; exterior untouched");

                bool visTrace = _wmo.VisTrace;
                if (ImGui.Checkbox("Console visibility trace", ref visTrace))
                    _wmo.VisTrace = visTrace;
                ImGui.SameLine();
                bool dumpGroups = _wmo.DumpLargeWmoGroups;
                if (ImGui.Checkbox("Dump groups on load", ref dumpGroups))
                    _wmo.DumpLargeWmoGroups = dumpGroups;

                if (_wmo.LargestWmoGroupCount > 0)
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                        $"  {_wmo.LargestWmoName}: inside={_wmo.LastInsideCity}  " +
                        $"shells {_wmo.ShellsDrawnLastFrame} drawn / {_wmo.ShellsHiddenLastFrame} hidden  " +
                        $"groups {_wmo.LargestWmoGroupsDrawn}/{_wmo.LargestWmoGroupCount}");

                ImGui.Separator();
                ImGui.Text("Group picker: MIDDLE-CLICK anything (or crosshair button)");
                if (ImGui.Button("Pick under crosshair"))
                    _lastPick = _wmo.PickGroups(_window.Camera, _window.Camera.Position, _window.Camera.Forward);
                ImGui.SameLine();
                if (ImGui.Button("Clear pick")) _lastPick.Clear();
                foreach (var h in _lastPick)
                    ImGui.Text($"  {h.Path} [{h.GroupIndex,3}] '{h.Name}' 0x{h.Flags:X8} " +
                               $"{(h.Interior ? "INT" : "ext")}{(h.Shell ? " LOD" : "")} " +
                               $"v={h.VertexCount} {h.Distance:F0}yd -> {h.Reason}");

                if (_lastPick.Count > 0 && _overrides is not null)
                {
                    var top = _lastPick[0];
                    ImGui.TextDisabled($"override [{top.GroupIndex}] '{top.Name}' in {top.Path}:");
                    if (ImGui.Button("Hide inside##ov"))
                        _overrides.Set(top.Root, top.Path, top.GroupIndex, OverrideRule.HideInside,
                            "hidden once inside the city", _currentVantage ?? "");
                    ImGui.SameLine();
                    if (ImGui.Button("Hide always##ov"))
                        _overrides.Set(top.Root, top.Path, top.GroupIndex, OverrideRule.Hide,
                            "hidden everywhere", _currentVantage ?? "");
                    ImGui.SameLine();
                    if (ImGui.Button("Show inside##ov"))
                        _overrides.Set(top.Root, top.Path, top.GroupIndex, OverrideRule.ShowInside,
                            "forced visible inside", _currentVantage ?? "");
                    ImGui.SameLine();
                    if (ImGui.Button("Clear##ov"))
                        _overrides.Remove(top.Root, top.GroupIndex);
                }

                if (_overrides is not null && _overrides.All.Count > 0)
                {
                    ImGui.TextDisabled("active overrides:");
                    foreach (var ov in _overrides.All)
                        ImGui.TextDisabled($"  {ov.RootFile} [{ov.GroupIndex}] {ov.Rule}");
                }
            }
            else
            {
                ImGui.Text("  none loaded");
            }

            if (_doodads is not null)
            {
                ImGui.Separator();
                ImGui.Text("Doodads");
                ImGui.Text($"  {_doodads.InstanceCount:N0} placed, {_doodads.DrawnLastFrame:N0} drawn");
                ImGui.Text($"  {_doodads.ModelCount} model(s), {_doodads.CollisionModels} with collision");
                ImGui.Text($"  {_doodads.TotalTriangles:N0} triangles");
                ImGui.Text($"  {_doodads.InteriorLitCount:N0} with baked interior light");

                // Moved to Escape / Graphics / Advanced - doodads: draw, frustum
                // cull, GPU instancing, flat cull bounds, MODD interior light and
                // its brightness, alpha cut and draw distance. The A/B arguments
                // that used to be comments here are now the tooltips on those
                // controls, so the reason lives beside the switch.
                //
                // The one that matters for measurement: flat cull bounds was
                // 55.8 ms -> 0.3 ms on a crossing frame, and PLAN_08 section 7
                // step 3 says it gets backed out if the number does not move.
                // Diff `cull` in the [hitch] doodad line, not this panel.
                ImGui.TextDisabled(
                    $"  draw {(_doodads.Enabled ? "on" : "OFF")}  " +
                    $"frustum {(_doodads.FrustumCulling ? "on" : "off")}  " +
                    $"instanced {(_doodads.UseInstancing ? "on" : "off")}  " +
                    $"flat bounds {(_doodads.FlatCullBounds ? "on" : "off")}");
                ImGui.TextDisabled(
                    $"  distance {_doodads.DrawDistance:F0} yd  alpha {_doodads.AlphaCutoff:F2}  " +
                    $"MODD {(_doodads.InteriorLighting ? $"x{_doodads.VertexColorScale:F2}" : "off")}");
            }
            }

            if (ImGui.CollapsingHeader("Character"))
            {
            if (_character is not null)
            {
                ImGui.Text($"  {_character.Race} {_character.Gender}");
                ImGui.Text($"  {_character.BoneCount} bones, {_character.ClipCount} clip(s)");

                if (_character.BoneOverflow)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        "  TOO MANY BONES - animation disabled, see the console");
                ImGui.Text($"  {_character.VisiblePieces}/{_character.PieceCount} geoset(s) drawn");

                // Head-texture status (the "gear texture on the head" bug). Green = the scalp is
                // covered by a hair geoset; red = the bald base body shows through.
                if (_character.ScalpCovered)
                    ImGui.TextColored(new Vector4(0.5f, 0.95f, 0.55f, 1f), $"  head: {_character.HairResolution}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), $"  HEAD ISSUE: {_character.HairResolution}");
                if (ImGui.TreeNode("Head / hair geosets"))
                {
                    foreach (var hline in _character.HeadDiag) ImGui.TextUnformatted("  " + hline);
                    ImGui.TreePop();
                }
                if (ImGui.Button("Capture diagnostics -> file")) _character.SaveDiagnostics();
                if (_character.LastDiagnosticPath is not null)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), "  saved - send me this file:");
                    ImGui.TextDisabled("  " + _character.LastDiagnosticPath);
                }

                if (_character.UnboundSlots > 0)
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                        $"  {_character.UnboundSlots} texture slot(s) unbound");

                // If ClipTime sits pegged at ClipDuration and never wraps, the
                // clip is being treated as a one-shot and the character will
                // hold its last frame forever.
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                    $"  {_character.ClipName}  {_character.ClipTime:F2}/{_character.ClipDuration:F2}s " +
                    $"x{_character.ClipRate:F2} move {_character.ClipMoveSpeed:F2} " +
                    $"{(_character.ClipLooping ? "loop" : "ONCE")}");
                // COMMANDED against MEASURED. The gait and the leg-cycle rate
                // are driven by the first; the second is what the world let us
                // actually do. They diverge exactly where you would want them
                // to - walking into a wall, sliding along one, climbing a step -
                // and having both here is what makes that legible rather than a
                // mystery about the animation.
                ImGui.Text($"  speed {_character.GroundSpeed,5:F2} commanded" +
                           $"  {_character.MeasuredSpeed,5:F2} measured  yd/s");

                // Mid-fade the pose is a mix of two clips. Empty means settled.
                if (_character.BlendFrom.Length > 0)
                    ImGui.TextDisabled(
                        $"  blending from {_character.BlendFrom} ({_character.BlendWeight:P0} left)");

                // The drawn body against the aim. Standing still and turning,
                // the offset should grow to -90 or +90 and HOLD there while you
                // steer, then sweep back to zero the moment you stop. If it is
                // pinned at zero, the body heading is not running.
                ImGui.TextDisabled(
                    $"  body {_character.BodyYawDegrees,4:F0} deg   " +
                    $"offset from aim {_character.MoveYawDegrees,4:F0} deg");

                bool drawCharacter = _character.Enabled;
                if (ImGui.Checkbox("Draw character", ref drawCharacter)) _character.Enabled = drawCharacter;

                // FIRST THING TO TRY if the model looks folded or exploded: bind
                // pose makes every skin matrix an exact identity, so what you
                // see is the raw mesh at the placement transform. If bind pose
                // is right and animation is wrong, the bug is in M2Animator; if
                // bind pose is already wrong, it is the transform below.
                bool bind = _character.BindPose;
                if (ImGui.Checkbox("Bind pose (no animation)", ref bind)) _character.BindPose = bind;

                // The one thing arithmetic cannot settle - a bounding box is
                // invariant under a half turn, so only your eyes can say which
                // way the model faces. Line it up with the capsule's spike and
                // tell me the number.
                float heading = _character.HeadingOffsetDegrees;
                if (ImGui.SliderFloat("Heading offset", ref heading, -180f, 180f))
                    _character.HeadingOffsetDegrees = heading;
                ImGui.SameLine();
                if (ImGui.Button("90")) _character.HeadingOffsetDegrees = 90f;

                float modelScale = _character.ModelScale;
                if (ImGui.SliderFloat("Model scale", ref modelScale, 0.25f, 3f))
                    _character.ModelScale = modelScale;

                float zOffset = _character.ZOffset;
                if (ImGui.SliderFloat("Model Z offset", ref zOffset, -2f, 2f))
                    _character.ZOffset = zOffset;

                // Whole body turns to face travel / only the hips turn / a
                // separate sideways clip and nothing turns.
                int strafeStyle = (int)_character.Strafe;
                if (ImGui.Combo("Strafe style", ref strafeStyle,
                        "Split (legs + torso)\0Whole body\0Lower body only\0Sideways clips\0"))
                    _character.Strafe = (CharacterRenderer.StrafeStyle)strafeStyle;

                // How much of the strafe angle the TORSO keeps. The legs always
                // take all of it. 1.0 is the old whole-body mode, 0.0 the old
                // lower-body one, and the real client sits around two thirds.
                float torsoFollow = _character.TorsoFollow;
                if (ImGui.SliderFloat("Torso follow", ref torsoFollow, 0f, 1f))
                    _character.TorsoFollow = torsoFollow;

                ImGui.Text($"  legs {_character.MoveYawDegrees,4:F0} deg" +
                           $"   torso {_character.MoveYawDegrees * _character.TorsoFollow,4:F0} deg");

                int torsoBone = _character.TorsoBone;
                if (ImGui.DragInt("Torso bone (spine)", ref torsoBone, 0.25f, -1,
                        Math.Max(_character.BoneCount - 1, 0)))
                    _character.TorsoBone = torsoBone;

                ImGui.Text($"  strafe angle {_character.MoveYawDegrees,5:F0} deg" +
                           (_character.TwistBone < 0 ? "   HIP BONE NOT FOUND" : ""));

                float chaseRate = _character.StationaryChaseRate;
                if (ImGui.SliderFloat("Turn: release catch-up rate", ref chaseRate, 0.25f, 2f))
                    _character.StationaryChaseRate = chaseRate;

                // Isolates the mechanism from the trigger. Stand still and drag.
                float force = _character.ForceAngleDegrees;
                if (ImGui.SliderFloat("Force angle (deg)", ref force, -120f, 120f))
                    _character.ForceAngleDegrees = force;
                ImGui.SameLine();
                if (ImGui.Button("0")) _character.ForceAngleDegrees = 0f;

                // Ticked: the hip bone's subtree turns, which is the legs.
                // Unticked: everything else turns, which is the upper body.
                bool subtree = _character.TwistSubtree;
                if (ImGui.Checkbox("Twist subtree (legs, not torso)", ref subtree))
                    _character.TwistSubtree = subtree;

                // Where the twist stops. It comes from the key-bone table, which
                // is a convention rather than a guarantee - if the torso turns
                // with the legs, drag this until it does not.
                int hipBone = _character.TwistBone;
                if (ImGui.DragInt("Twist bone (hips)", ref hipBone, 0.25f, -1,
                        Math.Max(_character.BoneCount - 1, 0)))
                    _character.TwistBone = hipBone;

                float maxTwist = _character.MaxTwistDegrees;
                if (ImGui.SliderFloat("Max twist (deg)", ref maxTwist, 0f, 180f))
                    _character.MaxTwistDegrees = maxTwist;

                bool dressed = _dressed;
                if (ImGui.Checkbox("Wear Battlegear of Might", ref dressed))
                {
                    _dressed = dressed;
                    _character.Equipment = dressed
                        ? CharacterEquipment.BattlegearOfMight()
                        : new CharacterEquipment();
                    _character.ApplyEquipment();
                }

                if (_character.Attached is not null)
                {
                    ImGui.Text($"  attached {_character.Attached.DrawnLastFrame}/{_character.Attached.MountCount} drawn");

                    bool drawAttached = _character.Attached.Enabled;
                    if (ImGui.Checkbox("Draw attached items", ref drawAttached))
                        _character.Attached.Enabled = drawAttached;

                    // Attached items are SEPARATE M2 MODELS, not geosets, so the
                    // geoset checkboxes below have no effect on them. Two
                    // mechanisms, two switches.
                    if (ImGui.TreeNode("Attached items"))
                    {
                        foreach (var (label, visible) in _character.Attached.Mounts.ToList())
                        {
                            bool on = visible;
                            if (ImGui.Checkbox($"{label}##att", ref on))
                                _character.Attached.SetMountVisible(label, on);
                        }
                        ImGui.TreePop();
                    }
                }

                // Hair lives in category 0 alongside the base body, so the
                // category checkbox for it would take the whole character with
                // it. This is the switch for testing hair against a helm.
                // Appearance. These are the CharSections lookup keys - flipping
                // them proves the table is finding real rows, and the face and
                // hair should visibly change.
                if (ImGui.TreeNode("Appearance"))
                {
                    int skin = _character.SkinId, face = _character.FaceId;
                    int hairStyle = _character.HairStyleId, hairColour = _character.HairColorId;
                    int facial = _character.FacialHairId;
                    bool changed = false;

                    changed |= ImGui.SliderInt("Skin", ref skin, 0, 10);
                    changed |= ImGui.SliderInt("Face", ref face, 0, 10);
                    changed |= ImGui.SliderInt("Hair style", ref hairStyle, 0, 15);
                    changed |= ImGui.SliderInt("Hair colour", ref hairColour, 0, 10);
                    changed |= ImGui.SliderInt("Facial hair", ref facial, 0, 10);

                    if (changed)
                    {
                        _character.SkinId = skin;
                        _character.FaceId = face;
                        _character.HairStyleId = hairStyle;
                        _character.HairColorId = hairColour;
                        _character.FacialHairId = facial;
                        _character.Reload();
                    }

                    ImGui.TreePop();
                }

                bool hideHair = _character.HideHair;
                if (ImGui.Checkbox("Hide hair", ref hideHair))
                {
                    _character.HideHair = hideHair;
                    _character.ApplyEquipment();
                }

                // FLICKER HUNT. Z-fighting is two surfaces in the same place,
                // so the fastest way to name the pair is to switch one half off
                // and see whether it stops. One checkbox per category that is
                // actually being drawn, with its variant, so the list is short
                // and every entry means something.
                if (ImGui.TreeNode("Geosets drawn"))
                {
                    foreach (var (category, variant) in _character.ActiveGeosets)
                    {
                        bool on = !_character.HiddenCategories.Contains(category);
                        if (ImGui.Checkbox($"cat {category} (variant {variant})##geo{category}", ref on))
                        {
                            if (on) _character.HiddenCategories.Remove(category);
                            else _character.HiddenCategories.Add(category);
                            _character.ApplyEquipment();
                        }
                    }

                    // Soloing beats hiding for z-fighting: a fight needs both
                    // halves, so switching one off only proves a pair stopped.
                    // Stepping through one geoset at a time says which.
                    int solo = _character.SoloGeoset;
                    if (ImGui.SliderInt("Solo one geoset (-1 = all)", ref solo, -1,
                            Math.Max(_character.ActiveGeosets.Count - 1, 0)))
                    {
                        _character.SoloGeoset = solo;
                        _character.ApplyEquipment();
                    }

                    if (ImGui.Button("Show all categories"))
                    {
                        _character.HiddenCategories.Clear();
                        _character.SoloGeoset = -1;
                        _character.ApplyEquipment();
                    }

                    ImGui.TreePop();
                }

                bool allGeosets = _character.ShowAllGeosets;
                if (ImGui.Checkbox("All geosets", ref allGeosets)) _character.ShowAllGeosets = allGeosets;

                bool magenta = _character.MagentaUnbound;
                if (ImGui.Checkbox("Magenta unbound", ref magenta)) _character.MagentaUnbound = magenta;

                float charCut = _character.AlphaCutoff;
                if (ImGui.SliderFloat("Character alpha cut", ref charCut, 0f, 1f))
                    _character.AlphaCutoff = charCut;
            }
            else
            {
                ImGui.Text("  not loaded - see the console");
            }
            }

            if (ImGui.CollapsingHeader("Collision"))
            {
            if (_collision is not null)
            {
                ImGui.Text($"  {_collision.TriangleCount:N0} triangles, {_collision.NodeCount:N0} nodes");
                if (_vmaps is not null)
                    ImGui.Text($"  {_vmaps.SpawnsUsed}/{_vmaps.SpawnsSeen} spawns, " +
                               $"{_vmaps.DistinctUnresolved} model(s) with no .vmo");
                ImGui.Text($"  built in {_collisionBuildSeconds:F1}s");

                if (_collisionDebug is not null)
                {
                    bool show = _collisionDebug.Enabled;
                    if (ImGui.Checkbox("Show collision (C)", ref show))
                        SetCollisionDebugEnabled(show);

                    bool solid = _collisionDebug.Solid;
                    if (ImGui.Checkbox("Solid", ref solid)) _collisionDebug.Solid = solid;

                    // Isolate whatever last blocked you. One building's shell
                    // against that same building rendered is a single glance;
                    // a million triangles of wireframe is not.
                    bool isolate = _collisionDebug.SourceFilter >= 0;
                    if (ImGui.Checkbox("Isolate blocker", ref isolate))
                    {
                        _collisionDebug.SourceFilter = isolate && _controller is not null
                            ? _collision.SourceIdOf(_controller.LastBlockTriangle)
                            : -1;
                    }

                    // Live collision shift. Nudge until the wireframe sits on
                    // the geometry you can see, then bake the value into the
                    // loader and set this back to zero.
                    var offset = _collision.Offset;
                    var raw = new System.Numerics.Vector3(offset.X, offset.Y, offset.Z);
                    if (ImGui.DragFloat3("Collision offset", ref raw, 0.05f, -20f, 20f))
                        _collision.Offset = raw;

                    if (ImGui.Button("Nudge along facing") && _controller is not null)
                    {
                        var f = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0);
                        _collision.Offset += f * 0.25f;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Reset offset")) _collision.Offset = Vector3.Zero;

                    if (_collisionDebug.SourceFilter >= 0)
                        ImGui.Text($"    showing only {_collision.SourceOf(_controller?.LastBlockTriangle ?? -1)}");
                }

                // Live probes. Stand next to something solid and face it: if
                // "ahead" stays empty, the geometry is not where it looks like
                // it is, and that is a data problem rather than a movement one.
                if (_controller is not null)
                {
                    var mid = _controller.Position + new Vector3(0, 0, _config.Movement.Height * 0.5f);
                    var facing = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0);

                    var ahead = _collision.Raycast(mid, facing, 60f);
                    if (ahead is not null)
                    {
                        var p = ahead.Value.Point;
                        ImGui.Text($"  ahead  {ahead.Value.Distance,6:F2} yd at ({p.X:F1}, {p.Y:F1}, {p.Z:F1})");
                        ImGui.Text($"         from {_collision.SourceOf(ahead.Value.Triangle)}");
                    }
                    else
                    {
                        ImGui.Text("  ahead    nothing within 60");
                    }

                    // What actually stopped you, as opposed to what happens to
                    // be in front of you.
                    if (_controller.HasBlock)
                    {
                        var b = _controller.LastBlockPoint;
                        var n = _controller.LastBlockNormal;
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                            $"  BLOCKED at ({b.X:F2}, {b.Y:F2}, {b.Z:F2})");
                        ImGui.Text($"    from {_collision.SourceOf(_controller.LastBlockTriangle)}");
                        ImGui.Text($"    normal ({n.X:F2}, {n.Y:F2}, {n.Z:F2})");
                        ImGui.Text($"    you are at ({_controller.Position.X:F2}, " +
                                   $"{_controller.Position.Y:F2}, {_controller.Position.Z:F2})");
                        if (_controller.LastPushOut > 0.001f)
                            ImGui.Text($"    pushed out {_controller.LastPushOut:F2} yd this frame");
                    }
                }
            }
            else
            {
                ImGui.Text("  terrain only (no vmaps)");
            }
            }

            var cam = _window.Camera;
            var cp = cam.Position;
            if (ImGui.CollapsingHeader("Camera", ImGuiTreeNodeFlags.DefaultOpen))
            {
            ImGui.Text($"  {cp.X,9:F1} {cp.Y,9:F1} {cp.Z,9:F1}");
            ImGui.Text($"  yaw {cam.Yaw * 180f / MathF.PI,5:F0}  pitch {cam.Pitch * 180f / MathF.PI,4:F0}  " +
                       $"dist {cam.EffectiveDistance,4:F1}/{cam.Distance,4:F1}");

            // Non-zero orbit means the camera has been swung off the character's
            // back. It should return to 0 the moment you move.
            ImGui.Text($"  orbit {cam.OrbitYaw * 180f / MathF.PI,5:F0} deg   " +
                       $"view {cam.ViewYaw * 180f / MathF.PI,5:F0} deg");

            // Field of view, turn speed, eye height, mouse sensitivity and the
            // raw-cursor switch are now Escape / Camera and controls. They stopped
            // being config-file-plus-restart settings for a good reason - a spell
            // at FOV 45 flattened the world enough to be disliked on sight without
            // it being obvious which knob did it - and the modal keeps them live
            // AND persists them, which is what that episode actually wanted.
            ImGui.TextDisabled(
                $"  fov {cam.FieldOfViewDegrees:F0} deg   turn {_turnSpeed * 180f / MathF.PI:F0} deg/s   " +
                $"eye {cam.EyeHeight:F2}");

            // Mouse-look diagnostics. Read these WHILE dragging - each line
            // eliminates one link in the chain, so "the mouse does nothing"
            // becomes a specific broken link instead of a theory:
            //   buttons never light  -> the press is not reaching us at all
            //   buttons light, captured no -> ImGui is eating the click
            //   captured yes, moves frozen -> no motion events in this mode
            //   moves climbing, applied frozen -> deltas rejected as oversized
            //   applied climbing, delta 0,0 -> the cursor mode reports no motion
            ImGui.Text($"  mouse  L{(_window.MouseLeftDown ? 1 : 0)} R{(_window.MouseRightDown ? 1 : 0)}   " +
                       $"captured {(_window.MouseCaptured ? "yes" : "no")}   cursor {_window.CursorModeName}");
            ImGui.Text($"  moves {_window.MouseMoveEvents}  applied {_window.MouseLookEvents}  " +
                       $"last delta ({_window.LastMouseDelta.X,6:F1},{_window.LastMouseDelta.Y,6:F1})");

            // If look is dead, raw cursor is the first thing to turn off, and it
            // now lives in Escape / Camera and controls with that sentence on it.
            ImGui.TextDisabled(
                $"  raw cursor {(_window.RawCursor ? "on" : "OFF")}   " +
                $"sensitivity x{_window.MouseSensitivity:F2}");

            ImGui.Separator();

            if (_controller is not null)
            {
                bool flying = _controller.Flying;
                if (ImGui.Checkbox("Fly (F)", ref flying)) _controller.Flying = flying;
            }

            ImGui.Checkbox("Show player capsule", ref _showPlayerMarker);

            if (_terrain is not null)
            {
                int mode = _terrain.DebugMode;
                if (ImGui.Combo("Shading", ref mode,
                        "Textured\0Normals\0UVs\0Flat\0Splat mask\0Untextured\0"))
                    _terrain.DebugMode = mode;

                float scale = _terrain.TextureScale;
                if (ImGui.SliderFloat("Texture repeat", ref scale, 1f, 32f))
                    _terrain.TextureScale = scale;

                if (_terrain.TileCount > 0)
                    ImGui.Text($"tileset textures {_terrain.FirstTileTextureCount}");
            }

            ImGui.Separator();
            ImGui.TextWrapped("W/S walk, A/D turn, Q/E strafe (holding RIGHT mouse swaps A/D to " +
                              "strafe). Arrow keys turn and walk, PgUp/PgDn look up and down, " +
                              "Shift walk, Space jump, F toggle fly, C collision " +
                              "(Space/Ctrl for height while flying, Shift boosts). " +
                              "LEFT mouse swings the camera around your character without turning him; " +
                              "RIGHT mouse turns him and the camera together; moving re-centres the " +
                              "camera behind him. Wheel to zoom, Esc for the game menu.");
            }
        }
        ImGui.End();
        DrawSpellFxInspector();

        // The Water Tuning and Foliage Tuning windows are gone: every knob in
        // both is now Escape / Graphics, under Water and Ground clutter, where it
        // persists instead of evaporating at exit. The methods that drew them are
        // deleted rather than left unreferenced - a dead HUD window is a second
        // place for a value to disagree with itself.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Camera pose is per character and must be written before the network identity and world
        // camera are torn down. Distance is the user's requested boom, not collision pull-in.
        try { SaveCameraPoseForSession(); } catch { /* never block shutdown */ }

        // The creator location must be flushed before anything is torn down.
        try { UpdateCreatorLocationPersist(force: true); } catch { /* never block shutdown */ }

        _net?.Dispose();
        StopSocketTrace();
        StopMovementTrace();
        _wireLog.Dispose();
        _batchPortraitTarget?.Dispose();
        _batchPortraitTarget = null;
        _glue?.Dispose();
        DisposeGameplayUi();
        DisposePortraits();
        _creatures?.Dispose();
        _worldNames?.Dispose();
        _fishingLineRenderer?.Dispose();
        _minimapInteriorComposite?.Dispose();
        _minimapInteriorComposite = null;
        _unitShadows?.Dispose();

        try { _collisionBuildTask?.GetAwaiter().GetResult(); }
        catch { /* Shutdown must continue after a failed background build. */ }
        _gpuProfiler?.Dispose();
        _gpuProfiler = null;
        _skin?.Dispose();
        _skin = null;
        _character?.Dispose();
        _collisionDebug?.Dispose();
        _partySight?.Dispose();
        _xrayDebug?.Dispose();
        _xrayNavDebug?.Dispose();
        DisposeRealPortals();
        _doodads?.Dispose();
        _liquid?.Dispose();
        _foliage?.Dispose();
        _wmo?.Dispose();
        _terrain?.Dispose();
        DisposeLoadingArt();
        _sky?.Dispose();
        _weatherPrecipitation?.Dispose();
        _skybox?.Dispose();
        _glow?.Dispose();
        _painterly?.Dispose();
        _glueAdd?.Dispose();
        _uploads?.Dispose();
        _assetWorkers?.Dispose();

        // Renderer disposal joins any asset-preparation workers before the
        // extractor is detached and its shared archive handles are closed.
        AdtTerrainReader.StormLibExtractor = null;
        _particles?.Dispose();
        _spellEffectMeshes?.Dispose();
        _spellRibbons?.Dispose();
        _spellChainBeamRenderer?.Dispose();
        _spellParticles?.Dispose();
        _audioMixer?.Dispose();   // the device; SpellSoundSystem is policy over it and owns nothing
        _mpq?.Dispose();
    }
}
