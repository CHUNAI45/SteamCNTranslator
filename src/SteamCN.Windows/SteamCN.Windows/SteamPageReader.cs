using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Windows;

internal sealed record SteamPage(SteamWindowInfo Owner, string Session, nint Pane, ScreenRect Viewport,
    string Identity, string Url, long ReadAt, IReadOnlyList<TextFragment> Paragraphs);

internal static class SteamPageReader
{
    private const int MaxParagraphs = 100;
    private const long LifetimeMs = 650;
    private static readonly object Gate = new();
    private static SteamPage? _current;
    private static readonly Regex Noise = new(
        @"^(?:[\p{N}\p{P}\p{S}\s]+|(?:ver(?:sion)?\.?\s*|[vb])\d+(?:[.\-_:]\d+)*(?:[-+][\w.]+)?|\d[\d,.]*\s*(?:views?|items?|comments?|votes?|results?|hours?|hrs?|likes?)|home|browse|back|forward|store|library|community|workshop|discussions|guides|reviews|login|sign in|search|subscribe|unsubscribe|share|report|view all|add to cart|add to wishlist|privacy policy|terms of service|steam|商店|库|社区|创意工坊|讨论|搜索|登录|订阅)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static SteamPage? Read(SteamWindowInfo window)
    {
        string session = window.CurrentSessionId;
        var timer = Stopwatch.StartNew();
        try
        {
            if (!SteamWebClient.TryWebPane(window, out nint pane, out var viewport)) return null;
            var root = AutomationElement.FromHandle(pane);
            var docs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

            AutomationElement? document = null;
            string? url = null;
            ScreenRect content = default;
            foreach (AutomationElement candidate in docs)
            {
                if (candidate.Current.IsOffscreen || !TrySteamUrl(candidate, out string address)) continue;
                var raw = candidate.Current.BoundingRectangle;
                var rect = viewport.Intersect(new ScreenRect(raw.X, raw.Y, raw.Width, raw.Height));
                if (rect.IsEmpty || rect.Width < viewport.Width * 0.8 || rect.Height < viewport.Height * 0.8)
                    continue;
                if (document != null) return null;
                document = candidate;
                url = address;
                content = rect;
            }
            if (document == null || url == null || SteamPagePaths.IsWorkshopSubscriptions(new Uri(url)) ||
                !document.TryGetCurrentPattern(TextPattern.Pattern, out object patternObject) ||
                patternObject is not TextPattern text)
                return null;

            var paragraphs = Collect(text, content, session, timer);
            string identity = pane + ":" + string.Join(".", document.GetRuntimeId()) + ":" + url;
            if (paragraphs == null || !TrySteamUrl(document, out string endUrl) || endUrl != url ||
                !SteamWebClient.TryWebPane(window, out nint endPane, out var endViewport) ||
                endPane != pane || endViewport != viewport)
                return null;
            return new SteamPage(window, session, pane, viewport, identity, url,
                Environment.TickCount64, paragraphs);
        }
        catch (Exception) { return null; }
    }

    private static bool TrySteamUrl(AutomationElement document, out string url)
    {
        url = "";
        return document.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern) &&
            pattern is ValuePattern value && TryAllowedUrl(value.Current.Value, out url);
    }

    internal static bool TryAllowedUrl(string value, out string url)
    {
        url = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? address) ||
            address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort ||
            address.UserInfo.Length != 0 ||
            address.Host is not ("steamcommunity.com" or "store.steampowered.com"))
            return false;
        string path = address.AbsolutePath;
        if (address.Host == "store.steampowered.com")
        {
            if (path != "/" && !Regex.IsMatch(path, @"^/(?:app|category|search|genre|tags|bundle|sub|curator|news)(?:/|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        }
        else if (!SteamPagePaths.IsWorkshopSubscriptions(address) &&
            !Regex.IsMatch(path, @"(?:^/workshop(?:/|$)|^/sharedfiles/filedetails(?:/|$)|/(?:workshop|reviews|recommended|discussions|guides|guide)(?:/|$))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }
        url = address.AbsoluteUri;
        return true;
    }

    internal static bool IsMeaningful(string text) =>
        text.Length >= 4 && text.Count(char.IsLetter) >= 3 &&
        !TextNormalizer.IsUntranslatable(text) && !TextNormalizer.IsSimplifiedChinese(text) &&
        !Noise.IsMatch(text);

    private static List<TextFragment>? Collect(TextPattern text, ScreenRect viewport, string session, Stopwatch timer)
    {
        var paragraphs = new List<TextFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int scanned = 0;
        foreach (var visible in text.GetVisibleRanges())
        {
            var paragraph = visible.Clone();
            paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
            while (paragraph.CompareEndpoints(TextPatternRangeEndpoint.Start, visible, TextPatternRangeEndpoint.End) < 0)
            {
                if (++scanned > MaxParagraphs || timer.ElapsedMilliseconds > 425) return null;
                if (paragraph.CompareEndpoints(TextPatternRangeEndpoint.Start, visible, TextPatternRangeEndpoint.Start) >= 0 &&
                    paragraph.CompareEndpoints(TextPatternRangeEndpoint.End, visible, TextPatternRangeEndpoint.End) <= 0)
                {
                    string original = paragraph.GetText(2401).Trim();
                    string normalized = TextNormalizer.Normalize(original);
                    if (original.Length is >= 4 and <= 2400 && normalized.Count(char.IsLetter) >= 3 &&
                        !TextNormalizer.IsUntranslatable(normalized) && !TextNormalizer.IsSimplifiedChinese(normalized) &&
                        !Noise.IsMatch(normalized) && !seen.Contains(normalized))
                    {
                        Rect[] lines = paragraph.GetBoundingRectangles();
                        if (TryParagraphBounds(lines, viewport, out var bounds))
                        {
                            seen.Add(normalized);
                            var hash = TextNormalizer.ComputeHash(normalized);
                            paragraphs.Add(new TextFragment
                            {
                                Text = original,
                                NormalizedText = normalized,
                                Bounds = bounds,
                                Source = TextSource.Uia,
                                Confidence = 1,
                                SessionId = session,
                                ContentHash = hash,
                                RegionId = TextFragment.BuildRegionId(session, hash, bounds),
                                FragmentId = TextFragment.BuildFragmentId(hash, bounds),
                                DetectedLanguage = LanguageDetector.Detect(normalized),
                                MergedLineCount = lines.Length
                            });
                        }
                    }
                }

                var start = paragraph.Clone();
                if (paragraph.Move(TextUnit.Paragraph, 1) == 0) break;
                if (paragraph.CompareEndpoints(TextPatternRangeEndpoint.Start, start, TextPatternRangeEndpoint.Start) <= 0)
                    return null;
            }
        }
        return paragraphs;
    }

    private static bool TryParagraphBounds(Rect[] lines, ScreenRect viewport, out ScreenRect bounds)
    {
        bounds = default;
        if (lines.Length is 0 or > 24) return false;
        Rect union = Rect.Empty;
        foreach (Rect line in lines)
        {
            var rect = new ScreenRect(line.X, line.Y, line.Width, line.Height);
            if (!SteamWebClient.Contains(viewport, rect) || line.Width < 4 || line.Height < 5 ||
                line.Height > 100) return false;
            if (!union.IsEmpty && (line.Top < union.Top || line.Top > union.Bottom + line.Height ||
                line.Left > union.Right || line.Right < union.Left)) return false;
            union.Union(line);
        }
        bounds = new ScreenRect(union.X, union.Y, union.Width, union.Height);
        return true;
    }

    internal static bool Publish(SteamWindowInfo window, SteamPage? page)
    {
        lock (Gate)
        {
            if (page == null)
            {
                return Invalidate(window);
            }
            if (page.Session != window.CurrentSessionId || !SteamWebClient.IsForeground(window))
                return false;
            if (_current?.Owner == window && _current.Identity != page.Identity)
            {
                Invalidate(window);
                return true;
            }
            if (_current?.Owner != window && window.PageUrl != page.Url)
            {
                window.PageUrl = page.Url;
            }
            _current = page;
            return false;
        }
    }

    internal static bool Invalidate(SteamWindowInfo window)
    {
        lock (Gate)
        {
            if (_current?.Owner != window) return false;
            _current = null;
            window.PageUrl = null;
            window.CurrentSessionId = Guid.NewGuid().ToString("N");
            return true;
        }
    }

    internal static SteamPage? Current(SteamWindowInfo window)
    {
        SteamPage? page;
        lock (Gate) page = _current;
        if (page?.Owner != window || page.Session != window.CurrentSessionId ||
            Environment.TickCount64 - page.ReadAt > LifetimeMs ||
            !SteamWebClient.TryWebPane(window, out nint pane, out var viewport) ||
            page.Pane != pane || page.Viewport != viewport)
            return null;
        return page;
    }
}



