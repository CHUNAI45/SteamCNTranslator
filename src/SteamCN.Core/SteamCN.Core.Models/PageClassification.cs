using System;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class PageClassification
{
	public required PageArea Area { get; init; }

	public double Confidence { get; init; }

	public required string Evidence { get; init; }

	public string? AppId { get; init; }

	public required string SessionId { get; init; }

	public bool IsConfident => Confidence >= 0.6;

	public DateTimeOffset ClassifiedAt { get; init; } = DateTimeOffset.UtcNow;
}
