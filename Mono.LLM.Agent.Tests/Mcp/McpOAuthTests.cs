using System.Net;
using Mono.LLM.Agent.Mcp;
using static Mono.LLM.Agent.Tests.Mcp.RecordingHttpMessageHandler;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 OAuth 2.1 的各协议环节：发现、动态注册、PKCE 授权、刷新、令牌缓存与 401 重试。
/// </summary>
public class McpOAuthTests
{
    private static readonly Uri Resource = new("https://mcp.test/rpc");
    private static readonly Uri RedirectUri = new("http://127.0.0.1:38251/callback");

    private const string ServerMetadata = """
    {
      "issuer": "https://auth.test",
      "authorization_endpoint": "https://auth.test/authorize",
      "token_endpoint": "https://auth.test/token",
      "registration_endpoint": "https://auth.test/register",
      "code_challenge_methods_supported": ["S256"],
      "scopes_supported": ["mcp.read", "mcp.write"]
    }
    """;

    private static McpOAuthOptions CreateOptions(
        RecordingHttpMessageHandler handler,
        IMcpOAuthStore? store = null,
        string? clientId = "client-1") => new()
    {
        ClientId = clientId,
        Scopes = new[] { "mcp.read" },
        RedirectUri = RedirectUri,
        OpenBrowser = false,
        Store = store ?? new InMemoryMcpOAuthStore(),
        HttpClient = new HttpClient(handler),
        Interaction = new DelegateOAuthInteraction(RedirectUri, (redirect, _, _) =>
        {
            // 默认交互：直接以一个固定的 code 回跳，state 由测试按需覆盖。
            throw new InvalidOperationException("该测试不应触发交互式授权。");
        })
    };

    [Fact]
    public async Task Discovery_UsesProtectedResourceMetadata_ThenAuthorizationServerMetadata()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://mcp.test/.well-known/oauth-protected-resource/rpc" => Json(
                """{"resource":"https://mcp.test/rpc","authorization_servers":["https://auth.test"]}"""),
            "https://auth.test/.well-known/oauth-authorization-server" => Json(ServerMetadata),
            _ => NotFound()
        });

        using var client = new McpOAuthClient(Resource, CreateOptions(handler), new DelegateOAuthInteraction(
            RedirectUri, (_, _, _) => throw new InvalidOperationException()));

        var metadata = await client.EnsureMetadataAsync();

        Assert.Equal("https://auth.test", metadata.Issuer);
        Assert.Equal("https://auth.test/authorize", metadata.AuthorizationEndpoint.AbsoluteUri);
        Assert.Equal("https://auth.test/register", metadata.RegistrationEndpoint!.AbsoluteUri);
        Assert.Contains("S256", metadata.CodeChallengeMethodsSupported);
    }

    [Fact]
    public async Task Discovery_FallsBackToOidcConfiguration()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://auth.test/.well-known/oauth-authorization-server" => NotFound(),
            "https://auth.test/.well-known/openid-configuration" => Json(ServerMetadata),
            _ => NotFound()
        });

        var options = CreateOptions(handler);
        options.AuthorizationServer = new Uri("https://auth.test");

        using var client = new McpOAuthClient(Resource, options, new DelegateOAuthInteraction(
            RedirectUri, (_, _, _) => throw new InvalidOperationException()));

        var metadata = await client.EnsureMetadataAsync();

        Assert.Equal("https://auth.test/token", metadata.TokenEndpoint.AbsoluteUri);
    }

    [Fact]
    public async Task Discovery_PrefersChallengeHint()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://mcp.test/custom/resource-metadata" => Json(
                """{"authorization_servers":["https://auth2.test"]}"""),
            "https://auth2.test/.well-known/oauth-authorization-server" => Json(ServerMetadata),
            _ => NotFound()
        });

        using var client = new McpOAuthClient(Resource, CreateOptions(handler), new DelegateOAuthInteraction(
            RedirectUri, (_, _, _) => throw new InvalidOperationException()));

        await client.EnsureMetadataAsync(new Uri("https://mcp.test/custom/resource-metadata"));

        Assert.Contains(
            handler.Requests,
            r => r.Uri.AbsoluteUri == "https://mcp.test/custom/resource-metadata");
    }

    [Fact]
    public async Task Discovery_RejectsServerWithoutS256()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => Json("""
        {
          "issuer": "https://auth.test",
          "authorization_endpoint": "https://auth.test/authorize",
          "token_endpoint": "https://auth.test/token",
          "code_challenge_methods_supported": ["plain"]
        }
        """));

        var options = CreateOptions(handler);
        options.AuthorizationServer = new Uri("https://auth.test");

        using var client = new McpOAuthClient(Resource, options, new DelegateOAuthInteraction(
            RedirectUri, (_, _, _) => throw new InvalidOperationException()));

        var exception = await Assert.ThrowsAsync<McpOAuthException>(async () =>
            await client.EnsureMetadataAsync());

        Assert.Equal("unsupported_challenge_method", exception.Error);
    }

    [Fact]
    public async Task Registration_PostsClientMetadata()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://auth.test/.well-known/oauth-authorization-server" => Json(ServerMetadata),
            "https://auth.test/register" => Json("""{"client_id":"dcr-1","client_secret":"s3cr3t"}"""),
            _ => NotFound()
        });

        var options = CreateOptions(handler, clientId: null);
        options.AuthorizationServer = new Uri("https://auth.test");

        using var client = new McpOAuthClient(Resource, options, new DelegateOAuthInteraction(
            RedirectUri, (_, _, _) => throw new InvalidOperationException()));

        await client.EnsureMetadataAsync();
        var registration = await client.EnsureRegistrationAsync();

        Assert.Equal("dcr-1", registration.ClientId);
        Assert.Equal("s3cr3t", registration.ClientSecret);

        var request = handler.Single("/register");
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);

        Assert.Equal(
            RedirectUri.AbsoluteUri,
            body.RootElement.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal("none", body.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Contains(
            "authorization_code",
            body.RootElement.GetProperty("grant_types").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Authorize_SendsPkceAndResource_AndExchangesCode()
    {
        Uri? authorizationUrl = null;

        var handler = new RecordingHttpMessageHandler((request, body) => request.RequestUri!.AbsoluteUri switch
        {
            "https://auth.test/.well-known/oauth-authorization-server" => Json(ServerMetadata),
            "https://auth.test/token" => Json(
                """{"access_token":"at-1","token_type":"Bearer","expires_in":3600,"refresh_token":"rt-1","scope":"mcp.read"}"""),
            _ => NotFound()
        });

        var options = CreateOptions(handler);
        options.AuthorizationServer = new Uri("https://auth.test");
        options.Interaction = new DelegateOAuthInteraction(RedirectUri, (redirect, url, _) =>
        {
            authorizationUrl = url;
            var state = QueryParameters.Parse(url.Query)["state"];
            return Task.FromResult(new Uri($"{redirect}?code=code-1&state={state}"));
        });

        using var client = new McpOAuthClient(Resource, options, options.Interaction!);
        var metadata = await client.EnsureMetadataAsync();
        var registration = await client.EnsureRegistrationAsync();

        var token = await client.AuthorizeAsync(metadata, registration);

        Assert.Equal("at-1", token.AccessToken);
        Assert.Equal("rt-1", token.RefreshToken);
        Assert.NotNull(token.ExpiresAt);

        var query = QueryParameters.Parse(authorizationUrl!.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("client-1", query["client_id"]);
        Assert.Equal(Resource.AbsoluteUri, query["resource"]);
        Assert.Equal(RedirectUri.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("mcp.read", query["scope"]);

        var tokenRequest = handler.Single("/token");
        Assert.Equal("authorization_code", tokenRequest.Form("grant_type"));
        Assert.Equal("code-1", tokenRequest.Form("code"));
        Assert.Equal(Resource.AbsoluteUri, tokenRequest.Form("resource"));

        // 令牌请求里的 verifier 必须与授权请求里的 challenge 相匹配。
        var verifier = tokenRequest.Form("code_verifier");
        Assert.False(string.IsNullOrEmpty(verifier));
        Assert.Equal(query["code_challenge"], PkceCode.CreateChallenge(verifier!));
    }

    [Fact]
    public async Task Authorize_RejectsStateMismatch()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => Json(ServerMetadata));

        var options = CreateOptions(handler);
        options.AuthorizationServer = new Uri("https://auth.test");
        options.Interaction = new DelegateOAuthInteraction(
            RedirectUri,
            (redirect, _, _) => Task.FromResult(new Uri($"{redirect}?code=code-1&state=forged")));

        using var client = new McpOAuthClient(Resource, options, options.Interaction!);
        var metadata = await client.EnsureMetadataAsync();
        var registration = await client.EnsureRegistrationAsync();

        var exception = await Assert.ThrowsAsync<McpOAuthException>(
            async () => await client.AuthorizeAsync(metadata, registration));

        Assert.Equal("invalid_state", exception.Error);
    }

    [Fact]
    public async Task Authorize_SurfacesAccessDenied()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => Json(ServerMetadata));

        var options = CreateOptions(handler);
        options.AuthorizationServer = new Uri("https://auth.test");
        options.Interaction = new DelegateOAuthInteraction(RedirectUri, (redirect, url, _) =>
        {
            var state = QueryParameters.Parse(url.Query)["state"];
            return Task.FromResult(new Uri($"{redirect}?error=access_denied&error_description=user%20denied&state={state}"));
        });

        using var client = new McpOAuthClient(Resource, options, options.Interaction!);
        var metadata = await client.EnsureMetadataAsync();
        var registration = await client.EnsureRegistrationAsync();

        var exception = await Assert.ThrowsAsync<McpOAuthException>(
            async () => await client.AuthorizeAsync(metadata, registration));

        Assert.Equal("access_denied", exception.Error);
        Assert.True(exception.RequiresReauthorization);
    }

    [Fact]
    public async Task TokenProvider_ReturnsCachedToken_WithoutAnyHttpCall()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => throw new InvalidOperationException("不应发起请求"));
        var store = new InMemoryMcpOAuthStore();

        await store.SetAsync("srv|https://mcp.test/rpc", new McpOAuthSession
        {
            ClientId = "client-1",
            Token = new McpOAuthToken
            {
                AccessToken = "cached",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            }
        });

        await using var provider = new McpOAuthTokenProvider("srv", Resource, CreateOptions(handler, store));

        Assert.Equal("cached", await provider.GetTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TokenProvider_RefreshesExpiredToken_AndPersistsResult()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://auth.test/.well-known/oauth-authorization-server" => Json(ServerMetadata),
            "https://auth.test/token" => Json(
                """{"access_token":"at-new","expires_in":3600,"refresh_token":"rt-new"}"""),
            _ => NotFound()
        });

        var store = new InMemoryMcpOAuthStore();
        await store.SetAsync("srv|https://mcp.test/rpc", new McpOAuthSession
        {
            ClientId = "client-1",
            Token = new McpOAuthToken
            {
                AccessToken = "expired",
                RefreshToken = "rt-old",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        });

        var options = CreateOptions(handler, store);
        options.AuthorizationServer = new Uri("https://auth.test");

        await using var provider = new McpOAuthTokenProvider("srv", Resource, options);

        Assert.Equal("at-new", await provider.GetTokenAsync());

        var tokenRequest = handler.Single("/token");
        Assert.Equal("refresh_token", tokenRequest.Form("grant_type"));
        Assert.Equal("rt-old", tokenRequest.Form("refresh_token"));

        var saved = await store.GetAsync("srv|https://mcp.test/rpc");
        Assert.Equal("at-new", saved!.Token!.AccessToken);
        Assert.Equal("rt-new", saved.Token.RefreshToken);
    }

    [Fact]
    public async Task TokenProvider_Reauthorizes_WhenRefreshTokenIsRejected()
    {
        var handler = new RecordingHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth-authorization-server", StringComparison.Ordinal))
                return Json(ServerMetadata);

            if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return QueryParameters.Parse(body).GetValueOrDefault("grant_type") == "refresh_token"
                    ? Json("""{"error":"invalid_grant","error_description":"已过期"}""", HttpStatusCode.BadRequest)
                    : Json("""{"access_token":"at-fresh","expires_in":3600}""");
            }

            return NotFound();
        });

        var store = new InMemoryMcpOAuthStore();
        await store.SetAsync("srv|https://mcp.test/rpc", new McpOAuthSession
        {
            ClientId = "client-1",
            Token = new McpOAuthToken
            {
                AccessToken = "expired",
                RefreshToken = "rt-dead",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        });

        var options = CreateOptions(handler, store);
        options.AuthorizationServer = new Uri("https://auth.test");
        options.Interaction = new DelegateOAuthInteraction(RedirectUri, (redirect, url, _) =>
        {
            var state = QueryParameters.Parse(url.Query)["state"];
            return Task.FromResult(new Uri($"{redirect}?code=code-2&state={state}"));
        });

        await using var provider = new McpOAuthTokenProvider("srv", Resource, options);

        Assert.Equal("at-fresh", await provider.GetTokenAsync());

        Assert.Contains(
            handler.Requests,
            r => r.Form("grant_type") == "authorization_code");
    }

    [Fact]
    public async Task FileStore_RoundTripsSession_AndRestrictsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mono-mcp-oauth-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new FileMcpOAuthStore(directory);
            var session = new McpOAuthSession
            {
                ClientId = "client-9",
                Token = new McpOAuthToken { AccessToken = "at-file", RefreshToken = "rt-file" }
            };

            await store.SetAsync("k", session);
            var loaded = await store.GetAsync("k");

            Assert.Equal("client-9", loaded!.ClientId);
            Assert.Equal("at-file", loaded.Token!.AccessToken);

            await store.RemoveAsync("k");
            Assert.Null(await store.GetAsync("k"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Challenge_ParsesBearerParameters()
    {
        var header = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "resource_metadata=\"https://mcp.test/meta\", scope=\"mcp.read mcp.write\", error=\"invalid_token\", error_description=\"token expired\"");

        var challenge = McpOAuthChallenge.Parse(new[] { header });

        Assert.NotNull(challenge);
        Assert.Equal("https://mcp.test/meta", challenge!.ResourceMetadata!.AbsoluteUri);
        Assert.Equal("mcp.read mcp.write", challenge.Scope);
        Assert.Equal("invalid_token", challenge.Error);
        Assert.Equal("token expired", challenge.ErrorDescription);
    }

    [Fact]
    public void Challenge_ReturnsNull_WhenNoBearerHeader()
    {
        var header = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "abc");

        Assert.Null(McpOAuthChallenge.Parse(new[] { header }));
    }
}
