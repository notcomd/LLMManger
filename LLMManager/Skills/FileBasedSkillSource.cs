using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMManager.Skills;

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

    /// <inheritdoc/>
    public IEnumerable<SkillDefinition> LoadSkills()
    {
        if (!Directory.Exists(_directoryPath))
            return Enumerable.Empty<SkillDefinition>();

        var searchOption = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(_directoryPath, _filePattern, searchOption);

        var results = new List<SkillDefinition>();

        foreach (var filePath in files)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var fileDef = JsonSerializer.Deserialize<SkillDefinitionFile>(json, _jsonOptions);

                if (fileDef != null && !string.IsNullOrWhiteSpace(fileDef.Name))
                {
                    results.Add(new SkillDefinition
                    {
                        Name = fileDef.Name,
                        Description = fileDef.Description ?? string.Empty,
                        Category = fileDef.Category ?? "未分类",
                        SystemPrompt = fileDef.SystemPrompt ?? string.Empty,
                        Tags = fileDef.Tags ?? new List<string>(),
                        Version = fileDef.Version ?? "1.0"
                    });
                }
            }
            catch (JsonException)
            {
                // 跳过无法解析的文件，继续加载其他文件
            }
            catch (IOException)
            {
                // 跳过无法读取的文件
            }
        }

        return results;
    }
}

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
}
