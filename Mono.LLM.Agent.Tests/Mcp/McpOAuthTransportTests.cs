using System.Net;
using System.Text.Json;
using Mono.LLM.Agent.Mcp;
using static Mono.LLM.Agent.Tests.Mcp.RecordingHttpMessageHandler;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 HTTP 传输与 OAuth 的衔接：Bearer 注入、401 后刷新重试、失败时的异常语义。
/// </summary>
public class McpOAuthTransportTests
{
    private const string InitializeResult = """
    {"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"oauth-server","version":"1.0"}}
    """;

    private sealed class StubTokenProvider : IMcpTokenProvider
    {
        public Func<CancellationToken, string?> GetToken { get; set; } = _ => "access-1";

        public Func<McpOAuthChallenge?, CancellationToken, string?> RefreshToken { get; set; } = (_, _) => "access-2";

        public int RefreshCalls { get; private set; }

        public McpOAuthChallenge? LastChallenge { get; private set; }

        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(GetToken(cancellationToken));

        public ValueTask<string?> RefreshAsync(
            McpOAuthChallenge? challenge = null,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            LastChallenge = challenge;

            return ValueTask.FromResult(RefreshToken(challenge, cancellationToken));
        }
    }

    private static HttpResponseMessage Unauthorized()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        response.Headers.TryAddWithoutValidation(
            "WWW-Authenticate",
            "Bearer resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/rpc\", " +
            "error=\"invalid_token\", error_description=\"token expired\"");

        return response;
    }

    /// <summary>按请求 id 与 method 构造合法的 JSON-RPC 响应；通知请求返回 202。</summary>
    private static HttpResponseMessage Respond(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new HttpResponseMessage(HttpStatusCode.Accepted);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("id", out var id))
            return new HttpResponseMessage(HttpStatusCode.Accepted);

        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;

        var result = method switch
        {
            "initialize" => InitializeResult,
            "tools/list" => """{"tools":[{"name":"echo","description":"回声","inputSchema":{"type":"object","properties":{}}}]}""",
            "tools/call" => """{"content":[{"type":"text","text":"ok"}]}""",
            _ => "{}"
        };

        return Json($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
    }

    private static (McpClient Client, StubTokenProvider Provider) CreateClient(RecordingHttpMessageHandler handler)
    {
        var provider = new StubTokenProvider();

        var transport = new StreamableHttpMcpTransport(new StreamableHttpTransportOptions
        {
            Endpoint = new Uri("https://mcp.test/rpc"),
            Name = "oauth",
            HttpClient = new HttpClient(handler),
            TokenProvider = provider
        });

        return (new McpClient(transport, new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) }), provider);
    }

    private static List<RecordedRequest> InitializeRequests(RecordingHttpMessageHandler handler) =>
        handler.Requests
            .Where(r => r.Body is not null && r.Body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public async Task SendsBearerToken_AndRetriesOnceAfter401()
    {
        var posts = 0;

        var handler = new RecordingHttpMessageHandler((request, body) =>
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK);

            posts++;

            return posts == 1 ? Unauthorized() : Respond(body);
        });

        var (client, provider) = CreateClient(handler);
        await using var _ = client;

        await client.ConnectAsync();

        var attempts = InitializeRequests(handler);
        Assert.Equal(2, attempts.Count);
        Assert.Equal("Bearer access-1", attempts[0].Headers["Authorization"]);
        Assert.Equal("Bearer access-2", attempts[1].Headers["Authorization"]);

        Assert.Equal(1, provider.RefreshCalls);
        Assert.Equal(
            "https://mcp.test/.well-known/oauth-protected-resource/rpc",
            provider.LastChallenge!.ResourceMetadata!.AbsoluteUri);
        Assert.Equal("invalid_token", provider.LastChallenge.Error);
    }

    [Fact]
    public async Task ThrowsUnauthorized_WhenRefreshProducesNoToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => Unauthorized());

        var (client, provider) = CreateClient(handler);
        await using var __ = client;

        provider.RefreshToken = (_, _) => null;

        var exception = await Assert.ThrowsAsync<McpUnauthorizedException>(() => client.ConnectAsync());

        Assert.Equal(McpErrorCodes.AuthorizationRequired, exception.Code);
        Assert.NotNull(exception.Challenge);
        Assert.Equal("token expired", exception.Challenge!.ErrorDescription);
        Assert.Equal(1, provider.RefreshCalls);
    }

    [Fact]
    public async Task DoesNotRetryMoreThanOnce()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            request.Method == HttpMethod.Post ? Unauthorized() : new HttpResponseMessage(HttpStatusCode.OK));

        var (client, _) = CreateClient(handler);
        await using var __ = client;

        await Assert.ThrowsAsync<McpUnauthorizedException>(() => client.ConnectAsync());

        // 原请求 + 一次重试，不会陷入循环。
        Assert.Equal(2, InitializeRequests(handler).Count);
    }

    [Fact]
    public void Config_OAuthIsRejectedForStdioTransport()
    {
        var config = new McpServerConfig
        {
            Name = "local",
            Transport = McpTransportKind.Stdio,
            Command = "node",
            OAuth = new McpOAuthOptions { ClientId = "client-1" }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => config.CreateTransport(null));

        Assert.Contains("Streamable HTTP", exception.Message);
    }

    [Fact]
    public void Config_WithoutOAuth_CreatesTransportWithoutTokenProvider()
    {
        var config = new McpServerConfig
        {
            Name = "remote",
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = new Uri("https://mcp.test/rpc")
        };

        var transport = config.CreateTransport(null);

        Assert.IsType<StreamableHttpMcpTransport>(transport);
    }

    /// <summary>
    /// 端到端串联：配置 OAuth → 发现 → 动态注册 → PKCE 授权 → 带令牌握手 → 注册工具。
    /// </summary>
    [Fact]
    public async Task Manager_RunsFullOAuthFlow_AndRegistersTools()
    {
        var redirectUri = new Uri("http://127.0.0.1:41234/callback");

        var handler = new RecordingHttpMessageHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/rpc")
            {
                var authorized = request.Headers.Authorization?.Parameter == "access-1";
                return authorized ? Respond(body) : Unauthorized();
            }

            if (path == "/.well-known/oauth-protected-resource/rpc")
            {
                return Json("""
                {"resource":"https://mcp.test/rpc","authorization_servers":["https://auth.test"]}
                """);
            }

            if (path == "/.well-known/oauth-authorization-server")
            {
                return Json("""
                {
                  "issuer": "https://auth.test",
                  "authorization_endpoint": "https://auth.test/authorize",
                  "token_endpoint": "https://auth.test/token",
                  "registration_endpoint": "https://auth.test/register",
                  "code_challenge_methods_supported": ["S256"]
                }
                """);
            }

            if (path == "/register")
                return Json("""{"client_id":"dcr-live"}""");

            if (path == "/token")
                return Json("""{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1"}""");

            return NotFound();
        });

        var config = new McpServerConfig
        {
            Name = "remote",
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = new Uri("https://mcp.test/rpc"),
            HttpClient = new HttpClient(handler),
            OAuth = new McpOAuthOptions
            {
                Store = new InMemoryMcpOAuthStore(),
                HttpClient = new HttpClient(handler),
                Interaction = new DelegateOAuthInteraction(redirectUri, (redirect, authorizationUrl, _) =>
                {
                    var state = QueryParameters.Parse(authorizationUrl.Query)["state"];
                    return Task.FromResult(new Uri($"{redirect}?code=live-code&state={state}"));
                })
            }
        };

        var tools = new Mono.LLM.Agent.Tools.ToolManager();
        await using var manager = new McpClientManager(new[] { config }, tools);

        await manager.StartAsync();

        var connection = Assert.Single(manager.Connections);
        Assert.Null(connection.Error);
        Assert.True(connection.IsConnected);
        Assert.Equal(new[] { "remote__echo" }, connection.ToolNames);
        Assert.True(tools.IsRegistered("remote__echo"));

        // 第一枪必须带令牌；不该出现「先 401 再刷新」的额外往返。
        var firstRpc = handler.Requests.First(r => r.Uri.AbsolutePath == "/rpc");
        Assert.Equal("Bearer access-1", firstRpc.Headers["Authorization"]);

        Assert.Contains(
            handler.Requests,
            r => r.Form("grant_type") == "authorization_code");
    }
}
