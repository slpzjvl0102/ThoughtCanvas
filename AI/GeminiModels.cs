using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ThoughtCanvas;

// 기본 Model이 사용량 한도(HTTP 429)에 걸리면 같은 요청을 다른 Model로 다시 보냅니다.
// - 설정에 쉼표로 적은 대체 Model이 있으면 그 순서를 먼저 따릅니다 (이전 설정 호환).
// - 자동 전환이 켜져 있으면 이 API key로 쓸 수 있는 Gemini 3 Model 목록을 받아 성능 높은 순(Pro → Flash → Flash-Lite, 최신 버전 우선)으로 시도합니다.
// - 최근 429를 받은 Model은 목록 뒤로 보내 같은 실행에서 헛요청을 줄입니다.
public static class GeminiModels
{
    public const int MaxAttempts = 5;
    private const string Api = "https://generativelanguage.googleapis.com/v1beta/models";
    private static readonly string[] NonTextKinds = ["image", "tts", "audio", "live", "embedding", "native", "computer-use", "robotics", "veo", "imagen"];
    private static readonly Dictionary<string, List<string>> listCache = [];
    private static readonly Dictionary<string, DateTime> exhausted = [];
    private static readonly object gate = new();

    public static List<string> Chain(string? setting) =>
        (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
    public static bool IsValid(string? setting) => Settings.IsSupportedModel(setting ?? "");
    // 문서 정리에 쓸 수 있는 일반 Model인지 (Image 생성·음성·Embedding 전용 Model 제외)
    public static bool IsGeneralModel(string id) => Settings.IsSingleModel(id) && !NonTextKinds.Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase));
    public static int Tier(string id) => id.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? 1 : id.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 2 : id.Contains("pro", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    public static double Version(string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"^gemini-(\d+(?:\.\d+)?)");
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
    public static bool IsPreview(string id) => id.Contains("preview", StringComparison.OrdinalIgnoreCase) || id.Contains("exp", StringComparison.OrdinalIgnoreCase);
    // 성능 높은 순: 등급(Pro > Flash > Flash-Lite) → 버전 → 정식 > Preview
    public static List<string> ByPerformance(IEnumerable<string> ids) => ids.Where(IsGeneralModel).Distinct(StringComparer.Ordinal)
        .OrderByDescending(Tier).ThenByDescending(Version).ThenBy(IsPreview).ThenBy(id => id, StringComparer.Ordinal).ToList();

    public static async Task<List<string>> Available(HttpClient client, string key, CancellationToken token)
    {
        lock (gate) if (listCache.TryGetValue(key, out var cached)) return cached;
        var ids = new List<string>(); string? page = null;
        for (int i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}?pageSize=1000" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            request.Headers.Add("x-goog-api-key", key);
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Model 목록을 불러오지 못했습니다 ({(int)response.StatusCode}). API key를 확인하세요.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                {
                    string name = (m.TryGetProperty("name", out var n) ? n.GetString() : null) ?? "";
                    if (name.StartsWith("models/", StringComparison.Ordinal)) name = name[7..];
                    bool generates = !m.TryGetProperty("supportedGenerationMethods", out var methods) || methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                    if (generates) ids.Add(name);
                }
            page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(page)) break;
        }
        var result = ByPerformance(ids);
        lock (gate) listCache[key] = result;
        return result;
    }
    public static void ResetCache() { lock (gate) { listCache.Clear(); exhausted.Clear(); } }
    private static bool RecentlyExhausted(string model) { lock (gate) return exhausted.TryGetValue(model, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1); }

    public static async Task<(HttpResponseMessage Response, string Model)> Send(HttpClient client, string setting, string key, string json, CancellationToken token, bool autoSwitch = false)
    {
        var chain = Chain(setting);
        if (chain.Count == 0 || !chain.All(Settings.IsSingleModel)) throw new InvalidOperationException("Gemini 3 계열 Model 설정을 확인하세요.");
        bool expanded = false;
        for (int i = 0; ; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(chain[i])}:generateContent");
            request.Headers.Add("x-goog-api-key", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request, token);
            if (response.StatusCode != HttpStatusCode.TooManyRequests) return (response, chain[i]);
            lock (gate) exhausted[chain[i]] = DateTime.UtcNow;
            if (i + 1 >= MaxAttempts) return (response, chain[i]);
            if (i + 1 >= chain.Count && autoSwitch && !expanded)
            {
                expanded = true;
                try
                {
                    var others = (await Available(client, key, token)).Where(m => !chain.Contains(m)).ToList();
                    chain.AddRange(others.Where(m => !RecentlyExhausted(m)).Concat(others.Where(RecentlyExhausted)));
                }
                catch (OperationCanceledException) { response.Dispose(); throw; }
                catch { } // 목록을 못 받으면 원래 429 오류를 그대로 안내합니다.
            }
            if (i + 1 >= chain.Count) return (response, chain[i]);
            response.Dispose();
        }
    }
}
