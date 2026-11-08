using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Translation;

public class TranslationProviderManager : IProgressiveTranslationProvider
{
	private readonly GoogleTranslateProvider _googleProvider;

	private readonly BingTranslateProvider _bingProvider;

	private readonly LocalTranslatorProvider _localProvider;

	private readonly LibreTranslateProvider _libreProvider;

	private readonly AzureTranslatorProvider _azureProvider;

	private readonly object _switchLock = new object();

	private TranslationEngine _currentEngine;

	private bool _autoFallback = true;

	private bool _googleEnabled = true;

	private bool _bingEnabled = true;

	public string? LastFallbackReason { get; private set; }

	public string Name => _currentEngine switch
	{
		TranslationEngine.Google => "google", 
		TranslationEngine.Bing => "bing", 
		TranslationEngine.Local => "local", 
		TranslationEngine.LibreTranslate => "libretranslate", 
		TranslationEngine.Azure => "azure", 
		_ => "google", 
	};

	public int MaxCharactersPerRequest => ActiveProvider.MaxCharactersPerRequest;

	public bool IsConfigured => ActiveProvider.IsConfigured;

	public long TotalCharactersSubmitted => ActiveProvider.TotalCharactersSubmitted;

	public long TotalRequests => ActiveProvider.TotalRequests;

	public TranslationEngine CurrentEngine => _currentEngine;

	public TranslationEngine EffectiveEngine => _currentEngine;

	public ITranslationProvider ActiveProvider
	{
		get
		{
			lock (_switchLock)
			{
				return GetProviderInternal(_currentEngine);
			}
		}
	}

	public LocalTranslatorProvider LocalProvider => _localProvider;

	public LocalEngineInstaller LocalInstaller => _localProvider.Installer;

	public GoogleTranslateProvider GoogleProvider => _googleProvider;

	public BingTranslateProvider BingProvider => _bingProvider;

	public event Action? EngineChanged;

	public event Action<TranslationEngine, string>? EngineFallbackOccurred;

	public TranslationProviderManager(GoogleTranslateProvider googleProvider, BingTranslateProvider bingProvider, LocalTranslatorProvider localProvider, LibreTranslateProvider libreProvider, AzureTranslatorProvider azureProvider)
	{
		_googleProvider = googleProvider;
		_bingProvider = bingProvider;
		_localProvider = localProvider;
		_libreProvider = libreProvider;
		_azureProvider = azureProvider;
	}

	public void SwitchEngine(TranslationEngine engine)
	{
		lock (_switchLock)
		{
			if (_currentEngine == engine)
			{
				return;
			}
			_currentEngine = engine;
			LastFallbackReason = null;
		}
		EngineChanged?.Invoke();
	}

	public void ApplySettings(AppSettings settings, string? azureApiKey = null)
	{
		lock (_switchLock)
		{
			_currentEngine = settings.Translation.Engine;
			_autoFallback = settings.Translation.AutoFallback;
			_googleEnabled = settings.Translation.GoogleEnabled;
			_bingEnabled = settings.Translation.BingEnabled;
			LastFallbackReason = null;
		}
		_googleProvider.Configure(_googleEnabled);
		_bingProvider.Configure(_bingEnabled);
		_azureProvider.Configure(azureApiKey ?? string.Empty, settings.Translation.AzureRegion);
		_libreProvider.Configure(settings.Translation.LibreTranslateUrl, settings.Translation.LibreTranslateApiKey);
		EngineChanged?.Invoke();
	}

	public ITranslationProvider GetProvider(TranslationEngine engine)
	{
		return GetProviderInternal(engine);
	}

	private ITranslationProvider GetProviderInternal(TranslationEngine engine)
	{
		return engine switch
		{
			TranslationEngine.Google => (ITranslationProvider)_googleProvider, 
			TranslationEngine.Bing => _bingProvider, 
			TranslationEngine.Local => _localProvider, 
			TranslationEngine.LibreTranslate => _libreProvider, 
			TranslationEngine.Azure => _azureProvider, 
			_ => _googleProvider, 
		};
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
		TranslationEngine primary;
		bool allowFallback;
		lock (_switchLock)
		{
			primary = _currentEngine;
			allowFallback = _autoFallback;
		}

		var providerInternal = GetProviderInternal(primary);
		TranslationEngine? alternateWeb = primary switch
		{
			TranslationEngine.Google when _bingProvider.IsConfigured => TranslationEngine.Bing,
			TranslationEngine.Bing when _googleProvider.IsConfigured => TranslationEngine.Google,
			_ => null
		};
		bool useQuickFallback = allowFallback && onSuccess != null && alternateWeb != null && providerInternal.IsConfigured;
		var results = (await (useQuickFallback
			? ProgressiveRequests.RunAsync(texts, TranslateWithWebFallbackAsync, onSuccess, cancellationToken)
			: TranslateAsync(providerInternal, texts, onSuccess)).ConfigureAwait(false)).ToArray();
		if (!allowFallback || cancellationToken.IsCancellationRequested || results.All(result => result.Success))
			return results;

		string reason = results.First(result => !result.Success).ErrorMessage ?? "原引擎翻译失败";
		foreach (var fallback in GetFallbackChain(primary))
		{
			if (cancellationToken.IsCancellationRequested) break;
			if (useQuickFallback && fallback == alternateWeb) continue;
			var provider = GetProviderInternal(fallback);
			if (!provider.IsConfigured) continue;
			var missing = Enumerable.Range(0, results.Length).Where(index => !results[index].Success).ToArray();
			if (missing.Length == 0) break;
			var pending = missing.Select(index => texts[index]).ToArray();
			try
			{
				var recovered = await TranslateAsync(provider, pending,
					onSuccess == null ? null : (index, result) => onSuccess(missing[index], result)).ConfigureAwait(false);
				if (cancellationToken.IsCancellationRequested) break;
				bool hasRecovery = false;
				for (int i = 0; i < missing.Length && i < recovered.Count; i++)
				{
					if (!recovered[i].Success) continue;
					results[missing[i]] = recovered[i];
					hasRecovery = true;
				}
				if (hasRecovery)
				{
					string message = $"{primary} 部分段落由 {fallback} 补译：{reason}";
					lock (_switchLock)
					{
						if (_currentEngine != primary) break;
						LastFallbackReason = message;
					}
					EngineFallbackOccurred?.Invoke(fallback, message);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				System.Diagnostics.Debug.WriteLine(ex);
			}
		}
		return results;

		async Task<TranslationResult> TranslateWithWebFallbackAsync(string text, CancellationToken token)
		{
			var first = await TranslateWebAsync(primary, text, token).ConfigureAwait(false);
			if (first.Success || token.IsCancellationRequested) return first;
			var second = await TranslateWebAsync(alternateWeb!.Value, text, token).ConfigureAwait(false);
			return second.Success ? second : first;
		}

		Task<TranslationResult> TranslateWebAsync(TranslationEngine engine, string text, CancellationToken token) =>
			engine == TranslationEngine.Google
				? _googleProvider.TranslateOneAsync(text, sourceLanguage, targetLanguage, token, fastFail: true)
				: _bingProvider.TranslateOneAsync(text, sourceLanguage, targetLanguage, token, fastFail: true);

		Task<IReadOnlyList<TranslationResult>> TranslateAsync(ITranslationProvider provider, IReadOnlyList<string> input,
			Action<int, TranslationResult>? report) =>
			report != null && provider is IProgressiveTranslationProvider progressive
				? progressive.TranslateBatchProgressivelyAsync(input, sourceLanguage, targetLanguage, report, cancellationToken)
				: provider.TranslateBatchAsync(input, sourceLanguage, targetLanguage, cancellationToken);
	}

	private List<TranslationEngine> GetFallbackChain(TranslationEngine excludeEngine)
	{
		List<TranslationEngine> list = new List<TranslationEngine>();
		if (_googleEnabled && excludeEngine != TranslationEngine.Google)
		{
			list.Add(TranslationEngine.Google);
		}
		if (_bingEnabled && excludeEngine != TranslationEngine.Bing)
		{
			list.Add(TranslationEngine.Bing);
		}
		if (excludeEngine != TranslationEngine.Local && _localProvider.IsConfigured)
		{
			list.Add(TranslationEngine.Local);
		}
		if (excludeEngine != TranslationEngine.LibreTranslate && _libreProvider.IsConfigured)
		{
			list.Add(TranslationEngine.LibreTranslate);
		}
		if (excludeEngine != TranslationEngine.Azure && _azureProvider.IsConfigured)
		{
			list.Add(TranslationEngine.Azure);
		}
		return list;
	}

	public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ActiveProvider.TestConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public void ResetStatistics()
	{
		_googleProvider.ResetStatistics();
		_bingProvider.ResetStatistics();
		_localProvider.ResetStatistics();
		_libreProvider.ResetStatistics();
		_azureProvider.ResetStatistics();
	}

	public Dictionary<TranslationEngine, EngineStatus> GetAllEngineStatuses()
	{
		return new Dictionary<TranslationEngine, EngineStatus>
		{
			[TranslationEngine.Google] = new EngineStatus
			{
				Engine = TranslationEngine.Google,
				IsConfigured = _googleProvider.IsConfigured,
				DisplayName = "Google 网页翻译",
				Description = "非官方免费端点，无需 API 密钥，推荐在线首选（可能限频）"
			},
			[TranslationEngine.Bing] = new EngineStatus
			{
				Engine = TranslationEngine.Bing,
				IsConfigured = _bingProvider.IsConfigured,
				DisplayName = "Bing 网页翻译",
				Description = "微软 Edge 免费翻译接口，无需 API 密钥，在线备选"
			},
			[TranslationEngine.Local] = new EngineStatus
			{
				Engine = TranslationEngine.Local,
				IsConfigured = _localProvider.IsConfigured,
				DisplayName = "本地离线翻译",
				Description = "Argos Translate 引擎，安装语言包后完全离线，无需 API 密钥"
			},
			[TranslationEngine.LibreTranslate] = new EngineStatus
			{
				Engine = TranslationEngine.LibreTranslate,
				IsConfigured = _libreProvider.IsConfigured,
				DisplayName = "LibreTranslate",
				Description = "自托管或公共 LibreTranslate 服务，需配置服务地址"
			},
			[TranslationEngine.Azure] = new EngineStatus
			{
				Engine = TranslationEngine.Azure,
				IsConfigured = _azureProvider.IsConfigured,
				DisplayName = "Azure Translator",
				Description = "微软官方在线翻译，需 API 密钥（可选高级选项）"
			}
		};
	}
}

