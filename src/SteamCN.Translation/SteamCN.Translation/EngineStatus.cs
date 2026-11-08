using SteamCN.Core.Enums;

namespace SteamCN.Translation;

public class EngineStatus
{
	public TranslationEngine Engine { get; set; }

	public bool IsConfigured { get; set; }

	public string DisplayName { get; set; } = "";

	public string Description { get; set; } = "";
}
