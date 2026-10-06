using System.Numerics;

namespace MSUIClient.Net;

/// <summary>
/// WoW Karting v1 (owner 2026-10-04, shared_docs/WOW_KARTING.md), capability bit 15. The race is
/// server-authoritative (Core SuiKarting RaceManager): the client only asks to join/leave and draws
/// what the server tells it.
///
/// CMSG_SUI_KART (0x036E): u8 version (1), u8 action (<see cref="Action"/>), u32 param.
/// SMSG_SUI_KART (0x036F): u8 version (1), u8 kind (<see cref="Kind"/>), then by kind:
///   State:     u32 race, u8 phase, u8 laps, u8 checkpoints, i32 countdownMs (to GO, &lt;= 0 after),
///              u32 elapsedMs (since GO), u8 racers, racers x { u64 guid, u8 lap, u8 nextCheckpoint,
///              u8 rank, u8 flags, u32 finishMs }, u8 hasNext, [u32 map, f32 x, f32 y, f32 z, f32 radius]
///              - the receiving racer's next checkpoint.
///   Countdown: u32 race, u8 count (3, 2, 1, 0 = GO) - sent on the beat, for the sound and the numbers.
///   Prewarm:   u32 portalEntry, u32 teleportSpell, u32 map, f32 x, f32 y, f32 z, f32 o - the next leg's
///              Real Portal destination, so the client prepares it before the kart arrives.
///   Notice:    u8 code (<see cref="NoticeCode"/>), u32 param.
///   Item:      u8 item, u8 uses, u16 rouletteMs, u16 goldenMs (Phase 5, the MK64 items).
/// CMSG action 5 = use the held item (param 1 = backwards).
/// </summary>
public static class KartingWire
{
    public const uint Capability = 1u << 15;
    public const byte Version = 1;
    public const int MaximumRacers = 16;

    public enum Action : byte { Join = 1, Leave = 2, StartNow = 3, Query = 4, UseItem = 5 }
    public enum Kind : byte { State = 1, Countdown = 2, Prewarm = 3, Notice = 4, Item = 5 }

    /// <summary>The MK64 items (Core SuiKartingRace.cpp enum Item), in wire order.</summary>
    public enum Item : byte
    {
        None = 0, Banana, BananaBunch, GreenShell, TripleGreen, RedShell, TripleRed, SpinyShell, Mushroom,
        TripleMushroom, GoldenMushroom, Star, Lightning, FakeItemBox, Boo,
    }

    /// <summary>Item: u8 item, u8 uses, u16 rouletteMs (the roulette still spinning), u16 goldenMs.</summary>
    public readonly record struct ItemState(Item Item, int Uses, int RouletteMs, int GoldenMs);

    public static ItemState? ParseItem(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version || r.ReadU8() != (byte)Kind.Item) return null;
            return new((Item)r.ReadU8(), r.ReadU8(), r.ReadU16(), r.ReadU16());
        }
        catch (EndOfStreamException) { return null; }
    }
    public enum Phase : byte { Idle = 0, Gathering = 1, Countdown = 2, Racing = 3, Finished = 4 }
    public enum NoticeCode : byte { Joined = 1, Left = 2, NotOnKart = 3, RaceFull = 4, NoCourse = 5, Lap = 6, Finished = 7, Dnf = 8 }

    [Flags]
    public enum RacerFlags : byte { None = 0, Bot = 1, Finished = 2, Dnf = 4, Self = 8 }

    public readonly record struct Racer(ulong Guid, int Lap, int NextCheckpoint, int Rank, RacerFlags Flags, uint FinishMs);

    public sealed record State(uint Race, Phase Phase, int Laps, int Checkpoints, int CountdownMs, uint ElapsedMs,
        IReadOnlyList<Racer> Racers, int NextMap, Vector3 NextCheckpoint, float NextRadius, bool HasNext);

    public readonly record struct Prewarm(uint PortalEntry, uint TeleportSpell, uint Map, Vector3 Position, float Orientation);

    public static byte[] BuildRequest(Action action, uint param = 0)
    {
        var w = new PacketWriter(6);
        w.WriteU8(Version);
        w.WriteU8((byte)action);
        w.WriteU32(param);
        return w.ToArray();
    }

    /// <summary>The kind of an SMSG body, or null for a malformed or foreign-version body.</summary>
    public static Kind? KindOf(byte[] body) =>
        body.Length >= 2 && body[0] == Version && Enum.IsDefined(typeof(Kind), body[1]) ? (Kind)body[1] : null;

    public static State? ParseState(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version || r.ReadU8() != (byte)Kind.State) return null;
            uint race = r.ReadU32();
            var phase = (Phase)r.ReadU8();
            int laps = r.ReadU8(), checkpoints = r.ReadU8();
            int countdown = r.ReadI32();
            uint elapsed = r.ReadU32();
            int count = r.ReadU8();
            if (count > MaximumRacers) return null;
            var racers = new Racer[count];
            for (int i = 0; i < count; i++)
                racers[i] = new(r.ReadU64(), r.ReadU8(), r.ReadU8(), r.ReadU8(), (RacerFlags)r.ReadU8(), r.ReadU32());
            bool hasNext = r.ReadU8() != 0;
            int map = 0; Vector3 next = default; float radius = 0;
            if (hasNext)
            {
                map = (int)r.ReadU32();
                next = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());
                radius = r.ReadF32();
            }
            return new(race, phase, laps, checkpoints, countdown, elapsed, racers, map, next, radius, hasNext);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public static (uint Race, int Count)? ParseCountdown(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version || r.ReadU8() != (byte)Kind.Countdown) return null;
            return (r.ReadU32(), r.ReadU8());
        }
        catch (EndOfStreamException) { return null; }
    }

    public static Prewarm? ParsePrewarm(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version || r.ReadU8() != (byte)Kind.Prewarm) return null;
            return new(r.ReadU32(), r.ReadU32(), r.ReadU32(), new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32()), r.ReadF32());
        }
        catch (EndOfStreamException) { return null; }
    }

    public static (NoticeCode Code, uint Param)? ParseNotice(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version || r.ReadU8() != (byte)Kind.Notice) return null;
            return ((NoticeCode)r.ReadU8(), r.ReadU32());
        }
        catch (EndOfStreamException) { return null; }
    }
}
