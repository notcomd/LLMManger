using Microsoft.Extensions.Logging;
using LLMManager.Core;

namespace LLMManager.Resilience;

/// <summary>
/// 提供多LLM提供商之间的故障转移（Fallback）策略。
/// </summary>
/// <remarks>
/// <para>当主客户端操作失败时，此策略会按顺序尝试使用备用客户端执行相同操作，
/// 直到有一个客户端成功返回结果或所有客户端均失败。</para>
/// <para>客户端的尝试顺序为：首先使用传入的主客户端（<paramref name="primaryClient"/>），
/// 然后依次使用构造函数中提供的备用客户端列表中的每个客户端。</para>
/// <para>在切换到备用客户端时，会记录警告级别的日志，包含当前正在切换到的
/// 提供商类型和模型ID信息。</para>
/// </remarks>
public class FallbackStrategy
{
    private readonly List<ILLMClient> _fallbackClients;
    private readonly ILogger? _logger;

    /// <summary>
    /// 初始化 <see cref="FallbackStrategy"/> 类的新实例。
    /// </summary>
    /// <param name="fallbackClients">
    /// 备用LLM客户端列表。当主客户端失败时，将按列表顺序依次尝试这些客户端。
    /// 列表不能为 null，但可以为空。
    /// </param>
    /// <param name="logger">用于记录故障转移日志的日志记录器实例，可为 null。</param>
    /// <exception cref="ArgumentNullException">
    /// 当 <paramref name="fallbackClients"/> 为 null 时抛出。
    /// </exception>
    public FallbackStrategy(IEnumerable<ILLMClient> fallbackClients, ILogger? logger = null)
    {
        _fallbackClients = fallbackClients.ToList();
        _logger = logger;
    }

    /// <summary>
    /// 使用主客户端和备用客户端列表执行指定操作，实现自动故障转移。
    /// </summary>
    /// <typeparam name="TResult">操作返回结果的类型。</typeparam>
    /// <param name="primaryClient">优先使用的LLM主客户端。</param>
    /// <param name="operation">
    /// 要执行的异步操作委托，接收当前客户端实例和取消令牌作为参数。
    /// </param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>表示异步操作结果的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当主客户端和所有备用客户端均执行失败时抛出。
    /// </exception>
    /// <remarks>
    /// <para>执行流程：</para>
    /// <list type="number">
    ///   <item>首先使用 <paramref name="primaryClient"/> 执行操作</item>
    ///   <item>如果主客户端抛出异常，则依次尝试每个备用客户端</item>
    ///   <item>一旦某个客户端成功返回结果，立即返回该结果</item>
    ///   <item>如果所有客户端都失败，抛出 <see cref="InvalidOperationException"/></item>
    /// </list>
    /// <para>注意：最后一个客户端（无论是主客户端还是最后一个备用客户端）
    /// 的异常不会被捕获，将直接向上传播。</para>
    /// </remarks>
    public async Task<TResult> ExecuteWithFallbackAsync<TResult>(
        ILLMClient primaryClient,
        Func<ILLMClient, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var clients = new List<ILLMClient> { primaryClient };
        clients.AddRange(_fallbackClients);

        for (int i = 0; i < clients.Count; i++)
        {
            try
            {
                if (i > 0)
                {
                    _logger?.LogWarning(
                        "Falling back to provider {Provider} (model: {Model})",
                        clients[i].Configuration.ProviderType,
                        clients[i].Configuration.ModelId);
                }

                return await operation(clients[i], cancellationToken);
            }
            catch (Exception ex) when (i < clients.Count - 1)
            {
                _logger?.LogWarning(
                    ex,
                    "Provider {Provider} failed, attempting fallback.",
                    clients[i].Configuration.ProviderType);
            }
        }

        throw new InvalidOperationException("All providers including fallbacks have failed.");
    }
}
