// ============================================================================
// 文件：McpModels.cs
// 用途：MCP 协议数据模型：工具、资源、提示词与能力声明。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient、McpToolBridge、McpClientManager
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 服务端或客户端的实现信息（规范字段 <c>serverInfo</c> / <c>clientInfo</c>）。
/// </summary>
public sealed class McpImplementation
{
    /// <summary>实现名称，例如 <c>filesystem-server</c>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可选的展示标题。</summary>
    public string? Title { get; set; }

    /// <summary>实现版本。</summary>
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// 支持 <c>list_changed</c> 通知的能力声明（tools、prompts 共用该结构）。
/// </summary>
public sealed class McpListChangedCapability
{
    /// <summary>列表变化时是否推送通知。</summary>
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

/// <summary>
/// 资源能力声明。
/// </summary>
public sealed class McpResourcesCapability
{
    /// <summary>是否支持 <c>resources/subscribe</c>。</summary>
    public bool Subscribe { get; set; }

    /// <summary>资源列表变化时是否推送通知。</summary>
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

/// <summary>
/// 服务端能力的完整声明。
/// </summary>
/// <remarks>
/// 规范用「字段是否存在」表达「能力是否支持」，因此可空引用类型在这里有语义：
/// <see cref="Tools"/> 为 <c>null</c> 就表示服务端不提供工具。
/// </remarks>
public sealed class McpServerCapabilities
{
    /// <summary>日志能力。</summary>
    public JsonElement? Logging { get; set; }

    /// <summary>提示词能力。</summary>
    public McpListChangedCapability? Prompts { get; set; }

    /// <summary>资源能力。</summary>
    public McpResourcesCapability? Resources { get; set; }

    /// <summary>工具能力。</summary>
    public McpListChangedCapability? Tools { get; set; }

    /// <summary>参数补全能力。</summary>
    public JsonElement? Completions { get; set; }

    /// <summary>是否声明了工具能力。</summary>
    [JsonIgnore]
    public bool SupportsTools => Tools is not null;

    /// <summary>是否声明了资源能力。</summary>
    [JsonIgnore]
    public bool SupportsResources => Resources is not null;

    /// <summary>是否声明了提示词能力。</summary>
    [JsonIgnore]
    public bool SupportsPrompts => Prompts is not null;
}

/// <summary>
/// <c>initialize</c> 的响应结果。
/// </summary>
public sealed class McpInitializeResult
{
    /// <summary>服务端选定并回显的协议版本。</summary>
    public string ProtocolVersion { get; set; } = string.Empty;

    /// <summary>服务端能力。</summary>
    public McpServerCapabilities Capabilities { get; set; } = new();

    /// <summary>服务端实现信息。</summary>
    public McpImplementation ServerInfo { get; set; } = new();

    /// <summary>服务端给出的使用说明，可直接拼进系统提示词。</summary>
    public string? Instructions { get; set; }

    /// <summary>日志能力是否可用。</summary>
    [JsonIgnore]
    public bool SupportsLogging => Capabilities.Logging is not null;
}

/// <summary>
/// 服务端暴露的工具定义。
/// </summary>
public sealed class McpTool
{
    /// <summary>工具名称，服务端内唯一。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可选的展示标题。</summary>
    public string? Title { get; set; }

    /// <summary>工具说明，模型据此判断何时调用。</summary>
    public string? Description { get; set; }

    /// <summary>参数的 JSON Schema，标准要求是 <c>type: object</c>。</summary>
    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; set; }

    /// <summary>结构化输出的 JSON Schema（规范 2025-06-18 新增）。</summary>
    [JsonPropertyName("outputSchema")]
    public JsonElement? OutputSchema { get; set; }

    /// <summary>行为注解（只读、幂等、破坏性等）。</summary>
    public JsonElement? Annotations { get; set; }
}

/// <summary>
/// 服务端暴露的资源。
/// </summary>
public sealed class McpResource
{
    /// <summary>资源 URI。</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>资源名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可选的展示标题。</summary>
    public string? Title { get; set; }

    /// <summary>资源说明。</summary>
    public string? Description { get; set; }

    /// <summary>MIME 类型。</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>字节大小。</summary>
    public long? Size { get; set; }
}

/// <summary>
/// 资源模板，用 RFC 6570 URI Template 描述一类可参数化的资源。
/// </summary>
public sealed class McpResourceTemplate
{
    /// <summary>URI 模板，例如 <c>file:///{path}</c>。</summary>
    [JsonPropertyName("uriTemplate")]
    public string UriTemplate { get; set; } = string.Empty;

    /// <summary>模板名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可选的展示标题。</summary>
    public string? Title { get; set; }

    /// <summary>模板说明。</summary>
    public string? Description { get; set; }

    /// <summary>MIME 类型。</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }
}

/// <summary>
/// 提示词模板的参数声明。
/// </summary>
public sealed class McpPromptArgument
{
    /// <summary>参数名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>参数说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否必填。</summary>
    public bool Required { get; set; }
}

/// <summary>
/// 服务端暴露的提示词模板。
/// </summary>
public sealed class McpPrompt
{
    /// <summary>模板名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可选的展示标题。</summary>
    public string? Title { get; set; }

    /// <summary>模板说明。</summary>
    public string? Description { get; set; }

    /// <summary>参数列表。</summary>
    public List<McpPromptArgument> Arguments { get; set; } = new();
}

/// <summary>
/// 资源内容：文本资源与二进制资源共用一个类型。
/// </summary>
/// <remarks>
/// 规范用「<c>text</c> 与 <c>blob</c> 二选一」区分两种资源，这里保留同样的形态：
/// <see cref="Text"/> 非空即文本资源，否则 <see cref="Blob"/> 为 base64 编码的二进制内容。
/// </remarks>
public sealed class McpResourceContents
{
    /// <summary>资源 URI。</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>文本内容；二进制资源为 <c>null</c>。</summary>
    public string? Text { get; set; }

    /// <summary>base64 编码的二进制内容；文本资源为 <c>null</c>。</summary>
    public string? Blob { get; set; }
}

/// <summary>
/// <c>resources/read</c> 的响应结果。
/// </summary>
public sealed class McpReadResourceResult
{
    /// <summary>资源内容列表。</summary>
    public List<McpResourceContents> Contents { get; set; } = new();
}

/// <summary>
/// 一条模型消息（提示词渲染结果与采样请求共用）。
/// </summary>
public sealed class McpPromptMessage
{
    /// <summary>角色，<c>user</c> 或 <c>assistant</c>。</summary>
    public string Role { get; set; } = "user";

    /// <summary>消息内容块。</summary>
    public McpContent? Content { get; set; }
}

/// <summary>
/// <c>prompts/get</c> 的响应结果。
/// </summary>
public sealed class McpGetPromptResult
{
    /// <summary>模板说明。</summary>
    public string? Description { get; set; }

    /// <summary>渲染后的消息列表。</summary>
    public List<McpPromptMessage> Messages { get; set; } = new();
}

/// <summary>
/// 一个内容块。规范中的 text / image / audio / resource / resource_link 都由该类型承载。
/// </summary>
/// <remarks>
/// 选择「单类型 + <see cref="Type"/> 判别」而不是继承层次，是因为内容块本质上是线上报文，
/// 字段随 <c>type</c> 变化；用可空字段表达比引入多态序列化更直接，也更容易容忍未来新增的类型。
/// 未知类型不会导致解析失败，只会退化为 <c>[{type} content]</c> 的占位文本。
/// </remarks>
public sealed class McpContent
{
    /// <summary>内容类型：<c>text</c>、<c>image</c>、<c>audio</c>、<c>resource</c>、<c>resource_link</c>。</summary>
    public string Type { get; set; } = "text";

    /// <summary>文本内容（<c>text</c> 类型）。</summary>
    public string? Text { get; set; }

    /// <summary>base64 编码的二进制内容（<c>image</c> / <c>audio</c> 类型）。</summary>
    public string? Data { get; set; }

    /// <summary>MIME 类型。</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>资源 URI（<c>resource_link</c> 类型，或资源内容块的冗余字段）。</summary>
    public string? Uri { get; set; }

    /// <summary>资源名称（<c>resource_link</c> 类型）。</summary>
    public string? Name { get; set; }

    /// <summary>资源说明（<c>resource_link</c> 类型）。</summary>
    public string? Description { get; set; }

    /// <summary>资源大小（<c>resource_link</c> 类型）。</summary>
    public long? Size { get; set; }

    /// <summary>内嵌资源（<c>resource</c> 类型）。</summary>
    public McpResourceContents? Resource { get; set; }
}

/// <summary>
/// <c>tools/call</c> 的响应结果。
/// </summary>
public sealed class McpCallToolResult
{
    /// <summary>内容块列表。</summary>
    public List<McpContent> Content { get; set; } = new();

    /// <summary>可选的结构化结果，便于客户端按 schema 解析。</summary>
    [JsonPropertyName("structuredContent")]
    public JsonElement? StructuredContent { get; set; }

    /// <summary>服务端是否把本次调用判定为失败。</summary>
    /// <remarks>
    /// 注意这与 JSON-RPC 错误不同：协议层成功、业务层失败时 <see cref="IsError"/> 为 <c>true</c>，
    /// 内容块里通常带有给模型看的错误说明，属于「模型可自行修正」的情况。
    /// </remarks>
    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

/// <summary>
/// MCP 日志级别，取值与 RFC 5424 的 syslog 严重级别一致。
/// </summary>
public enum McpLogLevel
{
    /// <summary>调试信息。</summary>
    Debug,

    /// <summary>常规信息。</summary>
    Info,

    /// <summary>正常但重要的事件。</summary>
    Notice,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,

    /// <summary>严重错误。</summary>
    Critical,

    /// <summary>需要立即处理。</summary>
    Alert,

    /// <summary>系统不可用。</summary>
    Emergency
}

/// <summary>
/// <see cref="McpLogLevel"/> 与协议字符串的转换。
/// </summary>
public static class McpLogLevels
{
    /// <summary>转换为协议中使用的小写字符串。</summary>
    /// <param name="level">日志级别。</param>
    public static string ToProtocolString(McpLogLevel level) => level switch
    {
        McpLogLevel.Debug => "debug",
        McpLogLevel.Info => "info",
        McpLogLevel.Notice => "notice",
        McpLogLevel.Warning => "warning",
        McpLogLevel.Error => "error",
        McpLogLevel.Critical => "critical",
        McpLogLevel.Alert => "alert",
        McpLogLevel.Emergency => "emergency",
        _ => "info"
    };

    /// <summary>从协议字符串解析日志级别；无法识别时返回 <see cref="McpLogLevel.Info"/>。</summary>
    /// <param name="value">协议字符串。</param>
    public static McpLogLevel Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "debug" => McpLogLevel.Debug,
        "notice" => McpLogLevel.Notice,
        "warning" => McpLogLevel.Warning,
        "error" => McpLogLevel.Error,
        "critical" => McpLogLevel.Critical,
        "alert" => McpLogLevel.Alert,
        "emergency" => McpLogLevel.Emergency,
        _ => McpLogLevel.Info
    };
}
