using Mono.LLM.Agent.MultiAgent;
using Mono.LLM.Agent.Tests.Harness;

namespace Mono.LLM.Agent.Tests.Scenarios;

/// <summary>
/// 端到端场景：团队模式的多轮收敛、并行执行与轮次上限。
/// </summary>
public class TeamCollaborationTests : SandboxedTest
{
    [Fact]
    public async Task Team_ConvergesInTwoRounds_WhenReviewerRejectsThenAccepts()
    {
        var coordinator = Sandbox.Agents.Define("协调", "拆解与整合", "你是协调者。");
        var reviewer = Sandbox.Agents.Define("评审", "验收把关", "你是评审者。");
        var researcher = Sandbox.Agents.Define("调研", "收集资料", "你是调研员。");
        var writer = Sandbox.Agents.Define("撰写", "成稿", "你是撰稿人。", "擅长结构化写作");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "技术选型报告：方案 A。")
            .WhenContains("拆解", """
                ### TASK
                ASSIGNEE: 调研
                整理候选方案

                ### TASK
                ASSIGNEE: 撰写
                起草报告
                """);

        Sandbox.Agents.Script("评审").Sequence(
            ScriptedReply.Say("DONE: false\nREASON: 缺少成本对比\nNEXT: 补充成本维度"),
            ScriptedReply.Say("DONE: true\nREASON: 已覆盖全部要点\nNEXT:"));

        Sandbox.Agents.Script("调研").Fallback("候选方案：A / B / C");
        Sandbox.Agents.Script("撰写").Fallback("报告初稿");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([researcher, writer])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 2);

        var result = await Sandbox.RunAsync(() => team.RunAsync("出一份技术选型报告"));

        Assert.True(result.Success);
        Assert.Equal(2, result.TeamRounds.Count);
        Assert.False(result.TeamRounds[0].Completed);
        Assert.Equal("缺少成本对比", result.TeamRounds[0].Reason);
        Assert.True(result.TeamRounds[1].Completed);
        Assert.Equal("技术选型报告：方案 A。", result.FinalOutput);
    }

    [Fact]
    public async Task Team_EachMemberRunsOncePerRound()
    {
        var coordinator = Sandbox.Agents.Define("协调");
        var reviewer = Sandbox.Agents.Define("评审");
        var first = Sandbox.Agents.Define("甲");
        var second = Sandbox.Agents.Define("乙");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "汇总结果")
            .WhenContains("拆解", "### TASK\nASSIGNEE: 甲\n任务一\n\n### TASK\nASSIGNEE: 乙\n任务二");

        Sandbox.Agents.Script("评审").Sequence(
            ScriptedReply.Say("DONE: false\nREASON: 需要补充\nNEXT: 再细化"),
            ScriptedReply.Say("DONE: true\nREASON: ok\nNEXT:"));

        Sandbox.Agents.Script("甲").Fallback("甲的产出");
        Sandbox.Agents.Script("乙").Fallback("乙的产出");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([first, second])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 2);

        await Sandbox.RunAsync(() => team.RunAsync("做个方案"));

        Assert.Equal(2, Sandbox.Agents.ScriptOrNull("甲")!.CallCount);
        Assert.Equal(2, Sandbox.Agents.ScriptOrNull("乙")!.CallCount);

        // 协调者：两轮规划 + 一次最终整合
        Assert.Equal(3, Sandbox.Agents.ScriptOrNull("协调")!.CallCount);
        Assert.Equal(2, Sandbox.Agents.ScriptOrNull("评审")!.CallCount);
    }

    [Fact]
    public async Task Team_ParallelRound_DoesNotLeakSiblingOutputs()
    {
        var coordinator = Sandbox.Agents.Define("协调");
        var reviewer = Sandbox.Agents.Define("评审");
        var first = Sandbox.Agents.Define("甲");
        var second = Sandbox.Agents.Define("乙");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "汇总")
            .WhenContains("拆解", "### TASK\nASSIGNEE: 甲\n任务一\n\n### TASK\nASSIGNEE: 乙\n任务二");

        Sandbox.Agents.Script("评审").Fallback("DONE: true\nREASON: ok\nNEXT:");

        // 给两个成员相同的模拟耗时，确保并行时确实重叠；
        // 不设延迟的话，线程池可能恰好串行调度，断言会随负载随机失效。
        var delay = TimeSpan.FromMilliseconds(80);
        Sandbox.Agents.Script("甲").Fallback(ScriptedReply.Say("甲的独有产出", delay));
        Sandbox.Agents.Script("乙").Fallback(ScriptedReply.Say("乙的独有产出", delay));

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([first, second])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 1, executionMode: TeamExecutionMode.Parallel);

        var result = await Sandbox.RunAsync(() => team.RunAsync("做点事"));

        var round = Assert.Single(result.TeamRounds);
        Assert.Equal(2, round.Outputs.Count);
        Assert.Equal("甲的独有产出", round.Outputs["甲"]);
        Assert.Equal("乙的独有产出", round.Outputs["乙"]);

        // 并行时同轮成员启动于同一时刻，彼此看不到对方产出
        Assert.DoesNotContain(
            Sandbox.Agents.ScriptOrNull("乙")!.UserMessages,
            message => message.Contains("甲的独有产出"));
    }

    [Fact]
    public async Task Team_SequentialRound_LaterMemberSeesEarlierOutput()
    {
        var coordinator = Sandbox.Agents.Define("协调");
        var reviewer = Sandbox.Agents.Define("评审");
        var first = Sandbox.Agents.Define("甲");
        var second = Sandbox.Agents.Define("乙");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "汇总")
            .WhenContains("拆解", "### TASK\nASSIGNEE: 甲\n任务一\n\n### TASK\nASSIGNEE: 乙\n任务二");

        Sandbox.Agents.Script("评审").Fallback("DONE: true\nREASON: ok\nNEXT:");
        Sandbox.Agents.Script("甲").Fallback("甲的独有产出");
        Sandbox.Agents.Script("乙").Fallback("乙的独有产出");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([first, second])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 1, executionMode: TeamExecutionMode.Sequential);

        await Sandbox.RunAsync(() => team.RunAsync("做点事"));

        Assert.Contains(
            Sandbox.Agents.ScriptOrNull("乙")!.UserMessages,
            message => message.Contains("甲的独有产出"));
    }

    [Fact]
    public async Task Team_StopsAtMaxRounds_WhenReviewerNeverAccepts()
    {
        var coordinator = Sandbox.Agents.Define("协调");
        var reviewer = Sandbox.Agents.Define("评审");
        var member = Sandbox.Agents.Define("执行");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "当前最佳结果")
            .WhenContains("拆解", "### TASK\nASSIGNEE: 执行\n继续完善");

        Sandbox.Agents.Script("评审").Fallback("DONE: false\nREASON: 还差一点\nNEXT: 继续");
        Sandbox.Agents.Script("执行").Fallback("又一次尝试");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([member])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 2);

        var result = await Sandbox.RunAsync(() => team.RunAsync("做个复杂系统"));

        Assert.Equal(2, result.TeamRounds.Count);
        Assert.False(result.TeamRounds[^1].Completed);
        Assert.Equal("当前最佳结果", result.FinalOutput);
    }

    [Fact]
    public async Task Team_SecondRoundPlanningSeesReviewFeedback()
    {
        var coordinator = Sandbox.Agents.Define("协调");
        var reviewer = Sandbox.Agents.Define("评审");
        var member = Sandbox.Agents.Define("执行");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "最终")
            .WhenContains("拆解", "### TASK\nASSIGNEE: 执行\n做事");

        Sandbox.Agents.Script("评审").Sequence(
            ScriptedReply.Say("DONE: false\nREASON: 缺少压测数据\nNEXT: 补压测报告"),
            ScriptedReply.Say("DONE: true\nREASON: ok\nNEXT:"));

        Sandbox.Agents.Script("执行").Fallback("产出");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([member])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 2);

        await Sandbox.RunAsync(() => team.RunAsync("做个系统"));

        var planningCalls = Sandbox.Agents.ScriptOrNull("协调")!
            .UserMessages
            .Where(m => m.Contains("拆解"))
            .ToList();

        Assert.Equal(2, planningCalls.Count);
        Assert.DoesNotContain("缺少压测数据", planningCalls[0]);
        Assert.Contains("缺少压测数据", planningCalls[1]);
    }
}
