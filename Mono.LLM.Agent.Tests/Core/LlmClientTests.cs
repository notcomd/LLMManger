using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Resilience;
using Mono.LLM.Agent.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tests.Core;

/// <summary>
/// 覆盖 LLMClient 的选项处理与重试边界。
/// </summary>
public class LlmClientTests
{
    private static LLMConfiguration TestConfig() => new()
    {
        ProviderType = LLMProviderType.DeepSeek,
        ApiKey = "test",
        ModelId = "deepseek-chat",
        Temperature = 0.3f,
        MaxOutputTokens = 128,
        MaxRetryCount = 0
    };

    private static ChatMessage[] OneMessage() =>
        [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task ChatAsync_DoesNotMutateCallerOptions()
    {
        var fake = new FakeChatClient();
        using var client = new LLMClient(fake, TestConfig(), new DefaultRetryPolicy(0, TimeSpan.Zero));

        var callerOptions = new ChatOptions();
        await client.ChatAsync(OneMessage(), callerOptions);

        Assert.Null(callerOptions.Temperature);
        Assert.Null(callerOptions.MaxOutputTokens);
    }

    [Fact]
    public async Task ChatAsync_AppliesConfigurationDefaultsToInternalCopy()
    {
        var fake = new FakeChatClient();
        using var client = new LLMClient(fake, TestConfig(), new DefaultRetryPolicy(0, TimeSpan.Zero));

        await client.ChatAsync(OneMessage());

        var used = Assert.Single(fake.ReceivedOptions);
        Assert.NotNull(used);
        Assert.Equal(0.3f, used!.Temperature!.Value, 3);
        Assert.Equal(128, used.MaxOutputTokens);
    }

    [Fact]
    public async Task ChatAsync_RetryWithLazySequence_KeepsMessageCountStable()
    {
        var fake = new FakeChatClient((messages, _, _) =>
        {
            if (messages.Count == 0)
                throw new InvalidOperationException("消息序列被耗尽，重试拿到了空集合");

            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "ok")));
        });

        using var client = new LLMClient(
            fake,
            TestConfig(),
            new DefaultRetryPolicy(2, TimeSpan.Zero));

        IEnumerable<ChatMessage> lazy = OneMessage().Select(m => m);

        await client.ChatAsync(lazy);

        Assert.Equal(1, fake.CallCount);
        Assert.All(fake.ReceivedMessageCounts, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ChatAsync_MapsResponseMetadata()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "hello"))
        {
            ModelId = "deepseek-chat",
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 },
            FinishReason = ChatFinishReason.Stop
        }));

        using var client = new LLMClient(fake, TestConfig(), new DefaultRetryPolicy(0, TimeSpan.Zero));

        var response = await client.ChatAsync(OneMessage());

        Assert.Equal("hello", response.Content);
        Assert.Equal("deepseek-chat", response.ModelId);
        Assert.Equal(11, response.InputTokens);
        Assert.Equal(7, response.OutputTokens);
        Assert.NotNull(response.FinishReason);
    }

    [Fact]
    public async Task ChatAsync_NullMessages_Throws()
    {
        var fake = new FakeChatClient();
        using var client = new LLMClient(fake, TestConfig(), new DefaultRetryPolicy(0, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.ChatAsync(null!));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WhenProviderHasNoEmbedding_Throws()
    {
        var fake = new FakeChatClient();
        using var client = new LLMClient(fake, TestConfig(), new DefaultRetryPolicy(0, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GenerateEmbeddingAsync("text"));
    }
}
