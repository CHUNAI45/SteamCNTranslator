using System;

namespace SteamCN.Core.Models;

public class CachedTranslation
{
	public long Id { get; set; }

	public required string SourceHash { get; init; }

	public required string NormalizedSource { get; init; }

	public required string SourceLanguage { get; init; }

	public required string TargetLanguage { get; init; }

	public required string Provider { get; init; }

	public required string GlossaryVersion { get; init; }

	public string? AppId { get; init; }

	public required string TranslatedText { get; init; }

	public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

	public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;
}
