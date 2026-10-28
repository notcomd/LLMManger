namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 为单个测试提供独立的临时目录，测试结束后自动清理。
/// </summary>
/// <remarks>
/// <para>
/// 会话持久化、技能文件源这类测试都要落盘。若共用同一个目录，测试之间会互相污染，
/// 残留文件还会让结果随执行顺序漂移。此类型让每个沙箱拥有一个带 GUID 的独立根目录。
/// </para>
/// <para>
/// 清理前会校验目录确实位于约定根下，避免误删其它路径。
/// </para>
/// </remarks>
public sealed class FileSandbox : IDisposable
{
    private const string RootFolderName = "mono-llm-agent-tests";

    private bool _disposed;

    /// <summary>
    /// 创建一个新的文件沙箱。
    /// </summary>
    /// <param name="label">可选标签，便于在临时目录中定位是哪条测试产生的文件。</param>
    public FileSandbox(string? label = null)
    {
        var safeLabel = string.IsNullOrWhiteSpace(label) ? "sandbox" : Sanitize(label);
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            RootFolderName,
            $"{safeLabel}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Root);
    }

    /// <summary>沙箱根目录的绝对路径。</summary>
    public string Root { get; }

    /// <summary>清理失败时的原因；成功清理时为 <c>null</c>。</summary>
    public string? CleanupError { get; private set; }

    /// <summary>
    /// 把相对路径解析为沙箱内的绝对路径。
    /// </summary>
    /// <param name="relativePath">相对于沙箱根目录的路径。</param>
    /// <returns>绝对路径。</returns>
    /// <exception cref="ArgumentException">当路径逃逸出沙箱目录时抛出。</exception>
    public string GetPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relativePath));
        var normalizedRoot = System.IO.Path.GetFullPath(Root);

        var insideRoot = combined.StartsWith(
            normalizedRoot + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

        if (!insideRoot && !string.Equals(combined, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径 '{relativePath}' 逃逸出沙箱目录。", nameof(relativePath));

        return combined;
    }

    /// <summary>
    /// 在沙箱内创建目录。
    /// </summary>
    /// <param name="relativePath">相对于沙箱根目录的路径。</param>
    /// <returns>创建后的绝对路径。</returns>
    public string CreateDirectory(string relativePath)
    {
        var path = GetPath(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 在沙箱内写入文件，必要时自动创建父目录。
    /// </summary>
    /// <param name="relativePath">相对于沙箱根目录的路径。</param>
    /// <param name="content">文件内容。</param>
    /// <returns>写入后的绝对路径。</returns>
    public string Write(string relativePath, string content)
    {
        var path = GetPath(relativePath);
        var directory = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// 列出沙箱内的文件。
    /// </summary>
    /// <param name="pattern">搜索模式，默认全部文件。</param>
    /// <param name="recursive">是否递归子目录。</param>
    /// <returns>相对路径列表，按名称排序。</returns>
    public IReadOnlyList<string> ListFiles(string pattern = "*", bool recursive = false)
    {
        if (!Directory.Exists(Root))
            return Array.Empty<string>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.GetFiles(Root, pattern, option)
            .Select(path => System.IO.Path.GetRelativePath(Root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>判断文件是否存在。</summary>
    public bool Exists(string relativePath) => File.Exists(GetPath(relativePath));

    /// <summary>读取文件内容。</summary>
    public string Read(string relativePath) => File.ReadAllText(GetPath(relativePath));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var normalizedRoot = System.IO.Path.GetFullPath(Root);
        var expectedParent = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), RootFolderName));

        if (!normalizedRoot.StartsWith(
                expectedParent + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            CleanupError = $"拒绝删除不在沙箱根目录下的路径：{normalizedRoot}";
            return;
        }

        try
        {
            if (Directory.Exists(normalizedRoot))
                Directory.Delete(normalizedRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupError = ex.Message;
        }
    }

    private static string Sanitize(string label)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(label.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return string.IsNullOrEmpty(cleaned) ? "sandbox" : cleaned;
    }
}
