// ============================================================================
// 文件：SessionOptions.cs
// 用途：会话持久化相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Sessions;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 会话存储的实现类型。
/// </summary>
public enum SessionStoreKind
{
    /// <summary>进程内内存存储，重启即丢。适合开发与测试。</summary>
    Memory = 0,

    /// <summary>单个 JSON 文件，全量读写。适合单机小规模。</summary>
    JsonFile = 1,

    /// <summary>SQLite 数据库。适合高并发与需要事务的场景。</summary>
    Sqlite = 2
}

/// <summary>
/// 会话持久化相关的配置。
/// </summary>
public sealed class SessionOptions
{
    /// <summary>获取或设置存储类型，默认 <see cref="SessionStoreKind.Memory"/>。</summary>
    public SessionStoreKind Store { get; set; } = SessionStoreKind.Memory;

    /// <summary>
    /// 获取或设置落盘路径。<see cref="SessionStoreKind.JsonFile"/> 时是 JSON 文件路径，
    /// <see cref="SessionStoreKind.Sqlite"/> 时是数据库文件路径；内存存储忽略此项。
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 按配置创建会话存储实例。
    /// </summary>
    /// <returns>新的 <see cref="ISessionStore"/> 实例。</returns>
    /// <exception cref="InvalidOperationException">需要落盘路径却没有配置时抛出。</exception>
    public ISessionStore CreateStore() => Store switch
    {
        SessionStoreKind.Memory => new InMemorySessionStore(),
        SessionStoreKind.JsonFile => new JsonFileSessionStore(RequireFilePath()),
        SessionStoreKind.Sqlite => new SQLiteSessionStore(RequireFilePath()),
        _ => throw new InvalidOperationException($"未知的会话存储类型 {Store}。")
    };

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("sessions");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (!Enum.IsDefined(Store))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.store", $"未知的存储类型值 {(int)Store}。"));

        if (Store is SessionStoreKind.JsonFile or SessionStoreKind.Sqlite
            && string.IsNullOrWhiteSpace(FilePath))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.filePath", $"{Store} 存储必须配置 filePath。"));
        }

        return issues;
    }

    private string RequireFilePath()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException($"{Store} 存储必须配置 filePath。");

        return FilePath;
    }
}
