namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 一次模型调用的完整记录，用于断言调用次数、顺序与提示词内容。
/// </summary>
/// <param name="Agent">被调用的 Agent 名称。</param>
/// <param name="Index">该 Agent 的第几次调用，从 1 开始。</param>
/// <param name="SystemPrompt">调用时使用的系统提示词。</param>
/// <param name="UserMessage">调用时传入的用户消息。</param>
/// <param name="Reply">返回给调用方的内容。</param>
/// <param name="MatchedRule">命中的规则序号，未命中任何规则时为 -1。</param>
/// <param name="At">调用发生的时间，用于跨 Agent 排序。</param>
/// <param name="ToolCalls">本轮要求的工具调用名称。</param>
public sealed record ModelCall(
    string Agent,
    int Index,
    string SystemPrompt,
    string UserMessage,
    string Reply,
    int MatchedRule,
    DateTimeOffset At,
    IReadOnlyList<string> ToolCalls);

/// <summary>
/// 脚本化的模型回复：文本，或文本加一组工具调用。
/// </summary>
/// <param name="Text">面向用户的文本，可为空。</param>
/// <param name="ToolCalls">本轮要求模型调用的工具；为空表示普通文本回复。</param>
/// <param name="Delay">
/// 返回前的模拟耗时。并发场景下用它让多个任务确定性地重叠，
/// 避免断言因线程调度而随机通过或失败。
/// </param>
public sealed record ScriptedReply(
    string Text,
    IReadOnlyList<ScriptedToolCall>? ToolCalls = null,
    TimeSpan Delay = default)
{
    /// <summary>构造一个纯文本回复。</summary>
    public static ScriptedReply Say(string text, TimeSpan delay = default) => new(text, null, delay);

    /// <summary>
    /// 构造一个工具调用回复。
    /// </summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="text">同时输出的文本，默认空。</param>
    public static ScriptedReply Call(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string text = "",
        TimeSpan delay = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return new ScriptedReply(
            text,
            [new ScriptedToolCall(toolName, arguments ?? new Dictionary<string, object?>())],
            delay);
    }
}

/// <summary>一次脚本化的工具调用。</summary>
/// <param name="Name">工具名称。</param>
/// <param name="Arguments">工具参数。</param>
public sealed record ScriptedToolCall(string Name, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// 单个 Agent 的模型脚本：按提示词规则回放固定内容，并记录每次调用。
/// </summary>
/// <remarks>
/// <para>
/// 规则按添加顺序匹配，第一个命中者生效。所有匹配都是对<b>用户消息</b>做的，
/// 因为编排器把任务与上下文都放在 user 消息里，system 提示词是角色的固定设定。
/// </para>
/// <para>
/// 线程安全：团队模式并行执行时会被并发调用。
/// </para>
/// </remarks>
public sealed class ModelScript
{
    private readonly List<(Func<string, bool> Matches, ScriptedReply Reply, string Description)> _rules = new();
    private readonly Queue<ScriptedReply> _sequence = new();
    private readonly List<ModelCall> _calls = new();
    private readonly object _gate = new();

    internal ModelScript(string agentName)
    {
        AgentName = agentName;
    }

    /// <summary>该脚本所属的 Agent 名称。</summary>
    public string AgentName { get; }

    /// <summary>没有任何规则命中时返回的内容。</summary>
    public ScriptedReply FallbackReply { get; private set; } = ScriptedReply.Say("（默认回复：未配置匹配规则）");

    /// <summary>已发生的调用记录，按发生顺序排列。</summary>
    public IReadOnlyList<ModelCall> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToList();
        }
    }

    /// <summary>已发生的调用次数。</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
                return _calls.Count;
        }
    }

    /// <summary>按顺序取出所有调用中的用户消息，便于断言提示词内容。</summary>
    public IReadOnlyList<string> UserMessages => Calls.Select(c => c.UserMessage).ToList();

    /// <summary>
    /// 当用户消息包含指定片段时，返回给定内容。
    /// </summary>
    /// <param name="token">用于匹配的片段，忽略大小写。</param>
    /// <param name="reply">命中后返回的内容。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="token"/> 为空时抛出。</exception>
    public ModelScript WhenContains(string token, string reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(reply);

        return When(
            message => message.Contains(token, StringComparison.OrdinalIgnoreCase),
            ScriptedReply.Say(reply),
            $"包含「{token}」");
    }

    /// <summary>
    /// 当用户消息满足自定义条件时，返回给定内容。
    /// </summary>
    /// <param name="predicate">判断条件。</param>
    /// <param name="reply">命中后返回的内容。</param>
    /// <param name="description">用于诊断的描述，可省略。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript When(Func<string, bool> predicate, string reply, string? description = null)
        => When(predicate, ScriptedReply.Say(reply), description);

    /// <summary>
    /// 当用户消息包含指定片段时，返回结构化的脚本回复（可携带工具调用）。
    /// </summary>
    /// <param name="token">用于匹配的片段，忽略大小写。</param>
    /// <param name="reply">命中后返回的回复。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript WhenContains(string token, ScriptedReply reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(reply);

        return When(
            message => message.Contains(token, StringComparison.OrdinalIgnoreCase),
            reply,
            $"包含「{token}」");
    }

    /// <summary>
    /// 当用户消息包含指定片段时，要求模型发起一次工具调用。
    /// </summary>
    /// <param name="token">用于匹配的片段，忽略大小写。</param>
    /// <param name="toolName">要调用的工具名称。</param>
    /// <param name="arguments">工具参数。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript WhenContainsToolCall(
        string token,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null)
        => WhenContains(token, ScriptedReply.Call(toolName, arguments));

    /// <summary>
    /// 当用户消息满足自定义条件时，返回结构化回复。
    /// </summary>
    /// <param name="predicate">判断条件。</param>
    /// <param name="reply">命中后返回的回复。</param>
    /// <param name="description">用于诊断的描述。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript When(Func<string, bool> predicate, ScriptedReply reply, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(reply);

        lock (_gate)
        {
            _rules.Add((predicate, reply, description ?? $"规则 #{_rules.Count + 1}"));
        }

        return this;
    }

    /// <summary>
    /// 设置没有任何规则命中时的兜底回复。
    /// </summary>
    /// <param name="reply">兜底内容。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript Fallback(string reply) => Fallback(ScriptedReply.Say(reply));

    /// <summary>
    /// 设置没有任何规则命中时的兜底回复。
    /// </summary>
    /// <param name="reply">兜底回复。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    public ModelScript Fallback(ScriptedReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        lock (_gate)
            FallbackReply = reply;

        return this;
    }

    /// <summary>
    /// 按调用次序返回不同回复，优先于规则匹配。
    /// </summary>
    /// <param name="replies">依次使用的回复，用尽后回落到规则匹配。</param>
    /// <returns>当前脚本，支持链式配置。</returns>
    /// <remarks>
    /// 适合「第一次评审不通过、第二次通过」这类依赖调用次数的场景。
    /// </remarks>
    public ModelScript Sequence(params ScriptedReply[] replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        lock (_gate)
        {
            foreach (var reply in replies)
                _sequence.Enqueue(reply);
        }

        return this;
    }

    /// <summary>清空调用记录，保留规则配置。</summary>
    public void ClearCalls()
    {
        lock (_gate)
            _calls.Clear();
    }

    /// <summary>
    /// 生成回复。
    /// </summary>
    /// <param name="systemPrompt">系统提示词。</param>
    /// <param name="userMessage">最后一条用户消息，用于记录。</param>
    /// <param name="matchingText">
    /// 用于规则匹配的完整上下文。多轮场景下它包含此前的工具结果，
    /// 使脚本能区分「第一轮该调工具」和「拿到结果后该给结论」。
    /// </param>
    internal ScriptedReply Respond(string systemPrompt, string userMessage, string? matchingText = null)
    {
        var text = string.IsNullOrEmpty(matchingText) ? userMessage : matchingText;

        ScriptedReply reply;
        int matched = -1;

        lock (_gate)
        {
            if (_sequence.Count > 0)
            {
                var next = _sequence.Dequeue();
                RecordCall(systemPrompt, userMessage, next, -2);
                return next;
            }

            reply = FallbackReply;

            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Matches(text))
                {
                    reply = _rules[i].Reply;
                    matched = i;
                    break;
                }
            }

            RecordCall(systemPrompt, userMessage, reply, matched);
        }

        return reply;
    }

    private void RecordCall(string systemPrompt, string userMessage, ScriptedReply reply, int matchedRule)
    {
        _calls.Add(new ModelCall(
            AgentName,
            _calls.Count + 1,
            systemPrompt,
            userMessage,
            reply.Text,
            matchedRule,
            DateTimeOffset.UtcNow,
            reply.ToolCalls?.Select(c => c.Name).ToList() ?? new List<string>()));
    }

    /// <summary>返回便于断言的脚本摘要。</summary>
    public override string ToString() => $"{AgentName}: rules={_rules.Count}, calls={CallCount}";
}
