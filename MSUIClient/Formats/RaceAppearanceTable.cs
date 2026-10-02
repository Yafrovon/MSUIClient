namespace MSUIClient.Formats;

/// <summary>Original ChrRaces appearance flags. Field 1 bit 0x2 preserves bare feet.
/// Mounted vanilla rows 6/8 have flags 14; ordinary playable rows have 12 or 4.
/// See shared_docs/EQUIPMENT_CAPTURE.md for source provenance and compositor checks.</summary>
public sealed class RaceAppearanceTable
{
    public const string MpqPath = @"DBFilesClient\ChrRaces.dbc";
    public const uint BareFeetFlag = 0x2;
    private readonly Dictionary<uint, uint> _flags = [];
    public uint Flags(uint race) => _flags.GetValueOrDefault(race);
    public bool HasBareFeet(uint race) => (Flags(race) & BareFeetFlag) != 0;

    public static RaceAppearanceTable? Parse(byte[]? bytes)
    {
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 2 || dbc.RecordSize < 8) return null;
        var table = new RaceAppearanceTable();
        for (int row = 0; row < dbc.RecordCount; row++)
            table._flags[dbc.GetUInt(row, 0)] = dbc.GetUInt(row, 1);
        return table;
    }
}
