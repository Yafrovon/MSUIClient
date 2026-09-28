using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party member facts (owner decision 2026-08-25): party = full facts,
/// faction = orders. A member-facts capable server pushes every party/raid
/// AiBot's inventory snapshot (SMSG_SUI_SNAPSHOT, byte-identical to the
/// possession wire) and known spells (SMSG_SUI_MEMBER_SPELLS) WITHOUT
/// possession; the client pulls (CMSG_SUI_MEMBER_FACTS) when the roster
/// changes or a consumer panel opens. Non-party faction bots stay
/// command-only — the server refuses them and the client never asks.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyMemberFactsAvailable;
    private bool _partyItemMoveAvailable;         // Phase C v1: instant party item moves
    private double _partyMemberFactsPulledAt;
    private ulong _partyMemberFactsRosterHash;    // member-set fingerprint; pull on change
    private const double PartyMemberFactsPullMinIntervalSeconds = 2.0;

    /// <summary>Called by the shared control-ACK capability parser.</summary>
    private void ApplyPartyMemberFactsCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.PartyMemberFactsV1) != 0;
        if (available != _partyMemberFactsAvailable)
            Console.WriteLine(available
                ? "[member-facts] server advertised party-member-facts-v1"
                : "[member-facts] server has no party-member-facts-v1 advertisement");
        _partyMemberFactsAvailable = available;
        bool itemMove = (capabilities & SuiCapabilityWire.PartyItemMoveV1) != 0;
        if (itemMove != _partyItemMoveAvailable)
            Console.WriteLine(itemMove
                ? "[member-facts] server advertised party-item-move-v1"
                : "[member-facts] server has no party-item-move-v1 advertisement");
        _partyItemMoveAvailable = itemMove;
        // PLAN_20 P1 rides the same ACK. Folded in here rather than added as a
        // third call at the parse site, so every party-facts bit is applied from
        // one place under one guard.
        bool questFacts = (capabilities & SuiCapabilityWire.PartyQuestFactsV1) != 0;
        if (questFacts != _partyQuestFactsAvailable)
            Console.WriteLine(questFacts
                ? "[quest-facts] server advertised party-quest-facts-v1"
                : "[quest-facts] server has no party-quest-facts-v1 advertisement");
        _partyQuestFactsAvailable = questFacts;
        ApplyPartyQuestActsCapability(capabilities);
        ApplyPartyGiverStatusCapability(capabilities);
        ApplyPartyLeadCapability(capabilities);
        ApplyThreatMeterCapability(capabilities);
        ApplyCompanionsCapability(capabilities);
        ApplyPartyGiverQuestsCapability(capabilities);
    }

    private void ResetPartyMemberFacts()
    {
        _partyMemberFactsAvailable = false;
        _partyItemMoveAvailable = false;
        _partyMemberFactsPulledAt = 0;
        _partyMemberFactsRosterHash = 0;
        _controlledInvRefreshLeft = 0;
        _controlledInvRefreshGuid = 0;
    }

    // After using an item from a possessed bot, the consumed/decremented stack must re-sync — the
    // bot has no session to stream the change, and the server's re-snapshot in the use handler can
    // fire BEFORE a cast-time consumable is actually taken (TakeCastItem runs inside Spell::cast,
    // synchronous only for instant spells). So schedule a couple of targeted member-facts re-pulls
    // to catch the post-consumption state — exactly what pressing I does, but automatic.
    private ulong _controlledInvRefreshGuid;
    private double _controlledInvRefreshAt;
    private int _controlledInvRefreshLeft;

    private void ScheduleControlledInventoryRefresh(ulong unit)
    {
        if (unit == 0 || unit == LocalPlayerGuid) return;   // own char streams its own consumption
        if (!IsPartyMemberFactsSubject(unit)) return;        // only party AiBots can be re-pulled
        _controlledInvRefreshGuid = unit;
        _controlledInvRefreshAt = NowSeconds() + 0.4;        // catch an instant consume fast...
        _controlledInvRefreshLeft = 2;                        // ...and a short cast-time one at +1.2s
    }

    private void UpdateControlledInventoryRefresh()
    {
        if (_controlledInvRefreshLeft <= 0 || _net is not { IsInWorld: true }) return;
        if (NowSeconds() < _controlledInvRefreshAt) return;
        if (IsPartyMemberFactsSubject(_controlledInvRefreshGuid))
            _net.SuiMemberFacts([_controlledInvRefreshGuid]);
        _controlledInvRefreshLeft--;
        _controlledInvRefreshAt = NowSeconds() + 0.8;
    }

    /// <summary>
    /// Instant party item move (Phase C v1): hand one bag item from a party
    /// member (or yourself) to another. The server validates the party line,
    /// answers a result code, and re-snapshots both ends — the Party Inventory
    /// columns update from those pushes, not from client optimism.
    /// </summary>
    private void RequestMemberItemMove(ulong from, ulong to, byte bag, byte slot)
    {
        if (!_partyItemMoveAvailable)
        {
            ShowUiError("Item moves need the party-item-move server capability.");
            return;
        }
        if (from == to || from == 0 || to == 0) return;
        if (RefuseTacticalFreezeLiveCommand("moving party items")) return;
        if (RefuseTacticalFrozenActors([from, to], "move its party items")) return;
        _net?.SuiMemberItemMove(from, to, bag, slot);
    }

    /// <summary>
    /// In-place rearrange within ONE member's bags — the Ctrl+F party browser's
    /// same-owner drag from one slot to another. Unlike the cross-member give, the
    /// destination slot is explicit and the server swaps src↔dest on that owner
    /// (owner->SwapItem, no possession), then re-snapshots it.
    /// </summary>
    private void RequestMemberItemRearrange(ulong owner, byte srcBag, byte srcSlot,
        byte destBag, byte destSlot)
    {
        if (!_partyItemMoveAvailable)
        {
            ShowUiError("Item moves need the party-item-move server capability.");
            return;
        }
        if (owner == 0 || (srcBag == destBag && srcSlot == destSlot)) return;
        if (RefuseTacticalFreezeLiveCommand("rearranging party items")) return;
        if (RefuseTacticalFrozenActor(owner, "rearrange its party items")) return;
        _net?.SuiMemberItemRearrange(owner, srcBag, srcSlot, destBag, destSlot);
    }

    /// <summary>SMSG_SUI_MEMBER_ITEM_MOVE_RESULT: one code + both endpoints.
    /// Success needs no UI beyond the chat line — fresh snapshots follow.</summary>
    private void ApplySuiMemberItemMoveResult(byte[] body)
    {
        if (!MemberFactsWire.TryParseMemberItemMoveResult(body,
                out byte result, out ulong from, out ulong to))
            return;
        if (result == MemberFactsWire.ItemMoveOk)
        {
            AddChatMessage($"{ResolveUnitName(from)} hands an item to {ResolveUnitName(to)}.");
            return;
        }
        ShowUiError(result switch
        {
            MemberFactsWire.ItemMoveDenied => "Item move denied — party members only.",
            MemberFactsWire.ItemMoveNoItem => "That item is no longer there.",
            MemberFactsWire.ItemMoveTargetFull =>
                $"{ResolveUnitName(to)}'s bags are full.",
            MemberFactsWire.ItemMoveUnavailable =>
                "Those two cannot exchange items right now (different map, or busy).",
            MemberFactsWire.ItemMoveRefusedItem => "That item cannot be handed over.",
            _ => "Item move failed.",
        });
    }

    /// <summary>
    /// May this guid's facts (inventory snapshot / known spells) be applied
    /// without possession? Any same-party/raid member qualifies — the server
    /// only ever pushes AiBot subjects, but the client's own gate is the party
    /// line itself, checked against both roster sources.
    /// </summary>
    private bool IsPartyMemberFactsSubject(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return false;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid == guid) return true;
        foreach ((ulong rosterGuid, _) in _suiRoster)
            if (rosterGuid == guid) return true;
        return false;
    }

    /// <summary>
    /// Per-frame: under a member-facts server, watch the party member set and
    /// pull facts when it changes (join/leave/login). The server pushes on its
    /// own roster edge too — this is the belt to its suspenders, and the only
    /// trigger that survives a mid-session reconnect.
    /// </summary>
    private void UpdatePartyMemberFacts()
    {
        if (!_partyMemberFactsAvailable || _net is not { IsInWorld: true }) return;
        ulong hash = 14695981039346656037UL;      // FNV-1a offset basis
        foreach (PartyMember member in _partyMembers)
        {
            hash ^= member.Guid;
            hash *= 1099511628211UL;
        }
        if (hash == _partyMemberFactsRosterHash) return;
        if (RequestPartyMemberFacts("roster changed"))
            _partyMemberFactsRosterHash = hash;
    }

    /// <summary>Rate-limited whole-party pull; false = retried a later frame.</summary>
    private bool RequestPartyMemberFacts(string reason)
    {
        if (!_partyMemberFactsAvailable || _net is not { IsInWorld: true }) return false;
        if (_partyMembers.Count == 0) return true;   // nothing to pull; fingerprint settles
        double now = NowSeconds();
        if (now - _partyMemberFactsPulledAt < PartyMemberFactsPullMinIntervalSeconds)
            return false;
        _partyMemberFactsPulledAt = now;
        _net.SuiMemberFacts([]);
        Console.WriteLine($"[member-facts] pulled party facts ({reason})");
        return true;
    }

    /// <summary>
    /// SMSG_SUI_MEMBER_SPELLS: one party AiBot's known spell ids, pushed
    /// without possession. Seeds the per-guid store and resolves its layered
    /// bars — exactly what the possession proxy's SMSG_INITIAL_SPELLS did,
    /// minus cooldowns (live facts stay possession-only for now).
    /// </summary>
    private void ApplySuiMemberSpells(byte[] body)
    {
        if (!MemberFactsWire.TryParseMemberSpells(body, out ulong guid, out uint[] spells))
            return;
        if (guid != ControlledGuid && !IsPartyMemberFactsSubject(guid))
        {
            Console.WriteLine($"[member-facts] spells DROPPED for 0x{guid:X} — not a party member");
            return;
        }
        _commanderFactsSeen.Add(guid);
        _commanderRaidRosterNext = 0;
        ActionsFor(guid).SeedSpells(spells);
        PopulateBotBar(guid);   // resolves the layered bars + persists the BotSpells cache
        Console.WriteLine($"[member-facts] {ResolveUnitName(guid)}: {spells.Length} known spells");
    }
}
