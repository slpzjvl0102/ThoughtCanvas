using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace ThoughtCanvas;

public static class Secrets
{
    public static string Protect(string plain) => string.IsNullOrWhiteSpace(plain) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
    }
}
public static class GeminiCredentials
{
    // Read persisted Windows scopes as well, so an older launcher does not hide a newly set key.
    public static (string Key, string Source) Resolve(Settings settings, Func<string, EnvironmentVariableTarget, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" })
            foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.Process })
            {
                var value = read(name, scope)?.Trim();
                if (!string.IsNullOrEmpty(value)) return (value, $"{name} ({scope})");
            }
        return (Secrets.Unprotect(settings.ProtectedGeminiApiKey), string.IsNullOrEmpty(settings.ProtectedGeminiApiKey) ? "없음" : "앱에 저장한 Key");
    }
}
public class BoardStore
{
    public string Root { get; }
    private readonly string connectionString;
    public BoardStore(string root)
    {
        Root = root; Directory.CreateDirectory(root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "boards.sqlite"), DefaultTimeout = 10 }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS boards(id TEXT PRIMARY KEY,title TEXT NOT NULL,json TEXT NOT NULL,modified TEXT NOT NULL); CREATE TABLE IF NOT EXISTS revisions(board_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(board_id,revision));";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA synchronous=FULL;"; cmd.ExecuteNonQuery(); return c; }
    public void Save(Board board)
    {
        var json = DataJson.Write(board);
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO boards VALUES($id,$title,$json,$modified) ON CONFLICT(id) DO UPDATE SET title=excluded.title,json=excluded.json,modified=excluded.modified WHERE json_extract(excluded.json,'$.Revision') >= json_extract(boards.json,'$.Revision'); INSERT OR IGNORE INTO revisions VALUES($id,$revision,$json);";
        cmd.Parameters.AddWithValue("$id", board.Id); cmd.Parameters.AddWithValue("$title", board.Title); cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$revision", board.Revision); cmd.Parameters.AddWithValue("$modified", board.ModifiedUtc.ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public Board? Load(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM boards WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        var value = cmd.ExecuteScalar() as string; return value == null ? null : DataJson.Read<Board>(value);
    }
    public List<(string Id, string Title)> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,title FROM boards ORDER BY modified DESC"; using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>(); while (r.Read()) result.Add((r.GetString(0), r.GetString(1))); return result;
    }
    // 목록 캐시용 (v0.7.4): Board를 읽지 않고 저장 상태만 확인합니다. 순서는 List()와 같습니다.
    public List<(string Id, string Stamp)> ListStamps()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ifnull(json_extract(json,'$.Revision'),'') || '|' || length(json) || '|' || ifnull(modified,'') FROM boards ORDER BY modified DESC"; using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>(); while (r.Read()) result.Add((r.GetString(0), r.GetString(1))); return result;
    }
    public Board? LoadRevision(string id, long revision)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM revisions WHERE board_id=$id AND revision=$revision";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$revision", revision);
        var json = cmd.ExecuteScalar() as string; return json == null ? null : DataJson.Read<Board>(json);
    }
    public long StoreEvidence(string id, Board source)
    {
        var digest = SyncEngine.Digest(source); long revision = -(long)(Convert.ToUInt64(digest[..15], 16) + 1);
        var existing = LoadRevision(id, revision);
        if (existing != null) { if (SyncEngine.Digest(existing) != digest) throw new InvalidDataException("원본 식별 충돌입니다."); return revision; }
        var copy = DataJson.Clone(source); copy.Id = id; copy.Revision = revision;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT OR IGNORE INTO revisions(board_id,revision,json) VALUES($id,$revision,$json)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$revision", revision); cmd.Parameters.AddWithValue("$json", DataJson.Write(copy)); cmd.ExecuteNonQuery(); return revision;
    }
    // 설정 파일이 아직 없으면 이 기기에서 처음 실행한 것입니다 (기존 사용자는 이미 있음)
    public bool HasSettings => File.Exists(Path.Combine(Root, "settings.json"));
    public Settings LoadSettings()
    {
        var settings = File.Exists(Path.Combine(Root, "settings.json")) ? DataJson.Read<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) : new Settings();
        if (settings.MigrateAiProvider()) SaveSettings(settings);
        return settings;
    }
    public void SaveSettings(Settings settings) => AtomicWrite(Path.Combine(Root, "settings.json"), DataJson.Write(settings));
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + ".tmp";
        File.WriteAllText(temp, text, Encoding.UTF8); File.Move(temp, path, true);
    }
}
