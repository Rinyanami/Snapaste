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
    public bool Background { get; set; }
    public int EffectSize { get; set; } = 12;
    private BitmapSource? effect;
    private Rect effectBounds;
    private int effectSize;
    public Annotation Clone() => new() { Id = Id, Kind = Kind, Bounds = Bounds, Points = [.. Points], Color = Color, Stroke = Stroke, Text = Text, Font = Font, FontSize = FontSize, Bold = Bold, Background = Background, EffectSize = EffectSize };
    public void Translate(Vector d) { Bounds = new Rect(Bounds.Location + d, Bounds.Size); for (int i = 0; i < Points.Count; i++) Points[i] += d; }
    public void Resize(Rect next)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(next.X + (Points[i].X - Bounds.X) * next.Width / Math.Max(1, Bounds.Width), next.Y + (Points[i].Y - Bounds.Y) * next.Height / Math.Max(1, Bounds.Height));
        Bounds = next;
    }
    public void Draw(DrawingContext dc, ImageDocument image)
    {
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
                var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily(Font), FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), FontSize, brush, 1);
                if (Background) dc.DrawRectangle(new SolidColorBrush(Colors.White) { Opacity = .9 }, null, new Rect(Bounds.Location, new Size(text.Width + 8, text.Height + 4)));
                dc.DrawText(text, Bounds.Location); break;
            case Tool.Mosaic:
            case Tool.Blur:
                var area = Rect.Intersect(Bounds, image.Bounds); if (area.Width < 1 || area.Height < 1) break;
                if (effect == null || effectBounds != area || effectSize != EffectSize)
                {
                    effect = Effects.Apply(image.Raster(area), Kind == Tool.Mosaic, EffectSize); effectBounds = area; effectSize = EffectSize;
                }
                dc.DrawImage(effect, area); break;
        }
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
                int right = Math.Min(w, x + size), bottom = Math.Min(h, y + size), count = (right - x) * (bottom - y); var sums = new int[4];
                for (int yy = y; yy < bottom; yy++) for (int xx = x; xx < right; xx++) for (int c = 0; c < 4; c++) sums[c] += data[yy * stride + xx * 4 + c];
                for (int yy = y; yy < bottom; yy++) for (int xx = x; xx < right; xx++) for (int c = 0; c < 4; c++) data[yy * stride + xx * 4 + c] = (byte)(sums[c] / count);
            }
        }
        else
        {
            int radius = Math.Min(30, size); double sigma = Math.Max(1, radius / 2.5); var kernel = new double[radius * 2 + 1]; double total = 0;
            for (int k = -radius; k <= radius; k++) total += kernel[k + radius] = Math.Exp(-k * k / (2 * sigma * sigma));
            for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
            var temp = new byte[data.Length];
            Parallel.For(0, h, y => { for (int x = 0; x < w; x++) for (int c = 0; c < 4; c++) { double s = 0; for (int k = -radius; k <= radius; k++) s += data[y * stride + Math.Clamp(x + k, 0, w - 1) * 4 + c] * kernel[k + radius]; temp[y * stride + x * 4 + c] = (byte)s; } });
            Parallel.For(0, h, y => { for (int x = 0; x < w; x++) for (int c = 0; c < 4; c++) { double s = 0; for (int k = -radius; k <= radius; k++) s += temp[Math.Clamp(y + k, 0, h - 1) * stride + x * 4 + c] * kernel[k + radius]; data[y * stride + x * 4 + c] = (byte)s; } });
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, stride); result.Freeze(); return result;
    }
}
public sealed record EditSnapshot(Rect Crop, List<Annotation> Annotations);
public sealed class EditDocument
{
    public ImageDocument Image { get; }
    public Rect Crop { get; set; }
    public List<Annotation> Annotations { get; private set; } = [];
    private readonly Stack<EditSnapshot> undo = new(), redo = new();
    public EditDocument(ImageDocument image) { Image = image; Crop = image.Bounds; }
    private EditSnapshot Snapshot() => new(Crop, Annotations.Select(a => a.Clone()).ToList());
    public void Checkpoint() { undo.Push(Snapshot()); redo.Clear(); }
    public void Undo() { if (undo.Count == 0) return; redo.Push(Snapshot()); Restore(undo.Pop()); }
    public void Redo() { if (redo.Count == 0) return; undo.Push(Snapshot()); Restore(redo.Pop()); }
    private void Restore(EditSnapshot s) { Crop = s.Crop; Annotations = s.Annotations; }
    public void Draw(DrawingContext dc, Rect visible)
    {
        dc.PushClip(new RectangleGeometry(Crop)); Image.Draw(dc, visible);
        foreach (var a in Annotations) { var b = a.Bounds; b.Inflate(Math.Max(40, a.Stroke * 5), Math.Max(40, a.FontSize * 3)); if (b.IntersectsWith(visible)) a.Draw(dc, Image); }
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
