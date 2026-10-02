using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Authoring validation shared by the map/region panels and their regression checks.</summary>
public static class WorldMapAuthoringLaw
{
    public static string DirectoryFromName(string name) => new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());

    public static string? StampProblem(string directory, int col, int row, int wide, int tall, int targetCol, int targetRow)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            return "Choose a source map; its directory may contain letters, numbers and underscores.";
        if (wide < 1 || tall < 1) return "The terrain selection must be at least one tile wide and tall.";
        if (col < 0 || row < 0 || targetCol < 0 || targetRow < 0 ||
            (long)col + wide > 64 || (long)row + tall > 64 || (long)targetCol + wide > 64 || (long)targetRow + tall > 64)
            return "The source and destination must fit inside the 64 by 64 tile map.";
        return null;
    }

    public static string? PathProblem(string name, IReadOnlyList<Vector3> points, float width, float falloff)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name the path before saving.";
        if (points.Count < 2) return "Walk to the next point and add it; a path needs at least two points.";
        if (!float.IsFinite(width) || width <= 0 || !float.IsFinite(falloff) || falloff < 0)
            return "Path width must be positive and edge blending cannot be negative.";
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            return "Every path point must have a valid position.";
        if (points.Zip(points.Skip(1)).Any(pair => Vector2.DistanceSquared(new(pair.First.X, pair.First.Y), new(pair.Second.X, pair.Second.Y)) < 0.01f))
            return "Move away from the previous point before adding another.";
        return null;
    }
}
