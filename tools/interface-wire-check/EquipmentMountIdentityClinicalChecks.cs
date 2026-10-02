using MSUIClient;
using MSUIClient.World.Units;

internal static class EquipmentMountIdentityClinicalChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            checks++;
        }
        var piece = new CharacterEquipment.Piece { DisplayId = 76350, EquipmentSlot = 2, InventoryType = 3 };
        var left = EquipmentMountIdentity.FromPiece(piece);
        var right = EquipmentMountIdentity.FromPiece(piece);
        Check(left == right && left.DisplayId == 76350 && left.EquipmentSlot == 2 && left.InventoryType == 3,
            "Both shoulder identities must come from their parent equipment piece");
        piece.DisplayId = 17;
        piece.EquipmentSlot = 15;
        Check(left.DisplayId == 76350 && left.EquipmentSlot == 2, "Diagnostic identity must be a value snapshot");
        var weapon = new EquipmentMountIdentity(76333, 15, 13);
        Check(weapon.DisplayId == 76333 && weapon.EquipmentSlot == 15 && weapon.InventoryType == 13,
            "Existing non-shoulder mount identity was lost");
        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units", "AttachedItemRenderer.cs"));
        int begin = source.IndexOf("if (piece.InventoryType == CharacterEquipment.Slot.Shoulders)", StringComparison.Ordinal);
        int end = source.IndexOf("int heldSlot = piece.EquipmentSlot switch", begin, StringComparison.Ordinal);
        Check(begin >= 0 && end > begin, "Shoulder mount branch unavailable");
        string shoulder = source[begin..end];
        Check(shoulder.Contains("AttachShoulderLeft, piece.Name + \" (L)\",", StringComparison.Ordinal) &&
            shoulder.Contains("AttachShoulderRight, piece.Name + \" (R)\",", StringComparison.Ordinal) &&
            shoulder.Split("inspectionIdentity: EquipmentMountIdentity.FromPiece(piece)").Length == 3,
            "Both original shoulder paths must propagate source identity without guessing a filename");
        Check(!shoulder.Contains("piece.Sheath", StringComparison.Ordinal) &&
            !shoulder.Contains("piece.Enchants", StringComparison.Ordinal) &&
            !shoulder.Contains("piece.Row.ItemVisualId", StringComparison.Ordinal), "Diagnostic fix changed shoulder rendering fields");
        Check(source.Contains("InspectionIdentity = inspectionIdentity ?? new EquipmentMountIdentity(displayId, equipmentSlot, inventoryType)", StringComparison.Ordinal) &&
            source.Contains("DisplayId = displayId, EquipmentSlot = equipmentSlot, ItemVisualId = itemVisualId", StringComparison.Ordinal),
            "Mount construction no longer keeps diagnostic and existing rendering fields independent");
        string observer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units", "CreatureRenderer.EquipmentInspection.cs"));
        Check(observer.Contains("slot = mount.InspectionIdentity.EquipmentSlot", StringComparison.Ordinal) &&
            observer.Contains("mount.InspectionIdentity.DisplayId, mount.InspectionIdentity.InventoryType", StringComparison.Ordinal),
            "Remote inspection no longer reports captured source item identity");
        Console.WriteLine($"interface-wire-check: EquipmentMountIdentity PASS ({checks} checks)");
    }
}
