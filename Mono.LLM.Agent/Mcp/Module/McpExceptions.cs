// ============================================================================
// 文件：McpExceptions.cs
// 用途：MCP 协议交互失败时抛出的异常基类。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient、各传输实现、调用方 catch 分支
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 协议交互失败时抛出的异常基类。
/// </summary>
/// <remarks>
/// <see cref="Code"/> 与 JSON-RPC 错误码一一对应：服务端返回错误响应时使用报文中的
/// <c>error.code</c>，客户端本地产生的错误（超时、连接关闭）使用 <see cref="McpErrorCodes"/> 中的约定值。
/// 通过错误码可以区分「模型参数不对」（<see cref="McpErrorCodes.InvalidParams"/>）、
/// 「服务端没有这个方法」（<see cref="McpErrorCodes.MethodNotFound"/>）等不同处理策略。
/// </remarks>
public class McpException : Exception
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="code">JSON-RPC 错误码。</param>
    /// <param name="message">错误描述。</param>
    /// <param name="errorData">服务端返回的附加错误数据。</param>
    /// <param name="innerException">内部异常。</param>
    public McpException(int code, string message, JsonElement? errorData = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ErrorData = errorData;
    }

    /// <summary>JSON-RPC 错误码。</summary>
    public int Code { get; }

    /// <summary>服务端返回的原始 <c>error.data</c>；不存在时为 <c>null</c>。</summary>
    public JsonElement? ErrorData { get; }
}

/// <summary>
/// 协议层错误：报文格式非法、握手版本不受支持、状态机被破坏等。
/// </summary>
public sealed class McpProtocolException : McpException
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="message">错误描述。</param>
    /// <param name="code">错误码，默认 <see cref="McpErrorCodes.InvalidRequest"/>。</param>
    /// <param name="innerException">内部异常。</param>
    public McpProtocolException(string message, int code = McpErrorCodes.InvalidRequest, Exception? innerException = null)
        : base(code, message, null, innerException)
    {
    }
}

/// <summary>
/// 连接错误：传输未启动、进程退出、HTTP 请求失败或会话已过期。
/// </summary>
/// <remarks>
/// 收到该异常通常意味着应当重建 <see cref="McpClient"/>，而不是重试同一个请求。
/// </remarks>
public sealed class McpConnectionClosedException : McpException
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="message">错误描述。</param>
    /// <param name="innerException">内部异常。</param>
    public McpConnectionClosedException(string message, Exception? innerException = null)
        : base(McpErrorCodes.ConnectionClosed, message, null, innerException)
    {
    }
}

/// <summary>
/// 请求超时：在约定的时限内没有收到服务端响应。
/// </summary>
public sealed class McpRequestTimeoutException : McpException
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="method">超时的方法名。</param>
    /// <param name="timeout">生效的超时时长。</param>
    public McpRequestTimeoutException(string method, TimeSpan timeout)
        : base(McpErrorCodes.RequestTimeout, $"MCP 请求「{method}」在 {timeout.TotalSeconds:0.##} 秒内没有收到响应。")
    {
        Method = method;
        Timeout = timeout;
    }

    /// <summary>超时的方法名。</summary>
    public string Method { get; }

    /// <summary>生效的超时时长。</summary>
    public TimeSpan Timeout { get; }
}
