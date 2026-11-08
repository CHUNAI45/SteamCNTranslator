using System;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class TranslationAnchor
{
	public required string AnchorId { get; init; }

	public required string AnchorKey { get; init; }

	public required string PageId { get; init; }

	public required string SourceText { get; init; }

	public required string NormalizedText { get; init; }

	public required string ContentHash { get; init; }

	public ScreenRect SourceBounds { get; set; }

	public string? TranslationText { get; set; }

	public AnchorStatus Status { get; set; }

	public bool IsVisible { get; set; } = true;

	public DateTime LastPositionUpdate { get; set; } = DateTime.UtcNow;

	public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

	public int MissCount { get; set; }

	public required TextSource Source { get; init; }

	public RegionType RegionType { get; set; }

	public string? DetectedLanguage { get; init; }

	public bool IsParagraph { get; init; }

	public string? RegionId { get; set; }

	public static string BuildAnchorKey(string contentHash, ScreenRect bounds, TextSource source)
	{
		int value = (int)(bounds.X / 50.0);
		int value2 = (int)(bounds.Width / 50.0);
		return $"{contentHash}:c{value}:w{value2}:{source}";
	}

	public bool OverlapsWith(ScreenRect other, double overlapRatio = 0.3)
	{
		ScreenRect screenRect = SourceBounds.Intersect(other);
		if (screenRect.IsEmpty)
		{
			return false;
		}
		double num = SourceBounds.Width * SourceBounds.Height;
		if (num <= 0.0)
		{
			return false;
		}
		return screenRect.Width * screenRect.Height / num >= overlapRatio;
	}
}
