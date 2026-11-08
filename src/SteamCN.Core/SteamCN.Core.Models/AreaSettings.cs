using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class AreaSettings
{
	public bool Workshop { get; set; } = true;

	public bool Reviews { get; set; } = true;

	public bool Community { get; set; } = true;

	public bool Store { get; set; } = true;

	public bool Guides { get; set; } = true;

	public bool IsEnabled(PageArea area)
	{
		return area switch
		{
			PageArea.Workshop => Workshop, 
			PageArea.Reviews => Reviews, 
			PageArea.Community => Community, 
			PageArea.Store => Store, 
			PageArea.Guides => Guides, 
			_ => false, 
		};
	}
}
