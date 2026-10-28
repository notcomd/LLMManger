using Mono.LLM.Agent.Resilience;

namespace Mono.LLM.Agent.Tests.Resilience;

/// <summary>
/// 覆盖重试次数、异常分类与取消语义。
/// </summary>
public class RetryPolicyTests
{
    private sealed class CustomIOException : IOException
    {
    }

    [Fact]
    public async Task ExecuteAsync_SucceedsFirstAttempt_DoesNotRetry()
    {
        var policy = new DefaultRetryPolicy(3, TimeSpan.Zero);
        var attempts = 0;

        var result = await policy.ExecuteAsync((_, _) =>
        {
            attempts++;
            return Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RetryableFailure_RetriesUpToMaxCount()
    {
        var policy = new DefaultRetryPolicy(2, TimeSpan.Zero);
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ExecuteAsync<string>((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("network");
        }));

        // maxRetryCount = 2 → 首次 + 2 次重试
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_NonRetryableFailure_ThrowsImmediately()
    {
        var policy = new DefaultRetryPolicy(3, TimeSpan.Zero);
        var attempts = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => policy.ExecuteAsync<string>((_, _) =>
        {
            attempts++;
            throw new ArgumentException("bad input");
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DerivedRetryableException_IsStillRetried()
    {
        var policy = new DefaultRetryPolicy(1, TimeSpan.Zero);
        var attempts = 0;

        await Assert.ThrowsAsync<CustomIOException>(() => policy.ExecuteAsync<string>((_, _) =>
        {
            attempts++;
            throw new CustomIOException();
        }));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledToken_DoesNotRetry()
    {
        var policy = new DefaultRetryPolicy(3, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync<string>((_, _) =>
        {
            attempts++;
            cts.Cancel();
            throw new TaskCanceledException();
        }, cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversAfterTransientFailure()
    {
        var policy = new DefaultRetryPolicy(2, TimeSpan.Zero);
        var attempts = 0;

        var result = await policy.ExecuteAsync((_, _) =>
        {
            attempts++;
            if (attempts < 2)
                throw new TimeoutException();

            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsAscendingAttemptNumber()
    {
        var policy = new DefaultRetryPolicy(2, TimeSpan.Zero);
        var seen = new List<int>();

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ExecuteAsync<string>((_, attempt) =>
        {
            seen.Add(attempt);
            throw new HttpRequestException("x");
        }));

        Assert.Equal(new[] { 1, 2, 3 }, seen);
    }
}
