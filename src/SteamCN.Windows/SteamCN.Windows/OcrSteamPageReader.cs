using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SteamCN.Windows;

internal sealed class OcrSteamPageReader
{
    private sealed record Frame(nint Pane, ScreenRect Viewport, int Width, int Height,
        byte[] Content, byte[] Toolbar, int ToolbarWidth, int ToolbarHeight, ulong AddressHash, ulong ContentHash);

    private sealed record Source(TextFragment Paragraph, ulong Pixels);
    private sealed record Recognition(string Session, nint Pane, ScreenRect Viewport, ulong AddressHash, ulong ContentHash,
        string Url, IReadOnlyList<Source> Sources);

    private readonly IOcrEngine _engine;
    private OcrEngine? _addressReader;
    private Task<Recognition?>? _pending;
    private Recognition? _recognized;
    private (ulong Address, ulong Content, string Session) _requested;
    private long _retryAt;
    private long _addressSettledAt;
    private ulong _lastAddress;
    private ulong _verifiedBody;
    private string? _verifiedUrl;

    internal OcrSteamPageReader(IOcrEngine engine) => _engine = engine;

    internal SteamPage? Read(SteamWindowInfo window)
    {
        string session = window.CurrentSessionId;
        Frame? frame = Capture(window);
        if (frame == null) return null;
        if (_lastAddress != frame.AddressHash)
        {

            _lastAddress = frame.AddressHash;
            _addressSettledAt = Environment.TickCount64 + 275;
            _retryAt = 0;
        }
        if (Environment.TickCount64 < _addressSettledAt)
            return null;
        if (_pending is { IsCompleted: true })
        {
            var found = _pending.GetAwaiter().GetResult();
            if (found == null) _retryAt = Environment.TickCount64 + 1200;
            if (found != null &&
                found.Session == session && found.Pane == frame.Pane && found.Viewport == frame.Viewport &&
                found.AddressHash == frame.AddressHash)
                _recognized = found;
            _pending = null;
        }

        Recognition? current = _recognized;
        if (current == null || current.Pane != frame.Pane ||
            current.Viewport != frame.Viewport || current.AddressHash != frame.AddressHash)
        {
            current = null;
        }
        var request = (frame.AddressHash, frame.ContentHash, session);
        if (_pending == null && Environment.TickCount64 >= _retryAt &&
            (current == null || (current.ContentHash != frame.ContentHash && _requested != request)))
        {
            _requested = request;
            _retryAt = Environment.TickCount64 + 650;
            string? confirmedUrl = current?.Url;
            var previousUrl = _verifiedUrl;
            var previousBody = _verifiedBody;
            var previous = _recognized;
            _pending = Task.Run(() => RecognizeAsync(frame, session, confirmedUrl, previousUrl, previousBody, previous));
        }
        if (current == null) return null;
        _verifiedUrl = current.Url;
        _verifiedBody = frame.ContentHash;

        var visible = new List<TextFragment>(current.Sources.Count);
        foreach (Source source in current.Sources)
        {
            if (ParagraphPixels(frame, source.Paragraph.Bounds) == source.Pixels)
                visible.Add(ForSession(source.Paragraph, session));
        }

        return new SteamPage(window, session, frame.Pane, frame.Viewport,
            $"ocr:{frame.Pane}:{current.Url}", current.Url,
            Environment.TickCount64, visible);
    }

    private async Task<Recognition?> RecognizeAsync(Frame frame, string session, string? confirmedUrl, string? previousUrl, ulong previousBody, Recognition? previous)
    {
        try
        {
            string? url = confirmedUrl ?? await ReadAddressAsync(frame).ConfigureAwait(false);
            if (url == null || (previousUrl != null && url != previousUrl && frame.ContentHash == previousBody))
                return null;

            if (previous != null && previous.Url == url && previous.Pane == frame.Pane &&
                previous.Viewport == frame.Viewport && previous.ContentHash == frame.ContentHash)
                return previous with { Session = session, AddressHash = frame.AddressHash };

            var contrast = new byte[frame.Content.Length];
            for (int i = 0; i < contrast.Length; i += 4)
            {
                int value = (frame.Content[i] + 2 * frame.Content[i + 1] + frame.Content[i + 2]) / 4;
                byte gray = (byte)Math.Clamp((value - 96) * 5 / 4 + 96, 0, 255);
                contrast[i] = contrast[i + 1] = contrast[i + 2] = gray;
                contrast[i + 3] = 255;
            }

            var languages = _engine.AvailableLanguages;
            var alternate = languages.Where(tag => BaseLanguage(tag) is "ja" or "ko" or "ru" or "uk" or "el")
                .DistinctBy(BaseLanguage).ToArray();
            bool workshopSubscriptions = SteamPagePaths.IsWorkshopSubscriptions(new Uri(url));
            var runs = await Task.WhenAll(new[] { "en-US" }.Concat(alternate)
                .Select(language => VerifyLinesAsync(frame, contrast, language, workshopSubscriptions))).ConfigureAwait(false);
            var native = runs.Skip(1).SelectMany(run => run.Verified).ToList();
            var lines = native.Where(line => !native.Any(other =>
                    line.NormalizedText != other.NormalizedText && SameLocation(line.Bounds, other.Bounds)))
                .Concat(runs[0].Verified.Where(line => !native.Any(other => SameLocation(line.Bounds, other.Bounds))))
                .DistinctBy(line => line.NormalizedText)
                .OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).Take(100).ToList();

            var content = workshopSubscriptions
                ? lines.Where(line => IsWorkshopSubscriptionTitle(line, runs[0].Raw, frame)).ToList()
                : FilterLabels(lines, runs[0].Raw.Concat(lines).ToArray(), frame, url);
            var paragraphs = RebuildParagraphs(content, frame, session);
            return new Recognition(session, frame.Pane, frame.Viewport, frame.AddressHash, frame.ContentHash, url,
                paragraphs.Select(p => new Source(p, ParagraphPixels(frame, p.Bounds))).ToList());
        }
        catch (Exception) { return null; }
    }

    private async Task<(IReadOnlyList<TextFragment> Raw, IReadOnlyList<TextFragment> Verified)> VerifyLinesAsync(
        Frame frame, byte[] contrast, string language, bool workshopSubscriptions)
    {
        var raw = await _engine.RecognizeAsync(frame.Content, frame.Width, frame.Height, language).ConfigureAwait(false);
        var confirmed = await _engine.RecognizeAsync(contrast, frame.Width, frame.Height, language).ConfigureAwait(false);
        string script = Script(BaseLanguage(language));
        var verified = raw.Where(line => Script(line.DetectedLanguage) == script && IsReliable(line, frame, workshopSubscriptions) &&
            confirmed.Any(candidate => candidate.Confidence >= 0.85 &&
                WithoutTerminalPunctuation(line.NormalizedText) == WithoutTerminalPunctuation(candidate.NormalizedText) &&
                Overlap(line.Bounds, candidate.Bounds) >= 0.88)).ToArray();
        return (raw, verified);
    }

    private static string BaseLanguage(string tag) => tag.Split('-')[0].ToLowerInvariant();

    private static string Script(string? language) => language switch
    {
        "ru" or "uk" => "cyrillic",
        "ja" or "ko" or "el" or "zh" => language,
        _ => "latin"
    };

    private static bool SameLocation(ScreenRect a, ScreenRect b)
    {
        var intersection = a.Intersect(b);
        return intersection.Width * intersection.Height >= Math.Min(a.Width * a.Height, b.Width * b.Height) * 0.68;
    }

    private async Task<string?> ReadAddressAsync(Frame frame)
    {
        _addressReader ??= OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (_addressReader == null) return null;
        int width = frame.ToolbarWidth * 3;
        int height = frame.ToolbarHeight * 3;
        string? repairedSubscriptionUrl = null;
        var observedStoreApps = new HashSet<string>(StringComparer.Ordinal);
        foreach (int threshold in new[] { 39, 35, 43, 31, 47, 23 })
        {
            var buffer = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte red = frame.Toolbar[((y / 3) * frame.ToolbarWidth + x / 3) * 4 + 2];
                    byte value = (byte)(255 - Math.Clamp((red - threshold) * 6, 0, 255));
                    int i = (y * width + x) * 4;
                    buffer[i] = buffer[i + 1] = buffer[i + 2] = value;
                    buffer[i + 3] = 255;
                }
            }
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer.AsBuffer(),
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            var result = await _addressReader.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
            var storeApps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in result.Lines)
            {
                string address = string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c)));
                // Steam displays localized game slugs. A verified app ID identifies the page
                // even when the English address recognizer cannot read the slug.
                if (TryReadStoreAppAddress(address, out string storeApp))
                    storeApps.Add(storeApp);
                if (!Regex.IsMatch(address, @"^https://(?:store\.steampowered\.com|steamcommunity\.com)/[A-Za-z0-9_/.?&=+%#~-]*$",
                    RegexOptions.CultureInvariant)) continue;
                if (SteamPageReader.TryAllowedUrl(address, out string url)) return url;
                if (!TryRepairSubscriptionAddress(address, out url)) continue;
                if (url == repairedSubscriptionUrl) return url;
                repairedSubscriptionUrl = url;
            }
            foreach (string storeApp in storeApps)
                if (!observedStoreApps.Add(storeApp)) return storeApp;
        }
        return null;
    }

    private static bool TryReadStoreAppAddress(string address, out string url)
    {
        url = "";
        var match = Regex.Match(address, @"^https://store\.steampowered\.com/app/([1-9][0-9]{0,9})(?:/|$)",
            RegexOptions.CultureInvariant);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out uint appId)) return false;
        url = $"https://store.steampowered.com/app/{appId}/";
        return true;
    }

    private static bool TryRepairSubscriptionAddress(string address, out string url)
    {
        url = "";
        var match = Regex.Match(address,
            @"^(https://steamcommunity\.com/(?:id/[A-Za-z0-9_-]+|profiles/[0-9]{17})/)myworkshopfi[lI]es/?\?(.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var query = match.Groups[2].Value.Split('&');
        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] == "browsefiIter=mysubscriptions") query[i] = "browsefilter=mysubscriptions";
            else if (Regex.IsMatch(query[i], @"^appid=[0-9Il]+$", RegexOptions.CultureInvariant))
                query[i] = query[i].Replace('I', '1').Replace('l', '1');
        }
        string corrected = match.Groups[1].Value + "myworkshopfiles?" + string.Join('&', query);
        return Uri.TryCreate(corrected, UriKind.Absolute, out var candidate) &&
            SteamPagePaths.IsWorkshopSubscriptions(candidate) && SteamPageReader.TryAllowedUrl(corrected, out url);
    }

    private static string WithoutTerminalPunctuation(string value) => value.TrimEnd('.', ',', ':', ';', '!', '?', '…');

    private static TextFragment ForSession(TextFragment source, string session)
    {
        if (source.SessionId == session) return source;
        return new TextFragment
        {
            Text = source.Text,
            NormalizedText = source.NormalizedText,
            Bounds = source.Bounds,
            Source = source.Source,
            Confidence = source.Confidence,
            DetectedLanguage = source.DetectedLanguage,
            SessionId = session,
            ContentHash = source.ContentHash,
            RegionId = TextFragment.BuildRegionId(session, source.ContentHash, source.Bounds),
            FragmentId = source.FragmentId,
            MergedLineCount = source.MergedLineCount
        };
    }

    private static bool IsReliable(TextFragment line, Frame frame, bool workshopSubscriptions)
    {
        string text = line.NormalizedText;
        var bounds = line.Bounds;
        return line.Confidence >= 0.85 && SteamPageReader.IsMeaningful(text) &&
            !Regex.IsMatch(text, @"^[>›»]") &&
            !Regex.IsMatch(text, @"(?:\.{2,}|[…„])$") &&
            text.Count(char.IsLetter) >= (workshopSubscriptions ? 4 : 5) &&
            (workshopSubscriptions || line.DetectedLanguage is "ja" or "ko" || text.Any(char.IsWhiteSpace) || bounds.Height >= 18) &&
            bounds.Width >= 24 && bounds.Height >= 9 && bounds.Height <= 65 &&
            bounds.X > 1 && bounds.Y > 1 && bounds.Right < frame.Width - 2 && bounds.Bottom < frame.Height - 2;
    }

    private static bool IsWorkshopSubscriptionTitle(TextFragment line, IReadOnlyList<TextFragment> rawLines, Frame frame)
    {
        var title = line.Bounds;
        if (title.X > frame.Width * 0.64 || title.Height < 12) return false;
        // Every subscription row puts the smaller game name immediately under its item title.
        return rawLines.Any(game =>
        {
            var label = game.Bounds;
            double gap = label.Y - title.Bottom;
            return gap >= 0 && gap <= title.Height && label.Height >= title.Height * 0.55 &&
                label.Height < title.Height && Math.Abs(title.X - label.X) <= title.Height * 0.4 &&
                game.NormalizedText.Count(char.IsLetter) >= 3 && game.NormalizedText.Count(char.IsDigit) <= 3;
        });
    }

    private static List<TextFragment> FilterLabels(List<TextFragment> lines, IReadOnlyList<TextFragment> rawLines, Frame frame, string url)
    {
        var address = new Uri(url);
        string path = address.AbsolutePath.TrimEnd('/');
        bool store = address.Host == "store.steampowered.com";
        bool discussionIndex = path.EndsWith("/discussions", StringComparison.Ordinal);
        bool workshopDetail = path == "/sharedfiles/filedetails";
        var content = new List<TextFragment>();
        foreach (var line in lines)
        {
            var box = line.Bounds;
            if (discussionIndex && box.X > frame.Width * 0.64) continue;
            if (store && (box.Y < frame.ToolbarHeight * 1.65 ||
                Regex.IsMatch(line.NormalizedText, @"^(?:community hub|recent reviews|english reviews|release date|developer|publisher|(?:overwhelmingly|very|mostly) (?:positive|negative)(?:\s*\([\d,.\s]+\))?|mixed(?:\s*\([\d,.\s]+\))?)\s*:?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                continue;
            bool metadata = discussionIndex && rawLines.Any(above =>
            {
                var heading = above.Bounds;
                double gap = box.Y - heading.Bottom;
                return gap >= 0 && gap <= heading.Height * 2 &&
                    Math.Abs(box.X - heading.X) <= heading.Height * 0.25 &&
                    box.Height <= heading.Height * 1.18 && box.Width <= heading.Width &&
                    line.Text.Length < 36;
            });
            if (workshopDetail && LooksLikeCommentAuthor(line, lines, frame)) metadata = true;
            if (!metadata) content.Add(line);
        }
        return content;
    }

    private static bool LooksLikeCommentAuthor(TextFragment line, List<TextFragment> lines, Frame frame)
    {
        var name = line.Bounds;
        if (line.Text.Length > 38 || name.Width > name.Height * 18 ||
            Regex.IsMatch(line.Text.TrimEnd(), @"[.!?。！？]$")) return false;
        if (!lines.Any(body => body.Bounds.Y - name.Bottom >= name.Height &&
            body.Bounds.Y - name.Bottom <= name.Height * 3 &&
            Math.Abs(body.Bounds.X - name.X) <= name.Height * 0.35 &&
            body.Text.Length >= 12)) return false;

        int left = (int)(name.X - name.Height * 4.2);
        int right = (int)(name.X - name.Height * 1.35);
        int middle = (int)(name.Y + name.Height * 0.5);
        int backgroundX = (int)(name.X - name.Height * 0.45);
        if (left < 0 || right <= left || middle < 0 || middle + 5 >= frame.Height || backgroundX >= frame.Width)
            return false;
        int background = (middle * frame.Width + backgroundX) * 4;
        var shades = new HashSet<int>();
        int different = 0;
        for (int y = middle; y < middle + 5; y += 2)
        {
            for (int x = left; x < right; x += 4)
            {
                int pixel = (y * frame.Width + x) * 4;
                byte[] rgb = frame.Content;
                int contrast = Math.Abs(rgb[pixel] - rgb[background]) +
                    Math.Abs(rgb[pixel + 1] - rgb[background + 1]) + Math.Abs(rgb[pixel + 2] - rgb[background + 2]);
                if (contrast < 65) continue;
                different++;
                shades.Add((rgb[pixel] >> 4) | ((rgb[pixel + 1] >> 4) << 4) | ((rgb[pixel + 2] >> 4) << 8));
            }
        }
        return different >= 8 && shades.Count >= 5;
    }

    private static double Overlap(ScreenRect a, ScreenRect b)
    {
        var intersection = a.Intersect(b);
        return intersection.Width * intersection.Height / Math.Max(a.Width * a.Height, b.Width * b.Height);
    }

    private static List<TextFragment> RebuildParagraphs(List<TextFragment> lines, Frame frame, string session)
    {
        var paragraphs = new List<TextFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consumed = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (consumed[i]) continue;
            var first = lines[i];
            var last = first;
            var bounds = first.Bounds;
            string text = first.Text;
            int count = 1;
            int lastIndex = i;
            while (count < 16)
            {
                var previous = last.Bounds;
                if (count > 1 && previous.Width < bounds.Width * 0.45) break;
                int nextIndex = -1;
                for (int candidateIndex = lastIndex + 1; candidateIndex < lines.Count; candidateIndex++)
                {
                    var candidate = lines[candidateIndex];
                    var rect = candidate.Bounds;
                    double gap = rect.Y - previous.Bottom;
                    if (gap > previous.Height * 0.9) break;
                    if (consumed[candidateIndex] || gap < 0 ||
                        Script(first.DetectedLanguage) != Script(candidate.DetectedLanguage) ||
                        Math.Abs(rect.X - first.Bounds.X) > previous.Height * 0.55 ||
                        Math.Abs(rect.Height - previous.Height) > previous.Height * 0.32 ||
                        Regex.IsMatch(candidate.Text, @"^\s*(?:[-•*]|\d+[.)])\s+"))
                        continue;
                    nextIndex = candidateIndex;
                    break;
                }
                if (nextIndex < 0) break;

                var next = lines[nextIndex];
                var nextBounds = next.Bounds;
                text += " " + next.Text.Trim();
                double left = Math.Min(bounds.X, nextBounds.X);
                bounds = new ScreenRect(left, bounds.Y,
                    Math.Max(bounds.Right, nextBounds.Right) - left, nextBounds.Bottom - bounds.Y);
                consumed[nextIndex] = true;
                lastIndex = nextIndex;
                last = next;
                count++;
            }
            bool continuesBelowViewport = frame.Height - bounds.Bottom < last.Bounds.Height * 1.6 &&
                last.Bounds.Width >= frame.Width * 0.32 &&
                !Regex.IsMatch(last.Text.TrimEnd(), @"[.!?。！？…][""')\]]*$");
            if (continuesBelowViewport) continue;
            string normalized = TextNormalizer.Normalize(text);
            if (normalized.Length > 1000 || !seen.Add(normalized)) continue;
            bounds = bounds with { X = bounds.X + frame.Viewport.X, Y = bounds.Y + frame.Viewport.Y };
            var hash = TextNormalizer.ComputeHash(normalized);
            paragraphs.Add(new TextFragment
            {
                Text = text,
                NormalizedText = normalized,
                Bounds = bounds,
                Source = TextSource.Ocr,
                Confidence = first.Confidence,
                DetectedLanguage = LanguageDetector.Detect(normalized),
                SessionId = session,
                ContentHash = hash,
                RegionId = TextFragment.BuildRegionId(session, hash, bounds),
                FragmentId = TextFragment.BuildFragmentId(hash, bounds),
                MergedLineCount = count
            });
        }
        return paragraphs;
    }

    private static ulong ParagraphPixels(Frame frame, ScreenRect paragraph)
    {
        int x = (int)(paragraph.X - frame.Viewport.X);
        int y = (int)(paragraph.Y - frame.Viewport.Y);
        int w = (int)Math.Ceiling(paragraph.Right - frame.Viewport.X) - x;
        int h = (int)Math.Ceiling(paragraph.Bottom - frame.Viewport.Y) - y;
        if (x < 0 || y < 0 || x + w > frame.Width || y + h > frame.Height || w <= 0 || h <= 0)
            return 0;
        return PixelHash(frame.Content, frame.Width, x, y, w, h, 1);
    }

    private static ulong PixelHash(byte[] pixels, int stride, int x, int y, int width, int height, int step)
    {
        ulong hash = 14695981039346656037UL;
        for (int row = y; row < y + height; row += step)
        {
            for (int col = x; col < x + width; col += step)
            {
                int i = (row * stride + col) * 4;
                hash = (hash ^ (uint)(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16)) * 1099511628211UL;
            }
        }
        return hash;
    }

    private static Frame? Capture(SteamWindowInfo window)
    {
        if (!SteamWebClient.TryWebPane(window, out nint pane, out var viewport)) return null;
        nint previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) return null;
        try
        {
            if (!NativeMethods.GetWindowRect(window.Handle, out var outer) ||
                outer.Width is < 300 or > 4096 || outer.Height is < 200 or > 4096 ||
                !SteamWebClient.Contains(new ScreenRect(outer.Left, outer.Top, outer.Width, outer.Height), viewport))
                return null;
            double dpi = NativeMethods.GetDpiForWindow(window.Handle) / 96.0;
            int addressX = (int)(60 * dpi);
            int addressHeight = (int)(28 * dpi);
            int addressY = (int)viewport.Y - outer.Top - addressHeight;
            int addressWidth = Math.Min(1000, outer.Width - addressX - 4);
            if (addressX < 1 || addressY < 1 || addressWidth < 200 || addressHeight < 10)
                return null;

            using var bitmap = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                nint dc = graphics.GetHdc();
                try
                {
                    if (!NativeMethods.PrintWindow(window.Handle, dc, NativeMethods.PW_RENDERFULLCONTENT))
                        return null;
                }
                finally { graphics.ReleaseHdc(dc); }
            }

            if (!SteamWebClient.TryWebPane(window, out nint endPane, out var endViewport) ||
                pane != endPane || viewport != endViewport)
                return null;

            var address = CopyPixels(bitmap, new Rectangle(addressX, addressY, addressWidth, addressHeight));
            var web = CopyPixels(bitmap, new Rectangle((int)viewport.X - outer.Left, (int)viewport.Y - outer.Top,
                (int)viewport.Width, (int)viewport.Height));
            int width = (int)viewport.Width;
            int height = (int)viewport.Height;
            return new Frame(pane, viewport, width, height, web, address, addressWidth, addressHeight,
                PixelHash(address, addressWidth, 0, 0, addressWidth, addressHeight, 1),
                PixelHash(web, width, 0, 0, width, height, 5));
        }
        catch (Exception) { return null; }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static byte[] CopyPixels(Bitmap bitmap, Rectangle rect)
    {
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int row = rect.Width * 4;
            var pixels = new byte[row * rect.Height];
            for (int y = 0; y < rect.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * row, row);
                for (int i = y * row + 3; i < (y + 1) * row; i += 4) pixels[i] = 255;
            }
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}










