using System;

namespace SteamCN.Windows;

public class ScrollDetector
{
	private const int SampleCols = 32;

	private const int MaxSearchDelta = 250;

	private const double ScrollThreshold = 8.0;

	private const double MinConfidence = 0.25;

	private float[]? _prevRowProfile;

	private int _prevHeight;

	public ScrollDetectResult LastResult { get; private set; }

	public bool HasPreviousFrame
	{
		get
		{
			if (_prevRowProfile != null)
			{
				return _prevHeight > 0;
			}
			return false;
		}
	}

	public ScrollDetectResult Detect(byte[] pixels, int width, int height)
	{
		if (pixels == null || width <= 0 || height <= 0)
		{
			LastResult = ScrollDetectResult.None;
			return LastResult;
		}
		float[] array = ComputeRowProfile(pixels, width, height);
		if (_prevRowProfile == null || _prevHeight == 0)
		{
			_prevRowProfile = array;
			_prevHeight = height;
			LastResult = ScrollDetectResult.None;
			return LastResult;
		}
		(double delta, double score) tuple = FindBestOffset(array, height, _prevRowProfile, _prevHeight);
		double item = tuple.delta;
		double item2 = tuple.score;
		_prevRowProfile = array;
		_prevHeight = height;
		bool flag = Math.Abs(item) >= 8.0 && item2 >= 0.25;
		return LastResult = new ScrollDetectResult
		{
			IsScrolling = flag,
			DeltaY = (flag ? item : 0.0),
			RawDelta = item,
			Confidence = item2
		};
	}

	public void Reset()
	{
		_prevRowProfile = null;
		_prevHeight = 0;
		LastResult = ScrollDetectResult.None;
	}

	private static float[] ComputeRowProfile(byte[] pixels, int width, int height)
	{
		float[] array = new float[height];
		int num = Math.Max(1, width / 32);
		for (int i = 0; i < height; i++)
		{
			int num2 = i * width * 4;
			float num3 = 0f;
			int num4 = 0;
			for (int j = 0; j < width; j += num)
			{
				int num5 = num2 + j * 4;
				if (num5 + 2 >= pixels.Length)
				{
					break;
				}
				num3 += (float)(int)pixels[num5] * 0.11f + (float)(int)pixels[num5 + 1] * 0.59f + (float)(int)pixels[num5 + 2] * 0.3f;
				num4++;
			}
			if (num4 == 0)
			{
				array[i] = 0f;
				continue;
			}
			float num6 = num3 / (float)num4;
			float num7 = 0f;
			for (int k = 0; k < width; k += num)
			{
				int num8 = num2 + k * 4;
				if (num8 + 2 >= pixels.Length)
				{
					break;
				}
				float num9 = (float)(int)pixels[num8] * 0.11f + (float)(int)pixels[num8 + 1] * 0.59f + (float)(int)pixels[num8 + 2] * 0.3f - num6;
				num7 += num9 * num9;
			}
			array[i] = (float)Math.Sqrt(num7 / (float)num4);
		}
		return array;
	}

	private static (double delta, double score) FindBestOffset(float[] current, int curHeight, float[] prev, int prevHeight)
	{
		int num = Math.Min(curHeight, prevHeight);
		if (num < 20)
		{
			return (delta: 0.0, score: 0.0);
		}
		double num2 = 0.0;
		double num3 = 0.0;
		for (int i = 0; i < num; i++)
		{
			num2 += (double)current[i];
		}
		num2 /= (double)num;
		for (int j = 0; j < num; j++)
		{
			num3 += ((double)current[j] - num2) * ((double)current[j] - num2);
		}
		num3 = Math.Sqrt(num3 / (double)num);
		if (num3 < 1.0)
		{
			return (delta: 0.0, score: 0.0);
		}
		double num4 = -1.0;
		int num5 = 0;
		int num6 = Math.Min(250, num / 3);
		for (int k = -num6; k <= num6; k += 2)
		{
			double num7 = ComputeNccScore(current, prev, k, num, num2, num3);
			if (num7 > num4)
			{
				num4 = num7;
				num5 = k;
			}
		}
		for (int l = num5 - 1; l <= num5 + 1; l++)
		{
			if (l >= -num6 && l <= num6 && l != num5)
			{
				double num8 = ComputeNccScore(current, prev, l, num, num2, num3);
				if (num8 > num4)
				{
					num4 = num8;
					num5 = l;
				}
			}
		}
		return (delta: num5, score: Math.Max(0.0, num4));
	}

	private static double ComputeNccScore(float[] current, float[] prev, int delta, int minH, double curMean, double curStd)
	{
		int num = Math.Max(0, delta);
		int num2 = Math.Max(0, -delta);
		int num3 = minH - Math.Abs(delta);
		if (num3 < minH / 3)
		{
			return -1.0;
		}
		double num4 = 0.0;
		for (int i = 0; i < num3; i++)
		{
			num4 += (double)prev[num2 + i];
		}
		num4 /= (double)num3;
		double num5 = 0.0;
		for (int j = 0; j < num3; j++)
		{
			num5 += ((double)prev[num2 + j] - num4) * ((double)prev[num2 + j] - num4);
		}
		num5 = Math.Sqrt(num5 / (double)num3);
		if (num5 < 1.0)
		{
			return -1.0;
		}
		double num6 = 0.0;
		for (int k = 0; k < num3; k++)
		{
			double num7 = ((double)current[num + k] - curMean) / curStd;
			double num8 = ((double)prev[num2 + k] - num4) / num5;
			num6 += num7 * num8;
		}
		return num6 / (double)num3;
	}
}
