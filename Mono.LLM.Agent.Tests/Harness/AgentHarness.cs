using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.MultiAgent;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 管理测试中的 Agent 定义与它们的模型脚本，并产出可直接交给编排器使用的客户端工厂。
/// </summary>
/// <remarks>
/// <para>
/// 以前每个测试都要自己写一个 <c>LLMClientFactory</c> 的派生类再按关键词分派回复，
/// 逻辑散落且重复。这里把「Agent 身份」「模型行为」「调用记录」收拢到一处：
/// </para>
/// <code>
/// var architect = sandbox.Agents.Script("架构师")
///     .WhenContains("拆解", planJson)
///     .Fallback("已确认");
///
/// var team = new AgentTeam(sandbox.Agents.CreateFactory())
///     .AddMembers(new[] { sandbox.Agents["架构师"] });
/// </code>
/// <para>
/// 线程安全：团队模式会并行调用，脚本与字典都做了保护。
/// </para>
/// </remarks>
public sealed class AgentHarness
{
    private readonly Dictionary<string, ModelScript> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>
    /// 取得（必要时创建）指定 Agent 的模型脚本。
    /// </summary>
    /// <param name="name">Agent 名称，同时作为模型标识。</param>
    /// <returns>该 Agent 的脚本，可继续链式配置规则。</returns>
    public ModelScript Script(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (!_scripts.TryGetValue(name, out var script))
            {
                script = new ModelScript(name);
                _scripts[name] = script;
            }

            if (!_agents.ContainsKey(name))
                _agents[name] = CreateAgent(name, role: "成员", systemPrompt: $"你是{name}。");

            return script;
        }
    }

    /// <summary>
    /// 定义一个 Agent 并返回其定义对象。
    /// </summary>
    /// <param name="name">Agent 名称，同时作为模型标识。</param>
    /// <param name="role">角色描述。</param>
    /// <param name="systemPrompt">系统提示词，省略时自动生成。</param>
    /// <param name="description">能力描述，供路由与团队规划使用。</param>
    /// <returns>可用于 <c>AgentTeam.AddMember</c> 的定义对象。</returns>
    public AgentDefinition Define(
        string name,
        string role = "成员",
        string? systemPrompt = null,
        string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            var agent = CreateAgent(name, role, systemPrompt ?? $"你是{name}。", description);
            _agents[name] = agent;

            if (!_scripts.ContainsKey(name))
                _scripts[name] = new ModelScript(name);

            return agent;
        }
    }

    /// <summary>按名称获取已定义的 Agent。</summary>
    /// <param name="name">Agent 名称。</param>
    /// <exception cref="KeyNotFoundException">当该 Agent 尚未定义时抛出。</exception>
    public AgentDefinition this[string name]
    {
        get
        {
            lock (_gate)
            {
                if (_agents.TryGetValue(name, out var agent))
                    return agent;
            }

            throw new KeyNotFoundException(
                $"Agent '{name}' 未定义。请先调用 Define 或 Script 创建它。");
        }
    }

    /// <summary>获取全部已定义的 Agent。</summary>
    public IReadOnlyList<AgentDefinition> All
    {
        get
        {
            lock (_gate)
                return _agents.Values.ToList();
        }
    }

    /// <summary>获取某个 Agent 的脚本；未定义时返回 <c>null</c>。</summary>
    public ModelScript? ScriptOrNull(string name)
    {
        lock (_gate)
            return _scripts.TryGetValue(name, out var script) ? script : null;
    }

    /// <summary>按发生顺序汇总所有 Agent 的调用记录。</summary>
    public IReadOnlyList<ModelCall> AllCalls =>
        All
            .Select(a => ScriptOrNull(a.Name))
            .Where(s => s != null)
            .SelectMany(s => s!.Calls)
            .OrderBy(c => c.At)
            .ToList();

    /// <summary>全部 Agent 的累计调用次数。</summary>
    public int TotalCalls
    {
        get
        {
            lock (_gate)
                return _scripts.Values.Sum(s => s.CallCount);
        }
    }

    /// <summary>创建一个把调用转发到本 harness 的客户端工厂。</summary>
    /// <returns>可直接传给编排器、技能执行器或会话管理器的工厂实例。</returns>
    public LLMClientFactory CreateFactory() => new HarnessFactory(this);

    internal ModelScript Resolve(string modelId)
    {
        lock (_gate)
        {
            if (_scripts.TryGetValue(modelId, out var script))
                return script;

            // 未显式脚本化的模型给一个空脚本，便于发现"意外多出来的调用"。
            script = new ModelScript(modelId);
            _scripts[modelId] = script;
            return script;
        }
    }

    internal void RegisterAgent(string name, AgentDefinition agent)
    {
        lock (_gate)
            _agents[name] = agent;
    }

    private static AgentDefinition CreateAgent(
        string name,
        string role,
        string systemPrompt,
        string description = "") => new()
    {
        Name = name,
        Role = role,
        Description = description,
        SystemPrompt = systemPrompt,
        Configuration = new LLMConfiguration
        {
            ProviderType = LLMProviderType.Ollama,
            ModelId = name,
            Endpoint = "http://localhost:11434",
            MaxRetryCount = 0
        }
    };
}

/// <summary>
/// 把客户端创建请求路由到 <see cref="AgentHarness"/> 的工厂。
/// </summary>
internal sealed class HarnessFactory : LLMClientFactory
{
    private readonly AgentHarness _harness;

    public HarnessFactory(AgentHarness harness) => _harness = harness;

    public override ILLMClient Create(LLMConfiguration configuration)
        => new HarnessClient(_harness.Resolve(configuration.ModelId), configuration);
}

/// <summary>
/// 由 <see cref="ModelScript"/> 驱动的 <see cref="ILLMClient"/> 实现。
/// </summary>
internal sealed class HarnessClient : ILLMClient
{
    private readonly ModelScript _script;

    public HarnessClient(ModelScript script, LLMConfiguration configuration)
    {
        _script = script;
        Configuration = configuration;
    }

    public LLMConfiguration Configuration { get; }

    public IChatClient InnerClient { get; } = new NullChatClient();

    public async Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (system, user, transcript) = Split(messages);
        var reply = _script.Respond(system, user, transcript);

        if (reply.Delay > TimeSpan.Zero)
            await Task.Delay(reply.Delay, cancellationToken).ConfigureAwait(false);

        var contents = BuildContents(reply);

        return new ChatResponse
        {
            Content = reply.Text,
            RawMessage = BuildMessage(contents),
            ModelId = Configuration.ModelId,
            InputTokens = EstimateTokens(system + user),
            OutputTokens = EstimateTokens(reply.Text)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (system, user, transcript) = Split(messages);
        var reply = _script.Respond(system, user, transcript);

        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, BuildContents(reply));
    }

    private static List<AIContent> BuildContents(ScriptedReply reply)
    {
        var contents = new List<AIContent>(1 + (reply.ToolCalls?.Count ?? 0));

        if (!string.IsNullOrEmpty(reply.Text))
            contents.Add(new TextContent(reply.Text));

        if (reply.ToolCalls != null)
        {
            for (int i = 0; i < reply.ToolCalls.Count; i++)
            {
                var call = reply.ToolCalls[i];
                contents.Add(new FunctionCallContent(
                    $"call-{i + 1}",
                    call.Name,
                    new Dictionary<string, object?>(call.Arguments)));
            }
        }

        // 既无文本也无工具调用时给一个空文本，避免构造出空内容消息。
        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));

        return contents;
    }

    private static ChatMessage BuildMessage(List<AIContent> contents)
        => new(ChatRole.Assistant, contents);

    public Task<Embedding<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(new Embedding<float>(new[] { 1f, 0f }));

    public Task<IList<Embedding<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IList<Embedding<float>>>(
            texts.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList());

    public void Dispose() { }

    private static (string System, string User, string Transcript) Split(IEnumerable<ChatMessage> messages)
    {
        var system = string.Empty;
        var user = string.Empty;
        var transcript = new System.Text.StringBuilder();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                system = message.Text ?? string.Empty;
                continue;
            }

            if (message.Role == ChatRole.User)
                user = message.Text ?? string.Empty;

            transcript.AppendLine(message.Text);

            // 工具结果不体现在 Text 上，需要单独取出，否则多轮脚本无法区分阶段。
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
                transcript.AppendLine(result.Result?.ToString());
        }

        return (system, user, transcript.ToString());
    }

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length / 4) + 1;
}

/// <summary>不承载任何功能的 <see cref="IChatClient"/>，仅用于满足接口契约。</summary>
internal sealed class NullChatClient : IChatClient
{
    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Harness 客户端不直接暴露 IChatClient 调用。");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Harness 客户端不直接暴露 IChatClient 调用。");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
