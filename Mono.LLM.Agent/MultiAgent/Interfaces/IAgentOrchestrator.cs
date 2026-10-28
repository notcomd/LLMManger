// ============================================================================
// 文件：IAgentOrchestrator.cs
// 用途：定义多智能体编排器的接口，负责协调多个 Agent 协作完成指定任务。
// 实现方：Sequential / Parallel / Debate / Team / Router 五个编排器
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Core;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 定义多智能体编排器的接口，负责协调多个 Agent 协作完成指定任务。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IAgentOrchestrator"/> 是多智能体系统的核心抽象，定义了编排任务的唯一入口方法。
/// 不同的实现类对应不同的协作策略：
/// </para>
/// <list type="bullet">
///   <item><see cref="SequentialOrchestrator"/> — 管道式顺序执行</item>
///   <item><see cref="ParallelOrchestrator"/> — 并行独立执行</item>
///   <item><see cref="DebateOrchestrator"/> — 多轮辩论模式</item>
///   <item><see cref="RouterOrchestrator"/> — 智能路由分配</item>
/// </list>
/// <para>
/// 所有编排器实现都应确保在异常情况下返回包含错误信息的 <see cref="OrchestrationResult"/>，
/// 而非直接抛出未处理的异常。
/// </para>
/// </remarks>
public interface IAgentOrchestrator
{
    /// <summary>
    /// 异步执行编排任务，协调各 Agent 完成指定的任务描述。
    /// </summary>
    /// <param name="task">要执行的任务描述文本。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作的任务，任务结果为包含完整执行信息的 <see cref="OrchestrationResult"/>。</returns>
    Task<OrchestrationResult> RunAsync(
        string task,
        CancellationToken cancellationToken = default);
}
