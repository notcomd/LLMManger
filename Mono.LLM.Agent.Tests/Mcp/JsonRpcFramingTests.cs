using System.Text;
using System.Text.Json;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 stdio 传输的换行分隔 JSON 编解码。
/// </summary>
public class JsonRpcFramingTests
{
    [Fact]
    public async Task WriteAsync_ProducesSingleLineWithLineFeed()
    {
        using var stream = new MemoryStream();

        var message = JsonRpcMessage.CreateRequest(1, McpMethods.Ping);
        await JsonRpcFraming.WriteAsync(stream, message, CancellationToken.None);

        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.Single(text.Split('\n'), line => line.Length > 0);
        Assert.Contains("\"jsonrpc\":\"2.0\"", text);
        Assert.Contains("\"method\":\"ping\"", text);
    }

    [Fact]
    public async Task WriteAsync_OmitsNullMembers()
    {
        using var stream = new MemoryStream();

        await JsonRpcFraming.WriteAsync(
            stream, JsonRpcMessage.CreateNotification(McpMethods.InitializedNotification), CancellationToken.None);

        var text = Encoding.UTF8.GetString(stream.ToArray());

        // 通知不能带 id；响应字段也不应出现。
        Assert.DoesNotContain("\"id\"", text);
        Assert.DoesNotContain("\"result\"", text);
        Assert.DoesNotContain("\"error\"", text);
        Assert.DoesNotContain("null", text);
    }

    [Fact]
    public async Task ReadAsync_RoundTripsRequest()
    {
        using var stream = new MemoryStream();
        await JsonRpcFraming.WriteAsync(
            stream, JsonRpcMessage.CreateRequest(7, McpMethods.ToolsList), CancellationToken.None);
        stream.Position = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var message = await JsonRpcFraming.ReadAsync(reader, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(McpMethods.ToolsList, message!.Method);
        Assert.True(message.IsRequest);
        Assert.Equal("7", JsonRpcMessage.GetIdKey(message.Id!.Value));
    }

    [Fact]
    public async Task ReadAsync_SkipsBlankLines()
    {
        using var reader = new StringReader("\n\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n");

        var message = await JsonRpcFraming.ReadAsync(reader, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal("ping", message!.Method);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullAtEndOfStream()
    {
        using var reader = new StringReader(string.Empty);

        Assert.Null(await JsonRpcFraming.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ThrowsOnMalformedJson()
    {
        using var reader = new StringReader("not-json\n");

        await Assert.ThrowsAsync<JsonException>(
            () => JsonRpcFraming.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public void CreateError_ProducesErrorWithoutResult()
    {
        var id = JsonSerializer.SerializeToElement("abc");
        var message = JsonRpcMessage.CreateError(id, McpErrorCodes.MethodNotFound, "没这个方法");

        Assert.True(message.IsResponse);
        Assert.Null(message.Result);
        Assert.Equal(McpErrorCodes.MethodNotFound, message.Error!.Code);
        Assert.Equal("abc", JsonRpcMessage.GetIdKey(message.Id!.Value));
    }
}
