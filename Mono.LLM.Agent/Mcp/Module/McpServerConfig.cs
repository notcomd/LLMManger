// ============================================================================
// 文件：McpServerConfig.cs
// 用途：单个 MCP 服务器的连接配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClientManager、DI 装配、AgentOptions.Mcp
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 单个 MCP 服务器的连接配置。
/// </summary>
/// <remarks>
/// <para>
/// 同一个类型覆盖两种传输：stdio 只需 <see cref="Command"/> 与 <see cref="Arguments"/>，
/// Streamable HTTP 只需 <see cref="Endpoint"/> 与可选的 <see cref="Headers"/>。
/// 用 <see cref="Transport"/> 显式选择，而不是靠「哪个字段有值」来推断——配置错误应当在启动时立刻报出来。
/// </para>
/// <para>
/// 密钥等敏感信息建议通过 <see cref="EnvironmentVariables"/>（stdio）或
/// <see cref="Headers"/>（HTTP）注入，不要写死在代码里。
/// </para>
/// </remarks>
public sealed class McpServerConfig
{
    /// <summary>服务器名称，作为默认的工具名前缀与日志标签，同一进程内应唯一。</summary>
    public required string Name { get; init; }

    /// <summary>传输类型，默认 stdio。</summary>
    public McpTransportKind Transport { get; init; } = McpTransportKind.Stdio;

    /// <summary>是否启用；为 <c>false</c> 时管理器会跳过该服务器。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>stdio 传输的启动命令，可以是绝对路径、相对路径或 PATH 中的命令名。</summary>
    public string? Command { get; init; }

    /// <summary>stdio 传输的命令行参数。</summary>
    public IList<string> Arguments { get; init; } = new List<string>();

    /// <summary>stdio 传输的附加环境变量。</summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>stdio 子进程的工作目录；为 <c>null</c> 时继承当前进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Streamable HTTP 传输的端点地址。</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Streamable HTTP 传输的附加请求头，常用于鉴权。</summary>
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// 传输层使用的 <see cref="HttpClient"/>（代理、自定义证书、测试桩）；为 <c>null</c> 时由传输层创建并负责释放。
    /// </summary>
    /// <remarks>
    /// OAuth 的发现与令牌请求默认使用 <see cref="McpOAuthOptions.HttpClient"/>；
    /// 若那里没有单独指定，可直接复用同一个实例。
    /// </remarks>
    public HttpClient? HttpClient { get; init; }

    /// <summary>是否建立服务端消息流（GET + SSE），默认 <c>false</c>。</summary>
    public bool EnableServerStream { get; init; }

    /// <summary>注册到工具管理器时的名称前缀；为 <c>null</c> 时使用 <see cref="Name"/>。</summary>
    public string? ToolNamePrefix { get; init; }

    /// <summary>工具白名单；为 <c>null</c> 或空表示全部注册。</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>是否在收到 <c>tools/list_changed</c> 后自动刷新工具，默认 <c>true</c>。</summary>
    public bool AutoRefreshTools { get; init; } = true;

    /// <summary>普通请求超时；仅在 <see cref="ClientOptions"/> 为 <c>null</c> 时生效。</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>工具调用超时；仅在 <see cref="ClientOptions"/> 为 <c>null</c> 时生效。</summary>
    public TimeSpan? ToolCallTimeout { get; init; }

    /// <summary>
    /// 完整覆盖客户端行为配置（含采样、根目录、信息征询等回调）。
    /// </summary>
    /// <remarks>
    /// 提供该对象后 <see cref="RequestTimeout"/> 与 <see cref="ToolCallTimeout"/> 会被忽略，
    /// 以避免「两个地方都能配置超时」带来的歧义。
    /// </remarks>
    public McpClientOptions? ClientOptions { get; init; }

    /// <summary>
    /// OAuth 2.1 授权配置；为 <c>null</c> 时不启用 OAuth（可继续用 <see cref="Headers"/> 携带静态令牌）。
    /// </summary>
    /// <remarks>
    /// 仅对 <see cref="McpTransportKind.StreamableHttp"/> 有效。启用后，客户端会在首次请求时
    /// 自动发现授权服务器、完成动态注册与 PKCE 授权，并把令牌缓存到本地；
    /// 后续请求自动带上 <c>Authorization: Bearer</c>，收到 401 时刷新一次令牌并重试。
    /// </remarks>
    public McpOAuthOptions? OAuth { get; init; }

    /// <summary>按配置创建传输实例。</summary>
    /// <param name="loggerFactory">可选的日志工厂。</param>
    /// <param name="tokenProvider">可选的访问令牌提供者，仅 HTTP 传输使用。</param>
    internal IMcpTransport CreateTransport(ILoggerFactory? loggerFactory, IMcpTokenProvider? tokenProvider = null)
    {
        if (OAuth is not null && Transport != McpTransportKind.StreamableHttp)
        {
            throw new InvalidOperationException(
                $"MCP 服务器「{Name}」配置了 OAuth，但 OAuth 只适用于 Streamable HTTP 传输。");
        }

        switch (Transport)
        {
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(Command))
                {
                    throw new InvalidOperationException(
                        $"MCP 服务器「{Name}」使用 stdio 传输，必须配置 {nameof(Command)}。");
                }

                return new StdioMcpTransport(
                    new McpStdioTransportOptions
                    {
                        Command = Command!,
                        Arguments = Arguments,
                        EnvironmentVariables = EnvironmentVariables,
                        WorkingDirectory = WorkingDirectory,
                        Name = Name
                    },
                    loggerFactory?.CreateLogger<StdioMcpTransport>());

            case McpTransportKind.StreamableHttp:
                if (Endpoint is null)
                {
                    throw new InvalidOperationException(
                        $"MCP 服务器「{Name}」使用 Streamable HTTP 传输，必须配置 {nameof(Endpoint)}。");
                }

                return new StreamableHttpMcpTransport(
                    new StreamableHttpTransportOptions
                    {
                        Endpoint = Endpoint,
                        Headers = Headers,
                        Name = Name,
                        EnableServerStream = EnableServerStream,
                        TokenProvider = tokenProvider,
                        HttpClient = HttpClient
                    },
                    loggerFactory?.CreateLogger<StreamableHttpMcpTransport>());

            default:
                throw new InvalidOperationException($"MCP 服务器「{Name}」使用了未知的传输类型 {Transport}。");
        }
    }

    /// <summary>按配置构造客户端选项。</summary>
    internal McpClientOptions BuildClientOptions()
    {
        if (ClientOptions is { } configured)
            return configured;

        return new McpClientOptions
        {
            RequestTimeout = RequestTimeout ?? TimeSpan.FromSeconds(60),
            ToolCallTimeout = ToolCallTimeout ?? TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>按配置构造桥接选项。</summary>
    internal McpToolBridgeOptions BuildBridgeOptions()
    {
        var allowed = AllowedTools;

        return new McpToolBridgeOptions
        {
            ToolNamePrefix = string.IsNullOrWhiteSpace(ToolNamePrefix) ? Name : ToolNamePrefix,
            Filter = allowed is { Count: > 0 }
                ? tool => allowed.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)
                : null
        };
    }
}
