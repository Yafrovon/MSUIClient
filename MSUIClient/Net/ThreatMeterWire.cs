namespace MSUIClient.Net;

/// <summary>
/// Threat meter v1 (owner 2026-09-22), capability bit 14. Vanilla 1.12 never tells a client
/// anyone's threat, so the server answers a pull for one creature:
///
/// CMSG_SUI_THREAT (0x036C): u8 version (1), u8 rows wanted (1..10), u64 creature.
/// SMSG_SUI_THREAT (0x036D): u8 version (1), u64 creature, u8 status (0 = list follows,
///   1 = not a threat-list unit within reach), then for status 0: u64 current victim,
///   u16 list size, u8 rows, rows x { u64 guid, f32 threat }, u8 hasSelf,
///   [u16 rank (1-based), f32 threat] - the driven body's own row when it is not already
///   among the rows.
/// </summary>
public static class ThreatMeterWire
{
    public const uint Capability = 1u << 14;
    public const byte Version = 1;
    public const int MaximumRows = 10;

    public readonly record struct Row(ulong Guid, float Threat);

    public sealed record Snapshot(ulong Creature, bool Available, ulong Victim, int ListSize,
        IReadOnlyList<Row> Rows, int SelfRank, float SelfThreat);

    public static byte[] BuildRequest(ulong creature, int rows)
    {
        var w = new PacketWriter(10);
        w.WriteU8(Version);
        w.WriteU8((byte)Math.Clamp(rows, 1, MaximumRows));
        w.WriteU64(creature);
        return w.ToArray();
    }

    /// <summary>Parse a reply; null for a malformed or foreign-version body.</summary>
    public static Snapshot? Parse(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            if (r.ReadU8() != Version) return null;
            ulong creature = r.ReadU64();
            byte status = r.ReadU8();
            if (status != 0) return new(creature, false, 0, 0, [], 0, 0);
            ulong victim = r.ReadU64();
            int listSize = r.ReadU16();
            int count = r.ReadU8();
            if (count > MaximumRows) return null;
            var rows = new Row[count];
            for (int i = 0; i < count; i++) rows[i] = new(r.ReadU64(), r.ReadF32());
            int selfRank = 0;
            float selfThreat = 0;
            if (r.ReadU8() != 0)
            {
                selfRank = r.ReadU16();
                selfThreat = r.ReadF32();
            }
            return new(creature, true, victim, listSize, rows, selfRank, selfThreat);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
