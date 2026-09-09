using System.Collections.Concurrent;

namespace LLMManager.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> 的内存实现，使用并发字典在进程内存储会话数据。
/// </summary>
/// <remarks>
/// <para>此实现将所有会话信息存储在 <see cref="ConDictionary{TKey,TValue}"/> 中，
/// 提供线程安全的读写操作，适用于开发、测试或单实例部署场景。</para>
/// <para>注意：此存储实现是非持久化的，应用程序重启后所有会话数据将丢失。
/// 如需持久化存储，请实现自定义的 <see cref="ISessionStore"/>（如基于数据库或文件系统的实现）。</para>
/// </remarks>
public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();

    /// <summary>
    /// 异步保存或更新指定的会话信息到内存存储中。
    /// </summary>
    /// <param name="session">要保存的会话信息对象。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌（在此实现中未使用）。</param>
    /// <returns>表示异步保存操作的任务。由于操作是同步完成的，返回 <see cref="Task.CompletedTask"/>。</returns>
    /// <remarks>
    /// 使用会话ID作为键，如果已存在相同ID的会话则覆盖更新。
    /// </remarks>
    public Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 根据会话ID从内存存储中异步获取对应的会话信息。
    /// </summary>
    /// <param name="sessionId">要查找的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌（在此实现中未使用）。</param>
    /// <returns>
    /// 表示异步获取操作的任务。如果找到匹配的会话则返回对应的 <see cref="SessionInfo"/> 对象；
    /// 如果未找到则返回 null。
    /// </returns>
    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    /// <summary>
    /// 异步获取内存存储中所有已存储的会话信息列表。
    /// </summary>
    /// <param name="cancellationToken">用于取消异步操作的令牌（在此实现中未使用）。</param>
    /// <returns>
    /// 表示异步查询操作的任务。返回包含所有会话信息的只读列表，
    /// 列表按最后活跃时间（<see cref="SessionInfo.LastActiveAt"/>）降序排列。
    /// </returns>
    public Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SessionInfo>>(
            _sessions.Values.OrderByDescending(s => s.LastActiveAt).ToList());
    }

    /// <summary>
    /// 根据会话ID从内存存储中异步删除对应的会话信息。
    /// </summary>
    /// <param name="sessionId">要删除的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌（在此实现中未使用）。</param>
    /// <returns>表示异步删除操作的任务。即使指定ID的会话不存在也不会抛出异常。</returns>
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 异步检查指定ID的会话是否存在于内存存储中。
    /// </summary>
    /// <param name="sessionId">要检查的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌（在此实现中未使用）。</param>
    /// <returns>
    /// 表示异步检查操作的任务。如果会话存在返回 <c>true</c>；否则返回 <c>false</c>。
    /// </returns>
    public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessions.ContainsKey(sessionId));
    }
}
