// ============================================================================
// 文件：TeamPromptTemplates.cs
// 用途：团队模式提示词协议的默认模板与解析实现。
// 实现方：TeamPromptTemplates
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 团队模式提示词协议的默认模板与解析实现。
/// </summary>
/// <remarks>
/// <para>
/// 这里的实现被 <see cref="IOrchestrationPromptBuilder"/> 的默认接口方法和
/// <see cref="DefaultOrchestrationPromptBuilder"/> 共同复用，避免两处各写一份。
/// </para>
/// <para>
/// 协议刻意采用行标记而不是 JSON：模型输出长段落时 JSON 的引号与换行极易转义出错，
/// 而标记行即使被包在代码块或夹带解释文字也能稳定解析。
/// </para>
/// </remarks>
internal static class TeamPromptTemplates
{
    internal const string TaskSeparator = "### TASK";
    internal const string AssigneePrefix = "ASSIGNEE:";
    internal const string DonePrefix = "DONE:";
    internal const string ReasonPrefix = "REASON:";
    internal const string NextPrefix = "NEXT:";

    // ── 提示词构建 ──

    internal static string BuildPlanningContext(
        string task,
        IEnumerable<string> members,
        IReadOnlyList<TeamRoundRecord> previousRounds)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是这个团队的协调者，负责把任务拆解成可以分头执行的子任务，并指派给合适的成员。");
        sb.AppendLine();
        sb.AppendLine("原始任务:");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("团队成员:");
        foreach (var member in members)
        {
            sb.AppendLine($"- {member}");
        }
        sb.AppendLine();

        if (previousRounds.Count > 0)
        {
            sb.AppendLine("已进行的轮次:");
            AppendRoundDigest(sb, previousRounds);
            sb.AppendLine();
            sb.AppendLine("请针对评审指出的缺口调整本轮拆解方案，不要重复已完成的部分。");
            sb.AppendLine();
        }

        sb.AppendLine("输出要求:");
        sb.AppendLine("- 只输出子任务块，不要写任何解释、开场白或总结");
        sb.AppendLine("- 每个子任务严格按以下格式，描述可以多行:");
        sb.AppendLine();
        sb.AppendLine(TaskSeparator);
        sb.AppendLine($"{AssigneePrefix} <成员名称>");
        sb.AppendLine("<该子任务的具体要求>");
        sb.AppendLine();
        sb.AppendLine("- 子任务数量控制在 1-4 个");
        sb.AppendLine($"- {AssigneePrefix} 必须是上面列出的成员名称之一");

        return sb.ToString();
    }

    internal static string BuildExecutionContext(
        string task,
        TeamTask subTask,
        IReadOnlyDictionary<string, string> completedOutputs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("原始任务:");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("你负责的子任务:");
        sb.AppendLine(subTask.Description);
        sb.AppendLine();

        if (completedOutputs.Count > 0)
        {
            sb.AppendLine("团队其他成员已经完成的部分，可作为参考:");
            foreach (var (name, output) in completedOutputs)
            {
                sb.AppendLine($"--- [{name}] ---");
                sb.AppendLine(output);
                sb.AppendLine();
            }
        }

        sb.AppendLine("请只聚焦你的子任务，直接输出可被汇总的成果，不要复述任务背景，也不要寒暄。");

        return sb.ToString();
    }

    internal static string BuildReviewContext(string task, IReadOnlyList<TeamRoundRecord> rounds)
    {
        var sb = new StringBuilder();

        sb.AppendLine("原始任务:");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("团队到目前为止的产出:");
        AppendRoundDigest(sb, rounds);
        sb.AppendLine();
        sb.AppendLine("请判断原始任务是否已经被完整解决。");
        sb.AppendLine();
        sb.AppendLine("只输出下面三行，不要添加其它内容:");
        sb.AppendLine($"{DonePrefix} true 或 false");
        sb.AppendLine($"{ReasonPrefix} <一句话说明判断依据>");
        sb.AppendLine($"{NextPrefix} <若未完成，下一轮应重点解决什么；已完成则留空>");

        return sb.ToString();
    }

    internal static string BuildFinalContext(
        string task,
        IReadOnlyList<TeamRoundRecord> rounds,
        bool completed)
    {
        var sb = new StringBuilder();

        sb.AppendLine("原始任务:");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine($"团队经过 {rounds.Count} 轮协作，过程如下:");
        AppendRoundDigest(sb, rounds);
        sb.AppendLine();

        if (completed)
        {
            sb.AppendLine("评审已确认任务完成。");
        }
        else
        {
            var last = rounds.Count > 0 ? rounds[^1] : null;
            sb.AppendLine("任务尚未完全达成。");
            if (last != null && !string.IsNullOrWhiteSpace(last.Reason))
                sb.AppendLine($"最后一轮评审意见: {last.Reason}");
            if (last != null && !string.IsNullOrWhiteSpace(last.NextFocus))
                sb.AppendLine($"仍未解决的问题: {last.NextFocus}");
        }

        sb.AppendLine();
        sb.AppendLine("请把以上成果整合成一份完整的最终答复，直接呈现给用户。");
        sb.AppendLine("不要提及轮次、成员名称或协作过程，除非用户明确需要。");

        return sb.ToString();
    }

    // ── 解析 ──

    internal static IReadOnlyList<TeamTask> ParsePlan(string planningOutput)
    {
        if (string.IsNullOrWhiteSpace(planningOutput))
            return Array.Empty<TeamTask>();

        var tasks = new List<TeamTask>();
        var blocks = planningOutput.Split(TaskSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            string? assignee = null;
            var description = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                if (line.TrimStart().StartsWith(AssigneePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    assignee = line[(line.IndexOf(':') + 1)..].Trim().Trim('*', '`', '「', '」');
                    continue;
                }

                description.AppendLine(line);
            }

            var text = description.ToString().Trim();

            // 没有指派对象的块通常是模型的开场白或收尾语，跳过。
            if (string.IsNullOrWhiteSpace(assignee) && string.IsNullOrWhiteSpace(text))
                continue;

            if (string.IsNullOrWhiteSpace(assignee))
                continue;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            tasks.Add(new TeamTask(assignee, text));
        }

        return tasks;
    }

    internal static TeamReviewVerdict ParseReview(string reviewOutput)
    {
        if (string.IsNullOrWhiteSpace(reviewOutput))
            return TeamReviewVerdict.NotCompleted;

        bool? completed = null;
        var reason = string.Empty;
        var nextFocus = string.Empty;

        foreach (var rawLine in reviewOutput.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith(DonePrefix, StringComparison.OrdinalIgnoreCase))
            {
                completed = ParseBoolean(line[(DonePrefix.Length)..]);
            }
            else if (line.StartsWith(ReasonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = line[(ReasonPrefix.Length)..].Trim();
            }
            else if (line.StartsWith(NextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                nextFocus = line[(NextPrefix.Length)..].Trim();
            }
        }

        return new TeamReviewVerdict(completed ?? false, reason, nextFocus);
    }

    private static bool ParseBoolean(string value)
    {
        var token = value.Trim().Trim('*', '`', '。', '.', '"').ToLowerInvariant();

        return token switch
        {
            "true" or "yes" or "y" or "1" or "是" or "已完成" or "完成" or "达成" => true,
            _ => false
        };
    }

    // ── 摘要 ──

    internal static void AppendRoundDigest(StringBuilder sb, IReadOnlyList<TeamRoundRecord> rounds)
    {
        foreach (var round in rounds)
        {
            sb.AppendLine($"【第 {round.Round} 轮】");

            foreach (var task in round.Plan)
            {
                sb.AppendLine($"- 子任务（{task.Assignee}）: {task.Description}");
            }

            foreach (var (name, output) in round.Outputs)
            {
                sb.AppendLine($"- [{name}] 的产出:");
                sb.AppendLine(Indent(output));
            }

            if (!string.IsNullOrWhiteSpace(round.Reason))
                sb.AppendLine($"- 评审意见: {round.Reason}");

            if (!string.IsNullOrWhiteSpace(round.NextFocus))
                sb.AppendLine($"- 下一轮重点: {round.NextFocus}");

            sb.AppendLine();
        }
    }

    internal static string FormatRoundSummary(int round, IReadOnlyDictionary<string, string> outputs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【第 {round} 轮产出】");

        foreach (var (name, output) in outputs)
        {
            sb.AppendLine($"--- [{name}] ---");
            sb.AppendLine(output);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Indent(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
            sb.AppendLine("    " + line.TrimEnd('\r'));
        return sb.ToString().TrimEnd();
    }
}
