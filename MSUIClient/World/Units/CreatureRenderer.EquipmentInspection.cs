using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.World.Units;

public sealed partial class CreatureRenderer
{
    // Opt-in observation only. No model resolution, loading, queries, or pose changes.
    internal ulong EquipmentInspectionGuid { get; set; }
    internal object? EquipmentInspection { get; private set; }
    private ulong _equipmentInspectionRenderedGuid;
    private Vector4 _equipmentInspectionInteriorLight;
    private Vector3 _equipmentInspectionFeet;
    internal (Vector4 Light, Vector3 Feet)? InspectRenderedInteriorLight(ulong guid) =>
        InspectRenderedEquipment(guid) is null ? null :
            (_equipmentInspectionInteriorLight, _equipmentInspectionFeet);
    internal object? InspectRenderedEquipment(ulong guid) =>
        _equipmentInspectionRenderedGuid == guid ? EquipmentInspection : null;

    internal string[] InspectRenderedEquipmentErrors(ulong guid) =>
        _equipmentInspectionRenderedGuid == guid && _unitAttachments.TryGetValue(guid, out var attached)
            ? attached.Mounts.ResolutionErrors.ToArray() : [];

    private void RecordEquipmentInspection(WorldEntity entity, in CreatureModelInfo info,
        Appearance appearance)
    {
        if (entity.Guid != EquipmentInspectionGuid) return;
        _equipmentInspectionRenderedGuid = entity.Guid;
        _equipmentInspectionInteriorLight = _currentInteriorLight;
        _equipmentInspectionFeet = entity.Position;
        int[] slots = [0, 2, 3, 4, 5, 6, 7, 8, 9, 18, 14];
        bool attachmentFrame = _unitAttachments.TryGetValue(entity.Guid, out var attached) &&
            attached.LastSeenAt == _globalTime;
        EquipmentInspection = new
        {
            guid = $"0x{entity.Guid:X16}", observedInWorldRender = true,
            renderClock = _globalTime, model = info.ModelPath,
            interiorLight = _currentInteriorLight, feet = entity.Position,
            bodyKit = (info.ExtEquipment ?? []).Select((displayId, index) => new
            {
                appearanceSlot = index, slot = index < slots.Length ? slots[index] : -1, displayId,
            }).ToArray(),
            visibleGeosets = appearance.VisibleGeosets?.Order().ToArray(),
            attachmentFrameObserved = attachmentFrame,
            attachmentResolutionErrors = attachmentFrame ? attached!.Mounts.ResolutionErrors : [],
            mounts = attachmentFrame ? attached!.Mounts.Items.Select(mount => (object)new
            {
                slot = mount.InspectionIdentity.EquipmentSlot,
                mount.InspectionIdentity.DisplayId, mount.InspectionIdentity.InventoryType,
                model = mount.Model.Path, shoulderFit = mount.Model.ShoulderFit, mount.AttachmentId, mount.ItemSheath, mount.Visible,
            }).ToArray() : [],
            // A configured mount does not prove its triangles survived occlusion or sheath policy.
            limitation = "Body kit was used by the world draw; mounts are its current resolved mount set. Pixel visibility still requires the paired screenshot.",
        };
    }
}
