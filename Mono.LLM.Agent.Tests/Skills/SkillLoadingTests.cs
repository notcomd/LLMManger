using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tests.Harness;

namespace Mono.LLM.Agent.Tests.Skills;

/// <summary>
/// 覆盖技能加载链路：内置目录缓存、文件加载的异步与校验、多来源合并顺序。
/// </summary>
/// <remarks>
/// 落盘位置由 <see cref="FileSandbox"/> 提供：每个测试一个独立临时目录，
/// 结束后自动清理，不再需要手写构造/析构与 try-catch。
/// </remarks>
public class SkillLoadingTests : SandboxedTest
{
    private string Workspace => Sandbox.Files.Root;

    private string WriteSkillFile(string fileName, string content)
        => Sandbox.Files.Write(fileName, content);

    // ── 内置目录 ──

    [Fact]
    public void Catalog_ReturnsCachedInstances()
    {
        var first = SkillCatalog.GetAll().ToArray();
        var second = SkillCatalog.GetAll().ToArray();

        Assert.Equal(first.Length, second.Length);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void Catalog_CountMatchesEnumeration()
    {
        Assert.Equal(SkillCatalog.GetAll().Count(), SkillCatalog.Count);
    }

    // ── 文件来源 ──

    [Fact]
    public async Task FileSource_LoadsValidSkills()
    {
        WriteSkillFile("a.skill.json", """
            { "name": "技能A", "systemPrompt": "你是 A", "description": "d", "category": "测试" }
            """);
        WriteSkillFile("b.skill.json", """
            { "name": "技能B", "systemPrompt": "你是 B" }
            """);

        var source = new FileBasedSkillSource(Workspace);
        var skills = await source.LoadSkillsAsync();

        Assert.Equal(2, skills.Count);
        Assert.Empty(source.LoadErrors);
        Assert.Contains(skills, s => s.Name == "技能A" && s.Category == "测试");
        Assert.Contains(skills, s => s.Name == "技能B" && s.Category == "未分类");
    }

    [Fact]
    public async Task FileSource_MissingSystemPrompt_IsRejectedWithReason()
    {
        WriteSkillFile("bad.skill.json", """
            { "name": "无提示词技能" }
            """);

        var source = new FileBasedSkillSource(Workspace);
        var skills = await source.LoadSkillsAsync();

        Assert.Empty(skills);
        var error = Assert.Single(source.LoadErrors);
        Assert.Contains("systemPrompt", error.Message);
    }

    [Fact]
    public async Task FileSource_MissingName_IsRejectedWithReason()
    {
        WriteSkillFile("bad.skill.json", """
            { "systemPrompt": "你是 X" }
            """);

        var source = new FileBasedSkillSource(Workspace);
        var skills = await source.LoadSkillsAsync();

        Assert.Empty(skills);
        Assert.Contains(source.LoadErrors, e => e.Message.Contains("name"));
    }

    [Fact]
    public async Task FileSource_MalformedJson_DoesNotBlockOtherFiles()
    {
        WriteSkillFile("ok.skill.json", """
            { "name": "好的", "systemPrompt": "你是好的" }
            """);
        WriteSkillFile("broken.skill.json", "{ not json");

        var source = new FileBasedSkillSource(Workspace);
        var skills = await source.LoadSkillsAsync();

        Assert.Single(skills);
        Assert.Single(source.LoadErrors);
        Assert.Contains("JSON", source.LoadErrors[0].Message);
    }

    [Fact]
    public async Task FileSource_LoadErrorsAreResetBetweenRuns()
    {
        WriteSkillFile("broken.skill.json", "{ not json");
        var source = new FileBasedSkillSource(Workspace);

        await source.LoadSkillsAsync();
        Assert.Single(source.LoadErrors);

        File.Delete(Sandbox.Files.GetPath("broken.skill.json"));
        await source.LoadSkillsAsync();

        Assert.Empty(source.LoadErrors);
    }

    [Fact]
    public async Task FileSource_ReadsAllowedTools()
    {
        WriteSkillFile("tooled.skill.json", """
            { "name": "带工具的技能", "systemPrompt": "你是 X", "allowedTools": ["get_time", "search"] }
            """);

        var source = new FileBasedSkillSource(Workspace);
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Equal(new[] { "get_time", "search" }, skill.AllowedTools);
    }

    [Fact]
    public void FileSource_SyncApiStillWorks()
    {
        WriteSkillFile("a.skill.json", """
            { "name": "同步", "systemPrompt": "你是同步" }
            """);

        var source = new FileBasedSkillSource(Workspace);
        var skills = source.LoadSkills().ToList();

        Assert.Single(skills);
    }

    [Fact]
    public async Task FileSource_MissingDirectory_ReturnsEmpty()
    {
        var source = new FileBasedSkillSource(Sandbox.Files.GetPath("not-exist"));

        Assert.Empty(await source.LoadSkillsAsync());
    }

    // ── 多来源合并 ──

    [Fact]
    public async Task Composite_LaterSourceOverridesSameName()
    {
        var composite = new CompositeSkillSource()
            .Add(new InMemorySkillSource("s1", new[]
            {
                new SkillDefinition { Name = "dup", SystemPrompt = "来自 s1" }
            }))
            .Add(new InMemorySkillSource("s2", new[]
            {
                new SkillDefinition { Name = "dup", SystemPrompt = "来自 s2" }
            }));

        var skills = await composite.LoadSkillsAsync();

        var merged = Assert.Single(skills);
        Assert.Equal("来自 s2", merged.SystemPrompt);
    }

    [Fact]
    public async Task Composite_IsCaseInsensitiveOnNames()
    {
        var composite = new CompositeSkillSource()
            .Add(new InMemorySkillSource("s1", new[]
            {
                new SkillDefinition { Name = "CodeReview", SystemPrompt = "a" }
            }))
            .Add(new InMemorySkillSource("s2", new[]
            {
                new SkillDefinition { Name = "codereview", SystemPrompt = "b" }
            }));

        Assert.Single(await composite.LoadSkillsAsync());
    }

    [Fact]
    public async Task Composite_Empty_ReturnsEmpty()
    {
        Assert.Empty(await new CompositeSkillSource().LoadSkillsAsync());
    }

    // ── 加载配置 ──

    [Fact]
    public async Task LoadingConfig_LoadIntoAsync_RegistersSkills()
    {
        WriteSkillFile("a.skill.json", """
            { "name": "文件技能", "systemPrompt": "你是文件技能" }
            """);

        var registry = new SkillRegistry();
        var config = new SkillLoadingConfig().UseDirectory(Workspace);

        var count = await config.LoadIntoAsync(registry);

        Assert.Equal(1, count);
        Assert.NotNull(registry.Get("文件技能"));
    }

    [Fact]
    public async Task LoadingConfig_MergesBuiltInAndDirectory()
    {
        WriteSkillFile("a.skill.json", """
            { "name": "自定义", "systemPrompt": "x" }
            """);

        var registry = new SkillRegistry();
        var config = new SkillLoadingConfig()
            .UseBuiltIn()
            .UseDirectory(Workspace);

        var count = await config.LoadIntoAsync(registry);

        Assert.Equal(SkillCatalog.Count + 1, count);
        Assert.NotNull(registry.Get("自定义"));
    }
}
