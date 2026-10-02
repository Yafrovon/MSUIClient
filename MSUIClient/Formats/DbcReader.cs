using System.Numerics;
using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Reader for Blizzard's WDBC tables, and the typed rows this client needs
/// from them.
///
/// WHY THE CLIENT NEEDS THIS AT ALL
///   Everything about equipped gear lives in ItemDisplayInfo.dbc: which body
///   textures paint into the skin atlas, which geoset variants switch on, and
///   which separate M2 models hang off attachment points. A display ID is a row
///   number in that table and nothing else - without it, "equip the Breastplate
///   of Might" has no meaning the client can act on.
///
/// FORMAT
///   "WDBC", then four uint32: record count, field count, record size, string
///   block size. Records follow, then one contiguous string block. Every string
///   in a record is a BYTE OFFSET into that block, not a length-prefixed value.
///   Offset 0 is the empty string by convention.
///
/// NO GL, NO GAME LOGIC - this is Formats/, same rule as every other reader.
/// </summary>
public sealed class DbcFile
{
    public int RecordCount { get; private set; }
    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }

    private byte[] _records = [];
    private byte[] _strings = [];

    public static DbcFile? Parse(byte[] data)
    {
        if (data.Length < 20) return null;
        if (data[0] != 'W' || data[1] != 'D' || data[2] != 'B' || data[3] != 'C') return null;

        var dbc = new DbcFile
        {
            RecordCount = BitConverter.ToInt32(data, 4),
            FieldCount = BitConverter.ToInt32(data, 8),
            RecordSize = BitConverter.ToInt32(data, 12),
        };

        int stringSize = BitConverter.ToInt32(data, 16);

        if (dbc.RecordCount < 0 || dbc.RecordSize <= 0 || stringSize < 0) return null;

        long recordBytes = (long)dbc.RecordCount * dbc.RecordSize;
        if (20 + recordBytes + stringSize > data.Length) return null;

        dbc._records = new byte[recordBytes];
        Array.Copy(data, 20, dbc._records, 0, recordBytes);

        dbc._strings = new byte[stringSize];
        Array.Copy(data, 20 + recordBytes, dbc._strings, 0, stringSize);

        return dbc;
    }

    public uint GetUInt(int row, int field)
    {
        int offset = row * RecordSize + field * 4;
        if (row < 0 || row >= RecordCount || field < 0 || offset + 4 > _records.Length) return 0;
        return BitConverter.ToUInt32(_records, offset);
    }

    public int GetInt(int row, int field) => unchecked((int)GetUInt(row, field));

    /// <summary>
    /// Reinterpret a field's four bytes as a float. The Light tables need it:
    /// positions, falloff radii and every LightFloatBand value are IEEE floats
    /// sitting in the same fixed-width columns as the integers.
    /// </summary>
    public float GetFloat(int row, int field)
        => BitConverter.Int32BitsToSingle(GetInt(row, field));

    /// <summary>
    /// Read a stringref field. The value stored is an offset into the string
    /// block; the string runs to the next null byte.
    /// </summary>
    public string GetString(int row, int field)
    {
        uint offset = GetUInt(row, field);
        if (offset == 0 || offset >= _strings.Length) return "";

        int end = (int)offset;
        while (end < _strings.Length && _strings[end] != 0) end++;

        return Encoding.UTF8.GetString(_strings, (int)offset, end - (int)offset);
    }

    /// <summary>
    /// Like <see cref="GetString"/>, but only when the stored value points at the
    /// START of a string in the block - offset 0 (the empty string), or a byte
    /// immediately preceded by a null. An INTEGER column misread as a stringref
    /// lands in the middle of a neighbouring string; this tells the two apart.
    /// Returns null when the field is not a valid string start, so a column scan
    /// can skip it instead of accepting a truncated fragment.
    /// </summary>
    public string? GetStringIfStart(int row, int field)
    {
        uint offset = GetUInt(row, field);
        if (offset == 0) return "";
        if (offset >= _strings.Length) return null;
        if (_strings[offset - 1] != 0) return null;   // mid-string: not a real ref

        int end = (int)offset;
        while (end < _strings.Length && _strings[end] != 0) end++;

        return Encoding.UTF8.GetString(_strings, (int)offset, end - (int)offset);
    }
}

/// <summary>
/// One ItemDisplayInfo.dbc row, vanilla 1.12.1.
///
/// THE FIELD LAYOUT IS NOT GUESSED. It comes from SuperUI's DbcService, where
/// it was established by dumping all 23 fields across robes, plate chests,
/// cloth, boots, gloves and a trade good, plus a histogram of small integer
/// values at every field index across all 29,604 records. 23 fields, 92 bytes
/// per record:
///
///   [0]      m_ID
///   [1-2]    m_modelName[0..1]        stringref  - the separate M2 for helms,
///                                                  shoulders, weapons, shields
///   [3-4]    m_modelTexture[0..1]     stringref  - textures for those models
///   [5]      m_inventoryIcon          stringref  (single in vanilla)
///   [6-8]    m_geosetGroup[0..2]      uint32     - which geoset variants switch on
///   [9]      m_spellVisualID
///   [10]     m_groundModel
///   [11]     m_groupSoundIndex
///   [12-13]  m_helmetGeosetVis[0..1]  - drives hair hiding under a helm
///   [14-21]  m_texture[0..7]          stringref  - the body-atlas paints
///   [22]     m_itemVisual             - ItemVisuals.dbc, glow and lightning
///
/// A note from that same source, worth keeping: an earlier parser used a -2
/// shift for the texture base and looked correct on chests, because the
/// compositor's slot mapping started at 2 and the two errors cancelled. It was
/// wrong for the right reason, and it hid LegLower and Foot entirely.
/// </summary>
public sealed class ItemDisplayRow
{
    public uint Id;
    public string ModelName1 = "";
    public string ModelName2 = "";
    public string ModelTexture1 = "";
    public string ModelTexture2 = "";
    public string InventoryIcon = "";
    public int[] GeosetGroup = new int[3];
    public uint HelmetGeosetVis1;
    public uint HelmetGeosetVis2;

    /// <summary>
    /// m_texture[0..7], in slot order:
    /// 0 ArmUpper, 1 ArmLower, 2 Hand, 3 TorsoUpper, 4 TorsoLower,
    /// 5 LegUpper, 6 LegLower, 7 Foot.
    /// </summary>
    public string[] BodyTextures = new string[8];

    public uint ItemVisualId;
    public uint GroupSoundIndex;
    /// <summary>Ranged fallback SpellVisual (vanilla ItemDisplayInfo field 10).</summary>
    public uint SpellVisualId;

    public bool HasModel => ModelName1.Length > 0 || ModelName2.Length > 0;
    public bool HasBodyTexture => BodyTextures.Any(t => t.Length > 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (ModelName1.Length > 0) parts.Add($"model '{ModelName1}'");
        if (ModelTexture1.Length > 0) parts.Add($"modelTex '{ModelTexture1}'");

        var textures = BodyTextures
            .Select((t, i) => (t, i))
            .Where(x => x.t.Length > 0)
            .Select(x => $"{x.i}:'{x.t}'");
        if (textures.Any()) parts.Add("tex " + string.Join(" ", textures));

        if (GeosetGroup.Any(g => g != 0)) parts.Add($"geoset [{string.Join(",", GeosetGroup)}]");
        if (HelmetGeosetVis1 != 0 || HelmetGeosetVis2 != 0)
            parts.Add($"helmVis {HelmetGeosetVis1}/{HelmetGeosetVis2}");

        return parts.Count > 0 ? string.Join("  ", parts) : "(empty row)";
    }
}

/// <summary>
/// One CharSections.dbc row. Vanilla 1.12: 10 fields, 40 bytes.
///
/// Layout verified in SuperUI's DbcService against the real file (3,671
/// records, a 198,838-byte string block):
///
///   [0] ID
///   [1] Race          CharRaces id - 1 Human, 2 Orc, 3 Dwarf, 4 NightElf,
///                     5 Scourge, 6 Tauren, 7 Gnome, 8 Troll
///   [2] Sex           0 male, 1 female
///   [3] BaseSection   0 Skin, 1 Face, 2 FacialHair, 3 Hair, 4 Underwear
///   [4] VariationIndex  face shape or hair style
///   [5] ColorIndex      skin tone or hair colour
///   [6-8] TextureName[0..2]   for Face: [0] lower, [1] upper, [2] empty
///   [9] Flags
///
/// THIS TABLE IS WHERE A CHARACTER'S FACE AND HAIR ACTUALLY COME FROM. A body
/// skin BLP for most races has no eye detail at all - the eyes are a Face row
/// composited onto the atlas. Skip that step and the character renders
/// blank-faced, which reads as "eyes closed" and sends you looking at geosets.
/// </summary>
public sealed class CharSectionRow
{
    public uint Id;
    public int RowIndex;
    public uint Race;
    public uint Sex;
    public uint BaseSection;
    public uint VariationIndex;
    public uint ColorIndex;
    public string Texture1 = "";
    public string Texture2 = "";
    public string Texture3 = "";
    public uint Flags;
}

public sealed class CharSectionsTable
{
    public const string MpqPath = @"DBFilesClient\CharSections.dbc";

    public const uint SectionSkin = 0;
    public const uint SectionFace = 1;
    public const uint SectionFacialHair = 2;
    public const uint SectionHair = 3;
    public const uint SectionUnderwear = 4;

    private readonly List<CharSectionRow> _rows = [];
    public int Count => _rows.Count;

    public static CharSectionsTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.RecordSize != 40)
            Console.WriteLine($"[dbc] CharSections record size is {dbc.RecordSize}, expected 40 - " +
                              "field offsets are probably wrong");

        var table = new CharSectionsTable();

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            table._rows.Add(new CharSectionRow
            {
                Id = dbc.GetUInt(r, 0),
                RowIndex = r,
                Race = dbc.GetUInt(r, 1),
                Sex = dbc.GetUInt(r, 2),
                BaseSection = dbc.GetUInt(r, 3),
                VariationIndex = dbc.GetUInt(r, 4),
                ColorIndex = dbc.GetUInt(r, 5),
                Texture1 = dbc.GetString(r, 6),
                Texture2 = dbc.GetString(r, 7),
                Texture3 = dbc.GetString(r, 8),
                Flags = dbc.GetUInt(r, 9),
            });
        }

        Console.WriteLine($"[dbc] CharSections: {table.Count} row(s)");
        return table;
    }

    /// <summary>
    /// Find a section row. The MATCH KEYS DIFFER PER SECTION and getting them
    /// wrong returns a plausible row for the wrong character:
    ///
    ///   Skin       colour only        (the skin tone)
    ///   Face       variation AND colour  (face shape, then skin tone)
    ///   Hair       variation AND colour  (hair style, then hair colour)
    ///   Underwear  colour only        (follows the skin tone)
    ///
    /// Pass -1 for a key that should not be matched on.
    /// </summary>
    public CharSectionRow? Find(uint race, uint sex, uint section, int variation, int colour)
    {
        return FindMatches(race, sex, section, variation, colour).FirstOrDefault();
    }

    /// <summary>
    /// Return file-order matches for batch diagnostics. The production winner
    /// remains the first row, exactly as <see cref="Find"/> has always selected it.
    /// </summary>
    public IReadOnlyList<CharSectionRow> FindMatches(
        uint race, uint sex, uint section, int variation, int colour)
        => _rows.Where(row =>
                row.Race == race && row.Sex == sex && row.BaseSection == section &&
                (variation < 0 || row.VariationIndex == (uint)variation) &&
                (colour < 0 || row.ColorIndex == (uint)colour))
            .ToArray();

    /// <summary>Uncomposited type-8 skin-extra sheet. Original Tauren skin rows put
    /// mane, tail and hoof surfaces in Texture2; underwear is a different section.
    /// Selection follows skin colour, not face/hair or equipped clothing. An absent
    /// declaration stays absent rather than sampling a dressed body atlas.</summary>
    public string SkinExtraTexture(uint race, uint sex, int skinColour)
        => skinColour < 0 ? "" : Find(race, sex, SectionSkin, -1, skinColour)?.Texture2 ?? "";

    /// <summary>Vanilla CharRaces ids, matching the folder names used for MPQ paths.</summary>
    public static uint RaceId(string race) => race.ToLowerInvariant() switch
    {
        "human" => 1,
        "orc" => 2,
        "dwarf" => 3,
        "nightelf" => 4,
        "scourge" or "undead" => 5,
        "tauren" => 6,
        "gnome" => 7,
        "troll" => 8,
        _ => 1,
    };
}

/// <summary>
/// CharHairGeosets.dbc maps a character-creation hairstyle number to the
/// actual group-0 submesh in that race/gender M2. The hairstyle number is not
/// itself a geoset number.
///
/// Vanilla columns:
///   [0] ID, [1] RaceID, [2] SexID, [3] Variation, [4] GeosetID,
///   [5] ShowsScalp (when present; use the default scalp geoset instead).
/// </summary>
public sealed class CharHairGeosetsTable
{
    private readonly Dictionary<(uint Race, uint Sex, uint Style), int> _geosets = [];

    public const string MpqPath = @"DBFilesClient\CharHairGeosets.dbc";
    public int Count => _geosets.Count;

    public static CharHairGeosetsTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 5) return null;

        var table = new CharHairGeosetsTable();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint race = dbc.GetUInt(row, 1);
            uint sex = dbc.GetUInt(row, 2);
            uint style = dbc.GetUInt(row, 3);
            uint geoset = dbc.GetUInt(row, 4);
            bool showsScalp = dbc.FieldCount > 5 && dbc.GetUInt(row, 5) != 0;

            table._geosets[(race, sex, style)] = showsScalp ? 1 : (int)geoset;
        }

        Console.WriteLine($"[dbc] CharHairGeosets: {table.Count} mapping(s)");
        return table;
    }

    public int Find(uint race, uint sex, int style)
        => style >= 0 && _geosets.TryGetValue((race, sex, (uint)style), out int geoset)
            ? geoset
            : -1;
}

/// <summary>ItemDisplayInfo.dbc, indexed by display ID.</summary>
public sealed class ItemDisplayTable
{
    private readonly Dictionary<uint, ItemDisplayRow> _rows = [];

    public int Count => _rows.Count;
    public IReadOnlyCollection<ItemDisplayRow> All => _rows.Values;

    public ItemDisplayRow? Find(uint displayId)
        => _rows.TryGetValue(displayId, out var row) ? row : null;

    /// <summary>Vanilla path. Patched tables override it through the normal MPQ load order.</summary>
    public const string MpqPath = @"DBFilesClient\ItemDisplayInfo.dbc";

    private const int ExpectedRecordSize = 92;

    public static ItemDisplayTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        // Loud rather than silent: a different record size means a different
        // schema, and every field offset below would be reading the wrong
        // bytes while still producing plausible-looking numbers.
        if (dbc.RecordSize != ExpectedRecordSize)
        {
            Console.WriteLine(
                $"[dbc] ItemDisplayInfo record size is {dbc.RecordSize}, expected {ExpectedRecordSize} " +
                "for vanilla 1.12 - field offsets are probably wrong");
        }

        var table = new ItemDisplayTable();

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            var row = new ItemDisplayRow
            {
                Id = dbc.GetUInt(r, 0),
                ModelName1 = dbc.GetString(r, 1),
                ModelName2 = dbc.GetString(r, 2),
                ModelTexture1 = dbc.GetString(r, 3),
                ModelTexture2 = dbc.GetString(r, 4),
                InventoryIcon = dbc.GetString(r, 5),
                GeosetGroup = [dbc.GetInt(r, 6), dbc.GetInt(r, 7), dbc.GetInt(r, 8)],
                HelmetGeosetVis1 = dbc.GetUInt(r, 12),
                HelmetGeosetVis2 = dbc.GetUInt(r, 13),
                GroupSoundIndex = dbc.GetUInt(r, 11),
                ItemVisualId = dbc.GetUInt(r, 22),
                SpellVisualId = dbc.GetUInt(r, 10),
            };

            for (int t = 0; t < 8; t++)
                row.BodyTextures[t] = dbc.GetString(r, 14 + t);

            if (row.Id != 0) table._rows[row.Id] = row;
        }

        Console.WriteLine(
            $"[dbc] ItemDisplayInfo: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes each, {table.Count} indexed");

        return table;
    }
}

/// <summary>
/// GroundEffectDoodad.dbc - maps a ground-effect doodad ID to its grass/flower
/// M2 model path. Vanilla layout has an ID, an internal tag, the model filename
/// (stringref), flags and a couple of floats, and the exact field order shifted
/// across versions - so rather than hard-code an offset we SCAN each field for
/// the one stringref that resolves to a model path (.mdx/.m2/.mdl). Robust and
/// self-verifying; the record size is logged so a wrong parse is visible.
/// </summary>
public sealed class GroundEffectDoodadTable
{
    public const string MpqPath = @"DBFilesClient\GroundEffectDoodad.dbc";

    private readonly Dictionary<uint, string> _models = [];
    public int Count => _models.Count;
    public string? Model(uint id) => _models.TryGetValue(id, out var m) ? m : null;

    public static GroundEffectDoodadTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new GroundEffectDoodadTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            string model = "";
            for (int f = 1; f < dbc.FieldCount; f++)
            {
                // Only accept a value that points at the START of a string. The
                // model path shares the record with plain integer columns, and
                // because the whole string block is packed with model paths, an
                // integer misread as an offset almost always lands INSIDE one and
                // still ends in ".mdl"/".mdx" - a convincing but truncated fake
                // ("wFlo01.mdl" where the real path is "ElwGra01.mdl"), which then
                // fails to load. Requiring a genuine string start rejects those.
                string? s = dbc.GetStringIfStart(r, f);
                if (s is { Length: > 3 } &&
                    (s.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                     s.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
                     s.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
                { model = s; break; }
            }
            if (id != 0 && model.Length > 0) table._models[id] = model;
        }

        Console.WriteLine($"[dbc] GroundEffectDoodad: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._models.Count} with a model path");
        return table;
    }
}

/// <summary>One GroundEffectTexture row, resolved to model paths + weights.</summary>
public sealed class GroundEffectRecipe
{
    public (string Model, int Weight)[] Doodads = [];
    public int Density = 1;
}

/// <summary>
/// GroundEffectTexture.dbc - a ground-effect ID (from MCLY.EffectId) gives up to
/// four GroundEffectDoodad IDs plus a density (doodads scattered per cell). Two
/// column layouts exist and the field count tells them apart:
///
///   7 fields  (vanilla 1.12): ID, DoodadId[4], Density, Sound   - NO weights
///   11 fields (WotLK+):       ID, DoodadId[4], Weight[4], Density, Sound
///
/// This client loads the 7-field file, so density lives at field 5, not 9, and
/// there are no per-doodad weights (each doodad is equally likely). Reading the
/// wrong column left every recipe pinned to a fallback density, which scattered
/// grass far denser and more uniform than the data intends. The record size is
/// logged so a mismatch is obvious. Doodad IDs are resolved to model paths
/// through GroundEffectDoodad at parse time.
/// </summary>
public sealed class GroundEffectTextureTable
{
    public const string MpqPath = @"DBFilesClient\GroundEffectTexture.dbc";

    private readonly Dictionary<uint, GroundEffectRecipe> _byId = [];
    public int Count => _byId.Count;

    public GroundEffectRecipe? Get(int effectId)
        => effectId > 0 && _byId.TryGetValue((uint)effectId, out var r) ? r : null;

    public static GroundEffectTextureTable? Parse(byte[] data, GroundEffectDoodadTable doodads)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new GroundEffectTextureTable();

        // 11-field files carry a Weight[4] block before density; the 7-field
        // vanilla file has neither, so density sits at field 5 and every doodad
        // is equally weighted. See the class summary for both layouts.
        bool hasWeights = dbc.FieldCount >= 11;
        int densityField = hasWeights ? 9 : 5;

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;

            var list = new List<(string, int)>(4);
            for (int i = 0; i < 4; i++)
            {
                uint doodadId = dbc.GetUInt(r, 1 + i);
                if (doodadId == 0 || doodadId == 0xFFFFFFFF) continue;   // 0 / -1 = empty slot
                string? model = doodads.Model(doodadId);
                if (model is null) continue;
                int weight = hasWeights ? dbc.GetInt(r, 5 + i) : 1;
                list.Add((model, Math.Max(weight, 1)));
            }
            if (list.Count == 0) continue;

            int density = dbc.FieldCount > densityField ? dbc.GetInt(r, densityField) : 1;
            table._byId[id] = new GroundEffectRecipe
            {
                Doodads = list.ToArray(),
                Density = Math.Max(density, 1),
            };
        }

        Console.WriteLine($"[dbc] GroundEffectTexture: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._byId.Count} effect(s) with resolvable doodads");
        return table;
    }
}
// ============================================================================
// EXTERIOR LIGHTING — the Light.dbc chain (PLAN_09_EXTERIOR_LIGHTING.md §11).
//
//   Light.dbc          which light applies where, and how it falls off
//     -> LightParams.dbc     one setting-set per weather state
//        -> LightIntBand.dbc    18 colour curves over the day
//        -> LightFloatBand.dbc   6 scalar curves over the day
//
// Two unit traps, both of which produce results that look like bad data rather
// than like a bug:
//   * Positions and falloff radii are stored YARDS x 36. Converted once here,
//     at the reader boundary, so nothing downstream has to remember.
//   * Times are HALF-MINUTES from midnight, 0..2880. Also converted here, to
//     hours, for the same reason.
//
// Field indices come from wowdev.wiki and are verified against the record size
// on load, not trusted. GroundEffectTexture above records what a single wrong
// column costs: every recipe silently pinned to a fallback density, visible only
// as "the grass looks too thick". A wrong column here reads as slightly wrong
// colours, which is worse - so each table logs its shape and complains loudly
// when the geometry does not match 1.12.
// ============================================================================

/// <summary>One Light.dbc row: where a lighting setup applies, and how it fades.</summary>
public sealed class LightZone
{
    public uint Id;
    public uint MapId;

    /// <summary>Centre in WoW world space, YARDS (the x36 is already undone).</summary>
    public System.Numerics.Vector3 Position;

    /// <summary>Inside this radius the zone applies at full strength. Yards.</summary>
    public float FalloffStart;

    /// <summary>Past this radius the zone does not apply at all. Yards.</summary>
    public float FalloffEnd;

    public uint ParamsClear;
    public uint ParamsClearWater;
    public uint ParamsStorm;
    public uint ParamsStormWater;
    public uint ParamsDeath;

    /// <summary>
    /// A row at the origin with no radius is the MAP-WIDE DEFAULT rather than a
    /// zone at the map origin. Everything else blends on top of it.
    /// </summary>
    public bool IsMapDefault =>
        FalloffEnd <= 0f && Position.LengthSquared() <= 0.001f;
}

/// <summary>Light.dbc — vanilla 1.12 layout is 12 fields / 48 bytes.</summary>
public sealed class LightTable
{
    public const string MpqPath = @"DBFilesClient\Light.dbc";

    /// <summary>Stored x36. Undone at parse so callers only ever see yards.</summary>
    private const float DbcDistanceScale = 36f;

    private readonly List<LightZone> _zones = [];
    public IReadOnlyList<LightZone> Zones => _zones;
    public int Count => _zones.Count;

    public static LightTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        // 12 fields in 1.12; WotLK grew three more phase params on the end.
        // Fewer than 12 means the layout below is not this file's layout.
        if (dbc.FieldCount < 12)
        {
            Console.WriteLine($"[dbc] Light: {dbc.FieldCount} field(s) - expected at least 12 " +
                              "for the 1.12 layout. NOT LOADED; exterior lighting stays on its constants.");
            return null;
        }

        var table = new LightTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            var zone = new LightZone
            {
                Id = dbc.GetUInt(r, 0),
                MapId = dbc.GetUInt(r, 1),
                Position = new System.Numerics.Vector3(
                    dbc.GetFloat(r, 2) / DbcDistanceScale,
                    dbc.GetFloat(r, 3) / DbcDistanceScale,
                    dbc.GetFloat(r, 4) / DbcDistanceScale),
                FalloffStart = dbc.GetFloat(r, 5) / DbcDistanceScale,
                FalloffEnd = dbc.GetFloat(r, 6) / DbcDistanceScale,
                ParamsClear = dbc.GetUInt(r, 7),
                ParamsClearWater = dbc.GetUInt(r, 8),
                ParamsStorm = dbc.GetUInt(r, 9),
                ParamsStormWater = dbc.GetUInt(r, 10),
                ParamsDeath = dbc.GetUInt(r, 11),
            };
            if (zone.Id != 0) table._zones.Add(zone);
        }

        int defaults = table._zones.Count(z => z.IsMapDefault);
        Console.WriteLine($"[dbc] Light: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._zones.Count} zone(s), {defaults} map default(s)");
        return table;
    }

    /// <summary>Zones on one map, map default first so it can be the blend base.</summary>
    public List<LightZone> ForMap(uint mapId)
        => _zones.Where(z => z.MapId == mapId)
                 .OrderByDescending(z => z.IsMapDefault)
                 .ToList();
}

/// <summary>One LightParams.dbc row.</summary>
public sealed class LightParamsRow
{
    public uint Id;
    public bool HighlightSky;
    public uint SkyboxId;
    public float Glow;
    public float WaterShallowAlpha;
    public float WaterDeepAlpha;
    public float OceanShallowAlpha;
    public float OceanDeepAlpha;
    public uint Flags;
}

/// <summary>LightParams.dbc — 9 fields / 36 bytes in 1.12.</summary>
public sealed class LightParamsTable
{
    public const string MpqPath = @"DBFilesClient\LightParams.dbc";

    private readonly Dictionary<uint, LightParamsRow> _byId = [];
    public int Count => _byId.Count;
    public LightParamsRow? Get(uint id) => _byId.TryGetValue(id, out var p) ? p : null;

    public static LightParamsTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount < 9)
        {
            Console.WriteLine($"[dbc] LightParams: {dbc.FieldCount} field(s) - expected 9. NOT LOADED.");
            return null;
        }

        var table = new LightParamsTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            table._byId[id] = new LightParamsRow
            {
                Id = id,
                HighlightSky = dbc.GetUInt(r, 1) != 0,
                SkyboxId = dbc.GetUInt(r, 2),
                Glow = dbc.GetFloat(r, 3),
                WaterShallowAlpha = dbc.GetFloat(r, 4),
                WaterDeepAlpha = dbc.GetFloat(r, 5),
                OceanShallowAlpha = dbc.GetFloat(r, 6),
                OceanDeepAlpha = dbc.GetFloat(r, 7),
                Flags = dbc.GetUInt(r, 8),
            };
        }

        Console.WriteLine($"[dbc] LightParams: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._byId.Count} usable");
        return table;
    }
}

/// <summary>
/// LightSkybox.dbc (PLAN_18 Phase 2) - maps a LightParams.lightSkyboxID to the sky
/// MODEL to draw for that lighting state. 2 fields in 1.12: id, filename (an M2/MDX
/// under Environments\Stars\). Byte-checked against the client: 6 records
/// (StratholmeSkybox, PortalWorldLegionSky, DeathClouds, Stars, CavernsOfTimeSky,
/// DireMaulSkyBox), and only LightParams 1-5 reference one (all DeathClouds, the
/// ghost world); the instanced-zone skies come via the WMO MOSB path instead.
/// </summary>
public sealed class LightSkyboxTable
{
    public const string MpqPath = @"DBFilesClient\LightSkybox.dbc";

    private readonly Dictionary<uint, string> _byId = [];
    public int Count => _byId.Count;

    /// <summary>The model path for a skybox id, or null (id 0 or absent).</summary>
    public string? Path(uint id) => id != 0 && _byId.TryGetValue(id, out var p) ? p : null;

    public static LightSkyboxTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;
        if (dbc.FieldCount < 2)
        {
            Console.WriteLine($"[dbc] LightSkybox: {dbc.FieldCount} field(s) - expected >= 2. NOT LOADED.");
            return null;
        }

        var table = new LightSkyboxTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            string path = dbc.GetString(r, 1);
            if (!string.IsNullOrWhiteSpace(path)) table._byId[id] = path;
        }

        Console.WriteLine($"[dbc] LightSkybox: {dbc.RecordCount} record(s), {table._byId.Count} usable");
        return table;
    }
}

/// <summary>
/// One SCALAR band: up to 16 (time, value) keys forming a curve over the day.
///
/// Times arrive as half-minutes 0..2880 and are stored here as HOURS, so
/// everything downstream speaks one unit. The curve WRAPS - the segment from the
/// last key to the first crosses midnight, and a band that does not wrap
/// produces a hard snap at 00:00 that reads as a rendering glitch.
///
/// COLOURS DO NOT USE THIS CLASS, and that separation is deliberate. See
/// <see cref="LightColorBand"/> for why.
/// </summary>
public sealed class LightBand
{
    public float[] Hours = [];
    public float[] Values = [];

    public bool HasData => Hours.Length > 0;

    /// <summary>Sample the curve, wrapping across midnight. 0 if the band is empty.</summary>
    public float Sample(float hours)
    {
        int n = Hours.Length;
        if (n == 0) return 0f;
        if (n == 1) return Values[0];

        hours %= 24f;
        if (hours < 0f) hours += 24f;

        // Before the first key or after the last: we are inside the wrap
        // segment that spans midnight.
        if (hours < Hours[0] || hours >= Hours[n - 1])
        {
            float from = Hours[n - 1];
            float to = Hours[0] + 24f;
            float at = hours < Hours[0] ? hours + 24f : hours;
            float span = to - from;
            float t = span <= 0f ? 0f : (at - from) / span;
            return Values[n - 1] + (Values[0] - Values[n - 1]) * t;
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (hours >= Hours[i] && hours < Hours[i + 1])
            {
                float span = Hours[i + 1] - Hours[i];
                float t = span <= 0f ? 0f : (hours - Hours[i]) / span;
                return Values[i] + (Values[i + 1] - Values[i]) * t;
            }
        }
        return Values[n - 1];
    }
}

/// <summary>
/// One COLOUR band: the same 16 keys, but the value is a PACKED colour and is
/// kept packed until the moment it is sampled.
///
/// This exists because of a real bug, caught by the probe on its first run
/// (PLAN_09 §7). Sharing <see cref="LightBand"/> meant colours were interpolated
/// as SINGLE NUMBERS: lerping 0x0000FF toward 0xFF0000 carries across the byte
/// boundaries and lands on a colour that belongs to neither key. The symptom was
/// green ambient, cyan fog and a dark purple sun at 11:11, while every scalar
/// band in the same rows read perfectly plausibly - because lerping a float is
/// meaningful and lerping a packed RGB is not.
///
/// So: find the bracketing keys, decode BOTH, then interpolate per channel.
/// Decoding stays at sample time rather than at parse time so
/// <see cref="LightIntBandTable.SwapRedBlue"/> remains a live toggle.
/// </summary>
public sealed class LightColorBand
{
    public float[] Hours = [];
    public uint[] Packed = [];

    public bool HasData => Hours.Length > 0;

    public System.Numerics.Vector3 Sample(float hours)
    {
        int n = Hours.Length;
        if (n == 0) return System.Numerics.Vector3.Zero;
        if (n == 1) return LightIntBandTable.Decode(Packed[0]);

        hours %= 24f;
        if (hours < 0f) hours += 24f;

        int i0, i1;
        float t;

        if (hours < Hours[0] || hours >= Hours[n - 1])
        {
            // The wrap segment: last key -> first key, across midnight.
            i0 = n - 1;
            i1 = 0;
            float from = Hours[n - 1];
            float to = Hours[0] + 24f;
            float at = hours < Hours[0] ? hours + 24f : hours;
            float span = to - from;
            t = span <= 0f ? 0f : (at - from) / span;
        }
        else
        {
            i0 = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (hours >= Hours[i] && hours < Hours[i + 1]) { i0 = i; break; }
                i0 = i + 1;
            }
            i1 = Math.Min(i0 + 1, n - 1);
            float span = Hours[i1] - Hours[i0];
            t = span <= 0f ? 0f : (hours - Hours[i0]) / span;
        }

        var a = LightIntBandTable.Decode(Packed[i0]);
        var b = LightIntBandTable.Decode(Packed[i1]);
        return System.Numerics.Vector3.Lerp(a, b, Math.Clamp(t, 0f, 1f));
    }

    /// <summary>The raw keys, for the probe. A wrong curve is obvious in a list.</summary>
    public string Describe()
    {
        if (!HasData) return "(no keys)";
        var parts = new string[Hours.Length];
        for (int i = 0; i < Hours.Length; i++)
        {
            var c = LightIntBandTable.Decode(Packed[i]);
            parts[i] = $"{Hours[i]:F2}h={c.X:F2}/{c.Y:F2}/{c.Z:F2}";
        }
        return string.Join("  ", parts);
    }
}

/// <summary>
/// Shared layout for LightIntBand and LightFloatBand: id, entry count, 16 times,
/// 16 values. 34 fields / 136 bytes. Only the value type differs.
/// </summary>
public static class LightBandLayout
{
    public const int FieldCount = 34;
    public const int MaxEntries = 16;
    public const int TimeField = 2;
    public const int ValueField = 18;

    /// <summary>Half-minutes from midnight, 0..2880, to hours.</summary>
    public const float HalfMinutesPerHour = 120f;
}

/// <summary>
/// LightIntBand.dbc — the 18 colour curves per LightParams.
///
/// Band rows for LightParams P are ids `P*18-17 .. P*18`, i.e. band b is
/// id `P*18-17+b`. Looked up BY ID rather than by row index: the two usually
/// coincide and relying on that is exactly the assumption that breaks quietly.
/// </summary>
public sealed class LightIntBandTable
{
    public const string MpqPath = @"DBFilesClient\LightIntBand.dbc";

    /// <summary>Names for the 18 slots, so the probe reads as English.</summary>
    public static readonly string[] BandNames =
    [
        "global diffuse", "global ambient",
        "sky top", "sky middle", "sky band 1", "sky band 2", "sky smog",
        "fog", "sun", "cloud sun", "cloud emissive",
        "cloud L1 ambient", "cloud L2 ambient",
        "ocean close", "ocean far", "river close", "river far",
        "shadow opacity",
    ];

    public const int BandsPerParams = 18;

    // Named indices for the bands other systems consume, so a call site reads
    // as intent instead of as a magic number. LightFloatBandTable already does
    // this for fog; these four are PLAN_12's (SYSTEM_WATER.md section 5).
    public const int OceanCloseBand = 13;
    public const int OceanFarBand   = 14;
    public const int RiverCloseBand = 15;
    public const int RiverFarBand   = 16;

    // The cloud palette (PLAN_18). benilla's byte-exact cloud kernel reads these
    // three: sub-10 is the sun-GLOW palette, sub-11 the gradient SLOPE, sub-12 the
    // gradient BASE (benilla-app/src/lighting/mod.rs cloud_colors[0..2]). Note the
    // DbcReader band NAMES ("cloud emissive"/"cloud L1/L2 ambient") predate that
    // finding; the roles above are the ones WoW.exe's 0x6cfb00 colour pass uses.
    public const int CloudSunGlowBand = 10;
    public const int CloudSlopeBand   = 11;
    public const int CloudBaseBand    = 12;

    // Colour channel order is the one thing here the schema pages do not agree
    // on, and it cannot be settled by reading. It IS settled by looking: the sky
    // top at noon must be strongly BLUE. If it comes out red-dominant this flag
    // is wrong, so it is a flag and not a hard-coded shuffle. The probe shows the
    // raw hex beside the decoded triplet for exactly this check.
    public static bool SwapRedBlue;

    private readonly Dictionary<uint, LightColorBand> _byId = [];
    public int Count => _byId.Count;

    /// <summary>Band b (0..17) for a LightParams id, or null when unauthored.</summary>
    public LightColorBand? Band(uint lightParamsId, int band)
    {
        if (lightParamsId == 0 || band < 0 || band >= BandsPerParams) return null;
        uint id = lightParamsId * BandsPerParams - 17 + (uint)band;
        return _byId.TryGetValue(id, out var b) && b.HasData ? b : null;
    }

    /// <summary>Packed colour to 0..1 RGB. See <see cref="SwapRedBlue"/>.</summary>
    public static System.Numerics.Vector3 Decode(uint v)
    {
        float r = ((v >> 16) & 0xFF) / 255f;
        float g = ((v >> 8) & 0xFF) / 255f;
        float b = (v & 0xFF) / 255f;
        return SwapRedBlue
            ? new System.Numerics.Vector3(b, g, r)
            : new System.Numerics.Vector3(r, g, b);
    }

    /// <summary>
    /// Sample a colour band. The per-channel interpolation lives in
    /// LightColorBand.Sample - do NOT reintroduce a version that lerps the
    /// packed value, which is the bug this table was rewritten to remove.
    /// </summary>
    public System.Numerics.Vector3 SampleColor(uint lightParamsId, int band, float hours)
        => Band(lightParamsId, band)?.Sample(hours) ?? System.Numerics.Vector3.Zero;

    public static LightIntBandTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount < LightBandLayout.FieldCount)
        {
            Console.WriteLine($"[dbc] LightIntBand: {dbc.FieldCount} field(s) - expected " +
                              $"{LightBandLayout.FieldCount}. NOT LOADED.");
            return null;
        }

        var table = new LightIntBandTable();
        int withData = 0;
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            var band = ReadColorBand(dbc, r);
            table._byId[id] = band;
            if (band.HasData) withData++;
        }

        Console.WriteLine($"[dbc] LightIntBand: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {withData} band(s) with keys");
        return table;
    }

    /// <summary>
    /// Read one band row. `numEntries` is trusted only as far as the array
    /// allows, and a row claiming more than 16 keys is clamped rather than
    /// throwing - a corrupt count should cost a band, not the client.
    /// </summary>
    internal static LightColorBand ReadColorBand(DbcFile dbc, int row)
    {
        int n = Math.Clamp(dbc.GetInt(row, 1), 0, LightBandLayout.MaxEntries);
        var hours = new float[n];
        var packed = new uint[n];
        for (int i = 0; i < n; i++)
        {
            hours[i] = dbc.GetInt(row, LightBandLayout.TimeField + i)
                     / LightBandLayout.HalfMinutesPerHour;
            packed[i] = dbc.GetUInt(row, LightBandLayout.ValueField + i);
        }
        return new LightColorBand { Hours = hours, Packed = packed };
    }

    /// <summary>Scalar rows. Same layout, meaningful to interpolate as numbers.</summary>
    internal static LightBand ReadFloatBand(DbcFile dbc, int row)
    {
        int n = Math.Clamp(dbc.GetInt(row, 1), 0, LightBandLayout.MaxEntries);
        var hours = new float[n];
        var values = new float[n];
        for (int i = 0; i < n; i++)
        {
            hours[i] = dbc.GetInt(row, LightBandLayout.TimeField + i)
                     / LightBandLayout.HalfMinutesPerHour;
            values[i] = dbc.GetFloat(row, LightBandLayout.ValueField + i);
        }
        return new LightBand { Hours = hours, Values = values };
    }
}

/// <summary>
/// LightFloatBand.dbc — the 6 scalar curves per LightParams.
/// Band rows for LightParams P are ids `P*6-5 .. P*6`.
/// </summary>
public sealed class LightFloatBandTable
{
    public const string MpqPath = @"DBFilesClient\LightFloatBand.dbc";

    public static readonly string[] BandNames =
    [
        "fog end", "fog start multiplier", "celestial glow through",
        "cloud density", "unknown 4", "unknown 5",
    ];

    public const int BandsPerParams = 6;

    /// <summary>Band 0 is a distance and is stored x36, like Light.dbc's radii.</summary>
    public const int FogEndBand = 0;
    public const int FogStartMultiplierBand = 1;
    /// <summary>Cloud coverage density C (PLAN_18) - the cloud kernel's threshold input.</summary>
    public const int CloudDensityBand = 3;
    private const float DbcDistanceScale = 36f;

    private readonly Dictionary<uint, LightBand> _byId = [];
    public int Count => _byId.Count;

    public LightBand? Band(uint lightParamsId, int band)
    {
        if (lightParamsId == 0 || band < 0 || band >= BandsPerParams) return null;
        uint id = lightParamsId * BandsPerParams - 5 + (uint)band;
        return _byId.TryGetValue(id, out var b) && b.HasData ? b : null;
    }

    /// <summary>
    /// Sample a float band. Band 0 is converted from the stored x36 to yards
    /// here so no caller has to know which bands are distances.
    /// </summary>
    public float Sample(uint lightParamsId, int band, float hours)
    {
        var b = Band(lightParamsId, band);
        if (b is null) return 0f;
        float v = b.Sample(hours);
        return band == FogEndBand ? v / DbcDistanceScale : v;
    }

    public static LightFloatBandTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount < LightBandLayout.FieldCount)
        {
            Console.WriteLine($"[dbc] LightFloatBand: {dbc.FieldCount} field(s) - expected " +
                              $"{LightBandLayout.FieldCount}. NOT LOADED.");
            return null;
        }

        var table = new LightFloatBandTable();
        int withData = 0;
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            var band = LightIntBandTable.ReadFloatBand(dbc, r);
            table._byId[id] = band;
            if (band.HasData) withData++;
        }

        Console.WriteLine($"[dbc] LightFloatBand: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {withData} band(s) with keys");
        return table;
    }
}

// ============================================================================
// MAPS — Map.dbc (PLAN_13_INSTANCES.md section 1.1).
//
// 44 rows in 1.12, 42 fields, 168-byte records. This is the list of every
// world the client can be standing in, and the Directory column is what turns
// a map id into a path: World\Maps\{Directory}\{Directory}.wdt and every ADT
// under it.
//
// RESOLUTION ORDER MATTERS. Map.dbc ships in patch.MPQ, patch-2.MPQ and
// dbc.MPQ, and patch.MPQ wins. MpqMount's load order already gets this right,
// and reading the dbc.MPQ copy instead would be the kind of bug that looks
// like bad data.
//
// Column indices verified against the file, not trusted to a wiki: ids 33
// Shadowfang, 36 Deadmines, 43 Wailing Caverns, 90 Gnomeregan and 189 Scarlet
// Monastery all land on the right names with this layout.
// ============================================================================

/// <summary>What kind of world a map is, from Map.dbc's instanceType column.</summary>
public enum MapInstanceType
{
    World = 0,
    Dungeon = 1,
    Raid = 2,
    Battleground = 3,
}

/// <summary>One Map.dbc row.</summary>
public sealed class MapRow
{
    public int Id { get; init; }

    /// <summary>
    /// The on-disk directory name — "Azeroth", "DeadminesInstance". This is
    /// what AdtCache takes as its map name, and it is NOT the display name.
    /// </summary>
    public string Directory { get; init; } = "";

    /// <summary>The enUS display name — "Eastern Kingdoms", "Deadmines".</summary>
    public string Name { get; init; } = "";

    public MapInstanceType InstanceType { get; init; }

    /// <summary>
    /// LoadingScreenID (Map.dbc field 38) - an FK into LoadingScreens.dbc that
    /// selects this map's full-screen load art. 0 = no screen (dev/test maps).
    /// Verified against build 5875: Azeroth(0) -> 4, Kalimdor(1) -> 3.
    /// </summary>
    public int LoadingScreenId { get; init; }

    public bool IsInstance => InstanceType != MapInstanceType.World;

    public override string ToString() => $"{Id} {Name} ({Directory})";
}

public sealed class MapTable
{
    public const string MpqPath = @"DBFilesClient\Map.dbc";

    // Column layout of the 1.12 file. Fields 5..12 are the other locales and
    // field 13 is the locale mask; nothing here needs them.
    private const int FieldId = 0;
    private const int FieldDirectory = 1;
    private const int FieldInstanceType = 2;
    private const int FieldNameEnUs = 4;

    /// <summary>
    /// LoadingScreenID FK (build 5875: field 38, an id into LoadingScreens.dbc).
    /// Read only when the file actually has this many fields, so a truncated or
    /// unexpected DBC degrades to "no art" rather than reading past the record.
    /// </summary>
    private const int FieldLoadingScreen = 38;

    private readonly Dictionary<int, MapRow> _byId = [];
    private readonly List<MapRow> _all = [];

    /// <summary>Every row, in file order (which is ascending id in 1.12).</summary>
    public IReadOnlyList<MapRow> All => _all;
    public int Count => _all.Count;

    public MapRow? Get(int id) => _byId.TryGetValue(id, out var r) ? r : null;

    public MapRow? ByDirectory(string directory)
    {
        foreach (var r in _all)
            if (string.Equals(r.Directory, directory, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    public static MapTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount <= FieldNameEnUs)
        {
            Console.WriteLine($"[dbc] Map: {dbc.FieldCount} field(s), expected more than " +
                              $"{FieldNameEnUs}. NOT LOADED.");
            return null;
        }

        var table = new MapTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            string dir = dbc.GetString(r, FieldDirectory);
            if (string.IsNullOrWhiteSpace(dir)) continue;

            uint type = dbc.GetUInt(r, FieldInstanceType);
            var row = new MapRow
            {
                Id = dbc.GetInt(r, FieldId),
                Directory = dir,
                Name = dbc.GetString(r, FieldNameEnUs),
                InstanceType = type <= 3 ? (MapInstanceType)type : MapInstanceType.World,
                LoadingScreenId = dbc.FieldCount > FieldLoadingScreen
                    ? dbc.GetInt(r, FieldLoadingScreen) : 0,
            };

            table._all.Add(row);
            table._byId[row.Id] = row;
        }

        Console.WriteLine($"[dbc] Map: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._all.Count} map(s) with a directory");
        return table;
    }
}

/// <summary>
/// LoadingScreens.dbc - resolves a LoadingScreenID (Map.dbc field 38) to the
/// map's full-screen load-art BLP path.
///
/// Verified against build 5875 (benilla-formats/loading_screen.rs): 3 fields
/// (ID, Name string, FileName string), 12-byte records; field 0 = id, field 2 =
/// the BLP path (e.g. Interface\Glues\LoadingScreens\LoadScreenEasternKingdom.blp).
/// Continent art is shared - id 4 = Eastern Kingdoms, id 3 = Kalimdor - and the
/// other rows are per-instance. Row order is NOT id order, so the id->path map
/// is built by scanning field 0 rather than indexing by record.
///
/// NO GL - Formats/ rule. The caller decodes the BLP and uploads the texture.
/// </summary>
public sealed class LoadingScreenTable
{
    public const string MpqPath = @"DBFilesClient\LoadingScreens.dbc";

    private const int FieldId = 0;
    private const int FieldFileName = 2;

    private readonly Dictionary<int, string> _byId = [];

    /// <summary>The load-art BLP path for a LoadingScreenID, or null if absent.</summary>
    public string? PathFor(int id) => _byId.TryGetValue(id, out var p) ? p : null;

    public int Count => _byId.Count;

    public static LoadingScreenTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount <= FieldFileName)
        {
            Console.WriteLine($"[dbc] LoadingScreens: {dbc.FieldCount} field(s), expected more " +
                              $"than {FieldFileName}. NOT LOADED.");
            return null;
        }

        var table = new LoadingScreenTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            string path = dbc.GetString(r, FieldFileName);
            if (string.IsNullOrWhiteSpace(path)) continue;
            table._byId[dbc.GetInt(r, FieldId)] = path;
        }

        Console.WriteLine($"[dbc] LoadingScreens: {dbc.RecordCount} record(s); " +
                          $"{table._byId.Count} screen(s) catalogued");
        return table;
    }
}

/// <summary>
/// GameObjectDisplayInfo.dbc — displayId → model path for SERVER-SPAWNED
/// gameobjects (shop signs, mailboxes, chests, doors, herb/ore nodes). These
/// models are in no ADT/WMO placement list — Stormwind's shop signs sit in
/// Stormwind.wmo's MODN name table with ZERO MODD placements — so without this
/// table a server gameobject has no visual at all.
///
/// Verified against build 5875: 1,638 rows, 12 fields, 48-byte records;
/// field 0 = id, field 1 = the model path string (WORLD\...\*.MDL / *.MDX —
/// swapped to .m2 by the doodad loader's PathCandidates, like every MDDF
/// placement). Fields 2-11 (sound ids, unused) are ignored. (2026-08-12)
/// </summary>
public sealed class GameObjectDisplayTable
{
    public const string MpqPath = @"DBFilesClient\GameObjectDisplayInfo.dbc";

    private const int FieldId = 0;
    private const int FieldModelPath = 1;

    private readonly Dictionary<uint, string> _byId = [];

    public int Count => _byId.Count;

    /// <summary>The model path for a GAMEOBJECT_DISPLAYID, or null when the row
    /// is absent or authored without a model (invisible triggers, traps).</summary>
    public string? ModelPath(uint displayId) =>
        _byId.TryGetValue(displayId, out string? path) ? path : null;

    public static GameObjectDisplayTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount <= FieldModelPath)
        {
            Console.WriteLine($"[dbc] GameObjectDisplayInfo: {dbc.FieldCount} field(s), expected " +
                              $"more than {FieldModelPath}. NOT LOADED.");
            return null;
        }

        var table = new GameObjectDisplayTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            string path = dbc.GetString(r, FieldModelPath);
            if (string.IsNullOrWhiteSpace(path)) continue;
            table._byId[dbc.GetUInt(r, FieldId)] = path;
        }

        Console.WriteLine($"[dbc] GameObjectDisplayInfo: {dbc.RecordCount} record(s), " +
                          $"{dbc.FieldCount} field(s), {table._byId.Count} with a model path");
        return table;
    }
}

/// <summary>Build-5875 BankBagSlotPrices.dbc: id + copper price.</summary>
public sealed class BankBagSlotPriceTable
{
    public const string MpqPath = @"DBFilesClient\BankBagSlotPrices.dbc";
    private readonly Dictionary<int, uint> _prices = [];
    public uint Price(int oneBasedSlot) => _prices.GetValueOrDefault(oneBasedSlot);

    public static BankBagSlotPriceTable? Parse(byte[] data)
    {
        DbcFile? dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var table = new BankBagSlotPriceTable();
        for (int row = 0; row < dbc.RecordCount; row++)
            table._prices[dbc.GetInt(row, 0)] = dbc.GetUInt(row, 1);
        return table;
    }
}

// ============================================================================
// AREA TRIGGERS — AreaTrigger.dbc.
//
// 432 rows in 1.12, 10 fields, 40-byte records. Field 0 id, 1 map, 2-4 the
// position, 5 a sphere radius, 6-9 an oriented box (length, width, height,
// yaw). A row uses ONE of the two shapes: 352 are spheres with a radius and a
// zeroed box, 80 are boxes with a zero radius. Measured, not assumed.
//
// WHAT THIS TABLE IS AND IS NOT
//   It is every trigger VOLUME in the game, on both sides of every portal: the
//   one in the Moonbrook mineshaft (id 78, map 0, radius 7) and the ones inside
//   the Deadmines (map 36) alike.
//
//   It is NOT the destinations. Nothing in the client says where a trigger
//   sends you, or even which direction it points - that lives in the server's
//   areatrigger_teleport table. Checked and ruled out on the way here:
//   AreaPOI.dbc is landmarks ("Echo Ridge Mine", "Sentinel Hill"), and
//   pfQuest's areatrigger.lua carries only map-pin percentages.
//
// WHAT IT IS STILL WORTH ON ITS OWN
//   Every instance map's triggers sit INSIDE that instance's playable space,
//   which makes them far better arrival points than any geometric guess.
//   Deadmines' tile-cluster centre is (-267, -267); its content, measured from
//   the built collision BVH, spans X -316..162 Y -1050..-342. The centre misses
//   the dungeon by 469 yards and you arrive on empty terrain with all 698
//   doodads distance-culled. Trigger 119 at (-14, -393, 65) is inside it.
// ============================================================================

/// <summary>One AreaTrigger.dbc row: a sphere or an oriented box on some map.</summary>
public sealed class AreaTriggerRow
{
    public int Id { get; init; }
    public int MapId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    /// <summary>Sphere radius in yards. Zero when this row is a box.</summary>
    public float Radius { get; init; }

    public float BoxLength { get; init; }
    public float BoxWidth { get; init; }
    public float BoxHeight { get; init; }

    /// <summary>Box yaw in radians. Meaningless when <see cref="IsSphere"/>.</summary>
    public float BoxYaw { get; init; }

    public bool IsSphere => Radius > 0f;

    /// <summary>
    /// Is this point inside the volume? Sphere is a plain distance test; box is
    /// the point rotated into the box's frame and compared against half-extents.
    ///
    /// Z is treated as a half-height about the centre for boxes, and as part of
    /// the sphere for spheres. Vanilla's server does the same, and it is why a
    /// box trigger in a stairwell does not fire from the floor below.
    /// </summary>
    public bool Contains(Vector3 p)
    {
        float dx = p.X - X, dy = p.Y - Y, dz = p.Z - Z;

        if (IsSphere)
            return dx * dx + dy * dy + dz * dz <= Radius * Radius;

        if (MathF.Abs(dz) > BoxHeight * 0.5f) return false;

        float cos = MathF.Cos(-BoxYaw), sin = MathF.Sin(-BoxYaw);
        float rx = dx * cos - dy * sin;
        float ry = dx * sin + dy * cos;
        return MathF.Abs(rx) <= BoxLength * 0.5f
            && MathF.Abs(ry) <= BoxWidth * 0.5f;
    }

    public override string ToString()
        => IsSphere
            ? $"{Id} map {MapId} ({X:F1},{Y:F1},{Z:F1}) r{Radius:F1}"
            : $"{Id} map {MapId} ({X:F1},{Y:F1},{Z:F1}) box {BoxLength:F1}x{BoxWidth:F1}x{BoxHeight:F1} yaw {BoxYaw:F2}";
}

public sealed class AreaTriggerTable
{
    public const string MpqPath = @"DBFilesClient\AreaTrigger.dbc";

    private const int FieldId = 0;
    private const int FieldMap = 1;
    private const int FieldX = 2;
    private const int FieldRadius = 5;
    private const int FieldBoxLength = 6;

    private readonly List<AreaTriggerRow> _all = [];
    private readonly Dictionary<int, List<AreaTriggerRow>> _byMap = [];

    public IReadOnlyList<AreaTriggerRow> All => _all;
    public int Count => _all.Count;

    /// <summary>Every trigger on a map, in file order. Empty list, never null.</summary>
    public IReadOnlyList<AreaTriggerRow> ForMap(int mapId)
        => _byMap.TryGetValue(mapId, out var list) ? list : [];

    /// <summary>The trigger on this map whose centre is nearest a point, or null.</summary>
    public AreaTriggerRow? NearestOnMap(int mapId, Vector3 to)
    {
        AreaTriggerRow? best = null;
        float bestSq = float.MaxValue;
        foreach (var t in ForMap(mapId))
        {
            float dx = t.X - to.X, dy = t.Y - to.Y, dz = t.Z - to.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= bestSq) continue;
            bestSq = d;
            best = t;
        }
        return best;
    }

    /// <summary>
    /// Nearest trigger measured in XY only. Use this when picking somewhere to
    /// arrive: Gnomeregan's triggers run from Z -149 to -241 and Deadmines' from
    /// 35 to 65, so folding Z into the distance ranks them by depth as much as
    /// by where they are, which is not the question being asked.
    /// </summary>
    public AreaTriggerRow? NearestOnMapXY(int mapId, Vector2 to)
    {
        AreaTriggerRow? best = null;
        float bestSq = float.MaxValue;
        foreach (var t in ForMap(mapId))
        {
            float dx = t.X - to.X, dy = t.Y - to.Y;
            float d = dx * dx + dy * dy;
            if (d >= bestSq) continue;
            bestSq = d;
            best = t;
        }
        return best;
    }

    /// <summary>The first trigger on this map containing the point, or null.</summary>
    public AreaTriggerRow? Containing(int mapId, Vector3 p)
    {
        foreach (var t in ForMap(mapId))
            if (t.Contains(p)) return t;
        return null;
    }

    public static AreaTriggerTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount < 10)
        {
            Console.WriteLine($"[dbc] AreaTrigger: {dbc.FieldCount} field(s), expected 10. NOT LOADED.");
            return null;
        }

        var table = new AreaTriggerTable();
        int spheres = 0, boxes = 0;

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            int id = dbc.GetInt(r, FieldId);
            if (id == 0) continue;

            var row = new AreaTriggerRow
            {
                Id = id,
                MapId = dbc.GetInt(r, FieldMap),
                X = dbc.GetFloat(r, FieldX),
                Y = dbc.GetFloat(r, FieldX + 1),
                Z = dbc.GetFloat(r, FieldX + 2),
                Radius = dbc.GetFloat(r, FieldRadius),
                BoxLength = dbc.GetFloat(r, FieldBoxLength),
                BoxWidth = dbc.GetFloat(r, FieldBoxLength + 1),
                BoxHeight = dbc.GetFloat(r, FieldBoxLength + 2),
                BoxYaw = dbc.GetFloat(r, FieldBoxLength + 3),
            };

            if (row.IsSphere) spheres++; else boxes++;

            table._all.Add(row);
            if (!table._byMap.TryGetValue(row.MapId, out var list))
                table._byMap[row.MapId] = list = [];
            list.Add(row);
        }

        Console.WriteLine($"[dbc] AreaTrigger: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {spheres} sphere(s), {boxes} box(es) over {table._byMap.Count} map(s)");
        return table;
    }
}
