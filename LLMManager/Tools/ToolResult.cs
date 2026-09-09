namespace LLMManager.Tools;

/// <summary>
/// 表示一次工具调用的执行结果。
/// </summary>
/// <remarks>
/// 封装了工具执行的返回数据，统一了成功/失败两种状态的处理方式：
/// <list type="bullet">
///   <item><see cref="IsSuccess"/> 为 <c>true</c> 时，<see cref="Result"/> 包含有效输出；</item>
///   <item><see cref="IsSuccess"/> 为 <c>false</c> 时，<see cref="Error"/> 包含错误详情。</item>
/// </list>
/// 由 <see cref="ToolManager.ExecuteToolAsync"/> 方法返回。
/// </remarks>
public class ToolResult
{
    /// <summary>获取或设置关联的工具调用请求 ID。</summary>
    /// <value>
    /// 用于将此结果与特定的 <c>tool_call_id</c> 进行匹配的唯一标识符，
    /// 以便将结果回传给 AI 模型完成多轮对话中的工具调用闭环。
    /// 默认为空字符串。
    /// </value>
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>获取或设置被执行的工具函数名称。</summary>
    /// <value>被调用的工具在注册时的唯一名称标识符。默认为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置工具执行成功后的返回结果内容。</summary>
    /// <value>工具执行后产生的文本输出内容。默认为空字符串。</value>
    public string Result { get; set; } = string.Empty;

    /// <summary>获取或设置工具是否执行成功。</summary>
    /// <value>
    /// <c>true</c> 表示执行成功，<see cref="Result"/> 包含有效数据；
    /// <c>false</c> 表示执行失败，应通过 <see cref="Error"/> 查看具体原因。
    /// 默认为 <c>true</c>。
    /// </value>
    public bool IsSuccess { get; set; } = true;

    /// <summary>获取或设置工具执行失败时的错误描述信息。</summary>
    /// <value>
    /// 当 <see cref="IsSuccess"/> 为 <c>false</c> 时，此处包含人类可读的错误原因说明；
    /// 执行成功时为 <c>null</c>。
    /// </value>
    public string? Error { get; set; }
}
