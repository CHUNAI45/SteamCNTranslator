using System.Collections.Generic;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public class GlossaryProcessResult
{
	public required string ProtectedText { get; init; }

	public required IReadOnlyDictionary<string, GlossaryTerm> Placeholders { get; init; }

	public bool HasProtectedTerms => Placeholders.Count > 0;
}
