using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Sessions;

namespace Mono.LLM.Agent.Tests.Sessions;

/// <summary>
/// 覆盖会话生命周期与接口约定的一致性。
/// </summary>
/// <remarks>
/// 使用 Ollama 配置创建客户端，构造函数不会发起网络请求，因此无需真实服务即可测试。
/// </remarks>
public class SessionManagerTests
{
    private static SessionManager CreateManager() =>
        new(new LLMClientFactory(), LLMConfiguration.CreateOllama("llama3"));

    [Fact]
    public async Task CreateSession_AssignsIdAndBecomesActive()
    {
        var manager = CreateManager();

        var session = await manager.CreateSessionAsync("会话一");

        Assert.False(string.IsNullOrWhiteSpace(session.Id));
        Assert.Equal("会话一", session.Name);
        Assert.Equal(session.Id, manager.GetActiveSessionId());
        Assert.Equal(1, manager.SessionCount);
    }

    [Fact]
    public async Task CreateSession_EmptyName_Throws()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateSessionAsync("   "));
    }

    [Fact]
    public async Task DeleteSession_RemovesAndReassignsActive()
    {
        var manager = CreateManager();
        var first = await manager.CreateSessionAsync("a");
        var second = await manager.CreateSessionAsync("b");

        manager.SetActiveSession(second.Id);
        await manager.DeleteSessionAsync(second.Id);

        Assert.Equal(1, manager.SessionCount);
        Assert.Equal(first.Id, manager.GetActiveSessionId());
    }

    [Fact]
    public async Task DeleteSession_UnknownId_Throws()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.DeleteSessionAsync("missing"));
    }

    [Fact]
    public async Task SetActiveSession_UnknownId_ReturnsSilently()
    {
        var manager = CreateManager();
        var session = await manager.CreateSessionAsync("a");

        // 接口文档约定：会话不存在时静默返回，不应抛异常。
        var exception = Record.Exception(() => manager.SetActiveSession("missing"));

        Assert.Null(exception);
        Assert.Equal(session.Id, manager.GetActiveSessionId());
    }

    [Fact]
    public async Task SetSessionTags_ThenFilterByTag_FindsSession()
    {
        var manager = CreateManager();
        var session = await manager.CreateSessionAsync("a");

        await manager.SetSessionTagsAsync(session.Id, new[] { "技术", "复盘" });

        Assert.Single(manager.FilterByTag("技术"));
        Assert.Empty(manager.FilterByTag("财务"));
    }

    [Fact]
    public async Task SearchSessions_MatchesNameAndTag()
    {
        var manager = CreateManager();
        var a = await manager.CreateSessionAsync("季度复盘");
        await manager.CreateSessionAsync("无关会话");
        await manager.SetSessionTagsAsync(a.Id, new[] { "planning" });

        Assert.Single(manager.SearchSessions("复盘"));
        Assert.Single(manager.SearchSessions("planning"));
    }

    [Fact]
    public async Task GetAllSessions_OrderedByLastActiveDescending()
    {
        var manager = CreateManager();
        var a = await manager.CreateSessionAsync("a");
        await Task.Delay(10);
        var b = await manager.CreateSessionAsync("b");

        var all = manager.GetAllSessions();

        Assert.Equal(b.Id, all[0].Id);
        Assert.Equal(a.Id, all[1].Id);
    }

    [Fact]
    public async Task RenameSession_UpdatesName()
    {
        var manager = CreateManager();
        var session = await manager.CreateSessionAsync("旧名");

        await manager.RenameSessionAsync(session.Id, "新名");

        Assert.Equal("新名", manager.GetSession(session.Id)!.Name);
    }

    [Fact]
    public void GetSessionHistory_UnknownId_Throws()
    {
        var manager = CreateManager();

        Assert.Throws<InvalidOperationException>(() => manager.GetSessionHistory("missing"));
    }

    [Fact]
    public async Task ClearSessionHistory_KeepsSystemPrompt()
    {
        var manager = CreateManager();
        var session = await manager.CreateSessionAsync("a", systemPrompt: "系统提示");

        manager.ClearSessionHistory(session.Id);

        var history = manager.GetSessionHistory(session.Id);
        Assert.Single(history);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, history[0].Role);
    }
}
