using System.Text.Json.Serialization;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class CaptureSettings
{
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public CaptureMethod Preferred { get; set; }

	[JsonConverter(typeof(JsonStringEnumConverter))]
	public CaptureMethod Fallback { get; set; } = CaptureMethod.Ocr;

	public string OcrEngine { get; set; } = "windows";

	public bool OptionalOnnx { get; set; }

	public int ScrollDebounceMs { get; set; } = 300;

	public bool SkipUnchangedRegions { get; set; } = true;
}
