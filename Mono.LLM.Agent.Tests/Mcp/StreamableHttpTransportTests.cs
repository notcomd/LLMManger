using System.Net;
using System.Text;
using System.Text.Json;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 Streamable HTTP 传输：JSON 响应、SSE 响应、会话头与错误映射。
/// </summary>
public class StreamableHttpTransportTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new HttpRequest(
                request.Method.Method,
                body,
                request.Headers.ToDictionary(
                    h => h.Key,
                    h => string.Join(",", h.Value),
                    StringComparer.OrdinalIgnoreCase)));

            return _responder(request, body);
        }
    }

    private sealed record HttpRequest(
        string Method,
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    private static HttpResponseMessage JsonResponse(string id, string resultJson, string? session = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}",
                Encoding.UTF8,
                "application/json")
        };

        if (session is not null)
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);

        return response;
    }

    private static HttpResponseMessage SseResponse(string payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static (string Method, string Id) ParseRequest(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var method = root.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;

        var id = root.TryGetProperty("id", out var idElement)
            ? idElement.GetRawText()
            : string.Empty;

        return (method, id);
    }

    private const string InitializeResult =
        """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"http-server","version":"1.0"}}""";

    private static StreamableHttpMcpTransport CreateTransport(
        StubHttpMessageHandler handler,
        out McpClient client,
        McpClientOptions? options = null)
    {
        var transport = new StreamableHttpMcpTransport(new StreamableHttpTransportOptions
        {
            Endpoint = new Uri("https://mcp.example.test/rpc"),
            Name = "http",
            HttpClient = new HttpClient(handler)
        });

        client = new McpClient(
            transport,
            options ?? new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) });

        return transport;
    }

    [Fact]
    public async Task ConnectAsync_ReadsJsonResponse_AndCapturesSession()
    {
        var handler = new StubHttpMessageHandler((_, body) =>
        {
            var (method, id) = ParseRequest(body);
            if (id.Length == 0)
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            return method switch
            {
                McpMethods.Initialize => JsonResponse(id, InitializeResult, session: "sess-1"),
                McpMethods.ToolsList => JsonResponse(
                    id,
                    """{"tools":[{"name":"echo","description":"回声","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}]}"""),
                _ => JsonResponse(id, "{}")
            };
        });

        var transport = CreateTransport(handler, out var client);
        await using var _ = client;

        var initialized = await client.ConnectAsync();

        Assert.Equal("2025-06-18", initialized.ProtocolVersion);
        Assert.Equal("sess-1", transport.SessionId);

        var tools = await client.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);

        // 首个请求还没有会话与协议版本；之后的请求两者都要带上。
        var initializeRequest = handler.Requests[0];
        Assert.False(initializeRequest.Headers.ContainsKey("Mcp-Session-Id"));
        Assert.False(initializeRequest.Headers.ContainsKey("MCP-Protocol-Version"));

        var toolsRequest = handler.Requests.First(r => ParseRequest(r.Body).Method == McpMethods.ToolsList);
        Assert.Equal("sess-1", toolsRequest.Headers["Mcp-Session-Id"]);
        Assert.Equal("2025-06-18", toolsRequest.Headers["MCP-Protocol-Version"]);

        // initialized 通知也必须发出。
        Assert.Contains(
            handler.Requests,
            r => ParseRequest(r.Body).Method == McpMethods.InitializedNotification);
    }

    [Fact]
    public async Task SendAsync_ParsesSseStream()
    {
        var handler = new StubHttpMessageHandler((_, body) =>
        {
            var (method, id) = ParseRequest(body);
            if (id.Length == 0)
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            if (method == McpMethods.Initialize)
                return JsonResponse(id, InitializeResult, session: "sess-2");

            if (method == McpMethods.ToolsList)
            {
                // 先推一条通知，再推响应：验证 SSE 多消息派发。
                var payload =
                    "event: message\n" +
                    "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n" +
                    "\n" +
                    ": 心跳注释\n" +
                    "event: message\n" +
                    $"data: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[{{\"name\":\"a\"}},{{\"name\":\"b\"}}]}}}}\n" +
                    "\n";

                return SseResponse(payload);
            }

            return JsonResponse(id, "{}");
        });

        var transport = CreateTransport(handler, out var client);
        await using var _ = client;

        await client.ConnectAsync();

        var changes = 0;
        client.ListChanged += (_, _) => Interlocked.Increment(ref changes);

        var tools = await client.ListToolsAsync();

        Assert.Equal(2, tools.Count);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task SendAsync_MapsHttpError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("bad token", Encoding.UTF8, "text/plain")
            });

        CreateTransport(handler, out var client);
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpErrorCodes.InvalidRequest, exception.Code);
        Assert.Contains("401", exception.Message);
        Assert.Contains("bad token", exception.Message);
    }

    [Fact]
    public async Task SessionExpiry_ThrowsConnectionClosed_AndClearsSession()
    {
        var handler = new StubHttpMessageHandler((_, body) =>
        {
            var (method, id) = ParseRequest(body);
            if (id.Length == 0)
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            return method switch
            {
                McpMethods.Initialize => JsonResponse(id, InitializeResult, session: "sess-3"),
                McpMethods.ToolsList => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => JsonResponse(id, "{}")
            };
        });

        var transport = CreateTransport(handler, out var client);
        await using var __ = client;

        await client.ConnectAsync();
        Assert.Equal("sess-3", transport.SessionId);

        await Assert.ThrowsAsync<McpConnectionClosedException>(() => client.ListToolsAsync());

        Assert.Null(transport.SessionId);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Accepted202_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler((_, body) =>
        {
            var (method, id) = ParseRequest(body);
            if (id.Length == 0)
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            return method == McpMethods.Initialize
                ? JsonResponse(id, InitializeResult)
                : new HttpResponseMessage(HttpStatusCode.Accepted);
        });

        CreateTransport(handler, out var client);
        await using var __ = client;

        await client.ConnectAsync();

        // 通知走 202 是正常语义，不应被当成错误。
        await client.SendNotificationAsync(McpMethods.RootsListChangedNotification);
    }
}
