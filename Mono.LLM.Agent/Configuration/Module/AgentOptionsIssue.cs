// ============================================================================
// 文件：AgentOptionsIssue.cs
// 用途：配置校验问题、严重级别与聚合结果。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 一条配置校验问题。
/// </summary>
/// <param name="Path">配置路径，例如 <c>llm.modelId</c>、<c>mcp.servers[0].command</c>。</param>
/// <param name="Message">面向人的说明，包含实际值与期望值。</param>
/// <param name="Severity">严重级别。</param>
public sealed record AgentOptionsIssue(string Path, string Message, AgentOptionsIssueSeverity Severity)
{
    /// <summary>是否为阻断性错误。</summary>
    public bool IsError => Severity == AgentOptionsIssueSeverity.Error;

    /// <summary>返回格式为 <c>[级别] 路径：说明</c> 的单行文本。</summary>
    /// <returns>便于日志输出的描述。</returns>
    public override string ToString() => $"[{Severity}] {Path}: {Message}";

    internal static AgentOptionsIssue Error(string path, string message) =>
        new(path, message, AgentOptionsIssueSeverity.Error);

    internal static AgentOptionsIssue Warning(string path, string message) =>
        new(path, message, AgentOptionsIssueSeverity.Warning);
}

/// <summary>
/// 配置校验问题的严重级别。
/// </summary>
public enum AgentOptionsIssueSeverity
{
    /// <summary>不阻断启动，但很可能不是期望的行为。</summary>
    Warning = 0,

    /// <summary>配置不可用，装配阶段应直接失败。</summary>
    Error = 1
}

/// <summary>
/// 配置校验结果。
/// </summary>
/// <remarks>
/// 校验不抛异常，把所有问题一次性收集完再返回：配置错误往往成串出现，
/// 逐个试错比一次看全更浪费时间。
/// </remarks>
public sealed class AgentOptionsValidationResult
{
    /// <summary>创建一个空结果（无任何问题）。</summary>
    public AgentOptionsValidationResult()
        : this(Array.Empty<AgentOptionsIssue>())
    {
    }

    /// <summary>用给定的问题集合创建结果。</summary>
    /// <param name="issues">校验发现的问题。</param>
    public AgentOptionsValidationResult(IEnumerable<AgentOptionsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues.ToList();
    }

    /// <summary>获取全部问题，按配置路径排序。</summary>
    public IReadOnlyList<AgentOptionsIssue> Issues { get; }

    /// <summary>获取阻断性错误。</summary>
    public IReadOnlyList<AgentOptionsIssue> Errors => Issues.Where(issue => issue.IsError).ToList();

    /// <summary>获取不阻断的警告。</summary>
    public IReadOnlyList<AgentOptionsIssue> Warnings => Issues.Where(issue => !issue.IsError).ToList();

    /// <summary>获取配置是否可用（没有错误级问题）。</summary>
    public bool IsValid => !Issues.Any(issue => issue.IsError);

    /// <summary>
    /// 在存在错误时抛出 <see cref="InvalidOperationException"/>，异常消息包含全部错误。
    /// </summary>
    /// <exception cref="InvalidOperationException">存在错误级问题时抛出。</exception>
    public void ThrowIfInvalid()
    {
        if (IsValid)
            return;

        var details = string.Join(Environment.NewLine, Errors.Select(issue => $"  - {issue.Path}: {issue.Message}"));

        throw new InvalidOperationException($"配置校验未通过：{Environment.NewLine}{details}");
    }

    /// <summary>返回所有问题的多行文本，便于直接打印。</summary>
    /// <returns>每行一条问题的字符串。</returns>
    public override string ToString() =>
        Issues.Count == 0 ? "配置有效。" : string.Join(Environment.NewLine, Issues.Select(issue => issue.ToString()));
}
