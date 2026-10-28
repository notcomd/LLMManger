// ============================================================================
// 文件：SkillRegistrationOptions.cs
// 用途：在依赖注入容器构建阶段累积技能注册项，供 SkillRegistry 创建时一次性装载。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddSkillSystem / AddStandardSkills）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 在依赖注入容器构建阶段累积技能注册项，供 <see cref="SkillRegistry"/> 创建时一次性装载。
/// </summary>
/// <remarks>
/// <para>
/// 技能注册不能依赖“解析时执行副作用”的工厂委托，否则会与延迟解析、重复注册产生冲突。
/// 本类型改为在 <c>IServiceCollection</c> 阶段收集声明，注册表工厂在实例化时统一消费。
/// </para>
/// <para>
/// 因此 <c>AddSkill(...)</c> 与 <c>AddSkillSystem(...)</c> 的调用顺序不影响最终结果。
/// </para>
/// </remarks>
public sealed class SkillRegistrationOptions
{
    internal List<SkillDefinition> Skills { get; } = new();

    internal List<ISkillSource> Sources { get; } = new();

    /// <summary>
    /// 是否加载 <see cref="SkillCatalog"/> 中的全部内置技能。
    /// </summary>
    /// <remarks>
    /// 默认值为 <c>false</c>，由 <c>AddSkillSystem(bool loadCatalog)</c> 显式开启；
    /// 仅调用 <c>AddSkill(...)</c> 时不会意外引入内置技能集合。
    /// </remarks>
    public bool LoadCatalog { get; set; }

    /// <summary>
    /// 追加一个待注册的技能定义。
    /// </summary>
    /// <param name="skill">要注册的技能定义。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="skill"/> 为 <c>null</c> 时抛出。</exception>
    public SkillRegistrationOptions Add(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        Skills.Add(skill);
        return this;
    }

    /// <summary>
    /// 获取当前已累积的技能定义快照。
    /// </summary>
    /// <returns>技能定义的只读列表。</returns>
    public IReadOnlyList<SkillDefinition> GetSkills() => Skills.AsReadOnly();

    /// <summary>
    /// 追加一个技能来源，其内容在注册表实例化时加载。
    /// </summary>
    /// <param name="source">技能来源，例如 <see cref="StandardSkillSource"/>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="source"/> 为 <c>null</c> 时抛出。</exception>
    /// <remarks>
    /// 来源在注册表工厂中同步加载一次。磁盘扫描应当发生在启动阶段，
    /// 因此这里不提供异步入口——异步加载请直接使用 <see cref="SkillLoadingConfig"/>。
    /// </remarks>
    public SkillRegistrationOptions AddSource(ISkillSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Sources.Add(source);
        return this;
    }

    /// <summary>
    /// 获取当前已累积的技能来源快照。
    /// </summary>
    /// <returns>技能来源的只读列表。</returns>
    public IReadOnlyList<ISkillSource> GetSources() => Sources.AsReadOnly();
}
