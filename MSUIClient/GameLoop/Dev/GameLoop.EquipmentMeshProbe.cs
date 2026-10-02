using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Creator;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

/// <summary>Opt-in bounded native geometry evidence. Surface contact is review evidence, not artistic acceptance.</summary>
public sealed partial class GameLoop
{
    private readonly int _equipmentMeshProbeLimit = int.TryParse(
        Environment.GetEnvironmentVariable("MSUI_EQUIPMENT_MESH_PROBE"), out int requestedMeshProbes)
        ? Math.Clamp(requestedMeshProbes, 0, 64) : 0;
    private readonly Dictionary<string, object> _equipmentMeshProbeFiles = new(StringComparer.Ordinal);
    private readonly List<object> _equipmentMeshProbeDraws = [];
    private readonly HashSet<AttachedItemRenderer.Mount> _equipmentMeshProbeMounts = [];
    private bool _equipmentMeshProbeCollect;
    private readonly bool _equipmentMeshProbeWeapons =
        Environment.GetEnvironmentVariable("MSUI_EQUIPMENT_MESH_PROBE_WEAPONS") == "1";

    private bool EquipmentMeshProbeIncludes(AttachedItemRenderer.Mount mount) => mount.InventoryType == 1 ||
        mount.AttachmentId is AttachedItemRenderer.AttachShoulderLeft or AttachedItemRenderer.AttachShoulderRight ||
        (_equipmentMeshProbeWeapons && mount.HeldSlot >= 0);

    private static string EquipmentMeshProbeKey(EquipmentCaptureCase specimen) =>
        $"{specimen.Group}-{specimen.Pose.Key}-sheath{specimen.Sheath}";

    private void BeginEquipmentMeshProbeFrame(EquipmentCaptureCase specimen)
    {
        _equipmentMeshProbeDraws.Clear();
        _equipmentMeshProbeMounts.Clear();
        _equipmentMeshProbeCollect = _equipmentMeshProbeFiles.Count < _equipmentMeshProbeLimit &&
            !_equipmentMeshProbeFiles.ContainsKey(EquipmentMeshProbeKey(specimen));
    }

    private void ObserveEquipmentMeshDraw(AttachedItemRenderer.Mount mount, AttachedItemRenderer.Batch batch,
        Matrix4x4 world, Matrix4x4[] palette, int boneCount)
    {
        // Shoulder mounts predate inventory metadata; identify their semantic attachment IDs.
        if (!_equipmentMeshProbeCollect || !EquipmentMeshProbeIncludes(mount)) return;
        M2Model model = mount.Model.Source;
        // The character draws a cloned snapshot of its built mounts; count the built mount it
        // came from, so a stale snapshot still fails the expected/submitted comparison below.
        _equipmentMeshProbeMounts.Add(mount.SnapshotOrigin ?? mount);
        var skin = boneCount > 0 ? palette.Take(boneCount).ToArray() : null;
        var vertices = EquipmentProbeVertices(model, skin, world);
        int start = checked((int)batch.IndexStart), count = checked((int)batch.IndexCount);
        if (start < 0 || count < 0 || start + count > model.Indices.Count || count % 3 != 0)
            throw new InvalidDataException("mesh-probe-invalid-attachment-range");
        _equipmentMeshProbeDraws.Add(new
        {
            mount.Label, mount.DisplayId, mount.InventoryType, mount.EquipmentSlot, mount.HeldSlot, mount.ItemSheath,
            model = mount.Model.Path, shoulderFit = mount.Model.ShoulderFit, vertices,
            indices = model.Indices.Skip(start).Take(count).Select(x => (int)x).ToArray(),
            batch.BlendMode, batch.TwoSided, batch.NoZTest, batch.NoZWrite,
            cameraFacingPalette = mount.Model.UsesCameraFacingPalette,
            boneCount,
            note = "Actual submitted batch; alpha-texture holes and screen occlusion are not removed from geometry.",
        });
    }

    private object? WriteEquipmentMeshProbe(EquipmentCaptureCase specimen, CharacterRenderer.UnitState state)
    {
        if (_equipmentMeshProbeLimit == 0) return null;
        string key = EquipmentMeshProbeKey(specimen);
        if (_equipmentMeshProbeFiles.TryGetValue(key, out var previous)) return previous;
        if (!_equipmentMeshProbeCollect) return new { status = "probe-limit-reached", limit = _equipmentMeshProbeLimit };
        _equipmentMeshProbeCollect = false;
        // A sheathed ranged weapon can be intentionally hidden; count only mounts resolved in this exact draw.
        var expectedMounts = _character!.Attached!.InspectionMounts.Where(m => m.Visible && EquipmentMeshProbeIncludes(m) &&
            AttachedItemRenderer.ResolveAttachment(m, specimen.Sheath) >= 0).ToArray();
        var missingMounts = expectedMounts.Where(m => !_equipmentMeshProbeMounts.Contains(m)).Select(m => m.Label).ToArray();
        if (missingMounts.Length > 0 || _equipmentMeshProbeMounts.Count != expectedMounts.Length)
            throw new InvalidDataException($"mesh-probe-missing-submitted-mounts:expected={expectedMounts.Length}:actual={_equipmentMeshProbeMounts.Count}:missing={string.Join(',', missingMounts)}");
        SpellUnitPose pose = _character!.SpellPose(state);
        M2Model model = pose.Model ?? throw new InvalidDataException("mesh-probe-body-unavailable");
        var visible = _character.ActiveGeosets.Select(x => x.Category * 100 + x.Variant).ToHashSet();
        var triangles = new List<int[]>();
        var seen = new HashSet<int>();
        foreach (var submesh in model.Submeshes.Where(x => visible.Contains(x.Id)))
        {
            int start = submesh.IndexStart, count = submesh.IndexCount;
            if (start < 0 || count < 0 || start + count > model.Indices.Count || count % 3 != 0)
                throw new InvalidDataException("mesh-probe-invalid-body-range");
            for (int i = start; i < start + count; i += 3)
                if (seen.Add(i)) triangles.Add([model.Indices[i], model.Indices[i + 1], model.Indices[i + 2], submesh.Id]);
        }
        if (triangles.Count == 0) throw new InvalidDataException("mesh-probe-body-empty");
        var document = new
        {
            schemaVersion = 1, key, set = specimen.Set.Key, race = specimen.Race, sex = specimen.Sex,
            pose = specimen.Pose, sheathState = specimen.Sheath, sampledView = specimen.View,
            coordinateSpace = "absolute native client world Z-up, after body ModelToWorld; same completed draw",
            appearance = new { explicitRequest = _equipmentManifest!.Appearance is not null,
                skin = _character.SkinId, face = _character.FaceId, hairStyle = _character.HairStyleId,
                hairColor = _character.HairColorId, facialHair = _character.FacialHairId },
            body = new { model = _character.ModelPath, vertices = EquipmentProbeVertices(model, pose.Skin, pose.UnitTransform), triangles, visibleGeosets = visible.Order().ToArray() },
            attachments = _equipmentMeshProbeDraws.ToArray(),
            expectedAttachmentMounts = expectedMounts.Length, capturedAttachmentMounts = _equipmentMeshProbeMounts.Count,
            limitations = new[] { "Geometry contains alpha-cutout triangles and hidden backfaces; surface intersections require material and visual review.", "Enclosure is not penetration: a head wholly inside a helmet has zero surface crossing.", "No universal zero-contact threshold; compare same body/appearance/pose/view against stock.", "Face occlusion and artistic quality still require images; this does not claim either." },
            runtimeVerified = false,
        };
        string file = key + ".mesh.json.gz";
        string path = Path.Combine(_variantOutputDirectory, file);
        using (var stream = File.Create(path))
        using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, document, EquipmentCaptureManifest.JsonOptions);
        object evidence = new { status = "captured-contact-review-required", file,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            bytes = new FileInfo(path).Length, bodyTriangles = triangles.Count,
            attachmentDraws = _equipmentMeshProbeDraws.Count, sampledView = specimen.View.Key };
        _equipmentMeshProbeFiles.Add(key, evidence);
        return evidence;
    }

    private static float[][] EquipmentProbeVertices(M2Model model, IReadOnlyList<Matrix4x4>? skin, Matrix4x4 world)
    {
        var vertices = new float[model.Vertices.Count][];
        for (int i = 0; i < vertices.Length; i++)
        {
            TargetMeshPickLaw.SkinVertex(model.Vertices[i], skin, out Vector3 point, out _);
            point = Vector3.Transform(point, world);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new InvalidDataException("mesh-probe-nonfinite-vertex");
            vertices[i] = [point.X, point.Y, point.Z];
        }
        return vertices;
    }
}
