namespace SteamCN.Core.Models;

public class PrivacySettings
{
	public bool CaptureOnlySteam { get; set; } = true;

	public bool SaveScreenshots { get; set; }

	public bool ExcludeSensitiveRegions { get; set; } = true;

	public bool EncryptApiKeys { get; set; } = true;
}
