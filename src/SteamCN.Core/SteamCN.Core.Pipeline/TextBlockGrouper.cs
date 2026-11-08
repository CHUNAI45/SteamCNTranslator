using System;
using System.Collections.Generic;
using System.Linq;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Core.Pipeline;

public class TextBlockGrouper
{
	private class VisualLine
	{
		public List<TextFragment> Fragments { get; } = new List<TextFragment>();

		public ScreenRect Bounds { get; set; }

		public string Text { get; set; } = string.Empty;

		public double LineHeight { get; set; }

		public double Left { get; set; }

		public double Right { get; set; }

		public double Top { get; set; }

		public double Bottom { get; set; }

		public string? DetectedLanguage { get; set; }

		public double MaxConfidence { get; set; }
	}

	private class Paragraph
	{
		public List<VisualLine> Lines { get; } = new List<VisualLine>();
	}

	private const double MaxGapRatio = 1.6;

	private const double MaxIndentDeviation = 30.0;

	private const double MinHorizontalOverlap = 0.3;

	private const double MaxHeightDiffRatio = 0.45;

	private const int MaxLinesPerParagraph = 40;

	public int LastInputCount { get; private set; }

	public int LastOutputCount { get; private set; }

	public int LastMergedLines { get; private set; }

	public List<TextFragment> Group(List<TextFragment> fragments, string sessionId)
	{
		LastInputCount = fragments.Count;
		List<TextFragment> collection = fragments.Where((TextFragment f) => f.Source == TextSource.Uia).ToList();
		List<TextFragment> list = fragments.Where((TextFragment f) => f.Source == TextSource.Ocr).ToList();
		if (list.Count == 0)
		{
			LastOutputCount = fragments.Count;
			LastMergedLines = 0;
			return fragments;
		}
		List<TextFragment> sorted = (from f in list
			orderby f.Bounds.Y, f.Bounds.X
			select f).ToList();
		List<VisualLine> lines = BuildVisualLines(sorted);
		List<Paragraph> list2 = BuildParagraphs(lines);
		List<TextFragment> list3 = new List<TextFragment>();
		int num = 0;
		foreach (Paragraph item in list2)
		{
			if (item.Lines.Count == 1)
			{
				list3.AddRange(item.Lines[0].Fragments);
				continue;
			}
			TextFragment textFragment = MergeParagraphLines(item, sessionId);
			if (textFragment != null)
			{
				list3.Add(textFragment);
				num += item.Lines.Count;
				continue;
			}
			foreach (VisualLine line in item.Lines)
			{
				list3.AddRange(line.Fragments);
			}
		}
		LastMergedLines = num;
		List<TextFragment> list4 = new List<TextFragment>(collection);
		list4.AddRange(list3);
		LastOutputCount = list4.Count;
		return list4;
	}

	private List<VisualLine> BuildVisualLines(List<TextFragment> sorted)
	{
		List<VisualLine> list = new List<VisualLine>();
		foreach (TextFragment item in sorted)
		{
			bool flag = false;
			foreach (VisualLine item2 in list)
			{
				if (IsSameVisualLine(item2, item))
				{
					item2.Fragments.Add(item);
					item2.Bounds = UnionRect(item2.Bounds, item.Bounds);
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				list.Add(new VisualLine
				{
					Fragments = { item },
					Bounds = item.Bounds
				});
			}
		}
		foreach (VisualLine item3 in list)
		{
			item3.Fragments.Sort((TextFragment a, TextFragment b) => a.Bounds.X.CompareTo(b.Bounds.X));
			item3.Text = string.Join(" ", item3.Fragments.Select((TextFragment f) => f.Text.Trim()));
			item3.LineHeight = item3.Bounds.Height;
			item3.Left = item3.Bounds.X;
			item3.Right = item3.Bounds.X + item3.Bounds.Width;
			item3.Top = item3.Bounds.Y;
			item3.Bottom = item3.Bounds.Y + item3.Bounds.Height;
			TextFragment textFragment = item3.Fragments.OrderByDescending((TextFragment f) => f.Confidence).First();
			item3.DetectedLanguage = textFragment.DetectedLanguage;
			item3.MaxConfidence = item3.Fragments.Max((TextFragment f) => f.Confidence);
		}
		list.Sort((VisualLine a, VisualLine b) => a.Top.CompareTo(b.Top));
		return list;
	}

	private static bool IsSameVisualLine(VisualLine line, TextFragment frag)
	{
		double num = line.Bounds.Y + line.Bounds.Height / 2.0;
		double num2 = frag.Bounds.Y + frag.Bounds.Height / 2.0;
		double num3 = Math.Max(line.Bounds.Height, frag.Bounds.Height) * 0.6;
		return Math.Abs(num - num2) <= num3;
	}

	private List<Paragraph> BuildParagraphs(List<VisualLine> lines)
	{
		List<Paragraph> list = new List<Paragraph>();
		foreach (VisualLine line in lines)
		{
			bool flag = false;
			if (list.Count > 0)
			{
				Paragraph paragraph = list[list.Count - 1];
				List<VisualLine> lines2 = paragraph.Lines;
				VisualLine prevLine = lines2[lines2.Count - 1];
				if (paragraph.Lines.Count < 40 && CanMergeIntoParagraph(prevLine, line, paragraph))
				{
					paragraph.Lines.Add(line);
					flag = true;
				}
			}
			if (!flag)
			{
				list.Add(new Paragraph
				{
					Lines = { line }
				});
			}
		}
		return list;
	}

	private static bool CanMergeIntoParagraph(VisualLine prevLine, VisualLine curLine, Paragraph paragraph)
	{
		double num = curLine.Top - prevLine.Bottom;
		double num2 = (prevLine.LineHeight + curLine.LineHeight) / 2.0;
		if (num2 <= 0.0)
		{
			num2 = 16.0;
		}
		if (num < (0.0 - num2) * 0.5 || num > num2 * 1.6)
		{
			return false;
		}
		double num3 = Math.Max(prevLine.LineHeight, curLine.LineHeight);
		double num4 = Math.Abs(prevLine.LineHeight - curLine.LineHeight);
		if (num3 > 0.0 && num4 / num3 > 0.45)
		{
			return false;
		}
		double num5 = Math.Max(prevLine.Left, curLine.Left);
		double num6 = Math.Min(prevLine.Right, curLine.Right) - num5;
		double num7 = Math.Min(prevLine.Right - prevLine.Left, curLine.Right - curLine.Left);
		if (num7 > 0.0 && num6 / num7 < 0.3)
		{
			return false;
		}
		if (Math.Abs(curLine.Left - prevLine.Left) > 30.0)
		{
			if (curLine.Left < prevLine.Left - 30.0)
			{
				return false;
			}
			if (curLine.Left - prevLine.Left > num2 * 3.0)
			{
				return false;
			}
		}
		double top = paragraph.Lines[0].Top;
		double num8 = curLine.Bottom - top;
		double num9 = paragraph.Lines.Average((VisualLine l) => l.LineHeight);
		int num10 = 30;
		if (num8 > num9 * (double)num10)
		{
			return false;
		}
		return true;
	}

	private static TextFragment? MergeParagraphLines(Paragraph paragraph, string sessionId)
	{
		List<VisualLine> lines = paragraph.Lines;
		if (lines.Count == 0)
		{
			return null;
		}
		string text = string.Join(" ", lines.Select((VisualLine l) => l.Text.Trim())).Replace("  ", " ").Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		double num = lines.Min((VisualLine l) => l.Left);
		double num2 = lines.Min((VisualLine l) => l.Top);
		double num3 = lines.Max((VisualLine l) => l.Right);
		double num4 = lines.Max((VisualLine l) => l.Bottom);
		ScreenRect bounds = new ScreenRect(num, num2, num3 - num, num4 - num2);
		string text2 = TextNormalizer.Normalize(text);
		if (string.IsNullOrEmpty(text2))
		{
			return null;
		}
		if (TextNormalizer.IsUntranslatable(text2))
		{
			return null;
		}
		if (TextNormalizer.IsSimplifiedChinese(text2))
		{
			return null;
		}
		string contentHash = TextNormalizer.ComputeHash(text2);
		string regionId = TextFragment.BuildRegionId(sessionId, contentHash, bounds);
		double confidence = lines.Min((VisualLine l) => l.MaxConfidence);
		return new TextFragment
		{
			ContentHash = contentHash,
			RegionId = regionId,
			Text = text,
			NormalizedText = text2,
			Bounds = bounds,
			Source = TextSource.Ocr,
			Confidence = confidence,
			SessionId = sessionId,
			IsVisible = true,
			DetectedLanguage = lines.FirstOrDefault()?.DetectedLanguage,
			MergedLineCount = lines.Count
		};
	}

	private static ScreenRect UnionRect(ScreenRect a, ScreenRect b)
	{
		double num = Math.Min(a.X, b.X);
		double num2 = Math.Min(a.Y, b.Y);
		double num3 = Math.Max(a.X + a.Width, b.X + b.Width);
		double num4 = Math.Max(a.Y + a.Height, b.Y + b.Height);
		return new ScreenRect(num, num2, num3 - num, num4 - num2);
	}
}
