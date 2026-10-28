// ============================================================================
// 文件：McpOAuthOptions.cs
// 用途：MCP 的 OAuth 2.1 授权配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpServerConfig、McpOAuthClient
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 的 OAuth 2.1 授权配置。
/// </summary>
/// <remarks>
/// <para>
/// 默认行为贴近规范推荐路径：先用 <c>401</c> 响应或 <c>/.well-known/oauth-protected-resource</c>
/// 发现受保护资源与授权服务器，再按需做动态客户端注册（RFC 7591），
/// 最后用授权码 + PKCE（S256）拿令牌，并把 <c>resource</c> 参数（RFC 8707）带上，
/// 使令牌只对当前 MCP 服务器有效。
/// </para>
/// <para>
/// 若企业环境已预注册客户端，填好 <see cref="ClientId"/>（与可选的 <see cref="ClientSecret"/>）
/// 即可跳过动态注册；若授权服务器地址已知，填 <see cref="AuthorizationServer"/> 可跳过资源元数据探测。
/// </para>
/// </remarks>
public sealed class McpOAuthOptions
{
    /// <summary>预注册的客户端标识；为 <c>null</c> 时按 <see cref="UseDynamicClientRegistration"/> 决定是否自动注册。</summary>
    public string? ClientId { get; set; }

    /// <summary>预注册的客户端密钥；公开客户端（PKCE）留空。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>请求的授权范围；为空时不带 <c>scope</c> 参数，由服务端决定默认范围。</summary>
    public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();

    /// <summary>直接指定授权服务器，跳过受保护资源元数据的发现。</summary>
    public Uri? AuthorizationServer { get; set; }

    /// <summary>
    /// 回调地址；为 <c>null</c> 时自动选择 <c>http://127.0.0.1:{空闲端口}/callback</c>。
    /// </summary>
    /// <remarks>
    /// 规范要求回调地址必须是 loopback（<c>127.0.0.1</c> 或 <c>localhost</c>），
    /// 非回环地址会导致授权服务器拒绝。
    /// </remarks>
    public Uri? RedirectUri { get; set; }

    /// <summary>是否自动打开浏览器，默认 <c>true</c>。无图形环境时应关掉并自行展示授权链接。</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>等待用户完成授权的最长时间，默认 5 分钟。</summary>
    public TimeSpan AuthorizationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>判断令牌过期时的提前量，默认 60 秒。</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>是否在缺少预注册客户端时执行动态客户端注册，默认 <c>true</c>。</summary>
    public bool UseDynamicClientRegistration { get; set; } = true;

    /// <summary>注册到授权服务器时展示的客户端名称。</summary>
    public string ClientName { get; set; } = "Mono.LLM.Agent";

    /// <summary>会话存储；为 <c>null</c> 时使用 <see cref="FileMcpOAuthStore"/> 落盘。</summary>
    public IMcpOAuthStore? Store { get; set; }

    /// <summary>自定义的用户交互实现；为 <c>null</c> 时使用本机回环监听并自动打开浏览器。</summary>
    public IMcpOAuthInteraction? Interaction { get; set; }

    /// <summary>自定义 <see cref="HttpClient"/>（代理、证书、测试桩）；为 <c>null</c> 时由内部创建。</summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// OAuth 授权过程中与用户交互的抽象。
/// </summary>
/// <remarks>
/// 把「让用户去授权」这件事隔离开，才能让整条链路在无浏览器环境（CI、服务端、单元测试）里被验证。
/// 生产实现是 <see cref="LoopbackOAuthInteraction"/>，测试通常用 <see cref="DelegateOAuthInteraction"/>。
/// </remarks>
public interface IMcpOAuthInteraction
{
    /// <summary>
    /// 回调地址。必须在动态注册之前就能确定，因为注册请求要提交它。
    /// </summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// 引导用户完成授权并返回回调地址（含查询串）。
    /// </summary>
    /// <param name="createAuthorizationUrl">用回调地址构造授权地址的工厂方法。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>浏览器最终命中的回调地址。</returns>
    Task<Uri> AuthorizeAsync(Func<Uri, Uri> createAuthorizationUrl, CancellationToken cancellationToken);
}
