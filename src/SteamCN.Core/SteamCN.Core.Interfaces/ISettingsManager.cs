using System;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ISettingsManager
{
	AppSettings Settings { get; }

	event EventHandler<AppSettings>? SettingsChanged;

	Task LoadAsync();

	Task SaveAsync();

	Task ResetToDefaultAsync();

	Task SaveApiKeyAsync(string provider, string apiKey);

	Task<string?> GetApiKeyAsync(string provider);

	Task DeleteApiKeyAsync(string provider);

	string GetDataDirectory();
}
