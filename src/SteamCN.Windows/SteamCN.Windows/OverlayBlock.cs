using System;
using System.Windows.Controls;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

internal class OverlayBlock
{
	public required Border Border { get; init; }

	public required string AnchorId { get; init; }

	public required ScreenRect SourceBounds { get; set; }

	public string? SourceText { get; init; }

	public required LayoutMethod Method { get; init; }

	public DateTime LastVisible { get; set; } = DateTime.UtcNow;

	public int MissCount { get; set; }
}
