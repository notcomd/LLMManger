using LLMManager.Core;
using LLMManager.Conversation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatResponse = LLMManager.Core.ChatResponse;

namespace LLMManager.Sessions;

/// <summary>
/// 定义会话管理器的接口，提供会话的创建、删除、查询、通信和生命周期管理功能。
/// </summary>
/// <remarks>
/// <para>会话管理器是LLM会话系统的核心入口点，负责协调会话的完整生命周期，
/// 包括会话的CRUD操作、与LLM模型的交互、消息历史管理以及事件通知。</para>
/// <para>每个会话关联一个独立的对话上下文（<see cref="ConversationManager"/>），
/// 维护该会话的消息历史和系统提示词。</para>
/// </remarks>
public interface ISessionManager
{
    /// <summary>
    /// 异步创建一个新的LLM会话。
    /// </summary>
    /// <param name="name">会话的显示名称，不能为空或空白。</param>
    /// <param name="config">
    /// 可选的LLM配置。如果为 null，则使用管理器初始化时设置的默认配置。
    /// </param>
    /// <param name="systemPrompt">可选的系统提示词。如果为 null 则使用空字符串。</param>
    /// <param name="tags">可选的标签集合，用于对会话进行分类和筛选。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示异步创建操作的任务。返回新创建的 <see cref="SessionInfo"/> 对象，
    /// 其中包含自动生成的唯一ID。
    /// </returns>
    /// <exception cref="ArgumentException">
    /// 当 <paramref name="name"/> 为空或空白字符串时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 当未提供配置且没有设置默认配置时抛出。
    /// </exception>
    Task<SessionInfo> CreateSessionAsync(
        string name,
        LLMConfiguration? config = null,
        string? systemPrompt = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据会话ID异步删除指定的会话及其所有关联资源。
    /// </summary>
    /// <param name="sessionId">要删除的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步删除操作的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步重命名指定的会话。
    /// </summary>
    /// <param name="sessionId">要重命名的唯一会话标识符。</param>
    /// <param name="newName">新的会话名称。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步更新操作的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    Task RenameSessionAsync(string sessionId, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步设置指定会话的标签列表。
    /// </summary>
    /// <param name="sessionId">要修改标签的唯一会话标识符。</param>
    /// <param name="tags">新的标签集合，将完全替换现有标签。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步更新操作的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    Task SetSessionTagsAsync(string sessionId, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步设置指定会话的系统提示词。
    /// </summary>
    /// <param name="sessionId">要修改系统提示词的唯一会话标识符。</param>
    /// <param name="systemPrompt">新的系统提示词内容。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步更新操作的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    Task SetSessionSystemPromptAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定会话发送消息并获取完整的响应结果。
    /// </summary>
    /// <param name="sessionId">目标会话的唯一标识符。</param>
    /// <param name="message">要发送的用户消息内容。</param>
    /// <param name="options">可选的聊天选项，如温度、最大Token数等参数。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示异步发送操作的任务。返回包含LLM完整回复内容的 <see cref="ChatResponse"/> 对象。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    /// <remarks>
    /// 发送消息后，该会话将自动成为当前活跃会话，同时更新会话的最后活跃时间、
    /// 消息计数和Token消耗统计信息。
    /// </remarks>
    Task<ChatResponse> SendAsync(
        string sessionId,
        string message,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定会话发送消息并以流式方式获取响应更新。
    /// </summary>
    /// <param name="sessionId">目标会话的唯一标识符。</param>
    /// <param name="message">要发送的用户消息内容。</param>
    /// <param name="options">可选的聊天选项，如温度、最大Token数等参数。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 返回 <see cref="IAsyncEnumerable{T}"/>，逐个产生 <see cref="ChatResponseUpdate"/> 对象，
    /// 每个对象包含部分响应内容。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    /// <remarks>
    /// 流式传输适用于需要实时显示LLM回复的场景。完成后同样会更新会话统计信息。
    /// </remarks>
    IAsyncEnumerable<ChatResponseUpdate> SendStreamingAsync(
        string sessionId,
        string message,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据会话ID获取对应的会话信息。
    /// </summary>
    /// <param name="sessionId">要查找的唯一会话标识符。</param>
    /// <returns>
    /// 如果找到匹配的会话则返回对应的 <see cref="SessionInfo"/> 对象；
    /// 如果未找到则返回 null。
    /// </returns>
    SessionInfo? GetSession(string sessionId);

    /// <summary>
    /// 获取所有已存在会话的信息列表。
    /// </summary>
    /// <returns>
    /// 包含所有会话信息的只读列表，按最后活跃时间降序排列（最近活跃的排在前面）。
    /// </returns>
    IReadOnlyList<SessionInfo> GetAllSessions();

    /// <summary>
    /// 根据查询关键字搜索匹配的会话。
    /// </summary>
    /// <param name="query">搜索查询文本（不区分大小写）。</param>
    /// <returns>
    /// 匹配的会话信息集合。搜索范围包括会话名称、会话ID和标签，
    /// 结果按最后活跃时间降序排列。
    /// </returns>
    /// <remarks>
    /// 搜索支持模糊匹配，只要名称、ID或任一标签中包含查询文本即视为匹配。
    /// </remarks>
    IEnumerable<SessionInfo> SearchSessions(string query);

    /// <summary>
    /// 根据标签筛选具有指定标签的所有会话。
    /// </summary>
    /// <param name="tag">要筛选的标签名称（不区分大小写）。</param>
    /// <returns>
    /// 包含指定标签的会话信息集合，按最后活跃时间降序排列。
    /// </returns>
    IEnumerable<SessionInfo> FilterByTag(string tag);

    /// <summary>
    /// 获取指定会话的完整聊天消息历史记录。
    /// </summary>
    /// <param name="sessionId">目标会话的唯一标识符。</param>
    /// <returns>
    /// 包含该会话所有聊天消息的只读列表，按时间顺序排列。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    IReadOnlyList<ChatMessage> GetSessionHistory(string sessionId);

    /// <summary>
    /// 获取当前活跃会话的ID。
    /// </summary>
    /// <returns>当前活跃会话的唯一标识符字符串；如果没有活跃会话则返回空字符串。</returns>
    string GetActiveSessionId();

    /// <summary>
    /// 将指定会话设置为当前活跃会话。
    /// </summary>
    /// <param name="sessionId">要激活的唯一会话标识符。</param>
    /// <remarks>
    /// 如果指定ID的会话不存在，此方法将静默返回而不执行任何操作。
    /// 设置成功后将触发 <see cref="SessionActivated"/> 事件。
    /// </remarks>
    void SetActiveSession(string sessionId);

    /// <summary>
    /// 清除指定会话的聊天历史记录。
    /// </summary>
    /// <param name="sessionId">目标会话的唯一标识符。</param>
    /// <param name="keepSystemPrompt">
    /// 是否保留系统提示词消息。默认为 <c>true</c>，即仅清除用户和助手的对话消息；
    /// 设为 <c>false</c> 时将清除包括系统提示词在内的所有消息。
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// 当指定ID的会话不存在时抛出。
    /// </exception>
    void ClearSessionHistory(string sessionId, bool keepSystemPrompt = true);

    /// <summary>
    /// 获取当前管理的会话总数。
    /// </summary>
    /// <value>当前存在的会话数量。</value>
    int SessionCount { get; }

    /// <summary>
    /// 在新会话创建完成时触发的事件。
    /// </summary>
    event EventHandler<SessionInfo>? SessionCreated;

    /// <summary>
    /// 在会话被删除后触发的事件。
    /// </summary>
    event EventHandler<SessionInfo>? SessionDeleted;

    /// <summary>
    /// 在活跃会话切换时触发的事件。
    /// </summary>
    event EventHandler<SessionInfo>? SessionActivated;
}

/// <summary>
/// 会话管理器的默认实现，提供完整的会话生命周期管理和LLM通信功能。
/// </summary>
/// <remarks>
/// <para><see cref="SessionManager"/> 是 <see cref="ISessionManager"/> 接口的核心实现，
/// 协调以下组件协同工作：</para>
/// <list type="bullet">
///   <item><see cref="LLMClientFactory"/> — 用于根据配置创建LLM客户端实例</item>
///   <item><see cref="ISessionStore"/> — 用于持久化会话元数据</item>
///   <item><see cref="ConversationManager"/> — 管理每个会话独立的对话上下文和消息历史</item>
/// </list>
/// <para>此类内部通过锁机制保证线程安全，确保在多线程环境下对会话集合的操作不会产生竞争条件。</para>
/// <para>每个会话维护一个 <see cref="SessionContext"/> 内部对象，封装了会话信息、对话管理器和LLM配置，
/// 并实现了 <see cref="IDisposable"/> 接口以正确释放对话管理器资源。</para>
/// </remarks>
public class SessionManager : ISessionManager
{
    private readonly LLMClientFactory _clientFactory;
    private readonly ISessionStore _store;
    private readonly LLMConfiguration? _defaultConfig;
    private readonly ILogger<SessionManager>? _logger;
    private readonly Dictionary<string, SessionContext> _contexts = new();
    private readonly object _lock = new();
    private string _activeSessionId = string.Empty;

    /// <inheritdoc/>
    public event EventHandler<SessionInfo>? SessionCreated;

    /// <inheritdoc/>
    public event EventHandler<SessionInfo>? SessionDeleted;

    /// <inheritdoc/>
    public event EventHandler<SessionInfo>? SessionActivated;

    /// <inheritdoc/>
    public int SessionCount
    {
        get { lock (_lock) return _contexts.Count; }
    }

    /// <summary>
    /// 初始化 <see cref="SessionManager"/> 类的新实例。
    /// </summary>
    /// <param name="clientFactory">
    /// LLM客户端工厂实例，用于根据配置创建LLM客户端。此参数不能为 null。
    /// </param>
    /// <param name="defaultConfig">
    /// 可选的默认LLM配置。当创建会话未显式指定配置时使用此默认值。
    /// 如果为 null 且创建会话时也未提供配置，则会抛出异常。
    /// </param>
    /// <param name="store">
    /// 可选的会话存储实现。如果为 null，则使用 <see cref="InMemorySessionStore"/>
    /// 作为默认的内存存储实现。
    /// </param>
    /// <param name="logger">可选的日志记录器实例，用于记录会话操作日志。</param>
    /// <exception cref="ArgumentNullException">
    /// 当 <paramref name="clientFactory"/> 为 null 时抛出。
    /// </exception>
    public SessionManager(
        LLMClientFactory clientFactory,
        LLMConfiguration? defaultConfig = null,
        ISessionStore? store = null,
        ILogger<SessionManager>? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _defaultConfig = defaultConfig;
        _store = store ?? new InMemorySessionStore();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SessionInfo> CreateSessionAsync(
        string name,
        LLMConfiguration? config = null,
        string? systemPrompt = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Session name cannot be empty.", nameof(name));

        var effectiveConfig = config ?? _defaultConfig;
        if (effectiveConfig == null)
            throw new InvalidOperationException(
                "No LLM configuration provided. Pass a config or set a default.");

        var session = new SessionInfo
        {
            Name = name,
            SystemPrompt = systemPrompt ?? string.Empty,
            ProviderType = effectiveConfig.ProviderType,
            ModelId = effectiveConfig.ModelId,
            Tags = tags?.ToList() ?? new List<string>(),
            CreatedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow
        };

        var client = _clientFactory.Create(effectiveConfig);
        var conversation = new ConversationManager(client, systemPrompt);

        var context = new SessionContext(session, conversation, effectiveConfig);

        lock (_lock)
        {
            _contexts[session.Id] = context;
            if (string.IsNullOrEmpty(_activeSessionId))
            {
                _activeSessionId = session.Id;
            }
        }

        await _store.SaveAsync(session, cancellationToken);

        _logger?.LogInformation("Session created: {Id} - {Name}", session.Id, session.Name);
        SessionCreated?.Invoke(this, session);

        return session;
    }

    /// <inheritdoc/>
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        SessionContext context;
        lock (_lock)
        {
            if (!_contexts.Remove(sessionId, out context!))
                throw new InvalidOperationException($"Session '{sessionId}' not found.");

            if (_activeSessionId == sessionId)
            {
                _activeSessionId = _contexts.Keys.FirstOrDefault() ?? string.Empty;
            }
        }

        context.Dispose();
        await _store.DeleteAsync(sessionId, cancellationToken);

        _logger?.LogInformation("Session deleted: {Id}", sessionId);
        SessionDeleted?.Invoke(this, context.Session);
    }

    /// <inheritdoc/>
    public async Task RenameSessionAsync(string sessionId, string newName, CancellationToken cancellationToken = default)
    {
        var context = GetContext(sessionId);
        context.Session.Name = newName;
        context.Session.LastActiveAt = DateTime.UtcNow;
        await _store.SaveAsync(context.Session, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetSessionTagsAsync(string sessionId, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var context = GetContext(sessionId);
        context.Session.Tags = tags.ToList();
        context.Session.LastActiveAt = DateTime.UtcNow;
        await _store.SaveAsync(context.Session, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetSessionSystemPromptAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default)
    {
        var context = GetContext(sessionId);
        context.Conversation.SetSystemPrompt(systemPrompt);
        context.Session.SystemPrompt = systemPrompt;
        context.Session.LastActiveAt = DateTime.UtcNow;
        await _store.SaveAsync(context.Session, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> SendAsync(
        string sessionId,
        string message,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var context = GetContext(sessionId);
        _activeSessionId = sessionId;

        var response = await context.Conversation.SendAsync(message, options, cancellationToken);

        context.Session.MessageCount = context.Conversation.MessageCount;
        context.Session.TotalInputTokens += response.InputTokens;
        context.Session.TotalOutputTokens += response.OutputTokens;
        context.Session.LastActiveAt = DateTime.UtcNow;

        await _store.SaveAsync(context.Session, cancellationToken);

        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> SendStreamingAsync(
        string sessionId,
        string message,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = GetContext(sessionId);
        _activeSessionId = sessionId;

        await foreach (var update in context.Conversation.SendStreamingAsync(message, options, cancellationToken))
        {
            yield return update;
        }

        context.Session.MessageCount = context.Conversation.MessageCount;
        context.Session.LastActiveAt = DateTime.UtcNow;
        await _store.SaveAsync(context.Session, cancellationToken);
    }

    /// <inheritdoc/>
    public SessionInfo? GetSession(string sessionId)
    {
        lock (_lock)
        {
            return _contexts.TryGetValue(sessionId, out var ctx) ? ctx.Session : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        lock (_lock)
        {
            return _contexts.Values
                .Select(c => c.Session)
                .OrderByDescending(s => s.LastActiveAt)
                .ToList();
        }
    }

    /// <inheritdoc/>
    public IEnumerable<SessionInfo> SearchSessions(string query)
    {
        var lower = query.ToLowerInvariant();
        lock (_lock)
        {
            return _contexts.Values
                .Select(c => c.Session)
                .Where(s =>
                    s.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                    s.Id.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                    s.Tags.Any(t => t.Contains(lower, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(s => s.LastActiveAt)
                .ToList();
        }
    }

    /// <inheritdoc/>
    public IEnumerable<SessionInfo> FilterByTag(string tag)
    {
        lock (_lock)
        {
            return _contexts.Values
                .Select(c => c.Session)
                .Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(s => s.LastActiveAt)
                .ToList();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> GetSessionHistory(string sessionId)
    {
        return GetContext(sessionId).Conversation.History;
    }

    /// <inheritdoc/>
    public string GetActiveSessionId()
    {
        lock (_lock) return _activeSessionId;
    }

    /// <inheritdoc/>
    public void SetActiveSession(string sessionId)
    {
        if (GetContext(sessionId) == null) return;
        lock (_lock) _activeSessionId = sessionId;
        SessionActivated?.Invoke(this, GetSession(sessionId)!);
    }

    /// <inheritdoc/>
    public void ClearSessionHistory(string sessionId, bool keepSystemPrompt = true)
    {
        var context = GetContext(sessionId);
        context.Conversation.ClearHistory(keepSystemPrompt);
        context.Session.MessageCount = context.Conversation.MessageCount;
    }

    private SessionContext GetContext(string sessionId)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(sessionId, out var context))
                throw new InvalidOperationException($"Session '{sessionId}' not found.");
            return context;
        }
    }

    /// <summary>
    /// 封装单个会话的运行时上下文，包含会话信息、对话管理器和LLM配置。
    /// </summary>
    /// <remarks>
    /// 此类为私有密封类，仅供 <see cref="SessionManager"/> 内部使用，
    /// 实现 <see cref="IDisposable"/> 以确保对话管理器资源的正确释放。
    /// </remarks>
    private sealed class SessionContext : IDisposable
    {
        /// <summary>
        /// 获取与此上下文关联的会话信息对象。
        /// </summary>
        public SessionInfo Session { get; }

        /// <summary>
        /// 获取管理此会话对话上下文的对话管理器实例。
        /// </summary>
        public ConversationManager Conversation { get; }

        /// <summary>
        /// 获取创建此会话时使用的LLM配置。
        /// </summary>
        public LLMConfiguration Configuration { get; }

        /// <summary>
        /// 初始化 <see cref="SessionContext"/> 类的新实例。
        /// </summary>
        /// <param name="session">会话信息对象。</param>
        /// <param name="conversation">对话管理器实例。</param>
        /// <param name="config">LLM配置对象。</param>
        public SessionContext(SessionInfo session, ConversationManager conversation, LLMConfiguration config)
        {
            Session = session;
            Conversation = conversation;
            Configuration = config;
        }

        /// <summary>
        /// 释放此上下文所持有的对话管理器资源。
        /// </summary>
        public void Dispose()
        {
            Conversation.Dispose();
        }
    }
}
