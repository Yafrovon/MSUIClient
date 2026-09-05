using System.Text.Json;
using System.Text.Json.Serialization;
using MSUIClient.Engine.UI;

namespace MSUIClient.Engine;

/// <summary>
/// How exterior lighting interprets the authored Light.dbc chain. Both modes
/// consume the authored data (SYSTEM_EXTERIOR_LIGHTING.md); they differ in
/// interpretation:
///
///   Msui       - the tuned MSUI look: authored colours applied directly with
///                no daylight intensity curve, and a boosted interior doorway
///                spill. Exactly the pre-v6 runtime behaviour.
///   Parity112  - as close to the vanilla 1.12 client as we can get: the
///                authored colours are additionally scaled by the day/night
///                intensity curve the real client ships in World\dnc.db, and
///                the interior spill multiplier uses the shipped 1.10 balance.
///
/// Serialised as a string so settings.json stays hand-editable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LightingMode
{
    Msui,
    Parity112,
}

/// <summary>
/// Where the world clock (WorldAtmosphere.TimeOfDayHours) comes from - the v7
/// replacement for the old fixed TimeOfDay + CycleTimeOfDay pair:
///
///   Server - track the game time the server sends (SMSG_LOGIN_SETTIMESPEED)
///            and advance it locally at the server's timescale, like the real
///            client. Falls back to this machine's wall-clock time of day when
///            no server time has arrived (offline, creator mode). The default.
///   Fixed  - pin the world at LightingSettings.TimeOfDay (the pre-v7
///            behaviour, which shipped pinned to noon).
///   Cycle  - the accelerated debug day/night cycle, advancing at
///            LightingSettings.GameHoursPerMinute.
///
/// Serialised as a string so settings.json stays hand-editable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSource
{
    Server,
    Fixed,
    Cycle,
}

/// <summary>
/// The player's preferences: everything the settings modal owns, and nothing
/// else. Loaded from settings.json at the repo root, written back when the modal
/// is accepted, and applied to the live renderers by GameLoop (Program.Settings.cs).
///
/// WHY THIS IS NOT PART OF ClientConfig
///   client-config.json is per-machine WIRING - MPQ paths, vmap paths, the realmd
///   host, the start position, the DevTools flag - and is gitignored for exactly
///   that reason. This is TASTE. Keeping them apart means the settings page never
///   rewrites the file that holds the paths, and a machine move carries one of the
///   two rather than both tangled together.
///
/// WHY THIS IS NOT A Vantage
///   A vantage is a place and an instant: it exists to reproduce one frame, and
///   loading one is SUPPOSED to stomp your fog values. Settings outlive every
///   place. Merging the two types would make "reproduce that frame" silently
///   overwrite a preference, so ApplyVantage deliberately does not write here.
///   See PLAN_11 section 10.
///
/// WHY IT IS PLAIN DATA
///   No renderer references, no GL, no ImGui. GameLoop owns the translation in
///   both directions (ApplySettings / CaptureSettings) because it is the only
///   thing that knows which renderers exist yet. Keeping this file ignorant is
///   what lets Program.Main read it BEFORE the window exists, which is required
///   for the restart-scoped controls (resolution, sample count, anisotropy).
/// </summary>
public sealed class GameSettings
{
    /// <summary>Bumped when a rename or a units change needs migration handling.
    /// v2: portal culling (PLAN_10) became the shipped default.
    /// v3: ForceTwoSided went back to being a diagnostic, off by default.
    /// v4: painterly detail became an absolute gain and gained explicit calm/dither controls.
    /// v5: painterly band strength separated subtle flattening from the band count.
    /// v6: Lighting.UseAuthoredData became Lighting.Mode (MSUI Lighting / 1.12 Parity)
    ///     and the WMO doorway-spill multiplier became persisted (InteriorSpill).
    /// v7: Lighting.CycleTimeOfDay became Lighting.TimeSource, defaulting to tracking
    ///     the server's game clock instead of a world pinned at noon.
    /// v8: Escape/Options menu scale became independent from gameplay Interface scale.
    /// v9: Escape/Options text scale moved beside its independent chrome scale.
    /// v10: vanilla Camera Following Style became a persisted Smart/Always/Never control.
    /// v11: cursor scale and movable gameplay-frame positions became persistent.
    /// v12: HUD layouts (PLAN_21) replaced the chat-only offset; a Version 11 chat offset
    ///      migrates into a Custom layout.
    /// v13: non-custom water resolves to the real build-5875/1.12 shipped profile.</summary>
    public int Version { get; set; } = 13;

    /// <summary>Name of the preset last selected, or "Custom". Cosmetic; the values below are the truth.</summary>
    public string ActivePreset { get; set; } = "Custom";

    /// <summary>The last character highlighted on the character-select screen.</summary>
    public ulong LastCharacterGuid { get; set; }

    /// <summary>
    /// Account name saved by the login screen's Remember Account Name checkbox. Passwords are
    /// deliberately never stored here.
    /// </summary>
    public string SavedAccountName { get; set; } = "";

    /// <summary>
    /// What the client launches into: "Client" (the networked SuperUI client) or
    /// "Creator" (the offline spell-creator sandbox). Empty means never chosen -
    /// treated as "Client". Set from the login screen's Launch Options menu and
    /// sticky across sessions. Batch instruments (portrait/variant/movement/live-run)
    /// ignore it entirely.
    /// </summary>
    public string LaunchMode { get; set; } = "";

    public DisplaySettings Display { get; set; } = new();
    public CreatorSettings Creator { get; set; } = new();
    public ViewSettings View { get; set; } = new();
    public DetailSettings Detail { get; set; } = new();
    public ClutterSettings Clutter { get; set; } = new();
    public WaterSettings Water { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public ControlSettings Controls { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public DevWindowSettings DevWindow { get; set; } = new();
    public EncounterLabSettings EncounterLab { get; set; } = new();
    public AddOnSettings AddOns { get; set; } = new();
    public MenuLayoutSettings MenuLayout { get; set; } = new();
    public HudLayoutSettings HudLayout { get; set; } = new();
    public MountSettings Mounts { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();

    // ── groups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creator-mode preferences, dialable live from the creator bar's UI panel.
    /// UiScale sizes the panels/buttons/widgets; TextScale sizes the text
    /// independently - one knob scaling both reads as zoom, not as a setting.
    /// The look (race/sex/dials/equipment) is sticky: whatever was worn when a
    /// creator session ended comes back in the next one.
    /// </summary>
    public sealed class CreatorSettings
    {
        public float UiScale { get; set; } = 1f;     // live - MODAL widget/panel sizes
        public float TextScale { get; set; } = 1f;   // live - MODAL font size only

        /// <summary>
        /// Exposes the spell workshop's format-level controls (individual M2
        /// emitters today; bones, tracks, ribbons and other internals as they are
        /// added). The default workshop stays concerned with the recognizable
        /// spell phases, their look and their audio.
        /// </summary>
        public bool SpellAdvancedMode { get; set; }

        // The top menu bar (Character/Gear/Teleport/Target/Spells/UI) sizes
        // independently of the modals - its own button and caption dials.
        public float BarScale { get; set; } = 1f;
        public float BarTextScale { get; set; } = 1f;

        /// <summary>The docked workspace layout (2026-08-20): full-height side
        /// rails of square buttons plus a bottom control deck, instead of
        /// floating windows. Off = the classic floating-window layout.</summary>
        public bool Workspace { get; set; } = true;

        /// <summary>Bottom deck height as a fraction of the display (drag the
        /// deck's top edge to change it).</summary>
        public float DeckFraction { get; set; } = 0.30f;

        /// <summary>The Spell Workshop's focus layout: opening it takes BOTH
        /// sidebars - the spell and its phases on the left, the selected phase's
        /// dials on the right - and stands the bottom deck down, so the centre
        /// stays clear full-height for watching the spell play. Off = the Spell
        /// Workshop uses the ordinary rails and deck like every other panel.</summary>
        public bool SpellFocus { get; set; } = true;

        /// <summary>Focus-layout sidebar width as a fraction of the display. The
        /// SAME value drives both sides so the viewing stage stays centred on the
        /// model; dragging either inner edge changes it.</summary>
        public float SpellFocusFraction { get; set; } = 0.26f;

        /// <summary>Chrome fill opacity for the creator panels (0.3 - 1).</summary>
        public float PanelAlpha { get; set; } = 0.62f;

        /// <summary>Multiplier over the creator panels' window padding.</summary>
        public float PaddingScale { get; set; } = 1f;

        /// <summary>Multiplier over the creator panels' item spacing.</summary>
        public float SpacingScale { get; set; } = 1f;

        public byte Race { get; set; } = 1;          // ChrRaces id, Human
        public byte Sex { get; set; }                // 0 male, 1 female
        public int[] Dials { get; set; } = new int[5];   // skin, face, hairStyle, hairColor, facialHair
        public List<CreatorPieceSetting> Equipment { get; set; } = new();

        // The last creator-session location. LocMap -1 = never saved; the world
        // then loads at the client-config start position as before.
        public int LocMap { get; set; } = -1;
        public string LocMapName { get; set; } = "";
        public float LocX { get; set; }
        public float LocY { get; set; }
        public float LocZ { get; set; }
        public float LocYaw { get; set; }

        /// <summary>Per-panel ordering of the drill-down sections, user-arranged by
        /// dragging headers. Keyed by panel id; unknown ids are ignored on load.</summary>
        public Dictionary<string, List<string>> SectionOrder { get; set; } = new();

        /// <summary>Sections torn off into their own floating windows, as "panel/section".</summary>
        public List<string> PoppedSections { get; set; } = new();

        /// <summary>Expanded/collapsed state of every drill-down, by stable id - the
        /// arrangement you leave a panel in is the arrangement it reopens with.</summary>
        public Dictionary<string, bool> SectionOpen { get; set; } = new();

        /// <summary>Per-modal layout dials (the gear button on each window). These
        /// multiply ON TOP of the shared modal dials above, so one window can have
        /// its own "perfect" layout without moving the others.</summary>
        public Dictionary<string, PanelTuneSetting> PanelTuning { get; set; } = new();

        /// <summary>Hand-placed widget positions from the gear popup's "Move
        /// buttons" edit mode: [panel][widget key] = offset from the widget's
        /// natural flow position, in unscaled units (scale-independent).</summary>
        public Dictionary<string, Dictionary<string, float[]>> WidgetOffsets { get; set; } = new();
    }

    public sealed class PanelTuneSetting
    {
        public float Text { get; set; } = 1f;      // font size in this window
        public float Widget { get; set; } = 1f;    // slider/input/thumbnail sizing
        public float Button { get; set; } = 1f;    // red button size
        public float Icon { get; set; } = 1f;      // section headers + the +/- art
        public float Spacing { get; set; } = 1f;   // row spacing

        public bool IsNeutral =>
            Text == 1f && Widget == 1f && Button == 1f && Icon == 1f && Spacing == 1f;
    }

    /// <summary>
    /// Player-sized option windows, in logical UI units so changing Interface scale
    /// keeps the same useful amount of room. Zero means the authored default and is
    /// also what older settings files deserialize to.
    /// </summary>
    public sealed class MenuLayoutSettings
    {
        /// <summary>
        /// Escape/Options chrome scale. It is deliberately independent of Display.UiScale so the
        /// menu containing that global slider never resizes itself through a feedback loop.
        /// </summary>
        public float Scale { get; set; } = 1.8f;

        /// <summary>
        /// Escape/Options text size. This lives beside the menu's own chrome scale because the
        /// ImGui font is used by these menus, not by the exact-pixel gameplay frames.
        /// Default 1.75: at a normal desktop distance the 1.0 menu font reads far too small
        /// next to the 1.8 chrome, so fresh installs open with legible menu text. (Existing
        /// settings.json files keep their stored value — no migration bumps this.)
        /// </summary>
        public float TextScale { get; set; } = 1.75f;

        public float MainWidth { get; set; }
        public float MainHeight { get; set; }
        public float VideoWidth { get; set; }
        public float VideoHeight { get; set; }
        public float ControlsWidth { get; set; }
        public float ControlsHeight { get; set; }
        public float SoundWidth { get; set; }
        public float SoundHeight { get; set; }
        public float AddOnsWidth { get; set; }
        public float AddOnsHeight { get; set; }
    }

    /// <summary>
    /// Optional native client modules. These are deliberately not a Lua addon host: each switch
    /// gates an isolated C# feature, while the Escape-menu AddOns page gives players the familiar
    /// place to control it.
    /// </summary>
    public sealed class AddOnSettings
    {
        /// <summary>Active-objective and ready-to-turn-in pins on the world map and minimap.</summary>
        public bool QuestHelper { get; set; } = true;

        /// <summary>Movable player health/power bars with combo pips and the energy tick
        /// sweep. Its own class because this one is configurable rather than a plain switch.</summary>
        public PlayerPowerBarsSettings PowerBars { get; set; } = new();

        /// <summary>Cast on the unit under the cursor instead of the current target, without
        /// changing target. Off by default: it changes what an action key already does, and a
        /// switch everyone receives should not silently rebind a press nobody asked to move.</summary>
        public bool Hovercast { get; set; }

        /// <summary>Extend Hovercast to 3D bodies in the world, not only unit frames.
        /// Off by default, matching the addon — the crosshair passes over enemies constantly
        /// while the camera turns, so frames-only is the predictable half.</summary>
        public bool HovercastWorldUnits { get; set; }

        /// <summary>The melee/ranged auto-attack swing rail.</summary>
        public SwingTimerSettings SwingTimer { get; set; } = new();
    }

    /// <summary>
    /// Player Power Bars. Every number here is a player preference, not a tuning constant:
    /// the point of the feature is that the bars go where you want them, at the size you
    /// want, showing what you want. Offsets are logical UI units so changing Interface
    /// scale does not move the frame.
    /// </summary>
    public sealed class PlayerPowerBarsSettings
    {
        /// <summary>Off by default. The player frame already shows health and power; this
        /// is a second, movable display, and a shipped feature should not add furniture to
        /// someone's screen uninvited.</summary>
        public bool Enabled { get; set; }

        /// <summary>Drag the bars to move them. Locked hides the handle and the outline.</summary>
        public bool Unlocked { get; set; }

        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        public float Width { get; set; } = 200f;
        public float HealthHeight { get; set; } = 20f;
        public float PowerHeight { get; set; } = 14f;
        public float Spacing { get; set; } = 2f;
        public float Scale { get; set; } = 1f;

        /// <summary>Write the value on each bar.</summary>
        public bool ShowText { get; set; } = true;

        /// <summary>Write it as a percentage instead of value / max.</summary>
        public bool ShowPercent { get; set; }

        /// <summary>Combo pips above the bars. The client already draws the authored vanilla
        /// combo frame by the target frame; this is the copy that travels with these bars,
        /// for players who move them near the crosshair. Opt-in so it is never a duplicate
        /// nobody asked for.</summary>
        public bool ShowCombo { get; set; }

        /// <summary>The energy regen-tick sweep across the power bar.</summary>
        public bool ShowTickBar { get; set; } = true;

        /// <summary>Seconds per server regen tick. 2.0 is this server's hardcoded value
        /// (Player::RegenerateAll); exposed because a fork can change it.</summary>
        public float TickSeconds { get; set; } = 2f;
    }

    /// <summary>
    /// Swing Timer. One rail with a cursor per weapon, sweeping from "just swung" to "ready".
    /// Offsets are logical UI units so changing Interface scale does not move the rail.
    /// </summary>
    public sealed class SwingTimerSettings
    {
        /// <summary>Off by default: it adds furniture to the screen, and a shipped feature
        /// should not do that uninvited.</summary>
        public bool Enabled { get; set; }

        /// <summary>Drag the rail to move it. Unlocked also keeps it on screen while idle.</summary>
        public bool Unlocked { get; set; }

        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        public float Width { get; set; } = 240f;
        public float Height { get; set; } = 18f;
        public float Scale { get; set; } = 1f;

        /// <summary>Track main-hand and off-hand melee swings.</summary>
        public bool TrackMelee { get; set; } = true;

        /// <summary>Track Auto Shot and wand Shoot.</summary>
        public bool TrackRanged { get; set; } = true;

        /// <summary>Hide the rail when nothing is swinging.</summary>
        public bool HideWhenIdle { get; set; } = true;

        /// <summary>Show seconds remaining on the rail.</summary>
        public bool ShowText { get; set; } = true;

        /// <summary>The red plant/aim band over the last half second of a ranged reload.
        /// Hunters only - wands carry no aim penalty.</summary>
        public bool ShowAimBand { get; set; } = true;

        /// <summary>Start a swing already part-way along, by half the measured round trip,
        /// because that is how long the packet reporting it spent in flight. The addon could
        /// only do this for ranged and only from a 30-second GetNetStats sample.</summary>
        public bool CompensateLatency { get; set; } = true;

        /// <summary>Extra manual nudge on ranged shots for projectile travel, in seconds.</summary>
        public float RangedTravelSeconds { get; set; } = .15f;
    }

    // Player-placed HUD frames: HudLayoutSettings lives in Engine/UI/HudLayoutLaw.cs (PLAN_21).

    /// <summary>
    /// The mount workbench, persisted. Two halves that answer different questions:
    ///
    ///   LOOK — where the rider sits and how big everything is. Necessarily PER STEED
    ///   (`Tunes`), because a saddle offset that is right for a horse is meaningless on a
    ///   rocket car, and because some models carry a baked origin offset that only a
    ///   per-display correction can cancel. See SYSTEM_MOUNTS.md §7.
    ///
    ///   FEEL — how the mount handles. Global, because "nimble" is a preference about
    ///   riding, not about one horse: speed, turn rate and jump, each a multiplier on the
    ///   values the controller already uses, so 1.0 is exactly today's behaviour.
    ///
    /// The feel multipliers are CLIENT PREDICTION ONLY. On a live server the authoritative
    /// speed is still whatever it sent; riding faster than the server believes will fight
    /// its corrections. Offline (creator sandbox) nothing argues back.
    /// </summary>
    public sealed class MountSettings
    {
        /// <summary>Dev ride override: the steed the toolkit puts you on with no server involved.</summary>
        public int RideDisplayId { get; set; } = 2404;   // Riding Horse
        public bool Riding { get; set; }

        public float SpeedMultiplier { get; set; } = 1f;
        public float TurnMultiplier { get; set; } = 1f;
        public float JumpMultiplier { get; set; } = 1f;

        /// <summary>Gait playback rate on top of the stride matching. 1 = authored.</summary>
        public float AnimationRate { get; set; } = 1f;

        /// <summary>Where spent cart-kit charges come back from.</summary>
        public MountKitRecharge Recharge { get; set; } = MountKitRecharge.Time;

        /// <summary>
        /// While mounted, 1..6 fire the cart's kit instead of the action bar — but only for
        /// slots that actually hold a spell, so an unconfigured cart changes nothing.
        /// </summary>
        public bool KitOnNumberKeys { get; set; } = true;

        public List<MountTuneSetting> Tunes { get; set; } = new();
    }

    /// <summary>
    /// One steed's look, keyed by CreatureDisplayInfo id. Offsets are in the mount's own
    /// model space, in yards: +Forward is the way it faces, +Right its right flank, +Up the
    /// sky. That is the space attachment 0 is authored in, so a nudge here reads the same
    /// way the artist's saddle position does.
    /// </summary>
    public sealed class MountTuneSetting
    {
        public int DisplayId { get; set; }

        // where the rider sits, relative to the authored saddle
        public float SeatForward { get; set; }
        public float SeatRight { get; set; }
        public float SeatUp { get; set; }
        public float RiderYaw { get; set; }     // degrees about the rider's up axis
        public float RiderPitch { get; set; }   // degrees about its right axis (lean fore/aft)
        public float RiderRoll { get; set; }    // degrees about its forward axis (lean sideways)
        public float RiderScale { get; set; } = 1f;

        // where the steed itself sits, relative to the unit's ground position
        public float MountForward { get; set; }
        public float MountRight { get; set; }
        public float MountUp { get; set; }
        public float MountScale { get; set; } = 1f;

        /// <summary>What this cart can do. Empty means it is only something to sit on.</summary>
        public List<MountKitSlotSetting> Kit { get; set; } = new();

        public bool IsNeutral =>
            SeatForward == 0f && SeatRight == 0f && SeatUp == 0f &&
            RiderYaw == 0f && RiderPitch == 0f && RiderRoll == 0f && RiderScale == 1f &&
            MountForward == 0f && MountRight == 0f && MountUp == 0f && MountScale == 1f;
    }

    /// <summary>
    /// One thing a cart can fire. The SPELL is only the presentation — its authored 1.12
    /// visual, played through the ordinary spell-effect path — and the EFFECT below is what
    /// it does. They are deliberately separate: which spell dresses which effect is exactly
    /// the tuning pass this is built to make cheap.
    ///
    /// Charges are the resource. <see cref="MountSettings.Recharge"/> decides where they come
    /// back from: a timer today, a token picked up on the track once that exists — the seam is
    /// <c>NoteMountKitToken</c>, which is already called by the toolkit's test button.
    /// </summary>
    public sealed class MountKitSlotSetting
    {
        public uint SpellId { get; set; }
        public string Label { get; set; } = "";

        public int MaxCharges { get; set; } = 3;
        public float RechargeSeconds { get; set; } = 8f;
        public float CooldownSeconds { get; set; } = 1.5f;

        /// <summary>What firing it does. Nothing here deals damage — that is the design.</summary>
        public MountKitEffectKind Effect { get; set; } = MountKitEffectKind.Slow;

        /// <summary>Yards: the slow's reach, or the dash's distance.</summary>
        public float Radius { get; set; } = 12f;

        /// <summary>Multiplier applied to what it catches. 0.5 = half speed.</summary>
        public float SlowFactor { get; set; } = 0.5f;
        public float SlowSeconds { get; set; } = 4f;
    }

    public enum MountKitEffectKind
    {
        /// <summary>Visual only — the spell plays and nothing else happens.</summary>
        None,

        /// <summary>Everything in radius is slowed for a while. The default, and the point.</summary>
        Slow,

        /// <summary>The cart jumps <c>Radius</c> yards along its facing. Blink, as a cart move.</summary>
        Dash,
    }

    /// <summary>Where a spent charge comes back from.</summary>
    public enum MountKitRecharge
    {
        /// <summary>A timer, so the kit is usable before the pickup exists.</summary>
        Time,

        /// <summary>Only <c>NoteMountKitToken</c> gives charges back — the track-pickup design.</summary>
        Token,
    }

    /// <summary>One worn creator piece, as persisted (display id is the visual truth).</summary>
    public sealed class CreatorPieceSetting
    {
        public string Name { get; set; } = "";
        public uint DisplayId { get; set; }
        public int InventoryType { get; set; }
    }

    /// <summary>
    /// Window, buffers and the UI itself. Three of these cannot change without a
    /// restart: Silk requests the sample count at window creation, the resolution
    /// is the window, and anisotropy is selected once per texture at upload.
    /// They are still written immediately so the next boot picks them up.
    /// </summary>
    public sealed class DisplaySettings
    {
        public int WindowWidth { get; set; } = 1600;              // restart
        public int WindowHeight { get; set; } = 900;              // restart
        public bool Fullscreen { get; set; }                      // live (Alt+Enter toggles too)
        public bool Maximized { get; set; }                       // live
        public bool VSync { get; set; } = true;                   // live
        public int MsaaSamples { get; set; } = 4;                 // restart
        public bool MultisamplingEnabled { get; set; } = true;    // live (the GL enable, not the count)
        public float Anisotropy { get; set; } = 8f;               // restart
        public float UiScale { get; set; } = 1.8f;                // live
        public float CursorScale { get; set; } = 1f;              // live, on top of UiScale
        // Legacy v8 migration source. MenuLayout.TextScale now owns Escape/Options text.
        public float FontScale { get; set; } = 1f;
        public bool TexturedFrame { get; set; } = true;           // live - WowSkin.Textured

        // Painterly mode (Engine/PainterlyPass.cs) - all live. The shipped
        // crisp-flat profile keeps the source art legible and adds only light
        // value/edge structure. config render.painterly true is a hard-on
        // override for scripted runs.
        public bool Painterly { get; set; }                            // live
        public bool PainterlyUi { get; set; }                          // live, independently styles HUD art
        public float PainterlyBands { get; set; } = 18f;               // live, 3..24 painted value steps
        public float PainterlyBandStrength { get; set; } = 0.30f;      // live, 0..1 blend toward quantized values
        public float PainterlyDetail { get; set; } = 1f;               // live, 0..2 absolute residual gain; 1=source
        public float PainterlyInk { get; set; } = 0.10f;               // live, 0..1 boundary darkening
        public float PainterlyInkThreshold { get; set; } = 0.19f;      // live, 0.01..0.5 edge noise gate
        public float PainterlySilhouette { get; set; } = 0.22f;        // live, 0..1 depth-edge ink
        public float PainterlyDepthFade { get; set; } = 0.35f;         // live, 0..1 aerial perspective strength
        public float PainterlyCalmStart { get; set; } = 60f;           // live, world distance
        public float PainterlyCalmEnd { get; set; } = 240f;            // live, world distance
        public float PainterlySaturation { get; set; } = 1.07f;        // live, 0..2 colour richness; 1=source
        public float PainterlyContrast { get; set; } = 0.18f;          // live, 0..1 value S-curve before banding
        public float PainterlyLift { get; set; } = 1.01f;              // live, 0.5..2 midtone gamma lift; 1=source
        public float PainterlyWarmth { get; set; } = 0.08f;            // live, 0..1 sun/shade split tone
        public float PainterlyGrain { get; set; } = 0f;                // live, 0..1 canvas grain
        public float PainterlyDither { get; set; } = 0.04f;            // live, 0..1 stable band dither
        public int PainterlyCanvasHeight { get; set; } = 1440;         // live, 0=native; HUD remains native
    }

    /// <summary>
    /// How far you can see. DistancePercent is the composite: while
    /// DistanceCustom is false it GENERATES the five values under it through
    /// <see cref="ResolveViewDistance"/>, so two machines at the same percentage
    /// see the same thing. Touching any of the five sets DistanceCustom and the
    /// generator stops.
    /// </summary>
    public sealed class ViewSettings
    {
        public float DistancePercent { get; set; } = 60f;
        public bool DistanceCustom { get; set; }

        public float FieldOfView { get; set; } = 70f;

        public bool FogEnabled { get; set; } = true;
        public float FogStart { get; set; } = 350f;
        public float FogEnd { get; set; } = 777f;
        public bool CullAtFogEnd { get; set; } = true;
        public bool CoupleFarPlaneToFog { get; set; } = true;

        public float BuildingDistance { get; set; } = 777f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 2000f;
    }

    /// <summary>
    /// The master mix (Sound Options page). Defaults are the 1.12 registrar
    /// defaults - a fresh vanilla install runs music at 0.4 and ambience at
    /// 0.6, NOT uniform full volume. 1.12 has no SFX-only enable; EnableAll is
    /// the master switch, exactly like the reference CVar MasterSoundEffects.
    /// </summary>
    public sealed class AudioSettings
    {
        public bool EnableAll { get; set; } = true;
        public bool EnableMusic { get; set; } = true;
        public bool EnableAmbience { get; set; } = true;
        public float MasterVolume { get; set; } = 1f;
        public float EffectsVolume { get; set; } = 1f;
        public float MusicVolume { get; set; } = 0.4f;
        public float AmbienceVolume { get; set; } = 0.6f;
    }

    /// <summary>Doodads and buildings. Two composites, both with the same custom rule as ViewSettings.</summary>
    public sealed class DetailSettings
    {
        public float ObjectDetailPercent { get; set; } = 55f;
        public bool ObjectDetailCustom { get; set; }

        public float BuildingDetailPercent { get; set; } = 70f;
        public bool BuildingDetailCustom { get; set; }

        // Doodads (M2 props - trees, rocks, fences, furniture).
        public bool Doodads { get; set; } = true;
        public float DoodadDistance { get; set; } = 300f;
        public bool DoodadInstancing { get; set; } = true;
        public bool DoodadFrustumCulling { get; set; } = true;
        public bool DoodadFlatCullBounds { get; set; } = true;
        public float DoodadAlphaCutoff { get; set; } = 0.5f;
        public bool DoodadDemandStreaming { get; set; } = true;

        // Buildings (WMO).
        public bool Buildings { get; set; } = true;
        public bool WmoFrustumCulling { get; set; } = true;
        public bool DistanceLodShells { get; set; } = true;
        /// <summary>
        /// Draw every WMO batch two-sided. A DIAGNOSTIC, defaulted off.
        ///
        /// It shipped on, and it was the most expensive setting in the client:
        /// backface culling disabled for the pass that is 72-86% of GPU time in
        /// a city, so every wall paid double setup and double fill. No quality
        /// preset overrode it either, which is much of why Low never helped.
        /// Turn it on to tell "the geometry is missing" from "the winding is
        /// inward" in one click — that is what it is for.
        /// </summary>
        public bool ForceTwoSided { get; set; }
        public float WmoAlphaCutoff { get; set; } = 0.35f;
        public int ImpostorMaxVertices { get; set; } = 2000;
        public float InsideMargin { get; set; }
        public float InteriorCullDistance { get; set; } = 120f;
        public float ShellNearGuard { get; set; } = 196f;
        public bool OcclusionCulling { get; set; }
        public float OcclusionMinDistance { get; set; } = 40f;

        // PLAN_10 portal-traversal interior visibility (hides Stormwind's roof from
        // inside, holds the cathedral silhouette across the approach). ON by default
        // now that it is verified in-game - this is the expected 1.12 behaviour. The
        // WMO panel toggle stays for A/B; set false here to boot with it off.
        public bool WmoPortalCulling { get; set; } = true;

        // Per-object appear fade (benilla model_fade.rs): streamed-in doodads and
        // buildings ease in over AppearFadeSeconds instead of popping. On by
        // default; set false to restore the original hard pop-in.
        public bool AppearFade { get; set; } = true;
        public float AppearFadeSeconds { get; set; } = 2f;
    }

    /// <summary>
    /// Ground effects - the grass, ferns, flowers and road pebbles. Defaults
    /// mirror FoliageRenderer's own field initialisers; see SYSTEM_FOLIAGE.md
    /// section 4 for what each one means. The three 1.12 switches at the bottom
    /// are authenticity, not performance: turning them off is how the road grows
    /// grass again.
    /// </summary>
    public sealed class ClutterSettings
    {
        public bool Enabled { get; set; } = true;
        public float Density { get; set; } = 0.5f;
        public float Radius { get; set; } = 45f;

        public int MaxPerCell { get; set; } = 6;
        public float Scale { get; set; } = 1.0f;
        public float ScaleJitter { get; set; } = 0.25f;
        public int MaxInstances { get; set; } = 24000;
        public float RescatterDistance { get; set; } = 8f;

        public float WindStrength { get; set; } = 0.06f;
        public float WindSpeed { get; set; } = 1.4f;

        public bool LinkFadeToRadius { get; set; } = true;
        public float FadeStartFraction { get; set; } = 0.66f;
        public float FadeStart { get; set; } = 30f;
        public float FadeEnd { get; set; } = 45f;

        public float AlphaCutoff { get; set; } = 0.4f;
        public float Brightness { get; set; } = 1.0f;

        public bool UseCellLayerMap { get; set; } = true;
        public bool UseNoDoodadMask { get; set; } = true;
        public bool SkipHoles { get; set; } = true;

        /// <summary>Suppress land clutter in cells under water deeper than
        /// <see cref="LiquidFoliageMaxDepth"/>. Grass does not grow in the river.</summary>
        public bool SkipDeepLiquidCells { get; set; } = true;

        /// <summary>Water depth, in yards, above which a cell stops scattering.
        /// Kept small on purpose so reeds at the shallow margin survive.</summary>
        public float LiquidFoliageMaxDepth { get; set; } = 0.75f;

        /// <summary>
        /// Per-kind curation, keyed by FoliageKind name so a renamed or added
        /// enum member cannot corrupt an old file - an unknown key is ignored and
        /// a missing key keeps the renderer's default.
        /// </summary>
        public Dictionary<string, bool> KindEnabled { get; set; } = new();
        public Dictionary<string, float> KindDensity { get; set; } = new();
    }

    /// <summary>
    /// Liquid look. Defaults are LiquidRenderer's own, which are SYSTEM_WATER.md
    /// Draft 2's near-opaque textured surface - NOT Draft 1's Gerstner waves.
    /// WaveAmplitude 0 is deliberate and is the reversal that doc records.
    /// </summary>
    public sealed class WaterSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Draw WMO liquid (MLIQ) — Blackrock's lava lake, the Stormwind canals.
        /// DEFAULT ON: this pass is draw-only (WMO surfaces are never added to
        /// TryGetSurface / submersion), so unlike the first PLAN_15 build it
        /// cannot regress open-world water. New key, so old settings files pick
        /// up the default without a migration step.
        /// </summary>
        public bool DrawWmoLiquid { get; set; } = true;

        /// <summary>
        /// PLAN_12's A/B: take ocean/river colour from LightIntBand 13-16 instead
        /// of the hand-tuned constants.
        ///
        /// **DEFAULT FLIPPED TO FALSE, 2026-07-26.** This shipped default-ON and
        /// it ruins the water: `water.frag` MULTIPLIES the animated liquid texture
        /// by the band colour, Azeroth's authored river-close is
        /// `(0.000, 0.114, 0.161)` with red exactly zero, and vanilla's
        /// `lake_a.N.blp` frames ARE the bright animated highlight layer. Multiply
        /// the highlights by near-black and the river goes dark and monocolour --
        /// which is exactly what it did.
        ///
        /// The band INDEXING is correct (verified against wowdev and against our
        /// own sky, which is right). The values are real. **The interpretation is
        /// wrong: these are not a texture tint.** Two more signs -- the authored
        /// alphas are shallow 0.65 / deep 0.50 and ocean 1.00 / 0.75, i.e. shallow
        /// MORE opaque than deep, which is backwards for depth and sensible for
        /// camera distance; and across all 426 LightParams the close/far pairs have
        /// no systematic brightness ordering at all (river 156 vs 95, ocean 91 vs
        /// 84), which they would if they were shallow/deep.
        ///
        /// WoWee settles it: it loads all 18 colour bands, consumes seven
        /// (ambient, diffuse, fog, four sky), comments *"more channels exist
        /// (ocean, river, shadow, etc.)"* and hardcodes water colour per liquid
        /// type instead. See SYSTEM_WATER.md section 5.
        ///
        /// Leave this OFF until someone establishes what these bands actually
        /// drive in the real client. Off is bit-identical to the tuned look.
        /// </summary>
        public bool UseAuthoredColors { get; set; }

        public float DetailPercent { get; set; } = 70f;
        public bool DetailCustom { get; set; }

        public float TextureScale { get; set; } = 0.16f;
        public float AnimationFps { get; set; } = 12f;
        public float FrameBlend { get; set; }
        public float TexBrightness { get; set; } = 1f;
        public float TexContrast { get; set; } = 1f;
        public float TintR { get; set; } = 1f;
        public float TintG { get; set; } = 1f;
        public float TintB { get; set; } = 1f;

        public float Opacity { get; set; } = 1.0f;
        public float ShoreFade { get; set; } = 0.85f;
        public float ShoreWidth { get; set; } = 1.2f;

        public float DepthDarken { get; set; } = 0.78f;
        public float DepthRate { get; set; } = 0.12f;

        public float Brightness { get; set; } = 0.90f;
        public float AmbientAmount { get; set; } = 0.6f;
        public float SunAmount { get; set; } = 0.30f;
        public float SkySheen { get; set; } = 0.14f;

        public float WaveAmplitude { get; set; }
        public float WaveSpeed { get; set; } = 1.0f;

        /// <summary>
        /// River/lake body colour. THE WATER TEXTURE SUPPLIES NO COLOUR --
        /// lake_a.1.blp is a near-black greyscale highlight mask, measured mean
        /// RGB (0.014, 0.014, 0.014) -- so this is where the river gets its
        /// colour. Shallow and deep are derived from it. SYSTEM_WATER.md section 8.
        /// </summary>
        public float RiverBodyR { get; set; } = 0.13f;
        public float RiverBodyG { get; set; } = 0.16f;
        public float RiverBodyB { get; set; } = 0.17f;

        /// <summary>Ocean body colour. Same story as RiverBody.</summary>
        public float OceanBodyR { get; set; } = 0.04f;
        public float OceanBodyG { get; set; } = 0.16f;
        public float OceanBodyB { get; set; } = 0.38f;

        /// <summary>How hard the animated highlight mask is added over the body.
        /// 0 = a completely still surface, useful for judging the body colour alone.</summary>
        public float HighlightGain { get; set; } = 4.0f;

        /// <summary>
        /// Build-5875 CWater0Ripple records: wake.blp while translating and splash.blp
        /// rings while standing/turning or crossing the wade line. WakeStrength is the
        /// one presentation dial; the remaining legacy fields stay only so existing
        /// settings files continue to deserialize without losing user data.
        /// </summary>
        public bool WakeEnabled { get; set; } = true;
        public float WakeStrength { get; set; } = 0.9f;
        public float WakeLength { get; set; } = 4.5f;
        public float WakeWidth { get; set; } = 2.6f;
        public float WakeAhead { get; set; } = 0.6f;
        public float WakeFullSpeed { get; set; } = 2.5f;
        public float WakeFade { get; set; } = 0.45f;
        public float WakeRepeat { get; set; } = 2.5f;
        public float WakeWorldLock { get; set; } = 1.0f;
        public float WakeOpacity { get; set; } = 0.40f;
        public float WakeColorR { get; set; } = 0.30f;
        public float WakeColorG { get; set; } = 0.36f;
        public float WakeColorB { get; set; } = 0.42f;
    }

    /// <summary>
    /// Sky, sun and ambient. Mode is the important one: both modes resolve
    /// Light.dbc for your position and time (the hand-invented constants that
    /// SYSTEM_EXTERIOR_LIGHTING.md replaced survive only as the no-data
    /// fallback); the modes differ in how the authored values are interpreted.
    /// See SYSTEM_EXTERIOR_LIGHTING.md section "Lighting modes".
    ///
    /// TimeSource is here because where the clock comes from is a preference -
    /// but the hour itself is ALSO a DevTools instrument when pinned, the one
    /// control both surfaces keep.
    /// </summary>
    public sealed class LightingSettings
    {
        public bool DynamicLighting { get; set; } = true;

        /// <summary>
        /// How the authored Light.dbc values are interpreted. Replaced the old
        /// bool UseAuthoredData at settings v6 (that key in an old file is
        /// simply ignored on load; migration pins pre-v6 files to Msui).
        /// </summary>
        // Parity112 by owner decision 2026-08-14 (after the night-lighting
        // pass landed the dnc.db colour ramps): the vanilla-faithful look is
        // the default; Msui remains selectable as the pre-v6 hand-tuned look.
        public LightingMode Mode { get; set; } = LightingMode.Parity112;

        public float SunStrength { get; set; } = 1f;
        public float AmbientStrength { get; set; } = 1f;
        public float TerrainShadowStrength { get; set; } = 0.3f;
        public float UnitShadowOpacity { get; set; } = 0.42f;

        /// <summary>Interior baked light scale. 2.0 is vanilla - see SYSTEM_WMO_INTERIOR_LIGHTING.md.</summary>
        public float InteriorBrightness { get; set; } = 2.0f;

        /// <summary>
        /// Extra multiplier on baked MOCV light, on TOP of InteriorBrightness -
        /// WmoRenderer.InteriorBrightness, the knob that decides how strongly
        /// interior light spills from a doorway (the Northshire Abbey glow).
        /// Was a DevTools-only slider that silently reset to 1.0 every launch,
        /// which is why the spill always shipped faint. Per-mode recommended
        /// values live in ApplyLightingModeDefaults; the user can still override
        /// in Advanced.
        /// </summary>
        public float InteriorSpill { get; set; } = ParityInteriorSpill;

        /// <summary>Recommended doorway spill for MSUI Lighting - deliberately
        /// stronger than the old effective 1.0 (owner: the abbey spill was far
        /// too faint).</summary>
        public const float MsuiInteriorSpill = 1.8f;

        /// <summary>Shipped doorway/interior brightness for 1.12 Parity.</summary>
        public const float ParityInteriorSpill = 1.10f;

        /// <summary>
        /// Switch the lighting mode and push that mode's RECOMMENDED values for
        /// the knobs whose right answer is mode-dependent. Same contract as
        /// ApplyQuality: a real value push, not a label - and quality presets
        /// must never call this (the mode is not a quality dial).
        /// </summary>
        public void ApplyLightingModeDefaults(LightingMode mode)
        {
            Mode = mode;
            InteriorSpill = mode == LightingMode.Parity112
                ? ParityInteriorSpill
                : MsuiInteriorSpill;
        }

        /// <summary>
        /// Doodad baked light scale. MUST track InteriorBrightness or a barrel
        /// detaches from the floor it stands on - SYSTEM_DOODAD_LIGHTING.md's one
        /// invariant. The modal links them unless you unlink deliberately.
        /// </summary>
        public float DoodadInteriorBrightness { get; set; } = 2.0f;
        public bool LinkInteriorBrightness { get; set; } = true;

        public bool WmoVertexColors { get; set; } = true;
        public bool DoodadInteriorLighting { get; set; } = true;

        public bool SkyEnabled { get; set; } = true;
        public float SkyStopMiddle { get; set; } = 0.45f;
        public float SkyStopBand1 { get; set; } = 0.18f;
        public float SkyStopBand2 { get; set; } = 0.06f;

        /// <summary>
        /// Where the world clock comes from. Replaced the old CycleTimeOfDay
        /// bool at settings v7 (the raw file is consulted during migration
        /// because the key no longer exists to deserialize into).
        /// </summary>
        public TimeSource TimeSource { get; set; } = TimeSource.Server;

        /// <summary>Debug-cycle speed. Only consumed when TimeSource is Cycle.</summary>
        public float GameHoursPerMinute { get; set; } = 1f;

        /// <summary>The pinned hour. Only consumed when TimeSource is Fixed.</summary>
        public float TimeOfDay { get; set; } = 12f;
    }

    /// <summary>Mouse, camera feel and the free-look knobs a player would expect.</summary>
    public sealed class ControlSettings
    {
        public float MouseSensitivity { get; set; } = 1f;   // multiplier on config.Camera.MouseSensitivity
        /// <summary>Separate multiplier for the right-click-held drag (which also turns the
        /// character), independent of the left-click-held orbit-only look above.</summary>
        public float LookAroundSensitivity { get; set; } = 1f;
        public bool InvertPitch { get; set; }
        public bool RawCursor { get; set; } = true;
        /// <summary>
        /// Vanilla's inverted deselectOnClick option. False is the reference default:
        /// an empty world left-click clears the current target.
        /// </summary>
        public bool StickyTargeting { get; set; }
        /// <summary>MSUI option: let a right-click on another player's 3D model open
        /// the same interaction menu as their portrait. Off keeps world-model clicks
        /// selection-only and reserves menus for portraits.</summary>
        public bool WorldPlayerContextMenus { get; set; }
        /// <summary>
        /// Vanilla LOCK_ACTIONBAR. It blocks drag-start/drop while preserving Shift-click pickup.
        /// </summary>
        public bool LockActionBars { get; set; }
        public bool CameraCollision { get; set; } = true;
        public CameraFollowStyle CameraFollowStyle { get; set; } = CameraFollowStyle.Smart;
        /// <summary>Vanilla's hidden cameraSmoothTrackingStyle; no 1.12 options row owns it.</summary>
        public CameraFollowStyle CameraFollowTrackingStyle { get; set; } = CameraFollowStyle.Smart;
        /// <summary>Vanilla cameraYawSmoothSpeed in degrees per second (90..270).</summary>
        public float CameraFollowYawSpeed { get; set; } = CameraFollowLaw.DefaultYawSpeedDegrees;
        public float CameraClearance { get; set; } = 0.35f;
        public float CameraRestoreSpeed { get; set; } = 8f;
        public float MaxCameraDistance { get; set; } = 40f;
        /// <summary>Wheel zoom multiplier in body play (Camera -> Advanced): 1 = one yard a tick.</summary>
        public float CameraZoomSpeed { get; set; } = 1f;
        public float EyeHeight { get; set; } = 2.2f;
        public float TurnSpeedDegrees { get; set; } = 180f;
        public bool ShowPlayerNames { get; set; } = true;
        public bool ShowNpcNames { get; set; } = true;
        public bool ShowOwnName { get; set; } = true;

        // Class-colored portrait borders (issue #15). Independent per mode: off by default in
        // direct control (plain WoW look), on in the CRPG/RTS commander view.
        public bool PortraitBordersDirectControl { get; set; } = false;
        public bool PortraitBordersRts { get; set; } = true;
        public bool ChatBubbles { get; set; } = true;
        public bool PartyChatBubbles { get; set; } = true;

        /// <summary>Green center-screen "You receive loot" notices. The loot
        /// window and chat/economy state are unaffected.</summary>
        public bool ShowLootAcquisitionText { get; set; }

        /// <summary>Red center-screen entering/leaving-combat notices. Resource,
        /// aura, and damage/heal combat feedback keep their independent behavior.</summary>
        public bool ShowCombatStateText { get; set; }

        /// <summary>CRPG/RTS command strips beside the party portraits (roles, hold, patrol).</summary>
        public bool RtsCommands { get; set; }

        /// <summary>Warcraft-style spoken feedback from commanded companions, in their
        /// own 1.12 race/gender voices: hello on selection, yes on an order, charge or
        /// open fire on an attack, no on a refusal — and the classic pissed lines when
        /// a companion is clicked one time too many.</summary>
        public bool CompanionVoice { get; set; } = true;

        /// <summary>Divinity-style cutaway: while commanding an indoor toon from the
        /// free view, the building's shell/roof is hidden so the room shows from the
        /// sky. Owner verdict 2026-08-11: the open-face dollhouse look is "no good" —
        /// OFF by default, kept only as an experiment toggle. The wanted UX is a free
        /// camera that can DESCEND into buildings cleanly instead.</summary>
        public bool FreeViewCutaway { get; set; }

        /// <summary>The free-view camera is a floating body: it sweeps against walls,
        /// ceilings and terrain instead of ghosting through them, so a room contains
        /// its own view and you fly through the door to see the next one (owner
        /// decision 2026-08-11, replacing the cutaway).</summary>
        public bool FreeViewCameraCollision { get; set; } = true;

        /// <summary>Interface Options → Command View: which keys/mouse law drives the free-view
        /// rig (<see cref="Engine.CommandViewScheme"/>). First-person play is untouched.</summary>
        public CommandViewScheme CommandViewScheme { get; set; } = CommandViewScheme.Strafe;

        /// <summary>The free view's downward view angle in degrees for the schemes that lock
        /// the mouse out of pitch. Also set from the on-screen knob and PageUp/PageDown.</summary>
        public float CommandViewPitchDegrees { get; set; } = CommandViewLaw.DefaultPitchDegrees;

        /// <summary>Show the small view-angle knob at the bottom right of the free view.</summary>
        public bool CommandViewAngleKnob { get; set; } = true;

        /// <summary>Pan the free-view rig when the pointer rests on a screen edge.</summary>
        public bool CommandViewEdgePan { get; set; } = true;

        /// <summary>Wheel multiplier in the Command View: scales the rig's fly / elevator step
        /// and the Alt+wheel boom zoom alike. Owner feedback 2026-09-03: the wheel was far too
        /// sensitive there and holding Alt was only a workaround, so this is the real knob.</summary>
        public float CommandViewZoomSpeed { get; set; } = 1f;

        /// <summary>Glide the Command View camera after the rig instead of hard-coupling it
        /// (<see cref="CommandViewLaw.SmoothingTau"/>). Off = the pre-2026-09 direct camera.</summary>
        public bool CommandViewSmoothing { get; set; } = true;

        /// <summary>Lock the Command View camera on the primary selection: the rig rides the unit
        /// (eased), the mouse still turns around it, keys/wheel adjust the framing. Toggled from
        /// the on-screen tablet, Ctrl+L, or here.</summary>
        public bool CommandViewLockOnPrimary { get; set; }

        /// <summary>Cut the roof and upper walls off the building the commanded unit is in
        /// (Engine/WorldCut.cs) so the Command View sees the room from above. Shipped OFF as an
        /// experiment, then made the STANDARD by owner decision later the same day (2026-09-01)
        /// once the captures held up; still a toggle.</summary>
        public bool CommandViewCutPlane { get; set; } = true;

        /// <summary>Height of the cut above the commanded unit's feet, in yards.</summary>
        public float CommandViewCutHeight { get; set; } = 4.5f;

        /// <summary>Line-of-sight cut (Engine/WorldCut.cs, SightLine): static geometry between the
        /// camera and any party member is carved away so the party is never hidden behind a
        /// roof, a tree or a wall. Toggle; on by default (owner, 2026-09-01).</summary>
        public bool CommandViewSightCut { get; set; } = true;

        /// <summary>Party sight (World/PartySight.cs): the world is cut so the camera sees
        /// everything the primary can see - the primary's own view, reprojected to the camera.
        /// A character at a cave mouth opens the hillside over the cave floor it is looking at.
        /// Experimental and OFF by default since the same evening: its pixel-exact edges read
        /// as "weird edges" live, and the owner asked for the proximity roof cut instead
        /// (WmoRenderer.ResolveCutPlane, "treat the immediate 10 yards as cut ceilings"). Renamed from
        /// CommandViewPartySight the same night so a settings.json that saved the old default (true)
        /// no longer switches it on.</summary>
        public bool CommandViewPartySightExperimental { get; set; }

        /// <summary>Commander Guide panel width in logical px, set by dragging its left edge.
        /// 0 = size to the longest line. Clamped at draw time between the content width and
        /// the screen centre (owner, 2026-09-02).</summary>
        public float CommandViewGuideWidth { get; set; }

        /// <summary>Extra logical width added to the Key Bindings frame by dragging its right
        /// border (KeyBindingsUiLaw.ClampExtraWidth). 0 = vanilla's 640. Owner, 2026-09-03.</summary>
        public float KeyBindingsExtraWidth { get; set; }

        /// <summary>Whether the primary selection's server AI keeps fighting for it. OFF means the
        /// primary is yours alone: it moves on orders and does nothing else until you press a
        /// key (ORDER_MANUAL). ON hands its actions back to the AI (ORDER_AUTO). Owner, 2026-09-01:
        /// "primary should always be user controlled", then "on or off, as a toggle".</summary>
        public bool CommandViewPrimaryAi { get; set; }

        /// <summary>Auto Loot (the 2.0+ interface option, made the default here per owner
        /// 2026-09-02): a right-click loot takes everything at once; Shift inverts it.</summary>
        public bool AutoLoot { get; set; } = true;

        /// <summary>Automatically place accepted or recently progressed objective quests in
        /// the quest watch frame. Shift-click watches remain manual and are never governed by
        /// this switch.</summary>
        public bool AutomaticQuestTracking { get; set; } = true;
    }

    /// <summary>
    /// The NPC dev window (Ctrl+N): spawn/pathing/aggro overlays for flying around
    /// in the free view. Data comes from MangosSuperUI over HTTP; edits become
    /// change-set files, never direct writes.
    /// </summary>
    public sealed class DevWindowSettings
    {
        public bool ShowSpawnLabels { get; set; } = true;
        public bool ShowObservedPaths { get; set; } = true;
        public bool ShowAggroDiscs { get; set; } = true;
        public bool ShowWhoAggros { get; set; } = true;

        /// <summary>DB waypoint routes (creature_movement / _template) as solid polylines.</summary>
        public bool ShowDbPaths { get; set; } = true;

        /// <summary>DB spawn rows: authored spawn point markers, wander circles, and dimmed
        /// markers for spawns the server is not currently streaming.</summary>
        public bool ShowDbSpawns { get; set; } = true;

        /// <summary>Only draw aggro discs for creatures hostile to the player.</summary>
        public bool HostilesOnly { get; set; } = true;

        /// <summary>Overlay scope: false = every creature in range ("All"), true = only
        /// the focus set built by Ctrl+LeftClick while the window is open ("Selected").</summary>
        public bool FocusSelectedOnly { get; set; }

        /// <summary>Aggro reference level: "Level60" (raid), "MyLevel" (the controlled
        /// toon), or "NpcLevel" (each creature vs its own level = its base ring).</summary>
        public string AggroReference { get; set; } = "MyLevel";

        /// <summary>Concentric bands below the reference level (1 = single disc).</summary>
        public int AggroBandCount { get; set; } = 3;

        public float DiscOpacity { get; set; } = 0.32f;

        /// <summary>Overlays are culled beyond this many yards from the camera.</summary>
        public float OverlayRange { get; set; } = 150f;

        /// <summary>LEGACY. The web-app address now lives on
        /// LoginProfileSettings.WebAppUrl, outside the settings-preset blast radius
        /// and editable from the login screen's MSUI Web Connection modal. Read once
        /// in SettingsStore.Load to carry an existing file forward, then nulled so the
        /// key drops out of settings.json. Nothing else may read it.</summary>
        public string? SuiBaseUrl { get; set; }
    }

    /// <summary>
    /// The Encounter Lab (Ctrl+E). Separate from <see cref="DevWindowSettings"/> on
    /// purpose: the NPC dev window is spatial and static, the Lab is temporal and
    /// dynamic, and neither should be able to churn the other's saved settings.
    /// </summary>
    public sealed class EncounterLabSettings
    {
        /// <summary>Draw the footprints of effects landing at the scrubbed instant.</summary>
        public bool ShowFootprints { get; set; } = true;

        /// <summary>Draw every catalogued footprint the definition owns, ignoring
        /// timing — the "where could this ever land" view.</summary>
        public bool ShowStructural { get; set; }

        /// <summary>Draw the boss's authored movement route (flight waypoints).</summary>
        public bool ShowRoute { get; set; } = true;

        /// <summary>Draw scenario actor markers and the probe capsule.</summary>
        public bool ShowActors { get; set; } = true;

        /// <summary>Screen-space labels beside footprints and actors.</summary>
        public bool ShowLabels { get; set; } = true;

        /// <summary>Rendered puppet models for scenario bodies that carry a display
        /// id - Onyxia as a dragon, dummies as dummies - positions driven from the
        /// sim at the scrub head. Marks-only when off.</summary>
        public bool ShowModels { get; set; } = true;

        /// <summary>Milliseconds of simulated time per fixed step. The core's own
        /// creature update lands near 100 ms.</summary>
        public int StepMs { get; set; } = 100;

        /// <summary>Playback rate multiplier applied to wall-clock time.</summary>
        public float PlaybackSpeed { get; set; } = 1f;

        /// <summary>Seed naming the fight. Same seed, same rolls, forever.</summary>
        public int Seed { get; set; } = 2026;

        /// <summary>Fraction of the boss's health the raid removes per second. Purely a
        /// dial to make health-gated phases reachable — never presented as a damage model.</summary>
        public float RaidDpsFraction { get; set; } = 0.006f;

        /// <summary>How long a landed footprint stays on screen, in milliseconds.</summary>
        public int FootprintLingerMs { get; set; } = 1200;

        /// <summary>Opt-in: let the boss wander pre-pull in the sandbox (an invented
        /// what-if). Default OFF — she stands at spawn until pulled, the exact-db truth.
        /// A document-declared Wander/Waypoints idle plays its own exact route regardless.</summary>
        public bool SandboxRoam { get; set; }

        /// <summary>How far from spawn the pre-pull sandbox roam may take her, in yards,
        /// when <see cref="SandboxRoam"/> is on. A document-declared Wander/Waypoints idle
        /// plays its own exact route/radius instead.</summary>
        public float RoamRadiusYards { get; set; } = 22f;

        /// <summary>Tank/melee body dps counts only while the body is in melee reach
        /// of a grounded boss — an air phase honestly stalls her health gates.</summary>
        public bool MeleeDpsNeedsReach { get; set; } = true;

        /// <summary>Damage per second each living add deals its threat-lite victim
        /// while in melee reach. A consequence dial, never a combat model; 0 makes
        /// adds chase without biting (the pre-2026-08 behaviour).</summary>
        public float AddDps { get; set; } = 15f;

        /// <summary>Healing per second a Healer-job body pours into its resolved
        /// protect target — protect priorities as throughput. 0 keeps protection
        /// observational.</summary>
        public float HealerHps { get; set; } = 60f;

        /// <summary>Raid doctrine: derive formation stations from the encounter's
        /// hazard arcs, dodge telegraphs and keep clear of instant cones by default,
        /// spread off targeted casts, and derive healer assignments. Off returns
        /// every body to authored-only behaviour.</summary>
        public bool RaidDoctrine { get; set; } = true;

        /// <summary>The boss's pull ring in yards - a body crossing it starts the
        /// fight. Drawn on the ground until the pull happens.</summary>
        public float PullRangeYards { get; set; } = 30f;

        /// <summary>Record live SPELL_GO / MONSTER_MOVE traffic into a tape while the
        /// window is open. Off by default — instrumentation must not run unasked.</summary>
        public bool RecordTape { get; set; }
    }

    /// <summary>
    /// Residency. Every one of these is restart-scoped except the demand-stream
    /// switch, because the ring sizes are read when the world is built. Read
    /// SYSTEM_STREAMING.md before changing what these mean.
    /// </summary>
    public sealed class StreamingSettings
    {
        public int TileRadius { get; set; } = 1;                  // restart
        public int WmoPreloadRadius { get; set; } = 2;            // restart
        public bool DrainPreloadsAtStartup { get; set; }          // restart
    }

    // ── composites ───────────────────────────────────────────────────────────
    //
    // A composite is a REAL VALUE, not a label. Percent maps to a specific tuple
    // through a documented curve so two machines at 62% look the same. A preset
    // button that scatters four values and then forgets it did is what makes
    // settings menus untrustworthy - PLAN_11 H4.

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    /// <summary>
    /// View distance percent -> fog, building distance and the far plane.
    /// The curve is deliberately gentle at the top: vanilla's unpatched farclip
    /// ceiling was 777 yards, which lands at about 42%, so the whole upper half
    /// of the slider is already beyond what the real client could do.
    /// </summary>
    public void ResolveViewDistance()
    {
        if (View.DistanceCustom) return;

        float t = Math.Clamp(View.DistancePercent / 100f, 0f, 1f);

        View.FogEnd = Lerp(200f, 1600f, t);
        View.FogStart = View.FogEnd * 0.45f;
        View.BuildingDistance = Math.Clamp(View.FogEnd, 300f, 1250f);
        View.FarPlane = Math.Clamp(View.FogEnd * 1.35f, 500f, 4000f);
    }

    /// <summary>Object detail percent -> doodad draw distance and whether nearby-only streaming is on.</summary>
    public void ResolveObjectDetail()
    {
        if (Detail.ObjectDetailCustom) return;

        float t = Math.Clamp(Detail.ObjectDetailPercent / 100f, 0f, 1f);

        Detail.DoodadDistance = Lerp(80f, 800f, t);

        // Above about three quarters the ring is large enough that demand
        // streaming costs more in pop-in than it saves in residency.
        Detail.DoodadDemandStreaming = t < 0.75f;
    }

    /// <summary>
    /// Building detail percent -> the impostor / occlusion set. Note it does NOT
    /// touch BuildingDistance: that belongs to view distance, and two composites
    /// writing one value is how a settings page starts lying to you.
    /// </summary>
    public void ResolveBuildingDetail()
    {
        if (Detail.BuildingDetailCustom) return;

        float t = Math.Clamp(Detail.BuildingDetailPercent / 100f, 0f, 1f);

        // Higher detail = a LOWER impostor threshold, because fewer groups get
        // classified as distance-only shells and more real geometry is drawn.
        Detail.ImpostorMaxVertices = (int)MathF.Round(Lerp(4000f, 700f, t));
        Detail.InteriorCullDistance = Lerp(60f, 220f, t);
        Detail.ShellNearGuard = Lerp(120f, 260f, t);

        // Occlusion culling costs BVH traversal per group and only pays on weak
        // hardware, which is the bottom of this slider.
        Detail.OcclusionCulling = t < 0.4f;
    }

    /// <summary>
    /// Water detail percent -> animation and shoreline softness. Seventy percent is the
    /// shipped build-5875 presentation. Frame blending deliberately stays off at every
    /// automatic quality level: the 1.12 client swaps the authored texture frames rather
    /// than cross-fading them. Advanced settings can still opt into a custom blend.
    /// </summary>
    public void ResolveWaterDetail()
    {
        if (Water.DetailCustom) return;

        float t = Math.Clamp(Water.DetailPercent / 100f, 0f, 1f);
        const float shipped = 0.70f;
        if (t <= shipped)
        {
            float q = t / shipped;
            Water.AnimationFps = Lerp(4f, 12f, q);
            Water.ShoreFade = Lerp(1f, 0.85f, q);
            Water.ShoreWidth = Lerp(0.2f, 1.2f, q);
        }
        else
        {
            float q = (t - shipped) / (1f - shipped);
            Water.AnimationFps = Lerp(12f, 24f, q);
            Water.ShoreFade = Lerp(0.85f, 0.75f, q);
            Water.ShoreWidth = Lerp(1.2f, 2.0f, q);
        }
        Water.FrameBlend = 0f;
    }

    /// <summary>Run every composite that is not in custom mode. Cheap; call it after any composite moves.</summary>
    public void ResolveComposites()
    {
        ResolveViewDistance();
        ResolveObjectDetail();
        ResolveBuildingDetail();
        ResolveWaterDetail();
    }

    // ── quality presets ──────────────────────────────────────────────────────

    /// <summary>The five built-in levels. Code-defined so they cannot rot in a stale file.</summary>
    public static readonly string[] QualityNames = ["Low", "Fair", "Good", "High", "Ultra"];

    /// <summary>
    /// Overwrite this object with a built-in quality level. Everything it does
    /// not name is left alone deliberately - the 1.12 authenticity switches, the
    /// water colour set and the lighting data source are not quality dials and a
    /// preset has no business moving them.
    /// </summary>
    public void ApplyQuality(string name)
    {
        int level = Array.FindIndex(QualityNames,
            n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (level < 0) return;

        float t = level / (float)(QualityNames.Length - 1);   // 0 .. 1

        View.DistanceCustom = false;
        Detail.ObjectDetailCustom = false;
        Detail.BuildingDetailCustom = false;
        Water.DetailCustom = false;

        View.DistancePercent = Lerp(18f, 100f, t);
        Detail.ObjectDetailPercent = Lerp(15f, 100f, t);
        Detail.BuildingDetailPercent = Lerp(20f, 100f, t);
        Water.DetailPercent = Lerp(25f, 100f, t);

        Clutter.Enabled = level >= 1;
        Clutter.Density = Lerp(0.15f, 1.2f, t);
        Clutter.Radius = Lerp(20f, 90f, t);
        Clutter.MaxPerCell = (int)MathF.Round(Lerp(2f, 14f, t));
        Clutter.MaxInstances = (int)MathF.Round(Lerp(6000f, 40000f, t));

        Detail.DoodadInstancing = true;
        Detail.DoodadFlatCullBounds = true;

        Display.MultisamplingEnabled = level >= 3;
        Display.MsaaSamples = level >= 4 ? 4 : 1;
        Display.Anisotropy = Lerp(1f, 16f, t);

        Water.Enabled = true;
        Lighting.SkyEnabled = true;

        ResolveComposites();
        ActivePreset = QualityNames[level];
    }

    // ── serialisation ────────────────────────────────────────────────────────

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// A deep copy, taken when the modal opens so Cancel has something real to
    /// restore. Round-tripping through JSON rather than hand-writing a copy
    /// constructor is deliberate: a copy constructor is one more place to forget
    /// a field when a setting is added, and this runs once per modal open.
    /// </summary>
    public GameSettings Clone()
        => JsonSerializer.Deserialize<GameSettings>(JsonSerializer.Serialize(this, Json), Json)
           ?? new GameSettings();

    public static GameSettings Defaults() => new();
}

/// <summary>
/// A named set of settings the user saved. Built-in quality levels are NOT
/// stored here - they are code (<see cref="GameSettings.ApplyQuality"/>) so an
/// old settings.json cannot pin them to a stale definition.
/// </summary>
public sealed class SettingsPreset
{
    public string Name { get; set; } = "";
    public GameSettings Settings { get; set; } = new();
}

/// <summary>
/// Login-front-door profiles are persisted beside GameSettings rather than inside it.
/// Settings presets may replace graphics/input/audio preferences, but must never replace
/// connections, accounts, or locally saved passwords as a side effect.
/// </summary>
public sealed class LoginProfileSettings
{
    public string ActiveConnectionId { get; set; } = "";
    public string ActiveLaunchConfigurationId { get; set; } = "";

    /// <summary>The MangosSuperUI web app this client pushes finished spell designs
    /// to (and reads DB tables from). Stored HERE, beside the connections, and NOT
    /// inside GameSettings: a settings preset replaces the whole GameSettings object,
    /// and loading an old graphics preset must never silently restore an old server
    /// address. Empty means not set up - the Spell Workshop still previews and edits
    /// everything locally, but "Push to Completer" has nowhere to go.</summary>
    public string WebAppUrl { get; set; } = "";

    public List<ConnectionProfileSetting> Connections { get; set; } = [];
    public List<LaunchConfigurationSetting> LaunchConfigurations { get; set; } = [];
}

public sealed class ConnectionProfileSetting
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Home Server";
    public string RealmdHost { get; set; } = "127.0.0.1";
    public int RealmdPort { get; set; } = 3724;
    public string Realm { get; set; } = "";
    public int WorldPortFallback { get; set; } = 8085;
    public bool WorldUsesRealmdHost { get; set; } = true;
    public int TimeoutMs { get; set; } = 10000;
    public bool RealPortals { get; set; } = true;
}

public sealed class LaunchConfigurationSetting
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Default";
    public string ConnectionId { get; set; } = "";
    public string Mode { get; set; } = "Client";
    public bool AutoLogin { get; set; }
    public string Account { get; set; } = "";
    public bool SavePassword { get; set; }
    public string Password { get; set; } = "";
    public bool AutoEnterWorld { get; set; }
    public string Character { get; set; } = "";
}

/// <summary>
/// The file itself. Same shape and same promises as <see cref="VantageStore"/>:
/// repo-root JSON, human-readable, hand-editable, and it NEVER throws on read -
/// a missing or malformed file logs a line and starts from defaults, because
/// refusing to start over a preferences file would be absurd.
/// </summary>
public sealed class SettingsStore
{
    private sealed class FileShape
    {
        public GameSettings Settings { get; set; } = new();
        public List<SettingsPreset> Presets { get; set; } = new();
        public LoginProfileSettings LoginProfiles { get; set; } = new();
    }

    private readonly string _path;

    public GameSettings Settings { get; private set; }
    public List<SettingsPreset> Presets { get; }
    public LoginProfileSettings LoginProfiles { get; }

    /// <summary>True when the file did not exist and the defaults are in play.</summary>
    public bool IsFresh { get; private set; }

    private SettingsStore(string path, GameSettings settings, List<SettingsPreset> presets,
        LoginProfileSettings loginProfiles, bool fresh)
    {
        _path = path;
        Settings = settings;
        Presets = presets;
        LoginProfiles = loginProfiles;
        IsFresh = fresh;
    }

    /// <summary>Not named Path: a member called Path would hide System.IO.Path inside this class.</summary>
    public string FilePath => _path;

    public static SettingsStore Load(string repoRoot, string? overridePath = null)
    {
        string path = string.IsNullOrWhiteSpace(overridePath)
            ? System.IO.Path.Combine(repoRoot, "settings.json")
            : System.IO.Path.GetFullPath(overridePath);

        try
        {
            if (File.Exists(path))
            {
                string rawJson = File.ReadAllText(path);
                bool serializedPainterlyDetail = HasSerializedPainterlyDetail(rawJson);
                bool legacyCycleTimeOfDay = ReadLegacyCycleTimeOfDay(rawJson);
                var parsed = JsonSerializer.Deserialize<FileShape>(
                    rawJson, GameSettings.Json);

                if (parsed is not null)
                {
                    // Composites regenerate on load rather than being trusted from
                    // the file: a hand-edited percentage should take effect, and a
                    // curve change in a new build should reach an old file.
                    parsed.Settings.ResolveComposites();
                    Migrate(parsed.Settings, serializedPainterlyDetail, legacyCycleTimeOfDay);

                    // One-time promotion: the web-app address used to live inside
                    // DevWindowSettings, where a preset load could stomp it. Migrate()
                    // is handed parsed.Settings only and cannot reach LoginProfiles, so
                    // the carry-forward has to happen here. Terminating: the legacy key
                    // is nulled unconditionally, so a deliberately blanked address stays
                    // blank on the next launch.
                    LoginProfileSettings profiles =
                        parsed.LoginProfiles ?? new LoginProfileSettings();
                    if (profiles.WebAppUrl.Length == 0 &&
                        !string.IsNullOrWhiteSpace(parsed.Settings.DevWindow.SuiBaseUrl))
                    {
                        profiles.WebAppUrl =
                            parsed.Settings.DevWindow.SuiBaseUrl!.Trim().TrimEnd('/');
                        Console.WriteLine("[settings] promoted DevWindow.SuiBaseUrl -> " +
                                          $"LoginProfiles.WebAppUrl ({profiles.WebAppUrl})");
                    }
                    parsed.Settings.DevWindow.SuiBaseUrl = null;

                    Console.WriteLine($"[settings] {path}  " +
                                      $"preset '{parsed.Settings.ActivePreset}', " +
                                      $"{parsed.Presets.Count} saved preset(s)");
                    return new SettingsStore(path, parsed.Settings, parsed.Presets,
                        profiles, false);
                }
            }
            else
            {
                Console.WriteLine($"[settings] no {path} - starting from shipped defaults");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] could not read {path} - using defaults ({ex.Message})");
        }

        var fresh = GameSettings.Defaults();
        fresh.ResolveComposites();
        return new SettingsStore(path, fresh, new List<SettingsPreset>(),
            new LoginProfileSettings(), true);
    }

    /// <summary>
    /// One-time forward migrations keyed on <see cref="GameSettings.Version"/>, so a
    /// new shipped default reaches an existing settings.json instead of being pinned
    /// to a stale value. Each step is idempotent and bumps the version; the user's
    /// later choices (saved at the new version) are then respected.
    /// </summary>
    private static bool HasSerializedPainterlyDetail(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        return TryGetProperty(document.RootElement, "Settings", out JsonElement settings) &&
               TryGetProperty(settings, "Display", out JsonElement display) &&
               TryGetProperty(display, "PainterlyDetail", out _);
    }

    /// <summary>
    /// Raw-file read of the pre-v7 Lighting.CycleTimeOfDay flag, which no longer
    /// exists as a property to deserialize into (same pattern as the painterly
    /// presence check above). Absent or false both mean "was not cycling".
    /// </summary>
    private static bool ReadLegacyCycleTimeOfDay(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        return TryGetProperty(document.RootElement, "Settings", out JsonElement settings) &&
               TryGetProperty(settings, "Lighting", out JsonElement lighting) &&
               TryGetProperty(lighting, "CycleTimeOfDay", out JsonElement cycle) &&
               cycle.ValueKind == JsonValueKind.True;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void Migrate(GameSettings s, bool serializedPainterlyDetail, bool legacyCycleTimeOfDay)
    {
        // v1 -> v2: WMO portal culling (PLAN_10) became the shipped default - it is
        // the expected 1.12 behaviour (hides Stormwind's roof from inside, holds the
        // cathedral silhouette on approach). Force it on once for pre-v2 files.
        if (s.Version < 2)
        {
            s.Detail.WmoPortalCulling = true;
            s.Version = 2;
        }

        // v2 -> v3: ForceTwoSided goes back to being the diagnostic it was
        // written as. It shipped ON, which disabled backface culling for the
        // whole WMO pass - the pass that is most of the frame in a city, on a
        // GPU with no hidden-surface removal. Settings.Detail wins over the
        // renderer's own default (see ApplyDetail), so without this step an
        // existing settings.json would pin the old value forever and the change
        // would never reach anyone who has already run the client.
        if (s.Version < 3)
        {
            s.Detail.ForceTwoSided = false;
            s.Version = 3;
        }

        // v3 -> v4: Detail used to be an additive boost where zero still kept
        // all source detail. It is now an honest absolute gain: 0 removes the
        // residual and 1 preserves it. Preserve old files' appearance first;
        // users can then simplify from a meaningful zero. The new calm and
        // dither fields need their new defaults seeded as part of the versioned
        // transition. Property initializers apply to missing JSON fields, so use
        // the raw-file presence check to distinguish a real legacy Detail value
        // from the new absolute-gain initializer.
        if (s.Version < 4)
        {
            s.Display.PainterlyDetail = serializedPainterlyDetail
                ? Math.Clamp(1f + s.Display.PainterlyDetail, 0f, 2f)
                : 1f;
            s.Display.PainterlyCalmStart = 35f;
            s.Display.PainterlyCalmEnd = 180f;
            s.Display.PainterlyDither = 0.18f;
            s.Display.PainterlyUi = s.Display.Painterly;
            s.Version = 4;
        }

        // v4 -> v5: band count no longer implies full-strength posterization.
        // Preserve every existing painterly choice and seed only the new blend.
        if (s.Version < 5)
        {
            s.Display.PainterlyBandStrength = 0.30f;
            s.Version = 5;
        }

        // v5 -> v6 (2026-08-12): Lighting.UseAuthoredData became Lighting.Mode.
        // Every pre-v6 install was on the authored path with the MSUI
        // interpretation, so Msui - REGARDLESS of what UseAuthoredData said
        // (the old key is not even read; false only ever selected the
        // hand-invented constants, which survive as the no-data fallback).
        // InteriorSpill is new: it persists the WMO doorway-spill multiplier
        // that used to be a DevTools slider resetting to 1.0 every launch.
        // Seeding the Msui default deliberately CHANGES the look - the owner
        // called the Northshire Abbey doorway glow far too faint.
        if (s.Version < 6)
        {
            s.Lighting.Mode = LightingMode.Msui;
            s.Lighting.InteriorSpill = GameSettings.LightingSettings.MsuiInteriorSpill;
            s.Version = 6;
        }

        // v6 -> v7 (2026-08-12): the CycleTimeOfDay bool became TimeSource, and
        // the default changed from a world pinned at noon to TRACKING the game
        // clock - the server's SMSG_LOGIN_SETTIMESPEED time online, the local
        // wall clock otherwise. The owner explicitly wants the vanilla day/night
        // to move, so pre-v7 files map to Server (or Cycle if they were already
        // cycling). This deliberately CHANGES what an existing install sees;
        // the old pinned look stays one click away (TimeSource = Fixed - the
        // saved TimeOfDay hour is preserved untouched). The legacy flag comes
        // from the raw file because the property no longer exists to
        // deserialize into.
        if (s.Version < 7)
        {
            s.Lighting.TimeSource = legacyCycleTimeOfDay ? TimeSource.Cycle : TimeSource.Server;
            s.Version = 7;
        }

        // v7 -> v8: the Escape/Options windows get their own scale. Seed it from the user's
        // existing Interface scale once so the menu opens at exactly the size they already chose,
        // then the two controls are permanently independent.
        if (s.Version < 8)
        {
            s.MenuLayout ??= new GameSettings.MenuLayoutSettings();
            s.MenuLayout.Scale = Math.Clamp(s.Display.UiScale, 0.5f, 4f);
            s.Version = 8;
        }

        // v8 -> v9: FontScale only ever sized the ImGui Escape/Options type. Move ownership next
        // to the menu's independent chrome scale and preserve the user's existing value once.
        if (s.Version < 9)
        {
            s.MenuLayout ??= new GameSettings.MenuLayoutSettings();
            s.MenuLayout.TextScale = Math.Clamp(s.Display.FontScale, 0.5f, 3f);
            s.Version = 9;
        }

        // v9 -> v10: replace MSUI's unconditional per-moving-frame exponential recenter with
        // vanilla's edge-armed cameraSmoothStyle. Smart is both the 1.12 registrar default and
        // current Benilla's shipped selection. The tracking selector and speed are real engine
        // CVars but have no visible 1.12 row, so only the style is exposed by the Options page.
        if (s.Version < 10)
        {
            s.Controls.CameraFollowStyle = CameraFollowStyle.Smart;
            s.Controls.CameraFollowTrackingStyle = CameraFollowStyle.Smart;
            s.Controls.CameraFollowYawSpeed = CameraFollowLaw.DefaultYawSpeedDegrees;
            s.Version = 10;
        }

        if (s.Version < 11)
        {
            s.Display.CursorScale = 1f;
            s.HudLayout ??= new HudLayoutSettings();
            s.Version = 11;
        }

        if (s.Version < 12)
        {
            s.HudLayout ??= new HudLayoutSettings();
            HudLayoutLaw.Migrate11To12(s.HudLayout);
            s.Version = 12;
        }

        // v12 -> v13 (2026-09-04): the WaterSettings initializers described the
        // build-5875 look, but ResolveWaterDetail immediately replaced the shipped
        // 70% values with 18 FPS and 0.70 frame cross-fade. Move every non-custom
        // install onto the actual 1.12 baseline once. Explicit Advanced/custom
        // water remains the user's choice and is not touched.
        if (s.Version < 13)
        {
            if (!s.Water.DetailCustom)
            {
                s.Water.DetailPercent = 70f;
                s.ResolveWaterDetail();
            }
            s.Version = 13;
        }

    }

    /// <summary>Replace the live settings object (used by Cancel and by preset load).</summary>
    public void Replace(GameSettings settings) => Settings = settings;

    public SettingsPreset? FindPreset(string name)
    {
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Add or overwrite a named preset from the current settings, then persist.</summary>
    public void SavePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var snapshot = Settings.Clone();
        snapshot.ActivePreset = name;

        var existing = FindPreset(name);
        if (existing is not null) existing.Settings = snapshot;
        else Presets.Add(new SettingsPreset { Name = name, Settings = snapshot });

        Save();
    }

    public void DeletePreset(string name)
    {
        var existing = FindPreset(name);
        if (existing is null) return;
        Presets.Remove(existing);
        Save();
    }

    public void Save()
    {
        try
        {
            var shape = new FileShape
            {
                Settings = Settings,
                Presets = Presets,
                LoginProfiles = LoginProfiles,
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(shape, GameSettings.Json));
            IsFresh = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] could not write {_path} - {ex.Message}");
        }
    }
}
