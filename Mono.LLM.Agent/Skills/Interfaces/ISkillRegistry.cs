// ============================================================================
// 文件：ISkillRegistry.cs
// 用途：定义技能注册表的接口，提供技能的注册、注销、查询和搜索功能。
// 实现方：SkillRegistry
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 定义技能注册表的接口，提供技能的注册、注销、查询和搜索功能。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISkillRegistry"/> 是技能管理的核心抽象，定义了技能生命周期管理的基本操作。
/// 实现类（如 <see cref="SkillRegistry"/>）负责维护技能的存储和检索逻辑。
/// </para>
/// <para>
/// 该接口支持按名称精确查询、全文模糊搜索、按分类筛选和按标签筛选等多种检索方式，
/// 适用于不同场景下的技能发现需求。
/// </para>
/// </remarks>
public interface ISkillRegistry
{
    /// <summary>
    /// 将指定的技能定义注册到注册表中。
    /// </summary>
    /// <param name="skill">要注册的技能定义。</param>
    /// <exception cref="ArgumentException">当 <paramref name="skill"/> 的 <see cref="SkillDefinition.Name"/> 为空或空白时抛出。</exception>
    void Register(SkillDefinition skill);

    /// <summary>
    /// 从注册表中注销指定名称的技能。
    /// </summary>
    /// <param name="skillName">要注销的技能名称。</param>
    /// <returns>如果成功找到并移除该技能则返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    bool Unregister(string skillName);

    /// <summary>
    /// 根据名称获取技能定义。
    /// </summary>
    /// <param name="skillName">要查找的技能名称。</param>
    /// <returns>找到的技能定义对象；如果未找到则返回 <c>null</c>。</returns>
    SkillDefinition? Get(string skillName);

    /// <summary>
    /// 获取注册表中所有已注册的技能定义列表。
    /// </summary>
    /// <returns>包含所有已注册技能的只读列表，按分类和名称排序。</returns>
    IReadOnlyList<SkillDefinition> GetAll();

    /// <summary>
    /// 根据查询关键词搜索匹配的技能。
    /// </summary>
    /// <param name="query">搜索关键词，将在技能名称、描述和标签中进行模糊匹配。</param>
    /// <returns>匹配的技能定义的可枚举序列，按分类和名称排序。</returns>
    IEnumerable<SkillDefinition> Search(string query);

    /// <summary>
    /// 根据分类获取该分类下所有的技能定义。
    /// </summary>
    /// <param name="category">要筛选的分类名称（不区分大小写）。</param>
    /// <returns>属于指定分类的技能定义的可枚举序列，按名称排序。</returns>
    IEnumerable<SkillDefinition> GetByCategory(string category);

    /// <summary>
    /// 根据标签获取包含该标签的所有技能定义。
    /// </summary>
    /// <param name="tag">要筛选的标签名称（不区分大小写）。</param>
    /// <returns>包含指定标签的技能定义的可枚举序列，按分类和名称排序。</returns>
    IEnumerable<SkillDefinition> GetByTag(string tag);

    /// <summary>
    /// 获取当前注册表中已注册技能的总数量。
    /// </summary>
    /// <value>已注册技能的数量。</value>
    int Count { get; }
}
