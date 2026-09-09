using LLMManager.Tools;

namespace LLMManager.Skills;

/// <summary>
/// 表示一个技能的定义，包含技能的元数据、系统提示词和工具签名信息。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillDefinition"/> 是技能系统的核心数据模型，用于描述单个 AI 技能的完整定义。
/// 每个技能包含名称、描述、分类、系统提示词等元数据，以及可选的工具签名和自定义元数据。
/// </para>
/// <para>
/// 技能定义通常通过 <see cref="SkillCatalog"/> 预定义，也可以在运行时动态创建和注册到
/// <see cref="ISkillRegistry"/> 中。
/// </para>
/// </remarks>
public class SkillDefinition
{
    /// <summary>
    /// 获取或设置技能的唯一名称。
    /// </summary>
    /// <value>技能的名称标识符，用于在注册表中唯一标识该技能。默认值为空字符串。</value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能的描述文本。
    /// </summary>
    /// <value>对技能功能的自然语言描述，可用于展示或搜索匹配。默认值为空字符串。</value>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能所属的分类。
    /// </summary>
    /// <value>分类名称，如"开发"、"语言"、"创意"等。默认值为空字符串。</value>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能执行时使用的系统提示词。
    /// </summary>
    /// <value>发送给 LLM 的系统级指令，用于设定技能的行为和输出风格。默认值为空字符串。</value>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能的标签列表。
    /// </summary>
    /// <value>用于搜索和分类的标签集合。默认为空列表。</value>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置技能关联的工具参数签名列表。
    /// </summary>
    /// <value>描述该技能可调用工具的参数信息列表。默认为空列表。</value>
    public List<ToolParameterInfo> ToolSignature { get; set; } = new();

    /// <summary>
    /// 获取或设置技能的自定义元数据键值对。
    /// </summary>
    /// <value>用于存储扩展信息的字典。默认为空字典。</value>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// 获取或设置技能的版本号。
    /// </summary>
    /// <value>语义化版本字符串，默认值为 "1.0"。</value>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// 获取或设置技能的图标标识符。
    /// </summary>
    /// <value>图标名称或路径，用于 UI 展示。默认值为空字符串。</value>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// 返回技能的字符串表示形式，包含分类、名称和版本号。
    /// </summary>
    /// <returns>格式为 "[{Category}] {Name} v{Version}" 的字符串。</returns>
    public override string ToString() => $"[{Category}] {Name} v{Version}";
}
