namespace SteamCN.Windows;

public struct ScrollDetectResult
{
	public bool IsScrolling { get; init; }

	public double DeltaY { get; init; }

	public double RawDelta { get; init; }

	public double Confidence { get; init; }

	public static ScrollDetectResult None => new ScrollDetectResult
	{
		IsScrolling = false,
		DeltaY = 0.0,
		RawDelta = 0.0,
		Confidence = 0.0
	};
}
