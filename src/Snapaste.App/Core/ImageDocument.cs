namespace Snapaste.Core;

public sealed record ImageSegment(string Path, int SourceY, int Height, int DestinationY);
public sealed class ImageDocument
{
    private readonly BitmapSource? original;
    private readonly Dictionary<string, BitmapSource> cache = new();
    public IReadOnlyList<ImageSegment> Segments { get; }
    public int Width { get; }
    public int Height { get; }
    public string Kind { get; }
    public Rect Bounds => new(0, 0, Width, Height);
    public ImageDocument(BitmapSource source, string kind = "Capture")
    {
        source.Freeze(); original = source; Width = source.PixelWidth; Height = source.PixelHeight; Kind = kind; Segments = [];
    }
    public ImageDocument(int width, IReadOnlyList<ImageSegment> segments, string kind = "Scroll")
    { Width = width; Segments = segments.ToArray(); Height = segments.Count == 0 ? 0 : segments.Max(s => s.DestinationY + s.Height); Kind = kind; }
    public static ImageDocument Open(string path)
    {
        // A stored long PNG is decoded only for ordinary history/pin use with a bounded limit.
        var source = ReadBitmap(path); return new(source);
    }
    public ImageDocument Fork() => original == null ? new ImageDocument(Width, Segments, Kind) : new ImageDocument(original, Kind);
    public static BitmapSource ReadBitmap(string path, int previewWidth = 0)
    {
        var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (previewWidth > 0) bmp.DecodePixelWidth = previewWidth;
        using var stream = File.OpenRead(path); bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze(); return bmp;
    }
    public void Draw(DrawingContext dc, Rect visible)
    {
        if (original != null) { dc.DrawImage(original, Bounds); return; }
        foreach (var segment in Segments)
        {
            var rect = new Rect(0, segment.DestinationY, Width, segment.Height);
            if (!rect.IntersectsWith(visible)) continue;
            if (!cache.TryGetValue(segment.Path, out var source))
            {
                source = ReadBitmap(segment.Path);
                if (cache.Count >= 4) cache.Remove(cache.Keys.First());
                cache.Add(segment.Path, source);
            }
            var crop = new CroppedBitmap(source, new Int32Rect(0, segment.SourceY, Width, segment.Height)); crop.Freeze();
            dc.DrawImage(crop, rect);
        }
    }
    public BitmapSource Raster(Rect region, double scale = 1)
    {
        int w = Math.Max(1, (int)Math.Ceiling(region.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(region.Height * scale));
        if ((long)w * h > 64_000_000) throw new InvalidOperationException("图片太大，请保存为 PNG，或缩小/裁剪后复制和贴图。");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushTransform(new TranslateTransform(-region.X, -region.Y)); Draw(dc, region); }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual); bmp.Freeze(); return bmp;
    }
    public BitmapSource Preview(int maxWidth = 300, int maxHeight = 1800) => Raster(Bounds, Math.Min(1, Math.Min((double)maxWidth / Width, (double)maxHeight / Height)));
    public static void SaveBitmap(BitmapSource bitmap, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch { ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 94 }, ".bmp" => new BmpBitmapEncoder(), _ => new PngBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var f = File.Create(path); encoder.Save(f);
    }
    public static BitmapSource Detach(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4; var bytes = new byte[stride * converted.PixelHeight]; converted.CopyPixels(bytes, stride, 0);
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, converted.Format, null, bytes, stride); bitmap.Freeze(); return bitmap;
    }
}
