using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public sealed class SteamWindowManager : ISteamWindowManager, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, SteamWindowInfo> _windows = new();
    private Timer? _timer;
    private bool _disposed;
    private int _refreshing;
    private nint _primary;
    private long _nextScan;
    private SteamPage? _lastNotifiedPage;
    private readonly OcrSteamPageReader? _ocrReader;
    private readonly ISettingsManager? _settings;

    public SteamWindowManager() { }
    public SteamWindowManager(IOcrEngine ocrEngine) => _ocrReader = new OcrSteamPageReader(ocrEngine);
    public SteamWindowManager(IOcrEngine ocrEngine, ISettingsManager settings) : this(ocrEngine) =>
        _settings = settings;

    public IReadOnlyList<SteamWindowInfo> BoundWindows
    {
        get { lock (_gate) return _windows.Values.ToArray(); }
    }

    public SteamWindowInfo? PrimaryWindow
    {
        get { lock (_gate) return _windows.GetValueOrDefault(_primary) ?? _windows.Values.FirstOrDefault(); }
    }

    public event EventHandler<SteamWindowInfo>? WindowBound;
    public event EventHandler<SteamWindowInfo>? WindowUnbound;
    public event EventHandler<SteamWindowInfo>? WindowChanged;
    public event Action<SteamWindowInfo, IReadOnlyList<TextFragment>>? VerifiedTextChanged;
    public event EventHandler<(SteamWindowInfo Window, string OldTitle)>? PageNavigated;

    public void StartMonitoring()
    {
        lock (_gate)
        {
            if (!_disposed)
                _timer ??= new Timer(_ => RefreshWindowStates(), null, 0, 180);
        }
    }

    public void StopMonitoring()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public Task<IReadOnlyList<SteamWindowInfo>> ScanAndBindAsync() => Task.Run(Scan);

    private IReadOnlyList<SteamWindowInfo> Scan()
    {
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (NativeMethods.IsWindowVisible(handle)) BindWindow(handle);
            return true;
        }, 0);
        return BoundWindows;
    }

    public bool BindWindow(nint handle)
    {
        if (!SteamWebClient.IsMainWindow(handle, out int processId, out string path))
            return false;
        SteamWindowInfo window;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_windows.ContainsKey(handle)) return true;
            if (_windows.Count != 0) return false;

            window = new SteamWindowInfo
            {
                Handle = handle,
                ProcessId = processId,
                ProcessName = System.IO.Path.GetFileNameWithoutExtension(path),
                ProcessPath = path,
                Title = "Steam"
            };
            Update(window);
            _windows.Add(handle, window);
        }
        WindowBound?.Invoke(this, window);
        return true;
    }

    public void UnbindWindow(nint handle)
    {
        SteamWindowInfo? window;
        lock (_gate)
        {
            if (!_windows.Remove(handle, out window)) return;
            SteamPageReader.Invalidate(window);
        }
        WindowUnbound?.Invoke(this, window);
    }

    public void SetPrimaryWindow(nint handle)
    {
        lock (_gate)
            if (_windows.ContainsKey(handle)) _primary = handle;
    }

    public void RefreshWindowStates()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var windows = BoundWindows;
            if (windows.Count == 0 && Environment.TickCount64 >= _nextScan)
            {
                _nextScan = Environment.TickCount64 + 1500;
                windows = Scan();
            }
            foreach (var window in windows)
            {
                if (!SteamWebClient.IsLive(window))
                {
                    UnbindWindow(window.Handle);
                    continue;
                }
                var old = (window.Bounds, window.DpiScale, window.IsVisible, window.IsMinimized, window.IsForeground);
                Update(window);
                if (old != (window.Bounds, window.DpiScale, window.IsVisible, window.IsMinimized, window.IsForeground))
                {
                    SteamPageReader.Invalidate(window);
                    WindowChanged?.Invoke(this, window);
                }

                if (!window.IsForeground) continue;
                if (_settings?.Settings.AutoTranslate == false)
                {
                    SteamPageReader.Invalidate(window);
                    continue;
                }
                var page = SteamPageReader.Read(window) ?? _ocrReader?.Read(window);
                bool navigated;
                lock (_gate)
                {
                    if (_disposed || _windows.GetValueOrDefault(window.Handle) != window) continue;
                    navigated = SteamPageReader.Publish(window, page);
                }
                if (navigated) PageNavigated?.Invoke(this, (window, window.Title));
                if (page != null && page.Session == window.CurrentSessionId && page.Paragraphs.Count > 0 &&
                    (_lastNotifiedPage?.Owner != window || _lastNotifiedPage.Session != page.Session ||
                    !_lastNotifiedPage.Paragraphs.Select(f => f.ContentHash).SequenceEqual(page.Paragraphs.Select(f => f.ContentHash))))
                {
                    _lastNotifiedPage = page;
                    VerifiedTextChanged?.Invoke(window, page.Paragraphs);
                }
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    private static void Update(SteamWindowInfo window)
    {
        window.IsVisible = NativeMethods.IsWindowVisible(window.Handle);
        window.IsMinimized = NativeMethods.IsIconic(window.Handle);
        window.IsForeground = SteamWebClient.IsForeground(window);
        window.Bounds = SteamWebClient.TryClientBounds(window.Handle, out var bounds) ? bounds : default;
        window.DpiScale = NativeMethods.GetDpiForWindow(window.Handle) / 96.0;
    }

    public static bool IsSteamForeground(SteamWindowInfo window) => SteamWebClient.IsForeground(window);

    public static bool TryActivateForRescan(SteamWindowInfo window, nint requester)
    {
        if (SteamWebClient.IsForeground(window)) return true;
        if (requester == 0 || NativeMethods.GetForegroundWindow() != requester ||
            !SteamWebClient.IsMainWindow(window.Handle, out int processId, out _) || processId != window.ProcessId)
            return false;
        if (NativeMethods.IsIconic(window.Handle) && !ShowWindowAsync(window.Handle, 9))
            return false;
        return SetForegroundWindow(window.Handle) || SteamWebClient.IsForeground(window);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);

    public bool IsWindowValid(nint handle)
    {
        lock (_gate)
            return _windows.TryGetValue(handle, out var window) && SteamWebClient.IsLive(window);
    }

    public void Dispose()
    {
        nint[] handles;
        lock (_gate)
        {
            _disposed = true;
            StopMonitoring();
            handles = _windows.Keys.ToArray();
        }
        foreach (var handle in handles) UnbindWindow(handle);
    }
}


