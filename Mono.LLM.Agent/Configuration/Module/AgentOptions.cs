// ============================================================================
// 文件：AgentOptions.cs
// 用途：Mono.LLM.Agent 的统一配置模型：把散落在各子系统的可配置项收拢到一棵树上。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：应用启动、DI 装配（AddMonoLLMAgent）
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Mcp;
using Mono.LLM.Agent.Sessions;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// Mono.LLM.Agent 的统一配置模型：把散落在各子系统的可配置项收拢到一棵树上。
/// </summary>
/// <remarks>
/// <para>
/// 之前要装配一个可用的 Agent，需要在 <see cref="LLMConfiguration"/>、
/// <see cref="ToolExecutionOptions"/>、<see cref="SkillLoadingConfig"/>、
/// <see cref="McpServerConfig"/> 等处分别构造对象；字段一多就容易漏配，
/// 也没法整体落盘或从配置中心读取。本类型把这些问题收敛成三件事：
/// </para>
/// <list type="bullet">
///   <item><b>声明</b>：一个对象描述全部可配置项，支持 JSON 读写与环境变量取密钥；</item>
///   <item><b>校验</b>：<see cref="Validate"/> 一次返回所有问题及其配置路径，而不是首个异常；</item>
///   <item><b>转换</b>：<c>To*</c> 方法产出各子系统真正接受的选项对象，避免两套配置各写一遍。</item>
/// </list>
/// <para>
/// 与 DI 结合时只需一次注册：<c>services.AddMonoLLMAgent(options)</c>。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var options = new AgentOptions()
///     .UseDeepSeek("sk-***", "deepseek-chat")
///     .UseStandardSkills("skills")
///     .ConfigureTools(tools => tools.Timeout = TimeSpan.FromSeconds(10));
///
/// options.ValidateOrThrow();
/// services.AddMonoLLMAgent(options);
/// </code>
/// </example>
public sealed class AgentOptions
{
    /// <summary>默认的配置节名称，便于 <c>IConfiguration.GetSection(AgentOptions.DefaultConfigurationSection)</c> 绑定。</summary>
    public const string DefaultConfigurationSection = "MonoLlmAgent";

    /// <summary>获取或设置模型接入配置。</summary>
    public LlmOptions Llm { get; set; } = new();

    /// <summary>获取或设置对话行为配置。</summary>
    public ConversationOptions Conversation { get; set; } = new();

    /// <summary>获取或设置会话持久化配置。</summary>
    public SessionOptions Sessions { get; set; } = new();

    /// <summary>获取或设置工具执行配置。</summary>
    public ToolOptions Tools { get; set; } = new();

    /// <summary>获取或设置技能系统配置。</summary>
    public SkillOptions Skills { get; set; } = new();

    /// <summary>获取或设置 MCP 配置。</summary>
    public McpOptions Mcp { get; set; } = new();

    /// <summary>获取或设置多智能体编排配置。</summary>
    public MultiAgentOptions MultiAgent { get; set; } = new();

    /// <summary>
    /// 获取 JSON 读写使用的序列化选项：驼峰命名、枚举按字符串、忽略 null、允许注释与尾逗号。
    /// </summary>
    /// <remarks>
    /// 时间间隔通过 <see cref="FlexibleTimeSpanConverter"/> 读取 <c>30s</c> / <c>5m</c> 这类写法。
    /// 与 <c>Microsoft.Extensions.Configuration</c> 绑定不冲突：那边的枚举与 TimeSpan 由 Binder 自行解析。
    /// </remarks>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// 从 JSON 文本反序列化配置。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>配置对象。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="json"/> 为 <c>null</c> 时抛出。</exception>
    /// <exception cref="JsonException">JSON 为空或结构不匹配时抛出。</exception>
    public static AgentOptions FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<AgentOptions>(json, SerializerOptions)
               ?? throw new JsonException("配置内容为空，无法解析出 AgentOptions。");
    }

    /// <summary>
    /// 从 JSON 文件加载配置。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>配置对象。</returns>
    /// <exception cref="ArgumentException">路径为空时抛出。</exception>
    /// <exception cref="FileNotFoundException">文件不存在时抛出。</exception>
    public static AgentOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"配置文件不存在：{fullPath}", fullPath);

        return FromJson(File.ReadAllText(fullPath));
    }

    /// <summary>
    /// 序列化为 JSON 文本。
    /// </summary>
    /// <param name="indented">是否缩进输出，默认 <c>true</c>。</param>
    /// <returns>JSON 文本。空的密钥字段不会被写出。</returns>
    public string ToJson(bool indented = true)
    {
        var settings = new JsonSerializerOptions(SerializerOptions) { WriteIndented = indented };
        return JsonSerializer.Serialize(this, settings);
    }

    /// <summary>
    /// 保存为 JSON 文件，必要时创建父目录。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <exception cref="ArgumentException">路径为空时抛出。</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, ToJson());
    }

    /// <summary>
    /// 校验全部分区。
    /// </summary>
    /// <returns>按分区顺序排列的问题列表；完全合规时为空。</returns>
    /// <remarks>
    /// 不抛异常，把所有问题一次收齐。每条问题带配置路径（如 <c>mcp.servers[0].command</c>），
    /// 可以直接定位到 JSON 中的字段。
    /// </remarks>
    public IReadOnlyList<AgentOptionsIssue> Validate()
    {
        var issues = new List<AgentOptionsIssue>();

        issues.AddRange(Llm.Validate("llm"));
        issues.AddRange(Conversation.Validate("conversation"));
        issues.AddRange(Sessions.Validate("sessions"));
        issues.AddRange(Tools.Validate("tools"));
        issues.AddRange(Skills.Validate("skills"));
        issues.AddRange(Mcp.Validate("mcp"));
        issues.AddRange(MultiAgent.Validate("multiAgent"));

        return issues;
    }

    /// <summary>
    /// 校验并返回包含错误的聚合结果。
    /// </summary>
    /// <returns>可直接判断 <c>IsValid</c> 或打印的校验结果。</returns>
    public AgentOptionsValidationResult ValidateAll() => new(Validate());

    /// <summary>
    /// 校验配置，存在错误时抛出异常。
    /// </summary>
    /// <exception cref="InvalidOperationException">存在错误级问题时抛出，消息中包含全部错误。</exception>
    public void ValidateOrThrow() => ValidateAll().ThrowIfInvalid();

    /// <summary>
    /// 转换为运行时的 <see cref="LLMConfiguration"/>。
    /// </summary>
    /// <returns>可直接传给客户端工厂的配置。</returns>
    public LLMConfiguration ToLLMConfiguration() => Llm.ToConfiguration();

    /// <summary>
    /// 转换为运行时的 <see cref="ToolExecutionOptions"/>。
    /// </summary>
    /// <returns>可直接传给工具管理器的选项。</returns>
    public ToolExecutionOptions ToToolExecutionOptions() => Tools.ToExecutionOptions();

    /// <summary>
    /// 按配置创建技能来源集合。
    /// </summary>
    /// <returns>内置（可选）→ JSON 目录 → 标准目录。</returns>
    public IReadOnlyList<ISkillSource> CreateSkillSources() => Skills.CreateSources();

    /// <summary>
    /// 按配置创建会话存储。
    /// </summary>
    /// <returns>新的 <see cref="ISessionStore"/> 实例。</returns>
    public ISessionStore CreateSessionStore() => Sessions.CreateStore();

    /// <summary>
    /// 按配置创建 MCP 服务器配置集合。
    /// </summary>
    /// <returns>已启用服务器的配置；MCP 关闭时为空集合。</returns>
    public IReadOnlyList<McpServerConfig> CreateMcpServerConfigs() => Mcp.ToServerConfigs();

    /// <summary>配置模型接入分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureLlm(Action<LlmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Llm);
        return this;
    }

    /// <summary>配置对话分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureConversation(Action<ConversationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Conversation);
        return this;
    }

    /// <summary>配置会话分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureSessions(Action<SessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Sessions);
        return this;
    }

    /// <summary>配置工具分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureTools(Action<ToolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Tools);
        return this;
    }

    /// <summary>配置技能分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureSkills(Action<SkillOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Skills);
        return this;
    }

    /// <summary>配置 MCP 分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureMcp(Action<McpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Mcp);
        return this;
    }

    /// <summary>配置多智能体分区。</summary>
    /// <param name="configure">配置回调。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions ConfigureMultiAgent(Action<MultiAgentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(MultiAgent);
        return this;
    }

    /// <summary>
    /// 选择服务提供商与模型。
    /// </summary>
    /// <param name="provider">提供商。</param>
    /// <param name="modelId">模型标识。</param>
    /// <param name="apiKey">可选的 API 密钥。</param>
    /// <param name="endpoint">可选的端点。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseProvider(
        LLMProviderType provider,
        string modelId,
        string? apiKey = null,
        string? endpoint = null)
    {
        Llm.Provider = provider;
        Llm.ModelId = modelId;

        if (apiKey is not null)
            Llm.ApiKey = apiKey;

        if (endpoint is not null)
            Llm.Endpoint = endpoint;

        return this;
    }

    /// <summary>使用 DeepSeek。</summary>
    /// <param name="apiKey">API 密钥。</param>
    /// <param name="modelId">模型标识，默认 <c>deepseek-chat</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseDeepSeek(string apiKey, string modelId = "deepseek-chat") =>
        UseProvider(LLMProviderType.DeepSeek, modelId, apiKey);

    /// <summary>使用 OpenAI。</summary>
    /// <param name="apiKey">API 密钥。</param>
    /// <param name="modelId">模型标识，默认 <c>gpt-4o</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseOpenAI(string apiKey, string modelId = "gpt-4o") =>
        UseProvider(LLMProviderType.OpenAI, modelId, apiKey);

    /// <summary>使用 Ollama 本地模型。</summary>
    /// <param name="modelId">模型标识，例如 <c>llama3</c>。</param>
    /// <param name="endpoint">服务地址，默认 <c>http://localhost:11434</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseOllama(string modelId, string? endpoint = null) =>
        UseProvider(LLMProviderType.Ollama, modelId, endpoint: endpoint);

    /// <summary>使用 Azure OpenAI。</summary>
    /// <param name="apiKey">API 密钥。</param>
    /// <param name="endpoint">资源端点。</param>
    /// <param name="modelId">部署名称。</param>
    /// <param name="apiVersion">API 版本，默认 <c>2025-01-01-preview</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseAzureOpenAI(
        string apiKey,
        string endpoint,
        string modelId,
        string apiVersion = "2025-01-01-preview")
    {
        UseProvider(LLMProviderType.AzureOpenAI, modelId, apiKey, endpoint);
        Llm.ApiVersion = apiVersion;
        return this;
    }

    /// <summary>
    /// 从环境变量读取 API 密钥，避免把密钥写进配置文件。
    /// </summary>
    /// <param name="variableName">环境变量名，例如 <c>DEEPSEEK_API_KEY</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseApiKeyFromEnvironment(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        Llm.ApiKeyEnvironmentVariable = variableName;
        Llm.ApiKey = null;
        return this;
    }

    /// <summary>追加一个标准技能目录（<c>SKILL.md</c>）。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="recursive">是否递归扫描，默认 <c>true</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseStandardSkills(string path, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Skills.StandardDirectories.Add(new StandardSkillDirectoryOptions
        {
            Path = path,
            Recursive = recursive
        });

        return this;
    }

    /// <summary>追加一个 JSON 技能目录（<c>*.skill.json</c>）。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="filePattern">文件模式，默认 <c>*.skill.json</c>。</param>
    /// <param name="recursive">是否递归扫描，默认 <c>false</c>。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseJsonSkills(string path, string filePattern = "*.skill.json", bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Skills.Directories.Add(new SkillDirectoryOptions
        {
            Path = path,
            FilePattern = filePattern,
            Recursive = recursive
        });

        return this;
    }

    /// <summary>选择会话存储方式。</summary>
    /// <param name="kind">存储类型。</param>
    /// <param name="filePath">落盘路径，内存存储可省略。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public AgentOptions UseSessionStore(SessionStoreKind kind, string? filePath = null)
    {
        Sessions.Store = kind;
        Sessions.FilePath = filePath;
        return this;
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
