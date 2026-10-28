// ============================================================================
// 文件：McpClientOptions.cs
// 用途：McpClient 的行为配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient（构造时读取）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// <see cref="McpClient"/> 的行为配置。
/// </summary>
/// <remarks>
/// 除超时与分页上限外，这里的回调决定了客户端「愿意为服务端做哪些事」：
/// 只有提供了对应处理器，客户端才会在握手时声明 <c>roots</c>、<c>sampling</c>、
/// <c>elicitation</c> 能力。不声明也不实现，是对服务端最诚实的答复。
/// </remarks>
public sealed class McpClientOptions
{
    /// <summary>客户端名称，写入 <c>clientInfo.name</c>。</summary>
    public string ClientName { get; set; } = "Mono.LLM.Agent";

    /// <summary>客户端版本，写入 <c>clientInfo.version</c>；为 <c>null</c> 时取程序集版本。</summary>
    public string? ClientVersion { get; set; }

    /// <summary>
    /// 期望的协议版本；为 <c>null</c> 时使用 <see cref="McpProtocol.LatestVersion"/>。
    /// </summary>
    /// <remarks>
    /// 这只是客户端的偏好：最终以服务端回显的版本为准，不支持的版本会导致握手失败。
    /// </remarks>
    public string? ProtocolVersion { get; set; }

    /// <summary>普通请求的超时时间，默认 60 秒。设为 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 可关闭。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 工具调用的超时时间，默认 5 分钟。
    /// </summary>
    /// <remarks>
    /// 工具可能执行外部操作（抓网页、跑脚本、查数据库），耗时远超普通元数据请求，
    /// 因此单独给一个更宽松的上限。
    /// </remarks>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>列表分页的最大页数，默认 100。用于防止服务端返回异常的 <c>nextCursor</c> 造成死循环。</summary>
    public int MaxPaginationPages { get; set; } = 100;

    /// <summary>
    /// 处理服务端 <c>sampling/createMessage</c> 请求的回调：接收请求参数，返回采样结果。
    /// </summary>
    /// <remarks>
    /// 提供该回调即表示客户端支持采样，握手时会声明 <c>sampling</c> 能力。
    /// 典型实现是把请求转给本地的 <c>ILLMClient</c>，让服务端复用客户端的模型。
    /// </remarks>
    public Func<JsonElement, CancellationToken, Task<JsonElement>>? SamplingHandler { get; set; }

    /// <summary>
    /// 处理服务端 <c>roots/list</c> 请求的回调：返回形如 <c>{"roots":[{"uri":"file:///work","name":"work"}]}</c> 的对象。
    /// </summary>
    public Func<CancellationToken, Task<JsonElement>>? RootsHandler { get; set; }

    /// <summary>
    /// 处理服务端 <c>elicitation/create</c> 请求的回调：向用户索取补充信息并返回结果。
    /// </summary>
    public Func<JsonElement, CancellationToken, Task<JsonElement>>? ElicitationHandler { get; set; }

    /// <summary>解析后的客户端版本。</summary>
    internal string ResolvedClientVersion =>
        string.IsNullOrWhiteSpace(ClientVersion)
            ? typeof(McpClientOptions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
            : ClientVersion!;

    /// <summary>解析后的协议版本。</summary>
    internal string ResolvedProtocolVersion =>
        string.IsNullOrWhiteSpace(ProtocolVersion) ? McpProtocol.LatestVersion : ProtocolVersion!;
}
