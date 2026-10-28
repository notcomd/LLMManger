// ============================================================================
// 文件：McpOAuthClient.cs
// 用途：MCP 的 OAuth 2.1 协议客户端：发现、注册、授权码 + PKCE、令牌刷新。
// 实现方：McpOAuthClient
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 授权服务器的元数据（RFC 8414 / OIDC Discovery）。
/// </summary>
public sealed class McpOAuthServerMetadata
{
    /// <summary>签发者标识。</summary>
    public required string Issuer { get; init; }

    /// <summary>授权端点。</summary>
    public required Uri AuthorizationEndpoint { get; init; }

    /// <summary>令牌端点。</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>动态客户端注册端点；为 <c>null</c> 表示不支持自动注册。</summary>
    public Uri? RegistrationEndpoint { get; init; }

    /// <summary>服务端支持的授权范围。</summary>
    public IReadOnlyList<string> ScopesSupported { get; init; } = Array.Empty<string>();

    /// <summary>服务端支持的 PKCE 挑战方法。</summary>
    public IReadOnlyList<string> CodeChallengeMethodsSupported { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 客户端注册结果。
/// </summary>
public sealed class McpOAuthClientRegistration
{
    /// <summary>客户端标识。</summary>
    public required string ClientId { get; init; }

    /// <summary>客户端密钥；公开客户端为 <c>null</c>。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>注册时提交的回调地址。</summary>
    public required Uri RedirectUri { get; init; }
}

/// <summary>
/// MCP 的 OAuth 2.1 协议客户端：发现、注册、授权码 + PKCE、令牌刷新。
/// </summary>
/// <remarks>
/// <para>
/// 只负责协议本身，不负责「什么时候该授权」——那属于 <see cref="McpOAuthTokenProvider"/>。
/// 这样拆分的好处是每一步都能单独测试，也便于替换其中某个环节（比如把动态注册换成企业预注册）。
/// </para>
/// <para>
/// 安全相关的强制项：
/// </para>
/// <list type="bullet">
///   <item>PKCE 固定使用 S256，授权服务器若不支持则直接失败，不降级到 plain；</item>
///   <item><c>state</c> 必须原样回传，否则中止授权（防 CSRF）；</item>
///   <item>令牌请求携带 <c>resource</c>（RFC 8707），使令牌只对当前 MCP 服务器有效，不能被拿去访问别的服务。</item>
/// </list>
/// </remarks>
public sealed class McpOAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly McpOAuthOptions _options;
    private readonly IMcpOAuthInteraction _interaction;
    private readonly ILogger? _logger;

    /// <summary>初始化 OAuth 客户端。</summary>
    /// <param name="resource">MCP 服务器地址，作为资源标识写入 <c>resource</c> 参数。</param>
    /// <param name="options">授权配置。</param>
    /// <param name="interaction">用户交互实现。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public McpOAuthClient(
        Uri resource,
        McpOAuthOptions options,
        IMcpOAuthInteraction interaction,
        ILogger? logger = null)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _logger = logger;

        if (options.HttpClient is { } provided)
        {
            _httpClient = provided;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _ownsHttpClient = true;
        }
    }

    /// <summary>MCP 服务器地址。</summary>
    public Uri Resource { get; }

    /// <summary>已发现的授权服务器元数据；未发现时为 <c>null</c>。</summary>
    public McpOAuthServerMetadata? Metadata { get; private set; }

    /// <summary>回调地址。</summary>
    public Uri RedirectUri => _interaction.RedirectUri;

    /// <summary>
    /// 发现授权服务器。
    /// </summary>
    /// <param name="resourceMetadataHint">来自 <c>WWW-Authenticate</c> 的 <c>resource_metadata</c> 提示，可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 探测顺序：受保护资源元数据（RFC 9728）→ 其中的 <c>authorization_servers</c> →
    /// 授权服务器元数据（RFC 8414，失败则回退 OIDC Discovery）。
    /// 任何一步缺失都有兜底：没有资源元数据就用服务器地址的 origin，没有注册端点就走预注册客户端。
    /// </remarks>
    public async ValueTask<McpOAuthServerMetadata> EnsureMetadataAsync(
        Uri? resourceMetadataHint = null,
        CancellationToken cancellationToken = default)
    {
        if (Metadata is { } cached)
            return cached;

        var resourceMetadata = _options.AuthorizationServer is null
            ? await FetchResourceMetadataAsync(resourceMetadataHint, cancellationToken).ConfigureAwait(false)
            : null;

        var authorizationServer =
            _options.AuthorizationServer
            ?? resourceMetadata?.AuthorizationServers.FirstOrDefault()
            ?? OriginOf(Resource);

        var metadata = await FetchServerMetadataAsync(authorizationServer, cancellationToken).ConfigureAwait(false);

        Metadata = metadata;

        _logger?.LogInformation(
            "OAuth 发现完成：授权服务器 {Issuer}，注册端点 {Registration}",
            metadata.Issuer,
            metadata.RegistrationEndpoint?.ToString() ?? "（无）");

        return metadata;
    }

    /// <summary>
    /// 取得客户端凭据：优先使用已缓存或预配置的，否则执行动态客户端注册。
    /// </summary>
    /// <param name="cachedClientId">上次注册得到的客户端标识。</param>
    /// <param name="cachedClientSecret">上次注册得到的客户端密钥。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<McpOAuthClientRegistration> EnsureRegistrationAsync(
        string? cachedClientId = null,
        string? cachedClientSecret = null,
        CancellationToken cancellationToken = default)
    {
        var clientId = cachedClientId ?? _options.ClientId;
        var clientSecret = cachedClientSecret ?? _options.ClientSecret;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return new McpOAuthClientRegistration
            {
                ClientId = clientId!,
                ClientSecret = clientSecret,
                RedirectUri = RedirectUri
            };
        }

        if (!_options.UseDynamicClientRegistration)
        {
            throw new McpOAuthException(
                "client_not_registered",
                "没有可用的 ClientId，且已禁用动态客户端注册（UseDynamicClientRegistration = false）。");
        }

        var metadata = Metadata ?? await EnsureMetadataAsync(null, cancellationToken).ConfigureAwait(false);

        if (metadata.RegistrationEndpoint is null)
        {
            throw new McpOAuthException(
                "registration_not_supported",
                $"授权服务器「{metadata.Issuer}」未提供 registration_endpoint，请通过 McpOAuthOptions.ClientId 预注册客户端。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["client_name"] = _options.ClientName,
            ["redirect_uris"] = new[] { RedirectUri.AbsoluteUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none"
        };

        if (_options.Scopes.Count > 0)
            payload["scope"] = string.Join(' ', _options.Scopes);

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient
            .PostAsync(metadata.RegistrationEndpoint, content, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ParseError(body);
            throw new McpOAuthException(
                error ?? $"registration_failed_{(int)response.StatusCode}",
                description ?? $"动态客户端注册失败：HTTP {(int)response.StatusCode}。");
        }

        ClientRegistrationDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClientRegistrationDto>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new McpOAuthException("invalid_registration_response", "注册端点返回的不是合法 JSON。", ex);
        }

        if (dto?.ClientId is not { Length: > 0 } registeredClientId)
        {
            throw new McpOAuthException("invalid_registration_response", "注册端点未返回 client_id。");
        }

        _logger?.LogInformation("已为 MCP 服务器完成动态客户端注册，client_id = {ClientId}", registeredClientId);

        return new McpOAuthClientRegistration
        {
            ClientId = registeredClientId,
            ClientSecret = dto.ClientSecret,
            RedirectUri = RedirectUri
        };
    }

    /// <summary>
    /// 走完整的授权码 + PKCE 流程，返回新的令牌。
    /// </summary>
    /// <param name="metadata">授权服务器元数据。</param>
    /// <param name="registration">客户端凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<McpOAuthToken> AuthorizeAsync(
        McpOAuthServerMetadata metadata,
        McpOAuthClientRegistration registration,
        CancellationToken cancellationToken = default)
    {
        // verifier 只存在于本次授权，不落盘；challenge 是它的 SHA-256。
        var verifier = PkceCode.CreateVerifier();
        var challenge = PkceCode.CreateChallenge(verifier);
        var state = PkceCode.CreateState();

        var callback = await _interaction.AuthorizeAsync(
                redirectUri => BuildAuthorizationUrl(metadata, registration.ClientId, redirectUri, challenge, state),
                cancellationToken)
            .ConfigureAwait(false);

        var parameters = QueryParameters.Parse(callback.Query);

        if (parameters.TryGetValue("error", out var error))
        {
            throw new McpOAuthException(
                error,
                parameters.GetValueOrDefault("error_description") ?? $"授权服务器返回错误：{error}。");
        }

        if (!parameters.TryGetValue("state", out var returnedState) ||
            !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new McpOAuthException(
                "invalid_state",
                "授权回调中的 state 与请求不一致，已中止（可能遭遇 CSRF）。");
        }

        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new McpOAuthException("invalid_request", "授权回调中没有 authorization code。");
        }

        return await ExchangeCodeAsync(metadata, registration, code, RedirectUri, verifier, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>用刷新令牌换取新的访问令牌。</summary>
    /// <param name="metadata">授权服务器元数据。</param>
    /// <param name="registration">客户端凭据。</param>
    /// <param name="refreshToken">刷新令牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask<McpOAuthToken> RefreshAsync(
        McpOAuthServerMetadata metadata,
        McpOAuthClientRegistration registration,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = registration.ClientId,
            ["resource"] = Resource.AbsoluteUri
        };

        AddClientSecret(form, registration.ClientSecret);

        return RequestTokenAsync(metadata.TokenEndpoint, form, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async ValueTask<McpOAuthToken> ExchangeCodeAsync(
        McpOAuthServerMetadata metadata,
        McpOAuthClientRegistration registration,
        string code,
        Uri redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["client_id"] = registration.ClientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = Resource.AbsoluteUri
        };

        AddClientSecret(form, registration.ClientSecret);

        return await RequestTokenAsync(metadata.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
    }

    private static void AddClientSecret(IDictionary<string, string> form, string? clientSecret)
    {
        if (!string.IsNullOrEmpty(clientSecret))
            form["client_secret"] = clientSecret;
    }

    private async ValueTask<McpOAuthToken> RequestTokenAsync(
        Uri tokenEndpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ParseError(body);
            throw new McpOAuthException(
                error ?? $"token_request_failed_{(int)response.StatusCode}",
                description ?? $"令牌端点返回 HTTP {(int)response.StatusCode}。");
        }

        McpOAuthToken? token;
        try
        {
            token = JsonSerializer.Deserialize<McpOAuthToken>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new McpOAuthException("invalid_token_response", "令牌端点返回的不是合法 JSON。", ex);
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new McpOAuthException("invalid_token_response", "令牌端点未返回 access_token。");

        if (token.ExpiresIn is { } seconds && seconds > 0)
            token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);

        return token;
    }

    private Uri BuildAuthorizationUrl(
        McpOAuthServerMetadata metadata,
        string clientId,
        Uri redirectUri,
        string codeChallenge,
        string state)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("state", state),
            // RFC 8707：把令牌绑定到本 MCP 服务器，避免拿到通用受众的令牌后被重放。
            new("resource", Resource.AbsoluteUri)
        };

        if (_options.Scopes.Count > 0)
            parameters.Add(new KeyValuePair<string, string>("scope", string.Join(' ', _options.Scopes)));

        var endpoint = metadata.AuthorizationEndpoint.AbsoluteUri;
        var separator = endpoint.Contains('?') ? '&' : '?';

        return new Uri(endpoint + separator + QueryParameters.Build(parameters));
    }

    private async ValueTask<ProtectedResourceMetadata?> FetchResourceMetadataAsync(
        Uri? hint,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Uri>();

        if (hint is not null)
            candidates.Add(hint);

        candidates.Add(WellKnown(Resource, "oauth-protected-resource"));

        foreach (var candidate in candidates)
        {
            using var document = await TryGetJsonAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (document is null)
                continue;

            var dto = document.RootElement.Deserialize<ProtectedResourceDto>(JsonOptions);
            if (dto is null)
                continue;

            var servers = (dto.AuthorizationServers ?? new List<string>())
                .Where(value => Uri.TryCreate(value, UriKind.Absolute, out _))
                .Select(value => new Uri(value))
                .ToList();

            _logger?.LogDebug(
                "受保护资源元数据：{Uri}，授权服务器 {Count} 个。",
                candidate,
                servers.Count);

            return new ProtectedResourceMetadata(servers, dto.ScopesSupported ?? new List<string>());
        }

        return null;
    }

    private async ValueTask<McpOAuthServerMetadata> FetchServerMetadataAsync(
        Uri authorizationServer,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            WellKnown(authorizationServer, "oauth-authorization-server"),
            new Uri(authorizationServer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration")
        };

        foreach (var candidate in candidates)
        {
            using var document = await TryGetJsonAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (document is null)
                continue;

            ServerMetadataDto? dto;
            try
            {
                dto = document.RootElement.Deserialize<ServerMetadataDto>(JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new McpOAuthException("invalid_metadata", $"{candidate} 的元数据结构非法。", ex);
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.AuthorizationEndpoint) ||
                string.IsNullOrWhiteSpace(dto.TokenEndpoint))
            {
                continue;
            }

            // 规范要求必须支持 S256；服务端显式列出了方法却缺少 S256 时，不能退化成 plain。
            if (dto.CodeChallengeMethodsSupported is { Count: > 0 } methods &&
                !methods.Contains("S256", StringComparer.Ordinal))
            {
                throw new McpOAuthException(
                    "unsupported_challenge_method",
                    $"授权服务器 {authorizationServer} 不支持 PKCE S256（支持：{string.Join(", ", methods)}）。");
            }

            return new McpOAuthServerMetadata
            {
                Issuer = dto.Issuer ?? authorizationServer.AbsoluteUri,
                AuthorizationEndpoint = new Uri(dto.AuthorizationEndpoint),
                TokenEndpoint = new Uri(dto.TokenEndpoint),
                RegistrationEndpoint = Uri.TryCreate(dto.RegistrationEndpoint, UriKind.Absolute, out var registration)
                    ? registration
                    : null,
                ScopesSupported = dto.ScopesSupported ?? new List<string>(),
                CodeChallengeMethodsSupported = dto.CodeChallengeMethodsSupported ?? new List<string>()
            };
        }

        throw new McpOAuthException(
            "discovery_failed",
            $"无法从 {authorizationServer} 获取授权服务器元数据（已尝试 RFC 8414 与 OIDC Discovery）。");
    }

    private async ValueTask<JsonDocument?> TryGetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new McpOAuthException("discovery_failed", $"无法访问 {uri}：{ex.Message}", ex);
        }

        using (response)
        {
            // 404 等「这个探测路径不存在」的情况交给调用方继续尝试下一个候选。
            if (response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.BadRequest
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new McpOAuthException(
                    "discovery_failed",
                    $"访问 {uri} 返回 HTTP {(int)response.StatusCode}。");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new McpOAuthException("discovery_failed", $"{uri} 返回的不是合法 JSON。", ex);
            }
        }
    }

    private static (string? Error, string? Description) ParseError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            var error = document.RootElement.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;

            var description = document.RootElement.TryGetProperty("error_description", out var descriptionElement) &&
                              descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            return (error, description);
        }
        catch (JsonException)
        {
            return (null, string.IsNullOrWhiteSpace(body) ? null : Truncate(body, 300));
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    /// <summary>
    /// 按 RFC 8414 / 9728 的规则拼接 well-known 地址：把 <c>/.well-known/{document}</c>
    /// 插在主机之后、路径之前。
    /// </summary>
    private static Uri WellKnown(Uri baseUri, string document)
    {
        var origin = $"{baseUri.Scheme}://{baseUri.Authority}";
        var path = baseUri.AbsolutePath.TrimEnd('/');

        return new Uri($"{origin}/.well-known/{document}{path}");
    }

    private static Uri OriginOf(Uri uri) => new($"{uri.Scheme}://{uri.Authority}");

    private sealed record ProtectedResourceMetadata(
        IReadOnlyList<Uri> AuthorizationServers,
        IReadOnlyList<string> ScopesSupported);

    private sealed class ProtectedResourceDto
    {
        [JsonPropertyName("resource")]
        public string? Resource { get; set; }

        [JsonPropertyName("authorization_servers")]
        public List<string>? AuthorizationServers { get; set; }

        [JsonPropertyName("scopes_supported")]
        public List<string>? ScopesSupported { get; set; }
    }

    private sealed class ServerMetadataDto
    {
        [JsonPropertyName("issuer")]
        public string? Issuer { get; set; }

        [JsonPropertyName("authorization_endpoint")]
        public string? AuthorizationEndpoint { get; set; }

        [JsonPropertyName("token_endpoint")]
        public string? TokenEndpoint { get; set; }

        [JsonPropertyName("registration_endpoint")]
        public string? RegistrationEndpoint { get; set; }

        [JsonPropertyName("scopes_supported")]
        public List<string>? ScopesSupported { get; set; }

        [JsonPropertyName("code_challenge_methods_supported")]
        public List<string>? CodeChallengeMethodsSupported { get; set; }
    }

    private sealed class ClientRegistrationDto
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; set; }
    }
}

/// <summary>
/// PKCE（RFC 7636）参数生成。
/// </summary>
internal static class PkceCode
{
    /// <summary>
    /// 生成 code_verifier：32 字节随机数的 base64url 编码，长度 43，落在规范要求的 43~128 之间。
    /// </summary>
    public static string CreateVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>生成 code_challenge：<c>base64url(SHA256(ASCII(verifier)))</c>。</summary>
    public static string CreateChallenge(string verifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>生成 state：16 字节随机数，用于关联回调并防 CSRF。</summary>
    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// 查询串的构建与解析。
/// </summary>
internal static class QueryParameters
{
    public static string Build(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join(
            '&',
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    public static Dictionary<string, string> Parse(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));

            result[key] = value;
        }

        return result;
    }
}
