using System;
using System.Collections.Generic;
using SteamCN.Core.Enums;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface IOverlayRenderer
{
	DisplayMode CurrentMode { get; }

	bool IsVisible { get; }

	int DisplayedTranslationCount { get; }

	event EventHandler<DisplayMode>? ModeChanged;

	void AttachToWindow(SteamWindowInfo window);

	void DetachFromWindow(nint windowHandle);

	void UpdateTranslations(string sessionId, IReadOnlyList<TranslationResult> results, IReadOnlyList<TextFragment> sourceFragments);

	void ShiftBlocks(string sessionId, double deltaY);

	void UpdateAnchorPositions(string sessionId, IReadOnlyList<(string AnchorId, ScreenRect NewBounds)> movedAnchors);

	void RemoveBlocks(string sessionId, IReadOnlyList<string> expiredAnchorIds);

	void HideTranslations(string sessionId);

	void HideAll();

	void ShowAll();

	void SetMode(DisplayMode mode);

	void SetLayoutMode(LayoutMode mode);

	void ApplyOverlaySettings(OverlaySettings settings);

	void UpdateWindowPosition(SteamWindowInfo window);

	void SetClickThrough(bool enabled);

	void ClearAll();
}
