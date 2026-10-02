using System.Text.Json;

namespace MSUIClient.Creator;

/// <summary>Offline production-renderer capture inputs. Display IDs must exist in mounted MPQs.</summary>
public sealed class EquipmentCaptureManifest
{
    public int SchemaVersion { get; set; } = 1;
    public int Width { get; set; } = 768;
    public int Height { get; set; } = 768;
    public int[] Races { get; set; } = [1, 2, 3, 4, 5, 6, 7, 8];
    public int[] Sexes { get; set; } = [0, 1];
    public int[] SheathStates { get; set; } = [1, 0];
    public EquipmentCaptureView[] Views { get; set; } =
    [
        new() { Key = "front", YawDegrees = 0 },
        new() { Key = "right", YawDegrees = 90 },
        new() { Key = "back", YawDegrees = 180 },
        new() { Key = "left", YawDegrees = 270 },
        new() { Key = "front-three-quarter", YawDegrees = 45, PitchDegrees = 10 },
        new() { Key = "back-three-quarter", YawDegrees = 225, PitchDegrees = 10 },
    ];
    public EquipmentCapturePose[] Poses { get; set; } =
    [
        new() { Key = "stand-start", AnimationId = 0, TimeSeconds = 0 },
        new() { Key = "stand-mid", AnimationId = 0, TimeSeconds = .6f },
        new() { Key = "walk", AnimationId = 4, TimeSeconds = .25f },
        new() { Key = "run", AnimationId = 5, TimeSeconds = .2f },
        new() { Key = "attack", AnimationId = 16, TimeSeconds = .2f },
    ];
    public EquipmentCaptureSet[] Sets { get; set; } = [];
    /// <summary>Explicit choice for a single requested body; null preserves legacy choice zero.</summary>
    public EquipmentCaptureAppearance? Appearance { get; set; }
    /// <summary>Optional offline cache proof: retain these additional bodies with the same set,
    /// then draw every body's MountSet through one attachment renderer in alternating order.</summary>
    public EquipmentCaptureBody[] ConcurrentBodies { get; set; } = [];


    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IncludeFields = true,
    };

    public static EquipmentCaptureManifest Load(string path)
    {
        var value = JsonSerializer.Deserialize<EquipmentCaptureManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Equipment capture manifest is empty.");
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported equipment capture schemaVersion.");
        if (Width < 128 || Width > 2048 || Height < 128 || Height > 2048)
            throw new InvalidDataException("Capture width/height must be 128..2048.");
        if (Races.Length == 0 || Races.Any(x => x < 1 || x > 8) || Races.Distinct().Count() != Races.Length)
            throw new InvalidDataException("Races must be distinct vanilla IDs 1..8.");
        if (Sexes.Length == 0 || Sexes.Any(x => x < 0 || x > 1) || Sexes.Distinct().Count() != Sexes.Length)
            throw new InvalidDataException("Sexes must be distinct values 0 or 1.");
        if (SheathStates.Length == 0 || SheathStates.Any(x => x < 0 || x > 2) || SheathStates.Distinct().Count() != SheathStates.Length)
            throw new InvalidDataException("SheathStates must be distinct values 0..2.");
        if (Appearance is not null)
        {
            if (Races.Length != 1 || Sexes.Length != 1)
                throw new InvalidDataException("Explicit appearance requires exactly one race and sex; use separate manifests for other bodies.");
            Appearance.Validate();
        }
        if (ConcurrentBodies is null || ConcurrentBodies.Length > 3 ||
            ConcurrentBodies.Any(x => x is null || x.Race < 1 || x.Race > 8 || x.Sex < 0 || x.Sex > 1) ||
            ConcurrentBodies.Select(x => (x.Race, x.Sex)).Distinct().Count() != ConcurrentBodies.Length)
            throw new InvalidDataException("ConcurrentBodies requires up to three distinct vanilla race/sex pairs.");
        if (ConcurrentBodies.Length > 0 && (Races.Length != 1 || Sexes.Length != 1 || Appearance is not null ||
            ConcurrentBodies.Any(x => x.Race == Races[0] && x.Sex == Sexes[0])))
            throw new InvalidDataException("Concurrent capture requires one primary body, no appearance override, and distinct peers.");
        ValidateKeys(Sets.Select(x => x.Key), "sets");
        ValidateKeys(Views.Select(x => x.Key), "views");
        ValidateKeys(Poses.Select(x => x.Key), "poses");
        foreach (var view in Views)
            if (!float.IsFinite(view.YawDegrees) || !float.IsFinite(view.PitchDegrees) || Math.Abs(view.PitchDegrees) > 80)
                throw new InvalidDataException($"Invalid camera view {view.Key}.");
        foreach (var pose in Poses)
            if (pose.AnimationId < 0 || !float.IsFinite(pose.TimeSeconds) || pose.TimeSeconds < 0 || pose.TimeSeconds > 60)
                throw new InvalidDataException($"Invalid animation sample {pose.Key}.");
        foreach (var set in Sets)
        {
            if (set.Equipment.Length == 0) throw new InvalidDataException($"Set {set.Key} contains no equipment.");
            if (set.Equipment.Any(x => x.DisplayId == 0 || x.InventoryType < 1 || x.InventoryType > 28 || x.EquipmentSlot < -1 || x.EquipmentSlot > 18))
                throw new InvalidDataException($"Set {set.Key} has invalid equipment fields.");
            var slots = set.Equipment.Where(x => x.EquipmentSlot >= 0).Select(x => x.EquipmentSlot).ToArray();
            if (slots.Distinct().Count() != slots.Length) throw new InvalidDataException($"Set {set.Key} repeats an equipment slot.");
        }
        long captures = (long)Sets.Length * Races.Length * Sexes.Length * Views.Length * Poses.Length * SheathStates.Length;
        if (captures > 100000) throw new InvalidDataException("Capture matrix exceeds 100000 images; split the manifest.");
    }

    private static void ValidateKeys(IEnumerable<string> source, string field)
    {
        string[] keys = source.ToArray();
        if (keys.Length == 0 || keys.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80 || x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length)
            throw new InvalidDataException($"{field} needs distinct keys containing letters, numbers, '-' or '_'.");
    }
}

public sealed class EquipmentCaptureSet
{
    public string Key { get; set; } = "";
    public EquipmentCaptureItem[] Equipment { get; set; } = [];
}

public sealed class EquipmentCaptureItem
{
    public string Name { get; set; } = "";
    public uint DisplayId { get; set; }
    public int InventoryType { get; set; }
    public int EquipmentSlot { get; set; } = -1;
    public byte ItemClass { get; set; }
    public byte ItemSubclass { get; set; }
    public byte Material { get; set; }
    public byte Sheath { get; set; }
}

public sealed class EquipmentCaptureView
{
    public string Key { get; set; } = "";
    public float YawDegrees { get; set; }
    public float PitchDegrees { get; set; }
}

public sealed class EquipmentCapturePose
{
    public string Key { get; set; } = "";
    public int AnimationId { get; set; }
    public float TimeSeconds { get; set; }
}

/// <summary>Native character-creation choices, not model geoset numbers.</summary>
public sealed class EquipmentCaptureAppearance
{
    public int Skin { get; set; }
    public int Face { get; set; }
    public int HairStyle { get; set; }
    public int HairColor { get; set; }
    public int FacialHair { get; set; }

    public void Validate()
    {
        if (new[] { Skin, Face, HairStyle, HairColor, FacialHair }.Any(x => x < 0 || x > 255))
            throw new InvalidDataException("Appearance choices must be character-creation indices 0..255.");
    }
}

public sealed class EquipmentCaptureBody
{
    public byte Race { get; set; }
    public byte Sex { get; set; }
}
