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
    // 진단용 ON Usage: (Page << 16 | Usage). 문자열은 Summary를 열 때만 만듭니다.
    private readonly HashSet<uint> observed = [];
    private string lastError = "없음";
    private readonly Queue<string> transitions = new();
    private int lastState = -1;
    private string status;
    // 매 Report마다 새로 할당하지 않도록 재사용하는 버퍼 (UI thread 전용)
    private nint rawBuffer; private uint rawCapacity;
    private byte[] rawBytes = [];
    private byte[] report = [];
    private readonly ushort[] usages = new ushort[1024];
    public string Summary => $"HID Raw Input v0.5.5: {status}, 장치 {devices.Values.Count(d => d != null)}/{devices.Count}, WM_INPUT {messages}, Report {reports}, Down {presses}, Pen 우클릭 {penMouse}\n" +
        $"Report ID: {string.Join(", ", reportIds.Order().Select(x => x.ToString("X2")))}\nID 일치 {matched}, 해석 성공 {parsed}, 실패 {parseErrors}, 최근 오류 {lastError}\n" +
        $"감지된 ON Usage: {string.Join(", ", observed.Order().Select(x => $"{x >> 16:X4}:{x & 0xFFFF:X4}"))}\n최근 상태 변화 (B=Barrel, I=Invert, E=Eraser):\n{string.Join("\n", transitions)}\n" +
        string.Join("\n", devices.Values.Where(d => d != null).SelectMany(d => d!.Buttons).Take(64).Select(c => $"Cap: ID={c.ReportId:X2} Page={c.Page:X4} Link={c.LinkCollection} Usage={c.UsageMin:X4}~{(c.IsRange != 0 ? c.UsageMax : c.UsageMin):X4}"));
    // Report ID별로 해석할 Collection 목록. 장치의 Button Caps는 바뀌지 않으므로 처음 한 번만 계산합니다.
    private readonly record struct Collection(ushort Page, ushort Link, bool HasToolSwitch);
    private sealed class Device(nint data, ButtonCap[] buttons)
    {
        public nint Data { get; } = data;
        public ButtonCap[] Buttons { get; } = buttons;
        private readonly Dictionary<byte, Collection[]> byReport = [];
        public Collection[] For(byte reportId)
        {
            if (byReport.TryGetValue(reportId, out var cached)) return cached;
            var caps = Buttons.Where(c => c.ReportId == reportId).ToArray();
            // Only a successfully decoded tool-switch collection may release the gate.
            var result = caps.DistinctBy(c => (c.Page, c.LinkCollection)).Select(cap => new Collection(cap.Page, cap.LinkCollection,
                caps.Any(c => c.Page == cap.Page && c.LinkCollection == cap.LinkCollection &&
                    (ContainsUsage(c, 0x44) || ContainsUsage(c, 0x5A) || ContainsUsage(c, 0x3C) || ContainsUsage(c, 0x45)) && c.Page == 13))).ToArray();
            byReport[reportId] = result; return result;
        }
    }
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
    private static void ValidateReports(int dataLength, int offset, uint size, uint count)
    {
        if (size == 0 || count > 4096 || offset < 0 || offset > dataLength || (ulong)size * count > (ulong)(dataLength - offset))
            throw new InvalidDataException("HID Report 길이 오류");
    }
    public static List<byte[]> SplitReports(byte[] data, int offset, uint size, uint count)
    {
        ValidateReports(data.Length, offset, size, count);
        var result = new List<byte[]>();
        for (uint i = 0; i < count; i++) result.Add(data.AsSpan(checked(offset + (int)(i * size)), checked((int)size)).ToArray());
        return result;
    }
    private void ReadRaw(nint handle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawHeader>(), size = 0;
        if (GetRawInputData(handle, 0x10000003, 0, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 1_048_576) return;
        if (rawCapacity < size)
        {
            if (rawBuffer != 0) Marshal.FreeHGlobal(rawBuffer);
            rawBuffer = 0; rawCapacity = 0;
            rawBuffer = Marshal.AllocHGlobal((int)size); rawCapacity = size;
        }
        uint read = GetRawInputData(handle, 0x10000003, rawBuffer, ref size, headerSize);
        if (read == uint.MaxValue || read < headerSize + 8) return;
        var header = Marshal.PtrToStructure<RawHeader>(rawBuffer); if (header.Type != 2) return;
        if (!devices.TryGetValue(header.Device, out var device))
        {
            if (devices.Count >= 32) return;
            device = LoadDevice(header.Device); devices[header.Device] = device;
        }
        if (device == null) return;
        if (rawBytes.Length < read) rawBytes = new byte[Math.Max(read, 256)];
        Marshal.Copy(rawBuffer, rawBytes, 0, (int)read);
        uint reportSize = BitConverter.ToUInt32(rawBytes, (int)headerSize), count = BitConverter.ToUInt32(rawBytes, (int)headerSize + 4);
        int start = (int)headerSize + 8;
        ValidateReports((int)read, start, reportSize, count);
        // HidP_GetUsages는 Report 길이와 같은 배열을 받으므로, 같은 크기의 배열 하나를 재사용합니다.
        if (report.Length != reportSize) report = new byte[reportSize];
        for (uint index = 0; index < count; index++)
        {
            Buffer.BlockCopy(rawBytes, checked(start + (int)(index * reportSize)), report, 0, (int)reportSize);
            reports++;
            reportIds.Add(report[0]);
            var collections = device.For(report[0]);
            if (collections.Length == 0) continue;
            matched++;
            bool known = false, pressed = false;
            bool barrel = false, inverted = false, eraser = false;
            foreach (var cap in collections)
            {
                uint length = (uint)usages.Length;
                int result = HidP_GetUsages(0, cap.Page, cap.Link, usages, ref length, device.Data, report, (uint)report.Length);
                if (result != 0x00110000) { parseErrors++; lastError = $"0x{result:X8} (ID={report[0]:X2}, Page={cap.Page:X4}, Link={cap.Link})"; continue; }
                parsed++;
                known |= cap.HasToolSwitch;
                int used = (int)Math.Min(length, (uint)usages.Length);
                for (int i = 0; i < used; i++)
                {
                    ushort usage = usages[i];
                    if (observed.Count < 128) observed.Add((uint)cap.Page << 16 | usage);
                    if (cap.HasToolSwitch)
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
                int state = (barrel ? 1 : 0) | (inverted ? 2 : 0) | (eraser ? 4 : 0);
                if (state != lastState)
                {
                    lastState = state;
                    if (transitions.Count >= 24) transitions.Dequeue();
                    transitions.Enqueue($"{DateTime.Now:HH:mm:ss.fff} B={(barrel ? 1 : 0)} I={(inverted ? 1 : 0)} E={(eraser ? 1 : 0)}");
                }
                // Button 판정 Gate가 Raw 경로를 우선 경로로 유지하도록 매 Report마다 전달합니다 (호출 빈도 유지).
                if (GetCursorPos(out var point)) { if (pressed) presses++; sample(new(point.X, point.Y), pressed, true); }
            }
        }
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
        if (rawBuffer != 0) { Marshal.FreeHGlobal(rawBuffer); rawBuffer = 0; rawCapacity = 0; }
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
