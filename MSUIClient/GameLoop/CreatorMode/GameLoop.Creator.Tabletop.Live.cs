using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Tabletop;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop - the living board (shared_docs/QUEST_TABLETOP.md §11, 2026-10-21): the SuperUI bots on the board
// where they are now. Positions, goal/why, hp/mana and fault state come read-only from the web app's brain feed
// (GET /Bots/MapBots); what each bot looks like - race, sex, look, worn gear with display ids - from
// GET /Bots/Appearance. The brain hears from a bot about every 4 s, stamped with when it heard; the table plays every
// bot back LiveDelaySeconds behind real time and interpolates between those stamps, so figures walk instead of
// teleporting. In the near layer a bot is a view-only, dressed player (the remote-player renderer, item templates
// from the appearance feed - no server query); everywhere on the board it is a class-coloured marker with a name, an
// hp bar and a trail. Nothing here is ever added to the game's entity store or sent anywhere.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private sealed class TtLiveBot
    {
        public uint Guid;
        public string Name = "";
        public int Level, HpPct, ManaPct, TimeInGoalSec, NoProgressSec, MapId, ZoneId;
        public string Goal = "", Step = "", Why = "";
        public bool Dead, InCombat, Stalled;
        public string? Stall, Failure, Target;
        public DateTime LastStateUtc;
        /// <summary>Positions on this client's clock (NowSeconds) at the moment the brain heard them.</summary>
        public readonly List<(double At, Vector3 Pos)> Samples = new();
        public double LastSeen;
        public Vector3 Shown;            // true-height world position at the playback time
        public float Facing, Speed;
        /// <summary>How far under the ground the brain reports this bot (0 = on or above it): a fall through the world
        /// or a stale height - shown on its card, never used to place it.</summary>
        public float BelowGround;
        public TtBotLook? Look;
        public WorldEntity? View;        // the dressed, view-only player (built once the look arrives)
    }

    private sealed record TtBotLook(int Race, int Cls, int Gender, int Skin, int Face, int HairStyle, int HairColor,
        int FacialHair, IReadOnlyList<(int Slot, uint Entry)> Items);

    private const double LiveDelaySeconds = 5.0, LivePollSeconds = 1.0, LiveForgetSeconds = 12.0, LiveTrailSeconds = 60.0;
    private const double LiveLookRefreshSeconds = 120.0;
    private const float LiveTeleportYards = 150f;
    /// <summary>Below this the brain's position noise is standing still (an idle bot drifts a few yards per report).</summary>
    private const float LiveWalkYardsPerSecond = 1.5f;
    /// <summary>Selecting a bot frames it this close: a dressed figure reads at true scale.</summary>
    private const float LiveFrameRadius = 70f;

    private readonly Dictionary<uint, TtLiveBot> _live = new();
    private readonly Dictionary<uint, ItemTemplate> _liveItems = new();
    private readonly Dictionary<uint, double> _liveLookAsked = new();
    private Task<List<JsonElement>>? _livePoll;
    private Task<Dictionary<uint, (TtBotLook Look, List<ItemTemplate> Items)>>? _liveLookFetch;
    private double _livePolledAt;
    private string _liveStatus = "";
    private uint? _liveSel;
    private bool _liveFollow;
    private readonly List<(TtLiveBot Bot, Vector2 Px, float R)> _liveMarkers = new();
    private CharCreateCatalog? _liveCatalog;
    private bool _liveCatalogTried;

    private static readonly Dictionary<int, uint> LiveClassColours = new()
    {
        [1] = 0xC79C6E, [2] = 0xF58CBA, [3] = 0xABD473, [4] = 0xFFF569, [5] = 0xFFFFFF,
        [7] = 0x0070DE, [8] = 0x69CCF0, [9] = 0x9482C9, [11] = 0xFF7D0A,
    };
    private static readonly string[] LiveClassNames =
        ["", "Warrior", "Paladin", "Hunter", "Rogue", "Priest", "", "Shaman", "Mage", "Warlock", "", "Druid"];

    private static Vector3 LiveColour(TtLiveBot b)
        => b.Look is { } l && LiveClassColours.TryGetValue(l.Cls, out uint hex) ? QuestBoard.Hex(hex) : QuestBoard.Hex(0xd9c7a0);

    // ── feed ────────────────────────────────────────────────────────────────

    /// <summary>Per frame while the table is open: poll the brain feed, adopt answers, fetch missing looks, follow.</summary>
    private void LiveUpdate()
    {
        var s = Settings.Creator.Tabletop;
        if (!s.LiveBots || _tabletop?.Board is not { } board || string.IsNullOrWhiteSpace(SuiWebAppUrl))
        {
            if (!s.LiveBots) { _live.Clear(); _liveSel = null; _liveStatus = ""; }
            return;
        }
        double now = NowSeconds();
        string web = SuiWebAppUrl.TrimEnd('/');

        if (_livePoll is null && now - _livePolledAt >= LivePollSeconds)
        {
            _livePolledAt = now;
            _livePoll = Task.Run(async () =>
            {
                using var http = WebAppHttp.Create(TimeSpan.FromSeconds(5));
                using var doc = JsonDocument.Parse(await http.GetStringAsync(web + "/Bots/MapBots"));
                return doc.RootElement.TryGetProperty("bots", out var bots)
                    ? bots.EnumerateArray().Select(b => b.Clone()).ToList() : new List<JsonElement>();
            });
        }
        if (_livePoll is { IsCompleted: true } poll)
        {
            _livePoll = null;
            if (poll.IsCompletedSuccessfully) LiveAdopt(poll.Result, board, now);
            else _liveStatus = "live bots: " + (poll.Exception?.GetBaseException().Message ?? "feed failed");
        }

        // Looks: once per bot, refreshed rarely (gear changes), batched.
        if (_liveLookFetch is { IsCompleted: true } fetch)
        {
            _liveLookFetch = null;
            if (fetch.IsCompletedSuccessfully)
                foreach (var (guid, (look, items)) in fetch.Result)
                {
                    foreach (var it in items) _liveItems[it.Entry] = it;
                    if (_live.TryGetValue(guid, out var bot)) { bot.Look = look; bot.View = LiveBuildView(bot, look); }
                }
            else Console.WriteLine($"[tabletop] live looks failed: {fetch.Exception?.GetBaseException().Message}");
        }
        if (_liveLookFetch is null)
        {
            var want = _live.Values.Where(b => b.Look is null
                    ? !_liveLookAsked.TryGetValue(b.Guid, out double at) || now - at > 10
                    : now - _liveLookAsked.GetValueOrDefault(b.Guid) > LiveLookRefreshSeconds)
                .Select(b => b.Guid).Take(200).ToList();
            if (want.Count > 0)
            {
                foreach (uint g in want) _liveLookAsked[g] = now;
                string url = web + "/Bots/Appearance?guids=" + string.Join(',', want);
                _liveLookFetch = Task.Run(async () =>
                {
                    using var http = WebAppHttp.Create(TimeSpan.FromSeconds(15));
                    using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
                    return LiveParseLooks(doc.RootElement);
                });
            }
        }

        // Playback: every bot LiveDelaySeconds behind now, between the two samples around that moment.
        double t = now - LiveDelaySeconds;
        foreach (var bot in _live.Values) LivePlace(bot, t);

        // Follow the selected bot (any pan or flight lets go).
        if (_liveFollow && _ttDragButton == 0 && _ttPressTravel >= TabletopLaw.ClickTravelPixels) _liveFollow = false;
        if (_liveFollow && _ttFlight is null && _liveSel is { } sel && _live.TryGetValue(sel, out var followed))
        {
            float k = 1f - MathF.Exp(-ImGui.GetIO().DeltaTime * 3f);
            _ttTarget = Vector3.Lerp(_ttTarget, followed.Shown, k);
        }
    }

    private void LiveAdopt(List<JsonElement> rows, TabletopLaw.Board board, double now)
    {
        int onBoard = 0, total = rows.Count;
        var utcNow = DateTime.UtcNow;
        foreach (var r in rows)
        {
            uint guid = r.TryGetProperty("guid", out var g) && g.TryGetUInt32(out uint gu) ? gu : 0;
            if (guid == 0 || !r.TryGetProperty("pos", out var pos)) continue;
            int map = Int(r, "mapId");
            var p = new Vector3(F(pos, "x"), F(pos, "y"), F(pos, "z"));
            if (map != board.MapId || !board.Contains(p.X, p.Y)) { if (_live.Remove(guid) && _liveSel == guid) _liveSel = null; continue; }
            onBoard++;
            if (!_live.TryGetValue(guid, out var bot)) _live[guid] = bot = new TtLiveBot { Guid = guid };
            bot.Name = Str(r, "name"); bot.Level = Int(r, "level");
            bot.Goal = Str(r, "goal"); bot.Step = Str(r, "step"); bot.Why = Str(r, "why");
            bot.HpPct = Int(r, "hpPct"); bot.ManaPct = Int(r, "manaPct");
            bot.TimeInGoalSec = Int(r, "timeInGoalSec"); bot.NoProgressSec = Int(r, "noProgressSec");
            bot.MapId = map; bot.ZoneId = Int(r, "zoneId");
            bot.Dead = Bool(r, "dead"); bot.InCombat = Bool(r, "inCombat");
            bot.Stall = Compact(r, "stall"); bot.Stalled = bot.Stall is not null;
            bot.Failure = Compact(r, "failure"); bot.Target = Compact(r, "target");
            bot.LastSeen = now;

            // The brain stamps when it heard: place the sample at that moment on our clock.
            DateTime heard = r.TryGetProperty("sensoryFeed", out var feed) && feed.TryGetProperty("lastStateReceivedUtc", out var h)
                && h.ValueKind == JsonValueKind.String && DateTime.TryParse(h.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hu) ? hu : utcNow;
            if (heard != bot.LastStateUtc || bot.Samples.Count == 0)
            {
                bot.LastStateUtc = heard;
                double at = now - Math.Max(0, (utcNow - heard).TotalSeconds);
                if (bot.Samples.Count > 0 && at <= bot.Samples[^1].At) at = bot.Samples[^1].At + 0.01;
                bot.Samples.Add((at, p));
                while (bot.Samples.Count > 2 && bot.Samples[0].At < now - LiveTrailSeconds - LiveDelaySeconds) bot.Samples.RemoveAt(0);
            }
        }
        foreach (uint stale in _live.Values.Where(b => now - b.LastSeen > LiveForgetSeconds).Select(b => b.Guid).ToList())
        {
            _live.Remove(stale);
            if (_liveSel == stale) _liveSel = null;
        }
        _liveStatus = $"live bots: {onBoard} on this board ({total} online)";

        static float F(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetSingle(out float f) ? f : 0f;
        static int Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
        static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static string? Compact(JsonElement e, string k)
        {
            if (!e.TryGetProperty(k, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            string text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            return text.Length > 400 ? text[..400] + "..." : text;
        }
    }

    /// <summary>Where a bot is at playback time t: between the samples around t, held at the last one past it, snapped
    /// across a teleport.</summary>
    private void LivePlace(TtLiveBot bot, double t)
    {
        // On the live table a bot inside the eye's bubble is in the world right now: the truth, not the feed's past.
        if (_liveTableOpen && _entities.TryGet(bot.Guid, out WorldEntity liveBody))
        {
            bot.Shown = liveBody.Position; bot.Facing = liveBody.Orientation;
            bot.Speed = liveBody.IsMoving ? 4f : 0f; bot.BelowGround = 0f;
            return;
        }
        var sm = bot.Samples;
        if (sm.Count == 0) return;
        int i = sm.FindLastIndex(x => x.At <= t);
        Vector3 at;
        float speed = 0f, facing = bot.Facing;
        if (i < 0) at = sm[0].Pos;
        else if (i == sm.Count - 1) at = sm[i].Pos;
        else
        {
            var (t0, p0) = sm[i];
            var (t1, p1) = sm[i + 1];
            float d = Vector2.Distance(new(p0.X, p0.Y), new(p1.X, p1.Y));
            if (d > LiveTeleportYards) at = p1;
            else
            {
                float u = (float)Math.Clamp((t - t0) / Math.Max(t1 - t0, 0.001), 0, 1);
                at = Vector3.Lerp(p0, p1, u);
                speed = d / (float)Math.Max(t1 - t0, 0.001);
                if (d > 0.3f) facing = MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X);
            }
        }
        // Stand on the real ground (the near layer's where loaded, the relief's elsewhere). The brain's heights drift a
        // few yards and can be far UNDER the surface (2026-10-21: Bluebear reported 37 yd below the Barrens floor, which
        // put the follow camera under the board): below or just above the ground is the ground; well above it (a
        // bridge, a roof, flight) is kept.
        float? ground = TtGroundAt(at.X, at.Y) ?? _tabletop?.HeightAt(at.X, at.Y);
        bot.BelowGround = ground is { } g0 && g0 - at.Z > 5f ? g0 - at.Z : 0f;
        if (ground is { } g && at.Z - g < 12f) at.Z = g;
        bot.Shown = at; bot.Speed = speed >= LiveWalkYardsPerSecond ? speed : 0f; bot.Facing = facing;
    }

    private static Dictionary<uint, (TtBotLook Look, List<ItemTemplate> Items)> LiveParseLooks(JsonElement root)
    {
        var result = new Dictionary<uint, (TtBotLook, List<ItemTemplate>)>();
        if (!root.TryGetProperty("bots", out var bots) || bots.ValueKind != JsonValueKind.Object) return result;
        foreach (var b in bots.EnumerateObject())
        {
            if (!uint.TryParse(b.Name, out uint guid)) continue;
            var v = b.Value;
            int I(JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.TryGetInt32(out int n) ? n : 0;
            uint U(JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.TryGetUInt32(out uint n) ? n : 0u;
            var slots = new List<(int, uint)>();
            var items = new List<ItemTemplate>();
            if (v.TryGetProperty("items", out var arr))
                foreach (var it in arr.EnumerateArray())
                {
                    uint entry = U(it, "entry");
                    if (entry == 0) continue;
                    slots.Add((I(it, "slot"), entry));
                    items.Add(new ItemTemplate
                    {
                        Entry = entry, DisplayInfoId = U(it, "display"), InventoryType = U(it, "invType"),
                        Class = U(it, "itemClass"), Subclass = U(it, "subclass"), Material = (uint)Math.Max(0, I(it, "material")),
                        Sheath = U(it, "sheath"),
                    });
                }
            var look = new TtBotLook(I(v, "race"), I(v, "cls"), I(v, "gender"), I(v, "skin"), I(v, "face"),
                I(v, "hairStyle"), I(v, "hairColor"), I(v, "facialHair"), slots);
            result[guid] = (look, items);
        }
        return result;
    }

    /// <summary>The dressed, view-only player for a bot: race/class/sex, look and worn item entries in the same descriptor
    /// fields a remote player arrives with; the near layer's renderer resolves the entries from <see cref="_liveItems"/>.</summary>
    private WorldEntity? LiveBuildView(TtLiveBot bot, TtBotLook look)
    {
        if (!_liveCatalogTried) { _liveCatalogTried = true; _liveCatalog = CharCreateCatalog.Load(_config.ClientDataPath); }
        uint display = _liveCatalog?.NativeDisplay((byte)look.Race, (byte)look.Gender) ?? 0;
        if (display == 0 || look.Race is < 1 or > 8 || look.Gender > 1) return null;
        var f = ObjectFields.ForSyntheticUnit((int)display);
        f.SetU32(ObjectFields.UNIT_NATIVEDISPLAYID, display);
        f.SetU32(ObjectFields.UNIT_BYTES_0, (uint)(look.Race | (look.Cls << 8) | (look.Gender << 16)));
        f.SetU32(ObjectFields.PLAYER_BYTES, (uint)(look.Skin | (look.Face << 8) | (look.HairStyle << 16) | (look.HairColor << 24)));
        f.SetU32(ObjectFields.PLAYER_BYTES_2, (uint)look.FacialHair);
        foreach (var (slot, entry) in look.Items)
            if (slot is >= 0 and < 19) f.SetU32((ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + slot * 12), entry);
        f.SetU32(ObjectFields.UNIT_MAXHEALTH, 100);
        f.SetU32(ObjectFields.UNIT_HEALTH, 100);
        return new WorldEntity { Guid = 0xF140_B07B_0000_0000UL | bot.Guid, Type = ObjectTypeId.Player, Fields = f, Position = bot.Shown };
    }

    /// <summary>The live bots standing in the near layer this frame, dressed and posed (TabletopUnits adds them).</summary>
    private void LiveAppendUnits(List<WorldEntity> into, IReadOnlySet<(int col, int row)> tiles, float figure)
    {
        if (!Settings.Creator.Tabletop.LiveBots || (_ttNear is null && !_liveTableOpen)) return;
        // Offline the near scene dresses them from the appearance feed; on the live table the world's renderer
        // resolves items through the session's own item cache (the server answers).
        if (_ttNear is not null && !_liveTableOpen)
            _ttNear.PlayerItemResolver = entry => _liveItems.TryGetValue(entry, out var it) ? (true, it) : (false, null);
        foreach (var bot in _live.Values)
        {
            if (bot.View is not { } view || !tiles.Contains(TabletopLawTile(bot.Shown))) continue;
            // In the eye's bubble the live world already draws this very bot, casting and all.
            if (_liveTableOpen && _entities.TryGet(bot.Guid, out _)) continue;
            float ground = TtGroundAt(bot.Shown.X, bot.Shown.Y) ?? bot.Shown.Z;
            view.Fields.SetU32(ObjectFields.OBJECT_SCALE_X, BitConverter.SingleToUInt32Bits(figure));
            view.Fields.SetU32(ObjectFields.UNIT_HEALTH, bot.Dead ? 0u : (uint)Math.Clamp(bot.HpPct, 1, 100));
            view.Fields.SetUnitStandState((byte)(bot.Dead ? UnitStandState.Dead : UnitStandState.Stand));
            var shown = bot.Shown with { Z = bot.Shown.Z + TtLift(ground) };
            uint flags = bot.Speed > 0f ? (uint)MovementFlags.Forward : 0u;
            if (bot.Speed is > 0f and < 3.5f) flags |= (uint)MovementFlags.WalkMode;
            into.Add(view.WithRenderPose(shown, bot.Facing, bot.Dead ? 0f : bot.Speed, flags, false));
        }
    }

    // ── overlays ────────────────────────────────────────────────────────────

    /// <summary>Markers, names, hp bars and trails for every live bot on the board (over the 3D table).</summary>
    private void LiveDrawOverlays()
    {
        _liveMarkers.Clear();
        var s = Settings.Creator.Tabletop;
        if (!s.LiveBots || _live.Count == 0 || _tabletop?.Board is null) return;
        var display = ImGui.GetIO().DisplaySize;
        var dl = ImGui.GetBackgroundDrawList();
        float ui = MathF.Max(1f, display.Y / 1080f);
        double now = NowSeconds(), t = now - LiveDelaySeconds;
        bool close = _ttRadius < 1500f;
        var font = ImGui.GetFont();

        foreach (var bot in _live.Values.OrderBy(b => b.Guid == _liveSel))
        {
            var col = LiveColour(bot);
            bool sel = bot.Guid == _liveSel;
            if (s.LiveTrails && bot.Samples.Count > 1)
            {
                var pts = new List<Vector2>();
                foreach (var (at, p) in bot.Samples)
                {
                    if (at > t) break;
                    if (QaProject(QaShown(p, 1.5f), display, out var px)) pts.Add(px);
                }
                if (QaProject(QaShown(bot.Shown, 1.5f), display, out var head)) pts.Add(head);
                for (int i = 1; i < pts.Count; i++)
                {
                    if (Vector2.Distance(pts[i - 1], pts[i]) > display.X * 0.3f) continue;   // a teleport, not a walk
                    float a = (sel ? 0.8f : 0.45f) * (0.25f + 0.75f * i / pts.Count);
                    dl.AddLine(pts[i - 1], pts[i], QaCol(col, a), (sel ? 2.5f : 1.5f) * ui);
                }
            }

            var foot = QaShown(bot.Shown, 0f);
            if (!QaProject(foot, display, out var fp) || QaOccluded(foot + new Vector3(0, 0, 2f))) continue;
            float r = (sel ? 6.5f : 4.5f) * ui;
            uint body = bot.Dead ? QaCol(new Vector3(0.45f), 0.9f) : QaCol(col, 0.95f);
            // Where the dressed model stands (near layer, close enough to read), the marker is a ring at its feet,
            // never a dot over it; everywhere else a filled class-coloured dot.
            var nearTiles = _liveTableOpen ? _liveTableTiles : _ttNear?.ReadyTiles;
            bool modelShown = _ttRadius < 700f && (bot.View is not null || _liveTableOpen) && nearTiles?.Contains(TabletopLawTile(bot.Shown)) == true;
            if (modelShown)
            {
                if (!QaProject(foot + new Vector3(1.2f, 0, 0), display, out var edge)) edge = fp + new Vector2(r, 0);
                r = MathF.Max(Vector2.Distance(fp, edge) * 1.1f, 6f * ui);
                dl.AddCircle(fp, r, body, 32, 2f * ui);
            }
            else
            {
                dl.AddCircleFilled(fp, r, body, 16);
                dl.AddCircle(fp, r, QaCol(new Vector3(0.06f, 0.04f, 0.02f), 0.9f), 16, 1.5f * ui);
            }
            if (bot.InCombat && !bot.Dead) dl.AddCircle(fp, r + 3f * ui, QaCol(QuestBoard.Hex(0xe0614f), 0.95f), 20, 2f * ui);
            if (bot.Stalled) dl.AddCircle(fp, r + (6f + 2f * MathF.Sin((float)now * 6f)) * ui, QaCol(QuestBoard.Hex(0xff3b30), 0.9f), 24, 2f * ui);
            if (sel) dl.AddCircle(fp, r + 10f * ui, QaCol(new Vector3(1f, 0.96f, 0.84f), 0.95f), 28, 2f * ui);
            _liveMarkers.Add((bot, fp, MathF.Max(r + 4f * ui, 10f * ui)));

            if (!(close || sel)) continue;
            // Name over the head (the head is a figure's height above the foot at the current figure scale).
            float figure = TabletopLaw.FigureScale(s.FigureScale, _ttRadius);
            if (!QaProject(foot + new Vector3(0, 0, 2.6f * figure), display, out var hp)) hp = fp;
            string name = bot.Dead ? $"{bot.Name} (dead)" : bot.Name;
            float size = 12.5f * ui;
            var ts = QaTextSize(name, size);
            var pos = hp - new Vector2(ts.X * 0.5f, ts.Y + 9f * ui);
            dl.AddText(font, size, pos + new Vector2(1f, 1f) * ui, QaCol(Vector3.Zero, 0.9f), name);
            dl.AddText(font, size, pos, QaCol(col, 1f), name);
            // hp bar
            float w = MathF.Max(ts.X, 34f * ui), h = 3.5f * ui;
            var bar = hp - new Vector2(w * 0.5f, 6f * ui);
            dl.AddRectFilled(bar, bar + new Vector2(w, h), QaCol(new Vector3(0.08f, 0.05f, 0.03f), 0.85f));
            dl.AddRectFilled(bar, bar + new Vector2(w * Math.Clamp(bot.HpPct, 0, 100) / 100f, h),
                QaCol(bot.HpPct > 50 ? QuestBoard.Hex(0x4caf50) : bot.HpPct > 20 ? QuestBoard.Hex(0xe8a33a) : QuestBoard.Hex(0xe0614f), 0.95f));
        }
    }

    private TtLiveBot? LiveHit(Vector2 mouse)
    {
        TtLiveBot? best = null;
        float bestD = float.MaxValue;
        foreach (var (bot, px, r) in _liveMarkers)
        {
            float d = Vector2.Distance(px, mouse);
            if (d <= r && d < bestD) { best = bot; bestD = d; }
        }
        return best;
    }

    /// <summary>A rendered unit that is one of the live bots' dressed views.</summary>
    private TtLiveBot? LiveBotOf(WorldEntity unit)
        => unit.IsPlayer && (unit.Guid & 0xFFFF_FFFF_0000_0000UL) == 0xF140_B07B_0000_0000UL
           && _live.TryGetValue((uint)unit.Guid, out var bot) ? bot : null;

    private void LiveSelect(TtLiveBot bot)
    {
        QaClearSelection();
        _liveSel = bot.Guid;
        _liveFollow = true;
        TabletopFlyTo(bot.Shown, LiveFrameRadius, 1.0f, null, TabletopLaw.FrameSeconds);
    }

    private void LiveTooltip(TtLiveBot bot)
    {
        string cls = bot.Look is { } l && l.Cls is > 0 and < 12 ? LiveClassNames[l.Cls] : "";
        ImGui.TextColored(new Vector4(LiveColour(bot), 1f), bot.Name);
        ImGui.TextDisabled($"Lv {bot.Level} {cls} · {bot.Goal}{(bot.Step.Length > 0 && bot.Step != "idle" ? " / " + bot.Step : "")}");
        ImGui.TextDisabled($"hp {bot.HpPct}%  mana {bot.ManaPct}%" + (bot.Dead ? " · dead" : bot.InCombat ? " · in combat" : "") + (bot.Stalled ? " · STALLED" : ""));
    }

    // ── card ────────────────────────────────────────────────────────────────

    private void LiveCard(TtLiveBot bot)
    {
        string cls = bot.Look is { } l && l.Cls is > 0 and < 12 ? LiveClassNames[l.Cls] : "";
        ImGui.TextDisabled($"Bot {bot.Guid} · live, {LiveDelaySeconds:0} s behind");
        var badges = new List<(string, Vector4)> { ($"Lv {bot.Level}", AtlasInkDim) };
        if (cls.Length > 0) badges.Add((cls, new Vector4(LiveColour(bot), 1f)));
        if (bot.Dead) badges.Add(("dead", new Vector4(0.7f, 0.7f, 0.7f, 1f)));
        else if (bot.InCombat) badges.Add(("in combat", new Vector4(0.88f, 0.38f, 0.31f, 1f)));
        if (bot.Stalled) badges.Add(("STALLED", new Vector4(1f, 0.23f, 0.19f, 1f)));
        QaBadges(badges);

        QaSection("Doing");
        ImGui.TextColored(AtlasGold, bot.Goal + (bot.Step.Length > 0 ? " / " + bot.Step : ""));
        if (bot.Why.Length > 0) ImGui.TextWrapped(bot.Why);
        ImGui.TextDisabled($"in this goal {LiveAgo(bot.TimeInGoalSec)} · no progress for {LiveAgo(bot.NoProgressSec)}");

        QaSection("State");
        LiveBar("Health", bot.HpPct, QuestBoard.Hex(0x4caf50));
        LiveBar("Mana", bot.ManaPct, QuestBoard.Hex(0x4f8fe0));
        if (bot.Target is { } target) { ImGui.TextDisabled("Target"); ImGui.TextWrapped(target); }
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
            $"map {bot.MapId} · zone {bot.ZoneId} · ({bot.Shown.X:0}, {bot.Shown.Y:0}, {bot.Shown.Z:0})"));
        if (bot.BelowGround > 0f)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), $"reported {bot.BelowGround:0} yd under the ground (fell through, or a stale height)");
        if (bot.Stall is { } stall) { QaSection("Stall"); ImGui.TextWrapped(stall); }
        if (bot.Failure is { } failure) { QaSection("Last failure"); ImGui.TextWrapped(failure); }

        ImGui.Spacing();
        bool follow = _liveFollow;
        if (ImGui.Checkbox("Follow with the camera", ref follow))
        {
            _liveFollow = follow;
            if (follow) TabletopFlyTo(bot.Shown, MathF.Min(_ttRadius, LiveFrameRadius), null, null, TabletopLaw.FrameSeconds);
        }
        if (CreatorButton("Open bot map in web") && !string.IsNullOrWhiteSpace(SuiWebAppUrl))
        {
            string url = $"{SuiWebAppUrl.TrimEnd('/')}/Bots/Map";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Console.WriteLine($"[tabletop] could not open {url}: {e.Message}"); }
        }
    }

    private static string LiveAgo(int seconds)
        => seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s";

    private void LiveBar(string label, int pct, Vector3 colour)
    {
        float s = CreatorPixelScale;
        ImGui.TextDisabled(label);
        ImGui.SameLine(70f * s);
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X - 40f * s, h = ImGui.GetTextLineHeight() * 0.6f;
        var dl = ImGui.GetWindowDrawList();
        var y = p + new Vector2(0f, ImGui.GetTextLineHeight() * 0.2f);
        dl.AddRectFilled(y, y + new Vector2(w, h), QaCol(new Vector3(0.1f, 0.07f, 0.04f), 0.9f), 2f * s);
        dl.AddRectFilled(y, y + new Vector2(w * Math.Clamp(pct, 0, 100) / 100f, h), QaCol(colour, 0.95f), 2f * s);
        ImGui.Dummy(new Vector2(w, ImGui.GetTextLineHeight()));
        ImGui.SameLine();
        ImGui.TextDisabled($"{pct}%");
    }
}
