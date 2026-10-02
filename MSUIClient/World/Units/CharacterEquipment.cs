using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// What a character is wearing, and what that does to the model.
///
/// VANILLA GEAR IS THREE DIFFERENT MECHANISMS, not one
///
///   1. BODY ATLAS. Chest, legs, boots, gloves, bracers, belt and tabard have
///      no geometry of their own. They PAINT into the single 256x256 skin
///      texture the whole character shares, at fixed rectangles. Eight texture
///      slots per item, one per body region. This class does that composite.
///
///   2. GEOSET VARIANTS. The same items also switch which body geosets are
///      drawn - sleeves instead of bare arms, a boot instead of a bare foot.
///      ItemDisplayInfo's m_geosetGroup picks the variant.
///
///   3. ATTACHED MODELS. Helms, shoulders, weapons and shields are
///      SEPARATE M2 FILES mounted on the skeleton's attachment points. Nothing
///      here handles those - they need an item-model render path, and M2Reader
///      already parses the Attachments array that will drive it.
///
///   Capes are a fourth case: the cloth is geoset 1502 in the character M2,
///   and its BLP is supplied through replaceable texture type 2.
///
/// So a full Tier 1 set is not one feature. Body armour appears from (1) and
/// (2); the helm and pauldrons need (3).
/// </summary>
public sealed class CharacterEquipment
{
    public readonly record struct GuildEmblemDesign(uint Style, uint Color, uint BorderStyle,
        uint BorderColor, uint BackgroundColor);
    public GuildEmblemDesign? GuildEmblem { get; set; }

    /// <summary>The wearer's body appearance bytes (skin/face/hair/facial). Carried on the kit so a
    /// remote-player appearance worker can build the base atlas without a separate descriptor.</summary>
    public readonly record struct PlayerAppearance(
        byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialHair);
    public PlayerAppearance? PlayerLook { get; set; }

    /// <summary>Free-form content signature used by PlayerRenderer to cache identical looks. Not used by dressing.</summary>
    public string Signature { get; set; } = "";

    /// <summary>Vanilla InventoryType values, the ones that matter for dressing.</summary>
    public static class Slot
    {
        public const int Head = 1;
        public const int Shoulders = 3;
        public const int Shirt = 4;
        public const int Chest = 5;
        public const int Waist = 6;
        public const int Legs = 7;
        public const int Feet = 8;
        public const int Wrists = 9;
        public const int Hands = 10;
        public const int Weapon = 13;
        public const int Shield = 14;
        public const int Ranged = 15;
        public const int Cloak = 16;
        public const int TwoHand = 17;
        public const int Tabard = 19;
        public const int Robe = 20;
        public const int MainHand = 21;
        public const int OffHand = 22;
    }

    public sealed class Piece
    {
        public string Name = "";
        public uint DisplayId;
        public int InventoryType;
        public int EquipmentSlot = -1;
        public byte ItemClass;
        public byte ItemSubclass;
        public byte Material;
        public byte Sheath;
        public uint[] Enchants = [];
        public ItemDisplayRow? Row;

        /// <summary>True once this piece has something the body model can show.</summary>
        public bool AffectsBody => Row is not null && (Row.HasBodyTexture || Row.GeosetGroup.Any(g => g != 0));

        /// <summary>True when the piece is a separate M2 - not handled yet.</summary>
        public bool NeedsAttachment => InventoryType != Slot.Cloak && Row is not null && Row.HasModel;
    }

    private readonly List<Piece> _pieces = [];
    public IReadOnlyList<Piece> Pieces => _pieces;
    internal List<(int Region, string Declared, string? Resolved)> InspectionBodyTextures { get; } = [];

    public void Clear() => _pieces.Clear();

    public void Add(string name, uint displayId, int inventoryType, int equipmentSlot = -1,
        byte itemClass = 0, byte itemSubclass = 0, byte material = 0, byte sheath = 0,
        IReadOnlyList<uint>? enchants = null)
        => _pieces.Add(new Piece
        {
            Name = name, DisplayId = displayId, InventoryType = inventoryType,
            EquipmentSlot = equipmentSlot, ItemClass = itemClass, ItemSubclass = itemSubclass,
            Material = material, Sheath = sheath,
            Enchants = enchants?.ToArray() ?? [],
        });

    /// <summary>
    /// Attach the DBC rows and say what each piece turned out to be. The log is
    /// the point: it separates "the item resolved and paints nothing" from "the
    /// display ID is not in this table", which look identical on screen.
    /// </summary>
    public void Resolve(ItemDisplayTable? table)
    {
        if (table is null)
        {
            Console.WriteLine("[equip] no ItemDisplayInfo table - nothing can be resolved");
            return;
        }

        foreach (var piece in _pieces)
        {
            piece.Row = table.Find(piece.DisplayId);

            if (piece.Row is null)
            {
                Console.WriteLine($"[equip] {piece.Name} (display {piece.DisplayId}): NOT FOUND in ItemDisplayInfo");
                continue;
            }

            string kind = piece.NeedsAttachment ? "attachment" : piece.AffectsBody ? "body" : "nothing";
            Console.WriteLine($"[equip] {piece.Name} (display {piece.DisplayId}, inv {piece.InventoryType}, " +
                              $"equip {piece.EquipmentSlot}, sheath {piece.Sheath}) -> {kind}: {piece.Row}");
        }

        int attachments = _pieces.Count(p => p.NeedsAttachment);
        if (attachments > 0)
            Console.WriteLine($"[equip] {attachments} piece(s) are separate M2 models (helm, shoulders, weapons, " +
                              "shield) and need the attachment path, which is not built yet");
    }

    // ── body atlas ───────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical body-atlas rectangles on the 256x256 skin, in slot order.
    /// From SuperUI's region-rects.js, marked CANONICAL there and shared by all
    /// sixteen race/gender combinations.
    ///
    /// Left column x=0: armUpper, armLower, hand, faceUpper, faceLower.
    /// Right column x=128: torsoUpper, torsoLower, legUpper, legLower, foot.
    /// Each column sums to exactly 256 - a useful check if these ever look off.
    /// </summary>
    private static readonly (int X, int Y, int W, int H, string Folder)[] SlotRegions =
    [
        (0,   0, 128, 64, "ArmUpperTexture"),     // 0 arm upper
        (0,  64, 128, 64, "ArmLowerTexture"),     // 1 arm lower
        (0, 128, 128, 32, "HandTexture"),         // 2 hand
        (128,  0, 128, 64, "TorsoUpperTexture"),  // 3 torso upper
        (128, 64, 128, 32, "TorsoLowerTexture"),  // 4 torso lower
        (128, 96, 128, 64, "LegUpperTexture"),    // 5 leg upper
        (128,160, 128, 64, "LegLowerTexture"),    // 6 leg lower
        (128,224, 128, 32, "FootTexture"),        // 7 foot
    ];

    /// <summary>The atlas layout is authored against 256x256; anything else scales.</summary>
    private const int AtlasSize = 256;

    /// <summary>Vanilla body-atlas paint order, copied from SuperUI's equip.js.</summary>
    private static int PaintOrder(int inventoryType) => inventoryType switch
    {
        Slot.Shirt => 1,
        Slot.Legs => 2,
        Slot.Chest or Slot.Robe => 3,
        Slot.Feet => 4,
        Slot.Wrists => 5,
        Slot.Waist => 6,
        Slot.Hands => 7,
        Slot.Tabard => 8,
        Slot.Cloak => 9,
        _ => 100,
    };

    /// <summary>
    /// Composite every equipped body texture onto a copy of the base skin.
    ///
    /// Order matters and follows SuperUI's tested vanilla paint priorities:
    /// broad garments first, then boots/bracers/belt/gloves, then tabard/cape.
    /// A plate belt's LegUpper strip is a buckle band drawn over the legplates'
    /// full thigh texture - swap the order and the belt disappears.
    ///
    /// Returns a new BGRA buffer; the base is not modified.
    /// </summary>
    public byte[] Composite(byte[] baseSkin, int width, int height, Func<string, (byte[] bgra, int w, int h)?> load, bool bareFeet)
    {
        var canvas = (byte[])baseSkin.Clone();

        float sx = width / (float)AtlasSize;
        float sy = height / (float)AtlasSize;

        int painted = 0, missing = 0;

        foreach (var piece in _pieces
                     .Select((piece, index) => (piece, index))
                     .OrderBy(x => PaintOrder(x.piece.InventoryType))
                     .ThenBy(x => x.index)
                     .Select(x => x.piece))
        {
            if (piece.Row is null) continue;

            for (int slot = 0; slot < SlotRegions.Length; slot++)
            {
                // ChrRaces bare-foot bodies retain the skin/hoof atlas region. Boot cuffs
                // still paint LegLower (slot 6); only FootTexture (slot 7) is suppressed.
                if (bareFeet && slot == 7) continue;
                string partial = piece.Row.BodyTextures[slot];
                if (partial.Length == 0) continue;

                var image = ResolveSlotTexture(partial, slot, load);
                if (image is null)
                {
                    missing++;
                    continue;
                }

                var region = SlotRegions[slot];
                Blit(canvas, width, height,
                     image.Value.bgra, image.Value.w, image.Value.h,
                     (int)(region.X * sx), (int)(region.Y * sy),
                     (int)(region.W * sx), (int)(region.H * sy));
                painted++;
            }
        }

        if (GuildEmblem is { } emblem && _pieces.Any(p => p.InventoryType == Slot.Tabard))
            PaintGuildEmblem(canvas, width, height, emblem, load);

        Console.WriteLine($"[equip] atlas composite: {painted} region(s) painted, {missing} texture(s) not found");
        return canvas;
    }

    private static void PaintGuildEmblem(byte[] canvas, int width, int height,
        GuildEmblemDesign design, Func<string, (byte[] bgra, int w, int h)?> load)
    {
        foreach ((string suffix, int slot) in new[] { ("TU", 3), ("TL", 4) })
        {
            string[] paths =
            [
                $@"Textures\GuildEmblems\Background_{design.BackgroundColor:D2}_{suffix}_U.blp",
                $@"Textures\GuildEmblems\Border_{design.BorderStyle:D2}_{design.BorderColor:D2}_{suffix}_U.blp",
                $@"Textures\GuildEmblems\Emblem_{design.Style:D2}_{design.Color:D2}_{suffix}_U.blp",
            ];
            var region = SlotRegions[slot];
            foreach (string path in paths)
            {
                var image = load(path); if (image is null) continue;
                float sx = width / (float)AtlasSize, sy = height / (float)AtlasSize;
                Blit(canvas, width, height, image.Value.bgra, image.Value.w, image.Value.h,
                    (int)(region.X * sx), (int)(region.Y * sy),
                    (int)(region.W * sx), (int)(region.H * sy));
            }
        }
    }

    /// <summary>
    /// Turn an ItemDisplayInfo texture partial name into pixels.
    ///
    /// The partial is a bare name like "Plate_A_01Chest". The real file lives
    /// under Item\TextureComponents\&lt;Folder&gt;\ with a gender suffix, and the
    /// exact convention is the one thing here I could not confirm from the
    /// sources at hand - so instead of picking one and hoping, this tries the
    /// plausible spellings and LOGS the one that hits. The first run tells us
    /// the rule, exactly as the vmtile-to-vmo naming was settled.
    /// </summary>
    public string GenderSuffix { get; set; } = "M";

    private readonly Dictionary<string, (byte[] bgra, int w, int h)?> _textureCache = [];
    private readonly HashSet<string> _reportedConvention = [];

    private (byte[] bgra, int w, int h)? ResolveSlotTexture(
        string partial, int slot, Func<string, (byte[] bgra, int w, int h)?> load)
    {
        string key = $"{slot}|{partial}";
        if (_textureCache.TryGetValue(key, out var cached)) return cached;

        string folder = SlotRegions[slot].Folder;
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        var candidates = new[]
        {
            $@"Item\TextureComponents\{folder}\{stem}_{GenderSuffix}.blp",
            $@"Item\TextureComponents\{folder}\{stem}_U.blp",
            $@"Item\TextureComponents\{folder}\{stem}.blp",
            $"{stem}.blp",
        };

        foreach (var candidate in candidates)
        {
            var image = load(candidate);
            if (image is null) continue;

            // Say the winning shape once per folder, not once per texture.
            if (_reportedConvention.Add(folder))
                Console.WriteLine($"[equip] {folder} resolves as: {candidate}");

            InspectionBodyTextures.Add((slot, partial, candidate));
            _textureCache[key] = image;
            return image;
        }

        Console.WriteLine($"[equip] slot {slot} texture '{partial}' not found - tried: {string.Join("  ", candidates)}");
        InspectionBodyTextures.Add((slot, partial, null));
        _textureCache[key] = null;
        return null;
    }

    /// <summary>
    /// Nearest-neighbour blit with stretch. Vanilla item textures are authored
    /// at the region size, so this is normally a straight copy; the sampling is
    /// only there so an odd-sized texture lands in the right rectangle instead
    /// of tearing across the atlas.
    ///
    /// Alpha is honoured - the paint is composited over the skin rather than
    /// replacing it, which is what makes bracers and belts read as sitting on
    /// the arm rather than cutting a hole in it.
    /// </summary>
    /// <summary>Public so the appearance composite can reuse the same alpha-aware blit.</summary>
    public static void BlitOver(byte[] dst, int dstW, int dstH,
                                byte[] src, int srcW, int srcH,
                                int x, int y, int w, int h)
        => Blit(dst, dstW, dstH, src, srcW, srcH, x, y, w, h);

    private static void Blit(byte[] dst, int dstW, int dstH,
                             byte[] src, int srcW, int srcH,
                             int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || srcW <= 0 || srcH <= 0) return;

        for (int row = 0; row < h; row++)
        {
            int dy = y + row;
            if (dy < 0 || dy >= dstH) continue;

            int sy = row * srcH / h;

            for (int col = 0; col < w; col++)
            {
                int dx = x + col;
                if (dx < 0 || dx >= dstW) continue;

                int sx = col * srcW / w;

                int si = (sy * srcW + sx) * 4;
                int di = (dy * dstW + dx) * 4;
                if (si + 3 >= src.Length || di + 3 >= dst.Length) continue;

                byte a = src[si + 3];
                if (a == 0) continue;

                if (a == 255)
                {
                    dst[di + 0] = src[si + 0];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = 255;
                    continue;
                }

                int inv = 255 - a;
                dst[di + 0] = (byte)((src[si + 0] * a + dst[di + 0] * inv) / 255);
                dst[di + 1] = (byte)((src[si + 1] * a + dst[di + 1] * inv) / 255);
                dst[di + 2] = (byte)((src[si + 2] * a + dst[di + 2] * inv) / 255);
                dst[di + 3] = 255;
            }
        }
    }

    // ── geosets ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Which geoset category each slot controls, as (category, geosetGroup
    /// index, offset). Ported from SuperUI's geoset-rules.js SLOT_RULES.
    ///
    /// The offset is universally 1 for armour, and it is NOT "skip the bare
    /// variant". The decompiled vanilla client computes the mesh as
    /// BASE + geosetGroup[N], where BASE is the first mesh ID in the category -
    /// 401 for gloves, 501 for boots. Expressed as variant numbers that is
    /// dbcValue + 1, which is where the offset comes from.
    ///
    /// This is the tested SuperUI rule table. Pants drive categories 9 and 11;
    /// category 13 is reserved for the mutually-exclusive robe/thigh geometry.
    /// </summary>
    private static readonly Dictionary<int, (int Category, int GroupIndex, int Offset)[]> SlotGeosets = new()
    {
        [Slot.Shoulders] = [(15, 0, 0)],
        [Slot.Shirt] = [(8, 0, 1), (10, 1, 0)],
        [Slot.Chest] = [(8, 0, 1), (10, 1, 0)],
        [Slot.Legs] = [(9, 0, 1), (11, 1, 1)],
        [Slot.Feet] = [(5, 0, 1)],
        [Slot.Hands] = [(4, 0, 1)],
        [Slot.Tabard] = [(12, 0, 1)],
        [Slot.Robe] = [(8, 0, 1), (10, 1, 0), (13, 2, 1)],
    };

    private static readonly Dictionary<int, int[]> SlotForceHide = new()
    {
        [Slot.Robe] = [9, 11],
    };

    /// <summary>
    /// Apply the equipped pieces to a category-to-variant map that already
    /// holds the naked defaults.
    ///
    /// A geosetGroup value of ZERO means "leave the default alone", not "hide".
    /// That distinction is what makes a robe work: its group index 1 is 0,
    /// meaning it does not put a shirt-tail under itself, and the default for
    /// that category is hidden anyway.
    /// </summary>
    public void ApplyGeosets(Dictionary<int, int> selectedVariant)
    {
        foreach (var piece in _pieces)
        {
            if (piece.Row is null) continue;

            // Cloak display rows name a BLP in the model-texture columns; the
            // geometry comes from the row's GeosetGroup[0], exactly as
            // CharacterGeosets.cs applies it for streamed players (1501 + group).
            // Hard-coding variant 2 promoted every short cape to the long 1502
            // cloth the moment the first-person body dressed it, so a possessed
            // bot's cape changed length depending on which renderer drew it.
            if (piece.InventoryType == Slot.Cloak)
            {
                if (piece.Row.ModelTexture1.Length > 0 || piece.Row.ModelTexture2.Length > 0)
                    selectedVariant[15] = 1 + (piece.Row.GeosetGroup.Length > 0
                        ? Math.Max(piece.Row.GeosetGroup[0], 0) : 0);
                continue;
            }

            if (!SlotGeosets.TryGetValue(piece.InventoryType, out var rules)) continue;

            foreach (var (category, groupIndex, offset) in rules)
            {
                if (groupIndex < 0 || groupIndex >= piece.Row.GeosetGroup.Length) continue;

                int value = piece.Row.GeosetGroup[groupIndex];
                if (value > 0) selectedVariant[category] = value + offset;
            }

            if (SlotForceHide.TryGetValue(piece.InventoryType, out var hiddenCategories))
                foreach (int category in hiddenCategories)
                    selectedVariant[category] = 0;
        }
    }

    /// <summary>
    /// True when a closed helm should suppress hair. Equal visibility fields
    /// identify open helms such as Helm of Might, which retain styled hair.
    /// </summary>
    public bool HidesHair()
    {
        foreach (var piece in _pieces)
        {
            if (piece.InventoryType != Slot.Head || piece.Row is null) continue;
            return piece.Row.HelmetGeosetVis1 != piece.Row.HelmetGeosetVis2;
        }
        return false;
    }

    /// <summary>
    /// Nico's Tier 1 warrior set, as given. Display IDs are ItemDisplayInfo row
    /// IDs; the item IDs are here only so a console line can be read against
    /// what he asked for.
    /// </summary>
    public static CharacterEquipment BattlegearOfMight()
    {
        var kit = new CharacterEquipment();

        // Body-atlas and geoset pieces - these should appear.
        kit.Add("Breastplate of Might", 31021, Slot.Chest);
        kit.Add("Legplates of Might", 31023, Slot.Legs);
        kit.Add("Sabatons of Might", 31025, Slot.Feet);
        kit.Add("Gauntlets of Might", 31022, Slot.Hands);
        kit.Add("Bracers of Might", 31020, Slot.Wrists);
        kit.Add("Belt of Might", 31019, Slot.Waist);
        kit.Add("Tabard of the Scarlet Crusade", 15817, Slot.Tabard);
        kit.Add("Cape texture test", 13963, Slot.Cloak);

        // Separate M2 models - these need the attachment path.
        kit.Add("Helm of Might", 31260, Slot.Head);
        kit.Add("Pauldrons of Might", 31024, Slot.Shoulders);
        kit.Add("Quel'Serrar", 30994, Slot.MainHand);
        kit.Add("Aegis of the Scarlet Commander", 18751, Slot.Shield);

        return kit;
    }
}
