// ============================================================================
// 文件：FileBasedSkillSource.cs
// 用途：基于文件系统的技能来源，从指定目录中扫描 JSON 文件并反序列化为 SkillDefinition 集合。
// 实现方：FileBasedSkillSource
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 基于文件系统的技能来源，从指定目录中扫描 JSON 文件并反序列化为 <see cref="SkillDefinition"/> 集合。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileBasedSkillSource"/> 从磁盘目录加载技能定义文件。每个 JSON 文件代表一个技能，
/// 文件名（不含扩展名）作为技能名称，文件内容需符合 <see cref="SkillDefinitionFile"/> 的 JSON 结构。
/// </para>
/// <para>
/// 此类不使用反射进行类型发现，仅通过文件 I/O 和 JSON 反序列化显式加载已存在的文件。
/// 支持递归子目录扫描和文件过滤（默认只读取 *.skill.json）。
/// </para>
/// <para>
/// JSON 文件格式示例（my-skill.skill.json）：
/// <code language="json">
/// {
///   "name": "我的技能",
///   "description": "技能描述",
///   "category": "分类",
///   "systemPrompt": "你是一个...",
///   "tags": ["标签1", "标签2"],
///   "version": "1.0"
/// }
/// </code>
/// </para>
/// </remarks>
public class FileBasedSkillSource : ISkillSource
{
    private readonly string _directoryPath;
    private readonly string _filePattern;
    private readonly bool _recursive;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<SkillLoadError> _loadErrors = new();

    /// <inheritdoc/>
    public string SourceName { get; }

    /// <summary>
    /// 初始化 <see cref="FileBasedSkillSource"/> 的新实例。
    /// </summary>
    /// <param name="directoryPath">包含技能定义文件的目录路径。</param>
    /// <param name="sourceName">可选的来源名称。若为 null 则使用目录路径作为名称。</param>
    /// <param name="filePattern">文件搜索模式，默认为 "*.skill.json"。</param>
    /// <param name="recursive">是否递归搜索子目录，默认为 false。</param>
    public FileBasedSkillSource(
        string directoryPath,
        string? sourceName = null,
        string filePattern = "*.skill.json",
        bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path cannot be empty.", nameof(directoryPath));

        _directoryPath = directoryPath;
        SourceName = sourceName ?? $"file://{directoryPath}";
        _filePattern = filePattern;
        _recursive = recursive;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>单次加载中允许同时打开的读取数量，避免目录内文件很多时耗尽句柄。</summary>
    private const int MaxConcurrentReads = 8;

    /// <inheritdoc/>
    /// <remarks>
    /// 同步版本是异步实现的包装，内部不会占用调用线程做 IO 等待。
    /// 在已有异步上下文（如 ASP.NET Core 请求）中请优先使用 <see cref="LoadSkillsAsync"/>。
    /// </remarks>
    public IEnumerable<SkillDefinition> LoadSkills() =>
        LoadSkillsAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SkillDefinition>> LoadSkillsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_loadErrors)
        {
            _loadErrors.Clear();
        }

        if (!Directory.Exists(_directoryPath))
            return Array.Empty<SkillDefinition>();

        var searchOption = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(_directoryPath, _filePattern, searchOption);

        if (files.Length == 0)
            return Array.Empty<SkillDefinition>();

        var parsed = new SkillDefinition?[files.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentReads,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                parsed[index] = await ReadSkillFileAsync(files[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return parsed.Where(skill => skill != null).Cast<SkillDefinition>().ToList();
    }

    private async Task<SkillDefinition?> ReadSkillFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var fileDef = JsonSerializer.Deserialize<SkillDefinitionFile>(json, _jsonOptions);

            if (fileDef == null)
            {
                RecordError(filePath, "文件内容为空或不是有效的 JSON 对象。");
                return null;
            }

            var validationError = Validate(fileDef);
            if (validationError != null)
            {
                RecordError(filePath, validationError);
                return null;
            }

            return new SkillDefinition
            {
                Name = fileDef.Name!,
                Description = fileDef.Description ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(fileDef.Category) ? "未分类" : fileDef.Category,
                SystemPrompt = fileDef.SystemPrompt!,
                Tags = fileDef.Tags ?? new List<string>(),
                Version = string.IsNullOrWhiteSpace(fileDef.Version) ? "1.0" : fileDef.Version,
                AllowedTools = fileDef.AllowedTools ?? new List<string>()
            };
        }
        catch (JsonException ex)
        {
            RecordError(filePath, $"JSON 解析失败：{ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            RecordError(filePath, $"文件读取失败：{ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            RecordError(filePath, $"无权访问文件：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 校验技能文件的关键字段。
    /// </summary>
    /// <returns>校验失败时返回可读原因；通过时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 缺少 <c>systemPrompt</c> 的技能没有实际意义（模型得不到任何角色约束），
    /// 因此与缺少 <c>name</c> 同等对待，直接跳过并记录原因。
    /// </remarks>
    private static string? Validate(SkillDefinitionFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Name))
            return "缺少必填字段 name。";

        if (string.IsNullOrWhiteSpace(file.SystemPrompt))
            return $"技能 '{file.Name}' 缺少必填字段 systemPrompt。";

        return null;
    }

    /// <summary>
    /// 获取最近一次 <see cref="LoadSkills"/> 中被跳过的文件及原因。
    /// </summary>
    /// <remarks>
    /// 单个技能文件损坏不应阻断整批加载，但静默吞掉会让使用者完全无法察觉配置错误。
    /// 加载结束后可通过此属性核对哪些文件没有被采纳。
    /// </remarks>
    public IReadOnlyList<SkillLoadError> LoadErrors
    {
        get
        {
            lock (_loadErrors)
                return _loadErrors.ToList();
        }
    }

    private void RecordError(string filePath, string message)
    {
        lock (_loadErrors)
            _loadErrors.Add(new SkillLoadError(filePath, message));
    }
}

/// <summary>
/// 描述一条技能文件加载失败记录。
/// </summary>
/// <param name="FilePath">出错的文件路径。</param>
/// <param name="Message">失败原因。</param>
public readonly record struct SkillLoadError(string FilePath, string Message);

/// <summary>
/// 技能定义文件的 JSON 序列化结构，用于 <see cref="FileBasedSkillSource"/> 的文件解析。
/// </summary>
internal class SkillDefinitionFile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("allowedTools")]
    public List<string>? AllowedTools { get; set; }
}
