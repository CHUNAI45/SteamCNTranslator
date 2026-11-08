using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public class TranslationLayoutEngine
{
	public double BaseFontSize { get; set; } = 14.0;

	public double FontScale { get; set; } = 1.0;

	public double MaxTranslationWidth { get; set; } = 420.0;

	public double OverlayOpacity { get; set; } = 0.82;

	public double SideOpacity { get; set; } = 0.72;

	public double Gap { get; set; } = 6.0;

	public double ScaledFontSize => BaseFontSize * Math.Max(0.5, Math.Min(2.0, FontScale));

	public LayoutPlacement Calculate(ScreenRect fragmentBounds, string originalText, string translatedText, double viewportWidth, double viewportHeight, double windowScreenLeft, double windowScreenTop, double dpiScale, LayoutMode mode)
	{
		double scaledFontSize = ScaledFontSize;
		double num = (fragmentBounds.X - windowScreenLeft) / dpiScale;
		double num2 = (fragmentBounds.Y - windowScreenTop) / dpiScale;
		double num3 = fragmentBounds.Width / dpiScale;
		double num4 = fragmentBounds.Height / dpiScale;
		Rect sourceRect = new Rect(num, num2, num3, num4);
		var (estW, estH) = EstimateTextSize(translatedText, scaledFontSize, MaxTranslationWidth);
		return mode switch
		{
			LayoutMode.SideBySide => CalculateSideBySide(num, num2, num3, num4, estW, estH, viewportWidth, viewportHeight, scaledFontSize, sourceRect), 
			LayoutMode.Overlay => CalculateOverlay(num, num2, num3, num4, translatedText, viewportWidth, viewportHeight, scaledFontSize, sourceRect), 
			LayoutMode.Adaptive => CalculateAdaptive(num, num2, num3, num4, originalText, translatedText, estW, estH, viewportWidth, viewportHeight, scaledFontSize, sourceRect), 
			_ => CalculateAdaptive(num, num2, num3, num4, originalText, translatedText, estW, estH, viewportWidth, viewportHeight, scaledFontSize, sourceRect), 
		};
	}

	public Dictionary<string, LayoutPlacement> CalculateBatch(IReadOnlyList<TextFragment> fragments, IReadOnlyDictionary<string, TranslationResult> resultsByRegionId, double viewportWidth, double viewportHeight, double windowScreenLeft, double windowScreenTop, double dpiScale, LayoutMode mode, IReadOnlyList<(Rect Rect, string AnchorId)>? existingObstacles = null)
	{
		Dictionary<string, LayoutPlacement> dictionary = new Dictionary<string, LayoutPlacement>();
		List<PlacedRect> list = new List<PlacedRect>();
		if (existingObstacles != null)
		{
			foreach (var (rect, regionId) in existingObstacles)
			{
				if (rect.Width > 0.0 && rect.Height > 0.0)
				{
					list.Add(new PlacedRect
					{
						Rect = rect,
						RegionId = regionId,
						Method = LayoutMethod.EdgeFloat
					});
				}
			}
		}
		foreach (TextFragment item3 in (from f in fragments
			where resultsByRegionId.ContainsKey(f.RegionId)
			orderby GetLayoutPriority(f.RegionType), f.Bounds.Y, f.Bounds.X
			select f).ToList())
		{
			if (resultsByRegionId.TryGetValue(item3.RegionId, out TranslationResult value) && !string.IsNullOrEmpty(value.TranslatedText) && !(item3.Bounds.Width < 5.0) && !(item3.Bounds.Height < 5.0))
			{
				LayoutPlacement layoutPlacement = Calculate(item3.Bounds, item3.Text, value.TranslatedText, viewportWidth, viewportHeight, windowScreenLeft, windowScreenTop, dpiScale, mode);
				(double Width, double Height) tuple2 = EstimateTextSize(value.TranslatedText, layoutPlacement.FontSize, layoutPlacement.WrapWidth);
				double item = tuple2.Width;
				double item2 = tuple2.Height;
				Rect placementRect = new Rect(layoutPlacement.X, layoutPlacement.Y, Math.Max(item, 40.0), item2);
				LayoutPlacement layoutPlacement2 = ResolveCollision(layoutPlacement, placementRect, item3, value.TranslatedText, list, viewportWidth, viewportHeight, windowScreenLeft, windowScreenTop, dpiScale, mode);
				if (layoutPlacement2 != null)
				{
					dictionary[item3.RegionId] = layoutPlacement2;
					var (val, height) = EstimateTextSize(value.TranslatedText, layoutPlacement2.FontSize, layoutPlacement2.WrapWidth);
					list.Add(new PlacedRect
					{
						Rect = new Rect(layoutPlacement2.X, layoutPlacement2.Y, Math.Max(val, 40.0), height),
						RegionId = item3.RegionId,
						Method = layoutPlacement2.Method
					});
				}
			}
		}
		return dictionary;
	}

	private static int GetLayoutPriority(RegionType type)
	{
		return type switch
		{
			RegionType.PageTitle => 0, 
			RegionType.ItemTitle => 1, 
			RegionType.DescriptionBody => 2, 
			RegionType.CommentBody => 3, 
			RegionType.CardTitle => 4, 
			RegionType.CardBody => 5, 
			RegionType.CommentAuthor => 6, 
			RegionType.Navigation => 7, 
			RegionType.Other => 8, 
			RegionType.Tag => 9, 
			RegionType.Button => 10, 
			_ => 5, 
		};
	}

	private LayoutPlacement? ResolveCollision(LayoutPlacement placement, Rect placementRect, TextFragment fragment, string translatedText, List<PlacedRect> placedRects, double vpW, double vpH, double windowScreenLeft, double windowScreenTop, double dpiScale, LayoutMode mode)
	{
		if (!HasCollision(placementRect, placedRects))
		{
			return placement;
		}
		double fontSize = placement.FontSize;
		double num = (fragment.Bounds.X - windowScreenLeft) / dpiScale;
		double num2 = (fragment.Bounds.Y - windowScreenTop) / dpiScale;
		double num3 = fragment.Bounds.Width / dpiScale;
		double num4 = fragment.Bounds.Height / dpiScale;
		Rect sourceRect = new Rect(num, num2, num3, num4);
		for (int i = 1; i <= 3; i++)
		{
			double num5 = placement.Y + placementRect.Height * (double)i + Gap;
			if (num5 + placementRect.Height > vpH - 4.0)
			{
				break;
			}
			if (!HasCollision(new Rect(placement.X, num5, placementRect.Width, placementRect.Height), placedRects))
			{
				double? y = num5;
				return ClonePlacement(placement, null, y);
			}
		}
		for (int j = 1; j <= 2; j++)
		{
			double num6 = placement.X + placementRect.Width * (double)j + Gap;
			if (num6 + placementRect.Width > vpW - 4.0)
			{
				break;
			}
			if (!HasCollision(new Rect(num6, placement.Y, placementRect.Width, placementRect.Height), placedRects))
			{
				return ClonePlacement(placement, num6);
			}
		}
		if (placement.Method == LayoutMethod.Right)
		{
			LayoutPlacement layoutPlacement = CalculateSideBySide(num, num2, num3, num4, 0.0, placementRect.Height, vpW, vpH, fontSize, sourceRect);
			if (layoutPlacement.Method == LayoutMethod.Below)
			{
				var (val, height) = EstimateTextSize(translatedText, layoutPlacement.FontSize, layoutPlacement.WrapWidth);
				if (!HasCollision(new Rect(layoutPlacement.X, layoutPlacement.Y, Math.Max(val, 40.0), height), placedRects))
				{
					return layoutPlacement;
				}
			}
		}
		if (placement.Method != LayoutMethod.Overlay && fragment.RegionType != RegionType.Button)
		{
			LayoutPlacement layoutPlacement2 = CalculateOverlay(num, num2, num3, num4, translatedText, vpW, vpH, fontSize, sourceRect);
			var (val2, height2) = EstimateTextSize(translatedText, layoutPlacement2.FontSize, layoutPlacement2.WrapWidth);
			if (!HasCollision(new Rect(layoutPlacement2.X, layoutPlacement2.Y, Math.Max(val2, 40.0), height2), placedRects))
			{
				return layoutPlacement2;
			}
		}
		double num7 = vpH - placementRect.Height - 8.0;
		if (num7 > 4.0 && !HasCollision(new Rect(8.0, num7, Math.Min(placementRect.Width, vpW - 16.0), placementRect.Height), placedRects))
		{
			return new LayoutPlacement
			{
				X = 8.0,
				Y = num7,
				Method = LayoutMethod.EdgeFloat,
				BackgroundAlpha = SideOpacity + 0.1,
				FontSize = fontSize,
				WrapWidth = Math.Min(placement.WrapWidth, vpW - 16.0),
				SourceRect = sourceRect
			};
		}
		return null;
	}

	private static bool HasCollision(Rect rect, List<PlacedRect> placedRects)
	{
		Rect rect2 = new Rect(rect.X - 2.0, rect.Y - 2.0, rect.Width + 4.0, rect.Height + 4.0);
		foreach (PlacedRect placedRect in placedRects)
		{
			if (rect2.IntersectsWith(placedRect.Rect))
			{
				return true;
			}
		}
		return false;
	}

	private static LayoutPlacement ClonePlacement(LayoutPlacement src, double? x = null, double? y = null)
	{
		return new LayoutPlacement
		{
			X = (x ?? src.X),
			Y = (y ?? src.Y),
			Method = src.Method,
			BackgroundAlpha = src.BackgroundAlpha,
			FontSize = src.FontSize,
			WrapWidth = src.WrapWidth,
			SourceRect = src.SourceRect
		};
	}

	private LayoutPlacement CalculateSideBySide(double fragX, double fragY, double fragW, double fragH, double estW, double estH, double vpW, double vpH, double fontSize, Rect sourceRect)
	{
		double num = vpW - (fragX + fragW) - Gap;
		double num2 = vpH - (fragY + fragH) - Gap;
		if (num >= Math.Min(60.0, estW) || num >= 80.0)
		{
			double wrapWidth = Math.Min(MaxTranslationWidth, Math.Max(60.0, num));
			return new LayoutPlacement
			{
				X = fragX + fragW + Gap,
				Y = fragY,
				Method = LayoutMethod.Right,
				BackgroundAlpha = SideOpacity,
				FontSize = fontSize,
				WrapWidth = wrapWidth,
				SourceRect = sourceRect
			};
		}
		if (num2 >= Math.Min(estH, 24.0))
		{
			double wrapWidth2 = Math.Min(MaxTranslationWidth, Math.Min(Math.Max(fragW, 120.0), vpW - fragX - 4.0));
			return new LayoutPlacement
			{
				X = fragX,
				Y = fragY + fragH + Gap,
				Method = LayoutMethod.Below,
				BackgroundAlpha = SideOpacity,
				FontSize = fontSize,
				WrapWidth = wrapWidth2,
				SourceRect = sourceRect
			};
		}
		double num3 = fragY - estH - Gap;
		if (num3 < 4.0)
		{
			num3 = Math.Max(4.0, fragY);
		}
		double wrapWidth3 = Math.Min(MaxTranslationWidth, vpW - fragX - 8.0);
		return new LayoutPlacement
		{
			X = fragX,
			Y = num3,
			Method = LayoutMethod.EdgeFloat,
			BackgroundAlpha = SideOpacity + 0.1,
			FontSize = fontSize,
			WrapWidth = wrapWidth3,
			SourceRect = sourceRect
		};
	}

	private LayoutPlacement CalculateOverlay(double fragX, double fragY, double fragW, double fragH, string translatedText, double vpW, double vpH, double fontSize, Rect sourceRect)
	{
		double val = Math.Min(MaxTranslationWidth, Math.Max(fragW + 16.0, 100.0));
		val = Math.Min(val, vpW - fragX - 4.0);
		val = Math.Max(val, 60.0);
		return new LayoutPlacement
		{
			X = fragX,
			Y = fragY,
			Method = LayoutMethod.Overlay,
			BackgroundAlpha = OverlayOpacity,
			FontSize = fontSize,
			WrapWidth = val,
			SourceRect = sourceRect
		};
	}

	private LayoutPlacement CalculateAdaptive(double fragX, double fragY, double fragW, double fragH, string originalText, string translatedText, double estW, double estH, double vpW, double vpH, double fontSize, Rect sourceRect)
	{
		bool flag = originalText.Length <= 50 && fragH <= 32.0;
		bool num = fragH < 28.0 && fragW < 140.0 && originalText.Length <= 25;
		bool flag2 = fragW >= 180.0;
		double num2 = vpW - (fragX + fragW) - Gap;
		_ = Gap;
		if (num)
		{
			if (num2 >= 60.0)
			{
				double wrapWidth = Math.Min(MaxTranslationWidth, Math.Max(60.0, num2));
				return new LayoutPlacement
				{
					X = fragX + fragW + Gap,
					Y = fragY,
					Method = LayoutMethod.Right,
					BackgroundAlpha = SideOpacity,
					FontSize = fontSize,
					WrapWidth = wrapWidth,
					SourceRect = sourceRect
				};
			}
			double wrapWidth2 = Math.Min(MaxTranslationWidth, Math.Min(Math.Max(fragW, 100.0), vpW - fragX - 4.0));
			return new LayoutPlacement
			{
				X = fragX,
				Y = fragY + fragH + Gap,
				Method = LayoutMethod.Below,
				BackgroundAlpha = SideOpacity,
				FontSize = fontSize,
				WrapWidth = wrapWidth2,
				SourceRect = sourceRect
			};
		}
		if (flag)
		{
			return CalculateSideBySide(fragX, fragY, fragW, fragH, estW, estH, vpW, vpH, fontSize, sourceRect);
		}
		if (flag2 && fragH >= 18.0)
		{
			return CalculateOverlay(fragX, fragY, fragW, fragH, translatedText, vpW, vpH, fontSize, sourceRect);
		}
		if (num2 >= Math.Min(120.0, estW))
		{
			double wrapWidth3 = Math.Min(MaxTranslationWidth, Math.Max(80.0, num2));
			return new LayoutPlacement
			{
				X = fragX + fragW + Gap,
				Y = fragY,
				Method = LayoutMethod.Right,
				BackgroundAlpha = SideOpacity,
				FontSize = fontSize,
				WrapWidth = wrapWidth3,
				SourceRect = sourceRect
			};
		}
		return CalculateOverlay(fragX, fragY, fragW, fragH, translatedText, vpW, vpH, fontSize, sourceRect);
	}

	public static (double Width, double Height) EstimateTextSize(string text, double fontSize, double wrapWidth)
	{
		if (string.IsNullOrEmpty(text))
		{
			return (Width: 0.0, Height: fontSize * 1.35);
		}
		int num = 0;
		int num2 = 0;
		string text2 = text;
		foreach (char c in text2)
		{
			if ((c >= '一' && c <= '鿿') || (c >= '\u3000' && c <= '〿') || (c >= '\uff00' && c <= '\uffef'))
			{
				num++;
			}
			else if (c != '\n')
			{
				num2++;
			}
		}
		double num3 = (double)num * fontSize + (double)num2 * fontSize * 0.55;
		double num4 = 1.0;
		double num5 = Math.Max(40.0, wrapWidth);
		if (num3 > num5)
		{
			num4 = Math.Ceiling(num3 / num5);
		}
		int num6 = 0;
		text2 = text;
		for (int i = 0; i < text2.Length; i++)
		{
			if (text2[i] == '\n')
			{
				num6++;
			}
		}
		num4 += (double)num6;
		double num7 = fontSize * 1.4;
		return (Width: Math.Min(num3, num5), Height: num4 * num7);
	}
}
