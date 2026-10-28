using Microsoft.Extensions.DependencyInjection;
using Mono.LLM.Agent.Configuration;
using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Mcp;
using Mono.LLM.Agent.Sessions;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Configuration;

/// <summary>
/// 覆盖统一配置模型：默认值、JSON 读写、分区校验、运行时映射与 DI 装配。
/// </summary>
public class AgentOptionsTests : Mono.LLM.Agent.Tests.Harness.SandboxedTest
{
    private string Root => Sandbox.Files.Root;

    // ── 默认值与校验 ──

    [Fact]
    public void Defaults_RequireModelId()
    {
        var result = new AgentOptions().ValidateAll();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Path == "llm.modelId");
    }

    [Fact]
    public void Ollama_DoesNotRequireApiKey()
    {
        var options = new AgentOptions().UseOllama("llama3");

        Assert.True(options.ValidateAll().IsValid, options.ValidateAll().ToString());
    }

    [Fact]
    public void DeepSeek_WithoutApiKey_IsError()
    {
        var options = new AgentOptions().UseDeepSeek(apiKey: string.Empty);

        var result = options.ValidateAll();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Path == "llm.apiKey");
    }

    [Fact]
    public void ApiKey_CanComeFromEnvironment()
    {
        var variable = $"MONO_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "sk-from-env");

        try
        {
            var options = new AgentOptions()
                .UseProvider(LLMProviderType.DeepSeek, "deepseek-chat")
                .UseApiKeyFromEnvironment(variable);

            Assert.Equal("sk-from-env", options.Llm.ResolveApiKey());
            Assert.True(options.ValidateAll().IsValid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void MissingEnvironmentVariable_ReportsActionableMessage()
    {
        var options = new AgentOptions()
            .UseProvider(LLMProviderType.DeepSeek, "deepseek-chat")
            .UseApiKeyFromEnvironment($"MONO_MISSING_{Guid.NewGuid():N}");

        var issue = Assert.Single(options.ValidateAll().Errors, i => i.Path == "llm.apiKey");

        Assert.Contains("环境变量", issue.Message);
    }

    [Fact]
    public void AzureRequiresEndpoint()
    {
        var options = new AgentOptions();
        options.Llm.Provider = LLMProviderType.AzureOpenAI;
        options.Llm.ModelId = "gpt-4o";
        options.Llm.ApiKey = "sk-test";

        Assert.Contains(options.ValidateAll().Errors, issue => issue.Path == "llm.endpoint");
    }

    [Fact]
    public void Validate_CollectsIssuesFromEverySection()
    {
        var options = new AgentOptions();
        options.Tools.Timeout = TimeSpan.Zero;
        options.Tools.MaxDegreeOfParallelism = 0;
        options.MultiAgent.MaxRounds = 0;
        options.Mcp.Servers.Add(new McpServerOptions { Name = "stdio-no-command" });
        options.Skills.StandardDirectories.Add(new StandardSkillDirectoryOptions { Path = string.Empty });

        var result = options.ValidateAll();
        var paths = result.Errors.Select(issue => issue.Path).ToList();

        Assert.Contains("llm.modelId", paths);
        Assert.Contains("tools.timeout", paths);
        Assert.Contains("tools.maxDegreeOfParallelism", paths);
        Assert.Contains("multiAgent.maxRounds", paths);
        Assert.Contains("mcp.servers[0].command", paths);
        Assert.Contains("skills.standardDirectories[0].path", paths);
    }

    [Fact]
    public void ValidateOrThrow_ListsEveryError()
    {
        var options = new AgentOptions();
        options.Tools.MaxDegreeOfParallelism = 0;

        var exception = Assert.Throws<InvalidOperationException>(options.ValidateOrThrow);

        Assert.Contains("llm.modelId", exception.Message);
        Assert.Contains("tools.maxDegreeOfParallelism", exception.Message);
    }

    [Fact]
    public void Mcp_Disabled_SkipsServerValidation()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Mcp.Enabled = false;
        options.Mcp.Servers.Add(new McpServerOptions { Name = "broken-without-command" });

        Assert.True(options.ValidateAll().IsValid);
        Assert.Empty(options.CreateMcpServerConfigs());
    }

    [Fact]
    public void Mcp_OAuthOnStdio_IsError()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Mcp.Servers.Add(new McpServerOptions
        {
            Name = "stdio",
            Command = "npx",
            OAuth = new McpOAuthSettings()
        });

        Assert.Contains(
            options.ValidateAll().Errors,
            issue => issue.Path == "mcp.servers[0].oauth");
    }

    [Fact]
    public void Mcp_DuplicateServerNames_IsError()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Mcp.Servers.Add(new McpServerOptions { Name = "dup", Command = "a" });
        options.Mcp.Servers.Add(new McpServerOptions { Name = "dup", Command = "b" });

        Assert.Contains(
            options.ValidateAll().Errors,
            issue => issue.Path == "mcp.servers[1].name");
    }

    [Fact]
    public void SkillToolNames_MustDiffer()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.Toolset.ReadResourceToolName = options.Skills.Toolset.LoadSkillToolName;

        Assert.Contains(
            options.ValidateAll().Errors,
            issue => issue.Path == "skills.toolset");
    }

    // ── JSON ──

    [Fact]
    public void Json_ReadsFriendlyTimeSpans()
    {
        var options = AgentOptions.FromJson("""
            {
              // 注释与尾逗号都允许
              "llm": { "provider": "deepSeek", "modelId": "deepseek-chat", "apiKey": "sk-test", "retryDelay": "500ms" },
              "tools": { "timeout": "5m" },
              "sessions": { "store": "sqlite", "filePath": "sessions.db" },
            }
            """);

        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Llm.RetryDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Tools.Timeout);
        Assert.Equal(LLMProviderType.DeepSeek, options.Llm.Provider);
        Assert.Equal(SessionStoreKind.Sqlite, options.Sessions.Store);
    }

    [Fact]
    public void Json_WritesCamelCaseAndStringEnums()
    {
        var options = new AgentOptions().UseDeepSeek("sk-test");
        options.Tools.Timeout = TimeSpan.FromMinutes(5);

        var json = options.ToJson();

        Assert.Contains("\"provider\": \"deepSeek\"", json);
        Assert.Contains("\"timeout\": \"00:05:00\"", json);
        Assert.DoesNotContain("\"apiKeyEnvironmentVariable\"", json);
    }

    [Fact]
    public void Json_RoundTripsFullConfiguration()
    {
        var options = new AgentOptions()
            .UseDeepSeek("sk-test", "deepseek-chat")
            .UseStandardSkills("skills")
            .UseJsonSkills("legacy", recursive: true)
            .UseSessionStore(SessionStoreKind.JsonFile, "sessions.json");

        options.Conversation.SystemPrompt = "你是一个助手。";
        options.Conversation.ContextTokenLimit = 64000;
        options.Tools.MaxDegreeOfParallelism = 4;
        options.Skills.Validation.StrictNames = true;
        options.Skills.Toolset.MaxResourceBytes = 1024;
        options.Mcp.Servers.Add(new McpServerOptions
        {
            Name = "filesystem",
            Command = "npx",
            Arguments = ["-y", "server-filesystem", "D:/work"],
            RequestTimeout = TimeSpan.FromSeconds(20),
            OAuth = new McpOAuthSettings { Scopes = ["mcp.read"], AuthorizationTimeout = TimeSpan.FromMinutes(2) }
        });
        options.MultiAgent.MaxRounds = 5;

        var restored = AgentOptions.FromJson(options.ToJson());

        Assert.Equal(options.Llm.Provider, restored.Llm.Provider);
        Assert.Equal("sk-test", restored.Llm.ApiKey);
        Assert.Equal(64000, restored.Conversation.ContextTokenLimit);
        Assert.Equal(4, restored.Tools.MaxDegreeOfParallelism);
        Assert.Equal(SessionStoreKind.JsonFile, restored.Sessions.Store);
        Assert.Equal("sessions.json", restored.Sessions.FilePath);
        Assert.Equal("skills", Assert.Single(restored.Skills.StandardDirectories).Path);
        Assert.Equal("legacy", Assert.Single(restored.Skills.Directories).Path);
        Assert.True(restored.Skills.Validation.StrictNames);
        Assert.Equal(5, restored.MultiAgent.MaxRounds);

        var server = Assert.Single(restored.Mcp.Servers);
        Assert.Equal("filesystem", server.Name);
        Assert.Equal(new[] { "-y", "server-filesystem", "D:/work" }, server.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(20), server.RequestTimeout);
        Assert.Equal("mcp.read", Assert.Single(server.OAuth!.Scopes));
    }

    [Fact]
    public void Json_IgnoresNullApiKey()
    {
        var options = new AgentOptions().UseOllama("llama3");

        Assert.DoesNotContain("\"apiKey\"", options.ToJson());
    }

    [Fact]
    public void Json_InvalidTimeSpan_Throws()
    {
        Assert.ThrowsAny<Exception>(() => AgentOptions.FromJson("""
            { "tools": { "timeout": "soon" } }
            """));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var path = Path.Combine(Root, "nested", "agent.json");
        var options = new AgentOptions().UseOllama("llama3");
        options.MultiAgent.DebateRounds = 7;

        options.Save(path);
        var restored = AgentOptions.Load(path);

        Assert.True(File.Exists(path));
        Assert.Equal(7, restored.MultiAgent.DebateRounds);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => AgentOptions.Load(Path.Combine(Root, "nope.json")));
    }

    // ── 映射 ──

    [Fact]
    public void ToConfiguration_MapsEveryField()
    {
        var options = new AgentOptions();
        options.Llm.Provider = LLMProviderType.AzureOpenAI;
        options.Llm.ModelId = "gpt-4o";
        options.Llm.ApiKey = "sk-test";
        options.Llm.Endpoint = "https://example.openai.azure.com";
        options.Llm.ApiVersion = "2025-01-01-preview";
        options.Llm.MaxRetryCount = 2;
        options.Llm.RetryDelay = TimeSpan.FromSeconds(3);
        options.Llm.MaxOutputTokens = 800;
        options.Llm.Temperature = 0.2f;
        options.Llm.AdditionalHeaders["x-tenant"] = "mono";

        var configuration = options.ToLLMConfiguration();

        Assert.Equal(LLMProviderType.AzureOpenAI, configuration.ProviderType);
        Assert.Equal("gpt-4o", configuration.ModelId);
        Assert.Equal("sk-test", configuration.ApiKey);
        Assert.Equal("https://example.openai.azure.com", configuration.Endpoint);
        Assert.Equal("2025-01-01-preview", configuration.Version);
        Assert.Equal(2, configuration.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(3), configuration.RetryDelay);
        Assert.Equal(800, configuration.MaxOutputTokens);
        Assert.Equal(0.2f, configuration.Temperature);
        Assert.Equal("mono", configuration.AdditionalHeaders!["x-tenant"]);
    }

    [Theory]
    [InlineData(LLMProviderType.DeepSeek, "https://api.deepseek.com/v1")]
    [InlineData(LLMProviderType.Ollama, "http://localhost:11434")]
    public void ToConfiguration_AppliesProviderEndpointDefaults(LLMProviderType provider, string expected)
    {
        var options = new AgentOptions();
        options.Llm.Provider = provider;
        options.Llm.ModelId = "m";
        options.Llm.ApiKey = "sk-test";

        Assert.Equal(expected, options.ToLLMConfiguration().Endpoint);
    }

    [Fact]
    public void ToExecutionOptions_MapsFields()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Tools.Timeout = TimeSpan.FromSeconds(5);
        options.Tools.MaxDegreeOfParallelism = 3;
        options.Tools.ValidateArguments = false;
        options.Tools.ApplyDefaultValues = false;

        var execution = options.ToToolExecutionOptions();

        Assert.Equal(TimeSpan.FromSeconds(5), execution.Timeout);
        Assert.Equal(3, execution.MaxDegreeOfParallelism);
        Assert.False(execution.ValidateArguments);
        Assert.False(execution.ApplyDefaultValues);
    }

    [Fact]
    public void SkillSections_MapToRuntimeOptions()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.Validation.StrictNames = true;
        options.Skills.Validation.MaxBodyLines = 100;
        options.Skills.Toolset.LoadSkillToolName = "open_skill";
        options.Skills.Toolset.MaxResourceBytes = 4096;
        options.Skills.Index.Heading = "## Skills";
        options.Skills.Index.GroupByCategory = false;

        var validation = options.Skills.ToValidationOptions();
        var toolset = options.Skills.ToToolsetOptions();
        var index = options.Skills.ToIndexOptions();

        Assert.True(validation.StrictNames);
        Assert.Equal(100, validation.MaxBodyLines);
        Assert.Equal("open_skill", toolset.LoadSkillToolName);
        Assert.Equal(4096, toolset.MaxResourceBytes);
        Assert.Equal("## Skills", index.Heading);
        Assert.False(index.GroupByCategory);
    }

    [Fact]
    public void CreateSkillSources_OrdersBuiltInThenDirectories()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.UseJsonSkills(Path.Combine(Root, "json"));
        options.UseStandardSkills(Path.Combine(Root, "standard"));

        var sources = options.CreateSkillSources();

        Assert.Collection(
            sources,
            source => Assert.IsType<InMemorySkillSource>(source),
            source => Assert.IsType<FileBasedSkillSource>(source),
            source => Assert.IsType<StandardSkillSource>(source));

        Assert.Equal("built-in", sources[0].SourceName);
    }

    [Fact]
    public void CreateSkillSources_CanSkipBuiltIn()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.LoadBuiltInCatalog = false;

        Assert.Empty(options.CreateSkillSources());
    }

    [Theory]
    [InlineData(SessionStoreKind.Memory, typeof(InMemorySessionStore))]
    [InlineData(SessionStoreKind.JsonFile, typeof(JsonFileSessionStore))]
    public void CreateSessionStore_ReturnsConfiguredKind(SessionStoreKind kind, Type expected)
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.UseSessionStore(kind, Path.Combine(Root, "sessions.json"));

        Assert.IsType(expected, options.CreateSessionStore());
    }

    [Fact]
    public void CreateSessionStore_SqliteRequiresPath()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.UseSessionStore(SessionStoreKind.Sqlite);

        Assert.Contains(options.ValidateAll().Errors, issue => issue.Path == "sessions.filePath");
        Assert.Throws<InvalidOperationException>(() => options.CreateSessionStore());
    }

    [Fact]
    public void CreateMcpServerConfigs_MapsAndSkipsDisabled()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Mcp.Servers.Add(new McpServerOptions
        {
            Name = "local",
            Command = "npx",
            Arguments = ["-y", "server"],
            EnvironmentVariables = { ["TOKEN"] = "x" },
            ToolCallTimeout = TimeSpan.FromMinutes(1)
        });
        options.Mcp.Servers.Add(new McpServerOptions
        {
            Name = "remote",
            Transport = McpTransportKind.StreamableHttp,
            Enabled = false,
            Endpoint = "https://mcp.example.com/rpc"
        });

        var server = Assert.Single(options.CreateMcpServerConfigs());

        Assert.Equal("local", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal("x", server.EnvironmentVariables["TOKEN"]);
        Assert.Equal(TimeSpan.FromMinutes(1), server.ToolCallTimeout);
    }

    [Fact]
    public void CreateMcpServerConfigs_MapsOAuth()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Mcp.Servers.Add(new McpServerOptions
        {
            Name = "remote",
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = "https://mcp.example.com/rpc",
            OAuth = new McpOAuthSettings
            {
                ClientId = "client",
                Scopes = ["mcp.read"],
                AuthorizationServer = "https://auth.example.com",
                OpenBrowser = false
            }
        });

        var server = Assert.Single(options.CreateMcpServerConfigs());

        Assert.Equal("https://mcp.example.com/rpc", server.Endpoint!.ToString());
        Assert.Equal("client", server.OAuth!.ClientId);
        Assert.False(server.OAuth.OpenBrowser);
        Assert.Equal("https://auth.example.com/", server.OAuth.AuthorizationServer!.ToString());
    }

    // ── DI 装配 ──

    [Fact]
    public void Di_RegistersCoreServices()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.LoadBuiltInCatalog = false;

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(provider.GetRequiredService<LLMClientFactory>());
        Assert.NotNull(provider.GetRequiredService<ILLMClient>());
        Assert.NotNull(provider.GetRequiredService<ToolManager>());
        Assert.NotNull(provider.GetRequiredService<ISkillRegistry>());
        Assert.NotNull(provider.GetRequiredService<SkillToolset>());
        Assert.NotNull(provider.GetRequiredService<ISessionManager>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ConversationManager>());
    }

    [Fact]
    public async Task Di_AppliesToolTimeout()
    {
        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.LoadBuiltInCatalog = false;
        options.Tools.Timeout = TimeSpan.FromMilliseconds(50);

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetRequiredService<ToolManager>();

        tools.RegisterTool("slow", "慢工具", [], async (_, token) =>
        {
            await Task.Delay(500, token);
            return new ToolResult { Name = "slow", Result = "done" };
        });

        var result = await tools.ExecuteToolAsync("slow", "{}");

        Assert.False(result.IsSuccess);
        Assert.Contains("超时", result.Error);
    }

    [Fact]
    public void Di_LoadsStandardSkillsFromConfiguredDirectory()
    {
        Sandbox.Files.Write("skills/demo/SKILL.md", """
            ---
            name: demo
            description: 演示技能。用于验证装配时。
            ---
            操作步骤：先做这个，再做那个。
            """);

        var options = new AgentOptions().UseOllama("llama3");
        options.Skills.LoadBuiltInCatalog = false;
        options.UseStandardSkills(Path.Combine(Root, "skills"));

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.NotNull(registry.Get("demo"));
    }

    [Fact]
    public void Di_RegistersSkillTools_WhenStandardSkillsDeclared()
    {
        WriteDemoSkill();

        var options = new AgentOptions().UseOllama("llama3");
        options.UseStandardSkills(Path.Combine(Root, "skills"));

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetRequiredService<ToolManager>();

        Assert.True(tools.IsRegistered(SkillToolset.DefaultLoadSkillToolName));
        Assert.True(tools.IsRegistered(SkillToolset.DefaultReadResourceToolName));
    }

    [Fact]
    public void Di_InjectsSkillIndexIntoSystemPrompt()
    {
        WriteDemoSkill();

        var options = new AgentOptions().UseOllama("llama3");
        options.Conversation.SystemPrompt = "你是一个助手。";
        options.UseStandardSkills(Path.Combine(Root, "skills"));

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var conversation = scope.ServiceProvider.GetRequiredService<ConversationManager>();

        var prompt = Assert.Single(conversation.History).Text;

        Assert.Contains("你是一个助手。", prompt);
        Assert.Contains("`demo`", prompt);
        Assert.Contains("演示技能", prompt);
        Assert.DoesNotContain("操作步骤", prompt);
    }

    [Fact]
    public void Di_SkipsSkillIndex_WhenDisabled()
    {
        WriteDemoSkill();

        var options = new AgentOptions().UseOllama("llama3");
        options.Conversation.SystemPrompt = "你是一个助手。";
        options.Skills.InjectIndexIntoSystemPrompt = false;
        options.UseStandardSkills(Path.Combine(Root, "skills"));

        var services = new ServiceCollection();
        services.AddMonoLLMAgent(options);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var conversation = scope.ServiceProvider.GetRequiredService<ConversationManager>();

        Assert.Equal("你是一个助手。", Assert.Single(conversation.History).Text);
    }

    private void WriteDemoSkill() => Sandbox.Files.Write("skills/demo/SKILL.md", """
        ---
        name: demo
        description: 演示技能。用于验证装配时。
        ---
        操作步骤：先做这个，再做那个。
        """);

    [Fact]
    public void Di_InvalidOptions_ThrowsBeforeRegistering()
    {
        var options = new AgentOptions();

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMonoLLMAgent(options));
    }

    [Fact]
    public void Di_FromFile_LoadsConfiguration()
    {
        var path = Path.Combine(Root, "agent.json");
        new AgentOptions().UseOllama("llama3").Save(path);

        var services = new ServiceCollection();
        services.AddMonoLLMAgentFromFile(path);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ILLMClient>());
    }

    [Fact]
    public void Di_ConfigureCallback_RunsBeforeValidation()
    {
        var services = new ServiceCollection();

        services.AddMonoLLMAgent(new AgentOptions(), options => options.UseOllama("llama3"));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ILLMClient>());
    }
}
