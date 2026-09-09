using LLMManager.Conversation;
using LLMManager.Tools;

namespace LLMManager.Skills;

/// <summary>
/// 表示技能执行的上下文环境，封装了技能定义、对话管理和工具管理器。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillExecutionContext"/> 在每次技能执行前由 <see cref="SkillExecutor.CreateContext"/> 创建，
/// 它将技能定义与运行时的对话客户端和工具实例绑定在一起，作为单次技能调用的完整执行环境。
/// </para>
/// <para>
/// 该上下文的生命周期与单次技能调用绑定，不应跨多次调用复用。
/// </para>
/// </remarks>
public class SkillExecutionContext
{
    /// <summary>获取当前上下文关联的技能定义。</summary>
    /// <value><see cref="SkillDefinition"/> 实例，包含技能的元数据和系统提示词。</value>
    public SkillDefinition Skill { get; }

    /// <summary>获取当前上下文关联的对话管理器。</summary>
    /// <value><see cref="ConversationManager"/> 实例，负责维护与 LLM 的对话状态。</value>
    public ConversationManager Conversation { get; }

    /// <summary>获取当前上下文关联的工具管理器。</summary>
    /// <value><see cref="ToolManager"/> 实例，负责管理技能可用的工具集。</value>
    public ToolManager Tools { get; }

    /// <summary>
    /// 初始化 <see cref="SkillExecutionContext"/> 类的新实例。
    /// </summary>
    /// <param name="skill">要绑定的技能定义。</param>
    /// <param name="conversation">用于此技能的对话管理器。</param>
    /// <param name="tools">用于此技能的工具管理器。</param>
    public SkillExecutionContext(SkillDefinition skill, ConversationManager conversation, ToolManager tools)
    {
        Skill = skill;
        Conversation = conversation;
        Tools = tools;
    }
}
