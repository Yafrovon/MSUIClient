using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Benilla's live, Classic-priority enemy cycle. The game loop supplies legal streamed units
/// and camera projections; this law owns scoring, pulled-in screen tiering and recent history.
/// </summary>
public static class TargetCycleLaw
{
    public const float Range = 41f;
    /// <summary>Range for auto-acquiring a target on an Attack press with nothing selected -
    /// deliberately tighter than the Tab-cycle range above, since this fires without the
    /// player choosing a direction to look in first.</summary>
    public const float AttackAcquireRange = Range * 0.5f;
    public const float FrustumPullSides = .10f;
    public const float FrustumPullTop = .10f;
    public const float FrustumPullBottom = .10f;
    public const double HistorySeconds = 4.0;
    public const float ScreenWeight = 1f;
    public const float DistanceWeight = 1f;
    public const float CombatWithMeBonus = 3f;

    public readonly record struct Candidate(ulong Guid, bool OnScreen, float Score);
    public readonly record struct Pick(ulong Guid, bool Wrapped);

    /// <summary>
    /// Normalized distance from screen center when the point is inside the ten-percent
    /// pulled-in viewport. A point outside that inner rect belongs to the fallback tier.
    /// </summary>
    public static float? ScreenOffCenter(Vector2 pixel, Vector2 viewport)
    {
        if (viewport.X <= 0f || viewport.Y <= 0f) return null;
        float x0 = viewport.X * FrustumPullSides;
        float x1 = viewport.X * (1f - FrustumPullSides);
        float y0 = viewport.Y * FrustumPullTop;
        float y1 = viewport.Y * (1f - FrustumPullBottom);
        if (pixel.X < x0 || pixel.X > x1 || pixel.Y < y0 || pixel.Y > y1) return null;
        float halfDiagonal = viewport.Length() * .5f;
        return halfDiagonal > 0f
            ? Vector2.Distance(pixel, viewport * .5f) / halfDiagonal
            : null;
    }

    public static float PriorityScore(float? offCenter, float distance, bool combatWithMe) =>
        ScreenWeight * (offCenter ?? 0f) +
        DistanceWeight * (Math.Max(0f, distance) / Range) -
        (combatWithMe ? CombatWithMeBonus : 0f);

    /// <summary>
    /// Sort by screen tier and score, then enforce AllowAnyOnScreen=1: when an inner-screen
    /// candidate exists, off-screen candidates cannot enter this press's cycle.
    /// </summary>
    public static List<Candidate> SortedPool(IEnumerable<Candidate> candidates)
    {
        List<Candidate> sorted = candidates
            .OrderByDescending(candidate => candidate.OnScreen)
            .ThenBy(candidate => candidate.Score)
            .ToList();
        if (sorted.Any(candidate => candidate.OnScreen))
            sorted.RemoveAll(candidate => !candidate.OnScreen);
        return sorted;
    }

    /// <summary>
    /// Reverse walks newest-to-oldest history. Forward chooses the best fresh non-current
    /// candidate; exhausting history wraps to the best non-current candidate.
    /// </summary>
    public static Pick? Select(IReadOnlyList<Candidate> pool,
        IReadOnlyList<ulong> visitedOldestFirst, ulong current, bool reverse)
    {
        if (pool.Count == 0) return null;
        if (reverse)
        {
            for (int historyIndex = visitedOldestFirst.Count - 1; historyIndex >= 0; historyIndex--)
            {
                ulong guid = visitedOldestFirst[historyIndex];
                if (guid == current) continue;
                if (pool.Any(candidate => candidate.Guid == guid)) return new(guid, false);
            }
        }

        for (int i = 0; i < pool.Count; i++)
            if (pool[i].Guid != current && !visitedOldestFirst.Contains(pool[i].Guid))
                return new(pool[i].Guid, false);
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].Guid != current)
                return new(pool[i].Guid, true);
        return new(pool[0].Guid, true);
    }
}

/// <summary>Insertion-ordered, four-second target history shared by forward and reverse TAB.</summary>
public sealed class TargetCycleHistory
{
    private readonly List<(ulong Guid, double When)> _visited = [];

    public IReadOnlyList<ulong> Guids => _visited.Select(entry => entry.Guid).ToArray();

    public void Prune(double now) =>
        _visited.RemoveAll(entry => now - entry.When >= TargetCycleLaw.HistorySeconds);

    public void Push(ulong guid, double now)
    {
        _visited.RemoveAll(entry => entry.Guid == guid);
        _visited.Add((guid, now));
    }

    public void Clear() => _visited.Clear();
}
