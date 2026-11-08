using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;
using SteamCN.Windows;

namespace SteamCN.App.Services;

public class TranslationOrchestrator : IDisposable
{
	private readonly ISteamWindowManager _windowManager;

	private readonly ITextAcquisition _textAcquisition;

	private readonly ITextRegionTracker _regionTracker;

	private readonly IOverlayRenderer _overlayRenderer;

	private readonly ISettingsManager _settingsManager;

	private readonly TranslationScheduler _scheduler;

	private readonly ICompatibilityManager _compatibilityManager;

	private readonly Dictionary<nint, DispatcherTimer> _debounceTimers = new Dictionary<nint, DispatcherTimer>();

	private readonly DispatcherTimer _pollTimer;

	private readonly Dispatcher _uiDispatcher;

	private readonly object _lock = new object();

	private readonly Dictionary<nint, int> _captureFailCounts = new Dictionary<nint, int>();

	private readonly Dictionary<nint, bool> _lastForegroundStates = new Dictionary<nint, bool>();

	private DateTime _lastScanTime = DateTime.MinValue;

	private DateTime _lastCaptureTime = DateTime.MinValue;

	private int _autoRecoveryCount;

	private bool _isRunning;
	private bool _showOnRestart = true;

	private bool _disposed;

	private int _pollTickCount;

	public bool IsRunning => _isRunning;

	public TranslationScheduler Scheduler => _scheduler;

	public DateTime LastScanTime => _lastScanTime;

	public DateTime LastCaptureTime => _lastCaptureTime;

	public DateTime NextCheckTime => _lastScanTime.AddMilliseconds(Math.Max(500, _settingsManager.Settings.Capture.ScrollDebounceMs * 2));

	public int AutoRecoveryCount => _autoRecoveryCount;

	public bool HasCaptureFailure
	{
		get
		{
			lock (_lock)
			{
				return _captureFailCounts.Values.Any((int c) => c > 0);
			}
		}
	}

	public string ScanStatus
	{
		get
		{
			if (!_isRunning)
			{
				return "已停止";
			}
			if (_scheduler.IsPaused)
			{
				return "已暂停";
			}
			if (HasCaptureFailure)
			{
				return "异常(重试中)";
			}
			return "运行中";
		}
	}

	public event Action<bool>? RunningStateChanged;

	public event Action<string>? StatusMessage;

	public event Action<string>? ErrorOccurred;

	private static void Log(string message)
	{
		try
		{
			File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator", "diag.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
		}
		catch
		{
		}
	}

	public TranslationOrchestrator(ISteamWindowManager windowManager, ITextAcquisition textAcquisition, ITextRegionTracker regionTracker, IOverlayRenderer overlayRenderer, ISettingsManager settingsManager, TranslationScheduler scheduler, ICompatibilityManager compatibilityManager)
	{
		_windowManager = windowManager;
		_textAcquisition = textAcquisition;
		_regionTracker = regionTracker;
		_overlayRenderer = overlayRenderer;
		_settingsManager = settingsManager;
		_scheduler = scheduler;
		_compatibilityManager = compatibilityManager;
		_uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
		_pollTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(Math.Max(500, _settingsManager.Settings.Capture.ScrollDebounceMs * 2))
		};
		_pollTimer.Tick += OnPollTick;
		_windowManager.WindowBound += OnWindowBound;
		_windowManager.WindowUnbound += OnWindowUnbound;
		_windowManager.WindowChanged += OnWindowChanged;
		if (_windowManager is SteamWindowManager steam) steam.VerifiedTextChanged += OnVerifiedTextChanged;
		_windowManager.PageNavigated += OnPageNavigated;
		_scheduler.PipelineError += OnPipelineError;
		_scheduler.PipelineDiagnostics += (object? _, PipelineDiagnostics diag) =>
		{
			_lastScanTime = DateTime.Now;
			nint? num = null;
			foreach (SteamWindowInfo boundWindow in _windowManager.BoundWindows)
			{
				if (boundWindow.CurrentSessionId == diag.SessionId)
				{
					num = boundWindow.Handle;
					break;
				}
			}
			if (diag.CaptureSuccess || diag.FragmentCount > 0)
			{
				_lastCaptureTime = DateTime.Now;
				bool flag;
				lock (_lock)
				{
					flag = _captureFailCounts.Values.Any((int c) => c > 0);
					_captureFailCounts.Clear();
				}
				if (flag)
				{
					_autoRecoveryCount++;
					Log($"捕获恢复: 自动恢复第{_autoRecoveryCount}次 session={diag.SessionId.Substring(0, 8)}");
				}
			}
			else if (num.HasValue)
			{
				lock (_lock)
				{
					_captureFailCounts.TryGetValue(num.Value, out var value);
					_captureFailCounts[num.Value] = value + 1;
					if (value == 0)
					{
						Log("捕获失败: session=" + diag.SessionId.Substring(0, 8) + " 开始递增重试");
					}
				}
			}
			if (diag.FirstOnlineResultMs is long firstOnline)
				Log($"[Timing] 首条在线译文={firstOnline}ms 整批网络={diag.TranslateMs}ms 待翻译={diag.UncachedCount}");
			if (diag.RegionAdded > 0 || diag.RegionMoved > 0 || diag.RegionRemoved > 0)
			{
				Log($"[Region] +{diag.RegionAdded}/~{diag.RegionMoved}/-{diag.RegionRemoved} {diag.RegionTypes}");
			}
		};
	}

	public async Task StartAsync()
	{
		if (_isRunning)
		{
			return;
		}
		_isRunning = true;
		_scheduler.Resume();
		if (_showOnRestart) _overlayRenderer.ShowAll();
		_windowManager.StartMonitoring();
		HealthCheckResult healthCheckResult = await _compatibilityManager.RunHealthCheckAsync();
		if (healthCheckResult.Status == CompatibilityStatus.Incompatible)
		{
			ErrorOccurred?.Invoke("兼容性检查失败: " + string.Join("; ", healthCheckResult.Errors));
		}
		else if (healthCheckResult.Warnings.Count > 0)
		{
			StatusMessage?.Invoke("警告: " + string.Join("; ", healthCheckResult.Warnings));
		}
		if (_settingsManager.Settings.AutoDetectSteam)
		{
			IReadOnlyList<SteamWindowInfo> readOnlyList = await _windowManager.ScanAndBindAsync();
			Log($"扫描完成: 找到 {readOnlyList.Count} 个 Steam 窗口");
			foreach (SteamWindowInfo item in readOnlyList)
			{
				Log($"  窗口: Handle={item.Handle} Title='{item.Title}' Vis={item.IsVisible} Min={item.IsMinimized}");
			}
			StatusMessage?.Invoke((readOnlyList.Count > 0) ? $"已绑定 {readOnlyList.Count} 个 Steam 窗口" : "未检测到 Steam 窗口，等待启动...");
		}
		else
		{
			Log("AutoDetectSteam=false，跳过自动扫描");
		}
		_pollTimer.Start();
		RunningStateChanged?.Invoke(obj: true);
		StatusMessage?.Invoke("翻译服务已启动");
	}

	public void Stop()
	{
		if (!_isRunning)
		{
			return;
		}
		_isRunning = false;
		_pollTimer.Stop();
		_windowManager.StopMonitoring();
		foreach (var window in _windowManager.BoundWindows) _scheduler.CancelWindow(window.Handle);
		lock (_lock)
		{
			foreach (DispatcherTimer value in _debounceTimers.Values)
			{
				value.Stop();
			}
			_debounceTimers.Clear();
		}
		_showOnRestart = _overlayRenderer.IsVisible;
		_overlayRenderer.HideAll();
		RunningStateChanged?.Invoke(obj: false);
		StatusMessage?.Invoke("翻译服务已停止");
	}

	public bool TogglePause()
	{
		if (_scheduler.IsPaused)
		{
			_scheduler.Resume();
			StatusMessage?.Invoke("翻译已恢复");
			return false;
		}
		_scheduler.Pause();
		StatusMessage?.Invoke("翻译已暂停");
		return true;
	}

	public void RefreshDisplayMode()
	{
		_overlayRenderer.SetMode(_settingsManager.Settings.DisplayMode);
		StatusMessage?.Invoke($"显示模式: {_settingsManager.Settings.DisplayMode}");
	}

	public void ForceRescanAll()
	{
		RunOnUi(() =>
		{
			int num = 0;
			foreach (SteamWindowInfo boundWindow in _windowManager.BoundWindows)
			{
				_scheduler.ProcessWindow(boundWindow);
				num++;
			}
			Log($"ForceRescanAll: 触发 {num} 个窗口重新扫描");
			StatusMessage?.Invoke((num > 0) ? $"已触发 {num} 个窗口重新扫描" : "无绑定窗口");
		});
	}

	public void CycleDisplayMode()
	{
		DisplayMode displayMode = _overlayRenderer.CurrentMode switch
		{
			DisplayMode.Original => DisplayMode.Bilingual, 
			DisplayMode.Bilingual => DisplayMode.Chinese, 
			DisplayMode.Chinese => DisplayMode.Original, 
			_ => DisplayMode.Bilingual, 
		};
		_overlayRenderer.SetMode(displayMode);
		StatusMessage?.Invoke($"显示模式: {displayMode}");
	}

	public void ToggleOverlayVisibility()
	{
		if (_overlayRenderer.IsVisible)
		{
			_overlayRenderer.HideAll();
			StatusMessage?.Invoke("覆盖层已隐藏");
		}
		else
		{
			_overlayRenderer.ShowAll();
			StatusMessage?.Invoke("覆盖层已显示");
		}
	}

	private void RunOnUi(Action action)
	{
		if (_uiDispatcher.CheckAccess())
		{
			action();
		}
		else
		{
			_uiDispatcher.BeginInvoke(action);
		}
	}

	private void OnWindowBound(object? sender, SteamWindowInfo window)
	{
		Log($"OnWindowBound: Handle={window.Handle} Title='{window.Title}' UI线程={_uiDispatcher.CheckAccess()}");
		RunOnUi(() =>
		{
			try
			{
				_overlayRenderer.AttachToWindow(window);
				Log($"AttachToWindow 完成: Handle={window.Handle}");
				StatusMessage?.Invoke("已绑定 Steam 窗口: " + window.Title);
				ScheduleTranslation(window);
				Log($"ScheduleTranslation 完成: Handle={window.Handle}");
			}
			catch (Exception ex)
			{
				Log($"OnWindowBound 异常: {ex}");
				ErrorOccurred?.Invoke("窗口绑定处理失败: " + ex.Message);
			}
		});
	}

	private void OnWindowUnbound(object? sender, SteamWindowInfo window)
	{
		Log($"OnWindowUnbound: Handle={window.Handle} Title='{window.Title}'");
		RunOnUi(() =>
		{
			try
			{
				_scheduler.CancelWindow(window.Handle);
				_overlayRenderer.DetachFromWindow(window.Handle);
				_regionTracker.ClearSession(window.CurrentSessionId);
				_scheduler.AnchorManager.ClearSession(window.CurrentSessionId);
				lock (_lock)
				{
					if (_debounceTimers.TryGetValue(window.Handle, out DispatcherTimer value))
					{
						value.Stop();
						_debounceTimers.Remove(window.Handle);
					}
				}
				StatusMessage?.Invoke("已解绑窗口: " + window.Title);
			}
			catch (Exception ex)
			{
				ErrorOccurred?.Invoke("窗口解绑处理失败: " + ex.Message);
			}
		});
	}

	private void OnWindowChanged(object? sender, SteamWindowInfo window)
	{
		RunOnUi(() =>
		{
			try
			{
				if (!window.IsForeground) _scheduler.CancelWindow(window.Handle);
				_overlayRenderer.UpdateWindowPosition(window);
				if (!(_lastForegroundStates.TryGetValue(window.Handle, out var value) & value) && window.IsForeground)
				{
					Log($"焦点恢复: Handle={window.Handle} 强制重新翻译");
					ForceRescanWindow(window);
				}
				_lastForegroundStates[window.Handle] = window.IsForeground;
				ScheduleTranslation(window);
			}
			catch (Exception ex)
			{
				ErrorOccurred?.Invoke("窗口状态更新失败: " + ex.Message);
			}
		});
	}

	private void OnPageNavigated(object? sender, (SteamWindowInfo Window, string OldTitle) e)
	{
		var (window, _) = e;
		Log($"页面跳转: '{e.OldTitle}' → '{window.Title}' Handle={window.Handle} 新Session={window.CurrentSessionId.Substring(0, 8)}");
		RunOnUi(() =>
		{
			try
			{
				_textAcquisition.ResetScrollDetector();
				_scheduler.AnchorManager.ClearAllExcept(window.CurrentSessionId);
				_overlayRenderer.HideTranslations(window.CurrentSessionId);
				ForceRescanWindow(window);
				StatusMessage?.Invoke("页面切换: " + window.Title);
			}
			catch (Exception value)
			{
				Log($"OnPageNavigated 异常: {value}");
			}
		});
	}

	public void ForceRescanWindow(SteamWindowInfo window)
	{
		if (_isRunning && !_scheduler.IsPaused && window.IsVisible && !window.IsMinimized)
		{
			lock (_lock)
			{
				_captureFailCounts.Remove(window.Handle);
			}
			ScheduleTranslation(window);
		}
	}

	private void OnVerifiedTextChanged(SteamWindowInfo window, IReadOnlyList<TextFragment> fragments)
	{
		RunOnUi(() =>
		{
			if (_isRunning && !_scheduler.IsPaused && SteamWindowManager.IsSteamForeground(window))
				_scheduler.ProcessVerifiedText(window, fragments);
		});
	}

	private void OnPollTick(object? sender, EventArgs e)
	{
		if (!_isRunning || _scheduler.IsPaused)
		{
			return;
		}
		foreach (SteamWindowInfo boundWindow in _windowManager.BoundWindows)
		{
			if (!boundWindow.IsVisible || boundWindow.IsMinimized)
			{
				continue;
			}
			if (_settingsManager.Settings.Overlay.HideWhenUnfocused && !boundWindow.IsForeground)
			{
				_overlayRenderer.HideTranslations(boundWindow.CurrentSessionId);
				continue;
			}
			lock (_lock)
			{
				if (_captureFailCounts.TryGetValue(boundWindow.Handle, out var value) && value > 0)
				{
					int num = Math.Min(5, value - 1);
					if (_pollTickCount % (num + 1) != 0)
					{
						continue;
					}
				}
			}
			ScheduleTranslation(boundWindow);
		}
		if (_pollTickCount % 60 == 0)
		{
			int num2 = _scheduler.AnchorManager.PruneStaleSessions();
			if (num2 > 0)
			{
				Log($"锚点会话清理: 移除 {num2} 个过期会话");
			}
		}
		_pollTickCount++;
	}

	private void ScheduleTranslation(SteamWindowInfo window)
	{
		if (!_isRunning || _scheduler.IsPaused || !window.IsVisible || window.IsMinimized)
		{
			return;
		}
		int scrollDebounceMs = _settingsManager.Settings.Capture.ScrollDebounceMs;
		DispatcherTimer timer;
		lock (_lock)
		{
			if (_debounceTimers.TryGetValue(window.Handle, out DispatcherTimer value))
			{
				timer = value;
				timer.Stop();
			}
			else
			{
				timer = new DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(scrollDebounceMs)
				};
				timer.Tick += (object? s, EventArgs e) =>
				{
					timer.Stop();
					_scheduler.ProcessWindow(window);
				};
				_debounceTimers[window.Handle] = timer;
			}
		}
		timer.Start();
	}

	private void OnPipelineError(object? sender, Exception ex)
	{
		ErrorOccurred?.Invoke("管线错误: " + ex.Message);
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			Stop();
			_windowManager.WindowBound -= OnWindowBound;
			_windowManager.WindowUnbound -= OnWindowUnbound;
			_windowManager.WindowChanged -= OnWindowChanged;
			if (_windowManager is SteamWindowManager steam) steam.VerifiedTextChanged -= OnVerifiedTextChanged;
			_windowManager.PageNavigated -= OnPageNavigated;
			_scheduler.PipelineError -= OnPipelineError;
			_scheduler.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}


