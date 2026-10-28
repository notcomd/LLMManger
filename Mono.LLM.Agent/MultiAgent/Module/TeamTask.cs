// ============================================================================
// 文件：TeamTask.cs
// 用途：表示团队模式中由协调者拆解出的一个子任务。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 表示团队模式中由协调者拆解出的一个子任务。
/// </summary>
/// <param name="Assignee">建议承接该子任务的成员名称，可能为空（解析时未给出）。</param>
/// <param name="Description">子任务的具体描述。</param>
/// <remarks>
/// 子任务由协调 Agent 在规划阶段产出，随后被分派给对应成员执行。
/// 当 <see cref="Assignee"/> 为空或指向不存在的成员时，编排器会回退到成员列表轮转分配。
/// </remarks>
public sealed record TeamTask(string Assignee, string Description)
{
    /// <summary>
    /// 返回该子任务的简短表示，便于日志与追踪。
    /// </summary>
    /// <returns>格式为 "[指派对象] 描述前 60 字符" 的字符串。</returns>
    public override string ToString()
    {
        var who = string.IsNullOrWhiteSpace(Assignee) ? "未指派" : Assignee;
        var desc = Description.Length > 60 ? Description[..60] + "…" : Description;
        return $"[{who}] {desc}";
    }
}

/// <summary>
/// 记录团队模式中一轮「规划 → 执行 → 汇总 → 评审」的完整快照。
/// </summary>
/// <remarks>
/// 这些记录保存在 <see cref="OrchestrationResult.TeamRounds"/> 中，
/// 用于还原团队是如何逐步收敛到最终答案的：每一轮拆了什么、谁做了什么、评审为什么不通过。
/// </remarks>
public sealed class TeamRoundRecord
{
    /// <summary>轮次编号，从 1 开始。</summary>
    public int Round { get; init; }

    /// <summary>本轮拆解出的子任务列表。</summary>
    public IReadOnlyList<TeamTask> Plan { get; init; } = Array.Empty<TeamTask>();

    /// <summary>本轮各成员的实际产出，键为成员名称。</summary>
    public IReadOnlyDictionary<string, string> Outputs { get; init; } =
        new Dictionary<string, string>();

    /// <summary>本轮产出拼接而成的阶段性汇总文本。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>评审是否判定任务已完成。</summary>
    public bool Completed { get; init; }

    /// <summary>评审给出的理由。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>若未完成，下一轮应重点解决的问题。</summary>
    public string NextFocus { get; init; } = string.Empty;

    /// <summary>返回本轮的简短摘要，便于日志输出。</summary>
    public override string ToString() =>
        $"Round {Round}: tasks={Plan.Count}, outputs={Outputs.Count}, completed={Completed}";
}

/// <summary>
/// 表示团队评审者对一轮产出的判定结果。
/// </summary>
/// <param name="Completed">任务是否已被完整解决。</param>
/// <param name="Reason">判定理由。</param>
/// <param name="NextFocus">若未完成，下一轮应重点解决的问题。</param>
public sealed record TeamReviewVerdict(bool Completed, string Reason, string NextFocus)
{
    /// <summary>表示「未完成且无附加说明」的默认判定，解析失败时作为兜底。</summary>
    public static TeamReviewVerdict NotCompleted { get; } = new(false, string.Empty, string.Empty);
}
