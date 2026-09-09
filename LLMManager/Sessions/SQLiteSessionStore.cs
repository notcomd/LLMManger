using LLMManager.Core;
using Microsoft.Data.Sqlite;

namespace LLMManager.Sessions;

/// <summary>
/// 基于 SQLite 数据库的 <see cref="ISessionStore"/> 实现，提供高性能、可靠的会话持久化能力。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SQLiteSessionStore"/> 使用 SQLite 作为后端存储引擎，通过 ADO.NET 执行参数化查询，
/// 支持完整的 CRUD 操作和并发安全的读写访问。
/// </para>
/// <para>
/// 首次使用时会自动创建表结构（sessions 表 + metadata 表），无需手动建表。
/// 每个连接使用 WAL（Write-Ahead Logging）模式以提升并发读性能。
/// </para>
/// <para>
/// 此类实现了 <see cref="IDisposable"/> 接口，使用完毕后应调用 <see cref="Dispose"/> 方法释放数据库连接资源。
/// </para>
/// <para>
/// 典型用法：
/// <code>
/// await using var store = new SQLiteSessionStore("data/sessions.db");
/// await store.SaveAsync(session);
/// var loaded = await store.GetAsync(sessionId);
/// </code>
/// </para>
/// </remarks>
public class SQLiteSessionStore : ISessionStore, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _initialized;

    /// <summary>获取数据库文件的路径。</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 初始化 <see cref="SQLiteSessionStore"/> 的新实例，指定 SQLite 数据库文件路径。
    /// </summary>
    /// <param name="databasePath">SQLite 数据库文件路径（如 "data/sessions.db"）。若文件不存在则自动创建。</param>
    public SQLiteSessionStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));

        DatabasePath = databasePath;
        _connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;";
    }

    /// <summary>
    /// 获取或创建内部的 SQLite 连接实例（延迟初始化）。
    /// </summary>
    private SqliteConnection Connection
    {
        get
        {
            if (_connection == null)
            {
                _connection = new SqliteConnection(_connectionString);
                _connection.Open();
            }
            return _connection;
        }
    }

    /// <summary>
    /// 初始化数据库表结构（首次调用时执行）。
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        var conn = Connection;
        await conn.ExecuteAsync(ct, @"
            CREATE TABLE IF NOT EXISTS sessions (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL DEFAULT '',
                system_prompt   TEXT NOT NULL DEFAULT '',
                provider_type   TEXT NOT NULL DEFAULT 'Unknown',
                model_id        TEXT NOT NULL DEFAULT '',
                created_at      TEXT NOT NULL,
                last_active_at  TEXT NOT NULL,
                tags            TEXT DEFAULT '[]',
                message_count   INTEGER NOT NULL DEFAULT 0,
                total_input_tokens  INTEGER NOT NULL DEFAULT 0,
                total_output_tokens INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS session_metadata (
                session_id  TEXT NOT NULL,
                key         TEXT NOT NULL,
                value       TEXT NOT NULL,
                PRIMARY KEY (session_id, key),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );");

        _initialized = true;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken);

        var conn = Connection;
        var tagsJson = System.Text.Json.JsonSerializer.Serialize(session.Tags ?? new());
        var nowUtc = DateTime.UtcNow.ToString("O");

        await conn.ExecuteAsync(cancellationToken, @"
            INSERT INTO sessions (id, name, system_prompt, provider_type, model_id, created_at, last_active_at, tags, message_count, total_input_tokens, total_output_tokens)
            VALUES (@id, @name, @sp, @pt, @mid, @ca, @laa, @tags, @mc, @tit, @tot)
            ON CONFLICT(id) DO UPDATE SET
                name          = excluded.name,
                system_prompt = excluded.system_prompt,
                provider_type = excluded.provider_type,
                model_id      = excluded.model_id,
                last_active_at= excluded.last_active_at,
                tags          = excluded.tags,
                message_count = excluded.message_count,
                total_input_tokens  = excluded.total_input_tokens,
                total_output_tokens = excluded.total_output_tokens;",
            ("@id", session.Id),
            ("@name", session.Name),
            ("@sp", session.SystemPrompt),
            ("@pt", session.ProviderType.ToString()),
            ("@mid", session.ModelId),
            ("@ca", session.CreatedAt.ToString("O")),
            ("@laa", session.LastActiveAt.ToString("O")),
            ("@tags", tagsJson),
            ("@mc", session.MessageCount),
            ("@tit", session.TotalInputTokens),
            ("@tot", session.TotalOutputTokens));

        // 更新 metadata
        await conn.ExecuteAsync(cancellationToken,
            "DELETE FROM session_metadata WHERE session_id = @sid;",
            ("@sid", session.Id));

        if (session.Metadata != null)
        {
            foreach (var kvp in session.Metadata)
            {
                await conn.ExecuteAsync(cancellationToken,
                    "INSERT OR IGNORE INTO session_metadata (session_id, key, value) VALUES (@sid, @key, @val);",
                    ("@sid", session.Id), ("@key", kvp.Key), ("@val", kvp.Value));
            }
        }
    }

    /// <inheritdoc/>
    public async Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var conn = Connection;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, system_prompt, provider_type, model_id,
                   created_at, last_active_at, tags, message_count,
                   total_input_tokens, total_output_tokens
            FROM sessions WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var info = ReadSessionFromReader(reader);
        await LoadMetadataAsync(conn, info, cancellationToken);
        return info;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var conn = Connection;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, system_prompt, provider_type, model_id,
                   created_at, last_active_at, tags, message_count,
                   total_input_tokens, total_output_tokens
            FROM sessions ORDER BY datetime(last_active_at) DESC;";

        var results = new List<SessionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var info = ReadSessionFromReader(reader);
            results.Add(info);
        }

        // 批量加载 metadata
        foreach (var info in results)
            await LoadMetadataAsync(conn, info, cancellationToken);

        return results;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var conn = Connection;

        // 外键级联删除 metadata
        await conn.ExecuteAsync(cancellationToken,
            "DELETE FROM session_metadata WHERE session_id = @id;",
            ("@id", sessionId));
        await conn.ExecuteAsync(cancellationToken,
            "DELETE FROM sessions WHERE id = @id;",
            ("@id", sessionId));
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var conn = Connection;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", sessionId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    /// <summary>释放数据库连接资源。</summary>
    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    // ── 私有辅助方法 ──

    private static SessionInfo ReadSessionFromReader(SqliteDataReader reader)
    {
        var info = new SessionInfo
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            SystemPrompt = reader.GetString(2),
            ProviderType = ParseProviderType(reader.GetString(3)),
            ModelId = reader.GetString(4),
            CreatedAt = DateTime.Parse(reader.GetString(5)),
            LastActiveAt = DateTime.Parse(reader.GetString(6)),
            MessageCount = reader.GetInt32(8),
            TotalInputTokens = reader.GetInt32(9),
            TotalOutputTokens = reader.GetInt32(10)
        };

        // 反序列化 tags
        if (!reader.IsDBNull(7))
        {
            var tagsJson = reader.GetString(7);
            info.Tags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new();
        }

        return info;
    }

    private static async Task LoadMetadataAsync(SqliteConnection conn, SessionInfo info, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM session_metadata WHERE session_id = @id;";
        cmd.Parameters.AddWithValue("@id", info.Id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            info.Metadata[key] = value;
        }
    }

    private static LLMProviderType ParseProviderType(string value)
    {
        return Enum.TryParse<LLMProviderType>(value, out var result) ? result : LLMProviderType.OpenAI;
    }
}

/// <summary>
/// SqliteCommand 的异步执行扩展方法。
/// </summary>
file static class SqliteExtensions
{
    /// <summary>异步执行非查询 SQL 命令。</summary>
    internal static async Task ExecuteAsync(
        this SqliteConnection conn, CancellationToken ct, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
