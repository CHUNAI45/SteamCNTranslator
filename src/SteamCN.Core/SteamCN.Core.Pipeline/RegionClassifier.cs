using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Core.Pipeline;

public partial class RegionClassifier
{
	private class FragmentContext
	{
		public TextFragment? Above { get; set; }

		public TextFragment? Below { get; set; }
	}

	private const double ButtonMaxHeight = 30.0;

	private const double ButtonMaxWidth = 160.0;

	private const double TitleMinHeight = 24.0;

	private const double BodyMinWidth = 180.0;

	private const double TopAreaRatio = 0.15;

	public int LastClassifiedCount { get; private set; }

	public int LastSkippedCount { get; private set; }

	public List<TextFragment> Classify(IReadOnlyList<TextFragment> fragments, double viewportHeight)
	{
		LastClassifiedCount = 0;
		LastSkippedCount = 0;
		List<TextFragment> list = new List<TextFragment>(fragments.Count);
		List<FragmentContext> list2 = BuildContexts(fragments);
		for (int i = 0; i < fragments.Count; i++)
		{
			TextFragment textFragment = fragments[i];
			FragmentContext ctx = list2[i];
			RegionType type = (textFragment.RegionType = ClassifyOne(textFragment, ctx, viewportHeight));
			if (ShouldTranslate(type, textFragment))
			{
				list.Add(textFragment);
				LastClassifiedCount++;
			}
			else
			{
				LastSkippedCount++;
			}
		}
		return list;
	}

	private static List<FragmentContext> BuildContexts(IReadOnlyList<TextFragment> fragments)
	{
		List<(TextFragment, int)> list = (from x in fragments.Select((TextFragment f, int i) => (Fragment: f, Index: i))
			orderby x.Fragment.Bounds.Y, x.Fragment.Bounds.X
			select x).ToList();
		FragmentContext[] array = new FragmentContext[fragments.Count];
		for (int num = 0; num < fragments.Count; num++)
		{
			array[num] = new FragmentContext();
		}
		for (int num2 = 0; num2 < list.Count; num2++)
		{
			(TextFragment, int) tuple = list[num2];
			TextFragment item = tuple.Item1;
			int item2 = tuple.Item2;
			FragmentContext fragmentContext = array[item2];
			for (int num3 = num2 + 1; num3 < list.Count && num3 < num2 + 5; num3++)
			{
				TextFragment item3 = list[num3].Item1;
				double num4 = item3.Bounds.Y - (item.Bounds.Y + item.Bounds.Height);
				if (num4 > item.Bounds.Height * 2.0)
				{
					break;
				}
				if (!(num4 < 0.0 - item.Bounds.Height) && item3.Bounds.X < item.Bounds.X + item.Bounds.Width && item3.Bounds.X + item3.Bounds.Width > item.Bounds.X)
				{
					fragmentContext.Below = item3;
					break;
				}
			}
			int num5 = num2 - 1;
			while (num5 >= 0 && num5 > num2 - 5)
			{
				TextFragment item4 = list[num5].Item1;
				double num6 = item.Bounds.Y - (item4.Bounds.Y + item4.Bounds.Height);
				if (num6 > item4.Bounds.Height * 2.0)
				{
					break;
				}
				if (!(num6 < 0.0 - item4.Bounds.Height) && item4.Bounds.X < item.Bounds.X + item.Bounds.Width && item4.Bounds.X + item4.Bounds.Width > item.Bounds.X)
				{
					fragmentContext.Above = item4;
					break;
				}
				num5--;
			}
		}
		return array.ToList();
	}

	private static RegionType ClassifyOne(TextFragment frag, FragmentContext ctx, double viewportHeight)
	{
		double height = frag.Bounds.Height;
		double width = frag.Bounds.Width;
		double y = frag.Bounds.Y;
		string text = frag.Text.Trim();
		bool flag = y < viewportHeight * 0.15;
		if (IsTimeOrDate(text))
		{
			return RegionType.CommentTime;
		}
		if (IsStatisticsText(text))
		{
			return RegionType.Statistics;
		}
		if (height <= 30.0 && width <= 160.0 && text.Length <= 30 && IsButtonText(text))
		{
			return RegionType.Button;
		}
		if (height <= 30.0 && text.Length <= 20 && !text.Contains(' ') && (flag || IsTagText(text)))
		{
			return RegionType.Tag;
		}
		if (flag && height >= 24.0 && text.Length <= 80)
		{
			return RegionType.PageTitle;
		}
		if (flag && height < 24.0 && text.Length <= 40)
		{
			return RegionType.Navigation;
		}
		if (frag.IsParagraph || height > 50.0)
		{
			if (width >= 180.0)
			{
				return RegionType.DescriptionBody;
			}
			return RegionType.CardBody;
		}
		if (text.Length <= 30 && ctx.Below != null && IsTimeOrDate(ctx.Below.Text))
		{
			return RegionType.CommentAuthor;
		}
		if (ctx.Above != null && (IsTimeOrDate(ctx.Above.Text) || ctx.Above.Text.Length <= 30) && width >= 108.0)
		{
			return RegionType.CommentBody;
		}
		if (height >= 16.799999999999997 && height <= 48.0 && text.Length <= 60 && ctx.Below != null && ctx.Below.Bounds.Width >= 90.0)
		{
			return RegionType.CardTitle;
		}
		if (width >= 180.0)
		{
			if (!frag.IsParagraph)
			{
				return RegionType.CardBody;
			}
			return RegionType.DescriptionBody;
		}
		return RegionType.Other;
	}

	private static bool ShouldTranslate(RegionType type, TextFragment frag)
	{
		return type switch
		{
			RegionType.CommentTime => false, 
			RegionType.Statistics => false, 
			RegionType.ImageText => false, 
			_ => true, 
		};
	}

	private static bool IsTimeOrDate(string text)
	{
		if (string.IsNullOrEmpty(text) || text.Length > 40)
		{
			return false;
		}
		if (!TimePattern().IsMatch(text) && !RelativeTimePattern().IsMatch(text) && !text.Contains("ago", StringComparison.OrdinalIgnoreCase))
		{
			if (text.Contains("前", StringComparison.Ordinal))
			{
				return text.Length <= 10;
			}
			return false;
		}
		return true;
	}

	private static bool IsStatisticsText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		if (NumberOnlyPattern().IsMatch(text))
		{
			return true;
		}
		return StatsPattern().IsMatch(text);
	}

	private static bool IsButtonText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		string lower = text.ToLowerInvariant();
		return new string[65]
		{
			"subscribe", "unsubscribe", "订阅", "取消订阅", "add to cart", "加入购物车", "buy", "购买", "install", "安装",
			"play", "开始游戏", "like", "点赞", "favorite", "收藏", "wish", "愿望单", "comment", "评论",
			"reply", "回复", "share", "分享", "follow", "关注", "sign in", "登录", "log in", "log out",
			"退出登录", "download", "下载", "update", "更新", "accept", "接受", "decline", "拒绝", "cancel",
			"取消", "continue", "继续", "back", "返回", "next", "下一步", "submit", "提交", "post",
			"发布", "send", "发送", "rate", "评分", "vote", "投票", "award", "奖励", "report",
			"举报", "block", "屏蔽", "mute", "静音"
		}.Any((string k) => lower.Contains(k));
	}

	private static bool IsTagText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		string lower = text.ToLowerInvariant();
		if (new string[24]
		{
			"scene", "map", "model", "skin", "weapon", "item", "tool", "effect", "npc", "vehicle",
			"building", "campaign", "mission", "quest", "challenge", "trending", "popular", "new", "top", "recent",
			"most", "all", "featured", "recommended"
		}.Any((string k) => lower == k || lower.Contains(k)))
		{
			return text.Length <= 25;
		}
		return false;
	}

	[GeneratedRegex("\\b\\d{1,2}[:/]\\d{2}(:\\d{2})?\\b|\\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\\s+\\d{1,2},?\\s*\\d{4}\\b|\\b\\d{4}[-/]\\d{1,2}[-/]\\d{1,2}\\b", RegexOptions.IgnoreCase)]
	private static partial Regex TimePattern();

	[GeneratedRegex("\\b\\d+\\s*(second|minute|hour|day|week|month|year)s?\\s+ago\\b|\\b\\d+\\s*(秒|分钟|小时|天|周|月|年)前\\b", RegexOptions.IgnoreCase)]
	private static partial Regex RelativeTimePattern();

	[GeneratedRegex("^[\\d,.]+$")]
	private static partial Regex NumberOnlyPattern();

	[GeneratedRegex("\\b\\d[\\d,.]*\\s*(subscribers?|online|users?|downloads?|views?|ratings?|comments?|favorites?|likes?|items?|entries)\\b", RegexOptions.IgnoreCase)]
	private static partial Regex StatsPattern();
}
