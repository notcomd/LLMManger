using Microsoft.Extensions.DependencyInjection;
using Mono.LLM.Agent.Configuration;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tests.Harness;

namespace Mono.LLM.Agent.Tests.Skills;

/// <summary>
/// 覆盖技能热重载：手动重载的增删改、同名覆盖与还原、故障保护，以及 FileSystemWatcher 的实际触发。
/// </summary>
public class SkillHotReloadTests : SandboxedTest
{
    private string Root => Sandbox.Files.Root;

    private string SkillsRoot => Path.Combine(Root, "skills");

    private void WriteSkill(string directory, string name, string description, string body = "操作步骤：先做这个，再做那个。")
        => Sandbox.Files.Write($"skills/{directory}/SKILL.md", $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);

    private SkillHotReloader CreateReloader(
        ISkillRegistry registry,
        SkillHotReloadOptions? options = null) =>
        new(registry, [SkillsRoot], [new StandardSkillSource(SkillsRoot)], options);

    // ── 手动重载 ──

    [Fact]
    public async Task Reload_AddsNewSkills()
    {
        WriteSkill("alpha", "alpha", "第一个技能。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);

        var result = await reloader.ReloadAsync();

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.NotNull(registry.Get("alpha"));
        Assert.Contains("alpha", reloader.OwnedSkills);
    }

    [Fact]
    public async Task Reload_IsIdempotent()
    {
        WriteSkill("alpha", "alpha", "第一个技能。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);

        await reloader.ReloadAsync();
        var second = await reloader.ReloadAsync();

        Assert.False(second.HasChanges);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task Reload_DetectsContentChange()
    {
        WriteSkill("alpha", "alpha", "旧描述。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);

        await reloader.ReloadAsync();
        WriteSkill("alpha", "alpha", "新描述。用于测试时。");
        var result = await reloader.ReloadAsync();

        Assert.Equal(1, result.Updated);
        Assert.Contains("新描述", registry.Get("alpha")!.Description);
    }

    [Fact]
    public async Task Reload_RemovesDeletedSkill()
    {
        WriteSkill("alpha", "alpha", "第一个技能。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);

        await reloader.ReloadAsync();
        Directory.Delete(Path.Combine(SkillsRoot, "alpha"), recursive: true);
        var result = await reloader.ReloadAsync();

        Assert.Equal(1, result.Removed);
        Assert.Null(registry.Get("alpha"));
    }

    [Fact]
    public async Task Reload_BrokenFile_TakesSkillOfflineWithDiagnostic()
    {
        WriteSkill("alpha", "alpha", "第一个技能。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);

        await reloader.ReloadAsync();
        Sandbox.Files.Write("skills/alpha/SKILL.md", "# 没有 frontmatter");
        var result = await reloader.ReloadAsync();

        Assert.Equal(1, result.Removed);
        Assert.Null(registry.Get("alpha"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError);
    }

    [Fact]
    public async Task Reload_RestoresShadowedSkillOnDelete()
    {
        var builtIn = new SkillDefinition
        {
            Name = "alpha",
            Description = "内置版本。",
            SystemPrompt = "内置提示词"
        };

        var registry = new SkillRegistry();
        registry.Register(builtIn);
        WriteSkill("alpha", "alpha", "文件版本。用于测试时。");

        using var reloader = CreateReloader(registry);
        await reloader.ReloadAsync();

        Assert.Equal("文件版本。用于测试时。", registry.Get("alpha")!.Description);

        Directory.Delete(Path.Combine(SkillsRoot, "alpha"), recursive: true);
        await reloader.ReloadAsync();

        Assert.Same(builtIn, registry.Get("alpha"));
    }

    [Fact]
    public async Task Reload_LeavesForeignSkillsAlone()
    {
        var manual = new SkillDefinition { Name = "manual", SystemPrompt = "手工注册" };
        var registry = new SkillRegistry();
        registry.Register(manual);

        WriteSkill("alpha", "alpha", "第一个技能。用于测试时。");
        using var reloader = CreateReloader(registry);

        await reloader.ReloadAsync();
        Directory.Delete(Path.Combine(SkillsRoot, "alpha"), recursive: true);
        await reloader.ReloadAsync();

        Assert.Same(manual, registry.Get("manual"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task Reload_SourceFailure_KeepsExistingSkills()
    {
        var registry = new SkillRegistry();
        registry.Register(new SkillDefinition { Name = "keep", SystemPrompt = "保留" });

        using var reloader = new SkillHotReloader(registry, [SkillsRoot], [new ThrowingSource()]);

        var result = await reloader.ReloadAsync();

        Assert.False(result.HasChanges);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.NotNull(registry.Get("keep"));
    }

    [Fact]
    public async Task Reload_ReportsAddedAndRemovedTogether()
    {
        WriteSkill("alpha", "alpha", "旧技能。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry);
        await reloader.ReloadAsync();

        Directory.Delete(Path.Combine(SkillsRoot, "alpha"), recursive: true);
        WriteSkill("beta", "beta", "新技能。用于测试时。");
        var result = await reloader.ReloadAsync();

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    // ── FileSystemWatcher ──

    [Fact]
    public async Task Watcher_PicksUpNewSkillFile()
    {
        Directory.CreateDirectory(SkillsRoot);

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry, new SkillHotReloadOptions
        {
            Debounce = TimeSpan.FromMilliseconds(50)
        });

        var added = WaitForReloadAsync(reloader, result => result.Added > 0);
        reloader.Start();

        Assert.True(reloader.IsRunning);

        WriteSkill("beta", "beta", "新增技能。用于测试时。");

        await added.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(registry.Get("beta"));
    }

    [Fact]
    public async Task Watcher_PicksUpContentChange()
    {
        WriteSkill("alpha", "alpha", "旧描述。用于测试时。");

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry, new SkillHotReloadOptions
        {
            Debounce = TimeSpan.FromMilliseconds(50)
        });

        await reloader.ReloadAsync();

        var updated = WaitForReloadAsync(reloader, result => result.Updated > 0);
        reloader.Start();

        WriteSkill("alpha", "alpha", "热更新后的描述。用于测试时。");

        await updated.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("热更新后的描述", registry.Get("alpha")!.Description);
    }

    [Fact]
    public async Task Watcher_IgnoresNonSkillFiles()
    {
        Directory.CreateDirectory(SkillsRoot);

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry, new SkillHotReloadOptions
        {
            Debounce = TimeSpan.FromMilliseconds(50)
        });

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloader.Reloaded += (_, _) => changed.TrySetResult();

        reloader.Start();

        Sandbox.Files.Write("skills/notes.txt", "随手记");
        var completed = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.NotSame(changed.Task, completed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Watcher_StopsAndRestarts()
    {
        Directory.CreateDirectory(SkillsRoot);

        var registry = new SkillRegistry();
        using var reloader = CreateReloader(registry, new SkillHotReloadOptions
        {
            Debounce = TimeSpan.FromMilliseconds(50)
        });

        reloader.Start();
        Assert.True(reloader.IsRunning);

        reloader.Stop();
        Assert.False(reloader.IsRunning);

        var added = WaitForReloadAsync(reloader, result => result.Added > 0);
        reloader.Start();
        WriteSkill("gamma", "gamma", "重启后新增。用于测试时。");

        await added.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(registry.Get("gamma"));
    }

    // ── 配置与装配 ──

    [Fact]
    public void ConfiguredWatch_RequiresPositiveDebounce()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.WatchForChanges = true;
        options.Skills.WatchDebounce = TimeSpan.Zero;

        Assert.Contains(
            options.ValidateAll().Errors,
            issue => issue.Path == "skills.watchDebounce");
    }

    [Fact]
    public void ConfiguredWatch_ExposesDirectoriesAndFileSources()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.UseStandardSkills("skills");
        options.UseJsonSkills("legacy");

        Assert.Equal(2, options.Skills.GetWatchDirectories().Count);
        Assert.Equal(2, options.Skills.CreateFileSources().Count);
        Assert.Equal(3, options.Skills.CreateSources().Count);
    }

    [Fact]
    public void Di_StartsWatcherWhenEnabled()
    {
        WriteSkill("di", "di", "装配时热重载。用于测试时。");

        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.LoadBuiltInCatalog = false;
        options.Skills.WatchForChanges = true;
        options.Skills.WatchDebounce = TimeSpan.FromMilliseconds(50);
        options.UseStandardSkills(SkillsRoot);

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        var reloader = provider.GetRequiredService<SkillHotReloader>();

        Assert.True(reloader.IsRunning);
        Assert.Equal(SkillsRoot, Assert.Single(reloader.Directories));
    }

    private static Task<SkillHotReloadResult> WaitForReloadAsync(
        SkillHotReloader reloader,
        Func<SkillHotReloadResult, bool> predicate)
    {
        var completion = new TaskCompletionSource<SkillHotReloadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        reloader.Reloaded += (_, result) =>
        {
            if (predicate(result))
                completion.TrySetResult(result);
        };

        return completion.Task;
    }

    private sealed class ThrowingSource : ISkillSource
    {
        public string SourceName => "throwing";

        public IEnumerable<SkillDefinition> LoadSkills() =>
            throw new InvalidOperationException("模拟来源故障");
    }
}
