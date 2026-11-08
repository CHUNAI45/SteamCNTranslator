using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SteamCN.App.Services;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.App;

public class FloatingBallWindow : Window
{
	public enum BallState
	{
		Green,
		Yellow,
		Red,
		Gray
	}

	private readonly ISettingsManager _settingsManager;

	private readonly TranslationOrchestrator _orchestrator;

	private readonly DispatcherTimer _clickTimer;

	private (ScreenPoint Cursor, ScreenRect Window)? _press;

	private bool _dragging;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_NOACTIVATE = 134217728;

	private const int WS_EX_TOOLWINDOW = 128;

	private const int WM_CANCELMODE = 0x001F;
	private const int WM_MOUSEMOVE = 0x0200;
	private const int WM_LBUTTONDOWN = 0x0201;
	private const int WM_LBUTTONUP = 0x0202;
	private const int WM_LBUTTONDBLCLK = 0x0203;
	private const int WM_RBUTTONUP = 0x0205;
	private const int WM_CAPTURECHANGED = 0x0215;
	private const int MK_LBUTTON = 0x0001;
	private const uint MoveOnly = 0x0015; // Keep size, Z order, and activation.

	private BallState _lastState = BallState.Gray;

	private DateTime _lastErrorTime = DateTime.MinValue;

	internal Border BallFace;

	internal Popup QuickPanel;

	internal Button BtnPauseResume;

	internal Button BtnShowHide;

	[StructLayout(LayoutKind.Sequential)]
	private struct ScreenPoint
	{
		public int X, Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ScreenRect
	{
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public ScreenRect Bounds, WorkArea;
		public uint Flags;
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll")]
	private static extern nint SetCapture(nint window);

	[DllImport("user32.dll")]
	private static extern nint GetCapture();

	[DllImport("user32.dll")]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out ScreenPoint point);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint window, out ScreenRect bounds);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

	public FloatingBallWindow(ISettingsManager settingsManager, TranslationOrchestrator orchestrator)
	{
		InitializeComponent();
		_settingsManager = settingsManager;
		_orchestrator = orchestrator;
		_clickTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		_clickTimer.Tick += (object? s, EventArgs e) =>
		{
			_clickTimer.Stop();
			if (_press == null) QuickPanel.IsOpen = !QuickPanel.IsOpen;
		};
		Loaded += OnLoaded;
		Closing += OnClosing;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		WindowInteropHelper windowInteropHelper = new WindowInteropHelper(this);
		SetWindowLong(windowInteropHelper.Handle, GWL_EXSTYLE, GetWindowLong(windowInteropHelper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
		HwndSource.FromHwnd(windowInteropHelper.Handle).AddHook(WndProc);
		FloatingBallSettings floatingBall = _settingsManager.Settings.FloatingBall;
		double num = floatingBall.BallX;
		double num2 = floatingBall.BallY;
		if (num == -1.0 && num2 == -1.0)
		{
			Rect workArea = SystemParameters.WorkArea;
			num = workArea.Right - Width - 20.0;
			num2 = workArea.Bottom - Height - 20.0;
		}
		Left = num;
		Top = num2;
		Opacity = Math.Clamp(floatingBall.Opacity, 0.3, 1.0);
		_orchestrator.RunningStateChanged += OnRunningStateChanged;
		_orchestrator.StatusMessage += OnStatusMessage;
		_orchestrator.ErrorOccurred += OnErrorOccurred;
		_orchestrator.Scheduler.PipelineDiagnostics += OnPipelineDiagnostics;
		UpdateBallState((!_orchestrator.IsRunning) ? BallState.Gray : BallState.Green);
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		switch (msg)
		{
			case WM_LBUTTONDOWN:
			case WM_LBUTTONDBLCLK:
				if (GetCursorPos(out var cursor) && GetWindowRect(hwnd, out var bounds))
				{
					SetCapture(hwnd);
					if (GetCapture() == hwnd)
					{
						_press = (cursor, bounds);
						_dragging = false;
					}
				}
				handled = true;
				break;
			case WM_MOUSEMOVE when _press is { } press:
				if ((wParam.ToInt64() & MK_LBUTTON) == 0 || GetCapture() != hwnd)
				{
					CancelPress(hwnd);
					break;
				}
				if (GetCursorPos(out var current))
				{
					int dx = current.X - press.Cursor.X;
					int dy = current.Y - press.Cursor.Y;
					var dpi = VisualTreeHelper.GetDpi(this);
					if (_dragging || Math.Abs(dx) >= 4 * dpi.DpiScaleX || Math.Abs(dy) >= 4 * dpi.DpiScaleY)
					{
						_dragging = true;
						_clickTimer.Stop();
						SetWindowPos(hwnd, 0, press.Window.Left + dx, press.Window.Top + dy, 0, 0, MoveOnly);
					}
				}
				handled = true;
				break;
			case WM_LBUTTONUP when _press != null:
				bool dragged = _dragging;
				ClearPress(hwnd);
				if (dragged)
				{
					if (_settingsManager.Settings.FloatingBall.SnapToEdge) SnapToNearestEdge(hwnd);
					SavePosition();
				}
				else if (ReleasedInside(hwnd, lParam))
				{
					if (_clickTimer.IsEnabled)
					{
						_clickTimer.Stop();
						QuickPanel.IsOpen = false;
						if (Application.Current is App app) app.ShowMainWindow();
					}
					else
					{
						_clickTimer.Start();
					}
				}
				handled = true;
				break;
			case WM_CAPTURECHANGED when lParam != hwnd:
			case WM_CANCELMODE:
				CancelPress(hwnd);
				break;
			case WM_RBUTTONUP:
				CancelPress(hwnd);
				_clickTimer.Stop();
				QuickPanel.IsOpen = !QuickPanel.IsOpen;
				handled = true;
				break;
		}
		return 0;
	}

	private void CancelPress(nint hwnd)
	{
		if (_press == null) return;
		bool dragged = _dragging;
		ClearPress(hwnd);
		_clickTimer.Stop();
		if (dragged) SavePosition();
	}

	private void ClearPress(nint hwnd)
	{
		_press = null;
		_dragging = false;
		if (GetCapture() == hwnd) ReleaseCapture();
	}

	private static bool ReleasedInside(nint hwnd, nint lParam)
	{
		int packed = unchecked((int)lParam);
		int x = unchecked((short)packed);
		int y = unchecked((short)(packed >> 16));
		return GetWindowRect(hwnd, out var bounds) &&
			x >= 0 && y >= 0 && x < bounds.Right - bounds.Left && y < bounds.Bottom - bounds.Top;
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		_clickTimer.Stop();
		ClearPress(new WindowInteropHelper(this).Handle);
		SavePosition();
		_orchestrator.RunningStateChanged -= OnRunningStateChanged;
		_orchestrator.StatusMessage -= OnStatusMessage;
		_orchestrator.ErrorOccurred -= OnErrorOccurred;
		_orchestrator.Scheduler.PipelineDiagnostics -= OnPipelineDiagnostics;
	}

	private void OnDeactivated(object? sender, EventArgs e)
	{
		if (QuickPanel.IsOpen)
		{
			QuickPanel.IsOpen = false;
		}
	}

	private void SnapToNearestEdge(nint hwnd)
	{
		var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor) || !GetWindowRect(hwnd, out var bounds))
			return;

		var work = monitor.WorkArea;
		var dpi = VisualTreeHelper.GetDpi(this);
		int x = bounds.Left;
		int y = bounds.Top;
		if (bounds.Left - work.Left < 20 * dpi.DpiScaleX) x = work.Left;
		else if (work.Right - bounds.Right < 20 * dpi.DpiScaleX) x += work.Right - bounds.Right;
		if (bounds.Top - work.Top < 20 * dpi.DpiScaleY) y = work.Top;
		else if (work.Bottom - bounds.Bottom < 20 * dpi.DpiScaleY) y += work.Bottom - bounds.Bottom;
		if (x != bounds.Left || y != bounds.Top) SetWindowPos(hwnd, 0, x, y, 0, 0, MoveOnly);
	}

	private void SavePosition()
	{
		try
		{
			FloatingBallSettings floatingBall = _settingsManager.Settings.FloatingBall;
			floatingBall.BallX = Left;
			floatingBall.BallY = Top;
			_settingsManager.SaveAsync();
		}
		catch
		{
		}
	}

	public void UpdateBallState(BallState state)
	{
		Dispatcher.Invoke(() =>
		{
			Border ballFace = BallFace;
			ballFace.Background = AppUi.Brush(state switch
			{
				BallState.Green => "UiLimeTint",
				BallState.Yellow => "UiAmberTint",
				BallState.Red => "UiPinkTint",
				_ => "UiCanvas"
			});
		});
	}

	private void OnRunningStateChanged(bool isRunning)
	{
		if (!isRunning)
		{
			_lastState = BallState.Gray;
			UpdateBallState(BallState.Gray);
		}
		else if (_lastState == BallState.Gray)
		{
			_lastState = BallState.Green;
			UpdateBallState(BallState.Green);
		}
	}

	private void OnStatusMessage(string message)
	{
		if (_lastState != BallState.Yellow)
		{
			_lastState = BallState.Yellow;
			UpdateBallState(BallState.Yellow);
		}
		Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
		{
			if (!_orchestrator.Scheduler.IsProcessing && _lastState == BallState.Yellow)
			{
				_lastState = BallState.Green;
				UpdateBallState(BallState.Green);
			}
		}));
	}

	private void OnErrorOccurred(string error)
	{
		_lastErrorTime = DateTime.Now;
		_lastState = BallState.Red;
		UpdateBallState(BallState.Red);
	}

	private void OnPipelineDiagnostics(object? sender, PipelineDiagnostics diag)
	{
		if (_lastState == BallState.Red && (DateTime.Now - _lastErrorTime).TotalSeconds > 5.0)
		{
			_lastState = ((!_orchestrator.IsRunning) ? BallState.Gray : BallState.Green);
			UpdateBallState(_lastState);
		}
	}

	private void OnPauseResumeClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		bool flag = _orchestrator.TogglePause();
		BtnPauseResume.Content = flag ? "恢复翻译" : "暂停翻译";
		UpdateBallState(flag ? BallState.Gray : BallState.Green);
		_lastState = (flag ? BallState.Gray : BallState.Green);
	}

	private void OnShowHideClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		_orchestrator.ToggleOverlayVisibility();
	}

	private void OnModeClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		if (sender is Button { Tag: string tag })
		{
			AppSettings settings = _settingsManager.Settings;
			AppSettings appSettings = settings;
			appSettings.DisplayMode = tag switch
			{
				"Bilingual" => DisplayMode.Bilingual, 
				"ChineseOnly" => DisplayMode.Chinese, 
				"OriginalOnly" => DisplayMode.Original, 
				_ => settings.DisplayMode, 
			};
			_settingsManager.SaveAsync();
			_orchestrator.RefreshDisplayMode();
		}
	}

	private void OnRescanClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		_orchestrator.ForceRescanAll();
	}

	private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		if (Application.Current is App app)
		{
			app.ShowMainWindow();
		}
	}

	private void OnExitClick(object sender, RoutedEventArgs e)
	{
		QuickPanel.IsOpen = false;
		if (Application.Current is App app)
		{
			app.ExitApplication();
		}
	}

	private void InitializeComponent()
	{
		Title = "SteamCN悬浮球";
		Width = Height = 44;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		Topmost = true;
		ShowInTaskbar = false;
		ShowActivated = false;
		ResizeMode = ResizeMode.NoResize;
		Focusable = false;
		FontFamily = new FontFamily("Inter, Segoe UI, Microsoft YaHei UI");

		var view = AppUi.Load<Grid>("FloatingBallWindow");
		Content = view;
		BallFace = AppUi.Require<Border>(view, nameof(BallFace));
		QuickPanel = AppUi.Require<Popup>(view, nameof(QuickPanel));
		BtnPauseResume = AppUi.Require<Button>(view, nameof(BtnPauseResume));
		BtnShowHide = AppUi.Require<Button>(view, nameof(BtnShowHide));
		Deactivated += OnDeactivated;
		BtnPauseResume.Click += OnPauseResumeClick;
		BtnShowHide.Click += OnShowHideClick;
		AppUi.Require<Button>(view, "BtnModeBilingual").Click += OnModeClick;
		AppUi.Require<Button>(view, "BtnModeChinese").Click += OnModeClick;
		AppUi.Require<Button>(view, "BtnModeOriginal").Click += OnModeClick;
		AppUi.Require<Button>(view, "BtnRescan").Click += OnRescanClick;
		AppUi.Require<Button>(view, "BtnSettings").Click += OnOpenSettingsClick;
		AppUi.Require<Button>(view, "BtnExit").Click += OnExitClick;
	}
}
