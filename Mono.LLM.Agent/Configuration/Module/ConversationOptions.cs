// ============================================================================
// 文件：ConversationOptions.cs
// 用途：对话行为相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Conversation;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 对话行为相关的配置。
/// </summary>
public sealed class ConversationOptions
{
    /// <summary>获取或设置默认系统提示词。为 <c>null</c> 时不注入系统消息。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// 获取或设置上下文 token 上限，默认 128000。超出后自动裁剪最早的非系统消息。
    /// </summary>
    public int ContextTokenLimit { get; set; } = 128_000;

    /// <summary>
    /// 把本分区的配置应用到已有的对话管理器上。
    /// </summary>
    /// <param name="manager">目标对话管理器。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="manager"/> 为 <c>null</c> 时抛出。</exception>
    public void ApplyTo(ConversationManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        manager.ContextTokenLimit = ContextTokenLimit;

        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            manager.SetSystemPrompt(SystemPrompt);
    }

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("conversation");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (ContextTokenLimit <= 0)
        {
            issues.Add(AgentOptionsIssue.Error(
                $"{prefix}.contextTokenLimit",
                "上下文上限必须为正数；该值过小会导致历史被全部裁掉。"));
        }

        return issues;
    }
}
