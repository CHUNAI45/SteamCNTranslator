using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCN.Translation;

public sealed class PythonBridge : IAsyncDisposable
{
	private Process? _process;

	private StreamReader? _stdout;

	private StreamWriter? _stdin;

	private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

	private readonly string _pythonExePath;

	private readonly string _bridgeScriptPath;

	private int _requestCounter;

	private int _restartCount;

	private bool _disposed;

	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public Dictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();

	public bool IsRunning
	{
		get
		{
			Process process = _process;
			if (process != null)
			{
				return !process.HasExited;
			}
			return false;
		}
	}

	public int RestartCount => _restartCount;

	public event Action<string>? LogReceived;

	public PythonBridge(string pythonExePath, string bridgeScriptPath)
	{
		_pythonExePath = pythonExePath;
		_bridgeScriptPath = bridgeScriptPath;
	}

	public Task StartAsync(CancellationToken ct = default(CancellationToken))
	{
		if (IsRunning)
		{
			return Task.CompletedTask;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = _pythonExePath,
			Arguments = "\"" + _bridgeScriptPath + "\"",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WorkingDirectory = (Path.GetDirectoryName(_bridgeScriptPath) ?? Environment.CurrentDirectory)
		};
		processStartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
		processStartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
		processStartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
		foreach (KeyValuePair<string, string> environmentVariable in EnvironmentVariables)
		{
			processStartInfo.EnvironmentVariables[environmentVariable.Key] = environmentVariable.Value;
		}
		_process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("无法启动 Python 进程");
		_stdin = _process.StandardInput;
		_stdout = _process.StandardOutput;
		Task.Run((Func<Task?>)ReadStderrAsync, ct);
		return Task.CompletedTask;
	}

	public async Task<BridgeResponse> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
	{
		request.Id = Interlocked.Increment(ref _requestCounter).ToString();
		for (int attempt = 0; attempt < 2; attempt++)
		{
			if (!IsRunning)
			{
				await EnsureRunningAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			await _ioLock.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				Stopwatch sw = Stopwatch.StartNew();
				string text = JsonSerializer.Serialize(request, JsonOpts);
				await _stdin.WriteLineAsync(text.AsMemory(), ct).ConfigureAwait(continueOnCapturedContext: false);
				await _stdin.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				string text2 = await _stdout.ReadLineAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				sw.Stop();
				if (text2 == null)
				{
					throw new IOException("Python 进程已关闭输出流");
				}
				BridgeResponse bridgeResponse = JsonSerializer.Deserialize<BridgeResponse>(text2, JsonOpts) ?? throw new InvalidDataException("无法解析响应: " + text2);
				bridgeResponse.RoundTripMs = sw.ElapsedMilliseconds;
				if (attempt > 0)
				{
					bridgeResponse.RecoveredAfterRestart = true;
				}
				return bridgeResponse;
			}
			catch (IOException) when (attempt == 0)
			{
				_restartCount++;
				LogReceived?.Invoke($"[Bridge] IO异常，自动重启 Python 进程（第 {_restartCount} 次）");
				await EnsureRunningAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				_ioLock.Release();
			}
		}
		throw new InvalidOperationException("Python 进程通信失败（已尝试自动重启）");
	}

	public async Task<bool> PingAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			return (await SendAsync(new BridgeRequest
			{
				Action = "ping"
			}, ct).ConfigureAwait(continueOnCapturedContext: false)).Success;
		}
		catch
		{
			return false;
		}
	}

	public async Task EnsureRunningAsync(CancellationToken ct = default(CancellationToken))
	{
		if (!IsRunning)
		{
			await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
			await StartAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task ReadStderrAsync()
	{
		try
		{
			while (true)
			{
				Process process = _process;
				if (process != null && !process.HasExited && !_disposed)
				{
					string text = await _process.StandardError.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false);
					if (text != null)
					{
						LogReceived?.Invoke(text);
						continue;
					}
					break;
				}
				break;
			}
		}
		catch
		{
		}
	}

	public Task StopAsync()
	{
		try
		{
			if (_stdin != null)
			{
				try
				{
					_stdin.Close();
				}
				catch
				{
				}
			}
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(3000);
			}
		}
		catch
		{
		}
		finally
		{
			_process?.Dispose();
			_process = null;
			_stdin = null;
			_stdout = null;
		}
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		_disposed = true;
		await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
		_ioLock.Dispose();
	}
}
