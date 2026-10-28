using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖回环回调服务：真实起监听、模拟浏览器跳回、跳过无关请求。
/// </summary>
/// <remarks>
/// 这是唯一使用本地端口的测试，不访问外网；它验证的是「浏览器真的跳回来时能不能接住」，
/// 这一段无法用 HTTP 桩替代。
/// </remarks>
public class LoopbackOAuthInteractionTests
{
    [Fact]
    public async Task CapturesAuthorizationCode()
    {
        var interaction = new LoopbackOAuthInteraction(
            openBrowser: false,
            timeout: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var authorizeTask = interaction.AuthorizeAsync(
            redirect =>
            {
                // 监听已在工厂被调用之前启动，这里可以安全地模拟浏览器。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(80);
                    using var http = new HttpClient();

                    try
                    {
                        await http.GetAsync($"{redirect}?code=code-9&state=st-1");
                    }
                    catch (HttpRequestException)
                    {
                        // 回调服务写完响应就会关闭连接，客户端拿到异常也不影响断言。
                    }
                });

                return new Uri($"{redirect}?response_type=code");
            },
            cts.Token);

        var callback = await authorizeTask;
        var query = QueryParameters.Parse(callback.Query);

        Assert.Equal("code-9", query["code"]);
        Assert.Equal("st-1", query["state"]);
    }

    [Fact]
    public async Task IgnoresRequestsOutsideCallbackPath()
    {
        var interaction = new LoopbackOAuthInteraction(
            openBrowser: false,
            timeout: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var authorizeTask = interaction.AuthorizeAsync(
            redirect =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(80);
                    using var http = new HttpClient();

                    var origin = new Uri(redirect.GetLeftPart(UriPartial.Authority));

                    // 浏览器常见的第一枪：favicon。不能被当成回调。
                    await TryGetAsync(http, new Uri(origin, "/favicon.ico"));

                    await Task.Delay(30);

                    await TryGetAsync(http, new Uri($"{redirect}?code=code-10&state=st-2"));
                });

                return new Uri($"{redirect}?response_type=code");
            },
            cts.Token);

        var callback = await authorizeTask;

        Assert.Equal("code-10", QueryParameters.Parse(callback.Query)["code"]);
    }

    [Fact]
    public void RejectsNonLoopbackRedirectUri()    {
        Assert.Throws<ArgumentException>(() =>
            new LoopbackOAuthInteraction(new Uri("https://evil.example.com/callback")));
    }

    [Fact]
    public async Task TimesOut_WhenNoCallbackArrives()
    {
        var interaction = new LoopbackOAuthInteraction(
            openBrowser: false,
            timeout: TimeSpan.FromMilliseconds(150));

        var exception = await Assert.ThrowsAsync<McpOAuthException>(() =>
            interaction.AuthorizeAsync(redirect => redirect, CancellationToken.None));

        Assert.Equal("authorization_timeout", exception.Error);
    }

    /// <summary>发一个 GET 并忽略连接异常——回调服务写完响应就会关闭连接。</summary>
    private static async Task TryGetAsync(HttpClient client, Uri uri)
    {
        try
        {
            using var response = await client.GetAsync(uri);
        }
        catch (HttpRequestException)
        {
        }
    }
}
