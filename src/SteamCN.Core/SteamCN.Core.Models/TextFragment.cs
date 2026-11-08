using System;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class TextFragment
{
	public required string ContentHash { get; init; }

	public required string RegionId { get; init; }

	public required string Text { get; init; }

	public required string NormalizedText { get; init; }

	public required ScreenRect Bounds { get; init; }

	public required TextSource Source { get; init; }

	public double Confidence { get; init; } = 1.0;

	public string? DetectedLanguage { get; set; }

	public bool IsVisible { get; set; } = true;

	public required string SessionId { get; init; }

	public string? AppId { get; set; }

	public bool IsSensitive { get; set; }

	public RegionType RegionType { get; set; }

	public string? FragmentId { get; set; }

	public int MergedLineCount { get; set; } = 1;

	public bool IsParagraph => MergedLineCount > 1;

	public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

	public static string BuildRegionId(string sessionId, string contentHash, ScreenRect bounds, int occurrence = 0)
	{
		int value = (int)(bounds.X / 5.0);
		int value2 = (int)(bounds.Y / 5.0);
		return $"{sessionId}:{contentHash}:{value}:{value2}:{occurrence}";
	}

	public static string BuildFragmentId(string contentHash, ScreenRect bounds)
	{
		int value = (int)(bounds.X / 50.0);
		int value2 = (int)(bounds.Width / 50.0);
		return $"{contentHash}:c{value}:w{value2}";
	}
}
