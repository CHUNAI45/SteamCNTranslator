namespace SteamCN.Core.Models;

public class HotkeySettings
{
	public string TogglePause { get; set; } = "Ctrl+Alt+T";

	public string CycleMode { get; set; } = "Ctrl+Alt+M";

	public string HideOverlay { get; set; } = "Ctrl+Alt+H";

	public string OpenSettings { get; set; } = "Ctrl+Alt+S";

	public bool Enabled { get; set; } = true;
}
