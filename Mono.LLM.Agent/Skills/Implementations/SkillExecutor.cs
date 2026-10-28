// ============================================================================
// 文件：SkillExecutor.cs
// 用途：技能执行器，负责创建技能执行上下文并以同步或流式方式执行技能。
// 实现方：SkillExecutor
// 调用方：应用层（显式执行技能）、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;
using System.Text.Json;
using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Skills;

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
///   <item>把用户输入追加到 <see cref="SkillExecutionContext.Conversation"/>，并按上下文上限裁剪</item>
///   <item>按 <see cref="SkillDefinition.AllowedTools"/> 确定本次可见的工具集</item>
///   <item>调用 LLM 获取响应</item>
///   <item>若响应包含工具调用，批量执行并把结果回填到同一份历史</item>
///   <item>重复步骤 3-4 直到没有更多工具调用或达到最大轮次限制</item>
/// </list>
/// </para>
/// <para>
/// 与早期版本不同，消息历史只存在于 <see cref="ConversationManager"/> 中，执行器不再维护第二份副本，
/// 因此同一上下文可以承载多轮技能对话。
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
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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
    /// 若技能声明了 <see cref="SkillDefinition.AllowedTools"/>，上下文会额外持有一个裁剪后的工具集；
    /// 名单中出现但尚未注册的工具会记录警告，便于发现拼写错误或注册遗漏。
    /// </remarks>
    public SkillExecutionContext CreateContext(
        SkillDefinition skill,
        LLMConfiguration? config = null,
        ToolManager? toolManager = null)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var effectiveConfig = config ?? throw new ArgumentNullException(nameof(config),
            "LLMConfiguration is required to create a skill execution context.");

        var client = _clientFactory.Create(effectiveConfig);
        var conversation = new ConversationManager(client, skill.SystemPrompt);
        var tools = toolManager ?? new ToolManager();

        var effectiveTools = tools;
        if (skill.AllowedTools.Count > 0)
        {
            ReportUnknownTools(skill, tools);
            effectiveTools = tools.CreateScoped(skill.AllowedTools);
        }

        return new SkillExecutionContext(skill, conversation, tools, effectiveTools);
    }

    /// <summary>
    /// 以非流式模式异步执行技能，支持多轮工具调用。
    /// </summary>
    /// <param name="context">技能执行上下文，包含技能定义、对话和工具管理器。</param>
    /// <param name="userInput">用户的输入文本。</param>
    /// <param name="options">可选的聊天选项，本方法不会修改传入实例。</param>
    /// <param name="maxToolRoundtrips">允许的最大工具调用轮次，默认为 5。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作的任务，任务结果为最终的聊天响应。</returns>
    /// <remarks>
    /// <para>
    /// 达到 <paramref name="maxToolRoundtrips"/> 上限时，会再发起一次不携带工具的请求，
    /// 强制模型基于已有信息给出最终答复，而不是把中间过程直接截断。
    /// </para>
    /// <para>
    /// 执行结果会写入上下文的历史，因此对同一上下文再次调用可继续多轮对话。
    /// </para>
    /// </remarks>
    public async Task<ChatResponse> ExecuteAsync(
        SkillExecutionContext context,
        string userInput,
        ChatOptions? options = null,
        int maxToolRoundtrips = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userInput);

        var conversation = context.Conversation;
        var tools = context.EffectiveTools;
        var rounds = Math.Max(1, maxToolRoundtrips);

        conversation.AddUserMessage(userInput);
        conversation.TrimHistory();

        for (int round = 0; round < rounds; round++)
        {
            var roundOptions = BuildOptions(options, tools);
            var response = await conversation.Client
                .ChatAsync(conversation.History, roundOptions, cancellationToken)
                .ConfigureAwait(false);

            if (response.RawMessage == null)
                return response;

            var toolCalls = response.RawMessage.Contents.OfType<FunctionCallContent>().ToList();

            if (toolCalls.Count == 0)
            {
                conversation.AddMessage(response.RawMessage);
                return response;
            }

            _logger?.LogDebug("Skill '{Skill}' round {Round}: {Count} tool call(s)",
                context.Skill.Name, round + 1, toolCalls.Count);

            conversation.AddMessage(response.RawMessage);
            await ExecuteToolCallsAsync(tools, conversation, toolCalls, cancellationToken).ConfigureAwait(false);
            conversation.TrimHistory();
        }

        _logger?.LogWarning("Skill '{Skill}' reached max tool roundtrips ({Max})",
            context.Skill.Name, maxToolRoundtrips);

        var finalOptions = BuildOptions(options, tools: null);
        var finalResponse = await conversation.Client
            .ChatAsync(conversation.History, finalOptions, cancellationToken)
            .ConfigureAwait(false);

        if (finalResponse.RawMessage != null)
            conversation.AddMessage(finalResponse.RawMessage);

        return finalResponse;
    }

    /// <summary>
    /// 以流式模式异步执行技能，支持多轮工具调用，逐步产出响应更新。
    /// </summary>
    /// <param name="context">技能执行上下文，包含技能定义、对话和工具管理器。</param>
    /// <param name="userInput">用户的输入文本。</param>
    /// <param name="options">可选的聊天选项，本方法不会修改传入实例。</param>
    /// <param name="maxToolRoundtrips">允许的最大工具调用轮次，默认为 5。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>异步枚举流，逐步产生聊天响应更新片段。</returns>
    /// <remarks>
    /// <para>
    /// 每轮迭代中，方法会消费完整的流式响应以捕获可能的函数调用内容，同时将文本内容实时 yield 给调用方。
    /// 当检测到工具调用时，暂停流式输出，执行工具后将结果加入历史继续下一轮。
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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userInput);

        var conversation = context.Conversation;
        var tools = context.EffectiveTools;
        var rounds = Math.Max(1, maxToolRoundtrips);

        conversation.AddUserMessage(userInput);
        conversation.TrimHistory();

        for (int round = 0; round < rounds; round++)
        {
            var roundOptions = BuildOptions(options, tools);
            var text = new StringBuilder();
            var toolCalls = new List<FunctionCallContent>();

            await foreach (var update in conversation.Client
                .StreamChatAsync(conversation.History, roundOptions, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    text.Append(update.Text);
                    yield return update;
                }

                if (update.Contents is { Count: > 0 } contents)
                {
                    foreach (var content in contents)
                    {
                        if (content is FunctionCallContent call)
                            toolCalls.Add(call);
                    }
                }
            }

            if (toolCalls.Count == 0)
            {
                if (text.Length > 0)
                    conversation.AddAssistantMessage(text.ToString());

                yield break;
            }

            _logger?.LogDebug("Skill '{Skill}' streaming round {Round}: {Count} tool call(s)",
                context.Skill.Name, round + 1, toolCalls.Count);

            conversation.AddMessage(BuildAssistantToolCallMessage(text.ToString(), toolCalls));
            await ExecuteToolCallsAsync(tools, conversation, toolCalls, cancellationToken).ConfigureAwait(false);
            conversation.TrimHistory();
        }

        _logger?.LogWarning("Skill '{Skill}' streaming reached max tool roundtrips ({Max})",
            context.Skill.Name, maxToolRoundtrips);

        var finalOptions = BuildOptions(options, tools: null);

        await foreach (var update in conversation.Client
            .StreamChatAsync(conversation.History, finalOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task ExecuteToolCallsAsync(
        ToolManager tools,
        ConversationManager conversation,
        List<FunctionCallContent> toolCalls,
        CancellationToken cancellationToken)
    {
        var requests = toolCalls
            .Select(call => new ToolCallRequest(
                call.Name ?? string.Empty,
                SerializeArguments(call.Arguments),
                call.CallId))
            .ToList();

        var results = await tools
            .ExecuteToolCallsAsync(requests, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < toolCalls.Count; i++)
        {
            var callId = toolCalls[i].CallId ?? Guid.NewGuid().ToString("N");
            var result = results[i];

            conversation.AddToolMessage(
                callId,
                result.IsSuccess ? result.Result : $"Error: {result.Error}");
        }
    }

    private static ChatOptions BuildOptions(ChatOptions? template, ToolManager? tools)
    {
        var options = template?.Clone() ?? new ChatOptions();

        options.Tools = tools is { } manager && manager.Tools.Count > 0
            ? manager.GetAITools()
            : null;

        return options;
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => arguments is null ? "{}" : JsonSerializer.Serialize(arguments);

    private static ChatMessage BuildAssistantToolCallMessage(
        string text,
        IReadOnlyList<FunctionCallContent> toolCalls)
    {
        var contents = new List<AIContent>(toolCalls.Count + 1);

        if (!string.IsNullOrEmpty(text))
            contents.Add(new TextContent(text));

        contents.AddRange(toolCalls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private void ReportUnknownTools(SkillDefinition skill, ToolManager tools)
    {
        if (_logger == null)
            return;

        var registered = tools.GetRegisteredNames();
        var missing = skill.AllowedTools
            .Where(name => !registered.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Skill '{Skill}' declares tools that are not registered: {Missing}",
                skill.Name,
                string.Join(", ", missing));
        }
    }
}
