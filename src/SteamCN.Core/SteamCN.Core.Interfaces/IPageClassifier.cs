using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface IPageClassifier
{
	Task<PageClassification> ClassifyAsync(SteamWindowInfo window, IReadOnlyList<TextFragment> fragments, CancellationToken cancellationToken = default(CancellationToken));

	void SetManualOverride(nint windowHandle, PageArea area);

	void ClearManualOverride(nint windowHandle);
}
