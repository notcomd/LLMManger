// ============================================================================
// 文件：McpServiceCollectionExtensions.cs
// 用途：MCP 相关的依赖注入扩展方法。
// 实现方：McpServiceCollectionExtensions
// 调用方：应用启动（AddMcpServers）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// MCP 相关的依赖注入扩展方法。
/// </summary>
/// <remarks>
/// <para>
/// 注册只负责「装配」：把配置与 <see cref="McpClientManager"/> 放进容器。真正的连接是异步的，
/// 需要在应用启动阶段显式调用一次：
/// </para>
/// <code>
/// var manager = services.BuildServiceProvider().GetRequiredService&lt;McpClientManager&gt;();
/// await manager.StartAsync();
/// </code>
/// <para>
/// 之所以不在注册时同步等待连接，是因为连接需要网络或子进程，可能耗时数秒；
/// 在 <c>ConfigureServices</c> 里阻塞会拖慢启动，也容易在容器尚未构建完成时产生循环依赖。
/// </para>
/// </remarks>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个 MCP 服务器配置。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="server">服务器配置。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    public static IServiceCollection AddMcpServer(this IServiceCollection services, McpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(server);

        return services.AddMcpServers(server);
    }

    /// <summary>
    /// 注册一组 MCP 服务器配置，并把它们接入 <see cref="ToolManager"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="servers">服务器配置集合。</param>
    /// <returns>服务集合本身，支持链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 每个配置以单例形式注册，<see cref="McpClientManager"/> 也以单例注册（重复调用不会产生多个管理器）。
    /// 工具注册依赖 <see cref="ToolManager"/>，因此应在 <c>AddToolManager()</c> 之后调用；
    /// 即便顺序颠倒也不会失败，只是管理器拿到的是 <c>null</c>，此时只连接、不注册工具。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMcpServers(this IServiceCollection services, params McpServerConfig[] servers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(servers);

        foreach (var server in servers)
        {
            if (server is null)
                continue;

            services.AddSingleton(server);
        }

        services.TryAddSingleton(sp => new McpClientManager(
            sp.GetServices<McpServerConfig>(),
            sp.GetService<ToolManager>(),
            sp.GetService<ILoggerFactory>()));

        return services;
    }
}
