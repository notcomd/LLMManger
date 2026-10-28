// ============================================================================
// 文件：SkillDefinition.cs
// 用途：表示一个技能的定义，包含技能的元数据、系统提示词和工具签名信息。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：技能来源、注册表、执行器、配置与 Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Skills;

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
/// <para>
/// 定义有两种来源形态：<c>*.skill.json</c>（说明全部写在 <see cref="SystemPrompt"/> 里，一次性进入上下文），
/// 以及标准技能目录 <c>SKILL.md</c>（说明分三层：索引只取 <see cref="Description"/>，
/// 加载时才取 <see cref="Body"/>，资源按需读取）。后者由 <see cref="StandardSkillSource"/> 读取。
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
    /// <summary>
    /// 获取或设置该技能允许调用的工具名称列表。
    /// </summary>
    /// <value>
    /// 工具名集合，不区分大小写。默认为空列表，表示不限制（暴露当前已注册的全部工具）。
    /// </value>
    /// <remarks>
    /// 声明后只有名单内的工具会被提供给模型：未列出的工具既不进入提示词，也无法被执行。
    /// 暴露的工具越少，模型选错工具的概率越低，提示词占用也越小。
    /// </remarks>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>
    /// 获取或设置技能正文，即标准技能文件 frontmatter 之后的 Markdown 内容。
    /// </summary>
    /// <value>
    /// 渐进式披露的第二层。由 <see cref="StandardSkillSource"/> 从 <c>SKILL.md</c> 读入；
    /// 由内存或 JSON 定义的技能留空，直接把说明写在 <see cref="SystemPrompt"/> 里即可。
    /// </value>
    /// <remarks>
    /// 只有在模型显式加载该技能时才应把这段正文送进上下文。它与 <see cref="SystemPrompt"/> 内容一致，
    /// 分开保存是为了让调用方能在「索引阶段」只展示 <see cref="Description"/>，不误把正文一起塞进系统提示词。
    /// </remarks>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能根目录；由文件加载的技能非空，纯内存定义的技能为空字符串。
    /// </summary>
    /// <value>技能的绝对目录路径，用于解析 <see cref="Resources"/> 中的相对路径。</value>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置技能捆绑的资源文件列表。
    /// </summary>
    /// <value>
    /// 技能目录下的附属文件，按 <see cref="SkillResourceKind"/> 分类。默认为空列表。
    /// </value>
    /// <remarks>
    /// 资源不进入提示词，只在模型调用 <c>read_skill_resource</c> 时按需读取。
    /// 这样一份技能可以带几十份参考文档而不占用上下文。
    /// </remarks>
    public IReadOnlyList<SkillResource> Resources { get; set; } = Array.Empty<SkillResource>();

    /// <summary>
    /// 获取或设置技能的许可证标识。
    /// </summary>
    /// <value>如 <c>MIT</c>、<c>Apache-2.0</c>。默认值为空字符串。</value>
    public string License { get; set; } = string.Empty;

    /// <summary>获取技能是否带有可读取的捆绑资源。</summary>
    public bool HasResources => Resources.Count > 0;

    /// <summary>
    /// 已废弃。该字段从未被任何执行路径读取。
    /// </summary>
    /// <value>保留仅为兼容现有调用方，值不参与任何逻辑。</value>
    /// <remarks>
    /// 需要限制技能可调用的工具时，请改用 <see cref="AllowedTools"/>；
    /// 需要让模型感知参数结构时，请在 <c>ToolManager</c> 中注册工具并由模型自动获取 Schema。
    /// 此属性将在下个大版本移除。
    /// </remarks>
    [Obsolete("该字段未被任何执行路径使用，请改用 AllowedTools 声明技能可调用的工具。")]
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
