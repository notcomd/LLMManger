// ============================================================================
// 文件：StreamableHttpMcpTransport.cs
// 用途：基于 Streamable HTTP 的 MCP 传输（规范 2025-03-26 起推荐）。
// 实现方：StreamableHttpMcpTransport
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// Streamable HTTP 传输的配置。
/// </summary>
public sealed class StreamableHttpTransportOptions
{
    /// <summary>MCP 端点地址，所有 JSON-RPC 消息都 POST 到这里。</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>附加的 HTTP 请求头，常用于 <c>Authorization</c> 等鉴权信息。</summary>
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>传输名称，用于日志与错误信息。</summary>
    public string Name { get; init; } = "http";

    /// <summary>
    /// 是否建立长连接的服务端消息流（GET + SSE）。
    /// </summary>
    /// <remarks>
    /// 只有需要接收服务端主动请求（<c>sampling/createMessage</c>、<c>roots/list</c>、
    /// <c>elicitation/create</c>）或未经请求的通知时才需要开启。默认关闭，
    /// 因为多数只读工具服务器并不下发服务端请求，长期占用一条 HTTP 连接没有必要。
    /// </remarks>
    public bool EnableServerStream { get; init; }

    /// <summary>服务端消息流断开后的重连间隔，默认 2 秒。</summary>
    public TimeSpan ServerStreamReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>外部提供的 <see cref="HttpClient"/>；为 <c>null</c> 时由传输层自行创建并负责释放。</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// 访问令牌提供者；设置后每个请求都会带上 <c>Authorization: Bearer</c> 头，
    /// 并在收到 401 时自动刷新一次令牌后重试。
    /// </summary>
    public IMcpTokenProvider? TokenProvider { get; init; }
}

/// <summary>
/// 基于 Streamable HTTP 的 MCP 传输（规范 2025-03-26 起推荐）。
/// </summary>
/// <remarks>
/// <para>
/// 与 stdio 不同，HTTP 传输没有长驻的双向管道，客户端每条消息都是一次 POST：
/// </para>
/// <list type="bullet">
///   <item>服务端可以直接返回 <c>application/json</c>，一条报文搞定；</item>
///   <item>也可以返回 <c>text/event-stream</c>，在流中推送多条消息（进度、通知、最终响应）；</item>
///   <item>或者返回 <c>202 Accepted</c>，表示「已接收，结果稍后从别处推送」。</item>
/// </list>
/// <para>
/// 本实现完整处理上述三种形态，并遵循会话约定：服务端在初始化响应中下发
/// <c>Mcp-Session-Id</c>，后续所有请求都要回传；初始化完成后所有请求还要带
/// <c>MCP-Protocol-Version</c> 头。服务端返回 404 时视为会话失效，清空会话标识并抛出
/// <see cref="McpConnectionClosedException"/>，由上层重新握手。
/// </para>
/// </remarks>
public sealed class StreamableHttpMcpTransport : IMcpTransport
{
    private readonly StreamableHttpTransportOptions _options;
    private readonly ILogger<StreamableHttpMcpTransport>? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _sessionLock = new();

    private CancellationTokenSource? _serverStreamCts;
    private Task? _serverStreamLoop;
    private string? _sessionId;
    private int _started;
    private int _disposed;
    private int _closed;

    /// <summary>初始化 Streamable HTTP 传输。</summary>
    /// <param name="options">端点与请求头配置。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public StreamableHttpMcpTransport(
        StreamableHttpTransportOptions options,
        ILogger<StreamableHttpMcpTransport>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (options.HttpClient is { } provided)
        {
            _httpClient = provided;
            _ownsHttpClient = false;
        }
        else
        {
            // 工具调用可能持续数分钟，超时由 McpClient 按请求粒度控制，HttpClient 不应该再插一手。
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public Func<JsonRpcMessage, Task>? MessageHandler { get; set; }

    /// <inheritdoc />
    public string? ProtocolVersion { get; set; }

    /// <summary>当前会话标识，由服务端在初始化响应中下发；未建立会话时为 <c>null</c>。</summary>
    public string? SessionId
    {
        get
        {
            lock (_sessionLock)
                return _sessionId;
        }
    }

    /// <inheritdoc />
    public event EventHandler<McpTransportClosedEventArgs>? Closed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        if (_options.EnableServerStream)
        {
            _serverStreamCts = new CancellationTokenSource();
            _serverStreamLoop = Task.Run(
                () => RunServerStreamAsync(_serverStreamCts.Token),
                CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("传输尚未启动，请先调用 StartAsync。");

        var provider = _options.TokenProvider;
        var token = provider is null
            ? null
            : await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        var response = await PostAsync(message, token, cancellationToken).ConfigureAwait(false);

        // 401 时按 MCP 授权规范刷新一次令牌再重试；只重试一次，避免陷入循环。
        if (response.StatusCode == HttpStatusCode.Unauthorized && provider is not null)
        {
            var challenge = McpOAuthChallenge.Parse(response.Headers.WwwAuthenticate);
            response.Dispose();

            _logger?.LogInformation("MCP 服务器「{Name}」返回 401，尝试刷新访问令牌。", Name);

            var refreshed = await provider.RefreshAsync(challenge, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(refreshed))
                throw new McpUnauthorizedException(Name, _options.Endpoint, challenge);

            response = await PostAsync(message, refreshed, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var retryChallenge = McpOAuthChallenge.Parse(response.Headers.WwwAuthenticate);
                response.Dispose();

                throw new McpUnauthorizedException(Name, _options.Endpoint, retryChallenge);
            }
        }

        using (response)
        {
            CaptureSession(response);

            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodySafelyAsync(response, cancellationToken).ConfigureAwait(false);
                HandleErrorStatus(response.StatusCode, body);
                return;
            }

            // 202：服务端已接收，不在此连接上回包（结果可能从服务端消息流推送）。
            if (response.StatusCode == HttpStatusCode.Accepted)
                return;

            if (response.Content.Headers.ContentLength == 0)
                return;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var expectedId = message.HasId && message.Id is { } id ? JsonRpcMessage.GetIdKey(id) : null;

            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await ReadEventStreamAsync(stream, cancellationToken, expectedId).ConfigureAwait(false);
                return;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await DispatchPayloadAsync(text, cancellationToken, expectedId).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        JsonRpcMessage message,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message, McpJson.Options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyCommonHeaders(request);

        // 显式配置的 Authorization 优先，令牌只在没有手工头时注入。
        var hasExplicitAuthorization =
            request.Headers.Authorization is not null ||
            _options.Headers.ContainsKey("Authorization");

        if (!string.IsNullOrEmpty(accessToken) && !hasExplicitAuthorization)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new McpConnectionClosedException($"无法连接 MCP 服务器「{Name}」（{_options.Endpoint}）：{ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpConnectionClosedException($"访问 MCP 服务器「{Name}」超时。");
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_serverStreamCts is not null)
        {
            await _serverStreamCts.CancelAsync().ConfigureAwait(false);

            if (_serverStreamLoop is not null)
            {
                try
                {
                    await _serverStreamLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        // 规范建议客户端在结束时 DELETE 掉会话，让服务端及时回收状态；失败不影响关闭流程。
        var session = SessionId;
        if (!string.IsNullOrEmpty(session))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _options.Endpoint);
                ApplyCommonHeaders(request);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "结束 MCP 服务器「{Name}」的 HTTP 会话时出错。", Name);
            }
        }

        _serverStreamCts?.Dispose();

        if (_ownsHttpClient)
            _httpClient.Dispose();

        RaiseClosed(new McpTransportClosedEventArgs
        {
            Reason = $"MCP 服务器「{Name}」的 HTTP 传输已关闭。"
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private void ApplyCommonHeaders(HttpRequestMessage request)
    {
        foreach (var pair in _options.Headers)
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

        var session = SessionId;
        if (!string.IsNullOrEmpty(session))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);

        // 2025-06-18 起要求客户端在初始化之后的所有请求上声明协商版本。
        if (!string.IsNullOrEmpty(ProtocolVersion))
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            return;

        var session = values.FirstOrDefault();
        if (string.IsNullOrEmpty(session))
            return;

        lock (_sessionLock)
            _sessionId = session;

        _logger?.LogDebug("MCP 服务器「{Name}」下发了会话标识：{SessionId}", Name, session);
    }

    private void HandleErrorStatus(HttpStatusCode statusCode, string body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $"：{Truncate(body, 500)}";

        if (statusCode == HttpStatusCode.NotFound && SessionId is not null)
        {
            // 会话过期：清空标识，下次调用会重新走 initialize。
            lock (_sessionLock)
                _sessionId = null;

            // 通知客户端这次是连接级失败，而不是某一次调用的业务错误。
            RaiseClosed(new McpTransportClosedEventArgs
            {
                Reason = $"MCP 服务器「{Name}」的会话已失效（HTTP 404）。"
            });

            throw new McpConnectionClosedException(
                $"MCP 服务器「{Name}」的会话已失效（HTTP 404），需要重新初始化。{detail}");
        }

        var code = statusCode switch
        {
            HttpStatusCode.BadRequest => McpErrorCodes.InvalidRequest,
            HttpStatusCode.Unauthorized => McpErrorCodes.InvalidRequest,
            HttpStatusCode.Forbidden => McpErrorCodes.InvalidRequest,
            _ => McpErrorCodes.InternalError
        };

        throw new McpException(
            code,
            $"MCP 服务器「{Name}」返回 HTTP {(int)statusCode}（{statusCode}）{detail}");
    }

    private static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>解析单个或批量（旧版规范允许）的消息体，全部派发出去。</summary>
    /// <returns>是否遇到了与 <paramref name="stopOnId"/> 匹配的响应。</returns>
    private async Task<bool> DispatchPayloadAsync(string payload, CancellationToken cancellationToken, string? stopOnId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "MCP 服务器「{Name}」返回了无法解析的 JSON。", Name);
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var matches = IsMatchingResponse(item, stopOnId);
                    await DispatchMessageAsync(item, cancellationToken).ConfigureAwait(false);

                    if (matches)
                        return true;
                }

                return false;
            }

            var matched = IsMatchingResponse(root, stopOnId);
            await DispatchMessageAsync(root, cancellationToken).ConfigureAwait(false);
            return matched;
        }
    }

    private async Task DispatchMessageAsync(JsonElement element, CancellationToken cancellationToken)
    {
        JsonRpcMessage? message;
        try
        {
            message = element.Deserialize<JsonRpcMessage>(McpJson.Options);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "MCP 服务器「{Name}」返回了结构非法的消息。", Name);
            return;
        }

        if (message is null)
            return;

        var handler = MessageHandler;
        if (handler is null)
            return;

        await handler(message).ConfigureAwait(false);
    }

    private static bool IsMatchingResponse(JsonElement element, string? expectedId)
    {
        if (expectedId is null || element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("method", out _))
            return false;

        if (!element.TryGetProperty("id", out var id))
            return false;

        return JsonRpcMessage.GetIdKey(id) == expectedId;
    }

    /// <summary>
    /// 读取一条 SSE 流。POST 响应流在收到预期响应后即可停止读取；
    /// 服务端消息流（GET）则一直读到连接断开。
    /// </summary>
    private async Task ReadEventStreamAsync(Stream stream, CancellationToken cancellationToken, string? stopOnResponseId)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);

        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();

                    if (await DispatchPayloadAsync(payload, cancellationToken, stopOnResponseId).ConfigureAwait(false))
                        return;
                }

                continue;
            }

            // 以冒号开头的是注释（心跳），按规范忽略。
            if (line[0] == ':')
                continue;

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];

            if (value.Length > 0 && value[0] == ' ')
                value = value[1..];

            // event / id / retry 字段对 JSON-RPC 派发没有影响，忽略即可。
            if (field == "data")
            {
                if (data.Length > 0)
                    data.Append('\n');

                data.Append(value);
            }
        }

        if (data.Length > 0)
            await DispatchPayloadAsync(data.ToString(), cancellationToken, stopOnResponseId).ConfigureAwait(false);
    }

    private async Task RunServerStreamAsync(CancellationToken cancellationToken)
    {
        var delay = _options.ServerStreamReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                ApplyCommonHeaders(request);

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // 服务端不支持 GET 流时，安静退出即可——这是规范允许的可选能力。
                if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                {
                    _logger?.LogDebug("MCP 服务器「{Name}」不支持服务端消息流，已跳过。", Name);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug(
                        "MCP 服务器「{Name}」的服务端消息流返回 HTTP {Status}。",
                        Name, (int)response.StatusCode);

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await ReadEventStreamAsync(stream, cancellationToken, stopOnResponseId: null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "MCP 服务器「{Name}」的服务端消息流中断，稍后重连。", Name);
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseClosed(McpTransportClosedEventArgs args)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try
        {
            Closed?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理 MCP 服务器「{Name}」的关闭事件时出错。", Name);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
