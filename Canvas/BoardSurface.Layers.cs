using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public partial class BoardSurface : Canvas
{
    // 완성된 Stroke 하나당 DrawingVisual 하나. 순서는 Board.Ink 순서(나중 Stroke가 위)를 따릅니다.
    private sealed class InkLayer : FrameworkElement
    {
        private readonly VisualCollection children;
        private readonly Dictionary<string, DrawingVisual> visuals = [];
        public InkLayer() { children = new VisualCollection(this); IsHitTestVisible = false; }
        protected override int VisualChildrenCount => children.Count;
        protected override Visual GetVisualChild(int index) => children[index];
        public void Clear() { children.Clear(); visuals.Clear(); }
        public void Render(string id, Action<DrawingContext> draw)
        {
            if (!visuals.TryGetValue(id, out var visual)) { visual = new DrawingVisual(); visuals[id] = visual; children.Add(visual); }
            using var dc = visual.RenderOpen(); draw(dc);
        }
        public void Remove(string id) { if (visuals.Remove(id, out var visual)) children.Remove(visual); }
        public void SetTransform(string id, Transform? transform) { if (visuals.TryGetValue(id, out var visual)) visual.Transform = transform; }
    }
    private sealed class DrawLayer(Action<DrawingContext> render) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => render(dc);
    }
}
