using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMManager.Sessions;

/// <summary>
/// 基于 JSON 文件的 <see cref="ISessionStore"/> 实现，将会话数据持久化到本地磁盘。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonFileSessionStore"/> 将所有会话信息以 JSON 格式写入单个文件（默认 sessions.json），
/// 每次读写操作都会同步到磁盘。适用于单机部署、开发调试或轻量级生产场景。
/// </para>
/// <para>
/// 线程安全说明：使用 <c>lock</c> 保护文件 I/O 操作，确保并发访问时数据一致性。
/// 但此实现不适合高并发写场景，如需更高性能请使用 <see cref="SQLiteSessionStore"/> 或数据库方案。
/// </para>
/// <para>
/// 文件格式为包含 <see cref="JsonFileData"/> 结构的 JSON 数组：
/// <code language="json">
/// [
///   { "id": "...", "name": "...", "systemPrompt": "...", ... },
///   { "id": "...", ... }
/// ]
/// </code>
/// </para>
/// </remarks>
public class JsonFileSessionStore : ISessionStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _lock = new();

    /// <summary>获取此存储的文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 初始化 <see cref="JsonFileSessionStore"/> 的新实例，指定数据文件的保存路径。
    /// </summary>
    /// <param name="filePath">JSON 数据文件的完整路径。若文件不存在则自动创建。</param>
    public JsonFileSessionStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        _filePath = filePath;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // 确保目录存在
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 如果文件不存在则创建空数组
        if (!File.Exists(filePath))
            File.WriteAllText(filePath, "[]");
    }

    /// <inheritdoc/>
    public Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            var all = LoadAll();
            var existingIndex = all.FindIndex(s => s.Id == session.Id);

            if (existingIndex >= 0)
                all[existingIndex] = session;
            else
                all.Add(session);

            WriteAll(all);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        List<SessionInfo> all;
        lock (_lock)
            all = LoadAll();

        return Task.FromResult(all.FirstOrDefault(s => s.Id == sessionId));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        List<SessionInfo> all;
        lock (_lock)
            all = LoadAll();

        return Task.FromResult<IReadOnlyList<SessionInfo>>(
            all.OrderByDescending(s => s.LastActiveAt).ToList());
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var all = LoadAll();
            all.RemoveAll(s => s.Id == sessionId);
            WriteAll(all);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        List<SessionInfo> all;
        lock (_lock)
            all = LoadAll();

        return Task.FromResult(all.Any(s => s.Id == sessionId));
    }

    /// <summary>
    /// 从文件加载全部会话数据。调用方需持有锁。
    /// </summary>
    private List<SessionInfo> LoadAll()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return new();

            var data = JsonSerializer.Deserialize<List<SessionInfo>>(json, _jsonOptions);
            return data ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
        catch (IOException)
        {
            return new();
        }
    }

    /// <summary>
    /// 将全部会话数据写入文件。调用方需持有锁。
    /// </summary>
    private void WriteAll(List<SessionInfo> sessions)
    {
        var json = JsonSerializer.Serialize(sessions, _jsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
