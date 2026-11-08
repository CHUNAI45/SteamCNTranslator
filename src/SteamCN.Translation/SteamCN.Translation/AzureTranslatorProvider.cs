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

public class AzureTranslatorProvider : ITranslationProvider
{
	private sealed class AzureTranslationResponse
	{
		public List<AzureTranslation>? Translations { get; set; }
	}

	private sealed class AzureTranslation
	{
		public string? Text { get; set; }

		public string? To { get; set; }
	}

	private const string DefaultEndpoint = "https://api.cognitive.microsofttranslator.com";

	private const string DefaultRegion = "global";

	private const int MaxBatchSize = 100;

	private readonly HttpClient _httpClient;

	private readonly SemaphoreSlim _rateLimiter;

	private readonly object _statsLock = new object();

	private string? _apiKey;

	private string? _region;

	private string _endpoint;

	public string Name => "azure";

	public int MaxCharactersPerRequest => 10000;

	public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

	public long TotalCharactersSubmitted { get; private set; }

	public long TotalRequests { get; private set; }

	public event Action? ConfigurationChanged;

	public AzureTranslatorProvider(HttpClient? httpClient = null)
	{
		_httpClient = httpClient ?? new HttpClient();
		_httpClient.Timeout = TimeSpan.FromSeconds(30.0);
		_rateLimiter = new SemaphoreSlim(1, 1);
		_endpoint = "https://api.cognitive.microsofttranslator.com";
	}

	public void Configure(string apiKey, string? region = null, string? endpoint = null)
	{
		_apiKey = (string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
		_region = (string.IsNullOrWhiteSpace(region) ? "global" : region);
		_endpoint = (string.IsNullOrWhiteSpace(endpoint) ? "https://api.cognitive.microsofttranslator.com" : endpoint.TrimEnd('/'));
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
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "Azure API 密钥未配置", TranslationFailureType.AuthenticationFailed)).ToList();
		}
		List<TranslationResult> allResults = new List<TranslationResult>(texts.Count);
		List<List<string>> list = SplitIntoBatches(texts);
		foreach (List<string> item in list)
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				allResults.AddRange(await TranslateSingleBatchAsync(item, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
				continue;
			}
			break;
		}
		return allResults;
	}

	public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConfigured)
		{
			return false;
		}
		try
		{
			return (await TranslateAsync("Hello", "en", "zh-Hans", cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Success;
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

	private List<List<string>> SplitIntoBatches(IReadOnlyList<string> texts)
	{
		List<List<string>> list = new List<List<string>>();
		List<string> list2 = new List<string>();
		int num = 0;
		foreach (string text in texts)
		{
			int length = text.Length;
			if (list2.Count >= 100 || num + length > MaxCharactersPerRequest)
			{
				if (list2.Count > 0)
				{
					list.Add(list2);
				}
				list2 = new List<string>();
				num = 0;
			}
			list2.Add(text);
			num += length;
		}
		if (list2.Count > 0)
		{
			list.Add(list2);
		}
		return list;
	}

	private async Task<IReadOnlyList<TranslationResult>> TranslateSingleBatchAsync(List<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
	{
		int retryCount = 0;
		while (true)
		{
			try
			{
				await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					return await SendRequestAsync(texts, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
			catch (AzureTranslatorException ex2) when (IsRetryable(ex2.FailureType) && retryCount < 3)
			{
				retryCount++;
				await Task.Delay((ex2.FailureType == TranslationFailureType.RateLimited) ? CalculateRetryDelay(ex2) : ((int)Math.Pow(2.0, retryCount) * 1000), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (AzureTranslatorException ex3)
			{
				AzureTranslatorException ex4 = ex3;
				AzureTranslatorException ex5 = ex4;
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, ex5.Message, ex5.FailureType, ex5.StatusCode)).ToList();
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

	private async Task<IReadOnlyList<TranslationResult>> SendRequestAsync(List<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
	{
		int num = texts.Sum((string t) => t.Length);
		string text = "/translate?api-version=3.0&to=" + Uri.EscapeDataString(targetLanguage);
		if (!sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
			text += "&from=" + Uri.EscapeDataString(sourceLanguage);
		Uri requestUri = new Uri(_endpoint + text);
		StringContent content = new StringContent(JsonSerializer.Serialize(texts.Select((string t) => new
		{
			Text = t
		}).ToList()), Encoding.UTF8, "application/json");
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUri);
		request.Content = content;
		request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
		if (!string.IsNullOrEmpty(_region) && _region != "global")
		{
			request.Headers.Add("Ocp-Apim-Subscription-Region", _region);
		}
		lock (_statsLock)
		{
			TotalCharactersSubmitted += num;
			TotalRequests++;
		}
		using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			string value = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			throw new AzureTranslatorException($"Azure API 返回 {(int)response.StatusCode}: {value}", MapHttpStatus(response.StatusCode), (int)response.StatusCode);
		}
		List<AzureTranslationResponse> list = JsonSerializer.Deserialize<List<AzureTranslationResponse>>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
		if (list == null || list.Count != texts.Count)
		{
			throw new AzureTranslatorException($"Azure API 响应条目数不匹配（期望 {texts.Count}，实际 {list?.Count ?? 0}）", TranslationFailureType.InvalidResponse);
		}
		string sessionId = Guid.NewGuid().ToString("N");
		List<TranslationResult> list2 = new List<TranslationResult>(texts.Count);
		for (int num2 = 0; num2 < texts.Count; num2++)
		{
			string text2 = texts[num2];
			string text3 = TextNormalizer.Normalize(text2) ?? text2;
			string text4 = list[num2].Translations?.FirstOrDefault()?.Text;
			list2.Add(new TranslationResult
			{
				NormalizedSource = text3,
				SourceHash = TextNormalizer.ComputeHash(text3),
				TranslatedText = (text4 ?? string.Empty),
				SourceLanguage = sourceLanguage,
				TargetLanguage = targetLanguage,
				Provider = Name,
				FromCache = false,
				Success = !string.IsNullOrWhiteSpace(text4),
				ErrorMessage = (string.IsNullOrWhiteSpace(text4) ? "Azure 返回空译文" : null),
				CharacterCount = text2.Length,
				SessionId = sessionId
			});
		}
		return list2;
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
		case HttpStatusCode.Conflict:
			return TranslationFailureType.InvalidResponse;
		default:
			if (statusCode >= HttpStatusCode.InternalServerError)
			{
				return TranslationFailureType.ServerError;
			}
			return TranslationFailureType.NetworkError;
		}
	}

	private static int CalculateRetryDelay(AzureTranslatorException ex)
	{
		if (ex.StatusCode != 429)
		{
			return 2000;
		}
		return 5000;
	}

	private static TranslationResult CreateFailure(string text, string sourceLang, string targetLang, string error, TranslationFailureType failureType, int statusCode = 0)
	{
		string text2 = TextNormalizer.Normalize(text) ?? text;
		return new TranslationResult
		{
			NormalizedSource = text2,
			SourceHash = TextNormalizer.ComputeHash(text2),
			TranslatedText = string.Empty,
			SourceLanguage = sourceLang,
			TargetLanguage = targetLang,
			Provider = "azure",
			Success = false,
			ErrorMessage = $"{error}（类型: {failureType}）",
			CharacterCount = 0,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}
}
