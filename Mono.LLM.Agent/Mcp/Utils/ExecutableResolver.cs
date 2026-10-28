// ============================================================================
// 文件：ExecutableResolver.cs
// 用途：把配置里的命令名解析为可直接交给 Process 的可执行文件。
// 实现方：ExecutableResolver
// 调用方：McpClientManager、ToolManager、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================


namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// 可执行文件的解析结果。
/// </summary>
/// <param name="ExecutablePath">解析后的绝对路径。</param>
/// <param name="RequiresCommandShell">是否必须经 <c>cmd.exe</c> 启动（Windows 的 .cmd/.bat）。</param>
/// <param name="CommandShellPath">命令解释器路径；<paramref name="RequiresCommandShell"/> 为 <c>false</c> 时为空串。</param>
internal sealed record ExecutableResolution(
    string ExecutablePath,
    bool RequiresCommandShell,
    string CommandShellPath);

/// <summary>
/// 把配置里的命令名解析为可直接交给 <see cref="System.Diagnostics.Process"/> 的可执行文件。
/// </summary>
/// <remarks>
/// <para>
/// 现实中的 MCP 服务器大多通过 <c>npx</c>、<c>uvx</c>、<c>node</c> 这类启动器运行，而
/// <see cref="System.Diagnostics.ProcessStartInfo.UseShellExecute"/> 为 <c>false</c> 时，
/// Windows 无法直接执行 <c>.cmd</c>/<c>.bat</c>（<c>npx</c> 恰好就是 <c>npx.cmd</c>）。
/// 因此这里做三件事：
/// </para>
/// <list type="number">
///   <item>按 <c>PATHEXT</c> 在 <c>PATH</c> 中补全扩展名；</item>
///   <item>识别 <c>.cmd</c>/<c>.bat</c>，标记为需要 <c>cmd.exe</c> 中转；</item>
///   <item>统一返回绝对路径，让启动失败的报错能直接指出缺失的可执行文件。</item>
/// </list>
/// <para>
/// 注意：<c>.ps1</c> 脚本不在此列——PowerShell 的执行策略与参数转义差异过大，
/// 建议由使用者自行用 <c>powershell -File</c> 包装后再配置。
/// </para>
/// </remarks>
internal static class ExecutableResolver
{
    private static readonly string[] WindowsExtensions = { ".exe", ".cmd", ".bat", ".com" };

    /// <summary>解析命令，返回绝对路径与是否需要命令解释器。</summary>
    /// <param name="command">配置中的命令，可以是绝对路径、相对路径或 PATH 中的命令名。</param>
    /// <exception cref="FileNotFoundException">命令不存在。</exception>
    public static ExecutableResolution Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("MCP 服务器的启动命令不能为空。", nameof(command));

        if (OperatingSystem.IsWindows())
        {
            var path = ResolveOnWindows(command);
            var extension = Path.GetExtension(path);
            var requiresShell = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

            return new ExecutableResolution(
                path,
                requiresShell,
                requiresShell ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") : string.Empty);
        }

        return new ExecutableResolution(ResolveOnUnix(command), false, string.Empty);
    }

    /// <summary>构造一个把目标命令包进 <c>cmd.exe /c call</c> 的参数序列。</summary>
    /// <remarks>
    /// 使用 <c>call</c> 而非直接把路径塞给 <c>/c</c>，是为了规避 cmd 对「以引号开头的命令串」
    /// 掐头去尾的解析规则——路径包含空格时直接传会丢失引号导致找不到文件。
    /// </remarks>
    public static IReadOnlyList<string> BuildCommandShellArguments(ExecutableResolution resolution, IEnumerable<string> arguments)
    {
        var list = new List<string>
        {
            "/d",
            "/s",
            "/c",
            "call",
            resolution.ExecutablePath
        };

        list.AddRange(arguments);
        return list;
    }

    private static string ResolveOnWindows(string command)
    {
        var hasDirectory = command.Contains('\\') || command.Contains('/');

        if (hasDirectory)
        {
            var fullPath = Path.GetFullPath(command);
            if (File.Exists(fullPath))
                return fullPath;

            if (string.IsNullOrEmpty(Path.GetExtension(fullPath)))
            {
                foreach (var extension in WindowsExtensions)
                {
                    var candidate = fullPath + extension;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            throw new FileNotFoundException($"找不到 MCP 服务器的可执行文件：{command}", command);
        }

        var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, command);

            if (File.Exists(candidate))
                return candidate;

            foreach (var extension in pathExtensions)
            {
                // PATHEXT 通常是大写，而磁盘上的文件名可能是小写，两种都试一次。
                foreach (var variant in new[] { extension.ToLowerInvariant(), extension })
                {
                    var withExtension = candidate + variant;
                    if (File.Exists(withExtension))
                        return withExtension;
                }
            }
        }

        throw new FileNotFoundException(
            $"在 PATH 中找不到命令「{command}」。请检查 MCP 服务器配置，或改用可执行文件的绝对路径。",
            command);
    }

    private static string ResolveOnUnix(string command)
    {
        if (command.Contains('/'))
        {
            var fullPath = Path.GetFullPath(command);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"找不到 MCP 服务器的可执行文件：{command}", command);

            return fullPath;
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"在 PATH 中找不到命令「{command}」。请检查 MCP 服务器配置，或改用可执行文件的绝对路径。",
            command);
    }
}
