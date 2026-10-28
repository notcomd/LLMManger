// ============================================================================
// 文件：SkillValidator.cs
// 用途：按 Agent Skills 规范校验技能元数据与正文。
// 实现方：SkillValidator
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.RegularExpressions;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 标准技能校验的可调参数。
/// </summary>
/// <remarks>
/// 默认值来自 Agent Skills 规范：名称不超过 64 字符、描述不超过 1024 字符、正文建议不超过 500 行。
/// 这些数字不是随意设定的——它们决定了渐进式披露第一层在系统提示词里占多少空间。
/// </remarks>
public sealed class SkillValidationOptions
{
    /// <summary>
    /// 是否要求名称严格符合「小写字母 + 数字 + 连字符」的规范格式。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <c>false</c>：允许中文等非 ASCII 名称，只拒绝空白字符与路径分隔符，
    /// 因此本库既能量产符合规范的英文技能，也能直接用中文命名。
    /// </para>
    /// <para>
    /// 置为 <c>true</c> 时，任何不匹配 <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> 的名称都会被判为错误，
    /// 适合要对外分发技能包的场景。
    /// </para>
    /// </remarks>
    public bool StrictNames { get; set; }

    /// <summary>名称的字符数上限，默认 64。</summary>
    public int MaxNameLength { get; set; } = 64;

    /// <summary>描述的字符数上限，默认 1024。</summary>
    public int MaxDescriptionLength { get; set; } = 1024;

    /// <summary>正文的建议行数上限，默认 500。超出只告警，不影响加载。</summary>
    public int MaxBodyLines { get; set; } = 500;

    /// <summary>正文的最短字符数，默认 20。低于此值只告警。</summary>
    public int MinBodyLength { get; set; } = 20;
}

/// <summary>
/// 按 Agent Skills 规范校验技能元数据与正文。
/// </summary>
/// <remarks>
/// 校验只产出诊断，不抛异常，也不决定「加载还是不加载」——那是 <see cref="StandardSkillSource"/> 的判断。
/// 这样同一套规则既能用于加载期阻断，也能用于编辑器的实时提示。
/// </remarks>
public static class SkillValidator
{
    private static readonly Regex StrictNamePattern =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] NameForbiddenChars =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly string[] UsageHints =
    [
        "use when", "use this", "use for", "used when", "when to", "when the", "when user",
        "用于", "适用", "当用户", "需要时", "在需要", "何时"
    ];

    /// <summary>
    /// 校验技能元数据与正文。
    /// </summary>
    /// <param name="metadata">frontmatter 元数据。</param>
    /// <param name="body">Markdown 正文。</param>
    /// <param name="filePath">可选的文件路径，用于诊断定位。</param>
    /// <param name="options">校验参数；为 <c>null</c> 时使用默认值。</param>
    /// <returns>按严重级别排序的诊断列表；完全合规时为空。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="metadata"/> 为 <c>null</c> 时抛出。</exception>
    public static IReadOnlyList<SkillDiagnostic> Validate(
        SkillMetadata metadata,
        string body,
        string? filePath = null,
        SkillValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var settings = options ?? new SkillValidationOptions();
        var diagnostics = new List<SkillDiagnostic>();

        ValidateName(metadata.Name, settings, filePath, diagnostics);
        ValidateDescription(metadata.Description, settings, filePath, diagnostics);
        ValidateBody(body ?? string.Empty, settings, filePath, diagnostics);

        return diagnostics;
    }

    private static void ValidateName(
        string name,
        SkillValidationOptions options,
        string? filePath,
        List<SkillDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.MissingName,
                SkillDiagnosticSeverity.Error,
                "缺少必填字段 name。",
                filePath));

            return;
        }

        if (name.Length > options.MaxNameLength)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.NameTooLong,
                SkillDiagnosticSeverity.Error,
                $"name 长度为 {name.Length}，超过上限 {options.MaxNameLength}。",
                filePath));
        }

        if (options.StrictNames)
        {
            if (!StrictNamePattern.IsMatch(name))
            {
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticCode.InvalidName,
                    SkillDiagnosticSeverity.Error,
                    $"name '{name}' 不符合规范：只能使用小写字母、数字与连字符，且不能以连字符开头或结尾。",
                    filePath));
            }

            return;
        }

        var hasWhitespace = name.Any(char.IsWhiteSpace);
        var hasForbidden = name.IndexOfAny(NameForbiddenChars) >= 0;
        var hasControl = name.Any(char.IsControl);

        if (hasWhitespace || hasForbidden || hasControl)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.InvalidName,
                SkillDiagnosticSeverity.Error,
                $"name '{name}' 不能包含空白字符、控制字符或路径分隔符（/ \\ : * ? \" < > |）。",
                filePath));
        }
    }

    private static void ValidateDescription(
        string description,
        SkillValidationOptions options,
        string? filePath,
        List<SkillDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.MissingDescription,
                SkillDiagnosticSeverity.Error,
                "缺少必填字段 description。模型只靠这一句话决定是否加载技能。",
                filePath));

            return;
        }

        if (description.Length > options.MaxDescriptionLength)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.DescriptionTooLong,
                SkillDiagnosticSeverity.Error,
                $"description 长度为 {description.Length}，超过上限 {options.MaxDescriptionLength}。",
                filePath));
        }

        var lowered = description.ToLowerInvariant();
        var mentionsUsage = UsageHints.Any(hint => lowered.Contains(hint, StringComparison.Ordinal));

        if (!mentionsUsage)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.MissingUsageHint,
                SkillDiagnosticSeverity.Warning,
                "description 没有说明「何时使用」。建议补一句触发场景，否则模型难以判断该不该加载。",
                filePath));
        }
    }

    private static void ValidateBody(
        string body,
        SkillValidationOptions options,
        string? filePath,
        List<SkillDiagnostic> diagnostics)
    {
        var trimmed = body.Trim();

        if (trimmed.Length == 0)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.BodyTooShort,
                SkillDiagnosticSeverity.Warning,
                "正文为空。加载技能后模型拿不到任何额外说明。",
                filePath));

            return;
        }

        var lineCount = trimmed.Count(ch => ch == '\n') + 1;

        if (lineCount > options.MaxBodyLines)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.BodyTooLong,
                SkillDiagnosticSeverity.Warning,
                $"正文共 {lineCount} 行，超过建议上限 {options.MaxBodyLines} 行。考虑把细节移到 references/ 目录。",
                filePath));
        }

        if (trimmed.Length < options.MinBodyLength)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.BodyTooShort,
                SkillDiagnosticSeverity.Warning,
                $"正文只有 {trimmed.Length} 个字符，可能缺少可执行的步骤说明。",
                filePath));
        }
    }
}
