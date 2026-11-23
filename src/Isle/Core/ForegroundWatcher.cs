using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Isle.Core;

/// <summary>
/// Event-driven foreground-window tracking (SetWinEventHook, no polling). Start once on the UI thread.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    readonly Native.WinEventDelegate _callback; // keep alive
    IntPtr _hook;

    public event Action<IntPtr>? Changed;

    public ForegroundWatcher() => _callback = OnEvent;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        try
        {
            Changed?.Invoke(hwnd);
        }
        catch (Exception ex) { Logger.Error("foreground handler", ex); }
    }

    public static bool IsOwn(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Process name without extension ("chrome"), or "" when it cannot be read.</summary>
    public static string ProcessName(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        return Native.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    internal static bool TryGetExternalWindowBounds(IntPtr hwnd, out Native.RECT bounds)
    {
        bounds = default;
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || IsOwn(hwnd)) return false;
        if (Native.DwmGetWindowAttributeInt(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<Native.RECT>()) == 0
            || Native.GetWindowRect(hwnd, out bounds);
    }

    internal static IReadOnlyList<string> OpenAppNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Native.EnumWindows((hwnd, _) =>
        {
            if (TryGetExternalWindowBounds(hwnd, out var bounds) && bounds.Width >= 40 && bounds.Height >= 30 &&
                ClassName(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            {
                var name = ProcessName(hwnd);
                if (name.Length > 0) names.Add(name);
            }
            return true;
        }, IntPtr.Zero);
        return [.. names];
    }

    /// <summary>True when <paramref name="hwnd"/> is a real fullscreen window (game, video, F11 browser) on the given monitor.</summary>
    public static bool IsFullscreen(IntPtr hwnd, ScreenInfo screen)
    {
        if (hwnd == IntPtr.Zero || IsOwn(hwnd)) return false;
        // Windows' own verdict: 2 = busy (full-screen app), 3 = D3D exclusive full screen, 4 = presentation mode
        if (screen.Handle != IntPtr.Zero && Native.MonitorFromWindow(hwnd, 0) == screen.Handle &&
            Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4) return true;
        var cls = ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow") return false;
        // a maximized window (browsers draw their own title bar, so no WS_CAPTION) is not full screen
        if (Native.IsZoomed(hwnd)) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Left + screen.Width && r.Bottom >= screen.Top + screen.Height;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
