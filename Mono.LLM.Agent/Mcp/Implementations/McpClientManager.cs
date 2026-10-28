// ============================================================================
// 文件：McpClientManager.cs
// 用途：管理一组 MCP 服务器的连接与工具注册。
// 实现方：McpClientManager
// 调用方：DI 装配、应用启动（StartAsync）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 一个已配置服务器的连接状态快照。
/// </summary>
/// <remarks>
/// 初始化失败的服务器也会保留一条记录（<see cref="Error"/> 非空），
/// 而不是被静默丢弃——排查配置问题时，看得见失败比「少了一个服务器」有用得多。
/// </remarks>
public sealed class McpServerConnection
{
    /// <summary>服务器名称。</summary>
    public required string Name { get; init; }

    /// <summary>已连接的客户端；初始化失败时为 <c>null</c>。</summary>
    public McpClient? Client { get; internal set; }

    /// <summary>握手结果；初始化失败时为 <c>null</c>。</summary>
    public McpInitializeResult? InitializeResult { get; internal set; }

    /// <summary>工具桥接；未配置工具管理器或初始化失败时为 <c>null</c>。</summary>
    public McpToolBridge? Bridge { get; internal set; }

    /// <summary>本次连接使用的令牌提供者（启用 OAuth 时非空），由管理器负责释放。</summary>
    internal IMcpTokenProvider? TokenProvider { get; set; }

    /// <summary>本次注册的工具名称。</summary>
    public IReadOnlyList<string> ToolNames { get; internal set; } = Array.Empty<string>();

    /// <summary>初始化失败的原因；成功时为 <c>null</c>。</summary>
    public string? Error { get; internal set; }

    /// <summary>完成握手的时间。</summary>
    public DateTimeOffset? ConnectedAt { get; internal set; }

    /// <summary>是否已连接。</summary>
    public bool IsConnected => Client?.IsConnected == true;
}

/// <summary>
/// 管理一组 MCP 服务器的连接与工具注册。
/// </summary>
/// <remarks>
/// <para>
/// 典型用法：注册配置 → <see cref="StartAsync"/> 一次完成全部服务器的握手与工具桥接。
/// 单个服务器失败不会影响其他服务器，失败原因记录在对应 <see cref="McpServerConnection.Error"/> 上。
/// </para>
/// <para>
/// 服务端推送 <c>tools/list_changed</c> 时，如果配置开启了 <see cref="McpServerConfig.AutoRefreshTools"/>，
/// 管理器会在后台线程重新同步工具列表，避免模型调用已经不存在的工具。
/// </para>
/// </remarks>
public sealed class McpClientManager : IAsyncDisposable
{
    private readonly IReadOnlyList<McpServerConfig> _configs;
    private readonly ToolManager? _toolManager;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<McpClientManager>? _logger;
    private readonly List<McpServerConnection> _connections = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private int _started;
    private int _disposed;

    /// <summary>初始化管理器。</summary>
    /// <param name="servers">服务器配置集合。</param>
    /// <param name="toolManager">目标工具管理器；为 <c>null</c> 时只连接、不注册工具。</param>
    /// <param name="loggerFactory">可选的日志工厂。</param>
    public McpClientManager(
        IEnumerable<McpServerConfig> servers,
        ToolManager? toolManager = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(servers);

        _configs = servers.ToList();
        _toolManager = toolManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpClientManager>();
    }

    /// <summary>全部服务器配置（含未启用的）。</summary>
    public IReadOnlyList<McpServerConfig> Configs => _configs;

    /// <summary>已尝试连接的服务器状态，顺序与配置一致。</summary>
    public IReadOnlyList<McpServerConnection> Connections => _connections;

    /// <summary>已成功注册的 MCP 工具总数。</summary>
    public int TotalToolCount => _connections.Where(c => c.IsConnected).Sum(c => c.ToolNames.Count);

    /// <summary>已连接成功的服务器数量。</summary>
    public int ConnectedServerCount => _connections.Count(c => c.IsConnected);

    /// <summary>
    /// 依次连接所有启用的服务器，完成握手并注册工具。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 重复调用是安全的：只有第一次会真正建立连接。单个服务器抛出的异常会被捕获并记录，
    /// 因此本方法本身只在取消时抛出 <see cref="OperationCanceledException"/>。
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Volatile.Read(ref _started) != 0)
            return;

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) != 0)
                return;

            foreach (var config in _configs)
            {
                if (!config.Enabled)
                {
                    _logger?.LogDebug("MCP 服务器「{Name}」已禁用，跳过连接。", config.Name);
                    continue;
                }

                var connection = new McpServerConnection { Name = config.Name };
                _connections.Add(connection);

                await ConnectServerAsync(config, connection, cancellationToken).ConfigureAwait(false);
            }

            // 只有完整走完一轮才标记为已启动，否则取消后无法重试。
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>关闭所有连接。</summary>
    /// <returns>表示异步关闭的任务。</returns>
    public async Task StopAsync()
    {
        foreach (var connection in _connections)
        {
            connection.Bridge?.RemoveAll();

            if (connection.Client is { } client)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "关闭 MCP 服务器「{Name}」时出错。", connection.Name);
                }
            }

            connection.Client = null;
            connection.Bridge = null;
            connection.ToolNames = Array.Empty<string>();

            await DisposeTokenProviderAsync(connection).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }

    private async Task ConnectServerAsync(
        McpServerConfig config,
        McpServerConnection connection,
        CancellationToken cancellationToken)
    {
        McpClient? client = null;
        McpOAuthTokenProvider? tokenProvider = null;

        try
        {
            if (config.OAuth is { } oauth)
            {
                if (config.Endpoint is null)
                {
                    throw new InvalidOperationException(
                        $"MCP 服务器「{config.Name}」启用了 OAuth，必须配置 {nameof(McpServerConfig.Endpoint)}。");
                }

                tokenProvider = new McpOAuthTokenProvider(
                    config.Name,
                    config.Endpoint,
                    oauth,
                    _loggerFactory?.CreateLogger<McpOAuthTokenProvider>());

                connection.TokenProvider = tokenProvider;
            }

            var transport = config.CreateTransport(_loggerFactory, tokenProvider);
            client = new McpClient(
                transport,
                config.BuildClientOptions(),
                _loggerFactory?.CreateLogger<McpClient>());

            var initialize = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            connection.Client = client;
            connection.InitializeResult = initialize;
            connection.ConnectedAt = DateTimeOffset.UtcNow;

            if (_toolManager is not null)
            {
                var bridge = new McpToolBridge(_toolManager, client, config.BuildBridgeOptions());
                connection.Bridge = bridge;
                connection.ToolNames = await bridge.RegisterToolsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (config.AutoRefreshTools && connection.Bridge is { } boundBridge)
                client.ListChanged += (_, args) => OnListChanged(boundBridge, args);

            _logger?.LogInformation(
                "MCP 服务器「{Name}」就绪，注册工具 {Count} 个。",
                config.Name,
                connection.ToolNames.Count);
        }
        catch (OperationCanceledException)
        {
            connection.Error = "初始化被取消。";
            await DisposeClientAsync(client, config.Name).ConfigureAwait(false);
            await DisposeTokenProviderAsync(connection).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            connection.Error = ex.Message;
            connection.Client = null;

            _logger?.LogError(ex, "MCP 服务器「{Name}」初始化失败：{Message}", config.Name, ex.Message);

            await DisposeClientAsync(client, config.Name).ConfigureAwait(false);
            await DisposeTokenProviderAsync(connection).ConfigureAwait(false);
        }
    }

    private void OnListChanged(McpToolBridge bridge, McpListChangedEventArgs args)
    {
        if (args.Kind != McpListKind.Tools)
            return;

        // 连续收到多条 list_changed 时只跑一次刷新即可，重复拉取没有意义。
        if (!_refreshGate.Wait(0))
        {
            _logger?.LogDebug("MCP 服务器「{Name}」已有刷新任务在执行，跳过本次通知。", args.ServerName);
            return;
        }

        // 事件在客户端的读取循环里触发，直接 await 刷新会等一个永远读不到的响应，因此放到后台线程。
        _ = Task.Run(async () =>
        {
            try
            {
                var names = await bridge.RefreshAsync().ConfigureAwait(false);
                _logger?.LogInformation(
                    "MCP 服务器「{Name}」的工具列表已更新，当前 {Count} 个。",
                    args.ServerName,
                    names.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex, "刷新 MCP 服务器「{Name}」的工具列表失败。", args.ServerName);
            }
            finally
            {
                _refreshGate.Release();
            }
        });
    }

    private async Task DisposeClientAsync(McpClient? client, string serverName)
    {
        if (client is null)
            return;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放 MCP 服务器「{Name}」的客户端时出错。", serverName);
        }
    }

    private async Task DisposeTokenProviderAsync(McpServerConnection connection)
    {
        if (connection.TokenProvider is IAsyncDisposable disposable)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "释放 MCP 服务器「{Name}」的令牌提供者时出错。", connection.Name);
            }
        }

        connection.TokenProvider = null;
    }
}
