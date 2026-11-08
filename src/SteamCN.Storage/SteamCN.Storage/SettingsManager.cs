using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Storage;

[SupportedOSPlatform("windows")]
public class SettingsManager : ISettingsManager
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	private readonly string _configPath;

	private readonly string _dataDirectory;

	private readonly object _lock = new object();

	public AppSettings Settings { get; private set; } = new AppSettings();

	public event EventHandler<AppSettings>? SettingsChanged;

	public SettingsManager(string? dataDirectory = null)
	{
		_dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator");
		_configPath = Path.Combine(_dataDirectory, "config.json");
	}

	public string GetDataDirectory()
	{
		return _dataDirectory;
	}

	public Task LoadAsync()
	{
		lock (_lock)
		{
			Directory.CreateDirectory(_dataDirectory);
			if (File.Exists(_configPath))
			{
				try
				{
					AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath, Encoding.UTF8), JsonOptions);
					if (appSettings != null)
					{
						Settings = MergeWithDefaults(appSettings);
					}
				}
				catch
				{
					Settings = new AppSettings();
				}
			}
			else
			{
				Settings = new AppSettings();
				SaveSync();
			}
		}
		return Task.CompletedTask;
	}

	public Task SaveAsync()
	{
		lock (_lock)
		{
			SaveSync();
		}
		SettingsChanged?.Invoke(this, Settings);
		return Task.CompletedTask;
	}

	private void SaveSync()
	{
		string contents = JsonSerializer.Serialize(Settings, JsonOptions);
		File.WriteAllText(_configPath, contents, Encoding.UTF8);
	}

	public Task ResetToDefaultAsync()
	{
		lock (_lock)
		{
			Settings = new AppSettings();
			SaveSync();
		}
		SettingsChanged?.Invoke(this, Settings);
		return Task.CompletedTask;
	}

	public Task SaveApiKeyAsync(string provider, string apiKey)
	{
		if (string.IsNullOrEmpty(apiKey))
		{
			throw new ArgumentException("API key cannot be empty.", "apiKey");
		}
		byte[] bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes("SteamCN_" + provider), DataProtectionScope.CurrentUser);
		string apiKeyPath = GetApiKeyPath(provider);
		Directory.CreateDirectory(Path.GetDirectoryName(apiKeyPath));
		File.WriteAllBytes(apiKeyPath, bytes);
		return Task.CompletedTask;
	}

	public Task<string?> GetApiKeyAsync(string provider)
	{
		string apiKeyPath = GetApiKeyPath(provider);
		if (!File.Exists(apiKeyPath))
		{
			return Task.FromResult<string>(null);
		}
		try
		{
			byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(apiKeyPath), Encoding.UTF8.GetBytes("SteamCN_" + provider), DataProtectionScope.CurrentUser);
			return Task.FromResult(Encoding.UTF8.GetString(bytes));
		}
		catch
		{
			return Task.FromResult<string>(null);
		}
	}

	public Task DeleteApiKeyAsync(string provider)
	{
		string apiKeyPath = GetApiKeyPath(provider);
		if (File.Exists(apiKeyPath))
		{
			File.Delete(apiKeyPath);
		}
		return Task.CompletedTask;
	}

	private string GetApiKeyPath(string provider)
	{
		return Path.Combine(_dataDirectory, "keys", provider + ".key");
	}

	private static AppSettings MergeWithDefaults(AppSettings loaded)
	{
		AppSettings appSettings = new AppSettings();
		loaded.AppName = (string.IsNullOrEmpty(loaded.AppName) ? appSettings.AppName : loaded.AppName);
		loaded.Version = (string.IsNullOrEmpty(loaded.Version) ? appSettings.Version : loaded.Version);
		loaded.TargetLanguage = (string.IsNullOrEmpty(loaded.TargetLanguage) ? appSettings.TargetLanguage : loaded.TargetLanguage);
		AppSettings appSettings2 = loaded;
		if (appSettings2.Areas == null)
		{
			AreaSettings areaSettings = (appSettings2.Areas = appSettings.Areas);
		}
		appSettings2 = loaded;
		if (appSettings2.Capture == null)
		{
			CaptureSettings captureSettings = (appSettings2.Capture = appSettings.Capture);
		}
		appSettings2 = loaded;
		if (appSettings2.Overlay == null)
		{
			OverlaySettings overlaySettings = (appSettings2.Overlay = appSettings.Overlay);
		}
		appSettings2 = loaded;
		if (appSettings2.Translation == null)
		{
			TranslationSettings translationSettings = (appSettings2.Translation = appSettings.Translation);
		}
		appSettings2 = loaded;
		if (appSettings2.Compatibility == null)
		{
			CompatibilitySettings compatibilitySettings = (appSettings2.Compatibility = appSettings.Compatibility);
		}
		appSettings2 = loaded;
		if (appSettings2.Privacy == null)
		{
			PrivacySettings privacySettings = (appSettings2.Privacy = appSettings.Privacy);
		}
		appSettings2 = loaded;
		if (appSettings2.Startup == null)
		{
			StartupSettings startupSettings = (appSettings2.Startup = appSettings.Startup);
		}
		appSettings2 = loaded;
		if (appSettings2.Hotkeys == null)
		{
			HotkeySettings hotkeySettings = (appSettings2.Hotkeys = appSettings.Hotkeys);
		}
		return loaded;
	}
}
