using System.Buffers;

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
        int width = bitmap.PixelWidth;
        var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        Gray = new byte[Columns * Height];
        Span<int> sampleColumns = stackalloc int[Columns];
        for (int x = 0; x < Columns; x++) sampleColumns[x] = (int)((x + 3d) / (Columns + 6) * width);
        const int blockHeight = 32;
        var pixels = ArrayPool<byte>.Shared.Rent(width * Math.Min(Height, blockHeight));
        try
        {
            for (int top = 0; top < Height; top += blockHeight)
            {
                int rows = Math.Min(blockHeight, Height - top);
                gray.CopyPixels(new Int32Rect(0, top, width, rows), pixels, width, 0);
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < Columns; x++)
                        Gray[(top + y) * Columns + x] = pixels[y * width + sampleColumns[x]];
            }
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (Gray[(Height * i / 64) * Columns + i] > 128) hash |= 1UL << i;
        Hash = hash;
    }
    public double Difference(FrameSample other, bool includeLocalChanges = false)
    {
        if (Height != other.Height) return 1;
        Span<double> blocks = stackalloc double[96];
        for (int by = 0; by < 12; by++) for (int bx = 0; bx < 8; bx++)
        {
            int sum = 0, n = 0;
            for (int y = Height * by / 12; y < Height * (by + 1) / 12; y += 2)
                for (int x = bx * 10; x < bx * 10 + 10; x++)
                {
                    int index = y * Columns + x;
                    sum += Math.Abs(Gray[index] - other.Gray[index]);
                    n++;
                }
            blocks[by * 8 + bx] = sum / (Math.Max(1, n) * 255d);
        }
        // A few moving controls should not interrupt a stable frame. Retain a
        // contribution from localized content so a sparse page can still scroll.
        blocks.Sort();
        double mean = 0;
        foreach (double block in blocks[..88]) mean += block;
        double localChange = includeLocalChanges ? blocks[^1] : blocks[91];
        return mean / 88 + localChange * .15;
    }
}
public static class ScrollMatcher
{
    public static MatchMetadata Match(FrameSample a, FrameSample b, double sensitivity, CancellationToken token, int topOverride = -1, int bottomOverride = -1)
    {
        if (a.Height != b.Height || a.Height < 60) return new(0, 0, 0, 0, 0, Confidence.Low, 0, 0);
        int top = topOverride >= 0 ? topOverride : FixedBand(a, b, false);
        int bottom = bottomOverride >= 0 ? bottomOverride : FixedBand(a, b, true);
        var match = MatchOffsets(a, b, sensitivity, token, top, bottom);
        if (match.Confidence == Confidence.Low) return match;

        // A blank margin can agree both at the original position and after shifting.
        // Only use the stationary band where the confirmed shift disagrees with it.
        int confirmedTop = topOverride >= 0 ? top : ConfirmBand(a, b, top, match.Offset, false);
        int confirmedBottom = bottomOverride >= 0 ? bottom : ConfirmBand(a, b, bottom, match.Offset, true);
        if (confirmedTop == top && confirmedBottom == bottom) return match;
        return MatchOffsets(a, b, sensitivity, token, confirmedTop, confirmedBottom);
    }

    private static MatchMetadata MatchOffsets(FrameSample a, FrameSample b, double sensitivity, CancellationToken token, int top, int bottom)
    {
        long usableHeight = (long)a.Height - top - bottom;
        if (usableHeight <= 40) return new(0, 0, 0, 0, 0, Confidence.Low, top, bottom);
        int body = (int)usableHeight;
        int maxOffset = body - Math.Max(40, body / 6);

        int coarseStep = Math.Max(1, body / 80);
        var coarse = new Candidate[2 * maxOffset + 1];
        for (int offset = -maxOffset; offset <= maxOffset; offset++)
        {
            token.ThrowIfCancellationRequested();
            coarse[offset + maxOffset] = Cost(a, b, offset, top, bottom, coarseStep);
        }

        // Compare the forward match with zero and reverse motion. Rescore each
        // contender at the same density, including neighboring one-pixel shifts.
        var refined = new List<Candidate>();
        var seen = new bool[coarse.Length];
        void Refine(int center)
        {
            for (int offset = Math.Max(-maxOffset, center - 3); offset <= Math.Min(maxOffset, center + 3); offset++)
            {
                int index = offset + maxOffset;
                if (seen[index]) continue;
                token.ThrowIfCancellationRequested();
                seen[index] = true;
                refined.Add(coarseStep == 1 ? coarse[index] : Cost(a, b, offset, top, bottom, 1));
            }
        }
        Refine(0);
        int forward = 0, reverse = 0;
        foreach (var candidate in coarse.OrderBy(candidate => candidate.Error))
        {
            int offset = candidate.Offset;
            if (seen[offset + maxOffset]) continue;
            if (offset > 0 && forward < 8) { Refine(offset); forward++; }
            else if (offset < 0 && reverse < 5) { Refine(offset); reverse++; }
            if (forward == 8 && reverse == 5) break;
        }

        var best = refined.Where(candidate => candidate.Offset > 0).MinBy(candidate => candidate.Error);
        double secondError = refined.Where(candidate => candidate.Offset <= 0 || Math.Abs(candidate.Offset - best.Offset) >= 4).Min(candidate => candidate.Error);
        double neighborError = refined.Where(candidate => candidate.Offset > 0 && candidate.Offset != best.Offset && Math.Abs(candidate.Offset - best.Offset) < 4)
            .Select(candidate => candidate.Error).DefaultIfEmpty(double.PositiveInfinity).Min();
        double score = Math.Clamp(1 - best.Error / 35, 0, 1), second = Math.Clamp(1 - secondError / 35, 0, 1);
        double uniqueness = Math.Clamp((secondError - best.Error) / Math.Max(1.5, secondError), 0, 1);
        bool reliable = score >= sensitivity && uniqueness >= .20 && best.Texture >= 1.1 &&
            secondError - best.Error >= .20 && neighborError - best.Error >= .05;
        var confidence = !reliable ? Confidence.Low : score >= .92 && uniqueness >= .38 ? Confidence.High : Confidence.Medium;
        return new(best.Offset, score, second, uniqueness, best.Texture, confidence, top, bottom);
    }
    internal static bool FixedBandExpanded(FrameSample a, FrameSample b, int applied, int offset, bool bottom)
    {
        const int minimumGrowth = 4;
        int candidate = FixedBand(a, b, bottom);
        return candidate - applied >= minimumGrowth &&
            ConfirmBand(a, b, candidate, offset, bottom) - applied >= minimumGrowth;
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
        return last;
    }

    private static int ConfirmBand(FrameSample a, FrameSample b, int size, int offset, bool bottom)
    {
        const int width = FrameSample.Columns;
        int boundary = 0;
        for (int i = 0; i < size; i++)
        {
            int y = bottom ? a.Height - 1 - i : i;
            int shiftedA = bottom ? y : y + offset, shiftedB = bottom ? y - offset : y;
            if (shiftedA >= a.Height || shiftedB < 0) break;
            int stationary = 0, shifted = 0;
            for (int x = 0; x < width; x++)
            {
                stationary += Math.Abs(a.Gray[y * width + x] - b.Gray[y * width + x]);
                shifted += Math.Abs(a.Gray[shiftedA * width + x] - b.Gray[shiftedB * width + x]);
            }
            if (shifted >= 3 * width && shifted - stationary >= 2 * width) boundary = i + 1;
        }
        return boundary;
    }

    private readonly record struct Candidate(int Offset, double Error, double Texture);

    private static Candidate Cost(FrameSample a, FrameSample b, int offset, int top, int bottom, int step)
    {
        const int width = FrameSample.Columns;
        Span<double> errors = stackalloc double[8];
        double totalTexture = 0;
        int start = top + Math.Max(0, -offset) + 1;
        int end = a.Height - bottom - Math.Max(0, offset) - 1;
        int rowCount = (end - start + step - 1) / step;
        for (int band = 0; band < 8; band++)
        {
            int error = 0, texture = 0;
            for (int y = start; y < end; y += step)
            {
                for (int x = band * 10; x < band * 10 + 10; x += 2)
                {
                    int ai = (y + offset) * width + x, bi = y * width + x;
                    int difference = Math.Abs(a.Gray[ai] - b.Gray[bi]);
                    int da = a.Gray[ai + width] - a.Gray[ai - width], db = b.Gray[bi + width] - b.Gray[bi - width];
                    error += Math.Min(60, difference) * 3 + Math.Min(60, Math.Abs(da - db)) * 2;
                    texture += Math.Min(Math.Abs(da), Math.Abs(db));
                }
            }
            errors[band] = error / (rowCount * 25d);
            totalTexture += texture / (rowCount * 5d);
        }
        errors.Sort();
        double consistent = 0;
        foreach (double error in errors[..6]) consistent += error;
        return new(offset, consistent * (.85 / 6) + (errors[6] + errors[7]) * (.15 / 2), totalTexture / 8);
    }
}
