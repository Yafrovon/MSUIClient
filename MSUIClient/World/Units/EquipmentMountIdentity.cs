namespace MSUIClient.World.Units;

/// <summary>Observation-only item identity copied from the piece that created an attachment.</summary>
public readonly record struct EquipmentMountIdentity(uint DisplayId, int EquipmentSlot, int InventoryType)
{
    public static EquipmentMountIdentity FromPiece(CharacterEquipment.Piece piece) =>
        new(piece.DisplayId, piece.EquipmentSlot, piece.InventoryType);
}
