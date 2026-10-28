using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.MultiAgent;
using Mono.LLM.Agent.Tests.Fakes;

namespace Mono.LLM.Agent.Tests.MultiAgent;

/// <summary>
/// 覆盖团队编排器的循环行为：规划、执行、评审、收敛与兜底。
/// </summary>
public class TeamOrchestratorTests
{
    private const string PlanMarker = "拆解成可以分头执行";
    private const string ExecuteMarker = "你负责的子任务";
    private const string ReviewMarker = "判断原始任务是否已经被完整解决";
    private const string FinalMarker = "整合成一份完整的最终答复";

    private static AgentDefinition Member(string name, string role = "成员") => new()
    {
        Name = name,
        Role = role,
        Description = "测试成员",
        SystemPrompt = $"你是{name}",
        Configuration = new LLMConfiguration
        {
            ProviderType = LLMProviderType.Ollama,
            ModelId = name,
            Endpoint = "http://localhost:11434"
        }
    };

    private static string SingleTaskPlan(string assignee, string description = "做点事") =>
        $"### TASK\nASSIGNEE: {assignee}\n{description}";

    // ── 主流程 ──

    [Fact]
    public async Task RunAsync_ReviewerConfirmsCompletion_StopsAfterOneRound()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return $"{agent} 的产出";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: 覆盖完整\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师", "设计数据模型");
            return "整合后的最终答复";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 3);

        var result = await orchestrator.RunAsync("做一个登录页");

        Assert.True(result.Success);
        Assert.Single(result.TeamRounds);
        Assert.True(result.TeamRounds[0].Completed);
        Assert.Equal("整合后的最终答复", result.FinalOutput);
    }

    [Fact]
    public async Task RunAsync_ReviewerRejectsThenAccepts_RunsTwoRounds()
    {
        var reviews = 0;

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出内容";
            if (user.Contains(ReviewMarker))
            {
                reviews++;
                return reviews == 1
                    ? "DONE: false\nREASON: 缺少错误处理\nNEXT: 补充异常分支"
                    : "DONE: true\nREASON: 已补齐\nNEXT:";
            }
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师", "实现表单");
            return "最终答复";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师") },
            maxRounds: 3);

        var result = await orchestrator.RunAsync("做一个登录页");

        Assert.Equal(2, result.TeamRounds.Count);
        Assert.False(result.TeamRounds[0].Completed);
        Assert.Equal("缺少错误处理", result.TeamRounds[0].Reason);
        Assert.Equal("补充异常分支", result.TeamRounds[0].NextFocus);
        Assert.True(result.TeamRounds[1].Completed);
    }

    [Fact]
    public async Task RunAsync_NeverCompletes_StopsAtMaxRounds()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: false\nREASON: 还不行\nNEXT: 继续";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师");
            return "当前最佳答复";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师") },
            maxRounds: 2);

        var result = await orchestrator.RunAsync("做一个系统");

        Assert.Equal(2, result.TeamRounds.Count);
        Assert.False(result.TeamRounds[^1].Completed);
        Assert.Equal("当前最佳答复", result.FinalOutput);
    }

    [Fact]
    public async Task RunAsync_SecondRoundPlanSeesReviewFeedback()
    {
        var planningPrompts = new List<string>();

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(PlanMarker))
            {
                planningPrompts.Add(user);
                return SingleTaskPlan("架构师");
            }
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: false\nREASON: 缺少压测\nNEXT: 补压测报告";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师") },
            maxRounds: 2);

        await orchestrator.RunAsync("做一个系统");

        Assert.Equal(2, planningPrompts.Count);
        Assert.DoesNotContain("缺少压测", planningPrompts[0]);
        Assert.Contains("缺少压测", planningPrompts[1]);
        Assert.Contains("补压测报告", planningPrompts[1]);
    }

    // ── 兜底 ──

    [Fact]
    public async Task RunAsync_UnparsablePlan_FallsBackToEveryMember()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return "抱歉，我无法拆解这个任务。";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发"), Member("测试") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做个东西");

        var round = Assert.Single(result.TeamRounds);
        Assert.Equal(3, round.Plan.Count);
        Assert.Equal(3, round.Outputs.Count);
    }

    [Fact]
    public async Task RunAsync_UnknownAssignee_FallsBackButKeepsTask()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return $"{agent} 干的";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("查无此人", "神秘任务");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        var round = Assert.Single(result.TeamRounds);
        Assert.Single(round.Plan);
        Assert.True(round.Outputs.ContainsKey("架构师"));
    }

    [Fact]
    public async Task RunAsync_AssigneeWithSuffix_MatchesByContainment()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "完成";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("开发（后端）", "写接口");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        Assert.True(result.TeamRounds[0].Outputs.ContainsKey("开发"));
    }

    // ── 多子任务 ──

    [Fact]
    public async Task RunAsync_MultipleSubTasks_AllExecuted()
    {
        var executed = new System.Collections.Concurrent.ConcurrentBag<string>();

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                executed.Add(agent);
                return $"{agent} 的产出";
            }
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker))
                return "### TASK\nASSIGNEE: 架构师\n设计\n\n### TASK\nASSIGNEE: 开发\n实现";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        Assert.Equal(2, executed.Count);
        Assert.Contains("架构师", executed);
        Assert.Contains("开发", executed);
        Assert.Equal(2, result.TeamRounds[0].Outputs.Count);
    }

    [Fact]
    public async Task RunAsync_SameMemberTwice_AppendsOutputs()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
                return user.Contains("第一件事") ? "第一件结果" : "第二件结果";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker))
                return "### TASK\nASSIGNEE: 架构师\n第一件事\n\n### TASK\nASSIGNEE: 架构师\n第二件事";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        var output = result.TeamRounds[0].Outputs["架构师"];
        Assert.Contains("第一件结果", output);
        Assert.Contains("第二件结果", output);
    }

    // ── 结果结构 ──

    [Fact]
    public async Task RunAsync_RecordsPlanAndExecuteMessages()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师", "设计模型");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(factory, new[] { Member("架构师") }, maxRounds: 1);
        var result = await orchestrator.RunAsync("做点事");

        Assert.Contains(result.Messages, m => m.Metadata.GetValueOrDefault("stage") == "plan");
        Assert.Contains(result.Messages, m => m.Metadata.GetValueOrDefault("stage") == "execute");
        Assert.Contains(result.AgentOutputs.Keys, k => k.EndsWith(".plan"));
    }

    [Fact]
    public async Task RunAsync_AccumulatesTokensAcrossPhases()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(factory, new[] { Member("架构师") }, maxRounds: 1);
        var result = await orchestrator.RunAsync("做点事");

        // 每次调用 30 token（plan + execute + review + final）
        Assert.Equal(120, result.TotalTokens);
    }

    // ── 参数校验 ──

    [Fact]
    public async Task RunAsync_NoMembers_ReturnsFailureResult()
    {
        var factory = new ScriptedFactory((_, _) => "x");
        var orchestrator = new TeamOrchestrator(factory, Array.Empty<AgentDefinition>());

        var result = await orchestrator.RunAsync("做点事");

        Assert.False(result.Success);
        Assert.Contains("No team members", result.Error);
    }

    [Fact]
    public async Task RunAsync_EmptyTask_ReturnsFailureResult()
    {
        var factory = new ScriptedFactory((_, _) => "x");
        var orchestrator = new TeamOrchestrator(factory, new[] { Member("架构师") });

        var result = await orchestrator.RunAsync("   ");

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public async Task RunAsync_AgentWithoutConfiguration_ReturnsFailureResult()
    {
        var factory = new ScriptedFactory((_, _) => "x");
        var broken = new AgentDefinition { Name = "无配置", SystemPrompt = "x" };

        var orchestrator = new TeamOrchestrator(factory, new[] { broken });
        var result = await orchestrator.RunAsync("做点事");

        Assert.False(result.Success);
        Assert.Contains("LLMConfiguration", result.Error);
    }

    // ── 经由 AgentTeam 接入 ──

    [Fact]
    public async Task AgentTeam_UseTeam_DelegatesToTeamOrchestrator()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker)) return "产出";
            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return SingleTaskPlan("架构师");
            return "最终答复";
        });

        var team = new AgentTeam(factory)
            .AddMembers(new[] { Member("架构师"), Member("开发") })
            .WithLeader(Member("架构师"))
            .UseTeam(maxRounds: 2);

        Assert.Equal(TeamMode.Team, team.Mode);

        var result = await team.RunAsync("做一个登录页");

        Assert.True(result.Success);
        Assert.NotEmpty(result.TeamRounds);
        Assert.Equal("最终答复", result.FinalOutput);
    }

    [Fact]
    public async Task AgentTeam_OtherModes_LeaveTeamRoundsEmpty()
    {
        var factory = new ScriptedFactory((_, _) => "普通回答");

        var team = new AgentTeam(factory)
            .AddMembers(new[] { Member("架构师") })
            .UseSequential();

        var result = await team.RunAsync("随便问问");

        Assert.Empty(result.TeamRounds);
    }
}
