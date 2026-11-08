using System.Collections.Generic;
using SteamCN.Core.Models;

namespace SteamCN.Core.Pipeline;

public class AnchorUpdateResult
{
	public required IReadOnlyList<TranslationAnchor> NewAnchors { get; init; }

	public required IReadOnlyList<TranslationAnchor> MovedAnchors { get; init; }

	public required IReadOnlyList<TranslationAnchor> ExpiredAnchors { get; init; }

	public required IReadOnlyList<TranslationAnchor> ActiveAnchors { get; init; }

	public bool HasChanges
	{
		get
		{
			if (NewAnchors.Count <= 0 && MovedAnchors.Count <= 0)
			{
				return ExpiredAnchors.Count > 0;
			}
			return true;
		}
	}
}
