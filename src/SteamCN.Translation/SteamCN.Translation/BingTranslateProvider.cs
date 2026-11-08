using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Translation;

public class BingTranslateProvider : IProgressiveTranslationProvider
{
	private const string HostUrl = "https://www.bing.com/Translator";

	private const string ApiUrl = "https://www.bing.com/ttranslatev3";

	private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

	private static readonly TimeSpan PageParamsCacheDuration = TimeSpan.FromMinutes(50.0);

	private static readonly Regex IgRegex = new Regex("IG:\"([A-Z0-9]+)\"", RegexOptions.Compiled);

	private static readonly Regex IidRegex = new Regex("data-iid=\"([^\"]+)\"", RegexOptions.Compiled);

	private static readonly Regex AbuseRegex = new Regex("var params_AbusePreventionHelper = (\\[.*?\\]);", RegexOptions.Compiled);

	private static readonly Regex KeyRegex = new Regex("\\[\\s*(\\d+)", RegexOptions.Compiled);

	private static readonly Regex TokenRegex = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);

	private readonly HttpClient _httpClient;

	private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(2, 2);

	private readonly SemaphoreSlim _paramsLock = new SemaphoreSlim(1, 1);

	private readonly object _statsLock = new object();

	private bool _enabled = true;

	private string? _ig;

	private string? _iid;

	private string? _key;

	private string? _token;

	private DateTimeOffset _paramsExpiry = DateTimeOffset.MinValue;

	public string Name => "bing";

	public int MaxCharactersPerRequest => 1000;

	public bool IsConfigured => _enabled;

	public long TotalCharactersSubmitted { get; private set; }

	public long TotalRequests { get; private set; }

	public event Action? ConfigurationChanged;

	public BingTranslateProvider(HttpClient? httpClient = null)
	{
		if (httpClient != null)
		{
			_httpClient = httpClient;
		}
		else
		{
			HttpClientHandler handler = new HttpClientHandler
			{
				CookieContainer = new CookieContainer(),
				UseCookies = true
			};
			_httpClient = new HttpClient(handler);
		}
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
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "Bing 网页翻译已禁用", TranslationFailureType.InvalidResponse)).ToList();
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
					: CreateFailure(text, sourceLanguage, targetLanguage, "Bing 请求超时", TranslationFailureType.Timeout);
			}
			catch (BingTranslateException ex2) when (IsRetryable(ex2.FailureType) && retryCount < (fastFail ? 1 : 2) &&
				(!fastFail || ex2.FailureType == TranslationFailureType.AuthenticationFailed))
			{
				retryCount++;
				if (ex2.FailureType == TranslationFailureType.AuthenticationFailed)
				{
					ClearPageParams();
				}
				try { await Task.Delay(fastFail ? 150 : (int)Math.Pow(2.0, retryCount) * 1000, ct).ConfigureAwait(false); }
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return CreateFailure(text, sourceLanguage, targetLanguage, "请求已取消", TranslationFailureType.Cancelled);
				}
			}
			catch (BingTranslateException ex3)
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

	private async Task<(string ig, string iid, string key, string token)> GetPageParamsAsync(CancellationToken ct)
	{
		if (_ig != null && _iid != null && _key != null && _token != null && DateTimeOffset.UtcNow < _paramsExpiry.AddMinutes(-2.0))
		{
			return (ig: _ig, iid: _iid, key: _key, token: _token);
		}
		await _paramsLock.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (_ig != null && _iid != null && _key != null && _token != null && DateTimeOffset.UtcNow < _paramsExpiry.AddMinutes(-2.0))
			{
				return (ig: _ig, iid: _iid, key: _key, token: _token);
			}
			using HttpResponseMessage response = await _httpClient.GetAsync("https://www.bing.com/Translator", ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!response.IsSuccessStatusCode)
			{
				TranslationFailureType translationFailureType = MapHttpStatus(response.StatusCode);
				throw new BingTranslateException(translationFailureType switch
				{
					TranslationFailureType.AuthenticationFailed => "Bing 翻译页面拒绝访问（可能触发反自动化保护），请切换引擎", 
					TranslationFailureType.RateLimited => "Bing 翻译页面限频，请稍后重试或切换引擎", 
					_ => $"Bing 翻译页面返回 {(int)response.StatusCode}", 
				}, translationFailureType);
			}
			string input = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			Match match = IgRegex.Match(input);
			if (!match.Success)
			{
				throw new BingTranslateException("Bing 页面结构变更：未找到 IG 参数", TranslationFailureType.InvalidResponse);
			}
			Match match2 = IidRegex.Match(input);
			if (!match2.Success)
			{
				throw new BingTranslateException("Bing 页面结构变更：未找到 IID 参数", TranslationFailureType.InvalidResponse);
			}
			Match match3 = AbuseRegex.Match(input);
			if (!match3.Success)
			{
				throw new BingTranslateException("Bing 页面结构变更：未找到 AbusePreventionHelper 参数", TranslationFailureType.InvalidResponse);
			}
			string value = match3.Groups[1].Value;
			Match match4 = KeyRegex.Match(value);
			Match match5 = TokenRegex.Match(value);
			if (!match4.Success || !match5.Success)
			{
				throw new BingTranslateException("Bing 页面结构变更：key/token 提取失败", TranslationFailureType.InvalidResponse);
			}
			_ig = match.Groups[1].Value;
			_iid = match2.Groups[1].Value;
			_key = match4.Groups[1].Value;
			_token = match5.Groups[1].Value;
			_paramsExpiry = DateTimeOffset.UtcNow + PageParamsCacheDuration;
			return (ig: _ig, iid: _iid, key: _key, token: _token);
		}
		finally
		{
			_paramsLock.Release();
		}
	}

	private void ClearPageParams()
	{
		_ig = null;
		_iid = null;
		_key = null;
		_token = null;
		_paramsExpiry = DateTimeOffset.MinValue;
	}

	private async Task<TranslationResult> SendRequestAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
	{
		if (text.Length > MaxCharactersPerRequest)
		{
			text = text.Substring(0, MaxCharactersPerRequest);
		}
		(string, string, string, string) tuple = await GetPageParamsAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		string item = tuple.Item1;
		string item2 = tuple.Item2;
		string item3 = tuple.Item3;
		string item4 = tuple.Item4;
		string value = MapFromLanguage(sourceLanguage);
		string value2 = MapToLanguage(targetLanguage);
		Dictionary<string, string> nameValueCollection = new Dictionary<string, string>
		{
			["text"] = text,
			["fromLang"] = value,
			["to"] = value2,
			["tryFetchingGenderDebiasedTranslations"] = "true",
			["key"] = item3,
			["token"] = item4
		};
		string requestUri = $"{"https://www.bing.com/ttranslatev3"}?isVertical=1&&IG={item}&IID={item2}";
		lock (_statsLock)
		{
			TotalCharactersSubmitted += text.Length;
			TotalRequests++;
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUri);
		request.Headers.Referrer = new Uri("https://www.bing.com/Translator");
		request.Headers.TryAddWithoutValidation("Origin", "https://www.bing.com");
		request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
		request.Content = new FormUrlEncodedContent(nameValueCollection);
		using HttpResponseMessage response = await _httpClient.SendAsync(request, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			string s = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			TranslationFailureType translationFailureType = MapHttpStatus(response.StatusCode);
			throw new BingTranslateException(translationFailureType switch
			{
				TranslationFailureType.AuthenticationFailed => "Bing key/token 无效或已过期，正在刷新重试", 
				TranslationFailureType.RateLimited => "Bing 翻译端点限频，请稍后重试或切换引擎", 
				_ => $"Bing 网页翻译返回 {(int)response.StatusCode}", 
			} + ": " + Truncate(s, 200), translationFailureType);
		}
		string text2 = ParseResponse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
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
			ErrorMessage = (string.IsNullOrWhiteSpace(text2) ? "Bing 返回空译文（可能是源语言不匹配）" : null),
			CharacterCount = text.Length,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}

	private static string ParseResponse(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.ValueKind != JsonValueKind.Array || rootElement.GetArrayLength() == 0)
		{
			throw new BingTranslateException("Bing 响应格式无效", TranslationFailureType.InvalidResponse);
		}
		JsonElement jsonElement = rootElement[0];
		if (jsonElement.ValueKind != JsonValueKind.Object || !jsonElement.TryGetProperty("translations", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
		{
			throw new BingTranslateException("Bing 响应缺少译文（key/token 可能无效）", TranslationFailureType.AuthenticationFailed);
		}
		StringBuilder stringBuilder = new StringBuilder();
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.TryGetProperty("text", out var value2) && value2.ValueKind == JsonValueKind.String)
			{
				stringBuilder.Append(value2.GetString());
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

	private static string MapFromLanguage(string code)
	{
		string text = code.ToLowerInvariant();
		if (text == "auto" || (text != null && text.Length == 0))
		{
			return "auto-detect";
		}
		return text.Split('-')[0];
	}

	private static string MapToLanguage(string code)
	{
		string text = code.ToLowerInvariant();
		switch (text)
		{
		case "zh-hans":
		case "zh-cn":
		case "zh":
			return "zh-Hans";
		case "zh-hant":
		case "zh-tw":
			return "zh-Hant";
		default:
			return text.Split('-')[0];
		}
	}

	private static bool IsRetryable(TranslationFailureType type)
	{
		if (type != TranslationFailureType.RateLimited && type != TranslationFailureType.ServerError && type != TranslationFailureType.Timeout)
		{
			return type == TranslationFailureType.AuthenticationFailed;
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
			if (statusCode == HttpStatusCode.ResetContent)
			{
				return TranslationFailureType.AuthenticationFailed;
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
			Provider = "bing",
			Success = false,
			ErrorMessage = $"{error}（类型: {failureType}）",
			CharacterCount = 0,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}
}
