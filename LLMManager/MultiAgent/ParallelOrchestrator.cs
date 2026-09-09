using System.Diagnostics;
using LLMManager.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LLMManager.MultiAgent;

/// <summary>
/// 并行模式的智能体编排器，同时调度所有 Agent 独立处理同一任务，并可配置聚合 Agent 整合各专家的输出结果。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ParallelOrchestrator"/> 将同一任务同时分发给所有注册的 Agent 并行处理，
/// 利用 <c>Task.WhenAll</c> 实现真正的并行执行。
/// </para>
/// <para>
/// 所有提示词模板通过注入的 <see cref="IOrchestrationPromptBuilder"/> 提供，
/// 编排器本身不包含任何硬编码的领域文本。具体 Agent 完全由应用层定义。
/// </para>
/// </remarks>
public class ParallelOrchestrator : IAgentOrchestrator
{
    private readonly LLMClientFactory _factory;
    private readonly List<AgentDefinition> _agents;
    private readonly AgentDefinition? _aggregatorAgent;
    private readonly IOrchestrationPromptBuilder _promptBuilder;
    private readonly ILogger<ParallelOrchestrator>? _logger;

    /// <summary>
    /// 初始化 <see cref="ParallelOrchestrator"/> 类的新实例。
    /// </summary>
    /// <param name="factory">用于创建各 Agent 的 LLM 客户端的工厂实例。</param>
    /// <param name="agents">参与并行执行的专家 Agent 定义集合。</param>
    /// <param name="aggregatorAgent">可选的聚合/领导 Agent，用于整合各专家的输出结果。</param>
    /// <param name="promptBuilder">
    /// 提示词构建策略。若为 <c>null</c> 则自动使用 <see cref="DefaultOrchestrationPromptBuilder"/>。
    /// </param>
    /// <param name="logger">可选的日志记录器实例。</param>
    public ParallelOrchestrator(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> agents,
        AgentDefinition? aggregatorAgent = null,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<ParallelOrchestrator>? logger = null)
    {
        _factory = factory;
        _agents = agents.ToList();
        _aggregatorAgent = aggregatorAgent;
        _promptBuilder = promptBuilder ?? new DefaultOrchestrationPromptBuilder();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<OrchestrationResult> RunAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        if (_agents.Count == 0)
            return new OrchestrationResult { Success = false, Error = "No agents configured." };

        var result = new OrchestrationResult();
        var sw = Stopwatch.StartNew();

        try
        {
            _logger?.LogDebug("Parallel: Dispatching to {Count} agents", _agents.Count);

            var tasks = _agents.Select(async agent =>
            {
                var config = agent.Configuration
                    ?? throw new InvalidOperationException($"Agent '{agent.Name}' has no LLMConfiguration.");

                var client = _factory.Create(config);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, agent.SystemPrompt),
                    new(ChatRole.User, task)
                };

                var options = new ChatOptions { MaxOutputTokens = agent.MaxReplyTokens };
                if (agent.ToolManager?.Tools.Count > 0)
                    options.Tools = agent.ToolManager.GetAITools();

                var response = await client.ChatAsync(messages, options, cancellationToken);
                client.Dispose();

                return (agent.Name, response.Content, response.InputTokens + response.OutputTokens);
            });

            var outputs = await Task.WhenAll(tasks);

            foreach (var (name, content, tokens) in outputs)
            {
                result.AgentOutputs[name] = content;
                result.TotalTokens += tokens;

                result.Messages.Add(new AgentMessage
                {
                    FromAgent = name,
                    ToAgent = _aggregatorAgent?.Name ?? "user",
                    Content = content
                });
            }

            if (_aggregatorAgent != null)
            {
                _logger?.LogDebug("Parallel: Running aggregator agent '{Name}'", _aggregatorAgent.Name);

                var aggConfig = _aggregatorAgent.Configuration
                    ?? throw new InvalidOperationException($"Aggregator agent '{_aggregatorAgent.Name}' has no LLMConfiguration.");

                var aggClient = _factory.Create(aggConfig);

                var aggregatedInput = _promptBuilder.BuildParallelAggregationInput(task, result.AgentOutputs);

                var aggMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, _aggregatorAgent.SystemPrompt),
                    new(ChatRole.User, aggregatedInput)
                };

                var aggResponse = await aggClient.ChatAsync(aggMessages, null, cancellationToken);
                result.FinalOutput = aggResponse.Content;
                result.AgentOutputs[_aggregatorAgent.Name] = aggResponse.Content;
                result.TotalTokens += aggResponse.InputTokens + aggResponse.OutputTokens;

                result.Messages.Add(new AgentMessage
                {
                    FromAgent = _aggregatorAgent.Name,
                    ToAgent = "user",
                    Content = aggResponse.Content
                });

                aggClient.Dispose();
            }
            else
            {
                result.FinalOutput = _promptBuilder.BuildParallelFallbackOutput(result.AgentOutputs);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Parallel orchestration failed");
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }
}
