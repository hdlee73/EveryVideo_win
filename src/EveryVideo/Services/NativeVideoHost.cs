using System.Runtime.InteropServices;

namespace EveryVideo.Services;

/// <summary>
/// LibVLCSharp.WPF 가 동영상용으로 만드는 Win32 "static" 창은 기본 배경이 밝은 회색이라
/// 영상 바깥(위아래·좌우 여백)이나 재생이 끝난 순간 하얗게 보인다. 그 창을 검게 칠하도록 바꾼다.
/// </summary>
public static class NativeVideoHost
{
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    private const int GwlpWndProc = -4, GwlStyle = -16;
    private const uint WmPaint = 0x000F, WmEraseBkgnd = 0x0014, WmNcDestroy = 0x0082;
    private const long WsClipChildren = 0x02000000;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect r, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);

    private static readonly Dictionary<IntPtr, (IntPtr prev, WndProc proc)> Hooked = new();

    public static void MakeBlack(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Hooked.ContainsKey(hwnd)) return;
        var style = (long)GetWindowLongPtr(hwnd, GwlStyle);
        SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)(style | WsClipChildren));
        WndProc proc = (h, m, w, l) => Proc(h, m, w, l);
        var prev = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(proc));
        Hooked[hwnd] = (prev, proc);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (!Hooked.TryGetValue(h, out var entry)) return IntPtr.Zero;
        var black = GetStockObject(4); // BLACK_BRUSH
        switch (m)
        {
            case WmEraseBkgnd:
                if (GetClientRect(h, out var rc)) FillRect(w, ref rc, black);
                return (IntPtr)1;
            case WmPaint:
                var dc = BeginPaint(h, out var ps);
                if (dc != IntPtr.Zero) FillRect(dc, ref ps.Paint, black);
                EndPaint(h, ref ps);
                return IntPtr.Zero;
            case WmNcDestroy:
                Hooked.Remove(h);
                SetWindowLongPtr(h, GwlpWndProc, entry.prev);
                return CallWindowProcW(entry.prev, h, m, w, l);
        }
        return CallWindowProcW(entry.prev, h, m, w, l);
    }
}
