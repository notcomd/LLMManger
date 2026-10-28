// ============================================================================
// 文件：ToolParameterInfo.cs
// 用途：描述工具函数的单个参数元数据。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillExecutor、StreamingManager、McpToolBridge、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 描述工具函数的单个参数元数据。
/// </summary>
/// <remarks>
/// 用于定义注册到 <see cref="ToolManager"/> 的工具的参数信息，包括名称、类型、描述、是否必填等。
/// 这些信息将被转换为 JSON Schema 格式提供给 AI 模型，帮助模型理解如何正确调用该参数。
/// </remarks>
/// <example>
/// <code>
/// var param = new ToolParameterInfo
/// {
///     Name = "city",
///     Type = "string",
///     Description = "要查询天气的城市名称",
///     Required = true,
///     EnumValues = new List&lt;string&gt; { "北京", "上海", "广州" }
/// };
/// </code>
/// </example>
public class ToolParameterInfo
{
    /// <summary>获取或设置参数的标识符名称。</summary>
    /// <value>参数的唯一名称，用于在 AI 模型和执行引擎中引用此参数。默认为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置参数的数据类型。</summary>
    /// <value>
    /// 参数的类型标识，通常为 JSON Schema 类型名。
    /// 常见值包括：<c>"string"</c>、<c>"number"</c>、<c>"boolean"</c>、<c>"integer"</c>、<c>"array"</c>、<c>"object"</c>。
    /// 默认为 <c>"string"</c>。
    /// </value>
    public string Type { get; set; } = "string";

    /// <summary>获取或设置参数的自然语言描述。</summary>
    /// <value>对参数用途和含义的说明文本，将展示给 AI 模型以辅助其决策是否需要提供此参数。默认为空字符串。</value>
    public string Description { get; set; } = string.Empty;

    /// <summary>获取或设置该参数是否为必填项。</summary>
    /// <value>
    /// 若为 <c>true</c>，AI 模型在调用工具时必须提供此参数；
    /// 若为 <c>false</c>，则该参数为可选项，模型可以自行决定是否传入。
    /// 默认为 <c>false</c>。
    /// </value>
    public bool Required { get; set; }

    /// <summary>获取或设置参数的默认值。</summary>
    /// <value>
    /// 当 AI 模型未显式提供此参数时使用的默认 JSON 值。
    /// 若无默认值则为 <c>null</c>，表示必须由调用方提供。
    /// </value>
    public JsonElement? DefaultValue { get; set; }

    /// <summary>获取或设置参数允许的枚举取值列表。</summary>
    /// <value>
    /// 限制参数可接受的取值范围的字符串集合。例如 <c>["low", "medium", "high"]</c>。
    /// 为 <c>null</c> 或空列表表示不限制取值范围，模型可传入任意合法值。
    /// </value>
    public List<string>? EnumValues { get; set; }
}
