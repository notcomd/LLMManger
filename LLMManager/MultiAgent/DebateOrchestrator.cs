using System.Diagnostics;
using LLMManager.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LLMManager.MultiAgent;

/// <summary>
/// 辩论模式的智能体编排器，组织多个辩手 Agent 围绕指定主题进行多轮辩论，最终可由裁判 Agent 进行总结评判。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DebateOrchestrator"/> 实现了模拟多方辩论的协作模式。每个辩手 Agent 在每轮中依次发言，
/// 能够看到之前所有发言的内容，从而形成有来有往的辩论过程。
/// </para>
/// <para>
/// 所有辩论相关的提示词模板（发言引导、记录格式、裁判指令等）均通过
/// 注入的 <see cref="IOrchestrationPromptBuilder"/> 提供，编排器不包含任何硬编码的领域文本。
/// 具体的辩手和裁判 Agent 完全由应用层通过 <see cref="AgentDefinition"/> 定义。
/// </para>
/// </remarks>
public class DebateOrchestrator : IAgentOrchestrator
{
    private readonly LLMClientFactory _factory;
    private readonly List<AgentDefinition> _debaters;
    private readonly AgentDefinition? _judgeAgent;
    private readonly int _maxRounds;
    private readonly IOrchestrationPromptBuilder _promptBuilder;
    private readonly ILogger<DebateOrchestrator>? _logger;

    /// <summary>
    /// 初始化 <see cref="DebateOrchestrator"/> 类的新实例。
    /// </summary>
    /// <param name="factory">用于创建各 Agent 的 LLM 客户端的工厂实例。</param>
    /// <param name="debaters">参与辩论的辩手 Agent 定义集合，至少需要 2 个。</param>
    /// <param name="judgeAgent">可选的裁判 Agent 定义，用于在辩论结束后进行总结和评判。</param>
    /// <param name="maxRounds">辩论的总轮次数，默认为 3。</param>
    /// <param name="promptBuilder">
    /// 提示词构建策略。若为 <c>null</c> 则自动使用 <see cref="DefaultOrchestrationPromptBuilder"/>。
    /// </param>
    /// <param name="logger">可选的日志记录器实例。</param>
    public DebateOrchestrator(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> debaters,
        AgentDefinition? judgeAgent = null,
        int maxRounds = 3,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<DebateOrchestrator>? logger = null)
    {
        _factory = factory;
        _debaters = debaters.ToList();
        _judgeAgent = judgeAgent;
        _maxRounds = maxRounds;
        _promptBuilder = promptBuilder ?? new DefaultOrchestrationPromptBuilder();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<OrchestrationResult> RunAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        if (_debaters.Count < 2)
            return new OrchestrationResult { Success = false, Error = "Debate requires at least 2 debaters." };

        var result = new OrchestrationResult();
        var sw = Stopwatch.StartNew();
        var transcript = new System.Text.StringBuilder();

        try
        {
            transcript.AppendLine(_promptBuilder.BuildDebateHeader(task));
            transcript.AppendLine();

            for (int round = 1; round <= _maxRounds; round++)
            {
                _logger?.LogDebug("Debate: Round {Round}/{MaxRounds}", round, _maxRounds);

                foreach (var debater in _debaters)
                {
                    var config = debater.Configuration
                        ?? throw new InvalidOperationException($"Agent '{debater.Name}' has no LLMConfiguration.");

                    var client = _factory.Create(config);

                    var context = _promptBuilder.BuildDebateTurnContext(
                        task, round, debater.Name, debater.Role, transcript.ToString());

                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, debater.SystemPrompt),
                        new(ChatRole.User, context)
                    };

                    var options = new ChatOptions { MaxOutputTokens = debater.MaxReplyTokens };
                    if (debater.ToolManager?.Tools.Count > 0)
                        options.Tools = debater.ToolManager.GetAITools();

                    _logger?.LogDebug("Debate: {Agent} speaking (round {Round})", debater.Name, round);

                    var response = await client.ChatAsync(messages, options, cancellationToken);

                    var entry = _promptBuilder.FormatDebateEntry(round, debater.Name, response.Content);
                    transcript.AppendLine(entry);
                    transcript.AppendLine();

                    result.AgentOutputs[$"{debater.Name}_R{round}"] = response.Content;
                    result.TotalTokens += response.InputTokens + response.OutputTokens;

                    result.Messages.Add(new AgentMessage
                    {
                        FromAgent = debater.Name,
                        ToAgent = "all",
                        Content = response.Content,
                        Metadata = { ["Round"] = round.ToString() }
                    });

                    client.Dispose();
                }
            }

            if (_judgeAgent != null)
            {
                _logger?.LogDebug("Debate: Judge '{Name}' summarizing", _judgeAgent.Name);

                var judgeConfig = _judgeAgent.Configuration
                    ?? throw new InvalidOperationException($"Judge agent '{_judgeAgent.Name}' has no LLMConfiguration.");

                var judgeClient = _factory.Create(judgeConfig);

                var judgeContext = _promptBuilder.BuildJudgeContext(task, transcript.ToString());

                var judgeMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, _judgeAgent.SystemPrompt),
                    new(ChatRole.User, judgeContext)
                };

                var judgeResponse = await judgeClient.ChatAsync(judgeMessages, null, cancellationToken);
                result.FinalOutput = judgeResponse.Content;
                result.AgentOutputs[_judgeAgent.Name] = judgeResponse.Content;
                result.TotalTokens += judgeResponse.InputTokens + judgeResponse.OutputTokens;

                result.Messages.Add(new AgentMessage
                {
                    FromAgent = _judgeAgent.Name,
                    ToAgent = "user",
                    Content = judgeResponse.Content
                });

                judgeClient.Dispose();
            }
            else
            {
                result.FinalOutput = transcript.ToString();
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Debate orchestration failed");
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }
}
