using System.Globalization;

namespace MSUIClient.Engine.UI;

/// <summary>Read-only live protocol precondition over replicated player-visible slots.</summary>
public static class EquipmentReadinessLaw
{
    public static bool TryParse(string? specification, out IReadOnlyDictionary<int, uint> requirements, out string detail)
    {
        requirements = new Dictionary<int, uint>();
        detail = "invalid-item-specification";
        if (string.IsNullOrEmpty(specification) || specification.Length > 300) return false;
        string[] pairs = specification.Split(',');
        if (pairs.Length is < 1 or > 19) return false;
        var parsed = new Dictionary<int, uint>();
        foreach (string pair in pairs)
        {
            string[] values = pair.Split(':');
            if (values.Length != 2 ||
                !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                slot is < 0 or > 18 ||
                !uint.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint entry) ||
                !parsed.TryAdd(slot, entry)) return false;
        }
        requirements = parsed;
        detail = "valid";
        return true;
    }

    public static bool Matches(string? specification, IReadOnlyList<uint>? visibleEntries, out string detail)
    {
        if (!TryParse(specification, out var required, out detail)) return false;
        if (visibleEntries is null || visibleEntries.Count != 19)
        {
            detail = "player-visible-slots-unavailable";
            return false;
        }
        string[] differences = required.Where(pair => visibleEntries[pair.Key] != pair.Value)
            .Select(pair => $"slot={pair.Key}:expected={pair.Value}:actual={visibleEntries[pair.Key]}").ToArray();
        detail = differences.Length == 0 ? "matched" : string.Join(',', differences);
        return differences.Length == 0;
    }
}
