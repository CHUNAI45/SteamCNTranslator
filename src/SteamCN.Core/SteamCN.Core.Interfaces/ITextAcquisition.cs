using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ITextAcquisition
{
	CaptureMethod Method { get; }

	int LastUiaFragmentCount { get; }

	int LastOcrFragmentCount { get; }

	bool LastOcrAttempted { get; }

	bool LastCaptureSuccess { get; }

	int LastMergedCount { get; }

	long LastUiaScanMs { get; }

	double LastScrollDeltaY { get; }

	bool IsScrolling { get; }

	Task<IReadOnlyList<TextFragment>> AcquireAsync(SteamWindowInfo window, ScreenRect visibleRegion, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> IsUiaAvailableAsync(SteamWindowInfo window);

	bool IsOcrAvailable();

	void ResetScrollDetector();
}
