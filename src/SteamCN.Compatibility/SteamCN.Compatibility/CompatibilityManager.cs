using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;

namespace SteamCN.Compatibility;

public class CompatibilityManager : ICompatibilityManager
{
	private readonly ITextAcquisition _textAcquisition;

	private readonly IOcrEngine _ocrEngine;

	private readonly ITranslationProvider _translationProvider;

	private readonly Dictionary<PageArea, string> _degradedAreas = new Dictionary<PageArea, string>();

	private readonly object _lock = new object();

	private string? _currentRulesJson;

	private string? _previousRulesJson;

	public CompatibilityStatus Status
	{
		get
		{
			lock (_lock)
			{
				if (_degradedAreas.Count == 0)
				{
					return CompatibilityStatus.Normal;
				}
				int num = 5;
				return (_degradedAreas.Count < num) ? CompatibilityStatus.PartialDegraded : CompatibilityStatus.Incompatible;
			}
		}
	}

	public event EventHandler<CompatibilityStatus>? StatusChanged;

	public event EventHandler<(PageArea Area, bool Degraded)>? AreaDegraded;

	public CompatibilityManager(ITextAcquisition textAcquisition, IOcrEngine ocrEngine, ITranslationProvider translationProvider)
	{
		_textAcquisition = textAcquisition;
		_ocrEngine = ocrEngine;
		_translationProvider = translationProvider;
	}

	public async Task<HealthCheckResult> RunHealthCheckAsync()
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		bool flag = false;
		try
		{
			flag = _textAcquisition.Method == CaptureMethod.Uia || _textAcquisition.Method == CaptureMethod.Hybrid;
			if (!flag)
			{
				list.Add("UIA 文本采集不可用，将依赖 OCR 兜底");
			}
		}
		catch (Exception ex)
		{
			list.Add("UIA 检测异常: " + ex.Message);
		}
		bool flag2 = false;
		IReadOnlyList<string> readOnlyList = Array.Empty<string>();
		try
		{
			flag2 = _ocrEngine.IsAvailable;
			readOnlyList = _ocrEngine.AvailableLanguages;
			if (!flag2)
			{
				list.Add("Windows OCR 不可用：未安装 OCR 语言包。请在 Windows 设置 > 时间和语言 > 语言和区域 中添加语言并勾选\"光学字符识别\"");
			}
			else if (!readOnlyList.Any((string l) => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
			{
				list.Add("未检测到英语 OCR 语言包，英文识别可能不准确");
			}
		}
		catch (Exception ex2)
		{
			list.Add("OCR 检测异常: " + ex2.Message);
		}
		bool flag3 = false;
		try
		{
			flag3 = _textAcquisition.IsOcrAvailable();
			if (!flag3 & flag2)
			{
				list.Add("OCR 引擎可用但采集器未启用 OCR 兜底");
			}
		}
		catch (Exception ex3)
		{
			list.Add("截图检测异常: " + ex3.Message);
		}
		if (!_translationProvider.IsConfigured)
		{
			list2.Add("翻译服务 " + _translationProvider.Name + " 未配置 API 密钥");
		}
		if (!flag && !(flag2 & flag3))
		{
			list2.Add("无任何可用的文本采集方式（UIA 和 OCR+截图均不可用）");
		}
		CompatibilityStatus status = ((list2.Count > 0) ? CompatibilityStatus.Incompatible : ((list.Count > 0) ? CompatibilityStatus.PartialDegraded : CompatibilityStatus.Normal));
		SetStatus(status);
		HealthCheckResult result = new HealthCheckResult
		{
			Status = status,
			UiaAvailable = flag,
			OcrAvailable = flag2,
			CaptureAvailable = flag3,
			OcrLanguages = readOnlyList,
			Warnings = list,
			Errors = list2
		};
		await Task.CompletedTask;
		return result;
	}

	public void MarkAreaDegraded(PageArea area, string reason)
	{
		CompatibilityStatus? compatibilityStatus = null;
		lock (_lock)
		{
			bool flag = _degradedAreas.ContainsKey(area);
			_degradedAreas[area] = reason ?? "未知原因";
			if (!flag)
			{
				compatibilityStatus = Status;
			}
		}
		AreaDegraded?.Invoke(this, (area, true));
		if (compatibilityStatus.HasValue)
		{
			StatusChanged?.Invoke(this, compatibilityStatus.Value);
		}
	}

	public void ClearAreaDegraded(PageArea area)
	{
		CompatibilityStatus? compatibilityStatus = null;
		lock (_lock)
		{
			if (_degradedAreas.Remove(area))
			{
				compatibilityStatus = Status;
			}
		}
		AreaDegraded?.Invoke(this, (area, false));
		if (compatibilityStatus.HasValue)
		{
			StatusChanged?.Invoke(this, compatibilityStatus.Value);
		}
	}

	public bool IsAreaDegraded(PageArea area)
	{
		lock (_lock)
		{
			return _degradedAreas.ContainsKey(area);
		}
	}

	public string? GetDegradedReason(PageArea area)
	{
		lock (_lock)
		{
			string value;
			return _degradedAreas.TryGetValue(area, out value) ? value : null;
		}
	}

	public Task<bool> LoadRulesAsync(string rulesJson)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(rulesJson);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
			{
				return Task.FromResult(result: false);
			}
			lock (_lock)
			{
				_previousRulesJson = _currentRulesJson;
				_currentRulesJson = rulesJson;
			}
			return Task.FromResult(result: true);
		}
		catch (JsonException)
		{
			return Task.FromResult(result: false);
		}
	}

	public Task<bool> RollbackRulesAsync()
	{
		lock (_lock)
		{
			if (_previousRulesJson == null)
			{
				return Task.FromResult(result: false);
			}
			_currentRulesJson = _previousRulesJson;
			_previousRulesJson = null;
			return Task.FromResult(result: true);
		}
	}

	public string ExportDiagnostics()
	{
		return JsonSerializer.Serialize(new
		{
			Timestamp = DateTimeOffset.UtcNow.ToString("o"),
			Status = Status.ToString(),
			TextAcquisition = new
			{
				Type = _textAcquisition.GetType().Name,
				Method = _textAcquisition.Method.ToString(),
				IsOcrAvailable = SafeCheck(() => _textAcquisition.IsOcrAvailable())
			},
			OcrEngine = new
			{
				Name = _ocrEngine.Name,
				IsAvailable = SafeCheck(() => _ocrEngine.IsAvailable),
				Languages = SafeCheck(() => _ocrEngine.AvailableLanguages)
			},
			TranslationProvider = new { _translationProvider.Name, _translationProvider.IsConfigured, _translationProvider.TotalCharactersSubmitted, _translationProvider.TotalRequests },
			DegradedAreas = GetDegradedAreasSnapshot(),
			CurrentRulesLoaded = (_currentRulesJson != null)
		}, new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
	}

	private Dictionary<string, string> GetDegradedAreasSnapshot()
	{
		lock (_lock)
		{
			return _degradedAreas.ToDictionary((KeyValuePair<PageArea, string> kv) => kv.Key.ToString(), (KeyValuePair<PageArea, string> kv) => kv.Value);
		}
	}

	private void SetStatus(CompatibilityStatus newStatus)
	{
		StatusChanged?.Invoke(this, newStatus);
	}

	private static T SafeCheck<T>(Func<T> check)
	{
		try
		{
			return check();
		}
		catch (Exception ex)
		{
			return (typeof(T) == typeof(string)) ? ((T)(object)("检测失败: " + ex.Message)) : default(T);
		}
	}
}
