using System.Text.Json;
using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Mcp;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 MCP 工具到 <see cref="ToolManager"/> 的桥接：命名、Schema 保真、调用转发与刷新。
/// </summary>
public class McpToolBridgeTests
{
    private const string NestedSchema = """
    {
      "type": "object",
      "properties": {
        "query": { "type": "string", "description": "检索词" },
        "filter": {
          "type": "object",
          "properties": {
            "lang": { "type": "string", "enum": ["zh", "en"] },
            "limit": { "type": "integer", "default": 5 }
          },
          "required": ["lang"]
        }
      },
      "required": ["query"]
    }
    """;

    private static async Task<(McpClient Client, FakeMcpServerTransport Server, ToolManager Manager)> CreateConnectedAsync(
        IEnumerable<McpTool> tools)
    {
        var server = new FakeMcpServerTransport("github");
        foreach (var tool in tools)
            server.Tools.Add(tool);

        var client = new McpClient(server, new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
        await client.ConnectAsync();

        return (client, server, new ToolManager());
    }

    private static JsonElement SchemaOf(ToolManager manager, string name) =>
        manager.GetAITools()
            .Cast<AIFunction>()
            .First(t => t.Name == name)
            .JsonSchema;

    [Fact]
    public async Task RegisterToolsAsync_PrefixesNamesAndKeepsNestedSchema()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool
            {
                Name = "search",
                Description = "检索仓库代码",
                InputSchema = JsonDocument.Parse(NestedSchema).RootElement
            }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions
        {
            ToolNamePrefix = "github"
        });

        var names = await bridge.RegisterToolsAsync();

        Assert.Equal(new[] { "github__search" }, names);

        var schema = SchemaOf(manager, "github__search");
        var filter = schema.GetProperty("properties").GetProperty("filter");

        // 嵌套对象必须完整保留，否则模型看不到 lang 的枚举约束。
        Assert.Equal("object", filter.GetProperty("type").GetString());
        Assert.Equal("zh", filter.GetProperty("properties").GetProperty("lang").GetProperty("enum")[0].GetString());
        Assert.Equal("query", schema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task RegisterToolsAsync_RespectsFilter()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "read" },
            new McpTool { Name = "delete" }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions
        {
            ToolNamePrefix = "repo",
            Filter = tool => tool.Name != "delete"
        });

        var names = await bridge.RegisterToolsAsync();

        Assert.Equal(new[] { "repo__read" }, names);
        Assert.Single(manager.Tools);
    }

    [Fact]
    public async Task RegisterToolsAsync_SanitizesIllegalCharacters()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "fs.read:file" }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions
        {
            ToolNamePrefix = "local"
        });

        var names = await bridge.RegisterToolsAsync();

        Assert.Equal(new[] { "local__fs_read_file" }, names);
    }

    [Fact]
    public async Task RegisteredTool_ForwardsArgumentsAndReturnsText()
    {
        var (client, server, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool
            {
                Name = "search",
                Description = "检索",
                InputSchema = JsonDocument.Parse(NestedSchema).RootElement
            }
        });

        await using var __ = client;

        server.CallToolHandler = (name, arguments) => new McpCallToolResult
        {
            Content =
            {
                new McpContent
                {
                    Type = "text",
                    Text = $"{name}|{arguments!.Value.GetProperty("query").GetString()}"
                }
            }
        };

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "gh" });
        await bridge.RegisterToolsAsync();

        var result = await manager.ExecuteToolAsync("gh__search", """{"query":"mcp"}""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("search|mcp", result.Result);

        // 服务端收到的必须是原始工具名，不能带上前缀。
        Assert.Equal("search", server.LastRequest(McpMethods.ToolsCall)!.Params!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RegisteredTool_ReportsBusinessErrorAsFailedResult()
    {
        var (client, server, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "read" }
        });

        await using var __ = client;

        server.CallToolHandler = (_, _) => new McpCallToolResult
        {
            IsError = true,
            Content = { new McpContent { Type = "text", Text = "权限不足" } }
        };

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "fs" });
        await bridge.RegisterToolsAsync();

        var result = await manager.ExecuteToolAsync("fs__read", "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal("权限不足", result.Error);
    }

    [Fact]
    public async Task RefreshAsync_RemovesDisappearedTools()
    {
        var (client, server, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "alpha" },
            new McpTool { Name = "beta" }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "s" });
        await bridge.RegisterToolsAsync();
        Assert.Equal(2, manager.Tools.Count);

        server.Tools.RemoveAll(t => t.Name == "beta");
        server.Tools.Add(new McpTool { Name = "gamma" });

        var names = await bridge.RefreshAsync();

        Assert.Equal(new[] { "s__alpha", "s__gamma" }, names.OrderBy(n => n));
        Assert.True(manager.IsRegistered("s__alpha"));
        Assert.False(manager.IsRegistered("s__beta"));
        Assert.True(manager.IsRegistered("s__gamma"));
    }

    [Fact]
    public async Task RemoveAll_UnregistersEverything()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "alpha" }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "s" });
        await bridge.RegisterToolsAsync();

        var removed = bridge.RemoveAll();

        Assert.Equal(1, removed);
        Assert.Empty(manager.Tools);
        Assert.Empty(bridge.RegisteredToolNames);
    }

    [Fact]
    public async Task DuplicateExposedNames_Throw()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "a.b" },
            new McpTool { Name = "a:b" }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "s" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.RegisterToolsAsync());
    }

    [Fact]
    public async Task GetAITools_ExposesMcpToolToModel()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool
            {
                Name = "ping",
                Description = "探活",
                InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement
            }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "srv" });
        await bridge.RegisterToolsAsync();

        var tool = manager.GetAITools().Cast<AIFunction>().Single();

        Assert.Equal("srv__ping", tool.Name);
        Assert.Contains("探活", tool.Description);
        Assert.Contains("github", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
    }

    [Fact]
    public async Task ToolWithoutSchema_FallsBackToEmptyObjectSchema()
    {
        var (client, _, manager) = await CreateConnectedAsync(new[]
        {
            new McpTool { Name = "bare", InputSchema = null }
        });

        await using var __ = client;

        var bridge = new McpToolBridge(manager, client, new McpToolBridgeOptions { ToolNamePrefix = "s" });
        await bridge.RegisterToolsAsync();

        var schema = SchemaOf(manager, "s__bare");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("properties", out var properties) &&
                     properties.EnumerateObject().Any());
    }
}
