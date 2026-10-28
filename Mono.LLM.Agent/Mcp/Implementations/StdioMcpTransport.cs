// ============================================================================
// 文件：StdioMcpTransport.cs
// 用途：基于子进程标准输入输出的 MCP 传输。
// 实现方：StdioMcpTransport
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// stdio 传输的配置。
/// </summary>
public sealed class McpStdioTransportOptions
{
    /// <summary>启动命令，可以是绝对路径、相对路径或 PATH 中的命令名（如 <c>npx</c>、<c>uvx</c>）。</summary>
    public required string Command { get; init; }

    /// <summary>命令行参数。</summary>
    public IList<string> Arguments { get; init; } = new List<string>();

    /// <summary>附加的环境变量，会与当前进程的环境合并（同名覆盖）。</summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>子进程的工作目录；为 <c>null</c> 时继承当前进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>传输名称，用于日志与错误信息。</summary>
    public string Name { get; init; } = "stdio";

    /// <summary>关闭时等待子进程自行退出的时长，超时后强制结束进程树。默认 2 秒。</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// 基于子进程标准输入输出的 MCP 传输。
/// </summary>
/// <remarks>
/// <para>
/// 这是本地 MCP 服务器的标准接入方式：客户端拉起子进程，把 JSON-RPC 请求写进 stdin，
/// 从 stdout 逐行读回响应。stderr 不承载协议消息，本实现把它按行收集并写入日志，
/// 同时保留最后若干行，用于进程异常退出时给出可诊断的原因。
/// </para>
/// <para>
/// 设计要点：
/// </para>
/// <list type="bullet">
///   <item>写入串行化 —— 多个并发请求共享同一个 stdin，必须用信号量保护，否则会出现半行交错；</item>
///   <item>换行固定为 <c>\n</c> —— 客户端与服务端可能跨平台，不能依赖 <c>Environment.NewLine</c>；</item>
///   <item>进程退出即失败 —— 立即把 <see cref="Closed"/> 抛出，让上层把挂起的请求快速失败，而不是等到超时。</item>
/// </list>
/// <para>
/// 该类不是线程安全的启动器：<see cref="StartAsync"/> 只会真正执行一次，重复调用会被忽略。
/// </para>
/// </remarks>
public sealed class StdioMcpTransport : IMcpTransport
{
    private const int StderrTailCapacity = 20;

    private readonly McpStdioTransportOptions _options;
    private readonly ILogger<StdioMcpTransport>? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCts = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly object _stderrLock = new();

    private Process? _process;
    private Stream? _stdin;
    private StreamReader? _stdout;
    private Task? _readLoop;
    private Task? _stderrLoop;
    private int _started;
    private int _closed;

    /// <summary>初始化 stdio 传输。</summary>
    /// <param name="options">进程启动配置。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public StdioMcpTransport(McpStdioTransportOptions options, ILogger<StdioMcpTransport>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public Func<JsonRpcMessage, Task>? MessageHandler { get; set; }

    /// <inheritdoc />
    /// <remarks>stdio 传输不携带协议版本，该值仅用于诊断。</remarks>
    public string? ProtocolVersion { get; set; }

    /// <summary>子进程的进程号；未启动时为 <c>null</c>。</summary>
    public int? ProcessId => _process?.Id;

    /// <inheritdoc />
    public event EventHandler<McpTransportClosedEventArgs>? Closed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        var startInfo = BuildStartInfo();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start 返回了 false。");
        }
        catch (Exception ex)
        {
            process.Dispose();
            Interlocked.Exchange(ref _started, 0);
            throw new McpConnectionClosedException(
                $"无法启动 MCP 服务器「{Name}」（{startInfo.FileName}）：{ex.Message}", ex);
        }

        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput;
        _readLoop = Task.Run(() => ReadLoopAsync(_stdout), CancellationToken.None);
        _stderrLoop = Task.Run(() => ReadStderrAsync(process), CancellationToken.None);

        _logger?.LogInformation(
            "MCP 服务器「{Name}」已启动：{FileName} (PID {Pid})",
            Name, startInfo.FileName, process.Id);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var stdin = _stdin
                    ?? throw new McpConnectionClosedException($"MCP 服务器「{Name}」尚未启动。");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closed) != 0)
                throw new McpConnectionClosedException($"MCP 服务器「{Name}」的连接已关闭。");

            await JsonRpcFraming.WriteAsync(stdin, message, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new McpConnectionClosedException($"向 MCP 服务器「{Name}」写入消息失败：{ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new McpConnectionClosedException($"MCP 服务器「{Name}」的标准输入已关闭。", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
            return;

        RaiseClosed(new McpTransportClosedEventArgs { Reason = $"MCP 服务器「{Name}」的传输已关闭。" });

        // 先关闭 stdin：规范约定子进程应据此退出。直接 Kill 会跳过它的清理逻辑。
        try
        {
            _stdin?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "关闭 MCP 服务器「{Name}」的 stdin 时出错。", Name);
        }

        try
        {
            if (!process.HasExited)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(_options.ShutdownTimeout);

                try
                {
                    await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 宽限期结束仍未退出，下面强制结束。
                }
            }

            if (!process.HasExited)
            {
                _logger?.LogWarning("MCP 服务器「{Name}」未在宽限期内退出，强制结束进程树。", Name);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "结束 MCP 服务器「{Name}」时出错。", Name);
        }
        finally
        {
            _process = null;

            try
            {
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "释放 MCP 服务器「{Name}」的进程对象时出错。", Name);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _readCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        await CloseAsync().ConfigureAwait(false);

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_stderrLoop is not null)
        {
            try
            {
                await _stderrLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _readCts.Dispose();
        _writeLock.Dispose();
    }

    private ProcessStartInfo BuildStartInfo()
    {
        ExecutableResolution resolution;
        try
        {
            resolution = ExecutableResolver.Resolve(_options.Command);
        }
        catch (Exception ex)
        {
            throw new McpConnectionClosedException($"解析 MCP 服务器「{Name}」的启动命令失败：{ex.Message}", ex);
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = JsonRpcFraming.Utf8NoBom,
            StandardErrorEncoding = JsonRpcFraming.Utf8NoBom
        };

        if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
            startInfo.WorkingDirectory = _options.WorkingDirectory!;

        if (resolution.RequiresCommandShell)
        {
            startInfo.FileName = resolution.CommandShellPath;

            foreach (var argument in ExecutableResolver.BuildCommandShellArguments(resolution, _options.Arguments))
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.FileName = resolution.ExecutablePath;

            foreach (var argument in _options.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in _options.EnvironmentVariables)
            startInfo.Environment[pair.Key] = pair.Value;

        return startInfo;
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        try
        {
            while (!_readCts.IsCancellationRequested)
            {
                JsonRpcMessage? message;

                try
                {
                    message = await JsonRpcFraming.ReadAsync(reader, _readCts.Token).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(
                        ex, "MCP 服务器「{Name}」输出了无法解析为 JSON-RPC 的内容，已跳过该行。", Name);
                    continue;
                }

                if (message is null)
                    break;

                await DispatchAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            RaiseClosed(new McpTransportClosedEventArgs
            {
                Reason = $"读取 MCP 服务器「{Name}」的输出失败：{ex.Message}",
                Exception = ex
            });
            return;
        }

        RaiseClosed(new McpTransportClosedEventArgs
        {
            Reason = $"MCP 服务器「{Name}」的标准输出已关闭。",
            ExitCode = TryGetExitCode()
        });
    }

    private async Task DispatchAsync(JsonRpcMessage message)
    {
        var handler = MessageHandler;
        if (handler is null)
            return;

        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 处理器内部的异常不应中断读取循环，否则一个坏消息会让整条连接失联。
            _logger?.LogError(ex, "处理 MCP 服务器「{Name}」的消息时出错。", Name);
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            while (!_readCts.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(_readCts.Token).ConfigureAwait(false);
                if (line is null)
                    return;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                lock (_stderrLock)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrTailCapacity)
                        _stderrTail.Dequeue();
                }

                _logger?.LogDebug("MCP[{Name}] stderr: {Line}", Name, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取 MCP 服务器「{Name}」的 stderr 时出错。", Name);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = TryGetExitCode();
        var tail = SnapshotStderr();

        RaiseClosed(new McpTransportClosedEventArgs
        {
            ExitCode = exitCode,
            Reason = tail.Length == 0
                ? $"MCP 服务器「{Name}」的进程已退出（退出码 {exitCode}）。"
                : $"MCP 服务器「{Name}」的进程已退出（退出码 {exitCode}）。stderr 末尾：{tail}"
        });
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process is { HasExited: true } process ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string SnapshotStderr()
    {
        lock (_stderrLock)
        {
            return _stderrTail.Count == 0 ? string.Empty : string.Join(" | ", _stderrTail);
        }
    }

    private void RaiseClosed(McpTransportClosedEventArgs args)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _logger?.LogDebug("MCP 服务器「{Name}」的传输已关闭：{Reason}", Name, args.Reason);

        try
        {
            Closed?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理 MCP 服务器「{Name}」的关闭事件时出错。", Name);
        }
    }
}
