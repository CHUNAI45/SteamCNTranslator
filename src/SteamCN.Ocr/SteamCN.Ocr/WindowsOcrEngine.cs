using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SteamCN.Ocr;

public class WindowsOcrEngine : IOcrEngine
{
	private OcrEngine? _engine;

	private readonly string? _preferredLanguage;

	private readonly Dictionary<string, OcrEngine> _recognizers = new(StringComparer.OrdinalIgnoreCase);

	private readonly object _lock = new object();

	public string Name => "Windows.Media.Ocr";

	public bool IsAvailable
	{
		get
		{
			lock (_lock)
			{
				EnsureEngine();
				return _engine != null;
			}
		}
	}

	public IReadOnlyList<string> AvailableLanguages
	{
		get
		{
			try
			{
				return OcrEngine.AvailableRecognizerLanguages.Select((Language l) => l.LanguageTag).ToList();
			}
			catch
			{
				return Array.Empty<string>();
			}
		}
	}

	public WindowsOcrEngine(string? preferredLanguage = null)
	{
		_preferredLanguage = preferredLanguage;
	}

	public async Task<IReadOnlyList<TextFragment>> RecognizeAsync(byte[] imageData, int width, int height, string? language = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		OcrEngine? engine;
		lock (_lock)
		{
			if (language == null)
			{
				EnsureEngine();
				engine = _engine;
			}
			else if (!_recognizers.TryGetValue(language, out engine))
			{
				engine = OcrEngine.TryCreateFromLanguage(new Language(language));
				if (engine != null) _recognizers.Add(language, engine);
			}
		}
		if (engine == null)
		{
			return Array.Empty<TextFragment>();
		}
		try
		{
			using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(imageData.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
			OcrResult ocrResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			DiagLog($"OCR: lang={engine.RecognizerLanguage.LanguageTag} size={width}x{height} lines={ocrResult.Lines.Count}");
			List<TextFragment> list = ConvertToTextFragments(ocrResult, width, height);
			DiagLog($"OCR: after-filter fragments={list.Count}");
			return list;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			DiagLog("OCR ERROR: " + ex2.GetType().Name + ": " + ex2.Message);
			return Array.Empty<TextFragment>();
		}
	}

	private void EnsureEngine()
	{
		if (_engine != null)
		{
			return;
		}
		try
		{
			if (!string.IsNullOrEmpty(_preferredLanguage))
			{
				Language language = new Language(_preferredLanguage);
				_engine = OcrEngine.TryCreateFromLanguage(language);
				DiagLog($"OCR engine: preferred={_preferredLanguage} created={_engine != null}");
			}
			if (_engine == null)
			{
				List<Language> list = OcrEngine.AvailableRecognizerLanguages.ToList();
				DiagLog("OCR engine: available=[" + string.Join(",", list.Select((Language l) => l.LanguageTag)) + "]");
				Language language2 = list.FirstOrDefault((Language l) => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
				ref OcrEngine? engine = ref _engine;
				OcrEngine? ocrEngine;
				if (language2 != null)
				{
					ocrEngine = OcrEngine.TryCreateFromLanguage(language2);
				}
				else
				{
					ocrEngine = ((list.Count > 0) ? OcrEngine.TryCreateFromLanguage(list[0]) : null);
				}
				engine = ocrEngine;
				DiagLog($"OCR engine: fallback created={_engine != null} lang={_engine?.RecognizerLanguage.LanguageTag}");
			}
		}
		catch (Exception ex)
		{
			DiagLog("OCR engine ERROR: " + ex.Message);
			_engine = null;
		}
	}

	private static void DiagLog(string message)
	{
		try
		{
			File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator", "diag.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [OCR] {message}\n");
		}
		catch
		{
		}
	}

	private List<TextFragment> ConvertToTextFragments(OcrResult result, int imageWidth, int imageHeight)
	{
		List<TextFragment> list = new List<TextFragment>();
		string sessionId = Guid.NewGuid().ToString("N");
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		foreach (OcrLine line in result.Lines)
		{
			string text = line.Text?.Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				num++;
				continue;
			}
			string text2 = TextNormalizer.Normalize(text);
			if (string.IsNullOrEmpty(text2))
			{
				num2++;
				continue;
			}
			if (TextNormalizer.IsUntranslatable(text2))
			{
				num3++;
				continue;
			}
			if (TextNormalizer.IsSimplifiedChinese(text2))
			{
				num4++;
				continue;
			}
			double num5 = double.MaxValue;
			double num6 = double.MaxValue;
			double num7 = double.MinValue;
			double num8 = double.MinValue;
			foreach (OcrWord word in line.Words)
			{
				Rect boundingRect = word.BoundingRect;
				if (boundingRect.X < num5)
				{
					num5 = boundingRect.X;
				}
				if (boundingRect.Y < num6)
				{
					num6 = boundingRect.Y;
				}
				if (boundingRect.X + boundingRect.Width > num7)
				{
					num7 = boundingRect.X + boundingRect.Width;
				}
				if (boundingRect.Y + boundingRect.Height > num8)
				{
					num8 = boundingRect.Y + boundingRect.Height;
				}
			}
			if (num5 != double.MaxValue)
			{
				ScreenRect bounds = new ScreenRect(num5, num6, num7 - num5, num8 - num6);
				double confidence = EstimateConfidence(text, bounds, imageWidth, imageHeight);
				TextFragment item = new TextFragment
				{
					ContentHash = TextNormalizer.ComputeHash(text2),
					RegionId = TextFragment.BuildRegionId(sessionId, TextNormalizer.ComputeHash(text2), bounds),
					Text = text,
					NormalizedText = text2,
					Bounds = bounds,
					Source = TextSource.Ocr,
					Confidence = confidence,
					SessionId = sessionId,
					IsVisible = true,
					DetectedLanguage = LanguageDetector.Detect(text2)
				};
				list.Add(item);
			}
		}
		DiagLog($"OCR filter: total={result.Lines.Count} kept={list.Count} empty={num} norm={num2} untrans={num3} chinese={num4}");
		return list;
	}

	private static double EstimateConfidence(string text, ScreenRect bounds, int imageWidth, int imageHeight)
	{
		double num = 0.8;
		if (text.Length >= 10)
		{
			num += 0.1;
		}
		else if (text.Length <= 3)
		{
			num -= 0.2;
		}
		if (bounds.Height >= 8.0 && bounds.Height <= 60.0)
		{
			num += 0.05;
		}
		if (bounds.Width < 10.0)
		{
			num -= 0.1;
		}
		return Math.Clamp(num, 0.1, 1.0);
	}
}
