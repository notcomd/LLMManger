using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Providers;

namespace Mono.LLM.Agent.Tests.Providers;

/// <summary>
/// 覆盖 Ollama 通道对推理模型 <c>thinking</c> 字段的解析与请求侧隔离。
/// </summary>
public class OllamaReasoningTests
{
    private static (OllamaChatClient Client, StubHandler Handler) Create(string responseBody)
    {
        var handler = new StubHandler(responseBody);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaChatClient(new Uri("http://localhost:11434"), "deepseek-r1", http);
        return (client, handler);
    }

    [Fact]
    public async Task GetResponseAsync_MapsThinkingToReasoningContent()
    {
        var (client, _) = Create("""
            {"model":"deepseek-r1","message":{"role":"assistant","content":"答案是 42","thinking":"先理解题意，再做运算。"},"done":true}
            """);

        using var clientScope = client;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1+1")]);

        var message = response.Messages[0];
        var reasoning = Assert.Single(message.Contents.OfType<TextReasoningContent>());
        Assert.Equal("先理解题意，再做运算。", reasoning.Text);
        Assert.Equal("答案是 42", message.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WithoutThinking_ProducesOnlyText()
    {
        var (client, _) = Create("""
            {"model":"llama3","message":{"role":"assistant","content":"普通回答"},"done":true}
            """);

        using var clientScope = client;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Empty(response.Messages[0].Contents.OfType<TextReasoningContent>());
        Assert.Equal("普通回答", response.Messages[0].Text);
    }

    [Fact]
    public async Task GetResponseAsync_DoesNotSendThinkingInRequest()
    {
        var (client, handler) = Create("""
            {"model":"deepseek-r1","message":{"role":"assistant","content":"ok"},"done":true}
            """);

        using var clientScope = client;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.NotNull(handler.LastRequestBody);
        Assert.DoesNotContain("thinking", handler.LastRequestBody);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_SeparatesThinkingAndContent()
    {
        var (client, _) = Create("""
            {"model":"deepseek-r1","message":{"role":"assistant","thinking":"思考一"},"done":false}
            {"model":"deepseek-r1","message":{"role":"assistant","content":"答案"},"done":false}
            {"model":"deepseek-r1","message":{"role":"assistant","content":""},"done":true}
            """);

        using var clientScope = client;

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        Assert.Equal(2, updates.Count);

        var reasoning = Assert.Single(updates[0].Contents!.OfType<TextReasoningContent>());
        Assert.Equal("思考一", reasoning.Text);
        Assert.True(string.IsNullOrEmpty(updates[0].Text));

        Assert.Equal("答案", updates[1].Text);
        Assert.Empty(updates[1].Contents!.OfType<TextReasoningContent>());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmptyChunks_AreNotEmitted()
    {
        var (client, _) = Create("""
            {"model":"llama3","message":{"role":"assistant","content":""},"done":false}
            {"model":"llama3","message":{"role":"assistant","content":"有内容"},"done":true}
            """);

        using var clientScope = client;

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        Assert.Single(updates);
        Assert.Equal("有内容", updates[0].Text);
    }

    [Fact]
    public void Constructor_ReusesProvidedHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://192.168.1.10:11434") };
        var client = new OllamaChatClient(new Uri("http://localhost:11434"), "llama3", http);

        // 已配置的 BaseAddress 不应被构造参数覆盖
        Assert.Equal(new Uri("http://192.168.1.10:11434"), http.BaseAddress);
    }

    [Fact]
    public void Constructor_NullEndpoint_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OllamaChatClient(null!, "llama3"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body) => _body = body;

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
