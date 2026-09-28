using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The Threat Meter (owner 2026-09-22: "we don't need everyone, but maybe top 5, so as a tank or
/// dps I can visualize where I am"). Pure rules; the server supplies the numbers
/// (<see cref="ThreatMeterWire"/>).
///
/// The one fact every reader needs is how close each row is to taking aggro. A creature keeps
/// its victim until someone exceeds 110 % of the victim's threat in melee range, or 130 % at
/// range. So the bar scale is the ranged pull point (130 % of the holder), a tick marks the
/// melee pull point (110 %), and every row reads "% of the holder". The holder's own row is
/// always 100 %.
/// </summary>
public static class ThreatMeterLaw
{
    public const float MeleePull = 1.10f;
    public const float RangedPull = 1.30f;
    public const int MinimumRows = 3;
    public const int MaximumRows = ThreatMeterWire.MaximumRows;
    public const float Width = 200f;
    public const float RowHeight = 14f;
    public const float RowGap = 2f;
    public const float TitleHeight = 16f;
    public const float Padding = 6f;
    /// <summary>A pull every half second: fresh enough to act on, cheap for the server.</summary>
    public const double RequestIntervalSeconds = 0.5;
    /// <summary>A reply older than this is stale (target changed, server quiet).</summary>
    public const double StaleSeconds = 2.0;

    public readonly record struct Entry(ulong Guid, float Threat, int Rank, bool IsHolder, bool IsSelf);

    public static int ClampRows(int rows) => Math.Clamp(rows, MinimumRows, MaximumRows);

    public static bool Visible(bool enabled, bool unlocked, bool hideWhenIdle, bool hasRows) =>
        enabled && (unlocked || !hideWhenIdle || hasRows);

    /// <summary>The rows to draw: the top of the list, then your own row (with its real rank)
    /// when you are not already among them.</summary>
    public static IReadOnlyList<Entry> Entries(ThreatMeterWire.Snapshot snapshot, ulong self)
    {
        var entries = new List<Entry>(snapshot.Rows.Count + 1);
        for (int i = 0; i < snapshot.Rows.Count; i++)
        {
            ThreatMeterWire.Row row = snapshot.Rows[i];
            entries.Add(new(row.Guid, row.Threat, i + 1, row.Guid == snapshot.Victim, row.Guid == self));
        }
        if (snapshot.SelfRank > 0 && self != 0 && entries.All(e => e.Guid != self))
            entries.Add(new(self, snapshot.SelfThreat, snapshot.SelfRank, self == snapshot.Victim, true));
        return entries;
    }

    /// <summary>The threat the current holder has; falls back to the top row when the holder is
    /// not among the rows (a taunt or a scripted target switch can do that briefly).</summary>
    public static float HolderThreat(IReadOnlyList<Entry> entries)
    {
        foreach (Entry entry in entries)
            if (entry.IsHolder) return entry.Threat;
        return entries.Count == 0 ? 0 : entries.Max(e => e.Threat);
    }

    /// <summary>Bar scale: the ranged pull point, or the largest row if someone is past it.</summary>
    public static float Scale(IReadOnlyList<Entry> entries, float holderThreat)
    {
        float top = entries.Count == 0 ? 0 : entries.Max(e => e.Threat);
        return MathF.Max(MathF.Max(top, holderThreat * RangedPull), 1f);
    }

    public static float Fraction(float threat, float scale) =>
        scale <= 0 ? 0 : Math.Clamp(threat / scale, 0f, 1f);

    /// <summary>Share of the holder's threat, in whole percent; null when nobody holds it.</summary>
    public static int? PercentOfHolder(float threat, float holderThreat) =>
        holderThreat <= 0 ? null : (int)MathF.Round(threat / holderThreat * 100f);

    /// <summary>Colour of the bar by how close the row is to pulling (non-holders only):
    /// under 80 % calm, to the melee pull point warning, past it danger.</summary>
    public static uint BarColor(Entry entry, float holderThreat, uint classColor)
    {
        if (entry.IsHolder || holderThreat <= 0) return classColor;
        float share = entry.Threat / holderThreat;
        if (share >= MeleePull) return 0xFF2020E0;   // red (ABGR)
        if (share >= 0.8f) return 0xFF10A0F0;        // amber
        return classColor;
    }

    public static string FormatThreat(float threat) => threat switch
    {
        >= 1_000_000f => (threat / 1_000_000f).ToString("0.0", CultureInfo.InvariantCulture) + "m",
        >= 10_000f => (threat / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => MathF.Round(threat).ToString("0", CultureInfo.InvariantCulture),
    };

    public static float Height(int entryCount) =>
        Padding * 2 + TitleHeight + Math.Max(1, entryCount) * (RowHeight + RowGap);
}
