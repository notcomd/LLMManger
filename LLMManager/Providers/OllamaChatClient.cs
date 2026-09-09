using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LLMManager.Providers;

/// <summary>
/// Ollama 本地大语言模型的聊天客户端实现，同时支持文本生成和向量嵌入生成。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OllamaChatClient"/> 实现了 <see cref="IChatClient"/> 和 <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> 接口，
/// 提供与 Ollama 本地服务（默认端口 11434）的完整交互能力。
/// </para>
/// <para>
/// 支持的功能包括：
/// <list type="bullet">
///   <item>非流式和流式的聊天补全（<see cref="GetResponseAsync"/> / <see cref="GetStreamingResponseAsync"/>）</item>
///   <item>文本向量化（<see cref="GenerateAsync"/>）</item>
///   <item>可配置的温度和最大输出 token 参数</item>
///   <item>自动将 Microsoft.Extensions.AI 的消息角色映射到 Ollama 的角色格式</item>
/// </list>
/// </para>
/// <para>
/// 该类通过 HTTP POST 请求与 Ollama 的 REST API（/api/chat 和 /api/embeddings）通信，
/// 使用 snake_case_lower 命名策略进行 JSON 序列化。
/// </para>
/// <para>
/// 此类实现了 <see cref="IDisposable"/> 接口，使用完毕后应调用 <see cref="Dispose"/> 方法释放底层 HTTP 客户端资源。
/// </para>
/// </remarks>
public class OllamaChatClient : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// 初始化 <see cref="OllamaChatClient"/> 类的新实例，连接到指定的 Ollama 服务端点。
    /// </summary>
    /// <param name="endpoint">Ollama 服务的 URI 地址，如 http://localhost:11434。</param>
    /// <param name="modelId">要使用的模型标识符，如 "llama3"、"qwen2" 等。</param>
    public OllamaChatClient(Uri endpoint, string modelId)
    {
        _httpClient = new HttpClient { BaseAddress = endpoint };
        _modelId = modelId;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// 以非流式模式异步发送聊天请求并获取完整的模型响应。
    /// </summary>
    /// <param name="chatMessages">要发送的消息序列，包含系统、用户、助手或工具角色的消息。</param>
    /// <param name="options">可选的聊天选项，用于设置温度、最大输出 token 数等参数。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作的任务，任务结果为包含模型回复内容的 <see cref="ChatResponse"/>。</returns>
    /// <exception cref="HttpRequestException">当 HTTP 请求失败或返回非成功状态码时抛出。</exception>
    /// <remarks>
    /// 将 Microsoft.Extensions.AI 的消息格式转换为 Ollama API 格式后发送到 /api/chat 端点（stream=false），
    /// 然后将响应解析回 <see cref="ChatResponse"/> 对象，包含内容、模型 ID、用量统计和完成原因等信息。
    /// </remarks>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messagesList = chatMessages.ToList();
        var request = new OllamaChatRequest
        {
            Model = _modelId,
            Messages = messagesList.Select(m => new OllamaMessage
            {
                Role = MapRole(m.Role),
                Content = m.Text ?? string.Empty
            }).ToList(),
            Stream = false,
            Options = options != null ? new OllamaOptions
            {
                Temperature = options.Temperature,
                NumPredict = options.MaxOutputTokens
            } : null
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/api/chat", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, _jsonOptions);

        return new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            chatResponse?.Message?.Content ?? string.Empty))
        {
            ModelId = chatResponse?.Model ?? _modelId,
            Usage = chatResponse != null ? new UsageDetails
            {
                InputTokenCount = chatResponse.PromptEvalCount ?? 0,
                OutputTokenCount = chatResponse.EvalCount ?? 0
            } : null,
            FinishReason = chatResponse?.Done == true ? ChatFinishReason.Stop : null
        };
    }

    /// <summary>
    /// 以流式模式异步发送聊天请求，逐步产出模型的响应更新片段。
    /// </summary>
    /// <param name="chatMessages">要发送的消息序列，包含系统、用户、助手或工具角色的消息。</param>
    /// <param name="options">可选的聊天选项，用于设置温度、最大输出 token 数等参数。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>异步枚举流，逐步产生 <see cref="ChatResponseUpdate"/> 片段。</returns>
    /// <exception cref="HttpRequestException">当 HTTP 请求失败或返回非成功状态码时抛出。</exception>
    /// <remarks>
    /// <para>
    /// 使用 stream=true 模式发送请求到 /api/chat 端点，然后逐行读取 SSE (Server-Sent Events) 格式的流式响应。
    /// 每行 JSON 被反序列化为一个响应块，提取其中的文本内容并 yield 为 <see cref="ChatResponseUpdate"/>。
    /// </para>
    /// <para>
    /// 此方法使用了 <c>[EnumeratorCancellation]</c> 特性，允许在 foreach 循环中使用取消令牌中断异步枚举。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messagesList = chatMessages.ToList();
        var request = new OllamaChatRequest
        {
            Model = _modelId,
            Messages = messagesList.Select(m => new OllamaMessage
            {
                Role = MapRole(m.Role),
                Content = m.Text ?? string.Empty
            }).ToList(),
            Stream = true,
            Options = options != null ? new OllamaOptions
            {
                Temperature = options.Temperature,
                NumPredict = options.MaxOutputTokens
            } : null
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = httpContent
        };

        var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, _jsonOptions);
            if (chunk?.Message?.Content != null)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    chunk.Message.Content);
            }
        }
    }

    /// <summary>
    /// 异步生成指定文本集合的向量嵌入表示。
    /// </summary>
    /// <param name="values">需要向量化的文本集合。</param>
    /// <param name="options">可选的嵌入生成选项。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步操作的任务，任务结果为包含所有嵌入向量的 <see cref="GeneratedEmbeddings{T}"/> 集合。</returns>
    /// <exception cref="HttpRequestException">当 HTTP 请求失败或返回非成功状态码时抛出。</exception>
    /// <remarks>
    /// 逐一对每个输入文本调用 Ollama 的 /api/embeddings API 生成嵌入向量，
    /// 所有生成的向量汇总到最终的结果集中返回。每次调用都是独立的 HTTP 请求。
    /// </remarks>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var embeddings = new List<Embedding<float>>();

        foreach (var text in texts)
        {
            var request = new OllamaEmbedRequest
            {
                Model = _modelId,
                Input = text
            };

            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/embeddings", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var embedResponse = JsonSerializer.Deserialize<OllamaEmbedResponse>(responseJson, _jsonOptions);

            if (embedResponse?.Embedding != null)
            {
                embeddings.Add(new Embedding<float>(embedResponse.Embedding));
            }
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    object? IChatClient.GetService(Type? serviceType, object? key) =>
        serviceType == typeof(IEmbeddingGenerator<string, Embedding<float>>) ? this : null;

    object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(IChatClient) ? this : null;

    void IDisposable.Dispose() => _httpClient.Dispose();

    private static string MapRole(ChatRole role)
    {
        if (role == ChatRole.User) return "user";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.System) return "system";
        if (role == ChatRole.Tool) return "tool";
        return "user";
    }
}
