namespace SteamCN.Core.Models;

public class FloatingBallSettings
{
	public bool Enabled { get; set; } = true;

	public double BallX { get; set; } = -1.0;

	public double BallY { get; set; } = -1.0;

	public double Opacity { get; set; } = 0.85;

	public bool SnapToEdge { get; set; } = true;
}
