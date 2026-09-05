using System.Numerics;
using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient.Engine;

/// <summary>A completed world gesture. Modifier state is captured with the gesture,
/// not polled later by the game loop, so releasing Shift beside the mouse cannot turn
/// a Shift+Left editor move into an ordinary click.</summary>
public readonly record struct WorldMouseClick(
    MouseButton Button, Vector2 Position, bool ShiftDown = false,
    bool CtrlDown = false, bool AltDown = false);

/// <summary>
/// Window, GL context, main loop, input and the debug HUD.
///
/// Deliberately thin: Silk.NET is a binding layer, not an engine, so this owns
/// the loop rather than plugging into someone else's. That is what makes the
/// painterly render mode a shader variant instead of a fight with a framework.
///
/// MOUSE LOOK IS POLLED, NOT PURELY EVENT DRIVEN
///   The original version drove capture entirely from MouseDown and MouseUp and
///   flipped the cursor to CursorMode.Raw on the way in. Three things can each
///   kill that outright, and none of them announces itself:
///
///     1. Raw cursor mode is not universally supported. Setting it can throw, or
///        silently fail, and on some drivers it stops the position callback
///        reporting usable coordinates at all - so MouseMove fires and every
///        delta is zero.
///     2. Switching to Raw or Disabled makes the reported cursor position jump
///        into a different coordinate space. The first delta after capture is
///        then nonsense, large enough to spin the view somewhere arbitrary.
///     3. A MouseUp delivered while ImGui owned the mouse, or with the pointer
///        outside the window, is simply never seen, so capture sticks on or off.
///
///   So capture is now derived from POLLING the button state every frame, with
///   the events kept only for the motion delta. The first delta after capture is
///   discarded, oversized deltas are dropped, and the cursor mode falls back
///   from Raw to Hidden if the platform refuses it.
///
///   Every one of those states is published for the HUD - see the mouse
///   diagnostics below. "The mouse does nothing" should be a number, not a
///   theory.
///
/// LEFT AND RIGHT DRAG MEAN DIFFERENT THINGS
///   Left swings the camera around the character without turning him. Right
///   turns him and takes the camera along. See Camera.OrbitYaw - the separation
///   lives there, and this class only decides which of the two a given drag
///   feeds.
/// </summary>
public sealed class ClientWindow : IDisposable
{
    private static readonly GraphicsAPI GraphicsApi = new(
        ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
        new APIVersion(3, 3));

    private readonly ClientConfig _config;

    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imgui = null!;
    private IMouse? _mouse;
    private string _hardwareCursorKey = "";
    private bool _hardwareCursorRequested;
    private string _hardwareCursorProposalKey = "";
    private HardwareCursorImage? _hardwareCursorProposal;

    public GL Gl => _gl;
    public Camera Camera { get; } = new();
    public Vector3 SkyColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public int FramebufferSamples { get; private set; }
    public bool MultisamplingEnabled
    {
        get => _gl is not null && _gl.IsEnabled(EnableCap.Multisample);
        set
        {
            if (_gl is null) return;
            if (value) _gl.Enable(EnableCap.Multisample);
            else _gl.Disable(EnableCap.Multisample);
        }
    }

    /// <summary>
    /// The live swap-interval request. Setting this after the context exists is
    /// important on drivers that ignore the value supplied during window creation.
    /// </summary>
    /// <summary>True fullscreen at the desktop video mode. Live: flipping it
    /// switches the window state immediately; leaving it restores the configured
    /// windowed size. Alt+Enter routes here too.</summary>
    public bool Fullscreen
    {
        get => _window is not null
            ? _window.WindowState == WindowState.Fullscreen
            : _config.Window.Fullscreen;
        set
        {
            _config.Window.Fullscreen = value;
            if (_window is null) return;
            if (value)
            {
                // Size to the monitor's DESKTOP mode before flipping the state:
                // GLFW picks the fullscreen video mode from the window size, so
                // entering at the configured windowed size (1600x900 on a 4K
                // panel) rendered the client into a fraction of the screen.
                var resolution = _window.Monitor?.VideoMode.Resolution;
                if (resolution is { X: > 0, Y: > 0 } native)
                    _window.Size = native;
                _window.WindowState = WindowState.Fullscreen;
            }
            else
            {
                // Leaving fullscreen returns to whichever windowed state was configured -
                // Maximized if that is set, otherwise the configured windowed size.
                _window.WindowState = _config.Window.Maximized ? WindowState.Maximized : WindowState.Normal;
                if (!_config.Window.Maximized)
                    _window.Size = new Vector2D<int>(_config.Window.Width, _config.Window.Height);
            }
            // The transition does not reliably raise FramebufferResize on every
            // driver - sync the viewport/aspect to the real framebuffer now.
            if (_gl is not null) HandleResize(_window.FramebufferSize);
        }
    }

    /// <summary>Maximized to the desktop work area, short of true fullscreen (Fullscreen picks the
    /// monitor's own video mode and hides the taskbar; this keeps window chrome and just grows to
    /// fill the screen). Ignored while <see cref="Fullscreen"/> is on - that state wins, and this
    /// flag is simply what windowed mode returns to once Fullscreen is turned back off.</summary>
    public bool Maximized
    {
        get => _window is not null
            ? _window.WindowState == WindowState.Maximized
            : _config.Window.Maximized;
        set
        {
            _config.Window.Maximized = value;
            if (_window is null || Fullscreen) return;
            _window.WindowState = value ? WindowState.Maximized : WindowState.Normal;
            if (!value)
                _window.Size = new Vector2D<int>(_config.Window.Width, _config.Window.Height);
            if (_gl is not null) HandleResize(_window.FramebufferSize);
        }
    }

    /// <summary>
    /// The monitor's current desktop mode, or (0,0) when there is no monitor to ask. This is the
    /// same value the Fullscreen setter sizes to, so the Video page and the fullscreen flip agree
    /// about what "native" means.
    /// </summary>
    public (int Width, int Height) NativeResolution
    {
        get
        {
            var resolution = _window?.Monitor?.VideoMode.Resolution;
            return resolution is { X: > 0, Y: > 0 } native ? (native.X, native.Y) : (0, 0);
        }
    }

    /// <summary>
    /// Every video mode the current monitor reports, as plain pairs. Wrapped because this is the
    /// one place the client asks the driver a question it is allowed to refuse: a backend with no
    /// monitor (headless/scripted runs) or a driver that declines enumeration must degrade to the
    /// caller's own fallback list rather than take the settings page down with it.
    /// </summary>
    public IReadOnlyList<(int Width, int Height)> AvailableVideoModes()
    {
        try
        {
            var monitor = _window?.Monitor;
            if (monitor is null) return [];
            var modes = new List<(int, int)>();
            foreach (var mode in monitor.GetAllVideoModes())
                if (mode.Resolution is { X: > 0, Y: > 0 } size) modes.Add((size.X, size.Y));
            return modes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[display] video mode enumeration unavailable: {ex.Message}");
            return [];
        }
    }

    public bool VSync
    {
        get => _window is not null ? _window.VSync : _config.Window.VSync;
        set
        {
            _config.Window.VSync = value;
            if (_window is not null) _window.VSync = value;
            Console.WriteLine($"[display] VSync {(value ? "on" : "off")}");
        }
    }

    /// <summary>Raised once after the GL context exists — build GPU resources here.</summary>
    public event Action<GL>? OnLoad;

    /// <summary>
    /// Whether the native game window currently owns foreground focus. Audio uses
    /// this to avoid constructing its first Windows MCI/DirectShow graph while
    /// startup is running in the background; a graph born under that contention
    /// can remain permanently underrun until the process is restarted.
    /// </summary>
    public bool IsFocused { get; private set; } = true;

    /// <summary>Raised every frame before rendering. Argument is delta seconds.</summary>
    public event Action<float>? OnUpdate;

    /// <summary>Raised every frame to draw the world.</summary>
    public event Action<float>? OnRender;

    /// <summary>Raised inside the ImGui frame — draw HUD windows here.</summary>
    public event Action? OnGui;

    /// <summary>Raised after the HUD is built but BEFORE the ImGui draw pass - a seam for extra GL
    /// passes (the additive glue overlay) that must composite UNDER the HUD.</summary>
    public event Action? OnOverlay;

    /// <summary>Raised AFTER the ImGui draw pass - the additive glue overlay's "on top" mode, drawn
    /// over the HUD (max brightness, no panel dimming; washes text a touch).</summary>
    public event Action? OnOverlayTop;

    /// <summary>
    /// Raised while the render context is still current. GPU owners must
    /// release queries, buffers, textures and programs here rather than after
    /// Window.Run returns, when Silk has already torn the context down.
    /// </summary>
    public event Action? OnClosing;

    // Input state
    private readonly HashSet<Key> _held = [];

    /// <summary>
    /// Keys whose DOWN edge arrived since the last binding scan, with the modifier state as it
    /// was at that instant.
    ///
    /// _held alone cannot answer "was this pressed?", only "is it down now". A tap that starts
    /// and ends between two frames does Add-then-Remove before anything reads it, so the press
    /// is simply gone - and the busier the scene, the longer the frame, the wider that hole.
    /// At 30fps it is a 33ms window; in a 300-bot fight it is much worse. This is the record
    /// that survives it, drained once per frame by UpdateBindingLatches.
    ///
    /// The modifiers are captured HERE rather than read at scan time on purpose: for a quick
    /// Ctrl+F the Ctrl may also be released before the next frame, and a chord resolved against
    /// scan-time modifiers would come out as a bare F.
    /// </summary>
    public readonly record struct KeyPress(bool Alt, bool Control, bool Shift, bool Super);

    private readonly Dictionary<Key, KeyPress> _pressedSinceScan = [];

    /// <summary>Take the presses seen since the last call and clear the record.</summary>
    public IReadOnlyList<KeyValuePair<Key, KeyPress>> DrainPressedSinceScan()
    {
        if (_pressedSinceScan.Count == 0) return [];
        var drained = _pressedSinceScan.ToArray();
        _pressedSinceScan.Clear();
        return drained;
    }
    private Vector2 _lastMouse;
    private bool _mouseCaptured;
    private bool _skipNextDelta;
    private float _pendingYaw, _pendingOrbitYaw, _pendingPitch, _pendingZoom;
    private bool _previousRightDown;

    /// <summary>
    /// Which button owns the current drag, and therefore what horizontal motion
    /// means.
    ///
    ///   LEFT  swings the camera around the character. He keeps facing where he
    ///         was, so you can walk north and look at your own face.
    ///   RIGHT turns the character, and the camera comes with him.
    ///
    /// Right wins when both are held, because turning is the stronger intent.
    /// </summary>
    private bool _lookTurnsCharacter;

    /// <summary>
    /// A single mouse-move event larger than this is discarded rather than
    /// applied. It is not a real hand movement; it is the cursor changing
    /// coordinate space, and applying it throws the view somewhere random.
    /// </summary>
    private const float MaxDeltaPixels = 300f;

    // Frame timing
    private double _fpsAccumulator;
    private int _framesSinceSample;
    public float Fps { get; private set; }
    public double FrameMs { get; private set; }

    // ---- Frame boundary breakdown (PLAN_07) ------------------------------
    // Handbook 3.30 says a slow frame with low GPU and low update time "points
    // at unmeasured UI/presentation/driver pacing and requires instrumenting
    // that boundary". These four are that instrument. Without them every
    // millisecond spent here lands in the hitch recorder's "unaccounted"
    // bucket, which names the region but not the cause.

    /// <summary>Mouse polling and camera input application, per frame.</summary>
    public double InputMilliseconds { get; private set; }

    /// <summary>ImGui's own per-frame update (not our HUD code).</summary>
    public double ImguiUpdateMilliseconds { get; private set; }

    /// <summary>Building and drawing the HUD: OnGui plus the ImGui draw pass.</summary>
    public double GuiMilliseconds { get; private set; }

    /// <summary>
    /// Our HUD code alone: the OnGui handlers building ImGui windows. Pure CPU,
    /// none of our GL. Baseline is ~0.25 ms and it does not move.
    /// </summary>
    public double HudMilliseconds { get; private set; }

    /// <summary>
    /// <c>_imgui.Render()</c> alone - the LAST GL submission of the frame, and
    /// therefore where the driver's implicit flush lands.
    ///
    /// Split out from <see cref="GuiMilliseconds"/> because the combined number
    /// was actively misleading. Measured 2026-07-25: hitch records blamed
    /// "hud-imgui" for 27-33 ms frames while our HUD cost 0.25 ms on every
    /// neighbouring frame - the time was the driver blocking inside ImGui's draw
    /// calls, not ImGui. Same mistake shape as the three in SYSTEM_STREAMING 1.2:
    /// a bracket charged for a wait it does not own. Two numbers, no ambiguity.
    /// </summary>
    public double ImguiRenderMilliseconds { get; private set; }

    /// <summary>
    /// End of one render to the start of the next update: the buffer swap and
    /// the platform event pump. On a driver that stalls behind shared-context
    /// uploads, or on a vsync wait, the time shows up HERE and nowhere else.
    ///
    /// NOT TRUE ON THIS HARDWARE - the sentence above is left standing as the
    /// assumption it was. Measured on the Iris Xe: present is 0.05-0.9 ms even on
    /// 26 ms frames. The driver does not block in the swap; it blocks in the next
    /// GL call that needs a buffer, which is whichever of update, render or the
    /// ImGui draw comes first. Turning vsync off makes the whole cost vanish.
    /// Read present as "swap and event pump", never as "the driver".
    /// </summary>
    public double PresentMilliseconds { get; private set; }

    private long _renderEndStamp;

    // ── mouse diagnostics, all published for the HUD ─────────────────────────

    /// <summary>True while the mouse is captured for camera look.</summary>
    public bool MouseCaptured => _mouseCaptured;

    /// <summary>
    /// CRPG free view: the LEFT button is marquee selection, never camera look.
    /// The right button keeps the look/steer behaviour unchanged.
    /// </summary>
    public bool FreeSelectMode { get; set; }

    /// <summary>Command View schemes that make the view angle a knob: a right-drag turns but
    /// never tilts, so the accumulated mouse pitch is dropped instead of applied.</summary>
    public bool LookPitchLocked { get; set; }

    /// <summary>The mouse-look yaw (radians) applied to the camera this frame — the RTS scheme
    /// swings the rig around its ground focus by this much on top of the keyboard turn.</summary>
    public float AppliedLookYaw { get; private set; }

    /// <summary>
    /// An armed in-world editor owns left press/drag/release gestures. This is
    /// separate from <see cref="FreeSelectMode"/> because editing a waypoint in
    /// the ordinary character view must not redirect the wheel to the free-fly
    /// rig. Right-drag remains camera look in either mode.
    /// </summary>
    public bool LeftButtonReservedForWorldClicks { get; set; }

    /// <summary>Pure ownership rule shared by event and polling input paths.</summary>
    public static bool CameraLookRequested(bool leftDown, bool rightDown,
        bool freeSelectMode, bool leftButtonReserved)
        => rightDown || (leftDown && !freeSelectMode && !leftButtonReserved);

    /// <summary>Wheel clicks accumulated while <see cref="FreeSelectMode"/> is up —
    /// the free view's altitude control, consumed once per frame by the game loop
    /// (which flies the rig; the window knows nothing about the controller).</summary>
    private float _freeFlightScroll;

    /// <summary>Drain the free-view wheel accumulator (positive = wheel up).</summary>
    public float TakeFreeFlightScroll()
    {
        float value = _freeFlightScroll;
        _freeFlightScroll = 0f;
        return value;
    }

    public bool MouseLeftDown { get; private set; }
    public bool MouseRightDown { get; private set; }

    /// <summary>Middle button, polled. Used by the in-world group picker.</summary>
    public bool MouseMiddleDown { get; private set; }
    public bool MouseButton4Down { get; private set; }
    public bool MouseButton5Down { get; private set; }
    public float BindingWheelDelta { get; private set; }

    /// <summary>Cursor position in window pixels (top-left origin).</summary>
    public Vector2 MousePosition => _mouse?.Position ?? default;

    private struct WorldPress
    {
        public bool Active;
        public bool AcceptDragRelease;
        public bool ShiftDown;
        public bool CtrlDown;
        public bool AltDown;
        public Vector2 Position;
        public float Travel;
    }

    private WorldPress _leftWorldPress;
    private WorldPress _rightWorldPress;
    private readonly Queue<WorldMouseClick> _worldClicks = new();
    private const float WorldClickDragPixels = 4f;

    /// <summary>Take one clean left/right release that did not become a camera drag.</summary>
    public bool TryDequeueWorldClick(out WorldMouseClick click)
    {
        if (_worldClicks.TryDequeue(out click)) return true;
        click = default;
        return false;
    }

    public void ClearWorldClicks()
    {
        _worldClicks.Clear();
        _leftWorldPress = default;
        _rightWorldPress = default;
    }

    /// <summary>Live framebuffer size in physical pixels, for UI layout and cursor unprojection.</summary>
    public Vector2 FramebufferSize
        => _window is null
            ? Vector2.One
            : new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);

    /// <summary>Motion events seen since start. Frozen means no events arrive at all.</summary>
    public int MouseMoveEvents { get; private set; }

    /// <summary>Motion events actually applied to the camera. The gap is what was rejected.</summary>
    public int MouseLookEvents { get; private set; }

    /// <summary>Last accepted delta in pixels. Zero while moving means the delta is the problem.</summary>
    public Vector2 LastMouseDelta { get; private set; }

    /// <summary>What the cursor mode actually ended up as, which is not always what was asked for.</summary>
    public string CursorModeName { get; private set; } = "Normal";

    /// <summary>Starts one cursor-authority frame. A custom cursor must renew its claim.</summary>
    public void BeginHardwareCursorFrame()
    {
        _hardwareCursorRequested = false;
        _hardwareCursorProposalKey = "";
        _hardwareCursorProposal = null;
    }

    /// <summary>
    /// Proposes a real OS cursor for this frame. Several surfaces may refine the cursor while the
    /// HUD is drawn; only the final proposal is installed by <see cref="EndHardwareCursorFrame"/>.
    /// This avoids changing Point -> Loot -> Point inside every frame, which Windows exposes as a
    /// rapidly flickering cursor even though the final classification is stable.
    /// </summary>
    private string _hardwareCursorCandidateKey = "";
    private int _hardwareCursorCandidateFrames;
    private bool _softwareCursorRequested;
    private bool _softwareCursorActive;

    /// <summary>A drawn (software) cursor owns the pointer this frame: the OS pointer is hidden so
    /// the two are never both visible (owner, 2026-09-02: "needs to be either/or").</summary>
    public void RequestSoftwareCursor() => _softwareCursorRequested = true;

    /// <summary>Dev: log every hardware cursor commit decision.</summary>
    public bool CursorTrace { get; set; }

    public bool UseHardwareCursor(string key, in HardwareCursorImage image)
    {
        if (_mouse is null || _mouseCaptured || string.IsNullOrWhiteSpace(key)) return false;
        if (image.Width <= 0 || image.Height <= 0 || image.Rgba.Length == 0) return false;
        _hardwareCursorRequested = true;
        _hardwareCursorProposalKey = key;
        _hardwareCursorProposal = image;
        return true;
    }

    /// <summary>Commits the final proposal, or releases stale authority when none was made.</summary>
    public void EndHardwareCursorFrame()
    {
        if (_mouse is null) return;
        try
        {
            ICursor cursor = _mouse.Cursor;
            bool softwareNow = _softwareCursorRequested;
            _softwareCursorRequested = false;
            if (softwareNow && !_mouseCaptured)
            {
                if (!_softwareCursorActive || cursor.CursorMode != CursorMode.Hidden)
                {
                    cursor.CursorMode = CursorMode.Hidden;
                    _softwareCursorActive = true;
                    CursorModeName = "Software";
                }
                return;
            }
            if (_softwareCursorActive && !_mouseCaptured)
            {
                cursor.CursorMode = CursorMode.Normal;
                _softwareCursorActive = false;
            }
            if (_hardwareCursorRequested && _hardwareCursorProposal is { } image)
            {
                cursor.CursorMode = CursorMode.Normal;
                // Hysteresis: a stem must hold for two frames before the OS cursor is rebuilt.
                // Rebuilding on every one-frame flicker leaked into a black square at the pointer.
                if (_hardwareCursorCandidateKey.Equals(_hardwareCursorProposalKey, StringComparison.OrdinalIgnoreCase))
                    _hardwareCursorCandidateFrames++;
                else
                {
                    _hardwareCursorCandidateKey = _hardwareCursorProposalKey;
                    _hardwareCursorCandidateFrames = 1;
                }
                bool settled = _hardwareCursorCandidateFrames >= 2 || _hardwareCursorKey.Length == 0;
                if (CursorTrace)
                    Console.WriteLine($"[cursor] proposal={_hardwareCursorProposalKey} candidateFrames={_hardwareCursorCandidateFrames} settled={settled} key={_hardwareCursorKey} type={cursor.Type}");
                if (settled && (!_hardwareCursorKey.Equals(_hardwareCursorProposalKey,
                        StringComparison.OrdinalIgnoreCase) || cursor.Type != CursorType.Custom))
                {
                    cursor.Image = new RawImage(image.Width, image.Height, image.Rgba);
                    cursor.HotspotX = 0;
                    cursor.HotspotY = 0;
                    cursor.Type = CursorType.Custom;
                    _hardwareCursorKey = _hardwareCursorProposalKey;
                    CursorModeName = $"Custom:{_hardwareCursorProposalKey}";
                }
                return;
            }
            if (_hardwareCursorKey.Length == 0) return;
            cursor.Type = CursorType.Standard;
            cursor.StandardCursor = StandardCursor.Default;
            _hardwareCursorKey = "";
            CursorModeName = cursor.CursorMode.ToString();
        }
        catch (Exception ex)
        {
            _hardwareCursorKey = "";
            Console.WriteLine($"[input] hardware cursor commit failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Raw cursor mode: unbounded look, cursor hidden and locked. Turn it OFF if
    /// look is dead - Hidden keeps the cursor in normal screen coordinates, which
    /// works everywhere but stops at the screen edge.
    /// </summary>
    public bool RawCursor { get; set; } = true;

    /// <summary>Multiplier on camera.mouseSensitivity, so a too-slow look is one drag away from fixed.</summary>
    public float MouseSensitivity { get; set; } = 1f;

    /// <summary>Same role as <see cref="MouseSensitivity"/> but for the right-click-held drag
    /// (which also turns the character) instead of the left-click-held orbit-only look.</summary>
    public float LookAroundSensitivity { get; set; } = 1f;

    /// <summary>
    /// Path to a TTF for the whole UI, or null for ImGui's own bitmap font. Set
    /// by Program.Main BEFORE Run(): ImGui rasterises its glyph atlas when the
    /// controller is constructed and there is no supported way to swap it after.
    /// </summary>
    public string? UiFontPath { get; set; }

    /// <summary>Pixel height to rasterise <see cref="UiFontPath"/> at. See UiFont.SizeFor.</summary>
    public int UiFontSize { get; set; } = 13;

    /// <summary>
    /// Maps the current framebuffer callback to the gameplay text scale (set by Program.Main to
    /// the same proportional current-window law GameplayUiScale uses), so maximising also rebakes
    /// exact gameplay fonts at their new draw size.
    /// </summary>
    public Func<float, float, float>? GameplayTextScaleRule { get; set; }

    /// <summary>
    /// The gameplay scale the game loop actually rendered with this frame. When the em targets
    /// it implies differ from the baked atlas (maximise, UI-scale change), the atlas rebuilds
    /// between frames - gameplay text must come from exact-size rasters, never an upscaled bake.
    /// </summary>
    public void EnsureGameplayTextScale(float scale) => _pendingGameplayTextScale = scale;

    private float _pendingGameplayTextScale = -1f;
    private uint _rebakedFontTexture;

    /// <summary>
    /// How many times larger than its display size the TTF atlas is rasterised. The glue screen
    /// draws this font much larger than the in-game UI (labels 17*s, the typed line 18*s, the red
    /// button captions up to ~29 px), so a 12 px atlas was being up-scaled and blurred. Rasterising
    /// N times larger and setting FontGlobalScale = 1/N keeps the in-game text its intended size
    /// but sourced from a hi-res atlas, so every larger glue size is DOWN-scaled - which is crisp.
    /// 1 = off.
    /// </summary>
    private const float FontSupersample = 3f;

    /// <summary>The supersampled UI face and the exact-size gameplay faces share one glyph-range
    /// definition (see <see cref="UI.GameTextLaw.GlyphRanges"/>) so their punctuation coverage
    /// can never drift apart. Without it ImGui's default Latin-1 range dropped the em-dash and
    /// friends to the '?' fallback even though FRIZQT carries them.</summary>
    private static nint UiGlyphRangesPtr => UI.GameTextLaw.GlyphRangesPtr;

    /// <summary>Whether the atlas was built from a real TTF (see <see cref="ApplyUiFontScale"/>).</summary>
    private bool _realUiFont;

    /// <summary>
    /// Set ImGui's FontGlobalScale for an interface-scale preference. THE ONLY
    /// legal way to change it after startup.
    ///
    /// With a real TTF the atlas is rasterised <see cref="FontSupersample"/>x
    /// larger than its display size, so 1/FontSupersample is not a free
    /// parameter - it is the compensation that makes in-game widgets come out
    /// their intended size. Assigning the user's scale over it multiplies the
    /// ENTIRE ImGui UI by FontSupersample (3x here), which blows the settings
    /// menu past the screen edge and puts its own slider and Okay/Cancel
    /// buttons out of reach - i.e. it breaks the one control that could undo
    /// it. Only the fallback bitmap face, which has no supersampled atlas, may
    /// be scaled by the preference. Interface scale reaches the real UI through
    /// WowSkin.Scale, not through this.
    ///
    /// <paramref name="fontScale"/> is the user's INDEPENDENT text-size
    /// preference and is the only thing allowed to move type on its own. It
    /// rides on top of the compensation, so it stays crisp all the way to
    /// FontSupersample (3x) - at exactly 3.0 the atlas is drawn 1:1 - and only
    /// starts up-scaling beyond that, which is why the menu caps it there.
    /// </summary>
    public void ApplyUiFontScale(float uiScale, float fontScale = 1f)
    {
        float f = Math.Clamp(fontScale, 0.5f, FontSupersample);
        ImGui.GetIO().FontGlobalScale =
            _realUiFont ? f / FontSupersample : Math.Clamp(uiScale, 0.5f, 4f) * f;
    }

    public ClientWindow(ClientConfig config) => _config = config;

    public GpuUploadWorker CreateGpuUploadWorker()
        => new(_window, GraphicsApi);

    /// <summary>When this binary was built, as a short display string ("" if it
    /// cannot be determined). Shown in the title bar and the Encounter Lab so a
    /// stale exe can never masquerade as a fresh build again.</summary>
    public static string BuildStamp { get; } = ComputeBuildStamp();

    private static string ComputeBuildStamp()
    {
        try
        {
            string? path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path)) path = Environment.ProcessPath;
            return path is { Length: > 0 }
                ? File.GetLastWriteTime(path).ToString("MMM d HH:mm")
                : "";
        }
        catch { return ""; }
    }

    public void Run()
    {
        var startup = Stopwatch.StartNew();
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(_config.Window.Width, _config.Window.Height),
            // The title stays clean by owner request; the build stamp lives in
            // the Encounter Lab toolbar (ClientWindow.BuildStamp), where the
            // "which binary is this" question actually gets asked.
            Title = _config.Window.Title,
            VSync = _config.Window.VSync,
            Samples = Math.Clamp(_config.Render.MsaaSamples, 0, 16),
            // ALWAYS created windowed: creating directly in fullscreen picks the
            // video mode from the configured windowed size, which on a high-res
            // panel rendered the client into a fraction of the screen. HandleLoad
            // flips to fullscreen through the property, which sizes to the
            // monitor's desktop mode first and syncs the viewport.
            WindowState = WindowState.Normal,
            // 3.3 core is the floor; anything modern exceeds it, and it keeps the
            // client runnable on older hardware and inside VMs.
            API = GraphicsApi,
        };

        _window = Window.Create(options);

        _window.Load += HandleLoad;
        _window.Update += dt => HandleUpdate((float)dt);
        _window.Render += dt => HandleRender((float)dt);
        _window.FramebufferResize += HandleResize;
        _window.FocusChanged += focused =>
        {
            IsFocused = focused;
            // DROP EVERY HELD KEY ON THE WAY OUT. The window is told a key went down and up by
            // events, and no KeyUp is delivered for a key that was still held when focus left -
            // so an Alt+Tab used to leave Alt (and Tab) latched down in _held for the rest of the
            // session, until those keys happened to be pressed and released again. Everything
            // downstream that asks "is Ctrl/Shift/Alt down" then answered wrongly: chords
            // resolved to the wrong binding, and the free view's modifier-sensitive number keys
            // were the most visible casualty.
            if (!focused) { _held.Clear(); _pressedSinceScan.Clear(); }
            Console.WriteLine($"[window] focus {(focused ? "gained" : "lost")}");
        };
        _window.Closing += HandleClosing;

        Console.WriteLine($"[startup] window created          {startup.Elapsed.TotalSeconds,6:F2}s");
        _window.Run();
    }

    /// <summary>The MSUI crest as the live window/taskbar icon (the exe icon is
    /// baked by the build via ApplicationIcon; GLFW windows need it set at
    /// runtime too). Decoded from the embedded PNG into the RGBA Silk expects.</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            using var stream = typeof(ClientWindow).Assembly
                .GetManifestResourceStream("MSUIClient.Assets.msui-icon.png");
            if (stream is null) return;
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null) return;
            var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height,
                SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) != SkiaSharp.SKCodecResult.Success) return;
            var icon = new Silk.NET.Core.RawImage(info.Width, info.Height, bitmap.Bytes);
            _window.SetWindowIcon(new[] { icon });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] window icon failed (cosmetic): {ex.Message}");
        }
    }

    private void HandleLoad()
    {
        var startup = Stopwatch.StartNew();
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        ApplyWindowIcon();

        // WoW's own UI face, if Program.Main managed to pull it out of the
        // archives. ImGui builds its glyph atlas during construction, so this is
        // the only moment it can be supplied - and a bitmap font that is
        // obviously not the game's is the loudest thing wrong with an in-game
        // menu, louder than the frame art. See Engine/UI/UiFont.cs.
        // SUPERSAMPLED atlas (see FontSupersample). ImGui rasterises the face once and scales that
        // bitmap to whatever size text is drawn at; the glue screen draws it 2-3x larger than the
        // in-game UI, so a 12 px atlas up-scaled and blurred. Rasterise UiFontSize * FontSupersample
        // and hand FontGlobalScale back to 1/FontSupersample below, so it is crisp at every size.
        ImGuiFontConfig? font = null;
        if (!string.IsNullOrEmpty(UiFontPath) && File.Exists(UiFontPath))
        {
            int rasterPx = Math.Clamp((int)Math.Round(UiFontSize * (double)FontSupersample), 16, 72);
            font = new ImGuiFontConfig(UiFontPath, rasterPx, _ => UiGlyphRangesPtr);
        }

        // The exact-size gameplay text fonts (spellbook, tooltips) join the same one-shot atlas
        // build. onConfigureIO is the only seam between "UI font added" and "atlas baked".
        // See Engine/UI/GameTextLaw.cs for why the supersampled atlas cannot serve them.
        // Retarget from the REAL framebuffer first: the config size the em targets were seeded
        // from is wrong the moment the window opens maximised or DPI-scaled.
        if (GameplayTextScaleRule is not null)
            UI.GameTextLaw.Retarget(GameplayTextScaleRule(
                _window.FramebufferSize.X, _window.FramebufferSize.Y));
        _imgui = new ImGuiController(_gl, _window, _input, font,
            () => UI.GameTextLaw.BakeInto(ImGui.GetIO()));

        // The atlas exists now; apply the client's floor(advance)+1 glyph-step law to the
        // gameplay fonts (a no-op if none were baked).
        UI.GameTextLaw.ApplyAdvanceLaw();

        // Reapply after context creation. Some Windows/driver combinations do
        // not honor the creation-time hint, which leaves automatic swaps free
        // to tear even though window.vSync is true in the config.
        _window.VSync = _config.Window.VSync;
        Console.WriteLine($"[display] VSync requested {(_config.Window.VSync ? "on" : "off")}, " +
                          $"window reports {(_window.VSync ? "on" : "off")}");

        // Widget metrics always scale. The FONT only scales when we are stuck
        // with ImGui's bitmap face: a real TTF was already rasterised at the
        // right pixel height above, and scaling it again on top would blur it
        // and break every size that was chosen to match a 21-pixel button.
        var scale = Math.Clamp(_config.Window.UiScale, 0.5f, 4f);
        bool realFont = font is not null;
        _realUiFont = realFont;

        // Undo the supersample for ImGui's own widgets (the in-game UI) so they keep their intended
        // size; the glue draw-list text sets its size per call and keeps the full atlas resolution.
        // ApplyUiFontScale is the same law the live interface-scale slider goes through.
        ApplyUiFontScale(scale, _config.Window.FontScale);

        // ScaleAllSizes is cumulative and cannot be undone, so it stays a
        // startup-only step; the live slider moves WowSkin.Scale instead.
        if (Math.Abs(scale - 1f) > 0.01f) ImGui.GetStyle().ScaleAllSizes(scale);

        Console.WriteLine($"[ui] scale {scale:F2}, font " +
                          (realFont ? $"{Path.GetFileName(UiFontPath)} at {UiFontSize}px"
                                    : "ImGui default (scaled)") +
                          $", text x{Math.Clamp(_config.Window.FontScale, 0.5f, FontSupersample):F2}" +
                          $" (FontGlobalScale {ImGui.GetIO().FontGlobalScale:F3})");

        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Version)}");

        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += (keyboard, key, _) =>
            {
                // Repeat events land here too, but a held key is already visible to the scan
                // through _held, so only the true down edge needs recording.
                if (_held.Add(key) && !BindingChordLaw.IsModifier(key))
                    _pressedSinceScan[key] = new KeyPress(
                        keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight),
                        keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight),
                        keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight),
                        keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
            };
            kb.KeyUp += (_, key, _) => _held.Remove(key);
            // Alt+Enter: the universal fullscreen toggle, handled here so it
            // works on every screen including the glue front door.
            kb.KeyDown += (keyboard, key, _) =>
            {
                if (key == Key.Enter &&
                    (keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight)))
                    Fullscreen = !Fullscreen;
            };
        }

        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        Console.WriteLine($"[input] {_input.Keyboards.Count} keyboard(s), {_input.Mice.Count} mouse/mice");

        foreach (var mouse in _input.Mice)
        {
            // Down and up are kept only so a press registers on the same frame it
            // happens. The polling pass in HandleUpdate is what actually decides
            // whether look is engaged, so a missed event cannot strand it.
            mouse.MouseDown += (m, btn) =>
            {
                if (ImGui.GetIO().WantCaptureMouse) return;
                if (btn is MouseButton.Right or MouseButton.Left)
                {
                    bool leftReserved = btn == MouseButton.Left &&
                        (FreeSelectMode || LeftButtonReservedForWorldClicks);
                    BeginWorldPress(btn, m.Position, leftReserved);
                    if (!leftReserved)
                        BeginLook(m);
                }
            };

            mouse.MouseUp += (m, btn) =>
            {
                if (btn is MouseButton.Right or MouseButton.Left)
                {
                    EndWorldPress(btn, m.Position);
                    if (!CameraLookRequested(
                            m.IsButtonPressed(MouseButton.Left),
                            m.IsButtonPressed(MouseButton.Right),
                            FreeSelectMode, LeftButtonReservedForWorldClicks))
                        EndLook(m);
                }
            };

            mouse.MouseMove += (_, pos) =>
            {
                MouseMoveEvents++;

                if (!_mouseCaptured) { _lastMouse = pos; return; }

                var delta = pos - _lastMouse;
                _lastMouse = pos;

                // The frame capture begins, the cursor changes mode and the
                // reported position moves with it. That first delta describes
                // the mode change, not the hand.
                if (_skipNextDelta) { _skipNextDelta = false; return; }

                if (MathF.Abs(delta.X) > MaxDeltaPixels || MathF.Abs(delta.Y) > MaxDeltaPixels) return;
                if (delta.X == 0f && delta.Y == 0f) return;

                float travel = delta.Length();
                if (_leftWorldPress.Active) _leftWorldPress.Travel += travel;
                if (_rightWorldPress.Active) _rightWorldPress.Travel += travel;

                LastMouseDelta = delta;
                MouseLookEvents++;

                // Right-click-held look also turns the character and gets its own
                // sensitivity; left-click-held orbit-only look keeps the general one.
                // Pitch shares whichever is active - looking up and down is a camera
                // thing either way, but it's still part of the same drag mode's feel.
                float sensitivity = _config.Camera.MouseSensitivity *
                    (_lookTurnsCharacter ? LookAroundSensitivity : MouseSensitivity);

                if (_lookTurnsCharacter) _pendingYaw -= delta.X * sensitivity;
                else _pendingOrbitYaw -= delta.X * sensitivity;

                // Screen Y grows DOWNWARD, and Camera.Pitch is elevation ABOVE
                // the target. So standard behaviour — push the mouse up, look up
                // — needs the delta ADDED: looking up drops the camera below the
                // target, which is a smaller pitch. Subtracting here inverts the
                // vertical axis, which is what this used to do.
                float pitchSign = _config.Camera.InvertPitch ? -1f : 1f;
                _pendingPitch += delta.Y * sensitivity * pitchSign;
            };

            mouse.Scroll += (_, wheel) =>
            {
                if (ImGui.GetIO().WantCaptureMouse) return;
                BindingWheelDelta += wheel.Y;
                // Ordinary orbit zoom is a rebindable host command. The free-view rig remains a
                // physical wheel consumer and drains this path as altitude movement.
                if (FreeSelectMode) _pendingZoom += wheel.Y;
            };
        }

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        if (_config.Render.MsaaSamples > 1)
            _gl.Enable(EnableCap.Multisample);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        // WoW is Z-up and its terrain winds counter-clockwise seen from above.
        _gl.FrontFace(FrontFaceDirection.Ccw);

        unsafe
        {
            int actualSamples = 0;
            _gl.GetInteger(GLEnum.Samples, &actualSamples);
            FramebufferSamples = actualSamples;
            float anisotropy = Texture.ConfigureAnisotropy(_gl, _config.Render.Anisotropy);
            Console.WriteLine($"[display] MSAA requested {_config.Render.MsaaSamples}x, " +
                              $"framebuffer reports {actualSamples}x; anisotropy {anisotropy:F0}x");
        }

        Camera.FieldOfViewDegrees = _config.Render.FieldOfView;
        Camera.NearPlane = _config.Render.NearPlane;
        Camera.FarPlane = _config.Render.FarPlane;
        Camera.AspectRatio = (float)_config.Window.Width / _config.Window.Height;

        Console.WriteLine($"[startup] GL + input + UI         {startup.Elapsed.TotalSeconds,6:F2}s");
        startup.Restart();
        OnLoad?.Invoke(_gl);
        Console.WriteLine($"[startup] game load callback     {startup.Elapsed.TotalSeconds,6:F2}s");

        // Fullscreen is entered HERE, not at window creation - through the
        // property so the window sizes to the monitor's desktop mode first and
        // the viewport is synced (see the creation options note).
        if (_config.Window.Fullscreen && _window.WindowState != WindowState.Fullscreen)
        {
            Fullscreen = true;
            Console.WriteLine($"[display] fullscreen at {_window.FramebufferSize.X}x{_window.FramebufferSize.Y}");
        }
        else if (_config.Window.Maximized && _window.WindowState != WindowState.Maximized)
        {
            Maximized = true;
            Console.WriteLine($"[display] maximized at {_window.FramebufferSize.X}x{_window.FramebufferSize.Y}");
        }
    }

    private void BeginWorldPress(MouseButton button, Vector2 position,
        bool acceptDragRelease)
    {
        ref WorldPress press = ref (button == MouseButton.Left
            ? ref _leftWorldPress
            : ref _rightWorldPress);
        press = new WorldPress
        {
            Active = true,
            AcceptDragRelease = acceptDragRelease,
            ShiftDown = _held.Contains(Key.ShiftLeft) || _held.Contains(Key.ShiftRight),
            CtrlDown = _held.Contains(Key.ControlLeft) || _held.Contains(Key.ControlRight),
            AltDown = _held.Contains(Key.AltLeft) || _held.Contains(Key.AltRight),
            Position = position
        };

        // A two-button chord is camera control, never two coincident clicks.
        ref WorldPress other = ref (button == MouseButton.Left
            ? ref _rightWorldPress
            : ref _leftWorldPress);
        if (other.Active)
        {
            press.Travel = WorldClickDragPixels + 1f;
            other.Travel = WorldClickDragPixels + 1f;
        }
    }

    private void EndWorldPress(MouseButton button, Vector2 releasePosition)
    {
        ref WorldPress press = ref (button == MouseButton.Left
            ? ref _leftWorldPress
            : ref _rightWorldPress);
        if (press.Active &&
            (press.AcceptDragRelease || press.Travel <= WorldClickDragPixels))
        {
            // Editors consume the whole gesture and want the release point so
            // Shift+left can be used naturally as a drag. Ordinary clicks keep
            // their stable press point and camera drags remain disqualified.
            Vector2 clickPosition = press.AcceptDragRelease
                ? releasePosition
                : press.Position;
            bool shiftDown = press.ShiftDown ||
                _held.Contains(Key.ShiftLeft) || _held.Contains(Key.ShiftRight);
            bool ctrlDown = press.CtrlDown ||
                _held.Contains(Key.ControlLeft) || _held.Contains(Key.ControlRight);
            bool altDown = press.AltDown ||
                _held.Contains(Key.AltLeft) || _held.Contains(Key.AltRight);
            _worldClicks.Enqueue(new WorldMouseClick(button, clickPosition, shiftDown,
                ctrlDown, altDown));
        }
        press = default;
    }

    // ── mouse capture ────────────────────────────────────────────────────────

    private void BeginLook(IMouse mouse)
    {
        if (_mouseCaptured) return;

        _mouseCaptured = true;
        _lastMouse = mouse.Position;
        _skipNextDelta = true;

        ApplyCursorMode(mouse);
    }

    private void EndLook(IMouse mouse)
    {
        if (!_mouseCaptured) return;

        _mouseCaptured = false;
        SetCursorMode(mouse, CursorMode.Normal);
    }

    /// <summary>
    /// Raw is the mode a game wants: the cursor is locked and motion is
    /// unbounded, so you can keep turning past the edge of the screen. It is
    /// also the mode most likely to be refused, and a refusal that is swallowed
    /// looks exactly like broken camera code. Fall back and say so.
    /// </summary>
    private void ApplyCursorMode(IMouse mouse)
    {
        if (RawCursor && SetCursorMode(mouse, CursorMode.Raw)) return;

        if (RawCursor)
        {
            RawCursor = false;
            Console.WriteLine("[input] raw cursor refused by the platform - falling back to Hidden. " +
                              "Look will stop at the edge of the screen.");
        }

        if (!SetCursorMode(mouse, CursorMode.Hidden))
            SetCursorMode(mouse, CursorMode.Normal);
    }

    private bool SetCursorMode(IMouse mouse, CursorMode mode)
    {
        try
        {
            mouse.Cursor.CursorMode = mode;
            CursorModeName = mode.ToString();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[input] cursor mode {mode} failed: {ex.Message}");
            return false;
        }
    }

    private void PollMouse()
    {
        if (_mouse is null) return;

        MouseLeftDown = _mouse.IsButtonPressed(MouseButton.Left);
        MouseRightDown = _mouse.IsButtonPressed(MouseButton.Right);
        MouseMiddleDown = _mouse.IsButtonPressed(MouseButton.Middle);
        MouseButton4Down = _mouse.IsButtonPressed(MouseButton.Button4);
        MouseButton5Down = _mouse.IsButtonPressed(MouseButton.Button5);

        // Pressing the right button turns the character to wherever the camera
        // has been swung, without the view moving. Done on the TRANSITION, so a
        // held right button does not keep re-folding a zero offset.
        if (MouseRightDown && !_previousRightDown) Camera.FoldOrbitIntoFacing();
        _previousRightDown = MouseRightDown;

        _lookTurnsCharacter = MouseRightDown;

        // Engage from polling as well as from the event. If MouseDown was
        // swallowed - ImGui claiming the mouse on the wrong frame is the usual
        // culprit - this still starts the look.
        bool lookButton = CameraLookRequested(MouseLeftDown, MouseRightDown,
            FreeSelectMode, LeftButtonReservedForWorldClicks);
        if (lookButton && !_mouseCaptured && !ImGui.GetIO().WantCaptureMouse)
            BeginLook(_mouse);

        // And release from polling, so a MouseUp that never arrived cannot leave
        // the cursor hidden and the view spinning.
        if (!lookButton && _mouseCaptured)
            EndLook(_mouse);
    }

    private void HandleUpdate(float dt)
    {
        // Everything between the last render finishing and this update starting
        // is swap + present + platform events. Measured before anything else
        // happens this frame, or it cannot be measured at all.
        if (_renderEndStamp != 0)
            PresentMilliseconds = Stopwatch.GetElapsedTime(_renderEndStamp).TotalMilliseconds;

        long inputStarted = Stopwatch.GetTimestamp();

        // Clamp so an alt-tab or a breakpoint doesn't teleport anything.
        dt = MathF.Min(dt, 0.05f);

        PollMouse();

        AppliedLookYaw = _pendingYaw;
        Camera.Rotate(_pendingYaw, LookPitchLocked ? 0f : _pendingPitch);
        Camera.RotateView(_pendingOrbitYaw);
        // In the free view the wheel belongs to the FLY RIG (altitude), not the orbit boom. Normal
        // camera zoom is dispatched later through the rebindable CAMERAZOOMIN/OUT commands.
        if (_pendingZoom != 0)
        {
            if (FreeSelectMode) _freeFlightScroll += _pendingZoom;
        }
        _pendingYaw = _pendingOrbitYaw = _pendingPitch = _pendingZoom = 0;

        InputMilliseconds = Stopwatch.GetElapsedTime(inputStarted).TotalMilliseconds;

        OnUpdate?.Invoke(dt);
        BindingWheelDelta = 0f;
    }

    private void HandleRender(float dt)
    {
        // Between frames is the only safe moment to rebuild the font atlas (never inside
        // NewFrame..Render). Retarget is a cheap set-compare; it only fires a rebuild when the
        // gameplay em targets actually changed (maximise, resize, UI-scale preference).
        if (_pendingGameplayTextScale > 0f && UI.GameTextLaw.Retarget(_pendingGameplayTextScale))
            RebuildFontAtlas();

        _fpsAccumulator += dt;
        _framesSinceSample++;
        if (_fpsAccumulator >= 0.5)
        {
            Fps = (float)(_framesSinceSample / _fpsAccumulator);
            FrameMs = _fpsAccumulator * 1000.0 / _framesSinceSample;
            _fpsAccumulator = 0;
            _framesSinceSample = 0;
        }

        long imguiStarted = Stopwatch.GetTimestamp();
        _imgui.Update(dt);
        ImguiUpdateMilliseconds = Stopwatch.GetElapsedTime(imguiStarted).TotalMilliseconds;

        // Sky colour matches the far fog so the visibility boundary disappears
        // into aerial perspective instead of ending in a hard silhouette.
        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        OnRender?.Invoke(dt);

        // Two brackets, not one. OnGui is our HUD code and is pure CPU;
        // _imgui.Render() is the frame's last GL submission and is where the
        // driver's implicit flush lands. Lumping them made every driver stall
        // read as "the HUD is slow", which it never was. See the field docs.
        long hudStarted = Stopwatch.GetTimestamp();
        OnGui?.Invoke();
        long imguiRenderStarted = Stopwatch.GetTimestamp();
        HudMilliseconds = Stopwatch.GetElapsedTime(hudStarted, imguiRenderStarted).TotalMilliseconds;

        // The additive glue overlay (char-select row highlight, alphaMode=ADD) composites on the
        // framebuffer BEFORE the ImGui draw pass, so it sits under the translucent roster panel and
        // the row text. A dedicated GL pass, never an ImGui draw callback (that crashed the loop).
        OnOverlay?.Invoke();

        _imgui.Render();
        OnOverlayTop?.Invoke();
        _renderEndStamp = Stopwatch.GetTimestamp();

        ImguiRenderMilliseconds =
            Stopwatch.GetElapsedTime(imguiRenderStarted, _renderEndStamp).TotalMilliseconds;

        // Kept as the sum so the recorder's unaccounted residual still balances
        // and nothing downstream double-counts. HudMs + ImguiRenderMs == GuiMs
        // is asserted by construction here; do not compute it any other way.
        GuiMilliseconds = HudMilliseconds + ImguiRenderMilliseconds;
    }

    private void HandleResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        _gl.Viewport(size);
        Camera.AspectRatio = (float)size.X / size.Y;
        // Gameplay text scale moves with the framebuffer; queue the em-target check even before
        // the game loop reports its own scale (it will refine the value every frame anyway).
        if (GameplayTextScaleRule is not null)
            _pendingGameplayTextScale = GameplayTextScaleRule(size.X, size.Y);
    }

    /// <summary>
    /// Rebuild the whole ImGui font atlas: the supersampled UI font exactly as the controller's
    /// constructor added it, plus the gameplay fonts at their CURRENT exact em sizes, then a
    /// fresh GL texture handed to ImGui. The controller's own texture object is simply no longer
    /// referenced by the atlas; it is disposed with the controller at shutdown.
    /// </summary>
    private unsafe void RebuildFontAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.Clear();
        bool realFont = !string.IsNullOrEmpty(UiFontPath) && File.Exists(UiFontPath);
        if (realFont)
        {
            int rasterPx = Math.Clamp((int)Math.Round(UiFontSize * (double)FontSupersample), 16, 72);
            // Same General-Punctuation range as the constructor build, so a live resize or
            // font-scale rebuild does not silently revert the dashes to '?' glyphs.
            var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            try
            {
                cfg.GlyphRanges = UiGlyphRangesPtr;
                io.Fonts.AddFontFromFileTTF(UiFontPath, rasterPx, cfg);
            }
            finally { cfg.Destroy(); }
        }
        else io.Fonts.AddFontDefault();
        UI.GameTextLaw.BakeInto(io);
        io.Fonts.Build();

        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height, out _);
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        io.Fonts.SetTexID((nint)texture);
        io.Fonts.ClearTexData();

        if (_rebakedFontTexture != 0) _gl.DeleteTexture(_rebakedFontTexture);
        _rebakedFontTexture = texture;
        UI.GameTextLaw.ApplyAdvanceLaw();
        Console.WriteLine($"[game-text] font atlas rebuilt {width}x{height}: " +
                          UI.GameTextLaw.DescribeBake());
    }

    private void HandleClosing()
    {
        OnClosing?.Invoke();
        _window.GLContext?.MakeCurrent();
        if (_rebakedFontTexture != 0) _gl.DeleteTexture(_rebakedFontTexture);
        _imgui.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }

    // ── input queries ─────────────────────────────────────────────────────────

    public bool IsDown(Key key) => _held.Contains(key);

    public float Axis(Key positive, Key negative)
        => (_held.Contains(positive) ? 1f : 0f) - (_held.Contains(negative) ? 1f : 0f);

    public void Close() => _window.Close();

    public void Dispose() => _window?.Dispose();
}
