using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThoughtCanvas;

public record DriveSnapshot(string Id, string Name, string ModifiedTime);
public record BoardBundle(Board Board, List<Board> Sources);
public class DriveService
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private string accessToken = "";
    private DateTime expiry;
    public void InvalidateToken() { accessToken = ""; expiry = DateTime.MinValue; }
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task Authorize(Settings settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.GoogleClientId)) throw new InvalidOperationException("설정에 Google Desktop OAuth Client ID를 입력해 주세요.");
        string verifier = B64(RandomNumberGenerator.GetBytes(48)), state = B64(RandomNumberGenerator.GetBytes(24));
        int port; var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        string redirect = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.GoogleClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/drive.file", ["state"] = state,
            ["code_challenge"] = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent"
        };
        string url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        bool valid = context.Request.QueryString["state"] == state && !string.IsNullOrWhiteSpace(context.Request.QueryString["code"]);
        byte[] page = Encoding.UTF8.GetBytes(valid ? "<!doctype html><meta charset='utf-8'><p>인증 응답을 받았습니다. ThoughtCanvas 앱에서 연결 결과를 확인하세요.</p>" : "<!doctype html><meta charset='utf-8'><p>인증이 취소되었거나 유효하지 않습니다.</p>");
        context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.OutputStream.WriteAsync(page, token); context.Response.Close();
        if (!valid) throw new InvalidOperationException("Google 인증이 완료되지 않았습니다.");
        var data = new Dictionary<string, string> { ["client_id"] = settings.GoogleClientId, ["code"] = context.Request.QueryString["code"]!, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier };
        string secret = Secrets.Unprotect(settings.ProtectedGoogleClientSecret); if (secret.Length > 0) data["client_secret"] = secret;
        var json = await TokenRequest(data, token);
        if (!json.TryGetProperty("refresh_token", out var refresh)) throw new InvalidOperationException("Refresh token이 없습니다. Google 연결을 다시 시도해 주세요.");
        settings.ProtectedRefreshToken = Secrets.Protect(refresh.GetString()!);
    }
    private async Task<JsonElement> TokenRequest(Dictionary<string, string> data, CancellationToken token)
    {
        using var r = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(data), token);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Google 인증 실패 ({(int)r.StatusCode}). OAuth Client 설정을 확인하세요.");
        using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
        accessToken = json.RootElement.GetProperty("access_token").GetString()!; expiry = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 60); return json.RootElement.Clone();
    }
    private async Task Ready(Settings settings, CancellationToken token)
    {
        if (accessToken.Length > 0 && expiry > DateTime.UtcNow) return;
        string refresh = Secrets.Unprotect(settings.ProtectedRefreshToken);
        if (refresh.Length == 0) throw new InvalidOperationException("먼저 설정에서 Google Drive를 연결해 주세요.");
        var data = new Dictionary<string, string> { ["client_id"] = settings.GoogleClientId, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token" };
        string secret = Secrets.Unprotect(settings.ProtectedGoogleClientSecret); if (secret.Length > 0) data["client_secret"] = secret;
        await TokenRequest(data, token);
    }
    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) { var code = (int)response.StatusCode; response.Dispose(); throw new InvalidOperationException($"Drive 요청 실패 ({code}). 연결 상태와 권한을 확인해 주세요."); }
        return response;
    }
    private async Task<string> Folder(string name, string? parent, CancellationToken token)
    {
        string q = $"trashed=false and mimeType='application/vnd.google-apps.folder' and name='{name.Replace("'", "\\'")}'" + (parent == null ? "" : $" and '{parent}' in parents");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?q=" + Uri.EscapeDataString(q) + "&fields=files(id)&pageSize=100");
        using var response = await Send(request, token); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var files = json.RootElement.GetProperty("files"); if (files.GetArrayLength() > 0) return files[0].GetProperty("id").GetString()!;
        var body = new Dictionary<string, object> { ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder" }; if (parent != null) body["parents"] = new[] { parent };
        using var create = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var created = await Send(create, token); using var result = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token)); return result.RootElement.GetProperty("id").GetString()!;
    }
    private async Task UploadFile(string local, string parent, CancellationToken token)
    {
        var info = new FileInfo(local); string mime = Path.GetExtension(local) switch { ".png" => "image/png", ".html" => "text/html", ".json" => "application/json", ".zip" => "application/zip", _ => "application/octet-stream" };
        using var init = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id")
        { Content = new StringContent(JsonSerializer.Serialize(new { name = info.Name, parents = new[] { parent } }), Encoding.UTF8, "application/json") };
        init.Headers.Add("X-Upload-Content-Type", mime); init.Headers.Add("X-Upload-Content-Length", info.Length.ToString());
        using var response = await Send(init, token); var location = response.Headers.Location ?? throw new InvalidDataException("Drive 업로드 주소를 받지 못했습니다.");
        using var file = File.OpenRead(local); using var upload = new HttpRequestMessage(HttpMethod.Put, location) { Content = new StreamContent(file) }; upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mime); upload.Content.Headers.ContentLength = info.Length;
        using var result = await Send(upload, token);
    }
    public async Task UploadExport(Settings settings, Board board, string export, CancellationToken token)
    {
        await Ready(settings, token);
        if (settings.DriveRootId.Length == 0) settings.DriveRootId = await Folder("ThoughtCanvas", null, token);
        string boards = await Folder("Boards", settings.DriveRootId, token);
        string parent = await Folder(board.Id, boards, token);
        string device = await Folder(settings.DeviceName + "_" + settings.DeviceId[..8], parent, token);
        string version = await Folder($"v{board.Revision}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}", device, token);
        var directories = new Dictionary<string, string> { [export] = version };
        foreach (var dir in Directory.EnumerateDirectories(export, "*", SearchOption.AllDirectories).OrderBy(x => x.Length)) directories[dir] = await Folder(Path.GetFileName(dir), directories[Path.GetDirectoryName(dir)!], token);
        foreach (var path in Directory.EnumerateFiles(export, "*", SearchOption.AllDirectories)) await UploadFile(path, directories[Path.GetDirectoryName(path)!], token);
        // Publish the importable snapshot last; partially uploaded versions never appear as complete snapshots.
        string zip = export.TrimEnd(Path.DirectorySeparatorChar) + ".zip";
        if (!File.Exists(zip)) ZipFile.CreateFromDirectory(export, zip);
        await UploadFile(zip, version, token);
    }
    public async Task<List<DriveSnapshot>> ListSnapshots(Settings settings, CancellationToken token)
    {
        await Ready(settings, token);
        string q = "trashed=false and mimeType='application/zip'";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?q=" + Uri.EscapeDataString(q) + "&fields=files(id,name,modifiedTime)&orderBy=modifiedTime desc&pageSize=100");
        using var response = await Send(request, token); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("files").EnumerateArray().Select(f => new DriveSnapshot(f.GetProperty("id").GetString()!, f.GetProperty("name").GetString()!, f.GetProperty("modifiedTime").GetString()!)).ToList();
    }
    public async Task<BoardBundle> Download(Settings settings, DriveSnapshot snapshot, CancellationToken token)
    {
        await Ready(settings, token); using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(snapshot.Id) + "?alt=media");
        using var response = await Send(request, token); using var bytes = new MemoryStream(await response.Content.ReadAsByteArrayAsync(token)); using var zip = new ZipArchive(bytes);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == "AppData/원본/board.json") ?? throw new InvalidDataException("ThoughtCanvas 원본이 없는 파일입니다.");
        if (entry.Length > 100_000_000) throw new InvalidDataException("원본 크기가 Prototype 제한을 초과했습니다.");
        using var reader = new StreamReader(entry.Open()); var board = DataJson.Read<Board>(await reader.ReadToEndAsync(token));
        var sources = new List<Board>();
        foreach (var source in zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("AppData/복원 지점/") && e.FullName.EndsWith(".json")))
        {
            if (source.Length > 100_000_000) throw new InvalidDataException("복원 자료가 너무 큽니다.");
            using var sr = new StreamReader(source.Open()); sources.Add(DataJson.Read<Board>(await sr.ReadToEndAsync(token)));
        }
        return new(board, sources);
    }
}
