// ============================================================================
// 文件：MultiAgentOptions.cs
// 用途：多智能体编排相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.MultiAgent;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 多智能体编排相关的配置。
/// </summary>
/// <remarks>
/// 成员本身（名称、角色、提示词、工具）是业务数据，不适合塞进配置文件的「选项」里；
/// 这里只收拢真正影响编排行为的参数，并提供两个按这些参数装配编排器的快捷方法。
/// </remarks>
public sealed class MultiAgentOptions
{
    /// <summary>获取或设置团队模式的最大轮次，默认 3。</summary>
    public int MaxRounds { get; set; } = 3;

    /// <summary>获取或设置辩论模式的轮次，默认 3。</summary>
    public int DebateRounds { get; set; } = 3;

    /// <summary>获取或设置团队并行执行时的最大并发数，默认 4。</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>获取或设置团队默认执行方式，默认并行。</summary>
    public TeamExecutionMode ExecutionMode { get; set; } = TeamExecutionMode.Parallel;

    /// <summary>
    /// 按本分区参数创建团队编排器。
    /// </summary>
    /// <param name="factory">客户端工厂。</param>
    /// <param name="members">团队成员。</param>
    /// <param name="coordinator">协调者；为 <c>null</c> 时使用第一个成员。</param>
    /// <param name="reviewer">评审者；为 <c>null</c> 时由协调者兼任。</param>
    /// <param name="promptBuilder">可选的提示词构建器。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <returns>配置完毕的 <see cref="TeamOrchestrator"/>。</returns>
    public TeamOrchestrator CreateTeam(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> members,
        AgentDefinition? coordinator = null,
        AgentDefinition? reviewer = null,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<TeamOrchestrator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(members);

        return new TeamOrchestrator(
            factory,
            members,
            coordinator,
            reviewer,
            MaxRounds,
            promptBuilder,
            logger,
            ExecutionMode,
            MaxDegreeOfParallelism);
    }

    /// <summary>
    /// 按本分区参数创建辩论编排器。
    /// </summary>
    /// <param name="factory">客户端工厂。</param>
    /// <param name="debaters">参与辩论的成员。</param>
    /// <param name="judge">裁判；为 <c>null</c> 时由第一个成员兼任。</param>
    /// <param name="promptBuilder">可选的提示词构建器。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <returns>配置完毕的 <see cref="DebateOrchestrator"/>。</returns>
    public DebateOrchestrator CreateDebate(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> debaters,
        AgentDefinition? judge = null,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<DebateOrchestrator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(debaters);

        return new DebateOrchestrator(factory, debaters, judge, DebateRounds, promptBuilder, logger);
    }

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("multiAgent");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (MaxRounds < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxRounds", "团队轮次至少为 1。"));

        if (DebateRounds < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.debateRounds", "辩论轮次至少为 1。"));

        if (MaxDegreeOfParallelism < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxDegreeOfParallelism", "并发度至少为 1。"));

        if (!Enum.IsDefined(ExecutionMode))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.executionMode", $"未知的执行方式值 {(int)ExecutionMode}。"));

        return issues;
    }
}
