using System.Collections.Generic;

namespace SteamCN.Translation;

public class PackageInstallResult
{
	public bool Success { get; set; }

	public List<string> InstalledPackages { get; set; } = new List<string>();

	public bool IsPivot { get; set; }

	public string? PivotNote { get; set; }

	public List<string> PivotPackages { get; set; } = new List<string>();

	public LocalEngineErrorType ErrorType { get; set; } = LocalEngineErrorType.Unknown;

	public string ErrorMessage { get; set; } = "";
}
