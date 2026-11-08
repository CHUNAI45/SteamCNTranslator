using System.Text.Json.Serialization;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class OverlaySettings
{
	public bool Enabled { get; set; } = true;

	public bool ClickThrough { get; set; } = true;

	public bool FollowWindow { get; set; } = true;

	public bool HideWhenUnfocused { get; set; }

	public bool AvoidInteractiveElements { get; set; } = true;

	public string LayoutFallback { get; set; } = "side_panel";

	[JsonConverter(typeof(JsonStringEnumConverter))]
	public LayoutMode LayoutMode { get; set; } = LayoutMode.Adaptive;

	public double OverlayOpacity { get; set; } = 0.82;

	public double FontScale { get; set; } = 1.0;

	public int MaxTranslationWidth { get; set; } = 420;

	public bool ShowOriginal { get; set; } = true;

	public int BilingualGap { get; set; } = 4;
}
