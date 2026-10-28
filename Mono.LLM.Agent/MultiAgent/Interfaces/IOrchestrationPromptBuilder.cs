// ============================================================================
// 文件：IOrchestrationPromptBuilder.cs
// 用途：定义多智能体编排过程中的提示词构建策略接口。
// 实现方：DefaultOrchestrationPromptBuilder（可由 WithPromptBuilder 替换）
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 定义多智能体编排过程中的提示词构建策略接口。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IOrchestrationPromptBuilder"/> 将编排引擎中的提示词模板生成逻辑抽象为可替换的策略。
/// 应用层通过实现此接口可以完全自定义各编排模式下的上下文构建方式，
/// 包括语言、格式、附加指令等所有提示词内容。
/// </para>
/// <para>
/// 库提供 <see cref="DefaultOrchestrationPromptBuilder"/> 作为内置默认实现，
/// 包含中文提示词模板。应用层可在注册时替换为自定义实现以满足特定需求。
/// </para>
/// <para>
/// 此接口的设计遵循"库仅提供基础设施，具体 Agent 定义和交互协议由应用层决定"的原则：
/// 编排器本身不包含任何领域相关的硬编码文本，所有提示词均由此接口的实现者提供。
/// </para>
/// </remarks>
public interface IOrchestrationPromptBuilder
{
    /// <summary>
    /// 构建顺序模式下非首轮 Agent 的上下文输入。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="previousAgentName">上一阶段执行者的 Agent 名称。</param>
    /// <param name="previousOutput">上一阶段的输出内容。</param>
    /// <returns>拼接后的完整上下文字符串。</returns>
    string BuildSequentialContext(string task, string previousAgentName, string previousOutput);

    /// <summary>
    /// 构建并行模式下聚合 Agent 的汇总输入。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="expertOutputs">各专家 Agent 的名称到输出内容的字典。</param>
    /// <returns>包含原始任务和所有专家意见的汇总文本。</returns>
    string BuildParallelAggregationInput(string task, Dictionary<string, string> expertOutputs);

    /// <summary>
    /// 构建并行模式下无聚合 Agent 时的最终输出拼接结果。
    /// </summary>
    /// <param name="expertOutputs">各专家 Agent 的名称到输出内容的字典。</param>
    /// <returns>按顺序拼接的所有专家输出。</returns>
    string BuildParallelFallbackOutput(Dictionary<string, string> expertOutputs);

    /// <summary>
    /// 构建辩论模式的主题头部文本。
    /// </summary>
    /// <param name="task">辩论的主题/任务描述。</param>
    /// <returns>辩论记录的开头部分。</returns>
    string BuildDebateHeader(string task);

    /// <summary>
    /// 构建辩论模式中单轮单次发言的上下文输入。
    /// </summary>
    /// <param name="task">辩论主题。</param>
    /// <param name="round">当前轮次编号。</param>
    /// <param name="debaterName">发言者 Agent 名称。</param>
    /// <param name="debaterRole">发言者 Agent 角色描述。</param>
    /// <param name="transcript">之前的完整辩论记录。</param>
    /// <returns>该发言者的完整输入上下文。</returns>
    string BuildDebateTurnContext(string task, int round, string debaterName, string debaterRole, string transcript);

    /// <summary>
    /// 构建辩论记录中的单条发言条目。
    /// </summary>
    /// <param name="round">轮次编号。</param>
    /// <param name="agentName">发言者名称。</param>
    /// <param name="content">发言内容。</param>
    /// <returns>格式化的发言记录行。</returns>
    string FormatDebateEntry(int round, string agentName, string content);

    /// <summary>
    /// 构建裁判 Agent 的评判上下文输入。
    /// </summary>
    /// <param name="task">辩论主题。</param>
    /// <param name="fullTranscript">完整的辩论记录文本。</param>
    /// <returns>提交给裁判的完整上下文。</returns>
    string BuildJudgeContext(string task, string fullTranscript);

    /// <summary>
    /// 构建路由 Agent 的增强系统提示（含专家列表和输出格式约定）。
    /// </summary>
    /// <param name="baseSystemPrompt">路由 Agent 原始的系统提示词。</param>
    /// <param name="specialists">可用专家列表，每项为 "名称: 角色 (描述)" 格式。</param>
    /// <returns>追加了专家列表和格式指令的系统提示词。</returns>
    string BuildRoutingSystemPrompt(string baseSystemPrompt, IEnumerable<string> specialists);

    /// <summary>
    /// 从路由 Agent 的响应中解析出目标专家名称。
    /// </summary>
    /// <param name="routingOutput">路由 Agent 的原始输出文本。</param>
    /// <returns>解析出的专家名称；若无法解析则返回 <c>null</c>。</returns>
    /// <remarks>
    /// 此方法的解析逻辑必须与 <see cref="BuildRoutingSystemPrompt"/> 中约定的输出格式一致。
    /// 自定义实现时需确保两者的协议匹配。
    /// </remarks>
    string? ParseRoutingAssignment(string routingOutput);

    // ── 团队模式 ──
    // 以下成员均提供默认实现，未覆盖它们的自定义实现仍可正常工作。

    /// <summary>
    /// 构建团队模式中协调者的规划上下文：说明任务、成员名单、历史轮次与输出格式约定。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="members">可用成员的描述列表，每项为 "名称: 角色 (描述)" 格式。</param>
    /// <param name="previousRounds">已完成的轮次记录，首轮为空。</param>
    /// <returns>提交给协调者的完整输入。</returns>
    string BuildTeamPlanningContext(
        string task,
        IEnumerable<string> members,
        IReadOnlyList<TeamRoundRecord> previousRounds)
        => TeamPromptTemplates.BuildPlanningContext(task, members, previousRounds);

    /// <summary>
    /// 构建团队模式中单个成员执行子任务的上下文。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="subTask">分配给该成员的子任务。</param>
    /// <param name="completedOutputs">本轮此前已完成成员的产出，可作为参考。</param>
    /// <returns>提交给执行成员的完整输入。</returns>
    string BuildTeamExecutionContext(
        string task,
        TeamTask subTask,
        IReadOnlyDictionary<string, string> completedOutputs)
        => TeamPromptTemplates.BuildExecutionContext(task, subTask, completedOutputs);

    /// <summary>
    /// 构建团队模式中评审者的判定上下文。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="rounds">截至当前的轮次记录。</param>
    /// <returns>提交给评审者的完整输入。</returns>
    string BuildTeamReviewContext(string task, IReadOnlyList<TeamRoundRecord> rounds)
        => TeamPromptTemplates.BuildReviewContext(task, rounds);

    /// <summary>
    /// 构建团队模式最终答复的整合上下文。
    /// </summary>
    /// <param name="task">原始任务描述。</param>
    /// <param name="rounds">全部轮次记录。</param>
    /// <param name="completed">评审是否确认任务已完成。</param>
    /// <returns>提交给协调者的整合输入。</returns>
    string BuildTeamFinalContext(
        string task,
        IReadOnlyList<TeamRoundRecord> rounds,
        bool completed)
        => TeamPromptTemplates.BuildFinalContext(task, rounds, completed);

    /// <summary>
    /// 从协调者的规划输出中解析出子任务列表。
    /// </summary>
    /// <param name="planningOutput">协调者的原始输出文本。</param>
    /// <returns>解析出的子任务；无法解析时返回空列表。</returns>
    /// <remarks>
    /// 解析逻辑必须与 <see cref="BuildTeamPlanningContext"/> 中约定的输出格式一致。
    /// </remarks>
    IReadOnlyList<TeamTask> ParseTeamPlan(string planningOutput)
        => TeamPromptTemplates.ParsePlan(planningOutput);

    /// <summary>
    /// 从评审者的输出中解析出完成判定。
    /// </summary>
    /// <param name="reviewOutput">评审者的原始输出文本。</param>
    /// <returns>解析出的判定结果；无法解析时返回 <see cref="TeamReviewVerdict.NotCompleted"/>。</returns>
    /// <remarks>
    /// 解析逻辑必须与 <see cref="BuildTeamReviewContext"/> 中约定的输出格式一致。
    /// </remarks>
    TeamReviewVerdict ParseTeamReview(string reviewOutput)
        => TeamPromptTemplates.ParseReview(reviewOutput);
}
