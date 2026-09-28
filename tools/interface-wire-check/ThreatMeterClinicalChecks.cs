using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

/// <summary>Threat meter v1 (owner 2026-09-22): wire layout, capability gate, and the pull-scale
/// rules the bars are drawn from.</summary>
internal static class ThreatMeterClinicalChecks
{
    public static void Run()
    {
        byte[] request = ThreatMeterWire.BuildRequest(0xF130_0000_0000_1234, 5);
        Check(request.Length == 10 && request[0] == 1 && request[1] == 5 &&
              BitConverter.ToUInt64(request, 2) == 0xF130_0000_0000_1234 &&
              ThreatMeterWire.BuildRequest(1, 99)[1] == ThreatMeterWire.MaximumRows &&
              ThreatMeterWire.BuildRequest(1, 0)[1] == 1,
            "CMSG_SUI_THREAT body is not u8 version, u8 rows (1..10), u64 creature");
        Check((ushort)Op.CMSG_SUI_THREAT == 876 && (ushort)Op.SMSG_SUI_THREAT == 877 &&
              ThreatMeterWire.Capability == 1u << 14,
            "threat meter opcodes/capability drifted from the Core pair (876/877, bit 14)");

        // Reply: tank 787 holds with 1000, bot 115 at 1050 (past nothing: ranged), self 116 ranked 9th.
        var w = new PacketWriter(96);
        w.WriteU8(1); w.WriteU64(0xF130_0000_0000_1234); w.WriteU8(0);
        w.WriteU64(787); w.WriteU16(12); w.WriteU8(2);
        w.WriteU64(115); w.WriteF32(1050f);
        w.WriteU64(787); w.WriteF32(1000f);
        w.WriteU8(1); w.WriteU16(9); w.WriteF32(400f);
        ThreatMeterWire.Snapshot? snapshot = ThreatMeterWire.Parse(w.ToArray());
        Check(snapshot is { Available: true, Victim: 787, ListSize: 12, SelfRank: 9 } &&
              snapshot.Rows.Count == 2 && snapshot.Rows[0].Guid == 115 && snapshot.SelfThreat == 400f,
            "SMSG_SUI_THREAT parse lost a field");
        Check(ThreatMeterWire.Parse([1, 0, 0]) is null &&
              ThreatMeterWire.Parse([2, 0, 0, 0, 0, 0, 0, 0, 0, 1]) is null,
            "a truncated or foreign-version threat reply was accepted");
        var unavailable = new PacketWriter(16);
        unavailable.WriteU8(1); unavailable.WriteU64(5); unavailable.WriteU8(1);
        Check(ThreatMeterWire.Parse(unavailable.ToArray()) is { Available: false },
            "status 1 (no threat list within reach) is not reported as unavailable");

        var entries = ThreatMeterLaw.Entries(snapshot!, self: 116);
        float holder = ThreatMeterLaw.HolderThreat(entries);
        float scale = ThreatMeterLaw.Scale(entries, holder);
        Check(entries.Count == 3 && entries[2] is { Guid: 116, Rank: 9, IsSelf: true } &&
              entries[1].IsHolder && holder == 1000f && scale == 1300f &&
              ThreatMeterLaw.PercentOfHolder(1050f, holder) == 105 &&
              ThreatMeterLaw.Fraction(holder * ThreatMeterLaw.MeleePull, scale) is > 0.84f and < 0.85f,
            "threat entries / pull scale drifted (self row, holder, 130% scale, 110% tick)");
        Check(ThreatMeterLaw.Entries(snapshot!, self: 115).Count == 2,
            "your own row is listed twice when you are already in the top rows");
        Check(ThreatMeterLaw.BarColor(entries[0], holder, 0xFF123456) == 0xFF10A0F0 &&
              ThreatMeterLaw.BarColor(entries[1], holder, 0xFF123456) == 0xFF123456 &&
              ThreatMeterLaw.BarColor(entries[0] with { Threat = 1200f }, holder, 0xFF123456) == 0xFF2020E0,
            "threat bar warning colours drifted (amber from 80%, red past the melee pull)");
        Check(ThreatMeterLaw.FormatThreat(950f) == "950" && ThreatMeterLaw.FormatThreat(12345f) == "12.3k" &&
              ThreatMeterLaw.ClampRows(1) == 3 && ThreatMeterLaw.ClampRows(40) == 10 &&
              !ThreatMeterLaw.Visible(false, true, false, true) &&
              !ThreatMeterLaw.Visible(true, false, true, false) && ThreatMeterLaw.Visible(true, true, true, false),
            "threat meter formatting / row clamp / visibility drifted");

        string root = ClientConfig.FindRepoRoot();
        string meter = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud", "GameLoop.ThreatMeter.cs"));
        Check(meter.Contains("_threatMeterAvailable ? ThreatMeterSubject() : 0", StringComparison.Ordinal) &&
              meter.Contains("ThreatMeterLaw.Entries(snapshot, ControlledGuid)", StringComparison.Ordinal) &&
              meter.Contains("snapshot.Creature != _threatRequestSubject", StringComparison.Ordinal),
            "threat meter sends before capability 14, ignores the driven body, or accepts stale-target replies");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
