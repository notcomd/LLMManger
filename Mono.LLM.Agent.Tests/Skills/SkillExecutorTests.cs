using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tests.Fakes;
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;
using ChatResponse = Mono.LLM.Agent.Core.ChatResponse;

namespace Mono.LLM.Agent.Tests.Skills;

/// <summary>
/// 覆盖技能执行器：工具白名单、多轮工具循环、历史复用与轮次上限。
/// </summary>
public class SkillExecutorTests
{
    private static ChatResponse WithToolCall(string callId, string toolName) => new()
    {
        Content = string.Empty,
        RawMessage = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())
        ])
    };

    private static ChatResponse PlainText(string text) => new()
    {
        Content = text,
        RawMessage = new ChatMessage(ChatRole.Assistant, text)
    };

    private static ToolManager BuildTools(Action<string>? onInvoke = null)
    {
        var tools = new ToolManager();

        tools.RegisterTool(
            "get_time",
            "获取当前时间",
            Array.Empty<ToolParameterInfo>(),
            (_, _) =>
            {
                onInvoke?.Invoke("get_time");
                return Task.FromResult(new ToolResult { Name = "get_time", Result = "12:00" });
            });

        tools.RegisterTool(
            "delete_all",
            "危险操作",
            Array.Empty<ToolParameterInfo>(),
            (_, _) =>
            {
                onInvoke?.Invoke("delete_all");
                return Task.FromResult(new ToolResult { Name = "delete_all", Result = "deleted" });
            });

        return tools;
    }

    private static SkillExecutionContext BuildContext(
        FakeLlmClient fake,
        SkillDefinition skill,
        ToolManager tools)
    {
        var conversation = new ConversationManager(fake, skill.SystemPrompt);
        var effective = skill.AllowedTools.Count > 0
            ? tools.CreateScoped(skill.AllowedTools)
            : tools;

        return new SkillExecutionContext(skill, conversation, tools, effective);
    }

    // ── 白名单 ──

    [Fact]
    public void CreateScoped_EmptyAllowedTools_ExposesEverything()
    {
        var tools = BuildTools();
        var skill = new SkillDefinition { Name = "s", SystemPrompt = "p" };

        var context = BuildContext(new FakeLlmClient(), skill, tools);

        Assert.Same(context.Tools, context.EffectiveTools);
        Assert.Equal(2, context.EffectiveTools.Tools.Count);
    }

    [Fact]
    public void CreateScoped_WithAllowedTools_NarrowsToolSet()
    {
        var tools = BuildTools();
        var skill = new SkillDefinition
        {
            Name = "s",
            SystemPrompt = "p",
            AllowedTools = ["get_time"]
        };

        var context = BuildContext(new FakeLlmClient(), skill, tools);

        Assert.Single(context.EffectiveTools.Tools);
        Assert.True(context.EffectiveTools.IsRegistered("get_time"));
        Assert.False(context.EffectiveTools.IsRegistered("delete_all"));
        // 原工具集不受影响
        Assert.Equal(2, context.Tools.Tools.Count);
    }

    [Fact]
    public void CreateScoped_IsCaseInsensitive()
    {
        var tools = BuildTools();
        var skill = new SkillDefinition
        {
            Name = "s",
            SystemPrompt = "p",
            AllowedTools = ["GET_TIME"]
        };

        var context = BuildContext(new FakeLlmClient(), skill, tools);

        Assert.Single(context.EffectiveTools.Tools);
    }

    // ── 执行与工具循环 ──

    [Fact]
    public async Task ExecuteAsync_RunsToolThenReturnsFinalAnswer()
    {
        var invoked = new List<string>();
        var tools = BuildTools(invoked.Add);

        var fake = new FakeLlmClient
        {
            Responder = (_, call) => call == 1
                ? WithToolCall("c1", "get_time")
                : PlainText("12:00 了")
        };

        var skill = new SkillDefinition
        {
            Name = "s",
            SystemPrompt = "你是助手",
            AllowedTools = ["get_time"]
        };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        var response = await executor.ExecuteAsync(context, "几点了");

        Assert.Equal("12:00 了", response.Content);
        Assert.Equal(new[] { "get_time" }, invoked);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyExposesWhitelistedToolsToModel()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient
        {
            Responder = (_, _) => PlainText("ok")
        };

        var skill = new SkillDefinition
        {
            Name = "s",
            SystemPrompt = "p",
            AllowedTools = ["get_time"]
        };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        await executor.ExecuteAsync(context, "hi");

        var exposed = fake.ReceivedOptions[0]?.Tools;
        Assert.NotNull(exposed);
        Assert.Single(exposed!);
        Assert.Equal("get_time", ((AIFunction)exposed![0]).Name);
    }

    [Fact]
    public async Task ExecuteAsync_WritesToolMessagesIntoHistory()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient
        {
            Responder = (_, call) => call == 1
                ? WithToolCall("c1", "get_time")
                : PlainText("done")
        };

        var skill = new SkillDefinition { Name = "s", SystemPrompt = "sys", AllowedTools = ["get_time"] };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        await executor.ExecuteAsync(context, "hi");

        var toolMessages = context.Conversation.History.Where(m => m.Role == ChatRole.Tool).ToList();
        var toolMessage = Assert.Single(toolMessages);
        var result = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        Assert.Equal("c1", result.CallId);
    }

    [Fact]
    public async Task ExecuteAsync_ReusesHistoryAcrossCalls()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient
        {
            Responder = (_, call) => PlainText($"reply-{call}")
        };

        var skill = new SkillDefinition { Name = "s", SystemPrompt = "sys" };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        await executor.ExecuteAsync(context, "第一问");
        await executor.ExecuteAsync(context, "第二问");

        // 第二次请求应能看到第一轮的问答：system + user1 + assistant1 + user2
        Assert.Equal(4, fake.Calls[1].Count);
    }

    [Fact]
    public async Task ExecuteAsync_UnlimitedHistory_Accumulates()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient { Responder = (_, _) => PlainText("ok") };
        var skill = new SkillDefinition { Name = "s", SystemPrompt = "sys" };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        for (int i = 0; i < 3; i++)
            await executor.ExecuteAsync(context, $"问题 {i}");

        // system + 3 轮 × (user + assistant)
        Assert.Equal(7, context.Conversation.MessageCount);
    }

    // ── 轮次上限 ──

    [Fact]
    public async Task ExecuteAsync_RoundLimitReached_DisablesToolsOnFinalRequest()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient
        {
            // 永远要求调用工具，制造无法收敛的场景
            Responder = (_, call) => WithToolCall($"c{call}", "get_time")
        };

        var skill = new SkillDefinition { Name = "s", SystemPrompt = "p", AllowedTools = ["get_time"] };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        await executor.ExecuteAsync(context, "hi", maxToolRoundtrips: 2);

        Assert.Equal(3, fake.CallCount);
        Assert.NotNull(fake.ReceivedOptions[0]?.Tools);
        Assert.NotNull(fake.ReceivedOptions[1]?.Tools);
        Assert.Null(fake.ReceivedOptions[2]?.Tools);
    }

    [Fact]
    public async Task ExecuteAsync_NoToolCall_OnlyOneRequest()
    {
        var tools = BuildTools();
        var fake = new FakeLlmClient { Responder = (_, _) => PlainText("直接回答") };
        var skill = new SkillDefinition { Name = "s", SystemPrompt = "p" };

        using var context = BuildContext(fake, skill, tools);
        var executor = new SkillExecutor(new LLMClientFactory());

        await executor.ExecuteAsync(context, "hi");

        Assert.Equal(1, fake.CallCount);
    }

    // ── 构造校验 ──

    [Fact]
    public void CreateContext_RequiresConfiguration()
    {
        var executor = new SkillExecutor(new LLMClientFactory());
        var skill = new SkillDefinition { Name = "s", SystemPrompt = "p" };

        Assert.Throws<ArgumentNullException>(() => executor.CreateContext(skill, config: null));
    }

    [Fact]
    public void CreateContext_AppliesWhitelist()
    {
        var executor = new SkillExecutor(new LLMClientFactory());
        var tools = BuildTools();
        var skill = new SkillDefinition { Name = "s", SystemPrompt = "p", AllowedTools = ["get_time"] };

        using var context = executor.CreateContext(skill, LLMConfiguration.CreateOllama("llama3"), tools);

        Assert.Single(context.EffectiveTools.Tools);
    }

    [Fact]
    public async Task ExecuteAsync_NullContext_Throws()
    {
        var executor = new SkillExecutor(new LLMClientFactory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => executor.ExecuteAsync(null!, "hi"));
    }
}
