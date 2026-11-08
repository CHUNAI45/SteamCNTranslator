using System.Collections.Generic;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public class TextRegionUpdateResult
{
	public required IReadOnlyList<TextFragment> Added { get; init; }

	public required IReadOnlyList<TextFragment> Unchanged { get; init; }

	public required IReadOnlyList<TextFragment> Removed { get; init; }

	public required IReadOnlyList<TextFragment> Moved { get; init; }

	public bool HasChanges
	{
		get
		{
			if (Added.Count <= 0 && Removed.Count <= 0)
			{
				return Moved.Count > 0;
			}
			return true;
		}
	}
}
