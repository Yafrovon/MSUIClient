namespace MSUIClient.Formats;

public readonly record struct SoundVariant(string Path, uint Weight);

public readonly record struct SoundEntry(
    uint Id, uint Type, string Name, IReadOnlyList<SoundVariant> Variants,
    float Volume, uint Flags, float MinDistance, float CutoffDistance, uint Eax)
{
    public bool Looping => (Flags & 0x200) != 0;
    public bool NoDuplicates => (Flags & 0x20) != 0;
    public bool VaryPitch => (Flags & 0x400) != 0;
}

/// <summary>Complete build-5875 SoundEntries.dbc spell-audio projection.</summary>
public sealed class SoundEntriesCatalog
{
    public const string MpqPath = @"DBFilesClient\SoundEntries.dbc";
    private readonly Dictionary<uint, SoundEntry> _byId = [];
    private readonly Dictionary<string, uint> _byName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byId.Count;
    /// <summary>Every row, for pickers (SPELL_CREATOR_PLAN.md P3.9).</summary>
    public IEnumerable<SoundEntry> Entries => _byId.Values;
    public bool TryGet(uint id, out SoundEntry entry) => _byId.TryGetValue(id, out entry);
    public bool TryGet(string name, out SoundEntry entry)
    {
        entry = default;
        return _byName.TryGetValue(name, out uint id) && _byId.TryGetValue(id, out entry);
    }

    public static SoundEntriesCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount < 29) return null;
        var result = new SoundEntriesCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            string directory = dbc.GetString(row, 23).TrimEnd('\\', '/');
            var variants = new List<SoundVariant>(10);
            for (int i = 0; i < 10; i++)
            {
                string file = dbc.GetString(row, 3 + i);
                if (file.Length == 0) continue;
                string path = directory.Length == 0 ? file : $@"{directory}\{file}";
                variants.Add(new SoundVariant(path.Replace('/', '\\'), dbc.GetUInt(row, 13 + i)));
            }
            string name = dbc.GetString(row, 2);
            var entry = new SoundEntry(id, dbc.GetUInt(row, 1), name, variants,
                dbc.GetFloat(row, 24), dbc.GetUInt(row, 25), dbc.GetFloat(row, 26),
                dbc.GetFloat(row, 27), dbc.GetUInt(row, 28));
            result._byId[id] = entry;
            if (name.Length > 0) result._byName[name] = id;
        }
        return result;
    }
}
