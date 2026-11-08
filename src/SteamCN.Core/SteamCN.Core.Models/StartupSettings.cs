namespace SteamCN.Core.Models;

public class StartupSettings
{
	public bool MinimizeToTray { get; set; } = true;

	public bool AutoStartWithWindows { get; set; }

	public bool PortableMode { get; set; }
}
