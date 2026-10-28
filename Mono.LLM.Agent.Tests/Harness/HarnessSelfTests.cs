using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 夹具自身的测试。测试基础设施必须比被测代码更可靠，否则故障会伪装成产品缺陷。
/// </summary>
public class HarnessSelfTests
{
    // ── ModelScript ──

    [Fact]
    public void ModelScript_MatchesRulesInDeclarationOrder()
    {
        var script = new ModelScript("a")
            .WhenContains("具体", "第一条")
            .WhenContains("模糊", "第二条")
            .Fallback("兜底");

        Assert.Equal("第一条", script.Respond("sys", "具体但模糊的话").Text);
        Assert.Equal("第二条", script.Respond("sys", "只是模糊").Text);
        Assert.Equal("兜底", script.Respond("sys", "无关内容").Text);
    }

    [Fact]
    public void ModelScript_RecordsEveryCall()
    {
        var script = new ModelScript("a").WhenContains("x", "y");

        script.Respond("系统", "含 x 的消息");
        script.Respond("系统", "另一条");

        Assert.Equal(2, script.CallCount);
        Assert.Equal("系统", script.Calls[0].SystemPrompt);
        Assert.Equal("含 x 的消息", script.Calls[0].UserMessage);
        Assert.Equal(0, script.Calls[0].MatchedRule);
        Assert.Equal(-1, script.Calls[1].MatchedRule);
    }

    [Fact]
    public void ModelScript_MatchingTextOverridesUserMessage()
    {
        var script = new ModelScript("a")
            .WhenContains("工具结果", "已拿到结果")
            .Fallback("需要调工具");

        // 用户消息没变，但上下文里出现了工具结果
        var reply = script.Respond("sys", "分析数据", "分析数据\n工具结果: 42");

        Assert.Equal("已拿到结果", reply.Text);
    }

    [Fact]
    public void ModelScript_CanReturnToolCalls()
    {
        var script = new ModelScript("a")
            .WhenContainsToolCall("查一下", "get_weather", new Dictionary<string, object?> { ["city"] = "杭州" });

        var reply = script.Respond("sys", "帮我查一下");

        Assert.NotNull(reply.ToolCalls);
        Assert.Equal("get_weather", reply.ToolCalls![0].Name);
        Assert.Equal("杭州", reply.ToolCalls[0].Arguments["city"]);
        Assert.Equal("get_weather", script.Calls[0].ToolCalls[0]);
    }

    [Fact]
    public void ModelScript_IsThreadSafeUnderParallelCalls()
    {
        var script = new ModelScript("a").Fallback("ok");

        Parallel.For(0, 200, i => script.Respond("sys", $"消息 {i}"));

        Assert.Equal(200, script.CallCount);
        Assert.Equal(200, script.Calls.Select(c => c.Index).Distinct().Count());
    }

    // ── AgentHarness ──

    [Fact]
    public async Task AgentHarness_RoutesCallsByModelId()
    {
        var harness = new AgentHarness();
        harness.Script("架构师").Fallback("架构师的回答");
        harness.Script("开发").Fallback("开发的回答");

        var factory = harness.CreateFactory();

        var architect = harness["架构师"];
        var developer = harness["开发"];

        using var clientA = factory.Create(architect.Configuration!);
        using var clientD = factory.Create(developer.Configuration!);

        Assert.Equal("架构师的回答", (await clientA.ChatAsync([Msg("hi")])).Content);
        Assert.Equal("开发的回答", (await clientD.ChatAsync([Msg("hi")])).Content);

        Assert.Equal(1, harness.ScriptOrNull("架构师")!.CallCount);
        Assert.Equal(1, harness.ScriptOrNull("开发")!.CallCount);
    }

    [Fact]
    public void AgentHarness_UndefinedAgentThrows()
    {
        var harness = new AgentHarness();

        Assert.Throws<KeyNotFoundException>(() => harness["不存在的"]);
    }

    [Fact]
    public void AgentHarness_ScriptAutoDefinesAgent()
    {
        var harness = new AgentHarness();

        harness.Script("自动创建");

        Assert.Single(harness.All);
        Assert.Equal("自动创建", harness["自动创建"].Name);
    }

    [Fact]
    public void AgentHarness_TotalCallsAggregatesAllScripts()
    {
        var harness = new AgentHarness();
        harness.Script("a").Fallback("1");
        harness.Script("b").Fallback("2");

        harness.ScriptOrNull("a")!.Respond("s", "m");
        harness.ScriptOrNull("b")!.Respond("s", "m");
        harness.ScriptOrNull("b")!.Respond("s", "m");

        Assert.Equal(3, harness.TotalCalls);
    }

    // ── FileSandbox ──

    [Fact]
    public void FileSandbox_IsolatesDifferentInstances()
    {
        using var first = new FileSandbox("self");
        using var second = new FileSandbox("self");

        first.Write("only-in-first.txt", "x");

        Assert.True(first.Exists("only-in-first.txt"));
        Assert.False(second.Exists("only-in-first.txt"));
        Assert.NotEqual(first.Root, second.Root);
    }

    [Fact]
    public void FileSandbox_WritesNestedFilesAndListsThem()
    {
        using var sandbox = new FileSandbox("self");

        sandbox.Write("a/b/c.txt", "hello");

        Assert.Equal("hello", sandbox.Read("a/b/c.txt"));
        Assert.Single(sandbox.ListFiles("*.txt", recursive: true));
    }

    [Fact]
    public void FileSandbox_RejectsPathEscape()
    {
        using var sandbox = new FileSandbox("self");

        Assert.Throws<ArgumentException>(() => sandbox.GetPath("../../escape.txt"));
    }

    [Fact]
    public void FileSandbox_RemovesDirectoryOnDispose()
    {
        string root;

        using (var sandbox = new FileSandbox("self"))
        {
            root = sandbox.Root;
            sandbox.Write("temp.txt", "x");
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FileSandbox_LabelIsSanitized()
    {
        using var sandbox = new FileSandbox("bad:name*with?chars");

        Assert.True(Directory.Exists(sandbox.Root));
        Assert.DoesNotContain(':', System.IO.Path.GetFileName(sandbox.Root));
    }

    // ── HttpSandbox ──

    [Fact]
    public async Task HttpSandbox_RecordsRequestsAndReturnsConfiguredBody()
    {
        var sandbox = new HttpSandbox().RespondWith("""{"ok":true}""");
        using var client = sandbox.CreateClient();

        var body = await client.GetStringAsync("/api/chat");

        Assert.Equal("""{"ok":true}""", body);
        var record = Assert.Single(sandbox.Requests);
        Assert.Equal("GET", record.Method);
        Assert.Equal(200, record.StatusCode);
        Assert.Contains("/api/chat", record.Uri);
    }

    [Fact]
    public async Task HttpSandbox_SequenceRespondsInOrder()
    {
        var sandbox = new HttpSandbox().RespondWithSequence("第一次", "第二次");
        using var client = sandbox.CreateClient();

        Assert.Equal("第一次", await client.GetStringAsync("/a"));
        Assert.Equal("第二次", await client.GetStringAsync("/b"));
        Assert.Equal("第二次", await client.GetStringAsync("/c"));
        Assert.Equal(3, sandbox.RequestCount);
    }

    [Fact]
    public async Task HttpSandbox_CapturesRequestBody()
    {
        var sandbox = new HttpSandbox();
        using var client = sandbox.CreateClient();

        using var content = new StringContent("""{"model":"x"}""");
        await client.PostAsync("/api/chat", content);

        Assert.Contains("model", sandbox.Requests[0].Body);
    }

    [Fact]
    public async Task HttpSandbox_CustomStatusIsPropagated()
    {
        var sandbox = new HttpSandbox().RespondWith("boom", HttpStatusCode.InternalServerError);
        using var client = sandbox.CreateClient();

        var response = await client.GetAsync("/fail");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(500, sandbox.Requests[0].StatusCode);
    }

    // ── AgentSandbox ──

    [Fact]
    public async Task AgentSandbox_TimesOutHungOperation()
    {
        using var sandbox = AgentSandbox.Create("self");
        sandbox.Timeout = TimeSpan.FromMilliseconds(60);

        await Assert.ThrowsAsync<TimeoutException>(() => sandbox.RunAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return 1;
        }));

        Assert.Contains(sandbox.Problems, p => p.Contains("未完成"));
    }

    [Fact]
    public async Task AgentSandbox_CompletesWithinTimeout()
    {
        using var sandbox = AgentSandbox.Create("self");
        sandbox.Timeout = TimeSpan.FromSeconds(5);

        var value = await sandbox.RunAsync(() => Task.FromResult(42));

        Assert.Equal(42, value);
        Assert.Empty(sandbox.Problems);
    }

    [Fact]
    public void AgentSandbox_DisposesServiceProviders()
    {
        var tracker = new DisposeTracker();

        using (var sandbox = AgentSandbox.Create("self"))
        {
            // 用工厂注册并实际解析，容器才会创建实例并接管所有权
            var provider = sandbox.Services(services => services.AddSingleton(_ => tracker));
            _ = provider.GetRequiredService<DisposeTracker>();

            Assert.False(tracker.Disposed);
        }

        Assert.True(tracker.Disposed);
    }

    [Fact]
    public void AgentSandbox_ClearCallsResetsScripts()
    {
        using var sandbox = AgentSandbox.Create("self");
        sandbox.Agents.Script("a").Fallback("x").Respond("s", "m");

        sandbox.ClearCalls();

        Assert.Equal(0, sandbox.Agents.TotalCalls);
    }

    private static Microsoft.Extensions.AI.ChatMessage Msg(string text) =>
        new(Microsoft.Extensions.AI.ChatRole.User, text);

    private sealed class DisposeTracker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
