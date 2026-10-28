// ============================================================================
// 文件：AgentTeam.cs
// 用途：表示一个多智能体团队，负责管理成员 Agent 的注册、领导者/裁判者的配置以及协作模式的设定。
// 实现方：AgentTeam
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Core;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 表示一个多智能体团队，负责管理成员 Agent 的注册、领导者/裁判者的配置以及协作模式的设定。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentTeam"/> 采用流畅 API（Fluent Builder）设计模式，通过链式调用方法来组装团队配置。
/// 此类是纯组装工具，不定义任何具体的 Agent 对象 — 所有 <see cref="AgentDefinition"/> 实例均由应用层提供。
/// </para>
/// <para>
/// 配置完成后，调用 <see cref="RunAsync"/> 启动任务执行，该方法会根据当前选择的 <see cref="Mode"/>
/// 自动创建对应的 <see cref="IAgentOrchestrator"/> 实例来驱动整个流程。
/// </para>
/// <para>
/// 四种模式的取舍：
/// <list type="bullet">
///   <item><b>顺序</b> — 流水线固定、后一步依赖前一步，适合「先设计再实现再审查」</item>
///   <item><b>并行</b> — 多视角独立评估同一对象，适合评审与对比</item>
///   <item><b>辩论</b> — 立场对立、需要论证与裁判，适合决策类问题</item>
///   <item><b>团队</b> — 任务可拆分且需要多轮收敛，适合「做一个完整方案」这类复合目标</item>
/// </list>
/// </para>
/// <para>
/// 通过 <see cref="WithPromptBuilder"/> 可自定义所有编排模式下的提示词生成策略，
/// 进一步确保库不包含任何领域相关的硬编码文本。
/// </para>
/// </remarks>
public class AgentTeam
{
    private readonly LLMClientFactory _factory;
    private readonly List<AgentDefinition> _members = new();
    private AgentDefinition? _leader;
    private AgentDefinition? _judge;
    private TeamMode _mode = TeamMode.Sequential;
    private int _debateRounds = 3;
    private int _teamRounds = 3;
    private TeamExecutionMode _teamExecutionMode = TeamExecutionMode.Parallel;
    private IOrchestrationPromptBuilder? _promptBuilder;

    /// <summary>获取团队成员的只读列表。</summary>
    public IReadOnlyList<AgentDefinition> Members => _members.AsReadOnly();

    /// <summary>获取团队的领导/聚合 Agent 定义。</summary>
    public AgentDefinition? Leader => _leader;

    /// <summary>获取团队当前的协作模式。</summary>
    public TeamMode Mode => _mode;

    /// <summary>初始化 <see cref="AgentTeam"/> 类的新实例。</summary>
    /// <param name="factory">用于创建 LLM 客户端的工厂实例，供各 Agent 在执行时使用。</param>
    public AgentTeam(LLMClientFactory factory)
    {
        _factory = factory;
    }

    /// <summary>向团队添加一个成员 Agent。</summary>
    public AgentTeam AddMember(AgentDefinition agent) { _members.Add(agent); return this; }

    /// <summary>批量向团队添加多个成员 Agent。</summary>
    public AgentTeam AddMembers(IEnumerable<AgentDefinition> agents) { _members.AddRange(agents); return this; }

    /// <summary>设置团队的领导/聚合 Agent。</summary>
    public AgentTeam WithLeader(AgentDefinition leader) { _leader = leader; return this; }

    /// <summary>设置团队的裁判 Agent（用于辩论模式）。</summary>
    public AgentTeam WithJudge(AgentDefinition judge) { _judge = judge; return this; }

    /// <summary>将协作模式设置为顺序执行。</summary>
    public AgentTeam UseSequential() { _mode = TeamMode.Sequential; return this; }

    /// <summary>将协作模式设置为并行执行。</summary>
    public AgentTeam UseParallel() { _mode = TeamMode.Parallel; return this; }

    /// <summary>将协作模式设置为辩论模式，并可自定义辩论轮次。</summary>
    public AgentTeam UseDebate(int rounds = 3) { _mode = TeamMode.Debate; _debateRounds = rounds; return this; }

    /// <summary>
    /// 将协作模式设置为团队模式：协调者拆解任务、成员分头执行、
    /// 评审者判定是否完成，未完成则带着反馈进入下一轮。
    /// </summary>
    /// <param name="maxRounds">
    /// 最大迭代轮次，默认 3。评审一旦判定完成会提前结束，不会跑满。
    /// </param>
    /// <param name="executionMode">
    /// 子任务的执行方式，默认并行。若子任务之间存在「后者依赖前者具体内容」的关系，
    /// 应传入 <see cref="TeamExecutionMode.Sequential"/>。
    /// </param>
    /// <returns>当前团队实例，支持链式调用。</returns>
    /// <remarks>
    /// 协调者由 <see cref="WithLeader"/> 指定，未设置时使用成员列表的第一个；
    /// 评审者由 <see cref="WithJudge"/> 指定，未设置时由协调者兼任。
    /// </remarks>
    public AgentTeam UseTeam(
        int maxRounds = 3,
        TeamExecutionMode executionMode = TeamExecutionMode.Parallel)
    {
        _mode = TeamMode.Team;
        _teamRounds = maxRounds;
        _teamExecutionMode = executionMode;
        return this;
    }

    /// <summary>
    /// 设置自定义的提示词构建策略，覆盖默认的 <see cref="DefaultOrchestrationPromptBuilder"/>。
    /// </summary>
    /// <param name="promptBuilder">自定义的提示词构建器实现。若为 <c>null</c> 则使用默认实现。</param>
    /// <returns>当前团队实例，支持链式调用。</returns>
    /// <remarks>
    /// 通过此方法，应用层可以完全控制编排过程中生成的所有提示词内容，
    /// 包括语言、格式、附加指令等，确保库本身不包含任何领域相关的硬编码文本。
    /// </remarks>
    public AgentTeam WithPromptBuilder(IOrchestrationPromptBuilder promptBuilder)
    {
        _promptBuilder = promptBuilder;
        return this;
    }

    /// <summary>异步运行团队任务，根据当前协作模式自动选择对应的编排器执行。</summary>
    public Task<OrchestrationResult> RunAsync(string task, CancellationToken cancellationToken = default)
    {
        var pb = _promptBuilder ?? new DefaultOrchestrationPromptBuilder();

        IAgentOrchestrator orchestrator = _mode switch
        {
            TeamMode.Sequential => new SequentialOrchestrator(_factory, _members, pb),
            TeamMode.Parallel => new ParallelOrchestrator(_factory, _members, _leader, pb),
            TeamMode.Debate => new DebateOrchestrator(_factory, _members, _judge, _debateRounds, pb),
            TeamMode.Team => new TeamOrchestrator(
                _factory, _members, _leader, _judge, _teamRounds, pb,
                executionMode: _teamExecutionMode),
            _ => throw new InvalidOperationException($"Unknown team mode: {_mode}")
        };

        return orchestrator.RunAsync(task, cancellationToken);
    }
}
