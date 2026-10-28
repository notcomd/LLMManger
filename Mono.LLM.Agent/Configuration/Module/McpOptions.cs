// ============================================================================
// 文件：McpOptions.cs
// 用途：MCP 客户端行为与服务器列表的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json.Serialization;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 单个 MCP 服务器的声明式配置。
/// </summary>
/// <remarks>
/// 覆盖了 <see cref="McpServerConfig"/> 中能写进配置文件的全部字段。
/// 只能由代码提供的钩子（<c>HttpClient</c>、令牌存储、OAuth 交互实现、采样/根目录回调）
/// 不在模型里，需要时请直接构造 <see cref="McpServerConfig"/> 并调用 <c>AddMcpServers</c>。
/// </remarks>
public sealed class McpServerOptions
{
    /// <summary>获取或设置服务器名称，也是默认的工具名前缀。必填且应唯一。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置传输类型，默认 stdio。</summary>
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

    /// <summary>获取或设置是否启用；关闭后不会连接。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>获取或设置 stdio 传输的启动命令。</summary>
    public string? Command { get; set; }

    /// <summary>获取或设置 stdio 传输的命令行参数。</summary>
    public List<string> Arguments { get; set; } = new();

    /// <summary>获取或设置 stdio 子进程的附加环境变量。</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>获取或设置 stdio 子进程的工作目录。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>获取或设置 Streamable HTTP 的端点地址。</summary>
    public string? Endpoint { get; set; }

    /// <summary>获取或设置 Streamable HTTP 的附加请求头，常用于静态令牌鉴权。</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>获取或设置是否建立服务端消息流（GET + SSE）。</summary>
    public bool EnableServerStream { get; set; }

    /// <summary>获取或设置工具名前缀；为 <c>null</c> 时使用 <see cref="Name"/>。</summary>
    public string? ToolNamePrefix { get; set; }

    /// <summary>获取或设置工具白名单；为 <c>null</c> 或空表示全部注册。</summary>
    public List<string>? AllowedTools { get; set; }

    /// <summary>获取或设置是否在收到工具列表变更通知后自动刷新，默认 <c>true</c>。</summary>
    public bool AutoRefreshTools { get; set; } = true;

    /// <summary>获取或设置普通请求超时；为 <c>null</c> 时用客户端默认值 60 秒。</summary>
    [JsonConverter(typeof(FlexibleNullableTimeSpanConverter))]
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>获取或设置工具调用超时；为 <c>null</c> 时用客户端默认值 5 分钟。</summary>
    [JsonConverter(typeof(FlexibleNullableTimeSpanConverter))]
    public TimeSpan? ToolCallTimeout { get; set; }

    /// <summary>获取或设置 OAuth 2.1 授权配置；为 <c>null</c> 时不启用。</summary>
    public McpOAuthSettings? OAuth { get; set; }

    /// <summary>
    /// 转换为运行时的 <see cref="McpServerConfig"/>。
    /// </summary>
    /// <returns>可直接传给 <c>AddMcpServers</c> 的服务器配置。</returns>
    /// <exception cref="InvalidOperationException">端点或授权服务器地址不是合法 URI 时抛出。</exception>
    public McpServerConfig ToServerConfig() => new()
    {
        Name = Name,
        Transport = Transport,
        Enabled = Enabled,
        Command = Command,
        Arguments = Arguments,
        EnvironmentVariables = EnvironmentVariables,
        WorkingDirectory = WorkingDirectory,
        Endpoint = ParseUri(Endpoint, "endpoint"),
        Headers = Headers,
        EnableServerStream = EnableServerStream,
        ToolNamePrefix = ToolNamePrefix,
        AllowedTools = AllowedTools,
        AutoRefreshTools = AutoRefreshTools,
        RequestTimeout = RequestTimeout,
        ToolCallTimeout = ToolCallTimeout,
        OAuth = OAuth?.ToOAuthOptions()
    };

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (string.IsNullOrWhiteSpace(Name))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.name", "服务器名称不能为空。"));

        if (!Enum.IsDefined(Transport))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.transport", $"未知的传输类型值 {(int)Transport}。"));

        if (Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(Command))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.command", "stdio 传输必须配置 command。"));

        if (Transport == McpTransportKind.StreamableHttp)
        {
            if (string.IsNullOrWhiteSpace(Endpoint))
                issues.Add(AgentOptionsIssue.Error($"{prefix}.endpoint", "Streamable HTTP 传输必须配置 endpoint。"));
            else if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
                issues.Add(AgentOptionsIssue.Error($"{prefix}.endpoint", $"'{Endpoint}' 不是合法的绝对 URI。"));
        }

        if (OAuth is not null && Transport != McpTransportKind.StreamableHttp)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.oauth", "OAuth 只适用于 Streamable HTTP 传输。"));

        if (OAuth is not null)
            issues.AddRange(OAuth.Validate($"{prefix}.oauth"));

        if (AllowedTools is { Count: 0 })
            issues.Add(AgentOptionsIssue.Warning($"{prefix}.allowedTools", "空白名单与「不限制」含义相同，建议删掉该字段。"));

        return issues;
    }

    private static Uri? ParseUri(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"字段 {field} 不是合法的绝对 URI：{value}");
    }
}

/// <summary>
/// MCP 的 OAuth 2.1 授权配置（声明式部分）。
/// </summary>
public sealed class McpOAuthSettings
{
    /// <summary>获取或设置预注册的客户端标识；为空时按需动态注册。</summary>
    public string? ClientId { get; set; }

    /// <summary>获取或设置预注册的客户端密钥；公开客户端留空。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>获取或设置请求的授权范围。</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>获取或设置授权服务器地址；填写后跳过资源元数据探测。</summary>
    public string? AuthorizationServer { get; set; }

    /// <summary>获取或设置回调地址；为空时自动选择回环端口。</summary>
    public string? RedirectUri { get; set; }

    /// <summary>获取或设置是否自动打开浏览器，默认 <c>true</c>。</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>获取或设置等待用户授权的时长，默认 5 分钟。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan AuthorizationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>获取或设置令牌过期的提前判定量，默认 60 秒。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>获取或设置是否允许动态客户端注册，默认 <c>true</c>。</summary>
    public bool UseDynamicClientRegistration { get; set; } = true;

    /// <summary>获取或设置注册时展示的客户端名称。</summary>
    public string ClientName { get; set; } = "Mono.LLM.Agent";

    /// <summary>
    /// 转换为运行时的 <see cref="McpOAuthOptions"/>。
    /// </summary>
    /// <returns>不含令牌存储与交互实现的授权配置。</returns>
    /// <exception cref="InvalidOperationException">地址字段不是合法 URI 时抛出。</exception>
    public McpOAuthOptions ToOAuthOptions() => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Scopes = Scopes,
        AuthorizationServer = TryParse(AuthorizationServer),
        RedirectUri = TryParse(RedirectUri),
        OpenBrowser = OpenBrowser,
        AuthorizationTimeout = AuthorizationTimeout,
        ClockSkew = ClockSkew,
        UseDynamicClientRegistration = UseDynamicClientRegistration,
        ClientName = ClientName
    };

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (!string.IsNullOrWhiteSpace(AuthorizationServer)
            && !Uri.TryCreate(AuthorizationServer, UriKind.Absolute, out _))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.authorizationServer", $"'{AuthorizationServer}' 不是合法的绝对 URI。"));
        }

        if (!string.IsNullOrWhiteSpace(RedirectUri)
            && !Uri.TryCreate(RedirectUri, UriKind.Absolute, out _))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.redirectUri", $"'{RedirectUri}' 不是合法的绝对 URI。"));
        }

        if (AuthorizationTimeout <= TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.authorizationTimeout", "授权等待时长必须为正数。"));

        if (ClockSkew < TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.clockSkew", "时钟偏移量不能为负。"));

        return issues;
    }

    private static Uri? TryParse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
}

/// <summary>
/// MCP 客户端行为与服务器列表的配置。
/// </summary>
public sealed class McpOptions
{
    /// <summary>获取或设置是否启用 MCP 子系统，默认 <c>true</c>。关闭后不连接任何服务器。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>获取或设置客户端级参数（协议版本、请求超时、分页上限等）。</summary>
    public McpClientSettings Client { get; set; } = new();

    /// <summary>获取或设置服务器列表。</summary>
    public List<McpServerOptions> Servers { get; set; } = new();

    /// <summary>
    /// 转换为运行时的服务器配置集合。
    /// </summary>
    /// <returns>已启用服务器的配置；整体关闭或无服务器时返回空集合。</returns>
    public IReadOnlyList<McpServerConfig> ToServerConfigs() =>
        !Enabled
            ? Array.Empty<McpServerConfig>()
            : Servers.Where(server => server.Enabled).Select(server => server.ToServerConfig()).ToList();

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("mcp");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (!Enabled)
            return issues;

        issues.AddRange(Client.Validate($"{prefix}.client"));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Servers.Count; i++)
        {
            var server = Servers[i];
            issues.AddRange(server.Validate($"{prefix}.servers[{i}]"));

            if (!string.IsNullOrWhiteSpace(server.Name) && !names.Add(server.Name))
                issues.Add(AgentOptionsIssue.Error($"{prefix}.servers[{i}].name", $"服务器名称 '{server.Name}' 重复。"));

            if (!server.Enabled)
            {
                issues.Add(AgentOptionsIssue.Warning(
                    $"{prefix}.servers[{i}]",
                    "服务器已禁用，配置校验仍会执行，但不会连接。"));
            }
        }

        return issues;
    }
}

/// <summary>
/// MCP 客户端级参数。
/// </summary>
public sealed class McpClientSettings
{
    /// <summary>获取或设置客户端名称，写入 <c>clientInfo.name</c>。</summary>
    public string ClientName { get; set; } = "Mono.LLM.Agent";

    /// <summary>获取或设置客户端版本；为空时取程序集版本。</summary>
    public string? ClientVersion { get; set; }

    /// <summary>获取或设置期望的协议版本；为空时使用库支持的最新版本。</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>获取或设置普通请求超时，默认 60 秒。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>获取或设置工具调用超时，默认 5 分钟。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>获取或设置列表分页的最大页数，默认 100。</summary>
    public int MaxPaginationPages { get; set; } = 100;

    /// <summary>
    /// 转换为运行时的 <see cref="McpClientOptions"/>。
    /// </summary>
    /// <returns>不含服务端回调的客户端选项。</returns>
    public McpClientOptions ToClientOptions() => new()
    {
        ClientName = ClientName,
        ClientVersion = ClientVersion,
        ProtocolVersion = ProtocolVersion,
        RequestTimeout = RequestTimeout,
        ToolCallTimeout = ToolCallTimeout,
        MaxPaginationPages = MaxPaginationPages
    };

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (string.IsNullOrWhiteSpace(ClientName))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.clientName", "客户端名称不能为空。"));

        if (RequestTimeout <= TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.requestTimeout", "请求超时必须为正数。"));

        if (ToolCallTimeout <= TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.toolCallTimeout", "工具调用超时必须为正数。"));

        if (MaxPaginationPages < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxPaginationPages", "分页上限至少为 1。"));

        return issues;
    }
}
