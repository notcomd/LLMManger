using Microsoft.Extensions.AI;
using LLMManager.Core;
using System.Text.Json;

namespace LLMManager.Streaming;

/// <summary>
/// 提供 LLM 流式响应的处理能力，支持纯文本收集和带工具调用处理的流式交互。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StreamingManager"/> 封装了对 <see cref="ILLMClient.StreamChatAsync"/> 的两种使用模式：
/// </para>
/// <list type="bullet">
///   <item><see cref="StreamAndCollectAsync"/> — 收集完整流式响应文本，支持逐 Token 回调通知；</item>
///   <item><see cref="StreamWithToolCallHandlingAsync"/> — 在流式过程中检测并自动处理 AI 发起的工具调用。</item>
/// </list>
/// </remarks>
public class StreamingManager
{
    private readonly ILLMClient _client;

    /// <summary>
    /// 初始化 <see cref="StreamingManager"/> 的新实例。
    /// </summary>
    /// <param name="client">用于与 LLM 进行流式通信的客户端实例。</param>
    public StreamingManager(ILLMClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 以流式方式发送消息并收集完整的响应文本。
    /// </summary>
    /// <param name="messages">要发送给 LLM 的完整消息历史记录。</param>
    /// <param name="onToken">
    /// 可选的回调委托，每当收到一个新的 Token 文本片段时触发。
    /// 可用于实现逐字显示、实时日志等功能。
    /// </param>
    /// <param name="options">可选的聊天选项（如温度、Top-P 等）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 流式传输完成后拼接而成的完整响应文本字符串。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 此方法遍历 <see cref="ILLMClient.StreamChatAsync"/> 返回的所有增量更新，
    /// 将每段文本累积到内部的 <see cref="System.Text.StringBuilder"/> 中，
    /// 同时通过 <paramref name="onToken"/> 回调实时通知调用方每个 Token 的到达。
    /// </para>
    /// <para>
    /// 注意：此方法不会自动将响应消息添加到对话历史中，调用方需自行管理历史记录。
    /// </para>
    /// </remarks>
    public async Task<string> StreamAndCollectAsync(
        IEnumerable<ChatMessage> messages,
        Action<string>? onToken = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fullContent = new System.Text.StringBuilder();

        await foreach (var update in _client.StreamChatAsync(messages, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                fullContent.Append(update.Text);
                onToken?.Invoke(update.Text);
            }
        }

        return fullContent.ToString();
    }

    /// <summary>
    /// 以流式方式发送消息，并在检测到工具调用时自动执行工具并将结果反馈给对话。
    /// </summary>
    /// <param name="conversation">
    /// 当前对话的 <see cref="Conversation.ConversationManager"/> 实例，
    /// 用于读取历史记录以及写入工具调用相关的消息。
    /// </param>
    /// <param name="toolManager">
    /// 已注册好可用工具的 <see cref="Tools.ToolManager"/> 实例，
    /// 用于将工具列表传递给 AI 并执行 AI 请求的工具调用。
    /// </param>
    /// <param name="onToken">
    /// 可选的回调委托，每当收到新的文本 Token 时触发。
    /// </param>
    /// <param name="options">可选的聊天选项。若未提供，将创建新的 <see cref="ChatOptions"/> 并注入工具列表。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作完成的 <see cref="Task"/>。</returns>
    /// <remarks>
    /// <para>
    /// 此方法实现了"流式 + 工具调用"的完整工作流：
    /// </para>
    /// <list type="number">
    ///   <item>将 <paramref name="toolManager"/> 中已注册的工具注入到聊天选项中；</item>
    ///   <item>开始流式监听 LLM 的响应；</item>
    ///   <item>当检测到 <see cref="ChatFinishReason.ToolCalls"/> 或 <see cref="ChatFinishReason.Stop"/>
    ///         且响应中包含 <see cref="FunctionCallContent"/> 时，自动提取函数名和参数；</item>
    ///   <item>通过 <paramref name="toolManager"/> 执行对应的工具；</item>
    ///   <item>将工具调用标记和执行结果（或错误信息）写入 <paramref name="conversation"/> 的历史记录中。</item>
    /// </list>
    /// <para>
    /// 工具执行完毕后会终止当前流式循环（break），调用方可根据需要决定是否发起下一轮对话。
    /// </para>
    /// </remarks>
    public async Task StreamWithToolCallHandlingAsync(
        Conversation.ConversationManager conversation,
        Tools.ToolManager toolManager,
        Action<string>? onToken = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var toolOptions = options ?? new ChatOptions();
        toolOptions.Tools = toolManager.GetAITools();

        var buffer = new System.Text.StringBuilder();

        await foreach (var update in _client.StreamChatAsync(
            conversation.History, toolOptions, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                buffer.Append(update.Text);
                onToken?.Invoke(update.Text);
            }

            if ((update.FinishReason == ChatFinishReason.ToolCalls ||
                 update.FinishReason == ChatFinishReason.Stop) &&
                update.Contents?.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent funcCall)
                    {
                        var callId = funcCall.CallId ?? Guid.NewGuid().ToString();
                        var funcName = funcCall.Name ?? string.Empty;
                        var argumentsJson = funcCall.Arguments != null
                            ? JsonSerializer.Serialize(funcCall.Arguments)
                            : "{}";

                        var result = await toolManager.ExecuteToolAsync(funcName, argumentsJson, cancellationToken);

                        conversation.AddAssistantMessage(
                            $"[Tool Call: {funcName}]");

                        conversation.AddToolMessage(
                            callId,
                            result.IsSuccess ? result.Result : $"Error: {result.Error}");
                    }
                }

                break;
            }
        }
    }
}
