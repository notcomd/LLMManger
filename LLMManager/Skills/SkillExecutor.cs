using System.Text;
using LLMManager.Conversation;
using LLMManager.Core;
using LLMManager.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatResponse = LLMManager.Core.ChatResponse;

namespace LLMManager.Skills;

/// <summary>
/// 技能执行器，负责创建技能执行上下文并以同步或流式方式执行技能。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillExecutor"/> 是技能系统的核心执行引擎。它接受用户输入，结合技能的系统提示词和可用工具，
/// 通过多轮工具调用循环完成完整的技能执行流程。
/// </para>
/// <para>
/// 执行流程：
/// <list type="number">
///   <item>构建系统消息和用户消息</item>
///   <item>将可用工具附加到请求中</item>
///   <item>调用 LLM 获取响应</item>
///   <item>若响应包含工具调用，依次执行工具并将结果回传给 LLM</item>
///   <item>重复步骤 2-4 直到没有更多工具调用或达到最大轮次限制</item>
/// </list>
/// </para>
/// <para>
/// 支持两种执行模式：<see cref="ExecuteAsync"/>（非流式）和 <see cref="ExecuteStreamingAsync"/>（流式）。
/// </para>
/// </remarks>
public class SkillExecutor
{
    private readonly LLMClientFactory _clientFactory;
    private readonly ILogger<SkillExecutor>? _logger;

    /// <summary>
    /// 初始化 <see cref="SkillExecutor"/> 类的新实例。
    /// </summary>
    /// <param name="clientFactory">用于创建 LLM 客户端的工厂实例。</param>
    /// <param name="logger">可选的日志记录器实例。</param>
    public SkillExecutor(LLMClientFactory clientFactory, ILogger<SkillExecutor>? logger = null)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 创建一个新的技能执行上下文。
    /// </summary>
    /// <param name="skill">要执行的技能定义。</param>
    /// <param name="config">必需的 LLM 配置，不能为 <c>null</c>。</param>
    /// <param name="toolManager">可选的自定义工具管理器。若为 <c>null</c> 则使用空的 <see cref="ToolManager"/>。</param>
    /// <returns>绑定完成的技能执行上下文实例。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="config"/> 为 <c>null</c> 时抛出。</exception>
    /// <remarks>
    /// 此方法根据提供的配置创建 LLM 客户端，初始化带有技能系统提示词的对话管理器，
    /// 并将其与技能定义和工具管理器组装为一个完整的执行上下文。
    /// </remarks>
    public SkillExecutionContext CreateContext(
        SkillDefinition skill,
        LLMConfiguration? config = null,
        ToolManager? toolManager = null)
    {
        var effectiveConfig = config ?? throw new ArgumentNullException(nameof(config),
            "LLMConfiguration is required to create a skill execution context.");

        var client = _clientFactory.Create(effectiveConfig);
        var conversation = new ConversationManager(client, skill.SystemPrompt);
        var tools = toolManager ?? new ToolManager();

        return new SkillExecutionContext(skill, conversation, tools);
    }

    /// <summary>
    /// 以非流式模式异步执行技能，支持多轮工具调用。
    /// </summary>
    /// <param name="context">技能执行上下文，包含技能定义、对话和工具管理器。</param>
    /// <param name="userInput">用户的输入文本。</param>
    /// <param name="options">可选的聊天选项，如温度、最大输出 token 数等。</param>
    /// <param name="maxToolRoundtrips">允许的最大工具调用轮次，默认为 5。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作的任务，任务结果为最终的聊天响应。</returns>
    /// <remarks>
    /// <para>
    /// 该方法会构建初始消息（系统提示 + 用户输入），然后进入工具调用循环：
    /// 每次收到 LLM 响应后检查是否包含函数调用，如果有则执行对应工具并将结果追加到消息历史中继续下一轮。
    /// 当 LLM 不再返回工具调用或达到最大轮次限制时返回最终响应。
    /// </para>
    /// <para>
    /// 如果达到最大工具调用轮次限制，方法仍会发出最后一次 LLM 调用以获取最终回复，
    /// 并记录警告日志。
    /// </para>
    /// </remarks>
    public async Task<ChatResponse> ExecuteAsync(
        SkillExecutionContext context,
        string userInput,
        ChatOptions? options = null,
        int maxToolRoundtrips = 5,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, context.Skill.SystemPrompt),
            new(ChatRole.User, userInput)
        };

        var toolOptions = options ?? new ChatOptions();
        var client = context.Conversation.Client;

        for (int round = 0; round < maxToolRoundtrips; round++)
        {
            if (context.Tools.Tools.Count > 0)
            {
                toolOptions.Tools = context.Tools.GetAITools();
            }

            var response = await client.ChatAsync(messages, toolOptions, cancellationToken);

            if (response.RawMessage == null)
            {
                return response;
            }

            var toolCalls = response.RawMessage.Contents
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count == 0)
            {
                return response;
            }

            _logger?.LogDebug("Skill '{Skill}' round {Round}: {Count} tool call(s)",
                context.Skill.Name, round + 1, toolCalls.Count);

            messages.Add(response.RawMessage);

            foreach (var toolCall in toolCalls)
            {
                var argumentsJson = System.Text.Json.JsonSerializer.Serialize(
                    toolCall.Arguments ?? new Dictionary<string, object?>());

                var result = await context.Tools.ExecuteToolAsync(
                    toolCall.Name, argumentsJson, cancellationToken);

                var callId = toolCall.CallId ?? Guid.NewGuid().ToString("N");
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(callId, result.IsSuccess ? result.Result : $"Error: {result.Error}")]));
            }
        }

        _logger?.LogWarning("Skill '{Skill}' reached max tool roundtrips ({Max})",
            context.Skill.Name, maxToolRoundtrips);

        return await client.ChatAsync(messages, toolOptions, cancellationToken);
    }

    /// <summary>
    /// 以流式模式异步执行技能，支持多轮工具调用，逐步产出响应更新。
    /// </summary>
    /// <param name="context">技能执行上下文，包含技能定义、对话和工具管理器。</param>
    /// <param name="userInput">用户的输入文本。</param>
    /// <param name="options">可选的聊天选项，如温度、最大输出 token 数等。</param>
    /// <param name="maxToolRoundtrips">允许的最大工具调用轮次，默认为 5。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>异步枚举流，逐步产生聊天响应更新片段。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ExecuteAsync"/> 类似，但以流式方式处理 LLM 输出。每轮迭代中，
    /// 方法会消费完整的流式响应以捕获可能的函数调用内容，同时将文本内容实时 yield 给调用方。
    /// </para>
    /// <para>
    /// 当检测到工具调用时，暂停流式输出，执行工具后将结果加入消息历史继续下一轮。
    /// 如果某轮没有工具调用，则直接结束流式输出。
    /// </para>
    /// <para>
    /// 此方法使用了 <c>[EnumeratorCancellation]</c> 特性，允许在 foreach 循环中使用取消令牌中断异步枚举。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteStreamingAsync(
        SkillExecutionContext context,
        string userInput,
        ChatOptions? options = null,
        int maxToolRoundtrips = 5,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, context.Skill.SystemPrompt),
            new(ChatRole.User, userInput)
        };

        var toolOptions = options ?? new ChatOptions();
        var client = context.Conversation.Client;

        for (int round = 0; round < maxToolRoundtrips; round++)
        {
            if (context.Tools.Tools.Count > 0)
            {
                toolOptions.Tools = context.Tools.GetAITools();
            }

            var fullContent = new StringBuilder();
            List<FunctionCallContent>? capturedToolCalls = null;

            await foreach (var update in client.StreamChatAsync(messages, toolOptions, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    fullContent.Append(update.Text);
                    yield return update;
                }

                if (update.Contents?.Count > 0)
                {
                    capturedToolCalls = update.Contents
                        .OfType<FunctionCallContent>()
                        .ToList();
                }
            }

            if (capturedToolCalls == null || capturedToolCalls.Count == 0)
            {
                yield break;
            }

            _logger?.LogDebug("Skill '{Skill}' streaming round {Round}: {Count} tool call(s)",
                context.Skill.Name, round + 1, capturedToolCalls.Count);

            messages.Add(BuildAssistantMessage(fullContent.ToString(), capturedToolCalls));

            foreach (var toolCall in capturedToolCalls)
            {
                var argumentsJson = System.Text.Json.JsonSerializer.Serialize(
                    toolCall.Arguments ?? new Dictionary<string, object?>());

                var result = await context.Tools.ExecuteToolAsync(
                    toolCall.Name, argumentsJson, cancellationToken);

                var callId = toolCall.CallId ?? Guid.NewGuid().ToString("N");
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(callId, result.IsSuccess ? result.Result : $"Error: {result.Error}")]));
            }
        }

        _logger?.LogWarning("Skill '{Skill}' streaming reached max tool roundtrips ({Max})",
            context.Skill.Name, maxToolRoundtrips);

        await foreach (var update in client.StreamChatAsync(messages, toolOptions, cancellationToken))
        {
            yield return update;
        }
    }

    private static ChatMessage BuildAssistantMessage(string text, List<FunctionCallContent> toolCalls)
    {
        if (string.IsNullOrEmpty(text) && toolCalls.Count > 0)
        {
            return new ChatMessage(ChatRole.Assistant, [.. toolCalls]);
        }

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(text))
            contents.Add(new TextContent(text));
        contents.AddRange(toolCalls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }
}
