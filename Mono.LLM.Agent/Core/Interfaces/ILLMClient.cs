// ============================================================================
// 文件：ILLMClient.cs
// 用途：定义与大语言模型交互的统一客户端接口。
// 实现方：LLMClient（OpenAI / DeepSeek / Azure / Ollama 共用）；测试夹具中的脚本化客户端
// 调用方：LLMClientFactory、ConversationManager、SkillExecutor、StreamingManager、EmbeddingManager、SessionManager
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Core;

/// <summary>
/// 定义与大语言模型交互的统一客户端接口。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ILLMClient"/> 是 Mono.LLM.Agent 的核心抽象接口，提供了与各种 LLM 服务提供商
/// 进行通信的统一 API。该接口支持以下功能：
/// </para>
/// <list type="bullet">
///   <item><description>非流式对话（<see cref="ChatAsync"/>）</description></item>
///   <item><description>流式对话（<see cref="StreamChatAsync"/>）</description></item>
///   <item><description>单条文本向量化（<see cref="GenerateEmbeddingAsync"/>）</description></item>
///   <item><description>批量文本向量化（<see cref="GenerateEmbeddingsAsync"/>）</description></item>
/// </list>
/// <para>
/// 所有实现类均需实现 <see cref="IDisposable"/> 接口以正确释放底层资源。
/// 建议通过 <see cref="LLMClientFactory"/> 创建具体实例。
/// </para>
/// </remarks>
public interface ILLMClient : IDisposable
{
    /// <summary>
    /// 以非流式方式发送聊天消息并获取完整响应。
    /// </summary>
    /// <param name="messages">要发送给模型的聊天消息集合。</param>
    /// <param name="options">
    /// 可选的聊天选项配置，如温度参数、最大输出 Token 数等。
    /// 若为 <c>null</c> 则使用配置中的默认值。
    /// </param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 一个表示异步操作的任务，任务值为包含完整响应信息的 <see cref="ChatResponse"/> 对象。
    /// </returns>
    Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 以流式方式发送聊天消息并逐步接收响应更新。
    /// </summary>
    /// <param name="messages">要发送给模型的聊天消息集合。</param>
    /// <param name="options">
    /// 可选的聊天选项配置，如温度参数、最大输出 Token 数等。
    /// 若为 <c>null</c> 则使用配置中的默认值。
    /// </param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 一个异步枚举序列，按顺序产生每个 <see cref="ChatResponseUpdate"/> 片段，
    /// 代表模型逐步生成的响应内容。
    /// </returns>
    IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为单条文本生成向量嵌入（Embedding）。
    /// </summary>
    /// <param name="text">需要进行向量化的源文本。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 一个表示异步操作的任务，任务值为文本对应的浮点数向量嵌入。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当当前提供商不支持向量嵌入功能时抛出。
    /// </exception>
    Task<Embedding<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为多条文本批量生成向量嵌入（Embedding）。
    /// </summary>
    /// <param name="texts">需要进行向量化的源文本集合。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 一个表示异步操作的任务，任务值为文本集合对应的浮点数向量嵌入列表。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当当前提供商不支持向量嵌入功能时抛出。
    /// </exception>
    Task<IList<Embedding<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前客户端使用的 LLM 配置信息。
    /// </summary>
    /// <value>
    /// 包含提供商类型、API 密钥、模型标识符、端点地址等配置项的
    /// <see cref="LLMConfiguration"/> 对象。
    /// </value>
    LLMConfiguration Configuration { get; }

    /// <summary>
    /// 获取底层框架的原生聊天客户端实例。
    /// </summary>
    /// <value>
    /// 来自 Microsoft.Extensions.AI 的 <see cref="IChatClient"/> 实现，
    /// 可用于直接访问底层 SDK 能力。
    /// </value>
    IChatClient InnerClient { get; }
}
