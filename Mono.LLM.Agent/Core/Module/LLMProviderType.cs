// ============================================================================
// 文件：LLMProviderType.cs
// 用途：表示支持的 LLM 服务提供商类型。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：LLMConfiguration、LLMClientFactory、AgentOptions
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Core;

/// <summary>
/// 表示支持的 LLM 服务提供商类型。
/// </summary>
/// <remarks>
/// 此枚举定义了 Mono.LLM.Agent 所支持的所有大语言模型服务提供商。
/// 每个值对应一种特定的 LLM 提供商，用于在创建客户端时选择正确的实现。
/// </remarks>
public enum LLMProviderType
{
    /// <summary>OpenAI 官方服务（api.openai.com）。</summary>
    OpenAI,

    /// <summary>DeepSeek 服务（api.deepseek.com）。</summary>
    DeepSeek,

    /// <summary>Azure OpenAI 服务。</summary>
    AzureOpenAI,

    /// <summary>Ollama 本地/自托管模型服务。</summary>
    Ollama,

    /// <summary>MiniMax 服务。</summary>
    MiniMax,

    /// <summary>Mimo 服务。</summary>
    Mimo,
}
