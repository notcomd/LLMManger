// ============================================================================
// 文件：AgentOptionsServiceCollectionExtensions.cs
// 用途：用一份 AgentOptions 完成整个 Agent 的依赖注入装配。
// 实现方：AgentOptionsServiceCollectionExtensions
// 调用方：应用启动（AddMonoLLMAgent）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Mcp;
using Mono.LLM.Agent.Sessions;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 用一份 <see cref="AgentOptions"/> 完成整个 Agent 的依赖注入装配。
/// </summary>
/// <remarks>
/// <para>
/// 与逐个调用 <c>AddLLMClient</c> / <c>AddToolManager</c> / <c>AddSkillSystem</c> / <c>AddMcpServers</c> 等价，
/// 但只读一份配置、只做一次校验。注册项都用 <c>TryAdd</c>，因此调用方仍可在之后替换任意实现。
/// </para>
/// <para>
/// MCP 只做装配不做连接：连接是异步的，需要应用启动时自行
/// <c>await provider.GetRequiredService&lt;McpClientManager&gt;().StartAsync()</c>。
/// </para>
/// </remarks>
public static class AgentOptionsServiceCollectionExtensions
{
    /// <summary>
    /// 按配置注册 Agent 的全部子系统。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="options">配置对象。</param>
    /// <param name="configure">可选的最后一步改写回调，在装配前执行。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <exception cref="ArgumentNullException">
    /// 当 <paramref name="services"/> 或 <paramref name="options"/> 为 <c>null</c> 时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">配置校验存在错误级问题时抛出，消息包含全部错误。</exception>
    public static IServiceCollection AddMonoLLMAgent(
        this IServiceCollection services,
        AgentOptions options,
        Action<AgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        configure?.Invoke(options);
        options.ValidateOrThrow();

        var configuration = options.ToLLMConfiguration();

        services.TryAddSingleton<LLMClientFactory>();
        services.TryAddSingleton<ILLMClient>(sp =>
            sp.GetRequiredService<LLMClientFactory>().Create(configuration));

        services.TryAddSingleton(sp =>
        {
            var tools = new ToolManager(options.ToToolExecutionOptions());

            // 声明了标准技能目录，就把按需加载的两个工具挂上：否则索引里的技能名没有对应入口。
            // 注册放在工具管理器这一侧，保证「先解析谁」都能拿到完整工具集。
            if (options.Skills.StandardDirectories.Count > 0)
                sp.GetRequiredService<SkillToolset>().RegisterTools(tools);

            return tools;
        });

        RegisterSkills(services, options);
        RegisterConversation(services, options);
        RegisterSessions(services, options, configuration);
        RegisterMcp(services, options);

        return services;
    }

    /// <summary>
    /// 从 JSON 文件加载配置并注册。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configurationFile">配置文件路径。</param>
    /// <param name="configure">可选的最后一步改写回调，在装配前执行。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <exception cref="FileNotFoundException">配置文件不存在时抛出。</exception>
    public static IServiceCollection AddMonoLLMAgentFromFile(
        this IServiceCollection services,
        string configurationFile,
        Action<AgentOptions>? configure = null) =>
        services.AddMonoLLMAgent(AgentOptions.Load(configurationFile), configure);

    private static void RegisterSkills(IServiceCollection services, AgentOptions options)
    {
        services.TryAddSingleton<ISkillRegistry>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Mono.LLM.Agent.Skills");
            var registry = new SkillRegistry();

            foreach (var source in options.CreateSkillSources())
            {
                foreach (var skill in source.LoadSkills())
                    registry.Register(skill);

                ReportSourceProblems(source, logger);
            }

            return registry;
        });

        services.TryAddSingleton<SkillExecutor>();
        services.TryAddSingleton<SkillTreeCache>();

        services.TryAddSingleton(sp => new SkillToolset(
            sp.GetRequiredService<ISkillRegistry>(),
            options.Skills.ToToolsetOptions(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<SkillToolset>(),
            sp.GetRequiredService<SkillTreeCache>(),
            options.Skills.ToIndexOptions()));

        services.TryAddSingleton(sp => new SkillRouter(
            sp.GetRequiredService<ISkillRegistry>(),
            sp.GetRequiredService<SkillTreeCache>(),
            options.Skills.ToRoutingOptions(),
            options.Skills.ToIndexOptions()));

        if (options.Skills.WatchForChanges)
        {
            // 解析即启动：注册这个单例的唯一目的就是让文件变化被同步，
            // 拿到实例却还要额外 Start() 只会制造「配了没生效」的困惑。
            services.TryAddSingleton(sp =>
            {
                var reloader = new SkillHotReloader(
                    sp.GetRequiredService<ISkillRegistry>(),
                    options.Skills.GetWatchDirectories(),
                    options.Skills.CreateFileSources(),
                    new SkillHotReloadOptions { Debounce = options.Skills.WatchDebounce },
                    sp.GetService<ILogger<SkillHotReloader>>());

                reloader.Start();
                return reloader;
            });
        }
    }

    private static void RegisterConversation(IServiceCollection services, AgentOptions options)
    {
        services.TryAddScoped(sp =>
        {
            var manager = new ConversationManager(
                sp.GetRequiredService<ILLMClient>(),
                BuildSystemPrompt(sp, options));

            manager.ContextTokenLimit = options.Conversation.ContextTokenLimit;
            return manager;
        });
    }

    /// <summary>
    /// 把标准技能的索引拼到系统提示词之后。只列文件加载的技能，避免把内置的十个技能也塞进提示词。
    /// </summary>
    private static string? BuildSystemPrompt(IServiceProvider provider, AgentOptions options)
    {
        var basePrompt = options.Conversation.SystemPrompt;

        if (!options.Skills.InjectIndexIntoSystemPrompt || options.Skills.StandardDirectories.Count == 0)
            return basePrompt;

        var standardSkills = provider
            .GetRequiredService<ISkillRegistry>()
            .GetAll()
            .Where(skill => !string.IsNullOrWhiteSpace(skill.DirectoryPath));

        var prompt = SkillIndexBuilder.Compose(basePrompt, standardSkills, options.Skills.ToIndexOptions());

        return string.IsNullOrWhiteSpace(prompt) ? basePrompt : prompt;
    }

    private static void RegisterSessions(
        IServiceCollection services,
        AgentOptions options,
        LLMConfiguration configuration)
    {
        services.TryAddSingleton<ISessionStore>(_ => options.CreateSessionStore());

        services.TryAddSingleton<ISessionManager>(sp => new SessionManager(
            sp.GetRequiredService<LLMClientFactory>(),
            configuration,
            sp.GetRequiredService<ISessionStore>(),
            sp.GetService<ILogger<SessionManager>>()));

        services.TryAddSingleton(sp => (SessionManager)sp.GetRequiredService<ISessionManager>());
    }

    private static void RegisterMcp(IServiceCollection services, AgentOptions options)
    {
        var servers = options.CreateMcpServerConfigs();

        if (servers.Count > 0)
            services.AddMcpServers(servers.ToArray());
    }

    private static void ReportSourceProblems(ISkillSource source, ILogger? logger)
    {
        if (logger is null)
            return;

        var diagnostics = SkillSourceDiagnostics.Collect(source);

        foreach (var diagnostic in diagnostics.Where(d => d.Severity != SkillDiagnosticSeverity.Info))
        {
            if (diagnostic.IsError)
                logger.LogError("技能加载问题：{Diagnostic}", diagnostic);
            else
                logger.LogWarning("技能加载问题：{Diagnostic}", diagnostic);
        }
    }
}
