// ============================================================================
// 文件：SkillRegistry.cs
// 用途：技能注册表的线程安全实现，基于并发字典管理技能定义的存储与检索。
// 实现方：SkillRegistry
// 调用方：DI 装配、SkillRouter、SkillToolset、SkillHotReloader
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Collections.Concurrent;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能注册表的线程安全实现，基于并发字典管理技能定义的存储与检索。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillRegistry"/> 实现了 <see cref="ISkillRegistry"/> 接口，使用
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 作为底层存储容器，确保在高并发场景下的线程安全性。
/// </para>
/// <para>
/// 技能名称的比较采用不区分大小写的规则，即 "CodeReview" 和 "codereview" 视为同一个技能。
/// 注册同名技能时会覆盖已有定义。
/// </para>
/// </remarks>
public class SkillRegistry : ISkillRegistry
{
    private readonly ConcurrentDictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取当前注册表中已注册技能的总数量。
    /// </summary>
    /// <value>已注册技能的数量。</value>
    public int Count => _skills.Count;

    /// <summary>
    /// 将指定的技能定义注册到注册表中。
    /// </summary>
    /// <param name="skill">要注册的技能定义。</param>
    /// <exception cref="ArgumentException">当 <paramref name="skill"/> 的 <see cref="SkillDefinition.Name"/> 为空或空白时抛出。</exception>
    /// <remarks>
    /// 如果注册表中已存在同名的技能定义，将被新传入的定义覆盖。
    /// </remarks>
    public void Register(SkillDefinition skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Name))
            throw new ArgumentException("Skill name cannot be empty.", nameof(skill));

        _skills[skill.Name] = skill;
    }

    /// <summary>
    /// 从注册表中注销指定名称的技能。
    /// </summary>
    /// <param name="skillName">要注销的技能名称。</param>
    /// <returns>如果成功找到并移除该技能则返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool Unregister(string skillName)
    {
        return _skills.TryRemove(skillName, out _);
    }

    /// <summary>
    /// 根据名称获取技能定义。
    /// </summary>
    /// <param name="skillName">要查找的技能名称（不区分大小写）。</param>
    /// <returns>找到的技能定义对象；如果未找到则返回 <c>null</c>。</returns>
    public SkillDefinition? Get(string skillName)
    {
        _skills.TryGetValue(skillName, out var skill);
        return skill;
    }

    /// <summary>
    /// 获取注册表中所有已注册的技能定义列表。
    /// </summary>
    /// <returns>包含所有已注册技能的只读列表，先按分类再按名称升序排列。</returns>
    public IReadOnlyList<SkillDefinition> GetAll()
    {
        return _skills.Values.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// 根据查询关键词搜索匹配的技能。
    /// </summary>
    /// <param name="query">搜索关键词，将在技能名称、描述和标签中进行不区分大小写的模糊匹配。</param>
    /// <returns>匹配的技能定义的可枚举序列，按分类和名称排序。</returns>
    public IEnumerable<SkillDefinition> Search(string query)
    {
        var lower = query.ToLowerInvariant();
        return _skills.Values
            .Where(s =>
                s.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(t => t.Contains(lower, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name);
    }

    /// <summary>
    /// 根据分类获取该分类下所有的技能定义。
    /// </summary>
    /// <param name="category">要筛选的分类名称（不区分大小写）。</param>
    /// <returns>属于指定分类的技能定义的可枚举序列，按名称升序排列。</returns>
    public IEnumerable<SkillDefinition> GetByCategory(string category)
    {
        return _skills.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name);
    }

    /// <summary>
    /// 根据标签获取包含该标签的所有技能定义。
    /// </summary>
    /// <param name="tag">要筛选的标签名称（不区分大小写）。</param>
    /// <returns>包含指定标签的技能定义的可枚举序列，按分类和名称排序。</returns>
    public IEnumerable<SkillDefinition> GetByTag(string tag)
    {
        return _skills.Values
            .Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name);
    }

    /// <summary>
    /// 批量注册多个技能定义到注册表中。
    /// </summary>
    /// <param name="skills">要注册的技能定义的可枚举集合。</param>
    /// <exception cref="ArgumentException">当集合中任意技能的 <see cref="SkillDefinition.Name"/> 为空或空白时抛出。</exception>
    /// <remarks>
    /// 对集合中的每个技能依次调用 <see cref="Register(SkillDefinition)"/> 方法进行注册。
    /// </remarks>
    public void RegisterAll(IEnumerable<SkillDefinition> skills)
    {
        foreach (var skill in skills)
        {
            Register(skill);
        }
    }
}
