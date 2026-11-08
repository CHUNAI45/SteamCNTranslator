using System.Collections.Generic;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ITranslationMemory
{
	long Count { get; }

	CachedTranslation? Lookup(string normalizedSource, string sourceLanguage, string targetLanguage, string provider, string glossaryVersion, string? appId);

	IReadOnlyDictionary<string, CachedTranslation?> LookupBatch(IEnumerable<string> normalizedSources, string sourceLanguage, string targetLanguage, string provider, string glossaryVersion, string? appId);

	void Store(CachedTranslation entry);

	void StoreBatch(IEnumerable<CachedTranslation> entries);

	int InvalidateByGlossaryVersion(string oldVersion);

	int InvalidateByAppId(string appId);

	void ClearAll();

	int TrimToCapacity(long maxEntries);

	Task<string> ExportToJsonAsync();

	Task<int> ImportFromJsonAsync(string jsonContent);

	bool CheckAndRepair();
}
