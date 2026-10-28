// ============================================================================
// 文件：IMcpTransport.cs
// 用途：MCP 传输层抽象：负责把 JSON-RPC 消息可靠地送达对端，并把对端消息回调给客户端。
// 实现方：StdioMcpTransport、StreamableHttpMcpTransport
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 传输层抽象：负责把 JSON-RPC 消息可靠地送达对端，并把对端消息回调给客户端。
/// </summary>
/// <remarks>
/// <para>
/// 传输层只关心「字节怎么走」，不关心方法语义——握手、能力协商、请求/响应配对全部由
/// <see cref="McpClient"/> 完成。这样 stdio 与 HTTP 两种传输可以共用同一套协议实现。
/// </para>
/// <para>
/// 生命周期：<see cref="StartAsync"/> → 多次 <see cref="SendAsync"/> → <see cref="CloseAsync"/>。
/// 收到对端消息时调用 <see cref="MessageHandler"/>；连接意外中断时触发 <see cref="Closed"/>。
/// 两个入口都不会抛出到调用方，异常由传输层自行记录。
/// </para>
/// </remarks>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>传输名称，通常等于配置中的服务器名，用于日志与工具命名前缀。</summary>
    string Name { get; }

    /// <summary>对端消息的处理器，由 <see cref="McpClient"/> 在启动前设置。</summary>
    /// <remarks>
    /// 传输层在读取循环中顺序调用，因此实现应尽快返回，避免阻塞后续消息。
    /// </remarks>
    Func<JsonRpcMessage, Task>? MessageHandler { get; set; }

    /// <summary>协商后的协议版本，由客户端在初始化完成后回写。</summary>
    /// <remarks>
    /// 供 Streamable HTTP 传输写入 <c>MCP-Protocol-Version</c> 请求头；stdio 传输忽略该值。
    /// </remarks>
    string? ProtocolVersion { get; set; }

    /// <summary>连接关闭（正常关闭或意外中断）时触发，只触发一次。</summary>
    event EventHandler<McpTransportClosedEventArgs>? Closed;

    /// <summary>启动传输（stdio 拉起子进程、HTTP 建立服务端消息流）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>发送一条消息。</summary>
    /// <param name="message">待发送的请求、响应或通知。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);

    /// <summary>关闭传输并释放底层资源，可重复调用。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 传输关闭事件参数。
/// </summary>
public sealed class McpTransportClosedEventArgs : EventArgs
{
    /// <summary>关闭原因（进程 stderr 尾部内容、HTTP 错误说明等），正常关闭时为 <c>null</c>。</summary>
    public string? Reason { get; init; }

    /// <summary>子进程退出码；非 stdio 传输或进程未退出时为 <c>null</c>。</summary>
    public int? ExitCode { get; init; }

    /// <summary>导致关闭的异常，正常关闭时为 <c>null</c>。</summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// 传输类型。
/// </summary>
public enum McpTransportKind
{
    /// <summary>通过子进程的标准输入输出通信，本地 MCP 服务器的默认方式。</summary>
    Stdio,

    /// <summary>通过 HTTP POST + SSE 通信，适用于远程或容器化的 MCP 服务器。</summary>
    StreamableHttp
}
