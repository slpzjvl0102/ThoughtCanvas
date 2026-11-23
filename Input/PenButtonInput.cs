using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThoughtCanvas;

// Shared edge detector: held packets cannot repeatedly cycle tools.
public sealed class PenButtonGate
{
    private bool down;
    private long lastPress = long.MinValue / 2, lastNative = long.MinValue / 2;
    private long lastRaw = long.MinValue / 2;
    public bool UpdateRaw(bool pressed, long now)
    {
        lastRaw = now;
        return Edge(pressed, now);
    }
    private bool Edge(bool pressed, long now)
    {
        bool rising = pressed && !down; down = pressed;
        if (!rising || now - lastPress < 180) return false;
        lastPress = now; return true;
    }
    public bool Update(bool pressed, bool native, long now)
    {
        if (now - lastRaw < 500) return false;
        if (native) lastNative = now;
        else if (now - lastNative < 500) return false;
        return Edge(pressed, now);
    }
    public void Reset() { down = false; lastNative = lastRaw = long.MinValue / 2; }
}

// Observe pen messages without consuming them; WPF retains pressure, touch and handwriting handling.
public sealed class NativePenObserver : IDisposable
{
    private readonly HwndSource source;
    private readonly Action<Point, bool> sample;
    private readonly Action leave;
    private uint? penId;
    public NativePenObserver(Window window, Action<Point, bool> sample, Action leave)
    {
        this.sample = sample; this.leave = leave;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        source.AddHook(Hook);
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        uint id = (uint)(wParam.ToInt64() & 0xffff);
        if (message is 0x024A or 0x024C) // LEAVE, CAPTURECHANGED
        {
            if (penId == id) { penId = null; leave(); }
        }
        else if (message is 0x0245 or 0x0246 or 0x0247 or 0x0249) // UPDATE, DOWN, UP, ENTER
        {
            if (GetPointerPenInfo(id, out var info))
            {
                penId = id;
                sample(new Point(info.Pointer.Pixel.X, info.Pointer.Pixel.Y), (info.Flags & 1) != 0);
            }
        }
        return 0;
    }
    public void Dispose() => source.RemoveHook(Hook);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public nint SourceDevice, Target;
        public NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PenInfo
    {
        public PointerInfo Pointer;
        public uint Flags, Mask, Pressure, Rotation;
        public int TiltX, TiltY;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfo(uint pointerId, out PenInfo penInfo);
}
