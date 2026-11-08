using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public class OverlayRenderer : IOverlayRenderer, IDisposable
{
	private readonly Dictionary<nint, OverlayWindow> _overlays = new Dictionary<nint, OverlayWindow>();

	private readonly object _lock = new object();

	private readonly Dispatcher _uiDispatcher;

	private DisplayMode _currentMode = DisplayMode.Bilingual;

	private LayoutMode _currentLayoutMode = LayoutMode.Adaptive;

	private bool _isVisible = true;

	private bool _disposed;

	private bool _clickThrough = true;

	public double FontSize { get; set; } = 14.0;

	public string FontFamilyName { get; set; } = "Microsoft YaHei UI";

	public double BackgroundOpacity { get; set; } = 0.75;

	public double OverlayOpacity { get; set; } = 0.82;

	public double FontScale { get; set; } = 1.0;

	public double MaxTranslationWidth { get; set; } = 420.0;

	public bool ShowOriginal { get; set; } = true;

	public double BilingualGap { get; set; } = 4.0;

	public DisplayMode CurrentMode => _currentMode;

	public LayoutMode CurrentLayoutMode => _currentLayoutMode;

	public bool IsVisible => _isVisible;

	public int DisplayedTranslationCount
	{
		get
		{
			lock (_lock)
			{
				return _overlays.Values.Sum((OverlayWindow o) => o.DisplayedCount);
			}
		}
	}

	public event EventHandler<DisplayMode>? ModeChanged;

	public OverlayRenderer()
	{
		_uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
	}

	private void RunOnUi(Action action)
	{
		if (!_disposed)
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
	}

	public void AttachToWindow(SteamWindowInfo window)
	{
		RunOnUi(() =>
		{
			lock (_lock)
			{
				if (!_disposed && !_overlays.ContainsKey(window.Handle))
				{
					OverlayWindow overlayWindow = new OverlayWindow
					{
						CurrentMode = _currentMode,
						CurrentLayoutMode = _currentLayoutMode,
						FontSize = FontSize,
						FontFamily = FontFamilyName,
						BackgroundOpacity = BackgroundOpacity,
						OverlayOpacity = OverlayOpacity,
						FontScale = FontScale,
						TextMaxWidth = MaxTranslationWidth,
						ShowOriginal = ShowOriginal,
						BilingualGap = BilingualGap
					};
					overlayWindow.AttachTo(window);
					overlayWindow.SetClickThrough(_clickThrough);
					if (_isVisible)
					{
						overlayWindow.Show();
					}
					else
					{
						overlayWindow.Hide();
					}
					_overlays[window.Handle] = overlayWindow;
					nint handle = overlayWindow.Handle;
					DiagLog($"AttachToWindow: Steam={window.Handle} OverlayHwnd={handle} IsVisible={_isVisible} OverlayIsVisible={overlayWindow.IsOverlayVisible} Bounds={window.Bounds}");
				}
			}
		});
	}

	private static void DiagLog(string message)
	{
		try
		{
			File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator", "diag.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
		}
		catch
		{
		}
	}

	public void DetachFromWindow(nint windowHandle)
	{
		RunOnUi(() =>
		{
			OverlayWindow value;
			lock (_lock)
			{
				if (!_overlays.Remove(windowHandle, out value))
				{
					return;
				}
			}
			try
			{
				value.ClearAll();
				value.Close();
			}
			catch
			{
			}
		});
	}

	public void UpdateTranslations(string sessionId, IReadOnlyList<TranslationResult> results, IReadOnlyList<TextFragment> sourceFragments)
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				try
				{
					item.UpdateTranslations(sessionId, results, sourceFragments);
				}
				catch (Exception)
				{
				}
			}
		});
	}

	public void HideTranslations(string sessionId)
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.HideTranslations(sessionId);
			}
		});
	}

	public void ShiftBlocks(string sessionId, double deltaY)
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.ShiftBlocks(sessionId, deltaY);
			}
		});
	}

	public void UpdateAnchorPositions(string sessionId, IReadOnlyList<(string AnchorId, ScreenRect NewBounds)> movedAnchors)
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.UpdateAnchorPositions(sessionId, movedAnchors);
			}
		});
	}

	public void RemoveBlocks(string sessionId, IReadOnlyList<string> expiredAnchorIds)
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.RemoveBlocks(sessionId, expiredAnchorIds);
			}
		});
	}

	public void HideAll()
	{
		_isVisible = false;
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.Hide();
			}
		});
	}

	public void ShowAll()
	{
		_isVisible = true;
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.Show();
			}
		});
	}

	public void SetMode(DisplayMode mode)
	{
		if (_currentMode == mode)
		{
			return;
		}
		_currentMode = mode;
		ModeChanged?.Invoke(this, mode);
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.CurrentMode = mode;
			}
		});
	}

	public void SetLayoutMode(LayoutMode mode)
	{
		if (_currentLayoutMode == mode)
		{
			return;
		}
		_currentLayoutMode = mode;
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.CurrentLayoutMode = mode;
			}
		});
	}

	public void ApplyOverlaySettings(OverlaySettings settings)
	{
		OverlayOpacity = Math.Clamp(settings.OverlayOpacity, 0.1, 1.0);
		FontScale = Math.Clamp(settings.FontScale, 0.5, 2.0);
		MaxTranslationWidth = Math.Clamp(settings.MaxTranslationWidth, 100, 800);
		ShowOriginal = settings.ShowOriginal;
		BilingualGap = Math.Clamp(settings.BilingualGap, 0, 20);
		BackgroundOpacity = Math.Max(0.3, OverlayOpacity - 0.1);
		_currentLayoutMode = settings.LayoutMode;
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.OverlayOpacity = OverlayOpacity;
				item.FontScale = FontScale;
				item.TextMaxWidth = MaxTranslationWidth;
				item.ShowOriginal = ShowOriginal;
				item.BilingualGap = BilingualGap;
				item.BackgroundOpacity = BackgroundOpacity;
				item.CurrentLayoutMode = _currentLayoutMode;
				item.RefreshLayoutConfig();
			}
		});
	}

	public void UpdateWindowPosition(SteamWindowInfo window)
	{
		RunOnUi(() =>
		{
			OverlayWindow value;
			lock (_lock)
			{
				_overlays.TryGetValue(window.Handle, out value);
			}
			value?.UpdatePosition(window);
		});
	}

	public void SetClickThrough(bool enabled)
	{
		_clickThrough = enabled;
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.SetClickThrough(enabled);
			}
		});
	}

	public void ClearAll()
	{
		RunOnUi(() =>
		{
			List<OverlayWindow> list;
			lock (_lock)
			{
				list = _overlays.Values.ToList();
			}
			foreach (OverlayWindow item in list)
			{
				item.ClearAll();
			}
		});
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		List<OverlayWindow> list;
		lock (_lock)
		{
			list = _overlays.Values.ToList();
			_overlays.Clear();
		}
		foreach (OverlayWindow overlay in list)
		{
			try
			{
				if (overlay.Dispatcher.CheckAccess())
				{
					overlay.Close();
					continue;
				}
				overlay.Dispatcher.Invoke(() =>
				{
					overlay.Close();
				}, DispatcherPriority.Send);
			}
			catch
			{
			}
		}
		GC.SuppressFinalize(this);
	}
}

