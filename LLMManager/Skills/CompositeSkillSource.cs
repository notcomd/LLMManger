namespace LLMManager.Skills;

/// <summary>
/// 组合式技能来源，按顺序聚合多个 <see cref="ISkillSource"/> 的加载结果。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CompositeSkillSource"/> 实现了组合模式，允许同时从多个来源加载技能并合并为统一的结果集。
/// 后注册的来源中的同名技能会覆盖先注册的来源中的同名技能（后写胜出策略）。
/// </para>
/// <para>
/// 典型用法：先加载内置默认技能，再叠加用户自定义技能，自定义技能可以覆盖同名的默认定义。
/// </para>
/// </remarks>
public class CompositeSkillSource : ISkillSource
{
    private readonly List<ISkillSource> _sources = new();

    /// <inheritdoc/>
    public string SourceName { get; } = "composite";

    /// <summary>获取当前已注册的所有子来源列表。</summary>
    public IReadOnlyList<ISkillSource> Sources => _sources.AsReadOnly();

    /// <summary>初始化 <see cref="CompositeSkillSource"/> 的新实例。</summary>
    public CompositeSkillSource() { }

    /// <summary>
    /// 向组合中追加一个子来源。
    /// </summary>
    /// <param name="source">要添加的技能来源。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public CompositeSkillSource Add(ISkillSource source)
    {
        _sources.Add(source);
        return this;
    }

    /// <inheritdoc/>
    public IEnumerable<SkillDefinition> LoadSkills()
    {
        var merged = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            foreach (var skill in source.LoadSkills())
            {
                merged[skill.Name] = skill;
            }
        }

        return merged.Values;
    }
}
