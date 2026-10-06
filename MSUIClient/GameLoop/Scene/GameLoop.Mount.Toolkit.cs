using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// MOUNT TOOLKIT — ride anything, sit anywhere on it, and dial how it handles.
//
// Two halves, because they are answers to different questions (SYSTEM_MOUNTS.md §9):
//
//   LOOK is per steed and persisted per display id. Where the rider sits, how it
//   leans, how big both are, and where the steed itself sits relative to the unit
//   — the last of which is how you cancel a model's baked origin offset without
//   putting a guess in the renderer (the rocket cars need -3.16 forward).
//
//   FEEL is global: speed, turn rate and jump, as multipliers on the values the
//   controller already uses, so 1.0 is exactly stock. These are CLIENT PREDICTION.
//   Offline nothing argues; on a live realm the server still believes its own
//   speed and will correct you, which the panel says out loud.
//
// The ride override is client-side too: it sets nothing on the server, it just
// draws you on a steed. That is the point — it makes every mount reachable for
// tuning without a GM command or a real mount item.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private bool _mountToolkitOpen;
    private string _mountToolkitStatus = "";

    /// <summary>Handy steeds: the three "vehicles" 1.12 actually has, plus a baseline horse.</summary>
    private static readonly (int Display, string Label)[] MountPresets =
    [
        (2404, "Riding Horse"),
        (10318, "Goblin Rocket Car"),
        (2490, "Gnome Rocket Car"),
        (15381, "Steam Tonk"),
    ];

    /// <summary>
    /// The steed the local player is on: the toolkit's override first, then the server's
    /// UNIT_FIELD_MOUNTDISPLAYID. 0 is on foot.
    /// </summary>
    private int SelfMountDisplayId()
    {
        var mounts = Settings.Mounts;
        if (mounts.Riding && mounts.RideDisplayId > 0) return mounts.RideDisplayId;

        ulong guid = RenderSelfGuid;
        return guid != 0 && _entities.TryGet(guid, out WorldEntity self) ? self.MountDisplayId : 0;
    }

    /// <summary>Per-display look corrections, in the shape the renderer wants.</summary>
    private MountTuning MountTuningFor(int displayId)
    {
        GameSettings.MountTuneSetting? tune = FindMountTune(displayId);
        float rate = MathF.Max(0.05f, Settings.Mounts.AnimationRate);
        if (tune is null)
            return MountTuning.Neutral with
            {
                AnimationRate = rate,
                MountOffset = BakedMountOriginCorrection(displayId),
                HiddenSubmeshes = BuiltInDriverSubmeshes(displayId),
            };

        return new MountTuning(
            SeatOffset: new Vector3(tune.SeatForward, tune.SeatUp, tune.SeatRight),
            RiderRotationDegrees: new Vector3(tune.RiderRoll, tune.RiderYaw, tune.RiderPitch),
            RiderScale: tune.RiderScale,
            MountOffset: new Vector3(tune.MountForward, tune.MountUp, tune.MountRight),
            MountScale: tune.MountScale,
            AnimationRate: rate,
            HiddenSubmeshes: BuiltInDriverSubmeshes(displayId));
    }

    /// <summary>
    /// The NPC driver modelled into a vehicle steed, hidden so the rider IS the driver (WoW Karting:
    /// the Mirage Raceway cars as real mounts). Submesh indices of each model's view 0, read from the
    /// archives (GoblinRocketCar.m2: submesh 2 is the 770-vertex goblin at the seat; GnomeRocketCar.m2:
    /// submesh 4 is the gnome body and 5-10 its hair/ear/beard geosets 1301/702/701/1/401/1501).
    /// </summary>
    private static readonly Dictionary<int, HashSet<int>> BuiltInDrivers = new()
    {
        [10318] = [2],
        [2490] = [4, 5, 6, 7, 8, 9, 10],
    };

    private static IReadOnlySet<int>? BuiltInDriverSubmeshes(int displayId) =>
        BuiltInDrivers.TryGetValue(displayId, out HashSet<int>? hidden) ? hidden : null;

    /// <summary>
    /// The per-display offset table SYSTEM_MOUNTS.md §7 asks for, applied when the user has no tune
    /// of their own. The Mirage Raceway rocket cars bake a constant root-bone translation into every
    /// sequence (GoblinRocketCar: (-3.16, 0, 0.89)), so as real mounts (WoW Karting,
    /// shared_docs/WOW_KARTING.md) they would draw yards away from every rider - the player, other
    /// players and racing bots alike. Only these displays are corrected, and by the model's OWN
    /// measured root translation (the toolkit's "Cancel baked offset", horizontal part only), not by a constant: the two
    /// cars are authored differently (a shared constant put the gnome car beside its rider).
    /// </summary>
    private static readonly HashSet<int> BakedOriginMountDisplays = [10318, 2490];   // Goblin / Gnome Rocket Car
    private readonly Dictionary<int, Vector3> _bakedOriginCorrections = new();

    private Vector3 BakedMountOriginCorrection(int displayId)
    {
        if (!BakedOriginMountDisplays.Contains(displayId)) return Vector3.Zero;
        if (_bakedOriginCorrections.TryGetValue(displayId, out Vector3 known)) return known;
        // Measurable once the model is resident; until then (a frame or two) draw it uncorrected.
        if (_creatures is null || !_creatures.TryMeasureMountOrigin(displayId, out Vector3 drift))
            return Vector3.Zero;
        // Horizontal only: the root's vertical part (render Y, 0.89 on the goblin car) is the lift that stands
        // the car on its wheels - cancelling it sank the body a yard into the ground (owner, 2026-10-04).
        Vector3 correction = new(-drift.X, 0f, -drift.Z);
        _bakedOriginCorrections[displayId] = correction;
        Console.WriteLine($"[mount] display {displayId}: cancelling baked root offset ({drift.X:F2}, {drift.Y:F2}, {drift.Z:F2})");
        return correction;
    }

    private GameSettings.MountTuneSetting? FindMountTune(int displayId)
    {
        foreach (var tune in Settings.Mounts.Tunes)
            if (tune.DisplayId == displayId) return tune;
        return null;
    }

    private GameSettings.MountTuneSetting MountTuneFor(int displayId)
    {
        if (FindMountTune(displayId) is { } existing) return existing;
        var created = new GameSettings.MountTuneSetting { DisplayId = displayId };
        Settings.Mounts.Tunes.Add(created);
        return created;
    }

    /// <summary>Feel, pushed at the controller each frame before it integrates.</summary>
    private void ApplyMountHandling()
    {
        if (_controller is null) return;
        bool mounted = ControllerOwnsControlledBodyPose && SelfMountDisplayId() > 0;
        _controller.SpeedMultiplier = mounted ? Settings.Mounts.SpeedMultiplier : 1f;
        _controller.JumpMultiplier = mounted ? Settings.Mounts.JumpMultiplier : 1f;
    }

    private float MountTurnMultiplier() =>
        ControllerOwnsControlledBodyPose && SelfMountDisplayId() > 0
            ? MathF.Max(0.05f, Settings.Mounts.TurnMultiplier)
            : 1f;

    // ── the window ───────────────────────────────────────────────────────────

    private void DrawMountToolkit()
    {
        if (!_mountToolkitOpen || !_config.DevTools) return;

        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);
        bool open = _mountToolkitOpen;
        if (!ImGui.Begin("Mount toolkit", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            _mountToolkitOpen = open;
            return;
        }
        _mountToolkitOpen = open;

        var mounts = Settings.Mounts;
        int display = SelfMountDisplayId();
        bool save = false;

        // ── what you are on ──────────────────────────────────────────────────
        ImGui.TextUnformatted(display > 0
            ? $"riding display {display}" + (mounts.Riding ? "  (dev override)" : "  (from the server)")
            : "on foot");
        if (display > 0 && _creatures is not null)
            ImGui.TextDisabled(_creatures.TryDescribeMount(display, out string model, out float dbcScale)
                ? $"{model}   dbc scale {dbcScale:F2}"
                : "model not resident yet");
        if (_character is not null && _character.MountSeat is { } seat)
            ImGui.TextDisabled($"seat ({seat.M41:F2}, {seat.M42:F2}, {seat.M43:F2})   " +
                               $"{(_creatures?.MountsDrawnLastFrame ?? 0)} steed(s) drawn");

        ImGui.Separator();

        int ride = mounts.RideDisplayId;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Display id", ref ride)) { mounts.RideDisplayId = Math.Max(0, ride); save = true; }
        ImGui.SameLine();
        bool riding = mounts.Riding;
        if (ImGui.Checkbox("Ride it", ref riding))
        {
            mounts.Riding = riding;
            _mountToolkitStatus = riding ? "mounted (client-side only)" : "dismounted";
            save = true;
        }

        for (int i = 0; i < MountPresets.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var preset = MountPresets[i];
            if (ImGui.SmallButton(preset.Label))
            {
                mounts.RideDisplayId = preset.Display;
                mounts.Riding = true;
                _mountToolkitStatus = $"riding {preset.Label} ({preset.Display})";
                save = true;
            }
        }
        ImGui.TextDisabled("Client-side only: nothing is sent, the server is not told.");

        // ── feel ─────────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Handling", ImGuiTreeNodeFlags.DefaultOpen))
        {
            float speed = mounts.SpeedMultiplier;
            if (ImGui.SliderFloat("Speed x", ref speed, 0.25f, 4f, "%.2f")) { mounts.SpeedMultiplier = speed; save = true; }
            float turn = mounts.TurnMultiplier;
            if (ImGui.SliderFloat("Turn x", ref turn, 0.25f, 4f, "%.2f")) { mounts.TurnMultiplier = turn; save = true; }
            float jump = mounts.JumpMultiplier;
            if (ImGui.SliderFloat("Jump x", ref jump, 0.25f, 3f, "%.2f")) { mounts.JumpMultiplier = jump; save = true; }
            float rate = mounts.AnimationRate;
            if (ImGui.SliderFloat("Gait rate x", ref rate, 0.25f, 3f, "%.2f")) { mounts.AnimationRate = rate; save = true; }

            float yards = _config.Movement.RunSpeed * MathF.Max(0.05f, mounts.SpeedMultiplier);
            ImGui.TextDisabled($"run {yards:F1} yd/s  (stock {_config.Movement.RunSpeed:F1}).  " +
                               "Prediction only — a live server still believes its own speed.");
            if (ImGui.SmallButton("Reset handling"))
            {
                mounts.SpeedMultiplier = mounts.TurnMultiplier = mounts.JumpMultiplier = 1f;
                mounts.AnimationRate = 1f;
                _mountToolkitStatus = "handling back to stock";
                save = true;
            }
        }

        // ── look, per steed ──────────────────────────────────────────────────
        if (display <= 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Mount up to tune a seat — the offsets below are per display id.");
        }
        else if (ImGui.CollapsingHeader($"Seat and steed (display {display})",
                     ImGuiTreeNodeFlags.DefaultOpen))
        {
            var tune = MountTuneFor(display);

            ImGui.TextDisabled("Rider, in the steed's model space (yards)");
            float f = tune.SeatForward;
            if (ImGui.SliderFloat("Seat forward", ref f, -4f, 4f, "%.2f")) { tune.SeatForward = f; save = true; }
            float r = tune.SeatRight;
            if (ImGui.SliderFloat("Seat right", ref r, -4f, 4f, "%.2f")) { tune.SeatRight = r; save = true; }
            float u = tune.SeatUp;
            if (ImGui.SliderFloat("Seat up", ref u, -4f, 4f, "%.2f")) { tune.SeatUp = u; save = true; }

            float yaw = tune.RiderYaw;
            if (ImGui.SliderFloat("Rider yaw", ref yaw, -180f, 180f, "%.0f deg")) { tune.RiderYaw = yaw; save = true; }
            float pitch = tune.RiderPitch;
            if (ImGui.SliderFloat("Rider pitch", ref pitch, -90f, 90f, "%.0f deg")) { tune.RiderPitch = pitch; save = true; }
            float roll = tune.RiderRoll;
            if (ImGui.SliderFloat("Rider roll", ref roll, -90f, 90f, "%.0f deg")) { tune.RiderRoll = roll; save = true; }
            float riderScale = tune.RiderScale;
            if (ImGui.SliderFloat("Rider scale", ref riderScale, 0.25f, 3f, "%.2f")) { tune.RiderScale = riderScale; save = true; }

            ImGui.Spacing();
            ImGui.TextDisabled("Steed, relative to the unit's ground position");
            float mf = tune.MountForward;
            if (ImGui.SliderFloat("Steed forward", ref mf, -6f, 6f, "%.2f")) { tune.MountForward = mf; save = true; }
            float mr = tune.MountRight;
            if (ImGui.SliderFloat("Steed right", ref mr, -6f, 6f, "%.2f")) { tune.MountRight = mr; save = true; }
            float mu = tune.MountUp;
            if (ImGui.SliderFloat("Steed up", ref mu, -6f, 6f, "%.2f")) { tune.MountUp = mu; save = true; }
            float mscale = tune.MountScale;
            if (ImGui.SliderFloat("Steed scale", ref mscale, 0.25f, 3f, "%.2f")) { tune.MountScale = mscale; save = true; }

            if (ImGui.SmallButton("Reset this steed"))
            {
                Settings.Mounts.Tunes.RemoveAll(t => t.DisplayId == display);
                _mountToolkitStatus = $"display {display} back to authored";
                save = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel baked offset"))
            {
                // The rocket cars carry a constant root-bone translation, so their mesh AND
                // saddle draw yards away from the unit. Measuring it by eye is the slow way;
                // the renderer already knows where the steed ended up.
                if (_creatures is not null &&
                    _creatures.TryMeasureMountOrigin(display, out Vector3 modelSpaceDrift))
                {
                    tune.MountForward = -modelSpaceDrift.X;
                    tune.MountUp = -modelSpaceDrift.Y;
                    tune.MountRight = -modelSpaceDrift.Z;
                    _mountToolkitStatus =
                        $"cancelled ({modelSpaceDrift.X:F2}, {modelSpaceDrift.Y:F2}, {modelSpaceDrift.Z:F2})";
                }
                else _mountToolkitStatus = "no baked offset found (model not resident?)";
                save = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Some models are authored off their own origin — both Mirage Raceway\n" +
                    "rocket cars sit 3.16 yards back. This reads the model's root translation\n" +
                    "and writes the negation into the steed offset above.");
        }

        if (display > 0) save |= DrawMountKitSection(display);

        if (_mountToolkitStatus.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled(_mountToolkitStatus);
        }

        if (save) SettingsFile?.Save();
        ImGui.End();
    }

    private static readonly string[] MountKitEffectNames = ["None", "Slow", "Dash"];

    /// <summary>What the cart can fire, and what it does when it lands.</summary>
    private bool DrawMountKitSection(int display)
    {
        if (!ImGui.CollapsingHeader("Cart kit", ImGuiTreeNodeFlags.DefaultOpen)) return false;

        bool save = false;
        var tune = MountTuneFor(display);
        var mounts = Settings.Mounts;

        if (ImGui.SmallButton("Install frost kit"))
        {
            save = InstallDefaultMountKit(display) > 0;
            _mountToolkitStatus = save
                ? "Cone of Cold / Blizzard / Blink installed"
                : "spell catalog has none of those names";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Cone of Cold and Blizzard as slows, Blink as a cart dash.\n" +
                             "Looked up BY NAME in this machine's own catalog, so the ids are real.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add slot") && tune.Kit.Count < MountKitSlots)
        {
            tune.Kit.Add(new GameSettings.MountKitSlotSetting());
            _mountKitState.Clear();
            save = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Drop a token")) NoteMountKitToken();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Calls the same seam the pickup on the track will call.\n" +
                             "Set Recharge to Token and this is the only way charges come back.");

        int recharge = (int)mounts.Recharge;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.Combo("Recharge", ref recharge, ["Time", "Token"], 2))
        {
            mounts.Recharge = (GameSettings.MountKitRecharge)recharge;
            save = true;
        }
        ImGui.SameLine();
        bool keys = mounts.KitOnNumberKeys;
        if (ImGui.Checkbox("1..6 fire the cart", ref keys)) { mounts.KitOnNumberKeys = keys; save = true; }

        double now = NowSeconds();
        int remove = -1;
        for (int i = 0; i < tune.Kit.Count; i++)
        {
            var slot = tune.Kit[i];
            ImGui.PushID(i);
            ImGui.Separator();

            string charges = i < _mountKitState.Count
                ? $"{_mountKitState[i].Charges}/{slot.MaxCharges}" : $"-/{slot.MaxCharges}";
            bool ready = MountKitSlotReady(i, now);
            ImGui.TextUnformatted($"{i + 1}.  {MountKitSlotName(slot, i)}   {charges}" +
                                  (ready ? "" : "   (not ready)"));

            int spellId = (int)slot.SpellId;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.InputInt("Spell id", ref spellId))
            {
                slot.SpellId = (uint)Math.Max(0, spellId);
                slot.Label = _spellCatalog?.TryGet(slot.SpellId, out SpellInfo found) == true ? found.Name : "";
                save = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Fire")) FireMountKitSlot(i);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) remove = i;

            int effect = (int)slot.Effect;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.Combo("Effect", ref effect, MountKitEffectNames, MountKitEffectNames.Length))
            {
                slot.Effect = (GameSettings.MountKitEffectKind)effect;
                save = true;
            }

            float radius = slot.Radius;
            if (ImGui.SliderFloat("Radius / distance", ref radius, 2f, 40f, "%.0f yd")) { slot.Radius = radius; save = true; }

            if (slot.Effect == GameSettings.MountKitEffectKind.Slow)
            {
                float factor = slot.SlowFactor;
                if (ImGui.SliderFloat("Slow to", ref factor, 0.1f, 1f, "%.2f x")) { slot.SlowFactor = factor; save = true; }
                float seconds = slot.SlowSeconds;
                if (ImGui.SliderFloat("Slow for", ref seconds, 1f, 20f, "%.0f s")) { slot.SlowSeconds = seconds; save = true; }
            }

            int max = slot.MaxCharges;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.SliderInt("Charges", ref max, 1, 9)) { slot.MaxCharges = max; save = true; }
            ImGui.SameLine();
            float rechargeSeconds = slot.RechargeSeconds;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.SliderFloat("Recharge s", ref rechargeSeconds, 1f, 60f, "%.0f")) { slot.RechargeSeconds = rechargeSeconds; save = true; }
            float cooldown = slot.CooldownSeconds;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.SliderFloat("Cooldown s", ref cooldown, 0.1f, 30f, "%.1f")) { slot.CooldownSeconds = cooldown; save = true; }

            ImGui.PopID();
        }
        if (remove >= 0)
        {
            tune.Kit.RemoveAt(remove);
            _mountKitState.Clear();
            save = true;
        }

        if (tune.Kit.Count == 0)
            ImGui.TextDisabled("No kit on this cart. \"Install frost kit\" is the fastest way in.");
        if (_mountKitSlows.Count > 0)
            ImGui.TextDisabled($"{_mountKitSlows.Count} unit(s) currently slowed");

        return save;
    }

    /// <summary>
    /// The cart's own bar: one line per slot with its charges and cooldown, on screen while
    /// you are riding something that has a kit. Deliberately plain — the Blizzard-art version
    /// belongs with the rest of the HUD, and this has to be readable while driving.
    /// </summary>
    private void DrawMountKitBar()
    {
        if (!_config.DevTools) return;
        var kit = CurrentMountKit();
        if (kit is null || kit.Count == 0) return;

        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.SetNextWindowSize(new Vector2(250, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 120), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Cart", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
                                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        double now = NowSeconds();
        for (int i = 0; i < kit.Count; i++)
        {
            var slot = kit[i];
            if (slot.SpellId == 0) { ImGui.TextDisabled($"{i + 1}.  empty"); continue; }

            var state = i < _mountKitState.Count ? _mountKitState[i] : null;
            int charges = state?.Charges ?? 0;
            double cooling = state is null ? 0 : Math.Max(0, state.ReadyAt - now);

            string line = $"{i + 1}.  {MountKitSlotName(slot, i)}   {charges}/{slot.MaxCharges}";
            if (cooling > 0.05) line += $"   {cooling:F1}s";
            if (charges > 0 && cooling <= 0.05) ImGui.TextUnformatted(line);
            else ImGui.TextDisabled(line);
        }

        if (Settings.Mounts.Recharge == GameSettings.MountKitRecharge.Token)
            ImGui.TextDisabled("charges come from tokens");
        if (_mountKitStatus.Length > 0 && NowSeconds() - _mountKitStatusAt < 3.0)
            ImGui.TextDisabled(_mountKitStatus);
        ImGui.End();
    }
}
