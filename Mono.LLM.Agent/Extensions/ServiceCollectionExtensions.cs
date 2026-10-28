// ============================================================================
// 文件：ServiceCollectionExtensions.cs
// 用途：为 IServiceCollection 提供 LLM Manager 相关服务的依赖注入扩展方法集合。
// 实现方：ServiceCollectionExtensions
// 调用方：应用启动（DI 注册）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Resilience;
using Mono.LLM.Agent.Sessions;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.MultiAgent;

namespace Mono.LLM.Agent.Extensions;

/// <summary>
/// 为 <see cref="IServiceCollection"/> 提供 LLM Manager 相关服务的依赖注入扩展方法集合。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceCollectionExtensions"/> 提供了一组流畅的扩展方法，用于在 .NET 依赖注入容器中
/// 注册 LLM Manager 框架的所有核心服务。这些方法可以链式组合使用以构建完整的服务配置。
/// </para>
/// <para>
/// 可注册的服务类别：
/// <list type="bullet">
///   <item><b>核心基础设施</b>: <see cref="AddMonoLLMAgent"/>, <see cref="AddLLMClient"/></item>
///   <item><b>功能管理器</b>: <see cref="AddConversationManager"/>, <see cref="AddToolManager(IServiceCollection)"/>,
///         <see cref="AddEmbeddingManager"/>, <see cref="AddStreamingManager"/></item>
///   <item><b>容错机制</b>: <see cref="AddFallbackStrategy"/></item>
///   <item><b>会话管理</b>: <c>AddSessionManager</c></item>
///   <item><b>技能系统</b>: <see cref="AddSkillSystem"/>, <see cref="AddSkill"/></item>
///   <item><b>多智能体</b>: <see cref="AddAgentTeam"/></item>
/// </list>
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 LLM Manager 核心基础服务，包括 <see cref="LLMClientFactory"/> 单例。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 这是使用 LLM Manager 框架的基础注册方法，必须在其他扩展方法之前调用。
    /// 它注册了 <see cref="LLMClientFactory"/> 作为单例服务，该工厂负责根据配置创建不同类型的 LLM 客户端实例。
    /// </remarks>
    public static IServiceCollection AddMonoLLMAgent(this IServiceCollection services)
    {
        services.AddSingleton<LLMClientFactory>();
        return services;
    }

    /// <summary>
    /// 注册一个具名的 LLM 客户端实例到服务容器中。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">用于创建客户端的 LLM 配置信息。</param>
    /// <param name="clientName">可选的客户端名称。若为 <c>null</c> 或 "default"，则额外注册为默认的无键服务。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 客户端以键值单例（Keyed Singleton）方式注册到 DI 容器中，可通过
    /// <c>GetRequiredKeyedService&lt;ILLMClient&gt;(name)</c> 获取。
    /// </para>
    /// <para>
    /// 当 <paramref name="clientName"/> 为 <c>null</c> 或等于 "default" 时，
    /// 除了注册键值服务外，还会额外注册一个无键的默认 <see cref="ILLMClient"/> 单例，
    /// 方便直接注入使用而无需指定名称。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLLMClient(
        this IServiceCollection services,
        LLMConfiguration configuration,
        string? clientName = null)
    {
        var name = clientName ?? configuration.ProviderType.ToString();

        services.AddKeyedSingleton<ILLMClient>(name, (sp, key) =>
        {
            var factory = sp.GetRequiredService<LLMClientFactory>();
            return factory.Create(configuration);
        });

        if (clientName == null || clientName == "default")
        {
            services.AddSingleton(sp =>
            {
                var factory = sp.GetRequiredService<LLMClientFactory>();
                return factory.Create(configuration);
            });
        }

        return services;
    }

    /// <summary>
    /// 注册对话管理器服务，支持按客户端名称选择不同的 LLM 后端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="clientName">可选的要使用的已注册 LLM 客户端名称。若为 <c>null</c> 则使用默认客户端。</param>
    /// <param name="systemPrompt">可选的系统提示词前缀，将在每次对话开始时作为系统消息发送。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 以作用域（Scoped）生命周期注册 <see cref="Conversation.ConversationManager"/>，
    /// 每个作用域创建独立的对话管理器实例，确保不同请求之间的对话上下文隔离。
    /// </remarks>
    public static IServiceCollection AddConversationManager(
        this IServiceCollection services,
        string? clientName = null,
        string? systemPrompt = null)
    {
        services.AddScoped(sp =>
        {
            ILLMClient client;

            if (clientName != null)
            {
                client = sp.GetRequiredKeyedService<ILLMClient>(clientName);
            }
            else
            {
                client = sp.GetRequiredService<ILLMClient>();
            }

            return new Conversation.ConversationManager(client, systemPrompt);
        });

        return services;
    }

    /// <summary>
    /// 注册工具管理器单例服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 以单例（Singleton）生命周期注册 <see cref="Tools.ToolManager"/>，
    /// 全局共享同一工具管理器实例，管理所有可用工具的定义和执行。
    /// </remarks>
    public static IServiceCollection AddToolManager(this IServiceCollection services)
    {
        services.AddSingleton<Tools.ToolManager>();
        return services;
    }

    /// <summary>
    /// 注册工具管理器，并指定执行行为（超时、并发度、参数校验等）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="options">工具执行选项。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="options"/> 为 <c>null</c> 时抛出。</exception>
    /// <remarks>
    /// 与无参重载的区别仅在于是否传入 <see cref="Tools.ToolExecutionOptions"/>；
    /// 若使用 <c>AddMonoLLMAgent(AgentOptions)</c>，工具选项会从配置的 <c>tools</c> 分区自动映射。
    /// </remarks>
    public static IServiceCollection AddToolManager(
        this IServiceCollection services,
        Tools.ToolExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(_ => new Tools.ToolManager(options));
        return services;
    }

    /// <summary>
    /// 注册嵌入向量管理器服务，支持按客户端名称选择不同的 LLM 后端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="clientName">可选的要使用的已注册 LLM 客户端名称。若为 <c>null</c> 则使用默认客户端。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 以作用域（Scoped）生命周期注册 <see cref="Embedding.EmbeddingManager"/>，
    /// 用于文本向量化操作，如语义搜索、文档相似度计算等场景。
    /// </remarks>
    public static IServiceCollection AddEmbeddingManager(
        this IServiceCollection services,
        string? clientName = null)
    {
        services.AddScoped(sp =>
        {
            ILLMClient client;

            if (clientName != null)
            {
                client = sp.GetRequiredKeyedService<ILLMClient>(clientName);
            }
            else
            {
                client = sp.GetRequiredService<ILLMClient>();
            }

            return new Embedding.EmbeddingManager(client);
        });

        return services;
    }

    /// <summary>
    /// 注册流式输出管理器服务，支持按客户端名称选择不同的 LLM 后端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="clientName">可选的要使用的已注册 LLM 客户端名称。若为 <c>null</c> 则使用默认客户端。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 以作用域（Scoped）生命周期注册 <see cref="Streaming.StreamingManager"/>，
    /// 用于处理流式 LLM 输出的实时传输和渲染。
    /// </remarks>
    public static IServiceCollection AddStreamingManager(
        this IServiceCollection services,
        string? clientName = null)
    {
        services.AddScoped(sp =>
        {
            ILLMClient client;

            if (clientName != null)
            {
                client = sp.GetRequiredKeyedService<ILLMClient>(clientName);
            }
            else
            {
                client = sp.GetRequiredService<ILLMClient>();
            }

            return new Streaming.StreamingManager(client);
        });

        return services;
    }

    /// <summary>
    /// 注册容错降级策略服务，配置多个备用 LLM 客户端作为故障转移目标。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="fallbackClientNames">按优先级排序的备用客户端名称列表，主客户端失败时依次尝试。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 以单例（Singleton）生命周期注册 <see cref="FallbackStrategy"/>，
    /// 当主 LLM 客户端不可用时按列表顺序尝试备用客户端，实现高可用性保障。
    /// </remarks>
    public static IServiceCollection AddFallbackStrategy(
        this IServiceCollection services,
        params string[] fallbackClientNames)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<FallbackStrategy>>();
            var clients = fallbackClientNames
                .Select(name => sp.GetRequiredKeyedService<ILLMClient>(name))
                .ToList();

            return new FallbackStrategy(clients, logger);
        });

        return services;
    }

    /// <summary>
    /// 注册会话管理系统，使用内存存储作为会话持久化后端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="defaultConfig">可选的默认 LLM 配置，用于动态创建会话时的客户端初始化。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 注册以下服务：
    /// <list type="bullet">
    ///   <item><see cref="ISessionStore"/> → <see cref="InMemorySessionStore"/>（单例）</item>
    ///   <item><see cref="ISessionManager"/> → <see cref="SessionManager"/>（单例）</item>
    ///   <item><see cref="SessionManager"/> 具体类型（单例，方便直接注入具体类型）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 会话管理器支持多会话并行管理，每个会话维护独立的对话历史和元数据。
    /// 默认使用基于内存的存储实现，适合开发和测试环境。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSessionManager(
        this IServiceCollection services,
        LLMConfiguration? defaultConfig = null)
    {
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<ISessionManager>(sp =>
        {
            var factory = sp.GetRequiredService<LLMClientFactory>();
            var store = sp.GetRequiredService<ISessionStore>();
            var logger = sp.GetService<ILogger<SessionManager>>();
            return new SessionManager(factory, defaultConfig, store, logger);
        });

        services.AddSingleton(sp =>
        {
            var manager = sp.GetRequiredService<ISessionManager>();
            return (SessionManager)manager;
        });

        return services;
    }

    /// <summary>
    /// 注册会话管理系统，使用自定义的会话存储后端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="customStore">自定义的会话存储实现，用于替代默认的内存存储。</param>
    /// <param name="defaultConfig">可选的默认 LLM 配置，用于动态创建会话时的客户端初始化。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 允许传入自定义的 <see cref="ISessionStore"/> 实现（如数据库存储、Redis 存储等），
    /// 以满足生产环境中会话持久化和分布式部署的需求。
    /// </remarks>
    public static IServiceCollection AddSessionManager(
        this IServiceCollection services,
        ISessionStore customStore,
        LLMConfiguration? defaultConfig = null)
    {
        services.AddSingleton<ISessionStore>(customStore);
        services.AddSingleton<ISessionManager>(sp =>
        {
            var factory = sp.GetRequiredService<LLMClientFactory>();
            var store = sp.GetRequiredService<ISessionStore>();
            var logger = sp.GetService<ILogger<SessionManager>>();
            return new SessionManager(factory, defaultConfig, store, logger);
        });

        services.AddSingleton(sp =>
        {
            var manager = sp.GetRequiredService<ISessionManager>();
            return (SessionManager)manager;
        });

        return services;
    }

    /// <summary>
    /// 注册完整的技能系统服务，包括技能注册表、执行器和可选的内置技能目录加载。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="loadCatalog">是否在注册表创建时装载 <see cref="SkillCatalog"/> 中的全部预定义技能。默认为 <c>true</c>。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 注册以下服务：
    /// <list type="bullet">
    ///   <item><see cref="ISkillRegistry"/> → <see cref="SkillRegistry"/>（单例）</item>
    ///   <item><see cref="SkillRegistry"/> 具体类型（单例）</item>
    ///   <item><see cref="SkillExecutor"/>（单例）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 技能装载在注册表工厂内部一次完成：先按 <paramref name="loadCatalog"/> 决定是否装载内置集合，
    /// 再叠加通过 <see cref="AddSkill"/> 累积的自定义技能。工厂不会反向解析 <see cref="ISkillRegistry"/>，
    /// 因此不存在自引用递归。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSkillSystem(this IServiceCollection services, bool loadCatalog = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        GetOrCreateSkillOptions(services).LoadCatalog = loadCatalog;
        EnsureSkillRegistryServices(services);

        return services;
    }

    /// <summary>
    /// 向技能注册表中注册单个自定义技能定义。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="skill">要注册的技能定义。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// 技能定义在构建阶段被收集到 <see cref="SkillRegistrationOptions"/>，
    /// 由 <see cref="ISkillRegistry"/> 的工厂在实例化时统一注册。
    /// 此方法与 <see cref="AddSkillSystem"/> 的调用顺序不影响最终结果，可重复调用以注册多个技能。
    /// </remarks>
    public static IServiceCollection AddSkill(this IServiceCollection services, SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(skill);

        GetOrCreateSkillOptions(services).Add(skill);
        EnsureSkillRegistryServices(services);

        return services;
    }

    /// <summary>
    /// 注册一个磁盘上的标准技能目录（<c>SKILL.md</c>），技能在注册表实例化时加载。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="directoryPath">
    /// 技能根目录：每个子目录各含一个 <c>SKILL.md</c>，或该目录自身就含 <c>SKILL.md</c>。
    /// </param>
    /// <param name="recursive">是否递归扫描子目录，默认为 <c>true</c>。</param>
    /// <param name="validation">可选的校验参数；为 <c>null</c> 时使用规范默认值。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="AddSkill"/> 一样，来源被收集到 <see cref="SkillRegistrationOptions"/>，
    /// 由 <see cref="ISkillRegistry"/> 的工厂统一消费，因此调用顺序不影响结果。
    /// </para>
    /// <para>
    /// 目录扫描在首次解析 <see cref="ISkillRegistry"/> 时同步执行一次，应发生在启动阶段。
    /// 需要拿到逐条加载诊断时，请直接使用 <see cref="StandardSkillSource"/>。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddStandardSkills(
        this IServiceCollection services,
        string directoryPath,
        bool recursive = true,
        SkillValidationOptions? validation = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        GetOrCreateSkillOptions(services).AddSource(new StandardSkillSource(new StandardSkillOptions
        {
            DirectoryPath = directoryPath,
            Recursive = recursive,
            Validation = validation ?? new SkillValidationOptions()
        }));

        EnsureSkillRegistryServices(services);

        return services;
    }

    private static SkillRegistrationOptions GetOrCreateSkillOptions(IServiceCollection services)
    {
        var existing = services
            .LastOrDefault(d => d.ServiceType == typeof(SkillRegistrationOptions))
            ?.ImplementationInstance as SkillRegistrationOptions;

        if (existing != null)
            return existing;

        var created = new SkillRegistrationOptions();
        services.AddSingleton(created);
        return created;
    }

    private static void EnsureSkillRegistryServices(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ISkillRegistry)))
            return;

        services.AddSingleton<ISkillRegistry>(sp =>
        {
            var options = sp.GetRequiredService<SkillRegistrationOptions>();
            var registry = new SkillRegistry();

            if (options.LoadCatalog)
                registry.RegisterAll(SkillCatalog.GetAll());

            foreach (var skill in options.GetSkills())
                registry.Register(skill);

            foreach (var source in options.GetSources())
                registry.RegisterAll(source.LoadSkills());

            return registry;
        });

        services.AddSingleton(sp => (SkillRegistry)sp.GetRequiredService<ISkillRegistry>());
        services.AddSingleton<SkillExecutor>();
    }

    /// <summary>
    /// 注册多智能体团队服务，通过配置回调来组装团队成员和协作模式。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">用于配置团队（添加成员、设置领导/裁判、选择协作模式）的回调委托。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 以单例（Singleton）生命周期注册 <see cref="AgentTeam"/>，通过提供的配置回调完成团队的组装。
    /// 配置回调可以利用 <see cref="AgentTeam"/> 的流式 API 进行灵活配置：
    /// </para>
    /// <code>
    /// services.AddAgentTeam(team => team
    ///     .AddMember(agent1)
    ///     .AddMember(agent2)
    ///     .WithLeader(leaderAgent)
    ///     .UseParallel());
    /// </code>
    /// </remarks>
    public static IServiceCollection AddAgentTeam(
        this IServiceCollection services,
        Action<AgentTeam> configure)
    {
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<LLMClientFactory>();
            var team = new AgentTeam(factory);
            configure(team);
            return team;
        });

        return services;
    }
}
