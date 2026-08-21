using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexPulse.Services;

internal sealed class AppServerProtocolException : Exception
{
    public AppServerProtocolException(string message) : base(message)
    {
    }
}

internal sealed class AppServerClient : IDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private int _nextRequestId;
    private bool _initialized;
    private bool _disposed;

    public string? CodexHome { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => _process is { HasExited: false } && _initialized;

    public event Action<JsonElement>? NotificationReceived;

    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return true;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return true;
            }

            StopProcess();
            var executable = ResolveCodexExecutable();
            if (executable is null)
            {
                LastError = "未找到 codex CLI；可设置 CODEX_PULSE_CODEX_PATH。";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += Process_OutputDataReceived;
            process.ErrorDataReceived += Process_ErrorDataReceived;
            process.Exited += Process_Exited;

            if (!process.Start())
            {
                process.Dispose();
                LastError = "无法启动 codex app-server。";
                return false;
            }

            _process = process;
            _initialized = false;
            LastError = null;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var initializeResult = await SendRequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new
                        {
                            name = "codex_pulse",
                            title = "Codex Pulse",
                            version = "0.1.0"
                        },
                        capabilities = new
                        {
                            experimentalApi = false
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                CodexHome = JsonHelpers.TryGetString(initializeResult, "codexHome", "codex_home");
                SendNotification("initialized", new { });
                _initialized = true;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
                StopProcess();
                return false;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (!await EnsureStartedAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AppServerProtocolException(LastError ?? "app-server 不可用");
        }

        return await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new AppServerProtocolException("app-server 进程已退出");
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        var request = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["id"] = requestId
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        try
        {
            var json = JsonSerializer.Serialize(request);
            lock (_writeGate)
            {
                if (_process is null || _process.HasExited)
                {
                    throw new AppServerProtocolException("app-server 进程已退出");
                }

                _process.StandardInput.WriteLine(json);
                _process.StandardInput.Flush();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            var response = await completion.Task.ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    private void SendNotification(string method, object parameters)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        var notification = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["method"] = method,
            ["params"] = parameters
        });

        lock (_writeGate)
        {
            process.StandardInput.WriteLine(notification);
            process.StandardInput.Flush();
        }
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.Data);
            var root = document.RootElement;
            if (JsonHelpers.TryGetProperty(root, out var idElement, "id") &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.TryGetInt32(out var requestId) &&
                _pending.TryRemove(requestId, out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = JsonHelpers.TryGetString(error, "message") ?? "app-server 请求失败";
                    completion.TrySetException(new AppServerProtocolException(message));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetResult(JsonDocument.Parse("{}").RootElement.Clone());
                }

                return;
            }

            NotificationReceived?.Invoke(root.Clone());
        }
        catch (JsonException)
        {
            LastError = "app-server 返回了无法解析的 JSONL。";
        }
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data) &&
            !e.Data.Contains("warm featured plugin", StringComparison.OrdinalIgnoreCase))
        {
            LastError = e.Data;
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        _initialized = false;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new AppServerProtocolException("app-server 进程已退出"));
        }

        _pending.Clear();
    }

    private static string? ResolveCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_PULSE_CODEX_PATH") ??
                         Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var candidate in new[] { "codex.cmd", "codex.exe", "codex" })
        {
            try
            {
                using var probe = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = candidate,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                probe.Start();
                var result = probe.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                probe.WaitForExit(1500);
                var path = result.FirstOrDefault(value => File.Exists(value.Trim()));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path.Trim();
                }
            }
            catch
            {
                // Try the next executable name.
            }
        }

        return null;
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _initialized = false;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // Process teardown is best-effort.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _startGate.Dispose();
    }
}
