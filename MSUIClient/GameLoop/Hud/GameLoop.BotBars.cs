using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ── Layered bot skillbars ─────────────────────────────────────────────────────────────────
    // Bots run on rotations server-side and own no action buttons, so the client builds their
    // bars from three layers (most specific slot wins):
    //   1. generated baseline — the bot's known ACTIVE spells, highest rank per name, laid onto
    //      the main bar (wire 0-11) then the bottom-left bar (wire 48-59);
    //   2. class customization — a persisted sparse slot map applied to every bot of the class;
    //   3. per-bot override — a persisted sparse slot map for one named bot.
    // Persisted in botbars.json at the repo root, together with a per-bot spell cache so the
    // free view can show bars for bots that are not currently possessed. An explicit 0 in a
    // layer masks the layer below (a deliberately emptied slot stays empty).

    private sealed class BotBarsDocument
    {
        public Dictionary<string, Dictionary<string, uint>> ClassSlots { get; set; } = [];
        public Dictionary<string, Dictionary<string, uint>> BotSlots { get; set; } = [];
        public Dictionary<string, List<uint>> BotSpells { get; set; } = [];
        public Dictionary<string, string> BotClasses { get; set; } = [];
        public Dictionary<string, string> BotRoles { get; set; } = [];   // Tank / Healer / DPS
        public Dictionary<string, bool> BotLinks { get; set; } = [];     // chain membership (absent = linked)
        // Quick-slot AI policy per bot: "slotIndex" -> By tactics / Emergency only / Never.
        // Client-authored today; the Phase B rotation integration is the consumer.
        public Dictionary<string, Dictionary<string, string>> BotSlotPolicies { get; set; } = [];
    }

    private BotBarsDocument? _botBars;
    private bool _botBarSaveToClass;   // banner toggle: edits land on the class layer instead

    private string BotBarsPath => Path.Combine(_config.RepoRoot, "botbars.json");

    private BotBarsDocument LoadBotBars()
    {
        if (_botBars is not null) return _botBars;
        try
        {
            if (File.Exists(BotBarsPath))
                _botBars = JsonSerializer.Deserialize<BotBarsDocument>(File.ReadAllText(BotBarsPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[botbars] load failed ({ex.Message}); starting fresh");
        }
        return _botBars ??= new BotBarsDocument();
    }

    private void SaveBotBars()
    {
        try
        {
            File.WriteAllText(BotBarsPath, JsonSerializer.Serialize(LoadBotBars(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[botbars] save failed: {ex.Message}");
        }
    }

    private static string ClassIdName(byte classId) => classId switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "",
    };

    private string BotClassName(ulong guid, string name)
    {
        if (_entities.TryGet(guid, out WorldEntity entity) && entity.IsPlayer)
        {
            (_, byte classId, _, _) = entity.Fields.Bytes0;
            string resolved = ClassIdName(classId);
            if (resolved.Length != 0) return resolved;
        }
        return name.Length != 0 ? LoadBotBars().BotClasses.GetValueOrDefault(name, "") : "";
    }

    /// <summary>
    /// A party/raid member's WoW class colour (ABGR) for portrait borders (issue #15).
    /// Resolves the class from the streamed entity, then cached player traits, then the
    /// bot-bars class map by name; falls back to neutral grey when the class is unknown.
    /// Works for your own character too (its entity carries the class).
    /// </summary>
    private uint ClassColorForGuid(ulong guid, string name = "")
    {
        byte classId = 0;
        if (_entities.TryGet(guid, out WorldEntity entity) && entity.IsPlayer)
            classId = entity.Fields.Bytes0.Class;
        else if (_playerTraits.TryGetValue(guid, out PlayerTraits traits))
            classId = traits.Class;
        string className = classId != 0 ? ClassIdName(classId) : "";
        if (className.Length == 0 && name.Length != 0)
            className = LoadBotBars().BotClasses.GetValueOrDefault(name, "");
        return CommanderClassColor(className);
    }

    /// <summary>Re-alpha an ABGR colour (top byte) — for the subtle/glow class borders.</summary>
    private static uint WithAlphaByte(uint abgr, byte alpha) =>
        (abgr & 0x00ff_ffffu) | ((uint)alpha << 24);

    /// <summary>Whether class-colored portrait borders draw right now (issue #15). The CRPG/RTS
    /// commander view (free view) and direct-control play are toggled independently in the
    /// interface options ("Class Portrait Borders"): off by default in direct control, on in RTS.
    /// </summary>
    private bool ClassPortraitBordersOn() => _freeView
        ? Settings.Controls.PortraitBordersRts
        : Settings.Controls.PortraitBordersDirectControl;

    /// <summary>The plain dark rim a portrait wore before issue #15, drawn when class borders
    /// are toggled off so the surface reverts to its original look.</summary>
    private static void DrawNeutralPortraitRim(ImDrawListPtr dl, Vector2 min, Vector2 max,
        float scale) => dl.AddRect(min, max, 0xff2a343d, 0f, ImDrawFlags.None, MathF.Max(1f, scale));

    /// <summary>
    /// The class-colour frame for a SQUARE card portrait (issue #15): a soft outer glow plus a
    /// ~50%-opacity class border, replacing the flat dark rim. <paramref name="guid"/> resolves
    /// the class; unknown classes get the neutral grey, so the rim never vanishes.
    /// </summary>
    private void DrawClassPortraitBorderRect(ImDrawListPtr dl, Vector2 min, Vector2 max,
        ulong guid, float scale, string name = "")
    {
        if (!ClassPortraitBordersOn()) { DrawNeutralPortraitRim(dl, min, max, scale); return; }
        uint cls = ClassColorForGuid(guid, name);
        float glowW = MathF.Max(1f, 2f * scale);
        dl.AddRect(min - new Vector2(glowW), max + new Vector2(glowW),
            WithAlphaByte(cls, 0x33), 0f, ImDrawFlags.None, glowW);
        dl.AddRect(min, max, WithAlphaByte(cls, 0x88), 0f, ImDrawFlags.None,
            MathF.Max(1f, 1.5f * scale));
    }

    /// <summary>
    /// The class-colour ring for a ROUND portrait (issue #15): a soft outer glow and a ~50%
    /// class ring hugging the disc, drawn AFTER the gilded frame art so it reads on the ring.
    /// </summary>
    private void DrawClassPortraitRing(ImDrawListPtr dl, Vector2 center, float radius,
        ulong guid, float scale, string name = "")
    {
        if (!ClassPortraitBordersOn()) return;   // gilded frame ring stays, no class tint
        uint cls = ClassColorForGuid(guid, name);
        float thick = MathF.Max(1.5f, 2f * scale);
        dl.AddCircle(center, radius + thick, WithAlphaByte(cls, 0x2E), 48, thick * 1.6f);
        dl.AddCircle(center, radius, WithAlphaByte(cls, 0x82), 48, thick);
    }

    /// <summary>
    /// The ANIMATED class-colour border for the PRIMARY mini portrait (issue #15): four class dots
    /// chase clockwise around the rim, each with a little fading trail — an RTS "this one is
    /// selected" marker. A static class rim sits underneath so the frame is always defined.
    /// </summary>
    private void DrawAnimatedClassPortraitBorder(ImDrawListPtr dl, Vector2 min, Vector2 max,
        ulong guid, float scale, string name = "")
    {
        if (!ClassPortraitBordersOn()) { DrawNeutralPortraitRim(dl, min, max, scale); return; }
        uint cls = ClassColorForGuid(guid, name);
        dl.AddRect(min, max, WithAlphaByte(cls, 0x66), 0f, ImDrawFlags.None,
            MathF.Max(1f, 1.5f * scale));
        float head = ((float)NowSeconds() * 0.30f) % 1f;   // one lap ~3.3s, clockwise
        float dot = MathF.Max(1.8f, 2.2f * scale);
        const int trail = 5;
        for (int d = 0; d < 4; d++)                         // four dots, a quarter-lap apart
        {
            float baseF = head + d * 0.25f;
            for (int k = 0; k < trail; k++)                 // fading trail behind each dot
            {
                float f = (((baseF - k * 0.02f) % 1f) + 1f) % 1f;
                byte a = (byte)(0xE6 * (1f - k / (float)trail));
                dl.AddCircleFilled(PointOnRectPerimeter(min, max, f),
                    dot * (1f - 0.45f * k / trail), WithAlphaByte(cls, a));
            }
        }
    }

    /// <summary>Point at fraction <paramref name="f"/> (0..1) clockwise around a rect's perimeter,
    /// starting at the top-left corner.</summary>
    private static Vector2 PointOnRectPerimeter(Vector2 min, Vector2 max, float f)
    {
        float w = max.X - min.X, h = max.Y - min.Y;
        float d = Math.Clamp(f, 0f, 1f) * 2f * (w + h);
        if (d < w) return new Vector2(min.X + d, min.Y);
        d -= w;
        if (d < h) return new Vector2(max.X, min.Y + d);
        d -= h;
        if (d < w) return new Vector2(max.X - d, max.Y);
        d -= w;
        return new Vector2(min.X, max.Y - d);
    }

    private static int ParseSpellRank(string rank)
    {
        int space = rank.LastIndexOf(' ');
        return space >= 0 && int.TryParse(rank[(space + 1)..], out int value) ? value : 0;
    }

    /// <summary>Resolve the three layers into a full 120-slot bar.</summary>
    private uint[] ResolveBotBar(string className, string botName, IReadOnlySet<uint> knownSpells)
    {
        var bar = new uint[120];

        // Layer 1: generated baseline — highest rank per spell name, active spells only.
        if (_spellCatalog is not null)
        {
            var bestByName = new Dictionary<string, (uint Id, int Rank)>();
            foreach (uint id in knownSpells.OrderBy(v => v))
            {
                if (!_spellCatalog.TryGet(id, out SpellInfo info)) continue;
                if (info.Passive || !info.InSpellbook || info.Name.Length == 0) continue;
                int rank = ParseSpellRank(info.Rank);
                if (!bestByName.TryGetValue(info.Name, out (uint Id, int Rank) seen) ||
                    rank > seen.Rank || (rank == seen.Rank && id > seen.Id))
                    bestByName[info.Name] = (id, rank);
            }
            List<uint> picks = [.. bestByName.Values.Select(v => v.Id).OrderBy(v => v)];
            for (int i = 0; i < picks.Count && i < 24; i++)
                bar[i < 12 ? i : 48 + (i - 12)] = picks[i];
        }

        // Layer 2 + 3: persisted sparse overrides (an explicit 0 empties the slot).
        BotBarsDocument doc = LoadBotBars();
        void ApplyLayer(Dictionary<string, uint>? layer)
        {
            if (layer is null) return;
            foreach ((string key, uint spell) in layer)
                if (int.TryParse(key, out int slot) && slot is >= 0 and < 120)
                    bar[slot] = spell;
        }
        if (className.Length != 0) ApplyLayer(doc.ClassSlots.GetValueOrDefault(className));
        if (botName.Length != 0) ApplyLayer(doc.BotSlots.GetValueOrDefault(botName));
        return bar;
    }

    /// <summary>
    /// Fill a bot's action store from the layered bars and cache its spellbook + class for
    /// later free-view resolution. Called when the possession proxy delivers the spellbook
    /// (and after SMSG_ACTION_BUTTONS clears a bar the server has no content for).
    /// </summary>
    private void PopulateBotBar(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return;
        PlayerActions store = ActionsFor(guid);
        string name = ResolveUnitName(guid);
        string className = BotClassName(guid, name);

        if (name.Length != 0 && store.KnownSpells.Count > 0)
        {
            BotBarsDocument doc = LoadBotBars();
            doc.BotSpells[name] = [.. store.KnownSpells.OrderBy(v => v)];
            if (className.Length != 0) doc.BotClasses[name] = className;
            SaveBotBars();
        }

        uint[] bar = ResolveBotBar(className, name, store.KnownSpells);
        for (int slot = 0; slot < bar.Length; slot++)
            store.Set(slot, bar[slot] != 0
                ? new ActionSlot((byte)(bar[slot] >> 24), bar[slot] & 0x00ff_ffffu)
                : null);
    }

    /// <summary>
    /// Free-view selection wants this bot's bars without possessing it: seed the store from
    /// the persisted spell cache (recorded during any earlier possession) and resolve.
    /// </summary>
    private void EnsureBotBarForViewing(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return;
        PlayerActions store = ActionsFor(guid);
        if (store.KnownSpells.Count == 0)
        {
            string name = ResolveUnitName(guid);
            if (name.Length != 0 &&
                LoadBotBars().BotSpells.TryGetValue(name, out List<uint>? cached))
                store.SeedSpells(cached);
        }
        if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
            PopulateBotBar(guid);
    }

    // ── RTS/CRPG command strips beside the party portraits ────────────────────────────────────
    // Toggled by Settings.Controls.RtsCommands. Role feeds the rotation work later; Hold and
    // Patrol ride the existing CMSG_SUI_ORDER codes (2 stop / 4 patrol-loop).

    private static string RoleGlyph(string role) => role switch
    {
        "Tank" => "T", "Healer" => "H", _ => "D",
    };

    /// <summary>Is this member part of the chain? Server truth when the roster carries it
    /// (row v2); the saved per-name intent is only the fallback against an older core.</summary>
    private bool PartyMemberLinked(PartyMember member) =>
        _suiChain.TryGetValue(member.Guid, out (byte State, ulong Anchor) chain)
            ? chain.State != SuiChainUnlinked
            : LoadBotBars().BotLinks.GetValueOrDefault(member.Name, true);

    private bool PartyMemberLinked(string name) =>
        LoadBotBars().BotLinks.GetValueOrDefault(name, true);

    /// <summary>The member's chain state as the server reports it (linked when unknown).</summary>
    private byte PartyChainState(PartyMember member) =>
        _suiChain.TryGetValue(member.Guid, out (byte State, ulong Anchor) chain)
            ? chain.State
            : PartyMemberLinked(member.Name) ? SuiChainLinked : SuiChainUnlinked;

    private ulong PartyChainAnchor(PartyMember member) =>
        _suiChain.TryGetValue(member.Guid, out (byte State, ulong Anchor) chain) ? chain.Anchor : 0;

    /// <summary>
    /// Divinity-style chain toggle: linked members follow whoever is being driven
    /// (ORDER_LINK, server-side formation gate); an unlinked member stands its ground.
    /// </summary>
    private void SetPartyLink(PartyMember member, bool linked)
    {
        if (!TrySendLiveSuiOrder(6, [member.Guid], 0, linked ? 1f : 0f, 0, 0)) return;
        // Chaining is the driven body's gesture ("Follow me!"); unchaining is the
        // member's acknowledgement that it will stand its ground.
        if (linked) PlayCompanionEmoteVoice(ControlledGuid, CompanionVoiceLaw.EmoteFollowMe);
        else PlayCompanionEmoteVoice(member.Guid, CompanionVoiceLaw.EmoteYes);
        BotBarsDocument doc = LoadBotBars();
        doc.BotLinks[member.Name] = linked;
        SaveBotBars();
        AddChatMessage(linked
            ? $"{member.Name} chained back into the party."
            : $"{member.Name} unchained — standing ground until re-linked.");
    }

    /// <summary>ORDER_FOLLOW: chain this member to a specific anchor (0 = the default, the
    /// driven body of its human). The server stores it on the unit and the roster reports it.</summary>
    private void SetPartyChainAnchor(PartyMember member, ulong anchor)
    {
        if (!TrySendLiveSuiOrder(5, [member.Guid], anchor, 0, 0, 0)) return;
        AddChatMessage(anchor != 0
            ? $"{member.Name}: chained to {ResolveUnitName(anchor)}."
            : $"{member.Name}: chained to the party.");
    }

    /// <summary>Pixels between the pressed portrait's centre and the cursor.</summary>
    private float PartyDragDistance(int index)
    {
        float scale = GameplayUiScale();
        Vector2 from = (PartyMemberLogicalOrigin(index) + new Vector2(47, 15)) * scale;
        return (ImGui.GetIO().MousePos - from).Length();
    }

    private bool MouseOverPlayerFrame()
    {
        float scale = GameplayUiScale();
        Vector2 mouse = ImGui.GetIO().MousePos;
        Vector2 min = _playerFrameOrigin * scale;       // the player frame's resolved origin
        Vector2 size = new Vector2(232, 100) * scale;
        return mouse.X >= min.X && mouse.X <= min.X + size.X &&
               mouse.Y >= min.Y && mouse.Y <= min.Y + size.Y;
    }

    // ── Order-state chips ─────────────────────────────────────────────────────
    // The wire has no per-order server acknowledgement yet (Phase B), so the
    // chip is the client's own record of the last order each companion was
    // given — the same optimism as the move markers, kept visible.
    private readonly Dictionary<ulong, string> _rtsOrderChips = [];

    /// <summary>Every companion order funnels here: stamp the visible order
    /// chip for each subject, then play the voice acknowledgement.</summary>
    private void NoteCompanionOrder(byte orderType, IReadOnlyList<ulong> subjects)
    {
        string? chip = orderType switch
        {
            0 => "moving", 1 => "attacking", 2 => "holding", 3 => "on waypoints",
            4 => "patrolling", 5 => "regrouping", 8 => "forming line",
            9 => "forming circle", _ => null,
        };
        foreach (ulong guid in subjects)
        {
            if (orderType == 12) _rtsOrderChips.Remove(guid);   // mustered out
            else if (chip is not null) _rtsOrderChips[guid] = chip;
        }
        PlayCompanionOrderVoice(orderType, subjects);
    }

    /// <summary>
    /// Corner-anchored role medallion, fully on the frame (the leader-crown
    /// precedent): a filled disc at the portrait's lower-right corner carrying
    /// shape, color AND letter, never color alone.
    /// </summary>
    private void DrawPartyRoleMedallion(int index, string role)
    {
        float s = GameplayUiScale();
        Vector2 center = (PartyMemberLogicalOrigin(index) + new Vector2(44, 43)) * s;
        DrawRoleMedallion(ImGui.GetForegroundDrawList(), center, 8f * s, role, s);
    }

    /// <summary>The medallion disc itself — shape, color AND letter, never
    /// color alone. Shared by the party frames and the commander console.</summary>
    private static void DrawRoleMedallion(ImDrawListPtr dl, Vector2 center,
        float radius, string role, float scale)
    {
        uint fill = role switch
        {
            "Tank" => 0xffcc7e4a,     // steel blue
            "Healer" => 0xff57a73f,   // green
            _ => 0xff4044c7,          // red
        };
        dl.AddCircleFilled(center, radius, fill);
        dl.AddCircle(center, radius, 0xff10181f, 0, MathF.Max(1f, 1.5f * scale));
        string glyph = RoleGlyph(role);
        Vector2 half = ImGui.CalcTextSize(glyph) * 0.5f;
        dl.AddText(center - half + new Vector2(1f, 1f) * scale, 0xd0000000, glyph);
        dl.AddText(center - half, 0xffffffff, glyph);
    }

    private void DrawRtsCommandStrips(PartyMember[] members)
    {
        if (!Settings.Controls.RtsCommands || _net is null) return;
        float scale = GameplayUiScale();
        BotBarsDocument doc = LoadBotBars();
        PreparedSharedSpellTooltip? tooltip = null;
        for (int i = 0; i < members.Length; i++)
        {
            PartyMember member = members[i];
            string role = doc.BotRoles.GetValueOrDefault(member.Name, "DPS");
            DrawPartyRoleMedallion(i, role);

            Vector2 pos = (PartyMemberLogicalOrigin(i) +
                new Vector2(PartyFrameUiLaw.FrameWidth + 6, 4)) * scale;
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.35f);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
            if (!ImGui.Begin($"##rts-strip-{i}", flags)) { ImGui.End(); continue; }
            _skin?.DrawBackdrop(ImGui.GetWindowDrawList(), ImGui.GetWindowPos(),
                ImGui.GetWindowPos() + ImGui.GetWindowSize(), WowSkin.Tooltip);

            if (ImGui.SmallButton($"{RoleGlyph(role)}##role{i}"))
            {
                doc.BotRoles[member.Name] = role switch
                {
                    "DPS" => "Tank",
                    "Tank" => "Healer",
                    _ => "DPS",
                };
                SaveBotBars();
            }
            if (ImGui.IsItemHovered())
                HoverTip($"Preferred role: {role} — click to cycle. Client-side\n" +
                    "until the server accepts an effective role (Phase B).");
            ImGui.SameLine();
            if (ImGui.SmallButton($"≡##tac{i}")) OpenPartyTactics(member.Guid);
            if (ImGui.IsItemHovered())
                HoverTip("Tactics: role, quick-slot AI policy, stances");

            DrawPartyQuickSlots(member, i, scale, ref tooltip);

            ImGui.SameLine();
            if (ImGui.SmallButton($"Hold##rts{i}"))
            {
                if (TrySendLiveSuiOrder(2, [member.Guid], 0, 0, 0, 0))
                {
                    NoteCompanionOrder(2, [member.Guid]);
                    AddChatMessage($"{member.Name}: stand your ground.");
                }
            }
            if (ImGui.IsItemHovered())
                HoverTip("Stand your ground: stop and hold this spot");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Patrol##rts{i}") &&
                _entities.TryGet(member.Guid, out WorldEntity unit))
            {
                if (TrySendLiveSuiOrder(4, [member.Guid], 0,
                        unit.Position.X, unit.Position.Y, unit.Position.Z))
                {
                    NoteCompanionOrder(4, [member.Guid]);
                    AddChatMessage($"{member.Name}: patrol the current route.");
                }
            }
            if (ImGui.IsItemHovered())
                HoverTip("Loop the waypoint chain (Shift+RightClick spots in free view\n" +
                    "with the bot selected first); its current spot closes the loop.\n" +
                    "Move or Hold cancels the patrol.");
            bool linked = PartyMemberLinked(member.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton(linked ? $"chained##rtsl{i}" : $"loose##rtsl{i}"))
                SetPartyLink(member, !linked);
            if (ImGui.IsItemHovered())
                HoverTip(linked
                    ? "Chained: follows whoever you drive. Click (or drag the portrait away) to unchain."
                    : "Unchained: stands its ground. Click (or drop the portrait on another) to re-chain.");

            // Status line: enlistment plus the last order this client gave.
            bool enlisted = RtsEnlisted(member.Guid);
            bool hasChip = _rtsOrderChips.TryGetValue(member.Guid, out string? chipText);
            if (enlisted || hasChip)
            {
                ImGui.TextDisabled(enlisted && hasChip
                    ? $"enlisted · {chipText}"
                    : enlisted ? "enlisted" : chipText!);
                if (enlisted && ImGui.IsItemHovered())
                    HoverTip("In a control group: the questing brain stands down\n" +
                        "for this bot until it is dismissed from every group.");
            }
            ImGui.End();
        }
        if (tooltip is { } prepared)
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner,
                () => DrawSpellTooltip(prepared.Snapshot));
    }

    /// <summary>
    /// The four quick-action slots (Drawing 1 of the party command design):
    /// truthful icon buttons from this bot's known bar, cooldown swipe when the
    /// client actually knows it, and an honest unknown state when it does not.
    /// Casting them needs the Phase B server hook — until then a click says so.
    /// </summary>
    private void DrawPartyQuickSlots(PartyMember member, int memberIndex, float scale,
        ref PreparedSharedSpellTooltip? tooltip)
    {
        PlayerActions store = ActionsFor(member.Guid);
        if (store.OccupiedCount == 0) EnsureBotBarForViewing(member.Guid);
        double now = NowSeconds();
        float size = 22f * scale;
        var side = new Vector2(size, size);
        int drawn = 0;
        for (int slot = 0; slot < 12 && drawn < 4; slot++)
        {
            // Kind 0x00 = spell (PlayerActions.ActionSlot packing).
            if (store[slot] is not ActionSlot action || action.Kind != 0 ||
                _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) != true)
                continue;
            drawn++;
            ImGui.SameLine();
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + side;
            ImGui.InvisibleButton($"##pqs-{memberIndex}-{slot}", side);
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            // Same law as the action bars: Attack renders the member's weapon,
            // never spell 6603's internal Temp-face DBC icon.
            uint icon = PainterlyArt(ResolveSpellActionIcon(spell,
                _entities.TryGet(member.Guid, out WorldEntity quickSlotOwner)
                    ? quickSlotOwner : null));
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            if (store.TryCooldownDisplay(action.ActionId, 0, spell, now,
                    out CooldownDisplay cooldown) && cooldown.SweepFraction is float sweep)
                DrawCooldownSwipe(dl, min, max, sweep);
            dl.AddRect(min, max, ImGui.IsItemHovered() ? 0xffd0b060 : 0xff30404d,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (QuickSlotPolicy(member.Name, slot) != "Never")
            {
                // The pet bar's autocast language: gold corners mean the AI may
                // use it on its own (policy set in the Tactics panel).
                uint autocast = _gameplayArt?.Handle(
                    @"Interface\Buttons\UI-AutoCastableOverlay") ?? 0;
                var over = new Vector2(6f, 6f) * scale;
                if (autocast != 0) dl.AddImage((nint)autocast, min - over, max + over);
            }
            if (ImGui.IsItemHovered())
                tooltip = PrepareSharedSpellTooltip(
                    new GameTooltipOwnerKey("party-quick", (ulong)(memberIndex * 16 + slot + 1)),
                    spell.Id, scale, SpellTooltipPlacement.DefaultBottomRight);
            if (ImGui.IsItemClicked())
                ShowUiError($"{member.Name}'s quick casts need the companion-cast server " +
                    "hook (Phase B) — Alt+click the bot in the free view to drive it directly.");
        }
        if (drawn > 0) return;

        // Honest unknown: four dim wells. The client has never seen this
        // companion's spells and refuses to invent them.
        for (int k = 0; k < 4; k++)
        {
            ImGui.SameLine();
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + side;
            ImGui.InvisibleButton($"##pqs-unknown-{memberIndex}-{k}", side);
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(min, max, 0x66141a20);
            dl.AddRect(min, max, 0xff2a3640, 0, ImDrawFlags.None, MathF.Max(1f, scale));
            Vector2 half = ImGui.CalcTextSize("?") * 0.5f;
            dl.AddText((min + max) * 0.5f - half, 0x88aabbcc, "?");
            if (ImGui.IsItemHovered())
                HoverTip(_partyMemberFactsAvailable
                    ? "Abilities unknown — the server syncs party spellbooks\n" +
                      "automatically; this companion's have not arrived yet."
                    : "Abilities unknown — possess this companion once to teach\n" +
                      "the client its spells; a member-facts server pushes them\n" +
                      "without possession.");
        }
    }

    /// <summary>
    /// The permanent Divinity-style chain: link beads run from the player frame down
    /// through every chained portrait; an unchained member shows a broken stub instead.
    /// Always visible with the party frames — this is party state, not an RTS-strip extra.
    /// </summary>
    /// <summary>
    /// The chain, redesigned (owner 2026-09-03: "something that fits the UI vibe that shows
    /// green or red chain, and also WHO that's chained to"). No rail, no beads: each member
    /// wears a CHAIN LINK glyph at the left edge of its portrait, drawn in the same style as
    /// the role medallion (dark rim, coloured body, shadow) — green = linked, red = unchained
    /// by you (holds until re-linked), amber = held by the world (re-forms when its anchor is
    /// back in range) — and a small medallion beside it carrying the ANCHOR's initial. Hover
    /// names it in words; click toggles the link. Server truth (roster row v2).
    /// </summary>
    private void DrawPartyChainLinks(ImDrawListPtr draw, int i)
    {
        // Owner 2026-09-22: drawn on the member frame's OWN window list, not the foreground list,
        // so the loot window and the master-loot panel cover the badges instead of the badges
        // bleeding through them; and smaller, docked at the right end of the bars (chain beside
        // the mana bar, the anchor's initial beside the health bar) instead of on the portrait.
        if (_partyChainRoster is not { } members || i < 0 || i >= members.Length) return;
        float scale = GameplayUiScale();
        PartyMember member = members[i];
        byte state = PartyChainState(member);
        ulong anchor = PartyChainAnchor(member);
        Vector2 center = (PartyMemberLogicalOrigin(i) + new Vector2(122.5f, 25f)) * scale;
        float radius = 3.6f * scale;
        DrawChainGlyph(draw, center, radius, state);
        string anchorName = anchor != 0 ? ResolveUnitName(anchor) : "";
        Vector2 medallion = (PartyMemberLogicalOrigin(i) + new Vector2(122.5f, 15.5f)) * scale;
        if (anchorName.Length > 0)
            DrawChainAnchorMedallion(draw, medallion, 4f * scale, anchorName, state, scale);

        Vector2 hitMin = new(medallion.X - 5f * scale, medallion.Y - 5f * scale);
        Vector2 hitMax = center + new Vector2(radius + 2f * scale, radius + 2f * scale);
        // The member window must be the hovered one: a click on a window lying over the badge
        // (loot, master loot) belongs to that window, never to the chain.
        bool hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(hitMin, hitMax, false);
        if (hovered)
        {
            string text = state switch
            {
                SuiChainLinked => anchorName.Length > 0 ? $"Chained to {anchorName}" : "Chained",
                SuiChainWorldHold => anchorName.Length > 0
                    ? $"Holding — waits for {anchorName} to come back in range"
                    : "Holding — waits for the party to come back in range",
                _ => "Unchained — stands its ground until re-linked",
            };
            OfferOwnerAnchoredSharedGameTooltip(new("party-chain", member.Guid),
                [new(text, GameTooltipTextTone.White),
                 new(state == SuiChainUnlinked ? "Click to re-link" : "Click to unchain",
                     GameTooltipTextTone.Gold)],
                hitMax, Vector2.Zero);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SetPartyLink(member, linked: state == SuiChainUnlinked);
        }
    }

    /// <summary>This frame's roster, for the per-member chain badges drawn inside each frame.</summary>
    private PartyMember[]? _partyChainRoster;

    /// <summary>The authored bordered chain badge shared by party frames and command cards.
    /// The roster state chooses the sprite; anchor identity stays in
    /// <see cref="DrawChainAnchorMedallion"/> beside it.</summary>
    private void DrawChainGlyph(ImDrawListPtr dl, Vector2 center, float radius, byte state)
    {
        uint texture = _gameplayArt?.EmbeddedPngHandle(
            PartyChainBadgeUiLaw.ResourceForState(state)) ?? 0;
        if (texture == 0) return; // EmbeddedPngHandle logs the first failure; the law check guards shipping.

        float side = PartyChainBadgeUiLaw.SideForRadius(radius);
        Vector2 half = new(side * 0.5f);
        dl.AddImage((nint)texture, center - half, center + half);
    }

    /// <summary>WHO: the anchor's initial on a medallion disc in the chain's colour.</summary>
    private static void DrawChainAnchorMedallion(ImDrawListPtr dl, Vector2 center, float radius,
        string anchorName, byte state, float scale)
    {
        dl.AddCircleFilled(center + new Vector2(1f, 1f) * scale, radius, 0x90000000);
        dl.AddCircleFilled(center, radius, SuiChainColor(state));
        dl.AddCircle(center, radius, 0xff10181f, 0, MathF.Max(1f, 1.5f * scale));
        string glyph = anchorName[..1].ToUpperInvariant();
        // FrizQt at the disc's size: 10 pt fills an 11 px disc, so the text scale follows the radius.
        GameText.DrawCentered(dl, "GameFontHighlightSmall", glyph, center, radius / 5.5f);
    }

    /// <summary>Dashed hint from the dragged portrait to the cursor while chaining a follow.</summary>
    private void DrawPartyDragFeedback(PartyMember[] members)
    {
        if (_partyPressIndex < 0 || _partyPressIndex >= members.Length ||
            _partyPressButton != PartyPointerButton.Left) return;
        float scale = GameplayUiScale();
        Vector2 from = (PartyMemberLogicalOrigin(_partyPressIndex) + new Vector2(47, 15)) * scale;
        Vector2 mouse = ImGui.GetIO().MousePos;
        if ((mouse - from).Length() < 20f * scale) return;
        var draw = ImGui.GetForegroundDrawList();
        DrawDashedLine(draw, from, mouse, 0xCC55D8F0, 8f, 6f);
        draw.AddCircleFilled(mouse, 4f, 0xFF55D8F0);
    }

    /// <summary>
    /// A bar edit while driving a bot: persist to the chosen layer (this bot, or its whole
    /// class) and keep the in-memory store as the resolved truth. Never touches the wire —
    /// CMSG_SET_ACTION_BUTTON would edit the SESSION character's server bar.
    /// </summary>
    private void SaveBotBarSlot(int slot, uint packed)
    {
        if (ControlledGuid == LocalPlayerGuid) return;
        string name = ResolveUnitName(ControlledGuid);
        if (name.Length == 0) return;
        string className = BotClassName(ControlledGuid, name);
        BotBarsDocument doc = LoadBotBars();
        string key = slot.ToString();
        if (_botBarSaveToClass && className.Length != 0)
        {
            doc.ClassSlots.TryGetValue(className, out Dictionary<string, uint>? classMap);
            (classMap ??= doc.ClassSlots[className] = []) [key] = packed;
            // A per-bot override on the same slot would mask the class edit just made.
            if (doc.BotSlots.TryGetValue(name, out Dictionary<string, uint>? botMap))
                botMap.Remove(key);
        }
        else
        {
            doc.BotSlots.TryGetValue(name, out Dictionary<string, uint>? botMap);
            (botMap ??= doc.BotSlots[name] = []) [key] = packed;
        }
        SaveBotBars();
    }
}
