// ============================================================================
// 文件：SkillExecutionContext.cs
// 用途：表示技能执行的上下文环境，封装了技能定义、对话管理和工具管理器。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Skills;

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
public class SkillExecutionContext : IDisposable
{
    private bool _disposed;
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
    /// 获取按 <see cref="SkillDefinition.AllowedTools"/> 裁剪后实际生效的工具管理器。
    /// </summary>
    /// <value>
    /// 技能未声明白名单时与 <see cref="Tools"/> 相同；声明后仅包含名单内的工具。
    /// </value>
    /// <remarks>
    /// <see cref="SkillExecutor"/> 执行时使用此属性而非 <see cref="Tools"/>，
    /// 以确保未授权的工具既不出现在提示词中，也无法被调用。
    /// </remarks>
    public ToolManager EffectiveTools { get; }

    /// <summary>
    /// 初始化 <see cref="SkillExecutionContext"/> 类的新实例。
    /// </summary>
    /// <param name="skill">要绑定的技能定义。</param>
    /// <param name="conversation">用于此技能的对话管理器。</param>
    /// <param name="tools">用于此技能的工具管理器。</param>
    /// <param name="effectiveTools">
    /// 裁剪后的工具管理器。为 <c>null</c> 时与 <paramref name="tools"/> 相同。
    /// </param>
    public SkillExecutionContext(
        SkillDefinition skill,
        ConversationManager conversation,
        ToolManager tools,
        ToolManager? effectiveTools = null)
    {
        Skill = skill;
        Conversation = conversation;
        Tools = tools;
        EffectiveTools = effectiveTools ?? tools;
    }

    /// <summary>
    /// 释放上下文持有的对话管理器及其底层 LLM 客户端。
    /// </summary>
    /// <remarks>
    /// <see cref="SkillExecutor.CreateContext"/> 会为每个上下文创建独立的客户端，
    /// 因此上下文的所有者需要负责释放，否则连接资源不会被回收。此方法可重复调用。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Conversation.Dispose();
    }
}
