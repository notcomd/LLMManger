using Microsoft.Extensions.AI;

namespace LLMManager.Core;

/// <summary>
/// 表示一次非流式聊天请求的完整响应结果。
/// </summary>
/// <remarks>
/// 该类封装了从 LLM 服务端返回的完整响应信息，包括生成内容、
/// Token 用量统计、结束原因以及原始消息对象等。
/// 适用于需要一次性获取完整回复结果的场景。
/// </remarks>
public class ChatResponse
{
    /// <summary>
    /// 获取或设置模型生成的文本内容。
    /// </summary>
    /// <value>模型返回的文本内容，默认为空字符串。</value>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置响应对应的模型标识符。
    /// </summary>
    /// <value>模型 ID，例如 "gpt-4o"、"deepseek-chat" 等，默认为空字符串。</value>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置本次请求消耗的输入 Token 数量。
    /// </summary>
    /// <value>输入 Token 计数，默认为 0。</value>
    public int InputTokens { get; set; }

    /// <summary>
    /// 获取或设置本次请求生成的输出 Token 数量。
    /// </summary>
    /// <value>输出 Token 计数，默认为 0。</value>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 获取或设置模型停止生成的结束原因。
    /// </summary>
    /// <value>
    /// 结束原因的字符串表示，常见值包括 "Stop"、"Length"、"ToolCall" 等。
    /// 若未提供则为 <c>null</c>。
    /// </value>
    public string? FinishReason { get; set; }

    /// <summary>
    /// 获取或设置来自底层框架的原始聊天消息对象。
    /// </summary>
    /// <value>
    /// <see cref="Microsoft.Extensions.AI.ChatMessage"/> 类型的原始消息，
    /// 包含完整的角色、内容和附加属性信息。若不可用则为 <c>null</c>。
    /// </value>
    public ChatMessage? RawMessage { get; set; }
}
