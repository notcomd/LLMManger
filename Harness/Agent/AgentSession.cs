using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Streaming;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Harness.Agent;

/// <summary>会话级参数。</summary>
public sealed class AgentSessionOptions
{
    /// <summary>未选择技能时使用的系统提示。</summary>
    public string SystemPrompt { get; set; } =
        "你是一个可以调用工具的助手。需要事实、计算或记忆时先调用工具，再基于结果回答；回答保持简洁。";

    /// <summary>单次回复内允许的工具往返轮次。</summary>
    public int MaxToolRoundtrips { get; set; } = 6;

    /// <summary>上下文 token 上限，超出后裁剪最早的非系统消息。</summary>
    public int ContextTokenLimit { get; set; } = 32_000;

    internal AgentSessionOptions Clone() => new()
    {
        SystemPrompt = SystemPrompt,
        MaxToolRoundtrips = MaxToolRoundtrips,
        ContextTokenLimit = ContextTokenLimit
    };
}

/// <summary>
/// 一次连续会话：持有历史、工具集与技能，支持多轮对话中的工具调用与技能切换。
/// </summary>
/// <remarks>
/// <para>
/// 会话内部只有一份历史。工具往返由 <see cref="StreamingManager"/> 负责：
/// 模型请求工具 → 执行 → 结果回填 → 继续生成，全程写入同一份历史，
/// 因此第二轮提问能看到第一轮的工具结果。
/// </para>
/// <para>
/// 技能的作用是「换系统提示 + 收窄工具集」，不会清空历史：
/// 切换技能后仍记得之前的对话，这更符合连续会话的直觉。
/// </para>
/// </remarks>
public sealed class AgentSession : IDisposable
{
    private readonly AgentSessionOptions _options;
    private readonly ToolManager _allTools;
    private readonly StreamingManager _streaming;
    private readonly SkillRouter _router;

    private ToolManager _activeTools;
    private bool _disposed;

    /// <summary>创建一个会话。</summary>
    /// <param name="factory">客户端工厂。</param>
    /// <param name="configuration">模型配置。</param>
    /// <param name="tools">全量工具集。</param>
    /// <param name="skills">
    /// 技能注册表。为 <c>null</c> 时新建一个并装入内置技能；传入时由调用方负责填充，
    /// 这样调用方可以在创建会话前先把磁盘上的技能加载进去，工具也能读到同一份列表。
    /// </param>
    /// <param name="options">会话参数。</param>
    public AgentSession(
        LLMClientFactory factory,
        LLMConfiguration configuration,
        ToolManager tools,
        ISkillRegistry? skills = null,
        AgentSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tools);

        _options = (options ?? new AgentSessionOptions()).Clone();
        _allTools = tools;

        var client = factory.Create(configuration);
        Configuration = configuration;
        Conversation = new ConversationManager(client, _options.SystemPrompt)
        {
            ContextTokenLimit = _options.ContextTokenLimit
        };

        _streaming = new StreamingManager(client);

        if (skills == null)
        {
            var registry = new SkillRegistry();

            foreach (var skill in SkillCatalog.GetAll())
                registry.Register(skill);

            Skills = registry;
        }
        else
        {
            Skills = skills;
        }

        _activeTools = tools;
        Subscribe(_activeTools);

        // 默认自动挑技能：用户只描述任务，不必先 /use。显式选择过技能时以选择为准。
        _router = new SkillRouter(Skills, new SkillTreeCache(), new SkillRoutingOptions
        {
            MaxSkills = 1,
            MinScore = 0.2
        });
    }

    /// <summary>是否按输入自动加载相关技能。显式切换过技能时不生效。</summary>
    public bool AutoSkills { get; set; } = true;

    /// <summary>自动加载技能时触发，用于在界面上提示本轮命中了什么。</summary>
    public event Action<SkillTurnPlan>? SkillsPlanned;

    /// <summary>本次会话使用的模型配置。</summary>
    public LLMConfiguration Configuration { get; }

    /// <summary>会话历史。</summary>
    public ConversationManager Conversation { get; }

    /// <summary>可用技能。</summary>
    public ISkillRegistry Skills { get; }

    /// <summary>当前生效的技能，未选择时为 <c>null</c>。</summary>
    public SkillDefinition? ActiveSkill { get; private set; }

    /// <summary>当前生效的工具集（可能被技能收窄）。</summary>
    public ToolManager ActiveTools => _activeTools;

    /// <summary>工具执行完成时触发。</summary>
    public event EventHandler<ToolExecutedEventArgs>? ToolExecuted;

    /// <summary>收到思考过程时触发。</summary>
    public event Action<string>? ReasoningReceived;

    /// <summary>
    /// 发送一条用户消息并等待完整回复，期间自动完成工具调用往返。
    /// </summary>
    /// <param name="input">用户输入。</param>
    /// <param name="onToken">正文增量回调，用于流式显示。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮累计的助手正文（不含思考与工具结果）。</returns>
    /// <remarks>
    /// 未显式选择技能时，会按输入自动匹配并加载相关技能（<see cref="AutoSkills"/>），
    /// 因此用户直接描述任务即可，不需要先 <c>/use</c>。
    /// </remarks>
    public async Task<string> SendAsync(
        string input,
        Action<string>? onToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (AutoSkills && ActiveSkill is null)
        {
            var plan = _router.PlanTurn(input, _options.SystemPrompt);
            Conversation.SetSystemPrompt(plan.SystemPrompt);

            if (plan.HasActivation)
                SkillsPlanned?.Invoke(plan);
        }

        Conversation.AddUserMessage(input);
        Conversation.TrimHistory();

        return await _streaming.StreamWithToolCallHandlingAsync(
            Conversation,
            _activeTools,
            onToken,
            options: null,
            maxToolRoundtrips: _options.MaxToolRoundtrips,
            cancellationToken: cancellationToken,
            onReasoning: text => ReasoningReceived?.Invoke(text)).ConfigureAwait(false);
    }

    /// <summary>
    /// 切换到指定技能：替换系统提示，并按 <see cref="SkillDefinition.AllowedTools"/> 收窄工具集。
    /// </summary>
    /// <returns>被激活的技能定义。</returns>
    /// <exception cref="InvalidOperationException">技能不存在时抛出。</exception>
    public SkillDefinition UseSkill(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var skill = Skills.Get(name)
                    ?? throw new InvalidOperationException($"未找到技能「{name}」。用 /skills 查看可用项。");

        ActiveSkill = skill;
        Conversation.SetSystemPrompt(skill.SystemPrompt);

        _activeTools = skill.AllowedTools.Count > 0
            ? _allTools.CreateScoped(skill.AllowedTools)
            : _allTools;

        Subscribe(_activeTools);
        return skill;
    }

    /// <summary>清除当前技能，恢复默认系统提示与全量工具。</summary>
    public void ClearSkill()
    {
        ActiveSkill = null;
        Conversation.SetSystemPrompt(_options.SystemPrompt);
        _activeTools = _allTools;
    }

    /// <summary>清空对话历史，保留当前技能的提示词。</summary>
    public void Reset() => Conversation.ClearHistory(keepSystemPrompt: true);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Conversation.Dispose();
    }

    private void Subscribe(ToolManager tools)
        => tools.ToolExecuted += (sender, args) => ToolExecuted?.Invoke(sender, args);
}
