using System;
using System.Threading.Tasks;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Interfaces;

public interface ICompatibilityManager
{
	CompatibilityStatus Status { get; }

	event EventHandler<CompatibilityStatus>? StatusChanged;

	event EventHandler<(PageArea Area, bool Degraded)>? AreaDegraded;

	Task<HealthCheckResult> RunHealthCheckAsync();

	void MarkAreaDegraded(PageArea area, string reason);

	void ClearAreaDegraded(PageArea area);

	bool IsAreaDegraded(PageArea area);

	string? GetDegradedReason(PageArea area);

	Task<bool> LoadRulesAsync(string rulesJson);

	Task<bool> RollbackRulesAsync();

	string ExportDiagnostics();
}
