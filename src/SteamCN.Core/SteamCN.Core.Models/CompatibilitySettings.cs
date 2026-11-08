namespace SteamCN.Core.Models;

public class CompatibilitySettings
{
	public bool SafeFallback { get; set; } = true;

	public bool CheckRuleUpdates { get; set; } = true;

	public bool AutoApplyVerifiedRules { get; set; } = true;

	public bool RollbackOnError { get; set; } = true;
}
