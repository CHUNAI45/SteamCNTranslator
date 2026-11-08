using System.Collections.Generic;

namespace SteamCN.Translation;

public class EngineDiagnostics
{
	public bool PythonInstalled { get; set; }

	public bool BridgeScriptValid { get; set; }

	public bool ArgosImportable { get; set; }

	public string? ArgosVersion { get; set; }

	public bool LocalPackagesDownloaded { get; set; }

	public int LocalPackageCount { get; set; }

	public string? LocalPackagesDir { get; set; }

	public bool PackagesDeployed { get; set; }

	public int DeployedPackageCount { get; set; }

	public List<string> DeployedPackages { get; set; } = new List<string>();

	public string StatusText { get; set; } = "未安装";
}
