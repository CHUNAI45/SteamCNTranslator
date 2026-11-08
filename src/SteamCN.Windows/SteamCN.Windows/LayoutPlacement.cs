using System.Windows;

namespace SteamCN.Windows;

public class LayoutPlacement
{
	public double X { get; init; }

	public double Y { get; init; }

	public LayoutMethod Method { get; init; }

	public double BackgroundAlpha { get; init; }

	public double FontSize { get; init; }

	public double WrapWidth { get; init; }

	public Rect SourceRect { get; init; }
}
