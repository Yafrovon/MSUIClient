using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

namespace MSUIClient.Net;

public delegate void WirePacketObserver(
    bool outgoing, ushort opcode, ReadOnlySpan<byte> payload);
public delegate void SocketWriteObserver(
    ushort opcode, ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sha256);

// The world server (mangosd) connection: the auth handshake + 1.12 header
// obfuscation, packet framing, and the outbound CMSG builders. Ported from
// benilla-protocol/src/world/{mod,session}.rs.
//
// Framing (world/mod.rs, verified vs vmangos):
//   send: [u16 BE size = body+4][u32 LE opcode] header (encrypted after AUTH_SESSION) + plaintext body
//   recv: [u16 BE size][u16 LE opcode] 4-byte header (decrypted), then body of (size - 2) bytes.
//
// Threading: ReceivePacket() is called only from the net worker thread (owns the
// decrypter). SendPacket() takes a lock so the game-loop thread can send movement
// while the worker reads — the encrypt/decrypt cipher halves keep independent state.

public sealed class WardenRequiredException()
    : Exception("this server requires the Warden anticheat, which this client does not implement");

public sealed class WorldAuthException(byte result)
    : Exception($"world auth rejected: result 0x{result:X2}") { public byte Result { get; } = result; }

/// <summary>A CMSG_CHAR_CREATE request (benilla-protocol CharCreateReq): identity + the five
/// appearance dials the create screen picked. outfit_id is always 0 on the wire.</summary>
public readonly record struct CharCreateParams(
    string Name, byte Race, byte Class, byte Gender,
    byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialHair);

public sealed class WorldSession : IDisposable
{
    private const byte AuthOk = 0x0C;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly WorldHeaderCrypto _crypto;
    private readonly string _account;          // uppercased
    private readonly object _sendLock = new();
    private readonly WirePacketObserver? _wireObserver;
    private readonly SocketWriteObserver? _socketWriteObserver;
    private volatile bool _closed;

    private WorldSession(TcpClient tcp, NetworkStream stream, WorldHeaderCrypto crypto,
        string account, WirePacketObserver? wireObserver, SocketWriteObserver? socketWriteObserver)
    {
        _tcp = tcp;
        _stream = stream;
        _crypto = crypto;
        _account = account;
        _wireObserver = wireObserver;
        _socketWriteObserver = socketWriteObserver;
    }

    public string Account => _account;

    /// <summary>Connect + complete the auth handshake, leaving header obfuscation enabled and the session ready for CharEnum.</summary>
    public static WorldSession Connect(string host, int port, string username, byte[] sessionKey,
        TimeSpan timeout, WirePacketObserver? wireObserver = null,
        SocketWriteObserver? socketWriteObserver = null)
    {
        var tcp = new TcpClient();
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeout))
                throw new IOException($"world connect to {host}:{port} timed out");
            tcp.NoDelay = true; // Nagle off — this is a latency-critical, small-packet stream
            tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds; // handshake only; cleared after login
            var stream = tcp.GetStream();
            string account = Srp6Client.Normalize(username);
            var session = new WorldSession(tcp, stream, new WorldHeaderCrypto(sessionKey),
                account, wireObserver, socketWriteObserver);
            session.Handshake(sessionKey);
            return session;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private void Handshake(byte[] sessionKey)
    {
        // 1. SMSG_AUTH_CHALLENGE (plaintext) → server seed.
        var (op, body) = ReceivePacket();
        if (op != (ushort)Op.SMSG_AUTH_CHALLENGE)
            throw new IOException($"expected SMSG_AUTH_CHALLENGE, got opcode 0x{op:X4}");
        uint serverSeed = new PacketReader(body).ReadU32();

        // 2. Bind session key to (server seed, our client seed) → proof + header crypto.
        uint clientSeed = WorldHeaderCrypto.NewClientSeed();
        byte[] proof = WorldHeaderCrypto.AuthProof(_account, sessionKey, serverSeed, clientSeed);

        // 3. CMSG_AUTH_SESSION goes out with a PLAINTEXT header; obfuscation begins right after.
        SendPacket((ushort)Op.CMSG_AUTH_SESSION, BuildAuthSession(RealmClient.ClientBuild, _account, clientSeed, proof));
        _crypto.Enable();

        // 4. Wait for SMSG_AUTH_RESPONSE (first encrypted packet). Skip interleaved packets; a
        //    SMSG_WARDEN_DATA means the server runs Warden and will kick us ~30s later → fail now.
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_AUTH_RESPONSE)
            {
                byte result = b.Length > 0 ? b[0] : (byte)0xFF;
                if (result == AuthOk) break;
                throw new WorldAuthException(result);
            }
            if (o == (ushort)Op.SMSG_WARDEN_DATA)
                throw new WardenRequiredException();
        }

        // Handshake done: clear the read timeout so the streaming phase can block on a quiet world.
        _tcp.ReceiveTimeout = 0;
    }

    // --- framing --------------------------------------------------------------------------------

    /// <summary>Send a client packet (encrypted header + plaintext body). Thread-safe.</summary>
    public void SendPacket(ushort opcode, ReadOnlySpan<byte> body)
    {
        // size counts the 4-byte opcode + the body, but not the size field itself.
        ushort size = checked((ushort)(body.Length + 4));
        lock (_sendLock)
        {
            byte[] header = _crypto.EncryptClientHeader(size, opcode);
            // One write (header + body) so a movement report is one TCP segment.
            var packet = new byte[header.Length + body.Length];
            header.CopyTo(packet, 0);
            body.CopyTo(packet.AsSpan(header.Length));
            _stream.Write(packet, 0, packet.Length);
            _stream.Flush();
            ObserveSocketWrite(opcode, packet);
        }
        ObserveWire(outgoing: true, opcode, body);
    }

    /// <summary>Read + decrypt one server packet. Blocking; call only from the net worker thread.</summary>
    public (ushort Opcode, byte[] Body) ReceivePacket()
    {
        Span<byte> header = stackalloc byte[4];
        _stream.ReadExactly(header);
        var (size, opcode) = _crypto.DecryptServerHeader(header);
        int bodyLen = Math.Max(0, size - 2);
        byte[] body = new byte[bodyLen];
        if (bodyLen > 0) _stream.ReadExactly(body);
        ObserveWire(outgoing: false, opcode, body);
        return (opcode, body);
    }

    private void ObserveWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body)
    {
        try { _wireObserver?.Invoke(outgoing, opcode, body); }
        catch
        {
            // Instrumentation is observational: it may never fail a successful
            // send or prevent a decoded packet from reaching its handler.
        }
    }

    private void ObserveSocketWrite(ushort opcode, ReadOnlySpan<byte> packet)
    {
        if (_socketWriteObserver is null) return;
        try
        {
            byte[] sha256 = SHA256.HashData(packet);
            _socketWriteObserver(opcode, packet, sha256);
        }
        catch
        {
            // This callback runs only after Write + Flush both succeeded.
            // Evidence collection may never turn a successful socket write into
            // a failed gameplay send.
        }
    }

    // --- high-level requests --------------------------------------------------------------------

    /// <summary>Request the roster and return it (loops past interleaved account-data / tutorial packets).</summary>
    public List<Character> CharEnum()
    {
        SendPacket((ushort)Op.CMSG_CHAR_ENUM, ReadOnlySpan<byte>.Empty);
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_ENUM)
            {
                var r = new PacketReader(b);
                int count = r.ReadU8();
                var list = new List<Character>(count);
                for (int i = 0; i < count; i++) list.Add(Character.Read(r));
                return list;
            }
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    /// <summary>CMSG_CHAR_CREATE (benilla create_character): send the request, skip interleaved
    /// packets, and return the SMSG_CHAR_CREATE result byte (0x2E = success).</summary>
    public byte CreateCharacter(in CharCreateParams p)
    {
        SendPacket((ushort)Op.CMSG_CHAR_CREATE, BuildCharCreate(p));
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_CREATE) return b.Length > 0 ? b[0] : (byte)0xFF;
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    /// <summary>
    /// CMSG_CHAR_DELETE (guid, u64) -> the SMSG_CHAR_DELETE result byte. 0x39 = CHAR_DELETE_SUCCESS
    /// (vmangos Packets/Character.cpp); anything else is a refusal the caller should surface.
    /// Same shape as <see cref="CreateCharacter"/>: sent and awaited on the worker while parked at
    /// character select, so the roster can be re-enumerated straight after.
    /// </summary>
    public byte DeleteCharacter(ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_CHAR_DELETE, w.ToArray());
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_DELETE) return b.Length > 0 ? b[0] : (byte)0xFF;
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    public void RequestAccountData(uint type)
        => SendPacket((ushort)Op.CMSG_REQUEST_ACCOUNT_DATA, AccountDataPackets.Request(type));

    /// <summary>Required rename, serviced only by the character-select worker.</summary>
    public CharacterRenameResult RenameCharacter(ulong guid, string name)
    {
        SendPacket((ushort)Op.CMSG_CHAR_RENAME, CharacterRenamePackets.Build(guid, name));
        while (true)
        {
            var (opcode, body) = ReceivePacket();
            if (opcode == (ushort)Op.SMSG_CHAR_RENAME)
            {
                CharacterRenameResult result = CharacterRenamePackets.Parse(body);
                if (result.Succeeded && result.Guid != guid)
                    throw new InvalidDataException("rename reply character mismatch");
                return result;
            }
            if (opcode == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    // CMSG_CHAR_CREATE body (benilla-protocol messages/client.rs char_create, byte-exact vs vmangos
    // Packets/Character.cpp): name (CString) + race, class, gender, skin, face, hairStyle, hairColor,
    // facialHair, outfitId(0). The server reads and ignores outfitId, recomputing start gear.
    private static byte[] BuildCharCreate(in CharCreateParams p)
    {
        byte[] name = Encoding.ASCII.GetBytes(p.Name ?? "");
        var w = new PacketWriter(name.Length + 12);
        w.WriteBytes(name);
        w.WriteU8(0);                 // NUL terminator
        w.WriteU8(p.Race);
        w.WriteU8(p.Class);
        w.WriteU8(p.Gender);
        w.WriteU8(p.Skin);
        w.WriteU8(p.Face);
        w.WriteU8(p.HairStyle);
        w.WriteU8(p.HairColor);
        w.WriteU8(p.FacialHair);
        w.WriteU8(0);                 // outfit_id (ignored by the server)
        return w.ToArray();
    }

    public void PlayerLogin(ulong guid) => SendFullGuid(Op.CMSG_PLAYER_LOGIN, guid);

    /// <summary>Declare the unit we control — vmangos drops all MSG_MOVE_* until this "confirmed mover" is set.</summary>
    public void SetActiveMover(ulong guid) => SendFullGuid(Op.CMSG_SET_ACTIVE_MOVER, guid);

    /// <summary>Ask to possess a party bot (SuperUI extension; answered by SMSG_SUI_CONTROL_ACK).</summary>
    public void SuiControlRequest(ulong targetGuid) => SendFullGuid(Op.CMSG_SUI_CONTROL_REQUEST, targetGuid);

    /// <summary>Release possession. mode 0 = back to own character, 1 = free view.</summary>
    public void SuiControlRelease(byte mode)
    {
        var w = new PacketWriter(1);
        w.WriteU8(mode);
        SendPacket((ushort)Op.CMSG_SUI_CONTROL_RELEASE, w.ToArray());
    }

    /// <summary>
    /// Companion act (COMPANIONS v1): 1 summon, 2 dismiss, 3 list (guid 0). Callers gate
    /// on capability bit 7 — never send before the server has advertised it.
    /// </summary>
    public void SuiCompanion(byte action, ulong guid) =>
        SendPacket((ushort)Op.CMSG_SUI_COMPANION, CompanionWire.BuildRequest(action, guid));

    /// <summary>
    /// Party flight (PARTY_TAXI v1): the commanded party takes the flight-master chain;
    /// flags 0x01 = confirmed (fly the eligible even if some cannot board). Callers gate
    /// on capability bit 11 — never send before the server has advertised it.
    /// </summary>
    public void SuiPartyTaxi(byte flags, ulong flightMaster, IReadOnlyList<uint> nodes) =>
        SendPacket((ushort)Op.CMSG_SUI_PARTY_TAXI, PartyTaxiWire.BuildRequest(flags, flightMaster, nodes));

    /// <summary>Request or release one capability-gated Tactical Freeze radius lock.</summary>
    public void SuiTacticalFreeze(uint requestId, bool desiredActive, ulong lockId) =>
        SendPacket((ushort)Op.CMSG_SUI_TACTICAL_FREEZE,
            TacticalFreezeWire.BuildFreezeRequest(requestId, desiredActive, lockId));

    /// <summary>Enqueue, cancel or clear explicit actor records under an authoritative lock.</summary>
    public void SuiTacticalQueue(ulong lockId, uint requestId, byte operation,
        IReadOnlyList<TacticalQueueRequestRecord> records) =>
        SendPacket((ushort)Op.CMSG_SUI_TACTICAL_QUEUE,
            TacticalFreezeWire.BuildQueueRequest(lockId, requestId, operation, records));

    /// <summary>
    /// RTS order. Empty subjects retains the legacy "whole real party" meaning;
    /// callers for temporary groups must always send a nonempty explicit list.
    /// </summary>
    public void SuiCommanderRaid(byte[] body) => SendPacket((ushort)Op.CMSG_SUI_COMMANDER_RAID, body);

    public void SuiOrder(byte orderType, IReadOnlyList<ulong> subjects, ulong targetGuid, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(subjects),
                $"SUI order supports at most {byte.MaxValue} explicit subjects.");
        var w = new PacketWriter(2 + subjects.Count * 8 + 8 + 12);
        w.WriteU8(orderType);
        w.WriteU8((byte)subjects.Count);
        foreach (ulong guid in subjects) w.WriteU64(guid);
        w.WriteU64(targetGuid);
        w.WriteF32(x);
        w.WriteF32(y);
        w.WriteF32(z);
        SendPacket((ushort)Op.CMSG_SUI_ORDER, w.ToArray());
    }

    /// <summary>
    /// Free-view camera position; the server relocates the streaming eye to it.
    ///
    /// The trailing ACTIVE byte is the free view's on/off signal, not decoration. The server
    /// keys real behaviour off it — the streaming eye, and whether a possessed bot runs its own
    /// AI — so it has to be told when the camera comes down, which is otherwise a purely
    /// client-side decision it can never observe.
    /// </summary>
    public void SuiCam(float x, float y, float z, bool active = true)
    {
        var w = new PacketWriter(13);
        w.WriteF32(x);
        w.WriteF32(y);
        w.WriteF32(z);
        w.WriteU8(active ? (byte)1 : (byte)0);
        SendPacket((ushort)Op.CMSG_SUI_CAM, w.ToArray());
    }

    /// <summary>Ask for the commander map's per-zone bots/players census (SuperUI extension).</summary>
    public void SuiZoneIntel()
    {
        var w = new PacketWriter(1);
        w.WriteU8(0);                 // flags, reserved
        SendPacket((ushort)Op.CMSG_SUI_ZONE_INTEL, w.ToArray());
    }

    /// <summary>Ask for the tier-2 RTS worldstate snapshot (mode, honor pools, heroes...).</summary>
    public void SuiRtsState()
    {
        var w = new PacketWriter(1);
        w.WriteU8(0);                 // flags, reserved
        SendPacket((ushort)Op.CMSG_SUI_RTS_STATE, w.ToArray());
    }

    /// <summary>RTS action: 1 hero declare / 2 upgrade / 3 revive (answered by SMSG_SUI_RTS_ACTION_RESULT).</summary>
    public void SuiRtsAction(byte action, ulong subjectGuid)
    {
        SendPacket((ushort)Op.CMSG_SUI_RTS_ACTION,
            BuildSuiRtsActionBody(action, subjectGuid));
    }

    public static byte[] BuildSuiRtsActionBody(byte action, ulong subjectGuid) =>
        RtsWire.BuildActionBody(action, subjectGuid);

    /// <summary>Request one correlated, GUID-cursored page of same-faction RTS AiBots.</summary>
    public void SuiForceRoster(uint requestId, uint zoneId, uint afterGuidLow,
        byte limit = RtsWire.MaximumForcePageSize)
    {
        SendPacket((ushort)Op.CMSG_SUI_FORCE_ROSTER,
            BuildSuiForceRosterBody(requestId, zoneId, afterGuidLow, limit));
    }

    public static byte[] BuildSuiForceRosterBody(uint requestId, uint zoneId,
        uint afterGuidLow, byte limit = RtsWire.MaximumForcePageSize) =>
        RtsWire.BuildForceRosterRequestBody(requestId, zoneId, afterGuidLow, limit);

    /// <summary>
    /// Pull party member facts (bags + known spells) for party/raid AiBots —
    /// explicit subjects, or empty = every AiBot in the group. Only sent once
    /// the control-ACK trailer advertised party-member-facts-v1.
    /// </summary>
    public void SuiMemberFacts(IReadOnlyList<ulong> subjects) =>
        SendPacket((ushort)Op.CMSG_SUI_MEMBER_FACTS,
            MemberFactsWire.BuildMemberFactsBody(subjects));

    /// <summary>Instant party item move (Phase C v1); only sent once the
    /// control-ACK trailer advertised party-item-move-v1.</summary>
    public void SuiMemberItemMove(ulong from, ulong to, byte bag, byte slot) =>
        SendPacket((ushort)Op.CMSG_SUI_MEMBER_ITEM_MOVE,
            MemberFactsWire.BuildMemberItemMoveBody(from, to, bag, slot));

    /// <summary>In-place rearrange within ONE member's bags (Ctrl+F party
    /// browser): swap src↔dest on a single owner. Rides the item-move opcode with
    /// the in-place flag; the server does owner->SwapItem.</summary>
    public void SuiMemberItemRearrange(ulong owner, byte srcBag, byte srcSlot,
        byte destBag, byte destSlot) =>
        SendPacket((ushort)Op.CMSG_SUI_MEMBER_ITEM_MOVE,
            MemberFactsWire.BuildMemberItemMoveBody(owner, owner, srcBag, srcSlot,
                inPlace: true, destBag, destSlot));

    /// <summary>Take group leadership (PLAN_20 P4a). The server only ever grants
    /// this when the current leader is an AiBot in the requester's own group.
    /// Only sent once the control-ACK trailer advertised party-lead-v1.</summary>
    /// <summary>Threat meter v1: the top of one creature's threat list. Only sent once the
    /// control-ACK trailer advertised capability 14 (<see cref="ThreatMeterWire"/>).</summary>
    public void SuiThreat(ulong creature, int rows) =>
        SendPacket((ushort)Op.CMSG_SUI_THREAT, ThreatMeterWire.BuildRequest(creature, rows));

    /// <summary>WoW Karting v1: join/leave/start/query the kart race. Only sent once the control-ACK
    /// trailer advertised capability 15 (<see cref="KartingWire"/>).</summary>
    public void SuiKart(KartingWire.Action action, uint param = 0) =>
        SendPacket((ushort)Op.CMSG_SUI_KART, KartingWire.BuildRequest(action, param));

    public void SuiPartyLead(byte action, ulong subject) =>
        SendPacket((ushort)Op.CMSG_SUI_PARTY_LEAD,
            PartyLeadWire.BuildPartyLeadBody(action, subject));

    /// <summary>Ask what every party member would see over these questgivers'
    /// heads (PLAN_20 P5). The giver list is exactly what we are drawing markers
    /// for — there is no whole-zone shorthand, because the server's work is
    /// proportional to what it is asked about. Only sent once the control-ACK
    /// trailer advertised party-giver-status-v1.</summary>
    public void SuiGiverStatus(IReadOnlyList<ulong> givers) =>
        SendPacket((ushort)Op.CMSG_SUI_GIVER_STATUS,
            GiverStatusWire.BuildGiverStatusBody(givers));

    /// <summary>PLAN_20 Model B: ask one giver for its quests + each party member's
    /// eligibility, so the free-view quest window can draw per-member cards without
    /// possession. Only sent once party-giver-quests-v1 is advertised.</summary>
    public void SuiGiverQuests(ulong giver) =>
        SendPacket((ushort)Op.CMSG_SUI_GIVER_QUESTS, GiverQuestsWire.BuildRequest(giver));

    /// <summary>Pull party quest logs (PLAN_20 P1); an empty list means the whole
    /// group AND our own character — the latter is how overflow quests arrive.
    /// Only sent once the control-ACK trailer advertised party-quest-facts-v1.</summary>
    public void SuiQuestFacts(IReadOnlyList<ulong> subjects) =>
        SendPacket((ushort)Op.CMSG_SUI_QUEST_FACTS,
            QuestFactsWire.BuildQuestFactsBody(subjects));

    /// <summary>Accept / turn in / abandon a quest for an explicit set of party
    /// members (PLAN_20 P3). There is deliberately no whole-party shorthand: who
    /// acts must always be visible. Only sent once the control-ACK trailer
    /// advertised party-quest-acts-v1.</summary>
    public void SuiPartyQuest(byte action, uint questId, ulong npcGuid,
        IReadOnlyList<PartyQuestSubject> subjects) =>
        SendPacket((ushort)Op.CMSG_SUI_PARTY_QUEST,
            PartyQuestWire.BuildPartyQuestBody(action, questId, npcGuid, subjects));

    /// <summary>Vanilla quest sharing. Ordinary 1.12 traffic, not a SuperUI
    /// extension: real party members get the real confirmation dialog, and
    /// AiBot companions answer it through their own AI.</summary>
    /// <summary>
    /// MSG_QUEST_PUSH_RESULT (client → server): u64 sharer, u8 QuestShareMessages. The receiver's
    /// answer to a shared quest — the server relays it to the sharer and clears its share latch.
    /// 3 = DECLINE_QUEST, 5 = BUSY. Never sent for an NPC giver.
    /// </summary>
    public void QuestPushResult(ulong sharer, byte message)
    {
        var w = new PacketWriter(9);
        w.WriteU64(sharer); w.WriteU8(message);
        SendPacket((ushort)Op.MSG_QUEST_PUSH_RESULT, w.ToArray());
    }

    /// <summary>CMSG_SUMMON_RESPONSE: u64 summoner — accept a summon (a decline sends nothing).</summary>
    public void SummonResponse(ulong summoner)
    { var w = new PacketWriter(8); w.WriteU64(summoner); SendPacket((ushort)Op.CMSG_SUMMON_RESPONSE, w.ToArray()); }

    /// <summary>CMSG_QUEST_CONFIRM_ACCEPT: u32 quest — join an escort/party-accept quest a member started.</summary>
    public void QuestConfirmAccept(uint questId)
    { var w = new PacketWriter(4); w.WriteU32(questId); SendPacket((ushort)Op.CMSG_QUEST_CONFIRM_ACCEPT, w.ToArray()); }

    /// <summary>CMSG_SELF_RES: empty — the server casts PLAYER_SELF_RES_SPELL (soulstone, reincarnation).</summary>
    public void SelfRes() => SendPacket((ushort)Op.CMSG_SELF_RES, Array.Empty<byte>());

    /// <summary>CMSG_LOOT_MASTER_GIVE: u64 loot, u8 slot, u64 target.</summary>
    public void LootMasterGive(ulong lootGuid, byte slot, ulong target)
    {
        var w = new PacketWriter(17); w.WriteU64(lootGuid); w.WriteU8(slot); w.WriteU64(target);
        SendPacket((ushort)Op.CMSG_LOOT_MASTER_GIVE, w.ToArray());
    }

    public void PushQuestToParty(uint questId)
    {
        var w = new PacketWriter(4);
        w.WriteU32(questId);
        SendPacket((ushort)Op.CMSG_PUSHQUESTTOPARTY, w.ToArray());
    }

    /// <summary>Request a correlated REAL_PORTALS descriptor for a nearby portal GO.</summary>
    public void SuiPortalPrepare(uint requestId, ulong portalGuid, ushort requestFlags = 0) =>
        SuiPortalPrepare(PortalWire.Prepare(requestId, portalGuid, requestFlags));

    public void SuiPortalPrepare(PortalPreparePacket packet) =>
        SendPacket((ushort)Op.CMSG_SUI_PORTAL_PREPARE, PortalWire.BuildPrepare(packet));

    /// <summary>Publish this client's result for one exact descriptor generation/revision/ticket.</summary>
    public void SuiPortalReady(PortalLoadResult loadResult, ulong portalGuid,
        uint spawnGeneration, uint descriptorRevision, ulong ticket) =>
        SuiPortalReady(PortalWire.Ready(
            loadResult, portalGuid, spawnGeneration, descriptorRevision, ticket));

    public void SuiPortalReady(PortalReadyPacket packet) =>
        SendPacket((ushort)Op.CMSG_SUI_PORTAL_READY, PortalWire.BuildReady(packet));

    /// <summary>Acknowledge SMSG_TRIGGER_CINEMATIC as an immediate ESC-style skip.</summary>
    public void CompleteCinematic() =>
        SendPacket((ushort)Op.CMSG_COMPLETE_CINEMATIC, ReadOnlySpan<byte>.Empty);

    /// <summary>Ask for the raid/dungeon lockout list (spec P1). Empty body;
    /// answered by SMSG_RAID_INSTANCE_INFO.</summary>
    public void RequestRaidInfo() =>
        SendPacket((ushort)Op.CMSG_REQUEST_RAID_INFO, ReadOnlySpan<byte>.Empty);

    /// <summary>Reset every instance the character is NOT saved to (spec P1). Empty
    /// body; answered per-map by SMSG_INSTANCE_RESET / SMSG_INSTANCE_RESET_FAILED.</summary>
    public void ResetInstances() =>
        SendPacket((ushort)Op.CMSG_RESET_INSTANCES, ReadOnlySpan<byte>.Empty);

    /// <summary>Ask a stablemaster for the pet list (spec P3). Answered by
    /// MSG_LIST_STABLED_PETS.</summary>
    public void RequestStabledPets(ulong npcGuid) =>
        SendPacket((ushort)Op.MSG_LIST_STABLED_PETS, StableWire.BuildNpcGuidBody(npcGuid));

    /// <summary>Stable the currently active pet at this stablemaster.</summary>
    public void StablePet(ulong npcGuid) =>
        SendPacket((ushort)Op.CMSG_STABLE_PET, StableWire.BuildNpcGuidBody(npcGuid));

    /// <summary>Take a stabled pet out (make it active).</summary>
    public void UnstablePet(ulong npcGuid, uint petNumber) =>
        SendPacket((ushort)Op.CMSG_UNSTABLE_PET, StableWire.BuildPetActionBody(npcGuid, petNumber));

    /// <summary>Buy an additional stable slot.</summary>
    public void BuyStableSlot(ulong npcGuid) =>
        SendPacket((ushort)Op.CMSG_BUY_STABLE_SLOT, StableWire.BuildNpcGuidBody(npcGuid));

    /// <summary>Swap the active pet with a stabled one.</summary>
    public void SwapStablePet(ulong npcGuid, uint petNumber) =>
        SendPacket((ushort)Op.CMSG_STABLE_SWAP_PET, StableWire.BuildPetActionBody(npcGuid, petNumber));

    public void LogoutRequest() =>
        SendPacket((ushort)Op.CMSG_LOGOUT_REQUEST, ReadOnlySpan<byte>.Empty);

    public void LogoutCancel() =>
        SendPacket((ushort)Op.CMSG_LOGOUT_CANCEL, ReadOnlySpan<byte>.Empty);

    public void MarkTutorial(uint wireBit)
    {
        if (wireBit >= 256) throw new ArgumentOutOfRangeException(nameof(wireBit));
        var w = new PacketWriter(4); w.WriteU32(wireBit); SendPacket((ushort)Op.CMSG_TUTORIAL_FLAG, w.ToArray());
    }
    public void ClearTutorials() => SendPacket((ushort)Op.CMSG_TUTORIAL_CLEAR, ReadOnlySpan<byte>.Empty);
    public void ResetTutorials() => SendPacket((ushort)Op.CMSG_TUTORIAL_RESET, ReadOnlySpan<byte>.Empty);

    public void SetSelection(ulong guid) => SendFullGuid(Op.CMSG_SET_SELECTION, guid);
    public void FarSight(bool engage) =>
        SendPacket((ushort)Op.CMSG_FAR_SIGHT, ViewSubjectLaw.VoteBody(engage));
    public void Inspect(ulong guid) => SendFullGuid(Op.CMSG_INSPECT, guid);
    public void QueryAreaSpiritHealer(ulong guide)
    {
        var writer = new PacketWriter(); writer.WriteU64(guide);
        SendPacket((ushort)Op.CMSG_AREA_SPIRIT_HEALER_QUERY, writer.ToArray());
    }
    public void QueueAreaSpiritHealer(ulong guide)
    {
        var writer = new PacketWriter(); writer.WriteU64(guide);
        SendPacket((ushort)Op.CMSG_AREA_SPIRIT_HEALER_QUEUE, writer.ToArray());
    }
    public void RequestBattlefieldPositions() => SendPacket((ushort)Op.MSG_BATTLEGROUND_PLAYER_POSITIONS, ReadOnlySpan<byte>.Empty);
    public void RequestBattlefieldScores() => SendPacket((ushort)Op.MSG_PVP_LOG_DATA, ReadOnlySpan<byte>.Empty);
    public void RequestBattlefieldStatus() => SendPacket((ushort)Op.CMSG_BATTLEFIELD_STATUS, ReadOnlySpan<byte>.Empty);
    public void JoinBattlefield(ulong source, uint map, uint instance, bool asGroup)
    {
        var w = new PacketWriter(); w.WriteU64(source); w.WriteU32(map); w.WriteU32(instance); w.WriteU8(asGroup ? (byte)1 : (byte)0);
        SendPacket((ushort)Op.CMSG_BATTLEMASTER_JOIN, w.ToArray());
    }
    public void BattlefieldPort(uint map, bool enter)
    {
        var w = new PacketWriter(); w.WriteU32(map); w.WriteU8(enter ? (byte)1 : (byte)0);
        SendPacket((ushort)Op.CMSG_BATTLEFIELD_PORT, w.ToArray());
    }
    public void LeaveBattlefield(uint map)
    {
        var w = new PacketWriter(); w.WriteU32(map); SendPacket((ushort)Op.CMSG_LEAVE_BATTLEFIELD, w.ToArray());
    }
    public void InspectHonor(ulong guid) => SendFullGuid(Op.MSG_INSPECT_HONOR_STATS, guid);
    public void PetAction(ulong petGuid, uint packedAction, ulong targetGuid)
        => SendPacket((ushort)Op.CMSG_PET_ACTION,
            BuildPetActionBody(petGuid, packedAction, targetGuid));
    public static byte[] BuildPetActionBody(ulong petGuid, uint packedAction, ulong targetGuid)
    {
        var w = new PacketWriter(20);
        w.WriteU64(petGuid); w.WriteU32(packedAction); w.WriteU64(targetGuid);
        return w.ToArray();
    }
    public void RequestPetInfo() => SendPacket((ushort)Op.CMSG_REQUEST_PET_INFO, ReadOnlySpan<byte>.Empty);
    public void PetStopAttack(ulong petGuid) => SendFullGuid(Op.CMSG_PET_STOP_ATTACK, petGuid);
    public void PetSetAction(ulong petGuid, IReadOnlyList<(uint Position, uint Packed)> entries)
        => SendPacket((ushort)Op.CMSG_PET_SET_ACTION, BuildPetSetActionBody(petGuid, entries));
    public static byte[] BuildPetSetActionBody(ulong petGuid,
        IReadOnlyList<(uint Position, uint Packed)> entries)
    {
        if (entries.Count is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(entries), "pet set-action carries one or two entries");
        var w = new PacketWriter(8 + entries.Count * 8);
        w.WriteU64(petGuid);
        foreach ((uint position, uint packed) in entries)
        { w.WriteU32(position); w.WriteU32(packed); }
        return w.ToArray();
    }
    public void PetCancelAura(ulong petGuid, uint spellId)
        => SendPacket((ushort)Op.CMSG_PET_CANCEL_AURA, BuildPetCancelAuraBody(petGuid, spellId));
    public static byte[] BuildPetCancelAuraBody(ulong petGuid, uint spellId)
    {
        var w = new PacketWriter(12);
        w.WriteU64(petGuid); w.WriteU32(spellId);
        return w.ToArray();
    }
    public void PetSpellAutocast(ulong petGuid, uint spellId, bool enabled)
        => SendPacket((ushort)Op.CMSG_PET_SPELL_AUTOCAST,
            BuildPetSpellAutocastBody(petGuid, spellId, enabled));
    public static byte[] BuildPetSpellAutocastBody(ulong petGuid, uint spellId, bool enabled)
    {
        var w = new PacketWriter(13);
        w.WriteU64(petGuid); w.WriteU32(spellId); w.WriteU8(enabled ? (byte)1 : (byte)0);
        return w.ToArray();
    }
    public void PetUnlearn(ulong petGuid) => SendFullGuid(Op.CMSG_PET_UNLEARN, petGuid);
    public void PetAbandon(ulong petGuid)
        => SendPacket((ushort)Op.CMSG_PET_ABANDON, BuildPetAbandonBody(petGuid));
    public static byte[] BuildPetAbandonBody(ulong petGuid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(petGuid);
        return w.ToArray();
    }
    public void PetRename(ulong petGuid, string name)
        => SendPacket((ushort)Op.CMSG_PET_RENAME, BuildPetRenameBody(petGuid, name));
    public static byte[] BuildPetRenameBody(ulong petGuid, string name)
    {
        var w = new PacketWriter(9 + System.Text.Encoding.UTF8.GetByteCount(name));
        w.WriteU64(petGuid); w.WriteCString(name);
        return w.ToArray();
    }
    public void ZoneUpdate(uint zoneId) =>
        SendPacket((ushort)Op.CMSG_ZONEUPDATE, BuildZoneUpdateBody(zoneId));

    /// <summary>CMSG_TOGGLE_PVP is a toggle verb with a deliberately empty body.</summary>
    public void TogglePvp() => SendPacket((ushort)Op.CMSG_TOGGLE_PVP, BuildTogglePvpBody());
    public static byte[] BuildTogglePvpBody() => [];

    /// <summary>The equipment-display preferences are also empty-bodied server-side flips.</summary>
    public void ToggleHelm() => SendPacket((ushort)Op.CMSG_TOGGLE_HELM, BuildToggleHelmBody());
    public void ToggleCloak() => SendPacket((ushort)Op.CMSG_TOGGLE_CLOAK, BuildToggleCloakBody());
    public static byte[] BuildToggleHelmBody() => [];
    public static byte[] BuildToggleCloakBody() => [];

    /// <summary>
    /// Report an AreaTrigger.dbc volume to the server. The trigger id is the
    /// entire 1.12 packet body; VMaNGOS validates the player's authoritative
    /// map/position and owns any resulting teleport.
    /// </summary>
    public void AreaTrigger(uint triggerId)
        => SendPacket((ushort)Op.CMSG_AREATRIGGER,
            AreaTriggerPackets.BuildReport(triggerId));

    /// <summary>
    /// Vanilla's administrator worldport packet. VMaNGOS checks account
    /// security before honoring it; MSUI uses it only to repair an impossible
    /// server map/position pair which matches an authored portal destination.
    /// </summary>
    public void WorldTeleport(uint mapId, Vector3 position, float orientation)
        => SendPacket((ushort)Op.CMSG_WORLD_TELEPORT,
            BuildWorldTeleportBody(MovementInfo.ClientUptimeMs(), mapId, position, orientation));

    public static byte[] BuildWorldTeleportBody(uint timestamp, uint mapId,
        Vector3 position, float orientation)
    {
        var w = new PacketWriter(24);
        w.WriteU32(timestamp);
        w.WriteU32(mapId);
        w.WriteVector3(position);
        w.WriteF32(orientation);
        return w.ToArray();
    }
    public static byte[] BuildZoneUpdateBody(uint zoneId)
    {
        var w = new PacketWriter(4); w.WriteU32(zoneId); return w.ToArray();
    }
    public void AttackSwing(ulong guid) => SendFullGuid(Op.CMSG_ATTACKSWING, guid);
    public void AttackStop() => SendPacket((ushort)Op.CMSG_ATTACKSTOP, ReadOnlySpan<byte>.Empty);
    public void SendChatSay(string text, uint language) =>
        SendChat(0 /* CHAT_MSG_SAY */, language, null, text);

    /// <summary>
    /// CMSG_MESSAGECHAT for any type. Wire body: uint32 type, uint32 language,
    /// then (WHISPER) the target name or (CHANNEL) the channel name as a cstring,
    /// then the message cstring. language is the character's faction tongue -
    /// VMaNGOS rejects a client-chosen Universal.
    /// </summary>
    public void SendChat(uint type, uint language, string? target, string text)
    {
        var w = new PacketWriter(System.Text.Encoding.UTF8.GetByteCount(text) + 24);
        w.WriteU32(type);
        w.WriteU32(language);
        if (target is not null) w.WriteCString(target);
        w.WriteCString(text);
        SendPacket((ushort)Op.CMSG_MESSAGECHAT, w.AsSpan());
    }

    public void JoinChannel(string name, string password) =>
        SendPacket((ushort)Op.CMSG_JOIN_CHANNEL, ChannelPackets.BuildJoin(name, password));
    public void LeaveChannel(string name) =>
        SendPacket((ushort)Op.CMSG_LEAVE_CHANNEL, ChannelPackets.BuildName(name));
    public void ChannelList(string name) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_LIST, ChannelPackets.BuildName(name));
    public void ChannelPassword(string name, string password) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_PASSWORD, ChannelPackets.BuildPair(name, password));
    public void ChannelSetOwner(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_SET_OWNER, ChannelPackets.BuildPair(name, player));
    public void ChannelOwner(string name) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_OWNER, ChannelPackets.BuildName(name));
    public void ChannelModerator(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_MODERATOR, ChannelPackets.BuildPair(name, player));
    public void ChannelUnmoderator(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_UNMODERATOR, ChannelPackets.BuildPair(name, player));
    public void ChannelMute(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_MUTE, ChannelPackets.BuildPair(name, player));
    public void ChannelUnmute(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_UNMUTE, ChannelPackets.BuildPair(name, player));
    public void ChannelInvite(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_INVITE, ChannelPackets.BuildPair(name, player));
    public void ChannelKick(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_KICK, ChannelPackets.BuildPair(name, player));
    public void ChannelBan(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_BAN, ChannelPackets.BuildPair(name, player));
    public void ChannelUnban(string name, string player) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_UNBAN, ChannelPackets.BuildPair(name, player));
    public void ChannelAnnouncements(string name) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_ANNOUNCEMENTS, ChannelPackets.BuildName(name));
    public void ChannelModerate(string name) =>
        SendPacket((ushort)Op.CMSG_CHANNEL_MODERATE, ChannelPackets.BuildName(name));

    public void PlayedTime() =>
        SendPacket((ushort)Op.CMSG_PLAYED_TIME, ReadOnlySpan<byte>.Empty);

    public void QueryTime() =>
        SendPacket((ushort)Op.CMSG_QUERY_TIME, ReadOnlySpan<byte>.Empty);

    public void RandomRoll(uint minimum, uint maximum)
    {
        var w = new PacketWriter(8);
        w.WriteU32(minimum);
        w.WriteU32(maximum);
        SendPacket((ushort)Op.MSG_RANDOM_ROLL, w.AsSpan());
    }

    /// <summary>
    /// CMSG_TEXT_EMOTE for a numbered emote (/wave, /dance, ...): u32 textEmoteId
    /// (EmoteCommandLaw's key - the same index GlobalStrings.lua's EMOTE&lt;N&gt;_*
    /// globals use), u32 emoteNum (unused here, 0 - the server picks its own
    /// flavour-text variant), u64 target guid (0 for an untargeted emote). The
    /// server builds the full chat line itself and relays it back as an ordinary
    /// SMSG_MESSAGECHAT (CHAT_MSG_TEXT_EMOTE), which the existing chat pipeline
    /// already displays - no receive-side change needed for the text.
    /// </summary>
    public void SendTextEmote(uint textEmoteId, ulong targetGuid)
    {
        var w = new PacketWriter(16);
        w.WriteU32(textEmoteId);
        w.WriteU32(0);
        w.WriteU64(targetGuid);
        SendPacket((ushort)Op.CMSG_TEXT_EMOTE, w.AsSpan());
    }
    public void SetSheathed(byte state)
    {
        var w = new PacketWriter(4); w.WriteU32(state);
        SendPacket((ushort)Op.CMSG_SETSHEATHED, w.AsSpan());
    }

    /// <summary>
    /// CMSG_STANDSTATECHANGE: u32 UnitStandStateType. This, not CMSG_TEXT_EMOTE,
    /// is what actually moves UNIT_FIELD_BYTES_1's StandState byte - confirmed
    /// against vmangos/core: WorldSession::HandleTextEmoteOpcode explicitly
    /// skips calling Unit::HandleEmote for the STATE_SLEEP/SIT/KNEEL/ONESHOT_NONE
    /// text ids, so /sit and /kneel typed as chat commands only ever produced
    /// the flavour text over that packet, never the pose.
    /// </summary>
    public void SendStandStateChange(UnitStandState state)
    {
        var w = new PacketWriter(4); w.WriteU32((uint)state);
        SendPacket((ushort)Op.CMSG_STANDSTATECHANGE, w.AsSpan());
    }

    public void CastSpell(uint spellId, ulong targetGuid)
    {
        byte[] body = BuildCastSpellBody(spellId, targetGuid);
        SendPacket((ushort)Op.CMSG_CAST_SPELL, body);
    }

    public static byte[] BuildCastSpellBody(uint spellId, ulong targetGuid)
    {
        var w = new PacketWriter(targetGuid == 0 ? 6 : 14);
        w.WriteU32(spellId);
        w.WriteU16(targetGuid == 0 ? (ushort)0 : (ushort)0x0002); // TARGET_FLAG_SELF / UNIT
        if (targetGuid != 0) w.WritePackedGuid(targetGuid);
        return w.ToArray();
    }

    public void CastSpellAtLocation(uint spellId, System.Numerics.Vector3 dest)
        => SendPacket((ushort)Op.CMSG_CAST_SPELL, BuildCastSpellAtLocationBody(spellId, dest));

    /// <summary>
    /// Ground-target cast: mask TARGET_FLAG_DEST_LOCATION (0x0040) then three raw floats.
    /// Byte shape per vmangos SpellCastTargets::read (SpellCastTargetsInfo.cpp:169-174) —
    /// the 1.12 dest block carries no transport guid.
    /// </summary>
    public static byte[] BuildCastSpellAtLocationBody(uint spellId, System.Numerics.Vector3 dest)
    {
        var w = new PacketWriter(18);
        w.WriteU32(spellId);
        w.WriteU16(0x0040);
        w.WriteF32(dest.X); w.WriteF32(dest.Y); w.WriteF32(dest.Z);
        return w.ToArray();
    }

    public void CastSpellOnGameObject(uint spellId, ulong gameObjectGuid)
        => SendPacket((ushort)Op.CMSG_CAST_SPELL,
            BuildCastSpellOnGameObjectBody(spellId, gameObjectGuid));

    public static byte[] BuildCastSpellOnGameObjectBody(uint spellId, ulong gameObjectGuid)
    {
        var w = new PacketWriter(14);
        w.WriteU32(spellId);
        // TARGET_FLAG_GAMEOBJECT alone. 0x4000 is the client-internal LOCKED
        // targeting-word bit; BindTarget consumes it and never writes it to the wire.
        w.WriteU16(0x0800);
        w.WritePackedGuid(gameObjectGuid);
        return w.ToArray();
    }

    public void CancelCast(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId);
        SendPacket((ushort)Op.CMSG_CANCEL_CAST, w.AsSpan());
    }

    public void CancelChannelling(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId);
        SendPacket((ushort)Op.CMSG_CANCEL_CHANNELLING, w.AsSpan());
    }

    public void CancelAura(uint spellId)
    {
        SendPacket((ushort)Op.CMSG_CANCEL_AURA, BuildCancelAuraBody(spellId));
    }

    public static byte[] BuildCancelAuraBody(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId); return w.ToArray();
    }

    public void CancelAutoRepeat()
        => SendPacket((ushort)Op.CMSG_CANCEL_AUTO_REPEAT_SPELL, ReadOnlySpan<byte>.Empty);

    public void StandStateChange(uint state)
        => SendPacket((ushort)Op.CMSG_STANDSTATECHANGE, BuildStandStateChangeBody(state));

    public static byte[] BuildStandStateChangeBody(uint state)
    {
        var w = new PacketWriter(4);
        w.WriteU32(state);
        return w.ToArray();
    }

    public void MountSpecial()
        => SendPacket((ushort)Op.CMSG_MOUNTSPECIAL_ANIM, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Abandon one complete skill line. There is no acknowledgement packet; the authoritative
    /// removal returns later through PLAYER_SKILL_INFO field updates.
    /// </summary>
    public void UnlearnSkill(uint skillId)
        => SendPacket((ushort)Op.CMSG_UNLEARN_SKILL, BuildUnlearnSkillBody(skillId));

    public static byte[] BuildUnlearnSkillBody(uint skillId)
    {
        var w = new PacketWriter(4);
        w.WriteU32(skillId);
        return w.ToArray();
    }

    public void SetActionButton(byte wireSlot, uint packed)
        => SendPacket((ushort)Op.CMSG_SET_ACTION_BUTTON, BuildSetActionButtonBody(wireSlot, packed));

    public static byte[] BuildSetActionButtonBody(byte wireSlot, uint packed)
    {
        var w = new PacketWriter(5);
        w.WriteU8(wireSlot);
        w.WriteU32(packed);
        return w.ToArray();
    }

    public void SendMovement(Op moveOp, MovementInfo info)
    {
        var w = new PacketWriter(32);
        info.Write(w);
        SendPacket((ushort)moveOp, w.AsSpan());
    }

    public void MoveSplineDone(MovementInfo info, uint splineId) =>
        SendPacket((ushort)Op.CMSG_MOVE_SPLINE_DONE,
            BuildMoveSplineDoneBody(info, splineId));

    public static byte[] BuildMoveSplineDoneBody(MovementInfo info, uint splineId)
    {
        var writer = new PacketWriter(40);
        info.Write(writer);
        writer.WriteU32(splineId);
        writer.WriteF32(1f); // real-client completion fraction; server reads and discards it
        return writer.ToArray();
    }

    public void CastSpellOnItem(uint spellId, ulong itemGuid)
        => SendPacket((ushort)Op.CMSG_CAST_SPELL, BuildCastSpellOnItemBody(spellId, itemGuid));

    public static byte[] BuildCastSpellOnItemBody(uint spellId, ulong itemGuid)
    {
        var w = new PacketWriter(14);
        w.WriteU32(spellId);
        w.WriteU16(0x0010); // TARGET_FLAG_ITEM
        w.WritePackedGuid(itemGuid);
        return w.ToArray();
    }

    /// <summary>
    /// Acknowledge SMSG_FORCE_MOVE_ROOT/UNROOT with the server's counter and the mover's
    /// resulting MovementInfo. A root apply must carry MOVEFLAG_ROOT and no moving bits.
    /// </summary>
    public void MoveRootAck(ulong guid, uint counter, bool rooted, MovementInfo info)
    {
        SendPacket((ushort)(rooted ? Op.CMSG_FORCE_MOVE_ROOT_ACK :
            Op.CMSG_FORCE_MOVE_UNROOT_ACK), BuildMoveRootAckBody(guid, counter, info));
    }

    public static byte[] BuildMoveRootAckBody(ulong guid, uint counter, MovementInfo info)
    {
        var w = new PacketWriter(48);
        w.WriteU64(guid);
        w.WriteU32(counter);
        info.Write(w);
        return w.ToArray();
    }

    public void SetFactionAtWar(uint reputationSlot, bool atWar)
        => SendPacket((ushort)Op.CMSG_SET_FACTION_ATWAR,
            ReputationFrameUiLaw.SlotAndFlagBody(reputationSlot, atWar));

    public void SetFactionInactive(uint reputationSlot, bool inactive)
        => SendPacket((ushort)Op.CMSG_SET_FACTION_INACTIVE,
            ReputationFrameUiLaw.SlotAndFlagBody(reputationSlot, inactive));

    public void SetWatchedFaction(int reputationSlot)
        => SendPacket((ushort)Op.CMSG_SET_WATCHED_FACTION,
            ReputationFrameUiLaw.WatchedBody(reputationSlot));

    public void MoveModeAck(ulong guid, MovementModeKind kind, uint counter, bool apply,
        MovementInfo info) => SendPacket((ushort)MovementModePackets.AckOpcode(kind, apply),
            BuildMoveModeAckBody(guid, counter, info, apply));

    public static byte[] BuildMoveModeAckBody(ulong guid, uint counter, MovementInfo info,
        bool apply)
    {
        var w = new PacketWriter(52);
        w.WriteU64(guid);
        w.WriteU32(counter);
        info.Write(w);
        w.WriteU32(apply ? 1u : 0u);
        return w.ToArray();
    }

    /// <summary>Acknowledge an addressed knockback with full GUID, counter and impulse.</summary>
    public void KnockbackAck(ulong guid, uint counter, MovementInfo info) =>
        SendPacket((ushort)Op.CMSG_MOVE_KNOCK_BACK_ACK, KnockbackPackets.BuildAck(guid, counter, info));

    /// <summary>Acknowledge an addressed force-speed change with full guid, live pose and echo.</summary>
    public void ForceSpeedChangeAck(ulong guid, MovementSpeedKind kind, uint counter,
        MovementInfo info, float speed) =>
        SendPacket((ushort)MovementSpeedPackets.AckOpcode(kind),
            BuildForceSpeedChangeAckBody(guid, counter, info, speed));

    public static byte[] BuildForceSpeedChangeAckBody(ulong guid, uint counter,
        MovementInfo info, float speed)
    {
        var w = new PacketWriter(52);
        w.WriteU64(guid);
        w.WriteU32(counter);
        info.Write(w);
        w.WriteF32(speed);
        return w.ToArray();
    }

    public void Ping(uint sequence, uint lastRttMs)
    {
        var w = new PacketWriter(8);
        w.WriteU32(sequence);
        w.WriteU32(lastRttMs);
        SendPacket((ushort)Op.CMSG_PING, w.AsSpan());
    }

    public void NameQuery(ulong guid) => SendFullGuid(Op.CMSG_NAME_QUERY, guid);
    public void FriendList() => SendPacket((ushort)Op.CMSG_FRIEND_LIST, ReadOnlySpan<byte>.Empty);
    public void AddFriend(string name)
    {
        var w = new PacketWriter(name.Length + 1); w.WriteCString(name);
        SendPacket((ushort)Op.CMSG_ADD_FRIEND, w.AsSpan());
    }
    public void DeleteFriend(ulong guid) => SendFullGuid(Op.CMSG_DEL_FRIEND, guid);
    public void Who(string name)
        => Who(SocialPackets.ParseWhoFilter(name));
    public void Who(SocialPackets.WhoRequest request) =>
        SendPacket((ushort)Op.CMSG_WHO, SocialPackets.BuildWhoBody(request));
    public void AddIgnore(string name)
    {
        var w = new PacketWriter(name.Length + 1); w.WriteCString(name);
        SendPacket((ushort)Op.CMSG_ADD_IGNORE, w.AsSpan());
    }
    public void DeleteIgnore(ulong guid) => SendFullGuid(Op.CMSG_DEL_IGNORE, guid);
    public void ChatIgnored(ulong senderGuid) =>
        SendPacket((ushort)Op.CMSG_CHAT_IGNORED, ChatPackets.BuildIgnoredBody(senderGuid));
    public void GroupInvite(string name)
        => SendPacket((ushort)Op.CMSG_GROUP_INVITE, BuildGroupInviteBody(name));
    public void GroupAccept()
        => SendPacket((ushort)Op.CMSG_GROUP_ACCEPT, BuildGroupAcceptBody());
    public void GroupDecline()
        => SendPacket((ushort)Op.CMSG_GROUP_DECLINE, BuildGroupDeclineBody());
    public void GroupUninvite(string name)
        => SendPacket((ushort)Op.CMSG_GROUP_UNINVITE, BuildGroupUninviteBody(name));
    public void GroupUninviteGuid(ulong guid)
        => SendPacket((ushort)Op.CMSG_GROUP_UNINVITE_GUID, BuildGroupUninviteGuidBody(guid));
    public void GroupSetLeader(ulong guid)
        => SendPacket((ushort)Op.CMSG_GROUP_SET_LEADER, BuildGroupSetLeaderBody(guid));
    public void GroupLootMethod(uint method, ulong lootMaster, uint threshold)
        => SendPacket((ushort)Op.CMSG_LOOT_METHOD,
            BuildGroupLootMethodBody(method, lootMaster, threshold));
    public void GroupDisband()
        => SendPacket((ushort)Op.CMSG_GROUP_DISBAND, BuildGroupDisbandBody());
    public void RequestPartyMemberStats(ulong guid)
        => SendPacket((ushort)Op.CMSG_REQUEST_PARTY_MEMBER_STATS,
            BuildRequestPartyMemberStatsBody(guid));
    public void GroupChangeSubGroup(string name, byte groupNumber)
        => SendPacket((ushort)Op.CMSG_GROUP_CHANGE_SUB_GROUP,
            BuildGroupChangeSubGroupBody(name, groupNumber));
    public void GroupSwapSubGroup(string name, string swapWith)
        => SendPacket((ushort)Op.CMSG_GROUP_SWAP_SUB_GROUP,
            BuildGroupSwapSubGroupBody(name, swapWith));
    public void GroupRaidConvert()
        => SendPacket((ushort)Op.CMSG_GROUP_RAID_CONVERT, BuildGroupRaidConvertBody());
    public void GroupAssistantLeader(ulong guid, bool grant)
        => SendPacket((ushort)Op.CMSG_GROUP_ASSISTANT_LEADER,
            BuildGroupAssistantLeaderBody(guid, grant));
    public void GroupMinimapPing(float x, float y)
        => SendPacket((ushort)Op.MSG_MINIMAP_PING, BuildGroupMinimapPingBody(x, y));
    public void SetRaidTarget(byte icon, ulong guid)
        => SendPacket((ushort)Op.MSG_RAID_TARGET_UPDATE, BuildRaidTargetSetBody(icon, guid));
    public void RequestRaidTargets()
        => SendPacket((ushort)Op.MSG_RAID_TARGET_UPDATE, BuildRaidTargetRequestBody());
    public void StartReadyCheck()
        => SendPacket((ushort)Op.MSG_RAID_READY_CHECK, BuildReadyCheckStartBody());
    public void AnswerReadyCheck(bool ready)
        => SendPacket((ushort)Op.MSG_RAID_READY_CHECK, BuildReadyCheckAnswerBody(ready));

    public static byte[] BuildGroupInviteBody(string name) => BuildGroupNameBody(name);
    public static byte[] BuildGroupAcceptBody() => [];
    public static byte[] BuildGroupDeclineBody() => [];
    public static byte[] BuildGroupUninviteBody(string name) => BuildGroupNameBody(name);
    public static byte[] BuildGroupUninviteGuidBody(ulong guid) => BuildGroupGuidBody(guid);
    public static byte[] BuildGroupSetLeaderBody(ulong guid) => BuildGroupGuidBody(guid);
    public static byte[] BuildRequestPartyMemberStatsBody(ulong guid) => BuildGroupGuidBody(guid);
    public static byte[] BuildGroupDisbandBody() => [];
    public static byte[] BuildGroupRaidConvertBody() => [];
    public static byte[] BuildReadyCheckStartBody() => [];

    public static byte[] BuildGroupLootMethodBody(uint method, ulong lootMaster, uint threshold)
    {
        var w = new PacketWriter(16);
        w.WriteU32(method);
        w.WriteU64(lootMaster);
        w.WriteU32(threshold);
        return w.ToArray();
    }

    public static byte[] BuildGroupChangeSubGroupBody(string name, byte groupNumber)
    {
        var w = new PacketWriter(name.Length + 2);
        w.WriteCString(name);
        w.WriteU8(groupNumber);
        return w.ToArray();
    }

    public static byte[] BuildGroupSwapSubGroupBody(string name, string swapWith)
    {
        var w = new PacketWriter(name.Length + swapWith.Length + 2);
        w.WriteCString(name);
        w.WriteCString(swapWith);
        return w.ToArray();
    }

    public static byte[] BuildGroupAssistantLeaderBody(ulong guid, bool grant)
    {
        var w = new PacketWriter(9);
        w.WriteU64(guid);
        w.WriteU8(grant ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    public static byte[] BuildGroupMinimapPingBody(float x, float y)
    {
        var w = new PacketWriter(8);
        w.WriteF32(x);
        w.WriteF32(y);
        return w.ToArray();
    }

    public static byte[] BuildRaidTargetSetBody(byte icon, ulong guid)
    {
        var w = new PacketWriter(9);
        w.WriteU8(icon);
        w.WriteU64(guid);
        return w.ToArray();
    }

    public static byte[] BuildRaidTargetRequestBody() => [0xff];
    public static byte[] BuildReadyCheckAnswerBody(bool ready) => [ready ? (byte)1 : (byte)0];

    private static byte[] BuildGroupNameBody(string name)
    {
        var w = new PacketWriter(name.Length + 1);
        w.WriteCString(name);
        return w.ToArray();
    }

    private static byte[] BuildGroupGuidBody(ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(guid);
        return w.ToArray();
    }
    public void InitiateTrade(ulong guid) => SendFullGuid(Op.CMSG_INITIATE_TRADE, guid);
    public void BeginTrade() => SendPacket((ushort)Op.CMSG_BEGIN_TRADE, ReadOnlySpan<byte>.Empty);
    public void BusyTrade() =>
        SendPacket((ushort)Op.CMSG_BUSY_TRADE, BuildTradeDeclineBody());
    public void IgnoreTrade() =>
        SendPacket((ushort)Op.CMSG_IGNORE_TRADE, BuildTradeDeclineBody());
    public static byte[] BuildTradeDeclineBody() => [];
    public void AcceptTrade()
        => SendPacket((ushort)Op.CMSG_ACCEPT_TRADE, BuildAcceptTradeBody());
    public void UnacceptTrade() => SendPacket((ushort)Op.CMSG_UNACCEPT_TRADE, ReadOnlySpan<byte>.Empty);
    public void CancelTrade() => SendPacket((ushort)Op.CMSG_CANCEL_TRADE, ReadOnlySpan<byte>.Empty);
    public void SetTradeItem(byte tradeSlot, byte bag, byte slot)
        => SendPacket((ushort)Op.CMSG_SET_TRADE_ITEM, BuildSetTradeItemBody(tradeSlot, bag, slot));
    public void ClearTradeItem(byte tradeSlot)
    {
        var w = new PacketWriter(1); w.WriteU8(tradeSlot);
        SendPacket((ushort)Op.CMSG_CLEAR_TRADE_ITEM, w.AsSpan());
    }
    public void SetTradeGold(uint gold)
        => SendPacket((ushort)Op.CMSG_SET_TRADE_GOLD, BuildSetTradeGoldBody(gold));
    public void DuelAccepted(ulong arbiter)
        => SendPacket((ushort)Op.CMSG_DUEL_ACCEPTED, DuelPackets.BuildReplyBody(arbiter));
    public void DuelCancelled(ulong arbiter)
        => SendPacket((ushort)Op.CMSG_DUEL_CANCELLED, DuelPackets.BuildReplyBody(arbiter));
    public static byte[] BuildAcceptTradeBody()
    { var w = new PacketWriter(4); w.WriteU32(1); return w.ToArray(); }
    public static byte[] BuildSetTradeItemBody(byte tradeSlot, byte bag, byte slot)
    { var w = new PacketWriter(3); w.WriteU8(tradeSlot); w.WriteU8(bag); w.WriteU8(slot); return w.ToArray(); }
    public static byte[] BuildSetTradeGoldBody(uint gold)
    { var w = new PacketWriter(4); w.WriteU32(gold); return w.ToArray(); }
    public void GmTicketCreate(byte type, uint map, Vector3 position, string message)
        => SendPacket((ushort)Op.CMSG_GMTICKET_CREATE, BuildGmTicketCreateBody(type, map, position, message));
    public void GmTicketUpdate(byte type, string message)
    {
        var w = new PacketWriter(message.Length + 2); w.WriteU8(type); w.WriteCString(message);
        SendPacket((ushort)Op.CMSG_GMTICKET_UPDATETEXT, w.AsSpan());
    }
    public void GmTicketGet() => SendPacket((ushort)Op.CMSG_GMTICKET_GETTICKET, ReadOnlySpan<byte>.Empty);
    public void GmTicketDelete() => SendPacket((ushort)Op.CMSG_GMTICKET_DELETETICKET, ReadOnlySpan<byte>.Empty);
    public void GmTicketSystemStatus() => SendPacket((ushort)Op.CMSG_GMTICKET_SYSTEMSTATUS, ReadOnlySpan<byte>.Empty);
    public static byte[] BuildGmTicketCreateBody(byte type, uint map, Vector3 position, string message)
    {
        var w = new PacketWriter(message.Length + 24); w.WriteU8(type); w.WriteU32(map);
        w.WriteF32(position.X); w.WriteF32(position.Y); w.WriteF32(position.Z);
        w.WriteCString(message); w.WriteCString(""); return w.ToArray();
    }

    /// <summary>CMSG_CREATURE_QUERY — resolve a creature template's name/type. Body = entry(u32) + guid(u64).</summary>
    public void CreatureQuery(uint entry, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(entry);
        w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_CREATURE_QUERY, w.AsSpan());
    }

    /// <summary>CMSG_PET_NAME_QUERY — pet number followed by the pet's full GUID.</summary>
    public void PetNameQuery(uint petNumber, ulong guid)
        => SendPacket((ushort)Op.CMSG_PET_NAME_QUERY, BuildPetNameQueryBody(petNumber, guid));

    public static byte[] BuildPetNameQueryBody(uint petNumber, ulong guid)
    {
        var writer = new PacketWriter(12);
        writer.WriteU32(petNumber);
        writer.WriteU64(guid);
        return writer.ToArray();
    }

    /// <summary>CMSG_GAMEOBJECT_QUERY -- resolve a streamed object's type-specific template data.</summary>
    public void GameObjectQuery(uint entry, ulong guid)
    {
        SendPacket((ushort)Op.CMSG_GAMEOBJECT_QUERY, BuildGameObjectQueryBody(entry, guid));
    }

    public static byte[] BuildGameObjectQueryBody(uint entry, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(entry); w.WriteU64(guid);
        return w.ToArray();
    }

    public void ItemQuery(uint entry, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(entry); w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_ITEM_QUERY_SINGLE, w.AsSpan());
    }

    public void GossipHello(ulong guid) => SendFullGuid(Op.CMSG_GOSSIP_HELLO, guid);
    public void GameObjectUse(ulong guid) => SendFullGuid(Op.CMSG_GAMEOBJ_USE, guid);
    public void TaxiNodeStatusQuery(ulong guid) => SendFullGuid(Op.CMSG_TAXINODE_STATUS_QUERY, guid);
    public void TaxiQueryAvailableNodes(ulong guid) => SendFullGuid(Op.CMSG_TAXIQUERYAVAILABLENODES, guid);
    public void ActivateTaxi(ulong guid, uint sourceNode, uint destinationNode) =>
        SendPacket((ushort)Op.CMSG_ACTIVATETAXI, BuildActivateTaxiBody(guid, sourceNode, destinationNode));
    public static byte[] BuildActivateTaxiBody(ulong guid, uint sourceNode, uint destinationNode)
    {
        var w = new PacketWriter(16); w.WriteU64(guid); w.WriteU32(sourceNode); w.WriteU32(destinationNode); return w.ToArray();
    }
    public void ActivateTaxiExpress(ulong guid, uint totalCost, IReadOnlyList<uint> nodes) =>
        SendPacket((ushort)Op.CMSG_ACTIVATETAXIEXPRESS,
            TaxiPackets.BuildActivateExpressBody(guid, totalCost, nodes));
    public void RepopRequest() => SendPacket((ushort)Op.CMSG_REPOP_REQUEST, ReadOnlySpan<byte>.Empty);
    public void CorpseQuery() => SendPacket((ushort)Op.MSG_CORPSE_QUERY, ReadOnlySpan<byte>.Empty);
    public void ReclaimCorpse(ulong corpseGuid) => SendPacket((ushort)Op.CMSG_RECLAIM_CORPSE, BuildGuidBody(corpseGuid));
    public void SpiritHealerActivate(ulong healerGuid) => SendPacket((ushort)Op.CMSG_SPIRIT_HEALER_ACTIVATE, BuildGuidBody(healerGuid));
    public void ResurrectResponse(ulong casterGuid, bool accept)
        => SendPacket((ushort)Op.CMSG_RESURRECT_RESPONSE, BuildResurrectResponseBody(casterGuid, accept));
    public void PageTextQuery(uint pageId, ulong guid)
        => SendPacket((ushort)Op.CMSG_PAGE_TEXT_QUERY, BuildPageTextQueryBody(pageId, guid));

    public static byte[] BuildGameObjectUseBody(ulong guid) => BuildGuidBody(guid);
    public static byte[] BuildReclaimCorpseBody(ulong guid) => BuildGuidBody(guid);
    public static byte[] BuildSpiritHealerBody(ulong guid) => BuildGuidBody(guid);
    public static byte[] BuildResurrectResponseBody(ulong guid, bool accept)
    { var w = new PacketWriter(9); w.WriteU64(guid); w.WriteU8(accept ? (byte)1 : (byte)0); return w.ToArray(); }
    public static byte[] BuildPageTextQueryBody(uint pageId, ulong guid)
    {
        var w = new PacketWriter(12); w.WriteU32(pageId); w.WriteU64(guid); return w.ToArray();
    }

    public void GossipSelect(ulong guid, uint listId, string? code = null)
    {
        var w = new PacketWriter(16 + (code?.Length ?? 0));
        w.WriteFullGuid(guid);
        w.WriteU32(listId);
        if (!string.IsNullOrEmpty(code)) w.WriteCString(code);
        SendPacket((ushort)Op.CMSG_GOSSIP_SELECT_OPTION, w.AsSpan());
    }

    public void TrainerList(ulong guid)
    {
        SendPacket((ushort)Op.CMSG_TRAINER_LIST, BuildTrainerListBody(guid));
    }
    public void BinderActivate(ulong guid) => SendPacket((ushort)Op.CMSG_BINDER_ACTIVATE, BuildGuidBody(guid));
    public static byte[] BuildBinderBody(ulong guid) => BuildGuidBody(guid);

    public void TrainerBuySpell(ulong guid, uint spellId)
    {
        SendPacket((ushort)Op.CMSG_TRAINER_BUY_SPELL, BuildTrainerBuyBody(guid, spellId));
    }

    public void LearnTalent(uint talentId, uint requestedRank)
        => SendPacket((ushort)Op.CMSG_LEARN_TALENT, BuildLearnTalentBody(talentId, requestedRank));

    public void ConfirmTalentWipe(ulong trainerGuid)
        => SendPacket((ushort)Op.MSG_TALENT_WIPE_CONFIRM, BuildTalentWipeBody(trainerGuid));

    public static byte[] BuildLearnTalentBody(uint talentId, uint requestedRank)
    { var w = new PacketWriter(8); w.WriteU32(talentId); w.WriteU32(requestedRank); return w.ToArray(); }

    public static byte[] BuildTalentWipeBody(ulong trainerGuid)
    { var w = new PacketWriter(8); w.WriteU64(trainerGuid); return w.ToArray(); }

    public void BankerActivate(ulong guid)
        => SendPacket((ushort)Op.CMSG_BANKER_ACTIVATE, BuildBankGuidBody(guid));

    public void BuyBankSlot(ulong guid)
        => SendPacket((ushort)Op.CMSG_BUY_BANK_SLOT, BuildBankGuidBody(guid));

    public static byte[] BuildBankGuidBody(ulong guid) => BuildGuidBody(guid);

    /// <summary>CMSG_AUTOBANK_ITEM — deposit the item at the source wire position;
    /// the server selects the first valid bank destination.</summary>
    public void AutoBankItem(byte sourceBag, byte sourceSlot)
        => SendPacket((ushort)Op.CMSG_AUTOBANK_ITEM,
            BuildAutoBankItemBody(sourceBag, sourceSlot));

    /// <summary>CMSG_AUTOSTORE_BANK_ITEM — move the bank item at the source wire
    /// position into inventory; the server selects the destination.</summary>
    public void AutostoreBankItem(byte sourceBag, byte sourceSlot)
        => SendPacket((ushort)Op.CMSG_AUTOSTORE_BANK_ITEM,
            BuildAutostoreBankItemBody(sourceBag, sourceSlot));

    public static byte[] BuildAutoBankItemBody(byte sourceBag, byte sourceSlot) =>
        [sourceBag, sourceSlot];

    public static byte[] BuildAutostoreBankItemBody(byte sourceBag, byte sourceSlot) =>
        [sourceBag, sourceSlot];

    public void GetMailList(ulong mailboxGuid)
        => SendPacket((ushort)Op.CMSG_GET_MAIL_LIST, BuildMailGuidBody(mailboxGuid));

    public void SendMail(ulong mailboxGuid, string receiver, string subject, string body,
        ulong itemGuid, uint money, uint cod)
        => SendPacket((ushort)Op.CMSG_SEND_MAIL,
            BuildSendMailBody(mailboxGuid, receiver, subject, body, itemGuid, money, cod));

    public void MailTakeMoney(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_TAKE_MONEY, BuildMailActionBody(mailboxGuid, mailId));

    public void MailTakeItem(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_TAKE_ITEM, BuildMailActionBody(mailboxGuid, mailId));

    public void MailMarkAsRead(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_MARK_AS_READ, BuildMailActionBody(mailboxGuid, mailId));

    public void MailReturn(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_RETURN_TO_SENDER, BuildMailActionBody(mailboxGuid, mailId));

    public void MailDelete(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_DELETE, BuildMailActionBody(mailboxGuid, mailId));

    public void MailCreateTextItem(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_CREATE_TEXT_ITEM,
            BuildMailCreateTextItemBody(mailboxGuid, mailId));

    public void ItemTextQuery(uint textId, uint mailId)
        => SendPacket((ushort)Op.CMSG_ITEM_TEXT_QUERY, BuildItemTextQueryBody(textId, mailId));

    public void QueryNextMailTime()
        => SendPacket((ushort)Op.MSG_QUERY_NEXT_MAIL_TIME, ReadOnlySpan<byte>.Empty);

    public static byte[] BuildMailGuidBody(ulong mailboxGuid) => BuildGuidBody(mailboxGuid);

    public static byte[] BuildMailActionBody(ulong mailboxGuid, uint mailId)
    { var w = new PacketWriter(12); w.WriteU64(mailboxGuid); w.WriteU32(mailId); return w.ToArray(); }

    public static byte[] BuildMailCreateTextItemBody(ulong mailboxGuid, uint mailId)
    { var w = new PacketWriter(16); w.WriteU64(mailboxGuid); w.WriteU32(mailId); w.WriteU32(0); return w.ToArray(); }

    public static byte[] BuildItemTextQueryBody(uint textId, uint mailId)
    { var w = new PacketWriter(12); w.WriteU32(textId); w.WriteU32(mailId); w.WriteU32(0); return w.ToArray(); }

    public static byte[] BuildSendMailBody(ulong mailboxGuid, string receiver, string subject, string body,
        ulong itemGuid, uint money, uint cod)
    {
        var w = new PacketWriter(49 + receiver.Length + subject.Length + body.Length);
        w.WriteU64(mailboxGuid); w.WriteCString(receiver); w.WriteCString(subject); w.WriteCString(body);
        w.WriteU32(41); // normal stationery
        w.WriteU32(0);  // package
        w.WriteU64(itemGuid); w.WriteU32(money); w.WriteU32(cod);
        w.WriteU64(0); w.WriteU8(0); // build-5875 trailing constants
        return w.ToArray();
    }

    public void AuctionHello(ulong guid) => SendPacket((ushort)Op.MSG_AUCTION_HELLO, BuildAuctionGuidBody(guid));
    public void AuctionBrowse(ulong guid, in AuctionBrowseQuery query)
        => SendPacket((ushort)Op.CMSG_AUCTION_LIST_ITEMS, BuildAuctionBrowseBody(guid, query));
    public void AuctionOwnerList(ulong guid, uint listFrom)
        => SendPacket((ushort)Op.CMSG_AUCTION_LIST_OWNER_ITEMS, BuildAuctionPageBody(guid, listFrom));
    public void AuctionBidderList(ulong guid, uint listFrom, IReadOnlyList<uint>? refreshIds = null)
        => SendPacket((ushort)Op.CMSG_AUCTION_LIST_BIDDER_ITEMS, BuildAuctionBidderBody(guid, listFrom, refreshIds));
    public void AuctionBid(ulong guid, uint id, uint price)
        => SendPacket((ushort)Op.CMSG_AUCTION_PLACE_BID, BuildAuctionBidBody(guid, id, price));
    public void AuctionCancel(ulong guid, uint id)
        => SendPacket((ushort)Op.CMSG_AUCTION_REMOVE_ITEM, BuildAuctionPageBody(guid, id));
    public void AuctionSell(ulong guid, ulong item, uint bid, uint buyout, uint durationMinutes)
        => SendPacket((ushort)Op.CMSG_AUCTION_SELL_ITEM, BuildAuctionSellBody(guid, item, bid, buyout, durationMinutes));

    public static byte[] BuildAuctionGuidBody(ulong guid) => BuildGuidBody(guid);
    public static byte[] BuildAuctionPageBody(ulong guid, uint value)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(value); return w.ToArray(); }
    public static byte[] BuildAuctionBidBody(ulong guid, uint id, uint price)
    { var w = new PacketWriter(16); w.WriteU64(guid); w.WriteU32(id); w.WriteU32(price); return w.ToArray(); }
    public static byte[] BuildAuctionSellBody(ulong guid, ulong item, uint bid, uint buyout, uint durationMinutes)
    { var w = new PacketWriter(32); w.WriteU64(guid); w.WriteU64(item); w.WriteU32(bid); w.WriteU32(buyout); w.WriteU32(durationMinutes); return w.ToArray(); }
    /// <summary>
    /// CMSG_AUCTION_LIST_BIDDER_ITEMS (vmangos AuctionListBidderItem::ReadFromWorldPacket):
    /// u64 auctioneer, u32 listfrom, u32 count, count × u32 auctionId. The count word is
    /// ALWAYS present — the old 12-byte page body under-read it, vmangos's read check refused
    /// the packet, and the Bids tab could never be answered. The id list is a refresh set
    /// (the server emits those rows first, then every auction the player holds the bid on).
    /// </summary>
    public static byte[] BuildAuctionBidderBody(ulong guid, uint listFrom, IReadOnlyList<uint>? refreshIds)
    {
        int count = refreshIds?.Count ?? 0;
        var w = new PacketWriter(16 + 4 * count);
        w.WriteU64(guid); w.WriteU32(listFrom); w.WriteU32((uint)count);
        for (int i = 0; i < count; i++) w.WriteU32(refreshIds![i]);
        return w.ToArray();
    }

    /// <summary>
    /// CMSG_AUCTION_LIST_ITEMS (vmangos AuctionListItems::ReadFromWorldPacket): u64 auctioneer,
    /// u32 listfrom (a ROW offset — page × 50, not a page index), cstring name, u8 levelmin,
    /// u8 levelmax, u32 slotId, u32 mainCategory, u32 subCategory, u32 quality, u8 usable.
    /// 0xFFFFFFFF / 0 are the "no filter" sentinels; quality is a MINIMUM; the level pair gates
    /// RequiredLevel; usable drops what the actor's CanUseItem refuses.
    /// </summary>
    public static byte[] BuildAuctionBrowseBody(ulong guid, in AuctionBrowseQuery q)
    {
        var w = new PacketWriter(42 + q.Search.Length); w.WriteU64(guid); w.WriteU32(q.ListFrom);
        w.WriteCString(q.Search);
        w.WriteU8(q.LevelMin); w.WriteU8(q.LevelMax); w.WriteU32(q.InventoryType);
        w.WriteU32(q.ItemClass); w.WriteU32(q.Subclass); w.WriteU32(q.Quality);
        w.WriteU8(q.UsableOnly ? (byte)1 : (byte)0); return w.ToArray();
    }

    public static byte[] BuildTrainerListBody(ulong guid)
    { var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray(); }

    public static byte[] BuildTrainerBuyBody(ulong guid, uint spellId)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(spellId); return w.ToArray(); }

    public void QuestgiverStatus(ulong guid) => SendPacket((ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY, BuildGuidBody(guid));
    public static byte[] BuildQuestQueryBody(uint questId)
    { var w = new PacketWriter(4); w.WriteU32(questId); return w.ToArray(); }
    public void QuestQuery(uint questId) => SendPacket((ushort)Op.CMSG_QUEST_QUERY, BuildQuestQueryBody(questId));
    public void QuestgiverHello(ulong guid) => SendPacket((ushort)Op.CMSG_QUESTGIVER_HELLO, BuildGuidBody(guid));
    public void QuestgiverQuery(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverAccept(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverComplete(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_COMPLETE_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverRequestReward(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_REQUEST_REWARD, BuildQuestGuidBody(guid, questId));
    public void QuestgiverChooseReward(ulong guid, uint questId, uint choice)
    { var w = new PacketWriter(16); w.WriteU64(guid); w.WriteU32(questId); w.WriteU32(choice); SendPacket((ushort)Op.CMSG_QUESTGIVER_CHOOSE_REWARD, w.AsSpan()); }
    public void QuestLogRemove(byte slot)
    { var w = new PacketWriter(1); w.WriteU8(slot); SendPacket((ushort)Op.CMSG_QUESTLOG_REMOVE_QUEST, w.AsSpan()); }
    public static byte[] BuildQuestLogSwapBody(byte firstSlot, byte secondSlot)
        => [firstSlot, secondSlot];
    public void QuestLogSwap(byte firstSlot, byte secondSlot)
        => SendPacket((ushort)Op.CMSG_QUESTLOG_SWAP_QUEST,
            BuildQuestLogSwapBody(firstSlot, secondSlot));

    public static byte[] BuildQuestGuidBody(ulong guid, uint questId)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(questId); return w.ToArray(); }

    private static byte[] BuildGuidBody(ulong guid)
    { var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray(); }

    public void NpcTextQuery(uint textId, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(textId);
        w.WriteFullGuid(guid);
        SendPacket((ushort)Op.CMSG_NPC_TEXT_QUERY, w.AsSpan());
    }

    public void ListInventory(ulong guid)
        => SendPacket((ushort)Op.CMSG_LIST_INVENTORY, BuildListInventoryBody(guid));
    public static byte[] BuildListInventoryBody(ulong guid)
    {
        var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray();
    }
    public void BuyItem(ulong vendorGuid, uint itemId, byte count)
        => SendPacket((ushort)Op.CMSG_BUY_ITEM, BuildBuyItemBody(vendorGuid, itemId, count));
    public static byte[] BuildBuyItemBody(ulong vendorGuid, uint itemId, byte count)
    {
        var w = new PacketWriter(14); w.WriteU64(vendorGuid); w.WriteU32(itemId);
        w.WriteU8(count); w.WriteU8(0); return w.ToArray();
    }
    public void BuyItemInSlot(ulong vendorGuid, uint itemId, ulong bagGuid, byte slot, byte count)
        => SendPacket((ushort)Op.CMSG_BUY_ITEM_IN_SLOT, BuildBuyItemInSlotBody(vendorGuid, itemId, bagGuid, slot, count));
    public static byte[] BuildBuyItemInSlotBody(ulong vendorGuid, uint itemId, ulong bagGuid, byte slot, byte count)
    {
        var w = new PacketWriter(22); w.WriteU64(vendorGuid); w.WriteU32(itemId);
        w.WriteU64(bagGuid); w.WriteU8(slot); w.WriteU8(count); return w.ToArray();
    }
    public void SellItem(ulong vendorGuid, ulong itemGuid, byte count)
        => SendPacket((ushort)Op.CMSG_SELL_ITEM,
            BuildSellItemBody(vendorGuid, itemGuid, count));
    public static byte[] BuildSellItemBody(ulong vendorGuid, ulong itemGuid, byte count)
    {
        var w = new PacketWriter(17); w.WriteU64(vendorGuid); w.WriteU64(itemGuid);
        w.WriteU8(count); return w.ToArray();
    }
    public void BuybackItem(ulong vendorGuid, uint slot)
        => SendPacket((ushort)Op.CMSG_BUYBACK_ITEM,
            BuildBuybackItemBody(vendorGuid, slot));
    public static byte[] BuildBuybackItemBody(ulong vendorGuid, uint absoluteSlot)
    {
        var w = new PacketWriter(12); w.WriteU64(vendorGuid); w.WriteU32(absoluteSlot);
        return w.ToArray();
    }
    public void RepairItem(ulong vendorGuid, ulong itemGuid)
        => SendPacket((ushort)Op.CMSG_REPAIR_ITEM,
            BuildRepairItemBody(vendorGuid, itemGuid));
    public static byte[] BuildRepairItemBody(ulong vendorGuid, ulong itemGuid)
    {
        var w = new PacketWriter(16); w.WriteU64(vendorGuid); w.WriteU64(itemGuid);
        return w.ToArray();
    }

    public void UseItem(byte bag, byte slot, byte spellSlot, ulong targetGuid = 0,
        System.Numerics.Vector3? destination = null)
        => SendPacket((ushort)Op.CMSG_USE_ITEM, BuildUseItemBody(bag, slot, spellSlot, targetGuid, destination));

    public static byte[] BuildUseItemBody(byte bag, byte slot, byte spellSlot, ulong targetGuid = 0,
        System.Numerics.Vector3? destination = null)
    {
        var w = new PacketWriter(26);
        w.WriteU8(bag); w.WriteU8(slot); w.WriteU8(spellSlot);
        w.WriteU16((ushort)((targetGuid != 0 ? 0x0002 : 0) | (destination.HasValue ? 0x0040 : 0)));
        if (targetGuid != 0) w.WritePackedGuid(targetGuid);
        if (destination is { } point)
        {
            w.WriteF32(point.X); w.WriteF32(point.Y); w.WriteF32(point.Z);
        }
        return w.ToArray();
    }

    public void ReadItem(byte bag, byte slot)
        => SendPacket((ushort)Op.CMSG_READ_ITEM, BuildReadItemBody(bag, slot));

    public static byte[] BuildReadItemBody(byte bag, byte slot) => [bag, slot];

    public void WrapItem(byte paperBag, byte paperSlot, byte itemBag, byte itemSlot)
        => SendPacket((ushort)Op.CMSG_WRAP_ITEM, [paperBag, paperSlot, itemBag, itemSlot]);

    public void OpenItem(byte bag, byte slot)
        => SendPacket((ushort)Op.CMSG_OPEN_ITEM, BuildOpenItemBody(bag, slot));

    public static byte[] BuildOpenItemBody(byte bag, byte slot) => [bag, slot];

    public void DestroyItem(byte bag, byte slot, byte count)
        => SendPacket((ushort)Op.CMSG_DESTROYITEM, BuildDestroyItemBody(bag, slot, count));

    public static byte[] BuildDestroyItemBody(byte bag, byte slot, byte count) =>
        [bag, slot, count, 0, 0, 0];

    public void AutoEquipItem(byte bag, byte slot)
    {
        SendPacket((ushort)Op.CMSG_AUTOEQUIP_ITEM, BuildAutoEquipBody(bag, slot));
    }

    /// <summary>CMSG_AUTOSTORE_BAG_ITEM — move the source item into the named equipped bag;
    /// the server selects a compatible destination slot.</summary>
    public void AutostoreBagItem(byte sourceBag, byte sourceSlot, byte destinationBag)
        => SendPacket((ushort)Op.CMSG_AUTOSTORE_BAG_ITEM,
            BuildAutostoreBagItemBody(sourceBag, sourceSlot, destinationBag));

    public void SwapInventoryItems(byte sourceSlot, byte destinationSlot)
    {
        SendPacket((ushort)Op.CMSG_SWAP_INV_ITEM, BuildSwapInventoryBody(sourceSlot, destinationSlot));
    }

    public void SwapItems(byte destinationBag, byte destinationSlot, byte sourceBag, byte sourceSlot)
    {
        SendPacket((ushort)Op.CMSG_SWAP_ITEM,
            BuildSwapItemsBody(destinationBag, destinationSlot, sourceBag, sourceSlot));
    }

    public void SetAmmo(uint entry)
    {
        SendPacket((ushort)Op.CMSG_SET_AMMO, BuildSetAmmoBody(entry));
    }

    public void SplitItem(byte sourceBag, byte sourceSlot, byte destinationBag, byte destinationSlot,
        byte count)
    {
        SendPacket((ushort)Op.CMSG_SPLIT_ITEM,
            BuildSplitItemBody(sourceBag, sourceSlot, destinationBag, destinationSlot, count));
    }

    public static byte[] BuildAutoEquipBody(byte bag, byte slot) => [bag, slot];
    public static byte[] BuildAutostoreBagItemBody(byte sourceBag, byte sourceSlot,
        byte destinationBag) => [sourceBag, sourceSlot, destinationBag];
    public static byte[] BuildSetAmmoBody(uint entry) => BitConverter.GetBytes(entry);
    public static byte[] BuildSwapInventoryBody(byte sourceSlot, byte destinationSlot) => [sourceSlot, destinationSlot];
    public static byte[] BuildSwapItemsBody(byte destinationBag, byte destinationSlot, byte sourceBag, byte sourceSlot) =>
        [destinationBag, destinationSlot, sourceBag, sourceSlot];
    public static byte[] BuildSplitItemBody(byte sourceBag, byte sourceSlot, byte destinationBag,
        byte destinationSlot, byte count) =>
        [sourceBag, sourceSlot, destinationBag, destinationSlot, count];

    /// <summary>CMSG_LOOT — request a corpse's loot window. Body = the full 8-byte guid
    /// (vmangos Server/Packets/Loot.cpp:8-11; GameObjects use CMSG_GAMEOBJ_USE instead).</summary>
    public void Loot(ulong guid) => SendPacket((ushort)Op.CMSG_LOOT, BuildLootGuidBody(guid));

    /// <summary>CMSG_LOOT_MONEY — take the whole coin pile. Empty body.</summary>
    public void LootMoney() => SendPacket((ushort)Op.CMSG_LOOT_MONEY, ReadOnlySpan<byte>.Empty);

    /// <summary>CMSG_LOOT_RELEASE — close the loot session on the server. Full guid body.</summary>
    public void LootRelease(ulong guid) => SendPacket((ushort)Op.CMSG_LOOT_RELEASE, BuildLootGuidBody(guid));

    /// <summary>CMSG_AUTOSTORE_LOOT_ITEM — one u8: the 0-based WIRE loot slot; the server
    /// places the item into the first free bag slot (no destination on the wire).</summary>
    public void AutostoreLootItem(byte lootSlot)
    {
        SendPacket((ushort)Op.CMSG_AUTOSTORE_LOOT_ITEM, BuildAutostoreLootBody(lootSlot));
    }

    public static byte[] BuildLootGuidBody(ulong guid)
    {
        var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray();
    }

    public static byte[] BuildAutostoreLootBody(byte lootSlot) => [lootSlot];

    /// <summary>CMSG_LOOT_ROLL: full loot-target guid, u32 wire item slot, u8 vote.</summary>
    public void LootRoll(ulong lootedTarget, uint itemSlot, GroupLootVote vote)
        => SendPacket((ushort)Op.CMSG_LOOT_ROLL,
            LootPackets.BuildRollBody(lootedTarget, itemSlot, vote));

    public void GuildRoster() => SendPacket((ushort)Op.CMSG_GUILD_ROSTER, ReadOnlySpan<byte>.Empty);
    public void GuildQuery(uint guildId)
        => SendPacket((ushort)Op.CMSG_GUILD_QUERY, BuildGuildQueryBody(guildId));
    public static byte[] BuildGuildQueryBody(uint guildId)
    {
        var w = new PacketWriter(4);
        w.WriteU32(guildId);
        return w.ToArray();
    }
    public void GuildInvite(string name)
        => SendPacket((ushort)Op.CMSG_GUILD_INVITE, BuildCStringBody(name));
    public void GuildAccept()
        => SendPacket((ushort)Op.CMSG_GUILD_ACCEPT, ReadOnlySpan<byte>.Empty);
    public void GuildDecline()
        => SendPacket((ushort)Op.CMSG_GUILD_DECLINE, ReadOnlySpan<byte>.Empty);
    public void GuildInfo()
        => SendPacket((ushort)Op.CMSG_GUILD_INFO, ReadOnlySpan<byte>.Empty);
    public void GuildInfoText(string text)
        => SendPacket((ushort)Op.CMSG_GUILD_INFO_TEXT, BuildCStringBody(text));
    public void GuildRemove(string name)
        => SendPacket((ushort)Op.CMSG_GUILD_REMOVE, BuildCStringBody(name));
    public void GuildSetPublicNote(string name, string note)
        => SendPacket((ushort)Op.CMSG_GUILD_SET_PUBLIC_NOTE,
            BuildTwoCStringBody(name, note));
    public void GuildSetOfficerNote(string name, string note)
        => SendPacket((ushort)Op.CMSG_GUILD_SET_OFFICER_NOTE,
            BuildTwoCStringBody(name, note));
    public void GuildRank(uint rankId, uint rights, string name)
        => SendPacket((ushort)Op.CMSG_GUILD_RANK, BuildGuildRankBody(rankId, rights, name));
    public static byte[] BuildGuildRankBody(uint rankId, uint rights, string name)
    {
        var w = new PacketWriter();
        w.WriteU32(rankId);
        w.WriteU32(rights);
        w.WriteCString(name);
        return w.ToArray();
    }
    public void GuildAddRank(string name)
        => SendPacket((ushort)Op.CMSG_GUILD_ADD_RANK, BuildCStringBody(name));
    public void GuildDeleteRank()
        => SendPacket((ushort)Op.CMSG_GUILD_DEL_RANK, ReadOnlySpan<byte>.Empty);
    public static byte[] BuildTwoCStringBody(string first, string second)
    {
        var w = new PacketWriter();
        w.WriteCString(first);
        w.WriteCString(second);
        return w.ToArray();
    }
    public void GuildMotd(string text) => SendPacket((ushort)Op.CMSG_GUILD_MOTD, BuildCStringBody(text));
    public void GuildPromote(string name) => SendPacket((ushort)Op.CMSG_GUILD_PROMOTE, BuildCStringBody(name));
    public void GuildDemote(string name) => SendPacket((ushort)Op.CMSG_GUILD_DEMOTE, BuildCStringBody(name));
    public void GuildLeader(string name) => SendPacket((ushort)Op.CMSG_GUILD_LEADER, BuildCStringBody(name));
    public void GuildLeave() => SendPacket((ushort)Op.CMSG_GUILD_LEAVE, ReadOnlySpan<byte>.Empty);
    public void GuildDisband() => SendPacket((ushort)Op.CMSG_GUILD_DISBAND, ReadOnlySpan<byte>.Empty);
    public void SaveGuildEmblem(ulong vendorGuid, uint emblemStyle, uint emblemColor,
        uint borderStyle, uint borderColor, uint backgroundColor)
        => SendPacket((ushort)Op.MSG_SAVE_GUILD_EMBLEM,
            BuildSaveGuildEmblemBody(vendorGuid, emblemStyle, emblemColor,
                borderStyle, borderColor, backgroundColor));
    public static byte[] BuildSaveGuildEmblemBody(ulong vendorGuid, uint emblemStyle,
        uint emblemColor, uint borderStyle, uint borderColor, uint backgroundColor)
    {
        var w = new PacketWriter(28); w.WriteU64(vendorGuid); w.WriteU32(emblemStyle);
        w.WriteU32(emblemColor); w.WriteU32(borderStyle); w.WriteU32(borderColor);
        w.WriteU32(backgroundColor); return w.ToArray();
    }
    public static byte[] BuildCStringBody(string value)
    {
        var w = new PacketWriter(Encoding.UTF8.GetByteCount(value) + 1);
        w.WriteCString(value); return w.ToArray();
    }

    /// <summary>
    /// Acknowledge an adopted SMSG_NEW_WORLD. Ownership belongs to the game-thread
    /// transition; the socket reader must never call this while world state is stale.
    /// </summary>
    public void WorldportAck() =>
        SendPacket((ushort)Op.CMSG_MOVE_WORLDPORT_ACK, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Client half of the build-5875 same-map teleport handshake. The server's
    /// MSG_MOVE_TELEPORT_ACK carries a packed mover guid, counter and destination
    /// MovementInfo; the reply is the full guid, the same counter, and the
    /// client's monotonic movement clock.
    /// </summary>
    public void TeleportAck(ulong guid, uint counter)
    {
        SendPacket((ushort)Op.MSG_MOVE_TELEPORT_ACK, BuildTeleportAckBody(guid, counter));
    }

    public static byte[] BuildTeleportAckBody(ulong guid, uint counter)
    {
        var w = new PacketWriter(16);
        w.WriteU64(guid);
        w.WriteU32(counter);
        w.WriteU32(MovementInfo.ClientUptimeMs());
        return w.ToArray();
    }

    private void SendFullGuid(Op op, ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(guid);
        SendPacket((ushort)op, w.AsSpan());
    }

    // --- CMSG_AUTH_SESSION body -----------------------------------------------------------------

    public static byte[] BuildAuthSession(ushort build, string accountUpper, uint clientSeed, byte[] proof)
    {
        if (proof.Length != 20) throw new ArgumentException("AUTH_SESSION proof must be 20 bytes", nameof(proof));
        byte[] addonTail = AuthSessionAddonLaw.StockTail();
        var w = new PacketWriter(33 + accountUpper.Length + addonTail.Length);
        w.WriteU32(build);
        w.WriteU32(0);                    // server id
        w.WriteBytes(Encoding.ASCII.GetBytes(accountUpper));
        w.WriteU8(0);                     // NUL
        w.WriteU32(clientSeed);
        w.WriteBytes(proof);              // 20-byte SHA1 digest
        w.WriteBytes(addonTail);           // stock secure-addon block: size + RFC1950 zlib
        return w.ToArray();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _tcp.Dispose(); } catch { /* ignore */ }
    }
}
