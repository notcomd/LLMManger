// ============================================================================
// 文件：ToolExecutionOptions.cs
// 用途：控制工具执行行为的选项。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：ToolManager 构造函数、AgentOptions.Tools
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 控制工具执行行为的选项。
/// </summary>
/// <remarks>
/// <para>
/// 这些选项作用于 <see cref="ToolManager"/> 的每一次执行，可在构造时统一设置，
/// 也可以通过 <see cref="ToolManager.CreateScoped"/> 为子集单独指定。
/// </para>
/// <para>
/// <see cref="MaxDegreeOfParallelism"/> 默认为 1（串行）。工具通常带有副作用
/// （下单、发消息、写文件），并行执行可能引发竞态，因此需要显式开启。
/// </para>
/// </remarks>
public sealed class ToolExecutionOptions
{
    /// <summary>
    /// 单次工具执行的超时时间，默认 30 秒。
    /// </summary>
    /// <value>
    /// 超过该时长的执行会被取消并返回失败结果。设为 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// 可关闭超时控制。
    /// </value>
    /// <remarks>
    /// 超时通过取消令牌传递给 handler，若 handler 不响应取消令牌，则无法真正中断其执行。
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 批量执行多个工具调用时的最大并发度，默认 1（完全串行）。
    /// </summary>
    /// <value>大于 1 时启用并行，值即最大并发数。</value>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>
    /// 是否在调用 handler 之前校验参数（必填项、类型、枚举取值），默认为 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 校验失败时不会调用 handler，而是把结构化的错误描述返回给模型，
    /// 使其能自行修正参数后重试。
    /// </remarks>
    public bool ValidateArguments { get; set; } = true;

    /// <summary>
    /// 是否把 <see cref="ToolParameterInfo.DefaultValue"/> 填充到缺失的可选参数中，默认为 <c>true</c>。
    /// </summary>
    public bool ApplyDefaultValues { get; set; } = true;

    internal ToolExecutionOptions Clone() => new()
    {
        Timeout = Timeout,
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        ValidateArguments = ValidateArguments,
        ApplyDefaultValues = ApplyDefaultValues
    };

    internal bool HasTimeout =>
        Timeout > TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan;
}

/// <summary>
/// 描述一次待执行的工具调用。
/// </summary>
/// <param name="Name">工具名称。</param>
/// <param name="ArgumentsJson">JSON 格式的参数对象。</param>
/// <param name="CallId">模型返回的调用标识，用于把结果与原调用对应。</param>
public sealed record ToolCallRequest(string Name, string ArgumentsJson, string? CallId = null);

/// <summary>
/// 工具执行完成后触发的事件参数，用于接入日志、指标或审计。
/// </summary>
public sealed class ToolExecutedEventArgs : EventArgs
{
    /// <summary>工具名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>对应的模型调用标识，可能为 <c>null</c>。</summary>
    public string? CallId { get; init; }

    /// <summary>是否执行成功。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>执行耗时（含参数校验与重试开销）。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>失败原因；成功时为 <c>null</c>。</summary>
    public string? Error { get; init; }

    /// <summary>实际生效的参数（JSON 文本，已应用默认值）；无法获取时为 <c>null</c>。</summary>
    /// <remarks>
    /// 与模型发出的原始参数可能不同：可选项的默认值会在此前被补齐。
    /// </remarks>
    public string? Arguments { get; init; }

    /// <summary>工具返回的内容；失败时为 <c>null</c>。</summary>
    public string? Result { get; init; }
}
