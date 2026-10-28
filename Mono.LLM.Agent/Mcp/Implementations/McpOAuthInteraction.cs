// ============================================================================
// 文件：McpOAuthInteraction.cs
// 用途：OAuth 授权的用户交互实现：回环监听与委托回调。
// 实现方：McpOAuthInteraction
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 通过本机回环地址接收授权回调的交互实现，是面向最终用户的默认实现。
/// </summary>
/// <remarks>
/// <para>
/// 流程：先在本机起一个临时 HTTP 服务，把 <c>http://127.0.0.1:{port}/callback</c> 作为
/// <c>redirect_uri</c> 交给授权服务器，再打开浏览器让用户完成登录与授权；
/// 浏览器跳回后从查询串里取 <c>code</c> 与 <c>state</c>。
/// </para>
/// <para>
/// 用 <see cref="TcpListener"/> 而不是 <see cref="HttpListener"/> 是因为后者在 Windows 上
/// 对非管理员账户的 URL 前缀有额外限制；裸 socket 只读一行请求头，够用且没有权限问题。
/// 端口在构造时选定，因为动态客户端注册必须在打开浏览器之前就知道回调地址。
/// </para>
/// <para>
/// 浏览器可能先请求 <c>/favicon.ico</c> 之类的路径，因此回调服务会跳过路径不匹配的请求继续等待。
/// </para>
/// </remarks>
public sealed class LoopbackOAuthInteraction : IMcpOAuthInteraction
{
    private readonly bool _openBrowser;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;
    private readonly string _path;

    /// <summary>初始化回环交互。</summary>
    /// <param name="redirectUri">自定义回调地址，必须是回环地址；为 <c>null</c> 时自动分配空闲端口。</param>
    /// <param name="openBrowser">是否自动打开浏览器。</param>
    /// <param name="timeout">等待回调的超时时间。</param>
    /// <param name="logger">可选的日志记录器。</param>
    public LoopbackOAuthInteraction(
        Uri? redirectUri = null,
        bool openBrowser = true,
        TimeSpan? timeout = null,
        ILogger? logger = null)
    {
        var target = redirectUri ?? new Uri($"http://127.0.0.1:{FindFreePort()}/callback");

        if (!target.IsLoopback)
        {
            throw new ArgumentException(
                $"OAuth 回调地址必须是回环地址（127.0.0.1 或 localhost），当前为 {target}。", nameof(redirectUri));
        }

        RedirectUri = target;
        _path = string.IsNullOrEmpty(target.AbsolutePath) || target.AbsolutePath == "/"
            ? "/"
            : target.AbsolutePath.TrimEnd('/');
        _openBrowser = openBrowser;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _logger = logger;
    }

    /// <inheritdoc />
    public Uri RedirectUri { get; }

    /// <inheritdoc />
    public async Task<Uri> AuthorizeAsync(
        Func<Uri, Uri> createAuthorizationUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createAuthorizationUrl);

        using var listener = new TcpListener(IPAddress.Loopback, RedirectUri.Port);
        listener.Start();

        try
        {
            var authorizationUrl = createAuthorizationUrl(RedirectUri);

            // 即使不开浏览器也要打日志：无图形环境时用户需要手动复制这个地址。
            _logger?.LogInformation("请在浏览器中完成 MCP 授权：{Url}", authorizationUrl);

            if (_openBrowser)
                TryOpenBrowser(authorizationUrl);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(timeoutCts.Token).ConfigureAwait(false);
                    await using var stream = client.GetStream();

                    var requestLine = await ReadRequestLineAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                    var target = ParseRequestTarget(requestLine);

                    if (!IsCallbackTarget(target))
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", "这个地址不是 OAuth 回调。", timeoutCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await WriteResponseAsync(
                            stream,
                            200,
                            "授权完成",
                            "授权已完成，可以关闭此页面并回到终端。",
                            timeoutCts.Token)
                        .ConfigureAwait(false);

                    return new Uri($"http://127.0.0.1:{RedirectUri.Port}{target}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpOAuthException(
                    "authorization_timeout",
                    $"等待授权回调超时（{_timeout.TotalMinutes:0.#} 分钟），请重试。");
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private bool IsCallbackTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
            return false;

        var path = target.Split('?', 2)[0];

        return string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();

        while (builder.Length < 4096)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            var value = (char)buffer[0];
            if (value == '\n')
                break;

            if (value != '\r')
                builder.Append(value);
        }

        return builder.ToString();
    }

    private static string? ParseRequestTarget(string requestLine)
    {
        // 形如：GET /callback?code=...&state=... HTTP/1.1
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var body = $"""
            <!doctype html>
            <html lang="zh-CN">
            <head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="font-family:system-ui,-apple-system,Segoe UI,sans-serif;margin:4rem auto;max-width:32rem;line-height:1.7">
            <h1 style="font-size:1.25rem">{WebUtility.HtmlEncode(title)}</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
            </body>
            </html>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Not Found")}\r\n" +
                     "Content-Type: text/html; charset=utf-8\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryOpenBrowser(Uri url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url.AbsoluteUri);
            }
            else
            {
                Process.Start("xdg-open", url.AbsoluteUri);
            }
        }
        catch (Exception)
        {
            // 打开失败不影响流程：授权地址已写入日志，用户可以手动访问。
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// 把授权交互交给调用方实现，便于接入自定义 UI、无头环境或单元测试。
/// </summary>
/// <example>
/// <code>
/// var interaction = new DelegateOAuthInteraction(
///     new Uri("http://127.0.0.1:4200/callback"),
///     (redirectUri, authorizationUrl, ct) =>
///     {
///         Console.WriteLine($"请访问：{authorizationUrl}");
///         return Task.FromResult(new Uri($"{redirectUri}?code={code}&amp;state={state}"));
///     });
/// </code>
/// </example>
public sealed class DelegateOAuthInteraction : IMcpOAuthInteraction
{
    private readonly Func<Uri, Uri, CancellationToken, Task<Uri>> _handler;

    /// <summary>初始化委托交互。</summary>
    /// <param name="redirectUri">回调地址，必须与授权服务器登记的地址一致。</param>
    /// <param name="handler">接收回调地址与授权地址、返回最终回调地址的处理器。</param>
    public DelegateOAuthInteraction(
        Uri redirectUri,
        Func<Uri, Uri, CancellationToken, Task<Uri>> handler)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(handler);

        RedirectUri = redirectUri;
        _handler = handler;
    }

    /// <inheritdoc />
    public Uri RedirectUri { get; }

    /// <inheritdoc />
    public Task<Uri> AuthorizeAsync(Func<Uri, Uri> createAuthorizationUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createAuthorizationUrl);

        return _handler(RedirectUri, createAuthorizationUrl(RedirectUri), cancellationToken);
    }
}
