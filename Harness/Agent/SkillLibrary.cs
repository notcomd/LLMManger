using Mono.LLM.Agent.Skills;

namespace Mono.LLM.Agent.Harness.Agent;

/// <summary>一次技能重载的结果。</summary>
/// <param name="Loaded">从目录读到的技能数量。</param>
/// <param name="Removed">因为文件消失而下线的技能数量。</param>
/// <param name="Errors">读取失败的文件。</param>
public sealed record SkillReloadResult(int Loaded, int Removed, IReadOnlyList<SkillLoadError> Errors);

/// <summary>
/// 管理磁盘上的技能文件：启动时加载，运行中可重载。
/// </summary>
/// <remarks>
/// <para>
/// 技能文件采用 <c>*.skill.json</c>，字段与 <see cref="SkillDefinition"/> 对应。
/// 重载是幂等的：同名文件会被新内容覆盖，被删掉的文件对应技能会从注册表移除。
/// </para>
/// <para>
/// 与内置技能同名时以文件为准；文件删除后不会误删内置技能。
/// </para>
/// </remarks>
public sealed class SkillLibrary
{
    private static readonly HashSet<string> BuiltInNames =
        new(SkillCatalog.GetAll().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

    private readonly FileBasedSkillSource _source;
    private readonly HashSet<string> _fileSkillNames = new(StringComparer.OrdinalIgnoreCase);

    public SkillLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory = directory;
        _source = new FileBasedSkillSource(directory, recursive: true);
    }

    /// <summary>技能目录的绝对路径。</summary>
    public string Directory { get; }

    /// <summary>目录是否存在。</summary>
    public bool Exists => System.IO.Directory.Exists(Directory);

    /// <summary>当前由文件提供的技能数量。</summary>
    public int FileSkillCount => _fileSkillNames.Count;

    /// <summary>重新扫描目录并同步到注册表。</summary>
    public async Task<SkillReloadResult> ReloadAsync(
        ISkillRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!Exists)
            return new SkillReloadResult(0, 0, Array.Empty<SkillLoadError>());

        var skills = await _source.LoadSkillsAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        var removed = 0;

        foreach (var stale in _fileSkillNames.Where(name => !names.Contains(name)).ToList())
        {
            _fileSkillNames.Remove(stale);

            if (BuiltInNames.Contains(stale))
                continue;

            registry.Unregister(stale);
            removed++;
        }

        foreach (var skill in skills)
        {
            registry.Register(skill);
            _fileSkillNames.Add(skill.Name);
        }

        return new SkillReloadResult(skills.Count, removed, _source.LoadErrors.ToList());
    }
}
