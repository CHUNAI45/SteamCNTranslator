using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamCN.Translation;

public class BridgeResponse
{
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("success")]
	public bool Success { get; set; }

	[JsonPropertyName("translations")]
	public List<string>? Translations { get; set; }

	[JsonPropertyName("packages")]
	public List<string>? Packages { get; set; }

	[JsonPropertyName("error")]
	public string? Error { get; set; }

	[JsonPropertyName("error_type")]
	public string? ErrorType { get; set; }

	[JsonPropertyName("package")]
	public string? Package { get; set; }

	[JsonPropertyName("installed")]
	public List<string>? Installed { get; set; }

	[JsonPropertyName("pong")]
	public bool Pong { get; set; }

	[JsonPropertyName("pivot")]
	public bool? Pivot { get; set; }

	[JsonPropertyName("pivot_note")]
	public string? PivotNote { get; set; }

	[JsonPropertyName("pivot_packages")]
	public List<string>? PivotPackages { get; set; }

	[JsonPropertyName("bridge_ok")]
	public bool BridgeOk { get; set; }

	[JsonPropertyName("argos_installed")]
	public bool ArgosInstalled { get; set; }

	[JsonPropertyName("argos_version")]
	public string? ArgosVersion { get; set; }

	[JsonPropertyName("issues")]
	public List<string>? Issues { get; set; }

	[JsonPropertyName("inference_ms")]
	public long? InferenceMs { get; set; }

	[JsonIgnore]
	public long RoundTripMs { get; set; }

	[JsonIgnore]
	public bool RecoveredAfterRestart { get; set; }
}
