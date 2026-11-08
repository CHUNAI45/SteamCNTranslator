using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ITranslationProvider
{
	string Name { get; }

	bool IsConfigured { get; }

	int MaxCharactersPerRequest { get; }

	long TotalCharactersSubmitted { get; }

	long TotalRequests { get; }

	Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default(CancellationToken));

	void ResetStatistics();
}
