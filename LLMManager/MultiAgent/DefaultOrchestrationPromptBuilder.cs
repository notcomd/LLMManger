namespace LLMManager.MultiAgent;

/// <summary>
/// <see cref="IOrchestrationPromptBuilder"/> 的默认实现，提供中文提示词模板和标准 ASSIGN 路由协议。
/// </summary>
/// <remarks>
/// <para>
/// 此实现作为库的内置默认值，所有编排器在未指定自定义 <see cref="IOrchestrationPromptBuilder"/> 时
/// 自动使用此实例。应用层可通过实现 <see cref="IOrchestrationPromptBuilder"/> 接口来替换为自定义模板，
/// 以支持不同语言、格式或领域特定的编排协议。
/// </para>
/// <para>
/// 路由协议约定：路由 Agent 输出需包含 <c>ASSIGN: &lt;专家名称&gt;</c> 格式的行。
/// </para>
/// </remarks>
public class DefaultOrchestrationPromptBuilder : IOrchestrationPromptBuilder
{
    /// <summary>路由分配标记前缀，用于从路由输出中解析目标专家。</summary>
    public const string AssignmentPrefix = "ASSIGN:";

    /// <inheritdoc/>
    public string BuildSequentialContext(string task, string previousAgentName, string previousOutput) =>
        $"任务背景: {task}\n\n上一阶段 [{previousAgentName}] 的输出:\n{previousOutput}";

    /// <inheritdoc/>
    public string BuildParallelAggregationInput(string task, Dictionary<string, string> expertOutputs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"原始任务: {task}");
        sb.AppendLine();
        sb.AppendLine("各专家意见:");
        foreach (var (name, content) in expertOutputs)
        {
            sb.AppendLine($"--- [{name}] ---");
            sb.AppendLine(content);
        }
        return sb.ToString();
    }

    /// <inheritdoc/>
    public string BuildParallelFallbackOutput(Dictionary<string, string> expertOutputs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (name, content) in expertOutputs)
        {
            sb.AppendLine($"【{name}】");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <inheritdoc/>
    public string BuildDebateHeader(string task) => $"辩论主题: {task}";

    /// <inheritdoc/>
    public string BuildDebateTurnContext(string task, int round, string debaterName, string debaterRole, string transcript) =>
        $"辩论主题: {task}\n\n" +
        $"第 {round} 轮\n\n" +
        $"之前的讨论:\n{transcript}\n" +
        $"请 {debaterName} 发言（{debaterRole}），回应之前的观点并提出你的看法:";

    /// <inheritdoc/>
    public string FormatDebateEntry(int round, string agentName, string content) =>
        $"[第{round}轮] {agentName}: {content}";

    /// <inheritdoc/>
    public string BuildJudgeContext(string task, string fullTranscript) =>
        $"辩论主题: {task}\n\n完整辩论记录:\n{fullTranscript}\n\n请作为裁判进行总结和评判:";

    /// <inheritdoc/>
    public string BuildRoutingSystemPrompt(string baseSystemPrompt, IEnumerable<string> specialists) =>
        baseSystemPrompt + $"""
            可用专家列表:
            {string.Join("\n", specialists)}

            分析用户任务，确定交给哪个专家处理。输出格式:
            {AssignmentPrefix} <专家名称>
            REASON: <分配理由>
            """;

    /// <inheritdoc/>
    public string? ParseRoutingAssignment(string routingOutput)
    {
        var lines = routingOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith(AssignmentPrefix, StringComparison.OrdinalIgnoreCase))
                return line[AssignmentPrefix.Length..].Trim();
        }
        return null;
    }
}
