using System;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class GlossaryTerm
{
	public long Id { get; set; }

	public required string Scope { get; init; }

	public string? AppId { get; set; }

	public required string SourceTerm { get; init; }

	public required string TargetTerm { get; init; }

	public GlossaryAction Action { get; init; }

	public bool CaseSensitive { get; init; }

	public bool Enabled { get; set; } = true;

	public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
