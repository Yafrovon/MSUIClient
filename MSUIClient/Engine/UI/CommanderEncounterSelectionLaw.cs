using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

public static class CommanderEncounterSelectionLaw
{
    /// <summary>
    /// The authored definition that owns this creature entry on this map, or null.
    ///
    /// A boss is its own encounter, so matching objectives alone was enough for the ten Molten
    /// Core bosses. A TRASH PACK is not: the compiled pack definitions name every member in
    /// AddEntries and only the leader in Objectives, so clicking any other member of a pack
    /// found nothing and the raid had no plan for the pack standing in front of it. Objectives
    /// are searched first so a boss standing among its adds still binds to the boss fight.
    /// </summary>
    public static CommanderEncounterDefinition? FindForEntry(
        IReadOnlyList<CommanderEncounterDefinition> definitions, uint mapId, uint entry) =>
        FindForEntry(definitions, mapId, entry, null);

    /// <summary>
    /// With <paramref name="position"/>, the definition whose room CONTAINS that unit wins.
    /// Thirteen different Molten Core packs are led by a Firelord and each carries its own room
    /// bounds and tank anchor, so entry alone picks the wrong room twelve times out of thirteen -
    /// and the executor gates objective binding on the room, so the raid would refuse to fight
    /// the pack actually standing in front of it.
    /// </summary>
    public static CommanderEncounterDefinition? FindForEntry(
        IReadOnlyList<CommanderEncounterDefinition> definitions, uint mapId, uint entry,
        Vector3? position)
    {
        if (entry == 0) return null;
        var objectives = definitions.Where(d => d.MapId == mapId && d.ObjectiveEntries.Contains(entry)).ToArray();
        var adds = definitions.Where(d => d.MapId == mapId && d.AddEntries.Contains(entry)).ToArray();
        if (position is { } point)
            // Compiled room boxes are generous and overlap, so "contains" alone still picks a
            // neighbouring pack's room. Among the rooms that contain the unit, the nearest
            // authored tank anchor is the pack it actually belongs to.
            return Nearest(objectives.Where(d => Contains(d, point)).ToArray(), point)
                ?? Nearest(adds.Where(d => Contains(d, point)).ToArray(), point)
                ?? Nearest(objectives, point) ?? Nearest(adds, point);
        return objectives.FirstOrDefault() ?? adds.FirstOrDefault();
    }

    private static bool Contains(CommanderEncounterDefinition definition, Vector3 point)
    {
        float[] min = definition.Bounds.Min, max = definition.Bounds.Max;
        if (min.Length < 3 || max.Length < 3) return false;
        return point.X >= min[0] && point.X <= max[0] &&
               point.Y >= min[1] && point.Y <= max[1] &&
               point.Z >= min[2] && point.Z <= max[2];
    }

    /// <summary>Fallback when no room contains the unit: the nearest authored tank anchor.</summary>
    private static CommanderEncounterDefinition? Nearest(
        IReadOnlyList<CommanderEncounterDefinition> definitions, Vector3 point)
    {
        CommanderEncounterDefinition? best = null;
        float bestDistance = float.MaxValue;
        foreach (CommanderEncounterDefinition definition in definitions)
        {
            float[] anchor = definition.TankAnchor;
            if (anchor.Length < 3) continue;
            float distance = Vector3.DistanceSquared(point, new Vector3(anchor[0], anchor[1], anchor[2]));
            if (distance >= bestDistance) continue;
            best = definition; bestDistance = distance;
        }
        return best;
    }

    public static CommanderEncounterDefinition Select(IReadOnlyList<CommanderEncounterDefinition> definitions,
        CommanderBossFact fact, uint mapId, Vector3 position, int groupSize)
    {
        var authored = FindForEntry(definitions, mapId, fact.Entry, position);
        if (authored is not null) return authored;
        float x = position.X, y = position.Y, z = position.Z;
        CommanderEncounterTeam[] teams = groupSize > 5
            ? [new("Left", [x - 8, y + 18, z]), new("Right", [x - 8, y - 18, z])]
            : [new("Raid", [x - 8, y + 18, z])];
        return new(1, $"basic-{mapId}-{fact.Entry}", fact.Name + " (basic plan)", mapId, fact.Entry,
            new([x - 50, y - 50, z - 30], [x + 50, y + 50, z + 30]), [x + 15, y, z],
            teams, 0, 1, Math.Max(teams.Length, (int)Math.Ceiling(groupSize / 5.0)), [],
            [new(1, "Combat", 0, 0, 100, -1, 0, false, true, true, .8f)], [])
        { ImmuneSchools = fact.ImmuneSchools, Objectives = [fact.Entry], Coverage = "basic" };
    }
}
