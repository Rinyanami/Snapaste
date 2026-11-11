using System.Buffers.Binary;
using System.IO.Compression;
using Snapaste.Editor;
namespace Snapaste.Core;

public static class ImageExport
{
    public static void Save(EditDocument document, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                AtomicFile.Write(path, file => PngStream.Write(document, file));
                break;
            case ".jpg":
            case ".jpeg":
            case ".bmp":
                ImageDocument.SaveBitmap(document.Raster(document.Crop), path);
                break;
            default:
                throw new ArgumentException("请选择 PNG、JPEG 或 BMP 格式。", nameof(path));
        }
    }

    private static string? PickPath(Settings settings, Window? owner)
    {
        var ext = settings.DefaultFormat?.ToUpperInvariant() switch { "JPEG" or "JPG" => "jpg", "BMP" => "bmp", _ => "png" };
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}",
            DefaultExt = "." + ext,
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            FilterIndex = ext == "jpg" ? 2 : ext == "bmp" ? 3 : 1,
            InitialDirectory = Directory.Exists(settings.SaveDirectory) ? settings.SaveDirectory : "",
            AddExtension = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public static bool SaveDialog(EditDocument document, Settings settings, Window? owner = null)
    {
        var path = PickPath(settings, owner);
        if (path == null) return false;
        Save(document, path);
        return true;
    }

    public static async Task<bool> SaveDialogAsync(EditDocument document, Settings settings, Window owner)
    {
        var path = PickPath(settings, owner);
        if (path == null) return false;
        var snapshot = document.Fork();
        bool wasEnabled = owner.IsEnabled;
        owner.IsEnabled = false;
        try
        {
            await RenderWorker.Run(() => Save(snapshot, path));
            return true;
        }
        finally { owner.IsEnabled = wasEnabled; }
    }

    public static async Task Copy(EditDocument document)
    {
        var snapshot = document.Fork();
        var bitmap = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext
            ? await RenderWorker.Run(() => snapshot.Raster(snapshot.Crop))
            : snapshot.Raster(snapshot.Crop);
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
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static void Write(EditDocument doc, Stream destination)
    {
        int width = checked((int)doc.Crop.Width), height = checked((int)doc.Crop.Height);
        if (width <= 0 || height <= 0) throw new ArgumentException("图片尺寸无效。", nameof(doc));

        destination.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        Chunk(destination, "IHDR"u8, header);

        using (var chunks = new IdatStream(destination))
        using (var zip = new ZLibStream(chunks, CompressionLevel.Fastest, true))
        {
            int stride = checked(width * 4);
            int stripHeight = Math.Min(height, Math.Clamp(8 * 1024 * 1024 / stride, 1, 512));
            var pixels = new byte[checked(stride * stripHeight)];
            var rgb = new byte[checked(width * 3 + 1)];
            for (int y = 0; y < height;)
            {
                int rows = Math.Min(stripHeight, height - y);
                var bitmap = doc.Raster(new Rect(doc.Crop.X, doc.Crop.Y + y, width, rows));
                bitmap.CopyPixels(pixels, stride, 0);
                for (int row = 0; row < rows; row++)
                {
                    int source = row * stride;
                    for (int x = 1; x < rgb.Length; x += 3, source += 4)
                    {
                        rgb[x] = pixels[source + 2];
                        rgb[x + 1] = pixels[source + 1];
                        rgb[x + 2] = pixels[source];
                    }
                    zip.Write(rgb);
                }
                y += rows;
            }
        }
        Chunk(destination, "IEND"u8, []);
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xffffffff);
        stream.Write(number);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            table[i] = crc;
        }
        return table;
    }

    private sealed class IdatStream(Stream target) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int n = Math.Min(65536, buffer.Length);
                Chunk(target, "IDAT"u8, buffer[..n]);
                buffer = buffer[n..];
            }
        }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
