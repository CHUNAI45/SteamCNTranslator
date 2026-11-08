using System.Collections.Generic;
using System.Text.Json.Serialization;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class TranslationSettings
{
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public TranslationEngine Engine { get; set; }

	public bool GoogleEnabled { get; set; } = true;

	public bool BingEnabled { get; set; } = true;

	public bool AutoFallback { get; set; } = true;

	public string LibreTranslateUrl { get; set; } = string.Empty;

	public string LibreTranslateApiKey { get; set; } = string.Empty;

	public List<string> InstalledLanguagePackages { get; set; } = new List<string>();

	public bool LocalEngineInstalled { get; set; }

	public string Provider { get; set; } = "google";

	public int MaxConcurrency { get; set; } = 2;

	public int BatchCharacters { get; set; } = 2000;

	public int TimeoutSeconds { get; set; } = 30;

	public int MaxRetries { get; set; } = 2;

	public bool CacheFirst { get; set; } = true;

	public long MonthlyCharacterBudget { get; set; }

	public string AzureRegion { get; set; } = string.Empty;
}
