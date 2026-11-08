using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SteamCN.Core.Pipeline;

public static class TextNormalizer
{
	public static string Normalize(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}
		StringBuilder stringBuilder = new StringBuilder(text.Length);
		bool flag = false;
		foreach (char c in text)
		{
			if (char.IsWhiteSpace(c) || c == '\u00a0')
			{
				if (!flag)
				{
					stringBuilder.Append(' ');
				}
				flag = true;
			}
			else
			{
				stringBuilder.Append(c);
				flag = false;
			}
		}
		return stringBuilder.ToString().Trim();
	}

	public static string ComputeHash(string normalizedText)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)), 0, 16).ToLowerInvariant();
	}

	public static bool IsUntranslatable(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		string text2 = text.Trim();
		if (text2.All((char c) => char.IsDigit(c) || c == '.' || c == ',' || c == ':' || c == '-' || c == '+'))
		{
			return true;
		}
		if (text2.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (text2.Contains('\\') && (text2.Contains(':') || text2.Contains('.')))
		{
			return true;
		}
		if (text2.Length <= 2 && !text2.Any((char c) => c > '\u007f'))
		{
			return true;
		}
		if (text2.All((char c) => char.IsPunctuation(c) || char.IsSymbol(c)))
		{
			return true;
		}
		return false;
	}

	public static bool IsSimplifiedChinese(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		int num = 0;
		int num2 = 0;
		foreach (char c in text)
		{
			if (c >= '一' && c <= '鿿')
			{
				num++;
				num2++;
			}
		}
		if (num == 0)
		{
			return false;
		}
		if (text.Any((char c2) => (c2 >= '\u3040' && c2 <= 'ヿ') || (c2 >= 'ㇰ' && c2 <= 'ㇿ')))
		{
			return false;
		}
		return (double)num / (double)text.Length > 0.3;
	}
}
