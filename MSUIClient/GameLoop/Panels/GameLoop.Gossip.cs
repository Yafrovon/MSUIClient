using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint NpcGossip = WorldCursorUiLaw.Gossip;
    private const uint NpcQuestGiver = WorldCursorUiLaw.Questgiver;
    private const uint NpcVendor = WorldCursorUiLaw.Vendor;
    private const uint NpcFlightMaster = WorldCursorUiLaw.FlightMaster;
    private const uint NpcTrainer = WorldCursorUiLaw.Trainer;
    private const uint NpcInnkeeper = WorldCursorUiLaw.Innkeeper;
    private const uint NpcBanker = WorldCursorUiLaw.Banker;
    private const uint NpcAuctioneer = WorldCursorUiLaw.Auctioneer;
    private const uint NpcTabardDesigner = WorldCursorUiLaw.TabardDesigner;
    private const uint GossipNpcFlags = NpcGossip | NpcQuestGiver | NpcVendor |
        NpcFlightMaster | NpcTrainer | WorldCursorUiLaw.SpiritHealer |
        WorldCursorUiLaw.SpiritGuide | NpcInnkeeper | NpcBanker |
        WorldCursorUiLaw.Petitioner | NpcTabardDesigner | WorldCursorUiLaw.Battlemaster |
        NpcAuctioneer | WorldCursorUiLaw.StableMaster;
    private const float GossipInteractDistance = NpcSessionUiLaw.ServiceRange;

    private GossipMenu? _gossipMenu;
    private string? _gossipGreeting;
    private readonly Dictionary<uint, NpcText> _npcTextRecords = [];
    private uint _gossipSourceFlags;
    private float _gossipScroll;
    private GossipPoi? _gossipPoi;
    private uint _gossipPoiMapId;

    private void ResetGossip()
    {
        _gossipMenu = null;
        _gossipGreeting = null;
        _gossipSourceFlags = 0;
        _gossipScroll = 0;
    }

    private void ResetGossipPoi()
    {
        _gossipPoi = null;
        _gossipPoiMapId = 0;
    }

    private bool RequestGossip(ulong guid)
    {
        if (RefuseTacticalFreezeLiveCommand("opening gossip")) return false;
        if (RefuseTacticalFrozenActor(guid, "open gossip with it")) return false;
        string outcome;
        string detail;
        WorldEntity? target = null;
        float distance = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } ||
            !TryGetInteractionBodyPose(out WorldBodyPose sessionBody))
        {
            outcome = "REFUSED_NOT_IN_WORLD";
            detail = "inWorld=false";
        }
        else if (!_entities.TryGet(guid, out target) || !target.IsCreature)
        {
            outcome = "REFUSED_NOT_CREATURE";
            detail = "descriptorPresent=false";
        }
        else if (target.IsDead)
        {
            outcome = "REFUSED_DEAD";
            detail = $"health={target.Fields.Health}/{target.Fields.MaxHealth}";
        }
        else if ((target.NpcFlags & GossipNpcFlags) == 0)
        {
            outcome = "REFUSED_NO_SUPPORTED_NPC_FLAG";
            detail = $"npcFlags=0x{target.NpcFlags:X8}";
        }
        else if (!NpcSessionUiLaw.InRange(
                     Vector3.DistanceSquared(sessionBody.Position, target.Position)))
        {
            distance = Vector3.Distance(sessionBody.Position, target.Position);
            outcome = "REFUSED_RANGE";
            detail = $"distance={distance:R};limit={GossipInteractDistance:R};npcFlags=0x{target.NpcFlags:X8}";
        }
        else
        {
            bool sent = _net.GossipHello(guid);
            outcome = sent ? "SENT" : "SEND_FAILED";
            detail = $"distance={distance:R};npcFlags=0x{target.NpcFlags:X8};route={ClassifyGossipRoute(target.NpcFlags, "")}";
            if (sent)
            {
                _gossipMenu = null;
                _gossipGreeting = null;
                _gossipSourceFlags = target.NpcFlags;
            }
        }
        EmitInterface("gossip", "hello", outcome, guid, detail);
        return outcome == "SENT";
    }

    private bool UpdateGossipLifecycle()
    {
        if (_gossipMenu is null ||
            !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)) return false;
        ulong sourceGuid = _gossipMenu.SourceGuid;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity source);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, source.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        ResetGossip();
        EmitInterface("gossip", "lifecycle-close", "CLOSED", sourceGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyGossipMenu(byte[] body)
    {
        GossipMenu menu = GossipPackets.ParseMenu(body);
        // Some streamed spawns carry a stale innkeeper bit even though their creature-query
        // identity explicitly names another vendor profession. Never expose the bind menu for
        // that mismatch; enter the vendor service directly.
        if (_entities.TryGet(menu.SourceGuid, out WorldEntity routedSource) &&
            HasStaleInnkeeperBit(routedSource) && (routedSource.NpcFlags & NpcVendor) != 0)
        {
            EmitInterface("gossip", "menu", "REROUTED_VENDOR", menu.SourceGuid,
                $"textId={menu.TextId};npcFlags=0x{routedSource.NpcFlags:X8}");
            ResetGossip();
            RequestVendor(menu.SourceGuid);
            return;
        }
        _gossipMenu = menu;
        _gossipGreeting = null;
        _gossipScroll = 0;
        byte sourceGender = 0;
        if (_entities.TryGet(menu.SourceGuid, out WorldEntity source))
        {
            _gossipSourceFlags = source.NpcFlags;
            sourceGender = source.Fields.Bytes0.Gender;
        }
        EmitInterface("gossip", "menu", "DECODED", menu.SourceGuid,
            $"textId={menu.TextId};options={menu.Options.Count};quests={menu.Quests.Count};npcFlags=0x{_gossipSourceFlags:X8}");
        if (_npcTextRecords.TryGetValue(menu.TextId, out NpcText? cached))
        {
            _gossipGreeting = DrawGossipGreeting(cached, sourceGender);
            EmitInterface("gossip", "text-query", "CACHE_HIT", menu.SourceGuid,
                $"textId={menu.TextId};gender={sourceGender}");
        }
        else
        {
            bool sent = _net?.NpcTextQuery(menu.TextId, menu.SourceGuid) == true;
            EmitInterface("gossip", "text-query", sent ? "SENT" : "SEND_FAILED", menu.SourceGuid,
                $"textId={menu.TextId}");
        }
    }

    private void ApplyNpcText(byte[] body)
    {
        NpcText text = GossipPackets.ParseText(body);
        _npcTextRecords[text.TextId] = text;
        if (_gossipMenu is null || text.TextId != _gossipMenu.TextId)
        {
            EmitInterface("gossip", "text", "IGNORED_STALE", 0,
                $"textId={text.TextId};openTextId={_gossipMenu?.TextId ?? 0}");
            return;
        }
        byte sourceGender = _entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity source)
            ? source.Fields.Bytes0.Gender
            : (byte)0;
        _gossipGreeting = DrawGossipGreeting(text, sourceGender);
        EmitInterface("gossip", "text", "DECODED", _gossipMenu.SourceGuid,
            $"textId={text.TextId};blocks={text.Blocks.Count};gender={sourceGender};selectedChars={_gossipGreeting.Length}");
    }

    private void ApplyGossipPoi(byte[] body)
    {
        GossipPoi poi = GossipPackets.ParsePoi(body);
        _gossipPoi = poi;
        // Stamp with the LIVE map (adopted on every worldport), never the roster snapshot:
        // the minimap tests this against _config.Start.Map, so a snapshot stamp could never
        // match again after the first map change and the guard-directions pin vanished.
        _gossipPoiMapId = checked((uint)Math.Max(0, _config.Start.Map));
        EmitInterface("gossip", "poi", "DECODED", _gossipMenu?.SourceGuid ?? 0,
            $"map={_gossipPoiMapId};x={poi.Position.X:R};y={poi.Position.Y:R};" +
            $"icon={poi.Icon};flags=0x{poi.Flags:X8};data={poi.Data};" +
            $"name={SanitizeEvidence(poi.Name)}");
    }

    private static string DrawGossipGreeting(NpcText text, byte sourceGender) =>
        GossipUiLaw.SelectGreeting(text.Blocks, sourceGender,
            GossipUiLaw.GreetingRoll(Random.Shared)) ?? "";

    private bool SelectGossipOption(int visualIndex)
    {
        if (_gossipMenu is null || visualIndex < 0 || visualIndex >= _gossipMenu.Options.Count)
        {
            EmitInterface("gossip", "select", "REFUSED_NO_OPTION", _gossipMenu?.SourceGuid ?? 0,
                $"visualIndex={visualIndex};count={_gossipMenu?.Options.Count ?? 0}");
            return false;
        }
        GossipOption option = _gossipMenu.Options[visualIndex];
        if (option.Coded)
        {
            EmitInterface("gossip", "select", "REFUSED_CODE_REQUIRED", _gossipMenu.SourceGuid,
                $"visualIndex={visualIndex};listId={option.ListId}");
            return false;
        }
        if (RefuseTacticalFreezeLiveCommand("selecting a gossip option")) return false;
        if (RefuseTacticalFrozenActor(_gossipMenu.SourceGuid,
                "select a gossip option from it")) return false;
        string route = ClassifyGossipRoute(_gossipSourceFlags, option.Text);
        bool sent = _net?.GossipSelect(_gossipMenu.SourceGuid, option.ListId) == true;
        EmitInterface("gossip", "select", sent ? "SENT" : "SEND_FAILED", _gossipMenu.SourceGuid,
            $"visualIndex={visualIndex};listId={option.ListId};icon={option.Icon};route={route};text={SanitizeEvidence(option.Text)}");
        return sent;
    }

    private static string ClassifyGossipRoute(uint flags, string optionText)
    {
        if ((flags & NpcVendor) != 0) return "vendor";
        if ((flags & NpcTrainer) != 0) return "trainer";
        if ((flags & NpcFlightMaster) != 0) return "flightmaster";
        if ((flags & NpcInnkeeper) != 0) return "innkeeper";
        if ((flags & NpcBanker) != 0) return "banker";
        if ((flags & NpcAuctioneer) != 0) return "auctioneer";
        if ((flags & NpcQuestGiver) != 0) return "quest";
        return optionText.Length == 0 ? "unknown" : "gossip";
    }

    private static string SanitizeEvidence(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();

    private bool IsInnkeeper(WorldEntity npc)
    {
        if ((npc.NpcFlags & NpcInnkeeper) == 0) return false;
        if (!_creatureQueryRecords.TryGetValue(npc.Entry, out CreatureQueryInfo? identity) ||
            identity is null)
            return true;
        return identity.Name.Contains("Innkeeper", StringComparison.OrdinalIgnoreCase) ||
               (identity.Subname?.Contains("Innkeeper", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // True only for the narrow stale-data case: the innkeeper bit is actually set, but the
    // creature-query identity contradicts it. Must not match a legitimate multi-role NPC
    // (e.g. quest-giver + vendor) that simply never had the innkeeper bit in the first place.
    private bool HasStaleInnkeeperBit(WorldEntity npc) =>
        (npc.NpcFlags & NpcInnkeeper) != 0 && !IsInnkeeper(npc);

    private void EmitInterface(string family, string step, string outcome, ulong guid, string detail)
    {
        var verdict = new InterfaceVerdict(NowSeconds(), family, step, outcome, guid, detail);
        _verdicts.Add(verdict);
        if (_config.DevTools) Console.WriteLine($"[verdict:interface] {verdict.ToLine()}");
    }

    private void DrawGossipFrame()
    {
        if (_gossipMenu is null) return;
        // The creature query can complete after SMSG_GOSSIP_MESSAGE. Re-check here so a late
        // profession identity still closes a wrongly offered bind menu exactly once.
        if (_entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity identifiedSource) &&
            HasStaleInnkeeperBit(identifiedSource) && (identifiedSource.NpcFlags & NpcVendor) != 0)
        {
            ulong vendorGuid = _gossipMenu.SourceGuid;
            ResetGossip();
            RequestVendor(vendorGuid);
            return;
        }
        float s = GameplayUiScale();
        Vector2 size = GossipUiLaw.FrameSize(s);
        Vector2 p = UiPanelFrameOrigin(UiPanelOwnershipRegistry[0], s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);ImGuiWindowFlags flags=ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##gossip-frame", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="gossip"){BeginUiParityFrame(p,s);CollectUiParityDraw("GossipFrame","Frame",p,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        WorldEntity? source = _entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity foundSource)
            ? foundSource : null;
        // The portrait is an ARTWORK region below the shell textures. Drawing it first lets the
        // authored transparent circular aperture mask the square portrait naturally.
        if (source is not null)
            DrawUnitPortraitImage(dl, source, p + GossipUiLaw.Portrait.Min * s,
                GossipUiLaw.Portrait.Width * s, 0, false);
        foreach (GossipUiLaw.ArtPiece piece in GossipUiLaw.ShellArt)
        {
            Vector2 artMin = p + piece.Rect.Min * s;
            DrawArt(dl, piece.Path, artMin, piece.Rect.Size, s);
            if (_uiParityArmed && _uiParityPanel == "gossip")
                CollectUiParityDraw(piece.Element, "Texture", artMin, piece.Rect.Size * s,
                    "GossipFrameGreetingPanel", new(piece.Path, 0xffffffff, "IMGUI_IMAGE",
                        "TOPLEFT", "GossipFrame", "TOPLEFT", piece.Rect.X, -piece.Rect.Y));
        }
        string sourceName = source is not null
            ? source.IsPlayer
                ? _playerNames.GetValueOrDefault(source.Guid, "Player")
                : _creatureNames.GetValueOrDefault(source.Entry, $"Creature {source.Entry}")
            : $"0x{_gossipMenu.SourceGuid:X16}";
        GameText.DrawCentered(dl, GossipUiLaw.TitleFont, sourceName,
            p + GossipUiLaw.TitleCenter * s, s);
        string greeting = _gossipGreeting ?? $"Loading text {_gossipMenu.TextId}...";
        greeting = ExpandQuestText(greeting);
        float greetingHeight = MeasureQuestWrappedText(greeting,
            GossipUiLaw.Greeting.Width, "QuestFont", s) / s;
        var rows = new List<(bool Quest, int Index, string Text, string Icon,
            bool Enabled, uint QuestId, uint QuestIcon)>();
        for (int i = 0; i < _gossipMenu.Quests.Count &&
                rows.Count < GossipUiLaw.MaximumRows; i++)
        {
            GossipQuest quest = _gossipMenu.Quests[i];
            rows.Add((true, i, $"[{quest.Level}] {ExpandQuestText(quest.Title)}",
                GossipUiLaw.QuestIcon(quest.Icon), true, quest.QuestId, quest.Icon));
        }
        for (int i = 0; i < _gossipMenu.Options.Count &&
                rows.Count < GossipUiLaw.MaximumRows; i++)
        {
            GossipOption option = _gossipMenu.Options[i];
            string displayText = ExpandQuestText(option.Text);
            if (option.Text.StartsWith("GOSSIP_OPTION_", StringComparison.Ordinal))
                displayText = InventoryGlobalString(option.Text, option.Text switch
                {
                    "GOSSIP_OPTION_AUCTIONEER" => "I would like to make a bid.",
                    _ => displayText,
                });
            rows.Add((false, i, displayText,
                GossipUiLaw.OptionIcon(option.Icon), !option.Coded, 0, 0));
        }
        float[] rowHeights = rows.Select(row => GossipUiLaw.RowHeight(
            MeasureQuestWrappedText(row.Text, GossipUiLaw.RowTextWidth,
                "QuestFont", s) / s)).ToArray();
        float contentHeight = GossipUiLaw.ContentHeight(greetingHeight, rowHeights);
        _gossipScroll = GossipUiLaw.ClampScroll(_gossipScroll, contentHeight);
        Vector2 scrollMin = p + GossipUiLaw.Scroll.Min * s;
        Vector2 scrollMax = scrollMin + GossipUiLaw.Scroll.Size * s;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0 && ImGui.IsMouseHoveringRect(scrollMin, scrollMax, false))
            _gossipScroll = GossipUiLaw.WheelScroll(_gossipScroll, contentHeight, wheel);

        ImGui.PushClipRect(scrollMin, scrollMax, true);
        DrawQuestWrappedText(dl, greeting,
            p + GossipUiLaw.GreetingMin(_gossipScroll) * s,
            GossipUiLaw.Greeting.Width, "QuestFont", s,
            FontObjectLaw.Get("QuestFont").Color);
        float rowY = GossipUiLaw.RowTop(greetingHeight);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool clicked = DrawGossipTitleRow(dl,
                row.Quest ? $"##gossip-quest-{row.QuestId}" : $"##gossip-option-{row.Index}",
                p + GossipUiLaw.RowMin(rowY, _gossipScroll) * s,
                s, row.Text, row.Icon, row.Enabled, out _);
            if (clicked && row.Quest)
            {
                if (QuestFrameUiLaw.GreetingAction(row.QuestIcon) == QuestGreetingAction.Complete)
                    RequestQuestCompletion(_gossipMenu.SourceGuid, row.QuestId);
                else
                    RequestQuestDetails(_gossipMenu.SourceGuid, row.QuestId);
            }
            else if (clicked) SelectGossipOption(row.Index);
            rowY += rowHeights[i];
        }
        ImGui.PopClipRect();
        DrawGossipScrollBar(dl, p, s, contentHeight);
        // BOTTOMRIGHT relative to GossipFrame BOTTOMRIGHT (-39,+73), 78x22.
        if(VanillaButton(dl,"##gossip-goodbye","Goodbye",
            p + GossipUiLaw.Goodbye.Min * s, GossipUiLaw.Goodbye.Size, s)) ResetGossip();
        Vector2 close = p + GossipUiLaw.Close.Min * s;
        DrawImageButton(dl,"##gossip-close",close,GossipUiLaw.Close.Size*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())ResetGossip();
        if(_uiParityArmed&&_uiParityPanel=="gossip")MarkUiParityFrameComplete();ImGui.End();
    }

    private bool DrawGossipTitleRow(ImDrawListPtr dl, string id, Vector2 min, float s,
        string text, string iconPath, bool enabled, out float logicalAdvance)
    {
        float textHeight = MeasureQuestWrappedText(
            text, GossipUiLaw.RowTextWidth, "QuestFont", s) / s;
        logicalAdvance = GossipUiLaw.RowHeight(textHeight);
        Vector2 hitSize = GossipUiLaw.RowHitSize(logicalAdvance);
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton(id, hitSize * s);
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\QuestFrame\UI-QuestTitleHighlight") ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight,min,min+hitSize*s);
        }
        uint tint = enabled ? 0xffffffff : 0xff777777;
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        if (icon != 0)
            dl.AddImage((nint)icon, min, min + GossipUiLaw.RowIconSize * s,
                GossipUiLaw.RowIconUvMin, GossipUiLaw.RowIconUvMax, tint);
        DrawQuestWrappedText(dl, text, min + GossipUiLaw.RowTextOffset * s,
            GossipUiLaw.RowTextWidth,
            "QuestFont",s, enabled ? FontObjectLaw.Get("QuestFont").Color : 0xff777777);
        return enabled && clicked;
    }

    private void DrawGossipScrollBar(ImDrawListPtr dl, Vector2 origin, float scale,
        float contentHeight)
    {
        float maximum = GossipUiLaw.MaximumScroll(contentHeight);
        if (maximum <= 0 || _gameplayArt is null) return;

        void Arrow(string id, GossipLogicalRect rect, bool up)
        {
            bool enabled = up ? _gossipScroll > 0 : _gossipScroll < maximum;
            Vector2 min = origin + rect.Min * scale;
            Vector2 size = rect.Size * scale;
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id, size);
            bool active = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
            if (!enabled) ImGui.EndDisabled();
            string stem = up ? "UI-ScrollBar-ScrollUpButton" : "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint art = _gameplayArt.Handle($@"Interface\Buttons\{stem}-{state}");
            if (art != 0)
                dl.AddImage((nint)art, min, min + size,
                    GossipUiLaw.ScrollButtonUvMin, GossipUiLaw.ScrollButtonUvMax);
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    $@"Interface\Buttons\{stem}-Highlight");
                if (highlight != 0)
                    dl.AddImage((nint)highlight, min, min + size,
                        GossipUiLaw.ScrollButtonUvMin, GossipUiLaw.ScrollButtonUvMax);
            }
            if (clicked)
            {
                _gossipScroll = GossipUiLaw.ClampScroll(_gossipScroll +
                    (up ? -GossipUiLaw.ScrollStep : GossipUiLaw.ScrollStep), contentHeight);
                PlayUiSound("UChatScrollButton", "ui.gossip");
            }
        }

        Arrow("##gossip-scroll-up", GossipUiLaw.ScrollUp, true);
        Arrow("##gossip-scroll-down", GossipUiLaw.ScrollDown, false);
        Vector2 trackMin = origin + GossipUiLaw.ScrollTrack.Min * scale;
        Vector2 trackSize = GossipUiLaw.ScrollTrack.Size * scale;
        Vector2 knobSize = GossipUiLaw.ScrollKnobSize * scale;
        Vector2 knobMin = origin +
            GossipUiLaw.ScrollKnobMin(_gossipScroll, contentHeight) * scale;
        uint knob = _gameplayArt.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
            dl.AddImage((nint)knob, knobMin, knobMin + knobSize,
                GossipUiLaw.ScrollButtonUvMin, GossipUiLaw.ScrollButtonUvMax);
        ImGui.SetCursorScreenPos(trackMin);
        ImGui.InvisibleButton("##gossip-scroll-track", trackSize);
        if (ImGui.IsItemActive())
        {
            float travel = trackSize.Y - knobSize.Y;
            float localY = ImGui.GetIO().MousePos.Y - trackMin.Y - knobSize.Y * .5f;
            _gossipScroll = GossipUiLaw.ClampScroll(
                Math.Clamp(localY / MathF.Max(1, travel), 0, 1) * maximum,
                contentHeight);
        }
    }

}
