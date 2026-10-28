// ============================================================================
// 文件：RouterOrchestrator.cs
// 用途：路由模式的智能体编排器，使用专用的路由 Agent 分析任务并将其分配给最合适的专家 Agent 处理。
// 实现方：RouterOrchestrator
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using Mono.LLM.Agent.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 路由模式的智能体编排器，使用专用的路由 Agent 分析任务并将其分配给最合适的专家 Agent 处理。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RouterOrchestrator"/> 实现了基于 LLM 驱动的智能路由机制，整个流程分为两个阶段：
/// 路由阶段（由路由 Agent 分析并选择专家）和执行阶段（由被选中的专家处理任务）。
/// </para>
/// <para>
/// 路由提示词增强格式和分配结果解析逻辑均通过注入的 <see cref="IOrchestrationPromptBuilder"/> 提供，
/// 编排器本身不硬编码任何路由协议或领域文本。应用层可实现自定义路由协议（如 JSON 格式、XML 格式等）。
/// </para>
/// <para>
/// 如果路由 Agent 无法确定合适的专家（输出中不包含有效的分配标记），
/// 编排器将默认选择专家列表中的第一个 Agent 作为回退方案。
/// </para>
/// </remarks>
public class RouterOrchestrator : IAgentOrchestrator
{
    private readonly LLMClientFactory _factory;
    private readonly AgentDefinition _routerAgent;
    private readonly Dictionary<string, AgentDefinition> _specialists;
    private readonly IOrchestrationPromptBuilder _promptBuilder;
    private readonly ILogger<RouterOrchestrator>? _logger;

    /// <summary>
    /// 初始化 <see cref="RouterOrchestrator"/> 类的新实例。
    /// </summary>
    /// <param name="factory">用于创建各 Agent 的 LLM 客户端的工厂实例。</param>
    /// <param name="routerAgent">负责分析任务并进行路由分配的路由 Agent 定义。</param>
    /// <param name="specialists">可供选择的专家 Agent 定义集合，路由 Agent 从中选择目标。</param>
    /// <param name="promptBuilder">
    /// 提示词构建策略。若为 <c>null</c> 则自动使用 <see cref="DefaultOrchestrationPromptBuilder"/>。
    /// </param>
    /// <param name="logger">可选的日志记录器实例。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="routerAgent"/> 为 <c>null</c> 时抛出。</exception>
    public RouterOrchestrator(
        LLMClientFactory factory,
        AgentDefinition routerAgent,
        IEnumerable<AgentDefinition> specialists,
        IOrchestrationPromptBuilder? promptBuilder = null,
        ILogger<RouterOrchestrator>? logger = null)
    {
        _factory = factory;
        _routerAgent = routerAgent;
        _specialists = specialists.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _promptBuilder = promptBuilder ?? new DefaultOrchestrationPromptBuilder();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<OrchestrationResult> RunAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        if (_specialists.Count == 0)
            return new OrchestrationResult { Success = false, Error = "No specialist agents configured." };

        var result = new OrchestrationResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var specialistList = _specialists.Values
                .Select(s => $"- {s.Name}: {s.Role} ({s.Description})");

            var routingPrompt = _promptBuilder.BuildRoutingSystemPrompt(
                _routerAgent.SystemPrompt, specialistList);

            var routerConfig = _routerAgent.Configuration
                ?? throw new InvalidOperationException($"Router agent '{_routerAgent.Name}' has no LLMConfiguration.");

            using var routerClient = _factory.Create(routerConfig);

            var routingMessages = new List<ChatMessage>
            {
                new(ChatRole.System, routingPrompt),
                new(ChatRole.User, task)
            };

            _logger?.LogDebug("Router: Analyzing task for routing");

            var routingResponse = await routerClient.ChatAsync(routingMessages, null, cancellationToken);
            result.AgentOutputs[$"{_routerAgent.Name}_routing"] = routingResponse.Content;
            result.TotalTokens += routingResponse.InputTokens + routingResponse.OutputTokens;

            var assignedAgent = _promptBuilder.ParseRoutingAssignment(routingResponse.Content);

            AgentDefinition selectedSpecialist;
            if (assignedAgent != null && _specialists.TryGetValue(assignedAgent, out var specialist))
            {
                selectedSpecialist = specialist;
                _logger?.LogInformation("Router: Task assigned to '{Name}'", selectedSpecialist.Name);
            }
            else
            {
                selectedSpecialist = _specialists.Values.First();
                _logger?.LogWarning("Router: Could not determine specialist, defaulting to '{Name}'", selectedSpecialist.Name);
            }

            result.Messages.Add(new AgentMessage
            {
                FromAgent = _routerAgent.Name,
                ToAgent = selectedSpecialist.Name,
                Content = routingResponse.Content
            });


            var specialistConfig = selectedSpecialist.Configuration
                ?? throw new InvalidOperationException($"Specialist '{selectedSpecialist.Name}' has no LLMConfiguration.");

            using var specialistClient = _factory.Create(specialistConfig);

            var specialistMessages = new List<ChatMessage>
            {
                new(ChatRole.System, selectedSpecialist.SystemPrompt),
                new(ChatRole.User, task)
            };

            var options = new ChatOptions { MaxOutputTokens = selectedSpecialist.MaxReplyTokens };
            if (selectedSpecialist.ToolManager?.Tools.Count > 0)
                options.Tools = selectedSpecialist.ToolManager.GetAITools();

            _logger?.LogDebug("Router: Delegating to specialist '{Name}'", selectedSpecialist.Name);

            var specialistResponse = await specialistClient.ChatAsync(specialistMessages, options, cancellationToken);
            result.FinalOutput = specialistResponse.Content;
            result.AgentOutputs[selectedSpecialist.Name] = specialistResponse.Content;
            result.TotalTokens += specialistResponse.InputTokens + specialistResponse.OutputTokens;

            result.Messages.Add(new AgentMessage
            {
                FromAgent = selectedSpecialist.Name,
                ToAgent = "user",
                Content = specialistResponse.Content
            });

        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Router orchestration failed");
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }
}
