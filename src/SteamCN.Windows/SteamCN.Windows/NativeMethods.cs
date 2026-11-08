using System.Runtime.InteropServices;
using System.Text;

namespace SteamCN.Windows;

internal static class NativeMethods
{
	public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

	public struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;

		public readonly int Width => Right - Left;

		public readonly int Height => Bottom - Top;
	}

	public struct MARGINS
	{
		public int cxLeftWidth;

		public int cxRightWidth;

		public int cyTopHeight;

		public int cyBottomHeight;
	}

	public const int GWL_STYLE = -16;

	public const int GWL_EXSTYLE = -20;

	public const int WS_EX_LAYERED = 524288;

	public const int WS_EX_TRANSPARENT = 32;

	public const int WS_EX_NOACTIVATE = 134217728;

	public const int WS_EX_TOOLWINDOW = 128;

	public const int WS_EX_TOPMOST = 8;

	public const uint SWP_NOSIZE = 1u;

	public const uint SWP_NOMOVE = 2u;

	public const uint SWP_NOACTIVATE = 16u;

	public const uint SWP_SHOWWINDOW = 64u;

	public const uint SWP_HIDEWINDOW = 128u;

	public const nint HWND_TOPMOST = -1;

	public const nint HWND_NOTOPMOST = -2;

	public const uint LWA_COLORKEY = 1u;

	public const uint LWA_ALPHA = 2u;

	public const int DWMWA_EXCLUDED_FROM_PEEK = 12;

	public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

	public const uint PROCESS_QUERY_INFORMATION = 1024u;

	public const uint PROCESS_VM_READ = 16u;

	public const int PW_RENDERFULLCONTENT = 2;

	public const uint SRCCOPY = 13369376u;

	public const int MONITOR_DEFAULTTONEAREST = 2;

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsIconic(nint hWnd);

	[DllImport("user32.dll")]
	public static extern nint GetForegroundWindow();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetWindowTextLength(nint hWnd);

	[DllImport("user32.dll")]
	public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	public static extern nint GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll")]
	public static extern uint GetWindowStyle(nint hWnd);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CloseHandle(nint hObject);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern nint GetModuleHandle(string lpModuleName);

	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(nint hWnd);

	[DllImport("user32.dll")]
	public static extern uint GetDpiForSystem();

	[DllImport("shcore.dll")]
	public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll")]
	public static extern nint GetWindowDC(nint hWnd);

	[DllImport("user32.dll")]
	public static extern int ReleaseDC(nint hWnd, nint hDC);

	[DllImport("gdi32.dll")]
	public static extern nint CreateCompatibleDC(nint hdc);

	[DllImport("gdi32.dll")]
	public static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

	[DllImport("gdi32.dll")]
	public static extern nint SelectObject(nint hdc, nint hgdiobj);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool PrintWindow(nint hWnd, nint hdcBlt, int nFlags);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DeleteObject(nint hObject);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DeleteDC(nint hdc);

	[DllImport("gdi32.dll")]
	public static extern int GetDeviceCaps(nint hdc, int nIndex);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

	[DllImport("dwmapi.dll")]
	public static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

	[DllImport("dwmapi.dll")]
	public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);
}
