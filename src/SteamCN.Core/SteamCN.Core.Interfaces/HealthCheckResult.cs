using System;
using System.Collections.Generic;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Interfaces;

public class HealthCheckResult
{
	public required CompatibilityStatus Status { get; init; }

	public bool UiaAvailable { get; init; }

	public bool OcrAvailable { get; init; }

	public bool CaptureAvailable { get; init; }

	public IReadOnlyList<string> OcrLanguages { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
