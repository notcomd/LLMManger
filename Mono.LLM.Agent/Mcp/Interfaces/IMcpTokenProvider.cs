// ============================================================================
// 文件：IMcpTokenProvider.cs
// 用途：为 MCP 传输提供访问令牌的抽象。
// 实现方：McpOAuthTokenProvider
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 为 MCP 传输提供访问令牌的抽象。
/// </summary>
/// <remarks>
/// 把「取令牌」与「发请求」解耦：传输层只负责把令牌塞进 <c>Authorization</c> 头，
/// 令牌怎么来（OAuth、静态配置、外部密钥服务）由实现决定。
/// </remarks>
public interface IMcpTokenProvider
{
    /// <summary>
    /// 取一个可用的访问令牌；返回 <c>null</c> 表示当前没有可用令牌（不添加认证头）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 实现应在令牌仍然有效时直接返回缓存值，即将过期时才刷新，避免每次请求都打令牌端点。
    /// </remarks>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 强制刷新令牌，用于服务端返回 401 之后。
    /// </summary>
    /// <param name="challenge">服务端给出的认证挑战，可从中拿到资源元数据地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 没有刷新令牌或刷新令牌已失效时，实现可以退回到完整的交互式授权流程。
    /// </remarks>
    ValueTask<string?> RefreshAsync(
        McpOAuthChallenge? challenge = null,
        CancellationToken cancellationToken = default);
}
