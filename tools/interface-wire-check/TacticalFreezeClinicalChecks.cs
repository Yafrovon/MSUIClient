using System.Numerics;
using MSUIClient;
using MSUIClient.Net;
using MSUIClient.World.Units;

/// <summary>
/// Tactical Freeze v1: exact versioned wire, defensive bounds, overlapping membership law,
/// spline-clock pause, opcode/capability assignments, and the production wiring that keeps
/// Command View live until its explicit eighth command-card button is pressed.
/// </summary>
internal static class TacticalFreezeClinicalChecks
{
    public static void Run()
    {
        CheckConstantsAndRequests();
        CheckFreezeSnapshots();
        CheckQueueSnapshots();
        CheckOverlapAndSplineLaw();
        CheckProductionWiring();
        Console.WriteLine("interface-wire-check: TacticalFreeze PASS");
    }

    private static void CheckConstantsAndRequests()
    {
        Check((ushort)Op.CMSG_SUI_TACTICAL_FREEZE == 870 &&
              (ushort)Op.SMSG_SUI_TACTICAL_FREEZE == 871 &&
              (ushort)Op.CMSG_SUI_TACTICAL_QUEUE == 872 &&
              (ushort)Op.SMSG_SUI_TACTICAL_QUEUE == 873,
            "tactical freeze opcode quartet drift");
        Check(SuiCapabilityWire.TacticalFreezeV1 == 1u << 12,
            "Tactical Freeze must remain capability bit 12");
        Check(TacticalFreezeWire.MaxMembers == ushort.MaxValue,
            "client imposed a smaller-than-wire Tactical Freeze member cap");

        byte[] acquire = TacticalFreezeWire.BuildFreezeRequest(0x11223344, true, 0);
        Check(acquire.Length == 14 && acquire[0] == TacticalFreezeWire.Version &&
              BitConverter.ToUInt32(acquire, 1) == 0x11223344 && acquire[5] == 1 &&
              BitConverter.ToUInt64(acquire, 6) == 0,
            "freeze acquire body is not v:u8/request:u32/active:u8/lock:u64");
        byte[] release = TacticalFreezeWire.BuildFreezeRequest(7, false, 0xAABBCCDDUL);
        Check(release.Length == 14 && release[5] == 0 &&
              BitConverter.ToUInt64(release, 6) == 0xAABBCCDDUL,
            "freeze release did not retain the authoritative lock id");
        Refused(() => TacticalFreezeWire.BuildFreezeRequest(0, true, 0));
        Refused(() => TacticalFreezeWire.BuildFreezeRequest(1, true, 4));
        Refused(() => TacticalFreezeWire.BuildFreezeRequest(1, false, 0));

        TacticalQueueRequestRecord move = new(0x10, 0, TacticalFreezeWire.ActionMove,
            0, new Vector3(1, 2, 3), 0);
        byte[] queued = TacticalFreezeWire.BuildQueueRequest(0xABC, 9,
            TacticalFreezeWire.QueueEnqueue, [move]);
        Check(queued.Length == TacticalFreezeWire.QueueRequestHeaderBytes +
                  TacticalFreezeWire.QueueRequestRecordBytes && queued.Length == 52 &&
              queued[0] == 1 && BitConverter.ToUInt64(queued, 1) == 0xABC &&
              BitConverter.ToUInt32(queued, 9) == 9 && queued[13] == 0 && queued[14] == 1 &&
              BitConverter.ToUInt64(queued, 15) == 0x10 && queued[27] == 1,
            "queue request v1 fixed offsets drift");
        Refused(() => TacticalFreezeWire.BuildQueueRequest(1, 1,
            TacticalFreezeWire.QueueEnqueue,
            [move with { TargetGuid = 4 }]));
        Refused(() => TacticalFreezeWire.BuildQueueRequest(1, 1,
            TacticalFreezeWire.QueueEnqueue,
            [new TacticalQueueRequestRecord(1, 0, TacticalFreezeWire.ActionAttack,
                2, new Vector3(1, 0, 0), 0)]));
        Refused(() => TacticalFreezeWire.BuildQueueRequest(1, 1,
            TacticalFreezeWire.QueueCancel,
            [new TacticalQueueRequestRecord(1, 0, 0, 0, Vector3.Zero, 0)]));
        TacticalQueueRequestRecord selfCast = new(0xCAFE, 0, TacticalFreezeWire.ActionCast,
            0xCAFE, Vector3.Zero, 116);
        byte[] selfQueued = TacticalFreezeWire.BuildQueueRequest(0xABC, 10,
            TacticalFreezeWire.QueueEnqueue, [selfCast]);
        Check(BitConverter.ToUInt64(selfQueued, 15) == 0xCAFE &&
              selfQueued[27] == TacticalFreezeWire.ActionCast &&
              BitConverter.ToUInt64(selfQueued, 28) == 0xCAFE &&
              BitConverter.ToUInt32(selfQueued, 48) == 116,
            "self cast request must carry actorGuid as its explicit target, never ground origin");
        Refused(() => TacticalFreezeWire.BuildQueueRequest(1, 1,
            TacticalFreezeWire.QueueEnqueue,
            [selfCast with { Position = Vector3.One }]));
    }

    private static void CheckFreezeSnapshots()
    {
        var w = new PacketWriter();
        w.WriteU8(1);
        w.WriteU32(41);
        w.WriteU8(TacticalFreezeWire.FreezeOk);
        w.WriteU8(1);
        w.WriteU32(3);
        w.WriteU64(0xA0);                 // lock
        w.WriteU64(0xB0);                 // real socket owner
        w.WriteVector3(new Vector3(10, 20, 30));
        w.WriteF32(100);
        w.WriteU16(2);
        // Driven/anchor body may be a possessed companion and therefore differs from ownerGuid.
        w.WriteU64(0xC0);
        w.WriteU8(TacticalFreezeWire.MemberFrozen |
            TacticalFreezeWire.MemberCommandableByRecipient |
            TacticalFreezeWire.MemberAnchorBody);
        w.WriteU64(0xD0);
        w.WriteU8(TacticalFreezeWire.MemberFrozen | TacticalFreezeWire.MemberRealHuman);
        byte[] active = w.ToArray();
        Check(active.Length == TacticalFreezeWire.FreezeSnapshotHeaderBytes +
                  2 * TacticalFreezeWire.FreezeMemberBytes && active.Length == 63,
            "freeze snapshot exact 45+9N size drift");
        Check(TacticalFreezeWire.TryParseFreezeSnapshot(active, out TacticalFreezeSnapshot parsed) &&
              parsed.Active && parsed.Revision == 3 && parsed.LockId == 0xA0 &&
              parsed.OwnerGuid == 0xB0 && parsed.Members.Length == 2 &&
              parsed.Members[0].Guid == 0xC0 && parsed.Members[0].AnchorBody &&
              parsed.Members[1].RealHuman && !parsed.Members[1].CommandableByRecipient,
            "active flagged-member snapshot did not parse");

        var tombstone = new PacketWriter();
        tombstone.WriteU8(1); tombstone.WriteU32(42);
        tombstone.WriteU8(TacticalFreezeWire.FreezeOk); tombstone.WriteU8(0);
        tombstone.WriteU32(4); tombstone.WriteU64(0xA0); tombstone.WriteU64(0xB0);
        tombstone.WriteVector3(Vector3.Zero); tombstone.WriteF32(0);
        tombstone.WriteU16(0);
        Check(TacticalFreezeWire.TryParseFreezeSnapshot(tombstone.ToArray(), out var released) &&
              !released.Active && released.LockId == 0xA0 && released.OwnerGuid == 0xB0,
            "inactive tombstone must retain lock and owner for overlap-safe removal");

        var denial = new PacketWriter();
        denial.WriteU8(1); denial.WriteU32(43);
        denial.WriteU8(TacticalFreezeWire.FreezeDeniedState); denial.WriteU8(0);
        denial.WriteU32(0); denial.WriteU64(0); denial.WriteU64(0);
        denial.WriteVector3(Vector3.Zero); denial.WriteF32(0); denial.WriteU16(0);
        Check(TacticalFreezeWire.TryParseFreezeSnapshot(denial.ToArray(), out var denied) &&
              denied.LockId == 0 && denied.Revision == 0,
            "identified no-lock acquire denial was rejected");

        var notFound = new PacketWriter();
        notFound.WriteU8(1); notFound.WriteU32(44);
        notFound.WriteU8(TacticalFreezeWire.FreezeNotFound); notFound.WriteU8(0);
        notFound.WriteU32(0); notFound.WriteU64(0xA0); notFound.WriteU64(0);
        notFound.WriteVector3(Vector3.Zero); notFound.WriteF32(0); notFound.WriteU16(0);
        Check(TacticalFreezeWire.TryParseFreezeSnapshot(notFound.ToArray(), out var retired) &&
              retired.LockId == 0xA0 && retired.OwnerGuid == 0 && retired.Revision == 0,
            "NOT_FOUND must echo a nonzero stale lock without inventing owner/revision state");

        byte[] badVersion = (byte[])active.Clone(); badVersion[0] = 2;
        byte[] badResult = (byte[])active.Clone(); badResult[5] = 13;
        byte[] badActiveResult = (byte[])active.Clone();
        badActiveResult[5] = TacticalFreezeWire.FreezeReleasedView;
        byte[] badActiveRadius = (byte[])active.Clone();
        BitConverter.GetBytes(99f).CopyTo(badActiveRadius, 39);
        byte[] badFlags = (byte[])active.Clone(); badFlags[53] |= 0x80;
        byte[] badInactiveCenter = (byte[])tombstone.ToArray().Clone();
        BitConverter.GetBytes(1f).CopyTo(badInactiveCenter, 27);
        byte[] badInactiveResult = (byte[])tombstone.ToArray().Clone();
        badInactiveResult[5] = TacticalFreezeWire.FreezeNotOwner;
        byte[] statefulNotFound = (byte[])tombstone.ToArray().Clone();
        statefulNotFound[5] = TacticalFreezeWire.FreezeNotFound;
        Check(!TacticalFreezeWire.TryParseFreezeSnapshot(badVersion, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badResult, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badActiveResult, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badActiveRadius, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badFlags, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badInactiveCenter, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(badInactiveResult, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(statefulNotFound, out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot(active[..^1], out _) &&
              !TacticalFreezeWire.TryParseFreezeSnapshot([.. active, 0], out _),
            "freeze parser accepted version/result/flags/length drift");
    }

    private static void CheckQueueSnapshots()
    {
        var w = new PacketWriter();
        w.WriteU8(1); w.WriteU64(0xA0); w.WriteU32(8); w.WriteU32(17);
        w.WriteU8(TacticalFreezeWire.QueueActionStarted);
        w.WriteU64(0xC0); w.WriteU32(0x1234); w.WriteU8(1);
        w.WriteU64(0xC0); w.WriteU8(1);
        w.WriteU32(0x1234); w.WriteU8(TacticalFreezeWire.ActionCast);
        w.WriteU64(0xD0); w.WriteVector3(Vector3.Zero); w.WriteU32(116);
        byte[] body = w.ToArray();
        Check(body.Length == 31 + 9 + 29 && body.Length == 69,
            "queue snapshot base/actor/action size drift");
        Check(TacticalFreezeWire.TryParseQueueSnapshot(body, out TacticalQueueSnapshot parsed) &&
              parsed.LockId == 0xA0 && parsed.Revision == 8 && parsed.RequestId == 17 &&
              parsed.Result == TacticalFreezeWire.QueueActionStarted &&
              parsed.ResultActorGuid == 0xC0 && parsed.ResultActionId == 0x1234 &&
              parsed.Actors.Length == 1 && parsed.Actors[0].Actions.Length == 1 &&
              parsed.Actors[0].Actions[0].SpellId == 116,
            "authoritative queue/action attribution did not parse");

        var badPacket = new PacketWriter();
        badPacket.WriteU8(1); badPacket.WriteU64(0); badPacket.WriteU32(0);
        badPacket.WriteU32(18); badPacket.WriteU8(TacticalFreezeWire.QueueBadPacket);
        badPacket.WriteU64(0); badPacket.WriteU32(0); badPacket.WriteU8(0);
        Check(TacticalFreezeWire.TryParseQueueSnapshot(badPacket.ToArray(), out var refused) &&
              refused.LockId == 0 && refused.Revision == 0 && refused.Actors.Length == 0,
            "stateless BAD_PACKET queue result was rejected");
        var missing = new PacketWriter();
        missing.WriteU8(1); missing.WriteU64(0xA0); missing.WriteU32(0);
        missing.WriteU32(19); missing.WriteU8(TacticalFreezeWire.QueueLockNotFound);
        missing.WriteU64(0); missing.WriteU32(0); missing.WriteU8(0);
        Check(TacticalFreezeWire.TryParseQueueSnapshot(missing.ToArray(), out var queueRetired) &&
              queueRetired.LockId == 0xA0 && queueRetired.Revision == 0,
            "LOCK_NOT_FOUND queue result did not preserve its stale lock id");

        var notOwner = new PacketWriter();
        notOwner.WriteU8(1); notOwner.WriteU64(0xA0); notOwner.WriteU32(9);
        notOwner.WriteU32(20); notOwner.WriteU8(TacticalFreezeWire.QueueNotOwner);
        notOwner.WriteU64(0); notOwner.WriteU32(0); notOwner.WriteU8(0);
        Check(TacticalFreezeWire.TryParseQueueSnapshot(notOwner.ToArray(), out var refusedOwner) &&
              refusedOwner.LockId == 0xA0 && refusedOwner.Revision == 9 &&
              refusedOwner.Actors.Length == 0,
            "stateful NOT_OWNER with zero actors/attribution was rejected");

        byte[] badVersion = (byte[])body.Clone(); badVersion[0] = 2;
        byte[] badResult = (byte[])body.Clone(); badResult[17] = 15;
        byte[] badRevision = (byte[])body.Clone(); Array.Clear(badRevision, 9, 4);
        byte[] statefulBadPacket = (byte[])body.Clone();
        statefulBadPacket[17] = TacticalFreezeWire.QueueBadPacket;
        byte[] attributedStateless = (byte[])badPacket.ToArray().Clone();
        attributedStateless[18] = 1;
        Check(!TacticalFreezeWire.TryParseQueueSnapshot(badVersion, out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot(badResult, out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot(badRevision, out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot(statefulBadPacket, out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot(attributedStateless, out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot(body[..^1], out _) &&
              !TacticalFreezeWire.TryParseQueueSnapshot([.. body, 0], out _),
            "queue parser accepted version/result/revision/length drift");
    }

    private static void CheckOverlapAndSplineLaw()
    {
        TacticalFreezePoseLaw.Clear();
        TacticalFreezePoseLaw.ApplyLockSnapshot(1, true, [10UL, 20UL]);
        TacticalFreezePoseLaw.ApplyLockSnapshot(2, true, [20UL, 30UL]);
        Check(TacticalFreezePoseLaw.IsFrozen(10) && TacticalFreezePoseLaw.IsFrozen(20) &&
              TacticalFreezePoseLaw.IsFrozen(30), "overlap union did not freeze all members");
        TacticalFreezePoseLaw.ApplyLockSnapshot(1, false, []);
        Check(!TacticalFreezePoseLaw.IsFrozen(10) && TacticalFreezePoseLaw.IsFrozen(20) &&
              TacticalFreezePoseLaw.IsFrozen(30),
            "releasing one lock incorrectly thawed an overlapping member");
        TacticalFreezePoseLaw.ApplyLockSnapshot(2, false, []);
        Check(TacticalFreezePoseLaw.FrozenGuids.Count == 0, "last release did not thaw aggregate");

        var spline = new CreatureSpline([Vector3.Zero, new Vector3(10, 0, 0)],
            1000, flying: false, startMs: 0);
        spline.Sample(400, out Vector3 before, out _);
        spline.RebaseAfterPause(600);
        spline.Sample(1000, out Vector3 held, out _);
        spline.Sample(1100, out Vector3 resumed, out _);
        Check(MathF.Abs(before.X - 4f) < .001f && MathF.Abs(held.X - 4f) < .001f &&
              MathF.Abs(resumed.X - 5f) < .001f,
            "spline pause did not hold and resume from the sampled fraction");
        TacticalFreezePoseLaw.Clear();
    }

    private static void CheckProductionWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string Read(params string[] parts) => SourceText.Read(Path.Combine([root, "MSUIClient", .. parts]));
        string tactical = Read("GameLoop", "Scene", "GameLoop.TacticalFreeze.cs");
        string control = Read("GameLoop", "Scene", "GameLoop.Control.cs");
        string shelf = Read("GameLoop", "Hud", "GameLoop.CommandShelf.cs");
        string net = Read("GameLoop", "Scene", "GameLoop.Net.cs");
        string portals = Read("GameLoop", "Scene", "GameLoop.RealPortals.cs");
        string program = Read("Program.cs");
        string entities = Read("Net", "Entities.cs");
        string renderer = Read("World", "Units", "CreatureRenderer.cs");
        string mounts = Read("World", "Units", "CreatureRenderer.Mounts.cs");
        string taxi = Read("GameLoop", "Panels", "GameLoop.Taxi.cs");
        string follow = Read("GameLoop", "Scene", "GameLoop.Follow.cs");
        string casting = Read("GameLoop", "Combat", "GameLoop.Casting.cs");
        string combatAnimations = Read("GameLoop", "Combat", "GameLoop.CombatAnimations.cs");
        string meleeSounds = Read("GameLoop", "Combat", "GameLoop.MeleeSounds.cs");
        string emotes = Read("GameLoop", "Combat", "GameLoop.Emotes.cs");
        string sheath = Read("GameLoop", "Combat", "GameLoop.Sheath.cs");
        string companions = Read("GameLoop", "Scene", "GameLoop.CompanionRoster.cs");
        string partyQuestActs = Read("GameLoop", "Scene", "GameLoop.PartyQuestActs.cs");
        string memberFacts = Read("GameLoop", "Scene", "GameLoop.MemberFacts.cs");
        string partyLead = Read("GameLoop", "Scene", "GameLoop.PartyLead.cs");
        string commander = Read("GameLoop", "Scene", "GameLoop.CommanderMap.cs");
        string rtsGroups = Read("GameLoop", "Hud", "GameLoop.RtsControlGroups.cs");
        string targeting = Read("GameLoop", "Combat", "GameLoop.Targeting.cs");
        string actionBars = Read("GameLoop", "Hud", "GameLoop.ActionBars.cs");
        string pet = Read("GameLoop", "Panels", "GameLoop.Pet.cs");
        string petMenu = Read("GameLoop", "Panels", "GameLoop.PetMenu.cs");
        string spellbook = Read("GameLoop", "Panels", "GameLoop.Spellbook.cs");
        string unitPopup = Read("GameLoop", "Hud", "GameLoop.UnitPopup.cs");
        string partyFrames = Read("GameLoop", "Hud", "GameLoop.PartyFrames.cs");
        string duel = Read("GameLoop", "Hud", "GameLoop.Duel.cs");
        string loot = Read("GameLoop", "Panels", "GameLoop.Loot.cs");
        string groupLoot = Read("GameLoop", "Panels", "GameLoop.GroupLoot.cs");
        string confirms = Read("GameLoop", "Panels", "GameLoop.Confirms.cs");
        string gossip = Read("GameLoop", "Panels", "GameLoop.Gossip.cs");
        string vendor = Read("GameLoop", "Panels", "GameLoop.Vendor.cs");
        string vendorRender = Read("GameLoop", "Panels", "GameLoop.Vendor.Render.cs");
        string vendorRepair = Read("GameLoop", "Panels", "GameLoop.Vendor.Repair.cs");
        string bank = Read("GameLoop", "Panels", "GameLoop.Bank.cs");
        string auction = Read("GameLoop", "Panels", "GameLoop.Auction.cs");
        string mail = Read("GameLoop", "Panels", "GameLoop.Mail.cs");
        string trade = Read("GameLoop", "Panels", "GameLoop.Trade.cs");
        string quest = Read("GameLoop", "Panels", "GameLoop.Quest.cs");
        string giverQuests = Read("GameLoop", "Scene", "GameLoop.GiverQuests.cs");
        string trainer = Read("GameLoop", "Panels", "GameLoop.Trainer.cs");
        string talents = Read("GameLoop", "Panels", "GameLoop.Talents.cs");
        string hearth = Read("GameLoop", "Panels", "GameLoop.Hearth.cs");
        string tabard = Read("GameLoop", "Panels", "GameLoop.Tabard.cs");
        string stable = Read("GameLoop", "Scene", "GameLoop.Stable.cs");
        string instances = Read("GameLoop", "Scene", "GameLoop.Instances.cs");
        string inventory = Read("GameLoop", "Panels", "GameLoop.Inventory.cs");
        string deathRez = Read("GameLoop", "Combat", "GameLoop.DeathRez.cs");
        string bindings = Read("GameLoop", "Panels", "GameLoop.Bindings.cs");
        string chat = Read("GameLoop", "Panels", "GameLoop.Chat.cs");
        string social = Read("GameLoop", "Panels", "GameLoop.Social.cs");
        string guildMember = Read("GameLoop", "Panels", "GameLoop.GuildMemberDetail.cs");
        string reputation = Read("GameLoop", "Panels", "GameLoop.Reputation.cs");
        string raidInfo = Read("GameLoop", "Panels", "GameLoop.RaidInfoPanel.cs");
        string auraTools = Read("GameLoop", "Dev", "GameLoop.DevTools.Auras.cs");
        string liveRun = Read("GameLoop", "Dev", "GameLoop.LiveRun.cs");

        Check(portals.Contains("ApplyTacticalFreezeCapability(capabilities);", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_SUI_TACTICAL_FREEZE:", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_SUI_TACTICAL_QUEUE:", StringComparison.Ordinal),
            "capability latch or incoming dispatch is missing");
        Check(shelf.IndexOf("##card-tactical-freeze", StringComparison.Ordinal) >
              shelf.IndexOf("##card-sheathe", StringComparison.Ordinal) &&
              shelf.Contains("DrawTacticalQueueStrip(shelf, scale);", StringComparison.Ordinal),
            "Freeze/Resume no longer occupies the eighth command-card cell or queue strip vanished");
        Check(shelf.Contains("bool liveCommandsBlocked = TacticalFreezeBlocksLiveCommands ||",
                  StringComparison.Ordinal) &&
              shelf.Contains("TacticalOrderActorsFrozen(subjects);", StringComparison.Ordinal) &&
              shelf.Contains("(tacticalActive || !liveCommandsBlocked)", StringComparison.Ordinal) &&
              shelf.Contains("(any || _rtsPatrolAuthoring) && !liveCommandsBlocked",
                  StringComparison.Ordinal) &&
              shelf.Contains("any && !liveCommandsBlocked", StringComparison.Ordinal),
            "ordinary command-card actions remain enabled while an owned plan drains");
        Check(control.Contains("if (!PrepareTacticalCommandViewExit()) return;",
                  StringComparison.Ordinal) &&
              control.Contains("TryQueueTacticalMove(", StringComparison.Ordinal) &&
              control.Contains("TryQueueTacticalAttack(", StringComparison.Ordinal),
            "view-exit thaw or frozen move/attack routing is missing");
        int liveAttackQueue = control.IndexOf("private void QueueRtsAttack(",
            StringComparison.Ordinal);
        int liveAttackGate = liveAttackQueue < 0 ? -1 : control.IndexOf(
            "if (RefuseTacticalFreezeLiveCommand(\"issuing live attack orders\")) return;",
            liveAttackQueue, StringComparison.Ordinal);
        int liveAttackActorGate = liveAttackQueue < 0 ? -1 : control.IndexOf(
            "RefuseTacticalFrozenActors(TacticalOrderActors(subjects)", liveAttackQueue,
            StringComparison.Ordinal);
        int liveAttackMutation = liveAttackQueue < 0 ? -1 : control.IndexOf(
            "_rtsAttackQueue.Add(target);", liveAttackQueue, StringComparison.Ordinal);
        int liveMoveOrder = control.IndexOf("private void IssueRtsMoveOrder(",
            StringComparison.Ordinal);
        int liveMoveGate = liveMoveOrder < 0 ? -1 : control.IndexOf(
            "if (RefuseTacticalFreezeLiveCommand(\"issuing live move orders\")) return;",
            liveMoveOrder, StringComparison.Ordinal);
        int liveMoveActorGate = liveMoveOrder < 0 ? -1 : control.IndexOf(
            "RefuseTacticalFrozenActors(TacticalOrderActors(subjects)", liveMoveOrder,
            StringComparison.Ordinal);
        int liveMoveMutation = liveMoveOrder < 0 ? -1 : control.IndexOf(
            "ClearRtsAttackQueue();", liveMoveOrder, StringComparison.Ordinal);
        Check(liveAttackQueue >= 0 && liveAttackGate > liveAttackQueue &&
              liveAttackActorGate > liveAttackGate && liveAttackMutation > liveAttackActorGate &&
              liveMoveOrder >= 0 && liveMoveGate > liveMoveOrder &&
              liveMoveActorGate > liveMoveGate && liveMoveMutation > liveMoveActorGate,
            "delayed RTS attack/move state can be staged during owned queue drain");
        int pendingInteractionUpdate = control.IndexOf(
            "private void UpdateCommandViewPendingInteraction()", StringComparison.Ordinal);
        int pendingInteractionGate = pendingInteractionUpdate < 0 ? -1 : control.IndexOf(
            "if (TacticalFreezeBlocksLiveCommands)", pendingInteractionUpdate,
            StringComparison.Ordinal);
        int pendingInteractionAttempt = pendingInteractionUpdate < 0 ? -1 : control.IndexOf(
            "_cvPendingInteractAttempts++;", pendingInteractionUpdate, StringComparison.Ordinal);
        Check(pendingInteractionUpdate >= 0 && pendingInteractionGate > pendingInteractionUpdate &&
              pendingInteractionAttempt > pendingInteractionGate,
            "a pre-lock walk-to-service interaction can fire after tactical queue drain");
        Check(control.Contains("if (RefuseTacticalFreezeLiveCommand(\"changing control\")) return;",
                  StringComparison.Ordinal) &&
              control.Contains("RefuseTacticalFrozenActor(guid, \"take control\")",
                  StringComparison.Ordinal) &&
              control.Contains("if (TacticalFreezeBlocksLiveCommands)", StringComparison.Ordinal) &&
              shelf.Contains("if (RefuseTacticalFreezeLiveCommand(\"changing control\")) return;",
                  StringComparison.Ordinal) &&
              follow.Contains("RefuseTacticalFreezeLiveCommand(\"following another player\")",
                  StringComparison.Ordinal),
            "control handoff or ordinary follow remains reachable through Tactical Freeze");
        Check(companions.Contains(
                  "action != CompanionWire.ActionList && TacticalFreezeBlocksLiveCommands",
                  StringComparison.Ordinal) &&
              companions.Contains("RefuseTacticalFreezeLiveCommand(\"changing the companion roster\")",
                  StringComparison.Ordinal) &&
              companions.Contains("RefuseTacticalFrozenActor(guid,", StringComparison.Ordinal),
            "companion summon/dismiss can mutate the party during Tactical Freeze");
        Check(partyQuestActs.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"changing party quests\")",
                  StringComparison.Ordinal) &&
              memberFacts.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"moving party items\")",
                  StringComparison.Ordinal) &&
              memberFacts.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"rearranging party items\")",
                  StringComparison.Ordinal) &&
              partyLead.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"changing party leadership\")",
                  StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFreezeLiveCommand(\"starting party travel\")",
                  StringComparison.Ordinal) &&
              commander.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"changing RTS hero state\")",
                  StringComparison.Ordinal) &&
              partyQuestActs.Contains("RefuseTacticalFrozenActors(subjects.Select",
                  StringComparison.Ordinal) &&
              memberFacts.Contains("RefuseTacticalFrozenActors([from, to]",
                  StringComparison.Ordinal) &&
              memberFacts.Contains("RefuseTacticalFrozenActor(owner,",
                  StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFrozenActors(CommandViewTaxiWalkers()",
                  StringComparison.Ordinal) &&
              commander.Contains("RefuseTacticalFrozenActor(subjectGuid,",
                  StringComparison.Ordinal),
            "a custom SUI mutation can bypass the Tactical Freeze live-command gate");
        Check(tactical.Contains("private bool IsTacticalActorFrozen(ulong guid)",
                  StringComparison.Ordinal) &&
              tactical.Contains("private bool RefuseTacticalFrozenActor(ulong guid, string action)",
                  StringComparison.Ordinal) &&
              tactical.Contains("private ulong KnownPlayerGuid(string name)",
                  StringComparison.Ordinal) &&
              tactical.Contains("TacticalOrderActors(subjects).FirstOrDefault(IsTacticalActorFrozen)",
                  StringComparison.Ordinal) &&
              tactical.Contains("targetGuid != 0 && IsTacticalActorFrozen(targetGuid)",
                  StringComparison.Ordinal) &&
              tactical.Contains("_rtsAttackQueue.Any(IsTacticalActorFrozen)",
                  StringComparison.Ordinal) &&
              tactical.Contains("SnapshotFreezes(_pendingCastExplicitTarget)",
                  StringComparison.Ordinal) &&
              tactical.Contains("SnapshotFreezes(_cvPendingInteractGuid)",
                  StringComparison.Ordinal) &&
              tactical.Contains("SnapshotFreezes(_autoFollowGuid)",
                  StringComparison.Ordinal) &&
              control.Contains("RefuseTacticalFrozenActor(guid, \"interact with it\")",
                  StringComparison.Ordinal) &&
              follow.Contains("RefuseTacticalFrozenActor(guid, \"follow them\")",
                  StringComparison.Ordinal) &&
              tactical.Contains("_controlSwitchQueued = 0;", StringComparison.Ordinal) &&
              tactical.Contains("ClearRtsForceTakeControl();", StringComparison.Ordinal) &&
              tactical.Contains("_groundCastSpell = 0;", StringComparison.Ordinal) &&
              tactical.Contains("CancelItemTargeting();", StringComparison.Ordinal),
            "target-local frozen actors or pre-lock delayed buffers can escape the lock boundary");
        Check(tactical.Contains("owned.OwnerGuid != LocalPlayerGuid", StringComparison.Ordinal) &&
              tactical.Contains("ApplyLockSnapshot(snapshot.LockId, snapshot.Active", StringComparison.Ordinal) &&
              tactical.Contains("snapshot.Result == TacticalFreezeWire.FreezeNotFound", StringComparison.Ordinal) &&
              tactical.Contains("snapshot.Revision == 0", StringComparison.Ordinal) &&
              tactical.Contains("bool queuedWorkRemains", StringComparison.Ordinal) &&
              tactical.Contains("if (!snapshot.Active && !queuedWorkRemains)", StringComparison.Ordinal) &&
              tactical.Contains("_tacticalQueues.Remove(snapshot.LockId);", StringComparison.Ordinal) &&
              tactical.Contains("private TacticalLockView? OwnedDrainingTacticalLock", StringComparison.Ordinal) &&
              tactical.Contains("_tacticalQueues.ContainsKey(view.LockId)", StringComparison.Ordinal) &&
              tactical.Contains("OwnedActiveTacticalLock is not null || OwnedDrainingTacticalLock is not null",
                  StringComparison.Ordinal) &&
              tactical.Contains("if (snapshot.Active && affectsLocalControl)", StringComparison.Ordinal) &&
              tactical.Contains("verdict.Kind == CastTargetKind.SelfImplicit", StringComparison.Ordinal) &&
              tactical.Contains("? actorGuid : verdict.Guid", StringComparison.Ordinal) &&
              tactical.Contains("ApplyTacticalQueueSnapshot", StringComparison.Ordinal),
            "queue authority is not tied to the socket owner or snapshots are no longer authoritative");
        Check(tactical.Contains("_tacticalFreezePendingDesiredActive", StringComparison.Ordinal) &&
              tactical.Contains("_tacticalFreezePendingLockId", StringComparison.Ordinal) &&
              tactical.Contains("private bool PrepareTacticalCommandViewExit()", StringComparison.Ordinal) &&
              tactical.Contains("bool queuedActions", StringComparison.Ordinal) &&
              tactical.Contains("bool queueMutationPending", StringComparison.Ordinal) &&
              tactical.Contains("Press Ctrl+F again after DRAINED", StringComparison.Ordinal) &&
              tactical.Contains("private bool TacticalCommandViewExitAuthorizationValid()",
                  StringComparison.Ordinal) &&
              tactical.Contains("bool conflictingActive", StringComparison.Ordinal) &&
              tactical.Contains("bool conflictingDrain", StringComparison.Ordinal) &&
              control.Contains("TacticalCommandViewExitAuthorizationValid()", StringComparison.Ordinal) &&
              control.Contains("if (RefuseTacticalFreezeLiveCommand(\"changing control\")) return;",
                  StringComparison.Ordinal) &&
              control.Split(".SuiControlRelease(", StringSplitOptions.None).Length - 1 == 1,
            "control release can race acquire, queue drain, overlap, or bypass the exact thaw seam");
        Check(control.Contains("private bool CanAuthorControlledGameplay =>", StringComparison.Ordinal) &&
              control.Contains("private bool CanAuthorControlledOrSelf =>", StringComparison.Ordinal) &&
              control.Contains("!TacticalFreezeBlocksLiveCommands &&", StringComparison.Ordinal) &&
              targeting.Contains("bool targetFrozen = IsTacticalActorFrozen(guid);",
                  StringComparison.Ordinal) &&
              targeting.Contains("if (canAuthorSelection) _net?.SetSelection(guid);",
                  StringComparison.Ordinal) &&
              targeting.Contains("RefuseTacticalFrozenActor(guid, \"attack it\")",
                  StringComparison.Ordinal) &&
              actionBars.Contains("RefuseTacticalFrozenActor(target, \"target it with a live spell\")",
                  StringComparison.Ordinal),
            "live gameplay/selection authorship is not closed through owned queue drain");
        Check(shelf.Contains("_rtsUnitCastTacticalLockId", StringComparison.Ordinal) &&
              shelf.Contains("ownedTactical is null", StringComparison.Ordinal) &&
              shelf.Contains("_rtsUnitCastTacticalLockId == 0 &&",
                  StringComparison.Ordinal) &&
              shelf.Contains("RefuseTacticalFrozenActor(targetGuid, \"target it with a live spell\")",
                  StringComparison.Ordinal),
            "friendly spell targeting can cross a tactical lock/drain boundary");
        Check(pet.Contains("RefuseTacticalFrozenActor(petGuid, \"command it\")",
                  StringComparison.Ordinal) &&
              pet.Contains("RefuseTacticalFrozenActor(actionTarget,", StringComparison.Ordinal) &&
              pet.Contains("RefuseTacticalFrozenActor(petGuid, \"change its autocast\")",
                  StringComparison.Ordinal) &&
              petMenu.Contains("RefuseTacticalFrozenActor(_petPopupGuid,",
                  StringComparison.Ordinal) &&
              spellbook.Contains("RefuseTacticalFrozenActor(_petGuid, \"command it\")",
                  StringComparison.Ordinal) &&
              spellbook.Contains("RefuseTacticalFrozenActor(actionTarget,",
                  StringComparison.Ordinal) &&
              unitPopup.Contains("RefuseTacticalFrozenActor(guid, \"dismiss it\")",
                  StringComparison.Ordinal),
            "a pet/charm frozen inside another owner's lock can still be mutated or commanded");
        Check(program.Contains("controllerTacticalFrozen", StringComparison.Ordinal) &&
              program.Contains("bool tacticalLiveAuthorshipBlocked = TacticalFreezeBlocksLiveCommands;",
                  StringComparison.Ordinal) &&
              program.Contains("bool controllerTacticalFrozen = !_freeView && tacticalLiveAuthorshipBlocked;",
                  StringComparison.Ordinal) &&
              program.Contains("!serverRideActive && !controllerTacticalFrozen", StringComparison.Ordinal) &&
              program.Contains("bool sessionBodyTacticallyFrozen = TacticalFreezePoseLaw.IsFrozen(LocalPlayerGuid);",
                  StringComparison.Ordinal) &&
              program.Contains("!TacticalFreezePoseLaw.IsFrozen(ControlledGuid) &&",
                  StringComparison.Ordinal) &&
              program.Contains("translating || BindingDown(GameBinding.Jump)",
                  StringComparison.Ordinal) &&
              program.Contains("UpdateServerRideTacticalFreeze(tacticalLiveAuthorshipBlocked)",
                  StringComparison.Ordinal) &&
              program.Contains("!tacticalLiveAuthorshipBlocked && UpdateServerRide()",
                  StringComparison.Ordinal) &&
              program.Contains("bool standTriggerNow = !tacticalLiveAuthorshipBlocked &&",
                  StringComparison.Ordinal) &&
              program.Contains("TacticalFreezePoseLaw.IsFrozen(ControlledGuid)", StringComparison.Ordinal),
            "a frozen real player's local controller/CharacterRenderer can still predict movement");
        int fullReset = tactical.IndexOf("private void ResetTacticalFreezeState()",
            StringComparison.Ordinal);
        int capabilityClear = fullReset < 0 ? -1 : tactical.IndexOf(
            "_tacticalFreezeAvailable = false;", fullReset, StringComparison.Ordinal);
        int worldResetCall = capabilityClear < 0 ? -1 : tactical.IndexOf(
            "ResetTacticalFreezeWorldState();", capabilityClear, StringComparison.Ordinal);
        Check(net.Contains("if ((Op)opcode == Op.SMSG_NEW_WORLD)", StringComparison.Ordinal) &&
              net.Contains("ResetTacticalFreezeWorldState();", StringComparison.Ordinal) &&
              tactical.Contains("private void ResetTacticalFreezeWorldState()", StringComparison.Ordinal) &&
              fullReset >= 0 && capabilityClear > fullReset && worldResetCall > capabilityClear,
            "NEW_WORLD does not retire map locks while preserving negotiated capability");
        Check(taxi.Contains("_serverRideTacticalPauseStartedMs", StringComparison.Ordinal) &&
              taxi.Contains("_serverRideSpline?.RebaseAfterPause", StringComparison.Ordinal),
            "a frozen taxi entrant's local server-ride spline can still advance");
        Check(casting.Contains("else if (!ControlledBodyTacticallyFrozen) _character?.BeginSpellVisual",
                  StringComparison.Ordinal) &&
              casting.Contains("else if (!ControlledBodyTacticallyFrozen) _character?.ReleaseSpellVisual",
                  StringComparison.Ordinal) &&
              casting.Contains("else if (!ControlledBodyTacticallyFrozen) _character?.CancelSpellVisual",
                  StringComparison.Ordinal) &&
              combatAnimations.Contains("if (!ControlledBodyTacticallyFrozen)", StringComparison.Ordinal) &&
              meleeSounds.Contains("if (!ControlledBodyTacticallyFrozen)", StringComparison.Ordinal) &&
              emotes.Contains("if (!ControlledBodyTacticallyFrozen)", StringComparison.Ordinal),
            "packet-driven CharacterRenderer actions can mutate a tactically held pose");
        Check(sheath.Contains("if (ControlledBodyTacticallyFrozen)", StringComparison.Ordinal) &&
              sheath.Contains("_sheathTacticalFreezeHeld = true;", StringComparison.Ordinal) &&
              sheath.Contains("_lastServerSheathState = byte.MaxValue;", StringComparison.Ordinal) &&
              sheath.Contains("if (ControlledBodyTacticallyFrozen) return;", StringComparison.Ordinal) &&
              sheath.Contains("bool liveAuthorshipBlocked = TacticalFreezeBlocksLiveCommands;",
                  StringComparison.Ordinal) &&
              sheath.Contains("if (!liveAuthorshipBlocked && combatForcesDrawn",
                  StringComparison.Ordinal) &&
              sheath.Contains("if (!liveAuthorshipBlocked && controllerOwnsBody",
                  StringComparison.Ordinal) &&
              sheath.Contains("volunteer && !TacticalFreezeBlocksLiveCommands",
                  StringComparison.Ordinal),
            "sheath input, adoption, or late visual events can mutate a tactically held pose");
        Check(entities.Contains("e.Spline?.RebaseAfterPause", StringComparison.Ordinal) &&
              entities.Contains("TacticalFreezePoseLaw.IsFrozen", StringComparison.Ordinal) &&
              renderer.Contains("TacticalFreezeVisualLatch", StringComparison.Ordinal) &&
              renderer.Contains("TrackTacticalFreezeIntervals();", StringComparison.Ordinal) &&
              renderer.Contains("foreach (ulong guid in TacticalFreezePoseLaw.FrozenGuids)",
                  StringComparison.Ordinal) &&
              renderer.Contains("float freezeStartedAt = EnsureTacticalFreezeStartedAt(e.Guid);",
                  StringComparison.Ordinal) &&
              renderer.Contains("if (!animationFrozen && remoteMovementChanged && !remoteMasked)",
                  StringComparison.Ordinal) &&
              renderer.Contains("ReconcileTacticalFreezeThaws", StringComparison.Ordinal),
            "current-pose latch, freeze-start global evaluation, or thaw-time rebasing is missing");
        Check(mounts.Contains("_mountFreezeVisuals", StringComparison.Ordinal) &&
              mounts.Contains("clip = held.Clip;", StringComparison.Ordinal) &&
              mounts.Contains("? EnsureTacticalFreezeStartedAt(guid)", StringComparison.Ordinal) &&
              mounts.Contains("ForgetMount(guid);", StringComparison.Ordinal) &&
              program.Contains("bool tacticalMountFrozen = TacticalFreezePoseLaw.IsFrozen(ControlledGuid);",
                  StringComparison.Ordinal) &&
              program.Contains("aura?.Frozen == true || tacticalMountFrozen",
                  StringComparison.Ordinal),
            "a frozen mount can switch clips or leak its held evaluation state");
        int tacticalCast = shelf.IndexOf("TryQueueTacticalSpell(primary, spellId, explicitTarget)",
            StringComparison.Ordinal);
        int possessionHandoff = shelf.IndexOf("BeginControlHandover(primary);", StringComparison.Ordinal);
        Check(tacticalCast >= 0 && possessionHandoff > tacticalCast,
            "frozen card spells can reach possession handoff before the tactical queue path");
        int itemMethod = shelf.IndexOf("private void UsePrimaryQuickSlot", StringComparison.Ordinal);
        int itemGate = shelf.IndexOf("if (TacticalFreezeBlocksLiveCommands)",
            itemMethod, StringComparison.Ordinal);
        int itemHandoff = shelf.IndexOf("BeginControlHandover(primary);", itemMethod,
            StringComparison.Ordinal);
        Check(itemMethod >= 0 && itemGate > itemMethod && itemHandoff > itemGate &&
              shelf.Contains("private void CancelPendingPrimaryItemUse()", StringComparison.Ordinal) &&
              shelf.Contains("private void CancelPendingPrimaryCast()", StringComparison.Ordinal) &&
              tactical.Contains("CancelPendingPrimaryCast();", StringComparison.Ordinal),
            "quick-slot items can still fire or possession-handoff during Tactical Freeze");

        Check(rtsGroups.Contains("RefuseTacticalFreezeLiveCommand(\"editing control groups\")",
                  StringComparison.Ordinal),
            "control-group membership can diverge locally while a tactical plan owns live state");
        Check(loot.Contains("RefuseTacticalFreezeLiveCommand(\"looting\")", StringComparison.Ordinal) &&
              loot.Contains("RefuseTacticalFreezeLiveCommand(\"taking loot\")", StringComparison.Ordinal) &&
              groupLoot.Contains("RefuseTacticalFreezeLiveCommand(\"rolling on loot\")",
                  StringComparison.Ordinal) &&
              confirms.Contains("RefuseTacticalFreezeLiveCommand(\"assigning loot\")",
                  StringComparison.Ordinal),
            "loot request/take/roll/master mutation bypasses the tactical live-command gate");
        Check(gossip.Contains("RefuseTacticalFreezeLiveCommand(\"opening gossip\")",
                  StringComparison.Ordinal) &&
              gossip.Contains("RefuseTacticalFreezeLiveCommand(\"selecting a gossip option\")",
                  StringComparison.Ordinal) &&
              vendor.Contains("RefuseTacticalFreezeLiveCommand(\"opening a vendor\")",
                  StringComparison.Ordinal) &&
              vendor.Contains("RefuseTacticalFreezeLiveCommand(\"buying from a vendor\")",
                  StringComparison.Ordinal) &&
              vendor.Contains("RefuseTacticalFreezeLiveCommand(\"selling to a vendor\")",
                  StringComparison.Ordinal) &&
              vendorRender.Contains("RefuseTacticalFreezeLiveCommand(\"buying back an item\")",
                  StringComparison.Ordinal) &&
              vendorRepair.Contains("RefuseTacticalFreezeLiveCommand(\"repairing",
                  StringComparison.Ordinal),
            "gossip/vendor session or mutation bypasses Tactical Freeze");
        Check(bank.Contains("RefuseTacticalFreezeLiveCommand(\"opening the bank\")",
                  StringComparison.Ordinal) &&
              bank.Contains("RefuseTacticalFreezeLiveCommand(\"moving bank items\")",
                  StringComparison.Ordinal) &&
              inventory.Contains("RefuseTacticalFreezeLiveCommand(\"withdrawing a bank item\")",
                  StringComparison.Ordinal) &&
              auction.Contains("RefuseTacticalFreezeLiveCommand(\"opening the auction house\")",
                  StringComparison.Ordinal) &&
              auction.Contains("RefuseTacticalFreezeLiveCommand(\"placing an auction bid\")",
                  StringComparison.Ordinal) &&
              auction.Contains("RefuseTacticalFreezeLiveCommand(\"creating an auction\")",
                  StringComparison.Ordinal),
            "bank/auction sessions or mutations bypass Tactical Freeze");
        Check(mail.Contains("RefuseTacticalFreezeLiveCommand(\"opening a mailbox\")",
                  StringComparison.Ordinal) &&
              mail.Contains("RefuseTacticalFreezeLiveCommand(\"taking money from mail\")",
                  StringComparison.Ordinal) &&
              mail.Contains("RefuseTacticalFreezeLiveCommand(\"sending mail\")",
                  StringComparison.Ordinal) &&
              mail.Contains("MailUiLaw.IsCopied(row.Checked) && !TacticalFreezeBlocksLiveCommands",
                  StringComparison.Ordinal) &&
              trade.Contains("RefuseTacticalFreezeLiveCommand(\"starting a trade\")",
                  StringComparison.Ordinal) &&
              trade.Contains("RefuseTacticalFreezeLiveCommand(\"changing a trade offer\")",
                  StringComparison.Ordinal),
            "mail/trade mutation bypasses Tactical Freeze");
        Check(quest.Contains("RefuseTacticalFreezeLiveCommand(\"opening quest services\")",
                  StringComparison.Ordinal) &&
              quest.Contains("RefuseTacticalFreezeLiveCommand(\"accepting a quest\")",
                  StringComparison.Ordinal) &&
              quest.Contains("RefuseTacticalFreezeLiveCommand(\"completing a quest\")",
                  StringComparison.Ordinal) &&
              quest.IndexOf("RefuseTacticalFreezeLiveCommand(\"abandoning a quest\")",
                  StringComparison.Ordinal) < quest.IndexOf("_net.QuestLogRemove(",
                  StringComparison.Ordinal) &&
              companions.Contains("action != CompanionWire.ActionList", StringComparison.Ordinal),
            "quest/companion mutation bypasses Tactical Freeze or abandons optimistically");
        Check(trainer.Contains("RefuseTacticalFreezeLiveCommand(\"opening trainer services\")",
                  StringComparison.Ordinal) &&
              trainer.Contains("RefuseTacticalFreezeLiveCommand(\"training a spell\")",
                  StringComparison.Ordinal) &&
              hearth.Contains("RefuseTacticalFreezeLiveCommand(\"binding a hearthstone\")",
                  StringComparison.Ordinal) &&
              hearth.Contains("RefuseTacticalFreezeLiveCommand(\"using a hearthstone\")",
                  StringComparison.Ordinal) &&
              tabard.Contains("RefuseTacticalFreezeLiveCommand(\"opening the tabard designer\")",
                  StringComparison.Ordinal) &&
              stable.Contains("RefuseTacticalFreezeLiveCommand(\"opening the stable\")",
                  StringComparison.Ordinal) &&
              stable.Contains("!TacticalFreezeBlocksLiveCommands", StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFreezeLiveCommand(\"querying a flight master\")",
                  StringComparison.Ordinal) &&
              instances.Contains("if (TacticalFreezeBlocksLiveCommands) return;",
                  StringComparison.Ordinal),
            "trainer/hearth/tabard/stable/taxi/area-trigger send seam bypasses Tactical Freeze");
        Check(confirms.Contains("RefuseTacticalFreezeLiveCommand(\"accepting a summon\")",
                  StringComparison.Ordinal) &&
              confirms.Contains("RefuseTacticalFreezeLiveCommand(\"accepting a quest confirmation\")",
                  StringComparison.Ordinal),
            "summon or quest-confirm acceptance can teleport/mutate during Tactical Freeze");

        int lootRequest = loot.IndexOf("private bool RequestLoot", StringComparison.Ordinal);
        int lootTargetGate = loot.IndexOf(
            "RefuseTacticalFrozenActor(guid, \"loot it\")", lootRequest,
            StringComparison.Ordinal);
        int lootOptimism = loot.IndexOf("_lootAutoAllArmed =", lootRequest,
            StringComparison.Ordinal);
        Check(lootRequest >= 0 && lootTargetGate > lootRequest && lootOptimism > lootTargetGate &&
              loot.Contains("RefuseTacticalFrozenActor(_loot.Source, \"take loot from it\")",
                  StringComparison.Ordinal) &&
              groupLoot.Contains("RefuseTacticalFrozenActor(roll.Key.LootedTarget,",
                  StringComparison.Ordinal) &&
              confirms.Contains("RefuseTacticalFrozenActor(_loot.Source, \"assign its loot\")",
                  StringComparison.Ordinal) &&
              confirms.Contains("RefuseTacticalFrozenActor(candidate, \"assign loot to them\")",
                  StringComparison.Ordinal),
            "a frozen corpse/source can still be opened, looted, rolled, or assigned");

        Check(gossip.Contains("RefuseTacticalFrozenActor(guid, \"open gossip with it\")",
                  StringComparison.Ordinal) &&
              gossip.Contains("RefuseTacticalFrozenActor(_gossipMenu.SourceGuid,",
                  StringComparison.Ordinal) &&
              vendor.Contains("RefuseTacticalFrozenActor(guid, \"open its vendor inventory\")",
                  StringComparison.Ordinal) &&
              vendor.Contains("RefuseTacticalFrozenActor(_vendor.VendorGuid,",
                  StringComparison.Ordinal) &&
              vendorRender.Contains("RefuseTacticalFrozenActor(_vendor!.VendorGuid,",
                  StringComparison.Ordinal) &&
              vendorRepair.Contains("RefuseTacticalFrozenActor(_vendor.VendorGuid,",
                  StringComparison.Ordinal),
            "a frozen gossip/vendor source can still open or mutate a service session");

        Check(bank.Contains("RefuseTacticalFrozenActor(guid, \"open its bank service\")",
                  StringComparison.Ordinal) &&
              bank.Contains("RefuseTacticalFrozenActor(_bankSource,",
                  StringComparison.Ordinal) &&
              inventory.Contains("RefuseTacticalFrozenActor(_bankSource, \"withdraw through it\")",
                  StringComparison.Ordinal) &&
              auction.Contains("RefuseTacticalFrozenActor(guid, \"open its auction service\")",
                  StringComparison.Ordinal) &&
              auction.Contains("RefuseTacticalFrozenActor(_auctioneerGuid,",
                  StringComparison.Ordinal),
            "a frozen banker/auctioneer source can still open or mutate a service session");

        Check(trade.Contains("RefuseTacticalFrozenActor(guid, \"start a trade with them\")",
                  StringComparison.Ordinal) &&
              trade.Contains("RefuseTacticalFrozenActor(initiator, \"accept a trade from them\")",
                  StringComparison.Ordinal) &&
              trade.Contains("RefuseTacticalFrozenActor(_tradePartnerGuid,",
                  StringComparison.Ordinal),
            "a frozen trade partner can still be targeted by an open trade mutation");

        Check(quest.Contains("RefuseTacticalFrozenActor(guid, \"open its quest service\")",
                  StringComparison.Ordinal) &&
              quest.Contains("RefuseTacticalFrozenActor(giver, \"accept a quest from them\")",
                  StringComparison.Ordinal) &&
              quest.Contains("RefuseTacticalFrozenActor(_questOffer.GiverGuid,",
                  StringComparison.Ordinal) &&
              quest.Contains("RefuseTacticalFrozenActor(subject, \"change its quests\")",
                  StringComparison.Ordinal) &&
              giverQuests.Contains("RefuseTacticalFrozenActor(giver,",
                  StringComparison.Ordinal) &&
              partyQuestActs.Contains("RefuseTacticalFrozenActor(npcGuid,",
                  StringComparison.Ordinal) &&
              partyQuestActs.Contains("RefuseTacticalFrozenActors(CommandViewPartyGuids()",
                  StringComparison.Ordinal),
            "a frozen quest giver, subject, or recipient can still receive newly authored quest state");

        Check(trainer.Contains("RefuseTacticalFrozenActor(guid, \"open its trainer service\")",
                  StringComparison.Ordinal) &&
              trainer.Contains("RefuseTacticalFrozenActor(_trainer.TrainerGuid,",
                  StringComparison.Ordinal) &&
              talents.Contains("RefuseTacticalFreezeLiveCommand(\"spending a talent point\")",
                  StringComparison.Ordinal) &&
              talents.Contains("RefuseTacticalFrozenActor(_talentWipeTrainer,",
                  StringComparison.Ordinal) &&
              hearth.Contains("RefuseTacticalFrozenActor(guid, \"bind through it\")",
                  StringComparison.Ordinal) &&
              hearth.Contains("RefuseTacticalFrozenActor(_binderGuid,",
                  StringComparison.Ordinal) &&
              tabard.Contains("RefuseTacticalFrozenActor(guid, \"open its tabard service\")",
                  StringComparison.Ordinal) &&
              tabard.Contains("RefuseTacticalFrozenActor(_tabardVendorGuid,",
                  StringComparison.Ordinal),
            "trainer/talent/binder/tabard targets can still mutate through a frozen service source");

        Check(stable.Contains("RefuseTacticalFrozenActor(guid, \"open its stable service\")",
                  StringComparison.Ordinal) &&
              stable.Contains("RefuseTacticalFrozenActor(StableNpcGuid,",
                  StringComparison.Ordinal) &&
              stable.Contains("RefuseTacticalFrozenActor(_petGuid,",
                  StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFrozenActor(guid, \"open its taxi map\")",
                  StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFrozenActor(_taxiMasterGuid,",
                  StringComparison.Ordinal) &&
              taxi.Contains("RefuseTacticalFrozenActor(flightMaster,",
                  StringComparison.Ordinal),
            "stable/taxi sources or their physical subjects can still mutate while frozen");

        Check(confirms.Contains("RefuseTacticalFrozenActor(_summonRequester,",
                  StringComparison.Ordinal) &&
              confirms.Contains("RefuseTacticalFrozenActor(_questConfirmStarter,",
                  StringComparison.Ordinal) &&
              duel.Contains("RefuseTacticalFrozenActor(guid, \"duel them\")",
                  StringComparison.Ordinal) &&
              partyFrames.Contains("RefuseTacticalFrozenActor(_duelPopupChallenger,",
                  StringComparison.Ordinal) &&
              partyFrames.Contains("_partyInviteName", StringComparison.Ordinal) &&
              partyFrames.Split("_playerNames.FirstOrDefault",
                  StringSplitOptions.None).Length - 1 >= 2,
            "a delayed summon/quest/duel/party target can be accepted after it becomes frozen");

        Check(deathRez.Contains("RefuseTacticalFreezeLiveCommand(\"releasing your spirit\")",
                  StringComparison.Ordinal) &&
              deathRez.Contains("RefuseTacticalFrozenActor(_corpseGuid, \"reclaim it\")",
                  StringComparison.Ordinal) &&
              deathRez.Contains("RefuseTacticalFrozenActor(offer.Caster,",
                  StringComparison.Ordinal) &&
              deathRez.Contains("RefuseTacticalFrozenActor(healer, \"resurrect through it\")",
                  StringComparison.Ordinal) &&
              deathRez.Contains("RefuseTacticalFreezeLiveCommand(\"using self-resurrection\")",
                  StringComparison.Ordinal),
            "death/resurrection world-state actions can bypass the frozen subject/source law");

        Check(unitPopup.Contains("RefuseTacticalFrozenActor(guid, \"invite them to a party\")",
                  StringComparison.Ordinal) &&
              unitPopup.Contains("RefuseTacticalFrozenActor(guid, \"remove them from the party\")",
                  StringComparison.Ordinal) &&
              unitPopup.Contains("RefuseTacticalFrozenActor(_unitPopupGuid, \"change their raid marker\")",
                  StringComparison.Ordinal) &&
              bindings.Contains("RefuseTacticalFrozenActor(_selectionGuid, \"change their raid marker\")",
                  StringComparison.Ordinal) &&
              chat.Contains("KnownPlayerGuid(name)", StringComparison.Ordinal) &&
              social.Contains("RefuseTacticalFrozenActor(sel.Guid, \"invite them to a party\")",
                  StringComparison.Ordinal) &&
              guildMember.Contains("RefuseTacticalFrozenActor(KnownPlayerGuid(member.Name),",
                  StringComparison.Ordinal) &&
              liveRun.Contains("RefuseTacticalFreezeLiveCommand(\"inviting a party member\")",
                  StringComparison.Ordinal) &&
              liveRun.Contains("RefuseTacticalFrozenActor(invGuid,\"invite them to a party\")",
                  StringComparison.Ordinal) &&
              partyFrames.Contains("RefuseTacticalFrozenActor(_partyInviteGuid,",
                  StringComparison.Ordinal),
            "party/raid commandability can still be changed through a frozen explicit target");

        Check(reputation.Contains(
                  "RefuseTacticalFreezeLiveCommand(\"changing faction combat hostility\")",
                  StringComparison.Ordinal) &&
              chat.Contains("RefuseTacticalFreezeLiveCommand(\"changing PvP hostility\")",
                  StringComparison.Ordinal) &&
              raidInfo.Contains("RefuseTacticalFreezeLiveCommand(\"resetting dungeon instances\")",
                  StringComparison.Ordinal),
            "combat-hostility or instance world-state mutation bypasses Tactical Freeze");

        int cancelPlayerAura = auraTools.IndexOf("private void CancelPlayerAura(",
            StringComparison.Ordinal);
        int cancelAuraFreezeGate = cancelPlayerAura < 0 ? -1 : auraTools.IndexOf(
            "RefuseTacticalFreezeLiveCommand(\"canceling an aura\")", cancelPlayerAura,
            StringComparison.Ordinal);
        int cancelAuraSend = cancelPlayerAura < 0 ? -1 : auraTools.IndexOf(
            "_net.CancelAura(aura.SpellId)", cancelPlayerAura, StringComparison.Ordinal);
        Check(cancelPlayerAura >= 0 && cancelAuraFreezeGate > cancelPlayerAura &&
              cancelAuraSend > cancelAuraFreezeGate &&
              auraTools.Contains("CANCEL_BLOCKED_TACTICAL_FREEZE", StringComparison.Ordinal),
            "production aura cancellation can mutate a tactically frozen player");

        string gameLoopRoot = Path.Combine(root, "MSUIClient", "GameLoop");
        string tacticalPath = Path.Combine(gameLoopRoot, "Scene", "GameLoop.TacticalFreeze.cs");
        string[] bypasses = Directory.EnumerateFiles(gameLoopRoot, "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Equals(tacticalPath, StringComparison.OrdinalIgnoreCase) &&
                SourceText.Read(path).Contains(".SuiOrder(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path)).ToArray();
        Check(bypasses.Length == 0,
            "ordinary SUI_ORDER bypasses the Tactical Freeze gate: " + string.Join(", ", bypasses));
        Check(tactical.Contains("private bool TrySendLiveSuiOrder(", StringComparison.Ordinal) &&
              tactical.Contains("if (TacticalFreezeBlocksLiveCommands)", StringComparison.Ordinal),
            "legacy live orders are not centrally suppressed while a tactical lock applies");
    }

    private static void Refused(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidDataException("malformed tactical wire request was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
