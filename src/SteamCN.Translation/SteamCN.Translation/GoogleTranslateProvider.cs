using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Translation;

public class GoogleTranslateProvider : IProgressiveTranslationProvider
{
	private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

	private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

	private readonly HttpClient _httpClient;

	private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(2, 2);

	private readonly object _statsLock = new object();

	private bool _enabled = true;

	public string Name => "google";

	public int MaxCharactersPerRequest => 1800;

	public bool IsConfigured => _enabled;

	public long TotalCharactersSubmitted { get; private set; }

	public long TotalRequests { get; private set; }

	public event Action? ConfigurationChanged;

	public GoogleTranslateProvider(HttpClient? httpClient = null)
	{
		_httpClient = httpClient ?? new HttpClient();
		_httpClient.Timeout = TimeSpan.FromSeconds(15.0);
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
	}

	public void Configure(bool enabled)
	{
		_enabled = enabled;
		ConfigurationChanged?.Invoke();
	}

	public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default(CancellationToken))
	{
		return (await TranslateBatchAsync(new string[1] { text }, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))[0];
	}

	public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
		TranslateBatchCoreAsync(texts, sourceLanguage, targetLanguage, null, cancellationToken);

	public Task<IReadOnlyList<TranslationResult>> TranslateBatchProgressivelyAsync(IReadOnlyList<string> texts,
		string sourceLanguage, string targetLanguage, Action<int, TranslationResult> onSuccess, CancellationToken cancellationToken) =>
		TranslateBatchCoreAsync(texts, sourceLanguage, targetLanguage, onSuccess, cancellationToken);

	private async Task<IReadOnlyList<TranslationResult>> TranslateBatchCoreAsync(IReadOnlyList<string> texts,
		string sourceLanguage, string targetLanguage, Action<int, TranslationResult>? onSuccess, CancellationToken cancellationToken)
	{
		if (texts.Count == 0)
		{
			return Array.Empty<TranslationResult>();
		}
		if (!IsConfigured)
		{
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "Google 网页翻译已禁用", TranslationFailureType.InvalidResponse)).ToList();
		}
		return await ProgressiveRequests.RunAsync(texts, (text, token) => TranslateOneAsync(text, sourceLanguage, targetLanguage, token),
			onSuccess, cancellationToken).ConfigureAwait(false);
	}

	internal async Task<TranslationResult> TranslateOneAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct, bool fastFail = false)
	{
		int retryCount = 0;
		while (true)
		{
			try
			{
				await _rateLimiter.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					using var timeout = fastFail ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
					timeout?.CancelAfter(ProgressiveRequests.QuickAttemptTimeout);
					return await SendRequestAsync(text, sourceLanguage, targetLanguage, timeout?.Token ?? ct).ConfigureAwait(false);
				}
				finally
				{
					_rateLimiter.Release();
				}
			}
			catch (OperationCanceledException)
			{
				return ct.IsCancellationRequested
					? CreateFailure(text, sourceLanguage, targetLanguage, "请求已取消", TranslationFailureType.Cancelled)
					: CreateFailure(text, sourceLanguage, targetLanguage, "Google 请求超时", TranslationFailureType.Timeout);
			}
			catch (GoogleTranslateException ex2) when (!fastFail && IsRetryable(ex2.FailureType) && retryCount < 2)
			{
				retryCount++;
				try { await Task.Delay((int)Math.Pow(2.0, retryCount) * 1000, ct).ConfigureAwait(false); }
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return CreateFailure(text, sourceLanguage, targetLanguage, "请求已取消", TranslationFailureType.Cancelled);
				}
			}
			catch (GoogleTranslateException ex3)
			{
				return CreateFailure(text, sourceLanguage, targetLanguage, ex3.Message, ex3.FailureType);
			}
			catch (HttpRequestException ex4)
			{
				return CreateFailure(text, sourceLanguage, targetLanguage, "网络错误: " + ex4.Message, TranslationFailureType.NetworkError);
			}
			catch (Exception ex5)
			{
				return CreateFailure(text, sourceLanguage, targetLanguage, "未知错误: " + ex5.Message, TranslationFailureType.InvalidResponse);
			}
		}
	}

	private async Task<TranslationResult> SendRequestAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
	{
		string value = MapLanguageCode(sourceLanguage);
		string value2 = MapLanguageCode(targetLanguage);
		if (text.Length > MaxCharactersPerRequest)
		{
			text = text.Substring(0, MaxCharactersPerRequest);
		}
		string requestUri = $"{"https://translate.googleapis.com/translate_a/single"}?client=gtx&dt=t&sl={value}&tl={value2}&q={Uri.EscapeDataString(text)}";
		lock (_statsLock)
		{
			TotalCharactersSubmitted += text.Length;
			TotalRequests++;
		}
		using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			string s = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			TranslationFailureType translationFailureType = MapHttpStatus(response.StatusCode);
			throw new GoogleTranslateException(translationFailureType switch
			{
				TranslationFailureType.AuthenticationFailed => "Google 非官方端点拒绝访问（可能触发反自动化保护），请切换引擎", 
				TranslationFailureType.RateLimited => "Google 非官方端点限频，请稍后重试或切换引擎", 
				_ => $"Google 网页翻译返回 {(int)response.StatusCode}", 
			} + ": " + Truncate(s, 200), translationFailureType);
		}
		string text2 = ParseGtxResponse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
		string text3 = TextNormalizer.Normalize(text) ?? text;
		return new TranslationResult
		{
			NormalizedSource = text3,
			SourceHash = TextNormalizer.ComputeHash(text3),
			TranslatedText = text2,
			SourceLanguage = sourceLanguage,
			TargetLanguage = targetLanguage,
			Provider = Name,
			FromCache = false,
			Success = !string.IsNullOrWhiteSpace(text2),
			ErrorMessage = (string.IsNullOrWhiteSpace(text2) ? "Google 返回空译文（可能是源语言不匹配）" : null),
			CharacterCount = text.Length,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}

	private static string ParseGtxResponse(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.ValueKind != JsonValueKind.Array || rootElement.GetArrayLength() == 0)
		{
			throw new GoogleTranslateException("Google 响应格式无效", TranslationFailureType.InvalidResponse);
		}
		JsonElement jsonElement = rootElement[0];
		if (jsonElement.ValueKind != JsonValueKind.Array)
		{
			throw new GoogleTranslateException("Google 响应缺少译文数组", TranslationFailureType.InvalidResponse);
		}
		StringBuilder stringBuilder = new StringBuilder();
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0)
			{
				JsonElement jsonElement2 = item[0];
				if (jsonElement2.ValueKind == JsonValueKind.String)
				{
					stringBuilder.Append(jsonElement2.GetString());
				}
			}
		}
		return stringBuilder.ToString();
	}

	public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConfigured)
		{
			return false;
		}
		try
		{
			return (await TranslateAsync("Hello", "en", "zh-CN", cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Success;
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
		string text = code.ToLowerInvariant();
		switch (text)
		{
		case "zh-hans":
		case "zh-cn":
		case "zh":
			return "zh-CN";
		case "zh-hant":
		case "zh-tw":
			return "zh-TW";
		default:
			return text.Split('-')[0];
		}
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

	private static string Truncate(string s, int max)
	{
		if (s.Length > max)
		{
			return s.Substring(0, max) + "...";
		}
		return s;
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
			Provider = "google",
			Success = false,
			ErrorMessage = $"{error}（类型: {failureType}）",
			CharacterCount = 0,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}
}


