using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Creator;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

/// <summary>Deterministic equipment evidence using the shipping character, atlas and attachment renderer.</summary>
public sealed partial class GameLoop
{
    private EquipmentCaptureManifest? _equipmentManifest;
    private readonly List<EquipmentCaptureCase> _equipmentCases = [];
    private readonly List<object> _equipmentEvidence = [];
    private readonly Dictionary<string, object> _equipmentAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _equipmentPlacements = [];
    private readonly List<string> _equipmentPlacementErrors = [];
    private int _equipmentIndex, _equipmentErrorCases, _equipmentExpectedCases;
    private string _equipmentGroup = "";
    private string[] _equipmentGroupErrors = [];
    private object[] _equipmentMountEvidence = [];
    private bool _equipmentLimited;
    private sealed record EquipmentCaptureCase(EquipmentCaptureSet Set, byte Race, byte Sex,
        EquipmentCapturePose Pose, byte Sheath, EquipmentCaptureView View)
    {
        public string Group => $"{Set.Key}-race{Race}-sex{Sex}";
        public string Key => $"{Group}-{Pose.Key}-sheath{Sheath}-{View.Key}";
    }

    private void InitEquipmentCapture(GL gl)
    {
        string manifestPath = _variantBatchOptions?.ListFile is { } list ? ResolveBatchPath(list) :
            throw new InvalidDataException("equipment axis requires --list manifest.json");
        _equipmentManifest = EquipmentCaptureManifest.Load(manifestPath);
        _equipmentConcurrentGl = gl;
        _batchPortraitTarget?.Dispose();
        _batchPortraitTarget = new PortraitRenderTarget(gl, _equipmentManifest.Width, _equipmentManifest.Height);
        foreach (var set in _equipmentManifest.Sets)
        foreach (byte race in _equipmentManifest.Races)
        foreach (byte sex in _equipmentManifest.Sexes)
        foreach (var pose in _equipmentManifest.Poses)
        foreach (byte sheath in _equipmentManifest.SheathStates)
        foreach (var view in _equipmentManifest.Views)
            _equipmentCases.Add(new(set, race, sex, pose, sheath, view));
        _equipmentExpectedCases = _equipmentCases.Count;
        if (_variantBatchOptions?.Limit is { } limit && limit < _equipmentCases.Count)
        {
            _equipmentCases.RemoveRange(limit, _equipmentCases.Count - limit);
            _equipmentLimited = true;
        }
        File.WriteAllText(Path.Combine(_variantOutputDirectory, "manifest.json"),
            JsonSerializer.Serialize(_equipmentManifest, EquipmentCaptureManifest.JsonOptions));
        RecordEquipmentAsset(ItemDisplayTable.MpqPath);
        _character!.FrozenStandPose = false;
        _character.MagentaUnbound = false;
        _character.SunDirection = Vector3.Normalize(new Vector3(-.4f, -.6f, 1));
        _character.SunColor = Vector3.One;
        _character.SunIntensity = .65f;
        _character.AmbientColor = Vector3.One;
        _character.AmbientIntensity = .55f;
        _character.FogStart = 1000;
        _character.FogEnd = 2000;
        Console.WriteLine($"[equipment-capture] {_equipmentCases.Count}/{_equipmentExpectedCases} requested captures; production renderer, offline only; out={_variantOutputDirectory}");
    }

    private void StepEquipmentCapture()
    {
        if (_equipmentIndex >= _equipmentCases.Count) { FinishEquipmentCapture(null); return; }
        EquipmentCaptureCase specimen = _equipmentCases[_equipmentIndex];
        var errors = new List<string>();
        try
        {
            if (_equipmentGroup != specimen.Group) PrepareEquipmentCapture(specimen);
            errors.AddRange(_equipmentGroupErrors);
            if (!_character!.AvailableAnimationIds.Contains(specimen.Pose.AnimationId))
                throw new InvalidDataException($"animation-unavailable:{specimen.Pose.AnimationId}");
            _character.InspectionPose = (specimen.Pose.AnimationId, specimen.Pose.TimeSeconds);
            _character.SheathState = specimen.Sheath;
            var state = new CharacterRenderer.UnitState { Position = Vector3.Zero, Yaw = 0, Grounded = true, HasIntent = true };
            float height = _character.StandBoxHeight();
            float distance = MathF.Max(3, height * 2.6f) * MathF.Max(1, _equipmentConcurrentBodies.Count * .8f);
            Camera camera = PortraitCamera(Vector3.Zero, specimen.View.YawDegrees * MathF.PI / 180f, height * .53f, distance);
            camera.Pitch = specimen.View.PitchDegrees * MathF.PI / 180f;
            camera.AspectRatio = _equipmentManifest!.Width / (float)_equipmentManifest.Height;
            byte[] pixels = [];
            EquipmentCapturePixels.Measurements metrics = null!;
            int attempts = 0;
            do
            {
                BeginEquipmentMeshProbeFrame(specimen);
                _equipmentPlacements.Clear();
                _equipmentPlacementErrors.Clear();
                _batchPortraitTarget!.Bake(() =>
                {
                    if (_equipmentConcurrentBodies.Count > 0) RenderEquipmentConcurrent(specimen, camera);
                    else _character.Render(camera, state);
                }, transparent: true);
                pixels = _batchPortraitTarget.CaptureRgba();
                metrics = EquipmentCapturePixels.Measure(pixels, _equipmentManifest.Width, _equipmentManifest.Height);
                attempts++;
                if (metrics.BorderPixels == 0 || attempts >= 4) break;
                camera.Distance *= 1.4f;
                camera.EffectiveDistance = camera.Distance;
            } while (true);
            errors.AddRange(_equipmentPlacementErrors);
            errors.AddRange(_equipmentConcurrentErrors);
            if (metrics.SubjectPixels == 0) errors.Add("blank-render");
            if (metrics.BorderPixels != 0) errors.Add("framing-clips-subject");
            string file = specimen.Key + ".png";
            PortraitRenderTarget.SaveRgbaPng(Path.Combine(_variantOutputDirectory, file), _equipmentManifest.Width, _equipmentManifest.Height, pixels);
            string valueFile = specimen.Key + "-value-contour.png";
            PortraitRenderTarget.SaveRgbaPng(Path.Combine(_variantOutputDirectory, valueFile), _equipmentManifest.Width, _equipmentManifest.Height,
                EquipmentCapturePixels.ValueAndContour(pixels, _equipmentManifest.Width, _equipmentManifest.Height));
            object? meshProbe = _equipmentConcurrentBodies.Count == 0 ? WriteEquipmentMeshProbe(specimen, state) : null;
            _equipmentEvidence.Add(new
            {
                meshProbe,
                concurrentBodies = _equipmentConcurrentEvidence.ToArray(),
                key = specimen.Key, set = specimen.Set.Key, race = specimen.Race, sex = specimen.Sex,
                pose = specimen.Pose, sheathState = specimen.Sheath, view = specimen.View,
                appearance = new { explicitRequest = _equipmentManifest!.Appearance is not null,
                    skin = _character.SkinId, face = _character.FaceId, hairStyle = _character.HairStyleId,
                    hairColor = _character.HairColorId, facialHair = _character.FacialHairId },
                image = file, valueContourImage = valueFile,
                pngSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_variantOutputDirectory, file)))),
                status = errors.Count == 0 ? "captured-review-required" : "technical-error", errors,
                framingAttempts = attempts, metrics,
                camera = new { position = camera.Position, camera.Yaw, camera.Pitch, camera.Distance, camera.EyeHeight, camera.FieldOfViewDegrees, camera.AspectRatio, camera.NearPlane, camera.FarPlane, viewProjection = camera.ViewProjection },
                bodyModel = _character.ModelPath, bodyTransform = _character.BuildTransform(state),
                lighting = new { _character.SunDirection, _character.SunIntensity, _character.AmbientIntensity },
                geosets = _character.ActiveGeosets.Select(x => new { x.Category, x.Variant }).ToArray(),
                bodyTextureResolutions = _character.Equipment.InspectionBodyTextures.Select(x => new { x.Region, x.Declared, x.Resolved }).ToArray(),
                bodyTextureSlots = _character.InspectionTextureSlots.Select(x => new { x.Type, x.Source, x.Fill }).ToArray(),
                mounts = _equipmentMountEvidence, placements = _equipmentPlacements.ToArray(),
                rendererWarnings = new { _character.UnboundSlots, _character.HairResolution },
            });
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            _equipmentEvidence.Add(new { key = specimen.Key, status = "technical-error", errors });
            Console.Error.WriteLine($"[equipment-capture] {specimen.Key}: {exception.Message}");
        }
        if (errors.Count > 0) _equipmentErrorCases++;
        _equipmentIndex++;
        if (_equipmentIndex % 24 == 0 || _equipmentIndex == _equipmentCases.Count)
            Console.WriteLine($"[equipment-capture] {_equipmentIndex}/{_equipmentCases.Count}, technical-error cases={_equipmentErrorCases}");
        if (_equipmentIndex % 120 == 0) WriteEquipmentEvidence();
    }

    private void PrepareEquipmentCapture(EquipmentCaptureCase specimen)
    {
        var equipment = new CharacterEquipment();
        for (int i = 0; i < specimen.Set.Equipment.Length; i++)
        {
            var item = specimen.Set.Equipment[i];
            equipment.Add($"gear-{i}-{item.DisplayId}", item.DisplayId, item.InventoryType, item.EquipmentSlot,
                item.ItemClass, item.ItemSubclass, item.Material, item.Sheath);
        }
        _character!.Equipment = equipment;
        var appearance = _equipmentManifest!.Appearance ?? new EquipmentCaptureAppearance();
        if (_equipmentManifest.Appearance is not null) ValidateEquipmentCaptureAppearance(specimen, appearance);
        _character.SkinId = appearance.Skin;
        _character.FaceId = appearance.Face;
        _character.HairStyleId = appearance.HairStyle;
        _character.HairColorId = appearance.HairColor;
        _character.FacialHairId = appearance.FacialHair;
        if (!_character.Load(PlayerRaceName(specimen.Race), specimen.Sex == 1 ? "Female" : "Male"))
            throw new InvalidDataException("body-model-unavailable");
        _character.ApplyEquipment();
        var errors = new List<string>();
        var attached = _character.Attached ?? throw new InvalidDataException("attachment-renderer-unavailable");
        errors.AddRange(attached.InspectionErrors);
        foreach (var piece in equipment.Pieces)
        {
            if (piece.Row is null) { errors.Add($"display-not-found:{piece.DisplayId}"); continue; }
            if (!piece.NeedsAttachment) continue;
            int expected = piece.InventoryType == CharacterEquipment.Slot.Shoulders
                ? (piece.Row.ModelName1.Length > 0 ? 1 : 0) + (piece.Row.ModelName2.Length > 0 ? 1 : 0) : 1;
            int mounted = attached.InspectionMounts.Count(m => m.Label == piece.Name || m.Label == piece.Name + " (L)" || m.Label == piece.Name + " (R)");
            if (mounted != expected) errors.Add($"mount-count:{piece.DisplayId}:expected={expected}:actual={mounted}");
        }
        foreach (var texture in equipment.InspectionBodyTextures)
            if (texture.Resolved is null) errors.Add($"body-texture-missing:{texture.Region}:{texture.Declared}");
            else RecordEquipmentAsset(texture.Resolved);
        RecordEquipmentAsset(_character.ModelPath);
        foreach (var slot in _character.InspectionTextureSlots)
            if (!string.IsNullOrEmpty(slot.Source) && slot.Source.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                RecordEquipmentAsset(slot.Source);
        _equipmentMountEvidence = attached.InspectionMounts.Select(mount =>
        {
            RecordEquipmentAsset(mount.Model.Path);
            if (mount.Model.ShoulderFit is { } fit)
            {
                RecordEquipmentAsset(fit.DefaultPath);
                RecordEquipmentAsset(fit.ManifestPath!);
                RecordEquipmentAsset(fit.SkinPath!);
            }
            object[] batches = mount.Model.Batches.Select((batch, index) =>
            {
                string? texture = attached.InspectionTexturePath(batch.Texture);
                string? secondTexture = attached.InspectionTexturePath(batch.Texture2);
                if (texture is null) errors.Add($"attachment-texture-unbound:{mount.Label}:batch{index}");
                else RecordEquipmentAsset(texture);
                if (secondTexture is not null) RecordEquipmentAsset(secondTexture);
                return (object)new { index, texture, secondTexture, batch.IndexCount, batch.BlendMode, batch.TwoSided, batch.Unlit };
            }).ToArray();
            var vertices = mount.Model.Source.Vertices;
            var bounds = new
            {
                min = new Vector3(vertices.Min(v => v.PosX), vertices.Min(v => v.PosY), vertices.Min(v => v.PosZ)),
                max = new Vector3(vertices.Max(v => v.PosX), vertices.Max(v => v.PosY), vertices.Max(v => v.PosZ)),
                origin = Vector3.Zero,
                coordinateSpace = "client/glTF Y-up local (raw M2 x,y,z mapped to x,z,-y), before character attachment transform",
            };
            return (object)new { mount.Label, mount.DisplayId, mount.EquipmentSlot, mount.InventoryType, mount.ItemSheath,
                path = mount.Model.Path, shoulderFit = mount.Model.ShoulderFit, vertices = vertices.Count, triangles = mount.Model.Source.Indices.Count / 3, localBounds = bounds, batches };
        }).ToArray();
        attached.InspectionMeshDraw = _equipmentMeshProbeLimit > 0 ? ObserveEquipmentMeshDraw : null;
        attached.InspectionPlacement = (mount, attachmentId, bone, transform, status) =>
        {
            _equipmentPlacements.Add(new { mount.Label, model = mount.Model.Path, attachmentId, bone, worldTransform = transform, status });
            if (status is not ("resolved" or "hidden-by-sheath")) _equipmentPlacementErrors.Add($"{status}:{mount.Label}:{attachmentId}");
        };
        PrepareEquipmentConcurrent(specimen, errors);
        _equipmentGroupErrors = errors.ToArray();
        _equipmentGroup = specimen.Group;
    }

    private void ValidateEquipmentCaptureAppearance(EquipmentCaptureCase specimen, EquipmentCaptureAppearance appearance)
    {
        byte[] Required(string path)
        {
            RecordEquipmentAsset(path);
            return _mpq!.ReadFile(path) ?? throw new InvalidDataException($"appearance-dbc-missing:{path}");
        }
        var sections = CharSectionsTable.Parse(Required(CharSectionsTable.MpqPath))
            ?? throw new InvalidDataException("appearance-sections-invalid");
        var hair = CharHairGeosetsTable.Parse(Required(CharHairGeosetsTable.MpqPath))
            ?? throw new InvalidDataException("appearance-hair-table-invalid");
        var facial = CharacterFacialHairTable.Parse(Required(CharacterFacialHairTable.MpqPath))
            ?? throw new InvalidDataException("appearance-facial-table-invalid");
        if (sections.Find(specimen.Race, specimen.Sex, CharSectionsTable.SectionSkin, -1, appearance.Skin) is null)
            throw new InvalidDataException("appearance-skin-unavailable");
        if (sections.Find(specimen.Race, specimen.Sex, CharSectionsTable.SectionFace, appearance.Face, appearance.Skin) is null)
            throw new InvalidDataException("appearance-face-unavailable");
        if (hair.Find(specimen.Race, specimen.Sex, appearance.HairStyle) < 0)
            throw new InvalidDataException("appearance-hair-style-unavailable");
        if (sections.FindMatches(specimen.Race, specimen.Sex, CharSectionsTable.SectionHair, -1, appearance.HairColor).Count == 0)
            throw new InvalidDataException("appearance-hair-color-unavailable");
        if (facial.Find(specimen.Race, specimen.Sex, appearance.FacialHair) is null)
            throw new InvalidDataException("appearance-facial-style-unavailable");
    }

    private void RecordEquipmentAsset(string path)
    {
        if (_equipmentAssets.ContainsKey(path)) return;
        var resolved = _mpq!.ReadFileWithSupplier(path);
        _equipmentAssets[path] = resolved is { } asset
            ? new { path, supplier = asset.Item2, byteLength = asset.Item1.Length, sha256 = Convert.ToHexString(SHA256.HashData(asset.Item1)) }
            : new { path, error = "not-found-in-mounted-archives" };
    }

    private void WriteEquipmentEvidence()
    {
        File.WriteAllText(Path.Combine(_variantOutputDirectory, "captures.json"), JsonSerializer.Serialize(_equipmentEvidence, EquipmentCaptureManifest.JsonOptions));
        File.WriteAllText(Path.Combine(_variantOutputDirectory, "assets.json"), JsonSerializer.Serialize(_equipmentAssets.Values, EquipmentCaptureManifest.JsonOptions));
    }

    private void FinishEquipmentCapture(string? error)
    {
        if (_variantFinished) return;
        _variantFinished = true;
        try
        {
            Directory.CreateDirectory(_variantOutputDirectory);
            WriteEquipmentEvidence();
            bool complete = error is null && !_equipmentLimited && _equipmentIndex == _equipmentExpectedCases && _equipmentExpectedCases > 0;
            File.WriteAllText(Path.Combine(_variantOutputDirectory, "summary.json"), JsonSerializer.Serialize(new
            {
                evidenceKind = "offline-production-character-renderer", inWorldVerified = false, visualReviewRequired = true,
                generatedUtc = DateTime.UtcNow, captureComplete = complete, requested = _equipmentExpectedCases,
                completed = _equipmentIndex, technicalErrorCases = _equipmentErrorCases, error,
                fullVanillaBodyMatrix = _equipmentManifest?.Races.Length == 8 && _equipmentManifest?.Sexes.Length == 2,
                limitations = new[] { "Technical checks do not certify style, clipping against the body, or artistic coherence.", "No authenticated live world or remote observer was used.", _equipmentManifest?.Appearance is null
                    ? "Base appearance uses choice zero; other hairstyles require separate review."
                    : "Only the explicit appearance in the manifest was captured; other hairstyles require separate review.", "Value/contour images and scanline spans are diagnostic views, not automatic deformation verdicts." },
            }, EquipmentCaptureManifest.JsonOptions));
            WriteEquipmentReviewIndex();
            VariantBatchExitCode = !complete ? 1 : _equipmentErrorCases > 0 ? 4 : 0;
            Console.WriteLine($"[equipment-capture] finished: captures={_equipmentIndex}, expected={_equipmentExpectedCases}, errors={_equipmentErrorCases}, exit={VariantBatchExitCode}; visual review required, not live-world verified");
        }
        catch (Exception exception) { VariantBatchExitCode = 1; Console.Error.WriteLine(exception); }
        DisposeEquipmentConcurrent();
        _window.Close();
    }

    private void WriteEquipmentReviewIndex()
    {
        var html = new System.Text.StringBuilder("<!doctype html><meta charset=utf-8><title>Equipment capture review</title><style>body{background:#20242a;color:#eee;font:16px system-ui;margin:24px}main{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:16px}figure{margin:0;background:#303640;padding:12px}img{width:100%;background:repeating-conic-gradient(#414850 0% 25%,#333a42 0% 50%) 50%/24px 24px}figcaption{font-size:12px;overflow-wrap:anywhere}input{padding:12px;width:40em;max-width:90%}a{color:#8fcfff}</style><h1>Equipment capture review</h1><p>Actual MSUIClient equipment renderer. Visual review required; this is not live-world verification.</p><p><a href='summary.json'>Coverage and limitations</a> · <a href='captures.json'>Measurements and transforms</a> · <a href='assets.json'>Asset hashes</a></p><input id='filter' placeholder='Filter by set, race, sex, pose, sheath or view'><main>");
        foreach (var specimen in _equipmentCases.Take(_equipmentIndex))
        {
            string key = specimen.Key;
            if (!File.Exists(Path.Combine(_variantOutputDirectory, key + ".png"))) continue;
            html.Append($"<figure data-key='{key}'><a href='{key}.png'><img loading='lazy' src='{key}.png'></a><figcaption>{key}<br><a href='{key}-value-contour.png'>Value and silhouette contours</a></figcaption></figure>");
        }
        html.Append("</main><script>document.querySelector('#filter').oninput=e=>{const words=e.target.value.toLowerCase().split(/\\s+/);document.querySelectorAll('figure').forEach(f=>f.hidden=!words.every(w=>f.dataset.key.toLowerCase().includes(w)));}</script>");
        File.WriteAllText(Path.Combine(_variantOutputDirectory, "index.html"), html.ToString());
    }
}
