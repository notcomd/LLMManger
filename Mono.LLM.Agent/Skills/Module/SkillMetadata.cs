// ============================================================================
// 文件：SkillMetadata.cs
// 用途：标准技能的 YAML frontmatter 元数据。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillMarkdownParser、StandardSkillSource
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 标准技能的 YAML frontmatter 元数据。
/// </summary>
/// <remarks>
/// <para>
/// 字段对应 Agent Skills 规范：<c>name</c> 与 <c>description</c> 必填，
/// <c>license</c>、<c>allowed-tools</c>、<c>metadata</c> 可选。
/// </para>
/// <para>
/// 这是渐进式披露的第一层：库只把 <see cref="Name"/> 与 <see cref="Description"/>
/// 放进系统提示词，正文与资源等到模型显式加载时才进入上下文。
/// </para>
/// </remarks>
public sealed class SkillMetadata
{
    /// <summary>获取技能名称。规范要求使用「小写字母 + 数字 + 连字符」的短标识。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取技能描述。第三方的模型只靠这句话判断「这个技能是做什么的、什么时候用」。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取许可证标识，例如 <c>MIT</c>、<c>Apache-2.0</c>。未声明时为空字符串。</summary>
    public string License { get; init; } = string.Empty;

    /// <summary>获取技能版本号。未声明时为 <c>"1.0"</c>。</summary>
    public string Version { get; init; } = "1.0";

    /// <summary>
    /// 获取技能声明可使用的工具名列表。
    /// </summary>
    /// <remarks>
    /// 对应 frontmatter 的 <c>allowed-tools</c>，既可以写成 YAML 列表，也可以写成逗号/空格分隔的字符串。
    /// 值为空表示不限制，与 <see cref="SkillDefinition.AllowedTools"/> 的语义一致。
    /// </remarks>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>获取自定义元数据。<c>category</c>、<c>tags</c>、<c>icon</c> 等约定键会映射到技能定义。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>获取技能分类，取自 <c>metadata.category</c>；未声明时为空字符串。</summary>
    public string Category => GetMetadata("category");

    /// <summary>获取技能标签，取自 <c>metadata.tags</c>（列表或逗号分隔字符串）。</summary>
    public IReadOnlyList<string> Tags => SplitTags(GetMetadata("tags"));

    /// <summary>获取技能图标标识，取自 <c>metadata.icon</c>；未声明时为空字符串。</summary>
    public string Icon => GetMetadata("icon");

    /// <summary>获取技能主分类，缺失时回退到 <see cref="SkillFallback.Category"/>。</summary>
    internal string CategoryOrDefault =>
        string.IsNullOrWhiteSpace(Category) ? SkillFallback.Category : Category.Trim();

    private string GetMetadata(string key) =>
        Metadata.TryGetValue(key, out var value) ? value : string.Empty;

    private static IReadOnlyList<string> SplitTags(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// 技能加载过程中使用的约定值。
/// </summary>
internal static class SkillFallback
{
    /// <summary>未声明分类时使用的分类名。</summary>
    internal const string Category = "未分类";

    /// <summary>标准技能文件的固定文件名。</summary>
    internal const string FileName = "SKILL.md";
}
