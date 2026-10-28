// ============================================================================
// 文件：LLMClient.cs
// 用途：ILLMClient 接口的默认实现，提供统一的 LLM 服务访问能力。
// 实现方：LLMClient
// 调用方：LLMClientFactory（唯一构造入口）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Resilience;

namespace Mono.LLM.Agent.Core;

/// <summary>
/// <see cref="ILLMClient"/> 接口的默认实现，提供统一的 LLM 服务访问能力。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LLMClient"/> 是 Mono.LLM.Agent 的核心客户端类，封装了对底层
/// <see cref="IChatClient"/> 和 <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// 的调用，并在其之上添加了以下能力：
/// </para>
/// <list type="bullet">
///   <item><description>自动重试：通过 <see cref="IRetryPolicy"/> 在瞬态故障时自动重试请求</description></item>
///   <item><description>默认选项合并：将 <see cref="LLMConfiguration"/> 中的全局配置应用到每次调用</description></item>
///   <item><description>响应映射：将框架原生响应转换为统一的 <see cref="ChatResponse"/> 格式</description></item>
///   <item><description>日志记录：通过 <see cref="ILogger{TCategoryName}"/> 输出调试与诊断信息</description></item>
/// </list>
/// <para>
/// 此类实现了 <see cref="IDisposable"/>，在使用完毕后应调用 <see cref="Dispose"/>
/// 以释放底层客户端资源。推荐通过 <see cref="LLMClientFactory"/> 创建实例。
/// </para>
/// </remarks>
public class LLMClient : ILLMClient
{
    private readonly IChatClient _chatClient;
    private readonly LLMConfiguration _configuration;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ILogger<LLMClient>? _logger;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;

    /// <summary>
    /// 获取当前客户端关联的 LLM 配置对象。
    /// </summary>
    /// <value>构造时传入的 <see cref="LLMConfiguration"/> 只读引用。</value>
    public LLMConfiguration Configuration => _configuration;

    /// <summary>
    /// 获取底层框架的原生聊天客户端实例。
    /// </summary>
    /// <value>
    /// 用于执行实际 API 调用的 <see cref="IChatClient"/> 实现。
    /// </value>
    public IChatClient InnerClient => _chatClient;

    /// <summary>
    /// 初始化 <see cref="LLMClient"/> 类的新实例。
    /// </summary>
    /// <param name="chatClient">
    /// 底层聊天客户端，负责实际与 LLM 服务进行通信。
    /// </param>
    /// <param name="configuration">
    /// 包含连接参数和行为配置的 <see cref="LLMConfiguration"/> 对象。
    /// </param>
    /// <param name="retryPolicy">
    /// 可选的重试策略。若为 <c>null</c>，则使用默认策略（最多重试 3 次，间隔 1 秒）。
    /// </param>
    /// <param name="logger">
    /// 可选的日志记录器。若为 <c>null</c>，则不记录日志。
    /// </param>
    public LLMClient(
        IChatClient chatClient,
        LLMConfiguration configuration,
        IRetryPolicy? retryPolicy = null,
        ILogger<LLMClient>? logger = null)
    {
        _chatClient = chatClient;
        _configuration = configuration;
        _retryPolicy = retryPolicy ?? new DefaultRetryPolicy(3, TimeSpan.FromSeconds(1));
        _logger = logger;
        InitializeEmbeddingGenerator();
    }

    private void InitializeEmbeddingGenerator()
    {
        if (_chatClient is IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
        {
            _embeddingGenerator = embeddingGenerator;
        }
    }

    /// <inheritdoc cref="ILLMClient.ChatAsync"/>
    /// <remarks>
    /// 传入的消息序列会在重试循环外物化一次，因此即使调用方传入惰性序列（<c>Select</c> / <c>yield</c>），
    /// 重试时也不会拿到已耗尽的空集合。调用方提供的 <see cref="ChatOptions"/> 不会被修改。
    /// </remarks>
    public async Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var effectiveOptions = BuildEffectiveOptions(options);

        return await _retryPolicy.ExecuteAsync(async (ct, attempt) =>
        {
            _logger?.LogDebug("Chat attempt {Attempt} with model {Model}", attempt, _configuration.ModelId);

            var response = await _chatClient.GetResponseAsync(messageList, effectiveOptions, ct);

            return MapToChatResponse(response);
        }, cancellationToken);
    }

    /// <inheritdoc cref="ILLMClient.StreamChatAsync"/>
    public async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var effectiveOptions = BuildEffectiveOptions(options);

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messageList, effectiveOptions, cancellationToken))
        {
            yield return update;
        }
    }

    /// <inheritdoc cref="ILLMClient.GenerateEmbeddingAsync"/>
    public async Task<Embedding<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (_embeddingGenerator == null)
        {
            throw new InvalidOperationException(
                $"The provider {_configuration.ProviderType} does not support embedding generation.");
        }

        return await _retryPolicy.ExecuteAsync(async (ct, _) =>
        {
            var result = await _embeddingGenerator.GenerateAsync(
                new[] { text }, null, ct);
            return result[0];
        }, cancellationToken);
    }

    /// <inheritdoc cref="ILLMClient.GenerateEmbeddingsAsync"/>
    public async Task<IList<Embedding<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (_embeddingGenerator == null)
        {
            throw new InvalidOperationException(
                $"The provider {_configuration.ProviderType} does not support embedding generation.");
        }

        return await _retryPolicy.ExecuteAsync(async (ct, _) =>
        {
            var result = await _embeddingGenerator.GenerateAsync(texts, null, ct);
            return result.ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// 基于全局配置构建本次调用实际使用的选项副本。
    /// </summary>
    /// <remarks>
    /// 通过 <see cref="ChatOptions.Clone"/> 生成副本后再补默认值，调用方传入的实例保持不变，
    /// 反复复用同一个 <see cref="ChatOptions"/> 对象不会被静默改写。
    /// </remarks>
    private ChatOptions BuildEffectiveOptions(ChatOptions? options)
    {
        var effective = options?.Clone() ?? new ChatOptions();

        effective.MaxOutputTokens ??= _configuration.MaxOutputTokens;
        effective.Temperature ??= _configuration.Temperature;

        if (_configuration.AdditionalHeaders is { Count: > 0 } headers)
        {
            var properties = effective.AdditionalProperties is null
                ? new AdditionalPropertiesDictionary()
                : new AdditionalPropertiesDictionary(effective.AdditionalProperties);

            foreach (var header in headers)
            {
                properties.TryAdd(header.Key, header.Value);
            }

            effective.AdditionalProperties = properties;
        }

        return effective;
    }

    /// <summary>
    /// 释放底层聊天客户端所占用的资源。
    /// </summary>
    /// <remarks>
    /// 仅当底层 <see cref="IChatClient"/> 实现了 <see cref="IDisposable"/> 时才会被释放。
    /// 此方法可安全地多次调用。
    /// </remarks>
    public void Dispose()
    {
        if (_chatClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static ChatResponse MapToChatResponse(Microsoft.Extensions.AI.ChatResponse response)
    {
        var message = response.Messages.FirstOrDefault(m => m.Role == ChatRole.Assistant)
                      ?? response.Messages.Last();

        return new ChatResponse
        {
            Content = message.Text ?? string.Empty,
            Reasoning = ExtractReasoning(message),
            ModelId = response.ModelId ?? string.Empty,
            InputTokens = (int)(response.Usage?.InputTokenCount ?? 0),
            OutputTokens = (int)(response.Usage?.OutputTokenCount ?? 0),
            FinishReason = response.FinishReason?.ToString(),
            RawMessage = message
        };
    }

    /// <summary>
    /// 汇总消息中所有 <see cref="TextReasoningContent"/> 的文本。
    /// </summary>
    /// <returns>思考过程文本；没有则返回 <c>null</c>。</returns>
    /// <remarks>
    /// <see cref="ChatMessage.Text"/> 只拼接普通 <see cref="TextContent"/>，
    /// 推理型模型产生的思考过程需要单独从 <see cref="ChatMessage.Contents"/> 中取。
    /// </remarks>
    private static string? ExtractReasoning(ChatMessage message)
    {
        StringBuilder? builder = null;

        foreach (var content in message.Contents)
        {
            if (content is TextReasoningContent { Text.Length: > 0 } reasoning)
                (builder ??= new StringBuilder()).Append(reasoning.Text);
        }

        return builder?.ToString();
    }
}
