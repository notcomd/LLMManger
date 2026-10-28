using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 MCP 客户端的握手、发现、调用、错误传播与通知分发。
/// </summary>
public class McpClientTests
{
    private static (McpClient Client, FakeMcpServerTransport Server) CreateClient(
        Action<FakeMcpServerTransport>? configure = null,
        McpClientOptions? options = null)
    {
        var server = new FakeMcpServerTransport();
        configure?.Invoke(server);

        var client = new McpClient(
            server,
            options ?? new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) });

        return (client, server);
    }

    [Fact]
    public async Task ConnectAsync_SendsInitializeAndInitializedNotification()
    {
        var (client, server) = CreateClient();
        await using var keepAlive = client;

        var result = await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Equal(McpProtocol.LatestVersion, result.ProtocolVersion);
        Assert.Equal("fake-server", result.ServerInfo.Name);
        Assert.Equal("fake instructions", client.Instructions);

        var initialize = server.LastRequest(McpMethods.Initialize);
        Assert.NotNull(initialize);
        Assert.Equal(McpProtocol.LatestVersion, initialize!.Params!.Value.GetProperty("protocolVersion").GetString());
        Assert.Equal(
            "Mono.LLM.Agent",
            initialize.Params.Value.GetProperty("clientInfo").GetProperty("name").GetString());

        // 握手完成后必须发送 initialized 通知，否则服务端不会对外提供能力。
        Assert.Single(server.Notifications(McpMethods.InitializedNotification));
    }

    [Fact]
    public async Task ConnectAsync_IsIdempotent()
    {
        var (client, server) = CreateClient();
        await using var keepAlive = client;

        await client.ConnectAsync();
        await client.ConnectAsync();

        Assert.Equal(1, server.CountRequests(McpMethods.Initialize));
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenServerReturnsUnsupportedVersion()
    {
        var (client, _) = CreateClient(server => server.ServerProtocolVersion = "1999-01-01");
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<McpProtocolException>(() => client.ConnectAsync());
        Assert.Contains("1999-01-01", exception.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_DeclaresOnlySupportedCapabilities()
    {
        var (client, server) = CreateClient();
        await using var keepAlive = client;

        await client.ConnectAsync();

        var capabilities = server.LastRequest(McpMethods.Initialize)!
            .Params!.Value.GetProperty("capabilities");

        // 未提供任何服务端请求处理器时，不应声明 roots / sampling / elicitation。
        Assert.False(capabilities.TryGetProperty("sampling", out _));
        Assert.False(capabilities.TryGetProperty("roots", out _));
        Assert.Equal(JsonValueKind.Object, capabilities.ValueKind);
    }

    [Fact]
    public async Task ConnectAsync_DeclaresSampling_WhenHandlerProvided()
    {
        var options = new McpClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            SamplingHandler = (_, _) => Task.FromResult(JsonDocument.Parse("{}").RootElement)
        };

        var (client, server) = CreateClient(options: options);
        await using var keepAlive = client;

        await client.ConnectAsync();

        var capabilities = server.LastRequest(McpMethods.Initialize)!
            .Params!.Value.GetProperty("capabilities");

        Assert.True(capabilities.TryGetProperty("sampling", out _));
    }

    [Fact]
    public async Task ListToolsAsync_FollowsPagination()
    {
        var (client, server) = CreateClient(s =>
        {
            s.PageSize = 2;
            for (var i = 0; i < 5; i++)
                s.Tools.Add(new McpTool { Name = $"tool{i}", Description = $"第 {i} 个工具" });
        });

        await using var keepAlive = client;
        await client.ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(5, tools.Count);
        Assert.Equal(new[] { "tool0", "tool1", "tool2", "tool3", "tool4" }, tools.Select(t => t.Name));
        Assert.Equal(3, server.CountRequests(McpMethods.ToolsList));
    }

    [Fact]
    public async Task CallToolAsync_SendsArgumentsAndReadsTextContent()
    {
        var (client, server) = CreateClient(s => s.CallToolHandler = (name, arguments) =>
            new McpCallToolResult
            {
                Content =
                {
                    new McpContent
                    {
                        Type = "text",
                        Text = $"{name}:{arguments!.Value.GetProperty("city").GetString()}"
                    }
                }
            });

        await using var keepAlive = client;
        await client.ConnectAsync();

        var arguments = JsonNode.Parse("""{"city":"杭州"}""");
        var result = await client.CallToolAsync("weather", arguments);

        Assert.False(result.IsError);
        Assert.Equal("weather:杭州", result.Content[0].Text);

        var request = server.LastRequest(McpMethods.ToolsCall)!;
        Assert.Equal("weather", request.Params!.Value.GetProperty("name").GetString());
        Assert.Equal("杭州", request.Params.Value.GetProperty("arguments").GetProperty("city").GetString());
    }

    [Fact]
    public async Task CallToolAsync_DoesNotThrow_WhenServerReportsBusinessError()
    {
        var (client, _) = CreateClient(s => s.CallToolHandler = (_, _) => new McpCallToolResult
        {
            IsError = true,
            Content = { new McpContent { Type = "text", Text = "文件不存在" } }
        });

        await using var __ = client;
        await client.ConnectAsync();

        var result = await client.CallToolAsync("read_file");

        // isError 属于业务失败，模型可以据此重试，不应升级为协议异常。
        Assert.True(result.IsError);
        Assert.Equal("文件不存在", McpContentFormatter.ToText(result));
    }

    [Fact]
    public async Task SendRequestAsync_PropagatesJsonRpcErrorCode()
    {
        var (client, _) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        var exception = await Assert.ThrowsAsync<McpException>(
            () => client.SendRequestAsync("boom"));

        Assert.Equal(McpErrorCodes.InternalError, exception.Code);
        Assert.Equal("服务端炸了", exception.Message);
    }

    [Fact]
    public async Task SendRequestAsync_ThrowsMethodNotFound_ForUnknownMethod()
    {
        var (client, _) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        var exception = await Assert.ThrowsAsync<McpException>(
            () => client.SendRequestAsync("does/not/exist"));

        Assert.Equal(McpErrorCodes.MethodNotFound, exception.Code);
    }

    [Fact]
    public async Task SendRequestAsync_TimesOut_AndNotifiesCancellation()
    {
        var (client, server) = CreateClient(
            options: new McpClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(80) });

        await using var __ = client;
        await client.ConnectAsync();

        // 握手成功后再屏蔽响应，才能把「超时」与「握手失败」区分开。
        server.SuppressResponses = true;

        await Assert.ThrowsAsync<McpRequestTimeoutException>(
            () => client.SendRequestAsync("slow"));

        // 取消通知是即发即忘的，给它一点调度时间。
        await WaitUntilAsync(() => server.Notifications(McpMethods.CancelledNotification).Count > 0);

        var cancelled = server.Notifications(McpMethods.CancelledNotification)[0];
        Assert.True(cancelled.Params!.Value.TryGetProperty("requestId", out _));
    }

    [Fact]
    public async Task ListChanged_RaisesEvent()
    {
        var (client, server) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        McpListChangedEventArgs? received = null;
        client.ListChanged += (_, args) => received = args;

        await server.NotifyAsync(McpMethods.ToolsListChangedNotification, new JsonObject());

        Assert.NotNull(received);
        Assert.Equal(McpListKind.Tools, received!.Kind);
        Assert.Equal("fake", received.ServerName);
    }

    [Fact]
    public async Task ProgressNotification_IsParsed()
    {
        var (client, server) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        McpProgressEventArgs? received = null;
        client.Progress += (_, args) => received = args;

        await server.NotifyAsync(McpMethods.ProgressNotification, new JsonObject
        {
            ["progressToken"] = "job-1",
            ["progress"] = 3,
            ["total"] = 10,
            ["message"] = "处理中"
        });

        Assert.NotNull(received);
        Assert.Equal("job-1", received!.ProgressToken);
        Assert.Equal(3, received.Progress);
        Assert.Equal(10, received.Total);
        Assert.Equal("处理中", received.Message);
    }

    [Fact]
    public async Task LoggingNotification_IsParsed()
    {
        var (client, server) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        McpLogMessageEventArgs? received = null;
        client.LogMessage += (_, args) => received = args;

        await server.NotifyAsync(McpMethods.LoggingMessageNotification, new JsonObject
        {
            ["level"] = "warning",
            ["logger"] = "fs",
            ["data"] = new JsonObject { ["message"] = "磁盘快满了" }
        });

        Assert.NotNull(received);
        Assert.Equal(McpLogLevel.Warning, received!.Level);
        Assert.Equal("fs", received.Logger);
        Assert.Equal("磁盘快满了", received.Data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CallToolAsync_BeforeConnect_Throws()
    {
        var (client, _) = CreateClient();
        await using var __ = client;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("anything"));
    }

    [Fact]
    public async Task DisposeAsync_FailsInFlightRequest()
    {
        var (client, server) = CreateClient(
            options: new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(30) });

        await client.ConnectAsync();
        server.SuppressResponses = true;

        var pending = client.SendRequestAsync("slow");
        await Task.Delay(30);
        await client.DisposeAsync();

        await Assert.ThrowsAsync<McpConnectionClosedException>(() => pending);
        Assert.True(server.IsClosed);
    }

    [Fact]
    public async Task TransportClose_SurfacesAsConnectionClosed_AndResetsState()
    {
        var (client, server) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        await server.CloseAsync();

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListToolsAsync());
    }

    [Fact]
    public async Task ListResourcesAndPrompts_AreParsed()
    {
        var (client, _) = CreateClient();
        await using var __ = client;
        await client.ConnectAsync();

        var resources = await client.ListResourcesAsync();
        var prompts = await client.ListPromptsAsync();

        Assert.Single(resources);
        Assert.Equal("file:///a.txt", resources[0].Uri);
        Assert.Single(prompts);
        Assert.Equal("greet", prompts[0].Name);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.Fail("等待条件超时。");
    }
}
