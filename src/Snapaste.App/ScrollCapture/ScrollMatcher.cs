namespace Snapaste.ScrollCapture;

public enum Confidence { Low, Medium, High }
public sealed record MatchMetadata(int Offset, double Score, double SecondBestScore, double Uniqueness, double Texture, Confidence Confidence, int FixedTop, int FixedBottom);
public sealed record StitchMetadata(int AddedRows, int TotalRows, bool Manual);
public sealed class ScrollFrame
{
    public required string BitmapPath { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public Rect CaptureRect { get; init; }
    public int FrameIndex { get; init; }
    public ulong PerceptualHash { get; init; }
    public MatchMetadata? MatchMetadata { get; set; }
    public StitchMetadata? StitchMetadata { get; set; }
    public BitmapSource Bitmap => Core.ImageDocument.ReadBitmap(BitmapPath);
}
public sealed class FrameSample
{
    public const int Columns = 80;
    public int Height { get; }
    public byte[] Gray { get; }
    public ulong Hash { get; }
    public FrameSample(BitmapSource bitmap)
    {
        Height = bitmap.PixelHeight;
        var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0); var pixels = new byte[bitmap.PixelWidth * Height]; gray.CopyPixels(pixels, bitmap.PixelWidth, 0);
        Gray = new byte[Columns * Height];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Columns; x++)
        {
            // Keep away from scrollbars and common floating buttons at the far edges.
            int px = Math.Clamp((int)((x + 3d) / (Columns + 6) * bitmap.PixelWidth), 0, bitmap.PixelWidth - 1);
            Gray[y * Columns + x] = pixels[y * bitmap.PixelWidth + px];
        }
        ulong hash = 0; for (int i = 0; i < 64; i++) if (Gray[(Height * i / 64) * Columns + (i % Columns)] > 128) hash |= 1UL << i; Hash = hash;
    }
    public double Difference(FrameSample other)
    {
        if (Height != other.Height) return 1;
        // Ignore the most volatile eighth of blocks (caret, clock, animation).
        var blocks = new List<double>();
        for (int by = 0; by < 12; by++) for (int bx = 0; bx < 8; bx++)
        {
            double sum = 0; int n = 0;
            for (int y = Height * by / 12; y < Height * (by + 1) / 12; y += 3) for (int x = bx * 10; x < bx * 10 + 10; x++) { sum += Math.Abs(Gray[y * Columns + x] - other.Gray[y * Columns + x]); n++; }
            blocks.Add(sum / Math.Max(1, n) / 255);
        }
        blocks.Sort(); return blocks.Take(84).Average();
    }
}
public static class ScrollMatcher
{
    public static MatchMetadata Match(FrameSample a, FrameSample b, double sensitivity, CancellationToken token, int topOverride = -1, int bottomOverride = -1)
    {
        int h = a.Height; if (h != b.Height || h < 60) return new(0, 0, 0, 0, 0, Confidence.Low, 0, 0);
        int top = topOverride >= 0 ? topOverride : FixedBand(a, b, false), bottom = bottomOverride >= 0 ? bottomOverride : FixedBand(a, b, true);
        int minOverlap = Math.Max(40, (h - top - bottom) / 6), maxOffset = h - top - bottom - minOverlap;
        var scores = new List<(int Offset, double Cost, double Texture)>();
        for (int d = 4; d <= maxOffset; d += 4)
        {
            token.ThrowIfCancellationRequested(); var result = Cost(a, b, d, top, bottom, 4); scores.Add((d, result.Cost, result.Texture));
        }
        if (scores.Count == 0) return new(0, 0, 0, 0, 0, Confidence.Low, top, bottom);
        var seed = scores.OrderBy(s => s.Cost).Take(5).ToArray();
        foreach (var item in seed) for (int d = Math.Max(1, item.Offset - 3); d <= Math.Min(maxOffset, item.Offset + 3); d++)
        {
            token.ThrowIfCancellationRequested(); var result = Cost(a, b, d, top, bottom, 2); scores.Add((d, result.Cost, result.Texture));
        }
        var best = scores.MinBy(s => s.Cost);
        var alternatives = scores.Where(s => Math.Abs(s.Offset - best.Offset) >= 10).ToArray();
        double secondCost = alternatives.Length == 0 ? best.Cost : alternatives.Min(s => s.Cost);
        double score = Math.Clamp(1 - best.Cost / 35, 0, 1), second = Math.Clamp(1 - secondCost / 35, 0, 1);
        double uniqueness = Math.Clamp((secondCost - best.Cost) / Math.Max(1.5, secondCost), 0, 1);
        bool reliable = score >= sensitivity && uniqueness >= .16 && best.Texture >= 1.1 && secondCost - best.Cost >= .65;
        var confidence = !reliable ? Confidence.Low : score >= .92 && uniqueness >= .38 ? Confidence.High : Confidence.Medium;
        return new(best.Offset, score, second, uniqueness, best.Texture, confidence, top, bottom);
    }
    private static int FixedBand(FrameSample a, FrameSample b, bool bottom)
    {
        int h = a.Height, last = 0, bad = 0;
        for (int i = 0; i < h / 4; i++)
        {
            int y = bottom ? h - 1 - i : i, equal = 0;
            for (int x = 0; x < FrameSample.Columns; x++) if (Math.Abs(a.Gray[y * FrameSample.Columns + x] - b.Gray[y * FrameSample.Columns + x]) < 5) equal++;
            if (equal >= FrameSample.Columns * .94) { last = i + 1; bad = 0; } else if (++bad >= 3) break;
        }
        return Math.Max(0, last - 2);
    }
    private static (double Cost, double Texture) Cost(FrameSample a, FrameSample b, int offset, int top, int bottom, int step)
    {
        const int width = FrameSample.Columns;
        var bands = new List<(double Error, double Texture)>();
        for (int band = 0; band < 8; band++)
        {
            double error = 0, texture = 0; int count = 0;
            for (int y = top + 2; y < a.Height - bottom - offset - 2; y += step)
            {
                for (int x = band * 10; x < band * 10 + 10; x += 2)
                {
                    int ai = (y + offset) * width + x, bi = y * width + x;
                    double difference = Math.Abs(a.Gray[ai] - b.Gray[bi]);
                    double da = a.Gray[ai + width] - a.Gray[ai - width], db = b.Gray[bi + width] - b.Gray[bi - width];
                    error += Math.Min(60, difference) * .6 + Math.Min(60, Math.Abs(da - db)) * .4;
                    texture += Math.Abs(da); count++;
                }
            }
            bands.Add((error / Math.Max(1, count), texture / Math.Max(1, count)));
        }
        // Discard two worst vertical strips: local overlays and animated content cannot dominate.
        var accepted = bands.OrderBy(v => v.Error).Take(6).ToArray(); return (accepted.Average(v => v.Error), accepted.Average(v => v.Texture));
    }
}
