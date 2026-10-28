// ============================================================================
// 文件：StandardSkillSource.cs
// 用途：从磁盘加载标准技能（SKILL.md + YAML frontmatter + 捆绑资源）。
// 实现方：StandardSkillSource
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 标准技能来源的可调参数。
/// </summary>
public sealed class StandardSkillOptions
{
    /// <summary>获取或设置技能根目录；若该目录本身包含 <c>SKILL.md</c>，则按单个技能处理。</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>获取或设置技能文件名，默认 <c>SKILL.md</c>。</summary>
    public string FileName { get; set; } = SkillFallback.FileName;

    /// <summary>获取或设置是否递归扫描子目录，默认 <c>true</c>（技能通常放在各自目录下）。</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>获取或设置单个技能允许捆绑的资源文件数上限，默认 200。超出部分不再列出。</summary>
    public int MaxResourceFiles { get; set; } = 200;

    /// <summary>获取或设置元数据与正文的校验参数。</summary>
    public SkillValidationOptions Validation { get; set; } = new();
}

/// <summary>
/// 从磁盘加载标准技能（<c>SKILL.md</c> + YAML frontmatter + 捆绑资源）。
/// </summary>
/// <remarks>
/// <para>
/// 支持两种目录布局：
/// </para>
/// <list type="bullet">
///   <item><b>技能集合</b>：根目录下每个子目录各含一个 <c>SKILL.md</c>，例如
///   <c>skills/pdf-processing/SKILL.md</c>、<c>skills/docx/SKILL.md</c>。默认按此布局递归扫描。</item>
///   <item><b>单个技能</b>：传入的目录自身就含 <c>SKILL.md</c>。</item>
/// </list>
/// <para>
/// 加载结果会填充 <see cref="SkillDefinition.Body"/>（渐进式披露第二层）、
/// <see cref="SkillDefinition.DirectoryPath"/> 与 <see cref="SkillDefinition.Resources"/>，
/// 并由 <see cref="SkillToolset"/> 在运行时决定何时把它们交给模型。
/// </para>
/// <para>
/// 单个技能文件损坏不会阻断整批加载：跳过该技能并把原因写入 <see cref="Diagnostics"/>。
/// </para>
/// </remarks>
public sealed class StandardSkillSource : ISkillSource
{
    private const int MaxConcurrentReads = 8;

    private readonly StandardSkillOptions _options;
    private readonly List<SkillDiagnostic> _diagnostics = new();

    /// <summary>
    /// 使用目录路径初始化 <see cref="StandardSkillSource"/>。
    /// </summary>
    /// <param name="directoryPath">技能根目录。</param>
    /// <param name="recursive">是否递归扫描子目录，默认 <c>true</c>。</param>
    /// <param name="sourceName">可选的来源名称；为 <c>null</c> 时使用目录路径。</param>
    /// <exception cref="ArgumentException">当 <paramref name="directoryPath"/> 为空时抛出。</exception>
    public StandardSkillSource(string directoryPath, bool recursive = true, string? sourceName = null)
        : this(new StandardSkillOptions { DirectoryPath = directoryPath, Recursive = recursive }, sourceName)
    {
    }

    /// <summary>
    /// 使用完整参数初始化 <see cref="StandardSkillSource"/>。
    /// </summary>
    /// <param name="options">加载参数。</param>
    /// <param name="sourceName">可选的来源名称；为 <c>null</c> 时使用目录路径。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="options"/> 为 <c>null</c> 时抛出。</exception>
    /// <exception cref="ArgumentException">当目录路径为空时抛出。</exception>
    public StandardSkillSource(StandardSkillOptions options, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
            throw new ArgumentException("技能目录不能为空。", nameof(options));

        _options = options;
        SourceName = sourceName ?? $"standard://{options.DirectoryPath}";
    }

    /// <inheritdoc/>
    public string SourceName { get; }

    /// <summary>
    /// 获取最近一次加载产生的全部诊断，包含解析告警与拒绝加载的原因。
    /// </summary>
    /// <remarks>
    /// 每次调用 <see cref="LoadSkillsAsync"/> 都会重置。有错误级诊断的技能不会出现在结果里，
    /// 但原因保留在这里，便于定位格式问题。
    /// </remarks>
    public IReadOnlyList<SkillDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnostics)
                return _diagnostics.ToList();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 同步版本是异步实现的包装。启动期一次性加载可以接受，请求路径中请使用 <see cref="LoadSkillsAsync"/>。
    /// </remarks>
    public IEnumerable<SkillDefinition> LoadSkills() =>
        LoadSkillsAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SkillDefinition>> LoadSkillsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_diagnostics)
            _diagnostics.Clear();

        var files = EnumerateSkillFiles();

        if (files.Count == 0)
            return Array.Empty<SkillDefinition>();

        var loaded = new (SkillDefinition? Skill, List<SkillDiagnostic> Diagnostics)[files.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentReads,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                loaded[index] = await LoadOneAsync(files[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (skill, diagnostics) in loaded)
        {
            AppendDiagnostics(diagnostics);

            if (skill is null)
                continue;

            if (byName.TryGetValue(skill.Name, out _))
            {
                AppendDiagnostics([new SkillDiagnostic(
                    SkillDiagnosticCode.DuplicateName,
                    SkillDiagnosticSeverity.Warning,
                    $"技能名 '{skill.Name}' 重复，后加载的定义会覆盖先前的。",
                    skill.DirectoryPath)]);
            }

            // 同名技能只保留最后扫描到的定义，与 CompositeSkillSource 的「后写胜出」保持一致。
            byName[skill.Name] = skill;
        }

        return byName.Values
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> EnumerateSkillFiles()
    {
        var directory = Path.GetFullPath(_options.DirectoryPath);

        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        var direct = Path.Combine(directory, _options.FileName);

        if (File.Exists(direct))
            return [direct];

        var option = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            return Directory
                .GetFiles(directory, _options.FileName, option)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppendDiagnostics([new SkillDiagnostic(
                SkillDiagnosticCode.FileReadFailed,
                SkillDiagnosticSeverity.Error,
                $"扫描技能目录失败：{ex.Message}",
                directory)]);

            return Array.Empty<string>();
        }
    }

    private async Task<(SkillDefinition? Skill, List<SkillDiagnostic> Diagnostics)> LoadOneAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<SkillDiagnostic>();

        string text;

        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.FileReadFailed,
                SkillDiagnosticSeverity.Error,
                $"读取失败：{ex.Message}",
                filePath));

            return (null, diagnostics);
        }

        var parse = SkillMarkdownParser.Parse(text, filePath);
        diagnostics.AddRange(parse.Diagnostics);

        if (parse.Metadata is null)
            return (null, diagnostics);

        var metadata = parse.Metadata;
        diagnostics.AddRange(SkillValidator.Validate(metadata, parse.Body, filePath, _options.Validation));

        if (diagnostics.HasErrors())
            return (null, diagnostics);

        var skillDirectory = Path.GetDirectoryName(filePath) ?? _options.DirectoryPath;
        var directoryName = Path.GetFileName(skillDirectory);

        if (!string.IsNullOrEmpty(directoryName)
            && !string.Equals(metadata.Name, directoryName, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.DirectoryNameMismatch,
                SkillDiagnosticSeverity.Warning,
                $"技能名 '{metadata.Name}' 与目录名 '{directoryName}' 不一致，按目录名检索时会找不到它。",
                filePath));
        }

        var resources = ScanResources(skillDirectory, diagnostics, filePath);

        var skill = new SkillDefinition
        {
            Name = metadata.Name,
            Description = metadata.Description,
            Category = metadata.CategoryOrDefault,
            SystemPrompt = parse.Body,
            Body = parse.Body,
            Tags = metadata.Tags.ToList(),
            Version = metadata.Version,
            License = metadata.License,
            Icon = metadata.Icon,
            AllowedTools = metadata.AllowedTools.ToList(),
            DirectoryPath = skillDirectory,
            Resources = resources,
            Metadata = metadata.Metadata.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };

        return (skill, diagnostics);
    }

    private IReadOnlyList<SkillResource> ScanResources(
        string skillDirectory,
        List<SkillDiagnostic> diagnostics,
        string filePath)
    {
        var files = new List<string>();
        CollectFiles(skillDirectory, files, _options.MaxResourceFiles);

        if (files.Count >= _options.MaxResourceFiles)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.TooManyResources,
                SkillDiagnosticSeverity.Warning,
                $"资源文件超过上限 {_options.MaxResourceFiles}，多余的部分不会列给模型。",
                filePath));
        }

        var resources = new List<SkillResource>(files.Count);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(skillDirectory, file).Replace('\\', '/');

            if (relative.Equals(_options.FileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsIgnored(relative))
                continue;

            long size = 0;

            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 拿不到大小不影响读取，按 0 处理。
            }

            resources.Add(new SkillResource(relative, SkillResourceReader.DetectKind(relative), size));
        }

        return resources
            .OrderBy(resource => resource.Kind)
            .ThenBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectFiles(string directory, List<string> files, int maxFiles)
    {
        if (files.Count >= maxFiles)
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (files.Count >= maxFiles)
                    return;

                files.Add(file);
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                if (IsIgnored(Path.GetFileName(subdirectory)))
                    continue;

                CollectFiles(subdirectory, files, maxFiles);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 单个子目录不可读时跳过，其余目录继续扫描。
        }
    }

    private static bool IsIgnored(string relativePath)
    {
        var segments = relativePath.Split('/', '\\');

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                continue;

            if (segment[0] == '.')
                return true;

            if (segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void AppendDiagnostics(IEnumerable<SkillDiagnostic> diagnostics)
    {
        lock (_diagnostics)
            _diagnostics.AddRange(diagnostics);
    }
}
