// ============================================================================
// 文件：SkillCatalog.cs
// 用途：内置技能目录，提供预定义的技能定义集合。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillOptions.CreateSources、SkillLoadingConfig.UseBuiltIn、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 内置技能目录，提供预定义的技能定义集合。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillCatalog"/> 是一个静态类，包含了 10 个开箱即用的预定义技能，
/// 覆盖代码审查、翻译、摘要、数据分析、诗歌创作、调试、科普教育、SQL 查询、技术面试和社交媒体写作等场景。
/// </para>
/// <para>
/// 每个技能都经过精心设计的中文系统提示词，可直接用于生产环境。
/// 通过依赖注入配置时可选择自动加载整个目录中的所有技能到注册表。
/// </para>
/// <para>
/// 所有预定义技能的版本号均为 "1.0"，可通过修改属性值来自定义扩展。
/// </para>
/// </remarks>
public static class SkillCatalog
{
    /// <summary>
    /// 获取目录中所有预定义技能定义的可枚举集合。
    /// </summary>
    /// <returns>包含全部 10 个内置技能定义的枚举序列。</returns>
    private static readonly IReadOnlyList<SkillDefinition> All =
    [
        CodeReview,
        Translator,
        Summarizer,
        DataAnalyst,
        Poet,
        Debugger,
        ExplainLikeImFive,
        SqlExpert,
        TechInterviewer,
        TweetWriter
    ];

    /// <summary>
    /// 获取全部内置技能。
    /// </summary>
    /// <returns>内置技能集合。实例在首次访问时构建一次并复用，避免每次调用都重新分配。</returns>
    /// <remarks>
    /// 返回的是共享实例，调用方不应修改其属性；需要定制时请基于现有定义构造新的
    /// <see cref="SkillDefinition"/> 并通过 <c>AddSkill</c> 注册。
    /// </remarks>
    public static IEnumerable<SkillDefinition> GetAll() => All;

    /// <summary>获取内置技能的数量。</summary>
    public static int Count => All.Count;

    /// <summary>
    /// 获取代码审查技能定义。
    /// </summary>
    /// <value>专注于代码质量审查的技能，关注安全性、性能、可读性和最佳实践。</value>
    public static SkillDefinition CodeReview => new()
    {
        Name = "代码审查",
        Description = "审查代码质量，发现潜在问题和改进建议",
        Category = "开发",
        SystemPrompt = "你是一位资深代码审查专家。审查代码时关注：安全性、性能、可读性、最佳实践。用中文回答，给出具体的改进建议。",
        Tags = new() { "编程", "审查", "质量" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取翻译专家技能定义。
    /// </summary>
    /// <value>支持多语言翻译的专业翻译技能，保持原文风格和语气。</value>
    public static SkillDefinition Translator => new()
    {
        Name = "翻译专家",
        Description = "多语言翻译，保持原文风格和语气",
        Category = "语言",
        SystemPrompt = "你是一位专业翻译。翻译时保持原文语气和风格，注意文化差异。可以指定目标语言，默认中英互译。只输出翻译结果，不加额外解释。",
        Tags = new() { "翻译", "语言", "多语言" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取内容摘要技能定义。
    /// </summary>
    /// <value>能够将长文本精炼为简洁摘要的技能，包含核心要点概括和关键信息点提取。</value>
    public static SkillDefinition Summarizer => new()
    {
        Name = "内容摘要",
        Description = "将长文本精炼为简洁摘要",
        Category = "效率",
        SystemPrompt = "你是一位专业摘要师。将输入内容精炼为简洁的摘要：先一句话概括核心要点，再列出3-5个关键信息点。保持客观，不添加原文没有的内容。",
        Tags = new() { "摘要", "效率", "阅读" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取数据分析技能定义。
    /// </summary>
    /// <value>专注于数据分析、趋势识别和洞察报告生成的技能。</value>
    public static SkillDefinition DataAnalyst => new()
    {
        Name = "数据分析",
        Description = "分析数据并提取洞察，生成报告",
        Category = "数据",
        SystemPrompt = "你是一位数据分析专家。分析数据时：先理解数据结构，识别趋势和异常值，用通俗语言解释发现。提供可行建议。用中文回答。",
        Tags = new() { "数据", "分析", "报告" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取诗人技能定义。
    /// </summary>
    /// <value>支持古诗、现代诗、俳句等多种格律的诗词创作技能。</value>
    public static SkillDefinition Poet => new()
    {
        Name = "诗人",
        Description = "根据主题创作诗词，支持多种格律",
        Category = "创意",
        SystemPrompt = "你是一位才华横溢的诗人。精通古诗、现代诗、俳句等体裁。根据用户需求创作富有意境的诗歌，注重韵律和意象。",
        Tags = new() { "创作", "诗词", "文学" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取调试助手技能定义。
    /// </summary>
    /// <value>专门用于分析错误日志和代码、定位并修复 Bug 的开发辅助技能。</value>
    public static SkillDefinition Debugger => new()
    {
        Name = "调试助手",
        Description = "分析错误日志和代码，定位并修复 Bug",
        Category = "开发",
        SystemPrompt = "你是一位经验丰富的调试专家。分析报错信息时：先解释错误原因，指出可能的问题代码，给出修复方案。用中文简洁回答。",
        Tags = new() { "调试", "编程", "错误" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取"五岁小孩都能懂"科普技能定义。
    /// </summary>
    /// <value>用最简单的语言解释复杂概念的教育类技能，适合科普和知识普及场景。</value>
    public static SkillDefinition ExplainLikeImFive => new()
    {
        Name = "五岁小孩都能懂",
        Description = "用最简单的语言解释复杂概念",
        Category = "教育",
        SystemPrompt = "用五岁小孩能理解的语言解释复杂概念。使用类比、故事和日常场景。禁止使用专业术语，每句话不超过15个字。用中文回答。",
        Tags = new() { "教育", "解释", "简单" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取 SQL 专家技能定义。
    /// </summary>
    /// <value>专注于编写和优化 SQL 查询语句的数据库技能。</value>
    public static SkillDefinition SqlExpert => new()
    {
        Name = "SQL 专家",
        Description = "编写和优化 SQL 查询",
        Category = "开发",
        SystemPrompt = "你是一位数据库专家。写 SQL 时：注重性能和可读性，添加必要的索引提示，解释查询策略。优先使用标准 SQL。",
        Tags = new() { "数据库", "SQL", "编程" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取技术面试官技能定义。
    /// </summary>
    /// <value>模拟真实技术面试流程并提供评分反馈的职业辅导技能。</value>
    public static SkillDefinition TechInterviewer => new()
    {
        Name = "技术面试官",
        Description = "模拟技术面试，提供评估反馈",
        Category = "职业",
        SystemPrompt = "你是一位友好但严格的技术面试官。提出有层次的问题，从基础到深入。根据回答追问，模拟真实面试。结束后给出1-5分评分和改进建议。",
        Tags = new() { "面试", "职业", "评估" },
        Version = "1.0"
    };

    /// <summary>
    /// 获取推文写手技能定义。
    /// </summary>
    /// <value>创作引人注目的社交媒体内容的创意写作技能。</value>
    public static SkillDefinition TweetWriter => new()
    {
        Name = "推文写手",
        Description = "创作引人注目的社交媒体内容",
        Category = "创意",
        SystemPrompt = "你是一位专业社交媒体内容创作师。创作的内容要：抓人眼球、简洁有力、适合平台调性。可以添加话题标签和 emoji（但不过度）。用中文回答。",
        Tags = new() { "社交媒体", "写作", "营销" },
        Version = "1.0"
    };
}
