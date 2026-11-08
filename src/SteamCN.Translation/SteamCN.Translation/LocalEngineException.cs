using System;

namespace SteamCN.Translation;

public class LocalEngineException : Exception
{
	public LocalEngineErrorType ErrorType { get; }

	public LocalEngineException(string message, LocalEngineErrorType errorType)
		: base(message)
	{
		ErrorType = errorType;
	}

	public LocalEngineException(string message, LocalEngineErrorType errorType, Exception innerException)
		: base(message, innerException)
	{
		ErrorType = errorType;
	}

	public string GetUserFriendlyMessage()
	{
		return ErrorType switch
		{
			LocalEngineErrorType.BridgeScriptMissing => "翻译桥接脚本缺失或损坏。请点击「修复本地翻译引擎」从程序内置资源恢复。", 
			LocalEngineErrorType.PythonRuntimeMissing => "Python 运行环境缺失。请点击「修复本地翻译引擎」重新下载安装。需要网络连接。", 
			LocalEngineErrorType.PythonDependencyFailed => "翻译组件（argostranslate）加载失败。请点击「修复本地翻译引擎」重新安装依赖。", 
			LocalEngineErrorType.NetworkError => "网络连接失败，无法下载所需组件。请检查网络后重试。", 
			LocalEngineErrorType.PackageDownloadFailed => "语言包下载失败。请检查网络连接后重试。", 
			LocalEngineErrorType.PackageCorrupted => "语言包文件损坏（校验不通过）。请重新下载安装。", 
			LocalEngineErrorType.ModelLoadFailed => "语言模型加载失败。请尝试重新安装语言包，或点击「修复本地翻译引擎」。", 
			LocalEngineErrorType.TranslationTestFailed => "翻译测试未通过，语言包可能不完整。请重新安装语言包。", 
			LocalEngineErrorType.PackageNotFound => "未找到所需的语言包。部分语言可能需要通过英语中转（自动安装中转包）。", 
			_ => Message, 
		};
	}

	public string GetLogLine()
	{
		return $"[LocalEngine:{ErrorType}] {Message}";
	}
}
