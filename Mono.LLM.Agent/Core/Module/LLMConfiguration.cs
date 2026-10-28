// ============================================================================
// 文件：LLMConfiguration.cs
// 用途：封装连接到 LLM 服务所需的全部配置信息。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：LLMClientFactory、LLMClient、SessionManager、AgentOptions、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Core;

/// <summary>
/// 封装连接到 LLM 服务所需的全部配置信息。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LLMConfiguration"/> 是一个可复用的配置容器，包含了与指定 LLM 服务提供商
/// 建立连接所需的所有参数，包括身份认证信息、模型选择、行为调优参数以及弹性策略配置。
/// </para>
/// <para>
/// 推荐使用提供的静态工厂方法来创建针对特定提供商的预配置实例：
/// <list type="bullet">
///   <item><description><see cref="CreateOpenAI"/> — OpenAI 官方服务</description></item>
///   <item><description><see cref="CreateDeepSeek"/> — DeepSeek 服务</description></item>
///   <item><description><see cref="CreateAzureOpenAI"/> — Azure OpenAI 服务</description></item>
///   <item><description><see cref="CreateOllama"/> — Ollama 本地服务</description></item>
/// </list>
/// </para>
/// <para>
/// 工厂方法仅设置各提供商的核心必需字段，其他可选属性（如 <see cref="MaxRetryCount"/>、
/// <see cref="Temperature"/> 等）可在创建后根据需要进行调整。
/// </para>
/// </remarks>
public class LLMConfiguration
{
    /// <summary>
    /// 获取或设置 LLM 服务提供商的类型。
    /// </summary>
    /// <value>用于标识目标提供商的 <see cref="LLMProviderType"/> 枚举值。</value>
    public LLMProviderType ProviderType { get; set; }

    /// <summary>
    /// 获取或设置用于身份认证的 API 密钥。
    /// </summary>
    /// <value>API 密钥字符串，对于无需认证的本地服务（如 Ollama）可为空。</value>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置要使用的模型标识符。
    /// </summary>
    /// <value>模型名称或 ID，例如 "gpt-4o"、"deepseek-chat"、"llama3" 等。</value>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 LLM 服务的 API 端点地址。
    /// </summary>
    /// <value>服务的 HTTP(S) 端点 URL。OpenAI 使用默认端点时可为空。</value>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Azure OpenAI 的 API 版本号。
    /// </summary>
    /// <value>API 版本日期字符串，例如 "2025-01-01-preview"。仅对 Azure OpenAI 有效。</value>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置请求失败时的最大重试次数。
    /// </summary>
    /// <value>最大重试次数，默认值为 3。</value>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// 获取或设置重试操作之间的等待时间间隔。
    /// </summary>
    /// <value>重试延迟时间跨度，默认值为 1 秒。</value>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 获取或设置单次响应允许的最大输出 Token 数量。
    /// </summary>
    /// <value>最大输出 Token 上限，默认值为 4096。</value>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// 获取或设置控制生成随机性的温度参数。
    /// </summary>
    /// <value>
    /// 温度值，范围通常为 0.0～2.0。值越低输出越确定，值越高输出越随机。
    /// 默认值为 0.7。
    /// </value>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// 获取或设置随请求一起发送的自定义 HTTP 头部。
    /// </summary>
    /// <value>
    /// 键值对形式的头部字典；若不需要自定义头则为 <c>null</c>。
    /// 这些头部将被合并到每次 API 请求中。
    /// </value>
    public Dictionary<string, string>? AdditionalHeaders { get; set; }

    /// <summary>
    /// 创建适用于 OpenAI 官方服务的配置实例。
    /// </summary>
    /// <param name="apiKey">OpenAI 平台的 API 密钥。</param>
    /// <param name="modelId">
    /// 要使用的模型标识符，默认为 "gpt-4o"。
    /// </param>
    /// <returns>
    /// 预配置了 OpenAI 提供商类型和基本参数的 <see cref="LLMConfiguration"/> 实例。
    /// </returns>
    public static LLMConfiguration CreateOpenAI(string apiKey, string modelId = "gpt-4o")
    {
        return new LLMConfiguration
        {
            ProviderType = LLMProviderType.OpenAI,
            ApiKey = apiKey,
            ModelId = modelId
        };
    }

    /// <summary>
    /// 创建适用于 DeepSeek 服务的配置实例。
    /// </summary>
    /// <param name="apiKey">DeepSeek 平台的 API 密钥。</param>
    /// <param name="modelId">
    /// 要使用的模型标识符，默认为 "deepseek-chat"。
    /// </param>
    /// <returns>
    /// 预配置了 DeepSeek 提供商类型、API 端点和基本参数的
    /// <see cref="LLMConfiguration"/> 实例。
    /// </returns>
    public static LLMConfiguration CreateDeepSeek(string apiKey, string modelId = "deepseek-chat")
    {
        return new LLMConfiguration
        {
            ProviderType = LLMProviderType.DeepSeek,
            ApiKey = apiKey,
            ModelId = modelId,
            Endpoint = "https://api.deepseek.com/v1"
        };
    }

    /// <summary>
    /// 创建适用于 Azure OpenAI 服务的配置实例。
    /// </summary>
    /// <param name="apiKey">Azure OpenAI 资源的访问密钥。</param>
    /// <param name="endpoint">Azure OpenAI 资源的端点 URL。</param>
    /// <param name="modelId">Azure OpenAI 部署的模型名称。</param>
    /// <param name="version">
    /// API 版本号，默认为 "2025-01-01-preview"。
    /// </param>
    /// <returns>
    /// 预配置了 Azure OpenAI 提供商类型和全部必需参数的
    /// <see cref="LLMConfiguration"/> 实例。
    /// </returns>
    public static LLMConfiguration CreateAzureOpenAI(string apiKey, string endpoint, string modelId, string version = "2025-01-01-preview")
    {
        return new LLMConfiguration
        {
            ProviderType = LLMProviderType.AzureOpenAI,
            ApiKey = apiKey,
            Endpoint = endpoint,
            ModelId = modelId,
            Version = version
        };
    }

    /// <summary>
    /// 创建适用于 Ollama 本地/自托管服务的配置实例。
    /// </summary>
    /// <param name="modelId">Ollama 上运行的模型名称，例如 "llama3"。</param>
    /// <param name="endpoint">
    /// Ollama 服务的监听地址，默认为 "http://localhost:11434"。
    /// </param>
    /// <returns>
    /// 预配置了 Ollama 提供商类型、端点地址和模型名称的
    /// <see cref="LLMConfiguration"/> 实例。
    /// </returns>
    public static LLMConfiguration CreateOllama(string modelId, string endpoint = "http://localhost:11434")
    {
        return new LLMConfiguration
        {
            ProviderType = LLMProviderType.Ollama,
            ModelId = modelId,
            Endpoint = endpoint
        };
    }
}
