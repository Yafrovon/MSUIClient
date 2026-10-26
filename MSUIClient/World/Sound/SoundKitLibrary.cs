using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>
/// SoundEntries.dbc, and the one question it answers: WHICH FILE does this kit
/// play right now.
///
/// This is shared policy, not a device concern and not a spell concern - a zone
/// ambience bed and a fireball impact resolve their file by the identical rules
/// (weighted-depleting variant choice). The 0x20 "no duplicates" flag is a live
/// same-kit admission gate and belongs to the playback owner, not file selection.
/// It lives here so both callers use the selection rules
/// rather than one of them borrowing the other's channels to get at them.
/// </summary>
public sealed class SoundKitLibrary
{
    private readonly SoundEntriesCatalog? _catalog;

    /// <summary>The last kit:file actually chosen, for the dev readouts.</summary>
    public string LastCue { get; private set; } = "";

    /// <summary>Remaining authored weight per file. A pick spends one unit; the
    /// complete authored pool is restored only when exhausted.</summary>
    private readonly Dictionary<uint, uint[]> _remainingWeights = [];

    public SoundKitLibrary(MpqMount mpq) => _catalog = SoundEntriesCatalog.Load(mpq);

    /// <summary>The SoundEntries rows themselves (read-only), for the creator's sound picker.</summary>
    public SoundEntriesCatalog? Catalog => _catalog;

    public bool TryGet(uint? soundId, out SoundEntry entry)
    {
        entry = default;
        return soundId is uint id && id != 0 && _catalog?.TryGet(id, out entry) == true;
    }

    public bool TryGet(string soundName, out SoundEntry entry)
    {
        entry = default;
        return _catalog?.TryGet(soundName, out entry) == true;
    }

    /// <summary>Whether the authored flags say this kit loops. Callers may force a
    /// loop on top of this; they may not turn one off.</summary>
    public bool IsAuthoredLoop(uint? soundId)
        => TryGet(soundId, out SoundEntry entry) && entry.Looping;

    /// <summary>Pick from the kit's remaining weighted pool, then spend one unit
    /// of the chosen file's weight. This avoids memoryless same-file rattling while
    /// preserving authored ratios over each depletion cycle.</summary>
    public SoundVariant PickVariant(in SoundEntry entry)
    {
        IReadOnlyList<SoundVariant> candidates = entry.Variants;
        if (candidates.Count == 0)
            throw new InvalidOperationException($"sound kit {entry.Id} has no variants");
        if (candidates.Count == 1)
        {
            SoundVariant only = candidates[0];
            LastCue = $"{entry.Id}:{only.Path}";
            return only;
        }

        if (!_remainingWeights.TryGetValue(entry.Id, out uint[]? remaining) ||
            remaining.Length != candidates.Count)
        {
            remaining = candidates.Select(static candidate => candidate.Weight).ToArray();
            _remainingWeights[entry.Id] = remaining;
        }

        ulong total = 0;
        foreach (uint weight in remaining) total += weight;
        if (total == 0)
        {
            for (int i = 0; i < candidates.Count; i++) remaining[i] = candidates[i].Weight;
            foreach (uint weight in remaining) total += weight;
        }

        int pickedIndex = 0;
        if (total > 0)
        {
            ulong roll = (ulong)Random.Shared.NextInt64((long)total);
            for (int i = 0; i < remaining.Length; i++)
            {
                if (roll < remaining[i]) { pickedIndex = i; break; }
                roll -= remaining[i];
            }
            remaining[pickedIndex]--;
        }
        SoundVariant picked = candidates[pickedIndex];
        LastCue = $"{entry.Id}:{picked.Path}";
        return picked;
    }

    /// <summary>Select an authored variation by its exact zero-based index.</summary>
    public SoundVariant PickVariantAt(in SoundEntry entry, int variation)
    {
        if ((uint)variation >= (uint)entry.Variants.Count)
            throw new ArgumentOutOfRangeException(nameof(variation));
        SoundVariant picked = entry.Variants[variation];
        LastCue = $"{entry.Id}:{picked.Path}";
        return picked;
    }
}
