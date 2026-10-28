// ============================================================================
// 文件：ToolOptions.cs
// 用途：工具执行相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json.Serialization;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 工具执行相关的配置。
/// </summary>
/// <remarks>
/// 对应 <see cref="ToolExecutionOptions"/>。默认串行执行、30 秒超时、开启参数校验——
/// 工具通常带副作用，并行与跳过校验都应当是显式选择。
/// </remarks>
public sealed class ToolOptions
{
    /// <summary>获取或设置单次执行的超时，默认 30 秒。设为 <c>infinite</c> 可关闭。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>获取或设置批量执行的并发度，默认 1（串行）。</summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>获取或设置是否在调用处理器前校验参数，默认 <c>true</c>。</summary>
    public bool ValidateArguments { get; set; } = true;

    /// <summary>获取或设置是否用默认值填充缺失的可选参数，默认 <c>true</c>。</summary>
    public bool ApplyDefaultValues { get; set; } = true;

    /// <summary>
    /// 转换为运行时的 <see cref="ToolExecutionOptions"/>。
    /// </summary>
    /// <returns>可直接传给 <see cref="ToolManager"/> 构造函数的选项。</returns>
    public ToolExecutionOptions ToExecutionOptions() => new()
    {
        Timeout = Timeout,
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        ValidateArguments = ValidateArguments,
        ApplyDefaultValues = ApplyDefaultValues
    };

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("tools");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            issues.Add(AgentOptionsIssue.Error(
                $"{prefix}.timeout",
                "超时必须为正数，或写 infinite 关闭超时。"));
        }

        if (MaxDegreeOfParallelism < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxDegreeOfParallelism", "并发度至少为 1。"));

        return issues;
    }
}
