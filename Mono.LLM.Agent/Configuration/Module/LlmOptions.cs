// ============================================================================
// 文件：LlmOptions.cs
// 用途：模型接入相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json.Serialization;
using Mono.LLM.Agent.Core;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 模型接入相关的配置。
/// </summary>
/// <remarks>
/// <para>
/// 对应 <see cref="LLMConfiguration"/>，但把所有「不填也能跑」的项给了合理默认值，
/// 并把重试参数从提供商字段里划分出来单独说明——它们是客户端行为，与选哪家服务无关。
/// </para>
/// <para>
/// 密钥可以不写进配置文件：设置 <see cref="ApiKeyEnvironmentVariable"/> 后，
/// 取值时优先读环境变量，其次是 <see cref="ApiKey"/>。前者适合提交到仓库的配置，后者适合本地覆盖。
/// </para>
/// </remarks>
public sealed class LlmOptions
{
    /// <summary>获取或设置服务提供商，默认 <see cref="LLMProviderType.OpenAI"/>。</summary>
    public LLMProviderType Provider { get; set; } = LLMProviderType.OpenAI;

    /// <summary>获取或设置模型标识，例如 <c>gpt-4o</c>、<c>deepseek-chat</c>、<c>llama3</c>。必填。</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>获取或设置 API 密钥。建议改用 <see cref="ApiKeyEnvironmentVariable"/>，避免密钥进入配置文件。</summary>
    public string? ApiKey { get; set; }

    /// <summary>获取或设置保存 API 密钥的环境变量名。设置且变量存在时优先于 <see cref="ApiKey"/>。</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>
    /// 获取或设置服务端点。为 <c>null</c> 时按提供商取默认值：
    /// DeepSeek 用官方地址，Ollama 用 <c>http://localhost:11434</c>，其余留空交给 SDK。
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>获取或设置 Azure OpenAI 的 API 版本，例如 <c>2025-01-01-preview</c>。仅 Azure 需要。</summary>
    public string? ApiVersion { get; set; }

    /// <summary>获取或设置失败重试次数，默认 3。总尝试次数为该值加 1。</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>获取或设置重试的基础延迟，默认 1 秒。实际延迟按线性退避随尝试次数增长。</summary>
    [JsonConverter(typeof(FlexibleTimeSpanConverter))]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>获取或设置单次回复的最大输出 token 数，默认 4096。</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>获取或设置采样温度，默认 0.7。取值范围通常为 0～2。</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>获取或设置随每次请求发送的自定义 HTTP 头。默认空。</summary>
    public Dictionary<string, string> AdditionalHeaders { get; set; } = new();

    /// <summary>
    /// 解析实际使用的 API 密钥：环境变量优先，其次配置值。
    /// </summary>
    /// <returns>密钥文本；两处都没有时返回 <c>null</c>。</returns>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment;
        }

        return string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
    }

    /// <summary>
    /// 解析实际使用的端点：显式配置优先，否则按提供商取默认值。
    /// </summary>
    /// <returns>端点字符串；该提供商没有默认值时为 <c>null</c>。</returns>
    public string? ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(Endpoint))
            return Endpoint;

        return Provider switch
        {
            LLMProviderType.DeepSeek => "https://api.deepseek.com/v1",
            LLMProviderType.Ollama => "http://localhost:11434",
            _ => null
        };
    }

    /// <summary>
    /// 转换为运行时的 <see cref="LLMConfiguration"/>。
    /// </summary>
    /// <returns>可直接传给 <c>LLMClientFactory.Create</c> 的配置对象。</returns>
    /// <exception cref="InvalidOperationException">当端点无法解析为绝对 URI 时抛出。</exception>
    public LLMConfiguration ToConfiguration()
    {
        var endpoint = ResolveEndpoint();

        if (endpoint is not null
            && !Uri.TryCreate(endpoint, UriKind.Absolute, out _)
            && Provider != LLMProviderType.OpenAI)
        {
            throw new InvalidOperationException($"端点 '{endpoint}' 不是合法的绝对 URI。");
        }

        return new LLMConfiguration
        {
            ProviderType = Provider,
            ApiKey = ResolveApiKey() ?? string.Empty,
            ModelId = ModelId,
            Endpoint = endpoint ?? string.Empty,
            Version = ApiVersion ?? string.Empty,
            MaxRetryCount = MaxRetryCount,
            RetryDelay = RetryDelay,
            MaxOutputTokens = MaxOutputTokens,
            Temperature = Temperature,
            AdditionalHeaders = AdditionalHeaders.Count == 0
                ? null
                : new Dictionary<string, string>(AdditionalHeaders, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// 校验本分区。
    /// </summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("llm");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        if (!Enum.IsDefined(Provider))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.provider", $"未知的提供商值 {(int)Provider}。"));

        if (string.IsNullOrWhiteSpace(ModelId))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.modelId", "必须指定模型标识，例如 gpt-4o 或 deepseek-chat。"));

        if (Provider is LLMProviderType.MiniMax or LLMProviderType.Mimo)
        {
            issues.Add(AgentOptionsIssue.Warning(
                $"{prefix}.provider",
                $"内置客户端尚未覆盖 {Provider}，需要自行提供 LLMClientFactory 派生实现。"));
        }

        if (RequiresApiKey() && string.IsNullOrWhiteSpace(ResolveApiKey()))
        {
            var hint = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
                ? "请设置 apiKey，或改用 apiKeyEnvironmentVariable 指向环境变量。"
                : $"环境变量 {ApiKeyEnvironmentVariable} 未设置，且 apiKey 为空。";

            issues.Add(AgentOptionsIssue.Error($"{prefix}.apiKey", hint));
        }

        if (Provider == LLMProviderType.AzureOpenAI && string.IsNullOrWhiteSpace(Endpoint))
            issues.Add(AgentOptionsIssue.Error($"{prefix}.endpoint", "Azure OpenAI 必须显式指定端点。"));

        var endpoint = ResolveEndpoint();

        if (!string.IsNullOrWhiteSpace(endpoint)
            && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.endpoint", $"'{endpoint}' 不是合法的绝对 URI。"));
        }

        if (MaxRetryCount < 0)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxRetryCount", "重试次数不能为负。"));

        if (RetryDelay < TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.retryDelay", "重试延迟不能为负。"));

        if (MaxOutputTokens <= 0)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxOutputTokens", "最大输出 token 必须为正数。"));

        if (Temperature is < 0f or > 2f)
            issues.Add(AgentOptionsIssue.Warning($"{prefix}.temperature", $"温度 {Temperature} 超出常见范围 0～2，输出可能不可控。"));

        foreach (var key in AdditionalHeaders.Keys.Where(string.IsNullOrWhiteSpace))
            issues.Add(AgentOptionsIssue.Warning($"{prefix}.additionalHeaders", "存在空的请求头名称。"));

        return issues;
    }

    private bool RequiresApiKey() => Provider != LLMProviderType.Ollama;
}
