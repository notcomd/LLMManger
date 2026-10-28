using System.Text.Json;
using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Tools;

/// <summary>
/// 覆盖原始 JSON Schema 工具的注册、Schema 透传与注销。
/// </summary>
public class ToolManagerRawToolTests
{
    private static JsonElement SchemaOf(ToolManager manager, string name) =>
        manager.GetAITools()
            .Cast<AIFunction>()
            .First(t => t.Name == name)
            .JsonSchema;

    [Fact]
    public void RegisterRawTool_PassesNestedSchemaThrough()
    {
        var manager = new ToolManager();

        manager.RegisterRawTool(
            "search",
            "检索",
            JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "filter": {
                  "type": "object",
                  "properties": { "limit": { "type": "integer" } }
                }
              }
            }
            """).RootElement,
            (_, _) => Task.FromResult(new ToolResult { Name = "search", Result = "ok" }));

        var schema = SchemaOf(manager, "search");

        Assert.Equal("object", schema.GetProperty("properties").GetProperty("filter").GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").GetProperty("filter").TryGetProperty("properties", out _));
    }

    [Fact]
    public void RegisterRawTool_ClonesSchema_SoCallerCanDisposeDocument()
    {
        var manager = new ToolManager();

        using (var document = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""))
        {
            manager.RegisterRawTool(
                "t", "d", document.RootElement,
                (_, _) => Task.FromResult(new ToolResult { Name = "t" }));
        }

        // 原文档已释放，克隆后的 Schema 仍应可读。
        var schema = SchemaOf(manager, "t");
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("q").GetProperty("type").GetString());
    }

    [Fact]
    public async Task RegisterRawTool_SkipsLocalArgumentValidation()
    {
        var manager = new ToolManager();
        var invoked = false;

        manager.RegisterRawTool(
            "t",
            "d",
            JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""").RootElement,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(new ToolResult { Name = "t", Result = "ok" });
            });

        // 本地校验器不理解原始 Schema，因此不应拦截缺少必填参数的调用。
        var result = await manager.ExecuteToolAsync("t", "{}");

        Assert.True(invoked);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void RegisterRawTool_WithUndefinedSchema_FallsBackToEmptyObject()
    {
        var manager = new ToolManager();

        manager.RegisterRawTool(
            "t", "d", default,
            (_, _) => Task.FromResult(new ToolResult { Name = "t" }));

        var schema = SchemaOf(manager, "t");

        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void UnregisterTool_RemovesRegistration()
    {
        var manager = new ToolManager();
        manager.RegisterRawTool(
            "t", "d", JsonDocument.Parse("""{"type":"object"}""").RootElement,
            (_, _) => Task.FromResult(new ToolResult { Name = "t" }));

        Assert.True(manager.IsRegistered("t"));
        Assert.True(manager.UnregisterTool("t"));
        Assert.False(manager.IsRegistered("t"));
        Assert.False(manager.UnregisterTool("t"));
        Assert.Empty(manager.GetAITools());
    }

    [Fact]
    public void RegisterTool_StillUsesParameterSchema()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "legacy",
            "d",
            new[] { new ToolParameterInfo { Name = "city", Type = "string", Required = true } },
            (_, _) => Task.FromResult(new ToolResult { Name = "legacy" }));

        var schema = SchemaOf(manager, "legacy");

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("city").GetProperty("type").GetString());
    }
}
