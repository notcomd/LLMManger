namespace LLMManager.MultiAgent;

/// <summary>
/// 定义多智能体团队的协作模式枚举。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TeamMode"/> 决定了 <see cref="AgentTeam"/> 中多个 Agent 如何协同工作来完成任务。
/// 每种模式对应一种不同的 <see cref="IAgentOrchestrator"/> 实现：
/// </para>
/// <list type="bullet">
///   <item><see cref="Sequential"/> — 顺序执行，Agent 依次处理任务并传递上下文</item>
///   <item><see cref="Parallel"/> — 并行执行，所有 Agent 同时独立处理同一任务</item>
///   <item><see cref="Debate"/> — 辩论模式，Agent 多轮辩论后由裁判总结</item>
/// </list>
/// <para>
/// 此外还有第四种隐含的路由模式（<see cref="RouterOrchestrator"/>），通过独立的路由 Agent 将任务分配给最合适的专家。
/// </para>
/// </remarks>
public enum TeamMode
{
    /// <summary>
    /// 顺序模式：Agent 按列表顺序依次执行，每个 Agent 接收前一阶段的输出作为上下文。
    /// 对应 <see cref="SequentialOrchestrator"/> 编排器。
    /// </summary>
    Sequential,

    /// <summary>
    /// 并行模式：所有 Agent 同时接收相同的任务输入并独立生成响应，可配置聚合 Agent 整合结果。
    /// 对应 <see cref="ParallelOrchestrator"/> 编排器。
    /// </summary>
    Parallel,

    /// <summary>
    /// 辩论模式：Agent 围绕主题进行多轮辩论，每轮依次发言，最终由裁判 Agent 总结评判。
    /// 对应 <see cref="DebateOrchestrator"/> 编排器。
    /// </summary>
    Debate
}
