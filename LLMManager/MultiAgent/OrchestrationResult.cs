namespace LLMManager.MultiAgent;

/// <summary>
/// 表示一次多智能体编排任务的执行结果，包含最终输出、消息历史、各 Agent 输出和性能指标。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrchestrationResult"/> 由各类 <see cref="IAgentOrchestrator"/> 实现在任务完成后返回，
/// 提供了完整的执行快照信息，包括每个 Agent 的独立输出、完整的消息交换记录、token 消耗统计和耗时数据。
/// </para>
/// <para>
/// 通过检查 <see cref="Success"/> 和 <see cref="Error"/> 属性可以判断任务是否成功完成。
/// <see cref="FinalOutput"/> 包含了编排器生成的最终结论，而 <see cref="AgentOutputs"/> 则提供了
/// 每个 Agent 的原始输出以便进一步分析。
/// </para>
/// </remarks>
public class OrchestrationResult
{
    /// <summary>
    /// 获取或设置编排任务的最终输出文本。
    /// </summary>
    /// <value>经过编排处理后的最终结果文本。默认值为空字符串。</value>
    /// <remarks>
    /// 在不同模式下，最终输出的来源不同：顺序模式下为最后一个 Agent 的输出；
    /// 并行模式下为聚合 Agent 的输出或拼接的所有输出；辩论模式下为裁判的总结或完整辩论记录。
    /// </remarks>
    public string FinalOutput { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置任务执行过程中产生的所有 Agent 间消息记录。
    /// </summary>
    /// <value><see cref="AgentMessage"/> 列表，按时间顺序记录了完整的交互历史。默认为空列表。</value>
    public List<AgentMessage> Messages { get; set; } = new();

    /// <summary>
    /// 获取或设置各 Agent 的名称到其输出内容的映射字典。
    /// </summary>
    /// <value>以 Agent 名称（或"名称_轮次"格式）为键、输出文本为值的字典。默认为空字典。</value>
    public Dictionary<string, string> AgentOutputs { get; set; } = new();

    /// <summary>
    /// 获取或设置任务执行过程中消耗的总 token 数量。
    /// </summary>
    /// <value>所有 Agent 调用的输入 token 与输出 token 之和。默认值为 0。</value>
    public int TotalTokens { get; set; }

    /// <summary>
    /// 获取或设置任务执行所用的总耗时。
    /// </summary>
    /// <value>从开始到结束经过的时间跨度。默认值为 <see cref="TimeSpan.Zero"/>。</value>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// 获取或设置指示任务是否成功完成的标志。
    /// </summary>
    /// <value>若任务成功完成则为 <c>true</c>；若发生错误则为 <c>false</c>。默认值为 <c>true</c>。</value>
    public bool Success { get; set; } = true;

    /// <summary>
    /// 获取或设置任务失败时的错误信息。
    /// </summary>
    /// <value>错误描述文本；若任务成功则为 <c>null</c>。</value>
    public string? Error { get; set; }

    /// <summary>
    /// 返回结果的摘要字符串表示形式，包含成功状态、参与的 Agent 列表、Token 消耗和耗时。
    /// </summary>
    /// <returns>格式为 "Success={Success}, Agents=[...], Tokens={TotalTokens}, Duration={Duration}s" 的字符串。</returns>
    public override string ToString()
    {
        var agents = string.Join(", ", AgentOutputs.Keys);
        return $"Success={Success}, Agents=[{agents}], Tokens={TotalTokens}, Duration={Duration.TotalSeconds:F1}s";
    }
}
