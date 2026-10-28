using System.Text.Json;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 MCP 内容块到模型可读文本的渲染规则。
/// </summary>
public class McpContentFormatterTests
{
    [Fact]
    public void ToText_JoinsTextBlocksWithNewLines()
    {
        var result = new McpCallToolResult
        {
            Content =
            {
                new McpContent { Type = "text", Text = "第一行" },
                new McpContent { Type = "text", Text = "第二行" }
            }
        };

        Assert.Equal("第一行\n第二行", McpContentFormatter.ToText(result));
    }

    [Fact]
    public void ToText_SummarizesBinaryBlocksWithoutBase64()
    {
        var result = new McpCallToolResult
        {
            Content =
            {
                new McpContent { Type = "image", MimeType = "image/png", Data = new string('A', 4000) },
                new McpContent { Type = "audio", MimeType = "audio/wav", Data = "AAAA" }
            }
        };

        var text = McpContentFormatter.ToText(result);

        Assert.Contains("[image: image/png", text);
        Assert.Contains("KB", text);
        Assert.DoesNotContain(new string('A', 100), text);
    }

    [Fact]
    public void ToText_EmbedsTextResourceBody()
    {
        var result = new McpCallToolResult
        {
            Content =
            {
                new McpContent
                {
                    Type = "resource",
                    Resource = new McpResourceContents
                    {
                        Uri = "file:///readme.md",
                        MimeType = "text/markdown",
                        Text = "# 标题"
                    }
                }
            }
        };

        var text = McpContentFormatter.ToText(result);

        Assert.Contains("file:///readme.md", text);
        Assert.Contains("# 标题", text);
    }

    [Fact]
    public void ToText_SummarizesBlobResource()
    {
        var result = new McpCallToolResult
        {
            Content =
            {
                new McpContent
                {
                    Type = "resource",
                    Resource = new McpResourceContents
                    {
                        Uri = "file:///a.bin",
                        MimeType = "application/octet-stream",
                        Blob = "AAAA"
                    }
                }
            }
        };

        Assert.Contains("[binary resource: file:///a.bin", McpContentFormatter.ToText(result));
    }

    [Fact]
    public void ToText_FallsBackToStructuredContent()
    {
        var result = new McpCallToolResult
        {
            StructuredContent = JsonDocument.Parse("""{"count":2}""").RootElement
        };

        Assert.Equal("""{"count":2}""", McpContentFormatter.ToText(result));
    }

    [Fact]
    public void ToToolResult_MapsSuccess()
    {
        var result = new McpCallToolResult
        {
            Content = { new McpContent { Type = "text", Text = "ok" } }
        };

        var toolResult = McpContentFormatter.ToToolResult("s__t", result);

        Assert.True(toolResult.IsSuccess);
        Assert.Equal("ok", toolResult.Result);
        Assert.Null(toolResult.Error);
        Assert.Equal("s__t", toolResult.Name);
    }

    [Fact]
    public void ToToolResult_MapsBusinessError()
    {
        var result = new McpCallToolResult
        {
            IsError = true,
            Content = { new McpContent { Type = "text", Text = "参数不对" } }
        };

        var toolResult = McpContentFormatter.ToToolResult("s__t", result);

        Assert.False(toolResult.IsSuccess);
        Assert.Equal("参数不对", toolResult.Error);
        Assert.Equal(string.Empty, toolResult.Result);
    }

    [Fact]
    public void ToToolResult_UsesFallbackMessage_WhenErrorHasNoContent()
    {
        var toolResult = McpContentFormatter.ToToolResult("s__t", new McpCallToolResult { IsError = true });

        Assert.False(toolResult.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(toolResult.Error));
    }
}
