using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LLMManager.Tools;

/// <summary>
/// 管理工具函数的注册、查询和执行，是 LLM 工具调用（Function Calling）机制的核心组件。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolManager"/> 提供完整的工具生命周期管理能力：
/// </para>
/// <list type="number">
///   <item><b>注册</b> — 通过 <see cref="RegisterTool"/> 或 <see cref="RegisterSimpleTool"/> 将自定义逻辑注册为可被 AI 调用的工具；</item>
///   <item><b>转换</b> — 通过 <see cref="GetAITools"/> 将已注册工具转换为 Microsoft.Extensions.AI 框架的 <see cref="AITool"/> 列表；</item>
///   <item><b>执行</b> — 通过 <see cref="ExecuteToolAsync"/> 按名称调用已注册的工具并返回统一的 <see cref="ToolResult"/>。</item>
/// </list>
/// <para>
/// 线程安全：所有公开方法均使用内置锁保护并发访问。
/// </para>
/// </remarks>
public class ToolManager
{
    /// <summary>内部结构：将工具元数据与执行处理器绑定在一起，避免双集合一致性问题（修复 Issue #3）。</summary>
    private sealed class ToolEntry
    {
        public required ToolRegistration Registration { get; init; }
        public required Func<JsonElement, CancellationToken, Task<ToolResult>> Handler { get; init; }
    }

    private readonly List<ToolEntry> _entries = new();
    private readonly object _lock = new(); // 修复 Issue #4：线程安全锁

    /// <summary>获取当前已注册的所有工具的只读列表。</summary>
    public IReadOnlyList<ToolRegistration> Tools
    {
        get
        {
            lock (_lock)
                return _entries.Select(e => e.Registration).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 注册一个具有完整参数定义的自定义工具。
    /// </summary>
    /// <param name="toolName">工具的唯一名称标识符。</param>
    /// <param name="description">工具功能的自然语言描述。</param>
    /// <param name="parameters">工具参数的定义列表。</param>
    /// <param name="handler">工具的实际执行逻辑委托。</param>
    public void RegisterTool(
        string toolName,
        string description,
        IEnumerable<ToolParameterInfo> parameters,
        Func<JsonElement, CancellationToken, Task<ToolResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            // 移除同名旧条目（防止重复注册）
            _entries.RemoveAll(e => e.Registration.Name == toolName);

            _entries.Add(new ToolEntry
            {
                Registration = new ToolRegistration
                {
                    Name = toolName,
                    Description = description,
                    Parameters = parameters.ToList()
                },
                Handler = handler
            });
        }
    }

    /// <summary>
    /// 注册一个仅接收单个字符串输入的简化工具（便捷方法）。
    /// </summary>
    /// <param name="toolName">工具的唯一名称标识符。</param>
    /// <param name="description">工具功能的自然语言描述。</param>
    /// <param name="handler">简化的执行逻辑委托，接收字符串输入返回字符串结果。</param>
    
    public void RegisterSimpleTool(
        string toolName,
        string description,
        Func<string, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Registration.Name == toolName);

            _entries.Add(new ToolEntry
            {
                Registration = new ToolRegistration
                {
                    Name = toolName,
                    Description = description,
                    Parameters = new List<ToolParameterInfo>
                    {
                        new() { Name = "input", Type = "string", Description = description, Required = true }
                    }
                },
                Handler = async (json, ct) =>
                {
                    var input = ExtractInputFromJson(json);
                    var result = await handler(input).ConfigureAwait(false);
                    return new ToolResult { Name = toolName, Result = result };
                }
            });
        }
    }

    /// <summary>
    /// 将所有已注册的工具转换为 Microsoft.Extensions.AI 框架可识别的 <see cref="AITool"/> 列表。
    /// </summary>
    /// <returns>可直接赋值给 <see cref="ChatOptions.Tools"/> 的 <see cref="AITool"/> 集合。</returns>
    /// <remarks>
    /// <para>修复说明：</para>
    /// <list type="bullet">
    ///   <item><b>Bug1 修复</b>: 使用异步委托替代同步 .Result 调用，消除死锁风险。</item>
    ///   <item><b>Bug2 修复</b>: 参数 Schema 信息追加到描述文本中，确保 AI 模型能获取参数结构。</item>
    /// </list>
    /// </remarks>
    public List<AITool> GetAITools()
    {
        lock (_lock)
        {
            var aiFunctions = new List<AITool>(_entries.Count);

            foreach (var entry in _entries)
            {
                var tool = entry.Registration;
                var handler = entry.Handler;

                // 构建增强描述：原始描述 + 参数 Schema
                var enhancedDescription = BuildEnhancedDescription(tool);

                // Bug1 修复: 使用 async 委托，不再使用 .Result 同步阻塞
                var aiFunc = AIFunctionFactory.Create(
                    async (string input) =>
                    {
                        var jsonArgs = JsonDocument.Parse($"{{\"input\":\"{EscapeJsonString(input)}\"}}").RootElement;
                        var result = await handler(jsonArgs, default).ConfigureAwait(false);
                        return result.IsSuccess ? result.Result : $"Error: {result.Error}";
                    },
                    tool.Name,
                    enhancedDescription);

                aiFunctions.Add(aiFunc);
            }

            return aiFunctions;
        }
    }

    /// <summary>
    /// 构建包含参数 Schema 的增强描述文本。
    /// </summary>
    /// <remarks>
    /// 由于当前版本的 AIFunctionFactory.Create 不支持独立的参数定义对象，
    /// 将 JSON Schema 追加到描述末尾作为变通方案，使 AI 模型仍能获取参数结构信息。
    /// </remarks>
    private static string BuildEnhancedDescription(ToolRegistration tool)
    {
        if (tool.Parameters.Count == 0)
            return tool.Description;

        var sb = new System.Text.StringBuilder();
        sb.Append(tool.Description);
        sb.Append("\n\nParameters (JSON Schema): ");
        sb.Append(BuildToolSchema(tool));
        return sb.ToString();
    }

    /// <summary>
    /// 按名称异步执行已注册的工具（接受结构化 JSON 参数）。
    /// </summary>
    public async Task<ToolResult> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ToolEntry? entry;
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => e.Registration.Name == toolName);
        }

        if (entry == null)
        {
            return new ToolResult { Name = toolName, IsSuccess = false, Error = $"Tool '{toolName}' not found." };
        }

        try
        {
            return await entry.Handler(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ToolResult { Name = toolName, IsSuccess = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 按名称异步执行已注册的工具（接受 JSON 字符串形式的参数）。
    /// </summary>
    public async Task<ToolResult> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        return await ExecuteToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
    }

    // ── 私有辅助方法 ──

    /// <summary>
    /// 从 JSON 元素中提取实际的字符串输入值。
    /// </summary>
    /// <remarks>
    /// <para>修复 Issue #5: 优先匹配 "input" 键名，再回退到第一个非 null 属性值。</para>
    /// <para>支持的 JSON 结构：</para>
    /// <list type="bullet">
    ///   <item><c>{"input": "hello"}</c> → 优先取 input 的值</item>
    ///   <item><c>{"query": "hello"}</c> → 取第一个非 null 属性值</item>
    ///   <item><c>"hello"</c> → 直接返回字符串</item>
    /// </list>
    /// </remarks>
    private static string ExtractInputFromJson(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            // Issue #5 修复: 第一遍优先查找 "input" 键
            foreach (var prop in json.EnumerateObject())
            {
                if (prop.NameEquals("input") && prop.Value.ValueKind != JsonValueKind.Null)
                {
                    return ExtractStringValue(prop.Value);
                }
            }

            // 第二遍: 回退到第一个非 null 属性
            foreach (var prop in json.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Null)
                {
                    return ExtractStringValue(prop.Value);
                }
            }
        }

        if (json.ValueKind == JsonValueKind.String)
            return json.GetString() ?? string.Empty;

        return json.GetRawText();
    }

    private static string ExtractStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    /// <summary>根据工具注册信息构建 JSON Schema 字符串（用于日志/调试）。</summary>
    private static string BuildToolSchema(ToolRegistration tool)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in tool.Parameters)
        {
            var propDef = new Dictionary<string, object>
            {
                ["type"] = param.Type,
                ["description"] = param.Description
            };
            if (param.EnumValues?.Count > 0)
                propDef["enum"] = param.EnumValues;
            properties[param.Name] = propDef;
            if (param.Required)
                required.Add(param.Name);
        }

        var schema = new { type = "object", properties, required = required.ToArray() };
        return JsonSerializer.Serialize(schema);
    }

    /// <summary>转义字符串中的特殊字符以便安全嵌入 JSON。</summary>
    private static string EscapeJsonString(string input)
    {
        return input.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
    }
}
