namespace MSUIClient.Creator;

/// <summary>Measured pixel evidence, not an aesthetic acceptance classifier.</summary>
public static class EquipmentCapturePixels
{
    public sealed record Measurements(int SubjectPixels, int BorderPixels, int NearBlackPixels,
        int NearWhitePixels, int MagentaPixels, double MeanLuma, int[] Bounds, int[][] SilhouetteRows);

    public static Measurements Measure(byte[] rgba, int width, int height)
    {
        int subject = 0, border = 0, dark = 0, white = 0, magenta = 0;
        int minX = width, minY = height, maxX = -1, maxY = -1;
        double sum = 0;
        var rows = new List<int[]>();
        for (int y = 0; y < height; y++)
        {
            int left = width, right = -1, spans = 0;
            bool previous = false;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool present = rgba[i + 3] > 16;
                if (present && !previous) spans++;
                previous = present;
                if (!present) continue;
                subject++;
                left = Math.Min(left, x); right = x;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (x < 3 || y < 3 || x >= width - 3 || y >= height - 3) border++;
                double luma = (.2126 * rgba[i] + .7152 * rgba[i + 1] + .0722 * rgba[i + 2]) / 255;
                sum += luma;
                if (luma < .03) dark++;
                if (luma > .97) white++;
                if (rgba[i] > 235 && rgba[i + 1] < 25 && rgba[i + 2] > 235) magenta++;
            }
            if (right >= left) rows.Add([y, left, right, spans]);
        }
        return new(subject, border, dark, white, magenta, subject == 0 ? 0 : sum / subject,
            subject == 0 ? [-1, -1, -1, -1] : [minX, minY, maxX, maxY], rows.ToArray());
    }

    public static byte[] ValueAndContour(byte[] rgba, int width, int height)
    {
        byte[] result = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            byte value = (byte)(.2126 * rgba[i] + .7152 * rgba[i + 1] + .0722 * rgba[i + 2]);
            bool subject = rgba[i + 3] > 16;
            bool edge = subject && (x == 0 || y == 0 || x == width - 1 || y == height - 1 ||
                rgba[i - 4 + 3] <= 16 || rgba[i + 4 + 3] <= 16 ||
                rgba[i - width * 4 + 3] <= 16 || rgba[i + width * 4 + 3] <= 16);
            result[i] = edge ? (byte)255 : subject ? value : (byte)24;
            result[i + 1] = edge ? (byte)150 : subject ? value : (byte)24;
            result[i + 2] = edge ? (byte)0 : subject ? value : (byte)24;
            result[i + 3] = 255;
        }
        return result;
    }
}
