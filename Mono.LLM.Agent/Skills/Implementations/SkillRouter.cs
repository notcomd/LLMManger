// ============================================================================
// 文件：SkillRouter.cs
// 用途：技能路由器：根据用户输入自动挑出相关技能，不需要显式指定。
// 实现方：SkillRouter
// 调用方：DI 装配、Harness（每轮自动选技能）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能自动路由的参数。
/// </summary>
public sealed class SkillRoutingOptions
{
    /// <summary>获取或设置是否启用自动路由，默认 <c>true</c>。关闭后退化为「只给索引，由模型自行加载」。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>获取或设置单轮最多自动加载的技能数，默认 2。设 0 等效于关闭。</summary>
    public int MaxSkills { get; set; } = 2;

    /// <summary>获取或设置最低匹配得分，默认 0.15。调高更保守，调低更积极。</summary>
    public double MinScore { get; set; } = 0.15;

    /// <summary>获取或设置自动注入正文的总字符预算，默认 8000。超出部分截断。</summary>
    /// <remarks>
    /// 一个技能正文可能很长。预算用来防止「一次匹配到三个大技能」把上下文塞满。
    /// </remarks>
    public int MaxInstructionCharacters { get; set; } = 8000;

    /// <summary>获取或设置是否在系统提示词里附带技能索引，默认 <c>true</c>。</summary>
    /// <remarks>
    /// 自动路由没命中时，模型仍能通过索引 + <c>load_skill</c> 找到技能；关掉后只能靠路由命中。
    /// </remarks>
    public bool IncludeIndex { get; set; } = true;
}

/// <summary>
/// 一轮对话的技能规划结果。
/// </summary>
/// <remarks>
/// <see cref="SystemPrompt"/> 可直接交给 <c>ConversationManager.SetSystemPrompt</c>，
/// 这样调用方不需要让用户先执行 <c>/use 技能名</c>——相关技能已经按当前输入自动装配好了。
/// </remarks>
public sealed class SkillTurnPlan
{
    internal SkillTurnPlan(
        string systemPrompt,
        IReadOnlyList<SkillDefinition> activatedSkills,
        IReadOnlyList<SkillMatch> matches)
    {
        SystemPrompt = systemPrompt;
        ActivatedSkills = activatedSkills;
        Matches = matches;
    }

    /// <summary>获取本轮应使用的系统提示词：基础提示 + 技能索引 + 命中技能的正文。</summary>
    public string SystemPrompt { get; }

    /// <summary>获取本轮自动加载的技能，按得分降序。</summary>
    public IReadOnlyList<SkillDefinition> ActivatedSkills { get; }

    /// <summary>获取全部候选匹配（含因预算不足未加载的项）。</summary>
    public IReadOnlyList<SkillMatch> Matches { get; }

    /// <summary>获取是否命中了至少一个技能。</summary>
    public bool HasActivation => ActivatedSkills.Count > 0;

    /// <summary>返回本轮加载的技能名，便于日志。</summary>
    /// <returns>逗号分隔的技能名；未命中时为空字符串。</returns>
    public override string ToString() =>
        ActivatedSkills.Count == 0
            ? "未激活技能"
            : "已激活：" + string.Join(", ", ActivatedSkills.Select(skill => skill.Name));
}

/// <summary>
/// 技能路由器：根据用户输入自动挑出相关技能，不需要显式指定。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="SkillToolset"/> 的分工：工具集让「模型自己决定是否加载」，适合技能多、话题跳转频繁的场景；
/// 路由器在每轮开始前按输入先算一次，适合「用户只描述任务、不该关心技能叫什么」的对话产品。
/// 两者共用同一份 <see cref="SkillTreeCache"/>，不会重复构建树。
/// </para>
/// <para>
/// 打分只看名称、描述、分类与标签的词元覆盖度，不调用模型——路由必须足够便宜，
/// 每轮都跑也不能拖慢首字延迟。
/// </para>
/// </remarks>
public sealed class SkillRouter
{
    private readonly ISkillRegistry _registry;
    private readonly SkillTreeCache _cache;
    private readonly SkillRoutingOptions _options;
    private readonly SkillIndexOptions _indexOptions;

    /// <summary>
    /// 初始化 <see cref="SkillRouter"/>。
    /// </summary>
    /// <param name="registry">技能注册表。</param>
    /// <param name="cache">技能树缓存；为 <c>null</c> 时新建一个。</param>
    /// <param name="options">路由参数；为 <c>null</c> 时使用默认值。</param>
    /// <param name="indexOptions">索引渲染参数；为 <c>null</c> 时使用默认值。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="registry"/> 为 <c>null</c> 时抛出。</exception>
    public SkillRouter(
        ISkillRegistry registry,
        SkillTreeCache? cache = null,
        SkillRoutingOptions? options = null,
        SkillIndexOptions? indexOptions = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _cache = cache ?? new SkillTreeCache();
        _options = options ?? new SkillRoutingOptions();
        _indexOptions = indexOptions ?? new SkillIndexOptions();
    }

    /// <summary>获取当前生效的路由参数。</summary>
    public SkillRoutingOptions Options => _options;

    /// <summary>获取缓存的技能树。</summary>
    public SkillTree Tree => _cache.Get(_registry);

    /// <summary>
    /// 按输入挑选相关技能。
    /// </summary>
    /// <param name="input">用户输入或任务描述。</param>
    /// <returns>按得分降序的匹配结果；未启用或无匹配时为空。</returns>
    public IReadOnlyList<SkillMatch> Select(string input)
    {
        if (!_options.Enabled || _options.MaxSkills <= 0 || string.IsNullOrWhiteSpace(input))
            return Array.Empty<SkillMatch>();

        return _cache.Get(_registry).Match(input, _options.MaxSkills, _options.MinScore);
    }

    /// <summary>
    /// 规划一轮对话的系统提示词，并把命中的技能正文一并装配进去。
    /// </summary>
    /// <param name="input">用户输入。</param>
    /// <param name="basePrompt">基础系统提示词，可以为 <c>null</c>。</param>
    /// <returns>本轮规划结果。</returns>
    public SkillTurnPlan PlanTurn(string input, string? basePrompt = null)
    {
        var matches = Select(input);
        var activated = new List<SkillDefinition>();
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(basePrompt))
            sections.Add(basePrompt.Trim());

        if (_options.IncludeIndex)
        {
            var index = _cache.Get(_registry).Render(_indexOptions);

            if (index.Length > 0)
                sections.Add(index);
        }

        var budget = _options.MaxInstructionCharacters;

        foreach (var match in matches)
        {
            if (budget <= 0)
                break;

            var instructions = string.IsNullOrWhiteSpace(match.Skill.Body)
                ? match.Skill.SystemPrompt
                : match.Skill.Body;

            if (string.IsNullOrWhiteSpace(instructions))
                continue;

            sections.Add(RenderInstructions(match.Skill, instructions.Trim(), ref budget));
            activated.Add(match.Skill);
        }

        return new SkillTurnPlan(string.Join("\n\n", sections), activated, matches);
    }

    private static string RenderInstructions(SkillDefinition skill, string instructions, ref int budget)
    {
        var builder = new StringBuilder();
        builder.Append("## 当前任务相关技能：").AppendLine(skill.Name);

        if (!string.IsNullOrWhiteSpace(skill.Description))
            builder.AppendLine().AppendLine(skill.Description.Trim());

        builder.AppendLine();

        if (instructions.Length <= budget)
        {
            builder.Append(instructions);
            budget -= instructions.Length;
            return builder.ToString();
        }

        var allowed = Math.Max(0, budget);

        if (allowed > 0)
            builder.Append(instructions.AsSpan(0, allowed)).AppendLine();

        builder.Append("[正文超出本轮预算，已截断。需要完整说明时调用 load_skill。]");
        budget = 0;

        return builder.ToString();
    }
}
