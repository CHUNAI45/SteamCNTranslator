using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

internal static class SteamWebClient
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint window, NativeMethods.EnumWindowsProc callback, nint parameter);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    internal static bool Contains(ScreenRect outer, ScreenRect inner) =>
        !inner.IsEmpty && inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    internal static bool IsMainWindow(nint handle, out int processId, out string path)
    {
        processId = 0;
        path = "";
        if (!NativeMethods.IsWindow(handle) || GetWindow(handle, 4) != 0 ||
            WindowTitle(handle) != "Steam" || ClassName(handle) is not ("SDL_app" or "vguiPopupWindow"))
            return false;
        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        path = ProcessPath(pid);
        string fileName = Path.GetFileName(path);
        if (fileName.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase))
        {
            bool fromSteam = false;
            foreach (Process steam in Process.GetProcessesByName("steam"))
            {
                using (steam)
                {
                    string root = Path.GetDirectoryName(ProcessPath((uint)steam.Id)) ?? "";
                    if (root.Length > 0 && path.StartsWith(Path.Combine(root, "bin", "cef") +
                        Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        fromSteam = true;
                }
            }
            if (!fromSteam) return false;
        }
        else if (!fileName.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        processId = (int)pid;
        return true;
    }

    internal static bool IsLive(SteamWindowInfo window)
    {
        uint thread = NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid);
        return thread != 0 && pid == window.ProcessId && GetWindow(window.Handle, 4) == 0;
    }

    internal static bool IsForeground(SteamWindowInfo window) =>
        NativeMethods.GetForegroundWindow() == window.Handle && IsLive(window) &&
        NativeMethods.IsWindowVisible(window.Handle) && !NativeMethods.IsIconic(window.Handle) &&
        DwmGetWindowAttribute(window.Handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked == 0;

    internal static bool TryClientBounds(nint handle, out ScreenRect bounds)
    {
        bounds = default;
        nint previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) return false;
        try
        {
            Point origin = default;
            if (!NativeMethods.GetClientRect(handle, out var rect) ||
                !ClientToScreen(handle, ref origin) || rect.Width <= 0 || rect.Height <= 0)
                return false;
            var end = new Point { X = rect.Right, Y = rect.Bottom };
            if (!ClientToScreen(handle, ref end) || end.X <= origin.X || end.Y <= origin.Y)
                return false;
            bounds = new ScreenRect(origin.X, origin.Y, end.X - origin.X, end.Y - origin.Y);
            return true;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    internal static bool TryWebPane(SteamWindowInfo window, out nint pane, out ScreenRect viewport)
    {
        pane = 0;
        viewport = default;
        if (!IsForeground(window) || !TryClientBounds(window.Handle, out var client))
            return false;

        nint candidate = 0;
        ScreenRect candidateBounds = default;
        bool ambiguous = false;
        EnumChildWindows(window.Handle, (child, _) =>
        {
            if (ClassName(child) != "CefBrowserWindow" || !NativeMethods.IsWindowVisible(child) ||
                GetAncestor(child, 2) != window.Handle ||
                !TryClientBounds(child, out var rect) || !Contains(client, rect) ||
                rect.Y <= client.Y || rect.Width < 100 || rect.Height < 80)
                return true;

            if (candidate != 0)
            {
                ambiguous = true;
                return false;
            }
            candidate = child;
            candidateBounds = rect;
            return true;
        }, 0);
        if (ambiguous || candidate == 0) return false;
        pane = candidate;
        viewport = candidateBounds;
        return true;
    }

    internal static string WindowTitle(nint handle)
    {
        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    private static string ClassName(nint handle)
    {
        var name = new StringBuilder(128);
        NativeMethods.GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }

    private static string ProcessPath(uint pid)
    {
        nint process = NativeMethods.OpenProcess(0x1000, false, pid);
        if (process == 0) return "";
        try
        {
            var path = new StringBuilder(1024);
            uint length = (uint)path.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : "";
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}

