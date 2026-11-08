using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface ISteamWindowManager
{
	IReadOnlyList<SteamWindowInfo> BoundWindows { get; }

	SteamWindowInfo? PrimaryWindow { get; }

	event EventHandler<SteamWindowInfo>? WindowBound;

	event EventHandler<SteamWindowInfo>? WindowUnbound;

	event EventHandler<SteamWindowInfo>? WindowChanged;

	event EventHandler<(SteamWindowInfo Window, string OldTitle)>? PageNavigated;

	void StartMonitoring();

	void StopMonitoring();

	Task<IReadOnlyList<SteamWindowInfo>> ScanAndBindAsync();

	bool BindWindow(nint handle);

	void UnbindWindow(nint handle);

	void SetPrimaryWindow(nint handle);

	void RefreshWindowStates();

	bool IsWindowValid(nint handle);
}
