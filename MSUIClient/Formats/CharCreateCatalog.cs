namespace MSUIClient.Formats;

// The character-creation DATA catalog: which classes each race may be, and the five
// appearance-dial counts per (race, sex). Byte-faithful to benilla's
// crate::entities::CharCreate (see SPEC_CHARACTER_CREATE.md section 9):
//
//   classes_for_race / allows  <- CharBaseInfo.dbc
//   dial ranges (skin/face/hairStyle/hairColor/facialHair) <- CharSections.dbc +
//                              CharHairGeosets.dbc + CharacterFacialHairStyles.dbc
//
// GROUND TRUTH verified against Nico's own DBCs (dbc.MPQ, 2026-07-28):
//   * CharBaseInfo.dbc  : WDBC, 41 records, recordSize=2, fieldCount=2 -> each record
//     is two BYTES (raceId u8, classId u8), NOT the usual 4-byte fields. So the class
//     lists come out: race1 [1,2,4,5,8,9]  race2 [1,3,4,7,9]  race3 [1,2,3,4,5,8]
//     race4 [1,3,4,5,11]  race5 [1,4,5,8,9]  race6 [1,3,7,11]  race7 [1,4,8,9]
//     race8 [1,3,4,5,7,8].
//   * CharSections.dbc  : 10 fields / 40-byte records. [1]Race [2]Sex [3]BaseSection
//     [4]Variation [5]Color. BaseSection enum = 0 Skin, 1 Face, 2 FacialHair, 3 Hair,
//     4 Underwear. Dials: skin = distinct Color @section0, face = distinct Variation
//     @section1, hairColor = distinct Color @section3.
//   * CharHairGeosets.dbc: [1]Race [2]Sex [3]Variation -> hairStyle = distinct Variation.
//   * CharacterFacialHairStyles.dbc: NO id column. [0]Race [1]Sex [2]Variation ->
//     facialHair = distinct Variation.
//   Sanity: Human male -> skin 10, face 12, hairStyle 12, hairColor 10, facialHair 9;
//           Orc male   -> 9, 9, 7, 8, 11.
//
// This class parses the four DBCs with its OWN minimal WDBC reader (rather than the
// shared DbcFile) so it can read CharBaseInfo's 2-byte records, and so nothing existing
// has to change. Load once; it drives the create screen's class grid + dial ranges.
public sealed class CharCreateCatalog
{
    public const string CharBaseInfoPath = @"DBFilesClient\CharBaseInfo.dbc";
    public const string CharSectionsPath = @"DBFilesClient\CharSections.dbc";
    public const string CharHairGeosetsPath = @"DBFilesClient\CharHairGeosets.dbc";
    public const string FacialHairPath = @"DBFilesClient\CharacterFacialHairStyles.dbc";
    public const string CharStartOutfitPath = @"DBFilesClient\CharStartOutfit.dbc";
    public const string ChrRacesPath = @"DBFilesClient\ChrRaces.dbc";

    // race -> valid classes (ascending file order, deduped) from CharBaseInfo.dbc, filtered by
    // Creatable112.
    private readonly Dictionary<byte, List<byte>> _classes = new();

    /// <summary>The race/class pairings a 1.12 character can actually be created as (ChrRaces
    /// id -> ChrClasses ids). The stock archives are NOT the whole truth here: build 5875's
    /// CharBaseInfo.dbc carries 41 rows and CharStartOutfit.dbc dresses all of them, and the
    /// 41st is a Dwarf MAGE - leftover data the live 1.12 client never offered and that no
    /// server's playercreateinfo has a row for, so creating one only ever answers "Error
    /// creating character" (owner, 2026-09-03: "Dwarf mages in our client... we need to fix
    /// that"). Everything the DBC lists is intersected with this table.</summary>
    public static readonly IReadOnlyDictionary<byte, byte[]> Creatable112 = new Dictionary<byte, byte[]>
    {
        [1] = [1, 2, 4, 5, 8, 9],       // Human: Warrior Paladin Rogue Priest Mage Warlock
        [2] = [1, 3, 4, 7, 9],          // Orc: Warrior Hunter Rogue Shaman Warlock
        [3] = [1, 2, 3, 4, 5],          // Dwarf: Warrior Paladin Hunter Rogue Priest (no Mage)
        [4] = [1, 3, 4, 5, 11],         // Night Elf: Warrior Hunter Rogue Priest Druid
        [5] = [1, 4, 5, 8, 9],          // Undead: Warrior Rogue Priest Mage Warlock
        [6] = [1, 3, 7, 11],            // Tauren: Warrior Hunter Shaman Druid
        [7] = [1, 4, 8, 9],             // Gnome: Warrior Rogue Mage Warlock
        [8] = [1, 3, 4, 5, 7, 8],       // Troll: Warrior Hunter Rogue Priest Shaman Mage
    };

    public static bool IsCreatable112(byte race, byte cls) =>
        Creatable112.TryGetValue(race, out byte[]? classes) && Array.IndexOf(classes, cls) >= 0;
    // (race, sex) -> [skin, face, hairStyle, hairColor, facialHair] counts.
    private readonly Dictionary<(byte Race, byte Sex), int[]> _dials = new();
    // (race, class, sex) -> the level-1 starting outfit as (ItemDisplayInfo id, InventoryType) pairs.
    private readonly Dictionary<(byte Race, byte Class, byte Sex), List<(uint DisplayId, int InvType)>> _outfits = new();
    // race -> ([male facial token, female facial token], shared hair token), ChrRaces 26/27/28.
    private readonly Dictionary<byte, (string[] Facial, string Hair)> _customizationTokens = new();

    /// <summary>True once the class map loaded (a create screen can render even without dials).</summary>
    public bool Ok { get; private set; }

    /// <summary>The classes this race may be, ascending class id (CharBaseInfo file order).</summary>
    public IReadOnlyList<byte> ClassesForRace(byte race) =>
        _classes.TryGetValue(race, out var list) ? list : Array.Empty<byte>();

    /// <summary>Whether the given (race, class) pairing exists in CharBaseInfo.</summary>
    public bool Allows(byte race, byte cls) =>
        _classes.TryGetValue(race, out var list) && list.Contains(cls);

    /// <summary>[skin, face, hairStyle, hairColor, facialHair] dial counts for a (race, sex).</summary>
    public int[] DialCounts(byte race, byte sex) =>
        _dials.TryGetValue((race, sex), out var c) ? c : new[] { 1, 1, 1, 1, 1 };

    /// <summary>The (race,class,sex) level-1 starting outfit as (ItemDisplayInfo id, InventoryType)
    /// pairs - dress the create preview straight from these (DisplayItemID is a direct display id).</summary>
    public IReadOnlyList<(uint DisplayId, int InvType)> StartOutfit(byte race, byte cls, byte sex) =>
        _outfits.TryGetValue((race, cls, sex), out var l)
            ? l
            : (IReadOnlyList<(uint DisplayId, int InvType)>)Array.Empty<(uint, int)>();

    public string HairCustomization(byte race) =>
        _customizationTokens.TryGetValue(race, out var value) ? value.Hair : "NORMAL";

    public string FacialHairCustomization(byte race, byte sex) =>
        _customizationTokens.TryGetValue(race, out var value)
            ? value.Facial[Math.Min((int)sex, 1)] : "NORMAL";

    /// <summary>Load the catalog from the client MPQs. Null if CharBaseInfo is missing.</summary>
    public static CharCreateCatalog? Load(string clientDataPath)
    {
        byte[]? baseInfo = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, CharBaseInfoPath);
        if (baseInfo is null)
        {
            Console.WriteLine("[charcreate] CharBaseInfo.dbc not found - class catalog unavailable");
            return null;
        }

        var cat = new CharCreateCatalog();
        cat.ParseBaseInfo(baseInfo);

        byte[]? races = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, ChrRacesPath);
        cat.ParseCustomizationTokens(races);

        byte[]? sections = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, CharSectionsPath);
        byte[]? hair = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, CharHairGeosetsPath);
        byte[]? facial = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, FacialHairPath);
        cat.ParseDials(sections, hair, facial);

        byte[]? outfits = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, CharStartOutfitPath);
        cat.ParseOutfits(outfits);

        cat.Ok = cat._classes.Count > 0;
        Console.WriteLine($"[charcreate] catalog: {cat._classes.Count} race(s), dial ranges for {cat._dials.Count} (race,sex) pair(s)");
        return cat;
    }

    private readonly Dictionary<(byte Race, byte Sex), uint> _nativeDisplay = new();

    /// <summary>The race/sex body model (ChrRaces MaleDisplayId/FemaleDisplayId): what UNIT_DISPLAYID holds for an
    /// undisguised player. 0 when unknown.</summary>
    public uint NativeDisplay(byte race, byte sex) => _nativeDisplay.GetValueOrDefault((race, sex));

    // ChrRaces build 5875: 29 u32 fields / 116-byte records. Fields 4/5 are the male/female display
    // ids; fields 26/27 are the male/female facial-hair GlueStrings tokens and field 28 is the shared
    // hair token.
    private void ParseCustomizationTokens(byte[]? d)
    {
        if (!ValidWdbc(d) || Fields(d!) < 29 || I32(d!, 12) < 116) return;
        ForEachRecord(d!, rec =>
        {
            byte race = (byte)U32(d!, rec, 0);
            if (race is >= 1 and <= 8)
            {
                _nativeDisplay[(race, 0)] = U32(d!, rec, 4);
                _nativeDisplay[(race, 1)] = U32(d!, rec, 5);
            }
            string male = WdbcString(d!, U32(d!, rec, 26));
            string female = WdbcString(d!, U32(d!, rec, 27));
            string hair = WdbcString(d!, U32(d!, rec, 28));
            if (race is >= 1 and <= 8 && male.Length > 0 && female.Length > 0 && hair.Length > 0)
                _customizationTokens[race] = ([male, female], hair);
        });
    }

    // CharBaseInfo.dbc: WDBC header then recordCount * 2 bytes = (raceId, classId). Rows the
    // live game could never create (the stray Dwarf Mage) are dropped - see Creatable112.
    private void ParseBaseInfo(byte[] d)
    {
        if (!ValidWdbc(d)) return;
        int recordCount = I32(d, 4);
        int recordSize = I32(d, 12);
        int dataOff = 20;
        for (int r = 0; r < recordCount; r++)
        {
            int p = dataOff + r * recordSize;
            if (p + 1 >= d.Length) break;
            byte race = d[p], cls = d[p + 1];
            if (!IsCreatable112(race, cls))
            {
                Console.WriteLine($"[charcreate] CharBaseInfo row race={race} class={cls} is not creatable in 1.12 - dropped");
                continue;
            }
            if (!_classes.TryGetValue(race, out var list)) { list = new List<byte>(); _classes[race] = list; }
            if (!list.Contains(cls)) list.Add(cls);
        }
    }

    // Distinct-value counts, keyed by (race, sex), for each of the five dials.
    private void ParseDials(byte[]? sections, byte[]? hair, byte[]? facial)
    {
        var skin = new Dictionary<(byte, byte), HashSet<uint>>();
        var face = new Dictionary<(byte, byte), HashSet<uint>>();
        var hairColor = new Dictionary<(byte, byte), HashSet<uint>>();
        var hairStyle = new Dictionary<(byte, byte), HashSet<uint>>();
        var facialHair = new Dictionary<(byte, byte), HashSet<uint>>();

        // CharSections: [1]Race [2]Sex [3]BaseSection [4]Variation [5]Color (4-byte fields).
        if (ValidWdbc(sections) && Fields(sections!) >= 6)
        {
            ForEachRecord(sections!, (rec) =>
            {
                byte race = (byte)U32(sections!, rec, 1);
                byte sex = (byte)U32(sections!, rec, 2);
                uint section = U32(sections!, rec, 3);
                uint variation = U32(sections!, rec, 4);
                uint color = U32(sections!, rec, 5);
                var key = (race, sex);
                if (section == 0) Add(skin, key, color);
                else if (section == 1) Add(face, key, variation);
                else if (section == 3) Add(hairColor, key, color);
            });
        }

        // CharHairGeosets: [1]Race [2]Sex [3]Variation.
        if (ValidWdbc(hair) && Fields(hair!) >= 4)
            ForEachRecord(hair!, (rec) =>
                Add(hairStyle, ((byte)U32(hair!, rec, 1), (byte)U32(hair!, rec, 2)), U32(hair!, rec, 3)));

        // CharacterFacialHairStyles: NO id column -> [0]Race [1]Sex [2]Variation.
        if (ValidWdbc(facial) && Fields(facial!) >= 3)
            ForEachRecord(facial!, (rec) =>
                Add(facialHair, ((byte)U32(facial!, rec, 0), (byte)U32(facial!, rec, 1)), U32(facial!, rec, 2)));

        for (byte race = 1; race <= 8; race++)
            for (byte sex = 0; sex <= 1; sex++)
            {
                var key = (race, sex);
                _dials[key] = new[]
                {
                    Count(skin, key), Count(face, key), Count(hairStyle, key),
                    Count(hairColor, key), Count(facialHair, key),
                };
            }
    }

    // CharStartOutfit.dbc (verified vs his patch.MPQ): 82 records, 152-byte stride = 38 u32 columns
    // (fieldCount 41 counts the packed word as 4 byte-fields). [0]Id [1]RaceClassGender packed
    // (race | class<<8 | sex<<16, byte3 outfitId) [2..13]ItemID [14..25]DisplayItemID [26..37]
    // InventoryType. DisplayItemID is a DIRECT ItemDisplayInfo id (no Item.dbc hop - Item.dbc isn't
    // even shipped), so it dresses the model exactly like the roster's equipment display ids. Empty
    // slot = DisplayItemID -1; InventoryType 0 = a non-equip consumable (food/hearthstone) -> skip.
    private void ParseOutfits(byte[]? d)
    {
        if (!ValidWdbc(d)) return;
        if (I32(d!, 12) < 152) { Console.WriteLine("[charcreate] CharStartOutfit stride unexpected - outfits skipped"); return; }
        ForEachRecord(d!, rec =>
        {
            uint packed = U32(d!, rec, 1);
            byte race = (byte)(packed & 0xFF);
            byte cls = (byte)((packed >> 8) & 0xFF);
            byte sex = (byte)((packed >> 16) & 0xFF);
            var items = new List<(uint, int)>();
            for (int i = 0; i < 12; i++)
            {
                int disp = (int)U32(d!, rec, 14 + i);
                int inv = (int)U32(d!, rec, 26 + i);
                if (disp <= 0 || inv <= 0) continue;   // -1/0 empty slot, or inv 0 = non-equip consumable
                items.Add(((uint)disp, inv));
            }
            if (items.Count > 0) _outfits[(race, cls, sex)] = items;
        });
    }

    // ── tiny WDBC helpers ──────────────────────────────────────────────────────────────────────
    private static bool ValidWdbc(byte[]? d) =>
        d is { Length: >= 20 } && d[0] == 'W' && d[1] == 'D' && d[2] == 'B' && d[3] == 'C';
    private static int I32(byte[] d, int off) =>
        d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24);
    private static int Fields(byte[] d) => I32(d, 8);
    private static void ForEachRecord(byte[] d, Action<int> body)
    {
        int recordCount = I32(d, 4), recordSize = I32(d, 12), dataOff = 20;
        for (int r = 0; r < recordCount; r++)
        {
            if (dataOff + r * recordSize + recordSize > d.Length) break;
            body(dataOff + r * recordSize);
        }
    }
    private static uint U32(byte[] d, int recOff, int field)
    {
        int p = recOff + field * 4;
        return (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
    }
    private static string WdbcString(byte[] d, uint offset)
    {
        int strings = 20 + I32(d, 4) * I32(d, 12);
        long startLong = strings + offset;
        if (startLong < strings || startLong >= d.Length) return "";
        int start = (int)startLong;
        int end = start;
        while (end < d.Length && d[end] != 0) end++;
        return end == start ? "" : System.Text.Encoding.UTF8.GetString(d, start, end - start);
    }
    private static void Add(Dictionary<(byte, byte), HashSet<uint>> d, (byte, byte) key, uint value)
    {
        if (!d.TryGetValue(key, out var set)) { set = new HashSet<uint>(); d[key] = set; }
        set.Add(value);
    }
    // At least 1 so the dial spinner always has a position (benilla's [1;5] degenerate floor).
    private static int Count(Dictionary<(byte, byte), HashSet<uint>> d, (byte, byte) key) =>
        d.TryGetValue(key, out var set) ? Math.Max(1, set.Count) : 1;
}
