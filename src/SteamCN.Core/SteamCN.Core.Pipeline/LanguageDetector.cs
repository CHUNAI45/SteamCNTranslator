using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SteamCN.Core.Pipeline;

public static class LanguageDetector
{
	private static readonly HashSet<string> EnglishMarkers = new(StringComparer.OrdinalIgnoreCase)
	{
		"the", "this", "these", "those", "with", "without", "your", "you", "should", "could",
		"would", "which", "their", "they", "from", "have", "has", "been", "will", "there", "when", "what"
	};

	private static readonly Regex Words = new(@"[\p{L}\p{M}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static string? Detect(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		int latin = 0, han = 0, kana = 0, hangul = 0, cyrillic = 0, greek = 0;
		bool ukrainian = false;
		foreach (char c in text)
		{
			if (!char.IsLetter(c)) continue;
			if (c is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff' or >= '\uff66' and <= '\uff9d') kana++;
			else if (c is >= '\uac00' and <= '\ud7a3' or >= '\u1100' and <= '\u11ff' or >= '\u3131' and <= '\u318e') hangul++;
			else if (c is >= '\u3400' and <= '\u9fff') han++;
			else if (c is >= '\u0400' and <= '\u052f')
			{
				cyrillic++;
				ukrainian |= c is 'і' or 'І' or 'ї' or 'Ї' or 'є' or 'Є' or 'ґ' or 'Ґ';
			}
			else if (c is >= '\u0370' and <= '\u03ff' or >= '\u1f00' and <= '\u1fff') greek++;
			else if (c <= '\u024f' || c is >= '\u1e00' and <= '\u1eff') latin++;
		}
		if (hangul > kana) return "ko";
		if (kana > 0) return "ja";
		if (han > 0 && han * 3 > latin + cyrillic + greek) return "zh";
		if (cyrillic > 0 && cyrillic >= latin && cyrillic >= greek) return ukrainian ? "uk" : "ru";
		if (greek > 0 && greek >= latin) return "el";
		if (latin == 0) return null;
		string? firstMarker = null;
		foreach (Match word in Words.Matches(text))
		{
			if (!EnglishMarkers.Contains(word.Value)) continue;
			if (firstMarker == null) firstMarker = word.Value;
			else if (!word.Value.Equals(firstMarker, StringComparison.OrdinalIgnoreCase)) return "en";
		}
		return "auto";
	}

	public static bool NeedsTranslation(string? detectedLanguage, string targetLanguage) =>
		!string.Equals(detectedLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase) &&
		!string.Equals(detectedLanguage, "zh", StringComparison.OrdinalIgnoreCase) &&
		!string.Equals(detectedLanguage, "zh-Hans", StringComparison.OrdinalIgnoreCase);
}
