// ============================================================================
// 文件：SkillSourceDiagnostics.cs
// 用途：从技能来源中提取诊断信息的统一入口。
// 实现方：SkillSourceDiagnostics
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 从技能来源中提取诊断信息的统一入口。
/// </summary>
/// <remarks>
/// 不同来源暴露诊断的方式不同：标准技能用 <see cref="StandardSkillSource.Diagnostics"/>，
/// JSON 技能用 <see cref="FileBasedSkillSource.LoadErrors"/>。这里把两者收敛成同一形态，
/// 供依赖注入装配与热重载共用。
/// </remarks>
internal static class SkillSourceDiagnostics
{
    internal static IReadOnlyList<SkillDiagnostic> Collect(ISkillSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source switch
        {
            StandardSkillSource standard => standard.Diagnostics,
            FileBasedSkillSource json => json.LoadErrors
                .Select(error => new SkillDiagnostic(
                    SkillDiagnosticCode.FileReadFailed,
                    SkillDiagnosticSeverity.Error,
                    error.Message,
                    error.FilePath))
                .ToList(),
            _ => Array.Empty<SkillDiagnostic>()
        };
    }
}
