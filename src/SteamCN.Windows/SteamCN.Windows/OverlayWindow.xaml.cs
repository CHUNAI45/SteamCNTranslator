using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public sealed class OverlayWindow
{
    private sealed record Card(TranslationResult Translation, Border Visual, TextBlock Text);
    private readonly Canvas _canvas = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TranslationResult> _recentTranslations = new(StringComparer.Ordinal);
    private string? _recentPage;
    private readonly List<nint> _hooks = new();
    private readonly WinEventProc _onEvent;
    private readonly DispatcherTimer _guard;
    private SteamWindowInfo? _owner;
    private string? _session;
    private nint _hwnd;
    private HwndSource? _surface;
    private nint _webPane;
    private bool _requested;
    private bool _moving;
    private SteamPage? _lastPage;
    private ScreenRect _lastViewport;
    private ScreenRect _targetViewport;
    private double _lastDpi;
    private bool _layoutDirty = true;
    private DisplayMode _mode = DisplayMode.Bilingual;
    private LayoutMode _layout = LayoutMode.Adaptive;

    public nint TargetWindowHandle => _owner?.Handle ?? 0;
    public nint Handle => _hwnd;
    public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
    public bool IsVisible => _hwnd != 0 && NativeMethods.IsWindowVisible(_hwnd);
    public bool IsOverlayVisible => IsVisible;
    public int DisplayedCount { get; private set; }
    public DisplayMode CurrentMode
    {
        get => _mode;
        set { _mode = value; _layoutDirty = true; }
    }
    public LayoutMode CurrentLayoutMode
    {
        get => _layout;
        set { _layout = value; _layoutDirty = true; }
    }
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double BackgroundOpacity { get; set; } = 0.75;
    public double OverlayOpacity { get; set; } = 0.82;
    public double FontScale { get; set; } = 1;
    public double TextMaxWidth { get; set; } = 420;
    public bool ShowOriginal { get; set; } = true;
    public double BilingualGap { get; set; } = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private nint GuardWindowMessages(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021)
        {
            handled = true;
            return 3;
        }
        if (message == 0x0046 && lParam != 0)
        {
            var position = Marshal.PtrToStructure<WindowPos>(lParam);
            position.Flags |= 0x0010;
            if (!_targetViewport.IsEmpty && (position.Flags & 0x0080) == 0)
            {
                if ((position.Flags & 0x0002) == 0)
                {
                    position.X = (int)_targetViewport.X;
                    position.Y = (int)_targetViewport.Y;
                }
                if ((position.Flags & 0x0001) == 0)
                {
                    position.Width = (int)_targetViewport.Width;
                    position.Height = (int)_targetViewport.Height;
                }
            }
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    private delegate void WinEventProc(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    public OverlayWindow()
    {
        _onEvent = OnWinEvent;
        _guard = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _guard.Tick += (_, _) => Refresh();
    }

    public void AttachTo(SteamWindowInfo owner)
    {
        _owner = owner;
        foreach (var range in new (uint Start, uint End)[] { (3, 3), (10, 11), (18, 19), (22, 23), (0x8001, 0x8003), (0x800B, 0x800B) })
        {
            nint hook = SetWinEventHook(range.Start, range.End, 0, _onEvent, range.Start == 3 ? 0 : (uint)owner.ProcessId, 0, 2);
            if (hook != 0) _hooks.Add(hook);
        }
        _guard.Start();
    }

    private void OnWinEvent(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_owner == null) return;
        if (type == 3 && window != _owner.Handle)
        {
            SteamPageReader.Invalidate(_owner);
            ClearVisuals();
            return;
        }
        if (window != _owner.Handle && window != _webPane) return;
        if (type == 10) _moving = true;
        if (type == 11) _moving = false;
        if (type is 10 or 11 or 18 or 19 or 22 or 23 ||
            (objectId == 0 && childId == 0 && type is 0x8001 or 0x8003))
        {
            SteamPageReader.Invalidate(_owner);
            ClearVisuals();
        }
        else if (type == 0x800B && objectId == 0 && childId == 0)
        {
            Refresh();
        }
    }

    public void UpdatePosition(SteamWindowInfo window) => Refresh();
    public void RefreshLayoutConfig() => _layoutDirty = true;
    public void SetClickThrough(bool enabled) { }

    public void UpdateTranslations(string sessionId, IReadOnlyList<TranslationResult> results, IReadOnlyList<TextFragment> sourceFragments)
    {
        if (_owner == null || _owner.CurrentSessionId != sessionId) return;
        SteamPage? page = SteamPageReader.Current(_owner);
        if (page == null) { HideSurface(); return; }
        SelectPage(page);
        var live = page.Paragraphs.ToDictionary(p => p.ContentHash);
        foreach (var result in results)
        {
            if (!result.Success || result.SessionId != sessionId || string.IsNullOrWhiteSpace(result.TranslatedText) ||
                !live.TryGetValue(result.SourceHash, out var paragraph) ||
                result.NormalizedSource != paragraph.NormalizedText)
                continue;

            if (_recentTranslations.Count >= 250 && !_recentTranslations.ContainsKey(result.SourceHash))
                _recentTranslations.Remove(_recentTranslations.Keys.First());
            _recentTranslations[result.SourceHash] = result;
        }
        Refresh();
    }

    private void SelectPage(SteamPage page)
    {
        ResetSession(page.Session);
        if (_recentPage == page.Identity) return;
        _recentTranslations.Clear();
        _recentPage = page.Identity;
    }

    private void RestoreCards(SteamPage page)
    {
        foreach (var paragraph in page.Paragraphs)
        {
            if (!_recentTranslations.TryGetValue(paragraph.ContentHash, out var result) ||
                result.NormalizedSource != paragraph.NormalizedText ||
                (_cards.TryGetValue(result.SourceHash, out var current) &&
                    current.Translation.TranslatedText == result.TranslatedText))
                continue;

            if (_cards.TryGetValue(result.SourceHash, out Card? old)) _canvas.Children.Remove(old.Visual);
            var text = new TextBlock
            {
                Text = result.TranslatedText,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkSlateGray
            };
            var visual = new Border { Child = text, Padding = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(2) };
            _cards[result.SourceHash] = new Card(result, visual, text);
            _layoutDirty = true;
        }
    }

    public void ShiftBlocks(string sessionId, double deltaY) => HideTranslations(sessionId);
    public void UpdateAnchorPositions(string sessionId, IReadOnlyList<(string AnchorId, ScreenRect NewBounds)> movedAnchors)
    {
        if (_session == sessionId) Refresh();
    }

    public void RemoveBlocks(string sessionId, IReadOnlyList<string> expiredAnchorIds)
    {
        if (_session == sessionId) Refresh();
    }

    public void HideTranslations(string sessionId)
    {
        if (_session == sessionId) ClearAll();
    }

    public void ClearAll()
    {
        _recentTranslations.Clear();
        _recentPage = null;
        ClearVisuals();
    }

    private void ClearVisuals()
    {
        _cards.Clear();
        _canvas.Children.Clear();
        _session = null;
        _lastPage = null;
        _layoutDirty = true;
        HideSurface();
    }

    public void Hide()
    {
        _requested = false;
        HideSurface();
    }

    public void Show()
    {
        _requested = true;
        Refresh();
    }

    private void ResetSession(string session)
    {
        if (_session == session) return;
        ClearVisuals();
        _session = session;
    }

    private void HideSurface()
    {
        DisplayedCount = 0;
        if (IsVisible) NativeMethods.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x0097);
    }

    private void Refresh()
    {
        if (_owner == null) return;
        if (_session != null && _session != _owner.CurrentSessionId) ClearVisuals();
        if (!_requested || _moving || _mode == DisplayMode.Original)
        {
            HideSurface();
            return;
        }
        if (!SteamWebClient.IsLive(_owner))
        {
            ClearAll();
            return;
        }
        if (!SteamWebClient.IsForeground(_owner))
        {
            SteamPageReader.Invalidate(_owner);
            ClearVisuals();
            return;
        }
        var page = SteamPageReader.Current(_owner);
        if (page == null)
        {
            HideSurface();
            return;
        }

        SelectPage(page);
        RestoreCards(page);
        if (_cards.Count == 0) { HideSurface(); return; }
        _webPane = page.Pane;
        if (_hwnd != 0 && _targetViewport != page.Viewport)
            ReleaseSurface();
        _targetViewport = page.Viewport;
        if (_hwnd == 0)
        {
            var rect = page.Viewport;
            var parameters = new HwndSourceParameters("SteamCN translation")
            {
                ParentWindow = _owner.Handle,
                WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x080000A0,
                PositionX = (int)rect.X,
                PositionY = (int)rect.Y,
                Width = (int)rect.Width,
                Height = (int)rect.Height,
                UsesPerPixelOpacity = true
            };
            _surface = new HwndSource(parameters);
            _hwnd = _surface.Handle;
            _surface.AddHook(GuardWindowMessages);
            _surface.CompositionTarget.BackgroundColor = Colors.Transparent;
            _surface.RootVisual = _canvas;
        }
        if (GetWindow(_hwnd, 4) != _owner.Handle) { ClearAll(); return; }

        var viewport = page.Viewport;
        if (!Position(viewport)) { HideSurface(); return; }
        double dpi = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (dpi <= 0) { HideSurface(); return; }
        if (_layoutDirty || _lastDpi != dpi || _lastViewport != viewport ||
            !SameParagraphs(_lastPage, page))
        {
            Place(page, dpi);
            _lastDpi = dpi;
            _lastViewport = viewport;
            _layoutDirty = false;
        }
        _lastPage = page;
        if (_canvas.Children.Count == 0 || !StillCurrent(page))
        {
            HideSurface();
            return;
        }
        if (!IsVisible && !NativeMethods.SetWindowPos(_hwnd, 0, (int)viewport.X, (int)viewport.Y,
            (int)viewport.Width, (int)viewport.Height, 0x0054))
        {
            HideSurface();
            return;
        }
        if (!Position(viewport) || !StillCurrent(page))
        {
            HideSurface();
            return;
        }
        DisplayedCount = _canvas.Children.Count;
    }

    private bool StillCurrent(SteamPage page)
    {
        var latest = SteamPageReader.Current(page.Owner);
        return latest != null && latest.Session == page.Session &&
            latest.Identity == page.Identity && latest.Viewport == page.Viewport &&
            SameParagraphs(page, latest);
    }

    private bool Position(ScreenRect rect)
    {
        if (SteamWebClient.TryClientBounds(_hwnd, out var current) && current == rect) return true;
        const uint noActivateNoZOrder = 0x0014;
        return NativeMethods.SetWindowPos(_hwnd, 0, (int)rect.X, (int)rect.Y,
            (int)rect.Width, (int)rect.Height, noActivateNoZOrder) &&
            SteamWebClient.TryClientBounds(_hwnd, out current) && current == rect;
    }

    private static bool SameParagraphs(SteamPage? old, SteamPage page) => old != null &&
        old.Paragraphs.Select(p => (p.ContentHash, p.Bounds)).SequenceEqual(
            page.Paragraphs.Select(p => (p.ContentHash, p.Bounds)));

    private void Place(SteamPage page, double dpi)
    {
        _canvas.Children.Clear();
        var sources = page.Paragraphs.ToDictionary(p => p.ContentHash,
            p => new Rect((p.Bounds.X - page.Viewport.X) / dpi, (p.Bounds.Y - page.Viewport.Y) / dpi,
                p.Bounds.Width / dpi, p.Bounds.Height / dpi));
        foreach (string gone in _cards.Keys.Where(hash => !sources.ContainsKey(hash)).ToArray())
            _cards.Remove(gone);

        var occupied = new List<Rect>();
        var area = new Rect(0, 0, page.Viewport.Width / dpi, page.Viewport.Height / dpi);
        double gap = Math.Max(3, BilingualGap);
        foreach (var paragraph in page.Paragraphs)
        {
            if (!_cards.TryGetValue(paragraph.ContentHash, out Card? card)) continue;
            Rect source = sources[paragraph.ContentHash];
            bool placed = false;
            if (_mode != DisplayMode.Chinese && _layout != LayoutMode.Overlay && ShowOriginal)
                placed = TryPlace(card, source, area, gap, false, sources.Values, occupied);
            if (!placed && (_layout != LayoutMode.SideBySide || _mode == DisplayMode.Chinese || !ShowOriginal))
                TryPlace(card, source, area, gap, true, sources.Values, occupied);
        }
    }

    private bool TryPlace(Card card, Rect source, Rect area, double gap, bool replace,
        IEnumerable<Rect> sources, List<Rect> occupied)
    {
        double width = replace ? source.Width : Math.Min(TextMaxWidth, Math.Max(source.Width, 120));
        double top = replace ? source.Top : source.Bottom + gap;
        if (width <= 6 || source.Left + width > area.Right || top >= area.Bottom) return false;
        card.Text.FontFamily = new FontFamily(FontFamily);
        card.Text.FontSize = Math.Max(9, FontSize * FontScale);
        card.Visual.Width = width;
        card.Visual.Height = double.NaN;
        card.Visual.Measure(new Size(width, double.PositiveInfinity));
        if (replace && card.Visual.DesiredSize.Height > source.Height)
        {
            card.Text.FontSize = Math.Min(card.Text.FontSize, Math.Max(10, source.Height * 0.74));
            card.Visual.Measure(new Size(width, double.PositiveInfinity));
        }
        if (replace && card.Visual.DesiredSize.Height > source.Height + 12)
        {
            width = Math.Min(area.Right - source.Left, source.Width + Math.Min(48, source.Width * 0.4));
            card.Visual.Width = width;
            card.Visual.Measure(new Size(width, double.PositiveInfinity));
        }
        double height = card.Visual.DesiredSize.Height;
        if (replace)
        {
            // OCR reports glyph bounds, which are shorter than a readable WPF line box.
            if (height > source.Height + 12 || height > area.Height) return false;
            height = Math.Max(height, source.Height);
            top = Math.Clamp(source.Top - (height - source.Height) / 2, 0, area.Bottom - height);
        }
        card.Text.VerticalAlignment = replace ? VerticalAlignment.Center : VerticalAlignment.Top;
        var rect = new Rect(source.Left, top, width, height);
        var clearance = rect;
        clearance.Inflate(0, gap / 2);
        if (!area.Contains(rect) || occupied.Any(other => other.IntersectsWith(clearance)) ||
            sources.Any(other => (!replace || other != source) && other.IntersectsWith(rect)))
            return false;

        card.Visual.Background = new SolidColorBrush(Color.FromArgb(
            replace ? (byte)255 : (byte)Math.Clamp(220 * BackgroundOpacity + 35, 140, 255), 233, 242, 246));
        card.Visual.Height = rect.Height;
        Canvas.SetLeft(card.Visual, rect.Left);
        Canvas.SetTop(card.Visual, rect.Top);
        _canvas.Children.Add(card.Visual);
        occupied.Add(rect);
        return true;
    }

    public void Close()
    {
        _guard.Stop();
        foreach (nint hook in _hooks) UnhookWinEvent(hook);
        ClearAll();
        ReleaseSurface();
    }

    private void ReleaseSurface()
    {
        if (_surface == null) return;
        HideSurface();
        _surface.RootVisual = null;
        _surface.Dispose();
        _surface = null;
        _hwnd = 0;
    }
}








