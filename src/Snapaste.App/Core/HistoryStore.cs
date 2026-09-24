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
    public ImageDocument Open() => new(Width, Segments, Kind);
}
public sealed class HistoryStore
{
    public List<HistoryEntry> Entries()
    {
        var result = new List<HistoryEntry>();
        foreach (var file in Directory.EnumerateFiles(Paths.History, "entry.json", SearchOption.AllDirectories))
        {
            try { var entry = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(file)); if (entry != null && entry.Segments.All(s => File.Exists(s.Path))) result.Add(entry); }
            catch (Exception e) when (e is IOException or JsonException) { Log.Error("History read", e); }
        }
        return result.OrderByDescending(e => e.Created).ToList();
    }
    public void Add(EditDocument document, Settings? configuration = null)
    {
        var settings = configuration ?? App.Current.Settings;
        if (!settings.HistoryEnabled) return;
        var entry = new HistoryEntry { Kind = document.Image.Kind, Width = (int)document.Crop.Width, Height = (int)document.Crop.Height };
        Directory.CreateDirectory(entry.DirectoryPath);
        for (int y = 0; y < entry.Height; y += 1024)
        {
            int rows = Math.Min(1024, entry.Height - y); var path = Path.Combine(entry.DirectoryPath, $"{y:D8}.png");
            ImageDocument.SaveBitmap(document.Raster(new Rect(document.Crop.X, document.Crop.Y + y, entry.Width, rows)), path);
            entry.Segments.Add(new(path, 0, rows, y));
        }
        ImageDocument.SaveBitmap(document.Raster(document.Crop, Math.Min(1, Math.Min(240d / entry.Width, 400d / entry.Height))), entry.Thumbnail);
        AtomicFile.Write(Path.Combine(entry.DirectoryPath, "entry.json"), JsonSerializer.Serialize(entry, Settings.Json)); Clean(false, settings);
    }
    public void Clean(bool all = false, Settings? configuration = null)
    {
        var entries = Entries(); long size = entries.Sum(e => Directory.EnumerateFiles(e.DirectoryPath).Sum(f => new FileInfo(f).Length));
        int count = entries.Count; var s = configuration ?? App.Current.Settings;
        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            if (!all && count <= s.HistoryCount && size <= (long)s.HistoryMegabytes * 1024 * 1024) break;
            long bytes = Directory.EnumerateFiles(entry.DirectoryPath).Sum(f => new FileInfo(f).Length);
            // Pin images are separate immutable cache copies, never history references.
            Directory.Delete(entry.DirectoryPath, true); size -= bytes; count--;
        }
    }
}
