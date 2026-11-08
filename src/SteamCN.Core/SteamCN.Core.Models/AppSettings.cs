using System.Text.Json.Serialization;
using SteamCN.Core.Enums;

namespace SteamCN.Core.Models;

public class AppSettings
{
	public string AppName { get; set; } = "SteamCN Translator";

	public string Version { get; set; } = "1.0";

	public string Mode { get; set; } = "external";

	public string TargetApp { get; set; } = "Steam";

	public bool AutoDetectSteam { get; set; } = true;

	public bool AutoTranslate { get; set; } = true;

	[JsonConverter(typeof(JsonStringEnumConverter))]
	public DisplayMode DisplayMode { get; set; } = DisplayMode.Bilingual;

	public string TargetLanguage { get; set; } = "zh-Hans";

	public AreaSettings Areas { get; set; } = new AreaSettings();

	public bool SmartScroll { get; set; } = true;

	public bool TranslationMemory { get; set; } = true;

	public bool Glossary { get; set; } = true;

	public CaptureSettings Capture { get; set; } = new CaptureSettings();

	public OverlaySettings Overlay { get; set; } = new OverlaySettings();

	public TranslationSettings Translation { get; set; } = new TranslationSettings();

	public CompatibilitySettings Compatibility { get; set; } = new CompatibilitySettings();

	public PrivacySettings Privacy { get; set; } = new PrivacySettings();

	public StartupSettings Startup { get; set; } = new StartupSettings();

	public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

	public FloatingBallSettings FloatingBall { get; set; } = new FloatingBallSettings();
}
