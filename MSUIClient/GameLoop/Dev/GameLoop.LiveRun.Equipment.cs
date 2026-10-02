using System.Numerics;
using System.Text.Json;
using MSUIClient.Creator;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private int _liveEquipmentInspectionOrdinal;

    // equipment watch|inspect self|selection [safe-label]
    // Watch only arms read-only renderer observation; inspect also writes the current evidence.
    private bool InspectLiveEquipment(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length >= 2 && args[1] == "require-items")
        {
            string subjectName = args.Length > 2 ? args[2] : "missing";
            bool validCommand = args.Length == 4 && subjectName is "self" or "selection";
            ulong subject = validCommand ? subjectName == "self" ? ControlledGuid : _selectionGuid : 0;
            uint[]? visible = null;
            if (subject != 0 && _entities.TryGet(subject, out WorldEntity itemUnit) && itemUnit.IsPlayer)
                visible = Enumerable.Range(0, 19).Select(itemUnit.Fields.PlayerVisibleItemEntry).ToArray();
            string expected = args.Length == 4 ? args[3] : "invalid-command";
            string actual = "invalid-command";
            bool matches = validCommand && EquipmentReadinessLaw.Matches(expected, visible, out actual);
            Console.WriteLine($"[live-equipment] require-items guid=0x{subject:X16};expected={expected};actual={actual};ok={matches}");
            if (!matches && _liveRunOptions is not null)
                FinishLiveBootstrap("EQUIPMENT_READINESS_MISMATCH", $"check=require-items;subject={subjectName};guid={subject};expected={expected};actual={actual}");
            return matches;
        }
        if (args.Length == 4 && args[1] == "require-guid" && args[2] is "self" or "selection")
        {
            ulong actual = args[2] == "self" ? ControlledGuid : _selectionGuid;
            bool matches = ulong.TryParse(args[3], out ulong expected) && expected != 0 && actual == expected;
            if (!matches && _liveRunOptions is not null)
                FinishLiveBootstrap("EQUIPMENT_ACTOR_MISMATCH", $"subject={args[2]};expected={args[3]};actual={actual}");
            return matches;
        }
        if (args.Length == 4 && args[2] is "self" or "selection" &&
            args[1] is "require-level" or "require-spells")
        {
            ulong subject = args[2] == "self" ? ControlledGuid : _selectionGuid;
            bool available = subject != 0 && _entities.TryGet(subject, out WorldEntity observed) && observed.IsPlayer;
            bool matches = false;
            string actual = "unavailable";
            if (available && args[1] == "require-level" && _entities.TryGet(subject, out WorldEntity levelUnit))
            {
                actual = levelUnit.Level.ToString();
                matches = uint.TryParse(args[3], out uint required) && required > 0 && levelUnit.Level == required;
            }
            else if (available && args[1] == "require-spells" && _actionsByGuid.TryGetValue(subject, out PlayerActions? actions))
            {
                string[] requested = args[3].Split(',');
                uint[] required = requested.Select(value => uint.TryParse(value, out uint id) ? id : 0u).ToArray();
                uint[] missing = required.Where(id => id == 0 || !actions.KnownSpells.Contains(id)).ToArray();
                actual = "missing=" + string.Join(',', missing);
                matches = required.Length > 0 && missing.Length == 0;
            }
            Console.WriteLine($"[live-equipment] {args[1]} guid=0x{subject:X16};expected={args[3]};actual={actual};ok={matches}");
            if (!matches && _liveRunOptions is not null)
                FinishLiveBootstrap("EQUIPMENT_READINESS_MISMATCH", $"check={args[1]};subject={args[2]};guid={subject};expected={args[3]};actual={actual}");
            return matches;
        }
        if (args.Length is < 3 or > 4 || args[1] is not ("watch" or "inspect") ||
            args[2] is not ("self" or "selection")) return false;
        ulong guid = args[2] == "self" ? ControlledGuid : _selectionGuid;
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity unit) || !unit.IsPlayer) return false;
        if (_creatures is not null) _creatures.EquipmentInspectionGuid = guid;
        if (args[1] == "watch") return true;
        string label = args.Length == 4 ? args[3] : args[2];
        if (label.Length is < 1 or > 80 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;

        object[] visibleItems = Enumerable.Range(0, 19).Select(slot =>
        {
            uint entry = unit.Fields.PlayerVisibleItemEntry(slot);
            ItemTemplate? template = null;
            bool templateAvailable = entry != 0 && _items?.TryGet(entry, out template) == true && template is not null;
            return (object)new
            {
                slot, entry, templateAvailable,
                displayId = templateAvailable ? template!.DisplayInfoId : 0,
                inventoryType = templateAvailable ? template!.InventoryType : 0,
                enchants = Enumerable.Range(0, 7).Select(index => unit.Fields.PlayerVisibleItemEnchant(slot, index)).ToArray(),
            };
        }).ToArray();
        bool local = guid == ControlledGuid && !ControlledBodyIsStreamed && _character is not null;
        string[] attachmentResolutionErrors = local ? _character!.Attached?.InspectionErrors.ToArray() ?? [] : _creatures?.InspectRenderedEquipmentErrors(guid) ?? [];
        object? rendered = local ? new
        {
            source = "controlled-character-renderer",
            attachmentResolutionErrors,
            mounts = _character!.Attached?.InspectionMounts.Select(mount => new {
                model = mount.Model.Path, shoulderFit = mount.Model.ShoulderFit, mount.InspectionIdentity.DisplayId,
                slot = mount.InspectionIdentity.EquipmentSlot, mount.InspectionIdentity.InventoryType, mount.AttachmentId, mount.Visible }).ToArray(),
            model = _character!.ModelPath,
            pieces = _character.Equipment.Pieces.Select(piece => new
            {
                slot = piece.EquipmentSlot, piece.DisplayId, piece.InventoryType, piece.Name,
            }).ToArray(),
            geosets = _character.ActiveGeosets.Select(value => new { value.Category, value.Variant }).ToArray(),
        } : _creatures?.InspectRenderedEquipment(guid);
        // Read the last world-render value; do not call InteriorUnitLight.For here because
        // it advances the production cache/blend. A fresh floor resolve is separate evidence.
        var remoteLight = local ? null : _creatures?.InspectRenderedInteriorLight(guid);
        Vector4? productionLight = local ? _character!.InteriorLight : remoteLight?.Light;
        Vector3 lightFeet = local ? _controller?.Position ?? unit.Position : remoteLight?.Feet ?? unit.Position;
        float? terrainWorldZ = _terrain?.SampleHeight(lightFeet.X, lightFeet.Y);
        Vector3? resolvedFloorLight = _wmo?.ResolveInteriorLight(lightFeet, terrainWorldZ);
        var camera = _window.Camera;
        var evidence = new
        {
            schemaVersion = 1, evidenceKind = "live-equipment-observation", label,
            takenUtc = DateTimeOffset.UtcNow,
            sessionGuid = $"0x{LocalPlayerGuid:X16}", controlledGuid = $"0x{ControlledGuid:X16}",
            observedGuid = $"0x{guid:X16}", subject = args[2],
            inWorld = _net?.IsInWorld == true, unit.DisplayId, unit.Scale,
            serverSheath = unit.Fields.SheathState, playerFlags = unit.Fields.PlayerFlags,
            position = unit.Position, orientation = unit.Orientation,
            visibleItems, renderedKitAvailable = rendered is not null && attachmentResolutionErrors.Length == 0, rendered,
            attachmentResolutionErrors,
            camera = new { camera.Position, camera.Target, camera.EyeTarget, camera.Yaw,
                camera.OrbitYaw, camera.Pitch, camera.Distance, camera.EffectiveDistance,
                achievedEyeTargetDistance = Vector3.Distance(camera.Position, camera.EyeTarget),
                camera.FieldOfViewDegrees, camera.AspectRatio, collisionEnabled = _config.Camera.Collision },
            lighting = new { source = _timeSource.ToString(), timeOfDay = _atmosphere.TimeOfDayHours,
                description = WorldClockDescription(),
                interior = new {
                    enabled = _interiorUnitLight.Enabled,
                    productionUniformAvailable = productionLight.HasValue,
                    productionUniform = productionLight,
                    productionWeight = productionLight?.W,
                    productionSource = local ? "last-assigned-controlled-character-uniform" : "last-observed-streamed-world-draw-uniform",
                    sampleFeet = lightFeet, terrainWorldZ,
                    resolvedFloorColor = resolvedFloorLight,
                    floorClassifiedInterior = resolvedFloorLight.HasValue,
                    room = _wmo?.DescribeInteriorLight(lightFeet, terrainWorldZ),
                    residentVersion = _wmo?.ResidentVersion,
                    bakedLightScale = local ? _character!.BakedLightScale : _creatures?.BakedLightScale,
                    limitation = "The uniform is the last renderer observation; the fresh floor query is diagnostic only. Allow a rendered frame after watch/movement. No floor color means no interior floor resolved, not proof of an outdoor location."
                } },
            limitation = "Observation is not acceptance. Match expected entries and display IDs, protocol result and paired gameplay screenshot; inspect remote after equipment watch and at least one rendered frame.",
        };
        string output = _liveRunOptions?.OutputDirectory ?? "live-runs";
        string directory = Path.Combine(Path.IsPathRooted(output) ? output : Path.Combine(_config.RepoRoot, output), "equipment");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{++_liveEquipmentInspectionOrdinal:D3}-{label}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, EquipmentCaptureManifest.JsonOptions));
        Console.WriteLine($"[live-equipment] guid=0x{guid:X16};rendered={rendered is not null};path={path}");
        if (attachmentResolutionErrors.Length > 0)
        {
            if (_liveRunOptions is not null) FinishLiveBootstrap("EQUIPMENT_RESOLUTION_FAILED", string.Join(";", attachmentResolutionErrors));
            return false;
        }
        return true;
    }
}
