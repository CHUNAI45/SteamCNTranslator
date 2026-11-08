using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCN.Translation;

public class LocalEngineInstaller
{
	private const string PythonVersion = "3.11.9";

	private const string PythonDownloadUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";

	private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

	private const string ArgosPackagesDirEnv = "ARGOS_PACKAGES_DIR";

	private const string BridgeScriptMarker = "SteamCN Translator - Argos Translate Bridge";

	private readonly HttpClient _httpClient;

	private readonly string _baseDir;

	private readonly string _pythonDir;

	private readonly string _packagesDir;

	private readonly string _bridgePath;

	public string PythonExePath => Path.Combine(_pythonDir, "python.exe");

	public string BridgeScriptPath => _bridgePath;

	public string PackagesDir => _packagesDir;

	public bool IsPythonInstalled => File.Exists(PythonExePath);

	public bool IsBridgeScriptValid => ValidateBridgeScript(_bridgePath);

	public bool IsArgosInstalled
	{
		get
		{
			if (!IsPythonInstalled)
			{
				return false;
			}
			try
			{
				(int, string) tuple = RunPython("-c \"import argostranslate; print('ok')\"", 15000);
				return tuple.Item1 == 0 && tuple.Item2.Contains("ok");
			}
			catch
			{
				return false;
			}
		}
	}

	public bool IsEngineReady
	{
		get
		{
			if (IsPythonInstalled && IsBridgeScriptValid)
			{
				return IsArgosInstalled;
			}
			return false;
		}
	}

	public event Action<int, string>? ProgressChanged;

	public event Action<string>? Log;

	public static string? FindLocalPackagesDir()
	{
		string baseDirectory = AppContext.BaseDirectory;
		string[] array = new string[2]
		{
			Path.Combine(baseDirectory, "language_packages"),
			Path.Combine(baseDirectory, "resources", "language_packages")
		};
		foreach (string text in array)
		{
			try
			{
				if (Directory.Exists(text) && Directory.EnumerateFiles(text, "*.argosmodel").Any())
				{
					return text;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	public LocalEngineInstaller(HttpClient httpClient, string? baseDir = null)
	{
		_httpClient = httpClient;
		_baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator", "engine");
		_pythonDir = Path.Combine(_baseDir, "python");
		_packagesDir = Path.Combine(_baseDir, "argos_packages");
		_bridgePath = Path.Combine(_baseDir, "translate_bridge.py");
	}

	public string? GetArgosVersion()
	{
		if (!IsPythonInstalled)
		{
			return null;
		}
		try
		{
			(int, string) tuple = RunPython("-c \"from importlib.metadata import version; print(version('argostranslate'))\"", 15000);
			return (tuple.Item1 == 0) ? tuple.Item2.Trim() : null;
		}
		catch
		{
			return null;
		}
	}

	public EngineDiagnostics GetEngineDiagnostics()
	{
		EngineDiagnostics engineDiagnostics = new EngineDiagnostics
		{
			PythonInstalled = IsPythonInstalled,
			BridgeScriptValid = IsBridgeScriptValid,
			ArgosImportable = IsArgosInstalled,
			ArgosVersion = GetArgosVersion()
		};
		string text = FindLocalPackagesDir();
		if (text != null)
		{
			try
			{
				List<string> list = Directory.EnumerateFiles(text, "*.argosmodel").ToList();
				engineDiagnostics.LocalPackagesDownloaded = list.Count > 0;
				engineDiagnostics.LocalPackageCount = list.Count;
				engineDiagnostics.LocalPackagesDir = text;
			}
			catch
			{
			}
		}
		if (Directory.Exists(_packagesDir))
		{
			try
			{
				List<string> list2 = Directory.EnumerateDirectories(_packagesDir).Where((string d) =>
				{
					string fileName = Path.GetFileName(d);
					return fileName.StartsWith("translate-") || fileName.Contains('_');
				}).ToList();
				engineDiagnostics.PackagesDeployed = list2.Count > 0;
				engineDiagnostics.DeployedPackageCount = list2.Count;
				engineDiagnostics.DeployedPackages = list2.Select((string d) => Path.GetFileName(d) ?? d).ToList();
			}
			catch
			{
			}
		}
		EngineDiagnostics engineDiagnostics2 = engineDiagnostics;
		string statusText;
		if (engineDiagnostics.ArgosImportable)
		{
			statusText = $"已安装（v{engineDiagnostics.ArgosVersion ?? "?"}，{engineDiagnostics.DeployedPackageCount} 个已部署包）";
		}
		else if (engineDiagnostics.PythonInstalled)
		{
			statusText = (engineDiagnostics.PackagesDeployed ? "Python已装/翻译组件待安装" : "Python已装/组件和包待安装");
		}
		else
		{
			statusText = "未安装";
		}
		engineDiagnostics2.StatusText = statusText;
		return engineDiagnostics;
	}

	public void DeployBridgeScript()
	{
		Directory.CreateDirectory(_baseDir);
		if (TryDeployFromEmbeddedResource() || TryDeployFromFileSystem())
		{
			return;
		}
		throw new LocalEngineException("翻译桥接脚本缺失：嵌入资源和文件系统均未找到 translate_bridge.py", LocalEngineErrorType.BridgeScriptMissing);
	}

	private bool TryDeployFromEmbeddedResource()
	{
		try
		{
			Assembly assembly = typeof(LocalEngineInstaller).Assembly;
			string text = assembly.GetManifestResourceNames().FirstOrDefault((string n) => n.EndsWith("translate_bridge.py", StringComparison.OrdinalIgnoreCase));
			if (text == null)
			{
				Log?.Invoke("嵌入资源中未找到 translate_bridge.py");
				return false;
			}
			using Stream stream = assembly.GetManifestResourceStream(text);
			if (stream == null)
			{
				return false;
			}
			using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
			string text2 = streamReader.ReadToEnd();
			if (string.IsNullOrEmpty(text2) || !text2.Contains("SteamCN Translator - Argos Translate Bridge"))
			{
				Log?.Invoke("嵌入资源中的桥接脚本内容无效");
				return false;
			}
			File.WriteAllText(_bridgePath, text2, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			Log?.Invoke("桥接脚本已从嵌入资源部署: " + _bridgePath);
			return true;
		}
		catch (Exception ex)
		{
			Log?.Invoke("嵌入资源部署失败: " + ex.Message);
			return false;
		}
	}

	private bool TryDeployFromFileSystem()
	{
		string[] array = new string[2]
		{
			Path.Combine(AppContext.BaseDirectory, "resources", "translate_bridge.py"),
			Path.Combine(AppContext.BaseDirectory, "translate_bridge.py")
		};
		foreach (string text in array)
		{
			if (File.Exists(text) && ValidateBridgeScript(text))
			{
				File.Copy(text, _bridgePath, overwrite: true);
				Log?.Invoke("桥接脚本已从文件系统部署: " + text + " -> " + _bridgePath);
				return true;
			}
		}
		return false;
	}

	private static bool ValidateBridgeScript(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return false;
			}
			string text = File.ReadAllText(path, Encoding.UTF8);
			return !string.IsNullOrEmpty(text) && text.Contains("SteamCN Translator - Argos Translate Bridge");
		}
		catch
		{
			return false;
		}
	}

	public async Task InstallEngineAsync(CancellationToken ct = default(CancellationToken))
	{
		Directory.CreateDirectory(_baseDir);
		Directory.CreateDirectory(_packagesDir);
		ProgressChanged?.Invoke(2, "部署翻译桥接脚本...");
		DeployBridgeScript();
		if (!IsPythonInstalled)
		{
			ProgressChanged?.Invoke(5, "下载 Python 3.11.9 嵌入版...");
			await DownloadPythonAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			ProgressChanged?.Invoke(30, "解压 Python...");
			ExtractPython();
		}
		ProgressChanged?.Invoke(35, "配置 Python 环境...");
		EnableSitePackages();
		ProgressChanged?.Invoke(40, "安装 pip...");
		await InstallPipAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		ProgressChanged?.Invoke(50, "安装 argostranslate（约100MB，请耐心等待）...");
		await PipInstallAsync("argostranslate", ct).ConfigureAwait(continueOnCapturedContext: false);
		ProgressChanged?.Invoke(95, "验证安装...");
		if (!IsArgosInstalled)
		{
			throw new LocalEngineException("argostranslate 安装后验证失败（import 测试未通过）", LocalEngineErrorType.PythonDependencyFailed);
		}
		ProgressChanged?.Invoke(100, "本地引擎安装完成");
	}

	private async Task DownloadPythonAsync(CancellationToken ct)
	{
		string zipPath = Path.Combine(_baseDir, "python-embed.zip");
		if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 1000000)
		{
			Log?.Invoke("使用已缓存的 Python 压缩包");
			return;
		}
		try
		{
			using HttpResponseMessage resp = await _httpClient.GetAsync("https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			resp.EnsureSuccessStatusCode();
			long totalBytes = resp.Content.Headers.ContentLength ?? 12000000;
			await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			await using FileStream file = File.Create(zipPath);
			byte[] buffer = new byte[81920];
			long read = 0L;
			while (true)
			{
				int num;
				int n = (num = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(continueOnCapturedContext: false));
				if (num <= 0)
				{
					break;
				}
				await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(continueOnCapturedContext: false);
				read += n;
				int arg = 5 + (int)(read * 25 / totalBytes);
				ProgressChanged?.Invoke(arg, $"下载 Python... {read / 1024}KB");
			}
		}
		catch (HttpRequestException ex)
		{
			throw new LocalEngineException("下载 Python 嵌入版失败: " + ex.Message, LocalEngineErrorType.NetworkError, ex);
		}
		catch (TaskCanceledException innerException) when (!ct.IsCancellationRequested)
		{
			throw new LocalEngineException("下载 Python 嵌入版超时，请检查网络连接", LocalEngineErrorType.NetworkError, innerException);
		}
	}

	private void ExtractPython()
	{
		string text = Path.Combine(_baseDir, "python-embed.zip");
		if (!File.Exists(text))
		{
			throw new LocalEngineException("Python 压缩包不存在", LocalEngineErrorType.PythonRuntimeMissing);
		}
		if (Directory.Exists(_pythonDir))
		{
			Directory.Delete(_pythonDir, recursive: true);
		}
		ZipFile.ExtractToDirectory(text, _pythonDir, overwriteFiles: true);
		Log?.Invoke("Python 已解压到 " + _pythonDir);
	}

	private void EnableSitePackages()
	{
		string[] files = Directory.GetFiles(_pythonDir, "python*._pth");
		if (files.Length == 0)
		{
			throw new LocalEngineException("Python 环境配置文件（._pth）未找到，安装可能损坏", LocalEngineErrorType.PythonRuntimeMissing);
		}
		string[] array = files;
		foreach (string path in array)
		{
			string text = File.ReadAllText(path);
			if (!text.Contains("import site"))
			{
				text += "\r\nimport site";
				File.WriteAllText(path, text);
			}
		}
	}

	private async Task InstallPipAsync(CancellationToken ct)
	{
		string getPipPath = Path.Combine(_baseDir, "get-pip.py");
		if (!File.Exists(getPipPath))
		{
			try
			{
				using HttpResponseMessage resp = await _httpClient.GetAsync("https://bootstrap.pypa.io/get-pip.py", ct).ConfigureAwait(continueOnCapturedContext: false);
				resp.EnsureSuccessStatusCode();
				await using FileStream file = File.Create(getPipPath);
				await resp.Content.CopyToAsync(file, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (HttpRequestException ex)
			{
				throw new LocalEngineException("下载 get-pip.py 失败: " + ex.Message, LocalEngineErrorType.NetworkError, ex);
			}
		}
		var (num, value) = RunPython("\"" + getPipPath + "\" --no-warn-script-location", 120000);
		if (num != 0)
		{
			throw new LocalEngineException($"pip 安装失败 (exit={num}): {value}", LocalEngineErrorType.PythonDependencyFailed);
		}
		Log?.Invoke("pip 安装完成");
	}

	public async Task PipInstallAsync(string package, CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		var (num, text) = await Task.Run(() => RunPython("-m pip install " + package + " --no-warn-script-location --no-input", 600000), ct).ConfigureAwait(continueOnCapturedContext: false);
		if (num != 0)
		{
			LocalEngineErrorType errorType;
			if (text.Contains("Could not find a version") || text.Contains("No matching distribution"))
			{
				errorType = LocalEngineErrorType.PythonDependencyFailed;
			}
			else
			{
				errorType = ((text.Contains("ConnectionError") || text.Contains("timed out") || text.Contains("SSLError")) ? LocalEngineErrorType.NetworkError : LocalEngineErrorType.PythonDependencyFailed);
			}
			throw new LocalEngineException($"pip install {package} 失败 (exit={num}):\n{text}", errorType);
		}
		Log?.Invoke(package + " 安装完成");
	}

	public List<LocalEngineErrorType> VerifyComponents()
	{
		List<LocalEngineErrorType> list = new List<LocalEngineErrorType>();
		if (!IsPythonInstalled)
		{
			list.Add(LocalEngineErrorType.PythonRuntimeMissing);
		}
		if (!IsBridgeScriptValid)
		{
			list.Add(LocalEngineErrorType.BridgeScriptMissing);
		}
		if (IsPythonInstalled && !IsArgosInstalled)
		{
			list.Add(LocalEngineErrorType.PythonDependencyFailed);
		}
		return list;
	}

	public async Task RepairAsync(CancellationToken ct = default(CancellationToken))
	{
		Directory.CreateDirectory(_baseDir);
		Directory.CreateDirectory(_packagesDir);
		List<LocalEngineErrorType> issues = VerifyComponents();
		if (issues.Count == 0)
		{
			ProgressChanged?.Invoke(100, "所有组件正常，无需修复");
			return;
		}
		Log?.Invoke($"修复开始，发现 {issues.Count} 个问题: {string.Join(", ", issues)}");
		int step = 0;
		int totalSteps = issues.Count;
		if (issues.Contains(LocalEngineErrorType.BridgeScriptMissing))
		{
			step++;
			ProgressChanged?.Invoke(step * 100 / totalSteps, "恢复翻译桥接脚本...");
			DeployBridgeScript();
		}
		if (issues.Contains(LocalEngineErrorType.PythonRuntimeMissing))
		{
			step++;
			ProgressChanged?.Invoke(step * 100 / totalSteps, "重新安装 Python 运行环境...");
			await DownloadPythonAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			ExtractPython();
			EnableSitePackages();
			await InstallPipAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (issues.Contains(LocalEngineErrorType.PythonDependencyFailed))
		{
			step++;
			ProgressChanged?.Invoke(step * 100 / totalSteps, "重新安装翻译组件...");
			await PipInstallAsync("argostranslate --force-reinstall", ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		List<LocalEngineErrorType> list = VerifyComponents();
		if (list.Count > 0)
		{
			throw new LocalEngineException("修复后仍存在问题: " + string.Join(", ", list), list[0]);
		}
		ProgressChanged?.Invoke(100, "修复完成，语言包和缓存已保留");
		Log?.Invoke("修复完成");
	}

	public async Task<PackageInstallResult> InstallLanguagePackageAsync(PythonBridge bridge, string fromCode, string toCode, CancellationToken ct = default(CancellationToken))
	{
		string text = FindLocalPackagesDir();
		ProgressChanged?.Invoke(10, (text != null) ? $"从本地离线包安装 {fromCode}->{toCode}..." : $"查询并下载语言包 {fromCode}->{toCode}...");
		BridgeResponse bridgeResponse = await bridge.SendAsync(new BridgeRequest
		{
			Action = "install_package",
			From = fromCode,
			To = toCode,
			LocalPackagesDir = text
		}, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (bridgeResponse.Success)
		{
			string pivotNote = ((bridgeResponse.Pivot == true) ? (bridgeResponse.PivotNote ?? ("使用了英语中转: " + string.Join(", ", bridgeResponse.PivotPackages ?? new List<string>()))) : null);
			ProgressChanged?.Invoke(100, $"语言包 {fromCode}->{toCode} 安装完成");
			Log?.Invoke("已安装语言包: " + string.Join(", ", bridgeResponse.Installed ?? new List<string>()));
			return new PackageInstallResult
			{
				Success = true,
				InstalledPackages = (bridgeResponse.Installed ?? new List<string>()),
				IsPivot = (bridgeResponse.Pivot == true),
				PivotNote = pivotNote,
				PivotPackages = (bridgeResponse.PivotPackages ?? new List<string>())
			};
		}
		LocalEngineErrorType localEngineErrorType = MapBridgeErrorType(bridgeResponse.ErrorType, bridgeResponse.Error);
		Log?.Invoke($"语言包安装失败 [{localEngineErrorType}]: {bridgeResponse.Error}");
		return new PackageInstallResult
		{
			Success = false,
			ErrorType = localEngineErrorType,
			ErrorMessage = (bridgeResponse.Error ?? "未知错误")
		};
	}

	public async Task<List<string>> GetInstalledPackagesAsync(PythonBridge bridge, CancellationToken ct = default(CancellationToken))
	{
		return (await bridge.SendAsync(new BridgeRequest
		{
			Action = "list_packages"
		}, ct).ConfigureAwait(continueOnCapturedContext: false)).Packages ?? new List<string>();
	}

	public async Task<HealthCheckResult> CheckHealthAsync(PythonBridge bridge, CancellationToken ct = default(CancellationToken))
	{
		BridgeResponse bridgeResponse = await bridge.SendAsync(new BridgeRequest
		{
			Action = "check_health"
		}, ct).ConfigureAwait(continueOnCapturedContext: false);
		return new HealthCheckResult
		{
			BridgeOk = bridgeResponse.Success,
			ArgosInstalled = bridgeResponse.ArgosInstalled,
			ArgosVersion = bridgeResponse.ArgosVersion,
			Packages = (bridgeResponse.Packages ?? new List<string>()),
			Issues = (bridgeResponse.Issues ?? new List<string>())
		};
	}

	private static LocalEngineErrorType MapBridgeErrorType(string? bridgeErrorType, string? errorMessage)
	{
		switch (bridgeErrorType)
		{
		case "PackageNotFound":
			return LocalEngineErrorType.PackageNotFound;
		case "PackageDownloadFailed":
			return LocalEngineErrorType.PackageDownloadFailed;
		case "PackageCorrupted":
			return LocalEngineErrorType.PackageCorrupted;
		case "ModelLoadFailed":
			return LocalEngineErrorType.ModelLoadFailed;
		case "NetworkError":
			return LocalEngineErrorType.NetworkError;
		case "ArgosNotInstalled":
			return LocalEngineErrorType.PythonDependencyFailed;
		case "PackageNotInstalled":
			return LocalEngineErrorType.PackageNotFound;
		default:
			if ((errorMessage != null && errorMessage.Contains("ConnectionError")) || (errorMessage != null && errorMessage.Contains("timed out")) || (errorMessage != null && errorMessage.Contains("URLError")))
			{
				return LocalEngineErrorType.NetworkError;
			}
			return LocalEngineErrorType.Unknown;
		}
	}

	private (int exitCode, string output) RunPython(string arguments, int timeoutMs = 30000)
	{
		if (!IsPythonInstalled)
		{
			throw new LocalEngineException("Python 运行环境未安装", LocalEngineErrorType.PythonRuntimeMissing);
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo();
		processStartInfo.FileName = PythonExePath;
		processStartInfo.Arguments = arguments;
		processStartInfo.UseShellExecute = false;
		processStartInfo.CreateNoWindow = true;
		processStartInfo.RedirectStandardOutput = true;
		processStartInfo.RedirectStandardError = true;
		processStartInfo.StandardOutputEncoding = Encoding.UTF8;
		processStartInfo.StandardErrorEncoding = Encoding.UTF8;
		processStartInfo.WorkingDirectory = _baseDir;
		processStartInfo.EnvironmentVariables["ARGOS_PACKAGES_DIR"] = _packagesDir;
		using Process process = Process.Start(processStartInfo) ?? throw new LocalEngineException("无法启动 Python 进程", LocalEngineErrorType.PythonRuntimeMissing);
		Task<string> task = process.StandardOutput.ReadToEndAsync();
		Task<string> task2 = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(timeoutMs))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
			}
			throw new LocalEngineException("Python 命令超时: " + arguments, LocalEngineErrorType.PythonDependencyFailed);
		}
		string result = task.Result;
		string result2 = task2.Result;
		string item = (string.IsNullOrEmpty(result2) ? result : (result + "\n" + result2));
		return (exitCode: process.ExitCode, output: item);
	}

	public Dictionary<string, string> GetBridgeEnvironment()
	{
		return new Dictionary<string, string> { ["ARGOS_PACKAGES_DIR"] = _packagesDir };
	}
}
