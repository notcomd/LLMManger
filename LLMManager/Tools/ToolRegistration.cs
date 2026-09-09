using LLMManager.Tools;

namespace LLMManager.Tools;

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
}
