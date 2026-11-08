using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;

namespace SteamCN.Translation;

public class LocalTranslatorProvider : ITranslationProvider
{
	private readonly LocalEngineInstaller _installer;

	private PythonBridge? _bridge;

	private readonly SemaphoreSlim _bridgeLock = new SemaphoreSlim(1, 1);

	private readonly object _statsLock = new object();

	private bool _disposed;

	public string Name => "local";

	public int MaxCharactersPerRequest => 5000;

	public bool IsConfigured => _installer.IsEngineReady;

	public long TotalCharactersSubmitted { get; private set; }

	public long TotalRequests { get; private set; }

	public LocalEngineInstaller Installer => _installer;

	public event Action? ConfigurationChanged;

	public event Action<string>? Log;

	public LocalTranslatorProvider(LocalEngineInstaller installer)
	{
		_installer = installer;
	}

	private async Task<PythonBridge> GetBridgeAsync(CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		PythonBridge bridge = _bridge;
		if (bridge != null && bridge.IsRunning)
		{
			return _bridge;
		}
		await _bridgeLock.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			bridge = _bridge;
			if (bridge != null && bridge.IsRunning)
			{
				return _bridge;
			}
			if (_bridge != null)
			{
				await _bridge.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
				_bridge = null;
			}
			if (!_installer.IsBridgeScriptValid)
			{
				Log?.Invoke("桥接脚本缺失，正在从嵌入资源恢复...");
				_installer.DeployBridgeScript();
			}
			if (!_installer.IsPythonInstalled)
			{
				throw new LocalEngineException("Python 运行环境未安装，请先安装本地引擎", LocalEngineErrorType.PythonRuntimeMissing);
			}
			if (!_installer.IsArgosInstalled)
			{
				throw new LocalEngineException("翻译组件（argostranslate）未安装，请先安装或修复本地引擎", LocalEngineErrorType.PythonDependencyFailed);
			}
			_bridge = new PythonBridge(_installer.PythonExePath, _installer.BridgeScriptPath);
			foreach (KeyValuePair<string, string> item in _installer.GetBridgeEnvironment())
			{
				_bridge.EnvironmentVariables[item.Key] = item.Value;
			}
			_bridge.LogReceived += (string msg) =>
			{
				Log?.Invoke(msg);
			};
			await _bridge.StartAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			BridgeResponse bridgeResponse = await _bridge.SendAsync(new BridgeRequest
			{
				Action = "init"
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!bridgeResponse.Success)
			{
				LocalEngineErrorType errorType = ((bridgeResponse.ErrorType == "ArgosNotInstalled") ? LocalEngineErrorType.PythonDependencyFailed : LocalEngineErrorType.ModelLoadFailed);
				throw new LocalEngineException("本地引擎初始化失败: " + bridgeResponse.Error, errorType);
			}
			Log?.Invoke("本地引擎已启动，已安装语言包: " + string.Join(", ", bridgeResponse.Packages ?? new List<string>()));
			return _bridge;
		}
		finally
		{
			_bridgeLock.Release();
		}
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
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "本地翻译引擎未安装，请在设置中安装", TranslationFailureType.InvalidResponse)).ToList();
		}
		if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
			return texts.Select(t => CreateFailure(t, sourceLanguage, targetLanguage,
				"本地翻译需要确定原文语种和对应语言包", TranslationFailureType.InvalidResponse)).ToArray();
		try
		{
			BridgeResponse resp = await (await GetBridgeAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).SendAsync(new BridgeRequest
			{
				Action = "translate",
				Texts = texts.ToList(),
				From = sourceLanguage,
				To = targetLanguage
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			lock (_statsLock)
			{
				TotalCharactersSubmitted += texts.Sum((string t) => t.Length);
				TotalRequests++;
			}
			if (!resp.Success)
			{
				TranslationFailureType failureType = ((resp.ErrorType == "PackageNotInstalled") ? TranslationFailureType.InvalidResponse : TranslationFailureType.ServerError);
				return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, resp.Error ?? "本地翻译失败", failureType)).ToList();
			}
			List<string> list = resp.Translations ?? new List<string>();
			string sessionId = Guid.NewGuid().ToString("N");
			List<TranslationResult> list2 = new List<TranslationResult>(texts.Count);
			for (int num = 0; num < texts.Count; num++)
			{
				string text = texts[num];
				string text2 = TextNormalizer.Normalize(text) ?? text;
				string text3 = ((num < list.Count) ? list[num] : "");
				list2.Add(new TranslationResult
				{
					NormalizedSource = text2,
					SourceHash = TextNormalizer.ComputeHash(text2),
					TranslatedText = text3,
					SourceLanguage = sourceLanguage,
					TargetLanguage = targetLanguage,
					Provider = Name,
					FromCache = false,
					Success = !string.IsNullOrWhiteSpace(text3),
					ErrorMessage = (string.IsNullOrWhiteSpace(text3) ? "本地引擎返回空译文" : null),
					CharacterCount = text.Length,
					SessionId = sessionId
				});
			}
			return list2;
		}
		catch (OperationCanceledException)
		{
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "请求已取消", TranslationFailureType.Cancelled)).ToList();
		}
		catch (LocalEngineException ex2)
		{
			LocalEngineException ex3 = ex2;
			LocalEngineException ex4 = ex3;
			Log?.Invoke(ex4.GetLogLine());
			TranslationFailureType failureType2 = ex4.ErrorType switch
			{
				LocalEngineErrorType.PythonRuntimeMissing => TranslationFailureType.InvalidResponse, 
				LocalEngineErrorType.PythonDependencyFailed => TranslationFailureType.InvalidResponse, 
				LocalEngineErrorType.PackageNotFound => TranslationFailureType.InvalidResponse, 
				LocalEngineErrorType.NetworkError => TranslationFailureType.NetworkError, 
				_ => TranslationFailureType.ServerError, 
			};
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, ex4.GetUserFriendlyMessage(), failureType2)).ToList();
		}
		catch (InvalidOperationException ex5)
		{
			InvalidOperationException ex6 = ex5;
			InvalidOperationException ex7 = ex6;
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, ex7.Message, TranslationFailureType.InvalidResponse)).ToList();
		}
		catch (Exception ex8)
		{
			Exception ex9 = ex8;
			Exception ex10 = ex9;
			Log?.Invoke("本地翻译异常: " + ex10.Message);
			return texts.Select((string t) => CreateFailure(t, sourceLanguage, targetLanguage, "本地引擎错误: " + ex10.Message, TranslationFailureType.ServerError)).ToList();
		}
	}

	public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConfigured)
		{
			return false;
		}
		try
		{
			return await (await GetBridgeAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).PingAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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

	public Task<PythonBridge> GetBridgeForManagementAsync(CancellationToken ct = default(CancellationToken))
	{
		return GetBridgeAsync(ct);
	}

	public void NotifyConfigurationChanged()
	{
		ConfigurationChanged?.Invoke();
	}

	public async ValueTask DisposeAsync()
	{
		_disposed = true;
		if (_bridge != null)
		{
			await _bridge.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			_bridge = null;
		}
		_bridgeLock.Dispose();
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
			Provider = "local",
			Success = false,
			ErrorMessage = $"{error}（类型: {failureType}）",
			CharacterCount = 0,
			SessionId = Guid.NewGuid().ToString("N")
		};
	}
}
