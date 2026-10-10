namespace Snapaste.UI;

// Generated locally from our line geometry, shared by the title bar and tray.
internal static class BrandIcon
{
    private static BitmapSource? source;
    public static BitmapSource Source
    {
        get
        {
            if (source != null) return source;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(119, 98, 186)), null, new Rect(0, 0, 32, 32), 8, 8);
                dc.PushTransform(new TranslateTransform(3, 3)); dc.PushTransform(new ScaleTransform(1.3, 1.3));
                dc.DrawGeometry(null, new System.Windows.Media.Pen(Brushes.White, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(Icons.Get("Capture")));
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return source = bitmap;
        }
    }
    public static System.Drawing.Icon Tray()
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Source)); using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(bytes.Length); writer.Write(22); writer.Write(bytes); writer.Flush(); stream.Position = 0;
        using var icon = new System.Drawing.Icon(stream); return (System.Drawing.Icon)icon.Clone();
    }
}
