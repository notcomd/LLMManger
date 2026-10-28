using System.Text.Json;
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tests.Tools;

/// <summary>
/// 覆盖工具注册、JSON Schema 生成与执行路径。
/// </summary>
public class ToolManagerTests
{
    private static JsonElement SchemaOf(AITool tool) =>
        JsonDocument.Parse(((AIFunction)tool).JsonSchema.GetRawText()).RootElement;

    [Fact]
    public void RegisterTool_ExposesEveryDeclaredParameter()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "book_flight",
            "预订机票",
            new[]
            {
                new ToolParameterInfo { Name = "city", Type = "string", Description = "目的城市", Required = true },
                new ToolParameterInfo { Name = "date", Type = "string", Description = "出发日期", Required = true },
                new ToolParameterInfo { Name = "passengers", Type = "int", Description = "人数", Required = true }
            },
            (_, _) => Task.FromResult(new ToolResult { Name = "book_flight", Result = "ok" }));

        var schema = SchemaOf(manager.GetAITools()[0]);
        var properties = schema.GetProperty("properties");

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(3, properties.EnumerateObject().Count());
        Assert.Equal("string", properties.GetProperty("city").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("passengers").GetProperty("type").GetString());
    }

    [Fact]
    public void RegisterTool_RequiredListContainsOnlyRequiredParameters()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "t",
            "d",
            new[]
            {
                new ToolParameterInfo { Name = "a", Type = "string", Required = true },
                new ToolParameterInfo { Name = "b", Type = "string", Required = false }
            },
            (_, _) => Task.FromResult(new ToolResult { Name = "t", Result = "ok" }));

        var required = SchemaOf(manager.GetAITools()[0])
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.Equal(new[] { "a" }, required);
    }

    [Theory]
    [InlineData("int", "integer")]
    [InlineData("integer", "integer")]
    [InlineData("long", "integer")]
    [InlineData("float", "number")]
    [InlineData("double", "number")]
    [InlineData("bool", "boolean")]
    [InlineData("boolean", "boolean")]
    [InlineData("list", "array")]
    [InlineData("", "string")]
    public void RegisterTool_NormalizesTypeAliases(string declared, string expected)
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "t",
            "d",
            new[] { new ToolParameterInfo { Name = "p", Type = declared, Required = true } },
            (_, _) => Task.FromResult(new ToolResult { Name = "t", Result = "ok" }));

        var type = SchemaOf(manager.GetAITools()[0])
            .GetProperty("properties")
            .GetProperty("p")
            .GetProperty("type")
            .GetString();

        Assert.Equal(expected, type);
    }

    [Fact]
    public void RegisterTool_KeepsEnumValuesAndDescriptions()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "t",
            "说明",
            new[]
            {
                new ToolParameterInfo
                {
                    Name = "cabin",
                    Type = "string",
                    Description = "舱位",
                    Required = false,
                    EnumValues = new List<string> { "经济舱", "商务舱" }
                }
            },
            (_, _) => Task.FromResult(new ToolResult { Name = "t", Result = "ok" }));

        var cabin = SchemaOf(manager.GetAITools()[0]).GetProperty("properties").GetProperty("cabin");

        Assert.Equal("舱位", cabin.GetProperty("description").GetString());
        Assert.Equal(2, cabin.GetProperty("enum").GetArrayLength());
    }

    [Fact]
    public void RegisterSimpleTool_ExposesSingleInputParameter()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("now", "当前时间", _ => Task.FromResult("2026-01-01"));

        var schema = SchemaOf(manager.GetAITools()[0]);

        Assert.True(schema.GetProperty("properties").TryGetProperty("input", out var input));
        Assert.Equal("string", input.GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ForwardsStructuredArgumentsToHandler()
    {
        var manager = new ToolManager();
        string? received = null;

        manager.RegisterTool(
            "echo",
            "回显",
            new[]
            {
                new ToolParameterInfo { Name = "city", Type = "string", Required = true },
                new ToolParameterInfo { Name = "count", Type = "int", Required = true }
            },
            (json, _) =>
            {
                received = json.GetRawText();
                return Task.FromResult(new ToolResult { Name = "echo", Result = "done" });
            });

        var function = (AIFunction)manager.GetAITools()[0];
        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["city"] = "杭州", ["count"] = 3 },
            CancellationToken.None);

        Assert.Equal("done", result);
        Assert.NotNull(received);
        Assert.Contains("\"city\"", received);
        Assert.Contains("\"count\":3", received);
    }

    [Fact]
    public async Task InvokeAsync_WhenHandlerFails_ReturnsErrorTextInsteadOfThrowing()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "boom",
            "d",
            Array.Empty<ToolParameterInfo>(),
            (_, _) => Task.FromResult(new ToolResult { Name = "boom", IsSuccess = false, Error = "业务失败" }));

        var function = (AIFunction)manager.GetAITools()[0];
        var result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Equal("Error: 业务失败", result);
    }

    [Fact]
    public async Task ExecuteToolAsync_UnknownTool_ReturnsFailure()
    {
        var manager = new ToolManager();

        var result = await manager.ExecuteToolAsync("missing", "{}");

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ExecuteToolAsync_InvalidJson_ReturnsFailureInsteadOfThrowing()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("noop", "d", _ => Task.FromResult("ok"));

        var result = await manager.ExecuteToolAsync("noop", "{not json");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public void RegisterTool_SameNameTwice_ReplacesPrevious()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("dup", "first", _ => Task.FromResult("1"));
        manager.RegisterSimpleTool("dup", "second", _ => Task.FromResult("2"));

        Assert.Single(manager.Tools);
        Assert.Equal("second", manager.Tools[0].Description);
    }

    [Fact]
    public async Task ExecuteToolAsync_HandlerThrows_IsCapturedAsFailure()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("throwing", "d", _ => throw new InvalidOperationException("炸了"), requiresInput: false);

        var result = await manager.ExecuteToolAsync("throwing", "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal("炸了", result.Error);
    }
}
