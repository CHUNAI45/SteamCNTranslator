using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Windows;

public class UiaTextReader
{
	private const int UiaTimeBudgetMs = 3000;

	private const int MaxScanDepth = 5;

	private const int MaxNodes = 150;

	private const int MaxFragments = 80;

	private const uint OBJID_CLIENT = 4294967292u;

	private nint _cachedWindowHandle;

	private List<AutomationElement>? _cachedTextElements;

	private DateTime _cacheTime = DateTime.MinValue;

	private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30.0);

	public long LastScanMs { get; private set; }

	public bool LastScanTimedOut { get; private set; }

	public int LastNodeCount { get; private set; }

	[DllImport("oleacc.dll")]
	private static extern int AccessibleObjectFromWindow(nint hWnd, uint dwId, ref Guid riid, out nint ppvObject);

	public List<TextFragment> ReadText(SteamWindowInfo window, ScreenRect visibleRegion, string sessionId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		List<TextFragment> list = new List<TextFragment>();
		LastScanTimedOut = false;
		LastNodeCount = 0;
		AutomationElement automationElement = null;
		try
		{
			TryTriggerChromiumAccessibility(window.Handle);
			automationElement = AutomationElement.FromHandle(window.Handle);
		}
		catch (Exception)
		{
			LastScanMs = stopwatch.ElapsedMilliseconds;
			return list;
		}
		if (automationElement == null)
		{
			LastScanMs = stopwatch.ElapsedMilliseconds;
			return list;
		}
		try
		{
			CollectBounded(automationElement, window, visibleRegion, sessionId, list, stopwatch);
		}
		catch (Exception)
		{
		}
		LastScanMs = stopwatch.ElapsedMilliseconds;
		return list;
	}

	private void CollectBounded(AutomationElement root, SteamWindowInfo window, ScreenRect visibleRegion, string sessionId, List<TextFragment> fragments, Stopwatch sw)
	{
		int nodeCount = 0;
		foreach (AutomationElement item in GetTextElementsWithCache(root, sw, ref nodeCount))
		{
			if (fragments.Count >= 80)
			{
				break;
			}
			if (sw.ElapsedMilliseconds > 3000)
			{
				LastScanTimedOut = true;
				break;
			}
			try
			{
				if (item.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) && patternObject is TextPattern textPattern)
				{
					ExtractFromTextPattern(textPattern, window, visibleRegion, sessionId, fragments, sw);
				}
			}
			catch
			{
			}
		}
		if (fragments.Count < 10 && sw.ElapsedMilliseconds < 3000)
		{
			CollectNamedBounded(root, window, visibleRegion, sessionId, fragments, sw, ref nodeCount);
		}
		LastNodeCount = nodeCount;
	}

	private List<AutomationElement> GetTextElementsWithCache(AutomationElement root, Stopwatch sw, ref int nodeCount)
	{
		if (_cachedTextElements != null && _cachedWindowHandle == root.Current.NativeWindowHandle && DateTime.UtcNow - _cacheTime < CacheExpiry)
		{
			return _cachedTextElements;
		}
		List<AutomationElement> list = new List<AutomationElement>();
		try
		{
			PropertyCondition condition = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
			foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, condition))
			{
				if (list.Count < 150)
				{
					list.Add(item);
					nodeCount++;
					continue;
				}
				break;
			}
		}
		catch
		{
		}
		_cachedWindowHandle = root.Current.NativeWindowHandle;
		_cachedTextElements = list;
		_cacheTime = DateTime.UtcNow;
		return list;
	}

	public void InvalidateCache()
	{
		_cachedTextElements = null;
		_cachedWindowHandle = IntPtr.Zero;
		_cacheTime = DateTime.MinValue;
	}

	private void ExtractFromTextPattern(TextPattern textPattern, SteamWindowInfo window, ScreenRect visibleRegion, string sessionId, List<TextFragment> fragments, Stopwatch sw)
	{
		try
		{
			TextPatternRange[] visibleRanges = textPattern.GetVisibleRanges();
			foreach (TextPatternRange textPatternRange in visibleRanges)
			{
				if (fragments.Count >= 80 || sw.ElapsedMilliseconds > 3000)
				{
					break;
				}
				string text = textPatternRange.GetText(-1);
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				Rect[] boundingRectangles = textPatternRange.GetBoundingRectangles();
				if (boundingRectangles.Length == 0)
				{
					Rect[] boundingRectangles2 = textPattern.DocumentRange.GetBoundingRectangles();
					if (boundingRectangles2.Length != 0)
					{
						AddFragment(text, boundingRectangles2[0], window, visibleRegion, sessionId, fragments);
					}
					continue;
				}
				Rect[] array = boundingRectangles;
				int num = 0;
				if (num < array.Length)
				{
					Rect rect = array[num];
					AddFragment(text, rect, window, visibleRegion, sessionId, fragments);
				}
			}
		}
		catch
		{
		}
	}

	private void CollectNamedBounded(AutomationElement root, SteamWindowInfo window, ScreenRect visibleRegion, string sessionId, List<TextFragment> fragments, Stopwatch sw, ref int nodeCount)
	{
		TreeWalker contentViewWalker = TreeWalker.ContentViewWalker;
		Queue<(AutomationElement, int)> queue = new Queue<(AutomationElement, int)>();
		AutomationElement firstChild = contentViewWalker.GetFirstChild(root);
		if (firstChild != null)
		{
			queue.Enqueue((firstChild, 1));
		}
		while (queue.Count > 0 && fragments.Count < 80 && nodeCount < 150)
		{
			if (sw.ElapsedMilliseconds > 3000)
			{
				LastScanTimedOut = true;
				break;
			}
			var (automationElement, num) = queue.Dequeue();
			nodeCount++;
			try
			{
				string name = automationElement.Current.Name;
				if (!string.IsNullOrWhiteSpace(name) && name.Length > 2 && name.Length < 2000)
				{
					Rect boundingRectangle = automationElement.Current.BoundingRectangle;
					if (!boundingRectangle.IsEmpty && boundingRectangle.Width > 0.0 && boundingRectangle.Height > 0.0)
					{
						AddFragment(name, boundingRectangle, window, visibleRegion, sessionId, fragments);
					}
				}
				if (num < 5)
				{
					AutomationElement automationElement2 = contentViewWalker.GetFirstChild(automationElement);
					while (automationElement2 != null)
					{
						queue.Enqueue((automationElement2, num + 1));
						automationElement2 = contentViewWalker.GetNextSibling(automationElement2);
					}
				}
			}
			catch
			{
			}
		}
	}

	private void AddFragment(string text, Rect rect, SteamWindowInfo window, ScreenRect visibleRegion, string sessionId, List<TextFragment> fragments)
	{
		ScreenRect bounds = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
		if (!bounds.IntersectsWith(visibleRegion) || bounds.Width < 5.0 || bounds.Height < 5.0)
		{
			return;
		}
		string text2 = TextNormalizer.Normalize(text);
		if (!string.IsNullOrEmpty(text2) && !TextNormalizer.IsUntranslatable(text2))
		{
			string contentHash = TextNormalizer.ComputeHash(text2);
			int num = 0;
			string regionId = TextFragment.BuildRegionId(sessionId, contentHash, bounds, num);
			while (fragments.Any((TextFragment f) => f.RegionId == regionId))
			{
				num++;
				regionId = TextFragment.BuildRegionId(sessionId, contentHash, bounds, num);
			}
			TextFragment textFragment = new TextFragment
			{
				ContentHash = contentHash,
				RegionId = regionId,
				FragmentId = TextFragment.BuildFragmentId(contentHash, bounds),
				Text = text.Trim(),
				NormalizedText = text2,
				Bounds = bounds,
				Source = TextSource.Uia,
				Confidence = 1.0,
				SessionId = sessionId,
				IsVisible = true
			};
			textFragment.DetectedLanguage = LanguageDetector.Detect(text2);
			if (!TextNormalizer.IsSimplifiedChinese(text2))
			{
				fragments.Add(textFragment);
			}
		}
	}

	private static void TryTriggerChromiumAccessibility(nint windowHandle)
	{
		try
		{
			Guid riid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
			if (AccessibleObjectFromWindow(windowHandle, 4294967292u, ref riid, out var ppvObject) == 0 && ppvObject != IntPtr.Zero)
			{
				Marshal.Release(ppvObject);
			}
		}
		catch
		{
		}
	}

	public bool IsAvailable(SteamWindowInfo window)
	{
		try
		{
			AutomationElement automationElement = AutomationElement.FromHandle(window.Handle);
			PropertyCondition condition = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
			return automationElement.FindFirst(TreeScope.Descendants, condition) != null;
		}
		catch
		{
			return false;
		}
	}
}
