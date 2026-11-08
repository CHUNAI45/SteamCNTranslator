using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Compatibility;

public partial class PageClassifier : IPageClassifier
{
	private readonly Dictionary<nint, PageArea> _manualOverrides = new Dictionary<nint, PageArea>();

	private readonly object _lock = new object();

	private static readonly string[] s_workshopKeywords = new string[9] { "subscribe", "unsubscribe", "订阅", "取消订阅", "item", " creations", "creation", " addon", "mod" };

	private static readonly string[] s_reviewKeywords = new string[9] { "recommended", "not recommended", "推荐", "不推荐", "helpful", "有用", "hours played", "游戏时长", "hours on record" };

	private static readonly string[] s_communityKeywords = new string[12]
	{
		"discussion", "讨论", "forum", "帖子", "reply", "回复", "topic", "话题", "member", "成员",
		"group", "群组"
	};

	private static readonly string[] s_storeKeywords = new string[12]
	{
		"add to cart", "加入购物车", "buy", "购买", "wishlist", "愿望单", "price", "价格", "discount", "折扣",
		"free to play", "免费"
	};

	private static readonly string[] s_guideKeywords = new string[8] { "guide", "指南", "tutorial", "教程", "walkthrough", "攻略", "how to", "如何" };

	public Task<PageClassification> ClassifyAsync(SteamWindowInfo window, IReadOnlyList<TextFragment> fragments, CancellationToken cancellationToken = default(CancellationToken))
	{
		lock (_lock)
		{
			if (_manualOverrides.TryGetValue(window.Handle, out var value))
			{
				return Task.FromResult(CreateResult(value, 1.0, "用户手动指定", window));
			}
		}
		var (area, confidence, evidence) = ClassifyInternal(window, fragments);
		return Task.FromResult(CreateResult(area, confidence, evidence, window));
	}

	public void SetManualOverride(nint windowHandle, PageArea area)
	{
		lock (_lock)
		{
			_manualOverrides[windowHandle] = area;
		}
	}

	public void ClearManualOverride(nint windowHandle)
	{
		lock (_lock)
		{
			_manualOverrides.Remove(windowHandle);
		}
	}

	private (PageArea Area, double Confidence, string Evidence) ClassifyInternal(SteamWindowInfo window, IReadOnlyList<TextFragment> fragments)
	{
				if (Uri.TryCreate(window.PageUrl, UriKind.Absolute, out Uri? address))
		{
			string path = address.AbsolutePath.ToLowerInvariant();
			PageArea area = PageArea.Unknown;
			if (path.Contains("/reviews") || path.Contains("/recommended")) area = PageArea.Reviews;
			else if (path.Contains("/workshop") || path.Contains("/filedetails") || SteamPagePaths.IsWorkshopSubscriptions(address)) area = PageArea.Workshop;
			else if (path.Contains("/guides") || path.Contains("/guide")) area = PageArea.Guides;
			else if (path.Contains("/discussions") || path.Contains("/groups/")) area = PageArea.Community;
			else if (address.Host == "store.steampowered.com") area = PageArea.Store;
			if (area != PageArea.Unknown) return (area, 1.0, "Steam 网页地址");
		}
		string text = window.Title ?? string.Empty;
		string text2 = text.ToLowerInvariant();
		string textLower = string.Join(' ', fragments.Select((TextFragment f) => f.Text)).ToLowerInvariant();
		if (text2.Contains("workshop") || text2.Contains("创意工坊"))
		{
			return (Area: PageArea.Workshop, Confidence: 0.95, Evidence: "标题匹配: " + text);
		}
		if (text2.Contains("review") || text2.Contains("评测") || text2.Contains("评价"))
		{
			return (Area: PageArea.Reviews, Confidence: 0.9, Evidence: "标题匹配: " + text);
		}
		if (text2.Contains("community hub") || text2.Contains("社区中心") || text2.Contains("discussions") || text2.Contains("讨论区"))
		{
			return (Area: PageArea.Community, Confidence: 0.9, Evidence: "标题匹配: " + text);
		}
		if (text2.Contains("store") || text2.Contains("商店"))
		{
			return (Area: PageArea.Store, Confidence: 0.85, Evidence: "标题匹配: " + text);
		}
		if (text2.Contains("guide") || text2.Contains("指南"))
		{
			return (Area: PageArea.Guides, Confidence: 0.9, Evidence: "标题匹配: " + text);
		}
		Match match = UrlPattern().Match(text2);
		if (match.Success)
		{
			var (pageArea, item) = ClassifyByUrl(match.Value);
			if (pageArea != PageArea.Unknown)
			{
				return (Area: pageArea, Confidence: item, Evidence: "URL匹配: " + match.Value);
			}
		}
		var (pageArea2, item2, item3) = ClassifyByText(textLower, fragments);
		if (pageArea2 != PageArea.Unknown)
		{
			return (Area: pageArea2, Confidence: item2, Evidence: item3);
		}
		return (Area: PageArea.Unknown, Confidence: 0.3, Evidence: "无法识别页面类型");
	}

	private static (PageArea, double) ClassifyByUrl(string url)
	{
		if (url.Contains("/workshop"))
		{
			return (PageArea.Workshop, 0.95);
		}
		if (url.Contains("/reviews") || url.Contains("recommended"))
		{
			return (PageArea.Reviews, 0.9);
		}
		if (url.Contains("/community") || url.Contains("/discussions"))
		{
			return (PageArea.Community, 0.9);
		}
		if (url.Contains("/app/") && url.Contains("store"))
		{
			return (PageArea.Store, 0.85);
		}
		if (url.Contains("/guides"))
		{
			return (PageArea.Guides, 0.9);
		}
		return (PageArea.Unknown, 0.0);
	}

	private static (PageArea, double, string) ClassifyByText(string textLower, IReadOnlyList<TextFragment> fragments)
	{
		int num = CountKeywords(textLower, s_workshopKeywords);
		if (num >= 3)
		{
			return (PageArea.Workshop, 0.7, $"文本关键词命中 {num} 个工坊特征词");
		}
		int num2 = CountKeywords(textLower, s_reviewKeywords);
		if (num2 >= 3)
		{
			return (PageArea.Reviews, 0.7, $"文本关键词命中 {num2} 个评价特征词");
		}
		int num3 = CountKeywords(textLower, s_communityKeywords);
		if (num3 >= 3)
		{
			return (PageArea.Community, 0.65, $"文本关键词命中 {num3} 个社区特征词");
		}
		int num4 = CountKeywords(textLower, s_storeKeywords);
		if (num4 >= 4)
		{
			return (PageArea.Store, 0.6, $"文本关键词命中 {num4} 个商店特征词");
		}
		int num5 = CountKeywords(textLower, s_guideKeywords);
		if (num5 >= 2)
		{
			return (PageArea.Guides, 0.65, $"文本关键词命中 {num5} 个指南特征词");
		}
		return (PageArea.Unknown, 0.0, "文本关键词不足");
	}

	private static int CountKeywords(string text, string[] keywords)
	{
		int num = 0;
		foreach (string value in keywords)
		{
			if (text.Contains(value))
			{
				num++;
			}
		}
		return num;
	}

	private static PageClassification CreateResult(PageArea area, double confidence, string evidence, SteamWindowInfo window)
	{
		return new PageClassification
		{
			Area = area,
			Confidence = confidence,
			Evidence = evidence,
			AppId = ExtractAppId(window.Title),
			SessionId = Guid.NewGuid().ToString("N")
		};
	}

	private static string? ExtractAppId(string? title)
	{
		if (string.IsNullOrEmpty(title))
		{
			return null;
		}
		Match match = AppIdPattern().Match(title);
		if (!match.Success)
		{
			return null;
		}
		return match.Groups[1].Value;
	}

	[GeneratedRegex("https?://[^\\s]+")]
	private static partial Regex UrlPattern();

	[GeneratedRegex("(?:/app/(\\d+))|(?:::\\s*(\\d+)\\s*$)")]
	private static partial Regex AppIdPattern();
}

