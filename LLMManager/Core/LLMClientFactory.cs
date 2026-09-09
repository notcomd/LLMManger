using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using LLMManager.Resilience;

namespace LLMManager.Core;

/// <summary>
/// 根据配置信息创建对应 LLM 服务提供商的 <see cref="ILLMClient"/> 实例的工厂类。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LLMClientFactory"/> 封装了不同 LLM 提供商的具体实例化逻辑，
/// 调用者只需提供一个 <see cref="LLMConfiguration"/> 对象即可获得完全配置好的客户端实例。
/// 工厂会自动完成以下工作：
/// </para>
/// <list type="bullet">
///   <item><description>根据 <see cref="LLMConfiguration.ProviderType"/> 选择正确的 SDK 客户端实现</description></item>
///   <item><description>注入 API 密钥、端点地址等认证与连接参数</description></item>
///   <item><description>基于配置中的重试参数构建 <see cref="IRetryPolicy"/></description></item>
///   <item><description>组装并返回带有完整弹性和日志能力的 <see cref="LLMClient"/> 实例</description></item>
/// </list>
/// <para>
/// 支持的提供商包括：OpenAI、DeepSeek、Azure OpenAI 和 Ollama。
/// 若指定的提供商类型不在支持范围内，将在运行时抛出异常。
/// </para>
/// </remarks>
public class LLMClientFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="LLMClientFactory"/> 类的新实例。
    /// </summary>
    /// <param name="loggerFactory">
    /// 可选的日志工厂。传入后，工厂创建的所有 <see cref="LLMClient"/> 实例
    /// 将自动获得日志记录能力。若为 <c>null</c> 则不启用日志。
    /// </param>
    public LLMClientFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// 根据指定的配置创建一个新的 <see cref="ILLMClient"/> 实例。
    /// </summary>
    /// <param name="configuration">
    /// 描述目标 LLM 服务的完整配置信息，包括提供商类型、认证凭据、模型选择及行为参数等。
    /// </param>
    /// <returns>
    /// 一个已完全初始化且可用于立即发起请求的 <see cref="ILLMClient"/> 实例。
    /// </returns>
    /// <exception cref="ArgumentException">
    /// 当 <paramref name="configuration"/> 中的 <see cref="LLMConfiguration.ProviderType"/>
    /// 不受支持时抛出。
    /// </exception>
    public ILLMClient Create(LLMConfiguration configuration)
    {
        var logger = _loggerFactory?.CreateLogger<LLMClient>();

        IChatClient chatClient = configuration.ProviderType switch
        {
            LLMProviderType.OpenAI => CreateOpenAIClient(configuration),
            LLMProviderType.DeepSeek => CreateDeepSeekClient(configuration),
            LLMProviderType.AzureOpenAI => CreateAzureOpenAIClient(configuration),
            LLMProviderType.Ollama => CreateOllamaClient(configuration),
            _ => throw new ArgumentException($"Unsupported provider type: {configuration.ProviderType}")
        };

        var retryPolicy = new DefaultRetryPolicy(
            configuration.MaxRetryCount,
            configuration.RetryDelay,
            logger);

        return new LLMClient(chatClient, configuration, retryPolicy, logger);
    }

    private IChatClient CreateOpenAIClient(LLMConfiguration config)
    {
        var client = new OpenAI.Chat.ChatClient(
            config.ModelId,
            new System.ClientModel.ApiKeyCredential(config.ApiKey));
        return client.AsIChatClient();
    }

    private IChatClient CreateDeepSeekClient(LLMConfiguration config)
    {
        var clientOptions = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri(config.Endpoint)
        };
        var client = new OpenAI.Chat.ChatClient(
            config.ModelId,
            new System.ClientModel.ApiKeyCredential(config.ApiKey),
            clientOptions);
        return client.AsIChatClient();
    }

    private IChatClient CreateAzureOpenAIClient(LLMConfiguration config)
    {
        var client = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(config.Endpoint),
            new Azure.AzureKeyCredential(config.ApiKey));
        return client.GetChatClient(config.ModelId).AsIChatClient();
    }

    private IChatClient CreateOllamaClient(LLMConfiguration config)
    {
        return new Providers.OllamaChatClient(
            new Uri(config.Endpoint),
            config.ModelId);
    }
}
