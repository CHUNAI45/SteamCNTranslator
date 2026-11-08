using System.Windows;

namespace SteamCN.Windows;

internal class PlacedRect
{
	public required Rect Rect { get; init; }

	public required string RegionId { get; init; }

	public required LayoutMethod Method { get; init; }
}
