// ============================================================================
// 文件：McpProtocol.cs
// 用途：MCP（Model Context Protocol）协议常量：版本号、方法名与错误码。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient、McpOAuthClient、McpClientManager
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP（Model Context Protocol）协议常量：版本号、方法名与错误码。
/// </summary>
/// <remarks>
/// <para>
/// 本实现遵循 MCP 规范的 JSON-RPC 2.0 传输格式，默认协商
/// <see cref="LatestVersion"/>（2025-06-18），并向后兼容 2025-03-26 与 2024-11-05 三个版本的
/// 客户端行为差异（旧版本无 <c>MCP-Protocol-Version</c> 请求头、允许批量消息）。
/// </para>
/// <para>
/// 规范文档：<see href="https://modelcontextprotocol.io/specification"/>
/// </para>
/// </remarks>
public static class McpProtocol
{
    /// <summary>JSON-RPC 版本号，固定为 <c>"2.0"</c>。</summary>
    public const string JsonRpcVersion = "2.0";

    /// <summary>本实现默认（也是最高）支持的 MCP 协议版本。</summary>
    public const string LatestVersion = "2025-06-18";

    /// <summary>2025-03-26 修订版。</summary>
    public const string Version2025_03_26 = "2025-03-26";

    /// <summary>2024-11-05 修订版，首个正式发布版本。</summary>
    public const string Version2024_11_05 = "2024-11-05";

    /// <summary>按优先级排列的受支持协议版本。</summary>
    public static IReadOnlyList<string> SupportedVersions { get; } =
        new[] { LatestVersion, Version2025_03_26, Version2024_11_05 };

    /// <summary>判断给定版本号是否被本客户端支持。</summary>
    /// <param name="version">服务端返回或配置的版本号。</param>
    /// <returns>受支持返回 <c>true</c>。</returns>
    public static bool IsSupported(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        SupportedVersions.Contains(version, StringComparer.Ordinal);
}

/// <summary>
/// MCP 规范定义的 JSON-RPC 方法名。
/// </summary>
/// <remarks>
/// 以 <c>notifications/</c> 开头的常量是不需要响应的通知；其余为请求方法，
/// 服务端必须返回带相同 <c>id</c> 的响应。
/// </remarks>
public static class McpMethods
{
    /// <summary>初始化握手，客户端发起。</summary>
    public const string Initialize = "initialize";

    /// <summary>初始化完成通知，客户端在收到 initialize 响应后发送。</summary>
    public const string InitializedNotification = "notifications/initialized";

    /// <summary>健康检查。</summary>
    public const string Ping = "ping";

    /// <summary>列出服务端提供的工具。</summary>
    public const string ToolsList = "tools/list";

    /// <summary>调用服务端的某个工具。</summary>
    public const string ToolsCall = "tools/call";

    /// <summary>工具列表发生变化（服务端通知）。</summary>
    public const string ToolsListChangedNotification = "notifications/tools/list_changed";

    /// <summary>列出资源。</summary>
    public const string ResourcesList = "resources/list";

    /// <summary>列出资源模板（RFC 6570 URI Template）。</summary>
    public const string ResourcesTemplatesList = "resources/templates/list";

    /// <summary>读取资源内容。</summary>
    public const string ResourcesRead = "resources/read";

    /// <summary>订阅某个资源的变化。</summary>
    public const string ResourcesSubscribe = "resources/subscribe";

    /// <summary>取消资源订阅。</summary>
    public const string ResourcesUnsubscribe = "resources/unsubscribe";

    /// <summary>资源列表发生变化（服务端通知）。</summary>
    public const string ResourcesListChangedNotification = "notifications/resources/list_changed";

    /// <summary>某个已订阅资源的内容发生变化（服务端通知）。</summary>
    public const string ResourcesUpdatedNotification = "notifications/resources/updated";

    /// <summary>列出提示词模板。</summary>
    public const string PromptsList = "prompts/list";

    /// <summary>获取指定提示词模板的渲染结果。</summary>
    public const string PromptsGet = "prompts/get";

    /// <summary>提示词列表发生变化（服务端通知）。</summary>
    public const string PromptsListChangedNotification = "notifications/prompts/list_changed";

    /// <summary>设置服务端日志级别。</summary>
    public const string LoggingSetLevel = "logging/setLevel";

    /// <summary>服务端日志消息通知。</summary>
    public const string LoggingMessageNotification = "notifications/message";

    /// <summary>长任务进度通知。</summary>
    public const string ProgressNotification = "notifications/progress";

    /// <summary>请求取消通知。</summary>
    public const string CancelledNotification = "notifications/cancelled";

    /// <summary>参数自动补全（服务端能力 <c>completions</c>）。</summary>
    public const string CompletionComplete = "completion/complete";

    /// <summary>服务端向客户端请求补全（采样）。</summary>
    public const string SamplingCreateMessage = "sampling/createMessage";

    /// <summary>服务端向客户端请求根目录列表。</summary>
    public const string RootsList = "roots/list";

    /// <summary>根目录列表变化通知。</summary>
    public const string RootsListChangedNotification = "notifications/roots/list_changed";

    /// <summary>服务端向客户端发起信息征询。</summary>
    public const string ElicitationCreate = "elicitation/create";
}

/// <summary>
/// JSON-RPC 2.0 标准错误码与 MCP 专用的服务端错误码。
/// </summary>
/// <remarks>
/// 服务端自定义错误应落在 <c>-32000 ~ -32099</c> 区间；本类型中的
/// <see cref="ConnectionClosed"/>、<see cref="RequestTimeout"/>、<see cref="ResourceNotFound"/>
/// 与官方 SDK 的取值保持一致，便于跨实现识别。
/// </remarks>
public static class McpErrorCodes
{
    /// <summary>收到无法解析的 JSON。</summary>
    public const int ParseError = -32700;

    /// <summary>JSON 合法但不是有效的请求对象。</summary>
    public const int InvalidRequest = -32600;

    /// <summary>方法不存在或未公开。</summary>
    public const int MethodNotFound = -32601;

    /// <summary>方法参数无效。</summary>
    public const int InvalidParams = -32602;

    /// <summary>服务端内部错误。</summary>
    public const int InternalError = -32603;

    /// <summary>连接已关闭，请求无法完成（官方 SDK 取值）。</summary>
    public const int ConnectionClosed = -32000;

    /// <summary>请求超时（官方 SDK 取值）。</summary>
    public const int RequestTimeout = -32001;

    /// <summary>请求的资源不存在（MCP 规范取值）。</summary>
    public const int ResourceNotFound = -32002;

    /// <summary>需要 OAuth 授权，或现有凭据已失效（本实现自定义，与官方 SDK 时段不冲突）。</summary>
    public const int AuthorizationRequired = -32003;
}
