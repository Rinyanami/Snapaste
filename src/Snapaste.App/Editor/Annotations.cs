using System.Globalization;
using Snapaste.Core;
namespace Snapaste.Editor;

public enum Tool { Select, Rectangle, Ellipse, Line, Arrow, Pen, Highlighter, Text, Mosaic, Blur, Crop, Color }
public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Tool Kind { get; set; }
    public Rect Bounds { get; set; }
    public List<Point> Points { get; set; } = [];
    public Color Color { get; set; } = Colors.Red;
    public double Stroke { get; set; } = 3;
    public string Text { get; set; } = "";
    public string Font { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 24;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public double TextIndent { get; set; }
    public bool Background { get; set; }
    public double TextWidth { get; set; }
    public double Rotation { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public int EffectSize { get; set; } = 12;
    private BitmapSource? effect;
    private Rect effectBounds;
    private int effectSize;
    private bool effectPreview, effectBusy;
    private (Rect Area, int Size)? failedPreview;
    private static readonly SemaphoreSlim PreviewWorkers = new(1, 1);
    public Annotation Clone() => new()
    {
        Id = Id, Kind = Kind, Bounds = Bounds, Points = [.. Points], Color = Color, Stroke = Stroke,
        Text = Text, Font = Font, FontSize = FontSize, Bold = Bold, Italic = Italic, Underline = Underline,
        Alignment = Alignment, TextIndent = TextIndent, Background = Background, TextWidth = TextWidth,
        Rotation = Rotation, FlipX = FlipX, FlipY = FlipY, EffectSize = EffectSize
    };

    public Matrix Transform
    {
        get
        {
            var center = new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
            var matrix = Matrix.Identity;
            matrix.ScaleAt(FlipX ? -1 : 1, FlipY ? -1 : 1, center.X, center.Y);
            matrix.RotateAt(Rotation, center.X, center.Y); return matrix;
        }
    }
    public Point ToWorld(Point point) => Transform.Transform(point);
    public Point ToLocal(Point point) { var inverse = Transform; inverse.Invert(); return inverse.Transform(point); }
    public Rect VisualBounds => new MatrixTransform(Transform).TransformBounds(Bounds);
    private double FixedTextWidth => double.IsFinite(TextWidth) && TextWidth > 0 ? Math.Max(1, TextWidth) : 0;

    public double TextInset
    {
        get
        {
            double indent = double.IsFinite(TextIndent) ? Math.Max(0, TextIndent) : 0;
            return FixedTextWidth > 0 ? Math.Min(indent, FixedTextWidth - 1) : indent;
        }
    }

    public FormattedText FormatText(Brush brush) => LayoutText(brush).Text;

    private (FormattedText Text, double Width) LayoutText(Brush brush)
    {
        var typeface = new Typeface(new FontFamily(Font), Italic ? FontStyles.Italic : FontStyles.Normal,
            Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, brush, 1)
        {
            Trimming = TextTrimming.None
        };
        if (Underline) text.SetTextDecorations(TextDecorations.Underline);

        double inset = TextInset;
        double width = FixedTextWidth;
        // Give centered and right-aligned automatic text the same frame as
        // left-aligned text, based on the longest line's unaligned width.
        if (width == 0) width = inset + Math.Max(1, Math.Ceiling(text.WidthIncludingTrailingWhitespace));
        text.MaxTextWidth = Math.Max(1, width - inset);
        text.TextAlignment = Alignment;
        return (text, width);
    }

    public void MeasureText()
    {
        // Editing/reflow grows along the object's own axes. Keep its visual
        // top-left fixed even though the bounds-based rotation pivot changes.
        var origin = ToWorld(Bounds.TopLeft);
        var (text, width) = LayoutText(Brushes.Black);
        Bounds = new Rect(Bounds.Location, new Size(width, Math.Max(1, text.Height)));
        Translate(origin - ToWorld(Bounds.TopLeft));
    }
    public bool HitTest(Point point, double tolerance)
    {
        point = ToLocal(point);
        double strokeExtent = Kind == Tool.Highlighter ? Stroke * 2.5 : Kind == Tool.Arrow ? Stroke * 2 : Stroke / 2;
        var bounds = Bounds; bounds.Inflate(tolerance + strokeExtent, tolerance + strokeExtent);
        if (!bounds.Contains(point)) return false;
        if (Kind == Tool.Rectangle)
        {
            var interior = Bounds; interior.Inflate(-Math.Min(Bounds.Width / 2, tolerance + Stroke / 2), -Math.Min(Bounds.Height / 2, tolerance + Stroke / 2));
            return !interior.Contains(point);
        }
        if (Kind == Tool.Ellipse && Bounds.Width > 0 && Bounds.Height > 0)
        {
            double x = (point.X - Bounds.X - Bounds.Width / 2) / (Bounds.Width / 2), y = (point.Y - Bounds.Y - Bounds.Height / 2) / (Bounds.Height / 2);
            return Math.Abs(Math.Sqrt(x * x + y * y) - 1) * Math.Min(Bounds.Width, Bounds.Height) / 2 <= tolerance + Stroke / 2;
        }
        if (Kind is Tool.Line or Tool.Arrow or Tool.Pen or Tool.Highlighter)
        {
            double distance = tolerance + Stroke * (Kind == Tool.Highlighter ? 2.5 : Kind == Tool.Arrow ? 2 : .5);
            for (int i = 1; i < Points.Count; i++)
            {
                var segment = Points[i] - Points[i - 1]; double t = segment.LengthSquared < .001 ? 0 : Math.Clamp(Vector.Multiply(point - Points[i - 1], segment) / segment.LengthSquared, 0, 1);
                if ((point - (Points[i - 1] + segment * t)).Length <= distance) return true;
            }
            return false;
        }
        return true;
    }
    // Selection can use the interior of a closed shape without changing the
    // stroke-only hit test used when drawing inside an unselected shape.
    public bool ContainsInterior(Point point)
    {
        point = ToLocal(point);
        if (!Bounds.Contains(point)) return false;
        if (Kind == Tool.Rectangle) return true;
        if (Kind != Tool.Ellipse || Bounds.Width <= 0 || Bounds.Height <= 0) return false;
        double x = (point.X - Bounds.X - Bounds.Width / 2) / (Bounds.Width / 2);
        double y = (point.Y - Bounds.Y - Bounds.Height / 2) / (Bounds.Height / 2);
        return x * x + y * y <= 1;
    }
    public void Translate(Vector d) { Bounds = new Rect(Bounds.Location + d, Bounds.Size); for (int i = 0; i < Points.Count; i++) Points[i] += d; }
    public void Resize(Rect next)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(next.X + (Points[i].X - Bounds.X) * next.Width / Math.Max(1, Bounds.Width), next.Y + (Points[i].Y - Bounds.Y) * next.Height / Math.Max(1, Bounds.Height));
        Bounds = next;
    }
    public void Draw(DrawingContext dc, ImageDocument image, Action? previewReady = null)
    {
        if (Kind is Tool.Mosaic or Tool.Blur) { DrawEffect(dc, image, previewReady); return; }
        dc.PushTransform(new MatrixTransform(Transform));
        var brush = new SolidColorBrush(Color); var pen = new System.Windows.Media.Pen(brush, Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (Kind)
        {
            case Tool.Rectangle: dc.DrawRectangle(null, pen, Bounds); break;
            case Tool.Ellipse: dc.DrawEllipse(null, pen, new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2), Bounds.Width / 2, Bounds.Height / 2); break;
            case Tool.Line:
            case Tool.Arrow:
                if (Points.Count < 2) break;
                var a = Points[0]; var b = Points[^1]; dc.DrawLine(pen, a, b);
                if (Kind == Tool.Arrow && (b - a).Length > 1)
                {
                    var direction = a - b; direction.Normalize(); var normal = new Vector(-direction.Y, direction.X); double size = Math.Max(12, Stroke * 4);
                    var head = new StreamGeometry(); using (var c = head.Open()) { c.BeginFigure(b, true, true); c.LineTo(b + direction * size + normal * size * .4, true, false); c.LineTo(b + direction * size - normal * size * .4, true, false); } dc.DrawGeometry(brush, null, head);
                }
                break;
            case Tool.Pen:
            case Tool.Highlighter:
                if (Points.Count < 2) break;
                if (Kind == Tool.Highlighter) { brush = new SolidColorBrush(Color) { Opacity = .32 }; pen = new(brush, Stroke * 5) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }; }
                var geometry = new StreamGeometry(); using (var c = geometry.Open()) { c.BeginFigure(Points[0], false, false); c.PolyLineTo(Points.Skip(1).ToList(), true, false); } dc.DrawGeometry(null, pen, geometry); break;
            case Tool.Text:
                var text = FormatText(brush);
                if (Background) dc.DrawRectangle(new SolidColorBrush(Colors.White) { Opacity = .9 }, null, Bounds);
                dc.DrawText(text, new Point(Bounds.X + TextInset, Bounds.Y)); break;
        }
        dc.Pop();
    }
    private void DrawEffect(DrawingContext dc, ImageDocument image, Action? previewReady)
    {
        // Transform the mask, not the underlying screenshot: rotating a blur
        // must never paste a rotated copy of unrelated pixels over the target.
        var area = Rect.Intersect(VisualBounds, image.Bounds);
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) return;
        bool valid = effect != null && effectBounds == area && effectSize == EffectSize;
        if (previewReady == null && (!valid || effectPreview))
        {
            effect = Effects.Apply(image.Raster(area), Kind == Tool.Mosaic, EffectSize);
            effectBounds = area; effectSize = EffectSize; effectPreview = false; valid = true;
        }
        else if (!valid && !effectBusy && failedPreview != (area, EffectSize) && previewReady != null)
            _ = PreparePreview(image, area, EffectSize, previewReady);

        dc.PushClip(new RectangleGeometry(Bounds) { Transform = new MatrixTransform(Transform) });
        if (valid) dc.DrawImage(effect, area);
        else
        {
            // An explicit placeholder is safer than showing an old, displaced
            // redaction. Exports always compute the full-resolution effect.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(58, 54, 66)), null, area);
            var label = new FormattedText(UI.Strings.T(failedPreview == (area, EffectSize) ? "EffectPreviewFailed" : "EffectPreviewWorking"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.White, 1);
            var center = ToWorld(new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2));
            dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
        }
        dc.Pop();
    }
    private async Task PreparePreview(ImageDocument image, Rect area, int size, Action ready)
    {
        effectBusy = true;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        BitmapSource? bitmap = null;
        try
        {
            // Use exactly the export pixels and radius. Downsampling can make a
            // privacy mask look stronger than it will be in the saved image.
            // A separate image cache and STA worker also keep WPF rasterization
            // off the input thread. Allocate only after taking the worker slot.
            if (area.Width * area.Height > 16_000_000) throw new InvalidOperationException(UI.Strings.T("EffectPreviewFailed"));
            var snapshot = image.Fork();
            bool mosaic = Kind == Tool.Mosaic;
            await PreviewWorkers.WaitAsync().ConfigureAwait(false);
            try
            {
                bitmap = await RenderWorker.Run(() => Effects.Apply(snapshot.Raster(area), mosaic, size)).ConfigureAwait(false);
            }
            finally { PreviewWorkers.Release(); }
        }
        catch (Exception e)
        {
            Log.Error("Annotation preview", e);
        }
        if (dispatcher.HasShutdownStarted) return;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (bitmap != null) { effect = bitmap; effectBounds = area; effectSize = size; effectPreview = true; failedPreview = null; }
                else failedPreview = (area, size);
                effectBusy = false; ready();
            });
        }
        catch (OperationCanceledException) { /* Dispatcher shut down while work was in flight. */ }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
    }
}
public static class Effects
{
    public static BitmapSource Apply(BitmapSource source, bool mosaic, int size)
    {
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4; var data = new byte[stride * h]; source.CopyPixels(data, stride, 0); size = Math.Clamp(size, 2, 64);
        if (mosaic)
        {
            for (int y = 0; y < h; y += size) for (int x = 0; x < w; x += size)
            {
                int right = Math.Min(w, x + size), bottom = Math.Min(h, y + size), count = (right - x) * (bottom - y);
                int blue = 0, green = 0, red = 0, alpha = 0;
                for (int yy = y; yy < bottom; yy++) for (int xx = x; xx < right; xx++) { int p = yy * stride + xx * 4; blue += data[p]; green += data[p + 1]; red += data[p + 2]; alpha += data[p + 3]; }
                for (int yy = y; yy < bottom; yy++) for (int xx = x; xx < right; xx++) { int p = yy * stride + xx * 4; data[p] = (byte)(blue / count); data[p + 1] = (byte)(green / count); data[p + 2] = (byte)(red / count); data[p + 3] = (byte)(alpha / count); }
            }
        }
        else
        {
            int radius = Math.Min(30, size); double sigma = Math.Max(1, radius / 2.5); var kernel = new double[radius * 2 + 1]; double total = 0;
            for (int k = -radius; k <= radius; k++) total += kernel[k + radius] = Math.Exp(-k * k / (2 * sigma * sigma));
            for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
            var temp = new byte[data.Length];
            Parallel.For(0, h, y =>
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    double b = 0, g = 0, r = 0, a = 0;
                    for (int k = -radius; k <= radius; k++) { int p = row + Math.Clamp(x + k, 0, w - 1) * 4; double weight = kernel[k + radius]; b += data[p] * weight; g += data[p + 1] * weight; r += data[p + 2] * weight; a += data[p + 3] * weight; }
                    int q = row + x * 4; temp[q] = (byte)b; temp[q + 1] = (byte)g; temp[q + 2] = (byte)r; temp[q + 3] = (byte)a;
                }
            });
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double b = 0, g = 0, r = 0, a = 0;
                    for (int k = -radius; k <= radius; k++) { int p = Math.Clamp(y + k, 0, h - 1) * stride + x * 4; double weight = kernel[k + radius]; b += temp[p] * weight; g += temp[p + 1] * weight; r += temp[p + 2] * weight; a += temp[p + 3] * weight; }
                    int q = y * stride + x * 4; data[q] = (byte)b; data[q + 1] = (byte)g; data[q + 2] = (byte)r; data[q + 3] = (byte)a;
                }
            });
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, stride); result.Freeze(); return result;
    }
}
public sealed record EditSnapshot(Rect Crop, List<Annotation> Annotations)
{
    public long EstimatedBytes { get; } = 64 + Annotations.Sum(a => 256L + a.Points.Count * 16L + a.Text.Length * 2L + a.Font.Length * 2L);
}
public sealed class EditDocument
{
    public ImageDocument Image { get; }
    public Rect Crop { get; set; }
    public List<Annotation> Annotations { get; private set; } = [];
    private readonly Stack<EditSnapshot> undo = new(), redo = new();
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public EditDocument(ImageDocument image) { Image = image; Crop = image.Bounds; }
    public EditDocument Fork() => new(Image.Fork()) { Crop = Crop, Annotations = Annotations.Select(a => a.Clone()).ToList() };
    private EditSnapshot Snapshot() => new(Crop, Annotations.Select(a => a.Clone()).ToList());
    internal EditSnapshot CaptureState() => Snapshot();
    internal void CommitState(EditSnapshot before) { PushHistory(undo, before); redo.Clear(); }
    // Long freehand sessions must not retain an unbounded quadratic number of points.
    // Keep at least the latest state, up to 100 actions / 48 MiB per direction.
    private static void PushHistory(Stack<EditSnapshot> stack, EditSnapshot snapshot)
    {
        stack.Push(snapshot); long bytes = 0; int keep = 0;
        foreach (var item in stack)
        {
            if (keep > 0 && (keep >= 100 || bytes + item.EstimatedBytes > 48L * 1024 * 1024)) break;
            bytes += item.EstimatedBytes; keep++;
        }
        if (keep == stack.Count) return;
        var retained = stack.Take(keep).Reverse().ToArray(); stack.Clear(); foreach (var item in retained) stack.Push(item);
    }
    public void Checkpoint() { PushHistory(undo, Snapshot()); redo.Clear(); }
    public void Undo() { if (undo.Count == 0) return; PushHistory(redo, Snapshot()); Restore(undo.Pop()); }
    public void Redo() { if (redo.Count == 0) return; PushHistory(undo, Snapshot()); Restore(redo.Pop()); }
    private void Restore(EditSnapshot s) { Crop = s.Crop; Annotations = s.Annotations; }
    public void Draw(DrawingContext dc, Rect visible, Action? previewReady = null)
    {
        dc.PushClip(new RectangleGeometry(Crop)); Image.Draw(dc, visible);
        DrawAnnotations(dc, visible, previewReady: previewReady);
        dc.Pop();
    }
    public void DrawAnnotations(DrawingContext dc, Rect visible, Annotation? hidden = null, Action? previewReady = null)
    {
        dc.PushClip(new RectangleGeometry(Crop));
        foreach (var a in Annotations) { if (a == hidden) continue; var b = a.VisualBounds; b.Inflate(Math.Max(40, a.Stroke * 5), Math.Max(40, a.FontSize * 3)); if (b.IntersectsWith(visible)) a.Draw(dc, Image, previewReady); }
        dc.Pop();
    }
    public BitmapSource Raster(Rect rect, double scale = 1)
    {
        int w = Math.Max(1, (int)Math.Ceiling(rect.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(rect.Height * scale));
        if ((long)w * h > 64_000_000) throw new InvalidOperationException("图片超过复制/贴图内存限制，请保存 PNG 或先裁剪。");
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new(0, 0, w, h)); dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushTransform(new TranslateTransform(-rect.X, -rect.Y)); Draw(dc, rect); }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
