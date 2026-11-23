using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
namespace ThoughtCanvas;

public record LiveRequest(Board? Basis, Board? Edited, List<Board>? Sources = null);
public record LiveReply(Board? Board, string Error = "", List<Board>? Sources = null);
public sealed class LiveSession : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener? listener;
    private readonly byte[] key;
    private readonly string? host;
    private readonly int port;
    private readonly Dictionary<string, LiveReply> replies = [];
    public string Code { get; }
    private LiveSession(TcpListener? listener, byte[] key, string? host, int port, string code)
    { this.listener = listener; this.key = key; this.host = host; this.port = port; Code = code; }
    public static LiveSession Host(Func<LiveRequest, Task<LiveReply>> process, bool loopbackOnly = false)
    {
        var listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, 0); listener.Start(8);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; var key = RandomNumberGenerator.GetBytes(32);
        var ip = loopbackOnly ? IPAddress.Loopback : Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) ?? IPAddress.Loopback;
        var session = new LiveSession(listener, key, null, port, $"{ip}:{port}/{Convert.ToHexString(key)}");
        _ = Task.Run(() => session.Listen(process)); return session;
    }
    public static LiveSession Join(string code)
    {
        var parts = code.Trim().Split('/'); if (parts.Length != 2) throw new InvalidDataException("연결 Code를 확인하세요.");
        var endpoint = IPEndPoint.Parse(parts[0]); var key = Convert.FromHexString(parts[1]);
        if (key.Length != 32 || endpoint.Port == 0) throw new InvalidDataException("연결 Code를 확인하세요.");
        return new(null, key, endpoint.Address.ToString(), endpoint.Port, code.Trim());
    }
    private async Task Listen(Func<LiveRequest, Task<LiveReply>> process)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = await listener!.AcceptTcpClientAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var stream = client.GetStream();
                var request = DataJson.Read<LiveRequest>(Encoding.UTF8.GetString(await Read(stream, key, timeout.Token)));
                string requestId = request.Basis == null || request.Edited == null ? "" : request.Basis.Id + ":" + SyncEngine.Digest(request.Basis) + ":" + SyncEngine.Digest(request.Edited);
                LiveReply result;
                if (requestId.Length > 0 && replies.TryGetValue(requestId, out var cached)) result = cached;
                else
                {
                    result = await process(request);
                    if (requestId.Length > 0 && result.Board != null && !SyncEngine.Equivalent(request.Basis!, request.Edited!))
                    { if (replies.Count >= 128) replies.Remove(replies.Keys.First()); replies[requestId] = result; }
                }
                await Write(stream, key, Encoding.UTF8.GetBytes(DataJson.Write(result)), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException) { }
        }
    }
    public async Task<LiveReply> Exchange(LiveRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient(); await client.ConnectAsync(host!, port, timeout.Token); using var stream = client.GetStream();
        await Write(stream, key, Encoding.UTF8.GetBytes(DataJson.Write(request)), timeout.Token);
        return DataJson.Read<LiveReply>(Encoding.UTF8.GetString(await Read(stream, key, timeout.Token)));
    }
    public static byte[] Encrypt(byte[] data, byte[] key)
    {
        if (data.Length > 32_000_000) throw new InvalidDataException("공동 편집 데이터가 32MB 한도를 초과했습니다.");
        byte[] packet = new byte[data.Length + 28]; RandomNumberGenerator.Fill(packet.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(packet.AsSpan(0, 12), data, packet.AsSpan(28), packet.AsSpan(12, 16)); return packet;
    }
    public static byte[] Decrypt(byte[] packet, byte[] key)
    {
        if (packet.Length < 28 || packet.Length > 32_000_028) throw new InvalidDataException("공동 편집 데이터 길이 오류");
        var data = new byte[packet.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(28), packet.AsSpan(12, 16), data); return data;
    }
    private static async Task Write(Stream stream, byte[] key, byte[] data, CancellationToken token)
    { var packet = Encrypt(data, key); await stream.WriteAsync(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(packet.Length)), token); await stream.WriteAsync(packet, token); }
    private static async Task<byte[]> Read(Stream stream, byte[] key, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token); int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header));
        if (length < 28 || length > 32_000_028) throw new InvalidDataException("공동 편집 데이터 길이 오류");
        byte[] packet = new byte[length]; await stream.ReadExactlyAsync(packet, token); return Decrypt(packet, key);
    }
    public void Dispose() { stop.Cancel(); listener?.Stop(); }
}

public static class LiveMerge
{
    public static Board Apply(Board current, Board basis, Board edited, string device)
    {
        if (current.Id != basis.Id || basis.Id != edited.Id) throw new InvalidDataException("다른 Board의 공동 편집 요청입니다.");
        var result = DataJson.Clone(current); bool conflict = false; var alternatives = new List<string>();
        void Items<T>(List<T> target, List<T> before, List<T> after, Func<T, string> id, Action<T, string> setId)
        {
            foreach (var key in before.Select(id).Concat(after.Select(id)).Distinct())
            {
                var old = before.FirstOrDefault(x => id(x) == key); var next = after.FirstOrDefault(x => id(x) == key);
                if (DataJson.Write(old) == DataJson.Write(next)) continue;
                var existing = target.FirstOrDefault(x => id(x) == key);
                if (DataJson.Write(existing) == DataJson.Write(next)) continue;
                if (DataJson.Write(existing) == DataJson.Write(old)) { target.RemoveAll(x => id(x) == key); if (next != null) target.Add(DataJson.Clone(next)); }
                else { conflict = true; if (next != null) { var copy = DataJson.Clone(next); string alternative = Guid.NewGuid().ToString("N"); setId(copy, alternative); alternatives.Add(alternative); target.Add(copy); } }
            }
        }
        Items(result.Ink, basis.Ink, edited.Ink, i => i.Id, (i, id) => i.Id = id);
        Items(result.Texts, basis.Texts, edited.Texts, t => t.Id, (t, id) => t.Id = id);
        if (edited.Title != basis.Title)
        {
            if (current.Title == basis.Title) result.Title = edited.Title;
            else if (current.Title != edited.Title) { conflict = true; result.DocumentEdits.Add(new() { Origin = "live_title_alternative", Text = edited.Title, SourceRevision = edited.Revision }); }
        }
        bool SameAnalysis(AnalysisResult? a, AnalysisResult? b) => SyncEngine.Digest(new Board { Title = "", Analysis = a }) == SyncEngine.Digest(new Board { Title = "", Analysis = b });
        if (!SameAnalysis(edited.Analysis, basis.Analysis))
        {
            if (SameAnalysis(current.Analysis, basis.Analysis)) result.Analysis = DataJson.Clone(edited.Analysis);
            else if (edited.Analysis != null && !SameAnalysis(current.Analysis, edited.Analysis))
            {
                result.Analysis ??= new(); conflict = true;
                foreach (var idea in edited.Analysis.Ideas)
                {
                    if (result.Analysis.Ideas.Any(i => DataJson.Write(i) == DataJson.Write(idea))) continue;
                    var copy = DataJson.Clone(idea); if (result.Analysis.Ideas.Any(i => i.Id == copy.Id)) copy.Id = Guid.NewGuid().ToString("N"); result.Analysis.Ideas.Add(copy);
                }
                result.Analysis.ArchivedDocument += "\n\n[공동 편집 검토 기록]\n" + edited.Analysis.ArchivedDocument;
            }
        }
        foreach (var doc in edited.TopicDocuments)
        {
            var before = basis.TopicDocuments.FirstOrDefault(d => d.Topic == doc.Topic);
            if (DataJson.Write(before) == DataJson.Write(doc)) continue;
            var existing = result.TopicDocuments.FirstOrDefault(d => d.Topic == doc.Topic);
            if (existing == null) result.TopicDocuments.Add(DataJson.Clone(doc));
            else if (DataJson.Write(existing) == DataJson.Write(before)) { result.TopicDocuments.Remove(existing); result.TopicDocuments.Add(DataJson.Clone(doc)); }
            else if (existing.Text != doc.Text) { existing.ProposedText = doc.Text; conflict = true; }
        }
        foreach (var edit in edited.DocumentEdits) if (!result.DocumentEdits.Any(e => DataJson.Write(e) == DataJson.Write(edit))) result.DocumentEdits.Add(DataJson.Clone(edit));
        if (edited.UserDocument != basis.UserDocument)
        {
            if (current.UserDocument == basis.UserDocument) { result.UserDocument = edited.UserDocument; result.DocumentEdited = true; }
            else if (current.UserDocument != edited.UserDocument) { result.ProposedDocument = edited.UserDocument; conflict = true; }
        }
        if (alternatives.Count > 0)
        {
            var part = new Board { Ink = result.Ink.Where(i => alternatives.Contains(i.Id)).ToList(), Texts = result.Texts.Where(t => alternatives.Contains(t.Id)).ToList() };
            var from = SyncEngine.Bounds(part); var occupied = SyncEngine.Bounds(current);
            if (!from.IsEmpty && !occupied.IsEmpty) SyncEngine.Offset(result, alternatives, occupied.Right + 160 - from.Left, occupied.Top - from.Top);
        }
        if (conflict) { result.Analysis ??= new(); result.Analysis.Reviews.Add(new() { Kind = "contradiction", SourceIds = alternatives, Question = "공동 편집 중 같은 요소 또는 문서가 양쪽에서 수정되었습니다. 대체 내용을 빈 공간에 보존했습니다. 중복 요소와 문서 제안을 확인하세요." }); }
        foreach (var pair in edited.Clock) result.Clock[pair.Key] = Math.Max(result.Clock.GetValueOrDefault(pair.Key), pair.Value);
        if (!SyncEngine.Equivalent(current, result)) { result.Revision = current.Revision + 1; result.ModifiedUtc = DateTime.UtcNow; SyncEngine.Stamp(result, device); }
        return result;
    }
}
