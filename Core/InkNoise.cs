using System.Windows;

namespace ThoughtCanvas;

/// <summary>
/// AI 정리에서 맥락 없는 실수 점·짧은 흔적을 걸러냅니다 (v0.7.9).
/// 아주 작은 획(Tiny)이면서 주변에 다른 필기·Text가 없는(Isolated) 획만 Noise로 봅니다.
/// 마침표·콜론·글머리 점처럼 다른 요소 옆에 있는 작은 획은 그대로 AI에 보냅니다.
/// 원본 Board에서는 아무것도 지우지 않습니다.
/// </summary>
public static class InkNoise
{
    // 획의 가로·세로 최대 크기 (Canvas 좌표, 펜 굵기 제외). 손글씨 한 글자(약 20~40)보다 충분히 작은 값.
    public const double MaxSize = 6;
    // 이 거리 안에 다른 요소가 하나도 없으면 고립된 점으로 봅니다.
    public const double Isolation = 48;

    private static Rect Bounds(InkItem ink)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in ink.Points) { left = Math.Min(left, p.X); top = Math.Min(top, p.Y); right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y); }
        return new Rect(left, top, right - left, bottom - top);
    }
    public static bool IsTiny(InkItem ink)
    {
        if (ink.Points.Count == 0) return false;
        var b = Bounds(ink); return b.Width <= MaxSize && b.Height <= MaxSize;
    }
    /// <summary>작은 획 전체 (주변 요소 여부와 무관). 구조화 기록에 연결되지 않아도 검증 항목을 만들지 않습니다.</summary>
    public static HashSet<string> Tiny(Board board) => board.Ink.Where(IsTiny).Select(i => i.Id).ToHashSet();
    /// <summary>AI 요청에서 제외할 고립된 작은 획. 작은 획만 주변을 검사하므로 비용은 (작은 획 수 × 요소 수) 사각형 비교입니다.</summary>
    public static HashSet<string> Find(Board board)
    {
        var candidates = board.Ink.Where(IsTiny).ToList();
        if (candidates.Count == 0) return [];
        var others = board.Ink.Where(i => i.Points.Count > 0).Select(i => (i.Id, Box: Bounds(i)))
            .Concat(board.Texts.Select(t => (t.Id, Box: BoardSurface.TextBounds(t)))).ToList();
        var noise = new HashSet<string>();
        foreach (var c in candidates)
        {
            var box = Bounds(c);
            if (!others.Any(o => o.Id != c.Id && Gap(box, o.Box) < Isolation)) noise.Add(c.Id);
        }
        return noise;
    }
    private static double Gap(Rect a, Rect b)
    {
        double dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        double dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
