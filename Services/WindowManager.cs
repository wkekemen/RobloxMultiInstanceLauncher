using System.Runtime.InteropServices;
using System.Text;
using RobloxMultiLauncher.Interop;

namespace RobloxMultiLauncher.Services;

public sealed record MonitorOption(string DeviceName, string DisplayName);

/// <summary>Finds and controls top-level windows owned by a process (standard Win32 calls only).</summary>
public static class WindowManager
{
    private const int CompactWindowWidth = 640;
    private const int CompactWindowHeight = 360;

    public static IntPtr FindMainWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid && NativeMethods.IsWindowVisible(h) && GetTitle(h).Length > 0)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int len = NativeMethods.GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsMinimized(IntPtr hWnd) => NativeMethods.IsIconic(hWnd);

    public static bool IsOwnedBy(IntPtr hWnd, int pid)
    {
        if (!NativeMethods.IsWindow(hWnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint ownerPid);
        return ownerPid == (uint)pid;
    }

    public static bool Focus(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd)) return false;
        if (NativeMethods.IsIconic(hWnd)) NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        // Tapping Alt lets SetForegroundWindow succeed when another app owns the foreground.
        NativeMethods.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        return NativeMethods.SetForegroundWindow(hWnd);
    }

    public static bool Minimize(IntPtr hWnd) =>
        NativeMethods.IsWindow(hWnd) && NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);

    public static bool SetCompactSize(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd)) return false;

        bool resized = NativeMethods.SetWindowPos(
            hWnd, IntPtr.Zero, 0, 0, CompactWindowWidth, CompactWindowHeight,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (!resized)
            Logger.Warn($"Could not set Roblox window {hWnd} to {CompactWindowWidth}x{CompactWindowHeight} (Win32 error {Marshal.GetLastWin32Error()}).");
        return resized;
    }

    public static IReadOnlyList<MonitorOption> GetMonitors()
    {
        var monitors = new List<MonitorOption>();
        NativeMethods.MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                DeviceName = ""
            };
            if (NativeMethods.GetMonitorInfo(handle, ref info))
            {
                int width = info.WorkArea.Right - info.WorkArea.Left;
                int height = info.WorkArea.Bottom - info.WorkArea.Top;
                string primary = (info.Flags & 1) != 0 ? " (Primary)" : "";
                monitors.Add(new MonitorOption(info.DeviceName, $"{info.DeviceName} — {width} × {height}{primary}"));
            }
            else
            {
                Logger.Warn($"Could not read monitor information (Win32 error {Marshal.GetLastWin32Error()}).");
            }
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            Logger.Warn($"Could not enumerate displays (Win32 error {Marshal.GetLastWin32Error()}).");
        return monitors;
    }

    public static int ArrangeWindows(IReadOnlyCollection<IntPtr> windows, string? monitorDeviceName, int sessionsPerRow)
    {
        if (windows.Count == 0) return 0;
        var workArea = GetMonitorWorkArea(monitorDeviceName);
        if (workArea == null)
        {
            Logger.Warn($"Could not get the work area for monitor '{monitorDeviceName ?? "primary"}' (Win32 error {Marshal.GetLastWin32Error()}); using the primary monitor.");
            workArea = GetMonitorWorkArea(null);
        }
        if (workArea == null)
        {
            Logger.Warn($"Could not get the primary monitor work area (Win32 error {Marshal.GetLastWin32Error()}).");
            return windows.Count;
        }

        int width = workArea.Value.Right - workArea.Value.Left;
        int height = workArea.Value.Bottom - workArea.Value.Top;
        if (width <= 0 || height <= 0)
        {
            Logger.Warn("Could not arrange Roblox windows because the primary monitor work area is invalid.");
            return windows.Count;
        }

        int count = windows.Count;
        int columns = Math.Min(Math.Clamp(sessionsPerRow, 1, 12), count);
        int rows = (count + columns - 1) / columns;
        int windowWidth = Math.Min(CompactWindowWidth, width / columns);
        int windowHeight = Math.Min(CompactWindowHeight, height / rows);
        double scale = Math.Min(windowWidth / (double)CompactWindowWidth, windowHeight / (double)CompactWindowHeight);
        windowWidth = Math.Max(1, (int)Math.Floor(CompactWindowWidth * scale));
        windowHeight = Math.Max(1, (int)Math.Floor(CompactWindowHeight * scale));
        int failures = 0;
        int index = 0;

        foreach (var hWnd in windows)
        {
            int currentIndex = index++;
            if (!NativeMethods.IsWindow(hWnd))
            {
                failures++;
                continue;
            }

            if (NativeMethods.IsIconic(hWnd))
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

            int column = currentIndex % columns;
            int row = currentIndex / columns;
            int left = workArea.Value.Left + column * width / columns;
            int top = workArea.Value.Top + row * height / rows;

            if (!NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, left, top, windowWidth, windowHeight,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            {
                failures++;
                Logger.Warn($"Could not arrange Roblox window {hWnd} (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }

        return failures;
    }

    private static NativeMethods.Rect? GetMonitorWorkArea(string? deviceName)
    {
        NativeMethods.Rect? result = null;
        NativeMethods.MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                DeviceName = ""
            };
            if (!NativeMethods.GetMonitorInfo(handle, ref info)) return true;

            bool isPrimary = (info.Flags & 1) != 0;
            if (string.IsNullOrWhiteSpace(deviceName) ? isPrimary
                : string.Equals(info.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                result = info.WorkArea;
                return false;
            }
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            Logger.Warn($"Could not enumerate displays (Win32 error {Marshal.GetLastWin32Error()}).");
        return result;
    }
}
