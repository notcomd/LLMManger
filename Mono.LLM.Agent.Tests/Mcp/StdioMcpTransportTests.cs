using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 覆盖 stdio 传输的启动失败路径与可执行文件解析规则。
/// </summary>
/// <remarks>
/// 这里不拉起真实的 MCP 服务器：协议行为已由假传输覆盖，
/// 本文件只验证「命令找不到时会给出清晰错误」和「Windows 的 .cmd 会走 cmd.exe 中转」这两条易错路径。
/// </remarks>
public class StdioMcpTransportTests
{
    [Fact]
    public async Task StartAsync_ThrowsActionableMessage_WhenCommandMissing()
    {
        var transport = new StdioMcpTransport(new McpStdioTransportOptions
        {
            Command = "mono-mcp-binary-that-does-not-exist-xyz"
        });

        var exception = await Assert.ThrowsAsync<McpConnectionClosedException>(() => transport.StartAsync());

        Assert.Contains("PATH", exception.Message);
        Assert.Equal(McpErrorCodes.ConnectionClosed, exception.Code);
    }

    [Fact]
    public void Resolve_AcceptsAbsolutePath()
    {
        var path = typeof(StdioMcpTransportTests).Assembly.Location;

        var resolution = ExecutableResolver.Resolve(path);

        Assert.Equal(Path.GetFullPath(path), resolution.ExecutablePath);
        Assert.False(resolution.RequiresCommandShell);
    }

    [Fact]
    public void Resolve_Throws_ForMissingAbsolutePath()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe");

        Assert.Throws<FileNotFoundException>(() => ExecutableResolver.Resolve(path));
    }

    [Fact]
    public void Resolve_MarksBatchFileAsRequiringShell()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "mono-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var scriptPath = Path.Combine(directory, "fake-server.cmd");
            File.WriteAllText(scriptPath, "@echo off\r\n");

            var resolution = ExecutableResolver.Resolve(scriptPath);

            // npx / uvx 在 Windows 上都是 .cmd，必须经 cmd.exe 才能启动。
            Assert.True(resolution.RequiresCommandShell);
            Assert.NotEmpty(resolution.CommandShellPath);

            var arguments = ExecutableResolver.BuildCommandShellArguments(resolution, new[] { "-y", "pkg" });
            Assert.Equal(new[] { "/d", "/s", "/c", "call", resolution.ExecutablePath, "-y", "pkg" }, arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
