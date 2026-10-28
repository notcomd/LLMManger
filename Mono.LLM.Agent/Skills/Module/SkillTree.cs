// ============================================================================
// 文件：SkillTree.cs
// 用途：按分类组织的技能树，是技能缓存与自动路由的公共数据结构。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能树节点的类型。
/// </summary>
public enum SkillTreeNodeKind
{
    /// <summary>根节点，不对应任何技能。</summary>
    Root = 0,

    /// <summary>分类节点，聚合同一分类下的技能。</summary>
    Category = 1,

    /// <summary>技能叶子节点。</summary>
    Skill = 2
}

/// <summary>
/// 技能树中的一个节点。
/// </summary>
/// <remarks>
/// 分类节点只保存名称与聚合描述，技能节点持有完整的 <see cref="SkillDefinition"/>。
/// 路径形如 <c>文档/pdf-processing</c>，可直接用于日志与检索。
/// </remarks>
public sealed class SkillTreeNode
{
    internal SkillTreeNode(
        string name,
        string path,
        SkillTreeNodeKind kind,
        string description,
        SkillDefinition? skill,
        IReadOnlyList<SkillTreeNode> children)
    {
        Name = name;
        Path = path;
        Kind = kind;
        Description = description;
        Skill = skill;
        Children = children;
    }

    /// <summary>获取节点名称：分类名或技能名。</summary>
    public string Name { get; }

    /// <summary>获取从根到当前节点的路径，例如 <c>文档/pdf-processing</c>。</summary>
    public string Path { get; }

    /// <summary>获取节点类型。</summary>
    public SkillTreeNodeKind Kind { get; }

    /// <summary>获取描述。分类节点是所有子技能描述的拼接。</summary>
    public string Description { get; }

    /// <summary>获取叶子节点对应的技能定义；分类与根节点为 <c>null</c>。</summary>
    public SkillDefinition? Skill { get; }

    /// <summary>获取子节点。</summary>
    public IReadOnlyList<SkillTreeNode> Children { get; }

    /// <summary>获取是否为技能叶子节点。</summary>
    public bool IsSkill => Skill is not null;

    /// <summary>深度优先枚举自身及全部后代。</summary>
    /// <returns>节点序列。</returns>
    public IEnumerable<SkillTreeNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants())
                yield return node;
        }
    }

    /// <summary>返回 <c>[类型] 路径</c> 形式的文本，便于日志输出。</summary>
    /// <returns>节点描述。</returns>
    public override string ToString() => $"[{Kind}] {Path}";
}

/// <summary>
/// 一次技能匹配的结果。
/// </summary>
/// <param name="Skill">命中的技能。</param>
/// <param name="Score">匹配得分，越高越相关。</param>
/// <param name="Reasons">命中理由，例如「名称出现在输入中」「标签命中：pdf」。</param>
public sealed record SkillMatch(SkillDefinition Skill, double Score, IReadOnlyList<string> Reasons);

/// <summary>
/// 按分类组织的技能树，是技能缓存与自动路由的公共数据结构。
/// </summary>
/// <remarks>
/// <para>
/// 技能列表是扁平的，但人的心智与模型的检索都更接近「分类 → 技能」两层。
/// 树化之后，索引提示词更短（分类只出现一次），匹配也能利用分类作为弱信号。
/// </para>
/// <para>
/// 构建是纯函数：给定同一组技能得到相同的树。缓存与失效由 <see cref="SkillTreeCache"/> 负责。
/// </para>
/// </remarks>
public sealed class SkillTree
{
    private SkillTree(SkillTreeNode root, IReadOnlyList<SkillDefinition> skills)
    {
        Root = root;
        Skills = skills;
    }

    /// <summary>获取根节点。</summary>
    public SkillTreeNode Root { get; }

    /// <summary>获取全部技能，按「分类 → 名称」排序。</summary>
    public IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>获取技能数量。</summary>
    public int Count => Skills.Count;

    /// <summary>
    /// 从技能集合构建树。
    /// </summary>
    /// <param name="skills">技能集合。</param>
    /// <param name="rootName">根节点名称，默认「技能」。</param>
    /// <returns>技能树；输入为空时返回只有根节点的空树。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="skills"/> 为 <c>null</c> 时抛出。</exception>
    public static SkillTree Build(IEnumerable<SkillDefinition> skills, string rootName = "技能")
    {
        ArgumentNullException.ThrowIfNull(skills);

        var ordered = skills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .OrderBy(skill => NormalizeCategory(skill.Category), StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var categories = new List<SkillTreeNode>();

        foreach (var group in ordered.GroupBy(
                     skill => NormalizeCategory(skill.Category),
                     StringComparer.OrdinalIgnoreCase))
        {
            var leaves = group
                .Select(skill => new SkillTreeNode(
                    skill.Name,
                    $"{group.Key}/{skill.Name}",
                    SkillTreeNodeKind.Skill,
                    skill.Description,
                    skill,
                    Array.Empty<SkillTreeNode>()))
                .ToList();

            var summary = string.Join("；", leaves.Select(leaf => leaf.Description).Where(d => d.Length > 0));

            categories.Add(new SkillTreeNode(
                group.Key,
                group.Key,
                SkillTreeNodeKind.Category,
                summary,
                null,
                leaves));
        }

        var root = new SkillTreeNode(
            rootName,
            string.Empty,
            SkillTreeNodeKind.Root,
            string.Empty,
            null,
            categories);

        return new SkillTree(root, ordered);
    }

    /// <summary>
    /// 按名称或路径查找节点。
    /// </summary>
    /// <param name="pathOrName">技能名、分类名或完整路径。</param>
    /// <returns>找到的节点；不存在时为 <c>null</c>。</returns>
    public SkillTreeNode? Find(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return null;

        return Root.SelfAndDescendants().FirstOrDefault(node =>
            node.Path.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)
            || node.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按输入文本挑选相关技能。
    /// </summary>
    /// <param name="query">用户输入或任务描述。</param>
    /// <param name="maxResults">最多返回数量，默认 3。</param>
    /// <param name="minScore">最低得分，默认 0.15。低于该值视为不相关。</param>
    /// <returns>按得分降序排列的匹配结果；无匹配时为空。</returns>
    /// <remarks>
    /// 打分基于「输入词元被技能词汇覆盖的比例」，再叠加名称与标签的直接命中奖励。
    /// 中文按二元切分，英文与数字按词切分，因此不依赖分词器也能工作。
    /// </remarks>
    public IReadOnlyList<SkillMatch> Match(string query, int maxResults = 3, double minScore = 0.15)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<SkillMatch>();

        var queryTokens = SkillText.Tokenize(query);

        if (queryTokens.Count == 0)
            return Array.Empty<SkillMatch>();

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var matches = new List<SkillMatch>();

        foreach (var skill in Skills)
        {
            var vocabulary = SkillText.BuildVocabulary(skill);
            var hits = queryTokens.Where(vocabulary.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var coverage = (double)hits.Count / queryTokens.Count;
            var reasons = new List<string>();

            if (hits.Count > 0)
                reasons.Add($"词汇覆盖 {hits.Count}/{queryTokens.Count}");

            var nameHit = skill.Name.Length > 0
                          && normalizedQuery.Contains(skill.Name.ToLowerInvariant(), StringComparison.Ordinal);

            if (nameHit)
                reasons.Add($"名称命中：{skill.Name}");

            var tagHits = skill.Tags
                .Where(tag => tag.Length > 0
                              && normalizedQuery.Contains(tag.ToLowerInvariant(), StringComparison.Ordinal))
                .ToList();

            if (tagHits.Count > 0)
                reasons.Add("标签命中：" + string.Join("、", tagHits));

            var score = coverage * 0.7
                        + (nameHit ? 0.5 : 0)
                        + Math.Min(tagHits.Count * 0.15, 0.45);

            if (score < minScore)
                continue;

            matches.Add(new SkillMatch(skill, Math.Round(score, 4), reasons));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// 把树渲染成索引提示词：分类一层，技能一层。
    /// </summary>
    /// <param name="options">渲染选项；为 <c>null</c> 时使用默认值。</param>
    /// <returns>索引文本；空树时返回空字符串。</returns>
    public string Render(SkillIndexOptions? options = null)
    {
        var settings = options ?? new SkillIndexOptions();

        if (Skills.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(settings.Heading))
            builder.AppendLine(settings.Heading.Trim());

        if (!string.IsNullOrWhiteSpace(settings.Intro))
        {
            builder.AppendLine();
            builder.AppendLine(settings.Intro.Trim());
        }

        if (!settings.GroupByCategory)
        {
            builder.AppendLine();

            foreach (var skill in Skills)
                AppendSkill(builder, skill, 0, settings);

            return builder.ToString().TrimEnd();
        }

        foreach (var category in Root.Children)
        {
            builder.AppendLine();
            builder.Append("- **").Append(category.Name).AppendLine("**");

            foreach (var node in category.Children.Where(node => node.Skill is not null))
                AppendSkill(builder, node.Skill!, 1, settings);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSkill(
        StringBuilder builder,
        SkillDefinition skill,
        int indent,
        SkillIndexOptions options)
    {
        builder
            .Append(' ', indent * 2)
            .Append("- `").Append(skill.Name).Append("` — ")
            .AppendLine(SkillText.Summarize(skill.Description, options.MaxDescriptionLength));
    }

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? SkillFallback.Category : category.Trim();
}

/// <summary>
/// 技能匹配用的轻量文本处理：中英混排切分、停用词过滤、描述压缩。
/// </summary>
/// <remarks>
/// 不引入分词器是有意为之：技能描述通常几十个字，二元切分足够稳定，
/// 而且没有词典依赖，中文、日文、英文混写都不会失效。
/// </remarks>
internal static class SkillText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "this", "that", "you", "are", "can", "please", "help", "use", "using",
        "帮我", "一下", "怎么", "如何", "这个", "那个", "可以", "需要", "一个", "我们", "你们", "看看",
        "请帮", "帮忙", "现在", "然后", "还是", "就是", "什么",
        "用于", "适用", "使用", "支持", "包含", "提供", "以及", "并且", "或者", "如果", "因为", "所以",
        "但是", "通过", "进行", "相关", "内容", "时候"
    };

    internal static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var latin = new StringBuilder();
        var cjk = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin();
                cjk.Append(ch);
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                FlushCjk();
                latin.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushLatin();
            FlushCjk();
        }

        FlushLatin();
        FlushCjk();

        return tokens;

        void FlushLatin()
        {
            if (latin.Length >= 2)
            {
                var word = latin.ToString();

                if (!StopWords.Contains(word))
                    tokens.Add(word);
            }

            latin.Clear();
        }

        void FlushCjk()
        {
            if (cjk.Length == 0)
                return;

            var run = cjk.ToString();
            cjk.Clear();

            if (run.Length == 1)
            {
                if (!StopWords.Contains(run))
                    tokens.Add(run);

                return;
            }

            for (var i = 0; i < run.Length - 1; i++)
            {
                var bigram = run.Substring(i, 2);

                if (!StopWords.Contains(bigram))
                    tokens.Add(bigram);
            }
        }
    }

    internal static HashSet<string> BuildVocabulary(SkillDefinition skill)
    {
        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in Tokenize(skill.Name))
            vocabulary.Add(token);

        foreach (var token in Tokenize(skill.Description))
            vocabulary.Add(token);

        foreach (var token in Tokenize(skill.Category))
            vocabulary.Add(token);

        foreach (var tag in skill.Tags)
        {
            foreach (var token in Tokenize(tag))
                vocabulary.Add(token);
        }

        return vocabulary;
    }

    internal static string Summarize(string? description, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "（无描述）";

        var single = description
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        while (single.Contains("  ", StringComparison.Ordinal))
            single = single.Replace("  ", " ", StringComparison.Ordinal);

        if (maxLength <= 0 || single.Length <= maxLength)
            return single;

        return single[..maxLength].TrimEnd() + "…";
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u30FF';
}
