// ============================================================================
// 文件：McpOAuthToken.cs
// 用途：OAuth 2.1 访问令牌。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpOAuthTokenProvider、McpOAuthClient
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// OAuth 2.1 访问令牌。
/// </summary>
/// <remarks>
/// 字段名沿用 RFC 6749 的蛇形命名，便于直接把令牌端点的响应反序列化进来；
/// <see cref="ExpiresAt"/> 不是协议字段，由客户端按 <c>expires_in</c> 换算后写入，
/// 并随令牌一起持久化，避免重启后把过期令牌当成有效令牌。
/// </remarks>
public sealed class McpOAuthToken
{
    /// <summary>访问令牌。</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>令牌类型，规范固定为 <c>Bearer</c>。</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    /// <summary>有效期（秒）；服务端未给出时该值为 <c>null</c>，表示不过期。</summary>
    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    /// <summary>刷新令牌；服务端未下发时为 <c>null</c>。</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>授权范围，空格分隔。</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    /// <summary>本地换算出的绝对过期时间。</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// 判断令牌是否已经过期（考虑提前量）。
    /// </summary>
    /// <param name="clockSkew">提前判过期的余量，用于吸收时钟漂移与服务端处理延迟。</param>
    /// <remarks>
    /// 没有 <see cref="ExpiresAt"/> 时一律视为有效——规范允许服务端不返回 <c>expires_in</c>，
    /// 这种情况下由服务端在失效时返回 401，客户端再刷新。
    /// </remarks>
    public bool IsExpired(TimeSpan clockSkew) =>
        ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow + clockSkew >= expiresAt;
}

/// <summary>
/// 本地保存的 OAuth 会话：令牌加上动态注册得到的客户端凭据。
/// </summary>
/// <remarks>
/// 把两者放在一起持久化，是为了避免每次启动都重新做一次动态客户端注册——
/// 有些授权服务器会对注册频率做限制，重复注册也会在服务端留下无用记录。
/// </remarks>
public sealed class McpOAuthSession
{
    /// <summary>当前的访问令牌。</summary>
    public McpOAuthToken? Token { get; set; }

    /// <summary>动态注册（或预配置）得到的客户端标识。</summary>
    public string? ClientId { get; set; }

    /// <summary>客户端密钥；公开客户端为 <c>null</c>。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>签发该客户端的授权服务器地址，用于发现结果变化时判断缓存是否仍然可用。</summary>
    public string? AuthorizationServer { get; set; }
}

/// <summary>
/// OAuth 会话的持久化抽象。
/// </summary>
/// <remarks>
/// 令牌等同于密码，默认实现把文件放在用户本地应用数据目录并收紧权限；
/// 有更高要求时可以对接系统钥匙串或密钥管理服务。
/// </remarks>
public interface IMcpOAuthStore
{
    /// <summary>读取会话。</summary>
    /// <param name="key">缓存键，通常由服务器名与资源地址组合而成。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已保存的会话；不存在或无法解析时返回 <c>null</c>。</returns>
    ValueTask<McpOAuthSession?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>保存会话（覆盖）。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="session">要保存的会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SetAsync(string key, McpOAuthSession session, CancellationToken cancellationToken = default);

    /// <summary>删除会话。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 进程内的会话存储，仅适合测试或明确接受「重启后重新授权」的场景。
/// </summary>
public sealed class InMemoryMcpOAuthStore : IMcpOAuthStore
{
    private readonly Dictionary<string, McpOAuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <inheritdoc />
    public ValueTask<McpOAuthSession?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return ValueTask.FromResult(_sessions.TryGetValue(key, out var session) ? session : null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, McpOAuthSession session, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _sessions[key] = session;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _sessions.Remove(key);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 把 OAuth 会话保存为本地 JSON 文件。
/// </summary>
/// <remarks>
/// <para>
/// 默认目录为当前用户的本地应用数据目录（Windows 的 <c>%LOCALAPPDATA%</c>、
/// Linux 的 <c>~/.local/share</c>、macOS 的 <c>~/Library/Application Support</c>）下的
/// <c>Mono.LLM.Agent/mcp-oauth</c>。文件名取缓存键的 SHA-256，避免服务器名里的
/// 路径分隔符或非法字符造成问题，也避免把服务器地址直接暴露在目录列表里。
/// </para>
/// <para>
/// 写入采用「临时文件 + 替换」，防止进程在写一半时崩溃留下半个 JSON；
/// 类 Unix 系统上会把权限收紧到仅当前用户可读写。
/// </para>
/// </remarks>
public sealed class FileMcpOAuthStore : IMcpOAuthStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;

    /// <summary>初始化文件存储。</summary>
    /// <param name="directory">自定义目录；为 <c>null</c> 时使用默认目录。</param>
    public FileMcpOAuthStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mono.LLM.Agent",
            "mcp-oauth");
    }

    /// <summary>实际使用的存储目录。</summary>
    public string Directory => _directory;

    /// <inheritdoc />
    public async ValueTask<McpOAuthSession?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<McpOAuthSession>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 文件损坏或不可读时按「没有缓存」处理，让流程退回重新授权，而不是直接失败。
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(
        string key,
        McpOAuthSession session,
        CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var path = GetPath(key);
        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, session, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        RestrictPermissions(tempPath);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        if (File.Exists(path))
            File.Delete(path);

        return ValueTask.CompletedTask;
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)));

        return Path.Combine(_directory, hash[..32].ToLowerInvariant() + ".json");
    }

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // 权限收紧失败不应阻断授权流程（例如文件系统不支持 Unix 权限位）。
        }
    }
}

/// <summary>
/// OAuth 授权流程失败时抛出的异常。
/// </summary>
/// <remarks>
/// <see cref="Error"/> 保留 RFC 6749 定义的错误码（<c>invalid_grant</c>、<c>access_denied</c> 等），
/// 调用方可以据此决定是「重新走授权」还是「提示用户拒绝授权」。<c>invalid_grant</c>
/// 与 <c>invalid_token</c> 意味着刷新令牌已不可用，必须重新授权。
/// </remarks>
public sealed class McpOAuthException : McpException
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="error">OAuth 错误码。</param>
    /// <param name="description">错误说明。</param>
    /// <param name="innerException">内部异常。</param>
    public McpOAuthException(string error, string? description = null, Exception? innerException = null)
        : base(
            McpErrorCodes.AuthorizationRequired,
            string.IsNullOrWhiteSpace(description) ? $"OAuth 授权失败：{error}" : $"OAuth 授权失败：{error} - {description}",
            null,
            innerException)
    {
        Error = error;
    }

    /// <summary>RFC 6749 错误码。</summary>
    public string Error { get; }

    /// <summary>判断该错误是否意味着刷新令牌已经失效，必须重新授权。</summary>
    public bool RequiresReauthorization =>
        Error is "invalid_grant" or "invalid_token" or "access_denied";
}

/// <summary>
/// 服务端返回 401 时解析出的 <c>WWW-Authenticate</c> 挑战信息。
/// </summary>
/// <remarks>
/// 规范要求 MCP 服务器在未授权时返回 <c>WWW-Authenticate: Bearer</c>，
/// 并可用 <c>resource_metadata</c> 直接给出受保护资源元数据地址，
/// 省掉客户端「先猜 <c>/.well-known</c> 路径」的探测。
/// </remarks>
public sealed class McpOAuthChallenge
{
    /// <summary>受保护资源元数据的地址（<c>resource_metadata</c> 参数）。</summary>
    public Uri? ResourceMetadata { get; init; }

    /// <summary>要求的授权范围（<c>scope</c> 参数）。</summary>
    public string? Scope { get; init; }

    /// <summary>错误码，例如 <c>invalid_token</c>。</summary>
    public string? Error { get; init; }

    /// <summary>错误说明。</summary>
    public string? ErrorDescription { get; init; }

    /// <summary>从响应头中解析 Bearer 挑战；不存在时返回 <c>null</c>。</summary>
    /// <param name="headers">响应的 <c>WWW-Authenticate</c> 集合。</param>
    public static McpOAuthChallenge? Parse(IEnumerable<AuthenticationHeaderValue>? headers)
    {
        if (headers is null)
            return null;

        foreach (var header in headers)
        {
            if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                continue;

            var parameters = ParseParameters(header.Parameter);

            return new McpOAuthChallenge
            {
                ResourceMetadata = parameters.TryGetValue("resource_metadata", out var metadata)
                                   && Uri.TryCreate(metadata, UriKind.Absolute, out var metadataUri)
                    ? metadataUri
                    : null,
                Scope = parameters.GetValueOrDefault("scope"),
                Error = parameters.GetValueOrDefault("error"),
                ErrorDescription = parameters.GetValueOrDefault("error_description")
            };
        }

        return null;
    }

    /// <summary>
    /// 解析 <c>key="value", key2=value2</c> 形式的认证参数。
    /// </summary>
    /// <remarks>
    /// 逗号可能出现在引号内的值里（例如 <c>error_description</c>），因此不能简单 split。
    /// </remarks>
    private static Dictionary<string, string> ParseParameters(string? parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parameter))
            return result;

        var index = 0;

        while (index < parameter.Length)
        {
            while (index < parameter.Length && (parameter[index] == ',' || char.IsWhiteSpace(parameter[index])))
                index++;

            var keyStart = index;
            while (index < parameter.Length && parameter[index] != '=' && parameter[index] != ',')
                index++;

            if (index >= parameter.Length || parameter[index] != '=')
                break;

            var key = parameter[keyStart..index].Trim();
            index++;

            string value;
            if (index < parameter.Length && parameter[index] == '"')
            {
                index++;
                var builder = new System.Text.StringBuilder();

                while (index < parameter.Length && parameter[index] != '"')
                {
                    if (parameter[index] == '\\' && index + 1 < parameter.Length)
                        index++;

                    builder.Append(parameter[index]);
                    index++;
                }

                if (index < parameter.Length)
                    index++;

                value = builder.ToString();
            }
            else
            {
                var valueStart = index;
                while (index < parameter.Length && parameter[index] != ',')
                    index++;

                value = parameter[valueStart..index].Trim();
            }

            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }
}

/// <summary>
/// 服务端拒绝访问（401）且无法通过刷新令牌恢复时抛出的异常。
/// </summary>
public sealed class McpUnauthorizedException : McpException
{
    /// <summary>初始化异常实例。</summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="endpoint">被访问的端点。</param>
    /// <param name="challenge">服务端给出的认证挑战，可能为 <c>null</c>。</param>
    public McpUnauthorizedException(string serverName, Uri endpoint, McpOAuthChallenge? challenge)
        : base(
            McpErrorCodes.AuthorizationRequired,
            $"MCP 服务器「{serverName}」拒绝了访问（{endpoint}），需要重新授权。")
    {
        Challenge = challenge;
    }

    /// <summary>服务端给出的认证挑战。</summary>
    public McpOAuthChallenge? Challenge { get; }
}
