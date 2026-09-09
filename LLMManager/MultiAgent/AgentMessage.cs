namespace LLMManager.MultiAgent;

/// <summary>
/// 表示多智能体系统中 Agent 之间的通信消息。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentMessage"/> 记录了一次 Agent 间交互的完整信息，包括消息的发送方、接收方、
/// 内容、时间戳和可选的元数据。它是 <see cref="OrchestrationResult.Messages"/> 集合的基本元素，
/// 用于追踪多智能体协作过程中的完整对话历史。
/// </para>
/// <para>
/// 元数据字典可用于存储额外的上下文信息，如轮次编号、完成原因等。
/// </para>
/// </remarks>
public class AgentMessage
{
    /// <summary>
    /// 获取或设置消息发送方的 Agent 名称。
    /// </summary>
    /// <value>发出此消息的 Agent 标识符。默认值为空字符串。</value>
    public string FromAgent { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置消息接收方的 Agent 名称或目标标识。
    /// </summary>
    /// <value>接收消息的目标 Agent 名称，或特殊值如 "user"、"all"。默认值为空字符串。</value>
    public string ToAgent { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置消息的内容文本。
    /// </summary>
    /// <value>消息的实际内容，通常为 Agent 生成的回复文本。默认值为空字符串。</value>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置消息的时间戳。
    /// </summary>
    /// <value>消息创建时间，默认为当前 UTC 时间。</value>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 获取或设置消息关联的元数据键值对。
    /// </summary>
    /// <value>用于存储扩展信息的字典，如轮次编号、完成原因等。默认为空字典。</value>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// 返回消息的简短字符串表示形式，包含发送方、接收方和截断后的内容预览。
    /// </summary>
    /// <returns>格式为 "[{FromAgent} → {ToAgent}] {Content前60字符}" 的字符串。</returns>
    public override string ToString() => $"[{FromAgent} → {ToAgent}] {Content[..Math.Min(60, Content.Length)]}";
}
