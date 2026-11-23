using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThoughtCanvas;

public sealed class RawPenInput : IDisposable
{
    private readonly HwndSource source;
    private readonly Window window;
    private readonly Action<Point, bool, bool> sample;
    private readonly Dictionary<nint, Device?> devices = [];
    private int messages, reports, presses, penMouse;
    private int matched, parsed, parseErrors;
    private readonly HashSet<byte> reportIds = [];
    private readonly HashSet<string> observed = [];
    private string lastError = "없음";
    private readonly Queue<string> transitions = new();
    private string lastState = "";
    private string status;
    public string Summary => $"HID Raw Input v0.5.5: {status}, 장치 {devices.Values.Count(d => d != null)}/{devices.Count}, WM_INPUT {messages}, Report {reports}, Down {presses}, Pen 우클릭 {penMouse}\n" +
        $"Report ID: {string.Join(", ", reportIds.Order().Select(x => x.ToString("X2")))}\nID 일치 {matched}, 해석 성공 {parsed}, 실패 {parseErrors}, 최근 오류 {lastError}\n" +
        $"감지된 ON Usage: {string.Join(", ", observed.Order())}\n최근 상태 변화 (B=Barrel, I=Invert, E=Eraser):\n{string.Join("\n", transitions)}\n" +
        string.Join("\n", devices.Values.Where(d => d != null).SelectMany(d => d!.Buttons).Take(64).Select(c => $"Cap: ID={c.ReportId:X2} Page={c.Page:X4} Link={c.LinkCollection} Usage={c.UsageMin:X4}~{(c.IsRange != 0 ? c.UsageMax : c.UsageMin):X4}"));
    private sealed record Device(nint Data, ButtonCap[] Buttons);
    public RawPenInput(Window window, Action<Point, bool, bool> sample)
    {
        this.window = window; this.sample = sample;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        // Foreground only. Do not suppress legacy input or capture keyboard/media keys.
        var registrations = new[] { new Registration { Page = 13, Usage = 2, Flags = 0x2000, Target = source.Handle }, new Registration { Page = 13, Usage = 1, Flags = 0x2000, Target = source.Handle } };
        status = RegisterRawInputDevices(registrations, 2, (uint)Marshal.SizeOf<Registration>()) ? "등록됨" : $"등록 실패 {Marshal.GetLastWin32Error()}";
        source.AddHook(Hook);
    }
    public static bool IsPenMouse(ulong extra) => (extra & 0xFFFFFF00) == 0xFF515700 && (extra & 0x80) == 0;
    public static bool IsBarrelUsage(ushort page, ushort usage) => page == 13 && usage is 0x44 or 0x5A;
    // This app maps the integrated S Pen erase affordance to the same tool cycle.
    // Hover uses Invert; contact may replace it with Eraser. OR both to avoid a second edge.
    public static bool IsToolSwitchUsage(ushort page, ushort usage) => IsBarrelUsage(page, usage) || page == 13 && usage is 0x3C or 0x45;
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x00FE)
        {
            if (devices.Remove(lParam, out var removed) && removed != null) Marshal.FreeHGlobal(removed.Data);
            return 0;
        }
        if (!window.IsActive) return 0;
        if (message == 0x00FF)
        {
            messages++;
            try { ReadRaw(lParam); }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidDataException)
            { status = "Report 형식 확인 필요"; }
        }
        else if (message is 0x0204 or 0x0205 or 0x0206) // right down/up/double click, pen signature only
        {
            if (IsPenMouse(unchecked((ulong)GetMessageExtraInfo().ToInt64())) && GetCursorPos(out var point))
            {
                penMouse++; sample(new(point.X, point.Y), message != 0x0205, false);
                // Preserve normal mouse and Windows/WPF message processing.
            }
        }
        return 0;
    }
    public static List<byte[]> SplitReports(byte[] data, int offset, uint size, uint count)
    {
        if (size == 0 || count > 4096 || offset < 0 || offset > data.Length || (ulong)size * count > (ulong)(data.Length - offset))
            throw new InvalidDataException("HID Report 길이 오류");
        var result = new List<byte[]>();
        for (uint i = 0; i < count; i++) result.Add(data.AsSpan(checked(offset + (int)(i * size)), checked((int)size)).ToArray());
        return result;
    }
    private void ReadRaw(nint handle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawHeader>(), size = 0;
        if (GetRawInputData(handle, 0x10000003, 0, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 1_048_576) return;
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize + 8) return;
            var header = Marshal.PtrToStructure<RawHeader>(buffer); if (header.Type != 2) return;
            if (!devices.TryGetValue(header.Device, out var device))
            {
                if (devices.Count >= 32) return;
                device = LoadDevice(header.Device); devices[header.Device] = device;
            }
            if (device == null) return;
            var bytes = new byte[read]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
            uint reportSize = BitConverter.ToUInt32(bytes, (int)headerSize), count = BitConverter.ToUInt32(bytes, (int)headerSize + 4);
            foreach (var report in SplitReports(bytes, (int)headerSize + 8, reportSize, count))
            {
                reports++;
                reportIds.Add(report[0]);
                var caps = device.Buttons.Where(c => c.ReportId == report[0]).ToArray();
                if (caps.Length == 0) continue;
                matched++;
                bool known = false, pressed = false;
                bool barrel = false, inverted = false, eraser = false;
                foreach (var cap in caps.DistinctBy(c => (c.Page, c.LinkCollection)))
                {
                    var usages = new ushort[1024]; uint length = (uint)usages.Length;
                    int result = HidP_GetUsages(0, cap.Page, cap.LinkCollection, usages, ref length, device.Data, report, (uint)report.Length);
                    if (result != 0x00110000) { parseErrors++; lastError = $"0x{result:X8} (ID={report[0]:X2}, Page={cap.Page:X4}, Link={cap.LinkCollection})"; continue; }
                    parsed++;
                    // Only a successfully decoded tool-switch collection may release the gate.
                    bool hasBarrel = caps.Any(c => c.Page == cap.Page && c.LinkCollection == cap.LinkCollection &&
                        (ContainsUsage(c, 0x44) || ContainsUsage(c, 0x5A) || ContainsUsage(c, 0x3C) || ContainsUsage(c, 0x45)) && c.Page == 13);
                    known |= hasBarrel;
                    foreach (var usage in usages.Take((int)Math.Min(length, (uint)usages.Length)))
                    {
                        if (observed.Count < 128) observed.Add($"{cap.Page:X4}:{usage:X4}");
                        if (hasBarrel)
                        {
                            pressed |= IsToolSwitchUsage(cap.Page, usage);
                            barrel |= IsBarrelUsage(cap.Page, usage);
                            inverted |= cap.Page == 13 && usage == 0x3C;
                            eraser |= cap.Page == 13 && usage == 0x45;
                        }
                    }
                }
                if (known)
                {
                    string state = $"B={(barrel ? 1 : 0)} I={(inverted ? 1 : 0)} E={(eraser ? 1 : 0)}";
                    if (state != lastState)
                    {
                        lastState = state;
                        if (transitions.Count >= 24) transitions.Dequeue();
                        transitions.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {state}");
                    }
                    if (GetCursorPos(out var point)) { if (pressed) presses++; sample(new(point.X, point.Y), pressed, true); }
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static Device? LoadDevice(nint handle)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(handle, 0x20000005, 0, ref size) == uint.MaxValue || size == 0 || size > 1_048_576) return null;
        nint data = Marshal.AllocHGlobal((int)size);
        bool retained = false;
        try
        {
            if (GetRawInputDeviceInfo(handle, 0x20000005, data, ref size) == uint.MaxValue || HidP_GetCaps(data, out var caps) != 0x00110000 || caps.ButtonCount == 0 || caps.ButtonCount > 1024) return null;
            ushort count = caps.ButtonCount; var buttons = new ButtonCap[count];
            if (HidP_GetButtonCaps(0, buttons, ref count, data) != 0x00110000) return null;
            retained = true; return new(data, buttons.Take(count).ToArray());
        }
        finally { if (!retained) Marshal.FreeHGlobal(data); }
    }
    private static bool ContainsUsage(ButtonCap cap, ushort usage) => cap.IsRange != 0
        ? cap.UsageMin <= usage && cap.UsageMax >= usage : cap.UsageMin == usage;
    public void Dispose()
    {
        source.RemoveHook(Hook);
        foreach (var device in devices.Values) if (device != null) Marshal.FreeHGlobal(device.Data);
        devices.Clear();
        RegisterRawInputDevices([new() { Page = 13, Usage = 2, Flags = 1 }, new() { Page = 13, Usage = 1, Flags = 1 }], 2, (uint)Marshal.SizeOf<Registration>());
    }
    [StructLayout(LayoutKind.Sequential)] private struct Registration { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct Caps { [FieldOffset(46)] public ushort ButtonCount; }
    [StructLayout(LayoutKind.Explicit, Size = 72)] private struct ButtonCap
    {
        [FieldOffset(0)] public ushort Page;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint handle, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] private static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint data, out Caps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, [Out] ButtonCap[] caps, ref ushort count, nint data);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int type, ushort page, ushort collection, [Out] ushort[] usages, ref uint count, nint data, byte[] report, uint length);
}
