// ============================================================================
// 文件：McpClientEvents.cs
// 用途：MCP 客户端事件参数：列表变更、日志与进度通知。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient（事件发布）、调用方订阅
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 可变化的列表种类。
/// </summary>
public enum McpListKind
{
    /// <summary>工具列表（<c>tools/list</c>）。</summary>
    Tools,

    /// <summary>资源列表（<c>resources/list</c>）。</summary>
    Resources,

    /// <summary>资源模板列表（<c>resources/templates/list</c>）。</summary>
    ResourceTemplates,

    /// <summary>提示词列表（<c>prompts/list</c>）。</summary>
    Prompts
}

/// <summary>
/// 服务端宣告某个列表发生变化的事件参数。
/// </summary>
/// <remarks>
/// 收到该通知后应重新拉取对应列表：工具桥接层会据此刷新 <see cref="Tools.ToolManager"/> 中的注册项。
/// </remarks>
public sealed class McpListChangedEventArgs : EventArgs
{
    /// <summary>发起通知的服务器名称。</summary>
    public required string ServerName { get; init; }

    /// <summary>发生变化的列表种类。</summary>
    public McpListKind Kind { get; init; }
}

/// <summary>
/// 已订阅资源内容发生变化的事件参数。
/// </summary>
public sealed class McpResourceUpdatedEventArgs : EventArgs
{
    /// <summary>发起通知的服务器名称。</summary>
    public required string ServerName { get; init; }

    /// <summary>内容已变化的资源 URI。</summary>
    public string Uri { get; init; } = string.Empty;
}

/// <summary>
/// 服务端日志消息的事件参数（对应 <c>notifications/message</c>）。
/// </summary>
public sealed class McpLogMessageEventArgs : EventArgs
{
    /// <summary>发起日志的服务器名称。</summary>
    public required string ServerName { get; init; }

    /// <summary>日志级别。</summary>
    public McpLogLevel Level { get; init; }

    /// <summary>日志来源标识，可选。</summary>
    public string? Logger { get; init; }

    /// <summary>原始日志数据，可能是字符串、对象或数组。</summary>
    public System.Text.Json.JsonElement Data { get; init; }
}

/// <summary>
/// 长任务进度通知的事件参数（对应 <c>notifications/progress</c>）。
/// </summary>
public sealed class McpProgressEventArgs : EventArgs
{
    /// <summary>发起通知的服务器名称。</summary>
    public required string ServerName { get; init; }

    /// <summary>进度令牌，用于把通知关联到具体请求。</summary>
    public string? ProgressToken { get; init; }

    /// <summary>已完成量。</summary>
    public double Progress { get; init; }

    /// <summary>总量；服务端未知时为 <c>null</c>。</summary>
    public double? Total { get; init; }

    /// <summary>进度说明。</summary>
    public string? Message { get; init; }
}
