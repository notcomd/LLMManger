// ============================================================================
// 文件：ToolArgumentValidator.cs
// 用途：在调用 handler 之前校验模型给出的参数，并把校验失败的原因组织成模型能理解的描述。
// 实现方：ToolArgumentValidator
// 调用方：ToolManager 执行管线
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 参数类型的规范化与兼容性判断。
/// </summary>
internal static class ToolTypeNames
{
    /// <summary>把注册时声明的 CLR 风格类型名归一化为 JSON Schema 类型名。</summary>
    public static string Normalize(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            null or "" => "string",
            "str" or "text" or "varchar" => "string",
            "int" or "int32" or "int64" or "long" or "short" => "integer",
            "float" or "double" or "decimal" or "num" or "single" => "number",
            "bool" => "boolean",
            "list" or "array" or "string[]" => "array",
            "dict" or "map" or "json" => "object",
            var other => other
        };

    /// <summary>
    /// 判断实际传入的值能否被期望的类型接受。
    /// </summary>
    /// <remarks>
    /// 模型经常把数字写成字符串（<c>"3"</c>）、把布尔写成 <c>"true"</c>，这类情况视为可接受，
    /// 只拦截真正无法解释的结构性错配（例如用字符串冒充数组）。校验的目的是帮模型纠错，
    /// 而不是制造无谓的拒绝。
    /// </remarks>
    public static bool Accepts(string normalizedType, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;

        return normalizedType switch
        {
            "string" => value.ValueKind is JsonValueKind.String
                or JsonValueKind.Number
                or JsonValueKind.True
                or JsonValueKind.False,
            "integer" => IsIntegral(value),
            "number" => IsNumeric(value),
            "boolean" => value.ValueKind is JsonValueKind.True
                or JsonValueKind.False
                || (value.ValueKind is JsonValueKind.String && bool.TryParse(value.GetString(), out _)),
            "array" => value.ValueKind is JsonValueKind.Array,
            "object" => value.ValueKind is JsonValueKind.Object,
            _ => true
        };
    }

    private static bool IsIntegral(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Number)
            return value.TryGetInt64(out _);

        return value.ValueKind is JsonValueKind.String
               && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsNumeric(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Number)
            return true;

        return value.ValueKind is JsonValueKind.String
               && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>获取 JSON 值的可读类型名，用于生成错误信息。</summary>
    public static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "undefined"
    };
}

/// <summary>
/// 在调用 handler 之前校验模型给出的参数，并把校验失败的原因组织成模型能理解的描述。
/// </summary>
internal static class ToolArgumentValidator
{
    /// <summary>
    /// 校验参数，返回错误描述列表。空列表表示通过。
    /// </summary>
    public static IReadOnlyList<string> Validate(
        JsonElement arguments,
        IReadOnlyList<ToolParameterInfo> parameters)
    {
        if (parameters.Count == 0)
            return Array.Empty<string>();

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new[]
            {
                $"参数必须是一个 JSON 对象，实际收到 {ToolTypeNames.Describe(arguments)}。"
            };
        }

        var provided = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            provided[property.Name] = property.Value;

        var errors = new List<string>();

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                continue;

            if (!provided.TryGetValue(parameter.Name, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (parameter.Required)
                {
                    var hint = string.IsNullOrWhiteSpace(parameter.Description)
                        ? string.Empty
                        : $"（{parameter.Description}）";
                    errors.Add($"缺少必填参数 '{parameter.Name}'，类型 {ToolTypeNames.Normalize(parameter.Type)}{hint}。");
                }

                continue;
            }

            var expected = ToolTypeNames.Normalize(parameter.Type);
            if (!ToolTypeNames.Accepts(expected, value))
            {
                errors.Add(
                    $"参数 '{parameter.Name}' 期望 {expected}，实际收到 {ToolTypeNames.Describe(value)}。");
                continue;
            }

            if (parameter.EnumValues is { Count: > 0 } &&
                value.ValueKind is JsonValueKind.String)
            {
                var actual = value.GetString();
                if (!parameter.EnumValues.Contains(actual, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"参数 '{parameter.Name}' 的取值必须是 {string.Join(" / ", parameter.EnumValues)} 之一，实际为 '{actual}'。");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// 把声明的默认值填充到缺失的可选参数中。
    /// </summary>
    /// <returns>填充后的参数对象；无需填充时返回原值。</returns>
    public static JsonElement ApplyDefaults(
        JsonElement arguments,
        IReadOnlyList<ToolParameterInfo> parameters)
    {
        if (parameters.Count == 0 || arguments.ValueKind != JsonValueKind.Object)
            return arguments;

        JsonObject? target = null;

        foreach (var parameter in parameters)
        {
            if (parameter.DefaultValue is not { } defaultValue ||
                defaultValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (arguments.TryGetProperty(parameter.Name, out _))
                continue;

            target ??= JsonNode.Parse(arguments.GetRawText())?.AsObject() ?? new JsonObject();
            target[parameter.Name] = JsonNode.Parse(defaultValue.GetRawText());
        }

        return target is null ? arguments : JsonSerializer.SerializeToElement(target);
    }
}
