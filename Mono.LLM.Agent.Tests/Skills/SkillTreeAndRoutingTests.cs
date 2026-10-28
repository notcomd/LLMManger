using Microsoft.Extensions.DependencyInjection;
using Mono.LLM.Agent.Configuration;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Skills;

/// <summary>
/// 覆盖技能树、树缓存、自动路由，以及工具集合与工具树的缓存行为。
/// </summary>
public class SkillTreeAndRoutingTests
{
    private static SkillDefinition Pdf() => new()
    {
        Name = "pdf-processing",
        Description = "提取 PDF 文本与表格。用于处理 .pdf 文件时。",
        Category = "文档",
        Tags = ["pdf", "抽取"],
        Body = "1. 用 scripts/extract.py 抽取文本。\n2. 需要字段说明时读 references/REFERENCE.md。"
    };

    private static SkillDefinition Review() => new()
    {
        Name = "code-review",
        Description = "审查代码质量，发现安全问题。用于提交前自检时。",
        Category = "开发",
        Tags = ["代码", "审查"],
        Body = "按安全性、性能、可读性三方面审查。"
    };

    private static SkillRegistry Registry(params SkillDefinition[] skills)
    {
        var registry = new SkillRegistry();

        foreach (var skill in skills)
            registry.Register(skill);

        return registry;
    }

    // ── 技能树 ──

    [Fact]
    public void Tree_GroupsByCategory()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        Assert.Equal(2, tree.Count);
        Assert.Equal(2, tree.Root.Children.Count);
        Assert.Contains(tree.Root.Children, node => node.Name == "文档" && node.Path == "文档");
        Assert.Contains(
            tree.Root.Children,
            node => node.Name == "文档" && node.Children.Single().Path == "文档/pdf-processing");
    }

    [Fact]
    public void Tree_Empty_ProducesEmptyRender()
    {
        var tree = SkillTree.Build([]);

        Assert.Equal(0, tree.Count);
        Assert.Empty(tree.Root.Children);
        Assert.Equal(string.Empty, tree.Render());
    }

    [Fact]
    public void Tree_RenderIsTwoLevel()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var index = tree.Render();

        Assert.Contains("## 可用技能", index);
        Assert.Contains("- **文档**", index);
        Assert.Contains("  - `pdf-processing` — 提取 PDF 文本", index);
        Assert.Contains("- **开发**", index);
        Assert.Contains("  - `code-review` — 审查代码", index);
    }

    [Fact]
    public void Tree_RenderCanFlatten()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var index = tree.Render(new SkillIndexOptions { GroupByCategory = false });

        Assert.DoesNotContain("**文档**", index);
        Assert.Contains("- `pdf-processing`", index);
        Assert.Contains("- `code-review`", index);
    }

    [Fact]
    public void Tree_Find_ByNameAndPath()
    {
        var tree = SkillTree.Build([Pdf()]);

        Assert.Equal("文档/pdf-processing", tree.Find("pdf-processing")!.Path);
        Assert.Equal("pdf-processing", tree.Find("文档/pdf-processing")!.Name);
        Assert.Null(tree.Find("不存在"));
    }

    // ── 匹配 ──

    [Fact]
    public void Match_ChineseInput_PrefersDescriptionOverlap()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var matches = tree.Match("帮我提取这个 PDF 的文本");

        Assert.NotEmpty(matches);
        Assert.Equal("pdf-processing", matches[0].Skill.Name);
        Assert.DoesNotContain(matches, match => match.Skill.Name == "code-review");
    }

    [Fact]
    public void Match_TagHit_IsEnoughSignal()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var matches = tree.Match("这段代码帮我审查一下");

        Assert.NotEmpty(matches);
        Assert.Equal("code-review", matches[0].Skill.Name);
    }

    [Fact]
    public void Match_NameMention_RanksFirst()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var matches = tree.Match("用 code-review 看一下这个提交");

        Assert.Equal("code-review", matches[0].Skill.Name);
        Assert.Contains(matches[0].Reasons, reason => reason.Contains("名称命中"));
    }

    [Fact]
    public void Match_UnrelatedInput_ReturnsEmpty()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        Assert.Empty(tree.Match("今天天气怎么样"));
        Assert.Empty(tree.Match(string.Empty));
    }

    [Fact]
    public void Match_RespectsMaxResults()
    {
        var tree = SkillTree.Build([Pdf(), Review()]);

        var matches = tree.Match("提取 PDF 文本并审查代码质量", maxResults: 1);

        Assert.Single(matches);
    }

    // ── 缓存 ──

    [Fact]
    public void Cache_ReusesTreeWhenNothingChanges()
    {
        var registry = Registry(Pdf());
        var cache = new SkillTreeCache();

        var first = cache.Get(registry);
        var second = cache.Get(registry);

        Assert.Same(first, second);
        Assert.Equal(1, cache.BuildCount);
    }

    [Fact]
    public void Cache_RebuildsOnRegistryChange()
    {
        var registry = Registry(Pdf());
        var cache = new SkillTreeCache();

        cache.Get(registry);
        registry.Register(Review());
        var rebuilt = cache.Get(registry);

        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(2, cache.BuildCount);
    }

    [Fact]
    public void Cache_RebuildsWhenSkillMutatedInPlace()
    {
        var pdf = Pdf();
        var registry = Registry(pdf);
        var cache = new SkillTreeCache();

        cache.Get(registry);
        pdf.Description = "换了一段描述。用于验证指纹时。";
        var rebuilt = cache.Get(registry);

        Assert.Equal(2, cache.BuildCount);
        Assert.Contains("换了一段描述", rebuilt.Skills.Single().Description);
    }

    [Fact]
    public void Cache_InvalidateForcesRebuild()
    {
        var registry = Registry(Pdf());
        var cache = new SkillTreeCache();

        cache.Get(registry);
        cache.Invalidate();
        cache.Get(registry);

        Assert.Equal(2, cache.BuildCount);
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent()
    {
        var left = SkillTreeCache.ComputeFingerprint([Pdf(), Review()]);
        var right = SkillTreeCache.ComputeFingerprint([Review(), Pdf()]);

        Assert.Equal(left, right);
    }

    // ── 自动路由 ──

    [Fact]
    public void Router_PlanTurn_ActivatesWithoutExplicitSelection()
    {
        var router = new SkillRouter(Registry(Pdf(), Review()));

        var plan = router.PlanTurn("帮我提取这个 PDF 的文本", "你是一个助手。");

        Assert.True(plan.HasActivation);
        Assert.Equal("pdf-processing", plan.ActivatedSkills[0].Name);
        Assert.Contains("你是一个助手。", plan.SystemPrompt);
        Assert.Contains("## 可用技能", plan.SystemPrompt);
        Assert.Contains("当前任务相关技能：pdf-processing", plan.SystemPrompt);
        Assert.Contains("scripts/extract.py", plan.SystemPrompt);
    }

    [Fact]
    public void Router_NoMatch_KeepsIndexOnly()
    {
        var router = new SkillRouter(Registry(Pdf()));

        var plan = router.PlanTurn("今天天气怎么样", "你是一个助手。");

        Assert.False(plan.HasActivation);
        Assert.Contains("## 可用技能", plan.SystemPrompt);
        Assert.DoesNotContain("当前任务相关技能", plan.SystemPrompt);
        Assert.DoesNotContain("scripts/extract.py", plan.SystemPrompt);
    }

    [Fact]
    public void Router_Disabled_ProducesNoActivation()
    {
        var router = new SkillRouter(
            Registry(Pdf()),
            options: new SkillRoutingOptions { Enabled = false });

        var plan = router.PlanTurn("帮我提取这个 PDF 的文本", "基础提示");

        Assert.False(plan.HasActivation);
        Assert.Contains("## 可用技能", plan.SystemPrompt);
    }

    [Fact]
    public void Router_BudgetTruncatesBody()
    {
        var router = new SkillRouter(
            Registry(Pdf()),
            options: new SkillRoutingOptions { MaxInstructionCharacters = 20 });

        var plan = router.PlanTurn("帮我提取这个 PDF 的文本");

        Assert.True(plan.HasActivation);
        Assert.Contains("超出本轮预算", plan.SystemPrompt);
        Assert.DoesNotContain("references/REFERENCE.md", plan.SystemPrompt);
    }

    [Fact]
    public void Router_RespectsMaxSkills()
    {
        var router = new SkillRouter(
            Registry(Pdf(), Review()),
            options: new SkillRoutingOptions { MaxSkills = 1, MinScore = 0.05 });

        var plan = router.PlanTurn("提取 PDF 文本并审查代码质量");

        Assert.Single(plan.ActivatedSkills);
    }

    [Fact]
    public void Router_SelectIsCheap_AndUsesCache()
    {
        var registry = Registry(Pdf());
        var cache = new SkillTreeCache();
        var router = new SkillRouter(registry, cache);

        for (var i = 0; i < 5; i++)
            router.Select("提取 PDF 文本");

        Assert.Equal(1, cache.BuildCount);
    }

    // ── 工具缓存与工具树 ──

    [Fact]
    public void ToolCache_ReusesWrappers()
    {
        var tools = new ToolManager();
        tools.RegisterSimpleTool("calc", "计算", _ => Task.FromResult("1"), requiresInput: false);

        var first = tools.GetAITools();
        var second = tools.GetAITools();

        Assert.NotSame(first, second);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void ToolCache_InvalidatedByRegistration()
    {
        var tools = new ToolManager();
        tools.RegisterSimpleTool("calc", "计算", _ => Task.FromResult("1"), requiresInput: false);
        var first = tools.GetAITools();

        tools.RegisterSimpleTool("clock", "时间", _ => Task.FromResult("now"), requiresInput: false);
        var second = tools.GetAITools();

        Assert.Equal(2, second.Count);
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(2, tools.Version);
    }

    [Fact]
    public void ToolCache_InvalidatedByUnregister()
    {
        var tools = new ToolManager();
        tools.RegisterSimpleTool("calc", "计算", _ => Task.FromResult("1"), requiresInput: false);
        tools.GetAITools();

        Assert.True(tools.UnregisterTool("calc"));
        Assert.Empty(tools.GetAITools());
        Assert.Equal(2, tools.Version);
    }

    [Fact]
    public void ToolTree_GroupsByNamespace()
    {
        var tools = new ToolManager();
        tools.RegisterSimpleTool("filesystem__read_file", "读文件", _ => Task.FromResult(""), requiresInput: false);
        tools.RegisterSimpleTool("filesystem__write_file", "写文件", _ => Task.FromResult(""), requiresInput: false);
        tools.RegisterSimpleTool("calc", "计算", _ => Task.FromResult(""), requiresInput: false);

        var tree = tools.GetToolTree();

        Assert.Equal(3, tree.Count);
        Assert.Equal(2, tree.Root.Children.Count);

        var filesystem = Assert.Single(tree.Root.Children, node => node.Name == "filesystem");
        Assert.Equal(2, filesystem.Children.Count);
        Assert.Equal("filesystem/read_file", filesystem.Children[0].Path);

        var general = Assert.Single(tree.Root.Children, node => node.Name == ToolTree.DefaultNamespace);
        Assert.Equal("calc", general.Children.Single().Name);
    }

    [Fact]
    public void ToolTree_IsCached()
    {
        var tools = new ToolManager();
        tools.RegisterSimpleTool("calc", "计算", _ => Task.FromResult(""), requiresInput: false);

        var first = tools.GetToolTree();

        Assert.Same(first, tools.GetToolTree());

        tools.RegisterSimpleTool("clock", "时间", _ => Task.FromResult(""), requiresInput: false);

        Assert.NotSame(first, tools.GetToolTree());
    }

    // ── 与工具集/DI 的协同 ──

    [Fact]
    public void Toolset_SharesTreeCache()
    {
        var registry = Registry(Pdf());
        var cache = new SkillTreeCache();

        var first = new SkillToolset(registry, treeCache: cache);
        var second = new SkillToolset(registry, treeCache: cache);

        Assert.Same(first.Tree, second.Tree);
        Assert.Contains("  - `pdf-processing`", first.BuildSystemPrompt("基础"));
    }

    [Fact]
    public void Di_RegistersRouterAndCacheWithOptions()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.AutoActivate = true;
        options.Skills.MaxAutoSkills = 3;
        options.Skills.AutoActivateThreshold = 0.3;

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<SkillTreeCache>());

        var router = provider.GetRequiredService<SkillRouter>();

        Assert.Equal(3, router.Options.MaxSkills);
        Assert.Equal(0.3, router.Options.MinScore);
    }

    [Fact]
    public void Validate_RejectsInvalidAutoRoutingSettings()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.AutoActivateThreshold = 1.5;
        options.Skills.MaxAutoSkills = -1;
        options.Skills.MaxAutoInstructionCharacters = 0;

        var paths = options.ValidateAll().Errors.Select(issue => issue.Path).ToList();

        Assert.Contains("skills.autoActivateThreshold", paths);
        Assert.Contains("skills.maxAutoSkills", paths);
        Assert.Contains("skills.maxAutoInstructionCharacters", paths);
    }
}
