// ============================================================================
// 文件：SkillIndexBuilder.cs
// 用途：把技能列表渲染成渐进式披露的第一层：只含名称与描述的系统提示词片段。
// 实现方：SkillIndexBuilder
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能索引提示词的构建选项。
/// </summary>
public sealed class SkillIndexOptions
{
    /// <summary>获取或设置索引的标题行，默认 <c>## 可用技能</c>。</summary>
    public string Heading { get; set; } = "## 可用技能";

    /// <summary>获取或设置索引前的使用说明。设为空字符串可省略。</summary>
    public string Intro { get; set; } =
        "需要时再加载：先按描述判断相关性，相关时调用 `load_skill` 取完整说明，再按说明操作。";

    /// <summary>获取或设置是否按分类分组（树形两层），默认 <c>true</c>。</summary>
    public bool GroupByCategory { get; set; } = true;

    /// <summary>获取或设置单条描述的最大字符数，默认 240。超出部分截断。</summary>
    public int MaxDescriptionLength { get; set; } = 240;
}

/// <summary>
/// 把技能列表渲染成渐进式披露的第一层：只含名称与描述的系统提示词片段。
/// </summary>
/// <remarks>
/// <para>
/// 这是标准技能相对「把所有技能的系统提示词一次性拼进上下文」的关键差别。
/// 十个技能各写 500 字说明就是 5000 字常驻上下文；而索引只有名字加一句话，
/// 加载与否交给模型按需决定。
/// </para>
/// <para>
/// 渲染按「分类 → 技能」两层组织，分类名只出现一次，比平铺列表更省 token，也更接近人找技能的方式。
/// 具体排版由 <see cref="SkillTree.Render"/> 负责。
/// </para>
/// </remarks>
public static class SkillIndexBuilder
{
    /// <summary>
    /// 构建技能索引文本。
    /// </summary>
    /// <param name="skills">要列入索引的技能。</param>
    /// <param name="options">渲染选项；为 <c>null</c> 时使用默认值。</param>
    /// <returns>索引文本；技能为空时返回空字符串。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="skills"/> 为 <c>null</c> 时抛出。</exception>
    public static string Build(IEnumerable<SkillDefinition> skills, SkillIndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(skills);

        return SkillTree.Build(skills).Render(options);
    }

    /// <summary>
    /// 把技能索引拼到基础系统提示词之后。
    /// </summary>
    /// <param name="basePrompt">原有的系统提示词，可以为 <c>null</c> 或空。</param>
    /// <param name="skills">要列入索引的技能。</param>
    /// <param name="options">渲染选项；为 <c>null</c> 时使用默认值。</param>
    /// <returns>拼接后的系统提示词；没有技能时原样返回基础提示词。</returns>
    public static string Compose(
        string? basePrompt,
        IEnumerable<SkillDefinition> skills,
        SkillIndexOptions? options = null)
    {
        var index = Build(skills, options);

        if (index.Length == 0)
            return basePrompt?.Trim() ?? string.Empty;

        return string.IsNullOrWhiteSpace(basePrompt)
            ? index
            : $"{basePrompt.Trim()}\n\n{index}";
    }
}
