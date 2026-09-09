namespace LLMManager.Skills;

/// <summary>
/// 定义技能来源的抽象接口，用于从不同位置加载 <see cref="SkillDefinition"/> 集合。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISkillSource"/> 是技能加载体系的策略接口。库提供多种内置实现，
/// 应用层也可通过实现此接口来接入自定义的技能来源（如数据库、远程 API、配置中心等）。
/// </para>
/// <para>
/// 所有加载操作均为显式调用，不使用反射或自动发现机制。具体使用哪个来源
/// 由应用层通过 <see cref="SkillLoadingConfig"/> 配置决定。
/// </para>
/// </remarks>
public interface ISkillSource
{
    /// <summary>获取此来源的描述性名称，用于日志和调试。</summary>
    string SourceName { get; }

    /// <summary>
    /// 从此来源加载所有可用的技能定义。
    /// </summary>
    /// <returns>此来源提供的所有技能定义的可枚举集合。</returns>
    IEnumerable<SkillDefinition> LoadSkills();
}
