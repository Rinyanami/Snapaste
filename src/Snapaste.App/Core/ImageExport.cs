using System.Buffers.Binary;
using System.IO.Compression;
using Snapaste.Editor;
namespace Snapaste.Core;

public static class ImageExport
{
    public static void Save(EditDocument document, string path)
    {
        var temp = path + ".snapaste-tmp";
        try
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var file = File.Create(temp); PngStream.Write(document, file);
            }
            else
            {
                var bitmap = document.Raster(document.Crop);
                BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? new JpegBitmapEncoder { QualityLevel = 94 } : new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(temp); encoder.Save(file);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool SaveDialog(EditDocument document, Settings settings, Window? owner = null)
    {
        var ext = settings.DefaultFormat.ToLowerInvariant() == "jpeg" ? "jpg" : settings.DefaultFormat.ToLowerInvariant();
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}.{ext}", Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp", FilterIndex = ext == "jpg" ? 2 : ext == "bmp" ? 3 : 1, InitialDirectory = Directory.Exists(settings.SaveDirectory) ? settings.SaveDirectory : "", AddExtension = true };
        if (dialog.ShowDialog(owner) != true) return false;
        Save(document, dialog.FileName); return true;
    }
    public static async Task Copy(EditDocument document)
    {
        var bitmap = document.Raster(document.Crop);
        for (int i = 0; ; i++)
        {
            try { Clipboard.SetImage(bitmap); return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 4) { await Task.Delay(60); }
        }
    }
}
// PNG is emitted strip by strip; memory is independent of final image height.
internal static class PngStream
{
    public static void Write(EditDocument doc, Stream destination)
    {
        int width = (int)doc.Crop.Width, height = (int)doc.Crop.Height;
        destination.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2; Chunk(destination, "IHDR", header);
        using (var chunks = new IdatStream(destination))
        using (var zip = new ZLibStream(chunks, CompressionLevel.Fastest, true))
        {
            var rgb = new byte[width * 3 + 1];
            for (int y = 0; y < height; y += 512)
            {
                int rows = Math.Min(512, height - y); var bitmap = doc.Raster(new Rect(doc.Crop.X, doc.Crop.Y + y, width, rows));
                var pixels = new byte[width * rows * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                for (int row = 0; row < rows; row++) { for (int x = 0; x < width; x++) { int p = (row * width + x) * 4, q = x * 3 + 1; rgb[q] = pixels[p + 2]; rgb[q + 1] = pixels[p + 1]; rgb[q + 2] = pixels[p]; } zip.Write(rgb); }
            }
        }
        Chunk(destination, "IEND", []);
    }
    private static void Chunk(Stream stream, string kind, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); stream.Write(length);
        var type = System.Text.Encoding.ASCII.GetBytes(kind); stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff; foreach (var b in type) crc = Crc(crc, b); foreach (var b in data) crc = Crc(crc, b); BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xffffffff); stream.Write(length);
    }
    private static uint Crc(uint crc, byte b) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); return crc; }
    private sealed class IdatStream(Stream target) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { while (!buffer.IsEmpty) { int n = Math.Min(65536, buffer.Length); Chunk(target, "IDAT", buffer[..n]); buffer = buffer[n..]; } }
        public override void Flush() => target.Flush(); public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    }
}
