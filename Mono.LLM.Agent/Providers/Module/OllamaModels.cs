// ============================================================================
// 文件：OllamaModels.cs
// 用途：Ollama 请求体与响应体的 DTO。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：LLMClientFactory（Ollama 提供商）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Providers;

/// <summary>表示发送给 Ollama /api/chat 端点的请求体结构。</summary>
internal class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OllamaMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions? Options { get; set; }
}

/// <summary>表示 Ollama API 中的单条消息结构。</summary>
internal class OllamaMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 推理型模型（如 deepseek-r1、qwq）在 content 之外返回的思考过程。
    /// </summary>
    /// <remarks>
    /// 为 <c>null</c> 时不会被序列化，因此请求侧不会把该字段发给服务端。
    /// </remarks>
    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }
}

/// <summary>表示 Ollama API 中模型推理的可选参数结构。</summary>
internal class OllamaOptions
{
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }
}

/// <summary>表示来自 Ollama /api/chat 端点的响应体结构。</summary>
internal class OllamaChatResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("message")]
    public OllamaMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }
}

/// <summary>表示发送给 Ollama /api/embeddings 端点的请求体结构。</summary>
internal class OllamaEmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;
}

/// <summary>表示来自 Ollama /api/embeddings 端点的响应体结构。</summary>
internal class OllamaEmbedResponse
{
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}
