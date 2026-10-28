// ============================================================================
// 文件：TeamOrchestrator.cs
// 用途：团队模式的智能体编排器：由协调者把任务拆解为子任务，成员分头执行， 评审者判断是否完成，未完成则带着反馈进入下一轮，直到达成目标或达到轮次上限。
// 实现方：TeamOrchestrator
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using Mono.LLM.Agent.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 团队模式的智能体编排器：由协调者把任务拆解为子任务，成员分头执行，
/// 评审者判断是否完成，未完成则带着反馈进入下一轮，直到达成目标或达到轮次上限。
/// </summary>
/// <remarks>
/// <para>每一轮包含四个阶段：</para>
/// <list type="number">
///   <item><b>规划</b> — 协调者结合原始任务、成员能力与历史轮次，产出带指派对象的子任务清单</item>
///   <item><b>执行</b> — 子任务按顺序分派给成员，后执行的成员可以看到本轮已完成的部分</item>
///   <item><b>汇总</b> — 把本轮所有产出拼接为阶段性成果，作为评审与下一轮的输入</item>
///   <item><b>评审</b> — 评审者判断任务是否完成；未完成时给出下一轮的聚焦点</item>
/// </list>
/// <para>
/// 与辩论模式的区别：辩论模式的成员围绕同一议题各自表述、互不分工；团队模式的成员承担的是
/// <b>互不重叠的子任务</b>，产出需要被拼装成完整方案。与顺序模式的区别：顺序模式的链路在运行前
/// 已固定，团队模式的拆解方式由模型根据任务和上一轮评审动态决定。
/// </para>
/// <para>
/// 所有提示词与解析协议来自 <see cref="IOrchestrationPromptBuilder"/>，编排器本身不含领域文本。
/// </para>
/// </remarks>
public class TeamOrchestrator : IAgentOrchestrator
{
    private const int DefaultMaxRounds = 3;

    private readonly LLMClientFactory _factory;
    private readonly List<AgentDefinition> _members;
    private readonly AgentDefinition? _coordinator;
    private readonly AgentDefinition? _reviewer;
    private readonly int _maxRounds;
    private readonly TeamExecutionMode _executionMode;
    private readonly int _maxDegreeOfParallelism;
    private readonly IOrchestrationPromptBuilder _promptBuilder;
    private readonly ILogger<TeamOrchestrator>? _logger;

    /// <summary>
    /// 初始化 <see cref="TeamOrchestrator"/> 的新实例。
    /// </summary>
    /// <param name="factory">用于创建各 Agent 的 LLM 客户端的工厂实例。</param>
    /// <param name="members">参与执行的团队成员。</param>
    /// <param name="coordinator">
    /// 负责拆解任务与整合最终答复的协调者。为 <c>null</c> 时使用成员列表的第一个。
    /// </param>
    /// <param name="reviewer">
    /// 负责判定任务是否完成的评审者。为 <c>null</c> 时由协调者兼任。
    /// </param>
    /// <param name="maxRounds">最大轮次，默认 3。评审确认完成会提前结束。</param>
    /// <param name="promptBuilder">提示词构建策略，为 <c>null</c> 时使用默认实现。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <param name="executionMode">
    /// 子任务的执行方式，默认并行。并行适合相互独立的子任务；存在上下游依赖时改用
    /// <see cref="TeamExecutionMode.Sequential"/>，后执行者才能看到前者的具体产出。
    /// </param>
    /// <param name="maxDegreeOfParallelism">
    /// 并行执行时的最大并发数，默认 4。用于避免一次性打满模型服务的限流。
    /// </param>
    public TeamOrchestrator(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> members,
        AgentDefinition? coordinator = null,
        AgentDefinition? reviewer = null,
        int maxRounds = DefaultMaxRounds,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<TeamOrchestrator>? logger = null,
        TeamExecutionMode executionMode = TeamExecutionMode.Parallel,
        int maxDegreeOfParallelism = 4)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(members);

        _factory = factory;
        _members = members.ToList();
        _coordinator = coordinator;
        _reviewer = reviewer;
        _maxRounds = Math.Max(1, maxRounds);
        _executionMode = executionMode;
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        _promptBuilder = promptBuilder ?? new DefaultOrchestrationPromptBuilder();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<OrchestrationResult> RunAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        if (_members.Count == 0)
            return new OrchestrationResult { Success = false, Error = "No team members configured." };

        if (string.IsNullOrWhiteSpace(task))
            return new OrchestrationResult { Success = false, Error = "Task description is empty." };

        var coordinator = _coordinator ?? _members[0];
        var reviewer = _reviewer ?? coordinator;

        var result = new OrchestrationResult();
        var watch = Stopwatch.StartNew();

        try
        {
            var rounds = new List<TeamRoundRecord>();

            // 跨轮共享的工作区：本轮写入的产出，下一轮的成员都能读到。
            var workspace = new TeamWorkspace();

            for (int round = 1; round <= _maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var plan = await PlanAsync(coordinator, task, rounds, result, cancellationToken)
                    .ConfigureAwait(false);

                if (plan.Count == 0)
                {
                    _logger?.LogWarning(
                        "Team round {Round}: planner produced no parsable sub-tasks, falling back to one task per member.",
                        round);

                    plan = BuildFallbackPlan(task);
                }

                _logger?.LogDebug("Team round {Round}: {Count} sub-task(s) planned", round, plan.Count);

                var (outputs, executionTokens) = await ExecuteAsync(task, plan, workspace, cancellationToken)
                    .ConfigureAwait(false);

                result.TotalTokens += executionTokens;

                var summary = TeamPromptTemplates.FormatRoundSummary(round, outputs);
                var pending = new TeamRoundRecord
                {
                    Round = round,
                    Plan = plan,
                    Outputs = outputs,
                    Summary = summary
                };

                var reviewContext = rounds
                    .Append(pending)
                    .ToList();

                var (verdict, reviewTokens) = await ReviewAsync(
                    reviewer, task, reviewContext, cancellationToken).ConfigureAwait(false);

                result.TotalTokens += reviewTokens;

                var record = new TeamRoundRecord
                {
                    Round = round,
                    Plan = plan,
                    Outputs = outputs,
                    Summary = summary,
                    Completed = verdict.Completed,
                    Reason = verdict.Reason,
                    NextFocus = verdict.NextFocus
                };

                rounds.Add(record);
                result.TeamRounds.Add(record);

                RecordRoundMessages(result, round, plan, outputs, coordinator.Name);

                _logger?.LogInformation(
                    "Team round {Round}: completed={Completed}, reason={Reason}",
                    round, verdict.Completed, verdict.Reason);

                if (verdict.Completed)
                    break;
            }

            var completed = rounds.Count > 0 && rounds[^1].Completed;

            var (finalOutput, summaryTokens) = await SummarizeAsync(
                coordinator, task, rounds, completed, cancellationToken).ConfigureAwait(false);

            result.TotalTokens += summaryTokens;
            result.FinalOutput = finalOutput;
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Team orchestration was canceled.";
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Team orchestration failed");
        }

        watch.Stop();
        result.Duration = watch.Elapsed;
        return result;
    }

    // ── 阶段实现 ──

    private async Task<IReadOnlyList<TeamTask>> PlanAsync(
        AgentDefinition coordinator,
        string task,
        IReadOnlyList<TeamRoundRecord> rounds,
        OrchestrationResult result,
        CancellationToken cancellationToken)
    {
        var roster = _members
            .Select(m => $"{m.Name}: {m.Role} ({m.Description})")
            .ToList();

        var prompt = _promptBuilder.BuildTeamPlanningContext(task, roster, rounds);

        var response = await ChatAsync(coordinator, prompt, includeTools: false, cancellationToken)
            .ConfigureAwait(false);

        result.TotalTokens += response.InputTokens + response.OutputTokens;
        result.AgentOutputs[$"round{rounds.Count + 1}.plan"] = response.Content;

        return _promptBuilder.ParseTeamPlan(response.Content);
    }

    private async Task<(IReadOnlyDictionary<string, string> Outputs, int Tokens)> ExecuteAsync(
        string task,
        IReadOnlyList<TeamTask> plan,
        TeamWorkspace workspace,
        CancellationToken cancellationToken)
    {
        // 先把全部指派关系解析出来，避免并行时共享 cursor 产生竞态。
        var assignments = ResolveAssignments(plan);

        var roundOutputs = new TeamWorkspace();
        var tokens = 0;

        if (_executionMode == TeamExecutionMode.Sequential)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (content, cost) = await RunSubTaskAsync(
                    assignments[i], task, plan[i], workspace.Snapshot(), cancellationToken)
                    .ConfigureAwait(false);

                workspace.Write(assignments[i].Name, content);
                roundOutputs.Write(assignments[i].Name, content);
                tokens += cost;
            }

            return (roundOutputs.Snapshot(), tokens);
        }

        var costs = new int[plan.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, plan.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                // 并行时拿到的是启动那一刻的快照，同轮其他成员尚未写入的产出不在其中；
                // 但这不影响结果完整性——每个人写完后评审能从工作区读到全量。
                var (content, cost) = await RunSubTaskAsync(
                    assignments[index], task, plan[index], workspace.Snapshot(), token)
                    .ConfigureAwait(false);

                workspace.Write(assignments[index].Name, content);
                roundOutputs.Write(assignments[index].Name, content);
                costs[index] = cost;
            }).ConfigureAwait(false);

        return (roundOutputs.Snapshot(), costs.Sum());
    }

    private async Task<(string Content, int Tokens)> RunSubTaskAsync(
        AgentDefinition agent,
        string task,
        TeamTask subTask,
        IReadOnlyDictionary<string, string> context,
        CancellationToken cancellationToken)
    {
        var prompt = _promptBuilder.BuildTeamExecutionContext(task, subTask, context);

        var response = await ChatAsync(agent, prompt, includeTools: true, cancellationToken)
            .ConfigureAwait(false);

        return (response.Content, response.InputTokens + response.OutputTokens);
    }

    private List<AgentDefinition> ResolveAssignments(IReadOnlyList<TeamTask> plan)
    {
        var assignments = new List<AgentDefinition>(plan.Count);
        var cursor = 0;

        foreach (var subTask in plan)
            assignments.Add(ResolveAssignee(subTask.Assignee, ref cursor));

        return assignments;
    }

    private async Task<(TeamReviewVerdict Verdict, int Tokens)> ReviewAsync(
        AgentDefinition reviewer,
        string task,
        IReadOnlyList<TeamRoundRecord> rounds,
        CancellationToken cancellationToken)
    {
        var prompt = _promptBuilder.BuildTeamReviewContext(task, rounds);

        var response = await ChatAsync(reviewer, prompt, includeTools: false, cancellationToken)
            .ConfigureAwait(false);

        var verdict = _promptBuilder.ParseTeamReview(response.Content);
        return (verdict, response.InputTokens + response.OutputTokens);
    }

    private async Task<(string Text, int Tokens)> SummarizeAsync(
        AgentDefinition coordinator,
        string task,
        IReadOnlyList<TeamRoundRecord> rounds,
        bool completed,
        CancellationToken cancellationToken)
    {
        var prompt = _promptBuilder.BuildTeamFinalContext(task, rounds, completed);

        var response = await ChatAsync(coordinator, prompt, includeTools: false, cancellationToken)
            .ConfigureAwait(false);

        return (response.Content, response.InputTokens + response.OutputTokens);
    }

    // ── 辅助 ──

    private async Task<ChatResponse> ChatAsync(
        AgentDefinition agent,
        string userContent,
        bool includeTools,
        CancellationToken cancellationToken)
    {
        var config = agent.Configuration
            ?? throw new InvalidOperationException($"Agent '{agent.Name}' has no LLMConfiguration.");

        using var client = _factory.Create(config);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, agent.SystemPrompt),
            new(ChatRole.User, userContent)
        };

        var options = new ChatOptions { MaxOutputTokens = agent.MaxReplyTokens };

        if (includeTools && agent.ToolManager?.Tools.Count > 0)
            options.Tools = agent.ToolManager.GetAITools();

        return await client.ChatAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把子任务的指派对象解析为实际成员。
    /// </summary>
    /// <remarks>
    /// 模型可能给出不存在的名称、或在名称后附加说明，因此先精确匹配，再退化到包含匹配，
    /// 仍失败时按轮转分配，保证子任务不会被丢弃。
    /// </remarks>
    private AgentDefinition ResolveAssignee(string? assignee, ref int cursor)
    {
        if (!string.IsNullOrWhiteSpace(assignee))
        {
            var name = assignee.Trim();

            var exact = _members.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return exact;

            var contained = _members.FirstOrDefault(
                m => name.Contains(m.Name, StringComparison.OrdinalIgnoreCase));

            if (contained != null)
                return contained;

            _logger?.LogWarning(
                "Team: unknown assignee '{Assignee}', falling back to round-robin.",
                assignee);
        }

        var fallback = _members[cursor % _members.Count];
        cursor++;
        return fallback;
    }

    /// <summary>
    /// 规划完全失败时的兜底方案：让每个成员独立处理一次原始任务。
    /// </summary>
    private IReadOnlyList<TeamTask> BuildFallbackPlan(string task) =>
        _members.Select(m => new TeamTask(m.Name, task)).ToList();

    private static void RecordRoundMessages(
        OrchestrationResult result,
        int round,
        IReadOnlyList<TeamTask> plan,
        IReadOnlyDictionary<string, string> outputs,
        string coordinatorName)
    {
        var roundLabel = round.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var task in plan)
        {
            result.Messages.Add(new AgentMessage
            {
                FromAgent = coordinatorName,
                ToAgent = task.Assignee,
                Content = task.Description,
                Metadata = { ["round"] = roundLabel, ["stage"] = "plan" }
            });
        }

        foreach (var (name, content) in outputs)
        {
            result.Messages.Add(new AgentMessage
            {
                FromAgent = name,
                ToAgent = "team",
                Content = content,
                Metadata = { ["round"] = roundLabel, ["stage"] = "execute" }
            });

            result.AgentOutputs[$"round{round}.{name}"] = content;
        }
    }
}
