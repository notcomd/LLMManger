// ============================================================================
// 文件：SequentialOrchestrator.cs
// 用途：顺序模式的智能体编排器，按列表顺序依次驱动每个 Agent 执行任务，并将前一阶段的输出作为后一阶段的上下文传递。
// 实现方：SequentialOrchestrator
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 顺序模式的智能体编排器，按列表顺序依次驱动每个 Agent 执行任务，并将前一阶段的输出作为后一阶段的上下文传递。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SequentialOrchestrator"/> 实现了管道式的任务处理流程。每个 Agent 接收原始任务（第一个 Agent）
/// 或前一个 Agent 的输出作为输入，处理后产生新的输出传递给下一个 Agent。
/// </para>
/// <para>
/// 此编排器不包含任何硬编码的领域相关文本。所有提示词模板均通过构造函数注入的
/// <see cref="IOrchestrationPromptBuilder"/> 提供，默认使用 <see cref="DefaultOrchestrationPromptBuilder"/>。
/// 具体的 Agent 定义（名称、角色、系统提示词等）完全由应用层通过 <see cref="AgentDefinition"/> 提供。
/// </para>
/// </remarks>
public class SequentialOrchestrator : IAgentOrchestrator
{
    private readonly LLMClientFactory _factory;
    private readonly List<AgentDefinition> _agents;
    private readonly IOrchestrationPromptBuilder _promptBuilder;
    private readonly ILogger<SequentialOrchestrator>? _logger;

    /// <summary>
    /// 初始化 <see cref="SequentialOrchestrator"/> 类的新实例。
    /// </summary>
    /// <param name="factory">用于创建各 Agent 的 LLM 客户端的工厂实例。</param>
    /// <param name="agents">参与顺序执行的 Agent 定义集合。</param>
    /// <param name="promptBuilder">
    /// 提示词构建策略。若为 <c>null</c> 则自动使用 <see cref="DefaultOrchestrationPromptBuilder"/>。
    /// </param>
    /// <param name="logger">可选的日志记录器实例。</param>
    public SequentialOrchestrator(
        LLMClientFactory factory,
        IEnumerable<AgentDefinition> agents,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<SequentialOrchestrator>? logger = null)
    {
        _factory = factory;
        _agents = agents.ToList();
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
        var accumulatedContext = new List<ChatMessage>();

        try
        {
            for (int i = 0; i < _agents.Count; i++)
            {
                var agent = _agents[i];
                var config = agent.Configuration
                    ?? throw new InvalidOperationException($"Agent '{agent.Name}' has no LLMConfiguration.");

                using var client = _factory.Create(config);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, agent.SystemPrompt)
                };

                if (i == 0)
                {
                    messages.Add(new ChatMessage(ChatRole.User, task));
                }
                else
                {
                    var contextText = _promptBuilder.BuildSequentialContext(
                        task, _agents[i - 1].Name, accumulatedContext.Last().Text!);
                    messages.Add(new ChatMessage(ChatRole.User, contextText));
                }

                var options = new ChatOptions { MaxOutputTokens = agent.MaxReplyTokens };
                if (agent.ToolManager?.Tools.Count > 0)
                    options.Tools = agent.ToolManager.GetAITools();

                _logger?.LogDebug("Sequential: Agent '{Name}' starting (step {Step}/{Total})",
                    agent.Name, i + 1, _agents.Count);

                var response = await client.ChatAsync(messages, options, cancellationToken);

                var assistantMsg = new ChatMessage(ChatRole.Assistant, response.Content);
                accumulatedContext.Add(assistantMsg);

                result.Messages.Add(new AgentMessage
                {
                    FromAgent = agent.Name,
                    ToAgent = i < _agents.Count - 1 ? _agents[i + 1].Name : "user",
                    Content = response.Content
                });
                result.AgentOutputs[agent.Name] = response.Content;
                result.TotalTokens += response.InputTokens + response.OutputTokens;

            }

            result.FinalOutput = accumulatedContext.Last()?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Sequential orchestration failed");
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }
}
