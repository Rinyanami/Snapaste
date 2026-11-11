using System.Text.Json;
using Snapaste.Editor;
namespace Snapaste.Core;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "Capture";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<ImageSegment> Segments { get; set; } = [];
    public string DirectoryPath => Path.Combine(Paths.History, Id);
    public string Thumbnail => Path.Combine(DirectoryPath, "thumb.png");
    public ImageDocument Open() => HistoryStore.Open(this);
}

public sealed class HistoryStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WeakReference<object>> OpenImages = new(StringComparer.OrdinalIgnoreCase);
    private const string Manifest = "entry.json";
    private const string PendingPrefix = ".pending-";

    public List<HistoryEntry> Entries(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadEntries(Directories(), cancellationToken);
        }
    }

    private static DirectoryInfo[] Directories() => new DirectoryInfo(Paths.History).EnumerateDirectories()
        .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();

    private static List<HistoryEntry> ReadEntries(IEnumerable<DirectoryInfo> directories, CancellationToken cancellationToken)
    {
        var result = new List<HistoryEntry>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(directory.Name, "N", out _)) continue;
            var path = Path.Combine(directory.FullName, Manifest);
            if (!File.Exists(path)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(path));
                if (entry == null || !directory.Name.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    entry.Width <= 0 || entry.Height <= 0 || entry.Segments == null || entry.Segments.Count == 0) continue;
                // Segment paths in earlier versions were absolute. Use the local entry folder after a move.
                var segments = new List<ImageSegment>(entry.Segments.Count);
                long rows = 0;
                foreach (var segment in entry.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (segment == null || segment.Height <= 0 || segment.SourceY < 0 || segment.DestinationY != rows) break;
                    var name = Path.GetFileName(segment.Path);
                    if (string.IsNullOrEmpty(name) || !Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase)) break;
                    var imagePath = Path.Combine(directory.FullName, name);
                    if (!File.Exists(imagePath) || (File.GetAttributes(imagePath) & FileAttributes.ReparsePoint) != 0) break;
                    segments.Add(segment with { Path = imagePath });
                    rows += segment.Height;
                }
                if (segments.Count != entry.Segments.Count || rows != entry.Height) continue;
                entry.Segments = segments;
                result.Add(entry);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Log.Error("History read", e); }
        }
        return result.OrderByDescending(e => e.Created).ToList();
    }

    internal static ImageDocument Open(HistoryEntry entry)
    {
        lock (Gate)
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) || !File.Exists(Path.Combine(entry.DirectoryPath, Manifest)))
                throw new FileNotFoundException("这张截图已从历史中清除，请刷新历史。");
            var directory = entry.DirectoryPath;
            if (!OpenImages.TryGetValue(directory, out var reference) || !reference.TryGetTarget(out var lease))
            {
                lease = new object();
                OpenImages[directory] = new(lease);
            }
            return new(entry.Width, entry.Segments, entry.Kind, lease);
        }
    }

    public void Add(EditDocument document, Settings? configuration = null)
    {
        var settings = configuration ?? App.Current.Settings;
        if (!settings.HistoryEnabled) return;
        var entry = new HistoryEntry { Kind = document.Image.Kind, Width = (int)document.Crop.Width, Height = (int)document.Crop.Height };
        if (entry.Width <= 0 || entry.Height <= 0) throw new ArgumentException("截图尺寸无效。", nameof(document));
        var staging = Path.Combine(Paths.History, PendingPrefix + entry.Id);
        Directory.CreateDirectory(staging);
        try
        {
            int stripHeight = Math.Clamp((8 * 1024 * 1024) / checked(entry.Width * 4), 1, 1024);
            for (int y = 0; y < entry.Height;)
            {
                int rows = Math.Min(stripHeight, entry.Height - y);
                var name = $"{y:D8}.png";
                ImageDocument.SaveBitmap(document.Raster(new Rect(document.Crop.X, document.Crop.Y + y, entry.Width, rows)), Path.Combine(staging, name));
                entry.Segments.Add(new(Path.Combine(entry.DirectoryPath, name), 0, rows, y));
                y += rows;
            }
            // Keep a readable excerpt at the top of a long screenshot.
            var preview = new Rect(document.Crop.X, document.Crop.Y, document.Crop.Width, Math.Min(document.Crop.Height, document.Crop.Width * .72));
            ImageDocument.SaveBitmap(document.Raster(preview, Math.Min(1, 320d / entry.Width)), Path.Combine(staging, "thumb.png"));
            AtomicFile.Write(Path.Combine(staging, Manifest), JsonSerializer.Serialize(entry, Settings.Json));
            lock (Gate) Directory.Move(staging, entry.DirectoryPath);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Incomplete history cleanup", e); }
            }
        }
        try { Clean(false, settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("History retention", e); }
    }

    public void Clean(bool all = false, Settings? configuration = null)
    {
        lock (Gate)
        {
            foreach (var path in OpenImages.Keys.ToArray())
                if (!OpenImages[path].TryGetTarget(out _)) OpenImages.Remove(path);

            var directories = Directories();
            foreach (var directory in directories)
            {
                if (Guid.TryParseExact(directory.Name, "N", out _))
                {
                    if (all || !File.Exists(Path.Combine(directory.FullName, Manifest))) Retire(directory.FullName);
                }
                else if (directory.Name.StartsWith(PendingPrefix, StringComparison.Ordinal) &&
                         Guid.TryParseExact(directory.Name[PendingPrefix.Length..], "N", out _) &&
                         directory.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                {
                    directory.Delete(true);
                }
            }

            if (all) return;
            var settings = configuration ?? App.Current.Settings;
            var candidates = directories.Where(directory => Guid.TryParseExact(directory.Name, "N", out _) &&
                File.Exists(Path.Combine(directory.FullName, Manifest))).ToArray();
            var readableDates = ReadEntries(candidates, default).ToDictionary(entry => entry.Id, entry => entry.Created, StringComparer.OrdinalIgnoreCase);
            var cached = candidates.Select(directory =>
            {
                bool readable = readableDates.TryGetValue(directory.Name, out var created);
                return (Path: directory.FullName, Readable: readable, Created: readable ? created : directory.LastWriteTime,
                    Bytes: directory.EnumerateFiles().Sum(file => file.Length));
            }).OrderBy(entry => entry.Created).ToArray();
            int count = readableDates.Count;
            long bytes = cached.Sum(entry => entry.Bytes);
            long byteLimit = (long)settings.HistoryMegabytes * 1024 * 1024;
            foreach (var entry in cached)
            {
                if (count <= settings.HistoryCount && bytes <= byteLimit) break;
                if (!entry.Readable && bytes <= byteLimit) continue;
                Retire(entry.Path);
                if (entry.Readable) count--;
                bytes -= entry.Bytes;
            }
        }
    }

    private static void Retire(string directory)
    {
        if (OpenImages.TryGetValue(directory, out var reference) && reference.TryGetTarget(out _))
        {
            // Hide the record immediately and keep the files of an active editor/export until it closes.
            File.Delete(Path.Combine(directory, Manifest));
            return;
        }
        Directory.Delete(directory, true);
    }
}
