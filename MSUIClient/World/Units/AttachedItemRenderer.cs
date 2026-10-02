using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

public readonly record struct ItemGlowPlacement(string Key, string Path, Matrix4x4 Transform,
    bool RenderMesh = true);

/// <summary>
/// A drawn unit whose OWN body M2 authors particle/ribbon emitters (an elemental's flames,
/// a wisp's trail). The effect pipeline plays those emitters on the unit's live pose - the
/// body mesh itself is the unit renderer's, so there is nothing to render here but effects.
/// </summary>
public readonly record struct UnitModelEffectPlacement(ulong Guid, string ModelPath);

/// <summary>
/// Draws the equipment that has geometry of its own - helms, shoulders,
/// weapons and shields. Cape cloth is part of the character M2 and is handled
/// by CharacterRenderer's type-2 replaceable texture slot.
///
/// WHY THESE ARE NOT LIKE THE REST OF THE GEAR
///   A breastplate is paint on the character's own skin. A helm is a SEPARATE
///   M2 FILE that gets parented to a bone. Nothing about the body atlas or the
///   geoset rules touches these, which is why a fully-working Tier set can
///   still be missing its pauldrons.
///
/// HOW A MODEL FINDS ITS PLACE
///   Every M2 carries an Attachments array: a semantic id, a bone index and a
///   position in model space. M2Reader already parses it. The skinning matrix
///   the animator computes for that bone is exactly the transform a rigid point
///   attached to it needs, so the whole placement is:
///
///       item vertex -> T(attachment.Position) -> Skin[attachment.BoneIndex]
///                   -> the character's own instance matrix
///
///   which means an attached model follows the animation for free. No separate
///   bone chain, no second skinning path. The item M2s themselves are drawn
///   unskinned - a sword does not bend.
///
/// ATTACHMENT IDS come from SuperUI's equip.js, where they were established by
/// eye rather than from a table:
///     0  LeftWrist   - shields mount HERE, not on the palm
///     1  HandRight
///     2  HandLeft
///     5  ShoulderRight  (ModelName2, the R file)
///     6  ShoulderLeft   (ModelName1, the L file)
///    11  Helm
/// </summary>
public sealed class AttachedItemRenderer : IDisposable
{
    public const int AttachLeftWrist = 0;
    public const int AttachHandRight = 1;
    public const int AttachHandLeft = 2;
    public const int AttachShoulderRight = 5;
    public const int AttachShoulderLeft = 6;
    public const int AttachHelm = 11;
    public const int AttachBackRight = 26;
    public const int AttachBackLeft = 27;
    public const int AttachShieldBack = 28;
    public const int AttachBackLowerMain = 30;
    public const int AttachBackLowerOff = 31;
    public const int AttachHipMain = 32;
    public const int AttachHipOff = 33;

    /// <summary>Position(3), normal(3), UV0(2), normalized bone weights(4), bone indices(4), UV1(2).</summary>
    private const int FloatsPerVertex = 18;

    internal sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public Texture? Texture2;
        public bool FirstUsesUv1;
        public bool SecondUsesUv1;
        public bool SecondEnvironmentMapped;
        public bool SteadyModulatedGlow;
        public bool TwoSided;
        public int BlendMode;
        public bool NoZWrite;
        public bool NoZTest;
        public bool Unlit;
        public int FogPolicy;
        public bool EnvironmentMapped;
        public bool SteadyWarglaiveBlade;
        public M2Batch? Source;
        public bool Transparent => BlendMode >= 2 || NoZWrite;
    }

    internal sealed class ItemModel : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public List<Batch> Batches = [];
        public string Path = "";
        public ShoulderFitSelection? ShoulderFit;
        public M2Model Source = null!;
        public bool UsesCameraFacingPalette;

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            if (Vao != 0) _gl.DeleteVertexArray(Vao);
            if (Vbo != 0) _gl.DeleteBuffer(Vbo);
            if (Ebo != 0) _gl.DeleteBuffer(Ebo);
        }
    }

    /// <summary>One mounted piece: a model, where it hangs, and what it is called.</summary>
    internal sealed class Mount
    {
        public ItemModel Model = null!;
        public int AttachmentId;
        public int HeldSlot = -1;
        public int InventoryType;
        public byte ItemSheath;
        public uint DisplayId;
        public int EquipmentSlot = -1;
        // Diagnostic identity is independent of legacy render/effect owner fields.
        public EquipmentMountIdentity InspectionIdentity;
        public int ItemVisualId;
        public uint[] Enchants = [];
        public string Label = "";
        public bool Visible = true;
        // The built mount a render snapshot was cloned from, so diagnostics that observe the
        // drawn clone can match it to the mount list; null on built mounts themselves.
        internal Mount? SnapshotOrigin;

        internal Mount Snapshot()
        {
            var copy = (Mount)MemberwiseClone();
            copy.SnapshotOrigin = SnapshotOrigin ?? this;
            return copy;
        }
    }

    public sealed class MountSet
    {
        internal readonly List<Mount> Items = [];
        public int Count => Items.Count;
        internal readonly List<string> Errors = [];
        public IReadOnlyList<string> ResolutionErrors => Errors;
    }

    private sealed class SharedResources
    {
        public required GL Gl;
        public Shader? Shader;
        public readonly Dictionary<string, ItemModel?> Models =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Texture?> Textures =
            new(StringComparer.OrdinalIgnoreCase);
        public ItemVisualCatalog? ItemVisuals;
        public EnchantCatalog? Enchants;
        public bool GlowCatalogsLoaded;
        public int Owners;
    }

    private static readonly object SharedGate = new();
    private static SharedResources? s_shared;

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly SharedResources _shared;
    private bool _disposed;

    private Shader? Shader => _shared.Shader;
    private Dictionary<string, ItemModel?> Models => _shared.Models;
    private Dictionary<string, Texture?> Textures => _shared.Textures;
    private readonly List<Mount> _mounts = [];
    private MountSet? _mountSnapshot;
    private readonly List<string> _resolutionErrors = [];
    internal IReadOnlyList<string> InspectionErrors => _resolutionErrors;
    internal IReadOnlyList<Mount> InspectionMounts => _mounts;
    internal Action<Mount, int, int, Matrix4x4?, string>? InspectionPlacement { get; set; }
    internal Action<Mount, Batch, Matrix4x4, Matrix4x4[], int>? InspectionMeshDraw { get; set; }
    internal string? InspectionTexturePath(Texture? texture) => texture is null ? null :
        Textures.FirstOrDefault(pair => ReferenceEquals(pair.Value, texture)).Key?.Split('|', 2).Last();
    private readonly List<ItemGlowPlacement> _glowPlacements = [];
    private readonly List<CarriedLightPlacement> _carriedLights = [];
    private readonly List<FishingPoleTipPlacement> _fishingPoleTips = [];
    private readonly HashSet<string> _reportedHeldPlacements = new(StringComparer.Ordinal);
    private readonly Matrix4x4[] _itemSkin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _packedItemSkin = new float[M2Animator.MaxBones * 12];

    public bool Enabled { get; set; } = true;
    public int MountCount => _mounts.Count;

    /// <summary>
    /// Label and visibility of each mounted piece, so the HUD can switch them
    /// off one at a time.
    ///
    /// The geoset checkboxes cannot do this: an attached item is a separate M2,
    /// not a geoset, so hiding every geoset category leaves the helm and the
    /// sword exactly where they were. Two different mechanisms need two
    /// different switches.
    /// </summary>
    public IEnumerable<(string Label, bool Visible)> Mounts
        => _mounts.Select(m => (m.Label, m.Visible));

    public void SetMountVisible(string label, bool visible)
    {
        foreach (var mount in _mounts)
            if (mount.Label == label && mount.Visible != visible)
            {
                mount.Visible = visible;
                _mountSnapshot = null;
            }
    }

    /// <summary>
    /// Retain one attachment presentation across a frozen body pose. Models stay shared,
    /// but visibility belongs to the snapshot; unchanged normal frames reuse this set.
    /// </summary>
    public MountSet SnapshotMountSet()
    {
        if (_mountSnapshot is not null) return _mountSnapshot;
        var snapshot = new MountSet();
        foreach (Mount mount in _mounts) snapshot.Items.Add(mount.Snapshot());
        snapshot.Errors.AddRange(_resolutionErrors);
        return _mountSnapshot = snapshot;
    }

    public int DrawnLastFrame { get; private set; }
    public IReadOnlyList<ItemGlowPlacement> GlowPlacements => _glowPlacements;
    public IReadOnlyList<CarriedLightPlacement> CarriedLights => _carriedLights;
    public IReadOnlyList<FishingPoleTipPlacement> FishingPoleTips => _fishingPoleTips;
    public string GlowOwnerKey { get; set; } = "local";
    public byte SheathState { get; set; }

    public HandGrip ResolveHandGrip(M2Model? character)
        => Enabled ? ResolveHandGrip(character, _mounts, SheathState) : HandGrip.None;

    public static HandGrip ResolveHandGrip(M2Model? character, MountSet? mounts, byte sheathState)
        => ResolveHandGrip(character, mounts?.Items, sheathState);

    private static HandGrip ResolveHandGrip(M2Model? character, IReadOnlyList<Mount>? mounts, byte sheathState)
    {
        if (character is null || mounts is null) return HandGrip.None;
        HandGrip hands = HandGrip.None;
        foreach (Mount mount in mounts)
        {
            if (!mount.Visible) continue;
            int id = ResolveAttachment(mount, sheathState);
            HandGrip hand = HandGripLaw.ForAttachment(id);
            if (hand == HandGrip.None) continue;
            M2Attachment? attachment = FindAttachment(character, id);
            if (attachment is not null && attachment.BoneIndex < character.Bones.Count)
                hands |= hand;
        }
        return hands;
    }

    /// <summary>Matched to the character so a pauldron lights like the shoulder under it.</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    /// <summary>The owner's room light (rgb MOCV/255, a interior weight); zero = sky.</summary>
    public Vector4 InteriorLight { get; set; }
    public float BakedLightScale { get; set; } = 2f;
    public float ShadowSoftness { get; set; } = 0f;   // zero selects CharacterRenderer's in-world Model2 response
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;
    public float AlphaCutoff { get; set; } = 0.35f;
    public float BodyAlpha { get; set; } = 1f;
    public Vector3 BodyTint { get; set; } = Vector3.One;

    /// <summary>
    /// Two-letter race code plus M or F, e.g. HuM.
    ///
    /// HELMS ARE PER RACE AND GENDER and nothing else is. A helm has to fit the
    /// head it sits on, so vanilla ships one file per head shape and the name
    /// carries this suffix - Helm_Plate_A_01_HuM. Shoulders and weapons have a
    /// single file each. Missing this is the most likely reason a helm resolves
    /// to nothing while the pauldrons mount fine.
    /// </summary>
    public string RaceGenderCode { get; set; } = "HuM";

    public AttachedItemRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
        lock (SharedGate)
        {
            if (s_shared is null)
                s_shared = new SharedResources { Gl = gl };
            else if (!ReferenceEquals(s_shared.Gl, gl))
                throw new InvalidOperationException(
                    "attachment resources cannot span unrelated GL contexts");
            _shared = s_shared;
            _shared.Owners++;
            if (!_shared.GlowCatalogsLoaded)
            {
                _shared.GlowCatalogsLoaded = true;
                _shared.ItemVisuals = ItemVisualCatalog.Load(_config.ClientDataPath);
                _shared.Enchants = EnchantCatalog.Load(_config.ClientDataPath);
                Console.WriteLine($"[item-glow] catalogs: visuals={_shared.ItemVisuals?.Count ?? 0}, " +
                                  $"enchants={_shared.Enchants?.Count ?? 0}");
            }
        }
    }

    public void BeginGlowFrame()
    {
        _glowPlacements.Clear();
        _carriedLights.Clear();
        _fishingPoleTips.Clear();
    }

    /// <summary>
    /// Unskinned attachment shader. It keeps WMO lighting, but uses its own
    /// vertex stage so clip positions retain character-scale precision at
    /// large world coordinates.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        lock (SharedGate)
        {
            _shared.Shader ??= MSUIClient.Engine.Shader.FromFiles(_gl,
                Path.Combine(shaderDir, "attached.vert"),
                Path.Combine(shaderDir, "character.frag"));
        }
    }

    // ── building the mount list ──────────────────────────────────────────────

    /// <summary>
    /// Which folder under Item\ObjectComponents holds a slot's models. Vanilla
    /// splits them by body part rather than by item type.
    /// </summary>
    private static string FolderFor(int inventoryType) => inventoryType switch
    {
        CharacterEquipment.Slot.Head => "Head",
        CharacterEquipment.Slot.Shoulders => "Shoulder",
        CharacterEquipment.Slot.Shield => "Shield",
        _ => "Weapon",
    };

    /// <summary>
    /// Where a slot's model hangs. A shield goes on the LEFT WRIST and not the
    /// palm, which is the sort of thing that looks like a transform bug when it
    /// is really a table lookup.
    /// </summary>
    private static int AttachmentFor(int inventoryType) => inventoryType switch
    {
        CharacterEquipment.Slot.Head => AttachHelm,
        CharacterEquipment.Slot.Shield => AttachLeftWrist,
        CharacterEquipment.Slot.OffHand => AttachHandLeft,
        _ => AttachHandRight,
    };

    public void Rebuild(CharacterEquipment equipment)
    {
        _mountSnapshot = null;
        _mounts.Clear();
        if (Shader is null) return;

        _resolutionErrors.Clear();
        BuildMounts(equipment, _mounts, _resolutionErrors);
        Console.WriteLine($"[attach] {_mounts.Count} model(s) mounted");
    }

    /// <summary>
    /// Drop unattended batch-only model/texture caches after the current specimen is no longer
    /// being drawn. Normal interactive rendering never calls this.
    /// </summary>
    public void ClearVariantCache()
    {
        _mountSnapshot = null;
        _mounts.Clear();
        lock (SharedGate)
        {
            foreach (ItemModel model in Models.Values.Where(model => model is not null)
                         .Select(model => model!).Distinct())
                model.Dispose();
            foreach (Texture texture in Textures.Values.Where(texture => texture is not null)
                         .Select(texture => texture!).Distinct())
                texture.Dispose();
            Models.Clear();
            Textures.Clear();
        }
    }

    public MountSet BuildMountSet(CharacterEquipment equipment)
    {
        var result = new MountSet();
        if (Shader is not null) BuildMounts(equipment, result.Items, result.Errors);
        Console.WriteLine($"[attach] {result.Count} model(s) mounted");
        return result;
    }

    private void BuildMounts(CharacterEquipment equipment, List<Mount> mounts, List<string> errors)
    {

        foreach (var piece in equipment.Pieces)
        {
            if (piece.Row is null || !piece.Row.HasModel) continue;

            string folder = FolderFor(piece.InventoryType);

            if (piece.InventoryType == CharacterEquipment.Slot.Shoulders)
            {
                // Shoulders are two files: ModelName1 is the left, ModelName2
                // the right, and both are needed.
                AddMount(mounts, piece.Row.ModelName1, piece.Row.ModelTexture1, folder,
                         AttachShoulderLeft, piece.Name + " (L)",
                         inspectionIdentity: EquipmentMountIdentity.FromPiece(piece), resolutionErrors: errors);
                AddMount(mounts, piece.Row.ModelName2, string.IsNullOrEmpty(piece.Row.ModelTexture2) ? piece.Row.ModelTexture1 : piece.Row.ModelTexture2, folder,
                         AttachShoulderRight, piece.Name + " (R)",
                         inspectionIdentity: EquipmentMountIdentity.FromPiece(piece), resolutionErrors: errors);
                continue;
            }

            int heldSlot = piece.EquipmentSlot switch { 15 => 0, 16 => 1, 17 => 2, _ => -1 };
            AddMount(mounts, piece.Row.ModelName1, piece.Row.ModelTexture1, folder,
                     AttachmentFor(piece.InventoryType), piece.Name, heldSlot,
                     piece.InventoryType, piece.Sheath, piece.DisplayId,
                     piece.EquipmentSlot, unchecked((int)piece.Row.ItemVisualId), piece.Enchants, resolutionErrors: errors);
        }
    }

    private void AddMount(List<Mount> mounts, string modelName, string textureName,
        string folder, int attachmentId, string label,
        int heldSlot = -1, int inventoryType = 0, byte itemSheath = 0,
        uint displayId = 0, int equipmentSlot = -1, int itemVisualId = 0,
        IReadOnlyList<uint>? enchants = null, EquipmentMountIdentity? inspectionIdentity = null, List<string>? resolutionErrors = null)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        ItemModel? model;
        try { model = ResolveModel(modelName, textureName, folder,
            attachmentId == AttachShoulderLeft ? "L" : attachmentId == AttachShoulderRight ? "R" : ""); }
        catch (InvalidDataException ex)
        {
            string error = $"shoulder-fit-resolution:{label}:{RaceGenderCode}:{ex.Message}";
            resolutionErrors?.Add(error);
            Console.Error.WriteLine("[attach] " + error);
            return;
        }
        if (model is null)
        {
            Console.WriteLine($"[attach] {label}: model '{modelName}' not found under {folder}");
            return;
        }

        mounts.Add(new Mount
        {
            Model = model, AttachmentId = attachmentId, Label = label,
            HeldSlot = heldSlot, InventoryType = inventoryType, ItemSheath = itemSheath,
            DisplayId = displayId, EquipmentSlot = equipmentSlot, ItemVisualId = itemVisualId,
            InspectionIdentity = inspectionIdentity ?? new EquipmentMountIdentity(displayId, equipmentSlot, inventoryType),
            Enchants = enchants?.ToArray() ?? [],
        });
        int stowedAttachment = heldSlot >= 0
            ? ResolveAttachment(mounts[^1], sheathState: 0)
            : attachmentId;
        Console.WriteLine($"[attach] {label}: {model.Path} on attachment {attachmentId}; " +
                          $"heldSlot={heldSlot};inventoryType={inventoryType};" +
                          $"itemSheath={itemSheath};stowedAttachment={stowedAttachment}");
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ItemDisplayInfo stores bare names like "Helm_Plate_A_01". The real file
    /// lives under Item\ObjectComponents\&lt;Folder&gt;\ and vanilla records the
    /// .mdx extension while the MPQ holds .m2 - the same swap MDDF needs. The
    /// candidates cover both, and the first hit is logged so the convention is
    /// learned rather than assumed.
    /// </summary>
    private ItemModel? ResolveModel(string modelName, string textureName, string folder, string side)
    {
        string key = ShoulderFitResolver.CacheKey(_config.ClientDataPath, folder, modelName, textureName, RaceGenderCode, side);
        if (Models.TryGetValue(key, out var cached)) return cached;

        string stem = modelName.Replace('/', '\\').TrimStart('\\');
        int dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];

        var candidates = new List<string>();

        // Helms first try the race/gender file, because for the Head folder
        // that IS the file and the bare name will simply not exist.
        if (folder == "Head")
        {
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.m2");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.M2");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.mdx");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}{RaceGenderCode}.m2");
        }

        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.m2");
        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.M2");
        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.mdx");
        candidates.Add($"{stem}.m2");

        byte[]? bytes = null;
        string found = "";
        foreach (string candidate in candidates)
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) { found = candidate; break; }
        }

        if (bytes is null)
        {
            Console.WriteLine($"[attach] not found, tried: {string.Join("  ", candidates)}");
            Models[key] = null;
            return null;
        }

        var m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid)
        {
            Models[key] = null;
            return null;
        }

        ShoulderFitSelection? selected = null;
        if (folder == "Shoulder")
        {
            string skinStem = textureName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? textureName[..^4] : textureName;
            selected = ShoulderFitResolver.Resolve(found, bytes, m2.Name, RaceGenderCode,
                $@"Item\ObjectComponents\Shoulder\{skinStem}.blp", side,
                path => AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, path));
            if (selected.ManifestPath is not null)
            {
                m2 = M2Reader.Parse(selected.Bytes);
                if (m2 is null || !m2.IsValid) throw new InvalidDataException("Declared shoulder-fit M2 cannot be parsed.");
                found = selected.Path;
                Console.WriteLine($"[attach] shoulder-fit body={RaceGenderCode};side={side};selected={found};default={selected.DefaultPath};sha256={selected.Sha256};manifestSha256={selected.ManifestSha256}");
            }
        }
        var model = BuildModel(m2, textureName, folder);
        if (model is not null)
        {
            model.Path = found;
            model.ShoulderFit = selected?.ManifestPath is not null ? selected : null;
            ClassifySteadyWarglaiveBlade(model);
        }
        Models[key] = model;
        return model;
    }

    private static void ClassifySteadyWarglaiveBlade(ItemModel model)
    {
        // Only the ArmorReflect stage and its opaque same-submesh base form the
        // energy blade. The handle/guard remains normally lit. This model-path
        // gate is deliberate: hundreds of unrelated weapons also use
        // ArmorReflect3 with the same topology.
        foreach (Batch batch in model.Batches)
            batch.SteadyWarglaiveBlade = batch.Source is not null &&
                AttachedItemMaterialLaw.IsSteadyWarglaiveBladeBatch(
                    model.Path, model.Source, batch.Source);
    }

    private unsafe ItemModel? BuildModel(M2Model m2, string textureName, string folder)
    {
        int vertexCount = m2.Vertices.Count;
        var vertices = new float[vertexCount * FloatsPerVertex];

        for (int i = 0; i < vertexCount; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.PosX; vertices[o + 1] = v.PosY; vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX; vertices[o + 4] = v.NormY; vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU; vertices[o + 7] = v.TexV;
            SpellMeshSkinningLaw.VertexSkin skin = SpellMeshSkinningLaw.Resolve(v);
            vertices[o + 8] = skin.Weights.X; vertices[o + 9] = skin.Weights.Y;
            vertices[o + 10] = skin.Weights.Z; vertices[o + 11] = skin.Weights.W;
            vertices[o + 12] = skin.Indices.X; vertices[o + 13] = skin.Indices.Y;
            vertices[o + 14] = skin.Indices.Z; vertices[o + 15] = skin.Indices.W;
            vertices[o + 16] = v.TexU1; vertices[o + 17] = v.TexV1;
        }

        var indices = m2.Indices.ToArray();
        if (indices.Length < 3) return null;

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        _gl.EnableVertexAttribArray(5);
        _gl.VertexAttribPointer(5, 2, VertexAttribPointerType.Float, false, stride, (void*)(16 * sizeof(float)));

        _gl.BindVertexArray(0);

        var model = new ItemModel
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            Source = m2,
            UsesCameraFacingPalette = AttachedItemBillboardLaw.UsesCameraFacingPalette(m2),
        };
        model.Attach(_gl);

        // An item M2's own texture table is usually a single type-2 slot with
        // no filename - the name comes from ItemDisplayInfo instead.
        var texture = ResolveTexture(textureName, folder);

        // Preserve every authored visible pass. Weapon models commonly overlay an additive
        // specular/environment layer on the same submesh as the opaque base. Depth writes are
        // disabled for that blended layer in the draw path, so the passes do not z-fight.
        var visibleBatches = m2.Batches
            .Select((batch, index) => (batch, index))
            .Where(x => !m2.IsBatchConstantInvisible(x.batch))
            .OrderBy(x => x.index)
            .ToList();

        foreach (var entry in visibleBatches)
        {
            var batch = entry.batch;
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            var submesh = m2.Submeshes[batch.SubmeshIndex];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > indices.Length) continue;

            // ItemDisplayInfo's texture replaces only an empty Type-2 unit. Resolve every
            // authored unit independently: Warglaive wave batches use two copies of their
            // effect sheet with different UV sets, and dropping unit 1 drops the moving lines.
            Texture? ResolveBatchTexture(int unit, bool allowDisplayFallback)
            {
                int lookup = batch.TextureIndex + unit;
                if (lookup < m2.TextureLookup.Count)
                {
                    int texIdx = m2.TextureLookup[lookup];
                    if (texIdx >= 0 && texIdx < m2.Textures.Count)
                    {
                        M2TextureRef textureRef = m2.Textures[texIdx];
                        if (textureRef.Filename.Length > 0)
                            return ResolveTexturePath(textureRef.Filename);
                        if (textureRef.Type == 2)
                            return texture;
                        return null;
                    }
                }
                return allowDisplayFallback ? texture : null;
            }

            Texture? slot = ResolveBatchTexture(0, allowDisplayFallback: true);
            Texture? slot2 = batch.TextureCount > 1
                ? ResolveBatchTexture(1, allowDisplayFallback: false)
                : null;
            int firstCoordinate = m2.GetTextureCoordinateForBatchUnit(batch, 0);
            int secondCoordinate = m2.GetTextureCoordinateForBatchUnit(batch, 1);

            var renderFlags = batch.MaterialIndex < m2.RenderFlags.Count
                ? m2.RenderFlags[batch.MaterialIndex]
                : null;

            model.Batches.Add(new Batch
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                Texture = slot,
                Texture2 = slot2,
                FirstUsesUv1 = firstCoordinate == 1,
                SecondUsesUv1 = secondCoordinate == 1,
                SecondEnvironmentMapped = m2.UsesEnvironmentMapForBatchUnit(batch, 1),
                SteadyModulatedGlow = batch.TextureCount == 2 &&
                    (renderFlags?.BlendingMode ?? 0) == 4 &&
                    firstCoordinate == 0 && secondCoordinate == 1,
                TwoSided = renderFlags?.TwoSided ?? false,
                BlendMode = renderFlags?.BlendingMode ?? 0,
                NoZWrite = renderFlags?.NoZWrite ?? false,
                NoZTest = renderFlags?.NoZTest ?? false,
                Unlit = renderFlags?.Unlit ?? false,
                FogPolicy = AttachedItemMaterialLaw.FogPolicy(
                    renderFlags?.BlendingMode ?? 0, renderFlags?.Unfogged ?? false),
                EnvironmentMapped = m2.UsesEnvironmentMapForBatch(batch),
                Source = batch,
            });
        }
        // Only a truly batch-less file needs the raw-geometry fallback. A model whose authored
        // batches all combine to alpha zero is intentionally meshless (often an emitter helper);
        // re-adding its whole index buffer here would undo the visibility law above.
        if (model.Batches.Count == 0 && m2.Batches.Count == 0)
            model.Batches.Add(new Batch { IndexStart = 0, IndexCount = (uint)indices.Length, Texture = texture, TwoSided = true });

        return model;
    }

    private Texture? ResolveTexture(string textureName, string folder)
    {
        if (string.IsNullOrWhiteSpace(textureName)) return null;

        string stem = textureName.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        string[] candidates =
        [
            $@"Item\ObjectComponents\{folder}\{stem}.blp",
            $"{stem}.blp",
        ];

        foreach (string candidate in candidates)
        {
            var made = ResolveTexturePath(candidate);
            if (made is not null) return made;
        }

        Console.WriteLine($"[attach] texture '{textureName}' not found, tried: {string.Join("  ", candidates)}");
        return null;
    }

    private Texture? ResolveTexturePath(string blpPath)
    {
        string key = _config.ClientDataPath + "|" + blpPath;
        if (Textures.TryGetValue(key, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null) { Textures[key] = null; return null; }

        var (bgra, w, h) = decoded.Value;

        // Same 1-bit alpha guard as everywhere else. See CharacterRenderer.
        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
        if (maxAlpha == 1)
            for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) bgra[i] = 255;

        var texture = Texture.From2D(_gl, bgra, w, h);
        Textures[key] = texture;
        return texture;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draw every mounted item. <paramref name="skin"/> is the animator's
    /// per-bone skinning matrices, which is what makes a sword swing with the
    /// hand instead of hovering where the hand started.
    /// </summary>
    public unsafe void Render(Camera camera, Matrix4x4 instance,
                              M2Model? character, Matrix4x4[] skin, ulong ownerGuid = 0,
                              float modelTime = 0f)
        => RenderMounts(camera, instance, character, skin, _mounts, SheathState, ownerGuid,
            modelTime);

    public unsafe void Render(Camera camera, Matrix4x4 instance,
        M2Model? character, Matrix4x4[] skin, MountSet mounts, byte sheathState,
        ulong ownerGuid = 0, float modelTime = 0f)
        => RenderMounts(camera, instance, character, skin, mounts.Items, sheathState, ownerGuid,
            modelTime);

    private unsafe void RenderMounts(Camera camera, Matrix4x4 instance,
        M2Model? character, Matrix4x4[] skin, IReadOnlyList<Mount> mounts,
        byte sheathState, ulong ownerGuid, float modelTime)
    {
        DrawnLastFrame = 0;
        Shader? shader = Shader;
        if (!Enabled || shader is null || mounts.Count == 0 || character is null) return;

        shader.Use();
        shader.Set("uCameraPos", Vector3.Zero);
        shader.Set("uView", camera.RelativeView);
        shader.Set("uSunDirection", SunDirection);
        shader.Set("uSunColor", SunColor);
        shader.Set("uSunIntensity", SunIntensity);
        shader.Set("uAmbientColor", AmbientColor);
        shader.Set("uAmbientIntensity", AmbientIntensity);
        shader.Set("uInteriorLight", InteriorLight);
        shader.Set("uBakedLightScale", BakedLightScale);
        shader.Set("uShadowWrap", ShadowSoftness);
        shader.Set("uFogStart", FogStart);
        shader.Set("uFogEnd", FogEnd);
        shader.Set("uFogColor", FogColor);
        shader.Set("uTexture", 0);
        shader.Set("uTexture2", 1);
        float bodyAlpha = Math.Clamp(BodyAlpha, 0f, 1f);
        shader.Set("uBodyAlpha", bodyAlpha);
        shader.Set("uBodyTint", BodyTint);
        CarriedLightFrame.Upload(shader, camera.Position);
        bool bodyTranslucent = bodyAlpha < 1f - AuraVisualLaw.AlphaSettledEpsilon;

        // Item geometry is submitted camera-relative for precision. The shared effect pipeline
        // consumes absolute world transforms and performs its own camera rebase, so restore the
        // camera translation before publishing glow roots.
        Matrix4x4 worldInstance = instance;
        worldInstance.M41 += camera.Position.X;
        worldInstance.M42 += camera.Position.Y;
        worldInstance.M43 += camera.Position.Z;

        foreach (Mount mount in mounts)
        {
            if (!mount.Visible) continue;
            int attachmentId = ResolveAttachment(mount, sheathState);
            if (attachmentId < 0)
            {
                InspectionPlacement?.Invoke(mount, attachmentId, -1, null, "hidden-by-sheath");
                continue;
            }
            M2Attachment? attachment = FindAttachment(character, attachmentId);
            if (attachment is null)
            {
                InspectionPlacement?.Invoke(mount, attachmentId, -1, null, "missing-attachment");
                ReportHeldPlacement(character, mount, sheathState, attachmentId, null,
                    skin.Length, null);
                continue;
            }
            int bone = (int)attachment.BoneIndex;
            Matrix4x4 boneMatrix = bone >= 0 && bone < skin.Length
                ? skin[bone] : Matrix4x4.Identity;
            Matrix4x4 itemRoot = Matrix4x4.CreateTranslation(attachment.Position) *
                                 boneMatrix * worldInstance;
            ReportHeldPlacement(character, mount, sheathState, attachmentId, attachment,
                skin.Length, itemRoot);
            InspectionPlacement?.Invoke(mount, attachmentId, bone, itemRoot,
                bone >= 0 && bone < skin.Length ? "resolved" : "bone-out-of-range");
            AppendCarriedLights(mount, itemRoot);
            AppendItemModelEffects(mount, itemRoot);
            if (mount.HeldSlot >= 0) AppendGlowPlacements(mount, itemRoot);
            if (ownerGuid != 0 && mount.HeldSlot == 0 &&
                attachmentId == AttachHandRight)
                AppendFishingPoleTip(ownerGuid, mount, itemRoot);
        }

        bool cullingOn = true;
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool depthTestOn = hadDepthTest;

        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;
            if (transparentPass)
            {
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.Blend);
            }

            foreach (var mount in mounts)
            {
                if (!mount.Visible) continue;

                int attachmentId = ResolveAttachment(mount, sheathState);
                if (attachmentId < 0) continue;
                var attachment = FindAttachment(character, attachmentId);
                if (attachment is null) continue;

                int bone = (int)attachment.BoneIndex;
                var boneMatrix = bone >= 0 && bone < skin.Length ? skin[bone] : Matrix4x4.Identity;

                var model = Matrix4x4.CreateTranslation(attachment.Position) * boneMatrix * instance;
                shader.Set("uModel", model);
                shader.Set("uModelViewProjection", model * camera.RelativeViewProjection);
                Matrix4x4 worldModel = Matrix4x4.CreateTranslation(attachment.Position) *
                    boneMatrix * worldInstance;
                int itemBoneCount = AttachedItemBillboardLaw.PreparePalette(
                    mount.Model.Source, mount.Model.UsesCameraFacingPalette, worldModel,
                    camera.Position, camera.Forward, _itemSkin);
                if (itemBoneCount > 0)
                {
                    M2Animator.Pack(_itemSkin, itemBoneCount, _packedItemSkin);
                    shader.SetVec4Array("uBones", _packedItemSkin, itemBoneCount * 3);
                }
                shader.Set("uBoneCount", itemBoneCount);

                _gl.BindVertexArray(mount.Model.Vao);

                foreach (var batch in mount.Model.Batches)
                {
                    AttachedItemMaterialLaw.Sample material = AttachedItemMaterialLaw.At(
                        mount.Model.Source, batch.Source, modelTime);
                    if (!material.Visible) continue;
                    if ((bodyTranslucent || batch.Transparent || material.Translucent) !=
                        transparentPass) continue;

                    if (batch.NoZTest && depthTestOn)
                    {
                        _gl.Disable(EnableCap.DepthTest);
                        depthTestOn = false;
                    }
                    else if (!batch.NoZTest && !depthTestOn)
                    {
                        _gl.Enable(EnableCap.DepthTest);
                        depthTestOn = true;
                    }

                    if (batch.TwoSided && cullingOn) { _gl.Disable(EnableCap.CullFace); cullingOn = false; }
                    else if (!batch.TwoSided && !cullingOn) { _gl.Enable(EnableCap.CullFace); cullingOn = true; }

                    if (batch.Texture is not null)
                    {
                        batch.Texture.Bind(0);
                        shader.Set("uHasTexture", 1);
                        shader.Set("uAlphaCutoff", batch.BlendMode == 1 ? AlphaCutoff : 0f);
                    }
                    else
                    {
                        shader.Set("uHasTexture", 0);
                        shader.Set("uAlphaCutoff", 0f);
                    }
                    if (batch.Texture2 is not null)
                    {
                        batch.Texture2.Bind(1);
                        shader.Set("uHasTexture2", 1);
                    }
                    else
                    {
                        shader.Set("uHasTexture2", 0);
                    }
                    shader.Set("uSteadyModulatedGlow", batch.SteadyModulatedGlow ? 1 : 0);

                    if (transparentPass) ApplyBlendMode(batch.BlendMode);

                    shader.Set("uBodyTint", BodyTint * material.Tint);
                    shader.Set("uBodyAlpha", bodyAlpha * material.Alpha);
                    shader.Set("uUvOffset", material.UvOffset);
                    shader.Set("uUvOffset2", batch.Source is null
                        ? Vector2.Zero
                        : AttachedItemMaterialLaw.UvOffsetAt(
                            mount.Model.Source, batch.Source, 1, modelTime));
                    shader.Set("uUvSet", batch.FirstUsesUv1 ? 1 : 0);
                    shader.Set("uUvSet2", batch.SecondUsesUv1 ? 1 : 0);
                    shader.Set("uEnvironmentMap", batch.EnvironmentMapped ? 1 : 0);
                    shader.Set("uEnvironmentMap2", batch.SecondEnvironmentMapped ? 1 : 0);
                    // Generic environment/blend passes honor the authored UNLIT bit. The known
                    // Warglaive energy submesh is the narrow exception: keeping its base and line
                    // pass stable prevents the hand animation from becoming a whole-blade pulse.
                    shader.Set("uUnlit", (batch.Unlit || batch.SteadyWarglaiveBlade) ? 1 : 0);
                    shader.Set("uFogPolicy", batch.FogPolicy);

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                    InspectionMeshDraw?.Invoke(mount, batch, worldModel, _itemSkin, itemBoneCount);
                }

                if (!transparentPass) DrawnLastFrame++;
            }

            if (transparentPass)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        if (hadDepthTest && !depthTestOn) _gl.Enable(EnableCap.DepthTest);
        else if (!hadDepthTest && depthTestOn) _gl.Disable(EnableCap.DepthTest);
        _gl.BindVertexArray(0);
    }

    private void AppendGlowPlacements(Mount mount, Matrix4x4 itemRoot)
    {
        int visual = ItemGlowLaw.EffectiveVisual(_shared.ItemVisuals, _shared.Enchants,
            mount.ItemVisualId, mount.Enchants);
        string?[]? effects = _shared.ItemVisuals?.Effects(visual);
        if (effects is null) return;
        for (int slot = 0; slot < effects.Length; slot++)
        {
            string? path = effects[slot];
            if (path is null || ItemGlowLaw.AttachmentPosition(mount.Model.Source, slot) is not
                Vector3 local) continue;
            Matrix4x4 transform = Matrix4x4.CreateTranslation(local) * itemRoot;
            string key = $"{GlowOwnerKey}:{mount.EquipmentSlot}:{mount.DisplayId}:{slot}";
            _glowPlacements.Add(new ItemGlowPlacement(key, path, transform));
        }
    }

    /// <summary>
    /// Equipped M2s can author their own particle/ribbon effects (torch flames and shoulder
    /// sparkles). Publish the item root through the shared effect pipeline, but suppress its mesh:
    /// the ordinary attached-item pass already drew that geometry.
    /// </summary>
    private void AppendItemModelEffects(Mount mount, Matrix4x4 itemRoot)
    {
        M2Model source = mount.Model.Source;
        if (source.ParticleEmitters.Count == 0 && source.RibbonEmitters.Count == 0) return;
        string key = $"{GlowOwnerKey}:{mount.EquipmentSlot}:{mount.DisplayId}:" +
            $"item-effects:{mount.Model.Path}";
        _glowPlacements.Add(new ItemGlowPlacement(
            key, mount.Model.Path, itemRoot, RenderMesh: false));
    }

    private void AppendFishingPoleTip(ulong ownerGuid, Mount mount, Matrix4x4 itemRoot)
    {
        M2EventMarker? marker = mount.Model.Source.Events.FirstOrDefault(e =>
            string.Equals(e.Identifier, "$CCH", StringComparison.Ordinal));
        if (marker is null) return;
        _fishingPoleTips.Add(new FishingPoleTipPlacement(ownerGuid,
            Vector3.Transform(marker.Position, itemRoot)));
    }

    private void AppendCarriedLights(Mount mount, Matrix4x4 itemRoot)
    {
        for (int index = 0; index < mount.Model.Source.Lights.Count; index++)
        {
            M2Light light = mount.Model.Source.Lights[index];
            if (!light.Casts) continue;
            Vector3 position = Vector3.Transform(light.Position, itemRoot);
            Vector3 color = light.DiffuseColor * MathF.Max(0f, light.DiffuseIntensity);
            string key = $"{GlowOwnerKey}:{mount.EquipmentSlot}:{mount.DisplayId}:light:{index}";
            _carriedLights.Add(new CarriedLightPlacement(key, position, color));
        }
    }

    internal static int ResolveAttachment(Mount mount, byte sheathState)
    {
        if (mount.HeldSlot < 0) return mount.AttachmentId;

        // Ranged slot: bows use the left hand, all other ranged families the
        // right, and vanilla detaches the model entirely while it is stowed.
        if (mount.HeldSlot == 2)
            return sheathState == 2
                ? mount.InventoryType == 15 ? AttachHandLeft : AttachHandRight
                : -1;

        if (sheathState == 1)
            return mount.HeldSlot == 0 ? AttachHandRight
                : mount.InventoryType == CharacterEquipment.Slot.Shield ? AttachLeftWrist
                : AttachHandLeft;

        return mount.ItemSheath switch
        {
            1 => mount.HeldSlot == 0 ? AttachBackRight : AttachBackLeft,
            2 => mount.HeldSlot == 0 ? AttachBackLowerMain : AttachBackLowerOff,
            3 => mount.HeldSlot == 0 ? AttachHipMain : AttachHipOff,
            4 => AttachShieldBack,
            // 5/6 are the hip pair and 7 the shield in the client's sheathe enum. They were
            // falling through to -1, and -1 does not mean "put it somewhere else" here, it
            // means DRAW NOTHING: sheathing simply deleted the weapon. Sheathe byte 7 is
            // present in real item data (four sightings across the captured dumps).
            5 or 6 => mount.HeldSlot == 0 ? AttachHipMain : AttachHipOff,
            7 => AttachShieldBack,
            // Byte 0 or anything unmapped. An equipped weapon must stay on the body: a data
            // gap is allowed to put it in the wrong place, never to make it disappear. The
            // fallback is the over-the-shoulder pair, the commonest stow, and it is announced
            // once per item so the real value shows up as evidence instead of a vanished model.
            _ => ReportUnmappedSheath(mount),
        };
    }

    private static readonly HashSet<string> _reportedUnmappedSheaths = [];

    private static int ReportUnmappedSheath(Mount mount)
    {
        string key = $"{mount.DisplayId}:{mount.ItemSheath}";
        lock (_reportedUnmappedSheaths)
            if (_reportedUnmappedSheaths.Add(key))
                Console.WriteLine($"[attach] UNMAPPED sheathe byte {mount.ItemSheath} on " +
                    $"{mount.Label} (display {mount.DisplayId}, inv {mount.InventoryType}); " +
                    "stowing over the shoulder instead of hiding it");
        return mount.HeldSlot == 0 ? AttachBackRight : AttachBackLeft;
    }

    private void ApplyBlendMode(int mode)
    {
        switch (mode)
        {
            case 3:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 4:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 5:
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case 6:
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
                break;
            default:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    private static M2Attachment? FindAttachment(M2Model model, int id)
    {
        foreach (var attachment in model.Attachments)
            if (attachment.Id == (uint)id) return attachment;
        return null;
    }

    private void ReportHeldPlacement(M2Model character, Mount mount, byte sheathState,
        int attachmentId, M2Attachment? attachment, int skinCount, Matrix4x4? itemRoot)
    {
        if (mount.HeldSlot < 0) return;
        string key = $"{mount.EquipmentSlot}:{mount.DisplayId}:{sheathState}:{attachmentId}";
        if (!_reportedHeldPlacements.Add(key)) return;
        if (attachment is null)
        {
            string offered = string.Join(',', character.Attachments.Select(point => point.Id));
            Console.WriteLine($"[attach-place] {mount.Label}: state={sheathState};" +
                $"resolved={attachmentId};MISSING;offered={offered}");
            return;
        }

        int bone = (int)attachment.BoneIndex;
        string root = itemRoot is Matrix4x4 matrix
            ? $"({matrix.M41:R},{matrix.M42:R},{matrix.M43:R})"
            : "pending";
        Console.WriteLine($"[attach-place] {mount.Label}: state={sheathState};" +
            $"resolved={attachmentId};bone={bone};boneValid={bone >= 0 && bone < skinCount};" +
            $"position=({attachment.Position.X:R},{attachment.Position.Y:R}," +
            $"{attachment.Position.Z:R});worldRoot={root};vertices={mount.Model.Source.Vertices.Count};" +
            $"batches={mount.Model.Batches.Count};visible={mount.Visible}");
    }

    /// <summary>Print what the character model actually offers, without truncating later IDs.</summary>
    public static void ReportAttachments(M2Model model)
    {
        Console.WriteLine($"[attach] character offers {model.Attachments.Count} point(s)");
        foreach (M2Attachment[] chunk in model.Attachments.Chunk(10))
            Console.WriteLine("[attach] points: " + string.Join(" ",
                chunk.Select(a => $"{a.Id}(bone {a.BoneIndex})")));
    }

    public void Dispose()
    {
        _mountSnapshot = null;
        _mounts.Clear();
        lock (SharedGate)
        {
            if (_disposed) return;
            _disposed = true;
            _shared.Owners--;
            if (_shared.Owners != 0) return;
            foreach (var model in _shared.Models.Values) model?.Dispose();
            foreach (var texture in _shared.Textures.Values) texture?.Dispose();
            _shared.Models.Clear();
            _shared.Textures.Clear();
            _shared.Shader?.Dispose();
            _shared.Shader = null;
            if (ReferenceEquals(s_shared, _shared)) s_shared = null;
        }
    }
}
