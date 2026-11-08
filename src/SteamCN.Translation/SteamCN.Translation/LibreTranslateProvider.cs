using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Translation;

public class LibreTranslateProvider : ITranslationProvider
{
	private sealed class LibreTranslateRequest
	{
		public List<string> Q { get; set; } = new List<string>();

		public string Source { get; set; } = "";

		public string Target { get; set; } = "";

		public string Format { get; set; } = "text";

		public string? ApiKey { get; set; }
	}

	private sealed class LibreTranslateResponse
	{
		[JsonPropertyName("translatedText")]
		public JsonElement? TranslatedText { get; set; }
	}

	private readonly HttpClient _httpClient;

	private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(2, 2);

	private readonly object _statsLock = new object();

	private string? _url;

	private string? _apiKey;

	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public string Name => "libretranslate";

	public int MaxCharactersPerRequest => 5000;

	public bool IsConfigured => !string.IsNullOrWhiteSpace(_url);

	public long TotalCharactersSubmitted { get; private set; }

	public long TotalRequests { get; private set; }

	public event Action? ConfigurationChanged;

	public LibreTranslateProvider(HttpClient? httpClient = null)
	{
		_httpClient = httpClient ?? new HttpClient();
		_httpClient.Timeout = TimeSpan.FromSeconds(30.0);
	}

	public void Configure(string url, string? apiKey = null)
	{
		_url = (string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/'));
		_apiKey = (string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
		ConfigurationChanged?.Invoke();
	}

	public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default(CancellationToken))
	{
		return (await TranslateBatchAsync(new string[1] { text }, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))[0];
	}

	public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (texts.Count == 0)
		{
			return Array.Empty<TranslationResult>();
		}
		if (!IsConfigured)
		{
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "LibreTranslate 服务地址未配置", TranslationFailureType.InvalidResponse)).ToList();
		}
		List<TranslationResult> allResults = new List<TranslationResult>(texts.Count);
		for (int i = 0; i < texts.Count; i += 10)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			List<string> texts2 = texts.Skip(i).Take(10).ToList();
			allResults.AddRange(await TranslateSingleBatchAsync(texts2, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
		}
		return allResults;
	}

	private async Task<IReadOnlyList<TranslationResult>> TranslateSingleBatchAsync(List<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
	{
		int retryCount = 0;
		while (true)
		{
			try
			{
				await _rateLimiter.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					return await SendRequestAsync(texts, sourceLanguage, targetLanguage, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					_rateLimiter.Release();
				}
			}
			catch (OperationCanceledException)
			{
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "请求已取消", TranslationFailureType.Cancelled)).ToList();
			}
			catch (LibreTranslateException ex2) when (IsRetryable(ex2.FailureType) && retryCount < 2)
			{
				retryCount++;
				await Task.Delay((int)Math.Pow(2.0, retryCount) * 1000, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (LibreTranslateException ex3)
			{
				LibreTranslateException ex4 = ex3;
				LibreTranslateException ex5 = ex4;
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, ex5.Message, ex5.FailureType)).ToList();
			}
			catch (HttpRequestException ex6)
			{
				HttpRequestException ex7 = ex6;
				HttpRequestException ex8 = ex7;
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "网络错误: " + ex8.Message, TranslationFailureType.NetworkError)).ToList();
			}
			catch (Exception ex9)
			{
				Exception ex10 = ex9;
				Exception ex11 = ex10;
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "未知错误: " + ex11.Message, TranslationFailureType.InvalidResponse)).ToList();
			}
		}
	}

	private async Task<IReadOnlyList<TranslationResult>> SendRequestAsync(List<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
	{
		LibreTranslateRequest value = new LibreTranslateRequest
		{
			Q = texts,
			Source = MapLanguageCode(sourceLanguage),
			Target = MapLanguageCode(targetLanguage),
			Format = "text",
			ApiKey = _apiKey
		};
		int num = texts.Sum((string t) => t.Length);
		lock (_statsLock)
		{
			TotalCharactersSubmitted += num;
			TotalRequests++;
		}
		using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(_url + "/translate", value, JsonOpts, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			string value2 = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			throw new LibreTranslateException($"LibreTranslate 返回 {(int)response.StatusCode}: {value2}", MapHttpStatus(response.StatusCode));
		}
		LibreTranslateResponse? libreTranslateResponse = JsonSerializer.Deserialize<LibreTranslateResponse>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false), JsonOpts);
		if (libreTranslateResponse == null || !libreTranslateResponse.TranslatedText.HasValue)
		{
			throw new LibreTranslateException("LibreTranslate 响应格式无效", TranslationFailureType.InvalidResponse);
		}
		JsonElement? translatedText = libreTranslateResponse.TranslatedText;
		if (translatedText.HasValue)
		{
			JsonElement valueOrDefault = translatedText.GetValueOrDefault();
			List<string> list;
			if (valueOrDefault.ValueKind == JsonValueKind.Array)
			{
				list = (from x in valueOrDefault.EnumerateArray()
					select x.GetString() ?? "").ToList();
			}
			else
			{
				JsonElement jsonElement = valueOrDefault;
				if (jsonElement.ValueKind != JsonValueKind.String)
				{
					goto IL_0392;
				}
				list = new List<string> { jsonElement.GetString() ?? "" };
			}
			List<string> list2 = list;
			if (list2.Count != texts.Count)
			{
				throw new LibreTranslateException($"响应条目数不匹配（期望 {texts.Count}，实际 {list2.Count}）", TranslationFailureType.InvalidResponse);
			}
			string sessionId = Guid.NewGuid().ToString("N");
			List<TranslationResult> list3 = new List<TranslationResult>(texts.Count);
			for (int num2 = 0; num2 < texts.Count; num2++)
			{
				string text = texts[num2];
				string text2 = TextNormalizer.Normalize(text) ?? text;
				string text3 = list2[num2];
				list3.Add(new TranslationResult
				{
					NormalizedSource = text2,
					SourceHash = TextNormalizer.ComputeHash(text2),
					TranslatedText = text3,
					SourceLanguage = sourceLanguage,
					TargetLanguage = targetLanguage,
					Provider = Name,
					FromCache = false,
					Success = !string.IsNullOrWhiteSpace(text3),
					ErrorMessage = (string.IsNullOrWhiteSpace(text3) ? "LibreTranslate 返回空译文" : null),
					CharacterCount = text.Length,
					SessionId = sessionId
				});
			}
			return list3;
		}
		goto IL_0392;
		IL_0392:
		throw new LibreTranslateException("LibreTranslate 译文格式无法识别", TranslationFailureType.InvalidResponse);
	}

	public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConfigured)
		{
			return false;
		}
		try
		{
			return (await TranslateAsync("Hello", "en", "zh", cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Success;
		}
		catch
		{
			return false;
		}
	}

	public void ResetStatistics()
	{
		lock (_statsLock)
		{
			TotalCharactersSubmitted = 0L;
			TotalRequests = 0L;
		}
	}

	private static string MapLanguageCode(string code)
	{
		return code.Split('-')[0].ToLowerInvariant();
	}

	private static bool IsRetryable(TranslationFailureType type)
	{
		if (type != TranslationFailureType.RateLimited && type != TranslationFailureType.ServerError)
		{
			return type == TranslationFailureType.Timeout;
		}
		return true;
	}

	private static TranslationFailureType MapHttpStatus(HttpStatusCode statusCode)
	{
		switch (statusCode)
		{
		case HttpStatusCode.Unauthorized:
			return TranslationFailureType.AuthenticationFailed;
		case HttpStatusCode.Forbidden:
			return TranslationFailureType.AuthenticationFailed;
		case HttpStatusCode.RequestTimeout:
			return TranslationFailureType.Timeout;
		case HttpStatusCode.TooManyRequests:
			return TranslationFailureType.RateLimited;
		case HttpStatusCode.BadRequest:
			return TranslationFailureType.InvalidResponse;
		default:
			if (statusCode >= HttpStatusCode.InternalServerError)
			{
				return TranslationFailureType.ServerError;
			}
			return TranslationFailureType.NetworkError;
		}
	}

	private static TranslationResult CreateFailure(string text, string sourceLang, string targetLang, string error, TranslationFailureType failureType)
	{
		string text2 = TextNormalizer.Normalize(text) ?? text;
		return new TranslationResult
		{
			NormalizedSource = text2,
			SourceHash = TextNormalizer.ComputeHash(text2),
			TranslatedText = string.Empty,
			SourceLanguage = sourceLang,
			TargetLanguage = targetLang,
			Provider = "libretranslate",
			Success = false,
			ErrorMessage = $"{error}（类型: {failureType}）",
			CharacterCount = 0,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}
}
