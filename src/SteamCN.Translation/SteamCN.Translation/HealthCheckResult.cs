using System.Collections.Generic;

namespace SteamCN.Translation;

public class HealthCheckResult
{
	public bool BridgeOk { get; set; }

	public bool ArgosInstalled { get; set; }

	public string? ArgosVersion { get; set; }

	public List<string> Packages { get; set; } = new List<string>();

	public List<string> Issues { get; set; } = new List<string>();
}
