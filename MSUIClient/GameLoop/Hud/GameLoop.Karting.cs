using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Portals;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// WoW Karting, client half (shared_docs/WOW_KARTING.md). The race is the server's
// (Core SuiKarting RaceManager): this file asks to join/leave (/kart, or the race
// marshal's gossip), draws the WoW-styled 3-2-1-GO and the race HUD from
// SMSG_SUI_KART, and prepares the next leg's Real Portal destination the moment the
// server names it, so the kart crosses into a READY window instead of a curtain.
//
// The countdown is Mario Kart 64's: three beats and a GO, one second apart, each a
// number that lands big and settles - drawn in the zone-text face over a three-lamp
// starter (the Mirage Raceway's own Daisy calls it in-world), with a map-ping beat
// and a goblin steam whistle on GO.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private bool _kartingAvailable;
    private KartingWire.State? _kartState;
    private double _kartStateAt = double.NegativeInfinity;
    private int _kartBeat = -1;                       // 3, 2, 1, 0 = GO; -1 none
    private double _kartBeatAt = double.NegativeInfinity;
    private KartingWire.Prewarm? _kartPendingPrewarm;
    private KartingWire.ItemState _kartItem;
    private double _kartItemAt = double.NegativeInfinity;
    private bool _kartUseWasDown;
    private int _kartRouletteFrame = -1;

    private const double KartBeatSeconds = 1.0;       // one beat shown per second, MK64
    private const double KartGoHoldSeconds = 1.2;
    private const double KartStateStaleSeconds = 5.0;

    private void ApplyKartingCapability(uint capabilities)
    {
        bool available = (capabilities & KartingWire.Capability) != 0;
        if (available != _kartingAvailable)
            Console.WriteLine(available
                ? "[karting] server advertised karting-v1"
                : "[karting] server has no karting-v1 advertisement");
        _kartingAvailable = available;
    }

    private void ResetKarting()
    {
        _kartItem = default;
        _kartingAvailable = false;
        _kartState = null;
        _kartBeat = -1;
        _kartPendingPrewarm = null;
    }

    private void ApplySuiKart(byte[] body)
    {
        switch (KartingWire.KindOf(body))
        {
            case KartingWire.Kind.State:
                if (KartingWire.ParseState(body) is { } state)
                {
                    if (_kartState?.Phase != state.Phase)
                        EmitInterface("karting", "phase", state.Phase.ToString().ToUpperInvariant(), ControlledGuid,
                            $"race={state.Race};racers={state.Racers.Count};laps={state.Laps};checkpoints={state.Checkpoints}");
                    _kartState = state;
                    _kartStateAt = NowSeconds();
                }
                break;
            case KartingWire.Kind.Countdown:
                if (KartingWire.ParseCountdown(body) is { } beat)
                {
                    _kartBeat = beat.Count;
                    _kartBeatAt = NowSeconds();
                    PlayUiSound(beat.Count > 0 ? "MapPing" : "DeadMineSteamWhistleon", "ui.karting");
                    EmitInterface("karting", "countdown", beat.Count > 0 ? $"BEAT{beat.Count}" : "GO", ControlledGuid, $"race={beat.Race}");
                }
                break;
            case KartingWire.Kind.Prewarm:
                if (KartingWire.ParsePrewarm(body) is { } prewarm)
                {
                    _kartPendingPrewarm = prewarm;
                    Console.WriteLine($"[karting] next portal {prewarm.PortalEntry} -> map {prewarm.Map}");
                }
                break;
            case KartingWire.Kind.Item:
                if (KartingWire.ParseItem(body) is { } item)
                {
                    bool fresh = item.RouletteMs > 0 && _kartItem.RouletteMs == 0;
                    _kartItem = item;
                    _kartItemAt = NowSeconds();
                    if (fresh) PlayUiSound("igQuestListOpen", "ui.karting");
                    EmitInterface("karting", "item", item.Item.ToString().ToUpperInvariant(), ControlledGuid,
                        $"uses={item.Uses};rouletteMs={item.RouletteMs};goldenMs={item.GoldenMs}");
                }
                break;
            case KartingWire.Kind.Notice:
                if (KartingWire.ParseNotice(body) is { } notice)
                {
                    AddChatMessage(KartNoticeText(notice.Code, notice.Param));
                    EmitInterface("karting", "notice", notice.Code.ToString().ToUpperInvariant(), ControlledGuid, $"param={notice.Param}");
                }
                break;
        }
    }

    private static string KartNoticeText(KartingWire.NoticeCode code, uint param) => code switch
    {
        KartingWire.NoticeCode.Joined => "You join the next kart race.",
        KartingWire.NoticeCode.Left => "You leave the kart race.",
        KartingWire.NoticeCode.NotOnKart => "You need to be on a kart to race. The race marshal has karts.",
        KartingWire.NoticeCode.RaceFull => "The race is full.",
        KartingWire.NoticeCode.NoCourse => "There is no race course on this realm.",
        KartingWire.NoticeCode.Lap => param > 0 ? $"Lap {param}!" : "Final lap!",
        KartingWire.NoticeCode.Finished => $"You finish {Ordinal((int)param)}!",
        KartingWire.NoticeCode.Dnf => "You did not finish.",
        _ => "",
    };

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st", 2 => "nd", 3 => "rd", _ => "th",
    });

    private bool HandleKartSlash(string args)
    {
        string verb = args.Trim().ToLowerInvariant();
        if (_net is null) return true;
        if (!_kartingAvailable)
        {
            AddChatMessage("This realm has no kart racing.");
            return true;
        }
        KartingWire.Action? action = verb switch
        {
            "join" or "" => KartingWire.Action.Join,
            "leave" or "quit" => KartingWire.Action.Leave,
            "start" or "go" => KartingWire.Action.StartNow,
            "status" => KartingWire.Action.Query,
            "use" or "item" or "use back" or "item back" => KartingWire.Action.UseItem,
            _ => null,
        };
        if (action is null)
        {
            AddChatMessage("Kart racing: /kart join, /kart leave, /kart start (fill with bots and go), /kart status, /kart use [back] (or the Use Kart Item key).");
            return true;
        }
        _net.SuiKart(action.Value, verb.EndsWith(" back", StringComparison.Ordinal) ? 1u : 0u);
        return true;
    }

    /// <summary>
    /// Hand the next leg's portal to the Real Portals warm slot once nothing else owns it and a world is
    /// loaded: a kart covers the 150-yd prepare radius in seven seconds, too little to load a continent.
    /// </summary>
    /// <summary>When a portal crossing last made a prepared world the active one.</summary>
    private double _lastWorldSwitchAt = double.NegativeInfinity;

    /// <summary>
    /// The arrival world needs these seconds to itself: a next-leg warm started on the arrival frame churned the
    /// world just promoted (terrain cache released, WMO/doodad sets swapped) - beige ground and trees vanishing
    /// for seconds after every crossing (owner, 2026-10-04, frame bursts dumps/frames/rec1-*).
    /// </summary>
    private const double KartWarmSettleSeconds = 8.0;

    private void AdvanceKartPortalPrewarm()
    {
        if (_kartPendingPrewarm is not { } p || !RealPortalPreviewEnabled || _realPortalCastPrewarm is not null ||
            _net is not { IsInWorld: true } || RealPortalNow() - _lastWorldSwitchAt < KartWarmSettleSeconds)
            return;
        var hint = new PortalPrewarmHint(0, p.PortalEntry, p.TeleportSpell, p.Map, p.Position, p.Orientation);
        _kartPendingPrewarm = null;
        if (!hint.IsValid) return;
        _realPortalCastPrewarm = new RealPortalCastPrewarm
        {
            CasterGuid = ControlledGuid,
            Hint = hint,
            ExistingPortalGuids = [],
            ExpiresAt = RealPortalNow() + 240.0,   // a leg is ~40-90 s
            Placed = true,
        };
        if (_realPortalScene?.IsActive == true &&
            (_realPortalScene.Descriptor is not null || _realPortalScene.PrewarmHint != hint))
            RetireRealPortalScene();
        Console.WriteLine($"[karting] warming the next portal's destination: map {p.Map}");
    }

    private void DrawKartingHud()
    {
        AdvanceKartPortalPrewarm();
        double now = NowSeconds();
        KartingWire.State? state = _kartState is { } live && now - _kartStateAt <= KartStateStaleSeconds ? live : null;
        DrawKartCountdown(now);
        if (state is null || state.Phase is KartingWire.Phase.Idle) return;

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        KartingWire.Racer? self = null;
        foreach (KartingWire.Racer r in state.Racers)
            if ((r.Flags & KartingWire.RacerFlags.Self) != 0) self = r;

        // Top centre: lap, place, clock - three large numbers a driver reads in a glance.
        var size = new Vector2(330, 58) * s;
        var min = new Vector2((display.X - size.X) * .5f, 64 * s);
        var max = min + size;
        dl.AddRectFilled(min + new Vector2(2, 2) * s, max - new Vector2(2, 2) * s, 0xC0080808u, 4 * s);
        _skin?.DrawBackdrop(dl, min, max, WowSkin.Tooltip, Vector4.Zero, new Vector4(0.8f, 0.8f, 0.8f, 1f));
        float third = size.X / 3f;
        string lap = self is { } me ? $"{Math.Clamp(me.Lap, 1, Math.Max(1, state.Laps))}/{state.Laps}" : $"-/{state.Laps}";
        string place = self is { } mine && mine.Rank > 0 ? $"{Ordinal(mine.Rank)}/{state.Racers.Count}" : $"-/{state.Racers.Count}";
        uint elapsedMs = self is { FinishMs: > 0 } done ? done.FinishMs : state.ElapsedMs;
        string clock = state.Phase is KartingWire.Phase.Racing or KartingWire.Phase.Finished ? KartClock(elapsedMs) : "0:00.0";
        if (state.Phase == KartingWire.Phase.Racing && self is null or { FinishMs: 0 })
            clock = KartClock(elapsedMs + (uint)Math.Max(0, (now - _kartStateAt) * 1000));
        DrawKartCell(dl, "LAP", lap, new Vector2(min.X + third * .5f, min.Y), s);
        DrawKartCell(dl, "PLACE", place, new Vector2(min.X + third * 1.5f, min.Y), s);
        DrawKartCell(dl, "TIME", clock, new Vector2(min.X + third * 2.5f, min.Y), s);
        if (state.Phase is KartingWire.Phase.Racing or KartingWire.Phase.Countdown)
            DrawKartItemWindow(dl, new Vector2(max.X, min.Y), s, now);

        if (state.Phase == KartingWire.Phase.Gathering)
            GameText.DrawCentered(dl, "GameFontNormal", "Racers gathering at the Mirage Raceway...",
                new Vector2(display.X * .5f, max.Y + 14 * s), s);

        if (state.HasNext && self is { FinishMs: 0 } && state.NextMap == _config.Start.Map && _controller is not null)
            DrawKartNextArrow(dl, state, new Vector2(display.X * .5f, max.Y + 40 * s), s);

        if (state.Phase == KartingWire.Phase.Finished || self is { FinishMs: > 0 })
            DrawKartResults(dl, state, new Vector2(display.X * .5f, display.Y * .30f), s);
    }

    /// <summary>The held item's WoW icon (and a tint for the turtle shells' colours).</summary>
    private static (string Icon, uint Tint) KartItemIcon(KartingWire.Item item) => item switch
    {
        KartingWire.Item.Banana or KartingWire.Item.BananaBunch => ("INV_Misc_Food_24", 0xFFFFFFFFu),
        KartingWire.Item.GreenShell or KartingWire.Item.TripleGreen => ("Ability_Hunter_Pet_Turtle", 0xFF80FF80u),
        KartingWire.Item.RedShell or KartingWire.Item.TripleRed => ("Ability_Hunter_Pet_Turtle", 0xFF6060FFu),
        KartingWire.Item.SpinyShell => ("Ability_Hunter_Pet_Turtle", 0xFFFF9060u),
        KartingWire.Item.Mushroom or KartingWire.Item.TripleMushroom => ("INV_Mushroom_11", 0xFFFFFFFFu),
        KartingWire.Item.GoldenMushroom => ("INV_Mushroom_11", 0xFF40D0FFu),
        KartingWire.Item.Star => ("Spell_Holy_DivineIntervention", 0xFFFFFFFFu),
        KartingWire.Item.Lightning => ("Spell_Nature_Lightning", 0xFFFFFFFFu),
        KartingWire.Item.FakeItemBox => ("INV_Misc_QuestionMark", 0xFF8080FFu),
        KartingWire.Item.Boo => ("Ability_Stealth", 0xFFFFFFFFu),
        _ => ("INV_Misc_QuestionMark", 0xFFFFFFFFu),
    };

    private static string KartItemName(KartingWire.Item item) => item switch
    {
        KartingWire.Item.BananaBunch => "Banana Bunch", KartingWire.Item.GreenShell => "Green Shell",
        KartingWire.Item.TripleGreen => "Triple Green Shells", KartingWire.Item.RedShell => "Red Shell",
        KartingWire.Item.TripleRed => "Triple Red Shells", KartingWire.Item.SpinyShell => "Spiny Shell",
        KartingWire.Item.TripleMushroom => "Triple Mushrooms", KartingWire.Item.GoldenMushroom => "Golden Mushroom",
        KartingWire.Item.FakeItemBox => "Fake Item Box", _ => item.ToString(),
    };

    private void UpdateKartItemBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.KartUseItem);
        if (down && !_kartUseWasDown && !typing && _kartingAvailable && _net is not null &&
            _kartItem.Item != KartingWire.Item.None && _kartState?.Phase == KartingWire.Phase.Racing)
            _net.SuiKart(KartingWire.Action.UseItem, BindingDown(GameBinding.MoveBackward) ? 1u : 0u);
        _kartUseWasDown = down;
    }

    /// <summary>
    /// MK64's item window, top centre beside the lap panel: the roulette spins through every item (a tick each
    /// step) until the server's roll lands, then the held item with its uses; the golden mushroom shows its clock.
    /// </summary>
    private void DrawKartItemWindow(ImDrawListPtr dl, Vector2 topRight, float s, double now)
    {
        var size = new Vector2(58, 58) * s;
        var min = new Vector2(topRight.X + 10 * s, topRight.Y);
        var max = min + size;
        dl.AddRectFilled(min + new Vector2(2, 2) * s, max - new Vector2(2, 2) * s, 0xC0080808u, 4 * s);
        _skin?.DrawBackdrop(dl, min, max, WowSkin.Tooltip, Vector4.Zero, new Vector4(0.8f, 0.8f, 0.8f, 1f));
        KartingWire.ItemState held = _kartItem;
        double rouletteLeft = held.RouletteMs / 1000.0 - (now - _kartItemAt);
        KartingWire.Item shown = held.Item;
        if (rouletteLeft > 0)
        {
            int frame = (int)((now - _kartItemAt) * 12);
            shown = (KartingWire.Item)(1 + frame % 14);
            if (frame != _kartRouletteFrame) { _kartRouletteFrame = frame; if (frame % 2 == 0) PlayUiSound("MapPing", "ui.karting"); }
        }
        else if (_kartRouletteFrame >= 0)
        {
            _kartRouletteFrame = -1;
            if (held.Item != KartingWire.Item.None) PlayUiSound("igQuestListComplete", "ui.karting");
        }
        var inset = new Vector2(7, 7) * s;
        if (shown != KartingWire.Item.None)
        {
            (string icon, uint tint) = KartItemIcon(shown);
            uint tex = PainterlyArt($@"Interface\Icons\{icon}.blp");
            if (tex != 0) dl.AddImage((nint)tex, min + inset, max - inset, Vector2.Zero, Vector2.One, tint);
            if (rouletteLeft <= 0)
            {
                if (held.Uses > 1)
                    GameText.DrawRightAligned(dl, "NumberFontNormal", $"x{held.Uses}", new Vector2(max.X - 5 * s, max.Y - 18 * s), s);
                GameText.DrawCentered(dl, "GameFontNormalSmall", KartItemName(shown), new Vector2((min.X + max.X) * .5f, max.Y + 9 * s), s);
                if (held.GoldenMs > 0)
                {
                    double golden = held.GoldenMs / 1000.0 - (now - _kartItemAt);
                    if (golden > 0)
                        GameText.DrawCentered(dl, "GameFontHighlightSmall", $"{golden:0.0}s", new Vector2((min.X + max.X) * .5f, max.Y + 22 * s), s);
                }
            }
        }
        else
            GameText.DrawCentered(dl, "GameFontDisableSmall", "Item", new Vector2((min.X + max.X) * .5f, (min.Y + max.Y) * .5f), s);
    }

    private static string KartClock(uint ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms / 100 % 10}";

    private static void DrawKartCell(ImDrawListPtr dl, string label, string value, Vector2 top, float s)
    {
        GameText.DrawCentered(dl, "GameFontNormalSmall", label, top + new Vector2(0, 14) * s, s);
        GameText.DrawCentered(dl, "GameFontHighlightLarge", value, top + new Vector2(0, 36) * s, s);
    }

    /// <summary>A gold chevron pointing at the next checkpoint relative to where the camera looks, with its distance.</summary>
    private void DrawKartNextArrow(ImDrawListPtr dl, KartingWire.State state, Vector2 center, float s)
    {
        Vector3 here = _controller!.Position;
        Vector2 to = new(state.NextCheckpoint.X - here.X, state.NextCheckpoint.Y - here.Y);
        float distance = to.Length();
        float angle = MathF.Atan2(to.Y, to.X) - _window.Camera.Yaw;   // 0 = straight ahead
        // Screen up is ahead; WoW yaw grows counter-clockwise (to the left), screen x grows right.
        Vector2 dir = new(-MathF.Sin(angle), -MathF.Cos(angle));
        Vector2 side = new(-dir.Y, dir.X);
        float r = 14 * s;
        dl.AddTriangleFilled(center + dir * r, center - dir * r * .6f + side * r * .8f, center - dir * r * .6f - side * r * .8f, 0xE000D1FFu);
        dl.AddTriangle(center + dir * r, center - dir * r * .6f + side * r * .8f, center - dir * r * .6f - side * r * .8f, 0xFF000000u, 1.5f * s);
        GameText.DrawCentered(dl, "GameFontHighlightSmall", $"{distance:0} yd", center + new Vector2(0, 26) * s, s);
    }

    private void DrawKartResults(ImDrawListPtr dl, KartingWire.State state, Vector2 top, float s)
    {
        var rows = state.Racers.Where(r => (r.Flags & KartingWire.RacerFlags.Finished) != 0)
            .OrderBy(r => r.Rank).ToList();
        if (rows.Count == 0) return;
        var size = new Vector2(300, 34 + 18 * rows.Count) * s;
        var min = new Vector2(top.X - size.X * .5f, top.Y);
        var max = min + size;
        dl.AddRectFilled(min + new Vector2(2, 2) * s, max - new Vector2(2, 2) * s, 0xD0080808u, 4 * s);
        _skin?.DrawBackdrop(dl, min, max, WowSkin.Tooltip, Vector4.Zero, new Vector4(0.8f, 0.8f, 0.8f, 1f));
        GameText.DrawCentered(dl, "GameFontNormal", "Results", new Vector2(top.X, min.Y + 14 * s), s);
        float y = min.Y + 28 * s;
        foreach (KartingWire.Racer r in rows)
        {
            bool mine = (r.Flags & KartingWire.RacerFlags.Self) != 0;
            string font = mine ? "GameFontHighlight" : "GameFontNormalSmall";
            GameText.Draw(dl, font, $"{Ordinal(r.Rank)}  {ThreatMeterName(r.Guid)}", new Vector2(min.X + 12 * s, y), s);
            GameText.DrawRightAligned(dl, font, KartClock(r.FinishMs), new Vector2(max.X - 12 * s, y), s);
            y += 18 * s;
        }
    }

    /// <summary>
    /// MK64's start: each beat lands huge and settles inside the second; three red lamps, all green on GO.
    /// The numbers are the zone-text face, so the start reads as WoW, not as a port.
    /// </summary>
    private void DrawKartCountdown(double now)
    {
        if (_kartBeat < 0) return;
        double t = now - _kartBeatAt;
        if (t > (_kartBeat == 0 ? KartGoHoldSeconds : KartBeatSeconds)) { if (_kartBeat == 0) _kartBeat = -1; return; }
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        var center = new Vector2(display.X * .5f, display.Y * .38f);

        // The starter lamps: three housings; lit red up to this beat, all green on GO.
        float lamp = 22 * s, gap = 58 * s;
        var housingMin = center + new Vector2(-gap * 1.5f, -150 * s);
        var housingMax = center + new Vector2(gap * 1.5f, -150 * s + lamp * 2.6f);
        dl.AddRectFilled(housingMin, housingMax, 0xE0101010u, 8 * s);
        dl.AddRect(housingMin, housingMax, 0xFF2F5A7Au, 8 * s, ImDrawFlags.None, 2 * s);
        for (int i = 0; i < 3; i++)
        {
            var c = new Vector2(center.X + (i - 1) * gap, housingMin.Y + lamp * 1.3f);
            bool lit = _kartBeat == 0 || i < 4 - _kartBeat;
            uint color = _kartBeat == 0 ? 0xFF30E040u : lit ? 0xFF2020E0u : 0xFF303030u;
            dl.AddCircleFilled(c, lamp, color, 24);
            dl.AddCircle(c, lamp, 0xFF000000u, 24, 2 * s);
            if (lit) dl.AddCircleFilled(c - new Vector2(lamp * .3f, lamp * .3f), lamp * .25f, 0x80FFFFFFu, 12);
        }

        // The number lands at 2.2x and settles to 1x in the first quarter, then fades in the last.
        float land = (float)Math.Clamp(t / 0.25, 0, 1);
        float size = 120f * (2.2f - 1.2f * (1 - (1 - land) * (1 - land)));
        double life = _kartBeat == 0 ? KartGoHoldSeconds : KartBeatSeconds;
        float alpha = (float)Math.Clamp((life - t) / 0.3, 0, 1);
        string text = _kartBeat == 0 ? "GO!" : _kartBeat.ToString();
        uint baseColor = _kartBeat == 0 ? 0x0030E040u : 0x0000D1FFu;      // green GO, WoW gold numbers
        uint color2 = baseColor | ((uint)(alpha * 255) << 24);
        // 1.12 caps a FontHeight at 32 (GameTextLaw.EmCap); big text is a SCALED frame, as in FrameXML.
        GameText.DrawPlainCentered(dl, text, center, 32f, s * size / 32f, color2, (uint)(alpha * 230) << 24);
    }
}
