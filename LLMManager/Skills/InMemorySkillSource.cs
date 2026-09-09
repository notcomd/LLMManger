namespace LLMManager.Skills;

/// <summary>
/// 基于内存列表的技能来源，直接持有预先构建的 <see cref="SkillDefinition"/> 集合。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InMemorySkillSource"/> 是最简单的技能来源实现。它将一组已构造好的
/// <see cref="SkillDefinition"/> 对象包装为 <see cref="ISkillSource"/> 接口，
/// 适用于硬编码预定义、程序化生成或单元测试等场景。
/// </para>
/// <para>
/// 使用示例：
/// <code>
/// var source = new InMemorySkillSource("my-skills", new[]
/// {
///     new SkillDefinition { Name = "翻译", SystemPrompt = "..." },
///     new SkillDefinition { Name = "摘要", SystemPrompt = "..." }
/// });
/// </code>
/// </para>
/// </remarks>
public class InMemorySkillSource : ISkillSource
{
    private readonly IReadOnlyList<SkillDefinition> _skills;

    /// <inheritdoc/>
    public string SourceName { get; }

    /// <summary>
    /// 初始化 <see cref="InMemorySkillSource"/> 的新实例。
    /// </summary>
    /// <param name="sourceName">来源名称，用于标识此来源。</param>
    /// <param name="skills">要包装的技能定义集合。</param>
    public InMemorySkillSource(string sourceName, IEnumerable<SkillDefinition> skills)
    {
        SourceName = sourceName;
        _skills = skills.ToList();
    }

    /// <inheritdoc/>
    public IEnumerable<SkillDefinition> LoadSkills() => _skills;
}
