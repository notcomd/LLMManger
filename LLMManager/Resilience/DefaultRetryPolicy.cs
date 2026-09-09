using Microsoft.Extensions.Logging;

namespace LLMManager.Resilience;

/// <summary>
/// 定义重试策略的接口，提供对可重试操作的统一抽象。
/// </summary>
/// <remarks>
/// <para>实现此接口的类型应封装具体的重试逻辑，包括最大重试次数、
/// 重试延迟策略以及可重试异常类型的判断。</para>
/// <para>典型使用场景包括网络请求、外部服务调用等可能因瞬时故障而失败的操作。</para>
/// </remarks>
public interface IRetryPolicy
{
    /// <summary>
    /// 使用当前重试策略执行指定的异步操作。
    /// </summary>
    /// <typeparam name="TResult">操作返回结果的类型。</typeparam>
    /// <param name="operation">要执行的异步操作委托。接收取消令牌和当前尝试次数作为参数。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>表示异步操作结果的任务。</returns>
    /// <exception cref="OperationCanceledException">
    /// 当 <paramref name="cancellationToken"/> 被取消时抛出。
    /// </exception>
    /// <exception cref="Exception">
    /// 当所有重试次数耗尽后操作仍然失败时，抛出最后一次的异常或包装后的异常。
    /// </exception>
    /// <remarks>
    /// <paramref name="operation"/> 委托中的第二个参数（attempt）从1开始计数，
    /// 表示当前是第几次尝试执行该操作。
    /// </remarks>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, int, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认的重试策略实现，支持配置最大重试次数、重试延迟和可重试异常类型。
/// </summary>
/// <remarks>
/// <para>此实现内置支持以下异常类型作为可重试异常：</para>
/// <list type="bullet">
///   <item><see cref="HttpRequestException"/> — HTTP请求异常</item>
///   <item><see cref="TaskCanceledException"/> — 任务取消/超时异常</item>
///   <item><see cref="TimeoutException"/> — 操作超时异常</item>
///   <item><see cref="System.Net.Sockets.SocketException"/> — 套接字异常</item>
///   <item><see cref="IOException"/> — IO异常</item>
/// </list>
/// <para>可通过 <see cref="AddRetryableException{TException}()"/> 方法添加额外的可重试异常类型。</para>
/// <para>重试延迟采用线性退避策略：每次重试的等待时间为基础延迟乘以当前尝试次数。</para>
/// </remarks>
public class DefaultRetryPolicy : IRetryPolicy
{
    private readonly int _maxRetryCount;
    private readonly TimeSpan _retryDelay;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly HashSet<Type> _retryableExceptions;

    /// <summary>
    /// 初始化 <see cref="DefaultRetryPolicy"/> 类的新实例。
    /// </summary>
    /// <param name="maxRetryCount">最大重试次数，默认值为3。总尝试次数为 maxRetryCount + 1（首次执行 + 重试）。</param>
    /// <param name="retryDelay">
    /// 每次重试之间的基础延迟时间，默认值为1秒。
    /// 实际延迟采用线性退避策略计算：baseDelay × attempt。
    /// </param>
    /// <param name="logger">用于记录重试日志的日志记录器实例，可为 null。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 当 <paramref name="maxRetryCount"/> 小于0时抛出。
    /// </exception>
    public DefaultRetryPolicy(
        int maxRetryCount = 3,
        TimeSpan? retryDelay = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _maxRetryCount = maxRetryCount;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _logger = logger;
        _retryableExceptions = new HashSet<Type>
        {
            typeof(HttpRequestException),
            typeof(TaskCanceledException),
            typeof(TimeoutException)
        };
    }

    /// <summary>
    /// 将指定类型的异常添加到可重试异常集合中。
    /// </summary>
    /// <typeparam name="TException">要标记为可重试的异常类型，必须继承自 <see cref="Exception"/>。</typeparam>
    /// <remarks>
    /// 调用此方法后，当操作执行过程中抛出 <typeparamref name="TException"/> 类型的异常时，
    /// 重试策略将触发重试行为而非立即终止。重复添加同一类型不会产生副作用。
    /// </remarks>
    public void AddRetryableException<TException>() where TException : Exception
    {
        _retryableExceptions.Add(typeof(TException));
    }

    /// <summary>
    /// 使用当前重试策略执行指定的异步操作。
    /// </summary>
    /// <typeparam name="TResult">操作返回结果的类型。</typeparam>
    /// <param name="operation">要执行的异步操作委托。接收取消令牌和当前尝试次数（从1开始）作为参数。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>表示异步操作结果的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 当重试逻辑意外耗尽所有尝试时抛出（正常情况下不应触发）。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 当 <paramref name="cancellationToken"/> 被取消时抛出。
    /// </exception>
    /// <remarks>
    /// <para>该方法在每次重试前会记录警告级别的日志，包含当前尝试次数、最大重试次数和错误信息。</para>
    /// <para>如果操作抛出的异常不在可重试异常集合中，则立即向上传播异常而不进行重试。</para>
    /// </remarks>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, int, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= _maxRetryCount + 1; attempt++)
        {
            try
            {
                return await operation(cancellationToken, attempt);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt <= _maxRetryCount)
            {
                _logger?.LogWarning(
                    ex,
                    "Retry attempt {Attempt}/{MaxRetry} after error: {Error}",
                    attempt, _maxRetryCount, ex.Message);

                var delay = TimeSpan.FromTicks(_retryDelay.Ticks * attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Retry logic exhausted unexpectedly.");
    }

    private bool IsRetryable(Exception ex)
    {
        return _retryableExceptions.Contains(ex.GetType()) ||
               ex is System.Net.Sockets.SocketException ||
               ex is IOException;
    }
}
