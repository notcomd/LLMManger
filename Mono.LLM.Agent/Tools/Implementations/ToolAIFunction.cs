// ============================================================================
// 文件：ToolAIFunction.cs
// 用途：将 ToolManager 中注册的工具暴露为带完整 JSON Schema 的 AIFunction。
// 实现方：ToolAIFunction
// 调用方：ToolManager.GetAITools()
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 将 <see cref="ToolManager"/> 中注册的工具暴露为带完整 JSON Schema 的 <see cref="AIFunction"/>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>AIFunctionFactory.Create(Delegate)</c> 不同，本类型不依赖编译期的方法签名，
/// 而是直接把 <see cref="ToolParameterInfo"/> 描述的参数结构输出到 <see cref="JsonSchema"/>，
/// 因此模型可以看到真实的参数名、类型、枚举与必填约束。
/// </para>
/// <para>
/// 调用时 <see cref="AIFunctionArguments"/> 会被序列化为 <see cref="JsonElement"/> 后交给注册的处理委托，
/// 并由本类型统一把 <see cref="ToolResult"/> 转换为模型可读的字符串结果。
/// </para>
/// </remarks>
internal sealed class ToolAIFunction : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;
    private readonly Func<JsonElement, CancellationToken, Task<ToolResult>> _handler;

    public ToolAIFunction(
        string name,
        string description,
        JsonElement schema,
        Func<JsonElement, CancellationToken, Task<ToolResult>> handler)
    {
        _name = name;
        _description = description;
        _schema = schema;
        _handler = handler;
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        JsonElement args;
        try
        {
            args = SerializeArguments(arguments);
        }
        catch (Exception ex)
        {
            return $"Error: 参数无法序列化 - {ex.Message}";
        }

        try
        {
            var result = await _handler(args, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Result : $"Error: {result.Error}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static JsonElement SerializeArguments(AIFunctionArguments arguments)
    {
        if (arguments.Count == 0)
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

        var payload = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var pair in arguments)
            payload[pair.Key] = pair.Value;

        return JsonSerializer.SerializeToElement(payload);
    }
}

/// <summary>
/// 根据 <see cref="ToolParameterInfo"/> 构建 JSON Schema 的辅助方法。
/// </summary>
internal static class ToolSchemaBuilder
{
    /// <summary>
    /// 生成 <c>{"type":"object","properties":{...},"required":[...]}</c> 形式的 Schema。
    /// </summary>
    public static JsonElement Build(IReadOnlyList<ToolParameterInfo> parameters)
    {
        if (parameters.Count == 0)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            });
        }

        var properties = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                continue;

            var normalized = ToolTypeNames.Normalize(parameter.Type);

            var property = new Dictionary<string, object>
            {
                ["type"] = normalized
            };

            if (!string.IsNullOrWhiteSpace(parameter.Description))
                property["description"] = parameter.Description;

            if (parameter.EnumValues is { Count: > 0 })
                property["enum"] = parameter.EnumValues;

            if (normalized == "array")
                property["items"] = new Dictionary<string, object> { ["type"] = "string" };

            if (parameter.DefaultValue is { } defaultValue &&
                defaultValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                property["default"] = JsonSerializer.SerializeToElement(defaultValue);
            }

            properties[parameter.Name] = property;

            if (parameter.Required)
                required.Add(parameter.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required.Count > 0)
            schema["required"] = required.ToArray();

        return JsonSerializer.SerializeToElement(schema);
    }
}
