// ============================================================================
// 文件：McpContentFormatter.cs
// 用途：把 MCP 的内容块渲染成模型可读的文本，并映射为 ToolResult。
// 实现方：McpContentFormatter
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;
using System.Text.Json;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 把 MCP 的内容块渲染成模型可读的文本，并映射为 <see cref="ToolResult"/>。
/// </summary>
/// <remarks>
/// <para>
/// MCP 的 <c>tools/call</c> 返回的是内容块数组，而工具调用链路只消费纯文本。
/// 这个转换层决定模型最终「看到」什么，因此取舍如下：
/// </para>
/// <list type="bullet">
///   <item>文本块原样透传——这是绝大多数工具的返回形式，不应有任何加工；</item>
///   <item>图片、音频只给一行摘要（类型、MIME、大小），不塞 base64，否则会瞬间吃光上下文；</item>
///   <item>内嵌文本资源带上 URI 与正文，二进制资源给摘要；</item>
///   <item>没有任何文本块时，退回 <c>structuredContent</c> 的 JSON，保证模型不会收到空字符串。</item>
/// </list>
/// </remarks>
public static class McpContentFormatter
{
    /// <summary>把工具调用结果渲染为纯文本。</summary>
    /// <param name="result">服务端返回的工具调用结果。</param>
    /// <returns>以换行分隔的文本；无内容时返回空字符串。</returns>
    public static string ToText(McpCallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();

        // 服务端可能显式返回 "content": null，此时反序列化会覆盖掉默认的空列表。
        foreach (var block in result.Content ?? Enumerable.Empty<McpContent>())
        {
            if (block is null)
                continue;

            AppendBlock(builder, block);
        }

        if (builder.Length == 0 && result.StructuredContent is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured)
            builder.Append(JsonSerializer.Serialize(structured, McpJson.Options));

        return builder.ToString().TrimEnd();
    }

    /// <summary>把工具调用结果映射为统一的 <see cref="ToolResult"/>。</summary>
    /// <param name="toolName">工具名称，写入结果的 <see cref="ToolResult.Name"/>。</param>
    /// <param name="result">服务端返回的工具调用结果。</param>
    /// <param name="callId">可选的调用标识。</param>
    /// <remarks>
    /// 服务端把业务失败放在 <see cref="McpCallToolResult.IsError"/> 中，
    /// 映射后成为 <see cref="ToolResult.IsSuccess"/> 为 <c>false</c> 且
    /// <see cref="ToolResult.Error"/> 携带服务端给出的说明，让模型有机会自行纠正后重试。
    /// </remarks>
    public static ToolResult ToToolResult(string toolName, McpCallToolResult result, string? callId = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = ToText(result);

        return new ToolResult
        {
            Name = toolName,
            ToolCallId = callId ?? string.Empty,
            IsSuccess = !result.IsError,
            Result = result.IsError ? string.Empty : text,
            Error = result.IsError
                ? (string.IsNullOrWhiteSpace(text) ? "MCP 工具报告执行失败，但没有提供错误说明。" : text)
                : null
        };
    }

    private static void AppendBlock(StringBuilder builder, McpContent block)
    {
        switch (block.Type)
        {
            case "text":
                AppendLine(builder, block.Text);
                break;

            case "image":
                AppendLine(builder, $"[image: {block.MimeType ?? "unknown type"}, {DescribeBase64Size(block.Data)}]");
                break;

            case "audio":
                AppendLine(builder, $"[audio: {block.MimeType ?? "unknown type"}, {DescribeBase64Size(block.Data)}]");
                break;

            case "resource":
                AppendResource(builder, block);
                break;

            case "resource_link":
                AppendLine(builder, $"[resource link: {block.Name ?? "(unnamed)"} <{block.Uri}>]");
                break;

            default:
                // 未知类型保留痕迹而不是静默丢弃，便于排查对端新增的扩展内容。
                AppendLine(builder, $"[{block.Type} content]");
                break;
        }
    }

    private static void AppendResource(StringBuilder builder, McpContent block)
    {
        var resource = block.Resource;

        if (resource is null)
        {
            AppendLine(builder, $"[resource: {block.Uri ?? "unknown"}]");
            return;
        }

        if (resource.Text is not null)
        {
            AppendLine(builder, $"[resource: {resource.Uri}]");
            AppendLine(builder, resource.Text);
            return;
        }

        AppendLine(
            builder,
            $"[binary resource: {resource.Uri}, {resource.MimeType ?? "unknown type"}, {DescribeBase64Size(resource.Blob)}]");
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        if (builder.Length > 0)
            builder.Append('\n');

        builder.Append(line);
    }

    /// <summary>按 base64 长度估算原始字节数，避免把整段内容解出来。</summary>
    private static string DescribeBase64Size(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return "0 B";

        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64.EndsWith('=') ? 1
            : 0;

        var bytes = Math.Max(0, base64.Length / 4 * 3 - padding);
        return bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";
    }
}
