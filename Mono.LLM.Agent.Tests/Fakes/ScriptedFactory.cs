using Mono.LLM.Agent.Core;
using Microsoft.Extensions.AI;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Tests.Fakes;

/// <summary>
/// 按脚本应答的客户端工厂，用于在不访问网络的前提下驱动编排流程。
/// </summary>
/// <remarks>
/// 编排器每次调用都会新建客户端，因此脚本函数必须是无状态的：
/// 它接收 (Agent 名称, 用户消息) 并直接返回内容，不依赖实例字段。
/// </remarks>
internal sealed class ScriptedFactory : LLMClientFactory
{
    private readonly Func<string, string, string> _respond;

    public ScriptedFactory(Func<string, string, string> respond)
    {
        _respond = respond;
    }

    public override ILLMClient Create(LLMConfiguration configuration)
        => new ScriptedClient(configuration, _respond);
}

/// <summary>
/// 按脚本返回固定内容的 <see cref="ILLMClient"/>。
/// </summary>
internal sealed class ScriptedClient : ILLMClient
{
    private readonly Func<string, string, string> _respond;

    public ScriptedClient(LLMConfiguration configuration, Func<string, string, string> respond)
    {
        Configuration = configuration;
        _respond = respond;
    }

    public LLMConfiguration Configuration { get; }

    public IChatClient InnerClient { get; } = new FakeChatClient();

    public Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = _respond(Configuration.ModelId, userText);

        return Task.FromResult(new ChatResponse
        {
            Content = reply,
            InputTokens = 10,
            OutputTokens = 20
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var text = (await ChatAsync(messages, options, cancellationToken).ConfigureAwait(false)).Content;
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    public Task<Embedding<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(new Embedding<float>(new[] { 1f, 0f }));

    public Task<IList<Embedding<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IList<Embedding<float>>>(
            texts.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList());

    public void Dispose() { }
}
