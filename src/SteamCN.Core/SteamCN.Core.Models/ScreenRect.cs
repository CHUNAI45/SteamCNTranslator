using System;

namespace SteamCN.Core.Models;

public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
	public double Right => X + Width;

	public double Bottom => Y + Height;

	public bool IsEmpty
	{
		get
		{
			if (!(Width <= 0.0))
			{
				return Height <= 0.0;
			}
			return true;
		}
	}

	public bool IntersectsWith(ScreenRect other)
	{
		if (other.X < Right && X < other.Right && other.Y < Bottom)
		{
			return Y < other.Bottom;
		}
		return false;
	}

	public ScreenRect Intersect(ScreenRect other)
	{
		double num = Math.Max(X, other.X);
		double num2 = Math.Max(Y, other.Y);
		double num3 = Math.Min(Right, other.Right);
		double num4 = Math.Min(Bottom, other.Bottom);
		if (num3 <= num || num4 <= num2)
		{
			return default;
		}
		return new ScreenRect(num, num2, num3 - num, num4 - num2);
	}

	public bool Contains(double px, double py)
	{
		if (px >= X && px < Right && py >= Y)
		{
			return py < Bottom;
		}
		return false;
	}
}
