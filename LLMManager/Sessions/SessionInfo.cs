using LLMManager.Core;

namespace LLMManager.Sessions;

/// <summary>
/// 表示一个LLM会话的完整信息，包含会话的元数据、配置和统计信息。
/// </summary>
/// <remarks>
/// <para>此类是会话系统的核心数据模型，用于持久化和传输会话相关的所有信息。</para>
/// <para>每个会话通过唯一的 <see cref="Id"/> 进行标识，并关联到特定的
/// LLM提供商（<see cref="ProviderType"/>）和模型（<see cref="ModelId"/>）。</para>
/// <para><see cref="Id"/> 属性默认使用GUID的前12个字符作为唯一标识符，
/// 在对象构造时自动生成。如果需要自定义ID，可在创建后手动设置该属性。</para>
/// </remarks>
public class SessionInfo
{
    /// <summary>
    /// 获取或设置会话的唯一标识符。
    /// </summary>
    /// <value>
    /// 会话ID字符串，默认值为新生成GUID的前12个字符（无连字符格式）。
    /// </value>
    /// <remarks>
    /// 此属性在对象实例化时自动生成，但也可以在创建后手动设置以使用自定义ID。
    /// </remarks>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 获取或设置会话的显示名称。
    /// </summary>
    /// <value>会话名称字符串，默认值为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置会话的系统提示词（System Prompt）。
    /// </summary>
    /// <value>
    /// 系统提示词字符串，默认值为空字符串。
    /// 系统提示词会在每次与LLM交互时作为上下文的一部分发送。
    /// </value>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置此会话使用的LLM提供商类型。
    /// </summary>
    /// <value>
    /// 枚举值，表示当前会话所连接的LLM服务提供商类型（如OpenAI、Azure等）。
    /// </value>
    public LLMProviderType ProviderType { get; set; }

    /// <summary>
    /// 获取或设置此会话使用的模型标识符。
    /// </summary>
    /// <value>模型ID字符串，默认值为空字符串。例如 "gpt-4"、"claude-3" 等。</value>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置会话的创建时间（UTC时间）。
    /// </summary>
    /// <value>
    /// 创建时间的 <see cref="DateTime"/> 值，默认值为当前UTC时间。
    /// </value>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 获取或设置会话的最后活跃时间（UTC时间）。
    /// </summary>
    /// <value>
    /// 最后活跃时间的 <see cref="DateTime"/> 值，默认值为当前UTC时间。
    /// 该属性在每次发送消息时自动更新。
    /// </value>
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 获取或设置会话的标签列表。
    /// </summary>
    /// <value>
    /// 字符串标签集合，默认为空列表。
    /// 标签可用于对会话进行分类和筛选。
    /// </value>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置会话中的消息总数。
    /// </summary>
    /// <value>
    /// 消息计数的整数值，默认值为0。
    /// 包含用户消息和助手回复的总数。
    /// </value>
    public int MessageCount { get; set; }

    /// <summary>
    /// 获取或设置会话累计消耗的输入Token数量。
    /// </summary>
    /// <value>
    /// 输入Token总数的整数值，默认值为0。
    /// 每次发送消息后累加该次请求的输入Token数。
    /// </value>
    public int TotalInputTokens { get; set; }

    /// <summary>
    /// 获取或设置会话累计消耗的输出Token数量。
    /// </summary>
    /// <value>
    /// 输出Token总数的整数值，默认值为0。
    /// 每次发送消息后累加该次请求的输出Token数。
    /// </value>
    public int TotalOutputTokens { get; set; }

    /// <summary>
    /// 获取或设置会话的自定义元数据键值对集合。
    /// </summary>
    /// <value>
    /// 键值对字典，默认为空字典。
    /// 可用于存储任意扩展信息，如用户偏好、会话配置等。
    /// </value>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// 返回会话信息的字符串表示形式。
    /// </summary>
    /// <returns>
    /// 格式化为 "[{Id}] {Name} ({ProviderType}/{ModelId})" 的字符串。
    /// </returns>
    public override string ToString() => $"[{Id}] {Name} ({ProviderType}/{ModelId})";
}
