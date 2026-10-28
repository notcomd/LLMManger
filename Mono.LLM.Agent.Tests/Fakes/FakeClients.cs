using Mono.LLM.Agent.Core;
using Microsoft.Extensions.AI;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Tests.Fakes;

/// <summary>
/// 可编程的 <see cref="IChatClient"/> 替身，用于避免测试依赖真实网络。
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<Microsoft.Extensions.AI.ChatResponse>> _handler;

    public FakeChatClient(
        Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<Microsoft.Extensions.AI.ChatResponse>>? handler = null)
    {
        _handler = handler ?? ((_, _, _) => Task.FromResult(
            new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))));
    }

    public int CallCount { get; private set; }

    public List<ChatOptions?> ReceivedOptions { get; } = new();

    public List<int> ReceivedMessageCounts { get; } = new();

    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions.Add(options);

        var list = messages.ToList();
        ReceivedMessageCounts.Add(list.Count);

        return _handler(list, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions.Add(options);
        await Task.Yield();
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// 可编程的 <see cref="ILLMClient"/> 替身，用于测试会话与对话逻辑。
/// </summary>
internal sealed class FakeLlmClient : ILLMClient
{
    public int CallCount { get; private set; }

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    public List<ChatOptions?> ReceivedOptions { get; } = new();

    public Func<IReadOnlyList<ChatMessage>, int, ChatResponse>? Responder { get; set; }

    public LLMConfiguration Configuration { get; } = LLMConfiguration.CreateDeepSeek("test-key");

    public IChatClient InnerClient { get; } = new FakeChatClient();

    public Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        CallCount++;
        Calls.Add(list);
        ReceivedOptions.Add(options);

        var response = Responder?.Invoke(list, CallCount)
                       ?? new ChatResponse { Content = "ok" };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        CallCount++;
        Calls.Add(list);

        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public Task<Embedding<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(new Embedding<float>(new[] { 1f, 0f }));

    public Task<IList<Embedding<float>>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        => Task.FromResult<IList<Embedding<float>>>(
            texts.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList());

    public void Dispose() { }
}
