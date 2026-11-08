using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface IGlossaryProcessor
{
	string Version { get; }

	event EventHandler? GlossaryChanged;

	GlossaryProcessResult ProtectTerms(string text, string? appId);

	string RestoreTerms(string translatedText, IReadOnlyDictionary<string, GlossaryTerm> placeholders);

	IReadOnlyList<GlossaryTerm> GetTerms(string? appId = null);

	void UpsertTerm(GlossaryTerm term);

	bool DeleteTerm(long id);

	Task<int> ImportFromJsonAsync(string jsonContent, string? appId = null);

	Task<string> ExportToJsonAsync(string? appId = null);

	void RefreshVersion();
}
