using System.Net;
using System.Net.Http;
using System.Text;

namespace ThoughtCanvas;

// Model 설정은 "gemini-3.8-flash, gemini-3.5-flash-lite"처럼 쉼표로 여러 개를 적을 수 있습니다.
// 앞의 Model이 사용량 한도(HTTP 429)에 걸리면 다음 Model로 같은 요청을 한 번씩 다시 보냅니다.
public static class GeminiModels
{
    public static List<string> Chain(string? setting) =>
        (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
    public static bool IsValid(string? setting) { var chain = Chain(setting); return chain.Count > 0 && chain.All(Settings.IsSupportedModel); }
    public static async Task<(HttpResponseMessage Response, string Model)> Send(HttpClient client, string setting, string key, string json, CancellationToken token)
    {
        var chain = Chain(setting);
        if (chain.Count == 0 || !chain.All(Settings.IsSupportedModel)) throw new InvalidOperationException("Gemini 3 계열 Model 설정을 확인하세요.");
        for (int i = 0; ; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(chain[i])}:generateContent");
            request.Headers.Add("x-goog-api-key", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && i + 1 < chain.Count) { response.Dispose(); continue; }
            return (response, chain[i]);
        }
    }
}
