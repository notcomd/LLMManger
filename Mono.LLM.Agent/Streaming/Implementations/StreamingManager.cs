// ============================================================================
// 文件：StreamingManager.cs
// 用途：提供 LLM 流式响应的处理能力，支持纯文本收集和带工具调用处理的流式交互。
// 实现方：StreamingManager
// 调用方：AgentSession（Harness）、应用层流式对话
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Core;
using System.Text;
using System.Text.Json;

namespace Mono.LLM.Agent.Streaming;

/// <summary>
/// 提供 LLM 流式响应的处理能力，支持纯文本收集和带工具调用处理的流式交互。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StreamingManager"/> 不持有对话状态，全部上下文来自调用方传入的
/// <see cref="Conversation.ConversationManager"/>，因此可以在多个会话之间安全复用。
/// </para>
/// </remarks>
public class StreamingManager
{
    private readonly ILLMClient _client;

    /// <summary>
    /// 初始化 <see cref="StreamingManager"/> 的新实例。
    /// </summary>
    /// <param name="client">用于发起流式请求的 LLM 客户端。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="client"/> 为 <c>null</c> 时抛出。</exception>
    public StreamingManager(ILLMClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// 流式输出并将全部增量拼接为完整文本返回。
    /// </summary>
    /// <param name="messages">消息序列。</param>
    /// <param name="onToken">每收到一个文本增量时的回调。</param>
    /// <param name="options">可选的聊天选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="onReasoning">
    /// 每收到一段思考过程（推理型模型产生）时的回调。思考内容不会混入返回的正文。
    /// </param>
    /// <returns>拼接后的完整回复文本，不含思考过程。</returns>
    public async Task<string> StreamAndCollectAsync(
        IEnumerable<ChatMessage> messages,
        Action<string>? onToken = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default,
        Action<string>? onReasoning = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var fullContent = new StringBuilder();

        await foreach (var update in _client.StreamChatAsync(messages, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                fullContent.Append(update.Text);
                onToken?.Invoke(update.Text);
            }

            EmitReasoning(update, onReasoning);
        }

        return fullContent.ToString();
    }

    /// <summary>
    /// 以多轮循环处理流式响应中的工具调用，直到模型产出最终文本。
    /// </summary>
    /// <param name="conversation">承载消息历史的对话管理器，工具调用的往返记录会写入其中。</param>
    /// <param name="toolManager">提供可用工具及其执行逻辑的工具管理器。</param>
    /// <param name="onToken">每收到一个文本增量时的回调。</param>
    /// <param name="options">可选的聊天选项，本方法不会修改传入实例。</param>
    /// <param name="maxToolRoundtrips">最大工具调用轮次，默认 5。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="onReasoning">每收到一段思考过程时的回调，同样适用于工具往返的各个轮次。</param>
    /// <returns>所有轮次累计产生的助手文本，不含思考过程。</returns>
    /// <remarks>
    /// <para>
    /// 每一轮都会：消费完整流 → 累积文本增量 → 收集 <see cref="FunctionCallContent"/>。
    /// 若本轮存在工具调用，则把带 <see cref="FunctionCallContent"/> 的助手消息与
    /// 带 <see cref="FunctionResultContent"/> 的工具消息写入历史，然后进入下一轮，
    /// 由模型基于工具结果继续生成，直到没有新的工具调用。
    /// </para>
    /// <para>
    /// 达到 <paramref name="maxToolRoundtrips"/> 上限时，会再发起一次不携带工具的请求，
    /// 强制模型基于已有信息给出最终答复，而不是把中间过程直接截断。
    /// </para>
    /// </remarks>
    public async Task<string> StreamWithToolCallHandlingAsync(
        Conversation.ConversationManager conversation,
        Tools.ToolManager toolManager,
        Action<string>? onToken = null,
        ChatOptions? options = null,
        int maxToolRoundtrips = 5,
        CancellationToken cancellationToken = default,
        Action<string>? onReasoning = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(toolManager);

        var aggregated = new StringBuilder();
        var rounds = Math.Max(1, maxToolRoundtrips);

        for (int round = 0; round < rounds; round++)
        {
            var roundOptions = options?.Clone() ?? new ChatOptions();
            if (toolManager.Tools.Count > 0)
                roundOptions.Tools = toolManager.GetAITools();

            var (text, toolCalls) = await ReadRoundAsync(
                conversation, roundOptions, onToken, onReasoning, cancellationToken).ConfigureAwait(false);

            aggregated.Append(text);

            if (toolCalls.Count == 0)
            {
                if (text.Length > 0)
                    conversation.AddAssistantMessage(text);

                return aggregated.ToString();
            }

            conversation.AddMessage(BuildAssistantToolCallMessage(text, toolCalls));

            foreach (var call in toolCalls)
            {
                var argumentsJson = call.Arguments is null
                    ? "{}"
                    : JsonSerializer.Serialize(call.Arguments);

                var result = await toolManager
                    .ExecuteToolAsync(call.Name, argumentsJson, cancellationToken)
                    .ConfigureAwait(false);

                var callId = call.CallId ?? Guid.NewGuid().ToString("N");
                conversation.AddToolMessage(
                    callId,
                    result.IsSuccess ? result.Result : $"Error: {result.Error}");
            }
        }

        // 轮次用尽：关闭工具再请求一次，拿到真正的最终答复而非中途截断。
        var finalOptions = options?.Clone() ?? new ChatOptions();
        finalOptions.Tools = null;

        var (finalText, _) = await ReadRoundAsync(
            conversation, finalOptions, onToken, onReasoning, cancellationToken).ConfigureAwait(false);

        if (finalText.Length > 0)
            conversation.AddAssistantMessage(finalText);

        aggregated.Append(finalText);
        return aggregated.ToString();
    }

    private async Task<(string Text, List<FunctionCallContent> ToolCalls)> ReadRoundAsync(
        Conversation.ConversationManager conversation,
        ChatOptions options,
        Action<string>? onToken,
        Action<string>? onReasoning,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();

        await foreach (var update in _client.StreamChatAsync(conversation.History, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                text.Append(update.Text);
                onToken?.Invoke(update.Text);
            }

            if (update.Contents is not { Count: > 0 } contents)
                continue;

            foreach (var content in contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text.Length: > 0 } reasoning:
                        onReasoning?.Invoke(reasoning.Text);
                        break;
                    case FunctionCallContent call:
                        toolCalls.Add(call);
                        break;
                }
            }
        }

        return (text.ToString(), toolCalls);
    }

    private static void EmitReasoning(ChatResponseUpdate update, Action<string>? onReasoning)
    {
        if (onReasoning == null || update.Contents is not { Count: > 0 } contents)
            return;

        foreach (var content in contents)
        {
            if (content is TextReasoningContent { Text.Length: > 0 } reasoning)
                onReasoning(reasoning.Text);
        }
    }

    private static ChatMessage BuildAssistantToolCallMessage(
        string text,
        IReadOnlyList<FunctionCallContent> toolCalls)
    {
        var contents = new List<AIContent>(toolCalls.Count + 1);

        if (!string.IsNullOrEmpty(text))
            contents.Add(new TextContent(text));

        contents.AddRange(toolCalls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }
}
