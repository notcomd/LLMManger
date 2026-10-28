// ============================================================================
// 文件：JsonRpc.cs
// 用途：JSON-RPC 2.0 的请求、响应与错误模型。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：McpClient、StdioMcpTransport、StreamableHttpMcpTransport
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Mcp;

/// <summary>
/// JSON-RPC 2.0 错误对象。
/// </summary>
public sealed class JsonRpcError
{
    /// <summary>错误码，见 <see cref="McpErrorCodes"/>。</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>简短的错误描述，面向开发者而非终端用户。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>可选的附加错误数据，结构由错误码约定。</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>
/// 统一的 JSON-RPC 2.0 消息信封，同时表示请求、响应与通知。
/// </summary>
/// <remarks>
/// <para>
/// 使用单个类型承载三种消息形态，是为了让传输层只处理一种对象：
/// </para>
/// <list type="bullet">
///   <item><b>请求</b> — <see cref="Method"/> 非空且 <see cref="Id"/> 存在；</item>
///   <item><b>通知</b> — <see cref="Method"/> 非空但 <see cref="Id"/> 缺失（不期待响应）；</item>
///   <item><b>响应</b> — <see cref="Method"/> 为空，由 <see cref="Result"/> 或 <see cref="Error"/> 承载结果。</item>
/// </list>
/// <para>
/// 属性为 <c>null</c> 时不会写入 JSON，因此同一个实例序列化出的报文严格符合规范中
/// 「响应不得同时包含 result 与 error」的要求。
/// </para>
/// </remarks>
public sealed class JsonRpcMessage
{
    /// <summary>协议版本，固定为 <c>"2.0"</c>。</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpcVersion;

    /// <summary>消息标识，可为数字或字符串；通知没有该字段。</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    /// <summary>方法名；响应没有该字段。</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>方法参数，必须是对象或数组；无参数时可省略。</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    /// <summary>成功响应的结果。</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    /// <summary>失败响应的错误详情。</summary>
    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    /// <summary>是否携带有效标识（<c>null</c> 与缺失视为无标识）。</summary>
    [JsonIgnore]
    public bool HasId => Id is { } id && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>是否为请求（有方法名且有标识）。</summary>
    [JsonIgnore]
    public bool IsRequest => Method is not null && HasId;

    /// <summary>是否为通知（有方法名但无标识）。</summary>
    [JsonIgnore]
    public bool IsNotification => Method is not null && !HasId;

    /// <summary>是否为响应（无方法名）。</summary>
    [JsonIgnore]
    public bool IsResponse => Method is null;

    /// <summary>创建请求消息。</summary>
    /// <param name="id">请求标识，通常为自增整数。</param>
    /// <param name="method">方法名。</param>
    /// <param name="parameters">参数对象，可为 <c>null</c>。</param>
    public static JsonRpcMessage CreateRequest(int id, string method, JsonNode? parameters = null) => new()
    {
        Id = JsonSerializer.SerializeToElement(id),
        Method = method,
        Params = ToElement(parameters)
    };

    /// <summary>创建通知消息。</summary>
    /// <param name="method">方法名。</param>
    /// <param name="parameters">参数对象，可为 <c>null</c>。</param>
    public static JsonRpcMessage CreateNotification(string method, JsonNode? parameters = null) => new()
    {
        Method = method,
        Params = ToElement(parameters)
    };

    /// <summary>创建成功响应。</summary>
    /// <param name="id">对应请求的标识。</param>
    /// <param name="result">结果对象；为 <c>null</c> 时写为空对象。</param>
    public static JsonRpcMessage CreateResult(JsonElement id, JsonNode? result) => new()
    {
        Id = id,
        Result = result is null
            ? JsonSerializer.SerializeToElement(new JsonObject())
            : JsonSerializer.SerializeToElement(result)
    };

    /// <summary>创建失败响应。</summary>
    /// <param name="id">对应请求的标识。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误描述。</param>
    /// <param name="data">可选的附加数据。</param>
    public static JsonRpcMessage CreateError(JsonElement id, int code, string message, JsonElement? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message, Data = data }
    };

    /// <summary>把消息标识统一成可比较的字符串键（字符串取原值，数字取原始文本）。</summary>
    internal static string GetIdKey(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.GetRawText();

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);
}

/// <summary>
/// MCP 协议与传输层共用的序列化选项。
/// </summary>
/// <remarks>
/// 采用 <see cref="JsonSerializerDefaults.Web"/>（camelCase、大小写不敏感、数字可从字符串读取），
/// 与规范的字段命名一致；写入时忽略 <c>null</c>，确保请求报文不携带多余的 <c>result</c>/<c>error</c>。
/// </remarks>
internal static class McpJson
{
    /// <summary>共享的序列化配置，所有 MCP 报文收发都复用它。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
