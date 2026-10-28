// ============================================================================
// 文件：ToolRegistration.cs
// 用途：表示一个已注册到 ToolManager 的工具的完整注册信息。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillExecutor、StreamingManager、McpToolBridge、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 表示一个已注册到 <see cref="ToolManager"/> 的工具的完整注册信息。
/// </summary>
/// <remarks>
/// 作为 <see cref="ToolManager"/> 内部存储工具元数据的载体，记录了每个工具的
/// 名称、功能描述和参数定义。可通过 <see cref="ToolManager.Tools"/> 属性获取所有已注册的工具信息。
/// </remarks>
public class ToolRegistration
{
    /// <summary>获取或设置工具的唯一名称标识符。</summary>
    /// <value>用于在 AI 模型和执行引擎中引用此工具的名称。默认为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置工具的功能描述。</summary>
    /// <value>向 AI 模型说明此工具用途的自然语言文本，帮助模型判断何时应该调用此工具。默认为空字符串。</value>
    public string Description { get; set; } = string.Empty;

    /// <summary>获取或设置工具的参数定义列表。</summary>
    /// <value>
    /// 由 <see cref="ToolParameterInfo"/> 组成的集合，完整描述了该工具接受的所有参数及其约束条件。
    /// 默认为空列表。
    /// </value>
    public List<ToolParameterInfo> Parameters { get; set; } = new();

    /// <summary>
    /// 获取或设置工具的原始 JSON Schema；设置后将取代由 <see cref="Parameters"/> 生成的 Schema。
    /// </summary>
    /// <value>
    /// 完整的 <c>inputSchema</c> 对象。为 <c>null</c> 时由 <see cref="Parameters"/> 推导，
    /// 适合嵌套对象、数组元素结构复杂等 <see cref="ToolParameterInfo"/> 无法表达的场景（例如 MCP 工具）。
    /// </value>
    /// <remarks>
    /// 提供原始 Schema 的工具不参与本地参数校验——本地结构无法理解嵌套约束，
    /// 交给工具自身（或远端服务器）校验并返回结构化错误更准确。
    /// </remarks>
    public JsonElement? InputSchema { get; set; }
}
