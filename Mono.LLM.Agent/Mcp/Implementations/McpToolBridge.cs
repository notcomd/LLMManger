// ============================================================================
// 文件：McpToolBridge.cs
// 用途：把 MCP 服务端的工具注册进 ToolManager，使它们与本地工具对模型完全等价。
// 实现方：McpToolBridge
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 工具桥接的配置。
/// </summary>
public sealed class McpToolBridgeOptions
{
    /// <summary>
    /// 注册到 <see cref="ToolManager"/> 时的名称前缀，通常取服务器名。
    /// </summary>
    /// <remarks>
    /// 多个服务器都提供 <c>search</c> 这类通用名时，没有前缀会互相覆盖；
    /// 加上前缀后模型也能从名字看出工具归属。
    /// </remarks>
    public string? ToolNamePrefix { get; set; }

    /// <summary>前缀与工具名之间的分隔符，默认 <c>__</c>。</summary>
    public string Separator { get; set; } = "__";

    /// <summary>工具过滤器；返回 <c>false</c> 的工具不会被注册。</summary>
    /// <remarks>
    /// 工具越多，模型选错的概率越高、Schema 占用的 token 也越多，
    /// 因此按业务需要裁剪是常规做法。
    /// </remarks>
    public Func<McpTool, bool>? Filter { get; set; }

    /// <summary>
    /// 是否把非法字符替换为下划线，默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 多数模型 API 要求工具名匹配 <c>^[a-zA-Z0-9_-]{1,64}$</c>，
    /// 而 MCP 工具名允许更宽的字符集（例如 <c>fs.read</c>），需要做一次收敛。
    /// </remarks>
    public bool SanitizeToolNames { get; set; } = true;

    /// <summary>是否在描述末尾标注来源服务器，默认 <c>true</c>。</summary>
    public bool IncludeServerNameInDescription { get; set; } = true;
}

/// <summary>
/// 把 MCP 服务端的工具注册进 <see cref="ToolManager"/>，使它们与本地工具对模型完全等价。
/// </summary>
/// <remarks>
/// <para>
/// 桥接做三件事：命名（加前缀、清洗字符）、描述（补充来源）、调用（转发到 <c>tools/call</c>
/// 并把内容块渲染成文本）。参数 Schema 原样透传，不做有损转换。
/// </para>
/// <para>
/// 服务端宣告 <c>notifications/tools/list_changed</c> 后应调用 <see cref="RefreshAsync"/>：
/// 它会重新拉取列表，下线已消失的工具，并覆盖更新仍在的工具。
/// </para>
/// </remarks>
public sealed class McpToolBridge
{
    private static readonly Regex InvalidNameChars = new("[^A-Za-z0-9_-]", RegexOptions.Compiled);

    private readonly ToolManager _toolManager;
    private readonly McpClient _client;
    private readonly McpToolBridgeOptions _options;
    private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>初始化桥接。</summary>
    /// <param name="toolManager">目标工具管理器。</param>
    /// <param name="client">已完成握手的 MCP 客户端。</param>
    /// <param name="options">命名与过滤配置；为 <c>null</c> 时使用默认值。</param>
    public McpToolBridge(ToolManager toolManager, McpClient client, McpToolBridgeOptions? options = null)
    {
        _toolManager = toolManager ?? throw new ArgumentNullException(nameof(toolManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new McpToolBridgeOptions();
    }

    /// <summary>来源服务器名称。</summary>
    public string ServerName => _client.Name;

    /// <summary>当前已注册到工具管理器的名称（暴露名，而非服务端原始名）。</summary>
    public IReadOnlyList<string> RegisteredToolNames
    {
        get
        {
            lock (_lock)
                return _registered.Keys.ToList();
        }
    }

    /// <summary>
    /// 拉取服务端工具并全部注册，已存在的同名工具会被覆盖。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次注册的暴露名列表。</returns>
    /// <exception cref="InvalidOperationException">两个不同的工具映射到了同一个暴露名。</exception>
    public async Task<IReadOnlyList<string>> RegisterToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var registered = new List<string>(tools.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
                continue;

            if (_options.Filter is { } filter && !filter(tool))
                continue;

            var exposedName = ResolveToolName(tool);

            if (!seen.Add(exposedName))
            {
                throw new InvalidOperationException(
                    $"MCP 服务器「{ServerName}」的两个工具映射到了同一个名称「{exposedName}」，" +
                    "请调整 ToolNamePrefix 或 Separator。");
            }

            Register(tool, exposedName);
            registered.Add(exposedName);
        }

        lock (_lock)
        {
            // 记录暴露名到原始名的映射，刷新时据此判断哪些工具已经下线。
            var stale = _registered.Keys
                .Where(name => !seen.Contains(name))
                .ToList();

            foreach (var name in stale)
            {
                _toolManager.UnregisterTool(name);
                _registered.Remove(name);
            }
        }

        return registered;
    }

    /// <summary>
    /// 重新同步工具列表：新增的注册、仍存在的覆盖更新、已消失的注销。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>刷新后的暴露名列表。</returns>
    public Task<IReadOnlyList<string>> RefreshAsync(CancellationToken cancellationToken = default) =>
        RegisterToolsAsync(cancellationToken);

    /// <summary>注销本桥接注册的全部工具。</summary>
    /// <returns>实际移除的数量。</returns>
    public int RemoveAll()
    {
        List<string> names;
        lock (_lock)
        {
            names = _registered.Keys.ToList();
            _registered.Clear();
        }

        var removed = 0;
        foreach (var name in names)
        {
            if (_toolManager.UnregisterTool(name))
                removed++;
        }

        return removed;
    }

    private void Register(McpTool tool, string exposedName)
    {
        var description = BuildDescription(tool);
        var schema = BuildSchema(tool);

        _toolManager.RegisterRawTool(
            exposedName,
            description,
            schema,
            async (arguments, cancellationToken) =>
            {
                var result = await _client.CallToolAsync(tool.Name, arguments, cancellationToken).ConfigureAwait(false);
                return McpContentFormatter.ToToolResult(exposedName, result);
            });

        lock (_lock)
            _registered[exposedName] = tool.Name;
    }

    private string ResolveToolName(McpTool tool)
    {
        var name = tool.Name;

        if (!string.IsNullOrWhiteSpace(_options.ToolNamePrefix))
            name = _options.ToolNamePrefix + _options.Separator + name;

        return _options.SanitizeToolNames ? InvalidNameChars.Replace(name, "_") : name;
    }

    private string BuildDescription(McpTool tool)
    {
        var description = tool.Description;

        if (string.IsNullOrWhiteSpace(description))
            description = $"MCP 工具「{tool.Name}」。";

        if (_options.IncludeServerNameInDescription)
            description = $"{description}\n\n(MCP 服务器：{_client.Name})";

        return description;
    }

    private static JsonElement BuildSchema(McpTool tool)
    {
        if (tool.InputSchema is { ValueKind: JsonValueKind.Object } schema)
            return schema;

        // 规范要求 inputSchema 必须存在且为对象；对不合规的服务端给一个空对象 Schema 兜底，
        // 让工具至少可被发现，而不是在注册阶段直接失败。
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>()
        });
    }
}
