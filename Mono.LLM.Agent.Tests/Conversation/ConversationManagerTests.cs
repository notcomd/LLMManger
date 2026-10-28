using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tests.Conversation;

/// <summary>
/// 覆盖对话历史的增删、裁剪与工具消息结构。
/// </summary>
public class ConversationManagerTests
{
    [Fact]
    public void SetSystemPrompt_ReplacesExistingSystemMessage()
    {
        using var conversation = new ConversationManager(new FakeLlmClient(), "旧提示");

        conversation.SetSystemPrompt("新提示");

        var systemMessages = conversation.History.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Single(systemMessages);
        Assert.Equal("新提示", systemMessages[0].Text);
        Assert.Equal(ChatRole.System, conversation.History[0].Role);
    }

    [Fact]
    public void ClearHistory_KeepSystemPrompt_RetainsOnlySystemMessage()
    {
        using var conversation = new ConversationManager(new FakeLlmClient(), "系统提示");
        conversation.AddUserMessage("q");
        conversation.AddAssistantMessage("a");

        conversation.ClearHistory(keepSystemPrompt: true);

        Assert.Single(conversation.History);
        Assert.Equal(ChatRole.System, conversation.History[0].Role);
    }

    [Fact]
    public void ClearHistory_WithoutSystemPrompt_ClearsEverything()
    {
        using var conversation = new ConversationManager(new FakeLlmClient(), "系统提示");
        conversation.AddUserMessage("q");

        conversation.ClearHistory(keepSystemPrompt: false);

        Assert.Empty(conversation.History);
    }

    [Fact]
    public void RemoveLastExchange_RemovesUserAndAssistantPair()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());
        conversation.AddUserMessage("q1");
        conversation.AddAssistantMessage("a1");
        conversation.AddUserMessage("q2");
        conversation.AddAssistantMessage("a2");

        conversation.RemoveLastExchange();

        Assert.Equal(2, conversation.MessageCount);
        Assert.Equal("q1", conversation.History[0].Text);
        Assert.Equal("a1", conversation.History[1].Text);
    }

    [Fact]
    public void RemoveLastExchange_WithoutAssistant_DoesNothing()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());
        conversation.AddUserMessage("q1");

        conversation.RemoveLastExchange();

        Assert.Single(conversation.History);
    }

    [Fact]
    public void RemoveLastMessage_DoesNotRemoveSystemPrompt()
    {
        using var conversation = new ConversationManager(new FakeLlmClient(), "系统提示");

        conversation.RemoveLastMessage();

        Assert.Single(conversation.History);
    }

    [Fact]
    public async Task SendAsync_TrimsHistoryWhenChineseTextExceedsLimit()
    {
        var fake = new FakeLlmClient();
        using var conversation = new ConversationManager(fake)
        {
            ContextTokenLimit = 200
        };

        for (int i = 0; i < 20; i++)
            await conversation.SendAsync($"第 {i} 轮：这是一段用于触发上下文裁剪的中文内容，长度足够。");

        // 未裁剪时应为 40 条（20 轮问答），裁剪后必须显著减少。
        Assert.True(
            conversation.MessageCount < 40,
            $"预期历史被裁剪，实际 {conversation.MessageCount} 条");
    }

    [Fact]
    public async Task SendAsync_AppendsUserAndAssistantMessages()
    {
        var fake = new FakeLlmClient
        {
            Responder = (_, _) => new Mono.LLM.Agent.Core.ChatResponse { Content = "回答" }
        };

        using var conversation = new ConversationManager(fake);
        var response = await conversation.SendAsync("问题");

        Assert.Equal("回答", response.Content);
        Assert.Equal(2, conversation.MessageCount);
        Assert.Equal(ChatRole.User, conversation.History[0].Role);
        Assert.Equal(ChatRole.Assistant, conversation.History[1].Role);
    }

    [Fact]
    public void AddToolMessage_UsesFunctionResultContent()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());

        conversation.AddToolMessage("call-1", "结果");

        var message = conversation.History[^1];
        Assert.Equal(ChatRole.Tool, message.Role);

        var result = message.Contents.OfType<FunctionResultContent>().SingleOrDefault();
        Assert.NotNull(result);
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public void AddToolMessage_EmptyCallId_Throws()
    {
        using var conversation = new ConversationManager(new FakeLlmClient());

        Assert.Throws<ArgumentException>(() => conversation.AddToolMessage("  ", "x"));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var conversation = new ConversationManager(new FakeLlmClient());

        conversation.Dispose();
        conversation.Dispose();
    }
}
