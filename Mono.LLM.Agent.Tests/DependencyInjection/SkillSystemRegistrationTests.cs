using Mono.LLM.Agent.Extensions;
using Mono.LLM.Agent.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Mono.LLM.Agent.Tests.DependencyInjection;

/// <summary>
/// 覆盖技能系统的依赖注入注册契约。
/// </summary>
/// <remarks>
/// 这组用例的存在意义：此前 `AddSkillSystem()` 的默认调用会因为
/// ISkillRegistry 工厂自引用而栈溢出，`AddSkill(...)` 则因为懒解析永远不执行。
/// 任何回归都应在此直接被捕获，而不是等到运行时进程静默退出。
/// </remarks>
public class SkillSystemRegistrationTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddMonoLLMAgent();
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddSkillSystem_WithDefaultArguments_LoadsBuiltInCatalog()
    {
        using var provider = BuildProvider(s => s.AddSkillSystem());

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.Equal(SkillCatalog.GetAll().Count(), registry.Count);
        Assert.True(registry.Count > 0);
    }

    [Fact]
    public void AddSkillSystem_WithoutCatalog_LeavesRegistryEmpty()
    {
        using var provider = BuildProvider(s => s.AddSkillSystem(loadCatalog: false));

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void AddSkill_BeforeAddSkillSystem_IsRegistered()
    {
        using var provider = BuildProvider(s =>
        {
            s.AddSkill(new SkillDefinition { Name = "custom-a", SystemPrompt = "p" });
            s.AddSkillSystem(loadCatalog: false);
        });

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.NotNull(registry.Get("custom-a"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void AddSkill_AfterAddSkillSystem_IsRegistered()
    {
        using var provider = BuildProvider(s =>
        {
            s.AddSkillSystem(loadCatalog: false);
            s.AddSkill(new SkillDefinition { Name = "custom-b", SystemPrompt = "p" });
        });

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.NotNull(registry.Get("custom-b"));
    }

    [Fact]
    public void AddSkill_MultipleCalls_RegistersAll()
    {
        using var provider = BuildProvider(s =>
        {
            s.AddSkillSystem(loadCatalog: false)
             .AddSkill(new SkillDefinition { Name = "s1", SystemPrompt = "p" })
             .AddSkill(new SkillDefinition { Name = "s2", SystemPrompt = "p" });
        });

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void AddSkill_Alone_DoesNotPullInBuiltInCatalog()
    {
        using var provider = BuildProvider(s =>
            s.AddSkill(new SkillDefinition { Name = "only-mine", SystemPrompt = "p" }));

        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.Equal(1, registry.Count);
        Assert.NotNull(registry.Get("only-mine"));
    }

    [Fact]
    public void Registry_And_ConcreteType_ResolveToSameInstance()
    {
        using var provider = BuildProvider(s => s.AddSkillSystem(loadCatalog: false));

        var asInterface = provider.GetRequiredService<ISkillRegistry>();
        var asConcrete = provider.GetRequiredService<SkillRegistry>();

        Assert.Same(asInterface, asConcrete);
    }

    [Fact]
    public void AddSkillSystem_RegistersExecutor()
    {
        using var provider = BuildProvider(s => s.AddSkillSystem(loadCatalog: false));

        Assert.NotNull(provider.GetRequiredService<SkillExecutor>());
    }
}
