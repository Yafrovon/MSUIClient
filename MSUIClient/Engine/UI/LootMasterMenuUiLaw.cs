using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The master-looter "Give loot to" panel (the window that opens on a LOOT_SLOT_MASTER row).
///
/// Owner 2026-09-22: the vanilla-style dropdown was "totally wrong" for a raid - forty bare
/// names in one transparent column, opened at the mouse on top of the loot window, with no way
/// to tell who plays what. This is a custom panel in the same art (riveted dialog border on an
/// opaque fill) that:
///   - docks to the loot frame's right edge (left edge when there is no room) instead of the
///     mouse, so it never covers the item being assigned;
///   - heads itself with the item (icon + quality-coloured name);
///   - groups candidates by class under a class-icon header, class-coloured names;
///   - lists who can actually use the item first (class restriction + armour/weapon
///     proficiency) and dims the rest, which stay assignable;
///   - flows the groups into columns, so a 40-man list fits without paging.
/// </summary>
public static class LootMasterMenuUiLaw
{
    public const float RowHeight = 16f;
    public const float ColumnWidth = 124f;
    public const float ColumnGap = 6f;
    /// <summary>Clear of the riveted Dialog edge (insets 11/12/12/11) on every side.</summary>
    public const float Border = 14f;
    public const float TitleHeight = 40f;
    public const float ItemIconSize = 30f;
    public const float ClassIconSize = 14f;
    public const float NameIndent = 18f;
    public const int MinimumRowsPerColumn = 14;
    public const int MaximumColumns = 4;
    /// <summary>The loot panel art's visible right edge (the minimize button ends at 191).</summary>
    public const float LootFrameVisibleRight = 196f;
    public const float DockGap = 2f;

    /// <summary>The vanilla class list, in the character-create / raid-frame order.</summary>
    public static readonly byte[] ClassOrder = [1, 2, 3, 4, 5, 7, 8, 9, 11];

    public readonly record struct Candidate(ulong Guid, string Name, byte ClassId, bool Usable);

    public enum CellKind { ClassHeader, Member }

    /// <summary>One drawn line: a class header or a member name, at (column, row).</summary>
    public readonly record struct Cell(CellKind Kind, int Column, int Row, byte ClassId,
        int GroupCount, Candidate Member);

    public readonly record struct Layout(Vector2 Origin, Vector2 Size, float Scale,
        IReadOnlyList<Cell> Cells, int Columns, int RowsPerColumn)
    {
        public Vector2 CellMin(in Cell cell) => Origin + new Vector2(
            Border + cell.Column * (ColumnWidth + ColumnGap),
            Border + TitleHeight + cell.Row * RowHeight) * Scale;
        public Vector2 CellSize => new Vector2(ColumnWidth, RowHeight) * Scale;
        public Vector2 ItemIconMin => Origin + new Vector2(Border, Border) * Scale;
        public Vector2 ItemIconMax => ItemIconMin + new Vector2(ItemIconSize, ItemIconSize) * Scale;
        public Vector2 TitleTextMin => Origin + new Vector2(Border + ItemIconSize + 7, Border) * Scale;
        public Vector2 SubtitleMin => Origin + new Vector2(Border + ItemIconSize + 7, Border + 16) * Scale;
        public float TitleWidth => (Size.X / Scale - Border * 2 - ItemIconSize - 7) * Scale;
        public Vector2 DividerMin => Origin + new Vector2(Border, Border + TitleHeight - 5) * Scale;
        public Vector2 DividerMax => Origin + new Vector2(Size.X / Scale - Border, Border + TitleHeight - 4) * Scale;
    }

    /// <summary>
    /// Whether <paramref name="classId"/> can use the item, from the template's class mask and
    /// the vanilla level-60 armour/weapon proficiencies. Unknown class (0) counts as usable so
    /// nobody is dimmed for missing data.
    /// </summary>
    public static bool CanUse(byte classId, int allowableClass, uint itemClass, uint subclass)
    {
        if (classId is 0 or > 11) return true;
        uint bit = 1u << (classId - 1);
        if (allowableClass > 0 && (allowableClass & 0x5FF) != 0x5FF && (allowableClass & bit) == 0)
            return false;
        string classes = itemClass switch
        {
            4 => subclass switch   // armour
            {
                2 => "1,2,3,4,7,11",        // leather
                3 => "1,2,3,7",             // mail
                4 => "1,2",                 // plate
                6 => "1,2,7",               // shield
                7 => "2",                   // libram
                8 => "11",                  // idol
                9 => "7",                   // totem
                _ => "",                    // misc (rings, necks, trinkets, held), cloth
            },
            2 => subclass switch   // weapons
            {
                0 or 1 => "1,2,3,7",                 // axes
                2 or 3 or 16 or 18 => "1,3,4",        // bow, gun, thrown, crossbow
                4 => "1,2,4,5,7,11",                  // one-hand mace
                5 => "1,2,7,11",                      // two-hand mace
                6 => "1,2,3",                         // polearm
                7 => "1,2,3,4,8,9",                   // one-hand sword
                8 => "1,2,3",                         // two-hand sword
                10 => "1,3,5,7,8,9,11",               // staff
                13 => "1,3,4,7,11",                   // fist weapon
                15 => "1,3,4,5,7,8,9,11",             // dagger
                19 => "5,8,9",                        // wand
                _ => "",                              // misc, fishing pole
            },
            _ => "",
        };
        if (classes.Length == 0) return true;
        foreach (string id in classes.Split(','))
            if (byte.Parse(id) == classId) return true;
        return false;
    }

    /// <summary>
    /// Group by class, usable members first within a group, groups that can use the item ahead
    /// of groups that cannot, then vanilla class order; names alphabetical. Unknown class last.
    /// </summary>
    public static IReadOnlyList<(byte ClassId, IReadOnlyList<Candidate> Members)> Group(
        IEnumerable<Candidate> candidates)
    {
        return candidates
            .GroupBy(c => c.ClassId)
            .Select(g => (ClassId: g.Key, Members: (IReadOnlyList<Candidate>)g
                .OrderByDescending(c => c.Usable)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Members.Any(c => c.Usable))
            .ThenBy(g => Array.IndexOf(ClassOrder, g.ClassId) is int i && i >= 0 ? i : 99)
            .ToList();
    }

    /// <summary>
    /// Flow the class groups into columns (a group is only split when it alone is taller than a
    /// column) and dock the panel beside the loot frame, clamped inside the display.
    /// </summary>
    public static Layout Resolve(IEnumerable<Candidate> candidates, Vector2 lootFrameOrigin,
        float rowTop, Vector2 display, float scale)
    {
        var groups = Group(candidates);
        int lines = groups.Sum(g => g.Members.Count + 1);
        int rowsPerColumn = Math.Max(MinimumRowsPerColumn,
            (lines + MaximumColumns - 1) / MaximumColumns);
        // Whole groups waste column tails, so grow the column until the flow fits the cap.
        List<Cell> cells = Flow(groups, rowsPerColumn);
        while (cells.Count != 0 && cells.Max(c => c.Column) + 1 > MaximumColumns && rowsPerColumn < lines)
            cells = Flow(groups, ++rowsPerColumn);
        int columns = cells.Count == 0 ? 1 : cells.Max(c => c.Column) + 1;
        int usedRows = cells.Count == 0 ? 1 : cells.Max(c => c.Row) + 1;

        float width = Border * 2 + columns * ColumnWidth + (columns - 1) * ColumnGap;
        width = MathF.Max(width, Border * 2 + ColumnWidth + 60);
        float height = Border * 2 + TitleHeight + usedRows * RowHeight;
        Vector2 size = new Vector2(width, height) * scale;

        float right = lootFrameOrigin.X + (LootFrameVisibleRight + DockGap) * scale;
        float x = right + size.X <= display.X ? right : lootFrameOrigin.X - size.X - DockGap * scale;
        float y = rowTop - (Border + TitleHeight * 0.5f) * scale;
        var origin = new Vector2(
            Math.Clamp(x, 0, MathF.Max(0, display.X - size.X)),
            Math.Clamp(y, 0, MathF.Max(0, display.Y - size.Y)));
        return new(origin, size, scale, cells, columns, rowsPerColumn);
    }

    private static List<Cell> Flow(
        IReadOnlyList<(byte ClassId, IReadOnlyList<Candidate> Members)> groups, int rowsPerColumn)
    {
        var cells = new List<Cell>();
        int column = 0, row = 0;
        foreach (var (classId, members) in groups)
        {
            // Keep a group whole when it fits in a fresh column but not in the rest of this one.
            if (row > 0 && row + members.Count + 1 > rowsPerColumn && members.Count + 1 <= rowsPerColumn)
            { column++; row = 0; }
            cells.Add(new(CellKind.ClassHeader, column, row++, classId, members.Count, default));
            foreach (Candidate member in members)
            {
                if (row >= rowsPerColumn) { column++; row = 0; }
                cells.Add(new(CellKind.Member, column, row++, classId, members.Count, member));
            }
        }
        return cells;
    }
}
