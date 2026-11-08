namespace SteamCN.Translation;

public enum LocalEngineErrorType
{
	None,
	BridgeScriptMissing,
	PythonRuntimeMissing,
	PythonDependencyFailed,
	NetworkError,
	PackageDownloadFailed,
	PackageCorrupted,
	ModelLoadFailed,
	TranslationTestFailed,
	PackageNotFound,
	Unknown
}
