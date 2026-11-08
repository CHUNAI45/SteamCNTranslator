using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface IOcrEngine
{
	string Name { get; }

	bool IsAvailable { get; }

	IReadOnlyList<string> AvailableLanguages { get; }

	Task<IReadOnlyList<TextFragment>> RecognizeAsync(byte[] imageData, int width, int height, string? language = null, CancellationToken cancellationToken = default(CancellationToken));
}
