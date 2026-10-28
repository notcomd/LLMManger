// ============================================================================
// 文件：SkillResource.cs
// 用途：技能捆绑资源的模型、分类与安全读取。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：StandardSkillSource、SkillToolset（read_skill_resource）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能捆绑资源的类别，对应标准技能目录下约定的三个子目录。
/// </summary>
/// <remarks>
/// 目录约定来自 Agent Skills 规范：<c>scripts/</c> 放可直接执行的代码，
/// <c>references/</c> 放按需查阅的文档，<c>assets/</c> 放模板、字体、图片等素材。
/// 不在这三个目录下的文件归入 <see cref="Other"/>，仍然可以被读取。
/// </remarks>
public enum SkillResourceKind
{
    /// <summary>可执行脚本（<c>scripts/</c>）。</summary>
    Script = 0,

    /// <summary>参考文档（<c>references/</c>）。</summary>
    Reference = 1,

    /// <summary>模板与素材（<c>assets/</c>）。</summary>
    Asset = 2,

    /// <summary>其它位置的附属文件。</summary>
    Other = 3
}

/// <summary>
/// 描述一个随技能捆绑的资源文件。
/// </summary>
/// <param name="RelativePath">相对于技能根目录的路径，始终使用 <c>/</c> 分隔。</param>
/// <param name="Kind">资源类别。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
public sealed record SkillResource(string RelativePath, SkillResourceKind Kind, long SizeBytes)
{
    /// <summary>资源的文件名（不含目录）。</summary>
    public string FileName => Path.GetFileName(RelativePath);

    /// <summary>人类可读的大小文本，例如 <c>12.4 KB</c>。</summary>
    public string HumanSize => FormatSize(SizeBytes);

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes / (1024d * 1024d):F1} MB"
    };
}

/// <summary>
/// 读取技能资源文件的结果。
/// </summary>
/// <param name="Resource">被读取的资源。</param>
/// <param name="Content">文本内容；文件超过大小上限时是前若干个字节。</param>
/// <param name="Truncated">内容是否因为超过上限而被截断。</param>
public sealed record SkillResourceContent(SkillResource Resource, string Content, bool Truncated);

/// <summary>
/// 按安全规则读取技能目录内的资源文件。
/// </summary>
/// <remarks>
/// <para>
/// 资源路径最终来自模型的工具调用参数，属于不可信输入。读取前会把路径规范化并校验其仍位于
/// 技能根目录之内，<c>..</c>、绝对路径、盘符跳转、符号链接逃逸都会被拒绝。
/// </para>
/// <para>
/// 单次读取有字节上限（默认 256 KB）。超出部分截断而不是报错：一个 10 MB 的日志文件
/// 塞进上下文会直接挤掉真正的对话内容。
/// </para>
/// </remarks>
public static class SkillResourceReader
{
    /// <summary>默认的单次读取字节上限。</summary>
    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>
    /// 异步读取技能目录内的资源文件。
    /// </summary>
    /// <param name="skillDirectory">技能根目录的绝对路径。</param>
    /// <param name="relativePath">相对于技能根目录的资源路径。</param>
    /// <param name="maxBytes">允许读取的最大字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取结果，包含内容与是否被截断。</returns>
    /// <exception cref="ArgumentException">路径为空、为绝对路径，或逃逸出技能目录时抛出。</exception>
    /// <exception cref="FileNotFoundException">资源文件不存在时抛出。</exception>
    /// <exception cref="DirectoryNotFoundException">技能目录不存在时抛出。</exception>
    public static async Task<SkillResourceContent> ReadAsync(
        string skillDirectory,
        string relativePath,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "读取上限必须为正数。");

        var root = Path.GetFullPath(skillDirectory);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"技能目录不存在：{root}");

        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("资源路径必须是相对于技能目录的相对路径。", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsInside(root, fullPath))
            throw new ArgumentException($"资源路径 '{relativePath}' 逃逸出技能目录。", nameof(relativePath));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"资源不存在：{relativePath}", fullPath);

        var info = new FileInfo(fullPath);
        var limit = Math.Min(maxBytes, (int)Math.Min(info.Length, int.MaxValue));
        var buffer = new byte[Math.Max(limit, 0)];
        var read = 0;

        await using (var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length == 0 ? 1 : buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (read < buffer.Length)
            {
                var chunk = await stream
                    .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                    .ConfigureAwait(false);

                if (chunk == 0)
                    break;

                read += chunk;
            }
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var normalized = relativePath.Replace('\\', '/');

        return new SkillResourceContent(
            new SkillResource(normalized, DetectKind(normalized), info.Length),
            text,
            info.Length > read);
    }

    /// <summary>
    /// 根据相对路径判断资源类别。
    /// </summary>
    /// <param name="relativePath">使用 <c>/</c> 分隔的相对路径。</param>
    /// <returns>资源类别。</returns>
    public static SkillResourceKind DetectKind(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var normalized = relativePath.Replace('\\', '/');
        var separator = normalized.IndexOf('/');

        var top = separator < 0 ? string.Empty : normalized[..separator];

        return top.ToLowerInvariant() switch
        {
            "scripts" or "script" => SkillResourceKind.Script,
            "references" or "reference" => SkillResourceKind.Reference,
            "assets" or "asset" => SkillResourceKind.Asset,
            _ => SkillResourceKind.Other
        };
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, comparison);
    }
}
