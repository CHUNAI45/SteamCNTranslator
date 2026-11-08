using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamCN.Translation;

public class BridgeRequest
{
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("action")]
	public string Action { get; set; } = "";

	[JsonPropertyName("texts")]
	public List<string>? Texts { get; set; }

	[JsonPropertyName("from")]
	public string? From { get; set; }

	[JsonPropertyName("to")]
	public string? To { get; set; }

	[JsonPropertyName("local_packages_dir")]
	public string? LocalPackagesDir { get; set; }
}
