using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

public sealed record VariantBatchOptions(
    string Axis,
    string? OutputDirectory,
    string? ListFile,
    int? Limit,
    string? DiffFile,
    bool Unmasked,
    bool Exhaustive);

public static partial class Program
{
    private static bool TryParseVariantBatchArgs(string[] args,
        out VariantBatchOptions? options, out string? configPath, out string? error)
    {
        options = null;
        configPath = null;
        error = null;
        if (!args.Contains("--variant-batch", StringComparer.OrdinalIgnoreCase)) return true;

        string axis = "npc-extras";
        string? output = null, list = null, diff = null;
        int? limit = null;
        bool unmasked = false, exhaustive = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--variant-batch", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--unmasked", StringComparison.OrdinalIgnoreCase))
            {
                unmasked = true;
                continue;
            }
            if (arg.Equals("--exhaustive", StringComparison.OrdinalIgnoreCase))
            {
                exhaustive = true;
                continue;
            }
            if (arg is "--axis" or "--out" or "--list" or "--limit" or "--diff")
            {
                if (++i >= args.Length)
                {
                    error = $"missing value for {arg}";
                    return false;
                }
                string value = args[i];
                if (arg == "--axis") axis = value.ToLowerInvariant();
                else if (arg == "--out") output = value;
                else if (arg == "--list") list = value;
                else if (arg == "--diff") diff = value;
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                             out int parsed) || parsed <= 0)
                {
                    error = "--limit must be a positive integer";
                    return false;
                }
                else limit = parsed;
                continue;
            }
            if (arg.StartsWith('-'))
            {
                error = $"unknown option {arg}";
                return false;
            }
            if (configPath is not null)
            {
                error = $"unexpected argument {arg}";
                return false;
            }
            configPath = arg;
        }

        if (axis is not ("npc-extras" or "items" or "players" or "equipment"))
        {
            error = $"axis '{axis}' is not available at the items review checkpoint";
            return false;
        }
        options = new VariantBatchOptions(axis, output, list, limit, diff, unmasked, exhaustive);
        return true;
    }

    private static void PrintVariantBatchUsage() => Console.Error.WriteLine(
        "usage: MSUIClient [config.json] --variant-batch [--axis npc-extras|items|players|equipment] " +
        "[--out <dir>] [--list <file>] [--limit <n>] [--diff <verdicts.csv>] " +
        "[--unmasked] [--exhaustive]");
}

public sealed partial class GameLoop
{
    private const int VariantCacheChunk = 128;
    private const string VariantCsvHeader =
        "rowKey,key,axis,displayId,extraId,batchIndex,geosetId,region,textureType," +
        "resolvedTexture,effectiveTexture,supplier,customContent,demandedTexture," +
        "demandedSupplier,missingDemandedTexture,predicted7C2Texture,protocolRow," +
        "modelPath,modelSupplier,race,sex,skin,face,hairStyle,hairColor,facialHair," +
        "equipment,geosetsChosen,bakeTexture,bakeSupplier,helmDisplayId,helmSuffix," +
        "helmModel,helmSupplier,shoulderDisplayId,shoulderModels,shoulderSuppliers," +
        "attachmentStatus,capeTexture,charSectionsDupKey,charSectionsWinnerRow," +
        "outcome,subjectPx,meanLuma,elapsedMs,note";

    private readonly VariantBatchOptions? _variantBatchOptions;
    private readonly List<VariantSpecimen> _variantSpecimens = [];
    private readonly List<VariantSpecimenResult> _variantSpecimenResults = [];
    private readonly List<VariantCsvRow> _variantRows = [];
    private readonly List<(string Key, byte[] Rgba, int Width, int Height)> _variantSheetCells = [];
    private readonly HashSet<string> _variantExpectedBlank = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _variantKnownBlank = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _variantItemKnownIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _variantItemKnownIssueBuckets =
        new(StringComparer.OrdinalIgnoreCase);
    private string _variantOutputDirectory = "";
    private int _variantIndex;
    private int _variantSheetIndex;
    private bool _variantFinished;
    private Stopwatch? _variantClock;
    private ItemDisplayTable? _variantItemDisplay;
    private CharSectionsTable? _variantPlayerCharSections;
    private string _variantPlayerModelKey = "";
    private string _variantItemDbcSupplier = "NONE";
    private readonly Dictionary<string, string> _variantSupplierCache =
        new(StringComparer.OrdinalIgnoreCase);
    public int VariantBatchExitCode { get; private set; } = 1;

    private readonly record struct VariantSpecimen(
        string Key, uint ExtraId, int DisplayId, string ModelPath, string? SkipNote,
        string Kind = "", int InventoryType = 0, byte PlayerRace = 0, byte PlayerSex = 0,
        int PlayerSkin = 0, int PlayerFace = 0, int PlayerHairStyle = 0,
        int PlayerHairColor = 0, int PlayerFacialHair = 0, string PlayerAxis = "");

    private readonly record struct VariantSpecimenResult(
        VariantSpecimen Specimen, PortraitOutcome Outcome, int SubjectPx,
        double MeanLuma, double ElapsedMs, string Note);

    private readonly record struct VariantCsvRow(
        string RowKey,
        string Key,
        string Axis,
        int DisplayId,
        uint ExtraId,
        int BatchIndex,
        int GeosetId,
        string Region,
        uint TextureType,
        string ResolvedTexture,
        string EffectiveTexture,
        string Supplier,
        bool CustomContent,
        string DemandedTexture,
        string DemandedSupplier,
        bool MissingDemandedTexture,
        string Predicted7C2Texture,
        string ProtocolRow,
        string ModelPath,
        string ModelSupplier,
        byte Race,
        byte Sex,
        uint Skin,
        uint Face,
        byte HairStyle,
        uint HairColor,
        byte FacialHair,
        string Equipment,
        string GeosetsChosen,
        string BakeTexture,
        string BakeSupplier,
        uint HelmDisplayId,
        string HelmSuffix,
        string HelmModel,
        string HelmSupplier,
        uint ShoulderDisplayId,
        string ShoulderModels,
        string ShoulderSuppliers,
        string AttachmentStatus,
        string CapeTexture,
        int CharSectionsDupKey,
        string CharSectionsWinnerRow,
        PortraitOutcome Outcome,
        int SubjectPx,
        double MeanLuma,
        double ElapsedMs,
        string Note)
    {
        public string CsvLine => string.Join(',',
            Csv(RowKey), Csv(Key), Csv(Axis), DisplayId, ExtraId, BatchIndex, GeosetId,
            Csv(Region), TextureType == uint.MaxValue ? "NONE" : TextureType,
            Csv(ResolvedTexture), Csv(EffectiveTexture), Csv(Supplier),
            CustomContent.ToString().ToLowerInvariant(), Csv(DemandedTexture),
            Csv(DemandedSupplier), MissingDemandedTexture.ToString().ToLowerInvariant(),
            Csv(Predicted7C2Texture), Csv(ProtocolRow), Csv(ModelPath), Csv(ModelSupplier),
            Race, Sex, Skin, Face, HairStyle, HairColor, FacialHair, Csv(Equipment),
            Csv(GeosetsChosen), Csv(BakeTexture), Csv(BakeSupplier), HelmDisplayId,
            Csv(HelmSuffix), Csv(HelmModel), Csv(HelmSupplier), ShoulderDisplayId,
            Csv(ShoulderModels), Csv(ShoulderSuppliers), Csv(AttachmentStatus),
            Csv(CapeTexture), CharSectionsDupKey, Csv(CharSectionsWinnerRow),
            Outcome, SubjectPx, F(MeanLuma), F(ElapsedMs), Csv(Note));

        private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        private static string Csv(object value) => Convert.ToString(value, CultureInfo.InvariantCulture)!
            .Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
    }

    private void InitVariantBatch(GL gl)
    {
        try
        {
            _mpq = new MpqMount(_config.ClientDataPath);
            AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;
            _portraitOverrides = PortraitOverrideStore.Load(_config.RepoRoot);

            VariantBatchOptions options = _variantBatchOptions!;
            if (options.Axis == "npc-extras")
            {
                _creatures = new CreatureRenderer(gl, _mpq, _config);
                _batchPortraitTarget = new PortraitRenderTarget(gl);
            }
            else
            {
                _batchPortraitTarget = new PortraitRenderTarget(gl, 466, 448);
                _character = new CharacterRenderer(gl, _config, null, null);
                string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(shaderDir, "character.vert")))
                    shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _character.LoadShaders(shaderDir);
                if (!_character.Load("Human", "Male"))
                    throw new InvalidDataException("HumanMale item specimen model unavailable");
                _character.BindPose = false;
                _character.FrozenStandPose = true;
                if (options.Axis == "players")
                {
                    byte[] sectionBytes = _mpq.ReadFile(CharSectionsTable.MpqPath)
                        ?? throw new InvalidDataException(CharSectionsTable.MpqPath);
                    _variantPlayerCharSections = CharSectionsTable.Parse(sectionBytes)
                        ?? throw new InvalidDataException("CharSections parse failed");
                    _variantPlayerModelKey = "1:0";
                }
            }
            _variantOutputDirectory = ResolveBatchPath(options.OutputDirectory ??
                Path.Combine("variant-batch", DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), options.Axis));
            Directory.CreateDirectory(_variantOutputDirectory);
            if (options.Axis == "npc-extras") BuildNpcExtraSpecimens(options);
            else if (options.Axis == "items") BuildItemSpecimens(options);
            else if (options.Axis == "players") BuildPlayerSpecimens(options);
            if (options.Axis == "equipment") { InitEquipmentCapture(gl); return; }
            LoadVariantBlankLists();
            _variantClock = Stopwatch.StartNew();
            Console.WriteLine($"[variant-batch] axis={options.Axis} ready: " +
                              $"{_variantSpecimens.Count} specimen(s), " +
                              $"out={_variantOutputDirectory}, masked={!options.Unmasked}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[variant-batch] initialization failed: {exception}");
            FinishVariantBatch(incomplete: true, error: exception.Message);
        }
    }

    private void BuildNpcExtraSpecimens(VariantBatchOptions options)
    {
        byte[] displayBytes = _mpq!.ReadFile(CreatureDisplayInfoTable.MpqPath)
            ?? throw new InvalidDataException(CreatureDisplayInfoTable.MpqPath);
        byte[] modelBytes = _mpq.ReadFile(CreatureModelDataTable.MpqPath)
            ?? throw new InvalidDataException(CreatureModelDataTable.MpqPath);
        byte[] extraBytes = _mpq.ReadFile(CreatureDisplayExtraTable.MpqPath)
            ?? throw new InvalidDataException(CreatureDisplayExtraTable.MpqPath);
        CreatureDisplayInfoTable displays = CreatureDisplayInfoTable.Parse(displayBytes)
            ?? throw new InvalidDataException("CreatureDisplayInfo parse failed");
        CreatureModelDataTable models = CreatureModelDataTable.Parse(modelBytes)
            ?? throw new InvalidDataException("CreatureModelData parse failed");
        CreatureDisplayExtraTable extras = CreatureDisplayExtraTable.Parse(extraBytes)
            ?? throw new InvalidDataException("CreatureDisplayInfoExtra parse failed");
        var resolver = new CreatureModelResolver(displays, models, extras);
        Dictionary<uint, int[]> byExtra = displays.All
            .Where(row => row.ExtendedDisplayId != 0)
            .GroupBy(row => row.ExtendedDisplayId)
            .ToDictionary(group => group.Key,
                group => group.Select(row => (int)row.Id).OrderBy(id => id).ToArray());

        VariantSpecimen Build(uint extraId, int? requestedDisplay = null)
        {
            int[] candidates = byExtra.GetValueOrDefault(extraId, []);
            int displayId = requestedDisplay is { } requested && candidates.Contains(requested)
                ? requested
                : extraId == 675 && candidates.Contains(2072) ? 2072
                : extraId == 54 && candidates.Contains(3340) ? 3340
                : candidates.FirstOrDefault();
            string key = $"npc-extra:{extraId}:display:{displayId}";
            if (displayId == 0)
                return new VariantSpecimen(key, extraId, 0, "", "no-display-row");
            return resolver.TryResolve(displayId, out CreatureModelInfo info)
                ? new VariantSpecimen(key, extraId, displayId, info.ModelPath, null)
                : new VariantSpecimen(key, extraId, displayId, "", "display-resolve-failed");
        }

        if (options.ListFile is null)
        {
            _variantSpecimens.AddRange(extras.All.OrderBy(row => row.Id)
                .Select(row => Build(row.Id)));
        }
        else
        {
            string listPath = ResolveBatchPath(options.ListFile);
            foreach (string sourceLine in File.ReadLines(listPath))
            {
                string line = sourceLine.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(':');
                if (parts.Length == 2 && parts[0].Equals("npc-extra", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint extraId))
                {
                    _variantSpecimens.Add(extras.Find(extraId) is null
                        ? new VariantSpecimen(line, extraId, 0, "", "extra-not-found")
                        : Build(extraId));
                }
                else if (parts.Length == 4 &&
                    parts[0].Equals("npc-extra", StringComparison.OrdinalIgnoreCase) &&
                    parts[2].Equals("display", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out extraId) &&
                    int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int displayId))
                {
                    _variantSpecimens.Add(extras.Find(extraId) is null
                        ? new VariantSpecimen(line, extraId, displayId, "", "extra-not-found")
                        : Build(extraId, displayId));
                }
                else if (parts.Length == 2 &&
                    parts[0].Equals("display", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out displayId) &&
                    displays.Find((uint)displayId) is { ExtendedDisplayId: not 0 } display)
                {
                    _variantSpecimens.Add(Build(display.ExtendedDisplayId, displayId));
                }
                else
                {
                    _variantSpecimens.Add(new VariantSpecimen(line, 0, 0, "", "invalid-list-entry"));
                }
            }
        }

        if (options.Limit is { } limit && _variantSpecimens.Count > limit)
            _variantSpecimens.RemoveRange(limit, _variantSpecimens.Count - limit);
    }

    private void BuildItemSpecimens(VariantBatchOptions options)
    {
        (byte[] bytes, string supplier) = _mpq!.ReadFileWithSupplier(ItemDisplayTable.MpqPath)
            ?? throw new InvalidDataException(ItemDisplayTable.MpqPath);
        _variantItemDbcSupplier = supplier.Length == 0 ? "NONE" : supplier;
        _variantItemDisplay = ItemDisplayTable.Parse(bytes) ??
            throw new InvalidDataException("ItemDisplayInfo parse failed");

        static bool IsHelm(ItemDisplayRow row) =>
            row.HelmetGeosetVis1 != 0 || row.HelmetGeosetVis2 != 0 ||
            row.ModelName1.Contains("helm", StringComparison.OrdinalIgnoreCase) ||
            row.ModelName2.Contains("helm", StringComparison.OrdinalIgnoreCase);
        static bool IsCape(ItemDisplayRow row) =>
            row.ModelName1.Length == 0 && row.ModelName2.Length == 0 &&
            (row.ModelTexture1.Length > 0 || row.ModelTexture2.Length > 0) &&
            row.GeosetGroup[0] != 0;

        VariantSpecimen Build(ItemDisplayRow row, string kind) => new(
            $"item:{kind}:{row.Id}", 0, checked((int)row.Id), "Character\\Human\\Male\\HumanMale.m2",
            null, kind, kind == "helm" ? CharacterEquipment.Slot.Head : CharacterEquipment.Slot.Cloak);

        if (options.ListFile is null)
        {
            IEnumerable<VariantSpecimen> helms = _variantItemDisplay.All.Where(IsHelm)
                .OrderBy(row => row.Id).Select(row => Build(row, "helm"));
            IEnumerable<VariantSpecimen> capes = _variantItemDisplay.All.Where(row => !IsHelm(row) && IsCape(row))
                .OrderBy(row => row.Id).Select(row => Build(row, "cape"));
            _variantSpecimens.AddRange(helms.Concat(capes));
        }
        else
        {
            foreach (string sourceLine in File.ReadLines(ResolveBatchPath(options.ListFile)))
            {
                string line = sourceLine.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(':');
                string kind = parts.Length == 3 ? parts[1].ToLowerInvariant() : "";
                if (parts.Length == 3 && parts[0].Equals("item", StringComparison.OrdinalIgnoreCase) &&
                    kind is "helm" or "cape" &&
                    uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) &&
                    _variantItemDisplay.Find(id) is { } row)
                {
                    bool matches = kind == "helm" ? IsHelm(row) : IsCape(row);
                    _variantSpecimens.Add(matches ? Build(row, kind) : new VariantSpecimen(
                        line, 0, checked((int)id), "", "item-kind-mismatch", kind));
                }
                else
                {
                    _variantSpecimens.Add(new VariantSpecimen(
                        line, 0, 0, "", "invalid-list-entry", kind));
                }
            }
        }

        if (options.Limit is { } limit && _variantSpecimens.Count > limit)
            _variantSpecimens.RemoveRange(limit, _variantSpecimens.Count - limit);
    }

    private void BuildPlayerSpecimens(VariantBatchOptions options)
    {
        if (options.ListFile is not null)
            throw new InvalidDataException("the reduced players axis does not accept --list");
        CharCreateCatalog catalog = CharCreateCatalog.Load(_config.ClientDataPath)
            ?? throw new InvalidDataException("character-create catalog unavailable");
        string[] axes = ["skin", "face", "hair-style", "hair-color", "facial-hair"];

        for (byte race = 1; race <= 8; race++)
        for (byte sex = 0; sex <= 1; sex++)
        {
            string raceName = PlayerRaceName(race);
            string gender = sex == 1 ? "Female" : "Male";
            string stem = $"player:{raceName.ToLowerInvariant()}-{gender.ToLowerInvariant()}";
            string model = $@"Character\{raceName}\{gender}\{raceName}{gender}.m2";
            _variantSpecimens.Add(new VariantSpecimen(
                $"{stem}:base", 0, 0, model, null, "player", 0, race, sex,
                PlayerAxis: "base"));

            int[] counts = catalog.DialCounts(race, sex);
            for (int axis = 0; axis < axes.Length; axis++)
            for (int value = 1; value < counts[axis]; value++)
            {
                int skin = 0, face = 0, hairStyle = 0, hairColor = 0, facialHair = 0;
                if (axis == 0) skin = value;
                else if (axis == 1) face = value;
                else if (axis == 2) hairStyle = value;
                else if (axis == 3) hairColor = value;
                else facialHair = value;
                _variantSpecimens.Add(new VariantSpecimen(
                    $"{stem}:{axes[axis]}:{value}", 0, 0, model, null, "player", 0,
                    race, sex, skin, face, hairStyle, hairColor, facialHair, axes[axis]));
            }
        }

        if (options.Limit is { } limit && _variantSpecimens.Count > limit)
            _variantSpecimens.RemoveRange(limit, _variantSpecimens.Count - limit);
    }

    private void LoadVariantBlankLists()
    {
        LoadVariantBlankList("portrait-expected-blank.txt", _variantExpectedBlank);
        LoadVariantBlankList("portrait-known-blank.txt", _variantKnownBlank);
        LoadVariantItemKnownIssues();
    }

    private void LoadVariantItemKnownIssues()
    {
        string path = Path.Combine(_config.RepoRoot, "data", "diagnostics", "variant-items-known-issues.txt");
        if (!File.Exists(path)) return;
        foreach (string sourceLine in File.ReadLines(path))
        {
            string[] parts = sourceLine.Split('#', 2);
            string key = parts[0].Trim();
            if (key.Length == 0) continue;
            _variantItemKnownIssues.Add(key);
            if (parts.Length < 2) continue;
            foreach (string field in parts[1].Split(';'))
            {
                string value = field.Trim();
                if (value.StartsWith("bucket=", StringComparison.OrdinalIgnoreCase))
                    _variantItemKnownIssueBuckets[key] = value[7..].Trim();
            }
        }
    }

    private void LoadVariantBlankList(string fileName, HashSet<string> destination)
    {
        string path = Path.Combine(_config.RepoRoot, "data", "diagnostics", fileName);
        if (!File.Exists(path)) return;
        foreach (string sourceLine in File.ReadLines(path))
        {
            string key = sourceLine.Split('#', 2)[0].Trim();
            if (key.Length > 0) destination.Add(key);
        }
    }

    private void StepVariantBatch()
    {
        if (_variantFinished) return;
        if (_variantBatchOptions?.Axis == "equipment") { StepEquipmentCapture(); return; }
        bool npcAxis = _variantBatchOptions!.Axis == "npc-extras";
        if (_batchPortraitTarget is null || (npcAxis ? _creatures is null : _character is null))
        {
            FinishVariantBatch(incomplete: true, error: "variant renderer unavailable");
            return;
        }
        if (_variantIndex >= _variantSpecimens.Count)
        {
            FinishVariantBatch(incomplete: false, error: null);
            return;
        }

        try
        {
            VariantSpecimen specimen = _variantSpecimens[_variantIndex];
            VariantSpecimenResult result = _variantBatchOptions.Axis switch
            {
                "npc-extras" => BakeNpcExtraSpecimen(specimen),
                "items" => BakeItemSpecimen(specimen),
                _ => BakePlayerSpecimen(specimen),
            };
            _variantSpecimenResults.Add(result);
            _variantIndex++;
            if (_variantIndex % 25 == 0 || _variantIndex == _variantSpecimens.Count)
            {
                int missing = MissingVariantResolutionRows();
                Console.WriteLine($"[variant-batch] {_variantIndex}/{_variantSpecimens.Count} " +
                    $"ready={_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.Ready)} " +
                    $"blank={_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.Blank)} " +
                    $"missingResolution={missing}");
            }
            if (_variantIndex % VariantCacheChunk == 0)
            {
                if (npcAxis) _creatures!.ClearPortraitCache();
                else if (_variantBatchOptions.Axis == "items") _character!.ClearVariantItemCache();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[variant-batch] incomplete at {_variantIndex + 1}: {exception}");
            FinishVariantBatch(incomplete: true, error: exception.Message);
        }
    }

    private VariantSpecimenResult BakeNpcExtraSpecimen(VariantSpecimen specimen)
    {
        var timer = Stopwatch.StartNew();
        _batchPortraitTarget!.Bake(() => { });
        PortraitOutcome outcome = PortraitOutcome.Skipped;
        int subjectPx = 0;
        double meanLuma = 0;
        string note = specimen.SkipNote ?? "";

        if (specimen.SkipNote is null)
        {
            var entity = new WorldEntity
            {
                Guid = 0xB47D_0000_0000UL + specimen.ExtraId,
                Type = ObjectTypeId.Unit,
                Fields = ObjectFields.ForSyntheticUnit(specimen.DisplayId, 1f),
                Position = Vector3.Zero,
                Orientation = 0f,
            };
            (PortraitTuning tuning, bool storeHit) = ResolveTuningWithHit(
                CreaturePortraitKey(specimen.DisplayId));
            if (_creatures!.PrepareVariantSpecimen(specimen.DisplayId) &&
                TryBakeCreaturePortrait(_batchPortraitTarget, entity, tuning, storeHit,
                    out CreaturePortraitBake bake))
            {
                outcome = !bake.Drawn ? PortraitOutcome.NotDrawn
                    : bake.Stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank;
                subjectPx = bake.Stats.SubjectPixels;
                meanLuma = bake.Stats.MeanLuma;
            }
            else note = "model-unavailable";
        }

        SaveVariantImage(specimen);
        CreatureRenderer.NpcVariantTrace? trace = specimen.DisplayId == 0
            ? null : _creatures!.TraceNpcVariant(specimen.DisplayId);
        AddVariantRows(specimen, trace, outcome, subjectPx, meanLuma,
            timer.Elapsed.TotalMilliseconds, note);
        return new VariantSpecimenResult(
            specimen, outcome, subjectPx, meanLuma, timer.Elapsed.TotalMilliseconds, note);
    }

    private VariantSpecimenResult BakeItemSpecimen(VariantSpecimen specimen)
    {
        var timer = Stopwatch.StartNew();
        PortraitOutcome outcome = PortraitOutcome.Skipped;
        int subjectPx = 0;
        double meanLuma = 0;
        string note = specimen.SkipNote ?? "";
        ItemDisplayRow? row = specimen.DisplayId > 0
            ? _variantItemDisplay?.Find((uint)specimen.DisplayId) : null;

        if (specimen.SkipNote is null && row is not null)
        {
            var equipment = new CharacterEquipment();
            equipment.Add($"variant {specimen.Kind} {specimen.DisplayId}",
                (uint)specimen.DisplayId, specimen.InventoryType);
            _character!.Equipment = equipment;
            _character.ApplyEquipment();

            var state = new CharacterRenderer.UnitState
            {
                Position = Vector3.Zero,
                Yaw = 0f,
                Grounded = true,
                HasIntent = true,
            };
            // Head items face the booth; cape specimens turn the camera around the fixed model
            // so the cloth is visible instead of hidden behind the torso.
            float cameraYaw = specimen.Kind == "cape" ? MathF.PI : 0f;
            Camera camera = PortraitCamera(Vector3.Zero, cameraYaw, 1.25f, 4.15f);
            camera.FieldOfViewDegrees = 43f;
            camera.AspectRatio = 233f / 224f;
            _batchPortraitTarget!.Bake(() => _character.Render(camera, state), transparent: true);
            PortraitRenderTarget.ReadbackStats stats = _batchPortraitTarget.Analyze(transparent: true);
            outcome = stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank;
            subjectPx = stats.SubjectPixels;
            meanLuma = stats.MeanLuma;
        }
        else
        {
            _batchPortraitTarget!.Bake(() => { }, transparent: true);
            if (row is null && note.Length == 0) note = "item-display-not-found";
        }

        SaveVariantImage(specimen);
        AddItemVariantRow(specimen, row, outcome, subjectPx, meanLuma,
            timer.Elapsed.TotalMilliseconds, note);
        return new VariantSpecimenResult(
            specimen, outcome, subjectPx, meanLuma, timer.Elapsed.TotalMilliseconds, note);
    }

    private VariantSpecimenResult BakePlayerSpecimen(VariantSpecimen specimen)
    {
        var timer = Stopwatch.StartNew();
        string race = PlayerRaceName(specimen.PlayerRace);
        string gender = specimen.PlayerSex == 1 ? "Female" : "Male";
        string modelKey = $"{specimen.PlayerRace}:{specimen.PlayerSex}";
        _character!.SkinId = specimen.PlayerSkin;
        _character.FaceId = specimen.PlayerFace;
        _character.HairStyleId = specimen.PlayerHairStyle;
        _character.HairColorId = specimen.PlayerHairColor;
        _character.FacialHairId = specimen.PlayerFacialHair;
        _character.Equipment = new CharacterEquipment();
        bool loaded;
        if (_variantPlayerModelKey != modelKey)
        {
            loaded = _character.Load(race, gender);
            _variantPlayerModelKey = loaded ? modelKey : "";
        }
        else
        {
            _character.Reload();
            loaded = true;
        }

        PortraitOutcome outcome = PortraitOutcome.Skipped;
        int subjectPx = 0;
        double meanLuma = 0;
        string note = loaded ? _character.HairResolution : "model-unavailable";
        if (loaded)
        {
            var state = new CharacterRenderer.UnitState
            {
                Position = Vector3.Zero,
                Yaw = 0f,
                Grounded = true,
                HasIntent = true,
            };
            Camera camera = PortraitCamera(Vector3.Zero, 0f, 1.25f, 4.15f);
            camera.FieldOfViewDegrees = 43f;
            camera.AspectRatio = 233f / 224f;
            _batchPortraitTarget!.Bake(() => _character.Render(camera, state), transparent: true);
            PortraitRenderTarget.ReadbackStats stats = _batchPortraitTarget.Analyze(transparent: true);
            outcome = stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank;
            subjectPx = stats.SubjectPixels;
            meanLuma = stats.MeanLuma;
        }
        else _batchPortraitTarget!.Bake(() => { }, transparent: true);

        SaveVariantImage(specimen);
        AddPlayerVariantRow(specimen, outcome, subjectPx, meanLuma,
            timer.Elapsed.TotalMilliseconds, note);
        return new VariantSpecimenResult(
            specimen, outcome, subjectPx, meanLuma, timer.Elapsed.TotalMilliseconds, note);
    }

    private void AddItemVariantRow(VariantSpecimen specimen, ItemDisplayRow? row,
        PortraitOutcome outcome, int subjectPx, double meanLuma, double elapsedMs, string note)
    {
        string modelPath = "NONE", modelSupplier = "NONE";
        string texturePath = "NONE", textureSupplier = "NONE";
        string demandedTexture = "NONE";
        bool missingTexture = false;
        string capeTexture = "NONE";
        string attachmentStatus = "none-authored";

        if (row is not null && specimen.Kind == "helm")
        {
            (modelPath, modelSupplier) = ResolveVariantAsset(
                row.ModelName1.Length > 0 ? HelmModelCandidates(row.ModelName1) : []);
            string declaredTexture = row.ModelTexture1.Length > 0
                ? row.ModelTexture1 : row.ModelTexture2;
            if (declaredTexture.Length > 0)
            {
                string[] candidates = HelmTextureCandidates(declaredTexture).ToArray();
                demandedTexture = candidates.FirstOrDefault() ?? declaredTexture;
                (texturePath, textureSupplier) = ResolveVariantAsset(candidates);
                missingTexture = texturePath == "NONE";
            }
            attachmentStatus = _character?.Attached?.MountCount > 0 ? "mounted" : "not-mounted";
        }
        else if (row is not null && specimen.Kind == "cape")
        {
            string declaredTexture = row.ModelTexture1.Length > 0
                ? row.ModelTexture1 : row.ModelTexture2;
            string[] candidates = CapeTextureCandidates(declaredTexture).ToArray();
            demandedTexture = candidates.FirstOrDefault() ?? declaredTexture;
            (texturePath, textureSupplier) = ResolveVariantAsset(candidates);
            capeTexture = _character?.VariantCapeTexture is { Length: > 0 } actual
                ? actual : texturePath;
            missingTexture = declaredTexture.Length > 0 && texturePath == "NONE";
            attachmentStatus = capeTexture == "NONE" ? "cape-unbound" : "cape-bound";
        }

        string supplier = string.Join('|', new[] { modelSupplier, textureSupplier }
            .Where(value => value != "NONE").Distinct(StringComparer.OrdinalIgnoreCase));
        if (supplier.Length == 0) supplier = "NONE";
        bool custom = supplier.Split('|').Any(IsPatch4);
        string geosets = _character is null ? "" : string.Join(';',
            _character.ActiveGeosets.Select(pair => $"{pair.Category}:{pair.Variant}"));
        string demandedSupplier = texturePath == "NONE" ? "NONE" : textureSupplier;

        _variantRows.Add(new VariantCsvRow(
            specimen.Key, specimen.Key, "items", specimen.DisplayId, 0, -1,
            -1, specimen.Kind, specimen.Kind == "cape" ? 2u : uint.MaxValue,
            texturePath, texturePath == "NONE" ? "UNBOUND" : texturePath,
            supplier, custom, demandedTexture, demandedSupplier, missingTexture,
            "NONE", "", specimen.ModelPath, SupplierFor(specimen.ModelPath),
            1, 0, 0, 0, 9, 0, 0, row is null ? "" : row.Id.ToString(CultureInfo.InvariantCulture),
            geosets, "NONE", "NONE", specimen.Kind == "helm" ? (uint)specimen.DisplayId : 0,
            "HuM", modelPath, modelSupplier, 0, "NONE", "NONE",
            attachmentStatus, capeTexture, 0, "NONE",
            outcome, subjectPx, meanLuma, elapsedMs, note));
    }

    private void AddPlayerVariantRow(VariantSpecimen specimen,
        PortraitOutcome outcome, int subjectPx, double meanLuma, double elapsedMs, string note)
    {
        (int duplicates, string winners) = TracePlayerCharSections(specimen);
        string skin = _character?.SkinTexturePath ?? "NONE";
        if (skin.Length == 0 || skin == "(none)") skin = "NONE";
        string supplier = SupplierFor(skin);
        string geosets = _character is null ? "" : string.Join(';',
            _character.ActiveGeosets.Select(pair => $"{pair.Category}:{pair.Variant}"));
        _variantRows.Add(new VariantCsvRow(
            specimen.Key, specimen.Key, "players", 0, 0, -1, -1,
            specimen.PlayerAxis, uint.MaxValue, skin,
            skin == "NONE" ? "UNBOUND" : skin, supplier, IsPatch4(supplier),
            "NONE", "NONE", false, "NONE", "", specimen.ModelPath,
            SupplierFor(specimen.ModelPath), specimen.PlayerRace, specimen.PlayerSex,
            (uint)specimen.PlayerSkin, (uint)specimen.PlayerFace,
            (byte)specimen.PlayerHairStyle, (uint)specimen.PlayerHairColor,
            (byte)specimen.PlayerFacialHair, "", geosets, "NONE", "NONE", 0,
            "NONE", "NONE", "NONE", 0, "NONE", "NONE", "none-authored", "NONE",
            duplicates, winners, outcome, subjectPx, meanLuma, elapsedMs, note));
    }

    private (int Duplicates, string Winners) TracePlayerCharSections(VariantSpecimen specimen)
    {
        if (_variantPlayerCharSections is null) return (0, "NONE");
        int duplicates = 0;
        var winners = new List<string>();

        CharSectionRow? Lookup(string label, uint section, int variation, int colour)
        {
            IReadOnlyList<CharSectionRow> matches = _variantPlayerCharSections.FindMatches(
                specimen.PlayerRace, specimen.PlayerSex, section, variation, colour);
            duplicates += Math.Max(0, matches.Count - 1);
            CharSectionRow? winner = matches.FirstOrDefault();
            winners.Add(winner is null
                ? $"{label}=NONE"
                : $"{label}=id{winner.Id}@row{winner.RowIndex}/flags0x{winner.Flags:X}");
            return winner;
        }

        _ = Lookup("skin", CharSectionsTable.SectionSkin, -1, specimen.PlayerSkin);
        _ = Lookup("face", CharSectionsTable.SectionFace,
            specimen.PlayerFace, specimen.PlayerSkin);
        CharSectionRow? hair = Lookup("hair", CharSectionsTable.SectionHair,
            specimen.PlayerHairStyle, specimen.PlayerHairColor);
        if (hair is null || hair.Texture1.Length == 0)
            _ = Lookup("hair-substitute", CharSectionsTable.SectionHair,
                1, specimen.PlayerHairColor);
        _ = Lookup("facial", CharSectionsTable.SectionFacialHair,
            specimen.PlayerFacialHair, specimen.PlayerHairColor);
        return (duplicates, string.Join('|', winners));
    }

    private static string PlayerRaceName(byte race) => race switch
    {
        1 => "Human",
        2 => "Orc",
        3 => "Dwarf",
        4 => "NightElf",
        5 => "Scourge",
        6 => "Tauren",
        7 => "Gnome",
        8 => "Troll",
        _ => "Human",
    };

    private (string Path, string Supplier) ResolveVariantAsset(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates.Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string supplier = SupplierFor(candidate);
            if (supplier != "NONE") return (candidate, supplier);
        }
        return ("NONE", "NONE");
    }

    private string SupplierFor(string path)
    {
        if (path.Length == 0 || path == "NONE") return "NONE";
        if (_variantSupplierCache.TryGetValue(path, out string? cached)) return cached;
        string supplier = _mpq!.ReadFileWithSupplier(path)?.Supplier ?? "NONE";
        _variantSupplierCache[path] = supplier;
        return supplier;
    }

    private static IEnumerable<string> HelmModelCandidates(string modelName)
    {
        string stem = modelName.Replace('/', '\\').TrimStart('\\');
        int dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];
        yield return $@"Item\ObjectComponents\Head\{stem}_HuM.m2";
        yield return $@"Item\ObjectComponents\Head\{stem}_HuM.M2";
        yield return $@"Item\ObjectComponents\Head\{stem}_HuM.mdx";
        yield return $@"Item\ObjectComponents\Head\{stem}HuM.m2";
        yield return $@"Item\ObjectComponents\Head\{stem}.m2";
        yield return $@"Item\ObjectComponents\Head\{stem}.M2";
        yield return $@"Item\ObjectComponents\Head\{stem}.mdx";
        yield return $"{stem}.m2";
    }

    private static IEnumerable<string> HelmTextureCandidates(string textureName)
    {
        string stem = textureName.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        yield return $@"Item\ObjectComponents\Head\{stem}.blp";
        yield return $"{stem}.blp";
    }

    private static IEnumerable<string> CapeTextureCandidates(string textureName)
    {
        string stem = textureName.Replace('/', '\\').TrimStart('\\');
        bool hasDirectory = stem.Contains('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        if (hasDirectory)
        {
            yield return stem + ".blp";
            yield break;
        }
        yield return $@"Item\ObjectComponents\Cape\{stem}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_M.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_U.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_M.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_U.blp";
    }

    private void AddVariantRows(VariantSpecimen specimen,
        CreatureRenderer.NpcVariantTrace? trace, PortraitOutcome outcome,
        int subjectPx, double meanLuma, double elapsedMs, string note)
    {
        if (trace is null || trace.Textures.Count == 0)
        {
            _variantRows.Add(new VariantCsvRow(
                $"{specimen.Key}:batch:-1", specimen.Key, "npc-extras", specimen.DisplayId,
                specimen.ExtraId, -1, -1, "NONE", uint.MaxValue,
                "NONE", "UNBOUND", "NONE", false, "NONE", "NONE", false,
                "NONE", "", specimen.ModelPath, "NONE", 0, 0, 0, 0, 0, 0, 0,
                "", "", "NONE", "NONE", 0, "NONE", "NONE", "NONE", 0,
                "NONE", "NONE", "none-authored", "NONE", 0, "NONE",
                outcome, subjectPx, meanLuma,
                elapsedMs, note));
            return;
        }

        foreach (CreatureRenderer.VariantTextureTrace texture in trace.Textures)
        {
            bool custom = IsPatch4(texture.Supplier) || IsPatch4(trace.ModelSupplier) ||
                IsPatch4(trace.BakeSupplier) || IsPatch4(trace.HelmetSupplier) ||
                trace.ShoulderSuppliers.Split('|').Any(IsPatch4);
            _variantRows.Add(new VariantCsvRow(
                $"{specimen.Key}:batch:{texture.BatchIndex}", specimen.Key, "npc-extras",
                trace.DisplayId, trace.ExtraId, texture.BatchIndex, texture.GeosetId,
                texture.Region, texture.TextureType, texture.ResolvedTexture,
                texture.EffectiveTexture, texture.Supplier, custom,
                texture.DemandedTexture, texture.DemandedSupplier,
                texture.MissingDemandedTexture, texture.Predicted7C2Texture,
                texture.ProtocolRow, trace.ModelPath, trace.ModelSupplier,
                trace.Race, trace.Sex, trace.Skin, trace.Face, trace.HairStyle,
                trace.HairColor, trace.FacialHair, trace.Equipment, trace.GeosetsChosen,
                trace.BakeTexture, trace.BakeSupplier, trace.HelmetDisplayId,
                trace.HelmetSuffix, trace.HelmetModel, trace.HelmetSupplier,
                trace.ShoulderDisplayId, trace.ShoulderModels, trace.ShoulderSuppliers,
                trace.AttachmentStatus, "NONE", 0, "NONE",
                outcome, subjectPx, meanLuma, elapsedMs, note));
        }
    }

    private static bool IsPatch4(string supplier)
        => supplier.Equals("patch-4.MPQ", StringComparison.OrdinalIgnoreCase);

    private void SaveVariantImage(VariantSpecimen specimen)
    {
        if (_variantBatchOptions!.Axis == "npc-extras" && !_variantBatchOptions.Unmasked)
            _batchPortraitTarget!.ApplyCircularMask();
        string pngPath = Path.Combine(_variantOutputDirectory,
            FileNameForKey(specimen.Key) + ".png");
        _batchPortraitTarget!.SavePng(pngPath);
        _variantSheetCells.Add((specimen.Key, _batchPortraitTarget.CaptureRgba(),
            _batchPortraitTarget.Width, _batchPortraitTarget.Height));
        if (_variantSheetCells.Count == 64) FlushVariantContactSheet();
    }

    private void FlushVariantContactSheet()
    {
        if (_variantSheetCells.Count == 0) return;
        const int cell = 256, columns = 8, rows = 8, width = cell * columns, height = cell * rows;
        byte[] sheet = new byte[width * height * 4];
        var indexLines = new List<string>();
        for (int index = 0; index < _variantSheetCells.Count; index++)
        {
            int cellX = index % columns * cell;
            int cellY = index / columns * cell;
            var source = _variantSheetCells[index];
            byte[] rgba = source.Rgba;
            for (int y = 0; y < cell; y++)
            {
                int sourceY = y * source.Height / cell;
                for (int x = 0; x < cell; x++)
                {
                    int sourceX = x * source.Width / cell;
                    int src = (sourceY * source.Width + sourceX) * 4;
                    int dst = ((cellY + y) * width + cellX + x) * 4;
                    sheet[dst] = rgba[src];
                    sheet[dst + 1] = rgba[src + 1];
                    sheet[dst + 2] = rgba[src + 2];
                    sheet[dst + 3] = rgba[src + 3];
                }
            }
            string key = _variantSheetCells[index].Key;
            string label = key.Split(':').LastOrDefault() ?? "0";
            StampDigits(sheet, width, height, cellX + 3, cellY + cell - 10, label);
            indexLines.Add($"cell={index} row={index / columns} col={index % columns} key={key}");
        }

        int sheetIndex = ++_variantSheetIndex;
        SaveRgbaPng(Path.Combine(_variantOutputDirectory,
            $"contact-sheet-{sheetIndex:00}.png"), width, height, sheet);
        File.WriteAllLines(Path.Combine(_variantOutputDirectory,
            $"contact-sheet-{sheetIndex:00}.txt"), indexLines);
        _variantSheetCells.Clear();
    }

    private void FinishVariantBatch(bool incomplete, string? error)
    {
        if (_variantFinished) return;
        if (_variantBatchOptions?.Axis == "equipment") { FinishEquipmentCapture(error); return; }
        _variantFinished = true;
        try
        {
            FlushVariantContactSheet();
            Directory.CreateDirectory(_variantOutputDirectory);
            File.WriteAllLines(Path.Combine(_variantOutputDirectory, "verdicts.csv"),
                new[] { VariantCsvHeader }.Concat(_variantRows.Select(row => row.CsvLine)));
            WriteVariantDiff();
            WriteVariantSummary(incomplete, error);
            int blanks = UnexpectedVariantBlanks();
            int missing = MissingVariantResolutionRows();
            VariantBatchExitCode = incomplete ? 1 : missing > 0 ? 4 : blanks > 0 ? 3 : 0;
            Console.WriteLine($"[variant-batch] complete: " +
                $"{_variantSpecimenResults.Count}/{_variantSpecimens.Count}, rows={_variantRows.Count}, " +
                $"blanks={blanks}, missingResolution={missing}, exit={VariantBatchExitCode}");
        }
        catch (Exception exception)
        {
            VariantBatchExitCode = 1;
            Console.Error.WriteLine($"[variant-batch] output failed: {exception}");
        }
        _window.Close();
    }

    private int UnexpectedVariantBlanks() => _variantSpecimenResults.Count(result =>
        result.Outcome == PortraitOutcome.Blank &&
        !_variantExpectedBlank.Contains(CreaturePortraitKey(result.Specimen.DisplayId)) &&
        !_variantKnownBlank.Contains(CreaturePortraitKey(result.Specimen.DisplayId)));

    private int MissingVariantResolutionRows() => _variantRows.Count(row =>
        row.MissingDemandedTexture && !_variantItemKnownIssues.Contains(row.RowKey));

    private void WriteVariantSummary(bool incomplete, string? error)
    {
        int blanks = UnexpectedVariantBlanks();
        int rawMissingRows = _variantRows.Count(row => row.MissingDemandedTexture);
        int knownMissingRows = _variantRows.Count(row => row.MissingDemandedTexture &&
            _variantItemKnownIssues.Contains(row.RowKey));
        int missingRows = MissingVariantResolutionRows();
        int missingSpecimens = _variantRows.Where(row => row.MissingDemandedTexture &&
                !_variantItemKnownIssues.Contains(row.RowKey))
            .Select(row => row.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var lines = new List<string>
        {
            $"axis: {_variantBatchOptions!.Axis}",
            $"specimens: {_variantSpecimenResults.Count}/{_variantSpecimens.Count}",
            $"csvRows: {_variantRows.Count}",
            $"Ready: {_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.Ready)}",
            $"Blank: {_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.Blank)}",
            $"NotDrawn: {_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.NotDrawn)}",
            $"Skipped: {_variantSpecimenResults.Count(row => row.Outcome == PortraitOutcome.Skipped)}",
            $"G1 blanks (unexpected): {blanks} ({(blanks == 0 ? "PASS" : "FAIL")})",
            $"G3 resolution missing: {missingRows} row(s) / {missingSpecimens} specimen(s) " +
                $"({(missingRows == 0 ? "PASS" : "FAIL")})",
            $"G3 raw resolution missing: {rawMissingRows} row(s); allowlisted known issues: {knownMissingRows}",
            $"customContentRows: {_variantRows.Count(row => row.CustomContent)}",
            $"durationSeconds: {_variantClock?.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "0"}",
            $"clientGitDescribe: {TryGitDescribe()}",
            _variantBatchOptions.Axis switch
            {
                "npc-extras" => "startupPath: variant-batch-only GL + shared MPQ/DBC + real CreatureRenderer portrait path",
                "items" => "startupPath: variant-batch-only GL + shared MPQ/DBC + real HumanMale CharacterRenderer paper-doll path",
                _ => "startupPath: variant-batch-only GL + shared MPQ/DBC + real race/sex CharacterRenderer paper-doll path",
            },
            $"cachePolicy: release regenerable renderer asset cache every {VariantCacheChunk} specimens",
            _variantBatchOptions.Axis == "players"
                ? "charSectionsDupKey/charSectionsWinnerRow: populated from production file-order lookup keys"
                : "charSectionsDupKey/charSectionsWinnerRow: reserved in the common CSV; populated by the sequenced player axis",
            "supplier/customContent: exact winning archive; patch-4.MPQ marks Nico custom content",
        };
        if (_variantBatchOptions.Axis == "items")
        {
            lines.Add($"ItemDisplayInfoSupplier: {_variantItemDbcSupplier}");
            lines.Add("enumeration: SPEC 07 fallback - installed ItemDisplayInfo helm/cape field signatures");
            lines.Add($"helmSpecimens: {_variantSpecimens.Count(row => row.Kind == "helm")}");
            lines.Add($"capeSpecimens: {_variantSpecimens.Count(row => row.Kind == "cape")}");
            lines.Add($"mountedHelms: {_variantRows.Count(row => row.AttachmentStatus == "mounted")}");
            lines.Add($"unmountedHelms: {_variantRows.Count(row => row.AttachmentStatus == "not-mounted")}");
            lines.Add($"boundCapes: {_variantRows.Count(row => row.AttachmentStatus == "cape-bound")}");
            lines.Add($"unboundCapes: {_variantRows.Count(row => row.AttachmentStatus == "cape-unbound")}");
            lines.Add($"customAssetHelms: {_variantRows.Count(row => row.Region == "helm" && row.CustomContent)}");
            lines.Add($"customAssetCapes: {_variantRows.Count(row => row.Region == "cape" && row.CustomContent)}");
            lines.Add($"unmountedCustomAssetRows: {_variantRows.Count(row => row.AttachmentStatus == "not-mounted" && row.CustomContent)}");
            lines.Add($"missingDemandSupplierMarkedCustomRows: {_variantRows.Count(row => row.MissingDemandedTexture && row.CustomContent)} " +
                "(UNBOUND rows have no supplying archive)");
            lines.Add($"knownCustomMissingDemandRows: {_variantRows.Count(row =>
                row.MissingDemandedTexture && _variantItemKnownIssueBuckets.TryGetValue(row.RowKey, out string? bucket) &&
                bucket.Equals("nico-custom", StringComparison.OrdinalIgnoreCase))}");
            foreach (string bucket in _variantItemKnownIssueBuckets.Values
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                HashSet<string> keys = _variantItemKnownIssueBuckets
                    .Where(pair => pair.Value.Equals(bucket, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                int unique = _variantRows.Count(row => keys.Contains(row.RowKey));
                int unmounted = _variantRows.Count(row => keys.Contains(row.RowKey) &&
                    row.AttachmentStatus == "not-mounted");
                int unresolved = _variantRows.Count(row => keys.Contains(row.RowKey) &&
                    row.MissingDemandedTexture);
                lines.Add($"knownIssueBucket {bucket}: uniqueRows={unique} unmounted={unmounted} " +
                    $"missingDemand={unresolved} failureObservations={unmounted + unresolved}");
            }
        }
        if (_variantBatchOptions.Axis == "players")
        {
            int collisionRows = _variantRows.Count(row => row.CharSectionsDupKey > 0);
            lines.Add($"raceSexPairs: {_variantRows.Select(row => $"{row.Race}:{row.Sex}").Distinct().Count()}");
            lines.Add($"baseRows: {_variantRows.Count(row => row.Region == "base")}");
            lines.Add($"skinAxisRows: {_variantRows.Count(row => row.Region == "skin")}");
            lines.Add($"faceAxisRows: {_variantRows.Count(row => row.Region == "face")}");
            lines.Add($"hairStyleAxisRows: {_variantRows.Count(row => row.Region == "hair-style")}");
            lines.Add($"hairColorAxisRows: {_variantRows.Count(row => row.Region == "hair-color")}");
            lines.Add($"facialHairAxisRows: {_variantRows.Count(row => row.Region == "facial-hair")}");
            lines.Add($"charSectionsCollisionRows: {collisionRows} " +
                $"({(collisionRows == 0 ? "ZERO - Flags hardening remains known gap" : "7C-3 CANDIDATE")})");
            if (collisionRows > 0)
            {
                lines.Add("");
                lines.Add("7C-3 collision protocol rows:");
                lines.AddRange(_variantRows.Where(row => row.CharSectionsDupKey > 0)
                    .Select(row => $"{row.RowKey}: dup={row.CharSectionsDupKey} winner={row.CharSectionsWinnerRow}"));
            }
        }
        if (incomplete) lines.Add($"incomplete: {error ?? "unknown error"}");
        lines.Add("");
        lines.Add("named protocol rows:");
        lines.AddRange(_variantRows.Where(row => row.ProtocolRow.Length > 0)
            .Select(row => $"{row.ProtocolRow} rowKey={row.RowKey} " +
                $"resolved={row.ResolvedTexture} effective={row.EffectiveTexture} " +
                $"predicted7C2={row.Predicted7C2Texture} supplier={row.Supplier}"));
        lines.Add("");
        lines.Add("G3 missing-demand rows (first 50):");
        lines.AddRange(_variantRows.Where(row => row.MissingDemandedTexture).Take(50)
            .Select(row => $"{row.RowKey}: demanded={row.DemandedTexture} " +
                $"effective={row.EffectiveTexture}"));
        File.WriteAllLines(Path.Combine(_variantOutputDirectory, "summary.txt"), lines);
    }

    private readonly record struct PriorVariantRow(
        string Outcome, int SubjectPx, string ResolvedTexture, string EffectiveTexture,
        string Supplier, string DemandedTexture, string GeosetsChosen,
        string HelmModel, string HelmSupplier, string ShoulderModels,
        string AttachmentStatus, string CapeTexture);

    private void WriteVariantDiff()
    {
        if (_variantBatchOptions?.DiffFile is not { } diffFile) return;
        Dictionary<string, PriorVariantRow> previous = ReadPriorVariantRows(
            ResolveBatchPath(diffFile));
        var current = _variantRows.ToDictionary(row => row.RowKey, StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        foreach (VariantCsvRow row in _variantRows)
        {
            if (!previous.TryGetValue(row.RowKey, out PriorVariantRow old))
            {
                changes.Add($"{row.RowKey}: ADDED");
                continue;
            }
            var fields = new List<string>();
            AddDiff(fields, "outcome", old.Outcome, row.Outcome.ToString());
            if (old.SubjectPx == 0 ? row.SubjectPx != 0
                : Math.Abs(row.SubjectPx - old.SubjectPx) / (double)Math.Abs(old.SubjectPx) > 0.15)
                fields.Add($"subjectPx {old.SubjectPx} -> {row.SubjectPx}");
            AddDiff(fields, "resolvedTexture", old.ResolvedTexture, row.ResolvedTexture);
            AddDiff(fields, "effectiveTexture", old.EffectiveTexture, row.EffectiveTexture);
            AddDiff(fields, "supplier", old.Supplier, row.Supplier);
            AddDiff(fields, "demandedTexture", old.DemandedTexture, row.DemandedTexture);
            AddDiff(fields, "geosetsChosen", old.GeosetsChosen, row.GeosetsChosen);
            AddDiff(fields, "helmModel", old.HelmModel, row.HelmModel);
            AddDiff(fields, "helmSupplier", old.HelmSupplier, row.HelmSupplier);
            AddDiff(fields, "shoulderModels", old.ShoulderModels, row.ShoulderModels);
            AddDiff(fields, "attachmentStatus", old.AttachmentStatus, row.AttachmentStatus);
            AddDiff(fields, "capeTexture", old.CapeTexture, row.CapeTexture);
            if (fields.Count > 0) changes.Add($"{row.RowKey}: {string.Join("; ", fields)}");
        }
        foreach (string removed in previous.Keys.Where(key => !current.ContainsKey(key)))
            changes.Add($"{removed}: REMOVED");
        File.WriteAllLines(Path.Combine(_variantOutputDirectory, "diff.txt"), changes);
        foreach (string change in changes) Console.WriteLine($"[variant-diff] {change}");
        Console.WriteLine($"[variant-diff] changedRows={changes.Count}");
    }

    private static void AddDiff(List<string> fields, string name, string oldValue, string newValue)
    {
        if (!oldValue.Equals(newValue, StringComparison.Ordinal))
            fields.Add($"{name} {oldValue} -> {newValue}");
    }

    private static Dictionary<string, PriorVariantRow> ReadPriorVariantRows(string path)
    {
        using var reader = File.OpenText(path);
        string[] headers = (reader.ReadLine() ?? "").Split(',');
        int I(string name) => Array.IndexOf(headers, name);
        string[] required = ["rowKey", "outcome", "subjectPx", "resolvedTexture",
            "effectiveTexture", "supplier", "demandedTexture", "geosetsChosen",
            "helmModel", "helmSupplier", "shoulderModels", "attachmentStatus", "capeTexture"];
        if (required.Any(name => I(name) < 0))
            throw new InvalidDataException("--diff CSV is missing variant resolution columns");
        var result = new Dictionary<string, PriorVariantRow>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split(',');
            if (fields.Length < headers.Length ||
                !int.TryParse(fields[I("subjectPx")], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int subjectPx)) continue;
            string V(string name) => fields[I(name)];
            result[V("rowKey")] = new PriorVariantRow(
                V("outcome"), subjectPx, V("resolvedTexture"), V("effectiveTexture"),
                V("supplier"), V("demandedTexture"), V("geosetsChosen"),
                V("helmModel"), V("helmSupplier"), V("shoulderModels"),
                V("attachmentStatus"), V("capeTexture"));
        }
        return result;
    }
}
