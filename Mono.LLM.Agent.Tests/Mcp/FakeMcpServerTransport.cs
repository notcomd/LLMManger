using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 进程内的假 MCP 服务器，用于验证客户端的协议行为。
/// </summary>
/// <remarks>
/// 它实现 <see cref="IMcpTransport"/>，在收到请求后立即构造响应并回调客户端，
/// 因此测试不依赖网络、子进程或外部服务——与仓库既有的「测试不联网」约定一致。
/// </remarks>
internal sealed class FakeMcpServerTransport : IMcpTransport
{
    private readonly object _receivedLock = new();

    public FakeMcpServerTransport(string name = "fake")
    {
        Name = name;
    }

    public string Name { get; }

    public Func<JsonRpcMessage, Task>? MessageHandler { get; set; }

    public string? ProtocolVersion { get; set; }

    public bool IsStarted { get; private set; }

    public bool IsClosed { get; private set; }

    /// <summary>客户端发来的全部消息（含通知）。</summary>
    public List<JsonRpcMessage> Received { get; } = new();

    /// <summary>服务端返回给客户端的全部消息。</summary>
    public List<JsonRpcMessage> Delivered { get; } = new();

    public string ServerProtocolVersion { get; set; } = McpProtocol.LatestVersion;

    public string ServerName { get; set; } = "fake-server";

    public List<McpTool> Tools { get; } = new();

    /// <summary>工具调用处理器；为 <c>null</c> 时返回固定文本。</summary>
    public Func<string, JsonElement?, McpCallToolResult>? CallToolHandler { get; set; }

    /// <summary>每页的工具数量，用于验证分页。</summary>
    public int PageSize { get; set; } = 2;

    /// <summary>为 <c>true</c> 时不响应任何请求，用于验证超时路径。</summary>
    public bool SuppressResponses { get; set; }

    public event EventHandler<McpTransportClosedEventArgs>? Closed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        lock (_receivedLock)
            Received.Add(message);

        // 通知与客户端回送的响应都不需要 fake 再回应。
        if (!message.IsRequest || SuppressResponses)
            return;

        var response = BuildResponse(message);
        await DeliverAsync(response).ConfigureAwait(false);
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed)
            return Task.CompletedTask;

        IsClosed = true;
        Closed?.Invoke(this, new McpTransportClosedEventArgs { Reason = "测试主动关闭。" });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>主动向客户端推送一条通知。</summary>
    public Task NotifyAsync(string method, JsonNode? parameters = null)
        => DeliverAsync(JsonRpcMessage.CreateNotification(method, parameters));

    /// <summary>查找客户端发来的指定方法的最后一条消息。</summary>
    public JsonRpcMessage? LastRequest(string method)
    {
        lock (_receivedLock)
        {
            return Received.LastOrDefault(
                m => m.Method == method && m.IsRequest);
        }
    }

    public int CountRequests(string method)
    {
        lock (_receivedLock)
            return Received.Count(m => m.Method == method && m.IsRequest);
    }

    public IReadOnlyList<JsonRpcMessage> Notifications(string method)
    {
        lock (_receivedLock)
            return Received.Where(m => m.Method == method && m.IsNotification).ToList();
    }

    private async Task DeliverAsync(JsonRpcMessage message)
    {
        Delivered.Add(message);

        var handler = MessageHandler;
        if (handler is not null)
            await handler(message).ConfigureAwait(false);
    }

    private JsonRpcMessage BuildResponse(JsonRpcMessage message)
    {
        var id = message.Id!.Value;
        var parameters = message.Params;

        switch (message.Method)
        {
            case McpMethods.Initialize:
                return JsonRpcMessage.CreateResult(id, new JsonObject
                {
                    ["protocolVersion"] = ServerProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = true },
                        ["resources"] = new JsonObject(),
                        ["prompts"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = "9.9.9"
                    },
                    ["instructions"] = "fake instructions"
                });

            case McpMethods.ToolsList:
                return JsonRpcMessage.CreateResult(id, BuildToolPage(parameters));

            case McpMethods.ToolsCall:
                return JsonRpcMessage.CreateResult(id, BuildToolCallResult(parameters));

            case McpMethods.ResourcesList:
                return JsonRpcMessage.CreateResult(id, new JsonObject
                {
                    ["resources"] = new JsonArray
                    {
                        JsonSerializer.SerializeToNode(
                            new McpResource { Uri = "file:///a.txt", Name = "a", MimeType = "text/plain" },
                            McpJson.Options)
                    }
                });

            case McpMethods.PromptsList:
                return JsonRpcMessage.CreateResult(id, new JsonObject
                {
                    ["prompts"] = new JsonArray
                    {
                        JsonSerializer.SerializeToNode(
                            new McpPrompt { Name = "greet", Description = "打招呼" },
                            McpJson.Options)
                    }
                });

            case McpMethods.Ping:
                return JsonRpcMessage.CreateResult(id, new JsonObject());

            case "boom":
                return JsonRpcMessage.CreateError(id, McpErrorCodes.InternalError, "服务端炸了");

            default:
                return JsonRpcMessage.CreateError(
                    id, McpErrorCodes.MethodNotFound, $"未实现的方法：{message.Method}");
        }
    }

    private JsonNode BuildToolPage(JsonElement? parameters)
    {
        var cursor = ReadString(parameters, "cursor");
        var start = int.TryParse(cursor, out var parsed) ? parsed : 0;
        var size = Math.Max(1, PageSize);
        var page = Tools.Skip(start).Take(size).ToList();

        var array = new JsonArray();
        foreach (var tool in page)
            array.Add(JsonSerializer.SerializeToNode(tool, McpJson.Options));

        var result = new JsonObject { ["tools"] = array };

        var next = start + page.Count;
        if (next < Tools.Count)
            result["nextCursor"] = next.ToString();

        return result;
    }

    private JsonNode BuildToolCallResult(JsonElement? parameters)
    {
        var name = ReadString(parameters, "name") ?? string.Empty;
        var arguments = ReadProperty(parameters, "arguments");

        var result = CallToolHandler?.Invoke(name, arguments)
                     ?? new McpCallToolResult
                     {
                         Content =
                         {
                             new McpContent { Type = "text", Text = $"echo:{name}" }
                         }
                     };

        return JsonSerializer.SerializeToNode(result, McpJson.Options)!;
    }

    private static JsonElement? ReadProperty(JsonElement? source, string propertyName)
    {
        if (source is not { ValueKind: JsonValueKind.Object } element)
            return null;

        return element.TryGetProperty(propertyName, out var value) ? value.Clone() : null;
    }

    private static string? ReadString(JsonElement? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        if (value is not { ValueKind: JsonValueKind.String } text)
            return null;

        return text.GetString();
    }
}
