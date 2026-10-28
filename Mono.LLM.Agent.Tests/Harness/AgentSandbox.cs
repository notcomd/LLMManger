using Microsoft.Extensions.DependencyInjection;

namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 单个测试的隔离运行环境，聚合模型、文件、HTTP 与服务容器四类资源。
/// </summary>
/// <remarks>
/// <para>
/// 一个沙箱对应一条测试。它保证：</para>
/// <list type="bullet">
///   <item>模型不访问网络，调用全部可追溯</item>
///   <item>落盘文件写在独立临时目录，测试结束即清理</item>
///   <item>出站 HTTP 被拦截，可断言发了什么、有没有多发</item>
///   <item>服务容器统一释放，不会把 DI 单例泄漏到别的测试</item>
///   <item>长耗时操作有超时兜底，卡死会失败而不是挂住整轮</item>
/// </list>
/// <para>
/// 推荐通过 <see cref="SandboxedTest"/> 继承使用，也可在需要精细控制时手动创建。
/// </para>
/// </remarks>
public sealed class AgentSandbox : IDisposable
{
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<string> _problems = new();
    private bool _disposed;

    private AgentSandbox(string? label)
    {
        Files = new FileSandbox(label);
    }

    /// <summary>模型与 Agent 夹具。</summary>
    public AgentHarness Agents { get; } = new();

    /// <summary>文件系统沙箱。</summary>
    public FileSandbox Files { get; }

    /// <summary>HTTP 沙箱。</summary>
    public HttpSandbox Http { get; } = new();

    /// <summary>
    /// 单次沙箱操作的超时上限，默认 15 秒。
    /// </summary>
    /// <remarks>
    /// 用于捕获死锁、未响应取消令牌等场景，避免测试挂住整轮执行。
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>运行期间积累的问题（超时、清理失败等）。</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems)
                return _problems.ToList();
        }
    }

    /// <summary>
    /// 创建一个新的沙箱。
    /// </summary>
    /// <param name="label">标签，用于命名临时目录，便于定位来源测试。</param>
    public static AgentSandbox Create(string? label = null) => new(label);

    /// <summary>
    /// 构建一个服务容器，生命周期由沙箱管理。
    /// </summary>
    /// <param name="configure">服务注册回调。</param>
    /// <returns>已构建的容器。</returns>
    public ServiceProvider Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var services = new ServiceCollection();
        configure(services);

        var provider = services.BuildServiceProvider();

        lock (_providers)
            _providers.Add(provider);

        return provider;
    }

    /// <summary>
    /// 在超时保护下执行异步操作。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="action">要执行的操作。</param>
    /// <returns>操作结果。</returns>
    /// <exception cref="TimeoutException">操作超过 <see cref="Timeout"/> 时抛出。</exception>
    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AddProblem($"操作超过 {Timeout.TotalSeconds:0.#}s 未完成，可能存在死锁或未响应取消令牌。");
            throw;
        }
    }

    /// <summary>
    /// 在超时保护下执行异步操作。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await RunAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>清空所有 Agent 的调用记录，便于在一个测试内分阶段断言。</summary>
    public void ClearCalls()
    {
        foreach (var agent in Agents.All)
            Agents.ScriptOrNull(agent.Name)?.ClearCalls();
    }

    /// <summary>记录一条问题，供测试收尾时统一暴露。</summary>
    /// <param name="problem">问题描述。</param>
    public void AddProblem(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        lock (_problems)
            _problems.Add(problem);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_providers)
        {
            foreach (var provider in _providers)
            {
                try
                {
                    provider.Dispose();
                }
                catch (Exception ex)
                {
                    AddProblem($"释放服务容器失败：{ex.Message}");
                }
            }

            _providers.Clear();
        }

        Files.Dispose();

        if (Files.CleanupError != null)
            AddProblem($"临时目录清理失败：{Files.CleanupError}");
    }
}

/// <summary>
/// 为每个测试自动准备 <see cref="AgentSandbox"/> 的基类。
/// </summary>
/// <remarks>
/// <para>
/// 继承后直接用 <see cref="Sandbox"/> 即可，无需在测试里写 try/finally 或实现 <c>IDisposable</c>。
/// </para>
/// <para>
/// 沙箱在退出时若记录了问题（超时、目录清理失败、容器释放异常），该测试会被判定失败——
/// 资源泄漏应当被看见，而不是悄悄溜过去。
/// </para>
/// </remarks>
public abstract class SandboxedTest : IAsyncLifetime
{
    /// <summary>当前测试的沙箱。</summary>
    protected AgentSandbox Sandbox { get; private set; } = null!;

    /// <inheritdoc/>
    public virtual Task InitializeAsync()
    {
        Sandbox = AgentSandbox.Create(GetType().Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual Task DisposeAsync()
    {
        Sandbox.Dispose();

        var problems = Sandbox.Problems;
        if (problems.Count > 0)
            throw new InvalidOperationException("沙箱退出时发现问题：" + string.Join(" | ", problems));

        return Task.CompletedTask;
    }
}
