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
        string destination = Path.Combine(BoardFolder(Path.Combine(root, "Boards"), board),
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
        Prune(Path.GetDirectoryName(destination)!, destination);
        return destination;
    }
    public const int KeepVersions = 5;
    // 같은 Board(ID)의 백업은 한 폴더에 모읍니다 (v0.6.9). 이름이 바뀌면 폴더 이름을 맞추고, 갈라진 옛 폴더는 합칩니다.
    // 정리에 실패해도 백업 자체는 진행합니다.
    public static string BoardFolder(string boards, Board board)
    {
        Directory.CreateDirectory(boards);
        string wanted = Path.Combine(boards, ExportService.SafeName(board.Title) + "_" + board.Id);
        var found = Directory.EnumerateDirectories(boards).Where(d => Path.GetFileName(d).EndsWith("_" + board.Id, StringComparison.Ordinal))
            .OrderBy(d => d == wanted ? 0 : 1).ThenBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        if (found.Count == 0) return wanted;
        string target = found[0];
        foreach (var other in found.Skip(1)) Tidy(() => MergeInto(other, target));
        if (target != wanted && !Directory.Exists(wanted)) Tidy(() => { Directory.Move(target, wanted); target = wanted; });
        return target;
    }
    private static void MergeInto(string source, string target)
    {
        foreach (var device in Directory.EnumerateDirectories(source).ToList())
        {
            string destination = Path.Combine(target, Path.GetFileName(device)); Directory.CreateDirectory(destination);
            // 완성된 저장본만 옮깁니다. 다른 기기가 아직 올리는 중인 폴더는 그대로 둡니다.
            foreach (var version in Directory.EnumerateDirectories(device).ToList())
            {
                string to = Path.Combine(destination, Path.GetFileName(version));
                if (File.Exists(Path.Combine(version, "snapshot.zip")) && !Directory.Exists(to)) Directory.Move(version, to);
            }
            if (!Directory.EnumerateFileSystemEntries(device).Any()) Directory.Delete(device);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }
    // 이 기기 폴더에서 Revision 기준 최근 KeepVersions개만 남깁니다. 방금 올린 저장본과 미완성 폴더는 지우지 않습니다.
    public static void Prune(string deviceFolder, string? keep = null, int count = KeepVersions)
    {
        static long Revision(string name) => name.StartsWith('v') && long.TryParse(name[1..].Split('_')[0], out var r) ? r : -1;
        var complete = Directory.EnumerateDirectories(deviceFolder)
            .Where(d => File.Exists(Path.Combine(d, "snapshot.zip")) && !File.Exists(Path.Combine(d, "snapshot.pending")))
            .OrderByDescending(d => d == keep).ThenByDescending(d => Revision(Path.GetFileName(d))).ThenByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        foreach (var old in complete.Skip(count)) Tidy(() => Directory.Delete(old, true));
    }
    private static void Tidy(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
