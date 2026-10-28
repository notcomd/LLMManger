// ============================================================================
// 文件：SkillDiagnostic.cs
// 用途：一条技能诊断记录，描述加载或校验过程中发现的问题。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：StandardSkillSource、SkillValidator、SkillHotReloader、DI 装配
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能诊断的严重级别。
/// </summary>
public enum SkillDiagnosticSeverity
{
    /// <summary>提示性信息，不影响技能可用性。</summary>
    Info = 0,

    /// <summary>不符合规范建议，技能仍会加载，但行为可能与预期不符。</summary>
    Warning = 1,

    /// <summary>违反规范约束，技能会被拒绝加载。</summary>
    Error = 2
}

/// <summary>
/// 技能诊断的问题分类。
/// </summary>
/// <remarks>
/// 每个分类对应一条可独立处理的规则。调用方可据此决定是否忽略某类问题，
/// 而不是靠匹配错误文本。
/// </remarks>
public enum SkillDiagnosticCode
{
    /// <summary>文件没有以 <c>---</c> 包裹的 YAML frontmatter。</summary>
    MissingFrontmatter,

    /// <summary>frontmatter 有开始标记但没有结束标记。</summary>
    UnclosedFrontmatter,

    /// <summary>frontmatter 语法不合法，或字段类型与规范不符。</summary>
    InvalidFrontmatter,

    /// <summary>缺少必填字段 <c>name</c>。</summary>
    MissingName,

    /// <summary><c>name</c> 不符合命名规范。</summary>
    InvalidName,

    /// <summary><c>name</c> 超出长度上限。</summary>
    NameTooLong,

    /// <summary>缺少必填字段 <c>description</c>。</summary>
    MissingDescription,

    /// <summary><c>description</c> 超出长度上限，会被模型侧截断。</summary>
    DescriptionTooLong,

    /// <summary><c>description</c> 没有说明「何时使用」，模型难以判断是否该加载。</summary>
    MissingUsageHint,

    /// <summary><c>name</c> 与技能所在目录名不一致。</summary>
    DirectoryNameMismatch,

    /// <summary>正文超出建议长度，渐进式披露的收益会被削弱。</summary>
    BodyTooLong,

    /// <summary>正文过短，可能缺少可执行的操作说明。</summary>
    BodyTooShort,

    /// <summary>多个技能使用了同一个名称，后加载的会覆盖先加载的。</summary>
    DuplicateName,

    /// <summary>文件读取失败。</summary>
    FileReadFailed,

    /// <summary>单个技能下捆绑的资源文件过多，超出扫描上限的部分被忽略。</summary>
    TooManyResources
}

/// <summary>
/// 一条技能诊断记录，描述加载或校验过程中发现的问题。
/// </summary>
/// <param name="Code">问题分类，可用于程序化处理。</param>
/// <param name="Severity">严重级别。</param>
/// <param name="Message">面向人的可读说明。</param>
/// <param name="FilePath">相关文件路径；与文件无关时为 <c>null</c>。</param>
/// <param name="Line">相关行号（从 1 开始）；未知时为 <c>null</c>。</param>
public sealed record SkillDiagnostic(
    SkillDiagnosticCode Code,
    SkillDiagnosticSeverity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    /// <summary>是否为阻断性错误。</summary>
    public bool IsError => Severity == SkillDiagnosticSeverity.Error;

    /// <summary>
    /// 返回格式为 <c>[级别] 代码：说明（位置）</c> 的字符串。
    /// </summary>
    /// <returns>便于日志输出的单行描述。</returns>
    public override string ToString()
    {
        var location = FilePath is null
            ? string.Empty
            : Line is null
                ? $" @ {FilePath}"
                : $" @ {FilePath}:{Line}";

        return $"[{Severity}] {Code}: {Message}{location}";
    }
}

/// <summary>
/// 判断一组诊断中是否存在阻断性错误的扩展方法。
/// </summary>
public static class SkillDiagnosticExtensions
{
    /// <summary>
    /// 判断序列中是否存在 <see cref="SkillDiagnosticSeverity.Error"/> 级别的诊断。
    /// </summary>
    /// <param name="diagnostics">待检查的诊断集合。</param>
    /// <returns>存在错误返回 <c>true</c>。</returns>
    public static bool HasErrors(this IEnumerable<SkillDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Any(d => d.IsError);
    }
}
