// ============================================================================
// 文件：McpOAuthTokenProvider.cs
// 用途：基于 OAuth 2.1 的令牌提供者：缓存 → 刷新 → 交互式授权，按需逐级降级。
// 实现方：McpOAuthTokenProvider
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 基于 OAuth 2.1 的令牌提供者：缓存 → 刷新 → 交互式授权，按需逐级降级。
/// </summary>
/// <remarks>
/// <para>
/// 这是 MCP 客户端的默认授权实现。它在每次取令牌时依次尝试：
/// </para>
/// <list type="number">
///   <item>缓存中未过期的访问令牌 —— 绝大多数请求走这条路径，零网络开销；</item>
///   <item>用刷新令牌换新令牌 —— 访问令牌过期但授权仍然有效时使用；</item>
///   <item>完整的授权码 + PKCE 流程 —— 首次使用，或刷新令牌已被服务端吊销时使用。</item>
/// </list>
/// <para>
/// 并发控制：刷新与授权都持有同一把异步锁。多个请求同时发现令牌过期时，只会有一个真正去刷新，
/// 其余等待后直接复用结果，避免把令牌端点打爆、也避免用户看到多个授权窗口。
/// </para>
/// <para>
/// 会话（令牌 + 动态注册得到的 client_id）默认落盘到用户本地应用数据目录，
/// 因此重启进程不会重复授权，也不会重复注册客户端。
/// </para>
/// </remarks>
public sealed class McpOAuthTokenProvider : IMcpTokenProvider, IAsyncDisposable
{
    private readonly McpOAuthOptions _options;
    private readonly McpOAuthClient _client;
    private readonly IMcpOAuthStore _store;
    private readonly ILogger? _logger;
    private readonly string _cacheKey;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Uri? _resourceMetadataHint;
    private int _disposed;

    /// <summary>初始化令牌提供者。</summary>
    /// <param name="serverName">服务器名称，参与缓存键与日志。</param>
    /// <param name="resource">MCP 服务器地址，作为 OAuth 资源标识。</param>
    /// <param name="options">授权配置。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public McpOAuthTokenProvider(
        string serverName,
        Uri resource,
        McpOAuthOptions options,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;
        ServerName = serverName;

        var interaction = options.Interaction
                          ?? new LoopbackOAuthInteraction(
                              options.RedirectUri,
                              options.OpenBrowser,
                              options.AuthorizationTimeout,
                              logger);

        _client = new McpOAuthClient(resource, options, interaction, logger);
        _store = options.Store ?? new FileMcpOAuthStore();
        _cacheKey = $"{serverName}|{resource.AbsoluteUri}";
    }

    /// <summary>服务器名称。</summary>
    public string ServerName { get; }

    /// <inheritdoc />
    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await LoadSessionAsync(cancellationToken).ConfigureAwait(false);

            if (session.Token is { } token &&
                !string.IsNullOrEmpty(token.AccessToken) &&
                !token.IsExpired(_options.ClockSkew))
            {
                return token.AccessToken;
            }

            if (session.Token?.RefreshToken is { Length: > 0 } refreshToken)
            {
                var refreshed = await TryRefreshAsync(session, refreshToken, cancellationToken).ConfigureAwait(false);
                if (refreshed is not null)
                    return refreshed.AccessToken;
            }

            var authorized = await AuthorizeAsync(session, cancellationToken).ConfigureAwait(false);
            return authorized.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> RefreshAsync(
        McpOAuthChallenge? challenge = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (challenge?.ResourceMetadata is { } hint)
            _resourceMetadataHint = hint;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await LoadSessionAsync(cancellationToken).ConfigureAwait(false);

            if (session.Token?.RefreshToken is { Length: > 0 } refreshToken)
            {
                var refreshed = await TryRefreshAsync(session, refreshToken, cancellationToken).ConfigureAwait(false);
                if (refreshed is not null)
                    return refreshed.AccessToken;
            }

            // 刷新令牌缺失或已失效：只能重新走交互式授权。
            var authorized = await AuthorizeAsync(session, cancellationToken).ConfigureAwait(false);
            return authorized.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _client.Dispose();
        _lock.Dispose();

        return ValueTask.CompletedTask;
    }

    private async ValueTask<McpOAuthToken?> TryRefreshAsync(
        McpOAuthSession session,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _client.EnsureMetadataAsync(_resourceMetadataHint, cancellationToken).ConfigureAwait(false);
            var (clientId, clientSecret) = SelectRegistration(session, metadata);

            var registration = await _client
                .EnsureRegistrationAsync(clientId, clientSecret, cancellationToken)
                .ConfigureAwait(false);

            var token = await _client
                .RefreshAsync(metadata, registration, refreshToken, cancellationToken)
                .ConfigureAwait(false);

            // 服务端可能选择不轮换刷新令牌，此时沿用原来的。
            token.RefreshToken ??= refreshToken;

            await SaveAsync(session, token, registration, metadata, cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("MCP 服务器「{Name}」的访问令牌已刷新。", ServerName);

            return token;
        }
        catch (McpOAuthException ex) when (ex.RequiresReauthorization)
        {
            _logger?.LogInformation(
                "MCP 服务器「{Name}」的刷新令牌已失效（{Error}），将重新授权。", ServerName, ex.Error);

            return null;
        }
    }

    private async ValueTask<McpOAuthToken> AuthorizeAsync(
        McpOAuthSession session,
        CancellationToken cancellationToken)
    {
        var metadata = await _client.EnsureMetadataAsync(_resourceMetadataHint, cancellationToken).ConfigureAwait(false);
        var (clientId, clientSecret) = SelectRegistration(session, metadata);

        var registration = await _client
            .EnsureRegistrationAsync(clientId, clientSecret, cancellationToken)
            .ConfigureAwait(false);

        _logger?.LogInformation("MCP 服务器「{Name}」需要用户授权，即将打开授权页面。", ServerName);

        var token = await _client.AuthorizeAsync(metadata, registration, cancellationToken).ConfigureAwait(false);

        await SaveAsync(session, token, registration, metadata, cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation("MCP 服务器「{Name}」授权完成。", ServerName);

        return token;
    }

    /// <summary>
    /// 判断缓存的客户端注册是否仍然适用。
    /// </summary>
    /// <remarks>
    /// 配置里的授权服务器换掉之后，旧 <c>client_id</c> 在新服务器上大概率不存在，
    /// 继续使用只会得到 <c>invalid_client</c>，因此这里主动丢弃并重新注册。
    /// </remarks>
    private static (string? ClientId, string? ClientSecret) SelectRegistration(
        McpOAuthSession session,
        McpOAuthServerMetadata metadata)
    {
        if (string.IsNullOrEmpty(session.ClientId))
            return (null, null);

        if (session.AuthorizationServer is { Length: > 0 } issuer &&
            !string.Equals(issuer.TrimEnd('/'), metadata.Issuer.TrimEnd('/'), StringComparison.Ordinal))
        {
            return (null, null);
        }

        return (session.ClientId, session.ClientSecret);
    }

    private async ValueTask<McpOAuthSession> LoadSessionAsync(CancellationToken cancellationToken)
    {
        var session = await _store.GetAsync(_cacheKey, cancellationToken).ConfigureAwait(false);

        return session ?? new McpOAuthSession();
    }

    private async ValueTask SaveAsync(
        McpOAuthSession session,
        McpOAuthToken token,
        McpOAuthClientRegistration registration,
        McpOAuthServerMetadata metadata,
        CancellationToken cancellationToken)
    {
        session.Token = token;
        session.ClientId = registration.ClientId;
        session.ClientSecret = registration.ClientSecret;
        session.AuthorizationServer = metadata.Issuer;

        await _store.SetAsync(_cacheKey, session, cancellationToken).ConfigureAwait(false);
    }
}
