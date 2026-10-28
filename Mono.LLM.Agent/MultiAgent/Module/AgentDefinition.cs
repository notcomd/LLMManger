// ============================================================================
// 文件：AgentDefinition.cs
// 用途：表示多智能体系统中单个 Agent（代理）的定义，包含其身份信息、行为配置和工具集。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 表示多智能体系统中单个 Agent（代理）的定义，包含其身份信息、行为配置和工具集。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentDefinition"/> 是构建多智能体团队的基本单元。每个定义描述了一个 AI 代理的角色、
/// 系统提示词、LLM 配置以及可用的工具集。通过组合多个 <see cref="AgentDefinition"/> 实例，
/// 可以使用不同的编排模式（顺序、并行、辩论、路由）来协作完成复杂任务。
/// </para>
/// <para>
/// 该类通常通过 <see cref="AgentTeam"/> 的流式 API 进行配置和组装，
/// 最终由对应的 <see cref="IAgentOrchestrator"/> 实现驱动执行。
/// </para>
/// </remarks>
public class AgentDefinition
{
    /// <summary>
    /// 获取或设置 Agent 的唯一名称标识符。
    /// </summary>
    /// <value>Agent 名称，用于在团队中唯一标识该代理，也用于消息路由和输出映射。默认值为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Agent 的角色描述。
    /// </summary>
    /// <value>角色文本，如"代码审查员"、"数据分析师"、"辩论者"等，用于向 LLM 说明该代理的专业领域。</value>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Agent 的系统提示词。
    /// </summary>
    /// <value>发送给 LLM 的系统级指令，定义该代理的行为规范、输出风格和专业约束。默认值为空字符串。</value>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Agent 的功能描述。
    /// </summary>
    /// <value>对该代理能力和职责的自然语言描述，可用于路由决策时的参考信息。默认值为空字符串。</value>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Agent 使用的 LLM 配置。
    /// </summary>
    /// <value><see cref="LLMConfiguration"/> 实例，指定模型提供商、端点、API 密钥等连接参数。可为 <c>null</c>。</value>
    /// <remarks>
    /// 在编排执行时，如果此属性为 <c>null</c>，将抛出异常。每个参与执行的 Agent 必须配置有效的 LLM 连接。
    /// </remarks>
    public LLMConfiguration? Configuration { get; set; }

    /// <summary>
    /// 获取或设置 Agent 关联的工具管理器。
    /// </summary>
    /// <value><see cref="ToolManager"/> 实例，管理该代理在执行时可调用的工具集。可为 <c>null</c>。</value>
    public ToolManager? ToolManager { get; set; }

    /// <summary>
    /// 获取或设置 Agent 的标签列表。
    /// </summary>
    /// <value>用于分类和筛选的标签集合。默认为空列表。</value>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置 Agent 单次回复的最大 token 数限制。
    /// </summary>
    /// <value>最大输出 token 数量，默认值为 2048。将传递给 <c>ChatOptions.MaxOutputTokens</c>。</value>
    public int MaxReplyTokens { get; set; } = 2048;

    /// <summary>
    /// 返回 Agent 的字符串表示形式，包含名称和角色。
    /// </summary>
    /// <returns>格式为 "[{Name}] {Role}" 的字符串。</returns>
    public override string ToString() => $"[{Name}] {Role}";
}
