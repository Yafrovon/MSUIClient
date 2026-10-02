using System.Numerics;
using MSUIClient.Creator;
using MSUIClient.Engine;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

/// <summary>Opt-in offline proof that retained remote-style MountSets remain body-specific
/// while one production attachment renderer and its GPU caches draw several bodies.</summary>
public sealed partial class GameLoop
{
    private GL? _equipmentConcurrentGl;
    private sealed record EquipmentConcurrentBody(byte Race, byte Sex, string BodyCode,
        CharacterRenderer Character, AttachedItemRenderer.MountSet Mounts);
    private readonly List<EquipmentConcurrentBody> _equipmentConcurrentBodies = [];
    private readonly List<object> _equipmentConcurrentEvidence = [];
    private readonly List<string> _equipmentConcurrentErrors = [];
    private readonly Dictionary<object, int> _equipmentConcurrentModelIds = new(ReferenceEqualityComparer.Instance);

    private void DisposeEquipmentConcurrent()
    {
        foreach (var body in _equipmentConcurrentBodies)
            if (!ReferenceEquals(body.Character, _character)) body.Character.Dispose();
        _equipmentConcurrentBodies.Clear();
        _equipmentConcurrentEvidence.Clear();
        _equipmentConcurrentErrors.Clear();
    }

    private void PrepareEquipmentConcurrent(EquipmentCaptureCase specimen, List<string> errors)
    {
        DisposeEquipmentConcurrent();
        if (_equipmentManifest!.ConcurrentBodies.Length == 0) return;
        var renderer = _character!.Attached!;
        string primaryCode = renderer.RaceGenderCode;
        try
        {
            Add(specimen.Race, specimen.Sex, _character);
            foreach (var peer in _equipmentManifest.ConcurrentBodies)
            {
                var character = new CharacterRenderer(_equipmentConcurrentGl!, _config, null, null);
                try
                {
                    string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                    if (!File.Exists(Path.Combine(shaderDir, "character.vert")))
                        shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                    character.LoadShaders(shaderDir);
                    character.CopyRuntimeTuningFrom(_character);
                    character.BindPose = false;
                    character.FrozenStandPose = false;
                    var equipment = new CharacterEquipment();
                    foreach (var item in specimen.Set.Equipment)
                        equipment.Add($"concurrent-{item.DisplayId}", item.DisplayId, item.InventoryType,
                            item.EquipmentSlot, item.ItemClass, item.ItemSubclass, item.Material, item.Sheath);
                    character.Equipment = equipment;
                    if (!character.Load(PlayerRaceName(peer.Race), peer.Sex == 1 ? "Female" : "Male"))
                        throw new InvalidDataException($"concurrent-body-unavailable:{peer.Race}:{peer.Sex}");
                    character.ApplyEquipment();
                    Add(peer.Race, peer.Sex, character);
                }
                catch { character.Dispose(); throw; }
            }
        }
        finally { renderer.RaceGenderCode = primaryCode; }

        void Add(byte race, byte sex, CharacterRenderer character)
        {
            string code = character.Attached!.RaceGenderCode;
            renderer.RaceGenderCode = code;
            var mounts = renderer.BuildMountSet(character.Equipment);
            errors.AddRange(mounts.ResolutionErrors.Select(x => $"concurrent:{code}:{x}"));
            int expected = character.Equipment.Pieces.Where(x => x.NeedsAttachment && x.Row is not null)
                .Sum(x => x.InventoryType == CharacterEquipment.Slot.Shoulders
                    ? (x.Row!.ModelName1.Length > 0 ? 1 : 0) + (x.Row.ModelName2.Length > 0 ? 1 : 0) : 1);
            if (mounts.Count != expected) errors.Add($"concurrent-mount-count:{code}:{mounts.Count}/{expected}");
            RecordEquipmentAsset(character.ModelPath);
            foreach (var mount in mounts.Items)
            {
                if (!_equipmentConcurrentModelIds.ContainsKey(mount.Model))
                    _equipmentConcurrentModelIds.Add(mount.Model, _equipmentConcurrentModelIds.Count + 1);
                RecordEquipmentAsset(mount.Model.Path);
                if (mount.Model.ShoulderFit is { } fit)
                {
                    RecordEquipmentAsset(fit.DefaultPath);
                    RecordEquipmentAsset(fit.ManifestPath!);
                    RecordEquipmentAsset(fit.SkinPath!);
                }
                foreach (var batch in mount.Model.Batches)
                    if (renderer.InspectionTexturePath(batch.Texture) is { } texture) RecordEquipmentAsset(texture);
                    else errors.Add($"concurrent-texture-unbound:{code}:{mount.Label}");
            }
            _equipmentConcurrentBodies.Add(new(race, sex, code, character, mounts));
        }
    }

    private void RenderEquipmentConcurrent(EquipmentCaptureCase specimen, Camera camera)
    {
        _equipmentConcurrentEvidence.Clear();
        _equipmentConcurrentErrors.Clear();
        var renderer = _character!.Attached!;
        var oldDraw = renderer.InspectionMeshDraw;
        var oldPlacement = renderer.InspectionPlacement;
        var toward = new Vector3(-camera.Position.X, -camera.Position.Y, 0);
        var across = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, toward));
        var order = Enumerable.Range(0, _equipmentConcurrentBodies.Count);
        if ((_equipmentIndex & 1) != 0) order = order.Reverse();
        try
        {
            foreach (int index in order)
            {
                var body = _equipmentConcurrentBodies[index];
                var character = body.Character;
                if (!character.AvailableAnimationIds.Contains(specimen.Pose.AnimationId))
                    throw new InvalidDataException($"concurrent-animation-unavailable:{body.BodyCode}:{specimen.Pose.AnimationId}");
                character.InspectionPose = (specimen.Pose.AnimationId, specimen.Pose.TimeSeconds);
                var state = new CharacterRenderer.UnitState
                {
                    Position = across * ((index - (_equipmentConcurrentBodies.Count - 1) * .5f) * 2.5f),
                    Yaw = 0, Grounded = true, HasIntent = true,
                };
                // Body pose is evaluated by the production renderer; its own attachments are
                // suppressed only in this fixture so the retained remote-style set draws once.
                bool enabled = character.Attached!.Enabled;
                try { character.Attached.Enabled = false; character.Render(camera, state); }
                finally { character.Attached.Enabled = enabled; }
                var pose = character.SpellPose(state);
                var relative = pose.UnitTransform;
                relative.M41 -= camera.Position.X;
                relative.M42 -= camera.Position.Y;
                relative.M43 -= camera.Position.Z;
                var draws = new List<object>();
                var drawn = new HashSet<AttachedItemRenderer.Mount>();
                var placements = new List<object>();
                renderer.InspectionMeshDraw = (mount, batch, world, skin, bones) =>
                {
                    drawn.Add(mount);
                    draws.Add(new { mount.Label, mount.AttachmentId, mount.DisplayId, path = mount.Model.Path,
                        modelInstance = _equipmentConcurrentModelIds[mount.Model], shoulderFit = mount.Model.ShoulderFit,
                        batch.IndexStart, batch.IndexCount, texture = renderer.InspectionTexturePath(batch.Texture),
                        worldTransform = world, itemBoneCount = bones });
                };
                renderer.InspectionPlacement = (mount, id, bone, world, status) =>
                {
                    placements.Add(new { mount.Label, id, bone, worldTransform = world, status });
                    if (status != "resolved" && status != "hidden-by-sheath")
                        _equipmentConcurrentErrors.Add($"concurrent-placement:{body.BodyCode}:{mount.Label}:{status}");
                };
                renderer.Render(camera, relative, pose.Model, pose.Skin!.ToArray(), body.Mounts,
                    specimen.Sheath, modelTime: specimen.Pose.TimeSeconds);
                foreach (var mount in body.Mounts.Items.Where(x => x.InventoryType == CharacterEquipment.Slot.Shoulders))
                    if (!drawn.Contains(mount)) _equipmentConcurrentErrors.Add($"concurrent-shoulder-not-drawn:{body.BodyCode}:{mount.Label}");
                _equipmentConcurrentEvidence.Add(new
                {
                    body.Race, body.Sex, body.BodyCode, slotIndex = index, bodyModel = character.ModelPath,
                    bodyTransform = pose.UnitTransform, bodyPiecesDrawn = character.VisiblePieces,
                    renderer = "one AttachedItemRenderer.Render(MountSet), same GL and shared cache",
                    retainedAcrossFrames = true, mountCount = body.Mounts.Count,
                    resolutionErrors = body.Mounts.ResolutionErrors, draws, placements,
                });
            }
        }
        finally { renderer.InspectionMeshDraw = oldDraw; renderer.InspectionPlacement = oldPlacement; }
    }
}
