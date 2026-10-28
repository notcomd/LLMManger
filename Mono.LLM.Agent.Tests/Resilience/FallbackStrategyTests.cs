using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Resilience;
using Mono.LLM.Agent.Tests.Fakes;

namespace Mono.LLM.Agent.Tests.Resilience;

/// <summary>
/// 覆盖多提供商故障转移的顺序与异常语义。
/// </summary>
public class FallbackStrategyTests
{
    [Fact]
    public async Task ExecuteWithFallbackAsync_PrimarySucceeds_DoesNotUseFallback()
    {
        var primary = new FakeLlmClient();
        var backup = new FakeLlmClient();
        var strategy = new FallbackStrategy([backup]);

        var result = await strategy.ExecuteWithFallbackAsync(
            primary,
            (_, _) => Task.FromResult("primary"));

        Assert.Equal("primary", result);
    }

    [Fact]
    public async Task ExecuteWithFallbackAsync_PrimaryFails_UsesFallbackInOrder()
    {
        var primary = new FakeLlmClient();
        var first = new FakeLlmClient();
        var second = new FakeLlmClient();
        var strategy = new FallbackStrategy([first, second]);

        var tried = new List<string>();

        var result = await strategy.ExecuteWithFallbackAsync(primary, (client, _) =>
        {
            tried.Add(client == primary ? "primary" : client == first ? "first" : "second");

            if (client == second)
                return Task.FromResult("second-result");

            throw new HttpRequestException("down");
        });

        Assert.Equal("second-result", result);
        Assert.Equal(new[] { "primary", "first", "second" }, tried);
    }

    [Fact]
    public async Task ExecuteWithFallbackAsync_AllFail_PropagatesLastException()
    {
        var primary = new FakeLlmClient();
        var backup = new FakeLlmClient();
        var strategy = new FallbackStrategy([backup]);

        await Assert.ThrowsAsync<HttpRequestException>(() => strategy.ExecuteWithFallbackAsync<string>(
            primary,
            (_, _) => throw new HttpRequestException("down")));
    }

    [Fact]
    public async Task ExecuteWithFallbackAsync_NullPrimary_Throws()
    {
        var strategy = new FallbackStrategy([]);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            strategy.ExecuteWithFallbackAsync<string>(null!, (_, _) => Task.FromResult("x")));
    }

    [Fact]
    public void Constructor_NullFallbackList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FallbackStrategy(null!));
    }
}
