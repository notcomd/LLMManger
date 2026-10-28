// ============================================================================
// 文件：SkillToolset.cs
// 用途：把技能注册表暴露成两个工具，实现「按需加载」的渐进式披露。
// 实现方：SkillToolset
// 调用方：DI 装配、Harness（load_skill / read_skill_resource）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// <see cref="SkillToolset"/> 的可调参数。
/// </summary>
public sealed class SkillToolsetOptions
{
    /// <summary>获取或设置加载技能的工具名，默认 <c>load_skill</c>。</summary>
    public string LoadSkillToolName { get; set; } = SkillToolset.DefaultLoadSkillToolName;

    /// <summary>获取或设置读取技能资源的工具名，默认 <c>read_skill_resource</c>。</summary>
    public string ReadResourceToolName { get; set; } = SkillToolset.DefaultReadResourceToolName;

    /// <summary>获取或设置单次资源读取的字节上限，默认 256 KB。</summary>
    public int MaxResourceBytes { get; set; } = SkillResourceReader.DefaultMaxBytes;
}

/// <summary>
/// 把技能注册表暴露成两个工具，实现「按需加载」的渐进式披露。
/// </summary>
/// <remarks>
/// <para>
/// 标准技能分三层进入上下文：
/// </para>
/// <list type="number">
///   <item><b>索引</b>：只有名称与描述，由 <see cref="BuildSystemPrompt"/> 拼进系统提示词；</item>
///   <item><b>正文</b>：模型调用 <c>load_skill</c> 后，正文作为工具结果进入对话；</item>
///   <item><b>资源</b>：模型调用 <c>read_skill_resource</c> 后，单个文件内容进入对话。</item>
/// </list>
/// <para>
/// 未加载的技能不占用任何 token。这与 <see cref="SkillExecutor"/> 的「切换技能即替换系统提示词」
/// 并不冲突：前者适合「技能很多、每轮只用一个」的助手，后者适合「用户显式指定角色」的会话。
/// </para>
/// <para>
/// 资源读取路径来自模型给出的工具参数，属于不可信输入，读取前会做目录逃逸校验。
/// </para>
/// </remarks>
public sealed class SkillToolset
{
    /// <summary>加载技能的工具默认名称。</summary>
    public const string DefaultLoadSkillToolName = "load_skill";

    /// <summary>读取技能资源的工具默认名称。</summary>
    public const string DefaultReadResourceToolName = "read_skill_resource";

    private readonly ISkillRegistry _registry;
    private readonly SkillToolsetOptions _options;
    private readonly ILogger<SkillToolset>? _logger;
    private readonly SkillTreeCache _treeCache;
    private readonly SkillIndexOptions _indexOptions;
    private readonly ConcurrentDictionary<string, SkillDefinition> _activated =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 初始化 <see cref="SkillToolset"/>。
    /// </summary>
    /// <param name="registry">提供技能定义的注册表。</param>
    /// <param name="options">可调参数；为 <c>null</c> 时使用默认值。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <param name="treeCache">技能树缓存；为 <c>null</c> 时新建一个。与 <see cref="SkillRouter"/> 共享可避免重复建树。</param>
    /// <param name="indexOptions">索引渲染参数；为 <c>null</c> 时使用默认值。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="registry"/> 为 <c>null</c> 时抛出。</exception>
    public SkillToolset(
        ISkillRegistry registry,
        SkillToolsetOptions? options = null,
        ILogger<SkillToolset>? logger = null,
        SkillTreeCache? treeCache = null,
        SkillIndexOptions? indexOptions = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new SkillToolsetOptions();
        _logger = logger;
        _treeCache = treeCache ?? new SkillTreeCache();
        _indexOptions = indexOptions ?? new SkillIndexOptions();
    }

    /// <summary>获取缓存的技能树。</summary>
    public SkillTree Tree => _treeCache.Get(_registry);

    /// <summary>获取正在使用的技能树缓存。</summary>
    public SkillTreeCache TreeCache => _treeCache;

    /// <summary>获取已被模型加载过的技能，按加载顺序无关的名称序排列。</summary>
    public IReadOnlyList<SkillDefinition> ActivatedSkills =>
        _activated.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>获取加载技能工具的名称。</summary>
    public string LoadSkillToolName => _options.LoadSkillToolName;

    /// <summary>获取读取资源工具的名称。</summary>
    public string ReadResourceToolName => _options.ReadResourceToolName;

    /// <summary>在技能被首次加载时触发。订阅方异常不会影响加载结果。</summary>
    public event EventHandler<SkillActivatedEventArgs>? SkillActivated;

    /// <summary>
    /// 向工具管理器注册 <c>load_skill</c> 与 <c>read_skill_resource</c>。
    /// </summary>
    /// <param name="tools">目标工具管理器。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="tools"/> 为 <c>null</c> 时抛出。</exception>
    /// <remarks>
    /// 重复调用会覆盖同名工具，便于在运行时更换工具集后重新挂载。
    /// </remarks>
    public void RegisterTools(ToolManager tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        tools.RegisterTool(
            _options.LoadSkillToolName,
            "加载一个技能的完整说明与资源清单。当可用技能索引中的描述与当前任务相关时调用。",
            [
                new ToolParameterInfo
                {
                    Name = "name",
                    Type = "string",
                    Description = "技能名，取自可用技能索引",
                    Required = true
                }
            ],
            (arguments, _) =>
            {
                var name = ReadString(arguments, "name");

                if (string.IsNullOrWhiteSpace(name))
                    return Task.FromResult(Failure(_options.LoadSkillToolName, "缺少参数 name。"));

                try
                {
                    return Task.FromResult(new ToolResult
                    {
                        Name = _options.LoadSkillToolName,
                        Result = Activate(name)
                    });
                }
                catch (SkillNotFoundException ex)
                {
                    return Task.FromResult(Failure(_options.LoadSkillToolName, ex.Message));
                }
            });

        tools.RegisterTool(
            _options.ReadResourceToolName,
            "读取技能捆绑的资源文件，路径取自 load_skill 返回的资源清单。",
            [
                new ToolParameterInfo
                {
                    Name = "skill",
                    Type = "string",
                    Description = "技能名",
                    Required = true
                },
                new ToolParameterInfo
                {
                    Name = "path",
                    Type = "string",
                    Description = "相对于技能目录的资源路径，例如 references/REFERENCE.md",
                    Required = true
                }
            ],
            async (arguments, cancellationToken) =>
            {
                var skillName = ReadString(arguments, "skill");
                var path = ReadString(arguments, "path");

                if (string.IsNullOrWhiteSpace(skillName) || string.IsNullOrWhiteSpace(path))
                    return Failure(_options.ReadResourceToolName, "参数 skill 与 path 都不能为空。");

                return await ReadResourceAsync(skillName, path, cancellationToken).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// 构建带技能索引的系统提示词。
    /// </summary>
    /// <param name="basePrompt">基础系统提示词，可以为 <c>null</c>。</param>
    /// <returns>基础提示词 + 技能索引；注册表为空时原样返回基础提示词。</returns>
    public string BuildSystemPrompt(string? basePrompt = null)
    {
        var index = _treeCache.Get(_registry).Render(_indexOptions);

        if (index.Length == 0)
            return basePrompt?.Trim() ?? string.Empty;

        return string.IsNullOrWhiteSpace(basePrompt)
            ? index
            : $"{basePrompt.Trim()}\n\n{index}";
    }

    /// <summary>
    /// 激活技能并返回要放进上下文的说明文本。
    /// </summary>
    /// <param name="name">技能名。</param>
    /// <returns>技能正文与资源清单的 Markdown 文本。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="name"/> 为空时抛出。</exception>
    /// <exception cref="SkillNotFoundException">注册表中不存在该技能时抛出。</exception>
    public string Activate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var skill = _registry.Get(name.Trim())
            ?? throw new SkillNotFoundException(
                $"未找到技能 '{name}'。可用技能：{FormatAvailableSkills()}");

        var firstTime = _activated.TryAdd(skill.Name, skill);
        var text = BuildActivationText(skill);

        _logger?.LogDebug("Skill '{Skill}' activated (first={First}), {Chars} chars",
            skill.Name, firstTime, text.Length);

        if (firstTime)
            RaiseActivated(skill);

        return text;
    }

    /// <summary>
    /// 判断技能是否已被加载过。
    /// </summary>
    /// <param name="name">技能名。</param>
    /// <returns>已加载返回 <c>true</c>。</returns>
    public bool IsActivated(string name) =>
        !string.IsNullOrWhiteSpace(name) && _activated.ContainsKey(name.Trim());

    /// <summary>
    /// 清空激活记录，用于开启新的会话。
    /// </summary>
    public void ResetActivated() => _activated.Clear();

    /// <summary>
    /// 读取技能捆绑的资源文件。
    /// </summary>
    /// <param name="skillName">技能名。</param>
    /// <param name="relativePath">相对于技能目录的资源路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>工具结果；失败时 <see cref="ToolResult.IsSuccess"/> 为 <c>false</c>。</returns>
    public async Task<ToolResult> ReadResourceAsync(
        string skillName,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var skill = _registry.Get(skillName.Trim());

        if (skill is null)
        {
            return Failure(
                _options.ReadResourceToolName,
                $"未找到技能 '{skillName}'。可用技能：{FormatAvailableSkills()}");
        }

        if (string.IsNullOrWhiteSpace(skill.DirectoryPath))
        {
            return Failure(
                _options.ReadResourceToolName,
                $"技能 '{skill.Name}' 没有捆绑目录，无法读取资源。");
        }

        try
        {
            var content = await SkillResourceReader
                .ReadAsync(skill.DirectoryPath, relativePath, _options.MaxResourceBytes, cancellationToken)
                .ConfigureAwait(false);

            var text = content.Content;

            if (content.Truncated)
            {
                text = text.TrimEnd()
                    + $"\n\n[内容已截断：文件共 {content.Resource.HumanSize}，本次只返回前 {_options.MaxResourceBytes} 字节。请缩小读取范围。]";
            }

            _logger?.LogDebug("Skill '{Skill}' resource '{Path}' read ({Bytes} bytes, truncated={Truncated})",
                skill.Name, relativePath, content.Content.Length, content.Truncated);

            return new ToolResult
            {
                Name = _options.ReadResourceToolName,
                Result = text
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException
                                       or DirectoryNotFoundException or IOException
                                       or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Skill '{Skill}' resource '{Path}' read failed", skill.Name, relativePath);

            return Failure(_options.ReadResourceToolName, ex.Message);
        }
    }

    private string BuildActivationText(SkillDefinition skill)
    {
        var builder = new StringBuilder();

        builder.Append("# 技能：").AppendLine(skill.Name);

        var instructions = string.IsNullOrWhiteSpace(skill.Body) ? skill.SystemPrompt : skill.Body;

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            builder.AppendLine();
            builder.AppendLine(skill.Description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            builder.AppendLine();
            builder.AppendLine(instructions.Trim());
        }

        if (skill.HasResources)
        {
            builder.AppendLine();
            builder.AppendLine("## 附带资源");
            builder.AppendLine();

            foreach (var resource in skill.Resources)
            {
                builder
                    .Append("- `").Append(resource.RelativePath).Append("` · ")
                    .Append(KindLabel(resource.Kind)).Append(" · ")
                    .AppendLine(resource.HumanSize);
            }

            builder.AppendLine();
            builder.Append("需要时用 ")
                .Append(_options.ReadResourceToolName)
                .Append("(skill=\"")
                .Append(skill.Name)
                .Append("\", path=\"...\") 读取。");
        }

        return builder.ToString().TrimEnd();
    }

    private static string KindLabel(SkillResourceKind kind) => kind switch
    {
        SkillResourceKind.Script => "script",
        SkillResourceKind.Reference => "reference",
        SkillResourceKind.Asset => "asset",
        _ => "file"
    };

    private string FormatAvailableSkills()
    {
        var names = _treeCache.Get(_registry).Skills.Select(skill => skill.Name).ToList();

        return names.Count == 0 ? "（注册表为空）" : string.Join(", ", names);
    }

    private static string ReadString(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return arguments.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static ToolResult Failure(string toolName, string message) => new()
    {
        Name = toolName,
        IsSuccess = false,
        Error = message
    };

    private void RaiseActivated(SkillDefinition skill)
    {
        try
        {
            SkillActivated?.Invoke(this, new SkillActivatedEventArgs(skill));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SkillActivated handler for '{Skill}' threw", skill.Name);
        }
    }
}

/// <summary>
/// 技能被加载时的事件参数。
/// </summary>
public sealed class SkillActivatedEventArgs : EventArgs
{
    /// <summary>
    /// 初始化 <see cref="SkillActivatedEventArgs"/>。
    /// </summary>
    /// <param name="skill">被加载的技能。</param>
    public SkillActivatedEventArgs(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        Skill = skill;
    }

    /// <summary>获取被加载的技能定义。</summary>
    public SkillDefinition Skill { get; }
}

/// <summary>
/// 按名称找不到技能时抛出的异常。
/// </summary>
/// <remarks>
/// 单独定义而不是复用 <see cref="KeyNotFoundException"/>，是为了让调用方能把
/// 「模型编了一个不存在的技能名」与其它查找失败区分开。
/// </remarks>
public sealed class SkillNotFoundException : KeyNotFoundException
{
    /// <summary>
    /// 初始化 <see cref="SkillNotFoundException"/>。
    /// </summary>
    /// <param name="message">错误说明，通常包含可用技能列表。</param>
    public SkillNotFoundException(string message) : base(message)
    {
    }
}
