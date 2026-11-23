namespace Snapaste.Capture;

/// <summary>Finds fully visible rectangular panel boundaries in a frozen desktop image.</summary>
public sealed class PixelRegionDetector
{
    private const int SampleBudget = 3_000_000;
    private const int MaxSampleSide = 4096;
    private const int MinContrast = 6;
    private const int MinEdgeLength = 24;
    private const int MaxEdges = 12_000;
    private const int BinSize = 96;
    private const int PointSlack = 18;
    private const int FaceLimit = 6;
    private const int ResultLimit = 8;

    // Position is refined in native pixels. Start and End describe the observed span of the edge.
    private readonly record struct Edge(int Position, int Start, int End, int Before, int After);

    private readonly Rect screen;
    private readonly int step;
    private readonly int sampleWidth;
    private readonly int[] colors;
    private readonly Edge[][] horizontal;
    private readonly Edge[][] vertical;

    private PixelRegionDetector(Rect screen, int step, int sampleWidth, int[] colors, Edge[][] horizontal, Edge[][] vertical)
    {
        this.screen = screen;
        this.step = step;
        this.sampleWidth = sampleWidth;
        this.colors = colors;
        this.horizontal = horizontal;
        this.vertical = vertical;
    }

    public static Task<PixelRegionDetector> CreateAsync(BitmapSource frozenImage, Rect screen, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frozenImage);
        if (!frozenImage.IsFrozen) throw new ArgumentException("The desktop bitmap must be frozen.", nameof(frozenImage));
        if (screen.IsEmpty || !double.IsFinite(screen.X) || !double.IsFinite(screen.Y) ||
            screen.Width != frozenImage.PixelWidth || screen.Height != frozenImage.PixelHeight)
            throw new ArgumentException("The physical screen bounds must match the desktop bitmap.", nameof(screen));
        return Task.Run(() => Build(frozenImage, screen, token), token);
    }

    /// <summary>Returns candidates containing the point, from smallest to largest, in physical screen coordinates.</summary>
    public IReadOnlyList<Rect> FindCandidates(Point screenPoint, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        double x = screenPoint.X - screen.X, y = screenPoint.Y - screen.Y;
        if (!(x >= 0 && x < screen.Width && y >= 0 && y < screen.Height) || horizontal.Length == 0 || vertical.Length == 0)
            return [];

        var (tops, bottoms) = NearFaces(horizontal[(int)x / BinSize], x, y, token);
        var (lefts, rights) = NearFaces(vertical[(int)y / BinSize], y, x, token);
        if (tops.Count == 0 || bottoms.Count == 0 || lefts.Count == 0 || rights.Count == 0) return [];

        // Consider outer faces first so stacked outlines do not repeatedly resample the same card.
        tops.Reverse();
        bottoms.Reverse();
        lefts.Reverse();
        rights.Reverse();

        var matches = new List<Rect>();
        foreach (var top in tops)
        {
            token.ThrowIfCancellationRequested();
            foreach (var bottom in bottoms)
            {
                int height = bottom.Position - top.Position;
                if (height < MinEdgeLength) continue;
                foreach (var left in lefts)
                {
                    if (!Fits(left, top.Position, bottom.Position)) continue;
                    foreach (var right in rights)
                    {
                        int width = right.Position - left.Position;
                        if (width < MinEdgeLength || !Fits(right, top.Position, bottom.Position) ||
                            !Fits(top, left.Position, right.Position) || !Fits(bottom, left.Position, right.Position)) continue;
                        if (Difference(top.After, bottom.Before) > 24 || Difference(top.After, left.After) > 24 ||
                            Difference(top.After, right.Before) > 24) continue;

                        var candidate = new Rect(left.Position, top.Position, width, height);
                        int similar = SimilarIndex(matches, candidate);
                        if (similar >= 0 && Area(candidate) <= Area(matches[similar])) continue;
                        if (!MostlyFlat(candidate) || AlternatingTexture(candidate)) continue;
                        if (similar < 0) matches.Add(candidate);
                        else matches[similar] = candidate;
                    }
                }
            }
        }

        return matches.OrderBy(Area).Take(ResultLimit)
            .Select(rect => new Rect(rect.X + screen.X, rect.Y + screen.Y, rect.Width, rect.Height)).ToArray();
    }

    private static PixelRegionDetector Build(BitmapSource source, Rect screen, CancellationToken token)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        int step = Math.Max(1, Math.Max((int)Math.Ceiling(Math.Sqrt((long)width * height / (double)SampleBudget)),
            (int)Math.Ceiling(Math.Max(width, height) / (double)MaxSampleSide)));
        int sampleWidth = (width - 1) / step + 1, sampleHeight = (height - 1) / step + 1;
        if (source.Format != PixelFormats.Bgr32 && source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            source.Freeze();
        }
        token.ThrowIfCancellationRequested();
        var allPixels = new int[checked(width * height)];
        source.CopyPixels(allPixels, checked(width * 4), 0);
        var colors = Sample(allPixels, width, height, step, sampleWidth, sampleHeight, token);
        var horizontal = Scan(allPixels, width, height, colors, step, sampleWidth, sampleHeight, true, token);
        var vertical = Scan(allPixels, width, height, colors, step, sampleWidth, sampleHeight, false, token);
        return new(screen, step, sampleWidth, colors, MakeBins(horizontal, width), MakeBins(vertical, height));
    }

    private static int[] Sample(int[] allPixels, int nativeWidth, int nativeHeight, int step, int width, int height, CancellationToken token)
    {
        if (step == 1)
        {
            token.ThrowIfCancellationRequested();
            return allPixels;
        }

        var colors = new int[checked(width * height)];
        for (int sy = 0; sy < height; sy++)
        {
            token.ThrowIfCancellationRequested();
            int y = sy * step, rowCount = Math.Min(step, nativeHeight - y);
            for (int sx = 0; sx < width; sx++)
            {
                int x = sx * step, columnCount = Math.Min(step, nativeWidth - x);
                int red = 0, green = 0, blue = 0;
                for (int dy = 0; dy < rowCount; dy++)
                {
                    int rowOffset = (y + dy) * nativeWidth + x;
                    for (int dx = 0; dx < columnCount; dx++)
                    {
                        int pixel = allPixels[rowOffset + dx];
                        red += Red(pixel);
                        green += Green(pixel);
                        blue += Blue(pixel);
                    }
                }
                int count = columnCount * rowCount;
                colors[sy * width + sx] = (red / count << 16) | (green / count << 8) | (blue / count);
            }
        }
        return colors;
    }

    private static List<Edge> Scan(int[] allPixels, int nativeWidth, int nativeHeight, int[] colors, int step, int width, int height, bool horizontal, CancellationToken token)
    {
        int lineCount = horizontal ? height : width, lineLength = horizontal ? width : height;
        int alongStride = horizontal ? 1 : width, normalStride = horizontal ? width : 1;
        int nativeLength = horizontal ? nativeWidth : nativeHeight;
        int minRun = Math.Max(5, (MinEdgeLength + step - 1) / step), maxGap = Math.Max(1, 4 / step);
        int sampleContrast = Math.Max(3, MinContrast / step);
        var result = new List<Edge>();
        var probes = new int[2 * step + 2];
        var votes = new int[2 * step + 1];
        var strengths = new int[votes.Length];
        for (int line = 1; line < lineCount; line++)
        {
            token.ThrowIfCancellationRequested();
            int start = -1, last = -1, count = 0, before = 0, after = 0, tolerance = 0;
            int offset = line * normalStride;
            for (int along = 0; along <= lineLength; along++)
            {
                bool inImage = along < lineLength;
                int index = offset + along * alongStride;
                int a = inImage ? colors[index - normalStride] : 0, b = inImage ? colors[index] : 0;
                int contrast = inImage ? Difference(a, b) : 0;
                bool hasStep = contrast >= sampleContrast;
                if (start >= 0 && hasStep && Difference(a, before) <= tolerance && Difference(b, after) <= tolerance &&
                    SameDirection(before, after, a, b))
                {
                    last = along;
                    count++;
                    continue;
                }
                if (start >= 0 && inImage && along - last <= maxGap) continue;
                if (start >= 0 && count >= minRun && count * 5 >= (last - start + 1) * 4)
                {
                    var edge = new Edge(line * step, start * step, Math.Min(nativeLength, (last + 1) * step), before, after);
                    int refined = step == 1 ? edge.Position : Refine(allPixels, nativeWidth, nativeHeight, edge, step, horizontal, probes, votes, strengths);
                    if (refined >= 0) result.Add(edge with { Position = refined });
                    if (result.Count > MaxEdges) return [];
                }

                start = hasStep ? along : -1;
                last = along;
                count = hasStep ? 1 : 0;
                before = a;
                after = b;
                tolerance = Math.Clamp(contrast / 5, 8, 18);
            }
        }
        return result;
    }

    private static int Refine(int[] allPixels, int nativeWidth, int nativeHeight, Edge edge, int step, bool horizontal, int[] probes, int[] votes, int[] strengths)
    {
        const int probeCount = 7;
        int nativeSize = horizontal ? nativeHeight : nativeWidth;
        int first = Math.Max(1, edge.Position - step), last = Math.Min(nativeSize - 1, edge.Position + step);
        int count = last - first + 2;
        Array.Clear(votes);
        Array.Clear(strengths);
        for (int sample = 0; sample < probeCount; sample++)
        {
            int along = edge.Start + (int)((long)(edge.End - edge.Start) * (sample + 1) / (probeCount + 1));
            if (horizontal)
            {
                int index = (first - 1) * nativeWidth + along;
                for (int i = 0; i < count; i++, index += nativeWidth) probes[i] = allPixels[index];
            }
            else
            {
                int index = along * nativeWidth + (first - 1);
                for (int i = 0; i < count; i++) probes[i] = allPixels[index + i];
            }
            for (int i = 0; i < count - 1; i++)
            {
                int contrast = Difference(probes[i], probes[i + 1]);
                if (contrast < MinContrast || !SameDirection(edge.Before, edge.After, probes[i], probes[i + 1])) continue;
                votes[i]++;
                strengths[i] += contrast;
            }
        }

        int best = -1;
        for (int i = 0; i < count - 1; i++)
        {
            if (votes[i] < 5) continue;
            if (best < 0 || votes[i] > votes[best] || (votes[i] == votes[best] && strengths[i] > strengths[best])) best = i;
        }
        return best < 0 ? -1 : first + best;
    }

    private static Edge[][] MakeBins(List<Edge> edges, int nativeSize)
    {
        if (edges.Count == 0) return [];
        var bins = new List<Edge>[(nativeSize - 1) / BinSize + 1];
        foreach (var edge in edges)
        {
            int first = Math.Max(0, edge.Start - PointSlack) / BinSize;
            int last = Math.Min(nativeSize - 1, edge.End + PointSlack) / BinSize;
            for (int i = first; i <= last; i++) (bins[i] ??= []).Add(edge);
        }
        return bins.Select(bin => bin?.ToArray() ?? []).ToArray();
    }

    private static (List<Edge> Before, List<Edge> After) NearFaces(Edge[] bin, double along, double across, CancellationToken token)
    {
        var before = new List<Edge>(FaceLimit);
        var after = new List<Edge>(FaceLimit);
        for (int i = 0; i < bin.Length; i++)
        {
            if ((i & 63) == 0) token.ThrowIfCancellationRequested();
            var edge = bin[i];
            if (along < edge.Start - PointSlack || along > edge.End + PointSlack) continue;
            bool firstSide = edge.Position <= across;
            var faces = firstSide ? before : after;
            int index = 0;
            while (index < faces.Count && (firstSide ? faces[index].Position > edge.Position : faces[index].Position < edge.Position)) index++;
            if (index == FaceLimit) continue;
            if (index < faces.Count && faces[index].Position == edge.Position)
            {
                if (edge.End - edge.Start > faces[index].End - faces[index].Start) faces[index] = edge;
                continue;
            }
            if (faces.Count == FaceLimit) faces.RemoveAt(FaceLimit - 1);
            faces.Insert(index, edge);
        }
        return (before, after);
    }

    private bool Fits(Edge edge, int start, int end)
    {
        // Rounded cards can lose short segments at their corners. Long overhangs do not close a box.
        int slack = Math.Clamp((end - start) / 12, 5, 22) + step;
        return Math.Abs(edge.Start - start) <= slack && Math.Abs(edge.End - end) <= slack;
    }

    private bool MostlyFlat(Rect rect)
    {
        const int size = 5;
        Span<int> sample = stackalloc int[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = (int)(rect.Left + rect.Width * (x + 1) / (size + 1));
                int sy = (int)(rect.Top + rect.Height * (y + 1) / (size + 1));
                sample[y * size + x] = ColorAt(sx, sy);
            }
        foreach (int color in sample)
        {
            int similar = 0;
            foreach (int other in sample)
                if (Difference(color, other) <= 14 && ++similar >= 15) return true;
        }
        return false;
    }

    private bool AlternatingTexture(Rect rect)
    {
        // An alternating tile has the opposite color on every side and its own color on every diagonal.
        int padding = Math.Max(2 * step, (int)Math.Min(12, Math.Min(rect.Width, rect.Height) / 5));
        int left = (int)rect.Left - padding, right = (int)rect.Right + padding;
        int top = (int)rect.Top - padding, bottom = (int)rect.Bottom + padding;
        if (left < 0 || top < 0 || right >= screen.Width || bottom >= screen.Height) return false;
        int x = (int)(rect.Left + rect.Width / 2), y = (int)(rect.Top + rect.Height / 2);
        int center = ColorAt(x, y), side = ColorAt(x, top);
        return Difference(center, side) >= 32 && Difference(side, ColorAt(x, bottom)) <= 14 &&
            Difference(side, ColorAt(left, y)) <= 14 && Difference(side, ColorAt(right, y)) <= 14 &&
            Difference(center, ColorAt(left, top)) <= 14 && Difference(center, ColorAt(right, top)) <= 14 &&
            Difference(center, ColorAt(left, bottom)) <= 14 && Difference(center, ColorAt(right, bottom)) <= 14;
    }

    private int ColorAt(int x, int y) => colors[(y / step) * sampleWidth + x / step];

    private int SimilarIndex(List<Rect> matches, Rect candidate)
    {
        int slack = step + 2;
        for (int i = 0; i < matches.Count; i++)
        {
            Rect match = matches[i];
            if (Math.Abs(match.Left - candidate.Left) <= slack && Math.Abs(match.Top - candidate.Top) <= slack &&
                Math.Abs(match.Right - candidate.Right) <= slack && Math.Abs(match.Bottom - candidate.Bottom) <= slack) return i;
        }
        return -1;
    }

    private static double Area(Rect rect) => rect.Width * rect.Height;
    private static int Red(int color) => (color >> 16) & 255;
    private static int Green(int color) => (color >> 8) & 255;
    private static int Blue(int color) => color & 255;

    private static int Difference(int a, int b) => Math.Max(Math.Abs(Red(a) - Red(b)),
        Math.Max(Math.Abs(Green(a) - Green(b)), Math.Abs(Blue(a) - Blue(b))));

    private static bool SameDirection(int before, int after, int a, int b) =>
        (Red(after) - Red(before)) * (Red(b) - Red(a)) + (Green(after) - Green(before)) * (Green(b) - Green(a)) +
        (Blue(after) - Blue(before)) * (Blue(b) - Blue(a)) > 0;
}
