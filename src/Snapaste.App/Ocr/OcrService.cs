using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using Snapaste.Core;
using Snapaste.Editor;

namespace Snapaste.Ocr;

public sealed record OcrLine(string Text, double Confidence, Rect Bounds);
public sealed record OcrRecognition(IReadOnlyList<OcrLine> Lines, int Width, int Height, string EngineName, long ElapsedMilliseconds);
public sealed record OcrProgress(string Message, int Completed, int Total);

public static class OcrService
{
    private const string EngineName = "PP-OCRv6 Small";
    private const int TileWidth = 2560;
    private const int TileHeight = 1280;
    private const int Overlap = 128;
    private const int MaxDetectorSide = TileWidth + 2 * Overlap;
    private const int EdgeTolerance = 8;
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private static readonly Lazy<nint> NativeRuntime = new(LoadNativeRuntime);
    private static RapidOcr? engine;

    public static async Task<OcrRecognition> RecognizeAsync(EditDocument snapshot, CancellationToken token, IProgress<OcrProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        Rect crop = snapshot.Crop;
        if (crop.IsEmpty || crop.Width <= 0 || crop.Height <= 0)
            throw new InvalidOperationException(OcrMessages.EmptySelection);

        int width = checked((int)Math.Ceiling(crop.Width));
        int height = checked((int)Math.Ceiling(crop.Height));
        int columns = (width - 1) / TileWidth + 1;
        int rows = (height - 1) / TileHeight + 1;
        int tileCount = checked(columns * rows);
        var timer = Stopwatch.StartNew();

        await InferenceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (engine == null)
            {
                progress?.Report(new(OcrMessages.Loading, 0, 0));
                engine = await Task.Run(LoadEngine, token).ConfigureAwait(false);
            }

            var tiles = new List<Candidate>[tileCount];
            for (int index = 0; index < tileCount; index++)
            {
                token.ThrowIfCancellationRequested();
                int column = index % columns, row = index / columns;
                var tile = GetTile(column, row, width, height);
                string label = tileCount == 1 ? OcrMessages.Progress : OcrMessages.TileProgress(index + 1, tileCount);
                progress?.Report(new(label + "…", index, tileCount));

                using var bitmap = await RenderWorker.Run(() => RenderTile(snapshot, crop, tile, token)).ConfigureAwait(false);
                var report = progress == null ? null : new RecognitionProgress(progress, label, index, tileCount);
                var result = await engine.DetectAsync(bitmap, DetectionOptions(tile), report, token).ConfigureAwait(false);
                var neighbors = AdjacentTiles(tiles, index, row, column, columns);
                tiles[index] = CollectLines(result, tile, width, height, neighbors, token);
                progress?.Report(new(OcrMessages.Complete, index + 1, tileCount));
            }

            token.ThrowIfCancellationRequested();
            IEnumerable<OcrLine> lines = tiles.SelectMany(tile => tile).Where(candidate => !candidate.Discarded).Select(candidate => candidate.Line);
            if (tileCount > 1) lines = lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left);
            return new(lines.ToArray(), width, height, EngineName, timer.ElapsedMilliseconds);
        }
        finally { InferenceGate.Release(); }
    }

    private static RapidOcr LoadEngine()
    {
        string modelFolder = Path.Combine(AppContext.BaseDirectory, "ocr-models");
        var models = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(modelFolder, "PP-OCRv6_det_small.onnx"),
            ClsModelPath = Path.Combine(modelFolder, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModelPath = Path.Combine(modelFolder, "PP-OCRv6_rec_small.onnx"),
            KeysPath = Path.Combine(modelFolder, "ppocrv6_small_dict.txt")
        };
        if (!File.Exists(models.DetModelPath) || !File.Exists(models.ClsModelPath) ||
            !File.Exists(models.RecModelPath) || !File.Exists(models.KeysPath))
            throw new InvalidOperationException(OcrMessages.MissingModel);

        var ocr = new RapidOcr();
        try
        {
            _ = NativeRuntime.Value;
            OrtEnv.Instance().DisableTelemetryEvents();
            using var options = RapidOcr.GetDefaultSessionOptions(Math.Clamp(Environment.ProcessorCount, 1, 4));
            options.EnableCpuMemArena = false;
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            ocr.InitModels(models, options);
            return ocr;
        }
        catch (Exception error)
        {
            ocr.Dispose();
            throw new InvalidOperationException(OcrMessages.LoadFailed, error);
        }
    }

    private static nint LoadNativeRuntime()
    {
        // Load the application-local runtime before resolving ONNX Runtime's native imports.
        foreach (string file in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll" })
            NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file));

        // Windows also ships onnxruntime.dll. Match the bundled native API to its managed binding.
        string path = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "onnxruntime.dll");
        var module = NativeLibrary.Load(path);
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                name.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase) || name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)
                    ? module : 0);
            return module;
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    private static RapidOcrOptions DetectionOptions(Int32Rect tile)
    {
        int shorter = Math.Min(tile.Width, tile.Height), longer = Math.Max(tile.Width, tile.Height);
        var options = RapidOcrOptions.PPOCRv6;
        // The library bounds the source before upscaling the shorter side. Cap that upscale too.
        int target = Math.Max(1, (int)((long)MaxDetectorSide * shorter / longer));
        return options with { MaxSideLen = MaxDetectorSide, MinSideLen = 1, LimitSideLen = Math.Min(options.LimitSideLen, target) };
    }

    private static Int32Rect GetTile(int column, int row, int width, int height)
    {
        int x = Math.Max(0, column * TileWidth - Overlap);
        int y = Math.Max(0, row * TileHeight - Overlap);
        int right = (int)Math.Min(width, (long)(column + 1) * TileWidth + Overlap);
        int bottom = (int)Math.Min(height, (long)(row + 1) * TileHeight + Overlap);
        return new(x, y, right - x, bottom - y);
    }

    private static SKBitmap RenderTile(EditDocument snapshot, Rect crop, Int32Rect tile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = snapshot.Raster(new Rect(crop.X + tile.X, crop.Y + tile.Y, tile.Width, tile.Height));
        token.ThrowIfCancellationRequested();

        // Raster composites on white; its premultiplied BGRA buffer is therefore opaque.
        var bitmap = new SKBitmap(tile.Width, tile.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try
        {
            nint pixels = bitmap.GetPixels();
            if (pixels == 0) throw new OutOfMemoryException(OcrMessages.Memory);
            source.CopyPixels(Int32Rect.Empty, pixels, bitmap.ByteCount, bitmap.RowBytes);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static IEnumerable<Candidate> AdjacentTiles(List<Candidate>[] tiles, int index, int row, int column, int columns)
    {
        if (column > 0)
            foreach (var candidate in tiles[index - 1]) yield return candidate;
        if (row == 0) yield break;
        for (int other = Math.Max(0, column - 1); other <= Math.Min(columns - 1, column + 1); other++)
            foreach (var candidate in tiles[(row - 1) * columns + other]) yield return candidate;
    }

    private static List<Candidate> CollectLines(OcrResult result, Int32Rect tile, int width, int height, IEnumerable<Candidate> neighbors, CancellationToken token)
    {
        var lines = new List<Candidate>();
        var adjacent = neighbors.Where(candidate => !candidate.Discarded).ToArray();
        foreach (var block in result.TextBlocks)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(block.Text) || block.BoxPoints.Length == 0) continue;
            var bounds = new Rect(new Point(block.BoxPoints.Min(p => p.X), block.BoxPoints.Min(p => p.Y)),
                new Point(block.BoxPoints.Max(p => p.X), block.BoxPoints.Max(p => p.Y)));
            bounds.Intersect(new Rect(0, 0, tile.Width, tile.Height));
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            bool clipped = (tile.X > 0 && bounds.Left <= EdgeTolerance) ||
                (tile.Y > 0 && bounds.Top <= EdgeTolerance) ||
                (tile.X + tile.Width < width && bounds.Right >= tile.Width - EdgeTolerance) ||
                (tile.Y + tile.Height < height && bounds.Bottom >= tile.Height - EdgeTolerance);
            bounds.Offset(tile.X, tile.Y);

            double confidence = block.CharScores is { Length: > 0 } scores ? scores.Average(value => (double)value) : block.BoxScore;
            confidence = double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 1) : 0;
            var candidate = new Candidate(new OcrLine(block.Text, confidence, bounds), clipped);
            var duplicate = adjacent.FirstOrDefault(previous => !previous.Discarded && Duplicate(previous.Line, candidate.Line));
            if (duplicate != null)
            {
                if (!Prefer(candidate, duplicate)) continue;
                duplicate.Discarded = true;
            }
            lines.Add(candidate);
        }
        return lines;
    }

    private static bool Duplicate(OcrLine first, OcrLine second)
    {
        Rect a = first.Bounds, b = second.Bounds;
        Rect overlap = Rect.Intersect(a, b);
        if (overlap.IsEmpty || overlap.Height < Math.Max(a.Height, b.Height) * .65) return false;
        double shorter = Math.Min(a.Width, b.Width), longer = Math.Max(a.Width, b.Width);
        return overlap.Width >= longer * .7 ||
            (overlap.Width >= shorter * .8 && (first.Text.Contains(second.Text, StringComparison.Ordinal) || second.Text.Contains(first.Text, StringComparison.Ordinal)));
    }

    private static bool Prefer(Candidate candidate, Candidate previous)
    {
        if (candidate.Clipped != previous.Clipped) return !candidate.Clipped;
        string next = candidate.Line.Text, prior = previous.Line.Text;
        if (next.Length > prior.Length && next.Contains(prior, StringComparison.Ordinal)) return true;
        if (prior.Length > next.Length && prior.Contains(next, StringComparison.Ordinal)) return false;
        return candidate.Line.Confidence > previous.Line.Confidence;
    }

    private sealed record Candidate(OcrLine Line, bool Clipped)
    {
        public bool Discarded { get; set; }
    }

    private sealed class RecognitionProgress(IProgress<OcrProgress> progress, string label, int tileIndex, int tileCount)
        : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) lines)
        {
            if (lines.Completed == 1 || lines.Completed == lines.Total || lines.Completed % 5 == 0)
                progress.Report(new(OcrMessages.LineProgress(label, lines.Completed, lines.Total), tileIndex, tileCount));
        }
    }
}
