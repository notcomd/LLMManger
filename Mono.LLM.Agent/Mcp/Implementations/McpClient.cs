// ============================================================================
// 文件：McpClient.cs
// 用途：标准 MCP 客户端：负责握手、请求/响应配对、能力发现与通知分发。
// 实现方：McpClient
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 标准 MCP 客户端：负责握手、请求/响应配对、能力发现与通知分发。
/// </summary>
/// <remarks>
/// <para>
/// 协议交互的完整流程：
/// </para>
/// <list type="number">
///   <item>调用 <see cref="ConnectAsync"/> 发送 <c>initialize</c>，声明客户端能力并协商协议版本；</item>
///   <item>收到响应后校验版本，发送 <c>notifications/initialized</c>，握手完成；</item>
///   <item>按需拉取工具、资源、提示词，或直接调用工具；</item>
///   <item>服务端下发的 <c>list_changed</c>、日志、进度通知通过事件暴露。</item>
/// </list>
/// <para>
/// 并发模型：所有请求共用一个读取循环，靠自增 <c>id</c> 与 <see cref="TaskCompletionSource{TResult}"/>
/// 做配对；因此单个客户端可以同时发起多个请求。超时与取消都会向服务端补发
/// <c>notifications/cancelled</c>，让服务端有机会释放资源。
/// </para>
/// <para>
/// 版本兼容：2024-11-05 没有 <c>MCP-Protocol-Version</c> 请求头与结构化输出，
/// 本实现对旧版本只降级「能力声明」与「响应解析」，请求方法名完全一致。
/// </para>
/// </remarks>
public sealed class McpClient : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly McpClientOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private McpInitializeResult? _initializeResult;
    private int _nextRequestId;
    private int _disposed;

    /// <summary>初始化客户端。</summary>
    /// <param name="transport">传输实现，负责实际的字节收发。</param>
    /// <param name="options">行为配置；为 <c>null</c> 时使用默认值。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public McpClient(IMcpTransport transport, McpClientOptions? options = null, ILogger? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new McpClientOptions();
        _logger = logger;
    }

    /// <summary>服务器名称（取自传输层配置）。</summary>
    public string Name => _transport.Name;

    /// <summary>是否已完成握手。</summary>
    public bool IsConnected => _initializeResult is not null;

    /// <summary>服务端实现信息；未连接时为 <c>null</c>。</summary>
    public McpImplementation? ServerInfo => _initializeResult?.ServerInfo;

    /// <summary>服务端能力声明；未连接时为 <c>null</c>。</summary>
    public McpServerCapabilities? Capabilities => _initializeResult?.Capabilities;

    /// <summary>服务端给出的使用说明；未连接或未提供时为 <c>null</c>。</summary>
    public string? Instructions => _initializeResult?.Instructions;

    /// <summary>协商后的协议版本；未连接时为 <c>null</c>。</summary>
    public string? ProtocolVersion => _initializeResult?.ProtocolVersion;

    /// <summary>服务端宣告某个列表发生变化。</summary>
    public event EventHandler<McpListChangedEventArgs>? ListChanged;

    /// <summary>已订阅资源的内容发生变化。</summary>
    public event EventHandler<McpResourceUpdatedEventArgs>? ResourceUpdated;

    /// <summary>收到服务端日志消息。</summary>
    public event EventHandler<McpLogMessageEventArgs>? LogMessage;

    /// <summary>收到长任务进度通知。</summary>
    public event EventHandler<McpProgressEventArgs>? Progress;

    /// <summary>
    /// 建立连接并完成 MCP 握手。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端的初始化响应，包含协议版本、能力与实现信息。</returns>
    /// <exception cref="McpProtocolException">服务端返回了不支持的协议版本或非法响应。</exception>
    /// <exception cref="McpConnectionClosedException">传输无法启动或握手期间连接中断。</exception>
    /// <remarks>
    /// 重复调用是安全的：握手只会执行一次，后续调用直接返回首次结果。
    /// </remarks>
    public async Task<McpInitializeResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_initializeResult is { } connected)
            return connected;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initializeResult is { } alreadyConnected)
                return alreadyConnected;

            _transport.MessageHandler = HandleMessageAsync;
            _transport.Closed += OnTransportClosed;

            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);

            var parameters = new JsonObject
            {
                ["protocolVersion"] = _options.ResolvedProtocolVersion,
                ["capabilities"] = BuildClientCapabilities(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = _options.ClientName,
                    ["version"] = _options.ResolvedClientVersion
                }
            };

            var result = await SendRequestAsync(McpMethods.Initialize, parameters, cancellationToken).ConfigureAwait(false);
            var initialize = Deserialize<McpInitializeResult>(result)
                             ?? throw new McpProtocolException("initialize 的响应体为空。");

            if (!McpProtocol.IsSupported(initialize.ProtocolVersion))
            {
                throw new McpProtocolException(
                    $"服务端要求使用 MCP 协议版本「{initialize.ProtocolVersion}」，" +
                    $"本客户端支持：{string.Join("、", McpProtocol.SupportedVersions)}。");
            }

            _transport.ProtocolVersion = initialize.ProtocolVersion;

            await SendNotificationAsync(McpMethods.InitializedNotification, null, cancellationToken)
                .ConfigureAwait(false);

            _initializeResult = initialize;

            _logger?.LogInformation(
                "MCP 服务器「{Name}」握手完成：{Server} {Version}，协议 {Protocol}，工具={Tools}，资源={Resources}，提示词={Prompts}",
                Name,
                initialize.ServerInfo.Name,
                initialize.ServerInfo.Version,
                initialize.ProtocolVersion,
                initialize.Capabilities.SupportsTools,
                initialize.Capabilities.SupportsResources,
                initialize.Capabilities.SupportsPrompts);

            return initialize;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 发送一个 JSON-RPC 请求并等待响应。
    /// </summary>
    /// <param name="method">方法名，见 <see cref="McpMethods"/>。</param>
    /// <param name="parameters">参数对象；无参数时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">本次请求的超时时间；为 <c>null</c> 时使用 <see cref="McpClientOptions.RequestTimeout"/>。</param>
    /// <returns>响应的 <c>result</c> 节点。</returns>
    /// <exception cref="McpException">服务端返回错误响应，异常携带原始错误码。</exception>
    /// <exception cref="McpRequestTimeoutException">请求超时。</exception>
    /// <remarks>
    /// 供标准方法未覆盖的扩展方法使用（例如 <c>completion/complete</c>），也便于对接厂商扩展。
    /// </remarks>
    public async Task<JsonElement> SendRequestAsync(
        string method,
        JsonNode? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var key = requestId.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[key] = completion;

        var request = JsonRpcMessage.CreateRequest(requestId, method, parameters);
        var effectiveTimeout = timeout ?? _options.RequestTimeout;

        using var timeoutCts = effectiveTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(effectiveTimeout)
            : null;

        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var token = linkedCts?.Token ?? cancellationToken;

        try
        {
            await _transport.SendAsync(request, token).ConfigureAwait(false);

            var response = await completion.Task.WaitAsync(token).ConfigureAwait(false);

            if (response.Error is { } error)
                throw new McpException(error.Code, error.Message, error.Data);

            return response.Result ?? default;
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            _ = NotifyCancelledAsync(requestId, $"请求超时（{effectiveTimeout.TotalSeconds:0.##} 秒）");
            throw new McpRequestTimeoutException(method, effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            _ = NotifyCancelledAsync(requestId, "调用方取消了请求");
            throw;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>发送通知（不期待响应）。</summary>
    /// <param name="method">方法名，通常以 <c>notifications/</c> 开头。</param>
    /// <param name="parameters">参数对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendNotificationAsync(
        string method,
        JsonNode? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _transport
            .SendAsync(JsonRpcMessage.CreateNotification(method, parameters), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>拉取服务端提供的全部工具（自动翻页）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return await CollectAsync<McpTool>(McpMethods.ToolsList, "tools", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拉取全部资源（自动翻页）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return await CollectAsync<McpResource>(McpMethods.ResourcesList, "resources", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拉取全部资源模板（自动翻页）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return await CollectAsync<McpResourceTemplate>(
            McpMethods.ResourcesTemplatesList, "resourceTemplates", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拉取全部提示词模板（自动翻页）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return await CollectAsync<McpPrompt>(McpMethods.PromptsList, "prompts", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用服务端工具。
    /// </summary>
    /// <param name="toolName">工具在服务端的原始名称。</param>
    /// <param name="arguments">参数对象；无参数时可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>工具执行结果，包含内容块与失败标记。</returns>
    /// <remarks>
    /// 服务端把业务失败放在 <see cref="McpCallToolResult.IsError"/> 里而不是协议错误，
    /// 这属于「模型可自行修正」的范畴，因此本方法不因为该标记抛异常。
    /// </remarks>
    public async Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonNode? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        EnsureConnected();

        var parameters = new JsonObject { ["name"] = toolName };
        if (arguments is not null)
            parameters["arguments"] = arguments.DeepClone();

        var result = await SendRequestAsync(
                McpMethods.ToolsCall, parameters, cancellationToken, _options.ToolCallTimeout)
            .ConfigureAwait(false);

        return Deserialize<McpCallToolResult>(result) ?? new McpCallToolResult();
    }

    /// <summary>调用服务端工具（参数以 <see cref="JsonElement"/> 传入）。</summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="arguments">参数对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var node = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : JsonNode.Parse(arguments.GetRawText());

        return CallToolAsync(toolName, node, cancellationToken);
    }

    /// <summary>读取资源内容。</summary>
    /// <param name="uri">资源 URI。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        EnsureConnected();

        var result = await SendRequestAsync(
                McpMethods.ResourcesRead, new JsonObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);

        return Deserialize<McpReadResourceResult>(result) ?? new McpReadResourceResult();
    }

    /// <summary>获取提示词模板的渲染结果。</summary>
    /// <param name="name">模板名称。</param>
    /// <param name="arguments">模板参数；无参数时为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<McpGetPromptResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureConnected();

        var parameters = new JsonObject { ["name"] = name };

        if (arguments is { Count: > 0 })
        {
            var argumentObject = new JsonObject();
            foreach (var pair in arguments)
            {
                if (pair.Value is not null)
                    argumentObject[pair.Key] = pair.Value;
            }

            parameters["arguments"] = argumentObject;
        }

        var result = await SendRequestAsync(McpMethods.PromptsGet, parameters, cancellationToken)
            .ConfigureAwait(false);

        return Deserialize<McpGetPromptResult>(result) ?? new McpGetPromptResult();
    }

    /// <summary>健康检查。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await SendRequestAsync(McpMethods.Ping, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>订阅某个资源的变更通知。</summary>
    /// <param name="uri">资源 URI。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SubscribeResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        EnsureConnected();

        await SendRequestAsync(McpMethods.ResourcesSubscribe, new JsonObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>取消资源订阅。</summary>
    /// <param name="uri">资源 URI。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnsubscribeResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        EnsureConnected();

        await SendRequestAsync(McpMethods.ResourcesUnsubscribe, new JsonObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>设置服务端日志级别。</summary>
    /// <param name="level">目标级别。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetLoggingLevelAsync(McpLogLevel level, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await SendRequestAsync(
                McpMethods.LoggingSetLevel,
                new JsonObject { ["level"] = McpLogLevels.ToProtocolString(level) },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _transport.Closed -= OnTransportClosed;
        _transport.MessageHandler = null;

        FailPending(new McpConnectionClosedException($"MCP 客户端「{Name}」已释放。"));

        try
        {
            await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "关闭 MCP 服务器「{Name}」的传输时出错。", Name);
        }

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放 MCP 服务器「{Name}」的传输时出错。", Name);
        }

        _disposeCts.Dispose();
        _connectLock.Dispose();
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_initializeResult is null)
        {
            throw new InvalidOperationException(
                $"MCP 服务器「{Name}」尚未完成初始化，请先调用 {nameof(ConnectAsync)}。");
        }
    }

    private JsonObject BuildClientCapabilities()
    {
        var capabilities = new JsonObject();

        if (_options.RootsHandler is not null)
            capabilities["roots"] = new JsonObject { ["listChanged"] = false };

        if (_options.SamplingHandler is not null)
            capabilities["sampling"] = new JsonObject();

        if (_options.ElicitationHandler is not null)
            capabilities["elicitation"] = new JsonObject();

        return capabilities;
    }

    private async Task<IReadOnlyList<T>> CollectAsync<T>(string method, string propertyName, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? cursor = null;
        var maxPages = Math.Max(1, _options.MaxPaginationPages);

        for (var page = 0; page < maxPages; page++)
        {
            var parameters = new JsonObject();
            if (!string.IsNullOrEmpty(cursor))
                parameters["cursor"] = cursor;

            var result = await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);

            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty(propertyName, out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    var item = element.Deserialize<T>(McpJson.Options);
                    if (item is not null)
                        items.Add(item);
                }
            }

            cursor = ReadString(result, "nextCursor");
            if (string.IsNullOrEmpty(cursor))
                break;
        }

        return items;
    }

    private Task HandleMessageAsync(JsonRpcMessage message)
    {
        if (message.Method is { } method)
        {
            if (message.HasId && message.Id is { } requestId)
                return HandleServerRequestAsync(requestId, method, message.Params);

            HandleNotification(method, message.Params);
            return Task.CompletedTask;
        }

        HandleResponse(message);
        return Task.CompletedTask;
    }

    private void HandleResponse(JsonRpcMessage message)
    {
        if (!message.HasId || message.Id is not { } id)
            return;

        var key = JsonRpcMessage.GetIdKey(id);

        // 找不到说明请求已超时或被取消，迟到的响应直接丢弃即可。
        if (_pending.TryRemove(key, out var completion))
            completion.TrySetResult(message);
    }

    private async Task HandleServerRequestAsync(JsonElement requestId, string method, JsonElement? parameters)
    {
        try
        {
            JsonNode? result;

            switch (method)
            {
                case McpMethods.Ping:
                    result = new JsonObject();
                    break;

                case McpMethods.RootsList when _options.RootsHandler is not null:
                    result = ToNode(await _options.RootsHandler(_disposeCts.Token).ConfigureAwait(false));
                    break;

                case McpMethods.SamplingCreateMessage when _options.SamplingHandler is not null:
                    result = ToNode(await _options
                        .SamplingHandler(parameters ?? default, _disposeCts.Token)
                        .ConfigureAwait(false));
                    break;

                case McpMethods.ElicitationCreate when _options.ElicitationHandler is not null:
                    result = ToNode(await _options
                        .ElicitationHandler(parameters ?? default, _disposeCts.Token)
                        .ConfigureAwait(false));
                    break;

                default:
                    await SendErrorAsync(
                            requestId,
                            McpErrorCodes.MethodNotFound,
                            $"客户端不支持服务端请求「{method}」。")
                        .ConfigureAwait(false);
                    return;
            }

            await _transport
                .SendAsync(JsonRpcMessage.CreateResult(requestId, result), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理 MCP 服务器「{Name}」的「{Method}」请求时出错。", Name, method);

            await SendErrorAsync(requestId, McpErrorCodes.InternalError, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task SendErrorAsync(JsonElement requestId, int code, string message)
    {
        try
        {
            await _transport
                .SendAsync(JsonRpcMessage.CreateError(requestId, code, message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "向 MCP 服务器「{Name}」回送错误响应失败。", Name);
        }
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case McpMethods.ToolsListChangedNotification:
                Raise(ListChanged, new McpListChangedEventArgs { ServerName = Name, Kind = McpListKind.Tools });
                break;

            case McpMethods.ResourcesListChangedNotification:
                Raise(ListChanged, new McpListChangedEventArgs { ServerName = Name, Kind = McpListKind.Resources });
                break;

            case McpMethods.PromptsListChangedNotification:
                Raise(ListChanged, new McpListChangedEventArgs { ServerName = Name, Kind = McpListKind.Prompts });
                break;

            case McpMethods.ResourcesUpdatedNotification:
                if (parameters is { } updated && ReadString(updated, "uri") is { Length: > 0 } uri)
                {
                    Raise(ResourceUpdated, new McpResourceUpdatedEventArgs { ServerName = Name, Uri = uri });
                }

                break;

            case McpMethods.LoggingMessageNotification:
                if (parameters is { } logParams)
                {
                    Raise(LogMessage, new McpLogMessageEventArgs
                    {
                        ServerName = Name,
                        Level = McpLogLevels.Parse(ReadString(logParams, "level")),
                        Logger = ReadString(logParams, "logger"),
                        Data = logParams.TryGetProperty("data", out var data) ? data.Clone() : default
                    });
                }

                break;

            case McpMethods.ProgressNotification:
                if (parameters is { } progressParams)
                {
                    Raise(Progress, new McpProgressEventArgs
                    {
                        ServerName = Name,
                        ProgressToken = ReadProgressToken(progressParams),
                        Progress = ReadDouble(progressParams, "progress") ?? 0,
                        Total = ReadDouble(progressParams, "total"),
                        Message = ReadString(progressParams, "message")
                    });
                }

                break;
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;

        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            // 订阅方的异常不应破坏读取循环。
            _logger?.LogError(ex, "处理 MCP 服务器「{Name}」的通知时，订阅方抛出异常。", Name);
        }
    }

    private void OnTransportClosed(object? sender, McpTransportClosedEventArgs args)
    {
        _initializeResult = null;

        var reason = string.IsNullOrWhiteSpace(args.Reason) ? "连接已关闭。" : args.Reason!;
        var exception = new McpConnectionClosedException($"MCP 服务器「{Name}」的连接已关闭：{reason}", args.Exception);

        _logger?.LogWarning("MCP 服务器「{Name}」的连接已关闭：{Reason}", Name, reason);

        FailPending(exception);
    }

    private void FailPending(Exception exception)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetException(exception);
        }
    }

    private async Task NotifyCancelledAsync(int requestId, string reason)
    {
        try
        {
            var parameters = new JsonObject
            {
                ["requestId"] = requestId,
                ["reason"] = reason
            };

            await _transport
                .SendAsync(
                    JsonRpcMessage.CreateNotification(McpMethods.CancelledNotification, parameters),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "向 MCP 服务器「{Name}」发送取消通知失败。", Name);
        }
    }

    private static T? Deserialize<T>(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined ? default : element.Deserialize<T>(McpJson.Options);

    private static JsonNode? ToNode(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadProgressToken(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("progressToken", out var token))
            return null;

        return token.ValueKind switch
        {
            JsonValueKind.String => token.GetString(),
            JsonValueKind.Number => token.GetRawText(),
            _ => null
        };
    }
}
