using System;

namespace SteamCN.Core.Models;

public class SteamWindowInfo
{
	public nint Handle { get; init; }

	public int ProcessId { get; init; }

	public required string ProcessName { get; init; }

	public required string ProcessPath { get; init; }

	public string? PageUrl { get; set; }

	public string Title { get; set; } = string.Empty;

	public ScreenRect Bounds { get; set; }

	public double DpiScale { get; set; } = 1.0;

	public bool IsVisible { get; set; } = true;

	public bool IsMinimized { get; set; }

	public bool IsForeground { get; set; }

	public string CurrentSessionId { get; set; } = Guid.NewGuid().ToString("N");

	public DateTimeOffset BoundAt { get; init; } = DateTimeOffset.UtcNow;
}

