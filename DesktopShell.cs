using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WidgesDesktopDotNet;

internal static class DesktopShell
{
    private static IntPtr _workerW;
    private static readonly int _taskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");

    internal static void Embed(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var workerW = EnsureWorkerW();
        if (workerW == IntPtr.Zero)
        {
            // WorkerW not available — fall back to pinning at the bottom of the normal Z-order.
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }

        GetWindowRect(hwnd, out var win);
        GetWindowRect(workerW, out var ww);

        // WPF creates WS_POPUP windows; parenting requires switching to WS_CHILD first,
        // otherwise the window keeps rendering in a layered plane above WorkerW.
        var style = (uint)GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, (int)((style & ~WsPopup) | WsChild));

        SetParent(hwnd, workerW);

        // Place at the bottom of WorkerW's z-order and snap to the correct parent-relative position.
        SetWindowPos(hwnd, HwndBottom, win.Left - ww.Left, win.Top - ww.Top, 0, 0, SwpNoSize | SwpNoActivate);
    }

    // Hook a window's WndProc to re-embed all widgets when Explorer restarts.
    internal static void AttachRestartHook(Window window, Action onRestart)
    {
        void Attach()
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null)
                return;

            IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
            {
                if (msg == _taskbarCreated)
                {
                    Invalidate();
                    onRestart();
                }

                return IntPtr.Zero;
            }

            source.AddHook(Hook);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Attach();
        else
            window.SourceInitialized += (_, _) => Attach();
    }

    internal static void Invalidate() => _workerW = IntPtr.Zero;

    // Returns screen position in logical (DIP) pixels — reliable even after SetParent.
    internal static (int Left, int Top) GetScreenPosition(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return ((int)Math.Round(window.Left), (int)Math.Round(window.Top));

        GetWindowRect(hwnd, out var r);
        var (dx, dy) = GetDpiScale(window);
        return ((int)Math.Round(r.Left / dx), (int)Math.Round(r.Top / dy));
    }

    internal static (double X, double Y) GetDpiScale(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual);
        if (source?.CompositionTarget is { } t)
            return (t.TransformToDevice.M11, t.TransformToDevice.M22);
        return (1.0, 1.0);
    }

    private static IntPtr EnsureWorkerW()
    {
        if (_workerW != IntPtr.Zero && IsWindow(_workerW))
            return _workerW;

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        // Poke Progman to spawn the WorkerW behind desktop icons; safe to repeat.
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                found = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        _workerW = found;
        return _workerW;
    }

    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlStyle = -16;
    private const uint WsPopup = 0x80000000;
    private const uint WsChild = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
}

internal static class WidgetDrag
{
    // Begins a drag operation. Call from MouseLeftButtonDown.
    // Uses SetWindowPos directly so it works correctly after SetParent into WorkerW.
    internal static void Begin(Window window, MouseButtonEventArgs e, Action onComplete)
    {
        if (e.ClickCount != 1)
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        GetCursorPos(out var cursor);
        GetWindowRect(hwnd, out var win);
        var anchorX = cursor.X - win.Left;
        var anchorY = cursor.Y - win.Top;

        void OnMove(object? s, MouseEventArgs _)
        {
            GetCursorPos(out var c);
            var parent = GetParent(hwnd);
            int px = 0, py = 0;
            if (parent != IntPtr.Zero)
            {
                GetWindowRect(parent, out var pr);
                px = pr.Left;
                py = pr.Top;
            }

            SetWindowPos(hwnd, IntPtr.Zero, c.X - anchorX - px, c.Y - anchorY - py, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        void OnUp(object? s, MouseButtonEventArgs _)
        {
            window.MouseMove -= OnMove;
            window.MouseLeftButtonUp -= OnUp;
            window.ReleaseMouseCapture();
            onComplete();
        }

        window.MouseMove += OnMove;
        window.MouseLeftButtonUp += OnUp;
        window.CaptureMouse();
        e.Handled = true;
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
}
