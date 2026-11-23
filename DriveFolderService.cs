using System.IO.Compression;
namespace ThoughtCanvas;

public static class DriveFolderService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, Entry Entry)> catalogCache = new();
    public static Dictionary<string, string> RegisterDevice(string folder, string id, string name)
    {
        var path = Path.Combine(Root(folder), "Devices"); Directory.CreateDirectory(path);
        BoardStore.AtomicWrite(Path.Combine(path, ExportService.SafeName(id) + ".json"), DataJson.Write(new { Id = id, Name = name, SeenUtc = DateTime.UtcNow }));
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
            try { using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)); result[json.RootElement.GetProperty("Id").GetString()!] = json.RootElement.GetProperty("Name").GetString()!; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException) { }
        return result;
    }
    public record Entry(string Path, string BoardId, string Title, string Device, long Revision, DateTime ModifiedUtc)
    {
        public string LocalId => "drive_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            BoardId + "/" + Device + "/" + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))))).ToLowerInvariant();
    }
    public record Catalog(List<Entry> Entries, int Skipped);
    public static Catalog Discover(string folder, CancellationToken token)
    {
        string boards = Path.Combine(Root(folder), "Boards");
        if (!Directory.Exists(boards)) return new([], 0);
        var entries = new List<Entry>(); int skipped = 0;
        foreach (var file in Directory.EnumerateFiles(boards, "snapshot.zip", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (catalogCache.TryGetValue(file, out var cached) && cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks) { entries.Add(cached.Entry); continue; }
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("AppData/원본/board.json") ?? throw new InvalidDataException();
                if (entry.Length > 100_000_000) throw new InvalidDataException();
                using var reader = new StreamReader(entry.Open());
                var board = DataJson.Read<Board>(reader.ReadToEnd());
                if (string.IsNullOrWhiteSpace(board.Id)) throw new InvalidDataException();
                string device = Directory.GetParent(Path.GetDirectoryName(file)!)!.Name;
                var item = new Entry(file, board.Id, board.Title, device, board.Revision, board.ModifiedUtc);
                if (catalogCache.Count > 5000) catalogCache.Clear(); catalogCache[file] = (info.Length, info.LastWriteTimeUtc.Ticks, item); entries.Add(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { skipped++; }
        }
        // Revisions are only comparable within a device branch. Keep other devices separately.
        return new(entries.GroupBy(e => (e.BoardId, e.Device)).Select(g => g.OrderByDescending(e => e.Revision)
            .ThenByDescending(e => e.ModifiedUtc).ThenByDescending(e => e.Path, StringComparer.Ordinal).First())
            .OrderByDescending(e => e.ModifiedUtc).ToList(), skipped);
    }
    public static string Root(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new IOException("Drive 폴더에 접근할 수 없습니다. Google Drive 실행 상태와 설정 경로를 확인하세요. 원본은 Local에 유지됩니다.");
        return Path.Combine(Path.GetFullPath(folder), "ThoughtCanvas");
    }
    public static string Publish(string folder, string deviceId, Board board, string export, CancellationToken token)
    {
        var root = Root(folder);
        // Unique version directories avoid overwriting work from another device.
        string destination = Path.Combine(root, "Boards", ExportService.SafeName(board.Title) + "_" + board.Id,
            ExportService.SafeName(deviceId), $"v{board.Revision}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(export, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(export, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
        }
        // Import only the complete archive. A failed/cancelled copy leaves no importable .zip.
        token.ThrowIfCancellationRequested();
        string pending = Path.Combine(destination, "snapshot.pending");
        ZipFile.CreateFromDirectory(export, pending);
        token.ThrowIfCancellationRequested();
        File.Move(pending, Path.Combine(destination, "snapshot.zip"));
        return destination;
    }
    public static BoardBundle Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Board ReadBoard(ZipArchiveEntry entry)
        {
            if (entry.Length > 100_000_000) throw new InvalidDataException("원본 크기가 Prototype 제한을 초과했습니다.");
            using var reader = new StreamReader(entry.Open()); return DataJson.Read<Board>(reader.ReadToEnd());
        }
        var original = zip.GetEntry("AppData/원본/board.json") ?? throw new InvalidDataException("ThoughtCanvas 저장본이 아닙니다.");
        var sources = zip.Entries.Where(e => e.FullName.StartsWith("AppData/복원 지점/") && e.FullName.EndsWith(".json")).Select(ReadBoard).ToList();
        return new(ReadBoard(original), sources);
    }
}
