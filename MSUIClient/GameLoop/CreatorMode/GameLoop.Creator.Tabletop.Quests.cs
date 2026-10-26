using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Tabletop;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P3 - quests on the table (shared_docs/QUEST_TABLETOP.md §5, §6): the zone bundle from the web app
// (GET /QuestAtlas/Zone), pieces for givers/enders on posts, chain arcs (ender of the earlier quest → giver of the
// later one; same NPC → a loop), the selected quest's journey, objective dots and explore rings, filters, the quest list
// grouped by chain, a compact card that links out to the full web card, and the layers popover. Semantics ported from
// the web sketch (wwwroot/js/questatlas/{model,pieces,index}.js). Overlays are drawn as anti-aliased draw-list shapes
// over the 3D table; pieces behind a hill are hidden by a sight-line test against the shown relief.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private readonly QuestAtlasClient _qaClient = new();
    private Task<List<Q.ZoneSummary>>? _qaZonesTask;
    private List<Q.ZoneSummary>? _qaZones;
    private string _qaZonesStatus = "";
    private Task<Q.ZoneBundle?>? _qaBundleTask;
    private uint _qaBundleArea;
    private Q.ZoneBundle? _qaBundle;
    private QuestBoard? _qaBoard;
    private string _qaStatus = "";
    private uint? _qaPendingSelect;

    // Filters (§5) and selection.
    private string _qaSearch = "";
    private string _qaFaction = "all";
    private int _qaLvMin = 1, _qaLvMax = 70;
    // "Filed in this zone only" starts on (owner, 2026-10-21): a wide map rectangle otherwise pulls in every neighbour's quests.
    private bool _qaHomeOnly = true, _qaClassQuests = true;
    private readonly HashSet<uint> _qaVisible = new();
    private uint? _qaSelQuest;
    private string? _qaSelEntity;
    private (uint Entry, Vector3 At)? _qaSelFigure;
    private bool _qaDirty = true;
    private bool _qaLayersOpen;
    private bool _qaScrollToSelection;

    // Pieces rebuilt on any filter / layer / selection change; positions are world yards, shown each frame.
    private sealed record QaPin(string Kind, uint Entry, Vector3 Place, HashSet<uint> Gives, HashSet<uint> Ends, string Glyph,
        Vector3 Colour, bool InFocus, bool Faded, float Lift, bool Label);
    private sealed record QaArc(Vector3 A, Vector3 B, Vector3 Colour, float Opacity, bool Dashed, float Thick, bool Loop, bool Arrow);
    private sealed record QaDot(Vector3 At, Vector3 Colour, float Px, float Opacity);
    private sealed record QaRing(Q.Trigger Trigger, bool Strong);
    private sealed record QaLabel(string Text, Vector3 At, float Lift, bool Faded, bool Boxed);
    private readonly List<QaPin> _qaPins = new();
    private readonly List<QaArc> _qaArcs = new();
    private readonly List<QaDot> _qaDots = new();
    private readonly List<QaRing> _qaRings = new();
    private readonly List<QaLabel> _qaLabels = new();
    private readonly List<(QaPin Pin, Vector2 Min, Vector2 Max)> _qaPinRects = new();
    private object? _qaHover;

    private static readonly Vector3 QaGold = QuestBoard.Hex(0xf2c14e), QaVisitor = QuestBoard.Hex(0xc9bfa8), QaCustom = QuestBoard.Hex(0xc59bf0);
    private static readonly Dictionary<string, Vector3> QaKindColour = new()
    {
        ["kill"] = QuestBoard.Hex(0xe0614f), ["collect"] = QuestBoard.Hex(0xe8a33a),
        ["object"] = QuestBoard.Hex(0x6fb3e0), ["explore"] = QuestBoard.Hex(0xb48be0),
    };

    // ── data ────────────────────────────────────────────────────────────────

    /// <summary>A zone starts loading: fetch its bundle (the board may need it for its rectangle).</summary>
    private void QaBeginZone(uint areaId)
    {
        _qaBundleArea = areaId;
        _qaBundle = null;
        _qaBoard = null;
        _qaSelQuest = null; _qaSelEntity = null; _qaSelFigure = null;
        _qaPins.Clear(); _qaArcs.Clear(); _qaDots.Clear(); _qaRings.Clear(); _qaLabels.Clear();
        _qaBundleTask = string.IsNullOrWhiteSpace(SuiWebAppUrl) ? null : _qaClient.ZoneAsync(SuiWebAppUrl, areaId);
        _qaStatus = _qaBundleTask is null ? "Quests: no web app configured." : "Gathering quests...";
    }

    /// <summary>The bundle's own rectangle, or one framed on its givers and enders (+250 yd, at least a tile across; §8).</summary>
    private static TabletopLaw.Board? QaBundleBoard(Q.ZoneBundle? bundle, string mapDirectory)
    {
        if (bundle is null) return null;
        int map = bundle.Zone.MapId;
        if (bundle.Zone.Rect is { } r)
            return new TabletopLaw.Board(map, mapDirectory, MathF.Max(r.Top, r.Bottom), MathF.Min(r.Top, r.Bottom), MathF.Max(r.Left, r.Right), MathF.Min(r.Left, r.Right));
        var pts = new List<Vector2>();
        foreach (var q in bundle.Quests)
            foreach (var reference in q.Givers.Concat(q.Enders))
            {
                var spawns = reference.Kind == "npc" ? bundle.Npcs.GetValueOrDefault(reference.Entry.ToString())?.Spawns
                                                     : bundle.Objects.GetValueOrDefault(reference.Entry.ToString())?.Spawns;
                foreach (var s in spawns ?? new()) if (s.Length >= 4 && (int)s[0] == map) pts.Add(new Vector2(s[1], s[2]));
            }
        if (pts.Count == 0) return null;
        const float pad = 250f;
        float top = pts.Max(p => p.X) + pad, bottom = pts.Min(p => p.X) - pad, left = pts.Max(p => p.Y) + pad, right = pts.Min(p => p.Y) - pad;
        static (float, float) Grow(float hi, float lo) { float d = TabletopLaw.TileYards - (hi - lo); return d > 0 ? (hi + d / 2, lo - d / 2) : (hi, lo); }
        (top, bottom) = Grow(top, bottom);
        (left, right) = Grow(left, right);
        return new TabletopLaw.Board(map, mapDirectory, top, bottom, left, right);
    }

    /// <summary>Per frame: adopt the zone list and the bundle, build the model once the board is up, rebuild pieces when dirty.</summary>
    private void QaUpdate()
    {
        if (_qaZonesTask is null && _qaZones is null && !string.IsNullOrWhiteSpace(SuiWebAppUrl))
        {
            _qaZonesTask = _qaClient.ZonesAsync(SuiWebAppUrl);
            _qaZonesStatus = "Loading zones...";
        }
        if (_qaZonesTask is { IsCompleted: true } zt)
        {
            _qaZonesTask = null;
            if (zt.IsFaulted) { _qaZones = new(); _qaZonesStatus = "Zones: " + zt.Exception?.GetBaseException().Message; }
            else { _qaZones = zt.Result; _qaZonesStatus = ""; }
        }
        if (_qaBundleTask is { IsCompleted: true } bt)
        {
            _qaBundleTask = null;
            if (bt.IsFaulted) _qaStatus = "Quests: " + bt.Exception?.GetBaseException().Message;
            else if (bt.Result is null) _qaStatus = "No quests for this zone on the web app.";
            else _qaBundle = bt.Result;
        }
        if (_qaBundle is not null && _qaBoard is null && _ttLoad is null && _tabletop?.Board is { } board && _ttAreaId == _qaBundleArea)
        {
            _qaBoard = new QuestBoard(_qaBundle, (map, x, y) => map == board.MapId && board.Contains(x, y));
            var (lo, hi) = _qaBoard.LevelRange;
            _qaStatus = $"{_qaBoard.Quests.Count} quests · levels {lo}-{hi} · {_qaBundle.Npcs.Count} NPCs";
            Console.WriteLine($"[tabletop] quests {_ttZoneName}: {_qaStatus}; {_qaBoard.Chains.Count(c => c.Multi)} chains, {_qaBoard.Edges.Count} links");
            _qaDirty = true;
            if (_qaPendingSelect is { } pending && _qaBoard.Quests.ContainsKey(pending)) { _qaPendingSelect = null; QaSelectQuest(pending); }
        }
        if (_qaDirty && _qaBoard is not null) { QaRefresh(); _qaDirty = false; }
    }

    // ── filters, selection ──────────────────────────────────────────────────

    private void QaRefresh()
    {
        if (_qaBoard is not { } m) return;
        string term = _qaSearch.Trim().ToLowerInvariant();
        _qaVisible.Clear();
        foreach (var q in m.Quests.Values)
        {
            if (q.Level > 0 && (q.Level < _qaLvMin || q.Level > _qaLvMax)) continue;
            if (_qaFaction != "all" && QuestBoard.Faction(q) is var fa && fa != "both" && fa != _qaFaction) continue;
            if (_qaHomeOnly && !q.Home) continue;
            if (!_qaClassQuests && QuestBoard.IsClassOrRace(q)) continue;
            if (term.Length > 0 && !m.SearchText(q).Contains(term, StringComparison.Ordinal)) continue;
            _qaVisible.Add(q.Id);
        }
        if (_qaSelQuest is { } sel) _qaVisible.Add(sel);   // never hide what you're reading
        QaRebuild();
    }

    private void QaSelectQuest(uint id, bool fly = true)
    {
        if (_qaBoard is not { } m || !m.Quests.ContainsKey(id)) return;
        _qaSelQuest = id; _qaSelEntity = null; _qaSelFigure = null;
        _qaVisible.Add(id);
        _qaScrollToSelection = true;
        QaRebuild();
        if (fly) QaFrameFocus();
    }

    private void QaSelectEntity(string kind, uint entry)
    {
        _qaSelQuest = null; _qaSelEntity = QuestBoard.Key(kind, entry); _qaSelFigure = null;
        QaRebuild();
        QaFrameFocus();
    }

    private void QaClearSelection()
    {
        _qaSelQuest = null; _qaSelEntity = null; _qaSelFigure = null;
        _liveSel = null; _liveFollow = false;
        _qaDirty = true;
    }

    /// <summary>Points worth framing for the selection: its givers/enders and the centroid of each objective.</summary>
    private List<Vector3> QaFocusPoints()
    {
        var pts = new List<Vector3>();
        if (_qaBoard is not { } m) return pts;
        if (_qaSelQuest is { } id && m.Quests.TryGetValue(id, out var q))
        {
            foreach (var r in q.Givers.Concat(q.Enders)) pts.AddRange(m.PiecePlaces(r.Kind, r.Entry));
            foreach (var t in m.ObjectiveTargets(q)) if (QuestBoard.Centroid(t.Points) is { } c) pts.Add(c);
        }
        else if (_qaSelEntity is { } e)
        {
            var parts = e.Split(':');
            pts.AddRange(m.PiecePlaces(parts[0], uint.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        return pts;
    }

    private void QaFrameFocus()
    {
        var pts = QaFocusPoints();
        if (pts.Count == 0 || _tabletop is null) return;
        var min = new Vector2(pts.Min(p => p.X), pts.Min(p => p.Y));
        var max = new Vector2(pts.Max(p => p.X), pts.Max(p => p.Y));
        var c = (min + max) * 0.5f;
        float radius = TabletopLaw.FrameRadius(max.X - min.X, max.Y - min.Y);
        var target = new Vector3(c.X, c.Y, _tabletop.HeightAt(c.X, c.Y));
        // Centre the framing in the open gap between the sidebar and the card, not on the whole screen: shift the target
        // sideways by the gap's offset from the screen centre, in yards per pixel at the target's distance.
        var display = ImGui.GetIO().DisplaySize;
        float gapRight = display.X - (QaCardOpen ? QaCardWidth + 14f * CreatorPixelScale : 0f);
        float offsetPx = display.X * 0.5f - (_creatorSidebarRight + gapRight) * 0.5f;
        // The framing law is sized for a full screen; the panels leave a narrower gap, so pull back in proportion.
        float gap = MathF.Max(gapRight - _creatorSidebarRight, display.X * 0.3f);
        radius *= Math.Clamp(display.X / gap * 0.8f, 1f, 2f);
        float yardsPerPx = 2f * radius * MathF.Tan(TabletopLaw.FovDegrees * MathF.PI / 360f) / MathF.Max(display.Y, 1f);
        var eyeDir = TabletopLaw.OrbitPosition(Vector3.Zero, 1f, 0.9f, _ttAzimuth);
        var right = Vector3.Normalize(Vector3.Cross(-eyeDir, Vector3.UnitZ));
        target += new Vector3(right.X, right.Y, 0f) * offsetPx * yardsPerPx;
        TabletopFlyTo(target, radius, 0.9f, null, TabletopLaw.FrameSeconds);
    }

    // ── pieces (pieces.js rebuild) ──────────────────────────────────────────

    private float QaSpan => _tabletop?.Board?.Span ?? 3000f;
    private float QaPostBase => QaSpan * 0.014f;

    private void QaRebuild()
    {
        _qaPins.Clear(); _qaArcs.Clear(); _qaDots.Clear(); _qaRings.Clear(); _qaLabels.Clear();
        if (_qaBoard is not { } m) return;
        var layers = Settings.Creator.Tabletop;
        var selQ = _qaSelQuest is { } sid && m.Quests.TryGetValue(sid, out var sq) ? sq : null;
        var focus = new HashSet<string>();
        if (selQ is not null) foreach (var r in selQ.Givers.Concat(selQ.Enders)) focus.Add(QuestBoard.Key(r));
        if (_qaSelEntity is { } se) focus.Add(se);
        bool dimOthers = selQ is not null || _qaSelEntity is not null;
        float postBase = QaPostBase, levelStep = QaSpan * 0.004f;
        var (lvLo, lvHi) = m.LevelRange;

        // People: one piece per giver/ender spawn (≤ 4 per entity).
        var pieces = new Dictionary<string, (string Kind, uint Entry, Vector3 Place, HashSet<uint> Gives, HashSet<uint> Ends)>();
        void Take(IEnumerable<Q.Ref> refs, uint qid, bool gives)
        {
            foreach (var r in refs)
            {
                var places = m.PiecePlaces(r.Kind, r.Entry);
                for (int i = 0; i < places.Count; i++)
                {
                    string k = $"{QuestBoard.Key(r)}:{i}";
                    if (!pieces.TryGetValue(k, out var pc)) pieces[k] = pc = (r.Kind, r.Entry, places[i], new(), new());
                    (gives ? pc.Gives : pc.Ends).Add(qid);
                }
            }
        }
        foreach (uint id in _qaVisible)
        {
            var q = m.Quests[id];
            Take(q.Givers, id, true);
            Take(q.Enders, id, false);
        }
        foreach (var pc in pieces.Values)
        {
            bool g = pc.Gives.Count > 0 && layers.Givers, e = pc.Ends.Count > 0 && layers.Enders;
            if (!g && !e) continue;
            string glyph = g && e ? "!?" : g ? "!" : "?";
            var qs = pc.Gives.Concat(pc.Ends).Select(id => m.Quests[id]).ToList();
            bool inFocus = focus.Contains(QuestBoard.Key(pc.Kind, pc.Entry));
            bool faded = dimOthers && !inFocus;
            Vector3 colour;
            if (layers.LevelTint)
            {
                var src = pc.Gives.Count > 0 ? pc.Gives : pc.Ends;
                int lv = Math.Max(lvLo, src.Min(id => m.Quests[id].Level));
                float t = lvHi > lvLo ? (lv - lvLo) / (float)(lvHi - lvLo) : 0f;
                colour = QuestBoard.Hsl((1f - t) * 0.33f * 360f, 0.75f, 0.55f);
            }
            else colour = qs.Any(q => q.Custom) ? QaCustom : qs.Any(q => q.Home) ? QaGold : QaVisitor;
            float lift = postBase;
            if (layers.LevelLift) lift += Math.Max(0, qs.Max(q => q.Level) - lvLo) * levelStep;
            _qaPins.Add(new QaPin(pc.Kind, pc.Entry, pc.Place, pc.Gives, pc.Ends, glyph, colour, inFocus, faded, lift, layers.Labels || inFocus));
        }

        // Chain arcs: from where you hand the earlier quest in to where the next one starts.
        if (layers.Chains)
        {
            var selChain = selQ is not null ? m.ChainOf.GetValueOrDefault(selQ.Id) : null;
            foreach (var edge in m.Edges)
            {
                if (!m.Quests.TryGetValue(edge.From, out var a) || !m.Quests.TryGetValue(edge.To, out var b)) continue;
                if (!_qaVisible.Contains(a.Id) || !_qaVisible.Contains(b.Id)) continue;
                var pa = m.EnderPlace(a) ?? m.GiverPlace(a);
                var pb = m.GiverPlace(b);
                if (pa is null || pb is null) continue;
                var chain = m.ChainOf.GetValueOrDefault(b.Id);
                bool strong = selQ is null || ReferenceEquals(chain, selChain);
                var colour = chain?.Colour ?? QuestBoard.Hex(0xd9c7a0);
                bool loop = Vector2.Distance(new(pa.Value.X, pa.Value.Y), new(pb.Value.X, pb.Value.Y)) < 5f;
                _qaArcs.Add(new QaArc(pa.Value, pb.Value, colour, strong ? 0.95f : 0.18f, edge.Breadcrumb, strong && selQ is not null ? 1.6f : 1f, loop, !edge.Breadcrumb));
            }
        }

        // Objectives: where the work is (the selected quest's always, larger).
        var want = new Dictionary<string, bool>
        {
            ["kill"] = layers.Kill, ["collect"] = layers.Collect, ["object"] = layers.Objects, ["explore"] = layers.Explore,
        };
        var seen = new HashSet<(string, float, float)>();
        foreach (uint id in _qaVisible)
        {
            if (selQ is not null && id == selQ.Id) continue;
            foreach (var t in m.ObjectiveTargets(m.Quests[id]))
            {
                if (!want.GetValueOrDefault(t.Kind)) continue;
                if (t.Trigger is { } tr) { _qaRings.Add(new QaRing(tr, false)); continue; }
                foreach (var p in t.Points)
                    if (seen.Add((t.Kind, p.X, p.Y))) _qaDots.Add(new QaDot(p, QaKindColour[t.Kind], selQ is not null ? 5f : 7f, selQ is not null ? 0.35f : 0.85f));
            }
        }
        if (selQ is not null)
        {
            foreach (var t in m.ObjectiveTargets(selQ))
            {
                if (t.Trigger is { } tr) { _qaRings.Add(new QaRing(tr, true)); continue; }
                foreach (var p in t.Points) _qaDots.Add(new QaDot(p, QaKindColour[t.Kind], 11f, 1f));
                if (QuestBoard.Centroid(t.Points) is { } c) _qaLabels.Add(new QaLabel(t.Label, c, postBase * 0.6f, false, true));
            }
            // Journey: giver → each objective → ender (dashed warm white).
            if (layers.Journey)
            {
                var white = QuestBoard.Hex(0xfff4d6);
                var g = m.GiverPlace(selQ);
                var stops = m.ObjectiveTargets(selQ).Select(t => QuestBoard.Centroid(t.Points)).Where(c => c is not null).Select(c => c!.Value).ToList();
                Vector3? prev = g;
                foreach (var s in stops)
                {
                    if (prev is { } p0 && Vector3.Distance(p0, s) > 5f) _qaArcs.Add(new QaArc(p0, s, white, 0.9f, true, 1.3f, false, false));
                    prev = s;
                }
                if (prev is { } pl && m.EnderPlace(selQ) is { } en && Vector3.Distance(pl, en) > 5f)
                    _qaArcs.Add(new QaArc(pl, en, white, 0.9f, true, 1.3f, false, false));
            }
        }
    }

    // ── drawing ─────────────────────────────────────────────────────────────

    /// <summary>A world point as shown: on the exaggerated ground (or above it at its own height) plus an unexaggerated lift.</summary>
    private Vector3 QaShown(Vector3 world, float lift = 0f)
    {
        if (_tabletop is null) return world;
        float ground = TtGroundAt(world.X, world.Y) ?? _tabletop.HeightAt(world.X, world.Y);
        float h = MathF.Max(world.Z, ground);
        return new Vector3(world.X, world.Y, TabletopLaw.Exaggerate(h, _tabletop.BaseZ, _tabletop.Exaggeration) + lift);
    }

    /// <summary>Hidden behind the shown relief, seen from the tabletop camera?</summary>
    private bool QaOccluded(Vector3 shown)
    {
        if (_tabletop is null) return false;
        var eye = _ttCamera.Position;
        var d = eye - shown;
        for (int i = 1; i <= 24; i++)
        {
            var p = shown + d * (i / 25f);
            if (_tabletop.Board is { } b && !b.Contains(p.X, p.Y)) break;
            if (p.Z < _tabletop.ShownHeightAt(p.X, p.Y) - 1f) return true;
        }
        return false;
    }

    private float QaFocalPx(Vector2 display) => display.Y * 0.5f / MathF.Tan(TabletopLaw.FovDegrees * MathF.PI / 360f);

    private bool QaProject(Vector3 shown, Vector2 display, out Vector2 px) => _ttCamera.TryProjectToScreen(shown, display, out px, out _);

    private float QaDepth(Vector3 shown) => MathF.Max(1f, Vector3.Dot(shown - _ttCamera.Position, Vector3.Normalize((_ttCamera.AuthoredTarget ?? shown) - _ttCamera.Position)));

    private static uint QaCol(Vector3 c, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(c, Math.Clamp(a, 0f, 1f)));

    /// <summary>Overlays over the 3D table (behind every panel): arcs, rings, dots, posts and pins, labels.</summary>
    private void QaDrawOverlays()
    {
        _qaPinRects.Clear();
        if (_qaBoard is null || _tabletop?.Board is null) return;
        var io = ImGui.GetIO();
        var display = io.DisplaySize;
        var dl = ImGui.GetBackgroundDrawList();
        float ui = MathF.Max(1f, display.Y / 1080f);
        float focal = QaFocalPx(display);
        float span = QaSpan, postBase = QaPostBase;

        // Rings (explore): on the ground, at the trigger radius.
        foreach (var ring in _qaRings)
        {
            var t = ring.Trigger;
            float r = MathF.Max(MathF.Max(t.Radius, t.BoxX > 0 ? MathF.Max(t.BoxX, t.BoxY) / 2f : 0f), 8f);
            var pts = new Vector2[48];
            int n = 0;
            for (int i = 0; i < 48; i++)
            {
                float a = i / 48f * MathF.Tau;
                var w = new Vector3(t.X + MathF.Cos(a) * r, t.Y + MathF.Sin(a) * r, t.Z);
                if (QaProject(QaShown(w, 3f), display, out var px)) pts[n++] = px;
            }
            if (n > 2)
            {
                var c = QaKindColour["explore"];
                float thick = MathF.Max(2f * ui, 0.18f * r * focal / QaDepth(QaShown(new Vector3(t.X, t.Y, t.Z), 3f)));
                dl.AddPolyline(ref pts[0], n, QaCol(c, ring.Strong ? 0.95f : 0.55f), ImDrawFlags.Closed, MathF.Min(thick, 14f * ui));
            }
        }

        // Arcs (chains, journey); same-NPC loops come after the pins so the glyph never hides them.
        foreach (var arc in _qaArcs) if (!arc.Loop) QaDrawArc(dl, arc, display, focal, span, postBase, ui);

        // Objective dots.
        foreach (var d in _qaDots)
        {
            var s = QaShown(d.At, 2f);
            if (!QaProject(s, display, out var px) || QaOccluded(s)) continue;
            float rad = d.Px * ui * 0.5f;
            dl.AddCircleFilled(px, rad, QaCol(d.Colour, d.Opacity));
            dl.AddCircle(px, rad * 0.8f, QaCol(Vector3.Zero, 0.6f * d.Opacity), 0, MathF.Max(1f, rad * 0.16f));
        }

        // Posts and pins: unfocused first so the focus draws on top.
        var font = ImGui.GetFont();
        float pinScale = TabletopLaw.PinScale(_ttRadius, span);
        foreach (var pin in _qaPins.OrderBy(p => p.InFocus))
        {
            var foot = QaShown(pin.Place);
            var head = foot + new Vector3(0f, 0f, pin.Lift);
            if (!QaProject(foot, display, out var footPx) || !QaProject(head, display, out var headPx) || QaOccluded(head)) continue;
            var col = pin.Faded ? pin.Colour * 0.35f : pin.Colour;
            dl.AddLine(footPx, headPx, QaCol(col, 0.85f), MathF.Max(1.5f, 1.5f * ui));
            // three.js sprite, sizeAttenuation off: scale 0.034 (focus 0.05) of the view's projected height.
            float spriteH = (pin.InFocus ? 0.05f : 0.034f) * focal * pinScale;   // scale · proj[1][1] · H / 2, eased by zoom
            float size = spriteH * 0.81f;
            var textSize = QaTextSize(pin.Glyph, size);
            var topLeft = headPx - new Vector2(textSize.X * 0.5f, textSize.Y * 0.85f);
            float alpha = pin.Faded ? 0.28f : 1f;
            float o = MathF.Max(1.5f, size * 0.06f);
            uint dark = QaCol(new Vector3(0.08f, 0.05f, 0.02f), 0.95f * alpha);
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                dl.AddText(font, size, topLeft + new Vector2(MathF.Cos(a), MathF.Sin(a)) * o, dark, pin.Glyph);
            }
            dl.AddText(font, size, topLeft, QaCol(pin.Colour, alpha), pin.Glyph);
            _qaPinRects.Add((pin, topLeft, topLeft + textSize));
            if (pin.Label) QaDrawLabel(dl, _qaBoard.EntityName(pin.Kind, pin.Entry), headPx - new Vector2(0f, textSize.Y * 0.9f), pin.Faded, false, ui);
        }

        // Same NPC hands you the next step: a small loop above them (above the glyph; several side by side, centred).
        // A busy NPC would otherwise grow a long row (owner, 2026-10-21): at most MaxLoopsPerPin, the strongest
        // (selected chain) first, the rest counted as "+N".
        var perPin = new Dictionary<int, List<QaArc>>();
        foreach (var arc in _qaArcs)
        {
            if (!arc.Loop) continue;
            int at = _qaPinRects.FindIndex(r => Vector2.Distance(new(r.Pin.Place.X, r.Pin.Place.Y), new(arc.A.X, arc.A.Y)) < 5f);
            if (at < 0) continue;
            if (!perPin.TryGetValue(at, out var list)) perPin[at] = list = new();
            list.Add(arc);
        }
        foreach (var (at, loops) in perPin)
        {
            var rect = _qaPinRects[at];
            var shown = loops.OrderByDescending(a => a.Opacity).ThenByDescending(a => a.Thick).Take(TabletopLaw.MaxLoopsPerPin).ToList();
            int extra = loops.Count - shown.Count;
            float r = MathF.Max(7f * ui, (rect.Max.Y - rect.Min.Y) * 0.12f);
            float step = r * 2.4f;
            var mid = new Vector2((rect.Min.X + rect.Max.X) * 0.5f, rect.Min.Y - r - 3f * ui);
            float first = -(shown.Count - 1) * 0.5f * step;
            for (int k = 0; k < shown.Count; k++)
                dl.AddCircle(mid + new Vector2(first + k * step, 0f), r, QaCol(shown[k].Colour, shown[k].Opacity), 24, MathF.Max(2f * ui, r * 0.22f));
            if (extra > 0)
            {
                string more = $"+{extra}";
                float size = 12f * ui;
                var at2 = mid + new Vector2(first + shown.Count * step - r * 0.6f, -QaTextSize(more, size).Y * 0.5f);
                dl.AddText(font, size, at2 + new Vector2(1f, 1f) * ui, QaCol(Vector3.Zero, 0.9f), more);
                dl.AddText(font, size, at2, QaCol(QuestBoard.Hex(0xfff7e0), 0.95f), more);
            }
        }

        // Objective labels.
        foreach (var l in _qaLabels)
        {
            var s = QaShown(l.At, l.Lift);
            if (QaProject(s, display, out var px)) QaDrawLabel(dl, l.Text, px, l.Faded, l.Boxed, ui);
        }
    }

    /// <summary>Text size at an arbitrary font size (measured at the current size and scaled).</summary>
    private static Vector2 QaTextSize(string text, float size) => ImGui.CalcTextSize(text) * (size / MathF.Max(ImGui.GetFontSize(), 1f));

    private static void QaDrawLabel(ImDrawListPtr dl, string text, Vector2 anchor, bool faded, bool boxed, float ui)
    {
        var font = ImGui.GetFont();
        float size = 13f * ui;
        var ts = QaTextSize(text, size);
        var pos = anchor - new Vector2(ts.X * 0.5f, ts.Y + (boxed ? 6f : 18f) * ui);
        float a = faded ? 0.4f : 1f;
        if (boxed)
        {
            dl.AddRectFilled(pos - new Vector2(6f, 2f) * ui, pos + ts + new Vector2(6f, 2f) * ui, QaCol(new Vector3(28, 23, 18) / 255f, 0.72f * a), 4f * ui);
            dl.AddRect(pos - new Vector2(6f, 2f) * ui, pos + ts + new Vector2(6f, 2f) * ui, QaCol(new Vector3(0.94f, 0.9f, 0.82f), 0.18f * a), 4f * ui);
            dl.AddText(font, size, pos, QaCol(QuestBoard.Hex(0xffe2b8), a), text);
            return;
        }
        dl.AddText(font, size, pos + new Vector2(1f, 1f) * ui, QaCol(Vector3.Zero, 0.9f * a), text);
        dl.AddText(font, size, pos, QaCol(QuestBoard.Hex(0xfff7e0), a), text);
    }

    private void QaDrawArc(ImDrawListPtr dl, QaArc arc, Vector2 display, float focal, float span, float postBase, float ui)
    {
        var A = QaShown(arc.A, postBase);
        var B = QaShown(arc.B, postBase);
        uint col = QaCol(arc.Colour, arc.Opacity);
        float rYd = MathF.Max(0.6f, span * 0.0005f) * arc.Thick;   // web: max(0.06, span·S·0.0005) scene units
        float Px(Vector3 at) => MathF.Max(1.5f * ui, 2f * rYd * focal / QaDepth(at));
        if (arc.Loop)
        {
            // Same NPC hands you the next step: a small loop above them.
            var c = A + new Vector3(0f, 0f, postBase * 0.5f);
            if (!QaProject(c, display, out var cp)) return;
            float rad = postBase * 0.2f * focal / QaDepth(c);
            dl.AddCircle(cp, MathF.Max(rad, 6f * ui), col, 24, MathF.Max(1.5f * ui, postBase * 0.05f * focal / QaDepth(c)));
            return;
        }
        float d = Vector3.Distance(A, B);
        var mid = (A + B) * 0.5f;
        mid.Z = MathF.Max(A.Z, B.Z) + d * 0.28f + 20f;
        const int Steps = 32;
        var pts = new Vector2[Steps + 1];
        var world = new Vector3[Steps + 1];
        bool ok = true;
        for (int i = 0; i <= Steps; i++)
        {
            world[i] = QuestBoard.Bezier(A, mid, B, i / (float)Steps);
            ok &= QaProject(world[i], display, out pts[i]);
        }
        if (!ok) return;
        if (arc.Dashed)
        {
            int dashes = Math.Max(6, (int)MathF.Round(d / 60f));
            for (int k = 0; k < dashes; k += 2)
            {
                int i0 = k * Steps / dashes, i1 = Math.Min(Steps, (k + 1) * Steps / dashes);
                if (i1 > i0) dl.AddPolyline(ref pts[i0], i1 - i0 + 1, col, ImDrawFlags.None, Px(world[(i0 + i1) / 2]));
            }
            return;
        }
        dl.AddPolyline(ref pts[0], Steps + 1, col, ImDrawFlags.None, Px(world[Steps / 2]));
        if (!arc.Arrow) return;
        // Arrowhead at the receiver: a cone r·2.4 wide, r·6 long, along the curve's end.
        var tip3 = world[Steps];
        var dir = Vector3.Normalize(tip3 - QuestBoard.Bezier(A, mid, B, 0.94f));
        var back3 = tip3 - dir * rYd * 6f;
        if (!QaProject(back3, display, out var back)) return;
        var tip = pts[Steps];
        var along = tip - back;
        if (along.LengthSquared() < 1e-3f) return;
        var side = Vector2.Normalize(new Vector2(-along.Y, along.X)) * MathF.Max(3f * ui, rYd * 2.4f * focal / QaDepth(tip3));
        dl.AddTriangleFilled(tip, back + side, back - side, col);
    }

    // ── picking + hover ─────────────────────────────────────────────────────

    /// <summary>A click on the table: a pin (one quest → select it, several → the NPC card), an explore ring, or a creature.</summary>
    private bool QaPick(Vector2 mouse)
    {
        if (LiveHit(mouse) is { } liveBot) { LiveSelect(liveBot); return true; }
        if (_qaBoard is not { } m) return false;
        if (QaHit(mouse) is not { } hit) return false;
        switch (hit)
        {
            case QaPin pin:
            {
                var qs = pin.Gives.Concat(pin.Ends).Distinct().ToList();
                if (qs.Count == 1) QaSelectQuest(qs[0]); else QaSelectEntity(pin.Kind, pin.Entry);
                return true;
            }
            case Q.Trigger trig:
            {
                var q = m.Quests.Values.FirstOrDefault(q => q.Objectives.Any(o => o.Kind == "explore" && o.Id == trig.Id));
                if (q is not null) QaSelectQuest(q.Id);
                return true;
            }
            case WorldEntity unit:
            {
                if (LiveBotOf(unit) is { } viewBot) { LiveSelect(viewBot); return true; }
                string k = QuestBoard.Key("npc", unit.Entry);
                if (m.Gives.ContainsKey(k) || m.Ends.ContainsKey(k)) QaSelectEntity("npc", unit.Entry);
                else { _qaSelQuest = null; _qaSelEntity = null; _qaSelFigure = (unit.Entry, unit.Position); QaRebuild(); }
                return true;
            }
        }
        return false;
    }

    private object? QaHit(Vector2 mouse)
    {
        for (int i = _qaPinRects.Count - 1; i >= 0; i--)
            if (mouse.X >= _qaPinRects[i].Min.X && mouse.X <= _qaPinRects[i].Max.X && mouse.Y >= _qaPinRects[i].Min.Y && mouse.Y <= _qaPinRects[i].Max.Y)
                return _qaPinRects[i].Pin;
        if (LiveHit(mouse) is { } liveBot) return liveBot;
        var display = ImGui.GetIO().DisplaySize;
        float focal = QaFocalPx(display);
        foreach (var ring in _qaRings)
        {
            var t = ring.Trigger;
            var c = QaShown(new Vector3(t.X, t.Y, t.Z), 3f);
            if (!QaProject(c, display, out var cp)) continue;
            float r = MathF.Max(MathF.Max(t.Radius, MathF.Max(t.BoxX, t.BoxY) / 2f), 8f) * focal / QaDepth(c);
            if (Vector2.Distance(cp, mouse) <= MathF.Max(r, 10f)) return t;
        }
        float ui = MathF.Max(1f, display.Y / 1080f);
        WorldEntity? best = null;
        float bestD = 18f * ui;
        foreach (var unit in _ttUnitsThisFrame)
        {
            if (!QaProject(unit.Position + new Vector3(0f, 0f, 1.2f), display, out var px)) continue;
            float d = Vector2.Distance(px, mouse);
            if (d < bestD) { bestD = d; best = unit; }
        }
        return best;
    }

    private void QaDrawHover()
    {
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || _ttDragButton >= 0) return;
        if (LiveHit(io.MousePos) is { } liveBot)
        {
            PushCreatorStyle();
            ImGui.BeginTooltip();
            LiveTooltip(liveBot);
            ImGui.EndTooltip();
            PopCreatorStyle();
            return;
        }
        if (_qaBoard is not { } m) return;
        var hit = QaHit(io.MousePos);
        if (hit is null) return;
        PushCreatorStyle();
        ImGui.BeginTooltip();
        switch (hit)
        {
            case QaPin pin:
            {
                var npc = pin.Kind == "npc" ? m.Npc(pin.Entry) : null;
                ImGui.TextColored(AtlasGold, m.EntityName(pin.Kind, pin.Entry) + (string.IsNullOrEmpty(npc?.Subname) ? "" : $" <{npc!.Subname}>"));
                var parts = new List<string>();
                if (pin.Gives.Count > 0) parts.Add($"gives {pin.Gives.Count}");
                if (pin.Ends.Count > 0) parts.Add($"takes {pin.Ends.Count}");
                ImGui.TextDisabled(string.Join(" · ", parts) + (pin.Gives.Count == 1 ? " - " + m.Quests[pin.Gives.First()].Title : ""));
                break;
            }
            case Q.Trigger t:
                ImGui.TextColored(AtlasGold, string.IsNullOrEmpty(t.Name) ? "Area trigger" : t.Name);
                ImGui.TextDisabled($"explore · trigger {t.Id}");
                break;
            case WorldEntity unit:
            {
                var c = TabletopCreatureInfo(unit.Entry);
                int wanted = m.QuestsWanting(unit.Entry).Count;
                ImGui.TextColored(AtlasGold, (c.Name ?? $"creature {unit.Entry}") + (string.IsNullOrEmpty(c.SubName) ? "" : $" <{c.SubName}>"));
                ImGui.TextDisabled($"Lv {QaLevels(c.LevelMin, c.LevelMax)}{QaRank(c.Rank)}" + (wanted > 0 ? $" · wanted by {wanted} quest{(wanted > 1 ? "s" : "")}" : ""));
                break;
            }
        }
        ImGui.EndTooltip();
        PopCreatorStyle();
    }

    private static string QaLevels(int lo, int hi) => hi > lo ? $"{lo}-{hi}" : $"{lo}";
    private static string QaRank(byte rank) => rank switch { 1 => " · elite", 2 => " · rare elite", 3 => " · boss", 4 => " · rare", _ => "" };
    private static string QaLv(int level) => level > 0 ? level.ToString(CultureInfo.InvariantCulture) : "·";

    // ── left panel: zones, filters, list ────────────────────────────────────

    private string _qaZoneFilter = "";

    private void QaDrawZonePicker()
    {
        if (_qaZones is not { Count: > 0 } zones) { DrawTabletopZonePicker(); if (_qaZonesStatus.Length > 0) ImGui.TextDisabled(_qaZonesStatus); return; }
        var font = ImGui.GetFont();
        bool star;
        unsafe { star = font.FindGlyphNoFallback(0x2726).NativePtr != null; }   // ✦ for our own zones, when the font has it
        string Label(Q.ZoneSummary z) => $"{z.Name} · {z.MinLevel}-{z.MaxLevel} · {z.Quests}q" + (z.AreaId >= 7000 ? star ? "  ✦" : "  (ours)" : "");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##qa-zone", _ttZoneName.Length > 0 ? _ttZoneName : "Choose a zone"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##qa-zone-filter", "search zones", ref _qaZoneFilter, 64);
            foreach (var (title, map) in new[] { ("Eastern Kingdoms", 0), ("Kalimdor", 1), ("Elsewhere", -1) })
            {
                var group = zones.Where(z => map >= 0 ? z.MapId == map : z.MapId > 1)
                    .Where(z => _qaZoneFilter.Length == 0 || z.Name.Contains(_qaZoneFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (group.Count == 0) continue;
                ImGui.TextDisabled(title);
                foreach (var z in group)
                    if (ImGui.Selectable($"{Label(z)}##qa-zone-{z.AreaId}", z.AreaId == _ttAreaId)) TabletopLoadZone(z.AreaId);
            }
            ImGui.EndCombo();
        }
    }

    private void QaDrawFiltersAndList()
    {
        ImGui.TextDisabled(_qaStatus);
        if (_qaBoard is not { } m) return;
        bool changed = false;
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint("##qa-search", "search quests, NPCs, items", ref _qaSearch, 96);
        foreach (var (key, label) in new[] { ("all", "All"), ("alliance", "Alliance"), ("horde", "Horde") })
        {
            if (key != "all") ImGui.SameLine();
            bool on = _qaFaction == key;
            if (on) { ImGui.PushStyleColor(ImGuiCol.Header, AtlasA(AtlasGold, 0.16f)); ImGui.PushStyleColor(ImGuiCol.Text, AtlasGold); }
            var size = ImGui.CalcTextSize(label) + new Vector2(18f, 6f) * CreatorUiScale;
            if (ImGui.Selectable(label + "##qa-fa", on, ImGuiSelectableFlags.None, size)) { _qaFaction = key; changed = true; }
            if (on) ImGui.PopStyleColor(2);
        }
        ImGui.TextDisabled("Levels");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.45f);
        if (ImGui.InputInt("##qa-lvmin", ref _qaLvMin, 0)) { _qaLvMin = Math.Clamp(_qaLvMin, 1, 70); changed = true; }
        ImGui.SameLine();
        ImGui.TextDisabled("to");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.45f);
        if (ImGui.InputInt("##qa-lvmax", ref _qaLvMax, 0)) { _qaLvMax = Math.Clamp(_qaLvMax, 1, 70); changed = true; }
        changed |= ImGui.Checkbox("Filed in this zone only", ref _qaHomeOnly);
        changed |= ImGui.Checkbox("Class and race quests", ref _qaClassQuests);
        if (changed) _qaDirty = true;

        ImGui.Separator();
        ImGui.TextDisabled($"{_qaVisible.Count} of {m.Quests.Count} quests");
        if (_qaSelQuest is not null || _qaSelEntity is not null || _qaSelFigure is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear selection")) { QaClearSelection(); TabletopOverview(); }
        }
        if (ImGui.BeginChild("##qa-list", new Vector2(0f, MathF.Max(ImGui.GetContentRegionAvail().Y - CreatorRowHeight * 2.4f, 120f))))
        {
            var dl = ImGui.GetWindowDrawList();
            foreach (var chain in m.Chains)
            {
                var ids = chain.Ids.Where(_qaVisible.Contains).ToList();
                if (ids.Count == 0) continue;
                if (chain.Multi)
                {
                    var p = ImGui.GetCursorScreenPos();
                    float h = ImGui.GetTextLineHeight();
                    dl.AddCircleFilled(p + new Vector2(h * 0.35f, h * 0.55f), h * 0.28f, QaCol(chain.Colour, 1f));
                    ImGui.SetCursorScreenPos(p + new Vector2(h * 0.9f, 0f));
                    ImGui.TextDisabled($"{m.Quests[chain.Ids[0]].Title} · {chain.Ids.Count} steps");
                    ImGui.Indent(h * 0.9f);
                }
                foreach (uint id in ids)
                {
                    var q = m.Quests[id];
                    string tags = (q.Custom ? "  [ours]" : "") + (QuestBoard.IsClassOrRace(q) ? "  [class/race]" : "");
                    if (!q.Home) ImGui.PushStyleColor(ImGuiCol.Text, AtlasInkDim);
                    bool sel = _qaSelQuest == id;
                    if (ImGui.Selectable($"{QaLv(q.Level),2}   {q.Title}{tags}##qa-q-{id}", sel)) QaSelectQuest(id);
                    if (sel && _qaScrollToSelection) { ImGui.SetScrollHereY(0.3f); _qaScrollToSelection = false; }
                    if (!q.Home) ImGui.PopStyleColor();
                }
                if (chain.Multi) ImGui.Unindent(ImGui.GetTextLineHeight() * 0.9f);
            }
            if (_qaVisible.Count == 0) ImGui.TextDisabled("Nothing matches these filters.");
        }
        ImGui.EndChild();
    }

    // ── layers popover (top right) ──────────────────────────────────────────

    private void QaDrawLayersPopover()
    {
        var io = ImGui.GetIO();
        float s = CreatorPixelScale;
        float cardW = QaCardOpen ? QaCardWidth + 14f * s : 0f;
        var anchor = new Vector2(io.DisplaySize.X - 14f * s - cardW, CreatorSidebarTop);
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, new Vector2(1f, 0f));
        PushCreatorStyle();
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##qa-layers", flags))
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            dl.AddRectFilled(min, min + ImGui.GetWindowSize(), AtlasU(new Vector4(28 / 255f, 23 / 255f, 18 / 255f, AtlasPanelAlpha)), 10f * s);
            dl.AddRect(min, min + ImGui.GetWindowSize(), AtlasU(AtlasInk, 0.12f), 10f * s);
            if (CreatorButton(_qaLayersOpen ? "Layers  ^" : "Layers  v")) _qaLayersOpen = !_qaLayersOpen;
            if (_qaLayersOpen)
            {
                var t = Settings.Creator.Tabletop;
                bool save = false;
                void Group(string title) { ImGui.Spacing(); ImGui.TextDisabled(title.ToUpperInvariant()); }
                void Layer(string label, Func<bool> get, Action<bool> set, uint swatch = 0)
                {
                    bool v = get();
                    if (swatch != 0)
                    {
                        var p = ImGui.GetCursorScreenPos();
                        float h = ImGui.GetFrameHeight();
                        ImGui.GetWindowDrawList().AddCircleFilled(p + new Vector2(h + 10f * s, h * 0.5f), h * 0.18f, QaCol(QuestBoard.Hex(swatch), 1f));
                    }
                    if (ImGui.Checkbox((swatch != 0 ? "      " : "") + label, ref v)) { set(v); save = true; _qaDirty = true; }
                }
                Group("People");
                Layer("Quest givers  !", () => t.Givers, v => t.Givers = v, 0xf2c14e);
                Layer("Turn-ins  ?", () => t.Enders, v => t.Enders = v, 0xf2c14e);
                Layer("Names", () => t.Labels, v => t.Labels = v);
                Group("Story");
                Layer("Chain arcs", () => t.Chains, v => t.Chains = v, 0x7fb8e8);
                Layer("Selected quest journey", () => t.Journey, v => t.Journey = v, 0xfff4d6);
                Group("Objectives");
                Layer("Kill targets", () => t.Kill, v => t.Kill = v, 0xe0614f);
                Layer("Item sources", () => t.Collect, v => t.Collect = v, 0xe8a33a);
                Layer("Objects to use", () => t.Objects, v => t.Objects = v, 0x6fb3e0);
                Layer("Places to explore", () => t.Explore, v => t.Explore = v, 0xb48be0);
                Group("Level");
                Layer("Tint by level", () => t.LevelTint, v => t.LevelTint = v);
                Layer("Lift by level (height = level)", () => t.LevelLift, v => t.LevelLift = v);
                Group("World");
                Layer("Buildings, props and ground detail", () => t.World, v => t.World = v);
                Layer("Creatures", () => t.Creatures, v => t.Creatures = v);
                Layer("Critters (rabbits, rats, deer)", () => t.Critters, v => t.Critters = v);
                float fig = t.FigureScale;
                ImGui.SetNextItemWidth(180f * s);
                if (ImGui.SliderFloat("Figures", ref fig, TabletopLaw.MinFigureScale, TabletopLaw.MaxFigureScale, "%.1fx")) t.FigureScale = fig;
                save |= ImGui.IsItemDeactivatedAfterEdit();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"True scale within {TabletopLaw.FigureTrueYards:0} yd of the camera target, this much larger by {TabletopLaw.FigureFullYards:0} yd (now {_ttFigureScaleShown:0.0}x).");
                Group("Live");
                Layer("Live bots (where they are now)", () => t.LiveBots, v => t.LiveBots = v, 0xABD473);
                Layer("Trails (last minute)", () => t.LiveTrails, v => t.LiveTrails = v);
                Group("Table");
                Layer("Water", () => t.Water, v => t.Water = v);
                Layer("Miniature lens (tilt-shift)", () => t.Miniature, v => t.Miniature = v);
                Layer("Relief eases close up (crisper ground)", () => t.ReliefEasesClose, v => t.ReliefEasesClose = v);
                float k = t.Exaggeration;
                ImGui.SetNextItemWidth(180f * s);
                if (ImGui.SliderFloat("Relief", ref k, TabletopLaw.MinExaggeration, TabletopLaw.MaxExaggeration, "%.1fx")) t.Exaggeration = k;
                save |= ImGui.IsItemDeactivatedAfterEdit();
                if (save) SettingsFile?.Save();
            }
        }
        ImGui.End();
        PopCreatorStyle();
    }

    // ── compact card (right) ────────────────────────────────────────────────

    private bool QaCardOpen => _liveSel is { } liveSel && _live.ContainsKey(liveSel)
        || _qaBoard is not null && (_qaSelQuest is not null || _qaSelEntity is not null || _qaSelFigure is not null);
    /// <summary>The web card's 420 px at 1080p, scaled with the display (never more than a quarter of the screen).</summary>
    private float QaCardWidth => MathF.Min(420f * MathF.Max(ImGui.GetIO().DisplaySize.Y / 1080f, 1f), ImGui.GetIO().DisplaySize.X * 0.25f);

    private void QaDrawCard()
    {
        if (!QaCardOpen) return;
        var m = _qaBoard;
        var liveBot = _liveSel is { } liveSel && _live.TryGetValue(liveSel, out var lb) ? lb : null;
        var io = ImGui.GetIO();
        float s = CreatorPixelScale;
        float top = CreatorSidebarTop;
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - 14f * s, top), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowSize(new Vector2(QaCardWidth, io.DisplaySize.Y - top - 14f * s), ImGuiCond.Always);
        PushCreatorStyle();
        if (ImGui.Begin("##qa-card", CreatorChromeFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings))
        {
            string title = liveBot is not null ? liveBot.Name
                : m is null ? ""
                : _qaSelQuest is { } qid && m.Quests.TryGetValue(qid, out var tq) ? tq.Title
                : _qaSelEntity is { } e ? m.EntityName(e.Split(':')[0], uint.Parse(e.Split(':')[1], CultureInfo.InvariantCulture))
                : "Creature";
            if (DrawCreatorPanelChrome(title, null)) QaClearSelection();
            BeginCreatorContent();
            if (liveBot is not null) LiveCard(liveBot);
            else if (m is null) { }
            else if (_qaSelQuest is { } id && m.Quests.TryGetValue(id, out var q)) QaQuestCard(m, q);
            else if (_qaSelEntity is { } ent) QaEntityCard(m, ent);
            else if (_qaSelFigure is { } fig) QaFigureCard(m, fig.Entry, fig.At);
            EndCreatorContent();
        }
        ImGui.End();
        PopCreatorStyle();
    }

    private void QaBadges(IEnumerable<(string Text, Vector4 Colour)> badges)
    {
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        bool first = true;
        foreach (var (text, colour) in badges)
        {
            var size = ImGui.CalcTextSize(text) + new Vector2(12f, 4f) * CreatorUiScale;
            if (!first && ImGui.GetItemRectMax().X + 6f * CreatorUiScale + size.X < right) ImGui.SameLine(0f, 6f * CreatorUiScale);
            var p = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRect(p, p + size, AtlasU(colour, 0.4f), 4f * CreatorUiScale);
            dl.AddText(p + new Vector2(6f, 2f) * CreatorUiScale, AtlasU(colour), text);
            ImGui.Dummy(size);
            first = false;
        }
    }

    /// <summary>A clickable entity name: on the board → select it; off the board → dimmed.</summary>
    private void QaEntityLink(QuestBoard m, string kind, uint entry)
    {
        bool on = m.SpawnsOnBoard(kind, entry).Count > 0;
        ImGui.PushStyleColor(ImGuiCol.Text, on ? AtlasInk : AtlasInkDim);
        if (ImGui.Selectable($"{m.EntityName(kind, entry)}##qa-ent-{kind}-{entry}-{ImGui.GetCursorPosY():0}", false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(m.EntityName(kind, entry))) && on)
            QaSelectEntity(kind, entry);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(on ? "Show on the table" : "Not on this board");
    }

    /// <summary>A clickable quest: on the board → select; an off-board stub → load its zone and select it there.</summary>
    private void QaQuestLink(QuestBoard m, uint id)
    {
        if (m.Quests.TryGetValue(id, out var q))
        {
            if (ImGui.Selectable($"{q.Title}##qa-ql-{id}-{ImGui.GetCursorPosY():0}", false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(q.Title))) QaSelectQuest(id);
            return;
        }
        if (m.Stubs.TryGetValue(id, out var stub))
        {
            string text = $"{stub.Title} ({stub.ZoneName ?? "elsewhere"})";
            ImGui.PushStyleColor(ImGuiCol.Text, AtlasInkDim);
            if (ImGui.Selectable($"{text}##qa-qs-{id}", false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(text)) && stub.TopZone != 0)
            {
                _qaPendingSelect = id;
                TabletopLoadZone(stub.TopZone);
            }
            ImGui.PopStyleColor();
            return;
        }
        ImGui.TextDisabled($"#{id}");
    }

    private static string QaText(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        // $N/$C/$R → ‹name›/‹class›/‹race›, $Gmale:female; → male/female, $B → line break (§6).
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\$[Gg]\s*([^:;]*):([^;]*);", m => $"{m.Groups[1].Value.Trim()}/{m.Groups[2].Value.Trim()}");
        return t.Replace("$N", "‹name›").Replace("$n", "‹name›").Replace("$C", "‹class›").Replace("$c", "‹class›")
                .Replace("$R", "‹race›").Replace("$r", "‹race›").Replace("$B", "\n").Replace("$b", "\n");
    }

    private static string QaMoney(int c)
    {
        int g = c / 10000, s = c / 100 % 100, k = c % 100;
        return string.Join(" ", new[] { g > 0 ? $"{g}g" : null, s > 0 ? $"{s}s" : null, k > 0 ? $"{k}c" : null }.Where(x => x is not null));
    }

    private void QaSection(string title)
    {
        ImGui.Spacing();
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        AtlasSmallCaps(dl, p, title, AtlasInkFaint, ImGui.GetFontSize() * 0.82f);
        ImGui.Dummy(new Vector2(1f, ImGui.GetFontSize()));
    }

    private void QaQuestCard(QuestBoard m, Q.Quest q)
    {
        ImGui.TextDisabled($"Quest {q.Id}{(q.Patch > 0 ? $" · patch row {q.Patch}" : "")} · {(q.Home ? m.Bundle.Zone.Name : q.ZoneOrSort < 0 ? $"sort {q.ZoneOrSort}" : $"filed under area {q.ZoneOrSort}")}");
        var badges = new List<(string, Vector4)> { (q.Level > 0 ? $"Lv {q.Level}" : "scales to player", AtlasInkDim), ($"req {q.MinLevel}", AtlasInkDim) };
        var fa = QuestBoard.Faction(q);
        if (fa != "both") badges.Add((fa == "alliance" ? "Alliance" : "Horde", AtlasInkDim));
        if (q.Classes != 0) badges.Add(("class quest", AtlasInkDim));
        else if (QuestBoard.IsClassOrRace(q)) badges.Add(("race quest", AtlasInkDim));
        if (q.Type == 1) badges.Add(("group", AtlasInkDim));
        if (q.Type == 81) badges.Add(("dungeon", AtlasInkDim));
        if (q.Type == 62) badges.Add(("raid", AtlasInkDim));
        if (q.SuggestedPlayers > 1) badges.Add(($"{q.SuggestedPlayers} players", AtlasInkDim));
        if ((q.SpecialFlags & 1) != 0) badges.Add(("repeatable", AtlasInkDim));
        if (q.LimitTime > 0) badges.Add(($"timed {Math.Round(q.LimitTime / 60.0)}m", AtlasInkDim));
        if (!q.Home) badges.Add(("filed elsewhere", AtlasInkDim));
        if (q.Custom) badges.Add(("ours", new Vector4(0.71f, 0.55f, 0.88f, 1f)));
        if (q.Givers.Count == 0) badges.Add(("no giver", AtlasAmber));
        if (q.Enders.Count == 0) badges.Add(("no turn-in", AtlasAmber));
        QaBadges(badges);

        QaSection("From");
        if (q.Givers.Count == 0) ImGui.TextDisabled("-");
        foreach (var r in q.Givers) QaEntityLink(m, r.Kind, r.Entry);
        if (q.SrcItem != 0) ImGui.TextDisabled($"starts from item {m.Item(q.SrcItem)?.Name ?? q.SrcItem.ToString()}");
        QaSection("To");
        if (q.Enders.Count == 0) ImGui.TextDisabled("-");
        foreach (var r in q.Enders) QaEntityLink(m, r.Kind, r.Entry);

        var before = m.Edges.Where(e => e.To == q.Id).ToList();
        var after = m.Edges.Where(e => e.From == q.Id).ToList();
        if (before.Count > 0 || after.Count > 0 || q.ExclusiveGroup != 0)
        {
            QaSection("Chain");
            foreach (var e in before) { ImGui.TextDisabled(e.Breadcrumb ? "before (breadcrumb)" : "before"); ImGui.SameLine(); QaQuestLink(m, e.From); }
            ImGui.TextColored(AtlasGold, "this");
            foreach (var e in after) { ImGui.TextDisabled(e.Breadcrumb ? "after (breadcrumb)" : "after"); ImGui.SameLine(); QaQuestLink(m, e.To); }
            if (q.ExclusiveGroup != 0)
            {
                ImGui.TextDisabled($"{(q.ExclusiveGroup > 0 ? "Only one of" : "All of")} group {Math.Abs(q.ExclusiveGroup)}:");
                var mates = m.Quests.Values.Where(o => o.Id != q.Id && o.ExclusiveGroup == q.ExclusiveGroup).Select(o => o.Id)
                    .Concat(m.Stubs.Values.Where(o => o.Id != q.Id && o.ExclusiveGroup == q.ExclusiveGroup).Select(o => o.Id)).ToList();
                if (mates.Count == 0) ImGui.TextDisabled("(none on this board)");
                foreach (var id in mates) QaQuestLink(m, id);
            }
        }

        QaSection("Objectives");
        if (q.Objectives.Count == 0) ImGui.TextDisabled("No tracked objectives - a talk-to or delivery quest.");
        foreach (var o in q.Objectives)
        {
            switch (o.Kind)
            {
                case "kill":
                    ImGui.TextWrapped($"{(o.Spell != 0 ? "Use spell on" : "Slay")} {o.Count}× {m.EntityName("npc", o.Id)}");
                    ImGui.TextDisabled($"   {m.SpawnsOnBoard("npc", o.Id).Count} on this board");
                    break;
                case "object":
                    ImGui.TextWrapped($"Use {(o.Count > 1 ? o.Count + "× " : "")}{m.EntityName("object", o.Id)}");
                    ImGui.TextDisabled($"   {m.SpawnsOnBoard("object", o.Id).Count} on this board");
                    break;
                case "item":
                {
                    var it = m.Item(o.Id);
                    ImGui.TextWrapped($"Collect {o.Count}× {it?.Name ?? "#" + o.Id}");
                    var src = (it?.Sources ?? new()).Take(6).Select(s => $"{m.EntityName(s.Kind, s.Entry)} {s.Chance:0.#}%").ToList();
                    ImGui.PushTextWrapPos();
                    ImGui.TextDisabled(src.Count > 0 ? "   from " + string.Join(", ", src) + (it!.Sources.Count > 6 ? $" +{it.Sources.Count - 6} more" : "")
                                                     : "   no drop source found (vendor, crafted or scripted)");
                    ImGui.PopTextWrapPos();
                    break;
                }
                case "explore":
                {
                    var t = m.Trigger(o.Id);
                    ImGui.TextWrapped($"Explore {(string.IsNullOrEmpty(t?.Name) ? "area trigger " + o.Id : t!.Name)}");
                    if (t is not null) ImGui.TextDisabled($"   trigger {t.Id} · radius {Math.Round(t.Radius > 0 ? t.Radius : MathF.Max(t.BoxX, t.BoxY) / 2f)} yd");
                    break;
                }
                case "reputation":
                    ImGui.TextWrapped($"Reach {o.Count} reputation with faction {o.Id}");
                    break;
            }
            if (!string.IsNullOrWhiteSpace(o.Text)) { ImGui.PushTextWrapPos(); ImGui.TextDisabled($"   \"{QaText(o.Text)}\""); ImGui.PopTextWrapPos(); }
        }
        if (q.SrcSpell != 0) ImGui.TextDisabled($"Casts spell {q.SrcSpell} on accept");

        var rw = q.Rewards;
        string ItemName(Q.ItemCount ic) => $"{m.Item(ic.Id)?.Name ?? "#" + ic.Id}{(ic.Count > 1 ? $" ×{ic.Count}" : "")}";
        if (rw.Choice.Count > 0) { QaSection("Choose one"); foreach (var ic in rw.Choice) ImGui.TextWrapped(ItemName(ic)); }
        if (rw.Items.Count > 0) { QaSection("You also receive"); foreach (var ic in rw.Items) ImGui.TextWrapped(ItemName(ic)); }
        var misc = new List<string>();
        if (rw.Money > 0) misc.Add(QaMoney(rw.Money));
        if (rw.Money < 0) misc.Add("costs " + QaMoney(-rw.Money));
        if (rw.Xp > 0) misc.Add($"{rw.Xp} xp");
        if (rw.Spell != 0) misc.Add($"teaches spell {rw.Spell}");
        if (rw.SpellCast != 0) misc.Add($"casts spell {rw.SpellCast}");
        if (misc.Count > 0) { QaSection("Also"); ImGui.TextWrapped(string.Join(" · ", misc)); }
        if (rw.Rep.Count > 0) { QaSection("Reputation"); foreach (var r in rw.Rep) ImGui.TextDisabled($"faction {r.Faction}: {(r.Value > 0 ? "+" : "")}{r.Value}"); }

        ImGui.Spacing();
        ImGui.Separator();
        if (CreatorButton("Open in web")) QaOpenInWeb(q.Id);
        ImGui.SameLine();
        ImGui.TextDisabled("story, progress, completion and voices");
    }

    private void QaEntityCard(QuestBoard m, string key)
    {
        var parts = key.Split(':');
        string kind = parts[0];
        uint entry = uint.Parse(parts[1], CultureInfo.InvariantCulture);
        var npc = kind == "npc" ? m.Npc(entry) : null;
        int spawns = npc?.SpawnTotal ?? m.Bundle.Objects.GetValueOrDefault(entry.ToString())?.SpawnTotal ?? 0;
        ImGui.TextDisabled($"{(kind == "npc" ? "NPC" : "Object")} {entry}{(spawns > 0 ? $" · {spawns} spawn{(spawns > 1 ? "s" : "")}" : "")}");
        var badges = new List<(string, Vector4)>();
        if (!string.IsNullOrEmpty(npc?.Subname)) badges.Add(($"<{npc!.Subname}>", AtlasInkDim));
        if (npc is { LevelMin: > 0 }) badges.Add(($"Lv {QaLevels(npc.LevelMin, npc.LevelMax)}", AtlasInkDim));
        if (entry >= 7_000_000) badges.Add(("ours", new Vector4(0.71f, 0.55f, 0.88f, 1f)));
        if (badges.Count > 0) QaBadges(badges);
        if (m.Bundle.Greetings.FirstOrDefault(g => g.Kind == kind && g.Entry == entry) is { Text: { Length: > 0 } greet })
        {
            QaSection("Greeting");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(QaText(greet));
            ImGui.PopTextWrapPos();
        }
        void List(string title, List<uint>? ids)
        {
            if (ids is not { Count: > 0 }) return;
            QaSection(title);
            foreach (var q in ids.Select(i => m.Quests[i]).OrderBy(q => q.Level))
                if (ImGui.Selectable($"{QaLv(q.Level),2}   {q.Title}##qa-ec-{title}-{q.Id}")) QaSelectQuest(q.Id);
        }
        List("Gives", m.Gives.GetValueOrDefault(key));
        List("Takes in", m.Ends.GetValueOrDefault(key));
    }

    private void QaFigureCard(QuestBoard m, uint entry, Vector3 at)
    {
        var c = TabletopCreatureInfo(entry);
        ImGui.TextDisabled($"Creature {entry}{QaRank(c.Rank)}");
        ImGui.TextColored(AtlasGold, c.Name ?? $"creature {entry}");
        var badges = new List<(string, Vector4)>();
        if (!string.IsNullOrEmpty(c.SubName)) badges.Add(($"<{c.SubName}>", AtlasInkDim));
        if (c.LevelMin > 0) badges.Add(($"Lv {QaLevels(c.LevelMin, c.LevelMax)}", AtlasInkDim));
        if (entry >= 7_000_000) badges.Add(("ours", new Vector4(0.71f, 0.55f, 0.88f, 1f)));
        if (badges.Count > 0) QaBadges(badges);
        var wanted = m.QuestsWanting(entry);
        QaSection("Wanted by");
        if (wanted.Count == 0) ImGui.TextDisabled("No quest on this board asks for this creature.");
        foreach (var (q, why) in wanted)
            if (ImGui.Selectable($"{QaLv(q.Level),2}   {q.Title} - {why}##qa-fw-{q.Id}")) QaSelectQuest(q.Id);
        QaSection("Standing at");
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"x {at.X:0.0} · y {at.Y:0.0} · z {at.Z:0.0}"));
    }

    /// <summary>§3.4: the full web card ({web}/QuestAtlas#zone=&lt;areaId&gt;&amp;quest=&lt;questId&gt;) in the system browser.</summary>
    private void QaOpenInWeb(uint questId)
    {
        if (string.IsNullOrWhiteSpace(SuiWebAppUrl)) return;
        string url = $"{SuiWebAppUrl.TrimEnd('/')}/QuestAtlas#zone={_ttAreaId}&quest={questId}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Console.WriteLine($"[tabletop] could not open {url}: {e.Message}"); }
    }
}
