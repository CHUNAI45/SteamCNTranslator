using System.Collections.Generic;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ITextRegionTracker
{
	TextRegionUpdateResult UpdateRegions(string sessionId, IReadOnlyList<TextFragment> currentFragments);

	void ClearSession(string sessionId);

	IReadOnlyList<TextFragment> GetActiveRegions(string sessionId);

	void NotifyScrollStarted(string sessionId);

	void NotifyScrollEnded(string sessionId);
}
