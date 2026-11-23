using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public partial class BoardSurface : Canvas
{
    public void ResetPenButton() { trackedPen = null; buttonGate.Reset(); penPoll.Stop(); }
    public void NativePenSample(Point screen, bool pressed)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        nativePackets++; if (!StylusPositionFresh) UpdatePointer(point); HandlePenButton(pressed, true, "Windows Pen");
    }
    private long penMouseUntil;
    public void RawPenSample(Point screen, bool pressed, bool raw)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        if (!StylusPositionFresh) UpdatePointer(point);
        if (!raw) penMouseUntil = Environment.TickCount64 + 500;
        if (raw ? buttonGate.UpdateRaw(pressed, Environment.TickCount64) : buttonGate.Update(pressed, true, Environment.TickCount64))
            CyclePenTool(raw ? "HID Raw Input" : "Pen 우클릭");
    }
    private void HandlePenButton(bool down, bool native, string source)
    {
        if (!buttonGate.Update(down, native, Environment.TickCount64)) return;
        CyclePenTool(source);
    }
    private void CyclePenTool(string source)
    {
        EndGesture(); CommitEditor();
        Tool = Tool switch { CanvasTool.Pen => CanvasTool.Eraser, CanvasTool.Eraser => CanvasTool.Lasso, _ => CanvasTool.Pen };
        switches++; lastButton = source; suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500);
        ToolChanged?.Invoke(); PenToolSwitched?.Invoke($"S Pen Button · {Tool}");
    }
    private void ObservePen(StylusEventArgs e) => ObservePen(e, e.GetStylusPoints(this));
    private void ObservePen(StylusEventArgs e, StylusPointCollection points)
    {
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        trackedPen = e.StylusDevice; if (!penPoll.IsEnabled) penPoll.Start(); penPackets++;
        bool? packetDown = null;
        if (points.Count > 0 && points[^1].HasProperty(StylusPointProperties.BarrelButton))
            packetDown = points[^1].GetPropertyValue(StylusPointProperties.BarrelButton) != 0;
        bool? collectionDown = ReadButton(trackedPen);
        if (packetDown.HasValue || collectionDown.HasValue)
            HandlePenButton(packetDown == true || collectionDown == true, false, "WPF Pen 상태");
    }
    private static bool? ReadButton(StylusDevice device)
    {
        foreach (StylusButton button in device.StylusButtons)
            if (button.Guid == StylusPointProperties.BarrelButton.Id) return button.StylusButtonState == StylusButtonState.Down;
        return null;
    }
}
