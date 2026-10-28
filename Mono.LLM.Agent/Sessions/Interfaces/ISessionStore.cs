// ============================================================================
// 文件：ISessionStore.cs
// 用途：定义会话持久化存储的接口，提供会话数据的增删查改操作。
// 实现方：InMemorySessionStore、JsonFileSessionStore、SQLiteSessionStore
// 调用方：SessionManager、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Sessions;

/// <summary>
/// 定义会话持久化存储的接口，提供会话数据的增删查改操作。
/// </summary>
/// <remarks>
/// <para>此接口抽象了会话的底层存储机制，允许使用不同的存储后端
/// （如内存、数据库、文件系统等）来持久化会话信息。</para>
/// <para>实现类应确保操作的线程安全性，特别是在多线程并发访问场景下。</para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// 异步保存或更新指定的会话信息。
    /// </summary>
    /// <param name="session">要保存的会话信息对象，不能为 null。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步保存操作的任务。</returns>
    /// <exception cref="ArgumentNullException">
    /// 当 <paramref name="session"/> 为 null 时抛出。
    /// </exception>
    /// <remarks>
    /// 如果具有相同 ID 的会话已存在，则执行更新操作；否则执行插入操作。
    /// </remarks>
    Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据会话ID异步获取对应的会话信息。
    /// </summary>
    /// <param name="sessionId">要查找的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示异步获取操作的任务。如果找到匹配的会话则返回对应的 <see cref="SessionInfo"/> 对象；
    /// 如果未找到则返回 null。
    /// </returns>
    Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步获取所有已存储的会话信息列表。
    /// </summary>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示异步查询操作的任务。返回包含所有会话信息的只读列表，
    /// 列表按最后活跃时间降序排列（最近活跃的排在前面）。
    /// </returns>
    Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据会话ID异步删除对应的会话信息。
    /// </summary>
    /// <param name="sessionId">要删除的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表示异步删除操作的任务。</returns>
    /// <remarks>
    /// 如果指定ID的会话不存在，实现类应静默处理而不抛出异常。
    /// </remarks>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步检查指定ID的会话是否存在于存储中。
    /// </summary>
    /// <param name="sessionId">要检查的唯一会话标识符。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示异步检查操作的任务。如果会话存在返回 <c>true</c>；否则返回 <c>false</c>。
    /// </returns>
    Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default);
}
