using MSUIClient;
using MSUIClient.Net;

/// <summary>
/// Party quest facts (PLAN_20 P1 — party = full facts, extended from bags and
/// spells to quest logs). Verifies the wire law (pull builder + quest-log
/// exact-length parser), the opcode/capability constants including the phases
/// still reserved for P3/P4, and the client-side gate law: party/raid members
/// and our own guid accepted, everything else dropped honestly.
/// </summary>
internal static class PartyQuestClinicalChecks
{
    public static void Run()
    {
        // ── Pull builder: u8 flags, u8 count, u64 guids; empty = group + self ──
        Check(QuestFactsWire.BuildQuestFactsBody([]) is [QuestFactsWire.RequestIncludeTimers, 0],
            "empty quest-facts pull must request timers and address the whole group");
        byte[] two = QuestFactsWire.BuildQuestFactsBody([0x1122334455667788UL, 9UL]);
        Check(two.Length == 18 && two[0] == QuestFactsWire.RequestIncludeTimers && two[1] == 2 &&
              BitConverter.ToUInt64(two, 2) == 0x1122334455667788UL &&
              BitConverter.ToUInt64(two, 10) == 9UL,
            "quest-facts pull body layout drift (flags/count/raw little-endian guids)");
        ExpectRefused(() => QuestFactsWire.BuildQuestFactsBody(
            [.. Enumerable.Repeat(1UL, QuestFactsWire.MaximumSubjects + 1)]));

        // ── Quest-log parser: negotiated 23-byte entries carry a deadline ─────
        var w = new PacketWriter();
        w.WriteU64(0xABCDEF01UL);
        w.WriteU8(QuestFactsWire.LogIncludesTimers);
        w.WriteU16(100);                 // heldCap — the server's MAX_QUEST_HELD
        w.WriteU16(2);
        WriteEntry(w, 3906, QuestFactsWire.StatusIncomplete, 0, 4,
            [7, 0, 0, 0], [0, 0, 0, 0], 1_000_860);
        WriteEntry(w, 1234, QuestFactsWire.StatusComplete,
            (byte)(QuestFactsWire.EntryComplete | QuestFactsWire.EntryOverflow),
            QuestFactsWire.NoLogSlot, [0, 0, 0, 0], [5, 300, 0, 0]);
        byte[] body = w.ToArray();

        Check(body.Length == QuestFactsWire.LogHeaderBytes + 2 * QuestFactsWire.LogEntryBytes,
            "quest-log fixture is not header + 2 fixed-stride entries — the stride constants drifted");
        Check(QuestFactsWire.TryParseQuestLog(body, out MemberQuestLog log) &&
              log.Subject == 0xABCDEF01UL && log.HeldCap == 100 && log.Entries.Length == 2,
            "quest-log parse did not round-trip subject + held cap + entry count");

        MemberQuestEntry first = log.Entries[0];
        Check(first.QuestId == 3906 && first.Status == QuestFactsWire.StatusIncomplete &&
              first.Slot == 4 && !first.Complete && !first.Overflow &&
              first.ObjectiveCounts is [7, 0, 0, 0] && first.ItemCounts is [0, 0, 0, 0] &&
              first.Timer == 1_000_860,
            "quest-log entry 0 drift (id/status/slot/objective counters/deadline)");

        MemberQuestEntry second = log.Entries[1];
        Check(second.QuestId == 1234 && second.Complete && second.Overflow && !second.Failed &&
              second.Slot == QuestFactsWire.NoLogSlot && second.ItemCounts is [5, 300, 0, 0],
            "quest-log overflow entry drift — a slotless quest must survive the wire " +
            "with slot 255 and the overflow flag, and item counts must not truncate at a byte");

        // A legacy server response remains readable and supplies an untimed zero.
        var legacy = new PacketWriter();
        legacy.WriteU64(9);
        legacy.WriteU8(0);
        legacy.WriteU16(20);
        legacy.WriteU16(1);
        WriteEntry(legacy, 3361, QuestFactsWire.StatusIncomplete, 0, 3,
            [0, 0, 0, 0], [0, 0, 0, 0], includeTimer: false);
        byte[] legacyBody = legacy.ToArray();
        Check(legacyBody.Length == QuestFactsWire.LogHeaderBytes + QuestFactsWire.LegacyLogEntryBytes &&
              QuestFactsWire.TryParseQuestLog(legacyBody, out MemberQuestLog legacyLog) &&
              legacyLog.Entries is [{ Timer: 0 }],
            "legacy 19-byte quest entries must remain readable as untimed quests");

        // An empty (but exact) log is legal: it means "told, and they hold none".
        var empty = new PacketWriter();
        empty.WriteU64(7);
        empty.WriteU8(0);
        empty.WriteU16(0);               // heldCap 0 = the server did not say
        empty.WriteU16(0);
        Check(QuestFactsWire.TryParseQuestLog(empty.ToArray(), out MemberQuestLog none) &&
              none.Subject == 7 && none.Entries.Length == 0,
            "an empty quest log is legal and must parse — it is not the same as never told");
        Check(none.HeldCap == 0,
            "a server that does not state a held cap must leave it zero, not guess");

        Check(!QuestFactsWire.TryParseQuestLog([], out _) &&
              !QuestFactsWire.TryParseQuestLog(body[..^1], out _) &&
              !QuestFactsWire.TryParseQuestLog([.. body, 0], out _),
            "quest-log parser must refuse truncated/padded bodies (exact-length wire law)");

        var zeroSubject = new PacketWriter();
        zeroSubject.WriteU64(0);
        zeroSubject.WriteU8(0);
        zeroSubject.WriteU16(0);
        zeroSubject.WriteU16(0);
        Check(!QuestFactsWire.TryParseQuestLog(zeroSubject.ToArray(), out _),
            "a quest log addressed to nobody must be refused, not filed under guid 0");

        var unknownFlags = new PacketWriter();
        unknownFlags.WriteU64(7);
        unknownFlags.WriteU8(0x80);
        unknownFlags.WriteU16(20);
        unknownFlags.WriteU16(0);
        Check(!QuestFactsWire.TryParseQuestLog(unknownFlags.ToArray(), out _),
            "unknown quest-log response flags must be refused, not guessed");

        // ── Opcodes + capability bit; P3/P4 stay unclaimed until they are built ─
        // 856/857 were reserved here until P3 claimed them; 858-859 stay reserved
        // for the P4 vendor pair. PartyQuestActsClinicalChecks owns the act pair.
        Check((ushort)Op.CMSG_SUI_QUEST_FACTS == 854 &&
              (ushort)Op.SMSG_SUI_QUEST_LOG == 855 &&
              !Enum.IsDefined((Op)0x035A) && !Enum.IsDefined((Op)0x035B) &&
              SuiCapabilityWire.PartyQuestFactsV1 == 1u << 5,
            "quest-facts opcodes must sit at 854/855 with 858-859 reserved for the " +
            "PLAN_20 P4 vendor pair, capability bit 5");

        // ── Client gate law ────────────────────────────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string questFacts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.QuestFacts.cs"));
        Check(questFacts.Contains("QuestFactsWire.TryParseQuestLog", StringComparison.Ordinal) &&
              questFacts.Contains("log.Subject != LocalPlayerGuid && !IsPartyMemberFactsSubject(log.Subject)",
                  StringComparison.Ordinal) &&
              questFacts.Contains("log DROPPED", StringComparison.Ordinal),
            "quest-facts gate law drift: own guid + party members accepted, everything " +
            "else dropped with an honest log line");
        Check(questFacts.Contains("counters | (state << 24), entry.Timer", StringComparison.Ordinal),
            "party quest deadlines must survive projection into the displayed quest log");
        Check(questFacts.Contains("if (entry.QuestId == 0 || entry.Rewarded ||",
                  StringComparison.Ordinal),
            "a REWARDED entry carries no log slot and must never be projected into a " +
            "quest log — it is reported only so a party view can say \"completed\", and " +
            "projecting it invents a phantom overflow quest that can be abandoned");

        Check(questFacts.Contains("if (!_partyQuestFactsAvailable || _net is not { IsInWorld: true }) return false;",
                  StringComparison.Ordinal) &&
              // Pin the COMPARISON, not the constant's name. Deleting the actual
              // rate-limit test left an unused private const behind, which C#
              // accepts without a warning — so the old assertion could not fail
              // on the one behaviour it claimed to protect.
              questFacts.Contains("if (now - _partyQuestFactsPulledAt < PartyQuestFactsPullMinIntervalSeconds)",
                  StringComparison.Ordinal) &&
              questFacts.Contains("_net.SuiQuestFacts([]);", StringComparison.Ordinal),
            "quest-facts pull must stay capability-gated and rate-limited");

        // The overflow half of the log only refreshes if something asks.
        string questPanel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        string partyPanel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.PartyQuestLog.cs"));
        string giverPanel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GiverQuests.cs"));
        string questRail = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.QuestPartyRail.cs"));
        string hudDraw = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(questPanel.Contains("RequestPartyQuestFacts(\"quest accepted\")", StringComparison.Ordinal) &&
              questPanel.Contains("RequestPartyQuestFacts(\"quest turned in\")", StringComparison.Ordinal),
            "an ordinary accept or turn-in must re-pull quest facts — a quest held past " +
            "the update-field slots produces no field change to observe, so without this " +
            "it stays invisible in the player's own log");

        // The limiter must DELAY a pull, never swallow one. Nothing on the server
        // pushes after an ordinary accept/turn-in/abandon, so a dropped pull is a
        // quest that stays wrong on screen until an unrelated roster edge.
        Check(questFacts.Contains("_partyQuestFactsPullPending = true;", StringComparison.Ordinal) &&
              questFacts.Contains("RequestPartyQuestFacts(_partyQuestFactsPendingReason + \" (deferred)\")",
                  StringComparison.Ordinal),
            "a throttled quest-facts pull must be deferred and flushed, not dropped");

        Check(questPanel.Contains("ForgetQuestFact(subject, questId);", StringComparison.Ordinal) &&
              questPanel.Contains("RequestPartyQuestFacts(\"quest abandoned\")", StringComparison.Ordinal),
            "an ordinary abandon must drop the cached row AND re-pull — MergedOwnQuestLog " +
            "re-adds any cached entry lacking a slot, so a stale row reappears as a " +
            "phantom overflow quest whose Abandon button bounces with NO_QUEST");

        // Companion credit produces NO server push of any kind.
        Check(questFacts.Contains("private void RefreshPartyQuestFactsWhileWatched()",
                  StringComparison.Ordinal) &&
              questFacts.Contains("RefreshPartyQuestFactsWhileWatched();", StringComparison.Ordinal) &&
              questFacts.Contains("bool partyTrackerVisible = _questWatches.Count > 0",
                  StringComparison.Ordinal),
            "the facts must refresh while a surface is displaying them; without it a " +
            "companion's counters are frozen for as long as you watch them");
        Check(partyPanel.Contains("MemberQuestLogAge(guid)", StringComparison.Ordinal),
            "the party quest log must state how old its facts are — the age helper " +
            "existed with no consumer at all, so the grid presented stale counters as live");
        Check(partyPanel.Contains("EnsureQuestServerTime();", StringComparison.Ordinal) &&
              partyPanel.Contains("QuestSecondsLeft(cell.Timer", StringComparison.Ordinal) &&
              partyPanel.Contains("Time Remaining: ", StringComparison.Ordinal),
            "the Party Quest Log must display each held character's own quest deadline");

        // Kill and collect objectives SHARE an index in vanilla and 89 quest/index
        // pairs across 83 quests in the shipped world DB carry both. This was fixed
        // in the party grid and left broken in the player's own log and watch frame,
        // where it also miscounted completion — because nothing pinned it anywhere.
        Check(questPanel.Contains("private IEnumerable<(string Text, bool Finished)> QuestObjectiveLines(",
                  StringComparison.Ordinal) &&
              !questPanel.Contains("private string? QuestObjectiveLine(", StringComparison.Ordinal),
            "the self quest log must emit EVERY objective line an index produces — a " +
            "single-return QuestObjectiveLine drops the collect objective whenever a " +
            "kill shares its index");
        Check(questPanel.Contains("// NOT else-if. See the summary.", StringComparison.Ordinal),
            "the collect branch must not be else-if'd onto the kill branch");
        foreach (string mixedLabelSite in new[] { questPanel, partyPanel })
            Check(mixedLabelSite.Contains("kill ? \"\" : objective.Text", StringComparison.Ordinal),
                "ObjectiveText[i] is the CREATURE objective's text; when an index carries " +
                "both objectives the collect line must fall back to the item name instead " +
                "of repeating the kill's label");

        // The pull must NOT bail on an empty party: a solo player still needs to
        // ask about their own overflow quests. This is the one place the quest
        // pull deliberately diverges from the bag pull.
        Check(!questFacts.Contains("if (_partyMembers.Count == 0) return true;", StringComparison.Ordinal),
            "quest-facts pull copied the bag pull's empty-party bail — that would " +
            "make a solo player's overflow quests permanently unreachable");

        string capability = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MemberFacts.cs"));
        Check(capability.Contains("SuiCapabilityWire.PartyQuestFactsV1", StringComparison.Ordinal) &&
              capability.Contains("_partyQuestFactsAvailable = questFacts;", StringComparison.Ordinal),
            "capability bit 5 is no longer applied alongside the other party-facts bits");

        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(dispatch.Contains("case Op.SMSG_SUI_QUEST_LOG:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplySuiQuestLog(body);", StringComparison.Ordinal),
            "SMSG_SUI_QUEST_LOG lost its dispatch case");

        // ── The braceless-guard regression this phase fixed ────────────────────
        string portals = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.RealPortals.cs"));
        Check(portals.Contains(
                  "if (trailerValid || capabilities != 0 || factionProbeReply)\n" +
                  "        {\n" +
                  "            ApplyFactionControlGroupsCapability(capabilities, factionProbeReply);\n" +
                  "            ApplyPartyMemberFactsCapability(capabilities);\n" +
                  // ApplyPartyTaxiCapability joined the guard; the block is pinned
                  // exactly so a new capability apply has to be placed inside the
                  // braces deliberately rather than trailing off the end of it.
                  "            ApplyPartyTaxiCapability(capabilities);\n" +
                  "        }", StringComparison.Ordinal),
            "the capability applies must stay inside ONE braced guard — without the " +
            "braces a trailerless ACK silently clears capabilities already advertised");

        // ── Panel law: merged rows, own column merges both sources ─────────────
        string panel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.PartyQuestLog.cs"));
        Check(panel.Contains("RequestPartyQuestFacts(\"party quest log opened\");",
                  StringComparison.Ordinal) &&
              panel.Contains("DrawVanillaPanelChrome(\"Party Quest Log\", scale, ref _partyQuestLogOpen);",
                  StringComparison.Ordinal),
            "Party Quest Log must pull on open and wear the vanilla panel chrome");
        Check(panel.Contains("ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);\n" +
                  "        ImGui.Dummy(new Vector2(1, 1));", StringComparison.Ordinal),
            "the positioned zero dummy is what gives the scrolling child its content " +
            "height over draw-list output — losing it silently kills the scrollbar");
        Check(panel.Contains("_giverQuestTextFromPartyLog = true;", StringComparison.Ordinal) &&
              panel.Contains("RequireQuestTemplate(questId);", StringComparison.Ordinal) &&
              hudDraw.Contains("DrawGiverQuestTextWindow();", StringComparison.Ordinal),
            "clicking a Party Quest Log row must open the shared read-only quest text window");
        Check(giverPanel.Contains("item:giver-party:", StringComparison.Ordinal) &&
              giverPanel.Contains("item:quest-preview:", StringComparison.Ordinal) &&
              questRail.Contains("item:quest-party-rail:", StringComparison.Ordinal) &&
              giverPanel.Contains(
                  "PrepareItemTooltipBodySnapshot(item, row.Count, ownerGuid: guid)",
                  StringComparison.Ordinal) &&
              giverPanel.Contains("PrepareItemTooltipBodySnapshot(item, row.Count)",
                  StringComparison.Ordinal) &&
              questRail.Contains(
                  "PrepareItemTooltipBodySnapshot(item, row.Count, ownerGuid: guid)",
                  StringComparison.Ordinal),
            "party quest reward chips must publish full item-stat tooltips and evaluate " +
            "member-specific proficiency against the reward's owner");

        string bars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        Check(bars.Contains("if (_freeView && bindings[i].Button == MicroMenuButtonId.QuestLog)",
                  StringComparison.Ordinal),
            "the free-view L fork must sit in the binding loop, not inside " +
            "ActivateMicroMenuButton, which the micro-menu mouse clicks share");
        Check(bars.Contains("BindingDown(GameBinding.OpenPartyQuestLog)", StringComparison.Ordinal) &&
              questPanel.Contains("AppendPartyQuestWatchLines(lines, owners);",
                  StringComparison.Ordinal) &&
              partyPanel.Contains(
                  "private IEnumerable<(string Text, bool Finished)> PartyQuestObjectiveLines(",
                  StringComparison.Ordinal),
            "the party quest log shortcut and per-member tracked objective wiring drift");
    }

    private static void WriteEntry(PacketWriter w, uint questId, byte status, byte flags,
        byte slot, byte[] objectives, ushort[] items, uint timer = 0, bool includeTimer = true)
    {
        w.WriteU32(questId);
        w.WriteU8(status);
        w.WriteU8(flags);
        w.WriteU8(slot);
        foreach (byte value in objectives) w.WriteU8(value);
        foreach (ushort value in items) w.WriteU16(value);
        if (includeTimer) w.WriteU32(timer);
    }

    private static void ExpectRefused(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidDataException("oversized quest-facts subject list was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
