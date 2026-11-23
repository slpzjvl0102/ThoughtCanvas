using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public static class DataJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("비어 있는 데이터입니다.");
    public static T Clone<T>(T value) => Read<T>(Write(value));
}
public record Sample(double X, double Y, float Pressure = .5f);
public class InkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public List<Sample> Points { get; set; } = [];
    public string Color { get; set; } = "#243047";
    public double Width { get; set; } = 2.5;
    public Stroke ToStroke()
    {
        var points = new StylusPointCollection(Points.Select(p => new StylusPoint(p.X, p.Y, Math.Clamp(p.Pressure, 0, 1))));
        return new Stroke(points) { DrawingAttributes = new DrawingAttributes { Color = (Color)ColorConverter.ConvertFromString(Color), Width = Width, Height = Width, FitToCurve = false, IgnorePressure = false } };
    }
    public static InkItem FromStroke(Stroke stroke, string? parentId = null) => new()
    {
        ParentId = parentId, Width = stroke.DrawingAttributes.Width, Color = stroke.DrawingAttributes.Color.ToString(),
        Points = stroke.StylusPoints.Select(p => new Sample(p.X, p.Y, p.PressureFactor)).ToList()
    };
}
public class TextItem
{
    public bool IsSticky { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 260;
    public double FontSize { get; set; } = 18;
    public string Text { get; set; } = "";
}
public class Idea
{
    public long SourceRevision { get; set; }
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Category { get; set; } = "";
    public string Content { get; set; } = "";
    public string Context { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Evidence { get; set; } = "inferred";
    public List<string> SourceIds { get; set; } = [];
    public List<string> Relations { get; set; } = [];
    public string Display => $"{Topic} · {Category}\n{Content}";
}
public class ReviewItem
{
    public string Kind { get; set; } = "word";
    public string Question { get; set; } = "";
    public List<string> SourceIds { get; set; } = [];
    public string Answer { get; set; } = "";
    public bool Resolved { get; set; }
    public string Display => $"{(Resolved ? "✓" : "○")} [{Kind}] {Question}";
}
public class AnalysisResult
{
    public Dictionary<string, string> RegionFingerprints { get; set; } = [];
    public AiRequestMetrics? RequestMetrics { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public long SourceRevision { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Document { get; set; } = "";
    public string ArchivedDocument { get; set; } = "";
    public List<Idea> Ideas { get; set; } = [];
    public List<ReviewItem> Reviews { get; set; } = [];
}
public class AiRequestMetrics
{
    public int InputCharacters { get; set; }
    public int ImagesSent { get; set; }
    public int ImagesReused { get; set; }
    public int HistorySnapshotsOmitted { get; set; }
    public int? PromptTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ThoughtTokens { get; set; }
}
public class DocumentEdit
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Origin { get; set; } = "user_document";
    public string Text { get; set; } = "";
    public long SourceRevision { get; set; }
}
public class Board
{
    public Dictionary<string, long> Clock { get; set; } = [];
    public HashSet<string> AcceptedSnapshots { get; set; } = [];
    public string ContentHash { get; set; } = "";
    public string LastWriter { get; set; } = "";
    public List<TopicDocument> TopicDocuments { get; set; } = [];
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "새로운 생각";
    public long Revision { get; set; }
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<InkItem> Ink { get; set; } = [];
    public List<TextItem> Texts { get; set; } = [];
    public double ViewX { get; set; } = 80;
    public double ViewY { get; set; } = 80;
    public double Zoom { get; set; } = 1;
    public int SavesSinceAi { get; set; }
    public AnalysisResult? Analysis { get; set; }
    public string UserDocument { get; set; } = "";
    public bool DocumentEdited { get; set; }
    public string? ProposedDocument { get; set; }
    public List<DocumentEdit> DocumentEdits { get; set; } = [];
    public HashSet<string> AllSourceIds() => Ink.Select(x => x.Id).Concat(Texts.Select(x => x.Id)).ToHashSet();
}
public record CanvasSnapshot(List<InkItem> Ink, List<TextItem> Texts);
public class CanvasHistory
{
    private readonly List<CanvasSnapshot> undo = [];
    private readonly List<CanvasSnapshot> redo = [];
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    // 목록만 얕게 복사합니다. 요소는 불변으로 취급하고, 바꿀 때는 BoardSurface가 복제본으로 교체합니다 (v0.6.7).
    public static CanvasSnapshot Capture(Board board) => new([.. board.Ink], [.. board.Texts]);
    public void Push(CanvasSnapshot before) { undo.Add(before); if (undo.Count > 50) undo.RemoveAt(0); redo.Clear(); }
    public bool Undo(Board board) => Move(undo, redo, board);
    public bool Redo(Board board) => Move(redo, undo, board);
    private static bool Move(List<CanvasSnapshot> from, List<CanvasSnapshot> to, Board board)
    {
        if (from.Count == 0) return false;
        to.Add(Capture(board)); var state = from[^1]; from.RemoveAt(from.Count - 1);
        board.Ink = [.. state.Ink]; board.Texts = [.. state.Texts]; return true;
    }
    public void Clear() { undo.Clear(); redo.Clear(); }
}
public class Settings
{
    public HashSet<string> OfflinePendingBoards { get; set; } = [];
    public string NotionParentId { get; set; } = "";
    public string ProtectedNotionToken { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool DarkMode { get; set; }
    public bool FocusMode { get; set; }
    public string LastBoardId { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public const string DefaultModel = "gemini-3.8-flash";
    public string ProtectedGeminiApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;
    public static bool IsSupportedModel(string model) => System.Text.RegularExpressions.Regex.IsMatch(model, @"^gemini-3[.a-zA-Z0-9-]*$");
    public bool MigrateAiProvider()
    {
        if (Model.StartsWith("gemini-3", StringComparison.Ordinal)) return false;
        Model = DefaultModel; AutomaticAi = false; return true;
    }
    public bool AutomaticAi { get; set; }
    public int AiEverySaves { get; set; } = 20;
    public string GoogleClientId { get; set; } = "";
    public string ProtectedGoogleClientSecret { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public string DriveRootId { get; set; } = "";
    public bool AutomaticDrive { get; set; }
    public string DriveSyncFolder { get; set; } = @"G:\내 드라이브";
    public bool AutomaticFolderBackup { get; set; } = true;
}
public class TopicDocument
{
    public bool Archived { get; set; }
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ProposedText { get; set; }
    public bool Edited { get; set; }
}
