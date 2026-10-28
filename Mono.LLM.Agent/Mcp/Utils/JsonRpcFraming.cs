// ============================================================================
// 文件：JsonRpcFraming.cs
// 用途：stdio 传输使用的换行分隔 JSON 编解码。
// 实现方：JsonRpcFraming
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;
using System.Text.Json;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// stdio 传输使用的换行分隔 JSON 编解码。
/// </summary>
/// <remarks>
/// <para>
/// MCP 的 stdio 传输规定：每条消息是一个单行 JSON，以 <c>\n</c> 分隔，消息内部不得包含换行。
/// 因此不能使用缩进序列化，也不能依赖平台的 <c>NewLine</c>（Windows 下为 <c>\r\n</c>，
/// 虽然多数服务端能容忍，但严格的实现会因此报错）。
/// </para>
/// <para>
/// 写入直接走字节流，避免 <see cref="StreamWriter"/> 的 BOM 与缓冲问题；
/// 读取走 <see cref="TextReader"/>，调用方可自行决定缓冲策略。
/// </para>
/// </remarks>
internal static class JsonRpcFraming
{
    private static readonly byte[] LineFeed = "\n"u8.ToArray();

    /// <summary>把消息序列化为单行 JSON 并写入流。</summary>
    /// <param name="stream">目标流（通常是子进程的标准输入）。</param>
    /// <param name="message">待发送的消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task WriteAsync(Stream stream, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, McpJson.Options);

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(LineFeed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取一行并反序列化为消息；流结束时返回 <c>null</c>。</summary>
    /// <param name="reader">消息来源（通常是子进程的标准输出）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的消息；到达流末尾时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 空行会被跳过——部分服务端在启动时会输出空行，这不属于协议错误；
    /// 而非 JSON 的行会抛出 <see cref="JsonException"/>，由调用方决定记录日志还是终止连接。
    /// </remarks>
    public static async Task<JsonRpcMessage?> ReadAsync(TextReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            return JsonSerializer.Deserialize<JsonRpcMessage>(line, McpJson.Options)
                   ?? throw new JsonException("JSON-RPC 消息解析结果为空。");
        }
    }

    /// <summary>把消息序列化为单行字符串（调试与日志用）。</summary>
    public static string Serialize(JsonRpcMessage message) =>
        JsonSerializer.Serialize(message, McpJson.Options);

    /// <summary>UTF-8（无 BOM）编码，供子进程重定向流使用。</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
