using System;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class TranslationResult
{
	public required string NormalizedSource { get; init; }

	public required string SourceHash { get; init; }

	public string RegionId { get; init; } = string.Empty;

	public string? AnchorId { get; set; }

	public required string TranslatedText { get; init; }

	public required string SourceLanguage { get; init; }

	public required string TargetLanguage { get; init; }

	public required string Provider { get; init; }

	public bool FromCache { get; init; }

	public bool Success { get; init; } = true;

	public string? ErrorMessage { get; init; }

	public int CharacterCount { get; init; }

	public ScreenRect SourceBounds { get; set; }

	public required string SessionId { get; init; }

	public PageArea Area { get; init; }

	public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
