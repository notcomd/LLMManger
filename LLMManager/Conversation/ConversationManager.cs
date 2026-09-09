using Microsoft.Extensions.AI;
using LLMManager.Core;

namespace LLMManager.Conversation;

/// <summary>
/// 管理与 LLM 的对话会话，包括消息历史记录、系统提示词维护和上下文窗口管理。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConversationManager"/> 封装了对话的完整生命周期：设置系统提示、添加各类消息、
/// 发送请求（同步/流式）、自动裁剪超长历史、以及清理和回退操作。
/// </para>
/// <para>
/// 实现了 <see cref="IDisposable"/> 接口，用于释放底层 <see cref="ILLMClient"/> 资源。
/// </para>
/// </remarks>
public class ConversationManager : IDisposable
{
    private readonly ILLMClient _client;
    private readonly List<ChatMessage> _history = new();
    private string _systemPrompt = string.Empty;

    /// <summary>
    /// 获取当前对话使用的 LLM 客户端实例。
    /// </summary>
    /// <value>用于与语言模型通信的 <see cref="ILLMClient"/> 实例。</value>
    public ILLMClient Client => _client;

    /// <summary>
    /// 获取当前对话的消息历史记录（只读视图）。
    /// </summary>
    /// <value>
    /// 包含所有已添加消息的只读列表，包括系统提示、用户消息、助手消息和工具消息。
    /// </value>
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();

    /// <summary>
    /// 获取当前历史记录中的消息总数。
    /// </summary>
    /// <value>历史记录列表中的消息条目数量。</value>
    public int MessageCount => _history.Count;

    /// <summary>
    /// 获取或设置上下文窗口的 Token 上限。
    /// </summary>
    /// <value>
    /// 当历史记录估算 Token 数超过此值时，将自动裁剪最早的非系统消息。默认值为 128000。
    /// </value>
    public int ContextTokenLimit { get; set; } = 128000;

    /// <summary>
    /// 初始化 <see cref="ConversationManager"/> 的新实例。
    /// </summary>
    /// <param name="client">用于与 LLM 通信的客户端实例。</param>
    /// <param name="systemPrompt">
    /// 可选的系统提示词。如果提供且非空，将作为首条系统消息插入历史记录。
    /// </param>
    public ConversationManager(ILLMClient client, string? systemPrompt = null)
    {
        _client = client;
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            SetSystemPrompt(systemPrompt);
        }
    }

    /// <summary>
    /// 设置或更新系统提示词，并将其插入到历史记录的首位。
    /// </summary>
    /// <param name="prompt">新的系统提示词内容。</param>
    /// <remarks>
    /// 如果历史记录中已存在系统消息，则先移除旧消息再插入新消息，
    /// 确保始终只有一条系统提示位于历史记录最前面。
    /// </remarks>
    public void SetSystemPrompt(string prompt)
    {
        _systemPrompt = prompt;
        var existingSystemMessage = _history.FirstOrDefault(m => m.Role == ChatRole.System);
        if (existingSystemMessage != null)
        {
            _history.Remove(existingSystemMessage);
        }
        _history.Insert(0, new ChatMessage(ChatRole.System, prompt));
    }

    /// <summary>
    /// 向历史记录中追加一条用户消息。
    /// </summary>
    /// <param name="content">用户消息的文本内容。</param>
    public void AddUserMessage(string content)
    {
        _history.Add(new ChatMessage(ChatRole.User, content));
    }

    /// <summary>
    /// 向历史记录中追加一条助手回复消息。
    /// </summary>
    /// <param name="content">助手回复的文本内容。</param>
    public void AddAssistantMessage(string content)
    {
        _history.Add(new ChatMessage(ChatRole.Assistant, content));
    }

    /// <summary>
    /// 向历史记录中追加一条工具调用结果消息。
    /// </summary>
    /// <param name="toolCallId">关联的工具调用标识符。</param>
    /// <param name="content">工具执行结果的文本内容。</param>
    public void AddToolMessage(string toolCallId, string content)
    {
        var message = new ChatMessage(ChatRole.Tool, content);
        message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        message.AdditionalProperties["toolCallId"] = toolCallId;
        _history.Add(message);
    }

    /// <summary>
    /// 向历史记录中追加一条任意类型的聊天消息。
    /// </summary>
    /// <param name="message">要添加的 <see cref="ChatMessage"/> 实例。</param>
    public void AddMessage(ChatMessage message)
    {
        _history.Add(message);
    }

    /// <summary>
    /// 发送用户消息并获取 LLM 的完整响应（非流式）。
    /// </summary>
    /// <param name="userMessage">用户发送的消息文本。</param>
    /// <param name="options">可选的聊天选项（如温度、最大 Token 数等）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 包含 LLM 响应内容和原始消息的 <see cref="LLMManager.Core.ChatResponse"/> 对象。
    /// 响应消息也会被自动追加到历史记录中。
    /// </returns>
    /// <remarks>
    /// 此方法会在发送前自动将用户消息加入历史记录，并在收到响应后将助手消息也加入历史记录。
    /// 如果估算的 Token 数超过 <see cref="ContextTokenLimit"/>，将在发送前自动裁剪历史记录。
    /// </remarks>
    public async Task<LLMManager.Core.ChatResponse> SendAsync(
        string userMessage,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AddUserMessage(userMessage);

        TrimHistoryIfNeeded();

        var response = await _client.ChatAsync(_history, options, cancellationToken);

        if (response.RawMessage != null)
        {
            _history.Add(response.RawMessage);
        }
        else
        {
            AddAssistantMessage(response.Content);
        }

        return response;
    }

    /// <summary>
    /// 以流式方式发送用户消息并逐 Token 返回 LLM 响应。
    /// </summary>
    /// <param name="userMessage">用户发送的消息文本。</param>
    /// <param name="options">可选的聊天选项。</param>
    /// <param name="cancellationToken">
    /// 用于取消异步操作的令牌。支持通过 <c>[EnumeratorCancellation]</c> 在枚举过程中取消。
    /// </param>
    /// <returns>
    /// 异步枚举 <see cref="ChatResponseUpdate"/> 序列，每个元素代表一个流式增量更新。
    /// 流结束后，完整的助手回复会被自动追加到历史记录中。
    /// </returns>
    /// <remarks>
    /// 与 <see cref="SendAsync"/> 类似，此方法也会在发送前处理用户消息和历史裁剪。
    /// 流式传输完成后，会将收集到的完整内容作为助手消息写入历史记录。
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> SendStreamingAsync(
        string userMessage,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AddUserMessage(userMessage);
        TrimHistoryIfNeeded();

        var fullContent = new System.Text.StringBuilder();
        ChatResponseUpdate? lastUpdate = null;

        await foreach (var update in _client.StreamChatAsync(_history, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                fullContent.Append(update.Text);
            }
            lastUpdate = update;
            yield return update;
        }

        var finalContent = fullContent.ToString();
        if (!string.IsNullOrEmpty(finalContent))
        {
            AddAssistantMessage(finalContent);
        }
    }

    /// <summary>
    /// 清空对话历史记录。
    /// </summary>
    /// <param name="keepSystemPrompt">
    /// 是否保留当前的系统提示词。默认为 <c>true</c>，即清空后重新插入系统消息；
    /// 若设为 <c>false</c>，则完全清除包括系统提示在内的所有历史。
    /// </param>
    public void ClearHistory(bool keepSystemPrompt = true)
    {
        if (keepSystemPrompt && !string.IsNullOrEmpty(_systemPrompt))
        {
            _history.Clear();
            _history.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        }
        else
        {
            _history.Clear();
            _systemPrompt = string.Empty;
        }
    }

    /// <summary>
    /// 移除历史记录中的最后一条非系统消息。
    /// </summary>
    /// <remarks>
    /// 如果最后一条消息是系统消息（角色为 <see cref="ChatRole.System"/>），则不执行任何操作。
    /// 可用于撤回最近一次的操作（如错误添加的用户或助手消息）。
    /// </remarks>
    public void RemoveLastMessage()
    {
        if (_history.Count > 0)
        {
            var last = _history[^1];
            if (last.Role == ChatRole.System) return;
            _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>
    /// 移除历史记录中最后一轮完整的对话交换（用户消息 + 助手回复 + 关联的工具消息）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 从末尾向前查找最近的助手消息，然后移除该助手消息及其之后的所有消息（如工具结果），
    /// 以及对应的用户消息，从而回退一整轮对话。
    /// </para>
    /// <para>
    /// 如果未找到助手消息，则不执行任何操作。
    /// </para>
    /// </remarks>
    public void RemoveLastExchange()
    {
        var assistantIndex = -1;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == ChatRole.Assistant)
            {
                assistantIndex = i;
                break;
            }
        }

        if (assistantIndex < 0) return;

        while (_history.Count > assistantIndex + 1)
        {
            _history.RemoveAt(_history.Count - 1);
        }
        _history.RemoveAt(assistantIndex);

        while (_history.Count > 0 &&
               _history[^1].Role != ChatRole.User &&
               _history[^1].Role != ChatRole.System)
        {
            _history.RemoveAt(_history.Count - 1);
        }

        if (_history.Count > 0 && _history[^1].Role == ChatRole.User)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private void TrimHistoryIfNeeded()
    {
        var estimatedTokens = _history.Sum(m => (m.Text?.Length ?? 0) / 4);
        while (estimatedTokens > ContextTokenLimit && _history.Count > 2)
        {
            var firstNonSystem = _history
                .Select((msg, idx) => new { msg, idx })
                .FirstOrDefault(x => x.msg.Role != ChatRole.System);

            if (firstNonSystem == null) break;
            _history.RemoveAt(firstNonSystem.idx);
            estimatedTokens = _history.Sum(m => (m.Text?.Length ?? 0) / 4);
        }
    }

    /// <summary>
    /// 释放此 <see cref="ConversationManager"/> 实例所持有的资源。
    /// </summary>
    /// <remarks>
    /// 调用底层 <see cref="ILLMClient.Dispose()"/> 方法释放 LLM 客户端资源。
    /// </remarks>
    public void Dispose()
    {
        _client.Dispose();
    }
}
