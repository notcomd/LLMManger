using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Resilience;
using Mono.LLM.Agent.Streaming;
using Mono.LLM.Agent.Tests.Fakes;
using AgentChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Tests.Core;

/// <summary>
/// 覆盖思考过程（reasoning content）的提取、历史剥离与流式回调。
/// </summary>
public class ReasoningTests
{
    private static LLMConfiguration Config() => new()
    {
        ProviderType = LLMProviderType.DeepSeek,
        ApiKey = "test",
        ModelId = "deepseek-reasoner",
        MaxRetryCount = 0
    };

    private static FakeChatClient RespondingWith(params AIContent[] contents) =>
        new((_, _, _) => Task.FromResult(
            new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, contents))));

    // ── 非流式提取 ──

    [Fact]
    public async Task ChatAsync_ExtractsReasoningSeparatelyFromContent()
    {
        var fake = RespondingWith(
            new TextReasoningContent("先分析题意，再推导结论。"),
            new TextContent("答案是 42。"));

        using var client = new LLMClient(fake, Config(), new DefaultRetryPolicy(0, TimeSpan.Zero));
        var response = await client.ChatAsync([new ChatMessage(ChatRole.User, "1+1")]);

        Assert.Equal("答案是 42。", response.Content);
        Assert.Equal("先分析题意，再推导结论。", response.Reasoning);
    }

    [Fact]
    public async Task ChatAsync_WithoutReasoning_LeavesReasoningNull()
    {
        var fake = RespondingWith(new TextContent("普通回答"));

        using var client = new LLMClient(fake, Config(), new DefaultRetryPolicy(0, TimeSpan.Zero));
        var response = await client.ChatAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Null(response.Reasoning);
        Assert.Equal("普通回答", response.Content);
    }

    [Fact]
    public async Task ChatAsync_MultipleReasoningSegments_AreConcatenated()
    {
        var fake = RespondingWith(
            new TextReasoningContent("第一段。"),
            new TextReasoningContent("第二段。"),
            new TextContent("结论"));

        using var client = new LLMClient(fake, Config(), new DefaultRetryPolicy(0, TimeSpan.Zero));
        var response = await client.ChatAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("第一段。第二段。", response.Reasoning);
    }

    [Fact]
    public async Task ChatAsync_RawMessageRetainsReasoningForAuditing()
    {
        var fake = RespondingWith(
            new TextReasoningContent("思考"),
            new TextContent("答复"));

        using var client = new LLMClient(fake, Config(), new DefaultRetryPolicy(0, TimeSpan.Zero));
        var response = await client.ChatAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Contains(response.RawMessage!.Contents, c => c is TextReasoningContent);
    }

    // ── 历史剥离 ──

    [Fact]
    public void AddMessage_StripsReasoningFromHistory()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());

        conversation.AddMessage(new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("不应被回传的思考"),
            new TextContent("答复正文")
        ]));

        var message = Assert.Single(conversation.History);
        Assert.DoesNotContain(message.Contents, c => c is TextReasoningContent);
        Assert.Equal("答复正文", message.Text);
    }

    [Fact]
    public void AddMessage_PreservesOtherContentTypes()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());

        conversation.AddMessage(new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("思考"),
            new FunctionCallContent("call-1", "get_time", new Dictionary<string, object?>())
        ]));

        var message = Assert.Single(conversation.History);
        Assert.Single(message.Contents.OfType<FunctionCallContent>());
        Assert.DoesNotContain(message.Contents, c => c is TextReasoningContent);
    }

    [Fact]
    public void AddMessage_ReasoningOnly_IsDroppedEntirely()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());

        conversation.AddMessage(new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("只有思考，没有正文")]));

        Assert.Empty(conversation.History);
    }

    [Fact]
    public void AddMessage_WithoutReasoning_KeepsSameInstance()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());
        var message = new ChatMessage(ChatRole.Assistant, "普通消息");

        conversation.AddMessage(message);

        Assert.Same(message, conversation.History[0]);
    }

    [Fact]
    public async Task SendAsync_HistoryHasNoReasoningAfterRoundTrip()
    {
        var fake = new FakeLlmClient
        {
            Responder = (_, _) => new AgentChatResponse
            {
                Content = "答复",
                Reasoning = "思考",
                RawMessage = new ChatMessage(ChatRole.Assistant,
                [
                    new TextReasoningContent("思考"),
                    new TextContent("答复")
                ])
            }
        };

        using var conversation = new ConversationManager(fake);
        await conversation.SendAsync("问题");

        Assert.DoesNotContain(
            conversation.History,
            m => m.Contents.Any(c => c is TextReasoningContent));
    }

    // ── 流式回调 ──

    [Fact]
    public async Task StreamAndCollect_SeparatesReasoningFromContent()
    {
        var client = new ReasoningStreamClient();
        var streaming = new StreamingManager(client);

        var reasoning = new System.Text.StringBuilder();
        var tokens = new System.Text.StringBuilder();

        var content = await streaming.StreamAndCollectAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            onToken: t => tokens.Append(t),
            onReasoning: r => reasoning.Append(r));

        Assert.Equal("答案。", content);
        Assert.Equal("答案。", tokens.ToString());
        Assert.Equal("思考一。思考二。", reasoning.ToString());
    }

    [Fact]
    public async Task StreamAndCollect_WithoutReasoningCallback_StillReturnsContent()
    {
        var client = new ReasoningStreamClient();
        var streaming = new StreamingManager(client);

        var content = await streaming.StreamAndCollectAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("答案。", content);
    }

    private sealed class ReasoningStreamClient : ILLMClient
    {
        public LLMConfiguration Configuration { get; } = Config();

        public IChatClient InnerClient { get; } = new FakeChatClient();

        public Task<AgentChatResponse> ChatAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本用例只覆盖流式路径。");

        public async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("思考一。")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("思考二。")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("答案。")]);
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
}
